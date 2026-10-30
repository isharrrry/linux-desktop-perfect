// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textline-fallback.py **生成**，不要手改。
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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using MS.Utility;
using MS.Internal.Shaping;
using MS.Internal.Text.TextInterface;
using MS.Internal.FontCache;

namespace MS.Internal.TextFormatting
{
    /// <summary>
    /// Implementation of TextFormatter
    /// </summary>
    /// <summary>
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
    {
        private FrugalStructList<TextFormatterContext>  _contextList;               // LS context free list
        private bool                                    _multipleContextProhibited; // prohibit multiple contexts within the same formatter
        private GlyphingCache                           _glyphingCache;             // Glyphing cache for font linking process
        private TextFormattingMode                      _textFormattingMode;
        private TextAnalyzer                            _textAnalyzer;              // TextAnalyzer used for shaping process

        private const int MaxGlyphingCacheCapacity = 16;

        /// <summary>
        /// Construct an instance of TextFormatter implementation
        /// </summary>
        internal TextFormatterImp(TextFormattingMode textFormattingMode)
            : this(null, textFormattingMode)
        { }

        /// <summary>
        /// Construct an instance of TextFormatter implementation
        /// </summary>
        internal TextFormatterImp() : this(null, TextFormattingMode.Ideal)
        {}

        /// <summary>
        /// Construct an instance of TextFormatter implementation with the specified context
        /// </summary>
        /// <param name="soleContext"></param>
        /// <remarks>
        /// TextFormatter created via this special ctor takes a specified context and uses it as the only known
        /// context within its entire lifetime. It prohibits reentering of TextFormatter during formatting as only
        /// one context is allowed. This restriction is critical to the optimal break algorithm supported by the current
        /// version of PTLS.
        /// </remarks>
        internal TextFormatterImp(TextFormatterContext soleContext, TextFormattingMode textFormattingMode)
        {
            _textFormattingMode = textFormattingMode;

            if (soleContext != null)
                _contextList.Add(soleContext);

            _multipleContextProhibited = (_contextList.Count != 0);
        }


        /// <summary>
        /// Finalizing text formatter
        /// </summary>
        ~TextFormatterImp()
        {
            CleanupInternal();
        }


        /// <summary>
        /// Release all unmanaged LS contexts
        /// </summary>
        public override void Dispose()
        {
            CleanupInternal();
            base.Dispose();
            GC.SuppressFinalize(this);
        }


        /// <summary>
        /// Release all unmanaged LS contexts
        /// </summary>
        private void CleanupInternal()
        {
            for (int i = 0; i < _contextList.Count; i++)
            {
                _contextList[i].Destroy();
            }
            _contextList.Clear();
        }


        /// <summary>
        /// Client to format a text line that fills a paragraph in the document.
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="paragraphWidth">width of paragraph in which the line fills</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <param name="previousLineBreak">LineBreak property of the previous text line, or null if this is the first line in the paragraph</param>
        /// <returns>object representing a line of text that client interacts with. </returns>
        public override TextLine FormatLine(
            TextSource                  textSource,
            int                         firstCharIndex,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak
            )
        {
            return FormatLineInternal(
                textSource,
                firstCharIndex,
                0,   // lineLength
                paragraphWidth,
                paragraphProperties,
                previousLineBreak,
                new TextRunCache()  // local cache, only live within this call
                );
        }



        /// <summary>
        /// Client to format a text line that fills a paragraph in the document.
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="paragraphWidth">width of paragraph in which the line fills</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <param name="previousLineBreak">LineBreak property of the previous text line, or null if this is the first line in the paragraph</param>
        /// <param name="textRunCache">an object representing content cache of the client.</param>
        /// <returns>object representing a line of text that client interacts with. </returns>
        public override TextLine FormatLine(
            TextSource                  textSource,
            int                         firstCharIndex,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak,
            TextRunCache                textRunCache
            )
        {
            return FormatLineInternal(
                textSource,
                firstCharIndex,
                0,   // lineLength
                paragraphWidth,
                paragraphProperties,
                previousLineBreak,
                textRunCache
                );
        }



        /// <summary>
        /// Client to reconstruct a previously formatted text line
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="lineLength">character length of the line</param>
        /// <param name="paragraphWidth">width of paragraph in which the line fills</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <param name="previousLineBreak">LineBreak property of the previous text line, or null if this is the first line in the paragraph</param>
        /// <param name="textRunCache">an object representing content cache of the client.</param>
        /// <returns>object representing a line of text that client interacts with. </returns>
#if OPTIMALBREAK_API
        public override TextLine RecreateLine(
#else
        internal override TextLine RecreateLine(
#endif
            TextSource                  textSource,
            int                         firstCharIndex,
            int                         lineLength,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak,
            TextRunCache                textRunCache
            )
        {
            return FormatLineInternal(
                textSource,
                firstCharIndex,
                lineLength,
                paragraphWidth,
                paragraphProperties,
                previousLineBreak,
                textRunCache
                );
        }



        /// <summary>
        /// Format and produce a text line either with or without previously known
        /// line break point.
        /// </summary>
        private TextLine FormatLineInternal(
            TextSource                  textSource,
            int                         firstCharIndex,
            int                         lineLength,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak,
            TextRunCache                textRunCache
            )
        {
            EventTrace.EasyTraceEvent(EventTrace.Keyword.KeywordText, EventTrace.Level.Verbose, EventTrace.Event.WClientStringBegin, "TextFormatterImp.FormatLineInternal Start");

            // prepare formatting settings
            FormatSettings settings = PrepareFormatSettings(
                textSource,
                firstCharIndex,
                paragraphWidth,
                paragraphProperties,
                previousLineBreak,
                textRunCache,
                (lineLength != 0),  // Do optimal break if break is given
                true,    // isSingleLineFormatting
                _textFormattingMode
                );

            TextLine textLine = null;

            if (    !settings.Pap.AlwaysCollapsible
                &&  previousLineBreak == null
                &&  lineLength <= 0
                )
            {
                // simple text line.
                textLine = SimpleTextLine.Create(
                    settings,
                    firstCharIndex,
                    RealToIdealFloor(paragraphWidth),
                    textSource.PixelsPerDip
                    ) as TextLine;
            }

            if (textLine == null)
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
                textLine = new TextMetrics.FullTextLine(
                    settings,
                    firstCharIndex,
                    lineLength,
                    RealToIdealFloor(paragraphWidth),
                    LineFlags.None
                    ) as TextLine;
            }

            EventTrace.EasyTraceEvent(EventTrace.Keyword.KeywordText, EventTrace.Level.Verbose, EventTrace.Event.WClientStringEnd, "TextFormatterImp.FormatLineInternal End");

            return textLine;
        }



        /// <summary>
        /// Client to ask for the possible smallest and largest paragraph width that can fully contain the passing text content
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <returns>min max paragraph width</returns>
        public override MinMaxParagraphWidth FormatMinMaxParagraphWidth(
            TextSource                  textSource,
            int                         firstCharIndex,
            TextParagraphProperties     paragraphProperties
            )
        {
            return FormatMinMaxParagraphWidth(
                textSource,
                firstCharIndex,
                paragraphProperties,
                new TextRunCache()  // local cache, only live within this call
                );
        }



        /// <summary>
        /// Client to ask for the possible smallest and largest paragraph width that can fully contain the passing text content
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <param name="textRunCache">an object representing content cache of the client.</param>
        /// <returns>min max paragraph width</returns>
        public override MinMaxParagraphWidth FormatMinMaxParagraphWidth(
            TextSource                  textSource,
            int                         firstCharIndex,
            TextParagraphProperties     paragraphProperties,
            TextRunCache                textRunCache
            )
        {
            // prepare formatting settings
            FormatSettings settings = PrepareFormatSettings(
                textSource,
                firstCharIndex,
                0,      // infinite paragraphWidth
                paragraphProperties,
                null,   // always format the whole paragraph - no previousLineBreak
                textRunCache,
                false,  // optimalBreak
                true,   // isSingleLineFormatting
                _textFormattingMode
                );

            // WPF-on-Linux（T1b/D3）：先试**托管路径**（宽=∞ 取最长行 ⇒ MaxWidth；宽=0 强制断 ⇒ MinWidth）。
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

            // ── T1c/RTL：宽松托管兜底（min/max 版；同站点 1 的理由）──
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
        }

        internal TextFormattingMode TextFormattingMode
        {
            get
            {
                return _textFormattingMode;
            }
        }

        /// <summary>
        /// Client to cache information about a paragraph to be used during optimal paragraph line formatting
        /// </summary>
        /// <param name="textSource">an object representing text layout clients text source for TextFormatter.</param>
        /// <param name="firstCharIndex">character index to specify where in the source text the line starts</param>
        /// <param name="paragraphWidth">width of paragraph in which the line fills</param>
        /// <param name="paragraphProperties">properties that can change from one paragraph to the next, such as text flow direction, text alignment, or indentation.</param>
        /// <param name="previousLineBreak">text formatting state at the point where the previous line in the paragraph
        /// was broken by the text formatting process, as specified by the TextLine.LineBreak property for the previous
        /// line; this parameter can be null, and will always be null for the first line in a paragraph.</param>
        /// <param name="textRunCache">an object representing content cache of the client.</param>
        /// <returns>object representing a line of text that client interacts with. </returns>
#if OPTIMALBREAK_API
        public override TextParagraphCache CreateParagraphCache(
#else
        internal override TextParagraphCache CreateParagraphCache(
#endif
            TextSource                  textSource,
            int                         firstCharIndex,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak,
            TextRunCache                textRunCache
            )
        {
            // prepare formatting settings
            FormatSettings settings = PrepareFormatSettings(
                textSource,
                firstCharIndex,
                paragraphWidth,
                paragraphProperties,
                previousLineBreak,
                textRunCache,
                true,   // optimalBreak
                false,  // !isSingleLineFormatting
                _textFormattingMode
                );

            //
            // Optimal paragraph formatting session specific check
            //
            if (!settings.Pap.Wrap && settings.Pap.OptimalBreak)
            {
                // Optimal paragraph must wrap.
                throw new ArgumentException(SR.OptimalParagraphMustWrap);
            }

            // create paragraph content cache object
            return new TextParagraphCache(
                settings,
                firstCharIndex,
                RealToIdeal(paragraphWidth)
                );
        }



        /// <summary>
        /// Validate all the relevant text formatting initial settings and package them
        /// </summary>
        private FormatSettings PrepareFormatSettings(
            TextSource                  textSource,
            int                         firstCharIndex,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextLineBreak               previousLineBreak,
            TextRunCache                textRunCache,
            bool                        useOptimalBreak,
            bool                        isSingleLineFormatting,
            TextFormattingMode              textFormattingMode
            )
        {
            VerifyTextFormattingArguments(
                textSource,
                firstCharIndex,
                paragraphWidth,
                paragraphProperties,
                textRunCache
                );

            if (textRunCache.Imp == null)
            {
                // No run cache object available, create one
                textRunCache.Imp = new TextRunCacheImp();
            }

            // initialize formatting settings
            return new FormatSettings(
                this,
                textSource,
                textRunCache.Imp,
                new ParaProp(this, paragraphProperties, useOptimalBreak),
                previousLineBreak,
                isSingleLineFormatting,
                textFormattingMode,
                false
                );
        }



        /// <summary>
        /// Verify all text formatting arguments
        /// </summary>
        private void VerifyTextFormattingArguments(
            TextSource                  textSource,
            int                         firstCharIndex,
            double                      paragraphWidth,
            TextParagraphProperties     paragraphProperties,
            TextRunCache                textRunCache
            )
        {
            ArgumentNullException.ThrowIfNull(textSource);

            ArgumentNullException.ThrowIfNull(textRunCache);

            ArgumentNullException.ThrowIfNull(paragraphProperties);

            if (paragraphProperties.DefaultTextRunProperties == null)
                throw new ArgumentNullException("paragraphProperties.DefaultTextRunProperties");

            if (paragraphProperties.DefaultTextRunProperties.Typeface == null)
                throw new ArgumentNullException("paragraphProperties.DefaultTextRunProperties.Typeface");

            ArgumentOutOfRangeException.ThrowIfEqual(paragraphWidth, double.NaN);
            ArgumentOutOfRangeException.ThrowIfNegative(paragraphWidth);
            ArgumentOutOfRangeException.ThrowIfEqual(paragraphWidth, double.PositiveInfinity);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(paragraphWidth, Constants.RealInfiniteWidth);

            double realMaxFontRenderingEmSize = Constants.RealInfiniteWidth / Constants.GreatestMutiplierOfEm;

            ArgumentOutOfRangeException.ThrowIfNegative(paragraphProperties.DefaultTextRunProperties.FontRenderingEmSize, "paragraphProperties.DefaultTextRunProperties.FontRenderingEmSize");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(paragraphProperties.DefaultTextRunProperties.FontRenderingEmSize, realMaxFontRenderingEmSize, "paragraphProperties.DefaultTextRunProperties.FontRenderingEmSize");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(paragraphProperties.Indent, Constants.RealInfiniteWidth, "paragraphProperties.Indent");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(paragraphProperties.LineHeight, Constants.RealInfiniteWidth, "paragraphProperties.LineHeight");
            ArgumentOutOfRangeException.ThrowIfNegative(paragraphProperties.DefaultIncrementalTab, "paragraphProperties.DefaultIncrementalTab");
            ArgumentOutOfRangeException.ThrowIfGreaterThan(paragraphProperties.DefaultIncrementalTab, Constants.RealInfiniteWidth, "paragraphProperties.DefaultIncrementalTab");
        }


        /// <summary>
        /// Validate the input character hit
        /// </summary>
        internal static void VerifyCaretCharacterHit(
            CharacterHit    characterHit,
            int             cpFirst,
            int             cchLength
            )
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(cpFirst, characterHit.FirstCharacterIndex);
            ArgumentOutOfRangeException.ThrowIfLessThan(cpFirst, characterHit.FirstCharacterIndex - cchLength);

            ArgumentOutOfRangeException.ThrowIfNegative(characterHit.TrailingLength, nameof(cchLength));
        }



        /// <summary>
        /// Acquire a free TextFormatter context for complex line operation
        /// </summary>
        /// <param name="owner">object that becomes the owner of LS context once acquired</param>
        /// <param name="ploc">matching PLOC</param>
        /// <returns>Active LS context</returns>
        /// <SecurityNotes>
        /// Critical - this sets the owner of the context
        /// Safe     - this doesn't expose critical info
        /// </SecurityNotes>
        internal TextFormatterContext AcquireContext(
            object      owner,
            IntPtr      ploc
            )
        {
            Invariant.Assert(owner != null);

            TextFormatterContext context = null;

            int c;
            int contextCount = _contextList.Count;

            for (c = 0; c < contextCount; c++)
            {
                context = (TextFormatterContext)_contextList[c];

                if (ploc == IntPtr.Zero)
                {
                    if(context.Owner == null)
                        break;
                }
                else if (ploc == context.Ploc)
                {
                    // LS requires that we use the exact same context for line
                    // destruction or hittesting (part of the reason is that LS
                    // actually caches some run info in the context). So here
                    // we use the actual PLSC as the context signature so we
                    // locate the one we want.

                    Debug.Assert(context.Owner == null);
                    break;
                }
            }

            if (c == contextCount)
            {
                if (contextCount == 0 || !_multipleContextProhibited)
                {
                    //  no free one exists, create a new one
                    context = new TextFormatterContext();
                    _contextList.Add(context);
                }
                else
                {
                    // This instance of TextFormatter only allows a single context, reentering the
                    // same TextFormatter in this case is not allowed.
                    //
                    // This requirement is currently enforced only during optimal break computation.
                    // Client implementing nesting of optimal break content inside another must create
                    // a separate TextFormatter instance for each content in different nesting level.
                    throw new InvalidOperationException(SR.TextFormatterReentranceProhibited);
                }
            }

            Debug.Assert(context != null);

            context.Owner = owner;
            return context;
        }


        /// <summary>
        /// Create an anti-inversion transform from the inversion flags.
        /// The result is used to correct glyph bitmap on an output to
        /// a drawing surface with the specified inversions applied on.
        /// </summary>
        internal static MatrixTransform CreateAntiInversionTransform(
            InvertAxes  inversion,
            double      paragraphWidth,
            double      lineHeight
            )
        {
            if (inversion == InvertAxes.None)
            {
                // avoid creating unncessary pressure on GC when anti-transform is not needed.
                return null;
            }

            double m11 = 1;
            double m22 = 1;
            double offsetX = 0;
            double offsetY = 0;

            if ((inversion & InvertAxes.Horizontal) != 0)
            {
                m11 = -m11;
                offsetX = paragraphWidth;
            }

            if ((inversion & InvertAxes.Vertical) != 0)
            {
                m22 = -m22;
                offsetY = lineHeight;
            }

            return new MatrixTransform(m11, 0, 0, m22, offsetX, offsetY);
        }

        /// <summary>
        /// Compare text formatter real values - since values are rounded in Display mode, comparison
        /// must also round and only return true if one rounded value is greater than the other.
        /// </summary>
        /// <param name="x">First value to compare.</param>
        /// <param name="y">Second value to compare.</param>
        /// <param name="mode">Text formatting mode.</param>
        /// <returns>1 if x greater than y, -1 if x less than y, 0 if x == y</returns>
        internal static int CompareReal(double x, double y, double pixelsPerDip, TextFormattingMode mode)
        {
            double xDisplay = x;
            double yDisplay = y;

            if (mode == TextFormattingMode.Display)
            {
                xDisplay = RoundDipForDisplayMode(x, pixelsPerDip);
                yDisplay = RoundDipForDisplayMode(y, pixelsPerDip);
            }

            if (xDisplay > yDisplay)
            {
                return 1;
            }

            if (xDisplay < yDisplay)
            {
                return -1;
            }

            return 0;
        }

        internal static double RoundDip(double value, double pixelsPerDip, TextFormattingMode textFormattingMode)
        {
            if (TextFormattingMode.Display == textFormattingMode)
            {
                return RoundDipForDisplayMode(value, pixelsPerDip);
            }
            else
            {
                return value;
            }
        }

        internal static double RoundDipForDisplayMode(double value, double pixelsPerDip)
        {
            return RoundDipForDisplayMode(value, pixelsPerDip, MidpointRounding.ToEven);
        }

        private static double RoundDipForDisplayMode(double value, double pixelsPerDip, MidpointRounding midpointRounding)
        {
            return Math.Round(value * pixelsPerDip, midpointRounding) / pixelsPerDip;
        }

        /// <summary>
        /// The default behavior of Math.Round() leads to undesirable behavior
        /// When used for display mode justified text, where we can find 
        /// characters belonging to the same word jumping sideways.
        /// A word can break among several GlyphRuns. So we need consistent
        /// rounding of the width of the GlyphRuns. If the width of one GlyphRun
        /// rounds up and the next GlyphRun rounds down then we see characters 
        /// overlapping and so on.
        /// It is too late to change the behavior of our rounding universally
        /// so we are making the change targeted to Display mode + Justified text
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        internal static double RoundDipForDisplayModeJustifiedText(double value, double pixelsPerDip)
        {
            return RoundDipForDisplayMode(value, pixelsPerDip, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Scale LS ideal resolution value to real value
        /// </summary>
        internal static double IdealToRealWithNoRounding(double i)
        {
            return i * Constants.DefaultIdealToReal;
        }

        /// <summary>
        /// Scale LS ideal resolution value to real value
        /// </summary>
        internal double IdealToReal(double i, double pixelsPerDip)
        {
            double value = IdealToRealWithNoRounding(i);
            if (_textFormattingMode == TextFormattingMode.Display)
            {
                value = RoundDipForDisplayMode(value, pixelsPerDip);
            }

            if (i > 0)
            {
                // Non-zero values should not be converted to 0 accidentally through rounding, ensure that at least the min value is returned.
                value = Math.Max(value, Constants.DefaultIdealToReal);
            }

            return value;
        }

        /// <summary>
        /// Scale real value to LS ideal resolution
        /// </summary>
        internal static int RealToIdeal(double i)
        {
            int value = (int)Math.Round(i * ToIdeal);
            if (i > 0)
            {
                // Non-zero values should not be converted to 0 accidentally through rounding, ensure that at least the min value is returned.
                value = Math.Max(value, 1);
            }
            return value;
        }

        /// <summary>
        /// Scale the real value to LS ideal resolution
        /// Use the floor value of the scale value
        /// </summary>
        /// <remarks>
        /// Using Math.Round may result in a line larger than
        /// the actual given paragraph width. For example,
        /// round tripping 100.112 with factor 300 becomes 100.1133...
        /// Using floor to ensure we never go beyond paragraph width
        /// </remarks>
        internal static int RealToIdealFloor(double i)
        {
            int value = (int)Math.Floor(i * ToIdeal);
            if (i > 0)
            {
                // Non-zero values should not be converted to 0 accidentally through rounding, ensure that at least the min value is returned.
                value = Math.Max(value, 1);
            }
            return value;
        }

        /// <summary>
        /// Real to ideal value scaling factor
        /// </summary>
        internal static double ToIdeal
        {
            get { return Constants.DefaultRealToIdeal; }
        }

        /// <summary>
        /// Return the GlyphingCache associated with this TextFormatterImp object.
        /// GlyphingCache stores the mapping from Unicode scalar value to the physical font that is
        /// used to display it.
        /// </summary>
        internal GlyphingCache GlyphingCache
        {
            get
            {
                if (_glyphingCache == null)
                {
                    _glyphingCache = new GlyphingCache(MaxGlyphingCacheCapacity);
                }

                return _glyphingCache;
            }
        }

        /// <summary>
        /// Return the TextAnalyzer associated with this TextFormatterImp object.
        /// TextAnalyzer is used in shaping process.
        /// </summary>
        internal TextAnalyzer TextAnalyzer
        {
            get
            {
                if (_textAnalyzer == null)
                {
                    _textAnalyzer = DWriteFactory.Instance.CreateTextAnalyzer();
                }

                return _textAnalyzer;
            }
        }
    }
}

