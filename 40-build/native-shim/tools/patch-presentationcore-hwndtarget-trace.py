#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""波 46 · `D-G54` 仪器：给 **`HwndTarget`** 加只读插桩（缺省关、有界、不改行为）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hwndtarget-trace.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hwndtarget-trace.py

【为什么打】`D-G54`：HC 组合框的弹窗窗口建了、map 了、几何对、视觉树也接好了，**但内容零渲染**。
  桥侧台账把范围收窄到：全运行**只有一条** `MilCmdTargetSetRoot`（handle 0x3＝主窗口），
  弹窗目标在主通道的副本（handle 0x894）只收到过 `MilCmdTargetUpdateWindowSettings`，**从没收到 SetRoot**
  ⇒ 弹窗目标**没有根** ⇒ 桥按设计"没有内容可画"。
  上游 `HwndTarget.CreateUCEResources()` 末尾**本来就有** `DUCE.CompositionTarget.SetRoot(...)`
  （`HwndTarget.cs:788`），而它的前半段（`CreateOrAddRefOnChannel` `:740` ＋ `DuplicateHandle` `:742`）
  在同一个运行里**走到了** ⇒ 要问的是：**弹窗那次为什么没走到 `:788`**。
  本插桩就是在"进入 / 建资源后 / 复制句柄后 / SetRoot 前 / SetRoot 后 / 根 setter"各打一行，
  主窗口与弹窗对照 —— 走到哪一步断的，缺的就是那一行。

【只读】5 处都在原语句之后/之前各插一行；**不改控制流、不改返回值、不吞异常**。
【缺省关】`WPF_LINUX_HT_TRACE=1` 才打；每进程 ≤ 80 行。
【为什么用 finally 之外的形态】本仓 applier 家族有两条活断言：**`throw` 条数必须不变**、
  **大括号盈亏必须不变** —— 所以插桩不写 try/catch（那会加 `throw`）。
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndTarget.cs"
UPSTREAM = os.path.join(ROOT, UP_REL)
GEN = os.path.join(PC_DIR, "HwndTarget.Linux.cs")

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 波46 D-G54：HwndTarget 设根链只读插桩（本文件由 src/Linux/tools/patch-presentationcore-hwndtarget-trace.py 生成）==== -->"
MARKER_END = "  <!-- ==== /波46 D-G54 ==== -->"

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hwndtarget-trace.py **生成**，不要手改。
//
// 内容 = 上游 `{rel}` 逐字复制 + {n} 处只读插桩（`D-G54`：弹窗目标为何没走到 `SetRoot`）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
// 插桩**缺省关**（`WPF_LINUX_HT_TRACE=1` 才打）、**有界**（≤80 行/进程）、**只打印**。

"""

TRACE_CLASS = '''        // ── 波46 · D-G54 只读仪器（缺省关、有界）──
        internal static class WpfLinuxHtTrace
        {
            private static int _on = -1;
            private static int _n;
            private static bool On()
            {
                if (_on < 0) _on = Environment.GetEnvironmentVariable("WPF_LINUX_HT_TRACE") == "1" ? 1 : 0;
                return _on == 1;
            }
            internal static void Log(string msg)
            {
                if (!On() || _n++ >= 80) return;
                Console.Error.WriteLine("[HT] " + msg);
                Console.Error.Flush();
            }
            internal static string Id(DUCE.Channel c) { return c == null ? "null" : c.GetHashCode().ToString("x"); }
            internal static string H(DUCE.ResourceHandle h) { return h.IsNull ? "null" : h.GetHashCode().ToString("x8"); }
        }

'''

A_ANCHOR = "        internal override void CreateUCEResources(DUCE.Channel channel, DUCE.Channel outOfBandChannel)\n        {\n"
A_REPL = TRACE_CLASS + A_ANCHOR + (
    '            WpfLinuxHtTrace.Log("CreateUCEResources 进入：channel=" + WpfLinuxHtTrace.Id(channel)'
    ' + " oob=" + WpfLinuxHtTrace.Id(outOfBandChannel));\n')

B_ANCHOR = "            bool resourceCreated = _compositionTarget.CreateOrAddRefOnChannel(this, outOfBandChannel, DUCE.ResourceType.TYPE_HWNDRENDERTARGET);\n"
B_REPL = B_ANCHOR + (
    '            WpfLinuxHtTrace.Log("建目标资源后：resourceCreated=" + resourceCreated'
    ' + " target(OOB)=" + WpfLinuxHtTrace.H(_compositionTarget.GetHandle(outOfBandChannel)));\n')

C_ANCHOR = "            _compositionTarget.DuplicateHandle(outOfBandChannel, channel);\n"
C_REPL = C_ANCHOR + (
    '            WpfLinuxHtTrace.Log("复制句柄后：target(主通道)=" + WpfLinuxHtTrace.H(_compositionTarget.GetHandle(channel))'
    ' + " IsOnChannel(main)=" + _compositionTarget.IsOnChannel(channel));\n')

D_ANCHOR = ("            DUCE.CompositionTarget.SetRoot(\n"
            "                _compositionTarget.GetHandle(channel),\n"
            "                _contentRoot.GetHandle(channel),\n"
            "                channel);\n")
D_REPL = ('            WpfLinuxHtTrace.Log("SetRoot 之前：target=" + WpfLinuxHtTrace.H(_compositionTarget.GetHandle(channel))'
          ' + " contentRoot=" + (_contentRoot.IsOnChannel(channel) ? WpfLinuxHtTrace.H(_contentRoot.GetHandle(channel)) : "not-on-channel"));\n'
          + D_ANCHOR +
          '            WpfLinuxHtTrace.Log("SetRoot 之后：已发出");\n')

E_ANCHOR = ("        public override Visual RootVisual\n"
            "        {\n"
            "            set\n"
            "            {\n"
            "                base.RootVisual = value;\n")
E_REPL = E_ANCHOR + (
    '                WpfLinuxHtTrace.Log("RootVisual setter 被调：value=" + (value == null ? "null" : value.GetType().Name));\n')

EDITS = [
    ("A 进入 CreateUCEResources", A_ANCHOR, A_REPL),
    ("B 建目标资源之后", B_ANCHOR, B_REPL),
    ("C 复制句柄之后", C_ANCHOR, C_REPL),
    ("D SetRoot 前后", D_ANCHOR, D_REPL),
    ("E T4 根 setter", E_ANCHOR, E_REPL),
]

REQUIRED = [
    ("进入行", 'WpfLinuxHtTrace.Log("CreateUCEResources 进入'),
    ("建资源行", 'WpfLinuxHtTrace.Log("建目标资源后'),
    ("复制句柄行", 'WpfLinuxHtTrace.Log("复制句柄后'),
    ("SetRoot 之前", 'WpfLinuxHtTrace.Log("SetRoot 之前'),
    ("SetRoot 之后", 'WpfLinuxHtTrace.Log("SetRoot 之后'),
    ("根 setter 行", 'WpfLinuxHtTrace.Log("RootVisual setter 被调'),
    ("缺省关", 'GetEnvironmentVariable("WPF_LINUX_HT_TRACE")'),
    ("有界", '_n++ >= 80'),
]


def _count(text, needle):
    return text.count(needle)


def generate(check_only):
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}")
        return 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print("[失败] 大括号盈亏变化（只读插桩不该改结构）")
        return 1
    print(f"[断言] `throw` {up_throws}=={out_throws}；大括号 {_count(out, '{')}/{_count(out, '}')}；"
          f"行数 {len(text.splitlines())} → {len(out.splitlines())}")

    out = HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS))) + out

    for name, needle in REQUIRED:
        if needle not in out:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")

    cur = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            cur = f.read()
    if cur != out:
        if not check_only:
            with open(GEN, "w", encoding="utf-8") as f:
                f.write(out)
            print(f"[生成] {os.path.relpath(GEN, ROOT)}：已从上游重生成")
        else:
            print("[检查] 生成物与上游不同步（需重跑本脚本）")
            return 1
    else:
        print(f"[生成] {os.path.relpath(GEN, ROOT)}：内容已是最新（未重写）")

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
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndTarget.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print("[接线] 已注入 3 行到 csproj（Remove 落在上游 Include 之后）")

    if check_only:
        print("[检查] 生成物就位且与上游同步")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
