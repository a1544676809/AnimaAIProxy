using System.Globalization;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一个大类的定义。
/// </summary>
public sealed class TagSectionInfo
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string DefaultHex { get; init; }
    public required int Index { get; init; }
}

/// <summary>
/// Anima 的标签大类。官方模型卡（circlestone-labs/Anima，Prompting → Tag order）:
///
///   [quality/meta/year/safety tags] [1girl/1boy/1other etc] [character]
///   [series] [artist] [general tags]
///
///   "Within each tag section, the tags can be in arbitrary order."
///
/// 第一段那个方括号里官方列了**四类**（quality / meta / year / safety），但它们被写成
/// 同一个方括号，所以"这四类必须合成一段"和"必须拆成四段"都不是官方规定——官方只规定
/// 它们整体排在数量段之前。本文件选择**拆成四段**，理由：
///
///   - 安全词本来就有独立的校验（<see cref="SafetyRating"/>），合成一段会让"这段里
///     哪个是安全词"要靠额外标记才能看出来；
///   - 官方自己的推荐前缀是 `masterpiece, best quality, score_7, safe`，正是
///     quality → safety 这个子序，拆开后无需特判；
///   - 四类的判据来源不同（quality/meta 来自模型卡逐条列举、safety 来自 Danbooru
///     评级规范），耦合在一段里改一处会牵动另一处。
///
/// 注意官方模型卡**自相矛盾**：`## Tag order` 写 `quality/meta/year/safety`，而
/// `## Full tag example` 却是 `year 2025, newest, normal quality, score_5, highres, safe`
/// （year 在前）。因为 `arbitrary order` 授权我们自定，这里按 Tag order 那一行的顺序，
/// 与官方推荐前缀一致。
///
/// 这个文件刻意不引用任何 WinUI 类型，以便对拍/回归用的 net10 控制台程序
/// 能直接编译同一份源码（见 tests\corecheck.csproj）。
/// </summary>
public static class TagSections
{
    // ---- 大类定义（顺序即官方顺序，不要随意调换）----

    /// <summary>质量：人类评分词 + PonyV7 的 score_* 审美分。官方第一段的第 1 类。</summary>
    public const string Quality = "quality";

    /// <summary>元信息：分辨率、截图来源、压缩伪影等。官方第一段的第 2 类。</summary>
    public const string Meta = "meta";

    /// <summary>年代：具体年份与 newest/recent 这类时期词。官方第一段的第 3 类。</summary>
    public const string Period = "period";

    /// <summary>安全：safe / sensitive / nsfw / explicit 四档。官方第一段的第 4 类。</summary>
    public const string Safety = "safety";

    public const string Count = "count";
    public const string Character = "character";
    public const string Series = "series";
    public const string Artist = "artist";
    public const string General = "general";

    /// <summary>
    /// 不是大类，而是「自然语言」这一档的配色键。
    ///
    /// Anima 的提示词允许标签和英文散文混排（官方："You can mix tags and natural
    /// language in arbitrary order."），散文不参与标签排序，但要能一眼和标签区分开。
    /// 它没有自己的"段"（不占官方大类顺序里的位置），所以只在配色表里单列一项。
    /// </summary>
    public const string Prose = "prose";

    public static readonly TagSectionInfo[] All =
    {
        new() { Key = Quality,   Index = 0, Title = "质量",        DefaultHex = "#E8A33D" },
        new() { Key = Meta,      Index = 1, Title = "元信息",      DefaultHex = "#B0A8B9" },
        new() { Key = Period,    Index = 2, Title = "年代",        DefaultHex = "#5FB3C4" },
        new() { Key = Safety,    Index = 3, Title = "安全",        DefaultHex = "#D9534F" },
        new() { Key = Count,     Index = 4, Title = "人物数量",    DefaultHex = "#2FA39B" },
        new() { Key = Character, Index = 5, Title = "角色",        DefaultHex = "#9B6BD6" },
        new() { Key = Series,    Index = 6, Title = "作品 / 系列",  DefaultHex = "#4A90D9" },
        new() { Key = Artist,    Index = 7, Title = "画师",        DefaultHex = "#E0607E" },
        new() { Key = General,   Index = 8, Title = "一般标签",    DefaultHex = "#7A8B99" },
    };

    /// <summary>设置页里的全部配色项：九个大类 + 自然语言。</summary>
    public static readonly TagSectionInfo[] Colorable =
        All.Append(new TagSectionInfo
        {
            Key = Prose,
            Index = All.Length,
            Title = "自然语言（散文）",
            DefaultHex = "#8FA876",
        }).ToArray();

    /// <summary>
    /// 官方第一段那四类 + 数量段：它们按官方规定整体排在 character 之前，
    /// 所以自动排序（<see cref="VocabDb.Arrange"/>）把它们当"头部"处理。
    /// 顺序就是 <see cref="All"/> 里的顺序。
    /// </summary>
    public static readonly string[] HeadKeys = { Quality, Meta, Period, Safety, Count };

    /// <summary>按 key 取大类定义。只认九个真大类——<see cref="Prose"/> 不是大类
    /// （它不占官方顺序里的位置），传进来会落到 general，因为调用方拿它做的是
    /// "这段文字属于哪个大类"的归类，而散文的答案就是"没有大类"。
    /// 配色要取散文色请用 <see cref="ColorOf"/>。
    /// </summary>
    public static TagSectionInfo Get(string key) =>
        All.FirstOrDefault(s => s.Key == key) ?? All[^1];

    /// <summary>配色用：九个大类之外还认自然语言。</summary>
    public static TagSectionInfo ColorOf(string key) =>
        Colorable.FirstOrDefault(s => s.Key == key) ?? All[^1];

    public static int IndexOf(string key) => Get(key).Index;

    /// <summary>
    /// 自然语言那一段在列表里排最后。取一个大过所有大类的值，让它自成一"段"，
    /// 免得混进 general：混进去的话它会排在 general 的行里，还可能抢到
    /// general 的段头条带，标题写成「一般标签」——那正好是我们不想暗示的事。
    /// </summary>
    public const int ProseIndex = 99;

    /// <summary>段头条带要显示的标题。散文不是大类，单独给名字。</summary>
    public static string TitleOf(int sectionIndex) =>
        sectionIndex >= ProseIndex ? ColorOf(Prose).Title : All[sectionIndex].Title;

    // ---- 控制词表：**唯一定义**，全部是有序数组 ----

    /*
     * 这些数组是分类与排序的唯一真源：
     *   - 各段专用的 HashSet 由它们派生；
     *   - 段内次序也由数组下标给出。
     *
     * 之所以要合并成一份：此前 VocabDb.HeadGroups 与本文件各写了一套控制词表，
     * 结果两边的成员已经不一致（HeadGroups 压根没有元信息那一组，导致 highres
     * 被当成普通标签按热度排进正文），而安全词表也在本文件和 SafetyRating 里
     * 各写了一份、成员不同（一个有 questionable 没 general，另一个反过来）。
     * 数组 + 派生集合从根上杜绝再次分叉。
     */

    /// <summary>
    /// 质量词。官方模型卡 `## Quality tags` 一节：
    /// Human score based（人类评分）+ PonyV7 aesthetic model based（score_9…score_1）。
    ///
    /// 数组顺序沿用 Python 参考实现（animatag.py 的 QUALITY），因为自动排序的
    /// 段内次序要与它逐字节一致；分类只看成员、与顺序无关。
    /// </summary>
    public static readonly string[] QualityWords =
    {
        "masterpiece", "best quality", "very aesthetic", "good quality",
        "normal quality", "worst quality", "low quality",
    };

    /// <summary>官方 `## Meta tags`：highres, absurdres, anime screenshot, jpeg artifacts,
    /// official art, etc。后半是官方推荐负向串里的词——它们同样是控制词，
    /// 不该被当成一般标签按热度排。</summary>
    public static readonly string[] MetaWords =
    {
        "highres", "absurdres", "anime screenshot", "official art", "jpeg artifacts",
        "blurry", "chromatic aberration", "artist name",
    };

    /// <summary>官方 `## Time period tags`：Specific year（year 2025…）+ Period。
    /// 官方列的时期词是 newest, recent, mid, early, old；oldest 是 Danbooru 侧
    /// 常见写法，一并认。</summary>
    public static readonly string[] PeriodWords =
    {
        "newest", "recent", "mid", "early", "old", "oldest",
    };

    /// <summary>
    /// 官方 `## Safety tags`：safe, sensitive, nsfw, explicit（四个词、没有判据，
    /// 边界见 <see cref="SafetyRating"/>）。questionable 是 Danbooru 的对应档，
    /// 词库里会见到，识别出来免得被当成普通标签。
    ///
    /// **数组次序刻意沿用 Python 参考实现（animatag.py 的 RATINGS）**，因为
    /// <see cref="VocabDb.Arrange"/> 的段内次序由下标给出，要与它逐字节一致。
    /// 分类只看成员，与次序无关；四个档位互斥（只该写一个），所以这个次序
    /// 实际只影响"万一同时出现多个"时的排列。
    /// 档位强弱的比较用 <see cref="SafetyRating.Ladder"/>，那是另一回事。
    /// </summary>
    public static readonly string[] SafetyWords =
    {
        "safe", "sensitive", "questionable", "explicit", "nsfw",
    };

    /// <summary>官方第二节：1girl / 1boy / 1other 这类数量与人称标签。</summary>
    public static readonly string[] CountWords =
    {
        "1girl", "2girls", "3girls", "4girls", "5girls", "6+girls", "multiple girls",
        "1boy", "2boys", "3boys", "4boys", "5boys", "6+boys", "multiple boys",
        "1other", "2others", "multiple others",
        "solo", "solo focus", "male focus", "female focus", "other focus", "no humans",
    };

    private static readonly HashSet<string> QualityTags = new(QualityWords, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MetaTags = new(MetaWords, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PeriodTags = new(PeriodWords, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SafetyTags = new(SafetyWords, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CountTags = new(CountWords, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这个字符串是否是一个"能认出来的标签"。
    ///
    /// 用途是**把标签和英文散文区分开**：Anima 的提示词是两者混排
    /// （官方："You can mix tags and natural language in arbitrary order."），
    /// 而散文必须留白、不参与标签校验，也不能被染成 general 标签色。
    ///
    /// 判定顺序刻意是"先认控制词、再查词库"：官方逐条列出的控制词
    /// （masterpiece / score_7 / year 2025 / safe / 1girl / solo …）在
    /// Danbooru 上要么没有条目、要么归类与 Anima 的用法不一致。
    ///
    /// 实测（679 个真实标签 + 官方两段提示词）：含句末标点的标签 0 个、
    /// 首字母大写的标签 0 个，所以把"句子形状"的串判成散文不会误伤真标签；
    /// 而 74 个词库不认的标签（`2 fingers`、`bangs`）都是小写短语，
    /// 仍然会被当成标签保留在列表里（标成"不在任何索引中"），不会消失。
    /// </summary>
    public static bool IsKnownTag(string tag, VocabDb? vocab)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim();
        var low = t.ToLowerInvariant();

        if (QualityTags.Contains(low) || MetaTags.Contains(low) ||
            PeriodTags.Contains(low) || SafetyTags.Contains(low) ||
            CountTags.Contains(low) || IsScore(low) || IsYear(low))
            return true;

        // 画师标签必须带 @（官方规则），@ 开头即视为标签
        if (t.StartsWith('@')) return true;

        return vocab?.Status(t) is "anima" or "valid" or "retired";
    }

    /// <summary>词库里用的中文大类名，映射到官方大类。</summary>
    private static readonly Dictionary<string, string> CnCategoryMap = new(StringComparer.Ordinal)    {
        ["质量"] = Quality,
        ["负面词"] = Meta,
        ["Anima控制词"] = Meta,
        ["画面风格"] = Meta,
        ["Anima风格"] = Meta,
        ["Anima构图"] = Meta,
        ["人物数量"] = Count,
    };

    private static bool IsScore(string low) =>
        low.Length == 7 && low.StartsWith("score_", StringComparison.Ordinal)
        && char.IsAsciiDigit(low[6]);

    private static bool IsYear(string low) =>
        low.StartsWith("year ", StringComparison.Ordinal)
        && int.TryParse(low.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
        && y >= 1900 && y <= 2200;

    private static int YearOf(string low) =>
        int.TryParse(low.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 1900;

    /// <summary>
    /// 头部控制词的判定 + **段内次序**，供自动排序使用。
    ///
    /// 这是分类与排序共用的唯一入口：<see cref="Classify"/> 判"属于哪一段"，
    /// 这里在同一份词表上再给出"段内排第几"。两者共用 <see cref="QualityWords"/>
    /// 等数组，所以不可能各自漂移。
    ///
    /// <paramref name="order"/> 在段内单调：静态词用数组下标，动态词
    /// （score_N / year N）按数值排在静态词之后。
    /// </summary>
    public static bool TryHeadSection(string tag, out string section, out int order)
    {
        section = General;
        order = 0;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var low = tag.Trim().ToLowerInvariant();

        static int At(string[] words, string low) =>
            Array.FindIndex(words, w => string.Equals(w, low, StringComparison.OrdinalIgnoreCase));

        var i = At(QualityWords, low);
        if (i >= 0) { section = Quality; order = i; return true; }
        if (IsScore(low))
        {
            section = Quality;
            order = QualityWords.Length + (low[6] - '1');
            return true;
        }

        i = At(MetaWords, low);
        if (i >= 0) { section = Meta; order = i; return true; }

        i = At(PeriodWords, low);
        if (i >= 0) { section = Period; order = i; return true; }
        if (IsYear(low))
        {
            section = Period;
            order = PeriodWords.Length + (YearOf(low) - 1900);
            return true;
        }

        i = At(SafetyWords, low);
        if (i >= 0) { section = Safety; order = i; return true; }

        i = At(CountWords, low);
        if (i >= 0) { section = Count; order = i; return true; }

        return false;
    }

    /// <summary>
    /// 把标签归到官方九个大类之一。
    ///
    /// 判断顺序有讲究：先认官方在模型卡里逐条列出的控制词。这些词在 Danbooru
    /// 上要么没有条目、要么归类与 Anima 的用法不一致（`sensitive` 在 Danbooru
    /// 是 general，但官方把它列在 safety 段），所以硬编码的清单优先。
    /// 之后才查词库，且 Anima 自带索引优先——它才是 Anima 认的标签集。
    /// </summary>
    public static string Classify(string tag, VocabDb? vocab)
    {
        if (string.IsNullOrWhiteSpace(tag)) return General;
        var t = tag.Trim();
        var low = t.ToLowerInvariant();

        // 官方第一段的四类，按官方 Tag order 那一行的次序判定
        if (QualityTags.Contains(low) || IsScore(low)) return Quality;
        if (MetaTags.Contains(low)) return Meta;
        if (PeriodTags.Contains(low) || IsYear(low)) return Period;
        if (SafetyTags.Contains(low)) return Safety;

        if (CountTags.Contains(low)) return Count;

        // 画师标签必须带 @（官方：不加 @ 效果会非常弱），所以 @ 开头就是画师
        if (t.StartsWith('@')) return Artist;

        // 词库查不到时兜底到 general：这是最保守的选择，因为 general 段本来
        // 就是"其余全部"。实测训练集 679 个标签里有 71 个走到这里（都是
        // 颜色+名词的复合短语，本来也属于 general）。
        var cat = vocab?.CategoryOf(t) ?? "";
        return cat switch
        {
            "character" => Character,
            "copyright" => Series,
            "artist" => Artist,
            "meta" => Meta,
            "anima-cat6" => General,     // Anima 索引里自成一类的杂项，官方没有对应段
            "anima-cat-1" => General,
            _ => CnCategoryMap.TryGetValue(cat, out var m) ? m : General,
        };
    }

    /// <summary>
    /// 只按大类做**稳定**排序：大类之间按官方顺序，大类内部的相对次序原样保留。
    ///
    /// 这里刻意不按 post_count / prior 之类的启发式重排同大类内部——用户明确
    /// 要求大类内部可调，任何"顺手优化"都会把手工调好的顺序弄丢。而且稳定
    /// 排序意味着：对已经合规的 caption 调用它是恒等变换（实测训练集 53/53
    /// 都是恒等变换，这条被当成回归测试）。
    ///
    /// 需要"按热度自动排"的是生图侧，那条路径用 <see cref="VocabDb.Arrange"/>：
    /// **分类共用本文件，段内策略不同**——这是有意的分工，不是重复实现。
    /// </summary>
    public static List<string> Canonicalize(IEnumerable<string> tags, VocabDb? vocab)
    {
        var keyed = new List<(string Tag, int Pos, int Sec)>();
        var i = 0;
        foreach (var raw in tags)
        {
            var t = (raw ?? "").Trim();
            if (t.Length == 0) continue;
            keyed.Add((t, i++, IndexOf(Classify(t, vocab))));
        }

        // 比较里带上原始位置，等于稳定排序，且不依赖 List.Sort 是否稳定
        keyed.Sort((a, b) =>
        {
            var c = a.Sec.CompareTo(b.Sec);
            return c != 0 ? c : a.Pos.CompareTo(b.Pos);
        });

        return keyed.Select(k => k.Tag).ToList();
    }

    // ---- 颜色（纯逻辑部分；画刷构造在 TagColors 里）----

    /// <summary>
    /// 把用户填的颜色串规范化成 "#RRGGBB"。接受带不带 #、6 位或 8 位（8 位丢弃
    /// alpha——大类颜色不需要透明度）。非法输入返回 null，由调用方回落默认色。
    /// </summary>
    public static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 && s.Length != 8) return null;
        foreach (var ch in s)
            if (!Uri.IsHexDigit(ch)) return null;
        // 8 位时后 6 位才是 RGB（前两位是 alpha）
        return "#" + s[^6..].ToUpperInvariant();
    }
}
