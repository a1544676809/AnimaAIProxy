using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>一次上游流式请求的"开场"：状态码与错误。正文增量走回调。</summary>
public sealed record StreamStart(bool Ok, int Status, string ContentType, string Error)
{
    public static StreamStart Transport(string error) => new(false, 0, "", error);
}

/// <summary>
/// 两条工具循环需要上游做的事。
///
/// 抽成接口**不是为了将来换实现**，而是为了让循环的控制流能被离线测到：
/// "模型一轮里同时调了我们的工具和客户端的工具，我们该交还什么"、
/// "轮数用完了怎么办"、"上游返回的 tool_calls 里有一个不认识的工具名"、
/// "流式里 tool_call 的 arguments 分片怎么拼"——这些才是这个程序真正的逻辑，
/// 而它们不该需要一台服务器才能验。回归里用一个假上游直接驱动循环，
/// 与 AnimaCaptioner 的 TagAgent.IHost 是同一个手法。
/// </summary>
public interface IUpstream
{
    /// <summary>发一次非流式 chat 请求。实现方负责换模型名与密钥。</summary>
    Task<UpstreamResult> ChatAsync(JsonObject body, string clientToken, CancellationToken ct);

    /// <summary>
    /// 发一次**流式** chat 请求，把上游的响应体流交给 <paramref name="onStream"/>。
    ///
    /// 返回的是"开场"而不是"结果"：正文要靠回调边收边处理，而调用方需要
    /// 在**第一个字节之前**知道状态码（不然上游 429 时我们只能往流里塞错误）。
    /// 回调抛异常或提前返回时，实现方负责把响应体释放掉。
    /// </summary>
    Task<StreamStart> StreamAsync(JsonObject body, string clientToken,
                                  Func<Stream, Task> onStream, CancellationToken ct);
}
