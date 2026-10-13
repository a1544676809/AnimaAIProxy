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

    /// <summary>
    /// 执行一次调用。返回回灌给模型的文本；<paramref name="summary"/> 是一句
    /// 给人看的一行摘要，供日志与界面显示。
    ///
    /// **这个类是无状态的**（只持一个 <see cref="VocabService"/> 引用），
    /// 所以可以跨请求共享。这里刻意**不**记"本请求已执行几次"：那个计数
    /// 放在共享实例上会跨请求累加，把"每次请求最多 N 次"悄悄变成"全局最多 N 次"——
    /// 跑一阵子之后所有人都查不了库，而日志上什么异常都没有。
    /// 计数归 <see cref="ToolLoop"/> 自己持有（它本来就是一次请求一个实例）。
    /// </summary>
    public string Execute(ProxyConfig cfg, ToolKind kind, string arguments, out string summary)
    {
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

        // 中文的括号限定形式回落。
        //
        // `ResolveChinese` 的精确键只认库里逐字存的那一个中文名，而词库
        // 331,836 条中文名里有 **163,154 条（49%）带「（限定词）」后缀**：
        // `弓（武器）`、`箭头（符号）`、`照片（媒介）`、`嘘（手势）`……
        // 用户和 agent 不会连着括号一起打，于是最常用的那个写法反而查不到。
        // 实测"裸形式不是精确键、且类别不是 junk"的条目有 **11,138 条**，
        // 就是这一层要补的那一批。
        //
        // 修在**这一层而不是 vendored 的 VocabDb 里**：内核的
        // "只允许词库键 ⊂ 用户输入"是它明确记着代价的决定，而 `Search` 那条路
        // 本来就是好的（`cn.StartsWith(q)` 命中）。让内核保持与上游逐字一致、
        // 把回落放在我们自己的解析工具里，既补上了这个洞，又不用去动一个
        // 我们已经承诺"只搬运不修改"的依赖。
        //
        // 回落**必须收窄，而且要由内核自己来裁**：
        //
        //   * 只接受"中文名与输入完全相同"或"中文名 = 输入 + 括号限定"。
        //     放宽到任意 `Search` 命中，就等于把内核特意堵上的坑重新挖开，
        //     而且是挖在更外层（实测 `Search("少女")` 的前几条是
        //     `少女前线`、`少女与战车`，全都要拒掉）。
        //   * 就算形状对上了，也把**库里那个完整中文名**交回 `ResolveChinese`
        //     再审一次；它认得，才说明这一条不在它的 JunkCategories
        //     （artist / character / copyright / 原神角色）里。
        //
        //     这一手是必须的，不是保险：`少女（mokufuu）` 和 `弓（武器）`
        //     的形状**一模一样**，差别只在类别（character vs general）。
        //     实测不审类别的话 `少女` 会解析到 `girl (mokufuu)` ——
        //     正是内核注释里点名的那个坑。
        //
        //     不自己抄一份类别黑名单：抄了就会和上游走偏，而"这个词库认不认
        //     这个名字"本来就该由词库自己回答。
        //   * 而且**只认用得最多的那一个同名变体**：形状对上的第一条如果内核
        //     不肯认，就到此为止，不去挑一个更冷门的兄弟。这一条也是被实测逼
        //     出来的——`初音未来` 的头号变体 `初音未来（VOCALOID）`（146,936 帖）
        //     是 character 被拒，若继续往后挑就会挑到 `初音未来（Cosplay）`
        //     （2,728 帖，category=general，能过审），于是"初音未来"被解析成
        //     `hatsune miku (cosplay)`。那比查不到更糟：查不到时模型会去 search，
        //     而一个**自信的错答案**会被它直接拿去用。
        //
        // 触发条件含 `fuzzy`：内核那条模糊路在中文里经常给出无关的短词，
        // 而"同名或同名+括号限定"指向的是**同一个名字**，可信度严格更高。
        if (en.Tag is null && (cn.Tag is null || cn.Status == "fuzzy"))
        {
            // limit 给大：Search 本身要线性扫完 33 万行，多返回一些几乎不要钱，
            // 而少返回会让"最佳候选恰好排在 25 名之后"变成随机失败。
            foreach (var hit in db.Search(tag, 200))
            {
                if (!IsSameNameOrQualified(hit.Cn, tag)) continue;

                // Search 的排序是"分数升序 → 帖子数降序"，所以形状对上的第一条
                // 就是这个同名变体里用得最多的那个。
                var verdict = db.ResolveChinese(hit.Cn);
                if (verdict.Tag is not null) cn = new ResolveResult(verdict.Tag, "qualified");
                break;
            }
        }

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

    /// <summary>
    /// 词库里的中文名是不是**就是**这个词（或者只是多挂了一个括号限定）。
    ///
    /// 只认这两种写法，是因为它们指向同一个名字：`弓（武器）` 和 `弓` 说的是
    /// 同一个概念，括号只是消歧后缀。放宽到任意包含就会把 `少女` 解析到
    /// `少女前线` 之类的作品标签上——vendored 的 <c>ResolveChinese</c> 里
    /// 明确记着这个坑，这里不能换个地方重新踩进去。
    ///
    /// **形状对上不等于可以用**：`少女（mokufuu）` 的形状和 `弓（武器）`
    /// 完全一样。能不能用由 <see cref="ResolveChinese"/> 自己裁，见调用处。
    ///
    /// 半角括号也认（库里只有 44 条，但模型和用户都可能打出来）。
    /// </summary>
    private static bool IsSameNameOrQualified(string cn, string input)
    {
        if (cn.Length == 0) return false;
        if (string.Equals(cn, input, StringComparison.Ordinal)) return true;
        if (cn.Length <= input.Length) return false;
        if (!cn.StartsWith(input, StringComparison.Ordinal)) return false;

        var rest = cn[input.Length..];
        return rest.StartsWith('（') || rest.StartsWith('(');
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
