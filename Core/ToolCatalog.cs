using System.Text.Json.Nodes;

namespace AnimaAIProxy.Core;

/// <summary>
/// 一份下发给上游的工具声明。形状与 OpenAI 的 <c>tools[]</c> 一一对应，
/// 序列化时再包一层 <c>{type:"function", function:{...}}</c>。
///
/// 刻意不复用 AnimaCaptioner 的 <c>AgentTool</c>：
/// 那个文件（Core/AgentKit.cs）依赖 <c>TagSearchAgent</c> → <c>LlmClient</c>，
/// 会把一整条 HTTP 客户端与配置链拖进来。我们只需要一个三元组。
/// </summary>
public sealed record ToolDecl(string Name, string Description, JsonObject Parameters)
{
    /// <summary>包成 OpenAI 的 tools 数组元素。</summary>
    public JsonObject ToOpenAiTool()
    {
        var fn = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["parameters"] = Parameters.DeepClone()
        };
        return new JsonObject { ["type"] = "function", ["function"] = fn };
    }
}

/// <summary>
/// 代理对外提供的工具集合。
///
/// 三条设计约束，每一条都对应一个具体的坑：
///
/// 1. **名字带 <c>danbooru_</c> 前缀。** 平台自己的工具叫 <c>search</c>、
///    <c>lookup</c> 的可能性很高，重名会让上游直接报 400，或者更糟——模型
///    调到了平台的工具而我们按自己的语义去执行。前缀把这个风险降到接近零。
/// 2. **仍然要处理重名。** 前缀只是降低概率，不是保证。真撞上时我们把
///    **自己**的名字加后缀让路，绝不覆盖客户端的声明：平台那边的工具是它的
///    功能，我们的是附加项，谁是主谁是次不能搞反。
/// 3. **工具说明必须自解释。** 平台的系统提示里只会介绍它自己的工具，模型
///    看不到我们的文档。所以每段说明都要写清"什么时候该调、参数怎么给"，
///    并且**明确禁止凭记忆猜标签名**——Danbooru 标签是精确字符串，
///    <c>blush</c> 和 <c>blushing</c> 是两个不同的东西（后者根本不存在）。
/// </summary>
public static class ToolCatalog
{
    public const string SearchName = "danbooru_search_tags";
    public const string ResolveName = "danbooru_resolve_tag";
    public const string ScanName = "danbooru_scan_text";

    /// <summary>重名时的后缀。</summary>
    public const string CollisionSuffix = "_aip";

    /// <summary>
    /// 查词库。说明沿用 AnimaCaptioner 里那条实测过的措辞（短查询、不许凭记忆、
    /// 整串查不到就拆词），因为踩过的坑是同一个。
    /// </summary>
    public static ToolDecl SearchTool => new(
        SearchName,
        "Search a local Danbooru/Anima tag database (330k tags, with Chinese " +
        "translations, categories and usage counts). Tags are EXACT strings: `blush` " +
        "and `blushing` are different things and the second one does not exist, so never " +
        "name a tag from memory - call this and copy the name out of the result. Use SHORT " +
        "queries of one or two words, one concept at a time (\"white dress\", \"lace\", " +
        "\"shy\"); the query is matched against tag names AND their Chinese meanings, so " +
        "English or Chinese both work. Never pass a whole sentence: split \"white lace " +
        "dress\" into \"white dress\" and \"lace\" and search both. If a long query returns " +
        "nothing, split it and search the parts.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One or two words, one concept. English or Chinese. " +
                                      "Not a sentence."
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "How many tags to return, 1-25. Default 25."
                }
            },
            ["required"] = new JsonArray { "query" }
        });

    /// <summary>
    /// 核对单个写法。搜索是"找候选"，这个是"判定某一个写法到底成不成立"——
    /// 模型经常需要问"我手上这个字符串是合法标签吗、它的规范形式是什么"。
    /// </summary>
    public static ToolDecl ResolveTool => new(
        ResolveName,
        "Check ONE exact spelling against the tag database: is it a real tag, what is its " +
        "canonical form, is it deprecated/banned, how many posts use it, and what does it " +
        "mean in Chinese. Input may be an English tag (`white_dress` and `white dress` are " +
        "both accepted) or a Chinese meaning. Use this to verify a tag you already have " +
        "rather than to explore - use " + SearchName + " for exploring.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tag"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One tag name or one Chinese meaning, e.g. " +
                                      "\"white dress\" or \"白色连衣裙\"."
                }
            },
            ["required"] = new JsonArray { "tag" }
        });

    /// <summary>
    /// 中文描述 → 标签。走的是词库里的最优切分（动态规划），不调模型。
    /// 给"把一段中文描述变成标签列表"这类需求用。
    /// </summary>
    public static ToolDecl ScanTool => new(
        ScanName,
        "Convert a Chinese description into the tags the database recognizes, without " +
        "calling a model. Useful for turning an image description into a tag list. It only " +
        "recognizes concepts that exist in the database; anything it cannot match is simply " +
        "omitted from the result, so an empty result means \"none of these words are tags\", " +
        "not \"the input was bad\".",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A Chinese description, e.g. \"一个女孩在笑，白色连衣裙\"."
                }
            },
            ["required"] = new JsonArray { "text" }
        });

    /// <summary>本次要注入的全部工具，顺序固定（便于测试与日志比对）。</summary>
    public static List<ToolDecl> All() => new() { SearchTool, ResolveTool, ScanTool };

    /// <summary>
    /// 一个工具名是不是代理自有的，**不管它这次实际下发的名字是什么**。
    ///
    /// 只按名字判、不查本次的 <see cref="InjectionPlan"/>，这是刻意的：
    /// 收尾那一轮我们已经把 plan 换成空集了，而这一轮响应的身份必须按它
    /// **当时下发的名字**来认——拿空 plan 去问"这是我们的吗"会把我们自己的
    /// 调用当成客户端的，然后原样交还给客户端，客户端收到一个它根本没有的
    /// <c>danbooru_*</c> 调用。
    ///
    /// 让路后缀（<c>_aip</c>、<c>_aip2</c>…）必须一起认，否则重名时我们
    /// 自己下发、自己执行的调用会被判成外来者。
    /// </summary>
    public static bool IsProxyToolName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var baseName in new[] { SearchName, ResolveName, ScanName })
        {
            if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(baseName + CollisionSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 为一次具体请求算出"叫什么名字"。<paramref name="taken"/> 是客户端已经用掉的
    /// 工具名（大小写不敏感）。
    ///
    /// 返回值同时给出 实际名字 → 语义 的映射，因为后续识别"这个 tool_call 是不是
    /// 我们的"必须按**实际下发的名字**来判，而不是按常量。重名让路之后两者不再相等，
    /// 直接比常量会把让路后的调用当成"未知工具"回一句错误，然后模型再试、再失败。
    /// </summary>
    public static InjectionPlan Plan(IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var decls = new List<ToolDecl>();
        var kinds = new Dictionary<string, ToolKind>(StringComparer.Ordinal);

        foreach (var (decl, kind) in new[]
                 {
                     (SearchTool, ToolKind.Search),
                     (ResolveTool, ToolKind.Resolve),
                     (ScanTool, ToolKind.Scan)
                 })
        {
            var name = decl.Name;
            if (used.Contains(name)) name += CollisionSuffix;
            // 加过后缀还是撞（客户端真有个叫 danbooru_search_tags_aip 的工具）：
            // 再让一步，加序号。宁可名字难看，也不能覆盖客户端的声明。
            var n = 2;
            while (used.Contains(name)) name = decl.Name + CollisionSuffix + n++;

            used.Add(name);
            kinds[name] = kind;
            decls.Add(decl with { Name = name });
            if (!string.Equals(name, decl.Name, StringComparison.Ordinal))
                ProxyLog.Write($"工具重名：{decl.Name} 已被接入方占用，本次改用 {name}");
        }

        return new InjectionPlan(decls, kinds);
    }
}

/// <summary>三类工具，用来在执行时派发。</summary>
public enum ToolKind { Search, Resolve, Scan }

/// <summary>一次请求实际下发的工具集，以及名字 → 语义的映射。</summary>
public sealed record InjectionPlan(
    IReadOnlyList<ToolDecl> Tools,
    IReadOnlyDictionary<string, ToolKind> Kinds)
{
    /// <summary>这个工具名是不是我们的。大小写不敏感，与 Plan 里的去重口径一致。</summary>
    public bool IsOurs(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var k in Kinds.Keys)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public ToolKind? KindOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var kv in Kinds)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public IReadOnlyList<JsonObject> ToOpenAiTools()
        => Tools.Select(t => t.ToOpenAiTool()).ToList();
}
