#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WindowsBase `Dispatcher` **托管侧出队读数**（只读插桩）—— 生成 + 接线 + 自检。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-msgflow.py --check   # 只读检查（不改任何文件）
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-msgflow.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-msgflow.py --prove   # 「只插入」机械证明（只读）

为什么打这一格
--------------
症状：**打字到不了 TextBox**（键到了、字符没了、不抛异常）。

PresentationCore 那组探针（`patch-presentationcore-inputtrace.py`）从
`HwndSource.OnPreprocessMessage` 的 WM_CHAR 分支往下看；但那条链**之前**还有一个问题没人回答：

    "那条 WM_CHAR，**托管侧的消息泵**到底有没有把它从队列里取出来？"

这一格就钉在 `Dispatcher.TranslateAndDispatchMessage(ref MSG)`（上游
`WindowsBase/System/Windows/Threading/Dispatcher.cs:2195`）——**托管侧第一个看见 MSG 的地方**
（调用点 `PushFrameImpl` 的 `GetMessage → TranslateAndDispatchMessage`，:2064-2067）。
读它能把"没字"劈成三块，且**互斥**：

    ① 出队读数里**根本没有** 0x102/0x106     ⇒ 消息在**更上游**丢的（原生队列/X11 侧）→ 转 M7b 的原生侧
    ② 有出队、但 `handled=True`                ⇒ 被**线程预处理 filter** 吃掉（PC 侧重放即可看到是谁）
    ③ 有出队、`handled=False`、也交给了
       `DispatchMessageW`                      ⇒ 消息**进了 WndProc 路径** ⇒ 问题在 PC 侧的过滤/翻译/TextInput

开关（两个取「**或**」，任一为真即开；都未设/空白 ⇒ **一行不打**）
--------------------------------------------------------------
    WPF_LINUX_INPUT_TRACE=1     与 PresentationCore 那组探针**同一个开关**（想一起开就用它）
    WPF_LINUX_MSGFLOW_TRACE=1   **只**开这一格（PC 侧不动 ⇒ 两股输出不混在一起）

⚠️ `WPF_LINUX_MSGFLOW_TRACE` **不是新名字**：原生侧 `win32_msg.c` 已经在用它打队列读数
（`push WM_CHAR 入队` / `pop api=…` / `PeekMessageW(PM_NOREMOVE)` 告警）；原生解析口径是
「**非空且不是字面 0** 即开」。本插桩对**这个名字**采用**同一口径**（`IsOnNativeSwitch`），
对 `WPF_LINUX_INPUT_TRACE` 用 PC 侧那份严格解析（`1`/`true`/`on`/`yes`）。
为什么必须对齐：否则 `=2`/`=y` 会出现"**原生在打、托管一行没有**"的假象，被误读成"托管泵没取消息"。

开哪一路、两个环境变量的**原始值**，都打在**第一行**的 `via=` / `原始读数` 里（读数自带出处，
省掉"到底哪个开关生效了"这种扯皮）。

有界
----
    MaxLines      = 200   每进程**硬上限**（含首行 banner）
    MaxInputLines = 150   键盘/字符消息（0x0100-0x0109）明细
    MaxOtherLines =  20   其它消息明细（够证明"泵在跑、在出队什么"即可）+ 1 行封顶通知
    MaxRollupLines=   5   每 200 条出队打一行统计（**总量永远看得到**，即使明细被上限掐掉）
`LineCount` = **实际写出去的行数**（只在真写成功时自增；这是上一轮踩过的坑：记"调用次数"会骗人）。

只读
----
四处调用点全是**独立语句**（插在原有的两条语句**之间**，不替换、不包裹），不改控制流、不改返回值、
不改异常结构。机械证明：`--prove`（把插桩**逆序**摘掉后与上游 sha256 **逐字节相同**）。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

UP_REL = "src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/Dispatcher.cs"
WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")
GEN = os.path.join(WB_DIR, "Dispatcher.Linux.cs")

# --------------------------------------------------------------------------------------
#  插桩类本体（原样插到 `public sealed class Dispatcher` **之前**，同一 namespace 里）
# --------------------------------------------------------------------------------------
TRACE_CLASS = '''// =====================================================================================
//  T1c 只读插桩：Dispatcher **托管侧出队读数**（缺省关 / 有界 / 只打印）
//
//  为什么：**打字到不了 TextBox**。这一格回答最上游的那个问题 ——
//    "那条 WM_CHAR 到底有没有被**托管侧消息泵**取出来？取出来之后是被线程预处理 filter
//     吃了，还是交给了 TranslateMessage + DispatchMessageW（WndProc 路径）？"
//  开关（取「或」；都未设 ⇒ 一行不打）：WPF_LINUX_INPUT_TRACE=1 或 WPF_LINUX_MSGFLOW_TRACE=1
//  有界：输入消息 ≤150 行、其它消息 ≤20 行、统计 ≤5 行、硬上限 200 行/进程。
//  只打印：调用点全是独立语句，不改控制流、不改返回值。
// =====================================================================================
internal static class WpfLinuxMsgFlowTrace
{
    private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";
    private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";

    private const int MaxLines = 200;        // 硬上限（含 banner）
    private const int MaxInputLines = 150;   // 键盘/字符消息明细
    private const int MaxOtherLines = 20;    // 其它消息明细
    private const int MaxRollupLines = 5;    // 统计行
    private const int RollupEvery = 200;     // 每 N 条出队打一行统计

    private static readonly bool s_enabled;
    private static readonly string s_via;

    private static int s_lines;          // **实际写出去的行数**
        private static int s_budgetNotice;   // L12：触顶只报一次
    private static int s_inputLines;
    private static int s_otherLines;
    private static int s_otherNotice;    // 0/1：封顶通知只打一次
    private static int s_rollups;
    private static int s_dequeued;       // 出队总数
    private static int s_inputSeen;      // 输入消息总数
    private static int s_charSeen;       // WM_CHAR / WM_SYSCHAR 总数
    private static int s_handledCount;   // 被线程预处理判 handled 的总数
    private static int s_dispatchCount;  // 交给 DispatchMessageW 的总数

    static WpfLinuxMsgFlowTrace()
    {
        string a = null, b = null;
        try { a = System.Environment.GetEnvironmentVariable(EnvInputTrace); } catch (System.Exception) { }
        try { b = System.Environment.GetEnvironmentVariable(EnvMsgFlow); } catch (System.Exception) { }
        s_enabled = IsOn(a) || IsOnNativeSwitch(b);
        s_via = IsOn(a) ? EnvInputTrace : (IsOnNativeSwitch(b) ? EnvMsgFlow : "(都未设)");
        if (s_enabled)
        {
            Emit("via=" + s_via + " pid=" + System.Environment.ProcessId
                 + " —— Dispatcher 出队读数（缺省关 / ≤" + MaxLines + " 行 / 只打印）"
                 + " 原始读数 " + EnvInputTrace + "=" + Raw(a) + " " + EnvMsgFlow + "=" + Raw(b));
        }
    }

    /// <summary>未设/空白 ⇒ **false**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
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

    /// <summary>
    /// `WPF_LINUX_MSGFLOW_TRACE` 的语义**必须与原生侧一致**：原生 `win32_msg.c` 用
    /// `(e &amp;&amp; *e &amp;&amp; *e != '0')` ⇒ 「非空且不是字面 0」即开。
    /// 若这里改成严格解析，`=2`/`=y` 这类值会出现"**原生在打、托管一行没有**"的假象，
    /// 而它只是解析口径不同 —— 这种假象会被误读成"托管泵没取消息"。所以对齐。
    /// </summary>
    internal static bool IsOnNativeSwitch(string value)
    {
        if (value == null) return false;
        string v = value.Trim();
        if (v.Length == 0) return false;
        return v != "0";
    }

    /// <summary>原始读数（截断），打在 banner 里：读数自带出处，省掉"到底哪个开关生效"的扯皮。</summary>
    internal static string Raw(string value)
    {
        if (value == null) return "(null)";
        string v = value.Trim();
        if (v.Length == 0) return "(空)";
        if (v.Length > 12) v = v.Substring(0, 12) + "…";
        return "\"" + v + "\"";
    }

    /// <summary>给报告/探针看的读数（== 实际输出行数）。</summary>
    internal static int LineCount { get { return s_lines; } }
    internal static bool Enabled { get { return s_enabled; } }
    internal static string Via { get { return s_via; } }

    private static bool IsInput(int msg) { return (msg >= 0x0100 && msg <= 0x0109); }

    private static string Name(int msg)
    {
        switch (msg)
        {
            case 0x0100: return "WM_KEYDOWN";
            case 0x0101: return "WM_KEYUP";
            case 0x0102: return "WM_CHAR";
            case 0x0103: return "WM_DEADCHAR";
            case 0x0104: return "WM_SYSKEYDOWN";
            case 0x0105: return "WM_SYSKEYUP";
            case 0x0106: return "WM_SYSCHAR";
            case 0x0107: return "WM_SYSDEADCHAR";
            case 0x0108: return "WM_KEYLAST";
            case 0x0109: return "WM_UNICHAR";
            case 0x0006: return "WM_ACTIVATE";
            case 0x0007: return "WM_SETFOCUS";
            case 0x0008: return "WM_KILLFOCUS";
            case 0x000f: return "WM_PAINT";
            case 0x0010: return "WM_CLOSE";
            case 0x0018: return "WM_SHOWWINDOW";
            case 0x0020: return "WM_SETCURSOR";
            case 0x0046: return "WM_WINDOWPOSCHANGING";
            case 0x0047: return "WM_WINDOWPOSCHANGED";
            case 0x0084: return "WM_NCHITTEST";
            case 0x0113: return "WM_TIMER";
            case 0x0200: return "WM_MOUSEMOVE";
            case 0x0201: return "WM_LBUTTONDOWN";
            case 0x0202: return "WM_LBUTTONUP";
            default: return "msg";
        }
    }

    private static string Tag(int msg)
    {
        return "0x" + msg.ToString("x4") + "(" + Name(msg) + ")";
    }

    private static string Hex(System.IntPtr v) { return "0x" + v.ToInt64().ToString("x"); }

    /// <summary>wParam 当码点看（只看合法的、可打印的，别把日志弄脏）。</summary>
    private static string Chr(System.IntPtr wParam)
    {
        long c = wParam.ToInt64();
        if (c < 0x20 || c > 0x10FFFF) return "";
        return " ch=U+" + c.ToString("x4");
    }

    private static void Emit(string message)
    {
        if (!s_enabled) return;
        if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines)   // 硬上限
        {
            if (System.Threading.Interlocked.Exchange(ref s_budgetNotice, 1) == 0)
            {
                try
                {
                    System.Console.Error.WriteLine("[INPUT_TRACE] **预算用尽**（MaxLines=" + MaxLines
                        + "）⇒ 之后不再打印；**「没打」≠「没发生」**：请调大额度或缩小过滤范围");
                    System.Console.Error.Flush();
                }
                catch (System.Exception) { }
            }
            return;
        }
        try
        {
            System.Console.Error.WriteLine("[MSGFLOW_TRACE] " + message);
            System.Console.Error.Flush();
        }
        catch (System.Exception)
        {
            System.Threading.Interlocked.Decrement(ref s_lines);                      // 没写出去就不算一行
        }
    }

    /// <summary>① 出队：`TranslateAndDispatchMessage` **入口**（`GetMessage` 刚把这条 MSG 交出来）。</summary>
    internal static void Dequeued(int msg, System.IntPtr hwnd, System.IntPtr wParam, System.IntPtr lParam)
    {
        if (!s_enabled) return;
        int total = System.Threading.Interlocked.Increment(ref s_dequeued);

        if (IsInput(msg))
        {
            System.Threading.Interlocked.Increment(ref s_inputSeen);
            if (msg == 0x0102 || msg == 0x0106) System.Threading.Interlocked.Increment(ref s_charSeen);
            if (System.Threading.Interlocked.Increment(ref s_inputLines) <= MaxInputLines)
            {
                Emit("① 出队 " + Tag(msg) + " hwnd=" + Hex(hwnd) + " wParam=" + wParam.ToInt64()
                     + Chr(wParam) + " lParam=" + Hex(lParam));
            }
        }
        else if (System.Threading.Interlocked.Increment(ref s_otherLines) <= MaxOtherLines)
        {
            Emit("① 出队(非输入) " + Tag(msg) + " hwnd=" + Hex(hwnd));
        }
        else if (System.Threading.Interlocked.Exchange(ref s_otherNotice, 1) == 0)
        {
            Emit("非输入消息明细已封顶(" + MaxOtherLines + " 行) ⇒ 之后只打输入消息明细 + 每 "
                 + RollupEvery + " 条一行统计（**总量不会丢**）");
        }

        if (total % RollupEvery == 0) Rollup(total);
    }

    /// <summary>② 线程预处理的结果（`ComponentDispatcher.RaiseThreadMessage` 的返回）。</summary>
    internal static void ThreadPreprocessResult(int msg, bool handled)
    {
        if (!s_enabled) return;
        if (handled) System.Threading.Interlocked.Increment(ref s_handledCount);

        // 只对**输入消息**或**被判 handled 的**打（非输入且没 handled 的不重复占行）
        if (!IsInput(msg) && !handled) return;
        if (IsInput(msg) && System.Threading.Interlocked.Increment(ref s_inputLines) > MaxInputLines) return;

        Emit("② 线程预处理 " + Tag(msg) + " handled=" + handled
             + (handled ? " ⇒ **不进** TranslateMessage/DispatchMessageW（被 preprocess filter 吃了）"
                        : " ⇒ 继续走 TranslateMessage + DispatchMessageW"));
    }

    /// <summary>③ 交给 `DispatchMessageW`（即 **WndProc 路径**；HwndSource.InputFilterMessage 就在这条路上）。</summary>
    internal static void DispatchToWndProc(int msg, System.IntPtr hwnd, System.IntPtr wParam)
    {
        if (!s_enabled) return;
        if (!IsInput(msg)) return;              // 非输入消息在这里不再重复打（省行）
        System.Threading.Interlocked.Increment(ref s_dispatchCount);
        if (System.Threading.Interlocked.Increment(ref s_inputLines) > MaxInputLines) return;
        Emit("③ 交给 DispatchMessageW " + Tag(msg) + " hwnd=" + Hex(hwnd) + " wParam=" + wParam.ToInt64());
    }

    private static void Rollup(int total)
    {
        if (System.Threading.Interlocked.Increment(ref s_rollups) > MaxRollupLines) return;
        Emit("统计：出队 " + total + " 条（输入 " + s_inputSeen + "，其中 WM_CHAR/SYSCHAR " + s_charSeen
             + "；线程预处理 handled " + s_handledCount + "；交给 DispatchMessageW " + s_dispatchCount
             + "；已打 " + s_lines + " 行）");
    }
}

'''

# --------------------------------------------------------------------------------------
#  四处锚点（全部要求**上游恰好命中 1 次**，否则报错退出、不静默降级）
# --------------------------------------------------------------------------------------
WB_CLASS_ANCHOR = '    public sealed class Dispatcher\n'

WB_ENTRY_ANCHOR = '''        private void TranslateAndDispatchMessage(ref MSG msg)
        {
            bool handled = false;
'''

WB_RAISE_ANCHOR = '            handled = ComponentDispatcher.RaiseThreadMessage(ref msg);\n'

WB_DISPATCH_ANCHOR = '''                UnsafeNativeMethods.TranslateMessage(ref msg);
                UnsafeNativeMethods.DispatchMessage(ref msg);
'''

EDITS = [
    ("W0 插桩类本体", WB_CLASS_ANCHOR, TRACE_CLASS + WB_CLASS_ANCHOR),
    ("W1 出队入口", WB_ENTRY_ANCHOR, WB_ENTRY_ANCHOR + '''            // ── T1c 只读插桩：**托管侧第一个看见这条 MSG 的地方**（GetMessage 刚交出来）──
            WpfLinuxMsgFlowTrace.Dequeued(msg.message, msg.hwnd, msg.wParam, msg.lParam);

'''),
    ("W2 线程预处理结果", WB_RAISE_ANCHOR, WB_RAISE_ANCHOR + '''            // ── T1c 只读插桩：preprocess filter（HwndSource.OnPreprocessMessage 就在里面）有没有把它吃掉 ──
            WpfLinuxMsgFlowTrace.ThreadPreprocessResult(msg.message, handled);
'''),
    ("W3 交给 DispatchMessageW", WB_DISPATCH_ANCHOR, '''                UnsafeNativeMethods.TranslateMessage(ref msg);
                // ── T1c 只读插桩：这条消息**进了 WndProc 路径**（HwndSource.InputFilterMessage→FilterMessage）──
                WpfLinuxMsgFlowTrace.DispatchToWndProc(msg.message, msg.hwnd, msg.wParam);
                UnsafeNativeMethods.DispatchMessage(ref msg);
'''),
]

REQUIRED = [
    ("插桩类存在", "internal static class WpfLinuxMsgFlowTrace"),
    ("开关名①", 'private const string EnvInputTrace = "WPF_LINUX_INPUT_TRACE";'),
    ("开关名②", 'private const string EnvMsgFlow = "WPF_LINUX_MSGFLOW_TRACE";'),
    ("两开关取或", "s_enabled = IsOn(a) || IsOnNativeSwitch(b);"),
    ("读数自带出处（via= + 原始读数）", 'Emit("via=" + s_via'),
    ("与原生侧同名开关语义对齐", "return v != \"0\";"),
    ("缺省关（纯函数首行）", "if (string.IsNullOrWhiteSpace(value)) return false;"),
    ("有界 200 行", "private const int MaxLines = 200;"),
    ("输入消息单独预算（防被别的消息饿死）", "private const int MaxInputLines = 150;"),
    ("读数 == 实际输出行数（触顶也看得见：走 NoticeBudget）",
     "if (System.Threading.Interlocked.Increment(ref s_lines) > MaxLines)"),
    ("① 出队入口", "WpfLinuxMsgFlowTrace.Dequeued(msg.message, msg.hwnd, msg.wParam, msg.lParam);"),
    ("② 预处理结果", "WpfLinuxMsgFlowTrace.ThreadPreprocessResult(msg.message, handled);"),
    ("③ WndProc 路径", "WpfLinuxMsgFlowTrace.DispatchToWndProc(msg.message, msg.hwnd, msg.wParam);"),
    ("输出前缀可分离", '"[MSGFLOW_TRACE] "'),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-windowsbase-msgflow.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（托管侧出队读数）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。这一格钉在 `Dispatcher.TranslateAndDispatchMessage`——
//   **托管侧第一个看见 MSG 的地方**——把"没字"劈成互斥三块：
//     ① 根本没看到 0x102/0x106 ⇒ 消息在更上游（原生队列/X11）就丢了 → 转原生侧
//     ② 看到了但 handled=True  ⇒ 被线程预处理 filter 吃掉 → 看 PC 侧探针是谁吃的
//     ③ 看到了、handled=False、交给了 DispatchMessageW ⇒ 进了 WndProc 路径 → 问题在 PC 侧翻译/TextInput
//   开关**缺省关**：WPF_LINUX_INPUT_TRACE=1 或 WPF_LINUX_MSGFLOW_TRACE=1（**取或**，开哪路打在首行 `via=`）；
//   **有界**（≤200 行/进程；输入消息单独 150 行预算，不被别的消息饿死）；
//   **只打印**（三处调用点都是独立语句，不改控制流、不改返回值）。
"""

MARKER_BEGIN = "  <!-- ==== T1c 输入链读数：Dispatcher 出队插桩（patch-windowsbase-msgflow.py 注入）==== -->"
MARKER_END = "  <!-- ==== T1c 输入链读数结束 ==== -->"


def _count(haystack, needle):
    return haystack.count(needle)


def _sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _build(check_only=False):
    """读上游 → 锚点自检 → 只插入 → 机械断言。返回 (generated_text 或 None, rc)。"""
    upstream = os.path.join(ROOT, UP_REL)
    if not os.path.exists(upstream):
        print(f"[失败] 找不到上游 {upstream}")
        return None, 1
    with open(upstream, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] Dispatcher {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}")
        return None, 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print(f"[失败] 大括号盈亏变化（只读插桩不该改结构）："
              f"上游 {_count(text, '{')}/{_count(text, '}')} → 生成物 {_count(out, '{')}/{_count(out, '}')}")
        return None, 1
    print(f"[断言] 大括号盈亏一致：{_count(out, '{')} / {_count(out, '}')}")
    print(f"[断言] `throw` {up_throws}=={out_throws}；行数 {len(text.splitlines())} → "
          f"{len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")

    full = HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS))) + out
    for name, needle in REQUIRED:
        if needle not in full:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中（含「缺省关」「有界」「两开关取或」）")
    return full, 0


def _header():
    return HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS)))


def prove():
    """「只插入」机械证明：把插桩**逆序**摘掉后必须与上游 sha256 逐字节相同。"""
    upstream = os.path.join(ROOT, UP_REL)
    with open(upstream, encoding="utf-8-sig") as f:
        text = f.read()

    body = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            body = f.read()
        head = _header()
        if not body.startswith(head):
            print(f"[失败] {os.path.relpath(GEN, ROOT)} 不是本脚本产出的（文件头对不上）")
            return 1
        body = body[len(head):]                      # 摘掉文件头，只留「上游 + 插桩」
        where = f"落盘生成物 {os.path.relpath(GEN, ROOT)}"
    else:
        full, rc = _build(check_only=True)
        if rc:
            return 1
        body = full[len(_header()):]
        where = "**内存**（生成物尚未产出；重放后再跑一次才是对落盘件取的真值）"

    cur = body
    for name, anchor, repl in reversed(EDITS):
        n = _count(cur, repl)
        if n != 1:
            print(f"[失败] 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）⇒ 生成物被手改过？")
            return 1
        cur = cur.replace(repl, anchor, 1)

    if cur != text:
        print("[失败] 摘掉插桩后与上游**不一致** ⇒ 这不是「只插入」，别信。")
        return 1
    print(f"[① 只插入] 取自 {where}")
    print(f"[① 只插入] 摘掉插桩后与上游**逐字节相同** ✓ sha256={_sha(cur)}")
    print(f"[① 只插入] 上游 sha256={_sha(text)}（两条相同即证明：控制流/返回值一个字节没动）")
    return 0


def generate(check_only):
    full, rc = _build(check_only=check_only)
    if rc:
        return rc

    up_to_date = True
    cur = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            cur = f.read()
    if cur != full:
        up_to_date = False
        if not check_only:
            with open(GEN, "w", encoding="utf-8") as f:
                f.write(full)
            print(f"[生成] {os.path.relpath(GEN, ROOT)}：已从上游重生成（只读插桩）sha256={_sha(full)}")
    else:
        print(f"[生成] {os.path.relpath(GEN, ROOT)}：内容已是最新（未重写）sha256={_sha(full)}")

    # ---- csproj 接线（幂等；只加自己那一块）----
    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
    elif check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1
    else:
        anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
        if _count(csproj, anchor) != 1:
            print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
            return 1
        block = (MARKER_BEGIN + "\n"
                 "  <ItemGroup>\n"
                 f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL}" />\n'
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Dispatcher.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 落在上游 Include 之后）")

    # ---- 假绿防线：生成物在、接线丢了 ⇒ 上游那份会被编译、探针**静默消失** ----
    print("[注意] `src/Linux/build/port-lib.py WindowsBase` 会**整份重写** csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本（不带 --check）。接线丢失**不会报编译错**，只会一行都不打 ⇒ 别把空输出读成「没消息」。")

    if check_only:
        print("[检查] " + ("生成物与接线都已就位且与上游同步" if up_to_date
                           else "生成物缺失/与上游不同步（需要重新生成）"))
        return 0 if up_to_date else 1

    print("\n下一步（**不要在这里重建 WindowsBase 权威产物**；编到 /tmp 做闸门）：")
    print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj -m:1 --nologo \\")
    print("      -getItem:Compile | grep -i 'Threading/Dispatcher'")
    print("  # 期望只剩 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Dispatcher.Linux.cs（上游那条被 Remove 掉）")
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
