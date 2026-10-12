using System.Globalization;
using System.Text;

namespace AnimaCaptioner.Core;

/// <summary>
/// 搜索窗口的查询与呈现逻辑。
///
/// 分成两个消费者，共用同一次查询：
/// - **界面**：中英混合搜词库，列出标签 + 中文释义 + 大类 + 引用数；
/// - **模型**：把同一份结果压成紧凑文本回灌给工具循环（见 <see cref="ToToolContent"/>）。
///
/// 不引用任何 WinUI 类型，所以能被 net10 回归程序直接编译。
/// </summary>
public static class TagSearch
{
    /// <summary>界面一次最多列这么多。再多也读不完，而每一行都要建控件。</summary>
    public const int UiLimit = 200;

    /// <summary>回灌给模型的条数上限。模型要的是"够选"，不是"全部"。</summary>
    public const int ToolLimit = 25;

    public sealed record Hit(string Tag, string Cn, string Category, int PostCount,
                             bool InAnima, bool Deprecated, string Section)
    {
        public bool HasCn => Cn.Length > 0;
        public string SectionTitle => TagSections.Get(Section).Title;
    }

    /// <summary><paramref name="SplitMatch"/> 表示这次是"整串没命中、按词拆开匹配"的结果。</summary>
    public sealed record QueryResult(List<Hit> Hits, bool SplitMatch);

    /// <summary>
    /// 中英混合查询，只取结果。三件事叠在一起，每一件都是实测逼出来的：
    ///
    /// 1. **下划线写法也查一遍**。词库存的是 Anima 形式（空格），所以搜 `^_^`
    ///    一条都匹配不到——而用户从 Danbooru 那边抄过来的写法恰恰是带下划线的。
    /// 2. **复合中文查询走覆盖兜底**。`白色蕾丝连衣裙` 整串查是 **0 条**（实测），
    ///    拆成「白色/蕾丝/连衣裙」才找得到 `white dress`(46 万帖) 和
    ///    `lace-trimmed dress`。用户和模型都习惯一口气写完，所以这条兜底
    ///    直接决定这个窗口能不能用。
    /// 3. 兜底**只在普通搜索落空时**跑：它要遍历全部 33 万行做十几次 Contains，
    ///    比普通搜索慢一个量级，不能挂在每次按键上。
    ///
    /// 顺序保持 <see cref="VocabDb.Search"/> 给的顺序（分数 → 引用数 → 标签），
    /// 不在这里重排：那套排序是补全和搜索共用的，改它要同时改回归断言。
    /// </summary>
    public static List<Hit> Query(VocabDb? vocab, string q, int limit = UiLimit)
        => QueryDetailed(vocab, q, limit).Hits;

    /// <summary>同 <see cref="Query"/>，但额外告诉调用方这次是不是"按词拆开匹配"的结果。</summary>
    public static QueryResult QueryDetailed(VocabDb? vocab, string q, int limit = UiLimit)
    {
        if (vocab is null || limit <= 0) return new QueryResult(new List<Hit>(), false);
        var query = (q ?? "").Trim();
        if (query.Length == 0) return new QueryResult(new List<Hit>(), false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<Hit>();

        void Add(VocabDb.SearchHit h)
        {
            if (outp.Count >= limit) return;
            if (!seen.Add(h.Tag)) return;
            outp.Add(new Hit(h.Tag, h.Cn, h.Category, h.PostCount, h.InAnima, h.Deprecated,
                             TagSections.Classify(h.Tag, vocab)));
        }

        void Collect(string text)
        {
            foreach (var h in vocab.Search(text, limit)) Add(h);
        }

        Collect(query);

        var spaced = query.Replace('_', ' ');
        if (!string.Equals(spaced, query, StringComparison.Ordinal)) Collect(spaced);

        if (outp.Count > 0) return new QueryResult(outp, false);

        // 覆盖兜底：要求命中的词数过半才算数。
        //
        // 这条门槛是实测定出来的，不是拍脑袋。用 need=1 时，
        // 「量子计算机」会把 `icon (computing)`(639 帖)、`calculator` 这类
        // 只碰到「计算」二字组的标签排在前面——而库里其实只有
        // `quantum protocol`(12 帖) 这种极小众标签，正确答案是"没有"。
        // 过半门槛让那一类查询**如实返回空**，而不是给一堆看似相关的噪声；
        // 而「白色蕾丝连衣裙」（6 个二字组）仍然能命中 `white dress`
        // （cn=白色连衣裙，命中 白色/连衣/衣裙 = 3 个）和 `lace-trimmed dress`。
        var terms = BuildTerms(query);
        if (terms.Count < 2) return new QueryResult(outp, false);

        var need = Math.Max(1, (terms.Count + 1) / 2);
        foreach (var h in vocab.SearchTerms(terms, need, limit)) Add(h);

        return new QueryResult(outp, outp.Count > 0);
    }

    /// <summary>
    /// 把查询拆成搜索词，供覆盖兜底用。
    ///
    /// - ASCII 段按空白与标点切开，整词保留（`white dress` → `white`、`dress`）；
    /// - CJK 连续段取**二字组**：中文词绝大多数是两个字，所以
    ///   `白色蕾丝连衣裙` → 白色/色蕾/蕾丝/丝连/连衣/衣裙。
    ///   其中 `色蕾`、`丝连` 是无意义的组合，但它们匹配不到任何东西，
    ///   覆盖计数会自然把它们忽略——而 `白色`、`蕾丝`、`连衣`、`衣裙` 都命中。
    ///
    /// 上限 16 个词：再多也只是让那一趟扫描更慢，而真正决定排序的是前几个词。
    /// </summary>
    public static List<string> BuildTerms(string? query, int max = 16)
    {
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var s = (query ?? "").Trim();
        if (s.Length == 0) return outp;

        void Add(string t)
        {
            if (t.Length < 2 || outp.Count >= max) return;
            if (seen.Add(t)) outp.Add(t);
        }

        var i = 0;
        while (i < s.Length && outp.Count < max)
        {
            if (VocabDb.IsCjk(s[i]))
            {
                var start = i;
                while (i < s.Length && VocabDb.IsCjk(s[i])) i++;
                var run = s[start..i];
                if (run.Length <= 2) Add(run);
                else for (var k = 0; k + 2 <= run.Length; k++) Add(run.Substring(k, 2));
            }
            else if (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
                Add(s[start..i].Replace('_', ' ').ToLowerInvariant());
            }
            else i++;
        }
        return outp;
    }

    /// <summary>结果行右侧的元信息：大类 · 引用数 · 来源 · 状态。</summary>
    public static string MetaOf(Hit h)
    {
        var parts = new List<string> { h.SectionTitle };
        parts.Add(h.PostCount > 0
            ? h.PostCount.ToString("N0", CultureInfo.InvariantCulture) + " 帖"
            : "无引用");
        if (h.InAnima) parts.Add("Anima");
        if (h.Deprecated) parts.Add("已废弃");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 把结果压成给模型看的文本。
    ///
    /// **格式是被实测改过的。** 第一版是裸的竖线分隔：
    /// <code>hatsune miku|初音未来（VOCALOID）|角色|146,936</code>
    /// 结果 8B **把整行当成了标签名**，回答里写成
    /// `` `hatsune miku|初音未来（VOCALOID）` `` ——它看不出标签名在哪里结束。
    /// 所以现在每个字段都带 <c>名字=</c> 前缀，标签名是 <c>tag=</c> 到下一个
    /// <c> | </c> 之间的那一段，边界明确。系统提示里也照这个说法写了一遍。
    ///
    /// 三条约束：
    /// 1. **标签原样输出**，不做任何改写——它就是词库里的规范形式，模型照抄即可。
    /// 2. **带上引用数**，模型要能一眼看出哪个标签更常用（实测它会引用这个数）。
    /// 3. 结尾必须写清还剩几次搜索。小模型不知道预算时会把轮数耗光。
    /// </summary>
    public static string ToToolContent(IReadOnlyList<Hit> hits, string query, int remaining,
                                       bool splitMatch = false)
    {
        var sb = new StringBuilder();
        sb.Append("query: ").Append(query).Append('\n');

        if (hits.Count == 0)
        {
            sb.Append("no tags matched this query. Try fewer or more general words.\n");
        }
        else
        {
            if (splitMatch)
                sb.Append("note: the query as a whole matched nothing, so it was split into ")
                  .Append("separate words and these tags matched most of them. They are only ")
                  .Append("partly related - prefer the ones whose cn= reads closest to what ")
                  .Append("was asked for.\n");
            sb.Append("matched ").Append(hits.Count)
              .Append(" tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | ")
              .Append("section=<category> | posts=<usage count>\n");
            for (var i = 0; i < hits.Count; i++)
            {
                var h = hits[i];
                sb.Append(i + 1).Append(". tag=").Append(h.Tag)
                  .Append(" | cn=").Append(h.HasCn ? h.Cn : "-")
                  .Append(" | section=").Append(h.SectionTitle)
                  .Append(" | posts=").Append(h.PostCount.ToString("N0", CultureInfo.InvariantCulture))
                  .Append('\n');
            }
        }

        sb.Append("searches remaining: ").Append(Math.Max(0, remaining));
        return sb.ToString();
    }

    /// <summary>
    /// 从工具结果里挑出"值得给用户点"的标签：按引用数降序。
    ///
    /// 模型自己写出来的标签不算数——那是它可能编的。可点列表一律来自
    /// **工具实际返回的行**，所以点一下必然加进去一个真实存在的标签。
    /// </summary>
    public static List<Hit> RankForUser(IEnumerable<Hit> found, int limit = 60)
        => found
            .GroupBy(h => h.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(h => h.InAnima)
            .ThenByDescending(h => h.PostCount)
            .ThenBy(h => h.Tag, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

    // ---------------- 模型回答的存在性核对 ----------------

    /// <summary>
    /// 取出回答里反引号包起来的片段。系统提示要求模型把标签名放进反引号，
    /// 所以这是"它明确声称的标签"最可靠的来源——比从散文里猜词形可靠得多。
    /// 没有成对的引号就返回空表（此时不做核对，而不是乱猜）。
    /// </summary>
    public static List<string> BacktickedTags(string? answer)
    {
        var outp = new List<string>();
        var s = answer ?? "";
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf('`', i);
            if (open < 0) break;
            var close = s.IndexOf('`', open + 1);
            if (close < 0) break;

            var inner = s.Substring(open + 1, close - open - 1).Trim();
            if (inner.Length > 0) outp.Add(inner);
            i = close + 1;
        }
        return outp;
    }

    /// <summary>
    /// 模型提到、但词库里查不到的标签名。
    ///
    /// 判据与「校验」走同一条路（<see cref="VocabDb.ResolveEnglish"/> 不行再试
    /// <see cref="VocabDb.ResolveChinese"/>），并且跳过控制词与风格触发器——
    /// 那些本来就合法但不在标签表里（`best quality`、`score_7`、`@xxx style`），
    /// 不跳过的话每次都会误报。
    ///
    /// 只用来**如实提示**，不阻止任何操作：词库不是全集，模型偶尔提到一个
    /// 库外但真实存在的标签也是可能的。
    /// </summary>
    public static List<string> SuspiciousMentions(string? answer, VocabDb? vocab)
    {
        var outp = new List<string>();
        if (vocab is null) return outp;

        foreach (var t in BacktickedTags(answer))
        {
            if (t.Length == 0 || t.Length > 64) continue;
            if (VocabDb.IsControlToken(t) || VocabDb.IsStyleTrigger(t)) continue;
            if (vocab.ResolveEnglish(t).Tag is not null) continue;
            if (vocab.ResolveChinese(t).Tag is not null) continue;
            if (!outp.Contains(t, StringComparer.OrdinalIgnoreCase)) outp.Add(t);
        }
        return outp;
    }
}
