using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaAIProxy.Core;

/// <summary>
/// 代理自身的全部可持久化配置，存在 %LocalAppData%\AnimaAIProxy\proxy.config.json。
///
/// **刻意不用程序目录**：程序常从 bin\ 里启动，重新构建会把配置一起清掉。
/// 上游密钥不在这里，它单独走 DPAPI 加密后存 <see cref="UpstreamKeyProtected"/>。
///
/// 整个类里没有一项是"替客户端做决定"的：模型名、温度、max_tokens、思考档位
/// 全部原样透传。这里配的只是**代理自己**的行为（监听端口、上游地址、
/// 要不要注入工具、注入几轮）。
/// </summary>
public sealed class ProxyConfig
{
    // ---------------- 路径 ----------------

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimaAIProxy");

    public static string ConfigPath => Path.Combine(Dir, "proxy.config.json");

    /// <summary>
    /// 词库位置的候选来源，按可信度从高到低。**第一优先是 AnimaCaptioner 的
    /// settings.json**：代理要用的就是同一份词库，让用户在两个程序里各填一遍
    /// 路径是纯粹的浪费，而填错一份会出现"打标软件搜得到、代理搜不到"
    /// 这种最难查的不一致。
    /// </summary>
    public static string DefaultVocabDbPath()
    {
        var borrowed = BorrowFromCaptioner("VocabDbPath");
        if (borrowed is not null) return borrowed;

        var local = Path.Combine(Dir, "vocab", "vocab.sqlite");
        if (File.Exists(local)) return local;

        return local;
    }

    public static string DefaultSeedPath()
    {
        var borrowed = BorrowFromCaptioner("SeedPath");
        if (borrowed is not null) return borrowed;
        return Path.Combine(Dir, "vocab", "seed.json");
    }

    private static string? BorrowFromCaptioner(string property)
    {
        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimaCaptioner", "settings.json");
            if (!File.Exists(file)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty(property, out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { /* 借不到就退回自己的默认目录，绝不因为别的程序配置坏了而起不来 */ }
        return null;
    }

    // ---------------- 监听 ----------------

    /// <summary>
    /// 监听端口。取 48620：落在 Windows 动态端口起始值（49152）之下，
    /// 免得和临时端口抢号；又远离 48977（DshBroker 的）等常见高位端口。
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    public const int DefaultPort = 48620;

    /// <summary>
    /// 允许连接的范围：local 仅回环 / lan 仅局域网 / wan 任意来源。
    /// 默认 local —— 代理手里握着上游密钥，把它暴露到网络上等于把密钥的
    /// 使用权一起交出去。要跨机就用 SSH 本地转发，别改这个。
    /// </summary>
    public string BindScope { get; set; } = "local";

    public static string NormalizeScope(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lan" => "lan",
        "wan" => "wan",
        _ => "local"
    };

    /// <summary>启动后是否立刻开始监听。关掉之后要用界面上的开关手动开。</summary>
    public bool AutoStart { get; set; } = true;

    // ---------------- 上游 ----------------

    /// <summary>
    /// 上游 OpenAI 兼容接口的基地址，**必须自带 /v1**（与 AnimaCaptioner 的约定一致，
    /// 不做路径自动补全：补全的规则永远猜不准别人的部署形状）。
    /// </summary>
    public string UpstreamBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>上游密钥（DPAPI 加密后的 base64）。明文只在内存里出现。</summary>
    public string UpstreamKeyProtected { get; set; } = "";

    /// <summary>
    /// 上游密钥的明文视图。**永不序列化**（见 JsonIgnore），避免某次"顺手保存"
    /// 把明文写进配置文件。
    /// </summary>
    [JsonIgnore]
    public string UpstreamApiKey
    {
        get => Secrets.TryUnprotect(UpstreamKeyProtected, Secrets.UpstreamEntropy) ?? "";
        set => UpstreamKeyProtected = string.IsNullOrEmpty(value)
            ? ""
            : Secrets.Protect(value, Secrets.UpstreamEntropy);
    }

    [JsonIgnore]
    public bool HasUpstreamKey => !string.IsNullOrEmpty(UpstreamKeyProtected);

    /// <summary>
    /// 模型名映射：客户端报的模型名 → 上游真正要的模型名。
    ///
    /// 为什么需要它：agent 平台往往把模型名写死在配置里（"gpt-4o"），
    /// 而上游只认自己的名字。没有这层映射，用户就得去改平台的配置，
    /// 那等于"接入我们的代理"变成"改造接入方"。
    /// 映射里没有的模型名**原样透传**，不猜、不改。
    /// </summary>
    public Dictionary<string, string> ModelMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 上游不认识某个模型名时，是否退化到 <see cref="FallbackModel"/>。
    /// 默认关：静默换模型会让用户以为在用 A 实际用了 B，比直接报错更难查。
    /// </summary>
    public bool FallbackModelEnabled { get; set; }

    public string FallbackModel { get; set; } = "";

    /// <summary>单次上游请求的超时（秒）。思考档位开满时上游可以想很久，所以给得宽。</summary>
    public int TimeoutSeconds { get; set; } = 600;

    // ---------------- 工具注入 ----------------

    /// <summary>总开关。关掉之后代理退化成纯粹的反向代理，一个字节都不改请求。</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>
    /// 是否把"你还有这些查词库的工具"写进系统提示。
    ///
    /// 默认**开**。多数平台的系统提示里只会列举它自己那套工具，模型因此
    /// 根本不会想到去调我们的工具——只挂工具声明、不解释它是什么，
    /// 实测模型会把它当成"某个不认识的、最好别碰的东西"。
    /// </summary>
    public bool AnnounceTools { get; set; } = true;

    /// <summary>
    /// 伪装工具调用循环的轮数上限。一轮 = 一次上游请求。太少会让"先查三个概念、
    /// 再回答"跑不完；太多则一次客户端请求要等好几倍时间。
    /// </summary>
    public int MaxToolRounds { get; set; } = 4;

    /// <summary>
    /// 把本轮已经查到的结果摘要回写进交给客户端的 <c>content</c>。
    ///
    /// 为什么需要：模型有时会在同一轮里既调我们的工具、又调客户端的工具。
    /// 我们把客户端那部分原样交还给它去执行，我们那部分由我们执行完了——
    /// 但**客户端的历史里不会有我们的工具结果**。不回写的话，等它把客户端
    /// 工具的结果发回来时，模型已经忘了刚才查到过什么，只好重查一遍。
    /// 回写到 content 里，这份结果就随客户端自己的对话历史一直带着走。
    /// </summary>
    public bool CarryFindings { get; set; } = true;

    /// <summary>一次请求最多本地执行多少次工具调用（防止模型一轮里发几十个查询）。</summary>
    public int MaxToolCallsPerRequest { get; set; } = 12;

    // ---------------- 词库 ----------------

    public string VocabDbPath { get; set; } = DefaultVocabDbPath();

    public string SeedPath { get; set; } = DefaultSeedPath();

    /// <summary>启动时预载词库。113 MB 的库载入要几秒，但第一次查询就不必现等。</summary>
    public bool PreloadVocab { get; set; } = true;

    // ---------------- 鉴权 ----------------

    /// <summary>
    /// 是否要求客户端携带代理签发的令牌。
    ///
    /// **默认 true，而且"一枚令牌都没有"时同样拒绝一切**——不是"没配就放行"。
    /// 代理手里有上游密钥，一个不设防的回环端口等于本机任何程序都能白用你的额度
    /// （浏览器里的网页也能向 127.0.0.1 发请求）。
    /// </summary>
    public bool RequireToken { get; set; } = true;

    // ---------------- 日志 ----------------

    /// <summary>
    /// 是否把完整的请求/响应体写进日志。
    /// 默认关：对话里可能有过隐私内容，落盘等于多开一个泄露面。
    /// 排查问题时临时打开，看完关掉。
    /// </summary>
    public bool LogBodies { get; set; }

    // ---------------- 读 / 写 ----------------

    private static readonly JsonSerializerOptions Options = new()
    {
        // 读的时候大小写不敏感（手写的 camelCase 和 PascalCase 都认），
        // 写的时候统一 camelCase，人和脚本都看得懂。
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ProxyConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<ProxyConfig>(File.ReadAllText(ConfigPath), Options);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // 配置坏了就用默认值起来，并把坏文件留个备份——直接覆盖会让用户
            // 连"我原来填的地址是什么"都找不回来。
            TryBackupBroken(ex);
        }

        var fresh = new ProxyConfig();
        fresh.Normalize();
        fresh.Save();
        return fresh;
    }

    private static void TryBackupBroken(Exception ex)
    {
        try
        {
            var bak = ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(ConfigPath, bak, overwrite: true);
            ProxyLog.Write("配置解析失败（" + ex.Message + "），已备份到 " + Path.GetFileName(bak) + "，改用默认值");
        }
        catch { /* 备份失败也不能挡住启动 */ }
    }

    /// <summary>把越界的值拉回合法区间。手改配置文件写错一个数字不该让程序起不来。</summary>
    public void Normalize()
    {
        if (Port is < 1024 or > 65535) Port = DefaultPort;
        BindScope = NormalizeScope(BindScope);
        if (TimeoutSeconds < 5) TimeoutSeconds = 5;
        if (MaxToolRounds is < 1 or > 12) MaxToolRounds = 4;
        if (MaxToolCallsPerRequest is < 1 or > 64) MaxToolCallsPerRequest = 12;
        if (string.IsNullOrWhiteSpace(UpstreamBaseUrl)) UpstreamBaseUrl = "https://api.deepseek.com/v1";
        ModelMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            ProxyLog.Write("配置保存失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 监听地址：只有 lan/wan 才放开到所有网卡（来源过滤在服务端做）。
    ///
    /// **必须 JsonIgnore**：这是算出来的视图，不是配置项。少了这个标记，
    /// 序列化时 System.Text.Json 会去读 IPAddress.ScopeId，在回环地址上抛
    /// SocketException，整个 Save() 挂掉——表现是"改完设置重启就没了"，
    /// 而且因为 Save 里 catch 了异常，只留一行日志，极难查。
    /// </summary>
    [JsonIgnore]
    public System.Net.IPAddress ListenAddress =>
        BindScope == "local" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any;
}
