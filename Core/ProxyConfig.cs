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

    /// <summary>
    /// 配置文件路径的**测试替身**。非 null 时一切读/写都走它。
    ///
    /// 为什么必须有：<see cref="Save"/> 会被 <c>ProxyServer</c> 在"按需拉取模型表"
    /// 时调用，也就是说**一次 HTTP 请求就会写配置文件**。回归里若不重定向，
    /// 测试用的临时配置会直接盖掉用户的真配置——而症状是"跑完回归，站点和密钥
    /// 全没了"，且没有任何提示。这与 <see cref="TokenStore.FilePathOverride"/>
    /// 是同一道防线，理由也一样。
    /// </summary>
    public static string? FilePathOverride { get; set; }

    public static string ConfigPath => FilePathOverride ?? Path.Combine(Dir, "proxy.config.json");

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

    // ---------------- 转发 API ----------------

    /// <summary>
    /// 是否启动**转发 API**（OpenAI 兼容那套端点）。
    ///
    /// 与 <see cref="McpEnabled"/> 是**两个独立的服务**：只做 MCP 的人可以把这里关掉，
    /// 于是这个程序不占任何对外端口/也不碰上游；只想当纯代理的人可以把 MCP 关掉。
    /// 关掉之后「启动」只起 MCP，界面上那个地址也就没有意义了。
    /// </summary>
    public bool ApiEnabled { get; set; } = true;

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

    // ---------------- 模型提供商 ----------------

    /// <summary>
    /// 模型提供商站点。请求按模型名路由到对应站点，见 <see cref="ModelCatalog.Resolve"/>。
    ///
    /// 只有 <see cref="ProviderProfile.Enabled"/> 的站点会被拉取模型、出现在对外
    /// 公布的模型表里、并接受请求。取消勾选等于"先放一边"，不必删配置。
    /// </summary>
    public List<ProviderProfile> Providers { get; set; } = new();

    /// <summary>
    /// 默认站点：模型名不在任何站点模型表里时发给它（见
    /// <see cref="ModelCatalog.Resolve"/> 第 3 条）。界面上的"设为默认"改的就是它。
    /// </summary>
    public string DefaultProviderId { get; set; } = "";

    /// <summary>新配置默认带的那个站点地址。**必须自带 /v1**（与 AnimaCaptioner 的约定一致，不做路径自动补全）。</summary>
    public const string DefaultProviderBaseUrl = "https://api.deepseek.com/v1";

    // ---- 旧版单上游配置：只用于读旧文件并迁移，见 MigrateLegacyUpstream ----

    /// <summary>
    /// 旧版字段。迁移后置为 null，新代码一律用 <see cref="Providers"/>。
    ///
    /// 可空是**为了让新配置文件里彻底不出现这两个键**：`WhenWritingDefault` 对
    /// 字符串的"默认值"是 null 而不是空串，写成空串照样会被序列化出来，
    /// 于是文件里永远留着两个没人读的字段——下次有人看到它们会以为还能手改。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamBaseUrl { get; set; }

    /// <summary>旧版字段（DPAPI 密文）。迁移时**原样搬走**，不解密再加密。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamKeyProtected { get; set; }

    /// <summary>按 Id 找站点。**不筛启用状态**：界面要能选中一个已停用的站点去编辑它。</summary>
    public ProviderProfile? ProviderById(string? id)
    {
        var want = (id ?? "").Trim();
        if (want.Length == 0) return null;
        return Providers.FirstOrDefault(p => string.Equals(p.Id, want, StringComparison.Ordinal));
    }

    /// <summary>
    /// 默认站点：模型名不在任何站点模型表里时发给它（见
    /// <see cref="ModelCatalog.Resolve"/> 第 3 条）。
    ///
    /// **只认启用中的站点**。指针指向一个已停用的站点时回落到第一个启用的——
    /// 否则"取消勾选"这个动作在界面上看起来生效了，请求却照旧发给它，
    /// 而这种不一致不会有任何症状。
    ///
    /// 全部站点都停用时返回 null，调用方如实报"没有可用的模型提供商"。
    /// **刻意不偷偷挑一个停用的站点**：用户把它们全关掉，就是想让它别再往外发。
    /// </summary>
    [JsonIgnore]
    public ProviderProfile? DefaultProvider
    {
        get
        {
            var pinned = ProviderById(DefaultProviderId);
            if (pinned is { Enabled: true }) return pinned;
            return Providers.FirstOrDefault(p => p.Enabled);
        }
    }

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

    // ---------------- 联网搜索 ----------------

    /// <summary>
    /// 是否允许给模型下发 <c>web_search</c> 工具。
    ///
    /// **默认关，而且光打开还不够**：后端没配到能用的程度时工具不下发，
    /// 见 <see cref="SearchReady"/>。给模型一个必然失败的工具，结果是它白烧
    /// 一轮预算、拿到一句"搜索不可用"，然后可能改去凭记忆编答案——
    /// 这比一开始就没有这个工具更糟。AnimaCaptioner 那边同样是这个取舍。
    /// </summary>
    public bool SearchEnabled { get; set; }

    /// <summary>
    /// 搜索后端，见 <see cref="SearchProviders"/>。空串 = 没选，等同于没配。
    /// 与 <see cref="ProviderProfile"/> 不同，**搜索只有一个后端**——
    /// 它是给模型查"这个词是什么意思"用的，不需要按名字路由到多家里。
    /// </summary>
    public string SearchProvider { get; set; } = "";

    /// <summary>searxng / shim 的后端地址；tavily、brave、serper 用各自固定的官方地址，这里留空。</summary>
    public string SearchBaseUrl { get; set; } = "";

    /// <summary>DPAPI 密文，加密后 Base64。不要直接改这个字段。</summary>
    public string SearchApiKeyProtected { get; set; } = "";

    /// <summary>明文搜索密钥。只在内存里，赋值时立刻加密写入 <see cref="SearchApiKeyProtected"/>。</summary>
    [JsonIgnore]
    public string SearchApiKey
    {
        get => _searchKey ??= Secrets.TryUnprotect(SearchApiKeyProtected, Secrets.UpstreamEntropy) ?? "";
        set
        {
            _searchKey = value ?? "";
            SearchApiKeyProtected = string.IsNullOrEmpty(_searchKey)
                ? ""
                : Secrets.Protect(_searchKey, Secrets.UpstreamEntropy);
        }
    }

    [JsonIgnore] private string? _searchKey;

    /// <summary>密文存在但解不开（换过机器）。界面据此提示重填，而不是静默当成没填。</summary>
    [JsonIgnore]
    public bool SearchApiKeyUnreadable =>
        !string.IsNullOrEmpty(SearchApiKeyProtected) &&
        Secrets.TryUnprotect(SearchApiKeyProtected, Secrets.UpstreamEntropy) is null;

    /// <summary>
    /// shim 后端的 TLS SNI / Host 名。留空时取 <see cref="SearchBaseUrl"/> 的主机名。
    /// 见 <see cref="SearchClient"/> 里对 shim 那条路的说明（SNI 与 Host 必须一致）。
    /// </summary>
    public string SearchSniHost { get; set; } = "";

    /// <summary>回灌给模型的结果条数上限。摘要很占上下文，给太多会把真正的问题挤出视野。</summary>
    public int SearchMaxResults { get; set; } = 5;

    /// <summary>单次搜索的超时（秒）。它比上游对话快得多，不需要几分钟。</summary>
    public int SearchTimeoutSeconds { get; set; } = 30;

    /// <summary>可选后端，顺序即界面下拉框的顺序。</summary>
    public static readonly string[] SearchProviders = { "searxng", "tavily", "brave", "serper", "shim" };

    /// <summary>把后端名归一化成小写白名单里的值；认不出就返回空串（= 未配置）。</summary>
    public static string NormalizeProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "searxng" => "searxng",
        "tavily" => "tavily",
        "brave" => "brave",
        "serper" => "serper",
        "shim" => "shim",
        _ => ""
    };

    [JsonIgnore]
    public string SearchProviderKey => NormalizeProvider(SearchProvider);

    /// <summary>这个后端是否必须要密钥（商业 API，以及实测需要密钥的 shim）。</summary>
    [JsonIgnore]
    public bool SearchNeedsKey => NeedsKeyFor(SearchProviderKey);

    /// <summary>这个后端是否必须要地址（自建实例）；商业 API 用固定域名。</summary>
    [JsonIgnore]
    public bool SearchNeedsEndpoint => NeedsEndpointFor(SearchProviderKey);

    /// <summary>
    /// 这个后端是否必须要密钥。
    ///
    /// **shim 也算必须**，这一条与 AnimaCaptioner 不同（那边密钥可选），依据是实测：
    /// 对着真实的搜索网关（nas.ddger.top:49977）不带密钥请求，它回
    /// <c>401 authentication_error: invalid api key</c>。而 AnimaCaptioner 自己那份
    /// 配置里也是带密钥的——"可选"只是代码允许，不是它真的能用。
    ///
    /// 把它算成必须，效果是"没填密钥就不下发 web_search"。反过来（可选）会下发一个
    /// **每次必然 401** 的工具：模型白烧一轮预算、拿到一句失败，还可能改去凭记忆编答案。
    /// 没有密钥时那个网关本来就不可用，所以这里不损失任何真实能力，只是不再假装可用。
    /// </summary>
    public static bool NeedsKeyFor(string providerKey) =>
        providerKey is "tavily" or "brave" or "serper" or "shim";

    public static bool NeedsEndpointFor(string providerKey) => providerKey is "searxng" or "shim";

    /// <summary>
    /// 后端配到"能用"的程度了吗——**这是 web_search 工具要不要下发的唯一判据**。
    ///
    /// 刻意不检查网络可达性：可达与否只有发请求才知道，而那是每次搜索的事。
    /// 这里只判"配置齐不齐"，因为缺配置是**必然失败**，而网络问题只是**这次失败**，
    /// 后者应该由工具如实告诉模型重试或放弃，而不是让它连工具都看不到。
    /// </summary>
    [JsonIgnore]
    public bool SearchReady =>
        SearchEnabled &&
        SearchProviderKey.Length > 0 &&
        (!SearchNeedsKey || SearchApiKey.Trim().Length > 0) &&
        (!SearchNeedsEndpoint || SearchBaseUrl.Trim().Length > 0);

    /// <summary>界面上那行状态说明。空串 = 没问题。</summary>
    [JsonIgnore]
    public string SearchProblem =>
        ProblemFor(SearchEnabled, SearchProviderKey, SearchApiKey.Trim().Length > 0,
                   SearchApiKeyUnreadable, SearchBaseUrl);

    /// <summary>
    /// 判据的**唯一**实现，界面上的实时提示也用它。
    ///
    /// 界面必须在用户**还没点保存**的时候就能说出"还差哪一项"，否则用户会先点
    /// 测试、看到一句失败、再回头猜是哪个框没填。而界面上那些值此刻还在控件里、
    /// 不在配置对象里，所以判据必须能从显式参数算——照抄一份到界面里是错的，
    /// 两份判据一定会走偏（"界面说已就绪、实际没下发工具"这类问题没有任何症状）。
    /// </summary>
    public static string ProblemFor(bool enabled, string providerKey, bool hasKey,
                                    bool keyUnreadable, string baseUrl)
    {
        if (!enabled) return "";
        if (providerKey.Length == 0) return "还没选后端";
        if (NeedsKeyFor(providerKey) && keyUnreadable) return "密钥解不开，请重新填写";
        if (NeedsKeyFor(providerKey) && !hasKey) return "这个后端需要 API Key";
        if (NeedsEndpointFor(providerKey) && (baseUrl ?? "").Trim().Length == 0)
            return "这个后端需要填地址";
        return "";
    }

    // ---------------- 联网抓取（web_fetch） ----------------

    /// <summary>
    /// 是否允许给模型下发 <c>web_fetch</c> 工具。
    ///
    /// **默认开**，与 <see cref="SearchEnabled"/> 的默认关**刻意不同**，理由是判据不同：
    /// 搜索没配后端时那个工具是**必然失败**的（下发它只会让模型白烧一轮预算、
    /// 还可能改去凭记忆编答案），所以必须等用户配好；而抓取的 HTTP 引擎
    /// **不需要任何配置就能用**（一个地址、一次 GET），默认关掉反而会让用户
    /// 以为"这个工具没做出来"。
    ///
    /// 代价是它是本代理唯一一条**由模型决定去向的出网通道**。所以另外配了
    /// <see cref="FetchAllowPrivateHosts"/> 默认拦截内网，见那里的说明。
    /// </summary>
    public bool FetchEnabled { get; set; } = true;

    /// <summary>
    /// 引擎策略：
    /// * <c>auto</c>（默认）—— 先走轻量 HTTP，读不到正文才自动升级到浏览器，见 <see cref="PageReader"/>；
    /// * <c>http</c> —— 只用 HTTP，绝不启动浏览器（不想让那 500 MB 出现时选它）；
    /// * <c>browser</c> —— 只用浏览器，每次都渲染。
    /// </summary>
    public string FetchEngine { get; set; } = "auto";

    /// <summary>
    /// 抓取用的代理，空 = **直连**（连系统代理都不走，行为可预测）。
    ///
    /// 这一项不是可选项 —— 实测本机**直连读不到 danbooru 与 wikipedia**
    /// （连接被拒 / 超时），填上本地代理（如 <c>http://127.0.0.1:10809</c>）才通。
    ///
    /// 两个引擎共用同一个值，但实现方式不同：HTTP 引擎走 <see cref="System.Net.WebProxy"/>，
    /// 浏览器引擎走 <c>--proxy-server</c>。**这个差异是有用的**：实测同一条代理，
    /// .NET 的 HTTP 客户端对某些站点 SSL 握手失败，而 Edge 能读通 —— 于是自动降级
    /// 恰好能把这类站点救回来。
    /// </summary>
    public string FetchProxy { get; set; } = "";

    /// <summary>
    /// 回灌正文的字符上限。默认 20000：够放下一篇长文档，又不至于把用户真正问的东西
    /// 挤出上下文（与 <see cref="SearchMaxResults"/> 是同一条理由）。
    /// </summary>
    public int FetchMaxChars { get; set; } = PageReader.DefaultMaxChars;

    /// <summary>单页抓取超时（秒）。浏览器渲染比 HTTP 慢得多，但也不该等几分钟。</summary>
    public int FetchTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 浏览器可执行文件路径。留空 = 自动查找（Edge 的常见安装位置 → Chrome → PATH）。
    /// 用到的启动参数都是 Chromium 通用的，所以指向 Chrome 或便携版 Chromium 也能用。
    /// </summary>
    public string FetchBrowserPath { get; set; } = "";

    /// <summary>
    /// 浏览器临时目录多久没人用就清理（秒）。**这一个值同时决定三件事**：
    /// 关掉浏览器、删掉它的临时目录、以及"这段时间内再抓取就复用同一个实例"。
    ///
    /// 为什么三件事必须是同一个值：临时目录的路径每次启动都是新的随机名
    /// （<see cref="BrowserFetcher.NewProfileDir"/>，避免两个实例撞车），所以
    /// **浏览器一关，那个目录就再也不会被复用了** —— 留着它纯粹是垃圾。
    /// 分成两个设置会得到一个自相矛盾的组合："浏览器 1 分钟后关、目录 1 小时后清"，
    /// 那 59 分钟里磁盘上躺着一个永远不会被再打开的目录。
    ///
    /// 取值见 <see cref="CleanupSchedule"/>：<c>-1</c> = 永不清理、<c>0</c> = 立即清理、
    /// 正数 = 秒。默认 5 分钟。
    /// </summary>
    public int FetchCleanupSeconds { get; set; } = CleanupSchedule.DefaultSeconds;

    /// <summary>
    /// 旧版字段（"浏览器空闲关闭秒数"，0–3600），只用于读旧文件并迁移到
    /// <see cref="FetchCleanupSeconds"/>。迁移后置为 null，新代码一律用新字段。
    ///
    /// 可空 + <c>WhenWritingNull</c> 的理由与 <see cref="UpstreamBaseUrl"/> 相同：
    /// 让新配置文件里彻底不出现这个键，免得下次有人看到它以为还能手改。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FetchBrowserIdleSeconds { get; set; }

    /// <summary>
    /// 是否允许抓本机/内网地址。**默认关，这是 fail-closed 的**。
    ///
    /// 理由：这个工具的 URL 是**模型给的**，而模型刚刚可能读过一段不可信网页 ——
    /// 一个被注入的页面完全可以指示模型去抓 <c>http://127.0.0.1:48620/…</c>
    /// 或内网里的其它服务。与 <see cref="RequireToken"/> 默认拒绝一切是同一套思路。
    ///
    /// **已知边界**（不假装它不存在）：域名只能按字面判断，所以解析到内网 IP 的
    /// 公网域名拦不住 —— 那需要先解析再连接，而 HttpClient 不提供那个钩子。
    /// 真要抓内网页面（自建 wiki 之类）时打开这个开关。
    /// </summary>
    public bool FetchAllowPrivateHosts { get; set; }

    /// <summary>可选引擎，顺序即界面下拉框的顺序。</summary>
    public static readonly string[] FetchEngines = { "auto", "http", "browser" };

    public static string NormalizeEngine(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "http" => "http",
        "browser" => "browser",
        _ => "auto"
    };

    [JsonIgnore]
    public string FetchEngineKey => NormalizeEngine(FetchEngine);

    /// <summary>
    /// 运行时事实：本机有没有可用的浏览器引擎。**不是配置项**（所以不落盘），
    /// 由 <c>ProxyServer</c> 在构造 <see cref="PageReader"/> 之后填写。
    ///
    /// 默认 **false** 是 fail-closed 的方向：engine=browser 而没人告诉我们浏览器在哪时，
    /// 宁可不下发这个工具，也不要下发一个每次必然失败的。
    /// </summary>
    [JsonIgnore]
    public bool FetchBrowserUsable { get; set; }

    /// <summary>
    /// <c>web_fetch</c> 要不要下发的**唯一判据**。
    /// 与 <see cref="SearchReady"/> 一样，只判"配置与前提齐不齐"，不判网络可达性 ——
    /// 可达与否是每次抓取的事，那种失败应该由工具如实告诉模型，而不是让它连工具都看不到。
    /// </summary>
    [JsonIgnore]
    public bool FetchReady =>
        FetchEnabled && (FetchEngineKey != "browser" || FetchBrowserUsable);

    /// <summary>界面上那行状态说明。空串 = 没问题。</summary>
    [JsonIgnore]
    public string FetchProblem => FetchProblemFor(FetchEnabled, FetchEngineKey, FetchBrowserUsable);

    /// <summary>判据的唯一实现，界面在用户**还没点保存**时也用它（与 <see cref="ProblemFor"/> 同理）。</summary>
    public static string FetchProblemFor(bool enabled, string engineKey, bool browserUsable)
    {
        if (!enabled) return "";
        if (engineKey == "browser" && !browserUsable)
            return "本机没找到 Edge/Chrome，浏览器引擎用不了；改成「自动」或「仅 HTTP」，或指定浏览器路径";
        return "";
    }

    // ---------------- 鉴权 ----------------

    /// <summary>
    /// 是否要求客户端携带代理签发的令牌。
    ///
    /// **默认 true，而且"一枚令牌都没有"时同样拒绝一切**——不是"没配就放行"。
    /// 代理手里有上游密钥，一个不设防的回环端口等于本机任何程序都能白用你的额度
    /// （浏览器里的网页也能向 127.0.0.1 发请求）。
    /// </summary>
    public bool RequireToken { get; set; } = true;

    // ---------------- MCP（独立服务） ----------------

    /// <summary>
    /// 是否把工具作为 **MCP 服务**对外提供。
    ///
    /// 与转发 API 彻底分开：**自己的端口、自己的绑定范围、自己的令牌要求、自己的启停**。
    /// 不复用转发端口是刻意的 —— 两者面向的对象不同（一个面向 agent 平台，一个面向
    /// MCP 客户端），把 MCP 表达成"转发 API 端口上的一个额外路径"会让"只想开其中
    /// 一个"变成一连串的例外（关掉 /v1 但留着 /mcp，或者反过来）。分成两个服务之后，
    /// 每个开关只影响它自己那一半。
    /// </summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>MCP 监听端口。默认 48621，紧跟转发端口的 48620。</summary>
    public int McpPort { get; set; } = DefaultMcpPort;

    public const int DefaultMcpPort = DefaultPort + 1;

    /// <summary>
    /// MCP 的绑定范围，**独立于转发 API**。默认 local：工具是本地能力，
    /// 没有理由跟着转发端口一起开放到网络。
    /// </summary>
    public string McpBindScope { get; set; } = "local";

    /// <summary>MCP 是否要求令牌。默认要（与转发 API 同一套令牌库、同一枚令牌都认）。</summary>
    public bool McpRequireToken { get; set; } = true;

    /// <summary>
    /// 逐工具的开关（键是工具名，值 false 表示不暴露）。
    /// **缺省 = 暴露**：新加工具时不用改配置也不会静默消失；用户关掉哪个才写哪个。
    /// 与"就绪判据"是乘的关系：搜索没配好时，即使这里勾着也不会出现。
    /// </summary>
    public Dictionary<string, bool> McpTools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>这个工具在配置里是不是允许暴露（没写过 = 允许）。</summary>
    public bool McpToolAllowed(string name)
        => !McpTools.TryGetValue(name, out var on) || on;

    [JsonIgnore]
    public System.Net.IPAddress McpListenAddress =>
        McpBindScope == "local" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any;

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

    /// <summary>
    /// 只读地读**真实**配置文件，无视 <see cref="FilePathOverride"/>。
    ///
    /// 存在的理由：回归进程会把 <see cref="FilePathOverride"/> 全局指向临时目录
    /// （见 tests/Program.cs），而 live 联调要用用户的真密钥。给它一条只读通道，
    /// "会不会写掉用户配置"就不再取决于每个测试是否记得重定向。
    ///
    /// **绝不落盘**：文件缺失或损坏时返回内存里的默认值，而不是像 <see cref="Load"/>
    /// 那样 Save()——否则这条"只读"通道本身就成了又一条写掉用户配置的路径。
    /// </summary>
    public static ProxyConfig LoadReal()
    {
        try
        {
            var path = Path.Combine(Dir, "proxy.config.json");
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<ProxyConfig>(File.ReadAllText(path), Options);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* 读不到就用默认值；这里刻意不记日志、不备份、不写盘 */ }

        var fallback = new ProxyConfig();
        fallback.Normalize();
        return fallback;
    }

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

        // MCP 是独立服务，端口自己也要合法；两个端口撞上会变成"先起的那个成功、
        // 后起的那个 bind 失败"，而日志里只有一行，很难和"手抖改错"区分开。
        if (McpPort is < 1024 or > 65535) McpPort = DefaultMcpPort;
        McpBindScope = NormalizeScope(McpBindScope);
        McpTools ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        if (McpPort == Port) McpPort = Port < 65535 ? Port + 1 : Port - 1;

        if (TimeoutSeconds < 5) TimeoutSeconds = 5;
        if (MaxToolRounds is < 1 or > 12) MaxToolRounds = 4;
        if (MaxToolCallsPerRequest is < 1 or > 64) MaxToolCallsPerRequest = 12;
        ModelMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 搜索：后端名收敛到白名单，条数与超时拉到合法区间。
        // 后端填了一个认不出的值（比如手写 "google"）时**不当成错，只是没有后端**——
        // 界面会显示"还没选后端"，用户看得见，而不是一个静默变成默认后端的值。
        SearchProvider = NormalizeProvider(SearchProvider);
        SearchBaseUrl = (SearchBaseUrl ?? "").Trim();
        SearchSniHost = (SearchSniHost ?? "").Trim();
        if (SearchMaxResults is < 1 or > 10) SearchMaxResults = 5;
        if (SearchTimeoutSeconds is < 5 or > 120) SearchTimeoutSeconds = 30;

        // 抓取：引擎名收敛到白名单，数值拉到合法区间。
        // 引擎填了认不出的值（手写 "edge"）时归到 auto，而不是留一个空串 ——
        // auto 是"能用就用"的安全默认，而且界面下拉框有它，用户看得见结果。
        FetchEngine = NormalizeEngine(FetchEngine);
        FetchProxy = (FetchProxy ?? "").Trim();
        FetchBrowserPath = (FetchBrowserPath ?? "").Trim();
        if (FetchMaxChars is < 500 or > 200_000) FetchMaxChars = PageReader.DefaultMaxChars;
        if (FetchTimeoutSeconds is < 5 or > 180) FetchTimeoutSeconds = 30;
        MigrateLegacyFetchIdle();
        FetchCleanupSeconds = CleanupSchedule.Normalize(FetchCleanupSeconds);

        Providers ??= new List<ProviderProfile>();
        MigrateLegacyUpstream();

        foreach (var p in Providers) p.Normalize();
        // 同一份配置里 Id 重复会让"按 Id 找站点"随机命中一个，而界面上两个条目
        // 看起来都正常。手改配置文件才可能出现，所以在这里补一道。
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Providers.Where(p => !ids.Add(p.Id)).ToList())
        {
            p.Id = ProviderProfile.NewId();
            ids.Add(p.Id);
        }

        // 一个站点都没有时给一个空站点：这样"默认站点"永远存在，
        // 界面一打开就有东西可编辑，请求也不会因为 Providers 为空而走进
        // 一条没被任何回归覆盖的分支。
        if (Providers.Count == 0)
            Providers.Add(new ProviderProfile { Name = "默认站点", BaseUrl = DefaultProviderBaseUrl });

        if (ProviderById(DefaultProviderId) is null) DefaultProviderId = Providers[0].Id;
    }

    /// <summary>
    /// 把旧版"单一上游"配置搬成一个站点。
    ///
    /// **密文原样搬走**（两边用同一个 DPAPI 熵），所以不必解密再加密——少一次
    /// 经手就少一次出错的机会，而且即使当前账户解不开（换过机器）也不会因此丢东西。
    ///
    /// 搬完就把旧字段清空，让配置文件收敛到新形状：留着两份真值来源，
    /// 下次改了一处而另一处没改，"设置里显示 A、实际请求发给 B"这类问题就来了。
    /// </summary>
    private void MigrateLegacyUpstream()
    {
        var hasLegacy = !string.IsNullOrWhiteSpace(UpstreamBaseUrl) ||
                        !string.IsNullOrEmpty(UpstreamKeyProtected);

        if (Providers.Count == 0 && hasLegacy)
        {
            var p = new ProviderProfile
            {
                Name = ProviderProfile.HostOf(UpstreamBaseUrl),
                BaseUrl = string.IsNullOrWhiteSpace(UpstreamBaseUrl) ? DefaultProviderBaseUrl : UpstreamBaseUrl!.Trim(),
                ApiKeyProtected = UpstreamKeyProtected ?? ""
            };
            Providers.Add(p);
            DefaultProviderId = p.Id;
            ProxyLog.Write("已把旧版单上游配置迁移为模型提供商：" + p.DisplayName);
        }

        UpstreamBaseUrl = null;
        UpstreamKeyProtected = null;
    }

    /// <summary>
    /// 把旧版"浏览器空闲关闭秒数"搬成新的"临时目录清理时间"。
    ///
    /// 只在旧值**不等于新默认值**时才搬：旧字段存在但值就是 300（老默认）时，
    /// 新字段的默认值本来就是 300，搬过去是空操作 —— 而"照搬一遍"会让
    /// 一份手改过的配置里两个键同时存在，谁赢说不清。用户真正改过的值（0 或别的秒数）
    /// 才需要保下来，否则症状是"我明明设了立即清理，升级后又变回 5 分钟"，且没有任何提示。
    /// </summary>
    private void MigrateLegacyFetchIdle()
    {
        if (FetchBrowserIdleSeconds is { } legacy && legacy != CleanupSchedule.DefaultSeconds)
        {
            // 旧值 0 = 每次抓完立刻关，正好就是新的"立即清理"。
            FetchCleanupSeconds = legacy <= 0
                ? CleanupSchedule.Immediate
                : Math.Min(legacy, CleanupSchedule.MaxSeconds);
            ProxyLog.Write("已把旧版「浏览器空闲关闭 " + legacy + " 秒」迁移为「临时目录清理 " +
                           CleanupSchedule.LabelFor(FetchCleanupSeconds) + "」");
        }
        FetchBrowserIdleSeconds = null;
    }

    private static readonly object SaveLock = new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, Options);
            // 加锁：界面保存与"按需拉取模型表"（来自 HTTP 请求线程）会同时写这个文件，
            // 交错写下去会得到一个半截 JSON，而下次启动时它会被当成"配置坏了"备份掉。
            lock (SaveLock) File.WriteAllText(ConfigPath, json);
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
