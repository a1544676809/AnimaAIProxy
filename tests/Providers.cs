using System.Net.Http;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>造配置的小工具：多处测试都要"给配置加一个站点"，集中一处免得各写各的。</summary>
public static class Fixture
{
    public static ProviderProfile AddProvider(ProxyConfig cfg, string baseUrl, string apiKey = "",
                                              string headers = "", bool enabled = true,
                                              string name = "", params string[] models)
    {
        var p = new ProviderProfile
        {
            Name = name,
            BaseUrl = baseUrl,
            CustomHeaders = headers,
            Enabled = enabled
        };
        if (!string.IsNullOrEmpty(apiKey)) p.ApiKey = apiKey;
        foreach (var m in models) p.Models.Add(new ModelEntry { Id = m });

        cfg.Providers.Add(p);
        if (string.IsNullOrWhiteSpace(cfg.DefaultProviderId)) cfg.DefaultProviderId = p.Id;
        return p;
    }
}

/// <summary>
/// 多模型提供商：请求头解析、按模型名路由、模型表聚合、旧配置迁移。
///
/// 这一层是"代理带多个站点"的全部判定，而且**全是纯逻辑**——不需要服务器、
/// 不需要网络就能验。真正会出错的也正是这里：一个模型名路由到了错的站点，
/// 症状只是"回话的风格不对"或一次莫名其妙的 401，很难追到路由上去。
/// </summary>
public static class Providers
{
    public static void Run()
    {
        HarnessGuard();
        ConfigPathRedirect();
        Headers();
        Routing();
        Aggregate();
        Fetched();
        Migration();
    }

    // ---------------- 全局防线：回归不许碰用户的真数据 ----------------

    /// <summary>
    /// 断言 <see cref="Program.GuardUserData"/> 这道**全局**防线真的在生效。
    ///
    /// 逐节重定向是可选的，而这里断言的是"不必依赖任何一节记得重定向"。
    /// 这条断言失效时同样不会有症状——真配置被写掉是跑完之后才发现的，
    /// 所以必须由断言来钉住，而不是靠人记得。
    /// </summary>
    private static void HarnessGuard()
    {
        Harness.Section("回归进程默认不碰用户真数据（配置与令牌库已全局重定向）");

        var realCfg = Path.Combine(ProxyConfig.Dir, "proxy.config.json");
        var realTok = Path.Combine(ProxyConfig.Dir, "proxy.tokens.json");

        Harness.True("配置路径在跑第一节之前就已被重定向", ProxyConfig.FilePathOverride is not null);
        Harness.True("配置路径不指向真配置", ProxyConfig.ConfigPath != realCfg);
        Harness.True("令牌库路径在跑第一节之前就已被重定向", TokenStore.FilePathOverride is not null);
        Harness.True("令牌库路径不指向真令牌库", TokenStore.FilePath != realTok);

        // LoadReal 是 live 联调读真配置的唯一通道，它必须是**只读**的：
        // 若它顺手 Save()，那这条"只读"通道本身就成了新的写掉配置的路径。
        var existed = File.Exists(realCfg);
        var before = existed ? File.ReadAllBytes(realCfg) : null;
        _ = ProxyConfig.LoadReal();
        Harness.True("LoadReal 读真配置不抛异常", true);
        if (existed)
            Harness.True("LoadReal 不改动真配置一个字节",
                File.ReadAllBytes(realCfg).AsSpan().SequenceEqual(before));
        else
            Harness.True("LoadReal 不会凭读动作创建出真配置", !File.Exists(realCfg));

        // LoadReal 读的是真配置，但**不能顺手改掉重定向**：它若是把
        // FilePathOverride 清成 null（"读完恢复原状"这种看起来合理的写法），
        // 后面的每一节就都开始写用户的真配置了——防线被一次"善意"的读取拆掉。
        Harness.True("LoadReal 读真配置之后，重定向仍然生效（没有被「读完恢复」拆掉）",
            ProxyConfig.FilePathOverride is not null && ProxyConfig.ConfigPath != realCfg);
    }

    // ---------------- 配置文件路径可被测试重定向 ----------------

    /// <summary>
    /// 这一节存在的原因是一次真实事故：<c>ProxyServer</c> 在"按需拉取模型表"时
    /// 会调用 <see cref="ProxyConfig.Save"/>，而回归里若不重定向配置路径，
    /// 跑一次 <c>e2e</c> 就把用户的真配置盖成了测试用的临时配置——
    /// 站点、密钥全没了，而且没有任何提示。
    ///
    /// 断言的是"重定向真的生效"，因为这道防线失效时同样不会有任何症状。
    /// </summary>
    private static void ConfigPathRedirect()
    {
        Harness.Section("配置路径可被测试重定向（回归不许写掉用户的真配置）");

        var real = Path.Combine(ProxyConfig.Dir, "proxy.config.json");
        var tmp = Path.Combine(Path.GetTempPath(), "aip-cfg-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var old = ProxyConfig.FilePathOverride;

        try
        {
            ProxyConfig.FilePathOverride = tmp;
            Harness.Eq("重定向后 ConfigPath 指向临时文件", ProxyConfig.ConfigPath, tmp);
            Harness.True("重定向后不再指向真配置", ProxyConfig.ConfigPath != real);

            var c = new ProxyConfig { Port = 45999 };
            c.Save();
            Harness.True("Save 写的是临时文件", File.Exists(tmp));
            Harness.Eq("读回来的就是刚写的那份", ProxyConfig.Load().Port, 45999);
        }
        finally
        {
            ProxyConfig.FilePathOverride = old;
            try { File.Delete(tmp); } catch { /* 删不掉不影响结论 */ }
        }

        // 还原后回到的是**进程级防线**设的那个临时路径，而不是真配置——
        // 这里曾经断言"回到真配置"，那是逐节重定向时代的写法：全局防线一上，
        // 它就变成了"还原等于把防线拆掉"。钉住现在的真值。
        Harness.Eq("还原后回到进程级防线设的路径（而不是真配置）", ProxyConfig.FilePathOverride, old);
        Harness.True("还原后依然不指向真配置", ProxyConfig.ConfigPath != real);
    }

    // ---------------- 自定义请求头 ----------------

    private static void Headers()
    {
        Harness.Section("自定义请求头：解析");

        var r = CustomHeaders.Parse("""
            # 注释行会被忽略
            x-opencode-session: abc123
            HTTP-Referer = https://example.com

            X-Title: My App
            """);
        Harness.Eq("注释与空行不算条目", r.Entries.Count, 3);
        Harness.Eq("冒号形式的名字对", r.Entries[0].Name, "x-opencode-session");
        Harness.Eq("冒号形式的值对（首尾空白去掉）", r.Entries[0].Value, "abc123");
        Harness.Eq("等号形式也认，且值里的冒号不会被误切（取最早的分隔符）",
                   r.Entries[1].Value, "https://example.com");
        Harness.Eq("等号形式的头名干净（不含被误切进来的片段）", r.Entries[1].Name, "HTTP-Referer");
        Harness.Eq("没有坏行", r.Rejected.Count, 0);

        var colonInValue = CustomHeaders.Parse("X-Token: a=b");
        Harness.Eq("值里的等号不参与切分", colonInValue.Entries[0].Value, "a=b");
        Harness.Eq("名字只到冒号为止", colonInValue.Entries[0].Name, "X-Token");

        // 这几个是"填了却没用"的典型来源。**必须被明确拒绝并给出原因**，
        // 否则用户看到的只是"填了没生效"，无从查起。
        var bad = CustomHeaders.Parse("""
            X-Bad Name: v
            X-Empty:
            Authorization: Bearer sk-xxx
            Dup: 1
            Dup: 2
            没有分隔符的一行
            """);
        Harness.Eq("合法的只有 Dup 的第一条", bad.Entries.Count, 1);
        Harness.Eq("五条坏行各自被拒（不是静默丢掉）", bad.Rejected.Count, 5);
        Harness.True("头名非法时说明原因",
                     bad.Rejected.Any(x => x.Contains("X-Bad Name") && x.Contains("非法字符")));
        Harness.True("空值被拒", bad.Rejected.Any(x => x.Contains("X-Empty") && x.Contains("值是空的")));
        Harness.True("手填 Authorization 被拒（它会和 API Key 拼成一个坏 token）",
                     bad.Rejected.Any(x => x.Contains("Authorization") && x.Contains("程序自己设置")));
        Harness.True("重复头被拒", bad.Rejected.Any(x => x.Contains("Dup") && x.Contains("重复")));
        Harness.True("缺分隔符被拒", bad.Rejected.Any(x => x.Contains("分隔符")));
        Harness.Eq("DescribeProblems 把问题拼成一句话",
                   CustomHeaders.DescribeProblems("Bad Name: v").Length > 0, true);
        Harness.Eq("没问题时 DescribeProblems 是空串",
                   CustomHeaders.DescribeProblems("X-Ok: 1"), "");

        Harness.Section("自定义请求头：应用到请求（含实体头的分流）");

        // 实体头加到 Headers 上会被**静默忽略**——这条是解析器存在的理由，
        // 所以在这里把它钉住：BCL 确实会返回 false 而不抛。
        using (var probe = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/"))
        {
            Harness.Eq("头名非法时 TryAddWithoutValidation 返回 false 而不抛（静默失败的证据）",
                       probe.Headers.TryAddWithoutValidation("X-Bad Name", "v"), false);
        }

        var parsed = CustomHeaders.Parse("X-Title: T\nContent-Type: application/json; charset=utf-8");
        using (var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/"))
        {
            req.Content = new StringContent("{}");
            CustomHeaders.Apply(req, parsed);

            Harness.Eq("普通头进了 Headers",
                       req.Headers.TryGetValues("X-Title", out var tv) ? string.Join(",", tv) : "", "T");

            // 实体头在 Headers 上**连问都问不了**：Contains 会抛
            // "Misused header name"。这就是解析器必须自己分流的第二个理由——
            // 调用点若"顺手"把它加到 Headers，得到的不是静默无效就是一次异常。
            var threw = false;
            try { _ = req.Headers.Contains("Content-Type"); }
            catch (InvalidOperationException) { threw = true; }
            Harness.True("实体头在 Headers 上会抛（所以必须走 Content.Headers）", threw);

            Harness.True("实体头走的是 Content.Headers",
                         req.Content.Headers.ContentType?.MediaType == "application/json");
        }

        using (var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/"))
        {
            // 内容还没设时 Apply 一个**实体头**：必须自己兜底建一个 Content，
            // 否则 req.Content.Headers 根本不存在，那一行会静默不生效。
            CustomHeaders.Apply(req, CustomHeaders.Parse("Content-Type: text/plain"));
            Harness.True("内容还没设时 Apply 兜底建了 Content（实体头才有地方放）",
                         req.Content is not null);
            Harness.Eq("兜底之后实体头确实生效了",
                       req.Content!.Headers.ContentType?.MediaType, "text/plain");
        }
    }

    // ---------------- 按模型名路由 ----------------

    private static void Routing()
    {
        Harness.Section("按模型名路由到站点");

        var cfg = new ProxyConfig();
        var a = Fixture.AddProvider(cfg, "http://a.example/v1", name: "站点A", models: new[] { "a1", "a2" });
        var b = Fixture.AddProvider(cfg, "http://b.example/v1", name: "站点B", models: new[] { "b1" });
        cfg.DefaultProviderId = a.Id;

        Harness.Eq("模型表命中站点A", ModelCatalog.Resolve(cfg, "a1").Provider?.Id, a.Id);
        Harness.Eq("模型名大小写不敏感", ModelCatalog.Resolve(cfg, "B1").Provider?.Id, b.Id);
        Harness.Eq("命中的是模型表里的那个名字（发往上游的就是它）",
                   ModelCatalog.Resolve(cfg, "a1").UpstreamModel, "a1");
        Harness.Eq("客户端报的名字原样记着（响应里回写用它）",
                   ModelCatalog.Resolve(cfg, "a1").ExposedModel, "a1");
        Harness.True("命中的判定值得进日志", ModelCatalog.Resolve(cfg, "a1").Notable);

        var unknown = ModelCatalog.Resolve(cfg, "local-llama");
        Harness.Eq("不在模型表里 → 默认站点", unknown.Provider?.Id, a.Id);
        Harness.Eq("不在模型表里 → 模型名原样透传（上游自己会回准确的错）",
                   unknown.UpstreamModel, "local-llama");
        Harness.Eq("这种判定不写日志（否则每个请求都刷一行）", unknown.Notable, false);

        // 关掉一个模型 = 它不再把请求引到这个站点。
        // **把默认站点改成 B**，这样"仍然命中 A"和"回落到 B"才区分得开——
        // 默认站点仍是 A 的话，这条断言无论如何都会过，等于什么都没测。
        cfg.DefaultProviderId = b.Id;
        a.FindModel("a2")!.Enabled = false;
        var offModel = ModelCatalog.Resolve(cfg, "a2");
        Harness.Eq("关掉的模型不再路由到它的站点", offModel.Provider?.Id, b.Id);
        Harness.Eq("模型名仍然原样发出（不猜、不改）", offModel.UpstreamModel, "a2");
        Harness.Eq("这是回落，不值得每个请求都写一行日志", offModel.Notable, false);

        // 关掉一个站点 = 它下面的模型全部不再引向它
        b.Enabled = false;
        var offSite = ModelCatalog.Resolve(cfg, "b1");
        Harness.Eq("停用的站点不再接受路由", offSite.Provider?.Id, a.Id);
        Harness.Eq("模型名仍然原样发出", offSite.UpstreamModel, "b1");
        b.Enabled = true;
        cfg.DefaultProviderId = a.Id;

        Harness.Section("模型名映射优先于模型表");

        cfg.ModelMap["gpt-4o"] = "b1";
        var mapped = ModelCatalog.Resolve(cfg, "gpt-4o");
        Harness.Eq("映射先换名字", mapped.UpstreamModel, "b1");
        Harness.Eq("再按换过的名字路由到站点B", mapped.Provider?.Id, b.Id);
        Harness.True("映射的判定进日志（用户配了它就想看到生效）", mapped.Notable);
        Harness.True("日志里说清了换成了什么", mapped.Note.Contains("gpt-4o → b1"));
        cfg.ModelMap.Clear();

        Harness.Section("默认站点的选取与失效");

        cfg.DefaultProviderId = b.Id;
        Harness.Eq("指针指到谁就是谁", cfg.DefaultProvider?.Id, b.Id);

        cfg.DefaultProviderId = "不存在的-id";
        Harness.Eq("指针失效 → 回落到第一个启用的站点（而不是让请求全废）",
                   cfg.DefaultProvider?.Id, a.Id);

        b.Enabled = false;
        cfg.DefaultProviderId = b.Id;
        Harness.Eq("指针指向一个已停用的站点 → 回落到启用的那个", cfg.DefaultProvider?.Id, a.Id);
        b.Enabled = true;

        foreach (var p in cfg.Providers) p.Enabled = false;
        Harness.Eq("全部停用 → 没有可用站点（如实报错，而不是偷偷发给一个停用的站点）",
                   cfg.DefaultProvider, null);
        Harness.Eq("此时路由也拿不到站点", ModelCatalog.Resolve(cfg, "a1").Provider, null);
        foreach (var p in cfg.Providers) p.Enabled = true;

        var empty = new ProxyConfig();
        Harness.Eq("一个站点都没有时给 null（调用方据此报「没有可用的模型提供商」）",
                   ModelCatalog.Resolve(empty, "x").Provider, null);
        Harness.Eq("空配置也要有可用的默认站点（Normalize 之后）", AfterNormalize().DefaultProvider is not null, true);
    }

    private static ProxyConfig AfterNormalize()
    {
        var c = new ProxyConfig();
        c.Normalize();
        return c;
    }

    // ---------------- 对外公布的模型表 ----------------

    private static void Aggregate()
    {
        Harness.Section("对外公布的模型表 = 勾选过的结果");

        var cfg = new ProxyConfig();
        var a = Fixture.AddProvider(cfg, "http://a.example/v1", name: "站点A", models: new[] { "a1", "a2" });
        var b = Fixture.AddProvider(cfg, "http://b.example/v1", name: "站点B", models: new[] { "b1" });
        a.FindModel("a2")!.Enabled = false;

        var ids = Ids(ModelCatalog.ToOpenAiModels(cfg));
        Harness.True("启用的模型在表里", ids.Contains("a1"));
        Harness.True("关掉的模型不在表里（用户关它就是为了让它消失）", !ids.Contains("a2"));
        Harness.True("第二个站点的模型也在", ids.Contains("b1"));
        Harness.Eq("一共两个", ids.Count, 2);

        b.Enabled = false;
        ids = Ids(ModelCatalog.ToOpenAiModels(cfg));
        Harness.True("停用的站点整个从表里消失", !ids.Contains("b1"));
        Harness.Eq("只剩站点A那一个", ids.Count, 1);
        b.Enabled = true;

        cfg.ModelMap["gpt-4o"] = "a1";
        ids = Ids(ModelCatalog.ToOpenAiModels(cfg));
        Harness.True("映射表里的名字也报出去（平台启动时会拿它校验配置）", ids.Contains("gpt-4o"));
        Harness.True("映射到已存在的模型时不重复出现", ids.Count(x => x == "a1") == 1);
        Harness.Eq("总数为 3", ids.Count, 3);

        var owners = ModelCatalog.ToOpenAiModels(cfg)
            .Select(n => ChatWire.GetString(n as JsonObject, "owned_by")).ToList();
        Harness.True("owned_by 填的是站点显示名（一眼看出模型来自哪里）", owners.Contains("站点A"));
        Harness.True("映射出来的名字标成 animaiproxy", owners.Contains("animaiproxy"));
        cfg.ModelMap.Clear();

        Harness.Section("/models 响应的四种形状");

        Harness.Eq("标准 data 数组",
                   string.Join(",", ModelCatalog.ParseModelIds("""{"data":[{"id":"m1"},{"id":"m2"}]}""")), "m1,m2");
        Harness.Eq("models 数组",
                   string.Join(",", ModelCatalog.ParseModelIds("""{"models":[{"name":"n1"}]}""")), "n1");
        Harness.Eq("裸字符串数组",
                   string.Join(",", ModelCatalog.ParseModelIds("""["x","y"]""")), "x,y");
        Harness.Eq("对象数组里取 model 字段",
                   string.Join(",", ModelCatalog.ParseModelIds("""[{"model":"z"}]""")), "z");
        Harness.Eq("重复的模型名只留一条",
                   ModelCatalog.ParseModelIds("""{"data":[{"id":"a"},{"id":"A"}]}""").Count, 1);
        Harness.Eq("不认识的形状 → 空表而不是抛", ModelCatalog.ParseModelIds("""{"nope":1}""").Count, 0);
        Harness.Eq("不是 JSON → 空表而不是抛", ModelCatalog.ParseModelIds("这不是 json").Count, 0);
        Harness.Eq("空串 → 空表", ModelCatalog.ParseModelIds("").Count, 0);
    }

    private static List<string> Ids(JsonArray arr) =>
        arr.Select(n => ChatWire.GetString(n as JsonObject, "id")).ToList();

    // ---------------- 拉取模型表 ----------------

    private static void Fetched()
    {
        Harness.Section("拉取模型表：保留用户的开关状态");

        var p = new ProviderProfile { BaseUrl = "http://x/v1" };
        p.Models.Add(new ModelEntry { Id = "keep-on" });
        p.Models.Add(new ModelEntry { Id = "keep-off", Enabled = false });
        p.Models.Add(new ModelEntry { Id = "gone" });
        p.Models.Add(new ModelEntry { Id = "typed-by-hand", Manual = true });

        p.ApplyFetchedModels(new[] { "keep-on", "keep-off", "brand-new" });

        Harness.Eq("上游不再报的模型被移除", p.FindModel("gone"), null);
        Harness.True("新拉到的默认启用", p.FindModel("brand-new")?.Enabled == true);
        Harness.True("**用户关掉的仍然是关的**（刷新不该把他的设置打开）",
                     p.FindModel("keep-off")?.Enabled == false);
        Harness.True("手工加的条目刷新后还在（本地服务不实现 /models，填了又消失像 bug）",
                     p.FindModel("typed-by-hand") is not null);
        Harness.Eq("记录下拉取时间", p.ModelsFetchedAt is not null, true);
        Harness.Eq("条目总数为 4", p.Models.Count, 4);

        var q = new ProviderProfile();
        q.ApplyFetchedModels(new[] { "a", "a", " " });
        Harness.Eq("同一批里的重复与空白被清掉", q.Models.Count, 1);

        Harness.Section("站点自身的归一化");

        var dup = new ProviderProfile { Id = "", BaseUrl = " http://y/v1 ", Name = " 名字 " };
        dup.Models.Add(new ModelEntry { Id = "m" });
        dup.Models.Add(new ModelEntry { Id = " M " });
        dup.Models.Add(new ModelEntry { Id = "" });
        dup.Normalize();
        Harness.True("空 Id 补一个", dup.Id.Length > 0);
        Harness.Eq("地址去空白", dup.BaseUrl, "http://y/v1");
        Harness.Eq("名字去空白", dup.Name, "名字");
        Harness.Eq("同名模型只留一条（两条会让开关看起来失灵）", dup.Models.Count, 1);

        Harness.Eq("显示名回落：没起名时用主机名",
                   new ProviderProfile { BaseUrl = "api.deepseek.com" }.DisplayName, "api.deepseek.com");
        Harness.Eq("没有 scheme 也解析得出主机名（用户常常先敲半截）",
                   ProviderProfile.HostOf("api.deepseek.com/v1"), "api.deepseek.com");
        Harness.Eq("去掉端口", ProviderProfile.HostOf("http://127.0.0.1:8080/v1"), "127.0.0.1");
        Harness.Eq("什么都没有时的占位",
                   new ProviderProfile().DisplayName, "未命名站点");
    }

    // ---------------- 旧版单上游配置的迁移 ----------------

    private static void Migration()
    {
        Harness.Section("旧版单上游配置 → 模型提供商");

        var roundTrip = Secrets.RoundTripWorks(Secrets.UpstreamEntropy);

        // 直接构造"旧文件"的样子：反序列化之后旧字段有值、Providers 为空。
        var old = new ProxyConfig
        {
            UpstreamBaseUrl = "https://api.deepseek.com/v1",
            UpstreamKeyProtected = roundTrip
                ? Secrets.Protect("sk-old-key-123456", Secrets.UpstreamEntropy)
                : "not-a-real-ciphertext"
        };
        var cipherBefore = old.UpstreamKeyProtected;

        old.Normalize();

        Harness.Eq("迁移出一个站点", old.Providers.Count, 1);
        Harness.Eq("地址原样搬过去", old.Providers[0].BaseUrl, "https://api.deepseek.com/v1");
        Harness.Eq("名字从主机名派生（旧配置没有名字）", old.Providers[0].Name, "api.deepseek.com");
        Harness.Eq("密文**原样**搬走（不解密再加密，少一次经手）",
                   old.Providers[0].ApiKeyProtected, cipherBefore);
        Harness.Eq("默认站点指向它", old.DefaultProviderId, old.Providers[0].Id);
        Harness.True("旧字段被置空（不留两份真值来源）", old.UpstreamBaseUrl is null);
        Harness.True("旧密钥字段也置空", old.UpstreamKeyProtected is null);

        if (roundTrip)
        {
            Harness.Eq("迁移后密钥能正常读出来（缓存没挡住）",
                       old.Providers[0].ApiKey, "sk-old-key-123456");
            Harness.Eq("HasKey 为真", !string.IsNullOrEmpty(old.Providers[0].ApiKeyProtected), true);
        }

        var json = System.Text.Json.JsonSerializer.Serialize(old);
        Harness.True("新配置里不再写旧字段（文件收敛到一种形状）",
                     !json.Contains("upstreamBaseUrl", StringComparison.OrdinalIgnoreCase));

        Harness.Section("迁移不会重复执行、也不会覆盖已有站点");

        var fresh = new ProxyConfig();
        fresh.Normalize();
        Harness.Eq("空配置给一个默认站点（界面一打开就有东西可编辑）", fresh.Providers.Count, 1);
        Harness.Eq("默认站点的地址是预填的那个", fresh.Providers[0].BaseUrl, ProxyConfig.DefaultProviderBaseUrl);

        var both = new ProxyConfig
        {
            UpstreamBaseUrl = "http://legacy/v1",
            UpstreamKeyProtected = "legacy-cipher"
        };
        Fixture.AddProvider(both, "http://real/v1", name: "新站点", models: new[] { "m" });
        both.Normalize();
        Harness.Eq("已有站点时不再造第二个", both.Providers.Count, 1);
        Harness.Eq("用的是已有的那个", both.Providers[0].Name, "新站点");
        Harness.True("旧字段照样置空", both.UpstreamBaseUrl is null);

        var twice = new ProxyConfig { UpstreamBaseUrl = "http://legacy/v1" };
        twice.Normalize();
        twice.Normalize();
        Harness.Eq("重复 Normalize 不会越迁越多", twice.Providers.Count, 1);

        Harness.Section("配置里的站点 Id 重复");

        var dup = new ProxyConfig();
        var p1 = Fixture.AddProvider(dup, "http://one/v1");
        var p2 = Fixture.AddProvider(dup, "http://two/v1");
        p2.Id = p1.Id;
        dup.Normalize();
        Harness.True("重复 Id 被改掉（否则「按 Id 找站点」会随机命中）", p1.Id != p2.Id);
    }
}
