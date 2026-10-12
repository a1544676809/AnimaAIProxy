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

## 目录

- `Core/` —— 代理逻辑层（纯 .NET，零 WinUI 依赖）
  - `ProxyConfig` / `Secrets`(DPAPI) / `ProxyLog` / `TokenStore`
  - `ToolCatalog`（工具声明） / `VocabService` / `ToolHost`（对真实词库执行）
  - `ChatWire`（请求/响应读写） / `ToolLoop`（透明工具循环） / `UpstreamClient`
- `Vendor/AnimaCaptioner/` —— 从 [AnimaCaptioner] 搬入的词库内核（零 WinUI / Windows API 引用）
- `tests/` —— `net10.0` 纯 .NET 回归工程。它同时是一道**守卫**：Core/ 或 Vendor/ 一旦混进 WinUI 类型就编译不过
- `docs/` —— 上游复用面的技术勘察报告（LlmClient、VocabDb）

## 回归测试

```powershell
dotnet run --project tests\proxycheck.csproj -c Release -- all
```

退出码：`0` 全通过 / `1` 有断言失败 / `2` **没能验证**（缺词库等外部输入，与"通过"区分开）。

词库路径解析顺序：`--db=<path>` &gt; 环境变量 `AIP_VOCABDB` &gt; AnimaCaptioner 的 `settings.json`。

## 当前状态

已完成：词库内核 vendored、配置/密钥/日志/令牌、工具层、透明工具循环、上游客户端、离线回归。

未完成：`ProxyServer`（Kestrel 端点 `/v1/chat/completions`、`/v1/models`、流式透传、令牌鉴权）、
WinUI 托盘外壳、端到端联调。

已验证的关键风险：Kestrel 可在 WinUI 进程内运行并被外部客户端访问（含 SSE 回流）；
词库内核在纯 `net10.0` 下编译并查询真实 113 MB 词库成功。
