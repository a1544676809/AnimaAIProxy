# AnimaAIProxy

一个**标准 OpenAI API 代理**：给其他 agent 平台接入，在**不影响接入方自身工具调用**的前提下，
额外提供由本代理在本地执行的工具 —— 主要是 Danbooru/Anima 标签词库查询。

思考等级（`reasoning_effort`）、`temperature`、`top_p`、`response_format`、
`chat_template_kwargs` 等字段**一律原样透传**，代理不劫持。

## 设计要点

| 关注点 | 做法 |
| --- | --- |
| 接入方的工具 | 一个不动。代理只在模型调用**自有**工具时接管；调用接入方工具时原样交还，`finish_reason` 保持 `tool_calls` |
| 自有工具调用 | 摘干净，绝不回给接入方；`finish_reason` 改回 `stop` |
| 多轮循环 | 对接入方**不可见**（影子历史）。usage 跨轮累加，账单对得上 |
| 系统提示 | 追加工具说明，**不替换**接入方的系统提示 |
| 重名工具 | 代理让路加后缀，绝不覆盖接入方的同名工具 |
| 思考字段 | 透传；工具路径按上游实测约束处理，不篡改用户设定 |
| 轮数用完的收尾 | **只关代理自己的工具**，接入方的工具原样保留 —— 那是它的能力，不是我们的 |
| 上游出错 | 状态码照抄上游（不把 429/401 包成 200 + error 体），不重试、不静默降级 |
| 认证边界 | 客户端只拿代理签发的 `aip-` 令牌；上游密钥永不出现在发给客户端的内容里 |

## 端点

| 端点 | 行为 |
| --- | --- |
| `GET /health` | 存活探测。**刻意不鉴权**（监控系统没有令牌），返回体里只有存活与词库就绪状态，不含上游地址/令牌数/路径 |
| `POST /v1/chat/completions` | 主端点。`stream=false` → 透明注入工具并本地执行；`stream=true` → 纯透传（这一阶段不做注入，见下） |
| `GET /v1/models` | 上游模型表；上游不可用时回退到配置里的模型映射表。配置里映射出来的名字会被补进列表（平台启动时会拿它校验配置） |
| `/v1/**` 其它 | 原样透传（embeddings、completions……）。**刻意不逐个列端点**：OpenAI 兼容服务的端点集合一直在变，逐个列等于把自己焊死在一个快照上 |

### 流式为什么这一阶段不注入

要在 SSE 的增量 delta 上做工具循环，就得先把整条流读完再自己合成一条假流给客户端，
首字延迟等于整段回答的时长 —— 流式的全部意义就没了。所以 `stream=true` 时如实纯透传，
**并在日志里写清"这次没注入工具，因为客户端要的是流式"**（否则用户会以为代理挂了工具，
而实际上这次一个都没挂）。

## 目录

- `Core/` —— 代理逻辑层（纯 .NET，零 WinUI 依赖）
  - `ProxyConfig` / `Secrets`(DPAPI) / `ProxyLog` / `TokenStore`
  - `ToolCatalog`（工具声明） / `VocabService` / `ToolHost`（对真实词库执行）
  - `ChatWire`（请求/响应读写） / `ToolLoop`（透明工具循环） / `UpstreamClient`
  - `ProxyServer`（Kestrel 端点、令牌鉴权、来源过滤、流式透传）
- `Vendor/AnimaCaptioner/` —— 从 [AnimaCaptioner] 搬入的词库内核（零 WinUI / Windows API 引用）
- `tests/` —— `net8.0` 纯 .NET 回归工程。它同时是一道**守卫**：Core/ 或 Vendor/ 一旦混进 WinUI 类型就编译不过
- `docs/` —— 上游复用面的技术勘察报告（LlmClient、VocabDb）

## 回归测试

```powershell
dotnet run --project tests\proxycheck.csproj all
```

分段：`vocab` | `tools` | `loop` | `merge` | `tokens` | `server` | `e2e` | `all`

退出码：`0` 全通过 / `1` 有断言失败 / `2` **没能验证**（缺词库等外部输入，与"通过"区分开）。

词库路径解析顺序：`--db=<path>` > 环境变量 `AIP_VOCABDB` > AnimaCaptioner 的 `settings.json`。

`server` 与 `e2e` 两段会**真的起 Kestrel、真的发 HTTP 请求**，但：

- 令牌库指向临时目录（否则跑一次回归就把用户发给各平台的真令牌全部作废）；
- `server` 的上游指向一个死端口（测的就是"连不上时回什么"）；
- `e2e` 的上游是**本进程内第二个 Kestrel**（桩上游），按剧本回话并记下收到的请求体 ——
  所以它验的是完整链路：工具声明真的进了发往上游的报文、工具结果真的回到了模型手上、
  最终交给客户端的那份 JSON 里我们的 `tool_call` 摘干净了、usage 跨轮累加了、
  **流式是真的分块回来的**（断言"首块到达时间远早于总耗时"，缓冲会当场失败）。
  它不需要网络。

`tests/last-run.txt` 是**手工留存**的快照（没有任何代码会写它），给手上没有 113 MB 真实词库的人看。

## 构建

```powershell
# 应用（WinUI，必须显式给平台，否则 MSIX 打包会拒绝 AnyCPU）
dotnet build AnimaAIProxy.csproj -p:Platform=x64

# 回归（快得多，且不需要 Windows SDK）
dotnet build tests\proxycheck.csproj
```

## 当前状态

**已完成**：词库内核 vendored、配置/密钥/日志/令牌、工具层、透明工具循环、上游客户端、
Kestrel 端点与鉴权、离线回归、**端到端联调**。

回归现状：**255 项断言全过 / 0 失败**（含 113 MB 真实词库、一次真实 Kestrel 起停、
以及一次完整的「客户端 → 代理 → 桩上游」链路，非流式注入与流式分块透传都在内）。

**未完成**：WinUI 托盘外壳（启停开关、上游配置、令牌管理、实时日志、词库状态）。
`ProxyServer` 已经可以被界面直接驱动（`StartAsync` / `StopAsync` / `BaseUrl` / `IsRunning`），
`ProxyLog.Written` 事件也已经给实时日志面板留好了。

**已验证的关键风险**：

- Kestrel 可在 WinUI 进程内运行并被外部客户端访问（含 SSE 回流）。
- 词库内核在纯 `net8.0`（不带 `-windows`）下编译并查询真实 113 MB 词库成功。

## 词库中文解析的一个已知边界

`Vendor/AnimaCaptioner` 的 `ResolveChinese` 对**人物 / 作品名**整体是不可用的，这是它的设计：

1. 精确键只认库里逐字存的那一个中文名 —— 331,836 条中文名里 **163,154 条（49%）**
   带「（限定词）」后缀，`cn='初音未来'` 精确匹配 **0 条**；
2. 就算连着括号一起打，`character` / `artist` / `copyright` 也会被 `JunkCategories` 主动拒掉。

代理**不去绕过它**（绕过会让 `少女` 解析到 `girl (mokufuu)` 这类角色标签）。
代理只补上一条收窄的回落：`danbooru_resolve_tag` 在内核落空后回落到 `Search`，但

- 只认"同名"或"同名 + 括号限定"，不接受任意模糊命中；
- 形状对上之后还把完整中文名交回 `ResolveChinese` 再审一次（类别由内核裁，不抄黑名单）；
- 只认**用得最多的**那个同名变体，第一条过不了审就放弃（否则 `初音未来` 会退而挑到
  `初音未来（Cosplay）`，变成一个自信的错答案 —— 比查不到更糟）。

净效果：补上 **11,138 条**"裸形式不是精确键、类别不是 junk"的中文概念
（`弓` → `bow (weapon)`、`箭头` → `arrow (symbol)`），人物/作品名则如实报 NOT FOUND
并把模型引向 `danbooru_search_tags`（那条路 `cn.StartsWith` 是好的）。

[AnimaCaptioner]: https://github.com/a1544676809/AnimaCaptioner
