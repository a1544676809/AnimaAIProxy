using System.Text;

namespace AnimaAIProxy.Core;

/// <summary>
/// 自定义请求头：把用户填的一行行文本解析成真正能发出去的 HTTP 头。
///
/// 从 AnimaCaptioner 的 <c>Core/CustomHeaders.cs</c> 原样移植。有些平台/网关要求
/// 专属请求头才肯服务（例如 OpenCode 的 <c>zen/go/v1</c> 在没有
/// <c>x-opencode-session</c> 时直接回「MissingSessionID」），只填 base url 是连不上的。
///
/// **为什么要有解析器，而不是直接 TryAddWithoutValidation**：实测
/// <c>HttpRequestMessage.Headers.TryAddWithoutValidation</c> 有两个静默失败：
///
/// 1. **头名非法时返回 <c>false</c> 而不抛**——空名、带空格、带冒号的名字会被直接丢掉。
///    <c>X-Bad Name</c> 悄悄消失，请求照发，服务端回 401，而界面上什么都没提示。
///    用户看到的是"填了却没用"，无从查起。
/// 2. **实体头（<c>Content-Type</c> 等）加到 <c>Headers</c> 上会被静默忽略**——
///    实测请求仍然带着原来的 <c>application/json</c> 出去。只有
///    <c>req.Content.Headers</c> 才改得动。
///
/// 所以这里**自己校验、自己分流**，并把不能用的行原样报出来让界面显示。
/// 宁可让用户看见"这一行有问题"，也不要让它无声无息地不生效。
/// </summary>
public static class CustomHeaders
{
    /// <summary>一行解析结果。</summary>
    /// <param name="Name">头名（原样保留用户写的形式）。</param>
    /// <param name="Value">头值（已去掉首尾空白）。</param>
    /// <param name="IsContentHeader">是否实体头（Content-Type 之类），要加到 Content.Headers。</param>
    public readonly record struct Entry(string Name, string Value, bool IsContentHeader);

    /// <summary>解析结果：能用的 + 被拒的（连同原因）。</summary>
    public sealed record Result(
        IReadOnlyList<Entry> Entries,
        IReadOnlyList<string> Rejected)
    {
        public bool HasRejected => Rejected.Count > 0;
    }

    /// <summary>
    /// 属于**实体头**的名字。这些加到 <c>req.Headers</c> 上不会生效，必须走
    /// <c>req.Content.Headers</c>——实测确认过，而且不抛异常，只是没效果。
    /// 只列真正可能被填的这几个：其余实体头（Content-Length 等）由传输层算，用户填了也不该认。
    /// </summary>
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Location",
        "Content-MD5", "Content-Disposition", "Expires", "Last-Modified",
        "Allow", "Content-Range"
    };

    /// <summary>
    /// 我们自己已经会设置的头。用户再填一遍会**合并成一个逗号连接的值**
    /// （实测：`Authorization: Bearer first` + `authorization: Bearer second`
    /// 变成 `Bearer first, Bearer second`），服务端只会当成一个坏 token。
    /// 与其产生这种结果，不如明确拒绝并让用户删掉 API Key 里那份。
    /// </summary>
    private static readonly HashSet<string> ManagedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Content-Length"
    };

    /// <summary>
    /// 解析多行文本。格式每行 <c>Name: Value</c>，也接受 <c>Name=Value</c>
    /// （很多人会顺手写等号）。空行与 <c>#</c> 开头的注释行忽略。
    /// </summary>
    public static Result Parse(string? text)
    {
        var entries = new List<Entry>();
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new Result(entries, rejected);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // 分隔符取**最先出现**的那一个（冒号或等号）。
            //
            // 这里比 AnimaCaptioner 的原版**改了一条**：原版是"冒号优先"，于是
            // `HTTP-Referer = https://example.com` 会切在 `https:` 的冒号上——
            // 头名变成 `HTTP-Referer = https`（含空格，判为非法而被拒），
            // 用户看到的只是"这一行有问题"，而原因完全看不出来。
            // 取最早的那个分隔符两种写法都对：`X: a=b` 切在冒号（等号在值里），
            // `X = a:b` 切在等号（冒号在值里）。
            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            var idx = colon < 0 ? equals
                    : equals < 0 ? colon
                    : Math.Min(colon, equals);
            if (idx <= 0)
            {
                rejected.Add($"{line}（缺少「名字: 值」的分隔符）");
                continue;
            }

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            if (name.Length == 0)
            {
                rejected.Add($"{line}（头名是空的）");
                continue;
            }
            if (!IsValidName(name))
            {
                rejected.Add($"{line}（头名含非法字符——只允许字母、数字和 - _ . ，不能有空格）");
                continue;
            }
            if (value.Length == 0)
            {
                rejected.Add($"{line}（值是空的——要删除这一行，而不是留空）");
                continue;
            }
            if (ManagedNames.Contains(name))
            {
                rejected.Add($"{name}（这个头由程序自己设置：「API Key」框填的那份就是它，" +
                             "这里再填一次会拼成两个值，服务端只会当成坏密钥）");
                continue;
            }
            if (!seen.Add(name))
            {
                rejected.Add($"{name}（重复了——同一个头只能填一次，否则会拼成一个值）");
                continue;
            }
            // 值里带换行会撕开请求（头注入）。解析是按行来的，理论上进不来，
            // 但用户可能从别处粘贴带 \r 的内容，所以还是挡一道。
            if (value.Contains('\n') || value.Contains('\r'))
            {
                rejected.Add($"{name}（值里有换行符）");
                continue;
            }

            entries.Add(new Entry(name, value, ContentHeaderNames.Contains(name)));
        }

        return new Result(entries, rejected);
    }

    /// <summary>
    /// 头名是否合法。按 RFC 7230 的 token 定义：字母、数字和
    /// <c>! # $ % &amp; ' * + - . ^ _ ` | ~</c>。实践中只需要前面那批，
    /// 这里放全是为了不误拒合法名字。
    /// </summary>
    private static bool IsValidName(string name)
    {
        foreach (var ch in name)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                     (ch >= '0' && ch <= '9') ||
                     ch is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or
                            '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// 把解析结果应用到一个请求上。实体头走 Content，其余走 Headers——
    /// 分流在这里做一次，调用点就不必各自记得这件事（记不住的，上面那两个静默失败
    /// 就是证据）。
    ///
    /// 已有同名头时**覆盖**而不是追加：程序自己设的那些（如 Authorization）本就
    /// 被 <see cref="Parse"/> 挡掉了，能走到这里的都是用户明确要的头。
    /// </summary>
    public static void Apply(System.Net.Http.HttpRequestMessage req, Result result)
    {
        foreach (var e in result.Entries)
        {
            if (e.IsContentHeader)
            {
                // 内容还没设时先给一个空的 StringContent，否则 Content.Headers 不存在。
                // 实际调用点总是先设了 Content 才 Apply，这里只是兜底。
                req.Content ??= new System.Net.Http.StringContent("");
                req.Content.Headers.Remove(e.Name);
                req.Content.Headers.TryAddWithoutValidation(e.Name, e.Value);
            }
            else
            {
                req.Headers.Remove(e.Name);
                req.Headers.TryAddWithoutValidation(e.Name, e.Value);
            }
        }
    }

    /// <summary>规范化成存盘形式（每行 <c>Name: Value</c>），用于把解析结果写回输入框。</summary>
    public static string ToText(IEnumerable<Entry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(e.Name).Append(": ").Append(e.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 界面上那条提示行用的一句话说清问题；没问题时返回空串。
    /// 与 AnimaCaptioner 的 <c>LlmClient.DescribeCustomHeaderProblems</c> 同一个用途：
    /// 用户填错却毫无反馈，是这类功能最常见的失败方式。
    /// </summary>
    public static string DescribeProblems(string? text)
    {
        var r = Parse(text);
        return r.HasRejected ? string.Join("；", r.Rejected) : "";
    }
}
