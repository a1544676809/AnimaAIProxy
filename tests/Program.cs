namespace AnimaAIProxy.Tests;

/// <summary>
/// 回归入口。用法与 corecheck 一致：
///
///     proxycheck vocab      # 词库内核：搜索 / 归类 / 工具结果文本（缺库时退出码 2）
///     proxycheck tools      # 工具声明与合并（纯逻辑，无需任何外部输入）
///     proxycheck loop       # 透明工具循环：自有调用执行、外来调用放行、影子历史
///     proxycheck merge      # 请求体改写：思考字段一律不劫持
///     proxycheck tokens     # 代理令牌与鉴权
///     proxycheck all        # 上面全部（默认）
///
/// 一条**要联网、要真实上游**的诊断不在这组里，见 README。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Harness.InitConsole();
        var which = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "all";

        Console.WriteLine("AnimaAIProxy 回归：" + which);

        switch (which)
        {
            case "vocab": Vocab.Run(args); break;
            case "tools": Tools.Run(); break;
            case "loop": Loop.Run(); break;
            case "merge": Merge.Run(); break;
            case "tokens": Tokens.Run(); break;
            case "all":
                Vocab.Run(args);
                Tools.Run();
                Loop.Run();
                Merge.Run();
                Tokens.Run();
                break;
            default:
                Console.WriteLine("未知的检查项：" + which);
                Console.WriteLine("可用：vocab | tools | loop | merge | tokens | all");
                return 2;
        }

        Console.WriteLine();
        if (Harness.Failures == 0)
        {
            Console.WriteLine("全部通过。");
            return 0;
        }
        Console.WriteLine($"{Harness.Failures} 项失败。");
        return 1;
    }
}
