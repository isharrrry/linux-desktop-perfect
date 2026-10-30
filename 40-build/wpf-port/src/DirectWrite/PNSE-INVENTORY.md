# T2 · Phase 1.1 —— DirectWriteForwarder 托管面的 PNSE 清点与四分类

> 对象：`build/DirectWriteForwarder.Linux/ManagedSurface.cs`（1015 行，65 处
> `NotSupported.Throw` 文本出现 = **62 个 PNSE 成员** + `NotSupported` 帮助类的 2 个方法体 +
> 1 处 `nameof` 引用；逐个成员核对见下表）。
>
> 分类依据不是猜的，逐条来自两路只读取证：
> 1. **上游形态**：`upstream/.../DirectWriteForwarder/CPP/DWriteWrapper/*.h/.cpp`；
> 2. **调用点普查**：对 `upstream/.../PresentationCore` 全树 grep 每个成员的调用点
>    （file:line 级），结论见每行的"PC 调用点"列。
>
> 四档的判据：
> | 档 | 含义 | 处置 |
> |---|---|---|
> | **A · 布局必需** | 字体解析/匹配/度量/字形索引/表访问。不实现就一个字都排不出来 | ✅ 已实现并接线 → WIRING.md §3 |
> | **B · 绘制必需** | 把字形摆到位并取轮廓所必需（字形度量） | ✅ 已实现并接线 → WIRING.md §3 |
> | **C · 可降级** | PC 根本不调用，或只在罕见路径（XPS/元数据/显式 Uri 构造）上调用 | ✅ 已实现并接线；即使不接也不阻塞出字 |
> | **D · 本轮不做** | 需要 shaping 引擎（HarfBuzz 级）/ COM 回调 / TrueType 子集化器 | 保持 PNSE，理由见 REPORT.md 降级清单 |

---

## 0. 计数表（接线前 → 接线后）

> **接线已执行**（主控 2026-09-10 授权 T2 直接改 `build/DirectWriteForwarder.Linux/`）。
> 下表两列分别是"清点时"与"接线后实测"的状态，后者的数字来自
> `grep -c "NotSupported.Throw" build/DirectWriteForwarder.Linux/ManagedSurface.cs` = **15**。

| 档 | 条数 | 占比 | 接线前 | **接线后（实测）** |
|---|---|---|---|---|
| **A · 布局必需** | **23** | 37% | ⛔ 全部 PNSE | ✅ **23/23 委托给 provider** |
| **B · 绘制必需** | **2** | 3% | ⛔ 全部 PNSE | ✅ **2/2 委托给 provider** |
| **C · 可降级** | **22** | 35% | ⛔ 全部 PNSE | ✅ **22/22 落地**（20 条委托 + 2 条英文兜底常量） |
| **D · 本轮不做** | **15** | 24% | ⛔ PNSE | ⛔ **15/15 仍为 PNSE**（文案已细化指向降级条目） |
| 合计 | **62** | 100% | 0 条可落地 | **47 条已落地 / 62 = 75.8%** |

```
23 (A) + 2 (B) + 22 (C) + 15 (D) = 62  ✓
残留 PNSE = 15（全部 D 档）；A/B/C 档残留 = 0
```

按类型分布：

| 类型 | PNSE 数 | A | B | C | D |
|---|---|---|---|---|---|
| `FontFace` | 12 | 6 | 2 | 4 | 0 |
| `FontFile` | 2 | 2 | 0 | 0 | 0 |
| `Font` | 13 | 8 | 0 | 5 | 0 |
| `FontList` | 4 | 2 | 0 | 2 | 0 |
| `FontFamily` | 8 | 3 | 0 | 5 | 0 |
| `FontCollection` | 5 | 1 | 0 | 4 | 0 |
| `LocalizedErrorMsgs` | 2 | 0 | 0 | 2 | 0 |
| `InternalFactory` | 1 | 1 | 0 | 0 | 0 |
| `TextAnalyzer` | 6 | 3 | 0 | 0 | 3 |
| `FontFileLoader` | 2 | 0 | 0 | 0 | 2 |
| `FontFileStream` | 4 | 0 | 0 | 0 | 4 |
| `FontFileEnumerator` | 2 | 0 | 0 | 0 | 2 |
| `TrueTypeSubsetter` | 1 | 0 | 0 | 0 | 1 |
| **合计** | **62** | **23** | **2** | **22** | **15** |

---

## A · 布局必需（23）

| # | 成员（骨架行号） | 上游形态 | PC 调用点（file:line） | provider 落点 |
|---|---|---|---|---|
| 1 | `FontFace.Type`（482） | FontFace.h `Type::get()` → `IDWriteFontFace::GetType` | FontFaceLayoutInfo.cs:361,365 | `LinuxFontFace.Type`（SFNT 标签 + CFF 表） |
| 2 | `FontFace.Index`（483） | FontFace.h `Index::get()` → `GetIndex` | GlyphTypeface.cs:89,1558 | `LinuxFontFace.FaceIndex`（TTC 下标） |
| 3 | `FontFace.GlyphCount`（489） | FontFace.h → `GetGlyphCount` | GlyphTypeface.cs:963,1060,1699；FontFaceLayoutInfo.cs:249 | `LinuxFontFace.GlyphCount`（maxp.numGlyphs） |
| 4 | `FontFace.GetFileZero`（490） | FontFace.cpp:47 取第 0 个文件 | GlyphTypeface.cs:83 | `LinuxFontFace.GetFileZero()` |
| 5 | `FontFace.GetArrayOfGlyphIndices`（499） | FontFace.cpp:175 → `GetGlyphIndices` | FontFaceLayoutInfo.cs:645,664 | `LinuxFontFace.GetArrayOfGlyphIndices`（cmap） |
| 6 | `FontFace.TryGetFontTable`（503） | FontFace.cpp:193 → `TryGetFontTable` | FontFaceLayoutInfo.cs:267,286,297,308 | `LinuxFontFace.TryGetFontTable` |
| 7 | `FontFile.Analyze`（532） | FontFile.h（`IDWriteFontFile::Analyze`） | Factory.cs:201 | `LinuxFontFile.Analyze` / `AnalyzeFile` |
| 8 | `FontFile.GetUriPath`（535） | FontFile.h → `IDWriteLocalFontFileLoader::GetFilePathFromKey` | GlyphTypeface.cs:85 | `LinuxFontFile.GetUriPath()` |
| 9 | `Font.Weight`（550） | Font.h → `IDWriteFont::GetWeight` | PhysicalFontFamily.cs:215 | `LinuxFont.Weight` |
| 10 | `Font.Stretch`（551） | Font.h → `GetStretch` | PhysicalFontFamily.cs:215 | `LinuxFont.Stretch` |
| 11 | `Font.Style`（552） | Font.h → `GetStyle` | PhysicalFontFamily.cs:215 | `LinuxFont.Style` |
| 12 | `Font.IsSymbolFont`（553） | Font.h → `IsSymbolFont` | GlyphTypeface.cs:663 | `LinuxFont.IsSymbolFont`（cmap(3,0)） |
| 13 | `Font.SimulationFlags`（555） | Font.h → `GetSimulations` | GlyphTypeface.cs:75 | `LinuxFont.SimulationFlags` |
| 14 | `Font.Metrics`（556） | Font.h → `CreateFontFace()->GetMetrics` | GlyphTypeface.cs:614,626,638,650,676,688,701,713,1601；FontFaceLayoutInfo.cs:95 | `LinuxFont.Metrics` |
| 15 | `Font.HasCharacter`（569） | Font.cpp:252 → `HasCharacter` | GlyphTypeface.cs:986；FontFaceLayoutInfo.cs:618；PhysicalFontFamily.cs:315,322,368,375 | `LinuxFont.HasCharacter` |
| 16 | `Font.GetFontFace`（561） | Font.cpp `GetFontFace()`（缓存 + **每次 AddRef**） | GlyphTypeface.cs:80,960,1057,1088,1260,1555,1696；FontFaceLayoutInfo.cs:129,246,264,358,642,661 | `LinuxFont.GetFontFace()` |
| 17 | `FontList.Count`（578） | FontList.h `Count::get()` | PhysicalFontFamily.cs:151 | `LinuxFontList.Count` |
| 18 | `FontList.GetEnumerator`（581） | FontList.h `FontsEnumerator`（含 NotStarted/ReachedEnd 语义） | PhysicalFontFamily.cs:154；FamilyCollection.cs:456,479 | `LinuxFontList.GetEnumerator` |
| 19 | `FontFamily.Metrics`（594） | FontFamily.cpp:46 → Regular 面的度量 | PhysicalFontFamily.cs:392,426 | `LinuxFontFamily.Metrics` |
| 20 | `FontFamily.DisplayMetrics`（596） | FontFamily.cpp:55 | PhysicalFontFamily.cs:397,431 | `LinuxFontFamily.DisplayMetrics` |
| 21 | `FontFamily.GetFirstMatchingFont`（598） | FontFamily.cpp:61 → `IDWriteFontFamily::GetFirstMatchingFont` | PhysicalFontFamily.cs:116 | `LinuxFontFamily.GetFirstMatchingFont` |
| 22 | `FontCollection.this[string]`（612） | FontCollection.h `default[String]` | FamilyCollection.cs:347,386 | `LinuxFontCollection[string]` |
| 23 | `InternalFactory.CreateFontFile`（672） | Factory.h `CreateFontFile` | Factory.cs:143 | `LinuxFontFile.AnalyzeFile` + 句柄表 |

## B · 绘制必需（2）

| # | 成员 | 上游形态 | PC 调用点 | provider 落点 |
|---|---|---|---|---|
| 24 | `FontFace.GetDesignGlyphMetrics`（494） | FontFace.cpp:134 → `GetDesignGlyphMetrics`（设计单位） | GlyphTypeface.cs:1068,1093（Ideal）；GlyphRun.cs:1225 ink bbox | `LinuxFontFace.GetDesignGlyphMetrics`（hmtx + loca/glyf bbox） |
| 25 | `FontFace.GetDisplayGlyphMetrics`（497） | FontFace.cpp:151 → `GetGdiCompatibleGlyphMetrics` | GlyphTypeface.cs:1073,1097（Display） | `LinuxFontFace.GetDisplayGlyphMetrics`（吸附像素网格） |

> 说明：字形**轮廓**本身不走这两个成员 —— 它走
> `Font.DWriteFontAddRef`（骨架里**不是** PNSE，返回 `(IntPtr)_font`）→
> `MilGlyphRun_GetGlyphOutline`（M7a 已用 `SKFont.GetGlyphPath` 实现）。
> 所以 B 档只有这两个"度量"成员：接线时它们决定字形 ink bbox 与 Display 模式的取整宽度。

## C · 可降级（22）

| # | 成员 | PC 调用点 | 为什么可降级 | provider 落点 |
|---|---|---|---|---|
| 26 | `FontFace.SimulationFlags`（484） | **未调用** | 只有 `Font.SimulationFlags` 被用 | `LinuxFontFace.SimulationFlags` |
| 27 | `FontFace.IsSymbolFont`（485） | **未调用** | 只有 `Font.IsSymbolFont` 被用 | `LinuxFontFace.IsSymbolFont` |
| 28 | `FontFace.Metrics`（487） | **未调用** | PC 一律走 `Font.Metrics` | `LinuxFontFace.Metrics` |
| 29 | `FontFace.ReadFontEmbeddingRights`（508） | FontFaceLayoutInfo.cs:132 | XPS/子集化路径 | `LinuxFontFace.ReadFontEmbeddingRights`（OS/2 +8） |
| 30 | `Font.Family`（549） | **未调用** | — | `LinuxFont.Family` |
| 31 | `Font.FaceNames`（554） | GlyphTypeface.cs:385；FamilyCollection.cs:459,481 | 命名面查找/元数据 | `LinuxFont.FaceNames`（name 表 id=2） |
| 32 | `Font.Version`（557） | GlyphTypeface.cs:602 | `GlyphTypeface.Version` 属性 | `LinuxFont.Version`（name id=5 → head.fontRevision） |
| 33 | `Font.DisplayMetrics`（559） | **未调用** | PC 走 `FontFamily.DisplayMetrics` | `LinuxFont.DisplayMetrics` |
| 34 | `Font.GetInformationalStrings`（566） | GlyphTypeface.cs:313,315,341,343,1631 | 版权/URL/许可元数据 | `LinuxFont.GetInformationalStrings` |
| 35 | `FontList.this[uint]`（577） | **未调用** | PC 只枚举 + Count | `LinuxFontList[int]` |
| 36 | `FontList.FontsCollection`（579） | **未调用** | — | `LinuxFontList.FontsCollection` |
| 37 | `FontFamily.FamilyNames`（590） | PhysicalFontFamily.cs:96 | 族名字典 | `LinuxFontFamily.FamilyNames` |
| 38 | `FontFamily.IsPhysical`（591） | **未调用** | — | 恒 true（只做物理族） |
| 39 | `FontFamily.IsComposite`（592） | **未调用** | 复合字体本轮不做 | 恒 false |
| 40 | `FontFamily.OrdinalName`（593） | FamilyCollection.cs:614 | 排序名 | `LinuxFontFamily.OrdinalName` |
| 41 | `FontFamily.GetMatchingFonts`（600） | **未调用** | — | `LinuxFontFamily.GetMatchingFonts` |
| 42 | `FontCollection.FamilyCount`（610） | FamilyCollection.cs:519,675 | 全族枚举 | `LinuxFontCollection.FamilyCount` |
| 43 | `FontCollection.this[uint]`（611） | FamilyCollection.cs:552 | 枚举器 Current | `LinuxFontCollection[int]` |
| 44 | `FontCollection.FindFamilyName`（614） | **未调用** | — | `LinuxFontCollection.FindFamilyName` |
| 45 | `FontCollection.GetFontFromFontFace`（619） | GlyphTypeface.cs:147 | 显式 Uri/faceIndex 构造路径 | `LinuxFontCollection.GetFontFromFontFace` |
| 46 | `LocalizedErrorMsgs.EnumeratorNotStarted`（653） | DWriteFactory.cs:25（**赋值**） | PC 只写不读；读方是骨架自己的 FontList 枚举器 | ⚠️ 建议骨架直接给英文常量 |
| 47 | `LocalizedErrorMsgs.EnumeratorReachedEnd`（659） | DWriteFactory.cs:26（赋值） | 同上 | ⚠️ 同上 |

> 第 46/47 条是**潜在的启动期陷阱**：`DWriteFactory` 的静态构造会**赋值**（赋值路径不抛），
> 但如果哪天有人先读后写就会炸。处置建议写在 WIRING.md §4（一条赋值语句，不做 provider 落点）。

## D · 本轮不做（15）

| # | 成员 | PC 调用点 | 不做的理由 |
|---|---|---|---|
| 48 | `TextAnalyzer.Itemize`（820） | TypefaceMap.cs:110 | 需要**脚本分段**（script runs）：DWrite 的 `AnalyzeScript`。Linux 侧需要 ICU/HarfBuzz 的 script 分段。本轮无 shaping 引擎 |
| 49 | `TextAnalyzer.AnalyzeExtendedAndItemize`（824） | **未直接调用**（Itemize 内部） | 同上；其本身是纯记账（摘要判定/ICharacter 位打包），但没有 Itemize 就没有输入 |
| 50 | `TextAnalyzer.ReleaseItemizationNativeResources`（829） | **未调用**（Itemize 的 finally 内部） | Linux 侧无原生资源可释放 → 应退化为 no-op，随第 48 条一起做 |
| 51 | `TextAnalyzer.GetGlyphs`（839） | LineServicesCallbacks.cs:1618 | 需要真 shaping（GSUB 连字/上下文替换/字簇）→ HarfBuzz |
| 52 | `TextAnalyzer.GetGlyphPlacements`（839→851） | LineServicesCallbacks.cs:1703 | 需要 GPOS（kerning/mark 定位）。Noto Sans 的 kern 在 GPOS 里，SkiaSharp 2.88 的 `GetKerningPairAdjustments` 只读旧式 `kern` 表（实测 `HasGetKerningPairAdjustments=False`） |
| 53 | `TextAnalyzer.GetGlyphsAndTheirPlacements`（860） | FormattedTextSymbols.cs:121 | 仅 TextTrimming 省略号路径；且是 51+52 的组合 |
| 54 | `FontFileLoader()` 无参构造（912） | **未调用** | 实际用的是 `FontFileLoader(IFontSourceFactory)`（Factory.cs:53，不抛） |
| 55 | `FontFileLoader.CreateStreamFromKey`（919） | **未调用**（仅原生 DWrite 回调） | COM 回调契约；Linux 无 DWrite 回调 |
| 56 | `FontFileStream.ReadFileFragment`（934） | **未调用**（FontSource.cs:205/412 只有 TODO 注释） | 同上 |
| 57 | `FontFileStream.ReleaseFileFragment`（936） | **未调用** | 同上 |
| 58 | `FontFileStream.GetFileSize`（940） | **未调用** | 同上 |
| 59 | `FontFileStream.GetLastWriteTime`（945） | **未调用** | 同上 |
| 60 | `FontFileEnumerator.MoveNext`（968） | **未调用**（仅接口实现） | 同上 |
| 61 | `FontFileEnumerator.GetCurrentFontFile`（974） | **未调用**（仅接口实现） | 同上 |
| 62 | `TrueTypeSubsetter.ComputeSubset`（1013） | FontDriver.cs:263 | TrueType 子集化器（XPS/IDeviceFont 序列化）。需要重写 glyf/loca/cmap 表，本轮明确不做 |

---

## 附：不在 62 条内、但同样阻塞"运行期出文字"的相邻项（**不属于 T2 的写入范围**）

这三条不在 PNSE 台账里，却是"HelloWpf 出文字"的**前置闸门**。
T2 只做清点与告警，改动属于主控/PresentationCore 一方的串行工作：

| 项 | 位置 | 症状 | 证据 |
|---|---|---|---|
| **#0-a 工厂初始化** | `PresentationCore/MS/internal/Text/TextInterface/Factory.cs:105` → `DWriteLoader.GetDWriteCreateFactoryFunctionPointer()` | Linux 上 `LoadDWrite()` 会抛 `DllNotFoundException("dwrite.dll")`（ModuleInitializer.cs:25 → UnloadDWrite 29）；即便不抛，函数指针也是 **null**，调用即崩 | `DWriteLoader.cs:19-31` |
| **#0-b 6 处原生 vtable 调用** | Factory.cs:73,84,214,281,312,328（`_factory.Value->…`） | 对镜像指针解引用 = 段错误。`CreateFontFace`（214）与 `GetSystemFontCollection`（281）正好在文本路径上 | `grep -c "_factory.Value->"` = 6 |
| **#0-c 文本分析回调** | `TextAnalyzer.Itemize` 的 4 个 `[DllImport(PresentationNative)]` 委托（`CreateTextAnalysisSource` 等） | Linux 侧无 PresentationNative → 属 D 档第 48 条的同一问题 | ManagedSurface.cs:629-641 |

> 结论：**62 条 PNSE 是必要条件，不是充分条件**。接线顺序建议见 WIRING.md §0。
