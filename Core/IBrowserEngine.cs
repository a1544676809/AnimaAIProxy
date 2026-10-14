namespace AnimaAIProxy.Core;

/// <summary>
/// 浏览器渲染引擎的契约。
///
/// 抽成接口的唯一理由是**让"什么时候该升级到浏览器"这条判断能被离线回归**：
/// 自动降级是本工具最核心的行为，而它需要真上游、真 Edge、真网络才能端到端跑一遍。
/// 有了这个接口，回归里可以塞一个"总是失败"或"总是成功"的假浏览器，
/// 把降级的每条分支都钉住（见 tests/PageReader.cs）。
/// 与 <see cref="IUpstream"/> 是同一个思路。
/// </summary>
public interface IBrowserEngine
{
    /// <summary>本机有没有可用的浏览器。没有时不该把 <c>web_fetch</c> 的 browser 模式当成可用。</summary>
    bool Available { get; }

    Task<BrowserPage> FetchAsync(string url, string proxy, int idleSeconds,
                                 TimeSpan timeout, CancellationToken ct);
}
