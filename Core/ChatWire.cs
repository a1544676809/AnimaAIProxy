using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>一次上游调用的结果。失败时 <see cref="Raw"/> 仍保留正文，便于如实回报错误。</summary>
public sealed record UpstreamResult(bool Ok, int Status, string Raw, JsonObject? Body, string Error)
{
    public static UpstreamResult Transport(string error) => new(false, 0, "", null, error);
}

/// <summary>解析出来的一个 tool_call。arguments **保持原始字符串**，不在这里 parse。</summary>
public sealed record ToolCallRef(string Id, string Name, string Arguments, JsonObject Raw);

/// <summary>从一次上游响应里解析出来的东西。</summary>
public sealed record ParsedChat(
    string Content,
    string Reasoning,
    string FinishReason,
    IReadOnlyList<ToolCallRef> ToolCalls,
    JsonObject Message,
    JsonObject Response)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// 线上格式的解析与拼装。全部是纯函数——代理里最容易出错的就是这一层
/// （字段名拼错、null 没判、把 reasoning 当成 content），而它又最难在真机上看出来
/// （表现只是"模型答得怪"）。所以这一层刻意做成不碰网络、不碰文件，能被回归直接驱动。
///
/// **一条铁律贯穿全文：未知字段一律保留。** 我们只改自己认识的那几个键，
/// 其余原样带过——上游的 <c>reasoning_content</c>、<c>system_fingerprint</c>、
/// <c>logprobs</c>、甚至还没发布的字段，都不该因为我们转发一次就消失。
/// 这也是"思考等级等透传不劫持"这条需求在代码里的落点。
/// </summary>
public static class ChatWire
{
    // ---------------- 请求侧 ----------------

    public static JsonArray? Messages(JsonObject body) => body["messages"] as JsonArray;

    public static bool WantsStream(JsonObject body) => GetBool(body, "stream") ?? false;

    /// <summary>客户端本次带来的工具名。用于判定重名让路。</summary>
    public static List<string> ClientToolNames(JsonObject body)
    {
        var names = new List<string>();
        if (body["tools"] is not JsonArray arr) return names;

        foreach (var node in arr)
        {
            if (node is not JsonObject tool) continue;
            // 标准形状：{type:"function", function:{name:...}}
            if (tool["function"] is JsonObject fn)
            {
                var n = GetString(fn, "name");
                if (n.Length > 0) names.Add(n);
                continue;
            }
            // 少数平台用扁平形状：{name:...} 或 {type:"function", name:...}
            var flat = GetString(tool, "name");
            if (flat.Length > 0) names.Add(flat);
        }
        return names;
    }

    // ---------------- 响应侧 ----------------

    /// <summary>
    /// 解析一次响应。结构不对时返回 null，由调用方如实回报——
    /// 这里**不猜**：猜出来的"内容"会让上游的一次故障看起来像模型胡言乱语。
    /// </summary>
    public static ParsedChat? Parse(JsonObject body)
    {
        if (body["choices"] is not JsonArray choices || choices.Count == 0) return null;
        if (choices[0] is not JsonObject choice) return null;
        if (choice["message"] is not JsonObject msg) return null;

        var calls = new List<ToolCallRef>();
        if (msg["tool_calls"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject call) continue;
                var fn = call["function"] as JsonObject;
                calls.Add(new ToolCallRef(
                    Id: GetString(call, "id"),
                    Name: GetString(fn, "name"),
                    Arguments: GetString(fn, "arguments"),
                    Raw: (JsonObject)call.DeepClone()));
            }
        }

        return new ParsedChat(
            Content: GetString(msg, "content"),
            // 两种命名都要读：DeepSeek / 部分 OpenAI 兼容服务用 reasoning_content，
            // 另一些用 reasoning。读漏一个就会把思考链当成正文少了一段。
            Reasoning: FirstNonEmpty(GetString(msg, "reasoning_content"), GetString(msg, "reasoning")),
            FinishReason: GetString(choice, "finish_reason"),
            ToolCalls: calls,
            Message: msg,
            Response: body);
    }

    /// <summary>把一条上游响应里的 tool_calls 过滤成只剩指定的一批（保持原顺序）。</summary>
    public static void KeepOnlyToolCalls(JsonObject response, IReadOnlyCollection<string> keepIds)
    {
        if (response["choices"] is not JsonArray choices || choices.Count == 0) return;
        if (choices[0] is not JsonObject choice) return;
        if (choice["message"] is not JsonObject msg) return;
        if (msg["tool_calls"] is not JsonArray arr) return;

        var keep = new HashSet<string>(keepIds, StringComparer.Ordinal);
        var kept = new JsonArray();
        foreach (var node in arr.ToList())
        {
            if (node is not JsonObject call) continue;
            var id = GetString(call, "id");
            // id 缺失时**一律保留**：认不出身份的一个调用，宁可多留一个
            // 让客户端去执行（最坏是它报一句"未知工具"），也不能吃掉它的工具。
            if (id.Length == 0 || keep.Contains(id)) kept.Add(node.DeepClone());
        }

        if (kept.Count == 0) msg.Remove("tool_calls");
        else msg["tool_calls"] = kept;

        // 只有我们的工具被调过、且客户端那边一个都没有：既然我们把 tool_calls
        // 摘干净了，finish_reason 必须同步改回 stop，否则客户端会以为
        // "模型要我调工具"，而工具列表里空空如也，多数框架会直接报错。
        if (kept.Count == 0)
        {
            var fr = GetString(choice, "finish_reason");
            if (fr is "tool_calls" or "function_call") choice["finish_reason"] = "stop";
        }
    }

    /// <summary>
    /// 往一条 assistant 消息的 content 里追加一段文本（用于把本地查到的结果带给客户端）。
    ///
    /// content 可能是字符串，也可能是多模态数组——两种都要能追加，
    /// 只处理字符串会让带图对话里的追加静默失效。
    /// </summary>
    public static void AppendToContent(JsonObject message, string extra)
    {
        if (string.IsNullOrEmpty(extra)) return;

        var current = message["content"];
        if (current is null)
        {
            message["content"] = extra;
            return;
        }

        if (current is JsonValue v && v.TryGetValue<string>(out var s))
        {
            message["content"] = string.IsNullOrEmpty(s) ? extra : s + "\n\n" + extra;
            return;
        }

        if (current is JsonArray parts)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = extra });
            return;
        }

        // 其它形状（数字之类）：不硬拼，直接换成字符串。
        // 这一条几乎不会走到，但走到时"丢掉原文"比"整条响应被客户端判为非法"好。
        message["content"] = extra;
    }

    /// <summary>把响应里的模型名改回客户端请求时用的那个。</summary>
    public static void RewriteModel(JsonObject response, string model)
    {
        if (!string.IsNullOrEmpty(model)) response["model"] = model;
    }

    /// <summary>
    /// 把多轮上游调用的 usage 累加起来。
    ///
    /// **必须累加而不是只报最后一轮**：我们的工具循环对客户端是不可见的，
    /// 它看到的是一次请求。只报最后一轮的话，账单上少掉的正是我们替它花的那几轮。
    /// 用户拿这个数去对上游账单会对不上，然后开始怀疑代理在偷偷扣费。
    /// 字段名碰不到的键（缓存命中、推理 token 之类）也一并相加，不做白名单。
    /// </summary>
    public static JsonObject? SumUsage(IEnumerable<JsonObject> responses)
    {
        JsonObject? total = null;
        foreach (var r in responses)
        {
            if (r["usage"] is not JsonObject u) continue;
            total ??= new JsonObject();
            foreach (var kv in u)
            {
                // 嵌套对象 / 数组（`prompt_tokens_details`、`completion_tokens_details`
                // 之类）：跨轮相加没有意义（"缓存命中数"加起来是什么？），所以不加，
                // 但也**绝不能让它消失**——客户端可能靠它做统计或计费，而字段静默
                // 消失是不会报错的。原样带过第一轮那一份。
                if (kv.Value is not JsonValue val)
                {
                    if (total[kv.Key] is null) total[kv.Key] = kv.Value?.DeepClone();
                    continue;
                }

                if (TryLong(val, out var n))
                {
                    var prev = total[kv.Key] is JsonValue pv && TryLong(pv, out var p) ? p : 0L;
                    total[kv.Key] = prev + n;
                }
                else if (total[kv.Key] is null)
                {
                    // 不是数字、也不是容器（字符串、布尔、null）：加不了，原样带过。
                    total[kv.Key] = val.DeepClone();
                }
            }
        }
        return total;
    }

    /// <summary>
    /// 从 JsonValue 里取出一个整数。
    ///
    /// **必须逐个类型试，不能只试 long。** <c>JsonValue.TryGetValue&lt;T&gt;</c> 对
    /// 装箱类型是**精确匹配**：一个由 <c>int</c> 造出来的 JsonValue 用
    /// <c>TryGetValue&lt;long&gt;</c> 取会返回 false。只试 long 的话，
    /// token 数（几乎总是 int）会被判成"不是数字"，于是累加静默退化成
    /// "保留第一轮的值"——而 usage 少报的症状是用户拿它对不上上游账单，
    /// 没有任何报错。这条是被离线回归抓出来的。
    ///
    /// 浮点只在**整值**时才算数：<c>42.0</c> 是 42，<c>0.42</c> 不是 0。
    /// 把 0.42 当成 0 会让"这个字段没有值"和"这个字段的值是 0"看起来一样。
    /// </summary>
    private static bool TryLong(JsonValue v, out long n)
    {
        if (v.TryGetValue<int>(out var i)) { n = i; return true; }
        if (v.TryGetValue<long>(out var l)) { n = l; return true; }
        if (v.TryGetValue<short>(out var s)) { n = s; return true; }
        if (v.TryGetValue<byte>(out var b)) { n = b; return true; }
        if (v.TryGetValue<double>(out var d) && IsWhole(d)) { n = (long)d; return true; }
        if (v.TryGetValue<decimal>(out var m) && IsWhole(m)) { n = (long)m; return true; }
        n = 0;
        return false;
    }

    private static bool IsWhole(double d) =>
        !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d;

    private static bool IsWhole(decimal m) => decimal.Truncate(m) == m;

    // ---------------- 安全取值 ----------------

    public static string GetString(JsonObject? obj, string key)
    {
        if (obj is null) return "";
        var node = obj[key];
        if (node is null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s ?? "";
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        }
        return "";
    }

    public static bool? GetBool(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        if (obj[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    /// <summary>
    /// 取一个整数字段。
    ///
    /// 走的是与 usage 累加同一套类型识别（<see cref="TryLong"/>），而不是
    /// 只试 <c>int</c>。这不是洁癖：我们自己写回去的 usage 是 <c>long</c>，
    /// 只试 int 会让"刚累加好的 token 数"在下一次读出来时变成 null——
    /// 同一份数据，写进去读不出来。
    /// </summary>
    public static int? GetInt(JsonObject? obj, string key)
    {
        if (obj is null) return null;
        if (obj[key] is not JsonValue v) return null;
        if (!TryLong(v, out var n)) return null;
        // 夹住而不是回绕：token 数不可能到这个量级，但真出现了也宁可报一个
        // 极大值，不要因为溢出报出一个负数。
        if (n > int.MaxValue) return int.MaxValue;
        if (n < int.MinValue) return int.MinValue;
        return (int)n;
    }

    public static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }

    /// <summary>把一个 JSON 字符串解析成对象；不成对象则返回 null。</summary>
    public static JsonObject? TryParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json);
            return node as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 序列化成要发给客户端的 JSON 文本。
    ///
    /// **必须指定编码器**：<c>ToJsonString()</c> 的默认编码器会把所有非 ASCII
    /// 字符转成 <c>\uXXXX</c>，于是一句中文错误信息在日志、在平台上、
    /// 在用户眼里都是一串码点。语义没错，但"代理报的错看不懂"会直接让用户
    /// 放弃排查——而错误信息存在的全部意义就是让人看懂。
    ///
    /// 用 UnsafeRelaxedJsonEscaping 是安全的：响应头是 application/json，
    /// 浏览器不会把它当 HTML 渲染，所以没有注入面。项目里的配置序列化
    /// （<see cref="ProxyConfig"/>）用的也是同一个编码器，两处口径一致。
    ///
    /// **TypeInfoResolver 也必须显式给**（这不是洁癖，是被回归抓出来的）：
    /// <c>JsonNode.ToJsonString(options)</c> 会先把 options 实例标成只读，
    /// 而"首次使用时自动补上默认反射解析器"那一步就来不及了，抛
    /// <c>JsonSerializerOptions instance must specify a TypeInfoResolver setting
    /// before being marked as read-only</c>。症状是**每一个**走这条路的响应都
    /// 500，堆栈指向序列化，看起来像我们的 JSON 有问题。
    /// （<c>JsonSerializer.Serialize(obj, options)</c> 不会踩到，所以配置那边没事。）
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public static string ToJson(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(WireOptions);
}
