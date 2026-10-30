#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c 第 2 批 · P1：`InputProviderSite.ReportInput` 只读插桩（生成 + 接线 + 自检）。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputsite-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputsite-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputsite-trace.py --prove   # 「只插入」机械证明（只读）

为什么打这一格（P1）
------------------
第 1 批的实跑读数已把范围钉死：`WM_CHAR` 到了 `HwndSource.OnPreprocessMessage`、
`_eatCharMessages` **所有描点都是 `False`**、三步都没置 handled，最后
**`ProcessTextInputAction ⇒ handled=True`**，而 TextBox 的文本**没变**。

链路上 `HwndKeyboardInputProvider.ProcessTextInputAction` 的下一条语句就是
`handled = _site.ReportInput(report);` —— **P1 就是这条链的下一格**：
把 provider 交出来的 `RawTextInputReport` 与 `InputManager.ProcessInput` 的返回值对上。

**这一格是"报告到底有没有交出去"的分水岭**：
    · P1 **有** ⇒ provider 确实 report 了 ⇒ 往下看 P2/P3/P5（交给谁、raise 给谁）
    · P1 **没有** ⇒ `ProcessTextInputAction` 在 P1 之前就返回了（`_site` 为 null / 别处 return）

插桩点（上游 `PresentationCore/System/Windows/Input/InputProviderSite.cs:76-98`）
    P1 = `ReportInput` 的 `return handled;` **之前**：报告类型名 + `Type` + 返回的 handled + `_inputManager` 有没有。
    （每次调用一行；`Text` 类必打，其它类只采样 —— 预算在 `WpfLinuxInputTrace` 里，见下。）

**跨命名空间**：本文件是 `System.Windows.Input`，而插桩类 `WpfLinuxInputTrace` 在
`System.Windows.Interop`（生成物 `HwndSource.Linux.cs`，由 `patch-presentationcore-inputtrace.py` 生成）
⇒ **必须全限定**（`System.Windows.Interop.WpfLinuxInputTrace.…`），否则编译不过（CS0103）。

只读 / 有界 / 缺省关：见插桩类本体（`WPF_LINUX_INPUT_TRACE`，第 2 批有**独立**预算）。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/InputProviderSite.cs"
GEN = os.path.join(PC_DIR, "InputProviderSite.Linux.cs")

P1_ANCHOR = '''            if (_inputManager is not null)
            {
                handled = _inputManager.ProcessInput(input);
            }

            return handled;
        }
'''

P1_REPL = '''            if (_inputManager is not null)
            {
                handled = _inputManager.ProcessInput(input);
            }

            // ── T1c 第 2 批 P1（只读插桩）：报告类型 + 返回的 handled ──
            //    跨命名空间 ⇒ **全限定**（WpfLinuxInputTrace 在 System.Windows.Interop）
            System.Windows.Interop.WpfLinuxInputTrace.B2ReportInput(inputReport, handled, _inputManager);

            return handled;
        }
'''

EDITS = [
    ("P1 ReportInput 的返回处", P1_ANCHOR, P1_REPL),
]

REQUIRED = [
    ("P1 调用点在位（全限定）",
     "System.Windows.Interop.WpfLinuxInputTrace.B2ReportInput(inputReport, handled, _inputManager);"),
    ("P1 在 return handled 之前", "            return handled;\n        }\n\n        private bool _isDisposed;"),
    ("上游 ReportInput 的 ProcessInput 调用未动", "handled = _inputManager.ProcessInput(input);"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputsite-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + 1 处 T1c **只读插桩**（第 2 批 P1）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。第 1 批已证明字符到了 OnPreprocessMessage 且没被
//   `_eatCharMessages` 吞掉，最后停在 `ProcessTextInputAction ⇒ handled=True` 而文本未变。
//   P1 钉在 `ProcessTextInputAction` 的下一句（`_site.ReportInput(report)`）——
//   **"报告有没有交出去"的分水岭**：P1 有 ⇒ 顺着 P2→P3→P5 往下；P1 无 ⇒ provider 侧提前返回。
//   开关与有界：见插桩类本体（`WPF_LINUX_INPUT_TRACE`；第 2 批预算**独立**于第 1 批，Text 类必打）。
"""

MARKER_BEGIN = ("  <!-- ==== T1c 输入链读数（第 2 批）：InputProviderSite.ReportInput"
                "（patch-presentationcore-inputsite-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c 输入链读数（第 2 批）InputProviderSite 结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _build(check_only=False):
    up = os.path.join(ROOT, UP_REL)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] InputProviderSite {name}：上游出现 {n} 次（要求 1）")
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
        print("[失败] 大括号盈亏变化（只读插桩不该改结构）："
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
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")
    return full, 0


def _header():
    return HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS)))


def prove():
    up = os.path.join(ROOT, UP_REL)
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            body = f.read()
        head = _header()
        if not body.startswith(head):
            print(f"[失败] {os.path.relpath(GEN, ROOT)} 不是本脚本产出的（文件头对不上）")
            return 1
        body = body[len(head):]
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
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputProviderSite.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 落在上游 Include 之后）")

    print("[注意] `src/Linux/build/port-lib.py PresentationCore` 会整份重写 csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本。接线丢失**不会报编译错**，只会一行都不打 ⇒ 别把空输出读成结论。")

    if check_only:
        print("[检查] " + ("生成物与接线都已就位且与上游同步" if up_to_date
                           else "生成物缺失/与上游不同步（需要重新生成）"))
        return 0 if up_to_date else 1
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
