using System.Net;
using System.Net.Sockets;

namespace AnimaAIProxy.Core;

/// <summary>
/// IP 名单：单地址与网段（CIDR），IPv4 / IPv6 都支持。
///
/// 两个用法对应两条语义，**都由调用方决定**：
///   * 黑名单：命中就拒绝，其余的放行（"别让这几台进来"）；
///   * 白名单：命中才放行，其余一律拒绝（"只让这几台进来"）。
///
/// 几条刻意写死的规矩：
///
/// 1. **IPv4-mapped IPv6 先归一成 IPv4。** 双栈监听时远端地址常常是
///    <c>::ffff:192.168.1.5</c>；不归一的话，名单里写的 <c>192.168.1.0/24</c>
///    永远匹配不上，而症状是"名单明明写对了却不生效"。
/// 2. **CIDR 之外的写法一律当错。** 不做 <c>192.168.1.</c> 这种前缀通配，
///    也不做通配符——一个"看起来像网段、实际按字符串处理"的实现只会在
///    边界情况下悄悄放过或拦错，而这正是最不该出错的地方。
/// 3. **解析失败不抛异常，而是把原文收进 errors。** 界面要能逐行告诉用户
///    哪一条写错了；请求路径上则按"这一条不存在"处理（拦不住总比整个代理
///    起不来强，但界面必须说出来）。
/// 4. **主机位清零。** <c>10.0.0.5/8</c> 按 <c>10.0.0.0/8</c> 处理，只记一条。
/// </summary>
public sealed class IpAccessList
{
    private readonly List<(IPAddress Network, int Prefix)> _ranges = new();

    public int Count => _ranges.Count;

    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>
    /// 解析一批条目。空行与 <c>#</c> 开头的注释行直接跳过；
    /// 解析不了的收进 <paramref name="errors"/>（原文，便于界面逐条显示）。
    /// </summary>
    public static IpAccessList Parse(IEnumerable<string>? entries, out List<string> errors)
    {
        var list = new IpAccessList();
        errors = new List<string>();

        foreach (var raw in entries ?? Enumerable.Empty<string>())
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (TryParseEntry(line, out var network, out var prefix))
                list._ranges.Add((network, prefix));
            else
                errors.Add(line);
        }
        return list;
    }

    /// <summary>解析单条：<c>1.2.3.4</c>、<c>10.0.0.0/8</c>、<c>::1</c>、<c>fd00::/8</c>。</summary>
    public static bool TryParseEntry(string text, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;

        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        var slash = s.IndexOf('/');
        var addrPart = slash < 0 ? s : s[..slash];
        var prefixPart = slash < 0 ? "" : s[(slash + 1)..].Trim();

        if (!IPAddress.TryParse(addrPart.Trim(), out var ip)) return false;
        ip = Normalize(ip);

        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixPart.Length == 0)
            prefix = max;
        else if (!int.TryParse(prefixPart, out prefix) || prefix < 0 || prefix > max)
            return false;

        network = Mask(ip, prefix);
        return true;
    }

    /// <summary>这个地址是否命中名单里的任意一条。</summary>
    public bool Contains(IPAddress? ip)
    {
        if (ip is null) return false;
        ip = Normalize(ip);

        foreach (var (network, prefix) in _ranges)
        {
            if (network.AddressFamily != ip.AddressFamily) continue;
            if (InRange(ip, network, prefix)) return true;
        }
        return false;
    }

    /// <summary>IPv4-mapped IPv6 → IPv4（见类注释第 1 条）。</summary>
    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static IPAddress Mask(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        var total = bytes.Length * 8;
        if (prefix >= total) return ip;

        for (var i = prefix; i < total; i++)
            bytes[i / 8] &= (byte)~(1 << (7 - (i % 8)));

        return new IPAddress(bytes);
    }

    private static bool InRange(IPAddress ip, IPAddress network, int prefix)
    {
        var a = ip.GetAddressBytes();
        var b = network.GetAddressBytes();
        if (a.Length != b.Length) return false;

        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (a[i] != b[i]) return false;

        var rest = prefix % 8;
        if (rest == 0) return true;

        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
