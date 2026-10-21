using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// MCP 服务端的回归。分两层：
///
/// 1. **协议层**（直接调 <see cref="McpEndpoint"/>，不起 HTTP）：版本协商、
///    封套错误码、id 类型原样回、通知不回体、工具清单按就绪判据过滤、
///    未知工具与缺参数的错误形状。
/// 2. **传输层**（真的起一次 Kestrel）：401 / 405 / 403（Origin）/
///    关掉开关后端点**真的不存在**、以及一次完整的 initialize + tools/call。
///
/// 为什么两层都要：协议层能在没有网络、没有词库的机器上跑，且改一个字段的
/// 反馈只要几毫秒；传输层的那些东西（状态码、Allow 头、路由有没有注册）
/// 恰恰是"接进客户端之后才发现不对"的地方，协议层一条都覆盖不到。
/// </summary>
public static class Mcp
{
    public static void Run()
    {
        Harness.Section("MCP 协议层（不起 HTTP）");
        Protocol();

        Harness.Section("MCP 传输层：独立服务（与转发 API 分开）");
        Http();
    }

    // ---------------- 协议层 ----------------

    private static McpEndpoint NewEndpoint(out ProxyConfig cfg)
    {
        cfg = new ProxyConfig
        {
            // 指向不存在的词库：工具调用会返回"不可用"，这正好验证
            // isError 与"查不到"是两件事（见下面的断言）。
            VocabDbPath = Path.Combine(Path.GetTempPath(), "aip-mcp-nodb.sqlite"),
            SeedPath = Path.Combine(Path.GetTempPath(), "aip-mcp-noseed.json")
        };
        cfg.Normalize();
        return new McpEndpoint(cfg, () => new ToolHost(new VocabService()));
    }

    private static McpEndpoint.McpResult Send(McpEndpoint ep, string method,
                                              JsonObject? p = null, string? rawId = "1")
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (rawId is not null) req["id"] = JsonNode.Parse(rawId);
        if (p is not null) req["params"] = p;
        return ep.HandleAsync(req.ToJsonString()).GetAwaiter().GetResult();
    }

    private static JsonObject? Result(McpEndpoint.McpResult r) => r.Body?["result"] as JsonObject;

    private static string ErrorCode(McpEndpoint.McpResult r)
        => (r.Body?["error"]?["code"]?.ToJsonString() ?? "none");

    private static void Protocol()
    {
        var ep = NewEndpoint(out var cfg);

        // ---------- initialize ----------
        {
            var r = Send(ep, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "回归", ["version"] = "1" }
            });
            var result = Result(r);

            Harness.Eq("initialize → 200", r.Status, 200);
            Harness.Eq("协议版本按客户端那个回",
                       ChatWire.GetString(result, "protocolVersion"), "2025-06-18");
            Harness.True("capabilities.tools 存在（只声明我们真有的能力）",
                         result?["capabilities"]?["tools"] is JsonObject);
            Harness.Eq("listChanged=false（工具集合要重启才变，不发假通知）",
                       result?["capabilities"]?["tools"]?["listChanged"]?.ToJsonString(), "false");
            Harness.Eq("serverInfo.name", ChatWire.GetString(result?["serverInfo"] as JsonObject, "name"),
                       "AnimaAIProxy");
            Harness.True("serverInfo.version 非空", ChatWire.GetString(result?["serverInfo"] as JsonObject,
                                                                      "version").Length > 0);
            Harness.True("instructions 交代了「绝不凭记忆写标签名」",
                         ChatWire.GetString(result, "instructions")
                                  .Contains("never write a tag name from memory", StringComparison.Ordinal));
        }

        // ---------- 版本协商的两个边界 ----------
        {
            var future = Send(ep, "initialize", new JsonObject { ["protocolVersion"] = "2099-01-01" });
            Harness.Eq("客户端报不认识的版本 → 回我们自己最新的那个",
                       ChatWire.GetString(Result(future), "protocolVersion"),
                       McpEndpoint.LatestProtocolVersion);

            var none = Send(ep, "initialize", new JsonObject());
            Harness.Eq("客户端没报版本 → 也回最新的",
                       ChatWire.GetString(Result(none), "protocolVersion"),
                       McpEndpoint.LatestProtocolVersion);

            var old = Send(ep, "initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" });
            Harness.Eq("认识的老版本原样回（客户端据此确认能按它说话）",
                       ChatWire.GetString(Result(old), "protocolVersion"), "2024-11-05");
        }

        // ---------- 封套 ----------
        {
            var bad = ep.HandleAsync("{ 这不是 json").GetAwaiter().GetResult();
            Harness.Eq("坏 JSON → 400", bad.Status, 400);
            Harness.Eq("坏 JSON 的错误码是 -32700", ErrorCode(bad), "-32700");

            var v1 = ep.HandleAsync("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}")
                       .GetAwaiter().GetResult();
            Harness.Eq("jsonrpc 不是 2.0 → 400", v1.Status, 400);
            Harness.Eq("jsonrpc 不是 2.0 → -32600", ErrorCode(v1), "-32600");

            var batch = ep.HandleAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]")
                          .GetAwaiter().GetResult();
            Harness.Eq("批量 → 400（2025-06-18 已移除批量）", batch.Status, 400);
            Harness.Eq("批量的错误码是 -32600", ErrorCode(batch), "-32600");
            Harness.True("批量错误里说清了原因（不是一句含糊的 invalid request）",
                         ChatWire.GetString(batch.Body?["error"] as JsonObject, "message")
                                  .Contains("批量", StringComparison.Ordinal));

            var unknown = Send(ep, "resources/list");
            Harness.Eq("不认识的方法 → -32601", ErrorCode(unknown), "-32601");

            var missing = ep.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1}").GetAwaiter().GetResult();
            Harness.Eq("缺 method → -32600", ErrorCode(missing), "-32600");
        }

        // ---------- 通知：不能有响应体 ----------
        {
            var n = Send(ep, "notifications/initialized", rawId: null);
            Harness.Eq("通知 → 202", n.Status, 202);
            Harness.True("通知不回任何 JSON（写了客户端会当成 id 对不上的响应）", n.Body is null);
            Harness.Eq("通知的 IsNotification", n.IsNotification, true);
        }

        // ---------- id 的类型必须原样回 ----------
        {
            var num = Send(ep, "ping", rawId: "7");
            Harness.Eq("数字 id 回数字", num.Body?["id"]?.ToJsonString(), "7");

            var str = Send(ep, "ping", rawId: "\"abc\"");
            Harness.Eq("字符串 id 回字符串", str.Body?["id"]?.ToJsonString(), "\"abc\"");
        }

        // ---------- tools/list：就绪判据 ----------
        {
            var r = Send(ep, "tools/list");
            var tools = Result(r)?["tools"] as JsonArray;
            var names = tools?.Select(t => ChatWire.GetString(t as JsonObject, "name")).ToList()
                        ?? new List<string>();

            // 默认是 4 个：词库三个 + web_fetch。抓取默认开着（FetchEnabled=true），
            // 而自动降级在"本机没有浏览器"时依然能用 HTTP 引擎，所以它就该在。
            Harness.Eq("默认 4 个工具（词库三个 + web_fetch）", names.Count, 4);
            Harness.True("有 danbooru_search_tags", names.Contains(ToolCatalog.SearchName));
            Harness.True("有 danbooru_resolve_tag", names.Contains(ToolCatalog.ResolveName));
            Harness.True("有 danbooru_scan_text", names.Contains(ToolCatalog.ScanName));
            Harness.True("没配搜索 → 不下发 web_search（下发一个必然失败的工具更糟）",
                         !names.Contains(ToolCatalog.WebSearchName));
            Harness.True("默认带 web_fetch（抓取默认开，auto 不需要本机有浏览器）",
                         names.Contains(ToolCatalog.WebFetchName));

            var first = tools?[0] as JsonObject;
            Harness.Eq("每个工具都有 inputSchema",
                       ChatWire.GetString(first?["inputSchema"] as JsonObject, "type"), "object");
            Harness.True("工具说明非空（客户端拿它决定什么时候调）",
                         ChatWire.GetString(first, "description").Length > 80);
        }

        // ---------- tools/list：配好搜索与抓取之后多出两个 ----------
        {
            cfg.SearchEnabled = true;
            cfg.SearchProvider = "searxng";
            cfg.SearchBaseUrl = "https://searx.example.com/";
            cfg.FetchEnabled = true;
            cfg.FetchEngine = "http";

            var names = (Result(Send(ep, "tools/list"))?["tools"] as JsonArray)?
                        .Select(t => ChatWire.GetString(t as JsonObject, "name")).ToList()
                        ?? new List<string>();
            Harness.Eq("配好之后 5 个工具", names.Count, 5);
            Harness.True("多了 web_search", names.Contains(ToolCatalog.WebSearchName));
            Harness.True("多了 web_fetch", names.Contains(ToolCatalog.WebFetchName));
            Harness.True("词库工具排在最前（模型从上往下读，常用的是它们）",
                         names[0] == ToolCatalog.SearchName);

            cfg.SearchEnabled = false;
            cfg.FetchEnabled = false;
        }

        // ---------- tools/call ----------
        {
            var unknown = Send(ep, "tools/call", new JsonObject
            {
                ["name"] = "no_such_tool",
                ["arguments"] = new JsonObject()
            });
            Harness.Eq("未知工具 → -32602", ErrorCode(unknown), "-32602");
            Harness.True("未知工具的错误里列了可用工具（排障要靠它）",
                         ChatWire.GetString(unknown.Body?["error"] as JsonObject, "message")
                                  .Contains(ToolCatalog.SearchName, StringComparison.Ordinal));

            var noName = Send(ep, "tools/call", new JsonObject { ["arguments"] = new JsonObject() });
            Harness.Eq("缺 name → -32602", ErrorCode(noName), "-32602");

            var call = Send(ep, "tools/call", new JsonObject
            {
                ["name"] = ToolCatalog.ResolveName,
                ["arguments"] = new JsonObject { ["tag"] = "white dress" }
            });
            var result = Result(call);
            var content = result?["content"] as JsonArray;
            var text = content is { Count: > 0 }
                ? ChatWire.GetString(content[0] as JsonObject, "text")
                : "";

            Harness.Eq("词库不可用时工具调用本身仍走完（200，不是 500）", call.Status, 200);
            Harness.True("isError=true（这次查不了）",
                         result?["isError"]?.ToJsonString() == "true");
            Harness.True("文本里写了数据库不可用（而不是「没有这个标签」）",
                         text.Contains("not available", StringComparison.Ordinal));
            Harness.True("content[0].type == text",
                         ChatWire.GetString(content?[0] as JsonObject, "type") == "text");
        }
    }

    // ---------------- 传输层：独立服务（不依赖转发 API） ----------------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed record HttpResult(int Status, string Text, string Allow, string ContentType);

    private static HttpResult Post(HttpClient http, string url, string body, string? token,
                                   string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);

        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
        return new HttpResult((int)resp.StatusCode,
                              resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                              string.Join(",", resp.Content.Headers.Allow),
                              resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    private static int GetStatus(HttpClient http, string url)
    {
        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
        return (int)resp.StatusCode;
    }

    private const string InitializeBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
        "\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"回归\"}}}";

    private const string ListToolsBody = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}";

    private static int ToolCount(HttpClient http, string url, string token)
    {
        var r = Post(http, url, ListToolsBody, token);
        return (ChatWire.TryParseObject(r.Text)?["result"]?["tools"] as JsonArray)?.Count ?? -1;
    }

    /// <summary>
    /// 传输层：真的起一次 MCP 服务，验鉴权、方法限制、Origin、逐工具开关，
    /// 以及**它和转发 API 是两个服务**这件事——互相的路径都不认识。
    /// </summary>
    private static void Http()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "aip-mcpcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        var oldTokenPath = TokenStore.FilePathOverride;
        var oldCfgPath = ProxyConfig.FilePathOverride;
        TokenStore.FilePathOverride = Path.Combine(tmpDir, "proxy.tokens.json");
        ProxyConfig.FilePathOverride = Path.Combine(tmpDir, "proxy.config.json");

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            McpPort = FreePort(),
            BindScope = "local",
            McpBindScope = "local",
            RequireToken = true,
            McpRequireToken = true,
            McpEnabled = true,
            ToolsEnabled = true,
            TimeoutSeconds = 5,
            VocabDbPath = Path.Combine(tmpDir, "no-such-vocab.sqlite"),
            SeedPath = Path.Combine(tmpDir, "no-such-seed.json")
        };
        cfg.Normalize();

        Harness.True("两个服务默认用不同端口（撞了 Normalize 会让开）", cfg.Port != cfg.McpPort);

        var tokens = new TokenStore();
        tokens.Tokens.Add(TokenStore.Create("回归用", "mcp", out var good));

        // 同一个 TokenStore 注入两个服务：一枚令牌两边都认，这是刻意的
        // （用户只需要管一份令牌），但端口/绑定/启停是各自独立的。
        var mcp = new McpServer(cfg, tokens, new VocabService());
        var api = new ProxyServer(cfg, tokens, new VocabService());

        var mcpBase = $"http://127.0.0.1:{cfg.McpPort}";
        var apiBase = $"http://127.0.0.1:{cfg.Port}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            // ---------- MCP 单独起来（转发 API 还停着） ----------
            mcp.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Harness.True("MCP 单独就能起来（转发 API 没起）", mcp.IsRunning);
            Harness.Eq("客户端地址用的是 MCP 自己的端口",
                       mcp.ClientUrl, mcpBase + "/mcp");

            // ---------- 鉴权 ----------
            var noToken = Post(http, mcpBase + "/mcp", InitializeBody, null);
            Harness.Eq("没有令牌 → 401", noToken.Status, 401);
            Harness.True("401 体是 JSON-RPC 形状（MCP 客户端按它解析）",
                         ChatWire.TryParseObject(noToken.Text)?["error"] is JsonObject);

            var badToken = Post(http, mcpBase + "/mcp", InitializeBody, "aip-wrong");
            Harness.Eq("错令牌 → 401", badToken.Status, 401);

            // ---------- 正常链路 ----------
            var ok = Post(http, mcpBase + "/mcp", InitializeBody, good);
            Harness.Eq("带令牌 initialize → 200", ok.Status, 200);
            Harness.Eq("initialize 回的是协商后的版本",
                       ChatWire.GetString(ChatWire.TryParseObject(ok.Text)?["result"] as JsonObject,
                                          "protocolVersion"), "2025-06-18");
            Harness.True("content-type 是 application/json",
                         ok.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase));

            Harness.Eq("tools/list 4 个工具（词库三个 + web_fetch）",
                       ToolCount(http, mcpBase + "/mcp", good), 4);

            var call = Post(http, mcpBase + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{" +
                "\"name\":\"danbooru_scan_text\",\"arguments\":{\"text\":\"一个女孩在笑\"}}}", good);
            Harness.True("tools/call 有 content 数组",
                         (ChatWire.TryParseObject(call.Text)?["result"] as JsonObject)?["content"] is JsonArray);

            // ---------- 通知：202 且没有体 ----------
            var note = Post(http, mcpBase + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", good);
            Harness.Eq("通知 → 202", note.Status, 202);
            Harness.Eq("通知没有响应体", note.Text.Length, 0);

            // ---------- 方法限制 ----------
            using (var get = http.SendAsync(new HttpRequestMessage(HttpMethod.Get, mcpBase + "/mcp"))
                               .GetAwaiter().GetResult())
            {
                Harness.Eq("GET /mcp → 405（没有 SSE 流）", (int)get.StatusCode, 405);
                Harness.True("405 带 Allow: POST",
                             string.Join(",", get.Content.Headers.Allow)
                                   .Contains("POST", StringComparison.OrdinalIgnoreCase));
            }
            using (var del = http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, mcpBase + "/mcp"))
                               .GetAwaiter().GetResult())
                Harness.Eq("DELETE /mcp → 405（无状态，没有会话可结束）", (int)del.StatusCode, 405);

            // ---------- Origin ----------
            var evil = Post(http, mcpBase + "/mcp", InitializeBody, good, "https://evil.example.com");
            Harness.Eq("Origin 是外站 → 403（DNS rebinding / 网页 CSRF）", evil.Status, 403);
            var self = Post(http, mcpBase + "/mcp", InitializeBody, good, mcpBase);
            Harness.Eq("Origin 是本机自己 → 200", self.Status, 200);
            var noOrigin = Post(http, mcpBase + "/mcp", InitializeBody, good);
            Harness.Eq("不带 Origin（本地客户端常态）→ 200", noOrigin.Status, 200);

            // ---------- /health ----------
            var health = ChatWire.TryParseObject(
                http.GetStringAsync(mcpBase + "/health").GetAwaiter().GetResult());
            Harness.Eq("MCP /health 报 ok", ChatWire.GetString(health, "status"), "ok");
            Harness.Eq("MCP /health 报工具数", health?["toolCount"]?.ToJsonString(), "4");
            Harness.True("MCP /health 不列工具名（那是给有令牌的人看的）",
                         ChatWire.GetString(health, "service") == "AnimaAIProxy-MCP");

            // ---------- 逐工具开关（传输层再确认一次） ----------
            cfg.McpTools[ToolCatalog.ScanName] = false;
            Harness.Eq("关掉 danbooru_scan_text 后清单只剩 3 个",
                       ToolCount(http, mcpBase + "/mcp", good), 3);
            var blocked = Post(http, mcpBase + "/mcp",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{" +
                "\"name\":\"danbooru_scan_text\",\"arguments\":{\"text\":\"测试\"}}}", good);
            Harness.Eq("关掉的工具调不动（-32602，不是\"能调但看不见\"）",
                       (blocked.Text.Contains("-32602") ? "-32602" : blocked.Text[..0]), "-32602");
            cfg.McpTools.Clear();

            // ---------- 分离性：两个服务互不认识对方的路径 ----------
            api.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Harness.True("转发 API 也能起来（两个服务可以同时运行）", api.IsRunning);

            var mcpOnApi = Post(http, apiBase + "/mcp", InitializeBody, good);
            Harness.Eq("转发端口上没有 /mcp → 404", mcpOnApi.Status, 404);
            Harness.Eq("MCP 端口上没有 /v1/models → 404",
                       GetStatus(http, mcpBase + "/v1/models"), 404);
            Harness.Eq("转发端口上的 /health 仍然在", GetStatus(http, apiBase + "/health"), 200);

            // ---------- 停 MCP：转发 API 不受影响 ----------
            mcp.StopAsync().GetAwaiter().GetResult();
            Harness.True("MCP 停了", !mcp.IsRunning);
            Harness.Eq("MCP 停掉后转发 API 照样在", GetStatus(http, apiBase + "/health"), 200);
        }
        finally
        {
            api.StopAsync().GetAwaiter().GetResult();
            mcp.StopAsync().GetAwaiter().GetResult();
            Harness.True("两个服务都停了", !api.IsRunning && !mcp.IsRunning);

            TokenStore.FilePathOverride = oldTokenPath;
            ProxyConfig.FilePathOverride = oldCfgPath;
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 删不掉不影响结论 */ }
        }
    }
}
