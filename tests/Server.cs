using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 代理 HTTP 端点的回归：**真的起一次 Kestrel、真的发 HTTP 请求**。
///
/// 为什么不能只测 <see cref="ToolLoop"/>：那测不到路由、鉴权、状态码、
/// content-type、流式是不是真的没被缓冲。而这些恰恰是"接进 agent 平台之后
/// 才发现不对"的地方——症状是平台报"接口不通"，而工具循环本身完全正常。
///
/// **刻意不碰用户真实数据**：
///   * 令牌库指到临时目录（否则跑一次回归就把用户发给各平台的真令牌作废）；
///   * 上游地址指到一个死端口（我们测的就是"连不上时回什么"，不需要真上游）。
///
/// 流式与工具注入的端到端验证需要一个真上游，不在这组里（见 README）。
/// </summary>
public static class Server
{
    /// <summary>拿一个当前空闲的端口。有极小的竞态窗口，但回归是单机单进程，够用。</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>也拿一个空闲端口，但**立刻关掉**，用来当"死上游"。</summary>
    private static int DeadPort() => FreePort();

    public static void Run()
    {
        Harness.Section("代理 HTTP 端点（真的起一次 Kestrel）");

        var tmpDir = Path.Combine(Path.GetTempPath(), "aip-servercheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);

        var oldTokenPath = TokenStore.FilePathOverride;
        TokenStore.FilePathOverride = Path.Combine(tmpDir, "proxy.tokens.json");

        var cfg = new ProxyConfig
        {
            Port = FreePort(),
            BindScope = "local",
            // 死端口：连接必然失败。我们断言的就是"上游不可达时客户端收到什么"。
            UpstreamBaseUrl = $"http://127.0.0.1:{DeadPort()}/v1",
            RequireToken = true,
            ToolsEnabled = true,
            TimeoutSeconds = 5,
            // 指向不存在的词库：工具执行会返回"词库不可用"，
            // 这正好也验证了"词库没配好不会让整个请求 500"。
            VocabDbPath = Path.Combine(tmpDir, "no-such-vocab.sqlite"),
            SeedPath = Path.Combine(tmpDir, "no-such-seed.json")
        };

        var tokens = new TokenStore();
        var good = TokenStore.Create("回归用", "servercheck", out var goodPlain);
        tokens.Tokens.Add(good);

        var server = new ProxyServer(cfg, tokens, new VocabService());
        var baseUrl = $"http://127.0.0.1:{cfg.Port}";

        try
        {
            server.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Harness.True("服务起来了", server.IsRunning);
        }
        catch (Exception ex)
        {
            Harness.True("服务起来了（起不来后面的断言全部无意义）", false);
            Harness.Info("启动失败：" + ex.Message);
            TokenStore.FilePathOverride = oldTokenPath;
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        try
        {
            // ---------- /health：刻意不鉴权 ----------
            {
                var resp = http.GetAsync(baseUrl + "/health").GetAwaiter().GetResult();
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var json = ChatWire.TryParseObject(text);

                Harness.Eq("GET /health 不带令牌也能通（监控系统没有令牌）",
                           (int)resp.StatusCode, 200);
                Harness.Eq("health 报 ok", ChatWire.GetString(json, "status"), "ok");
                Harness.True("health 里不含上游地址（它是给局域网上任何人看的）",
                             !text.Contains("127.0.0.1", StringComparison.Ordinal) &&
                             !text.Contains(cfg.UpstreamBaseUrl, StringComparison.Ordinal));
            }

            // ---------- 根路径：给一句人话 ----------
            {
                var resp = http.GetAsync(baseUrl + "/").GetAwaiter().GetResult();
                var json = ChatWire.TryParseObject(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Harness.Eq("GET / 返回 200", (int)resp.StatusCode, 200);
                Harness.True("根路径告诉用户 Base URL 该填什么",
                             ChatWire.GetString(json, "hint").Contains("/v1", StringComparison.Ordinal));
            }

            // ---------- 鉴权：三种拒绝 ----------
            {
                var body = Json();

                var noToken = Post(http, baseUrl + "/v1/chat/completions", body, null);
                Harness.Eq("没带令牌 → 401", (int)noToken.StatusCode, 401);
                Harness.True("401 体里有 error 对象（OpenAI 形状）",
                             ChatWire.TryParseObject(noToken.Text)?["error"] is JsonObject);

                var badToken = Post(http, baseUrl + "/v1/chat/completions", body, "aip-wrong-token");
                Harness.Eq("错令牌 → 401", (int)badToken.StatusCode, 401);

                var disabled = TokenStore.Create("已停用", "", out var disabledPlain);
                disabled.Enabled = false;
                tokens.Tokens.Add(disabled);
                var off = Post(http, baseUrl + "/v1/chat/completions", body, disabledPlain);
                Harness.Eq("已停用的令牌 → 401", (int)off.StatusCode, 401);

                // /v1/models 也要鉴权，不能因为是 GET 就放行
                var models = http.GetAsync(baseUrl + "/v1/models").GetAwaiter().GetResult();
                Harness.Eq("GET /v1/models 无令牌 → 401", (int)models.StatusCode, 401);
            }

            // ---------- 上游不可达：状态码必须如实 ----------
            {
                var r = Post(http, baseUrl + "/v1/chat/completions", Json(), goodPlain);

                // 这一条是本组里最重要的：上游连不上时如果回 200 + error 体，
                // 接入方会以为成功，然后去读 choices，读不到就报一个和真正原因
                // 毫无关系的错（"模型没有返回内容"）。
                Harness.True("上游连不上时不回 200（否则平台会去读一个不存在的 choices）",
                             (int)r.StatusCode >= 400);
                Harness.Eq("上游连不上 → 502", (int)r.StatusCode, 502);

                var err = ChatWire.TryParseObject(r.Text)?["error"] as JsonObject;
                Harness.True("错误体里有 error.message", err is not null);
                Harness.True("错误信息说清了是连不上上游，而不是含糊的「请求失败」",
                             ChatWire.GetString(err, "message").Contains("连不上", StringComparison.Ordinal));
                Harness.Info("上游不可达时的响应：" + r.Text);
            }

            // ---------- 请求体不是合法 JSON ----------
            {
                var r = Post(http, baseUrl + "/v1/chat/completions", "{ 这不是 json", goodPlain);
                Harness.Eq("坏 JSON → 400", (int)r.StatusCode, 400);
            }

            // ---------- /v1/models：上游挂了也要把映射表报出去 ----------
            {
                cfg.ModelMap["gpt-4o"] = "deepseek-chat";

                // 注意要带令牌：/v1/models 也要鉴权，不能因为是 GET 就放行
                // （这一条上面单独断言过，这里顺带再确认一次没漏）。
                var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + goodPlain);
                using var r = http.SendAsync(req).GetAwaiter().GetResult();
                var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var json = ChatWire.TryParseObject(text);

                Harness.Eq("上游 /models 挂了，但回退到配置里的映射表 → 200", (int)r.StatusCode, 200);
                var ids = (json?["data"] as JsonArray)?
                    .Select(n => ChatWire.GetString(n as JsonObject, "id")).ToList() ?? new List<string>();
                Harness.True("映射表里的名字出现在模型列表里（平台启动时会拿它校验配置）",
                             ids.Contains("gpt-4o"));
                cfg.ModelMap.Clear();
            }

            // ---------- 工具注入关掉时退化成纯反向代理 ----------
            {
                cfg.ToolsEnabled = false;
                var r = Post(http, baseUrl + "/v1/chat/completions", Json(), goodPlain);
                Harness.True("关掉工具注入后仍然只是转发，状态码照样如实",
                             (int)r.StatusCode >= 400);
                cfg.ToolsEnabled = true;
            }
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            Harness.True("服务停了", !server.IsRunning);

            // 端口应当真的释放了——没释放的话用户改了端口重启会撞"地址已占用"。
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, cfg.Port);
                probe.Start();
                probe.Stop();
                Harness.True("停止后端口已释放", true);
            }
            catch (SocketException)
            {
                Harness.True("停止后端口已释放", false);
            }

            TokenStore.FilePathOverride = oldTokenPath;
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 临时目录删不掉不影响结论 */ }
        }
    }

    private static string Json() => new JsonObject
    {
        ["model"] = "client-model",
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "你好" }
        }
    }.ToJsonString();

    private sealed record HttpResult(int StatusCode, string Text);

    private static HttpResult Post(HttpClient http, string url, string body, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (token is not null)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = http.SendAsync(req).GetAwaiter().GetResult();
        return new HttpResult((int)resp.StatusCode, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }
}
