using System.Text.Json.Serialization;

namespace AnimaAIProxy.Core;

/// <summary>
/// 一个模型提供商站点：接口地址 + 密钥 + 这个站点自己的请求头 + 它提供的模型表。
///
/// **为什么要按站点存而不是按字段存**：这些值天然是一组。换一个服务就要同时换
/// 地址、密钥、模型名，而有些网关还要额外的路由头（见 <see cref="CustomHeaders"/>）。
/// 拆成几个独立输入框时，用户每换一次服务都要改好几处，改漏一处就是一次 401
/// 或一个静默不生效的请求头。
///
/// 密钥走 DPAPI（<see cref="Secrets"/>），明文只在内存里。**熵沿用旧值**
/// （<see cref="Secrets.UpstreamEntropy"/>）：这样从旧版单上游配置迁移过来时
/// 密文可以原样搬走，不必解密再加密一次——少一次经手就少一次出错的机会。
/// </summary>
public sealed class ProviderProfile
{
    /// <summary>稳定标识。默认指针（<see cref="ProxyConfig.DefaultProviderId"/>）指向它。</summary>
    public string Id { get; set; } = NewId();

    /// <summary>用户起的显示名。空着时用 <see cref="DisplayName"/> 从地址派生。</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenAI 兼容接口的 base，例如 http://127.0.0.1:8080/v1 。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>DPAPI 密文，加密后 Base64。不要直接改这个字段。</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>
    /// 额外的请求头，每行 <c>名字: 值</c>。见 <see cref="CustomHeaders"/>。
    ///
    /// **明文存**（与 <see cref="ApiKey"/> 不同，不走 DPAPI）：它多数是路由标识
    /// 而非凭证，加密会让用户无法直接检查、也无法从别处复制过来。
    /// **不要把密钥填在这里**——上面那个框才是存密钥的地方，而且
    /// <see cref="CustomHeaders.Parse"/> 会明确拒绝手填 <c>Authorization</c>。
    /// </summary>
    public string CustomHeaders { get; set; } = "";

    /// <summary>
    /// 是否参与模型聚合与路由。**只有勾选的站点会被拉取模型**，也才会出现在
    /// 对外公布的模型表里。取消勾选等于"先把这个站点放一边"，不必删掉配置。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 允许代理自动向这个站点拉取模型表（界面上的"刷新模型"不受此限制）。
    ///
    /// 存在的理由：接入方常常在启动时拉一次 /v1/models 来校验它配的模型名，
    /// 而那时候用户未必记得先点过"刷新"。关掉它就完全手动。
    /// </summary>
    public bool AutoFetchModels { get; set; } = true;

    /// <summary>上次成功拉到模型表的时间（可读文本，null = 从未拉过）。</summary>
    public string? ModelsFetchedAt { get; set; }

    /// <summary>
    /// 这个站点提供的模型。**这张表是对外公布与路由的依据**：只有
    /// <see cref="ModelEntry.Enabled"/> 的模型会出现在 /v1/models 里，
    /// 也只有它们会把请求路由到这个站点。
    /// </summary>
    public List<ModelEntry> Models { get; set; } = new();

    /// <summary>新的随机标识。12 位十六进制足够区分（同时存在的站点只有个位数）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>明文 API Key。只在内存里，赋值时立刻加密写入 <see cref="ApiKeyProtected"/>。</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => _apiKey ??= Secrets.TryUnprotect(ApiKeyProtected, Secrets.UpstreamEntropy) ?? "";
        set
        {
            _apiKey = value ?? "";
            ApiKeyProtected = string.IsNullOrEmpty(_apiKey)
                ? ""
                : Secrets.Protect(_apiKey, Secrets.UpstreamEntropy);
        }
    }

    [JsonIgnore] private string? _apiKey;

    /// <summary>
    /// 丢掉明文缓存，下次读 <see cref="ApiKey"/> 时重新解密。
    ///
    /// 迁移必须调它：<see cref="ApiKey"/> 的 getter 只解一次并缓存（空密文也会
    /// 缓存成空串），而迁移是在反序列化**之后**才把旧密文搬过来的——不丢缓存的话
    /// 那份密钥读出来永远是空，看起来就像"迁移把密钥弄丢了"。
    /// </summary>
    internal void ForgetCachedKey() => _apiKey = null;

    /// <summary>密文存在但解不开（换了机器或账户）。界面据此提示重填，而不是静默当成没填。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable =>
        !string.IsNullOrEmpty(ApiKeyProtected) &&
        Secrets.TryUnprotect(ApiKeyProtected, Secrets.UpstreamEntropy) is null;

    /// <summary>
    /// 列表里显示的名字。没起名时依次回落到地址的主机名、占位文字——
    /// 空白行对上"这个站点是什么"毫无帮助，而主机名几乎总是能认出来的那一项。
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
            var host = HostOf(BaseUrl);
            if (host.Length > 0) return host;
            return "未命名站点";
        }
    }

    /// <summary>列表里的副标题：地址 · 模型数 · 状态。不点开也能看出这条指向哪里。</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var url = string.IsNullOrWhiteSpace(BaseUrl) ? "（未填地址）" : BaseUrl.Trim();
            var on = Models.Count(m => m.Enabled);
            var models = Models.Count == 0
                ? "尚未拉取模型"
                : $"{on}/{Models.Count} 个模型已启用";
            var key = string.IsNullOrEmpty(ApiKeyProtected)
                ? "未设密钥"
                : ApiKeyUnreadable ? "密钥解不开（换过机器？）" : "密钥已设";
            return $"{url}　·　{models}　·　{key}";
        }
    }

    /// <summary>
    /// 从地址里取主机名。手写解析而不上 <see cref="Uri"/>：用户常常先敲
    /// `api.deepseek.com`（没有 scheme）再补 `/v1`，那种串 `Uri` 解析不出来，
    /// 而"还没填完"正是最需要有个名字的时候。
    /// </summary>
    public static string HostOf(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return "";

        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep >= 0) s = s[(sep + 3)..];

        var end = s.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) s = s[..end];

        // 去掉 user@host 与 :port
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        var colon = s.LastIndexOf(':');
        if (colon >= 0) s = s[..colon];

        return s.Trim();
    }

    /// <summary>把越界的值拉回合法区间。手改配置文件写错一处不该让程序起不来。</summary>
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        BaseUrl = (BaseUrl ?? "").Trim();
        CustomHeaders = CustomHeaders ?? "";
        if (string.IsNullOrWhiteSpace(Id)) Id = NewId();
        Models ??= new List<ModelEntry>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<ModelEntry>();
        foreach (var m in Models)
        {
            if (m is null) continue;
            m.Id = (m.Id ?? "").Trim();
            if (m.Id.Length == 0) continue;
            // 同一个站点里同名模型只留一条：重复的会在模型表里出现两次，
            // 而开关一个另一个不动，看起来像开关失灵。
            if (!seen.Add(m.Id)) continue;
            kept.Add(m);
        }
        Models = kept;
    }

    /// <summary>按名字找模型（大小写不敏感，与 OpenAI 的模型名习惯一致）。</summary>
    public ModelEntry? FindModel(string? id)
    {
        var want = (id ?? "").Trim();
        if (want.Length == 0) return null;
        return Models.FirstOrDefault(m => string.Equals(m.Id, want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 用刚拉到的模型表更新本地表。
    ///
    /// **保留用户已有的开关状态**：拉取是"看看上游有什么"，不该把用户关掉的模型
    /// 又打开——那会让"我明明关了它"变成一次刷新就失效的设置。
    /// 上游不再报的模型会被移除，但 <see cref="ModelEntry.Manual"/> 的手工条目保留：
    /// 有些服务（本地 llama-server）根本不实现 /models，手工填的那个名字
    /// 一刷新就消失会让人以为是 bug。
    /// </summary>
    public void ApplyFetchedModels(IEnumerable<string> ids)
    {
        var incoming = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            var id = (raw ?? "").Trim();
            if (id.Length > 0 && seen.Add(id)) incoming.Add(id);
        }

        var kept = new List<ModelEntry>();
        foreach (var id in incoming)
        {
            var existing = FindModel(id);
            kept.Add(new ModelEntry { Id = id, Enabled = existing?.Enabled ?? true });
        }
        foreach (var m in Models.Where(m => m.Manual))
            if (seen.Add(m.Id)) kept.Add(m);

        Models = kept;
        ModelsFetchedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>深拷贝。编辑对话框在副本上改，取消才不会动到真配置。</summary>
    public ProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKeyProtected = ApiKeyProtected,
        CustomHeaders = CustomHeaders,
        Enabled = Enabled,
        AutoFetchModels = AutoFetchModels,
        ModelsFetchedAt = ModelsFetchedAt,
        Models = Models.Select(m => new ModelEntry { Id = m.Id, Enabled = m.Enabled, Manual = m.Manual }).ToList()
    };
}

/// <summary>
/// 提供商提供的一个模型。**开关决定它是否对外可见、是否参与路由**——
/// 这正是"维护一个模型列表来开关引入的模型"这条需求的最小载体。
/// </summary>
public sealed class ModelEntry
{
    /// <summary>模型名，原样发往上游。</summary>
    public string Id { get; set; } = "";

    /// <summary>是否对外公布并接受请求。默认开：拉回来的模型默认可用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 手工添加的条目（不是从上游拉到的）。刷新模型表时**不会被删掉**。
    /// </summary>
    public bool Manual { get; set; }
}
