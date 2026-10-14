using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AnimaAIProxy.Core;

/// <summary>浏览器渲染的结果：**渲染后的 HTML**，交给 <see cref="ArticleExtractor"/> 提取。</summary>
public sealed record BrowserPage(bool Ok, string Html, string Title, string Url, string Error);

/// <summary>
/// 一次临时目录清理的结果。
///
/// **只装事实，不装显示文案**：文案（"释放 12.3 MB"这类话）由界面拼，
/// 这样回归断言的是数字，而不是一串会随文案调整而失效的字符串。
/// </summary>
public sealed record CleanupReport(int RemovedDirs, long FreedBytes, bool BrowserStopped, bool Busy);

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
/// 3. **空闲自动关** —— 超过 <c>FetchCleanupSeconds</c>（见 <see cref="CleanupSchedule"/>）
///    没人用就杀掉，**并删掉它的临时目录**，把内存和磁盘一起还回去。
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

    /// <summary>
    /// 当前会话对临时目录的占用标记（见 <see cref="ProfileLockName"/>）。
    /// 与 <see cref="_userDataDir"/> 同生共死，但**必须单独记住**：它要在删除目录之前先释放。
    /// </summary>
    private FileStream? _profileLock;
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

    /// <summary>
    /// 当前会话用的临时目录（没有会话时是空串）。
    ///
    /// 单独暴露出来是为了让"到底复用了没有"这件事**可被观察**：抓两次拿到同一个目录
    /// 就是复用，拿到两个目录就是每次都在重启。日志里只有"已启动"那一行，
    /// 而它无法区分这两种情况。
    /// </summary>
    public string CurrentProfileDir
    {
        get
        {
            lock (_stateLock) return _userDataDir ?? "";
        }
    }

    /// <summary>当前有没有活着的浏览器实例（界面/日志用）。</summary>
    public bool Running
    {
        get
        {
            lock (_stateLock) return _proc is { HasExited: false };
        }
    }

    /// <summary>
    /// 有没有"会话"：活着的进程，**或者**只是还占着临时目录的残留状态。
    ///
    /// 为什么不能只看 <see cref="Running"/>：浏览器自己崩掉/被外部杀掉时，
    /// <c>_proc.HasExited</c> 为真而目录与占用标记还在我们手里 —— 只判 Running 的话
    /// 那个目录既不会被空闲回收（早退），也扫不掉（锁还在），会一直留到下次启动。
    /// </summary>
    private bool HasSession
    {
        get
        {
            lock (_stateLock) return _proc is not null || _userDataDir is not null;
        }
    }

    public BrowserFetcher(string? configuredPath = null)
    {
        Path = FindChromium(configuredPath);

        // 启动时清掉上次留下的残留目录。判据是**那把锁**（见 ProfileLockName），
        // 不再是"比 N 小时更旧"的猜测 —— 所以这里可以无条件地全清：
        // 别的活着的实例锁着自己的目录、扫不到，能扫到的都是已经没人在用的垃圾。
        // 这也让"立即清理"这个档位在启动那一刻就是真的立即。
        var swept = SweepProfiles(TimeSpan.Zero);
        if (swept > 0) ProxyLog.Write($"清理了 {swept} 个上次遗留的浏览器临时目录");
    }

    // ---------------------------------------------------------------- 用户数据目录的回收

    /// <summary>
    /// 临时用户数据目录的前缀。**改它必须同时改 <see cref="EnumerateProfiles"/> 的判据**，
    /// 否则旧前缀的目录会永远留在 %TEMP% 里（回归里有一条断言钉住这两处一致）。
    /// </summary>
    internal const string ProfilePrefix = "aip-edge-";

    /// <summary>
    /// 会话占用标记的文件名：活着的实例一直以 <c>FileShare.None</c> 持有它。
    ///
    /// 为什么需要它 —— 这是"某个目录还有没有人在用"的**精确**答案。
    /// 早先靠猜：删除时用"比 2 小时更旧"当判据，猜大了残留堆积
    /// （实测积了 13 个目录 / 234 MB），猜小了会删掉另一个活着的实例正在用的目录。
    /// 文件锁没有这个两难：能独占打开 = 没人在用。
    ///
    /// 两个关键性质：
    /// * **进程被强杀时句柄由内核释放** —— 正是我们要的语义（强杀留下的目录可以被清掉）；
    /// * 本进程同样打不开自己持有的文件，于是"当前正在用的那个目录"自动被排除，
    ///   不需要任何特判。
    /// </summary>
    internal const string ProfileLockName = ".aip-inuse";

    internal static string NewProfileDir() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                               ProfilePrefix + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>枚举 %TEMP% 下所有属于我们的临时目录。**只认前缀，别的目录一概不碰。**</summary>
    internal static List<string> EnumerateProfiles(string? root = null)
    {
        var found = new List<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(
                         root ?? System.IO.Path.GetTempPath(), ProfilePrefix + "*"))
                found.Add(d);
        }
        catch { /* %TEMP% 读不了就当没有 —— 绝不能因此起不来 */ }
        return found;
    }

    /// <summary>
    /// 这个目录还有活着的实例在用吗。
    ///
    /// **没有锁文件就当作"没人在用"**：只有本类会创建它，缺了它意味着创建它的进程
    /// 已经不在了（或者创建失败）。后者会让一个活着的目录被删掉 —— 后果有界
    /// （那个实例下次抓取时重启浏览器）；而反过来"没有锁文件就永不清理"
    /// 会让残留永远堆着，那正是这次要修的问题。
    /// </summary>
    internal static bool ProfileInUse(string dir)
    {
        var path = System.IO.Path.Combine(dir, ProfileLockName);
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;                                          // 能独占打开 = 没人在用
        }
        catch (IOException) { return true; }                        // 被别的实例锁着
        catch (UnauthorizedAccessException) { return true; }        // 同理，别去碰它
        catch { return false; }                                     // 其它错误交给删除去失败
    }

    /// <summary>持有占用标记。失败返回 null（后果见 <see cref="ProfileInUse"/> 的取舍）。</summary>
    private static FileStream? TryLockProfile(string dir)
    {
        try
        {
            return new FileStream(System.IO.Path.Combine(dir, ProfileLockName),
                                  FileMode.Create, FileAccess.Write, FileShare.None, 1,
                                  FileOptions.DeleteOnClose);
        }
        catch { return null; }
    }

    /// <summary>目录占多少字节。清理报告要说清"释放了多少"，否则用户看不出效果。</summary>
    internal static long ProfileSize(string dir)
    {
        var total = 0L;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* 正被删的文件 */ }
            }
        }
        catch { /* 目录刚好没了 */ }
        return total;
    }

    /// <summary>
    /// 删掉一个用户数据目录，最多等 <paramref name="attempts"/> × <paramref name="sleepMs"/>。
    ///
    /// 为什么要重试：Edge 的子进程被杀之后还会短暂持有目录里的文件句柄，第一次删除
    /// 常常因共享冲突失败。**目录本来就不存在算成功** —— 调用方要的是"它没了"。
    /// </summary>
    internal static bool TryDeleteProfile(string? dir, int attempts, int sleepMs)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        for (var i = 0; i < attempts; i++)
        {
            try { Directory.Delete(dir, recursive: true); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { if (i < attempts - 1) Thread.Sleep(sleepMs); }
        }
        return !Directory.Exists(dir);
    }

    /// <summary>
    /// 清掉**没有活着的实例在用**的残留目录，返回清掉的个数。
    ///
    /// <paramref name="olderThan"/> 是第二道保险：大于零时还要求目录足够旧。
    /// 传 <see cref="TimeSpan.Zero"/> 就是"立即清理"，此时唯一判据是那把锁。
    /// <paramref name="root"/> 可换，是为了让回归在自己的临时目录里验它、不碰真的 %TEMP%。
    /// </summary>
    internal static int SweepProfiles(TimeSpan olderThan, string? root = null)
    {
        var removed = 0;
        foreach (var d in EnumerateProfiles(root))
        {
            try
            {
                if (ProfileInUse(d)) continue;                        // 有活着的实例在用
                if (olderThan > TimeSpan.Zero &&
                    Directory.GetLastWriteTimeUtc(d) > DateTime.UtcNow - olderThan) continue;
                if (TryDeleteProfile(d, 3, 200)) removed++;
            }
            catch { /* 单个目录失败不影响其它 */ }
        }
        return removed;
    }

    /// <summary>
    /// 清掉所有残留（**不需要实例**）。界面上的「立即清理」在没有服务端时走这条。
    /// <paramref name="root"/> 可换，理由与 <see cref="SweepProfiles"/> 相同（回归用）。
    /// </summary>
    public static CleanupReport CleanupLeftovers(string? root = null)
    {
        var removed = 0;
        var freed = 0L;
        foreach (var d in EnumerateProfiles(root))
        {
            try
            {
                if (ProfileInUse(d)) continue;
                var size = ProfileSize(d);
                if (TryDeleteProfile(d, 3, 200)) { removed++; freed += size; }
            }
            catch { /* 单个目录失败不影响其它 */ }
        }
        return new CleanupReport(removed, freed, false, false);
    }

    /// <summary>
    /// 立刻清理（界面上的「立即清理」按钮）。
    ///
    /// 顺序是刻意的：**先关掉浏览器，再扫残留**。浏览器活着时它那个目录被我们自己
    /// 锁着、扫不到；先 <see cref="Kill"/> 才会释放锁，那个目录才成为可清对象。
    /// 有抓取在跑时**不关**（<c>_active &gt; 0</c>），否则会打断正在进行的抓取。
    /// </summary>
    public CleanupReport CleanupNow()
    {
        var busy = Volatile.Read(ref _active) > 0;
        var stopped = false;
        var removed = 0;
        var freed = 0L;

        if (!busy && HasSession)
        {
            var wasRunning = Running;
            var own = Kill(deleteAsync: false);
            if (own is not null)
            {
                freed += ProfileSize(own);
                if (TryDeleteProfile(own, 8, 200)) removed++;
                stopped = wasRunning;
            }
        }

        var rest = CleanupLeftovers();
        return new CleanupReport(removed + rest.RemovedDirs, freed + rest.FreedBytes, stopped, busy);
    }

    /// <summary>
    /// 启动参数。抽成纯函数是为了能被离线回归钉住 ——
    /// <c>--headless=new</c> 一旦少了，浏览器就会**弹出一个可见窗口**，而进程照常工作、
    /// 抓取也照常成功，所以从功能上完全看不出问题。实测确认过：带这个参数时，
    /// 浏览器那 5 个顶层窗口全是 hidden（不带的话会是一个真的 Edge 窗口）。
    /// </summary>
    internal static IReadOnlyList<string> LaunchArgs(string userDataDir, string? proxy)
    {
        var args = new List<string>
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-sync", "--disable-background-networking",
            "--disable-features=OptimizationHints,MediaRouter,Translate,BackForwardCache,AcceptCHFrame",
            "--renderer-process-limit=1", "--window-size=1280,900", "--mute-audio",
            "--remote-debugging-port=0", "--user-data-dir=" + userDataDir,
        };
        if (!string.IsNullOrWhiteSpace(proxy)) args.Add("--proxy-server=" + proxy.Trim());
        args.Add("about:blank");
        return args;
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
    public async Task<BrowserPage> FetchAsync(string url, string proxy, int cleanupSeconds,
                                              TimeSpan timeout, CancellationToken ct)
    {
        if (!Available)
            return new BrowserPage(false, "", "", url, "本机没有找到 Edge/Chrome，浏览器引擎不可用");

        _idleSeconds = cleanupSeconds;
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

            // 0 = 立即清理：抓完就关，**不等那个定时器** —— 用户选"立即清理"
            // 就是不想让浏览器（和它的临时目录）多活一秒，而定时器最多会多留一个周期。
            // 必须确认没有别的抓取在跑，否则先结束的那一个会把后一个正在用的浏览器杀掉。
            if (_idleSeconds == 0 && Volatile.Read(ref _active) == 0) Kill();
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

    /// <summary>
    /// 起一个浏览器实例，并把这次会话占的临时目录登记下来。
    ///
    /// 占用标记（<see cref="ProfileLockName"/>）在**启动浏览器之前**就拿住：中间隔着
    /// 最多 20 秒的端口等待，而清理逻辑（启动扫描、手动清理、别的实例）随时可能在这段时间里跑 ——
    /// 晚拿的话，这段时间里这个目录在别人眼里就是"没人在用的垃圾"。
    /// 起不来时连同锁一起交还（见 <c>finally</c>）。
    /// </summary>
    private async Task StartAsync(string proxy, CancellationToken ct)
    {
        var udd = NewProfileDir();
        Directory.CreateDirectory(udd);
        lock (_stateLock) _profileLock = TryLockProfile(udd);

        var committed = false;
        try
        {
            await StartCoreAsync(udd, proxy, ct).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // 不释放的话那个目录既扫不掉（被自己锁着）也删不掉，会永久留在 %TEMP% 里 ——
                // 比"这次没启动起来"更糟。
                lock (_stateLock)
                {
                    try { _profileLock?.Dispose(); } catch { }
                    _profileLock = null;
                }
                TryDeleteProfile(udd, 3, 200);
            }
        }

        // 日志里说清"什么时候会清理"：这一行是排查"它为什么一直占着内存"的唯一线索，
        // 而"空闲 立即 后清理"这种拼出来的话读不通，所以三个特殊档位单独写。
        ProxyLog.Write(_idleSeconds switch
        {
            < 0 => $"浏览器引擎已启动（{System.IO.Path.GetFileName(Path)}，不会自动清理）",
            0 => $"浏览器引擎已启动（{System.IO.Path.GetFileName(Path)}，每次抓完立刻清理）",
            _ => $"浏览器引擎已启动（{System.IO.Path.GetFileName(Path)}，" +
                 $"空闲 {CleanupSchedule.Humanize(_idleSeconds)} 后清理）",
        });
    }

    private async Task StartCoreAsync(string udd, string proxy, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in LaunchArgs(udd, proxy)) psi.ArgumentList.Add(a);

        // 代理交给浏览器自己走。**这比在 .NET 侧做要可靠**：实测同一个代理，
        // .NET 的 HttpClient 对 en.wikipedia.org 直接 SSL 握手失败，而 Edge 走通了
        // （拿到 11969 字符正文）——浏览器的 TLS 栈与代理协商方式跟 .NET 不一样。

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
            _idleTimer ??= new Timer(_ => ReapIfIdle(), null, 10_000, 10_000);
        }
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

    /// <summary>
    /// 空闲回收：把那份 400–500 MB 内存和临时目录一起还回去。有请求在跑时绝不回收。
    ///
    /// 清理发生在到点后的**一个定时器周期内**（周期 10 秒），所以实际比配置值最多晚 10 秒。
    /// 为了这点精度去调定时器周期不值得，但也不能像早先那样 30 秒一查 ——
    /// "立即清理"之外的档位里 5 分钟是最常选的，多留 30 秒是能被感觉到的。
    /// </summary>
    private void ReapIfIdle()
    {
        try
        {
            if (_disposed) return;
            if (_idleSeconds < 0) return;                        // 永不清理
            if (Volatile.Read(ref _active) > 0) return;
            if ((DateTime.UtcNow - _lastUsedUtc).TotalSeconds < _idleSeconds) return;
            if (!HasSession) return;

            ProxyLog.Write(_idleSeconds == 0
                ? "浏览器引擎已关闭并清掉临时目录（设置为立即清理）"
                : $"浏览器引擎空闲超过 {CleanupSchedule.Humanize(_idleSeconds)}，已关闭并清掉临时目录");
            Kill();
        }
        catch { /* 定时器回调里绝不抛 */ }
    }

    /// <summary>
    /// 杀掉浏览器实例并**返回它的用户数据目录**（没有实例时返回 null）。
    ///
    /// <paramref name="deleteAsync"/> 为 false 时**不删目录**，由调用方同步删 ——
    /// 因为 <see cref="Dispose"/> 与 <see cref="CleanupNow"/> 都要拿到删除结果
    /// （一个是"退出前必须删掉"，一个是"要如实报告释放了多少"），
    /// 而后台任务在进程即将退出时必然跑不完。
    /// </summary>
    private string? Kill(bool deleteAsync = true)
    {
        string? udd;
        lock (_stateLock)
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;

            // 释放占用标记，**必须在删除目录之前**：Windows 上被独占打开的文件删不掉，
            // 顺序反了会让每次删除都失败，而症状只是"临时目录一直在"。
            try { _profileLock?.Dispose(); } catch { }
            _profileLock = null;

            udd = _userDataDir;
            _userDataDir = null;
        }

        // 用户数据目录里有几百个文件，删不掉不影响功能，但别让它堆在 %TEMP% 里。
        // 放在锁外：删除要重试好几秒，不该占着状态锁。
        if (udd is not null && deleteAsync) _ = Task.Run(() => TryDeleteProfile(udd, 20, 250));

        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(new InvalidOperationException("浏览器已关闭"));
            _pending.Clear();
        }
        return udd;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _idleTimer?.Dispose(); } catch { }

        // 同步删：Kill 里那条删除路径是后台任务，而 Dispose 之后进程往往**立刻退出**
        // （应用关闭、回归跑完），后台任务根本没机会跑完 —— 实测就是这样在 %TEMP% 里
        // 留下了 13–55 MB 的残留目录。
        var udd = Kill(deleteAsync: false);
        TryDeleteProfile(udd, 8, 200);

        _startGate.Dispose();
        _sendGate.Dispose();
    }
}
