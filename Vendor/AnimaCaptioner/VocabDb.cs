using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AnimaCaptioner.Core;

/// <summary>词库里的一行。</summary>
public sealed class VocabRow
{
    public string Tag = "";        // Anima 形式（空格、画师带 @）
    public string Danbooru = "";   // Danbooru 原始形式
    public string Cn = "";         // 主中文名
    public string Aliases = "";    // 额外中文键，竖线分隔（仅供搜索展示）
    public string Category = "";
    public int PostCount;
    public bool Deprecated;
    public bool InAnima;
}

public readonly record struct ScanHit(string Tag, string Status, string Key);
public readonly record struct ResolveResult(string? Tag, string Status);

/// <summary>
/// 词库引擎。这是 animatag.py 的 C# 移植，行为必须与那个已验证的 Python 实现一致
/// （tools\parity 里有对拍脚本）。三条不能走样的规则：
///
/// 1. Anima 有自己的标签集，不是 Danbooru 的子集。`see-through`、`topless`、
///    `ass grab` 在 Danbooru 上不存在或是废弃名，却都是合法 Anima 标签。
///    所以 anima_index 的命中优先于 Danbooru 的废弃标记。
/// 2. 模糊匹配必须单向（只允许「词库键出现在用户输入里」）。双向匹配会让
///    10 万个角色名的中文把普通词吞掉（实测 `少女` -> 某个光之美少女角色）。
/// 3. 扫描用最优切分而不是贪婪最长匹配。贪婪在 `回头看向镜头` 上会取
///    `回头看`，剩下 `向镜头`，导致正确的 `看向镜头` 永远匹配不到。
/// </summary>
public sealed class VocabDb
{
    private static readonly HashSet<string> JunkCategories =
        new(StringComparer.Ordinal) { "artist", "character", "copyright", "原神角色" };

    private readonly Dictionary<string, VocabRow> _byTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VocabRow> _byDanbooru = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VocabRow> _cnExact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _anima = new(StringComparer.Ordinal);

    /// <summary>Anima 索引里每个标签的大类（general/meta/artist/character/copyright）。</summary>
    private readonly Dictionary<string, string> _animaCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _seed = new(StringComparer.Ordinal);

    /// <summary>Anima 原生但**不在 best 表里**的标签。见 <see cref="AnimaOnly"/> 的说明。</summary>
    public readonly record struct AnimaEntry(string Name, int Posts, string Category);

    /// <summary>
    /// Anima 自带索引里有、合并表（best）里没有的标签，约 12,125 个。
    ///
    /// 这一批必须单独留着，否则**搜索窗口看不到它们**——而其中不乏用户真正要用的：
    /// `female focus`(88 万帖)、`10s`(75 万)、`black footwear`(28 万)、
    /// `see-through`(16 万)、`wink`(10.6 万)、`topless`(10.6 万)、
    /// `cat smile`、`ass grab`(5.9 万)、`vertical stripes`(3.4 万)。
    ///
    /// 成因是数据源不同：best 来自 Danbooru 中英对照表，而 Anima 用的是自己那份
    /// 标签索引（作者在 Danbooru 之外另立了一批类别词，如 `black footwear`
    /// 对应 Danbooru 的 `black footwear` 根本不存在、`topless` 在 Danbooru 已被拆分）。
    ///
    /// 不补这一批会自相矛盾：程序里 `Status()` / `ResolveEnglish()` / `AuditTags()`
    /// 都认这些标签（走 `_anima`），「校验」不会报它们，但搜索就是搜不出来。
    /// </summary>
    private readonly List<AnimaEntry> _animaOnly = new();

    /// <summary>Anima 原生但不在合并表里的标签数（供状态栏/诊断）。</summary>
    public int AnimaOnlyCount => _animaOnly.Count;

    /// <summary>中文键，按长度降序（再按序数升序保证确定性）。</summary>
    private string[] _cnKeys = Array.Empty<string>();

    /// <summary>_cnKeys 的集合视图。Scan 每帧都要判存在，逐个新建会白白复制 32 万个字符串。</summary>
    private HashSet<string> _cnKeySet = new(StringComparer.Ordinal);

    private int _maxKey;

    public int TagCount => _byTag.Count;
    public int AnimaCount => _anima.Count;
    public int AliasCount => _alias.Count;
    public int SeedCount => _seed.Count;
    public int CnKeyCount => _cnKeys.Length;

    public static string Describe(string dbPath)
    {
        if (!File.Exists(dbPath)) return "词库不存在：" + dbPath;
        var mb = new FileInfo(dbPath).Length / 1024.0 / 1024.0;
        return $"{Path.GetFileName(dbPath)}（{mb:F1} MB）";
    }

    public void Load(string dbPath, string seedPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("词库数据库不存在：" + dbPath, dbPath);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var con = new SqliteConnection(cs);
        con.Open();

        // best：权威标签表。保持 Python 的 setdefault 语义——先到者胜，
        // 所以这里按 rowid 顺序读，不做 ORDER BY（加了反而改变优先级）。
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText =
                "SELECT tag, danbooru, cn, aliases, category, post_count, deprecated, in_anima FROM best";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new VocabRow
                {
                    Tag = r.IsDBNull(0) ? "" : r.GetString(0),
                    Danbooru = r.IsDBNull(1) ? "" : r.GetString(1),
                    Cn = r.IsDBNull(2) ? "" : r.GetString(2),
                    Aliases = r.IsDBNull(3) ? "" : r.GetString(3),
                    Category = r.IsDBNull(4) ? "" : r.GetString(4),
                    PostCount = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    Deprecated = !r.IsDBNull(6) && r.GetInt32(6) != 0,
                    InAnima = !r.IsDBNull(7) && r.GetInt32(7) != 0
                };
                var tl = row.Tag.ToLowerInvariant();
                if (tl.Length > 0) _byTag.TryAdd(tl, row);
                if (row.Danbooru.Length > 0) _byDanbooru.TryAdd(row.Danbooru.ToLowerInvariant(), row);
                if (row.Cn.Length > 0) _cnExact.TryAdd(row.Cn, row);
            }
        }

        // anima_index：Anima 自己的标签索引，命中即有效
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT name, post_count, category FROM anima_index";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var n = r.IsDBNull(0) ? "" : r.GetString(0);
                if (n.Length == 0) continue;
                var low = n.ToLowerInvariant();
                _anima.TryAdd(low, r.IsDBNull(1) ? 0 : r.GetInt32(1));
                var cat = r.IsDBNull(2) ? "" : r.GetString(2);
                if (cat.Length > 0) _animaCategory.TryAdd(low, cat);
            }
        }
        catch (SqliteException ex)
        {
            Log.Write("anima_index 缺失（旧版词库？）：" + ex.Message);
        }

        // alias：Danbooru 的活跃改名表
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT alias, raw, tag FROM alias";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var a = r.IsDBNull(0) ? "" : r.GetString(0);
                var raw = r.IsDBNull(1) ? "" : r.GetString(1);
                var t = r.IsDBNull(2) ? "" : r.GetString(2);
                if (a.Length > 0) _alias.TryAdd(a.ToLowerInvariant(), t);
                if (raw.Length > 0) _alias.TryAdd(raw.ToLowerInvariant(), t);
            }
        }
        catch (SqliteException ex)
        {
            Log.Write("alias 缺失：" + ex.Message);
        }

        LoadSeed(seedPath);
        BuildCnKeys();
        BuildAnimaOnly();

        Log.Write($"vocab loaded: tags={_byTag.Count} anima={_anima.Count} alias={_alias.Count} " +
                  $"cn={_cnExact.Count} seed={_seed.Count} keys={_cnKeys.Length} " +
                  $"anima-only={_animaOnly.Count}");
    }

    /// <summary>
    /// 挑出 Anima 索引里有、合并表里没有的标签。**必须在 best 和 anima_index
    /// 都读完、cnKeys 建好之后**再跑：它同时依赖 `_byTag` 和 `_anima`。
    /// 一次算好存起来，搜索时就不用每次遍历 10.8 万条 anima_index。
    /// </summary>
    private void BuildAnimaOnly()
    {
        foreach (var kv in _anima)
        {
            if (_byTag.ContainsKey(kv.Key)) continue;
            var cat = _animaCategory.TryGetValue(kv.Key, out var c) ? c : "";
            _animaOnly.Add(new AnimaEntry(kv.Key, kv.Value, cat));
        }
        // 引用数降序：搜索是按引用数裁决平局的，预先排好可以让"只靠包含命中"
        // 的那一档早点截断（Search 会 Take(limit)，但排序在最后）。
        _animaOnly.Sort((a, b) => b.Posts.CompareTo(a.Posts));
    }

    private void LoadSeed(string seedPath)
    {
        if (!File.Exists(seedPath))
        {
            Log.Write("seed 不存在，跳过：" + seedPath);
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(seedPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("map", out var map) &&
                map.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in map.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        _seed.TryAdd(p.Name, p.Value.GetString()!);
            }
        }
        catch (Exception ex)
        {
            Log.Write("seed 解析失败：" + ex.Message);
        }
    }

    private void BuildCnKeys()
    {
        // 与 Python 一致：cn_exact 的键 ∪ seed 的键
        var set = new HashSet<string>(_cnExact.Keys, StringComparer.Ordinal);
        set.UnionWith(_seed.Keys);
        var arr = new string[set.Count];
        set.CopyTo(arr);
        // Python 用 key=len 降序；等长时它依赖 set 迭代顺序（不确定）。
        // 这里补一个序数升序做平局裁决，让结果可复现。
        Array.Sort(arr, (a, b) =>
        {
            var c = b.Length.CompareTo(a.Length);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });
        _cnKeys = arr;
        _cnKeySet = new HashSet<string>(arr, StringComparer.Ordinal);
        _maxKey = 1;
        foreach (var k in arr) if (k.Length > _maxKey) _maxKey = k.Length;
    }

    /// <summary>
    /// 是否是 CJK 字符。公开出来是为了让切词（<see cref="TagSearch.BuildTerms"/>）
    /// 和这里的判断用同一套范围——两处各写一份迟早会不一致。
    /// </summary>
    public static bool IsCjk(char ch) =>
        (ch >= 0x2E80 && ch <= 0x9FFF) ||    // 部首扩展 ~ 中日韩统一表意
        (ch >= 0xF900 && ch <= 0xFAFF) ||    // 兼容表意
        (ch >= 0xFF00 && ch <= 0xFFEF);      // 全角

    /// <summary>是否含 CJK 字符。纯 ASCII 的串里不可能包含中文键，先挡掉能省下整轮 32 万次比较。</summary>
    private static bool HasCjk(string s)
    {
        foreach (var ch in s) if (IsCjk(ch)) return true;
        return false;
    }

    // ---------------- 解析 ----------------

    /// <summary>
    /// 标签在词库里的类别字符串，供 <see cref="TagSections.Classify"/> 归类用。
    ///
    /// 顺序与 <see cref="Status"/> 一致：Anima 自带索引优先。这不是吹毛求疵——
    /// `1girl`、`solo` 在 Anima 索引里是 general，在合并表里被中文包标成了
    /// 「人物数量」；两边对"人物数量"这种官方独立段的归属并不一致，以 Anima
    /// 自己的索引为准才不会把控制词塞进 general 段。
    /// 查不到返回空串。
    /// </summary>
    public string CategoryOf(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var low = tag.Trim().ToLowerInvariant();
        if (low.StartsWith('@')) low = low[1..];

        if (_animaCategory.TryGetValue(low, out var ac) && ac.Length > 0) return ac;
        if (_byTag.TryGetValue(low, out var r) && r.Category.Length > 0) return r.Category;
        return "";
    }

    /// <summary>'anima' | 'valid' | 'retired' | 'unknown'</summary>
    public string Status(string tag)
    {
        var low = tag.ToLowerInvariant();
        if (_anima.ContainsKey(low)) return "anima";
        if (!_byTag.TryGetValue(low, out var r)) return "unknown";
        if (r.PostCount == 0) return "unknown";
        return r.Deprecated ? "retired" : "valid";
    }

    /// <summary>英文标签 → 规范形式。</summary>
    public ResolveResult ResolveEnglish(string q)
    {
        q = q.Trim().Trim('.', ',', ';');
        if (q.Length == 0) return new ResolveResult(null, "empty");
        var low = q.ToLowerInvariant();

        // Anima 索引是权威。Danbooru 会把 `see-through` 重定向到
        // `see-through clothes`，但 Anima 自带 `see-through`（160,050 帖）。
        // 先查别名表会把这两个都改错。
        if (_anima.ContainsKey(low)) return new ResolveResult(low, "anima");
        if (_alias.TryGetValue(low, out var aliasTarget)) return new ResolveResult(aliasTarget, "alias");

        foreach (var (cand, note) in new[] { (low, "ok"), (low.Replace('_', ' '), "underscore-fixed") })
        {
            if (_byTag.TryGetValue(cand, out var r))
            {
                if (r.InAnima) return new ResolveResult(r.Tag, "anima");
                if (r.PostCount == 0) return new ResolveResult(r.Tag, "no-usage");
                if (r.Deprecated) return new ResolveResult(r.Tag, "retired");
                return new ResolveResult(r.Tag, note);
            }
        }
        if (_byDanbooru.TryGetValue(low, out var d)) return new ResolveResult(d.Tag, "underscore-fixed");
        return new ResolveResult(null, "unknown");
    }

    /// <summary>中文关键词 → 标签。</summary>
    public ResolveResult ResolveChinese(string kw, double minRatio = 0.5)
    {
        kw = kw.Trim();
        if (kw.Length == 0) return new ResolveResult(null, "empty");

        // 1. 人工整理的种子优先：它存在的意义正是补大表没有精确中文键的地方
        if (_seed.TryGetValue(kw, out var seedTag))
            return new ResolveResult(seedTag, Status(seedTag) != "unknown" ? "seed" : "seed-unverified");

        // 2. 精确中文键
        if (_cnExact.TryGetValue(kw, out var r))
        {
            if (JunkCategories.Contains(r.Category)) return new ResolveResult(null, "wrong-category");
            if (r.InAnima) return new ResolveResult(r.Tag, "exact");
            return new ResolveResult(r.Tag, r.Deprecated ? "retired" : "exact");
        }

        // 3. 包含匹配，只允许「词库键在用户关键词内」。反向匹配正是
        //    产生 `少女` -> 某个光之美少女角色的原因。
        //    纯 ASCII 输入不可能含中文键，先挡掉，省下整轮 32 万次比较
        //    （审计英文 caption 时每个标签都会走到这里）。
        if (!HasCjk(kw)) return new ResolveResult(null, "no-match");

        foreach (var k in _cnKeys)
        {
            if (k.Length > kw.Length) continue;
            if ((double)k.Length / kw.Length < minRatio) continue;
            if (kw.Contains(k, StringComparison.Ordinal))
            {
                var (t, st) = ResolveChinese(k, minRatio);
                if (t is not null)
                    return new ResolveResult(t, st.StartsWith("seed", StringComparison.Ordinal) ? st : "fuzzy");
            }
        }
        return new ResolveResult(null, "no-match");
    }

    // ---------------- 扫描（最优切分） ----------------

    /// <summary>
    /// 中文描述 → 标签，不调用模型。动态规划而非贪婪：
    /// 评分 = (覆盖字数, -用到的标签数) 取最大，即尽量多覆盖原文，
    /// 平局时偏向更少更长的标签，使 `跪坐` 保持一个标签而不是裂成 `跪`+`坐`。
    /// </summary>
    public List<ScanHit> Scan(string text)
    {
        var keys = _cnKeySet;   // 不在每帧复制 32 万个字符串
        var n = text.Length;
        var cov = new int[n + 1];
        var cnt = new int[n + 1];
        var pick = new (string Span, int Len, string Tag, string Status)?[n + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            var bc = cov[i + 1];
            var bn = cnt[i + 1];
            (string, int, string, string)? choice = null;

            var maxL = Math.Min(_maxKey, n - i);
            for (var L = maxL; L >= 1; L--)
            {
                var span = text.Substring(i, L);
                if (!keys.Contains(span)) continue;
                var (t, st) = ResolveChinese(span);
                if (t is null) continue;
                var c = L + cov[i + L];
                var k = 1 + cnt[i + L];
                if (c > bc || (c == bc && k < bn))
                {
                    bc = c; bn = k; choice = (span, L, t, st);
                }
            }
            cov[i] = bc; cnt[i] = bn; pick[i] = choice;
        }

        var outp = new List<ScanHit>();
        var pos = 0;
        while (pos < n)
        {
            var p = pick[pos];
            if (p is { } v)
            {
                outp.Add(new ScanHit(v.Tag, v.Status, v.Span));
                pos += v.Len;
            }
            else pos++;
        }
        return outp;
    }

    // ---------------- 排序 ----------------

    /*
     * 头部控制词的**分类**不再在这里定义，而是向 TagSections 要——那段方括号里的
     * 四类（quality/meta/year/safety）加数量段，一共五个"头部段"，它们的成员表和
     * 段间次序都由 TagSections.All 决定。
     *
     * 原先这里另抄了一份 HeadGroups，结果是两套分类各自漂移：这一份压根没有
     * 元信息那一组，于是 highres / absurdres 找不到归属、掉进 body 被按热度
     * 排进一般标签里。现在分类只有一份真源，那边加一个词这边自动跟上。
     *
     * 仍然属于本类的只有**段内策略**：Arrange 按 prior（训练集里的平均相对位置）
     * 重排 body，而界面用的 TagSections.Canonicalize 刻意保留人工段内顺序。
     * 两者服务不同场景（生图从零组装 vs 标注改已有文件），所以这个差异是
     * 有意保留的，不是重复实现。
     */

    /// <summary>安全词表与 TagSections 同源，只额外认 Danbooru 的写法。</summary>
    public static readonly string[] Ratings = TagSections.SafetyWords;

    public static List<string> Arrange(IEnumerable<string> tags, IReadOnlyDictionary<string, double> prior,
                                       string? rating = null, string? artist = null)
    {
        var head = new List<string>();
        var artists = new List<string>();
        var body = new List<string>();

        foreach (var t in tags)
        {
            if (t.Length == 0) continue;
            // 分类统一向 TagSections 要：头部五个段（质量/元信息/年代/安全/数量）
            // 的成员表只有那一份定义，这里不再另抄一套。
            if (TagSections.TryHeadSection(t, out _, out _)) head.Add(t);
            else if (t.StartsWith('@')) artists.Add(t);
            else body.Add(t);
        }

        // 头部次序 = 段序 * 1000 + 段内次序。段内 order 不会到 1000
        // （最大是 year 2200：PeriodWords.Length 6 + (2200-1900) = 306），
        // 所以段与段之间不会串位。
        int HeadKey(string t)
        {
            if (!TagSections.TryHeadSection(t, out var sec, out var ord)) return 99000;
            var gi = Array.IndexOf(TagSections.HeadKeys, sec);
            return gi < 0 ? 99000 : gi * 1000 + ord;
        }

        head.Sort((a, b) => HeadKey(a).CompareTo(HeadKey(b)));
        artists.Sort(StringComparer.Ordinal);
        body.Sort((a, b) =>
        {
            var pa = prior.TryGetValue(a, out var va) ? va : 9.0;
            var pb = prior.TryGetValue(b, out var vb) ? vb : 9.0;
            var c = pa.CompareTo(pb);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });

        var outp = new List<string>();
        if (!string.IsNullOrWhiteSpace(rating)) outp.Add(rating!);
        outp.AddRange(head);
        if (!string.IsNullOrWhiteSpace(artist)) outp.Add(artist!);
        outp.AddRange(artists);
        outp.AddRange(body);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dedup = new List<string>();
        foreach (var t in outp)
        {
            if (t.Length == 0) continue;
            if (seen.Add(t.ToLowerInvariant())) dedup.Add(t);
        }
        return dedup;
    }

    // ---------------- 审计与搜索 ----------------

    /// <summary>控制词与风格触发器：在 caption 里合法但不是 Danbooru 标签。</summary>
    private static readonly HashSet<string> ControlTokens = BuildControlTokens();

    private static HashSet<string> BuildControlTokens()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "safe", "sensitive", "questionable", "explicit", "nsfw",
            "masterpiece", "best quality", "good quality", "normal quality",
            "worst quality", "low quality", "very aesthetic", "highres", "absurdres",
            "newest", "recent", "mid", "old", "oldest",
            "1girl", "2girls", "3girls", "4girls", "5girls", "6+girls", "multiple girls",
            "1boy", "2boys", "multiple boys", "solo", "solo focus", "male focus",
            "other focus", "no humans"
        };
        for (var i = 1; i <= 9; i++) s.Add($"score_{i}");
        for (var y = 2015; y <= 2026; y++) s.Add($"year {y}");
        return s;
    }

    public static bool IsStyleTrigger(string t)
    {
        // 形如 "@<name> style"，名字故意不是 Danbooru 画师
        if (!t.StartsWith('@')) return false;
        var low = t.ToLowerInvariant();
        if (!low.EndsWith(" style", StringComparison.Ordinal)) return false;
        return t.Length > 7;
    }

    public static bool IsControlToken(string t) => ControlTokens.Contains(t);

    public sealed record AuditIssue(string Tag, string Status, string? Use);

    public static List<AuditIssue> AuditTags(IEnumerable<string> tags, VocabDb vocab)
    {
        var issues = new List<AuditIssue>();
        foreach (var t in tags)
        {
            if (t.Length == 0) continue;
            if (ControlTokens.Contains(t) || IsStyleTrigger(t)) continue;
            var (canon, st) = vocab.ResolveEnglish(t);
            if (canon is null)
            {
                var (cn, st2) = vocab.ResolveChinese(t);
                issues.Add(new AuditIssue(t, cn is null ? "not-a-tag" : st2, cn));
            }
            else if (!string.Equals(canon, t, StringComparison.OrdinalIgnoreCase))
                issues.Add(new AuditIssue(t, st, canon));
            else if (st is "retired" or "no-usage" or "unknown")
                issues.Add(new AuditIssue(t, st, null));
            else if (st == "underscore-fixed")
                issues.Add(new AuditIssue(t, "underscore", canon));
        }
        return issues;
    }

    /// <summary>
    /// 取一个已规范化标签的中文释义。UI 每帧要给几十行标签配释义，
    /// 走 <see cref="Search"/> 会每次扫全部 33 万行，必须用直接查表。
    /// </summary>
    public string LookupCn(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var low = tag.Trim().ToLowerInvariant();

        // 画师标签在表里存的是带 @ 的形式，去掉 @ 再试一次
        var bare = low.StartsWith('@') ? low[1..] : null;

        if (_byTag.TryGetValue(low, out var r) && r.Cn.Length > 0) return r.Cn;
        if (bare is not null && _byTag.TryGetValue(bare, out var r2) && r2.Cn.Length > 0) return r2.Cn;
        if (_alias.TryGetValue(low, out var target) && _byTag.TryGetValue(target.ToLowerInvariant(), out var r3))
            return r3.Cn;
        return "";
    }

    public sealed record SearchHit(string Tag, string Cn, string Category, int PostCount,
                                   bool InAnima, bool Deprecated);

    /// <summary>
    /// 中英混合搜索，供 UI 的补全/查找使用。
    ///
    /// 扫两个来源：合并表 <c>best</c>（33 万条，带中文释义）和
    /// <see cref="_animaOnly"/>（1.2 万条 Anima 原生标签，没有中文释义）。
    /// 只扫前者会让搜索窗口看不见 `female focus`、`black footwear`、`topless`、
    /// `cat smile` 这类 Anima 真正要用的标签，而程序其他地方（校验、解析）
    /// 都认它们——那种"校验说没问题、搜索却搜不到"的不一致比缺功能更难查。
    /// </summary>
    public List<SearchHit> Search(string q, int limit = 60)
    {
        q = q.Trim();
        if (q.Length == 0) return new List<SearchHit>();
        var low = q.ToLowerInvariant();
        var hits = new List<(int Score, SearchHit Hit)>();

        void Consider(string tag, string cn, string aliases, string category,
                      int posts, bool inAnima, bool deprecated)
        {
            int score;
            var tl = tag.ToLowerInvariant();
            if (tl == low || (cn.Length > 0 && cn == q)) score = 0;
            else if (tl.StartsWith(low, StringComparison.Ordinal)) score = 1;
            else if (cn.Length > 0 && cn.StartsWith(q, StringComparison.Ordinal)) score = 2;
            else if (tl.Contains(low, StringComparison.Ordinal)) score = 3;
            else if (cn.Length > 0 && cn.Contains(q, StringComparison.Ordinal)) score = 4;
            else if (aliases.Length > 0 && aliases.Contains(q, StringComparison.Ordinal)) score = 5;
            else return;

            // 废弃/无引用的排在后面并标出来，但保留可见性。
            //
            // **必须同时判 Anima 归属**，否则就和本类的头号规则自相矛盾：
            // Anima 自己的索引优先于 Danbooru 的废弃标记（`ResolveEnglish`、
            // `Status`、`AuditTags` 都是这么做的）。实测 `uniform`(Anima 15.9 万帖)、
            // `presenting`(3.4 万)、`music`(2.5 万)、`thigh grab`(1 万) 都是
            // best 里标了 deprecated、同时 in_anima=1 的标签——不判 InAnima 就惩罚，
            // 会把它们压到 `uniform vest` 这种前缀匹配后面，于是"校验说没问题、
            // 搜索却把它排在别人后面"。
            if (!inAnima && (deprecated || posts == 0)) score += 10;
            hits.Add((score, new SearchHit(tag, cn, category, posts, inAnima, deprecated)));
        }

        foreach (var r in _byTag.Values)
            Consider(r.Tag, r.Cn, r.Aliases, r.Category, r.PostCount, r.InAnima, r.Deprecated);

        foreach (var a in _animaOnly)
            Consider(a.Name, "", "", a.Category, a.Posts, true, false);

        return hits
            .OrderBy(h => h.Score)
            .ThenByDescending(h => h.Hit.PostCount)
            .ThenBy(h => h.Hit.Tag, StringComparer.Ordinal)
            .Take(limit)
            .Select(h => h.Hit)
            .ToList();
    }

    /// <summary>
    /// 多词覆盖搜索：计分 = 命中词数，再按 Anima 归属与引用数。
    ///
    /// 给**复合查询**兜底用。<see cref="Search"/> 是整串子串匹配，所以
    /// 「白色蕾丝连衣裙」这种一口气写完的中文**一条都命中不了**（实测 0 条）——
    /// 而它拆开是「白色 / 蕾丝 / 连衣裙」三个词，对应 `white dress`（46 万帖）
    /// 和 `lace`（5 万帖）两个真实标签。模型和用户都习惯这么问，
    /// 所以这条兜底不是锦上添花。
    ///
    /// 调用方负责切词（见 <see cref="TagSearch.BuildTerms"/>）并只在普通搜索
    /// 落空时才调用——这一趟要比 <see cref="Search"/> 慢一个量级。
    ///
    /// <paramref name="minHits"/> 是"至少命中几个词才算候选"。调用方按词数过半来定，
    /// 理由见 <see cref="TagSearch.QueryDetailed"/>：门槛放到 1 会让库里根本没有的
    /// 查询返回一堆看似相关的噪声。
    /// </summary>
    public List<SearchHit> SearchTerms(IReadOnlyList<string> terms, int minHits = 1, int limit = 60)
    {
        var outp = new List<(int Score, SearchHit Hit)>();
        if (terms.Count == 0) return new List<SearchHit>();
        var need = Math.Clamp(minHits, 1, terms.Count);

        foreach (var r in _byTag.Values)
        {
            var n = 0;
            foreach (var t in terms)
            {
                if (r.Tag.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    (r.Cn.Length > 0 && r.Cn.Contains(t, StringComparison.Ordinal)) ||
                    (r.Aliases.Length > 0 && r.Aliases.Contains(t, StringComparison.Ordinal)))
                    n++;
            }
            if (n < need) continue;
            outp.Add((n, new SearchHit(r.Tag, r.Cn, r.Category, r.PostCount, r.InAnima, r.Deprecated)));
        }

        // 与 Search 同理：Anima 原生标签也要参与，否则 `black footwear` 这类
        // 复合查询会漏掉它们。它们没有中文释义，只能拿标签名本身比对。
        foreach (var a in _animaOnly)
        {
            var n = 0;
            foreach (var t in terms)
                if (a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)) n++;
            if (n < need) continue;
            outp.Add((n, new SearchHit(a.Name, "", a.Category, a.Posts, true, false)));
        }

        return outp
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Hit.InAnima)
            .ThenByDescending(x => x.Hit.PostCount)
            .ThenBy(x => x.Hit.Tag, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Hit)
            .ToList();
    }

    /// <summary>从训练集目录反推排序先验：每个标签在各 caption 中的平均相对位置。</summary>
    public static Dictionary<string, double> OrderFromDir(string dir)
    {
        var acc = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return new Dictionary<string, double>();

        foreach (var f in Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8); }
            catch { continue; }
            var tags = CaptionFile.SplitTags(text);
            if (tags.Count == 0) continue;
            for (var i = 0; i < tags.Count; i++)
            {
                if (!acc.TryGetValue(tags[i], out var list)) acc[tags[i]] = list = new List<double>();
                list.Add((double)i / tags.Count);
            }
        }
        return acc.ToDictionary(kv => kv.Key, kv => kv.Value.Average(), StringComparer.Ordinal);
    }

    // ---------------- 散文词汇表 ----------------

    /// <summary>
    /// 人称与指代词。这些词**在整个词库里都没有对应标签**（实测：`少女`、`少年`、
    /// `女孩`、`男孩`、`女人`、`男人`、`人物` 在合并表和人工映射里全都查不到，
    /// 因为它们对应的标签是 `1girl` 这类数量词，不是词对词的翻译）。
    ///
    /// 后果很具体：把中文描述交给模型翻成英文散文时，模型会把没有译法的词原样留下，
    /// 实测稳定产出 `A silver-haired少女 stands…`。而 Anima 的 T5 词表里
    /// 32100 个 piece 的 CJK 数量是 **0**，中文整段会塌成 &lt;unk&gt;，那句话等于没写。
    ///
    /// 实测对照（同样 4 段中文、同一个 8B）：
    ///   不给词汇表         -> 2/4 段留下 `少女`
    ///   给这份词汇表        -> **0/4**
    /// 所以这里补上一份"英文单词"级别的对照，只用于散文翻译，不参与标签解析
    /// （`girl` 不是 Anima 标签，正确的标签是 `1girl`，两者不能混）。
    /// </summary>
    private static readonly (string Cn, string En)[] PersonWords =
    {
        ("\u5c11\u5973", "girl"),        // 少女
        ("\u5c11\u5e74", "boy"),         // 少年
        ("\u5973\u5b69", "girl"),        // 女孩
        ("\u7537\u5b69", "boy"),         // 男孩
        ("\u5973\u4eba", "woman"),       // 女人
        ("\u7537\u4eba", "man"),         // 男人
        ("\u5973\u6027", "female"),      // 女性
        ("\u7537\u6027", "male"),        // 男性
        ("\u4eba\u7269", "figure"),      // 人物
        ("\u4eba\u7fa4", "crowd"),       // 人群
        ("\u732b\u8033\u5c11\u5973", "catgirl"),   // 猫耳少女
        ("\u72d0\u5a18", "fox girl"),    // 狐娘
        ("\u4eba\u9c7c", "mermaid"),     // 人鱼
        ("\u5973\u4ec6", "maid"),        // 女仆
        ("\u7537\u4ec6", "butler"),      // 男仆
        ("\u8001\u4eba", "old person"),  // 老人
        ("\u5b69\u5b50", "child"),       // 孩子
        ("\u5a74\u513f", "baby"),        // 婴儿
    };

    /// <summary>
    /// 给"中文 → 英文散文"用的词汇表：中文字词 → 该用的英文单词。
    ///
    /// 两个来源合并：
    /// 1. 词库扫描命中（<see cref="Scan"/> 的中文键 → 规范标签），这是主体；
    /// 2. <see cref="PersonWords"/>——人称词，词库里根本没有，但不给译法模型就会
    ///    把中文原样留下。
    ///
    /// 长键优先（"猫耳少女" 要先于 "少女"），最多 <paramref name="max"/> 条，
    /// 免得把提示词堆得太长。
    /// </summary>
    public List<(string Cn, string En)> ProseGlossary(string text, int max = 14)
    {
        var outp = new List<(string Cn, string En)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(text))
        {
            foreach (var h in Scan(text))
            {
                if (h.Key.Length < 2) continue;
                if (seen.Add(h.Key)) outp.Add((h.Key, h.Tag));
            }
            foreach (var (cn, en) in PersonWords)
            {
                if (text.Contains(cn, StringComparison.Ordinal) && seen.Add(cn))
                    outp.Add((cn, en));
            }
        }

        return outp
            .OrderByDescending(p => p.Cn.Length)
            .Take(max)
            .ToList();
    }
}
