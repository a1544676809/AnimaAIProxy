using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AnimaAIProxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 网页获取（web_fetch）的离线回归。
///
/// 三块：
/// * **正文提取**（<see cref="ArticleExtractor"/>）—— 纯函数，用真实形态的 HTML 喂进去；
/// * **编码 / 地址 / 降级判据** —— 也都是纯函数，其中编码那节钉住的是
///   "GBK 到底解不解得开"这个曾经会让整页变乱码的前提；
/// * **双引擎编排** —— 用桩 <see cref="HttpMessageHandler"/> 与假浏览器，
///   把自动降级的每条分支都走一遍（真跑一遍需要真网络 + 真 Edge，见 Live.cs）。
///
/// 代理那一节**不用桩 handler**：它要验的正是"请求真的从代理走"，
/// 所以起一个真的桩 HTTP 代理服务器，并做一组对照（同一地址，填了代理能通、没填不通）。
/// </summary>
public static class PageRead
{
    public static void Run()
    {
        Harness.Section("网页获取：正文提取");
        Extract();

        Harness.Section("网页获取：编码");
        Encoding_();

        Harness.Section("网页获取：地址与内网拦截");
        Url();

        Harness.Section("网页获取：降级判据");
        Escalate();

        Harness.Section("网页获取：双引擎编排（桩 HTTP + 假浏览器）");
        Engine().GetAwaiter().GetResult();

        Harness.Section("网页获取：代理真的生效（真桩代理）");
        Proxy().GetAwaiter().GetResult();

        Harness.Section("网页获取：瞬时失败重试");
        Retry().GetAwaiter().GetResult();

        Harness.Section("网页获取：浏览器与临时目录的回收");
        Reclaim();
    }

    // ---------------------------------------------------------------- 正文提取

    private static void Extract()
    {
        // 1) 导航/页脚要被丢掉，正文要留下
        var nav = ExtractHtml("""
            <html><head><title>标题 &amp; 实体</title></head><body>
            <nav><a href="/a">首页</a><a href="/b">关于我们</a><a href="/c">联系方式</a><a href="/d">更多内容</a></nav>
            <article><h1>真正的标题</h1>
            <p>这是正文的第一段，长度要超过二十五个字符才会被当成正文看待。</p>
            <p>第二段同样有足够的内容，用来确认多段都被完整保留下来。</p></article>
            <footer><a href="/x">版权所有</a></footer>
            </body></html>
            """, "https://example.com/page");

        Harness.True("选中 article 并保留正文", nav.Markdown.Contains("这是正文的第一段"));
        Harness.True("导航被丢掉", !nav.Markdown.Contains("关于我们"));
        Harness.True("页脚被丢掉", !nav.Markdown.Contains("版权所有"));
        Harness.True("标题里的实体被解码", nav.Title.Contains("标题 & 实体"));
        Harness.Eq("h1 渲染成 Markdown 标题", nav.Markdown.Contains("# 真正的标题"), true);

        // 2) 整块正文都在 div 里、一个 <p> 都没有 —— 国内站点（知乎那类）的常态。
        //    这一条是针对 Mozilla Readability 那套"给 <p> 打分"的做法专门写的：
        //    照那套走会得到"没有候选"，直接退化成整页正文。
        var divOnly = ExtractHtml("""
            <html><body><div id="wrap">
            <div class="topbar"><a href="/n1">导航一</a><a href="/n2">导航二</a></div>
            <div class="richcontent">
            <div>知乎这类站点整块正文都在 div 里，一个 p 标签都没有，这里必须能选中它。</div>
            <div>第二块内容也要一起被选中，否则只拿到一半，模型会以为页面就这么短。</div>
            </div></div></body></html>
            """, "https://example.com/");

        Harness.True("div 排版也能选中正文", divOnly.Markdown.Contains("整块正文都在 div 里"));
        Harness.True("div 排版下第二块也在", divOnly.Markdown.Contains("第二块内容也要一起被选中"));
        Harness.True("div 排版下导航仍被丢掉", !divOnly.Markdown.Contains("导航一"));

        // 3) <script> 里的 JS 源码绝不能被当成正文，也不能靠它把外层容器捧成最高分。
        //    HtmlAgilityPack 的 InnerText 会把 <script> 的内容一起返回，
        //    所以计分之前必须先摘掉 —— 不摘的话分数最高的永远是装了脚本的那层。
        var withScript = ExtractHtml(
            "<html><body><div>" +
            "<script>var big = \"" + new string('x', 30000) + "\";</script>" +
            "<article><p>这段才是正文，长度足够超过二十五个字符的门槛。</p></article>" +
            "</div></body></html>", "https://example.com/");

        Harness.True("脚本源码没有进正文", !withScript.Markdown.Contains("var big"));
        Harness.True("脚本旁边仍能选出正文", withScript.Markdown.Contains("这段才是正文"));

        // 4) article 自己的 <header> 常常装着 h1，不能按标签名整块摘掉
        var header = ExtractHtml("""
            <html><body><article><header><h1>标题在 header 里</h1></header>
            <p>正文内容，长度足够超过二十五个字符的门槛才算数。</p></article></body></html>
            """, "https://example.com/");
        Harness.True("article 里的 header 不被当垃圾丢掉", header.Markdown.Contains("# 标题在 header 里"));

        // 5) 结构转换 + 相对链接补成绝对链接
        var md = ExtractHtml("""
            <html><body><article>
            <h2>小标题</h2>
            <ul><li>项目一</li><li>项目二</li></ul>
            <p>见 <a href="/doc/x">文档</a> 里的说明，这一段要够长才不会被当成噪声丢掉。</p>
            <pre><code>a &amp;&amp; b</code></pre>
            </article></body></html>
            """, "https://example.com/base/page");

        Harness.Eq("h2 渲染成 ##", md.Markdown.Contains("## 小标题"), true);
        Harness.Eq("列表渲染成 -", md.Markdown.Contains("- 项目一"), true);
        Harness.Eq("相对链接补成绝对链接", md.Markdown.Contains("[文档](https://example.com/doc/x)"), true);
        Harness.Eq("pre 保留代码块围栏", md.Markdown.Contains("```"), true);
        // 浏览器里 <pre>a &amp;&amp; b</pre> 显示的就是 "a && b"，所以这里必须是解码后的
        Harness.Eq("pre 里的实体按浏览器语义解码", md.Markdown.Contains("a && b"), true);

        // 6) 行内标签不能把单词粘起来（Collapse 会 trim 掉边界空格，这里专门钉住）
        var inline = ExtractHtml(
            "<html><body><article><p>Hello <b>world</b> again and again, " +
            "this paragraph is long enough to be treated as content.</p></article></body></html>",
            "https://example.com/");
        Harness.Eq("行内标签两侧的空格被保留", inline.Markdown.Contains("Hello **world** again"), true);

        // 7) og:description 与截断
        var withDesc = ExtractHtml("""
            <html><head><meta property="og:title" content="OG 标题">
            <meta property="og:description" content="这是页面描述"></head>
            <body><article><p>正文内容，长度足够超过二十五个字符的门槛才算数。</p></article></body></html>
            """, "https://example.com/");
        Harness.Eq("og:title 优先于 title", withDesc.Title, "OG 标题");
        Harness.Eq("og:description 被取到", withDesc.Description, "这是页面描述");

        var longHtml = "<html><body><article>" +
                       string.Join("", Enumerable.Range(0, 60)
                           .Select(i => $"<p>第 {i} 段内容，用来把正文撑长以便验证截断行为是否生效。</p>")) +
                       "</article></body></html>";
        var cut = ArticleExtractor.Extract(longHtml, "https://example.com/", 300);
        Harness.Eq("超过上限时标记截断", cut.Truncated, true);
        Harness.True("截断处有明确标记", cut.Markdown.Contains("[内容已截断]"));
        Harness.True("截断后长度受控", cut.Markdown.Length < 400);

        Harness.Eq("薄正文判定：空串算薄", ArticleExtractor.IsThin(""), true);
        Harness.Eq("薄正文判定：长文不算薄", ArticleExtractor.IsThin(new string('字', 300)), false);

        // 8) 畸形 HTML 不能抛（真实网页什么都有）
        var broken = ArticleExtractor.Extract("<html><body><div><p>没闭合<div><span>",
                                              "https://example.com/", 1000);
        Harness.True("畸形 HTML 不抛异常", broken is not null);
    }

    private static PageContent ExtractHtml(string html, string url, int maxChars = 20000) =>
        ArticleExtractor.Extract(html, url, maxChars);

    // ---------------------------------------------------------------- 编码

    private static void Encoding_()
    {
        // GBK 能不能解，取决于 PageReader 的静态构造函数有没有注册代码页提供程序。
        // 不做那一步的话 Encoding.GetEncoding("gbk") 直接抛，页面会变成一串问号 ——
        // 而"乱码"的症状是正文长度正常，比抓不到更难发现。
        // 调用 PageReader 的静态方法会触发它的静态构造，所以这条断言验的就是那个注册。
        var gbk = PageReader.Decode(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, "gbk");
        Harness.Eq("GBK 能被解码（代码页已注册）", gbk.Text, "中文");
        Harness.Eq("GBK 不被标记为不支持", gbk.Unsupported, false);

        var gb2312 = PageReader.Decode(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, "gb2312");
        Harness.Eq("GB2312 别名也能解", gb2312.Text, "中文");

        var utf8 = PageReader.Decode(Encoding.UTF8.GetBytes("中文内容"), "utf-8");
        Harness.Eq("UTF-8 正常", utf8.Text, "中文内容");

        var bom = PageReader.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("abc")).ToArray(), "utf-8");
        Harness.Eq("UTF-8 BOM 被剥掉", bom.Text, "abc");

        // 响应头没写 charset 时从正文的 <meta charset> 猜
        var sniffBytes = Encoding.ASCII.GetBytes("<html><head><meta charset=\"gbk\"></head><body>");
        Harness.Eq("能从 meta 里嗅出 charset", PageReader.SniffCharset(sniffBytes), "gbk");

        var unsupported = PageReader.Decode(Encoding.UTF8.GetBytes("x"), "x-not-a-real-encoding");
        Harness.Eq("认不出的编码被标记", unsupported.Unsupported, true);

        Harness.Eq("charset 归一化：utf8 → utf-8", PageReader.CleanCharset("UTF8"), "utf-8");
        Harness.Eq("charset 归一化：去引号", PageReader.CleanCharset("\"gbk\""), "gbk");
    }

    // ---------------------------------------------------------------- 地址

    private static void Url()
    {
        Harness.Eq("补协议", PageReader.NormalizeUrl("example.com/a"), "https://example.com/a");
        Harness.Eq("保留已有协议", PageReader.NormalizeUrl("http://a.com/b"), "http://a.com/b");
        Harness.Eq("去掉页内锚点", PageReader.NormalizeUrl("https://a.com/b#sec"), "https://a.com/b");
        Harness.Eq("拒绝非 http 协议", PageReader.NormalizeUrl("file:///C:/x"), "");
        Harness.Eq("拒绝空串", PageReader.NormalizeUrl("  "), "");
        Harness.Eq("去掉模型爱加的尖括号", PageReader.NormalizeUrl("<https://a.com/>"), "https://a.com/");

        Harness.Eq("localhost 被拦", PageReader.IsPrivateHost("http://localhost:8080/", out _), true);
        Harness.Eq("127.0.0.1 被拦", PageReader.IsPrivateHost("http://127.0.0.1/", out _), true);
        Harness.Eq("10.x 被拦", PageReader.IsPrivateHost("http://10.1.2.3/", out _), true);
        Harness.Eq("192.168.x 被拦", PageReader.IsPrivateHost("http://192.168.1.1/", out _), true);
        Harness.Eq("172.16-31 被拦", PageReader.IsPrivateHost("http://172.20.0.1/", out _), true);
        Harness.Eq("169.254.x 被拦", PageReader.IsPrivateHost("http://169.254.1.1/", out _), true);
        Harness.Eq("IPv6 回环被拦", PageReader.IsPrivateHost("http://[::1]/", out _), true);
        Harness.Eq("IPv6 ULA 被拦", PageReader.IsPrivateHost("http://[fd00::1]/", out _), true);
        Harness.Eq("内网域名被拦", PageReader.IsPrivateHost("http://nas.local/", out _), true);

        Harness.Eq("公网 IP 不拦", PageReader.IsPrivateHost("http://8.8.8.8/", out _), false);
        Harness.Eq("公网域名不拦", PageReader.IsPrivateHost("https://example.com/", out _), false);
        // 172.15/172.32 不在私有段里，别误伤
        Harness.Eq("172.15 不拦", PageReader.IsPrivateHost("http://172.15.0.1/", out _), false);
        Harness.Eq("172.32 不拦", PageReader.IsPrivateHost("http://172.32.0.1/", out _), false);
    }

    // ---------------------------------------------------------------- 降级判据

    private static PageReader.HttpAttempt Attempt(bool ok, string error, int status,
                                                  int textLen, int rawBytes,
                                                  bool charset = false, bool challenge = false) =>
        new(new FetchResult(ok, "https://a.com/", "", new string('字', textLen), "http", error, false, ""),
            rawBytes, status, charset, challenge);

    private static void Escalate()
    {
        Harness.Eq("人机校验页 → 升级",
            PageReader.ShouldEscalate(Attempt(false, "HTTP 403", 403, 0, 5000, challenge: true)), true);

        Harness.Eq("编码解不了 → 升级",
            PageReader.ShouldEscalate(Attempt(true, "", 200, 5000, 40000, charset: true)), true);

        Harness.Eq("传输层失败 → 升级（浏览器可能读得通）",
            PageReader.ShouldEscalate(Attempt(false, "SSL 握手失败", 0, 0, 0)), true);

        Harness.Eq("403 → 升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 403", 403, 0, 100)), true);
        Harness.Eq("429 → 升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 429", 429, 0, 100)), true);
        Harness.Eq("503 → 升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 503", 503, 0, 100)), true);

        // "页面本来就不存在"这类，浏览器去了结果一样，不该白等 1–2 秒、白占 500 MB
        Harness.Eq("404 不升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 404", 404, 0, 100)), false);
        Harness.Eq("410 不升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 410", 410, 0, 100)), false);
        Harness.Eq("401 不升级", PageReader.ShouldEscalate(Attempt(false, "HTTP 401", 401, 0, 100)), false);

        // 前端渲染页面的特征：HTML 很大、正文很少
        Harness.Eq("大 HTML + 薄正文 → 升级",
            PageReader.ShouldEscalate(Attempt(true, "", 200, 30, 250000)), true);
        // 而真正短的小页面不该升级（它本来就这么短）
        Harness.Eq("小 HTML + 薄正文 → 不升级",
            PageReader.ShouldEscalate(Attempt(true, "", 200, 30, 800)), false);
        Harness.Eq("正常正文 → 不升级",
            PageReader.ShouldEscalate(Attempt(true, "", 200, 8000, 60000)), false);
    }

    // ---------------------------------------------------------------- 瞬时失败重试

    /// <summary>
    /// 传输层抖动（TLS EOF、连接被重置）必须重试一次。否则一次抖动会被
    /// <see cref="PageReader"/> 的降级判据读成"传输层失败"，于是为了一个几百字节的页面
    /// 启动一个 500 MB 的 headless 浏览器。
    ///
    /// 实测依据（代理为 sing-box，同一站点连续 6 次）：<c>en.wikipedia.org</c> 成功 4 次、
    /// <c>example.com</c> 只成功 2 次，失败全是 TLS 层的 EOF / 握手错误。
    /// </summary>
    private static async Task Retry()
    {
        var html = "<html><head><title>重试后拿到的</title></head><body><article><p>" +
                   new string('正', 300) + "</p></article></body></html>";

        // 1) 第一次传输失败、第二次成功 → 重试真的发生，且最终拿到正文、不起浏览器
        {
            var stub = new StubHandler
            {
                Fail = new HttpRequestException(
                    "Received an unexpected EOF or 0 bytes from the transport stream."),
                FailFirst = 1,
                Reply = _ => Html(html)
            };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://flaky.example/");
            Harness.Eq("瞬时失败后重试了一次", stub.Calls, 2);
            Harness.Eq("重试成功即视为成功", r.Ok, true);
            Harness.Eq("成功的是 HTTP 引擎（没有白起浏览器）", r.Engine, "http");
            Harness.Eq("重试成功后不启动浏览器", browser.Calls, 0);
            Harness.Eq("拿到的是重试那次的正文", r.Title, "重试后拿到的");
        }

        // 2) 一直失败 → 有上限，不会无限重试；两次都失败才交给降级判据
        {
            var stub = new StubHandler
            {
                Fail = new HttpRequestException("connection reset"),
                FailFirst = 99
            };
            var browser = new FakeBrowser { Error = "浏览器也没读到正文" };
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://dead.example/");
            Harness.Eq("一直失败也只试两次", stub.Calls, 2);
            Harness.Eq("两次都失败则如实报错", r.Ok, false);
            Harness.Eq("仍然按降级判据启动了浏览器", browser.Calls, 1);
            Harness.True("错误里保留了传输失败的原因", r.Error.Contains("connection reset"));
        }

        // 3) 超时**不**重试 —— 重试会让实际等待翻倍，而调用方给的是一个整体预算。
        //    用 http 模式：auto 会升级到浏览器，那样最终结果就不是 HTTP 层的结果了。
        {
            var stub = new StubHandler
            {
                // 调用方的 ct 没有被取消、而请求被取消了 —— 这正是"我们自己的超时"
                // 在 PageReader 里的形态（超时用每请求一个 linked CTS 实现）。
                Fail = new TaskCanceledException("The request was canceled due to the configured timeout."),
                FailFirst = 99
            };
            using var reader = new PageReader(stub, new FakeBrowser());
            var r = await reader.FetchAsync(Cfg("http"), "https://slow.example/");
            Harness.Eq("超时不重试", stub.Calls, 1);
            Harness.True("超时如实报成超时，不报成传输失败", r.Error.Contains("超时"));
        }

        // 4) 判据本身：**只认确实是抖动的那几类**，不是"凡失败都重试"。
        //    这条很重要 —— 白名单写宽了，DNS 失败与连接被拒也会白等一次。
        {
            static HttpRequestException Wrapped(SocketError e) =>
                new("transport", new SocketException((int)e));

            Harness.Eq("TLS EOF → 重试", PageReader.IsTransientFailure(new HttpRequestException("EOF")), true);
            Harness.Eq("连接被重置 → 重试",
                PageReader.IsTransientFailure(Wrapped(SocketError.ConnectionReset)), true);
            Harness.Eq("连接被中止 → 重试",
                PageReader.IsTransientFailure(Wrapped(SocketError.ConnectionAborted)), true);
            Harness.Eq("域名不存在 → 不重试（重试必然一样）",
                PageReader.IsTransientFailure(Wrapped(SocketError.HostNotFound)), false);
            Harness.Eq("域名无数据 → 不重试",
                PageReader.IsTransientFailure(Wrapped(SocketError.NoData)), false);
            Harness.Eq("目标拒绝连接 → 不重试（立即重试没有意义）",
                PageReader.IsTransientFailure(Wrapped(SocketError.ConnectionRefused)), false);
            Harness.Eq("超时异常 → 不重试",
                PageReader.IsTransientFailure(new TaskCanceledException()), false);
        }
    }

    // ---------------------------------------------------------------- 双引擎编排

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Html("");
        public List<string> Urls { get; } = new();

        /// <summary>前 <see cref="FailFirst"/> 次请求直接抛这个异常，用来验重试。null = 不抛。</summary>
        public Exception? Fail;
        public int FailFirst;

        /// <summary>实际发出的请求数 —— 重试有没有发生只能靠它看出来。</summary>
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            lock (Urls) Urls.Add(req.RequestUri?.ToString() ?? "");
            Calls++;
            if (Fail is not null && Calls <= FailFirst) throw Fail;
            var resp = Reply(req);
            resp.RequestMessage ??= req;
            return Task.FromResult(resp);
        }
    }

    private static HttpResponseMessage Html(string html, HttpStatusCode code = HttpStatusCode.OK,
                                            string charset = "utf-8")
    {
        var resp = new HttpResponseMessage(code)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
        };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset };
        return resp;
    }

    private sealed class FakeBrowser : IBrowserEngine
    {
        public bool Available { get; set; } = true;
        public int Calls;
        public string Html =
            "<html><body><article><h1>渲染标题</h1><p>" +
            "这是浏览器渲染之后才出现的正文内容。前端渲染页面在原始 HTML 里只有一个空容器，" +
            "正文要等 JavaScript 跑完才会出现，所以 HTTP 引擎读到的字符数远远不够，" +
            "必须升级到浏览器引擎才能拿到这一段。这里刻意写长一些，让它超过薄页面的判定门槛，" +
            "从而真实反映「渲染成功」这个场景。" +
            "</p></article></body></html>";
        public string Error = "";

        public Task<BrowserPage> FetchAsync(string url, string proxy, int idleSeconds,
                                            TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Error.Length > 0
                ? new BrowserPage(false, "", "", url, Error)
                : new BrowserPage(true, Html, "渲染标题", url, ""));
        }
    }

    /// <summary>一个"HTML 很大、正文几乎没有"的前端渲染页面。</summary>
    private static string SpaHtml() =>
        "<html><head><title>App</title></head><body><div id=\"root\"></div><script>" +
        new string('x', 25000) + "</script></body></html>";

    private static ProxyConfig Cfg(string engine, bool browserUsable = true) => new()
    {
        FetchEnabled = true,
        FetchEngine = engine,
        FetchBrowserUsable = browserUsable,
        FetchTimeoutSeconds = 10
    };

    private static async Task Engine()
    {
        // auto + 前端渲染页面 + 有浏览器 → 真的升级
        {
            var stub = new StubHandler { Reply = _ => Html(SpaHtml()) };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://spa.example/");
            Harness.Eq("auto：SPA 升级到浏览器", r.Engine, "browser");
            Harness.Eq("auto：浏览器被调用一次", browser.Calls, 1);
            Harness.True("auto：拿到渲染后的正文", r.Text.Contains("浏览器渲染之后才出现的正文"));
        }

        // auto + SPA + 没有浏览器 → 不升级，但如实说明
        {
            var stub = new StubHandler { Reply = _ => Html(SpaHtml()) };
            var browser = new FakeBrowser { Available = false };
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto", browserUsable: false), "https://spa.example/");
            Harness.Eq("auto：没有浏览器时不升级", r.Engine, "http");
            Harness.Eq("auto：没有浏览器时不调用", browser.Calls, 0);
            Harness.True("auto：明确说明可能是 JS 页面", r.Note.Contains("JavaScript"));
        }

        // auto + 正常页面 → 一次都不升级（这是绝大多数情况，也是"轻量默认"的意义）
        {
            var good = "<html><body><article><p>" + new string('字', 500) + "</p></article></body></html>";
            var stub = new StubHandler { Reply = _ => Html(good) };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://ok.example/");
            Harness.Eq("auto：正常页面走 HTTP", r.Engine, "http");
            Harness.Eq("auto：正常页面不起浏览器", browser.Calls, 0);
        }

        // engine=http → 即使正文很薄也绝不启动浏览器（用户明确要求别付那 500 MB）
        {
            var stub = new StubHandler { Reply = _ => Html(SpaHtml()) };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("http"), "https://spa.example/");
            Harness.Eq("engine=http：不起浏览器", browser.Calls, 0);
            Harness.Eq("engine=http：引擎标注正确", r.Engine, "http");
        }

        // engine=browser → 直接用浏览器，不先发一次 HTTP
        {
            var stub = new StubHandler { Reply = _ => Html("<html><body>不该被用到</body></html>") };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("browser"), "https://spa.example/");
            Harness.Eq("engine=browser：用浏览器", r.Engine, "browser");
            Harness.Eq("engine=browser：没有多余的 HTTP 请求", stub.Urls.Count, 0);
        }

        // 工具参数 render=true → 强制渲染
        {
            var stub = new StubHandler { Reply = _ => Html("<html><body>不该被用到</body></html>") };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://spa.example/", forceRender: true);
            Harness.Eq("render=true 强制走浏览器", r.Engine, "browser");
            Harness.Eq("render=true 不发 HTTP", stub.Urls.Count, 0);
        }

        // 浏览器渲染出来的反而更少（登录墙之类）→ 保留 HTTP 那份，并说明原因
        {
            var thinish = "<html><body><div>" + new string('字', 150) + "</div><script>" +
                          new string('x', 25000) + "</script></body></html>";
            var stub = new StubHandler { Reply = _ => Html(thinish) };
            var browser = new FakeBrowser
            {
                Html = "<html><body><p>请先登录后查看内容</p></body></html>"
            };
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://wall.example/");
            Harness.Eq("浏览器渲染更少时保留 HTTP 那份", r.Engine, "http");
            Harness.True("并说明了为什么保留", r.Note.Contains("反而更少"));
            Harness.True("内容确实是 HTTP 抓到的那份", r.Text.Contains(new string('字', 150)));
        }

        // 校验失败页（Cloudflare）→ 升级；浏览器也失败时两条原因都要给出来
        {
            var stub = new StubHandler
            {
                Reply = _ => Html("<html><head><title>Just a moment...</title></head>" +
                                 "<body>Checking your browser before accessing.</body></html>",
                                 HttpStatusCode.Forbidden)
            };
            var browser = new FakeBrowser { Error = "页面卡在 Cloudflare 之类的人机校验上" };
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("auto"), "https://cf.example/");
            Harness.Eq("校验页升级后浏览器确实被调用", browser.Calls, 1);
            Harness.Eq("两条失败都失败时如实报错", r.Ok, false);
            Harness.True("错误里同时含 HTTP 与浏览器两条原因",
                r.Error.Contains("403") && r.Error.Contains("人机校验"));
        }

        // 404 不升级
        {
            var stub = new StubHandler { Reply = _ => Html("<html><body>not found</body></html>", HttpStatusCode.NotFound) };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            await reader.FetchAsync(Cfg("auto"), "https://a.example/missing");
            Harness.Eq("404 不启动浏览器", browser.Calls, 0);
        }

        // 内网拦截默认生效，开关打开后放行
        {
            var stub = new StubHandler { Reply = _ => Html("<html><body><p>" + new string('字', 400) + "</p></body></html>") };
            using var reader = new PageReader(stub, new FakeBrowser());
            var blocked = await reader.FetchAsync(Cfg("http"), "http://127.0.0.1:8080/admin");
            Harness.Eq("默认拦截内网地址", blocked.Ok, false);
            Harness.True("拦截原因说得清楚", blocked.Error.Contains("内网"));

            var cfg = Cfg("http");
            cfg.FetchAllowPrivateHosts = true;
            var allowed = await reader.FetchAsync(cfg, "http://127.0.0.1:8080/admin");
            Harness.Eq("打开开关后放行", allowed.Ok, true);
        }

        // 非 HTML 内容类型如实拒绝，而不是硬当网页解析
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7"))
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            var stub = new StubHandler { Reply = _ => resp };
            var browser = new FakeBrowser();
            using var reader = new PageReader(stub, browser);
            var r = await reader.FetchAsync(Cfg("http"), "https://a.example/x.pdf");
            Harness.Eq("PDF 如实拒绝", r.Ok, false);
            Harness.True("拒绝原因含内容类型", r.Error.Contains("application/pdf"));
        }

        // 纯文本原样返回
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("纯文本内容\n第二行"))
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            var stub = new StubHandler { Reply = _ => resp };
            using var reader = new PageReader(stub, new FakeBrowser());
            var r = await reader.FetchAsync(Cfg("http"), "https://a.example/robots.txt");
            Harness.Eq("纯文本成功", r.Ok, true);
            Harness.True("纯文本内容保留", r.Text.Contains("第二行"));
        }

        // 工具结果信封：失败时绝不能看起来像"页面是空的"
        var envelope = PageReader.ToToolContent(
            new FetchResult(false, "https://a.com/", "", "", "http", "抓取超时（30 秒）", false, ""),
            "https://a.com/");
        Harness.True("失败信封写明不等于不存在", envelope.Contains("does NOT mean"));
        Harness.True("失败信封禁止编造", envelope.Contains("Do not invent"));

        // 前缀契约：web_fetch 的执行摘要必须被回归的计数判据认得
        Harness.True("摘要前缀表含 web_fetch", ToolHost.SummaryPrefixes.Contains("抓「"));
        Harness.Eq("前缀条数 == 工具种类数",
            ToolHost.SummaryPrefixes.Length, Enum.GetValues<ToolKind>().Length);

        // 浏览器瞬时连接错误的重试判据（实测：经代理抓 wikipedia 三次里有一次 ERR_CONNECTION_CLOSED）
        Harness.Eq("连接被关 → 值得重试",
            BrowserFetcher.IsTransientNav("导航失败：net::ERR_CONNECTION_CLOSED"), true);
        Harness.Eq("连接被重置 → 值得重试",
            BrowserFetcher.IsTransientNav("导航失败：net::ERR_CONNECTION_RESET"), true);
        Harness.Eq("域名解析不了 → 不重试（重试必然一样）",
            BrowserFetcher.IsTransientNav("导航失败：net::ERR_NAME_NOT_RESOLVED"), false);
        Harness.Eq("超时 → 不重试（再等一遍就是两倍延迟）",
            BrowserFetcher.IsTransientNav("导航失败：net::ERR_TIMED_OUT"), false);
        Harness.Eq("非导航错误 → 不重试",
            BrowserFetcher.IsTransientNav("页面卡在 Cloudflare 之类的人机校验上"), false);
    }

    // ---------------------------------------------------------------- 回收

    /// <summary>
    /// 浏览器实例与它那份临时用户数据目录的回收。
    ///
    /// 为什么值得单独钉住：**进程回收与目录回收是两件事**，而目录那一半曾经静默漏掉 ——
    /// 实测一次抓取在 %TEMP% 里留下 13.6 MB，反复调试期间积了 13 个目录、共 234 MB。
    /// 漏掉的机制是：删除是 fire-and-forget 的后台任务，而 Dispose 之后进程往往立刻退出
    /// （应用关闭、回归跑完），它根本没机会跑完。
    /// </summary>
    private static void Reclaim()
    {
        // 启动参数：少了 --headless=new 就会弹出一个**可见窗口**，而抓取照常成功、
        // 进程照常回收 —— 功能上完全看不出来，所以只能靠断言钉住。
        var args = BrowserFetcher.LaunchArgs(@"C:\tmp\prof", "http://127.0.0.1:10808");
        Harness.True("启动参数含 --headless=new（否则会弹出可见窗口）",
            args.Contains("--headless=new"));
        Harness.True("用户数据目录指向我们自己的临时目录",
            args.Contains(@"--user-data-dir=C:\tmp\prof"));
        Harness.True("代理透传给浏览器", args.Contains("--proxy-server=http://127.0.0.1:10808"));
        Harness.Eq("不给代理时不加 --proxy-server",
            BrowserFetcher.LaunchArgs(@"C:\tmp\prof", "   ").Any(a => a.StartsWith("--proxy-server")), false);
        Harness.True("用随机调试端口（固定端口会撞上用户自己开着的 Edge）",
            args.Contains("--remote-debugging-port=0"));

        // 目录名与扫描判据必须是同一个前缀 —— 两处走偏，残留就永远清不掉。
        var dir = BrowserFetcher.NewProfileDir();
        Harness.True("新建目录带统一前缀",
            Path.GetFileName(dir).StartsWith(BrowserFetcher.ProfilePrefix, StringComparison.Ordinal));
        Harness.True("目录建在 %TEMP% 下",
            dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));

        // 扫描在自己的临时根里验，不碰真的 %TEMP%
        var root = Path.Combine(Path.GetTempPath(), "aip-sweeptest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var stale = Path.Combine(root, BrowserFetcher.ProfilePrefix + "stale001");
            var fresh = Path.Combine(root, BrowserFetcher.ProfilePrefix + "fresh001");
            var other = Path.Combine(root, "some-other-dir");     // 不该被碰
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(stale, "x.txt"), "x");
            // 推到阈值之外（先建文件再改时间，否则建文件会把时间刷新）
            Directory.SetLastWriteTimeUtc(stale,
                DateTime.UtcNow - BrowserFetcher.StaleProfileAge - TimeSpan.FromMinutes(10));

            var removed = BrowserFetcher.SweepStaleProfiles(BrowserFetcher.StaleProfileAge, root);
            Harness.Eq("陈旧残留被清掉", removed, 1);
            Harness.Eq("陈旧目录确实没了", Directory.Exists(stale), false);
            Harness.Eq("新鲜目录被保留（可能是活着的实例）", Directory.Exists(fresh), true);
            Harness.Eq("不匹配前缀的目录一概不碰", Directory.Exists(other), true);
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // 边界：目录本来就不存在算成功（调用方要的是"它没了"），空路径/null 不抛。
        Harness.Eq("删不存在的目录算成功",
            BrowserFetcher.TryDeleteProfile(
                Path.Combine(Path.GetTempPath(), "aip-nope-" + Guid.NewGuid().ToString("N")[..8]), 1, 1), true);
        Harness.Eq("空路径不抛", BrowserFetcher.TryDeleteProfile("", 1, 1), true);
        Harness.Eq("null 不抛", BrowserFetcher.TryDeleteProfile(null, 1, 1), true);
    }

    // ---------------------------------------------------------------- 代理

    /// <summary>
    /// 桩 HTTP 代理：任何请求都回一段 HTML，并记下它看到的 Host + Path。
    /// 用它来证明"填了代理，请求真的从代理走" —— 这件事只有真发一次才验得出来。
    /// </summary>
    private sealed class StubProxy
    {
        private WebApplication? _app;
        public int Port { get; private set; }
        public List<string> Seen { get; } = new();

        public async Task StartAsync()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            b.Logging.ClearProviders();
            b.Logging.SetMinimumLevel(LogLevel.None);
            b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, Port));

            var app = b.Build();
            app.Run(async ctx =>
            {
                lock (Seen) Seen.Add((ctx.Request.Host.Value ?? "") + ctx.Request.Path.ToString());
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "<html><body><article><p>这是桩代理返回的正文内容，长度足够通过薄页面判定。</p>" +
                    "</article></body></html>");
            });

            _app = app;
            await app.StartAsync();
        }

        public async Task StopAsync()
        {
            if (_app is null) return;
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static async Task Proxy()
    {
        var proxy = new StubProxy();
        await proxy.StartAsync();
        try
        {
            var proxyUrl = $"http://127.0.0.1:{proxy.Port}";

            // 对照 A：填了代理 → 请求从代理走，拿回代理给的正文
            {
                var cfg = new ProxyConfig
                {
                    FetchEnabled = true,
                    FetchEngine = "http",
                    FetchProxy = proxyUrl,
                    FetchTimeoutSeconds = 15
                };
                using var reader = new PageReader();
                var r = await reader.FetchAsync(cfg, "http://aip-does-not-exist.invalid/page");
                Harness.Eq("填了代理后抓取成功", r.Ok, true);
                Harness.True("正文来自桩代理", r.Text.Contains("桩代理返回的正文"));
                Harness.True("代理确实收到了请求",
                    proxy.Seen.Any(s => s.Contains("aip-does-not-exist.invalid")));
            }

            // 对照 B：不填代理 → 同一个域名解析不了，必然失败。
            // 这一条是 A 的对照组：没有它，"A 成功"可能只是碰巧走了别的路。
            {
                var cfg = new ProxyConfig
                {
                    FetchEnabled = true,
                    FetchEngine = "http",
                    FetchProxy = "",
                    FetchTimeoutSeconds = 15
                };
                using var reader = new PageReader();
                var r = await reader.FetchAsync(cfg, "http://aip-does-not-exist.invalid/page");
                Harness.Eq("不填代理时同一地址失败（对照）", r.Ok, false);
            }

            // 对照 C：目标是**回环地址**，填了代理仍然要走代理。
            // 这一条钉的是 WebProxy.BypassProxyOnLocal：默认是 true，
            // 而 v2rayN 这类本地代理恰恰监听在回环上 —— 保持默认的话，
            // "填了本地代理"会被系统规则直接绕过，等于没填，且毫无提示。
            {
                var cfg = new ProxyConfig
                {
                    FetchEnabled = true,
                    FetchEngine = "http",
                    FetchProxy = proxyUrl,
                    FetchAllowPrivateHosts = true,
                    FetchTimeoutSeconds = 15
                };
                using var reader = new PageReader();
                var before = proxy.Seen.Count;
                // 9 号端口上没有任何服务：如果被绕过，就会是"连接被拒"
                var r = await reader.FetchAsync(cfg, "http://127.0.0.1:9/loopback");
                Harness.Eq("回环目标也走代理（不被 BypassProxyOnLocal 绕过）", r.Ok, true);
                Harness.True("代理收到了那个回环目标的请求",
                    proxy.Seen.Count > before && proxy.Seen.Any(s => s.Contains("127.0.0.1:9")));
            }
        }
        finally
        {
            await proxy.StopAsync();
        }
    }
}
