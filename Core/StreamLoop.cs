using System.Text;
using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 流式响应的出口。由宿主（<see cref="ProxyServer"/>）实现；回归里用一个
/// 记账用的假出口，这样整条流式控制流不需要服务器就能验。
///
/// **<see cref="Committed"/> 是关键**：HTTP 状态行必须在第一个字节之前定下来。
/// 上游在吐出任何东西之前就失败时，我们还能如实回一个错误状态码；一旦已经
/// 转发过内容，就再也改不了状态码了，只能往流里发一个 error 事件。
/// 这两条路的差别必须由循环知道，所以它要能从出口这里问出来。
/// </summary>
public interface ISseSink
{
    /// <summary>是否已经写出过第一个字节（也就是状态码已经定死了）。</summary>
    bool Committed { get; }

    /// <summary>写出一个完整的 SSE 事件（含结尾空行），并立刻 flush。</summary>
    Task WriteAsync(string sseEvent, CancellationToken ct);
}

/// <summary>
/// **流式**的透明工具循环。与非流式的 <see cref="ToolLoop"/> 是同一个东西，
/// 只是客户端要求 <c>stream=true</c>。
///
/// 三条规则与非流式完全一致（客户端的 tool_calls 一个字节不动、思考字段一律不碰、
/// 只有 <c>stream=false</c> 之外的那条改成了"流式也注入"），差别只在**怎么发**：
///
/// **1. 正文增量实时转发。** 收到一个 <c>delta.content</c> 就立刻转给客户端并
///    flush。这是流式存在的全部意义，绝不能攒起来——攒到轮末再发，首字延迟
///    就等于整段回答的时长。
///
/// **2. 工具调用增量按轮缓冲。** <c>delta.tool_calls</c> **不转发**，先按
///    <c>index</c> 拼成完整调用（<see cref="ToolCallAssembler"/>）。理由是
///    协议上就必须这样：一个工具调用在被裁定"是我们的还是客户端的"之前，
///    转发出去就是错的——我们自己的调用绝不能让客户端看见。而 arguments 在
///    流里是**分片**下发的，不拼完整也执行不了。
///
/// **3. 最终答案那一轮才收尾。** 中间轮次不发 <c>finish_reason</c>、不发
///    usage、不发 <c>[DONE]</c>：客户端看到的是**一条连续的 assistant 消息**，
///    就好像模型自己在内部查完资料再一路说下来。这正是非流式循环里
///    "影子历史"在做的事，只是这里被摊平在同一条 SSE 流上。
///
/// 于是多轮在客户端眼里是拼起来的：`第1轮正文` + `第2轮正文`。这与"客户端自己
/// 做工具循环"时看到的东西一致（它那边是 正文 → 工具调用 → 工具结果 → 正文），
/// 所以不是 hack，是同一种形状少了它自己那一段。
/// </summary>
public sealed class StreamLoop
{
    private readonly IUpstream _upstream;
    private readonly ToolHost _tools;
    private readonly ProxyConfig _cfg;

    public StreamLoop(IUpstream upstream, ToolHost tools, ProxyConfig cfg)
    {
        _upstream = upstream;
        _tools = tools;
        _cfg = cfg;
    }

    /// <summary>
    /// 一次流式循环的结果。
    ///
    /// <see cref="Completed"/> 为 false 表示"还没写出任何字节就失败了"，
    /// 此时调用方**必须**用 <see cref="Status"/> 回一个 JSON 错误——
    /// 这是上游 429 / 401 / 模型名写错时唯一能如实告诉客户端的机会。
    /// 为 true 时流已经收尾（或已发出 error 事件），调用方什么都不用做。
    /// </summary>
    public sealed record Outcome(
        bool Completed, int Status, string Error, IReadOnlyList<string> Log, int UpstreamCalls);

    public async Task<Outcome> RunAsync(JsonObject body, string clientToken, ISseSink sink, CancellationToken ct)
    {
        var session = new Session(this, body, clientToken, sink, ct);
        return await session.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 一次请求的全部状态。做成一个类而不是一堆闭包局部变量：轮次之间要活的
    /// 东西不少（影子历史、累计 usage、信封字段、已执行计数），摊成局部变量
    /// 之后控制流会变得很难读，而这一层恰恰是最不能读错的地方。
    /// </summary>
    private sealed class Session
    {
        private readonly StreamLoop _owner;
        private readonly IUpstream _upstream;
        private readonly ToolHost _tools;
        private readonly ProxyConfig _cfg;
        private readonly ISseSink _sink;
        private readonly JsonObject _body;
        private readonly string _token;
        private readonly CancellationToken _ct;
        private readonly string _clientModel;

        private InjectionPlan _plan;
        private readonly JsonArray _shadow = new();
        private readonly List<string> _log = new();
        private readonly List<string> _collected = new();
        private readonly List<JsonObject> _usages = new();

        private readonly string _fallbackId = "chatcmpl-proxy-" + Guid.NewGuid().ToString("N")[..24];
        private string _envelopeId = "";
        private long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private bool _roleSent;
        private bool _wrappingUp;
        private int _upstreamCalls;
        private int _executed;

        public Session(StreamLoop owner, JsonObject body, string clientToken, ISseSink sink, CancellationToken ct)
        {
            _owner = owner;
            _upstream = owner._upstream;
            _tools = owner._tools;
            _cfg = owner._cfg;
            _sink = sink;
            _body = body;
            _token = clientToken;
            _ct = ct;
            _clientModel = ChatWire.GetString(body, "model");
            _plan = ToolCatalog.Plan(ChatWire.ClientToolNames(body), _cfg.SearchReady);

            // 影子历史：从客户端发来的 messages 开始，后面每一轮追加我们执行过的
            // 工具调用与结果。它只用于我们和上游之间的后续轮次，绝不会整体回给
            // 客户端——否则客户端会看到一堆它没发过的 tool_calls / role:tool。
            if (ChatWire.Messages(body) is { } incoming)
                foreach (var m in incoming) _shadow.Add(m?.DeepClone());
        }

        // ---------------- 主循环 ----------------

        public async Task<Outcome> ExecuteAsync()
        {
            while (true)
            {
                var round = await RunRoundAsync().ConfigureAwait(false);

                if (!round.Start.Ok)
                    return await FailAsync(round.Start.Status, round.Start.Error, null).ConfigureAwait(false);

                if (round.ErrorBody is not null)
                {
                    // 上游在流里发了一个 error 事件。
                    //
                    // **不能照抄上游的状态码**：这条路上上游的 HTTP 状态是 200
                    // （错误是在流开始之后才出现的），照抄就等于回一个 200 + error 体，
                    // 而那正是之前修掉的那个 bug——接入方以为成功了，去读 choices
                    // 读不到，报一个和真正原因毫无关系的错。
                    // 所以只有错误体里带了合法的 HTTP 状态码才用它，否则一律 502。
                    var msg = ErrorMessage(round.ErrorBody);
                    return await FailAsync(StatusOfError(round.ErrorBody), msg, round.ErrorBody)
                        .ConfigureAwait(false);
                }

                var calls = round.Calls.Build();

                // 200 但内容不是 SSE：上游把错误包在 200 里、或者根本不是流式端点。
                // **不猜**——猜出来的"正文"会让一次上游故障看起来像模型胡言乱语。
                if (!round.SawChoice && calls.Count == 0 && round.FirstBadEvent.Length > 0)
                {
                    return await FailAsync(502,
                        "上游返回的不是合法的流式响应：" + Truncate(round.FirstBadEvent, 300), null)
                        .ConfigureAwait(false);
                }

                var mine = calls.Where(c => ToolCatalog.IsProxyToolName(c.Name)).ToList();
                var theirs = calls.Where(c => !ToolCatalog.IsProxyToolName(c.Name)).ToList();

                // ---- 没有工具调用 = 这一轮就是最终答案，收尾 ----
                if (calls.Count == 0)
                {
                    _log.Add($"第 {_upstreamCalls} 轮：无工具调用，收尾");
                    await CloseAsync(round.FinishReason.Length > 0 ? round.FinishReason : "stop", null)
                        .ConfigureAwait(false);
                    return Completed();
                }

                // 措辞取自 ToolLoop 的常量（Core 与回归共用的契约），不在这里再抄一份：
                // 抄一份的下场就是改文案时两边走偏，而回归靠这行数"模型要求了几次"。
                _log.Add($"第 {_upstreamCalls} {ToolLoop.RoundSummaryTag} {calls.Count} 个" +
                         $"（{ToolLoop.OursLabel} {mine.Count} / {ToolLoop.TheirsLabel} {theirs.Count}）");

                // ---- 收尾轮：不再执行，也不再把我们的调用交出去 ----
                if (_wrappingUp)
                {
                    if (mine.Count > 0)
                        _log.Add($"收尾轮里模型仍调用了 {mine.Count} 个自有工具，已摘除");

                    if (theirs.Count > 0)
                    {
                        await CloseAsync("tool_calls", theirs).ConfigureAwait(false);
                    }
                    else
                    {
                        // 我们的调用被摘干净了，finish_reason 必须同步改回 stop——
                        // 否则客户端会以为"模型要我调工具"，而工具列表里空空如也。
                        var fr = round.FinishReason;
                        if (fr is "tool_calls" or "function_call") fr = "stop";
                        await CloseAsync(fr.Length > 0 ? fr : "stop", null).ConfigureAwait(false);
                    }
                    return Completed();
                }

                // ---- 一个我们的都没有 → 全是客户端的 → 原样交给客户端 ----
                // 正文此刻已经流过去了，这里补上工具调用与 finish_reason。
                if (mine.Count == 0)
                {
                    _log.Add("没有自有工具调用，原样交还接入方");
                    await CloseAsync(round.FinishReason.Length > 0 ? round.FinishReason : "tool_calls", calls)
                        .ConfigureAwait(false);
                    return Completed();
                }

                await ExecuteOursAsync(mine, theirs, calls, round).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 本地执行我们自己的调用，把它们与这一轮的 assistant 消息并进影子历史，
        /// 然后继续下一轮。
        ///
        /// **客户端那部分调用我们不执行、也不转发**，只往影子历史里补一条
        /// "这次调用由客户端处理"的说明。理由与非流式完全一致：上游要求每个
        /// tool_call 都有对应的 role:tool 回复，缺一个就 400；而真正的交还要等
        /// 模型在后续轮次里重新提出（客户端的历史里本来就没有我们的工具结果，
        /// 它会带着自己的历史再来一次）。
        /// </summary>
        private async Task ExecuteOursAsync(List<BufferedCall> mine, List<BufferedCall> theirs,
                                            IReadOnlyList<BufferedCall> all, Round round)
        {
            var toolMsgs = new JsonArray();

            foreach (var call in mine)
            {
                if (_executed >= _cfg.MaxToolCallsPerRequest)
                {
                    toolMsgs.Add(RequestShaper.ToolMessage(call.Id, RequestShaper.TooManyToolsNote));
                    _log.Add("达到单次请求的工具调用上限，已要求问答");
                    continue;
                }

                var kind = _plan.KindOf(call.Name);
                string text;

                if (kind is null)
                {
                    // 认不出的工具名：如实回一句并列出可用的。模型下一轮通常会
                    // 改用正确的名字，比让它空转好。
                    text = $"Unknown tool \"{call.Name}\". Available tools: " +
                           string.Join(", ", _plan.Tools.Select(t => t.Name)) + ".";
                }
                else
                {
                    _executed++;
                    var r = await _tools.ExecuteAsync(_cfg, kind.Value, call.Arguments, _ct)
                                        .ConfigureAwait(false);
                    text = r.Text;
                    _collected.Add(r.Summary);
                    _log.Add(r.Summary);
                }

                toolMsgs.Add(RequestShaper.ToolMessage(call.Id, text));
            }

            _shadow.Add(RequestShaper.AssistantEcho(all, round.ContentText));
            // DeepClone：这些节点此刻挂在 toolMsgs 上，直接 Add 进 shadow 会因为
            // "已经有了父节点"而抛异常。
            foreach (var m in toolMsgs) _shadow.Add(m!.DeepClone());
            foreach (var call in theirs)
                _shadow.Add(RequestShaper.ToolMessage(call.Id, RequestShaper.PendingClientNote));

            if (_upstreamCalls < _cfg.MaxToolRounds)
                return;   // 继续下一轮

            // 轮数用完：追加一条收尾指令，并**只关我们自己的工具**。
            // 客户端的工具原样留着——那是接入方的能力，我们没有资格因为自己的
            // 轮数用完就把它一起关掉。收尾提示里那句 "You have no more tool calls
            // available" 说的是**我们**的工具没了；一个都不发会让这句话变成谎话。
            _log.Add($"达到轮数上限 {_cfg.MaxToolRounds}，转为无工具收尾");
            _shadow.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = RequestShaper.WrapUpPrompt(_collected)
            });
            _plan = new InjectionPlan(Array.Empty<ToolDecl>(),
                                      new Dictionary<string, ToolKind>(StringComparer.Ordinal));
            _wrappingUp = true;
            return;
        }

        // ---------------- 一轮上游请求 ----------------

        private sealed class Round
        {
            public StreamStart Start = new(false, 0, "", "");
            public readonly ToolCallAssembler Calls = new();
            public readonly StringBuilder Content = new();
            public string FinishReason = "";
            public JsonObject? ErrorBody;
            public string FirstBadEvent = "";
            public bool SawChoice;

            public string ContentText => Content.ToString();
        }

        private async Task<Round> RunRoundAsync()
        {
            var round = new Round();
            var req = RequestShaper.Build(_body, _plan, _shadow, _cfg, stream: true);
            _upstreamCalls++;

            var start = await _upstream.StreamAsync(req, _token, stream => ConsumeAsync(stream, round), _ct)
                                       .ConfigureAwait(false);
            round.Start = start;
            return round;
        }

        private async Task ConsumeAsync(Stream stream, Round round)
        {
            await Sse.ReadAsync(stream, ev =>
            {
                if (ev.IsDone) return Task.CompletedTask;

                var node = ChatWire.TryParseObject(ev.Data);
                if (node is null)
                {
                    if (round.FirstBadEvent.Length == 0) round.FirstBadEvent = ev.Data;
                    return Task.CompletedTask;
                }

                if (node["error"] is not null)
                {
                    round.ErrorBody ??= node;
                    return Task.CompletedTask;
                }

                return HandleChunkAsync(node, round);
            }, _ct).ConfigureAwait(false);
        }

        private async Task HandleChunkAsync(JsonObject node, Round round)
        {
            // usage 一律**不转发**：中间轮的 usage 提前发给客户端是错的
            // （那一次请求还没结束），而我们替它多花了几轮，必须累加后只报一次。
            //
            // 收的是**整个 chunk 节点**而不是 node["usage"]：累加走
            // <see cref="ChatWire.SumUsage"/>，它按"从响应里取 usage"的口径工作，
            // 只喂给它那个 usage 对象的话，它会去里面再找一层 "usage" 而找不到，
            // 于是累加结果静默变成 null —— 客户端一个 usage 都收不到。
            if (node["usage"] is JsonObject) _usages.Add(node);

            if (_envelopeId.Length == 0)
            {
                var id = ChatWire.GetString(node, "id");
                if (id.Length > 0) _envelopeId = id;
            }
            if (ChatWire.GetInt(node, "created") is { } created && created > 0) _created = created;

            if (node["choices"] is not JsonArray choices || choices.Count == 0) return;

            // 先把整块的增量读完再决定要不要转发。
            //
            // **必须按块转发一次，不能按 choice 转发**：一个 chunk 里若有两个
            // choice，逐个转发会把整块（含全部 choice）发出去两次，客户端收到
            // 重复正文——而流式里"多发一遍"不会被任何协议层拦住，只会表现为
            // 答案里莫名重复一段话。
            var role = "";
            var forwardable = false;

            foreach (var c in choices)
            {
                if (c is not JsonObject choice) continue;
                round.SawChoice = true;

                var fr = ChatWire.GetString(choice, "finish_reason");
                if (fr.Length > 0) round.FinishReason = fr;

                if (choice["delta"] is not JsonObject delta) continue;

                // 工具调用增量：缓冲，且**只缓冲**。
                if (delta["tool_calls"] is JsonArray tc && tc.Count > 0) round.Calls.Add(tc);

                if (delta["content"] is not null)
                {
                    round.Content.Append(ChatWire.GetString(delta, "content"));
                    forwardable = true;
                }

                var r = ChatWire.GetString(delta, "role");
                if (r.Length > 0) { role = r; forwardable = true; }

                // 思考增量也照样转给客户端：那是它和上游之间的事，代理只负责搬运。
                if (delta["reasoning_content"] is not null || delta["reasoning"] is not null)
                    forwardable = true;
            }

            // 纯 tool_calls 的块（模型正在要工具，正文是空的）到此为止，
            // 一个字节都不发——那是我们和上游之间的内部协商。
            if (!forwardable) return;

            await ForwardAsync(node, role).ConfigureAwait(false);
        }

        /// <summary>
        /// 把一个上游增量转给客户端。
        ///
        /// **整块克隆后只改必须改的地方**，而不是"重新拼一个干净的对象"：
        /// 上游的 <c>delta</c> 里可能有我们根本不认识、但客户端要用的字段
        /// （某些服务会放 <c>extra_content</c>、<c>thought_signature</c> 之类）。
        /// 只搬运不劫持这条约定在流式路径上同样成立。
        ///
        /// 三处修改：
        /// ① 信封字段统一成第一条 chunk 的 id/created，模型名换回**客户端要的那个**
        ///    （上游报的是映射后的真名，客户端拿它记账会对不上）；
        /// ② 摘掉 <c>tool_calls</c>（那是缓冲的，由我们决定何时发）；
        /// ③ 摘掉 <c>finish_reason</c>（中间轮次的结束标记绝不能外泄）。
        /// </summary>
        private async Task ForwardAsync(JsonObject node, string role)
        {
            var clone = (JsonObject)node.DeepClone();
            clone.Remove("usage");
            clone["object"] = "chat.completion.chunk";
            clone["id"] = _envelopeId.Length > 0 ? _envelopeId : _fallbackId;
            clone["created"] = _created;
            if (_clientModel.Length > 0) clone["model"] = _clientModel;

            if (clone["choices"] is JsonArray cs)
            {
                foreach (var cc in cs)
                {
                    if (cc is not JsonObject cco) continue;
                    if (cco["delta"] is JsonObject d2) d2.Remove("tool_calls");
                    cco.Remove("finish_reason");
                }
            }

            // role 必须在第一条正文之前到。上游几乎总是自己会发一条 role chunk，
            // 但少数实现直接从 content 开始，那时客户端会收到一条没有角色的消息。
            if (!_roleSent && role.Length == 0) await SendRoleAsync().ConfigureAwait(false);
            if (role.Length > 0) _roleSent = true;

            await SendAsync(clone).ConfigureAwait(false);
        }

        private async Task SendRoleAsync()
        {
            _roleSent = true;
            await SendChunkAsync(new JsonObject { ["role"] = "assistant" }, null).ConfigureAwait(false);
        }

        /// <summary>
        /// 收尾：把缓冲的客户端工具调用补发出去，然后发 finish_reason、
        /// 累加后的 usage，最后自己发一次 <c>[DONE]</c>。
        /// 上游的那个 <c>[DONE]</c> 一律丢弃——多轮之下它只代表**某一轮**结束。
        /// </summary>
        private async Task CloseAsync(string finishReason, IReadOnlyList<BufferedCall>? calls)
        {
            if (calls is { Count: > 0 })
            {
                var arr = new JsonArray();
                for (var i = 0; i < calls.Count; i++)
                {
                    arr.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["id"] = calls[i].Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = calls[i].Name,
                            ["arguments"] = calls[i].Arguments
                        }
                    });
                }
                await SendChunkAsync(new JsonObject { ["tool_calls"] = arr }, null).ConfigureAwait(false);
            }

            if (!_roleSent) await SendRoleAsync().ConfigureAwait(false);

            await SendChunkAsync(new JsonObject(), finishReason).ConfigureAwait(false);

            if (ChatWire.SumUsage(_usages) is { } total)
                await SendUsageAsync(total).ConfigureAwait(false);

            await _sink.WriteAsync(Sse.Done, _ct).ConfigureAwait(false);
        }

        private async Task SendChunkAsync(JsonObject delta, string? finishReason)
        {
            var node = new JsonObject
            {
                ["id"] = _envelopeId.Length > 0 ? _envelopeId : _fallbackId,
                ["object"] = "chat.completion.chunk",
                ["created"] = _created,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = delta,
                        ["finish_reason"] = finishReason
                    }
                }
            };
            if (_clientModel.Length > 0) node["model"] = _clientModel;

            await SendAsync(node).ConfigureAwait(false);
        }

        private async Task SendUsageAsync(JsonObject usage)
        {
            var node = new JsonObject
            {
                ["id"] = _envelopeId.Length > 0 ? _envelopeId : _fallbackId,
                ["object"] = "chat.completion.chunk",
                ["created"] = _created,
                ["choices"] = new JsonArray(),
                ["usage"] = usage
            };
            if (_clientModel.Length > 0) node["model"] = _clientModel;

            await SendAsync(node).ConfigureAwait(false);
        }

        private Task SendAsync(JsonNode node) =>
            _sink.WriteAsync(Sse.Data(ChatWire.ToJson(node)), _ct);

        // ---------------- 失败路径 ----------------

        private Outcome Completed() => new(true, 200, "", _log, _upstreamCalls);

        /// <summary>
        /// 上游失败。**不重试**——429、余额不足、模型名写错，重试只会把同样的错
        /// 再犯一次，还多花一次配额。
        ///
        /// 分两种情况，由"有没有已经吐出去过字节"决定：没吐过就能如实回状态码，
        /// 吐过了就只能往流里发一个 error 事件（HTTP 状态行早就发出去了）。
        /// </summary>
        private async Task<Outcome> FailAsync(int status, string error, JsonObject? body)
        {
            if (_ct.IsCancellationRequested)
            {
                _log.Add("客户端已断开，停止注入");
                return new Outcome(true, 0, "客户端已取消本次请求", _log, _upstreamCalls);
            }

            _log.Add("上游失败：" + error);

            if (!_sink.Committed)
                return new Outcome(false, status > 0 ? status : 502, error, _log, _upstreamCalls);

            var payload = body ?? new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = error.Length > 0 ? error : "上游流式请求失败",
                    ["type"] = "upstream_error"
                }
            };

            try
            {
                await _sink.WriteAsync(Sse.Data(ChatWire.ToJson(payload)), _ct).ConfigureAwait(false);
                await _sink.WriteAsync(Sse.Done, _ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 客户端可能已经断了。日志留痕即可，不再往上抛——这时候抛出去
                // 只会变成一个 500，而真正的信息是"上游失败了"。
                ProxyLog.Write("写流式错误事件失败：" + ex.Message);
            }

            return new Outcome(true, 200, error, _log, _upstreamCalls);
        }

        private static string ErrorMessage(JsonObject node)
        {
            if (node["error"] is JsonObject err)
            {
                var m = ChatWire.GetString(err, "message");
                if (m.Length > 0) return m;
            }
            var text = ChatWire.ToJson(node);
            return text.Length > 400 ? text[..400] + "…" : text;
        }

        /// <summary>
        /// 从流内的 error 事件里取一个可信的 HTTP 状态码。
        ///
        /// 严格限定 400..599 且必须是**数字**：错误体里的 <c>code</c> 常常是字符串
        /// （<c>"invalid_request_error"</c>、<c>"rate_limit_exceeded"</c>），
        /// 拿它去当状态码会写进一个非法值，而 Kestrel 会在写响应头时才抛出来——
        /// 那时已经没有任何补救余地了。
        /// </summary>
        private static int StatusOfError(JsonObject node)
        {
            if (node["error"] is JsonObject err &&
                ChatWire.GetInt(err, "code") is { } code && code is >= 400 and <= 599)
                return code;
            return 502;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
