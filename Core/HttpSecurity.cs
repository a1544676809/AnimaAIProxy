using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace AnimaAIProxy.Core;

/// <summary>
/// 两个对外服务（转发 API 与 MCP）**共用**的三道门：来源过滤、Origin 校验、令牌鉴权。
///
/// 为什么抽出来而不是各写一份：这是**安全边界**的代码。复制一份之后，哪天补了个
/// 漏洞（比如 lan 少放行一个网段、或者 Origin 比较忘了端口），只会修在一处，
/// 另一个服务继续敞着——而且没有任何症状，直到出事。共用一份，改一次两边一起生效。
/// </summary>
internal static class HttpSecurity
{
    /// <summary>
    /// 来源过滤。只有 lan/wan 才需要它（local 时 Kestrel 只绑回环，
    /// 外面根本连不上）。
    ///
    /// lan 只放行私有网段：`BindScope=lan` 的语义是"给同一个局域网里的设备用"，
    /// 而不是"给任何能路由到本机的人用"。把公网地址也放进来，用户以为只是开了内网
    /// 共享，实际把上游额度暴露到了互联网上——而这个错误不会有任何症状，直到账单出来。
    /// </summary>
    public static bool RemoteAllowed(string bindScope, HttpContext ctx)
    {
        if (bindScope != "lan") return true;

        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;  // 172.16/12
            if (b[0] == 192 && b[1] == 168) return true;               // 192.168/16
            if (b[0] == 169 && b[1] == 254) return true;               // 169.254/16 链路本地
            return false;
        }

        // IPv6：fc00::/7 唯一本地、fe80::/10 链路本地
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 16)
        {
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }
        return false;
    }

    /// <summary>
    /// Origin 校验。**只在请求带了 Origin 时才校验**：本地客户端（MCP 客户端、
    /// agent 平台）普遍不带这个头；浏览器发跨源 POST 则一定带。不带就放行、
    /// 带了就必须是本机自己——既不会误伤客户端，又挡住了网页（DNS rebinding /
    /// CSRF：它们能向 127.0.0.1 发请求，但改不了 Origin）。
    /// </summary>
    public static bool OriginAllowed(int port, HttpContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"].ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;

        var o = origin.TrimEnd('/').ToLowerInvariant();
        return o == $"http://127.0.0.1:{port}" ||
               o == $"http://localhost:{port}" ||
               o == $"http://[::1]:{port}";
    }

    /// <summary>
    /// 令牌鉴权。通过返回 true 并给出令牌明文（只用于日志）；拒绝时
    /// <paramref name="reject"/> 里是给人看的原因。
    /// </summary>
    public static bool Authorize(bool requireToken, TokenStore tokens, HttpContext ctx,
                                 out string token, out string reject)
    {
        token = "";
        reject = "";

        ctx.Request.Headers.TryGetValue("Authorization", out var auth);
        ctx.Request.Headers.TryGetValue("x-api-key", out var xKey);
        var presented = TokenStore.ExtractToken(auth.ToString(), xKey.ToString());

        if (!requireToken)
        {
            token = presented ?? "";
            return true;
        }

        // "根本没配令牌"和"带了错令牌"要分开说。合成一句的话，用户第一次装好、
        // 客户端报"令牌无效"，会去反复检查客户端配置——而真正该做的是先签一枚令牌。
        if (!tokens.HasEnabled)
        {
            reject = "尚未签发任何令牌，已拒绝所有请求。请先在界面签发一枚令牌，" +
                     "再填进客户端的 API Key。";
            return false;
        }

        if (tokens.Authenticate(presented) is null)
        {
            reject = "令牌无效或已停用。";
            return false;
        }

        token = presented!;
        return true;
    }

    /// <summary>写一段 JSON 响应。两个服务的错误体都走这里，状态码由调用方给。</summary>
    public static Task WriteJsonAsync(HttpContext ctx, int status, JsonNode body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(ChatWire.ToJson(body), ctx.RequestAborted);
    }
}
