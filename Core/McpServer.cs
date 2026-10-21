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
/// MCP 服务：**一个独立的 HTTP 服务**，不是转发 API 端口上的一个路径。
///
/// 分离的理由（也是这次重构的核心）：
///   * 两者面向的对象不同——转发 API 给 agent 平台，MCP 给支持 MCP 的客户端；
///   * 表达成"同一端口上的另一个路径"之后，"只想开其中一个"会变成一连串例外
///     （关掉 /v1 但留着 /mcp，或者反过来），而每个例外都要在鉴权、来源过滤、
///     启动日志里各写一遍；
///   * 独立之后各有一份端口、绑定范围、令牌要求与启停，**互不影响**：
///     只做 MCP 的人可以把转发 API 关掉，这个程序就完全不碰上游。
///
/// 与转发服务共用的只有三样东西：令牌库、词库、以及
/// <see cref="HttpSecurity"/> 里那三道门（来源过滤 / Origin / 令牌）。
/// 文本编码器、搜索后端、浏览器引擎这类"重"资源由窗口创建后**注入**，
/// 两个服务同时开时共用一份——否则会各起一个 headless Edge。
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    public const string Path = "/mcp";

    private readonly ProxyConfig _cfg;
    private readonly TokenStore _tokens;
    private readonly VocabService _vocab;
    private readonly SearchClient _search;
    private readonly PageReader _reader;
    private readonly bool _ownsSearch;
    private readonly bool _ownsReader;
    private readonly McpEndpoint _mcp;

    private WebApplication? _app;
    private DateTimeOffset _startedAt;

    public McpServer(ProxyConfig cfg, TokenStore tokens, VocabService vocab,
                     SearchClient? search = null, PageReader? reader = null)
    {
        _cfg = cfg;
        _tokens = tokens;
        _vocab = vocab;
        _ownsSearch = search is null;
        _ownsReader = reader is null;
        _search = search ?? new SearchClient();
        _reader = reader ?? new PageReader();

        // web_fetch 要不要下发由 ProxyConfig.FetchReady 决定，而那个判据要知道
        // "本机有没有浏览器"这个机器事实。
        _cfg.FetchBrowserUsable = _reader.BrowserAvailable;

        _mcp = new McpEndpoint(cfg, () => new ToolHost(_vocab, _search, _reader));
    }

    public bool IsRunning => _app is not null;

    /// <summary>监听地址（按绑定范围给出主机名），供界面显示。</summary>
    public string Url => $"http://{(_cfg.McpBindScope == "local" ? "127.0.0.1" : "0.0.0.0")}:" +
                         $"{_cfg.McpPort}{Path}";

    /// <summary>
    /// 给客户端配置用的地址。绑定范围是 lan/wan 时 <see cref="Url"/> 会变成
    /// <c>0.0.0.0</c>，那个地址粘进客户端是连不上的——所以本机客户端一律给回环。
    /// </summary>
    public string ClientUrl => $"http://127.0.0.1:{_cfg.McpPort}{Path}";

    public string StartedAtText => _startedAt == default
        ? ""
        : _startedAt.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>当前真正对外提供的工具名（按就绪判据与逐工具开关过滤后）。</summary>
    public IReadOnlyList<string> ToolNames =>
        McpEndpoint.PlanFor(_cfg).Tools.Select(t => t.Name).ToList();

    public async Task StartAsync(CancellationToken ct)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ProxyLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(_cfg.McpListenAddress, _cfg.McpPort);
            // MCP 的报文都很小，但 tools/call 的参数里可能带整段中文描述。
            o.Limits.MaxRequestBodySize = 8L * 1024 * 1024;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        var app = builder.Build();

        app.MapPost(Path, HandleMcp);
        // GET/DELETE 按 spec 回 405 并带上 Allow：说明"这里只有 POST"，
        // 而不是让客户端对着 404 去猜自己是不是地址写错了。
        app.MapMethods(Path, new[] { "GET", "DELETE", "PUT", "PATCH" }, HandleMethodNotAllowed);
        app.MapGet("/health", HandleHealth);
        app.MapGet("/", HandleRoot);

        await app.StartAsync(ct).ConfigureAwait(false);

        _app = app;
        _startedAt = DateTimeOffset.Now;

        var tools = ToolNames;
        ProxyLog.Write($"MCP 已启动：{Url}（监听 {ScopeText(_cfg.McpBindScope)}，" +
                       $"令牌{(_cfg.McpRequireToken ? "必需" : "不校验")}，" +
                       $"工具 {tools.Count} 个：{(tools.Count == 0 ? "无" : string.Join("、", tools))}）");
    }

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
            ProxyLog.Write("停止 MCP 时异常：" + ex.Message);
        }

        await app.DisposeAsync().ConfigureAwait(false);
        ProxyLog.Write("MCP 已停止");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsSearch) _search.Dispose();
        if (_ownsReader) _reader.Dispose();
    }

    // ---------------- POST /mcp ----------------

    private async Task HandleMcp(HttpContext ctx)
    {
        if (!HttpSecurity.RemoteAllowed(_cfg.McpBindScope, ctx))
        {
            ProxyLog.Write($"拒绝来自 {ctx.Connection.RemoteIpAddress} 的 MCP 连接" +
                           $"（监听 {ScopeText(_cfg.McpBindScope)}）");
            await RejectAsync(ctx, StatusCodes.Status403Forbidden,
                              "来源地址不在允许的范围内。").ConfigureAwait(false);
            return;
        }

        if (!HttpSecurity.OriginAllowed(_cfg.McpPort, ctx))
        {
            ProxyLog.Write("拒绝 MCP 请求：Origin 不是本机自己（" +
                           ctx.Request.Headers["Origin"].ToString() + "）");
            await RejectAsync(ctx, StatusCodes.Status403Forbidden,
                              "Origin 不在允许范围内（只接受本机地址）。").ConfigureAwait(false);
            return;
        }

        if (!HttpSecurity.Authorize(_cfg.McpRequireToken, _tokens, ctx, out _, out var reject))
        {
            ProxyLog.Write("拒绝 MCP 请求：" + reject);
            await RejectAsync(ctx, StatusCodes.Status401Unauthorized, reject).ConfigureAwait(false);
            return;
        }

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted).ConfigureAwait(false);

        var result = await _mcp.HandleAsync(Encoding.UTF8.GetString(ms.ToArray()),
                                            ctx.RequestAborted).ConfigureAwait(false);

        ctx.Response.StatusCode = result.Status;
        // 通知的响应**不能有响应体**（spec：202 + 空体）。写了 body 的客户端会把它
        // 当成一个"id 对不上的响应"而报错。
        if (result.Body is null) return;

        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(ChatWire.ToJson(result.Body), ctx.RequestAborted)
                  .ConfigureAwait(false);
    }

    private Task HandleMethodNotAllowed(HttpContext ctx)
    {
        ctx.Response.Headers.Allow = "POST";
        return HttpSecurity.WriteJsonAsync(ctx, StatusCodes.Status405MethodNotAllowed, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = -32600,
                ["message"] = "本服务器的 MCP 只有 POST /mcp（无状态、不提供服务端推送）。" +
                              "GET（SSE 流）与 DELETE（结束会话）都不支持。"
            }
        });
    }

    /// <summary>
    /// MCP 的拒绝要回 **JSON-RPC 形状**，不是 OpenAI 的 error 形状。
    /// MCP 客户端按 JSON-RPC 解析响应体；给它一个 OpenAI 错误对象，它只会报
    /// "服务器返回了无法解析的响应"，把真正的 401/403 埋掉。
    /// </summary>
    private static Task RejectAsync(HttpContext ctx, int status, string message)
        => HttpSecurity.WriteJsonAsync(ctx, status, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject { ["code"] = -32001, ["message"] = message }
        });

    // ---------------- GET /health、GET / ----------------

    /// <summary>
    /// 存活探测。**不鉴权**（与转发服务同一条理由：监控没有令牌），返回体里只有
    /// 存活、就绪与**工具数量**——不列工具名，因为工具清单能透露配置状态，
    /// 而清单本身在 initialize/tools/list 里已经给过有权限的人了。
    /// </summary>
    private Task HandleHealth(HttpContext ctx)
    {
        var tools = ToolNames.Count;
        return HttpSecurity.WriteJsonAsync(ctx, StatusCodes.Status200OK, new JsonObject
        {
            ["status"] = "ok",
            ["service"] = "AnimaAIProxy-MCP",
            ["uptimeSeconds"] = _startedAt == default ? 0 : (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            ["vocabReady"] = _vocab.IsLoaded,
            ["toolCount"] = tools
        });
    }

    private Task HandleRoot(HttpContext ctx)
        => HttpSecurity.WriteJsonAsync(ctx, StatusCodes.Status200OK, new JsonObject
        {
            ["service"] = "AnimaAIProxy-MCP",
            ["hint"] = "把 MCP 客户端指向 " + ClientUrl +
                       "，鉴权用代理签发的令牌（Authorization: Bearer aip-…）。",
            ["endpoints"] = new JsonArray { Path, "/health" }
        });
}
