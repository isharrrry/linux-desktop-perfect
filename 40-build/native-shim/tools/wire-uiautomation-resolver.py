#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""D1 应用器：把 `Win32ShimResolver` 编进 **UIAutomationTypes / UIAutomationProvider**（**无参运行 = 应用**）。

    python3 src/WpfGfx.Linux.Native/tools/wire-uiautomation-resolver.py            # = 应用（幂等）
    python3 src/WpfGfx.Linux.Native/tools/wire-uiautomation-resolver.py --check    # 只检查（未就位 ⇒ 退出码 1）

【它挡的是什么（T3 实测）】
  `ListBox` 任意选中变化 ⇒ `ListBox.OnSelectionChanged` → `AutomationPeer..cctor` →
  `OSVersionHelper..cctor` → `[DllImport("PresentationNative_cor3.dll")]` → **DllNotFoundException**。
  实测：`Win32ShimResolver.cs` **只编进了 WindowsBase 与 PresentationCore**，
  而这条 P/Invoke 来自 **UIAutomationTypes** ⇒ 缺的**只是"resolver 这一层"**：
  符号在 shim 里是齐的（`libwpfwin32.so` 有 9 条 `IsWindows10*OrGreater`），
  `PresentationNative_cor3.dll` 也**已经在** resolver 的 `MappedLibraries` 里（:83）。

【⚠️ 一个必须同时处理的陷阱（否则"照既有做法加个文件"会直接编不过）】
  `src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs:37-43` 是

      #if WINDOWS_BASE      → namespace WpfLinux.Shims.WindowsBase
      #elif PRESENTATION_CORE → namespace WpfLinux.Shims.PresentationCore
      #else → #error "…只能编进 WindowsBase 或 PresentationCore（需要 WINDOWS_BASE / PRESENTATION_CORE 常量）"

  而 UIAutomationTypes 的 DefineConstants 是 `UIAUTOMATIONTYPES;WINDOWS_BASE_OR_PC`、
  UIAutomationProvider 的是 `AUTOMATION;WINDOWS_BASE_OR_PC`
  —— **两个常量都没有** ⇒ 直接加进 shims.txt 会命中 `#error`，构建红。
  所以本应用器**同时**给 resolver 加一个分支（键就是它们**已经有**的常量，不动 DefineConstants）：

      #elif UIAUTOMATIONTYPES || AUTOMATION
      namespace WpfLinux.Shims.UIAutomation

  `#if PRESENTATION_CORE` 门控的 MIL 桥 / WIC 段在这两个程序集里**不会被编进去**
  （与 WindowsBase 分支同样的道理：那两段只对 PC 成立）⇒ 行为变化是**净增 resolver**，没有别的。

【⚠️ 清单文件是共享状态：只增不删（merge）】
  第一版整文件覆写，把 UIAutomationTypes.shims.txt 里原有的 `Accessibility.Shim.cs` 删掉了
  ⇒ `UnsafeNativeMethodsCLR.cs(219,113): CS0122 "IAccessible" 不可访问`（它在 UIAutomationTypes 编译集里）。
  现在改为**合并**：保留既有行、只补缺失行；`--check` 只报"缺哪几行"。

【为什么用 shims.txt 而不是直接写 csproj】
  `src/Linux/build/port-lib.py:568-586` 会读 `src/Microsoft.DotNet.Wpf.Linux/src/shims/<项目名>.shims.txt` 并注入 `<Compile Include>`。
  ⇒ **波的第 1 步重生成 csproj 之后，这两行会自己回来**，不需要额外重放步骤。

【重放顺序】port-lib.py → reapply-patches.py → 各 patch-*.py →（需要时）本脚本
            → 之后重跑 `port-lib.py UIAutomationTypes` / `UIAutomationProvider` 或整波。
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

RESOLVER = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "Win32ShimResolver.cs")
SHIMS_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims")


def set_root(root):
    """把 ROOT 指向别处（**给"用临时副本做突变"的牙用**；默认仍是本文件推出的仓库根）。

    为什么需要：本脚本现在承载「零调用者」不变量（见下），而**仪器的牙必须能用突变证明它会红**
    —— 突变不能改真源，只能在 /tmp 的副本上做 ⇒ 副本要有自己的"仓库根"。
    """
    global ROOT, RESOLVER, SHIMS_DIR
    ROOT = os.path.normpath(root)
    RESOLVER = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "Win32ShimResolver.cs")
    SHIMS_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims")

# 每个工程**必须包含**的 shim 行（相对仓库根的路径）。
#
# ⚠️ **只增不删（merge）**：第一版是"整文件覆写"，结果把 UIAutomationTypes.shims.txt
#    原有的 `Accessibility.Shim.cs` **删掉了** ⇒ `UnsafeNativeMethodsCLR.cs`（它在
#    UIAutomationTypes 的编译集里）里 `ref IAccessible` 立刻 CS0122 —— 构建红。
#    这正是"工具悄悄破坏状态"那一类：**清单文件是别的车道也会用的共享状态，只能合并、不能重写**。
REQUIRED = {
    "UIAutomationTypes.shims.txt": [
        # UnsafeNativeMethodsCLR.cs（UIAutomationTypes 编译集里有它）要一个**可访问**的
        # `Accessibility.IAccessible`；上游那个是 internal ⇒ 必须带这份 shim。
        # 证据：不带它时 `UnsafeNativeMethodsCLR.cs(219,113): CS0122 "IAccessible" 不可访问`。
        "src/Microsoft.DotNet.Wpf.Linux/src/shims/Accessibility.Shim.cs",
        "src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs",
    ],
    "UIAutomationProvider.shims.txt": [
        "src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs",
    ],
}

ANCHOR = '''#elif PRESENTATION_CORE
namespace WpfLinux.Shims.PresentationCore
#else
#error "Win32ShimResolver.cs 只能编进 WindowsBase 或 PresentationCore（需要 WINDOWS_BASE / PRESENTATION_CORE 常量）"
#endif'''

REPLACEMENT = '''#elif PRESENTATION_CORE
namespace WpfLinux.Shims.PresentationCore
#elif UIAUTOMATIONTYPES || AUTOMATION
// [D1] UIAutomationTypes（DefineConstants 里的 `UIAUTOMATIONTYPES`）与
//      UIAutomationProvider（`AUTOMATION`）也需要这一层：ListBox 选中变化会走到
//      AutomationPeer..cctor → OSVersionHelper..cctor → [DllImport("PresentationNative_cor3.dll")]
//      ⇒ 没有 resolver 就是 DllNotFoundException（T3 实测）。
//      这两个常量**本来就在**它们的 DefineConstants 里 ⇒ 不必动 port-lib 的常量表。
//      被 `#if PRESENTATION_CORE` 门控的 MIL 桥 / WIC 段在这里不会编进来（与 WINDOWS_BASE 分支同理）。
namespace WpfLinux.Shims.UIAutomation
#else
#error "Win32ShimResolver.cs 只能编进 WindowsBase / PresentationCore / UIAutomationTypes / UIAutomationProvider（需要 WINDOWS_BASE / PRESENTATION_CORE / UIAUTOMATIONTYPES / AUTOMATION 常量）"
#endif'''


# ── [D-U1 裁决 · 阶段 1]「零调用者」**不变量** + 正对照（主控 2026-09-14 裁决）──────────
# 裁决方向：**不落 native 导出、不把 `UIAutomationCore.dll` 加进 `MappedLibraries`**
#   —— 理由是 `UiaLookupId` **不可达 + 零调用者**；为不可达路径编一张 GUID→ID 表，
#   等于把"诚实的失败"换成"看起来有值的编造值"。
# 本断言把该裁决变成**受监控不变量**：一旦真出现调用点（**含"用反射调它"这种字符串用法**），
#   本检查必须**变红并点名**，而不是等到运行期才炸。
# 【口径（= 红旗①的反面）】**字符串级子串**扫描，不是语法级、不是词边界：
#   反射用法 `GetMethod("UiaLookupId")` 与 `EntryPoint = "UiaLookupId"` 同样会被看见。
SCAN_DIRS = ("upstream", "src", "Linux", "build", "src", "tests", "samples")
SCAN_SKIP_DIRS = {"bin", "obj", ".artifacts", "node_modules", ".git"}
NEEDLE = "UiaLookupId"                          # 子串 ⇒ 同时覆盖 `RawUiaLookupId`
POSITIVE_CONTROL = "SupportsWin7Identifiers"    # 正对照：同 pattern 形状，**必须有 ≥1 个调用点**


SELF_PATH = os.path.abspath(__file__)   # 本仪器自身：**必须**含 needle 字面量 ⇒ 不计入扫描（见 _scan）


def _classify(line, path, needle):
    """把含 needle 的一行归类：def（定义/入口点声明）、doc（文档/注释）、call（调用点/反射用法）。

    ⚠️ `needle` **必须作参数传进来**（第一版写死了 `NEEDLE`）——否则正对照那一趟会用主 needle 的
       形状去判正对照的行 ⇒ 正对照恒为 0 ⇒ **仪器永远报"无信息"**（实测踩到）。
    """
    s = line.strip()
    if path.endswith(".md"):
        return "doc"
    if s.startswith(("//", "*", "/*", "#", "<!--", ">", "--")):
        return "doc"            # 注释（含上游自陈"we are not calling UiaLookupId"那一句）
    if "EntryPoint" in s and '"' in s:
        return "def"            # `[DllImport(..., EntryPoint = "...")]`
    if re.search(r"\b(static|extern)\b[^;{}]*\b" + needle + r"\s*\(", s):
        return "def"            # 方法/外部方法**声明**
    if re.search(r"\b" + needle + r"\s*\(", s) or ('"' + needle + '"') in s:
        return "call"           # 调用点，或**字符串字面量**（= 可能是反射用法）
    return "doc"


def _scan(root, needle):
    """扫 SCAN_DIRS ⇒ (buckets, 不可读文件数)。**不可读/二进制要计数并报出**，不许静默丢。"""
    buckets = {"call": [], "def": [], "doc": []}
    unreadable = 0
    for d in SCAN_DIRS:
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, d)):
            dirnames[:] = [x for x in dirnames if x not in SCAN_SKIP_DIRS]
            for fn in filenames:
                p = os.path.join(dirpath, fn)
                if os.path.abspath(p) == SELF_PATH:
                    continue        # 本仪器自身（它必须有 needle 字面量）⇒ 不计，否则恒红
                try:
                    with open(p, encoding="utf-8") as f:
                        for i, line in enumerate(f, 1):
                            if needle in line:
                                buckets[_classify(line, p, needle)].append(
                                    f"{os.path.relpath(p, root)}:{i}: {line.strip()[:150]}")
                except (UnicodeDecodeError, OSError):
                    unreadable += 1
    return buckets, unreadable


def audit_zero_callers(root, check_only):
    """不变量：`UiaLookupId` **零调用点**；正对照：`SupportsWin7Identifiers` **≥1 调用点**。

    返回：0 = 绿；1 = **不变量被破**（发现调用点）；2 = **仪器无信息**（正对照数不出来 ⇒ 不许报绿）。
    """
    b, unreadable = _scan(root, NEEDLE)
    print(f"[零调用者] `{NEEDLE}`：调用点 {len(b['call'])} ／ 定义 {len(b['def'])} ／ 文档注释 {len(b['doc'])}"
          f"（不可读/二进制文件 {unreadable} 个已跳过）")
    for h in b["call"]:
        print(f"    **调用点** {h}")
    for h in b["def"]:
        print(f"    定义     {h}")

    pc, _pc_unreadable = _scan(root, POSITIVE_CONTROL)
    print(f"[正对照] `{POSITIVE_CONTROL}`：调用点 {len(pc['call'])} ／ 定义 {len(pc['def'])}"
          f"（**必须 ≥1 个调用点**，否则本仪器无信息）")
    for h in pc["call"]:
        print(f"    （正对照的调用点）{h}")

    if len(pc["call"]) < 1:
        print(f"[仪器无信息] 正对照 `{POSITIVE_CONTROL}` 在本根下数不出任何**调用点** ⇒ "
              f"本次「零调用点」的结论**不成立**（不许报绿）。先查：根对不对？pattern 是不是退化成语法级了？")
        return 2
    if b["call"]:
        print(f"[不变量被破] `{NEEDLE}` 出现了 {len(b['call'])} 个**调用点** ⇒ "
              f"`D-U1` 的「不可达 + 零调用者」前提不再成立 ⇒ 必须重新裁决（**不许只加映射**）。")
        return 1 if check_only else 0    # 应用模式不改退出码：本断言**只读**，不阻断波里的应用步骤
    print("[零调用者] ✅ 不变量成立：`UiaLookupId` 没有任何调用点（含反射式字符串用法）")
    return 0


def apply(check_only):
    rc = 0
    # ---- ① resolver 的分支 ----
    if not os.path.exists(RESOLVER):
        print(f"[失败] 找不到 {RESOLVER}")
        return 1
    with open(RESOLVER, encoding="utf-8") as f:
        text = f.read()

    if "WpfLinux.Shims.UIAutomation" in text:
        print("[resolver] 分支已就位（幂等，不改）")
    elif check_only:
        print("[resolver] **未加 UIAutomation 分支** —— 需要运行一次本脚本（无参即应用）")
        rc = 1
    else:
        if text.count(ANCHOR) != 1:
            print(f"[失败] resolver 锚点出现 {text.count(ANCHOR)} 次，期望 1 次 —— 文件变了？")
            return 1
        with open(RESOLVER, "w", encoding="utf-8") as f:
            f.write(text.replace(ANCHOR, REPLACEMENT, 1))
        print("[resolver] 已加 `#elif UIAUTOMATIONTYPES || AUTOMATION` 分支（namespace WpfLinux.Shims.UIAutomation）")

    # ---- ② 两份 shims.txt：**合并**（保留既有行，只补缺失行）----
    for name, required in REQUIRED.items():
        path = os.path.join(SHIMS_DIR, name)
        existing = []
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                for line in f:
                    cur = line.rstrip("\n")
                    if cur.strip() and not cur.lstrip().startswith("#"):
                        existing.append(cur.strip())

        missing = [r for r in required if r not in existing]
        if not missing:
            print(f"[清单] {name} 已就位（{len(existing)} 行，幂等，不改）")
            continue
        if check_only:
            print(f"[清单] {name} **缺 {len(missing)} 行**（{', '.join(missing)}）—— 需要运行一次本脚本")
            rc = 1
            continue

        merged = existing + missing
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(merged) + "\n")
        print(f"[清单] {name}：保留既有 {len(existing)} 行 + 补入 {len(missing)} 行"
              f"（{', '.join(missing)}）")

    print()
    audit_rc = audit_zero_callers(ROOT, check_only)
    if audit_rc != 0:
        rc = audit_rc

    print("\n下一步（波里）：先跑本脚本 → 再跑 port-lib（UIAutomationTypes / UIAutomationProvider 两个工程）→ 构建这两个工程")
    return rc


def main():
    ap = argparse.ArgumentParser(
        description="D1：把 Win32ShimResolver 编进 UIAutomation*（**无参运行 = 应用**）；"
                    "并监控 `D-U1` 的「零调用者」不变量。退出码：0=绿 / 1=清单未就位或不变量被破 / 2=仪器无信息")
    ap.add_argument("--check", action="store_true", help="只检查，不写盘（未就位 ⇒ 退出码 1）")
    ap.add_argument("--root", default=None,
                    help="把'仓库根'指向别处（**给用临时副本做突变的牙用**；默认=本文件推出的仓库根）")
    args = ap.parse_args()
    if args.root:
        set_root(args.root)
    return apply(args.check)


if __name__ == "__main__":
    sys.exit(main())
