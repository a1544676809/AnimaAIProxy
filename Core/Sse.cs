using System.Text;

namespace AnimaAIProxy.Core;

/// <summary>
/// 一个 SSE 事件。<see cref="Raw"/> 是**从上游收到的原文**（含结尾的空行），
/// 需要原样转发时用它；<see cref="Data"/> 是各个 <c>data:</c> 行拼起来的内容。
/// </summary>
public sealed record SseEvent(string Raw, IReadOnlyList<string> DataLines)
{
    public string Data => DataLines.Count == 0 ? "" : string.Join("\n", DataLines);

    /// <summary>OpenAI 流的结束标记：<c>data: [DONE]</c>。</summary>
    public bool IsDone => string.Equals(Data.Trim(), "[DONE]", StringComparison.Ordinal);
}

/// <summary>
/// SSE（Server-Sent Events）的读写。
///
/// **为什么必须自己解析而不是直接转发字节**：流式注入要在增量上做判断——
/// "这一段是正文还是我们的工具调用"。不解析就只能整条读掉，那样首字延迟
/// 等于整段回答的时长，流式的全部意义就没了。
///
/// 解析按**字节**找事件边界，不按字符：分块读取时多字节 UTF-8 字符会被切在
/// 两个 chunk 中间。好在分隔符只有 <c>\n</c> / <c>\r</c>（都 &lt; 0x80），
/// 而 UTF-8 的续字节一律 ≥ 0x80，所以"字节级找空行"永远不会切坏一个字符。
/// 这也是下面这段代码里唯一一处需要小心的地方。
/// </summary>
public static class Sse
{
    /// <summary>把一个 JSON 文本包成一条 SSE 事件（含结尾空行）。</summary>
    public static string Data(string json) => "data: " + json + "\n\n";

    /// <summary>流的结束标记。<c>[DONE]</c> 由**我们**发一次，不从上游转发。</summary>
    public const string Done = "data: [DONE]\n\n";

    /// <summary>
    /// 从上游流里逐个读出事件并交给 <paramref name="onEvent"/>，**边读边回调**。
    ///
    /// 刻意做成回调而不是 <c>IAsyncEnumerable</c>：调用方要在事件之间维护轮次状态
    /// （累积 tool_calls、决定要不要静默收尾），写成迭代器会让那堆状态被拉成
    /// 迭代器外的一堆局部变量，反而更难读。
    /// </summary>
    public static async Task ReadAsync(Stream stream, Func<SseEvent, Task> onEvent, CancellationToken ct)
    {
        var buf = new byte[16384];
        var len = 0;

        while (true)
        {
            // 事件本身可能比缓冲大（长 tool_call 的 arguments 分片很少见，但
            // 一整条 JSON 行超过 16 KB 是可能的）。缓冲不够就翻倍，不设上限：
            // 上游发得出多大，我们就得接得住多大。
            if (len == buf.Length)
            {
                var bigger = new byte[buf.Length * 2];
                Buffer.BlockCopy(buf, 0, bigger, 0, len);
                buf = bigger;
            }

            var n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n <= 0) break;
            len += n;

            var start = 0;
            while (TryTakeEvent(buf, len, start, out var end, out var raw))
            {
                var ev = Parse(raw);
                // **没有任何 data 行的事件一律丢掉**，不交给调用方。
                //
                // SSE 的心跳注释（`: keep-alive`）就是一个合法但无载荷的事件。
                // 交出去的话上层会拿空串去 JsonNode.Parse，失败后把它记成
                // "上游返回的不是合法流式响应"的证据——于是一条纯粹的心跳
                // 会让一次完全正常的对话被判成上游故障。
                //
                // 注意判的是"有没有 data 行"而不是"data 是否为空"：
                // `data:`（空值）是一条**有载荷**的事件，语义与注释不同。
                if (ev.DataLines.Count > 0) await onEvent(ev).ConfigureAwait(false);
                start = end;
            }

            // 把没凑成完整事件的那一截挪到最前面，下一轮接着拼。
            if (start > 0)
            {
                var rest = len - start;
                if (rest > 0) Buffer.BlockCopy(buf, start, buf, 0, rest);
                len = rest;
            }
        }

        // 上游没以空行收尾（极少见）：把残留当作最后一个事件交出去，
        // 而不是静默丢弃——丢掉的很可能正是最后一段正文。
        if (len > 0)
        {
            var raw = Encoding.UTF8.GetString(buf, 0, len);
            var tail = Parse(raw);
            if (raw.Trim().Length > 0 && tail.DataLines.Count > 0)
                await onEvent(tail).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从 <paramref name="from"/> 开始找一个完整事件的结束位置。
    /// 空行的三种合法写法（<c>\n\n</c>、<c>\r\n\r\n</c>、<c>\r\r</c>）都要认：
    /// 只认 <c>\n\n</c> 的话，遇到用 <c>\r\n</c> 的上游会一直等不到边界，
    /// 表现是"流一直不往下走，最后超时"。
    /// </summary>
    private static bool TryTakeEvent(byte[] buf, int len, int from, out int end, out string raw)
    {
        end = 0;
        raw = "";

        for (var i = from; i < len; i++)
        {
            var c = buf[i];

            if (c == (byte)'\n' && i + 1 < len && buf[i + 1] == (byte)'\n') { end = i + 2; break; }

            if (c == (byte)'\r')
            {
                if (i + 1 < len && buf[i + 1] == (byte)'\r') { end = i + 2; break; }
                if (i + 3 < len && buf[i + 1] == (byte)'\n' &&
                    buf[i + 2] == (byte)'\r' && buf[i + 3] == (byte)'\n') { end = i + 4; break; }
            }

            if (c == (byte)'\n' && i + 2 < len && buf[i + 1] == (byte)'\r' && buf[i + 2] == (byte)'\n')
            {
                end = i + 3;
                break;
            }
        }

        if (end == 0) return false;
        raw = Encoding.UTF8.GetString(buf, from, end - from);
        return true;
    }

    /// <summary>
    /// 拆一个事件块。只取 <c>data:</c> 行；注释行（以 <c>:</c> 开头，SSE 里用来
    /// 当心跳）与 <c>event:</c> / <c>id:</c> 一律忽略——OpenAI 的流里只用到 data。
    /// </summary>
    public static SseEvent Parse(string raw)
    {
        var data = new List<string>();
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            if (!string.Equals(field, "data", StringComparison.Ordinal)) continue;

            var value = colon < 0 ? "" : line[(colon + 1)..];
            // 规范里 `data:` 后面可以有一个空格，它是分隔符不是内容。
            if (value.StartsWith(' ')) value = value[1..];
            data.Add(value);
        }

        return new SseEvent(raw, data);
    }
}
