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
| 关窗口 | **收进托盘，不退出**。这个程序的正事是在后台持续服务；关窗口就把服务停掉，等于"我瞄一眼状态"会打断接入方的 agent。真正退出走托盘菜单 |

## 界面

单窗口 + 托盘图标：

- **顶部状态条**：运行状态、Base URL（一键复制）、启动/停止、保存设置
- **模型提供商**：多个站点（增删改、逐个启用/停用、切换默认）、每个站点各自的地址、
  DPAPI 加密的密钥、每站点自定义请求头、是否自动拉取模型、一键刷新模型
- **模型**：聚合各启用站点的模型，逐个开关对外提供；手工添加条目；模型名映射
- **工具注入**：总开关、是否写进系统提示、是否回写结果、轮数上限、单次调用上限
- **联网搜索**：给模型 `web_search` 工具（后端选择、地址、DPAPI 密钥、TLS SNI/Host、条数、超时），带一键**测试搜索**
- **监听**：端口、允许范围、是否要求令牌、是否记录请求体
- **代理令牌**：签发（明文只显示一次）、启用/停用、删除
- **词库**：状态、路径、重新载入
- **实时日志**：订阅 `ProxyLog.Written`、清空、打开日志文件 / 配置目录

界面上的文案遵循一条约定：**只显示用户需要的信息，不显示内部标识符**。
配置里的 `BindScope` 在界面上是"仅本机 / 局域网 / 任意来源"，词库状态值
（内核的 `anima`/`valid`/`retired`/`unknown`）在日志摘要里是中文说法，而
**异常调用栈只进日志文件、不进面板**（`ProxyLog.WriteException` 把摘要给面板、
全文给文件）——面板里那几十行 `at System...` 只会把有用的三两行挤出去。
协议字段（模型看到的 `status=anima`）保持英文原样，翻译只发生在显示这一层。

托盘右键：显示窗口 / 启动代理 / 停止代理 / 退出。**左键单击图标 = 把窗口叫回来**。
鼠标悬停显示当前状态与 Base URL。

### 托盘菜单必须用 `Command`，不能用 `Click`

这是一个**看着完全正常、实际完全没接上**的坑，已实测确认：H.NotifyIcon 在 WinUI 下的
默认 `ContextMenuMode` 是 `PopupMenu`——它不渲染我们的 `MenuFlyout`，而是照着它建一个
**Win32 弹出菜单**，点击时调用每个 `MenuFlyoutItem` 上的 `Command`（库自己的 README
原话：*"will call the commands attached to your MenuFlyoutItem"*）。

绑 `Click` 的话：菜单照常弹出、每一项都能点、也有按下高亮，**但没有任何一行代码会执行**。
实测点「停止代理」之后服务照旧运行，日志里连一条记录都没有。所以菜单项的接线放在
`WireTrayCommands()` 里（不用 `x:Bind`：`MenuFlyout` 不在可视树里，而 `Window` 又不是
`FrameworkElement`，两种声明式写法在这里都靠不住）。

顺带修掉的三处：
- **左键原本什么都不做**（`MenuActivation=RightClick`，又没设 `LeftClickCommand`）。
  用户点一下毫无反应，就会判定"托盘是坏的"——而左键是托盘图标最常被点的那个键。
- **最小化过的窗口 `AppWindow.Show()` 不会还原**，得让 presenter `Restore()`。
- **失败是静默的**：Win32 菜单回调里抛出的异常传不回我们的代码。现在每个托盘动作都
  经 `RunTray()`——先回 UI 线程、再执行、成功与失败都留一行日志。

## 端点

| 端点 | 行为 |
| --- | --- |
| `GET /health` | 存活探测。**刻意不鉴权**（监控系统没有令牌），返回体里只有存活与词库就绪状态，不含上游地址/令牌数/路径 |
| `POST /v1/chat/completions` | 主端点。`stream=false` 与 `stream=true` **都**透明注入工具并本地执行；流式下正文增量实时转发（见下） |
| `GET /v1/models` | **只列"启用站点的启用模型"**，另加模型名映射的键。站点若开了自动拉取且还没拉过，这一次会按需拉一次并记下时间。平台启动时拿它校验"我配的模型名存在吗"，所以少报一个会让平台判定配置非法、多报一个会让它选到用不了的模型 |
| `/v1/**` 其它 | 原样透传（embeddings、completions……）。**刻意不逐个列端点**：OpenAI 兼容服务的端点集合一直在变，逐个列等于把自己焊死在一个快照上 |

### 流式也注入工具（怎么做到不损首字延迟）

早先 `stream=true` 是纯透传，理由是"要在增量 delta 上做工具循环，就得整条读完再
合成一条假流，首字延迟等于整段回答时长"。那个理由对"整条读完"成立，但它不是唯一
做法。现在是：

- **正文增量实时转发。** 收到一个 `delta.content` 就立刻写给客户端并 flush。
  首字延迟没有任何损失 —— 这一点有断言钉着（"第一个正文块就是第一轮的正文"，
  以及"首块到达时间远早于总耗时"，缓冲会当场失败）。
- **工具调用增量按轮缓冲。** `delta.tool_calls` 不转发，先按 `index` 拼成完整调用
  （`arguments` 在流里是**分片**下发的，不拼完整也执行不了）。一个调用在被裁定
  "是我们的还是客户端的"之前，转发出去就是错的 —— 我们自己的调用绝不能让客户端看见。
- **只有最终那一轮才收尾。** 中间轮不发 `finish_reason`、不发 usage、不发 `[DONE]`。
  客户端看到的是一条连续的 assistant 消息，就好像模型自己查完资料再一路说下来。

于是多轮在客户端眼里是**拼起来的**：`第1轮正文` + `第2轮正文`。这与"客户端自己做
工具循环"时看到的形状一致，只是少了它自己那一段。usage 跨轮累加后只报一次
（每轮都报是错的：客户端会在还没结束时就拿到一份"最终用量"）。

## 联网搜索（`web_search`）

模型可以自己决定上网查。工具名是 `web_search`（**不带 `danbooru_` 前缀**：它查的是
整个互联网，名字里放 danbooru 会误导模型）。接入方自己也有 `web_search` 时，
**它留下、我们改名**（`web_search_aip`），与其它工具同一条让路规则。

后端五种，代码移植自 AnimaCaptioner 的 `SearchClient`（同一批后端、同样的实测结论）：

| 后端 | 需要什么 | 说明 |
| --- | --- | --- |
| `searxng` | 实例地址 | 直连 JSON 接口。实例要在 `settings.yml` 的 `search.formats` 里加 `json`，否则回 403 |
| `tavily` / `brave` / `serper` | 只要 API Key | 官方域名固定；认证头分别是 `Bearer` / `X-Subscription-Token` / `X-API-KEY` |
| `shim` | 地址 + 密钥 + SNI/Host | Anthropic 兼容的搜索网关（`POST /v1/messages` + `web_search_20250305`） |

三条设计约束：

1. **没配到能用就不下发这个工具。** 判据是 `ProxyConfig.SearchReady`（后端、地址、
   密钥按后端类型逐项判齐）。下发一个必然失败的工具，模型会白烧一轮预算、拿到一句
   "搜索不可用"，然后可能改去凭记忆编答案——比一开始就没有它更糟。
2. **故障与"没结果"必须分开说。** 前者是"这次查不了"，后者是"网上没这东西"。
   混成一句，模型会把一次网络故障当成"这个概念不存在"。失败时的工具结果里带
   `error` 字段并明写 *"does NOT mean the thing you asked about does not exist"*。
3. **回灌内容必须截断**（单条 400 字、总量 2400 字）。把整页正文塞回去会把用户真正
   问的东西挤出上下文，反而答得更差。

### shim 那条路的两个实测细节（踩过，非试不可）

- **必须覆盖 TLS SNI。** 反向代理按 SNI 选站点，直连 IP 时 SNI 是那个 IP，代理找不到
  站点，直接回 `TLS alert: InternalError`。`SocketsHttpHandler.SslOptions.TargetHost`
  **不改变 SNI**；只有自己建 `SslStream` 并 `AuthenticateAsClientAsync(TargetHost=…)`
  才管用，所以走 `ConnectCallback`。
- **`Host` 头也必须写成 SNI 名。** 用 IP 当 Host 时反向代理回 `200 OK` +
  `Content-Length: 0`——一个**静默的空响应**，既不报错也没内容。

### shim 需要密钥（与 AnimaCaptioner 不同，有实测依据）

AnimaCaptioner 里 shim 的密钥是"可选"（有就发、没有就不发）。我们把它算成**必须**：
对着真实网关（`nas.ddger.top:49977`）不带密钥请求，它回
`401 authentication_error: invalid api key`；而 AnimaCaptioner 自己那份配置里也是带
密钥的——"可选"只是代码允许，不是它真的能用。算成必须的效果是"没填密钥就不下发工具"；
算成可选则会下发一个每次必然 401 的工具。**没有密钥时那个网关本来就不可用**，
所以这样不损失任何真实能力，只是不再假装可用。

### 怎么验

- 界面「联网搜索」面板 → **测试搜索**：用**界面上当前填的值**（不是已保存的配置）
  直连一次，结果直接显示在面板上。用户点它的时机正是"刚填完还没保存"，
  拿盘上的旧配置去测等于在测上一次的设置。
- `proxycheck search`：离线回归（注入门槛、五个后端的请求形状与解析、截断、
  故障与空结果的分流）。请求全部交给假 handler，**一条都不发到网上**。
- `proxycheck live` 的第 0 节：用**真配置里的搜索设置**直连一次。刻意不做硬断言——
  真实后端今天不通不代表代码有问题，把它报成"回归失败"会让这条检查变成噪声。
  它已对真实 `shim` 网关实测通过（`搜索自测（shim）：连接成功，返回 5 条`），
  真实模型也会主动调用它（见文末「实测记录」）。

## 多站点与模型路由

配置里不再只有"一个上游"，而是**一组模型提供商站点**。只有**启用**的站点会被
拉取模型、出现在 `/v1/models` 里、并接受请求；每个站点有各自的地址、密钥、
自定义请求头和"是否自动拉取模型"。

一次请求发往哪里，判定顺序是：

1. **模型名映射**（`gpt-4o=deepseek-chat`）优先 —— 那是用户显式写下的"接入方报 A
   就用 B"，比任何自动判定都更该被尊重。
2. 在**启用站点的启用模型**里按名字找：命中就用那个站点。
3. 都没命中就交给**默认站点**，模型名原样透传。

第 3 条是刻意保留的：agent 平台常把模型名写死在配置里，而我们未必拉过模型表
（新装的、上游没有 `/models` 的）。这时"照发"比"报错说没有这个模型"更有用 ——
上游自己会回一个准确的错误，比我们猜要准。

**关掉的模型不会再被路由**（即使接入方点名要它，也会落到默认站点），关掉的站点
一个字节都收不到 —— 没有"广播式重发"。这两条都有断言钉着。

自定义请求头按行写 `名字: 值`。解析器会拒绝会**悄悄改掉请求语义**的行：
重复的名字（HTTP 头允许重复，但 `HttpRequestMessage` 的头字典会抛，留到发请求时
才炸就太晚了）、没有名字、名字里有空白或非法字符。非法行在编辑框下方当场提示，
而不是等到请求失败才让人去猜。

### 界面上三个只会在真实顺序下出现的坑

这三个都是**实测**（真上游 + UIA 枚举控件树）才暴露的，各自都有回归或断言钉住：

1. **"从未拉过"有两种写法。** `modelsFetchedAt` 是 `null`（字段缺失）还是 `""`，
   语义都是"没拉过"，但判据只认 `null` 时，按需拉取会被**静默跳过**：不发请求、
   不记日志，用户只看到模型表永远空着。现在 `ProviderProfile.Normalize()` 把空白
   收敛为 `null`，判据也同时认两种写法——拉取一旦被跳过就完全没有痕迹，不值得让
   这一个判断承担那么高的排查成本。
2. **按需拉取改了模型表，界面得跟着刷新。** 拉取是**接入方调 `/v1/models`** 触发的，
   那时用户正盯着界面。少了通知，界面会一直停在"尚未拉取模型"，用户既看不到、
   也就无法开关那些**实际已经在对外提供**的模型（而平台那边已经在正常用了）。
   `ProxyServer.ModelsChanged` 事件 + `DispatcherQueue` 解决这一条。
3. **开关的事件不能选"程序化也会触发"的那种。** `Checked`/`Unchecked`（以及
   `ToggleSwitch.Toggled`）在**程序化改值时同样触发**，而列表重建时 `ListView` 会
   复用行容器——复用的复选框还带着上一行的 `IsChecked`，配上新行的 `DataContext`
   就会去改**另一个模型**的开关。症状是"点一个模型，关掉的是另一个"，且一次点击
   产生三条状态变更。`CheckBox` 改用只在真实用户操作时触发的 `Click`；没有 `Click`
   的 `ToggleSwitch` 加"值没变就不动作"的守卫。

## 目录

- `Core/` —— 代理逻辑层（纯 .NET，零 WinUI 依赖）
  - `ProxyConfig` / `Secrets`(DPAPI) / `ProxyLog` / `TokenStore`
  - `ToolCatalog`（工具声明） / `VocabService` / `ToolHost`（对真实词库与搜索后端执行）
  - `SearchClient`（`web_search` 的五种后端：searxng / tavily / brave / serper / shim）
  - `RelayCommand`（托盘菜单项的动作载体 —— 见「托盘菜单必须用 `Command`」）
  - `ChatWire`（请求/响应读写） / `Sse`（SSE 读写） / `RequestShaper`（发往上游的报文，两条循环共用）
  - `ToolLoop`（非流式透明工具循环） / `StreamLoop`（流式：正文实时转发、tool_call 按轮缓冲）
  - `ToolCallAssembler`（流式分片的 `tool_calls` 拼装） / `UpstreamClient`（按站点发请求）
  - `ProviderProfile` / `CustomHeaders` / `ModelCatalog`（多站点配置、请求头解析、模型聚合与路由）
  - `ProxyServer`（Kestrel 端点、令牌鉴权、来源过滤、SSE 出口）
- `Vendor/AnimaCaptioner/` —— 从 [AnimaCaptioner] 搬入的词库内核（零 WinUI / Windows API 引用）
- `tests/` —— `net10.0` 纯 .NET 回归工程。它同时是一道**守卫**：Core/ 或 Vendor/ 一旦混进 WinUI 类型就编译不过（TFM 代际必须与应用的 `net10.0-windows…` 保持一致，见文末「构建取舍」）
- `docs/` —— 上游复用面的技术勘察报告（LlmClient、VocabDb）

## 回归测试

```powershell
dotnet run --project tests\proxycheck.csproj all
```

分段：`vocab` | `tools` | `loop` | `stream` | `merge` | `tokens` | `providers` | `server` | `e2e` | `all`

另有一条 `live`，**刻意不进 `all`**：它要联网、要一把真密钥、还会花掉真实 token。

**测试永不写真实配置。** 回归入口会把配置与令牌目录**进程级重定向**到一个临时目录，
并且 `ProxyConfig.Save()` 在重定向生效期间是**只读拒绝**（不是"静默写别处"）——
因为曾经有一次新加的功能在拉取模型时顺手调了 `Save()`，把用户真实配置覆盖成了测试桩。
唯一被允许读真实配置的是 `live`，它走一条显式的只读通道。

```powershell
dotnet run --project tests\proxycheck.csproj live
```

它接**真实上游**（默认读 `proxy.config.json` 里的 base url 与密钥）跑完整链路，回答的正是
桩上游永远答不了的问题：真实模型的 `tool_calls` 长什么样、它认不认我们这套工具声明、
`reasoning_effort` 透传过去上游有没有反应、以及**关掉思考之后它还发不发 `tool_calls`**。

退出码：`0` 全通过 / `1` 有断言失败 / `2` **没能验证**（缺词库等外部输入，与"通过"区分开）。

**日志格式是 Core 与回归共用的契约。** 回归靠日志数"工具执行了几次""模型要求调用了几次"，
所以那两处措辞被提成了常量（`ToolHost.SummaryPrefixes`、`ToolLoop.RoundSummaryTag` /
`OursLabel` / `TheirsLabel`），测试从常量拼判据、**不写字面量**。新增工具时**必须**在
`SummaryPrefixes` 里补一条：`web_search` 加进来时就忘了补，于是"搜索执行了几次"被静默
数成 0，断言反而去怪"模型没调工具"。现在有一条不变量钉着它——前缀表条数必须等于
`ToolKind` 的取值个数（`Vocab.cs` 第 7 节与 `Search.cs` 第 6 节各断言一次）。

词库路径解析顺序：`--db=<path>` > 环境变量 `AIP_VOCABDB` > AnimaCaptioner 的 `settings.json`。

`server` 与 `e2e` 两段会**真的起 Kestrel、真的发 HTTP 请求**，但：

- 令牌库指向临时目录（否则跑一次回归就把用户发给各平台的真令牌全部作废）；
- `server` 的上游指向一个死端口（测的就是"连不上时回什么"）；
- `e2e` 的上游是**本进程内第二个 Kestrel**（桩上游），按剧本回话并记下收到的请求体 ——
  所以它验的是完整链路：工具声明真的进了发往上游的报文、工具结果真的回到了模型手上、
  最终交给客户端的那份 JSON 里我们的 `tool_call` 摘干净了、usage 跨轮累加了、
  **流式也是真的分块回来的**（断言"首块到达时间远早于总耗时"，缓冲会当场失败），
  以及**流式下工具真的被注入并本地执行了**（能看到 `弓 → bow (weapon)` 那条真实查库结果）。
  它不需要网络。
- `stream` 段是流式循环的离线控制流回归，用**真的 SSE 文本**驱动（假上游故意把字节切成
  7 字节一段，逼着解析层处理"事件边界与多字节字符都落在块中间"）：`tool_call` 分片拼装、
  正文实时转发、中间轮的 `finish_reason`/usage 不外泄、客户端工具原样交还、
  流内 error 事件的两条路（还没吐字节 → 回真状态码；已吐字节 → 只能往流里补 error + `[DONE]`）。

`tests/last-run.txt` 是**手工留存**的快照（没有任何代码会写它），给手上没有 113 MB 真实词库的人看。

## 构建与运行

```powershell
# 应用（WinUI）。必须显式给平台，否则 MSIX 打包会拒绝 AnyCPU
dotnet build AnimaAIProxy.csproj -p:Platform=x64
bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\AnimaAIProxy.exe

# 回归（快得多，且不需要 Windows SDK）
dotnet build tests\proxycheck.csproj
dotnet run --project tests\proxycheck.csproj all
```

### 构建取舍：非打包 + 全框架依赖（输出 42 MB）

**TFM 是 `net10.0-windows10.0.19041.0`，与 AnimaCaptioner 同代。** 这两个工程的
TFM 必须一起看：`tests/proxycheck.csproj` 是 `net10.0`（不带 `-windows`），它同时
充当守卫。**两边代际必须相等**——这里曾一度出现应用 `net10.0`、测试 `net8.0`
的错配（低两代），后果是 Core/ 里用 net10 API 测试照样全过、应用却编译不过，
守卫静默失效。改 TFM 时两个文件一起改。相关的包版本也随之在 net10 线上：
`Microsoft.Data.Sqlite` 10.0.12（与 AnimaCaptioner 同版）、
`H.NotifyIcon.WinUI` 2.4.1（其 lib 只有 `net10.0-windows`，正是本线）。

**以非打包（unpackaged）方式运行；`.NET` 与 `Windows App SDK` 都走框架依赖。**

- `WindowsPackageType=None`：直接跑打包工程产出的那个 exe 会在启动瞬间抛
  `REGDB_E_CLASSNOTREG (0x80040154)`（堆栈落在
  `WindowsAppRuntime.DeploymentInitializeOptions`）—— 打包应用的 exe 没有包标识。
  对一个本机自用的小工具来说，MSIX 换来的是商店分发与自动更新，而这里既不上架也不自动更新。
  `Package.appxmanifest` 仍留在仓库里：哪天要出 MSIX 包，把这两个属性去掉即可，代码一行不用改。
- `SelfContained=false` ——本机装了 .NET 运行时，把 `coreclr` /
  `System.Private.CoreLib` 塞进输出目录没有收益，只会让目录从 42 MB 涨到 251 MB。

  注意这一条有个坑：csproj 里写了 `<PublishProfile>win-$(Platform).pubxml</PublishProfile>`，
  所以只要构建时传了 `-p:Platform=x64`，`Properties/PublishProfiles/*.pubxml` 就会被导入 ——
  **连普通 `build` 也逃不掉**。那三个模板文件里写着 `<SelfContained>true</SelfContained>`，
  会把 csproj 里显式的 `false` 盖掉，症状是"输出目录里莫名其妙多出一整套 .NET 运行时"，
  且没有任何提示。三个 pubxml 都已改成 `false` 并写明了原因。
- `WindowsAppSDKSelfContained=false` ——这一刀是体积的大头，**156 MB → 42 MB**，
  与 AnimaCaptioner（41 MB / 70 文件）持平。WinAppSDK 2.x 是**组件化元包**，
  `Microsoft.WindowsAppSDK` 2.5.1 除了 Base/Foundation/WinUI/DWrite/InteractiveExperiences，
  还会把 **AI + ML + Search + Widgets** 一起拉进来；自带时这些原生负载整体落进输出目录，
  共 47 个文件 / **55.7 MB**（`onnxruntime.dll` 20.7 + `DirectML.dll` 17.8 +
  `SemanticIndex.AiFabric` 3.8 + `Windows.Search` 3.3 + …），而本代理一个 API 都不调它们。

  代价与 AnimaCaptioner 同一取舍：目标机需要
  **Windows App Runtime 2.x（2.5.1）+ .NET 10 Desktop Runtime**。
  要发到没装运行时的机器上，把 `WindowsAppSDKSelfContained` 改回 `true` 即可，
  代码一行都不用改，代价是输出目录回到约 156 MB。

  **实测证据（进程模块表，非推断）**：框架依赖版启动后，
  `Microsoft.UI.Xaml.dll` / `CoreMessagingXP.dll` 从
  `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe\`
  加载，输出目录里没有它们；`MainWindowHandle` 非 0（XAML 真建出了窗口），
  `/health` 返回 `vocabReady:true`。

- 发布路径还有一处 Microsoft 自己 targets 的漏洞要补：`WindowsAppSDKSelfContained=false`
  已经让 **build** 不带 Windows ML 原生负载了，但 **publish 照样会带**
  （`_SuppressWindowsMLNativeCopyLocalNative` 只堵了 build 那条资产拷贝路径）。
  症状是"build 42 MB、publish 81 MB"。csproj 里的
  `DropUnusedWindowsMLPayloadFromPublish` 按**元数据**（`AssetType=native` +
  `NuGetPackageId`）而非文件名剔除，发布目录因此稳定在 **58 文件 / 42.1 MB**。
  这个 target 也按同一套标准验证过**确实在干活**：把它的两个钩子同时改名使其失效后发布为
  62 文件 / 81.5 MB（`onnxruntime.dll` 20.66 + `DirectML.dll` 17.83 回来了）。

## 当前状态

**已完成**：词库内核 vendored、配置/密钥/日志/令牌、工具层、透明工具循环、上游客户端、
Kestrel 端点与鉴权、**多模型提供商站点（增删改 / 切换默认 / 每站点自定义请求头）与
按模型名的路由**、**联网搜索（`web_search`，五种后端）**、WinUI 外壳（启停 / 模型提供商 /
模型 / 工具注入 / 联网搜索 / 监听 / 令牌 / 词库 / 实时日志）、托盘（菜单 + 左键 + 收托盘）、
离线回归、端到端联调。

回归现状：**595 项断言全过 / 0 失败**（含 113 MB 真实词库、一次真实 Kestrel 起停、
以及一次完整的「客户端 → 代理 → 桩上游」链路，非流式注入与流式分块透传都在内；
多站点路由那一节用**两个桩站点**验证"请求真的只发到了该去的那个站点、
另一个站点一个字节都没收到"；联网搜索那一节用**桩搜索后端**验证
"代理真的发出了搜索请求、结果回到了上游第二轮、客户端看不到 `web_search`"）。

应用已实测可运行：启动后 `/health` 返回 `ok` 且 `vocabReady: true`
（词库路径自动从 AnimaCaptioner 的 `settings.json` 借到，331,808 条标签 1.5 秒载入），
无令牌请求得到 401，**关窗口后进程仍在、窗口已隐藏、代理继续服务**（发真实
`WM_CLOSE` 验证过），退出时端口正常释放。

**发布产物同样实测可运行**（不只是 `build` 能跑）：`dotnet publish -c Release -r win-x64
-p:Platform=x64` 产出 58 文件 / **42.1 MB**，直接启动该产物得 `MainWindowHandle` 非 0、
`/health` 正常、未鉴权 `/v1/models` 得 401；`Microsoft.UI.Xaml.dll` 等确认从已安装的
WindowsAppRuntime 2.5.1 加载。

**真实上游已实测**（`proxycheck live`，DeepSeek `api.deepseek.com/v1`，模型 `deepseek-flash`）：

- 真实模型**主动调用了**我们的词库工具，并由代理在本地执行（实测一轮里要求 20 次、
  执行 12 次 —— 上限正好卡住，`达到单次请求的工具调用上限` 恰好出现 8 次，自洽）。
  也就是说真实上游认了这套工具声明，`announceTools` 写进系统提示那一段是有效的。
- `reasoning_effort` 原样透传到上游且被接受：`high` 那一轮上游吐了 `reasoning_content`，
  `none` 那一轮没有 —— 这个字段确实生效了。
- 接入方的工具（`read_file`）与我们的工具并存时，它的 `tool_call` 原样交还、`id` 一字不差、
  `finish_reason` 保持 `tool_calls`、我们的调用一个都没混进去。
- **关掉思考后模型照样发 `tool_calls`**（high/none 两轮都执行了 2 次，且 high 有 reasoning、
  none 没有，A/B 成立）。这与 AnimaCaptioner 记录的现象**不同** —— 那边是本地
  llama-server + `chat_template_kwargs` 的路径，与云端上游不是一回事。
  两种结果都指向同一个结论：这个开关的效果取决于上游与模型，**代理没有资格替接入方决定**，
  所以 `reasoning_effort` / `chat_template_kwargs` / `temperature` / `top_p` 一律原样透传。

**多站点这条路也接真实上游实测过**（同上密钥，`GET /v1/models` 按需拉取）：

- 从"从未拉过"（`modelsFetchedAt` 为空、模型表为空）的状态出发，一次 `/v1/models`
  就**自己拉到并落盘**了 DeepSeek 的两个真实模型，日志记 `DeepSeek：拉到 2 个模型`；
  界面**无需手动刷新**即显示 `2/2 个模型已启用`。
- 按模型表路由真实生效：日志记 `按模型表路由到 DeepSeek`，工具在本地执行
  （`弓 → bow (weapon)`，31,433 帖），`tool_call` 零泄漏。
- 模型开关实测（UIA 点真实复选框）：关掉 `deepseek-v4-pro` 后 `/v1/models` 只剩
  `deepseek-flash`，日志**恰好一行**；站点级开关关掉后 `/v1/models` 返回空表。
  两条都验证过反向（重新打开即恢复）。
- 停用唯一站点后再发对话，得到 **502** 与
  `没有可用的模型提供商：请在代理界面添加一个站点并勾选启用。`——
  **不会**偷偷把请求发给一个已停用的站点。

**托盘与联网搜索的实测状态**：

- **托盘四项菜单 + 左键，全部用真实鼠标走完整流程验过**：`WM_CLOSE` 收进托盘 →
  打开 Win11 的隐藏图标面板 → 右键我们的图标 → 点菜单项。
  修复前点「显示窗口」窗口不回来、点「停止代理」服务照旧运行（uptime 553→578）、
  日志里一行都没有；修复后四项各自产生预期效果，日志留下 `托盘：…` 一行。
- **搜索后端对着真实网关实测过**：界面「测试搜索」发出真实请求并拿到真实响应
  （`nas.ddger.top:49977` 回 `401 authentication_error: invalid api key`）——
  这一条同时证明了 TLS SNI 覆盖、`Host` 头、认证头与错误透传都对，
  也证明了**面板上那个按钮真的会发请求**（不是只改了个状态文本）。
- **成功的真实搜索已实测通过**（密钥填好之后）：
  - 界面「测试搜索」对真实网关 `nas.ddger.top:49977` 直连成功，日志记
    `搜索自测（shim）：连接成功，返回 5 条（5 条带摘要）。首条：Tag Groups Wiki | Danbooru`。
  - **真实模型会主动调用它**：走运行中的代理、用真令牌发对话，模型自己调 `web_search`，
    拿回真实互联网结果并原样报给用户 —— 实测两次，分别拿到
    `https://wiki.qt.io/Qt_6.8_Release` 与 `https://www.nobelprize.org/prizes/physics/2024/summary/`，
    日志记 `第 1 轮：调用 1 个（自有 1 / 接入方 0）` + `搜索「…」→ 5 条`。
  - 日志里一次搜索会出现**两行**，不是执行了两次：`搜索 shim「…」→ N 条` 由 `SearchClient`
    在 HTTP 返回时**立即**写出（传输层），`搜索「…」→ N 条` 由 `ToolHost` 写出、随该轮
    工具摘要一起在**请求结束时**成批落盘。所以后者在文件里的时间戳晚于前者，中间还会夹着
    下一轮的上游调用 —— 排障时别把这两行当成重复搜索。

**已验证的关键风险**：

- Kestrel 可在 WinUI 进程内运行并被外部客户端访问（含 SSE 回流）。
- 词库内核在纯 `net10.0`（不带 `-windows`）下编译并查询真实 113 MB 词库成功。

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
