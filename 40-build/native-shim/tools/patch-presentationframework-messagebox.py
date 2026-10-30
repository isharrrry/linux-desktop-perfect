#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""任务 E：让 `MessageBox.Show` 真正弹出模态窗口并阻塞（生成式补丁，幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-messagebox.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-messagebox.py           # 生成 + 接线

【它补的是哪一半】
  上游 `MessageBox.ShowCore` 走的是 `user32!MessageBox`（P/Invoke）：
      MessageBox.cs:414
        MessageBoxResult result = Win32ToMessageBoxResult (UnsafeNativeMethods.MessageBox (new HandleRef (null, owner), messageBoxText, caption, style));
  而 shim（`src/WpfGfx.Linux.Native/src/win32_misc.c` 的 `MessageBoxW`）**没有 X11 模态对话框**，
  只往 stderr 写一行、把结果当"用户按了确定"返回（刻意降级，避免 headless 死锁）。

  ⇒ 本补丁把这一处调用换成**自产实现** `System.Windows.WpfLinuxMessageBox.Show(...)`
  （手写在 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxMessageBox.Linux.cs`）：
  用自产 `Window` + `ShowDialog()` 弹真窗口并阻塞；无 `DISPLAY` / 异常时**照旧降级不阻塞**。
  **返回值语义不变**：两边都返回 Win32 `ID*` 码，仍交给同文件里既有的 `Win32ToMessageBoxResult`。

【补法（**只改 1 行**）】
  锚点 = 上游 `MessageBox.cs:414` 整行（含缩进），替换为同一行的
  `Win32ToMessageBoxResult (WpfLinuxMessageBox.Show (owner, messageBoxText, caption, style))`。
  除此之外**逐字复制上游**；锚点找不到 / 命中数 ≠ 1 ⇒ 生成器报错退出（绝不静默产出未打补丁的副本）。

【注意】
  · 本脚本**无参运行 = 生成 + 接线**（家族约定：`patch-*` 无参必须真做事）。
  · `port-lib.py PresentationFramework` 会**整份重写** csproj ⇒ 接线必须每次都重放；本脚本自带
    `MARKER_BEGIN` 幂等接线（与 `patch-presentationframework-window-minmax-notify.py` 同款）。
  · 接线里同时收进**手写**的 `WpfLinuxMessageBox.Linux.cs`（与既有 `WpfLinux*Probe.Linux.cs` 同款）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
GEN = os.path.join(PF_DIR, "MessageBox.Linux.cs")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/MessageBox.cs"
UP = os.path.join(ROOT, UP_REL)

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 补丁：MessageBox 弹出真正的模态窗口（任务 E）=== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux 补丁结束（任务 E）==== -->"

BANNER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-messagebox.py **生成**，不要手改。
//
// 内容 = 上游 `{up}` 逐字复制 + **1 处替换**：
//   `ShowCore` 里那次 `UnsafeNativeMethods.MessageBox(...)`（上游 :414）→ `WpfLinuxMessageBox.Show(...)`。
//
// 为什么打（任务 E）：上游这行走 `user32!MessageBox`，而本移植的 shim 没有 X11 模态对话框，
//   只写 stderr 并按"用户按了确定"返回（刻意降级，防 headless 死锁）⇒ `MessageBox.Show` 从不弹窗、
//   也从不阻塞。替换后的 `WpfLinuxMessageBox`（手写件，编译进本程序集）用自产 `Window` + `ShowDialog()`
//   弹真窗口并阻塞；无 `DISPLAY` / 构造失败时**照旧降级不阻塞**。
// 纪律：**只改这一行**——不改任何既有语句、不删任何守卫；返回值仍是 Win32 `ID*` 码，语义不变。
//   锚点找不到 / 命中数 ≠ 1 ⇒ 生成器报错退出（不静默产出）。
// 每次运行该脚本都会从上游重读重生成。

"""

ANCHOR = ("            MessageBoxResult result = Win32ToMessageBoxResult "
          "(UnsafeNativeMethods.MessageBox (new HandleRef (null, owner), messageBoxText, caption, style));")
REPL = ("            // WPF-on-Linux（任务 E）：改走自产模态实现，返回值仍是 Win32 `ID*` 码。\n"
        "            MessageBoxResult result = Win32ToMessageBoxResult "
        "(WpfLinuxMessageBox.Show (owner, messageBoxText, caption, style));")

# ── 编辑表（**唯一声明处**）：形如 (上游相对路径, 生成物文件名, [(锚点, 替换, 期望命中次数), …])
# ⚠️ 条目**必须**是 3 元组 —— `applier-audit.py` 的 `declarations()`/`edits_of()` 按这个形状解析。
PATCHES = [
    (UP_REL, "MessageBox.Linux.cs", [
        (ANCHOR, REPL, 1),
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
                       % (anchor.strip()[:60], got, expect))
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
        print("[生成] %s：已从上游重生成（%d 处替换）" % (os.path.basename(GEN), len(edits)))

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
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/MessageBox.Linux.cs" />',
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxMessageBox.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
    ]
    csproj = csproj.replace(MARKERV, "\n".join(lines) + "\n" + MARKERV, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 3 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj -c Release -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
