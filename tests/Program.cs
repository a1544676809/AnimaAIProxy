using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 回归入口。用法与 corecheck 一致：
///
///     proxycheck vocab      # 词库内核：搜索 / 归类 / 工具结果文本（缺库时退出码 2）
///     proxycheck tools      # 工具声明与合并（纯逻辑，无需任何外部输入）
///     proxycheck loop       # 透明工具循环：自有调用执行、外来调用放行、影子历史
///     proxycheck stream     # 流式工具循环：正文实时转发、tool_call 分片拼装、不劫持
///     proxycheck merge      # 请求体改写：思考字段一律不劫持
///     proxycheck providers  # 多模型提供商：请求头解析、按模型名路由、模型表聚合、旧配置迁移
///     proxycheck search     # 联网搜索：注入门槛、五个后端的请求形状与解析、回灌截断
///     proxycheck pageread   # 网页获取：正文提取、编码、内网拦截、双引擎自动降级、代理生效
///     proxycheck tokens     # 代理令牌与鉴权
///     proxycheck log        # 日志：摘要进界面面板，异常栈只进文件
///     proxycheck server     # HTTP 端点：真的起一次 Kestrel，走一遍路由与鉴权
///     proxycheck mcp        # MCP：协议封套、工具清单过滤、鉴权/405/Origin，真起一次 Kestrel
///     proxycheck e2e        # 端到端：客户端 → 代理 → 桩上游，含流式分块到达
///     proxycheck all        # 上面全部（默认）
///
/// 另有一条**要联网、要真密钥、会花真 token** 的联调，刻意不进 all：
///
///     proxycheck live       # 客户端 → 代理 → 真实上游，看真实模型认不认我们的工具
///     proxycheck livepage   # 真实网页抓取：两个引擎各跑一遍（要联网，不需要密钥）
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Harness.InitConsole();
        var which = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "all";

        GuardUserData();

        Console.WriteLine("AnimaAIProxy 回归：" + which);

        switch (which)
        {
            case "vocab": Vocab.Run(args); break;
            case "tools": Tools.Run(); break;
            case "loop": Loop.Run(); break;
            case "stream": StreamLoopCheck.Run(); break;
            case "merge": Merge.Run(); break;
            case "providers": Providers.Run(); break;
            case "search": Search.Run(); break;
            case "pageread": PageRead.Run(); break;
            case "tokens": Tokens.Run(); break;
            case "log": LogCheck.Run(); break;
            case "server": Server.Run(); break;
            case "mcp": Mcp.Run(); break;
            case "e2e": E2E.Run(args); break;
            case "live": Live.Run(args); break;
            case "livepage": LivePage.Run(args); break;
            case "all":
                Vocab.Run(args);
                Tools.Run();
                Loop.Run();
                StreamLoopCheck.Run();
                Merge.Run();
                Providers.Run();
                Search.Run();
                PageRead.Run();
                Tokens.Run();
                LogCheck.Run();
                Server.Run();
                Mcp.Run();
                E2E.Run(args);
                break;
            default:
                Console.WriteLine("未知的检查项：" + which);
                Console.WriteLine("可用：vocab | tools | loop | stream | merge | providers | search | pageread | tokens | log | server | mcp | e2e | all | live | livepage");
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

    /// <summary>
    /// 把配置与令牌库**全局**重定向到临时目录，在跑任何一节之前。
    ///
    /// 为什么必须在这里做，而不是让各节自己重定向：这已经出过一次真实事故——
    /// <c>ProxyServer</c> 在"按需拉取模型表"时会调用 <see cref="ProxyConfig.Save"/>，
    /// 于是跑一次 <c>e2e</c> 就把用户的真配置盖成了测试用的临时配置，
    /// 站点与密钥全没了、且没有任何提示。逐节重定向的问题是**它是可选的**：
    /// 下次新写一节忘了写，事故就重演，而且同样无声。
    ///
    /// 放在这里之后，"忘了重定向"这个错误在结构上就不可能发生。
    /// 需要读真配置的只有 <c>live</c>，它走 <see cref="ProxyConfig.LoadReal"/>（只读）。
    /// </summary>
    private static void GuardUserData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aip-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        ProxyConfig.FilePathOverride = Path.Combine(dir, "proxy.config.json");
        TokenStore.FilePathOverride = Path.Combine(dir, "proxy.tokens.json");
    }
}
