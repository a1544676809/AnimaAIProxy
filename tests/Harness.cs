using System.Text;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 极简断言与输出工具。
///
/// 刻意不引入 xunit 之类的框架：这套回归要能在**没有网络、没有词库**的机器上
/// 用一条 dotnet run 跑起来，多一个包就多一个还原失败的理由。断言的样子沿用
/// AnimaCaptioner/tests 那套（corecheck），两个仓库的输出风格保持一致。
///
/// 退出码约定（与 corecheck 相同）：
///   0 = 全部通过；1 = 有断言失败；2 = **没能验证**（缺词库/缺数据集等外部输入）。
/// 2 与 0 的区别很重要：把"没跑"报成"通过"会让回归彻底失去意义。
/// </summary>
public static class Harness
{
    private static int _bad;

    public static int Failures => _bad;
    public static int ExitCode => _bad > 0 ? 1 : 0;

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + title + " ===");
    }

    public static void Eq<T>(string label, T actual, T expected)
    {
        var ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (!ok) _bad++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label,-52} {Format(actual)}" +
                          (ok ? "" : $"   (期望 {Format(expected)})"));
    }

    public static void True(string label, bool cond)
    {
        if (!cond) _bad++;
        Console.WriteLine($"  {(cond ? "ok  " : "FAIL")} {label}");
    }

    public static void Note(string text) => Console.WriteLine("  --   " + text);

    public static void Info(string text) => Console.WriteLine("       " + text);

    /// <summary>把多行文本缩进着打出来，用来核对给模型的工具结果原文。</summary>
    public static void Dump(string label, string text)
    {
        Console.WriteLine("  ..   " + label);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            Console.WriteLine("       | " + line);
    }

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        string s => s.Length > 90 ? "\"" + s[..90] + "…\"" : "\"" + s + "\"",
        _ => value.ToString() ?? ""
    };

    /// <summary>测试进程固定用 UTF-8 输出，否则中文断言在默认代码页下会变成问号。</summary>
    public static void InitConsole()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向到文件时可能失败，无妨 */ }
    }
}
