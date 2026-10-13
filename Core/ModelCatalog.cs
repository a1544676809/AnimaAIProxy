using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>一次请求该发给哪个站点、用哪个模型名。</summary>
/// <param name="Provider">目标站点；null = 一个可用的站点都没有（配置被清空了）。</param>
/// <param name="UpstreamModel">真正发给上游的模型名。</param>
/// <param name="ExposedModel">客户端报的模型名（响应里回写的就是它）。</param>
/// <param name="Note">一句话说明这次是怎么定的。</param>
/// <param name="Notable">
/// 这次判定值不值得进日志。映射与"按模型表命中"值得（用户配了它就想看到生效），
/// 而"不在模型表里、照原样发给默认站点"是绝大多数请求的常态，每次都记一行
/// 只会把日志淹掉。
/// </param>
public sealed record Routing(
    ProviderProfile? Provider,
    string UpstreamModel,
    string ExposedModel,
    string Note,
    bool Notable);

/// <summary>
/// 模型表：把"哪些站点开着、每个站点有哪些模型开着"变成两个问题的答案——
/// **对外公布哪些模型**，以及**某个模型名该发给谁**。
///
/// 刻意做成无状态的静态方法而不是缓存对象：配置是运行期可改的（界面上勾一个
/// 开关就变），缓存就得考虑失效，而失效漏掉一处的症状是"改了没生效"或
/// "删了的站点还在接请求"——两种都很难查。这里的计算量只是几十个字符串比较。
/// </summary>
public static class ModelCatalog
{
    /// <summary>参与聚合与路由的站点（界面里勾选的那些）。</summary>
    public static IEnumerable<ProviderProfile> EnabledProviders(ProxyConfig cfg) =>
        (cfg.Providers ?? new List<ProviderProfile>()).Where(p => p is { Enabled: true });

    /// <summary>所有"已启用站点"里的"已启用模型"，按站点顺序。</summary>
    public static IEnumerable<(ProviderProfile Provider, ModelEntry Model)> EnabledModels(ProxyConfig cfg)
    {
        foreach (var p in EnabledProviders(cfg))
            foreach (var m in p.Models ?? new List<ModelEntry>())
                if (m is { Enabled: true } && !string.IsNullOrWhiteSpace(m.Id))
                    yield return (p, m);
    }

    /// <summary>
    /// 决定一次请求发往哪里。判定顺序：
    ///
    /// 1. **模型名映射**（<see cref="ProxyConfig.ModelMap"/>）优先——那是用户
    ///    显式写下的"客户端报 A 就用 B"，比任何自动判定都更该被尊重。
    /// 2. 在**已启用站点的已启用模型**里按名字找：找到就用那个站点。
    /// 3. 都没找到就交给**默认站点**，模型名原样透传。
    ///
    /// 第 3 条是刻意保留的：agent 平台常常把模型名写死在配置里，而我们未必
    /// 拉过模型表（新装的、上游没有 /models 的）。这时"照发"比"报错说没有这个
    /// 模型"更有用——上游自己会回一个准确的错误，比我们猜要准。
    /// </summary>
    public static Routing Resolve(ProxyConfig cfg, string? requested)
    {
        var exposed = (requested ?? "").Trim();
        var target = exposed;
        var note = "";

        if (exposed.Length > 0 &&
            cfg.ModelMap is not null &&
            cfg.ModelMap.TryGetValue(exposed, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            target = mapped.Trim();
            note = $"模型名映射：{exposed} → {target}";
        }
        else if (exposed.Length > 0 && cfg.FallbackModelEnabled &&
                 !string.IsNullOrWhiteSpace(cfg.FallbackModel))
        {
            target = cfg.FallbackModel.Trim();
            note = $"落到兜底模型：{exposed} → {target}";
        }

        if (target.Length > 0)
        {
            foreach (var (p, m) in EnabledModels(cfg))
            {
                if (!string.Equals(m.Id, target, StringComparison.OrdinalIgnoreCase)) continue;
                var hit = note.Length > 0 ? note + "；按模型表路由到 " + p.DisplayName
                                          : $"按模型表路由到 {p.DisplayName}";
                return new Routing(p, m.Id, exposed, hit, true);
            }
        }

        var def = cfg.DefaultProvider;
        if (def is null) return new Routing(null, target, exposed, "没有可用的模型提供商", true);

        var tail = note.Length > 0 ? note + "；" : "";
        return new Routing(def, target, exposed,
            tail + $"{exposed} 不在模型表里，走默认站点 {def.DisplayName}",
            note.Length > 0);
    }

    /// <summary>
    /// 对外公布的模型表（OpenAI 的 <c>/v1/models</c> 形状）。
    ///
    /// 只有**已启用站点的已启用模型**会出现在这里，再加上映射表的键
    /// （那些名字我们也确实接受）。平台启动时拿它校验"我配的模型名存在吗"，
    /// 所以少报一个会让平台判定配置非法，多报一个会让它选到一个用不了的模型——
    /// 两边都得准。
    /// </summary>
    public static JsonArray ToOpenAiModels(ProxyConfig cfg)
    {
        var arr = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (p, m) in EnabledModels(cfg))
        {
            if (!seen.Add(m.Id)) continue;
            arr.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = p.DisplayName
            });
        }

        foreach (var key in (cfg.ModelMap ?? new Dictionary<string, string>()).Keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            arr.Add(new JsonObject
            {
                ["id"] = key,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "animaiproxy"
            });
        }

        return arr;
    }

    /// <summary>
    /// 解析一个 /models 响应里的模型名。
    ///
    /// 认四种形状（与 AnimaCaptioner 的 <c>LlmClient.TryParseModels</c> 同一套判据）：
    /// 标准 <c>{data:[...]}</c> 优先、<c>{models:[...]}</c>、裸字符串数组、
    /// 裸对象数组。**只吞 JSON 解析失败**，认不出的形状返回空表而不是抛——
    /// 拉不到模型表不该让界面报一个看不懂的异常。
    /// </summary>
    public static List<string> ParseModelIds(string? raw)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return ids;

        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch { return ids; }

        JsonArray? array = node switch
        {
            JsonArray bare => bare,
            JsonObject o when o["data"] is JsonArray d => d,
            JsonObject o when o["models"] is JsonArray m => m,
            _ => null
        };
        if (array is null) return ids;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            var id = IdOf(item);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>一条模型条目里的名字。裸字符串数组与对象数组都认。</summary>
    private static string? IdOf(JsonNode? item)
    {
        if (item is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (item is JsonObject o) return FirstString(o, "id", "name", "model");
        return null;
    }

    private static string? FirstString(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }
}
