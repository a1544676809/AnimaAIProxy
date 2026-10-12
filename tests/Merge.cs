using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 线上的解析与拼装回归：<see cref="ChatWire"/>。
///
/// 这一层是代理里最容易出静默错误的地方——字段名拼错、null 没判、
/// 把 reasoning 当 content。而症状在真机上只是"模型答得怪"，看不出是代理的锅。
/// 所以每条都对着**真实会出现的报文形状**断言。
/// </summary>
public static class Merge
{
    private static JsonObject ChatResponse(string content, string? reasoning = null,
                                           string finish = "stop", JsonArray? toolCalls = null)
    {
        var msg = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (reasoning is not null) msg["reasoning_content"] = reasoning;
        if (toolCalls is not null) msg["tool_calls"] = toolCalls;

        return new JsonObject
        {
            ["id"] = "x",
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["finish_reason"] = finish, ["message"] = msg }
            }
        };
    }

    private static JsonArray Calls(params (string Id, string Name, string Args)[] calls)
    {
        var arr = new JsonArray();
        foreach (var (id, name, args) in calls)
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
            });
        return arr;
    }

    public static void Run()
    {
        Harness.Section("响应解析");

        var p = ChatWire.Parse(ChatResponse("你好", "我先想一想"))!;
        Harness.Eq("content 取到", p.Content, "你好");
        Harness.Eq("reasoning_content 单独取到（不与 content 混）", p.Reasoning, "我先想一想");
        Harness.Eq("finish_reason 取到", p.FinishReason, "stop");
        Harness.Eq("没有工具调用", p.HasToolCalls, false);

        // 有的服务用 reasoning 而不是 reasoning_content
        var r2 = ChatWire.Parse(ChatResponse("a", null) is { } _ ? new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "a",
                        ["reasoning"] = "另一种字段名"
                    }
                }
            }
        } : new JsonObject())!;
        Harness.Eq("reasoning 这个字段名也认", r2.Reasoning, "另一种字段名");

        var p3 = ChatWire.Parse(ChatResponse("x", null, "tool_calls",
            Calls(("c1", "read_file", "{\"p\":1}"))))!;
        Harness.Eq("工具调用解析出来", p3.ToolCalls.Count, 1);
        Harness.Eq("名字对", p3.ToolCalls[0].Name, "read_file");
        Harness.Eq("id 对", p3.ToolCalls[0].Id, "c1");
        Harness.Eq("arguments 保持原始字符串（不在这里 parse）",
                   p3.ToolCalls[0].Arguments, "{\"p\":1}");

        Harness.True("没有 choices → null，不猜",
                     ChatWire.Parse(new JsonObject { ["id"] = "x" }) is null);
        Harness.True("choices 为空 → null",
                     ChatWire.Parse(new JsonObject { ["choices"] = new JsonArray() }) is null);
        Harness.True("message 缺失 → null",
                     ChatWire.Parse(new JsonObject
                     {
                         ["choices"] = new JsonArray { new JsonObject { ["index"] = 0 } }
                     }) is null);
        Harness.True("content 是 null 也不炸",
                     ChatWire.Parse(ChatResponse(null!)) is not null);

        Harness.Section("摘除自有工具调用、保留客户端调用");

        // 全是我们自己的 → tool_calls 整体摘掉，finish_reason 改回 stop
        var onlyMine = ChatResponse(null!, null, "tool_calls", Calls(("m1", "danbooru_search_tags", "{}")));
        ChatWire.KeepOnlyToolCalls(onlyMine, Array.Empty<string>());
        var m1 = (JsonObject)onlyMine["choices"]![0]!;
        Harness.True("tool_calls 被摘掉", m1["message"]!.AsObject()["tool_calls"] is null);
        Harness.Eq("finish_reason 改回 stop（否则客户端会等一个不存在的工具）",
                   ChatWire.GetString(m1, "finish_reason"), "stop");

        // 只留客户端那个
        var mixed = ChatResponse(null!, null, "tool_calls",
            Calls(("m1", "danbooru_search_tags", "{}"), ("k1", "read_file", "{}")));
        ChatWire.KeepOnlyToolCalls(mixed, new[] { "k1" });
        var m2 = (JsonObject)mixed["choices"]![0]!;
        var kept = m2["message"]!.AsObject()["tool_calls"]!.AsArray();
        Harness.Eq("只留下客户端那一个", kept.Count, 1);
        Harness.Eq("留下的正是客户端的", ChatWire.GetString(kept[0]!["function"] as JsonObject, "name"), "read_file");
        Harness.Eq("finish_reason 保持 tool_calls", ChatWire.GetString(m2, "finish_reason"), "tool_calls");

        // id 缺失的调用一律保留
        var noId = ChatResponse(null!, null, "tool_calls", new JsonArray
        {
            new JsonObject { ["function"] = new JsonObject { ["name"] = "mystery", ["arguments"] = "{}" } }
        });
        ChatWire.KeepOnlyToolCalls(noId, Array.Empty<string>());
        Harness.Eq("id 缺失的调用宁可多留也不吃掉",
                   ((JsonObject)noId["choices"]![0]!)["message"]!.AsObject()["tool_calls"]!.AsArray().Count, 1);

        Harness.Section("往 content 里追加（两种 content 形状都要能追加）");

        var s = new JsonObject { ["role"] = "assistant", ["content"] = "原文" };
        ChatWire.AppendToContent(s, "追加的");
        Harness.Eq("字符串 content 追加成两段", ChatWire.GetString(s, "content"), "原文\n\n追加的");

        var empty = new JsonObject { ["role"] = "assistant", ["content"] = "" };
        ChatWire.AppendToContent(empty, "只有追加");
        Harness.Eq("空 content 直接放追加内容", ChatWire.GetString(empty, "content"), "只有追加");

        var none = new JsonObject { ["role"] = "assistant" };
        ChatWire.AppendToContent(none, "新建");
        Harness.Eq("content 缺失时新建", ChatWire.GetString(none, "content"), "新建");

        // 多模态数组形状：带图对话里 content 是数组，只处理字符串会让追加静默失效
        var arr = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "看图说话" } }
        };
        ChatWire.AppendToContent(arr, "数组追加");
        Harness.Eq("数组 content 追加成了第二个 text 块", arr["content"]!.AsArray().Count, 2);
        Harness.Eq("原有的块没被动",
                   ChatWire.GetString(arr["content"]!.AsArray()[0]!.AsObject(), "text"), "看图说话");

        ChatWire.AppendToContent(arr, "");
        Harness.Eq("追加空串不改动", arr["content"]!.AsArray().Count, 2);

        Harness.Section("usage 累加");

        JsonObject WithUsage(int pt, int ct) => new()
        {
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = pt,
                ["completion_tokens"] = ct,
                ["total_tokens"] = pt + ct,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 3 }
            }
        };

        var summed = ChatWire.SumUsage(new[] { WithUsage(10, 5), WithUsage(20, 7) })!;
        Harness.Eq("prompt_tokens 累加", ChatWire.GetInt(summed, "prompt_tokens"), 30);
        Harness.Eq("completion_tokens 累加", ChatWire.GetInt(summed, "completion_tokens"), 12);
        Harness.Eq("total_tokens 累加", ChatWire.GetInt(summed, "total_tokens"), 42);
        Harness.True("不认识的嵌套字段也一并带过（不做白名单）",
                     summed["prompt_tokens_details"] is JsonObject);

        Harness.True("没有任何 usage 时返回 null", ChatWire.SumUsage(new[] { new JsonObject() }) is null);
        var single = ChatWire.SumUsage(new[] { WithUsage(3, 4) })!;
        Harness.Eq("只有一轮时原样", ChatWire.GetInt(single, "total_tokens"), 7);

        Harness.Section("模型名回写");

        var resp = ChatResponse("a");
        ChatWire.RewriteModel(resp, "client-wanted");
        Harness.Eq("模型名改成客户端请求用的那个", ChatWire.GetString(resp, "model"), "client-wanted");
        ChatWire.RewriteModel(resp, "");
        Harness.Eq("空名字不覆盖（避免把上游的真名抹成空）",
                   ChatWire.GetString(resp, "model"), "client-wanted");

        Harness.Section("客户端工具名收集（两种声明形状）");

        var body = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "read_file" }
                },
                // 少数平台的扁平形状
                new JsonObject { ["name"] = "flat_tool" }
            }
        };
        var names = ChatWire.ClientToolNames(body);
        Harness.Eq("两种形状都收进来", names.Count, 2);
        Harness.True("标准形状的名字对", names.Contains("read_file"));
        Harness.True("扁平形状的名字对", names.Contains("flat_tool"));
        Harness.Eq("没有 tools 时返回空表",
                   ChatWire.ClientToolNames(new JsonObject()).Count, 0);

        Harness.Section("stream 判定");

        Harness.Eq("stream=true", ChatWire.WantsStream(new JsonObject { ["stream"] = true }), true);
        Harness.Eq("stream=false", ChatWire.WantsStream(new JsonObject { ["stream"] = false }), false);
        Harness.Eq("缺省视为非流式", ChatWire.WantsStream(new JsonObject()), false);
        Harness.Eq("stream 写成字符串不当真",
                   ChatWire.WantsStream(new JsonObject { ["stream"] = "true" }), false);
    }
}
