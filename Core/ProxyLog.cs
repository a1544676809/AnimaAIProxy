using System.Text;

namespace AnimaAIProxy.Core;

/// <summary>
/// 代理的落盘日志。
///
/// 单独一份而不是复用 vendored 进来的 AnimaCaptioner <c>Log</c>：那个写的是
/// 打标软件自己的日志文件，两边的量级与保留策略完全不同——代理每次对话都要
/// 写好几行，跟着对方的 2 MB 阈值轮转会把打标软件的日志冲掉。
///
/// 保留策略沿用 DshBroker：超过 2 MB 轮转，保留 5 份。
/// </summary>
public static class ProxyLog
{
    private static readonly object Gate = new();

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "logs");

    public static string FilePath => Path.Combine(Dir, "proxy.log");

    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeepFiles = 5;

    /// <summary>最近若干行，供界面上的实时日志面板读取。</summary>
    private static readonly Queue<string> Recent = new();
    private const int RecentCap = 500;

    /// <summary>有新日志时触发，供 UI 订阅。参数是已经格式化好的整行。</summary>
    public static event Action<string>? Written;

    public static void Write(string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentCap) Recent.Dequeue();
        }

        try { Written?.Invoke(line); } catch { /* 订阅方自己的异常不该影响代理 */ }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { /* 日志本身绝不能把代理拖垮 */ }
    }

    /// <summary>把 proxy.log 挪成 proxy.1.log，其余依次后移，超出的删掉。</summary>
    private static void Rotate()
    {
        try
        {
            var oldest = Path.Combine(Dir, $"proxy.{KeepFiles}.log");
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var i = KeepFiles - 1; i >= 1; i--)
            {
                var from = Path.Combine(Dir, $"proxy.{i}.log");
                var to = Path.Combine(Dir, $"proxy.{i + 1}.log");
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }

            // 注意 .log 后缀：轮转文件名不能是 "proxy.log.1"，否则会和
            // "proxy.1.log" 两套命名混在一个目录里，用户永远分不清哪个是最新的。
            File.Move(FilePath, Path.Combine(Dir, "proxy.1.log"), overwrite: true);
        }
        catch { /* 轮转失败就让文件继续长，不能因此丢掉这条日志 */ }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }
}
