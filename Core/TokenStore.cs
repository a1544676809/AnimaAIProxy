using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaAIProxy.Core;

/// <summary>
/// 一枚代理令牌。
///
/// 明文只在"生成的那一刻"存在：库里存的是 <see cref="Hash"/>，
/// 校验时比对哈希。这样即使 proxy.tokens.json 被读走，也拿不到能用的令牌。
/// （上游密钥不同，它必须可还原，所以走 DPAPI 而不是哈希。）
/// </summary>
public sealed class ProxyToken
{
    /// <summary>短标识，用于日志与界面（不可用于鉴权）。</summary>
    public string Id { get; set; } = "";

    /// <summary>给人看的名字，例如"Claude Desktop"、"本机测试"。</summary>
    public string Name { get; set; } = "";

    /// <summary>令牌明文的前 8 位，只用于在界面上辨认是哪一枚。</summary>
    public string Prefix { get; set; } = "";

    /// <summary>SHA-256(令牌明文) 的 base64。校验时比对它。</summary>
    public string Hash { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>累计使用次数，界面上用来判断"这枚还在用吗"。</summary>
    public long UseCount { get; set; }

    /// <summary>备注：这枚令牌给哪个平台用的。</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// 令牌库。存 %LocalAppData%\AnimaAIProxy\proxy.tokens.json。
///
/// 校验用固定时间比较（<see cref="CryptographicOperations.FixedTimeEquals"/>）：
/// 逐字节短路比较会让攻击者按响应时间一个字节一个字节地试出哈希。
/// 回环地址上这个风险很小，但代价只有一行代码。
/// </summary>
public sealed class TokenStore
{
    private static readonly object Gate = new();

    /// <summary>
    /// 令牌库位置。默认 <c>%LocalAppData%\AnimaAIProxy\proxy.tokens.json</c>。
    ///
    /// **可覆盖**：离线回归要真的起一次 HTTP 服务、真的发一枚令牌、真的走一遍
    /// 鉴权，而那会调 <see cref="Save"/>。不把它指到临时目录的话，跑一次回归
    /// 就会把用户**已经发给各个平台的真令牌**全部作废——这是那种"跑测试把
    /// 生产配置弄坏了"的事故。
    /// </summary>
    public static string? FilePathOverride { get; set; }

    public static string FilePath =>
        FilePathOverride ?? Path.Combine(ProxyConfig.Dir, "proxy.tokens.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<ProxyToken> Tokens { get; set; } = new();

    /// <summary>令牌前缀，让用户一眼看出这是代理签发的而不是上游的。</summary>
    public const string Prefix = "aip-";

    // ---------------- 生成 ----------------

    /// <summary>
    /// 造一枚新令牌并入库。<paramref name="plaintext"/> 只在这一次返回，
    /// 之后再无可能取回——这是刻意的，库泄露也换不来能用的令牌。
    /// </summary>
    public static ProxyToken Create(string name, string note, out string plaintext)
    {
        // 32 字节随机 → base64url 去填充，43 个字符。够长，且能直接放进
        // Authorization 头、URL、配置文件里而不用转义。
        var raw = RandomNumberGenerator.GetBytes(32);
        plaintext = Prefix + Base64Url(raw);

        var token = new ProxyToken
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? "未命名" : name.Trim(),
            Note = (note ?? "").Trim(),
            Prefix = plaintext[..Math.Min(12, plaintext.Length)],
            Hash = Hash(plaintext),
            Enabled = true,
            CreatedAt = DateTimeOffset.Now
        };
        return token;
    }

    // ---------------- 校验 ----------------

    /// <summary>
    /// 校验一个令牌明文。命中则返回记录（并记一次使用），否则 null。
    ///
    /// 停用的令牌**一律不通过**，且不区分"不存在"与"已停用"——
    /// 对调用方来说两者都是"这枚令牌没用"，区分只会多给出一条探测信息。
    /// </summary>
    public ProxyToken? Authenticate(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;

        var candidate = Hash(plaintext);
        ProxyToken? hit = null;

        lock (Gate)
        {
            foreach (var t in Tokens)
            {
                if (!t.Enabled || t.Hash.Length == 0) continue;
                if (!FixedEquals(t.Hash, candidate)) continue;
                hit = t;
                break;
            }

            if (hit is not null)
            {
                hit.LastUsedAt = DateTimeOffset.Now;
                hit.UseCount++;
            }
        }

        if (hit is not null) Save();
        return hit;
    }

    /// <summary>有没有一枚启用中的令牌。用来区分"没带令牌"和"根本没配令牌"两种拒绝。</summary>
    public bool HasEnabled => Tokens.Any(t => t.Enabled && t.Hash.Length > 0);

    public int EnabledCount => Tokens.Count(t => t.Enabled && t.Hash.Length > 0);

    private static string Hash(string plaintext) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>固定时间比较。长度不同直接判否（长度本身不是秘密）。</summary>
    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------------- 读写 ----------------

    public static TokenStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var store = JsonSerializer.Deserialize<TokenStore>(File.ReadAllText(FilePath), Options);
                if (store is not null)
                {
                    store.Tokens ??= new List<ProxyToken>();
                    return store;
                }
            }
        }
        catch (Exception ex)
        {
            ProxyLog.Write("令牌库解析失败：" + ex.Message + "（改用空库；文件未被覆盖）");
            // 注意这里**不 Save()**：把坏文件覆盖掉会让用户连"我发过几枚令牌"
            // 都查不到，而下次启动我们还是解析失败——问题被永久掩埋。
            return new TokenStore();
        }
        return new TokenStore();
    }

    public void Save()
    {
        try
        {
            // 目录从**实际要写的那个路径**推出来，而不是固定用 ProxyConfig.Dir：
            // FilePath 可以被覆盖（见 FilePathOverride），写 temp 的路径却去建
            // 真实配置目录是两件不相干的事。
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (Gate)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            ProxyLog.Write("令牌库保存失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 从 Authorization 头里取出令牌明文。认三种写法：
    /// <c>Bearer &lt;token&gt;</c>（OpenAI 标准）、裸令牌、以及
    /// <c>x-api-key</c>（Anthropic 风格，不少平台会这么发）。
    /// </summary>
    public static string? ExtractToken(string? authorization, string? xApiKey = null)
    {
        if (!string.IsNullOrWhiteSpace(xApiKey)) return xApiKey.Trim();
        if (string.IsNullOrWhiteSpace(authorization)) return null;

        var v = authorization.Trim();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return v[7..].Trim();
        return v;
    }
}
