#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""波 51 · `TASK-0108` 的 `P3`：**托管侧"尺寸提示声明变了"的告示**（生成式补丁，幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-window-minmax-notify.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-window-minmax-notify.py           # 生成 + 接线

【它补的是哪一半（W101A 交下来的那一格）】
  `D-G88`（"运行期改尺寸提示到不了 X"）的修法 `P1`/`P2`/`P4` 已在 `win32_core.c` 落地
  （幂等发布 ＋ 尺寸真变补一拍 ＋ 重入闸）⇒ 4 个红格里 3 个转绿。**剩下一格同一根因**：
  **"应用改了声明但当时没有 resize"**（改**大**上限、或声明时窗口已经那么大）⇒
  上游 `Window` 自己**什么都不做**：

      Window.cs  OnMaxHeightChanged / OnMaxWidthChanged
        if (maxHeight < logicalSize.Y) { ... UpdateHwndSizeOnWidthHeightChange(...); }
        else { // no need to do anything. ... }

  ⇒ 我方**没有触发器** ⇒ X 侧的 `WM_NORMAL_HINTS` 要等"下一次窗口活动"才更新
  （W101A §4.2/§7.3 实测：`W3-DECLARE` 应用侧 `469x365` 而 X 侧 `<缺席>`；`A3 W1-REVERT` 期望
  `667x500` 实际停在 `521 by 417`；`W81AWindowProbe` 的 `late` 窗同样 `<absent>`）。

【补法（**只插入，不改任何既有语句**）】
  在这 4 个 DP 元数据回调的**实例方法末端**（`OnMin/Max{Height,Width}Changed`）各插一行
  `WpfLinuxWindowHints.NotifyMinMaxChanged(this);` ⇒ 无论"调高/调低"都通知 shim 重问一次。
  **为什么挂在这里而不是"入册"**：这 4 个回调**就是**上游用来处理这 4 个 DP 的唯一回调
  （`Window..cctor` `:46-50` 用 `OverrideMetadata` 注册）⇒ 不需要任何"每个 Window 自动入册"的钩子
  （W101A 实测 `Loaded` 类处理器 6 s 一次都没触发；`AddValueChanged` 虽可用但要按**实例**建强引用）。
  ⚠️ **已排除**：`OverrideMetadata(typeof(Window), …)` —— W101A 实测它会抢掉 `Window` 的**唯一** metadata
  槽位、毒死 `Window..cctor`（`ArgumentException: PropertyMetadata is already registered for type
  'Window'`，`DependencyProperty.cs:566` / `Window.cs:50`）⇒ `rc=134`。**别再试这条。**

【为什么"发一条私有消息"而不是"新导出一个 P/Invoke"（机械证据）】
  `PresentationFramework.dll` **没有** `DllImportResolver`：
      grep -c SetDllImportResolver   PF=0   WindowsBase=1  PresentationCore=1  UIAutomationTypes=1
  （解析器是按**程序集**注册的）⇒ 在 PF 里新声明的 `[DllImport("…")]` **落不到**
  `libwpfwin32.so`（`DllNotFoundException`）。而 `MS.Win32.UnsafeNativeMethods` 的那批
  `user32.dll` P/Invoke **声明在 WindowsBase**、经 IVT 被 PF 使用（上游 `Window.cs:251` 自己就这么调
  `WM_SYSCOMMAND`）⇒ 走它既不需要动 resolver，也是**已经在跑**的路径。
  ⇒ 消息号 `0xFF00`（`WM_APP + 0x7F00`）与 native `win32_internal.h` 的
  `WPF_LINUX_WM_HINTS_CHANGED` **逐字对齐**；shim 在 `wpf_dispatch_to_window()`（`SendMessageW` 与
  `DispatchMessageW` 的共同落点）拦下它，转调**已有的**幂等发布入口 `wpf_hints_publish()`
  （复用 `P1` 的 `hints_pub_*` 缓存与 `P4` 的重入闸 —— **没有第二套发布路径**），且不再进托管窗口过程。

【注意】
  · 本脚本**无参运行 = 生成 + 接线**（家族约定：`patch-*` 无参必须真做事，否则
    `integration-wave.sh` 的空操作护栏会判红）。
  · 生成物 = **上游逐字复制 + 5 处插入**（4 个调用点 + 1 个嵌套帮助类）；锚点找不到 / 命中数 ≠ 1
    ⇒ **报错退出**（绝不静默产出未打补丁的副本）。
  · `port-lib.py PresentationFramework` 会**整份重写** csproj ⇒ 接线必须每次都重放；本脚本自带
    `MARKER_BEGIN` 幂等接线（与 `patch-presentationframework-xamlaccess.py` 同款），
    `src/Linux/build/integration-wave.sh` 的 `patch-presentation*` 通配**每波都会重放它**。
  · ⚠️ 本脚本**不改任何既有语句、不删任何守卫**：`D-G83` 的修法语义与
    `DefWindowProcW` 的 `WM_GETMINMAXINFO` no-op 一个字节都不在射程内。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
GEN = os.path.join(PF_DIR, "Window.Linux.cs")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs"
UP = os.path.join(ROOT, UP_REL)

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 补丁：Window 的 min/max 声明告示（TASK-0108 P3）=== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux 补丁结束（TASK-0108 P3）==== -->"

BANNER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-window-minmax-notify.py **生成**，不要手改。
//
// 内容 = 上游 `{up}` 逐字复制 + 5 处**插入**：
//   ①..④ 4 个 DP 元数据回调的实例方法末端（`OnMinHeightChanged`／`OnMaxHeightChanged`／
//         `OnMinWidthChanged`／`OnMaxWidthChanged`）各插一行 `WpfLinuxWindowHints.NotifyMinMaxChanged(this);`
//   ⑤    在 `_OnMinHeightChanged` 前插入嵌套类 `WpfLinuxWindowHints`（发私有告示消息，见类内注释）
//
// 为什么打（波 51 · `TASK-0108` 的 `P3`）：应用在运行期改 `Min/MaxWidth/Height` 而**不伴随 resize**
//   时上游什么都不做 ⇒ 我方没有触发器 ⇒ X 侧的尺寸提示要等"下一次窗口活动"（W101A 实测 3 个现场）。
// 纪律：**只插入**——不改任何既有语句、不删任何守卫/断言；`D-G83` 的修法语义与 `DefWindowProcW` 的
//   `WM_GETMINMAXINFO` no-op 不在射程内。锚点找不到 / 命中数 ≠ 1 ⇒ 生成器报错退出（不静默产出）。
// 每次运行该脚本都会从上游重读重生成。

"""

# ── ⑤ 嵌套帮助类（插在 `_OnMinHeightChanged` 之前，仍在 `class Window` 内）────────────────
ANCHOR_HELPER = ("        private static void _OnMinHeightChanged(DependencyObject d, "
                 "DependencyPropertyChangedEventArgs e)")

HELPER = """        // ── WPF-on-Linux · 波 51（`TASK-0108` 的 `P3`）：托管侧"尺寸提示声明变了"的告示 ─────────
        // 【为什么需要它】上游只在"新的上限**小于**当前窗口尺寸"时才动作（`OnMaxHeightChanged`／
        //   `OnMaxWidthChanged` 里 `maxHeight/maxWidth < logicalSize` 那一支），**调高**或任何
        //   "不伴随 resize 的声明"时上游**什么都不做**（两个方法里那句 "no need to do anything"
        //   就是原文）。而 X 侧的 `WM_NORMAL_HINTS` 由 shim 按"值变了才发"的**幂等**口径推送
        //   （native `wpf_hints_publish()`），它**只在有窗口活动时**才被触发 ⇒
        //   "调高上限且此后没有窗口活动"这一格永远到不了 X。
        //   W101A 实测的两个现场：`W3-DECLARE`（应用侧 469x365，X 侧 <缺席>）与
        //   `A3 W1-REVERT`（期望 667x500，实际停在 521 by 417）。
        // 【为什么是"发一条私有消息"而不是新导出 P/Invoke】`PresentationFramework.dll` **没有**
        //   `DllImportResolver`（机械证据：`grep -c SetDllImportResolver` ⇒ PF=0，而 WindowsBase／
        //   PresentationCore／UIAutomationTypes 各 1）⇒ 本程序集里新声明的 `[DllImport("…")]`
        //   落不到 `libwpfwin32.so`。而 `MS.Win32.UnsafeNativeMethods` 的 `user32.dll` P/Invoke
        //   **声明在 WindowsBase**、经 IVT 被 PF 使用（本文件 `Window.cs` 自己就这么调
        //   `WM_SYSCOMMAND`）⇒ 走它合法、且是**已经在跑**的路径。
        // 【消息号】`0xFF00`（= `WM_APP + 0x7F00`），与 native `win32_internal.h` 的
        //   `WPF_LINUX_WM_HINTS_CHANGED` **逐字对齐**。shim 在 `wpf_dispatch_to_window()`
        //   （`SendMessageW` 与 `DispatchMessageW` 的**共同落点**）拦下它，转调**同一个**
        //   `wpf_hints_publish()`（复用 `P1` 的缓存与 `P4` 的重入闸，**没有第二套发布路径**），
        //   并且**不再进**托管窗口过程。
        // 【幂等】多喊几次无害：值没变就不发 `XSetWMNormalHints`（波 51 的先写判据 `I1`）。
        // 【绝不影响窗口行为】整段只做一件事（发一条通知消息）：任何异常一律**吞掉并停用**
        //   （这只是一个"告示"，绝不能因为告示失败而影响窗口的尺寸语义）。
        internal static class WpfLinuxWindowHints
        {
            /// <summary>== native `win32_internal.h` 的 `WPF_LINUX_WM_HINTS_CHANGED`（= `WM_APP + 0x7F00`）。</summary>
            internal const int HintsChangedMessage = 0xFF00;

            /// <summary>一旦告示通道不可用（旧 shim / 无 X 连接）就停用，不反复抛。</summary>
            private static bool s_disabled;

            /// <summary>把"这个窗口的 min/max 声明变了"告诉 shim（幂等；由 shim 去重后落 X）。</summary>
            internal static void NotifyMinMaxChanged(Window w)
            {
                if (s_disabled || w == null) return;
                try
                {
                    if (w.IsSourceWindowNull) return;         // 还没有源窗口 ⇒ 没有 HWND 可发
                    IntPtr hwnd = w.Handle;
                    if (hwnd == IntPtr.Zero) return;
                    MS.Win32.UnsafeNativeMethods.SendMessage(hwnd,
                        (WindowMessage)HintsChangedMessage, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception)
                {
                    s_disabled = true;    // 只吞一次就停用（不刷日志、不重试风暴）
                }
            }
        }

"""

NOTIFY_LINES = """            // WPF-on-Linux · 波 51（`TASK-0108` 的 `P3`）：声明变了就告诉 shim 重问一次
            //   （**与方向无关** —— 上游"调高"那一支本来什么都不做；shim 侧是幂等发布）。
            WpfLinuxWindowHints.NotifyMinMaxChanged(this);
"""


def tail_anchor(call_line):
    """4 个方法与之一一对应的尾部锚（含那个**唯一**的 `UpdateHwndSizeOnWidthHeightChange(...)` 实参对）。"""
    return (call_line + "\n"
            "                    }\n"
            "                    else\n"
            "                    {\n"
            "                        // no need to do anything.  When window is restored, we get WM_GETMINMAXINFO where\n"
            "                        // we restrict the max/min size of the window to [Max/Min][Height/Width]\n"
            "                    }\n"
            "                }\n"
            "            }\n"
            "        }")


def tail_replacement(call_line):
    return (call_line + "\n"
            "                    }\n"
            "                    else\n"
            "                    {\n"
            "                        // no need to do anything.  When window is restored, we get WM_GETMINMAXINFO where\n"
            "                        // we restrict the max/min size of the window to [Max/Min][Height/Width]\n"
            "                    }\n"
            "                }\n"
            "            }\n"
            + NOTIFY_LINES +
            "        }")


A_MINH = tail_anchor("                        UpdateHwndSizeOnWidthHeightChange(logicalSize.X, minHeight);")
A_MAXH = tail_anchor("                        UpdateHwndSizeOnWidthHeightChange(logicalSize.X, maxHeight);")
A_MINW = tail_anchor("                        UpdateHwndSizeOnWidthHeightChange(minWidth, logicalSize.Y);")
A_MAXW = tail_anchor("                        UpdateHwndSizeOnWidthHeightChange(maxWidth, logicalSize.Y);")

# ── 编辑表（**唯一声明处**）：applier-audit 的 A 级"变换等价"就按这张表从上游重算期望 ──────────
# 形如 (上游相对路径, 生成物文件名【相对本应用器的 csproj 目录】, [(锚点, 替换, 期望命中次数), …])
# ⚠️ 条目**必须**是 3 元组 `(锚点, 替换, 次数)` —— `src/Linux/build/MilBridge/tools/applier-audit.py`
#    的 `declarations()`/`edits_of()` 按这个形状解析（4 元组会被当成"没有声明表"⇒ 掉到 C 级）。
PATCHES = [
    (UP_REL, "Window.Linux.cs", [
        (ANCHOR_HELPER, HELPER + ANCHOR_HELPER, 1),
        (A_MINH, tail_replacement("                        UpdateHwndSizeOnWidthHeightChange(logicalSize.X, minHeight);"), 1),
        (A_MAXH, tail_replacement("                        UpdateHwndSizeOnWidthHeightChange(logicalSize.X, maxHeight);"), 1),
        (A_MINW, tail_replacement("                        UpdateHwndSizeOnWidthHeightChange(minWidth, logicalSize.Y);"), 1),
        (A_MAXW, tail_replacement("                        UpdateHwndSizeOnWidthHeightChange(maxWidth, logicalSize.Y);"), 1),
    ]),
]

MARKERV = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'


def die(msg):
    print("[失败] " + msg)
    return 1


def generate(check_only):
    if not os.path.exists(UP):
        return die("上游不存在：%s" % UP)
    with open(UP, encoding="utf-8-sig") as f:
        text = f.read()

    edits = PATCHES[0][2]
    patched = text
    for anchor, repl, expect in edits:
        got = patched.count(anchor)
        if got != expect:
            return die("锚点[%s…]在上游里命中 %d 处（期望 %d）—— 上游改过这段，本补丁不能盲目应用。"
                       % (anchor.strip().splitlines()[0][:60], got, expect))
        patched = patched.replace(anchor, repl)
    content = BANNER.format(up=UP_REL) + patched

    up_to_date = os.path.exists(GEN)
    if up_to_date:
        with open(GEN, encoding="utf-8") as f:
            up_to_date = (f.read() == content)
    if check_only:
        print("[检查] %s：%s" % (os.path.basename(GEN),
                                "内容已是最新" if up_to_date else "缺失/与上游不同步"))
    elif up_to_date:
        print("[生成] %s：内容已是最新（未重写）" % os.path.basename(GEN))
    else:
        with open(GEN, "w", encoding="utf-8") as f:
            f.write(content)
        print("[生成] %s：已从上游重生成（%d 处插入）" % (os.path.basename(GEN), len(edits)))

    # ── 接线（幂等；`port-lib.py PresentationFramework` 会整份重写 csproj ⇒ 必须每波重放）──
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1
    if MARKERV not in csproj:
        return die("csproj 里找不到 Sdk.targets 锚点")
    lines = [
        MARKER_BEGIN,
        "  <ItemGroup>",
        '    <Compile Remove="$(UpstreamWpfRoot)%s" />' % UP_REL,
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Window.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
    ]
    csproj = csproj.replace(MARKERV, "\n".join(lines) + "\n" + MARKERV, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 2 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
