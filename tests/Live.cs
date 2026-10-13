using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 接**真实上游**跑一轮真实对话。刻意不放进 <c>all</c>：
/// 它要联网、要一把真密钥、还会花掉真实的 token。
///
///     proxycheck live              # 用 config.json 里的密钥与 base url
///     proxycheck live &lt;baseUrl&gt;   # 换一个上游
///
/// 这一层要回答的问题只有真上游能回答（桩上游永远答不了）：
///   1. 真实模型的 tool_calls 到底长什么样（OpenAI 兼容程度）
///   2. 关掉思考（reasoning_effort / 档位）之后，它还发不发 tool_calls
///   3. reasoning_content 这类非标准字段真实存在时，我们摘得干不干净
///   4. 我们的工具描述，真实模型看得懂、会不会真的去调
/// </summary>
public static class Live
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>从 proxy.config.json 里取上游配置（密钥走 DPAPI 解密，和程序本体同一条路）。</summary>
    private static (string BaseUrl, string Key) ResolveUpstream(string[] args)
    {
        string? baseUrl = args.Length > 1 ? args[1] : null;
        string? key = null;

        try
        {
            var cfg = ProxyConfig.Load();
            baseUrl ??= cfg.UpstreamBaseUrl;
            key = cfg.UpstreamApiKey;
        }
        catch (Exception ex)
        {
            Harness.Note("读 proxy.config.json 失败：" + ex.Message);
        }

        return (baseUrl?.TrimEnd('/') ?? "", key ?? "");
    }

    public static void Run(string[] args)
    {
        Harness.Section("真实上游联调：客户端 → 代理 → 真实模型（要联网、要密钥、花真 token）");

        var (baseUrl, key) = ResolveUpstream(args);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            Harness.Note("没有可用的上游配置（base url 或密钥为空），跳过。");
            return;
        }

        Harness.Info($"上游：{baseUrl}（密钥 {key[..Math.Min(6, key.Length)]}…，{key.Length} 字符）");

        // ---- 先直连问一次：这既是握手，也是"真上游怎么回话"的基线 ----
        var model = Probe.Model(baseUrl, key) ?? "deepseek-chat";
        Harness.Info($"用模型：{model}");

        var dbPath = Vocab.ResolveDbPath(args);
        var hasDb = !string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath);
        if (!hasDb) Harness.Note("找不到词库：工具会退化成「不可用」，这一轮的结论会不完整。");

        var tmpDir = Path.Combine(Path.GetTempPath(), "aip-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        var oldTokenPath = TokenStore.FilePathOverride;
        TokenStore.FilePathOverride = Path.Combine(tmpDir, "proxy.tokens.json");

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            BindScope = "local",
            UpstreamBaseUrl = baseUrl,
            UpstreamApiKey = key,
            RequireToken = true,
            ToolsEnabled = true,
            AnnounceTools = true,
            MaxToolRounds = 4,
            TimeoutSeconds = 120,
            VocabDbPath = hasDb ? dbPath! : Path.Combine(tmpDir, "no-vocab.sqlite"),
            SeedPath = Vocab.ResolveSeedPath() ?? ""
        };

        var tokens = new TokenStore();
        tokens.Tokens.Add(TokenStore.Create("live", "", out var token));

        var server = new ProxyServer(cfg, tokens, new VocabService());
        server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var url = $"http://127.0.0.1:{cfg.Port}/v1/chat/completions";

        try
        {
            // ============ 1. 真实模型会不会真的去调我们的工具 ============
            {
                Harness.Section("1. 工具注入：真实模型会不会自己想到查 danbooru 词库");
                ProxyLog.Snapshot(); // 触发一次取用，便于下面按行打日志

                var body = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "system",
                            ["content"] = "你是助手。回答必须准确，需要查词库时就用工具查。"
                        },
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "画一个初音未来在弹吉他的图，给我 danbooru 标签。"
                        }
                    },
                    ["temperature"] = 0.7,
                    ["max_tokens"] = 1024
                };

                var sw = Stopwatch.StartNew();
                var r = Post(http, url, ChatWire.ToJson(body), token);
                sw.Stop();

                Harness.Eq("整条链路走通，200", r.Status, 200);
                Harness.Info($"耗时 {sw.ElapsedMilliseconds}ms");

                var json = ChatWire.TryParseObject(r.Text);
                if (json is null)
                {
                    Harness.Note("响应不是 JSON，原文前 500 字：" + Truncate(r.Text, 500));
                    return;
                }

                var parsed = ChatWire.Parse(json);
                if (parsed is null)
                {
                    Harness.Note("响应里没有可解析的 choice，原文前 800 字：" + Truncate(r.Text, 800));
                    return;
                }

                Harness.Info("最终回答：" + Truncate(parsed.Content, 600));
                Harness.Eq("客户端拿到的 message 里没有我们的 tool_call 残留",
                           parsed.ToolCalls.Count, 0);
                Harness.True("拿到了非空的最终回答", parsed.Content.Trim().Length > 0);

                // 日志是判断"工具到底有没有被调"最直接的证据。这里分两个口径数，
                // 不能混为一谈：
                //   "第 N 轮：调用 X 个（我们 Y / 客户端 Z）" → Y 是模型**要求**调用的数量
                //   "核对「…」→ …"                        → 真正**执行**成功的一次
                // 上限只约束后者：超限的那些会被挡下并回一句"工具调用太多"，
                // 所以"要求 15 次、执行 12 次"是正常且符合设计的。
                var lines = ProxyLog.Snapshot().ToList();
                Harness.Info($"代理本轮日志（共 {lines.Count} 行，取末尾）：");
                foreach (var l in lines.TakeLast(40)) Harness.Info("  " + l);

                var requested = 0;
                foreach (var l in lines)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(l, @"我们\s*(\d+)\s*/\s*客户端\s*(\d+)");
                    if (m.Success) requested += int.Parse(m.Groups[1].Value);
                }
                // 执行成功的那一行由 ToolHost 用三种前缀之一写出（查「/ 核对「/ 切分），
                // 对应三个工具；数它们就等于数"我们的工具真的跑了几次"。
                //
                // 剥掉时间戳后 TrimStart 再判前缀。**不要用固定偏移**：调用方的缩进
                // （ProxyServer 会再补两个空格）和日志自己的分隔空格加起来是可变的，
                // 按固定下标切会整体错位、一条都数不到（实测踩过：5 轮共 17 次调用
                // 被数成 0 次，而断言却"通过"，因为另一个断言只要求 > 0）。
                // 时间戳恰好 23 字符（yyyy-MM-dd HH:mm:ss.fff），这是唯一稳定的锚点。
                var executed = lines.Count(l => IsToolExecution(MsgOf(l)));

                Harness.Info(executed > 0
                    ? $"结论：真实模型**主动调用了**我们的词库工具——要求 {requested} 次，实际执行 {executed} 次" +
                      $"（上限 {cfg.MaxToolCallsPerRequest}）。"
                    : "结论：真实模型这一轮**没有调用**我们的工具（它认为不需要，或没理解工具用途）。");

                Harness.True("模型确实要求调用我们的工具（真实上游认了这套工具声明）", requested > 0);
                // **这一条是真正有意义的那个**：光是"要求"还不够，工具真的在本地执行了
                // 才算链路通了。上面那次误数成 0 的教训就是——只断言 requested>0 时，
                // 计数逻辑坏掉也照样"通过"。
                Harness.True("工具确实在本地执行了（日志里有成功执行的记录）", executed > 0);
                Harness.True($"实际执行次数没有超过配置上限 {cfg.MaxToolCallsPerRequest}",
                             executed <= cfg.MaxToolCallsPerRequest);

                if (hasDb)
                {
                    // 无论模型自觉不自觉，工具本身在真实链路上必须可用。
                    var vocab = new AnimaCaptioner.Core.VocabDb();
                    vocab.Load(dbPath!, Vocab.ResolveSeedPath() ?? Path.ChangeExtension(dbPath!, ".json"));
                    var hits = vocab.Search("hatsune miku", 3);
                    Harness.True("真实词库在真实链路上可查（hatsune miku 有命中）", hits.Count > 0);
                    if (hits.Count > 0)
                        Harness.Info($"  实查：{hits[0].Tag}（{hits[0].Cn}）");
                }
            }

            // ============ 2. 思考字段透传：我们不劫持，真上游认不认 ============
            {
                Harness.Section("2. 思考字段透传（代理不劫持，原样交给真上游）");

                foreach (var effort in new[] { "high", "none" })
                {
                    var body = new JsonObject
                    {
                        ["model"] = model,
                        ["messages"] = new JsonArray
                        {
                            new JsonObject { ["role"] = "user", ["content"] = "说一句 5 个字的话。" }
                        },
                        ["max_tokens"] = 256,
                        ["reasoning_effort"] = effort
                    };

                    var r = Post(http, url, ChatWire.ToJson(body), token);
                    var json = ChatWire.TryParseObject(r.Text);
                    var parsed = json is null ? null : ChatWire.Parse(json);

                    Harness.Info($"reasoning_effort={effort} → HTTP {r.Status}，" +
                                 (parsed is null
                                     ? "无 choice：" + Truncate(r.Text, 300)
                                     : $"content={Truncate(parsed.Content, 120)} / reasoning={Truncate(parsed.Reasoning, 80)}"));

                    Harness.True($"reasoning_effort={effort} 这一轮上游接了（2xx）",
                                 r.Status >= 200 && r.Status < 300);
                }
            }

            // ============ 2.5 关掉思考之后，真实模型还发不发 tool_calls ============
            //
            // **这一条是整份诊断里最有价值的一条。**
            // AnimaCaptioner 那边实测过一句结论："关掉思考后模型根本不发 tool_calls"，
            // 代理"不劫持思考字段"这个设计决定正是建立在它上面——如果我们自作主张
            // 替接入方把思考关小以省 token，就会连带把工具调用能力一起关掉，
            // 表现是"代理挂上了工具但模型永远不调"，且极难归因。
            //
            // 桩上游永远答不了这个问题：它按剧本回话，剧本里写什么就是什么。
            //
            // **A/B 必须只变一个量。** 这里刻意让客户端**不带任何 tools**，
            // 工具完全由代理注入（section 1 已经证明它会注入），于是两轮之间
            // 唯一的差别就只有 reasoning_effort。第一版我在这里自己声明了一遍
            // 工具名，结果与代理注入的那份重名、被让路改名，两轮都是 0 次调用——
            // 那种做法测不出任何东西，只会给出一个看着像结论的假象。
            {
                Harness.Section("2.5 思考开关对 tool_calls 的影响（决定「代理该不该动思考字段」）");

                var results = new Dictionary<string, int>(StringComparer.Ordinal);
                var reasoned = new Dictionary<string, bool>(StringComparer.Ordinal);

                // 执行次数用固定偏移剥时间戳后判前缀。日志是**全程累计**的，
                // 所以每一轮都要取"这一轮前后的差值"，而不是直接把累计值当结果。
                static int CountExecuted(IEnumerable<string> ls) => ls.Count(l =>
                {
                    var m = l.Length > 23 ? l[23..].TrimStart() : l.TrimStart();
                    return m.StartsWith("查「", StringComparison.Ordinal)
                        || m.StartsWith("核对「", StringComparison.Ordinal)
                        || m.StartsWith("切分", StringComparison.Ordinal);
                });

                foreach (var effort in new[] { "high", "none" })
                {
                    var before = CountExecuted(ProxyLog.Snapshot());
                    var body = new JsonObject
                    {
                        ["model"] = model,
                        ["messages"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["role"] = "user",
                                // 刻意问一个"只能查库才知道"的问题，逼它调工具。
                                ["content"] = "查一下 danbooru 里「蕾丝」对应的英文标签是什么，用工具查完再回答。"
                            }
                        },
                        ["max_tokens"] = 512,
                        ["reasoning_effort"] = effort
                        // 刻意不写 tools：由代理注入，两轮唯一的变量就是上面的 effort。
                    };

                    var r = Post(http, url, ChatWire.ToJson(body), token);
                    var json = ChatWire.TryParseObject(r.Text);
                    var parsed = json is null ? null : ChatWire.Parse(json);

                    if (parsed is null)
                    {
                        Harness.Note($"reasoning_effort={effort}：没有可解析的 choice，原文前 400 字：" +
                                     Truncate(r.Text, 400));
                        continue;
                    }

                    // 代理会把我们的 tool_call 摘干净，所以"客户端看到的 tool_calls"
                    // 恒为 0——判断模型到底调没调，要看这一轮日志里有没有执行记录。
                    var after = CountExecuted(ProxyLog.Snapshot());
                    results[effort] = after - before;
                    reasoned[effort] = parsed.Reasoning.Length > 0;

                    Harness.Info($"reasoning_effort={effort}：HTTP {r.Status}，" +
                                 $"finish_reason={parsed.FinishReason}，" +
                                 $"reasoning={(parsed.Reasoning.Length > 0 ? "有" : "无")}，" +
                                 $"本轮工具执行 {results[effort]} 次，" +
                                 $"content={Truncate(parsed.Content, 120)}");

                    Harness.True($"reasoning_effort={effort} 这一轮上游接了（2xx）",
                                 r.Status >= 200 && r.Status < 300);
                }

                if (results.Count == 2)
                {
                    var on = results["high"];
                    var off = results["none"];
                    var sawReasoning = reasoned["high"] || reasoned["none"];

                    Harness.Info($"对比：思考 high → 工具执行 {on} 次；思考 none → {off} 次");
                    Harness.Info($"两轮的 reasoning 字段：high={(reasoned["high"] ? "有" : "无")}，" +
                                 $"none={(reasoned["none"] ? "有" : "无")}");

                    if (on > 0 && off == 0)
                    {
                        Harness.Note("结论：**关掉思考后模型确实不发 tool_calls**（复现了 AnimaCaptioner 的观察）。");
                        Harness.Note("这正是代理绝不替接入方改动思考字段的原因——替它关掉思考" +
                                     "等于连工具能力一起关掉。");
                    }
                    else if (on > 0 && off > 0)
                    {
                        Harness.Note("结论：**两轮都发了 tool_calls**，" +
                                     "与 AnimaCaptioner 的观察不同（那是本地 llama-server + " +
                                     "chat_template_kwargs 的路径，与云端上游不是一回事）。");

                        // 别把"两轮都调了"直接说成"思考关掉也不影响工具"——如果两轮
                        // 的 reasoning 都是空的，那说明这组 A/B 里 effort 压根没改变
                        // 上游行为，这个对比就没测到思考开关，不能当结论用。
                        Harness.Note(sawReasoning
                            ? "这一组 effort 确实影响了上游（至少一轮吐了 reasoning），对比成立。"
                            : "但两轮都没有 reasoning 字段，说明 effort 在这条上游上没体现出差异——" +
                              "这组对比**不足以证明**思考开关与工具调用无关，只能说未见影响。");
                    }
                    else
                    {
                        Harness.Note($"结论：两轮都没调工具（high={on} / none={off}），" +
                                     "这一组对比不成立——模型可能只是没被说服去查库。");
                    }

                    Harness.Note("无论哪种结果，代理「不劫持思考字段」都是对的选择：" +
                                 "这个开关的效果取决于上游与模型，代理没有资格替接入方决定。");
                }
            }

            // ============ 3. 接入方工具 + 我们的工具同时存在 ============
            {
                Harness.Section("3. 两套工具并存：接入方的工具原样交还，不会被我们吃掉");

                var body = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "请调用 read_file 工具读取 /etc/hosts，然后告诉我你调用了什么工具名。"
                        }
                    },
                    ["max_tokens"] = 512,
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = "read_file",
                                ["description"] = "Read a file from disk.",
                                ["parameters"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["path"] = new JsonObject { ["type"] = "string" }
                                    },
                                    ["required"] = new JsonArray { "path" }
                                }
                            }
                        }
                    }
                };

                var r = Post(http, url, ChatWire.ToJson(body), token);
                Harness.Eq("200", r.Status, 200);

                var json = ChatWire.TryParseObject(r.Text);
                var parsed = json is null ? null : ChatWire.Parse(json);
                if (parsed is null)
                {
                    Harness.Note("无 choice，原文前 400 字：" + Truncate(r.Text, 400));
                    return;
                }

                if (parsed.ToolCalls.Count > 0)
                {
                    Harness.Eq("交还的确实是接入方的那个工具", parsed.ToolCalls[0].Name, "read_file");
                    Harness.True("id 非空（接入方要靠它回填结果）",
                                 !string.IsNullOrWhiteSpace(parsed.ToolCalls[0].Id));
                    Harness.Eq("finish_reason 是 tool_calls", parsed.FinishReason, "tool_calls");
                    Harness.Info("真实 tool_call 形态：" + Truncate(parsed.ToolCalls[0].ToString(), 300));
                    Harness.Info("原始 message 里的 tool_calls 字段：" +
                                 Truncate(json?["choices"]?[0]?["message"]?["tool_calls"]?.ToJsonString() ?? "(无)", 400));
                }
                else
                {
                    Harness.Info("这一轮真实模型没调工具，回答是：" + Truncate(parsed.Content, 300));
                    Harness.True("没调工具时，我们的 tool_call 也没有被凭空塞进去",
                                 parsed.FinishReason != "tool_calls");
                }
            }

            // ============ 4. 流式：真上游的 tool_calls 分片长什么样 ============
            //
            // 这一节只有真上游能回答：桩上游的分片形状是我们自己编的，而真实
            // OpenAI 兼容实现的 <c>delta.tool_calls</c> 到底怎么切、<c>index</c>
            // 给不给、<c>name</c> 会不会被拆开，只有接一次真的才知道。
            // 拼错的表现是"工具查不到东西"，而模型照样能胡诌出一个答案。
            {
                Harness.Section("4. 流式注入：真上游的分片 tool_calls 能不能拼完整并执行");
                var logMark = ProxyLog.Snapshot().Count;   // 见下方"只数本节的行"

                var body = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "system",
                            ["content"] = "你是助手。需要查 danbooru 标签时必须用工具查，不要凭记忆写。"
                        },
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "查一下「弓」这个标签的准确英文名和帖子数，用一句话回答。"
                        }
                    },
                    ["max_tokens"] = 1024,
                    // **客户端要的是流式。** 这是本节的重点：早先这条路不做注入。
                    ["stream"] = true
                };

                var (status, events, arrivals, raw) = PostStream(http, url, ChatWire.ToJson(body), token);

                Harness.Eq("流式请求 200", status, 200);
                Harness.True($"收到多个 SSE 事件（{events.Count} 个）", events.Count > 1);
                if (arrivals.Count > 1)
                    Harness.Info($"流式时序：读次数={arrivals.Count}，首块={arrivals[0]}ms，" +
                                 $"总耗时={arrivals[^1]}ms");

                // 客户端看到的正文（把所有 delta.content 拼起来）
                var sb = new StringBuilder();
                var leakedToolCalls = 0;
                var sawStop = false;
                var sawDone = false;
                JsonObject? usage = null;

                foreach (var ev in events)
                {
                    if (ev.IsDone) { sawDone = true; continue; }
                    var node = ChatWire.TryParseObject(ev.Data);
                    if (node is null) continue;

                    if (node["usage"] is JsonObject u) usage = u;

                    if (node["choices"] is not JsonArray cs) continue;
                    foreach (var c in cs)
                    {
                        if (c is not JsonObject co) continue;
                        if (co["delta"] is JsonObject d)
                        {
                            if (d["content"] is not null) sb.Append(ChatWire.GetString(d, "content"));
                            if (d["tool_calls"] is JsonArray tc)
                                foreach (var t in tc)
                                    if (ChatWire.GetString(t?["function"] as JsonObject, "name")
                                            .Contains("danbooru", StringComparison.OrdinalIgnoreCase))
                                        leakedToolCalls++;
                        }
                        if (ChatWire.GetString(co, "finish_reason") == "stop") sawStop = true;
                    }
                }

                var answer = sb.ToString();
                Harness.Info("客户端拼出来的最终回答：" + Truncate(answer, 500));
                Harness.True("拿到了非空的流式回答", answer.Trim().Length > 0);
                Harness.True("我们的 tool_call 一个都没漏给客户端", leakedToolCalls == 0);
                Harness.True("流里有 finish_reason=stop", sawStop);
                Harness.True("流里有 [DONE]", sawDone);
                Harness.Info("usage：" + (usage is null ? "(无)" : ChatWire.ToJson(usage)));

                // **真正的证据在日志里**：我们自己的工具到底有没有被本地执行。
                // 只看"回答里有没有提到标签"是不够的——模型完全可能凭记忆编一个
                // 看起来对的答案，而那正是这套工具要防的事。
                //
                // **只数本节产生的行**：ProxyLog.Snapshot() 是累计的（进程内全部日志），
                // 直接全量数会把前面几节的执行次数也算进来，"流式下工具执行了 N 次"
                // 这句话就成了假的验证。从 logMark 开始截。
                var lines = ProxyLog.Snapshot().Skip(logMark).ToList();
                Harness.Info($"代理本节日志（{lines.Count} 行，取末尾）：");
                foreach (var l in lines.TakeLast(25)) Harness.Info("  " + l);

                var executedStream = 0;
                var streamRounds = 0;
                foreach (var l in lines)
                {
                    var t = MsgOf(l);
                    if (IsToolExecution(t)) executedStream++;
                    if (t.StartsWith("第 ", StringComparison.Ordinal) && t.Contains("轮：")) streamRounds++;
                }

                Harness.True($"流式路径下我们的工具真的被本地执行了（{executedStream} 次）",
                             executedStream > 0);
                Harness.Info($"流式路径跑了 {streamRounds} 轮（>1 说明先查库、再作答）");

                if (hasDb)
                {
                    Harness.True("流式路径的回答里带着词库里的真实标签与帖子数",
                                 answer.Contains("bow", StringComparison.OrdinalIgnoreCase) ||
                                 System.Text.RegularExpressions.Regex.IsMatch(answer, @"\d[\d,]{3,}"));
                }

                // 流式路径上一轮的 usage 必须是**累加**过的（多轮跑完只报一次）。
                if (usage is not null && streamRounds > 1)
                {
                    var pt = ChatWire.GetInt(usage, "prompt_tokens") ?? 0;
                    Harness.True($"usage 是多轮累加的结果（prompt_tokens={pt}，只报了一次）", pt > 0);
                }

                _ = raw;
            }
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            TokenStore.FilePathOverride = oldTokenPath;
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(空)" : (s.Length <= n ? s : s[..n] + "…");

    /// <summary>
    /// 剥掉日志行的时间戳。时间戳恰好 23 字符（<c>yyyy-MM-dd HH:mm:ss.fff</c>），
    /// 这是唯一稳定的锚点：调用方补的缩进是可变的，按固定下标切会整体错位。
    /// </summary>
    private static string MsgOf(string line) =>
        line.Length > 23 ? line[23..].TrimStart() : line.TrimStart();

    /// <summary>
    /// 这一行是不是"我们的工具真的执行了一次"。
    ///
    /// 三种前缀对应三个工具（由 <c>ToolHost</c> 写出）。**两处口调用同一个函数**：
    /// 之前第 4 节自己抄了一份判据、还用了 <c>Contains(']')</c> 当锚点，而这些行
    /// 里根本没有 <c>]</c>，于是时间戳留在开头、一条都数不到，断言报 0 次——
    /// 而真实情况是工具已经执行过了。判据只留一份才不会再次走偏。
    /// </summary>
    private static bool IsToolExecution(string message) =>
        message.StartsWith("查「", StringComparison.Ordinal)
        || message.StartsWith("核对「", StringComparison.Ordinal)
        || message.StartsWith("切分", StringComparison.Ordinal);

    private sealed record HttpResult(int Status, string Text);

    private static HttpResult Post(HttpClient http, string url, string body, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        try
        {
            using var resp = http.SendAsync(req).GetAwaiter().GetResult();
            return new HttpResult((int)resp.StatusCode,
                                  resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new HttpResult(0, "请求异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 发一个**流式**请求，把 SSE 事件逐个读出来（不是一次性读完），
    /// 并记录每次读到的时刻 —— 只有逐个读才能证明"首字真的早"。
    /// </summary>
    private static (int Status, List<SseEvent> Events, List<long> Arrivals, string Raw)
        PostStream(HttpClient http, string url, string body, string token)
    {
        var events = new List<SseEvent>();
        var arrivals = new List<long>();
        var raw = new StringBuilder();

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                                 .GetAwaiter().GetResult();
            var status = (int)resp.StatusCode;

            using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            var buf = new byte[4096];
            var pending = new StringBuilder();
            int n;

            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                var text = Encoding.UTF8.GetString(buf, 0, n);
                raw.Append(text);
                pending.Append(text);
                arrivals.Add(sw.ElapsedMilliseconds);

                // 把已经凑完整的事件切出来。SSE 的空行是事件边界。
                var all = pending.ToString().Replace("\r\n", "\n");
                int cut;
                while ((cut = all.IndexOf("\n\n", StringComparison.Ordinal)) >= 0)
                {
                    var block = all[..(cut + 2)];
                    all = all[(cut + 2)..];
                    var ev = Sse.Parse(block);
                    if (ev.DataLines.Count > 0) events.Add(ev);
                }
                pending.Clear();
                pending.Append(all);
            }
        }
        catch (Exception ex)
        {
            Harness.Note("流式请求异常：" + ex.Message);
            return (0, events, arrivals, raw.ToString());
        }

        return (200, events, arrivals, raw.ToString());
    }
}

/// <summary>直连上游探测（不经代理），用来确定默认模型名。</summary>
internal static class Probe
{
    public static string? Model(string baseUrl, string key)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var resp = http.SendAsync(req).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var node = ChatWire.TryParseObject(text);
            var arr = node?["data"] as JsonArray;
            if (arr is null || arr.Count == 0) return null;

            var ids = arr.Select(n => ChatWire.GetString(n as JsonObject, "id"))
                         .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            Harness.Info($"上游共报 {ids.Count} 个模型：" + string.Join(", ", ids));

            // 优先挑一个明显适合对话的。
            return ids.FirstOrDefault(i => i.Contains("chat", StringComparison.OrdinalIgnoreCase))
                   ?? ids.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Harness.Note("探测 /models 失败：" + ex.Message);
            return null;
        }
    }
}
