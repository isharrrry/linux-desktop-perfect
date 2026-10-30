#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""窗口图标：让 IconHelper 在栅格化失败时降级为"无图标"（生成式补丁，幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-iconhelper.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-iconhelper.py

【根因（实测，非推断）】
  任何设置了 `Window.Icon` 的窗口在 Show 时都会走：
      Window.UpdateIcon                                  （PresentationFramework）
        → IconHelper.GetIconHandlesFromImageSource
        → IconHelper.CreateIconHandleFromImageSource
        → IconHelper.GenerateBitmapSource
        → new RenderTargetBitmap(...).Render(dv)          ← 本栈此处抛异常
  实测栈（最小 repro：原生 `Window` + `Icon=pack://…/icon.ico`，Xvfb 下运行）：
      Unhandled exception. System.InvalidCastException: Specified cast is not valid.
        at MS.Internal.HRESULT.Check(Int32 hr)
        at System.Windows.Media.Imaging.BitmapVisualManager.Render(Visual visual, Matrix worldTransform, Rect windowClip)
        at System.Windows.Media.Imaging.RenderTargetBitmap.Render(Visual visual)
        at MS.Internal.AppModel.IconHelper.GenerateBitmapSource(ImageSource img, Size renderSize)
        at System.Windows.Window.UpdateIcon()
  ⇒ 未捕获 ⇒ abort。后果：**任何带窗口图标的第三方 WPF 应用/库都打不开窗口**
  （如 HandyControl 示例的 8 个窗口 XAML 都写了 Icon="/HandyControlDemo;component/Resources/Img/icon.ico"）。

【补法（唯一改动 = 一处 try/catch）】
  只把 `GetIconHandlesFromImageSource` 里的两次句柄构造包进 try/catch，失败 ⇒ 转 `GetDefaultIconHandles`
  （= 与"窗口没设 Icon"完全同一条分支：走 exe 默认图标；在自产栈上它由 shim 的 `ExtractIconEx` 供句柄）。
  ⚠️ 降级**不能直接置 null**：`Window.UpdateIcon` 会把句柄当 SafeHandle 参数传给
  `UnsafeNativeMethods.SendMessage(WM_SETICON, …)`，null 在 marshal 层直接抛
  `ArgumentNullException: SafeHandle cannot be null.`（实测踩过）。
  即"图标拿不到就退回默认图标"，属栈口径里的**如实降级**，不是假装成功。

【纪律】
  · 除该处外**逐字复制上游**；锚点命中数 ≠ 1 ⇒ 生成器报错退出（绝不静默产出未打补丁的副本）。
  · `port-lib.py PresentationFramework` 会**整份重写** csproj ⇒ 接线必须每波重放；本脚本自带幂等接线。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
GEN = os.path.join(PF_DIR, "IconHelper.Linux.cs")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/AppModel/IconHelper.cs"
UP = os.path.join(ROOT, UP_REL)

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 补丁：窗口图标栅格化失败时降级（patch-presentationframework-iconhelper.py 注入）==== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux 补丁结束（窗口图标降级）==== -->"

BANNER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-iconhelper.py **生成**，不要手改。
//
// 内容 = 上游 `{up}` 逐字复制 + **1 处插入**：
//   `GetIconHandlesFromImageSource` 里的两次句柄构造 → 包进 try/catch，失败按"无图标"降级。
//
// 为什么打：本栈 `RenderTargetBitmap.Render` 抛 InvalidCastException
//   （MS.Internal.HRESULT.Check），未捕获会把整个进程 abort ⇒ 带窗口图标的第三方应用全打不开窗口。
// 纪律：**只插这一处**——不改任何既有语句、不删任何守卫；失败语义与上游"句柄为 null ⇒ 默认图标"一致。
//   锚点找不到 / 命中数 ≠ 1 ⇒ 生成器报错退出（不静默产出）。
// 每次运行该脚本都会从上游重读重生成。

"""

ANCHOR = """        public static void GetIconHandlesFromImageSource(ImageSource image, out NativeMethods.IconHandle largeIconHandle, out NativeMethods.IconHandle smallIconHandle)
        {
            EnsureSystemMetrics();
            largeIconHandle = CreateIconHandleFromImageSource(image, s_iconSize);
            smallIconHandle = CreateIconHandleFromImageSource(image, s_smallIconSize);
        }"""

REPL = """        public static void GetIconHandlesFromImageSource(ImageSource image, out NativeMethods.IconHandle largeIconHandle, out NativeMethods.IconHandle smallIconHandle)
        {
            EnsureSystemMetrics();
            // ── WPF-on-Linux 补丁：图标栅格化在本栈不可用（RenderTargetBitmap 抛 InvalidCastException）──
            // 图标属"锦上添花"：失败即退回"窗口未设 Icon"的默认图标分支，不允许因此让窗口创建失败、进程 abort。
            try
            {
                largeIconHandle = CreateIconHandleFromImageSource(image, s_iconSize);
                smallIconHandle = CreateIconHandleFromImageSource(image, s_smallIconSize);
            }
            catch (System.Exception)
            {
                // 降级到"未设置 Icon"时的**同一路径**：交给上游既有的默认图标分支。
                // ⚠️ 不能直接置 null：Window.UpdateIcon 会把句柄作为 SafeHandle 参数传给
                //    UnsafeNativeMethods.SendMessage(WM_SETICON, …)，null 会在 marshal 层抛
                //    ArgumentNullException: SafeHandle cannot be null.（实测）。
                GetDefaultIconHandles(out largeIconHandle, out smallIconHandle);
            }
        }"""

PATCHES = [
    (UP_REL, "IconHelper.Linux.cs", [
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

    # ── 接线（幂等；port-lib 会整份重写 csproj ⇒ 必须每波重放）──
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
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/IconHelper.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
    ]
    csproj = csproj.replace(MARKERV, "\n".join(lines) + "\n" + MARKERV, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 2 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj -c Release -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不写文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
