using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AnimaAIProxy.Core;

/// <summary>一次提取的结果。<see cref="Markdown"/> 是给模型读的正文。</summary>
public sealed record PageContent(string Title, string Description, string Markdown, bool Truncated);

/// <summary>
/// 网页正文提取：HTML → Markdown。**这是 web_fetch 的质量核心**，与用哪个引擎抓的无关
/// —— 浏览器引擎也把渲染后的 HTML 交回这里，两条路共用同一份提取逻辑（见 <see cref="PageReader"/>）。
///
/// 为什么要自己写一层而不是直接把 HTML 丢给模型：真实网页里正文通常只占百分之几，
/// 其余是导航、广告、脚本、评论区。整页塞回去会把用户真正问的东西挤出上下文，
/// 而模型还得自己去分辨哪一段才是内容。这与 <see cref="SearchClient"/> 里
/// "摘要必须截断"是同一条理由。
///
/// 打分思路借鉴 Mozilla Readability，但**做了两处针对中文站点的改动**，
/// 因为实测国内站点常常整块正文都用 &lt;div&gt; 而不是 &lt;p&gt;：
///
/// 1. **不靠 &lt;p&gt; 计分**（Readability 给每个 &lt;p&gt; 打分再往父节点累加）。
///    实测知乎的发现页全文在一个 &lt;div&gt; 里，一个 &lt;p&gt; 都没有，
///    照 Readability 那套会得到"没有候选"，直接退化成整页正文。
///    这里改成**用子树的正文长度计分**，两类站点都能用。
/// 2. **同分取最深**。子树长度这个口径有个必然结果：根节点永远最长。
///    所以取"达到最高分 85% 的候选里最深的那个" —— 内容外层往往套了
///    好几层 wrapper，最深的那层才是真正的正文容器。
///
/// 三个"不这么写就会错"的细节：
///
/// * **计分前必须先摘掉 script/style**。HtmlAgilityPack 的 <c>InnerText</c> 会连
///   &lt;script&gt; 里的 JS 源码一起返回，一段内联脚本几万字符是常事，
///   不摘的话分数最高的永远是某个装了脚本的容器。
/// * **&lt;header&gt; 不能当垃圾摘掉**。文章自己的 &lt;header&gt; 里往往放着
///   标题（h1），整块摘掉正文就没了开头。导航区靠**链接密度**识别，不靠标签名。
/// * **实体解码要挑地方**。HtmlAgilityPack 的 <c>InnerText</c> 对 &lt;title&gt;
///   这类不一定会解码（实测拿到的是 <c>What&amp;#39;s new</c>），所以要显式
///   <c>DeEntitize</c>；但 &lt;pre&gt;/&lt;code&gt; 里**不能**解 ——
///   代码示例里的 <c>&amp;amp;</c> 是内容本身，解了就改坏了。
/// </summary>
public static class ArticleExtractor
{
    /// <summary>低于这个长度就不认为是一篇正文（用于"这一页需要渲染吗"的判断）。</summary>
    internal const int ThinThreshold = 200;

    /// <summary>候选节点的标签。刻意含 div/section/td —— 中文站点大量用 div 排版。</summary>
    private static readonly string[] CandidateXPath =
        { "//article", "//main", "//section", "//div", "//td", "//body" };

    /// <summary>渲染时直接跳过的标签（永远不含正文）。</summary>
    private static readonly HashSet<string> DropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object",
        "embed", "link", "meta", "dialog", "form", "input", "select", "textarea",
        "button", "map", "area", "audio", "video", "track", "source"
    };

    /// <summary>会另起一段的标签。用于在 Markdown 里插空行。</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "header", "footer", "aside", "nav",
        "figure", "figcaption", "details", "summary", "dl", "dt", "dd", "address",
        "fieldset", "label", "center", "body"
    };

    /// <summary>
    /// 导航 / 广告 / 评论这类"不是正文"的 id/class 线索。
    /// 前面要求有边界（行首、空格、连字符、下划线），否则 <c>inside</c> 会命中 <c>side</c>、
    /// <c>gradient</c> 会命中 <c>ad</c> —— 这类误伤会让正文被整块吃掉，而且完全没有症状。
    /// </summary>
    private static readonly Regex JunkHint = new(
        @"(?:^|[\s\-_])(nav|navigation|navbar|menu|sidebar|side|footer|comment|comments|" +
        @"share|sharing|social|related|recommend|recommended|promo|advert|ads|ad|banner|" +
        @"breadcrumb|copyright|disclaimer|subscribe|newsletter|popup|modal|toolbar|" +
        @"pagination|pager|widget|sponsor|login|signin|signup|search|rating|tags|taglist)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>正文线索。命中会加权，**不是必须** —— 没有线索的站点靠长度与链接密度也能选中。</summary>
    private static readonly Regex ContentHint = new(
        @"(?:^|[\s\-_])(content|article|post|entry|main|markdown|rich|richcontent|detail|" +
        @"story|chapter|wiki|doc|docs|text|body|read|bodycontent|articlebody|postbody)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 提取正文。<paramref name="baseUrl"/> 用来把相对链接补成绝对链接
    /// （模型拿到 <c>/wiki/help</c> 这种相对路径没法用）。
    /// </summary>
    public static PageContent Extract(string html, string baseUrl, int maxChars)
    {
        var doc = new HtmlDocument { OptionMaxNestedChildNodes = 4000 };
        try { doc.LoadHtml(html ?? ""); }
        catch { return new PageContent("", "", "", false); }

        var title = Meta(doc, "og:title");
        if (title.Length == 0) title = DeEntitize(doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        var desc = Meta(doc, "og:description");
        if (desc.Length == 0) desc = MetaName(doc, "description");

        Strip(doc);

        var node = PickContent(doc);
        var sb = new StringBuilder(Math.Min(html?.Length ?? 0, maxChars + 1024));
        var ctx = new Ctx(baseUrl);
        Render(node, sb, ctx);

        var md = Tidy(sb.ToString());
        var truncated = false;
        if (md.Length > maxChars)
        {
            md = Cut(md, maxChars);
            truncated = true;
        }

        return new PageContent(Collapse(title), Collapse(desc), md, truncated);
    }

    /// <summary>这一页的正文是不是"薄得不像正文"—— 用于判断要不要改用浏览器引擎再试。</summary>
    public static bool IsThin(string markdown) => Collapse(markdown).Length < ThinThreshold;

    // ---------------------------------------------------------------- 清理

    /// <summary>
    /// 摘掉**结构上**就不可能含正文的节点。刻意不含 nav/header/footer/aside：
    /// 那些在计分阶段靠链接密度与 id/class 线索降权，在渲染阶段靠 <see cref="IsJunk"/> 跳过，
    /// 而 &lt;header&gt; 常常装着正文的 h1，按标签名整块摘掉会丢掉标题。
    /// </summary>
    private static void Strip(HtmlDocument doc)
    {
        var kill = doc.DocumentNode.SelectNodes(
            "//script|//style|//noscript|//template|//svg|//canvas|//iframe|//object|" +
            "//embed|//link|//meta|//dialog|//form|//input|//select|//textarea|//button|" +
            "//audio|//video|//source|//track");
        if (kill is null) return;
        foreach (var n in kill) n.Remove();
    }

    /// <summary>这个节点在渲染时该跳过吗（导航/广告/评论/隐藏元素）。</summary>
    private static bool IsJunk(HtmlNode n)
    {
        if (n.NodeType != HtmlNodeType.Element) return false;
        var name = n.Name;
        if (DropTags.Contains(name)) return true;
        if (name.Equals("nav", StringComparison.OrdinalIgnoreCase)) return true;

        var role = n.GetAttributeValue("role", "");
        if (role.Equals("navigation", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("banner", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("complementary", StringComparison.OrdinalIgnoreCase)) return true;

        if (n.GetAttributeValue("aria-hidden", "") == "true") return true;
        if (n.Attributes.Contains("hidden")) return true;

        var style = n.GetAttributeValue("style", "");
        if (style.Length > 0 &&
            (style.Contains("display:none", StringComparison.OrdinalIgnoreCase) ||
             style.Contains("display: none", StringComparison.OrdinalIgnoreCase) ||
             style.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase) ||
             style.Contains("visibility: hidden", StringComparison.OrdinalIgnoreCase))) return true;

        return JunkHint.IsMatch(Hint(n));
    }

    /// <summary>id + class + role 拼成的线索串（正则只需要一个字符串）。</summary>
    private static string Hint(HtmlNode n) =>
        n.GetAttributeValue("id", "") + " " +
        n.GetAttributeValue("class", "") + " " +
        n.GetAttributeValue("role", "");

    // ---------------------------------------------------------------- 选正文

    /// <summary>
    /// 挑正文容器：先算每个候选的子树的正文长度与链接占比，再打分，
    /// 最后在"达到最高分 85% 的候选"里取最深的那个。
    /// </summary>
    private static HtmlNode PickContent(HtmlDocument doc)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        var textLen = new Dictionary<HtmlNode, int>();
        var linkLen = new Dictionary<HtmlNode, int>();
        Measure(body, textLen, linkLen, 0);

        HtmlNode best = body;
        var bestScore = -1.0;
        var candidates = new List<(HtmlNode Node, double Score, int Depth)>();
        var depth = new Dictionary<HtmlNode, int>();
        MarkDepth(body, depth, 0);

        foreach (var xp in CandidateXPath)
        {
            var nodes = doc.DocumentNode.SelectNodes(xp);
            if (nodes is null) continue;
            foreach (var n in nodes) candidates.Add((n, Score(n, textLen, linkLen), depth.GetValueOrDefault(n)));
        }

        foreach (var (node, score, _) in candidates)
        {
            if (score > bestScore) { bestScore = score; best = node; }
        }

        if (bestScore <= 0) return body;

        // 同分取最深：内容外面常套着好几层 wrapper，最深的那层才是正文。
        var threshold = bestScore * 0.85;
        var pick = best;
        var pickDepth = depth.GetValueOrDefault(best);
        foreach (var (node, score, d) in candidates)
        {
            if (score < threshold) continue;
            if (d > pickDepth) { pick = node; pickDepth = d; }
        }
        return pick;
    }

    private static double Score(HtmlNode n,
                                Dictionary<HtmlNode, int> textLen,
                                Dictionary<HtmlNode, int> linkLen)
    {
        var len = textLen.GetValueOrDefault(n);
        if (len < 25) return 0;

        var s = (double)len;

        // 链接占比高 → 是导航/目录页，不是正文。这一条是识别导航的主力：
        // 不靠标签名（<nav> 常被省略），靠"这段文字有多少是链接文字"。
        var links = linkLen.GetValueOrDefault(n);
        var density = (double)links / len;
        if (density > 0.4) s *= 1.0 - Math.Min(0.9, density);

        if (JunkHint.IsMatch(Hint(n))) s *= 0.15;
        if (ContentHint.IsMatch(Hint(n))) s *= 1.6;

        s *= n.Name.ToLowerInvariant() switch
        {
            "article" => 1.40,
            "main" => 1.35,
            "section" => 1.05,
            "td" => 0.90,
            "body" => 0.95,     // 略微压低：能选中更具体的容器就不要选整页
            _ => 1.00
        };
        return s;
    }

    /// <summary>自底向上算每个节点的"正文长度"与"链接文字长度"。一次遍历，避免逐候选重复计算。</summary>
    private static void Measure(HtmlNode node,
                                Dictionary<HtmlNode, int> textLen,
                                Dictionary<HtmlNode, int> linkLen,
                                int depth)
    {
        if (depth > 400) { textLen[node] = 0; linkLen[node] = 0; return; }

        var total = 0;
        var links = 0;
        var isAnchor = node.NodeType == HtmlNodeType.Element &&
                       node.Name.Equals("a", StringComparison.OrdinalIgnoreCase);

        foreach (var c in node.ChildNodes)
        {
            int len;
            if (c.NodeType == HtmlNodeType.Text)
            {
                len = Collapse(c.InnerText).Length;
            }
            else if (c.NodeType == HtmlNodeType.Element)
            {
                Measure(c, textLen, linkLen, depth + 1);
                len = textLen.GetValueOrDefault(c);
                if (isAnchor) links += linkLen.GetValueOrDefault(c);
            }
            else continue;

            if (len <= 0) continue;
            if (total > 0) total++;     // 相邻块之间算一个分隔空格
            total += len;
        }

        textLen[node] = total;
        linkLen[node] = isAnchor ? total : links;
    }

    private static void MarkDepth(HtmlNode node, Dictionary<HtmlNode, int> map, int depth)
    {
        if (depth > 400) return;
        map[node] = depth;
        foreach (var c in node.ChildNodes)
            if (c.NodeType == HtmlNodeType.Element) MarkDepth(c, map, depth + 1);
    }

    // ---------------------------------------------------------------- 渲染成 Markdown

    private sealed class Ctx
    {
        public Ctx(string baseUrl)
        {
            BaseUrl = baseUrl;
            // 不能直接 `out Base`：属性不是变量（CS0206）。
            Base = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri : null;
        }

        public string BaseUrl { get; }
        public Uri? Base { get; }
        public int ListDepth { get; set; }
    }

    private static void Render(HtmlNode node, StringBuilder sb, Ctx ctx)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                AppendText(sb, child.InnerText);
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element) continue;
            if (IsJunk(child)) continue;

            var name = child.Name.ToLowerInvariant();
            switch (name)
            {
                case "br":
                    sb.Append('\n');
                    break;

                case "hr":
                    Block(sb);
                    sb.Append("---");
                    Block(sb);
                    break;

                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    Block(sb);
                    sb.Append('#', name[1] - '0').Append(' ');
                    Inline(child, sb, ctx);
                    Block(sb);
                    break;

                case "p":
                    Block(sb);
                    Inline(child, sb, ctx);
                    Block(sb);
                    break;

                case "pre":
                    Block(sb);
                    sb.Append("```\n").Append(DeEntitize(child.InnerText).Trim('\n', '\r', ' ')).Append("\n```");
                    Block(sb);
                    break;

                case "code":
                    // 在 <pre> 里时由 pre 整块处理，这里只处理行内代码。
                    if (InsidePre(child)) Inline(child, sb, ctx);
                    else sb.Append('`').Append(Collapse(DeEntitize(child.InnerText))).Append('`');
                    break;

                case "a":
                    Anchor(child, sb, ctx);
                    break;

                case "img":
                    Image(child, sb, ctx);
                    break;

                case "ul": case "ol":
                    List(child, sb, ctx, name == "ol");
                    break;

                case "blockquote":
                    Quote(child, sb, ctx);
                    break;

                case "table":
                    Table(child, sb, ctx);
                    break;

                case "strong": case "b":
                    sb.Append("**"); Inline(child, sb, ctx); sb.Append("**");
                    break;

                case "em": case "i":
                    sb.Append('*'); Inline(child, sb, ctx); sb.Append('*');
                    break;

                default:
                    if (BlockTags.Contains(name))
                    {
                        Block(sb);
                        Render(child, sb, ctx);
                        Block(sb);
                    }
                    else
                    {
                        Render(child, sb, ctx);
                    }
                    break;
            }
        }
    }

    /// <summary>行内渲染：与块级相同，只是不插空行。</summary>
    private static void Inline(HtmlNode node, StringBuilder sb, Ctx ctx) => Render(node, sb, ctx);

    /// <summary>
    /// 追加一段文本。**两个细节都不能省**：
    ///
    /// 1. **保留首尾的空格**。HTML 里 &lt;b&gt; 两侧的空格是有效的词间分隔，
    ///    而 <c>Collapse</c> 会把它 trim 掉 —— 于是
    ///    <c>Hello &lt;b&gt;world&lt;/b&gt; again</c> 会渲染成
    ///    <c>Hello**world**again</c>。中文看不出来（本来就不加空格），
    ///    英文整段会粘成一坨。
    /// 2. **解码实体**。HtmlAgilityPack 的 <c>InnerText</c> 并不总是解码
    ///    （实测 &lt;title&gt; 拿回来的是 <c>What&amp;#39;s new</c>），
    ///    不解码的话模型看到的是 <c>&amp;amp;</c> 这种源码形态。
    ///    每个文本节点只解一次，所以不会出现二次解码把 <c>&amp;amp;amp;</c> 吃成 <c>&amp;</c>。
    /// </summary>
    private static void AppendText(StringBuilder sb, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        var t = Whitespace.Replace(raw, " ");
        var core = t.Trim();

        if (core.Length == 0)
        {
            if (sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' ');
            return;
        }
        if (t[0] == ' ' && sb.Length > 0 && sb[^1] is not (' ' or '\n')) sb.Append(' ');
        sb.Append(DeEntitize(core));
        if (t[^1] == ' ') sb.Append(' ');
    }

    private static void Anchor(HtmlNode node, StringBuilder sb, Ctx ctx)
    {
        var text = Collapse(DeEntitize(node.InnerText));
        var href = Absolutize(node.GetAttributeValue("href", ""), ctx);

        if (href.Length == 0 || text.Length == 0)
        {
            if (text.Length > 0) sb.Append(text);
            else if (href.Length > 0) sb.Append(href);
            return;
        }
        // 锚文本就是地址时不必重复写两遍（"目录"类页面里很常见）
        if (string.Equals(text, href, StringComparison.OrdinalIgnoreCase)) { sb.Append(href); return; }

        sb.Append('[').Append(text).Append("](").Append(href).Append(')');
    }

    private static void Image(HtmlNode node, StringBuilder sb, Ctx ctx)
    {
        var src = Absolutize(node.GetAttributeValue("src", ""), ctx);
        if (src.Length == 0) src = Absolutize(node.GetAttributeValue("data-src", ""), ctx);
        if (src.Length == 0) return;
        var alt = Collapse(node.GetAttributeValue("alt", ""));
        sb.Append("![").Append(alt).Append("](").Append(src).Append(')');
    }

    private static void List(HtmlNode node, StringBuilder sb, Ctx ctx, bool ordered)
    {
        var i = 1;
        foreach (var li in node.ChildNodes)
        {
            if (li.NodeType != HtmlNodeType.Element ||
                !li.Name.Equals("li", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsJunk(li)) continue;

            Block(sb);
            sb.Append(' ', ctx.ListDepth * 2);
            sb.Append(ordered ? (i++).ToString() + ". " : "- ");
            ctx.ListDepth++;
            Inline(li, sb, ctx);
            ctx.ListDepth--;
        }
        Block(sb);
    }

    private static void Quote(HtmlNode node, StringBuilder sb, Ctx ctx)
    {
        var inner = new StringBuilder();
        Render(node, inner, ctx);
        var lines = Tidy(inner.ToString()).Split('\n');
        Block(sb);
        foreach (var line in lines) sb.Append("> ").Append(line).Append('\n');
        Block(sb);
    }

    private static void Table(HtmlNode node, StringBuilder sb, Ctx ctx)
    {
        Block(sb);
        foreach (var tr in node.SelectNodes(".//tr") ?? new HtmlNodeCollection(null))
        {
            if (IsJunk(tr)) continue;
            var cells = new List<string>();
            foreach (var cell in tr.ChildNodes)
            {
                if (cell.NodeType != HtmlNodeType.Element) continue;
                var cn = cell.Name.ToLowerInvariant();
                if (cn != "td" && cn != "th") continue;
                var inner = new StringBuilder();
                Render(cell, inner, ctx);
                cells.Add(Collapse(inner.ToString()).Replace("|", "\\|"));
            }
            if (cells.Count == 0) continue;
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }
        Block(sb);
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>确保这里是段落边界（最多一个空行）。</summary>
    private static void Block(StringBuilder sb)
    {
        var n = sb.Length;
        var newlines = 0;
        while (n > 0 && sb[n - 1] == '\n') { newlines++; n--; }
        if (sb.Length == 0) return;
        if (newlines == 0) sb.Append("\n\n");
        else if (newlines == 1) sb.Append('\n');
    }

    private static bool InsidePre(HtmlNode node)
    {
        for (var p = node.ParentNode; p is not null; p = p.ParentNode)
            if (p.Name.Equals("pre", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Absolutize(string href, Ctx ctx)
    {
        var h = (href ?? "").Trim();
        if (h.Length == 0) return "";
        if (h.StartsWith('#')) return "";        // 页内锚点对模型没用
        if (h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return "";
        if (h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return h;
        if (h.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "";

        if (ctx.Base is not null && Uri.TryCreate(ctx.Base, h, out var abs))
            return abs.ToString();
        return h;
    }

    /// <summary>收尾：压掉多余空行、去掉行尾空格、规范列表缩进。</summary>
    private static string Tidy(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>按长度截断，尽量切在行边界上（切在半句话中间会让模型以为句子就这么长）。</summary>
    private static string Cut(string s, int max)
    {
        var cut = s[..Math.Min(max, s.Length)];
        var nl = cut.LastIndexOf('\n');
        if (nl > max * 0.6) cut = cut[..nl];
        return cut.TrimEnd() + "\n\n[内容已截断]";
    }

    private static string Meta(HtmlDocument doc, string property)
    {
        var n = doc.DocumentNode.SelectSingleNode($"//meta[@property='{property}']") ??
                doc.DocumentNode.SelectSingleNode($"//meta[@name='{property}']");
        return DeEntitize(n?.GetAttributeValue("content", "") ?? "");
    }

    private static string MetaName(HtmlDocument doc, string name)
    {
        var n = doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']");
        return DeEntitize(n?.GetAttributeValue("content", "") ?? "");
    }

    /// <summary>解码 HTML 实体。**只对普通文本与元数据用，&lt;pre&gt;/&lt;code&gt; 里绝不能用**。</summary>
    private static string DeEntitize(string? s) => string.IsNullOrEmpty(s) ? "" : HtmlEntity.DeEntitize(s);

    internal static string Collapse(string? s) =>
        string.IsNullOrEmpty(s) ? "" : Whitespace.Replace(s, " ").Trim();
}
