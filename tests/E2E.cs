using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 端到端联调：**客户端 → 代理 → 桩上游**，全部走真实 HTTP。
///
/// 为什么这一层不能省：<see cref="ToolLoop"/> 的离线回归用一个内存里的假上游
/// 驱动，它验的是控制流；<see cref="Server"/> 验的是路由与鉴权。两者都验不到
/// "把它们接起来之后还对不对" —— 而接起来才出问题的地方恰恰最多：
/// 工具声明有没有真的出现在发往上游的报文里、工具结果有没有真的回到模型手上、
/// 最终交给客户端的那份 JSON 里我们的 tool_call 摘干净了没有、usage 累加了没有、
/// 流式是不是真的**一块一块**回来的。
///
/// 桩上游是**本进程内第二个 Kestrel**，按剧本回话并记下每次收到的请求体。
/// 用真词库（若可用）执行真实工具，所以这条链路是完整的那条。
/// </summary>
public static class E2E
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------------- 桩上游 ----------------

    private sealed class StubUpstream
    {
        private WebApplication? _app;
        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

        public List<JsonObject> Received { get; } = new();

        private readonly Queue<JsonObject> _replies = new();

        /// <summary>流式模式：不按剧本回 JSON，而是分块吐 SSE 并每块 flush。</summary>
        public bool StreamMode { get; set; }

        public int StreamChunks { get; set; } = 4;
        public int StreamChunkDelayMs { get; set; } = 150;

        public void Enqueue(JsonObject reply) => _replies.Enqueue(reply);

        public async Task StartAsync()
        {
            Port = FreePort();
            var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            b.Logging.ClearProviders();
            b.Logging.SetMinimumLevel(LogLevel.None);
            b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, Port));

            var app = b.Build();

            app.MapPost("/v1/chat/completions", async ctx =>
            {
                var raw = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync();
                var body = ChatWire.TryParseObject(raw);
                lock (Received) Received.Add(body ?? new JsonObject());

                if (StreamMode)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    for (var i = 0; i < StreamChunks; i++)
                    {
                        await ctx.Response.WriteAsync(
                            $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"块{i}\"}}}}]}}\n\n");
                        await ctx.Response.Body.FlushAsync();
                        await Task.Delay(StreamChunkDelayMs);
                    }
                    await ctx.Response.WriteAsync("data: [DONE]\n\n");
                    await ctx.Response.Body.FlushAsync();
                    return;
                }

                JsonObject reply;
                lock (_replies)
                    reply = _replies.Count > 0 ? _replies.Dequeue() : new JsonObject
                    {
                        ["error"] = new JsonObject { ["message"] = "桩上游的剧本用完了" }
                    };

                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(ChatWire.ToJson(reply));
            });

            app.MapGet("/v1/models", async ctx =>
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(ChatWire.ToJson(new JsonObject
                {
                    ["object"] = "list",
                    ["data"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "stub-model", ["object"] = "model" }
                    }
                }));
            });

            await app.StartAsync();
            _app = app;
        }

        public async Task StopAsync()
        {
            if (_app is null) return;
            await _app.StopAsync(TimeSpan.FromSeconds(3));
            await _app.DisposeAsync();
            _app = null;
        }
    }

    // ---------------- 报文构造 ----------------

    private static JsonObject Answer(string content, int pt, int ct) => new()
    {
        ["id"] = "chatcmpl-stub",
        ["object"] = "chat.completion",
        ["model"] = "stub-model",
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }
            }
        },
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = pt, ["completion_tokens"] = ct, ["total_tokens"] = pt + ct
        }
    };

    private static JsonObject ToolCall(string id, string name, string args, int pt, int ct)
    {
        var msg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
                }
            }
        };
        return new JsonObject
        {
            ["id"] = "chatcmpl-stub",
            ["object"] = "chat.completion",
            ["model"] = "stub-model",
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["finish_reason"] = "tool_calls", ["message"] = msg }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = pt, ["completion_tokens"] = ct, ["total_tokens"] = pt + ct
            }
        };
    }

    private static JsonObject ClientBody(bool stream = false) => new()
    {
        ["model"] = "client-model",
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a helpful agent." },
            new JsonObject { ["role"] = "user", ["content"] = "弓 这个标签叫什么？" }
        },
        ["temperature"] = 0.7,
        ["max_tokens"] = 2048,
        ["reasoning_effort"] = "high",
        ["stream"] = stream,
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read a file.",
                    ["parameters"] = new JsonObject { ["type"] = "object" }
                }
            }
        }
    };

    // ---------------- 主流程 ----------------

    public static void Run(string[] args)
    {
        Harness.Section("端到端：客户端 → 代理 → 桩上游（全部真实 HTTP）");

        var dbPath = Vocab.ResolveDbPath(args);
        var hasDb = !string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath);
        if (!hasDb)
            Harness.Note("找不到词库：工具执行那几条会退化成「词库不可用」，其余断言照常。");

        var tmpDir = Path.Combine(Path.GetTempPath(), "aip-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        var oldTokenPath = TokenStore.FilePathOverride;
        TokenStore.FilePathOverride = Path.Combine(tmpDir, "proxy.tokens.json");

        var stub = new StubUpstream();
        stub.StartAsync().GetAwaiter().GetResult();

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            BindScope = "local",
            UpstreamBaseUrl = stub.BaseUrl,
            RequireToken = true,
            ToolsEnabled = true,
            AnnounceTools = true,
            MaxToolRounds = 4,
            TimeoutSeconds = 30,
            VocabDbPath = hasDb ? dbPath! : Path.Combine(tmpDir, "no-vocab.sqlite"),
            SeedPath = hasDb ? (Vocab.ResolveSeedPath() ?? "") : Path.Combine(tmpDir, "no-seed.json")
        };

        var tokens = new TokenStore();
        tokens.Tokens.Add(TokenStore.Create("e2e", "", out var token));

        var server = new ProxyServer(cfg, tokens, new VocabService());
        server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var url = $"http://127.0.0.1:{cfg.Port}/v1/chat/completions";

        try
        {
            // ================= 1. 非流式：注入 → 本地执行 → 摘干净 =================
            {
                stub.Enqueue(ToolCall("call_1", ToolCatalog.SearchName, "{\"query\":\"弓\"}", 20, 7));
                stub.Enqueue(Answer("弓 对应的标签是 bow (weapon)。", 30, 9));
                stub.Received.Clear();

                var r = Post(http, url, ChatWire.ToJson(ClientBody()), token);
                Harness.Eq("整条链路走通，200", r.Status, 200);

                var parsed = ChatWire.Parse(ChatWire.TryParseObject(r.Text)!)!;

                // 上游收到了什么
                Harness.Eq("上游被问了两次（第一轮工具调用 + 第二轮收尾）", stub.Received.Count, 2);

                var first = stub.Received[0];
                var sentNames = ((JsonArray)first["tools"]!).Select(t =>
                    ChatWire.GetString(t!["function"] as JsonObject, "name")).ToList();
                Harness.True("发往上游的请求里有接入方的工具（它必须知道环境里有什么）",
                             sentNames.Contains("read_file"));
                Harness.True("发往上游的请求里有我们的三个工具",
                             sentNames.Contains(ToolCatalog.SearchName) &&
                             sentNames.Contains(ToolCatalog.ResolveName) &&
                             sentNames.Contains(ToolCatalog.ScanName));
                Harness.Eq("reasoning_effort 原样透传到了上游（代理不劫持）",
                           ChatWire.GetString(first, "reasoning_effort"), "high");
                Harness.Eq("模型名换成了上游要的（映射表没配 → 原样）",
                           ChatWire.GetString(first, "model"), "client-model");
                Harness.Eq("注入路径强制按非流式问上游", ChatWire.GetBool(first, "stream"), false);

                var sys = ChatWire.GetString(((JsonArray)first["messages"]!)[0] as JsonObject, "content");
                Harness.True("系统提示里追加了工具说明，且接入方原有的还在",
                             sys.Contains("You are a helpful agent.") &&
                             sys.Contains(ToolCatalog.SearchName));

                // 工具真的在本地执行了 —— 结果回到了第二轮的上游请求里
                var second = stub.Received[1];
                var toolMsgs = ((JsonArray)second["messages"]!).Where(m =>
                    ChatWire.GetString(m as JsonObject, "role") == "tool").ToList();
                Harness.True("第二轮请求里带上了 role:tool 的工具结果",
                             toolMsgs.Count == 1);

                var toolText = ChatWire.GetString(toolMsgs[0] as JsonObject, "content");
                if (hasDb)
                {
                    Harness.True("工具结果里是真实词库查出来的标签（弓 → bow (weapon)）",
                                 toolText.Contains("bow (weapon)", StringComparison.Ordinal));
                    Harness.Info("工具回灌给模型的内容：" + toolText.Split('\n')[0] +
                                 " …（共 " + toolText.Split('\n').Length + " 行）");
                }
                else
                {
                    Harness.True("词库不可用时如实说明，而不是编一个标签",
                                 toolText.Contains("not available", StringComparison.OrdinalIgnoreCase));
                }

                // 客户端拿到了什么
                Harness.Eq("客户端拿到的是模型的最终回答", parsed.Content, "弓 对应的标签是 bow (weapon)。");
                Harness.Eq("客户端看不到我们的任何 tool_call（摘干净了）", parsed.ToolCalls.Count, 0);
                Harness.Eq("finish_reason 改回 stop（否则客户端会等一个不存在的工具）",
                           parsed.FinishReason, "stop");
                Harness.Eq("模型名回写成客户端请求时用的那个",
                           ChatWire.GetString(ChatWire.TryParseObject(r.Text), "model"), "client-model");

                var usage = ChatWire.TryParseObject(r.Text)?["usage"] as JsonObject;
                Harness.Eq("usage 跨轮累加（20+30）", ChatWire.GetInt(usage, "prompt_tokens"), 50);
                Harness.Eq("usage 跨轮累加（7+9）", ChatWire.GetInt(usage, "completion_tokens"), 16);
                Harness.Eq("usage 跨轮累加（27+39）", ChatWire.GetInt(usage, "total_tokens"), 66);

                // 客户端的历史里没有影子消息：返回给它的 message 上不该出现
                // tool_calls（我们的被摘掉了；接入方这一轮本来就没调工具）。
                var finalJson = ChatWire.TryParseObject(r.Text)!;
                var finalMsg = (JsonObject)finalJson["choices"]![0]!["message"]!;
                Harness.True("返回给客户端的 message 里没有 tool_calls 字段",
                             finalMsg["tool_calls"] is null);
                Harness.Eq("返回给客户端的 message 只有 role/content",
                           finalMsg.Count, 2);
            }

            // ================= 2. 接入方的工具原样交还 =================
            {
                stub.Enqueue(ToolCall("call_theirs", "read_file", "{\"path\":\"/tmp/a\"}", 11, 3));
                stub.Received.Clear();

                var r = Post(http, url, ChatWire.ToJson(ClientBody()), token);
                var parsed = ChatWire.Parse(ChatWire.TryParseObject(r.Text)!)!;

                Harness.Eq("只调接入方工具时上游只被问一次", stub.Received.Count, 1);
                Harness.Eq("接入方的 tool_call 原样交还", parsed.ToolCalls.Count, 1);
                Harness.Eq("交还的是它那个", parsed.ToolCalls[0].Name, "read_file");
                Harness.Eq("id 一字不差（它要靠这个回填结果）", parsed.ToolCalls[0].Id, "call_theirs");
                Harness.Eq("finish_reason 保持 tool_calls", parsed.FinishReason, "tool_calls");
            }

            // ================= 3. 流式：真的透传，且真的分块回来 =================
            {
                stub.StreamMode = true;
                stub.StreamChunks = 4;
                stub.StreamChunkDelayMs = 150;
                stub.Received.Clear();

                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(ChatWire.ToJson(ClientBody(stream: true)),
                                                Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                                     .GetAwaiter().GetResult();

                Harness.Eq("流式请求 200", (int)resp.StatusCode, 200);
                Harness.True("content-type 是 event-stream",
                             (resp.Content.Headers.ContentType?.ToString() ?? "")
                                 .Contains("event-stream", StringComparison.OrdinalIgnoreCase));

                var sw = Stopwatch.StartNew();
                var arrivals = new List<long>();
                var text = new StringBuilder();

                using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    var buf = new byte[4096];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        text.Append(Encoding.UTF8.GetString(buf, 0, n));
                        arrivals.Add(sw.ElapsedMilliseconds);
                    }
                }
                var totalMs = sw.ElapsedMilliseconds;

                Harness.True("SSE 内容完整（4 块 + [DONE]）",
                             text.ToString().Contains("块0") && text.ToString().Contains("块3") &&
                             text.ToString().Contains("[DONE]"));
                Harness.Eq("流式路径不注入工具（这一阶段如实纯透传）", stub.Received.Count, 1);
                Harness.Eq("上游收到的 stream 是 true", ChatWire.GetBool(stub.Received[0], "stream"), true);
                Harness.True("上游请求里没有我们的工具（流式不做注入）",
                             stub.Received[0]["tools"] is null ||
                             !((JsonArray)stub.Received[0]["tools"]!).Any(t =>
                                 ChatWire.GetString(t!["function"] as JsonObject, "name")
                                     .StartsWith("danbooru_", StringComparison.Ordinal)));

                // 这一条是流式路径的核心：分块到达，而不是攒到最后一次性给。
                // 桩上游每块之间睡 150ms，4 块 ≈ 600ms；若代理做了缓冲，
                // 客户端会在接近 600ms 时一次性收到全部内容。
                Harness.True($"数据是分多次到达的（收到 {arrivals.Count} 次读，不是一次）",
                             arrivals.Count >= 3);
                Harness.True($"首块远早于最后一块（首块 {arrivals[0]}ms / 总 {totalMs}ms）",
                             arrivals[0] < totalMs / 2);
                Harness.Info($"流式时序：读次数={arrivals.Count}，首块={arrivals[0]}ms，总耗时={totalMs}ms");

                stub.StreamMode = false;
            }

            // ================= 4. /v1/models 走真实上游 =================
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{cfg.Port}/v1/models");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var r = http.SendAsync(req).GetAwaiter().GetResult();
                var json = ChatWire.TryParseObject(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());

                Harness.Eq("/v1/models 200", (int)r.StatusCode, 200);
                var ids = (json?["data"] as JsonArray)?.Select(n =>
                    ChatWire.GetString(n as JsonObject, "id")).ToList() ?? new List<string>();
                Harness.True("上游的模型出现在列表里", ids.Contains("stub-model"));
            }
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            stub.StopAsync().GetAwaiter().GetResult();
            TokenStore.FilePathOverride = oldTokenPath;
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    private sealed record HttpResult(int Status, string Text);

    private static HttpResult Post(HttpClient http, string url, string body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
        return new HttpResult((int)resp.StatusCode,
                              resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }
}
