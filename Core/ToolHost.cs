using AnimaCaptioner.Core;
using System.Globalization;
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
    private readonly SearchClient _search;
    private readonly PageReader _reader;

    public ToolHost(VocabService vocab, SearchClient? search = null, PageReader? reader = null)
    {
        _vocab = vocab;
        _search = search ?? new SearchClient();
        _reader = reader ?? new PageReader();
    }

    /// <summary>
    /// 一次工具执行的结果。
    ///
    /// 做成 record 而不是 <c>out</c> 参数，是因为执行体已经是异步的了
    /// （<c>web_search</c> 要发 HTTP），而 C# 不允许 async 方法带 out 参数。
    /// <paramref name="Text"/> 是回灌给模型的文本，<paramref name="Summary"/>
    /// 是给人看的一行摘要（日志与界面）。
    ///
    /// <paramref name="Ok"/> 是"这次调用本身成没成功"，只有 MCP 那条路用得上
    /// （它要回 <c>isError</c>）。**注入给模型的那条路刻意不看它**：模型读的是
    /// Text，「查不到这个标签」和「这次查不了」的区别写在 Text 里，比一个布尔位
    /// 表达得更清楚。
    /// </summary>
    public sealed record ToolResult(string Text, string Summary, bool Ok = true);

    /// <summary>
    /// 工具执行摘要的前缀，**Core 与回归共用的契约**。
    ///
    /// 回归靠它数"我们的工具到底跑了几次"。新增工具时**必须**在这里补一条，
    /// 否则测试会静默漏数：<c>web_search</c>（第 4 个工具）就是这么漏过一次——
    /// 判据里只列了前三个，模型只用搜索时会被数成"执行 0 次"，
    /// 看上去像模型没调工具，实际是判据没跟上。
    ///
    /// 回归里有一条不变量钉住它：**条数必须等于 <see cref="ToolKind"/> 的取值个数**。
    /// </summary>
    public static readonly string[] SummaryPrefixes =
        { "查「", "核对「", "切分", "搜索「", "抓「" };

    /// <summary>
    /// 执行一次调用，返回回灌给模型的文本。
    ///
    /// **这个类是无状态的**（只持 <see cref="VocabService"/> 与 <see cref="SearchClient"/>
    /// 两个引用），所以可以跨请求共享。这里刻意**不**记"本请求已执行几次"：那个计数
    /// 放在共享实例上会跨请求累加，把"每次请求最多 N 次"悄悄变成"全局最多 N 次"——
    /// 跑一阵子之后所有人都查不了库，而日志上什么异常都没有。
    /// 计数归 <see cref="ToolLoop"/> 自己持有（它本来就是一次请求一个实例）。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ProxyConfig cfg, ToolKind kind, string arguments,
                                               CancellationToken ct = default)
    {
        try
        {
            switch (kind)
            {
                case ToolKind.Search: return Search(cfg, arguments);
                case ToolKind.Resolve: return Resolve(cfg, arguments);
                case ToolKind.Scan: return Scan(cfg, arguments);
                case ToolKind.WebSearch: return await WebSearchAsync(cfg, arguments, ct).ConfigureAwait(false);
                case ToolKind.WebFetch: return await WebFetchAsync(cfg, arguments, ct).ConfigureAwait(false);
                default: return new ToolResult("Unknown tool.", "未知工具。", false);
            }
        }
        catch (Exception ex)
        {
            // 词库那边理论上不抛，但 113 MB 的数据 + 任意用户输入 + 一次外部 HTTP，
            // 兜底一层比让上游收到 500 强。
            ProxyLog.Write($"工具执行异常（{kind}）：{ex}");
            return new ToolResult("工具执行失败：" + ex.Message, "工具执行失败：" + ex.Message, false);
        }
    }

    private static ToolResult Fail(string text) => new(text, text, false);

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

    // ---------------- 批量入参的上限 ----------------

    /// <summary>一次调用最多几个查询 / 标签 / 描述。上限是**输出体积**的闸门，不是能力的边界。</summary>
    internal const int MaxQueries = 12;
    internal const int MaxTags = 40;
    internal const int MaxTexts = 8;

    /// <summary>
    /// 每个查询默认回几条。**从 25 降到 8**：25 行里绝大多数是同名变体
    /// （`smile` 搜出来只有第 1 行是那个标签，其余 24 行是 `smiley face hat ornament`
    /// 这种），对模型是纯干扰，而多出来的 17 行要占掉六七百 token。
    /// </summary>
    internal const int DefaultSearchLimit = 8;

    /// <summary>一次调用最多回多少行命中（跨查询合计）。超出就截断并**说明**。</summary>
    private const int MaxHitRows = 60;

    // ---------------- danbooru_search_tags ----------------

    private ToolResult Search(ProxyConfig cfg, string arguments)
    {
        var queries = ReadStringList(arguments, "queries", "query", MaxQueries);
        if (queries.Count == 0)
        {
            return new ToolResult(
                "No query was given. Pass an array of one or two word queries, for example " +
                "{\"queries\":[\"white dress\",\"蕾丝\"]}.",
                "查（空查询）");
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err);

        // 每个查询多要一条：**只为判断"还有更多"，不交给模型**。
        // 让模型知道结果被截断了，比让它以为"库里只有这三条"重要——后者会
        // 让它断定这个词库里没有别的近义标签，转而去凭记忆编。
        var limit = Math.Clamp(ReadInt(arguments, "limit", DefaultSearchLimit), 1, TagSearch.ToolLimit);

        var sb = new StringBuilder();
        var shown = 0;
        var found = 0;
        var legend = false;
        var truncated = false;

        for (var i = 0; i < queries.Count && !truncated; i++)
        {
            var query = queries[i];
            var res = TagSearch.QueryDetailed(db, query, limit + 1);
            var more = res.Hits.Count > limit;

            sb.Append("query ").Append(i + 1).Append('/').Append(queries.Count)
              .Append(": ").Append(query).Append(" → ");

            if (res.Hits.Count == 0)
            {
                sb.Append("none (no tag matches this; try fewer or more general words)\n");
                continue;
            }

            sb.Append(Math.Min(res.Hits.Count, limit));
            if (more) sb.Append('+');
            sb.Append(" tag(s)");
            if (res.SplitMatch)
                sb.Append(" (nothing matched the whole query, so it was split into words; " +
                          "only partly related)");
            sb.Append('\n');

            if (!legend)
            {
                // 图例只写一次。原先每个结果都重复一遍，25 行时那 150 字符就是白烧的。
                sb.Append("format: N. tag=<exact tag name> | cn=<meaning> | posts=<usage> | ")
                  .Append("status=<anima|valid|retired|unknown>\n");
                legend = true;
            }

            var perQuery = 0;
            foreach (var h in res.Hits)
            {
                if (shown >= MaxHitRows)
                {
                    sb.Append("(output limit reached at ").Append(MaxHitRows)
                      .Append(" rows; narrow the queries or lower limit)\n");
                    truncated = true;
                    break;
                }
                if (perQuery >= limit) break;
                perQuery++;
                shown++;
                found++;
                sb.Append(shown).Append(". tag=").Append(h.Tag)
                  .Append(" | cn=").Append(h.HasCn ? h.Cn : "-")
                  .Append(" | posts=").Append(h.PostCount.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | status=").Append(StatusOf(h))
                  .Append('\n');
            }
        }

        return new ToolResult(sb.ToString(), QuerySummary("查", queries, found, "条"));
    }

    // ---------------- danbooru_resolve_tags ----------------

    private ToolResult Resolve(ProxyConfig cfg, string arguments)
    {
        var tags = ReadStringList(arguments, "tags", "tag", MaxTags);
        if (tags.Count == 0)
        {
            return new ToolResult(
                "No tag was given. Pass an array of tag names or Chinese meanings, for " +
                "example {\"tags\":[\"blush\",\"white dress\",\"白色连衣裙\"]}.",
                "核对（空输入）");
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err);

        var sb = new StringBuilder();
        var found = 0;
        var misses = new List<string>();

        for (var i = 0; i < tags.Count; i++)
        {
            var input = tags[i];
            var (resolved, note) = ResolveOne(db, input);

            sb.Append(i + 1).Append(". ").Append(input).Append(" → ");
            if (resolved is null)
            {
                sb.Append("NOT FOUND\n");
                misses.Add(input);
                continue;
            }

            found++;
            var cnText = db.LookupCn(resolved);
            sb.Append("tag=").Append(resolved)
              .Append(" | cn=").Append(cnText.Length > 0 ? cnText : "-")
              .Append(" | posts=").Append(db.PostCount(resolved).ToString("N0", CultureInfo.InvariantCulture))
              .Append(" | status=").Append(db.Status(resolved));
            if (note.Length > 0) sb.Append(" (").Append(note).Append(')');
            sb.Append('\n');
        }

        // 未命中的话只写一遍。原先每个 miss 后面都跟三句英文（153 字符），
        // 十个拼错的标签就是 1.5 KB 的同一段话。
        sb.Append("resolved ").Append(found).Append(" of ").Append(tags.Count);
        if (misses.Count > 0)
        {
            sb.Append(". not tags: ").Append(string.Join(", ", misses))
              .Append(". Do not use those as tags - say the concept in prose instead, or ")
              .Append("search for a different name.");
        }
        sb.Append('\n');

        return new ToolResult(sb.ToString(), QuerySummary("核对", tags, found, "命中"));
    }

    /// <summary>
    /// 解析一个写法，返回 (规范标签, 备注)。备注只在值得告诉模型时非空：拼写被
    /// 规范化、或中文是连括号一起命中的——模型据此知道"这不是我写的那串字，
    /// 但它指向同一个标签"。
    ///
    /// 先 <see cref="VocabDb.ResolveEnglish"/>，不成立再
    /// <see cref="VocabDb.ResolveChinese"/>。这条顺序与 AnimaCaptioner 的
    /// SuspiciousMentions 判据一致——两处不一致的话，代理说"这个词库认得"、
    /// 而打标软件说"查不到"，用户会不知道信谁。
    ///
    /// 第三层是**中文的括号限定形式回落**：`ResolveChinese` 的精确键只认库里
    /// 逐字存的那一个中文名，而 33 万条中文名里近一半带「（限定词）」后缀
    /// （`弓（武器）`、`箭头（符号）`…），用户和 agent 不会连着括号一起打。
    /// 实测"裸形式不是精确键、且类别不是 junk"的条目有 11,138 条，正是这一层补的。
    ///
    /// 修在**这一层而不是 vendored 的 VocabDb 里**：内核的"只允许词库键 ⊂ 用户
    /// 输入"是它明确记着代价的决定，而 `Search` 那条路本来就是好的
    /// （`cn.StartsWith(q)` 命中）。
    ///
    /// 回落**必须收窄，而且由内核自己来裁**：
    ///   * 只接受"中文名与输入完全相同"或"中文名 = 输入 + 括号限定"——放宽到任意
    ///     `Search` 命中就等于把内核特意堵上的坑重新挖开（`少女` 的前几条是
    ///     `少女前线`、`少女与战车`，全都要拒）；
    ///   * 就算形状对上了，也把库里那个完整中文名交回 `ResolveChinese` 再审一次：
    ///     `少女（mokufuu）` 和 `弓（武器）` 的形状一模一样，差别只在类别
    ///     （character vs general），实测不审类别的话 `少女` 会解析到
    ///     `girl (mokufuu)`；
    ///   * 而且**只认第一个形状对上的**：`初音未来` 的头号变体
    ///     `初音未来（VOCALOID）`（146,936 帖）是 character 被拒，若继续往后挑就会
    ///     挑到 `初音未来（Cosplay）`（2,728 帖，general，能过审）——一个自信的
    ///     错答案比查不到更糟，因为模型不会再去 search。
    ///
    /// 触发条件含 `fuzzy`：内核那条模糊路在中文里经常给出无关的短词，而
    /// "同名或同名+括号限定"指向的是同一个名字，可信度严格更高。
    /// </summary>
    private static (string? Tag, string Note) ResolveOne(VocabDb db, string tag)
    {
        var en = db.ResolveEnglish(tag);
        var cn = en.Tag is null ? db.ResolveChinese(tag) : new ResolveResult(null, "");

        if (en.Tag is null && (cn.Tag is null || cn.Status == "fuzzy"))
        {
            // limit 给大：Search 本身要线性扫完 33 万行，多返回几乎不要钱，
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

        var resolved = en.Tag ?? cn.Tag;
        if (resolved is null) return (null, "");

        if (en.Tag is not null)
            return (resolved, string.Equals(en.Tag, tag.Trim(), StringComparison.OrdinalIgnoreCase)
                ? ""
                : "spelling normalized");

        if (cn.Status != "qualified") return (resolved, "");
        var cnName = db.LookupCn(resolved);
        return (resolved, cnName.Length > 0 ? "matched cn \"" + cnName + "\"" : "");
    }

    /// <summary>
    /// 搜索行里的状态。判据与 <see cref="VocabDb.Status"/> 同一口径（Anima 索引优先、
    /// 废弃次之、无引用最后），但**不查字典**：<see cref="TagSearch.Hit"/> 已经把
    /// InAnima / Deprecated / PostCount 都带出来了，25 行就是 25 次字典查找的差别，
    /// 在批量路径上没必要。
    /// </summary>
    private static string StatusOf(TagSearch.Hit h)
    {
        if (h.InAnima) return "anima";
        if (h.PostCount == 0) return "unknown";
        return h.Deprecated ? "retired" : "valid";
    }

    /// <summary>
    /// 一条给人看的摘要（日志面板 / 界面）。单个时原样列出，多个时列第一个 + "等 N 个"：
    /// 四十个标签全列出来会把日志撑爆，而只写"批量"又不便于事后追查。
    /// </summary>
    private static string QuerySummary(string verb, IReadOnlyList<string> items, int count, string unit)
    {
        var head = "「" + Cut(items[0], 24) + "」";
        return items.Count == 1
            ? $"{verb}{head}→ {count} {unit}"
            : $"{verb}{head}等 {items.Count} 个 → {count} {unit}";
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

    // ---------------- danbooru_scan_texts ----------------

    private ToolResult Scan(ProxyConfig cfg, string arguments)
    {
        var texts = ReadStringList(arguments, "texts", "text", MaxTexts);
        if (texts.Count == 0)
        {
            return new ToolResult(
                "No text was given. Pass an array of Chinese descriptions, for example " +
                "{\"texts\":[\"一个女孩在笑，白色连衣裙\"]}.",
                "切分（空输入）");
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err);

        var sb = new StringBuilder();
        var total = 0;
        var rows = 0;
        var truncated = false;

        for (var i = 0; i < texts.Count && !truncated; i++)
        {
            var text = texts[i];

            // 同一个标签只留第一处。DP 是逐位置输出最优选择，`雨` 在「雨中」和
            // 「雨伞」里各命中一次就会给模型两行一模一样的 `tag=rain`——而模型
            // 看到重复会以为"有两个都要写"，或者干脆怀疑结果坏了。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hits = new List<ScanHit>();
            foreach (var h in db.Scan(text))
                if (seen.Add(h.Tag)) hits.Add(h);

            sb.Append("text ").Append(i + 1).Append('/').Append(texts.Count)
              .Append(": ").Append(text).Append(" → ").Append(hits.Count).Append(" tag(s)\n");

            if (hits.Count == 0)
            {
                sb.Append("   none of these words matched a tag (that is a normal outcome, " +
                          "not an error)\n");
                continue;
            }

            foreach (var h in hits)
            {
                if (rows >= MaxHitRows)
                {
                    sb.Append("   (output limit reached at ").Append(MaxHitRows)
                      .Append(" rows; scan fewer or shorter descriptions)\n");
                    truncated = true;
                    break;
                }
                rows++;
                total++;
                sb.Append("   tag=").Append(h.Tag);
                if (!string.IsNullOrEmpty(h.Key)) sb.Append(" | from \"").Append(h.Key).Append('"');
                sb.Append(" | status=").Append(db.Status(h.Tag)).Append('\n');
            }
        }

        return new ToolResult(sb.ToString(), QuerySummary("切分", texts, total, "个标签"));
    }

    // ---------------- web_search ----------------

    /// <summary>
    /// 联网搜索。**这里刻意不做"搜索能不能用"的判断以外的任何补救**：
    /// 搜索失败时如实把失败原因交给模型（见 <see cref="SearchClient.ToToolContent"/>），
    /// 而不是回一句空结果——后者会让模型把"网断了"当成"这个概念不存在"，
    /// 然后凭记忆编一个答案出来，而它自己并不知道自己在编。
    /// </summary>
    private async Task<ToolResult> WebSearchAsync(ProxyConfig cfg, string arguments, CancellationToken ct)
    {
        var query = ReadString(arguments, "query");
        if (query.Length == 0)
        {
            return new ToolResult(
                "The query was empty. Call this tool again with a few keywords, " +
                "for example \"Qt 6.8 release date\".",
                "搜索（空查询）");
        }

        if (!cfg.SearchReady)
        {
            // 正常情况下走不到这里：没配好后端时这个工具根本不会被下发。
            // 但模型可能凭上文记忆去调一个这次没下发的工具名，所以如实回一句。
            return new ToolResult(
                "Web search is not available on this proxy right now, so this could not be " +
                "performed. Do not pretend that you searched, and do not invent sources or " +
                "urls. Tell the user that web search is unavailable.",
                "搜索 → 未配置");
        }

        var r = await _search.SearchAsync(cfg, query, ct).ConfigureAwait(false);
        var text = SearchClient.ToToolContent(r, query);
        return new ToolResult(text, r.Ok
            ? $"搜索「{query}」→ {r.Hits.Count} 条"
            : $"搜索「{query}」→ 失败：{r.Error}");
    }

    // ---------------- web_fetch ----------------

    /// <summary>
    /// 抓一个网页。与 <see cref="WebSearchAsync"/> 同一条原则：
    /// **失败如实说**（<see cref="PageReader.ToToolContent"/> 里那套信封），
    /// 绝不回一个空正文 —— 后者会让模型以为"这页没什么内容"，
    /// 而真实情况是"我们没读到"，接着它就会凭记忆补一段出来。
    /// </summary>
    private async Task<ToolResult> WebFetchAsync(ProxyConfig cfg, string arguments, CancellationToken ct)
    {
        var url = ReadString(arguments, "url");
        if (url.Length == 0)
        {
            return new ToolResult(
                "The url was empty. Call this tool again with the full url of one page, " +
                "for example \"https://learn.microsoft.com/dotnet/core/whats-new\".",
                "抓（空网址）");
        }

        if (!cfg.FetchReady)
        {
            // 正常情况下走不到这里：没配好时这个工具根本不会被下发。
            // 但模型可能凭上文记忆去调一个这次没下发的工具名，所以如实回一句。
            return new ToolResult(
                "Web page fetching is not available on this proxy right now, so this could not " +
                "be performed. Do not pretend that you read the page, and do not invent its " +
                "content. Tell the user that page fetching is unavailable.",
                "抓 → 未启用");
        }

        var render = ReadBool(arguments, "render");
        var r = await _reader.FetchAsync(cfg, url, render, null, ct).ConfigureAwait(false);
        var text = PageReader.ToToolContent(r, url);

        var label = ShortLabel(r.Url.Length > 0 ? r.Url : url);
        var summary = r.Ok
            ? $"抓「{label}」→ {r.Text.Length} 字符（{r.Engine}）"
            : $"抓「{label}」→ 失败：{Cut(r.Error, 60)}";
        return new ToolResult(text, summary);
    }

    /// <summary>日志里的短标签：主机 + 截短的路径。完整 URL 常常长到把日志撑爆。</summary>
    private static string ShortLabel(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Cut(url, 50);
            var path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
            return Cut(uri.Host + path, 60);
        }
        catch { return Cut(url, 50); }
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------------- 参数读取 ----------------

    /// <summary>
    /// 取一个整数参数（<c>limit</c> 这类）。非数字或解析失败时返回
    /// <paramref name="fallback"/>——与 <see cref="ReadString"/> 同样"尽力救，
    /// 救不回来就用默认值"。
    /// </summary>
    internal static int ReadInt(string arguments, string field, int fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(field, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
                if (el.ValueKind == JsonValueKind.String &&
                    int.TryParse(el.GetString(), out var n2)) return n2;
            }
        }
        catch { /* 参数不是合法 JSON：用默认值 */ }
        return fallback;
    }

    /// <summary>
    /// 取一个字符串数组（批量工具的入参）。宽容度是刻意的，逐条对应一种实测见过的
    /// 模型手抖：
    ///   * 声明是数组却传了裸字符串（<c>{"queries":"white dress"}</c>）——当单元素；
    ///   * 整串就是那个值（<c>"white dress"</c>，没有花括号）——同样当单元素；
    ///   * 字段名写错（<c>{"q":…}</c>、<c>{"keyword":…}</c>）——对象里所有字符串 /
    ///     字符串数组都收下，反正这类工具的语义就是"给我一串词"；
    ///   * 传了对象数组（<c>[{"tag":"a"}]</c>）——不收，交给调用方回一句"参数不对"，
    ///     因为猜它想表达什么比失败更危险。
    ///
    /// 去重（大小写不敏感）并保留顺序；超过 <paramref name="cap"/> 的部分丢掉——
    /// 调用方从返回条数就能看出被截断了。
    /// </summary>
    internal static List<string> ReadStringList(string arguments, string primary, string fallback, int cap)
    {
        var outp = new List<string>();
        var raw = (arguments ?? "").Trim();
        if (raw.Length == 0 || cap <= 0) return outp;

        void Add(string? s)
        {
            var v = (s ?? "").Trim();
            if (v.Length == 0 || outp.Count >= cap) return;
            if (outp.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) return;
            outp.Add(v);
        }

        void AddValue(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String) Add(el.GetString());
            else if (el.ValueKind == JsonValueKind.Array)
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) Add(item.GetString());
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String) { Add(root.GetString()); return outp; }
            if (root.ValueKind == JsonValueKind.Array) { AddValue(root); return outp; }
            if (root.ValueKind != JsonValueKind.Object) return outp;

            if (root.TryGetProperty(primary, out var el)) AddValue(el);
            if (outp.Count == 0 && root.TryGetProperty(fallback, out var el2)) AddValue(el2);

            // 字段名写错时：把对象里所有"像一串词"的值都收下。
            if (outp.Count == 0)
                foreach (var p in root.EnumerateObject()) AddValue(p.Value);

            return outp;
        }
        catch
        {
            // 不是合法 JSON。若它看起来就是那个值本身（没有花括号方括号），当裸值用。
            if (!raw.StartsWith('{') && !raw.StartsWith('['))
                Add(raw.Trim('"', '\'', '“', '”'));
            return outp;
        }
    }

    /// <summary>
    /// 取一个布尔字段。模型给 <c>true</c>、<c>"true"</c>、<c>1</c>、<c>"yes"</c> 都认 ——
    /// 把布尔写成字符串是常态，与 <see cref="ReadString"/> 的宽容度保持一致。
    /// 认不出时返回 false（<c>render</c> 的默认就是"不强制渲染"）。
    /// </summary>
    internal static bool ReadBool(string arguments, string field)
    {
        var raw = ReadString(arguments, field).ToLowerInvariant();
        return raw is "true" or "1" or "yes" or "y" or "on" or "是";
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
