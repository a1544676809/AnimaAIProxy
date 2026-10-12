using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 配置、令牌与密钥的回归。
///
/// 密钥那一节会真的调一次 DPAPI。它在非 Windows 上必然失败，所以
/// 那一条按"能否往返"如实报告，而不是硬断言——把一个平台相关的行为
/// 伪装成"必须通过"，会让人在别的平台上看到一片红而不知道该信哪条。
/// </summary>
public static class Tokens
{
    public static void Run()
    {
        Harness.Section("令牌：生成与校验");

        var store = new TokenStore();
        var token = TokenStore.Create("Claude Desktop", "本机测试", out var plaintext);

        Harness.True("明文带 aip- 前缀（一眼看出是代理签发的）",
                     plaintext.StartsWith(TokenStore.Prefix, StringComparison.Ordinal));
        Harness.True("明文足够长", plaintext.Length > 30);
        Harness.Eq("库里不存明文，只存哈希", token.Hash.Length > 0, true);
        Harness.True("哈希不是明文", !token.Hash.Contains(plaintext, StringComparison.Ordinal));

        store.Tokens.Add(token);

        Harness.True("正确的令牌能通过", store.Authenticate(plaintext) is not null);
        Harness.Eq("通过之后记了一次使用", token.UseCount, 1L);
        Harness.True("使用时间被记下", token.LastUsedAt is not null);
        Harness.True("错误的令牌不通过", store.Authenticate(plaintext + "x") is null);
        Harness.True("空令牌不通过", store.Authenticate("") is null);
        Harness.True("null 不通过", store.Authenticate(null) is null);
        Harness.True("前缀一样但后面不同也不通过",
                     store.Authenticate(plaintext[..20] + "AAAAAAAAAAAAAAAAAAAAAA") is null);

        token.Enabled = false;
        Harness.True("停用后不再通过", store.Authenticate(plaintext) is null);
        token.Enabled = true;

        var store2 = new TokenStore();
        Harness.Eq("空库没有启用中的令牌", store2.HasEnabled, false);
        Harness.True("空库里什么都通不过", store2.Authenticate(plaintext) is null);

        Harness.Section("令牌提取：三种常见写法");

        Harness.Eq("Bearer（OpenAI 标准）",
                   TokenStore.ExtractToken("Bearer aip-abc"), "aip-abc");
        Harness.Eq("bearer 小写也认",
                   TokenStore.ExtractToken("bearer aip-abc"), "aip-abc");
        Harness.Eq("裸令牌", TokenStore.ExtractToken("aip-abc"), "aip-abc");
        Harness.Eq("x-api-key 优先", TokenStore.ExtractToken("Bearer x", "aip-from-header"), "aip-from-header");
        Harness.Eq("都没有 → null", TokenStore.ExtractToken(null), null);
        Harness.Eq("空串 → null", TokenStore.ExtractToken("   "), null);

        Harness.Section("配置：越界值拉回合法区间");

        var cfg = new ProxyConfig
        {
            Port = 99999,
            TimeoutSeconds = 0,
            MaxToolRounds = 100,
            MaxToolCallsPerRequest = 999,
            BindScope = "NONSENSE",
            UpstreamBaseUrl = "   "
        };
        cfg.Normalize();
        Harness.Eq("端口越界 → 默认", cfg.Port, ProxyConfig.DefaultPort);
        Harness.Eq("超时过小 → 下限", cfg.TimeoutSeconds, 5);
        Harness.Eq("轮数越界 → 默认", cfg.MaxToolRounds, 4);
        Harness.Eq("单次调用上限越界 → 默认", cfg.MaxToolCallsPerRequest, 12);
        Harness.Eq("监听范围非法 → local", cfg.BindScope, "local");
        Harness.True("空上游地址 → 有默认值", cfg.UpstreamBaseUrl.Length > 0);
        Harness.Eq("local 时只监听回环",
                   cfg.ListenAddress.ToString(), "127.0.0.1");
        Harness.True("ModelMap 不会被清成 null", cfg.ModelMap is not null);

        var lan = new ProxyConfig { BindScope = "LAN" };
        lan.Normalize();
        Harness.Eq("LAN 归一化成小写", lan.BindScope, "lan");
        Harness.Eq("lan 时监听所有网卡", lan.ListenAddress.ToString(), "0.0.0.0");

        Harness.Section("模型名映射");

        var m = new ProxyConfig();
        m.ModelMap["gpt-4o"] = "deepseek-v4-pro";
        var up = new UpstreamClient(m);
        Harness.Eq("配了映射就换", up.MapModel("gpt-4o"), "deepseek-v4-pro");
        Harness.Eq("大小写不敏感", up.MapModel("GPT-4O"), "deepseek-v4-pro");
        Harness.Eq("没配映射的原样透传（不猜）", up.MapModel("some-local-model"), "some-local-model");
        Harness.Eq("空模型名原样", up.MapModel(""), "");
        up.Dispose();

        var fb = new ProxyConfig { FallbackModelEnabled = true, FallbackModel = "backup-model" };
        var up2 = new UpstreamClient(fb);
        Harness.Eq("开了兜底时未映射的走兜底", up2.MapModel("whatever"), "backup-model");
        Harness.Eq("默认不开兜底：静默换模型比报错更难查",
                   new ProxyConfig().FallbackModelEnabled, false);
        up2.Dispose();

        Harness.Section("上游密钥（DPAPI）");

        var roundTrip = Secrets.RoundTripWorks(Secrets.UpstreamEntropy);
        if (roundTrip)
        {
            var c = new ProxyConfig();
            Harness.Eq("没设密钥时明文为空", c.UpstreamApiKey, "");
            Harness.Eq("没设时 HasUpstreamKey 为假", c.HasUpstreamKey, false);

            c.UpstreamApiKey = "sk-secret-value-12345";
            Harness.Eq("设进去之后能读回来", c.UpstreamApiKey, "sk-secret-value-12345");
            Harness.Eq("HasUpstreamKey 为真", c.HasUpstreamKey, true);
            Harness.True("落盘的是密文而不是明文",
                         !c.UpstreamKeyProtected.Contains("sk-secret-value-12345", StringComparison.Ordinal));

            var json = System.Text.Json.JsonSerializer.Serialize(c);
            Harness.True("序列化结果里不含明文密钥（JsonIgnore 生效）",
                         !json.Contains("sk-secret-value-12345", StringComparison.Ordinal));
            Harness.True("但密文在里面", json.Contains("UpstreamKeyProtected", StringComparison.Ordinal));

            c.UpstreamApiKey = "";
            Harness.Eq("能清空", c.UpstreamApiKey, "");
            Harness.Eq("清空后 HasUpstreamKey 为假", c.HasUpstreamKey, false);
        }
        else
        {
            Harness.Note("DPAPI 在本机不可用（非 Windows？），密钥往返这一节跳过。");
        }
    }
}
