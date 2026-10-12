using AnimaCaptioner.Core;

namespace AnimaAIProxy.Core;

/// <summary>
/// 词库的持有者：按需载入、载入后常驻、失败可重试。
///
/// **为什么是"按需 + 常驻"而不是每次查询现载。** 113 MB 的库载入要几秒、
/// 吃几百 MB 内存，而代理的访问模式是"长时间空闲、来了请求要立刻答"。
/// 每次现载会让第一次查询等几秒（上游那边已经开始计时了），载完就扔掉，
/// 下一个请求再等一次。
///
/// **为什么失败后允许重试**，而不是像 AnimaCaptioner 的能力缓存那样
/// "一辈子只试一次"：那边失败的是网络探测，重试只是在浪费时间；这边失败的
/// 原因通常是"用户还没把词库下下来"或"路径填错了"，而这两件事**在程序运行
/// 期间就会被修好**（用户看到界面上的红字，去改设置或去下载）。永久缓存失败
/// 会逼用户重启代理，而重启之后它又会因为同样的原因再失败一次——
/// 用户永远修不好它。
/// </summary>
public sealed class VocabService
{
    private readonly object _gate = new();
    private VocabDb? _db;
    private string _loadedFrom = "";
    private string _lastError = "";
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>失败后至少隔这么久才再试一次，免得每次请求都去读一遍 113 MB 的文件。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    public bool IsLoaded => _db is not null;

    public string LoadedFrom => _loadedFrom;

    public string LastError => _lastError;

    /// <summary>载入失败时的说明，直接可以显示给用户。</summary>
    public string Status()
    {
        if (_db is not null)
            return $"已载入 {Path.GetFileName(_loadedFrom)}（标签 {_db.TagCount:N0}）";
        if (_lastError.Length > 0) return _lastError;
        return "尚未载入";
    }

    /// <summary>
    /// 取词库，没有就（在允许的时机）载一次。返回 null 表示当前不可用，
    /// <see cref="LastError"/> 里有原因。
    ///
    /// **绝不抛异常**：词库不可用只应该让工具返回一句"查不了"，
    /// 不该让整个代理请求失败——用户接的可能是别的对话，不该被这个词库拖下水。
    /// </summary>
    public VocabDb? Get(ProxyConfig cfg)
    {
        lock (_gate)
        {
            if (_db is not null)
            {
                // 路径被改了就重新载：用户换了词库却还在用旧的，
                // 表现是"搜出来的东西对不上"，极难归因。
                if (string.Equals(_loadedFrom, cfg.VocabDbPath, StringComparison.OrdinalIgnoreCase))
                    return _db;
                ProxyLog.Write("词库路径已变更，重新载入：" + cfg.VocabDbPath);
                _db = null;
            }

            if (DateTimeOffset.Now - _lastAttempt < RetryInterval) return null;
            _lastAttempt = DateTimeOffset.Now;

            try
            {
                if (!File.Exists(cfg.VocabDbPath))
                {
                    _lastError = "词库数据库不存在：" + cfg.VocabDbPath;
                    return null;
                }

                var db = new VocabDb();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // seed 是可选的人工映射表。不存在时传一个不存在的路径即可，
                // VocabDb 自己会记日志并跳过（见 VocabDb.Load 的 try 块）。
                db.Load(cfg.VocabDbPath,
                        File.Exists(cfg.SeedPath) ? cfg.SeedPath : cfg.SeedPath + ".absent");
                sw.Stop();

                _db = db;
                _loadedFrom = cfg.VocabDbPath;
                _lastError = "";
                ProxyLog.Write($"词库已载入：{cfg.VocabDbPath} " +
                               $"tags={db.TagCount} anima={db.AnimaCount} alias={db.AliasCount} " +
                               $"耗时={sw.ElapsedMilliseconds}ms");
                return _db;
            }
            catch (Exception ex)
            {
                _lastError = "词库载入失败：" + ex.Message;
                ProxyLog.Write(_lastError);
                return null;
            }
        }
    }

    /// <summary>界面上的"立刻重新载入"用。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _db = null;
            _loadedFrom = "";
            _lastError = "";
            _lastAttempt = DateTimeOffset.MinValue;
        }
    }
}
