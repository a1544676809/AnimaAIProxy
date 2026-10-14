namespace AnimaAIProxy.Core;

/// <summary>
/// 自定义模式下的时间单位。
///
/// 两个 Label 不是冗余：下拉框里要短（"分"），而状态行里要能读（"5 分钟"）——
/// 用同一个词会得到"空闲 5 分后……"这种半截话，或者下拉框里挤着"分钟"。
/// </summary>
public sealed record CleanupUnit(string Label, string LongLabel, int SecondsPerUnit);

/// <summary>
/// 浏览器临时目录的清理时间：界面档位、自定义换算、状态文案的**唯一来源**。
///
/// 为什么要单独一个类型而不是在配置里放个整数：这个值有三处消费者 ——
/// 配置的钳制、界面下拉框、状态行文案。三处各写一份换算，就会出现
/// "界面显示 5 分钟、实际按 3000 秒跑"这类**没有任何症状**的偏差
/// （与 <see cref="ProxyConfig.ProblemFor"/>、<see cref="ProxyConfig.FetchProblemFor"/>
/// 抽出来是同一个理由）。
///
/// <b>时间单位是近似值</b>：1 个月按 30 天、1 年按 365 天算。刻意不引入日历 ——
/// 这个设置问的是"多久没人用就清掉"，不是"到某月某日为止"，跨月/闰年的差别
/// 对它的用途没有意义，而引入日历只会多一处出错的地方。
/// </summary>
public static class CleanupSchedule
{
    /// <summary>永不清理。**负数**，这样"大于 0 = 秒数"这条判断不会被它破坏。</summary>
    public const int Never = -1;

    /// <summary>立即清理：每次抓完就关掉浏览器并删掉临时目录，不保留复用窗口。</summary>
    public const int Immediate = 0;

    /// <summary>默认 5 分钟。与浏览器引擎空闲回收的默认值一致，不改变老配置的行为。</summary>
    public const int DefaultSeconds = 300;

    /// <summary>上限（10 年）。再大没有意义，而且能保证乘法不溢出。</summary>
    public const int MaxSeconds = 315_360_000;

    /// <summary>
    /// 界面下拉框里的档位（不含"自定义"），顺序即显示顺序。
    /// 这是用户点名要的那几个：立即 / 5 分 / 30 分 / 1 时 / 6 时 / 1 日 / 1 月 / 永不。
    /// </summary>
    public static readonly IReadOnlyList<int> Presets = new[]
    {
        Immediate, 300, 1800, 3600, 21600, 86400, 2_592_000, Never
    };

    /// <summary>自定义模式可选的单位。顺序即下拉框顺序（从大到小）。</summary>
    public static readonly IReadOnlyList<CleanupUnit> Units = new[]
    {
        new CleanupUnit("年", "年", 31_536_000),
        new CleanupUnit("月", "个月", 2_592_000),
        new CleanupUnit("日", "天", 86_400),
        new CleanupUnit("时", "小时", 3_600),
        new CleanupUnit("分", "分钟", 60),
    };

    /// <summary>给状态行读的长单位，多一个"秒"（自定义里没有它，但手改配置可以出现任意秒数）。</summary>
    private static readonly IReadOnlyList<CleanupUnit> HumanizeUnits = new[]
    {
        new CleanupUnit("年", "年", 31_536_000),
        new CleanupUnit("月", "个月", 2_592_000),
        new CleanupUnit("日", "天", 86_400),
        new CleanupUnit("时", "小时", 3_600),
        new CleanupUnit("分", "分钟", 60),
        new CleanupUnit("秒", "秒", 1),
    };

    /// <summary>把配置里的值拉回合法区间。认不出的值退回默认 5 分钟。</summary>
    public static int Normalize(int seconds) =>
        seconds < Never || seconds > MaxSeconds ? DefaultSeconds : seconds;

    /// <summary>下拉框里那一行文字。<c>Never</c>/<c>Immediate</c> 有专名，其余由数值算出来。</summary>
    public static string LabelFor(int seconds) => seconds switch
    {
        Never => "永不清理",
        Immediate => "立即清理",
        _ => Humanize(seconds),
    };

    /// <summary>这个值是不是下拉框里的预设档位（不是就得走"自定义"那一行）。</summary>
    public static bool IsPreset(int seconds) => Presets.Contains(seconds);

    /// <summary>
    /// 自定义：数字 × 单位 → 秒。用 long 算再钳制，避免手填 999999 年时溢出成负数 ——
    /// 溢出的症状是"填了个大数，结果变成立即清理"，而且不报错。
    /// </summary>
    public static int FromCustom(int value, int unitSeconds)
    {
        if (unitSeconds <= 0) unitSeconds = 60;
        if (value < 1) value = 1;
        var total = (long)value * unitSeconds;
        return total > MaxSeconds ? MaxSeconds : (int)total;
    }

    /// <summary>
    /// 反过来：秒 → 自定义那两个控件该显示什么。取**能整除的最大单位**，
    /// 这样 3600 会显示成"1 时"而不是"60 分"。
    ///
    /// 没有一个单位能整除时（只有手改配置文件才会出现，比如 7 秒）退到分钟并向上取整。
    /// 取整不会静默改掉配置：状态行显示的是**真实秒数**算出来的文案，用户看得见差别。
    /// </summary>
    public static void SplitForCustom(int seconds, out int value, out int unitSeconds)
    {
        var s = Math.Clamp(seconds, 1, MaxSeconds);
        foreach (var u in Units)
        {
            if (s % u.SecondsPerUnit == 0)
            {
                value = s / u.SecondsPerUnit;
                unitSeconds = u.SecondsPerUnit;
                return;
            }
        }
        unitSeconds = 60;
        value = Math.Max(1, (int)Math.Ceiling(s / 60.0));
    }

    /// <summary>把秒数说成人话：300 → "5 分钟"、5400 → "1 小时 30 分钟"（最多两段）。</summary>
    public static string Humanize(int seconds)
    {
        if (seconds <= 0) return seconds == Immediate ? "立即" : "永不";

        var parts = new List<string>();
        var left = seconds;
        foreach (var u in HumanizeUnits)
        {
            var n = left / u.SecondsPerUnit;
            if (n <= 0) continue;
            parts.Add(n + " " + u.LongLabel);
            left -= n * u.SecondsPerUnit;
            if (parts.Count == 2) break;      // 三段以上没人读，也省得状态行过长
        }
        return parts.Count == 0 ? seconds + " 秒" : string.Join(" ", parts);
    }

    /// <summary>界面状态行里那句话说清楚"这个设置到底会怎样"。</summary>
    public static string Describe(int seconds)
    {
        if (seconds == Never)
            return "永不清理：浏览器引擎与它的临时目录会一直留着（实测 420–530 MB 内存），" +
                   "想立刻释放请点「立即清理」。";
        if (seconds == Immediate)
            return "立即清理：每次抓完就关掉浏览器并删掉临时目录，不保留复用" +
                   "（下次抓取要重新启动，约多花 0.7 秒）。";
        return $"空闲 {Humanize(seconds)}后关掉浏览器并删掉它的临时目录；" +
               "这段时间内再抓取会复用同一个实例。";
    }
}
