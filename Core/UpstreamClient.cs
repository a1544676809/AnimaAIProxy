using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 与上游 OpenAI 兼容接口说话的那个客户端。
///
/// 只有两件事：**把请求原样送上去**，以及**把响应原样拿回来**。
/// 这里刻意不做任何"聪明"的事：
///
/// - 不改请求体（除了模型名与调用方明确要求的那几处，见 <see cref="ToolLoop"/>）；
/// - 不改响应体；
/// - 不重试（上游的 429 / 余额不足 / 模型名写错，重试只会再犯一次）；
/// - 不缓存响应。
///
/// **按站点取地址、密钥、请求头**：每个请求先经 <see cref="ModelCatalog.Resolve"/>
/// 定出目标站点（见 <see cref="ProviderProfile"/>），再拿那个站点自己的配置发出去。
/// 这是"一个代理带多个模型提供商"的全部实现方式——客户端只报模型名，
/// 地址与密钥都不经过它。
///
/// 超时用 <see cref="HttpClient.Timeout"/> 之外的做法（<c>CancellationTokenSource</c>）：
/// <see cref="HttpClient"/> 一旦发过请求，Timeout 就不可改，而代理的配置是
/// 运行期可改的。所以这里把 HttpClient 的超时设成无限，由每次请求自己的
/// 令牌控制——代价是每次请求多一个 CTS，收益是改超时不用重启。
/// </summary>
public sealed class UpstreamClient : IUpstream, IDisposable
{
    private readonly HttpClient _http;
    private readonly ProxyConfig _cfg;

    public UpstreamClient(ProxyConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient(new SocketsHttpHandler
        {
            // 上游的连接复用：代理的访问模式是"长时间空闲、来了请求要立刻答"，
            // 不池化的话每次都要重新 TLS 握手，首字延迟白白多几十到几百毫秒。
            // 多个站点共用一个连接池：池是按主机分的，不会串台。
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            // 不自带超时，由每次请求的 CTS 管（见类注释）。
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>没有可用站点时的统一说法。用户看到的下一句应当是"去界面加一个"。</summary>
    public const string NoProviderMessage =
        "没有可用的模型提供商：请在代理界面添加一个站点并勾选启用。";

    /// <summary>
    /// 定出这次请求发往哪个站点。**每一步都先判定再发**：判定失败时连请求都不该建，
    /// 否则症状是"配置为空时收到一个上游的 401"，而真正的原因是本地没配站点。
    /// </summary>
    private Routing Route(JsonObject? body)
    {
        var r = ModelCatalog.Resolve(_cfg, ChatWire.GetString(body, "model"));
        if (r.Notable) ProxyLog.Write(r.Note);
        return r;
    }

    private static Uri BuildUri(ProviderProfile p, string suffix)
    {
        var baseUrl = (p.BaseUrl ?? "").TrimEnd('/');
        return new Uri(baseUrl + suffix, UriKind.Absolute);
    }

    /// <summary>
    /// 发一次非流式的 chat 请求。<paramref name="body"/> 会被就地改模型名
    /// （调用方传进来的都是自己造的副本），但**其它字段一个不动**。
    /// </summary>
    public async Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct)
    {
        try
        {
            var r = Route(body);
            if (r.Provider is null) return UpstreamResult.Transport(NoProviderMessage);

            if (r.UpstreamModel.Length > 0 &&
                !string.Equals(r.UpstreamModel, ChatWire.GetString(body, "model"), StringComparison.Ordinal))
                body["model"] = r.UpstreamModel;

            // 无论客户端要什么，我们都按非流式问上游：工具循环需要在完整的
            // message 对象上做判断。真正需要流式的场合在 ProxyServer 里
            // 走另一条完全独立的注入路径，不经过这里。
            body["stream"] = false;

            using var req = BuildRequest(r.Provider, body, stream: false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSeconds));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                      .ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body2 = ChatWire.TryParseObject(raw);
                return new UpstreamResult(false, (int)resp.StatusCode, raw, body2,
                    $"HTTP {(int)resp.StatusCode} {resp.StatusCode}：{Truncate(raw, 400)}");
            }

            var parsed = ChatWire.TryParseObject(raw);
            if (parsed is null)
                return new UpstreamResult(false, (int)resp.StatusCode, raw, null,
                    "上游返回的不是合法 JSON 对象：" + Truncate(raw, 300));

            return new UpstreamResult(true, (int)resp.StatusCode, raw, parsed, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动断开：不是错误，如实说一句就走，别写一条假的"上游超时"。
            return UpstreamResult.Transport("客户端已取消本次请求");
        }
        catch (OperationCanceledException)
        {
            return UpstreamResult.Transport($"上游请求超时（{_cfg.TimeoutSeconds} 秒）");
        }
        catch (HttpRequestException ex)
        {
            return UpstreamResult.Transport("连不上上游：" + ex.Message);
        }
        catch (Exception ex)
        {
            ProxyLog.WriteException("上游请求异常", ex);
            return UpstreamResult.Transport(ex.Message);
        }
    }

    /// <summary>
    /// 发一次**流式**请求，把上游的响应体流交给 <paramref name="onStream"/>。
    ///
    /// 与旧实现的区别：这里**不解析、不注入、不改任何字节**，只负责把流交出去。
    /// 工具注入的判断归 <see cref="StreamLoop"/>——传输层一旦开始理解 SSE 语义，
    /// 就会出现"同一个决定在两个地方各写一遍"的分叉。
    ///
    /// 用 <c>ResponseHeadersRead</c>：不复用 <c>ResponseContentRead</c>——
    /// 那会等整个流读完才返回，流式就彻底失去意义（首字延迟等于整段时长）。
    /// </summary>
    public async Task<StreamStart> StreamAsync(JsonObject body, string clientToken,
                                               Func<Stream, Task> onStream, CancellationToken ct)
    {
        try
        {
            var r = Route(body);
            if (r.Provider is null) return StreamStart.Transport(NoProviderMessage);

            if (r.UpstreamModel.Length > 0 &&
                !string.Equals(r.UpstreamModel, ChatWire.GetString(body, "model"), StringComparison.Ordinal))
                body["model"] = r.UpstreamModel;

            body["stream"] = true;

            using var req = BuildRequest(r.Provider, body, stream: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSeconds));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                      .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return new StreamStart(false, (int)resp.StatusCode, "application/json",
                    $"HTTP {(int)resp.StatusCode}：{Truncate(raw, 400)}");
            }

            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "text/event-stream";

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await onStream(stream).ConfigureAwait(false);

            return new StreamStart(true, (int)resp.StatusCode, contentType, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动断开：不是错误，如实说一句就走。
            return StreamStart.Transport("客户端已取消本次请求");
        }
        catch (OperationCanceledException)
        {
            return StreamStart.Transport($"上游流式请求超时（{_cfg.TimeoutSeconds} 秒）");
        }
        catch (HttpRequestException ex)
        {
            return StreamStart.Transport("连不上上游：" + ex.Message);
        }
        catch (Exception ex)
        {
            ProxyLog.WriteException("上游流式请求异常", ex);
            return StreamStart.Transport(ex.Message);
        }
    }

    /// <summary>
    /// 拉某个站点的 /models。
    ///
    /// 只给界面上的"测试连接 / 刷新模型"与 <see cref="ProxyServer"/> 的按需拉取用——
    /// 对外公布的 <c>/v1/models</c> 读的是本地模型表
    /// （<see cref="ModelCatalog.ToOpenAiModels"/>），因为那张表才是用户勾选过的结果。
    ///
    /// <paramref name="timeoutSeconds"/> 为 0 时用 <c>clamp(TimeoutSeconds, 5, 30)</c>。
    /// 按需拉取会传一个更短的值：那是在一个 GET 里顺手做的事，不该让接入方
    /// 在启动时干等半分钟。
    /// </summary>
    public async Task<(bool Ok, int Status, List<string> Ids, string Raw, string Error)> FetchModelsAsync(
        ProviderProfile provider, CancellationToken ct, int timeoutSeconds = 0)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(provider, "/models"));
            Authorize(req, provider);

            var seconds = timeoutSeconds > 0
                ? Math.Clamp(timeoutSeconds, 2, 120)
                : Math.Clamp(_cfg.TimeoutSeconds, 5, 30);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                      .ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, (int)resp.StatusCode, new List<string>(), raw,
                    $"HTTP {(int)resp.StatusCode} {resp.StatusCode}：{Truncate(raw, 300)}");

            return (true, (int)resp.StatusCode, ModelCatalog.ParseModelIds(raw), raw, "");
        }
        catch (OperationCanceledException)
        {
            return (false, 0, new List<string>(), "", "请求 /models 超时");
        }
        catch (Exception ex)
        {
            return (false, 0, new List<string>(), "", ex.Message);
        }
    }

    /// <summary>
    /// 通用透传：把客户端发来的任意请求原样转给上游，把响应原样带回来。
    ///
    /// 给 /v1 下的其它端点（embeddings、completions 之类）用。这些端点我们
    /// 不注入工具、也不该理解它们的语义——**理解得越多，兼容性越差**。
    ///
    /// 站点选择：从请求体里读 <c>model</c> 按模型表路由（embeddings 也带模型名），
    /// 读不到就用默认站点。**只用来选站点，不改请求体一个字节**——透传就该是透传。
    /// </summary>
    public async Task<(bool Ok, int Status, string ContentType, byte[] Body, string Error)> PassthroughAsync(
        HttpMethod method, string path, string query, string? contentType, byte[] body, CancellationToken ct)
    {
        try
        {
            var provider = _cfg.DefaultProvider;
            if (body.Length > 0 && LooksJson(contentType))
            {
                var parsed = ChatWire.TryParseObject(Encoding.UTF8.GetString(body));
                if (parsed is not null)
                {
                    var r = ModelCatalog.Resolve(_cfg, ChatWire.GetString(parsed, "model"));
                    if (r.Provider is not null) provider = r.Provider;
                }
            }
            if (provider is null) return (false, 0, "application/json", Array.Empty<byte>(), NoProviderMessage);

            var uri = BuildUri(provider, path) + query;
            using var req = new HttpRequestMessage(method, uri);
            Authorize(req, provider);

            if (body.Length > 0)
            {
                req.Content = new ByteArrayContent(body);
                if (!string.IsNullOrWhiteSpace(contentType) &&
                    MediaTypeHeaderValue.TryParse(contentType, out var mt))
                    req.Content.Headers.ContentType = mt;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSeconds));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                      .ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var respType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";

            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, respType, bytes,
                resp.IsSuccessStatusCode ? "" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            return (false, 0, "application/json", Array.Empty<byte>(), "上游请求超时");
        }
        catch (Exception ex)
        {
            return (false, 0, "application/json", Array.Empty<byte>(), ex.Message);
        }
    }

    private static bool LooksJson(string? contentType) =>
        contentType is null || contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage BuildRequest(ProviderProfile provider, JsonObject body, bool stream)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(provider, "/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        Authorize(req, provider);

        if (stream)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        return req;
    }

    /// <summary>
    /// 给请求装上认证与站点自定义头。三处调用点（chat / 流式 / models）共用它，
    /// 免得各自记得"还要加自定义头"——漏一处就是那个功能在某条路径上静默失效，
    /// 而症状只是"某个平台报 401"，很难追到是漏加头。
    ///
    /// 顺序有讲究：先设 <c>Authorization</c>，再应用自定义头。
    /// <see cref="CustomHeaders.Parse"/> 已经拒绝了手填 Authorization，
    /// 所以这里不存在互相覆盖的问题；万一将来放开，也应当是用户显式填的赢。
    ///
    /// **只挂目标站点的密钥**——客户端拿来鉴权的那枚代理令牌绝不能出现在发往上游的
    /// 请求里，否则我们"不让客户端拿到真密钥"这件事就白做了。
    /// </summary>
    private static void Authorize(HttpRequestMessage req, ProviderProfile provider)
    {
        var key = provider.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        var custom = CustomHeaders.Parse(provider.CustomHeaders);
        if (custom.Entries.Count > 0) CustomHeaders.Apply(req, custom);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
