using System.Text;

namespace AnimaCaptioner;

/// <summary>
/// 极简落盘日志。WinUI 在窗口起不来时不会有任何可见输出（unpackaged 也没有事件日志条目），
/// 所以任何一次启动都要留下痕迹，否则失败时无从查起。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "app.log");

    /// <summary>日志文件路径，供「打开日志」菜单使用。</summary>
    public static string FilePath => Path;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path)!;
                Directory.CreateDirectory(dir);
                // 日志超过 2MB 就轮转一次，避免长期使用后无限增长
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var bak = Path + ".1";
                    File.Delete(bak);
                    File.Move(Path, bak);
                }
                File.AppendAllText(Path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch { /* 日志本身绝不能把程序拖垮 */ }
    }
}
