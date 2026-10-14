using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AnimaAIProxy.Core;

/// <summary>浏览器渲染的结果：**渲染后的 HTML**，交给 <see cref="ArticleExtractor"/> 提取。</summary>
public sealed record BrowserPage(bool Ok, string Html, string Title, string Url, string Error);

/// <summary>
/// 浏览器引擎：驱动一个 headless Chromium（默认用系统已装的 Edge）渲染页面，拿回**渲染后的 DOM**。
///
/// 为什么走 CDP 而不是 WebView2：WebView2 要求宿主有 UI 线程与消息泵，而本代理的抓取
/// 发生在 Kestrel 的请求线程上。CDP 只是一条到 localhost 的 WebSocket，**与 UI 线程完全无关**，
/// 而且浏览器是独立进程 —— 崩了也带不走代理进程。
///
/// 为什么不用 Playwright / PuppeteerSharp：它们要额外下载一整套 Chromium（150 MB+），
/// 而 Edge 在 Windows 上基本都已经装好了。**磁盘成本 0**，这也是 AnimaCaptioner
/// "刻意不用 WebView2"的同一思路的延续：能借现成基础设施就别自带一套。
///
/// 代价必须说清楚（实测，非推断）：一个 headless Edge 是 **16–18 个进程、
/// 实打实占 420–530 MB 物理内存**（default 528 MB / 精简 flag 423 MB）。
/// 所以这里的策略是**把这份开销限制在真的需要它的那几分钟里**：
///
/// 1. **懒启动** —— 第一次真要用浏览器时才起，没用过就一个进程都不起；
/// 2. **跨请求复用** —— 一个实例服务所有抓取，省掉每次约 0.7 秒的启动；
/// 3. **空闲自动关** —— 超过 <c>FetchBrowserIdleSeconds</c> 没人用就杀掉，把内存还回去。
///
/// 三条合起来的效果：只抓静态文档的用户**永远不会付这 500 MB**。
///
/// 一个刻意不做的取舍：**不去读注册表**找 Edge。`Microsoft.Win32.Registry`
/// 在纯 `net10.0`（不带 -windows）里不可用，而 tests/proxycheck.csproj 正是这个 TFM
/// 并且直接编译 Core/*.cs —— 用了它，那道"Core 不许依赖 Windows 专属 API"的守卫
/// 就失效了。已知路径 + PATH 查找已经覆盖所有真实安装方式。
/// </summary>
public sealed class BrowserFetcher : IBrowserEngine, IDisposable
{
    private readonly object _stateLock = new();
    private Process? _proc;
    private string? _userDataDir;
    private ClientWebSocket? _ws;
    private Task? _reader;
    private Timer? _idleTimer;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private int _active;             // 正在抓取的请求数：>0 时绝不允许空闲关闭
    private int _id;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _idleSeconds = 300;
    private bool _disposed;

    /// <summary>可执行文件路径（空 = 没找到）。</summary>
    public string Path { get; private set; } = "";

    /// <summary>本机有没有可用的 Chromium。没有时 <see cref="ProxyConfig.FetchReady"/> 会拒绝下发工具。</summary>
    public bool Available => Path.Length > 0;

    /// <summary>当前有没有活着的浏览器实例（界面/日志用）。</summary>
    public bool Running
    {
        get
        {
            lock (_stateLock) return _proc is { HasExited: false };
        }
    }

    public BrowserFetcher(string? configuredPath = null)
    {
        Path = FindChromium(configuredPath);
    }

    /// <summary>
    /// 找一个 Chromium。顺序：配置里指定的 → 常见安装路径 → PATH。
    /// 配置项存在是为了让用户能指向 Chrome 或别的 Chromium —— 用到的 flag 都是通用的。
    /// </summary>
    public static string FindChromium(string? configuredPath)
    {
        var configured = (configuredPath ?? "").Trim().Trim('"');
        if (configured.Length > 0 && File.Exists(configured)) return configured;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("LocalAppData"),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var rel in new[]
            {
                @"Microsoft\Edge\Application\msedge.exe",
                @"Google\Chrome\Application\chrome.exe",
                @"Chromium\Application\chrome.exe",
            })
            {
                var p = System.IO.Path.Combine(root!, rel);
                if (File.Exists(p)) return p;
            }
        }

        // PATH 里找（便携版、绿色版走这条）
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
            {
                try
                {
                    var p = System.IO.Path.Combine(dir.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { /* PATH 里有非法字符的目录，跳过 */ }
            }
        }
        return "";
    }

    /// <summary>
    /// 按配置里的路径重新找一个浏览器。
    ///
    /// 存在的理由：界面上的"浏览器可执行文件"是一个可以随时改的框，而
    /// <see cref="Path"/> 只在构造时算一次。不重新解析的话，用户填了路径、
    /// 界面状态也显示"找到了"，实际抓取却还用着构造时那个空路径，
    /// 回一句"浏览器引擎不可用" —— 一个界面与实际互相矛盾的、很难查的症状。
    /// 正在运行时不换（换了会让活着的实例指向另一个可执行文件）。
    /// </summary>
    public void RefreshPath(string? configuredPath)
    {
        if (Running) return;
        var found = FindChromium(configuredPath);
        if (found.Length > 0) Path = found;
    }

    /// <summary>
    /// 渲染一个页面。失败一律返回 <c>Ok=false</c> + 原因，**不抛异常**
    /// （与 <see cref="SearchClient"/>、<see cref="ToolHost"/> 的约定一致：
    /// 一次抓取失败只应该让模型收到一句"这次读不到，原因是……"）。
    /// </summary>
    public async Task<BrowserPage> FetchAsync(string url, string proxy, int idleSeconds,
                                              TimeSpan timeout, CancellationToken ct)
    {
        if (!Available)
            return new BrowserPage(false, "", "", url, "本机没有找到 Edge/Chrome，浏览器引擎不可用");

        _idleSeconds = idleSeconds;
        Interlocked.Increment(ref _active);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                BrowserPage page;
                try
                {
                    await EnsureStartedAsync(proxy, ct).ConfigureAwait(false);
                    Touch();
                    page = await FetchCoreAsync(url, timeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new BrowserPage(false, "", "", url, "抓取被取消或超时");
                }
                catch (Exception ex)
                {
                    // 浏览器侧的任何异常都不能带走代理进程：杀掉实例，下次重新起。
                    ProxyLog.Write("浏览器抓取失败：" + ex.Message);
                    Kill();
                    return new BrowserPage(false, "", "", url, ex.Message);
                }

                // 瞬时的连接层错误重试一次。**这一条是实测逼出来的**：
                // 经本地代理抓 en.wikipedia.org 时，同一个地址三次里会有一次
                // 直接 net::ERR_CONNECTION_CLOSED（代理侧换了节点/抖了一下），
                // 而重启一个浏览器实例再试通常就通了。没有这一次重试，
                // 自动降级会在"HTTP 失败 + 浏览器抖了一下"时给出双重失败 ——
                // 而那次抓取本来是可以成功的。
                if (attempt == 0 && IsTransientNav(page.Error))
                {
                    ProxyLog.Write("浏览器抓取遇到瞬时连接错误，重试一次：" + page.Error);
                    Kill();
                    continue;
                }
                return page;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            Touch();
        }
    }

    /// <summary>
    /// 这个失败值不值得重试一次。
    ///
    /// **只认连接层的瞬时错误**，刻意不含两类：
    /// * <c>ERR_NAME_NOT_RESOLVED</c> / <c>ERR_INVALID_URL</c> —— 域名或地址本身有问题，重试必然一样；
    /// * <c>ERR_TIMED_OUT</c> —— 它已经吃掉了整个超时，再等一遍就是两倍延迟，
    ///   而超时通常意味着站点真的慢或不通，不是抖了一下。
    ///
    /// 上面这些错误都在一秒内返回，所以这一次重试的代价很小。
    /// </summary>
    internal static bool IsTransientNav(string error)
    {
        if (!error.StartsWith("导航失败：net::ERR_", StringComparison.Ordinal)) return false;
        foreach (var code in new[]
                 {
                     "ERR_CONNECTION_CLOSED", "ERR_CONNECTION_RESET", "ERR_CONNECTION_ABORTED",
                     "ERR_CONNECTION_FAILED", "ERR_EMPTY_RESPONSE", "ERR_SOCKET_NOT_CONNECTED"
                 })
            if (error.Contains(code, StringComparison.Ordinal)) return true;
        return false;
    }

    private async Task<BrowserPage> FetchCoreAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var created = await CallAsync("Target.createTarget", new { url = "about:blank" }, null, ct)
            .ConfigureAwait(false);
        var targetId = created.GetProperty("result").GetProperty("targetId").GetString();
        if (string.IsNullOrEmpty(targetId))
            return new BrowserPage(false, "", "", url, "浏览器没有返回 targetId");

        string? session = null;
        try
        {
            var attached = await CallAsync("Target.attachToTarget", new { targetId, flatten = true }, null, ct)
                .ConfigureAwait(false);
            session = attached.GetProperty("result").GetProperty("sessionId").GetString();
            if (string.IsNullOrEmpty(session))
                return new BrowserPage(false, "", "", url, "浏览器没有返回 sessionId");

            await CallAsync("Page.enable", null, session, ct).ConfigureAwait(false);
            await CallAsync("Runtime.enable", null, session, ct).ConfigureAwait(false);

            var nav = await CallAsync("Page.navigate", new { url }, session, ct).ConfigureAwait(false);
            if (nav.GetProperty("result").TryGetProperty("errorText", out var et))
                return new BrowserPage(false, "", "", url, "导航失败：" + et.GetString());

            var problem = await WaitForContentAsync(session, timeout, ct).ConfigureAwait(false);
            if (problem.Length > 0)
                return new BrowserPage(false, "", "", url, problem);

            var title = await EvalAsync("document.title", session, ct).ConfigureAwait(false);
            var finalUrl = await EvalAsync("location.href", session, ct).ConfigureAwait(false);
            var html = await EvalAsync(CleanHtmlJs, session, ct).ConfigureAwait(false);

            if (html.Length == 0)
                return new BrowserPage(false, "", title, finalUrl, "渲染完成但取不到页面内容");

            return new BrowserPage(true, html, title, finalUrl.Length > 0 ? finalUrl : url, "");
        }
        finally
        {
            try { await CallAsync("Target.closeTarget", new { targetId }, null, ct).ConfigureAwait(false); }
            catch { /* 关不掉就算了，浏览器实例整体会被空闲回收 */ }
        }
    }

    /// <summary>
    /// 在页面里剥掉 script/style 再取 outerHTML。
    ///
    /// 为什么在 JS 里剥、而不是传回 C# 再剥：<see cref="ArticleExtractor"/> 反正也会剥，
    /// 但那要先把几万字符的 JS 源码经 WebSocket 传一遍。这里是**纯粹省传输**。
    /// </summary>
    private const string CleanHtmlJs =
        "(() => { const c = document.documentElement.cloneNode(true); " +
        "c.querySelectorAll('script,style,noscript,template,link,meta,svg,canvas,iframe').forEach(e => e.remove()); " +
        "return c.outerHTML; })()";

    /// <summary>
    /// 等页面"稳定下来"。三个条件都满足才算好：
    ///
    /// 1. <c>readyState === 'complete'</c>；
    /// 2. 正文长度连续两次相同 —— SPA 的水合是异步的，load 事件之后内容还会长出来；
    /// 3. **不是 Cloudflare 之类的人机校验页**。这一条是实测逼出来的：
    ///    danbooru 经代理返回的就是一张 <c>403 "Just a moment..."</c> 挑战页，
    ///    它同样"加载完成、长度稳定"，只是里面一个字的正文都没有。
    ///    校验通常几秒内自动放行，所以这里**继续等**而不是立刻放弃。
    /// </summary>
    private async Task<string> WaitForContentAsync(string session, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastLen = -1;
        var stable = 0;
        var sawChallenge = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct).ConfigureAwait(false);

            var state = await EvalAsync("document.readyState", session, ct).ConfigureAwait(false);
            var probe = await EvalAsync(ProbeJs, session, ct).ConfigureAwait(false);
            var (len, challenge) = ParseProbe(probe);

            if (challenge) { sawChallenge = true; stable = 0; lastLen = len; continue; }

            if (state == "complete" && len > 0 && len == lastLen)
            {
                if (++stable >= 2) return "";
            }
            else stable = 0;
            lastLen = len;
        }

        if (sawChallenge)
            return "页面卡在 Cloudflare 之类的人机校验上，等待超时（校验没有通过）";
        return "页面在超时前没有渲染出内容";
    }

    /// <summary>一次求值同时拿"正文长度"和"是不是校验页"，省一半往返。</summary>
    private const string ProbeJs =
        "(() => { const t = document.body ? document.body.innerText : ''; " +
        "const c = /just a moment|checking your browser|cf-browser-verification|" +
        "enable javascript and cookies|attention required|verify you are human|" +
        "正在检查您的浏览器|请稍候/i.test(t.slice(0, 600) + ' ' + document.title); " +
        "return t.length + '|' + (c ? '1' : '0'); })()";

    private static (int Len, bool Challenge) ParseProbe(string probe)
    {
        var bar = probe.IndexOf('|');
        if (bar < 0) return (0, false);
        return (int.TryParse(probe[..bar], out var n) ? n : 0, probe[(bar + 1)..].StartsWith('1'));
    }

    // ---------------------------------------------------------------- CDP 管道

    private async Task EnsureStartedAsync(string proxy, CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_proc is { HasExited: false } && _ws is { State: WebSocketState.Open }) return;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_proc is { HasExited: false } && _ws is { State: WebSocketState.Open }) return;
            }
            Kill();
            await StartAsync(proxy, ct).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }
    }

    private async Task StartAsync(string proxy, CancellationToken ct)
    {
        var udd = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "aip-edge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(udd);

        var psi = new ProcessStartInfo(Path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-sync", "--disable-background-networking",
            "--disable-features=OptimizationHints,MediaRouter,Translate,BackForwardCache,AcceptCHFrame",
            "--renderer-process-limit=1", "--window-size=1280,900", "--mute-audio",
            "--remote-debugging-port=0", "--user-data-dir=" + udd,
        })
            psi.ArgumentList.Add(a);

        // 代理交给浏览器自己走。**这比在 .NET 侧做要可靠**：实测同一个代理，
        // .NET 的 HttpClient 对 en.wikipedia.org 直接 SSL 握手失败，而 Edge 走通了
        // （拿到 11969 字符正文）——浏览器的 TLS 栈与代理协商方式跟 .NET 不一样。
        if (!string.IsNullOrWhiteSpace(proxy)) psi.ArgumentList.Add("--proxy-server=" + proxy.Trim());
        psi.ArgumentList.Add("about:blank");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("浏览器没能启动");

        // --remote-debugging-port=0 让浏览器自己挑空闲端口并写进 DevToolsActivePort。
        // 固定端口会撞上用户自己开着的 Edge，或者上一次没杀干净的实例。
        var portFile = System.IO.Path.Combine(udd, "DevToolsActivePort");
        string? port = null;
        for (var i = 0; i < 400; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(portFile))
            {
                try
                {
                    var first = File.ReadAllLines(portFile).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(first) && int.TryParse(first, out _)) { port = first; break; }
                }
                catch { /* 文件刚建、还没写完 */ }
            }
            if (proc.HasExited)
                throw new InvalidOperationException($"浏览器提前退出（exit={proc.ExitCode}）");
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        if (port is null) throw new InvalidOperationException("等不到浏览器的调试端口");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var version = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
        var wsUrl = JsonDocument.Parse(version).RootElement.GetProperty("webSocketDebuggerUrl").GetString()
                    ?? throw new InvalidOperationException("浏览器没有给出 webSocketDebuggerUrl");

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await ws.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);

        lock (_stateLock)
        {
            _proc = proc;
            _userDataDir = udd;
            _ws = ws;
            _reader = Task.Run(() => ReaderAsync(ws));
            _idleTimer ??= new Timer(_ => ReapIfIdle(), null, 30_000, 30_000);
        }
        ProxyLog.Write($"浏览器引擎已启动（{System.IO.Path.GetFileName(Path)}，空闲 {_idleSeconds} 秒后自动关闭）");
    }

    private async Task ReaderAsync(ClientWebSocket ws)
    {
        var buf = new byte[1 << 16];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                // 渲染后的 DOM 可以到几 MB，必须一直读到 EndOfMessage。
                if (!r.EndOfMessage)
                {
                    if (sb.Length > 24 * 1024 * 1024) { sb.Clear(); break; }
                    continue;
                }

                var text = sb.ToString();
                sb.Clear();
                Dispatch(text);
            }
        }
        catch { /* 实例被杀时走到这里，正常路径 */ }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending) { _pending.TryGetValue(id, out tcs); _pending.Remove(id); }
                tcs?.TrySetResult(root.Clone());
            }
            // 事件（Page.* / Runtime.*）这里不消费：等待靠轮询 readyState + 正文长度，
            // 比维护一整套事件状态机简单得多，而且对 SPA 的异步水合更稳。
        }
        catch { /* 不是 JSON 就丢掉 */ }
    }

    private async Task<JsonElement> CallAsync(string method, object? prms, string? session, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("浏览器未启动");
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        var payload = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
        if (prms is not null) payload["params"] = prms;
        if (session is not null) payload["sessionId"] = session;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
            .ConfigureAwait(false);
        if (done != tcs.Task) throw new TimeoutException($"{method} 无响应");
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<string> EvalAsync(string expression, string session, CancellationToken ct)
    {
        var r = await CallAsync("Runtime.evaluate",
            new { expression, returnByValue = true, awaitPromise = false }, session, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("result", out var res) || !res.TryGetProperty("result", out var inner))
            return "";
        if (!inner.TryGetProperty("value", out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => ""
        };
    }

    private void Touch() => _lastUsedUtc = DateTime.UtcNow;

    /// <summary>空闲回收：把那份 400–500 MB 还回去。有请求在跑时绝不回收。</summary>
    private void ReapIfIdle()
    {
        try
        {
            if (_disposed) return;
            if (Volatile.Read(ref _active) > 0) return;
            if ((DateTime.UtcNow - _lastUsedUtc).TotalSeconds < _idleSeconds) return;
            if (!Running) return;

            ProxyLog.Write("浏览器引擎空闲，已关闭并释放内存");
            Kill();
        }
        catch { /* 定时器回调里绝不抛 */ }
    }

    private void Kill()
    {
        lock (_stateLock)
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;

            var udd = _userDataDir;
            _userDataDir = null;
            if (udd is not null)
            {
                // 用户数据目录里有几百个文件，删不掉不影响功能，但别让它堆在 %TEMP% 里。
                _ = Task.Run(() =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        try { Directory.Delete(udd, recursive: true); return; }
                        catch { Thread.Sleep(300); }
                    }
                });
            }
        }
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(new InvalidOperationException("浏览器已关闭"));
            _pending.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _idleTimer?.Dispose(); } catch { }
        Kill();
        _startGate.Dispose();
        _sendGate.Dispose();
    }
}
