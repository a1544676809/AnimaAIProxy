using System.Net;
using System.Text;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 联网搜索的离线回归：工具注入门槛、配置判据、五个后端的请求形状与解析、
/// 回灌内容的截断，以及"故障与空结果必须分开说"。
///
/// **一条请求都不发到网上**：全部请求交给 <see cref="FakeHandler"/>，
/// 它按剧本回话并把每个请求的形状（方法、URL、头、体）记下来。
/// 这样验的不只是"解析对不对"，还包括**发出去的请求对不对**——
/// 后者失效时的症状是上游回 401 或 400，与我们的代码毫无字面关联。
/// </summary>
public static class Search
{
    // ---------------- 假 HTTP ----------------

    /// <summary>一次请求的形状快照。**必须快照**：HttpRequestMessage 在
    /// SearchAsync 的 using 结束时就被释放了，事后再去读它的头不可靠。</summary>
    internal sealed record Sent(string Method, string Url, Dictionary<string, string> Headers, string Body)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<Sent, (HttpStatusCode Status, string Body)> _respond;

        public List<Sent> Requests { get; } = new();

        /// <summary>让响应先等一会儿。**必须可取消**：超时那条路就是靠它验的，
        /// 而不可取消的 Thread.Sleep 会让"超时"根本发生不了。</summary>
        public int DelayMs { get; set; }

        public FakeHandler(Func<Sent, (HttpStatusCode, string)> respond) => _respond = respond;

        public FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this(_ => (status, body)) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (DelayMs > 0) await Task.Delay(DelayMs, ct).ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content is not null)
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);

            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var sent = new Sent(request.Method.Method, request.RequestUri?.ToString() ?? "", headers, body);
            lock (Requests) Requests.Add(sent);

            var (status, text) = _respond(sent);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(text, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>把一份配置配成"可用"的 searxng（地址指向假后端）。</summary>
    private static ProxyConfig SearxngCfg(string url = "http://127.0.0.1:9") => new()
    {
        SearchEnabled = true,
        SearchProvider = "searxng",
        SearchBaseUrl = url,
        SearchMaxResults = 5
    };

    /// <summary>
    /// 从 URL 里取一个查询参数（已解码）。自己解析而不用 HttpUtility：
    /// 少一个"这个类型在不在共享框架里"的疑问，也让断言读起来就是它的意思。
    /// </summary>
    private static string QueryOf(string url, string key)
    {
        var q = new Uri(url).Query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i <= 0) continue;
            if (pair[..i] == key) return Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        return "";
    }

    // ---------------- 主入口 ----------------

    public static void Run()
    {
        Gate();
        ConfigJudgement();
        ProviderRequestShapes();
        Parsing();
        ContentShaping();
        HostExecution();
    }

    // ---------------- 1. 工具注入门槛 ----------------

    private static void Gate()
    {
        Harness.Section("联网搜索 1. 工具注入门槛（没配好后端就不给模型这个工具）");

        var plain = ToolCatalog.Plan(new[] { "read_file" });
        Harness.Eq("默认（不带 web_search）是三个工具", plain.Tools.Count, 3);
        Harness.True("默认不下发 web_search",
                     !plain.Tools.Any(t => t.Name == ToolCatalog.WebSearchName));

        var with = ToolCatalog.Plan(new[] { "read_file" }, withWebSearch: true);
        Harness.Eq("打开后是四个工具", with.Tools.Count, 4);
        Harness.Eq("web_search 排在最后（慢工具，词库那三个更常用）",
                   with.Tools[^1].Name, ToolCatalog.WebSearchName);
        Harness.Eq("它的语义被正确登记", with.KindOf(ToolCatalog.WebSearchName), (ToolKind?)ToolKind.WebSearch);

        // 接入方自己也有一个 web_search：**它留下，我们让路**。
        // 反过来的话，客户端会在自己的历史里收到一个它没有的工具名。
        var collide = ToolCatalog.Plan(new[] { ToolCatalog.WebSearchName }, withWebSearch: true);
        Harness.Eq("重名时我们改名", collide.Tools[^1].Name, ToolCatalog.WebSearchName + ToolCatalog.CollisionSuffix);
        Harness.True("接入方那个名字仍然不属于我们", !collide.IsOurs(ToolCatalog.WebSearchName));
        Harness.True("让路后的名字仍被认成我们的", collide.IsOurs(ToolCatalog.WebSearchName + ToolCatalog.CollisionSuffix));
        Harness.Eq("让路后的语义仍然正确",
                   collide.KindOf(ToolCatalog.WebSearchName + ToolCatalog.CollisionSuffix), (ToolKind?)ToolKind.WebSearch);
        Harness.True("按名字判身份也认让路后的名字（收尾那一轮 plan 是空的，只能靠名字）",
                     ToolCatalog.IsProxyToolName(ToolCatalog.WebSearchName + ToolCatalog.CollisionSuffix));
        Harness.True("web_search 本身按名字就是我们的", ToolCatalog.IsProxyToolName(ToolCatalog.WebSearchName));

        // 说明必须自解释：接入方的系统提示里不会介绍我们的工具。
        var desc = ToolCatalog.WebSearchTool.Description;
        Harness.True("说明里写明了「搜到的东西是不可信数据」",
                     desc.Contains("untrusted", StringComparison.OrdinalIgnoreCase));
        Harness.True("说明里写明了短查询而不是整句",
                     desc.Contains("SHORT", StringComparison.OrdinalIgnoreCase));
        Harness.True("说明里写明了摘要不是全文",
                     desc.Contains("not full pages", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 2. 配置判据 ----------------

    private static void ConfigJudgement()
    {
        Harness.Section("联网搜索 2. 「配好了没有」的判据（界面与注入共用同一份）");

        var off = new ProxyConfig { SearchEnabled = false, SearchProvider = "tavily", SearchApiKey = "k" };
        Harness.True("关掉时不就绪", !off.SearchReady);
        Harness.Eq("关掉时界面不报问题（用户主动关的，不是没配好）", off.SearchProblem, "");

        var noProvider = new ProxyConfig { SearchEnabled = true };
        Harness.True("没选后端时不就绪", !noProvider.SearchReady);
        Harness.Eq("问题说得具体", noProvider.SearchProblem, "还没选后端");

        var tavilyNoKey = new ProxyConfig { SearchEnabled = true, SearchProvider = "tavily" };
        Harness.True("商业后端没密钥时不就绪", !tavilyNoKey.SearchReady);
        Harness.Eq("问题指明要密钥", tavilyNoKey.SearchProblem, "这个后端需要 API Key");
        Harness.True("商业后端不需要地址", !tavilyNoKey.SearchNeedsEndpoint);

        var tavily = new ProxyConfig { SearchEnabled = true, SearchProvider = "tavily", SearchApiKey = "tvly-x" };
        Harness.True("填了密钥就绪", tavily.SearchReady);
        Harness.Eq("就绪时没有问题文本", tavily.SearchProblem, "");

        var searxngNoUrl = new ProxyConfig { SearchEnabled = true, SearchProvider = "searxng" };
        Harness.True("自建后端没地址时不就绪", !searxngNoUrl.SearchReady);
        Harness.Eq("问题指明要地址", searxngNoUrl.SearchProblem, "这个后端需要填地址");
        Harness.True("自建后端不需要密钥", !searxngNoUrl.SearchNeedsKey);

        // shim 也要密钥——这一条与 AnimaCaptioner 不同，依据是对真实网关的实测：
        // 不带密钥请求 nas.ddger.top:49977 会回 401 authentication_error。
        // 若把它当"可选"，工具就会被下发，而每次调用必然失败。
        var shimNoKey = new ProxyConfig
        {
            SearchEnabled = true, SearchProvider = "shim", SearchBaseUrl = "https://gw.example:49977"
        };
        Harness.True("shim 没密钥时不就绪（实测网关会回 401）", !shimNoKey.SearchReady);
        Harness.Eq("shim 缺密钥时提示指明要密钥", shimNoKey.SearchProblem, "这个后端需要 API Key");
        shimNoKey.SearchApiKey = "k";
        Harness.True("填了密钥就绪", shimNoKey.SearchReady);
        Harness.True("shim 同时需要地址", shimNoKey.SearchNeedsEndpoint);

        // 后端名大小写与空格都要收敛；认不出的值**不是错，只是没有后端**。
        var messy = new ProxyConfig { SearchEnabled = true, SearchProvider = "  Tavily " };
        Harness.Eq("后端名归一化", messy.SearchProviderKey, "tavily");
        var bogus = new ProxyConfig { SearchEnabled = true, SearchProvider = "google" };
        Harness.Eq("认不出的后端收敛成空", bogus.SearchProviderKey, "");
        Harness.True("认不出的后端 = 不就绪", !bogus.SearchReady);

        // 界面上的实时提示与实例判据必须是同一个函数（否则会出现
        // "界面说已就绪、实际没下发工具"这种没有任何症状的分叉）。
        Harness.Eq("ProblemFor 与实例判据一致",
                   ProxyConfig.ProblemFor(true, "tavily", hasKey: false, keyUnreadable: false, baseUrl: ""),
                   "这个后端需要 API Key");
        Harness.Eq("密钥解不开时单独提示",
                   ProxyConfig.ProblemFor(true, "tavily", hasKey: false, keyUnreadable: true, baseUrl: ""),
                   "密钥解不开，请重新填写");

        // 越界值拉回区间
        var bounds = new ProxyConfig { SearchMaxResults = 99, SearchTimeoutSeconds = 9999 };
        bounds.Normalize();
        Harness.Eq("条数拉回上限", bounds.SearchMaxResults, 5);
        Harness.Eq("超时拉回上限", bounds.SearchTimeoutSeconds, 30);
    }

    // ---------------- 3. 五个后端的请求形状 ----------------

    private static void ProviderRequestShapes()
    {
        Harness.Section("联网搜索 3. 五个后端的请求形状（URL / 认证头 / 体）");

        // --- searxng：GET + query，不需要密钥 ---
        {
            using var handler = new FakeHandler("{\"results\":[]}");
            using var client = new SearchClient(handler);
            var cfg = SearxngCfg("http://127.0.0.1:8888/");
            client.SearchAsync(cfg, "white dress").GetAwaiter().GetResult();

            var r = handler.Requests[0];
            Harness.Eq("searxng 用 GET", r.Method, "GET");
            // 用 Uri 解析而不是比字符串：Uri.ToString() 会把 %20 显示成空格，
            // 比字符串就等于在测 .NET 的转义风格，而不是"地址拼对了没有"。
            var uri = new Uri(r.Url);
            Harness.Eq("路径是 /search", uri.AbsolutePath, "/search");
            Harness.Eq("查询词原样送达（空格已转义）", QueryOf(r.Url, "q"), "white dress");
            Harness.Eq("要求 JSON 格式（SearXNG 不加这个参数会回 HTML）",
                       QueryOf(r.Url, "format"), "json");
            Harness.True("尾斜杠不会拼成双斜杠", !r.Url.Contains("//search", StringComparison.Ordinal));
            Harness.Eq("没配密钥就不发 Authorization", r.Header("Authorization"), "");
            Harness.Eq("声明接受 JSON", r.Header("Accept"), "application/json");
        }

        // --- tavily：POST + Bearer ---
        {
            using var handler = new FakeHandler("{\"results\":[]}");
            using var client = new SearchClient(handler);
            var cfg = new ProxyConfig
            {
                SearchEnabled = true, SearchProvider = "tavily", SearchApiKey = "tvly-secret",
                SearchMaxResults = 3
            };
            client.SearchAsync(cfg, "qt 6.8").GetAwaiter().GetResult();

            var r = handler.Requests[0];
            Harness.Eq("tavily 用 POST", r.Method, "POST");
            Harness.Eq("打的是官方域名", r.Url, "https://api.tavily.com/search");
            Harness.Eq("密钥走 Bearer", r.Header("Authorization"), "Bearer tvly-secret");
            Harness.True("体里有查询词与条数",
                         r.Body.Contains("\"query\":\"qt 6.8\"") && r.Body.Contains("\"max_results\":3"));
            Harness.True("不要上游替模型先下结论（include_answer=false）",
                         r.Body.Contains("\"include_answer\":false"));
        }

        // --- brave：GET + X-Subscription-Token ---
        {
            using var handler = new FakeHandler("{\"web\":{\"results\":[]}}");
            using var client = new SearchClient(handler);
            var cfg = new ProxyConfig
            {
                SearchEnabled = true, SearchProvider = "brave", SearchApiKey = "brave-key"
            };
            client.SearchAsync(cfg, "test").GetAwaiter().GetResult();

            var r = handler.Requests[0];
            Harness.Eq("brave 用 GET", r.Method, "GET");
            Harness.True("查询与条数在 URL 里", r.Url.Contains("q=test") && r.Url.Contains("count="));
            Harness.Eq("密钥走 X-Subscription-Token", r.Header("X-Subscription-Token"), "brave-key");
            Harness.Eq("不要错发 Authorization", r.Header("Authorization"), "");
        }

        // --- serper：POST + X-API-KEY ---
        {
            using var handler = new FakeHandler("{\"organic\":[]}");
            using var client = new SearchClient(handler);
            var cfg = new ProxyConfig
            {
                SearchEnabled = true, SearchProvider = "serper", SearchApiKey = "serper-key"
            };
            client.SearchAsync(cfg, "test").GetAwaiter().GetResult();

            var r = handler.Requests[0];
            Harness.Eq("serper 用 POST", r.Method, "POST");
            Harness.Eq("打的是官方域名", r.Url, "https://google.serper.dev/search");
            Harness.Eq("密钥走 X-API-KEY", r.Header("X-API-KEY"), "serper-key");
            Harness.True("体的字段名是 q/num（不是 query/max_results）",
                         r.Body.Contains("\"q\":\"test\"") && r.Body.Contains("\"num\":"));
        }

        // --- shim：POST /v1/messages + Anthropic 头 + Host 覆盖 ---
        {
            using var handler = new FakeHandler("{\"content\":[]}");
            using var client = new SearchClient(handler);
            var cfg = new ProxyConfig
            {
                SearchEnabled = true, SearchProvider = "shim",
                SearchBaseUrl = "https://192.0.2.10:49977",
                SearchSniHost = "nas.example.top",
                SearchApiKey = "shim-key"
            };
            client.SearchAsync(cfg, "test").GetAwaiter().GetResult();

            var r = handler.Requests[0];
            Harness.Eq("shim 打 /v1/messages", r.Url, "https://192.0.2.10:49977/v1/messages");
            Harness.Eq("Host 头写成 SNI 名（不一致时反向代理会静默回 200 空体）",
                       r.Header("Host"), "nas.example.top");
            Harness.Eq("带 anthropic-version", r.Header("anthropic-version"), "2023-06-01");
            Harness.Eq("密钥同时走 x-api-key", r.Header("x-api-key"), "shim-key");
            Harness.True("体里声明了 web_search server tool",
                         r.Body.Contains("web_search_20250305"));
        }

        // --- 密钥缺失：不发请求，直接如实报错 ---
        {
            using var handler = new FakeHandler("{}");
            using var client = new SearchClient(handler);
            var cfg = new ProxyConfig { SearchEnabled = true, SearchProvider = "tavily" };
            var res = client.SearchAsync(cfg, "x").GetAwaiter().GetResult();
            Harness.True("没密钥时如实失败", !res.Ok);
            Harness.Eq("而且一个请求都没发（不拿必然 401 的请求去撞上游）", handler.Requests.Count, 0);
        }

        // --- 连续两次搜索都要能成功 ---
        // 这一条钉着一个真实的坑：超时若靠改 HttpClient.Timeout 实现，
        // **第二次**搜索会抛 "Properties can only be modified before sending the
        // first request"，症状是"第一次能搜、之后每次都失败"。
        {
            using var handler = new FakeHandler("{\"results\":[{\"title\":\"t\",\"url\":\"u\",\"content\":\"c\"}]}");
            using var client = new SearchClient(handler);
            var cfg = SearxngCfg();
            var a = client.SearchAsync(cfg, "first").GetAwaiter().GetResult();
            var b = client.SearchAsync(cfg, "second").GetAwaiter().GetResult();
            Harness.True("第一次搜索成功", a.Ok && a.Hits.Count == 1);
            Harness.True("第二次搜索同样成功（超时不靠改 HttpClient.Timeout）", b.Ok && b.Hits.Count == 1);
            Harness.Eq("两次请求都真的发出去了", handler.Requests.Count, 2);
        }

        // --- 超时：如实说是超时，不是"没结果" ---
        // 等待必须**超过**超时值，而且等待本身要可取消，否则这一条测的是
        // "慢响应最终成功"。超时下限是 5 秒（配置项的合法区间），所以这里等 8 秒。
        {
            using var handler = new FakeHandler("{}") { DelayMs = 8000 };
            using var client = new SearchClient(handler);
            var cfg = SearxngCfg();
            cfg.SearchTimeoutSeconds = 5;
            var res = client.SearchAsync(cfg, "slow").GetAwaiter().GetResult();
            Harness.True("超时被判成失败而不是空结果", !res.Ok);
            Harness.True("错误里说明是超时", res.Error.Contains("超时", StringComparison.Ordinal));
            Harness.True("超时的正文里也带上这句（模型据此重试或放弃）",
                         SearchClient.ToToolContent(res, "slow").Contains("\"error\"", StringComparison.Ordinal));
        }
    }

    // ---------------- 4. 解析 ----------------

    private static void Parsing()
    {
        Harness.Section("联网搜索 4. 五个后端的响应解析");

        var searxng = SearchClient.ParseSearxng(
            "{\"results\":[{\"title\":\"A\",\"url\":\"https://a\",\"content\":\"正文\"}," +
            "{\"title\":\"B\",\"url\":\"https://b\",\"snippet\":\"摘要\"}]}", 10);
        Harness.Eq("searxng 条数", searxng.Hits.Count, 2);
        Harness.Eq("searxng 取 content", searxng.Hits[0].Snippet, "正文");
        Harness.Eq("searxng content 缺了才回落 snippet", searxng.Hits[1].Snippet, "摘要");

        // number_of_results 常是 0 而 results 非空，所以不能拿它当计数；
        // 引擎全挂时必须能说出来，否则"没结果"会被当成"这东西不存在"。
        var down = SearchClient.ParseSearxng(
            "{\"number_of_results\":0,\"results\":[],\"unresponsive_engines\":[[\"google\",\"timeout\"]]}", 10);
        Harness.Eq("引擎全挂时没有结果", down.Hits.Count, 0);
        Harness.True("引擎全挂时给出问题说明（而不是静默的空结果）",
                     down.Problem.Contains("没有可用引擎", StringComparison.Ordinal));

        var tavily = SearchClient.ParseTavily(
            "{\"results\":[{\"title\":\"T\",\"url\":\"https://t\",\"content\":\"c\"}]}", 10);
        Harness.Eq("tavily 条数", tavily.Count, 1);
        Harness.Eq("tavily 字段", tavily[0].Url, "https://t");

        var brave = SearchClient.ParseBrave(
            "{\"web\":{\"results\":[{\"title\":\"B\",\"url\":\"https://b\",\"description\":\"d\"}]}}", 10);
        Harness.Eq("brave 从 web.results 取", brave.Count, 1);
        Harness.Eq("brave 取 description", brave[0].Snippet, "d");

        var serper = SearchClient.ParseSerper(
            "{\"organic\":[{\"title\":\"S\",\"link\":\"https://s\",\"snippet\":\"sn\"}]}", 10);
        Harness.Eq("serper 从 organic 取", serper.Count, 1);
        Harness.Eq("serper 的网址字段叫 link（不是 url）", serper[0].Url, "https://s");

        // shim：摘要不在 web_search_tool_result 里，要靠 citations 按 url 配对取回。
        var shim = SearchClient.ParseShim(
            "{\"content\":[" +
            "{\"type\":\"web_search_tool_result\",\"content\":[" +
            "{\"title\":\"S1\",\"url\":\"https://s1\"},{\"title\":\"S2\",\"url\":\"https://s2\"}]}," +
            "{\"type\":\"text\",\"citations\":[{\"url\":\"https://s1\",\"cited_text\":\"配对上的正文\"}]}]}", 10);
        Harness.Eq("shim 条数", shim.Count, 2);
        Harness.Eq("shim 摘要按 url 配对取回", shim[0].Snippet, "配对上的正文");
        Harness.Eq("配不上的那条留空，而不是错配", shim[1].Snippet, "");

        // 条数上限必须真的生效
        var many = SearchClient.ParseTavily(
            "{\"results\":[{\"url\":\"1\"},{\"url\":\"2\"},{\"url\":\"3\"}]}", 2);
        Harness.Eq("条数上限生效", many.Count, 2);

        // 换行会被压平（模型读到多行摘要会把结构读乱）
        var multiline = SearchClient.ParseTavily(
            "{\"results\":[{\"url\":\"u\",\"content\":\"a\\nb\\r\\nc\"}]}", 10);
        Harness.Eq("摘要里的换行被压平", multiline[0].Snippet, "a b  c");
    }

    // ---------------- 5. 回灌给模型的内容 ----------------

    private static void ContentShaping()
    {
        Harness.Section("联网搜索 5. 回灌内容（截断 / 故障与空结果分开说）");

        // 故障与"没有结果"是两件事：混成一句，模型会把网络故障当成
        // "这东西不存在"，然后凭记忆编答案——那正是引入搜索要消灭的东西。
        var failed = SearchClient.ToToolContent(
            new SearchClient.SearchResult(false, new(), "连接被拒绝"), "q");
        Harness.True("失败时带 error 字段", failed.Contains("\"error\"", StringComparison.Ordinal));
        Harness.True("失败时明确说明「这不代表那东西不存在」",
                     failed.Contains("does NOT mean", StringComparison.Ordinal));

        var empty = SearchClient.ToToolContent(
            new SearchClient.SearchResult(true, new(), ""), "q");
        Harness.True("空结果里没有 error 字段", !empty.Contains("\"error\"", StringComparison.Ordinal));
        Harness.True("空结果如实说没有结果", empty.Contains("No results", StringComparison.Ordinal));

        // 中文不许被转义成 \uXXXX：这些文本是给人看的诊断，也是给模型读的正文。
        var cn = SearchClient.ToToolContent(
            new SearchClient.SearchResult(true, new() { new SearchClient.Hit("标题", "https://x", "中文摘要") }, ""), "中文查询");
        Harness.True("中文原样保留（不转义成 \\uXXXX）", cn.Contains("中文摘要", StringComparison.Ordinal));

        // 截断：单条上限与总量上限都要生效，否则整页正文会把用户的问题挤出上下文。
        var longSnippet = new string('x', 5000);
        var many = new SearchClient.SearchResult(true, Enumerable.Range(0, 10)
            .Select(i => new SearchClient.Hit("t" + i, "https://x/" + i, longSnippet)).ToList(), "");
        var shaped = SearchClient.ToToolContent(many, "q");
        Harness.True("总量被截断（不会把 10 条 5000 字的摘要原样塞回去）",
                     shaped.Length < SearchClient.TotalChars + 600);
        Harness.True("截断后仍然是合法 JSON", ChatWire.TryParseObject(shaped) is not null);
    }

    // ---------------- 6. 经工具层执行 ----------------

    private static void HostExecution()
    {
        Harness.Section("联网搜索 6. 经 ToolHost 执行（含未配置时的兜底）");

        // 未配置：如实告诉模型"搜不了"，并且**明确禁止它假装搜过**。
        var host = new ToolHost(new VocabService(), new SearchClient(new FakeHandler("{}")));
        var unconfigured = host.ExecuteAsync(new ProxyConfig(), ToolKind.WebSearch, "{\"query\":\"x\"}")
                               .GetAwaiter().GetResult();
        Harness.True("未配置时如实说明不可用",
                     unconfigured.Text.Contains("not available", StringComparison.OrdinalIgnoreCase));
        Harness.True("并且明确禁止编造来源",
                     unconfigured.Text.Contains("do not invent sources", StringComparison.OrdinalIgnoreCase));
        Harness.True("摘要用中文前缀，便于日志比对",
                     unconfigured.Summary.StartsWith("搜索", StringComparison.Ordinal));

        // 空查询：要求重试，而不是拿空串去搜
        var emptyQuery = host.ExecuteAsync(SearxngCfg(), ToolKind.WebSearch, "{}")
                             .GetAwaiter().GetResult();
        Harness.True("空查询时要求重试", emptyQuery.Text.Contains("query was empty", StringComparison.OrdinalIgnoreCase));
        Harness.Eq("空查询的摘要", emptyQuery.Summary, "搜索（空查询）");

        // 正常执行：结果进 Text，摘要带条数
        using var handler = new FakeHandler(
            "{\"results\":[{\"title\":\"Qt 6.8\",\"url\":\"https://qt.io/68\",\"content\":\"2024 年 10 月发布\"}]}");
        var ok = new ToolHost(new VocabService(), new SearchClient(handler));
        var res = ok.ExecuteAsync(SearxngCfg(), ToolKind.WebSearch, "{\"query\":\"Qt 6.8\"}")
                    .GetAwaiter().GetResult();
        Harness.True("结果里有网址", res.Text.Contains("https://qt.io/68", StringComparison.Ordinal));
        Harness.True("结果里有摘要", res.Text.Contains("2024 年 10 月发布", StringComparison.Ordinal));
        Harness.Eq("摘要带条数", res.Summary, "搜索「Qt 6.8」→ 1 条");

        // 参数宽容度：模型把参数写成裸字符串也要能搜
        var loose = ok.ExecuteAsync(SearxngCfg(), ToolKind.WebSearch, "Qt 6.8")
                      .GetAwaiter().GetResult();
        Harness.Eq("裸字符串参数也能搜", loose.Summary, "搜索「Qt 6.8」→ 1 条");

        // 后端报错：摘要里说明失败，正文里带 error（模型据此决定重试还是放弃）
        using var bad = new FakeHandler("{}", HttpStatusCode.Unauthorized);
        var failing = new ToolHost(new VocabService(), new SearchClient(bad));
        var failRes = failing.ExecuteAsync(SearxngCfg(), ToolKind.WebSearch, "{\"query\":\"x\"}")
                            .GetAwaiter().GetResult();
        Harness.True("失败时正文里有 error", failRes.Text.Contains("\"error\"", StringComparison.Ordinal));
        Harness.True("失败时摘要写明失败", failRes.Summary.Contains("失败", StringComparison.Ordinal));
    }
}
