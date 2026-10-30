#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1b/D3 应用器：把 `TextFormatterImp` 的两处 **FullTextLine（= LineServices）回退**
接到托管完整路径 `HbTextFallback`（幂等；无参运行 = 应用；`--check` 只读）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textline-fallback.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textline-fallback.py

======================================================================================
【根因（实测，非推断）】
  Linux 上没有 LineServices。实测走查（`src/Linux/build/MilBridge/gen/t1b-ls-live-*.txt`，装置 =
  ld.so 的 `LD_DEBUG=symbols` 符号查找日志）：

    WpfTextDemo : `LoCreateContext` 被查找 **27 次**，`libwpfwin32.so` 里该符号**不存在**
                  ⇒ `EntryPointNotFoundException` ⇒ **abort(134)**
                  （T3 的 `WPTD_SUMMARY=FAIL … blocker=lineservices:LoCreateContext`）
    HelloWpf    : `LoAcquireBreakRecord` / `LoCreateLine` / `LoCreateContext` **各 0 次**
                  （小文本留在 `SimpleTextLine` 快路径）⇒ 不崩

  上游只有一条回退（`TextFormatterImp.cs:236-246`）：
      `SimpleTextLine.Create` 返回 null ⇒ `new TextMetrics.FullTextLine(...)` ⇒
      `new TextFormatterContext()` ⇒ `TextFormatterContext.cs:113 LoCreateContext` ⇒ 崩。
  第二个同类站点：`TextFormatterImp.cs:309`（`FormatMinMaxParagraphWidth`）⇒
  `TextBlock.MeasureOverride` 会在**测量阶段**先崩。

【设计：替换 LS 这条回退路，**不**绕过 TextFormatter】
  · `SimpleTextLine` 快路径**一字不动**；
  · 只在本该进 LS 的两处，**先试托管路径**（`WpfLinux.Shims.PresentationCore.HbTextFallback`）；
  · 托管路径**任何不确定输入都返回 null**（非 `TextCharacters` 的 run / 字体解析不到文件 /
    异常 …），此时**原样走原来那条 `FullTextLine`** ⇒ 行为与接线前逐字一致；
  · 开关复用 `WPF_LINUX_TEXTLINE`（关掉 ⇒ 立刻回到接线前行为，便于 A/B 对照）；
  · 接了/没接/为什么没接，全部**计数**并出现在 `HB_TEXTLINE …` 汇总行里。

【同时接线】`DefineConstants` 追加 `TEXTLINE_SHIM_DIRECT`：
  现在 PC 里编的是 shim 的**反射分支**（能跑，但每次构造走反射）；加上它才走**直构分支**
  （`src/Linux/build/MilBridge/tests/DirectBranchCheck/` 已用 PC 的 IVT 名额把那一支单独编过，0 错 0 警）。

【为什么是"生成物 + csproj 接线"而不是改上游】
  上游 `upstream/wpf/**` 只读（本工程铁律：上游零改动）。生成物落在
  `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/`，csproj 用 `Compile Remove` 去掉上游那条、`Compile Include` 换上生成物。

【幂等】重复运行：内容一致则不重写；csproj 里见到 MARKER 就不再注入。
【锚点纪律】锚点必须**逐字**匹配且**恰好出现一次**，否则**报错退出**（绝不静默产出未打补丁的副本）。
"""

import argparse
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
GENERATED = os.path.join(PC_DIR, "TextFormatterImp.Linux.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                        "PresentationCore", "MS", "internal", "TextFormatting", "TextFormatterImp.cs")
UPSTREAM_REL = "src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/TextFormatting/TextFormatterImp.cs"

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux T1b/D3：FullTextLine 回退接到托管路径"
                "（由 src/Linux/tools/patch-presentationcore-textline-fallback.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux T1b/D3 结束 ==== -->"

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textline-fallback.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/TextFormatting/TextFormatterImp.cs`
//        逐字复制 + 2 处 T1b/D3 修改（把 FullTextLine 回退接到托管 `HbTextFallback`）
//        + T1c/RTL 宽松兜底类（`WpfLinuxLenientTextFallback`；`#17` 起它同时收两个 DIP 缩进形参，
//          `#23` P2 起它还把**段落原点** `paragraphOrigin: cpFirst` 透给工厂 —— 缺它则宽松档帧恒 0）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 计数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打（实测）：Linux 无 LineServices。WpfTextDemo 上 `LoCreateContext` 被查找 27 次而
//   `libwpfwin32.so` 里没有该符号 ⇒ EntryPointNotFoundException ⇒ abort(134)；HelloWpf 因小文本
//   留在 SimpleTextLine 快路径而 0 次查找。托管路径接不上时**原样回退**，行为与接线前一致。

"""

# =====================================================================================
#  逐字锚点 → 替换（含缩进）
# =====================================================================================

# ── 站点 1：FormatLineInternal 的 FullTextLine 回退（上游 :236-246）──
ANCHOR_1 = """            if (textLine == null)
            {
                // content is complex, creating complex line
                textLine = new TextMetrics.FullTextLine(
                    settings,
                    firstCharIndex,
                    lineLength,
                    RealToIdealFloor(paragraphWidth),
                    LineFlags.None
                    ) as TextLine;
            }
"""

REPL_1 = """            if (textLine == null)
            {
                // WPF-on-Linux（T1b/D3）：先试**托管完整路径**（HarfBuzz 排版 + 本工程的断行规则）。
                // 接不了（返回 null）⇒ 原样走下面那条 LineServices 回退，行为与接线前**逐字一致**。
                // 接了/没接/为什么没接都在 `HB_TEXTLINE …` 汇总行里（fallbackCalls/Handled/Bailed/lastBail）。
                textLine = WpfLinux.Shims.PresentationCore.HbTextFallback.TryFormatLine(
                    textSource,
                    firstCharIndex,
                    paragraphWidth,
                    textSource.PixelsPerDip,
                    settings.Pap.AlwaysCollapsible,
                    settings.Pap.LineHeight,     // 0 = 未设 ⇒ 托管侧用字体自然行高（真机口径）
                    // ── WAVE19 §0.1 B（`P4` 的同波兄弟件）：**严格档也要收缩进** ──
                    //   修前：`HbTextFallback.TryFormatLine` 形参表里没有 indent，
                    //   它转发的是**不收 indent 的那个** `FormatParagraph` 重载 ⇒
                    //   `Indent`/`ParagraphIndent` 在**默认配置最先尝试的这一层**被整条丢掉
                    //   （`R17A-recon.md` §1.2 F1；`#17` 的 P2 只修好了它**下面**那条宽松兜底）。
                    //   ⚠️ 单位：**原始 DIP**。`settings.Pap.Indent` / `.ParagraphIndent` 是
                    //   **理想整数 ×300**（`TextProperties.cs:39-40` 的 `RealToIdeal`）
                    //   ⇒ 传它们会引入一个新的 300 倍错误（`W17B-report.md` §7.6 的近失，
                    //   由本脚本的 P2 负断言机器化地挡住）。
                    //   `paragraphProperties` 是 `FormatLineInternal` 的形参（生成物 `:524`）
                    //   ⇒ 零换算、零舍入，与宽松档（`:610`）**同一来源、同一口径**。
                    //   语义（oracle 逐字符定下的）：`indentDip` = 网格锚点；
                    //   内容起点 = `Indent + ParagraphIndent`。
                    indentDip: paragraphProperties.Indent,
                    paragraphIndentDip: paragraphProperties.ParagraphIndent
                    ) as TextLine;
            }

            if (textLine == null)
            {
                // content is complex, creating complex line
                textLine = new TextMetrics.FullTextLine(
                    settings,
                    firstCharIndex,
                    lineLength,
                    RealToIdealFloor(paragraphWidth),
                    LineFlags.None
                    ) as TextLine;
            }
"""

# ── 站点 2：FormatMinMaxParagraphWidth 的 FullTextLine（上游 :308-325）──
ANCHOR_2 = """            // create specialized line specifically for min/max calculation
            TextMetrics.FullTextLine line = new TextMetrics.FullTextLine(
                settings,
                firstCharIndex,
                0,  // lineLength
                0,  // paragraph width has no significant meaning in min/max calculation
                (LineFlags.KeepState | LineFlags.MinMax)
                );

            // line width in this case is the width of a line when the entire paragraph is laid out
            // as a single long line.
            MinMaxParagraphWidth minMax = new MinMaxParagraphWidth(line.MinWidth, line.Width);
            line.Dispose();
            return minMax;
"""

REPL_2 = """            // WPF-on-Linux（T1b/D3）：先试**托管路径**（宽=∞ 取最长行 ⇒ MaxWidth；宽=0 强制断 ⇒ MinWidth）。
            // 接不了 ⇒ 原样走下面那条 LineServices 回退（`TextBlock.MeasureOverride` 会走到这里）。
            double hbMinWidth, hbMaxWidth;
            if (WpfLinux.Shims.PresentationCore.HbTextFallback.TryMinMaxParagraphWidth(
                    textSource,
                    textSource.PixelsPerDip,
                    out hbMinWidth,
                    out hbMaxWidth
                    ))
            {
                return new MinMaxParagraphWidth(hbMinWidth, hbMaxWidth);
            }

            // create specialized line specifically for min/max calculation
            TextMetrics.FullTextLine line = new TextMetrics.FullTextLine(
                settings,
                firstCharIndex,
                0,  // lineLength
                0,  // paragraph width has no significant meaning in min/max calculation
                (LineFlags.KeepState | LineFlags.MinMax)
                );

            // line width in this case is the width of a line when the entire paragraph is laid out
            // as a single long line.
            MinMaxParagraphWidth minMax = new MinMaxParagraphWidth(line.MinWidth, line.Width);
            line.Dispose();
            return minMax;
"""

# ---- (3) T1c/RTL：**宽松托管兜底**（跳过"不支持的 run 类型"而不是 bail）+ 计数器 ----
ANCHOR_3 = '    internal sealed class TextFormatterImp : TextFormatter\n'

REPLACEMENT_3 = r"""    /// <summary>
    /// T1c/RTL · **宽松的托管兜底**：`HbTextFallback.TryFormatLine` 对"不支持的 run 类型"是
    /// **bail ⇒ 原样回落 LineServices**，而 Linux 上没有 LS ⇒ `LoCreateContext` 抛 ⇒ **abort(134)**
    /// （实测：`text-rtl` 块 `FlowDirection=RightToLeft` 一碰就死）。本类把那一类输入**接管**：
    ///   · 收集段落时**跳过**非 `TextCharacters`/`TextEndOfLine` 的 run（计数 + 记住类型名），**不 bail**；
    ///   · 其余（字体解析 / 工厂调用）与 shim 同源；任何失败**照旧返回 null**（不假装成功）；
    ///   · 读过什么、跳过了什么、为什么失败 —— 全部计数并可由 <see cref="Diagnostics"/> 读取。
    /// **方向限制（如实登记）**：`HbTextLineFactory.FormatParagraph` 目前没有 RTL 参数
    /// ⇒ 本兜底能**保证不崩**，但 RTL 段落的**视觉顺序可能仍是 LTR**。这一条属排版车道
    /// （需要在工厂里加 bidi 级别支持），本类不假装支持。
    /// </summary>
    internal static class WpfLinuxLenientTextFallback
    {
        /// <summary>诊断开关：与 shim 同源（`WPF_LINUX_TEXTLINE_DIAG`），缺省关。</summary>
        internal const string DiagEnvVar = "WPF_LINUX_TEXTLINE_DIAG";

        private static int s_calls;
        private static int s_handled;
        private static int s_failed;
        private static int s_skippedRuns;
        private static int s_blankParagraphs;
        /// <summary>⭐ `D-T5` 修法（WAVE24 §1 P1）：**隐形 run（空 CBR）计数** —— 修了但没生效会当场露馅。</summary>
        private static int s_invisibleRuns;
        /// <summary>⭐ `D-T5-R` 修法：**段落默认 props 兜底**被真正用上的次数。
        /// ⚠️ 为什么必须有这个计数器：本件的失败形态是"**接线了但没生效**"（`#17` 那一族）——
        ///   兜底形参加了、调用点却没传 ⇒ `paragraphDefault` 恒 null ⇒ 行为与修前**逐位相同**却
        ///   **看不出来**。⇒ 把"兜底真的被取用"这件事做成**运行期可读**的读数。
        ///   `hiddenonly` 修后必须 ≥ 1；阳性对照（props 正常的段落）必须 **0**。</summary>
        private static int s_paraDefaults;
        private static string s_lastSkip = "-";
        private static string s_lastSkippedRange = "-";
        private static string s_lastInvisible = "-";
        private static string s_lastFail = "-";
        private static bool s_substitutedBlank;

        /// <summary>诊断读数（探针/复验可反射读取；与 shim 的 `HB_TEXTLINE …` 并列）。</summary>
        internal static string Diagnostics
        {
            get
            {
                return "relaxedCalls=" + s_calls
                     + " relaxedHandled=" + s_handled
                     + " relaxedFailed=" + s_failed
                     + " relaxedSkippedRuns=" + s_skippedRuns
                     + " relaxedBlankParagraphs=" + s_blankParagraphs
                     + " relaxedInvisibleRuns=" + s_invisibleRuns
                     + " relaxedParaDefaults=" + s_paraDefaults
                     + " lastSkip=\"" + s_lastSkip + "\""
                     + " lastSkippedRange=\"" + s_lastSkippedRange + "\""
                     + " lastInvisible=\"" + s_lastInvisible + "\""
                     + " lastFail=\"" + s_lastFail + "\"";
            }
        }

        private static void Diag(string message)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DiagEnvVar))) return;
            try { Console.Error.WriteLine("[TEXTLINE_RELAXED] " + message); Console.Error.Flush(); } catch (Exception) { }
        }

        /// <summary>
        /// ⭐ **`D-T5` 修法（WAVE24 §1 P1）：先分类、再取字符**（上游的分类法，不是"任何 run 都取字符"）。
        ///
        /// 【上游法律】`MS.Internal.TextFormatting.TextProperties.GetRunType`（`:396-412`）：
        ///   · 只有 `ITextSymbols`/`TextShapeableSymbols` 是 `Plsrun.Text`（`:398`）—— **唯一** deref
        ///     `CharacterBuffer` 的那一支（`FormatSettings.cs:196-202`）；
        ///   · `TextEndOfParagraph`/`TextEndOfLine` ⇒ 断行（`:404-408`）；
        ///   · **其余一律 `Plsrun.Hidden`**（`:410` 注释逐字："Other text run type are all considered
        ///     hidden by LS"）⇒ `Hidden` 拿的是**哨兵字符** `TextStore.PwchHidden`（`FormatSettings.cs:241-246`），
        ///     **从不 deref CBR**。
        /// 【本仓真身】空 CBR 的 `TextHidden`/`TextModifier`/`TextEndOfSegment`（含 `pf` 自产的
        ///   `MS.Internal.Text.TextSpanModifier`，`ComplexLine.cs:424/433/449`、`LineBase.cs:198/207/223`）
        ///   是**合法的隐形 run**：`Length ≥ 1`（**占码元**）、宽度 0（**Ghost**）。
        /// 【修前缺陷】`ExtractRun` **先 deref、后分类** ⇒ 隐形 run 的空 CBR ⇒ `buf == null` ⇒ `return null`
        ///   ⇒ `CollectLenient` `return false` ⇒ 交回 LineServices ⇒ Linux 上 `LoCreateContext`
        ///   ⇒ **进程级 abort(134)**（`D-T5`，`W23A-report.md` 实测）。
        /// 【修法】分类**落在 deref 之前**：隐形 run 绝不碰 CBR；按上游 Ghost 语义给**零宽占位**
        ///   （<see cref="GhostChar"/>），`Length` 仍由调用方按 `cp += run.Length` 记账 ⇒ **账目守恒**。
        /// </summary>
        private static bool IsTextRunType(TextRun run)   // 上游 `Plsrun.Text` 那一支（`GetRunType:398`）
        {
            return run is ITextSymbols || run is TextShapeableSymbols;
        }

        /// <summary>隐形 run 的**零宽占位字符**。
        /// 上游用的是 LineServices 的哨兵字符（`TextStore.PwchHidden`，声明 `TextStore.cs:2393`、
        /// 由 `esc.szHidden` 赋值 `:86`）—— 那个哨兵**由 LS 自己消费、不整形** ⇒ 我方**不能照抄那个码位**
        /// （我们会把它交给 HarfBuzz 整形）。
        /// 取 `U+200B ZWSP`：**零宽**是它的定义性质；HarfBuzz 对 default-ignorable 一律给 0 advance。
        /// 仓内实测佐证：`src/Linux/tests/parity/windows/layout-b34` 的 `A1_nbsp_zwsp_*` 族（文本含 **2 个** U+200B）
        /// 在 `tline` 臂与**真机**逐行一致（`src/Linux/build/MilBridge/arm-logs/tline.log`，该族唯一不一致的是一例
        /// `Collapse hasCollapsed` 期望，**宽度契约全过**）⇒ ZWSP 在我方管线里**贡献 0 宽度**。
        /// ⚠️ **如实登记的偏差**：UAX#14 里 ZWSP = **可断**（ZW 类）而真机的 Ghost run **不产生断点**
        /// ⇒ 本修法会在"原本会 abort 的段落"上**多出断点**（这些段落修前必然 abort ⇒ 不会让任何**现有**
        /// 读数变差；但它是与真机的偏离，已在 `W24A-report.md` 登记为残项）。</summary>
        private const char GhostChar = '\u200B';

        private static string ExtractRun(TextRun run)
        {
            if (run == null || run.Length <= 0) return string.Empty;
            // ⭐ 分类**在 deref 之前**：隐形 run 的 CBR 本来就是空的（`sealed`，`TextHidden.cs:44-47`）
            //    ⇒ **绝不碰它**（修前正是"先碰了"才 abort）。
            if (!IsTextRunType(run))
            {
                ++s_invisibleRuns;
                s_lastInvisible = run.GetType().Name + " x" + run.Length;
                return new string(GhostChar, run.Length);   // 占码元、零宽（上游 Ghost 语义）
            }
            CharacterBufferReference cbr = run.CharacterBufferReference;
            MS.Internal.CharacterBuffer buf = cbr.CharacterBuffer;      // 类型在 MS.Internal（PC 内部可见）
            if (buf == null) return null;      // 取字符类 run 却拿不到 buffer ⇒ 仍**诚实失败**（不假装成功）
            int off = cbr.OffsetToFirstChar;
            char[] chars = new char[run.Length];
            for (int i = 0; i < run.Length; ++i) chars[i] = buf[off + i];
            return new string(chars);
        }

        /// <summary>**进入 return false 之前**先诊断（T3 那次吃亏：诊断走的是被 abort 截断的那条路）。</summary>
        private static void DiagBeforeReturn(string why)
        {
            Diag("**即将交回 LS**（Linux 上等于 abort）原因：" + why
                 + " ｜ skipped=" + s_skippedRuns + " lastSkip=" + s_lastSkip);
        }

        /// <summary>**宽松收集**：跳过不支持的 run 类型（而不是 bail）。
        ///
        /// ⭐ **`D-T5-R` 修法（WAVE25 §2 / W25A）**：加 `paragraphDefault` 兜底形参。
        /// 【上游法律】全隐形段落里**每一个 run 的 `Properties` 都按上游法律恒为 `null`**：
        ///   · `TextHidden.Properties => null`（`TextHidden.cs:62-65`，**`sealed`**）；
        ///   · 段末 `TextEndOfParagraph(1)` → `TextEndOfLine(length, null)` → `_textRunProperties = null`
        ///     （`TextEndOfLine.cs:29` / `:49` / `:76-79`）。
        ///   ⇒ 遍历完整段，`props` **连一次被赋成非 null 的机会都没有** ⇒ 修前 `:243` 必然
        ///     `return false` ⇒ 交回 LineServices ⇒ `LoCreateContext` ⇒ **abort(134)**。
        ///     对比 `hidden1`/`hiddenmid`：段里有 `TextCharacters`（带真 props）⇒ 修前就绿。
        /// 【兜底来源】`paragraphProperties.DefaultTextRunProperties` —— 上游正式成员
        ///   （`TextParagraphProperties.cs:63`），且**非 null + `Typeface` 非 null 已被机器强制**
        ///   （见本文件 `FormatLineInternal` 起点的 `ArgumentNullException.ThrowIfNull` 一族）
        ///   ⇒ 恰好是 `ResolveFont` 需要的两样。仓内先例：`TextBlock.Linux.cs:2856`。
        /// 【形参带默认值】⇒ 既有调用点零改动（照 `indentDip`/`paragraphIndentDip` 先例）；
        ///   ⚠️ 但这**也正是"半接线"能静默通过的原因** ⇒ 必须靠**调用点计数牙齿**（本应用器的
        ///   `n_default_src == 2`）把"两个站点真的传了"钉死，见 WAVE25 §2 反极性③。
        ///
        /// ★ **T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）**：新增 `out TextRun eolRun` —— 段末取到的
        ///   那个 EOL run（宿主 PtsHost 给 `ParagraphBreakRun : TextEndOfParagraph`）。
        ///   【为什么必须由**收集层**交出来】`Line.FormattingResult`（`Line.cs:928-936`）**只认**
        ///   `ParagraphBreakRun`／`LineBreakRun`（`PresentationFramework` 的 internal 类型，shim 编在
        ///   `PresentationCore` **造不出来**）⇒ shim 在段末行只能**把源给的那个对象原样放回** span 里。
        ///   【不传的后果】段末行照样报 `fsflrOutOfSpace` ⇒ 驱动把行宽里那 1 个合成位也累加进 `dcp` ⇒
        ///   下一跳探到**下一个 Block 的 `ElementStart`** ⇒ `Invariant.FailFast`（`LineBase.cs:137`，不可捕获）。
        /// </summary>
        private static bool CollectLenient(TextSource src, int cpFirst,
                                           out string text, out TextRunProperties props,
                                           out int modifierOpenIndex, out int modifierScopeEnd, out int modifierCloseIndex,
                                           out TextRun eolRun,
                                           TextRunProperties paragraphDefault = null)
        {
            text = null; props = null;
            // ★T-A30：段末 EOL run（缺省 null ⇒ 与修前逐位相同：shim 侧就地造 `TextEndOfParagraph(1)`）。
            eolRun = null;
            // ── T1c/#13：只**记位置**，不改平铺/len 口径 ──────────────────────────
            //   ⭐ **`D-T2-c`（WAVE32 §1 W32A 本波修）**：**零宽跨度**的终点 `modifierScopeEnd` 现在真的被收集。
            //   两个**互不相同**的量（分工见 `src/Linux/build/MilBridge/T1d-tab-and-modifier.md:1058` 逐字）：
            //     · `modifierScopeEnd` = **覆盖终点**（半开；-1 ⇒ 到段末）⇒ **只喂「零宽跨度」**；
            //     · `modifierCloseIndex` = 客户端配对 `TextEndOfSegment` 的下标（-1 ⇒ 从不）⇒ **只喂 `lbNull`**。
            //   【来源 ①】**`TextModifier` run 自己的字符范围**：客户端在 `cp` 处返回的 `TextModifier`
            //     覆盖 `[cp, cp + run.Length)`。真机**同形**：`OracleModifier.Length` = `ModifierEnd - ModifierStart`
            //     = 39 ⇒ 覆盖 `[6,45)`（`src/Linux/tests/parity/windows/layout-b34/src/LayoutOracle/TextModel.cs:55-68`
            //     + `Cases.cs:288-305`）。而真机真值把这件事**逐字印了出来**：`M_modifier_w200.lines[0].runs`
            //     = `[[0,6,0,45.9667],[45,17,45.9667,110.95]]` ⇒ 该 run 覆盖的 39 个字符**整体 Ghost（零宽）**、
            //     可见的只有 `[0,6)+[45,63)`（`w=156.9167`）⇒ 重基到收集串：
            //     `modifierScopeEnd = (cp - cpFirst) + run.Length`。
            //   【来源 ②（**覆盖** ①）】后来按 R1 找到了配对的 `TextEndOfSegment` ⇒ **以它为准**
            //     —— 上游 `TextModifier.cs:22-24` 逐字："The scope extends to the next matching
            //     EndOfSegment text run …, or to the next EndOfParagraph"。落在收集循环**之后**的那一行。
            //   【退化】`TextModifier` 恒有 `Length ≥ 1` ⇒ 来源 ① 必然给出值；若整段**没有** `TextModifier`
            //     （`modifierOpenIndex < 0`）⇒ 三量同为 -1 ⇒ shim 侧 `modifierOpenIndex >= 0` 不成立
            //     ⇒ **一个字符都不落零宽**（与修前逐位相同）。
            //   R1（**未验证的选择**）：配对 = `TextModifier` 之后**第一个** `TextEndOfSegment`。
            //   ⚠️ 边界：同一段里多个 `TextEndOfSegment` 时 R1 可能配错 ⇒ 需公开可判的判别式；
            //      **本仓今天没有这种真值**（b34 语料 0 处 `TextEndOfSegment`，全仓 PC 路径亦 0）。
            //   ⚠️ `TextEndOfSegment.Length` 由构造期强制 ≥1（TextEndOfSegment.cs:29/31/33/51）
            //      ⇒ **closeIndex 唯一稳的定义 = close run 的起点下标**（不许假设 Length==1）。
            modifierOpenIndex = -1; modifierScopeEnd = -1; modifierCloseIndex = -1;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int skipped = 0;
            string skippedTypes = "-";
            int cp = cpFirst;

            for (int guard = 0; guard < 4096; ++guard)
            {
                TextRun run;
                try { run = src.GetTextRun(cp); }
                catch (Exception e) { s_lastFail = "GetTextRun 抛 " + e.GetType().Name; return false; }
                if (run == null) break;

                if (run is TextEndOfLine)
                {
                    // ⚠️ **空段落只有这一个 run**（例：空 TextBlock）⇒ 若不在这里抓 properties，
                    //    `props` 会一直是 null ⇒ 连"空白行"都产不出 ⇒ 又交回 LS ⇒ abort。
                    //    （"空"有两种含义：没有正文 vs 连 run 都没有 —— 这里把前者也兜住。）
                    if (props == null)
                    {
                        try { props = run.Properties; } catch (Exception) { }
                    }
                    // ★T-A30：**只认"段末"那一种** EOL。宿主 PtsHost 造的是
                    //   `ParagraphBreakRun : TextEndOfParagraph`（`RunClient.cs:203`）；`LineBreakRun`（硬断）
                    //   **不算段末** ⇒ 不取，免把"段中间的软断"当成"排到段尾"（那会让驱动提前收束 = 假成功方向）。
                    eolRun = (run is TextEndOfParagraph) ? run : null;
                    break;                            // EOL / EOP（它的字符不属于段落正文）
                }

                if (props == null)
                {
                    try { props = run.Properties; } catch (Exception) { }
                }

                // ★ 关键修正：**任何 run 的字符都取**（基类的 CharacterBufferReference/Length），
                //   而不是"非 TextCharacters 就整段丢掉" —— 后者正是 RTL abort 的根因。
                string s;
                try { s = ExtractRun(run); }
                catch (Exception e) { s = null; s_lastFail = "ExtractRun 抛 " + e.GetType().Name; }

                if (s == null)
                {
                    DiagBeforeReturn("CharacterBuffer 取不到（run 类型 " + run.GetType().Name + "）");
                    return false;
                }

                // ── T1c/#13：在**平铺之外**多记三个位置（run 序列上、重基到收集串）──
            if (run is TextModifier)
            {
                modifierOpenIndex = cp - cpFirst;                        // ① 开
                modifierScopeEnd = (cp - cpFirst) + run.Length;          // ①′ 覆盖终点 = 本 run 的字符范围终点
            }
            if (modifierOpenIndex >= 0 && modifierCloseIndex < 0 && run is TextEndOfSegment)
            { modifierCloseIndex = cp - cpFirst; }                       // ② 关（R1）
            if (s.Length > 0) sb.Append(s);

                if (!(run is TextCharacters))
                {
                    // 记"放宽"这件事（类型 + 区间），但**字符已经进 sb** —— 不再丢
                    ++skipped;
                    skippedTypes = run.GetType().Name;
                    s_lastSkippedRange = "[" + cp + "," + (cp + run.Length) + ") " + run.GetType().Name;
                }

                cp += run.Length;
            }

            // ── ★`D-T2-c` 来源 ②：R1 找到了配对的 `TextEndOfSegment` ⇒ **以它为准**（上游语义）──
            //   来源 ① 用的是 `TextModifier` run 自己的长度（b34 真值锚：`[6,45)`）；
            //   若客户端改用"**显式终点**"形态（`TextModifier` 只是起点标记 + `TextEndOfSegment` 收尾），
            //   终点必须听 `TextEndOfSegment` 的 —— 它才是上游 `TextModifier.cs:22-24` 说的那个终点。
            //   ⚠️ 本行**必须在收集循环之后**：放进循环里会被 ① 逐 run 覆盖回去 ⇒ 静默退回 b34 口径
            //      （本脚本的"顺序牙齿"把它按**下标**钉死，不靠人读）。
            if (modifierCloseIndex >= 0) { modifierScopeEnd = modifierCloseIndex; }

            if (skipped > 0)
            {
                s_skippedRuns += skipped;
                s_lastSkip = skippedTypes + " x" + skipped;
                Diag("跳过 " + skipped + " 个不支持的 run（类型 " + skippedTypes + "）—— 不 bail，继续用托管路径");
            }

            text = sb.ToString();
            // ★ `D-T5-R` 修法（WAVE25 §2）：**段落默认 run properties 兜底**。
            //   ⚠️ **位置是本质**，不是风格：
            //     · 必须在上面两处 `if (props == null) { props = run.Properties; }`（正文 run / EOL run）
            //       **之后** —— 否则 `props` 一开始就非 null ⇒ run 自己的属性**再也不会被取**
            //       ⇒ 会打断 `hidden1`/`hiddenmid`/`eos1` 与一切多 run 段落的 props 优先级（真回归）。
            //     · 必须在下面**两个**失败点（"空段落"分支里的 `props == null` 与段末的 `props == null`）
            //       **之前** —— 一行同时覆盖两处，不必改两个 return false。
            //   ⚠️ `paragraphDefault` 为 null（调用方没接线）⇒ 行为与修前**逐位相同**
            //      ⇒ "半接线"不会静默变绿（这正是 WAVE25 §2 反极性③要守的东西）。
            if (props == null && paragraphDefault != null)
            {
                props = paragraphDefault;
                ++s_paraDefaults;
                Diag("段落里**一个 props 来源都没有**（全隐形段落）⇒ 取段落默认属性兜底"
                     + "（已计数 relaxedParaDefaults=" + s_paraDefaults + "）");
            }
            if (text.Length == 0)
            {
                // ⚠️ **绝不因为"空文本"交回 LS**（Linux 上没有 LS ⇒ 那等于 abort）。
                //   诚实做法：用**一个空格**顶一行"空白行"（画不出任何东西），并**计数 + 诊断**说明发生过。
                ++s_blankParagraphs;
                DiagBeforeReturn("段落没有可提取字符（跳过 " + skipped + " 个 run；last=" + skippedTypes
                                 + "）⇒ 改用**空白行**（已计数 relaxedBlankParagraphs）");
                if (props == null)
                {
                    ++s_failed;
                    s_lastFail = "空段落且没有 run properties ⇒ 连空白行都产不出";
                    return false;
                }
                text = " ";
                s_substitutedBlank = true;
                return true;
            }

            if (props == null)
            {
                DiagBeforeReturn("没有 run properties");
                s_lastFail = "没有 run properties";
                return false;
            }

            return true;
        }

        private static bool ResolveFont(TextRunProperties props, out GlyphTypeface glyphTypeface, out string fontPath)
        {
            glyphTypeface = null; fontPath = null;
            if (props == null) { s_lastFail = "properties 为 null"; return false; }

            Typeface tf = props.Typeface;
            if (tf == null) { s_lastFail = "Typeface 为 null"; return false; }

            GlyphTypeface gt;
            if (!tf.TryGetGlyphTypeface(out gt) || gt == null) { s_lastFail = "取不到 GlyphTypeface"; return false; }

            Uri u = gt.FontUri;
            if (u == null || !u.IsFile) { s_lastFail = "FontUri 不是本地文件"; return false; }

            string path = u.LocalPath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { s_lastFail = "字体文件不存在"; return false; }

            glyphTypeface = gt; fontPath = path;
            return true;
        }

        /// <summary>宽松版 `TryFormatLine`：接不了返回 null（调用方**原样**回落 LS，不假装成功）。</summary>
        internal static TextLine TryFormatLine(TextSource textSource, int cpFirst, double paragraphWidth,
                                              double pixelsPerDip, bool alwaysCollapsible, double lineHeight,
                                              double indentDip = 0, double paragraphIndentDip = 0,
                                              TextRunProperties paragraphDefault = null)   // ★D-T5-R
        {
            ++s_calls;
            try
            {
                s_substitutedBlank = false;
                if (textSource == null) { ++s_failed; s_lastFail = "textSource 为 null"; return null; }

                string text; TextRunProperties props;
                int modOpen, modScopeEnd, modClose;
                TextRun eolRun;
                if (!CollectLenient(textSource, cpFirst, out text, out props, out modOpen, out modScopeEnd, out modClose,
                                    out eolRun,
                                    paragraphDefault: paragraphDefault)) { ++s_failed; return null; }

                if (s_substitutedBlank)
                {
                    Diag("段落起点=" + cpFirst + " 无可提取字符 ⇒ 用**空白行**顶替（不交回 LS）");
                }

                GlyphTypeface gt; string fontPath;
                if (!ResolveFont(props, out gt, out fontPath)) { ++s_failed; return null; }

                int consumed;
                System.Collections.Generic.List<WpfLinux.Shims.PresentationCore.HbTextLine> lines =
                    WpfLinux.Shims.PresentationCore.HbTextLineFactory.FormatParagraph(
                        text, fontPath, props.FontRenderingEmSize, paragraphWidth, gt, (float)pixelsPerDip,
                        props, alwaysCollapsible, false, lineHeight, out consumed,
                            modifierOpenIndex: modOpen, modifierScopeEnd: modScopeEnd, modifierCloseIndex: modClose,
                            indentDip: indentDip, paragraphIndentDip: paragraphIndentDip,
                            // ── ★`#23` P2（`D-T6-b`，Option 1）：**宽松档**也把**段落原点**透给工厂 ──
                            //   本档**每次重起段**：`CollectLenient(textSource, cpFirst, …)` 从 `cpFirst` 收集
                            //   （`:117 int cp = cpFirst;`），收集出来的 `text` 就是**以 cpFirst 为原点的串**，
                            //   而只交回 `lines[0]` ⇒ 该行 `range.Start == 0` ⇒ 修前**帧恒 0**。
                            //   帧（= 真机 `cases[].lines[].startChar` / `GetTextBounds` 第一参数）是**段落系绝对**
                            //   下标 ⇒ 原点必须 = `cpFirst`（`#22` 实测：不传 ⇒ 宽松档 133 行帧红）。
                            //   尾随可选形参 ⇒ 其它调用点零改动、逐位等价。
                            paragraphOrigin: cpFirst,
                            // ── ★T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：**段末 EOL run** 透给工厂 ──
                            //   `eolRun` = 收集层在段末取到的**源自己的**那个 run（宿主 PtsHost 给
                            //   `ParagraphBreakRun : TextEndOfParagraph`）⇒ 段末行的末 span 用它 ⇒
                            //   `Line.FormattingResult`（`Line.cs:928-936`，**只认** `ParagraphBreakRun`／
                            //   `LineBreakRun`）报 `fsflrEndOfParagraph` ⇒ 驱动在段末收束、**不再越界探 Block**。
                            eopRun: eolRun);

                if (lines == null || lines.Count == 0) { ++s_failed; s_lastFail = "工厂返回 0 行"; return null; }

                ++s_handled;
                Diag("接手（宽松）：段落起点=" + cpFirst + " 行数=" + lines.Count + " 文本长=" + text.Length);
                return lines[0];
            }
            catch (Exception e)
            {
                ++s_failed;
                s_lastFail = e.GetType().Name + ": " + e.Message;
                Diag("异常 " + s_lastFail);
                return null;
            }
        }

        /// <summary>宽松版 `TryMinMaxParagraphWidth`：与 shim 同构（宽=∞ 取 Max，宽=0 取 Min）。</summary>
        internal static bool TryMinMaxParagraphWidth(TextSource textSource, double pixelsPerDip,
                                                     out double minWidth, out double maxWidth,
                                                     TextRunProperties paragraphDefault = null)   // ★D-T5-R
        {
            ++s_calls;
            minWidth = 0; maxWidth = 0;
            try
            {
                if (textSource == null) { ++s_failed; s_lastFail = "textSource 为 null（minmax）"; return false; }

                s_substitutedBlank = false;
                string text; TextRunProperties props;
                int modOpen2, modScopeEnd2, modClose2;
                TextRun eolRun2;   // ★T-A30：段末 EOL run（min/max 只量宽，但**三个站点同一把尺子**）
                if (!CollectLenient(textSource, 0, out text, out props, out modOpen2, out modScopeEnd2, out modClose2,
                                    out eolRun2,
                                    paragraphDefault: paragraphDefault)) { ++s_failed; return false; }

                if (s_substitutedBlank)
                {
                    // 空段落：min=max=0 是**诚实**答案（没有任何字符可排），且**不交回 LS**
                    minWidth = 0; maxWidth = 0;
                    ++s_handled;
                    Diag("minmax：空段落 ⇒ min=max=0（不交回 LS）");
                    return true;
                }

                GlyphTypeface gt; string fontPath;
                if (!ResolveFont(props, out gt, out fontPath)) { ++s_failed; return false; }

                int c1, c2;
                System.Collections.Generic.List<WpfLinux.Shims.PresentationCore.HbTextLine> wide =
                    WpfLinux.Shims.PresentationCore.HbTextLineFactory.FormatParagraph(
                        text, fontPath, props.FontRenderingEmSize, double.MaxValue, gt, (float)pixelsPerDip,
                        props, false, false, 0, out c1,
                            modifierOpenIndex: modOpen2, modifierScopeEnd: modScopeEnd2, modifierCloseIndex: modClose2,
                            eopRun: eolRun2);   // ★T-A30：与宽松档**同一对**（三站点同尺）
                for (int i = 0; i < wide.Count; ++i) if (wide[i].Width > maxWidth) maxWidth = wide[i].Width;

                // ── WAVE17 §1 P3（`D-T2` **真身**）：min 探针必须与 max 探针**用同一把尺子** ──
                //   修前形态：max 传了 `modifierOpenIndex/modifierCloseIndex`，min **一个都不传**
                //   ⇒ `modifierOpenIndex >= 0` 时 max 走"`[open, 段末)` 零宽"、min 走"完全无作用域"
                //   ⇒ 读出 **`min > max`**，而真机契约（`TextFormatterImp.cs:257-258`）排除了它。
                //   实测（W17D 车道，`src/Linux/build/MilBridge/tests/MinMaxProbe/`，权威 `pc 1280323c9173bcde`）：
                //     修前 `MINMAX CASE mod0 RESULT min=22.652344 max=0.000000 rel=gt`（阴性对照 `rel=lt`）。
                //   ⚠️ 本件**只**补这一处的参数对等（`#25` 前的形态）；max 那条 `modifierScopeEnd = -1`
                //     （"到段末"）曾是**已知错**的跨度（shim `:3854-3858` 自述）—— 那是另一个登记
                //     `D-T2-c`，**已由 WAVE32 §1 W32A 本波修**（`CollectLenient` 现在收"覆盖终点"，
                //     本文件三个站点**同时**把 `modifierScopeEnd` 传下去）。本条注释保留其历史口径。
                System.Collections.Generic.List<WpfLinux.Shims.PresentationCore.HbTextLine> narrow =
                    WpfLinux.Shims.PresentationCore.HbTextLineFactory.FormatParagraph(
                        text, fontPath, props.FontRenderingEmSize, 0.0, gt, (float)pixelsPerDip,
                        props, false, false, 0, out c2,
                            modifierOpenIndex: modOpen2, modifierScopeEnd: modScopeEnd2, modifierCloseIndex: modClose2,
                            eopRun: eolRun2);
                for (int i = 0; i < narrow.Count; ++i) if (narrow[i].Width > minWidth) minWidth = narrow[i].Width;

                ++s_handled;
                return true;
            }
            catch (Exception e)
            {
                ++s_failed;
                s_lastFail = "minmax 异常 " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }

    internal sealed class TextFormatterImp : TextFormatter
"""

# ---- (4) 站点 1 的宽松兜底调用（插在 LS 之前）----
ANCHOR_4 = '''            if (textLine == null)
            {
                // content is complex, creating complex line
                textLine = new TextMetrics.FullTextLine('''

REPLACEMENT_4 = '''            if (textLine == null)
            {
                // ── T1c/RTL：**宽松托管兜底**（跳过不支持的 run 类型，而不是 bail）──
                //   为什么必须有这一层：shim 的 `TryCollect` 对"不支持的 run 类型"是 bail，
                //   而 Linux 上没有 LineServices ⇒ 原样回落 = `LoCreateContext` 抛 ⇒ abort(134)。
                //   （实测：WpfFeatureProbe 的 `text-rtl` 块 `FlowDirection=RightToLeft` 一碰就死。）
                textLine = WpfLinuxLenientTextFallback.TryFormatLine(
                    textSource,
                    firstCharIndex,
                    paragraphWidth,
                    textSource.PixelsPerDip,
                    settings.Pap.AlwaysCollapsible,
                    settings.Pap.LineHeight,
                    // ── WAVE17 §1 P2（`D-T3`）：缩进必须**按原始 DIP** 从宿主对象取 ──
                    //   `settings.Pap.Indent` / `.ParagraphIndent` 是**理想整数 ×300**
                    //   （`TextProperties.cs:39-40` 的 `RealToIdeal`，`LineServices.cs:1290` 因子 = 28800/96 = 300）
                    //   ⇒ 传它们会引入一个**新的 300 倍错误**（R17A-recon.md §1.2 F2）。
                    //   `paragraphProperties` 是本方法的形参（生成物 `:512`）⇒ 零换算、零舍入。
                    //   语义（oracle 逐字符定下的）：`indentDip` = 停靠网格锚点 = `Indent`；
                    //   `paragraphIndentDip` = 段落缩进 ⇒ 内容起点 = `Indent + ParagraphIndent`。
                    indentDip: paragraphProperties.Indent,
                    paragraphIndentDip: paragraphProperties.ParagraphIndent,
                    // @@D-T5R-SITE@@ 段落默认 run properties 兜底（D-T5-R；标识本站点，供本脚本计数牙齿定位）
                    // ── ★`#25` W25A（`D-T5-R`）：**段落默认 run properties** 兜底 ──
                    //   "整段全是隐形 run"（`TextHidden`）的段落里，**每个 run 的 `Properties` 按上游法律
                    //   恒为 `null`**（`TextHidden.cs:62-65` sealed 返 null；段末 `TextEndOfParagraph(1)`
                    //   → `TextEndOfLine(length,null)` 的 `_textRunProperties` 也是 null）
                    //   ⇒ 收集层**一个 props 来源都没有** ⇒ 修前 `CollectLenient` 在段末
                    //     `if (props == null)` 处 `return false` ⇒ 交回 LS ⇒ `LoCreateContext` ⇒ abort(134)。
                    //   `paragraphProperties` 是本方法的形参 ⇒ `DefaultTextRunProperties` **零换算**；
                    //   它的**非 null + `Typeface` 非 null** 已由 `FormatLineInternal` 起点的
                    //   `ArgumentNullException.ThrowIfNull` 一族机器强制 ⇒ 恰好满足 `ResolveFont`。
                    paragraphDefault: paragraphProperties.DefaultTextRunProperties
                    ) as TextLine;
            }

            if (textLine == null)
            {
                // content is complex, creating complex line
                textLine = new TextMetrics.FullTextLine('''

# ---- (5) 站点 2 的宽松兜底调用 ----
ANCHOR_5 = '''            // create specialized line specifically for min/max calculation
            TextMetrics.FullTextLine line = new TextMetrics.FullTextLine('''

REPLACEMENT_5 = '''            // ── T1c/RTL：宽松托管兜底（min/max 版；同站点 1 的理由）──
            //   ★`#25` W25A（`D-T5-R`）：**两个站点必须同时接**（`D-T2` 的教训："同一把尺子"）——
            //   否则全隐形段落在 min/max 站点仍会 `return false` ⇒ 交回 LS ⇒ abort(134)。
            //   ⚠️ **具名实参必须写在 `out` 实参之后**（C# 规则：具名实参**后面不许**再跟位置实参；
            //      写反了 ⇒ `CS8323 命名参数的使用位置不当` —— 本波真编一次抓到的，见报告）。
            //      ⚠️ 侦察报告 `d-t5r-plan.md` 附 A5 写的"写在 `out` 之前"**已被编译推翻**。
            // @@D-T5R-SITE@@ 段落默认 run properties 兜底（D-T5-R；标识本站点，供本脚本计数牙齿定位）
            double lenientMin, lenientMax;
            if (WpfLinuxLenientTextFallback.TryMinMaxParagraphWidth(textSource, textSource.PixelsPerDip,
                    out lenientMin, out lenientMax,
                    paragraphDefault: paragraphProperties.DefaultTextRunProperties))
            {
                return new MinMaxParagraphWidth(lenientMin, lenientMax);
            }

            // create specialized line specifically for min/max calculation
            TextMetrics.FullTextLine line = new TextMetrics.FullTextLine('''

EDITS = [
    ("站点1 FormatLineInternal 的 FullTextLine 回退", ANCHOR_1, REPL_1),
    ("站点2 FormatMinMaxParagraphWidth 的 FullTextLine 回退", ANCHOR_2, REPL_2),
    ("T1c/RTL 宽松兜底类", ANCHOR_3, REPLACEMENT_3),
    ("T1c/RTL 站点1 宽松兜底调用", ANCHOR_4, REPLACEMENT_4),
    ("T1c/RTL 站点2 宽松兜底调用", ANCHOR_5, REPLACEMENT_5),
]

REQUIRED_IN_OUTPUT = [
    ("站点1 托管调用", "WpfLinux.Shims.PresentationCore.HbTextFallback.TryFormatLine("),
    ("站点2 托管调用", "WpfLinux.Shims.PresentationCore.HbTextFallback.TryMinMaxParagraphWidth("),
    ("站点1 原回退仍在", "RealToIdealFloor(paragraphWidth),"),
    ("站点2 原回退仍在", "MinMaxParagraphWidth minMax = new MinMaxParagraphWidth(line.MinWidth, line.Width);"),
    # ---- T1c/RTL 宽松兜底 ----
    ("宽松兜底类存在", "internal static class WpfLinuxLenientTextFallback"),
    ("宽松兜底：站点1 调用", "WpfLinuxLenientTextFallback.TryFormatLine("),
    ("宽松兜底：站点2 调用", "WpfLinuxLenientTextFallback.TryMinMaxParagraphWidth(textSource, textSource.PixelsPerDip,"),
    ("宽松兜底：**跳过**而不是 bail（关键放宽）", "++skipped;"),
    ("宽松兜底：有诊断读数", "internal static string Diagnostics"),
    ("宽松兜底：RTL 方向限制如实登记", "视觉顺序可能仍是 LTR"),
    # ---- T3 事故（波 6：空文本 ⇒ 交回 LS ⇒ abort）的修复牙齿 ----
    #   ⚠️ WAVE24 §1 P1 **修正文案**：原标签写"**任何 run 都取字符**（不再只认 TextCharacters）"——
    #      那句话**已被实测推翻**（`W23A-report.md` ⑦.1：`TextHidden` 一族在默认配置下由 `SimpleTextLine`
    #      当 Ghost run 处理；而 `D-T5` 的修法**正是"不再任何 run 都取字符"**）。
    #      needle 的**代码串**（方法签名）逐字未动 ⇒ 只改文案，**不改射程**。
    ("D-T5 修法：`ExtractRun` 先分类再取字符（隐形 run 不 deref CBR）", "private static string ExtractRun(TextRun run)"),
    ("**空文本不再交回 LS**（改成空白行 + 计数）", "绝不因为\"空文本\"交回 LS"),
    ("空段落计数存在", "relaxedBlankParagraphs="),
    ("被跳过区间可追溯", "lastSkippedRange="),
    ("**进入 return false 之前先诊断**", "private static void DiagBeforeReturn(string why)"),
    ("空段落（只有 EOL run）也能抓到 properties", "//    （\"空\"有两种含义：没有正文 vs 连 run 都没有 —— 这里把前者也兜住。）"),
    ("两处调用数 = 2", None),   # 由下面的计数断言处理
    # ---- WAVE24 §1 P1（`D-T5` 修法）的**正向**断言（射程一旦缩到零必须当场报错）----
    #   ① 分类器 = 上游 `Plsrun.Text` 那一支（`TextProperties.GetRunType:398`）；
    ("D-T5 正向：分类按上游 `Plsrun.Text` 那一支（`ITextSymbols`/`TextShapeableSymbols`）",
     "return run is ITextSymbols || run is TextShapeableSymbols;"),
    #   ② **关键的"顺序"断言**：分类/false 分支必须**落在 deref CBR 之前** ——
    #      这正是 `D-T5` 的根因（修前是先 `cbr.CharacterBuffer` 再判 null ⇒ 隐形 run 必然 null ⇒ abort）。
    #      射程 = 生成物里必须出现这条 guard；它一旦被删/被挪到 deref 之后 ⇒ 修法即失效 ⇒ 必须红。
    ("D-T5 正向：**隐形 run 在 deref CBR 之前**被拦下（`if (!IsTextRunType(run))`）",
     "if (!IsTextRunType(run))"),
    #   ③ 隐形 run 的**占位**必须给足 `run.Length` 个码元（= 上游 Ghost 语义的"占码元"那一半；
    #      少了它 = "返空串"那种假修 ⇒ 行 `Length` 账对不上）。
    ("D-T5 正向：隐形 run 按 `Length` 给零宽占位（账目守恒，不是「返空串」）",
     "return new string(GhostChar, run.Length);"),
    #   ④ 计数器可读（"修了但没生效"这一族必须能被外部看见）。
    ("D-T5 正向：隐形 run 计数出现在 `Diagnostics` 里", "relaxedInvisibleRuns="),
    # ---- WAVE17 §1 P2（`D-T3`）：indent 接线的**正向**断言（射程一旦缩到零必须当场报错）----
    ("P2：宽松兜底同时收两个 DIP 缩进形参",
     #   ⚠️ `#25` W25A **同步那两处尾巴**（`--check` 当场报红抓到的，如实留档）：
     #      `D-T5-R` 在 `paragraphIndentDip` **之后**又加了 `TextRunProperties paragraphDefault = null`
     #      ⇒ 本条 needle 的 `)` 尾巴与 `W19-B` 那条的 `\n) as TextLine;` 尾巴**都不再匹配**。
     #      ⇒ 两处只改「尾巴」，**标识串本体逐字未动** ⇒ 射程（缩进形参对）**一个字节都没变**。
     "double indentDip = 0, double paragraphIndentDip = 0,"),
    ("P2：两个槽分别送 shim 的两个形参（不是同一个量送两次）",
     "indentDip: indentDip, paragraphIndentDip: paragraphIndentDip,"),
    # ---- WAVE23 §2 P2（`D-T6-b`）的**正向**断言（射程一旦缩到零必须当场报错）----
    #   ⚠️ ★T-A30：本站点实参表**尾**又追加了 `eopRun: eolRun`（见 `T-A30` 计数牙齿）⇒ 本 needle 的
    #      收尾从 `…cpFirst);` 改为 `…cpFirst,`（**射程一字未减**：仍是"段落原点 = `cpFirst`"）。
    ("W23-B：宽松兜底把**段落原点**透给工厂（= `cpFirst`；缺它则帧恒 0）",
     "paragraphOrigin: cpFirst,"),
    ("P2：站点1 从宿主对象取**原始 DIP** 的 Indent",
     "indentDip: paragraphProperties.Indent,"),
    ("P2：站点1 从宿主对象取**原始 DIP** 的 ParagraphIndent",
     "paragraphIndentDip: paragraphProperties.ParagraphIndent"),
    # ---- WAVE17 §1 P3（`D-T2` 真身）：min 探针与 max 探针的**参数对等** ----
    ("P3：min 探针也收了 modifier 作用域实参（调用行尾）",
     "props, false, false, 0, out c2,"),
    ("P3：min 探针传的是**同一个** `modOpen2/modScopeEnd2/modClose2`（与 max 同源）",
     "modifierOpenIndex: modOpen2, modifierScopeEnd: modScopeEnd2, modifierCloseIndex: modClose2,"),
    # ---- WAVE32 §1 W32A（`D-T2-c`）的**正向**断言（射程一旦缩到零必须当场报错）----
    #   【为什么必须是"每个站点"】`modifierScopeEnd` 是**尾随可选形参** ⇒ 漏传一个站点**编译得过**
    #   而那条路径的零宽跨度照旧是"到段末"（= 修前的错），**其它路径却会绿** ⇒ 这正是
    #   "半接线能静默通过"的形态。⇒ 用**逐站点**needle + 下面的**站点计数**牙齿一起钉死。
    ("W32A：`CollectLenient` 形参表里有 `out int modifierScopeEnd`（与 open/close 同列）",
     "out int modifierOpenIndex, out int modifierScopeEnd, out int modifierCloseIndex,"),
    ("W32A：站点1（宽松档 `TryFormatLine`）把 `modifierScopeEnd` 传下去",
     "modifierScopeEnd: modScopeEnd,"),
    ("W32A：站点2/3（`TryMinMaxParagraphWidth` 的 max/min 两探针）把 `modifierScopeEnd` 传下去",
     "modifierScopeEnd: modScopeEnd2,"),
    ("W32A：覆盖终点 = `TextModifier` run 自己的字符范围终点（重基到收集串）",
     "modifierScopeEnd = (cp - cpFirst) + run.Length;"),
    ("W32A：配对 `TextEndOfSegment` 存在时**以它为准**（覆盖行）",
     "if (modifierCloseIndex >= 0) { modifierScopeEnd = modifierCloseIndex; }"),
    # ---- WAVE19 §0.1 B（严格档 indent 缺口）的**正向**断言（射程一旦缩到零必须当场报错）----
    ("W19-B：站点1（**严格档**）从宿主对象取**原始 DIP** 的 Indent",
     #   ⚠️ `#25` W25A 留档的三次试错（三次都**当场**被 `--check` 抓住，如实登记 —— 纪律 3/25
     #      要求的形状：牙齿必须能真红）：
     #      v1：我以为"加了兜底实参 ⇒ 旧收尾不再匹配"，就把尾巴改成「兜底实参 + `) as TextLine;`」
     #          ⇒ 报 `缩进实参对收尾 = 1（要求 2）`。**真因**：宽松兜底**站点2**
     #          （`TryMinMaxParagraphWidth`）**从来不是**以 `) as TextLine;` 收尾（它收在 `out lenientMax))`）
     #          ⇒ 那条尾巴**只**属于 REPL_1 与 REPLACEMENT_4 两处。
     #      v2：修 REPLACEMENT_4 尾巴时我误给 `paragraphIndentDip: …` 补了一个**逗号** ——
     #          而 **REPL_1 那一处没有逗号** ⇒ 见我把它回退；同时本 needle 也跟着误加了逗号。
     #      v3（本版口径，已实测）：**本 needle 用 REPL_1 的原始形状（无逗号）**；
     #          `_INDENT_ARGS` 用「实参对 + 兜底实参**之前**的注释行 + `) as TextLine;`」——
     #          它恰好匹配 **REPL_1 + REPLACEMENT_4** 两处（站点2 不以 `) as TextLine;` 收尾）
     #          ⇒ 计数仍 **2**，与 `#19` 以来**语义相同**（两处"缩进实参对收尾"）。
     #      ⚠️ **本波两处 needle 的"尾巴"被迫改写，但标识串（两行缩进来源）逐字未动**
     #         ⇒ 射程（`indentDip`/`paragraphIndentDip` 的来源与口径）**一个字节都没变**。
     "                    // @@D-T5R-SITE@@ 段落默认 run properties 兜底"),
    ("W19-B：站点1 的 `settings.Pap.LineHeight` 仍逐字在位（没有被顺手改写/删除）",
     "                    settings.Pap.LineHeight,     // 0 = 未设 ⇒ 托管侧用字体自然行高（真机口径）"),
    # ---- WAVE25 §2（`D-T5-R`）的**正向**断言（射程一旦缩到零必须当场报错）----
    #   ① 兜底形参**必须带默认值**（否则既有两个调用点全部编译不过 —— 但更要紧的是：
    #      带默认值正是"半接线能静默通过"的**前提**，所以它必须与下面的**计数**牙齿成对存在）。
    ("D-T5-R 正向：`CollectLenient` 的兜底形参带默认值（既有调用点零改动）",
     "TextRunProperties paragraphDefault = null)"),
    #   ② 兜底**语句本身**在位（少了它 ⇒ 全隐形段落照旧 abort）。
    ("D-T5-R 正向：段落默认 props 兜底语句在位",
     "if (props == null && paragraphDefault != null)"),
    #   ③ 兜底**必须真的被取用**（"接上了"≠"生效了"）：用了就计数，计数进 `Diagnostics`。
    ("D-T5-R 正向：兜底被取用的次数进 `Diagnostics`（可外部读）",
     "relaxedParaDefaults="),
    #   ④ `TryMinMaxParagraphWidth` 也收了兜底形参（`D-T2` 的教训：两个站点必须同一把尺子）。
    ("D-T5-R 正向：min/max 站点也收兜底形参",
     "out double maxWidth,\n                                                     TextRunProperties paragraphDefault = null)"),
]

CS_DEFINE_BLOCK = """  <!-- WPF-on-Linux T1b/D3：让编进 PC 的 shim 走**直构分支**（internal 直接构造），
       而不是反射分支。已由 src/Linux/build/MilBridge/tests/DirectBranchCheck 单独编过（0 错 0 警）。 -->
  <PropertyGroup>
    <DefineConstants>$(DefineConstants);TEXTLINE_SHIM_DIRECT</DefineConstants>
  </PropertyGroup>
"""


def _count(haystack, needle):
    return haystack.count(needle)


def generate(check_only):
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    # ---- 锚点检查：每处必须**恰好出现一次**，不符即报错退出（不静默降级）----
    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？D3 补丁未应用（**不做任何静默降级**）。")
        print("        取锚点真值请用 `grep -an`（本仓库有文件含 NUL，不加 -a 只会得到 'Binary file matches'）。")
        return 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    # ---- 结构性断言（生成物自检）----
    for name, needle in REQUIRED_IN_OUTPUT:
        if needle is None:
            continue
        if needle not in out:
            print(f"[失败] 生成物缺少结构断言：{name}（{needle!r}）")
            return 1
    n_line = _count(out, "HbTextFallback.TryFormatLine(")
    n_minmax = _count(out, "HbTextFallback.TryMinMaxParagraphWidth(")
    if n_line != 1 or n_minmax != 1:
        print(f"[失败] 托管调用点计数不对：TryFormatLine={n_line}（要求 1）、"
              f"TryMinMaxParagraphWidth={n_minmax}（要求 1）")
        return 1
    print(f"[断言] 托管调用点 TryFormatLine=1、TryMinMaxParagraphWidth=1；两处原 LS 回退**都还在**")

    # ---- T1c/RTL 牙齿：**LineServices 站点数必须仍是 2，且都被两层托管兜底包住** ----
    #   将来若有人再加一处 `new TextMetrics.FullTextLine(`（= 又一条会 abort 的路），
    #   这里**立刻失败**，不必等某个 RTL/复杂文本块把进程打死才发现（"接不上就报错"的装置化）。
    ls_sites = _count(out, "new TextMetrics.FullTextLine(")
    if ls_sites != 2:
        print(f"[失败] 生成物里 `new TextMetrics.FullTextLine(` 出现 {ls_sites} 次（要求 2）"
              "—— 多出来那一处就是**没接兜底的 LS 站点**（Linux 上会 abort）")
        return 1
    # ---- T3 事故的**负断言**：旧写法（空段落 ⇒ return false）不许复活 ----
    for bad, why in ((chr(34) + "空段落" + chr(34) + "; return false;",
                      "空段落直接 return false（会把 RTL 交回 LS ⇒ abort）"),
                     ("if (text.Length == 0) {", "空文本的旧短路写法")):
        if bad in out:
            print(f"[失败] 生成物里出现**被禁用**的写法：{why} —— 那正是波 6 abort 的根因")
            return 1
    print("[断言] 负断言：空段落/空文本的旧短路写法**不存在** ✅")

    # ---- WAVE17 §1 P2（`D-T3`）的**负**断言：坏修法的两种指纹都必须不存在 ----
    #   ① 旧接线（`Pap.ParagraphIndent` 落进 `indentDip` 槽、`Pap.Indent` 从不被传）；
    #   ② 上游 `TryFormatLine` 的旧形参名 `paragraphIndent`（改名后若残留 ⇒ 半接线）。
    #   ⚠️ 这两条断言是"射程不许悄悄缩到零"的机器化：任何一次回退/半回退都会当场报错。
    for bad, why in (("indentDip: paragraphIndent", "回退成坏接线：PI 又落进 `indentDip`（网格锚）槽"),
                     ("paragraphIndent: settings.Pap.ParagraphIndent",
                      "回退成 v1 坏修法：`Pap.*` 是理想整数 ×300"),
                     ("indentDip: settings.Pap", "回退成 v1 坏修法：ideal 单位直传锚点槽（×300）"),
                     ("double paragraphIndent = 0", "旧形参名残留 ⇒ 半接线")):
        if bad in out:
            print(f"[失败] 生成物里出现**被禁用**的写法：{why}（`{bad}`）")
            return 1
    print("[断言] P2 负断言：`indentDip: paragraphIndent` / `Pap.*` 直传 / 旧形参名 **都不存在** ✅")

    # ---- P2 的**计数**牙齿：两个缩进槽各只有 1 处消费点，且取值来源**恰好 2 处**（两档各一）----
    #   ⚠️ **W19A 现场更正（这条牙齿抓到了我自己的改动，如实留档）**：
    #     `#17` 时来源只有**宽松档**一处 ⇒ 这条要求 `1/1`；`#19`/W19A 给**严格档**也接上
    #     `paragraphProperties.Indent` / `.ParagraphIndent` 之后**必然变成 2**。
    #     这不是牙齿坏了，正是它**按设计**把"接线站点数变了"这件事逼出来（`--check` 当场 `rc=1`）。
    #     ⇒ 口径从「来源唯一」改成「来源恰好 2 处 = 严格档 + 宽松档」，并**保留** `1/1` 的透传计数
    #     （`indentDip: indentDip` 只可能出现在宽松兜底类的转发里）。
    n_indent_slot = _count(out, "indentDip: indentDip")
    n_para_slot = _count(out, "paragraphIndentDip: paragraphIndentDip")
    n_src_indent = _count(out, "indentDip: paragraphProperties.Indent")
    n_src_para = _count(out, "paragraphIndentDip: paragraphProperties.ParagraphIndent")
    if (n_indent_slot, n_para_slot, n_src_indent, n_src_para) != (1, 1, 2, 2):
        print(f"[失败] P2/W19-B 接线计数不对：透传 indent/para={n_indent_slot}/{n_para_slot}（要求 1/1）、"
              f"来源 indent/para={n_src_indent}/{n_src_para}（要求 2/2 = 严格档站点1 + 宽松兜底站点1）")
        return 1
    print(f"[断言] P2/W19-B 接线：透传 indent={n_indent_slot} para={n_para_slot}；"
          f"来源（原始 DIP）= {n_src_indent}/{n_src_para}（严格档 + 宽松档）✅")

    # ---- WAVE23 §2 P2（`D-T6-b`）的**计数**牙齿：段落原点在生成物里**恰好 1 处**、且必须带 `cpFirst` ----
    #   ⚠️ 这条牙齿抓的正是 `#17` 那族事故："**登记了但没生效**"（needle 写错 ⇒ 注入静默不发生 ⇒
    #     所有绿判据照绿）。原点若丢了，宽松档的帧会**静默**退回恒 0。
    n_para_origin = _count(out, "paragraphOrigin: cpFirst")
    if n_para_origin != 1:
        print(f"[失败] W23-B 段落原点接线计数不对：`paragraphOrigin: cpFirst` 出现 {n_para_origin} 次"
              "（要求 1 = 宽松兜底站点1 那一处）")
        return 1
    print(f"[断言] W23-B 段落原点：宽松兜底透传 `paragraphOrigin: cpFirst` = {n_para_origin}（要求 1）✅")

    # ---- WAVE19 §0.1 B 的**负**断言 + 计数牙齿：严格档的缩进槽不许退回"没有 indent"的形态 ----
    #   修前形态（生成物 `56a5b4a5be1c6bcc:565-572`）= 站点1 的调用在
    #       `settings.Pap.LineHeight      // 0 = 未设 ⇒ 托管侧用字体自然行高（真机口径）` 处收尾。
    #
    #   ⚠️ **本牙齿的第一版是我自己的错，留档（这是纪律 3/25 要求的形状：牙齿必须能真红）**：
    #      v1 我写的负断言是「`settings.Pap.LineHeight\n … ) as TextLine;` 必须 0 处」——
    #         但**修前形态的 `LineHeight` 后面跟着行尾注释**（不是裸换行）⇒ 该串**恒不出现**
    #         ⇒ 这条牙齿**永远绿** = 假牙齿。
    #      v2 我把"缩进实参对"写成不带站点锚的三行并断言**恰好 1 次** ——
    #         但**宽松档那一段的结尾逐字相同**（`REPLACEMENT_4` 同一缩进、同一 `) as TextLine;`）
    #         ⇒ 实测计数 = 2 ⇒ 牙齿当场报红。
    #      v3（本版）把二者合并成**一个带站点1 专属锚点的整段**：
    #         `settings.Pap.LineHeight,` 这一行（**带逗号 + 行尾"真机口径"注释**）**站点1 独有**
    #         （站点2 的 `LineHeight` 在宽松兜底那一句里、后面没有这句注释）⇒ 计数才是可判的。
    #
    #   三条**真**牙齿（每条都当场红过一次）：
    #     ① 站点1 的**修前收尾形态**必须 0 处；
    #     ② 站点1 的**无逗号形态**必须 0 处（半接线：加了注释没加实参）；
    #     ③ 计数：站点1 专属整段（收尾行 + 注释）**恰好 1 处**；
    #        `indentDip: paragraphProperties.Indent,` / `paragraphIndentDip: paragraphProperties.ParagraphIndent`
    #        各**恰好 2 处**（严格档 + 宽松档）；
    #        `settings.Pap.LineHeight` 的**逗号形态**恰好 **1 处**（只有站点1 的宽松兜底那一句；
    #        站点2 的 min/max 兜底**根本不传 `LineHeight`** —— 现场复核见报告 §3）。
    #        ⚠️ v4 更正：v3 我写的是"要求 2"，实测 **1** ⇒ 牙齿再次报红。**再留一次档**：
    #           这条数字是我**从"两个站点"推断**出来的，不是数出来的；`settings.Pap.LineHeight`
    #           在生成物里**只有 2 处**（修前 `:571` 严格档 + `:587` 宽松档），min/max 站点**0 处**。
    _LH_LINE = ("                    settings.Pap.LineHeight,"
                "     // 0 = 未设 ⇒ 托管侧用字体自然行高（真机口径）")
    _LH_LINE_OLD = ("                    settings.Pap.LineHeight"
                    "      // 0 = 未设 ⇒ 托管侧用字体自然行高（真机口径）")
    #   ⚠️ `#25` W25A 现场更正（第一版我改错了，**留档**）：我原以为"加兜底实参后旧收尾串不再匹配"，
    #      于是把 `_INDENT_ARGS` 的尾巴换成了「兜底实参 + `) as TextLine;`」—— 结果 `--check` 报
    #      `缩进实参对收尾 = 1（要求 2）`。**真因**：**严格档** REPL_1 那个 `HbTextFallback.TryFormatLine`
    #      调用点**也**以 `) as TextLine;` 收尾、且**没有**兜底实参（shim 的形参表里没有它，
    #      而本波**不许动 shim**）⇒ 收紧尾巴会把**严格档站点**从计数里挤出去。
    #      ⇒ **恢复原尾巴**（`paragraphIndentDip: …\n<缩进>) as TextLine;`）：它恰好**只**匹配
    #      宽松兜底类的**两个**站点（站点1 + 站点2），严格档 REPL_1 因缺第 4 行而**天然不被计入**。
    #      计数仍为 **2**，与 `#19` 以来逐字相同 ⇒ **本条牙齿的射程一个字节都没动**。
    _INDENT_ARGS = "// @@D-T5R-SITE@@ 段落默认 run properties 兜底"
    _STRICT_BLOCK = (_LH_LINE + "\n"
                     "                    // ── WAVE19 §0.1 B（`P4` 的同波兄弟件）：**严格档也要收缩进** ──\n")
    if (_LH_LINE_OLD + "\n" + "                    ) as TextLine;") in out:
        print("[失败] W19-B 负断言①：站点1 又退回**不收缩进**的修前形态"
              "（`settings.Pap.LineHeight`（无逗号 + 行尾注释）后直接 `) as TextLine;`）"
              "—— 严格档的 indent 缺口被退回")
        return 1
    if _LH_LINE_OLD in out:
        print("[失败] W19-B 负断言②：站点1 的收尾行还是**无逗号**的修前形态"
              "（`settings.Pap.LineHeight      // …`）⇒ 缩进实参没接上（半接线）")
        return 1
    n_block = _count(out, _STRICT_BLOCK)
    n_indent = _count(out, "indentDip: paragraphProperties.Indent,")
    n_para = _count(out, "paragraphIndentDip: paragraphProperties.ParagraphIndent")
    n_args = _count(out, _INDENT_ARGS)
    n_lh_comma = _count(out, _LH_LINE)
    if (n_block, n_indent, n_para, n_args, n_lh_comma) != (1, 2, 2, 2, 1):
        print(f"[失败] W19-B 计数牙齿③：站点1 专属整段 = {n_block}（要求 1）、"
              f"`indentDip: paragraphProperties.Indent,` = {n_indent}（要求 2）、"
              f"`paragraphIndentDip: paragraphProperties.ParagraphIndent` = {n_para}（要求 2）、"
              f"缩进实参对收尾 = {n_args}（要求 2）、`LineHeight,`（逗号形态）= {n_lh_comma}（要求 1）"
              "—— 严格档的接线被删/被改写，或**多出**了一个未被本脚本管理的调用点")
        return 1
    print(f"[断言] W19-B③：站点1 专属整段 = {n_block}（要求 1）；"
          f"两档缩进来源 indent/para = {n_indent}/{n_para}（要求 2/2 = 严格档 + 宽松档）；"
          f"`LineHeight,` = {n_lh_comma}（要求 1）✅")
    print("[断言] W19-B①②：站点1 的**修前收尾形态 0 处**、**无逗号形态 0 处** ✅")

    # ---- WAVE25 §2（`D-T5-R`）的**顺序** + **计数**牙齿 ----
    #   【为什么必须是"顺序"】本件与 `D-T5` 主路同族：光有兜底语句、却把它放在两个
    #   `props = run.Properties` **之前**，会**静默打断** run 自身属性的优先级
    #   （`hidden1`/`hiddenmid`/`eos1` 与一切多 run 段落全都会改用段落默认属性）——
    #   那是**真回归**，而 `hiddenonly` 照样变绿 ⇒ 只看"绿不绿"抓不到它。⇒ 用下标把它钉死。
    _FALLBACK = "if (props == null && paragraphDefault != null)"
    _TOPTS = "text = sb.ToString();"
    _LOOP = "for (int guard = 0; guard < 4096; ++guard)"
    i_fb = out.find(_FALLBACK)
    i_ts = out.find(_TOPTS)
    i_lp = out.find(_LOOP)
    if i_fb < 0 or i_ts < 0 or i_lp < 0 or not (i_lp < i_ts < i_fb):
        print(f"[失败] D-T5-R 顺序牙齿：兜底（下标 {i_fb}）**没有**落在 `text = sb.ToString();`（下标 {i_ts}）"
              f"之后、收集循环（下标 {i_lp}）之后"
              "—— 兜底若跑在收集循环之前，`props` 一开始就非 null ⇒ **run 自己的属性再也不会被取**"
              "（`hidden1`/`hiddenmid` 一族的 props 优先级会被静默改写 = 真回归）")
        return 1
    print(f"[断言] D-T5-R 顺序：收集循环 @{i_lp} < `text = sb.ToString();` @{i_ts}"
          f" < 兜底 @{i_fb}（兜底在**采集之后**、两个失败点**之前**）✅")

    #   【计数牙齿 = `#17` "注册了但没生效"那一族的唯一防线】
    #     `paragraphDefault` **带默认值** ⇒ "只加形参、调用点不传"**编译得过**、且**行为与修前逐位相同**
    #     ⇒ 那是**半接线假修**，必须被计数抓住。两个真实站点（宽松档 `TryFormatLine` +
    #     `TryMinMaxParagraphWidth`）**缺一不可**（`D-T2` 的教训：两个站点必须同一把尺子）。
    n_default_forward = _count(out, "paragraphDefault: paragraphDefault)")
    n_default_src = _count(out, "paragraphDefault: paragraphProperties.DefaultTextRunProperties")
    n_fallback = _count(out, _FALLBACK)
    #   ⚠️ 口径（现场数出来的，不是推出来的）：`paragraphDefault: paragraphDefault)` **要求 2** ——
    #      它出现在**两个转发点**（`TryFormatLine` → `CollectLenient`、`TryMinMaxParagraphWidth` →
    #      `CollectLenient`）；`paragraphDefault: paragraphProperties.DefaultTextRunProperties` 同样是 2
    #      （两个调用点各一处）。（第一版我写成 1，`--check` 当场报红 ⇒ 如实登记在报告里。）
    if (n_default_forward, n_default_src, n_fallback) != (2, 2, 1):
        print(f"[失败] D-T5-R 计数牙齿：兜底透传 `paragraphDefault: paragraphDefault)` = {n_default_forward}"
              f"（要求 2 = 两个转发点）、来源 `paragraphDefault: paragraphProperties.DefaultTextRunProperties` = {n_default_src}"
              f"（要求 2 = 宽松档站点1 + min/max 站点2）、兜底语句 = {n_fallback}（要求 1）"
              "—— **半接线**（只加形参、调用点不传）会让 `paragraphDefault` 恒 null，"
              "行为与修前**逐位相同**却看不出来")
        return 1
    print(f"[断言] D-T5-R 接线：兜底透传 = {n_default_forward}（要求 2 = 两个转发点）；"
          f"来源 = {n_default_src}（要求 2 = 两个站点）；兜底语句 = {n_fallback}（要求 1）✅")

    #   【负断言：半接线的**旧形态**不许残留】两个调用点的旧 5 实参形态逐字不许再出现。
    for bad, why in (("out text, out props, out modOpen, out modClose)) { ++s_failed; return null; }",
                      "宽松档站点1 退回**不传兜底**的旧形态（半接线）"),
                     ("out text, out props, out modOpen2, out modClose2)) { ++s_failed; return false; }",
                      "min/max 站点2 退回**不传兜底**的旧形态（半接线）")):
        if bad in out:
            print(f"[失败] D-T5-R 负断言：{why}（`{bad}`）")
            return 1
    print("[断言] D-T5-R 负断言：两个站点的旧 5 实参形态 **0 处** ✅")

    # ---- WAVE17 §1 P3（`D-T2` 真身）的**负**断言：min 探针退回"无作用域"必须当场报错 ----
    #   修前形态 = min 探针的调用在 `out c2);` 处结束（一个 modifier 实参都不传）
    #   ⇒ 那正是 `min > max` 契约违反的根因（W17D 实测 `min=22.652344 max=0.000000`）。
    #   本仓该串**只可能**出现在这一处（实测：应用器 1 次、生成物 1 次、上游 0 次）。
    if "out c2);" in out:
        print("[失败] P3 负断言：min 探针又退回**不带** modifier 作用域实参的形态（`out c2);`）"
              "—— 那正是 `min > max` 契约违反的根因")
        return 1
    print("[断言] P3 负断言：min 探针的旧形态（`out c2);`）**不存在** ✅")

    # ---- P3 的**计数**牙齿：modifier 作用域实参必须**恰好 2 处**（max 探针 + min 探针）----
    #   "参数对等"这件事必须是**数出来的**，不是"看起来像"：少一处 = 退回不对称，多一处 = 多了一个站点。
    #   ⚠️ WAVE32 §1 W32A（`D-T2-c`）把这条 needle 的**尾巴加长**为同时带 `modifierScopeEnd`：
    #      射程**只增不减**（它现在同时钉住"min 与 max 用的是**同一组三个**实参"）。
    #   ⚠️ ★T-A30：两处实参表**尾**新增了 `eopRun: eolRun2`（见下面 `T-A30 站点计数牙齿`）⇒ 本 needle
    #      的收尾从 `…modClose2);` 改为 `…modClose2,`（**射程一字未减**：仍是"同一组三个实参"）。
    n_mod_args = _count(out, "modifierOpenIndex: modOpen2, modifierScopeEnd: modScopeEnd2, modifierCloseIndex: modClose2,")
    if n_mod_args != 2:
        print(f"[失败] P3 计数牙齿：modifier 作用域实参出现 {n_mod_args} 处"
              "（要求 2 = max 探针 + min 探针）")
        return 1
    print(f"[断言] P3 参数对等：modifier 作用域实参 {n_mod_args} 处（max + min）✅")

    # ---- ★T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）的**计数 + 顺序**牙齿 ----
    #   【为什么必须数】`CollectLenient` 的 `out TextRun eolRun` 与 `FormatParagraph` 的 `eopRun:` 都是
    #   **尾随可选** ⇒ "形参加了、站点不传"**编译得过**、行为与修前**逐位相同**（段末行照旧报
    #   `fsflrOutOfSpace` ⇒ 驱动越界探到下一个 Block ⇒ `FailFast`）⇒ 只能靠站点数把"接全了"钉死。
    #   ⚠️ 三个 `FormatParagraph` 站点（宽松档 + max + min）与本文件的既有口径同源。
    #   ⚠️ needle **不跨行**（形参/实参在生成物里是分行的）⇒ 分别钉"出口"与"实参"两半。
    n_eop_def = _count(out, "out TextRun eolRun,")
    n_eop_out1 = _count(out, "out eolRun,")
    n_eop_out23 = _count(out, "out eolRun2,")
    n_eop_arg1 = _count(out, "eopRun: eolRun);")
    n_eop_arg23 = _count(out, "eopRun: eolRun2);")
    n_eop_cap = _count(out, "eolRun = (run is TextEndOfParagraph) ? run : null;")
    n_eop_uses = _count(out, "eopRun:")
    if (n_eop_def, n_eop_out1, n_eop_out23, n_eop_arg1, n_eop_arg23, n_eop_cap) != (1, 1, 1, 1, 2, 1):
        print(f"[失败] T-A30 计数牙齿：形参定义 {n_eop_def}（要求 1）、站点1 出口 {n_eop_out1}（要求 1）、"
              f"min/max 出口 {n_eop_out23}（要求 1）、站点1 实参 {n_eop_arg1}（要求 1）、"
              f"min/max 实参 {n_eop_arg23}（要求 2）、段末取值语句 {n_eop_cap}（要求 1）"
              "—— 漏传 = 段末行照旧报 `fsflrOutOfSpace` ⇒ 驱动越界 ⇒ `Invariant.FailFast`（`LineBase.cs:137`）")
        return 1
    if n_eop_uses != 3:
        print(f"[失败] T-A30 计数牙齿：`eopRun:` 合计 {n_eop_uses}（要求 3 = 工厂站点数）")
        return 1
    print(f"[断言] T-A30 段末 EOL run 接线：形参 {n_eop_def}、出口 {n_eop_out1}+{n_eop_out23}、"
          f"工厂站点 {n_eop_arg1}+{n_eop_arg23}（共 {n_eop_uses}）、段末取值语句 {n_eop_cap} ✅")

    #   【负断言】"取值语句落在收集循环**之后**"是**顺序**要求：段末取值必须在 `break` 之前、且在
    #     循环体内（否则收集层根本拿不到段末那个对象）。
    i_eop_cap = out.find("eolRun = (run is TextEndOfParagraph) ? run : null;")
    i_eop_brk = out.find("break;                            // EOL / EOP（它的字符不属于段落正文）")
    if i_eop_cap < 0 or i_eop_brk < 0 or not (i_eop_cap < i_eop_brk):
        print(f"[失败] T-A30 顺序牙齿：段末取值（下标 {i_eop_cap}）**没有**落在 `break;`（下标 {i_eop_brk}）之前"
              "—— 那样 `eolRun` 恒 null，段末行照旧报 `fsflrOutOfSpace`（半接线）")
        return 1
    print(f"[断言] T-A30 顺序：段末取值 @{i_eop_cap} < `break;` @{i_eop_brk} ✅")

    # ---- WAVE32 §1 W32A（`D-T2-c`）的**站点计数**牙齿：`modifierScopeEnd` 必须**恰好传 3 处** ----
    #   【为什么必须数】`n_default_src` 那条只钉**兜底形参**，与"覆盖终点"无关；而本件新增的形参
    #   同样是**尾随可选** ⇒ 漏一个站点**编译得过**、行为却退回修前（该路径零宽照旧"到段末"）
    #   而**其它路径会绿** ⇒ 只能靠"站点数"把"接全了"钉死（生成物 `:167-169` 自己就记着这个教训）。
    n_scope_def = _count(out, "out int modifierOpenIndex, out int modifierScopeEnd, out int modifierCloseIndex,")
    n_scope_out1 = _count(out, "out modOpen, out modScopeEnd, out modClose,")
    n_scope_out23 = _count(out, "out modOpen2, out modScopeEnd2, out modClose2,")
    n_scope_arg1 = _count(out, "modifierScopeEnd: modScopeEnd,")
    n_scope_arg23 = _count(out, "modifierScopeEnd: modScopeEnd2,")
    n_scope_named = _count(out, "modifierScopeEnd:")
    n_scope_sites = n_scope_arg1 + n_scope_arg23
    n_scope_calls = n_scope_out1 + n_scope_out23
    n_scope_run = _count(out, "modifierScopeEnd = (cp - cpFirst) + run.Length;")
    n_scope_ovr = _count(out, "if (modifierCloseIndex >= 0) { modifierScopeEnd = modifierCloseIndex; }")
    #   ⚠️ 两套计数**口径不同、都不许省**（现场数出来的，不是推出来的）：
    #     · **`CollectLenient` 调用点 = 2**（宽松档站点1 + `TryMinMaxParagraphWidth` 的**一处**收集）
    #       —— 与上面 `n_default_forward == 2` 同源；
    #     · **`FormatParagraph` 传参站点 = 3**（宽松档 1 + max 探针 1 + min 探针 1）
    #       —— 这才是本件说的"一个都不许漏"的那三个站点。
    if ((n_scope_def, n_scope_out1, n_scope_out23, n_scope_arg1, n_scope_arg23) != (1, 1, 1, 1, 2)
            or n_scope_named != 3 or n_scope_sites != 3 or n_scope_calls != 2
            or n_scope_run != 1 or n_scope_ovr != 1):
        print(f"[失败] W32A 站点计数牙齿：形参定义 {n_scope_def}（要求 1）、站点1 出口 {n_scope_out1}（要求 1）、"
              f"min/max 出口 {n_scope_out23}（要求 1 = `TryMinMaxParagraphWidth` 里**只有一处**收集）、"
              f"站点1 实参 {n_scope_arg1}（要求 1）、min/max 实参 {n_scope_arg23}（要求 2 = max + min）、"
              f"`modifierScopeEnd:` 合计 {n_scope_named}（要求 3 = 工厂站点数）、"
              f"传参站点合计 {n_scope_sites}（要求 3）、`CollectLenient` 调用点 {n_scope_calls}（要求 2）、"
              f"终点取值语句 {n_scope_run}（要求 1）、EOS 覆盖行 {n_scope_ovr}（要求 1）"
              "—— **漏传一个站点 = 那条路径的零宽跨度照旧「到段末」**（修前的错），而其它路径会绿"
              " ⇒ 半接线静默通过")
        return 1
    print(f"[断言] W32A 覆盖终点接线：形参 {n_scope_def}、`CollectLenient` 调用点 {n_scope_calls}/2"
          f"（宽松档 {n_scope_out1} + min/max {n_scope_out23}）、工厂传参站点 {n_scope_sites}/3"
          f"（宽松档 {n_scope_arg1} + max/min {n_scope_arg23}）、"
          f"取值语句 {n_scope_run}、EOS 覆盖行 {n_scope_ovr} ✅")

    #   【WAVE32 §1 W32A 的**负**断言：三个站点"**不传覆盖终点**"的旧形态逐字不许残留】
    for bad, why in (("out text, out props, out modOpen, out modClose,",
                      "宽松档站点1 退回**不收**覆盖终点（`out modClose,` 收尾）"),
                     ("out text, out props, out modOpen2, out modClose2,",
                      "min/max 站点2 退回**不收**覆盖终点"),
                     ("modifierOpenIndex: modOpen, modifierCloseIndex: modClose,",
                      "宽松档站点1 退回**不传**覆盖终点"),
                     ("modifierOpenIndex: modOpen2, modifierCloseIndex: modClose2)",
                      "min/max 两探针退回**不传**覆盖终点")):
        if bad in out:
            print(f"[失败] W32A 负断言：{why}（`{bad}`）")
            return 1
    print("[断言] W32A 负断言：三个站点的「不传覆盖终点」旧形态 **0 处** ✅")

    #   【WAVE32 §1 W32A 的**顺序**牙齿】EOS 覆盖行必须在收集循环**之后**（按**下标**钉死）：
    #     放进循环里会被来源 ① 逐 run 覆盖回去 ⇒ 静默退回 b34 口径（"接上了"却"没生效"那一族）。
    i_scope_rec = out.find("modifierScopeEnd = (cp - cpFirst) + run.Length;")
    i_scope_ovr = out.find("if (modifierCloseIndex >= 0) { modifierScopeEnd = modifierCloseIndex; }")
    i_scope_txt = out.find("text = sb.ToString();")
    if (i_scope_rec < 0 or i_scope_ovr < 0 or i_scope_txt < 0
            or not (i_scope_rec < i_scope_ovr < i_scope_txt)):
        print(f"[失败] W32A 顺序牙齿：终点取值 @{i_scope_rec}、EOS 覆盖行 @{i_scope_ovr}、"
              f"`text = sb.ToString();` @{i_scope_txt}"
              " —— 要求 `取值 < 覆盖 < 收集收尾`：覆盖行若落在收集循环**里面**，"
              "每个后续 `TextModifier` run 都会把它覆盖回来源 ① ⇒ `TextEndOfSegment` 的终点被静默丢弃")
        return 1
    print(f"[断言] W32A 顺序：终点取值 @{i_scope_rec} < EOS 覆盖 @{i_scope_ovr}"
          f" < `text = sb.ToString();` @{i_scope_txt} ✅")

    for need in ("WpfLinuxLenientTextFallback.TryFormatLine(",
                 "WpfLinuxLenientTextFallback.TryMinMaxParagraphWidth("):
        if _count(out, need) != 1:
            print(f"[失败] 宽松兜底调用 `{need}` 出现 {_count(out, need)} 次（要求 1）")
            return 1
    print(f"[断言] LS 站点数 = {ls_sites}（两处都被 ①shim ②宽松兜底 包住）；宽松兜底调用各 1 处")

    # ---- WAVE24 §1 P1（`D-T5`）的**顺序**牙齿：分类必须落在 deref CBR **之前** ----
    #   【为什么必须是"顺序"而不是"存在"】needle 只能证明"这两串在文件里"；
    #   而 `D-T5` 的根因恰恰是**顺序**（先 `cbr.CharacterBuffer` ⇒ 隐形 run 的空 CBR 必然 null ⇒ abort）。
    #   光有 `IsTextRunType` 这段代码、却把它放在 deref **之后**，判据会全绿而缺陷照旧
    #   ⇒ 这正是 `#17` "注册了但没生效"那一族。⇒ 用**下标比较**把它钉死。
    i_guard = out.find("if (!IsTextRunType(run))")
    i_deref = out.find("CharacterBufferReference cbr = run.CharacterBufferReference;")
    if i_guard < 0 or i_deref < 0 or not (i_guard < i_deref):
        print(f"[失败] D-T5 顺序牙齿：分类 guard（下标 {i_guard}）**没有**落在 CBR deref（下标 {i_deref}）之前"
              "—— 隐形 run 会重新撞上 `buf == null ⇒ return null ⇒ 交回 LS ⇒ abort(134)`")
        return 1
    print(f"[断言] D-T5 顺序：分类 guard @{i_guard} < CBR deref @{i_deref}（隐形 run 在 deref 之前被拦下）✅")

    # ---- 机械证据：没有新增/删除任何 throw（"接线"不等于"搬异常"）----
    up_throws = _count(text, "throw ")
    out_throws = _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}"
              "（D3 只允许接线，不允许新增/搬运异常）")
        return 1
    print(f"[断言] `throw` 条数 上游 {up_throws} == 生成物 {out_throws}")

    # ---- 大括号平衡（插入块的语法自检；真正的编译验证在集成波里做）----
    if out.count("{") != out.count("}"):
        print(f"[失败] 生成物大括号不平衡：{{={out.count('{')} }}={out.count('}')}")
        return 1
    print(f"[断言] 大括号平衡 {{={out.count('{')} }}={out.count('}')}；"
          f"上游 {len(text.splitlines())} 行 → 生成物 {len(out.splitlines())} 行"
          f"（+{len(out.splitlines()) - len(text.splitlines())} 行，全是判断与注释）")

    output = HEADER + out

    up_to_date = False
    if os.path.exists(GENERATED):
        with open(GENERATED, encoding="utf-8") as f:
            up_to_date = (f.read() == output)

    if check_only:
        print(f"[检查] {os.path.relpath(GENERATED, ROOT)}："
              + ("内容已是最新" if up_to_date else "缺失/与上游不同步（需要重新生成）"))
    elif up_to_date:
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：内容已是最新（未重写）")
    else:
        # ★T-A30：**生成件用 `temp+rename` 重产**（同目录 `os.replace` 是原子的）—— 就地截断写会在
        #   写入中途失败时留下**半件**（而"半件"看上去与"没生成过"一样，正是本仓 `D-G56` 那一族的来源形态）。
        _tmp = GENERATED + ".tmp-tA30"
        with open(_tmp, "w", encoding="utf-8") as f:
            f.write(output)
        os.replace(_tmp, GENERATED)
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：已从上游重生成（2 处 D3 修改；temp+rename）")

    # ---- csproj 接线 ----
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        wired = (f'<Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/' + os.path.basename(GENERATED) + '" />') in csproj
        has_define = "<DefineConstants>$(DefineConstants);TEXTLINE_SHIM_DIRECT</DefineConstants>" in csproj
        print(f"[接线] csproj 已就位（幂等，不改）；生成物 Include={wired}；TEXTLINE_SHIM_DIRECT={has_define}")
        ok = (not check_only or (up_to_date and wired and has_define))
        if not (wired and has_define):
            print("[失败] MARKER 在但接线不完整 —— 请人工检查 csproj（**不自动半接线**）")
            return 1
        return 0 if ok else 1

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1

    # ⚠️ 顺序：Remove 必须落在上游那条 Include **之后**才生效（与补丁 J 同一个坑，见其注释 §接线坑②）
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             f'    <Compile Remove="$(UpstreamWpfRoot){UPSTREAM_REL}" />\n'
             f'    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/{os.path.basename(GENERATED)}" />\n'
             "  </ItemGroup>\n"
             + CS_DEFINE_BLOCK
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入到 {os.path.relpath(CSPROJ, ROOT)}："
          f"Remove 上游 TextFormatterImp.cs + Include {os.path.basename(GENERATED)} + "
          "DefineConstants 加 TEXTLINE_SHIM_DIRECT")
    print("\n下一步（**不要在这里重建 PC**，留给集成波）：")
    print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\")
    print("      -getItem:Compile -p:Configuration=Debug | grep -i TextFormatterImp")
    print("  # 期望只剩 TextFormatterImp.Linux.cs（上游那条被 Remove 掉）")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--apply", action="store_true", help="显式表示要写盘（默认行为，等价）")
    args = ap.parse_args()
    print("=== T1b/D3 · PresentationCore TextFormatterImp 回退接线 ===")
    rc = generate(check_only=args.check)
    print("=== 退出码", rc, "===")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
