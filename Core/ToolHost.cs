using AnimaCaptioner.Core;
using System.Text;
using System.Text.Json;

namespace AnimaAIProxy.Core;

/// <summary>
/// 工具的执行体：把模型发来的 tool_call 参数变成一段回灌给它的文本。
///
/// 与 <see cref="ToolCatalog"/> 的分工：那边管"怎么声明"，这边管"怎么执行"。
/// 执行结果**一律是给模型读的自然语言**，不是 JSON：实测把结构化数据塞回
/// 工具结果会让小模型开始讨论字段名，而它真正需要做的只是"从这一行里抄出
/// tag= 后面那一段"。这也正是 AnimaCaptioner 那边踩出来的结论。
///
/// **所有执行都不抛异常。** 一次工具执行失败只应该让模型收到"这次查不了，
/// 原因是……"，而不是让整个代理请求 500——上游那边会把这个 500 当成
/// "接口坏了"，而实际上只是词库没配好。
/// </summary>
public sealed class ToolHost
{
    private readonly VocabService _vocab;

    public ToolHost(VocabService vocab) => _vocab = vocab;

    /// <summary>本次请求里我们已经执行过的工具调用数，用来卡上限。</summary>
    public int ExecutedCount { get; private set; }

    public void ResetCounter() => ExecutedCount = 0;

    /// <summary>
    /// 执行一次调用。返回回灌给模型的文本；<paramref name="summary"/> 是一句
    /// 给人看的一行摘要，供日志与界面显示。
    /// </summary>
    public string Execute(ProxyConfig cfg, ToolKind kind, string arguments, out string summary)
    {
        ExecutedCount++;
        try
        {
            return kind switch
            {
                ToolKind.Search => Search(cfg, arguments, out summary),
                ToolKind.Resolve => Resolve(cfg, arguments, out summary),
                ToolKind.Scan => Scan(cfg, arguments, out summary),
                _ => Fail("未知工具。", out summary)
            };
        }
        catch (Exception ex)
        {
            // 词库那边理论上不抛，但 113 MB 的数据 + 任意用户输入，
            // 兜底一层比让上游收到 500 强。
            ProxyLog.Write($"工具执行异常（{kind}）：{ex}");
            return Fail("工具执行失败：" + ex.Message, out summary);
        }
    }

    private static string Fail(string text, out string summary)
    {
        summary = text;
        return text;
    }

    /// <summary>
    /// 取出并解析词库。不可用时返回 null，并且**把原因交给调用方**——
    /// 让模型知道"词库没配好"比让它以为"这个词库真没有这个标签"重要得多：
    /// 后者会让它转而凭记忆编一个标签名出来。
    /// </summary>
    private VocabDb? TryVocab(ProxyConfig cfg, out string error)
    {
        var db = _vocab.Get(cfg);
        if (db is null)
        {
            error = "The tag database is not available right now, so this lookup could not " +
                    "be performed. Do NOT guess tag names from memory to fill the gap - tell " +
                    "the user that the database is unavailable. Reason: " + _vocab.Status();
            return null;
        }
        error = "";
        return db;
    }

    // ---------------- danbooru_search_tags ----------------

    private string Search(ProxyConfig cfg, string arguments, out string summary)
    {
        var (query, limit) = ReadQueryAndLimit(arguments);
        if (query.Length == 0)
        {
            summary = "查（空查询）";
            return "The query was empty. Call this tool again with one or two words, " +
                   "for example \"white dress\" or \"蕾丝\".";
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err, out summary);

        var res = TagSearch.QueryDetailed(db, query, Math.Clamp(limit, 1, TagSearch.ToolLimit));

        // remaining 传 0：预算这件事由**我们**的循环掌握，而模型看到的这一轮
        // 之外还有没有下一轮，取决于它自己有没有别的工具要调。给它一个具体的
        // "还剩几次"是编的，不如统一说 0（"这是本轮的最后信息"），
        // 系统提示里另外交代它"需要更多就再调一次"。
        var text = TagSearch.ToToolContent(res.Hits, query, 0, res.SplitMatch);
        summary = res.Hits.Count > 0
            ? $"查「{query}」→ {res.Hits.Count} 条"
            : $"查「{query}」→ 无结果";
        return text;
    }

    // ---------------- danbooru_resolve_tag ----------------

    private string Resolve(ProxyConfig cfg, string arguments, out string summary)
    {
        var tag = ReadString(arguments, "tag");
        if (tag.Length == 0)
        {
            summary = "核对（空输入）";
            return "The tag was empty. Call this tool again with one tag name or one " +
                   "Chinese meaning.";
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err, out summary);

        // 先当英文标签解析，不成立再当中文释义解析。这条顺序与
        // AnimaCaptioner 的 SuspiciousMentions 判据一致——两处不一致的话，
        // 代理说"这个词库认得"、而打标软件说"查不到"，用户会不知道信谁。
        var en = db.ResolveEnglish(tag);
        var cn = en.Tag is null ? db.ResolveChinese(tag) : new ResolveResult(null, "");

        var sb = new StringBuilder();
        sb.Append("input: ").Append(tag).Append('\n');

        var resolved = en.Tag ?? cn.Tag;
        if (resolved is null)
        {
            sb.Append("result: NOT FOUND in the tag database.\n");
            sb.Append("Either the spelling is wrong or this concept has no tag. Do not use ").
              Append("it as a tag; say it in prose instead, or call ").
              Append(ToolCatalog.SearchName).Append(" with shorter words to look for a ").
              Append("real one.\n");
            summary = $"核对「{tag}」→ 查不到";
            return sb.ToString();
        }

        var status = db.Status(resolved);
        var category = db.CategoryOf(resolved);
        var cnText = db.LookupCn(resolved);
        var section = TagSections.Classify(resolved, db);

        sb.Append("result: FOUND\n");
        sb.Append("tag=").Append(resolved).Append('\n');
        sb.Append("cn=").Append(cnText.Length > 0 ? cnText : "-").Append('\n');
        sb.Append("section=").Append(TagSections.Get(section).Title).Append('\n');
        sb.Append("category=").Append(category.Length > 0 ? category : "-").Append('\n');
        sb.Append("status=").Append(status).Append('\n');
        if (en.Tag is not null && !string.Equals(en.Tag, tag.Trim(), StringComparison.OrdinalIgnoreCase))
            sb.Append("note: your spelling was normalized to the canonical form above.\n");

        // 停用 / 已废弃必须说清楚：这类标签加进提示词里要么不生效、要么被训练器
        // 当成噪声，而用户从旧的示例里抄来的恰恰是这些。
        if (status == "retired")
            sb.Append("warning: this tag is deprecated - prefer a replacement if one exists.\n");
        else if (status == "unknown")
            sb.Append("warning: this tag exists in the database but has no usage; treat it " +
                      "as unverified.\n");

        summary = $"核对「{tag}」→ {resolved}（{status}）";
        return sb.ToString();
    }

    // ---------------- danbooru_scan_text ----------------

    private string Scan(ProxyConfig cfg, string arguments, out string summary)
    {
        var text = ReadString(arguments, "text");
        if (text.Length == 0)
        {
            summary = "切分（空输入）";
            return "The text was empty. Call this tool again with a Chinese description.";
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err, out summary);

        var hits = db.Scan(text);
        var sb = new StringBuilder();
        sb.Append("input: ").Append(text).Append('\n');

        if (hits.Count == 0)
        {
            sb.Append("result: no tag matched. None of the words in this description ").
              Append("correspond to a tag in the database. This is a normal outcome, not an ").
              Append("error - say the leftover meaning in prose.\n");
            summary = "切分 → 无命中";
            return sb.ToString();
        }

        sb.Append("matched ").Append(hits.Count).Append(" tag(s):\n");
        var i = 0;
        foreach (var h in hits)
        {
            i++;
            sb.Append(i).Append(". tag=").Append(h.Tag);
            if (!string.IsNullOrEmpty(h.Key)) sb.Append(" (from \"").Append(h.Key).Append("\")");
            sb.Append('\n');
        }
        summary = $"切分 → {hits.Count} 个标签";
        return sb.ToString();
    }

    // ---------------- 参数读取 ----------------

    /// <summary>
    /// 从 tool_call 的 arguments 里取 query 与 limit。
    /// 参数不是合法 JSON 时**返回空查询**而不是抛异常：模型手抖写出
    /// <c>{query: white dress}</c>（没加引号）是很常见的，此时如实告诉它
    /// "查询是空的"，比让整轮失败好。
    /// </summary>
    internal static (string Query, int Limit) ReadQueryAndLimit(string arguments)
    {
        var query = ReadString(arguments, "query");
        var limit = TagSearch.ToolLimit;

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("limit", out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) limit = n;
                else if (el.ValueKind == JsonValueKind.String &&
                         int.TryParse(el.GetString(), out var n2)) limit = n2;
            }
        }
        catch { /* query 已经尽力取过了 */ }

        return (query, limit);
    }

    /// <summary>
    /// 取一个字符串字段。宽容度是刻意的：小模型经常把参数写成字符串
    /// （<c>"white dress"</c> 而不是 <c>{"query":"white dress"}</c>），
    /// 或者用中文引号。能救回来的都救回来，救不回来的返回空串由调用方提示重试。
    /// </summary>
    internal static string ReadString(string arguments, string field)
    {
        var raw = (arguments ?? "").Trim();
        if (raw.Length == 0) return "";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // 整个参数就是一个字符串：直接当值用。
            if (root.ValueKind == JsonValueKind.String) return (root.GetString() ?? "").Trim();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(field, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) return (el.GetString() ?? "").Trim();
                // 数字/布尔也认（"limit": 25 这类），但只有字符串字段才可能走到这。
                if (el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return el.ToString().Trim();
                // 数组：模型偶尔写成 {"query":["white dress"]}，取第一个。
                if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
                {
                    var first = el[0];
                    if (first.ValueKind == JsonValueKind.String) return (first.GetString() ?? "").Trim();
                }
            }

            // 字段名写错（model 用 "q"、"keyword"、"text" 之类）时，
            // 若对象里恰好只有一个字符串值，就认它。这一条救回过很多次调用。
            if (root.ValueKind == JsonValueKind.Object)
            {
                string? only = null;
                var count = 0;
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    count++;
                    only = p.Value.GetString();
                }
                if (count == 1) return (only ?? "").Trim();
            }
        }
        catch
        {
            // 不是合法 JSON。若它看起来就是那个值本身（没有花括号），
            // 当成裸值用——这是"没加引号"之外最常见的第二种手抖。
            if (!raw.StartsWith('{') && !raw.StartsWith('['))
                return raw.Trim().Trim('"', '\'', '“', '”');
        }

        return "";
    }
}
