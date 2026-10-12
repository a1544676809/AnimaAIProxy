using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 工具循环需要上游做的事。
///
/// 抽成接口**不是为了将来换实现**，而是为了让循环的控制流能被离线测到：
/// "模型一轮里同时调了我们的工具和客户端的工具，我们该交还什么"、
/// "轮数用完了怎么办"、"上游返回的 tool_calls 里有一个不认识的工具名"——
/// 这些才是这个程序真正的逻辑，而它们不该需要一台服务器才能验。
/// 回归里用一个假上游直接驱动 <see cref="ToolLoop"/>，与
/// AnimaCaptioner 的 TagAgent.IHost 是同一个手法。
/// </summary>
public interface IUpstream
{
    /// <summary>发一次非流式 chat 请求。实现方负责换模型名与密钥。</summary>
    Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct);
}
