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
        var queries = ReadStringList(arguments, "queries", "query", MaxQueries, out var dropped);
        if (queries.Count == 0)
        {
            return new ToolResult(
                "No query was given. Pass an array of one or two word queries, for example " +
                "{\"queries\":[\"white dress\",\"蕾丝\"]}.",
                "查（空查询）");
        }

        var db = TryVocab(cfg, out var err);
        if (db is null) return Fail(err);

        var limit = Math.Clamp(ReadInt(arguments, "limit", DefaultSearchLimit), 1, TagSearch.ToolLimit);

        // 60 行总额度**按查询数均分**（至少 1 行）。不均分的话"12 条查询 × 默认
        // limit 8"就是 96 行，后 4 条查询连标题都印不出来——调用方无法区分
        // "没查过"和"查了但没结果"，而这正是这类批量工具最不该含糊的地方。
        var perQuery = Math.Max(1, Math.Min(limit, MaxHitRows / queries.Count));

        var sb = new StringBuilder();

        // 上限相关的说明放**最前面**：调用方往往只看第一行就判断"这次全查了"。
        if (dropped > 0)
            sb.Append("note: only ").Append(queries.Count).Append(" of ")
              .Append(queries.Count + dropped).Append(" queries were processed (cap ")
              .Append(MaxQueries).Append("); ").Append(dropped).Append(" skipped.\n");

        var shown = 0;
        var found = 0;
        var legend = false;

        for (var i = 0; i < queries.Count; i++)
        {
            var query = queries[i];
            var (rows, more, mode) = QueryRows(db, query, perQuery);

            sb.Append("query ").Append(i + 1).Append('/').Append(queries.Count)
              .Append(": ").Append(query).Append(" → ");

            if (rows.Count == 0)
            {
                sb.Append(mode switch
                {
                    "split" => "none (the words matched only character/series/artist tags - not " +
                               "usable as scene tags; try other words)\n",
                    "namelike" => "none (this word only appears inside character/series names; " +
                                  "search the full name or a more general word)\n",
                    _ => "none (no tag matches this; try fewer or more general words)\n",
                });
                continue;
            }

            sb.Append(rows.Count);
            if (more) sb.Append('+');
            sb.Append(" tag(s)");
            if (mode == "split") sb.Append(" (split into words; only general tags kept)");
            if (mode == "phrase") sb.Append(" (matched by splitting the phrase into known concepts)");

            // 行本身保持四段（tag / cn / posts / status），不塞第五个字段：下游解析器
            // 是按 `status=…` 到行尾取的，多一段就会把 category 读成 status 的一部分。
            // "这一行是不是角色标签"用**行数说明**给，模型照样能对上（非 general 行
            // 一律排在后面）。
            var nonGeneral = rows.Count(x => x.Section != TagSections.General);
            if (nonGeneral > 0)
                sb.Append(", ").Append(nonGeneral)
                  .Append(" of them are character/series/artist tags (not scene tags)");
            sb.Append('\n');

            if (!legend)
            {
                // 图例只写一次。原先每个结果都重复一遍，25 行时那 150 字符就是白烧的。
                sb.Append("format: N. tag=<exact tag name> | cn=<meaning> | posts=<usage> | ")
                  .Append("status=<anima|valid|retired|unknown|control>\n");
                legend = true;
            }

            foreach (var row in rows)
            {
                shown++;
                found++;
                sb.Append(shown).Append(". tag=").Append(row.Tag)
                  .Append(" | cn=").Append(row.Cn)
                  .Append(" | posts=").Append(row.Posts is { } p
                      ? p.ToString("N0", CultureInfo.InvariantCulture) : "-")
                  .Append(" | status=").Append(row.Status)
                  .Append('\n');
            }
        }

        return new ToolResult(sb.ToString(), QuerySummary("查", queries, found, "条"));
    }

    /// <summary>一行结果。`Posts` 为 null 表示"这类词没有帖数"（控制词）。</summary>
    private sealed record Row(string Tag, string Cn, int? Posts, string Status, string Section);

    /// <summary>
    /// 一条查询 → 若干行。三种来源，按可信度从高到低：
    ///
    /// 1. **词库查询**（精确 / 前缀 / 中文命中）——正常情况，直接用；
    /// 2. **中文整句切分**（`db.Scan`）——查询是中文短句、整串与拆词都没给出好结果时用。
    ///    它按位置选最长词，比"拆成单词再搜"准：`湿漉漉的街道` 能同时给出 `wet floor`
    ///    和 `street`，而拆词只命中前者；
    /// 3. **拆词兜底**（<see cref="TagSearch.QueryResult.SplitMatch"/>）——最弱，只留
    ///    general：`rain night` 以前会给 `whisperain (arknights)`。
    /// </summary>
    private static (List<Row> Rows, bool More, string Mode) QueryRows(VocabDb db, string query, int perQuery)
    {
        var res = TagSearch.QueryDetailed(db, query, perQuery + 1);

        if ((res.Hits.Count == 0 || res.SplitMatch) && query.Any(VocabDb.IsCjk))
        {
            var phrase = PhraseRows(db, query);
            if (phrase.Count > 0)
                return (phrase.Take(perQuery).ToList(), phrase.Count > perQuery, "phrase");
        }

        var hits = res.Hits;
        if (res.SplitMatch)
        {
            // 先多取一些再过滤：前 25 条可能全是角色标签。
            // 门槛收紧到"general + Anima 常用"：拆词本来就是弱匹配，再放冷门
            // Danbooru 标签进来只会让调用方以为 `night` 之外还有别的场景词。
            var wide = TagSearch.QueryDetailed(db, query, TagSearch.ToolLimit);
            hits = wide.Hits.Where(h => SectionOf(h.Tag, db) == TagSections.General
                                        && db.Status(h.Tag) == "anima").ToList();
        }

        var rows = new List<Row>(perQuery);
        foreach (var h in hits.Take(perQuery))
        {
            var section = SectionOf(h.Tag, db);
            rows.Add(new Row(h.Tag, h.HasCn ? h.Cn : "-", db.PostCount(h.Tag),
                             StatusOfTag(h.Tag, db, section), section));
        }

        // 查询本身就是控制词（`explicit`、`score_8` 在库里没有行）时，把它自己
        // 作为一行给出来——否则调用方会以为"这个词库里没有"，转头把它删掉。
        var control = ControlCanonical(query);
        if (control is not null &&
            !rows.Any(r => string.Equals(r.Tag, control, StringComparison.OrdinalIgnoreCase)))
            rows.Insert(0, new Row(control, "-", null, "control", TagSections.General));

        // 单个英文词、且只命中角色/作品名时，不把这些名字当结果：调用方问的是
        // "refined 这个概念有没有标签"，给它 `refined barzam`（高达角色）只会被
        // 抄进提示词。名字搜索请给完整名字（多词，不触发这条）。
        if (rows.Count > 0 && rows.All(r => r.Section != TagSections.General && r.Status != "control")
            && IsSingleLatinWord(query))
            return (new List<Row>(), false, "namelike");

        return (rows, hits.Count > perQuery, res.SplitMatch ? "split" : "");
    }

    /// <summary>单个拉丁词（不含空格与 CJK）——用来区分"概念词"与"完整名字"。</summary>
    private static bool IsSingleLatinWord(string query)
    {
        var q = query.Trim();
        return q.Length > 0 && q.All(c => c < 0x80) && !q.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>把一句中文按词库切成概念，再翻成标签行（和 scan 工具同一套切分）。</summary>
    private static List<Row> PhraseRows(VocabDb db, string query)
    {
        var rows = new List<Row>();
        foreach (var h in db.Scan(query))
        {
            if (rows.Any(r => string.Equals(r.Tag, h.Tag, StringComparison.OrdinalIgnoreCase))) continue;

            var section = SectionOf(h.Tag, db);
            // 整句切出来的角色 / 作品名同样不该当场景标签：`银发的少女` 会切出
            // `jeanne d'arc (girl from orleans) (fate)` 这种。
            if (section != TagSections.General) continue;

            var cn = db.LookupCn(h.Tag);
            rows.Add(new Row(h.Tag, cn.Length > 0 ? cn : "-", db.PostCount(h.Tag),
                             StatusOfTag(h.Tag, db, section), section));
        }
        return rows;
    }

    // ---------------- danbooru_resolve_tags ----------------

    private ToolResult Resolve(ProxyConfig cfg, string arguments)
    {
        var tags = ReadStringList(arguments, "tags", "tag", MaxTags, out var dropped);
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
        var notes = new List<string>();

        for (var i = 0; i < tags.Count; i++)
        {
            var input = tags[i];
            var (resolved, note) = ResolveOne(db, input);

            sb.Append(i + 1).Append(". ").Append(input).Append(" → ");

            if (resolved is null)
            {
                // 词库里查不到，但它可能是**控制词**（质量 / 安全 / 数量…）：
                // `explicit`、`score_8` 在合并表里根本没有行，可它们在 Anima 的
                // 提示词里是完全合法的头部词。判"不存在"会让模型把它们删掉。
                var control = ControlCanonical(input);
                if (control is null)
                {
                    sb.Append("NOT FOUND\n");
                    misses.Add(input);
                    continue;
                }

                found++;
                sb.Append("tag=").Append(control).Append(" | cn=- | posts=- | status=control\n");
                // 备注放行尾会破坏 `status=…` 到行尾的解析（下游按这个取状态），
                // 所以统一收集起来，在 summary 之后单独写。
                if (!string.Equals(control, input.Trim(), StringComparison.OrdinalIgnoreCase))
                    notes.Add(input.Trim() + " is written as \"" + control + "\" in Anima prompts.");
                if (string.Equals(control, "general", StringComparison.OrdinalIgnoreCase))
                    notes.Add("\"general\" is the Danbooru rating word; Anima prompts use \"safe\".");
                continue;
            }

            found++;
            var section = SectionOf(resolved, db);
            var cnText = db.LookupCn(resolved);
            sb.Append("tag=").Append(resolved)
              .Append(" | cn=").Append(cnText.Length > 0 ? cnText : "-")
              .Append(" | posts=").Append(db.PostCount(resolved).ToString("N0", CultureInfo.InvariantCulture))
              .Append(" | status=").Append(StatusOfTag(resolved, db, section));
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

        foreach (var n in notes) sb.Append("note: ").Append(n).Append('\n');

        if (dropped > 0)
            sb.Append("note: only ").Append(tags.Count).Append(" of ")
              .Append(tags.Count + dropped).Append(" tags were checked (cap ")
              .Append(MaxTags).Append("); ").Append(dropped).Append(" skipped.\n");

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
    /// 标签所属的大类（九段之一）。用作输出里的 `category=`，也用来判"是不是控制词"。
    ///
    /// 用 <see cref="TagSections.Classify"/> 而不是词库的原始 `category` 列：后者是
    /// 数据源自己的分类串（`饰品`、`anima-cat6`），而这里要的是与官方大类顺序
    /// 对齐的那九段，且**不依赖词库**——`explicit`、`score_8` 根本不在库里，
    /// Classify 仍认得它们是安全词 / 质量词。
    /// </summary>
    private static string SectionOf(string tag, VocabDb db) => TagSections.Classify(tag, db);

    /// <summary>五段头部（质量 / 元信息 / 年代 / 安全 / 数量）——控制词，不是场景标签。</summary>
    private static bool IsControlSection(string section) =>
        Array.IndexOf(TagSections.HeadKeys, section) >= 0;

    /// <summary>
    /// 输出里的状态：`anima` / `valid` / `retired` / `unknown` / `control`。
    ///
    /// 判据仍交给 <see cref="VocabDb.Status"/>（Anima 索引优先、废弃次之），
    /// 只补一条：**词库说"无引用"而它其实是控制词**（`masterpiece`、`safe`、
    /// `score_7`）时写 `control`。这些词在合并表里 post_count=0 是正常的——
    /// 按 `unknown`（"没有使用量"）说，模型会把它们当成废标签删掉，而它们
    /// 恰恰是 Anima 必须写在头部的词。
    /// </summary>
    private static string StatusOfTag(string tag, VocabDb db, string section)
    {
        var st = db.Status(tag);
        if (st == "unknown" && IsControlSection(section)) return "control";
        return st;
    }

    /// <summary>
    /// 控制词的规范写法。命中返回词表里的那个成员（统一大小写），否则 null。
    ///
    /// 只认 Anima 官方词表（<see cref="TagSections"/> 的五组数组）+ `score_N`
    /// + Danbooru 的 `general`（Anima 写 `safe`，见 <see cref="SafetyRating"/>）。
    /// **它不替代词库查询**：词库里有的（`1girl`、`blurry`）照常解析，只有
    /// 查不到时才用这张表兜底——否则会把 `unsafe` 这种长得像的词也认成控制词。
    /// </summary>
    private static string? ControlCanonical(string input)
    {
        var low = input.Trim().ToLowerInvariant();
        if (low.Length == 0) return null;

        foreach (var list in new[]
                 {
                     TagSections.QualityWords, TagSections.MetaWords, TagSections.PeriodWords,
                     TagSections.SafetyWords, TagSections.CountWords,
                 })
            foreach (var w in list)
                if (string.Equals(w, low, StringComparison.OrdinalIgnoreCase)) return w;

        if (low.Length == 7 && low.StartsWith("score_", StringComparison.Ordinal)
            && char.IsAsciiDigit(low[6]))
            return low;

        // Danbooru 的档位词 `general` 在 Anima 里写 `safe`（见 SafetyRating）。
        // **返回值保持调用方写的写法**：换成 `safe` 会让 search 里 `general` 变成
        // 另一个标签名，调用方对不上号；提示放在备注里说。
        if (low == "general") return "general";
        return null;
    }

    /// <summary>
    /// "这段话里哪些字没变成标签"。按 <see cref="ScanHit.Key"/> 在原文里逐段回填覆盖，
    /// 剩下的连续片段就是没匹配上的部分（去掉标点与"的了在"这类虚词）。
    ///
    /// 不给出来会出真问题：长句里"夜"、"霓虹"丢了而输出只写 6 个标签，模型
    /// 会以为这句已完整解析，于是不再补写——场景氛围就这么从提示词里消失了。
    /// </summary>
    private static string UnmatchedSuffix(string text, IReadOnlyList<ScanHit> hits)
    {
        var covered = new bool[text.Length];
        var cursor = 0;
        foreach (var h in hits)
        {
            if (string.IsNullOrEmpty(h.Key)) continue;
            var at = text.IndexOf(h.Key, cursor, StringComparison.Ordinal);
            if (at < 0) continue;
            for (var k = at; k < at + h.Key.Length; k++) covered[k] = true;
            cursor = at + h.Key.Length;
        }

        var parts = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && !covered[i];
            if (inside && start < 0) start = i;
            if (!inside && start >= 0)
            {
                // 标点换成空格再收拢，免得报出 `少女，穿` 这种连标点一起的片段；
                // 首尾的虚词（的 / 了 / 上 / 穿）也剪掉，它们不是"概念"。
                var raw = text[start..i];
                start = -1;
                var cleaned = new string(raw.Select(c =>
                    char.IsPunctuation(c) || char.IsSymbol(c) ? ' ' : c).ToArray());
                cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                 .Trim(Particles)
                                 .Trim();
                if (cleaned.Length > 0 && HasContent(cleaned)) parts.Add(cleaned);
            }
        }

        if (parts.Count == 0) return "";
        var shown = string.Join("、", parts.Take(8));
        if (parts.Count > 8) shown += "…";
        return "; unmatched: " + shown;
    }

    /// <summary>只剩标点和虚词的片段不值得报（"的"、"了"、"在"）。</summary>
    private static bool HasContent(string span)
    {
        foreach (var ch in span)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            if (Array.IndexOf(Particles, ch) >= 0) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 虚词与"没有对应标签的常用动词/方位词"。只用来剪掉未匹配片段的边角——
    /// `穿`、`上`、`的` 单独成段报出去，只会让模型以为漏了概念。
    /// </summary>
    private static readonly char[] Particles =
    {
        '的', '了', '着', '在', '和', '与', '把', '被', '给', '对', '从', '向', '到', '于',
        '而', '或', '及', '地', '得', '过', '吗', '呢', '吧', '啊', '呀', '嘛', '很', '都',
        '也', '就', '又', '还', '是', '有', '这', '那', '哪', '我', '你', '他', '她', '它', '们',
        '嗯', '哦', '哈', '咯', '噢',
        '上', '下', '里', '中', '外', '内', '前', '后', '旁', '边', '处', '间',
        '穿', '戴', '拿', '举', '抱', '握', '放', '挂', '站', '坐', '躺', '蹲', '跑', '走',
        '来', '去', '说', '看', '望', '笑', '哭', '想', '让', '使', '做', '用', '变', '起',
        '开', '关', '收', '加', '减', '至', '往', '沿', '顺', '随', '由', '按', '并', '且',
        '但', '却', '因', '为', '所', '以', '如', '若', '虽', '然', '之', '其', '此', '该',
        '每', '各', '某', '些', '什', '么', '怎', '样',
    };

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
        var texts = ReadStringList(arguments, "texts", "text", MaxTexts, out var dropped);
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
        if (dropped > 0)
            sb.Append("note: only ").Append(texts.Count).Append(" of ")
              .Append(texts.Count + dropped).Append(" texts were scanned (cap ")
              .Append(MaxTexts).Append("); ").Append(dropped).Append(" skipped.\n");

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

            // 没匹配上的片段写在**标题行**上：调用方解析时看的就是这一行，
            // 单独一行会被当成无关内容跳过。
            sb.Append("text ").Append(i + 1).Append('/').Append(texts.Count)
              .Append(": ").Append(text).Append(" → ").Append(hits.Count).Append(" tag(s)")
              .Append(UnmatchedSuffix(text, hits)).Append('\n');

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
                sb.Append(" | status=").Append(StatusOfTag(h.Tag, db, SectionOf(h.Tag, db))).Append('\n');
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
    /// 去重（大小写不敏感）并保留顺序；超过 <paramref name="cap"/> 的部分丢掉，
    /// 并通过 <c>out dropped</c> 告诉调用方丢了几条——**静默截断必须避免**：
    /// 调用方否则无法区分"查过但没结果"和"根本没查"。
    /// </summary>
    internal static List<string> ReadStringList(string arguments, string primary, string fallback, int cap)
        => ReadStringList(arguments, primary, fallback, cap, out _);

    /// <summary>同 <see cref="ReadStringList(string,string,string,int)"/>，额外报告被上限丢掉的条数。</summary>
    internal static List<string> ReadStringList(string arguments, string primary, string fallback, int cap,
                                                out int dropped)
    {
        var outp = new List<string>();
        dropped = 0;
        var dirty = 0;   // out 参数不能进本地函数，先记在这里，出口再写回
        var raw = (arguments ?? "").Trim();
        if (raw.Length == 0 || cap <= 0) return outp;

        void Add(string? s)
        {
            var v = (s ?? "").Trim();
            if (v.Length == 0) return;
            if (outp.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) return;
            if (outp.Count >= cap) { dirty++; return; }
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
        finally
        {
            dropped = dirty;
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
