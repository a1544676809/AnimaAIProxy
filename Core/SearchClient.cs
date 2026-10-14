using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 联网搜索客户端：<c>web_search</c> 工具的执行体。
///
/// 移植自 AnimaCaptioner 的 <c>Core/SearchClient.cs</c>（同一作者、同一批后端、
/// 同样的实测结论），按代理这边的形状改了三处：
/// 1. 参数类型从 <c>AppSettings</c> 换成 <see cref="ProxyConfig"/>；
/// 2. 日志走 <see cref="ProxyLog"/>；
/// 3. 解析与摘要拼装抽成**纯静态函数**，并且 <see cref="HttpMessageHandler"/> 可注入，
///    于是五个后端能在离线回归里连"请求长什么样"一起验（见 tests/Search.cs）。
///
/// 所有后端共用一套返回结构，上层工具循环不必关心用的是哪家：
///
/// - **searxng**：直连一个能访问的 SearXNG 实例的 JSON 接口，无需密钥。
/// - **tavily / brave / serper**：商业 API，只需填 Key。
/// - **shim**：Anthropic 兼容的搜索网关（自建，对外是 <c>POST /v1/messages</c>，
///   让 <c>web_search_20250305</c> 这个 server tool 由自建 SearXNG 兑现，
///   于是搜索在内网完成、不依赖外部 API）。
///
/// 关于 shim 这一路，有两个**实测得出**、非试不可的细节（AnimaCaptioner 那边踩过）：
///
/// 1. **必须覆盖 TLS SNI**。反向代理按 SNI 选站点，直连 IP 时 SNI 会是那个 IP，
///    代理找不到站点，直接回 <c>TLS alert: InternalError</c>。
///    <c>SocketsHttpHandler.SslOptions.TargetHost</c> **不改变 SNI**；
///    只有自己建 <c>SslStream</c> 并 <c>AuthenticateAsClientAsync(TargetHost = ...)</c>
///    才管用，所以走 ConnectCallback。
/// 2. **<c>Host</c> 头也必须写成 SNI 名**。用 IP 当 Host 时反向代理会返回
///    <c>200 OK</c> + <c>Content-Length: 0</c>——一个**静默的空响应**，
///    既不报错也没内容。所以这里对空响应专门给一句可操作的提示。
///
/// 另一个刻意的约束：**回灌给模型的内容必须截断**。把整页正文塞回去会把用户真正
/// 问的东西挤出上下文，反而答得更差。所以每条只留标题 + 网址 + 一段摘要。
/// </summary>
public sealed class SearchClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _shimHttp;

    /// <summary>本次请求要用的 SNI/Host 名（shim 用）。发请求前从配置取。</summary>
    private string _sni = "";

    /// <summary>单条摘要字符上限。够说明"这是什么"，又不至于淹没提示词。</summary>
    internal const int SnippetChars = 400;

    /// <summary>回灌内容总字符上限，兜底防止多条长摘要叠加过大。</summary>
    internal const int TotalChars = 2400;

    public string LastError { get; private set; } = "";

    public sealed record Hit(string Title, string Url, string Snippet);

    public sealed record SearchResult(bool Ok, List<Hit> Hits, string Error);

    /// <summary>
    /// <paramref name="handler"/> 只给回归用：传进来就接管全部请求（含 shim 那条），
    /// 于是五个后端的请求形状与响应解析都能离线验。生产路径传 null。
    /// </summary>
    public SearchClient(HttpMessageHandler? handler = null)
    {
        if (handler is not null)
        {
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _shimHttp = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return;
        }

        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // shim 走局域网 IP，但 TLS 握手要报 SNI 名、HTTP 还要带同名 Host。
        // 只能自己建流，HttpClient 的常规开关做不到（见类注释）。
        var shimHandler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var sni = _sni;
                var tcp = new TcpClient();
                await tcp.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct)
                         .ConfigureAwait(false);
                var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = sni,   // <- 关键：覆盖 SNI
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, ct).ConfigureAwait(false);
                }
                catch
                {
                    ssl.Dispose();
                    tcp.Dispose();
                    throw;
                }
                return ssl;
            },
        };
        _shimHttp = new HttpClient(shimHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>发一次搜索。<paramref name="cfg"/> 决定用哪个后端。</summary>
    public async Task<SearchResult> SearchAsync(ProxyConfig cfg, string query, CancellationToken ct = default)
    {
        LastError = "";
        query = (query ?? "").Trim();
        if (query.Length == 0) return Fail("搜索词为空");

        _sni = string.IsNullOrWhiteSpace(cfg.SearchSniHost)
            ? HostOf(cfg.SearchBaseUrl)
            : cfg.SearchSniHost.Trim();

        var timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.SearchTimeoutSeconds, 5, 120));

        // 超时用**每次请求一个 CTS**，而不是去改 HttpClient.Timeout：
        // 那个属性只能在发出第一个请求之前设置，第二次搜索就会抛
        // "Properties can only be modified before sending the first request"，
        // 而症状是"第一次能搜、之后每次都失败"——非常容易被当成上游的问题。
        // 两个 HttpClient 的 Timeout 因此都设成无限，超时全权交给这个 CTS。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        try
        {
            return cfg.SearchProviderKey switch
            {
                "tavily" => await TavilyAsync(cfg, query, token).ConfigureAwait(false),
                "brave" => await BraveAsync(cfg, query, token).ConfigureAwait(false),
                "serper" => await SerperAsync(cfg, query, token).ConfigureAwait(false),
                "searxng" => await SearxngAsync(cfg, query, token).ConfigureAwait(false),
                "shim" => await ShimAsync(cfg, query, token).ConfigureAwait(false),
                _ => Fail("没有配置搜索后端"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 只把"超时"算成超时。调用方主动取消（客户端断了连接）不该被报成
            // "搜索超时"，那会把一次正常的断开变成一条误导性的诊断。
            LastError = "搜索超时";
            return Fail($"搜索超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ProxyLog.Write("搜索失败：" + ex.Message);
            return Fail(ex.Message);
        }
    }

    /// <summary>连通性自检，供界面上的"测试搜索"用。固定用一个无歧义的中性查询词。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(ProxyConfig cfg, CancellationToken ct = default)
    {
        var r = await SearchAsync(cfg, "Danbooru tag", ct).ConfigureAwait(false);
        if (!r.Ok) return (false, r.Error);
        if (r.Hits.Count == 0)
            return (false, "接口通了但没有返回结果——检查实例的引擎是否可用");
        var withSnippet = r.Hits.Count(h => h.Snippet.Length > 0);
        return (true, $"连接成功，返回 {r.Hits.Count} 条（{withSnippet} 条带摘要）。首条：{Trunc(r.Hits[0].Title, 60)}");
    }

    // ---- shim：Anthropic 兼容的搜索网关 ----

    /// <summary>
    /// 用 Anthropic 的 server tool 协议要搜索结果。
    ///
    /// 请求体里带上 <c>web_search_20250305</c> 工具，网关会把 SearXNG 的结果放进
    /// <c>web_search_tool_result</c> 块。**摘要不在那个块里**——结果项只有
    /// url/title/page_age，正文在 <c>citations[].cited_text</c>，要靠 url 配对取回。
    /// 这个结构是照 Anthropic 官方 schema 做的，不配对就等于没有摘要可用。
    /// </summary>
    private async Task<SearchResult> ShimAsync(ProxyConfig cfg, string query, CancellationToken ct)
    {
        var baseUrl = cfg.SearchBaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return Fail("shim 需要填搜索网关地址");

        var body = new JsonObject
        {
            ["model"] = "searxng-shim",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = query }
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 3,
                }
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };

        // Host 必须和 SNI 一致，否则反向代理回 200 + 空体（见类注释）
        if (_sni.Length > 0) req.Headers.Host = _sni;
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var key = cfg.SearchApiKey.Trim();
        if (key.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var resp = await _shimHttp.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"搜索网关 HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));
        if (raw.Length == 0)
            return Fail("搜索网关返回了空响应——通常是 Host 头与 TLS SNI 不一致（反向代理会静默返回 200 空体）");

        var hits = ParseShim(raw, cfg.SearchMaxResults);
        ProxyLog.Write($"搜索 shim「{query}」→ {hits.Count} 条");
        return new SearchResult(true, hits, "");
    }

    /// <summary>
    /// 解析 shim 的响应。抽成纯函数是为了能离线测（真实网关只有作者内网才有）。
    /// </summary>
    internal static List<Hit> ParseShim(string raw, int max)
    {
        using var doc = JsonDocument.Parse(raw);

        // 先收摘要：url -> cited_text
        var snippets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("content", out var blocks) &&
            blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                if (!b.TryGetProperty("citations", out var cits) ||
                    cits.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in cits.EnumerateArray())
                {
                    var u = Str(c, "url");
                    var t = Str(c, "cited_text");
                    if (u.Length > 0 && t.Length > 0) snippets.TryAdd(u, t);
                }
            }
        }

        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("content", out var blocks2) &&
            blocks2.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks2.EnumerateArray())
            {
                if (Str(b, "type") != "web_search_tool_result") continue;
                if (!b.TryGetProperty("content", out var inner) ||
                    inner.ValueKind != JsonValueKind.Array) continue;

                foreach (var it in inner.EnumerateArray())
                {
                    if (hits.Count >= max) break;
                    var url = Str(it, "url");
                    if (url.Length == 0) continue;
                    hits.Add(new Hit(Str(it, "title"), url,
                                     snippets.TryGetValue(url, out var s) ? s : ""));
                }
            }
        }
        return hits;
    }

    // ---- SearXNG：直连实例的 JSON 接口，无需 Key ----

    private async Task<SearchResult> SearxngAsync(ProxyConfig cfg, string query, CancellationToken ct)
    {
        var baseUrl = cfg.SearchBaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return Fail("SearXNG 需要填实例地址");

        var url = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        var key = cfg.SearchApiKey.Trim();
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // format=json 需要实例在 settings.yml 里开启 search.formats: [html, json]，
            // 没开时多返回 403。这个提示能省掉大量排查时间。
            var hint = (int)resp.StatusCode is 403 or 404
                ? "（SearXNG 需在 settings.yml 的 search.formats 里加上 json，否则 JSON 接口返回 403）"
                : "";
            return Fail($"SearXNG HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{hint}");
        }

        var (hits, engineProblem) = ParseSearxng(raw, cfg.SearchMaxResults);
        if (engineProblem.Length > 0) return Fail(engineProblem);

        ProxyLog.Write($"搜索 searxng「{query}」→ {hits.Count} 条");
        return new SearchResult(true, hits, "");
    }

    /// <summary>
    /// 解析 SearXNG 的 JSON。返回的第二个值是"引擎全挂"这类问题——
    /// 那种情况下"没结果"会被误当成"这东西不存在"，必须与真的没结果区分开。
    /// </summary>
    internal static (List<Hit> Hits, string Problem) ParseSearxng(string raw, int max)
    {
        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= max) break;
                hits.Add(new Hit(
                    Str(it, "title"),
                    Str(it, "url"),
                    FirstNonEmpty(Str(it, "content"), Str(it, "snippet"))));
            }

            // number_of_results 经常是 0 但 results 非空，所以不拿它当计数。
            if (hits.Count == 0 && doc.RootElement.TryGetProperty("unresponsive_engines", out var ue) &&
                ue.ValueKind == JsonValueKind.Array && ue.GetArrayLength() > 0)
                return (hits, "SearXNG 没有可用引擎（unresponsive_engines 非空），检查实例的代理与引擎配置");
        }
        return (hits, "");
    }

    // ---- Tavily：Key 走 Authorization 头 ----

    private async Task<SearchResult> TavilyAsync(ProxyConfig cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey.Trim();
        if (key.Length == 0) return Fail("Tavily 需要 API Key");

        var body = new JsonObject
        {
            ["query"] = query,
            ["max_results"] = Math.Clamp(cfg.SearchMaxResults, 1, 10),
            ["include_answer"] = false,   // 只要素材，不让上游先替模型下结论
            ["search_depth"] = "basic",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Tavily HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        var hits = ParseTavily(raw, cfg.SearchMaxResults);
        ProxyLog.Write($"搜索 tavily「{query}」→ {hits.Count} 条");
        return new SearchResult(true, hits, "");
    }

    internal static List<Hit> ParseTavily(string raw, int max)
    {
        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= max) break;
                hits.Add(new Hit(Str(it, "title"), Str(it, "url"), Str(it, "content")));
            }
        return hits;
    }

    // ---- Brave：Key 走 X-Subscription-Token 头 ----

    private async Task<SearchResult> BraveAsync(ProxyConfig cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey.Trim();
        if (key.Length == 0) return Fail("Brave Search 需要 API Key");

        var n = Math.Clamp(cfg.SearchMaxResults, 1, 20);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={n}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Brave HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        var hits = ParseBrave(raw, cfg.SearchMaxResults);
        ProxyLog.Write($"搜索 brave「{query}」→ {hits.Count} 条");
        return new SearchResult(true, hits, "");
    }

    internal static List<Hit> ParseBrave(string raw, int max)
    {
        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= max) break;
                hits.Add(new Hit(Str(it, "title"), Str(it, "url"),
                                 FirstNonEmpty(Str(it, "description"), Str(it, "snippet"))));
            }
        }
        return hits;
    }

    // ---- Serper：Key 走 X-API-KEY 头，返回 Google 结果。注意字段名是 link/snippet ----

    private async Task<SearchResult> SerperAsync(ProxyConfig cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey.Trim();
        if (key.Length == 0) return Fail("Serper 需要 API Key");

        var body = new JsonObject
        {
            ["q"] = query,
            ["num"] = Math.Clamp(cfg.SearchMaxResults, 1, 20),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-API-KEY", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Serper HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        var hits = ParseSerper(raw, cfg.SearchMaxResults);
        ProxyLog.Write($"搜索 serper「{query}」→ {hits.Count} 条");
        return new SearchResult(true, hits, "");
    }

    internal static List<Hit> ParseSerper(string raw, int max)
    {
        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("organic", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= max) break;
                hits.Add(new Hit(Str(it, "title"), Str(it, "link"), Str(it, "snippet")));
            }
        return hits;
    }

    // ---- 回灌给模型的内容 ----

    /// <summary>
    /// 把结果序列化成回灌给模型的文本。
    ///
    /// **失败与"没有结果"必须分开说**：前者是"这次查不了"，后者是"网上没这东西"。
    /// 混成一句的话，模型会把一次网络故障当成"这个概念不存在"，然后凭记忆编答案——
    /// 那正是引入搜索要消灭的东西。空结果里也明写了这一点。
    /// </summary>
    public static string ToToolContent(SearchResult r, string query)
    {
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        if (!r.Ok)
            return JsonSerializer.Serialize(new
            {
                query,
                error = r.Error,
                note = "The web search failed. This does NOT mean the thing you asked about " +
                       "does not exist. Either retry with a shorter query or answer without it."
            }, opts);

        if (r.Hits.Count == 0)
            return JsonSerializer.Serialize(new
            {
                query,
                note = "No results. Either the query is too specific or this is not a thing " +
                       "found on the web.",
                results = Array.Empty<object>()
            }, opts);

        var budget = TotalChars;
        var items = new List<object>();
        foreach (var h in r.Hits)
        {
            if (budget <= 0) break;
            var snip = h.Snippet.Length <= SnippetChars ? h.Snippet : h.Snippet[..SnippetChars] + "…";
            if (snip.Length > budget) snip = snip[..Math.Max(0, budget)];
            budget -= snip.Length;
            items.Add(new { title = h.Title, url = h.Url, snippet = snip });
        }

        return JsonSerializer.Serialize(new { query, results = items }, opts);
    }

    private SearchResult Fail(string message)
    {
        LastError = message;
        return new SearchResult(false, new(), message);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Replace("\n", " ").Replace("\r", " ").Trim()
            : "";

    private static string FirstNonEmpty(params string[] xs) =>
        xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>从地址里取主机名（SNI 的默认值）。手写解析，容忍"还没填完"的串。</summary>
    internal static string HostOf(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return "";
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep >= 0) s = s[(sep + 3)..];
        var end = s.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) s = s[..end];
        var colon = s.LastIndexOf(':');
        if (colon >= 0) s = s[..colon];
        return s.Trim();
    }

    public void Dispose()
    {
        _http.Dispose();
        _shimHttp.Dispose();
    }
}
