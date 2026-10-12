# LlmClient.cs 技术报告（逐行核对版）

> 对象文件：`E:\source\repos\AnimaCaptioner\Core\LlmClient.cs`（1092 行）
> 辅助参考：`Core\ModelProfile.cs`、`Core\DpapiSecret.cs`（为理解设置与密钥；LlmClient 本体**不**引用 DPAPI）
> 结论一句话：**这个文件是纯 .NET 的**（`System.Net.Http` + `System.Text.Json` + `Regex`），
> 没有任何 WinUI/WinRT 类型；唯二的工程外耦合是 `AppSettings`、`AgentTool` 和 `Log` 三个类型。

---

## 0. 事实速查表

| 项 | 结论 | 依据行 |
|---|---|---|
| HttpClient | `private readonly HttpClient _http = new();` — **默认 `HttpClientHandler`**，无自定义 handler | 30 |
| 超时可改 | `SetTimeout(int seconds)`，`Math.Max(10, seconds)`，只在值变化时写回 | 1073–1087 |
| 请求 URI | `cfg.ApiBaseUrl.TrimEnd('/') + "/chat/completions"` / `+ "/models"` | 185, 457, 750 |
| 认证 | `req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim())` | 256–257, 510–511, 757–758 |
| 请求体 | 手搭 `JsonObject`（`System.Text.Json.Nodes`），**不是 DTO 序列化** | 195–205, 476–482 |
| `stream` 字段 | **代码里完全没有** —— 全文件无流式/SSE | 全文 |
| 工具循环 | 不在本文件里；本文件只做**一轮** `ChatWithToolsAsync`，循环在 `TagAgent` / `TagTranslator` | 449–537 |
| 推理开关 | 关思考发 `chat_template_kwargs.enable_thinking=false`；档位发 `reasoning_effort` | 219–248 |
| 能力探测 | `GET /models` → `TryParseModels` → 内存字典 `_caps`（键 = baseUrl + `\|` + model） | 744–859 |
| 思考剥离 | `StripThinking`（成对标签 + 未闭合尾删 + Qwen3 `redacted_thinking`） | 354–380 |
| 图片 | data URI 内联，`image_url.url`，**没有 `detail` 字段** | 130–135, 609–627 |
| UI 耦合 | **本文件 0 处**；耦合在调用方（`ImagePayload` 用 WinRT 编码、`MainWindow` 持有实例） | — |

---

## 1. 类的整体结构

### 1.1 类型与常量

```csharp
public sealed class LlmClient : IDisposable        // 28
{
    private readonly HttpClient _http = new();      // 30
    private TimeSpan _currentTimeout = Timeout.InfiniteTimeSpan;   // 41

    public string LastError { get; private set; } = "";            // 43

    public const string KeywordSystemPrompt   = "...";  // 46-50  中文拆关键词
    public const string ProseSystemPrompt     = "...";  // 90-109 中文描述 → 英文散文（翻译器，非写手）
    public const string TagGlossSystemPrompt  = "...";  // 148-161 单标签 → 中文释义（允许答 UNKNOWN）
    private const string SearchToolDescription = "..."; // 164-167 **定义了但全文件无人引用**（死常量）
    private const int ReasoningHeadroom = 1200;         // 332
    private const string ThinkNames = "thinking|think|thought|reasoning";  // 383
    private static readonly string ThinkOpen  = "<" + "redacted" + "_thinking" + ">";   // 392
    private static readonly string ThinkClose = "</" + "redacted" + "_thinking" + ">";  // 393
}
```

三个 system prompt 是**承重**的（注释里写了实测代价）：`ProseSystemPrompt` 明确禁止编造、要求长度跟随输入、
要求不许丢方向词（第 93–109 行）；`TagGlossSystemPrompt` 允许弃权答 `UNKNOWN`（第 159–161 行）。

### 1.2 公共 record（签名照抄）

```csharp
public sealed record ChatResult(bool Ok, string Content, string Reasoning, string Raw, string Error);  // 111

public sealed record ToolCall(string Id, string Name, string Arguments);                              // 114

public sealed record ToolTurn(bool Ok, string Content, string Reasoning,
                              List<ToolCall> Calls, string Raw, string Error);                        // 117-118

public sealed record ChatImage(string MimeType, byte[] Bytes)                                         // 130
{
    public string DataUri =>                                                                          // 132-134
        "data:" + (string.IsNullOrWhiteSpace(MimeType) ? "image/jpeg" : MimeType) +
        ";base64," + Convert.ToBase64String(Bytes);
}

public sealed record ModelInfo(                                                                       // 679-690
    string Id,
    string Quantization = "",
    int ContextLength = 0,
    long SizeBytes = 0,
    IReadOnlyList<string>? Aliases = null,
    string DisplayName = "",
    int MaxOutputTokens = 0,
    IReadOnlyList<string>? InputModalities = null,
    IReadOnlyList<string>? EffortLevels = null,
    string DefaultEffort = "")
{
    public bool? SupportsImage => ...;   // 698-701  三态：true/false/null
    public bool SupportsEffort => EffortLevels is { Count: > 0 };   // 704
    public string Describe() { ... }     // 710-728  "显示名 · 量化 · 上下文 40960 · 4.68 GB · 支持图像输入 · 思考 low/high/max（默认 high）"
}
```

### 1.3 公共方法/属性（完整清单，按行序）

```csharp
// —— 实例：一次 chat/completions ——
public Task<ChatResult> ChatAsync(AppSettings cfg, string systemPrompt, string userPrompt,
                                  CancellationToken ct = default);                          // 170-172
public async Task<ChatResult> ChatAsync(AppSettings cfg, string systemPrompt, string userPrompt,
                                        ChatImage? image, CancellationToken ct = default);  // 178-287

// —— 实例：带工具的一轮 ——
public async Task<ToolTurn> ChatWithToolsAsync(
    AppSettings cfg, string systemPrompt, List<JsonObject> messages,
    IReadOnlyList<AgentTool> tools, int maxTokens,
    CancellationToken ct = default);                                                        // 449-537

// —— 实例：/models 与能力缓存 ——
public async Task<(bool Ok, List<ModelInfo> Models, string Error)> ListModelsAsync(
    AppSettings cfg, CancellationToken ct = default);                                        // 744-792
public ModelInfo? Caps(AppSettings cfg);                                                     // 826-830
public async Task<ModelInfo?> EnsureCapsAsync(AppSettings cfg, CancellationToken ct = default); // 838-859

// —— 实例：生命周期 ——
public void Dispose() => _http.Dispose();                                                    // 1091

// —— 静态：纯函数（全部可脱网测试）——
public static string StripThinking(string s);                                                // 354-380
public static ChatResult InterpretChoice(string content, string reasoning,
                                         string finishReason, string raw);                   // 411-434
public static string QueryFromArguments(string arguments);                                   // 596-606
public static JsonObject UserMessage(string text, ChatImage? image);                         // 609-627
public static JsonObject AssistantMessage(string content, List<ToolCall> calls);              // 630-647
public static JsonObject ToolMessage(string callId, string content);                          // 650-655
public static List<string> SplitKeywords(string content);                                     // 658-662
public static string FormatSize(long bytes);                                                  // 732-735
public static List<ModelInfo> ParseModels(string raw);                                        // 936-937
public static bool TryParseModels(string raw, out List<ModelInfo> models);                    // 947-974
public static string NormalizeEffort(string? effort);                                         // 881
public static bool IsThinkingOff(string? effort);                                             // 890-891
public static string? EffortToSend(string? configured, bool effortSupported);                 // 913-919
public static bool CanAttachImage(bool? supportsImage, bool hasImage);                        // 929-930

// —— 静态字段 / internal ——
public static readonly IReadOnlyList<string> EffortVocabulary = new[]
    { "none", "minimal", "low", "medium", "high", "xhigh", "ultra", "max" };                  // 875-878
internal static string CapKey(string baseUrl, string model);                                  // 807-809

// —— 私有：解析与工具 ——
private static ChatResult ParseResponse(string raw);        // 289-313
private static ToolTurn  ParseToolTurn(string raw);         // 539-593
private static void AddModels(JsonElement array, List<ModelInfo> sink);   // 976-1023
private static string? Str / int Int / long Long / List<string>? StrArray  // 1025-1046
private static void DedupeModels(List<ModelInfo> list);     // 1054-1064
private static ChatResult Fail(string m);                   // 1066
private void SetTimeout(int seconds);                       // 1073-1087
private void Remember(AppSettings cfg, List<ModelInfo> models);   // 811-818
private static string Truncate(string s, int n);            // 1089
private readonly Dictionary<string, ModelInfo> _caps      = new(StringComparer.OrdinalIgnoreCase); // 802
private readonly HashSet<string> _capTried                = new(StringComparer.OrdinalIgnoreCase); // 805
```

职责划分一句话：**静态部分全是可测纯函数**（解析 / 剥思考 / 真值表判断），
**实例部分只有一个 `HttpClient` + 两个能力缓存字典**——没有任何 UI 状态。

---

## 2. HTTP 层细节

### 2.1 HttpClient / Handler

```csharp
private readonly HttpClient _http = new();               // 30
```

- 无自定义 `HttpMessageHandler`、无 `SocketsHttpHandler`、无代理/证书配置、无 `PooledConnectionLifetime`。
- 每次 `SendAsync` 都 `using var req` / `using var resp`，**没有 `HttpClient` 复用之外的连接管理**。
- `HttpCompletionOption.ResponseContentRead`（261、515）——即完整缓冲响应体后再返回；
  `/models` 那条用默认重载（761）。

### 2.2 超时（踩过坑的地方）

```csharp
private TimeSpan _currentTimeout = Timeout.InfiniteTimeSpan;    // 41

private void SetTimeout(int seconds)                            // 1073-1087
{
    var want = TimeSpan.FromSeconds(Math.Max(10, seconds));
    if (want == _currentTimeout) return;
    try
    {
        _http.Timeout = want;
        _currentTimeout = want;
    }
    catch (InvalidOperationException)
    {
        // 已经有请求在飞：保持原超时继续用，不要让整次调用失败
        Log.Write($"HttpClient.Timeout 已锁定（已有请求在途），忽略 {want.TotalSeconds:F0}s");
    }
}
```

`/models` 用独立的窄超时（753）：`Math.Min(30, Math.Max(5, cfg.TimeoutSeconds))`。

### 2.3 URI 拼接

```csharp
var url = cfg.ApiBaseUrl.TrimEnd('/') + "/chat/completions";   // 185, 457
var url = cfg.ApiBaseUrl.TrimEnd('/') + "/models";             // 750
```

即 `cfg.ApiBaseUrl` 必须**自带 `/v1`**（默认值见 `AppSettings.DefaultBaseUrl = "http://127.0.0.1:8080/v1"`，
`Core/AppSettings.cs:101`）。**没有**任何 `/v1` 自动补全、没有 `Uri` 校验、没有 query string。

### 2.4 请求体：`ChatAsync` 的 JSON 布局（照抄）

```csharp
var thinking = !IsThinkingOff(cfg.ReasoningEffort);                                     // 190
var budget = thinking ? cfg.MaxTokens + ReasoningHeadroom : cfg.MaxTokens;              // 191

var body = new JsonObject                                                               // 195-205
{
    ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "local" : cfg.Model,
    ["messages"] = new JsonArray
    {
        new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
        UserMessage(userPrompt, image)
    },
    ["max_tokens"] = budget,
    ["temperature"] = cfg.Temperature
};

var effort = NormalizeEffort(cfg.ReasoningEffort);                                      // 219
if (IsThinkingOff(effort))
    body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };      // 222
```

随后按档位有条件地加一个键：

```csharp
if (effort.Length > 0)                                                                  // 226
{
    var caps = await EnsureCapsAsync(cfg, ct).ConfigureAwait(false);                     // 228
    var send = EffortToSend(effort, caps?.SupportsEffort == true);                       // 229
    if (send is not null)
    {
        body["reasoning_effort"] = send;                                                 // 232
        Log.Write($"llm effort: reasoning_effort={send} " +
                  $"(模型声明 {string.Join("/", caps!.EffortLevels!)}，默认 {caps.DefaultEffort})");
    }
    ...
}
```

**实际会出现的键集合**（`ChatAsync`）：
`model`、`messages`、`max_tokens`、`temperature`，条件性地加 `chat_template_kwargs`（仅档位 = `none`）
和 `reasoning_effort`（仅档位非空 **且** 服务端在 `/models` 里声明过 `effort` **且** 值在八词表内）。

未发送的键：**没有** `stream`、**没有** `top_p` / `presence_penalty` / `frequency_penalty`、
**没有** `response_format`、**没有** `tool_choice`（那条路才有）、**没有** `enable_thinking` 顶层字段
（它只作为 `chat_template_kwargs` 的内层键出现）。

序列化与发送：

```csharp
using var req = new HttpRequestMessage(HttpMethod.Post, url)                            // 252-255
{
    Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
};
if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());  // 256-257
```

注意 `body.ToJsonString()` **没有传 `JsonSerializerOptions`**，所以中文/非 ASCII 走
`System.Text.Json` 默认编码器（会被转义成 `\uXXXX`）；`UTF8Encoding(false)` 表示不写 BOM。

### 2.5 请求体：`ChatWithToolsAsync` 的 JSON 布局（照抄）

```csharp
var toolJson = new JsonArray();                                              // 459-470
foreach (var t in tools)
    toolJson.Add(new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Parameters.DeepClone(),
        },
    });

var msgs = new JsonArray();                                                  // 472-474
msgs.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
foreach (var m in messages) msgs.Add(m.DeepClone());

var body = new JsonObject                                                    // 476-482
{
    ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "local" : cfg.Model,
    ["messages"] = msgs,
    ["max_tokens"] = maxTokens,
    ["temperature"] = cfg.Temperature,
};

if (tools.Count > 0)                                                         // 487-491
{
    body["tools"] = toolJson;
    body["tool_choice"] = "auto";
}
```

两条硬规则：

1. **空工具列表时绝不下发 `"tools": []`**（注释 484–486：某些服务会当成"要求调用工具但无工具可用"而报错）。
2. **这一路刻意不下发 `enable_thinking=false`，也不下发 `reasoning_effort`**，即使档位设成「关闭思考」
   （注释 493–502，实测 llama-server b11393 + Qwen3-8B：关掉思考后模型根本不发 `tool_calls`）。

`max_tokens` 由调用方给：`TagAgent.Budget = 2400`（`Core/TagAgent.cs:69`）、
`TagTranslator.ToolBudget = 1600`（`Core/TagTranslator.cs:40`）。**这条路径没有 `ReasoningHeadroom` 追加**。

---

## 3. 工具调用（tool calls）的处理

### 3.1 `ToolTurn` / `ToolCall`

```csharp
public sealed record ToolCall(string Id, string Name, string Arguments);   // 114

public sealed record ToolTurn(bool Ok, string Content, string Reasoning,
                              List<ToolCall> Calls, string Raw, string Error);  // 117-118
```

`Calls` 可空列表（无工具调用时是 `new()`，见 455、523、546）。

### 3.2 `ChatWithToolsAsync` 执行流程

签名：

```csharp
public async Task<ToolTurn> ChatWithToolsAsync(
    AppSettings cfg, string systemPrompt, List<JsonObject> messages,
    IReadOnlyList<AgentTool> tools, int maxTokens,
    CancellationToken ct = default)                                       // 449-452
```

流程（449–537）：

1. `cfg.ApiBaseUrl` 空 → `new ToolTurn(false, "", "", new(), "", "未配置接口地址")`（454–455）。
2. 拼 URL（457）、把 `tools` 包成 OpenAI function 形状（459–470）。
3. 组装 `messages` = system + `messages.DeepClone()`（472–474；**深拷贝，避免污染调用方的历史**）。
4. 拼 body（476–482），按需加 `tools` + `tool_choice="auto"`（487–491）。
5. `SetTimeout(cfg.TimeoutSeconds)`（513）、`SendAsync(..., ResponseContentRead, ct)`（515–516）。
6. 非 2xx → `ToolTurn(false, "", "", new(), raw, "HTTP 4xx ...：<正文前 400 字>")`（519–524）。
7. 2xx → `ParseToolTurn(raw)`（526）。
8. `TaskCanceledException` → 超时文案（528–531）；其他异常 → `ex.Message`（532–536）。
   两条 catch 都**不设 `LastError`**（与 `ChatAsync` 不同，见第 6 节）。

### 3.3 tool_calls 的解析（照抄 558–572）

```csharp
var calls = new List<ToolCall>();
if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
{
    foreach (var tc in tcs.EnumerateArray())
    {
        if (!tc.TryGetProperty("function", out var fn)) continue;
        calls.Add(new ToolCall(
            tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? "" : "",
            fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                ? nm.GetString() ?? "" : "",
            fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String
                ? ar.GetString() ?? "" : ""));
    }
}
```

要点：**`arguments` 原样保存为字符串，绝不在这里 `JsonDocument.Parse`**——拼装/解析交给调用方。
`arguments` 只有在需要 `query` 时才解析：

```csharp
public static string QueryFromArguments(string arguments)              // 596-606
{
    try
    {
        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
            return q.GetString() ?? "";
    }
    catch { /* 参数不是合法 JSON：退回原文，总比丢掉这次调用好 */ }
    return "";
}
```

（注意 catch 注释说"退回原文"，但实际 catch 后 `return ""`——返回的是**空串**，注释与代码不符。
调用方对此有各自处理：`TagTranslator.cs:84` 用 `q.Length == 0 → q = tag` 兜底，
`TagAgent.cs:149-155` 明确**不**回落成整句，而是回一条要求重试的 tool 消息。）

### 3.4 无工具调用时的判定（照抄 579–587）

```csharp
if (calls.Count == 0)
{
    var res = InterpretChoice(content, reasoning, finish, raw);
    return res.Ok
        ? new ToolTurn(true, res.Content, reasoning, calls, raw, "")
        : new ToolTurn(false, "", reasoning, calls, raw, res.Error);
}

return new ToolTurn(true, StripThinking(content), reasoning, calls, raw, "");
```

即：**有工具调用 → 一定 `Ok=true`**（`content` 也要过一遍 `StripThinking`）；
**没有工具调用 → 复用 `ChatAsync` 的同一条判定**（只有思考没答案 = 失败）。

### 3.5 tool result 回填（构造消息的三件套，照抄 609–655）

```csharp
public static JsonObject UserMessage(string text, ChatImage? image)     // 609
public static JsonObject AssistantMessage(string content, List<ToolCall> calls)   // 630-647
{
    var arr = new JsonArray();
    foreach (var c in calls)
        arr.Add(new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
        });

    return new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = content,
        ["tool_calls"] = arr,
    };
}

public static JsonObject ToolMessage(string callId, string content) => new()   // 650-655
{
    ["role"] = "tool",
    ["tool_call_id"] = callId,
    ["content"] = content,
};
```

三条不可动的约定（注释在 629、649 行，`ChatWithToolsAsync` 的类注释 446–447 也强调）：

- **`tool_call_id` 必须原样回灌**；`AssistantMessage` 里 `id` / `arguments` 一律照抄，不改写。
- assistant 消息保留 `content`（可能为空串）**加上** `tool_calls` 数组。
- 顺序必须是 `assistant(tool_calls)` → 若干 `tool(tool_call_id)`。

### 3.6 多轮工具循环的终止条件（循环不在本文件）

`LlmClient` 只提供**一轮**原语，循环在调用方：

**`TagAgent.RunAsync`（`Core/TagAgent.cs:75-209`）**

| 终止条件 | 位置 |
|---|---|
| 轮数上限 `MaxRounds = 4`（`rounds < MaxRounds`） | `TagAgent.cs:62, 99` |
| `!turn.Ok` → 立刻返回失败 | 104–105 |
| `turn.Calls.Count == 0` → 用 `turn.Content` 收尾（空则失败） | 113–120 |
| 收到 `propose` 工具调用 → **立即返回提案**（"提案到手就停"） | 127–144 |
| 未知工具名 → 回一条 tool 消息说明，继续循环 | 175–179 |
| 轮数用完 → **追加一条 user 指令 + 空工具列表**再问一次（`Array.Empty<AgentTool>()`），逼出最终答案 | 183–209 |

**`TagTranslator.TranslateAsync`（`Core/TagTranslator.cs:50-147`）**
`MaxRounds = 3`；`turn.Calls.Count > 0` 则执行搜索、回灌、`continue`；
`rounds >= MaxRounds` 时追加一条"Based on the information above…"的 user 消息再问一次（97–115）。

### 3.7 是否支持流式？—— 不支持

全文件检索 `stream` / `Stream` / `event-stream` / `data:` 只有**一处**命中，且是图片 data URI（133 行）。
`HttpCompletionOption.ResponseContentRead` + `JsonDocument.Parse(整个响应体)` 的结构决定了
**它只消费非流式的完整 JSON 响应**；没有任何 `ReadAsStreamAsync`、`StreamReader`、SSE 分帧、
`[DONE]` 处理或 `delta` 解析。剥离 `reasoning_content` 也是从**完整消息对象**里取（304–305、555–556），
不是从 `delta` 累加。

---

## 4. 推理 / 思考等级

### 4.1 三态模型

| 概念 | 取值 | 代码 |
|---|---|---|
| 档位字符串 | `""`（自动）或八词表之一 | `NormalizeEffort`（881）去空白转小写 |
| 是否关思考 | 只有 `"none"` 为 true | `IsThinkingOff`（890-891） |
| 是否发 `reasoning_effort` | 见下 | `EffortToSend`（913-919） |

```csharp
public static readonly IReadOnlyList<string> EffortVocabulary = new[]        // 875-878
{
    "none", "minimal", "low", "medium", "high", "xhigh", "ultra", "max"
};

public static bool IsThinkingOff(string? effort) =>                          // 890-891
    string.Equals(NormalizeEffort(effort), "none", StringComparison.Ordinal);

public static string? EffortToSend(string? configured, bool effortSupported)  // 913-919
{
    var want = NormalizeEffort(configured);
    if (want.Length == 0) return null;      // 自动 → 不发
    if (!effortSupported) return null;      // 服务端没声明 effort → 不发（不冒 400 风险）
    return EffortVocabulary.Contains(want, StringComparer.Ordinal) ? want : null;  // 词表外 → 不发
}
```

注释里的两条实测事实（868–872、909–911）值得照抄进任何移植版：
① `supported_levels` 是"有意义的档位"，**不是"允许的取值"**，硬约束是那份八词表；
② `reasoning_effort` 是**接口级**的，不是模型级的。

### 4.2 token 余量：`ReasoningHeadroom = 1200`

```csharp
private const int ReasoningHeadroom = 1200;                                  // 332
var budget = thinking ? cfg.MaxTokens + ReasoningHeadroom : cfg.MaxTokens;   // 191
```

`max_tokens` 是**思考 + 答案**的总预算（注释 315–331 附实测表：400 → 长描述下 content 为空、
reasoning 729 字符、`finish_reason=length`）。**只作用于 `ChatAsync`**，工具那条路不加。

### 4.3 思考内容怎么被剥离

两条防线：

**① 从独立字段读，不混进 content**（304–305、555–556）：

```csharp
var reasoning = msg.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String
    ? r.GetString() ?? "" : "";
```

**② `StripThinking` 清内联标记**（354–380）：

```csharp
public static string StripThinking(string s)
{
    if (string.IsNullOrEmpty(s)) return "";
    var t = s;

    // 成对的尖括号标记
    foreach (var name in new[] { "thinking", "think", "thought", "reasoning" })
        t = Regex.Replace(t, "<" + name + @">[\s\S]*?</" + name + ">", "",
                          RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 未闭合的（被截断）：从标记处删到结尾
    t = Regex.Replace(t, "<(" + ThinkNames + ")>" + @"[\s\S]*$", "",
                      RegexOptions.Singleline | RegexOptions.IgnoreCase);
    // 残留的闭合标记
    t = Regex.Replace(t, "</(" + ThinkNames + ")>", "", RegexOptions.IgnoreCase);

    // Qwen3 的 redacted_thinking 块：先删成对块……
    t = Regex.Replace(t, Regex.Escape(ThinkOpen) + @"[\s\S]*?" + Regex.Escape(ThinkClose),
                      "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    // ……剩下的开标记就一定是没闭合的那个，从它删到结尾
    var i = t.IndexOf(ThinkOpen, StringComparison.OrdinalIgnoreCase);
    if (i >= 0) t = t[..i];

    return t.Trim();
}
```

两个实现坑（注释 346–352）：**标记字面量必须拼出来**（内联写会被编辑工具链吃掉，
变成空串后 `IndexOf("")` 恒返回 0 → 整段文本被截空）；**顺序必须是"先删成对块，再处理未闭合"**，
反了会把成对块后面的正文一起删掉。

**③ 铁律：思考永不进结果**（`InterpretChoice`，411–434）：

```csharp
public static ChatResult InterpretChoice(string content, string reasoning,
                                         string finishReason, string raw)
{
    var answer = StripThinking(content ?? "");
    var think = reasoning ?? "";

    if (answer.Length > 0)
        return new ChatResult(true, answer, think, raw, "");

    // 只有思考、没有答案：如实报错，绝不把思考当结果
    if (think.Trim().Length > 0)
    {
        var why = finishReason == "length"
            ? "模型还在思考时就用完了 token 预算（finish_reason=length），答案还没开始写。"
            : "模型只输出了思考过程，没有给出答案。";
        return new ChatResult(false, "", think, raw,
            why + $"本次思考约 {think.Length} 字符。" +
            "请调大「最大 token」，或把「推理档位」设为关闭思考。");
    }

    return new ChatResult(false, "", think, raw, finishReason == "length"
        ? "模型用完了 token 预算（finish_reason=length）却没写出内容，请调大「最大 token」。"
        : "模型返回了空内容");
}
```

注释（395–410）明确记录：早先有一条"content 为空就退回 reasoning"的兜底，**那个判断是错的**，
真实原因是 `max_tokens` 太小被截断；后果是 700 多字思考过程被当成译文写进训练集。

### 4.4 `/models` 能力探测

```csharp
public async Task<(bool Ok, List<ModelInfo> Models, string Error)> ListModelsAsync(
    AppSettings cfg, CancellationToken ct = default)                     // 744-792
{
    LastError = "";
    if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) return (false, new(), "未配置接口地址");
    var url = cfg.ApiBaseUrl.TrimEnd('/') + "/models";                   // 750
    var timeout = Math.Min(30, Math.Max(5, cfg.TimeoutSeconds));         // 753
    ...
    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);   // 761  GET
```

**关键语义**：`Ok` 与 `Models.Count` 是两件事——HTTP 成功但列表为空是合法结果
（有些网关返回 `{"object":"list","data":[]}`），此时应当照常让用户手填模型名（注释 737–743）。
非 2xx 时把正文前 300 字附进错误（401/403 的正文常写明原因，766–768）。

解析链（四种真实形状，936–974）：

```csharp
public static bool TryParseModels(string raw, out List<ModelInfo> models)   // 947-974
{
    models = new List<ModelInfo>();
    if (string.IsNullOrWhiteSpace(raw)) return false;

    JsonDocument doc;
    // 只吞解析异常：里面的走查若出错应当暴露出来，而不是被悄悄当成"空列表"
    try { doc = JsonDocument.Parse(raw); }
    catch (JsonException) { return false; }

    using (doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) { AddModels(root, models); return true; }
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            AddModels(data, models);          // 标准字段优先
        else if (root.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
            AddModels(ms, models);

        return true;
    }
}
```

`AddModels`（976–1023）逐字段映射（两套互不相同的非标准扩展）：

```csharp
var id = Str(el, "id") ?? Str(el, "name") ?? Str(el, "model");         // 988
var name = Str(el, "name");
if (string.Equals(name, id, StringComparison.Ordinal)) name = null;    // 1000 name 与 id 逐字符相同则丢弃

var ctx = Int(el, "context_window");                                    // 1004-1005
if (ctx <= 0 && hasMeta) ctx = Int(meta, "n_ctx");

var effort = el.TryGetProperty("effort", out var ef) && ef.ValueKind == JsonValueKind.Object
    ? ef : default;                                                     // 1007-1008

sink.Add(new ModelInfo(
    id.Trim(),
    hasMeta ? Str(meta, "ftype") ?? "" : "",                            // llama-server: meta.ftype
    ctx,
    hasMeta ? Long(meta, "size") : 0L,                                  // llama-server: meta.size
    StrArray(el, "aliases"),
    name?.Trim() ?? "",
    Int(el, "max_output_tokens"),
    StrArray(el, "input_modalities"),                                   // 云服务: 三态图像能力
    effort.ValueKind == JsonValueKind.Object ? StrArray(effort, "supported_levels") : null,
    effort.ValueKind == JsonValueKind.Object ? Str(effort, "default_level")?.Trim() ?? "" : ""));
```

收尾去重排序（`DedupeModels`，1054–1064）：`OrdinalIgnoreCase` 去重 + 忽略大小写排序
（注释 1051–1052：不用区域敏感比较，否则土耳其语 I/ı 会让排序因机器语言而异）。
解析出的字段用 `Describe()`（710–728）渲染成一行给人看的说明。

### 4.5 能力缓存（`_caps` / `_capTried` / `EnsureCapsAsync`）

```csharp
private readonly Dictionary<string, ModelInfo> _caps = new(StringComparer.OrdinalIgnoreCase);   // 802
private readonly HashSet<string> _capTried = new(StringComparer.OrdinalIgnoreCase);             // 805

internal static string CapKey(string baseUrl, string model) =>                                   // 807-809
    (baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant() + "|" +
    (model ?? "").Trim().ToLowerInvariant();

public ModelInfo? Caps(AppSettings cfg)                                                          // 826-830
{
    lock (_caps)
        return _caps.TryGetValue(CapKey(cfg.ApiBaseUrl, cfg.Model), out var m) ? m : null;
}

public async Task<ModelInfo?> EnsureCapsAsync(AppSettings cfg, CancellationToken ct = default)   // 838-859
{
    var existing = Caps(cfg);
    if (existing is not null) return existing;

    var key = CapKey(cfg.ApiBaseUrl, cfg.Model);
    lock (_caps) { if (_capTried.Contains(key)) return null; }
    lock (_caps) { _capTried.Add(key); }

    try
    {
        var (ok, models, err) = await ListModelsAsync(cfg, ct).ConfigureAwait(false);
        if (!ok) Log.Write("caps: /models 拉取失败（按未知处理）：" + err);
        else if (models.Count == 0) Log.Write("caps: /models 没报模型（按未知处理）");
        else Log.Write($"caps: {cfg.Model} 能力已获取（{models.Count} 个模型）");
    }
    catch (Exception ex)
    {
        Log.Write("caps: 拉取异常（按未知处理）：" + ex.Message);
    }
    return Caps(cfg);
}
```

设计要点（注释 794–838）：**只在内存，不落盘**；键必须带 base url（同一个模型名在本地网关与云端是两回事）；
**每个键一辈子只试一次**（含失败），所以不实现 `/models` 的网关不会让每次翻译多等一次超时；
`null` 不等于"不支持"，只表示"不知道"。

三态图像能力（698–701）+ 附图的真值表（929–930）：

```csharp
public bool? SupportsImage =>
    InputModalities is null or { Count: 0 }
        ? null
        : InputModalities.Any(m => string.Equals(m, "image", StringComparison.OrdinalIgnoreCase));

public static bool CanAttachImage(bool? supportsImage, bool hasImage) =>
    hasImage && supportsImage != false;      // null（不知道）放行
```

调用侧一致：`MainWindow.xaml.cs:4166` 用它决定复选框可用性，`MainWindow.xaml.cs:4452-4455`
在 `SupportsImage == false` 时跳过读图并发日志说明。

---

## 5. 消息组装（带图）

```csharp
public static JsonObject UserMessage(string text, ChatImage? image)      // 609-627
{
    if (image is null)
        return new JsonObject { ["role"] = "user", ["content"] = text };

    return new JsonObject
    {
        ["role"] = "user",
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
            new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = image.DataUri },
            },
        },
    };
}
```

- **格式：data URI 内联**，不是先上传、也不是文件引用：

```csharp
public string DataUri =>                                                  // 132-134
    "data:" + (string.IsNullOrWhiteSpace(MimeType) ? "image/jpeg" : MimeType) +
    ";base64," + Convert.ToBase64String(Bytes);
```

- **没有 `detail` 字段**（全文件无 `"detail"`），也没有 `max_pixels` / `image_url.detail = "low"` 之类的压缩提示。
- MimeType 缺省回落 `"image/jpeg"`。实际生产值是 `ImagePayload.Mime = "image/jpeg"`（`Core/ImagePayload.cs:28`）。
- **无图时 `content` 是纯字符串**（不是单元素数组）——两条分支形状不同，是刻意的。
- 图片字节的**缩减责任在调用方**：注释 127–128 写明原图可达 8.4 MB（3258×3978）、base64 再涨 1/3，
  必须先缩到长边 1024 量级。真实实现是 `ImagePayload.LoadAsync`（**用 WinRT `BitmapDecoder`/`BitmapEncoder`
  缩到长边 1024、质量 0.85、尊重 EXIF 方向**，`Core/ImagePayload.cs:37-88`）——这是**唯一真正的图像侧 UI/平台耦合点**，
  但它不在 `LlmClient.cs` 里。
- 历史里**图片只挂在最后一轮**：`TagAgent.cs:90-92` 用 `isLast ? image : null`
  （注释 71–73：把图挂到每一轮会让后面每次请求都重复付一次图片的 token）。

---

## 6. 错误处理与重试

### 6.1 总原则

`ChatAsync` 的 XML 注释（169 行）：**"发一次 chat/completions。永不抛异常，错误放在返回值里，便于 UI 直接显示。"**
所有路径都返回 record，`Ok=false` + `Error` 承载信息；同时 `LastError` 记录最后一条错误（43 行）。

### 6.2 `ChatAsync` 的三个 catch（250–287）

```csharp
catch (TaskCanceledException)                                                     // 275-280
{
    LastError = "请求超时";
    return new ChatResult(false, "", "", "", "请求超时（" + cfg.TimeoutSeconds + " 秒）。" +
        "本地模型首次请求要编译 SYCL/CUDA kernel，可能较慢，可调大超时。");
}
catch (Exception ex)                                                              // 281-286
{
    LastError = ex.Message;
    Log.Write("llm request failed: " + ex);
    return new ChatResult(false, "", "", "", ex.Message);
}
```

注意 `TaskCanceledException` 这一支**无法区分"超时"与"调用方取消"**——
`ct` 被取消时 `SendAsync` 也抛 `TaskCanceledException`，会被报成"请求超时"。

### 6.3 非 200（265–271）

```csharp
if (!resp.IsSuccessStatusCode)
{
    var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";                  // 267
    LastError = msg;
    Log.Write($"llm {msg} url={url} body={Truncate(raw, 600)}");                    // 269
    return new ChatResult(false, "", "", raw, msg + "：" + Truncate(raw, 400));     // 270
}
```

`Truncate`（1089）：`s.Length <= n ? s : s[..n] + "…"`。`Raw` 字段保留**完整**响应体（截断只用于 `Error`）。

### 6.4 200 但结构不对（`ParseResponse`，289–313）

```csharp
if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
    choices.GetArrayLength() == 0)
    return new ChatResult(false, "", "", raw, "响应里没有 choices");               // 294-296
...
catch (Exception ex)
{
    return new ChatResult(false, "", "", raw, "响应解析失败：" + ex.Message);      // 309-312
}
```

字段级防御：`finish_reason`、`content`、`reasoning_content` 全部 `TryGetProperty` + `ValueKind == String`
判定（299–305）；`choice.GetProperty("message")`（301）**是唯一没保护的取属性**，缺 `message` 会抛
→ 落到 `catch` → "响应解析失败：…"。`ParseToolTurn` 同样结构（543–551，`message` 也是裸 `GetProperty`）。

### 6.5 降级路径 —— 关于 SSE

**没有 SSE 解析，因此也没有"SSE 解析失败"的降级路径。**请求体不带 `stream`，
响应体按完整 JSON 解析（`JsonDocument.Parse(raw)`）。所谓降级只有三条：

| 情形 | 降级行为 |
|---|---|
| `/models` 拉不到 / 拉不回模型 | 按"能力未知"处理，`CanAttachImage(null, true) == true` 放行、`EffortToSend(x, false) == null` 不发档位（850–851、926–928） |
| `/models` 报的 `data` 不是数组 | `TryParseModels` 返回 **true**（形状可识别）但列表为空（967–972；`tests/Models.cs:104-106` 钉住） |
| 响应里只有思考、没有答案 | **不降级**，如实报错并建议调大 token 或关闭思考（421–429）——这是唯一被明确禁止的"兜底" |

### 6.6 重试

**`LlmClient.cs` 内部没有任何重试/退避逻辑。**全文件 `retry` 只出现在注释里（37 行，讲
`HttpClient.Timeout` 的坑）。真正存在的"重试"在调用方：

- `ChatAsync` **不会**因 5xx/网络错误自动重发。
- `MainWindow.xaml.cs:4796-4824`：散文含 CJK 时，① 点名那几个字的提示重试一次，
  ② 再失败改成"copy editor"整句改写一次，③ 仍失败才如实告知用户。
- 工具循环的"重试"是**模型侧**的：未知工具名/空 query 时回一条 tool 消息让它下一轮改（`TagAgent.cs:149-155, 175-179`）。

### 6.7 `SetTimeout` 的容错

`InvalidOperationException`（首个请求已发出 / 同值重复赋值）被吞掉并记日志（1082–1086），
**不让整次调用失败**——这是注释 32–40 记录的实战坑。

---

## 7. 依赖与 UI 耦合（移植评估）

### 7.1 `using` 清单（第 1–6 行，逐条）

```csharp
using System.Net.Http;            // HttpClient / HttpRequestMessage / HttpCompletionOption
using System.Net.Http.Headers;    // AuthenticationHeaderValue
using System.Text;                // Encoding / UTF8Encoding
using System.Text.Json;           // JsonDocument / JsonElement / JsonValueKind / JsonException
using System.Text.Json.Nodes;     // JsonObject / JsonArray
using System.Text.RegularExpressions;   // Regex / RegexOptions
```

`namespace AnimaCaptioner.Core;`（8 行，**file-scoped**）。

### 7.2 未 using 但使用的类型

`List<T>` / `Dictionary<K,V>` / `HashSet<T>` / `IReadOnlyList<T>` / `StringComparer` / `Convert` /
`TimeSpan` / `Timeout` / `Math` / `Task` / `IDisposable` —— **全部来自 `ImplicitUsings`**
（`AnimaCaptioner.csproj:31`、`tests/corecheck.csproj:15` 都是 `<ImplicitUsings>enable</ImplicitUsings>`）。
移植到"关掉 implicit usings"的工程时，需要显式补
`System`、`System.Collections.Generic`、`System.Linq`、`System.Threading`、`System.Threading.Tasks`。
`SplitKeywords`（658）用了 LINQ 的 `.Select/.Where/.ToList`，需要 `System.Linq`。

### 7.3 外部类型依赖（只有三个）

| 类型 | 定义处 | 用途 | 移除难度 |
|---|---|---|---|
| `AppSettings cfg` | `Core/AppSettings.cs:11` | 参数载体：`ApiBaseUrl` / `ApiKey` / `Model` / `MaxTokens` / `Temperature` / `TimeoutSeconds` / `ReasoningEffort` | 换成 `record LlmOptions(string BaseUrl, string ApiKey, string Model, int MaxTokens, double Temperature, int TimeoutSeconds, string Effort)` 即可，**纯数据** |
| `AgentTool` | `Core/AgentKit.cs:7` — `public sealed record AgentTool(string Name, string Description, JsonObject Parameters)` | `ChatWithToolsAsync` 的 `tools` 参数 | **纯 record，直接搬或内联** |
| `Log.Write(string)` | `Log.cs:18`（`internal static class Log`，`namespace AnimaCaptioner`） | 18 处诊断日志（行 192/233/238/242/246/269/284/522/534/770/775/778/789/850/851/852/856/1085） | **纯 `File.AppendAllText`，无 UI**；换成 `ILogger` / `Console.WriteLine` 即可 |

**没有**引用：`Microsoft.Data.Sqlite`（那是 `VocabDb` 用的）、`Microsoft.UI.*` / `Microsoft.WindowsAppSDK`、
`Windows.*`（WinRT）、`Markdig`、`AnimaCaptioner` 根命名空间的任何 UI 类型。
**不引用 DPAPI**——`LlmClient` 只读 `cfg.ApiKey`（明文），加解密在 `ModelProfile`/`DpapiSecret` 里，
完全是另一条链（`Core/ModelProfile.cs:52-64`、`Core/DpapiSecret.cs`）。

### 7.4 UI 耦合点清单

**`LlmClient.cs` 内部：0 处。** 具体核对：

| 检查项 | 结果 |
|---|---|
| `using Microsoft.UI.*` / `Windows.*` / `WinRT` | 无 |
| WinUI 控件、`DispatcherQueue`、`Brush`、`Window`、XAML 类型 | 无 |
| `Windows.Storage` / `BitmapDecoder` / 文件对话框 | 无 |
| `[DllImport]` / P/Invoke | 无 |
| SQLite / 数据库 | 无 |
| 静态单例、UI 线程假设、`SynchronizationContext` 依赖 | 无（全程 `ConfigureAwait(false)`） |
| 抛异常给 UI | 无（"永不抛异常"，全部装进返回值） |

**结论：`LlmClient.cs` 可以整文件零改动丢进一个纯 `net8.0` / `net10.0` console 类库**——
前提是同时提供 `AppSettings`（或换掉参数类型）、`AgentTool`、`Log` 三个类型。
仓库自己已经这么干了：`tests/corecheck.csproj:4-7` 明确写着

```
<!-- 与应用同一个 TFM 代际（应用是 net10.0-windows10.0.19041.0）。
     刻意不带 -windows 后缀：这里编译的 Core 源码是纯 .NET，唯一的平台相关点
     DpapiSecret 走 P/Invoke 而不是 Windows 专用 API 包。 -->
<TargetFramework>net10.0</TargetFramework>
```

且 `tests/corecheck.csproj:36` 就是 `<Compile Include="..\Core\LlmClient.cs" />` ——
**同一个文件在纯 `net10.0` 下已经在编译并运行回归测试**（`tests/Models.cs`、`tests/Reasoning.cs`、
`tests/Agent.cs` 直接对着 `LlmClient.ParseModels` / `StripThinking` / `InterpretChoice` 等断言）。
这就是"它能被搬到 console 项目"的既成事实证据。

### 7.5 真正的耦合点全在**调用方**（搬 UI 功能时才需要处理）

| 耦合点 | 文件:行 | 性质 | 搬迁做法 |
|---|---|---|---|
| `ImagePayload.LoadAsync` 读图/缩图 | `Core/ImagePayload.cs:1-4, 37-88` | **真 WinRT**：`Windows.Graphics.Imaging.BitmapDecoder/BitmapEncoder/BitmapTransform`、`Windows.Storage.Streams` | 换 `ImageSharp` / `System.Drawing.Common`（Windows-only）/ `SkiaSharp`；产出的仍只是 `new LlmClient.ChatImage(Mime, bytes)`，**接口不变** |
| `LlmClient` 实例的生命周期 | `MainWindow.xaml.cs:22`、`TagSearchWindow.xaml.cs:39`、`SettingsDialog.xaml.cs:15` | UI 类各持有一个 `LlmClient`（`IDisposable`，`MainWindow` 负责释放） | console 里做成 DI 单例或 `using` |
| 模型列表喂给下拉框 | `SettingsDialog.xaml.cs:1217-1296` | `ListModelsAsync` 的结果直接进 `ComboBox.Items`、`DispatcherQueue.TryEnqueue` 补一次同步 | 只取 `(Ok, Models, Error)` 三元组，UI 部分重写 |
| 能力探测结果改 UI 可用性 | `MainWindow.xaml.cs:4166`、`4452-4455` | `CanAttachImage(caps?.SupportsImage, hasImage)` / `Caps(cfg)?.SupportsImage == false` | 保留这两个纯函数即可 |
| 用户提示文案 | `SettingsDialog.xaml.cs:1127-1180` | 根据 `IsThinkingOff` / 档位显示提示行 | 纯字符串逻辑，可复制 |
| 散文 CJK 重试 | `MainWindow.xaml.cs:4796-4824` | 三轮调用 `ChatAsync` + UI 提示 | 逻辑本身无 UI，抽出来即可复用 |

### 7.6 移植到纯 net8/net10 console 的建议改动（最小集）

1. **参数类型解耦**：`ChatAsync` / `ChatWithToolsAsync` / `ListModelsAsync` / `Caps` /
   `EnsureCapsAsync` 现在都吃 `AppSettings`。引入一个窄接口或 record（只含
   `ApiBaseUrl` / `ApiKey` / `Model` / `MaxTokens` / `Temperature` / `TimeoutSeconds` / `ReasoningEffort`），
   或在你的工程里照抄一份等价的 `AppSettings`（那 7 个属性以外的部分 LLM 路径一概不碰）。
2. **`AgentTool` 直接搬**（`Core/AgentKit.cs:7`，一个 `sealed record`，只依赖 `JsonObject`）。
   若不想引入 `System.Text.Json.Nodes` 的另一份定义，把 `ChatWithToolsAsync` 的 `tools` 参数
   就地改成 `IReadOnlyList<(string Name, string Description, JsonObject Parameters)>`。
3. **`Log` 换掉**（11 处调用）。或者照搬那份 41 行的实现——它只依赖 `File` / `Directory` /
   `FileInfo` / `DateTime`，**没有任何 UI 依赖**（`Log.cs:1-41`）。
4. **`ImplicitUsings`**：若目标工程关闭了它，补 `using System; using System.Collections.Generic;
   using System.Linq; using System.Threading; using System.Threading.Tasks;`。
5. **TFM**：`net8.0` 起即可。用到的 API 都很保守
   （`HttpClient` / `StringContent` / `AuthenticationHeaderValue` / `JsonObject` / `Regex` /
   `Convert.ToBase64String` / `string[..n]` 范围切片 → 需要 C# 8+；`record` / 目标类型 `new()` → C# 9+；
   `is not null` / `or { Count: 0 }` 列表模式 → C# 9/11）。**注意**：`InputModalities is null or { Count: 0 }`
   （699 行）用了 C# 11 的扩展属性模式，`net8.0` 配 `LangVersion latest` 没问题，但若把
   `LangVersion` 降到 9 需要改写这一处。
6. **UI 侧另做**：图片编码（`ImagePayload`）和界面呈现**不在这个文件里**，
   所以"把 LLM 客户端搬进 console"这件事**不需要**碰 WinRT 代码；反过来，如果想要"带图"能力，
   就必须在 console 侧自备一个 JPEG 缩放+编码器（源文件里那份是 WinRT 的）。

---

## 8. 移植风险评估（一句话结论）

| 项 | 评估 |
|---|---|
| 能否直接编译进纯 net8/net10 console？ | **能**。仓库自己的 `tests/corecheck.csproj` 就是 `net10.0` 且直接 `<Compile Include="..\Core\LlmClient.cs" />`（第 7、36 行）。 |
| 需要额外提供什么？ | `AppSettings`（或窄参数类型）、`AgentTool`、`Log` —— 三个**均无 UI 依赖**的类型。 |
| HttpClient 行为差异 | 无自定义 handler，移植后行为一致；但注意 `Timeout` 只能在首个请求前设置，且**同值重复赋值也会抛**（1082 行）。 |
| 流式 | 本来就没有，别期待；要加得自己重写发送与解析两处。 |
| 最大隐患 | ① `QueryFromArguments` 注释与实现不符（catch 后返回 `""`）；② `SearchToolDescription`（164 行）是**无人引用的死常量**；③ `TaskCanceledException` 无法区分超时与主动取消；④ 中文经 `body.ToJsonString()` 无 options 会被 `\uXXXX` 转义（语义无害，只是日志/抓包可读性差）。 |
