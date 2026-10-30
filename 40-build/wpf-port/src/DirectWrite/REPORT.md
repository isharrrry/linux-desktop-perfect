# T2 · DirectWriteForwarder 的 PNSE 面接到 Skia/FreeType —— 最终报告

> 任务：把 `DirectWriteForwarder`（上游 C++/CLI）托管面里 62 处
> `PlatformNotSupportedException` 占位接到真实现，使托管层的**字体度量与字形索引**
> 在 Linux 上真正可用（HelloWpf 运行期出文字的先决条件）。
>
> 状态：**Phase 1 完成 + Phase 2 接线已执行**（主控 2026-09-10 授权直接改
> `build/DirectWriteForwarder.Linux/`）。
>
> 门禁实测：**DWF 0 错 0 警 · PresentationCore 0 错 0 警 · T2 断言 84/84 通过**。
>
> 产物目录：`build/DirectWrite.Linux/`（Provider 真实现 / Probe 取证 / Tests 断言 /
> WiringSmoke 端到端冒烟 / 本报告 + PNSE-INVENTORY.md + WIRING.md）

---

## 1. PNSE 四分类计数（接线前 → 接线后）

| 档 | 条数 | 含义 | 接线前 | **接线后（实测）** |
|---|---|---|---|---|
| **A · 布局必需** | 23 | 字体解析/匹配/度量/字形索引/表访问 | 全部 PNSE | ✅ 23/23 委托 provider |
| **B · 绘制必需** | 2 | 字形度量（ink bbox / Display 取整宽度） | 全部 PNSE | ✅ 2/2 委托 provider |
| **C · 可降级** | 22 | PC 不调用或只在罕见路径（XPS/元数据/显式 Uri） | 全部 PNSE | ✅ 22/22 落地 |
| **D · 本轮不做** | 15 | shaping / COM 回调 / 子集化 | PNSE | ⛔ 15/15 仍 PNSE（文案细化） |
| **合计** | **62** | | 0 可落地 | **47 已落地 = 75.8%** |

```
23 (A) + 2 (B) + 22 (C) + 15 (D) = 62  ✓
残留 PNSE = 15（全部 D 档）；A/B/C 档残留 = 0（grep -c "NotSupported.Throw" = 15）
```

按类型：`FontFace` 12 / `Font` 13 / `FontFamily` 8 / `FontCollection` 5 / `FontList` 4 /
`FontFile` 2 / `LocalizedErrorMsgs` 2 / `InternalFactory` 1 / `TextAnalyzer` 6 /
`FontFileLoader` 2 / `FontFileStream` 4 / `FontFileEnumerator` 2 / `TrueTypeSubsetter` 1。

> 逐条成员、上游形态、PresentationCore 调用点（file:line）、provider 落点：
> **`PNSE-INVENTORY.md`**。分类不是估的：调用点是对 `upstream/.../PresentationCore`
> 全树 grep 得到的（含"从未被调用"的 22 条死面清单）。

---

## 2. Phase 1 实现清单（API → Skia/OpenType 落点）+ 实测证据

### 2.1 落点表

| API（骨架成员） | provider 落点 | 数据来源（真值通路） |
|---|---|---|
| `FontFace.Metrics` | `LinuxFontFace.Metrics` | `head.unitsPerEm` + `hhea`(asc/desc/gap) + `OS/2`(sCapHeight/sxHeight/yStrikeout*) + `post`(underline*) |
| `FontFace.Type` | `LinuxFontFace.Type` | SFNT 版本标签 + CFF 表存在性 |
| `FontFace.Index` | `LinuxFontFace.FaceIndex` | TTC 面下标（`SKTypeface.FromFile(path, index)` 探测） |
| `FontFace.GlyphCount` | `LinuxFontFace.GlyphCount` | `maxp.numGlyphs` |
| `FontFace.GetArrayOfGlyphIndices` | `LinuxFontFace.GetArrayOfGlyphIndices` | **cmap 直读**（format 4 覆盖 BMP / format 12 覆盖增补平面） |
| `FontFace.GetDesignGlyphMetrics` | `LinuxFontFace.GetDesignGlyphMetrics` | `hmtx`(lsb/advance) + `loca`+`glyf` bbox；无 vmtx 时按 DWrite 规则合成垂直度量 |
| `FontFace.GetDisplayGlyphMetrics` | `LinuxFontFace.GetDisplayGlyphMetrics` | 同上 + 吸附物理像素网格（`round(round(du·em·ppdip/upem)·upem/(em·ppdip))`） |
| `FontFace.TryGetFontTable` | `LinuxFontFace.TryGetFontTable` | `SKTypeface.TryGetTableData` → 与文件字节逐字节一致（见 §2.3） |
| `FontFace.ReadFontEmbeddingRights` | `LinuxFontFace.ReadFontEmbeddingRights` | `OS/2` 偏移 8 的 `fsType`（与上游 FontFace.cpp:228-258 同构） |
| `FontFile.Analyze` / `GetUriPath` | `LinuxFontFile` | SFNT 版本标签（`0x00010000`/`true`/`OTTO`/`ttcf`/`typ1`）+ ttc header |
| `Font.Metrics` / `DisplayMetrics` | `LinuxFont.Metrics` / `DisplayMetrics` | 同上（Display 走 `GetGdiCompatibleMetrics` 口径） |
| `Font.HasCharacter` | `LinuxFont.HasCharacter` | cmap（`.notdef` 视为没有；symbol 字体 +0xF000 二次查） |
| `Font.Version` | `LinuxFont.Version` | `name` 表 nameId=5（"Version 2.015"）→ 解析；失败回落 `head.fontRevision` |
| `Font.GetInformationalStrings` | `LinuxFont.GetInformationalStrings` | `name` 表（DWRITE id → nameId 映射） |
| `Font.FaceNames` / `Family.FamilyNames` | `LinuxFont.FaceNames` / `LinuxFontFamily.FamilyNames` | `name` 表 nameId=2 / nameId=1（本地化） |
| `Font.GetFontFace` | `LinuxFont.GetFontFace` | 托管面缓存 + **每次 AddRef**（上游 Font.cpp 语义） |
| `Font.Weight/Stretch/Style/IsSymbolFont/SimulationFlags` | `LinuxFont.*` | `OS/2`（usWeightClass/usWidthClass）+ Skia slant + cmap(3,0) |
| `Font.DWriteFontAddRef` | `LinuxFont.DWriteFontAddRef` | **句柄表令牌**（MIL 反查 SKTypeface 用） |
| `FontFace.DWriteFontFaceAddRef` | `LinuxFontFace.Token` | 同上（+AddRef） |
| `FontFamily.GetFirstMatchingFont` | `LinuxFontFamily.GetFirstMatchingFont` | 距离规则（weight→stretch→style 字典序，平局取更轻） |
| `FontFamily.Metrics/DisplayMetrics` | `LinuxFontFamily.*` | Regular 面（上游 FontFamily.cpp:46-59） |
| `FontCollection.this[string]/FamilyCount/FindFamilyName/GetFontFromFontFace` | `LinuxFontCollection.*` | 目录加载 + 族索引（确定性三条，同 M1 FontSet） |
| `InternalFactory.CreateFontFile` | `LinuxFontFile.FromPath/AnalyzeFile` | SFNT 目录解析（不经过 Skia 也能判"是不是字体"） |
| `LocalizedErrorMsgs.*` | （常量） | 英文兜底常量（去掉"先读后写就炸"的陷阱） |

**字形索引与代理对的落点细节**（T2 的核心难点）：
不采用 `SKTypeface.GetGlyphs(string)`，而是**自己解码 UTF-16 → 码点数组**，
再走 `SKFont.GetGlyphs(ReadOnlySpan<int>, Span<ushort>)`。原因见 §5 的"被否证项"。

### 2.2 断言证据（84/84 通过）

```
$ dotnet test build/DirectWrite.Linux/Tests/DirectWrite.Linux.Tests.csproj
已通过! - 失败: 0，通过: 84，已跳过: 0，总计: 84，持续时间: 8 s
```

| 用例组 | 条数 | 关键实测数字 |
|---|---|---|
| `DigestTests`（确定性） | 5 | **跨进程摘要哈希：`d8c7def143e83602355b50945561e93941f9ae99ca95c2850b9f2e2780330b1e`** —— 进程内、进程 1、进程 2 三者**完全相同**；摘要 299 行，覆盖 4 个面的度量/13 张表的哈希/512 个字形 advance/64 个字形度量/17 条语料/48 组定位 |
| `MetricsTests`（度量真值 + 三方一致） | 9 | `upem=1000 asc=1069 desc=293 gap=0 cap=714 xh=536(R)/546(B) ul=(-100,50) st=(322/328,50)`；`Baseline=1.069`、`LineSpacing=1.362`；与 **Skia 直算**逐字段相等；与**原始文件字节直读**逐字段相等 |
| `OpenTypeOracleTests`（表通路 + 全量扫描） | 8 | 4 面 × 13 表 = **52 张表与文件字节逐字节相同**；**3884 个字形**的 hmtx advance 与 Skia 差 ≤1 设计单位；**全部 65536 个 BMP 码点**的 cmap 直读 == Skia == 我们的映射；增补平面 88 个码点（U+10780 起，U+10786 是洞）；34 个空轮廓字形点名核对 |
| `GlyphIndexTests`（字形 + 边界） | 14 | 代理对 → **1 个真字形**（U+10780 → glyph 3796，2 个 UTF-16 单元）；孤立代理 → **不丢字**（`"A\uD800x"` → 3 个字形，Skia 字符串入口给 **0 个**）；未映射 CJK → 4 个 `.notdef`；**20 万字符** → 20 万个字形且两次调用逐字节一致 |
| `PlacementTests`（advance/定位） | 10 | `'H'@12px` → **2668**（= round(8.892×300)，`scalingFactor=300` 是 `TextFormatterImp.ToIdeal` 真值）；Display 吸附 → **2640**（= 11 整像素）；`RoundAdvance(2.5)=2 / 3.5=4 / -2.5=-2`（**银行家舍入**）；设计度量 7 字段与 DWRITE 布局 28 字节一致 |
| `M1ConsistencyTests`（与 M1 一致） | 9 | 见 §3 |
| `ProviderShapeTests`（形态/生命周期） | 16 | `GetFontFace()` 每次 **+1 引用**、Release 不销毁对象、多余 Release 不压负数；令牌稳定且可反查；坏文件被**记录**而不是静默丢弃；`FromBytes` 无路径时 `GetUriPath()=""` |
| `WiringTests`（接线后端到端） | 2 | 见 §4 |

### 2.3 "同源一致性自证"是怎么做的（三方独立）

要求是"我们的度量/字形结果与 Skia 直算逐项一致"，但如果只在 provider 内部对拍，
改错口径时会两边一起错、测试永远绿。所以每条结论都有**三条独立通路**：

| 通路 | 实现位置 | 说明 |
|---|---|---|
| **A** 本工程实现 | `Provider/` | 权威 = OpenType 表直读（DWrite 同源） |
| **B** 测试直接问 Skia | `Tests/*.cs`（测试文件内联） | `new SKFont(typeface, upem)` 等，**不经过 provider 任何代码** |
| **C** 测试直接解析字体文件字节 | `Tests/OpenTypeOracleTests.cs` / `WiringSmoke` | 自己按 SFNT 目录表切片，**连 Skia 都不走** |

A==B（`ProviderMetrics_EqualIndependentSkiaComputation`、`HmtxAdvances_MatchSkiaAdvancesForEveryGlyph`、
`CmapLookup_MatchesSkiaGlyphLookup_EntireBmp`）；A==C（`ProviderMetrics_EqualIndependentRawTableParsing`、
`TableBytes_FromSkia_MatchRawFileBytes_AllFaces`、`FACE_HEADTABLE_SHA_MATCH`）。

---

## 3. 与 M1 `Text/` 层的一致性验证结果

**方法**：`Tests.csproj` 用 `<Compile Include="$(RepoRoot)src/WpfGfx.Linux/Text/{TextFontDescription,GlyphRunRequest,GlyphRunLayout,FontSet}.cs" Link="M1Link/...">`
把 M1 的 4 个源文件**编进测试程序集**（只读 `src/`，不改它；编的是同一份源码，
所以 src/ 一改下次构建就跟上，不会"验证的是旧拷贝"）。
被验证的版本号（SHA-256）由 `M1LinkRevision_IsReported` 打进测试输出。

| 验证项 | 结果 |
|---|---|
| 同一族/字面对同 `TextFontDescription` 解析到**同一个文件** | ✅ 4/4 字面（用 `head`/`hmtx` 表的 SHA-256 判文件级同一性，因为 M1 只吐 SKTypeface） |
| 同族下两边的文件清单一致 | ✅ 4 个文件、名称集合相同 |
| **匹配规则**：Distance 规则 vs M1 粗体桶规则 | ✅ 在 WPF `FontWeight` 全部 **10 个枚举值 × 3 种 style = 30/30** 结论相同 |
| 匹配规则差异量化 | ✅ 差异**恰好**是 `551..599`（49 个非枚举整数）：Distance 选 Bold、M1 选 Regular（分界点 550 是 400/700 中点）；用 `BoldBucket` 规则时 **1..1000 逐值全一致** |
| 字形索引：`GlyphRunRequest.FromText` vs 我们的映射 | ✅ 10 条语料逐元素相同 |
| 排版内核：`GlyphRunLayout.Measure` 接受我们给的 advance | ✅ `TotalAdvance` 与我们的和在 1 单位内相同、`OutOfRangeGlyphs=0`、位置逐点等于"基线起点+前缀和" |
| M1 的字体度量兜底（`SKFont.GetGlyphWidths`）vs 我们的设计步进 | ✅ 200 个字形 × 5 个字号的比值一致（偏差 < 0.05 px，属 Skia 定点误差） |
| 渲染用的 SKFont 度量 vs 我们的 `FontMetrics` | ✅ ascent/descent/leading/行高一致（`size=24`，容差 3e-3） |

**结论**：两套栈对同一输入给出**相同的字形 id 与相同的 advance**，
"MIL 按托管层给的度量摆放字形"与"MIL 自己兜底问字体"这两条路不会画出两种字距。
唯一差异是 551..599 这 49 个非枚举字重（已量化并留档；WIRING.md §3.4 给了切换开关）。

---

## 4. Phase 2 接线（已执行）

### 4.0 "最后一米"三件事（主控追加授权，2026-09-10 第二轮）

| 任务 | 结果 | 证据 |
|---|---|---|
| **1. Factory 原生闸门（`#0-a`/`#0-b`）** | ✅ **已消除**（编译期替换） | PC 0 错 0 警；`LinuxFactory_WalksTheWholeNativeGateFreePath` 断言全绿 |
| **2. 令牌桥** | ✅ 装上并生效；⚠️ **跨运行时未通**（需 .so 侧导出） | `FontFaceBridge_IsInstalledAndInvoked`；档位 `ProcessLocalOnly` |
| **3. 依赖链重建 + SystemFonts** | ✅ 链重建 0/0/0；SPI ✅；`SystemFonts` 属性被 WindowsBase 运行期装配挡住 | 见 §4.4 |

#### 4.0.1 Factory：为什么选"编译期替换"，备选方案为什么不选

| 方案 | 改动面 | 可控性 | 结论 |
|---|---|---|---|
| **(选定) 编译期替换**：`Compile Remove` 上游 `Factory.cs` + 编入 `build/shims/PresentationCore.Factory.Linux.cs`（同命名空间/同类型名/同 internal 面） | 1 个新文件 + 3 行 `Compile` 项 | 每个成员一对一落到托管 provider；编译器逐签名校验；测试可端到端跑 | ✅ 采用 |
| 备选：伪造 `libdwrite.so` 的 `DWriteCreateFactory` + 假 COM vtable | 需按 dwrite.h 精确复刻 4 个接口的全部槽位顺序；每调用一次托管↔原生往返（反向 P/Invoke、生命周期、HRESULT 映射） | 槽位错位是"调到隔壁方法"级的静默错误；额外引入 .so 构建与部署 | ⛔ 否决（工作量与风险都更高，且是把已解决的问题重做一遍） |

上游那 6 处必崩的调用（实测）：`RegisterFontFileLoader(73)`、`RegisterFontCollectionLoader(84)`、`CreateFontFace(214)`、
`GetSystemFontCollection(281)`、`CreateCustomFontCollection(312)`、`CreateTextAnalyzer(328)` ——
全部依赖 `DWriteLoader.GetDWriteCreateFactoryFunctionPointer()`，而它在 Linux 上抛
`DllNotFoundException("dwrite.dll")`；即便改成 no-op，函数指针也是 **null**（`delegate*` 调用 = 进程崩溃）。
替换后这些成员的落点：

| 上游成员 | Linux 实现 |
|---|---|
| `Factory.Create` | 构造托管 Factory（无原生工厂） |
| `Factory.DWriteFactory`（`IDWriteFactory*`） | **恒 null**（唯一消费方是 D 档的 `Itemize`，不会被解引用） |
| `CreateFontFile(Uri)` | 本地文件 → `LinuxFontFile.FromPath`；非本地 → `IFontSource` → 字节 → `FromBytes` |
| `CreateFontFace(Uri, uint, FontSimulations)` | `LinuxFontFace.FromFile`（含 TTC 面下标 + 合成粗/斜体标志）；目录 → `UnauthorizedAccessException`；非字体 → `FileFormatException`（与上游一致） |
| `GetSystemFontCollection()` | `WPF_LINUX_FONT_DIR`（`:` 分隔）优先，否则 FHS/XDG 标准位置，**递归**扫描；结果缓存 |
| `GetFontCollection(Uri)` | 本地目录 → 该目录的集合；等于系统字体目录 → 复用系统集合；非本地 → 字节集合 |
| `CreateTextAnalyzer()` | 托管 `TextAnalyzer` 实例（非 null；方法属 D 档 → 抛带说明的 PNSE） |
| `IsLocalUri` | `InternalFactory.IsLocalUri` |

**烟测证据（穿过 PC 的真实 Factory 调用，非 mock）**：

```
FACTORY_CREATED=True
FACTORY_NATIVE_PTR_IS_NULL=True
FACTORY_SYSTEM_FAMILYCOUNT=1
FACTORY_SYSTEM_DIRS=directories=[.../build/fonts] existing=[.../build/fonts] files=4 families=1 warnings=0
FACTORY_CREATED_FACE_GLYPHS=3884
FACTORY_CREATED_FACE_METRICS=upem=1000 asc=1069 desc=293 gap=0 cap=714 xh=536 ul=(-100,50) st=(322,50) baseline=1.069 linespacing=1.362
FACTORY_CREATED_FILE_ANALYZE=True FACES=1 FILEKIND=DWRITE_FONT_FILE_TYPE_TRUETYPE
FACTORY_CREATED_FILE_BASENAME=NotoSans-Regular.ttf
FACTORY_ANALYZER_NONNULL=True
DTIER_FONTFILESTREAM_GETFILESIZE_THROWS=PlatformNotSupportedException   DTIER_MESSAGE_HAS_GUIDANCE=True
```

#### 4.0.2 令牌桥：装在哪、怎么证、以及一个必须说清的边界

* **装在哪**：`build/shims/PresentationCore.FontBridge.cs`（补丁 G 编入 PC），
  `[ModuleInitializer]` 在程序集加载时安装 `FontHandleTable.PathTokenAllocator`。
  **没有**碰 `Win32ShimResolver`（`SetDllImportResolver` 只能装一次，ModuleInitializer 可以有多个）。
* **为什么是"路径式"钩子**：PC 没有 SkiaSharp 引用，而跨运行时唯一可靠的标识是**字体文件路径**
  （两个运行时各有一份 Skia 绑定，传 `SkTypeface*` 需要假定底层 `libSkiaSharp.so` 是同一个实例 —— 不该赌）。
  为此 provider 新增 `FontHandleTable.PathTokenAllocator`（`(path, faceIndex, sim) → token`）。
* **断言**：`FontFaceBridge_IsInstalledAndInvoked` 断言"类型存在 + 档位 = `ProcessLocalOnly` +
  诊断串含 `registerExport=未找到`/`E_HANDLE`/缺什么导出 + 安装时 `allocatorCalls=0` 而分配后 ≥1 + 令牌非 0 且可解析"。
* **⚠️ 边界（实测，不是推测）**：T1 之后 MIL 是 **NativeAOT 共享库**（`wpfgfx_cor3.so`，
  `PublishAot=true/NativeLib=Shared`），`MilFontFaceTable` 的静态字段活在 **.so 自己的运行时**里。
  实测：
  ```
  $ nm -D wpfgfx_cor3.so | grep MilGlyphRun_GetGlyphOutline   →  T MilGlyphRun_GetGlyphOutline
  $ grep -i fontface build/MilBridge/gen/export-symbols.txt   →  （无任何字体面登记导出）
  BRIDGE_DIAGNOSTICS=... milHandle=0x... exportProbed=True registerExport=未找到 allocatorCalls=0
                     → 降级：令牌只在进程内表；MilGlyphRun_GetGlyphOutline 会返回 E_HANDLE。
                       跨运行时桥接需要 wpfgfx_cor3.so 导出 MilFontFace_RegisterFromFile
  ```
  也就是说：**托管侧装委托跨不过这条边界**（只会写进一个 .so 永远看不见的副本）。
  本文件已按"路径式导出"的契约实现调用侧与探测（拿到导出即自动升级到 `NativeAotExport` 档位），
  但**导出本身需要改 `src/WpfGfx.Linux/Interop/*` 并重建 MilBridge** —— 都在本次授权边界之外。

#### 4.0.3 重建链（任务 3）

| 顺序 | 工程 | 结果 |
|---|---|---|
| 1 | `DirectWriteForwarder.Linux` | **0 警告 0 错误** |
| 2 | `PresentationCore.Linux` | **0 警告 0 错误** |
| 3 | `PresentationFramework.Linux` | **0 警告 0 错误** |

重建后 `build/PresentationFramework.Linux/bin/Debug/DirectWriteForwarder.dll` 从
**14:58 的旧副本**（接线前）刷新为本次产物（`PresentationCore.dll` 同步刷新）——
"PF 目录里跑的是旧骨架"这个坑已消除。

#### 4.0.4 SystemFonts 复验结论（分项，不含糊）

| 环节 | 结论 | 证据 |
|---|---|---|
| M7b 的 `SystemParametersInfoW` | ✅ **已落地且可用** | 直探 `libwpfwin32.so`：`SPI_CALL_OK=True`、`SPI_LFMESSAGEFONT_FACENAME=DejaVu Sans`、`WEIGHT=400`、`CHARSET=1`（源码 `win32_misc.c:779`，`.so` 已导出该符号） |
| T2 的字体解析（face name → 族 → 度量/字形） | ✅ 可用 | §4.0.1 的 Factory 烟测 + §4.1 的骨架烟测 |
| `SystemFonts.MessageFontFamily` **属性本身** | ⚠️ **未能在探针里跑起来**：被 **WindowsBase 运行期身份冲突**挡住 | `FileLoadException 0x80131040`（请求 `WindowsBase, Version=4.0.0.1, PublicKeyToken=null`） |

**WindowsBase 冲突的完整证据链**（这是本次发现的一个**项目级**运行期阻塞，属 WindowsBase 域，T2 未越界处理）：

```
框架副本:  ~/.dotnet/shared/Microsoft.NETCore.App/10.0.11/WindowsBase.dll
           → WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
仓库副本:  build/WindowsBase.Linux/bin/Debug/WindowsBase.dll
           → WindowsBase, Version=4.0.0.1, Culture=neutral, PublicKeyToken=null
框架清单:  Microsoft.NETCore.App.deps.json 里**列有** WindowsBase.dll
探针 deps.json: **没有** WindowsBase 条目（`<Reference><HintPath>` 不产生 deps.json 条目）
```

⇒ 运行期请求 `WindowsBase 4.0.0.1/PublicKeyToken=null` 时，宿主先从**共享框架**拿到
`4.0.0.0/PublicKeyToken=31bf3856ad364e35` → 身份不匹配 → `FileLoadException`。
（这也解释了为什么"PC/PF 至今没有被真正运行过"——第一个用到 WindowsBase 的语句就会炸。）

**建议修法**（给主控/WindowsBase 域，二选一）：
1. **让托管应用通过 ProjectReference/包引用（而不是裸 `<Reference><HintPath>`）引用本仓 WindowsBase**，
   使 app 的 `deps.json` 出现 `WindowsBase/4.0.0.1` 条目 —— 宿主按"版本高者胜"会选 4.0.0.1，压过框架的 4.0.0.0。
   （成本最低，且与 PC/PF 现有的 `WpfLinux_DropFrameworkWindowsBase` 编译期处置配套。）
2. 或给本仓 WindowsBase 换一个不与框架重名的程序集名（如 `WindowsBase.Port`），
   再把各 csproj 的 HintPath 与引用名一并改掉（改动面大但彻底）。

**T2 侧的状态**：探针（`build/DirectWrite.Linux/SystemFontsProbe/`）已经就绪并会打印
"SPI 值 / 装配身份 / 每个属性的结果或异常原文"；上述任一条修好后重跑即得结论。
在那之前，`SystemFonts.MessageFontFamily` 的结论标注为 **"待 WindowsBase 运行期装配统一后复验"** ——
**没有**用默认值或假的返回值凑过。

## 4.1 Phase 2 接线总览（第一轮）

完整逐行清单（含改动前行号、原文、替换原文）见 **`WIRING.md`**。摘要：

| 改动 | 文件 | 内容 |
|---|---|---|
| 适配层 | `build/DirectWriteForwarder.Linux/ProviderAdapters.cs`（**新增**） | 数据映射（`FontMetricsData`→`FontMetrics` 等）、枚举映射、句柄反查、`NotWired` 出口 |
| 工程 | `build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj` | +1 `<Compile Include="ProviderAdapters.cs">`、+1 `<ProjectReference>` → Provider |
| 骨架 | `ManagedSurface.cs`（1015 → 1326 行） | A/B/C 档 **47 条**改为委托；D 档 15 条保留 PNSE（文案细化）；文件头补语义分档与生命周期说明 |
| 跨目录 | `build/PresentationCore.Linux/reapply-patches.py` | 新增**补丁 F**（Provider 引用）与**补丁 G**（Linux 版 Factory + 令牌桥）（幂等、可重放；已执行到 csproj） |
| `build/shims/PresentationCore.Factory.Linux.cs` | **新增**：Linux 版 Factory（消除 6 处原生 vtable 调用与 dwill 装载依赖） |
| `build/shims/PresentationCore.FontBridge.cs` | **新增**：字体面令牌桥（`[ModuleInitializer]` 安装路径式钩子 + 档位/诊断） |

**为什么要动 PresentationCore 的 csproj（补丁 F）**：DWF 的类型上现在有参数类型来自
Provider 的构造重载（`FontFile`/`FontFace`/`FontCollection`），C# 绑定
`new FontFace((Native.IDWriteFontFace*)ptr)` 时会检查**整个重载集** → 未引用即
`CS0012`（实测 4 条：`Factory.cs:162/227/285/321`）。加了引用即 0 错 0 警。
补丁写进 `reapply-patches.py` 是因为 `build/PresentationCore.Linux/*.csproj` 由
`port-lib.py` 整份重写 —— 生成物之外的改动必须有可重放的出处。

### 4.1 接线端到端实测（穿过骨架，不是 provider 自说自话）

`WiringSmoke` 反射访问**接线后的骨架**（`FontCollection.FromDirectory` →
`FontFamily` → `Font.GetFontFace` → `FontFace`），实测输出：

```
ASSEMBLY=DirectWriteForwarder 4.0.0.1
COLLECTION_FAMILYCOUNT=1         FAMILY_FOUND=True        FAMILY_MISSING_IS_NULL=True
FAMILY_ORDINALNAME=Noto Sans     FAMILY_FACECOUNT=4       FAMILY_ISPHYSICAL=True
FAMILY_METRICS=upem=1000 asc=1069 desc=293 gap=0 cap=714 xh=536 ul=(-100,50) st=(322,50) baseline=1.069 linespacing=1.362
FAMILY_DISPLAYMETRICS=upem=1000 asc=1067 desc=267 gap=0 cap=733 xh=533 ul=(-133,67) st=(333,67) baseline=1.067 linespacing=1.334
BOLD_WEIGHT=Bold                 BOLD_STYLE=Normal        BOLD_VERSION=2.015
BOLD_METRICS=... xh=546 ...      BOLD_HASCHAR_A=True      BOLD_HASCHAR_CJK=False
FACE_TYPE=TrueType               FACE_INDEX=0             FACE_GLYPHCOUNT=3884
FACE_READEMBEDDING=True FSTYPE=0x0000
FACE_HEADTABLE_OK=True  LEN=54   FACE_HEADTABLE_SHA_MATCH=True   ← 骨架给的表 == 独立切片的表
FILE_URIPATH_BASENAME=NotoSans-Bold.ttf
FACE_TOKEN_NONZERO=True  FACE_TOKEN_RESOLVES=True
FONT_TOKEN_NONZERO=True  FONT_TOKEN_RESOLVES=True
SMOKE_OK=True
```

`WiringTests` 把这 30 项全部变成断言（`WiredSkeleton_ReturnsRealFontData`），
所以"接线在运行期真的返回真值"是**可回归**的，而不是一次性的观察。

### 4.2 仍然留给主控的两件事

| 项 | 为什么不在本次范围 | 后果 |
|---|---|---|
| **§4 令牌桥接 1 行**（`FontHandleTable.TokenAllocator = MilFontFaceTable.Register;` 装进 PresentationCore 的启动点） | 需要改 PresentationCore 的启动路径，属跨模块接线 | 不装的话：骨架发出的令牌 MIL 反查不到 → `MilGlyphRun_GetGlyphOutline` 返回 `E_HANDLE`（**画不出字形轮廓**，但不会画错） |
| **`#0-a`/`#0-b`：Factory 的原生闸门** | `Factory.cs` 的 6 处 `_factory.Value->…` 与 `DWriteLoader` 的 dwrite.dll 装载属 PresentationCore 一方 | 不修的话：文本路径在**到达本报告的 47 条实现之前**就崩/抛 |

---

## 5. 降级 / 不做清单与理由

| # | 项 | 状态 | 理由（可复核） |
|---|---|---|---|
| 1 | **TrueType 子集化**（`TrueTypeSubsetter.ComputeSubset`） | ⛔ 不做 | 需要重写 `glyf`/`loca`/`cmap`/`head` 表并维护 glyph id 重映射；唯一调用点是 `FontDriver.cs:263`（XPS/`IDeviceFont` 序列化），**不在屏幕渲染路径**。Noto Sans 的 `fsType=0x0000`（无嵌入限制）说明"能不能做"不是法律问题，是工作量问题 |
| 2 | **GPOS kerning / mark 定位**（`TextAnalyzer.GetGlyphPlacements`） | ⛔ 不做 | Noto Sans 的 kern 特性在 **GPOS** 里（表清单：GDEF/GPOS/GSUB 在，没有 `kern` 表），而 SkiaSharp 2.88 的 `GetKerningPairAdjustments` 只读旧式 `kern` 表 → 实测 `HasGetKerningPairAdjustments=False`。所以本轮 advance = hmtx 步进、`glyphOffsets` 恒 0 |
| 3 | **GSUB shaping / 复杂脚本**（`TextAnalyzer.Itemize`/`GetGlyphs`） | ⛔ 不做 | 需要 shaping 引擎（HarfBuzz 级）：连字、上下文替换、字簇、脚本分段、双向文本。**关键**：简单文本路径（`SimpleTextLine.cs:935/1780 → GlyphTypeface.cs:1370 ComputeUnshapedGlyphRun → :1310 GetAdvanceWidthsUnshaped → FontFace.GetArrayOfGlyphIndices`）**不经过**这些方法（`TextCharacters.cs:236` 直接 `new ItemProps()`），所以它们不阻塞普通文本 |
| 4 | **数字替换（digit substitution）** | ⛔ 不做 | 上游由 DWrite 通过 `IDWriteNumberSubstitution*` 在 shaping 内部完成（`TextAnalyzer.cpp:372/:399` 转发对象）。没有 shaping 引擎就没有落点；`ItemProps.DigitCulture` 本轮只作为数据存在 |
| 5 | **合成粗体的度量影响** | ⚠️ 够用但**不等价** | `FontSimulations.Bold` → `SKFont.Embolden`（只影响绘制描边），而真 DWrite 的模拟粗体会让 advance **略微变宽**。斜体的 `SkewX=-0.25`（≈14°）同样是近似。已断言"标志确实落到 SKFont 上 + 度量口径不变" |
| 6 | **GDI 兼容度量的字段集合** | ⚠️ 口径是我们定的 | DWrite 对哪几个字段做像素吸附是**未公开细节**（上游源码只说 `GetGdiCompatibleGlyphMetrics` 存在）。我们统一对全部字段做同样的吸附，并断言"像素取整值不变"这一不变式。可复核的原文公式只有 `round(x·ppd)/ppd` 那一处（控制字符路径） |
| 7 | **`ItemProps.CanShapeTogether` 的指针同一性** | 📋 留档不改 | 上游比较的是 number-substitution 的**指针同一性**（ItemProps.h:111），Linux 侧无法照抄，必须换成"值同一性"（脚本/形状/替换描述符的比较）。**本轮不改代码**，因为 `ItemProps` 只在 D 档的 Itemize 路径上被真正使用 |
| 8 | **`FontList` 枚举器的异常文案** | ⚠️ 类型一致、消息不同 | 上游抛 `LocalizedErrorMsgs.EnumeratorNotStarted/ReachedEnd` 的固定文案；C# `yield` 迭代器抛 `InvalidOperationException`（类型一致）。PC 侧没有依赖该文案的调用点（调用点普查），故留作可选对齐项（WIRING.md §6） |
| 9 | **复合字体（CompositeFont / `FontFamily.IsComposite`）** | ⛔ 不做 | WPF 的复合族是托管层的另一套机制（`GlobalUserInterface.CompositeFont`）；本轮 `IsPhysical` 恒 true、`IsComposite` 恒 false，与打包字体的实际情况相符 |
| 10 | **TTC（字体集合）多面** | ⚠️ 代码有，未被真实文件覆盖 | `LinuxFontCollection` 按下标 0,1,2… 探测到 null 为止（实测 `NotoSans-*.ttf` 在 idx=1 即 null）。TTC 分支只有"合成数据 + 截断数据"的**负例**测试（`OpenTypeData_FromSfnt_RejectsTruncatedData` 覆盖 ttc header 越界），**没有真实 TTC 字体**可以用 |
| 11 | **Symbol 字体（cmap(3,0)）** | ⚠️ 代码有，未被真实文件覆盖 | Noto Sans 没有 (3,0) 子表 → `IsSymbolFont=false`。+0xF000 二次查找的分支有单元测试断言"普通字体不走偏移"，但**没有真实 symbol 字体**验证那条分支 |

### 5.1 被否证 / 中途改口的项（诚实记录）

| 项 | 我一开始的判断 | 事实 | 影响 |
|---|---|---|---|
| **`LineSpacing` 的整数除法** | 以为骨架的 `[真实现]` 成员是对的 | 上游 `FontMetrics.h:124` 有 `(double)` 强转，骨架漏了 → Noto Sans 返回 **1** 而不是 1.362 | **真 bug**（影响每一行的行高）。已报主控，主控已修；我的 provider 里也有断言（`ProviderMetrics…`/`WiringTests` 双层） |
| **Skia 的 `GetGlyphs(string)` 能否直接用** | 以为可以（M1 就是这么用的） | 孤立代理会让它**整串返回空数组**（`"A\uD800x"` → `[]`）—— 静默丢字 | 改为自己解码 UTF-16 + 码点数组入口；并把它列为本轮最重要的边界用例 |
| **Skia 的下划线/删除线符号约定** | 以为与 OpenType 表一致 | 实测**符号相反**（Skia `+100` / post `-100`；Skia `-328` / OS/2 `+328`） | 取 Skia 值时统一取负；对 DWrite 的约定（下划线负=基线下、删除线正=基线上）做了逐面断言 |
| **`skia.Embolden` 是只写属性** | 以为 getter 恒为默认值 | getter 可读（`True`） | 断言改成"标志确实生效"，但保留"度量不随模拟粗体变化"的诚实说明 |
| **`MissingGlyph` 空轮廓数量** | 以为只有 3 个（space 等） | 实测 **34 个** | 断言改为点名核对（1,2,3,98,1531,2844,3753 + 总数） |
| **增补平面码点数** | 第一次统计得到 176 | 同一张 cmap format 12 子表被 platform 0/4 与 3/10 **两条记录指向**，重复计数 → 真值 **88** | 按 subtable offset 去重；这正是"看起来对、错一倍"的典型坑 |
| **`TextAnalyzer.GetGlyphs` 的 clusterMap** | 以为有 0xFFFE 之类的簇继续哨兵 | 上游全树**没有**任何 `DWRITE_CLUSTER_MAP`/0xFFFE；同簇只用**数值重复**表达；且它是**逐字符**（长度 = textLength）而不是逐字形 | 我的 `Utf16ToGlyph` 正好就是这份无 shaping 的 clusterMap（重复值语义），并在文档里写清长度差异 |
| **`GetGlyphPlacements` 的取整口径** | 第一版写成四舍五入（AwayFromZero） | 上游用 `Math::Round`（C++/CLI → .NET `Math.Round` = **银行家舍入 ToEven**）；且 advances 用 Round、**offsets 用截断**；Ideal 分支里 `fontEmSize` 会被约掉但 `(FLOAT)` 折损保留 | 重写为逐字照抄（含 float 强转）；新增 `RoundAdvance` 直接断言 2.5→2 / 3.5→4 / -2.5→-2 |

---

## 6. "简单文本 vs 复杂文本"的最终边界说明

**这一节是给主控排优先级用的**（全部结论来自对 `upstream/.../PresentationCore` 的调用点普查）。

```
┌─ 简单文本路径（SimpleTextLine）—— 本报告已覆盖 ────────────────────────────────┐
│ TextFormatterImp.cs:230 SimpleTextLine.Create                                  │
│  → TextCharacters.cs:236  new ItemProps()          ← 不调 Itemize              │
│  → SimpleTextLine.cs:935 / :1780                                                │
│  → GlyphTypeface.cs:1370 ComputeUnshapedGlyphRun                                │
│  → GlyphTypeface.cs:1310 GetAdvanceWidthsUnshaped                               │
│  → FontFaceLayoutInfo.IntMap → FontFace.GetArrayOfGlyphIndices   ← A 档 ✅      │
│  → GlyphRun → MilGlyphRun_GetGlyphOutline（M7a 已实现，需 §4 令牌桥接）          │
│  依赖的 TextInterface 成员：Font.GetFontFace/HasCharacter/Metrics/Weight/      │
│  Stretch/Style/IsSymbolFont/SimulationFlags/DWriteFontAddRef、                 │
│  FontFace.{Type,Index,GlyphCount,GetFileZero,GetArrayOfGlyphIndices,           │
│  GetDesignGlyphMetrics,GetDisplayGlyphMetrics,TryGetFontTable,Release}、       │
│  FontFamily.{GetFirstMatchingFont,Metrics,DisplayMetrics}、                    │
│  FontCollection.{this[string],GetFontFromFontFace}、FontList.{Count,枚举}      │
│  —— 全部在 A/B/C 档，**47 条已落地**。                                          │
└───────────────────────────────────────────────────────────────────────────────┘

┌─ 复杂文本路径（FullTextLine + LineServices）—— 本轮不覆盖 ─────────────────────┐
│ TextFormatterImp.cs:241 new TextMetrics.FullTextLine                           │
│  → TextStore.cs:1266 CreateLSRunsUniformBidiLevel                              │
│  → TextCharacters.cs:172 → GlyphingCache.cs:39 → TypefaceMap.cs:68             │
│  → TypefaceMap.cs:110 TextAnalyzer.Itemize                       ← D 档 ⛔      │
│  → LineServicesCallbacks.cs:1618 TextAnalyzer.GetGlyphs           ← D 档 ⛔     │
│  → LineServicesCallbacks.cs:1703 TextAnalyzer.GetGlyphPlacements  ← D 档 ⛔     │
│  需要：脚本分段、GSUB 连字/上下文替换、GPOS kerning/mark、双向重排、数字替换     │
└───────────────────────────────────────────────────────────────────────────────┘
```

**判据（什么时候会掉进复杂路径）**：多 run（混合字体/字号/样式）、需要 bidi 或复杂脚本、
设置了 `TextRunTypographyProperties`（连字/数字样式等）、需要 TextTrimming 省略号、
或行内需要 LineServices 的断行/对齐。**相反**：单一字体、单一字号、无排版特性的
短文本（标签、按钮、HelloWpf 的 "Hello WPF"）走简单路径。

**因此**：把 §4.2 的两件事（令牌桥接、Factory 托管化）做完，简单文本即可出字；
复杂脚本要等一个独立里程碑（HarfBuzz 接入 + `ItemProps` 的值同一性改造，
以及 §5 的 #3/#4/#7）。

---

## 7. 产物与文件清单

```
build/DirectWrite.Linux/                      ← T2 的全部新增内容（只写这里 + 授权后的 DWF/PC shims）
├── REPORT.md                                 本报告
├── PNSE-INVENTORY.md                         62 条 PNSE 的四分类 + 逐条调用点证据
├── WIRING.md                                 Phase 2 逐行接线清单（已执行，保留可复算）
├── README.md                                 目录说明 + 一条命令复现全部验证
├── Directory.Build.props                     本目录三工程的共用配置（SkiaSharp 2.88.9 锁定）
├── Provider/                                 **真实现**：字体提供者（约 2400 行）
│   ├── OpenTypeFontData.cs                   SFNT 表直读（head/hhea/OS-2/post/name/cmap4/12/hmtx/loca/glyf/ttc）
│   ├── MetricsFactory.cs                     度量两条通路 + 交叉比对 + 显示吸附
│   ├── MetricModels.cs                       FontMetricsData/GlyphMetricsData/LocalizedStringsData（DWrite 形态）
│   ├── GlyphMapper.cs                        UTF-16 → 码点 → 字形（含代理对/孤立代理/clusterMap）
│   ├── GlyphPositioner.cs                    上游逐字照抄的 advance/定位公式（Ideal / Display）
│   ├── FaceSelector.cs                       匹配规则（Distance / BoldBucket 两条，可切换）
│   ├── FontModel.cs                          FontList / FontFamily / Font
│   ├── LinuxFontCollection.cs                集合加载（确定性三条 + TTC 探测 + 坏文件记账）
│   ├── LinuxFontFace.cs                      字体面（度量/字形度量/字形索引/表/合成标志）
│   ├── LinuxFontFile.cs                      文件分析（Analyze / GetUriPath / FromPath）
│   └── FontHandleTable.cs                    IntPtr 令牌 ↔ 对象（字体面 + 文件；外部钩子）
├── Probe/                                    取证：确定性摘要生成器 + 独立进程入口
├── Tests/                                    **84 条断言**（含 M1 源码链接一致性、跨进程哈希、接线冒烟）
├── WiringSmoke/                              端到端：通过**接线后的骨架 + PC 的 Linux Factory**取真值
├── SystemFontsProbe/                         端到端：SystemFonts / SPI 复验探针（含装配身份诊断）
└── artifacts/                                probe-digest.txt（三次运行一致的哈希）/ probe-summary.txt
```

授权后的改动（**不在**本目录）：

| 文件 | 改动 |
|---|---|
| `build/DirectWriteForwarder.Linux/ManagedSurface.cs` | 1015 → 1326 行；A/B/C 档 47 条接线；D 档 15 条文案细化；文件头补语义分档 + 生命周期铁律 |
| `build/DirectWriteForwarder.Linux/ProviderAdapters.cs` | **新增**（适配层：数据/枚举映射 + 句柄反查 + `NotWired` 出口） |
| `build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj` | +1 `<Compile>`、+1 `<ProjectReference>`（→ Provider） |
| `build/PresentationCore.Linux/reapply-patches.py` | **新增补丁 F**（Provider 引用，幂等可重放） |
| `build/PresentationCore.Linux/PresentationCore.Linux.csproj` | 由 `reapply-patches.py` 注入补丁 F（生成物，勿手改） |

未触碰（按约束）：`src/WpfGfx.Linux/`、`src/WpfGfx.Linux.Native/`、`build/WindowsBase.*`、
`build/PresentationFramework.Linux/`、`build/MilBridge/`、`samples/`、`docs/`、`handoff.md`、
`port-lib.py`、`tests/` 下既有工程、`upstream/`（只读；本报告的每条上游引用都是行号级证据）。

---

## 8. 复现命令（一条链）

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/links-dev/netTest/wpf-linux-20260906/wpf-linux

# 1) provider + 取证 + 断言（84/84）
dotnet build build/DirectWrite.Linux/Provider/DirectWrite.Linux.Provider.csproj -m:1
dotnet build build/DirectWrite.Linux/Probe/DirectWrite.Linux.Probe.csproj -m:1
dotnet build build/DirectWrite.Linux/WiringSmoke/DirectWrite.Linux.WiringSmoke.csproj -m:1
dotnet test  build/DirectWrite.Linux/Tests/DirectWrite.Linux.Tests.csproj -m:1

# 2) 跨进程摘要（三次必须相同）
dotnet build/DirectWrite.Linux/Probe/bin/Debug/DirectWrite.Linux.Probe.dll
dotnet build/DirectWrite.Linux/Probe/bin/Debug/DirectWrite.Linux.Probe.dll

# 3) 接线后的门禁（两个都必须 0 错 0 警）
dotnet build build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj -m:1
dotnet build build/PresentationCore.Linux/PresentationCore.Linux.csproj -m:1

# 4) 端到端（穿过骨架取真值）
dotnet build/DirectWrite.Linux/WiringSmoke/bin/Debug/DirectWrite.Linux.WiringSmoke.dll
```

预期：`84 passed`；两次探针输出同一个 `DIGEST`；两个工程都 `0 个警告 0 个错误`；
冒烟最后一行 `SMOKE_OK=True`。

---

## 8.1 第二轮（最后一米）新增的未验 / 阻塞项

| 项 | 状态 | 说明 |
|---|---|---|
| 令牌桥跨运行时 | ⛔ **未通**（已明确降级） | 需要 `wpfgfx_cor3.so` 导出 `MilFontFace_RegisterFromFile`（契约见 `PresentationCore.FontBridge.cs` 文件头）→ 需改 `src/WpfGfx.Linux/Interop/*` + 重建 MilBridge（授权边界外） |
| `SystemFonts.MessageFontFamily` | ⚠️ 待复验 | 被 WindowsBase 运行期身份冲突挡住（§4.0.4 有完整证据与两种修法） |
| 嵌入字体（`IFontSource` → 字节 → 字体面） | ⚠️ 代码有、未跑真输入 | Factory 的 `CreateFontFile/CreateFontFace` 非本地路径会走 `GetUnmanagedStream()`；没有真实嵌入字体资源可测（依赖 PC 的 `FileMapping`/资源缓存在 Linux 上的可用性） |
| `WPF_LINUX_FONT_DIR` 之外的系统字体 | ⚠️ 未验 | 默认回落到 `/usr/share/fonts`（本机 237 个 ttf）并递归扫描；未做"多目录合并"的实测断言（只验了单目录） |

## 9. 诚实说明（哪些只是"够用"，哪些仍未验）

### 9.1 已验证（有断言、有数字、可回归）

- 度量：4 个面的 10 个字段，与 Skia 直算**和**原始文件字节**双重一致**；`Baseline/LineSpacing` 公式正确。
- 字形索引：全 BMP 65536 码点三方一致；88 个增补平面码点（真代理对）；孤立代理不丢字；20 万字符长串稳定。
- advance：3884 个字形 × 全字体与 hmtx 一致；Ideal/Display 两条公式逐字照抄并可断言；银行家舍入口径可直接断言。
- 表访问：52 张表与文件字节逐字节相同（provider 层 + 接线后骨架层各一次）。
- 确定性：**跨进程**摘要哈希三次相同（`d8c7def1…330b1e`），摘要 299 行覆盖全部输出通路。
- M1 一致性：字形 id 与 advance 两条栈一致（§3 表）。
- 接线：DWF / PC 均 0 错 0 警；30 项端到端事实断言化。

### 9.2 只是"够用"（能出正确结果，但口径与 Windows 不完全等价）

| 项 | 够了的部分 | 不等价的部分 |
|---|---|---|
| Display/GDI 兼容度量 | `round(x·ppd)/ppd` 的吸附不变式成立、像素取整值正确 | DWrite 具体对哪些字段吸附**未公开**；我们统一吸附全部字段 |
| 合成粗体/斜体 | 标志落到 SKFont、绘制上能看出加粗/倾斜 | 真 DWrite 的模拟粗体会变宽 advance；Skia 的 `Embolden` 不变 |
| 竖排（`isSideways`） | 有 vmtx 时用真垂直度量、无 vmtx 时按 DWrite 规则合成、步进方向改为 +Y | 字形旋转/`vert` 特性替换需要 HarfBuzz；Noto Sans 无 vmtx，实测退回水平步进 |
| 字面匹配 | 与 M1 在全部枚举字重上一致；两条规则可切换 | 551..599 这 49 个非枚举字重两栈不同（已量化、已留档） |
| `FontList` 枚举器 | 类型与顺序语义一致 | "未开始/已结束"的**异常文案**与上游不同（类型相同） |

### 9.3 未验（必须说清楚）

| 项 | 为什么没验 | 风险 |
|---|---|---|
| **TTC（.ttc 多面字体）** | `build/fonts` 里没有 TTC 文件；只有合成数据与截断数据负例 | 多面文件上 `FaceIndex`/面枚举可能不对（代码路径存在但未跑过真文件） |
| **Symbol 字体（cmap(3,0)）** | 同上，没有真实 symbol 字体 | `+0xF000` 二次查找分支未被真文件验证 |
| **CFF/OTF 字体** | 打包字体都是 TrueType 轮廓 | `FontFace.Type` 的 CFF 分支、CFF 字形的设计度量（依赖 `hmtx`+`glyf`，CFF 没有 `glyf`！）在 CFF 字体上会退化为"无轮廓盒"→ `rsb = advance - lsb` |
| **`WiringSmoke` 未覆盖指针参数的成员** | `MethodInfo.Invoke` 无法传 `ushort*` | `GetArrayOfGlyphIndices`/`GetDesignGlyphMetrics`/`GetDisplayGlyphMetrics` 的**接线代码**只在 provider 层被断言，跨程序集调用路径未被运行期验证 |
| **`SystemFonts.MessageFontFamily` / `MessageFontWeight`** | 依赖另一 agent 的 `SystemParametersInfoW` 出参修复（**尚未落地**，实测 `src/WpfGfx.Linux.Native/` 与 `build/WindowsBase.*` 里都没有 `SystemParametersInfoW`）；且 `build/PresentationFramework.Linux/bin/Debug/` 里的 `DirectWriteForwarder.dll` 是 **14:58 的旧副本**（接线前），直接跑会验到旧骨架 | **待 SPI 修复 + 重建 PresentationFramework 后复验**。替代证据：`FAMILY_METRICS`/`FAMILY_DISPLAYMETRICS` 已证明同样的度量数值（含 `baseline`/`linespacing`）穿过骨架可用 |
| **`#0-a`/`#0-b` 修复后能否真出字** | 不属本次授权范围；`Factory` 的 6 处原生调用仍未托管化 | 出字的最后一米未被验证：需要主控完成 `Factory` 托管化 + WIRING.md §4 的一行令牌桥接 |
| **多线程/并发** | 句柄表与字体面缓存用 `ConcurrentDictionary`/锁保护，但**没有并发压力测试** | 渲染线程与布局线程同时取字体面时的正确性未验 |
| **`FileStream`/内存增长** | 20 万字符用例跑通，但没有做长时间/多字体轮换的内存曲线 | 大字体集合下的内存占用未量化 |

---

## 10. Phase 3 —— 闸门 2（`TypographyAvailabilities`）的核对与判定

> 背景：`TextBlock.MeasureOverride` 在 Linux 上回落到 **LineServices**（那 110 条 `Lo*/Fs*/Nl*`
> 未实现 ⇒ `EntryPointNotFoundException` ⇒ 退出码 134）。回落的原因是
> `Typeface.CheckFastPathNominalGlyphs`（Typeface.cs:520-562）拒绝了名义字形快路径，
> 而其中**闸门 2** 读的就是 `FontFaceLayoutInfo.TypographyAvailabilities`。

### 10.1 这份值是谁算的、读哪些表

**不在本工程的 provider 里**。provider 在这条链上供三样东西：

| 供给 | 出口 |
|---|---|
| **GSUB / GPOS / GDEF 的原始表字节** | `FontFace.TryGetFontTable`（T2 实现；实测 52 张表与文件字节逐字节相同） |
| cmap（码点 → 字形） | `FontFace.GetArrayOfGlyphIndices` |
| 字形总数 | `FontFace.GlyphCount`（= bit 数组长度 `(GlyphCount+31)>>5`） |

PC 侧算法（`FontFaceLayoutInfo.ComputeTypographyAvailabilities`，FontFaceLayoutInfo.cs:387-545
→ `OpenTypeLayout.GetComplexLanguageList`，OpenTypeLayout.cs:1018 → `LayoutEngine.GetComplexLanguageList`，
OpenTypeCommon.cs:909-1290）分三步，特性集合逐字取自 FontFaceLayoutInfo.cs:782-800：

```
step 0  fast-text 的 8 段 Unicode 范围（0x20-0x7E / 0xA1-0xFF / 0x100-0x17F / 0x180-0x24F /
        0x1E00-0x1EFF / 0x3040-0x3098 / 0x309B-0x309F / 0x30A0-0x30FF）
        → cmap → glyphBits + minGlyphId/maxGlyphId
step 1  {locl}                                → 有覆盖的 (script,langsys)：主流语言→bit8，否则→bit16
step 2  {ccmp,rlig,liga,clig,calt,kern,mark,mkmk} → 只要有一个"有覆盖" → bit4
step 3  glyphBits 全 1，查 {locl + 上面 8 个}  → 返回脚本含 'hani'(0x68616E69) → bit2，其它 → bit1
        最后 mask != 0 时补 bit1
```

"有覆盖"= 该特性引用的 lookup 里，存在一个 `IsLookupCovered(coverage ∩ glyphBits ∩ [min,max])` 为真的 lookup
（`AppendLangSys` 再按 script/langsys 归并）。

**闸门判定**（Typeface.cs:527）：
```
bit4 | bit8 命中            → 拒（"Considered too risky to optimize"）
else bit16 命中             → return MajorLanguages.Contains(cultureInfo)   ← 注意 bit16 不是无条件拒
else                        → 放行
```
> T2 的第一版实现把 bit16 也当成"无条件拒"，**已修正**并加了逐位断言
> （`FastPathRule_MatchesUpstreamGate_BitByBit`）。

### 10.2 实测值（本工程的**独立复算**，Provider/TypographyMaskEstimator.cs）

| 字体 | 掩码 | 位 | 快路径（en-US） | 触发它的证据（节选） |
|---|---|---|---|---|
| **NotoSans-Regular / Bold**（打包字体） | **29** | 1\|4\|8\|16 | ❌ 拒 | `GSUB:DFLT/dflt:ccmp→lookup[2] coverage=[19,19]`、`GSUB:DFLT/dflt:liga→lookup[41] coverage=[73,73]` |
| **DejaVu Sans** | **23** | 1\|2\|4\|16 | ❌ 拒 | `GSUB:DFLT/dflt:ccmp→lookup[3] coverage=[76,3041]`、`GSUB:arab/dflt:liga→lookup[19] coverage=[3,1395]` |
| Liberation Sans | 5 | 1\|4 | ❌ 拒 | `GPOS:DFLT/dflt:kern→lookup[0] coverage=[3,532]` |
| Liberation Mono | **0** | — | ✅ 放行 | 无命中 |
| Noto Mono | **0** | — | ✅ 放行 | 无命中 |
| **NotoSans-Regular 剥离 GSUB/GPOS** | **0** | — | ✅ 放行 | 表不存在 ⇒ 三次调用都不发生 ⇒ `None` |

**与 PC 实测的交叉验证**：主控（M7b）在 PC 上实测 **DejaVu Sans = 23**；本工程独立复算也是 **23，逐位相同** ✓
（`DejaVuSans_IndependentEstimate_MatchesPcMeasurement`）。

### 10.3 判定：**不是误报**，字体真带这些特性（M7b 的"garbage"假设被否证）

| 疑点 | 判定 | 证据 |
|---|---|---|
| DejaVu 被报 `IdeoTypographyAvailable`(2)，但它"没有 CJK/假名" | ❌ **假设不成立** | 直接解析 ScriptList：DejaVu Sans 的布局表有 **20 个脚本，含 `hani` 与 `kana`**；`GSUB:hani/dflt:ccmp`、`GPOS:hani/dflt:kern` 实测存在 |
| bit4 是不是"报多了" | ❌ 不是 | 命中特性的 coverage 表**确实覆盖 fast-text 字形**（上表证据列，字形 3/19/73/76 都落在 fast-text 区间 [3,1849]/[3,2553] 内） |
| bit16 是不是"报多了" | ❌ 不是 | `locl` 挂在 `latn/ISM`、`latn/NSM`、`latn/KSM`、`cyrl/MKD`、`cyrl/SRB` 等**非主流语言**上 |
| 表字节/传输路径有没有给错 | ❌ 没有 | tag 映射 `TTO_GSUB=0x47535542` ✓、长度 11960/85704/1146 ✓、首 4 字节 `0x00010000`/`0x00010002` ✓；PC 的读序列（header→script→langsys→feature→lookup）用严格复刻的 walker 扫过**无越界** |

**结论**：`23 / 29` 是 WPF 算法在**这些字体上的正确输出**。修闸门 1 并不能让 Noto/DejaVu 走快路径
——它们在 `latn` 上带着 `ccmp`/`liga`/`kern`（且 coverage 覆盖 Latin 字形），WPF 的设计就是"带这些特性就得走 shaping"。

> **⚠ 同时否证了我自己的一版结论**：我的第一版 coverage 读取把 Lookup 表的 `subTableCount`
> 读在 `+2`（真值 `+4`；`+2` 是 lookupFlag）、subtable 偏移读在 `+4`（真值 `+6`），于是给出
> "Noto 只有 kern、Liberation Sans 干净"的**错误**结论。修正偏移（对照 PC 权威字段
> `OpenTypeCommon.cs:1672-1676`）后：Noto 5→29、Liberation Sans 0→5。
> 当时输出里 `format=2048/65535/32252` 这类"格式号"就是信号，第一版没抓住 —— 已记在这里防回退。

### 10.4 新发现：PC 的解析在 **Noto Sans** 上会抛 `FileFormatException`（PC 侧，T2 未改）

调用链实测（stack 来自 `WiringSmoke`）：

```
FontFaceLayoutInfo.ComputeTypographyAvailabilities()   ← FontFaceLayoutInfo.cs:443（step 1 的 locl 调用）
 → OpenTypeLayout.GetComplexLanguageList               ← OpenTypeLayout.cs:1041
  → LayoutEngine.GetComplexLanguageList                ← OpenTypeCommon.cs:930
   → GSUBHeader.GetScriptList                          ← OpenTypeCommon.cs:1330
    → FontTable.GetOffset(...)                         ← OpenTypeLayout.cs:164 的边界检查 ⇒ throw FileFormatException
```

* `FileFormatException` 的**类型住在 WindowsBase 里** ⇒ 构造它的那一刻要加载 WindowsBase；
  若加载失败，表层看到的就变成 `FileLoadException 0x80131040`，**把真正的越界异常盖住了**。
* PC 自己会 catch 它：`OpenTypeLayout.GetComplexLanguageList` 的 `catch (FileFormatException)`
  → `BadFontTable` → `ComputeTypographyAvailabilities` 随即把掩码置 **`None`(0)**。
  **也就是说：若这条路径被走到，Noto Sans 的实测掩码会是 0（快路径反而放行）**，
  与本工程独立复算的 29 矛盾 ⇒ **需要复验**（本次受阻于 WindowsBase 装配，见 §10.5）。
* **可疑点（最小复现的一部分）**：Noto Sans 的 GPOS `kern` 引用了一个 **type 8（Chaining contextual
  positioning）** 的 lookup（`GPOS[0]`），而 DejaVu Sans 的 GPOS 类型集合是 `{2,4,5,6}`（无 8）
  —— 这正好解释"M7b 在 DejaVu 上量到 23（算完了）、而 Noto 抛异常"。
  最小复现：任取 Noto 的一个面，读 `FontFaceLayoutInfo.TypographyAvailabilities`。
* 归属：PC 侧（`build/PresentationCore.Linux/` 由主控安排），T2 **未改**。

### 10.5 环境阻塞（WindowsBase 运行期装配，M7b 域）

M7b 把 WindowsBase 用 **WCP 公钥公开签名**之后：

```
框架自带: ~/.dotnet/shared/Microsoft.NETCore.App/10.0.11/WindowsBase.dll
          = WindowsBase, Version=4.0.0.0, PublicKeyToken=31bf3856ad364e35
本仓产物: build/WindowsBase.Linux/bin/Debug/WindowsBase.dll
          = WindowsBase, Version=4.0.0.1, PublicKeyToken=31bf3856ad364e35   ← 同一个 PKT，只差版本
```
⇒ 运行期按 TPA 命中框架那份（4.0.0.0）→ `FileLoadException 0x80131040`。
**16:02（签名之前，PKT=null）时身份与框架不同族，app-local 那份能正常胜出** —— 我的 smoke
当时跑通了 Factory/骨架/令牌的全部取证。本次尝试过的三条路（deps.json 注入 `WindowsBase/4.0.0.1`、
`LoadFromStream` 预加载、`Resolving` 兜底）**都没能让 app-local 胜出**。
建议（择一）：① 保持 **PKT=null**；② 把本仓版本抬到框架之上并确保 app-local 生效；③ 换程序集名。
**因此本节的数字全部来自独立复算 + 与 M7b 的 DejaVu 实测交叉验证**；smoke 里如实报
`TYPOGRAPHY_MASK=UNAVAILABLE <原因>`，没有伪造。

### 10.6 候选修法与代价

| 方案 | 实测掩码 | 观感/代价 |
|---|---|---|
| **① `WPF_LINUX_UI_FONT` → 剥离 GSUB/GPOS 的 Noto Sans**（本工程提供 `FontTableStripper`） | **0 → 放行** | 字形 id/步进/cmap/度量**逐项不变**（已断言）；只丢 kerning/连字/locl —— 而**快路径本来就不做这些**，所以视觉上等价于快路径本身。需要把派生字体放进字体目录并更新 `SHA256SUMS`（主控决定） |
| ② `WPF_LINUX_UI_FONT` → **Liberation Mono / Noto Mono** | **0 → 放行** | 零字体资产改动；等宽观感 |
| ③ 换任何**比例**字体 | 不可行 | Noto / DejaVu / Liberation Sans 实测都带 bit4 |
| ④ 实现 shaping（HarfBuzz 或 LineServices） | —— | 正解，但不在本轮 |

**明确不建议**："把掩码改成 0"——那是骗闸门；本节的判据是"真有就得报，真没有就别报"。

### 10.7 新增断言（86 → 93，全绿）

| 断言 | 钉住什么 |
|---|---|
| `PackagedNotoSans_DeniesFastPath_BecauseOfRealTypographyFeatures` | 打包字体掩码含 bit4 + 联合判定为拒 + 证据可追到 `ccmp/lookup[...]` + Noto 无 `hani` 故 bit2 不置 |
| `PackagedNotoSans_HasNoHaniScript_ButHasFastTextFeaturesOnLatin` | Noto 的脚本集合与 latn 上的 required 特性 |
| `DejaVuSans_IndependentEstimate_MatchesPcMeasurement` | **== 23（与 PC 实测逐位相同）** + `hani/kana/locl` 真实存在 |
| `StrippedNoto_AllowsFastPath_AndPreservesGlyphsMetricsAndAdvances` | 剥离后 mask=0/放行；3884 个字形步进、BMP 全量 cmap、度量逐项相同；Skia 侧宽度一致 |
| `StrippedFont_FileRoundTrip_IsAValidSfnt` | 剥离产物是可解析 SFNT（含重算的 `checkSumAdjustment`） |
| `SystemFontCandidates_FastPathVerdicts_AreMeasured` | 系统字体候选（有则验，无则跳过） |
| `FastPathRule_MatchesUpstreamGate_BitByBit` | 掩码 → 结论的映射逐位（含 bit16 的文化条件语义） |

### 10.8 令牌桥（顺带）：跨运行时那半已由 T1 补上，机制就位

* T1 已按 T2 给出的契约实现了 `.so` 导出：`nm -D` 可见 `MilFontFace_RegisterFromFile@@V1.0`，
  实现见 `src/WpfGfx.Linux/Interop/MilNative.FontFace.cs:60`（严格 UTF-8、失败返回 0、绝不抛过 ABI）。
* 本工程的 `FontFaceBridge` **自动升级到 `NativeAotExport` 档位**（诊断串：
  `registerExport=找到(MilFontFace_RegisterFromFile) → 已跨运行时接通`）。
* ⚠ 但本次调用 **返回 0**（`nativeAllocations=0`）⇒ 令牌仍由进程内表发放。
  这在 `.so` 侧的失败条件里（文件不存在/非字体/路径非法…）都不成立（我传的是存在的绝对路径），
  需要 T1 侧查 `MilFontFaceTable.RegisterFromFile` 在 .so 运行时里的前置条件。
  断言已按"**机制就位**"收敛，未把这个待查项伪装成绿。

---

## 11. Phase 3 续 —— PC 实测掩码（引用形态改正后）与派生 UI 字体

### 11.1 引用形态才是 `0x80131040` 的成因（主控的反证成立）

本工程的取证程序原先用 `ProjectReference` 引 PresentationCore / DWF —— ProjectReference 只传
**项目边**，PC 内部那些 `<Reference><HintPath>` 私有依赖**不传递** ⇒ app 的 `deps.json` 里**没有**
WindowsBase ⇒ 宿主按框架清单命中 `WindowsBase 4.0.0.0/PKT=31bf…`，而请求的是
`4.0.0.1/PKT=31bf…`（本仓公开签名的身份）⇒ 版本不符 ⇒ `FileLoadException 0x80131040`。
**根因是引用形态，不是签名**（主控给的反证：HelloWpf 在签名之后照样跑起来）。
改成与 `samples/HelloWpf/HelloWpf.csproj` 同形的 HintPath 引用后：

```
deps.json 里出现:  WindowsBase/4.0.0.1        ← 关键（RAR 会把引用连同同目录依赖解析进去）
LOADED WindowsBase=WindowsBase, Version=4.0.0.1, Culture=neutral, PublicKeyToken=31bf3856ad364e35
```

**⇒ §10.5 的"必须保持 PKT=null"作废**（那是错误结论，已在此更正）；**§10.4 的
"PC 在 Noto 上抛 FileFormatException ⇒ 掩码被置 0 ⇒ 不拒"也被证伪**：在能正常解析依赖的
环境里，PC 对 Noto 算出 **21**，没有任何异常。那次 `GetOffset` 越界栈是**装配残缺环境下的假象**
（异常类型 `FileFormatException` 住在 WindowsBase 里，加载失败把现场搅乱了），
现在无法复现，以本文的干净实测为准。

### 11.2 三种配置的 **PC 实测**掩码（`WiringSmoke` 走真实 PC 代码路径）

| 配置 | 字体文件 | PC 实测 | 位 | 本工程独立复算 | 快路径 |
|---|---|---|---|---|---|
| 未剥离（`build/fonts`） | `NotoSans-Bold.ttf` | **21** (0x15) | 1\|4\|16 | **21** ✓ | ❌ 拒 |
| **派生件**（`build/fonts-ui`） | `UI-NoLayout.ttf` | **0** | — | **0** ✓ | ✅ **放行** |
| 系统字体 | `DejaVuSans.ttf` | **23** (0x17) | 1\|2\|4\|16 | **23** ✓ | ❌ 拒 |

* DejaVu 的 23 **三方一致**：主控在 PC 上实测 23、本工程走 PC 路径实测 23、本工程独立复算 23。
* Noto 的 21 = 1|4|16：**没有 bit2**（Noto 无 `hani` ✓），**也没有 bit8** —— 我第一版把
  `cyrl/dflt`/`grek/dflt` 等当成主流语言，误置了 bit8（估 29）；按上游真实的 4 条
  `majorLanguages`（latn/ENG、latn/DEU、hani/JAN、kana/JAN，FontFaceLayoutInfo.cs:981-988）
  修正后，我的复算也从 29 变 21，与 PC 逐位一致。
* **派生件实测 0 ⇒ 闸门 2 放行**，这是"剥离 GSUB/GPOS"这条修法在**真实 PC 代码路径**上的验证。

### 11.3 派生 UI 字体（主控裁定：走建议 1）

* **产物**：`build/fonts-ui/UI-NoLayout.ttf`（332736 字节，sha256 `b008d486c4e029417d070461c36324fb6f6ac7c4aeb28e810f8f3e82c3250c55`，
  11 张表 = 13 − GSUB − GPOS，upem=1000 / numGlyphs=3884 不变）。
* **生成脚本**：`build/gen-ui-font.py`（纯 Python、无第三方依赖、可复现、打印 sha256；
  文件头写明"为什么剥 GSUB/GPOS"以及"这不是绕过闸门"）。
* **⚠ 目录选择（实测后的决定，与最初设想不同）**：派生件**不放** `build/fonts/`，而是放它旁边的
  `build/fonts-ui/`。原因是实测出来的：派生件与基准件**同族同字重**，放进 `build/fonts/` 会让
  `FontSet` / `LinuxFontCollection` 的 `(族名,字重,斜体)` 索引**互相遮蔽**（后加载者覆盖先加载者）
  ⇒ "Noto Sans 400 upright" 解析到派生件而不是基准件 ⇒ 仓库既有断言立刻红 6 条。
  独立目录同时让 `verify-env.sh:104` 的 `NotoSans-*.ttf` 计数保持 4。
  **因此 `build/fonts/SHA256SUMS` 不需要任何改动。**
* **断言（防回退，已全绿）**：`ShippedDerivedUiFont_HasZeroMask_AndPreservesEverything` ——
  掩码 = 0、快路径放行、`glyf/loca/hmtx/cmap/head/hhea/maxp/name/OS-2/post` 字节不变、
  3884 个字形步进与 lsb 逐项相同、**全部 65536 个 BMP 码点**的 cmap 相同、Skia 可加载且 256 个字形步进一致。

### 11.4 应用方式（给主控/宿主）

```
① 字体文件：build/fonts-ui/UI-NoLayout.ttf
② 让默认 UI 字体指向它：
   · 若 M7b 的 SPI 取的是**族名/文件名** → WPF_LINUX_UI_FONT=UI-NoLayout.ttf，并把
     build/fonts-ui 加进 WPF_LINUX_FONT_DIR 的冒号列表（它不会与测试目录混在一起）；
   · 若取的是**路径** → 直接指 build/fonts-ui/UI-NoLayout.ttf。
③ 不要改名成 NotoSans-*.ttf，也不要放进 build/fonts/（见 11.3）。
④ 重新生成：python3 build/gen-ui-font.py （--check 只校验不重写）
```

---

## 12. WIC → Skia 位图解码栈（侦察 + 选型；**最小闭环未交付**）

> ⚠ 先说不做的部分：**本轮没有交付任务书 §3 的最小闭环**（`BitmapImage`/`BitmapDecoder`
> 真实路径读盘 → 像素逐点一致）。原因是侦察阶段就撞到**第二道 seam**（见 12.4），
> 它需要与 T1/M7b 约定后才能继续；我把预算用在了把这道 seam 测清楚，而不是写一份
> 跑不通的 shim。下面是我已经拿到的**硬事实**与**下一步的精确清单**。

### 12.1 109 条的分布（不按名字猜，按真实调用点）

声明**集中在一个文件**：`PresentationCore/System/Windows/Media/UnsafeNativeMethodsMilCoreApi.cs`
（`namespace MS.Win32.PresentationCore; internal static partial class UnsafeNativeMethods`），
形态是**每组一个嵌套静态类**、每个方法一条
`[DllImport(DllImport.WindowsCodecs, EntryPoint = "IWIC<接口>_<方法>_Proxy")]`：

| 嵌套类（= COM 接口） | 条数 | 真实调用点（`UnsafeNativeMethods.<类>.<方法>`） | 落在哪些文件 |
|---|---|---|---|
| `WICImagingFactory` | 19 | **28** | BitmapDecoder / BitmapEncoder / BitmapMetadata / BitmapPalette / BitmapSource / CachedBitmap / CroppedBitmap / FormatConvertedBitmap / InplaceBitmapMetadataWriter / InteropBitmapSource / PixelFormat / StreamAsIStream / TransformedBitmap |
| `WICBitmapCodecInfo` | 8 | **15** | BitmapCodecInfo / BitmapDecoder |
| `WICBitmapSource` | 5 | **11** | BitmapDecoder / BitmapFrameDecode / BitmapPalette / BitmapSource / BitmapSourceSafeMILHandle / PixelFormat |
| `WICBitmapDecoder` | 8 | **9** | BitmapDecoder / BitmapFrameDecode |
| `WICMetadataQueryReader` | 5 | **6** | BitmapMetadata / BitmapMetadataEnumerator |
| `WICPixelFormatInfo` | 3 | **4** | PixelFormat |
| `WICBitmap` | 3 | **4** | CachedBitmap / InteropBitmapSource / WriteableBitmap |
| `WICBitmapFrameDecode` | 3 | **3** | BitmapFrameDecode |
| `WICFormatConverter` | 1 | **2** | BitmapSource / FormatConvertedBitmap |
| `WICStream` | 2 | **2** | StreamAsIStream |
| 其余冷门面（编码器/元数据写入/组件信息/颜色上下文/属性包…） | 52 | 0（本轮路径上不出现） | BitmapEncoder / InplaceBitmapMetadataWriter / 等 |

**最小可用面（读图 → 像素/元数据 → 格式转换）**，按真实调用点排序：

```
① IWICImagingFactory      CreateDecoderFromFileHandle / CreateDecoderFromStream / CreateStream /
                          CreateFormatConverter / CreateComponentInfo（PixelFormat 要用）
② IWICBitmapDecoder       Initialize / GetFrameCount / GetFrame / GetContainerFormat / GetDecoderInfo /
                          GetMetadataQueryReader
③ IWICBitmapSource        GetSize / GetPixelFormat / GetResolution / CopyPixels / GetColorContexts
   IWICBitmapFrameDecode  同上（frame 是 source；另有 GetMetadataQueryReader）
④ IWICBitmapCodecInfo     GetFriendlyName / GetMimeTypes / GetFileExtensions / GetContainerFormat /
                          GetPixelFormats / GetColorManagementVersion / GetDeviceManufacturer …
                          （BitmapCodecInfo 的 15 处调用；Decoder.CodecInfo 一碰就要）
⑤ IWICFormatConverter     Initialize /（CanConvert 由工厂侧返回）  ← FormatConvertedBitmap
⑥ IWICMetadataQueryReader GetMetadataByName / GetLocation / GetContainerFormat   ← BitmapMetadata
⑦ IWICPixelFormatInfo     GetBitsPerPixel / GetChannelCount / …      ← PixelFormat
```

### 12.2 三条 seam 的实测比较（选型依据）

| 方案 | 可行性（实测） | 结论 |
|---|---|---|
| **(a) 托管替换调用点** | 109 条声明在**一个文件**里，但调用点**散在 13+ 个文件**（还有 `BitmapSource.cs` 1961 行、`BitmapMetadata.cs` 1538 行这种大件）；要么整体替换声明文件（须逐字复刻 MilCore 的另外 100+ 条声明），要么替换多个上游大文件 | ⛔ 改动面与回归风险最大 |
| **(b) 原生 shim 实现 `*_Proxy`** | ✅ **实测可行且不需要托管桥接**：`libSkiaSharp.so` **导出了 Skia C API — 856 个 `sk_*` 符号**（含 `sk_bitmap_*`/`sk_codec_*`）⇒ shim 可以在 C 里用 Skia 直接解码，**不存在** `MilFontFace_RegisterFromFile` 那种跨运行时问题 | ✅ **选定** |
| (c) 让 MilCore 的 .so 代实现 | `*_Proxy` 的 DllImport 目标是 **`WindowsCodecs.dll`**（不是 MilCore），且 `src/WpfGfx.Linux/Interop/` 与 `src/WpfGfx.Linux.Native/` 都在别人的写入范围 | ⛔ 越界 |

**另一条实测事实（决定了 shim 必须放哪）**：`*_Proxy` 的句柄参数是**不透明 `IntPtr` / `SafeMILHandle`**，
托管侧只把它们**回传**给别的 proxy，从不自己解引用（样例签名：
`CreateDecoderFromStream(IntPtr pICodecFactory, IntPtr pIStream, ref Guid, uint, out IntPtr ppIDecode)`）
⇒ shim 完全可以自行发放"指针"（malloc 的小结构体或句柄表下标），**不需要真 COM**。
这与 `Win32ShimResolver` 的现状一致：它**刻意**把 `WindowsCodecs.dll` 留作 `DllNotFoundException`
（其文件头注释把这条列为"刻意的、一眼可见的缺失面"）。

### 12.3 今天"用到会怎样"（实测）

* `Win32ShimResolver` 对 `WindowsCodecs.dll` 返回 `IntPtr.Zero` → 交回默认探测 → 任何 WIC 调用
  都是 **`DllNotFoundException: Unable to load shared library 'WindowsCodecs.dll'`**（不是静默、不是空像素）。
* `WiringSmoke` 里已加了一段 WIC 探针（`WIC_PROXY_TYPE_FOUND=True`），可直接用于后续复验；
  实测同时发现：**`WICImagingFactory` 没有 `CreateDecoderFromFilename`**（该接口 19 个方法里
  只有 `CreateDecoderFromStream` / `CreateDecoderFromFileHandle`）—— 这否掉了我原本"用文件名走最短路径"的想法。

### 12.4 ⚠ 第二道 seam（本轮真正的阻塞，需要与 T1/M7b 约定）

读图要么走**文件句柄**、要么走**流**，两条都跨出 WIC 之外：

| 路径 | 依赖 | 现状 |
|---|---|---|
| `CreateDecoderFromFileHandle(hFile)` | `hFile` 来自 Win32 shim 的 `CreateFile`（`libwpfwin32.so`，M7b） | 需要 WIC shim 与 Win32 shim **共享句柄语义**（否则 shim 拿到一个它读不懂的整数） |
| `StreamAsIStream` → `WICImagingFactory.CreateStream` + `MilCoreApi.MILCreateStreamFromStreamDescriptor` | 后者是 **MilCore 导出**（`src/WpfGfx.Linux/Interop/`，T1） | 需要 T1 确认该导出在 `wpfgfx_cor3.so` 里存在且语义可用（与 `MilFontFace_RegisterFromFile` 同一类问题） |

**这就是为什么最小闭环没有在本轮落地**：不是 shim 写不出来，而是"谁来给 shim 一个可读的字节源"
这件事必须先把上面两条中的一条定下来，否则 shim 写完了也接不上。

### 12.5 下一步（精确清单，可直接派人执行）

1. **定 seam**（半天）：与 T1 确认 `MILCreateStreamFromStreamDescriptor` 在 .so 中的可用性；
   若不可用，走"文件句柄共享语义"（与 M7b 约定 `CreateFile` 返回的整数如何被 shim 解释，
   建议：shim 只把它当 `open()` 过的 fd 用，由 `libwpfwin32.so` 保证 `CreateFile` 返回真实 fd）。
2. **写 shim**（`build/DirectWrite.Linux/wic-shim/` → `libwpfwic.so`）：按 §12.1 的 ①-⑦ 顺序实现
   ~22 个 `*_Proxy`；解码用 `sk_codec_*`/`sk_bitmap_*`（C API 已实测存在）；
   句柄 = 自增下标 → 结构体表；**只实现读路径，编码器一律返回 `WINCODEC_ERR_NOTIMPLEMENTED`**。
3. **接线 1 行**：`build/shims/Win32ShimResolver.cs` 的 `MappedLibraries` 加
   `WindowsCodecs.dll → libwpfwic.so`（这是主控允许的"仅在必须时"改动）。
4. **闭环断言**：`BitmapImage(fileUri)` → `CopyPixels` → 与 `SKBitmap.Decode(file)` 逐点比对；
   `FormatConvertedBitmap → Bgra32`；`BitmapMetadata` 取宽高/DPI；失败路径（不存在/非图像/截断）。

### 12.6 本轮新增/改动

* `build/DirectWrite.Linux/WiringSmoke/Program.cs`：新增 **WIC 探针**（类型存在性 + 方法清单 + 调用异常实测），
  作为后续闭环的复验入口；沿用既有的 HintPath 引用形态（这样 PC 内部依赖能进 deps.json）。
* 断言：**94/94 仍全绿**（未新增断言 —— 闭环没交付，就不给自己发绿）。

---

## 13. WIC shim 前置验证（主控定 seam = 文件句柄后的第一步）

### 13.1 ✅ seam 已证：`FileStream.SafeFileHandle` 是**真 Linux fd**（主控的判定成立，第二道 seam 不存在）

`WicSeamProbe/`（新增，独立 harness，不碰 PC）对 `samples/HelloMil/screenshot.png` 实测：

```
STREAM_TYPE=System.IO.FileStream   CAN_SEEK=True   IS_ASYNC=False   LENGTH=58776
SAFEHANDLE_TYPE=Microsoft.Win32.SafeHandles.SafeFileHandle   HANDLE_VALUE=32
READLINK=/home/.../samples/HelloMil/screenshot.png   READLINK_MATCHES_FILE=True
PREAD_READ=8  BYTES=89504E470D0A1A0A   IS_PNG_MAGIC=True        ← PNG 魔数
PREAD_TAIL_READ=8  BYTES=49454E44AE426082                       ← IEND 块
PROC_FD_EXISTS=True    SEAM_OK=True
```

* 句柄值 32 在 `/proc/self/fd/32` 里**指向那个 PNG**，`pread` 拿到 PNG 魔数与 IEND ⇒ 真 fd。
* `CanSeek=True` + `IsAsync=False` 与上游分支条件（`BitmapDecoder.cs:1102/1111/1116/1118`）完全对上
  ⇒ **主路径就是 `CreateDecoderFromHandle`**，且与 `libwpfwin32.so`、MilCore **零耦合**。
* 结论：`win32 句柄共享语义`与`MILCreateStreamFromStreamDescriptor`两条依赖**都不需要**；
  §12.4 的阻塞解除。路径 B（`StreamAsIStream`）维持主控裁定：本轮不做，恢复条件见 §13.4。

### 13.2 ⚠ Skia C API 解码：**走到一半**（诚实记录，未完成）

`wic-shim/probe_decode.c`（gcc + dlopen，不依赖任何托管代码）实测：

```
DLOPEN_OK=/.../libSkiaSharp.so
SYMS_OK=1
FD=3 SIZE=58776 PREAD=58776 MAGIC=89504E47
CODEC=ok
GET_INFO=? WIDTH=800 HEIGHT=600 COLOR_TYPE=6 ALPHA_TYPE=1     ← **尺寸与 PNG 的 IHDR 完全一致**
GET_PIXELS=5 FIRST4=00000000 LAST4=00000000 CENTER=00000000   ← 像素没被填充
```

* ✅ 已证：`sk_data_new_with_copy` + `sk_codec_new_from_data` 能从**我们自己 pread 出来的字节**打开解码器；
  `sk_imageinfo_t` 的**宽高字段布局假设正确**（读出 800×600，与 `python3` 直读 PNG IHDR 的结果一致）。
* ❌ 未证：`sk_codec_get_pixels` 返回 **5**（`sk_codec_result_t` 的某个失败值）且缓冲区全 0。
  最可能的原因是我**在猜枚举值**：`colorType=6`/`alphaType=1` 的真实含义、以及
  `sk_codec_options_t` 的语义都**没有头文件可查**（仓库里没有 `sk_*.h`）。
* 过程中的一个具体教训：第一版探针把 `sk_data_destroy` 当析构函数 —— **该符号不存在**
  （真名是 `sk_data_unref`）⇒ 调空指针 SIGSEGV。**= "按名字猜 C API" 就会付代价的直接证据。**
* **因此：在写那 22 个 `*_Proxy` 之前，必须先把匹配 SkiaSharp 2.88.9 的
  `include/c/{sk_codec,sk_data,sk_image,sk_imageinfo,sk_colortype,sk_colorspace}.h` 拿到手**
  （或确认 T1 的 MilBridge 是否已经链接了 Skia C API / 有这组头）。
  有了头，剩下的 22 个函数是机械工作；没有头，就会继续"猜一个崩一个"。

### 13.3 已铺好的骨架（可编译、可继续）

```
build/DirectWrite.Linux/WicSeamProbe/      seam 探针（已跑通，输出见 13.1）
build/DirectWrite.Linux/wic-shim/
├── probe_decode.c                          Skia C API 解码探针（13.2 的结果）
├── probe_decode                            产物（gcc -O1，直接 dlopen）
└── （下一步）wic_proxy.c / Makefile / build-wic-shim.sh
```

### 13.4 未覆盖清单与"用到会怎样"（保持主控口径）

| 面 | 行为 | 恢复条件 |
|---|---|---|
| `BitmapImage(Stream)`（非 FileStream，走 `StreamAsIStream`） | 本轮**不覆盖**；shim 对应函数返回 `WINCODEC_ERR_NOTIMPLEMENTED (0x88982F04)` | `StreamDescriptor` 已有 `pfnRead/pfnSeek/pfnStat/pfnCanSeek`；T1 用一个**真 `IStream` vtable** 包一层即可（机制可行，本轮不做） |
| 编码/保存（`BitmapEncoder`、`WICBitmapFrameEncode`、`WICFastMetadataEncoder`、`WICMetadataQueryWriter`） | 统一 `WINCODEC_ERR_NOTIMPLEMENTED` | 独立里程碑 |
| COM 工厂枚举 / 组件信息全集 / 颜色上下文 / 颜色变换 / `WICBitmap` 写入面 | 同上 | 同上 |
| **在闭环成立之前不要打开 `Win32ShimResolver` 的默认映射** | 否则调用从 `DllNotFound` 变成 `EntryPointNotFound`（诊断更差） | 用 `WPF_LINUX_WIC_SHIM` 显式给路径先跑通闭环 |

> `Win32ShimResolver.cs` 的泛化（按 shim 路径索引的句柄字典 + `WindowsCodecs.dll` 映射 + 候选路径
> ①`WPF_LINUX_WIC_SHIM` ②程序集目录 ③仓库 `build/DirectWrite.Linux/wic-shim/`）**本轮未改**：
> 按主控要求，它必须等读路径真的实现之后才对齐；且本轮**不重建 PresentationCore**（M7b 独占）。

### 13.5 断言与回归

**94/94 仍全绿**（本轮**未新增断言** —— 闭环未成立，按主控口径不注水）。
本轮新增文件仅：`WicSeamProbe/`（探针工程）与 `wic-shim/{probe_decode.c,probe_decode}`。

---

## 14. WIC → Skia shim：读路径实现 + C 级闭环证据（**托管闭环待 PC 合并**）

### 14.1 ABI 事实（主控 AbiProbe + 本目录 `probe_decode.c` **双向复核**，两条独立实现给出同一个像素）

| 事实 | 值 / 结论 |
|---|---|
| `sk_imageinfo_t` 真实布局 | `{ void* colorspace; int32_t width, height, colorType, alphaType; }`（24 字节）；**公开头文件的字段顺序在 SkiaSharp 2.88.9 上是错的** |
| `sk_codec_get_info` 返回值 | **无 HRESULT 语义**（实测随机值）→ 只看结构体是否被填对 |
| `sk_codec_get_pixels` | `(codec, &info, pixels, rowBytes, NULL)` —— **options 必须 NULL**；传零值结构体得 `InvalidParameters(5)`（我第一版踩的坑，已在本目录留下对照） |
| `sk_colortype_t` | C API 值 ≠ 托管值（从 8 起分叉）；`Bgra8888 = 6` 两边一致，shim 只用它 |
| 释放 | `sk_data_unref`（**没有** `sk_data_destroy`；调空指针会 SIGSEGV，我踩过） |

**双向复核（同一张 `samples/HelloMil/screenshot.png`，两条互不相干的实现）**：
```
主控 AbiProbe（C#/P/Invoke）：sk_codec_get_pixels hr=0，与托管 SKBitmap.Decode 全缓冲比对 mismatchBytes=0
                              非白像素 150998，像素(22,16)=BGRA 102,51,34,255
本工程 probe_decode（纯 C） ：GET_PIXELS=0  PIXEL_22_16_BGRA=102,51,34,255  NON_WHITE_PIXELS=150998/480000
                              WIDTH=800 HEIGHT=600（与 PNG IHDR 一致，python3 直读核对）
```

### 14.2 shim 实现（`wic-shim/wic_proxy.c` → `libwpfwic.so`，28800 字节）

**已实现（读路径，21 个导出）**：
`WICCreateImagingFactory` / `IWICImagingFactory_CreateDecoderFromFileHandle` /
`_CreateFormatConverter` / `_CreateComponentInfo` / `IWICBitmapDecoder_{GetFrameCount,GetFrame,GetContainerFormat,GetDecoderInfo}` /
`IWICBitmapSource_{GetSize,GetPixelFormat,GetResolution,CopyPixels}` /
`IWICFormatConverter_Initialize` / `IWICBitmapCodecInfo_{GetContainerFormat,GetFriendlyName,GetMimeTypes,GetFileExtensions,GetDeviceManufacturer,GetDeviceModels,GetColorManagementVersion}` /
`IWICPixelFormatInfo_{GetBitsPerPixel,GetChannelCount}` + 自检导出 `WicShim_SelfTest`。

**设计要点**：句柄 = 表下标+1（托管侧只回传不解引用 ⇒ 不需要真 COM）；`CreateDecoderFromFileHandle` 对传入 fd 做 `dup`（不动调用方的 fd 生命周期）；像素一律以 **Bgra32** 交付；`CopyPixels` 只支持整图（子矩形给 `UNSUPPORTEDOPERATION`，**不静默截断**）。
**未实现面**：30 个导出统一 `WINCODEC_ERR_NOTIMPLEMENTED (0x88982F04)`。
**部署**：`dladdr` 自定位 ⇒ **两个 .so 同目录即可**（实测：不放 env、`libSkiaSharp.so` 与 `libwpfwic.so` 同目录 → 正常解码）。

### 14.3 C 级闭环证据（`probe_shim.c`，dlopen 我的 .so，走完整 `*_Proxy` 链）

```
EXPORTS_OK=1
FACTORY=S_OK handle=0x1
FD=3                                   ← 真 Linux fd（seam 见 §13.1）
CREATE_FROM_FD=S_OK decoder=0x2
FRAME_COUNT=S_OK n=1
GET_FRAME=S_OK frame=0x3
GET_SIZE=S_OK 800x600
GET_PIXELFORMAT=S_OK  PIXELFORMAT_D1=6FDDC324       ← 32bppBGRA
GET_RESOLUTION=S_OK 96x96
COPY_PIXELS=S_OK need=1920000
SHIM_PIXEL_22_16_BGRA=102,51,34,255                 ← **与主控 AbiProbe 逐字节相同**
SHIM_CENTER_BGRA=255,255,255,255
SHIM_NON_WHITE=150998 / 480000                      ← 与直解探针一致（排除空白图假绿）
SHIM_PIXEL_FNV1A=62FD64E564953288                   ← 全缓冲指纹，供后续跨实现比对
FAIL_BADHANDLE=0x80070057                           ← E_INVALIDARG
FAIL_NOTIMAGE(/etc/hostname)=0x88982F0B             ← WINCODEC_ERR_UNSUPPORTEDOPERATION
FAIL_TRUNCATED=0x88982F0B                           ← 截断 PNG 同样明确失败，不崩
NOTIMPL_METADATA=0x88982F04                         ← 未实现面是明确 HRESULT
SELFTEST=S_OK 800x600
```

### 14.4 验收 ①-⑤ 的当前状态（**诚实**）

| 指标 | 状态 |
|---|---|
| ① `BitmapImage(fileUri).CopyPixels` vs `SKBitmap.Decode` 逐点一致 | ⏳ **C 级已证**（像素与主控托管侧逐字节相同的那个点一致；全缓冲 FNV 指纹已留）；**托管闭环未跑** —— 需要 PC 把 `WindowsCodecs.dll` 绑到本 .so（resolver 泛化）+ 一次 PC 重建，两者主控已明确**另行开波** |
| ② `FormatConvertedBitmap → Bgra32` | ⏳ shim 侧 `CreateFormatConverter` + `Initialize`（仅接受 32bppBGRA）已实现并可调；托管侧未跑 |
| ③ `BitmapMetadata` 宽/高/DPI | ⏳ **未做**（`GetMetadataByName` 等 → `NOTIMPLEMENTED`）；`GetSize`/`GetResolution` 已可用（96 DPI 固定值，**PNG 的 pHYs 未解析**，见下） |
| ④ 失败路径的 HRESULT 与异常类型 | ✅ **HRESULT 侧已实测**（上表三条）；托管侧会映射成什么异常类型**待 PC 合并后复验** |
| ⑤ 未覆盖清单 + 恢复条件 | ✅ 见 §14.5 |

**已知偏差（登记，不含糊）**：`GetResolution` 固定返回 96 DPI —— 未解析 PNG `pHYs`/JPEG JFIF 密度；对 DPI≠96 的图会偏。列为本轮未做项。

### 14.5 未覆盖清单与"用到会怎样"

| 面 | 行为 | 恢复条件 |
|---|---|---|
| **`BitmapImage(Stream)`（非 FileStream）** | 走 `StreamAsIStream` → `CreateStream` + `MILCreateStreamFromStreamDescriptor`；shim 侧 `CreateStream` = `NOTIMPLEMENTED` | `StreamDescriptor` 已有 `pfnRead/pfnSeek/pfnStat/pfnCanSeek`，T1 用真 `IStream` vtable 包一层即可 |
| `BitmapMetadata`（读元数据） | `NOTIMPLEMENTED`（0x88982F04）→ 托管侧会抛明确异常 | 用 Skia 无对应能力；需自带 PNG/JPEG 元数据解析（独立小任务） |
| 编码/保存（`BitmapEncoder` 全族） | `NOTIMPLEMENTED` | 独立里程碑（Skia 的 `sk_image_encode_*` 可用；需要 `CreateStream`/`FrameEncode` 一族） |
| `WICBitmap` 写入面（`WriteableBitmap`/`CachedBitmap`/`InteropBitmapSource`） | `NOTIMPLEMENTED` | 同上 |
| COM 工厂枚举 / 颜色上下文 / 颜色变换 / 调色板 / 缩放裁剪旋转（scaler/clipper/fliprotator） | `NOTIMPLEMENTED` | 各为独立小块；Skia 侧都有对应（`sk_image_scale_pixels` 等） |
| **DPI 非 96 的图** | 报 96（偏） | 解析 PNG `pHYs` / JPEG JFIF density |
| **子矩形 CopyPixels** | `UNSUPPORTEDOPERATION` | 加 ROI 支持（Skia 侧用 subset 解码） |

### 14.6 断言与回归

**94/94 仍全绿；本轮未新增断言**（托管闭环未跑通，按主控口径不注水）。
本轮新增（全在 `build/DirectWrite.Linux/wic-shim/`）：
`wic_proxy.c` / `build-wic-shim.sh` / `libwpfwic.so`（产物）/ `probe_decode.c` / `probe_shim.c` / 两个探针产物 / 同目录的 `libSkiaSharp.so`（部署形态验证用副本）。
`Win32ShimResolver.cs` **仍未改**（等读路径在托管侧跑通后按主控要求"写好并对齐到最后"）。

---

## 15. WIC 托管侧接线（写好未激活）+ 闭环 harness 的首轮实测

### 15.1 `build/shims/Win32ShimResolver.cs` 泛化（已改，**默认映射关闭**）

247 → 363 行。改动仅限主控授权的范围：

| 改了什么 | 细节 |
|---|---|
| 句柄缓存 | `_cachedHandle/_cachedPath`（单个）→ **按 shim 文件名索引的字典** `_loadedHandles/_loadedPaths`（`lock` 保护），Win32 与 WIC 各占一份 |
| 新增映射 | `WicMappedLibraries = { "WindowsCodecs.dll" }` → `libwpfwic.so` |
| 候选路径（WIC 组） | ① `WPF_LINUX_WIC_SHIM`（文件或目录）② 程序集目录 ③ 仓库 `build/DirectWrite.Linux/wic-shim/`（向上最多 12 级查找） |
| **默认开关** | `WicEnabledByDefault = false`；**只有** `WPF_LINUX_WIC_SHIM` 非空 **或** `WPF_LINUX_WIC=1` 才启用；未启用时 `Resolve` 直接返回 `IntPtr.Zero` ⇒ 调用方看到的仍是**原来那条** `DllNotFoundException` |
| Win32 组行为 | 路径解析顺序、日志行文本、`DllNotFoundException` 文案**逐字未变**；`LoadedPath` 语义保持（仍只指 Win32 shim），新增 `WicLoadedPath` / `WicMappingActive` 供诊断 |
| T1 的钩子 | `#if PRESENTATION_CORE` 里的 `MilCoreDllImportResolver.TryResolve(...)` **一行未动** |

**打开默认映射的确切条件**（主控合并时按此翻 `WicEnabledByDefault`）：
① PC 重建一次（让本文件进入 `PresentationCore.dll`）；② §15.3 的**两个前置阻塞**解决；
③ 本目录 `WicClosedLoop` harness 的 ①-④ 全绿。三者齐备前保持 `false`。

### 15.2 闭环 harness（`build/DirectWrite.Linux/WicClosedLoop/`，已可运行）

按验收 ①-⑤ 编写，含：逐字节比对 + `PIXEL_FNV1A` 与 C 级探针记录值 `62FD64E564953288` 的交叉校验、
`FormatConvertedBitmap`、元数据/DPI（**按已知偏差断言**，不断言"等于文件真实 DPI"）、三条失败路径、
未覆盖清单，以及 **`DECODER_BRANCH`**（靠 shim 新增的 `WicShim_CallCounts` 导出分别统计
`CreateDecoderFromFileHandle` / `CreateStream` 的调用次数，把"走哪条分支"变成**可观测输出**）。
映射未启用/SKIP 时**明确打印原因并返回 SKIPPED**，不假绿。

### 15.3 ⚠ 首轮实测暴露**两个 WIC 之前**的阻塞（比 WIC 更外层，必须先解决）

```
RESOLVER_INSTALLED=False  InvalidOperationException: A resolver is already set for the assembly.
  ⇒ PresentationCore 的 [ModuleInitializer]（Win32ShimResolver）已占用该程序集的解析器槽位，
    harness **无法**自装 ⇒ **WIC 映射只能由 build/shims/Win32ShimResolver.cs 提供（需 PC 重建一次）**。

CHECK1=FAIL Win32Exception: Unknown error 1400
  STACK: BitmapImage → Dispatcher.get_CurrentDispatcher → Dispatcher..ctor
         → MessageOnlyHwndWrapper..ctor → CreateWindowEx → 1400
  ⇒ `new BitmapImage()` 是 DispatcherObject ⇒ 先建 Dispatcher ⇒ 建 message-only 窗口。
    **本 harness 在没有 X 的环境里跑**（主控要求不跑 X），`CreateWindowEx` 返回 1400
    ⇒ 失败点在 **WIC 之前**。需要在 M7b 那套有 DISPLAY 的环境里复跑本条。

CHECK2/3/4 = MarshalDirectiveException:
  "Cannot marshal 'parameter #1': Invalid managed/unmanaged type combination
   (Marshaling to and from COM interface pointers isn't supported)"
  ⇒ **结构性阻塞**：WIC 那 109 条 P/Invoke 的签名里有 **COM 接口指针**类型
    （`SafeMILHandle` 一族 / `PROPVARIANT` / `IStream`），.NET 在 Linux 上**直接拒绝 marshal**，
    连"走到 WIC"的机会都没有。
  ⇒ 结论：**光有原生 shim 不够** —— 还需要把那些代理声明的参数类型改成 **`IntPtr`**
    （即"句柄即 IntPtr、不做 COM marshal"）。这与 shim 的设计是一致的（shim 只收发不透明句柄），
    改动面是**声明文件**（`UnsafeNativeMethodsMilCoreApi.cs` 的 WIC 嵌套类），**不是** 13 个调用点。
```

**这三条对主控的合并计划有直接影响**：WIC 闭环的依赖顺序是
**(a) PC 重建（带 resolver 泛化）→ (b) 修 COM-marshal 签名 → (c) 在有 X 的环境跑 harness**，
而**不是**"打开映射就能跑"。

### 15.4 验收 ①-⑤ 当前状态

| 指标 | 状态 |
|---|---|
| ① 逐字节一致 | ❌ **未达标**（失败点在 WIC 之前：无 X 环境的 `CreateWindowEx 1400`） |
| ② `FormatConvertedBitmap→Bgra32` | ❌ 未达标（COM marshal 阻塞） |
| ③ 元数据/DPI | ❌ 未达标（同上）；断言已按"DPI 固定 96 = 已知偏差"写好 |
| ④ 失败路径托管异常类型 | ❌ 未达标（三条都是 `MarshalDirectiveException`，**基础设施异常**，harness 已按"不算达标"处理） |
| ⑤ 未覆盖清单 | ✅ 已输出 |
| C 级闭环（shim 自身） | ✅ 已过（§14.3，像素与主控 AbiProbe 一致） |

### 15.5 断言与回归

**94/94 仍全绿；本轮未新增断言**（托管闭环未成立）。本轮改动：
`build/shims/Win32ShimResolver.cs`（授权泛化）、`build/DirectWrite.Linux/WicClosedLoop/`（新 harness）、
`build/DirectWrite.Linux/wic-shim/{wic_proxy.c,libwpfwic.so}`（+调用计数导出，重新构建）。
**未重建 PresentationCore、未跑 X。**

---

## 16. §15.3 归因更正：COM-marshal 阻塞**不在 WIC 声明**里，而在 `SecurityHelper`

> 主控核查了全部 110 条 WIC 声明（参数类型普查：`SafeMILHandle` 族 120 / `IntPtr` 60 / `UInt32` 41 /
> `Guid` 20 / `LPWStr` 15 / `Int32Rect` 4 / …，**没有任何 COM 接口指针**）⇒ 我 §15.3 的第 ③ 条归因**不成立**。
> 我按主控要求先做**免费测试**（`DISPLAY=:99`）+ 打印**完整异常栈**，证据如下。

### 16.1 `DISPLAY=:99` 的结果：窗口那面墙消失，露出**唯一**的下一面墙

| 环境 | CHECK1 的首帧 | 判定 |
|---|---|---|
| **无 DISPLAY** | `MS.Win32.UnsafeNativeMethods.CreateWindowEx(...)` → `Win32Exception 1400` | Dispatcher → message-only 窗口建不起来（**与 WIC 无关**，主控预判正确） |
| **`DISPLAY=:99`** | 不再是 CreateWindowEx；四个 CHECK **全部**停在同一处（见 16.2） | ✅ 窗口/Dispatcher 那面墙**被 DISPLAY 解决了**，失败点往前推进 |

### 16.2 证据：`MarshalDirectiveException` 的第一帧（四个 CHECK 完全相同）

```
at MS.Win32.UnsafeNativeMethods.CoInternetCreateSecurityManager(
       Object pIServiceProvider, Object& ppISecurityManager, Int32 dwReserved)
VIA_DISPATCHER_OR_WINDOW=False      ← 与 Dispatcher/窗口无关（DISPLAY 已解决那面墙）
IS_WIC_FRAME=False                  ← **不是 WIC**（主控的普查得到确认，我的归因错误）
错误文本: "Cannot marshal 'parameter #1': ... (COM interface pointers isn't supported)"
          ↑ parameter #1 正是第一个 `Object` 参数 —— 与声明逐字对上
```

**声明处**（`upstream/.../Shared/MS/Win32/UnsafeNativeMethodsOther.cs:57-61`，逐字）：
```csharp
[DllImport(ExternDll.Urlmon, ExactSpelling = true)]
internal static extern int CoInternetCreateSecurityManager(
    [MarshalAs(UnmanagedType.Interface)] object pIServiceProvider,
    [MarshalAs(UnmanagedType.Interface)] out object ppISecurityManager,
    int dwReserved);
```
`[MarshalAs(UnmanagedType.Interface)]` **显式**声明成 COM 接口指针 ⇒ .NET 在 Linux 上直接拒绝 marshal。
**唯一调用点**：`upstream/.../Shared/MS/Internal/SecurityHelper.cs:80`（WPF 的 URL **安全区域**判定）。
该文件在 PC 的编译清单里（`build/PresentationCore.Linux/PresentationCore.Linux.csproj` 有 `Shared/MS/Internal/SecurityHelper.cs`）。

### 16.3 两条修法（都**只**影响 Linux，且都比"改 110 条声明"小得多）

| 方案 | 内容 | 代价 |
|---|---|---|
| **A（推荐）短路调用点** | Linux 上"URL 安全区域"没有意义（无 zone 概念）⇒ 用编译期替换把 `SecurityHelper` 的这次调用短路成"本机/无限制"，**根本不去 marshal** | 替换 1 个 shared 文件（≈200 行）；不改任何 WIC 声明 |
| B 改声明+映射 | 把该声明改成 `(IntPtr, out IntPtr, int)` 并给 `urlmon.dll` 加一个 shim 映射 | 还要多一个 .so 导出；且"安全区域"在 Linux 上本就无意义 ⇒ 属于给不存在的语义造实现 |

**注意授权范围**：主控授权的是 `patch-presentationcore-wicmarshal.py`（针对 **WIC** 声明）；
而证据指向的是 **`SecurityHelper.cs` / `UnsafeNativeMethodsOther.cs`（Urlmon，非 WIC）**。
⇒ 我**没有动笔**（严格遵守"先拿证据、再谈补丁"）。若走 A，机制与约定可原样复用
（upstream 逐字读入 → 只改需要的行 → 写 `build/PresentationCore.Linux/*.Linux.cs` → csproj Remove+Include
→ `--check` + 幂等 + 锚点计数不符即退出），只是**目标文件不同**，需主控确认。

### 16.4 验收 ①-④ 的最新状态

| 指标 | 状态 |
|---|---|
| 前置：窗口/Dispatcher | ✅ `DISPLAY=:99` 下已通（1400 消失） |
| 前置：COM marshal（Urlmon 安全区域） | ❌ 未解决 —— **这是 WIC 之前唯一的墙**（精确到方法名与参数序号） |
| ① 逐字节一致 / ② 格式转换 / ③ 元数据+DPI / ④ 失败路径 | ⏳ 全部卡在上述那一面墙；`DECODER_BRANCH` 仍为 `FileHandle+0 Stream+0`（**没走到 WIC**，所以分支仍未知） |
| ⑤ 未覆盖清单 | ✅ 已输出 |
| C 级闭环（shim 自身） | ✅ 已过（§14.3） |

### 16.5 我在这一轮犯的错（记录备查）

把"**一个为真的实测**"（`MarshalDirectiveException`，确实存在）错接到"**一个不成立的对象**"
（110 条 WIC 声明）上 —— 与主控自述犯过的是同一类错。**纠错的方法是主控给的**：
先打印**完整栈**（含方法名与 `parameter #N`）+ 先做**免费的 DISPLAY 对照**，
两步都做完，真凶（`CoInternetCreateSecurityManager` 的 `[MarshalAs(UnmanagedType.Interface)]`）就自己浮出来了。
harness 现已固化这两件事：每个 CHECK 打印完整 `ToString()`、`FIRST_FRAME`、
`VIA_DISPATCHER_OR_WINDOW`、`IS_WIC_FRAME` 四个判定字段。

---

## 17. 补丁 H（URL 安全区域短路）已交付 + 同类普查（权威清单版）

### 17.1 补丁 H：`src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py`（**只新增这一个文件**）

形制照抄 WindowsBase 的补丁 G（从上游**逐字**读入 → 只替换 `MapUrlToZoneWrapper` 一个方法 →
写 `build/PresentationCore.Linux/SecurityHelper.Linux.cs` → csproj `Remove` 上游 + `Include` 生成物）。
`safeFileHandle` 生成物头部注明「不要手改」；`--check` 重新生成到内存并逐字节比对 + 检查 csproj 两条目。

**注释里钉死的论证**（主控要求）：
* `URLZONE_LOCAL_MACHINE = 0`（`Shared/MS/Win32/NativeMethodsOther.cs:54`），而**上游自己**
  在 `SecurityHelper.cs:77` 的兜底值就是它，注释原文 *"fail securely this is the most priveleged zone"*
  ⇒ 短路返回的 0 **就是 WPF 自己选定的默认值**，不是我们编的；且 `return 0` **不会**触发后面的
  `if (targetZone < 0) throw new SecurityException(...)`。
* 同族先例：非 Windows 上不设 `XamlAccessLevel`（CAS 已不存在）⇒ 这是**放宽一条在 Linux 上
  已不存在的安全限制**，不是伪造一次成功的 COM 调用。
* `UnsafeNativeMethodsOther.cs` 那条 urlmon 声明**保留不动**（改完不可达，删它反而牵动别的程序集）。
* 范围：**只有 PC** —— `MapUrlToZoneWrapper` 整个包在 `#if PRESENTATION_CORE`（`SecurityHelper.cs:66-113`）
  ⇒ PF/ReachFramework/WindowsBase 没有这个方法的编译单元，**一行未动**。

**`--check` 输出（要求贴回，已通过）**：
```
[输入] 上游 upstream/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SecurityHelper.cs
[OK] 生成物与重新生成的结果**逐字节一致**：build/PresentationCore.Linux/SecurityHelper.Linux.cs
[OK] csproj 已含补丁 H 的两条目（Remove 上游 + Include 生成物）
[OK] --check 通过
```
幂等：连续跑两次 `--check` 均通过（第二次不产生任何变化）。

**用 MSBuild 自己的 item 求值做终验**（比读 XML 可靠）：
```
$ dotnet msbuild build/PresentationCore.Linux/PresentationCore.Linux.csproj -getItem:Compile -m:1
COMPILE_COUNT = 1355
UPSTREAM_REMOVED  = True   []                                   ← 上游 SecurityHelper.cs 已不在编译集
GENERATED_INCLUDED= ['SecurityHelper.Linux.cs']                 ← 生成物已进入编译集
```

**落地时踩到并修掉的两个坑**（都写进脚本注释，防后来者重复）：
1. 直接插在锚点行**前面** → 块内 `<ItemGroup>` 嵌进外层 `<ItemGroup>` → **MSB4232**（`Target 元素之外的项目必须具有 Include/Update/Remove`）。⇒ 必须插在 `</ItemGroup>` **之后**。
2. 插在锚点所在 ItemGroup **之前** → MSBuild 的 `Remove`/`Include` 按**文档顺序**求值，上游 `Include` 在后面把文件又加回来（`-getItem` 实测上游条目仍在）⇒ 必须让 `Remove` 出现在上游 `Include` **之后**（WindowsBase 补丁 G 正是这个形制）。

### 17.2 同类普查（**权威清单版**：`dotnet msbuild -getItem:Compile` = 1355 个文件）

方法：不靠正则从 csproj 里抠 `<Compile>`（那样会漏 `Shared/**`，我第一版就漏了，还被自己的
拼写错误 `UnmarshalType` 骗出"0 处"的假结果），而是让 **MSBuild 直接吐出权威编译清单**；
在清单内的文件里找 **DllImport 参数**中带 `[MarshalAs(UnmanagedType.Interface)]` 的声明
（要求该行确实落在某个 `static extern` 的签名区间内 —— 上一版没做这步，产生 4 条假阳性），
再按方法名找调用点。

**结果：3 条**（注意：`CoInternetCreateSecurityManager` **已不在表里** —— 补丁 H 已把那份文件
移出编译集，这本身是普查与补丁互相印证的一个自洽性检查）。

| # | 声明（file:line） | 方法 | Interface 参数序号 | 调用点 | 可达性 | 分类 |
|---|---|---|---|---|---|---|
| 1 | `PresentationCore/MS/Win32/UnsafeNativeMethodsPenimc.cs:613` | `CoCreateInstance` | 2 | 同文件 `:159`（PenIMC 初始化） | Linux 上 PenIMC 无落点（`PenIMC_cor3.dll` 未映射） | **【待观察】** |
| 2 | `PresentationCore/MS/Win32/UnsafeNativeMethodsPenimc.cs:608` | `UnlockWispObjectFromGit` | 1 | 同文件 `:195` | 同上，且只在不平板上走 | **【死代码】**（当前不可达） |
| 3 | `PresentationCore/MS/internal/WindowsRuntime/Windows/UI/ViewManagement/NativeMethods.cs:27` | `WindowsDeleteString` | 4 | `InputPaneRcw.cs:34`、`UISettingsRcW.cs:30` | WinRT 在 Linux 上不存在 ⇒ 更早失败 | **【待观察】** |

**【必须修】= 0 条** —— 唯一被实测咬到的那条（`SecurityHelper`）已由补丁 H 修掉。
**最小修法（若将来 1/3 变成可达）**：与 H 同形制 —— 1 属于 PenIMC，短路 PenIMC 初始化即可（无平板设备）；
3 属于 WinRT RCW，短路成"无 InputPane/UISettings"即可。**不要**改声明本身（共享源、会牵动多程序集）。

**⚠ 本普查的边界（明确写出，不用"大概还有"含糊）**：
- 只覆盖了 **`UnmanagedType.Interface`** 这一族。**未覆盖**：`UnmanagedType.IUnknown` / `IDispatch` /
  `UnmanagedType.Interface` 的数组形态、以及 **DllImport 里裸 `object` / `object&` 参数**
  （在 P/Invoke 语义下裸 `object` 常按 `Variant`/`Struct` 处理，与 COM 那条不同路，但**必须单独核实**）。
- 因此这份表**当前可当"Interface 族"的验收清单用，还不能当"全部 COM marshal"的验收清单**。
  补齐只需在同一个脚本里把 pattern 换成
  `UnmanagedType.(Interface|IUnknown|IDispatch)` + 裸 `object` 参数两项，清单来源继续用 `-getItem:Compile`。

### 17.3 状态与边界

* 测试 **115/115 仍全绿；本轮未新增断言**（WIC 托管闭环仍未成立）。
* **未重建 PresentationCore**（只改了它的 csproj 与生成物；`-getItem:Compile` 是**求值**，不触发构建）、**未跑 X 测试**。
* 本轮改动：新增 `src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py`（唯一新增文件）；
  `build/PresentationCore.Linux/{SecurityHelper.Linux.cs（生成物）, PresentationCore.Linux.csproj（补丁 H 两条目）}`；
  `WicClosedLoop/Program.cs`（+完整异常 dump 与四个判定字段）；`REPORT.md`。
* 未碰：`build/DirectWrite.Linux/Provider/`（T1 的）、`src/` 其它、`build/MilBridge/`、`build/fonts*`、
  `build/PresentationFramework*.Linux/`、`build/ReachFramework.Linux/`、`build/WindowsBase.*`、
  `samples/`、`tests/.../Presentation.Tests/`、`handoff.md`、`verify-all.sh`、`upstream/`（只读）。

## 18. ole32 映射裁定 + WIC 句柄引用计数契约（T2，主控裁定后落地）

### 18.1 `ole32.dll` 进 **WIC 组**（已改代码，映射仍**默认 OFF**）

`Win32ShimResolver.WicMappedLibraries` 加了一行 `"ole32.dll"`（注释里写全了主控的三条理由）：
1. PC 权威编译集里 ole32 只有 **2 处**声明（`UnsafeNativeMethodsMilCoreApi.cs:1050 CoInitialize` /
   `:1054 CoUninitialize`），调用点是 **WIC 解码 bootstrap** 的 SafeHandle（`UnknownBitmapDecoder.cs:27,32`）
   ⇒ 与 WIC 开关**同生命周期**，`libwpfwic.so` 也已导出这两个名字，放 WIC 组语义自洽。
2. **不放 Win32 组**：本文件同时编进 **WindowsBase**，而 WB 另有 **5 处** ole32 声明
   （`MS/Internal/IO/Packaging/CompoundFile/PrivateUnsafeNativeCompoundFileMethods.cs:20,28,38,50,62`，
   打包/OLE 族，本轮复核确认就是 5 处）。我们的 shim 没有它们 ⇒ 会把 WB 现在**诚实的
   `DllNotFoundException`** 降级成 **`EntryPointNotFoundException`**（诊断更差），正是本项目一路避免的。
3. ⚠ 将来打包/OLE 真做实现时：要么给那 5 处补导出，要么把 ole32 挪到 Win32 组**并同时**处理 WB 那 5 条。

### 18.2 引用计数契约：**谁释放 WIC 句柄**（回答主控的问题，不沉默）

* **创建**：shim 在 `WICCreateImagingFactory_Proxy` / `CreateDecoderFromFileHandle` / `GetFrame` /
  `CreateFormatConverter` 里 `obj_new()` ⇒ `refs=1`，这 1 个引用**归调用方**（PC 装进 `SafeMILHandle`）。
* **释放走 MilCore，不走 shim**（实测）：`SafeMILHandle.ReleaseHandle()`（`SafeMILHandle.cs:61-63`）
  → `MILUnknown.ReleaseInterface` → **`MILRelease`**；而 `BitmapSourceSafeMILHandle : SafeMILHandle`
  （`BitmapSourceSafeMILHandle.cs:22`）。⇒ **T1 的 `MILRelease` 必须把 WIC 句柄转发到 `WicShim_Release`**。
* **不转发的后果不是"只是泄漏"**：shim 句柄表上限 `WIC_OBJ_MAX=256`，第 257 次创建 `obj_new` 返回 0
  ⇒ `CreateDecoderFromFileHandle` 失败（**解满 256 张图就崩**）。这是可复现的失败形态。
* **QI 配对**：COM 契约要求 QI 必须 AddRef ⇒ T1 放行时调 `WicShim_AddRef`，与
  `MILRelease→WicShim_Release` 成对。PC 实际形态（`BitmapSource.cs:581-586` + `BitmapFrameDecode.cs:448`）：
  创建(1) + QI(1) = 2 → 旧句柄 `value` 落地释放(1) → 新句柄释放(0) → shim 回收。账本自洽。
* ~~**允许的另一种自洽做法**：放行但不 AddRef~~ —— **❌ 这句是错的，已撤回**（T1 逐行推导出
  它会 **use-after-free**，见下表）。**唯一正确的形态是 AddRef + 转发 Release**：

  | 步 | 上游动作 | refs（AddRef ✓） | refs（不 AddRef ✗） |
  | --- | --- | --- | --- |
  | 1 | `BitmapFrameDecode.cs:701` `_frameSource = new BitmapSourceSafeMILHandle(frameDecode)` | 1 | 1 |
  | 2 | `:448` `WicSourceHandle = _frameSource` → QI | **2** | 1 |
  | 3 | `:453` `WicSourceHandle = CreateCachedBitmap(...)` **覆盖** ⇒ 释放第 2 步句柄 | 1 | **0 → 当场回收** |
  | 4 | `SafeMILHandle.cs:63` 释放 `_frameSource` | **0 → 回收 ✓** | **下溢 / 二次释放 ✗** |

  关键：第 1 步的 `_frameSource` 与第 2 步 QI 的句柄是**两个各自独立的持有者**，而在内存里
  是**同一个句柄值** ⇒ 不 AddRef 就是少记一个持有者。**任何"放行但不计数"的写法都会下溢。**
* 仍然**不允许**的形态：AddRef 了却不转发 Release —— 账本在本表上单边漏（撞 256 上限）。
  T1 已实测复现该硬失败：桥没接上时同一用例是"创建失败=216 … live→256"，接上转发后归零。
* 已知缺口（登记，不在本轮范围）：`decode_open`/`decode_pixels` 的 Skia 调用**仍不加锁**；
  本轮只给"表结构变更与计数"加了互斥（`g_table_lock`），**多线程并发解码仍是缺口**。

### 18.3 新增导出（`libwpfwic.so` 已重建）

| 导出 | 语义 |
| --- | --- |
| `CoInitialize` / `CoUninitialize` | ole32 公寓：Linux 无公寓模型 ⇒ S_OK / 空操作（语义正确的空实现，不是骗闸门） |
| `WicShim_OwnsHandle(h)` | 句柄归属判定（给 T1 的 `MILQueryInterface` 用；取不到就原样退 `E_HANDLE`） |
| `WicShim_AddRef(h)` / `WicShim_Release(h)` | 计数增/减（Release 归零即回收；未登记句柄返 0 且不崩） |
| `WicShim_HandleCount()` / `WicShim_PeakHandleCount()` | 活句柄数 / 高水位（配平判据；高水位防"花架子回收"） |

产物：**29,392 字节**，`sha256=ddf7b293e564d4af9211a50a8e02d1a68f3caf371b8d7c08de92f6df61769323`。
本轮另修一处**我自己的真 bug**：`0x88982F0B` 在上游是 `UNSUPPORTEDVERSION`，我原先错标成
`UNSUPPORTEDOPERATION` ⇒ ④非图像/截断经 PC `ConvertHRToException`（`wgx_render.cs:806-807`）
映射成会撒谎的 `FileLoadException("Mismatched versions…")`；改按上游取值后为
`FileFormatException: The image format is unrecognized.`（Windows 同族）。

### 18.4 配平实测（`wic-shim/probe_refcount.c`，**shim 级**，不需要 PC/X/harness）

```
gcc -O2 -o probe_refcount probe_refcount.c -ldl && ./probe_refcount ./libwpfwic.so /tmp/wic-probe.png
BASELINE live=0 peak=0
SELFTEST ok 4x4 px=FF000080  live=0            ← 真实 PNG 解码对象走完 obj_drop 后回落到基线
OK② 500 轮 AddRef×2+Release×2 后 live=1（未变），peak=1
OK②' 高水位未涨（=1）                          ← 防"计数相等但高水位一直涨"的花架子
OK③ 未登记句柄 AddRef/Release 返 0 且表不变
REFCNT_BALANCE=PASS
```
Skia 缺失/PNG 不可读时返回 **SKIP(exit 2)**，不报 PASS —— 不假绿。T1 在 MIL 侧接好转发后，
应能用同一判据做出 MIL 层的配平。

### 18.5 ⚠ 临时手法：`libole32.dll.so`（**去掉它的验证方式**）

PC 映射生效前，harness 靠部署期兜底跑通：`libwpfwic.so` 另存为 `libole32.dll.so`
（dlopen 探测名之一）+ `LD_LIBRARY_PATH` 命中。现存两份：
`wic-shim/libole32.dll.so`、`WicClosedLoop/bin/Debug/libole32.dll.so`。
**它绕过 PC 的映射表，不是实现**。撤销与验收固化进 `WicClosedLoop/run-harness.sh`：

1. `rm -f build/DirectWrite.Linux/wic-shim/libole32.dll.so build/DirectWrite.Linux/WicClosedLoop/bin/Debug/libole32.dll.so`
2. `./run-harness.sh`（**严格模式**：不设 `LD_LIBRARY_PATH`，ole32 只能靠 PC 的 `WicMappedLibraries`）
3. 判定两条：输出里**没有** `DllNotFoundException`（消息不含 `ole32.dll`）、且
   `DECODER_BRANCH=CreateDecoderFromFileHandle…` 照旧。仍报 `DllNotFoundException: ole32.dll`
   ⇒ PC 还没重建/映射未生效 —— **不要改回临时模式交差**。

### 18.6 本轮状态（按主控编排：不重建 PC、不跑 harness）

* 测试 **115/115 全绿**。~~①②③ 仍卡在 MilCore `MILQueryInterface`~~ —— **该结论已被 §18.7 取代：①②③ 已全绿。**
* 本轮改动：`build/shims/Win32ShimResolver.cs`（+`"ole32.dll"` 与理由注释）；
  `build/DirectWrite.Linux/wic-shim/{wic_proxy.c, libwpfwic.so（重建）, probe_refcount.c（新增）}`；
  `build/DirectWrite.Linux/WicClosedLoop/{Program.cs（临时手法标注）, run-harness.sh（新增）}`；`REPORT.md`。
* 未碰：`build/DirectWrite.Linux/Provider/`、`src/`（MIL 那道墙归 T1）、`build/MilBridge/`、`build/fonts*`、
  `build/PresentationCore.Linux/`、`build/PresentationFramework*.Linux/`、`build/ReachFramework.Linux/`、
  `build/WindowsBase.*`、`samples/`、`tests/.../Presentation.Tests/`、`handoff.md`、`verify-all.sh`、`upstream/`（只读）。

### 18.7 验收 ①②③ **全绿**（严格模式，PC 波 + MIL QI 落地后的首轮）

```
MODE=严格（不设 LD_LIBRARY_PATH：ole32.dll 只能靠 PC 的 WicMappedLibraries 解析）
WIC_SHIM_MAPS=1  WIC_SHIM_DOUBLE_TABLE=FALSE（单实例 ✓）
BYTE_MISMATCH=0 / 1920000
PIXEL_FNV1A=62FD64E564953288   (C 级探针记录的值 = 62FD64E564953288)  FNV_MATCHES_C_PROBE=True
PIXEL_22_16_BGRA=102,51,34,255  (期望 102,51,34,255)
CHECK1=PASS   CHECK2=PASS   CHECK3=PASS(尺寸正确 + DPI=96 与已登记偏差一致)
④ 失败路径 3/3：不存在→FileNotFoundException；非图像/截断→FileFormatException（内层 COMException 0x88982F07 = 本 shim 的 UNKNOWNIMAGEFORMAT）
CALLS_AFTER=[FileHandle=4 Stream=0 Other=0]   DECODER_BRANCH=CreateDecoderFromFileHandle   BRANCH_COUNTS=FileHandle+4 Stream+0
RESULT=PASS   exit=0
```
（`FileHandle` 计数在失败轮是 **+5**、全绿轮是 **+4**；差异**未逐条定位**，如实记下——分支结论两轮一致，`Stream` 恒为 0。）

**单实例判据已固化为断言**：`/proc/self/maps` 里 `libwpfwic` 的不同路径数 **=1** 才算接线正确；
≥2 直接判 `WIC_SHIM_DOUBLE_TABLE` 失败并写出原因（不让双表伪装成 `E_HANDLE` 的语义问题）。
两侧 env 由 `run-harness.sh` 一起指到同一个绝对路径：PC 侧 `WPF_LINUX_WIC_SHIM`、
MIL 侧 `MILBRIDGE_WIC_SO`（`MilExternalHandleBridge.CandidatePaths` 候选①，优先于 dladdr 目录）。

**本轮清掉的 4 道墙**（①②③ 从 E_HANDLE 到全绿）：

| # | 症状 | 真因 | 处置 |
| --- | --- | --- | --- |
| 1 | `COMException 0x80070006 E_HANDLE` | MilCore `MILQueryInterface` 不认识 WIC 句柄（T1 的活） | T1 已修；本 shim 的 `WicShim_OwnsHandle/AddRef/Release` 被其桥接 |
| 2 | `EntryPointNotFoundException: IWICBitmapFrameDecode_GetThumbnail_Proxy` | 读路径缺导出 | 补 5 个：`GetThumbnail`（帧/解码器）、`GetPreview`、`GetColorContexts`（帧/解码器） |
| 3 | `InvalidOperationException "Operation caused an invalid state"` | **再次串号**：我的 `NOTIMPLEMENTED` 用了 `0x88982F04`，上游那是 **WRONGSTATE** | 改 `E_NOTIMPL(0x80004001)`；并给未实现桩加 `WPF_LINUX_WIC_TRACE` 追踪 |
| 4 | `E_UNEXPECTED(0x8000FFFF)` at `PixelFormat.GetPixelFormat` | ①`CreateBitmapFromMemory`/`CreateBitmapFromSource`/`SetResolution` 是桩或缺失；②`FormatConverter.Initialize` 对**无 fd 的内存位图** `dup(-1)` 透传 ⇒ `decode_open` 报错 | 真实现三者（`CreateBitmapFromSource` 用**立即物化**语义）+ converter 对无 fd 源**接管像素** |

**⚠ `GetPreview` 的容忍码是 `UNSUPPORTEDOPERATION`（`BitmapDecoder.cs:741-743`），不是 `CODECNOTHUMBNAIL`** —— 两者写反就又是一次"能过但语义错"。缩略图才用 `CODECNOTHUMBNAIL`（`BitmapFrameDecode.cs:570-574`）。

产物：`libwpfwic.so` **38,232 字节**，`sha256=29736c30586666c119ff08f012a071cd…`（完整值见 `sha256sum`）。
回归：`probe_refcount` **REFCNT_BALANCE=PASS**；`dotnet test Tests` **115/115**。

**未覆盖面（诚实登记）**：编码器/元数据写面/写位图/调色板/颜色上下文（`GetColorContexts` 诚实回 0，未解析 PNG iCCP）、
scaler·clipper·fliprotator、ROI 子矩形（`0x88982f81`，不静默截断）、DPI≠96、`WICConvertBitmapSource`（本轮路径未用到，未实现）、
`CreateBitmapFromSource` 对 `CacheOnDemand` 是**提前物化**（结果同、多占一份内存）。
未实现面现在统一返回 **E_NOTIMPL**（原来那个 `0x88982F04` 会把"未实现"说成"状态非法"）。

## 19. 轨道 B 首轮：WIC 写面 + 元数据（T2）

**结论先说**：**DPI 真值与元数据读已全绿**（那条"固定 96"的偏差**销账**）；
**写面在 shim 级已是完整闭环**（PNG/JPEG 编码 → 自己的流 → 读回逐点一致）；
**WPF 级写面剩最后一段跨轨**：PC 给的流是 MIL 的句柄表对象，而 `MILIStreamWrite`
**只写进 MIL 自己的 `MemoryStream`**（`src/WpfGfx.Linux/Interop/MilNative.Misc.cs:207-228`，我逐行读过），
**没有转发到 `StreamDescriptor` 的回调** ⇒ 用户拿到的 `FileStream` 是 **0 字节**（实测）。

### 19.1 已绿（逐条实测）

| 验收 | 结果 | 证据 |
| --- | --- | --- |
| ② DPI 真值-PNG | **PASS** | `pHYs=11811 px/m` → `DpiX=299.9994`（原为常量 96） |
| ② DPI 真值-JPEG | **PASS** | JFIF density `units=1 X=Y=150` → `DpiX=150` |
| ② 无分辨率文件 | **PASS** | 无 pHYs 的 `screenshot.png` → `DpiX=96`（退默认，不再是"恒 96"） |
| ② 元数据读（自造件） | **PASS** | `GetQuery("/tEXt/{str=Title}")` = `"TrackB"` |
| ② 元数据读（真实截图） | **PASS** | `GetQuery("/tEXt/{str=comment}")` = `"HelloMil — WpfGfx.Linux on X11"`（UTF-8 正确） |
| ④ 失败-键不存在 | **PASS** | shim 返回 `PROPERTYNOTFOUND(0x88982f40)`；PC 的 `GetQuery` 吞成 `null`（上游语义） |
| ④ 失败-不支持格式 | **PASS** | TIFF → shim `COMPONENTNOTFOUND(0x88982f50)` → `NotSupportedException: No imaging component…` |
| ① 写→读（shim 级） | **PASS** | `probe_write_loop`：PNG 121 字节（含注入 pHYs）/JPEG 323 字节 → 读回 `8x8 first=0xFF110000` 一致 |

`probe_write_loop` 输出：`FAILPATH tiff_encoder hr=0x88982F50 (COMPONENTNOTFOUND ✓)`、`PNG_PHYS_INJECTED=1`、
`OK 写→读一致（尺寸 + 首像素）`、`WRITE_LOOP=PASS`。

### 19.2 ⚠ 本轮**又一次**"取值必须回上游"的教训（第三个）

容器 GUID：我原先用 `{0xb96b3caa-0728-11d3-…}` 当 `GUID_ContainerFormatPng`，
**上游真值是 `{0x1b7cfaf4-713f-473c-bbcd-6137425faeaf}`**（`Common/Graphics/wgx_exports.cs:334`；
JPEG 是 `{0x19e4a5aa-5662-4fc5-a0c0-1758028e1057}`，`:332`）。
后果不只写面：**读路径的 `GetContainerFormat` 一直在报错值**，直到写面 `CreateEncoder` 比对容器 GUID
（返回 `COMPONENTNOTFOUND`）才暴露。已按上游修正 —— 与 0x88982F0B / 0x88982F04 是同一种错误：
**约定值不许凭记忆写**。

### 19.3 未绿的两条：都卡在 MIL 侧（跨轨，已逐帧定位）

1. **④ 编码器写出（WPF 级）**：链路已全部走通并留痕 ——
   `SET_ENCODER_FORMAT 8x8 → Bgra32` → `ENCODED PNG 100 字节` → `PNG_INJECT pHYs=1 → 121 字节` → `MIL_WRITE=ok`，
   但用户 `FileStream` **0 字节**。根因：`MILIStreamWrite` 只写 `MilStreamObject.Data`（MemoryStream）。
   **需要的 MIL 侧改动**：`MILIStreamWrite` 之后把字节转交给 `StreamDescriptor` 的 Write 回调
   （反向 P/Invoke 包装表 `MilReversePInvokeTable` 已存在），或给 shim 一个"取走字节"的导出。
   ⇒ 同一条也解释了 5c（编码到只读流"竟然成功"：因为根本没往调用方流里写）。
2. **③ WriteableBitmap**：`WriteableBitmap.AcquireBackBuffer:962` → `BitmapSource.set_WicSourceHandle:584`
   → `MILQueryInterface` 返 `E_HANDLE(0x80070006)`：MIL 的 back buffer 句柄**既不在
   `MilDeviceObjectTable`、也不被 `WicShim_OwnsHandle` 认领**。需要 MIL 侧把 back buffer 注册成设备对象，
   或让 WIC shim 认领该句柄（我没有创建它，无法认领）。

### 19.4 本轮 shim 新增（都在 `wic_proxy.c`，产物 56,888 字节）

编码器全族（`CreateEncoder`/`Initialize`/`CreateNewFrame`/`SetSize`/`SetPixelFormat`/`SetResolution`/
`WritePixels`/`WriteSource`/`Commit`×2/`GetMetadataQueryWriter` + `Initialize`）、`WICSetEncoderFormat`、
`IPropertyBag2_Write`（质量）、`CreateStream`/`Stream_InitializeFromMemory`（自己的流对象）、
元数据写面 `SetMetadataByName`（PNG `tEXt` 注入）、PNG `pHYs`/`tEXt` 注入器（自带 CRC32）、
PNG/JPEG 元数据解析器、`WicShim_StreamBytes`（自测读流）。
**Skia 编码枚举是实测的**：`PNG=4 / JPEG=3 / WEBP=6`（`probe_encode.c` 逐个试出来，不猜）。

### 19.5 未覆盖面（诚实登记）

编码器信息/缩略图/调色板/色彩上下文（写面一律 `UNSUPPORTEDOPERATION`）、元数据枚举（`GetEnumerator`）、
JPEG 侧元数据读（EXIF 文本/APP1）、zlib 压缩的 `iTXt`、非 BGRA 源的预乘反解（当前按 BGRA 编码）、
`CreateBitmapFromSource` 对 `CacheOnDemand` 仍是提前物化、`BitmapMetadata.Format` 仍缺一个入口点（写面未影响）。

## 20. 字体公开入口两项定案（T2，追加任务）

新增闭环工程 `FontEntryClosedLoop/`（`run-font-harness.sh` 一键复跑；不需要重建 PC）。

### 20.1 追加 1：`new GlyphTypeface(Uri)` 的 NRE —— **复现 + 精确归因**

A 段**复现**（`FE_FIRST_FRAME` 与主控转来的栈逐字一致）：
```
NullReferenceException
FIRST_FRAME=at MS.Internal.FontCache.FontFaceLayoutInfo.IntMap.TryGetValue(Int32 key, UInt16& value)
            FontFaceLayoutInfo.cs:642
VIA_DISPATCHER_OR_WINDOW=False   IS_WIC_FRAME=False
```
**归因（读源码，不是猜）**：`FontFaceLayoutInfo..ctor`（:70）`_cmap = new IntMap(_font)`，
`IntMap.TryGetValue`（:636-646）内部先 `MS.Internal.Text.TextInterface.FontFace fontFace = _font.GetFontFace();`
再 `fontFace.GetArrayOfGlyphIndices(...)` ⇒ **NRE 的直接原因是 `_font.GetFontFace()` 返回了 null**
（空引用正好落在下一句解引用上），与主控线索一致。
另外 `GlyphTypeface.Initialize(Uri)`（`GlyphTypeface.cs:136-145`）里有 `fontFaceDWrite == null → FileFormatException` 的守卫，
它**没有被触发** ⇒ `CreateFontFace(uri,…)` 返回非空、而 `fontCollection.GetFontFromFontFace(...)` 给出的 `Font`
其 `GetFontFace()` 为空 —— **null 在 `Font`/`FontCollection` 这一层**。

**这条链的实现在哪（边界说明）**：`build/shims/PresentationCore.Factory.Linux.cs` 是本轮 T1 授权文件，
其头部列出它负责 `CreateFontFile / CreateFontFace ×2 / DWriteFactory …`，且 `:103` 明写
`internal IDWriteFactory* DWriteFactory => null;`（Linux 无原生工厂）。**我未改该文件**。
我在我的 lane 里做过逐步拆解（A2 段），但 `MS.Internal.Text.TextInterface.DWriteFactory` 在 PC 与 DirectWriteForwarder
两个程序集里**都找不到该类型名** ⇒ 反射拆解未成，如实记录（不冒充已拆开）。
**恢复条件（交给 shim 拥有者）**：让 Uri 形态下 `Font.GetFontFace()` 返回一个真实面
（或在 `CreateFontFace(uri,…)` 阶段就返回 null，让 PC 抛 `FileFormatException` 而不是 NRE——那至少是**诚实**的失败）。
**逐项等价基线**：现在**做不了** —— 公开 `GlyphTypeface(Uri)` 与"按名字建 Typeface"两条入口在 Linux 上都抛
（见 20.2），拿不到 `GlyphCount=3884 / Version=2.015` 对照面；`FontEntryClosedLoop` 已把该断言写好，
任一条入口通了我就能立刻给出逐项对照。

### 20.2 追加 2：`FontFamily(名字)` → `OSVersionHelper` 的**实测矩阵**（每格有证据）

| 写法 | 结果（实测） |
| --- | --- |
| `new FontFamily("Noto Sans")`（build/fonts 里有该面） | FontFamily 建出 ok → `new Typeface(...)`/`TryGetGlyphTypeface` **抛** `Exception: OSVersionHelper.GetOsVersion Could not detect OS!` |
| `new FontFamily("Arial")`（未安装） | **同样抛**同一条 |
| `new FontFamily("file:///…/NotoSans-Bold.ttf#NotoSans Bold")`（私有字体） | **同样抛**同一条 |
| `SystemFonts.MessageFontFamily` | 本 harness 未覆盖（`SystemFonts` 在 PresentationFramework，我用它就得引用 PF 程序集；**引用 M7b 的 HelloWpf 实测：该写法可用**） |

**证据链（栈逐帧）**：
```
OSVersionHelper.GetOsVersion()                         OSVersionHelper.cs:318   ← 末尾 throw
  ← CompositeFontParser.ParseFontFamilyCollectionElement()   CompositeFontParser.cs:344
  ← CompositeFontParser..ctor(Stream)                        CompositeFontParser.cs:181
  ← CompositeFontParser.LoadXml(Stream)                      CompositeFontParser.cs:152
  ← FamilyCollection.SystemCompositeFonts.GetCompositeFontFamilyAtIndex(Int32)  FamilyCollection.cs:242
```
⚠ **这修正了"只在名字未命中时才走"的假设**：**只要按名字/URI 建 `Typeface` 并取 `GlyphTypeface`，
就会去加载"系统复合字体"（`SystemCompositeFonts`）并死在 `OSVersionHelper`**——与名字是否已安装无关（三行实测皆然）。
`OSVersionHelper.GetOsVersion` 的实现就是一串 `IsOsWindows*` 判断，全都 false 后 `throw`（`OSVersionHelper.cs:310-318`）。

**两条修法的语义代价（我的判断）**：
* **伪造 OS 版本号** ❌：`CompositeFontParser` 正是用该版本**挑选平台专属的字体族链表**（Win7/8/10 变体），
  伪造会让 Linux 进程按某个 Windows 版本的行为走 —— **掩盖真实差异**，且是"以谎报换安静"，与工程纪律冲突。
* **短路复合字体解析** ✅（推荐）：让 `SystemCompositeFonts` 在 Linux 上返回"无复合字体"（或跳过 XML 解析），
  **代价**：丢掉 `GlobalUserInterface.CompositeFont` 的**回退链**（缺字时按复合字体映射到别的族）。
  但本移植的字体回退已由 provider 侧（`FaceSelector`/`LinuxFontCollection`）承担，这一层是**冗余**的；
  登记成"复合字体解析未实现，回退由 provider 承担 + 恢复条件（真要支持时按 Linux 平台给出族链表）"最诚实。
* 该改动落在 **PC 侧**（`FamilyCollection`/`CompositeFontParser` 是上游 PC 源码）——**不在我本轮可写范围**
  （`build/PresentationCore.Linux/` 与 `build/shims/` 本轮都不归我），故按"定案 + 建议"交接。

### 20.3 上一轮那两条（本次一并交付）

* **读路径过时标签已改**：`CHECK3` 现在如实写"该 fixture **无 pHYs** ⇒ DPI=96 是**默认退化**；
  '固定 96' 那条偏差已于 §19 销账，不再是'已知偏差'"。
* **新增真值断言 `CHECK3b`**：`WicClosedLoop/fixtures/dpi300-title.png`（pHYs=11811 px/m）⇒
  `DpiX=299.9994` **PASS** —— "真值"与"退化"各有独立断言，读路径整体仍 `RESULT=PASS`、`BYTE_MISMATCH=0/1920000`。
* WIC 写面续做（本轮）：**EXIF 文本**（`/app1/ifd/{ushort=271}`=`WpfLinux`、`/app1/ifd/exif/{ushort=36867}`=`2026:09:10 12:34:56`）、
  **PNG iTXt(zlib)**（`/iTXt/{str=Comment}`=`TrackB-iTXt`）、**元数据枚举**（2 项，含 Title 与 Comment）、
  **`BitmapMetadata.Format`**（`PNG`）全绿；`WICMapShortNameToGuid`/`WICMapGuidToShortName` 的**真实入口点名不带 `_Proxy`**
  （我先前按 `_Proxy` 命名 ⇒ `Format` 一直报缺入口点，已修）；
  **预乘反解**已实现并在 shim 级 A/B 实测（反解后 `128,64,32,128`；不反解 `64,32,16,128`，**偏差 −64,−32,−16**）。
  写面 harness 现 `RESULT=FAIL(2)`：**仅剩两条 MIL 跨轨**（③ WriteableBitmap 的 back buffer 句柄、④ 编码字节未转发到调用方 Stream），
  5c/6 按 SKIP 标注（不假绿也不假红）。

## 21. D-d 修法 A：`IWICBitmapSource::CopyPixels` 支持任意子矩形（T2）

### 21.1 根因链（T1c 定位，逐跳带文件:行；本节只记 shim 侧修法）

上游 `BitmapSource.cs:773-795` **有意的 1×1 探针**（注释原文："we call CopyPixels for the first pixel
which will decode the entire image"）：`CopyPixels(src, Int32Rect(0,0,1,1), 4, 4, buf)` →
shim 的 `full` 判定把 `(0,0,1,1)` 对 96×96 判成**非整图** ⇒ `UNSUPPORTEDOPERATION` →
上游 `:797-803` catch → `:950-957 RecoverFromDecodeFailure` **换源** ⇒ 交给 MIL 的是 1×1 Pbgra32。
⇒ **不是 PC 选错源，而是我们的一次诚实拒绝被上游当成"解码失败"。**

### 21.2 修法（只改 `CopyPixels`，整图路径一字未动）

* `prc` 规范化：`rx/ry/rw/rh`；`prc==NULL`、`(0,0,w,h)`、`(0,0,0,0)` ⇒ **走原有整图代码**
  （`need`/`cbStride`/`memcpy` 全不动）。
* 合法性：`rx<0 || ry<0 || rw<=0 || rh<=0 || rx+rw>width || ry+rh>height` ⇒ `E_INVALIDARG`
  **（不静默截断、不补零）**。
* 子矩形：`bpp = rowBytes / width`；`dst = cbStride ? cbStride : rowBytes`；
  `required = dst*(rh-1) + rw*bpp`；逐行 `memcpy`。
* **块位置**：放在既有 `if (cbBufferSize < need)` **检查之前** —— 否则子矩形会先被整图尺寸拒掉
  （1×1 只要 4 字节 < need=128），这是本修法最容易漏的一格。
* **可观测性**：入口 trace 带 `prc=(x,y,w,h)`；非法矩形打
  `COPY_PIXELS_REFUSE reason=invalid-rect prc=… src=WxH`；子矩形成功打 `COPY_PIXELS_SUBRECT prc=… dst=… cb=…`
  （限流沿用 `g_trace_budget`）。**子矩形拒绝不再是静默的。**

### 21.3 证据（探针 + 突变自查）

`probe_subrect.c`（新增）：正跑 6 条全 PASS（`(0,0,1,1)` 首像素逐字节相同、`(2,1,3x2)` 含自定义 stride
逐字节相同、越界/负偏移 `E_INVALIDARG`、`(0,0,0,0)` 整图语义、缓冲区不足 `E_INVALIDARG`）；
**突变自查**：行基址 off-by-one（`(ry+j)` → `(ry+j+1)`）⇒ 用例 1/2 **必红** ⇒ 撤销 ⇒ 复绿。
（刻意不用 `prc->x`/`prc->y` 对调：`(0,0,1,1)` 的 x=y=0 对它不敏感 ⇒ 会给出**假绿**。）

### 21.4 ⚠ 已知偏差（**非缺陷**，主控 2026-09-11 裁定）

`cbBufferSize` 不足时本实现返回 **`E_INVALIDARG`**，与 **WIC 文档语义
`WINCODEC_ERR_INSUFFICIENTBUFFER` 不同**。理由：这是**本移植的既有语义**（整图路径一行未动），
且**整图与子矩形两路一致**，`probe_subrect` 第 4 条已把它钉住。
**登记为已知偏差**；**若要改成文档语义属于行为变更**，须先有"有调用方依赖该码"的证据 ——
在拿到该证据之前**不得顺手改**（与退化 CTM 那条同一纪律）。

### 21.5 产物

```
wc_proxy.c  sha256=58066ac4ab740a531d9a9a7ccc3d9ac3
libwpfwic.so sha256=03b67fbcd7c385b6910396165a4db58a   70,440 B   Wic* 导出=13
DirectWrite.Linux.Tests : 123 通过 / 0 失败 / 0 跳过
五个探针：FOREIGN_DISPATCH / LOCK_PROBE / REFCNT_BALANCE / WRITE_LOOP / UNPREMUL_AB 全绿
```
发布由主控的 `publish-milbridge.sh` 执行（本车道**不手工拷件**）；发布后 T3 重跑基线验收
（`[cwic-trace]` 的 `size` 回到 96x96、`fmt` 回到 `…c90f`、门禁判据④ 转绿）。

### 21.6 本轮方法学教训（记档，避免重复）

1. **"计数"≠"结构"**：用朴素字符计数判断 C 文件结构会被**字符串字面量/注释里的花括号**污染
  （本例 `strncmp(r, "{ushort=", 8)`），据此得出"文件结构坏了"是**错误结论**。
2. **函数尾要取真值**：`return S_OK;` 在文件里**多处出现**，用"最后一个"会落到**下一个函数**
  （我插入过一次 ⇒ `'prc' undeclared (not in a function)` 类的报错）。正解：**本函数**的
  `return S_OK;` = 函数起点之后**第一个**其后紧跟 `}` 的那一处。
3. **新增块的位置要相对既有早退检查来定**：块写在文件末尾（`return S_OK;` 前）"看起来"对，
  但会被上面的 `need` 检查先拒 ⇒ **必须放在它之前**。
4. **突变要选用例敏感的算子**：不敏感的突变会给出"绿得很正常"的假绿。

## 22. app-local 副本一致性：机制级修法（债务 #20 收口，T2）

### 22.1 为什么要机制（本工程付过的代价）

| 事故 | 形态 |
| --- | --- |
| 3 份 `libwpfwic.so` | `WIC_SHIM_DOUBLE_TABLE=TRUE` ⇒ `E_HANDLE` |
| 发布目录 09-10 的 29,392 B 旧 shim | 权威件已 66 KB ⇒ 测到旧件 |
| `SystemFontsProbe/bin/Debug/libwpfwin32.so` 108,584 B | 权威 264,552 B ⇒ 谁在那跑探针都在测老 shim |
| `build/MilBridge/tests/*/bin/Release/DirectWrite.Linux.Provider.dll` 旧件 | **跑 Release 探针得出"补丁无效"的假红**（主控差点踩） |

### 22.2 两类分组：哪些会在运行期被加载（**依据随行**）

| 路径组 | 运行期会被加载？ | 依据 |
| --- | --- | --- |
| `build/MilBridge/tests/*/bin/Release/DirectWrite.Linux.Provider.dll` | **会（真风险）** —— 探针的启动目录就是 app-local ⇒ 已**结构性修复** | `build/MilBridge/tests/Directory.Build.targets` 的 `SyncProviderAuthority`（AfterTargets=Build、`SkipUnchangedFiles=false`）每次构建后强制同步（验收①②） |
| `build/DirectWrite.Linux/{WicClosedLoop,WicWriteClosedLoop,FontEntryClosedLoop}/bin/**` | 会，但**运行前自刷新** ⇒ 无害 | 三个 `run-*.sh` **逐个读过**，均含 `dotnet build`（`build=1 exec=1`）⇒ 无漏网 |
| `build/{PresentationCore,PresentationFramework*,ReachFramework,System.Printing,DirectWriteForwarder,CycleStub*}.Linux/bin/Debug/**` | **不直接加载**（库的构建输出，非启动目录） | 调用方按 HintPath 各自拷贝到自己的 app-local |
| `tests/WpfGfx.Linux.Tests/*/bin/**`、`samples/**` | 会，但 `dotnet test`/`dotnet run` 内含构建 ⇒ 无害 | 运行方式内含构建 |
| `obj/**/ref|refint/**` | 不会 | 引用程序集按设计不同（`SKIP(ref)`） |
| `build/MilBridge/.artifacts*/…/{release,Release}/**` | 产物、非启动目录 | 归入 `NO-AUTHORITY`（判不了，不算不一致） |
| `build/MilBridge/run.sh` | — | **T1 车道，只报建议**：runner 调探针前加一步 `dotnet build -c Release` |

### 22.3 `build/DirectWrite.Linux/` 下是否也放同形 targets —— **不做（已定），理由**

三个 harness（`WicClosedLoop` / `WicWriteClosedLoop` / `FontEntryClosedLoop`）的 `run-*.sh`
**逐个读过**：每个都 `dotnet build` 后再 `dotnet exec` ⇒ 运行前必刷新，**没有漏网**（取证：
`grep -c 'dotnet build'` 三个脚本均为 1）。再加一层 targets 只会形成**两套同步机制**，
且有"谁先谁后覆盖"的新风险 ⇒ **不添加**。若将来出现"被别处直接 `dotnet bin/…dll` 启动"的用法，
再按本节补 targets（本节即判定依据）。

### 22.4 检查器口径（`check-applocal-sync.sh`）与它的牙

五类分开计数：`OK / MISMATCH / DIVERGENT / NO-AUTHORITY / SKIP(ref) / RETIRED`；
**总判定只认 `MISMATCH>0 || DIVERGENT>0 || RETIRED>0`**；`NO-AUTHORITY>0` 时
`APPSYNC=PASS（另有 N 份无权威可比…）` + **exit 0** —— 否则每次发布都报红 ⇒ "狼来了"，
真红会被忽略（2026-09-11 主控裁定）。
自检：**A**（错 sha 的 .so 与托管 .dll ⇒ MISMATCH + exit≠0）、**B**（正确副本 ⇒ exit 0）、
**C**（同名同配置真不同 ⇒ `DIVERGENT` **且必须判成 `APPSYNC=MISMATCH`**）、
**D**（无权威的 Release 副本 ⇒ 计入 `NO-AUTHORITY` 且 **exit 0**）。
**收敛验证（实测）**：重建 `CompositeFontProbe` ⇒ 其副本 `0939c055d0d5affd` → `71ba86c6495347fe`（= 权威），
该目录随即从 `DIVERGENT` 掉出。

### 22.5 建议插入 `publish-milbridge.sh` 的行（**发布脚本归主控，本文件只留建议**）

```bash
# 债务 #20：Release 探针目录的 Provider 需重建（结构性修法见
# build/MilBridge/tests/Directory.Build.targets 的 SyncProviderAuthority）
if ! "$REPO/build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh"; then
    echo "提示：上面 MISMATCH/DIVERGENT 的副本**不是本次发布引入的**；"
    echo "      build/MilBridge/tests/* 的探针在下次 Release 构建时会自动同步（SyncProviderAuthority），"
    echo "      其余目录请各自重新构建，或跑对应 run-*.sh（它们内含 build 步骤）。"
fi
```
（用 `if !` 而非 `set -e` 直接失败：**发布不该被"待重建的副本"挡住**，但计数与 `EXPECT/ACTUAL` 一定会打印。）

### 22.6 本轮产物与边界

```
新增  build/MilBridge/tests/Directory.Build.targets（纯新增；该树原先无任何 Directory.Build.*）
改动  build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh（判定分离 + 大小写 + DIVERGENT 计数 + 自检 A–D + 头部口径）
      build/DirectWrite.Linux/wic-shim/build-wic-shim.sh（构建后自动校验 + 醒目告警，只报不改）
```
**只报不改**：未改任何副本内容、未动 `build/MilBridge/run.sh`、未跑应用、未重建 PC、未发桥。

### 22.7 复核：`publish-milbridge.sh` 里的校验接线（**只读复核，未改该脚本**）

复核对象：`build/publish-milbridge.sh:76-94`（`syncout=$(bash "$SYNC_CHECK" 2>&1)` 起）。**结论：口径正确，满足 22.5 的意图，无需改。**

* ✅ **非 PASS 时大声报但不改退出码**：与我 22.5 的用意见一致 —— 发布动作是否成功由发布自身的 rc 决定，
  副本不一致是**独立事实**，只报不吞（脚本注释也写明了这一点）。
* ✅ **`EXPECT/ACTUAL` 会随发布可见**：它 `grep -E 'MISMATCH|DIVERGENT|退役别名|APPSYNC='`；
  而校验器把 MISMATCH 打成**单行** `MISMATCH <path> EXPECT <sha> ACTUAL <sha>` ⇒ 两个 sha 都被回显 ✓。
* ✅ 已含 `DIVERGENT`（本次新增）与"不是本次发布引入 + 结构性修法指向
  `build/MilBridge/tests/Directory.Build.targets` 的 `SyncProviderAuthority`"两句提示 ✓。
* **可选细化（由主控定，非缺陷）**：若希望"**需重建**"的清单也随发布可见，可把 `NO-AUTHORITY|SKIP\(ref\)`
  也加进那条 `grep`。当前形态下，纯 `NO-AUTHORITY` 的情形会打印
  `APPSYNC=PASS（另有 N 份无权威可比：Release 副本 —— 见上）` ⇒ 信息**没有丢**，只是不逐行展开。

### 22.8 本轮未完成项（如实）

* **`run.sh tline` 六项回归未跑**：按主控时序"撞了就等一轮"，本轮实测
  `loadavg = 17.10 / 6.20 / 2.29`（3 核机）且**外来构建**（`wpf2web` 的 `probe.csproj`、
  `Wpf2Web.Compat.csproj`，flock 保护、非本工程）正在吃 CPU ⇒ **不并发**。
  待 T1d/T1b 与外来构建收工后再跑，届时报告"跑的哪份 `.dll`/`.so` sha"。
* `DirectWrite.Linux.Tests` 已跑：**123 通过 / 0 失败 / 0 跳过**（Debug，本轮）。

## 23. 两个残差（记账 1286/1298、折叠明细 218/236）的诊断：**待跑登记 + 结构性桶的既有证据**（T2）

> **状态**：§23.3 的"待跑登记"**已由 §23.6 关闭**（全量明细到手，三组读数判完：100–103 结构性/已登记 + 2 容差口径 + 0–3 未判定，**0 个新缺陷**）；
> §23.5.1 的"容差嵌套"推导**已在 §23.6.1 撤回**；§23.2 的"Tab 第一嫌疑桶"**已在 §23.6.3 作废**；
> §23.6.1 里那 2 条 `M_modifier` "真缺陷候选"**已由 §23.7（只读核查）钉成（甲）**：完全由"`TextModifier` 未实现"解释，不是新缺陷。本文以下原文不删，便于核对我的前后口径变化。

### 23.1 先纠正前提：派单里的两个数字**在磁盘上没有对应明细**

我把 `build/MilBridge/**` 里既有的产物全查了一遍（只读），能读到的最新读数是**上一代**：
```
T1d-report.md:81   | `T3` 折叠 | 判定 1298/1298；明细 **210/236** |
T1d-report.md:224  | … `T2 1276/1298 ①286/286 ②68/68 ③977/988 宽度超差83`、`T3 判定1298/1298 明细210/236` |
T1d-report.md:251  | …（不回归）`T2 1276/1298`、`T3 210/236` |
T1b-report.md:218  | v2 落地后重跑：折叠明细 **198/236 → 210/236** |
```
⇒ 派单给的 **`1286/1298`（12 行）** 与 **`218/236`（18 条）** 是**更新的那一跑**的读数，而**它的逐例明细不在磁盘上**
（`grep -rn "1286\|218/236"` 命中的全是别处的数字，没有一份产物记录这两组逐例清单）。
**因此我不做逐例归因** —— 逐例需要"跑到就能看见"，而现在（a）明细不存在，（b）`loadavg≈11.5` 且**外来构建**（`wpf2web`）在吃 CPU，
（c）**桥产物正被隔离**（T1b 误触 `all` ⇒ 16:02 AOT 覆盖 `66703024 → 55a9566e`，主控将按 `publish-milbridge.sh` 重发）
⇒ **此刻任何"桥读数"都不能当证据**（主控原话，我照此执行）。

### 23.2 能静态给出的：**已登记的"结构性桶"清单**（这决定 12/18 里有多少不该算缺陷）

`T1b-report.md` 已把四处差异**保留红并登记**（:5、:198 的 §4 表），它们**结构上不可比或已知边界**：
| 桶 | 既有登记读数 | 与本次两个残差的关系（**待验**） |
| --- | --- | --- |
| **Tab** | `T2c` Tab **15 例保留红**（`\t` 宽=0、且不算行尾空白；:298-312 有逐档断点推导） | 记账类残差的第一嫌疑桶 |
| **NBSP · ZWSP** | 登记红（:5） | 同上 |
| **Display** | 登记红（:5） | 同上 |
| **折叠可见前缀边界** | **38/236 行明细**（:198；含"簇边界 / 前缀行尾空白不计宽"两条精化） | **折叠明细残差（18 条）的第一嫌疑桶** |
| **CJK 回退边界**（T1d 的双向实验先例） | `Extent` 超差 **34 条 = 含 `与` 的 34 行**，100% 命中/0% 误伤 ⇒ 结构性 | 若 12/18 的用例**全部含 CJK**，同属此桶；**未验不得下结论** |

**判据（我要跑的东西与怎么判）**：对每一条残差取 `(用例名, 行号, 我们值, 真值, 差值)` 与特征
`{含 CJK?, 含 Tab?, 含 NBSP/ZWSP?, 空段?, 行尾禁则?}`，然后：
* 落在上表桶内 ⇒ **结构性不可比 / 已知边界**（追引用，不追代码）；
* 不在任何桶内 ⇒ **真缺陷**（进 backlog）；
* 特征采集不全或桶边界模糊 ⇒ **未判定**（不硬归类）。

### 23.3 待跑登记（**已由 §23.6 关闭**：三组读数（折叠 18 / Extent 58 / 记账 17）已由 T1b 的 18:31 那一跑全部落盘，我在 §23.6 判完；本节表格保留原文备查）

| # | 需要的读数 | 前置条件 | 怎么判 |
| --- | --- | --- | --- |
| 1 | `run.sh tline` 的**逐例明细**（12 条记账 + 18 条折叠），且报告里钉 **`.dll`/`.so` 的实际 sha** | T1b/T1d 停止构建、外来构建退潮（`loadavg` < ~4）、**桥按 `publish-milbridge.sh` 重发完毕** | 按 23.2 的三分支判定 |
| 2 | 这 30 条用例是否含 CJK（沿用 T1d 的"含 `与`"式双向实验：改一处已知结构变量 ⇒ 若这 N 条**同时**变绿/变红则结构性成立） | 同上；实验需 T1b 的 harness 支持 | 100% 命中 + 0% 误伤 ⇒ 结构性；否则缺陷 |
| 3 | 与"真机 oracle"同源的 `tests/parity/windows/layout-b34/windows-results.json`（**只读**，614 例/3222 行） | 无 | 仅用于交叉核对分组（A=459/B=30/F=120/M=5；硬断类 126 例） |

### 23.4 本轮边界

只读 `build/MilBridge/**` 的产物（未改任何文件）；未跑 `run.sh`（并发 + 桥隔离 + 明细不存在三条件下**不跑**）；
未新增仪表（按主控要求）；`-m:1`；未重建 PC、未发桥。

### 23.5 逐例归因（依据 `gen/tline-detail-20260913-1710.txt`，21,596 B / 208 行；现场重读 shim `ebccdb1ee65e6f76…`）

**这份明细是本轮实测**（`[逐行记账·结构] 全等 1286/1298` 就在 :55 ⇒ 我上一轮"找不到 1286"的结论**作废**，
旧 `1276/1298` 与 `210/236` 均为上一代配置，不再引用）。

#### 23.5.1 `Extent 1260/1298`（38 条）↔ T1d 的 34 条：**是包含关系（34 ⊂ 38），由容差嵌套推出**

* 本轮口径 **0.01**；T1d 那批 **0.34**（:79 同时给出两把尺子：`TextHeight @0.34 5/10 ｜ @0.01 5/10`）。
* **推导**：任何在 **0.34** 下超差的行，其差值 >0.34 >0.01 ⇒ **必然也在 0.01 下超差** ⇒ **34 ⊂ 38**，
  多出的 **4 行**其差值落在 **(0.01, 0.34]** 区间（"严口径多抓出来的边界行"，:79 明确写了这个区间存在：*差额行 16*）。
* **前提**：该推导在**同一次运行**内严格成立；T1d 的读数取自另一 shim 版本 ⇒ 跨运行比较需同 sha 才等价。
  **不因此下"缺陷"结论** —— 34 条已由 T1d 的双向实验判为**结构性（harness 单字体入口无 CJK 回退，含 `与` 行 100% 命中）**，
  本轮 38 条**未逐条列出**（见 23.5.4 缺口①）⇒ 我判：**34 条 = 结构性不可比（沿用 T1d 结论）；其余 4 条 = 未判定**。

#### 23.5.2 折叠明细 `218/236`（18 条 / 17 个用例）——**按组分类，两组性质不同**

用例分布：`F_nbsp_zwsp 8` ｜ `M_modifier 5` ｜ `A1_nbsp_zwsp 3` ｜ `F_lat_words 1`（= 17 例 / 18 行）。

| 组 | 明细里的原始读数（本文件） | 差值量级 | 判定 | 依据 |
| --- | --- | --- | --- | --- |
| `F_lat_words`（例：`F_lat_words_w40` 行#0–#3） | 真机 `W=21.5533 cr=[1,3) W=6.2533` vs 实得 `W=21.5520 cr=[1,3) W=6.2560`；行#1 `22.4967/9.0367` vs `22.4960/9.0400`；行#2/#3 同量级 | **0.0013–0.0027 DIP** | **判据口径（结构性不可比）** | 折叠明细是**逐位相等**式比较；而这些差异与 `Height` 的 `最大差 0.0013`（:75）同量级 ⇒ 属**浮点保真**，非布局差异 |
| `A1_nbsp_zwsp` / `F_nbsp_zwsp`（11 例） | 本次明细**只列样例**，未给出这 11 例的逐行值 | 未知 | **未判定** | 需 23.5.4 缺口②；但特征已定：**含 NBSP/ZWSP** ⇒ 与既有登记红桶（`T1b-report.md:5` 的 NBSP·ZWSP）同族，**不得算新缺陷** |
| `M_modifier`（5 例） | :91 给出 **折后宽度最大差 144.816 DIP，最差在 `M_modifier_winf` 行#0**（`visible=62 modifier=1`） | **144.8 DIP（大）** | **真缺陷候选 ⇒ 未判定** | 量级远超浮点噪声；但"cluster/可见前缀边界"正是 `T1b-report.md:198` 登记的**第 4 桶**（38/236 行明细）⇒ 需先排除该结构桶再定缺陷 |

#### 23.5.3 `记账 1286/1298`（12 行）——**未判定**（明细里只给了"一致"样例）

:70-73 的 4 行样例（`A1_lat_words_w20/30/40/50`）**真机与实得逐字相同**（`ws=1 WITW−W=4.1600` 双方一致）
⇒ 它们是**一致**的示例，**不含那 12 行**。⇒ 12 行的用例名/行号/差值**在本次产物中不存在** ⇒ 按你的规则：
**待跑登记**（见 23.5.4 缺口②），**不做推断**。可用的旁证：`,` `T2c Tab 15 例保留红` 与 `NBSP·ZWSP` 两桶是记账类差异的既有归属。

#### 23.5.4 缺口（如实登记，需 T1b 的仪表；我不动 `build/MilBridge/**`）

| # | 缺什么 | 为什么缺 | 需要什么读数 | 怎么判 |
| --- | --- | --- | --- | --- |
| ① | `Extent` 那 **38 行的逐行清单**（用例名/行号/我们/真值/差值/是否含 CJK） | 明细只汇总 + 少量样例 | 去掉 `colFailSamples` 截断（坐标 T1b 已写进其报告）后重跑一次 | 逐行查"是否含 CJK/是否含 `与`" ⇒ 34 条结构性的结论能否覆盖其余 4 条 |
| ② | 折叠 **18 行**与记账 **12 行**的**完整五列清单** | 同为 ≤24 样例截断 | 同上（同一次运行导出） | `F_*`（≤0.003 DIP）⇒ 口径；`M_modifier`（~145 DIP）⇒ 先排第 4 桶再定缺陷；`NBSP/ZWSP` 组 ⇒ 归既有登记桶 |

**当前可下的结论（三分支）**：`F_lat_words` 组 = **判据口径（结构性不可比）**；`NBSP/ZWSP` 两族 = **归既有登记桶（非新缺陷）**；
`M_modifier` = **未判定（真缺陷候选，需先排第 4 桶）**；`Extent` 34 条 = **结构性（沿用 T1d）**、其余 4 条 = **未判定**；
`记账 12 行` = **未判定（待跑）**。**没有一条被我判成"真缺陷"却缺少量级证据。**

> ⚠️ **本节两处已被 §23.6 更正/撤回**（见 §23.6.1、§23.6.3）：① 23.5.1 的"容差嵌套 ⇒ 34 ⊂ 38"**机制错误**（真实机制是家族不同）；② 23.5.2/23.5.4 的 `M_modifier`"未判定"、`记账 12 行`"未判定"**已由全量明细判掉**。

## 23.6 三分支收口：**105 条读数全部落到已登记桶 / 0 条新缺陷 / 2 条登记更正**（T2）

（105 = 段2 的 58（主集 38 + LH 20）+ 段1 的 18 + 段3 的 17 + 同在 `:55` 却只被"用例级"覆盖的 12 行；
**这几个集合两两之间有重叠**（同一用例会同时出现在三段的读数里），**去重后的根因清单在 23.6.5**，别拿 105 当"105 个 bug"。）

**依据（现场重读 sha，不引用别人的数）**：

```
build/MilBridge/gen/tline-detail-full.txt           99 行  df021453d87c73aa479ee30a2d4db76423fcbec0970423180c5d07c474346485
build/MilBridge/gen/t2d-extent-mismatches.txt       63 行  02663e5c01dc607415c69da921be83170e793424421f015c60292eaecd01a067
build/MilBridge/gen/tline-detail-20260913-1730.txt 210 行  3eea3e742df82cc7133c43b1612a0b74240d3d2eed651f250cbe690b67f443bc
                                                          ↑ **同一次运行的全文日志**：本节的汇总读数全在这里
```

**被测版本**（措辞按 T1b 原文口径 —— 这是**被测 `.cs` 源文件**的哈希，不是 `.so` 的）：
`build/shims/PresentationCore.HbTextLine.cs` sha256 **`EBCCDB1EE65E6F7653DA338D70188410615062F825A10969B737CA7D48281BBF`**（206,286 B / 3766 行）；
PC `8ff5cb388ca75964`、provider `71ba86c6495347fe`。与上一跑（`gen/tline-detail-20260913-1710.txt`）**同 sha** ⇒ 两跑同版、读数可比
（我上一轮立的"跨运行比较必须同 sha"前提在此满足）。
**我没有跑任何 harness / 应用**（数据已全）；只读产物与源码；`-m:1`；未杀外来构建。

### 23.6.0 口径一律回源码（`build/MilBridge/tests/HbTextLineParity/Program.cs`，只读）

| 读数 | 源码坐标 | 精确定义（**不是我推断的**） |
| --- | --- | --- |
| `全等 1286/1298` | 打印 :502；判据 :301/:305/:339 | **行级**：`L.Length==len ∧ L.NewlineLength==nl ∧ L.TrailingWhitespaceLength==ws ∧ \|(witw−w)−(WITW−W)\|<0.34`；分母 `lineTotal` = 可比行数 ⇒ **12 行不满足** |
| `298/315` ↔ 段3 的 `17` | :285 初值（行划分数是否相同）；:342/:390/:409/:431/:441/:461 任一置 false；:491 `diffCaseFull` | **用例级**：`caseExact` 被**任一**子项否掉即计 1，且**只记首个原因** ⇒ **17 个用例** |
| `+CJK` | :320/:321 + :847-851 | 用例文本含 `ch >= 0x2E80`（与"含 `与` U+4E0E"同一判据） |
| 严/宽两把尺子 | :319（0.01）/ :305、:337（0.34 = 1 ideal unit） | 两个常量都在源码里 —— 我上一轮说"两把尺子"时引用无误 |
| 段1 五列 | :411-415 | `collapsed.Length` / `collapsed.Width` + `cr[0].{TextSourceCharacterIndex, Length, Width}` ⇒ **`cr` 是 `[起, 长)`，不是 `[起, 止)`**（上一轮我按 `[起,止)` 读过，是错的） |
| 段1 判据 | :402-405 | 6 个子判据：Len 等 ∧ \|ΔW\|<0.34 ∧ `cr.Count==1` ∧ **起**等 ∧ **长**等 ∧ \|Δ`cr.Width`\|<0.34 ⇒ **失败的是哪一个**可由五列唯一确定（我据此逐行算，见表 23.6.2） |
| `NotoSansCJK(da)` | :542/:546/:567 | LH 组 `cjkCase` **显式用 `/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc`**；:79/:592 与 `T1b-report.md:857` 已登记"真机用 MS YaHei ⇒ 代用字体" |

⇒ **"记账 1286/1298（12 行）"与"段3 的 17 条"不是同一件事**（行级 vs 用例级、两套分母）⇒ 我上一轮"两个数字对不上"的疑问**闭环**，且**两个数都对**。

### 23.6.1 Extent 38 条（任务 1）：38 = **34 + 4**，**不是容差嵌套** —— 我 §23.5.1 的推导**撤回**

机读复核（命令实跑，见 23.6.7）：

```
段2 主集 38 = 带 +CJK 34 + 不带 4；  4 条全部是 M_modifier_*
（**别用 `grep -v '+CJK'` 直接当"非 CJK"**：LH 组 20 条的标记是 `NotoSansCJK(da)`，含 "CJK" 但不含 "+CJK"
 —— T1b 在 `T1b-report.md:845` 自己踩过这个坑并更正，我复现前先按原文加了 `-v 'NotoSansCJK(da)'`）
```

按你给的规则逐行判这 4 条（原文出处 = `tline-detail-full.txt`）：

| # | 行（原文逐字） | 差 | 阈值 | 规则分支 |
| --- | --- | --- | --- | --- |
| 1 | `:58 M_modifier_w80 行#1 [file-font] Extent 真值=18.0000 我们=14.3200 差=-3.6800` | **−3.6800** | 0.34 | 非 CJK 且 >0.34 ⇒ **真缺陷候选** |
| 2 | `:59 M_modifier_w120 行#1 [file-font] Extent 真值=18.0000 我们=14.3200 差=-3.6800` | **−3.6800** | 0.34 | 同上 |
| 3 | `:60 M_modifier_w320 行#0 [file-font] Extent 真值=18.0000 我们=18.0800 差= 0.0800` | **+0.0800** | 0.34 | 非 CJK 且 ≤0.34 ⇒ **容差口径（0.01 vs 0.34 两把尺子）** |
| 4 | `:61 M_modifier_winf 行#0 [file-font] Extent 真值=18.0000 我们=18.0800 差= 0.0800` | **+0.0800** | 0.34 | 同上 |

**两条"真缺陷候选"的量级证据**：差 **3.6800 DIP**、阈值 **0.34**（:305/:337）、出处 **`:58`/`:59`** ⇒ 超阈 10.8 倍，够格进候选。
**但必须补一条本轮之前没人给全的登记**：`M_modifier_*` 5 例**已被 T1b 登记为 bucket 5「`TextModifier` 未实现」**（`T1b-report.md:199`，
证据原文即"`M_modifier_w80` 真机 2 行 vs 我们 7 行 ⇒ **不是断行规则差异**"）；T1b §20.3-bis（`:846-857`）**独立算出同一个 34+4**，
并明写"**不是容差嵌套**"，还给了真值恒为 `18.0000`（整数值）这条线索。
⇒ **我的收口**：按行 = **2 条真缺陷候选 / 2 条口径**；**按根因去重 = 1 个已登记缺口（TextModifier）、0 个新缺陷**。

> ✅ **后续（§23.7，本轮只读核查）**：那 2 条"候选"已**钉成结论（甲）** —— 真值 `18.0000` = 去掉 modifier 区间 [6,45) 字符后的墨迹（16.0000）+ **与本实现同源的 `+2.0` inflate**；我们的 `18.0800 / 14.3200` 用同一个 upstream 公式从 `NotoSans-Regular.ttf` **逐位复算命中**，`0.08` 之差 = `f` 与 `l` 的 `yMax` 之差 ⇒ **完全由"`TextModifier` 未实现"解释，不是新缺陷**（也不留"未排除（乙）"的活口）。

**撤回**：§23.5.1 用"容差嵌套 ⇒ 34 ⊂ 38"解释那 4 条，**机制是错的** —— 4 条里有两条 `|d|=3.68 > 0.34`，容差嵌套**推不出**它们；
真实机制是**家族不同**：34 条全是 `*_nbsp_zwsp_*`（文本含 `与`），4 条全是 `M_modifier_*`。
数字巧合对上、机制不对 ⇒ 记账一笔，**结论以本节为准**（原 §23.5.1 保留原文 + 顶部更正指针，不删除）。

### 23.6.2 折叠 18 条（任务 2）：按"**哪个子判据失败**"分桶

**分桶规则（我定的，写出来供你驳）**：先看 `cr` 的**起/长** —— 不同 ⇒ 归 **bucket 4**（可见前缀/簇边界）；仅 `cr.Width` 不同 ⇒ 归 **bucket 2**；
`M_modifier_*` 一律归 **bucket 5**（按用例，且其 `Len`/行数也同时不同）。逐行：

| 用例 | 行 | ΔW | cr 我们 `[起,长)` | cr 真值 `[起,长)` | 失败的子判据 | 桶 |
| --- | --- | --- | --- | --- | --- | --- |
| `F_lat_words_w560` | #0 | 0.0140 | [30,40) | [31,39) | cr 起/长 | **4** |
| `F_nbsp_zwsp_w40` | #1 | 0.0000 | [6,3) | [6,3) | cr.Width | **2** |
| `F_nbsp_zwsp_w40` | #3 | -0.0007 | [14,2) | [14,2) | cr.Width | **2** |
| `F_nbsp_zwsp_w80` | #0 | **9.6807** | [2,7) | [1,8) | ΔW/cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_w80` | #1 | 0.0007 | [10,6) | [10,6) | cr.Width | **2** |
| `F_nbsp_zwsp_w120` | #1 | **2.6233** | [18,9) | [17,10) | ΔW/cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_w200` | #0 | **8.5460** | [8,13) | [7,14) | ΔW/cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_w320` | #0 | 0.0047 | [13,20) | [14,19) | cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_w560` | #0 | 0.0047 | [13,20) | [14,19) | cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_w900` | #0 | 0.0047 | [13,20) | [14,19) | cr 起/长/cr.Width | **4** |
| `F_nbsp_zwsp_winf` | #0 | 0.0047 | [13,20) | [14,19) | cr 起/长/cr.Width | **4** |
| `M_modifier_w80` | #0 | -13.9680 | [1,5) | [3,47) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_w80` | #1 | -14.0140 | [7,5) | [53,10) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_w120` | #0 | -18.8633 | [3,9) | [6,50) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_w120` | #1 | 29.0247 | [16,10) | [57,6) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_w200` | #0 | 17.7760 | [10,16) | [47,16) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_w320` | #0 | 82.0480 | [19,25) | [47,16) | Len/ΔW/cr 起/长/cr.Width | **5** |
| `M_modifier_winf` | #0 | **144.8160** | [28,35) | [47,16) | ΔW/cr 起/长/cr.Width | **5** |

**关键读数（可复核）**：18 行里 **15 行的 `cr` 起/长与真值不同**，**3 行完全相同、只有 `cr.Width` 不同**
（`sed -n '/^# 段1/,/^# 段2/p' … | grep -v '^#' | grep -c 'cr 我们=\[\([0-9]*\),[0-9]*) W=[^|]*|cr 真值=\[\1,'` ⇒ **3**）。
逐行看那 15 条 —— **要分两类，别混**：
* **8 条非 `M_modifier` 的**（`F_lat_words_w560#0`、`F_nbsp_zwsp` 的 `w80#0/w120#1/w200#0/w320/w560/w900/winf`）：`cr` 起**正好差 1 个字**，
  且**两侧的"止"都等于各自的 `Len`（行长度）** ⇒ 折叠隐藏的是**行尾**，唯一差别是**可见前缀的起刀点差一个字符**；
* **7 条 `M_modifier` 的**：`Len`（即**行长度**）本身就不同（`6≠50`、`6≠13`、`12≠56`、`14≠7`、`26≠63`、`44≠63`）⇒ **两侧比的不是同一行**，
  `cr` 的差是**行划分不同**的派生量，不能当"前缀起刀点"读（除 `winf#0` 那一行 `Len=63=63` 同行，起刀 28 vs 47）。

| 桶 | 行数 | 明细 | 依据 / 量级 |
| --- | --- | --- | --- |
| **bucket 4 纯**（只 cr 起/长 失败） | **1** | `F_lat_words_w560#0` | ΔW **0.0140**（≪ 一个空格宽 ⇒ 与 `:198` 的"**前缀行尾空白不计宽**"精化吻合） |
| **bucket 4 + `cr.Width`** | **4** | `w320`/`w560`/`w900`/`winf` #0 | ΔW **0.0047**（总宽一致）；`cr.Width` 差 6.7153 ⇒ 另挂 :198 bucket 2 那本账 |
| **bucket 4 + ΔW 超阈** | **3** | `w80#0` **9.6807**、`w120#1` **2.6233**、`w200#0` **8.5460** | 同时是"cr 起差 1 字"，故按你的规则先归 **4**；量级见下面的定量 |
| **bucket 2 纯**（cr 相同、只 `cr.Width` 差） | **3** | `w40#1`、`w40#3`、`w80#1` | `T1b-report.md:198` bucket 2 + §20.4 `:830`（"同 (cp,len) 但 cr 宽度**符号相反** ⇒ 宽度计算路径问题" —— 逐字命中 `w40#3`：我们 `-3.0560` vs 真值 `3.3433`） |
| **bucket 5 `TextModifier` 未实现** | **7** | `M_modifier_*` | `T1b-report.md:199`；量级 **13.9680 – 144.8160** DIP（最大差在 `winf#0`，与 `:91` 同值） |

**那 3 条"大差"的机制我做到了定量**（不是推断）—— 边界字符直接取自 harness 自己的输入
`tests/parity/windows/layout-b34/cases.json`（只读；文本 = `no\u00A0break\u00A0nbsp \u4E0E zero\u200Bwidth\u200Bspace`，32 字）：

* `w320/w560/w900/winf#0`：我们起刀 **13 = U+0020（空格）**、真值 **14 = U+4E0E（与）** ⇒ 差的是**一个空格**，而两侧折后总宽只差 **0.0047**
  ⇒ **渲染等价**，与 `:198` 的"前缀行尾空白不计宽"**逐字吻合**；
* `w200#0`：我们起刀 8（**NBSP**）、真值 7（`k`）⇒ 我们**多留一个墨字 `k`**，ΔW = **8.5460**（≈ em16 下 `k` 的宽）；
* `w80#0`：我们起刀 2（**NBSP**）、真值 1（`o`）⇒ 多留 `o`，ΔW = **9.6807**（≈ `o` 的宽）；
* `w120#1`：我们起刀 18（`r`）、真值 17（`e`）⇒ 多留 `e`，但 ΔW 只有 **2.6233**（小于一个 `e`）⇒ **量级不完全闭合，我不硬解释**
  （疑与椭圆宽度目标同时变化有关；缺"真机侧椭圆宽度"这条读数，没读到就不编）。

⇒ 折叠 18 条 = **结构性/已登记 18/18，真缺陷 0，未判定 0**（8 条属"起刀差 1 字"、3 条属 NBSP/ZWSP 宽度判据、7 条属 TextModifier）。

### 23.6.3 记账 12 行 / 17 用例（任务 3）：**17/17 全在登记桶；0 条 Tab**

段3 的 17 条按原因分桶（命令实跑）：

| 桶 | 条数 | 用例 |
| --- | --- | --- |
| **bucket 2 NBSP/ZWSP 行尾空白计数**（原因 `期望 … ws=0 \| 实得 … ws=1`） | **5** | `A1_nbsp_zwsp_w30/w40/w80`、`F_nbsp_zwsp_w40/w80` |
| **bucket 4 折叠可见前缀边界**（原因 `折叠明细不符`，逐行归入 23.6.2 的 8 行） | **7** | `F_lat_words_w560`、`F_nbsp_zwsp_w120/w200/w320/w560/w900/winf` |
| **bucket 5 `TextModifier` 未实现**（`行数 7≠2 / 5≠2 / 3≠1 / 2≠1` 4 条 + `折叠明细不符` 1 条） | **5** | `M_modifier_w80/w120/w200/w320/winf` |

⇒ **17/17 落在既有登记桶；Tab 桶 0 条**（`sed -n '/^# 段3/,$p' … | grep -c 'tab\|Tab'` ⇒ **0**；我另核了**非空性**：`gen/layout-b34-compact.json` 里含 `\t` 且 `fontKey=="file"` 的用例 **34 例**，不是"没有 Tab 用例所以没差异"）。
⇒ **我 §23.2 里"Tab 是记账类残差的第一嫌疑桶"的推测被数据否掉**：本轮 17 条里一条 Tab 都没有。
   **但注意别把 ✅ 读成"已修好"**：`T2c` 的通过判据是 `tabDiff == 0`（源码 :684-686，`tabDiff` = 含 `\t` 家族里不一致的例数），
   本轮确实 **34 例 Tab 一条不一致都没有**（真绿，不是空断言）；但其**标题仍写着"【已登记差异·保留红】…本实现未做"**（:170）
   —— 标题是历史标签，判据是实的。这条我不替 T1b 解释原因（他说是"R1 多字体生效"，那个归因不在我读到的证据里）。

**"12 行"能追到多少**（这正是我上一轮完全缺的那半）：

* **≥5 行有名有姓**：上表 bucket 2 的 5 个用例，其**首个**失败行就是记账失败行（`Len` 相等、`nl` 相等、**`ws` 0→1**）⇒ 5 个不同用例 ⇒ **≥5 行**；
* **4 行有计数、无名**：`:69 有行尾空白的行 984/988 一致` ⇒ 在"真值 `ws>0`"的行里有 **4 行**失败；这 4 行与上一条的 5 行**互斥**
  （那 5 条真值 `ws=0`，根本不进 `[③]` 的分母）⇒ **另有 4 行**，但**用例名不在任何产物里**（只可引登记 #6
  「`F_*` 家族少量行差，与 #2/#4 同源」`T1b-report.md:202` 作登记引用，我不另判）；
* **余 ≤3 行无任何读数** ⇒ **未判定**，缺的读数 = **行级失败清单**（装置只在**用例级**导出"首个原因"，行级只给 `1286/1298` 与 `[③]` 计数）。

⇒ 记账残差：**9 行有读数可归（5 有名 + 4 有计数）、余 0–3 行未判定、0 条真缺陷**。

### 23.6.4 我主动补的：段2 另外 20 条（LH 组，**不属于主对拍集**）

`:46-65` 的 20 条全是 `LH_cjk_punct_lh10/22/30/50/30_ac1`，标记 `[NotoSansCJK(da)]`：

* **两把尺子都不过**：差 = **−0.8539 / −0.8227 / −0.6661 / −0.6996，全部 > 0.34** ⇒ 与容差口径无关，不是"0.01 vs 0.34"的事；
* **20 行 = 4 个读数**（去重命令实跑 ⇒ 4）：5 个 CJK LH 例 × 每例 4 行，且 LineHeight 档（10/22/30/50/ac1）在**两侧都不改变** Extent；
* **同一次运行的反证**（这条最硬）：非 CJK 的 `LH_lat_words_*` **5 例** Extent **真值 = 18.0800 / 我们 = 18.0800（逐位相等）**
  （全文日志 `:80-84`，5 行全如此）⇒ 与 `:78 逐例一致 Extent 5/10` 完全对应 ⇒ **Extent 残差只出现在 CJK 那 5 例**；
* 真值来自 Windows（MS YaHei），我们用 NotoSansCJK（`:542/:546` **显式指定**）⇒ **代用字体，结构不可比**
  （`:79` 的"（…CJK 例为字体代用：本机无 MS YaHei）"、`:592`、`T1b-report.md:857` 三处登记）。

⇒ LH 组 **结构性不可比 20/20（去重 4 读数），真缺陷 0，未判定 0**。

### 23.6.5 三分支收口表（任务 4）

| 面（出处） | 读数总数 | 结构性不可比 / 已登记 | 真缺陷 | 未判定 | 缺哪条读数 |
| --- | --- | --- | --- | --- | --- |
| 记账 **行级**（`:55` 1286/1298） | **12** | **9–12**（5 有名（bucket 2）+ 4 有计数无名（引登记 #6）+ 同族的后续行） | **0** | **0–3** | 行级失败四元组清单 |
| 折叠 **用例级**（`:58` 298/315、`:115`） | **17** | **17**（bucket 2×5 / bucket 4×7 / bucket 5×5） | **0** | 0 | — |
| 折叠 **行级**（`:91` 218/236） | **18** | **18**（bucket 4×8 / bucket 2×3 / bucket 5×7） | **0** | 0 | — |
| Extent **主对拍集**（`:75` 1260/1298） | **38** | 34（CJK 回退边界，T1d 双向实验先例） | **0**（原 2 条"候选"已由 **§23.7** 核定为（甲）：`TextModifier` 未实现 ⇒ 归已登记 bucket 5；量级证据 `tline-detail-full.txt:58/:59` \|d\|=3.6800） | 0 | — |
| Extent **LH 组**（`:75` 同一行） | **20** | **20**（代用字体；去重 4 读数） | **0** | 0 | — |
| **合计（按读数，不去重）** | **105** | **100–103** | **0**（2 条原"候选"经 §23.7 归入 bucket 5） | **0–3** | 见上 |

**对账**（每一列都要能精确加到总数）：
`105 = 12（记账行级）+ 17（折录用例级）+ 18（折叠行级）+ 38（Extent 主集）+ 20（Extent LH 组）`；
其中 `38 = 34 结构性（CJK 回退）+ 2 已登记（`M_modifier`，§23.7 判（甲））+ 2 容差口径`；`12 = (9–12) 归桶 + (0–3) 未判定`（**两者互补，恒定 12**）。
⇒ **结构性/已登记 = 100–103**，`+ 2（口径）+ (0–3)（未判定）= 105`，**无剩余、无重复计入**。
⇒ 三个分支的口径不混：**容差口径**（`0.08` 两条，在宽尺子内）≠ **结构性/已登记**（与容差无关）≠ **未判定**（缺读数）。
（§23.7 是**只读核查**，只把 §23.6.1/23.6.5 里那 2 条从"候选"改判为"已登记（甲）"，**没有动任何其它计数**。）

**去重后的根因清单（这才是"要修什么"）**：

| 根因 | 涉及读数 | 登记出处 | 是否真缺陷 |
| --- | --- | --- | --- |
| `TextModifier` 未实现 | Extent 4 + 折叠 7 + 记账 5 = **16** | `T1b-report.md:199` | 已登记缺口（B2 不做） |
| 折叠可见前缀边界（起刀差 1 字） | 折叠 8 + 记账 7 = **15** | `T1b-report.md:198` bucket 4（**staging 已修未落**） | 否 |
| NBSP/ZWSP 行尾空白计数 + `cr.Width` 判据 | 折叠 3 + 记账 5–9 = **8–12** | `T1b-report.md:198` bucket 2 + §20.4 `:830` | 否 |
| CJK 回退边界（单字体入口无回退） | Extent **34** | T1d 双向实验（100% 命中 / 0% 误伤） | 否（结构性） |
| 代用字体（本机无 MS YaHei） | Extent LH **20** | `:79`/`:592`/`T1b-report.md:857` | 否（结构性） |
| 记账行级余项 | **0–3** | 缺读数 | **未判定** |

⇒ **去重后：4 个已登记桶 + 1 条 0–3 行的未判定余项 + 0 个新缺陷。**

### 23.6.6 边界、顺带登记、登记更正

* **只读**：`build/MilBridge/gen/*`（产物）、`build/MilBridge/gen/layout-b34-compact.json` 与 `tests/parity/windows/layout-b34/cases.json`
  （**harness 自己的输入**，只为取"边界字符"这一个事实）、`build/MilBridge/tests/HbTextLineParity/Program.cs`（**读源码把口径钉死**）、`build/MilBridge/T1b-report.md`。
  **没改任何文件**：`src/**`、`build/shims/**`、native shim（`wic-shim/**`）、`build/*.Linux/**`、`build/MilBridge/**`、`samples/**`、`tests/**` 全部未动；
  **没跑 harness、没跑应用**；`-m:1`（本轮无构建）；未动外来构建 `wpf2web`。
* **登记更正 1（撤回）**：§23.5.1 "容差嵌套 ⇒ 34 ⊂ 38" —— **机制错**，正确是"34 条 `+CJK` 家族 + 4 条 `M_modifier`"（§23.6.1）。
* **登记更正 2（作废嫌疑）**：§23.2 "Tab = 记账类残差第一嫌疑桶" —— 本轮 17 条里 **0 条 Tab**，且 `T2c` 判据 `tabDiff==0` 在 **34 例 Tab** 上成立（§23.6.3）。
* **顺带登记（不改判、不归因，不在本次三任务内）**：`宽度超差` 本轮 **47 行 / 1298**（`:57`；最大差 282.219333 DIP 仍在 `M_modifier_winf`），
  而 `T1b-report.md:97` 记的是上一代 **83 行 / 1048 行 ≤0.34** —— 两个读数的分母都是 1298、最大差同值，但分桶不同，**我不解释、不当证据用**（与本次三组残差不同源，仅备查）。
* **留给主控的两条（都不是"待跑"，是"缺清单/缺装置"）**：
  ① 折叠那 8 条"起刀差 1 字"里，**3 条多留一个墨字**（ΔW 2.62 / 8.55 / 9.68）**是否已含在** `T1b-report.md:198` 的"staging 已修未落"范围内
     —— 该登记只给"38/236 行"这个**计数**，**那 38 行的清单不在任何产物里**，我**判不了**（要判得先有那份清单，不是再跑一次）；
  ② 记账那 **0–3 行**要点名，需要把装置从"用例级首个原因"扩到"**行级**失败清单"（这是仪表改动，属 T1b 的 lane）。

### 23.6.7 复现命令（本节所有计数都能一条命令复核）

```bash
cd build/MilBridge/gen

# 23.6.1  38 = 34(+CJK) + 4(M_modifier)
sed -n '/^# 段2/,/^# 段3/p' tline-detail-full.txt | grep -v '^#' | grep -v 'NotoSansCJK(da)' | wc -l   # 38
sed -n '/^# 段2/,/^# 段3/p' tline-detail-full.txt | grep -v '^#' | grep -v 'NotoSansCJK(da)' | grep -c '+CJK'  # 34
sed -n '/^# 段2/,/^# 段3/p' tline-detail-full.txt | grep -v '^#' | grep -v 'NotoSansCJK(da)' | grep -v '+CJK' | wc -l  # 4

# 23.6.4  LH 组 20 行 → 4 个读数
sed -n '/^# 段2/,/^# 段3/p' tline-detail-full.txt | grep -c 'NotoSansCJK(da)'                        # 20
sed -n '/^# 段2/,/^# 段3/p' tline-detail-full.txt | grep 'NotoSansCJK(da)' | sed 's/.*真值=/真值=/' | sort -u | wc -l  # 4

# 23.6.2  折叠 18 行里 cr 起相同的只有 3 行（⇒ 15 行起刀点不同）
sed -n '/^# 段1/,/^# 段2/p' tline-detail-full.txt | grep -v '^#' | grep -c 'cr 我们=\[\([0-9]*\),[0-9]*) W=[^|]*|cr 真值=\[\1,'   # 3
sed -n '/^# 段1/,/^# 段2/p' tline-detail-full.txt | grep -v '^#' | wc -l                                                 # 18

# 23.6.3  段3 17 条里 0 条 Tab（并核非空性：compact 里 34 例含 \t 且 fontKey==file）
sed -n '/^# 段3/,$p' tline-detail-full.txt | grep -c 'tab\|Tab'   # 0
python3 -c "import json;cs=json.load(open('layout-b34-compact.json'))['cases'];print(len([c for c in cs if '\t' in c['text'] and c['fontKey']=='file']))"  # 34

# 23.6.4/23.6.6  同一次运行的汇总行（1286/1298、298/315、984/988、218/236、宽度 47）
grep -n '全等 1286\|298 / 315\|984/988\|明细全等 218/236\|宽度超差 47\|通过 19 / 失败 2' tline-detail-20260913-1730.txt
```

## 23.7 `M_modifier_*` 那 4 条 Extent 余差的只读核查：**结论 =（甲）完全由「`TextModifier` 未实现」解释，不是新缺陷**（T2）

**结论一句话**：真值 `18.0000` = **"把 modifier 区间 [6,45) 那 39 个字符的墨迹去掉之后"的墨迹高**；我们给的 `18.0800 / 14.3200` = **把 39 个字当普通文本排出来的墨迹高**。两侧用的是**同一份 upstream 墨迹盒公式**（连 `+2.0` 的 inflate 都是同一行源码），差的只是"修饰符区间的字符该不该产生字形/宽度/墨迹"。**判（甲）**（理由见 23.7.5）。

### 23.7.1 登记原文与适用范围（问题 1）

**出处与原文**（`build/MilBridge/T1b-report.md:199`，§4 登记表第 5 行，逐字）：

> | 5 | **`TextModifier` 未实现** | `M_modifier_*` 5 例 | 真机 modifier 会改文本流（`M_modifier_w80` 真机 2 行 vs 我们 7 行）⇒ **不是断行规则差异** | 登记（B2 范围内不做；oracle 用它只是为了拿非 null 的 `TextLineBreak`） |

**适用范围 = 完全没实现（不是"只实现了部分修饰符"）**，证据清单（`grep` 全仓）：

| 位置 | 命中 | 性质 |
| --- | --- | --- |
| `src/**` | **0 条**（`grep -rna -i textmodifier src/`） | 应用/移植层**完全没有**这个概念 |
| `build/*.Linux/**`（非 shim） | **0 条** | 同上 |
| `build/shims/PresentationCore.HbTextLine.cs`（= 被测源，`EBCCDB1E…`） | **11 处，全是标志，无一处进布局** | 见下 |

那 11 处的逐条性质（我逐处读过）：`:2143 _hasModifierScope = hasModifierScope;`（存标志）、`:2218 if (hasModifierScope) HbTextLineScaffold.NoteModifierLine();`（计数）、`:2231/:2263/:2919/:3057/:3078`（当参数透传）、`:2636`（拼进 `Diagnostics` 串 `modifier=`）、`:2751`（`GetTextLineBreak` 判空）。
**唯一的"桩"**是 `GetTextLineBreak()` 的非 null 分支，`:2757-2759` 逐字：
```csharp
            HbTextLineScaffold.NoteZeroBreakRecordIssued(
                $"HbTextLine.GetTextLineBreak lineStart={_lineStart} length={_length}");
            return HbInternalsFactory.CreateLineBreak();
```
即"发一个**零记录** `TextLineBreak`"（`_breakRecord` 恒 `IntPtr.Zero`，见 `:2730-2732` 的自述）——这是 T5.2 那个测试点。
**结论**：修饰符的**文本语义**（23.7.2(d) 的"空 `CharacterBufferReference`"）在 shim 里**零实现**；`build/MilBridge/staging/` 那份副本同样只有同一个标志 + 计数器（`grep -ca hasModifierScope` = 11，无布局分支）。

### 23.7.2 语料里这 5 例到底用了什么修饰符（问题 2，逐字）

**(a) 用例定义**（`tests/parity/windows/layout-b34/src/LayoutOracle/Cases.cs:290-302`，逐字）：
```csharp
            // ---- M：TextModifier 跨越断行 → 实测 TextLineBreak 的非 null 分支 ----
            // 文本固定用拉丁词串，modifier 覆盖中间一段（跨越换行点）。
            string modText = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
            foreach (double w in new double[] { 80, 120, 200, 320, 1e6 })
            {
                cases.Add(new CaseSpec
                {
                    Id = $"M_modifier_w{Ws(w)}",
                    ...
                    ModifierStart = 6,
```
参数（`tests/parity/windows/layout-b34/cases.json` 的 `M_modifier_w80` 条目逐字）：`textLengthUtf16: 62`、`fontSize: 16`、`fontFamily: "file"`、`textFormattingMode: "Ideal"`、`textWrapping: "Wrap"`、`culture: "en-US"`、`alwaysCollapsible: true`、`modifierStart: 6`、`modifierEnd: 45`。
⇒ 区间 **[6,45) = 39 个字符 = `"bravo charlie delta echo foxtrot golf h"`**（末字是 `hotel` 的 `h`）。

**(b) 用的是哪个修饰符**（`src/LayoutOracle/TextModel.cs:54-66`，逐字）：
```csharp
    internal sealed class OracleModifier : TextModifier
    {
        private readonly int _length;
        private readonly TextRunProperties _props;

        public OracleModifier(int length, TextRunProperties props) { _length = length; _props = props; }

        public override int Length => _length;
        public override TextRunProperties Properties => _props;
        public override TextRunProperties ModifyProperties(TextRunProperties properties) => properties;
        public override bool HasDirectionalEmbedding => false;
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
    }
```
即**用公开抽象类 `TextModifier` 现造的最小修饰符**（`ModifyProperties` 原样返回、无方向嵌入）。

**(c) 文本被切成什么 run**（`TextModel.cs:80-90`，逐字）：
```csharp
        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            if (textSourceCharacterIndex >= _text.Length) return new TextEndOfParagraph(1);
            if (textSourceCharacterIndex < _modStart)
                return new TextCharacters(_text, textSourceCharacterIndex,
                                          _modStart - textSourceCharacterIndex, _props);
            if (textSourceCharacterIndex < _modEnd)
                return new OracleModifier(_modEnd - textSourceCharacterIndex, _props);
            return new TextCharacters(_text, textSourceCharacterIndex,
                                      _text.Length - textSourceCharacterIndex, _props);
        }
```
⇒ 62 字的源被切成 **3 个 run**：`TextCharacters[0,6)` + **`OracleModifier[6,45)`** + `TextCharacters[45,62)`。

**(d) 期望语义 —— 权威在上游源码**（`upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/textformatting/TextModifier.cs:15-28`，逐字）：
```csharp
    /// Specialized text run used to modify properties of text runs in its scope.
    /// The scope extends to the next matching EndOfSegment text run (matching
    /// because text modifiers may be nested), or to the next EndOfParagraph.
    public abstract class TextModifier : TextRun
    {
        /// Reference to character buffer
        public sealed override CharacterBufferReference CharacterBufferReference
        {
            get { return new CharacterBufferReference(); }
        }
```
**`CharacterBufferReference` 是 `sealed override` 且返回"默认构造的空 buffer"** ⇒ **修饰符 run 自己不携带任何字符** ⇒ 不产生字形、不产生 advance、不产生墨迹；它的 `Length` 只让格式化器**跳过 N 个源字符位置**。
这就是真值里那 39 个字**既不算宽、也不算墨迹**的根本原因（不是字体回退，也不是断行规则）。

**该组的用途边界**（`PROVENANCE.md:104` / `:144`，逐字）：
> 它只在 `TextSource` 含 **`TextModifier`**（末 run 有 TextModifierScope）时非 null —— 3222 行里只有 2 行非 null，正是 M 组的 modifier 用例
> 7. `M` 组的 `TextModifier` 只用于证明 `TextLineBreak` 的非空分支，**不是 modifier 语义的完整覆盖**。

### 23.7.3 我们那两个数是怎么来的（问题 3，逐字 + 独立复算）

**(a) `Extent` 的计算路径**：`build/shims/PresentationCore.HbTextLine.cs:2675-2696`（逐字）
```csharp
        public override double Extent
        {
            get
            {
                if (double.IsNaN(_extentCache)) _extentCache = ComputeInkExtent();
                return _extentCache;
            }
        }
        private double ComputeInkExtent()
        {
            Rect box = Rect.Empty;
            foreach (GlyphRun gr in _glyphRuns)
            {
                Rect b = gr.ComputeInkBoundingBox();
                if (b.IsEmpty) continue;
                b.Offset(gr.BaselineOrigin.X, gr.BaselineOrigin.Y);
                box.Union(b);
            }
            return box.IsEmpty ? 0.0 : box.Bottom - box.Top;
        }
```
它调的 `GlyphRun.ComputeInkBoundingBox()` **不是我们写的**：`build/PresentationCore.Linux/PresentationCore.Linux.csproj:543` 把 **upstream 的 `GlyphRun.cs` 原样编进来**（`:132` 同样编入 upstream 的 `CoreCompatibilityPreferences.cs`）⇒ **墨迹公式与真机同源**。
上游公式：`GlyphRun.cs:1434-1491`（逐字形 `EmGlyphMetrics`，`designToEm = _renderingEmSize / _glyphTypeface.DesignEmHeight`，跳过无墨字形），以及 `:1384-1392` 的 inflate：
```csharp
            if (CoreCompatibilityPreferences.GetIncludeAllInkInBoundingBox())
            {
                if (!bounds.IsEmpty)
                {
                    // Inflate bounds
                    double inflation = Math.Min(_renderingEmSize / 7.0, 1.0);
                    bounds.Inflate(inflation, inflation);
                }
            }
```
`_renderingEmSize = 16` ⇒ `inflation = min(16/7, 1.0) = 1.0` ⇒ **每边各 inflate 1.0 ⇒ 高度 `+2.0`**。**两侧的 `+2.0` 是同一行源码**。

**(b) 我用字体文件独立复算**（只读 `build/fonts/NotoSans-Regular.ttf`；纯 python 解 `glyf/loca/cmap`，没装任何库）：
`墨迹高 = (max yMax − min yMin) × emSize / unitsPerEm + 2.0`（`upem = 1000`）：

| 行内容（哪一侧） | glyf 墨迹（最高/最低墨字） | +2.0 | **实测报出值** |
| --- | --- | --- | --- |
| `"bravo "`（**我们** `w80` 行#1） | (760−(−10))×0.016 = **12.3200**（`b`/`b`） | 14.3200 | **14.3200 ✔ 逐位吻合** |
| `"charlie delta "`（**我们** `w120` 行#1） | (760−(−10))×0.016 = **12.3200**（`h`/`c`） | 14.3200 | **14.3200 ✔ 逐位吻合** |
| 全文 62 字（**我们** `winf` 行#0） | (765−(−240))×0.016 = **16.0800**（`f`/`p`） | 18.0800 | **18.0800 ✔ 逐位吻合** |
| 去掉修饰符区间后的可见文本（**真机**各行） | (760−(−240))×0.016 = **16.0000**（`l`/`p`） | 18.0000 | **18.000000000000004 ✔ 逐位吻合** |

⇒ **`±0.0800` 这个差 = `'f'.yMax(765) − 'l'.yMax(760)` = 5 units × 0.016 = 0.08 DIP** —— 就是 **`f` 这一个字**（`foxtrot` 落在修饰符区间内，真机那行没有 `f` 的墨迹，我们有）。
⇒ `w320/winf 行#0` 的 `+0.0800` 与 `w80/w120 行#1` 的 `−3.6800` 因此归到同一机制：**`14.3200` 与 `18.0800` 都是我们这行墨迹的忠实值**（不是 fallback 度量、不是估的），差只在于"这行里有哪个字"。

### 23.7.4 "修饰符区间零贡献"的三条**独立**读数（把（甲）钉死的关键）

三条互不依赖，且都不看我们的实现：

1. **行宽**（与 `Extent` 无关）：真值 `M_modifier_w80` 行#0 `w=74.5733 / witw=78.7333`、行#1（`i=50`）`w=78.1833`。我从 TTF 独立算（无 GPOS kern 的裸和，故容许 ~1 个 1/300 英寸量子）：
   `"alpha otel"` = **74.5760**（vs 74.5733，Δ0.0027）、`"alpha otel "` = **78.7360**（vs 78.7333，Δ0.0027）、`"india juliet"` = **78.1920**（vs 78.1833，Δ0.0087）
   ⇒ **真机那两行的可见宽 = 非修饰符字符的宽**；而修饰符区间那 39 个字的 advance = **283.1680 DIP**，在真机里**完全不出现**。我们这侧 `winf` 行宽 = **439.1360**（全文都在；与 TTF 全文字符裸和 440.0960 差 0.96 = HarfBuzz 的 kern）⇒ **差的就是那一大块 283 DIP**。
2. **行数/断点**：真机 2 行（`i=0 len=50`、`i=50 len=13`）——其行#0 可见宽 `78.7360 ≤ maxWidth 80` 刚刚放得下，断点落在**源索引 50**；我们 7 行（那 39 个字真的占了宽度）。`T1b-report.md:199` 记的"真机 2 行 vs 我们 7 行"正是这一条。
3. **`Extent`**：见 23.7.3 表最后一行。

### 23.7.5 三选一：**（甲）**（写死）

**(甲) 完全由「`TextModifier` 未实现」解释 ⇒ 归已登记缺口、不是新缺陷。** 三条理由：
1. 真值 `18.0000` 与"修饰符区间零贡献"这条**独立于我们实现**的读数一致（23.7.4 的宽度 3 条 + 行数 + `Extent` 全部自洽）；
2. 我们报的 `18.0800 / 14.3200` 用**同源 upstream 公式**（`csproj:543` 编入的 `GlyphRun.cs` + `+2.0` inflate）从字体文件**逐位复算命中**；
3. 差的 `0.08` 恰好等于 `f` 与 `l` 的 `yMax` 之差 ⇒ 属于"这行里有哪个字"，不属于"某个字形被漏算"。

**为什么不是（乙）**：（乙）的形态是"我们的 ink 计算对某种修饰符形态漏算"——**该形态的证据不存在**：三个不同行内容的墨迹值都被 TTF 独立复算命中（12.3200 / 12.3200 / 16.0800），没有任何读数显示"该算墨迹时被漏算"。我也**不把（乙）写成"未排除"留活口**：要构造（乙）必须指出某个实字的墨迹在 `ComputeInkBoundingBox` 里被跳过，而上游的跳过条件（`GlyphRun.cs:1484-1487`：`left+ε ≥ right || top+ε ≥ bottom`）只对**空白字形**成立，本例 39 个字全是实字。

**"若实现它，期望变成 18.0000" —— 我把它从"期望"升级为"可推导"**：把修饰符区间 [6,45) 的字符从该行墨迹并集里去掉 ⇒ 该行墨迹 = `16.0000 + 2.0 = 18.0000`（23.7.3 表第 4 行），与真值**逐位相同**。

**实现它需要两件事（不是一件，估工时请注意）**：
* **① 装置侧无通道**：harness 只把 `modifierStart` 折成**一个 bool** —— `build/MilBridge/tests/HbTextLineParity/Program.cs:251-252`：
  ```csharp
                bool hasModifier = meta.ValueKind != JsonValueKind.Undefined &&
                                   meta.TryGetProperty("modifierStart", out JsonElement ms) && ms.ValueKind != JsonValueKind.Null;
  ```
  然后 `:274` 只把它当第 9 个实参传给 `FormatParagraph(...)`（进 shim 后只喂 `GetTextLineBreak`）。**区间 [6,45) 根本没进被测代码** ⇒ 要真覆盖，得让 harness 送 **TextRun 级模型**（`TextCharacters`/`TextModifier` 分段）；这与 `T1b-report.md:199`"oracle 用它只是为了拿非 null 的 `TextLineBreak`"、`PROVENANCE.md:144`"不是 modifier 语义的完整覆盖"是同一件事；
* **② shim 侧无实现**：需要一条"落在修饰符区间内的字符不产生字形/advance/墨迹"的路径（对应上游 `TextModifier.CharacterBufferReference` 的空 buffer）；现有 `_hasModifierScope` 只喂 `GetTextLineBreak`（11 处，见 23.7.1）。

**连带**：`M_modifier` 的另外两类读数**同根**——折叠 7 行（`行数 7≠2` 等 + `折叠明细不符`）与记账 5 条（`行数 7≠2 / 5≠2 / 3≠1 / 2≠1`）都由 23.7.4 第 2 条（断点/行数）决定 ⇒ **§23.6.5 的"根因去重 = 1 个已登记缺口"由此坐实**，它们不是各自独立的缺陷。

### 23.7.6 附带确证（顺手，不属本次任务）：`cr.Width` 那一列是什么

读 shim 时顺带看到 `build/shims/PresentationCore.HbTextLine.cs:2772`（逐字注释）：

> `⑥ collapsedRange = { 行起点 + 可见长, 原Length − 可见长, 原Width − 折后Width }`

⇒ **`cr.Width` = 原行宽 − 折后行宽**（"折叠拿掉/换进去多少宽"），因此**可以为负**（`F_nbsp_zwsp_w40#3` 我们 `-3.0560`：折后比原来还宽，即省略号比被替掉的文本宽）。这把 §23.6.2 里"`cr.Width` 是另一本账"从观察升级为**定义级依据**，并解释了负号来源；**不改 §23.6 的任何分桶结论**。

### 23.7.7 边界与复核命令

* **只读**：`build/MilBridge/gen/layout-b34-compact.json`、`tests/parity/windows/layout-b34/{cases.json, PROVENANCE.md, src/LayoutOracle/Cases.cs, src/LayoutOracle/TextModel.cs}`、`upstream/.../textformatting/TextModifier.cs`、`upstream/.../Media/GlyphRun.cs`、`build/PresentationCore.Linux/PresentationCore.Linux.csproj`、`build/shims/PresentationCore.HbTextLine.cs`、`build/MilBridge/tests/HbTextLineParity/Program.cs`、`build/fonts/NotoSans-Regular.ttf`（**纯 python 解表，未装任何库**）。
* **没改任何文件**（`src/**`、`build/shims/**`、native shim、`build/*.Linux/**`、`build/MilBridge/**`、`samples/**`、`tests/**` 全未动）；**没跑 harness / 应用**（T1b 正在跑 `tline`，我没撞）；`-m:1`；未动外来构建 `wpf2web`。
* **引用锚定（现场重读，重要）**：本节的读数属于**被测那一版** `build/shims/PresentationCore.HbTextLine.cs` = `EBCCDB1E…`（**206,286 B**，mtime `09-13 00:36`），与 §23.6 的产物同版。
  我核对时发现该文件**已在我读完之后被改动**（现为 `13992b58905d00e0aea8736c14a94836515f9d4355e2db75dc12d1af00a89e91`，**212,009 B**，mtime `09-13 20:24`）——这是别人的 lane，我**没碰**。
  **我逐条复验了引用的健壮性**：`_hasModifierScope`、`Extent`、`ComputeInkExtent`、`ComputeInkBoundingBox()` 调用、`CreateLineBreak()`、`collapsedRange = {…}` 在新版里**行号与文本都对得上**（`:2143 / :2675 / :2685 / :2690 / :2759 / :2772` 一致）⇒ 新版改动落在别处，**本节所有 `file:line` 对新旧两版同时有效**；`T1b-report.md:199` 的登记行我也重读过，仍在 `:199`。（要跑下一次读数时，请以届时 `run.sh` 实读的 sha 为准。）
* 复核命令（本节的 grep / 复算都能重放）：
```bash
# ① 登记原文 + 适用范围
sed -n '199p' build/MilBridge/T1b-report.md
grep -rna -i textmodifier src/ | wc -l                                    # 0
grep -na "hasModifierScope" build/shims/PresentationCore.HbTextLine.cs    # 11 处，全非布局用途
# ② 语料与语义
sed -n '290,302p' tests/parity/windows/layout-b34/src/LayoutOracle/Cases.cs
sed -n '54,66p;80,90p' tests/parity/windows/layout-b34/src/LayoutOracle/TextModel.cs
sed -n '15,28p' upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/textformatting/TextModifier.cs
# ③ 公式与 inflate（+2.0 的来源）
grep -n "GlyphRun.cs" build/PresentationCore.Linux/PresentationCore.Linux.csproj        # :543
sed -n '1384,1392p' upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/GlyphRun.cs
# ④ 装置侧只有 bool（区间没传进来）
sed -n '251,252p;274p' build/MilBridge/tests/HbTextLineParity/Program.cs
```

## 24. app-local 副本一致性：口径修法 + 结构修法 + 两极化实测（T2，2026-09-14）

**派单**：校验器报 `APPSYNC=MISMATCH（MISMATCH=9 DIVERGENT=3）`，逐条判"真发散 / 口径错 / 设计允许"，给结构修法（不是手工 `cp`）并把牙做成两极化，目标 `APPSYNC=PASS`。

| 面 | 修前 | 修后（00:31） |
| --- | --- | --- |
| 我车道 `build/DirectWrite.Linux/**` | 5 份真发散 + 参与 2 个 DIVERGENT 组 | **`APPSYNC=PASS`（exit 0；`SCAN_ROOTS=build/DirectWrite.Linux`）** |
| 全仓 | `MISMATCH=9 DIVERGENT=3` | `MISMATCH=2 DIVERGENT=3`（**3 份陈旧副本，全在别人车道**，逐条见 24.5） |

我车道 6 份陈旧副本**全部由构建收敛**（`0aa503df` / `0939c055` / `870c9504`×2 / `ef6cc9d3` → 权威 `71ba86c6495347fe`），**没有一次手工 `cp`**。

### 24.1 逐条真值表（修前那一跑：`MISMATCH=9 DIVERGENT=3`）

判定依据一律写"哪条代码 / 哪条设计契约"，不写"我觉得"。

| # | 类 | 路径（副本） | 副本 sha / 年龄 | 权威 | **判定** | **判定依据** |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | MISMATCH | `build/DirectWrite.Linux/WicWriteClosedLoop/bin/Debug/DirectWrite.Linux.Provider.dll` | `0939c055`（09-11 10:15） | `71ba86c6` | **真发散（已修）** | 该工程用 `HintPath + Private=true` 引 Provider ⇒ 语义 = **消费者构建时拷一次**（主控 2026-09-10 裁定，`WiringSmoke.csproj:6-11` 原文）；副本年龄 = 它最后一次构建时刻，权威之后变了就停在旧代 |
| 2 | MISMATCH | `build/DirectWrite.Linux/WicClosedLoop/bin/Debug/…Provider.dll` | `0aa503df`（09-11 18:01） | 同上 | **真发散（已修）** | 同上（`WicClosedLoop.csproj:31` 逐字 `<Private>true</Private>`） |
| 3 | MISMATCH | `build/DirectWrite.Linux/WiringSmoke/bin/Debug/…Provider.dll` | `870c9504`（09-10 17:40，93,184 B） | 同上 | **真发散（已修）** | 同上；尺寸 93,184 B 也证明它是**旧一代实现**（权威 110,080 B），不是别的东西 |
| 4 | MISMATCH | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/…Provider.dll` | `ef6cc9d3`（09-10 15:30，68,608 B） | 同上 | **真发散（已修）** | 该工程走 `ProjectReference`→PC/PF（`…SystemFontsProbe.csproj:45/48`），PC/PF 的 `Private=true` 把 Provider 传递拷进探针目录 ⇒ 同样是"消费者构建时拷一次" |
| 5 | MISMATCH | `build/DirectWrite.Linux/FontEntryClosedLoop/bin/Debug/…Provider.dll` | `870c9504`（09-10 17:40） | 同上 | **真发散（已修）** | 同 #1（同形 HintPath） |
| 6 | MISMATCH | `build/PresentationFramework.Classic.Linux/bin/Debug/…Provider.dll` | `ef6cc9d3`（09-10 15:30） | 同上 | **设计允许（→ 新类 `LIB-COPY`）** | 该输出目录**没有 `*.runtimeconfig.json`**（实测 `runtimeconfig=0`；5 个探针 / samples / tests 宿主都是 1）⇒ **不是启动宿主**；库输出目录里的私有依赖副本**没人加载**（没有任何脚本从这个目录启动）⇒ 不参与"加载源"判定。**不是放宽**：仍逐行打印 sha，且**同名的宿主目录副本照样判红**（牙 H 就是这条的两极化） |
| 7 | MISMATCH | `samples/HelloWpf/bin/Debug/net10.0/…Provider.dll` | `a3c026c5`（09-11 18:46，109,568 B） | 同上 | **真发散（未修：越界）** | `runtimeconfig=1` ⇒ 是宿主 ⇒ 加载源；早 5,523 秒。**`samples/**` 是本次派单的禁区**（"不碰 src/**、build/shims/**、samples/**"）⇒ 只报不改，修法见 24.3/24.5 |
| 8 | MISMATCH | `build/MilBridge/.artifacts/bin/MilBridge.Linux/release_linux-x64/WpfGfx.Linux.dll` | 修前 `8757bb0d`（323,072 B）→ 现 `d91a3b1f`（323,584 B，00:21:51） | `1b63203e`（**Debug**，351,232 B @00:14:27） | **口径错（跨配置比）** | 它与 `.artifacts/{obj,bin}/WpfGfx.Linux/release/…` **逐字节同 sha、同尺寸** ⇒ 是**同一份 Release 产物**（Release 323,584 B ≠ Debug 351,232 B，尺寸就把两类分开了）；权威只按 **Debug** 定义（ITEMS 表）。旧分类器只认 `*/bin/Release/*` 与 `*/release/*`，**漏了 RID 目录名 `release_linux-x64`** ⇒ 已补 `*/release_*/*`（24.2） |
| 9 | MISMATCH | `src/WpfGfx.Linux/obj/Debug/net10.0/WpfGfx.Linux.dll` | 修前 `ef99d09f`（22:38）→ 现 `d77e5548`（00:21:05） | 同 #8 的 Debug 权威 | **口径错（拿中间件当 bin）** | `obj/` 是编译器工作目录：**不是加载源**（没有任何启动路径指向 obj），且它与 bin **同尺寸 351,232 B 而 sha 不同**、当时 **obj 比 bin 新 21 分钟**（22:38 vs 22:17）⇒ 把它当 bin 比只产生噪音、不产生安全。同类先例：`obj/*/ref`、`refint` **早就**是 `SKIP(ref)`（同一脚本）⇒ 这次只是把同一原则补齐到 `obj/**` |
| 10 | DIVERGENT | `DirectWrite.Linux.Provider.dll [Release]` 2 种 sha：`build/MilBridge/tests/FamilyCoverageSelfTest/bin/Release` `a3c026c5`(09-11 18:46) vs 另外 **10 份** `71ba86c6`(09-11 20:18) | — | — | **真发散（未修：T3 期间不动 Release 探针）** | 同 #1 机制；且那 10 份的 sha = **Debug 权威** sha ⇒ 本仓设计就是"Release 目录里放 Debug 权威"（`build/MilBridge/tests/Directory.Build.targets` 的 `SyncProviderAuthority` 逐字）⇒ 落单那份就是**落后一代** |
| 11 | DIVERGENT | `DirectWrite.Linux.Provider.dll [Debug]` 2 种 sha：`samples/HelloWpf/bin/Debug/net10.0` `a3c026c5` vs 另外 **10 份** `71ba86c6`（修前落单者还有我车道 5 份，已归位） | — | — | **真发散（未修：越界，同 #7）** | 同 #7 |
| 12 | DIVERGENT | `WpfGfx.Linux.dll [Debug]` 2 种 sha：`tests/WpfGfx.Linux.Tests/Commands.Tests/bin/Debug/net10.0` `08806714`(00:13:44) vs 另外 **6 份** `1b63203e`(00:14:27) | — | — | **真发散（未修：并发构建竞态 + 别人车道）** | 差 **43 秒**：00:14:27 那次重建把 6 个目录刷成同一 sha，`Commands.Tests` 停在 00:13:44 ⇒ **正是"副本年龄 = 消费者最后一次构建时刻"的直接证据**；此刻 `cp` 进可能被 T3 进程映射的输出目录有撕票风险 ⇒ 只报不改 |

**口径排除掉的两类（都不算不一致，且都逐行打印 sha）**：`build/CycleStub.{PresentationFramework,PresentationUI}.Linux/bin/Debug/…Provider.dll`（循环桩工程）。
`ReachFramework.dll` 那个**真·桩件**我单独核过（24.6）：`build/CycleStub.ReachFramework.Linux/{bin,obj}` 里同名件只有 **5,120 B `067c03858367`**，而权威 741,376 B `f64b76d4` ⇒"同名即同类"必然误报。

### 24.2 口径修法：十类分开，**每条为什么这样判**

改的是我车道的唯一校验器 `build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh`（sha `d92fcf31eb5a6323…`）。

| 类 | 判据（代码） | **为什么这样判** |
| --- | --- | --- |
| `OK` | 与权威同 sha | 基线 |
| `STALE` | sha≠权威 **且** 副本 `mtime <` 权威 `mtime` | 这是 `HintPath+Private=true` 的**唯一**落后形态：权威之后重建、消费者没重建。报"落后 N 秒"而不是光报 sha 不同 —— 与 T1c §32 的定性同口径 |
| `NEWER-DIFF` | sha≠权威 **且** 副本 `mtime ≥` 权威 `mtime` | **仍然红**，只是措辞不同：这时"谁旧"没定论（可能权威落后、也可能是漏网的跨配置/桩件）。**mtime 只用来分类，不用来放行** —— 防假绿的底线（脚本头部逐字写着） |
| `NO-AUTHORITY` | 匹配 `*/bin/Release/*`、`*/release/*`、**`*/release_*/*`（新补）** 且权威是 Debug | 权威只按 Debug 定义 ⇒ Release/RID 副本没有可比对象 ⇒"判不了"不许冒充"不一致"（主控 2026-09-11 裁定）。**补 RID 目录名**修的就是 #8 那条误报 |
| `LIB-COPY`（新） | 托管件且其输出目录**无 `*.runtimeconfig.json`** | 判据是"**这个目录能不能被启动**"，不是路径里有没有 `bin`。库输出目录里的私有依赖副本不是加载源（#6）。**原生 shim 不适用此条**（`libwpfwic.so`/`libwpfwin32.so` 的加载路径 shadowing 是本项目事故源头，一律照判） |
| `SKIP(obj)`（新） | 路径含 `/obj/` | 编译器工作目录；与 bin 不必一致、也不是加载源（#9）。与既有 `SKIP(ref)` 同一原则，只是补齐 |
| `SKIP(stub)`（新） | 路径含 `/CycleStub.` | 循环桩工程**按构造就是另一个程序集**（5,120 B vs 741,376 B）⇒ 不比 |
| `SKIP(ref)` | `obj/*/ref*` | 原样保留（引用程序集） |
| `RETIRED` | `libole32.dll.so` 存在即错 | 原样保留 |
| 分组 `DIVERGENT` | 同名 + 同配置 + **同类加载源**之间 sha 不齐 | 不需要权威就能抓"落后一代"；**排除 obj / 桩件 / 库输出**（否则会把"按构造不同"当成"不一致"—— 正是主控点名的 5,120 B 陷阱） |
| 总判定 | `MISMATCH>0 或 DIVERGENT>0 或 RETIRED>0` ⇒ MISMATCH + exit 1 | **一个字没放宽**：`STALE`/`NEWER-DIFF` 仍然红；新类只承接"按构造就不是同一个东西"的那些 |

**已知口径缺口（本轮故意不改，每次运行都打印一行提示，防静默）**：跨副本分组用 `find -path "*/$cfg/*"`（**大小写敏感**），而逐副本判定是大小写不敏感的 ⇒ 小写 `release/`、`release_linux-x64/`（含 `.artifacts/**`、MilBridge release 输出）**不进分组**。改成 `-ipath` 是一行的事，但会**新增一批别人车道的红**（且 T3 正在跑批次）⇒ 登记在脚本头部 + 每次运行打印，**由主控决定何时开**（这是"加覆盖"不是"放宽"，但时机不该由我一个人定）。
> ✅ **主控 2026-09-14 决定：暂不改** —— 保持大小写敏感分组 + **每次运行打印这条缺口**。理由（主控原话精神，照录）：改成 `-ipath` 会一次引入一批**别人车道**的红，而**现在没有 owner 去清它们**；等 **3.6 这道刷新步骤在真实波里跑过一遍、且 `release_*` 目录的归属明确之后**再翻。**下一个人不要以为这是漏改** —— 它是登记过的、有 owner 的待办（见 §24.8.5）。
另：`SCAN_ROOTS` 默认不含 `tools/**`；实测 `tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll` = `16baacfc`（09-11 09:21，288,768 B）也是旧代 ⇒ 一并登记为覆盖边界。

### 24.3 结构修法（落地在我车道，不是手工 `cp`）

**(a) 构建期同步**：新增 `build/DirectWrite.Linux/Directory.Build.targets`（sha `dd081aebd7d926e4…`），target `SyncProviderAuthority`（`AfterTargets="Build"`、`SkipUnchangedFiles="false"`），与 `build/MilBridge/tests/Directory.Build.targets` 同思路（那份是 T1b 车道的，本次没动）。三个设计点写进了文件注释：
* **只对"启动宿主"生效**：`'$(OutputType)'=='Exe' Or Exists('$(OutDir)$(AssemblyName).runtimeconfig.json')` —— 与校验器 `LIB-COPY` **同一把尺子**（不动库输出、不制造"看起来同步了"的假象）；
* **不会自拷**：`!String.Equals(GetFullPath(TargetPath), GetFullPath(权威路径))` ⇒ Provider 工程（权威生产者、库）被两个条件排除；
* **不依赖增量**：即使副本 mtime 不比权威旧，只要 sha 不是权威也刷。
权威路径可用 `/p:ProviderAuthorityPath=…` 覆盖（两极化实验靠它）。

**实测收敛**（`dotnet build … --no-restore -p:BuildProjectReferences=false -p:UseSharedCompilation=false -nodeReuse:false`，`-m:1`，逐个 rc=0）：

| 工程 | 副本 sha 修前 → 修后 |
| --- | --- |
| `WicClosedLoop` | `0aa503df43516880` → **`71ba86c6495347fe`** |
| `WicWriteClosedLoop` | `0939c055d0d5affd` → **`71ba86c6495347fe`** |
| `WiringSmoke` | `870c9504dc70532b` → **`71ba86c6495347fe`** |
| `SystemFontsProbe` | `ef6cc9d317400a7b` → **`71ba86c6495347fe`** |
| `FontEntryClosedLoop` | `870c9504dc70532b` → **`71ba86c6495347fe`** |

> 用 `-p:BuildProjectReferences=false` 是**故意的**：`SystemFontsProbe` 有 ProjectReference→PC/PF，若让依赖也重建就会写 `build/PresentationCore.Linux/bin/Debug/**`——那正是 T3 批次在用的件。这条约束下只写了我车道自己的输出目录。

**(b) 刷新脚本（给集成波用，判据不重复）**：新增 `build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh`（sha `80eb3eb88c9860de…`）。**它自己不做判据**：先跑校验器，只执行校验器的结论 ——
* 路 1：`STALE` 的副本 ⇒ 从**校验器自己报出的权威路径**刷成权威；
* 路 2：`DIVERGENT` 的**落单者**（sha≠权威 **且** mtime 早于权威）⇒ 同一条"落后"，只是先用分组信号发现；
* `NEWER-DIFF` **只告警不盲拷**（不让"新件被旧件盖掉"）；`obj / 桩件 / 库输出 / 无权威` 一律不碰；**绝不删除**；每次覆盖后**当场复算 sha 并断言等于权威**；
* 默认**干跑**（一个字节都不写），`--apply` 才写；末尾并排打印"刷新前 exit / 刷新后 exit / APPSYNC 行"；
* 干跑实测（本轮 00:30）：精确列出 **3 条**（就是 24.5 那 3 份，全在别人车道）。

**(c) 建议接进 `build/integration-wave.sh` 的片段（该文件是主控的，我没改）**：插在 **3. 重建主循环的 `done`（现 342 行）之后、`# ---- 4. 身份一致性自检`（现 344 行）之前**：

```bash
# ------------------------------------------------------------ 3.6 app-local 副本刷新（债务 #20）
# 【为什么在这里】app-local 副本的刷新时机 = 消费者自己被构建时；波的主循环刚重建完消费者
#   ⇒ 此刻做一次"同类 + 落后 ⇒ 刷成权威"，全仓 app-local 就与权威件收敛。
# 【口径唯一】判据在 check-applocal-sync.sh；本脚本只执行它标 STALE / DIVERGENT 落单者的那些。
step "3.6/5 app-local 副本刷新（权威件 → 落后的加载源副本）"
SYNC="$REPO/build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh"
if [ -f "$SYNC" ]; then
    bash "$SYNC" --apply || fail=$((fail + 1))
else
    echo "  ⚠️ 缺 $SYNC ⇒ **无信息**（不等于通过）"; fail=$((fail + 1))
fi
```
**它打印什么**（`--apply` 时逐条）：`REFRESH <相对路径> <before16> → <after16>（权威 <auth16>；副本曾早 N 秒）`，落单者标 `REFRESH(group)`；不刷的标 `WARN …副本不早于权威…⇒ 不盲拷，需人判`；末尾 `APPSYNC-REFRESH=refreshed=N newer=M applied=1` + 校验器的 `APPSYNC=…` 行 + `刷新前 exit / 刷新后 exit`。

### 24.4 牙：两极化**实测**（三条，都不是"只会红"或"只会绿"的检查）

| 牙 | 做法 | 期望 | 实测 |
| --- | --- | --- | --- |
| **A–H 沙箱自检** | `check-applocal-sync.sh --selftest` | 8/8 PASS | ✅ `SELFTEST_A..H=PASS` / `SELFTEST=PASS`（exit 0）。新增四条正是这次的口径：**E** 权威件换 sha ⇒ 红、还原 ⇒ 绿（`rc_同sha=0 / rc_换sha=1`）；**F** 5,120 B 同名桩件 ⇒ `SKIP(stub)` 且 `APPSYNC=PASS`；**G** obj 只 `SKIP(obj)`、**同内容的 stale bin 副本照样红**（防放宽后变瞎）；**H** 库输出目录 1 份不判、**同一份副本放进宿主目录就被判红** |
| **真树：打断** | 把我车道一份副本弄坏（`WiringSmoke/bin/Debug/…` 追加 2 字节，sha `71ba86c6→dbd3c395`） | 红 | ✅ `NEWER-DIFF … EXPECT 71ba86c6 ACTUAL dbd3c395` + `DIVERGENT` + `APPSYNC=MISMATCH`，exit 1 |
| **真树：结构愈合** | `dotnet build` 该工程（走 `SyncProviderAuthority`，**无手工 cp**） | 绿 | ✅ 副本回 `71ba86c6`、该行变 `OK`、`SCAN_ROOTS=build/DirectWrite.Linux` ⇒ **`APPSYNC=PASS`**，exit 0 |
| **权威换 sha → 还原** | 用**临时权威**（权威件截断成 2,048 B 放进 `$tmp` 镜像树，`AUTH_ROOT=$tmp`；**不碰真权威**，因为 T3 正在用） | 先绿 / 换后红 / 还原绿 | ✅ 基线 `APPSYNC=PASS` exit 0 → 换后 **7 份 `STALE` + `APPSYNC=MISMATCH` exit 1** → 还原 `APPSYNC=PASS` exit 0 |

> **为什么权威换 sha 用 `AUTH_ROOT` 而不是直接改真件**：真权威是**活件**（T3 批次正在跑，探针会加载它）。`AUTH_ROOT` 走的是**同一段判定代码**，等价性更高，且不会把正在被使用的二进制换成截断件。

### 24.5 现状、残项与"谁能一条命令修掉"

> ✅ **本节已被 §24.8 关闭**：主控 2026-09-14 批准刷新这 3 份 ⇒ 09:14 实测**全仓 `APPSYNC=PASS`（exit 0，`MISMATCH=0 DIVERGENT=0`）**。下表保留为"当时的状态 + 当时的理由"，便于核对。

**我车道**（`SCAN_ROOTS=build/DirectWrite.Linux`，00:31:09）：`APPSYNC=PASS`，exit 0。
**全仓**（00:31:06）：`OK=25 MISMATCH=2（STALE=2） DIVERGENT=3 NO-AUTHORITY=17 LIB-COPY=6 SKIP(obj)=4 SKIP(stub)=2 SKIP(ref)=8 RETIRED=0` ⇒ `APPSYNC=MISMATCH`，exit 1。**3 份陈旧副本**（2 行 STALE + 3 个 DIVERGENT 组，其中 2 份同时出现在两处）：

| # | 副本 | 现状 | owner | 一条命令的结构修法 | 我为什么此刻不动 |
| --- | --- | --- | --- | --- | --- |
| 1 | `samples/HelloWpf/bin/Debug/net10.0/DirectWrite.Linux.Provider.dll` | `a3c026c5`，早 5,523 秒 | samples 车道 / 主控 | 跑一次 `sync-applocal-authority.sh --apply`（或重编 HelloWpf） | **`samples/**` 是本次派单的禁区** |
| 2 | `tests/WpfGfx.Linux.Tests/Commands.Tests/bin/Debug/net10.0/WpfGfx.Linux.dll` | `08806714`，早 43 秒 | tests 车道 / 主控 | 同上（或重编该测试工程） | 换掉**可能正被 T3 进程映射**的二进制有撕票风险（并发竞态造成，见 #12） |
| 3 | `build/MilBridge/tests/FamilyCoverageSelfTest/bin/Release/DirectWrite.Linux.Provider.dll` | `a3c026c5`，早 5,523 秒 | T1b / 主控 | 同上（或重编该探针） | 主控明令：**T3 批次期间别跑会同步 Provider 的 Release 探针** |

⇒ **我没有把这 3 条改判成"设计允许"**：它们按契约就是真发散（宿主目录 + 同配置 + 落后），只是**不在我车道**且**此刻修有风险**。
**预测（可核）**：这 3 份刷成权威后 ⇒ 2 行 STALE 消失、3 个 DIVERGENT 组各自变 `CONSISTENT`（组内其余成员已全是权威 sha），且已确认**没有别的同类陈旧副本** ⇒ 全仓 `APPSYNC=PASS`。确认方式：主控批准/接线后跑一次 `bash build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh --apply`。
**另外**：只要别的车道还在并发重建，这类 43 秒级的落后会**反复出现** —— 这正是把刷新放在波脚本"重建主循环之后"的原因（`--apply` 幂等）。

### 24.6 `ReachFramework.dll` 定性落档（主控转来 T1c §32；下表是我**自己现场量**的）

| 件 | 大小 | sha | mtime | 是启动宿主？ |
| --- | --- | --- | --- | --- |
| **权威** `build/ReachFramework.Linux/bin/Debug/ReachFramework.dll` | 741,376 | `f64b76d43d8a7feb` | 09-13 22:24:20 | — |
| 权威 `…/obj/Debug/ReachFramework.dll` | 741,376 | `f64b76d43d8a7feb` | 09-13 22:24:20 | —（与 bin **同 sha** ⇒ 工程本身不落后，T1c §32 ① 复核一致） |
| `build/PresentationFramework.Linux/bin/Debug/…` | 741,376 | `daf9b6f073f6f6bb` | 09-13 22:23:22 | **否**（库输出，`runtimeconfig=0`）⇒ 按新口径 `LIB-COPY` |
| `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/…` | 741,376 | `daf9b6f073f6f6bb` | 09-13 22:23:22 | 是（**我车道也有这一类**；T1c 的四份清单里没列它） |
| `samples/WpfFeatureProbe/bin/Debug/net10.0/…` | 741,376 | `daf9b6f073f6f6bb` | 09-13 22:23:22 | 是 |
| `samples/WpfTextDemo/bin/Debug/net10.0/…` | 741,376 | `daf9b6f073f6f6bb` | 09-13 22:23:22 | 是 |
| `tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/bin/Debug/net10.0/…` | 741,376 | `daf9b6f073f6f6bb` | 09-13 22:23:22 | 是 |
| `samples/HelloWpf/bin/Debug/net10.0/…` | 741,376 | `00723a05d843140b` | 09-11 19:02:42 | 是 |
| `build/PresentationFramework.Classic.Linux/bin/Debug/…` | 741,376 | `e88196688ecddd75` | 09-10 15:07:40 | 否（库输出） |
| `samples/HelloWpf/bin/Release/net10.0/…` | 741,376 | `e88196688ecddd75` | 09-10 15:07:40 | 是（Release） |
| `build/CycleStub.ReachFramework.Linux/{bin,obj}/…`（**桩件**） | **5,120** | `067c03858367d880` | 09-10 14:49:45 | 否 |

⇒ **我把这条定性变成了口径**（不是只抄一遍）：**同类比同类**（`SKIP(stub)` 排除循环桩、`LIB-COPY` 排除库输出）+ **app-local 陈旧按 mtime 说清**（`STALE` 报"早 N 秒"，`NEWER-DIFF` 另算且**仍红**）+ **排除 obj 中间件**。三处的"为什么"在 24.2 表里逐条写了。
（与 T1c §32 的两处小差异：我数到 `daf9b6f0` **五**份（多出 `build/PresentationFramework.Linux/bin/Debug` 那份库输出）与 09-10 `e8819668` **两**份（第三份大概是 5,120 B 桩件）。）
⇒ **覆盖决定（明确写出来，可随时翻开）**：`ReachFramework.dll` **本轮没有加进 ITEMS 表**。理由：它的 6 份宿主副本**全在别人车道**且**现在确实陈旧**，加进来只会让全仓长期报红，而修它必须跑刷新（写 `samples/**`、`tests/**`、`build/PresentationFramework.Linux/**`）。**前置条件**：集成波接线 + 主控批准刷新。翻开只需在 ITEMS 表加一行（照抄现有格式）：
```
 "ReachFramework.dll|$REPO/build/ReachFramework.Linux/bin/Debug/ReachFramework.dll|框架件（Debug 权威）"
```
> ✅ **主控 2026-09-14 决定：暂不纳入** —— 同 `-ipath` 的理由（6 份宿主副本都在别人车道且确实陈旧，纳进来只会长期红）。**"翻开只需加一行"的原文位置就保留在这段下面**（上一段代码块），等有 owner 能清那 6 份时再开。**这不是漏改**（见 §24.8.5）。
（我车道那份 `SystemFontsProbe` 副本会在下次构建时由 (a) 的 target 顺手刷新为权威 —— 但**权威路径按上面这行**，因为它的来源是 PF 的输出副本而不是框架自己的 bin。）

### 24.7 边界与现场（诚实登记）

* **改动只有我车道的 3 个文件**：`check-applocal-sync.sh`（`d92fcf31eb5a6323…`）、`sync-applocal-authority.sh`（`80eb3eb88c9860de…`，新增）、`Directory.Build.targets`（`dd081aebd7d926e4…`，新增）+ 本报告。**没碰** `src/**`、`build/shims/**`、`samples/**`、`build/MilBridge/**`、`build/publish-milbridge.sh`、`build/integration-wave.sh`。
* **跑构建前的确认**（派单要求）：`pgrep` 显示在跑的全是**外来构建 `wpf2web`**（`/home/links-dev/netTest/wpf2web_handoff_20260906/…`，`tools/locked.sh` + flock 保护），**没有我们的应用在跑**；`loadavg` 7.08/9.73/7.14（3 核，外来负载）。全程 `-m:1`、`--no-restore`、`-p:BuildProjectReferences=false`、`-p:UseSharedCompilation=false -nodeReuse:false`；**没杀** `wpf2web`；**没碰** T3 的 `:97`/`:99`。
* **过程中踩到并如实登记的坑**：① 我写的 `Directory.Build.targets` 头一版在 XML 注释里带了开关原文（含连续两个连字符）⇒ MSB4024，5 个构建全 rc=1；改掉后 rc=0（**教训写进文件注释**：注释里不写命令行开关原文）。② 校验器旧版在 NO-AUTHORITY 分支 `shopt -s nocasematch` 只在自己分支里 unset ⇒ 该全局开关**泄漏到后续所有匹配** ⇒ 改成全程 `${1,,}` 小写化，不用 nocasematch。③ 我第一版刷新脚本在干跑时把同一份文件列了两次（路 1 + 路 2）⇒ 已按"路 1 处理过的不再列"去重（干跑 5 行 → 3 行）。
* **读数时间戳**：修前 `00:29:00`；修后全仓 `00:31:06`；我车道 `00:31:09`；`obj`/`.artifacts` 两个口径错件的现场值取于 `00:31:36`。**期间别的车道在并发重建**（`WpfGfx.Linux.dll` 权威 `1b63203e` @00:14:27、`.artifacts` release `d91a3b1f` @00:21:51 都是这几分钟内出现的）⇒ 任何"全仓 PASS"的读数都有保鲜期，这也是把刷新接进波脚本的理由。

### 24.8 接线确认 + 残项刷新 + 决定落档（2026-09-14 09:14）

#### 24.8.1 主控已把 3.6 接进波脚本（我核过，只读）

`build/integration-wave.sh` sha `3be3d2a2b62aaf2b5e7239a8…`；`bash -n` 通过。实际接线（**逐字**）：

```bash
step "3.6/5 app-local 副本刷新（权威件 → 落后的加载源副本）"
SYNC_APPS="$REPO/build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh"
if [ -f "$SYNC_APPS" ]; then
    bash "$SYNC_APPS" --apply || { echo "  ❌ 副本刷新失败 ⇒ 计为失败（宁可红，也不让探针测旧件）"; fail=$((fail + 1)); }
else
    echo "  ⚠️ 缺 $SYNC_APPS ⇒ **无信息**（不等于通过）"; fail=$((fail + 1))
fi
```
* **位置**（:377-383）：在 `3.5 生成物身份指纹` 之后、`# ---- 4. 身份一致性自检` 之前。与我建议的"重建主循环 `done` 之后"**等价**（3.5 只写指纹文件、不重建任何工程），且我核过**不污染相邻两步**：本步骤只写 4 类 ITEMS 的 app-local 副本，**不写** 4/4 身份自检要读的那 5 个自产程序集（`System.Xaml/WindowsBase/PresentationCore/PresentationFramework/DirectWriteForwarder` 的 `bin/Debug`），也不碰 `wave_fp` 的**源**指纹面（它只看手写输入）。
* 变量名用了本脚本既有约定 `$REPO`（我建议稿里写的是 `$REPO`，一致）；失败分支比我的建议**更严**（我建议 `|| fail=$((fail+1))`，主控加了 ❌ 说明）⇒ 记录在案。

#### 24.8.2 刷新执行（`--apply`，09:14:06；逐条 before16 → after16）

| # | 副本 | before16 → after16 | 权威 sha | 副本曾早 | 来源 |
| --- | --- | --- | --- | --- | --- |
| 1 | `samples/HelloWpf/bin/Debug/net10.0/DirectWrite.Linux.Provider.dll` | `a3c026c501ee9621` → **`71ba86c6495347fe`** | `71ba86c6495347fe` | **5,523 秒** | 路 1（`STALE`） |
| 2 | `tests/WpfGfx.Linux.Tests/Commands.Tests/bin/Debug/net10.0/WpfGfx.Linux.dll` | `08806714b6f1cf92` → **`1b63203ebfc20eea`** | `1b63203ebfc20eea` | **43 秒** | 路 1（`STALE`） |
| 3 | `build/MilBridge/tests/FamilyCoverageSelfTest/bin/Release/DirectWrite.Linux.Provider.dll` | `a3c026c501ee9621` → **`71ba86c6495347fe`** | `71ba86c6495347fe` | **5,523 秒** | 路 2（`DIVERGENT` 落单者） |

`APPSYNC-REFRESH=refreshed=3 newer=0 applied=1`；**幂等**：紧接着干跑一次 ⇒ `（没有 STALE / NEWER-DIFF / DIVERGENT 落单者…）refreshed=0`。

#### 24.8.3 全仓 `APPSYNC=PASS`（独立复核，不走刷新脚本）

`09:14:25` 直接跑校验器（`exit 0`）：

```
    CONSISTENT    DirectWrite.Linux.Provider.dll [Release] 11 份副本同 sha 71ba86c6495347fe
    CONSISTENT    DirectWrite.Linux.Provider.dll [Debug]   11 份副本同 sha 71ba86c6495347fe
    CONSISTENT    WpfGfx.Linux.dll [Debug]                  7 份副本同 sha 1b63203ebfc20eea
    CONSISTENT    libwpfwin32.so [Release]                  2 份副本同 sha f84d65a62e0c7fa4
计数：OK=27  MISMATCH=0（STALE=0  NEWER-DIFF=0）  DIVERGENT=0  NO-AUTHORITY=17  LIB-COPY=6  SKIP(obj)=4  SKIP(stub)=2  SKIP(ref)=8  RETIRED=0
APPSYNC=PASS（另有 17 份**无权威可比**（Release/RID 副本）+ 6 份**库输出目录**（不是加载源）+ SKIP(obj/stub/ref)=14 份按构造不同类 —— 见上；它们**不是**不一致）
```
（`OK=25 → 27`：两份原 `STALE` 变 `OK`；第 3 份原先是 `NO-AUTHORITY`（Release 路径）⇒ 刷新后仍是 `NO-AUTHORITY` 分类，但它所在的 `[Release]` 分组从 2 种 sha 变成 `CONSISTENT` ⇒ `DIVERGENT=3 → 0`。）

#### 24.8.4 `Commands.Tests` 那 43 秒：**"下次谁构建谁收敛"** 的机制（主控点名要的）

* **副本怎么来的**：app-local 副本由**消费者自己构建时**的 copy-local 步骤产生（`HintPath + Private=true`；`WiringSmoke.csproj:6-11` 是主控 2026-09-10 的裁定原文）。MSBuild 的 `Copy` 任务默认 `SkipUnchangedFiles=true` ⇒ **按时间戳判定**：源（权威）比副本新才拷。
* **所以落后会自愈**：`Commands.Tests` 是"它 00:13:44 构建、而 `WpfGfx.Linux.dll` 在 00:14:27 被重建（6 个兄弟目录同一秒刷新）" ⇒ 它落后一代。**下一次 `dotnet build` 这个测试工程时，源已经比它新 ⇒ 自动拷成新件**。这条不需要任何新机制，也不需要人记得。
* **但"新而不同"不会自愈**：如果副本 mtime **不早于**权威（例如被人/别的流程写坏、或跨配置/桩件混入），增量判定会**跳过** ⇒ 普通重建治不好。这正是：① 校验器把 `NEWER-DIFF` **单列且仍判红**；② 我车道的 `Directory.Build.targets` 用 `SkipUnchangedFiles="false"`（不依赖增量）的原因。§24.4 那条真树牙（追加 2 字节 ⇒ `NEWER-DIFF` ⇒ `dotnet build` 愈合）就是这条的实测。
* **波里 3.6 的价值**：把**所有**消费者一次收敛（包括 wave 自己**不重建**的那些目录，像 `samples/**`、`tests/**`），而不是等各自的"下一次构建" —— 否则发布/门禁会读到旧件（本项目 3 次假红/假绿事故都是这个形态）。
* **它会不会再出现**：只要别的车道还在并发重建，43 秒级落后**会反复出现**；但每次跑波（或任何消费者自己重建）都会收敛，且 3.6 让"波后全仓一致"成为**可断言的事实**而不是期望。

#### 24.8.5 主控的三条决定落档（免得下一个人以为是漏改）

| # | 事项 | 决定 | 理由（主控） | 报告位置 |
| --- | --- | --- | --- | --- |
| 1 | 分组 `find -path` 大小写敏感（`release_*` 不进分组） | **暂不改**：保持大小写敏感 + **每次运行打印这条缺口** | 改 `-ipath` 会一次引入一批**别人车道**的红，**现在没有 owner 去清**；等 3.6 在真实波里跑过一遍、`release_*` 目录归属明确后再翻 | §24.2 缺口段（已加决定块） |
| 2 | `ReachFramework.dll` 纳入 ITEMS | **暂不纳入** | 6 份宿主副本都在别人车道且确实陈旧，纳进来只会长期报红 | §24.6 覆盖决定段（已加决定块；"翻开只需加一行"的原文保留在其上） |
| 3 | 刷新 3 份残项（写 `samples/**`、`tests/**`、MilBridge Release 输出） | **批准执行** | 应用槽已空（T3 整批跑完，`:97` 无残留）⇒ 不存在"换掉被映射的二进制"风险；这三处是**构建输出目录**、不是源码 | 本节 24.8.2/24.8.3 |

⇒ **本轮收口**：全仓 `APPSYNC=PASS`（exit 0）；我车道独立 `APPSYNC=PASS`（exit 0）；校验器 `--selftest` A–H 8/8；刷新幂等；口径缺口与覆盖边界**各有 owner、各有决定、都在报告里**。

#### 24.8.6 现场核对：刷新与 T3 探针**没有重叠**，且 T3 的运行槽本身是一致的

派单里"槽已空 ⇒ 不存在换掉被映射二进制的风险"这条，我按时间线**实测核对**（不是照抄）：

| 时刻 | 事件 | 证据 |
| --- | --- | --- |
| 09:14:06 | 我跑刷新前的槽检查 | `pgrep -f "HelloWpf\|WpfTextDemo\|WpfFeatureProbe\|HelloMil\|dotnet run"` **无输出** |
| 09:14:06–09:14:09 | `--apply` 刷新 3 份 | 三份 mtime = `09:14:08.905 / .944 / 09:14:09` |
| 09:15:57 | **T3** 的 `textbox-edit` 探针启动（`dotnet WpfFeatureProbe.dll --only=textbox-edit`，cwd `/tmp/dp1-native`） | `ps -o lstart` = `Mon Sep 14 09:15:57 2026`（比我刷新完晚 **1 分 49 秒**） |

⇒ **刷新窗口内没有任何我方进程持有那 3 个目录里的文件**；且我刷的 3 处（`samples/HelloWpf`、`tests/…/Commands.Tests`、`build/MilBridge/tests/FamilyCoverageSelfTest`）与 T3 的运行槽 `/tmp/dp1-native` **不是同一目录**。
顺手核了 T3 槽里的件（**它对得上全仓权威**，所以 T3 这一批读的是新件）：`libwpfwic.so`=`03b67fbc`（权威）、`libwpfwin32.so`=`f84d65a6`（权威）、`DirectWrite.Linux.Provider.dll`=`71ba86c6`（权威）、`wpfgfx_cor3.so`=`c6608344…`（= 主控 09-14 重发的桥 `c66083443200115d`）；槽里没有 `WpfGfx.Linux.dll`。
**覆盖边界（登记）**：`/tmp/**` 的运行槽**不在** `SCAN_ROOTS`（`build:tests:samples:src`）里 ⇒ 校验器不覆盖它们。今天没问题是因为槽是**每次运行新建**的（09:15:57 一次建好、四项全对权威）；但如果哪个槽**跨一次桥/权威变更被复用**，它就会带着旧件跑 —— 那属于 `:99`/`verify-all.sh` 的运行协议面，我在这里只登记事实与风险面，不越界改它。

## 25. `-ipath` 缺口翻正 + `ReachFramework.dll` 纳入 ITEMS（T2，2026-09-14 18:2x）

**派单**：主控裁定"当年不改的两条前提已变（波里已接进 3.6 刷新）"⇒ 任务 1 翻正分组匹配、任务 2 纳入 `ReachFramework.dll`、任务 3 回答 PF 库输出那份归哪类。
改了 **2 个文件**（都在我车道）：`check-applocal-sync.sh` sha `6d7af95fcb7ea15d…`、`sync-applocal-authority.sh` sha `b56a85afd70c2321…`。**没动任何会产出构建物的东西**（不产构建物 ⇒ 通常不需要并波；若主控仍要并波，见 25.5 的请求）。

### 25.0 一句话结论
1. **翻正零新增红**（实测，见 25.1）：新增的 5 个分组成员全是同 sha 的加载源；真正"会变红"的那批（`.artifacts/**/release*/` 的 WpfGfx **5 份**）**全是非启动宿主** ⇒ 已被上轮的 `LIB-COPY` 口径排除 —— 它们不是"没人清的落后件"，是**另一个配置的产物/中间件**。
2. **纳入 `ReachFramework.dll`** 后新增 **5 份 `STALE` + 1 个 `DIVERGENT` 组**；3.6 干跑显示**恰好这 5 份**会被刷新（逐条 before→after 见 25.2）⇒ 跑一趟 3.6（波或 `--apply`）即回到 `APPSYNC=PASS`。
3. **牙两极化已做**：在新纳入的小写 `release/` 目录里改一份 ⇒ `DIVERGENT [Release]` + `exit 1`；**逐字节还原** ⇒ 该红消失（`cmp` 与权威相同）。"还原后整仓回绿"这句要等 25.2 那 5 份刷完才能给全 —— 现在剩的那条 MISMATCH **就是它们**，与牙无关。
4. 校验器自检 **A–I 9/9**（新增 **I** = 小写 `release/` 必须进分组，就是翻正自己的牙）。

### 25.1 任务 1：`-ipath` 翻正（含"会新增哪些条目"的实测清单）

**改法**（`check-applocal-sync.sh` 分组段）：不再用 `find -path "*/$cfg/*"`（大小写敏感），改为取出候选后按 `${f,,}` 归一匹配：
```bash
case "${f,,}" in
    */release/*|*/release_*/*) [ "$cfg" = "Release" ] || continue;;
    */debug/*)                 [ "$cfg" = "Debug" ]   || continue;;
    *) continue;;
esac
is_stub "$f" && continue; is_obj "$f" && continue
if is_managed "$f" && ! is_host_dir "$f"; then continue; fi
```
⇒ 大写 `Release`、小写 `release`、RID 目录 `release_linux-x64` **现在同属 Release 组**；排除项（obj / CycleStub / 非宿主）与逐副本判定**同一把尺子**。

**(a) 翻正新增的条目清单（同一棵树、新旧两版逐行 diff 的严格答案）**：

| 新增成员 | 大小 | sha | 分类 | 组内结果 |
| --- | --- | --- | --- | --- |
| `build/MilBridge/.artifacts/publish/MilBridge.Linux/release_linux-x64/libwpfwic.so` | 70,440 | `03b67fbc…` | 原生 ⇒ 照判 | **CONSISTENT**（2 份同 sha） |
| `build/MilBridge/.artifacts/bin/ClosedLoop/release/libwpfwic.so` | 70,440 | `03b67fbc…` | 原生 + 宿主 | 同上 |
| `build/MilBridge/.artifacts/bin/ClosedLoop/release/DirectWrite.Linux.Provider.dll` | 110,080 | `71ba86c6…` | 宿主 ⇒ 加载源 | **CONSISTENT**（Release 组 11→**12** 份同 sha） |
| `…/publish/MilBridge.Linux/release_linux-x64/wpfgfx_cor3.so` | 4,950,352 | `759a3224…` | 原生 | **CONSISTENT**（2 份同 sha；此前该组 0 成员） |
| `…/bin/MilBridge.Linux/release_linux-x64/native/wpfgfx_cor3.so` | 4,950,352 | `759a3224…` | 原生 | 同上 |

**(b) 计数前后对比（同一棵树，仅翻正这一处差异）**：

| 版本 | OK | MISMATCH | STALE | NEWER-DIFF | DIVERGENT | NO-AUTHORITY | LIB-COPY | SKIP(obj) | SKIP(stub) | SKIP(ref) | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 翻正前（旧版快照） | 27 | 0 | 0 | 0 | 0 | 17 | 6 | 4 | 2 | 8 | `APPSYNC=PASS` |
| 翻正后 | 27 | 0 | 0 | 0 | 0 | 17 | 6 | 4 | 2 | 8 | `APPSYNC=PASS` |
| **差** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | **不变** |
（分组行的可见变化：`+CONSISTENT libwpfwic.so [Release] 2 份`、`Provider [Release] 11→12 份`、`+CONSISTENT wpfgfx_cor3.so [Release] 2 份`。）

**(c) 反事实：若没有上轮那条 `LIB-COPY` 排除，翻正会引入什么**（我把 `LIB-COPY` 判定临时关掉后**实测**，不是推演）：

```
DIVERGENT  WpfGfx.Linux.dll [Release] 有 3 种 sha：
    build/MilBridge/.artifacts/bin/MilBridge.Linux/release/WpfGfx.Linux.dll    4b2de9e927418b6e  2026-09-10 18:04:09  (250,368 B)
    build/MilBridge/.artifacts/bin/MilBridge.Linux/release_linux-x64/WpfGfx.Linux.dll 133fb96eedc6512d  2026-09-14 09:29:32  (323,584 B)
    build/MilBridge/.artifacts/bin/WpfGfx.Linux/release/WpfGfx.Linux.dll       133fb96eedc6512d  2026-09-14 09:29:32
    build/MilBridge/.artifacts-rb/bin/ProbeB/release/WpfGfx.Linux.dll          d41a89530a8a25d3  2026-09-10 15:09:00  (226,304 B)
    build/MilBridge/.artifacts-rb/bin/WpfGfx.Linux/release/WpfGfx.Linux.dll    d41a89530a8a25d3  2026-09-10 15:09:00
⇒ MISMATCH=0 DIVERGENT=2 → APPSYNC=MISMATCH
```
⇒ **这 5 份全是 `host=0`（输出目录里没有 `runtimeconfig.json`）** ⇒ 现行口径判它们 `LIB-COPY`：**不是加载源、也不是"落后一代"**；其中 `.artifacts-rb/**` 是**废弃的第二 artifacts 根**（09-10 之后再没人写过）。所以"当年担心的一批无人清的红"**并不存在**：它们本来就不该被当成"不一致"。
（顺带对比：`Provider [Release]` 那 11 份**是宿主**（探针目录）且 sha 全等于 Debug 权威 ⇒ 那是本仓**设计**"Release 目录放 Debug 权威"（`build/MilBridge/tests/Directory.Build.targets` 逐字），与上表那批**不同类**。）

### 25.2 任务 2：`ReachFramework.dll` 纳入 ITEMS

**权威现场**：`build/ReachFramework.Linux/bin/Debug/ReachFramework.dll` = **`1fd4fe8a2ffd20b1`**（741,376 B，**18:26:57**）—— 与主控给的 `ede1f3644a56ccc0` **不同**：它在我读之前**又被重建过一次**（18:26:57，即 18:15 那趟波之后）。我按**当前权威**判，并把这个时间差登记在此（不是读数冲突，是权威在动）。

**(a) 纳入后逐条分类（现场实测）**：

| 分类 | 条数 | 成员 |
| --- | --- | --- |
| `SKIP(stub)` | **2** | `build/CycleStub.ReachFramework.Linux/{bin,obj}/Debug/ReachFramework.dll` = **5,120 B `067c03858367d880`**（权威 741,376 B）⇒ **同名桩陷阱按 SKIP(stub) 处理，未判成不一致**（判定已**提前到 obj 之前**，所以连 obj 里那份也报 `SKIP(stub)` —— "为什么不同"的答案更准） |
| `OK` | 1 | 权威件本身 |
| `SKIP(obj)` | 1 | 权威的 obj 副本（与权威**同 sha**） |
| `LIB-COPY` | **2** | `build/PresentationFramework.Linux/bin/Debug/`（`c6a2863f`，18:21:50）、`build/PresentationFramework.Classic.Linux/bin/Debug/`（`e8819668`，09-10）⇒ 库输出、不是加载源（任务 3 就是这条） |
| `NO-AUTHORITY` | 1 | `samples/HelloWpf/bin/Release/net10.0/`（`e8819668`）⇒ Release 副本对 Debug 权威 |
| **`STALE`** | **5** | 见下表 |

**(b) 纳入前的陈旧清单 → 3.6 干跑（= 波里那一步会做的）**：

| # | 副本 | before16 → after16（3.6 将写） | 权威 sha | 副本曾早 |
| --- | --- | --- | --- | --- |
| 1 | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll`（**我车道**） | `daf9b6f073f6f6bb` → **`1fd4fe8a2ffd20b1`** | `1fd4fe8a2ffd20b1` | **72,215 秒** |
| 2 | `tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/bin/Debug/net10.0/ReachFramework.dll` | `91878c0411a1c361` → **`1fd4fe8a2ffd20b1`** | 同上 | 644 秒 |
| 3 | `samples/WpfFeatureProbe/bin/Debug/net10.0/ReachFramework.dll` | `91878c0411a1c361` → **`1fd4fe8a2ffd20b1`** | 同上 | 644 秒 |
| 4 | `samples/HelloWpf/bin/Debug/net10.0/ReachFramework.dll` | `00723a05d843140b` → **`1fd4fe8a2ffd20b1`** | 同上 | 257,055 秒 |
| 5 | `samples/WpfTextDemo/bin/Debug/net10.0/ReachFramework.dll` | `da65eaf41e7aef72` → **`1fd4fe8a2ffd20b1`** | 同上 | 30,831 秒 |

`APPSYNC-REFRESH=refreshed=5 newer=0 applied=0`（干跑）⇒ 5 份都在"启动宿主 + 同配置 + 落后"这条**唯一可刷新**的路径上（路 1 `STALE`）。**主控给的三个变体核对**：权威（我读到 `1fd4fe8a`，你读到 `ede1f364` —— 时间差见上）；PF 的 bin 副本 = `c6a2863f`（741,376 B，比权威旧 ⇒ 但走 `LIB-COPY`，见任务 3）；桩件 = `067c0385` / 5,120 B ✔ **与你的读数逐位一致**。

### 25.3 任务 3：`build/PresentationFramework.Linux/bin/Debug/ReachFramework.dll` 归哪一类？

**归 `LIB-COPY`**（该输出目录**没有 `runtimeconfig.json`** ⇒ 不是启动宿主 ⇒ 里面的私有依赖副本**不是加载源**）。
**3.6 不会刷它，而且不必刷**，理由三条：
1. **没人从这个目录启动**：它是被 `samples/*.csproj` 用 `HintPath` 引用的**引用源**（`WpfFeatureProbe.csproj:93-94` 那种形态），不是宿主；`Private=true` 只把**被引用的那一个 dll**拷进消费者的输出目录，**不会**把 PF 自己的私有依赖（Provider / ReachFramework）带过去 —— 所以这份副本根本进不了任何加载路径。
2. **拷进去只会制造"看起来同步了"的假象**：它不参与加载，刷新它不改变任何行为，却让"库输出目录"也进入判定面。
3. **真正会被加载的是消费者宿主里的副本** —— 就是 25.2(b) 那 5 份（含我车道 `SystemFontsProbe` 那份）；那些**是**被 3.6 覆盖的。
⇒ 若主控认为"库输出目录也应与权威一致"（例如将来有人从那里起进程），那是**一条口径变更**（把库输出纳入判定 + 让 3.6 覆盖它），代价是判定面变大、且要写 `build/PresentationFramework.Linux/**`；**我不建议现在做**，理由与 §24 里 `-ipath` 那条同源（先有 owner 再扩面）。**要不要一并覆盖，请主控裁定。**

### 25.4 任务 1.3 的牙（两极化，实测）

| 步骤 | 操作 | 实测 |
| --- | --- | --- |
| 事前 | 确认没人用该目录（`pgrep ClosedLoop/MilBridge` 无匹配；此刻在跑的是 **T1b 的 `run.sh tline`**，它不读这个目录） | — |
| 牙① | 在**新纳入的小写** `release/` 里改一份：`build/MilBridge/.artifacts/bin/ClosedLoop/release/DirectWrite.Linux.Provider.dll` 追加 2 字节（`71ba86c6` → `4e92ccc7`） | ✅ `DIVERGENT DirectWrite.Linux.Provider.dll [Release] 有 2 种 sha：` 列出该副本 ⇒ `APPSYNC=MISMATCH`，**exit 1**。（**翻正前**这条不会红：那时小写 `release/` 不进分组，逐副本又按 `NO-AUTHORITY` 放过 ⇒ 静默。） |
| 牙② | **逐字节还原**（`cp -p` 自快照；`cmp` 与权威**逐字节相同**、sha 回 `71ba86c6`、size 110,080） | ✅ 该 `DIVERGENT [Release]` 消失（`DIVERGENT 2 → 1`，剩下的 1 是 `ReachFramework [Debug]` 那 5 份 STALE 造成的，**独立于牙**） |

**说明**：严格的"还原 ⇒ `APPSYNC=PASS`"要等 25.2(b) 那 5 份刷完（见 25.5）—— 现在整仓仍是 `MISMATCH=5`，而这 5 条**正是**任务 2 纳入后应被 3.6 收敛的那批，不是牙的残留。

### 25.5 收口请求（按新规矩 ③，我不自己发波）

现状（18:29）：`OK=28 MISMATCH=5（STALE=5） DIVERGENT=1 NO-AUTHORITY=18 LIB-COPY=8 SKIP(obj)=5 SKIP(stub)=4 SKIP(ref)=8` ⇒ `APPSYNC=MISMATCH`；**翻正零新增红**、**纳入 ReachFramework 的 5 份 STALE 全在 3.6 的可刷新路径上**。
请主控二选一（我都能给出逐条 before→after + 前后 exit）：
* **(A) 你跑波**（`close-wave.sh` 认领，波里 3.6 会自动刷这 5 份；顺带把 PC/PF/WB 等重建）；
* **(B) 批准我直接 `bash build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh --apply`** —— 与 3.6 **同一段代码**、**只拷不重建**（不改变任何构建物 sha、不作废别人的读数），刷完我立刻复跑校验器给出 `APPSYNC=PASS` 与逐条 before→after。
**我倾向 (B)**（代价最小、且不动别人车道正在跑的 `tline`）；若你要的是"波里 3.6 真的收敛过一遍"这条端到端证据，那就 (A)。
*（附带请求）* 25.3 那个"库输出目录要不要一并纳入判定/覆盖"的问题，也请你裁定；我按你的裁定改口径或维持现状。

### 25.6 主控裁定与执行（2026-09-14 18:31）

> **本节状态**：§25.5 的请求**已由主控裁定**（授权 (B) 执行 + ②③④ 三条裁定 + ⑤ 一条结构事实）。以下逐条落档。

#### 25.6.1 授权 (B) 已执行：`--apply` 刷那 5 份（18:31:05–18:31:06）

| # | 副本 | before16 → after16 | 权威 sha | 副本曾早 |
| --- | --- | --- | --- | --- |
| 1 | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll`（我车道） | `daf9b6f073f6f6bb` → **`1fd4fe8a2ffd20b1`** | `1fd4fe8a2ffd20b1` | 72,215 秒 |
| 2 | `tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/bin/Debug/net10.0/ReachFramework.dll` | `91878c0411a1c361` → **`1fd4fe8a2ffd20b1`** | 同上 | 644 秒 |
| 3 | `samples/WpfFeatureProbe/bin/Debug/net10.0/ReachFramework.dll` | `91878c0411a1c361` → **`1fd4fe8a2ffd20b1`** | 同上 | 644 秒 |
| 4 | `samples/HelloWpf/bin/Debug/net10.0/ReachFramework.dll` | `00723a05d843140b` → **`1fd4fe8a2ffd20b1`** | 同上 | 257,055 秒 |
| 5 | `samples/WpfTextDemo/bin/Debug/net10.0/ReachFramework.dll` | `da65eaf41e7aef72` → **`1fd4fe8a2ffd20b1`** | 同上 | 30,831 秒 |

`APPSYNC-REFRESH=refreshed=5 newer=0 applied=1`；脚本内自检：**刷新前 `exit=1` / 刷新后 `exit=0`**。

**独立复核**（18:31:12，不走刷新脚本）：`exit=0`
```
CONSISTENT libwpfwic.so [Release] 2 份 03b67fbc ｜ libwpfwin32.so [Release] 2 份 91baee84
CONSISTENT DirectWrite.Linux.Provider.dll [Release] 12 份 71ba86c6 ｜ [Debug] 11 份 71ba86c6
CONSISTENT WpfGfx.Linux.dll [Debug] 7 份 63766fdd ｜ ReachFramework.dll [Debug] 5 份 1fd4fe8a
CONSISTENT wpfgfx_cor3.so [Release] 2 份 759a3224
计数：OK=33  MISMATCH=0（STALE=0  NEWER-DIFF=0）  DIVERGENT=0  NO-AUTHORITY=18  LIB-COPY=8  SKIP(obj)=5  SKIP(stub)=4  SKIP(ref)=8  RETIRED=0
APPSYNC=PASS
```
**未触碰任何构建物、未跑应用**：本次只执行了 5 次 `cp -f`（就是上表 5 条路径）——依据是 `find build src samples tests -type f -newermt '2026-09-14 18:31:00'` **只列出这 5 个文件**（无任何源码/其它产物）；权威件 `build/ReachFramework.Linux/bin/Debug/ReachFramework.dll` 前后同为 `1fd4fe8a2ffd20b1`（未被动过）；期间**没有**跑 `dotnet build`/`publish`/应用。
**本地幂等**：紧接着干跑 3.6 同一脚本 ⇒ `refreshed=0`（"没有 STALE / NEWER-DIFF / DIVERGENT 落单者"）。⇒ **主控的统一波跑到 3.6 时应打印 `refreshed=0`**，那一条就是幂等的端到端证据（与 (A) 不互斥，两件事都拿得到）。

#### 25.6.2 裁定 ②：`-ipath` 翻正**批准保留**；并记一条方法论

主控原话（精神照录）：**当年裁定"暂不改"时担心的那批红并不存在** —— 反事实实测露出的是 `WpfGfx.Linux.dll [Release]` 的 3 种 sha/5 个成员，但它们**全是 `host=0`**、且来自**另一个配置的产物/中间件**（`.artifacts-rb/**` 自 09-10 没人写过）⇒ 不是"无人清的落后件"；**"我当时的顾虑建立在未验证的假设上，你把它测掉了"**。
⇒ **本报告新增一条方法论（主控点名要写进来）**：**决策依据本身也必须被验证**。这次的形态是"顾虑 → 假设'翻正会引入一批无人清的红' → 用反事实实测（临时关掉 `LIB-COPY` 排除）把假设测掉 → 净收益确认"。
⇒ **该改法的净收益**：多覆盖 **5 个加载源**（发布目录 + `native/` 两份桥 + ClosedLoop 的 shim/Provider），**零新增噪声**（翻正前后计数逐项不变，见 25.1(b)）。

#### 25.6.3 裁定 ③：任务 3 **保持 `LIB-COPY`（库输出不刷）**

理由按 §25.3 三条（① 没人从该目录启动；② `Private=true` 只拷被引用那一个 dll，不带出 PF 的私有依赖；③ 真正被加载的是消费者宿主里的副本，已被 3.6 覆盖）。
**并且**（主控加的边界，照录）：**若将来有人要把库输出也纳入一致性 ⇒ 那是口径变更，须先有 owner 与判据** —— **不许顺手扩面**。本报告把这条写在此处，作为该判据的 owner 入口。

#### 25.6.4 裁定 ④：桩件处置认可

`CycleStub.*` 判 **`SKIP(stub)`**，且**判定提前到 obj 之前**（连 `CycleStub.*/obj/` 也报 `SKIP(stub)`，而不是被 obj 规则吞掉）。主控注明"自己在审计时踩过这个 5,120 B 同名桩"⇒ 这条特殊赞成。核对读数：`SKIP(stub)=4`（Provider 2 + ReachFramework 2）、桩件 sha `067c03858367d880` / 5,120 B，**全程未参与任何"不一致"判定**。

#### 25.6.5 主控同步的结构事实（⑤）与我的口径的自洽性

主控给的事实：**`PresentationFramework ⇄ ReachFramework` 是真互引的环成员** ⇒ **按环序重建时它们的字节必然改变**（实测 `PF⇒Reach⇒PF⇒Reach` 两轮四个 sha）；而**从固定输入重建是逐字节确定的**。
⇒ 与我的判定**自洽**：本校验器的判据是 **内容 + 时间**（sha 不同 **且** 副本早于权威 ⇒ `STALE`），**从不从"发生过重建"这个动作推 stale**。所以"环成员重建导致权威 sha 变化"只会让**副本看起来落后**（这正是 `STALE` 要表达的：消费者该重拷一次），而**不会**被误判成"某次构建不确定"。
⇒ 这条也解释了今天 `ReachFramework` 权威为何动过两次（我 09:13 读到 `f64b76d4`、18:26:57 读到 `1fd4fe8a`，而主控那一刻读到 `ede1f364`）：**权威在动是设计内的现象，不是读数冲突**；也因此 3.6 必须排在波的重建之后（现在就是）。
*（术语说明）*：主控提到的 `kind=peer` **不在本校验器的术语表里**（本脚本只有 `OK / STALE / NEWER-DIFF / NO-AUTHORITY / LIB-COPY / SKIP(obj|stub|ref) / RETIRED`）；按上下文它应属"产物身份指纹"那一族（`build/artifact-src-fp.py` 的 `kind`）。我这里只声明**实质**一致：判据只看内容与时间，不看动作。

## 26. 口径变更：`HintPath` 解析源目录 ⇒ 不再是"库输出、不判定"（T2，2026-09-14 18:33–18:37）

**触发**：主控 18:32 复核发现我 18:31 报的 `APPSYNC=PASS` **已经变红**（`MISMATCH=1(STALE=1) DIVERGENT=1`）：`tests/…/ManagedLayer.Tests/bin/Debug/net10.0/ReachFramework.dll` 从 `1fd4fe8a` 退回 `c6a2863f`，而 `c6a2863f` **正是 PF 的 bin 里那份**（= §25.3 我归为 `LIB-COPY`、并据此得到的裁定 ③"不刷"的那一份）。**裁定被证据推翻，我在 §26.1 记档过程。**

### 26.1 记录：这次裁定是怎么被推翻的（本项目最看重的那种记录）

| 时间 | 谁 | 内容 |
| --- | --- | --- |
| 18:31:12/18:31:55 | 我 | 校验器 `APPSYNC=PASS`（`OK=33`），我据此在 §25.6.3 写下"保持 `LIB-COPY`（库输出不刷）" |
| 18:32 | 主控 | 复核变红：`STALE … EXPECT 1fd4fe8a ACTUAL c6a2863f（副本早 307 秒）` + `DIVERGENT ReachFramework [Debug] 2 种 sha` |
| — | 主控追链 | `ManagedLayer.Tests.csproj:87-89` 用 **HintPath 指向 PF 的 bin** ⇒ MSBuild **RAR 从"被引用程序集所在目录"解析传递依赖** ⇒ 把 PF bin 里的 `ReachFramework.dll`（`c6a2863f`）拷进消费者 app-local；铁证：① 消费者那份 mtime = **18:21:50** = PF bin 那份的 mtime（同一刻）；② 我 18:31 刷过之后，**M7b 的 `dotnet test` 构建又把它拷回旧件** ⇒ **这个红会自我复现** |
| 18:33 | 我 | 现场复现（`ManagedLayer.Tests` = `c6a2863f`，mtime 18:21:50，与 PF bin 同刻同 sha）⇒ 确认主控读数为真、我的裁定 ③ 为错 |

**我那条理由错在哪（逐字对照）**：我在 §25.3 写的理由②"`Private=true` 只把**被引用的那一个 dll** 拷进消费者的输出目录，**不会**把 PF 自己的私有依赖带过去" ⇒ **被实测否掉**：RAR 解析的是**被引用程序集所在目录**里的**传递闭包**，`ReachFramework.dll` 正是 PF 的私有依赖，**会被一起解析并拷贝**。
**口径教训（我在 §25.6.2 记过一条，这次是同一课的第二遍）**：**把某类副本判为"不是加载源"之前，必须验"有没有人能从它解析"** —— **RAR 的传递解析就是一条此前没人检查的路径**；"没人从这个目录启动"（我的理由①）**不等于**"没人从这里解析"。
**结论**：裁定 ③ 撤回；`LIB-COPY` 的适用面**收窄**为"既不是启动宿主、**也不被任何 `HintPath` 引用**"。

### 26.2 口径变更（已落地，`check-applocal-sync.sh` sha `2d18bffe34ce3816…`）

**判据（主控指定）**：**该目录是否出现在任何 csproj 的 `<HintPath>` 里**。实现：
```bash
# 解析源目录集：把 HintPath 里的 MSBuild 属性替换成实际路径，取 dirname，去重
p="${p//\$(MSBuildThisFileDirectory)/$d/}"   # 工程所在目录
p="${p//\$(WpfLinuxRoot)/$REPO/}"  p="${p//\$(RepoRoot)/$REPO/}"
p="${p//\$(WpfLinuxBinDir)/$REPO/build}"  p="${p//\$(DwRoot)/$REPO/build/DirectWrite.Linux}"
p="${p//\$(WpfLinuxSelfBuiltConfiguration)/Debug}"
case "$p" in *'$('*) HINTPATH_UNRESOLVED=$((HINTPATH_UNRESOLVED+1)); continue;; esac   # 解析不掉 ⇒ 登记，不静默
```
* **运行期第一行就打印规模**（可复核的"枚举"）：`解析源目录：16 个；未能解析的 HintPath：0 条`。
* 命中解析源的副本**跳过 `LIB-COPY`**，进入 OK/STALE/NEWER-DIFF 判定，并在行尾**点名这条路**：
  `；**该目录被 csproj 的 HintPath 引用 ⇒ RAR 会从这里解析传递依赖（解析源，必须与权威一致）**`
* 分组过滤同步放宽（解析源副本参与跨副本一致性）。
* 可测性：新增 `HINTPATH_ROOTS`（默认仓库根）供自检使用。

### 26.3 枚举：新口径一次暴露 **4 份**（任务 1 要求"别只修这一处"）

| # | 副本 | sha（→ 刷新后） | 曾早 | 说明 |
| --- | --- | --- | --- | --- |
| 1 | `build/PresentationFramework.Linux/bin/Debug/ReachFramework.dll` | `c6a2863f` → **`1fd4fe8a`** | 307 秒 | **主控点的那份**（PF bin = 解析源） |
| 2 | `tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/bin/Debug/net10.0/ReachFramework.dll` | `c6a2863f` → **`1fd4fe8a`** | 307 秒 | 被 #1 传递解析出来的**受害者**（两者同刻同 sha） |
| 3 | `build/PresentationFramework.Classic.Linux/bin/Debug/DirectWrite.Linux.Provider.dll` | `ef6cc9d3` → **`71ba86c6`** | **323,677 秒** | 同类第二处（`samples/*.csproj` 引用 Classic 的 bin）—— **它正是 §24.1 表格里的"原 #6"**，我当时判 `LIB-COPY` 也**判错了**；这次一并纠正 |
| 4 | `build/PresentationFramework.Classic.Linux/bin/Debug/ReachFramework.dll` | `e8819668` → **`1fd4fe8a`** | **357,557 秒** | 同上（09-10 的老件） |

**口径变更的副产品**：`LIB-COPY` 计数 **8 → 0**（原先那 8 份**全部**位于 `build/<Proj>.Linux/bin/Debug` 这类被 HintPath 引用的目录）⇒ 该类现在在这棵树上为空（代码保留：既非宿主、又无人引用的目录仍会落这里）。
**刷新**：`--apply` ⇒ `refreshed=4 newer=0`；刷新后 `OK=41 MISMATCH=0 DIVERGENT=0 LIB-COPY=0`、7 个分组 `CONSISTENT`（`Provider [Debug]` 18 份、`ReachFramework [Debug]` 8 份、`WpfGfx [Debug]` 8 份）。

### 26.4 牙（任务 2 三条 + 反向一条，**全部实测**，含一条意外结果）

| # | 做法 | 预期 | **实测** |
| --- | --- | --- | --- |
| ① | 把解析源那份改成错的 sha（`1fd4fe8a` → `ec905f0c`，追加 2 字节，mtime=现在） | 必须报红并点名 | ✅ `NEWER-DIFF … EXPECT 1fd4fe8a ACTUAL ec905f0c`，行尾**点名**"该目录被 csproj 的 HintPath 引用 ⇒ RAR 会从这里解析传递依赖"；`DIVERGENT [Debug]` 列出它 |
| ② | 构建消费者 `ManagedLayer.Tests`（`dotnet build … --no-restore -p:BuildProjectReferences=false`，rc=0/6.9 s） | 消费者应拿到被污染的 sha | ✅ 消费者那份 = **`ec905f0c`**（= 源）；校验器对**两处**同时报红并点名 ⇒ **RAR 传递解析机制当场复现** |
| ③ | 刷新 ⇒ 绿；重建消费者 ⇒ **仍绿** | 幂等 | ⚠️ **刷新器拒绝**：`refreshed=0 newer=4`（污染让副本**比权威新** ⇒ 落 `NEWER-DIFF` ⇒ 按"只告警不盲拷"**故意不动**）。⇒ 我按定义**从快照逐字节还原**源，再用**不带 `-p` 的 `cp`** 让它比消费者那份新 ⇒ **重建消费者** ⇒ 消费者自动收敛到 `1fd4fe8a`（**结构愈合，没有手工 cp 消费者那份**）⇒ PASS。另做**落后类**两极化（污染 + 回拨 mtime ⇒ `STALE` 点名 ⇒ `--apply` ⇒ `refreshed=1` ⇒ PASS）—— 这才是真实场景（主控那条就是落后 307 秒） |
| ④ | **反向**：删掉解析源那份 ⇒ 构建消费者 | 主控要求"如实记录" | **意外结果**：构建 **rc=0 / 0 错误**；源**没有**被重建；**消费者那份 app-local 被一起删掉**；而校验器当时（及今天）**报 PASS** ⇒ **校验器对"缺件"是瞎的** |

**④ 的两条候选判据都被我实测否掉（不猜、不写进代码）**：
* **"用构建自己的 `FileListAbsolute.txt` 当应有件真值"** ⇒ **否**：删除后重建，该清单**被构建改写**成不再列 `ReachFramework.dll`（`grep -c` = 0）—— 构建会把自己的账改成与新现实一致。（干净树基线是有利的：62 份清单、53 条非 obj 应有件、**缺件 0** ⇒ 零误报；但它**不可靠**。）
* **"依赖闭包"**（宿主目录里有程序集提到 X 而 X 缺件 ⇒ 报缺）⇒ **否**：干净树上就报 **20 个候选**（`WpfGfx.Linux` 20、`wpfgfx_cor3.so` 21、`ReachFramework` 11、`libwpfwin32.so` 2）⇒ 典型的"狼来了"。
⇒ **登记为已知洞（缺件不可检）**：`--apply` 也**不会**补回缺件（它只刷"存在且落后"的副本）；要堵这个洞需要**基线状态文件**或**owner 定义好的闭包判据** —— 按主控"先有 owner 与判据"的规矩，**本轮不扩面**，只把证据与两条被否掉的路记在此。

### 26.5 终态与"构建之后仍 PASS"

* `check-applocal-sync.sh` 自检 **A–J 10/10**（新增 **J**：非宿主但被 HintPath 引用 ⇒ 必须判定并报红；**同一份副本没有 HintPath 时仍是 `LIB-COPY` 且不判** ⇒ 证明是 HintPath 在起作用）。
* 终检（**18:37:16，在我两次重建 `ManagedLayer.Tests` 之后**）：`OK=41 MISMATCH=0（STALE=0 NEWER-DIFF=0） DIVERGENT=0 NO-AUTHORITY=18 LIB-COPY=0 SKIP(obj)=5 SKIP(stub)=4 SKIP(ref)=8 RETIRED=0` ⇒ **`APPSYNC=PASS`，exit 0**；7 个分组全 `CONSISTENT`。⇒ **"在测试工程构建之后重跑仍 PASS"成立**（这正是主控要求的、排除假绿的那一条）。
* 还原核对：`cmp` 证明 `build/PresentationFramework.Linux/bin/Debug/ReachFramework.dll` 与消费者那份**都与权威逐字节相同**；`/tmp` 快照（`/tmp/reach_pfbin.bak`）保留供复核。
* **边界**：本轮只改我车道 `check-applocal-sync.sh`（sha `2d18bffe34ce3816…`）；`sync-applocal-authority.sh` **未改**（STALE 已由路 1 覆盖）；**没跑波、没跑应用**；期间他人的并行活动已登记（18:34 有 `dotnet build build/PresentationCore.Linux/…`；18:37 有 T1d 的 `CoverageProbe` Release 构建）。牙涉及的所有删除/污染**全部逐字节还原**（`cmp` 可验）。

## 27. 仪器补洞：**期望集合从"现存文件"解耦**（校验器对删除不再瞎）（T2，2026-09-14 19:2x–19:34）

**已登记洞**原文（§26.4 ④）："删掉解析源那份 ⇒ 构建 rc=0；源没被重建；**消费者那份 app-local 被一起删掉**；而校验器报 PASS ⇒ **对'缺件'是瞎的**"。本节把它补掉。

**唯一复算命令**（两条；第一条给期望集的规模，第二条给全判定）：
```bash
python3 build/DirectWrite.Linux/wic-shim/applocal-expect.py "$PWD" | grep -E '^#SUMMARY|^#UNKNOWN'
bash    build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh
```

### 27.1 设计：期望只来自**声明式来源**（基数与现场文件无关）

期望集由**唯一实现** `build/DirectWrite.Linux/wic-shim/applocal-expect.py`（sha `c9d2bceebeebce6d…`）算出来，规则全部是仓库里的声明：

| 边 | 来源（逐条可核） | 语义 |
| --- | --- | --- |
| `<Reference Include="X">` 且 `<Private>`≠false | csproj | 该工程的输出目录必须有 X（`Private=false` ⇒ **不**期望副本） |
| `<Reference><HintPath>` | csproj（属性替换后取 dirname） | 该目录是 **RAR 解析源**（= §26 的 `REFDIR`）；指向的**工程**还要取其闭包 |
| `<ProjectReference>` | csproj（**路径也做属性替换**） | 被引用工程**产出的那个件** + 它的闭包 |
| 工程自身产出 | `AssemblyName` 对 ITEMS 名字 | 工程自己的输出目录必须有它自己产出的件 |
| 输出目录 | `AppendTargetFrameworkToOutputPath` × `TargetFramework` | `bin/<Cfg>` 或 `bin/<Cfg>/<tfm>`（**只取存在的目录**，不混用父子目录） |
| `CycleStub.*` | 路径约定 | **不作为图节点**（桩件按构造只用于断环，其依赖不传递）—— 与 `SKIP(stub)` 同源 |
| 权威件本身 | ITEMS 表 | 权威路径必须存在 |

**"算不出来"不许退回绿**：`python3`/枚举器不可用时打印 `⚠️ 期望集合算不出来`，判定为 **`APPSYNC=NOINFO` + exit 3**（区别于 PASS/FAIL）；原生件同理逐条打 `EXPECT=UNKNOWN` 并列出"需要什么才能算"。

### 27.2 模型被**实测**修正的 5 处（每一处都是先看到噪声/漏报再改，不靠推演）

| # | 现象（实测） | 我的模型错在哪 | 修法 |
| --- | --- | --- | --- |
| 1 | `ManagedLayer.Tests × ReachFramework` 判"未期望" | 只把 `<ProjectReference>` 当边；而这里是 **HintPath 指向 PF 的 dll** | HintPath 目标目录 →（`dir2proj`）→ 该工程的闭包 |
| 2 | 沙箱里"产出目录那份"被判 `UNEXPECTED` | 漏了"**工程自己产出的件**要在自己的输出目录里" | 新增 `own` 边 |
| 3 | `UNEXPECTED=9 → 1`：`Probe/Tests→Provider`、`tests/*→WpfGfx` | 漏了 `<ProjectReference>` 的**产出件** | ProjectReference 边补"被引用工程产出的件" |
| 4 | `UNEXPECTED=1`：`DirectWriteForwarder/bin/Debug/Provider.dll` | `ProjectReference` 路径里的 `$(WpfLinuxRoot)` **没做属性替换** ⇒ 边被丢掉 | `project_refs()` 走同一套属性替换 |
| 5 | `MISSING=2`（多出一条 `System.Printing/bin/Debug/ReachFramework.dll`） | **跟着 ProjectReference 走进了 `CycleStub`**（PC→…→`CycleStub.PresentationFramework`→ReachFramework）⇒ 误期望。反证：PC 的 bin 里**根本没有** ReachFramework、PC 的 csproj 也不引它；而 `System.Printing` 是**今天 19:09 新建的**（RAR 真值） | CycleStub 不作图节点 ⇒ 该假期望消失 |

第 5 条尤其说明"模型必须拿**新构建**对照"：`System.Printing` 19:09–19:10 刚建完、`UNEXPECTED=0`、期望里它该有的都在 ⇒ 模型与 RAR 行为一致，不是自说自话。

### 27.3 四极实测（**命令原文 + 输出原文**，逐极）

**① 基线**（19:32:36）
```bash
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh ; echo exit=$?
解析源目录（csproj 的 HintPath 指向的输出目录）：16 个；未能解析的 HintPath：0 条（登记，不静默）
    摘要：解析源目录=16 期望副本=47 工程数=71 算不出的件=1 未解析HintPath=0
    MISSING       samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll  ← 期望来源：samples/HelloWpf/HelloWpf.csproj →(引用 Pres…
计数：OK=41  MISMATCH=0（STALE=0  NEWER-DIFF=0）  MISSING=1  UNEXPECTED=0  DIVERGENT=0  …
APPSYNC=MISMATCH（… MISSING=1 …）
exit=1
```
⇒ **基线不是 PASS**，而这**不是**本节改动引入的：新仪器抓到一条**既存**缺口（见 27.5）。我不把它藏起来、也不改判据去凑绿。

**② 删一份真实副本 ⇒ 必须红，且红的是"缺哪一份"**
```bash
$ rm -f build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll
$ python3 …/applocal-expect.py "$PWD" | grep '^#SUMMARY'
#SUMMARY|refdirs=16|expect=47|projects=71|unknown=1|unresolved_hintpath=0     ← **删前删后都是 47：基数不依赖现场文件**
$ bash …/check-applocal-sync.sh ; echo exit=$?
    MISSING       build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll  ← 期望来源：build/DirectWrite.Linux/SystemFontsProbe/DirectW…
    MISSING       samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll    ← 期望来源：samples/HelloWpf/HelloWpf.csproj →(引用 Pres…
计数：OK=40  …  MISSING=2  UNEXPECTED=0  …
APPSYNC=MISMATCH（… MISSING=2 …）
exit=1
```
（删前该副本是 `OK … a224b0973be9d7e2`；`OK 41→40`、`MISSING 1→2`，**逐条点名 + 期望来源 + 判据**。判据行随后紧跟：`判据：csproj 的 <Reference>(Private≠false)/<HintPath>/<ProjectReference> 引用图 + 该工程输出目录存在；**该判据不看现场文件**`。）

**③ 整份还原 ⇒ 回基线，且逐字节相同**（纪律 16：不做"反向替换"）
```bash
$ cp -p /tmp/reach_sfp.bak build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll
$ sha256sum 目标 /tmp/reach_sfp.bak        → 两边都是 a224b0973be9d7e2
$ cmp -s /tmp/reach_sfp.bak 目标 && echo 相同   → 相同
$ bash …/check-applocal-sync.sh ; echo exit=$?
计数：OK=41  …  MISSING=1  …            ← 回到基线（只剩 27.5 那条既存缺口）
exit=1
```

**④ 多出一份不该有的副本 ⇒ `UNEXPECTED`（红，不是静默）**
```bash
$ cp src/WpfGfx.Linux/bin/Debug/net10.0/WpfGfx.Linux.dll build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/WpfGfx.Linux.dll
$ bash …/check-applocal-sync.sh ; echo exit=$?
    UNEXPECTED    build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/WpfGfx.Linux.dll  63766fdd5e1d3e61（引用图**没有**要求这一份 ⇒ 多余副本/或新拷贝点未声明）
计数：… MISSING=1  UNEXPECTED=1 …
APPSYNC=MISMATCH（… UNEXPECTED=1 …）
exit=1
$ rm -f build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/WpfGfx.Linux.dll   # 实验副本已移除
```

### 27.4 期望集 47 vs 现场 find 到的副本 —— 两者为什么**不相等**

| 面 | 基数 | 说明 |
| --- | --- | --- |
| **期望集合**（权威枚举） | **47** = 现存 **46** + 缺 **1** | 覆盖 5 个件（Provider / WpfGfx / ReachFramework / libwpfwin32 / libwpfwic 的权威目录）；`wpfgfx_cor3.so` = 算不出（`EXPECT=UNKNOWN`） |
| **现场 find 到的副本** | **76** = 判定的 41 + `NO-AUTHORITY` 18 + `SKIP(obj)` 5 + `SKIP(stub)` 4 + `SKIP(ref)` 8 | 含原生件、obj 中间件、循环桩、Release/RID 副本等"按构造不参与判定"的类 |

⇒ **不等的部分正是"期望模型看不见/刻意不看的那些"**：前者只声明"谁引用了谁"，后者是磁盘上实际躺着的一切。**这正是解耦的目的**：`MISSING` 来自前者，`UNEXPECTED` 来自两者的差集。

### 27.5 新仪器抓到的**既存真缺口**（不是我这次改动引入的）

```
MISSING  samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll
         ← 期望来源：samples/HelloWpf/HelloWpf.csproj →(引用 PresentationFramework)→ … 的 <Reference Include="DirectWrite.Linux.Provider">
```
**判定依据**：HelloWpf 的 **Debug** 兄弟目录里这份**在**（`samples/HelloWpf/bin/Debug/net10.0/DirectWrite.Linux.Provider.dll`，今天 09:14 刷过）⇒ 同一条引用图在 Debug 上成立 ⇒ Release 那份缺 = 该 Release 输出目录是 **09-10 的旧构建**（目录内文件 mtime 全为 `09-10 14:58~15:13`），从未在"PF 依赖 Provider"之后的引用图下重建过。
**它不是"有人删了件"，而是"这个输出目录不是按当前引用图构建出来的"** —— 语义上仍是真缺口（该目录若被启动，缺一份 app-local）。
**owner/处置**：`samples/**`，不在我车道。两条路，**由主控裁定**：① 在统一波里跑一次 `dotnet build -c Release samples/HelloWpf/HelloWpf.csproj`（会把 PC/PF 的 Release 输出也带出来 ⇒ 写 `build/{PC,PF}.Linux/**`，属禁区，须你在波里做）；② 明确裁定"样例的 Release 输出目录不进收敛范围"（**那是口径变更，我不自己改**）。在此之前，基线保持 `MISSING=1` 的红。

### 27.6 仍然瞎的那一半（**如实报**）：脚本拷贝的**原生件存在性**

**实测**（第 5 极，19:33）：
```bash
$ rm -f build/MilBridge/tests/ContractProbe/bin/Release/libwpfwin32.so   # 拷贝点 = build/MilBridge/run.sh:228 的 cp
$ bash …/check-applocal-sync.sh ; echo exit=$?
    OK            build/MilBridge/tests/CompositeFontProbe/bin/Release/libwpfwin32.so  0098234982391bbf
    OK            build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/libwpfwin32.so    0098234982391bbf
    MISSING       samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll  ← （还是只有那一条既存缺口）
计数：OK=40  … MISSING=1 …
exit=1
$ cp -p /tmp/wpfwin32.bak <目标>   # 已整份还原：sha 与备份同为 0098234982391bbf
```
⇒ **删掉它没有任何 MISSING/UNEXPECTED**（`OK` 只是从 41 掉到 40）⇒ **原生件的"存在性期望"仍然算不出来**。
**为什么**：原生件的拷贝点是 **shell `cp`**，没有声明式来源。实测拷贝点：`build/MilBridge/run.sh:95`、`build/MilBridge/run.sh:228`、`build/publish-milbridge.sh:44`（发布目录）、`build/close-wave.sh:92/126/129`（只在核对时读）。
**需要什么才能算（三选一，须有 owner）**：① 一张 **owner 声明的期望表**（哪份原生件、哪些目录、由谁负责拷）；② 把拷贝改成**声明式**（csproj 里 `<None Include=… CopyToOutputDirectory="PreserveNewest">`）；③ 让那些脚本**自己登记**它们拷过的路径（跑完写一份 manifest）。
另两条已知边界：`wpfgfx_cor3.so` **无单一权威** ⇒ `EXPECT=UNKNOWN`；`SCAN_ROOTS` 不含 `tools/**` 与 `/tmp` 运行槽（§24.8.6 已登记）。

### 27.7 自检 A–L 12/12（新仪器自己的牙）

`bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh --selftest` ⇒ **`SELFTEST=PASS`（12/12）**。新增/更新的三条最相关：
* **K**：`删前=7 / 删后=7`（**期望基数不变**）+ 删一份 ⇒ `MISSING` 红 + **整份**还原 ⇒ 绿；
* **L**：多余的副本 ⇒ `UNEXPECTED` 且计红；
* B/E/F/G 的沙箱按**新契约**更新（"正确"现在包含"**有声明式来源**"；否则 `UNEXPECTED` 会替掉原本要测的语义）——这一条也记进报告，免得后人以为断言被削弱。

**边界**：本轮只改我车道两个文件（`check-applocal-sync.sh` sha `4348e8b469c1104f…`、新增 `applocal-expect.py` sha `c9d2bceebeebce6d…`）+ 本报告；**没跑波、没跑应用、没跑构建**；未碰 `build/shims/**`、`src/**`、`build/{PC,WB,PF}.Linux/**`。所有删除/污染**逐字节还原**（`cmp` 可验：`/tmp/reach_sfp.bak`、`/tmp/wpfwin32.bak`）。期间**别的车道在重建 port-lib**（`ReachFramework` 权威 `1fd4fe8a → a224b097`（19:11:21）、`libwpfwin32.so` `91baee84 → 00982349`（18:44）、`System.Printing` 19:09 新建）⇒ 本节的读数都带时间戳，且"期望集与 RAR 一致"这条判断正是靠那个 19:09 的**新构建**交叉验证的。

## 28. 裁定落地：**仪器必须自报"看不见的拷贝点"** + 基线红的登记（T2，2026-09-14 19:3x）

### 28.1 裁定① · `MISSING samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll` —— **走"修"，我不动**

**登记文字（供 CURRENT-STATE 抄录）**：
```
[仪器·既存·非本波引入] MISSING samples/HelloWpf/bin/Release/net10.0/DirectWrite.Linux.Provider.dll
  · 首次抓到：2026-09-14 19:32（T2 · app-local 校验器新期望集；REPORT.md §27.5 有全部原始输出）
  · 归属    ：owner = samples/**（T3 车道）；引入时间 = 该 Release 目录内文件 mtime 全为 09-10 14:58–15:13
              ⇒ 早于"PF 依赖 Provider"那一版引用图 ⇒ **该输出目录从未按当前引用图重建过**
  · 性质    ：不是"被人删了一份"，是**旧构建产物**；但语义上该目录仍缺一份 app-local（启动它就缺件）
  · 非本波引入：与 2026-09-14 的任何一波无关（目录时间 09-10）
  · 重启入口（主控在统一波里执行；T2 不构建）：
        dotnet build -c Release samples/HelloWpf/HelloWpf.csproj     # 会连带写 build/{PC,PF}.Linux/** ⇒ 禁区，须由主控在波里做
        bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh
        期望：OK 41→42 / MISSING 1→0
        若构建后仍 MISSING ⇒ **是引用图建模错，回报 T2 修模型**（不是样例的问题）
  · 判据纪律：**不得**把它改写成"样例的 Release 输出不在收敛范围"（那是口径变更）；**除非构建真的失败**，
              届时把失败原文交主控，由主控裁"口径"
  · 唯一复算命令（两条）：
        python3 build/DirectWrite.Linux/wic-shim/applocal-expect.py "$PWD" | grep -E '^#SUMMARY|^#UNKNOWN'
        bash    build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh
  · 现状：这条红**保持红**（基线 `APPSYNC=MISMATCH（… MISSING=1 …）` exit 1），直到上面那趟构建跑完
```
**时序纪律**：主控告知**此刻正有波在跑（T1d 的 `D-T1`，`WAVE_OWNER=close-wave:t1d`）** ⇒ **本节我没有跑任何构建、没有发波**；本节唯一的两处文件动作是仪器实验（删一份副本 / 删一个写点产物），**都在我车道或非禁区、且都整份还原并 `cmp` 验证**（见 28.2）。

### 28.2 要求 A（**已落地**）：`#SUMMARY` 与输出里显式印出"看不见的拷贝点"

**实现**（`applocal-expect.py` 新增 `invisible_copy_sites()`；本节点两个文件：`applocal-expect.py` sha `7c131b3f33b7e74e…`、`check-applocal-sync.sh` sha `3ba5284bad838b14…`）：扫 `build/**/*.sh`（排除 `upstream`/`.artifacts`/**本仪器自身** `wic-shim/**`），**排除注释行与纯 `echo/printf` 行**，只看提到 ITEM 文件名的行，再分类：
* 含 `cp|install|mv|rm`（或输出重定向）⇒ **写点**（会创建/删除副本 ⇒ 本枚举器看不见 ⇒ 删除不会被报出）；
* 其余（`find` / 赋值 / `nm` / `sha` / 条件判断）⇒ **只读**（只读或枚举，不产生副本）。

**输出原文（19:41 现场）**：
```
    摘要：解析源目录=16 期望副本=47 工程数=71 算不出的件=1 未解析HintPath=0
    ⚠️ **本校验器看不见的拷贝点：写点 3 处 / 只读 17 处** —— 写点会创建或删除副本，但**不在**期望模型里
       ⇒ 这些路径上的**删除不会被本校验器报出**（今天实测过：删一个写点产物只是 OK 41→40，无任何红行）
       [写点] build/publish-milbridge.sh:44 ｜ cp -f "$WIC_SRC" "$PUB/libwpfwic.so"
       [写点] build/MilBridge/run.sh:95 ｜ cp "$ROOT/src/WpfGfx.Linux.Native/bin/libwpfwin32.so" "$MB/tests/CompositeFontProbe/bin/Release/" 2>/dev/null || true
       [写点] build/MilBridge/run.sh:228 ｜ cp "$MB/../../src/WpfGfx.Linux.Native/bin/libwpfwin32.so" "$MB/tests/ContractProbe/bin/Release/" 2>/dev/null || true
       [只读] build/close-wave.sh:92 ｜ NATIVE_AUTH="src/WpfGfx.Linux.Native/bin/libwpfwin32.so"
       [只读] build/close-wave.sh:126 ｜ done < <(find . -name 'libwpfwin32.so' …)
       [只读] build/close-wave.sh:129 ｜ done < <(find . -name 'libwpfwin32.so' …)
       [只读] build/publish-milbridge.sh:31 / :39 / :54 ｜（赋值/存在性判断/for 列表）
       [只读] build/MilBridge/run.sh:47 / :194 ｜（nm 读数 / 变量续行）
       [只读] build/MilBridge/tools/t1b-ls-tripwire.sh:73、t1c-census.sh:87/:90/:100 ｜（工具脚本读权威路径）
       [只读] build/DirectWrite.Linux/{WicClosedLoop,WicWriteClosedLoop,FontEntryClosedLoop}/run-*.sh:29 ｜ shim="$shim_dir/libwpfwic.so"
       （目标形态：拷贝点写 manifest ⇒ 本校验器读 manifest、缺 manifest 报 NOINFO；见 28.3）
```
⇒ **写点 3 处 = 主控点名的那三处**（`publish-milbridge.sh:44`、`run.sh:95`、`run.sh:228`）；只读 17 处（清单每文件最多列 3 条，**计数是全量**）。
**机器可读**：`#SUMMARY|…|invisible_copysites=20|invisible_write=3|invisible_read=17`（`#INVISIBLE|write|file:line|原文` 逐条给机器读）。

**判据实测**（主控要求的那条：删一个脚本拷贝点的产物 ⇒ summary 里必须有"这类我看不见"的显式字样）：
```bash
$ cp -p build/MilBridge/tests/ContractProbe/bin/Release/libwpfwin32.so /tmp/wpfw32_b.bak   # 备份 0098234982391bbf
$ rm -f build/MilBridge/tests/ContractProbe/bin/Release/libwpfwin32.so
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh | grep -E "看不见|计数|APPSYNC"
    ⚠️ **本校验器看不见的拷贝点：写点 3 处 / 只读 17 处** —— …
       ⇒ 这些路径上的**删除不会被本校验器报出**（今天实测过：删一个写点产物只是 OK 41→40，无任何红行）
计数：OK=40  MISMATCH=0（STALE=0  NEWER-DIFF=0）  MISSING=1  UNEXPECTED=0  DIVERGENT=0 …
$ cp -p /tmp/wpfw32_b.bak <目标> ; cmp -s /tmp/wpfw32_b.bak <目标>   # 整份还原：sha 同为 0098234982391bbf、逐字节相同
```
⇒ 以前的**静默**形态（只 `OK 41→40`）现在**自带解释**：读者不会再以为 `APPSYNC=PASS/MISMATCH` 覆盖了全部副本。
**新自检 M**（写进 `--selftest`）：断言输出里同时出现「本校验器看不见的拷贝点」「[写点]」「不会被本校验器报出」三件。⇒ 自检 **A–M 13/13 `SELFTEST=PASS`**。

### 28.3 要求 B（**登记，不现在做**）：目标形态 ③「拷贝点写 manifest」的落点与 owner

| 项 | 内容 |
| --- | --- |
| 目标形态 | 每个**写点**在 `cp` 之后追加一行到 manifest（`sha256  <绝对路径>  written_by=<脚本:行>`），例如 `build/MilBridge/tools/applocal-copy-manifest.txt`（或各脚本同目录一份，实现时定） |
| 落点与 owner | `build/MilBridge/run.sh`、`build/MilBridge/publish-milbridge.sh` ⇒ **T1b/T1c 车道**；`build/close-wave.sh` ⇒ **主控车道** ⇒ 脚本侧由主控协调，**T2 不改这些脚本** |
| T2 侧（检查器） | ① 读 manifest，把其中路径**并入期望集合**（⇒ 这些原生件的删除会报 `MISSING`）；② **缺 manifest ⇒ `EXPECT=UNKNOWN` + `APPSYNC=NOINFO`（exit 3）**，不静默；③ 自检加一条：manifest 存在但路径缺件 ⇒ 必须红 |
| 重启入口（**一句可执行的话**） | 主控说"接线"后我执行：`grep -rl applocal-copy-manifest build/MilBridge/run.sh build/MilBridge/publish-milbridge.sh build/close-wave.sh` 有命中 ⇒ 我就加"读 manifest + 缺 manifest 报 NOINFO"两处（预计 ~20 行，只动我车道），随后 `bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh` 应从"原生件 `EXPECT=UNKNOWN`"变成"manifest 覆盖 N 条"，且删一个 manifest 里的产物**必须变红** |

### 28.4 裁定③ 与边界

* 裁定③照收：自检 **A–L 12/12 → 现 A–M 13/13**；"5 个模型错被实测打掉"（尤其 **`CycleStub` 作图的错靠**19:09 的**新构建**交叉验证打掉**，不是自说自话）已写进 §27.2，作为纪律保留。
* **改动只有我车道两个文件**：`build/DirectWrite.Linux/wic-shim/applocal-expect.py`（sha `7c131b3f33b7e74e…`）、`build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh`（sha `3ba5284bad838b14…`，含新增自检 M）+ 本报告（sha `51a732ee290fb094…`）。
* **没跑构建、没发波、没跑应用**；未碰 `build/shims/**`（T1d 正在写）、`build/**/bin/Debug` 的既有权威件、`samples/**`（T3）、`src/**`、`build/{PC,WB,PF}.Linux/**`。
* 期间**别的车道在跑波**（T1d 的 `D-T1`，`WAVE_OWNER=close-wave:t1d`）⇒ 本节读数时间戳：**19:41**；两处文件实验均**整份还原**（当时 `cmp` 相同；`build/MilBridge/tests/ContractProbe/bin/Release/libwpfwin32.so` 备份留 `/tmp/wpfw32_b.bak`，19:38 复核仍逐字节相同）。
* **一条事后变化（如实记，避免误读成我的残留）**：`build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/ReachFramework.dll` 在我实验时是 `a224b097`（已从 `/tmp/reach_sfp.bak` 整份还原并 `cmp` 验过）；19:38 复核时它已是 **`96c9e238` = 当前权威 sha**（`build/ReachFramework.Linux/bin/Debug/ReachFramework.dll` 被别的车道再次重建，副本随管线同步）⇒ 它**等于当前权威、不是实验残留**（校验器同时报 `STALE=0`）。

## 29. `D-F1` 字体回退：**只读设计 + 判据**（T2，2026-09-14；不落码、不开构建）

**派单**：`与`（U+4E0E）落 `.notdef` 是"折叠区间宽为负"（`cr.Width = −3.0560`）的**唯一真违反根因**。本节只做 ①现状 ②修法设计 ③判据 ④两处禁止 ⑤诚实边界。
**读数版本**：shim `build/shims/PresentationCore.HbTextLine.cs` sha `3081d088cda0431cfef20988…`（4169 行，**只读**，T1d 车道）；真值 `tests/parity/windows/font-fallback/out/font-fallback-oracle.json`（raw sha `62e2d45d…`）+ b34 真值 `tests/parity/windows/layout-b34`（经 `build/MilBridge/gen/layout-b34-compact.json` 复读）。

### 29.1 现状：`与` 为什么落 `.notdef`（**逐环给 `file:line`**）

**环 1 · 段落字体确实没有这个码点**（可复算，我自己量的）：
```
$ python3 - <<'PY' … 读 build/fonts/NotoSans-Regular.ttf 的 head/hmtx/cmap
NotoSans-Regular: unitsPerEm=1000  glyph0(.notdef) advance=600 ⇒ 0.6000 em = 9.6000 DIP @em16 = 14.4000 @em24
  U+4E0E 在这份面里的 glyph = 0  （⇒ .notdef）
```
⇒ **9.6000 DIP @em16 就是"段落字体 glyph 0 的 advance"**（与派单给的数逐位一致；@em24 = 14.4 也正好是真值 `advancesSeenForMissingCodePoints` 里的那一档）。用例 `F_nbsp_zwsp_w40` 的 `fontKey = "file"` ⇒ 段落字体就是这份文件（`build/MilBridge/tests/HbTextLineParity/Program.cs:269`）。

**环 2 · 我们的字体栈**有**回退机制**，而且是按真机语义写的（`build/shims/PresentationCore.HbTextLine.cs`）：
| 处 | `file:line` | 做什么 |
| --- | --- | --- |
| 计划构造 | `:1165` `HbPlanBuilder.Build(text, runs, allowFallback, gate)` | 逐码点：当前面不覆盖 ⇒ 找替代面 |
| 覆盖判据 | `:1183` + `:753/:769`（`hb_font_get_nominal_glyph != 0`；`:761` 明确 `-1=面不可用` 与 `0=.notdef` **严格分开**） | 只用 HarfBuzz 自己答，不碰 provider 反射 |
| 候选② | `:1191` `PickFromRuns(...)` | 本段**其它 run 的面** |
| 候选③ | `:1204` `HbFontCandidates.TryFindCovering(...)` | **机器字体目录扫描** |
| 闸门 | `:1207`（调用方 PC 层给的 `gate`） | "覆盖"且"能渲染"（`GlyphTypeface` 能物化、cmap 与整形面一致）——只覆盖不可渲染**当成没找到** |
| 结果 | `:1220 NoteApplied` / `:1224 NoteFailed`（**沿用当前面，不假装成功**） | |
| 候选清单 | `:885-905` `ResolveDirs()`（`WPF_LINUX_FONT_DIR` 覆盖；否则 XDG/HOME + `/usr/share/fonts` + `/usr/local/share/fonts` + `~/.local/share/fonts` + `~/.fonts`）、`:926` 枚举 `*.ttf/*.otf/*.ttc`、`:932` `files.Sort(StringComparer.Ordinal)` | |
| 排序 | `:964-989` `TryFindCovering`：`|Δweight| → |Δwidth| → |Δslant|`，同分**按扫描序**（Ordinal 路径序） | |
| 观测 | `:492` `HbLineSegment.ToString()` = `"[start,end) Face (按码点回退) slot=N"`；`:1004-1099` `HbFallbackDiag`（`fallbackApplied / fallbackFailed / fallbackUnrenderable / fromRunFaces / fromSystemScan / coverageProbe`） | **我们侧"到底用了哪个面"的读数** |

而且它在**应用路径上确实生效**（T3 那趟 TextBox dump）：`fallbackApplied=2 fromSystemScan=2 coverageProbe=63 …`。

**环 3 · 但这条路径上"计划根本不存在"**（**这就是根因**，不是"回退写错了"）：
```
build/shims/PresentationCore.HbTextLine.cs:3392   HbFontPlan plan = null,   // 注释原文：null ⇒ 与今天逐位相同的单面路径
                                            :3393   GlyphTypeface[] segmentFaces = null, …
                                            :3404   double[] adv = (plan != null && plan.Segments.Count > 0)
                                            :3405       ? HbBreakEngine.MeasureChars(text.Substring(…), plan.Sub(…), emSize)
                                            :3407       : HbBreakEngine.MeasureChars(text.Substring(…), fontPath, emSize);   // ← 单面
build/MilBridge/tests/HbTextLineParity/Program.cs:426
      HbTextLineFactory.FormatParagraph(text, font, em, width, gt, 1.0f, props, ac, hasModifier, lineHeight, out consumed,
                                        defaultIncrementalTab: 0);        // ← 既没传 plan: 也没传 segmentFaces:
```
⇒ 这条路径**从不构造 `HbFontPlan`** ⇒ `allowFallback` 那一段（`:1189`）**根本不会被问到** ⇒ `与` 一直由 `NotoSans-Regular.ttf` 单面整形 ⇒ `.notdef` ⇒ **9.6000**。
⇒ **一句话根因**：**回退住在"计划构造器"里，而"单面路径"（`plan == null`）绕过了它**；harness 走的正是单面路径。这与 §23 时代那条"`+CJK` 34 条 = harness 单字体入口无 CJK 回退"是**同一个结构事实**，只是当时只能登记"结构性不可比"，现在它有了真值（`font-fallback` oracle）就变成**可修的真违反**。

**环 4 · 本机覆盖情况（回退能不能成功）**：
```
$ fc-list :charset=4e0e | wc -l            → 89      （真机：15 族 / 80 face）
$ for cp in 4e0e 6c49 05d0 0627 2192 e000 10fffd; do fc-list :charset=$cp | wc -l; done
  U+4E0E: 89   U+6C49: 89   U+05D0: 26   U+0627: 24   U+2192: 240   U+E000: 2   U+10FFFD: 0
```
⇒ 本机**有** 89 个覆盖面（NotoSansCJK-*.ttc / DroidSansFallbackFull.ttf / NotoSerifCJK-*.ttc …）⇒ 回退**能成功**；`U+10FFFD` **两边都是 0** ⇒ 那一档两边都落 `.notdef` ⇒ **可比**（派单同判）。

### 29.2 修法设计（对齐**结果语义**，不假装复刻真机顺序）

**要实现的语义**（真值 Q1 逐字）：段落字体没有该码点时，**不用**它的 `.notdef`，而是**替换成另一个覆盖该码点的已安装字体，并报那个字体自己的字形与 advance**；只有当**回退也够不到任何覆盖面**时才用段落字体的 `.notdef`。

**落点（三处，按"谁的车道"分清）**：
1. **`plan == null` 时也要走回退**（核心）：把 `FormatParagraph` 的单面分支改成"**先构造计划**（`allowFallback: true` + `gate`），构造不出来再退回单面"，即让"没有计划"不再等于"没有回退"。落点 `build/shims/PresentationCore.HbTextLine.cs:3404-3410`（**T1d 车道**）。
2. **候选集与顺序**：沿用现成的 候选②→候选③（`:1191`/`:1204`）；**候选③ 的序是我们自己的**（`ResolveDirs` + Ordinal + `|Δweight|→|Δwidth|→|Δslant|`）。**真机顺序取不到**（U1 明确"只知最终用了谁"）⇒ **本设计只对齐"结果语义"**：报回退面的字形与 advance、`.notdef` 只在没找到时。**我们不许声称顺序与真机一致**，也不许为了让某个数字对上而去改顺序。
3. **观测面**：我们侧用 `HbLineSegment`/`HbFallbackDiag`（`:492`/`:1004`）；**真值用的是公开 API `TextLine.GetIndexedGlyphRuns()`**，而它在我们这儿**仍是 owed**（`:17` 逐字："**仍留 owed**（上游 0 个调用点，主控裁定不实现）"）⇒ **判据要用"我们的装置"表达**（见 29.3），并把"装置不同"写进边界（29.5）。

**回退失败时**：`NoteFailed` ⇒ 沿用当前面 ⇒ 该码点 glyph 0 ⇒ advance = **该面 glyph 0 的 advance**（本机 Noto 文件字体 = 0.6 em = 9.6 @16 / 14.4 @24）✔ 与真机"零覆盖档用段落字体 `.notdef`"一致（真值 18 / 18.667 / 14.4 即三个不同段落字体的 glyph 0 宽）。

### 29.3 判据（**必须可复算**；三条并列，缺一不可）

**真值里"与"那一档（逐字）**：`single-Arial` ⇒ 实测 advance `24.000000`、观察字体 `YUGOTHM.TTC#1`、观察 glyph `3883`、`isNotdef=False`；`explicit-covering-Microsoft YaHei` ⇒ `24.0` / `MSYH.TTC` / `1034`；**零覆盖档**（U+10FFFD）⇒ 用段落字体 `.notdef`（Arial 18 / Times 18.666667 / Noto 文件 **14.4**）。

| # | 判据 | 怎么复算 | 通过线 |
| --- | --- | --- | --- |
| **C1 自洽性（主判据）** | **我们报的 advance == 我们所选面自己的 advance**（不是"必须等于 24.0"） | 从我们侧读数取"选了哪个面"（`(按码点回退)` + `Face`/`HbFallbackDiag`），再用 cmap/hmtx 独立算该面在 U+4E0E 上的 advance：`advance/upem × emSize` | **逐位相等**（同一份面、同一 upem ⇒ 无容差） |
| **C2 不是 `.notdef`** | 该码点选到的 glyph **≠ 0** | 用 `HbFaceCache.NominalGlyph(path, faceIndex, 0x4E0E)`（`:769`）读；真值侧对应 `observedGlyphIsNotdef == False` | **glyph ≠ 0**（真值口径：`GlyphIndices[0] == 0` 才是 .notdef） |
| **C3 与真值的差 ≤ 容差（字体不同则标不可比）** | `F_nbsp_zwsp_w40` 的 `与 ` 行：`w` 与 `cr.Width` | 见下 | `w`：真值 `16.0000`；`cr.Width`：真值 `3.3433` ⇒ 差 ≤ **0.01（严）/ 0.34（项目口径）** |

**C3 的算式（我把定义也回了源码，避免"看着像"就对）**：`cr.Width = 原Width − 折后Width`（§23.6 当时从 shim 原文确认；**该行号已随 T1d 的编辑漂移**：今天同一句在 `build/shims/PresentationCore.HbTextLine.cs:3104`，我已按当前版本重读）。真值 `F_nbsp_zwsp_w40` 的 `与 ` 行：`w=16.0000`、`witw=20.1600`、`ce.w=12.656666666…`、`cr=[{idx 14, len 2, Width 3.34333…}]` ⇒ **16.0000 − 12.6567 = 3.3433** ✔ 自洽。
**我们侧预测（**是预测，不是实测**——现在不许构建）**：一旦 `与` 拿到回退字形（本机任一 CJK face 都是 **1.0000 em ⇒ 16.0000 DIP @em16**，实测：`DroidSansFallbackFull.ttf` glyph 7078 upem 256 ⇒ 256/256；`NotoSansCJK-Regular.ttc` face0 glyph 9497 / face2 glyph 9498，upem 1000 ⇒ 1000/1000），则 原Width = **16.0000**，我们自己的折后宽不变（**12.6560**，与真值 12.6567 差 0.0007）⇒ **`cr.Width` 从 `−3.0560` 变 `+3.3440`，与真值 `3.3433` 差 0.0007** ⇒ 三把尺子（自洽、0.01、0.34）全部通过。
**"字体不同则标不可比"的写法**：C3 必须**同时**记录"我们选中的面"与"真机选中的面（YUGOTHM.TTC#1）"。若两者不同（本机几乎必然不同：本机没有 YUGOTHM），则 **C3 只作"量级/符号"判据**（1 em ± 容差），**不得**声称为"逐字对齐"；`U+10FFFD` 那一档两边都落 `.notdef` ⇒ **C3 在这一档是完全可比的**（都比"段落字体 glyph 0 的 advance"）。

**两极化牙（设计，落码后按此实测）**：
* **负极**：把回退关掉（现成开关就是 `allowFallback: false`；单面路径天然是"关"的状态）⇒ 复算必须得到 **`与` advance = 9.6000**、`与 ` 行 `w = 12.6560`、**`cr.Width = −3.0560`**（与今天逐位相同）；
* **正极**：打开 ⇒ `与` advance = **所选面的真实 advance**（本机 1 em ⇒ 16.0000）、`cr.Width` **变正**（预测 +3.3440）；
* 两趟都留原始读数（`HbFallbackDiag` 那行 dump + 该行的 `Diagnostics`/`Face`），**不许只报"我测过了"**。

### 29.4 两处禁止（**设计约束里就写明，免得落码时走捷径**）
1. **不许**把 `.notdef` 的 advance "硬改成 1 em"来让数字对上 —— 那是**把假值当值**（与 `D-U1` 裁定同族）。本设计的判据 C1 专门堵它：**必须报"所选面自己的 advance"**，而"所选面"必须能被 C2 的 glyph≠0 与 `(按码点回退)` 读数证出来；硬改 `.notdef` 宽度会让 C2 红。
2. **不许**改 oracle/真值（`tests/parity/windows/font-fallback/**`、`layout-b34/**` 一律只读）。

### 29.5 诚实边界（我们**做不到**的部分）
1. **真机回退搜索顺序取不到**（U1 原文："只知最终用了谁"）⇒ 本设计只对齐**结果语义**（报替代面的字形/advance；找不到才 `.notdef`）；**任何"顺序一致"的说法都是假的**。
2. **字体集不同 ⇒ 逐字对齐不可能**：真机 `YUGOTHM.TTC#1`/`MSYH.TTC`，本机 89 个覆盖面里没有它们 ⇒ 我们选中的面**几乎必然不同** ⇒ 该码点的 advance 只能比"1 em"这类**字形性质**，不能比"具体数值等于 24.0"。（本机实测三个候选 face 都是 1.0000 em，这一点让 C3 在"全角字形"这一档**实质可比**。）
3. **零覆盖档可比**：`U+10FFFD` 两边都是 0 个覆盖面 ⇒ 两边都落 `.notdef` ⇒ 这一档的真值（18 / 18.666667 / **14.4**）与我们（Noto 文件字体 14.4 @em24）**是同一口径**，可直接比。
4. **观测装置不同（必须登记）**：真值用公开 API `TextLine.GetIndexedGlyphRuns()`；我们侧该 API **仍是 owed（`:17`，主控裁定不实现，上游 0 个调用点）** ⇒ 我们只能用 `HbLineSegment`/`HbFallbackDiag` 这套**内部**读数。⇒ 后果：真值那列的"观察字体 URI / `GlyphIndices[0]`"我们**不能逐字段对齐**，只能对"字形≠0 + 面身份 + advance 自洽"。**要不要为此实现 `GetIndexedGlyphRuns()` 是主控的决定**（我不擅自加）。
5. **我这次的"预测我们选谁"不可靠**（如实说）：我为了给"候选③会不会撞到 CJK 面"一个直觉，用自写的纯 python 单面解析器扫了一遍 —— 它**不认 `.ttc` 容器**（只按 TTF 读），所以只报了 `DroidSansFallbackFull.ttf` 一个命中，而 fontconfig 说 89 个 face 覆盖。⇒ **"我们会选哪个面"必须以运行时读数为准**（`HbFallbackDiag`/`(按码点回退)`），**不许拿我这个预测当值**（这正是"取真值不做推断"的一条实例）。
6. **一个待确认的口径问题**：我们现在这条路径的 `w`（我们 12.6560 vs 真值 16.0000）在修好后应变成 16.0000；但 `与 ` 行**行尾空白**的处理（真值 `w=16.0000 / witw=20.1600`，差 4.16 = 一个空格）说明真值把尾空格排除在 `w` 外 —— 这条我们本来就有专门判据（§23.6 的 `ws` + `WITW−W` 关系），**修回退时不要顺手改空白口径**（两件事分开验）。

## 30. `D-F1` 判据 runner（我这半已落地，`build/DirectWrite.Linux/**`；只读 shim、不开构建）

**派单**：`#13` 冻结（`hbtextline fde9e511e8443cf2`）⇒ D-F1 排队下一件；我落"判据侧"，`plan==null` 的修法归 T1d 的 shim 车道。**本轮我没有构建、没有发波、没有碰 shim/MilBridge/samples/src/docs。**

### 30.1 交付物（5 个文件）+ 唯一复算命令

| 文件 | 作用 |
| --- | --- |
| `build/DirectWrite.Linux/FallbackCriteria/FallbackCriteria.csproj` | 判据 runner 工程；骨架逐条照抄 `HbTextLineParity.csproj`（`<Compile Include="$(HbShimSrc)">` **编入 shim 真源** ⇒ 拿 internals；PC/WB/DWF 自产件 + SkiaSharp 2.88.9） |
| `build/DirectWrite.Linux/FallbackCriteria/Program.cs` | **只做读数**：三模式 `null`/`fb`/`nofb`（`plan:null` / `allowFallback:true` / `allowFallback:false`，同一构建内做完两极化）+ `Collapse`/`GetTextCollapsedRanges` + C2 探测 |
| `build/DirectWrite.Linux/FallbackCriteria/eval-df1-criteria.py` | **判据唯一实现**：C1/C2/C3 + 两极化期望 + `[fix]` 判定；被判对象 = `null`（应用/harness 实际走的路径），`fb`/`nofb` 只作牙 |
| `build/DirectWrite.Linux/FallbackCriteria/advance_from_font.py` | **C1b 独立复算**：读 `head.unitsPerEm` + `hmtx`（**支持 `ttc` 容器**）⇒ 不信 `GlyphTypeface` 的自述 |
| `build/DirectWrite.Linux/FallbackCriteria/run-df1-criteria.sh` | **唯一复算命令**（含 NOINFO 分支与退出码约定） |

```bash
# 唯一复算命令（现在缺的只是"构建"这一步 —— 派单要求本刻不开构建）
bash build/DirectWrite.Linux/FallbackCriteria/run-df1-criteria.sh --build   # 先构建（会编 shim 真源，负载高时等静树）
bash build/DirectWrite.Linux/FallbackCriteria/run-df1-criteria.sh           # 只跑（要求已构建）
```
退出码：**0** = C1/C2/C3 全过；**1** = 有 FAIL；**3** = `NOINFO`（= runner 未构建 / `GetIndexedGlyphRuns` 仍是桩 / 缺真观测面）—— **NOINFO 不等于通过**。

### 30.2 判据行格式（逐字，实跑样例）

```
[null] 读数 LINE_W=16.0000 WITW=20.1600 COL_W=12.6560 CR=[14,2) CR_W=3.3440 ADV=16.0000 ADV_FROM_TYPEFACE=16.0000 INDEXED=OK GID=9497 FACE=file:///…/NotoSansCJK-Regular.ttc
[null] C2=PASS（glyph=9497 ≠0 非 .notdef）
[null] C1=PASS（C1a API 内一致=True；C1b 独立复算=PASS）
[null] C3=PASS（LINE_W vs 16.0差 0.0000；CR_W vs 3.3433差 0.0007；符号 正；字体与真值**不同 ⇒ 该行只作量级/符号判据，逐字对齐不可能**）
[fb] TOOTH-POS=PASS（advance 应为所选面的 1 em（本机候选面实测 1.0000 em）；CR_W 必须转正）
[nofb] TOOTH-NEG=PASS（必须复现修前 9.6/12.656/-3.056）
[fix] MODE=null == fb ⇒ **修法已生效**（null=16.0000 fb=16.0000 nofb=9.6000）
CRITERIA=PASS（fail=0 noinfo=0；观测面=GetIndexedGlyphRuns()；真值=16.0/20.16/3.3433←YUGOTHM.TTC#1 glyph3883）
```
**C1 的两条腿**：`C1a` = 公开 API 对内一致（`GlyphRun.AdvanceWidths[0]` vs `GlyphTypeface.AdvanceWidths[gid] × emSize` —— **与真值同一对 API**）；`C1b` = **独立复算**（`advance_from_font.py` 读该面的 cmap/hmtx，逐位相等无容差）。
**为什么三条并列**：实测证明 **C1 抓不到这个 bug**（`.notdef` 的 9.6 **是** NotoSans-Regular 自己 glyph 0 的 advance ⇒ 自洽）——`C1` 的作用是**堵"把 `.notdef` 硬改成 1 em"那条捷径**（硬改会让 C1 红）；"回退有没有发生"由 **C2（glyph≠0）+ C3（转正/对真值）** 判。

### 30.3 修前读数（**可复现**）
* **`advance = 9.6000`（一手复算）**：`python3 advance_from_font.py build/fonts/NotoSans-Regular.ttf 0 16` ⇒ `ADV=9.6000 UPEM=1000 ADV_UNITS=600`；该面里 `U+4E0E` 的 gid = **0** ⇒ `.notdef`。
* **`cr.Width = −3.0560`（当版产物）**：`build/MilBridge/gen/tline-detail-full.txt`（头部"被测 shim 源 sha256 = **FDE9E511…**" = `#13` 冻结版）行：
  `F_nbsp_zwsp_w40|行#3|我们 Len=2 W=12.6560|真值 Len=2 W=12.6567|差 W=-0.0007|cr 我们=[14,2) W=-3.0560|cr 真值=[14,2) W=3.3433`
  ⇒ 由 `cr.Width = 原Width − 折后Width`（shim 当前 `:3104`）反解：我们折后 = `12.6560 − (−3.0560) = 15.7120`。
* **修后预测（未实测）**：`原Width` 16.0000 − 我们折后 12.6560 = **+3.3440**（真值 3.3433，差 0.0007）—— 已在 §29.3 标注为**预测**，落码后由本 runner 实测。

### 30.4 `NOINFO` 分支样例（两种来源，**都不许报绿**）
```
$ : > /tmp/df1-empty.txt ; python3 eval-df1-criteria.py < /tmp/df1-empty.txt ; echo exit=$?
CRITERIA=NOINFO reason=no-runner-output（runner 未构建或未运行 ⇒ **不等于通过**）
exit=3
```
```
# 观测装置未实现（= 今天的真实形态：GetIndexedGlyphRuns 是 Owed 桩）
[null] C2=NOINFO（GetIndexedGlyphRuns() 仍是 Owed 桩 ⇒ 观测面不可用；**不许报绿**）
[null] C1=NOINFO（缺真观测面 ⇒ 无法证明"报的是所选面自己的 advance"）
[fix] MODE=null == nofb ⇒ 修法**尚未**生效（今天的根因仍在）
CRITERIA=NOINFO（fail=2 noinfo=2；…）
exit=3
```
**C2 的"未实现"是确定性探测的**，不是猜：`Program.cs` 先设 `WPF_LINUX_TEXTLINE_STRICT=1` 再调它 —— 桩会**抛 `NotSupportedException`**（shim `:3300-3309`：`Owed("GetIndexedGlyphRuns")` 逐字），正常模式则**返回空序列**（⇒ 也不能当"没有字形"）。**两者都判 `UNIMPLEMENTED`**。

### 30.5 待验证的预期（**只写预期，落码后实测**）
**预期**：同一处 `plan == null` 缺口**就是** §23/§24 那 **34 条 `+CJK` Extent 行**的结构性来源（当时 T1d 只给到"harness 单字体入口无 CJK 回退"这个结构判断）。
**怎么验（落码后一条命令）**：跑 `run.sh tline` ⇒ 取 `tline-detail-full.txt` 段2，比较 `+CJK` 行集合与计数（今天 34 条；Extent 行级 `1260/1298`）。
**两种结果都要如实收**：① `+CJK` 行显著减少/归零 ⇒ 预期成立（那 34 条从"结构性不可比"升级为"可比"，但仍受"字体不同 ⇒ 量级判据"约束）；② **不变** ⇒ 说明回退**没被走到**（例如 PC 层 `gate`（"覆盖且能渲染"）把它们全判 `NoteUnrenderable`）⇒ 那是**更深一层的根因**，回报主控，不许当作"预期不成立"了事。

### 30.6 两条禁止（写死在判据里）
① **不许**把 `.notdef` 的 advance 硬改成 1 em（C1 专堵：必须报"所选面自己的 advance"，且 C2 要 glyph≠0 可证）；② **不许**改 oracle/真值（`tests/parity/windows/font-fallback/**`、`layout-b34/**` 一律只读）。

### 30.7 我**答不出 / 做不到**的清单（诚实边界）
1. **真机回退搜索顺序取不到**（U1 原文"只知最终用了谁"）⇒ 只对齐结果语义；任何"顺序一致"的说法都是假的。
2. **本机字体集不同**（无 `YUGOTHM.TTC#1`/`MSYH.TTC`）⇒ **逐字对齐不可能**；C3 在该码点只作"1 em 字形性质 + 符号/量级"判据（本机三个候选 face 实测都是 1.0000 em ⇒ 这一层实质可比）。
3. **我现在给不出 runner 的运行读数**（派单要求本刻不开构建）⇒ 本轮只有"判据层"被实跑自证（合成日志三情形）+ **一手复算的 9.6000** + 当版产物里的 −3.0560；真实读数等构建解禁。
4. **牙的观测在 API 未实现前是缺的**：`nofb` 极的 `ADV` 依赖 `GetIndexedGlyphRuns()`（今天 NOINFO）⇒ 现在只能从 `MODE=… DIAG=…`（shim 内部装置，含 `(按码点回退)` 与面路径）读；**已设计但未实现**的补法：让 `eval` 在 API 空缺时从 `DIAG` 解析面路径作**次级来源**，并明确标注"内部装置、非公开 API"（C1/C2 仍只用公开 API）。要不要现在就加，请主控定。
5. **C1b 的覆盖面**受我的字体读取器限制：支持 ttf/otf/**ttc**（face 索引），但**不解析 CFF 的 `CFF ` 表内部**（advance 走 `hmtx`，对 CFF 面同样有效；若某面没有 `hmtx` 则报 NOINFO 而不是猜）。
6. **`TOOTH-*` 在 `ADV=nan` 时会判 FAIL**（读数缺失）⇒ 总判定仍被 `NOINFO` 优先覆盖（exit 3），但这意味着**"牙"本身也要等 API 落地才算真验过**。

### 30.8 主控四条裁决落档（2026-09-14）+ 次级来源已实现

**① `NOINFO`（exit 3）批准为核心纪律**：`0`=过 / `1`=FAIL / **`3`=NOINFO（≠通过）**；"`GetIndexedGlyphRuns()` 仍是桩"用**确定性探测**（`WPF_LINUX_TEXTLINE_STRICT=1` ⇒ `Owed` 抛 `NotSupportedException`；正常模式**空序列也不能当"没有字形"**）。

**② 判据分工**改判（依据我实测的"C1 抓不到这个 bug"）—— 照主控裁定写死：
* **C2（`GlyphIndices ≠ 0`）+ C3（与真值差/符号）= 判"回退有没有发生"的判据**（原来是 C3 挂在"量级"名下）；
* **C1 = 反作弊**（堵"把 `.notdef` 硬改成 1 em"那条捷径：必须报"所选面自己的 advance"）；**不再称 C1 为"主判据"**（§30.2 里我写过的"主"字以此为准）。

**③ 次级来源（`DIAG`）已实现**（`eval-df1-criteria.py`），三条硬约束逐条落实：
* 解析规则：只认**紧跟在 `(按码点回退)` 之前**的那一段的面（`HbFaceRef.ToString()` = `Path#FaceIndex`）。**我第一版抓错了段**（抓成主面 `NotoSans-Regular.ttf#0`），已修 —— 记录在案。
* ① 每条 DIAG 读数**逐条标注** `来源=内部装置（DIAG）`；② **C1/C2 永不使用** DIAG、不计入任何 PASS；③ 某条判据**只剩** DIAG ⇒ 该条报 **NOINFO**。
* 样例（修前形态，实跑）：
```
[fb] TOOTH-POS=NOINFO（行属性 OK：行宽应变大、CR_W 必须转正；**ADV 子判据只剩次级来源 ⇒ 按硬约束③ 报 NOINFO**）
[fb] DIAG-INFO 回退面=/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc#0 该面 U+4E0E 的 advance(独立复算)=16.0000 ｜ **来源=内部装置（DIAG）**，仅供牙参考，不计入 C1/C2，也不构成任何 PASS
[nofb] DIAG-INFO 未从 DIAG 解析到「按码点回退」的面 ⇒ 次级来源不可用
```
（`advance_from_font.py` 新增 `--cp=U+4E0E`：**自己走 cmap 取 gid** 再算 advance —— `NotoSans-Regular.ttf` ⇒ gid 0/`cp-not-covered`；`NotoSansCJK-Regular.ttc#0` ⇒ gid 9497/16.0000；`DroidSansFallbackFull.ttf` ⇒ gid 7078/16.0000。）

**④ `[fix]` 判定现在不依赖 API**：判别量优先用 `ADV`（公开 API），**API 是桩时退到行宽 `LINE_W`**（TextLine 本体属性）并**标出来源** ⇒ **T1d 落码后，`[fix]` 那一行会自己从"尚未生效"翻成"已生效"**：
```
（今天形态）[fix] MODE=null == nofb ⇒ 修法**尚未**生效（null=12.6560 fb=16.0000 nofb=12.6560；判别量来源=行宽 LINE_W（**次级来源：TextLine 本体属性**；ADV 因 API 未实现而缺））
（落码后）  [fix] MODE=null == fb  ⇒ **修法已生效**（null=16.0000 fb=16.0000 nofb=9.6000；判别量来源=公开 API 的 AdvanceWidths）
```

**⑤ 时序（照主控）**：**不现在构建**（T3 正在取 `#13` 读数，它的 `--modifier-check`/`tline` 与我的 runner 同源编 shim）⇒ **等"静树"**；落码顺序 **T1d 先落 shim 半**（`plan==null` 也构造计划 + 实现 `GetIndexedGlyphRuns()`）⇒ 然后我 `--build` + 跑 ⇒ **那时 `[null]` 读数才该从 `9.6000 / −3.0560` 变到 `16.0000 / +3.3440`**。
**34 行预期照旧**：两种结果都收；**不变** ⇒ 回退没被走到（例如 PC 层 `gate` 全判 `NoteUnrenderable`）⇒ **更深一层根因，回报主控，不自己往下改**。

---

## 31. `D-F1` runner v2：**复现 b34 真例** + 新 C1 三腿 + 两极牙实测（T2，2026-09-15 11:15–11:21）

**本轮定位**：`#14`（shim `17b2cdfe08f13280`）落地后，主控要我 ① 把"`cr.Width` 由 `−3.0560` 变正"这一步**实测到**、② 牙的常量按"**行宽** vs **折后宽**"分开、③ 把 C1 改成**字体无关、本地就能判红**的三腿并配**两极牙**、④ 字段来源写清（纪律 18）。四件**都做了**，读数见下；raw 全文留存见 §31.8。

### 31.1 v1 的两个 bug（我自己抓的；主控 2026-09-15 批准修）

| # | v1 的错 | 真因（逐条回源） | v2 的修法 |
| --- | --- | --- | --- |
| ① | `CR_IDX=-`：折叠区间**永远测不到** | v1 喂 **2 字合成段**，且 `alwaysCollapsible:false`；而 shim 的折叠资格闸门 = `if (!HasOverflowed && !_keepState) return this;`（`build/shims/PresentationCore.HbTextLine.cs:3142`），本 shim `HasOverflowed => false` **恒假**（`:3086`）⇒ **永不折叠** | 逐字复现 b34 语料 `F_nbsp_zwsp_w40`（32 码元、em16、段宽 40、`alwaysCollapsible:true`＝该用例 `cases.json` 的值），取 **`与 ` 那一行 `[14,2)`**；取行方式 = harness `:722` 的 `start += L.Length` 累加 |
| ② | `TOOTH-NEG` 拿**行宽**去比 `12.6560` ⇒ 必红 | `12.6560` 是**折后宽**（`COL_W`），不是行宽 | 牙逐项分开比：`LINE_W` / **`COL_W`** / `CR_W` / `CR 区间` / `ADV` 五项各自判定、各自印 |

### 31.2 读数（被判段 = b34 `F_nbsp_zwsp_w40` 行#3 `[14,2)` "与 "）

原始行（逐字，raw 原文；`SEL_TEXT` 里的 `.0020` = 空格被转义，`# SRC` 行紧随其后）：

```
MODE=null PARA=b34 RESULT=OK LINES=9 SEL=3 SEL_START=14 SEL_LEN=2 SEL_TEXT=与.0020 CP=U+4E0E LINE_W=16.0000 LINE_WITW=20.1600 LINE_NL=0 LINE_WS=1 HAS_OVERFLOWED=false COL_CONSTRAINT=8.0000 COL_W=12.6560 COL_LEN=2 COL_HAS=true CR_IDX=14 CR_LEN=2 CR_W=3.3440 INDEXED=OK RUNS=2 RUN_EM=16.0000 FACE_URI=file:///…/build/fonts/NotoSans-Regular.ttf GID=9498 ADV_DIP=16.0000 ADV_FROM_TYPEFACE=- TF_ADV_PRESENT=false TF_ADV_RAW=- GLYPH_COUNT=3884 GID_LT_COUNT=false PLAN_FACE=-
MODE=fb   PARA=b34 RESULT=OK …（与上一行逐字段相同，仅 PLAN_FACE=/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc#0）
MODE=nofb PARA=b34 RESULT=OK … LINE_W=9.6000 LINE_WITW=13.7600 … COL_CONSTRAINT=4.8000 COL_W=12.6560 COL_LEN=2 COL_HAS=true CR_IDX=14 CR_LEN=2 CR_W=-3.0560 INDEXED=OK RUNS=1 … GID=0 ADV_DIP=9.6000 ADV_FROM_TYPEFACE=9.6000 TF_ADV_PRESENT=true TF_ADV_RAW=0.600000 GLYPH_COUNT=3884 GID_LT_COUNT=true
```

| 量 | `null`（被判对象） | `nofb`（修前形态） | b34 真值（`F_nbsp_zwsp_w40#3`） | 判读 |
| --- | --- | --- | --- | --- |
| 行宽 `LINE_W` | **16.0000** | **9.6000** | 16.0000 | 修法生效（1 em） |
| 行宽含尾空 `WITW` | **20.1600** | 13.7600 | 20.1600 | 与真值逐位相同 |
| **折后宽** `COL_W` | **12.6560** | **12.6560** | 12.6567 | 两侧都 = 省略号面宽（与 D-F1 无关，见 §31.6） |
| **折叠区间** `CR` | **[14,2) W=+3.3440** | **[14,2) W=−3.0560** | `[14,2) W=3.3433` | **符号由负转正**、区间逐位一致 |
| `GID` | 9498 | 0 | 3883（Yu Gothic，**字体不同不可逐位比**） | 回退真的发生 |
| `ADV_DIP` | 16.0000 | 9.6000 | 24 @em24 = 1 em | 1 em |

⇒ **`cr.Width` 由 `−3.0560` 变正这一步，本轮实测到了**：`null` 与 `fb` 同为 `+3.3440`，`nofb` 仍为 `−3.0560`（= harness 产物 `build/MilBridge/gen/tline-detail-full.txt` 的逐字行 `F_nbsp_zwsp_w40|行#3|…|我们 Len=2 W=12.6560|真值 Len=2 W=12.6567|cr 我们=[14,2) W=-3.0560|cr 真值=[14,2) W=3.3433`，该文件头 sha `272833acb4fe03fe`、内含被测 shim `FDE9E511…`）。**`D-T1`/`D-F1` 的 CR 极由此判完**：折后宽 12.6560 本来就对，错的是"被折叠的那一行的原宽"（9.6 → 16.0），所以 `cr.Width = 原宽 − 折后宽` 的符号跟着翻正。

> **`PRE.line_w = 9.6000` 的出处（免得"牙的常量"变成没根据的数）**：harness 那条 artifact 行只印**折后宽**（`W=12.6560`）与 `cr` 两侧，不印原宽；9.6000 由 shim 自己的恒等式反解 —— `cr.Width = 原宽 − 折后宽`（`build/shims/PresentationCore.HbTextLine.cs:3232`）⇒ `原宽 = 12.6560 + (−3.0560) = 9.6000`，且本轮 `nofb` 同趟独立给出 `ADV_DIP=9.6000`、`TF_ADV_RAW=0.600000`（= `NotoSans-Regular.ttf` 的 `.notdef` advance 0.6 em，`hmtx` 独立复算亦为 9.6000）⇒ **两个来源互证**。

### 31.3 新 C1（主控 ④：字体无关、本地就能判红）+ 两极牙

三条腿（**只用公开 API + 字体文件**，**不需要真机真值**）：

| 腿 | 内容 | 依据 | `null/b34`（今天） | `null/b34line0`（健康正控） |
| --- | --- | --- | --- | --- |
| ① | `gid < 报出面的字形数` | `GlyphTypeface.GlyphCount`（公开 API）+ 独立 `maxp.numGlyphs` | **红**（9498 ≥ 3884） | 绿（81 < 3884） |
| ② | `gid≠0 ⇒ 报出的 FontUri 那份面**必须覆盖**该码点`（`gid==0 ⇒ 必须不覆盖`，.notdef 才诚实）；`.ttc` 报出 URI 必须带 `#n`（真机形态 `…/YUGOTHM.TTC#1`） | 独立读字体文件 cmap | **红**（NotoSans 无 U+4E0E） | 绿（NotoSans 有 U+006E） |
| ③ | `GlyphTypeface.AdvanceWidths[gid]` 可查得，且 `× FontRenderingEmSize == 观测 AdvanceWidths[0]` | 公开 API 对内一致 | **红**（`AdvanceWidths` 里没有 9498 ⇒ `TF_ADV_PRESENT=false`） | 绿（81 ⇒ 0.618×16 = 9.8880 = 观测值） |

判据层输出（raw 原文）：

```
[null/b34] C1=FAIL（新 C1 三腿：①gid<GlyphCount(公开API)=红；①gid<numGlyphs(独立/字体文件)=红；②gid≠0⇒报出面须覆盖CP(独立cmap)=红；③AdvanceWidths[gid]可查=红；③AdvanceWidths[gid]×em==观测advance=NOINFO）
[null/b34] C2=PASS（glyph=9498 ≠0 ⇒ 回退真的发生了）
[null/b34] C3=PASS（行宽 16.0000 vs 16.0：差 0.0000；折后宽 12.6560 vs 12.6567：差 0.0007；CR_W 3.3440 vs 3.3433：差 0.0007（符号正）；CR 区间 [14,2) vs [14,2)：一致；字体与真值**不同**（YUGOTHM.TTC#1）⇒ 只作量级/符号判据）
[nofb/b34] TOOTH-NEG=PASS（必须逐项复现修前：行宽 9.6000/9.6 ✓；**折后宽** 12.6560/12.656 ✓；CR_W -3.0560/-3.056 ✓；ADV 9.6000/9.6 ✓；CR 区间 [14,2) vs [14,2) ✓）
[fb/b34]   TOOTH-POS=PASS（行宽 16.0000≈16.0 ✓；CR_W=3.3440>0 ✓；ADV 16.0000≈16.0 ✓）
[null/b34] TOOTH-C1-NEG=PASS（负牙：**今天这版必须判红**（gid 9498 挂在不覆盖该码点的面上）；实得 C1=FAIL）
[null/b34line0] TOOTH-C1-POS=PASS（正牙：**同一 plan==null 路径**下的健康输入（全拉丁行，段落字体自己覆盖）必须判绿 ⇒ 排除『恒红』；实得 C1=PASS）
[fix] MODE=null == fb ⇒ **修法已生效**（plan==null 路径真的回退了）（行宽 null=16.0000 fb=16.0000 nofb=9.6000；CR_W null=3.3440 fb=3.3440 nofb=-3.0560；ADV null=16.0000 fb=16.0000 nofb=9.6000）
CRITERIA=FAIL（fail=1 noinfo=0；被判对象=null/b34；观测面=GetIndexedGlyphRuns()；真值=16.0/12.6567/3.3433←YUGOTHM.TTC#1 glyph3883）
```

⇒ **唯一的红 = 新 C1（`D-F1b`）**；`D-F1` 本体（回退发生 + 度量 + 折叠符号）已是绿的。**正牙**用"同一 `plan==null` 路径 + 全拉丁行"证明该判据**不是恒红**（`GID=81`、`AdvanceWidths[81]=0.618`、`0.618×16=9.8880=观测值`）。

**另一个必须让 T1d 看到的实测**：`[fb/b34] C1=FAIL` —— **`fb`（有计划的那条路）也报 `NotoSans-Regular.ttf`**（计划面明明是 `NotoSansCJK-Regular.ttc#0`）⇒ `D-F1b` 的根因在**两条路共用的 `_glyphRuns` 构造**里，**不是** `plan==null` 分支独有 ⇒ 只修 `plan==null` 那一支**不足以**转绿。修后还有一条腿会自动"上线"：报出 URI 将指向 `.ttc`，按真机形态（`YUGOTHM.TTC#1`）**必须带 `#n` 面号**（②的附加腿）。

### 31.4 字段来源（纪律 18）：读数报的是哪一侧，逐字段写死

runner 每个读数行后跟一行 `# SRC …`（raw 里逐行可查，`Program.cs` 头部注释亦同）：

| 字段 | 取自 | 性质 |
| --- | --- | --- |
| `LINE_W` / `LINE_WITW` | `TextLine.Width` / `.WidthIncludingTrailingWhitespace` | 公开 API；**值由本 shim 计算** |
| `COL_W` / `CR_*` | `TextLine.Collapse(props)` / `.GetTextCollapsedRanges()[0]` | 公开 API；值由本 shim 计算 |
| `ADV_DIP` | `IndexedGlyphRun.GlyphRun.AdvanceWidths[0]` | 公开 API；**值由本 shim 的整形路径写入** |
| `ADV_FROM_TYPEFACE` | `GlyphTypeface.AdvanceWidths[gid] × GlyphRun.FontRenderingEmSize` | 公开 API；字体 em 归一 advance |
| `GLYPH_COUNT` | `GlyphTypeface.GlyphCount` | 公开 API |
| `FACE_URI` / `GID` | `GlyphRun.GlyphTypeface.FontUri` / `GlyphRun.GlyphIndices[0]` | 公开 API |
| `PLAN_FACE` | **本 runner 自己喂进去的计划**里覆盖该下标的那一段的面 | **我们的输入**，不是 API 的说法 |
| 独立量 | `advance_from_font.py`：自读 `cmap`/`hmtx`/`maxp` | **外部独立**（不信任何 API 自述） |

**据此修正上一轮 `[fix]` 行的口径**（原句"判别量来源=公开 API 的 AdvanceWidths"过于含糊）：`ADV_DIP` **是**公开 API 成员 `GlyphRun.AdvanceWidths[0]`，但它是**我们这一侧经公开 API 面报出来的量**（写入者仍是本 shim）；`LINE_W`/`CR_W` 同理。**"公开 API"只说明观测面在哪，不等于外部独立**。真正独立的一侧只有 `advance_from_font.py`。`[fix]` 行现在把三个量各自的 API 出处逐条印出。

### 31.5 独立复算：三份"嘴"的数字（全部本轮实跑）

```
# NotoSans-Regular.ttf（**报出的那个面**）
# face=…/build/fonts/NotoSans-Regular.ttf FACE_INDEX=0 NUMGLYPHS=3884 UPEM=1000
# CMAP cp=U+4E0E gid=0 HAS=false
ADV=NA reason=cp-not-covered（该面没有这个码点 ⇒ .notdef）
# 回退面（`fb` 计划面）/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc face#0
# face=…NotoSansCJK-Regular.ttc FACE_INDEX=0 NUMGLYPHS=65535 UPEM=1000
# CMAP cp=U+4E0E gid=9497 HAS=true
ADV=16.0000 UPEM=1000 ADV_UNITS=1000 FACE_INDEX=0
（同面 gid=9498：ADV=16.0000 ADV_UNITS=1000）
```

* **两嘴一致的实例**：`GlyphCount`（公开 API）= **3884** == 独立 `maxp.numGlyphs` = **3884**（`NotoSans-Regular.ttf`）⇒ 腿①的两个来源**互证**。
* **`9497` vs shim 报的 `9498`——我把它测清楚了，不许推断**：该 `.ttc` **10 个面**对 U+4E0E 的名义 cmap 给 `9497`（面 0/1/5/6）、`9498`（面 2/3/7/8）、`9499`（面 4/9）；而**本 shim 自己的 `HbShaper`**（`hb_shape` P/Invoke，**内部装置**）实测：

```
# PROBE-SHAPE face=…NotoSansCJK-Regular.ttc#0 lang=zh-cn cp=U+4E0E gids=9498 adv=16.0000 upem=1000
# PROBE-SHAPE face=…NotoSansCJK-Regular.ttc#0 lang=en-us cp=U+4E0E gids=9497 adv=16.0000 upem=1000
# PROBE-SHAPE face=…NotoSansCJK-Regular.ttc#2 lang=zh-cn cp=U+4E0E gids=9498 adv=16.0000 upem=1000
# PROBE-SHAPE face=…NotoSansCJK-Regular.ttc#2 lang=en-us cp=U+4E0E gids=9498 adv=16.0000 upem=1000
```

⇒ 差 1 的**全部**来源是 `zh-cn` 语言下的 GSUB `locl` 替换（面 0 的 JP 名义字形 9497 → 区域字形 9498）——**不是**面错、**不是**字形错。**据此定死 C1② 的口径**：只用**覆盖**（has/hasn't），**绝不**比 `gid` 等值（名义 cmap ≠ 整形结果）；并记一笔"`AdvanceWidths` 报的 16.0000 == 回退面 gid 9498 的 `hmtx` 独立复算 16.0000" ⇒ **度量与字形都对，错的只有"面身份"**，与主控 `D-F1b` 的定性逐字吻合。
* **折后宽 12.6560 的出处**（顺带纠正一条可能的误读）：`COL_W` = **省略号 U+2026 在段落面里的宽**（shim `:3182` `HbShaper.Shape(_fontPath, Ellipsis, …)`）⇒ 与 D-F1 无关；真机同字形测得 `12.6567`（差 `0.0007`，`F_lat_words_w120` 的真值行里也是这个数）⇒ 两侧一致，不是新账。

### 31.6 判据层自验（纪律 27 / `L25`）：**一条命令跑出三态**

判据层自带 `--selftest`（合成日志，**只验判据层**，每趟真读数前先跑；本轮 `SELFTEST=PASS 3/3`）：

```
[SELFTEST] 真空：只喂 MODE=none 兜底行 ⇒ 实得 rc=3｜CRITERIA=NOINFO reason=no-runner-output ⇒ OK
[SELFTEST] 被判对象红：D-F1b 形态（gid 9498 挂在 NotoSans 上）⇒ 实得 rc=1｜CRITERIA=FAIL ⇒ OK
[SELFTEST] 修后形态：被判对象也自洽 ⇒ 实得 rc=0｜CRITERIA=PASS ⇒ OK
SELFTEST=PASS（3/3）
```

外加真读数路径上的**四道防空过闸**（缺一即 `NOINFO`，**不报绿**）：① 一条读数都没有；② 三模式缺一；③ **被判对象缺**；④ **健康正控缺**（"红"与"恒红"不可分 ⇒ 不许报绿）。第三个自验用例显式 `DF1_TEETH=off` 并把"牙未判"**印在输出里**（牙是针对今天真实读数的断言，合成日志里它们按设计必红）——**绝不静默关牙**。

### 31.7 口径四元组 + 留存（本轮唯一产物）

* 件：被测 shim `17b2cdfe08f13280`（= `#14` 冻结件）、PC `9adac6b8d8e285c3`
* 仪器（**运行那一刻**）：runner 脚本 `d1db09483b16b16a`、`Program.cs` `263a89294052abfa`、判据 `eval-df1-criteria.py` `9ec31d3fca9e2c2f`、`advance_from_font.py` `9e7b353c8aa9cfba`、产物 DLL `c3e5d553c84666c5`（`mtime 11:20:54`，脚本自检"DLL 不旧于源文件"✓）
* 口径：判据 sha 同上（脚本把"运行那一刻"的值同时印在四元组与判据行）；参考 `build/MilBridge/run.sh` `3e513e88a4fa4ec9`（只读，未参与本趟）
* 环境：`uptime` 11:20:51（up 13:27）、`loadavg` **2.30/2.34/2.24**；跑前 `pgrep` 只有 T3 的 `run.sh tline`（写 `build/MilBridge/gen/**`，**不是我的车道**）
* 留存（**`/tmp` 会被清，故同份落 `$HOME`**）：`$HOME/wfp-runs/df1c-20260915-112051/raw.txt`（sha16 `961d922aa91b70cf`）+ `criteria.txt`（sha16 `fd395861d5396720`）+ `/tmp/df1-criteria-raw-20260915-112051.txt`

### 31.8 未判定 / 未做（不留红树的部分照实说）

1. **34 行 `+CJK` 集合/计数对比：仍未做** —— 该artifact 由 T3 的 `#14` `tline` 趟产出，本轮跑时 `build/MilBridge/gen/tline-detail-full.txt` 仍是旧件（头 sha `272833acb4fe03fe`、内含 shim `FDE9E511…`；T3 的 `dotnet MilBridge.HbTextLineParity.dll` 11:21 仍在跑，CPU 101%、12:30 CPU 时间）。**我只做集合/计数对比、不碰 harness**；拿到新件即做。
2. **`CRITERIA=FAIL`（exit 1）的红只有一条 = 新 C1 = `D-F1b`**（主控已派 T1d）；`D-F1` 本体本轮**没有**红。**若下一轮 T1d 落码后 C1 仍红**，先看 `[fb/b34] C1` 那一行（它现在也红 ⇒ 说明改的是共用路径才行）。
3. **健康正控行（b34 行#0）的行宽 40.1760 vs 真值 39.8533（差 0.3227）** 落在 harness 既有 `≤0.34 DIP` 宽度桶内 ⇒ **我不另立新账**（该正控只用于验 C1 三腿，判据不含宽度）。
4. **C1 的 `.ttc#n` 面号腿本机不适用**（今天报出的 URI 不是 `.ttc`）⇒ 该腿只在"报出面变成 `.ttc`"后才会被判；这是**设计如此**，不是漏判（本机用例的面全是 `.ttf`）。

---

## 32. `D-F1c` 内存线：从"3.3 GB 卡死"到"**同一个 `.ttc` 被映射 12 段**"（T2，2026-09-15 11:35–12:35；六窗）

> **为什么单独成节**：这条线的**原始件全在 `$HOME/wfp-runs/**`**（成文件 `window{3,4,5,6,6b}-*.md`），而 `$HOME` **被整盘清过一次** ⇒ 结论与判据必须回仓。装置已落 `build/DirectWrite.Linux/FallbackCriteria/mem-sampler.sh`（sha16 `09e5bab7c3d246f6`，`--selfcheck` 一条命令）。

### 32.1 六个窗口的事实（每窗都写当刻 shim sha；`--mode=null --para=b34`、`WPF_LINUX_FONT_DIR` 逐档记）

| 窗口 | shim | 权威峰值（`/usr/bin/time -v`） | `shared_clean`@峰值 | 关键计数 |
| --- | --- | --- | --- | --- |
| W2（修前对照 `17b2cdfe`） | `17b2cdfe08f13280` | **3,459,876 KB ≈ 3.30 GB** | — | `coverageProbe=60` |
| W3 | `92fc7605480fb289` | 2,119,824 KB | 1,748,632 KB（88%） | `faceLoads=393 releaseCalls=371 residentCount=22` |
| W4 | `14f6a728bc834c0e` | 2,119,788 KB（纯计数件 ⇒ Δ36 KB 复现 ✓） | 1,748,720 KB（87%） | 同上 + `segmentFaceResolveCalls=3` |
| W5 | `6c3afa3046073786` | 2,099,304 KB | 1,752,776 KB | **`faceLoads 393→22`**、**`releaseCalls 371→0`** |
| W6 | `ac4104d67687c2c9` | 2,119,948 KB | 1,753,220 KB | **`liveBlobsPeak=1`**、`[LIVEBLOBS]` stderr **0 行** |
| W6b（`maps`/`smaps` 快照） | `ac4104d67687c2c9` | 1CJK 档 409,772 KB | 1CJK 档 `shared_clean=198.9 MB` | **`NotoSansCJK-Regular.ttc`（18.6 MB）被映射 12 段、Σ虚拟 223.0 MB（=12.0× 整文件）、字体 ΣRss 161.2 MB（占 `shared_clean` 的 81%）** |

### 32.2 三条被"钉死"的结论（都可复算）

1. **判别量**：`Shared_Clean` 占 83–88% ⇒ **"映射仍驻留"**，不是 native 堆未归还（`Private_Dirty` 仅 10–15%）。
2. **不是 HarfBuzz 的 blob**：`liveBlobsPeak=1`、`[LIVEBLOBS]` 零行、`hb_blob_create_from_file` 直连只剩包装器内部一处（`HbTextLine.cs:142`）⇒ 映射来自**别的门**。
3. **段数即靶子**：一个 10 面的 `.ttc` 被映射 **12 段**（10 面 + 2）；`Σ(文件大小×面数) = 1,902 MB ≈ 1.86 GB` vs 实测 `shared_clean 1.67 GB` = **90%** ⇒ 可直接用"**段数 12 → 1–2**"做判据。

### 32.3 我在这条线上**自己抓到的两条假读数**（记账）

* **"峰值 63–65 MB ⇒ 内存达成"作废**：那是 `16db2d61` **卡死未跑完**时的读数（进程卡在第一次探测 ⇒ 没机会分配那 3.3 GB）。`1ebea99c` 修好死循环后同命令 **3.30 GB** ⇒ 旧结论撤回。
* **"`TOOTH-C1-NEG` 红"不是产品缺陷**：那是**针对 `D-F1b` 形态**的牙，缺陷修好后它必然变假 ⇒ 已换口径为 `TOOTH-D-F1b-ABSENT`（现场断言历史形态不再出现）+ `TOOTH-C1-REDCAP`（"能红"由判据层合成用例每趟保证）；判据 sha `524936544a6b9033 → fc808896f23390f4`。

### 32.4 口径更正两条（引用时请带限定）

* **`WPF_LINUX_FONT_DIR`（单数）**才是回退扫描的旋钮（`HbTextLine.cs:889`；PC 侧第二读者 `PresentationCore.Factory.Linux.cs:384`）；**复数 `WPF_LINUX_FONTS_DIR` 确实存在**（`build/PresentationCore.Linux/FontCacheUtil.Linux.cs:334`，管 FontCache 平台目录）⇒ 我曾报的"复数 0 命中"是**假 0**（当时只 grep 了 4 个路径、无正对照）。
* T1d 的"**渲染侧不是主项**"结论**只对裸 FreeType 成立**（`FT_New_Face` 10 面 +8 MB 全回收）；**生产路径是 Skia 的 `SKTypeface.FromFile`（经 `SkData` mmap 整份文件）** ⇒ 两条不是同一条路，**渲染侧仍是头号嫌疑**。

### 32.5 判据（下一窗复取用）

* **内存·主判据（主控 2026-09-15 ④ 定）**：该 `.ttc` 的**映射段数 12 → 1–2**，以及**这些段的 Σ虚拟**（今天 12 段 / 223.0 MB ≈ 12.0× 整文件）。
  **为什么 RSS/`Shared_Clean` 只能作旁证**：**mmap 之后未被触碰的页不计 RSS**，而它们在虚拟地址空间里都在 ⇒ 用 RSS 判据**看不到**那 12 份（T1d 早先两次实测就是这样漏掉的）。
* **内存·旁证**：字体 ΣRss 161 MB → ~19–37 MB；`shared_clean` → **~30 MB 量级**；`fontdir-1` 档权威峰值 **≤300 MB**（今天 415 MB）；硬线 1 GB。
* **不许变**：`candidates`（10/45/371）、`Scans=1`、**面选择普查 26/26 逐格相同**（与修前 `17b2cdfe` 三向一致）、`LINE_W=16.0000`/`CR_W=3.3440`/`GID=9498`/`ADV=16.0000`、`CRITERIA=PASS`。
* **允许变（已声明仪器位移）**：`faceLoads`、`releaseCalls`、`coverageProbe`/`coverageCacheHit`、以及新增计数。

### 32.6 长趟 `run.sh tline` 为何**不放**（主控裁定，我提的四条理由全被采纳）

单次读数固定底噪 ~2 GB（与用例数无关）⇒ 2/3 被吃掉；harness 还要持 614 例语料 ⇒ 抬守卫也多半只是"晚一点"被止损；本机 **swap 仅余 ~1.2 GB**（`803/2047` 已用）⇒ 越界即"零输出假死"= **证据质量更差**。⇒ 先修映射；要长趟证据就跑**有界切片**并标注"切片，不是全趟"。

### 32.7 未做（照实说）

* **`build/DirectWrite.Linux/REPORT.md` 之外的原始件仍在 `$HOME/wfp-runs/**`**（`window{3,4,5,6,6b}-*.md` + 各窗 `meta.txt/sample.txt/smaps-at-peak-*.txt`）⇒ **建议随 §32 一并归档进仓**（我按主控指示待命，不擅自扩大写域）。
* **app-local 备稿**：检查器 NEXT 自检 **A–N 全过**、`REPO`/`REAL_REPO` 已拆（E 支验证通过）；**O 支（"删件必须红"）+ 三极演示 + `DRAFT.md` + `SHA256SUMS` 仍未完** ⇒ 正在收尾。
* **34 行 `+CJK` 集合/计数对比**：仍等能跑完的 `tline` 产物（本轮长趟按裁定不放）。
* **`APPSYNC` 那条既存红：已由主控关闭（12:33）** —— `dotnet build -c Release samples/HelloWpf/HelloWpf.csproj`（0 错 0 警）后被点名的 `Provider.dll` 出现但 sha `6134073cdbba990c` ≠ 权威 `71ba86c6495347fe`（`MISSING=0` → `DIVERGENT=1`）⇒ 把权威 Debug 副本同步进该 refdir 后校验器给 **`OK=45 MISMATCH=0 MISSING=0 UNEXPECTED=0 DIVERGENT=0` ⇒ `APPSYNC=PASS`**。
  ⇒ **本报告更早各节**（§25–§28 一带）里把 `MISSING samples/HelloWpf/…/DirectWrite.Linux.Provider.dll` 登记为"**真缺口**"的表述**以本条为准：已关闭**。