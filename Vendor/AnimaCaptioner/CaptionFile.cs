using System.Text;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一个 caption 文件的原始形态。写回时必须原样还原，否则会污染训练集。
///
/// 实测 sayori-style-v1 的 53 个 .txt：无 BOM、CRLF 行尾、单个结尾换行、
/// 分隔符固定为 ", "。这些细节任何一个变了，diff 都会整个文件报红，
/// 而且有些训练脚本对 BOM 敏感。所以先探测、再按原样写回。
/// </summary>
public sealed class CaptionFile
{
    public string Path { get; init; } = "";
    public List<string> Tags { get; init; } = new();

    /// <summary>原文件是否带 UTF-8 BOM。</summary>
    public bool HasBom { get; init; }

    /// <summary>行尾风格。单行 caption 时只影响结尾换行。</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>原文件结尾是否有换行。</summary>
    public bool TrailingNewLine { get; init; } = true;

    /// <summary>标签之间的分隔符，默认 ", "。</summary>
    public string Separator { get; init; } = ", ";

    public string? OriginalText { get; init; }

    public string Text => string.Join(Separator, Tags);

    public static List<string> SplitTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    public static CaptionFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n');
        var stripped = text.TrimEnd('\r', '\n');

        // 分隔符探测：优先 ", "，否则退回 ","（不要凭空改写别人的风格）
        var sep = stripped.Contains(", ", StringComparison.Ordinal) ? ", " : ",";

        return new CaptionFile
        {
            Path = path,
            Tags = SplitTags(stripped),
            HasBom = hasBom,
            NewLine = nl,
            TrailingNewLine = trailing,
            Separator = sep,
            OriginalText = text
        };
    }

    /// <summary>按原文件的字节风格写回。返回是否真的改变了内容。</summary>
    public bool Save(IReadOnlyList<string>? tags = null)
    {
        var list = tags ?? Tags;
        return SaveBody(string.Join(Separator, list));
    }

    /// <summary>
    /// 按原文件的字节风格写回**已经拼好的整行正文**。
    ///
    /// 和 <see cref="Save"/> 分开是必要的：提示词不总是"纯标签列表"，它还可能是
    /// 「标签区 + 英文自然语言尾巴」（Anima 官方写法：
    /// `masterpiece, best score_7, safe. An anime girl …`）。那种正文不能再用
    /// 分隔符 join 一遍——join 会把句号之后的散文当成一个超长标签重新拼接。
    /// 所以由调用方用 PromptText.Rebuild 拼好整行，这里只负责字节风格与落盘。
    /// </summary>
    public bool SaveBody(string body)
    {
        var text = (body ?? "") + (TrailingNewLine ? NewLine : "");
        var bytes = new UTF8Encoding(false).GetBytes(text);

        if (HasBom)
        {
            var withBom = new byte[bytes.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(bytes, 0, withBom, 3, bytes.Length);
            bytes = withBom;
        }

        // 内容没变就不要碰文件（避免无谓地改 mtime，也避免打断别的工具的文件监视）
        if (File.Exists(Path))
        {
            var old = File.ReadAllBytes(Path);
            if (old.Length == bytes.Length && old.AsSpan().SequenceEqual(bytes)) return false;
        }

        // 先写临时文件再替换：写到一半崩溃不会留下半截 caption
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, Path, overwrite: true);
        return true;
    }
}

/// <summary>训练集目录里的一对（图片 + caption）。</summary>
public sealed class DatasetItem
{
    public string ImagePath { get; init; } = "";
    public string CaptionPath { get; init; } = "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(ImagePath);

    /// <summary>
    /// 是否已有同名 .txt。每次实时查盘而不是缓存：用户在界面里保存后
    /// 状态要立刻正确，缓存字段会一直停留在扫描那一刻的旧值。
    /// </summary>
    public bool HasCaption => File.Exists(CaptionPath);

    public CaptionFile? Caption { get; set; }

    /// <summary>
    /// 这张图的标签。已载入的 <see cref="Caption"/> 是权威（用户可能刚改过），
    /// 没载入过就从盘上读——绝不能返回空列表。
    ///
    /// 这个回退是必须的：<see cref="Caption"/> 只在切到某张图时才被填充，
    /// 所以在此之前的任何一次统计都会把未访问过的图算成 0 个标签
    /// （实测底部状态栏因此显示「标签 0」，而实际有 2335 个）。
    /// </summary>
    public List<string> Tags
    {
        get
        {
            if (Caption is not null) return Caption.Tags;
            if (!HasCaption) return new List<string>();
            try { return CaptionFile.Load(CaptionPath).Tags; }
            catch { return new List<string>(); }
        }
    }
}

public static class Dataset
{
    private static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    public static bool IsImage(string path) =>
        ImageExts.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>枚举目录下的图片；有同名 .txt 的排前面，其余按文件名。</summary>
    public static List<DatasetItem> Scan(string dir)
    {
        var items = new List<DatasetItem>();
        if (!Directory.Exists(dir)) return items;

        foreach (var img in Directory.EnumerateFiles(dir)
                     .Where(IsImage)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            items.Add(new DatasetItem
            {
                ImagePath = img,
                CaptionPath = System.IO.Path.ChangeExtension(img, ".txt")
            });
        }
        return items;
    }

    /// <summary>统计：总数、已标注、标签总数、有问题的 caption 数。</summary>
    public sealed record Stats(int Total, int Captioned, int TagInstances, int WithIssues);

    public static Stats ComputeStats(IEnumerable<DatasetItem> items, VocabDb? vocab)
    {
        var total = 0; var captioned = 0; var tags = 0; var issues = 0;
        foreach (var it in items)
        {
            total++;
            if (!it.HasCaption) continue;
            captioned++;
            var t = it.Tags;
            tags += t.Count;
            if (vocab is not null && VocabDb.AuditTags(t, vocab).Count > 0) issues++;
        }
        return new Stats(total, captioned, tags, issues);
    }
}
