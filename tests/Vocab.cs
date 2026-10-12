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

        var cn = vocab.ResolveChinese("初音未来");
        Harness.True("ResolveChinese(\"初音未来\") 有命中", cn.Tag is not null);
        Harness.Info("初音未来 → " + (cn.Tag ?? "(无)"));

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
