using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 日志的回归：**异常栈不能出现在界面面板里**。
///
/// 这条不是在测"日志能写"——那件事太显然。它钉的是一个**用户可见性**的约定：
/// 实时日志面板是给用户看的，调用栈有几十行 `at System...`，灌进去会把
/// 真正有用的三两行挤出去，而用户从这个界面上要得到的只是"出错了，原因是这个"。
/// 完整栈仍然要落盘（排查靠它），所以两边的差别必须被固定住——
/// 否则哪天有人把 <c>WriteException</c> 换回 <c>Write(ctx + "：" + ex)</c>，
/// 面板会重新被栈冲满，而没有任何测试会失败。
/// </summary>
public static class LogCheck
{
    public static void Run()
    {
        Harness.Section("日志：摘要进界面面板，异常栈只进文件");

        // 用一个必然带栈的异常，且让栈里出现一个可检索的独特标记。
        Exception ex;
        try { ThrowDeep(); throw new InvalidOperationException("不可达"); }
        catch (Exception caught) { ex = caught; }

        Harness.True("异常确实带了调用栈（否则这一节什么都没测到）",
                     ex.ToString().Contains(nameof(ThrowDeep), StringComparison.Ordinal));

        // **证明检测器本身有效**：确认原始 ex.ToString() 里确实有那种会被
        // 判为"栈"的形态。少了这一条，若哪天栈格式变了，上面的
        // "面板里没有调用栈"会变成永远通过的空断言——测了个寂寞。
        Harness.True("检测器有效：原始 ex.ToString() 里确实有调用栈行",
                     ex.ToString().Contains("   at ", StringComparison.Ordinal));

        ProxyLog.WriteException("回归用上下文", ex);

        var lines = ProxyLog.Snapshot();
        Harness.True("面板里收到了这次记录", lines.Count > 0);

        var shown = lines[^1];
        Harness.True("面板这一行里有摘要与原因",
                     shown.Contains("回归用上下文", StringComparison.Ordinal) &&
                     shown.Contains("内层", StringComparison.Ordinal));
        Harness.True("面板这一行里**没有**调用栈",
                     !shown.Contains("   at ", StringComparison.Ordinal) &&
                     !shown.Contains(nameof(ThrowDeep), StringComparison.Ordinal));

        Harness.True("面板里只有一行（栈没有被逐行摊进来）",
                     shown.IndexOf('\n') < 0);

        // **反向断言：栈不能被丢掉，只是进了文件。** 少了这一条，
        // 把 WriteException 实现成"什么都不写"也能让上面的断言全绿，
        // 而排查能力已经没了。
        var file = ProxyLog.FilePath;
        Harness.True("日志文件已经产生", File.Exists(file));
        var tail = File.ReadAllText(file);
        Harness.True("文件里有完整调用栈（排查靠它）",
                     tail.Contains("回归用上下文", StringComparison.Ordinal) &&
                     tail.Contains(nameof(ThrowDeep), StringComparison.Ordinal));
    }

    /// <summary>制造一段真实调用栈，让栈里出现这个名字供断言检索。</summary>
    private static void ThrowDeep() => throw new InvalidOperationException("内层");
}
