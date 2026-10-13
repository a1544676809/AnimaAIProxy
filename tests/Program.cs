namespace AnimaAIProxy.Tests;

/// <summary>
/// 回归入口。用法与 corecheck 一致：
///
///     proxycheck vocab      # 词库内核：搜索 / 归类 / 工具结果文本（缺库时退出码 2）
///     proxycheck tools      # 工具声明与合并（纯逻辑，无需任何外部输入）
///     proxycheck loop       # 透明工具循环：自有调用执行、外来调用放行、影子历史
///     proxycheck stream     # 流式工具循环：正文实时转发、tool_call 分片拼装、不劫持
///     proxycheck merge      # 请求体改写：思考字段一律不劫持
///     proxycheck tokens     # 代理令牌与鉴权
///     proxycheck server     # HTTP 端点：真的起一次 Kestrel，走一遍路由与鉴权
///     proxycheck e2e        # 端到端：客户端 → 代理 → 桩上游，含流式分块到达
///     proxycheck all        # 上面全部（默认）
///
/// 另有一条**要联网、要真密钥、会花真 token** 的联调，刻意不进 all：
///
///     proxycheck live       # 客户端 → 代理 → 真实上游，看真实模型认不认我们的工具
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
            case "stream": StreamLoopCheck.Run(); break;
            case "merge": Merge.Run(); break;
            case "tokens": Tokens.Run(); break;
            case "server": Server.Run(); break;
            case "e2e": E2E.Run(args); break;
            case "live": Live.Run(args); break;
            case "all":
                Vocab.Run(args);
                Tools.Run();
                Loop.Run();
                StreamLoopCheck.Run();
                Merge.Run();
                Tokens.Run();
                Server.Run();
                E2E.Run(args);
                break;
            default:
                Console.WriteLine("未知的检查项：" + which);
                Console.WriteLine("可用：vocab | tools | loop | merge | tokens | server | e2e | all | live");
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
