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
- **上游**：地址、DPAPI 加密的密钥、测试连接、超时、模型名映射
- **工具注入**：总开关、是否写进系统提示、是否回写结果、轮数上限、单次调用上限
- **监听**：端口、允许范围、是否要求令牌、是否记录请求体
- **代理令牌**：签发（明文只显示一次）、启用/停用、删除
- **词库**：状态、路径、重新载入
- **实时日志**：订阅 `ProxyLog.Written`、清空、打开日志文件 / 配置目录

托盘右键：显示窗口 / 启动代理 / 停止代理 / 退出。鼠标悬停显示当前状态与 Base URL。

## 端点

| 端点 | 行为 |
| --- | --- |
| `GET /health` | 存活探测。**刻意不鉴权**（监控系统没有令牌），返回体里只有存活与词库就绪状态，不含上游地址/令牌数/路径 |
| `POST /v1/chat/completions` | 主端点。`stream=false` 与 `stream=true` **都**透明注入工具并本地执行；流式下正文增量实时转发（见下） |
| `GET /v1/models` | 上游模型表；上游不可用时回退到配置里的模型映射表。配置里映射出来的名字会被补进列表（平台启动时会拿它校验配置） |
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

## 目录

- `Core/` —— 代理逻辑层（纯 .NET，零 WinUI 依赖）
  - `ProxyConfig` / `Secrets`(DPAPI) / `ProxyLog` / `TokenStore`
  - `ToolCatalog`（工具声明） / `VocabService` / `ToolHost`（对真实词库执行）
  - `ChatWire`（请求/响应读写） / `Sse`（SSE 读写） / `RequestShaper`（发往上游的报文，两条循环共用）
  - `ToolLoop`（非流式透明工具循环） / `StreamLoop`（流式：正文实时转发、tool_call 按轮缓冲）
  - `ToolCallAssembler`（流式分片的 `tool_calls` 拼装） / `UpstreamClient`
  - `ProxyServer`（Kestrel 端点、令牌鉴权、来源过滤、SSE 出口）
- `Vendor/AnimaCaptioner/` —— 从 [AnimaCaptioner] 搬入的词库内核（零 WinUI / Windows API 引用）
- `tests/` —— `net10.0` 纯 .NET 回归工程。它同时是一道**守卫**：Core/ 或 Vendor/ 一旦混进 WinUI 类型就编译不过（TFM 代际必须与应用的 `net10.0-windows…` 保持一致，见文末「构建取舍」）
- `docs/` —— 上游复用面的技术勘察报告（LlmClient、VocabDb）

## 回归测试

```powershell
dotnet run --project tests\proxycheck.csproj all
```

分段：`vocab` | `tools` | `loop` | `stream` | `merge` | `tokens` | `server` | `e2e` | `all`

另有一条 `live`，**刻意不进 `all`**：它要联网、要一把真密钥、还会花掉真实 token。

```powershell
dotnet run --project tests\proxycheck.csproj live
```

它接**真实上游**（默认读 `proxy.config.json` 里的 base url 与密钥）跑完整链路，回答的正是
桩上游永远答不了的问题：真实模型的 `tool_calls` 长什么样、它认不认我们这套工具声明、
`reasoning_effort` 透传过去上游有没有反应、以及**关掉思考之后它还发不发 `tool_calls`**。

退出码：`0` 全通过 / `1` 有断言失败 / `2` **没能验证**（缺词库等外部输入，与"通过"区分开）。

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
Kestrel 端点与鉴权、WinUI 外壳（启停 / 上游 / 工具注入 / 监听 / 令牌 / 词库 / 实时日志）、
离线回归、端到端联调。

回归现状：**255 项断言全过 / 0 失败**（含 113 MB 真实词库、一次真实 Kestrel 起停、
以及一次完整的「客户端 → 代理 → 桩上游」链路，非流式注入与流式分块透传都在内）。

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
