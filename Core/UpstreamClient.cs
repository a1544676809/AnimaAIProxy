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
/// - 不改请求体（除了调用方明确要求的那几处，见 <see cref="ToolLoop"/>）；
/// - 不改响应体；
/// - 不重试（上游的 429 / 余额不足 / 模型名写错，重试只会再犯一次）；
/// - 不缓存。
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
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            // 不自带超时，由每次请求的 CTS 管（见类注释）。
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>把客户端给我们的令牌换成上游的密钥。绝不把客户端的令牌送上去。</summary>
    private string UpstreamKey => _cfg.UpstreamApiKey;

    private Uri BuildUri(string suffix)
    {
        var baseUrl = (_cfg.UpstreamBaseUrl ?? "").TrimEnd('/');
        return new Uri(baseUrl + suffix, UriKind.Absolute);
    }

    /// <summary>模型名映射：客户端报的名字 → 上游要的名字。没配就原样透传。</summary>
    public string MapModel(string? requested)
    {
        var model = (requested ?? "").Trim();
        if (model.Length == 0) return model;

        if (_cfg.ModelMap.TryGetValue(model, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped.Trim();

        if (_cfg.FallbackModelEnabled && !string.IsNullOrWhiteSpace(_cfg.FallbackModel))
            return _cfg.FallbackModel.Trim();

        return model;
    }

    /// <summary>
    /// 发一次非流式的 chat 请求。<paramref name="body"/> 会被就地改模型名
    /// （调用方传进来的都是自己造的副本），但**其它字段一个不动**。
    /// </summary>
    public async Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct)
    {
        try
        {
            var model = ChatWire.GetString(body, "model");
            var mapped = MapModel(model);
            if (mapped.Length > 0 && !string.Equals(mapped, model, StringComparison.Ordinal))
            {
                body["model"] = mapped;
                ProxyLog.Write($"模型名映射：{model} → {mapped}");
            }

            // 无论客户端要什么，我们都按非流式问上游：工具循环需要在完整的
            // message 对象上做判断。真正需要流式的场合在 ProxyServer 里
            // 走另一条完全独立的透传路径，不经过这里。
            body["stream"] = false;

            using var req = BuildRequest(body, stream: false);
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
            var model = ChatWire.GetString(body, "model");
            var mapped = MapModel(model);
            if (mapped.Length > 0 && !string.Equals(mapped, model, StringComparison.Ordinal))
            {
                body["model"] = mapped;
                ProxyLog.Write($"模型名映射：{model} → {mapped}");
            }

            body["stream"] = true;

            using var req = BuildRequest(body, stream: true);
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

    /// <summary>拉上游的 /models，原样返回。客户端拿它列模型。</summary>
    public async Task<(bool Ok, int Status, string Raw, string Error)> ModelsAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri("/models"));
            Authorize(req);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_cfg.TimeoutSeconds, 5, 30)));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                      .ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, (int)resp.StatusCode, raw,
                    $"HTTP {(int)resp.StatusCode} {resp.StatusCode}：{Truncate(raw, 300)}");

            return (true, (int)resp.StatusCode, raw, "");
        }
        catch (OperationCanceledException)
        {
            return (false, 0, "", "上游 /models 超时");
        }
        catch (Exception ex)
        {
            return (false, 0, "", ex.Message);
        }
    }

    /// <summary>
    /// 通用透传：把客户端发来的任意请求原样转给上游，把响应原样带回来。
    ///
    /// 给 /v1 下的其它端点（embeddings、completions 之类）用。这些端点我们
    /// 不注入工具、也不该理解它们的语义——**理解得越多，兼容性越差**。
    /// </summary>
    public async Task<(bool Ok, int Status, string ContentType, byte[] Body, string Error)> PassthroughAsync(
        HttpMethod method, string path, string query, string? contentType, byte[] body, CancellationToken ct)
    {
        try
        {
            var uri = BuildUri(path) + query;
            using var req = new HttpRequestMessage(method, uri);
            Authorize(req);

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

    private HttpRequestMessage BuildRequest(JsonObject body, bool stream)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BuildUri("/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        Authorize(req);

        if (stream)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        return req;
    }

    /// <summary>
    /// 挂上上游密钥。**只挂上游的**——客户端拿来鉴权的那枚代理令牌
    /// 绝不能出现在发往上游的请求里，否则我们"不让客户端拿到真密钥"这件事
    /// 就白做了（令牌泄露的上游账号虽不同，但等于把代理的鉴权边界
    /// 泄露给了上游）。
    /// </summary>
    private void Authorize(HttpRequestMessage req)
    {
        var key = UpstreamKey;
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
