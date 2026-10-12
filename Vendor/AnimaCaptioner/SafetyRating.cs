using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimaCaptioner.Core;

/// <summary>
/// 安全标签的判据。
///
/// 背景：Anima 官方模型卡的 `## Safety tags` 一节**只列了四个词**
/// （`safe, sensitive, nsfw, explicit`），一句判据都没给。所以边界必须从外部借标准。
/// 这里采用的是 **Danbooru 官方评级规范**（wiki `howto:rate`），理由：
///   - Anima 的训练数据来自 Danbooru，而 Danbooru 存的就是这四档评级；
///   - 两套词表档数相同、递进顺序相同，两个词（sensitive / explicit）字面相同；
///   - 它是唯一有逐条原文边界的公开标准。
///
/// 对应关系（**推断**，非官方声明）：
///   safe      ← rating:general
///   sensitive ← rating:sensitive
///   nsfw      ← rating:questionable
///   explicit  ← rating:explicit
///
/// 本文件里的每个词表都能在 howto:rate 原文里找到对应句子。改动前请先读
/// `docs/08-safety-ratings.md`。
/// </summary>
public static class SafetyRating
{
    /// <summary>四个官方安全词，按强度递增。</summary>
    public static readonly string[] Ladder = { "safe", "sensitive", "nsfw", "explicit" };

    /// <summary>Danbooru 的四个评级值（与 Ladder 一一对应，仅用于显示）。</summary>
    public static readonly string[] DanbooruLadder = { "general", "sensitive", "questionable", "explicit" };

    /// <summary>
    /// 安全词表**以 <see cref="TagSections.SafetyWords"/> 为唯一真源**，这里只额外
    /// 认 Danbooru 自己的写法 `general`（Anima 用的是 `safe`）。
    ///
    /// 此前两边各写了一份、成员还不一致：TagSections 那份有 questionable 没 general，
    /// 这份反过来。改一处忘另一处时，"归类"和"校验"就会对同一个标签给出不同判断。
    /// </summary>
    private static readonly HashSet<string> SafetyWords = new(
        TagSections.SafetyWords.Append("general"), StringComparer.OrdinalIgnoreCase);

    public static bool IsSafetyWord(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && SafetyWords.Contains(tag.Trim());

    /// <summary>强度序号，0=safe … 3=explicit；不是安全词返回 -1。</summary>
    public static int Rank(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return -1;
        var low = tag.Trim().ToLowerInvariant();
        // Danbooru 的写法也接受，映射到同一档
        if (low == "general") low = "safe";
        if (low == "questionable") low = "nsfw";
        return Array.IndexOf(Ladder, low);
    }

    /// <summary>
    /// rating:explicit —— 露出的性器、性行为、体液。**打码不降档**。
    /// </summary>
    private static readonly HashSet<string> Explicit = new(StringComparer.OrdinalIgnoreCase)
    {
        // 露出性器（原文："Exposed genitals … whether censored or uncensored"）
        "pussy", "penis", "anus", "clitoris", "testicles", "urethra", "glans",
        "vaginal", "pubic hair", "pubic",
        // 性行为（原文点名 sex / fellatio / masturbation / handjobs / fingering / sex toy use）
        "sex", "hetero", "anal", "fellatio", "cunnilingus", "masturbation",
        "handjob", "handjobs", "fingering", "paizuri", "footjob",
        "sex toy", "sex toys", "dildo", "vibrator", "onahole", "buttplug", "anal beads",
        // 体液（原文："Bodily fluids, including cum, used condoms, and pussy juice"）
        "cum", "cum in pussy", "cum on body", "cum on breasts", "cum on face",
        "pussy juice", "precum", "semen", "creampie", "used condom", "after sex",
        // 刻意展示（原文：把 "presenting / spread legs" 归入 blatantly exposed）
        "spread pussy", "spread legs", "presenting pussy", "presenting anus",
        // 猎奇 / 排泄（原文："Guro, scat, extremely graphic violence"）
        "guro", "scat",
    };

    /// <summary>
    /// rating:questionable —— 非性器裸露、近裸、隔着衣服看得清、轻度性接触、拘束类。
    /// </summary>
    private static readonly HashSet<string> Nsfw = new(StringComparer.OrdinalIgnoreCase)
    {
        // 非性器裸露（原文："Simple non-genital nudity, including nipples, areolae, and bare ass"）
        "nude", "completely nude", "topless", "bottomless",
        "nipples", "areolae", "bare ass", "bare buttocks", "bare back",
        // 近裸 / 衣服半脱（原文："clothing that is partially removed or torn"）
        "clothes removed", "unworn clothes", "undressing", "partially undressed", "torn clothes",
        "panty pull", "panties pull", "pantyhose pull", "skirt removed", "skirt lift",
        "clothes lift", "clothes pull", "shirt lift", "dress lift", "wardrobe malfunction",
        // 隔衣可见（原文："Nipples or genitals clearly visible though clothing"）
        "covered nipples", "pronounced cameltoe", "wedgie", "bulge",
        "erection", "erection under clothes",
        // 轻度性接触（原文："Mild sexual contact (ear biting, groping, grabbing another's breast…)"）
        "groping", "grabbing another's breast", "grabbing another's ass", "ear biting",
        "heavy kissing", "french kiss",
        // 拘束 / BDSM（原文："without visible sex acts or bodily fluids"）
        "bondage", "bdsm", "spanking", "shibari", "bound wrists", "restrained",
        "blindfold", "collar and leash", "leash",
        // 摆着未用的性玩具（原文："conspicuously visible but aren't in use"）
        "presenting", "presenting breasts", "presenting ass", "presenting body",
        "presenting another", "presenting another's body", "presenting another's ass",
        // 暗示 / 模拟（原文："Implied sex acts that aren't clearly shown"）
        "implied sex", "simulated sex", "simulated fellatio", "simulated footjob",
        "penetration gestures", "fellatio gestures", "oral invitation",
    };

    /// <summary>
    /// rating:sensitive —— 暴露服装、镂空、镜头聚焦性化部位、走光、薄透湿。
    /// </summary>
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        // 暴露服装（原文点名 swimsuits / lingerie / underwear / playboy bunnysuits / leotards / skin tight）
        "swimsuit", "bikini", "micro bikini", "one-piece swimsuit", "competition swimsuit",
        "school swimsuit", "lingerie", "underwear", "panties", "bra", "bra only",
        "bra pull", "bra slip", "highleg panties", "highleg", "leotard", "skin tight",
        "playboy bunnysuit", "bodysuit", "nightgown", "pajamas", "sleepwear", "babydoll",
        // 镂空 —— 官方在 sensitive 一节**点名** "cleavage cutouts"
        "clothing cutout", "cleavage cutout", "underboob", "sideboob",
        // 镜头聚焦性化部位（原文："Anything focused on the ass, breasts, cleavage, underboob,
        //   sideboob, feet, armpits, midriff/stomach, navel, lips"）
        "cleavage", "ass", "breasts", "large breasts", "huge breasts", "small breasts",
        "midriff", "midriff peek", "navel", "stomach", "armpits", "feet", "toes",
        "lips", "thighs", "bare thighs", "thighhighs", "garter straps", "leg garter",
        // 走光（原文："Pantyshots, upskirts, and similar fanservice"）
        "pantyshot", "upskirt", "downblouse", "groin", "hip lines", "dimples of venus",
        // 薄 / 透 / 湿（原文："Thin, see-through, or wet clothes that reveal the underwear"）
        "see-through", "see-through clothes", "wet clothes", "wet shirt", "transparent",
        // 轻微驼趾 / 遮住的乳头（原文："Subtle cameltoes or covered nipples not blatant enough
        //   for rating:questionable"）
        "cameltoe",
        // 其余暴露（官方列入 sensitive 的着装状态）
        "bare shoulders", "collarbone", "short shorts", "micro shorts", "crop top",
        "sleeveless", "plunging neckline", "backless", "barefoot", "pantyhose",
        "fishnet thighhighs", "fishnets", "no shoes",
        // 接吻本身官方归 general（"kissing that isn't sexually charged"），但同一条
        // 官方兜底规则说"拿不准够不够 general 就用 sensitive"，而插画里的接吻
        // 常带情欲语境，所以放这一档。真正情欲的（heavy kissing / french kiss）在 nsfw。
        "kiss",
    };

    /// <summary>
    /// 按 Danbooru 官方判据给一组标签定档。
    /// 返回 (档位, 命中依据)。都未命中 → "safe"。
    /// </summary>
    public static (string Rating, List<string> Evidence) Judge(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(
            tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var hit = set.Intersect(Explicit, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (hit.Count > 0) return ("explicit", hit);

        hit = set.Intersect(Nsfw, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (hit.Count > 0) return ("nsfw", hit);

        hit = set.Intersect(Sensitive, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (hit.Count > 0) return ("sensitive", hit);

        return ("safe", new List<string>());
    }

    /// <summary>一条 caption 的安全标签检查结果。</summary>
    public sealed record Verdict(
        string? Declared,     // 文件里实际写的安全词（已归一化到 Ladder 用词）；缺失为 null
        int DeclaredCount,    // 写了几个（应为 1）
        string Expected,      // 按官方判据应是什么
        List<string> Evidence,// 判据命中的标签
        List<string> Notes);  // 人可读的问题描述

    /// <summary>
    /// 检查一条 caption 的安全标签。
    ///
    /// 注意：<paramref name="tagWords"/> 应当是**去掉散文尾巴之后的标签**——
    /// 散文里出现 "sex" 之类的英文单词不该触发定档。
    /// </summary>
    public static Verdict Check(IEnumerable<string> tagWords)
    {
        var words = tagWords.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        var declared = words.Where(IsSafetyWord).ToList();
        var body = words.Where(t => !IsSafetyWord(t)).ToList();

        var (expected, evidence) = Judge(body);
        var notes = new List<string>();

        string? norm = null;
        if (declared.Count > 0)
        {
            norm = declared[0].ToLowerInvariant() switch
            {
                "general" => "safe",
                "questionable" => "nsfw",
                var x => x,
            };
        }

        if (declared.Count == 0)
            notes.Add($"缺安全标签（判据认为应打 {expected}）");
        else if (declared.Count > 1)
            notes.Add($"写了 {declared.Count} 个安全标签（{string.Join(", ", declared)}），四个词互斥，只该留一个");

        if (declared.Count == 1)
        {
            var d = Rank(norm);
            var e = Rank(expected);
            if (d != e)
            {
                var dir = d > e ? "偏严" : "偏松";
                notes.Add($"现标 {norm}，但按 Danbooru 判据应为 {expected}（{dir}一档）");
            }
        }

        return new Verdict(norm, declared.Count, expected, evidence, notes);
    }
}
