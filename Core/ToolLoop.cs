using System.Text;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 透明工具循环。
///
/// **它对客户端是不可见的。** 客户端发来一次普通的 chat 请求，我们偷偷把
/// 自己的查库工具挂上去；模型调用我们的工具时，由我们本地执行、把结果回灌、
/// 再问一次上游；客户端最终拿到的是一份**普通的聊天响应**，里面既没有我们的
/// 工具声明，也没有我们的工具调用——它甚至不知道我们存在过。
///
/// 三条不可让步的规则，每条都对应一种"看起来能用但会坏掉"的写法：
///
/// **1. 客户端的 tool_calls 一个字节都不许动。** 模型常常在同一轮里既调我们的
///    工具、又调客户端自己的工具（Read/Write/Bash 之类）。我们执行我们的，
///    把客户端那部分**原样**交还给它去执行。若我们把整条响应吃掉自己跑完循环，
///    客户端永远收不到它的工具调用，它的 agent 就死在这一步了。
///    这也是"不影响接入 api 本身那个 agent 的工具调用"这条需求在代码里的落点。
///
/// **2. 思考字段一律不碰。** 不下发、不修改 <c>enable_thinking</c>、
///    <c>reasoning_effort</c>、<c>chat_template_kwargs</c>。客户端要什么档位就是
///    什么档位——思考等级是它和上游之间的事，代理只负责搬运。AnimaCaptioner
///    那边为了自己的界面刻意关了思考（因为测量下来关掉之后模型不发 tool_calls），
///    但那是**那个程序**的取舍；代理照抄过来就成了替所有接入方做决定。
///
/// **3. 这是非流式的那一条。** 客户端要 <c>stream=true</c> 时走
///    <see cref="StreamLoop"/> —— 同一个循环，只是把正文增量实时转发、
///    把工具调用增量按轮缓冲。两条路共用 <see cref="RequestShaper"/> 拼请求，
///    所以"发往上游的报文"逐字一致，不会出现"同一个问题流式和非流式答得不一样"。
/// </summary>
public sealed class ToolLoop
{
    private readonly IUpstream _upstream;
    private readonly ToolHost _tools;
    private readonly ProxyConfig _cfg;

    public ToolLoop(IUpstream upstream, ToolHost tools, ProxyConfig cfg)
    {
        _upstream = upstream;
        _tools = tools;
        _cfg = cfg;
    }

    /// <summary>
    /// 一次循环的结果，含给客户端的最终响应与给人看的日志。
    ///
    /// <see cref="Status"/> 是**必须**的：上游失败时如果照旧回 200 + 一个
    /// <c>{"error":...}</c> 体，接入方会认为这次请求成功了，然后去读
    /// <c>choices</c>，读不到就报一个和真正原因毫无关系的错（"模型没有返回内容"）。
    /// OpenAI 兼容接口的错误是**带状态码**的，代理必须照抄上游的状态码。
    /// </summary>
    public sealed record Outcome(
        JsonObject Response, IReadOnlyList<string> Log, bool Injected, int UpstreamCalls, int Status);

    /// <summary>上游给了状态码就用它的，没给（连不上/超时）就是 502。</summary>
    private static int StatusOf(UpstreamResult res) => res.Status > 0 ? res.Status : 502;

    /// <summary>
    /// 跑一次带注入的对话。<paramref name="body"/> 是客户端原始请求体，
    /// 本方法**不会修改它**（内部一律 DeepClone）。
    /// </summary>
    public async Task<Outcome> RunAsync(JsonObject body, string clientToken, CancellationToken ct)
    {
        var log = new List<string>();

        var clientTools = ChatWire.ClientToolNames(body);
        var plan = ToolCatalog.Plan(clientTools);

        // 我们自己那一轮的消息历史。它是**影子**历史：从客户端发来的 messages
        // 开始，加上我们执行过的工具调用与结果。这条历史只用于我们和上游之间的
        // 后续轮次，绝不会整体回给客户端——否则客户端会看到一堆它没发过的
        // "tool_calls / role:tool" 消息，多数框架会直接判定历史非法。
        var shadow = new JsonArray();
        if (ChatWire.Messages(body) is { } incoming)
            foreach (var m in incoming) shadow.Add(m?.DeepClone());

        var collected = new List<string>();
        var usageResponses = new List<JsonObject>();
        var upstreamCalls = 0;
        var executed = 0;   // 本次请求已本地执行的工具次数（见下方"执行我们的工具"）

        // 最后一轮必须把客户端的工具原样带上：我们要的是"模型在知道有这些工具
        // 的前提下决定该干什么"，只带我们自己的工具会让它以为这个环境里
        // 没有别的能力可用。
        while (true)
        {
            var req = RequestShaper.Build(body, plan, shadow, _cfg, stream: false);
            upstreamCalls++;

            var res = await _upstream.ChatAsync(req, clientToken, ct).ConfigureAwait(false);
            if (!res.Ok)
            {
                // 上游失败：如实回报。**不重试**——上游的错误信息（429、余额不足、
                // 模型名写错）重试一遍只会把同样的错再犯一次，还多花一次配额。
                log.Add($"上游失败：{res.Error}");
                return new Outcome(ErrorResponse(res), log, true, upstreamCalls, StatusOf(res));
            }

            var parsed = res.Body is null ? null : ChatWire.Parse(res.Body);
            if (parsed is null)
            {
                log.Add("上游返回的内容不完整，缺少回答正文");
                return new Outcome(res.Body ?? ErrorResponse(res), log, true, upstreamCalls, 502);
            }

            usageResponses.Add(res.Body!);

            if (!parsed.HasToolCalls)
            {
                // 没有工具调用 = 这一轮就是最终答案。收工。
                log.Add($"第 {upstreamCalls} 轮：无工具调用，收尾");
                var final = res.Body!;
                Finish(final, body, usageResponses, parsed, collected, log);
                return new Outcome(final, log, true, upstreamCalls, 200);
            }

            // 分成"我们的"和"客户端的"两堆。
            var mine = parsed.ToolCalls.Where(c => plan.IsOurs(c.Name)).ToList();
            var theirs = parsed.ToolCalls.Where(c => !plan.IsOurs(c.Name)).ToList();

            log.Add($"第 {upstreamCalls} 轮：调用 {parsed.ToolCalls.Count} 个" +
                    $"（词库 {mine.Count} / 接入方 {theirs.Count}）");

            // 一个我们的都没有 → 全是客户端的 → 原样回给客户端，循环结束。
            // 这是最常见的情况：模型在用它自己那套工具干活，我们只是路过。
            if (mine.Count == 0)
            {
                log.Add("没有词库工具调用，原样交还接入方");
                var asIs = res.Body!;
                Finish(asIs, body, usageResponses, parsed, collected, log);
                return new Outcome(asIs, log, true, upstreamCalls, 200);
            }

            // 执行我们的工具。
            //
            // 计数在**本方法内**：ToolLoop 是一次请求一个实例，而 ToolHost 是
            // 跨请求共享的。把计数挂在 ToolHost 上会让"每次请求最多 N 次"
            // 变成"全局最多 N 次"——跑一阵子之后所有人都查不了库，
            // 而日志上没有任何异常。
            var toolMsgs = new JsonArray();
            foreach (var call in mine)
            {
                if (executed >= _cfg.MaxToolCallsPerRequest)
                {
                    toolMsgs.Add(RequestShaper.ToolMessage(call.Id, RequestShaper.TooManyToolsNote));
                    log.Add("达到单次请求的工具调用上限，已要求问答");
                    continue;
                }

                var kind = plan.KindOf(call.Name);
                string text;
                if (kind is null)
                {
                    // 认不出的工具名：如实回一句，并列出可用的。
                    // 模型下一轮通常会改用正确的名字，比让它空转好。
                    text = $"Unknown tool \"{call.Name}\". Available tools: " +
                           string.Join(", ", plan.Tools.Select(t => t.Name)) + ".";
                }
                else
                {
                    executed++;
                    text = _tools.Execute(_cfg, kind.Value, call.Arguments, out var summary);
                    collected.Add(summary);
                    log.Add(summary);
                }

                toolMsgs.Add(RequestShaper.ToolMessage(call.Id, text));
            }

            // 把这一轮的 assistant 消息（含它的全部 tool_calls，包括客户端那部分）
            // 与我们的工具结果加进影子历史。
            //
            // **为什么客户端的 tool_calls 也留着而不删掉**：上游要求
            // "每个 tool_call 都必须有对应的 role:tool 回复"，缺一个就报 400。
            // 我们删掉客户端的调用，就等于让这条影子历史变成非法请求——
            // 而这条历史是我们自己发给上游的，客户端根本看不到它。
            // 等客户端把它那边的结果发回来时，我们会在下一轮请求里
            // 顺着客户端自己的历史一起带到上游，那才是它该出现的地方。
            shadow.Add(BuildAssistantEcho(parsed));
            // DeepClone：这些节点此刻挂在 toolMsgs 上，直接 Add 进 shadow 会因为
            // "已经有了父节点"而抛异常。
            foreach (var m in toolMsgs) shadow.Add(m!.DeepClone());

            // 影子历史里客户端那部分 tool_call 还没有结果。为了让这一步不报 400，
            // 给它们补一条说明性的 tool 消息——上游只要求"有回复"，不要求内容。
            foreach (var call in theirs)
                shadow.Add(RequestShaper.ToolMessage(call.Id, RequestShaper.PendingClientNote));

            if (upstreamCalls >= _cfg.MaxToolRounds)
            {
                // 轮数用完：把已经查到的结果整理成一段，**不再下发我们的工具**，
                // 再问一次。
                //
                // 不下发我们自己的工具是关键（AnimaCaptioner 那边实测出来的做法）：
                // 再带一次，模型多半还会继续调，而我们已经不想再执行了；不带的时候
                // 它只能给答案。
                //
                // **但客户端的工具必须原样留着。** 那是接入方的能力，不是我们的，
                // 我们没有资格因为自己的轮数用完就把它一起关掉——那会让客户端
                // 在"模型本该调它工具"的时刻收到一个纯文本回答，它的 agent 就断了。
                log.Add($"达到轮数上限 {_cfg.MaxToolRounds}，转为无工具收尾");
                shadow.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = RequestShaper.WrapUpPrompt(collected)
                });

                var lastPlan = new InjectionPlan(Array.Empty<ToolDecl>(),
                    new Dictionary<string, ToolKind>(StringComparer.Ordinal));
                var lastReq = RequestShaper.Build(body, lastPlan, shadow, _cfg, stream: false);
                upstreamCalls++;

                var lastRes = await _upstream.ChatAsync(lastReq, clientToken, ct).ConfigureAwait(false);
                if (!lastRes.Ok)
                {
                    log.Add($"收尾失败：{lastRes.Error}");
                    return new Outcome(ErrorResponse(lastRes), log, true, upstreamCalls, StatusOf(lastRes));
                }

                var lastParsed = lastRes.Body is null ? null : ChatWire.Parse(lastRes.Body);
                if (lastParsed is null)
                    return new Outcome(lastRes.Body ?? ErrorResponse(lastRes), log, true, upstreamCalls, 502);

                if (lastRes.Body is not null) usageResponses.Add(lastRes.Body);
                var wrap = lastRes.Body!;
                Finish(wrap, body, usageResponses, lastParsed, collected, log);
                return new Outcome(wrap, log, true, upstreamCalls, 200);
            }
        }
    }

    /// <summary>
    /// 收尾：把响应整理成客户端该看到的样子。
    ///
    /// 三件事：
    /// 1. 把客户端的工具调用**原样**保留（一个不动），把我们的摘掉；
    /// 2. 模型名改回客户端请求时用的那个（上游可能报了别的名字）；
    /// 3. usage 累加（我们替它多花的几轮也要算进去，否则对不上账单）。
    /// </summary>
    private void Finish(JsonObject response, JsonObject request,
                        List<JsonObject> usageResponses, ParsedChat parsed,
                        List<string> collected, List<string> log)
    {
        var theirs = parsed.ToolCalls.Where(c => !IsOursByName(c.Name)).ToList();
        var mine = parsed.ToolCalls.Where(c => IsOursByName(c.Name)).ToList();

        if (mine.Count > 0)
        {
            // 摘掉我们自己的调用，保留客户端的。
            // 用**名字**判定而不是这一轮的 plan：收尾路径上的 plan 已经换成空集了，
            // 而这一轮响应的身份必须按它当时下发的名字来认。
            ChatWire.KeepOnlyToolCalls(response, theirs.Select(c => c.Id).ToList());
            log.Add($"摘除 {mine.Count} 个词库工具调用，保留 {theirs.Count} 个接入方调用");
        }

        // 把我们查到的东西写进 content，让它跟着客户端自己的对话历史走。
        //
        // 为什么必须做：我们执行完自己的工具后，客户端的历史里不会出现
        // "role:tool" 那条结果。若这一轮同时还有客户端的工具调用，客户端会带着
        // 自己的历史再来一次——而那时模型已经看不到我们查过什么了，只好重查，
        // 于是每次都多花一轮。回写到 content 里，这份结果就一直是它上下文的一部分。
        //
        // **`theirs.Count > 0` 这个门槛是关键，不是保守。** 这段文字是给模型看的
        // 内部记账，不是给用户看的答案。只在"客户端还有活要干、一定会再回来"时才
        // 有意义；没有客户端调用时这一轮就是终稿，客户端会把这串
        // `[tag database lookups already performed by the proxy]` 原样显示给用户，
        // 等于把代理的内部结构漏到用户眼前。回归里"最终内容是模型的回答"那一条
        // 钉的就是这件事。
        if (_cfg.CarryFindings && collected.Count > 0 && theirs.Count > 0 &&
            response["choices"] is JsonArray ch &&
            ch.Count > 0 && ch[0] is JsonObject c0 && c0["message"] is JsonObject msg)
        {
            var sb = new StringBuilder();
            sb.Append("[tag database lookups already performed by the proxy]");
            foreach (var line in collected) sb.Append("\n- ").Append(line);
            sb.Append("\nResults above are already known; do not repeat these lookups.");
            ChatWire.AppendToContent(msg, sb.ToString());
        }

        // 模型名回写：客户端拿它做记账 / 路由，报成上游的真名会让它对不上。
        var wanted = ChatWire.GetString(request, "model");
        if (wanted.Length > 0) ChatWire.RewriteModel(response, wanted);

        var total = ChatWire.SumUsage(usageResponses);
        if (total is not null) response["usage"] = total;
    }

    /// <summary>判定一个工具名是不是代理自有的（按可能的名字形态）。</summary>
    private static bool IsOursByName(string name) => ToolCatalog.IsProxyToolName(name);

    /// <summary>
    /// 造一条放进影子历史的 assistant 消息。
    ///
    /// 直接复用上游给的 message 对象（DeepClone），只把 tool_calls 收敛成
    /// 我们确实要带回上游的那一批——即全部（包括客户端的），因为上游要求
    /// 每个 tool_call 都有回复，而我们会紧接着给它们都补上回复。
    /// </summary>
    private static JsonObject BuildAssistantEcho(ParsedChat parsed)
    {
        var msg = (JsonObject)parsed.Message.DeepClone();
        var all = new JsonArray();
        foreach (var c in parsed.ToolCalls) all.Add(c.Raw.DeepClone());
        msg["tool_calls"] = all;
        // 有些上游不接受 assistant 消息里同时有 reasoning_content 再回灌，
        // 去掉它——它是展示用的，不是对话内容。
        msg.Remove("reasoning_content");
        msg.Remove("reasoning");
        return msg;
    }

    private static JsonObject ErrorResponse(UpstreamResult res)
    {
        var body = res.Body;
        if (body is not null) return body;

        var err = new JsonObject
        {
            ["message"] = res.Error.Length > 0 ? res.Error : "upstream request failed",
            ["type"] = "upstream_error"
        };
        // code 只在真有状态码时才写。写一个 null 进去，部分客户端会把它
        // 当成"错误码是字符串 null"而报出更难懂的第二层错误。
        if (res.Status > 0) err["code"] = res.Status;

        return new JsonObject { ["error"] = err };
    }
}
