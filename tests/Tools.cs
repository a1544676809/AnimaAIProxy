using System.Text.Json.Nodes;
using AnimaAIProxy.Core;

namespace AnimaAIProxy.Tests;

/// <summary>
/// 工具声明与参数读取的回归（纯逻辑）。
///
/// 参数读取那一节对着**模型真实会写出来的畸形参数**断言：小模型很爱把对象写成
/// 裸字符串、把字段名记错、用中文引号、或者把多个概念塞进一个查询。
/// 这些不接住的话，表现是"工具调了但查不到东西"，而模型会以为库里真的没有。
/// </summary>
public static class Tools
{
    public static void Run()
    {
        Harness.Section("工具声明");

        var all = ToolCatalog.VocabTools();
        Harness.Eq("词库工具共三个（web_search 由配置门控，不在这里）", all.Count, 3);
        Harness.True("名字都带 danbooru_ 前缀，降低与平台自己的工具撞名的概率",
                     all.All(t => t.Name.StartsWith("danbooru_", StringComparison.Ordinal)));
        Harness.True("每个工具都有 object 参数块",
                     all.All(t => ChatWire.GetString(t.Parameters, "type") == "object"));
        Harness.True("每个工具都写了说明（平台不会替我们介绍它）",
                     all.All(t => t.Description.Length > 60));
        Harness.True("三个工具都是批量入参（数组），单个只是长度为 1",
                     all.All(t => t.Parameters["properties"]!.AsObject().All(p =>
                         ChatWire.GetString(p.Value as JsonObject, "type") == "array"
                         || ChatWire.GetString(p.Value as JsonObject, "type") == "integer")));

        var required = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ToolCatalog.SearchName] = "queries",
            [ToolCatalog.ResolveName] = "tags",
            [ToolCatalog.ScanName] = "texts",
        };
        foreach (var decl in all)
        {
            var want = required[decl.Name];
            Harness.True($"{decl.Name} 的 required 里确实有 {want}",
                         decl.Parameters["required"]!.AsArray()
                             .Any(n => n!.GetValue<string>() == want));
            Harness.Eq($"{decl.Name} 的 {want} 是 string 数组",
                       ChatWire.GetString(decl.Parameters["properties"]![want] as JsonObject, "type"),
                       "array");
        }

        // 包成 OpenAI 的 tools 元素
        var wrapped = all[0].ToOpenAiTool();
        Harness.Eq("包出来是 type=function", ChatWire.GetString(wrapped, "type"), "function");
        Harness.True("函数声明在 function 字段里", wrapped["function"] is JsonObject);
        Harness.Eq("函数名一致",
                   ChatWire.GetString(wrapped["function"] as JsonObject, "name"), all[0].Name);

        // 说明里必须明确禁止凭记忆报标签名——这是整个工具存在的理由
        Harness.True("search 的说明里明确禁止凭记忆猜标签名",
                     all[0].Description.Contains("never name a tag from memory",
                                                 StringComparison.OrdinalIgnoreCase));

        Harness.Section("重名让路");

        var plain = ToolCatalog.Plan(new[] { "read_file", "bash" });
        Harness.Eq("不撞名时原名下发", plain.Tools[0].Name, ToolCatalog.SearchName);
        Harness.Eq("三个都在", plain.Tools.Count, 3);
        Harness.True("IsOurs 认得自己的名字", plain.IsOurs(ToolCatalog.SearchName));
        Harness.True("IsOurs 认不出别人的名字", !plain.IsOurs("read_file"));

        var collided = ToolCatalog.Plan(new[] { ToolCatalog.SearchName, ToolCatalog.ResolveName });
        Harness.True("撞名的那两个加了后缀",
                     collided.Tools.Any(t => t.Name == ToolCatalog.SearchName + ToolCatalog.CollisionSuffix) &&
                     collided.Tools.Any(t => t.Name == ToolCatalog.ResolveName + ToolCatalog.CollisionSuffix));
        Harness.True("没撞的第三个保持原名",
                     collided.Tools.Any(t => t.Name == ToolCatalog.ScanName));
        Harness.True("让路之后 IsOurs 仍然认得",
                     collided.IsOurs(ToolCatalog.SearchName + ToolCatalog.CollisionSuffix));
        Harness.Eq("也仍然能派发到正确的语义",
                   collided.KindOf(ToolCatalog.SearchName + ToolCatalog.CollisionSuffix),
                   (ToolKind?)ToolKind.Search);

        // 连加后缀都撞：再让一步，绝不能覆盖客户端的声明
        var doubleCollide = ToolCatalog.Plan(new[]
        {
            ToolCatalog.SearchName, ToolCatalog.SearchName + ToolCatalog.CollisionSuffix
        });
        Harness.True("二次撞名时继续让路，不覆盖客户端",
                     doubleCollide.Tools.Any(t => t.Name == ToolCatalog.SearchName + ToolCatalog.CollisionSuffix + "2"));
        Harness.Eq("三个工具一个不少", doubleCollide.Tools.Count, 3);

        Harness.Section("工具参数读取（模型的手抖都要接住）");

        Harness.Eq("标准数组",
                   string.Join(",", ToolHost.ReadStringList("{\"queries\":[\"white dress\",\"lace\"]}",
                                                             "queries", "query", 12)),
                   "white dress,lace");
        Harness.Eq("数组里的空串与非字符串元素被丢掉",
                   string.Join(",", ToolHost.ReadStringList(
                       "{\"queries\":[\" a \",\"\",3,\"b\"]}", "queries", "query", 12)),
                   "a,b");
        Harness.Eq("重复项按大小写不敏感去重、保留顺序",
                   string.Join(",", ToolHost.ReadStringList(
                       "{\"queries\":[\"Lace\",\"lace\",\"dress\"]}", "queries", "query", 12)),
                   "Lace,dress");
        Harness.Eq("声明是数组却传裸字符串 → 当单元素",
                   string.Join(",", ToolHost.ReadStringList("{\"queries\":\"white dress\"}",
                                                             "queries", "query", 12)),
                   "white dress");
        Harness.Eq("整个参数就是裸字符串",
                   string.Join(",", ToolHost.ReadStringList("\"lace\"", "queries", "query", 12)),
                   "lace");
        Harness.Eq("没加引号的裸值也认（小模型常见手抖）",
                   string.Join(",", ToolHost.ReadStringList("white dress", "queries", "query", 12)),
                   "white dress");
        Harness.Eq("字段名写错 → 对象里的字符串数组照收",
                   string.Join(",", ToolHost.ReadStringList("{\"tags\":[\"blush\"]}",
                                                             "queries", "query", 12)),
                   "blush");
        Harness.Eq("旧字段名（单数）仍然可用",
                   string.Join(",", ToolHost.ReadStringList("{\"tag\":\"blush\"}",
                                                             "tags", "tag", 12)),
                   "blush");
        Harness.Eq("对象数组不猜（不认识的形状宁可空）",
                   string.Join(",", ToolHost.ReadStringList("{\"queries\":[{\"q\":\"a\"}]}",
                                                             "queries", "query", 12)),
                   "");
        Harness.Eq("空参数", ToolHost.ReadStringList("", "queries", "query", 12).Count, 0);
        Harness.Eq("超上限被截到 cap",
                   ToolHost.ReadStringList("{\"queries\":[\"a\",\"b\",\"c\"]}", "queries", "query", 2).Count, 2);

        Harness.Eq("limit 数字", ToolHost.ReadInt("{\"queries\":[\"x\"],\"limit\":5}", "limit", 8), 5);
        Harness.Eq("limit 缺省用默认值", ToolHost.ReadInt("{\"queries\":[\"x\"]}", "limit", 8), 8);
        Harness.Eq("limit 写成字符串也认", ToolHost.ReadInt("{\"limit\":\"3\"}", "limit", 8), 3);
        Harness.Eq("limit 非法时回落默认值", ToolHost.ReadInt("{\"limit\":\"x\"}", "limit", 8), 8);
    }
}
