using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// IP 名单：解析（v4 / v6 / CIDR / 写错的行）与匹配（含 IPv4-mapped IPv6），
/// 再真的起一次 Kestrel，从回环地址发请求，验证黑/白两种模式真的生效。
///
/// 为什么后一半必须真起服务：名单判定在 <see cref="HttpSecurity.RemoteAllowed"/> 里，
/// 而它的输入是 <c>HttpContext.Connection.RemoteIpAddress</c>——那是 Kestrel 填的，
/// 自己造一个假的 HttpContext 验不出"双栈监听时拿到的是 ::ffff:127.0.0.1"这类事实。
/// </summary>
public static class IpList
{
    public static void Run()
    {
        Harness.Section("IP 名单：解析");
        Parsing();

        Harness.Section("IP 名单：匹配");
        Matching();

        Harness.Section("IP 名单：配置语义");
        ConfigSemantics();

        Harness.Section("局域网模式放行哪些地址（Tailscale 段刻意不在其中）");
        LanRanges();

        Harness.Section("放行判定：范围给基线，名单做叠加");
        Policy();

        Harness.Section("IP 名单：真的起一次 Kestrel 从回环发请求");
        OverHttp();
    }

    /// <summary>
    /// lan 模式的放行表。**100.64.0.0/10（Tailscale / CGNAT）刻意不在其中**：
    /// 它同时是运营商级 NAT 用的段，塞进 lan 预设等于信任"同一 CGNAT 后面的其他用户"。
    /// 要用 Tailscale，就在白名单里写一条——显式、可撤。
    /// </summary>
    private static void LanRanges()
    {
        bool Ok(string ip) => HttpSecurity.LanAddressAllowed(IPAddress.Parse(ip));

        Harness.True("10.1.2.3 → 放行（RFC1918 10/8）", Ok("10.1.2.3"));
        Harness.True("172.16.0.1 → 放行（172.16/12 下界）", Ok("172.16.0.1"));
        Harness.True("172.31.255.254 → 放行（172.16/12 上界）", Ok("172.31.255.254"));
        Harness.True("172.32.0.1 → 拒绝（172.16/12 之外）", !Ok("172.32.0.1"));
        Harness.True("192.168.1.1 → 放行", Ok("192.168.1.1"));
        Harness.True("169.254.10.20 → 放行（链路本地）", Ok("169.254.10.20"));
        Harness.True("127.0.0.1 → 放行（回环）", Ok("127.0.0.1"));

        Harness.True("100.64.0.1 → 拒绝（Tailscale 段不在 lan 预设里）", !Ok("100.64.0.1"));
        Harness.True("100.101.102.103 → 拒绝（同上）", !Ok("100.101.102.103"));
        Harness.True("100.127.255.254 → 拒绝（同上）", !Ok("100.127.255.254"));
        Harness.True("100.63.255.255 → 拒绝", !Ok("100.63.255.255"));
        Harness.True("100.128.0.1 → 拒绝", !Ok("100.128.0.1"));
        Harness.True("100.64.0.1 的 IPv4-mapped 写法 → 同样拒绝（归一后一致）",
                     !Ok("::ffff:100.64.0.1"));

        Harness.True("fd00::1 → 放行（v6 ULA）", Ok("fd00::1"));
        Harness.True("fd7a:115c:a1e0::1 → 放行（Tailscale 的 v6 段落在 fc00::/7，这条没动）",
                     Ok("fd7a:115c:a1e0::1"));
        Harness.True("fe80::1 → 放行（v6 链路本地）", Ok("fe80::1"));
        Harness.True("2001:db8::1 → 拒绝（公网文档段）", !Ok("2001:db8::1"));

        Harness.True("8.8.8.8 → 拒绝（公网）", !Ok("8.8.8.8"));
    }

    /// <summary>
    /// 放行判定：范围给基线，名单做叠加。
    ///
    /// 这张表是这次改动的核心——同一份名单在不同范围下含义不同（额外屏蔽 / 额外放行 /
    /// 唯一依据），所以必须逐格钉住，否则"改了一个字，行为变了一个模式"没人发现。
    /// </summary>
    private static void Policy()
    {
        static ProxyConfig Cfg(string scope, string mode, params string[] list)
        {
            var c = new ProxyConfig { BindScope = scope, IpListMode = mode, IpList = list.ToList() };
            c.Normalize();
            return c;
        }

        bool Allow(ProxyConfig cfg, string ip) => HttpSecurity.AllowedByPolicy(cfg, IPAddress.Parse(ip));

        // ---------- 仅局域网 + 黑名单 = 额外屏蔽 ----------
        var lanDeny = Cfg("lan", "deny", "10.1.2.3");
        Harness.True("lan+黑名单：名单里的私有地址被额外屏蔽", !Allow(lanDeny, "10.1.2.3"));
        Harness.True("lan+黑名单：其他私有地址照常放行", Allow(lanDeny, "10.1.2.4"));
        Harness.True("lan+黑名单：公网仍被范围挡住", !Allow(lanDeny, "8.8.8.8"));

        // ---------- 仅局域网 + 白名单 = 额外放行 ----------
        var lanAllow = Cfg("lan", "allow", "100.64.0.0/10", "8.8.8.8");
        Harness.True("lan+白名单：名单里的 Tailscale 段被额外放行", Allow(lanAllow, "100.101.1.2"));
        Harness.True("lan+白名单：名单里的公网地址也被额外放行", Allow(lanAllow, "8.8.8.8"));
        Harness.True("lan+白名单：局域网本来就放行", Allow(lanAllow, "192.168.1.5"));
        Harness.True("lan+白名单：名单外的公网仍拒绝", !Allow(lanAllow, "1.1.1.1"));

        // ---------- 仅本机 ----------
        var localDeny = Cfg("local", "deny", "127.0.0.1");
        Harness.True("本机+黑名单：连回环都被屏蔽", !Allow(localDeny, "127.0.0.1"));
        var localAllow = Cfg("local", "allow", "10.9.9.9");
        Harness.True("本机+白名单：列表里的外部地址被额外放行", Allow(localAllow, "10.9.9.9"));
        Harness.True("本机+白名单：回环仍在", Allow(localAllow, "127.0.0.1"));
        Harness.True("本机+白名单：列表外的外部地址仍拒绝", !Allow(localAllow, "10.9.9.8"));

        // ---------- 任意来源 ----------
        var wanDeny = Cfg("wan", "deny", "8.8.8.8");
        Harness.True("任意来源+黑名单：名单里的被屏蔽", !Allow(wanDeny, "8.8.8.8"));
        Harness.True("任意来源+黑名单：其余照常", Allow(wanDeny, "1.1.1.1"));
        var wanAllow = Cfg("wan", "allow", "8.8.8.8");
        Harness.True("任意来源+白名单：本来就全放行，白名单不改变结果", Allow(wanAllow, "1.1.1.1"));

        // ---------- 按 IP 名单（自定义）：名单是唯一依据 ----------
        var listAllow = Cfg("list", "allow", "192.168.1.0/24");
        Harness.True("自定义+白名单：只放行名单里的", Allow(listAllow, "192.168.1.9"));
        Harness.True("自定义+白名单：回环也拒绝（没有暗门）", !Allow(listAllow, "127.0.0.1"));
        var listDeny = Cfg("list", "deny", "192.168.1.9");
        Harness.True("自定义+黑名单：名单外的全放行", Allow(listDeny, "8.8.8.8"));
        Harness.True("自定义+黑名单：名单里的拒绝", !Allow(listDeny, "192.168.1.9"));

        // ---------- 空白名单：不改动基线（除自定义+白名单） ----------
        var lanDenyEmpty = Cfg("lan", "deny");
        Harness.True("lan+黑名单+空名单 = 局域网原样", Allow(lanDenyEmpty, "10.1.2.3"));
        var lanAllowEmpty = Cfg("lan", "allow");
        Harness.True("lan+白名单+空名单 = 局域网原样（不是拒绝所有人）", Allow(lanAllowEmpty, "10.1.2.3"));
        var listAllowEmpty = Cfg("list", "allow");
        Harness.True("自定义+白名单+空名单 = 拒绝所有人", !Allow(listAllowEmpty, "10.1.2.3"));
        Harness.True("自定义+白名单+空名单：回环也拒绝", !Allow(listAllowEmpty, "127.0.0.1"));
    }

    private static void Parsing()
    {
        var list = IpAccessList.Parse(new[]
        {
            "192.168.1.0/24",
            "10.0.0.7",
            "::1",
            "fd00::/8",
            "# 这一行是注释",
            "",
            "  192.0.2.5  ",
            "10.0.0.0/8",          // 与 10.0.0.7 重叠，但语义上仍是两条独立条目
            "not-an-ip",           // 错
            "10.0.0.0/33",         // 错：v4 前缀最大 32
            "::1/129",             // 错：v6 前缀最大 128
            "1.2.3.4/abc"          // 错：前缀不是数字
        }, out var errors);

        Harness.Eq("有效条目 6 条（注释与空行跳过）", list.Count, 6);
        Harness.Eq("写错的 4 条被收进 errors", errors.Count, 4);
        Harness.True("errors 里保留原文（界面要能逐条指出来）", errors.Contains("not-an-ip"));
        Harness.True("缺省前缀按单地址算：10.0.0.7 → /32",
            IpAccessList.TryParseEntry("10.0.0.7", out var a, out var pa) && pa == 32);
        Harness.True("主机位会被清零：10.0.0.5/8 与 10.0.0.0/8 等价",
            IpAccessList.TryParseEntry("10.0.0.5/8", out var b1, out _) &&
            IpAccessList.TryParseEntry("10.0.0.0/8", out var b2, out _) &&
            b1.Equals(b2));
        Harness.True("v6 单地址 /128",
            IpAccessList.TryParseEntry("::1", out _, out var p6) && p6 == 128);
        Harness.True("空串不是合法条目", !IpAccessList.TryParseEntry("", out _, out _));
    }

    private static void Matching()
    {
        var list = IpAccessList.Parse(new[]
        {
            "192.168.1.0/24", "10.0.0.7", "fd00::/8", "2001:db8::/32"
        }, out _);

        Harness.True("192.168.1.5 命中 /24", list.Contains(IPAddress.Parse("192.168.1.5")));
        Harness.True("192.168.2.5 不在 /24 里", !list.Contains(IPAddress.Parse("192.168.2.5")));
        Harness.True("10.0.0.7 精确命中", list.Contains(IPAddress.Parse("10.0.0.7")));
        Harness.True("10.0.0.8 不算命中", !list.Contains(IPAddress.Parse("10.0.0.8")));
        Harness.True("fd00::1 命中 fd00::/8", list.Contains(IPAddress.Parse("fd00::1")));
        Harness.True("2001:db8:1::5 命中 /32", list.Contains(IPAddress.Parse("2001:db8:1::5")));
        Harness.True("2001:db9::1 不在 /32 里", !list.Contains(IPAddress.Parse("2001:db9::1")));

        // 双栈监听时 Kestrel 常把回环报成 ::ffff:127.0.0.1——不归一的话
        // 名单里写的 127.0.0.1 永远匹配不上。
        var loop = IpAccessList.Parse(new[] { "127.0.0.1" }, out _);
        Harness.True("IPv4-mapped IPv6 也能命中 127.0.0.1",
            loop.Contains(IPAddress.Parse("::ffff:127.0.0.1")));

        var v6 = IpAccessList.Parse(new[] { "::1" }, out _);
        Harness.True("::1 命中 ::1", v6.Contains(IPAddress.Parse("::1")));
        Harness.True("127.0.0.1 不命中 ::1（不同地址族，不该互相折）",
            !v6.Contains(IPAddress.Parse("127.0.0.1")));

        var empty = IpAccessList.Parse(Array.Empty<string>(), out _);
        Harness.True("空名单谁都不命中", !empty.Contains(IPAddress.Loopback));
        Harness.True("null 地址不命中（也不抛）", !empty.Contains(null));
    }

    private static void ConfigSemantics()
    {
        var cfg = new ProxyConfig { BindScope = "list", IpListMode = "deny", IpList = { "1.2.3.4" } };
        cfg.Normalize();
        Harness.Eq("list 是合法的范围值（不会被收敛成 local）", cfg.BindScope, "list");
        Harness.True("list 模式下 Kestrel 必须绑所有网卡（绑回环就没人能连）",
            cfg.ListenAddress.Equals(IPAddress.Any));

        cfg.IpListMode = "ALLOW";
        cfg.Normalize();
        Harness.Eq("模式大小写不敏感", cfg.IpListMode, "allow");

        cfg.IpListMode = "whatever";
        cfg.Normalize();
        Harness.Eq("认不出的模式一律当黑名单（更严的那边）", cfg.IpListMode, "deny");

        cfg.IpList = new List<string> { "192.168.1.0/24", "oops" };
        Harness.Eq("状态行说清条数", cfg.IpListStatus().StartsWith("黑名单共 1 条", StringComparison.Ordinal), true);
        Harness.True("状态行点名写错的那条", cfg.IpListStatus().Contains("oops", StringComparison.Ordinal));

        var allowEmpty = new ProxyConfig { BindScope = "list", IpListMode = "allow" };
        allowEmpty.Normalize();
        Harness.True("白名单空着 = 拒绝所有人（必须说出来）",
            allowEmpty.IpListStatus().Contains("所有连接都会被拒绝", StringComparison.Ordinal));

        var denyEmpty = new ProxyConfig { BindScope = "list", IpListMode = "deny" };
        denyEmpty.Normalize();
        Harness.True("黑名单空着 = 不拦任何人（两种空名单含义相反）",
            denyEmpty.IpListStatus().Contains("不拦任何地址", StringComparison.Ordinal));

        // MCP 页上没有"按名单"，手改配置写成 list 时要收回 local，
        // 否则界面显示"仅本机"、行为却按名单。
        var mcp = new ProxyConfig { BindScope = "list", McpBindScope = "list", McpPort = 49000 };
        mcp.Normalize();
        Harness.Eq("MCP 的 list 被收回 local", mcp.McpBindScope, "local");
        Harness.Eq("转发 API 的 list 保留", mcp.BindScope, "list");
    }

    // ---------------- 真起一次 Kestrel ----------------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int DeadPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void OverHttp()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "aip-iplist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        var oldTokenPath = TokenStore.FilePathOverride;
        var oldCfgPath = ProxyConfig.FilePathOverride;
        TokenStore.FilePathOverride = Path.Combine(tmpDir, "proxy.tokens.json");
        ProxyConfig.FilePathOverride = Path.Combine(tmpDir, "proxy.config.json");

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            BindScope = "list",
            IpListMode = "allow",
            IpList = new List<string> { "127.0.0.1" },
            RequireToken = true,
            ToolsEnabled = true,
            McpEnabled = false,
            TimeoutSeconds = 5,
            VocabDbPath = Path.Combine(tmpDir, "no-such-vocab.sqlite"),
            SeedPath = Path.Combine(tmpDir, "no-such-seed.json")
        };
        cfg.Normalize();

        var tokens = new TokenStore();
        tokens.Tokens.Add(TokenStore.Create("回归用", "iplist", out var good));

        // 上游指向死端口：我们只关心"门口放没放行"，502（连不上上游）就代表放行了。
        //
        // 顺序要紧：Normalize() 在"一个站点都没有"时会自动塞一个**默认站点**，而那个
        // 地址是真实的默认上游。先 Normalize 再加死上游，请求就会被默认站点发给真服务
        // ——第一次跑就撞上了：名单放行的那几条实际收到了一个真实的 401。
        cfg.Providers.Clear();
        var dead = Fixture.AddProvider(cfg, $"http://127.0.0.1:{DeadPort()}/v1", name: "死上游");
        dead.AutoFetchModels = false;
        cfg.DefaultProviderId = dead.Id;

        var server = new ProxyServer(cfg, tokens, new VocabService());
        var baseUrl = $"http://127.0.0.1:{cfg.Port}";
        var v6Base = $"http://[::1]:{cfg.Port}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            // 白名单里有 127.0.0.1 → 放行（死上游给出 502，说明真的走到转发了）
            // 放行与否只看"是不是被门口拦下（403）"：上游是死端口，502 与"真的转发成功"
            // 对这条测试是同一件事——我们要验的是门禁，不是上游。
            Harness.Eq("白名单含 127.0.0.1 → 放行（死上游给出 502，说明真的转发了）", Post(http, baseUrl, good), 502);

            // 白名单换成别的网段 → 回环被拒
            cfg.IpList = new List<string> { "192.0.2.0/24" };
            Harness.Eq("白名单不含回环 → 403", Post(http, baseUrl, good), 403);

            // 白名单空着 → 拒绝所有人
            cfg.IpList = new List<string>();
            Harness.Eq("白名单空着 → 403（不是「没配置就放行」）", Post(http, baseUrl, good), 403);

            // 黑名单：命中就拒
            cfg.IpListMode = "deny";
            cfg.IpList = new List<string> { "127.0.0.1" };
            Harness.Eq("黑名单含回环 → 403", Post(http, baseUrl, good), 403);

            // 黑名单：没命中就放
            cfg.IpList = new List<string> { "10.0.0.0/8", "fd00::/8" };
            Harness.Eq("黑名单不含回环 → 放行", Post(http, baseUrl, good), 502);

            // v6 走另一条路：连 [::1]。list 模式下服务端是双栈监听，所以 v6 客户端
            // 真的能连进来；名单里写 ::1/128 就该把它拦下。
            cfg.IpList = new List<string> { "::1/128" };
            Harness.Eq("黑名单写 ::1/128 → 经 [::1] 连接被拦", Post(http, v6Base, good), 403);

            cfg.IpList = new List<string> { "10.0.0.0/8" };
            Harness.Eq("黑名单不含 ::1 → 经 [::1] 放行", Post(http, v6Base, good), 502);

            // 写错的行被忽略（而不是把整个服务拦死）
            cfg.IpList = new List<string> { "not-an-ip", "10.0.0.0/8" };
            Harness.Eq("名单里有写错的行时，其余条目照常生效", Post(http, baseUrl, good), 502);

            // ---------- 非自定义模式下的叠加：范围仍给基线，名单只做增减 ----------
            cfg.BindScope = "lan";          // 绑定在启动时已定，改它只影响过滤
            cfg.IpListMode = "deny";
            cfg.IpList = new List<string> { "127.0.0.1" };
            Harness.Eq("局域网 + 黑名单含回环 → 403（额外屏蔽）", Post(http, baseUrl, good), 403);

            cfg.IpList = new List<string> { "10.0.0.0/8" };
            Harness.Eq("局域网 + 黑名单不含回环 → 放行（基线还在）", Post(http, baseUrl, good), 502);

            cfg.IpListMode = "allow";
            cfg.IpList = new List<string> { "100.64.0.0/10" };   // 只写了 Tailscale 段
            Harness.Eq("局域网 + 白名单（只有 Tailscale 段）→ 回环仍放行（额外放行，不是只看名单）",
                       Post(http, baseUrl, good), 502);
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            TokenStore.FilePathOverride = oldTokenPath;
            ProxyConfig.FilePathOverride = oldCfgPath;
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 删不掉不影响结论 */ }
        }
    }

    private static int Post(HttpClient http, string baseUrl, string token)
    {        var body = new JsonObject
        {
            ["model"] = "client-model",
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "你好" } }
        }.ToJsonString();

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Harness.Info($"  HTTP {(int)resp.StatusCode}: {Shorten(text)}");
        return (int)resp.StatusCode;
    }

    private static string Shorten(string text)
        => text.Length <= 160 ? text : text[..160] + "…";
}
