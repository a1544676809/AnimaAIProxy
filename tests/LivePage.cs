using System.Diagnostics;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 真实网页抓取联调：**要联网，但不需要密钥**，所以它比 <c>live</c> 更容易跑。
///
/// 存在的理由：<see cref="BrowserFetcher"/> 那条路（起 headless Edge、CDP 握手、
/// 等渲染稳定、取 DOM）在离线回归里只能用假浏览器覆盖 —— 而它恰恰是最容易坏的一环
/// （浏览器路径、启动参数、调试端口、代理透传，任何一处不对都只有真跑一次才知道）。
///
/// 用法：
///     proxycheck livepage                          # 默认几个境内站点，两个引擎各跑一遍
///     proxycheck livepage https://a.com https://b.com
///
/// 刻意**不进 all**：它要联网、要本机有 Edge，而且会真的起一个几百 MB 的浏览器。
/// 读的是真配置（<see cref="ProxyConfig.LoadReal"/>，只读通道），所以代理设置是真的那份。
/// </summary>
public static class LivePage
{
    private static readonly string[] DefaultUrls =
    {
        "https://www.runoob.com/w3cnote/ascii.html",
        "https://learn.microsoft.com/zh-cn/dotnet/core/whats-new/dotnet-10/overview",
    };

    public static void Run(string[] args)
    {
        var urls = args.Length > 1 ? args[1..] : DefaultUrls;

        var cfg0 = ProxyConfig.LoadReal();
        // AIP_PROXY=http://127.0.0.1:10809 可以临时覆盖代理，用来验证"经代理才读得到的站点"。
        // 不改真配置（LoadReal 是只读通道）。
        var proxyOverride = Environment.GetEnvironmentVariable("AIP_PROXY");
        if (!string.IsNullOrWhiteSpace(proxyOverride)) cfg0.FetchProxy = proxyOverride.Trim();

        Harness.Section("真实网页抓取（联网；读的是真配置，只读）");
        Harness.Info($"代理：{(cfg0.FetchProxy.Length > 0 ? cfg0.FetchProxy : "（直连）")}");
        Harness.Info($"浏览器：{(BrowserFetcher.FindChromium(cfg0.FetchBrowserPath) is { Length: > 0 } p ? p : "（本机没找到）")}");
        Harness.Info("目标：" + string.Join("，", urls));

        // auto 放第一个：它才是这个工具的默认形态，也最能说明"两个引擎各自能干什么"
        // —— 同一个地址，http 失败、browser 成功时，auto 应该给出 browser 的结果。
        foreach (var engine in new[] { "auto", "http", "browser" })
        {
            Harness.Section($"引擎 = {engine}");
            foreach (var url in urls) One(engine, url, proxyOverride);
        }
    }

    private static void One(string engine, string url, string? proxyOverride)
    {
        var cfg = ProxyConfig.LoadReal();
        cfg.FetchEnabled = true;
        cfg.FetchEngine = engine;
        cfg.FetchTimeoutSeconds = 60;
        if (!string.IsNullOrWhiteSpace(proxyOverride)) cfg.FetchProxy = proxyOverride.Trim();

        if (engine == "browser" && !cfg.FetchBrowserUsable &&
            BrowserFetcher.FindChromium(cfg.FetchBrowserPath).Length == 0)
        {
            Harness.Note("跳过：本机没有 Edge/Chrome");
            return;
        }

        var sw = Stopwatch.StartNew();
        string ok, detail;
        using (var reader = new PageReader())
        {
            var r = reader.FetchAsync(cfg, url, false, 3000).GetAwaiter().GetResult();
            sw.Stop();
            ok = r.Ok ? "ok" : "FAIL";
            detail = r.Ok
                ? $"{r.Text.Length} 字符，引擎 {r.Engine}，{sw.ElapsedMilliseconds} ms" +
                  (r.Title.Length > 0 ? $"，标题《{Trim(r.Title, 40)}》" : "") +
                  (r.Truncated ? "（已截断）" : "")
                : r.Error;
            if (r.Ok && r.Note.Length > 0) detail += "  note: " + Trim(r.Note, 60);
        }

        Console.WriteLine($"  {ok,-4} {Trim(url, 60),-60} {detail}");
        if (ok == "FAIL") Harness.Note("（这条失败不一定是代码问题：站点可能确实不可达）");

        // AIP_DUMP=1 时把正文原文打出来。提取质量只能靠人眼看，
        // 而"少了一整张表格"这类问题从字符数上看不出来（字符数正常、内容缺一块）。
        if (Environment.GetEnvironmentVariable("AIP_DUMP") == "1" && ok == "ok")
        {
            using var reader = new PageReader();
            var r = reader.FetchAsync(cfg, url, false, 6000).GetAwaiter().GetResult();
            Harness.Dump($"{engine} 正文（前 6000 字符）",
                         r.Text.Length > 6000 ? r.Text[..6000] : r.Text);
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
