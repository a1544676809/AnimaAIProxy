using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 透明工具循环的控制流回归。**不需要网络、不需要词库。**
///
/// 测的是这个程序真正的逻辑：一轮里同时有我们的调用和客户端的调用时交还什么、
/// 全是客户端的调用时做什么、轮数用完怎么办、思考字段有没有被我们改过。
/// 用一个假上游按剧本回话，逐条断言客户端最终看到的那份 JSON。
///
/// 这一节里每条断言都对应一个"上线之后才会发现的坏法"：
/// 客户端的工具被吃掉 → 它的 agent 卡死；usage 没累加 → 账单对不上；
/// 思考字段被改 → 用户设的档位静默失效。
/// </summary>
public static class Loop
{
    /// <summary>
    /// 假上游：按顺序把预设的响应发回去，并记下每次收到的请求体，
    /// 供断言"我们到底往上游发了什么"。
    /// </summary>
    private sealed class FakeUpstream : IUpstream
    {
        private readonly Queue<JsonObject> _script = new();
        public List<JsonObject> Received { get; } = new();

        public void Enqueue(JsonObject response) => _script.Enqueue(response);
        public int Calls => Received.Count;

        public Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct)
        {
            Received.Add((JsonObject)body.DeepClone());
            if (_script.Count == 0)
                return Task.FromResult(UpstreamResult.Transport("假上游的剧本用完了"));

            var resp = _script.Dequeue();
            return Task.FromResult(new UpstreamResult(true, 200, resp.ToJsonString(), resp, ""));
        }
    }

    /// <summary>造一条上游的普通回答。</summary>
    private static JsonObject Reply(string content, string model = "up-model") => new()
    {
        ["id"] = "chatcmpl-1",
        ["object"] = "chat.completion",
        ["model"] = model,
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }
            }
        },
        ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 }
    };

    /// <summary>造一条"模型要求调用工具"的回答。</summary>
    private static JsonObject ToolReply(params (string Id, string Name, string Args)[] calls)
        => ToolReplyWith(null, calls);

    private static JsonObject ToolReplyWith(string? content, params (string Id, string Name, string Args)[] calls)
    {
        var arr = new JsonArray();
        foreach (var (id, name, args) in calls)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
            });
        }

        var msg = new JsonObject { ["role"] = "assistant", ["content"] = content };
        msg["tool_calls"] = arr;

        return new JsonObject
        {
            ["id"] = "chatcmpl-2",
            ["object"] = "chat.completion",
            ["model"] = "up-model",
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["finish_reason"] = "tool_calls", ["message"] = msg }
            },
            ["usage"] = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 7, ["total_tokens"] = 27 }
        };
    }

    private static JsonObject ClientBody(string content = "介绍一下蕾丝这个标签", bool withTools = true)
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
            ["max_tokens"] = 2048
        };

        if (withTools)
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

    /// <summary>造一个不碰词库的 ToolHost（词库缺失时工具会返回"不可用"，正好也是要测的一条）。</summary>
    private static ToolHost Host()
    {
        var svc = new VocabService();
        var cfg = new ProxyConfig { VocabDbPath = Path.Combine(Path.GetTempPath(), "no-such-vocab.sqlite") };
        // 让载入立刻失败并给出"不存在"的说明，避免它真去读盘等 30 秒的重试间隔。
        svc.Get(cfg);
        return new ToolHost(new VocabService());
    }

    public static void Run()
    {
        Harness.Section("工具循环：客户端工具一个不许动");

        // ---------- 1. 模型只调客户端的工具 → 原样交还 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(ToolReply(("call_1", "read_file", "{\"path\":\"/tmp/a\"}")));
            var loop = new ToolLoop(up, Host(), new ProxyConfig());
            var body = ClientBody();

            var result = loop.RunAsync(body, "tok", CancellationToken.None).GetAwaiter().GetResult();
            var parsed = ChatWire.Parse(result.Response)!;

            Harness.Eq("只调客户端工具时，上游只被问了一次", up.Calls, 1);
            Harness.Eq("客户端的 tool_call 原样保留", parsed.ToolCalls.Count, 1);
            Harness.Eq("保留的正是客户端那个", parsed.ToolCalls[0].Name, "read_file");
            Harness.Eq("finish_reason 保持 tool_calls",
                       ChatWire.GetString((JsonObject)result.Response["choices"]![0]!, "finish_reason"),
                       "tool_calls");
            Harness.Eq("没有往 content 里塞我们的东西",
                       parsed.Content ?? "", "");
        }

        // ---------- 2. 模型只调我们的工具 → 本地执行、再问一轮、最终摘干净 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(ToolReply(("call_1", ToolCatalog.SearchName, "{\"query\":\"lace\"}")));
            up.Enqueue(Reply("蕾丝对应的标签是 lace。"));

            var cfg = new ProxyConfig { MaxToolRounds = 4 };
            var loop = new ToolLoop(up, Host(), cfg);

            var result = loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();
            var parsed = ChatWire.Parse(result.Response)!;

            Harness.Eq("我们的工具触发了第二轮上游调用", up.Calls, 2);
            Harness.Eq("最终响应里没有任何 tool_call（我们的被摘干净了）", parsed.ToolCalls.Count, 0);
            Harness.Eq("最终内容是模型的回答", parsed.Content ?? "", "蕾丝对应的标签是 lace。");
            Harness.Eq("finish_reason 改回了 stop",
                       ChatWire.GetString((JsonObject)result.Response["choices"]![0]!, "finish_reason"),
                       "stop");
            Harness.True("日志里记了工具调用", result.Log.Any(l => l.Contains("查「lace」")));

            // 第二轮请求里必须带上我们的工具结果，否则模型没有信息可用
            var second = up.Received[1];
            var msgs = (JsonArray)second["messages"]!;
            Harness.True("影子历史里带上了 role:tool 的结果",
                         msgs.Any(m => ChatWire.GetString(m as JsonObject, "role") == "tool"));
            Harness.Eq("模型名换回了客户端要的那个", ChatWire.GetString(second, "model"), "client-model");
        }

        // ---------- 3. 混合：我们的执行掉，客户端的原样交还 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(ToolReply(
                ("call_mine", ToolCatalog.SearchName, "{\"query\":\"lace\"}"),
                ("call_theirs", "read_file", "{\"path\":\"/tmp/b\"}")));
            up.Enqueue(ToolReplyWith(null, ("call_theirs", "read_file", "{\"path\":\"/tmp/b\"}")));

            var loop = new ToolLoop(up, Host(), new ProxyConfig { MaxToolRounds = 4 });
            var result = loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();
            var parsed = ChatWire.Parse(result.Response)!;

            Harness.Eq("混合场景下上游被问了两次", up.Calls, 2);
            Harness.Eq("交还客户端的工具调用恰好一个", parsed.ToolCalls.Count, 1);
            Harness.Eq("交还的是客户端那个而不是我们的", parsed.ToolCalls[0].Name, "read_file");
            Harness.Eq("它的 id 一字不差", parsed.ToolCalls[0].Id, "call_theirs");
            Harness.Eq("finish_reason 仍是 tool_calls（客户端还有活要干）",
                       ChatWire.GetString((JsonObject)result.Response["choices"]![0]!, "finish_reason"),
                       "tool_calls");

            // CarryFindings 的**正向**验证：这一轮客户端还有活要干、一定会带着自己的
            // 历史再回来，所以必须把已查到的结果写进 content，否则模型下一轮看不见
            // 我们查过什么，只好重查。上面"最终内容是模型的回答"那条钉的是它的反面。
            Harness.True("混合场景下把已查到的结果写回 content（客户端会带着历史再回来）",
                         (parsed.Content ?? "").Contains("already performed by the proxy",
                                                         StringComparison.Ordinal));
        }

        // ---------- 4. 我们的工具名被客户端占用了 → 让路 + 仍能识别 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(Reply("好。"));

            var body = ClientBody();
            // 客户端自己就用掉了 danbooru_search_tags 这个名字
            ((JsonArray)body["tools"]!).Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = ToolCatalog.SearchName, ["description"] = "客户端的" }
            });

            var loop = new ToolLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(body, "tok", CancellationToken.None).GetAwaiter().GetResult();

            var sent = up.Received[0];
            var names = ((JsonArray)sent["tools"]!).Select(t => ChatWire.GetString(t!["function"] as JsonObject, "name")).ToList();

            Harness.True("客户端原名的工具还在", names.Contains(ToolCatalog.SearchName));
            Harness.True("我们让路改用了带后缀的名字",
                         names.Contains(ToolCatalog.SearchName + ToolCatalog.CollisionSuffix));
            Harness.Eq("工具总数 = 客户端 2 + 我们 3", names.Count, 5);
        }

        // ---------- 5. 思考相关字段一律不劫持 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(Reply("答案"));

            var body = ClientBody();
            body["reasoning_effort"] = "high";
            body["enable_thinking"] = true;
            body["temperature"] = 0.42;
            body["top_p"] = 0.9;
            body["presence_penalty"] = 1.5;
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
            body["user"] = "someone";
            body["some_future_field"] = new JsonObject { ["nested"] = true };
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = true };

            var loop = new ToolLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(body, "tok", CancellationToken.None).GetAwaiter().GetResult();

            var sent = up.Received[0];
            Harness.Eq("reasoning_effort 原样透传", ChatWire.GetString(sent, "reasoning_effort"), "high");
            Harness.Eq("enable_thinking 原样透传", ChatWire.GetBool(sent, "enable_thinking"), true);
            Harness.Eq("temperature 原样透传", sent["temperature"]!.GetValue<double>(), 0.42);
            Harness.Eq("top_p 原样透传", sent["top_p"]!.GetValue<double>(), 0.9);
            Harness.Eq("presence_penalty 原样透传", sent["presence_penalty"]!.GetValue<double>(), 1.5);
            Harness.Eq("response_format 原样透传",
                       ChatWire.GetString(sent["response_format"] as JsonObject, "type"), "json_object");
            Harness.Eq("user 原样透传", ChatWire.GetString(sent, "user"), "someone");
            Harness.Eq("chat_template_kwargs.enable_thinking 原样",
                       ChatWire.GetBool(sent["chat_template_kwargs"] as JsonObject, "enable_thinking"), true);
            Harness.True("不认识的未来字段也原样带过去",
                         sent["some_future_field"] is JsonObject sf && ChatWire.GetBool(sf, "nested") == true);
            Harness.Eq("max_tokens 原样透传", ChatWire.GetInt(sent, "max_tokens"), 2048);
        }

        // ---------- 6. usage 累加：我们替它多花的几轮也要算 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(ToolReply(("call_1", ToolCatalog.SearchName, "{\"query\":\"lace\"}")));
            up.Enqueue(Reply("答案"));

            var loop = new ToolLoop(up, Host(), new ProxyConfig { MaxToolRounds = 4 });
            var result = loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();

            var usage = result.Response["usage"] as JsonObject;
            Harness.Eq("prompt_tokens 是两轮之和（10+20）", ChatWire.GetInt(usage, "prompt_tokens"), 30);
            Harness.Eq("completion_tokens 是两轮之和（5+7）", ChatWire.GetInt(usage, "completion_tokens"), 12);
            Harness.Eq("total_tokens 是两轮之和（15+27）", ChatWire.GetInt(usage, "total_tokens"), 42);
        }

        // ---------- 7. 轮数用完 → 不带工具收尾 ----------
        {
            var up = new FakeUpstream();
            // 剧本：连续两轮都在调我们的工具（MaxToolRounds=2），第三轮是收尾
            up.Enqueue(ToolReply(("c1", ToolCatalog.SearchName, "{\"query\":\"lace\"}")));
            up.Enqueue(ToolReply(("c2", ToolCatalog.SearchName, "{\"query\":\"dress\"}")));
            up.Enqueue(Reply("根据已查到的结果，可以这样说……"));

            var loop = new ToolLoop(up, Host(), new ProxyConfig { MaxToolRounds = 2 });
            var result = loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("跑满轮数后多问了一次收尾", up.Calls, 3);

            // 收尾那一轮只关**我们自己的**工具，客户端的原样留着。
            //
            // 这条曾经写成"一个工具都不下发"，与代码里的论证相反。定夺的结果是
            // 代码对、断言错：客户端的工具是接入方的能力，我们没有资格因为自己的
            // 轮数用完就把它一起关掉——那会让客户端在"模型本该调它工具"的时刻收到
            // 一个纯文本回答，它的 agent 就断在这一步。而收尾提示里那句
            // "You have no more tool calls available" 说的是**我们**的工具没了，
            // 一个都不发会让这句话变成谎话（模型看得见工具列表）。
            var wrapNames = (up.Received[2]["tools"] as JsonArray)?
                .Select(t => ChatWire.GetString(t!["function"] as JsonObject, "name"))
                .ToList() ?? new List<string>();

            Harness.True("收尾那一轮不再下发我们自己的工具（再带一次它多半还会继续调）",
                         !wrapNames.Any(n => n.StartsWith("danbooru_", StringComparison.Ordinal)));
            Harness.True("收尾那一轮客户端的工具原样保留（那是接入方的能力，不是我们的）",
                         wrapNames.Contains("read_file"));
            Harness.True("收尾提示里带上了已查到的摘要",
                         up.Received[2]["messages"]!.AsArray().Any(m =>
                             (ChatWire.GetString(m as JsonObject, "content")).Contains("lace")));
            Harness.Eq("最终给客户端的仍然是纯回答",
                       ChatWire.Parse(result.Response)!.ToolCalls.Count, 0);
        }

        // ---------- 8. 上游失败 → 如实回报，不重试 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = "模型名不存在", ["type"] = "invalid_request_error" }
            });
            // 让假上游把它当成功响应发回来也没关系——我们要测的是"连不上/结构不对"那条。
            var up2 = new FakeUpstream();   // 剧本为空 → 返回 Transport 失败

            var loop = new ToolLoop(up2, Host(), new ProxyConfig());
            var result = loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("上游失败时只问了一次（不重试）", up2.Calls, 1);
            Harness.True("返回体里有 error 对象", result.Response["error"] is JsonObject);
            Harness.True("日志里记了失败", result.Log.Any(l => l.Contains("上游失败")));
        }

        // ---------- 9. 系统提示里会介绍我们的工具，但不覆盖客户端原有的 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(Reply("好。"));

            var loop = new ToolLoop(up, Host(), new ProxyConfig { AnnounceTools = true });
            loop.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();

            var msgs = (JsonArray)up.Received[0]["messages"]!;
            var sys = ChatWire.GetString(msgs[0] as JsonObject, "content");
            Harness.True("客户端原有的系统提示还在（追加而不是替换）",
                         sys.Contains("You are a helpful agent."));
            Harness.True("我们的工具说明被追加进去了", sys.Contains(ToolCatalog.SearchName));
            Harness.Eq("仍然只有一条 system 消息",
                       msgs.Count(m => ChatWire.GetString(m as JsonObject, "role") == "system"), 1);

            var up3 = new FakeUpstream();
            up3.Enqueue(Reply("好。"));
            var loop3 = new ToolLoop(up3, Host(), new ProxyConfig { AnnounceTools = false });
            loop3.RunAsync(ClientBody(), "tok", CancellationToken.None).GetAwaiter().GetResult();
            var sys3 = ChatWire.GetString(((JsonArray)up3.Received[0]["messages"]!)[0] as JsonObject, "content");
            Harness.True("关掉 AnnounceTools 后不再追加说明", !sys3.Contains(ToolCatalog.SearchName));
        }

        // ---------- 10. 客户端把 stream 设成 true 时，注入路径不该被它带跑 ----------
        {
            var up = new FakeUpstream();
            up.Enqueue(Reply("答案"));

            var body = ClientBody();
            body["stream"] = true;   // 注入路径永远按非流式问上游

            var loop = new ToolLoop(up, Host(), new ProxyConfig());
            loop.RunAsync(body, "tok", CancellationToken.None).GetAwaiter().GetResult();

            Harness.Eq("注入路径强制按非流式问上游", ChatWire.GetBool(up.Received[0], "stream"), false);
        }
    }
}
