#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c 补丁 K 的一键应用器：`OleServicesContext` 的 STA 检查 + OLE 初始化（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-olecontext.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-olecontext.py [--apply]

【根因：与补丁 H（InputManager）**完全同一类**，只是换了个文件】
  Linux 上线程**永远**不是 STA（`GetApartmentState()` 恒 `Unknown`，`SetApartmentState` 抛
  `PlatformNotSupportedException` —— 实测见 ManagedLayer.Tests 的
  `ApartmentState_NeverReportsSTA_OnLinux`）。而 `OleServicesContext` 里有 **5 处**
  硬 STA 检查，其中一处正卡在"任何 WPF 窗口第一次 Show()"的路上：

      ThreadStateException: Current thread must be set to single thread apartment (STA)
        at System.Windows.OleServicesContext.SetDispatcherThread()   OleServicesContext.cs:143
        at System.Windows.OleServicesContext..ctor()                 OleServicesContext.cs:45
        at System.Windows.OleServicesContext.get_CurrentOleServicesContext()  OleServicesContext.cs:61
        at System.Windows.DragDrop.RegisterDropTarget(IntPtr)        DragDrop.cs:455
        at System.Windows.Interop.HwndSource.Initialize(...)         HwndSource.cs:334
        at System.Windows.Window.CreateSourceWindow(Boolean)         Window.cs:2519
        at System.Windows.Window.ShowHelper(Object)                  Window.cs:5483

  `HwndSource.Initialize`（HwndSource.cs:334）**无条件**调 `DragDrop.RegisterDropTarget`，
  所以这一处挡的是**每一个 WPF 窗口**。

【5 处的 Linux 语义（各自的理由不同，不能一刀切）】
  | 方法 | Linux 语义 | 为什么 |
  |---|---|---|
  | `SetDispatcherThread` | 跳过 STA 检查；**不调 `OleInitialize`**（没有 ole32 可初始化）；仍然挂 `ShutdownFinished` | 保持与 `OnDispatcherShutdown` 的配平结构不变 |
  | `OnDispatcherShutdown` | 直接返回 | 没有 `OleInitialize` 需要 `OleUninitialize` 配平 |
  | `OleRegisterDragDrop` | **返回 S_OK 的 no-op** | 没有 OLE drop target 可注册；`HwndSource.Initialize` 无条件调它 ⇒ **绝不能抛**，否则窗口建不出来 |
  | `OleRevokeDragDrop` | **返回 S_OK 的 no-op** | 同上（窗口关闭时调） |
  | `OleDoDragDrop` | 抛 **`PlatformNotSupportedException`** | OLE 拖放本身不存在。用 PNSE 而不是 ThreadStateException：后者会误导成"调用方线程用错了"；PNSE 说的是"这台机器上没有 OLE" —— 与 M4 对 OLE 公开 API 的裁决（`src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs`，消息含 U13）**同一口径** |

  ⚠️ `OleDoDragDrop` 是**唯一**一个仍然抛异常的：它对应"用户真的发起了一次拖放"，
  那件事在 Linux 上确实做不到，必须响亮失败；而 register/revoke 是**基础设施调用**，
  "没有对象可注册"的正确答案是成功返回。

【为什么用生成式补丁】
  与 G/H/I/J 同一套：从 upstream 逐字读入 → 只替换上面 5 个块 → 写到
  `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/OleServicesContext.Linux.cs`（与 SR.g.cs 同为生成物），
  csproj 侧 Remove 上游 + Include 生成物。**锚点必须逐个唯一命中**，否则报错退出。

  重放顺序：port-lib.py → reapply-patches.py(F/D/G) → patch-presentationcore-apartment.py(H)
            → patch-presentationcore-fontcache.py(I) → patch-presentationcore-registry.py(J)
            → 本脚本(K)

【同类清单（编译集实测，共 7 处 STA 检查）】
  本脚本覆盖 `OleServicesContext.cs` 的 5 处；另两处：
    · `InputManager.cs:142` —— 已由补丁 H 覆盖（生成物 InputManager.Linux.cs）
    · `BitmapEffect.cs:26`  —— 已废弃的 BitmapEffect API，示例与常见应用不走；需要时按同一手法加一处
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
GENERATED = os.path.join(PC_DIR, "OleServicesContext.Linux.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                        "PresentationCore", "System", "Windows", "OleServicesContext.cs")

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7c 补丁 K：OleServicesContext 的 STA 检查与 OLE 初始化"
                "（由 src/Linux/tools/patch-presentationcore-olecontext.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7c 补丁 K 结束 ==== -->"

STA_BLOCK = """        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new ThreadStateException(SR.OleServicesContext_ThreadMustBeSTA);
        }
"""

# (描述, 原文, 替换)。每一处都必须**唯一命中**。
PATCHES = [
    (
        "OleDoDragDrop → PNSE（唯一仍然抛异常的：拖放本身确实做不到）",
        STA_BLOCK + """
        InputManager inputManager = (InputManager)Dispatcher.CurrentDispatcher.InputManager;""",
        """        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // Linux 上线程永远不是 STA，而且 OLE 拖放**本身不存在**。
        // 抛 PlatformNotSupportedException（而不是 ThreadStateException）：后者会
        // 误导成"调用方线程用错了"，而真相是"这台机器上没有 OLE" ——
        // 与 M4 对 OLE 公开 API 的裁决同一口径（见 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs）。
        if (!System.OperatingSystem.IsWindows())
        {
            throw new System.PlatformNotSupportedException(
                "WPF-on-Linux: OLE 拖放（DoDragDrop）在 Linux 上不可用（U13）。");
        }

        InputManager inputManager = (InputManager)Dispatcher.CurrentDispatcher.InputManager;""",
    ),
    (
        "OleRegisterDragDrop → S_OK no-op（HwndSource.Initialize 无条件调它）",
        STA_BLOCK + """
        return UnsafeNativeMethods.RegisterDragDrop(windowHandle, dropTarget);""",
        """        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 没有 OLE drop target 可注册。注意：`HwndSource.Initialize`（HwndSource.cs:334）
        // **无条件**调用 DragDrop.RegisterDropTarget → 这里 ⇒ **不能抛**，否则
        // 每一个 WPF 窗口都建不出来。返回 S_OK = "没有对象需要注册"，这是真话。
        if (!System.OperatingSystem.IsWindows())
        {
            return 0;   // S_OK
        }

        return UnsafeNativeMethods.RegisterDragDrop(windowHandle, dropTarget);""",
    ),
    (
        "OleRevokeDragDrop → S_OK no-op（窗口关闭时调）",
        STA_BLOCK + """
        return UnsafeNativeMethods.RevokeDragDrop(windowHandle);""",
        """        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 与 OleRegisterDragDrop 对称：没有注册过，也就没有可撤销的。
        if (!System.OperatingSystem.IsWindows())
        {
            return 0;   // S_OK
        }

        return UnsafeNativeMethods.RevokeDragDrop(windowHandle);""",
    ),
    (
        "SetDispatcherThread → 跳过 STA 检查与 OleInitialize，仍挂 ShutdownFinished",
        STA_BLOCK + """
        // Initialize Ole services.""",
        """        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // Linux 上线程永远不是 STA（补丁 H 已为 InputManager 解过同一类问题），
        // 而且没有 ole32 可 OleInitialize。这里跳过两件事，但**保留**挂
        // ShutdownFinished 的结构 —— 与 OnDispatcherShutdown 的配平关系不变。
        if (!System.OperatingSystem.IsWindows())
        {
            Dispatcher.CurrentDispatcher.ShutdownFinished += new EventHandler(OnDispatcherShutdown);
            return;
        }

        // Initialize Ole services.""",
    ),
    (
        "OnDispatcherShutdown → 无 OLE 可反初始化",
        STA_BLOCK + """
        // Uninitialize Ole services.""",
        """        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 没有 OleInitialize 需要 OleUninitialize 配平（见 SetDispatcherThread）。
        if (!System.OperatingSystem.IsWindows())
        {
            return;
        }

        // Uninitialize Ole services.""",
    ),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-olecontext.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/OleServicesContext.cs`
//        逐字复制 + 5 处 Linux 守卫（见下方补丁 K 标记）。
// 每次运行该脚本都会从上游重读重生成；锚点对不上时**报错退出**。
//
// 为什么必须打：
//   Linux 上线程永远不是 STA（GetApartmentState() 恒 Unknown）⇒ 5 处硬 STA 检查全部必然抛
//   ThreadStateException。其中 `SetDispatcherThread` 挡在
//   `Window.ShowHelper → HwndSource.Initialize:334 → DragDrop.RegisterDropTarget` 上，
//   即**每一个 WPF 窗口第一次 Show()**。
//
// ↓↓↓ 以下为上游原文（仅 5 处有补丁 K 标记）↓↓↓
"""


def generate(check_only):
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    out = text
    for desc, old, new in PATCHES:
        n = out.count(old)
        if n != 1:
            print(f"[失败] 锚点不唯一（{n} 处）：{desc}\n"
                  f"       上游改过这段，补丁 K 不能盲目应用。")
            return 1
        out = out.replace(old, new, 1)
        print(f"[补丁] {desc}")

    content = HEADER + out
    up_to_date = os.path.exists(GENERATED)
    if up_to_date:
        with open(GENERATED, encoding="utf-8") as f:
            up_to_date = (f.read() == content)

    if check_only:
        print(f"[检查] {os.path.basename(GENERATED)}：{'内容已是最新' if up_to_date else '缺失/与上游不同步'}")
    elif up_to_date:
        print(f"[生成] {os.path.basename(GENERATED)}：内容已是最新（未重写）")
    else:
        with open(GENERATED, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[生成] {os.path.basename(GENERATED)}：已从上游重生成（5 处守卫）")

    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if anchor not in csproj:
        print("[失败] csproj 里找不到 Sdk.targets 锚点")
        return 1
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             '    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/OleServicesContext.cs" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/OleServicesContext.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 2 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
