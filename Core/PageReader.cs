using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimaAIProxy.Core;

/// <summary>一次抓取的结果。<see cref="Engine"/> 是 <c>http</c> 或 <c>browser</c>，用来解释这次是怎么读到的。</summary>
public sealed record FetchResult(
    bool Ok, string Url, string Title, string Text, string Engine,
    string Error, bool Truncated, string Note);

/// <summary>
/// 网页获取：<c>web_fetch</c> 工具的执行体。
///
/// **两个引擎，一个工具**：
/// * <b>http</b>（默认，轻量）—— <see cref="HttpClient"/> 取 HTML，<see cref="ArticleExtractor"/> 提正文。
///   约 10–20 MB 内存、0.3–0.7 秒一页。文档站、博客、静态页全都够用。
/// * <b>browser</b>（重，按需）—— <see cref="BrowserFetcher"/> 驱动 headless Edge 渲染后取 DOM。
///   实测 **420–530 MB、1.2–2.2 秒**，但只有它能读前端渲染页面。
///
/// <b>自动降级（默认）</b>：先走 http，满足下列任一条件才升级到 browser ——
/// 每一條都是实测逼出来的，不是想当然：
///
/// 1. **正文薄而原始 HTML 大**（&lt; 200 字符 vs &gt; 20 KB）—— 典型的前端渲染页面：
///    HTML 里装着一整套 JS 与样式，正文要等 JS 跑完才出现。
/// 2. **人机校验页**（Cloudflare 的 "Just a moment..."）—— 实测 danbooru 经代理
///    返回的正是这个：HTTP 200 或 403、页面"加载完成"、正文一个字都没有。
/// 3. **传输层失败**（SSL / DNS / 连接被拒）—— 这一条是最反直觉的：实测**同一个代理**，
///    .NET 的 HttpClient 对 en.wikipedia.org 直接 SSL 握手失败，而 Edge 走通了并读到
///    11,969 字符正文。浏览器的 TLS 栈与代理协商方式跟 .NET 不是一回事。
/// 4. **声明了本机解不了的编码**（GBK 之类；见 <see cref="Decode"/>）。
///
/// 刻意**不**升级的：404 / 410 这类"页面本来就不存在"—— 浏览器去了也一样，
/// 白等 1–2 秒还多占 500 MB。
///
/// 代理设置（<see cref="ProxyConfig.FetchProxy"/>）两个引擎共用，但**实现方式不同**：
/// http 走 <see cref="WebProxy"/>，browser 走 <c>--proxy-server</c>。这正是上面第 3 条
/// 能成立的原因 —— 同一条代理地址，两条路的能力不一样。
/// </summary>
public sealed class PageReader : IDisposable
{
    /// <summary>原始 HTML 的读取上限。真实网页极少超过这个数，而超了的多半是二进制被当成网页发。</summary>
    internal const int MaxRawBytes = 8 * 1024 * 1024;

    internal const int DefaultMaxChars = 20000;

    /// <summary>浏览器 UA。用真实 UA 而不是 <c>AnimaAIProxy/1.0</c>：很多站点对陌生 UA 直接回 403。</summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36";

    private readonly HttpMessageHandler? _injected;
    private readonly IBrowserEngine _browser;
    private HttpClient _http;
    private string _proxyApplied;

    /// <summary>
    /// 注册代码页提供程序。**不做这一步，GBK/GB2312/Big5 全都解不了** ——
    /// .NET 默认只带 Unicode 系，<c>Encoding.GetEncoding("gbk")</c> 会直接抛
    /// "is not a supported encoding name"。而提供程序本身在共享框架里
    /// （实测 <c>CodePagesEncodingProvider</c> 类型存在，注册后 cp936 与 cp18030 都可用），
    /// 所以不需要额外引 NuGet 包。
    ///
    /// 国内站点至今仍有大量 GBK/GB2312 页面，不注册的话拿到的是乱码 ——
    /// 而乱码的"症状"是正文长度正常、内容全是问号，比抓不到更难发现。
    /// </summary>
    static PageReader()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 注册不了就退回 UTF-8，Decode 会如实标记为"解不了"并升级到浏览器 */ }
    }

    /// <param name="handler">
    /// 只给回归用：传进来就接管全部请求，于是"代理设置、编码、降级判据"都能离线验。生产路径传 null。
    /// </param>
    /// <param name="browser">只给回归用；传 null 时用真的 <see cref="BrowserFetcher"/>。</param>
    public PageReader(HttpMessageHandler? handler = null, IBrowserEngine? browser = null)
    {
        _injected = handler;
        _browser = browser ?? new BrowserFetcher();
        _http = handler is not null
            ? new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }
            : BuildClient("");
        _proxyApplied = handler is not null ? "\u0000" : "";
    }

    /// <summary>本机有没有可用的浏览器引擎（界面与 <see cref="ProxyConfig.FetchReady"/> 用它）。</summary>
    public bool BrowserAvailable => _browser.Available;

    public string BrowserPath => (_browser as BrowserFetcher)?.Path ?? "";

    public bool BrowserRunning => (_browser as BrowserFetcher)?.Running ?? false;

    /// <summary>
    /// 代理变了就重建 <see cref="HttpClient"/>。
    ///
    /// 为什么不直接改 <c>SocketsHttpHandler.Proxy</c>：那个属性的可变性依赖具体实现与版本，
    /// 而一旦哪天不成立，症状是"改了代理设置但请求还走旧代理"—— 一个没有任何报错的错误。
    /// 重建只发生在用户改设置的那一次，代价可以忽略。
    /// </summary>
    private void EnsureProxy(string proxy)
    {
        if (_injected is not null) return;
        var want = (proxy ?? "").Trim();
        if (want == _proxyApplied) return;

        var old = _http;
        _http = BuildClient(want);
        _proxyApplied = want;
        old.Dispose();
    }

    private static HttpClient BuildClient(string proxy)
    {
        var handler = new SocketsHttpHandler
        {
            // **解压必须交给 handler 自己**。手写 Accept-Encoding 会让它认为压缩由调用方负责，
            // 于是不再解压 —— 拿到 Brotli 原始字节按 UTF-8 解出来是乱码，
            // 解析出零个节点，症状是"HTTP 200 但正文为空"。（实测踩过。）
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
        };

        if (proxy.Length > 0)
        {
            // BypassProxyOnLocal 默认是 true，而 v2rayN 这类本地代理恰恰监听在回环上 ——
            // 保持默认的话，"填了本地代理"会被系统规则直接绕过，等于没填。
            handler.Proxy = new WebProxy(proxy) { BypassProxyOnLocal = false };
            handler.UseProxy = true;
        }
        else
        {
            // 留空 = 直连，**连系统代理都不走**。这样"直连"是一个确定的行为，
            // 而不是"取决于这台机器的 IE 代理设置"。
            handler.UseProxy = false;
        }

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // ---------------------------------------------------------------- 对外入口

    /// <summary>
    /// 抓一个页面。<paramref name="forceRender"/> 来自工具参数 <c>render</c>
    /// （模型明确要求渲染时用）。**永不抛异常**，失败一律进 <c>Ok=false</c> + <c>Error</c>。
    /// </summary>
    public async Task<FetchResult> FetchAsync(ProxyConfig cfg, string url, bool forceRender = false,
                                              int? maxChars = null, CancellationToken ct = default)
    {
        var target = NormalizeUrl(url);
        if (target.Length == 0)
            return Fail(url, "网址无效：需要 http 或 https 的绝对地址");

        if (!cfg.FetchAllowPrivateHosts && IsPrivateHost(target, out var why))
            return Fail(target,
                $"目标地址指向本机或内网（{why}），已按默认设置拦截。" +
                "确实需要抓内网页面时，到代理界面打开「允许内网地址」。");

        var budget = Math.Clamp(maxChars ?? cfg.FetchMaxChars, 500, 200_000);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.FetchTimeoutSeconds, 5, 180));
        var mode = cfg.FetchEngineKey;

        if (mode == "browser" || forceRender)
            return await ViaBrowserAsync(cfg, target, budget, timeout, ct).ConfigureAwait(false);

        var attempt = await ViaHttpAsync(cfg, target, budget, timeout, ct).ConfigureAwait(false);

        if (mode == "http") return attempt.Result;
        if (!ShouldEscalate(attempt)) return attempt.Result;

        if (!_browser.Available)
        {
            // 该升级但升不了：如实说明，**不假装成功**。
            // 这里保留 http 抓到的内容（哪怕很薄）比回一句"失败"更有用，
            // 但必须让模型知道它可能只看到了骨架。
            if (attempt.Result.Ok)
                return attempt.Result with
                {
                    Note = "这一页的正文很少，很可能是需要 JavaScript 渲染的页面；" +
                           "本机没有找到 Edge/Chrome，所以只能用上面这些内容。" +
                           "不要据此断言页面里没有别的内容。"
                };
            return attempt.Result with
            {
                Error = attempt.Result.Error + "；本机也没有可用的浏览器引擎可以重试"
            };
        }

        ProxyLog.Write($"抓取升级到浏览器引擎（{EscalateReason(attempt)}）：{target}");
        var rendered = await ViaBrowserAsync(cfg, target, budget, timeout, ct).ConfigureAwait(false);
        if (rendered.Ok)
        {
            // 两边都读到了内容时**取更完整的那份**。浏览器渲染不一定更好：
            // 登录墙、地域限制、或者被 JS 换成了另一套内容，都可能让它反而更少。
            // 这条比较是必要的，不是保险 —— 少了它，一次"渲染出了登录页"就会
            // 把本来还能用的 HTTP 正文盖掉。
            if (attempt.Result.Ok && attempt.Result.Text.Length > rendered.Text.Length)
                return attempt.Result with
                {
                    Note = "浏览器引擎渲染后的正文反而更少，已保留 HTTP 引擎抓到的内容。"
                };
            return rendered;
        }

        if (attempt.Result.Ok)
            return attempt.Result with
            {
                Note = $"浏览器引擎也没能读到正文（{rendered.Error}），上面是 HTTP 引擎抓到的内容。"
            };
        return attempt.Result with
        {
            Error = attempt.Result.Error + "；浏览器引擎也失败：" + rendered.Error
        };
    }

    /// <summary>连通性自检，供界面上的"测试抓取"用。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(ProxyConfig cfg, string url,
                                                          CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var r = await FetchAsync(cfg, url, false, 4000, ct).ConfigureAwait(false);
        if (!r.Ok) return (false, r.Error);

        var head = r.Text.Length > 0 ? ArticleExtractor.Collapse(r.Text)[..Math.Min(60, ArticleExtractor.Collapse(r.Text).Length)] : "";
        var title = r.Title.Length > 0 ? $"《{ArticleExtractor.Collapse(r.Title)}》" : "";
        return (true, $"{EngineText(r.Engine)}读到 {r.Text.Length} 字符{title}（{sw.ElapsedMilliseconds} ms）。开头：{head}");
    }

    internal static string EngineText(string engine) => engine switch
    {
        "browser" => "浏览器引擎（已渲染）",
        "http" => "HTTP 引擎",
        _ => engine
    };

    // ---------------------------------------------------------------- http 引擎

    /// <summary>一次 http 尝试的完整信息。降级判据需要原始字节数与状态码，而它们不该出现在给模型的结果里。</summary>
    internal sealed record HttpAttempt(FetchResult Result, int RawBytes, int Status,
                                       bool UnsupportedCharset, bool Challenge);

    private async Task<HttpAttempt> ViaHttpAsync(ProxyConfig cfg, string url, int budget,
                                                 TimeSpan timeout, CancellationToken ct)
    {
        EnsureProxy(cfg.FetchProxy);

        // 超时用**每次请求一个 CTS**，不去改 HttpClient.Timeout ——
        // 那个属性只能在发出第一个请求前设置，第二次抓取就会抛
        // "Properties can only be modified before sending the first request"，
        // 症状是"第一次能抓、之后每次都失败"。（与 SearchClient 同一个坑。）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, MaxRawBytes, cts.Token).ConfigureAwait(false);

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var charset = resp.Content.Headers.ContentType?.CharSet;
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            var status = (int)resp.StatusCode;

            var (text, encodingName, unsupported) = Decode(bytes, charset);
            var challenge = LooksLikeChallenge(text);

            if (!resp.IsSuccessStatusCode)
            {
                var hint = challenge ? "（疑似人机校验页，浏览器引擎通常能过）" : "";
                var snippet = ArticleExtractor.Collapse(text);
                if (snippet.Length > 200) snippet = snippet[..200] + "…";
                var err = $"HTTP {status} {resp.ReasonPhrase}{hint}" +
                          (snippet.Length > 0 ? "：" + snippet : "");
                if (unsupported) err += $"（另外，页面声明了本机不支持的编码 {encodingName}）";
                return new HttpAttempt(
                    new FetchResult(false, finalUrl, "", "", "http", err, false, ""),
                    bytes.Length, status, unsupported, challenge);
            }

            var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                         (mediaType.Length == 0 && LooksLikeHtml(text));

            if (isHtml)
            {
                var page = ArticleExtractor.Extract(text, finalUrl, budget);
                var note = unsupported
                    ? $"页面声明了本机不支持的编码 {encodingName}，内容可能乱码"
                    : "";
                return new HttpAttempt(
                    new FetchResult(true, finalUrl, page.Title, page.Markdown, "http", "",
                                    page.Truncated, note),
                    bytes.Length, status, unsupported, challenge);
            }

            if (IsPlainText(mediaType))
            {
                var body = text.Trim();
                var truncated = body.Length > budget;
                if (truncated) body = body[..budget] + "\n\n[内容已截断]";
                return new HttpAttempt(
                    new FetchResult(true, finalUrl, "", body, "http", "", truncated,
                                    unsupported ? $"页面声明了本机不支持的编码 {encodingName}，内容可能乱码" : ""),
                    bytes.Length, status, unsupported, false);
            }

            return new HttpAttempt(
                new FetchResult(false, finalUrl, "", "", "http",
                    $"不支持的内容类型 {mediaType}（本工具只读 HTML 与纯文本页面，不解析 PDF、图片或二进制）",
                    false, ""),
                bytes.Length, status, unsupported, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpAttempt(
                new FetchResult(false, url, "", "", "http",
                    $"抓取超时（{timeout.TotalSeconds:0} 秒）", false, ""),
                0, 0, false, false);
        }
        catch (Exception ex)
        {
            // SSL / DNS / 连接被拒 / 代理不可用都走这里。**这类错误恰恰是浏览器引擎
            // 有可能救回来的**（实测 wikipedia 就是），所以原样交给降级判据。
            return new HttpAttempt(
                new FetchResult(false, url, "", "", "http", ex.Message, false, ""),
                0, 0, false, false);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (ms.Length < max)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- 降级判据

    /// <summary>
    /// 该不该升级到浏览器引擎。**做成纯函数是为了能离线回归** ——
    /// 这是本工具最核心的行为，而每条分支都需要真网络才能端到端跑一遍。
    /// </summary>
    internal static bool ShouldEscalate(HttpAttempt a)
    {
        if (a.Challenge) return true;
        if (a.UnsupportedCharset) return true;

        if (!a.Result.Ok)
        {
            // "页面本来就不存在"和"我们没有权限"这两类，浏览器去了结果一样，
            // 不该白等 1–2 秒、白占 500 MB。
            if (a.Status is 400 or 401 or 404 or 410 or 451) return false;
            // 传输层失败（Status==0）、403/429/5xx → 值得让浏览器再试一次
            return true;
        }

        // 抓到了、也是 200，但正文薄得不像正文，而原始 HTML 很大 → 前端渲染页面
        return a.Result.Text.Length < ArticleExtractor.ThinThreshold && a.RawBytes > 20_000;
    }

    private static string EscalateReason(HttpAttempt a)
    {
        if (a.Challenge) return "人机校验页";
        if (a.UnsupportedCharset) return "编码解不了";
        if (!a.Result.Ok) return a.Status == 0 ? "传输层失败" : $"HTTP {a.Status}";
        return $"正文只有 {a.Result.Text.Length} 字符而 HTML 有 {a.RawBytes / 1024} KB";
    }

    // ---------------------------------------------------------------- 浏览器引擎

    private async Task<FetchResult> ViaBrowserAsync(ProxyConfig cfg, string url, int budget,
                                                    TimeSpan timeout, CancellationToken ct)
    {
        if (!_browser.Available)
            return Fail(url, "浏览器引擎不可用：本机没有找到 Edge 或 Chrome。" +
                             "可以在代理界面的「联网抓取」里指定浏览器可执行文件路径。");

        // 界面上那个"浏览器路径"框随时可改，而路径只在构造时算过一次 ——
        // 不重新解析就会出现"界面说找到了、抓取却说不可用"这种自相矛盾的症状。
        if (_browser is BrowserFetcher bf) bf.RefreshPath(cfg.FetchBrowserPath);

        var page = await _browser
            .FetchAsync(url, cfg.FetchProxy, cfg.FetchBrowserIdleSeconds, timeout, ct).ConfigureAwait(false);
        if (!page.Ok)
            return Fail(page.Url.Length > 0 ? page.Url : url, page.Error);

        var content = ArticleExtractor.Extract(page.Html, page.Url, budget);
        var title = content.Title.Length > 0 ? content.Title : page.Title;

        // **只有真的一无所获才算失败**。正文很短时仍然把内容交出去、只在 note 里说明 ——
        // 早先这里是"薄就报失败"，结果是：浏览器明明渲染出了内容（只是这一页真的短），
        // 我们却回一句"浏览器没能读到正文"，然后把 HTTP 那份更差的交上去。
        // 那是一句**不成立的诊断**，比没有诊断更坏。
        if (content.Markdown.Length == 0)
            return new FetchResult(false, page.Url, title, "", "browser",
                "页面渲染后没有任何可读正文（可能需要登录，或者正文在图片/视频里）", false, "");

        var note = ArticleExtractor.IsThin(content.Markdown)
            ? "浏览器渲染后的正文也很少：可能这一页真的就这么短，也可能需要登录。"
            : "";
        return new FetchResult(true, page.Url, title, content.Markdown, "browser", "",
                               content.Truncated, note);
    }

    // ---------------------------------------------------------------- 编码

    /// <summary>
    /// 把响应字节解成字符串。顺序：响应头声明的 charset → 正文里的 <c>&lt;meta charset&gt;</c> → UTF-8。
    ///
    /// 第三个返回值是"声明的编码本机解不了"。**它必须单独报出来**：那种情况下
    /// UTF-8 硬解出来的是一串问号，正文长度正常、看不出异常，比抓不到更难发现。
    /// 拿到这个标记后，自动降级会让浏览器再试一次（浏览器自己会正确解码）。
    /// </summary>
    internal static (string Text, string EncodingName, bool Unsupported) Decode(byte[] bytes, string? headerCharset)
    {
        var name = CleanCharset(headerCharset);
        if (name.Length == 0) name = SniffCharset(bytes);
        if (name.Length == 0) name = "utf-8";

        try
        {
            var enc = Encoding.GetEncoding(name);
            var text = enc.GetString(bytes);
            // UTF-8 BOM 会变成一个 U+FEFF 字符留在开头，模型看到会当成正文的一部分
            return (text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text, name, false);
        }
        catch
        {
            return (Encoding.UTF8.GetString(bytes), name, true);
        }
    }

    internal static string CleanCharset(string? charset)
    {
        var c = (charset ?? "").Trim().Trim('"', '\'').ToLowerInvariant();
        return c switch
        {
            "utf8" => "utf-8",
            "gb-2312" => "gb2312",
            "iso8859-1" => "iso-8859-1",
            _ => c
        };
    }

    /// <summary>从正文前几 KB 里找 <c>charset=</c>（响应头没写时用）。</summary>
    internal static string SniffCharset(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(4096, bytes.Length));
        var i = head.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var s = head[(i + 8)..].TrimStart('"', '\'', ' ');
        var end = s.IndexOfAny(new[] { '"', '\'', ' ', '>', '/', ';', '\r', '\n' });
        return CleanCharset(end > 0 ? s[..end] : s);
    }

    private static bool LooksLikeHtml(string text)
    {
        var head = text.AsSpan(0, Math.Min(512, text.Length)).TrimStart();
        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlainText(string mediaType)
    {
        var t = mediaType.ToLowerInvariant();
        if (t.StartsWith("text/")) return true;
        return t is "application/json" or "application/xml" or "application/xhtml" or
                    "application/javascript" or "application/x-javascript" or
                    "application/x-yaml" or "application/yaml" or "application/csv";
    }

    private static readonly Regex ChallengeHint = new(
        @"just a moment|checking your browser|cf-browser-verification|cf_chl|" +
        @"enable javascript and cookies|attention required|verify you are human|" +
        @"正在检查您的浏览器|请稍候",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 这一页是不是人机校验页。只看开头一段：校验页正文很短，
    /// 而正文页里偶尔提到 "just a moment" 不该被误判（那也只是多跑一次浏览器）。
    /// </summary>
    internal static bool LooksLikeChallenge(string html)
    {
        if (html.Length == 0) return false;
        return ChallengeHint.IsMatch(html.AsSpan(0, Math.Min(4000, html.Length)));
    }

    // ---------------------------------------------------------------- 地址

    /// <summary>
    /// 把模型给的地址收拾成绝对 URL。
    /// 模型经常只给 <c>example.com/page</c>（没写协议），直接拒绝太苛刻，补成 https。
    /// </summary>
    public static string NormalizeUrl(string? url)
    {
        var u = (url ?? "").Trim().Trim('"', '\'', '<', '>', '`');
        if (u.Length == 0) return "";
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u.TrimStart('/');

        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
        if (uri.Host.Length == 0) return "";
        return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
    }

    /// <summary>
    /// 是不是本机/内网地址。
    ///
    /// 为什么需要：这个工具的 URL 是**模型给的**，而模型刚刚可能读过一段不可信网页。
    /// 一个被注入的页面完全可以指示模型去抓 <c>http://127.0.0.1:48620/…</c>
    /// 或内网里的其它服务。代理的默认配置是 fail-closed 的（见
    /// <see cref="ProxyConfig.RequireToken"/>），这里保持一致：**默认拦截，要抓内网得显式打开**。
    ///
    /// **已知边界**：域名只能按字面判断，解析到内网 IP 的公网域名（DNS rebinding、
    /// 或者用户自己的内网域名解析）拦不住 —— 那需要先解析再连接，而 HttpClient 不给我们
    /// 这个钩子。这条边界写在 <see cref="ProxyConfig.FetchAllowPrivateHosts"/> 的注释里，
    /// 不假装它不存在。
    /// </summary>
    public static bool IsPrivateHost(string url, out string reason)
    {
        reason = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            reason = "内网域名 " + host;
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip)) return false;

        if (IPAddress.IsLoopback(ip)) { reason = "回环地址"; return true; }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) { reason = "私有网段 10/8"; return true; }
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { reason = "私有网段 172.16/12"; return true; }
            if (b[0] == 192 && b[1] == 168) { reason = "私有网段 192.168/16"; return true; }
            if (b[0] == 169 && b[1] == 254) { reason = "链路本地 169.254/16"; return true; }
            if (b[0] == 127 || b[0] == 0) { reason = "保留网段"; return true; }
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) { reason = "IPv6 链路本地"; return true; }
            if (ip.IsIPv6SiteLocal) { reason = "IPv6 站点本地"; return true; }
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) { reason = "IPv6 唯一本地地址 fc00::/7"; return true; }
        }
        return false;
    }

    // ---------------------------------------------------------------- 回灌给模型

    /// <summary>
    /// 把结果序列化成回灌给模型的文本。
    ///
    /// 用纯文本信封而不是 JSON：正文动辄上万字符，JSON 会把每个换行转义成 <c>\n</c>，
    /// 既难读又平白多占上下文。
    ///
    /// **失败与"页面为空"必须分开说**（与 <see cref="SearchClient.ToToolContent"/> 同一条理由）：
    /// 前者是"这次读不到"，后者是"这页真没内容"。混成一句的话，模型会把一次网络故障
    /// 当成"这件事不存在"，然后凭记忆编答案。
    /// </summary>
    public static string ToToolContent(FetchResult r, string requestedUrl)
    {
        var sb = new StringBuilder();
        if (!r.Ok)
        {
            sb.Append("fetch failed: ").Append(requestedUrl).Append('\n');
            sb.Append("error: ").Append(r.Error).Append('\n');
            sb.Append("note: This page could not be read. That does NOT mean the page or the fact ").
               Append("you were looking for does not exist. Try a different url (an official site ").
               Append("or another search result), or answer without it and say what is missing. ").
               Append("Do not invent page content.\n");
            return sb.ToString();
        }

        sb.Append("url: ").Append(r.Url).Append('\n');
        if (r.Title.Length > 0) sb.Append("title: ").Append(r.Title).Append('\n');
        sb.Append("engine: ").Append(r.Engine).Append('\n');
        if (r.Truncated)
            sb.Append("truncated: true (only the beginning of the page is shown)\n");
        if (r.Note.Length > 0) sb.Append("note: ").Append(r.Note).Append('\n');
        sb.Append('\n').Append(r.Text);
        return sb.ToString();
    }

    private static FetchResult Fail(string url, string error) =>
        new(false, url, "", "", "http", error, false, "");

    public void Dispose()
    {
        _http.Dispose();
        (_browser as IDisposable)?.Dispose();
    }
}
