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
///   POST /v1/chat/completions    主端点：注入工具（流式与非流式都注入）
///   GET  /v1/models              模型表：**用户勾选过的结果**，见 HandleModels
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

    /// <summary>
    /// 搜索客户端。**在服务端持有一个而不是每次请求新建**：它内部有 HttpClient，
    /// 每请求一个的话会不断开新连接、把 TIME_WAIT 攒起来。dispose 归这里。
    /// </summary>
    private readonly SearchClient _search = new();

    /// <summary>
    /// 网页获取器。同样在服务端持有一个：它内部有 HttpClient，而且**持有浏览器引擎** ——
    /// 每次请求新建一个的话，那个 headless Edge（16–18 个进程、420–530 MB）
    /// 会被反复启动又丢弃，"跨请求复用 + 空闲自动关"这套设计就全废了。
    /// </summary>
    private readonly PageReader _reader;

    /// <summary>
    /// MCP 服务端。与注入那条路共用同一批工具实现（<see cref="ToolCatalog"/> +
    /// <see cref="ToolHost"/>），区别只在封套：那边是 OpenAI 的 <c>tool_calls</c>，
    /// 这边是 JSON-RPC 2.0。
    /// </summary>
    private readonly McpEndpoint _mcp;

    private WebApplication? _app;
    private DateTimeOffset _startedAt;

    public ProxyServer(ProxyConfig cfg, TokenStore tokens, VocabService vocab)
    {
        _cfg = cfg;
        _tokens = tokens;
        _vocab = vocab;
        _upstream = new UpstreamClient(cfg);
        _reader = new PageReader();
        // 工厂而不是实例：与 RunToolLoop / RunStreamLoop 同一条寿命纪律
        // （一次请求一个 ToolHost）。
        _mcp = new McpEndpoint(cfg, () => new ToolHost(_vocab, _search, _reader));

        // 把"本机有没有浏览器"这个**机器事实**交给配置对象，因为 web_fetch 要不要下发
        // 由 ProxyConfig.FetchReady 决定，而配置对象是 ToolLoop / StreamLoop 唯一拿得到的东西。
        // 不这么做的话，engine=browser 而下发判据不知道浏览器在哪，
        // 结果是下发一个每次必然失败的工具。
        _cfg.FetchBrowserUsable = _reader.BrowserAvailable;
    }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// 立刻清理浏览器临时目录（界面上的「立即清理」按钮）。
    /// 走服务端持有的那个 <see cref="PageReader"/>，因为它才拿着**正在被复用的**浏览器实例。
    /// </summary>
    public CleanupReport CleanupTempProfiles() => _reader.CleanupTempProfiles();

    /// <summary>对外主机名：仅本机时给回环，否则给所有网卡（与 Kestrel 的监听一致）。</summary>
    private string HostText => _cfg.BindScope == "local" ? "127.0.0.1" : "0.0.0.0";

    /// <summary>实际监听的地址，供界面显示与用户复制到平台配置里。</summary>
    public string BaseUrl => $"http://{HostText}:{_cfg.Port}/v1";

    /// <summary>MCP 端点地址，供界面显示与客户端配置里填。</summary>
    public string McpUrl => $"http://{HostText}:{_cfg.Port}{McpEndpoint.Path}";

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

        // MCP：同一个端口上的 POST /mcp。关掉时**不注册路由**，于是它就是一个
        // 普通的 404 —— 这比"注册了但回 403"更诚实：没有这个端点就是没有。
        if (_cfg.McpEnabled)
        {
            app.MapPost(McpEndpoint.Path, HandleMcp);
            // GET/DELETE 按 spec 回 405 并带上 Allow：说明"这里只有 POST"，
            // 而不是让客户端对着一个 404 去猜自己是不是地址写错了。
            app.MapMethods(McpEndpoint.Path, new[] { "GET", "DELETE", "PUT", "PATCH" },
                           HandleMcpNotAllowed);
        }

        await app.StartAsync(ct).ConfigureAwait(false);

        _app = app;
        _startedAt = DateTimeOffset.Now;
        ProxyLog.Write($"代理已启动：{BaseUrl}（监听 {ScopeText(_cfg.BindScope)}，" +
                       $"工具注入 {(_cfg.ToolsEnabled ? "开" : "关")}，" +
                       $"令牌 {_tokens.EnabledCount} 枚，" +
                       $"MCP {(_cfg.McpEnabled ? McpUrl : "关")}）");
    }

    /// <summary>把内部的 BindScope 值翻成界面上看得懂的说法。</summary>
    private static string ScopeText(string scope) => scope switch
    {
        "lan" => "局域网",
        "wan" => "任意来源",
        _ => "仅本机"
    };

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
        _search.Dispose();
        _reader.Dispose();
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
            reject = "代理尚未签发任何令牌，已拒绝所有请求。请在代理界面签发一枚令牌，" +
                     "再填进 agent 平台的 API Key。";
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
            ["vocabReady"] = _vocab.IsLoaded,
            ["webSearch"] = _cfg.SearchReady,
            // 抓取的两个字段分开报：fetchReady 是"工具会不会下发"，
            // browser 是"自动降级有没有后手"。两者不同：browser 缺失时
            // auto 模式照样下发工具（HTTP 引擎能用），只是读不了前端渲染的页面。
            ["webFetch"] = _cfg.FetchReady,
            ["webFetchBrowser"] = _reader.BrowserAvailable,
            // MCP 是"这个端口上还有一个入口"，属于用户需要知道的事实；
            // 具体有哪些工具不在这里报——那要看搜索/抓取配好没有。
            ["mcp"] = _cfg.McpEnabled
        };
        return ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted);
    }

    /// <summary>根路径给一句人话。用户拿浏览器点一下端口时不该看到空白页。</summary>
    private Task HandleRoot(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";

        var endpoints = new JsonArray { "/health", "/v1/chat/completions", "/v1/models" };
        if (_cfg.McpEnabled) endpoints.Add(McpEndpoint.Path);

        var payload = new JsonObject
        {
            ["service"] = "AnimaAIProxy",
            ["hint"] = "将 agent 平台的 OpenAI Base URL 指向 " + BaseUrl +
                       "，API Key 填代理签发的令牌。MCP 客户端填 " + McpUrl + "。",
            ["endpoints"] = endpoints
        };
        return ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted);
    }

    // ---------------- POST /mcp（MCP 客户端） ----------------

    /// <summary>
    /// MCP 的入口。传输层只做四件事：来源过滤、Origin 校验、鉴权、把 JSON-RPC
    /// 封套交给 <see cref="McpEndpoint"/>。协议细节全部在那边，于是回归可以直接
    /// 调协议层，不必为了验一个字段去起一次 HTTP。
    /// </summary>
    private async Task HandleMcp(HttpContext ctx)
    {
        if (!RemoteAllowed(ctx))
        {
            ProxyLog.Write($"拒绝来自 {ctx.Connection.RemoteIpAddress} 的 MCP 连接" +
                           $"（监听 {ScopeText(_cfg.BindScope)}）");
            await RejectMcpAsync(ctx, StatusCodes.Status403Forbidden,
                                 "来源地址不在允许的范围内。").ConfigureAwait(false);
            return;
        }

        if (!OriginAllowed(ctx))
        {
            // 浏览器发的跨源请求一定带 Origin，MCP 客户端（本地程序）通常不带。
            // 这条挡的是 DNS rebinding 与网页 CSRF：它们能向 127.0.0.1 发请求，
            // 但改不了 Origin。令牌本来就是一道墙，两堵墙分开立着才叫纵深。
            ProxyLog.Write("拒绝 MCP 请求：Origin 不是本机自己（" +
                           ctx.Request.Headers["Origin"].ToString() + "）");
            await RejectMcpAsync(ctx, StatusCodes.Status403Forbidden,
                                 "Origin 不在允许范围内（只接受本机地址）。").ConfigureAwait(false);
            return;
        }

        if (!Authorize(ctx, out _, out var reject))
        {
            ProxyLog.Write("拒绝 MCP 请求：" + reject);
            await RejectMcpAsync(ctx, StatusCodes.Status401Unauthorized, reject).ConfigureAwait(false);
            return;
        }

        var raw = await ReadBodyAsync(ctx).ConfigureAwait(false);
        var result = await _mcp.HandleAsync(Encoding.UTF8.GetString(raw), ctx.RequestAborted)
                                .ConfigureAwait(false);

        ctx.Response.StatusCode = result.Status;
        // 通知的响应**不能有响应体**（spec：202 + 空体）。写了 body 的客户端
        // 会把它当成一个"id 对不上的响应"而报错。
        if (result.Body is null) return;

        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(result.Body), ctx.RequestAborted)
                  .ConfigureAwait(false);
    }

    /// <summary>GET/DELETE /mcp：这里既没有 SSE 流，也没有会话可结束。</summary>
    private Task HandleMcpNotAllowed(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        ctx.Response.Headers.Allow = "POST";
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = -32600,
                ["message"] = "本服务器的 MCP 只有 POST /mcp（无状态、不提供服务端推送）。" +
                              "GET（SSE 流）与 DELETE（结束会话）都不支持。"
            }
        }), ctx.RequestAborted);
    }

    /// <summary>
    /// Origin 校验。**只在请求带了 Origin 时才校验**：MCP 客户端是本地程序，
    /// 普遍不带这个头；浏览器发跨源 POST 则一定带。不带就放行、带了就必须是
    /// 本机自己——既不会误伤客户端，又挡住了网页。
    /// </summary>
    private bool OriginAllowed(HttpContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;

        var o = origin.TrimEnd('/').ToLowerInvariant();
        return o == $"http://127.0.0.1:{_cfg.Port}" ||
               o == $"http://localhost:{_cfg.Port}" ||
               o == $"http://[::1]:{_cfg.Port}";
    }

    /// <summary>
    /// MCP 的拒绝要回 **JSON-RPC 形状**，不是 OpenAI 的 error 形状。
    /// MCP 客户端按 JSON-RPC 解析响应体；给它一个 OpenAI 错误对象，它只会报
    /// "服务器返回了无法解析的响应"，把真正的 401/403 埋掉。
    /// </summary>
    private async Task RejectMcpAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = -32001, ["message"] = message }
        }), ctx.RequestAborted).ConfigureAwait(false);
    }

    // ---------------- POST /v1/chat/completions ----------------

    private async Task HandleChat(HttpContext ctx)
    {
        if (!RemoteAllowed(ctx))
        {
            ProxyLog.Write($"拒绝来自 {ctx.Connection.RemoteIpAddress} 的连接（监听 {ScopeText(_cfg.BindScope)}）");
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
            // 流式**也注入工具**。
            //
            // 早先这里是纯透传，理由是"要在增量 delta 上做工具循环，就得整条读完
            // 再合成假流，首字延迟等于整段回答时长"。那个理由对"整条读完"成立，
            // 但它不是唯一做法：正文增量**实时转发**，工具调用增量按轮缓冲，
            // 轮末才裁定——首字延迟没有任何损失，而工具在流式下也能用上。
            // 见 <see cref="StreamLoop"/>。
            await RunStreamLoop(ctx, body, token).ConfigureAwait(false);
            return;
        }

        await RunToolLoop(ctx, body, token).ConfigureAwait(false);
    }

    private async Task RunToolLoop(HttpContext ctx, JsonObject body, string token)
    {
        // ToolLoop / ToolHost 都是**一次请求一个实例**：ToolLoop 里持有
        // 本请求的轮数与工具调用计数，复用会把"每次请求"变成"全局"。
        var loop = new ToolLoop(_upstream, new ToolHost(_vocab, _search, _reader), _cfg);

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
            ProxyLog.WriteException("工具循环异常", ex);
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

    // ---------------- 流式注入 ----------------

    /// <summary>
    /// 流式请求的 SSE 出口。它把 <see cref="StreamLoop"/> 的决定翻译成 HTTP。
    ///
    /// **头必须在第一个字节之前定下来**，所以"提交"这件事是显式的：
    /// 第一次写之前设置状态码与响应头，之后就只能往流里写。
    /// <see cref="Committed"/> 把这件事告诉循环，让它知道上游失败时
    /// 还能不能如实回一个错误状态码。
    /// </summary>
    private sealed class SseSink : ISseSink
    {
        private readonly HttpContext _ctx;

        public SseSink(HttpContext ctx) => _ctx = ctx;

        public bool Committed { get; private set; }

        public async Task WriteAsync(string sseEvent, CancellationToken ct)
        {
            if (!Committed)
            {
                _ctx.Response.StatusCode = StatusCodes.Status200OK;
                _ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                _ctx.Response.Headers["Cache-Control"] = "no-cache";
                // 让可能存在的中间反向代理不要缓冲（nginx 认这个头）。
                _ctx.Response.Headers["X-Accel-Buffering"] = "no";
                Committed = true;
            }

            var bytes = Encoding.UTF8.GetBytes(sseEvent);
            await _ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);

            // **每个事件都要 flush。** 攒着不 flush 的话 SSE 会停在 Kestrel 的
            // 缓冲里，首字延迟从几十毫秒变成整段回答的时长——而流式的全部
            // 意义就在首字延迟。
            await _ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunStreamLoop(HttpContext ctx, JsonObject body, string token)
    {
        // 与非流式同样的理由：一次请求一个实例，计数才不会跨请求累加。
        var loop = new StreamLoop(_upstream, new ToolHost(_vocab, _search, _reader), _cfg);
        var sink = new SseSink(ctx);

        // **在日志里标明这是流式路径。** 两条路产生的轮次/执行日志措辞一模一样，
        // 不标的话"这次工具有没有生效"在日志上分不出是哪种请求，
        // 而"客户端要的是流式"恰是历史上工具静默失效的那一种情况。
        ProxyLog.Write("流式请求：正文实时转发，工具调用按轮处理");

        StreamLoop.Outcome outcome;
        try
        {
            outcome = await loop.RunAsync(body, token, sink, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：上游那边也会被 ct 取消，这里只需安静收场。
            return;
        }
        catch (Exception ex)
        {
            ProxyLog.WriteException("流式工具循环异常", ex);
            // 还没写出任何字节 → 还能如实回一个 502。
            // 已经写出去了 → 状态行早发出去了，只能往流里补一个 error 事件，
            // 否则客户端会一直等一个永远不来的 [DONE]。
            if (!sink.Committed)
            {
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
            }
            else
            {
                try
                {
                    await sink.WriteAsync(Sse.Data(ChatWire.ToJson(new JsonObject
                    {
                        ["error"] = new JsonObject
                        {
                            ["message"] = "代理内部错误：" + ex.Message,
                            ["type"] = "proxy_error"
                        }
                    })), ctx.RequestAborted).ConfigureAwait(false);
                    await sink.WriteAsync(Sse.Done, ctx.RequestAborted).ConfigureAwait(false);
                }
                catch { /* 客户端可能已经断了 */ }
            }
            return;
        }

        foreach (var line in outcome.Log) ProxyLog.Write("  " + line);

        // 循环说"没写完"，意思是它在吐出任何字节之前就失败了。这时才轮到我们
        // 回一个真正的错误状态码——这正是流式路径上最容易丢掉的信息：
        // 上游 429 / 401 / 模型名写错，若不在这里如实回报，客户端只会看到
        // 一条空流然后超时。
        if (outcome.Completed) return;

        ProxyLog.Write("流式上游失败：" + outcome.Error);
        ctx.Response.StatusCode = outcome.Status > 0 ? outcome.Status : StatusCodes.Status502BadGateway;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = outcome.Error.Length > 0 ? outcome.Error : "上游流式请求失败",
                ["type"] = "upstream_error"
            }
        }), ctx.RequestAborted).ConfigureAwait(false);
    }

    // ---------------- GET /v1/models ----------------

    /// <summary>
    /// 按需拉取模型表的超时。比 <see cref="ProxyConfig.TimeoutSeconds"/> 短得多：
    /// 这是在一次 GET 里顺手做的事，接入方常常在启动时调它，
    /// 不该因为某个站点不通就让平台启动干等半分钟。
    /// </summary>
    private const int AutoFetchTimeoutSeconds = 6;

    /// <summary>本进程里已经尝试过自动拉取的站点。失败的站点不再每次 GET 都重试。</summary>
    private readonly HashSet<string> _autoFetchTried = new(StringComparer.Ordinal);
    private readonly object _autoFetchLock = new();

    /// <summary>
    /// 按需拉取成功、模型表真的变了时触发，供界面重建列表。
    ///
    /// 存在的理由：按需拉取是**接入方调 /v1/models 时触发的**，而界面上的列表是
    /// 上一次重建时的快照。少了这个通知，界面会一直停在"尚未拉取模型 / 还没有模型"，
    /// 用户既看不到、也就无法开关那些**实际已经在对外提供**的模型——而这时候
    /// 平台那边已经在正常使用了。这个错位只在"平台先调了 /v1/models"这种
    /// 真实顺序下才出现，点一次"刷新模型"就会消失，所以格外难被发现。
    /// </summary>
    public static event Action? ModelsChanged;

    private static void RaiseModelsChanged()
    {
        try { ModelsChanged?.Invoke(); } catch { /* 订阅方自己的异常不该影响代理 */ }
    }

    /// <summary>
    /// 对外公布的模型表 = **用户勾选过的结果**，不是上游的原样透传。
    ///
    /// 只有已启用站点的已启用模型会出现在这里。这一点是本功能的核心：
    /// 用户关掉一个模型，它就该从平台的模型选择里消失；关掉一个站点，
    /// 它下面的模型全部消失。
    ///
    /// **按需拉取**：勾选启用、允许自动拉取、且从未拉过的站点，趁这次 GET 拉一次。
    /// 没有这一步的话，"配好站点就能用"会退化成"配好站点还得记得先去点一下刷新"——
    /// 而平台的失败表现是"你配的模型不存在"，与真正的原因毫无关系。
    /// 拉取失败**不影响响应**：把已有的表报出去，用户至少还能看到自己配的东西。
    /// </summary>
    private async Task HandleModels(HttpContext ctx)
    {
        if (!Authorize(ctx, out _, out var reject))
        {
            await RejectAsync(ctx, reject).ConfigureAwait(false);
            return;
        }

        await EnsureModelsFetchedAsync(ctx.RequestAborted).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["object"] = "list",
            ["data"] = ModelCatalog.ToOpenAiModels(_cfg)
        };

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(payload), ctx.RequestAborted).ConfigureAwait(false);
    }

    private async Task EnsureModelsFetchedAsync(CancellationToken ct)
    {
        List<ProviderProfile> pending;
        lock (_autoFetchLock)
        {
            pending = _cfg.Providers
                // 判据是"从未拉过"，所以要同时认 null 与空串两种写法：空串本应被
                // ProviderProfile.Normalize 收敛成 null，这里再容错一次是因为拉取
                // 一旦被跳过就**完全没有痕迹**（不记日志、不发请求），而症状是模型表
                // 永远空着——不值得让这一个判断承担那么高的排查成本。
                .Where(p => p.Enabled && p.AutoFetchModels && string.IsNullOrWhiteSpace(p.ModelsFetchedAt) &&
                            !string.IsNullOrWhiteSpace(p.BaseUrl) &&
                            _autoFetchTried.Add(p.Id))
                .ToList();
        }
        if (pending.Count == 0) return;

        // 并行拉：站点之间互不相关，串行的话"三个站点里有一个不通"会让这次 GET
        // 多等好几倍。失败的站点已记进 _autoFetchTried，不会每次 GET 重来一遍；
        // 重启进程或在界面上点刷新会清掉这个标记。
        var results = await Task.WhenAll(pending.Select(async p =>
        {
            var (ok, _, ids, _, error) = await _upstream
                .FetchModelsAsync(p, ct, AutoFetchTimeoutSeconds).ConfigureAwait(false);
            return (Provider: p, Ok: ok, Ids: ids, Error: error);
        })).ConfigureAwait(false);

        var changed = false;
        foreach (var r in results)
        {
            if (!r.Ok)
            {
                ProxyLog.Write($"自动拉取 {r.Provider.DisplayName} 的模型表失败：{r.Error}");
                continue;
            }
            r.Provider.ApplyFetchedModels(r.Ids);
            changed = true;
            ProxyLog.Write($"{r.Provider.DisplayName}：拉到 {r.Ids.Count} 个模型");
        }

        if (changed)
        {
            _cfg.Save();
            // 只在真的拉到东西时才通知：失败的站点进 _autoFetchTried 后不再重试，
            // 每次 GET 都通知界面重建列表纯属白费。
            RaiseModelsChanged();
        }
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
    public ILogger CreateLogger(string categoryName) => new ProxyLogger();

    public void Dispose() { }

    private sealed class ProxyLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // 只写正文，不带 "[Warning] Kestrel:" 这种前缀：日志面板是给用户看的，
            // 级别与内部类名对他没有意义；真要排查时，异常本身已经被
            // WriteException 拆成"摘要进面板、栈进文件"了。
            var text = formatter(state, exception);
            ProxyLog.Write(text, exception?.ToString());
        }
    }
}
