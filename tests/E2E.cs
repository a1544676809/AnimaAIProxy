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

        /// <summary>
        /// 与 <see cref="Received"/> 一一对应的请求头快照。
        /// 用来验"站点自带的请求头真的发出去了"——这一条只有真收到请求才测得出来，
        /// 而它失效时的症状是"某个网关回 401"，与请求头毫无字面关联。
        /// </summary>
        public List<Dictionary<string, string>> ReceivedHeaders { get; } = new();

        private readonly Queue<JsonObject> _replies = new();

        /// <summary>流式模式：不按剧本回 JSON，而是分块吐 SSE 并每块 flush。</summary>
        public bool StreamMode { get; set; }

        public int StreamChunks { get; set; } = 4;
        public int StreamChunkDelayMs { get; set; } = 150;

        /// <summary>
        /// 流式模式下改吐这一串**原始 SSE 文本**（每项一段，逐段 flush）。
        /// 用来演"流式里模型先调我们的工具、再逐字把答案说出来"。
        /// </summary>
        public Queue<string> StreamScript { get; } = new();

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
                lock (Received)
                {
                    Received.Add(body ?? new JsonObject());
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in ctx.Request.Headers) headers[h.Key] = h.Value.ToString();
                    ReceivedHeaders.Add(headers);
                }

                if (StreamMode)
                {
                    ctx.Response.ContentType = "text/event-stream";

                    // 有剧本就按剧本吐原始 SSE（用来演多轮：先工具、后正文）。
                    string[] pieces;
                    lock (StreamScript)
                    {
                        pieces = StreamScript.Count > 0
                            ? StreamScript.Dequeue().Split(new[] { "\u0000" }, StringSplitOptions.None)
                            : new string[0];
                    }

                    if (pieces.Length > 0)
                    {
                        foreach (var p in pieces)
                        {
                            await ctx.Response.WriteAsync(p);
                            await ctx.Response.Body.FlushAsync();
                            await Task.Delay(StreamChunkDelayMs);
                        }
                        return;
                    }

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

    private static JsonObject ClientBody(bool stream = false, string model = "client-model") => new()
    {
        ["model"] = model,
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

        // 配置也必须重定向。**这一条是被一次真实事故逼出来的**：
        // 第 4 节的 /v1/models 会触发"按需拉取模型表"，那一步会 Save()，
        // 而 Save 写的是 ProxyConfig.ConfigPath——没重定向时它把用户的真配置
        // 整个盖成了这份临时配置（站点、密钥全没了，且没有任何提示）。
        var oldCfgPath = ProxyConfig.FilePathOverride;
        ProxyConfig.FilePathOverride = Path.Combine(tmpDir, "proxy.config.json");

        var stub = new StubUpstream();
        stub.StartAsync().GetAwaiter().GetResult();

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            BindScope = "local",
            RequireToken = true,
            ToolsEnabled = true,
            AnnounceTools = true,
            MaxToolRounds = 4,
            TimeoutSeconds = 30,
            VocabDbPath = hasDb ? dbPath! : Path.Combine(tmpDir, "no-vocab.sqlite"),
            SeedPath = hasDb ? (Vocab.ResolveSeedPath() ?? "") : Path.Combine(tmpDir, "no-seed.json")
        };

        // 桩上游当默认站点。**刻意不预置模型、也不设 ModelsFetchedAt**：
        // 这样第 4 节的 /v1/models 会走"按需拉取"那条路——那是新功能的入口，
        // 只有真发一次请求才验得到。
        Fixture.AddProvider(cfg, stub.BaseUrl, name: "桩上游");

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

            // ================= 3. 流式：注入工具，且真的分块回来 =================
            {
                stub.StreamMode = true;
                stub.StreamChunks = 4;
                stub.StreamChunkDelayMs = 150;
                stub.Received.Clear();
                stub.StreamScript.Clear();
                // 剧本：4 段正文 + [DONE]。模型这一轮**不调工具**，
                // 所以是纯透传形态，用来验时序（首块必须远早于末尾）。
                stub.StreamScript.Enqueue(
                    "data: {\"id\":\"s1\",\"object\":\"chat.completion.chunk\",\"model\":\"stub-model\"," +
                    "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"块0\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"块1\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"块2\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"块3\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n\u0000" +
                    "data: [DONE]\n\n");

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
                Harness.Eq("流式路径也被问了一次上游", stub.Received.Count, 1);
                Harness.Eq("上游收到的 stream 是 true", ChatWire.GetBool(stub.Received[0], "stream"), true);
                // **这条以前断言的是反面**（"流式不做注入"）。现在流式也注入：
                // 正文实时转发、工具调用按轮缓冲，首字延迟没有任何损失。
                Harness.True("流式路径也注入了我们的工具（旧行为是不注入）",
                             (stub.Received[0]["tools"] as JsonArray)?.Any(t =>
                                 ChatWire.GetString(t!["function"] as JsonObject, "name")
                                     .StartsWith("danbooru_", StringComparison.Ordinal)) == true);
                Harness.Eq("流式路径也带了 reasoning_effort（不劫持）",
                           ChatWire.GetString(stub.Received[0], "reasoning_effort"), "high");

                // 这一条是流式路径的核心：分块到达，而不是攒到最后一次性给。
                // 桩上游每块之间睡 150ms，若代理做了缓冲，客户端会在接近末尾时
                // 一次性收到全部内容。
                Harness.True($"数据是分多次到达的（收到 {arrivals.Count} 次读，不是一次）",
                             arrivals.Count >= 3);
                Harness.True($"首块远早于最后一块（首块 {arrivals[0]}ms / 总 {totalMs}ms）",
                             arrivals[0] < totalMs / 2);
                Harness.Info($"流式时序：读次数={arrivals.Count}，首块={arrivals[0]}ms，总耗时={totalMs}ms");

                stub.StreamMode = false;
                stub.StreamScript.Clear();
            }

            // ================= 3b. 流式 + 我们的工具：本地执行后答案才出来 =================
            {
                stub.StreamMode = true;
                stub.StreamChunkDelayMs = 0;
                stub.Received.Clear();
                stub.StreamScript.Clear();

                // 第一轮：模型要调我们的工具（arguments 分片下发，逼真）
                stub.StreamScript.Enqueue(
                    "data: {\"id\":\"s2\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s2\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                    "\"id\":\"call_s1\",\"type\":\"function\",\"function\":{\"name\":\"" +
                    ToolCatalog.SearchName + "\",\"arguments\":\"\"}}]}}]}\n\n\u0000" +
                    "data: {\"id\":\"s2\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                    "\"function\":{\"arguments\":\"{\\\"query\\\":\\\"弓\\\"}\"}}]}}]}\n\n\u0000" +
                    "data: {\"id\":\"s2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n\u0000" +
                    "data: [DONE]\n\n");

                // 第二轮：拿到工具结果后的最终回答
                stub.StreamScript.Enqueue(
                    "data: {\"id\":\"s3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"弓 对应 bow (weapon)。\"}}]}\n\n\u0000" +
                    "data: {\"id\":\"s3\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n\u0000" +
                    "data: [DONE]\n\n");

                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(ChatWire.ToJson(ClientBody(stream: true)),
                                                Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                                     .GetAwaiter().GetResult();
                var streamText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                Harness.Eq("流式注入：上游被问了两次（工具轮 + 收尾轮）", stub.Received.Count, 2);

                // 第二轮请求里必须带上真实执行的工具结果
                if (stub.Received.Count >= 2)
                {
                    var toolMsgs = ((JsonArray)stub.Received[1]["messages"]!).Where(m =>
                        ChatWire.GetString(m as JsonObject, "role") == "tool").ToList();
                    Harness.True("第二轮请求里带上了 role:tool 的工具结果", toolMsgs.Count == 1);
                    if (toolMsgs.Count == 1 && hasDb)
                    {
                        var toolText = ChatWire.GetString(toolMsgs[0] as JsonObject, "content");
                        Harness.True("流式路径里工具是真的在本地执行的（弓 → bow (weapon)）",
                                     toolText.Contains("bow (weapon)", StringComparison.Ordinal));
                    }
                }

                // 客户端看到的那条流里不该出现我们的 tool_call
                Harness.True("客户端拿到的正文是模型的最终回答",
                             streamText.Contains("弓 对应 bow (weapon)。", StringComparison.Ordinal));
                Harness.True("客户端的流里没有我们的 tool_call（连名字都搜不到）",
                             !streamText.Contains(ToolCatalog.SearchName, StringComparison.Ordinal));
                Harness.True("客户端的流里有 finish_reason=stop",
                             streamText.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
                Harness.True("客户端的流里以 [DONE] 收尾",
                             streamText.Contains("[DONE]", StringComparison.Ordinal));

                stub.StreamMode = false;
                stub.StreamScript.Clear();
            }

            // ================= 4. /v1/models：按需拉取 + 勾选结果 =================
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{cfg.Port}/v1/models");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var r = http.SendAsync(req).GetAwaiter().GetResult();
                var json = ChatWire.TryParseObject(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());

                Harness.Eq("/v1/models 200", (int)r.StatusCode, 200);
                var ids = (json?["data"] as JsonArray)?.Select(n =>
                    ChatWire.GetString(n as JsonObject, "id")).ToList() ?? new List<string>();
                Harness.True("从未拉过的站点被按需拉了一次，模型出现在表里", ids.Contains("stub-model"));
                Harness.True("站点自己拉完就记下了时间（不会每次 GET 都重拉）",
                             cfg.Providers[0].ModelsFetchedAt is not null);
                Harness.Eq("响应是 OpenAI 的 list 形状", ChatWire.GetString(json, "object"), "list");

                // 关掉这个模型 → 它必须从对外公布的模型表里消失。
                // 这正是"维护一个模型列表来开关引入的模型"这条需求的核心行为。
                cfg.Providers[0].FindModel("stub-model")!.Enabled = false;
                using var r2 = http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{cfg.Port}/v1/models")
                {
                    Headers = { { "Authorization", "Bearer " + token } }
                }).GetAwaiter().GetResult();
                var ids2 = (ChatWire.TryParseObject(r2.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["data"]
                    as JsonArray)?.Select(n => ChatWire.GetString(n as JsonObject, "id")).ToList() ?? new List<string>();
                Harness.True("关掉的模型从对外模型表里消失", !ids2.Contains("stub-model"));
                cfg.Providers[0].FindModel("stub-model")!.Enabled = true;
            }

            // ================= 5. 两个站点：按模型名路由 + 站点自带请求头 =================
            {
                var stub2 = new StubUpstream();
                stub2.StartAsync().GetAwaiter().GetResult();
                try
                {
                    // 站点2 声明一个只有它有的模型，并带一组自己的请求头。
                    // 预置模型 + 标记已拉取：这一节要测的是路由与请求头，不是拉取。
                    var p2 = Fixture.AddProvider(cfg, stub2.BaseUrl, name: "站点2",
                        headers: "x-opencode-session: sess-123\nX-Title: AnimaAIProxy",
                        models: new[] { "only-on-2" });
                    p2.ModelsFetchedAt = "已预置";

                    stub2.Enqueue(Answer("我是站点2。", 5, 5));
                    var beforeDefault = stub.Received.Count;

                    var r = Post(http, url, ChatWire.ToJson(ClientBody(model: "only-on-2")), token);
                    Harness.Eq("按模型名路由：整条链路走通", r.Status, 200);

                    Harness.Eq("请求真的发到了站点2", stub2.Received.Count, 1);
                    Harness.Eq("默认站点一个字节都没收到（没有广播式重发）",
                               stub.Received.Count, beforeDefault);
                    Harness.Eq("站点2 收到的模型名就是它自己的那个",
                               ChatWire.GetString(stub2.Received[0], "model"), "only-on-2");

                    var h = stub2.ReceivedHeaders[0];
                    Harness.True("站点自带的路由头发出去了",
                                 h.TryGetValue("x-opencode-session", out var sess) && sess == "sess-123");
                    Harness.True("第二个自定义头也发出去了",
                                 h.TryGetValue("X-Title", out var title) && title == "AnimaAIProxy");

                    // 反向断言：站点1 从没收到过站点2 的请求头。
                    // 少了这一条，"把请求头加在所有站点上"这种实现也能全绿。
                    Harness.True("站点1 的历史请求里没有站点2 的专属头",
                                 !stub.ReceivedHeaders.Any(x => x.ContainsKey("x-opencode-session")));

                    var resp = ChatWire.TryParseObject(r.Text);
                    Harness.Eq("响应里的模型名回写成客户端报的那个",
                               ChatWire.GetString(resp, "model"), "only-on-2");
                }
                finally
                {
                    stub2.StopAsync().GetAwaiter().GetResult();
                }
            }

            // ===== 4b. 「从未拉过」写成空串的站点，同样要被拉取 =====
            // 空串与 null 都表示"没拉过"。只认 null 时拉取会被**静默跳过**：不发请求、
            // 不记日志，用户只看到模型表永远空着——原因藏在两个字符里。这个陷阱真的发生过。
            {
                var pb = Fixture.AddProvider(cfg, stub.BaseUrl, name: "空串时间戳站点");
                pb.ModelsFetchedAt = "";

                using var r3 = http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                    $"http://127.0.0.1:{cfg.Port}/v1/models")
                {
                    Headers = { { "Authorization", "Bearer " + token } }
                }).GetAwaiter().GetResult();

                Harness.Eq("空串时间戳的站点：/v1/models 仍然 200", (int)r3.StatusCode, 200);
                Harness.True("空串被当成「从未拉过」，拉取真的发生了（而不是被静默跳过）",
                             !string.IsNullOrWhiteSpace(pb.ModelsFetchedAt));
                Harness.True("拉到的模型进了它自己的表", pb.FindModel("stub-model") is not null);
            }
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            stub.StopAsync().GetAwaiter().GetResult();
            TokenStore.FilePathOverride = oldTokenPath;
            ProxyConfig.FilePathOverride = oldCfgPath;
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
