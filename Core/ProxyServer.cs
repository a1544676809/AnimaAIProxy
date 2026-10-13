using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnimaAIProxy.Core;

/// <summary>
/// 对外的 HTTP 端点。agent 平台把它当成一个普通的 OpenAI 端点接进来。
///
/// 端点清单（与 OpenAI 的形状对齐，多余的一个都没有）：
///
///   GET  /health                 存活探测（**不鉴权**，见下）
///   POST /v1/chat/completions    主端点：非流式注入工具，流式纯透传
///   GET  /v1/models              上游的模型表（+ 配置里映射出来的名字）
///   *    /v1/**                  其它端点原样透传（embeddings、completions……）
///
/// **三条贯穿全文的原则：**
///
/// 1. **代理只做搬运，不替接入方做决定。** 模型名、温度、max_tokens、
///    reasoning_effort、chat_template_kwargs……全部原样透传。代理能改的只有
///    "加我们自己的工具声明"和"把我们的工具调用摘掉"这两件事。
/// 2. **认证边界是代理令牌，不是上游密钥。** 客户端永远拿不到上游密钥；
///    上游密钥也永远不会出现在我们发给上游之外的任何地方。默认"一枚令牌都
///    没有"时**拒绝一切**——回环端口上任何程序（包括浏览器里的网页）都能发
///    请求，不设防等于把额度送人。
/// 3. **出错时如实回报。** 不重试、不静默降级、不把上游的错误包装成
///    "模型答得怪"。上游返回什么状态码，客户端就拿到什么状态码。
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private readonly ProxyConfig _cfg;
    private readonly TokenStore _tokens;
    private readonly VocabService _vocab;
    private readonly UpstreamClient _upstream;

    private WebApplication? _app;
    private DateTimeOffset _startedAt;

    public ProxyServer(ProxyConfig cfg, TokenStore tokens, VocabService vocab)
    {
        _cfg = cfg;
        _tokens = tokens;
        _vocab = vocab;
        _upstream = new UpstreamClient(cfg);
    }

    public bool IsRunning => _app is not null;

    /// <summary>实际监听的地址，供界面显示与用户复制到平台配置里。</summary>
    public string BaseUrl => $"http://{(_cfg.BindScope == "local" ? "127.0.0.1" : "0.0.0.0")}:{_cfg.Port}/v1";

    public string StartedAtText => _startedAt == default
        ? ""
        : _startedAt.ToString("yyyy-MM-dd HH:mm:ss");

    // ---------------- 生命周期 ----------------

    public async Task StartAsync(CancellationToken ct)
    {
        if (_app is not null) return;

        // CreateSlimBuilder：不带 appsettings.json、不带 DI 扫描、不带
        // 开发环境的那些东西。我们是在别人的进程里嵌一个 HTTP 服务，
        // 任何"读一个不存在的配置文件"的默认行为都是纯粹的意外来源。
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        // Kestrel 自己的日志接到我们的日志里，不然出问题只有一句
        // "connection reset"，而没有任何上下文。
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ProxyLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(_cfg.ListenAddress, _cfg.Port);

            // 带图请求的 base64 能轻松超过默认的 30 MB。agent 平台常常
            // 一次塞好几张图，卡在这里的表现是"上游说请求格式不对"，
            // 而实际上是我们在门口就把包扔了。
            o.Limits.MaxRequestBodySize = 128L * 1024 * 1024;

            // 保持连接：agent 平台的请求是突发式的，握手成本占比不小。
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        var app = builder.Build();

        app.MapGet("/health", HandleHealth);
        app.MapGet("/", HandleRoot);
        app.MapPost("/v1/chat/completions", HandleChat);
        app.MapGet("/v1/models", HandleModels);
        // 其余 /v1/** 原样透传。**刻意不逐个列端点**：OpenAI 兼容服务的
        // 端点集合一直在变，逐个列等于把自己焊死在一个快照上；不认识的
        // 端点交给上游去回答"404 还是 400"才是对的。
        app.Map("/v1/{**rest}", HandlePassthrough);

        await app.StartAsync(ct).ConfigureAwait(false);

        _app = app;
        _startedAt = DateTimeOffset.Now;
        ProxyLog.Write($"代理已启动：{BaseUrl}（监听范围 {_cfg.BindScope}，" +
                       $"工具注入 {(_cfg.ToolsEnabled ? "开" : "关")}，" +
                       $"令牌 {_tokens.EnabledCount} 枚）");
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;

        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyLog.Write("停止代理时异常：" + ex.Message);
        }

        await app.DisposeAsync().ConfigureAwait(false);
        ProxyLog.Write("代理已停止");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _upstream.Dispose();
    }

    // ---------------- 鉴权 ----------------

    /// <summary>
    /// 校验请求携带的代理令牌。通过返回 true 并给出令牌明文（只用于日志）。
    ///
    /// 拒绝时**已经把响应写完了**，调用方直接 return 即可。
    /// </summary>
    private bool Authorize(HttpContext ctx, out string token, out string reject)
    {
        token = "";
        reject = "";

        ctx.Request.Headers.TryGetValue("Authorization", out var auth);
        ctx.Request.Headers.TryGetValue("x-api-key", out var xKey);
        var presented = TokenStore.ExtractToken(auth.ToString(), xKey.ToString());

        if (!_cfg.RequireToken)
        {
            token = presented ?? "";
            return true;
        }

        // "根本没配令牌"和"带了错令牌"要分开说。合成一句的话，用户第一次
        // 装好代理、平台报"令牌无效"，会去反复检查平台配置——而真正该做的
        // 是先在代理里生成一枚令牌。
        if (!_tokens.HasEnabled)
        {
            reject = "代理还没有生成任何令牌，已拒绝所有请求。请在代理界面里生成一枚令牌，" +
                     "再把它填进 agent 平台的 API Key 里。";
            return false;
        }

        var hit = _tokens.Authenticate(presented);
        if (hit is null)
        {
            reject = "令牌无效或已停用。";
            return false;
        }

        token = presented!;
        return true;
    }

    private async Task RejectAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var payload = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "invalid_request_error",
                ["code"] = "invalid_api_key"
            }
        };
        await ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 来源过滤。只有 lan/wan 才需要它（local 时 Kestrel 只绑回环，
    /// 外面根本连不上）。
    ///
    /// lan 只放行私有网段：`BindScope=lan` 的语义是"给同一个局域网里的
    /// 设备用"，而不是"给任何能路由到本机的人用"。把公网地址也放进来，
    /// 用户以为只是开了内网共享，实际把上游额度暴露到了互联网上——
    /// 而这个错误不会有任何症状，直到账单出来。
    /// </summary>
    private bool RemoteAllowed(HttpContext ctx)
    {
        if (_cfg.BindScope != "lan") return true;

        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16/12
            if (b[0] == 192 && b[1] == 168) return true;              // 192.168/16
            if (b[0] == 169 && b[1] == 254) return true;              // 169.254/16 链路本地
            return false;
        }

        // IPv6：fc00::/7 唯一本地、fe80::/10 链路本地
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 16)
        {
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }
        return false;
    }

    // ---------------- GET /health ----------------

    /// <summary>
    /// 存活探测。**刻意不鉴权**，因为监控/编排系统通常没有令牌，
    /// 而一个健康端点要令牌就等于没有健康端点。
    ///
    /// 代价是它会告诉局域网里的扫描者"这里有个 AnimaAIProxy"。所以返回体
    /// 里**只有**存活与词库是否就绪这类非敏感信息：不含上游地址、不含令牌
    /// 数量、不含任何路径。要判断"谁在用"，看日志，不看这里。
    /// </summary>
    private Task HandleHealth(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["service"] = "AnimaAIProxy",
            ["uptimeSeconds"] = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            ["tools"] = _cfg.ToolsEnabled,
            ["vocabReady"] = _vocab.IsLoaded
        };
        return ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted);
    }

    /// <summary>根路径给一句人话。用户拿浏览器点一下端口时不该看到空白页。</summary>
    private Task HandleRoot(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var payload = new JsonObject
        {
            ["service"] = "AnimaAIProxy",
            ["hint"] = "把 agent 平台的 OpenAI Base URL 指到 " + BaseUrl +
                       "，API Key 填代理签发的令牌（aip- 开头）。",
            ["endpoints"] = new JsonArray { "/health", "/v1/chat/completions", "/v1/models" }
        };
        return ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted);
    }

    // ---------------- POST /v1/chat/completions ----------------

    private async Task HandleChat(HttpContext ctx)
    {
        if (!RemoteAllowed(ctx))
        {
            ProxyLog.Write($"拒绝来自 {ctx.Connection.RemoteIpAddress} 的连接（BindScope={_cfg.BindScope}）");
            await RejectAsync(ctx, "来源地址不在允许的范围内。").ConfigureAwait(false);
            return;
        }

        if (!Authorize(ctx, out var token, out var reject))
        {
            ProxyLog.Write("拒绝请求：" + reject);
            await RejectAsync(ctx, reject).ConfigureAwait(false);
            return;
        }

        var raw = await ReadBodyAsync(ctx).ConfigureAwait(false);
        var body = ChatWire.TryParseObject(Encoding.UTF8.GetString(raw));
        if (body is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = "请求体不是合法的 JSON 对象。",
                    ["type"] = "invalid_request_error"
                }
            }), ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (_cfg.LogBodies)
            ProxyLog.Write("请求体：" + Encoding.UTF8.GetString(raw));

        // 工具注入关掉时，代理退化成纯粹的反向代理：一个字节都不改。
        if (!_cfg.ToolsEnabled)
        {
            await PassthroughBytes(ctx, raw, "application/json").ConfigureAwait(false);
            return;
        }

        if (ChatWire.WantsStream(body))
        {
            // 流式这一阶段**不做注入**，理由写在 ToolLoop 的类注释里
            // （要在增量 delta 上做工具循环，就得先整条读完再合成假流，
            // 首字延迟等于整段回答时长）。但**必须在日志里说清楚**，
            // 否则用户会以为"代理挂了工具"而实际上这次一个都没挂。
            ProxyLog.Write("客户端要的是流式：本次不注入工具，按纯透传处理");
            await StreamPassthrough(ctx, body, token).ConfigureAwait(false);
            return;
        }

        await RunToolLoop(ctx, body, token).ConfigureAwait(false);
    }

    private async Task RunToolLoop(HttpContext ctx, JsonObject body, string token)
    {
        // ToolLoop / ToolHost 都是**一次请求一个实例**：ToolLoop 里持有
        // 本请求的轮数与工具调用计数，复用会把"每次请求"变成"全局"。
        var loop = new ToolLoop(_upstream, new ToolHost(_vocab), _cfg);

        ToolLoop.Outcome outcome;
        try
        {
            outcome = await loop.RunAsync(body, token, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：上游那边也会被 ct 取消，这里只需安静收场。
            return;
        }
        catch (Exception ex)
        {
            ProxyLog.Write("工具循环异常：" + ex);
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = "代理内部错误：" + ex.Message,
                    ["type"] = "proxy_error"
                }
            }), ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        foreach (var line in outcome.Log) ProxyLog.Write("  " + line);

        var json = ChatWire.ToJson(outcome.Response);
        if (_cfg.LogBodies) ProxyLog.Write("响应体：" + json);

        // **状态码照抄上游**。上游 429 / 401 / 400 时如果回 200 + 一个 error 体，
        // 接入方会认为这次成功了，然后去读 choices，读不到就报一个和真正原因
        // 毫无关系的错。这一条是被"上游连不上"那条离线断言逼出来的。
        ctx.Response.StatusCode = outcome.Status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json, ctx.RequestAborted).ConfigureAwait(false);
    }

    // ---------------- 流式透传 ----------------

    private async Task StreamPassthrough(HttpContext ctx, JsonObject body, string token)
    {
        var wroteAnything = false;

        var result = await _upstream.StreamAsync(body, token, async upstreamStream =>
        {
            // 头必须**在第一个字节之前**定下来，所以放在回调里而不是调用之后。
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            // 让可能存在的中间反向代理不要缓冲（nginx 认这个头）。
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            wroteAnything = true;

            // 手工读写 + 每块 flush，**不用 CopyToAsync**：那个不保证每次读
            // 都 flush，SSE 会被攒在 Kestrel 的缓冲里，首字延迟从几十毫秒
            // 变成整段回答的时长——而流式的全部意义就在首字延迟。
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int n;
                while ((n = await upstreamStream.ReadAsync(buffer, ctx.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted)
                             .ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, ctx.RequestAborted).ConfigureAwait(false);

        if (result.Ok || wroteAnything) return;

        // 还没写出任何字节 → 可以如实回一个错误状态码。
        // 已经写出去了就只能断开，因为 HTTP 状态行早就发出去了。
        ProxyLog.Write("流式上游失败：" + result.Error);
        ctx.Response.StatusCode = result.Status > 0 ? result.Status : StatusCodes.Status502BadGateway;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = result.Error.Length > 0 ? result.Error : "上游流式请求失败",
                ["type"] = "upstream_error"
            }
        }), ctx.RequestAborted).ConfigureAwait(false);
    }

    // ---------------- GET /v1/models ----------------

    private async Task HandleModels(HttpContext ctx)
    {
        if (!Authorize(ctx, out _, out var reject))
        {
            await RejectAsync(ctx, reject).ConfigureAwait(false);
            return;
        }

        var (ok, status, rawText, error) = await _upstream.ModelsAsync(ctx.RequestAborted)
                                                        .ConfigureAwait(false);

        if (!ok)
        {
            // 上游挂了也要把**配置里映射出来的名字**报出去：平台可能在启动时
            // 先拉一次模型表来决定用哪个模型，上游临时不通不该让整个平台起不来。
            var fallback = BuildModelsFromMap(null);
            if (fallback.Count > 0)
            {
                ProxyLog.Write($"上游 /models 失败（{error}），回退到配置里的模型映射表");
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject { ["data"] = fallback }),
                                              ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = status > 0 ? status : StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = error.Length > 0 ? error : "上游 /models 失败",
                    ["type"] = "upstream_error"
                }
            }), ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 上游成功：原样透传，但**把配置里映射出来的名字补进去**。
        // 理由：映射表的键是代理真正接受的名字（客户端报 gpt-4o，我们换成上游的
        // 模型名）。平台在启动时按 /models 校验"我配的模型名存在吗"，不补进去
        // 它就会判定配置非法——而那个名字我们其实完全支持。
        var merged = MergeModelMap(rawText);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(merged, ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>上游 /models 的原样 JSON + 配置里映射出来的名字（去重）。</summary>
    private string MergeModelMap(string upstreamJson)
    {
        var node = ChatWire.TryParseObject(upstreamJson);
        if (node is null) return upstreamJson;

        if (node["data"] is not JsonArray data)
        {
            // 少数实现把模型表放在别的键下。**不猜形状**，原样透传，
            // 宁可少补几个名字，也不要把别人的响应改坏。
            return upstreamJson;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data)
            if (item is JsonObject o)
            {
                var id = ChatWire.GetString(o, "id");
                if (id.Length > 0) existing.Add(id);
            }

        var added = BuildModelsFromMap(existing);
        foreach (var m in added) data.Add(m);

        return ChatWire.ToJson(node);
    }

    /// <summary>把 <see cref="ProxyConfig.ModelMap"/> 的键做成 OpenAI 形状的模型条目。</summary>
    private JsonArray BuildModelsFromMap(HashSet<string>? exclude)
    {
        var arr = new JsonArray();
        foreach (var key in _cfg.ModelMap.Keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (exclude is not null && exclude.Contains(key)) continue;
            arr.Add(new JsonObject
            {
                ["id"] = key,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "animaiproxy"
            });
        }
        return arr;
    }

    // ---------------- /v1/** 通用透传 ----------------

    private async Task HandlePassthrough(HttpContext ctx)
    {
        if (!RemoteAllowed(ctx))
        {
            await RejectAsync(ctx, "来源地址不在允许的范围内。").ConfigureAwait(false);
            return;
        }

        if (!Authorize(ctx, out _, out var reject))
        {
            await RejectAsync(ctx, reject).ConfigureAwait(false);
            return;
        }

        var raw = await ReadBodyAsync(ctx).ConfigureAwait(false);
        await PassthroughBytes(ctx, raw, ctx.Request.ContentType ?? "application/json")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把一段请求体原样转给上游，把响应原样带回来。
    ///
    /// 注意这里用的是 <c>ctx.Request.Path</c> 里 <c>/v1</c> 之后的那一段：
    /// 上游基地址已经自带 <c>/v1</c>（与 AnimaCaptioner 的约定一致，
    /// 见 <see cref="ProxyConfig.UpstreamBaseUrl"/>），再拼一次会变成
    /// <c>/v1/v1/...</c>。
    /// </summary>
    private async Task PassthroughBytes(HttpContext ctx, byte[] raw, string contentType)
    {
        var path = ctx.Request.Path.Value ?? "";
        var suffix = path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path[3..] : path;
        if (suffix.Length == 0) suffix = "/";

        var (ok, status, respType, bytes, error) = await _upstream.PassthroughAsync(
            new HttpMethod(ctx.Request.Method), suffix, ctx.Request.QueryString.Value ?? "",
            contentType, raw, ctx.RequestAborted).ConfigureAwait(false);

        if (!ok && bytes.Length == 0)
        {
            ProxyLog.Write($"透传 {path} 失败：{error}");
            ctx.Response.StatusCode = status > 0 ? status : StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = error.Length > 0 ? error : "上游请求失败",
                    ["type"] = "upstream_error"
                }
            }), ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 状态码照抄上游：401 / 429 / 400 对客户端都是有意义的信息，
        // 一律改成 200 会让平台的错误处理逻辑全部失灵。
        ctx.Response.StatusCode = status > 0 ? status : StatusCodes.Status200OK;
        ctx.Response.ContentType = respType;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 读完整请求体。
    ///
    /// 不用 <c>ctx.Request.ReadFromJsonAsync</c>：那会把 JSON 解析失败直接
    /// 变成异常，而我们想自己控制"这不是合法 JSON"这条错误长什么样——
    /// 平台那边看到的错误信息是我们给的，措辞直接决定用户能不能自己修好。
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);
        return ms.ToArray();
    }
}

/// <summary>
/// 把 Kestrel 自己的日志接到 <see cref="ProxyLog"/>。
///
/// 只接 Warning 及以上：Kestrel 在 Debug/Information 级别会把每个连接、
/// 每次读都写一行，代理的日志文件会被这些噪声淹没，而真正要看的
/// "上游拒绝了我们的请求"就找不到了。
/// </summary>
internal sealed class ProxyLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ProxyLogger(categoryName);

    public void Dispose() { }

    private sealed class ProxyLogger : ILogger
    {
        private readonly string _category;

        public ProxyLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortName = _category.Contains('.') ? _category[(_category.LastIndexOf('.') + 1)..] : _category;
            var text = formatter(state, exception);
            if (exception is not null) text += " | " + exception.Message;
            ProxyLog.Write($"[{logLevel}] {shortName}: {text}");
        }
    }
}
