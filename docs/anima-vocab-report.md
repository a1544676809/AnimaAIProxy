# AnimaCaptioner 词库内核技术报告

> **范围**：`Core/VocabDb.cs`(777 行)、`Core/TagSearch.cs`(275 行)、`Core/TagSearchAgent.cs`(190 行)、`Core/TagSections.cs`(400 行)，外加 `Core/AppSettings.cs` 的路径默认值。
> **方法**：全部代码片段与行号来自实际文件读取；数据库形态来自对**本机真实词库** `E:\ComfyUI\anima-vocab\vocab.sqlite`（113.03 MB）的 `sqlite_master` 直读与查询；第 7 节的"能否编译"结论来自**实际动手编译并运行**，不是推断。
> **未修改任何文件**（`git status --porcelain` 对 AnimaCaptioner 为空）。

---

## 0. 结论速览（先看这 8 条）

| # | 事实 | 影响 |
|---|---|---|
| 1 | 是 SQLite，用 `Microsoft.Data.Sqlite`（**不是** EF Core / System.Data.SQLite） | 可移植性好，纯托管 |
| 2 | 表有 4 张：`best`、`anima_index`、`alias`、`tags`；**C# 只读前 3 张**，`tags` 表从未被 C# 代码访问 | 建库脚本不在本仓库，在 `E:\ComfyUI\anima-vocab\build_vocab.py` |
| 3 | **`anima_index` 表不在 `build_vocab.py` 里**，由另一个已不存在的脚本 `add_anima_index.py` 追加 | 重建词库需要两份脚本，本仓库和 anima-vocab 目录都只剩后者缺失 |
| 4 | 建表 SQL **不在本仓库任何地方**（本仓库 `CREATE TABLE` 匹配数为 0） | 要照抄 schema 只能从真实 .db 里 dump（本报告第 1.3 节已 dump） |
| 5 | **`AC_VOCABDB` / `AC_SEED` 与应用程序完全无关**，只有 `tests/Harness.cs` 读它们 | 想靠环境变量给 app 换词库路径是无效的；app 只认 `settings.json` |
| 6 | 这四个文件 **不含任何 WinUI / Windows 专有调用** | 第 7 节：实测 **net8.0 纯控制台编译 0 警告 0 错误，并成功连真实 DB 跑通** |
| 7 | 真正的阻碍不在 Core，而在 `TagSearchAgent` 的传递依赖 `LlmClient` / `AppSettings` → `DpapiSecret`（P/Invoke `crypt32.dll`） | 这是**运行时**而非编译期的 Windows 依赖 |
| 8 | 中文翻译有**三处**参与搜索：`best.cn` 精确键、`best.aliases`（仅 839 行）、`best.anima_aliases`（7,911 行，**C# 完全没用**） | 有一列现成的中文别名数据被白白浪费 |

---

## 1. 数据库形态

### 1.1 是 SQLite 吗？—— 是

`Core/VocabDb.cs:1-3`：

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AnimaCaptioner.Core;
```

驱动是 **`Microsoft.Data.Sqlite`**（MIT，纯托管，跨平台）。`AnimaCaptioner.csproj:45`：

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />
```

连接串在 `VocabDb.cs:99-106`，**只读**打开：

```csharp
var cs = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Mode = SqliteOpenMode.ReadOnly
}.ToString();

using var con = new SqliteConnection(cs);
con.Open();
```

> 注：`Load()` 用 `using` 把连接在方法末尾就关掉了 —— 词库是**一次性全量读进内存**的（见 1.4），之后不再碰数据库。

### 1.2 数据库默认路径

`Core/AppSettings.cs:19-20`：

```csharp
/// <summary>词库文件的默认位置（本仓库不含词库，需自行构建后放到这里）。</summary>
public static string DefaultVocabPath(string fileName) =>
    Path.Combine(Dir, "vocab", fileName);
```

`Core/AppSettings.cs:596-598`：

```csharp
public static string Dir =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 "AnimaCaptioner");
```

所以默认落点是：

```
%LOCALAPPDATA%\AnimaCaptioner\vocab\vocab.sqlite
%LOCALAPPDATA%\AnimaCaptioner\vocab\seed.json
```

`Core/AppSettings.cs:43,46`：

```csharp
public string VocabDbPath { get; set; } = DefaultVocabPath("vocab.sqlite");

/// <summary>人工整理的中文 → 标签映射（词库构建产物之一）。</summary>
public string SeedPath { get; set; } = DefaultVocabPath("seed.json");
```

**本机实际值**（`%LOCALAPPDATA%\AnimaCaptioner\settings.json`，用户改过）：

```json
"VocabDbPath": "E:\\ComfyUI\\anima-vocab\\vocab.sqlite",
"SeedPath": "E:\\ComfyUI\\anima-vocab\\seed.json",
```

该文件确实存在，**113.03 MB**，最后改写时间与 `tags.csv` 同批。

### 1.3 表结构（照抄）

> **重要**：建表 SQL **不在本仓库**（`grep "CREATE TABLE"` 在 AnimaCaptioner 全仓库 0 命中）。建库脚本在仓库外的 `E:\ComfyUI\anima-vocab\build_vocab.py`。以下 SQL 是从真实 `.db` 的 `sqlite_master` 原样 dump 的，与 `build_vocab.py` 源码逐字一致。

#### `best` —— 权威去重表（331,836 行）★ 核心

```sql
CREATE TABLE best(
  tag TEXT,
  danbooru TEXT,
  cn,
  aliases,
  category,
  post_count INT,
  source TEXT
, deprecated INTEGER DEFAULT 0, banned INTEGER DEFAULT 0, in_anima INTEGER DEFAULT 0, anima_aliases TEXT)
```

前 7 列由 `build_vocab.py:141-165` 的 `CREATE TABLE best AS ...` 物化生成；后 4 列是后来 `ALTER TABLE best ADD COLUMN` 追加的（`build_vocab.py:210-211` 加 `deprecated`/`banned`，`add_anima_index.py` 加 `in_anima`/`anima_aliases`）。

> 注意 dump 出来的 `cn,` 和 `aliases,` **没有类型声明**——这是 SQLite 允许的无类型列，源自 `build_vocab.py` 里 `COALESCE(...) AS cn` 的 CTAS。实际存的是 TEXT。

**`best` 表实测统计：**

| 项 | 值 |
|---|---|
| 总行数 | 331,836 |
| `cn` 非空 | 331,836（100%） |
| `cn` 含汉字 | 239,096 |
| `cn` 不含汉字（中英同名直通，如 `!?` → `!?`） | 92,740 |
| `cn` **去重后**不同值 | **326,943** ← 这个数正是代码注释里的"32 万中文键" |
| `cn` 非空但 `aliases` 也非空 | **仅 839** |
| `anima_aliases` 非空 | 7,911 |
| `deprecated=1` | 3,196 |
| `banned=1` | 547 |
| `in_anima=1` | 96,132 |

`category` 取值分布（前 5 是英文大类，其余是社区精选包带来的**中文分类**）：

```
artist        153,619
character     103,904
general        52,467
copyright      20,158
meta              737
背景与环境 65 / 画面风格 59 / 光照 48 / 发型 46 / 负面词 45 /
手臂与手势 43 / 构图 42 / 表情 35 / 原神角色 34 / 基础姿势 33 / ...
```

#### `anima_index` —— Anima 自带标签索引（108,257 行）

```sql
CREATE TABLE anima_index (
        name       TEXT NOT NULL,   -- Anima form (spaces, @artist)
        raw        TEXT NOT NULL,   -- as the file writes it
        category   TEXT,
        post_count INTEGER,
        aliases    TEXT,
        PRIMARY KEY (name)
    )
```

数据源是 `raw/anima-1.0.csv`（2.5 MB，格式 `name,category_id,post_count,"aliases"`）。分类映射 `0→general, 1→artist, 3→copyright, 4→character, 5→meta`，另有 CSV 里直接给出的 `anima-cat6` / `anima-cat-1`。

**分类实测分布：**

| category | 数量 |
|---|---|
| artist | 42,197 |
| character | 30,094 |
| general | 26,275 |
| copyright | 8,612 |
| anima-cat6 | 795 |
| meta | 282 |
| anima-cat-1 | 2 |

`anima_index.aliases` 非空 8,169 行（如 `breasts` 挂着上百个逗号分隔的英文别名）。**C# 完全没有读这一列。**

#### `alias` —— Danbooru 退役名 → 现役标签（33,145 行）

```sql
CREATE TABLE alias (
            alias  TEXT NOT NULL,   -- retired spelling, Anima form (spaces, @artist)
            raw    TEXT NOT NULL,   -- retired spelling as Danbooru writes it
            tag    TEXT NOT NULL,   -- canonical tag, joins best(tag)
            target TEXT,            -- canonical Danbooru name
            PRIMARY KEY (alias, tag)
        )
```

样例：`('kagiyoshi fuuka', 'kagiyoshi_fuuka', 'kagiyoshi fuka', 'kagiyoshi_fuka')`、`('wink', 'wink', 'one eye closed', 'one_eye_closed')`。

#### `tags` —— 原始多源表（332,327 行）—— **C# 从不读取**

```sql
CREATE TABLE tags (
            tag        TEXT    NOT NULL,  -- Anima-ready (spaces, @artist)
            danbooru   TEXT    NOT NULL,  -- original Danbooru form
            cn         TEXT,              -- primary Chinese translation
            aliases    TEXT,              -- extra Chinese keys, pipe-separated
            category   TEXT,
            post_count INTEGER DEFAULT 0,
            source     TEXT,
            priority   INTEGER DEFAULT 0,
            PRIMARY KEY (tag, source)
        )
```

只是 `best` 的构建原料。**四个目标文件中没有任何一处引用 `tags` 表**——仓库全代码搜索 `FROM tags` 仅出现在外部 `build_vocab.py`。

#### 索引（照抄）

```sql
CREATE INDEX idx_b_tag   ON best(tag);
CREATE INDEX idx_b_cn    ON best(cn);
CREATE INDEX idx_b_cat   ON best(category);
CREATE INDEX idx_b_pc    ON best(post_count DESC);
CREATE INDEX idx_b_anima ON best(in_anima);
CREATE INDEX idx_ai_raw  ON anima_index(raw);
-- 另有 3 个 sqlite_autoindex_*（来自上面的 PRIMARY KEY）
```

> **这些索引与 C# 的实际访问模式无关。** `VocabDb.Load()` 只发 3 条**全表 SELECT**（`VocabDb.cs:113,139,160`），无 `WHERE`、无 `ORDER BY`，索引一个都用不上。索引是给外部 `lookup.py` / `vocab_tool.py` 那些 SQL 查询用的。

### 1.4 读取模式：全量进内存，不是查询式

`VocabDb.Load()` 一次读三张表，全部灌进 `Dictionary`：

| 源表 | SQL（行号） | 落到 |
|---|---|---|
| `best` | `SELECT tag, danbooru, cn, aliases, category, post_count, deprecated, in_anima FROM best`（`:113`） | `_byTag` / `_byDanbooru` / `_cnExact` |
| `anima_index` | `SELECT name, post_count, category FROM anima_index`（`:139`） | `_anima` / `_animaCategory` |
| `alias` | `SELECT alias, raw, tag FROM alias`（`:160`） | `_alias` |

随后 `LoadSeed` → `BuildCnKeys` → `BuildAnimaOnly`（`:176-178`）。

**实测内存规模**（net8.0 控制台连真实 DB 跑出来的值）：

```
TagCount=331,808   AnimaCount=108,257   AliasCount=62,828
SeedCount=273      CnKeyCount=327,053   AnimaOnly=12,125
```

* `TagCount=331,808` 比 `best` 的 331,836 少 28 —— 因为 `_byTag.TryAdd(tag.ToLowerInvariant(), ...)`（`:129`）按**小写**去重，有 28 个标签仅大小写不同而碰撞。
* `AliasCount=62,828` ≈ `alias` 表 33,145 行 × 2（`alias` 与 `raw` 两列都入表，`:167-168`），去重后 62,828。
* `AnimaOnly=12,125` —— `anima_index` 里有、`best` 里没有的标签数。这正是 `VocabDb.cs:51-68` 注释里那个数字，**实测吻合**。

### 1.5 `AC_VOCABDB` / `AC_SEED` 的真实用途

**它们与应用程序毫无关系。** 应用程序只读 `settings.json`：

`MainWindow.xaml.cs:386-391`：

```csharp
private void LoadVocab()
{
    try
    {
        var v = new VocabDb();
        v.Load(_cfg.VocabDbPath, _cfg.SeedPath);
```

全仓库 `GetEnvironmentVariable` 搜索结果显示，app 侧只认 `AC_TRACE` / `AC_DUMP_TREE` 两个 UI 自动化开关。

**`AC_VOCABDB` / `AC_SEED` 只被测试入口 `tests/Harness.cs:27-32` 读取：**

```csharp
string? Arg(int i) => argv.Length > i && !string.IsNullOrWhiteSpace(argv[i]) ? argv[i] : null;
string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;

var dataset = Arg(1) ?? Env("AC_DATASET");
var vocabDb = Arg(2) ?? Env("AC_VOCABDB");
var seed = Arg(3) ?? Env("AC_SEED");
```

优先级是**命令行参数 > 环境变量**。用法见 `README.md:151-154`：

```powershell
$env:AC_VOCABDB = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\vocab.sqlite"
$env:AC_SEED    = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\seed.json"
```

缺少时以**退出码 2**报错（`Harness.cs:153-158` 的 `Need()`），2 的含义是"没能验证"而非"通过"。

### 1.6 `seed.json` 是什么、干什么用

**它是人工整理的中文概念 → 规范 Anima 标签的映射表，用来补大表没有精确中文键的洞。**

文件本身（`E:\ComfyUI\anima-vocab\seed.json`）自带说明：

```json
{
  "_comment": "Chinese concept -> canonical Anima tag. Every target below was verified to exist in vocab.sqlite. Fills the gap where the bulk Chinese table has no exact key (47 of 113 common framing/pose/colour words had none). Loaded by animatag.py in addition to the vocabulary.",
  "_note_artist_prefix": "Targets are Anima-form: underscores become spaces, artist tags need '@'. Never map a scene keyword to an artist.",
  "map": {
    "一名": "1girl",
    "单人": "solo",
    "回眸": "looking back",
    "看向镜头": "looking at viewer",
    "跪坐": "sitting on own leg",
    "湿身": "wet",
    ...
  }
}
```

**实测 273 条**（注意 `README-CAPTIONING.md:162` 写的"264 curated entries"是过期数字）。

**加载逻辑**（`VocabDb.cs:203-225`）：

```csharp
private void LoadSeed(string seedPath)
{
    if (!File.Exists(seedPath))
    {
        Log.Write("seed 不存在，跳过：" + seedPath);
        return;
    }
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath, Encoding.UTF8));
        if (doc.RootElement.TryGetProperty("map", out var map) &&
            map.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in map.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    _seed.TryAdd(p.Name, p.Value.GetString()!);
        }
    }
    catch (Exception ex)
    {
        Log.Write("seed 解析失败：" + ex.Message);
    }
}
```

**关键点：只读 `map` 对象，`_comment` / `_note_artist_prefix` 被忽略；文件缺失或解析失败只写日志，不抛异常。**

它参与两处（`BuildCnKeys`，`VocabDb.cs:227-245`）：

```csharp
// 与 Python 一致：cn_exact 的键 ∪ seed 的键
var set = new HashSet<string>(_cnExact.Keys, StringComparer.Ordinal);
set.UnionWith(_seed.Keys);
```

1. **并入中文键集合** → 参与 `Scan()`（DP 最优切分）与 `ResolveChinese` 的包含匹配；
2. **在 `ResolveChinese` 里享有最高优先级**（`VocabDb.cs:328-331`）：

```csharp
// 1. 人工整理的种子优先：它存在的意义正是补大表没有精确中文键的地方
if (_seed.TryGetValue(kw, out var seedTag))
    return new ResolveResult(seedTag, Status(seedTag) != "unknown" ? "seed" : "seed-unverified");
```

即 `seed-unverified` 表示"人工映射指向了一个词库里查不到的标签"——一个可诊断的信号。

---

## 2. `VocabDb` 的 public API（签名照抄）

`Core/VocabDb.cs`，`namespace AnimaCaptioner.Core`。**构造方式是 `new VocabDb()` + `Load(...)`，没有接受路径的构造函数。**

### 2.1 配套类型

```csharp
/// <summary>词库里的一行。</summary>
public sealed class VocabRow                       // :8-18  —— 字段是 public field，不是属性
{
    public string Tag = "";        // Anima 形式（空格、画师带 @）
    public string Danbooru = "";   // Danbooru 原始形式
    public string Cn = "";         // 主中文名
    public string Aliases = "";    // 额外中文键，竖线分隔（仅供搜索展示）
    public string Category = "";
    public int PostCount;
    public bool Deprecated;
    public bool InAnima;
}

public readonly record struct ScanHit(string Tag, string Status, string Key);      // :20
public readonly record struct ResolveResult(string? Tag, string Status);           // :21
```

* `ScanHit.Key` = **命中的中文原文片段**（不是标签名）；`Tag` = 规范标签；`Status` = 解析状态字符串。
* `ResolveResult.Tag` 为 `null` 表示解析失败，此时 `Status` 是失败原因（`"no-match"` / `"wrong-category"` / `"empty"` / `"unknown"`）。

```csharp
/// <summary>Anima 原生但**不在 best 表里**的标签。见 AnimaOnly 的说明。</summary>
public readonly record struct AnimaEntry(string Name, int Posts, string Category); // :51

public sealed record SearchHit(string Tag, string Cn, string Category, int PostCount,
                               bool InAnima, bool Deprecated);                     // :564

public sealed record AuditIssue(string Tag, string Status, string? Use);           // :520
```

### 2.2 计数属性

```csharp
public int AnimaOnlyCount => _animaOnly.Count;   // :71  Anima 原生但不在 best 里的标签数
public int TagCount     => _byTag.Count;         // :81
public int AnimaCount   => _anima.Count;         // :82
public int AliasCount   => _alias.Count;         // :83
public int SeedCount    => _seed.Count;          // :84
public int CnKeyCount   => _cnKeys.Length;       // :85
```

**全是只读计算属性，无 setter。** 实测值见 1.4。

### 2.3 加载与描述

```csharp
public static string Describe(string dbPath)                  // :87
public void Load(string dbPath, string seedPath)              // :94
```

**`Describe(string dbPath)`** → `string`
不加载数据，只为 UI 状态栏生成一行摘要。实测输出：`"vocab.sqlite（113.0 MB）"`。文件不存在时返回 `"词库不存在：" + dbPath`（`:89`）。

**`Load(string dbPath, string seedPath)`** → `void`
一次性全量加载。`dbPath` 不存在时**抛 `FileNotFoundException`**（`:96-97`）；`anima_index` / `alias` 缺失只记日志并继续（兼容旧版词库，`:151-154`、`:171-174`）。调用方（`MainWindow.LoadVocab`）用 try/catch 包住并把 `_vocab` 置 null。

### 2.4 解析（tag → 类别 / 状态 / 规范形式）

```csharp
public string CategoryOf(string tag)                                    // :274
public string Status(string tag)                                        // :286
public ResolveResult ResolveEnglish(string q)                           // :296
public ResolveResult ResolveChinese(string kw, double minRatio = 0.5)   // :323
public string LookupCn(string tag)                                      // :549
```

| 方法 | 返回 | 说明 |
|---|---|---|
| `CategoryOf(tag)` | 类别字符串，查不到 `""` | **Anima 索引优先**（`:280-281`）。`@` 前缀会被剥掉再查。喂给 `TagSections.Classify` |
| `Status(tag)` | `"anima"` \| `"valid"` \| `"retired"` \| `"unknown"` | `_anima` 命中即 `anima`（**压倒 Danbooru 废弃标记**）；`PostCount==0` 也是 `unknown` |
| `ResolveEnglish(q)` | `ResolveResult` | 英文/别名 → 规范标签。先剥首尾 `.,;`。**顺序：`_anima` → `_alias` → `_byTag`（含下划线改写）→ `_byDanbooru`** |
| `ResolveChinese(kw, minRatio)` | `ResolveResult` | 中文 → 标签。顺序：**seed → 精确 cn → 包含匹配**。`minRatio` 默认 0.5 是键/关键词长度比下限 |
| `LookupCn(tag)` | 中文释义，查不到 `""` | **直接查表，不扫描**，供 UI 每帧给几十行配释义（`:549` 注释） |

`Status` 状态值实测：`Status("see-through")` → `"anima"`，`CategoryOf("see-through")` → `"anima-cat6"`。

`ResolveEnglish` 的 `Status` 取值（`:296-320`）：`"empty"`、`"anima"`、`"alias"`、`"ok"`、`"underscore-fixed"`、`"no-usage"`、`"retired"`、`"unknown"`。

`ResolveChinese` 的 `Status` 取值（`:323-358`）：`"empty"`、`"seed"`、`"seed-unverified"`、`"wrong-category"`、`"exact"`、`"retired"`、`"fuzzy"`、`"no-match"`。

> **`wrong-category`**：`JunkCategories`（`:37-38`）= `{ "artist", "character", "copyright", "原神角色" }`。命中的中文键若属这些类别则**整体拒绝**——这是为了防止"10 万个角色名的中文把普通词吞掉"（实测 `少女` → 某个光之美少女角色）。

### 2.5 扫描（中文描述 → 标签，不调模型）

```csharp
public static bool IsCjk(char ch)      // :251
public List<ScanHit> Scan(string text) // :367
```

**`IsCjk(char)`** → `bool`，公开是为了让切词 `TagSearch.BuildTerms` 和扫描共用同一套范围（`:248-250` 注释）：

```csharp
public static bool IsCjk(char ch) =>
    (ch >= 0x2E80 && ch <= 0x9FFF) ||    // 部首扩展 ~ 中日韩统一表意
    (ch >= 0xF900 && ch <= 0xFAFF) ||    // 兼容表意
    (ch >= 0xFF00 && ch <= 0xFFEF);      // 全角
```

**`Scan(string text)`** → `List<ScanHit>`
**动态规划最优切分，不是贪婪最长匹配。** 评分 = `(覆盖字数, -用到的标签数)` 取最大（`:390`）：

```csharp
if (c > bc || (c == bc && k < bn))
```

`VocabDb.cs:31-33` 说明了为什么贪婪是错的：

> 贪婪在 `回头看向镜头` 上会取 `回头看`，剩下 `向镜头`，导致正确的 `看向镜头` 永远匹配不到。

### 2.6 排序与审计（静态方法）

```csharp
public static readonly string[] Ratings = TagSections.SafetyWords;   // :431

public static List<string> Arrange(IEnumerable<string> tags,
                                   IReadOnlyDictionary<string, double> prior,
                                   string? rating = null,
                                   string? artist = null);           // :433

public static bool IsStyleTrigger(string t);                         // :509
public static bool IsControlToken(string t);                         // :518
public static List<AuditIssue> AuditTags(IEnumerable<string> tags, VocabDb vocab); // :522
public static Dictionary<string, double> OrderFromDir(string dir);   // :682
```

**`Arrange(tags, prior, rating, artist)`** → 排序后的 `List<string>`
输出顺序：`[rating] + head(按段序*1000+段内序) + [artist] + artists(序数排序) + body(按 prior 升序，缺失=9.0)`，末尾大小写不敏感去重。**分类统一向 `TagSections.TryHeadSection` 要**，本类不再自带控制词表（`:415-428` 的长注释解释了这里曾经的分叉 bug）。

**`OrderFromDir(dir)`** → `Dictionary<string,double>`
从训练集目录的 `.txt` 反推"每个标签在各 caption 中的平均相对位置"（`:697`：`(double)i / tags.Count` 再取平均）。目录不存在返回空字典。

**`AuditTags(tags, vocab)`** → `List<AuditIssue>`
跳过控制词与风格触发器，先 `ResolveEnglish` 再 `ResolveChinese`。实测 `@sayori style`（风格触发器）、`best quality` / `score_7`（控制词）都不会被误报。

**`IsControlToken`** 的成员表（`:492-507`）：四个安全词 + `nsfw`、质量词、`highres`/`absurdres`、时期词、`1girl`…`6+girls`、`1boy`/`2boys`/`multiple boys`、`solo`/`solo focus`/`male focus`/`other focus`/`no humans`，外加生成出来的 `score_1`…`score_9` 与 `year 2015`…`year 2026`。

**`IsStyleTrigger`**（`:509-516`）：以 `@` 开头、以 `" style"` 结尾（小写比较）、且总长 > 7。

### 2.7 搜索（供 UI / 工具）

```csharp
public List<SearchHit> Search(string q, int limit = 60);                                   // :576
public List<SearchHit> SearchTerms(IReadOnlyList<string> terms, int minHits = 1, int limit = 60); // :640
```

**`Search(q, limit)`** —— 中英混合**整串子串匹配**。
打分（`:586-594`，越小越优先）：

| 分数 | 条件 |
|---|---|
| 0 | `tag` 完全等于 q，或 `cn` 完全等于 q |
| 1 | `tag` 以 q 开头 |
| 2 | `cn` 以 q 开头 |
| 3 | `tag` 包含 q |
| 4 | `cn` 包含 q |
| 5 | `aliases` 包含 q |

再对未命中者 `return`；`if (!inAnima && (deprecated || posts == 0)) score += 10;`（`:605`）。
排序：`分数 ↑ → PostCount ↓ → Tag 序数 ↑`，`Take(limit)`。

**扫描两个来源**：`_byTag.Values`（33 万）+ `_animaOnly`（1.2 万，`:609-613`）。后者是必须的，否则 `female focus` / `black footwear` / `topless` / `cat smile` 这类 Anima 真正要用的标签在搜索窗口里看不见（`:567-574` 注释）。

**`SearchTerms(terms, minHits, limit)`** —— **多词覆盖搜索**。
计分 = 命中词数 `n`；`minHits` 被 `Math.Clamp(minHits, 1, terms.Count)` 收敛（`:644`）。排序：`n ↓ → InAnima ↓ → PostCount ↓ → Tag 序数 ↑`。
**这是给复合查询兜底的，比 `Search` 慢一个量级**（要遍历 33 万行做十几次 `Contains`），调用方只在普通搜索落空时才调用。

### 2.8 散文词汇表

```csharp
public List<(string Cn, string En)> ProseGlossary(string text, int max = 14);  // :753
```

两个来源合并：`Scan()` 的命中 + 私有的 `PersonWords` 表（`:720-740`，18 条人称/指代词，如 `少女→girl`、`猫耳少女→catgirl`、`人鱼→mermaid`），按中文长度降序 `Take(max)`。
**只用于散文翻译，不参与标签解析**——`girl` 不是 Anima 标签，正确的标签是 `1girl`（`:716-718` 注释）。

---

## 3. `TagSearch`：`QueryDetailed` / `Hit` / `ToolLimit`

### 3.1 常量

```csharp
/// <summary>界面一次最多列这么多。再多也读不完，而每一行都要建控件。</summary>
public const int UiLimit = 200;      // :18

/// <summary>回灌给模型的条数上限。模型要的是"够选"，不是"全部"。</summary>
public const int ToolLimit = 25;     // :21
```

**`UiLimit = 200`**（界面用）、**`ToolLimit = 25`**（回灌给 LLM 用）。
界面走 `TagSearch.QueryDetailed(_vocab, q, TagSearch.UiLimit)`（`TagSearchWindow.xaml.cs:209`）；工具循环走 `TagSearch.QueryDetailed(vocab, query, TagSearch.ToolLimit)`（`TagSearchAgent.cs:155`）。

### 3.2 `Hit` 的确切定义

`TagSearch.cs:23-28`：

```csharp
public sealed record Hit(string Tag, string Cn, string Category, int PostCount,
                         bool InAnima, bool Deprecated, string Section)
{
    public bool HasCn => Cn.Length > 0;
    public string SectionTitle => TagSections.Get(Section).Title;
}
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `Tag` | `string` | **规范标签名**（Anima 形式：空格分词、画师带 `@`） |
| `Cn` | `string` | 中文翻译；无翻译时 `""`（Anima-only 标签一律为空） |
| `Category` | `string` | 词库原始类别（`general` / `artist` / `character` / `copyright` / `meta` / 中文分类 / `anima-cat6` / `anima-cat-1`） |
| `PostCount` | `int` | 引用数（帖数） |
| `InAnima` | `bool` | 是否属 Anima 自带索引 |
| `Deprecated` | `bool` | 是否被 Danbooru 废弃 |
| `Section` | `string` | **归到的大类 key**（`quality`/`meta`/`period`/`safety`/`count`/`character`/`series`/`artist`/`general`），由 `TagSections.Classify` 算出 |
| `HasCn` | 计算属性 | `Cn.Length > 0` |
| `SectionTitle` | 计算属性 | 大类的**中文标题**（如 `"一般标签"`、`"角色"`、`"画师"`） |

> `PostCount` 是"引用数/帖数"，界面上显示为 `4,218,715 帖`。`Category` 与 `Section` **不是一回事**：`Category` 是数据源原始字段，`Section` 是经过 `TagSections.Classify` 归类后的官方九段。

### 3.3 `QueryResult` 与 `splitMatch`

`TagSearch.cs:30-31`：

```csharp
/// <summary><paramref name="SplitMatch"/> 表示这次是"整串没命中、按词拆开匹配"的结果。</summary>
public sealed record QueryResult(List<Hit> Hits, bool SplitMatch);
```

**`SplitMatch` 是什么**：本次查询**整串一个都没命中**，于是把查询切成词、用覆盖兜底找到了一批"只部分相关"的标签。

它是**回灌给模型时的关键提示位** —— 见 3.5 里那段 `note:`。模型据此知道"这些只是部分相关，要挑 cn= 读起来最接近的"。

产生逻辑（`TagSearch.cs:52-96`）：

```csharp
public static QueryResult QueryDetailed(VocabDb? vocab, string q, int limit = UiLimit)
{
    if (vocab is null || limit <= 0) return new QueryResult(new List<Hit>(), false);
    var query = (q ?? "").Trim();
    if (query.Length == 0) return new QueryResult(new List<Hit>(), false);

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var outp = new List<Hit>();

    void Add(VocabDb.SearchHit h)
    {
        if (outp.Count >= limit) return;
        if (!seen.Add(h.Tag)) return;
        outp.Add(new Hit(h.Tag, h.Cn, h.Category, h.PostCount, h.InAnima, h.Deprecated,
                         TagSections.Classify(h.Tag, vocab)));
    }

    void Collect(string text)
    {
        foreach (var h in vocab.Search(text, limit)) Add(h);
    }

    Collect(query);

    var spaced = query.Replace('_', ' ');
    if (!string.Equals(spaced, query, StringComparison.Ordinal)) Collect(spaced);

    if (outp.Count > 0) return new QueryResult(outp, false);

    // 覆盖兜底：要求命中的词数过半才算数。
    var terms = BuildTerms(query);
    if (terms.Count < 2) return new QueryResult(outp, false);

    var need = Math.Max(1, (terms.Count + 1) / 2);
    foreach (var h in vocab.SearchTerms(terms, need, limit)) Add(h);

    return new QueryResult(outp, outp.Count > 0);
}
```

三件事叠加：
1. **原串查一遍**；
2. **下划线写法也查一遍**（`^_^` 这类从 Danbooru 抄来的写法词库里存的是空格形式）；
3. **只在普通搜索完全落空时**才走上限词覆盖兜底。

**门槛 `need = Math.Max(1, (terms.Count + 1) / 2)`**，即**命中词数过半**。这条门槛的来历（`:81-89` 注释）：

> 用 need=1 时，「量子计算机」会把 `icon (computing)`(639 帖)、`calculator` 这类只碰到「计算」二字组的标签排在前面…… 过半门槛让那一类查询**如实返回空**，而不是给一堆看似相关的噪声。

### 3.4 `TagSearch.BuildTerms`（切词规则）

`TagSearch.cs:110-143`：

```csharp
public static List<string> BuildTerms(string? query, int max = 16)
```

* ASCII 段按空白与标点切开，**整词保留**（`white dress` → `white`、`dress`），词内 `_` 换成空格并转小写（`:138`）；
* **CJK 连续段取二字组**（`:132`：`for (var k = 0; k + 2 <= run.Length; k++) Add(run.Substring(k, 2))`）；
* 单段 ≤2 字则整段保留；丢弃长度 < 2 的词；去重；上限 16 个。

**实测输出**：

```
BuildTerms("白色蕾丝连衣裙") → 白色 | 色蕾 | 蕾丝 | 丝连 | 连衣 | 衣裙
BuildTerms("white_dress lace") → white dress | lace
```

`色蕾`、`丝连` 是无意义组合，但匹配不到任何东西，覆盖计数会自然忽略它们（`:105-106`）。

### 3.5 `ToToolContent` 生成的文本 —— 原文照抄

`TagSearch.cs:172-205`：

```csharp
public static string ToToolContent(IReadOnlyList<Hit> hits, string query, int remaining,
                                   bool splitMatch = false)
{
    var sb = new StringBuilder();
    sb.Append("query: ").Append(query).Append('\n');

    if (hits.Count == 0)
    {
        sb.Append("no tags matched this query. Try fewer or more general words.\n");
    }
    else
    {
        if (splitMatch)
            sb.Append("note: the query as a whole matched nothing, so it was split into ")
              .Append("separate words and these tags matched most of them. They are only ")
              .Append("partly related - prefer the ones whose cn= reads closest to what ")
              .Append("was asked for.\n");
        sb.Append("matched ").Append(hits.Count)
          .Append(" tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | ")
          .Append("section=<category> | posts=<usage count>\n");
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.Append(i + 1).Append(". tag=").Append(h.Tag)
              .Append(" | cn=").Append(h.HasCn ? h.Cn : "-")
              .Append(" | section=").Append(h.SectionTitle)
              .Append(" | posts=").Append(h.PostCount.ToString("N0", CultureInfo.InvariantCulture))
              .Append('\n');
        }
    }

    sb.Append("searches remaining: ").Append(Math.Max(0, remaining));
    return sb.ToString();
}
```

#### 实测三种输出（连真实 DB 跑出来的原文）

**(A) 无命中：**

```
query: zzz
no tags matched this query. Try fewer or more general words.
searches remaining: 1
```

**(B) `splitMatch = true`（复合中文查询兜底），`TagSearch.QueryDetailed(v, "白色蕾丝连衣裙", 3)`：**

```
query: 白色蕾丝连衣裙
note: the query as a whole matched nothing, so it was split into separate words and these tags matched most of them. They are only partly related - prefer the ones whose cn= reads closest to what was asked for.
matched 3 tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | section=<category> | posts=<usage count>
1. tag=white dress | cn=白色连衣裙 | section=一般标签 | posts=460,878
2. tag=lace-trimmed dress | cn=蕾丝边连衣裙 | section=一般标签 | posts=5,874
3. tag=aerith gainsborough (white floral dress) | cn=爱丽丝·盖恩斯巴勒（白色花朵连衣裙） | section=角色 | posts=125
searches remaining: 1
```

**(C) 普通命中，`QueryDetailed(v, "微笑", 2)`：**

```
query: 微笑
matched 2 tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | section=<category> | posts=<usage count>
1. tag=smile | cn=微笑 | section=一般标签 | posts=4,218,715
2. tag=@samairu | cn=微笑 | section=画师 | posts=22
searches remaining: 0
```

#### 格式模板（可直接照抄）

```
query: {query}\n
[splitMatch 时插入那段 note]\n
matched {N} tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | section=<category> | posts=<usage count>\n
{1..N}. tag={Tag} | cn={Cn 或 "-"} | section={SectionTitle} | posts={PostCount:N0}\n
searches remaining: {max(0, remaining)}
```

**几个必须注意的细节：**

* **无命中时没有 `matched` 行、没有编号行**，只有一句英文提示，但结尾的 `searches remaining:` **始终存在**。
* `section=` 填的是**中文标题**（`SectionTitle`），不是 key —— 实测是 `一般标签` / `角色` / `画师`。
* `posts=` 用 `"N0"` + `InvariantCulture`，即**千分位逗号**：`460,878`。
* **`ToToolContent` 结尾不带换行符** —— 实测 `searches remaining: 2---- (end) ----` 直接粘连。
* **`cn` 为空的标签（Anima-only）填 `-`**，不是空串。
* 注释（`:157-170`）说明了为什么每字段都带 `名字=` 前缀：第一版是裸竖线分隔，结果 8B 把整行当成了标签名，回答里写成 `` `hatsune miku|初音未来（VOCALOID）` ``。

### 3.6 其余 public API

```csharp
public static List<Hit> Query(VocabDb? vocab, string q, int limit = UiLimit)   // :48  = QueryDetailed(...).Hits
public static string MetaOf(Hit h)                                             // :146
public static List<Hit> RankForUser(IEnumerable<Hit> found, int limit = 60)    // :213
public static List<string> BacktickedTags(string? answer)                      // :230
public static List<string> SuspiciousMentions(string? answer, VocabDb? vocab)  // :260
```

**`MetaOf`** 生成结果行右侧元信息，`" · "` 连接：`SectionTitle` + （`PostCount>0` ? `"4,218,715 帖"` : `"无引用"`）+ 可选 `"Anima"` + 可选 `"已废弃"`。实测：`一般标签 · 4,218,715 帖 · Anima`、`画师 · 22 帖`。

**`RankForUser`** 按 `InAnima ↓ → PostCount ↓ → Tag 序数 ↑` 排序并按 Tag 去重。
设计要点（`:207-211`）：**可点列表一律来自工具实际返回的行**，不解析模型的话——点一下必然加进去一个真实存在的标签。

**`BacktickedTags`** 用 `IndexOf('`')` 逐对提取反引号内容，**没有成对引号就返回空表**（此时不做核对，而不是乱猜）。

**`SuspiciousMentions`** 取 `BacktickedTags` 中长度 ≤64、非控制词、非风格触发器、且 `ResolveEnglish` 与 `ResolveChinese` 都返回 null 的标签。**只用来如实提示，不阻止任何操作**（`:257-258`）。

---

## 4. `TagSearchAgent.ToolDescription` 原文照抄

`Core/TagSearchAgent.cs:49-63`（含注释）：**

```csharp
/// <summary>
/// 工具说明。刻意写"每次都要先查"——和翻译那条路的"不知道才查"正好相反，
/// 因为这里的目标是**库里的真实标签名**，不是模型的知识。
///
/// 公开是因为 AI 助手（<see cref="AgentKit.SearchTool"/>）复用同一个工具，
/// 两处的说明必须逐字一致——不然同一个工具在两个入口表现不同，最难查。
/// </summary>
public const string ToolDescription =
    "Search the local Danbooru/Anima tag database. Call this FIRST, before answering, " +
    "for every question: the database is the only source of valid tag names and you " +
    "must not name tags from memory. Use SHORT queries of one or two words " +
    "(e.g. \"white dress\", \"lace\", \"shy\"), not a whole sentence: each query is " +
    "matched against tag names and their Chinese meanings. The query may be English, " +
    "Chinese, or a character/series/artist name. If a long query returns nothing, " +
    "split it into separate concepts and search them one by one.";
```

**拼接后的单行全文（519 字符）：**

```
Search the local Danbooru/Anima tag database. Call this FIRST, before answering, for every question: the database is the only source of valid tag names and you must not name tags from memory. Use SHORT queries of one or two words (e.g. "white dress", "lace", "shy"), not a whole sentence: each query is matched against tag names and their Chinese meanings. The query may be English, Chinese, or a character/series/artist name. If a long query returns nothing, split it into separate concepts and search them one by one.
```

**它是唯一真源**，被两处共用：

* `TagSearchAgent.AskAsync` → `_llm.ChatWithToolsAsync(cfg, SystemPrompt, messages, new[] { AgentKit.SearchTool }, Budget, ct)`（`:125-126`）
* `AgentKit.SearchTool`（`Core/AgentKit.cs:46-59`）：

```csharp
public static AgentTool SearchTool => new(
    SearchToolName, TagSearchAgent.ToolDescription, new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One or two words, one concept. May be English or Chinese.",
            }
        },
        ["required"] = new JsonArray { "query" },
    });
```

工具名是 `"search_tags"`（`AgentKit.cs:41`），**参数只有一个 `query`（必填）**。回归测试断言两处逐字一致（`tests/Agent.cs:65`）。

### 4.1 附带：`SystemPrompt`（同样与工具格式强耦合）

`TagSearchAgent.cs:81-99`，**1144 字符**。其中与 `ToToolContent` 格式直接对应的那条规则：

```
- Each result line looks like: 1. tag=<name> | cn=<meaning> | section=<category> " +
"| posts=<count>. The tag name is ONLY the text between \"tag=\" and the next " +
"\" | \". Copy exactly that text, never the whole line.
```

### 4.2 附带：`AskAsync` 的循环参数

```csharp
public async Task<Outcome> AskAsync(AppSettings cfg, VocabDb? vocab, string question,
                                    CancellationToken ct = default)   // :101-102

private const int MaxRounds = 3;    // :40  工具循环轮数上限
private const int Budget = 1600;    // :47  每轮 max_tokens
```

`Outcome`（`:35-37`）：

```csharp
public sealed record Outcome(bool Ok, string Answer, List<TagSearch.Hit> Found,
                             List<string> Log, List<string> Suspicious,
                             int Rounds, string Error);
```

轮数耗尽后**把已有结果拼成 digest、不带工具再问一次**（`:171-178`），因为不带工具时模型只能给答案，比"再发一次带工具的请求、求它别调了"可靠。

---

## 5. `TagSections.IsKnownTag` 与大类定义

### 5.1 `IsKnownTag` 的判据

`TagSections.cs:220-235`：

```csharp
public static bool IsKnownTag(string tag, VocabDb? vocab)
{
    if (string.IsNullOrWhiteSpace(tag)) return false;
    var t = tag.Trim();
    var low = t.ToLowerInvariant();

    if (QualityTags.Contains(low) || MetaTags.Contains(low) ||
        PeriodTags.Contains(low) || SafetyTags.Contains(low) ||
        CountTags.Contains(low) || IsScore(low) || IsYear(low))
        return true;

    // 画师标签必须带 @（官方规则），@ 开头即视为标签
    if (t.StartsWith('@')) return true;

    return vocab?.Status(t) is "anima" or "valid" or "retired";
}
```

**判定顺序（刻意如此）：**

1. 空/纯空白 → `false`；
2. **硬编码控制词表**（五张 HashSet：Quality/Meta/Period/Safety/Count）→ `true`；
3. **动态控制词**：`IsScore`（`score_` + 单个 ASCII 数字，长度恰为 7，`:248-250`）或 `IsYear`（`"year "` 前缀 + 1900–2200 整数，`:252-255`）→ `true`；
4. `@` 开头 → `true`（画师）；
5. 否则查词库：`vocab.Status(t)` ∈ `{"anima", "valid", "retired"}` → `true`；**`"unknown"` → `false`**。

**判据意义**：把标签和英文散文区分开。Anima 提示词允许两者混排，散文必须留白、不参与校验、不能染色。

**实测验证：**

| 输入 | 结果 | 命中的分支 |
|---|---|---|
| `"hatsune miku"` | `True` | 词库 `Status` = `anima`/`valid` |
| `"score_7"` | `True` | `IsScore` |
| `"year 2025"` | `True` | `IsYear` |
| `"@sayori style"` | `True` | `@` 前缀 |
| `"a girl standing there."` | `False` | 句末标点 → 判为散文 |

`TagSections.cs:216-218` 给了这个判据的实测依据：

> 实测（679 个真实标签 + 官方两段提示词）：含句末标点的标签 0 个、首字母大写的标签 0 个，所以把"句子形状"的串判成散文不会误伤真标签

**注意 `IsKnownTag` 与 `Classify` 是两套判据**：`IsKnownTag` 只回答"是不是标签"，`Classify` 回答"属于哪一段"。两者共用同一批词表数组，但 `Classify` 多了 `IsScore`/`IsYear` 之外的处理和一个 `@`→Artist 的映射。

### 5.2 大类的组织方式

**大类定义结构**（`TagSections.cs:8-14`）：

```csharp
public sealed class TagSectionInfo
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string DefaultHex { get; init; }
    public required int Index { get; init; }
}
```

**九个真大类 + 一个伪大类**（`TagSections.cs:48-72`）：

```csharp
public const string Quality   = "quality";
public const string Meta      = "meta";
public const string Period    = "period";
public const string Safety    = "safety";
public const string Count     = "count";
public const string Character = "character";
public const string Series    = "series";
public const string Artist    = "artist";
public const string General   = "general";
public const string Prose     = "prose";   // 不是大类，是「自然语言」的配色键
```

**`All` 数组 —— 顺序即官方顺序**（`:74-85`）：

```csharp
public static readonly TagSectionInfo[] All =
{
    new() { Key = Quality,   Index = 0, Title = "质量",        DefaultHex = "#E8A33D" },
    new() { Key = Meta,      Index = 1, Title = "元信息",      DefaultHex = "#B0A8B9" },
    new() { Key = Period,    Index = 2, Title = "年代",        DefaultHex = "#5FB3C4" },
    new() { Key = Safety,    Index = 3, Title = "安全",        DefaultHex = "#D9534F" },
    new() { Key = Count,     Index = 4, Title = "人物数量",    DefaultHex = "#2FA39B" },
    new() { Key = Character, Index = 5, Title = "角色",        DefaultHex = "#9B6BD6" },
    new() { Key = Series,    Index = 6, Title = "作品 / 系列",  DefaultHex = "#4A90D9" },
    new() { Key = Artist,    Index = 7, Title = "画师",        DefaultHex = "#E0607E" },
    new() { Key = General,   Index = 8, Title = "一般标签",    DefaultHex = "#7A8B99" },
};

/// <summary>设置页里的全部配色项：九个大类 + 自然语言。</summary>
public static readonly TagSectionInfo[] Colorable =
    All.Append(new TagSectionInfo
    {
        Key = Prose,
        Index = All.Length,
        Title = "自然语言（散文）",
        DefaultHex = "#8FA876",
    }).ToArray();
```

**官方依据**（`:16-42` 长注释）——官方模型卡 `Prompting → Tag order`：

```
[quality/meta/year/safety tags] [1girl/1boy/1other etc] [character]
[series] [artist] [general tags]

"Within each tag section, the tags can be in arbitrary order."
```

**为什么把官方那一个方括号拆成四段**（`:26-33`）：安全词本来就有独立校验；官方推荐前缀 `masterpiece, best quality, score_7, safe` 正是 quality → safety 子序；四类判据来源不同。

**头部段**（`:102`）：

```csharp
public static readonly string[] HeadKeys = { Quality, Meta, Period, Safety, Count };
```

这五段整体排在 `character` 之前，所以 `VocabDb.Arrange` 把它们当"头部"（`:453-458`，`段序*1000 + 段内序`）。

**控制词表 —— "唯一定义，全部是有序数组"**（`:129-202`）：

| 数组 | 条数 | 内容 |
|---|---|---|
| `QualityWords` | 7 | `masterpiece, best quality, very aesthetic, good quality, normal quality, worst quality, low quality` |
| `MetaWords` | 8 | `highres, absurdres, anime screenshot, official art, jpeg artifacts, blurry, chromatic aberration, artist name` |
| `PeriodWords` | 6 | `newest, recent, mid, early, old, oldest` |
| `SafetyWords` | 5 | `safe, sensitive, questionable, explicit, nsfw` |
| `CountWords` | 22 | `1girl`…`6+girls`, `multiple girls`, `1boy`…`6+boys`, `multiple boys`, `1other`, `2others`, `multiple others`, `solo`, `solo focus`, `male focus`, `female focus`, `other focus`, `no humans` |

另有五个派生 HashSet（`StringComparer.OrdinalIgnoreCase`，`:198-202`）和**只在 `Classify`/`TryHeadSection` 里用的段内次序**（数组下标即 `order`）。

**`TryHeadSection(tag, out section, out order)`**（`:270-308`）：头部控制词的判定 **+ 段内次序**，供自动排序。`score_N` 排在质量词之后（`QualityWords.Length + (low[6] - '1')`），`year N` 排在时期词之后（`PeriodWords.Length + (YearOf(low) - 1900)`）。

**`Classify(tag, vocab)`**（`:318-349`）→ 归到九大类之一。顺序：质量/score → 元信息 → 年代/year → 安全 → 数量 → `@`→画师 → **查词库**：

```csharp
var cat = vocab?.CategoryOf(t) ?? "";
return cat switch
{
    "character" => Character,
    "copyright" => Series,
    "artist" => Artist,
    "meta" => Meta,
    "anima-cat6" => General,     // Anima 索引里自成一类的杂项，官方没有对应段
    "anima-cat-1" => General,
    _ => CnCategoryMap.TryGetValue(cat, out var m) ? m : General,
};
```

**中文大类名 → 官方大类的映射**（`:238-246`）：

```csharp
private static readonly Dictionary<string, string> CnCategoryMap = new(StringComparer.Ordinal)
{
    ["质量"] = Quality,
    ["负面词"] = Meta,
    ["Anima控制词"] = Meta,
    ["画面风格"] = Meta,
    ["Anima风格"] = Meta,
    ["Anima构图"] = Meta,
    ["人物数量"] = Count,
};
```

**其余 public API：**

```csharp
public static TagSectionInfo Get(string key)        // :109  只认九个真大类，Prose 会落到 general
public static TagSectionInfo ColorOf(string key)    // :113  多认 Prose
public static int IndexOf(string key)               // :116
public const int ProseIndex = 99;                   // :123
public static string TitleOf(int sectionIndex)      // :126
public static string NormalizeHex(string? hex)      // :389  → "#RRGGBB" 或 null
public static List<string> Canonicalize(IEnumerable<string> tags, VocabDb? vocab)  // :362
```

实测：`Get("prose").Title` → `"一般标签"`（回落 general），而 `ColorOf("prose").Title` → `"自然语言（散文）"`、`TitleOf(99)` → `"自然语言（散文）"`。**这个差异是刻意的**，见 `:104-107` 注释。

`NormalizeHex`：接受带不带 `#`、6 位或 8 位；**8 位时丢掉前两位 alpha 取后 6 位**（`:398`：`"#" + s[^6..].ToUpperInvariant()`）。

`Canonicalize`：**只按大类做稳定排序**，大类内部原样保留。`:355-357` 注释指出"实测训练集 53/53 都是恒等变换，这条被当成回归测试"。

---

## 6. 中英翻译数据存哪里、怎么参与搜索

### 6.1 存哪里

**三处，全部在 SQLite 里：**

| 列 | 表 | 非空行数 | C# 是否使用 |
|---|---|---|---|
| `cn` | `best` | 331,836（100%） | ✅ `_cnExact` 精确键 + `SearchHit.Cn` 展示 |
| `aliases` | `best` | **839** | ✅ 仅 `Search` 的第 5 档打分与 `SearchTerms` 的 `Contains` |
| `anima_aliases` | `best` | 7,911 | ❌ **完全没用** |
| `aliases` | `anima_index` | 8,169 | ❌ **完全没用** |
| `map` | `seed.json` | 273 | ✅ 最高优先级 |

**外加** `seed.json`（JSON，非 SQLite），加载后并入 `_cnKeys`。

> **两处浪费值得指出**：`best.anima_aliases`（7,911 行）和 `anima_index.aliases`（8,169 行）里存着现成的中文/英文别名，C# 一个都没读。`VocabDb.Load()` 的 `best` SQL（`:113`）只取 8 列，`anima_index` 的 SQL（`:139`）只取 3 列。

### 6.2 中文数据怎么进搜索

**第一步：建索引（`Load` 阶段）**

`VocabDb.cs:131`（`best` 的精确中文键）：
```csharp
if (row.Cn.Length > 0) _cnExact.TryAdd(row.Cn, row);
```
—— 注意是 `TryAdd`，**先到者胜**。注释（`:108-109`）解释了为什么按 rowid 顺序读、**不**加 `ORDER BY`：加了反而改变优先级，破坏 Python 的 `setdefault` 语义。

`VocabDb.cs:167-168`（alias 表两列都入）：
```csharp
if (a.Length > 0)   _alias.TryAdd(a.ToLowerInvariant(), t);
if (raw.Length > 0) _alias.TryAdd(raw.ToLowerInvariant(), t);
```

`VocabDb.cs:227-245`（`_cnKeys` = `cn_exact` 键 ∪ `seed` 键，按长度降序）：
```csharp
private void BuildCnKeys()
{
    // 与 Python 一致：cn_exact 的键 ∪ seed 的键
    var set = new HashSet<string>(_cnExact.Keys, StringComparer.Ordinal);
    set.UnionWith(_seed.Keys);
    var arr = new string[set.Count];
    set.CopyTo(arr);
    // Python 用 key=len 降序；等长时它依赖 set 迭代顺序（不确定）。
    // 这里补一个序数升序做平局裁决，让结果可复现。
    Array.Sort(arr, (a, b) =>
    {
        var c = b.Length.CompareTo(a.Length);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    });
    _cnKeys = arr;
    _cnKeySet = new HashSet<string>(arr, StringComparer.Ordinal);
    _maxKey = 1;
    foreach (var k in arr) if (k.Length > _maxKey) _maxKey = k.Length;
}
```

**第二步：四条搜索路径用到中文**

| # | 路径 | 中文数据的作用 |
|---|---|---|
| 1 | `ResolveChinese` 精确命中 | `_seed` → `_cnExact` 直接查表（O(1)） |
| 2 | `ResolveChinese` 包含匹配 | 遍历 `_cnKeys`，只允许**词库键出现在用户关键词内**，且 `len(k)/len(kw) >= minRatio` |
| 3 | `Scan`（DP 最优切分） | `_cnKeySet` 判存在，`_maxKey` 定窗口上界 |
| 4 | `Search` / `SearchTerms` | `cn` 参与 4 档/覆盖打分；`aliases` 参与第 5 档 |

**反向匹配被封死**（`VocabDb.cs:340-344`）：
```csharp
// 3. 包含匹配，只允许「词库键在用户关键词内」。反向匹配正是
//    产生 `少女` -> 某个光之美少女角色的原因。
//    纯 ASCII 输入不可能含中文键，先挡掉，省下整轮 32 万次比较
//    （审计英文 caption 时每个标签都会走到这里）。
if (!HasCjk(kw)) return new ResolveResult(null, "no-match");
```

**两道性能护栏**：
1. `HasCjk(kw)` 短路——纯 ASCII 输入不可能是中文键，省下整轮 32 万次比较（`:257-261`）；
2. `_cnKeySet` 只建一次——`Scan` 每帧都要判存在，逐个新建会白白复制 32 万个字符串（`:76-77` 注释）。

**第三步：`minRatio` 的语义**

```csharp
public ResolveResult ResolveChinese(string kw, double minRatio = 0.5)   // :323
```
`:349`：`if ((double)k.Length / kw.Length < minRatio) continue;`
即"词库键至少要占用户关键词的一半长度"。`Scan` 调用时用默认值 0.5（`:386`）。

### 6.3 中英之间没有"翻译"这一步

值得强调：这不是一个翻译器。**中文和英文是同一行记录的两个入口**——`best.cn` 是"这个英文标签的中文叫法"，`best.tag` 是规范英文标签。搜索时中英混在同一张表里比较，不存在中→英的语言模型转换。

真正的"翻译"发生在 `ProseGlossary`（`:753`，中→英散文词汇表），那条路产出的英文单词**不参与标签解析**（`VocabDb.cs:716-718`）。

---

## 7. 依赖与耦合：能否编进纯 net8.0 项目

### 7.1 `using` 指令（四个文件，全部照抄）

| 文件 | using |
|---|---|
| `VocabDb.cs` | `System.Text`、`System.Text.Json`、`Microsoft.Data.Sqlite` |
| `TagSearch.cs` | `System.Globalization`、`System.Text` |
| `TagSearchAgent.cs` | `System.Text`、`System.Text.Json.Nodes` |
| `TagSections.cs` | `System.Globalization` |

**全部是 BCL + 一个 NuGet 包。四文件合计只用了 6 个不同的 using。**

### 7.2 是否引用 WinUI / UI 类型？—— 否

对四个文件搜 `DllImport|Windows\.|WinUI|Microsoft\.UI|System\.Windows|Registry|Crypt`，**只匹配到自己的注释文字**，无任何实际调用：

```
TagSearch.cs:13    /// 不引用任何 WinUI 类型，所以能被 net10 回归程序直接编译。
TagSections.cs:40  /// 这个文件刻意不引用任何 WinUI 类型，以便对拍/回归用的 net10 控制台程序
```

这四个文件属于仓库的 `Core/` "纯逻辑层"（`README.md:94`：`Core/ 纯逻辑层，不引用 WinUI`）。

### 7.3 实测：能。而且能跑

**不是推断 —— 我真做了两个工程并运行了。**

#### 实验 1：只把这 4 个文件编进 `net8.0`

**结果：0 警告 0 错误**，但有 3 个 `CS0246`（找不到 `AppSettings` / `LlmClient`）—— 即**缺的是同项目的兄弟类型，不是平台 API**：

```
TagSearchAgent.cs(101,41): error CS0246: 未能找到类型或命名空间名"AppSettings"
TagSearchAgent.cs(26,22):  error CS0246: 未能找到类型或命名空间名"LlmClient"
TagSearchAgent.cs(28,27):  error CS0246: 未能找到类型或命名空间名"LlmClient"
```

#### 实验 2：加上依赖闭包，编进 `net8.0` 控制台

```xml
<TargetFramework>net8.0</TargetFramework>   <!-- 无 -windows 后缀 -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
```

编译单元：`Log.cs` + `Core/{VocabDb, TagSections, TagSearch, TagSearchAgent, AppSettings, ModelProfile, LlmClient, DpapiSecret, AgentKit, CaptionFile, PromptHistory, ImageSizing, TagEscape, SafetyRating}.cs`

**结果：`已成功生成。 0 个警告 0 个错误`**

> 唯一的迭代：`AppSettings.cs:461` 引用 `PromptHistory.DefaultMaxSteps`，需要额外带上 `Core/PromptHistory.cs`。这是**普通类型依赖**，不是平台阻碍。

#### 实验 3：运行真实负载

同一份 net8.0 控制台连**真实 113 MB 词库**（`Microsoft.Data.Sqlite` 8.0.10）：

```
TagCount=331,808  AnimaCount=108,257  AliasCount=62,828
SeedCount=273     CnKeyCount=327,053  AnimaOnly=12,125
[split=True] hits=4
   white dress | cn=白色连衣裙 | sec=general/一般标签 | posts=460,878 | anima=True
   lace-trimmed dress | cn=蕾丝边连衣裙 | sec=general/一般标签 | posts=5,874  | anima=True
   ...
Describe=vocab.sqlite（113.0 MB）
```

**结论：`VocabDb` / `TagSearch` / `TagSections` 在纯 `net8.0`（非 windows）下可原样编译 + 运行，功能完整。**

工程外的官方背书：`tests/corecheck.csproj:7` 就是 `net10.0`（**刻意不带 `-windows` 后缀**），注释写着"这里编译的 Core 源码是纯 .NET，唯一的平台相关点 DpapiSecret 走 P/Invoke 而不是 Windows 专用 API 包"，并且它**直接 `Compile Include` 这四个真实源文件**（`corecheck.csproj:32,39,45,46`），不是副本。

### 7.4 所有阻碍点（逐条）

#### ⛔ 阻碍点 1：`LlmClient` → `AppSettings` → `DpapiSecret` 的 Windows P/Invoke（**唯一的真阻碍**）

**这是唯一一条会真正卡住你的。** `TagSearchAgent` 不是自足的，它需要 `LlmClient`：

`TagSearchAgent.cs:26-28`：
```csharp
private readonly LlmClient _llm;

public TagSearchAgent(LlmClient llm) => _llm = llm;
```

而 `LlmClient` 的方法签名需要 `AppSettings`（`TagSearchAgent.cs:101`），`AppSettings` 需要 `DpapiSecret`：

`Core/DpapiSecret.cs:22-31`：
```csharp
[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern bool CryptProtectData(...);

[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern bool CryptUnprotectData(...);

[DllImport("kernel32.dll")]
private static extern IntPtr LocalFree(IntPtr hMem);
```

被 `AppSettings.cs:572` 调用：
```csharp
get => _searchKey ??= DpapiSecret.TryUnprotect(SearchApiKeyProtected, SearchEntropy) ?? "";
```

**性质说明（重要，避免过度悲观）：**

* 这**不影响编译** —— `DllImport` 是标准 BCL 特性，`net8.0`（非 windows）下编译完全通过（实验 2 已证）。会触发 `CA1416` 平台兼容性分析器警告，应用工程的 `AnimaCaptioner.csproj:33` 正是用 `<NoWarn>$(NoWarn);CA1416</NoWarn>` 关掉的。
* 它只在**运行到调用点**时才会 `DllNotFoundException`。`ApiKey`（`AppSettings.cs:562-566`）和 `SearchApiKey`（`:570-580`）是**惰性求值**的（`??=`），所以只要不读这两个属性、或用 `FromJson` 解析一份不含密文的配置，Linux/macOS 上也能跑。
* **最干净的解耦办法**：`VocabDb` + `TagSearch` + `TagSections` 是**完全零阻碍**的；只有 `TagSearchAgent` 需要这个闭包。如果你只需要词库查询/搜索内核，**丢掉 `TagSearchAgent.cs` 即可，另外三个文件零依赖问题**。

#### ⚠️ 阻碍点 2：`Log` 是仓库根命名空间的类型

四个文件里 `VocabDb.cs` 用了 `Log.Write(...)`（`:153,173,180,207,223`）。
`Log.cs:3` 是 `namespace AnimaCaptioner;`（**不是** `AnimaCaptioner.Core`），靠 C# 的**外层命名空间查找**规则可见。

* 影响：`net8.0` 编译必须带上 `Log.cs`，或自己提供一个 `AnimaCaptioner.Log`。
* `Log.cs:12-13` 把日志写到 `AppContext.BaseDirectory/logs/app.log` —— 这是**运行时文件路径依赖**，只读文件系统（如容器只读根）下 `Write` 内部 `catch {}` 兜住（`:39`），不会崩。

#### ⚠️ 阻碍点 3：`TagSections.cs` 用了 `Append`（需要 `System.Linq`）

`TagSections.cs:88-95`：
```csharp
public static readonly TagSectionInfo[] Colorable =
    All.Append(new TagSectionInfo { ... }).ToArray();
```

`Append` / `ToArray` 来自 LINQ。文件里**没有 `using System.Linq;`** —— 靠 `ImplicitUsings=enable`（`AnimaCaptioner.csproj:31`）隐式引入。

* 影响：目标工程若关掉 `<ImplicitUsings>`，必须在文件或工程级补 `using System.Linq;`。同理 `VocabDb.cs:687` 用了 `.OrderBy(...)`、`:700` 用了 `.ToDictionary(...)`。
* `TagSections.cs:110,114` 用了 `FirstOrDefault`，同样依赖隐式 using。

#### ⚠️ 阻碍点 4：`required` 成员 → 要求 C# 11+

`TagSections.cs:10-13`：
```csharp
public required string Key { get; init; }
```

`required` 是 C# 11 特性。**net8.0 默认就是 C# 12**，所以不构成实际阻碍；但如果把 `<LangVersion>` 降到 10 或更低会编译失败。仓库两处都写了 `<LangVersion>latest</LangVersion>`（`AnimaCaptioner.csproj:32`、`corecheck.csproj:16`）。

#### ⚠️ 阻碍点 5：`Microsoft.Data.Sqlite` 版本

`VocabDb.cs:3` 硬依赖 `Microsoft.Data.Sqlite`。实测 **8.0.10 与 10.0.12 都能工作**（我用 8.0.10 跑通了 10.0.12 建出的库）。

* 注意：`Microsoft.Data.Sqlite` 依赖原生 `SQLitePCLRaw` bundle。**net8.0 / linux-x64 / win-x64 都有官方包**，一般不是问题；只有 AOT 或极简容器需要显式处理 native 资产。

#### ⚠️ 阻碍点 6：数据文件不在仓库里

* `README.md:202`：**"仓库里没有任何词库文件（无 `.sqlite` / `.csv` / 标签表）"**。
* `VocabDb.Load()` 在 `dbPath` 不存在时**抛 `FileNotFoundException`**（`:96-97`）。
* 默认路径是 `%LOCALAPPDATA%\...`（Windows 专有环境变量语义）。跨平台时 `Environment.SpecialFolder.LocalApplicationData` 会解析成 `~/.local/share`，但**词库文件本身必须自己准备**。
* `seedPath` 缺失**不致命**（只记日志，`:205-209`），但会损失 273 条人工映射和 110 个左右独有的中文键（实测 `CnKeyCount` 从 327,053 降）。

#### ✅ 不构成阻碍的点（澄清）

| 项 | 澄清 |
|---|---|
| WinUI / `Microsoft.WindowsAppSDK` | 四文件**零引用** |
| XAML / UI 线程 | 零引用 |
| `System.Drawing` / `System.Windows.Forms` | 零引用 |
| `Microsoft.Data.Sqlite` | MIT，跨平台，非 Windows 专有 |
| `System.Text.Json` / `System.Text.Json.Nodes` | BCL（net8.0 内置） |
| `Environment.SpecialFolder` | 跨平台 API（返回语义不同而已） |
| 文件 IO（`File` / `Directory` / `Path`） | 标准 BCL，跨平台 |

### 7.5 落地建议（若要把这四个文件搬进纯 net8.0 console/ASP.NET）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>     <!-- 不需要 -windows -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>        <!-- TagSections/VocabDb 需要 LINQ -->
    <LangVersion>latest</LangVersion>              <!-- required 成员需要 C# 11+ -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
  </ItemGroup>
</Project>
```

**最小可用集（零 Windows 依赖）：**

```
Log.cs
Core/VocabDb.cs      ← 需要 Microsoft.Data.Sqlite + Log
Core/TagSections.cs  ← 只要 System.Globalization + LINQ
Core/TagSearch.cs    ← 只要 System.Globalization/Text
```

**要加工具循环（`TagSearchAgent.cs`）则需额外带上：**

```
Core/LlmClient.cs、Core/AppSettings.cs、Core/ModelProfile.cs、
Core/DpapiSecret.cs、Core/AgentKit.cs、Core/PromptHistory.cs（AppSettings.cs:461 用）
```

—— 此时 `DpapiSecret` 的 `crypt32.dll` 成为**运行时**（非编译期）Windows 依赖。规避方式：不读 `AppSettings.ApiKey` / `SearchApiKey`，或把 `DpapiSecret` 换成一个恒返回 `null` 的桩实现。

### 7.6 顺带记录：一处文档与实现的不一致

`Core/TagSearch.cs:13` 与 `Core/TagSections.cs:40-41` 的注释都写"能被 **net10** 回归程序直接编译"，而 `tests/corecheck.csproj:7` 的 TFM 确实是 `net10.0`。**实测证明 `net8.0` 同样可以**（实验 2/3）。若要迁到更老的 TFM，唯一需注意的是 `required`（C# 11）与 `record struct`（C# 10）这两个语言版本门槛。

---

## 附录 A：关键数字一览（实测）

| 项 | 值 | 来源 |
|---|---|---|
| 词库文件大小 | 113.03 MB | `Get-Item` |
| `best` 行数 | 331,836 | `SELECT COUNT(*)` |
| `best.cn` 非空 | 331,836 (100%) | 同上 |
| 不同 `cn` 值 | 326,943 | `COUNT(DISTINCT cn)` |
| `anima_index` 行数 | 108,257 | 同上 |
| `alias` 行数 | 33,145 | 同上 |
| `tags` 行数（未被 C# 使用） | 332,327 | 同上 |
| `deprecated=1` | 3,196 | 同上 |
| `banned=1` | 547 | 同上 |
| `in_anima=1` | 96,132 | 同上 |
| Anima-only 标签 | 12,125 | `anima_index LEFT JOIN best` = 实测 `VocabDb.AnimaOnlyCount` |
| `seed.json` map 条数 | 273 | JSON 解析（文档写 264，已过期） |
| 运行时 `TagCount` | 331,808 | `VocabDb` 实测（小写去重掉 28 个） |
| 运行时 `AliasCount` | 62,828 | `VocabDb` 实测 |
| 运行时 `CnKeyCount` | 327,053 | `VocabDb` 实测 |
| `TagSearch.UiLimit` | 200 | 源码 `:18` |
| `TagSearch.ToolLimit` | 25 | 源码 `:21` |
| `TagSearchAgent.MaxRounds` | 3 | 源码 `:40` |
| `TagSearchAgent.Budget` | 1600 | 源码 `:47` |
| `ToolDescription` 长度 | 519 字符 | 运行时实测 |
| `SystemPrompt` 长度 | 1144 字符 | 运行时实测 |

## 附录 B：`ToToolContent` 可照抄的模板

```text
query: {query}
                                        ← 空行不存在；splitMatch 时此处是一整行 note:
matched {N} tag(s). Format: N. tag=<exact tag name> | cn=<Chinese meaning> | section=<category> | posts=<usage count>
1. tag={Tag} | cn={Cn|"-"} | section={SectionTitle} | posts={PostCount:N0}
2. tag={Tag} | cn={Cn|"-"} | section={SectionTitle} | posts={PostCount:N0}
...
searches remaining: {remaining}
```

无命中时替换为：

```text
query: {query}
no tags matched this query. Try fewer or more general words.
searches remaining: {remaining}
```

`splitMatch=true` 时在 `query:` 行后插入（**单行，无换行**）：

```text
note: the query as a whole matched nothing, so it was split into separate words and these tags matched most of them. They are only partly related - prefer the ones whose cn= reads closest to what was asked for.
```

---

*报告完。未修改任何文件：`git -C E:\source\repos\AnimaCaptioner status --porcelain` 输出为空；本次实验所用的临时工程与脚本已全部删除。*
