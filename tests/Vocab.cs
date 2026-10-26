using AnimaAIProxy.Core;
using AnimaCaptioner.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 词库内核的回归：这些断言**不是**在测我们自己写的代码，而是在钉住
/// vendored 进来的 AnimaCaptioner 内核的行为契约。
///
/// 为什么必须钉住它：代理对模型的全部承诺（"标签名只能来自这里"、
/// "tag= 后面那一段才是标签名"）都建立在这几个函数的语义上。上游哪天改了
/// 搜索的排序或工具结果的格式，代理送出去的工具结果就悄悄变形了——而模型
/// 那边的表现只是"回答变差了"，没有报错，极难归因。所以这里对着**真实词库**
/// 断言，而不只是对着 mock。
///
/// 词库缺失时整节跳过并以退出码 2 结束：那表示"没能验证"，不是"通过"。
/// </summary>
public static class Vocab
{
    /// <summary>
    /// 词库位置的解析顺序：命令行 &gt; 环境变量 &gt; AnimaCaptioner 的 settings.json。
    ///
    /// 特意读 AnimaCaptioner 的 settings.json：代理要用的就是同一份词库，
    /// 让用户在两个程序里各填一遍路径是纯粹的浪费，而填错一份就会出现
    /// "打标软件搜得到、代理搜不到"这种最难查的不一致。
    /// </summary>
    public static string? ResolveDbPath(string[] args)
    {
        foreach (var a in args)
            if (a.StartsWith("--db=", StringComparison.OrdinalIgnoreCase))
                return a[5..].Trim('"');

        var env = Environment.GetEnvironmentVariable("AIP_VOCABDB");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        return SettingsPathProbe.VocabDbPathFromSettings();
    }

    public static string? ResolveSeedPath()
    {
        var env = Environment.GetEnvironmentVariable("AIP_SEED");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return SettingsPathProbe.SeedPathFromSettings();
    }

    public static void Run(string[] args)
    {
        Harness.Section("1. 词库内核（vendored：VocabDb / TagSearch / TagSections）");

        var db = ResolveDbPath(args);
        if (string.IsNullOrWhiteSpace(db) || !File.Exists(db))
        {
            Harness.Note("找不到词库，本节跳过（退出码 2 = 没能验证）。");
            Harness.Info("用 --db=<path> 或环境变量 AIP_VOCABDB 指定，例如：");
            Harness.Info(@"  proxycheck vocab --db=E:\ComfyUI\anima-vocab\vocab.sqlite");
            Environment.ExitCode = 2;
            return;
        }

        var seed = ResolveSeedPath();
        Harness.Info("词库：" + VocabDb.Describe(db));
        Harness.Info("seed：" + (seed is not null && File.Exists(seed) ? seed : "(无)"));

        var vocab = new VocabDb();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vocab.Load(db, seed ?? Path.ChangeExtension(db, ".json"));
        sw.Stop();
        Harness.Info($"载入耗时 {sw.ElapsedMilliseconds} ms");

        Harness.Eq("标签数 > 30 万", vocab.TagCount > 300_000, true);
        Harness.True("Anima 索引非空", vocab.AnimaCount > 50_000);

        // ---- 精确解析：模型报出来的标签名必须能在这里核对 ----
        Harness.Section("2. ResolveEnglish / ResolveChinese：标签名与中文都能反查");

        var r1 = vocab.ResolveEnglish("1girl");
        Harness.Eq("ResolveEnglish(\"1girl\") 命中", r1.Tag, "1girl");

        var r2 = vocab.ResolveEnglish("blushing");
        Harness.Note("blushing 的状态：" + r2.Status + " → " + (r2.Tag ?? "(无)"));
        Harness.True("查不到的写法会如实报空或废弃，而不是编一个出来",
                     r2.Tag is null || !string.Equals(r2.Tag, "blushing", StringComparison.Ordinal)
                                    || r2.Status.Length > 0);

        Harness.Eq("ResolveEnglish 大小写不敏感", vocab.ResolveEnglish("1GIRL").Tag, "1girl");

        // 中文名的两条现状，都必须如实钉住——它们合起来决定了代理的中文策略。
        //
        // (1) 精确键只认库里逐字存的那一个名字。331,836 条中文名里有 163,154 条
        //     （49%）带「（限定词）」后缀，裸形式查不到：`初音未来` 精确匹配 0 条。
        // (2) 就算连着括号一起打，character / artist / copyright 这些类别也会被
        //     JunkCategories 主动拒掉——`初音未来（VOCALOID）` 正是 character。
        //
        // 两条合起来：内核的中文路对**人物 / 作品名**整体是不可用的。这是它的
        // 设计而非缺陷（它服务于打标时的自动转换，不该把场景词接到角色标签上），
        // 所以代理**不去绕过它**。模型要查人物就走 danbooru_search_tags，
        // 那条路（cn.StartsWith）是好的。
        var bare = vocab.ResolveChinese("初音未来");
        Harness.True("裸形式查不到（库里存的是 `初音未来（VOCALOID）`）", bare.Tag is null);

        var qualified = vocab.ResolveChinese("初音未来（VOCALOID）");
        Harness.True("连括号一起打也查不到——character 在 JunkCategories 里，内核主动拒",
                     qualified.Tag is null);
        Harness.Info($"ResolveChinese(初音未来（VOCALOID）) → {qualified.Tag ?? "(无)"} / {qualified.Status}");

        // 非 junk 类别则能精确命中——这才是代理回落真正服务的那一批
        Harness.Eq("非 junk 类别的括号限定形式能精确命中",
                   vocab.ResolveChinese("弓（武器）").Tag, "bow (weapon)");

        Harness.True("Search 按中文前缀能找到人物——模型查人物走的就是这条路",
                     vocab.Search("初音未来", 200).Any(h => h.Tag == "hatsune miku"));

        // ---- 归类：Hit.Section 决定工具结果里的 section= 字段 ----
        Harness.Section("3. TagSections：归类与 IsKnownTag");

        var sec = TagSections.Classify("hatsune miku", vocab);
        Harness.True("hatsune miku 归到某个大类", !string.IsNullOrEmpty(sec));
        Harness.Info("hatsune miku 的大类 = " + sec + " / 标题 = " + TagSections.Get(sec).Title);

        Harness.Eq("IsKnownTag(\"1girl\")", TagSections.IsKnownTag("1girl", vocab), true);
        Harness.Eq("IsKnownTag(\"definitely_not_a_real_tag_xyzzy\")",
                   TagSections.IsKnownTag("definitely_not_a_real_tag_xyzzy", vocab), false);

        // ---- 搜索：这是工具真正对外提供的能力 ----
        Harness.Section("4. TagSearch：搜索与工具结果文本");

        var q = TagSearch.QueryDetailed(vocab, "white dress", TagSearch.ToolLimit);
        Harness.True("搜 \"white dress\" 有命中", q.Hits.Count > 0);
        Harness.Eq("结果条数不超过 ToolLimit", q.Hits.Count <= TagSearch.ToolLimit, true);
        Harness.Eq("整串命中时 splitMatch 为 false", q.SplitMatch, false);
        Harness.Info("首条：" + (q.Hits.Count > 0 ? q.Hits[0].Tag + " / " + q.Hits[0].Cn : "(无)"));

        var content = TagSearch.ToToolContent(q.Hits, "white dress", 2, q.SplitMatch);
        Harness.Dump("ToToolContent 原文（这条文本会被原样回灌给模型）", content);
        Harness.True("第一行是 query:", content.StartsWith("query: ", StringComparison.Ordinal));
        Harness.True("每行都有 tag= 前缀（模型靠它认标签名的边界）",
                     q.Hits.Count == 0 || content.Contains("1. tag=", StringComparison.Ordinal));
        Harness.True("结尾报告剩余次数", content.EndsWith("searches remaining: 2", StringComparison.Ordinal));

        var miss = TagSearch.QueryDetailed(vocab, "zzzqqqxx_not_a_tag", TagSearch.ToolLimit);
        var missText = TagSearch.ToToolContent(miss.Hits, "zzzqqqxx_not_a_tag", 1, miss.SplitMatch);
        Harness.Eq("查不到时命中为空", miss.Hits.Count, 0);
        Harness.True("查不到时如实说没匹配，并提示换词",
                     missText.Contains("no tags matched", StringComparison.Ordinal));

        // 复合中文查询的覆盖兜底：这条兜底直接决定中文查询能不能用
        var cnq = TagSearch.QueryDetailed(vocab, "白色蕾丝连衣裙", TagSearch.ToolLimit);
        Harness.True("复合中文查询走覆盖兜底能查到东西", cnq.Hits.Count > 0);
        Harness.Eq("兜底路径会置 splitMatch", cnq.SplitMatch, true);
        Harness.Info("白色蕾丝连衣裙 → " +
                     string.Join(", ", cnq.Hits.Take(3).Select(h => h.Tag)));

        // 下划线写法：用户从 Danbooru 抄来的写法
        var und = TagSearch.QueryDetailed(vocab, "white_dress", TagSearch.ToolLimit);
        Harness.True("下划线写法也查得到（词库存的是空格形式）", und.Hits.Count > 0);

        // ---- 存在性核对：模型编的标签名要能被挑出来 ----
        Harness.Section("5. SuspiciousMentions：模型编的标签名会被挑出来");

        var sus = TagSearch.SuspiciousMentions(
            "推荐 `1girl` 和 `totally_made_up_tag_qq` 这两个。", vocab);
        Harness.Eq("只挑出编的那一个", sus.Count, 1);
        Harness.Eq("挑出来的正是编的那个", sus.Count > 0 ? sus[0] : "", "totally_made_up_tag_qq");
        Harness.Eq("控制词 best quality 不误报",
                   TagSearch.SuspiciousMentions("用 `best quality` 吧。", vocab).Count, 0);

        // ---- 代理对用户的保证：工具层把内核那个括号缺口补上 ----
        //
        // 三个工具都已经改成**批量入参**（数组），所以这一节同时钉两件事：
        // 批量形态本身（一次调用查一串），以及每一行的输出形状。
        Harness.Section("6. 工具层：批量解析 + 中文括号限定缺口");

        var host = new ToolHost(new VocabService());
        var toolCfg = new ProxyConfig { VocabDbPath = db };

        ToolHost.ToolResult Run(ToolKind kind, string args) =>
            host.ExecuteAsync(toolCfg, kind, args).GetAwaiter().GetResult();

        // 一次调用里混着英文 / 中文 / 括号限定 / 查不到 四种情况——这是模型
        // 真实会发出来的形状（"把我手上这串标签名验一遍"）。
        var batch = Run(ToolKind.Resolve,
                        "{\"tags\":[\"blush\",\"white_dress\",\"弓\",\"qqzzxx_not_a_tag\"]}");
        Harness.Dump("批量核对原文（会被原样回灌给模型）", batch.Text);
        Harness.True("英文标签命中", batch.Text.Contains("tag=blush", StringComparison.Ordinal));
        Harness.True("拼写被规范化时带一句备注",
                     batch.Text.Contains("tag=white dress", StringComparison.Ordinal) &&
                     batch.Text.Contains("(spelling normalized)", StringComparison.Ordinal));
        Harness.True("裸形式「弓」命中（内核落空 → 回落 Search）",
                     batch.Text.Contains("tag=bow (weapon)", StringComparison.Ordinal));
        Harness.True("中文回落带上命中的完整中文名，模型才知道匹配的是括号形式",
                     batch.Text.Contains("matched cn \"弓（武器）\"", StringComparison.Ordinal));
        Harness.True("行里同时给出帖数——工具说明承诺了 posts，就必须真的给",
                     batch.Text.Contains("posts=", StringComparison.Ordinal));
        Harness.True("未命中的那一行是 NOT FOUND", batch.Text.Contains("NOT FOUND", StringComparison.Ordinal));
        Harness.True("未命中的名字在结尾统一列一次（不再每行重复三句英文）",
                     batch.Text.Contains("not tags: qqzzxx_not_a_tag", StringComparison.Ordinal));
        Harness.Eq("摘要只列第一个 + 总数", batch.Summary, "核对「blush」等 4 个 → 3 命中");

        // 回落必须收窄，而且类别要由**内核**来裁：
        // `少女（mokufuu）` 的形状和 `弓（武器）` 一模一样，差别只在类别
        // （character vs general）。实测不审类别的话 `少女` 会解析到
        // `girl (mokufuu)` —— 正是内核注释里点名的那个坑。
        var shojo = Run(ToolKind.Resolve, "{\"tags\":[\"少女\",\"初音未\"]}");
        Harness.True("`少女` 仍是 NOT FOUND（回落让内核裁类别，不吃角色标签）",
                     shojo.Text.Contains("少女 → NOT FOUND", StringComparison.Ordinal));
        Harness.True("前缀相似不算命中（`初音未` 仍是 NOT FOUND）",
                     shojo.Text.Contains("初音未 → NOT FOUND", StringComparison.Ordinal));

        // 人物名与内核保持一致（不绕过 JunkCategories），但必须把模型引到
        // search 那条好路上——否则模型会以为"这个词库连初音未来都没有"。
        var miku = Run(ToolKind.Resolve, "{\"tags\":[\"初音未来\"]}");
        Harness.True("人物名与内核保持一致（NOT FOUND）",
                     miku.Text.Contains("初音未来 → NOT FOUND", StringComparison.Ordinal));
        Harness.True("没有退而求其次挑一个冷门同名变体（不许变成 hatsune miku (cosplay)）",
                     !miku.Text.Contains("tag=hatsune miku (cosplay)", StringComparison.Ordinal));

        // ---- 上游 VocabDb 那个"中文键被 junk 行顶掉"的 bug（AddCnKey）----
        //
        // 这批词以前**一律查不到**：`best` 里同名的第一行是画师 / 作品
        // （`@amagasa` 49 帖、`lingeries` 14 帖），精确中文键先到先得就记住了
        // 那一条，随后被 JunkCategories 拒掉，模型拿到的是"这个词不存在"——
        // 而真标签有上百万帖（内衣 103 万、长袖 239 万）。
        // 修法是建表时"非 junk 优先"，改在上游 VocabDb，两处共用同一份代码。
        var cnFix = Run(ToolKind.Resolve,
                        "{\"tags\":[\"内衣\",\"雨伞\",\"长袖\",\"荷叶边\",\"虎牙\",\"痣\",\"开心\"]}");
        Harness.Dump("常见中文词（以前全查不到）", cnFix.Text);
        var cnExpect = new (string Input, string Tag)[]
        {
            ("内衣", "underwear"), ("雨伞", "umbrella"), ("长袖", "long sleeves"),
            ("荷叶边", "frills"), ("虎牙", "fang"), ("痣", "mole"), ("开心", "happy"),
        };
        foreach (var (input, tag) in cnExpect)
            Harness.True($"{input} → {tag}（junk 行不再顶掉真标签）",
                         cnFix.Text.Contains($"{input} → tag={tag} ", StringComparison.Ordinal));

        // ---- scan：批量 + 去重 + 长词优先 ----
        var scan = Run(ToolKind.Scan,
                       "{\"texts\":[\"黑色雨伞，站在雨里，背景模糊\",\"银发少女站在雨中\"]}");
        Harness.Dump("批量切分原文", scan.Text);
        Harness.Eq("两段描述各自成段", CountOf(scan.Text, "text "), 2);
        Harness.True("雨伞 → umbrella（长词优先，不再退化成单字「雨」）",
                     scan.Text.Contains("tag=umbrella", StringComparison.Ordinal));
        // 去重是**分段内**的：两段各有一处「雨」本来就应该各出一行 rain。
        // 单段里「雨伞」和「雨里」两处都退化成「雨」才是那个 bug。
        var scanDedup = Run(ToolKind.Scan, "{\"texts\":[\"黑色雨伞，站在雨里\"]}");
        Harness.Eq("同一段里同一个标签不重复输出（rain 只出现一次）",
                   CountOf(scanDedup.Text, "tag=rain"), 1);
        Harness.True("scan 的结果行也带状态", scan.Text.Contains("| status=", StringComparison.Ordinal));

        // ---- search：批量 + 每查询分段 + 图例只写一次 ----
        var searched = Run(ToolKind.Search, "{\"queries\":[\"white dress\",\"蕾丝\"],\"limit\":5}");
        Harness.Dump("批量搜索原文", searched.Text);
        Harness.Eq("两个查询各自成段", CountOf(searched.Text, "query "), 2);
        Harness.True("行首是 `N. tag=`（模型靠它认标签名的边界）",
                     searched.Text.Contains("1. tag=", StringComparison.Ordinal));
        Harness.True("行里带帖数与状态",
                     searched.Text.Contains("posts=", StringComparison.Ordinal) &&
                     searched.Text.Contains("status=", StringComparison.Ordinal));
        Harness.Eq("图例只出现一次", CountOf(searched.Text, "format:"), 1);
        Harness.True("摘要带上词数", searched.Summary.Contains("等 2 个", StringComparison.Ordinal));

        // ---- 外部评审（2026-10-26）指出的四个会直接写坏提示词的缺陷 ----
        Harness.Section("6b. 评审回归：同义词一致 / 控制词 / 拆词过滤 / 批量反馈");

        // (1) 银发：三个工具必须给同一个答案，而且必须是 silver hair。
        //     以前 resolve/scan 走 seed（银发 → grey hair）、search 按词库 cn 命中
        //     silver hair，同一句话在两个工具里得到两个发色。
        var silverSearch = Run(ToolKind.Search, "{\"queries\":[\"银发\"],\"limit\":1}");
        var silverResolve = Run(ToolKind.Resolve, "{\"tags\":[\"银发\"]}");
        var silverScan = Run(ToolKind.Scan, "{\"texts\":[\"银发少女\"]}");
        Harness.True("search 银发 → silver hair",
                     silverSearch.Text.Contains("tag=silver hair", StringComparison.Ordinal));
        Harness.True("resolve 银发 → silver hair",
                     silverResolve.Text.Contains("tag=silver hair", StringComparison.Ordinal));
        Harness.True("scan 银发 → silver hair",
                     silverScan.Text.Contains("tag=silver hair", StringComparison.Ordinal));
        Harness.True("三处都不再给 grey hair（灰发才是它）",
                     !silverSearch.Text.Contains("tag=grey hair", StringComparison.Ordinal) &&
                     !silverResolve.Text.Contains("tag=grey hair", StringComparison.Ordinal) &&
                     !silverScan.Text.Contains("tag=grey hair", StringComparison.Ordinal));

        // (2) 控制词：`explicit` / `score_8` 在合并表里根本没有行，但它们在 Anima
        //     的提示词里是完全合法的头部词；说 NOT FOUND 会让模型把它们删掉。
        var control = Run(ToolKind.Resolve,
                          "{\"tags\":[\"explicit\",\"score_8\",\"best quality\",\"masterpiece\",\"general\"]}");
        Harness.Dump("控制词批量核对", control.Text);
        Harness.True("explicit → status=control（词表里有、库里的行没有）",
                     control.Text.Contains("explicit → tag=explicit | cn=- | posts=- | status=control",
                                           StringComparison.Ordinal));
        Harness.True("score_8 → status=control",
                     control.Text.Contains("score_8 → tag=score_8 | cn=- | posts=- | status=control",
                                           StringComparison.Ordinal));
        Harness.True("masterpiece → tag=masterpiece 且 status=control（不是 unknown）",
                     control.Text.Contains("tag=masterpiece", StringComparison.Ordinal) &&
                     control.Text.Contains("status=control", StringComparison.Ordinal) &&
                     !control.Text.Contains("masterpiece → tag=masterpiece | cn=杰作 | posts=0 | status=unknown",
                                            StringComparison.Ordinal));
        Harness.True("Danbooru 的 general 仍是控制词，并说明 Anima 写 safe",
                     control.Text.Contains("general → tag=general", StringComparison.Ordinal) &&
                     control.Text.Contains("Anima prompts use \"safe\"", StringComparison.Ordinal));

        // (3) 精确同名不吃"无引用"惩罚：否则 masterpiece 会输给
        //     `world masterpiece theater` 这种角色/作品标签。
        var mp = Run(ToolKind.Search, "{\"queries\":[\"masterpiece\"],\"limit\":1}");
        Harness.True("search masterpiece 第一行就是它自己",
                     mp.Text.Contains("1. tag=masterpiece", StringComparison.Ordinal));

        // (4) 拆词兜底只留"general + Anima 常用"：`rain night` 以前给出
        //     whisperain (arknights) 这类角色，现在连冷门 Danbooru 标签也不给。
        var split = Run(ToolKind.Search, "{\"queries\":[\"rain night\"],\"limit\":5}");
        Harness.Dump("拆词兜底结果", split.Text);
        var badRows = split.Text.Split('\n')
            .Where(l => l.Contains("tag=", StringComparison.Ordinal))
            .Count(l =>
            {
                var a = l.IndexOf("tag=", StringComparison.Ordinal) + 4;
                var b = l.IndexOf(" |", a, StringComparison.Ordinal);
                return b > a && l[a..b].Contains('(', StringComparison.Ordinal);
            });
        Harness.Eq("拆词结果里没有带括号的角色/作品标签", badRows, 0);
        Harness.True("拆词结果里不再出现 arknights 角色",
                     !split.Text.Contains("arknights", StringComparison.OrdinalIgnoreCase));
        Harness.True("拆词结果只留 status=anima 的 general 标签",
                     !split.Text.Contains("status=valid", StringComparison.Ordinal) &&
                     !split.Text.Contains("status=unknown", StringComparison.Ordinal));
        Harness.True("拆词结果里 rain / night 都在",
                     split.Text.Contains("tag=night", StringComparison.Ordinal));

        // (4b) 中文整句走"按词库切分"而不是拆词：湿漉漉的街道 → wet floor + street
        var phrase = Run(ToolKind.Search, "{\"queries\":[\"湿漉漉的街道\"],\"limit\":5}");
        Harness.Dump("中文整句搜索", phrase.Text);
        Harness.True("湿漉漉的街道能给出 street",
                     phrase.Text.Contains("tag=street", StringComparison.Ordinal));
        Harness.True("并说明是按概念切分出来的（phrase fallback）",
                     phrase.Text.Contains("splitting the phrase", StringComparison.Ordinal));

        // (4c) 复合词：雨夜 → rain + night（scan），夜景 → night（resolve/scan）
        var night = Run(ToolKind.Scan, "{\"texts\":[\"少女在雨夜奔跑\"]}");
        Harness.True("雨夜同时给出 rain 与 night",
                     night.Text.Contains("tag=rain", StringComparison.Ordinal) &&
                     night.Text.Contains("tag=night", StringComparison.Ordinal));
        var nightView = Run(ToolKind.Resolve, "{\"tags\":[\"夜景\"]}");
        Harness.True("夜景 → night", nightView.Text.Contains("tag=night", StringComparison.Ordinal));

        // (5) 12 条查询 × 默认 limit 不能把后几条查询整段吞掉（60 行额度要均分）。
        var many = Run(ToolKind.Search,
                       "{\"queries\":[" + string.Join(",", Enumerable.Range(0, 12)
                           .Select(i => "\"zzqq" + i + "\"")) + "]}");
        Harness.Eq("12 条查询全部有标题（一条都不丢）", CountOf(many.Text, "query "), 12);

        // (6) 超过上限要说明丢了几条，而不是静默截断；说明放在第一行。
        var over = Run(ToolKind.Resolve,
                       "{\"tags\":[" + string.Join(",", Enumerable.Range(0, 45)
                           .Select(i => "\"zztt" + i + "\"")) + "]}");
        Harness.True("超过 40 条会写明丢了几条",
                     over.Text.Contains("skipped", StringComparison.Ordinal) &&
                     over.Text.Contains("cap 40", StringComparison.Ordinal));

        var overSearch = Run(ToolKind.Search,
                             "{\"queries\":[" + string.Join(",", Enumerable.Range(0, 14)
                                 .Select(i => "\"zzhh" + i + "\"")) + "]}");
        Harness.True("search 超 12 条的说明在**第一行**（调用方只看第一行）",
                     overSearch.Text.StartsWith("note: only 12 of 14", StringComparison.Ordinal));

        // (7) scan 必须在标题行报告没匹配上的片段；已经匹配上的（复合词）不许误报。
        var unmatched = Run(ToolKind.Scan, "{\"texts\":[\"霓虹街道，雨夜，贵族气质\"]}");
        Harness.Dump("未匹配片段", unmatched.Text);
        Harness.True("scan 标题行里有 unmatched",
                     unmatched.Text.Contains("tag(s); unmatched:", StringComparison.Ordinal));
        // 只看 unmatched 里列出的那一段：整条输出里当然还回显着输入原文与 from 字段。
        var uAt = unmatched.Text.IndexOf("unmatched: ", StringComparison.Ordinal);
        var uTail = uAt >= 0
            ? unmatched.Text[(uAt + "unmatched: ".Length)..].Split('\n')[0]
            : "";
        Harness.True("未匹配的是「贵族气质」", uTail.Contains("贵族", StringComparison.Ordinal));
        Harness.True("已匹配的「霓虹」「雨夜」不会被报成 unmatched",
                     uTail.Length > 0 &&
                     !uTail.Contains("霓虹", StringComparison.Ordinal) &&
                     !uTail.Contains("雨夜", StringComparison.Ordinal));

        // (8) 同一个标签 search 与 resolve 的帖数必须一致（以前 silver hair 一处 0、
        //     一处 336,802：search 读合并表、resolve 回落到 Anima 索引）。
        var bloomSearch = Run(ToolKind.Search, "{\"queries\":[\"white dress\"],\"limit\":1}");
        var bloomResolve = Run(ToolKind.Resolve, "{\"tags\":[\"white dress\"]}");
        static string PostsOf(string text, string tag)
        {
            var i = text.IndexOf("tag=" + tag + " |", StringComparison.Ordinal);
            if (i < 0) return "";
            var p = text.IndexOf(" | posts=", i, StringComparison.Ordinal);
            if (p < 0) return "";
            var s = p + " | posts=".Length;
            var e = text.IndexOf(" |", s, StringComparison.Ordinal);
            return e < 0 ? text[s..].Trim() : text[s..e];
        }
        Harness.Eq("search 与 resolve 的帖数一致", PostsOf(bloomSearch.Text, "white dress"),
                   PostsOf(bloomResolve.Text, "white dress"));

        // ---- 摘要前缀是 Core 与回归共用的契约 ----
        Harness.Section("7. 工具摘要前缀（回归靠它数「执行了几次」，新增工具必须同步）");

        // 不变量：**一个工具种类一条前缀**。新增 ToolKind 却忘了补前缀，就会让
        // Live 那边"我们的工具执行了几次"静默漏数——web_search 加进来时正是这么漏的：
        // 模型明明搜索了，计数却是 0，断言反而去怪"模型没调工具"。
        Harness.Eq("摘要前缀数与工具种类数一致",
                   ToolHost.SummaryPrefixes.Length, Enum.GetValues<ToolKind>().Length);

        // 行为级：三个词库工具各跑一次，摘要都要被前缀表认出来。
        // web_search 由 Search.cs 那一节覆盖（它要桩后端才跑得起来）。
        var samples = new (ToolKind Kind, string Args)[]
        {
            (ToolKind.Search,  "{\"queries\":[\"lace\"]}"),
            (ToolKind.Resolve, "{\"tags\":[\"弓\"]}"),
            (ToolKind.Scan,    "{\"texts\":[\"1girl, lace\"]}"),
        };
        foreach (var (kind, callArgs) in samples)
        {
            var r = host.ExecuteAsync(toolCfg, kind, callArgs).GetAwaiter().GetResult();
            Harness.True($"{kind} 的摘要被前缀表认出（{r.Summary}）",
                         ToolHost.SummaryPrefixes.Any(p => r.Summary.StartsWith(p, StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// 数一个子串在文本里出现几次。批量输出里"每段一行 / 每行一条"这类断言
    /// 靠它，比写一串 Contains 精确（能发现"同一行被写了两遍"）。
    /// </summary>
    private static int CountOf(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }
}

/// <summary>
/// 从 AnimaCaptioner 的 settings.json 里借词库路径。
/// 只做只读探测，解析失败一律当"没有"，绝不抛异常——代理起不来
/// 不该是因为另一个程序的配置文件坏了。
/// </summary>
internal static class SettingsPathProbe
{
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnimaCaptioner", "settings.json");

    public static string? VocabDbPathFromSettings() => ReadString("VocabDbPath");
    public static string? SeedPathFromSettings() => ReadString("SeedPath");

    private static string? ReadString(string property)
    {
        try
        {
            if (!File.Exists(SettingsFile)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsFile));
            if (doc.RootElement.TryGetProperty(property, out var el) &&
                el.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* 借不到就借不到，调用方会提示用户显式指定 */ }
        return null;
    }
}
