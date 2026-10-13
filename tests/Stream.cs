using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 流式工具循环的控制流回归。**不需要网络、不需要词库。**
///
/// 这一层要回答的问题与非流式不同：非流式只要"最终那份 JSON 对不对"，
/// 而流式还要回答**什么时候发了什么** —— 正文必须实时出去（不能攒到轮末），
/// 我们自己的 tool_calls 一个字节都不许外泄，中间轮的 finish_reason / usage /
/// [DONE] 绝不能提前出现。这些错误的症状都很轻（客户端只是"答得怪"或
/// "答案重复一段"），在真机上极难定位，所以必须逐条钉住。
///
/// 假上游吐的是**真的 SSE 文本**，由 <see cref="Sse"/> 自己解析回来 ——
/// 这样分片拼接、空行边界这些容易错的地方也一并被测到。
/// </summary>
public static class StreamLoopCheck
{
    /// <summary>假上游：按剧本吐 SSE，并记下每次收到的请求体。</summary>
    private sealed class FakeStreamUpstream : IUpstream
    {
        private readonly Queue<string> _script = new();
        public List<JsonObject> Received { get; } = new();

        /// <summary>每次流式调用收到的请求体与吐出的内容。</summary>
        public void Enqueue(string sseText) => _script.Enqueue(sseText);

        public Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct)
            => throw new InvalidOperationException("流式回归里不该出现非流式调用。");

        public async Task<StreamStart> StreamAsync(JsonObject body, string clientToken,
                                                   Func<Stream, Task> onStream, CancellationToken ct)
        {
            Received.Add((JsonObject)body.DeepClone());
            if (_script.Count == 0)
                return StreamStart.Transport("假上游的剧本用完了");

            var text = _script.Dequeue();
            var bytes = Encoding.UTF8.GetBytes(text);

            // 故意拆成**奇数长度的小块**：多字节 UTF-8 字符与事件边界都会被切在
            // 块中间，逼着解析层按字节找空行、按字节拼接。一次给整段的话，
            // 那些边界处理永远测不到。
            await using var ms = new MemoryStream();
            for (var i = 0; i < bytes.Length; i += 7)
                ms.Write(bytes, i, Math.Min(7, bytes.Length - i));
            ms.Position = 0;

            await onStream(ms).ConfigureAwait(false);
            return new StreamStart(true, 200, "text/event-stream", "");
        }
    }

    /// <summary>记账用的假出口：把每个事件原样存下来，供断言"发了什么、第几个发的"。</summary>
    private sealed class RecordingSink : ISseSink
    {
        public List<string> Events { get; } = new();
        public bool Committed { get; private set; }
        public bool AutoCommit { get; set; } = true;

        public Task WriteAsync(string sseEvent, CancellationToken ct)
        {
            if (AutoCommit) Committed = true;
            Events.Add(sseEvent);
            return Task.CompletedTask;
        }

        /// <summary>把每个事件的 data 解析成 JSON，忽略 [DONE]。</summary>
        public List<JsonObject> Parsed()
        {
            var list = new List<JsonObject>();
            foreach (var e in Events)
            {
                var ev = Sse.Parse(e);
                if (ev.IsDone) continue;
                if (ChatWire.TryParseObject(ev.Data) is { } o) list.Add(o);
            }
            return list;
        }

        public bool SawDone => Events.Any(e => Sse.Parse(e).IsDone);

        /// <summary>把所有 chunk 的正文拼起来，还原客户端最终看到的那段话。</summary>
        public string AllContent()
        {
            var sb = new StringBuilder();
            foreach (var o in Parsed())
            {
                if (o["choices"] is not JsonArray cs) continue;
                foreach (var c in cs)
                    if (c?["delta"] is JsonObject d && d["content"] is not null)
                        sb.Append(ChatWire.GetString(d, "content"));
            }
            return sb.ToString();
        }

        /// <summary>最终 finish_reason（最后一个非 null 的）。</summary>
        public string FinishReason()
        {
            var fr = "";
            foreach (var o in Parsed())
            {
                if (o["choices"] is not JsonArray cs) continue;
                foreach (var c in cs)
                {
                    var v = ChatWire.GetString(c as JsonObject, "finish_reason");
                    if (v.Length > 0) fr = v;
                }
            }
            return fr;
        }

        /// <summary>客户端看到的全部 tool_calls（delta 里发出去的）。</summary>
        public List<(string Id, string Name, string Args)> ToolCalls()
        {
            var list = new List<(string, string, string)>();
            foreach (var o in Parsed())
            {
                if (o["choices"] is not JsonArray cs) continue;
                foreach (var c in cs)
                {
                    if (c?["delta"] is not JsonObject d) continue;
                    if (d["tool_calls"] is not JsonArray tcs) continue;
                    foreach (var tc in tcs)
                    {
                        if (tc is not JsonObject t) continue;
                        var fn = t["function"] as JsonObject;
                        list.Add((ChatWire.GetString(t, "id"),
                                  ChatWire.GetString(fn, "name"),
                                  ChatWire.GetString(fn, "arguments")));
                    }
                }
            }
            return list;
        }

        public bool AnyUsage()
            => Parsed().Any(o => o["usage"] is JsonObject);
    }

    private static string Chunk(string deltaJson, string finish = "null") =>
        "data: {\"id\":\"up-1\",\"object\":\"chat.completion.chunk\",\"model\":\"up-model\"," +
        "\"choices\":[{\"index\":0,\"delta\":" + deltaJson + ",\"finish_reason\":" + finish + "}]}\n\n";

    private static string TextChunk(string content, string finish = "null") =>
        Chunk("{\"content\":" + JsonN(content) + "}", finish);

    private static string RoleChunk() => Chunk("{\"role\":\"assistant\"}");

    /// <summary>把一段文本编成 JSON 字符串字面量（含引号）。</summary>
    private static string JsonN(string s) => ChatWire.ToJson(JsonValue.Create(s));

    /// <summary>把 tool_call 按"分片下发"的样子摊开——真实的流就是这样发的。</summary>
    private static string ToolCallChunks(int index, string id, string name, string args,
                                         bool splitArgs = true)
    {
        var sb = new StringBuilder();
        sb.Append(Chunk("{\"tool_calls\":[{\"index\":" + index + ",\"id\":" + JsonN(id) +
                        ",\"type\":\"function\",\"function\":{\"name\":" + JsonN(name) +
                        ",\"arguments\":\"\"}}]}"));
        if (!splitArgs)
        {
            sb.Append(Chunk("{\"tool_calls\":[{\"index\":" + index +
                            ",\"function\":{\"arguments\":" + JsonN(args) + "}}]}", "\"tool_calls\""));
            return sb.ToString();
        }

        // 把参数切成三片，模拟真实的增量下发。
        var third = Math.Max(1, args.Length / 3);
        for (var i = 0; i < args.Length; i += third)
        {
            var piece = args.Substring(i, Math.Min(third, args.Length - i));
            var last = i + third >= args.Length;
            sb.Append(Chunk("{\"tool_calls\":[{\"index\":" + index + ",\"function\":{\"arguments\":" +
                            JsonN(piece) + "}}]}", last ? "\"tool_calls\"" : "null"));
        }
        return sb.ToString();
    }

    private static string StreamAnswer(params string[] parts)
    {
        var sb = new StringBuilder();
        sb.Append(RoleChunk());
        foreach (var p in parts) sb.Append(TextChunk(p));
        sb.Append(TextChunk("", "\"stop\""));
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    /// <summary>带 usage 的收尾块。真实上游在最后一轮才会带它。</summary>
    private static string UsageChunk(int pt, int ct) =>
        "data: {\"id\":\"up-1\",\"object\":\"chat.completion.chunk\",\"model\":\"up-model\"," +
        "\"choices\":[],\"usage\":{\"prompt_tokens\":" + pt + ",\"completion_tokens\":" + ct +
        ",\"total_tokens\":" + (pt + ct) + "}}\n\n";

    private static JsonObject ClientBody(bool withClientTools = true, string content = "弓 这个标签叫什么？")
    {
        var body = new JsonObject
        {
            ["model"] = "client-model",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are a helpful agent." },
                new JsonObject { ["role"] = "user", ["content"] = content }
            },
            ["temperature"] = 0.7,
            ["max_tokens"] = 2048,
            ["reasoning_effort"] = "high",
            ["stream"] = true
        };

        if (withClientTools)
        {
            body["tools"] = new JsonArray
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
            };
        }
        return body;
    }

    /// <summary>造一个不碰词库的 ToolHost（工具会返回"不可用"，这也是要测的一条）。</summary>
    private static ToolHost Host()
    {
        var svc = new VocabService();
        var cfg = new ProxyConfig { VocabDbPath = Path.Combine(Path.GetTempPath(), "no-such-vocab.sqlite") };
        svc.Get(cfg);
        return new ToolHost(new VocabService());
    }

    private static (StreamLoop.Outcome Outcome, RecordingSink Sink, FakeStreamUpstream Up) Drive(
        string[] script, ProxyConfig? cfg = null, JsonObject? body = null)
    {
        var up = new FakeStreamUpstream();
        foreach (var s in script) up.Enqueue(s);
        var sink = new RecordingSink();
        var loop = new StreamLoop(up, Host(), cfg ?? new ProxyConfig());
        var outcome = loop.RunAsync(body ?? ClientBody(), "tok", sink, CancellationToken.None)
                          .GetAwaiter().GetResult();
        return (outcome, sink, up);
    }

    public static void Run()
    {
        Harness.Section("SSE 解析：按字节找事件边界，多字节字符被切开也不坏");

        {
            // 一个中文字符跨 chunk、一个事件跨 chunk：这是真实网络里必然出现的形状。
            var bytes = Encoding.UTF8.GetBytes(RoleChunk() + TextChunk("你好世界", "\"stop\"") + "data: [DONE]\n\n");
            using var ms = new MemoryStream();
            for (var i = 0; i < bytes.Length; i += 3) ms.Write(bytes, i, Math.Min(3, bytes.Length - i));
            ms.Position = 0;

            var events = new List<SseEvent>();
            Sse.ReadAsync(ms, e => { events.Add(e); return Task.CompletedTask; }, CancellationToken.None)
               .GetAwaiter().GetResult();

            Harness.Eq("三个事件都完整读出来（role / 正文 / DONE）", events.Count, 3);
            Harness.True("最后一个是 [DONE]", events.Count > 0 && events[^1].IsDone);
            Harness.True("被切成三字节一段的中文没坏",
                         events.Any(e => e.Data.Contains("你好世界", StringComparison.Ordinal)));
        }

        {
            // \r\n 空行的上游：只认 \n\n 的实现会一直等不到边界，表现是"流卡住"。
            var crlf = "data: {\"a\":1}\r\n\r\ndata: [DONE]\r\n\r\n";
            var events = new List<SseEvent>();
            Sse.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(crlf)),
                          e => { events.Add(e); return Task.CompletedTask; }, CancellationToken.None)
               .GetAwaiter().GetResult();
            Harness.Eq("CRLF 空行也认", events.Count, 2);
        }

        {
            // 心跳注释行必须被忽略，且不能把 data 吃掉。
            var withComment = ": keep-alive\n\ndata: {\"a\":1}\n\n";
            var events = new List<SseEvent>();
            Sse.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(withComment)),
                          e => { events.Add(e); return Task.CompletedTask; }, CancellationToken.None)
               .GetAwaiter().GetResult();
            Harness.Eq("注释心跳行不产生事件", events.Count, 1);
            Harness.Eq("注释后面的 data 正常取到", events[0].Data, "{\"a\":1}");
        }

        Harness.Section("流式：tool_call 分片拼装");

        {
            var up = new FakeStreamUpstream();
            // 参数被切成三片下发
            up.Enqueue(ToolCallChunks(0, "call_1", ToolCatalog.SearchName, "{\"query\":\"lace\"}") + "data: [DONE]\n\n");
            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            // 参数拼错了，工具就执行不了，第二轮请求里也就没有 role:tool 的结果。
            Harness.Eq("上游被问了两次（第一轮工具 + 第二轮收尾）", up.Received.Count, 2);
            var second = up.Received[1];
            var toolMsgs = ((JsonArray)second["messages"]!).Where(m =>
                ChatWire.GetString(m as JsonObject, "role") == "tool").ToList();
            Harness.True("分片的 arguments 被拼完整并执行（影子历史里有 role:tool）", toolMsgs.Count == 1);
            var echo = ((JsonArray)second["messages"]!).FirstOrDefault(m =>
                ChatWire.GetString(m as JsonObject, "role") == "assistant" &&
                (m as JsonObject)?["tool_calls"] is JsonArray);
            var echoedArgs = ChatWire.GetString(
                (echo as JsonObject)?["tool_calls"]![0]!["function"] as JsonObject, "arguments");
            Harness.Eq("拼出来的 arguments 逐字正确", echoedArgs, "{\"query\":\"lace\"}");
        }

        {
            // name 也被拆成两片：官方 SDK 是累加的，取值会得到残缺的工具名。
            var split = "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                        "\"id\":\"c1\",\"function\":{\"name\":\"danbooru_sea\",\"arguments\":\"\"}}]}}]}\n\n" +
                        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                        "\"function\":{\"name\":\"rch_tags\",\"arguments\":\"{\\\"query\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
                        "data: [DONE]\n\n";
            var up = new FakeStreamUpstream();
            up.Enqueue(split);
            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("名字分片相加后仍被认作我们的工具（所以触发了第二轮）",
                       up.Received.Count, 2);
        }

        Harness.Section("流式：正文实时转发、我们的调用不外泄");

        {
            var up = new FakeStreamUpstream();
            up.Enqueue(ToolCallChunks(0, "call_1", ToolCatalog.SearchName, "{\"query\":\"lace\"}"));
            up.Enqueue(StreamAnswer("蕾丝对应的标签是 ", "lace。"));

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig { MaxToolRounds = 4 });
            var outcome = loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None)
                              .GetAwaiter().GetResult();

            Harness.True("跑完了", outcome.Completed);
            Harness.Eq("客户端看到的正文是模型的最终回答，且两段拼起来",
                       sink.AllContent(), "蕾丝对应的标签是 lace。");
            Harness.Eq("我们自己的 tool_call 一个都没发给客户端", sink.ToolCalls().Count, 0);
            Harness.Eq("finish_reason 是 stop", sink.FinishReason(), "stop");
            Harness.True("发了 [DONE]", sink.SawDone);

            // 中间轮绝不能把 finish_reason=tool_calls 漏出去
            var leaked = sink.Parsed().Any(o =>
                (o["choices"] as JsonArray)?.Any(c =>
                    ChatWire.GetString(c as JsonObject, "finish_reason") is "tool_calls") == true);
            Harness.True("中间轮的 finish_reason=tool_calls 没有外泄", !leaked);
        }

        Harness.Section("流式：首块必须在整段回答之前就出去（不是攒到最后）");

        {
            var up = new FakeStreamUpstream();
            // 第一轮：正文 + 我们的工具调用（带 usage）
            up.Enqueue(TextChunk("先查一下。") +
                       ToolCallChunks(0, "c1", ToolCatalog.SearchName, "{\"query\":\"lace\"}") +
                       UsageChunk(20, 7));
            // 第二轮：最终回答（带 usage）
            up.Enqueue(RoleChunk() + TextChunk("查到了：lace。") + TextChunk("", "\"stop\"") +
                       UsageChunk(30, 9) + "data: [DONE]\n\n");

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            // 第一个事件的正文必须是第一轮的，且它出现在**[DONE] 之前很多个事件**——
            // 若实现是"整条读完再合成一条假流"，两轮的正文会被攒在一起发。
            var parsed = sink.Parsed();
            var firstContent = parsed.Select(o =>
                    (o["choices"] as JsonArray)?.Select(c => ChatWire.GetString(c?["delta"] as JsonObject, "content"))
                        .FirstOrDefault(s => !string.IsNullOrEmpty(s)))
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));

            Harness.Eq("第一个正文块就是第一轮的正文（说明它当场就发出去了）",
                       firstContent, "先查一下。");
            Harness.Eq("客户端最终看到两轮正文拼接", sink.AllContent(), "先查一下。查到了：lace。");

            // usage 只能在最后出现一次，且必须是**累加**过的。
            // 每轮都转发一次是错的：客户端会在还没结束时就拿到一份"最终用量"。
            var usageEvents = parsed.Where(o => o["usage"] is JsonObject).ToList();
            Harness.Eq("usage 只出现一次（不是每轮一次）", usageEvents.Count, 1);
            if (usageEvents.Count == 1)
            {
                var u = usageEvents[0]["usage"] as JsonObject;
                Harness.Eq("prompt_tokens 跨轮累加（20+30）", ChatWire.GetInt(u, "prompt_tokens"), 50);
                Harness.Eq("completion_tokens 跨轮累加（7+9）", ChatWire.GetInt(u, "completion_tokens"), 16);
                Harness.Eq("total_tokens 跨轮累加（27+39）", ChatWire.GetInt(u, "total_tokens"), 66);
            }

            // 客户端绝不能看到中间轮"我已经查过了"这种内部记账文字
            Harness.True("正文里没有代理的内部记账文字",
                         !sink.AllContent().Contains("already performed by the proxy",
                                                     StringComparison.Ordinal));
        }

        Harness.Section("流式：客户端的工具原样交还");

        {
            var up = new FakeStreamUpstream();
            up.Enqueue(ToolCallChunks(0, "call_theirs", "read_file", "{\"path\":\"/tmp/a\"}"));

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            var outcome = loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None)
                              .GetAwaiter().GetResult();

            Harness.True("跑完了", outcome.Completed);
            Harness.Eq("只问了一次上游（没有我们的调用，不循环）", up.Received.Count, 1);

            var calls = sink.ToolCalls();
            Harness.Eq("客户端的 tool_call 被交还", calls.Count, 1);
            if (calls.Count == 1)
            {
                Harness.Eq("交还的是它那个", calls[0].Name, "read_file");
                Harness.Eq("id 一字不差", calls[0].Id, "call_theirs");
                Harness.Eq("arguments 拼完整", calls[0].Args, "{\"path\":\"/tmp/a\"}");
            }
            Harness.Eq("finish_reason 保持 tool_calls", sink.FinishReason(), "tool_calls");
        }

        Harness.Section("流式：混合（我们的 + 客户端的）");

        {
            var up = new FakeStreamUpstream();
            // 第一轮：同时调我们的和客户端的
            up.Enqueue(ToolCallChunks(0, "call_mine", ToolCatalog.SearchName, "{\"query\":\"lace\"}") +
                       ToolCallChunks(1, "call_theirs", "read_file", "{\"path\":\"/tmp/b\"}"));
            // 第二轮：再提一次客户端的调用（模型在知道我们查过之后仍要它的结果）
            up.Enqueue(ToolCallChunks(0, "call_theirs2", "read_file", "{\"path\":\"/tmp/b\"}"));

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig { MaxToolRounds = 4 });
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            var calls = sink.ToolCalls();
            Harness.Eq("交还客户端的调用恰好一个（第二轮那个）", calls.Count, 1);
            if (calls.Count == 1)
                Harness.Eq("是我们摘掉自己的之后剩下的", calls[0].Name, "read_file");
            Harness.True("我们自己的调用没交给客户端",
                         !calls.Any(c => c.Name.StartsWith("danbooru_", StringComparison.Ordinal)));
        }

        Harness.Section("流式：轮数用尽 → 不带我们的工具收尾");

        {
            var up = new FakeStreamUpstream();
            up.Enqueue(ToolCallChunks(0, "c1", ToolCatalog.SearchName, "{\"query\":\"lace\"}"));
            up.Enqueue(ToolCallChunks(0, "c2", ToolCatalog.SearchName, "{\"query\":\"dress\"}"));
            up.Enqueue(StreamAnswer("根据已查到的结果……"));

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig { MaxToolRounds = 2 });
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("跑满轮数后多问了一次收尾", up.Received.Count, 3);

            var wrapNames = (up.Received[2]["tools"] as JsonArray)?
                .Select(t => ChatWire.GetString(t!["function"] as JsonObject, "name"))
                .ToList() ?? new List<string>();

            Harness.True("收尾轮不再下发我们自己的工具",
                         !wrapNames.Any(n => n.StartsWith("danbooru_", StringComparison.Ordinal)));
            Harness.True("收尾轮客户端的工具原样保留", wrapNames.Contains("read_file"));
            Harness.Eq("最终给客户端的是纯回答", sink.AllContent(), "根据已查到的结果……");
            Harness.Eq("一个 tool_call 都没外泄", sink.ToolCalls().Count, 0);
        }

        Harness.Section("流式：上游在吐字节之前失败 → 如实回状态码");

        {
            var up = new FakeStreamUpstream();   // 剧本为空 → Transport 失败
            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            var outcome = loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None)
                              .GetAwaiter().GetResult();

            Harness.Eq("没写完（状态码还能改）", outcome.Completed, false);
            Harness.Eq("状态码是 502（连不上上游）", outcome.Status, 502);
            Harness.Eq("一个字节都没发出去", sink.Events.Count, 0);
        }

        {
            // 上游返回 429：必须以**429** 收场，而不是 200 + 空流。
            var sink = new RecordingSink();
            var rejecting = new RejectingUpstream(429, "rate limited");
            var outcome = new StreamLoop(rejecting, Host(), new ProxyConfig())
                .RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("上游 429 时状态码照抄", outcome.Status, 429);
            Harness.Eq("没写出任何字节", sink.Events.Count, 0);
        }

        Harness.Section("流式：流内的 error 事件");

        {
            // 情况一：**还没吐过任何字节**。这时仍然能如实回状态码，比往流里塞
            // 一个 error 事件好得多（客户端能走它正常的错误处理路径）。
            var up = new FakeStreamUpstream();
            up.Enqueue(ToolCallChunks(0, "c1", ToolCatalog.SearchName, "{\"query\":\"lace\"}"));
            up.Enqueue("data: {\"error\":{\"message\":\"上游中途出错\",\"code\":429}}\n\n");

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            var outcome = loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None)
                              .GetAwaiter().GetResult();

            Harness.Eq("没吐过字节 → 回真正的状态码（不是 200）", outcome.Completed, false);
            Harness.Eq("错误体里带了 429 就用它", outcome.Status, 429);
            Harness.Eq("什么都没写进流", sink.Events.Count, 0);
        }

        {
            // 情况二：**已经吐过正文**。HTTP 状态行早就发出去了，改不了，
            // 只能往流里补一个 error 事件 + [DONE]，否则客户端会一直等。
            var up = new FakeStreamUpstream();
            up.Enqueue(TextChunk("先查一下。") +
                       ToolCallChunks(0, "c1", ToolCatalog.SearchName, "{\"query\":\"lace\"}"));
            up.Enqueue("data: {\"error\":{\"message\":\"上游中途出错\"}}\n\n");

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            var outcome = loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None)
                              .GetAwaiter().GetResult();

            Harness.True("以完成告终（状态码已经发出去了，改不了）", outcome.Completed);
            Harness.True("流里补了一个 error 事件",
                         sink.Parsed().Any(o => o["error"] is JsonObject));
            Harness.True("并且补了 [DONE]（否则客户端会一直等）", sink.SawDone);
            Harness.Eq("已经转出去的那段正文还在", sink.AllContent(), "先查一下。");

            // code 不是数字时不能拿它当状态码：Kestrel 到写响应头时才抛，那时
            // 已经没有任何补救余地。这条走的是 502 兜底。
            var up2 = new FakeStreamUpstream();
            up2.Enqueue(TextChunk("x"));
            up2.Enqueue("data: {\"error\":{\"message\":\"坏\",\"code\":\"invalid_request_error\"}}\n\n");
            var sink2 = new RecordingSink();
            var outcome2 = new StreamLoop(up2, Host(), new ProxyConfig())
                .RunAsync(ClientBody(), "tok", sink2, CancellationToken.None).GetAwaiter().GetResult();
            Harness.True("字符串 code 不会被当成 HTTP 状态码", outcome2.Completed);
            Harness.True("仍然补了 [DONE]", sink2.SawDone);
        }

        Harness.Section("流式：思考字段与未知字段一律不劫持");

        {
            var up = new FakeStreamUpstream();
            up.Enqueue(StreamAnswer("答案"));

            var body = ClientBody();
            body["enable_thinking"] = true;
            body["top_p"] = 0.9;
            body["some_future_field"] = new JsonObject { ["nested"] = true };
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(body, "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            var sent = up.Received[0];
            Harness.Eq("reasoning_effort 原样透传", ChatWire.GetString(sent, "reasoning_effort"), "high");
            Harness.Eq("enable_thinking 原样透传", ChatWire.GetBool(sent, "enable_thinking"), true);
            Harness.Eq("top_p 原样透传", sent["top_p"]!.GetValue<double>(), 0.9);
            Harness.Eq("chat_template_kwargs 原样",
                       ChatWire.GetBool(sent["chat_template_kwargs"] as JsonObject, "enable_thinking"), true);
            Harness.True("不认识的未来字段也带过去",
                         sent["some_future_field"] is JsonObject sf && ChatWire.GetBool(sf, "nested") == true);
            Harness.Eq("流式路径确实按流式问上游", ChatWire.GetBool(sent, "stream"), true);
        }

        {
            // 流式路径上的思考增量也要转给客户端（那是它和上游之间的事）。
            var up = new FakeStreamUpstream();
            up.Enqueue("data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                       "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"我思考\"}}]}\n\n" +
                       "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"答案\"}}]}\n\n" +
                       "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                       "data: [DONE]\n\n");

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            Harness.True("reasoning_content 增量原样转给客户端",
                         sink.Parsed().Any(o =>
                             (o["choices"] as JsonArray)?.Any(c =>
                                 (c?["delta"] as JsonObject)?["reasoning_content"] is not null) == true));
        }

        Harness.Section("流式：模型名与信封字段");

        {
            var up = new FakeStreamUpstream();
            up.Enqueue(StreamAnswer("答案"));

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(ClientBody(), "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            var chunks = sink.Parsed();
            Harness.True("所有 chunk 的模型名都是客户端要的那个（上游报的是 up-model）",
                         chunks.All(o => ChatWire.GetString(o, "model") == "client-model"));
            Harness.True("所有 chunk 的 object 都是 chat.completion.chunk",
                         chunks.All(o => ChatWire.GetString(o, "object") == "chat.completion.chunk"));
            Harness.True("所有 chunk 共用同一个 id",
                         chunks.Select(o => ChatWire.GetString(o, "id")).Distinct().Count() == 1);
            Harness.True("第一条 delta 里有 role=assistant",
                         ChatWire.GetString(((chunks[0]["choices"] as JsonArray)![0] as JsonObject)?["delta"] as JsonObject,
                                            "role") == "assistant");
        }

        Harness.Section("流式：工具重名让路后仍认得自己的调用");

        {
            var up = new FakeStreamUpstream();
            // 走让路后的名字（客户端占用了原名）
            up.Enqueue(ToolCallChunks(0, "c1", ToolCatalog.SearchName + ToolCatalog.CollisionSuffix,
                                      "{\"query\":\"lace\"}"));
            up.Enqueue(StreamAnswer("答案"));

            var body = ClientBody();
            ((JsonArray)body["tools"]!).Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = ToolCatalog.SearchName }
            });

            var sink = new RecordingSink();
            var loop = new StreamLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(body, "tok", sink, CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("让路后的调用仍被执行（触发了第二轮）", up.Received.Count, 2);
            Harness.Eq("并且没有外泄给客户端", sink.ToolCalls().Count, 0);
        }
    }

    /// <summary>模拟"上游直接回一个非 2xx"上游。</summary>
    private sealed class RejectingUpstream : IUpstream
    {
        private readonly int _status;
        private readonly string _message;

        public RejectingUpstream(int status, string message)
        {
            _status = status;
            _message = message;
        }

        public Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct)
            => throw new InvalidOperationException("流式回归里不该出现非流式调用。");

        public Task<StreamStart> StreamAsync(JsonObject body, string clientToken,
                                             Func<Stream, Task> onStream, CancellationToken ct)
            => Task.FromResult(new StreamStart(false, _status, "application/json",
                                               $"HTTP {_status}：{_message}"));
    }
}
