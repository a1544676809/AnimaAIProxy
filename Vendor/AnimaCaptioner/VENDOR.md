# Vendor 来源与纪律

这里的内容**原样来自** [AnimaCaptioner](https://github.com/a1544676809/AnimaCaptioner)，
不是本项目自己写的。两处共用同一份实现：改这里等于改上游，反过来上游改了也必须重新搬一次，
否则"打标软件搜得到、代理搜不到"这种最难查的不一致就会出现。

## 搬的是哪些文件、来自上游哪个提交

| 文件 | 上游路径 | 上游最后改动 |
| --- | --- | --- |
| `VocabDb.cs` | `Core/VocabDb.cs` | `d6c9216` 2026-10-26 词库：别名算精确命中；修 sailor suit 别名；seed 补咖啡厅 |
| `TagSearch.cs` | `Core/TagSearch.cs` | `2761a55` 2026-10-12 加解决方案文件，并把测试工程统一到 net10 |
| `TagSections.cs` | `Core/TagSections.cs` | `7177563` 2026-10-15 给词库里没有的标签指定类型（标签类型菜单） |
| `SafetyRating.cs` | `Core/SafetyRating.cs` | `b753a62` 2026-10-09 标签控制词拆成四段：质量 / 元信息 / 年代 / 安全 |
| `CaptionFile.cs` | `Core/CaptionFile.cs` | `fe658ef` 2026-10-06 AnimaCaptioner: Anima 训练集标注工具 |
| `Log.cs` | `Log.cs` | `fe658ef` 2026-10-06 AnimaCaptioner: Anima 训练集标注工具 |
| `seed.json` | `vocab/seed.json` | `b64c065` 2026-10-26 seed：发带 → hair ribbon、半裸 → topless |

`seed.json` 的字节与上游 `b64c065:vocab/seed.json` 完全一致（SHA256
`A5850DD001E2E3E93AA5A374A6792D91A79AC066756A46AB6ACD65E72ACE712F`，8,718 字节）。

## seed.json 为什么也要搬

它**不是生成的**：上游 `vocab/seed.json` 是人工母本（273 条"中文概念 → 标签"），
`Core/VocabBuilder.cs` 在构建词库时只是把它**复制**到库旁边，不推导、不补全。
真正生成的是 `vocab.sqlite`（118 MB，由上游 `VocabBuilder` 从 Danbooru CSV 构建）。

代理需要它，是因为词库里本来就查不到一部分中文词，全靠这张表兜底：
`银发 → silver hair`（不是 `grey hair`）、`咖啡厅 → cafe`（不是 `cafe (blue archive)`）、
`和服 → kimono`、`眨眼 → blinking` …… 少了它，中文查询会静默降级：

- `ProxyConfig.DefaultSeedPath()` 借不到 AnimaCaptioner 的配置时，回落到
  应用旁边的 `vocab/seed.json`（就是本目录这份，构建时复制到输出目录）；
- 回归的 `Vocab.ResolveSeedPath()` 同样在借不到时回落到这一份，
  于是**别人 clone 下来就能跑出同样的标签结论**，而不需要先装一遍打标软件。

## 怎么更新

```powershell
# 1) 拉上游
git -C ..\..\AnimaCaptioner pull

# 2) 搬文件（只有内容真的变了才需要动，别为了刷时间戳重搬）
Copy-Item ..\..\AnimaCaptioner\Core\VocabDb.cs      .
Copy-Item ..\..\AnimaCaptioner\Core\TagSearch.cs    .
Copy-Item ..\..\AnimaCaptioner\Core\TagSections.cs  .
Copy-Item ..\..\AnimaCaptioner\Core\SafetyRating.cs .
Copy-Item ..\..\AnimaCaptioner\Core\CaptionFile.cs  .
Copy-Item ..\..\AnimaCaptioner\Log.cs               .
Copy-Item ..\..\AnimaCaptioner\vocab\seed.json      .

# 3) 记下新的上游提交，跑一遍回归
dotnet run --project ..\..\tests\proxycheck.csproj -c Debug -- all
```

**`vocab.sqlite` 不在本目录、也不进 git**：118 MB，由上游 `VocabBuilder` 从
Danbooru CSV 构建，属于"本地数据"而非"源码"。代理只借用它的路径
（优先读 AnimaCaptioner 的 `settings.json`，见 `ProxyConfig.DefaultVocabDbPath()`）。
