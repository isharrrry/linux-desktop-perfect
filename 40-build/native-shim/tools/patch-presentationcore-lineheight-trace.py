#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c/D 应用器：给 `SimpleTextLine` 的**行高 / 行偏移**加一条**只读插桩**（缺省关、有界）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-lineheight-trace.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-lineheight-trace.py          # 无参 = 应用

======================================================================================
【为什么要它（主控 2026-09-11 批准的判据）】
  现象：WpfTextDemo 的**多行摞印**。T2b 的 origin-Y 读数 + T1c 的算术把它钉到了"origin Y 里
  **没有行偏移**"这一格，但**要看住是哪一格丢的**，必须在**行高/行偏移被算出来的那一刻**读一次：
    · `SimpleRun.Baseline` / `SimpleRun.Height`（`SimpleTextLine.cs:1338/1349`）——
      非空行的行高就是这一行所有 run 的 `Height` 取大，**行距的真源**；
    · 构造函数里 `_height` / `_baselineOffset` 的落点（round-trip 量化之后）——**行内基线偏移**；
    · `Draw(...)` 收到的 `origin` ——**宿主到底有没有把"这一行在段落里的 Y"带进来**。
  ⇒ 三者一并读，就能把"行距塌了"与"行偏移没带上"分开。

【只读/无副作用（三条硬约束）】
  ① **缺省关**：`WPF_LINUX_LINEHEIGHT_TRACE` 未设/空白 ⇒ `Enabled=false` ⇒ 一行都不打（连字符串都不拼）；
  ② **有界**：最多 `MaxLines = 60` 行（`Interlocked`），不会刷屏、不会拖慢渲染；
  ③ **不碰行为**：只在"算完之后读一眼再 return 同一个值"，不改任何控制流、不改任何返回值，
     不碰 `RenderDiagnostics`、不进任何 runner 判据。

【家族纪律（本项目用血换来的）】
  · 生成物 = 上游**逐字复制** + 4 处插入；锚点每处必须**恰好出现 1 次**，否则报错退出（不静默产出未打补丁的副本）；
  · 无参运行 = **真的应用**（写生成物 + csproj 接线），幂等；`--check` 只读；
  · 机械证据：生成物里的 `throw` 条数与上游**逐字相同**；
  · **接线必须在每次波重放后仍生效**：波的第 1 步会重写 csproj，只有由本应用器重放才活得下来；
  · 验证接线**不读 XML**，用 `dotnet msbuild … -getItem:Compile` 求值（命令见文件末尾打印）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
GENERATED = os.path.join(PC_DIR, "SimpleTextLine.Linux.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                        "PresentationCore", "MS", "internal", "TextFormatting", "SimpleTextLine.cs")
UPSTREAM_REL = ("src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/TextFormatting/"
                "SimpleTextLine.cs")

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux T1c/D：SimpleTextLine 行高/行偏移只读插桩"
                "（由 src/Linux/tools/patch-presentationcore-lineheight-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux T1c/D 结束 ==== -->"

# =====================================================================================
#  逐字锚点 → 替换（含缩进）
# =====================================================================================

# ---- (1) SimpleRun.Baseline：读一眼再 return 同一个值 ----
ANCHOR1 = ('                return TextRun.Properties.Typeface.Baseline('
           'TextRun.Properties.FontRenderingEmSize, 1, _pixelsPerDip, '
           '_textFormatterImp.TextFormattingMode);\n')

REPLACEMENT1 = '''                double runBaseline = TextRun.Properties.Typeface.Baseline(TextRun.Properties.FontRenderingEmSize, 1, _pixelsPerDip, _textFormatterImp.TextFormattingMode);
                // ── T1c/D 只读插桩（缺省关）──
                WpfLinuxLineHeightTrace.RunBaseline(
                    TextRun.Properties.Typeface.FontFamily == null ? "<null>" : TextRun.Properties.Typeface.FontFamily.Source,
                    TextRun.Properties.FontRenderingEmSize,
                    _pixelsPerDip,
                    _textFormatterImp.TextFormattingMode.ToString(),
                    runBaseline);
                return runBaseline;
'''

# ---- (2) SimpleRun.Height：同上（行距的真源） ----
ANCHOR2 = ('                return TextRun.Properties.Typeface.LineSpacing('
           'TextRun.Properties.FontRenderingEmSize, 1, _pixelsPerDip, '
           '_textFormatterImp.TextFormattingMode);\n')

REPLACEMENT2 = '''                double runHeight = TextRun.Properties.Typeface.LineSpacing(TextRun.Properties.FontRenderingEmSize, 1, _pixelsPerDip, _textFormatterImp.TextFormattingMode);
                // ── T1c/D 只读插桩（缺省关）──
                WpfLinuxLineHeightTrace.RunHeight(
                    TextRun.Properties.Typeface.FontFamily == null ? "<null>" : TextRun.Properties.Typeface.FontFamily.Source,
                    TextRun.Properties.FontRenderingEmSize,
                    _pixelsPerDip,
                    _textFormatterImp.TextFormattingMode.ToString(),
                    runHeight);
                return runHeight;
'''

# ---- (3) 行级：空行分支之后（此时 _height / _baselineOffset 都已定值）----
ANCHOR3 = ('                _baselineOffset = formatter.IdealToReal((int)Math.Round('
           'pap.DefaultTypeface.Baseline(pap.EmSize, Constants.DefaultIdealToReal, PixelsPerDip, '
           '_settings.TextFormattingMode)), PixelsPerDip);\n'
           '            }\n')

REPLACEMENT3 = '''                _baselineOffset = formatter.IdealToReal((int)Math.Round(pap.DefaultTypeface.Baseline(pap.EmSize, Constants.DefaultIdealToReal, PixelsPerDip, _settings.TextFormattingMode)), PixelsPerDip);
            }

            // ── T1c/D 只读插桩（缺省关）：行级量（round-trip 之后）──
            //   非空行：_height/_baselineOffset 来自本行 run 的 Height/Baseline 取大（上面那些 max）；
            //   空行：走上一个 if 分支用 DefaultTypeface 的度量。
            WpfLinuxLineHeightTrace.LineMetrics(
                realAscent, realDescent, realHeight, _height, _baselineOffset, PixelsPerDip,
                _settings.TextFormattingMode.ToString());
'''

# ---- (4) Draw 收到的 origin：宿主有没有把"这一行的 Y"带进来 ----
ANCHOR4 = '            ArgumentNullException.ThrowIfNull(drawingContext);\n'

REPLACEMENT4 = '''            ArgumentNullException.ThrowIfNull(drawingContext);

            // ── T1c/D 只读插桩（缺省关）：**宿主传进来的 origin** 是本诊断的关键一格 ──
            WpfLinuxLineHeightTrace.DrawOrigin(
                _cpFirst, origin.X, origin.Y, _height, _baselineOffset,
                _runs == null ? 0 : _runs.Length, PixelsPerDip);
'''

# ---- (5) 插桩类本体（放在 SimpleTextLine 类之前，同一 namespace 内）----
ANCHOR5 = '    internal class SimpleTextLine : TextLine\n'

REPLACEMENT5 = '''    /// <summary>
    /// T1c/D · 行高/行偏移的**只读**插桩（缺省关）：`WPF_LINUX_LINEHEIGHT_TRACE=1` 才打，最多
    /// <see cref="MaxLines"/> 行。**只读**：调用点都是"算完读一眼再返回同一个值"，不改控制流。
    ///
    /// 【为什么要它】"多行摞印"要分清两种病因：
    ///   (a) **行距被算小了**（`SimpleRun.Height` / `_height` 塌了）⇒ 行与行本来就贴在一起；
    ///   (b) **行偏移根本没带上**（`Draw` 收到的 `origin.Y` 每行都一样 / 等于行内基线偏移）
    ///       ⇒ 每一行都被画在同一个 Y 上。
    /// 只打"度量返回值"分不清这两者，所以四处一并读：run 的 Baseline/Height、行级 `_height`/
    /// `_baselineOffset`、以及宿主传进 `Draw` 的 `origin`。
    /// </summary>
    internal static class WpfLinuxLineHeightTrace
    {
        /// <summary>开关名。**未设/空白 ⇒ 关**（纯函数 <see cref="IsOn"/>）。</summary>
        internal const string EnvVar = "WPF_LINUX_LINEHEIGHT_TRACE";

        /// <summary>有界：测量期（run/line）最多打这么多行。</summary>
        private const int MaxLines = 120;

        /// <summary>
        /// **draw 的独立预算**。
        /// ⚠️ 实测踩到：四个调用点共用一个预算时，**测量期先跑**会把预算吃光 ⇒ `draw origin`
        /// **一行都打不出来**（正是本诊断最关键的一格）。⇒ 给 draw 单独一份，保证它一定可见。
        /// </summary>
        private const int MaxDrawLines = 40;

        private static readonly bool s_enabled = IsOn(Environment.GetEnvironmentVariable(EnvVar));
        private static int s_lines;
        private static int s_drawLines;

        internal static bool Enabled { get { return s_enabled; } }

        /// <summary>未设/空白 ⇒ **false**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
        internal static bool IsOn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            if (v == "1") return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string F(double value)
        {
            return value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Emit(string message)
        {
            if (!s_enabled) return;
            if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines) return;
            try
            {
                Console.Error.WriteLine("[LINEHEIGHT] " + message);
                Console.Error.Flush();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>`SimpleRun.Baseline` 的返回值（= 该 run 的上升部，DIP）。</summary>
        internal static void RunBaseline(string family, double emSize, double ppd, string mode, double value)
        {
            Emit("run.Baseline fam=\\"" + family + "\\" em=" + F(emSize) + " ppd=" + F(ppd)
                 + " mode=" + mode + " ret=" + F(value));
        }

        /// <summary>`SimpleRun.Height` 的返回值（= 该 run 的行距，DIP）—— **行距的真源**。</summary>
        internal static void RunHeight(string family, double emSize, double ppd, string mode, double value)
        {
            Emit("run.Height   fam=\\"" + family + "\\" em=" + F(emSize) + " ppd=" + F(ppd)
                 + " mode=" + mode + " ret=" + F(value));
        }

        /// <summary>行级量：`_height` / `_baselineOffset`（round-trip 量化之后）。</summary>
        internal static void LineMetrics(double realAscent, double realDescent, double realHeight,
                                         double height, double baselineOffset, double ppd, string mode)
        {
            Emit("line realAscent=" + F(realAscent) + " realDescent=" + F(realDescent)
                 + " realHeight=" + F(realHeight) + " _height=" + F(height)
                 + " _baselineOffset=" + F(baselineOffset) + " ppd=" + F(ppd) + " mode=" + mode);
        }

        /// <summary>宿主传进 `Draw` 的 origin —— **"行偏移有没有被带上"的唯一直接读数**。</summary>
        internal static void DrawOrigin(int cpFirst, double x, double y, double height,
                                        double baselineOffset, int runs, double ppd)
        {
            if (!s_enabled) return;
            if (System.Threading.Interlocked.Increment(ref s_drawLines) > MaxDrawLines) return;
            try
            {
                Console.Error.WriteLine("[LINEHEIGHT] draw cpFirst=" + cpFirst
                    + " origin=(" + F(x) + "," + F(y) + ") _height=" + F(height)
                    + " _baselineOffset=" + F(baselineOffset) + " runs=" + runs + " ppd=" + F(ppd));
                Console.Error.Flush();
            }
            catch (Exception)
            {
            }
        }
    }

    internal class SimpleTextLine : TextLine
'''

ANCHOR6 = '''            // instantiate a collapsible full text line, collapse it and return the collapsed line
            TextMetrics.FullTextLine textLine = new TextMetrics.FullTextLine(
                _settings,
                _cpFirst,
                0,  // lineLength
                TextFormatterImp.RealToIdeal(_paragraphWidth),
                LineFlags.None
                );
'''

REPLACEMENT6 = '''            // ── WPF-on-Linux（T1b/D3 同族·**折叠路径**）：先试**托管折叠** ──
            //   本栈没有 LineServices：下面那段 `new TextMetrics.FullTextLine(...)` 会走到
            //   `TextFormatterContext.CreateLine` ⇒ `LoCreateLine`，而该入口在本仓 shim 里是
            //   **刻意不导出**的（`src/WpfGfx.Linux.Native/src/win32_pts.c` 的"不冒充"纪律：
            //   出参含真几何/行内容/字形的入口一律不冒充）⇒ `EntryPointNotFoundException`，
            //   把 `TextBlock.MeasureOverride → Line.GetCollapsedWidth → 本方法` 整条链打断。
            //   （实测：HandyControl demo 的 `hc:PropertyGrid`「属性编辑器」页一进排版即抛。）
            //   托管行引擎的 `Collapse` 已是真实现（按真机 oracle 的 CharacterEllipsis 折叠）
            //   ⇒ 交给它接管；**接不了（返回 null）就原样走下面那条上游 LS 路径**，
            //   与接线前的行为逐字一致（不假装成功、不吞异常）。
            TextLine hbLine = WpfLinux.Shims.PresentationCore.HbTextFallback.TryFormatLine(
                _settings.TextSource,
                _cpFirst,
                _paragraphWidth,
                _settings.TextSource.PixelsPerDip,
                false,      // alwaysCollapsible：本处对应上游 LineFlags.None
                0           // lineHeight：0 ⇒ 用字体自然行高（真机口径）
                ) as TextLine;
            if (hbLine != null)
            {
                TextLine hbCollapsed = hbLine.Collapse(collapsingPropertiesList);
                if (hbCollapsed != hbLine)
                {
                    // 与上游同款处置：折叠产物是**新行**时才显式释放中间行
                    hbLine.Dispose();
                }
                return hbCollapsed;
            }

            // instantiate a collapsible full text line, collapse it and return the collapsed line
            TextMetrics.FullTextLine textLine = new TextMetrics.FullTextLine(
                _settings,
                _cpFirst,
                0,  // lineLength
                TextFormatterImp.RealToIdeal(_paragraphWidth),
                LineFlags.None
                );
'''

EDITS = [
    ("① SimpleRun.Baseline 只读插桩", ANCHOR1, REPLACEMENT1),
    ("② SimpleRun.Height 只读插桩", ANCHOR2, REPLACEMENT2),
    ("③ 行级 _height/_baselineOffset 插桩", ANCHOR3, REPLACEMENT3),
    ("④ Draw 的 origin 插桩", ANCHOR4, REPLACEMENT4),
    ("⑤ 插桩类本体", ANCHOR5, REPLACEMENT5),
    ("⑥ SimpleTextLine.Collapse 先试托管折叠（消 LoCreateLine/EntryPointNotFoundException）", ANCHOR6, REPLACEMENT6),
]

REQUIRED_IN_OUTPUT = [
    ("插桩类存在", "internal static class WpfLinuxLineHeightTrace"),
    ("开关名", 'internal const string EnvVar = "WPF_LINUX_LINEHEIGHT_TRACE";'),
    ("缺省关语义（纯函数首行）", "if (string.IsNullOrWhiteSpace(value)) return false;"),
    ("有界（测量期）", "private const int MaxLines = 120;"),
    ("有界（draw 独立预算）", "private const int MaxDrawLines = 40;"),
    ("① run.Baseline 调用点", "WpfLinuxLineHeightTrace.RunBaseline("),
    ("② run.Height 调用点", "WpfLinuxLineHeightTrace.RunHeight("),
    ("③ 行级调用点", "WpfLinuxLineHeightTrace.LineMetrics("),
    ("④ Draw origin 调用点", "WpfLinuxLineHeightTrace.DrawOrigin("),
    ("① 仍返回同一个值（行为不变）", "return runBaseline;"),
    ("② 仍返回同一个值（行为不变）", "return runHeight;"),
    ("⑥ 托管折叠接管点存在", "WpfLinux.Shims.PresentationCore.HbTextFallback.TryFormatLine("),
    ("⑥ 接不了仍保留上游 LS 路径", "TextMetrics.FullTextLine textLine = new TextMetrics.FullTextLine("),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-lineheight-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/TextFormatting/SimpleTextLine.cs`
//        逐字复制 + 5 处 T1c/D **只读插桩**（行高 / 行偏移 / Draw 的 origin）
//        ＋ **1 处行为改动**（⑥：`SimpleTextLine.Collapse` 先试托管折叠 `HbTextFallback`，
//          接不了则原样回落上游 LS 路径 —— 本栈没有 LineServices 且 `LoCreateLine` 刻意不导出）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 计数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打（主控 2026-09-11 批准）：WpfTextDemo "多行摞印"要分清
//   ① **行距被算小了**（`SimpleRun.Height`/`_height` 塌了）vs
//   ② **行偏移没带上**（`Draw` 收到的 `origin.Y` 每行相同 / 只等于行内基线偏移）。
// 插桩**缺省关**（`WPF_LINUX_LINEHEIGHT_TRACE` 未设 ⇒ 一行不打）、**有界**（≤60 行）、
// 且调用点都是"算完读一眼再 return 同一个值" ⇒ **不改变任何行为**。
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
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？T1c/D 插桩未应用（**不做任何静默降级**）。")
        return 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    # ---- 结构性断言（生成物自检）----
    for name, needle in REQUIRED_IN_OUTPUT:
        if needle not in out:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return 1

    # ---- 机械证据：没有新增/删除任何 throw（只读插桩 ≠ 改行为）----
    upstream_throws = _count(text, "throw ")
    out_throws = _count(out, "throw ")
    if upstream_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {upstream_throws} → 生成物 {out_throws}")
        return 1
    print(f"[断言] `throw` 条数 上游 {upstream_throws} == 生成物 {out_throws}（只读插桩，未改行为）")
    print(f"[断言] 上游 {len(text.splitlines())} 行 → 生成物 {len(out.splitlines())} 行"
          f"（+{len(out.splitlines()) - len(text.splitlines())} 行，全部是插桩与注释）")

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
        with open(GENERATED, "w", encoding="utf-8") as f:
            f.write(output)
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：已从上游重生成（5 处 T1c/D 只读插桩 + 1 处行为改动：折叠先试托管）")

    # ---- csproj 接线 ----
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0 if (not check_only or up_to_date) else 1

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1

    # ① 整块（含 <ItemGroup>）插在锚点**行之前**（否则 <ItemGroup> 嵌套 ⇒ MSB4232）；
    # ② Remove/Include 按文档顺序求值 ⇒ 上游那条 Include 必须先出现，Remove 落在它之后才生效
    #    （插在 Sdk.targets 之前 ⇒ 天然满足）。
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             f'    <Compile Remove="$(UpstreamWpfRoot){UPSTREAM_REL}" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SimpleTextLine.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 之后于上游 Include）")
    print("\n下一步（**不要在这里重建权威 PC**；编到 /tmp 做闸门）：")
    print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\")
    print("      -getItem:Compile -p:Configuration=Debug | grep -i SimpleTextLine")
    print("  # 期望只剩 SimpleTextLine.Linux.cs（上游那条被 Remove 掉）")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--apply", action="store_true", help="显式表示要写盘（默认行为，等价）")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
