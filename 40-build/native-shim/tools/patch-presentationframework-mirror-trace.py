#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c · RTL 镜像：**实际推给视觉树的变换** vs `GetFlowDirectionTransform()` 的报告口径（只读插桩）。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-mirror-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-mirror-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-mirror-trace.py --prove   # 「只插入」证明（只读）

为什么打（T1d §11 的已知 + 本批要钉的那一格）
--------------------------------------------
已知：shim 的 `PresentationCore.HbTextLine.cs:2424-2439` 确实推反演矩阵，但 `pw == W` 精确成立
（T3 实测 `pw-W=0.0000`）⇒ 那是**区间自映射、位置中性**的 ⇒ **既不是因、也不是药**。
渲染实测等效变换是 `R(y)=42−y`（镜在元素左边缘 x≈21），而上游口径
`GetFlowDirectionTransform()` = `Matrix(−1,0,0,1,**RenderSize.Width**,0)`（`FrameworkElement.cs:3940-3948`）
预测的墨迹区间 `[20.65, 86.03]` **对不上（差 44.3 px）**，`R(y)=42−y` 才对得上（≤0.4 px）。

⇒ 本批只问一件事：**实际推给 drawingContext/视觉树的那个变换是什么**，与 `GetFlowDirectionTransform()` 的返回值**并排**。
读码定位（上游逐字，`file:line` 见报告 §13）：
    :3940 `GetFlowDirectionTransform()`            —— 造 `MatrixTransform(-1,0,0,1,RenderSize.Width,0)`
    :3950 `ShouldApplyMirrorTransform(fe)`         —— 找 parent 的 FlowDirection
    :4030 `ApplyMirrorTransform(parentFD, thisFD)` —— **纯方向判定**（**签名里没有 offsetX** ⇒ 主控原先的假设要修正）
    :5130 `InternalSetLayoutTransform(element, transform)`，`:5135` 里 `additionalTransform = fe?.GetFlowDirectionTransform()`
    :5171 `SetLayoutOffset(offset, oldRenderSize)`，`:5197` 里 `additionalTransform = GetFlowDirectionTransform()`  ← **视觉变换的真正组装点**
    :4925 `GetLayoutClip` 里 `rtlMirror = GetFlowDirectionTransform()`（裁剪路径）
    `TextBlock.cs:1475 TextBlock.OnRender`                                                  ← 宿主侧读数

过滤：**先按类型名（不读 DP）** 命中 `TextBlock`，**再**读 `FlowDirection` 要求 `RightToLeft`；有界 ≤200 行 + 触顶通知（L12）。
⚠️ 本批**故意读 `FlowDirection` 这个 DP**（它就是过滤条件本身），只对 TextBlock 读；`ActualWidth` **不读**（改用 `RenderSize`，非 DP）。
"""

import argparse
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
PREFIX = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/"

FE_REL = PREFIX + "System/Windows/FrameworkElement.cs"
TB_REL = PREFIX + "System/Windows/Controls/TextBlock.cs"

TRACE_CLASS = '''// =====================================================================================
//  T1c · RTL 镜像读数：**实际推给视觉树的变换** vs `GetFlowDirectionTransform()` 的报告口径
//
//  只对 **TextBlock 且 FlowDirection==RightToLeft** 打（先按类型名过滤——不读 DP；再读 FlowDirection）。
//  ⚠️ 本类是唯一"故意读一个 DP"的插桩：`FlowDirection` 就是过滤条件本身；`ActualWidth` 不读（用 `RenderSize`）。
//  缺省关 / ≤200 行 / 触顶打一行（L12）/ 只打印；调用点实参全部惰性（ARGSHAPE 规则）。
// =====================================================================================
internal static class WpfLinuxMirrorTrace
{
    private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";
    private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";
    private const int MaxLines = 200;

    private static readonly bool s_enabled;
    private static readonly string s_via;
    private static int s_lines;
    private static int s_budgetNotice;

    static WpfLinuxMirrorTrace()
    {
        string a = null, b = null;
        try { a = System.Environment.GetEnvironmentVariable(EnvInputTrace); } catch (System.Exception) { }
        try { b = System.Environment.GetEnvironmentVariable(EnvMsgFlow); } catch (System.Exception) { }
        s_enabled = IsOn(a) || IsOnNativeSwitch(b);
        s_via = IsOn(a) ? EnvInputTrace : (IsOnNativeSwitch(b) ? EnvMsgFlow : "(都未设)");
        if (s_enabled)
        {
            Emit("via=" + s_via + " pid=" + System.Environment.ProcessId
                 + " —— RTL 镜像读数（只对 TextBlock+RTL；≤" + MaxLines + " 行）");
        }
    }

    internal static bool IsOn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string v = value.Trim();
        if (v == "1") return true;
        if (string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "on", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "yes", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsOnNativeSwitch(string value)
    {
        if (value == null) return false;
        string v = value.Trim();
        if (v.Length == 0) return false;
        return v != "0";
    }

    internal static int LineCount { get { return s_lines; } }

    private static void Emit(string message)
    {
        if (!s_enabled) return;
        if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines)
        {
            if (System.Threading.Interlocked.Exchange(ref s_budgetNotice, 1) == 0)
            {
                try
                {
                    System.Console.Error.WriteLine("[INPUT_TRACE] **预算用尽**（镜像读数 MaxLines=" + MaxLines
                        + "）⇒ 之后不再打印；**「没打」≠「没发生」**：请调大额度或缩小过滤范围");
                    System.Console.Error.Flush();
                }
                catch (System.Exception) { }
            }
            return;
        }
        try
        {
            System.Console.Error.WriteLine("[INPUT_TRACE] " + message);
            System.Console.Error.Flush();
        }
        catch (System.Exception)
        {
            System.Threading.Interlocked.Decrement(ref s_lines);
        }
    }

    /// <summary>先按**类型名**过滤（不读 DP），命中的 TextBlock 才去读 `FlowDirection`。</summary>
    private static bool IsRtlTextBlock(object o)
    {
        if (o == null) return false;
        try
        {
            string tn = o.GetType().Name;
            if (tn != "TextBlock") return false;              // ← 类型名先过滤：非 TextBlock 一个 DP 都不读
            System.Windows.FrameworkElement fe = o as System.Windows.FrameworkElement;
            return (fe != null) && (fe.FlowDirection == System.Windows.FlowDirection.RightToLeft);
        }
        catch (System.Exception) { return false; }
    }

    private static string FeOf(object o)
    {
        try
        {
            System.Windows.FrameworkElement fe = o as System.Windows.FrameworkElement;
            if (fe == null) return "(非 FrameworkElement)";
            return fe.GetType().Name + "#"
                 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fe).ToString("x")
                 + " FlowDirection=" + fe.FlowDirection
                 + " RenderSize=" + fe.RenderSize.Width.ToString("0.###") + "x" + fe.RenderSize.Height.ToString("0.###");
        }
        catch (System.Exception ex) { return "(读状态抛 " + ex.GetType().Name + ")"; }
    }

    /// <summary>变换读数：**矩阵四个量 + OffsetX**（`Transform.Value` 是纯计算，不读 DP）。</summary>
    internal static string XfOf(object t)
    {
        if (t == null) return "(null⇒无镜像)";
        try
        {
            System.Windows.Media.Transform xf = t as System.Windows.Media.Transform;
            if (xf == null) return "(非 Transform)";
            System.Windows.Media.Matrix m = xf.Value;
            return xf.GetType().Name + " M11=" + m.M11.ToString("0.####") + " M22=" + m.M22.ToString("0.####")
                 + " **OffsetX=" + m.OffsetX.ToString("0.####") + "** OffsetY=" + m.OffsetY.ToString("0.####");
        }
        catch (System.Exception ex) { return "(读 Value 抛 " + ex.GetType().Name + ")"; }
    }

    /// <summary>M1 `GetFlowDirectionTransform` 入口。</summary>
    internal static void M1Entry(object fe)
    {
        if (!IsRtlTextBlock(fe)) return;
        Emit("M1 GetFlowDirectionTransform 入口 " + FeOf(fe));
    }

    /// <summary>M1' **推了镜像**：OffsetX 就是当时的 `RenderSize.Width`。</summary>
    internal static void M1Mirror(object fe, double m11, double m22, double offsetX, double offsetY)
    {
        if (!IsRtlTextBlock(fe)) return;
        Emit("M1' **推了镜像** MatrixTransform M11=" + m11 + " M22=" + m22
             + " **OffsetX=" + offsetX + "** OffsetY=" + offsetY
             + "（OffsetX 应当 == 当时 RenderSize.Width）当时 " + FeOf(fe));
    }

    /// <summary>M1'' **没推镜像**（返回 null）。</summary>
    internal static void M1NoMirror(object fe)
    {
        if (!IsRtlTextBlock(fe)) return;
        Emit("M1'' **没推镜像**（return null）" + FeOf(fe));
    }

    /// <summary>M2 `SetLayoutOffset` 里 `GetFlowDirectionTransform()` 之后（**视觉变换的真正组装点**）。</summary>
    internal static void M2SetLayoutOffset(object fe, object offset, object oldRenderSize, object additionalTransform)
    {
        if (!IsRtlTextBlock(fe)) return;
        string off = "(?)", old = "(?)";
        try
        {
            System.Windows.Vector v = (System.Windows.Vector)offset;
            off = v.X.ToString("0.###") + "," + v.Y.ToString("0.###");
        }
        catch (System.Exception) { }
        try
        {
            System.Windows.Size s = (System.Windows.Size)oldRenderSize;
            old = s.Width.ToString("0.###") + "x" + s.Height.ToString("0.###");
        }
        catch (System.Exception) { }
        Emit("M2 **SetLayoutOffset** 传入 offset=(" + off + ") oldRenderSize=" + old
             + "｜additionalTransform=" + XfOf(additionalTransform) + "｜" + FeOf(fe));
    }

    /// <summary>M3 `InternalSetLayoutTransform` 里 `fe?.GetFlowDirectionTransform()` 之后（**推给视觉树的那条路**）。</summary>
    internal static void M3InternalSet(object element, object additionalTransform)
    {
        if (!IsRtlTextBlock(element)) return;
        Emit("M3 **InternalSetLayoutTransform** element=" + FeOf(element)
             + "｜additionalTransform=" + XfOf(additionalTransform));
    }

    /// <summary>M4 `GetLayoutClip` 里的 `rtlMirror`（裁剪路径）。</summary>
    internal static void M4LayoutClip(object fe, object rtlMirror)
    {
        if (!IsRtlTextBlock(fe)) return;
        Emit("M4 **GetLayoutClip** 的 rtlMirror=" + XfOf(rtlMirror) + "｜" + FeOf(fe));
    }

    /// <summary>M5 `ApplyMirrorTransform(parentFD, thisFD)` 的方向判定（**签名里没有 offsetX**）。</summary>
    internal static void M5ApplyMirrorTransform(object parentFD, object thisFD, bool result)
    {
        if (!s_enabled) return;
        string p = "", t = "";
        try { p = parentFD.ToString(); } catch (System.Exception) { p = "(?)"; }
        try { t = thisFD.ToString(); } catch (System.Exception) { t = "(?)"; }
        if (t != "RightToLeft" && p != "RightToLeft") return;      // 非 RTL 相关的不打
        Emit("M5 ApplyMirrorTransform(parentFD=" + p + ", thisFD=" + t + ") ⇒ " + result);
    }

    /// <summary>M6 `TextBlock.OnRender` 入口（**宿主侧**读数，用来与上面几格并排）。</summary>
    internal static void M6OnRender(object textBlock)
    {
        if (!IsRtlTextBlock(textBlock)) return;
        Emit("M6 **TextBlock.OnRender** 入口 " + FeOf(textBlock));
    }
}

'''

FE_CLASS_ANCHOR = '    public partial class FrameworkElement : UIElement, IFrameworkInputElement, ISupportInitialize, IHaveResources, IQueryAmbient\n'

# ── 【W48D · `D-G56`】插桩类的插入点**必须**在「本类型的 doc 注释 ＋ 属性块」**之前** ─────────
#   同族第二例（第一例在 `patch-windowsbase-dpvalue-trace.py`）：原锚 = 类声明行，`EDITS` 把
#   `TRACE_CLASS` 拼在锚行**前面** ⇒ 横幅落进 `[StyleTypedProperty]`/`[XmlLangProperty]`/
#   `[UsableDuringInitialization]` 与类声明之间 ⇒ 三条属性归属变成 `WpfLinuxMirrorTrace`。
#   （现场读数：修前 `[NS] ATTRCOUNT FrameworkElement=1`，修后 = 4 ⇒ 三条回到类身上。）
#   现锚 = 本类型 **doc 注释的首两行**（实测上游出现 **1** 次）⇒「doc ＋ 属性块 ＋ 类声明」紧贴如上游。
#   ⚠️ `TextBlock.Linux.cs` 那一路**不插类**（只插 `OnRender` 探针、且用全限定名
#      `System.Windows.WpfLinuxMirrorTrace`）⇒ 它的插入点无需改；但判据①对该文件**照样跑**。
FE_TYPE_HEAD_ANCHOR = ('    /// <summary>\n'
                       '    ///     The base object for the Frameworks\n')

FE_MIRROR_ANCHOR = '''        private Transform GetFlowDirectionTransform()
        {
            if (!BypassLayoutPolicies && ShouldApplyMirrorTransform(this)) //Window applies its own mirror
            {
                return new MatrixTransform(-1.0, 0.0, 0.0, 1.0, RenderSize.Width, 0.0);
            }

            return null;
        }
'''

FE_SETLAYOUTOFFSET_ANCHOR = '                Transform additionalTransform = GetFlowDirectionTransform(); //rtl\n'

FE_INTERNALSET_ANCHOR = '            Transform additionalTransform = (fe?.GetFlowDirectionTransform()); //rtl\n'

FE_LAYOUTCLIP_ANCHOR = '                Transform rtlMirror = GetFlowDirectionTransform();\n'

FE_APPLYMIRROR_ANCHOR = '''        internal static bool ApplyMirrorTransform(FlowDirection parentFD, FlowDirection thisFD)
        {
            return ((parentFD == FlowDirection.LeftToRight && thisFD == FlowDirection.RightToLeft) ||
                    (parentFD == FlowDirection.RightToLeft && thisFD == FlowDirection.LeftToRight));
        }
'''

TB_ONRENDER_ANCHOR = '''        protected sealed override void OnRender(DrawingContext ctx)
        {
'''

FE_EDITS = [
    ("M0 插桩类本体（**插在 doc 注释之前** ⇒ 属性块＋类声明保持紧贴，`D-G56`）",
     FE_TYPE_HEAD_ANCHOR, TRACE_CLASS + FE_TYPE_HEAD_ANCHOR),
    ("M1 GetFlowDirectionTransform", FE_MIRROR_ANCHOR, '''        private Transform GetFlowDirectionTransform()
        {
            // ── T1c M1（只读插桩）：进入本方法（只对 TextBlock+RTL 打）──
            WpfLinuxMirrorTrace.M1Entry(this);

            if (!BypassLayoutPolicies && ShouldApplyMirrorTransform(this)) //Window applies its own mirror
            {
                // ── T1c M1'：**推了镜像**（OffsetX = 当时 RenderSize.Width；实参惰性：只传标量）──
                WpfLinuxMirrorTrace.M1Mirror(this, -1.0, 1.0, RenderSize.Width, 0.0);
                return new MatrixTransform(-1.0, 0.0, 0.0, 1.0, RenderSize.Width, 0.0);
            }

            // ── T1c M1''：没推镜像 ──
            WpfLinuxMirrorTrace.M1NoMirror(this);
            return null;
        }
'''),
    ("M2 SetLayoutOffset 组装点", FE_SETLAYOUTOFFSET_ANCHOR, '''                Transform additionalTransform = GetFlowDirectionTransform(); //rtl
                // ── T1c M2（只读插桩）：**视觉变换的真正组装点**（offset/oldRenderSize/镜像三者并排）──
                WpfLinuxMirrorTrace.M2SetLayoutOffset(this, offset, oldRenderSize, additionalTransform);
'''),
    ("M3 InternalSetLayoutTransform 推给视觉树", FE_INTERNALSET_ANCHOR, '''            Transform additionalTransform = (fe?.GetFlowDirectionTransform()); //rtl
            // ── T1c M3（只读插桩）：推给视觉树那条路 ──
            WpfLinuxMirrorTrace.M3InternalSet(element, additionalTransform);
'''),
    ("M4 GetLayoutClip 的 rtlMirror", FE_LAYOUTCLIP_ANCHOR, '''                Transform rtlMirror = GetFlowDirectionTransform();
                // ── T1c M4（只读插桩）：裁剪路径用的镜像 ──
                WpfLinuxMirrorTrace.M4LayoutClip(this, rtlMirror);
'''),
    ("M5 ApplyMirrorTransform 方向判定（**纯插入**：probe 在 return 之前）", FE_APPLYMIRROR_ANCHOR, '''        internal static bool ApplyMirrorTransform(FlowDirection parentFD, FlowDirection thisFD)
        {
            // ── T1c M5（只读插桩）：方向判定本身（**签名里没有 offsetX**；probe 是纯插入，return 逐字未动）──
            WpfLinuxMirrorTrace.M5ApplyMirrorTransform(parentFD, thisFD,
                (parentFD == FlowDirection.LeftToRight && thisFD == FlowDirection.RightToLeft) ||
                (parentFD == FlowDirection.RightToLeft && thisFD == FlowDirection.LeftToRight));
            return ((parentFD == FlowDirection.LeftToRight && thisFD == FlowDirection.RightToLeft) ||
                    (parentFD == FlowDirection.RightToLeft && thisFD == FlowDirection.LeftToRight));
        }
'''),
]

TB_EDITS = [
    ("M6 TextBlock.OnRender 入口", TB_ONRENDER_ANCHOR, '''        protected sealed override void OnRender(DrawingContext ctx)
        {
            // ── T1c M6（只读插桩）：**宿主侧**读数（只对 TextBlock+RTL 打）──
            System.Windows.WpfLinuxMirrorTrace.M6OnRender(this);
'''),
]

TARGETS = [
    (FE_REL, "FrameworkElement.Linux.cs", FE_EDITS),
    (TB_REL, "TextBlock.Linux.cs", TB_EDITS),
]

# ── 【W48D · `D-G56`】防复发判据（跟 `--check` 一起跑；两极化各自实测过，见 `W48D-report.md`）─────
#   ① 文件级：「任何 `[Attr]` 属性行的**下一个非空行**都不许是插桩横幅」——修前
#      `FrameworkElement.Linux.cs:103 [UsableDuringInitialization(true)]` 的下一非空行是
#      `:104 // =====` ⇒ **红**。
#   ② 类级：「该生成件里被插类的类型，其**紧贴**属性行数 ≥ 下限」——修前 `FrameworkElement` = **0**、
#      修后 = **3**（`[StyleTypedProperty]`/`[XmlLangProperty]`/`[UsableDuringInitialization]`）。
ATTR_LINE_RE = re.compile(r'^\s*\[[^\]]*\]\s*$')
BANNER_LINE_RE = re.compile(r'^\s*//\s*(T1c\b|=+\s*$)')
#   gen_name → (类声明行, 紧贴属性行下限)｜`None` = 该生成件**不插类**（只跑判据①）
TYPE_ATTR_FLOOR = {
    "FrameworkElement.Linux.cs": (FE_CLASS_ANCHOR.rstrip("\n"), 3),
    "TextBlock.Linux.cs": None,
}


def guard_relapse(gen_name, full):
    """返回 (失败说明列表, 紧贴属性行数或 None)。空列表 = 该生成件的判据都过。"""
    fails = []
    lines = full.splitlines()
    for i, ln in enumerate(lines):
        if not ATTR_LINE_RE.match(ln):
            continue
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j < len(lines) and BANNER_LINE_RE.match(lines[j]):
            fails.append("判据① 属性行与类声明之间出现插入横幅：%s :%d %s ⇒ 下一非空行 :%d 是 %s"
                         % (gen_name, i + 1, repr(ln.strip()[:48]), j + 1,
                            repr(lines[j].strip()[:32])))
    n = None
    spec = TYPE_ATTR_FLOOR.get(gen_name)
    if spec:
        decl, floor = spec
        if decl not in lines:
            fails.append("判据② %s 里找不到类声明行：%s" % (gen_name, repr(decl)))
        else:
            k = lines.index(decl) - 1
            n = 0
            while k >= 0 and ATTR_LINE_RE.match(lines[k]):
                n += 1
                k -= 1
            if n < floor:
                fails.append("判据② %s 的 `%s` 紧贴属性行 = %d < %d（属性块被顶走了？）"
                             % (gen_name, decl.strip().split()[3], n, floor))
    return fails, n


REQUIRED = {
    "FrameworkElement.Linux.cs": [
        ("插桩类存在", "internal static class WpfLinuxMirrorTrace"),
        ("开关", 'private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";'),
        ("有界 200 行", "private const int MaxLines = 200;"),
        ("触顶看得见（L12）", "**预算用尽**（镜像读数 MaxLines="),
        ("只对 TextBlock+RTL（**先按类型名过滤，不读 DP**）", 'if (tn != "TextBlock") return false;'),
        ("M1 入口", "WpfLinuxMirrorTrace.M1Entry(this);"),
        ("M1' 推镜像（**实参惰性**：传标量，不在调用点 new）",
         "WpfLinuxMirrorTrace.M1Mirror(this, -1.0, 1.0, RenderSize.Width, 0.0);"),
        ("M1'' 没推镜像", "WpfLinuxMirrorTrace.M1NoMirror(this);"),
        ("M2 组装点（offset/oldRenderSize/镜像并排）",
         "WpfLinuxMirrorTrace.M2SetLayoutOffset(this, offset, oldRenderSize, additionalTransform);"),
        ("M3 推给视觉树", "WpfLinuxMirrorTrace.M3InternalSet(element, additionalTransform);"),
        ("M4 裁剪路径", "WpfLinuxMirrorTrace.M4LayoutClip(this, rtlMirror);"),
        ("M5 方向判定（probe 在 return 之前 ⇒ **纯插入**）",
         "WpfLinuxMirrorTrace.M5ApplyMirrorTransform(parentFD, thisFD,"),
        ("上游镜像那条语句逐字未动", "return new MatrixTransform(-1.0, 0.0, 0.0, 1.0, RenderSize.Width, 0.0);"),
        ("上游方向判定那条语句逐字未动", "return ((parentFD == FlowDirection.LeftToRight && thisFD == FlowDirection.RightToLeft) ||"),
    ],
    "TextBlock.Linux.cs": [
        ("M6 调用点（跨命名空间全限定）", "System.Windows.WpfLinuxMirrorTrace.M6OnRender(this);"),
        ("上游 OnRender 头逐字未动", "        protected sealed override void OnRender(DrawingContext ctx)"),
    ],
}

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-mirror-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（RTL 镜像：实推变换 vs 报告口径）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：`GetFlowDirectionTransform()` 报的是 `Matrix(−1,0,0,1,RenderSize.Width,0)`，
//   而渲染实测等效变换是 `R(y)=42−y`（镜在元素左边缘）⇒ 两者**不是同一个**（差 44.3 px）。
//   本批把"**实际推给视觉树的那个变换**"（`SetLayoutOffset`/`InternalSetLayoutTransform`/`GetLayoutClip` 三条路）
//   与 `GetFlowDirectionTransform()` 的返回值**并排**打出来。
//   过滤：先按**类型名**命中 TextBlock（不读 DP），再读 `FlowDirection` 要求 RightToLeft。
//   缺省关、≤200 行、触顶打一行（L12）、实参全部惰性（ARGSHAPE）。
"""

MARKER_BEGIN = ("  <!-- ==== T1c RTL 镜像读数：实推变换 vs GetFlowDirectionTransform"
                "（patch-presentationframework-mirror-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c RTL 镜像读数 结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _build_one(rel, gen_name, edits):
    up = os.path.join(ROOT, rel)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in edits:
        n = _count(text, anchor)
        print(f"[锚点] {gen_name} {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print(f"[失败] {gen_name} 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in edits:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] {gen_name}：`throw` 条数变了 {up_throws} → {out_throws}")
        return None, 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print(f"[失败] {gen_name}：大括号盈亏变了")
        return None, 1
    full = HEADER.replace("{rel}", rel).replace("{n}", str(len(edits))) + out
    for name, needle in REQUIRED.get(gen_name, []):
        if needle not in full:
            print(f"[失败] {gen_name} 缺少结构断言：{name}")
            return None, 1

    # 【W48D · `D-G56`】防复发判据（跟着 `--check` 一起跑）
    gfails, gn = guard_relapse(gen_name, full)
    for m in gfails:
        print(f"[失败] `D-G56` 防复发判据：{m}")
    if gfails:
        print(f"[失败] {gen_name}：属性块被插桩横幅打断 ⇒ 属性会挂到插桩类上（`D-G56`）。**不做任何静默降级**。")
        return None, 1
    gfloor = TYPE_ATTR_FLOOR.get(gen_name)
    print(f"[断言] `D-G56` 防复发判据（{gen_name}）：① 属性块与类声明之间无插入横幅 ✓；"
          + (f"② 紧贴属性行 = {gn}（要求 ≥ {gfloor[1]}）✓" if gfloor else "② 本件不插类（不适用）"))

    print(f"[断言] {gen_name}：锚点 {len(edits)} 处各 1 次；`throw` {up_throws}=={out_throws}；"
          f"大括号 {_count(out, '{')}/{_count(out, '}')}；结构断言 "
          f"{len(REQUIRED[gen_name])}/{len(REQUIRED[gen_name])} 全中；"
          f"行数 {len(text.splitlines())} → {len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")
    return full, 0


def _build_all():
    built = []
    for rel, gen, edits in TARGETS:
        full, rc = _build_one(rel, gen, edits)
        if rc:
            return None, 1
        built.append((gen, full, edits, rel))
    return built, 0


def prove():
    rc_all = 0
    for rel, gen_name, edits in TARGETS:
        up = os.path.join(ROOT, rel)
        gen = os.path.join(PF_DIR, gen_name)
        with open(up, encoding="utf-8-sig") as f:
            text = f.read()
        head = HEADER.replace("{rel}", rel).replace("{n}", str(len(edits)))
        body, where = None, None
        if os.path.exists(gen):
            with open(gen, encoding="utf-8") as f:
                raw = f.read()
            if raw.startswith(head):
                body = raw[len(head):]
                where = f"落盘生成物 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}"
            else:
                print(f"[注意] {gen_name} 文件头与当前脚本不一致（上一版产的）⇒ 退化为**内存**证明")
        if body is None:
            full, rc = _build_one(rel, gen_name, edits)
            if rc:
                rc_all = 1
                continue
            body = full[len(head):]
            where = f"**内存**（{gen_name} 尚未产出/已过时）"
        cur = body
        ok = True
        for name, anchor, repl in reversed(edits):
            n = _count(cur, repl)
            if n != 1:
                print(f"[失败] {gen_name} 逆向回代 {name}：命中 {n} 次（要求 1）")
                ok = False
                break
            cur = cur.replace(repl, anchor, 1)
        if not ok or cur != text:
            print(f"[失败] {gen_name} 逆代后与上游**不一致** ⇒ 别信这份生成物。")
            rc_all = 1
            continue
        print(f"[① 只插入] {gen_name}：取自 {where}")
        print(f"[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256={_sha(cur)}（== 上游 {_sha(text)}）")
    return rc_all


def generate(check_only):
    built, rc = _build_all()
    if rc:
        return rc

    stale = []
    for gen_name, full, _e, _r in built:
        gen = os.path.join(PF_DIR, gen_name)
        cur = None
        if os.path.exists(gen):
            with open(gen, encoding="utf-8") as f:
                cur = f.read()
        if cur != full:
            stale.append(gen_name)
            if not check_only:
                with open(gen, "w", encoding="utf-8") as f:
                    f.write(full)
                print(f"[生成] src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}：已从上游重生成 sha256={_sha(full)}")
        else:
            print(f"[生成] src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}：内容已是最新（未重写）sha256={_sha(full)}")

    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        if check_only and stale:
            print(f"[检查] 生成物过时（需要重新生成）：{', '.join(stale)} ⇒ rc=1")
            return 1
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1
    lines = [MARKER_BEGIN, "  <ItemGroup>"]
    for rel, gen_name, _e in TARGETS:
        lines.append(f'    <Compile Remove="$(UpstreamWpfRoot){rel}" />')
        lines.append(f'    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{gen_name}" />')
    lines.append("  </ItemGroup>")
    lines.append(MARKER_END)
    csproj = csproj.replace(anchor, "\n".join(lines) + "\n" + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 {len(TARGETS) * 2} 行到 {os.path.relpath(CSPROJ, ROOT)}")
    print("[注意] `src/Linux/build/port-lib.py PresentationFramework` 会整份重写 csproj ⇒ 必须重跑本脚本。")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--prove", action="store_true", help="「只插入」机械证明（只读）")
    args = ap.parse_args()
    if args.prove:
        return prove()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
