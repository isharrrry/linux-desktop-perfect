#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SplashScreen：把手写的 Linux 实现接进自产 PresentationFramework（幂等接线）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-splashscreen.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-splashscreen.py

【根因（实测）】
  上游 `System.Windows.SplashScreen` 位于 **WindowsBase**
  （`src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/SplashScreen.cs`，435 行），
  实现是纯 Win32 分层窗口（CsWin32 绑定 + `user32`/GDI + `BLENDFUNCTION` + `UpdateLayeredWindow`）。
  它**未随自产件移植**（`WindowsBase.Linux.csproj` 里查无此文件）⇒ 自产 WindowsBase 没有该类型 ⇒
  任何 `new SplashScreen(...)` 的 WPF 应用在 Linux 上**编译失败**：
      App.xaml.cs(96,32): error CS0246: 未能找到类型或命名空间名"SplashScreen"
  （HandyControl 示例实测；此前只能在应用侧自己补一个同名类型，属"每个消费者各补一次"。）

【补法】
  把手写件 `WpfLinuxSplashScreen.Linux.cs`（`namespace System.Windows`，以 WPF 窗口实现等价语义：
  显示入口程序集内资源图；`autoClose` 时在首个非闪屏窗口可见后关闭，并交还 `Application.MainWindow`）
  接进自产 PresentationFramework。上游该类型本属 WindowsBase，但 WindowsBase 层级更低、拿不到
  `Window`/`Image`，故实现上移到 PF；应用侧名字不变（`System.Windows.SplashScreen`）。

【注意】
  · **不替换上游任何文件**：上游那份在 WindowsBase 且未被编入 ⇒ 不存在双份类型冲突。
  · `port-lib.py PresentationFramework` 会**整份重写** csproj ⇒ 接线必须每波重放；本脚本自带幂等接线。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
HAND = os.path.join(PF_DIR, "WpfLinuxSplashScreen.Linux.cs")

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 补丁：System.Windows.SplashScreen 的 Linux 实现（patch-presentationframework-splashscreen.py 注入）==== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux 补丁结束（SplashScreen）==== -->"

MARKERV = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'


def die(msg):
    print("[失败] " + msg)
    return 1


def generate(check_only):
    if not os.path.exists(HAND):
        return die("手写实现不存在：%s" % HAND)
    print("[检查] %s：存在（%d 字节）" % (os.path.basename(HAND), os.path.getsize(HAND)))

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
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxSplashScreen.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
    ]
    csproj = csproj.replace(MARKERV, "\n".join(lines) + "\n" + MARKERV, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 1 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj -c Release -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不写文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
