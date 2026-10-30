# T2 · Phase 2 —— 接线清单（**已执行**，保留为可复算的记录）

> **状态：已执行。** 主控于 2026-09-10 授权 T2 直接改 `build/DirectWriteForwarder.Linux/`
> （该目录的并发使用者已撤出），因此本文档从"方案"变成了"已执行清单"：
> 下面每一条的"替换为"都已经落到代码里，行号是**改动前**的行号，便于逐条核对。
>
> | 项 | 结果 |
> |---|---|
> | 目标文件 | `build/DirectWriteForwarder.Linux/ManagedSurface.cs`（1015 → 1326 行） |
> | 新增文件 | `build/DirectWriteForwarder.Linux/ProviderAdapters.cs`（适配层） |
> | 工程改动 | `DirectWriteForwarder.Linux.csproj`：+1 个 `<Compile>`、+1 个 `<ProjectReference>` |
> | 跨目录改动 | `build/PresentationCore.Linux/reapply-patches.py` 新增**补丁 F**（Provider 引用，幂等可重放） |
> | 验收 | DWF **0 错 0 警**；PresentationCore **0 错 0 警**；T2 断言 **84/84 通过** |
>
> **第二轮更新（主控追加授权）：§0.2 的 `#0-a`/`#0-b` 与 §4 的令牌桥都已落地** ——
> `#0-a`/`#0-b` 用**编译期替换**（`build/shims/PresentationCore.Factory.Linux.cs`，补丁 G）整体消除；
> 令牌桥用 `build/shims/PresentationCore.FontBridge.cs` 的 `[ModuleInitializer]` 安装（路径式钩子）。
> 实测细节与"跨运行时未通"的边界见 REPORT.md §4.0.2。
>
> **仍然留给主控的**：
> ① `.so` 侧需要导出 `MilFontFace_RegisterFromFile`（跨运行时令牌桥的另一半，需改 `src/WpfGfx.Linux/Interop/*` + 重建 MilBridge）；
> ② WindowsBase 运行期装配（框架 4.0.0.0 vs 本仓 4.0.0.1 的身份冲突，REPORT.md §4.0.4 有证据与修法）。
> 其余各节按"已执行"阅读。

---

## §0 顺序与前置依赖（先读这一节，否则接线也出不了字）

接线的**顺序**建议如下。前三步做完，**简单文本路径**就能出字（依据见 §0.1）：

| 步骤 | 内容 | 归属 | 阻塞出字？ |
|---|---|---|---|
| 0 | 解除 `Factory` 的原生闸门（`#0-a` / `#0-b`） | PresentationCore 一方 | **是** |
| 1 | §1 工程引用 + §2 适配器文件 | 主控（DWF 目录） | 是 |
| 2 | §3 的 A/B/C 档成员替换（47 条） | 主控（DWF 目录） | 是 |
| 3 | §4 令牌桥接（1 行） | PresentationCore `ModuleInitializer` | 是（否则画不出轮廓） |
| 4 | §5 的 `LineSpacing` 修正（1 行） | 主控（DWF 目录） | **是**（行高全错） |
| 5 | §6 `LocalizedErrorMsgs` 常量（2 行） | 主控（DWF 目录） | 否（潜在陷阱） |
| 6 | D 档 15 条保持 PNSE + 消息改写 | 主控（DWF 目录） | 否（复杂脚本才需要） |

### §0.1 为什么"简单文本"不需要 TextAnalyzer shaping

对 PresentationCore 的全量调用点普查得到两条不同的排版路径：

```
复杂路径（FullTextLine + LineServices）
  TextFormatterImp.cs:197 → TextMetrics.FullTextLine → TextStore.cs:1266
    → TextCharacters.cs:172 GetTextShapeableSymbols
    → GlyphingCache.cs:39 → TypefaceMap.cs:68 → TypefaceMap.cs:110  TextAnalyzer.Itemize   ← D 档
    → LineServicesCallbacks.cs:1618 TextAnalyzer.GetGlyphs            ← D 档
    → LineServicesCallbacks.cs:1703 TextAnalyzer.GetGlyphPlacements   ← D 档

简单路径（SimpleTextLine，单 run / 无复杂脚本 / 无 TypographyProperties）
  TextFormatterImp.cs:230 SimpleTextLine.Create
    → TextCharacters.cs:236 new ItemProps()      ← ItemProps 在骨架里本来就不是 PNSE
    → SimpleTextLine.cs:935/1780
    → GlyphTypeface.cs:1370 ComputeUnshapedGlyphRun
    → GlyphTypeface.cs:1310 GetAdvanceWidthsUnshaped
    → FontFaceLayoutInfo.IntMap → FontFace.GetArrayOfGlyphIndices   ← A 档，本轮已实现
```

也就是说：**A/B/C 档接上之后，"Hello WPF" 这类单 run 文本可以不走 shaping**。
`TextAnalyzer.*`（D 档）是复杂脚本 / 双向文本 / 排版特性（kerning 以外的 GPOS）才需要的。

### §0.2 `#0-a` / `#0-b`：不在 62 条 PNSE 内，但会先炸

```csharp
// PresentationCore/MS/internal/Text/TextInterface/Factory.cs:105
delegate* unmanaged<int, void*, void*, int> pfnDWriteCreateFactory = DWriteLoader.GetDWriteCreateFactoryFunctionPointer();
```
`DWriteLoader.LoadDWrite()`（`ModuleInitializer.cs:25`）在 Linux 上会抛
`DllNotFoundException("dwrite.dll")`；即便把它改成 no-op，返回的函数指针也是 **null** —— 调用即崩。
另外 6 处 `_factory.Value->…`（Factory.cs:73,84,214,281,312,328）是对镜像指针的原生 vtable 调用，
Linux 上必然段错误。其中 `CreateFontFace`(214) 与 `GetSystemFontCollection`(281) 正在文本路径上。

**这两项必须由 PresentationCore 一方改造才能出字**（把 `Factory` 的这几处改成委托给 provider）。
本文把它标为 `#0`，因为它比 62 条 PNSE 更早触发。改造方案建议：
`Factory.GetSystemFontCollection()` / `GetFontCollection(Uri)` 返回由
`LinuxFontCollection.FromDirectory(...)` 构造的托管集合（字体目录由宿主配置，
例如环境变量 `WPF_LINUX_FONT_DIR` 或 `PresentationCore` 的字体目录常量），
`Factory.CreateFontFace(...)` 改为 `LinuxFontFace.FromFile(path, faceIndex, sim)`。

---

## §1 工程接线（csproj 2 处 + 1 个新文件）

### 1.1 引用 provider 工程

文件：`build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj`

在现有 `<ItemGroup>`（含 WindowsBase `<Reference>` 的那一组）**之前**插入：

```xml
  <ItemGroup>
    <!--
      T2 · Phase 2：接入 build/DirectWrite.Linux/Provider 的字体实现。
      方向是 DWF → Provider（单向）：Provider 只依赖 SkiaSharp 2.88.9，
      不引用 WpfGfx.Linux / PresentationCore / DWF，因此不会成环。
      注意：**不要**引用 Probe 或 Tests —— 那两个是取证工程，不属于产品依赖。
    -->
    <ProjectReference Include="$(WpfLinuxRoot)build/DirectWrite.Linux/Provider/DirectWrite.Linux.Provider.csproj">
      <Private>true</Private>
    </ProjectReference>
  </ItemGroup>
```

> 为什么用 `ProjectReference` 而不是 `<Reference><HintPath>`：Provider 带
> SkiaSharp / SkiaSharp.NativeAssets.Linux 两个包依赖，ProjectReference 会把它们
> 传递到 DWF 的输出目录（`SkiaSharp.dll` + `libSkiaSharp.so`），HintPath 不会。
> DWF 现在是 `AppendTargetFrameworkToOutputPath=false`、`bin/Debug/` 输出，
> 传递依赖同样会落到那里。

### 1.2 把适配器文件编进来

同一个 csproj，在现有 `<ItemGroup>` 的 `<Compile Include="…ManagedSurface.cs" />` **之后**追加一行：

```xml
    <Compile Include="$(MSBuildThisFileDirectory)ProviderAdapters.cs" />
```

### 1.3 `NoWarn` 追加（可选但建议）

Provider 未强签名，DWF 是公开签名程序集 → 1 条 `CS8002`。
现有 `NoWarn` 里**已经有** `CS8002`（见 csproj 第 47 行附近），所以**无需改动**；
若将来换了签名策略，这一条会重新出现，届时按需处理。

---

## §2 新增文件：`build/DirectWriteForwarder.Linux/ProviderAdapters.cs`

> 全文可直接粘贴。它只做三件事：**类型映射**（provider 的 `*Data` → 骨架的 `FontMetrics`/`GlyphMetrics`）、
> **句柄桥接**、以及**枚举值映射**（provider 用 int，骨架用枚举）。
> 把映射集中在一个文件里，是为了让 `ManagedSurface.cs` 的改动只剩"一行一行的委托"。

```csharp
// T2 · Phase 2 —— provider（MS.Internal.Text.TextInterface.Linux）↔ 骨架（本程序集）的适配层
// =====================================================================================
// 【为什么需要它】
//   骨架的成员签名是上游 C++/CLI 的形态（FontMetrics 类、GlyphMetrics 显式布局结构体、
//   FontWeight/FontStretch/FontStyle 枚举），provider 那边是等价但独立的托管类型
//   （FontMetricsData/GlyphMetricsData + int）。两边刻意不共享类型：
//     · provider 只依赖 SkiaSharp，不认识 WindowsBase 的 TextFormattingMode，也不认识骨架的枚举；
//     · 骨架要保持上游形态（PresentationCore 按名字与布局引用它）。
//   于是映射必须显式写出来 —— 这也正好是"哪几个字段对哪几个字段"的可审计清单。
//
// 【改动的边界】
//   本文件不改 ManagedSurface.cs 的任何语义，只提供 Convert/Resolve 两类纯函数。

using System;
using System.Collections.Generic;
using System.Globalization;
using MS.Internal.Text.TextInterface.Linux;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface
{
    /// <summary>provider 数据 → 骨架类型的映射。</summary>
    internal static class ProviderAdapters
    {
        // ---------------------------------------------------------------------------------
        //  度量
        // ---------------------------------------------------------------------------------

        /// <summary>FontMetricsData → 骨架的 FontMetrics（逐字段同名同型，10 个字段一个不少）。</summary>
        internal static FontMetrics ToFontMetrics(FontMetricsData source)
        {
            if (source == null) return null;

            return new FontMetrics
            {
                DesignUnitsPerEm = source.DesignUnitsPerEm,
                Ascent = source.Ascent,
                Descent = source.Descent,
                LineGap = source.LineGap,
                CapHeight = source.CapHeight,
                XHeight = source.XHeight,
                UnderlinePosition = source.UnderlinePosition,
                UnderlineThickness = source.UnderlineThickness,
                StrikethroughPosition = source.StrikethroughPosition,
                StrikethroughThickness = source.StrikethroughThickness,
            };
        }

        /// <summary>GlyphMetricsData → 骨架的 GlyphMetrics（[StructLayout(Explicit, Size=28)] 的 7 个字段）。</summary>
        internal static GlyphMetrics ToGlyphMetrics(in GlyphMetricsData source)
        {
            var result = new GlyphMetrics();
            result.LeftSideBearing = source.LeftSideBearing;
            result.AdvanceWidth = source.AdvanceWidth;
            result.RightSideBearing = source.RightSideBearing;
            result.TopSideBearing = source.TopSideBearing;
            result.AdvanceHeight = source.AdvanceHeight;
            result.BottomSideBearing = source.BottomSideBearing;
            result.VerticalOriginY = source.VerticalOriginY;
            return result;
        }

        /// <summary>
        /// 批量映射。调用方给的是"非托管指针"（PresentationCore 传进来的 pGlyphMetrics），
        /// 这里用 Span 包一层直接写进去，避免多一次分配。
        /// </summary>
        internal static unsafe void ToGlyphMetricsArray(
            GlyphMetricsData[] source, int count, GlyphMetrics* destination)
        {
            for (int i = 0; i < count; i++)
            {
                GlyphMetrics value = ToGlyphMetrics(source[i]);
                destination[i] = value;
            }
        }

        /// <summary>LocalizedStringsData → 骨架的 LocalizedStrings（两边都是只读 IDictionary）。</summary>
        internal static LocalizedStrings ToLocalizedStrings(LocalizedStringsData source)
        {
            if (source == null) return null;

            CultureInfo[] keys = source.KeysArray;
            string[] values = source.ValuesArray;
            return new LocalizedStrings(keys, values);
        }

        // ---------------------------------------------------------------------------------
        //  枚举（provider 用 int，骨架用上游枚举；字面值逐项对齐）
        // ---------------------------------------------------------------------------------

        internal static FontFaceType ToFontFaceType(FontFaceKind kind) => kind switch
        {
            FontFaceKind.CFF => FontFaceType.CFF,
            FontFaceKind.TrueType => FontFaceType.TrueType,
            FontFaceKind.TrueTypeCollection => FontFaceType.TrueTypeCollection,
            FontFaceKind.Type1 => FontFaceType.Type1,
            FontFaceKind.Vector => FontFaceType.Vector,
            FontFaceKind.Bitmap => FontFaceType.Bitmap,
            _ => FontFaceType.Unknown,
        };

        internal static FontFileType ToFontFileType(FontFileKind kind) => kind switch
        {
            FontFileKind.CFF => FontFileType.CFF,
            FontFileKind.TrueType => FontFileType.TrueType,
            FontFileKind.TrueTypeCollection => FontFileType.TrueTypeCollection,
            FontFileKind.Type1PFM => FontFileType.Type1PFM,
            FontFileKind.Type1PFB => FontFileType.Type1PFB,
            FontFileKind.Vector => FontFileType.Vector,
            FontFileKind.Bitmap => FontFileType.Bitmap,
            _ => FontFileType.Unknown,
        };

        internal static FontSimulations ToFontSimulations(int flags) => (FontSimulations)flags;
        internal static int FromFontSimulations(FontSimulations flags) => (int)flags;

        // ---------------------------------------------------------------------------------
        //  句柄
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 把一个原生形态的字体面指针还原成托管字体面。
        /// PresentationCore 只会把**我们自己发出去的**令牌以 `(IDWriteFontFace*)` 的形式传回来
        /// （Factory.cs:227 的 `new FontFace((Native.IDWriteFontFace*)dwriteFontFace)`），
        /// 所以这里做的是"令牌 → 对象"的反查，而不是解引用原生指针。
        /// 反查不到 → 抛（**不猜字体**，理由见 src/WpfGfx.Linux/Text/MilGlyphRunAdapter.cs:1-18）。
        /// </summary>
        internal static unsafe LinuxFontFace ResolveFontFace(Native.IDWriteFontFace* pointer)
        {
            IntPtr token = (IntPtr)pointer;
            if (token == IntPtr.Zero)
                throw new ArgumentNullException(nameof(pointer), "字体面指针为 null");

            if (FontHandleTable.TryResolveFace(token, out LinuxFontFace face)) return face;

            throw new InvalidOperationException(
                "这不是本进程发出的字体面令牌（0x" + token.ToInt64().ToString("X", CultureInfo.InvariantCulture) + "）。" +
                "Linux 侧没有原生 DWrite 对象可以解引用 —— 见 WIRING.md §0.2（Factory 的原生闸门）");
        }
    }
}
```

---

## §3 `ManagedSurface.cs` 逐条替换清单

> 记号：**原文**取自当前文件（行号为改动前的值），**替换为**给出可直接替换的语句。
> 所有替换都假设 §1.1 的 ProjectReference 与 §1.3 的 `using` 已就位。

### 3.0 文件头 using（在第 18 行 `using MS.Internal.Text.TextInterface.Native;` 之后加一行）

```csharp
using MS.Internal.Text.TextInterface.Linux;   // T2：Skia/FreeType 承载的真实现
```

同时把文件头注释里的语义分档说明补一句（可选，但建议——否则后来人会以为 `[PNSE]` 标签还在生效）：

```csharp
//   [PNSE]   需要 DWrite 原生对象或字形数据 —— 一律抛 PlatformNotSupportedException，
//            消息指向替代路线（M1 Skia 文本栈），**不返回假数据**。
//   ↓ T2 更新（2026-09-10）：A/B/C 档共 47 条已改为**委托给 Linux provider 的真实现**，
//     标签由 [PNSE] 改为 [真实现·provider]；剩余 15 条属 D 档（shaping / COM 回调 / 子集化），
//     仍为 [PNSE]。分类依据见 build/DirectWrite.Linux/PNSE-INVENTORY.md。
```

---

### 3.1 `FontFace`（12 条，A 档 6 + B 档 2 + C 档 4）

**(1) 加字段**（在 `private FontMetrics _fontMetrics;` 一行之后，第 471 行附近）：

```csharp
        /// <summary>T2：真正的字体面（Skia/FreeType 承载）。生命周期由 Font 的缓存持有，本类只借引用。</summary>
        private readonly LinuxFontFace _linuxFace;
```

**(2) 替换构造函数**（第 474-478 行）：

```csharp
        internal FontFace(IDWriteFontFace* fontFace)
        {
            // 令牌 → 托管字体面（PresentationCore 传回来的是我们自己发出去的令牌）
            _linuxFace = ProviderAdapters.ResolveFontFace(fontFace);
            _fontFace = fontFace;
            _refCount = 1;
        }

        /// <summary>T2：直接用托管字体面构造（Factory 的托管化路径用）。</summary>
        internal FontFace(LinuxFontFace linuxFace)
        {
            _linuxFace = linuxFace ?? throw new ArgumentNullException(nameof(linuxFace));
            _fontFace = (IDWriteFontFace*)linuxFace.Token;
            _refCount = 1;
        }

        /// <summary>T2：供 Font 等内部类型取用。</summary>
        internal LinuxFontFace LinuxFace => _linuxFace;
```

**(3) `DWriteFontFaceAddRef`**（第 481 行）——**必须改**，否则 MIL 拿不到可解析的令牌：

```csharp
        // 原文：internal IntPtr DWriteFontFaceAddRef => (IntPtr)_fontFace;   // 简化：不增引用计数（无原生对象）
        internal IntPtr DWriteFontFaceAddRef
        {
            get
            {
                // 上游语义：AddRef + 返回指针（GlyphTypeface.cs:1264 把它交给 MIL，
                // 注释原文 "Released in this native code function"）。
                FontHandleTable.AddRef(_linuxFace.Token);
                return _linuxFace.Token;
            }
        }
```

**(4) `FontFace` 的 12 个 PNSE 成员**：

| 骨架行 | 原文 | 替换为 |
|---|---|---|
| 482 | `internal FontFaceType Type => NotSupported.Throw<FontFaceType>(nameof(Type));` | `internal FontFaceType Type => ProviderAdapters.ToFontFaceType(_linuxFace.Type);` |
| 483 | `internal uint Index => NotSupported.Throw<uint>(nameof(Index));` | `internal uint Index => (uint)_linuxFace.FaceIndex;` |
| 484 | `internal FontSimulations SimulationFlags => NotSupported.Throw<FontSimulations>(nameof(SimulationFlags));` | `internal FontSimulations SimulationFlags => ProviderAdapters.ToFontSimulations(_linuxFace.SimulationFlags);` |
| 485 | `internal bool IsSymbolFont => NotSupported.Throw<bool>(nameof(IsSymbolFont));` | `internal bool IsSymbolFont => _linuxFace.IsSymbolFont;` |
| 487 | `internal FontMetrics Metrics => _fontMetrics ??= NotSupported.Throw<FontMetrics>(nameof(Metrics));` | `internal FontMetrics Metrics => _fontMetrics ??= ProviderAdapters.ToFontMetrics(_linuxFace.Metrics);` |
| 489 | `internal ushort GlyphCount => NotSupported.Throw<ushort>(nameof(GlyphCount));` | `internal ushort GlyphCount => (ushort)_linuxFace.GlyphCount;` |
| 490 | `internal FontFile GetFileZero() => NotSupported.Throw<FontFile>(nameof(GetFileZero));` | `internal FontFile GetFileZero() => new FontFile(_linuxFace.GetFileZero());` |
| 493-494 | `internal void GetDesignGlyphMetrics(ushort* pGlyphIndices, uint glyphCount, GlyphMetrics* pGlyphMetrics) => throw NotSupported.Throw(nameof(GetDesignGlyphMetrics));` | 见下方 **(5)** |
| 495-497 | `internal void GetDisplayGlyphMetrics(...)` | 见下方 **(6)** |
| 498-499 | `internal void GetArrayOfGlyphIndices(uint* pCodePoints, uint glyphCount, ushort* pGlyphIndices) => throw NotSupported.Throw(nameof(GetArrayOfGlyphIndices));` | 见下方 **(7)** |
| 500-504 | `internal bool TryGetFontTable(OpenTypeTableTag openTypeTableTag, out byte[] tableData) { tableData = null; throw NotSupported.Throw(nameof(TryGetFontTable)); }` | 见下方 **(8)** |
| 505-509 | `internal bool ReadFontEmbeddingRights(out ushort fsType) { fsType = 0; throw NotSupported.Throw(nameof(ReadFontEmbeddingRights)); }` | `internal bool ReadFontEmbeddingRights(out ushort fsType) => _linuxFace.ReadFontEmbeddingRights(out fsType);` |

**(5) `GetDesignGlyphMetrics` 的实体**（替换第 493-494 行）：

```csharp
        internal void GetDesignGlyphMetrics(ushort* pGlyphIndices, uint glyphCount, GlyphMetrics* pGlyphMetrics)
        {
            if (glyphCount == 0) return;

            var glyphs = new ReadOnlySpan<ushort>(pGlyphIndices, (int)glyphCount);
            var metrics = new GlyphMetricsData[glyphCount];
            _linuxFace.GetDesignGlyphMetrics(glyphs, metrics);

            for (int i = 0; i < glyphCount; i++)
                pGlyphMetrics[i] = ProviderAdapters.ToGlyphMetrics(metrics[i]);
        }
```

**(6) `GetDisplayGlyphMetrics` 的实体**（替换第 495-497 行）：

```csharp
        internal void GetDisplayGlyphMetrics(ushort* pGlyphIndices, uint glyphCount, GlyphMetrics* pGlyphMetrics,
                                             float emSize, bool useDisplayNatural, bool isSideways, float pixelsPerDip)
        {
            if (glyphCount == 0) return;

            var glyphs = new ReadOnlySpan<ushort>(pGlyphIndices, (int)glyphCount);
            var metrics = new GlyphMetricsData[glyphCount];
            _linuxFace.GetDisplayGlyphMetrics(glyphs, metrics, emSize, useDisplayNatural, isSideways, pixelsPerDip);

            for (int i = 0; i < glyphCount; i++)
                pGlyphMetrics[i] = ProviderAdapters.ToGlyphMetrics(metrics[i]);
        }
```

> ⚠️ 口径提醒：provider 的 `GetDisplayGlyphMetrics` 返回**设计单位**（Display 模式下已吸附到像素网格），
> 与上游 `GetGdiCompatibleGlyphMetrics` 一致。PresentationCore 的消费方
> （`GlyphTypeface.cs:1112-1150`）本来就是拿它**除以 DesignEmHeight**，所以口径吻合，不需要再缩放。

**(7) `GetArrayOfGlyphIndices` 的实体**（替换第 498-499 行）：

```csharp
        internal void GetArrayOfGlyphIndices(uint* pCodePoints, uint glyphCount, ushort* pGlyphIndices)
        {
            if (glyphCount == 0) return;

            // DWrite 的入参是 UTF-32 码点数组，与 provider 的 int 码点一一对应。
            var codePoints = new int[glyphCount];
            for (int i = 0; i < glyphCount; i++) codePoints[i] = (int)pCodePoints[i];

            var glyphs = new ushort[glyphCount];
            _linuxFace.GetArrayOfGlyphIndices(codePoints, glyphs);

            for (int i = 0; i < glyphCount; i++) pGlyphIndices[i] = glyphs[i];
        }
```

> 越界码点（> 0x10FFFF）由 provider 统一返回 0（.notdef），不会读到非法数据。

**(8) `TryGetFontTable` 的实体**（替换第 500-504 行）：

```csharp
        internal bool TryGetFontTable(OpenTypeTableTag openTypeTableTag, out byte[] tableData)
        {
            // provider 用 uint 标签（DWRITE_MAKE_OPENTYPE_TAG 语义），骨架的枚举值与之相同。
            return _linuxFace.TryGetFontTable((uint)openTypeTableTag, out tableData);
        }
```

**(9) `Release`**（第 492 行）—— **必须改**，否则引用计数不生效：

```csharp
        // 原文：internal void Release() => _refCount--;
        internal void Release() => _linuxFace?.Release();
```

> 上游 `Font::GetFontFace()` **每次调用都 +1 引用**（Font.cpp:GetFontFace：缓存命中 AddRef、
> 未命中 AddRef 后入缓存），PresentationCore 在 7 个 finally 里 Release
> （GlyphTypeface.cs:93,967,1079,1103,1275,1562,1703）。
> provider 的 `Release()` 只递减计数、**不销毁对象**（对象生命周期归 Font/集合），
> 所以这里**不要**把 Release 映射成 `Dispose()` —— 那会变成 use-after-free。
> `Dispose()`（第 512 行）保持 `=> Release();` 不变。

---

### 3.2 `FontFile`（2 条，A 档）

| 骨架行 | 原文 | 替换为 |
|---|---|---|
| 515-520 | 注释 + `private readonly IDWriteFontFile* _fontFile;` + `internal FontFile(IDWriteFontFile* fontFile) => _fontFile = fontFile;` | 见下方 (1) |
| 524-533 | `internal bool Analyze(out DWRITE_FONT_FILE_TYPE dwriteFontFileType, out DWRITE_FONT_FACE_TYPE dwriteFontFaceType, out uint numberOfFaces, int* hr) { ... throw NotSupported.Throw(nameof(Analyze)); }` | 见下方 (2) |
| 535 | `internal string GetUriPath() => NotSupported.Throw<string>(nameof(GetUriPath));` | `internal string GetUriPath() => _linuxFile?.GetUriPath() ?? string.Empty;` |

**(1) 字段与构造**：

```csharp
        private readonly IDWriteFontFile* _fontFile;
        private readonly LinuxFontFile _linuxFile;   // T2

        internal FontFile(IDWriteFontFile* fontFile)
        {
            _fontFile = fontFile;
            // 令牌 → 托管文件（同 FontFace：反查，不解引用）
            if (fontFile != null && FontHandleTable.TryResolveFile((IntPtr)fontFile, out LinuxFontFile file))
                _linuxFile = file;
        }

        /// <summary>T2：直接用托管字体文件构造。</summary>
        internal FontFile(LinuxFontFile linuxFile)
        {
            _linuxFile = linuxFile ?? throw new ArgumentNullException(nameof(linuxFile));
            _fontFile = (IDWriteFontFile*)FontHandleTable.RegisterFile(linuxFile);
        }

        internal LinuxFontFile LinuxFile => _linuxFile;
```

> `FontHandleTable.RegisterFile/TryResolveFile` 需要在 provider 里补两个方法
> （与现有的 `Register(LinuxFontFace)` 同构，10 行；见 §3.7 的 provider 侧补充）。

**(2) `Analyze` 的实体**：

```csharp
        internal bool Analyze(out DWRITE_FONT_FILE_TYPE dwriteFontFileType,
                              out DWRITE_FONT_FACE_TYPE dwriteFontFaceType,
                              out uint numberOfFaces,
                              int* hr)
        {
            dwriteFontFileType = default;
            dwriteFontFaceType = default;
            numberOfFaces = 0;
            if (hr != null) *hr = 0;

            if (_linuxFile == null) return false;                 // 与 DWRITE_E_FILEFORMAT 等价：false 而不是抛

            if (!_linuxFile.Analyze(out FontFileAnalysis analysis)) return false;

            dwriteFontFileType = ProviderAdapters.ToDwriteFileType(analysis.FileKind);
            dwriteFontFaceType = ProviderAdapters.ToDwriteFaceType(analysis.FaceKind);
            numberOfFaces = (uint)analysis.NumberOfFaces;
            return true;
        }
```

> 需要 `ProviderAdapters` 里再加两个反向映射（provider 枚举 → `Native.DWRITE_*`）：
> `ToDwriteFileType` / `ToDwriteFaceType`，各一个 switch（值逐项对齐，见 §2 的同名正映射）。

---

### 3.3 `Font`（13 条，A 档 8 + C 档 5）

**(1) 字段与构造**（替换第 542-547 行）：

```csharp
        private readonly IDWriteFont* _font;
        private readonly LinuxFont _linuxFont;   // T2

        internal Font(IDWriteFont* font)
        {
            _font = font;
        }

        /// <summary>T2：用托管字面构造（FontCollection/FontFamily 的托管化路径用）。</summary>
        internal Font(LinuxFont linuxFont)
        {
            _linuxFont = linuxFont ?? throw new ArgumentNullException(nameof(linuxFont));
            _font = (IDWriteFont*)linuxFont.DWriteFontAddRef;
        }

        internal LinuxFont LinuxFont => _linuxFont;
```

**(2) `DWriteFontAddRef`**（第 548 行）——**必须改**（GlyphRun.cs:1876 的 `pIDWriteFont` 就是它）：

```csharp
        // 原文：internal IntPtr DWriteFontAddRef => (IntPtr)_font;
        internal IntPtr DWriteFontAddRef => _linuxFont.DWriteFontAddRef;
```

**(3) 其余 13 条 PNSE**：

| 骨架行 | 原文 | 替换为 |
|---|---|---|
| 549 | `internal FontFamily Family => NotSupported.Throw<FontFamily>(nameof(Family));` | `internal FontFamily Family => _linuxFont.Family == null ? null : new FontFamily(_linuxFont.Family);` |
| 550 | `Weight` → PNSE | `internal FontWeight Weight => (FontWeight)_linuxFont.Weight;` |
| 551 | `Stretch` → PNSE | `internal FontStretch Stretch => (FontStretch)_linuxFont.Stretch;` |
| 552 | `Style` → PNSE | `internal FontStyle Style => (FontStyle)_linuxFont.Style;` |
| 553 | `IsSymbolFont` → PNSE | `internal bool IsSymbolFont => _linuxFont.IsSymbolFont;` |
| 554 | `FaceNames` → PNSE | `internal LocalizedStrings FaceNames => ProviderAdapters.ToLocalizedStrings(_linuxFont.FaceNames);` |
| 555 | `SimulationFlags` → PNSE | `internal FontSimulations SimulationFlags => ProviderAdapters.ToFontSimulations(_linuxFont.SimulationFlags);` |
| 556 | `Metrics` → PNSE | `internal FontMetrics Metrics => ProviderAdapters.ToFontMetrics(_linuxFont.Metrics);` |
| 557 | `Version` → PNSE | `internal double Version => _linuxFont.Version;` |
| 558-559 | `DisplayMetrics(emSize, pixelsPerDip)` → PNSE | `internal FontMetrics DisplayMetrics(float emSize, float pixelsPerDip) => ProviderAdapters.ToFontMetrics(_linuxFont.DisplayMetrics(emSize, pixelsPerDip));` |
| 561 | `GetFontFace()` → PNSE | `internal FontFace GetFontFace() => new FontFace(_linuxFont.GetFontFace());` |
| 562-567 | `GetInformationalStrings(...)` → PNSE | 见下方 (4) |
| 568-569 | `HasCharacter(uint)` → PNSE | `internal bool HasCharacter(uint unicodeValue) => _linuxFont.HasCharacter((int)unicodeValue);` |

**(4) `GetInformationalStrings` 的实体**：

```csharp
        internal bool GetInformationalStrings(InformationalStringID informationalStringID,
                                              out LocalizedStrings informationalStrings)
        {
            informationalStrings = null;
            if (!_linuxFont.GetInformationalStrings((int)informationalStringID, out LocalizedStringsData data))
                return false;

            informationalStrings = ProviderAdapters.ToLocalizedStrings(data);
            return informationalStrings != null;
        }
```

> 枚举值对齐：骨架的 `InformationalStringID`（InformationalStringID.h）与 provider 的
> `InformationalStrings` 常量都是 DWRITE 的字面序号（None=0 … SampleText=15），直接 `(int)` 转换即可。

**(5) `ResetFontFaceCache`**（第 560 行）保持不变：

```csharp
        internal static void ResetFontFaceCache() { /* 无原生缓存：no-op 是真实现，不是假成功 */ }
```

> 语义核对：上游缓存挂在 `Font` 实例上（Font.h 的 `_fontFaceCache` 是静态数组，但按 Font 身份查找），
> Linux 侧我们的缓存也在 `LinuxFont` 实例上，确实没有全局表可清。空实现是"真的没东西可清"。

---

### 3.4 `FontList` / `FontFamily`（12 条，A 档 5 + C 档 7）

**(1) `FontList` 加字段与托管构造**（替换第 573-583 行）：

```csharp
    internal class FontList : IEnumerable<Font>
    {
        private readonly LinuxFontList _linuxList;   // T2

        internal FontList() { }

        /// <summary>T2：用托管列表构造。</summary>
        internal FontList(LinuxFontList linuxList) => _linuxList = linuxList;

        internal LinuxFontList LinuxList => _linuxList;

        internal virtual Font this[uint index] => new Font(_linuxList[(int)index]);
        internal virtual uint Count => (uint)(_linuxList?.Count ?? 0);
        internal virtual FontCollection FontsCollection =>
            _linuxList?.FontsCollection == null ? null : new FontCollection(_linuxList.FontsCollection);

        public IEnumerator<Font> GetEnumerator()
        {
            if (_linuxList == null) yield break;
            foreach (LinuxFont font in _linuxList) yield return new Font(font);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
```

> ⚠️ 这里有一处**语义升级**：上游 `FontList` 的枚举器在"未开始/已结束"时抛
> `LocalizedErrorMsgs.EnumeratorNotStarted / EnumeratorReachedEnd`（FontList.h:104-120）。
> C# 的 `yield` 迭代器天然是"未开始取 Current 抛 InvalidOperationException"，
> 但**消息文本**不同。若有测试依赖那条文案，需要显式实现 `IEnumerator`（见 §6 的说明）。

**(2) `FontFamily`**（替换第 586-601 行）：

```csharp
    internal sealed class FontFamily : FontList
    {
        private readonly LinuxFontFamily _linuxFamily;   // T2

        internal FontFamily() { }

        /// <summary>T2：用托管族构造。</summary>
        internal FontFamily(LinuxFontFamily linuxFamily)
            : base(linuxFamily)
        {
            _linuxFamily = linuxFamily;
        }

        internal LinuxFontFamily LinuxFamily => _linuxFamily;

        internal LocalizedStrings FamilyNames => ProviderAdapters.ToLocalizedStrings(_linuxFamily.FamilyNames);
        internal bool IsPhysical => _linuxFamily.IsPhysical;
        internal bool IsComposite => _linuxFamily.IsComposite;
        internal string OrdinalName => _linuxFamily.OrdinalName;
        internal FontMetrics Metrics => ProviderAdapters.ToFontMetrics(_linuxFamily.Metrics);

        internal FontMetrics DisplayMetrics(float emSize, float pixelsPerDip) =>
            ProviderAdapters.ToFontMetrics(_linuxFamily.DisplayMetrics(emSize, pixelsPerDip));

        internal Font GetFirstMatchingFont(FontWeight weight, FontStretch stretch, FontStyle style) =>
            new Font(_linuxFamily.GetFirstMatchingFont((int)weight, (int)stretch, (int)style, FontMatchingRule.Distance));

        internal FontList GetMatchingFonts(FontWeight weight, FontStretch stretch, FontStyle style) =>
            new FontList(_linuxFamily.GetMatchingFonts((int)weight, (int)stretch, (int)style));
    }
```

> **匹配规则的选择**：默认传 `FontMatchingRule.Distance`（DWrite 风格：字重距离优先）。
> 它和 M1 `TextFontDescription` 的粗体桶口径在 **WPF FontWeight 的全部 10 个枚举值 × 3 种 style
> = 30/30 组合上结论相同**（T2 已断言），差异只在 551..599 这 49 个非枚举整数上。
> 若主控希望与 M1 逐值一致，把 `Distance` 换成 `BoldBucket` 即可（实测 1..1000 全部一致）。

**(3) `FontList` 的 4 条与 `FontFamily` 的 8 条**：全部由上面两段整体覆盖（共 12 条 → 0 条 PNSE）。

---

### 3.5 `FontCollection`（5 条，A 档 1 + C 档 4）

**(1) 字段与构造**（替换第 604-608 行）：

```csharp
    internal sealed unsafe class FontCollection
    {
        private readonly IDWriteFontCollection* _collection;
        private readonly LinuxFontCollection _linuxCollection;   // T2

        internal FontCollection(IDWriteFontCollection* collection) => _collection = collection;

        /// <summary>T2：用托管集合构造。</summary>
        internal FontCollection(LinuxFontCollection linuxCollection) =>
            _linuxCollection = linuxCollection ?? throw new ArgumentNullException(nameof(linuxCollection));

        /// <summary>T2：宿主（Factory 的托管化路径）用它按目录建集合。</summary>
        internal static FontCollection FromDirectory(string directory) =>
            new FontCollection(LinuxFontCollection.FromDirectory(directory));
```

**(2) 5 条 PNSE**：

| 骨架行 | 原文 | 替换为 |
|---|---|---|
| 610 | `internal uint FamilyCount => NotSupported.Throw<uint>(nameof(FamilyCount));` | `internal uint FamilyCount => (uint)(_linuxCollection?.FamilyCount ?? 0);` |
| 611 | `internal FontFamily this[uint familyIndex] => NotSupported.Throw<FontFamily>("FontCollection.this[uint]");` | `internal FontFamily this[uint familyIndex] => new FontFamily(_linuxCollection[(int)familyIndex]);` |
| 612 | `internal FontFamily this[string familyName] => NotSupported.Throw<FontFamily>("FontCollection.this[string]");` | 见下方 (3) |
| 613-617 | `FindFamilyName(...)` → PNSE | `internal bool FindFamilyName(string familyName, out uint index) { bool found = _linuxCollection.FindFamilyName(familyName, out int i); index = (uint)Math.Max(0, i); return found; }` |
| 618-619 | `GetFontFromFontFace(FontFace)` → PNSE | `internal Font GetFontFromFontFace(FontFace fontFace) { LinuxFont font = _linuxCollection.GetFontFromFontFace(fontFace.LinuxFace); return font == null ? null : new Font(font); }` |

**(3) `this[string]` 的实体**：

```csharp
        internal FontFamily this[string familyName]
        {
            get
            {
                // 上游语义：找不到时 DWrite 走 FindFamilyName 的 false 分支，WPF 自己有兜底逻辑。
                // 这里返回 null（**不回落系统字体、不造空族**），让上游的兜底逻辑照常生效。
                var family = _linuxCollection?[familyName];
                return family == null ? null : new FontFamily(family);
            }
        }
```

---

### 3.6 `InternalFactory` / `TextAnalyzer` / 其余

**`InternalFactory.CreateFontFile`**（第 670-672 行，A 档）：

```csharp
        internal static int CreateFontFile(IDWriteFactory* factory, FontFileLoader fontFileLoader,
                                           Uri filePathUri, IDWriteFontFile** dwriteFontFile)
        {
            *dwriteFontFile = null;

            if (filePathUri == null || !IsLocalUri(filePathUri)) return HResultFromWin32(2) /* ERROR_FILE_NOT_FOUND */;

            string path = filePathUri.LocalPath;
            if (!LinuxFontFile.AnalyzeFile(path, 0, out FontFileAnalysis _, out OpenTypeFontData _))
                return unchecked((int)0x88985000);   // DWRITE_E_FILEFORMAT（与 Factory.cs:24 的常量一致）

            var token = (IDWriteFontFile*)FontHandleTable.RegisterFile(new LinuxFontFile(path));
            *dwriteFontFile = token;
            return 0;   // S_OK
        }
```

> 说明：这条路径只产出"文件句柄"，**不解析字体**（与 DWrite 的
> `CreateFontFileReference` 同层次）。真正取字体面由 `Factory.CreateFontFace`（§0.2）负责。
> `FontHandleTable.RegisterFile` 见 §3.7。

**`TextAnalyzer`**（第 803-861 行，D 档 6 条）：**保持 PNSE**，但把 `NotSupported.Reason`
换成指向降级清单的文案（见 §7.2）。`CharHyphen`（第 810 行）保持不变（它本来就是真实现）。

**`LocalizedErrorMsgs`**（2 条，C 档）：见 §6。

**`FontFileLoader` / `FontFileStream` / `FontFileEnumerator` / `TrueTypeSubsetter`**
（第 912-974、1010-1014 行，D 档 9 条）：**保持 PNSE**，文案改写见 §7.2。

---

### 3.7 provider 侧的补充（**T2 需要补的 3 个小方法**，在主控执行接线前由 T2 补完）

`FontHandleTable` 目前只有 `LinuxFontFace` 的登记；接 §3.2/§3.6 需要文件对象的登记。
最小实现（与现有 `Register(SKTypeface)` 同构）：

```csharp
        // 文件对象的令牌（FontFile 的 DWriteFontFileNoAddRef / InternalFactory.CreateFontFile 用）
        public static IntPtr RegisterFile(LinuxFontFile file);
        public static bool TryResolveFile(IntPtr token, out LinuxFontFile file);
        public static bool UnregisterFile(IntPtr token);
```

> 这三条属于 Phase 1 的收尾项，**接线前我会补**（或在接线时一并加，10 行代码）。

---

## §4 令牌桥接：让 MIL 能反查骨架发出去的令牌（1 行）

**为什么必须做**：`GlyphTypeface.cs:1264` 把 `DWriteFontFaceAddRef` 的 `IntPtr` 交给
`MilGlyphRun_GetGlyphOutline`，而 MIL 侧（M7a 已实现）用
`MilFontFaceTable.TryResolve(pFontFace, out SKTypeface)` 反查；反查不到就返回 `E_HANDLE`
（不猜字体），表现是"画不出字形轮廓"。

**改动位置**：`PresentationCore/ModuleInitializer.cs`（或任何 PresentationCore 启动点）

```csharp
// T2：把 DWriteForwarder 发出的字体面令牌交给 MIL 的句柄表发放 ——
// 这样两边是**同一套令牌**，MIL 不需要额外注册就能反查。
// MilFontFaceTable.Register 的签名是（src/WpfGfx.Linux/Interop/MilHandleTables.cs:544）：
//     public static IntPtr Register(SKTypeface typeface)
MS.Internal.Text.TextInterface.Linux.FontHandleTable.TokenAllocator =
    WpfGfx.Linux.Interop.MilFontFaceTable.Register;
```

**替代方案（若主控不希望 PresentationCore 直接引用 WpfGfx.Linux 类型）**：
在 `MilFontFaceTable` 上加一个可设置的解析钩子（`Resolver`），由 PresentationCore 安装：

```csharp
// WpfGfx.Linux/Interop/MilHandleTables.cs（**src/ 改动，需主控裁决**）
public static Func<IntPtr, SKTypeface> ExternalResolver { get; set; }
```
但这样两边是两套令牌，容易出现"骨架发 A、MIL 找 B"的不一致 —— **推荐 §4 的直连方案**。

**验证方法（接线后必做）**：

```bash
export PATH="$HOME/.dotnet:$PATH"
cd build/DirectWriteForwarder.Linux && dotnet build      # 0 错 0 警
# 运行期：MilGlyphRun_GetGlyphOutline 不再返回 E_HANDLE，且轮廓点数 > 0
```
T2 已在 Tests 里把这条链路的前提验证过（`ProviderShapeTests.Font_DWriteFontAddRef_ProducesResolvableToken`：
令牌 → SKTypeface → `SKFont.GetGlyphPath('A').PointCount > 0`）。

---

## §5 【必须改】`ManagedSurface.cs` 的 `LineSpacing` 少了 `(double)` 强转

**这是一个真 bug，不是风格问题。** T2 在灌入真值后才暴露出来。

| 项 | 内容 |
|---|---|
| 上游原文 | `CPP/DWriteWrapper/FontMetrics.h:124`：`return (double)(this->Ascent + this->Descent + this->LineGap) / DesignUnitsPerEm;` |
| 骨架现状 | `ManagedSurface.cs:319`：`public double LineSpacing => (Ascent + Descent + LineGap) / DesignUnitsPerEm;` |
| 后果 | `Ascent`/`Descent` 是 `ushort`、`LineGap` 是 `short`、`DesignUnitsPerEm` 是 `ushort` → `int / int` **先整数除法**。Noto Sans 实测：应返回 **1.362**，实际返回 **1**（截断） |
| 影响面 | `FontMetrics.LineSpacing` → `PhysicalFontFamily.cs:426,431` → `Typeface.LineSpacing` → **每一行的行高从 1.362 em 变成 1.0 em**，多行文本行距全错 |
| 为什么至今没暴露 | 该成员被标为 `[真实现]`（公式取自头文件），但类型从未用真实数据构造过 —— 没有数据就走不到 |

**替换**（第 318-319 行）：

```csharp
        /// <summary>
        /// [真实现] 头文件公式（FontMetrics.h:124）：
        ///   (double)(Ascent + Descent + LineGap) / DesignUnitsPerEm
        /// 注意 (double) 强转**不能省**：Ascent/Descent 是 ushort、LineGap 是 short、
        /// DesignUnitsPerEm 是 ushort，少了强转会走整数除法（Noto Sans: 1362/1000 = 1 而不是 1.362）。
        /// </summary>
        public double LineSpacing => (double)(Ascent + Descent + LineGap) / DesignUnitsPerEm;
```

`Baseline`（第 316 行）**无需改**：`LineGap * 0.5` 已经把表达式提升成 `double`。

---

## §6 `LocalizedErrorMsgs` 的潜在陷阱（2 条，建议但不是必须）

现状（第 651-661 行）：getter 在未被赋值前抛 PNSE，setter 正常赋值。
`DWriteFactory.cs:25-26` 只做**赋值**，所以今天不会炸；但只要有谁先读后写就会抛。

**建议替换**（给英文兜底文案，而不是抛）：

```csharp
        internal static string EnumeratorNotStarted
        {
            get => _enumeratorNotStarted ?? "Enumerator not started.";
            set => _enumeratorNotStarted = value;
        }

        internal static string EnumeratorReachedEnd
        {
            get => _enumeratorReachedEnd ?? "Enumerator reached the end.";
            set => _enumeratorReachedEnd = value;
        }
```

> 这与 we 的 `FontList.GetEnumerator` 用 `yield` 实现有关：C# 迭代器在"未开始取 Current"
> 时抛 `InvalidOperationException` 而**不是** `LocalizedErrorMsgs` 的文案。
> 若上游有测试断言那条文案，需要把 `FontList.GetEnumerator` 改成手写 `IEnumerator`（约 25 行），
> 这是接线时可选的"完全对齐"项 —— T2 判定优先级低（PC 侧没有这样的测试，见调用点普查）。

---

## §7 验收与降级文案

### 7.1 接线后的验收命令与预期

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/links-dev/netTest/wpf-linux-20260906/wpf-linux

# 1. provider 与探针仍然自洽（T2 的 82 条断言）
dotnet build build/DirectWrite.Linux/Provider/DirectWrite.Linux.Provider.csproj
dotnet build build/DirectWrite.Linux/Probe/DirectWrite.Linux.Probe.csproj
dotnet test  build/DirectWrite.Linux/Tests/DirectWrite.Linux.Tests.csproj      # 期望 82 通过 / 0 失败

# 2. 骨架仍为 0 错 0 警（接线不应引入任何警告）
dotnet build build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj

# 3. 全仓构建（PresentationCore 0 错 0 警不变）
./verify-all.sh          # 或主控既有的串行验证脚本

# 4. 出字验证（HelloWpf 运行期）
#    判据：窗口里出现文字；且 MilGlyphRun_GetGlyphOutline 不再返回 E_HANDLE
```

### 7.2 D 档 15 条的 PNSE 文案（替换 `NotSupported.Reason`）

`NotSupported.Reason` 现在的文案指向 M1 Skia 文本栈。接线后建议改成分类更精确的版本
（这样"剩下的 PNSE"一眼能看出属于哪一类）：

```csharp
        internal const string Reason =
            "Linux 侧该成员尚未实现（T2 分类：D 档）。A/B/C 档 47 条已由 " +
            "build/DirectWrite.Linux/Provider（Skia/FreeType）承载，见 " +
            "build/DirectWrite.Linux/PNSE-INVENTORY.md 与 WIRING.md。";
```

`TextAnalyzer` 家族单独给一条更具体的：

```csharp
            "TextAnalyzer 需要排版 shaping 引擎（GSUB 连字/上下文替换、GPOS kerning/mark 定位），" +
            "Linux 侧本轮未接（T2 降级清单 #2/#3，见 build/DirectWrite.Linux/REPORT.md）。" +
            "简单文本路径（SimpleTextLine → ComputeUnshapedGlyphRun → GetArrayOfGlyphIndices）不经过本方法。";
```

`TrueTypeSubsetter` 单独一条：

```csharp
            "TrueType 子集化器未实现（T2 降级清单 #1）：需要重写 glyf/loca/cmap 表。" +
            "仅 XPS/IDeviceFont 序列化路径需要（FontDriver.cs:263），不影响屏幕渲染。";
```

---

## §8 接线时**不要**做的事（每一条都有代价）

| 不要 | 原因 |
|---|---|
| 不要给 `FontFace.Release()` 接 `Dispose()` | 上游 Release 只递减引用（对象由缓存持有）；接 Dispose 会 use-after-free |
| 不要让 `GetFontFace()` 返回**没有** AddRef 的同一个对象 | 上游每次 +1（Font.cpp:GetFontFace）。下游 7 个 finally 会把它释放掉 |
| 不要让 DWF 引用 `WpfGfx.Linux` | 方向应是 PresentationCore 同时引用两者；DWF → WpfGfx 会把 MIL 拉进字体层 |
| 不要引用 `Probe` / `Tests` 工程 | 它们是取证工程（含 xunit、含 20 万字符的测试语料），不是产品依赖 |
| 不要在 `FontFace.Metrics` 里自己算 | 度量口径（hhea vs OS/2、符号约定）只应有一份，在 provider 里且被 3 条通路交叉验证过 |
| 不要把 `GetGdiCompatibleGlyphMetrics` 的返回值再乘 `emSize/upem` | provider 返回的是**设计单位**（与上游一致），消费方本来就除以 DesignEmHeight |
| 不要动 `TextAnalyzer.CharHyphen`（第 810 行） | 它已经是真实现（上游字面量 `\x002d`） |
