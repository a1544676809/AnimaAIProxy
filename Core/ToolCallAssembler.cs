using System.Text;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 一个从流式增量里拼出来的完整工具调用。
///
/// 流式的 tool_call 是**分片下发**的：先来一条带 <c>index</c> 与
/// <c>function.name</c> 的，再来若干条只带 <c>function.arguments</c> 片段的。
/// 要执行它（或原样交还给客户端）就必须先拼回完整形态。
/// </summary>
public sealed record BufferedCall(string Id, string Name, string Arguments)
{
    /// <summary>还原成 OpenAI 的 tool_call 对象形状，用于回灌影子历史或交还客户端。</summary>
    public JsonObject ToRaw() => new()
    {
        ["id"] = Id,
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = Name, ["arguments"] = Arguments }
    };
}

/// <summary>
/// 把流式响应里的 <c>delta.tool_calls[]</c> 按 <c>index</c> 拼成完整的工具调用。
///
/// **拼接规则与 OpenAI 官方 SDK 的累加逻辑一致**（这是关键，不是随手的取舍）：
/// <c>function.name</c> 与 <c>function.arguments</c> 都做**字符串累加**，
/// <c>id</c> 取第一个非空值。
///
/// 为什么 name 也要累加而不是"取第一个非空"：官方 SDK 就是累加的，也就是说
/// 上游既有"name 只在第一片出现一次"的写法（累加等价于取值），也可能有
/// "name 被拆成几片"的写法（取值会得到一个残缺的工具名）。后者对本代理尤其
/// 危险——残缺的名字认不出来是我们的工具，就会被当成客户端的工具**交还回去**，
/// 于是客户端收到一个它根本没有的 <c>danbooru_*</c> 调用。跟着生态的约定走，
/// 比自己发明一套判据安全。
/// </summary>
public sealed class ToolCallAssembler
{
    private sealed class Slot
    {
        public string Id = "";
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Arguments = new();
    }

    // 用 SortedDictionary 按 index 排序：上游理论上按序发，但**不依赖它**——
    // 交还给客户端的顺序必须与模型当时的意图一致，乱序会让客户端把结果
    // 回填到错误的调用上。
    private readonly SortedDictionary<int, Slot> _slots = new();

    public bool Any => _slots.Count > 0;

    /// <summary>吃下一批增量分片。</summary>
    public void Add(JsonArray deltas)
    {
        for (var i = 0; i < deltas.Count; i++)
        {
            if (deltas[i] is not JsonObject d) continue;

            // index 缺失时用"这条在数组里的位置"兜底：少数实现不发 index，
            // 若一律当成 0，多个调用会被拼成一团乱码。
            var index = ChatWire.GetInt(d, "index") ?? i;
            if (!_slots.TryGetValue(index, out var slot))
            {
                slot = new Slot();
                _slots[index] = slot;
            }

            if (slot.Id.Length == 0) slot.Id = ChatWire.GetString(d, "id");

            if (d["function"] is JsonObject fn)
            {
                slot.Name.Append(ChatWire.GetString(fn, "name"));
                slot.Arguments.Append(ChatWire.GetString(fn, "arguments"));
            }
        }
    }

    /// <summary>拼好的调用，按 index 升序。</summary>
    public IReadOnlyList<BufferedCall> Build()
    {
        var list = new List<BufferedCall>(_slots.Count);
        foreach (var kv in _slots)
            list.Add(new BufferedCall(kv.Value.Id, kv.Value.Name.ToString(), kv.Value.Arguments.ToString()));
        return list;
    }
}
