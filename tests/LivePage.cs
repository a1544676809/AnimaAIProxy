using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AnimaAIProxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        Lifecycle();
    }

    // ---------------------------------------------------------------- 浏览器实例与临时目录的生命周期

    /// <summary>
    /// 真实浏览器实例与临时目录的复用 / 清理。
    ///
    /// 为什么必须真跑一次：这一整套行为（"这段时间内复用同一个实例"、"到点关掉并删掉目录"）
    /// 只有在真的起了 Edge、真的写了几百个文件的用户数据目录之后才成立 ——
    /// 离线回归里的假浏览器没有目录、没有进程，覆盖不到任何一条。
    ///
    /// 用**本机桩页面**而不是外网站点：这件事与网络无关，而桩页面让它完全确定、可重复。
    /// </summary>
    private static void Lifecycle()
    {
        Harness.Section("浏览器实例与临时目录的生命周期（本机桩页面，真起 Edge）");

        if (BrowserFetcher.FindChromium("").Length == 0)
        {
            Harness.Note("跳过：本机没有 Edge/Chrome");
            return;
        }

        using var server = StartStubServer();
        var url = server.Url;
        var before = ProfileDirs();
        Harness.Info($"桩页面：{url}；当前 %TEMP% 里的 aip-edge-* 目录：{before.Count} 个");

        // 1) 空闲 300 秒 = 复用：抓两次必须是同一个目录、同一个实例
        using (var fetcher = new BrowserFetcher())
        {
            var r1 = Fetch(fetcher, url, 300);
            var dir1 = fetcher.CurrentProfileDir;
            Harness.Eq("第一次抓取成功", r1.Ok, true);
            Harness.True("第一次抓取后有一个临时目录", dir1.Length > 0);
            Harness.Eq("临时目录真的在磁盘上", Directory.Exists(dir1), true);

            var r2 = Fetch(fetcher, url, 300);
            var dir2 = fetcher.CurrentProfileDir;
            Harness.Eq("第二次抓取成功", r2.Ok, true);
            Harness.Eq("第二次复用了同一个临时目录（这就是「复用」）", dir2, dir1);
            Harness.Eq("实例还活着（没到清理时间）", fetcher.Running, true);

            // 2) 「立即清理」按钮：关掉浏览器并删掉目录
            var report = fetcher.CleanupNow();
            Harness.Eq("手动清理报告关掉了浏览器", report.BrowserStopped, true);
            Harness.True("手动清理报告清掉了目录", report.RemovedDirs >= 1);
            Harness.Eq("手动清理后实例没了", fetcher.Running, false);
            Harness.Eq("手动清理后目录也没了", Directory.Exists(dir1), false);
        }

        // 3) 立即清理档位（0 秒）：抓完就该自己关掉并删掉目录
        string immediateDir;
        using (var fetcher = new BrowserFetcher())
        {
            var r = Fetch(fetcher, url, 0);
            Harness.Eq("立即清理档位下抓取成功", r.Ok, true);
            immediateDir = fetcher.CurrentProfileDir;
            Harness.Eq("抓完就关掉了实例（0 秒 = 不保留复用）", fetcher.Running, false);
            Harness.Eq("抓完就把目录交还了", immediateDir, "");
        }

        // 4) 收尾核对：不留下任何新增目录
        Thread.Sleep(500);
        var after = ProfileDirs();
        var leaked = after.Where(d => !before.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        Harness.Eq("这一节没有留下新的残留目录", leaked.Count, 0);
        if (leaked.Count > 0)
            foreach (var d in leaked) Harness.Note("残留：" + d);
    }

    private static BrowserPage Fetch(BrowserFetcher fetcher, string url, int cleanupSeconds) =>
        fetcher.FetchAsync(url, "", cleanupSeconds, TimeSpan.FromSeconds(60), CancellationToken.None)
               .GetAwaiter().GetResult();

    private static List<string> ProfileDirs() => BrowserFetcher.EnumerateProfiles();

    /// <summary>
    /// 一个只回一小段 HTML 的本机服务。**刻意不用外网站点**：这一节验的是生命周期，
    /// 不是网络，桩页面让它确定、可重复、也不受代理影响。
    /// </summary>
    private static StubPageServer StartStubServer() => StubPageServer.Start();

    private sealed class StubPageServer : IDisposable
    {
        private WebApplication? _app;
        public string Url { get; private set; } = "";

        public static StubPageServer Start()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            b.Logging.ClearProviders();
            b.Logging.SetMinimumLevel(LogLevel.None);
            b.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, port));

            var app = b.Build();
            app.MapGet("/", () => Results.Content(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>桩页面</title></head>" +
                "<body><h1>标题</h1><p>" + new string('文', 400) + "</p></body></html>",
                "text/html; charset=utf-8"));
            app.Start();

            return new StubPageServer { _app = app, Url = $"http://127.0.0.1:{port}/" };
        }

        public void Dispose()
        {
            try { _app?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _app?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
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
