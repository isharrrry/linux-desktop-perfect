#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PM-0102 · 渲染时钟只读诊断：给 MediaContext 加 4 处只读插桩（缺省关、有界、不改行为）。

    python3 40-build/native-shim/tools/patch-presentationcore-renderclock-trace.py --check
    python3 40-build/native-shim/tools/patch-presentationcore-renderclock-trace.py

【为什么打】PM-0102：「空闲时应用仍在 13 fps 持续整帧渲染、白烧 CPU」。平台只读链已确认
  调度闭环在 `MediaContext.ScheduleNextRenderOp`：`_timeManager.GetNextTickNeeded() >= 0`
  就永远排下一帧。要在真机上证明「时钟一直非负」并拿到每帧成本，必须在这四处打点：

  1. `ScheduleNextRenderOp` 入口：`minimumDelay`（= `_timeDelay` 10ms）、`Rendering` 是否有订阅者、
     `InterlockIsEnabled`、`_interlockState`、`_needToCommitChannel`；
  2. `nextTickNeeded = _timeManager.GetNextTickNeeded();` 之后：**时钟原始需求值**（>=0 即「时钟一直在要帧」）；
  3. 协商完的 nextTickNeeded（进入 interlock 分支前后）：调度间隔真值；
  4. `Render((ICompositionTarget)resizedCompositionTarget);` 前后：每帧渲染耗时（ms）+ 帧号。

【只读】4 处都只插入语句；**不改控制流、不改返回值、不吞异常、不加 try/catch、不加 using**。
  `throw` 条数与大括号盈亏与上游逐字节一致（本仓 applier 家族的硬断言）。
【缺省关】`WPF_LINUX_RENDERCLOCK_DIAG=1` 才打；每进程 ≤ 5000 行。
【口径】本仪器属**工程诊断**：不得进任何验收跑法（质量判定器已把 `RENDERCLOCK_DIAG` 类开关列为禁止项）。
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def _find_repo():
    """从脚本所在目录向上找含 `40-build` 的仓库根（工具目录被搬过几次，不靠固定层级）。"""
    cur = HERE
    while True:
        if os.path.isdir(os.path.join(cur, "40-build")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            raise SystemExit("ERROR: 找不到仓库根（含 40-build 的目录）")
        cur = parent


REPO = _find_repo()
PC_DIR = os.path.join(REPO, "40-build", "wpf-port", "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

UP_REL = "src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/MediaContext.cs"
UPSTREAM = os.path.join(REPO, "40-build", "build", "wpf", UP_REL)
GEN = os.path.join(PC_DIR, "MediaContext.Linux.cs")

MARKER_BEGIN = "  <!-- ==== PM-0102：MediaContext 渲染时钟只读诊断（本文件由 40-build/native-shim/tools/patch-presentationcore-renderclock-trace.py 生成）==== -->"
MARKER_END = "  <!-- ==== /PM-0102 ==== -->"

HEADER = """// ⚠️ 本文件由 40-build/native-shim/tools/patch-presentationcore-renderclock-trace.py **生成**，不要手改。
//
// 内容 = 上游 `{rel}` 逐字复制 + {n} 处只读插桩（PM-0102：空闲时谁在要帧）。
// 每次运行会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
// 插桩**缺省关**（`WPF_LINUX_RENDERCLOCK_DIAG=1` 才打）、**有界**（≤5000 行/进程）、**只打印**。

"""

TRACE_CLASS = '''        // ── PM-0102 只读仪器（缺省关、有界；env=WPF_LINUX_RENDERCLOCK_DIAG）──
        internal static class WpfLinuxRenderClockTrace
        {
            private static int _on = -1;
            private static int _n;
            private static long _frames;
            internal static bool On()
            {
                if (_on < 0) _on = Environment.GetEnvironmentVariable("WPF_LINUX_RENDERCLOCK_DIAG") == "1" ? 1 : 0;
                return _on == 1;
            }
            internal static void Log(string msg)
            {
                if (!On() || _n++ >= 5000) return;
                Console.Error.WriteLine("[RCLK] " + msg);
                Console.Error.Flush();
            }
            internal static long NextFrame()
            {
                return ++_frames;
            }
        }

'''

A_ANCHOR = "        private void ScheduleNextRenderOp(TimeSpan minimumDelay)\n        {\n"
A_REPL = (TRACE_CLASS + A_ANCHOR +
          '            WpfLinuxRenderClockTrace.Log("sched min=" + minimumDelay.TotalMilliseconds.ToString("F1") '
          '+ "ms rendering=" + (Rendering == null ? "0" : "1") '
          '+ " interlock=" + (InterlockIsEnabled ? "1" : "0") '
          '+ " state=" + _interlockState.ToString() '
          '+ " needCommit=" + (_needToCommitChannel ? "1" : "0"));\n')

B_ANCHOR = "                    nextTickNeeded = _timeManager.GetNextTickNeeded();\n"
B_REPL = (B_ANCHOR +
          '                    WpfLinuxRenderClockTrace.Log("clock demand=" '
          '+ nextTickNeeded.TotalMilliseconds.ToString("F1") + "ms (>=0 means a clock wants a frame)");\n')

C_ANCHOR = ("                if (nextTickNeeded >= TimeSpan.Zero)\n"
            "                {\n"
            "                    nextTickNeeded = TimeSpan.FromTicks(Math.Max(nextTickNeeded.Ticks, minimumDelay.Ticks));\n"
            "                    EnterInterlockedPresentation();\n"
            "                }\n"
            "                else\n"
            "                {\n"
            "                    LeaveInterlockedPresentation();\n"
            "                }\n")
C_REPL = (C_ANCHOR +
          '                WpfLinuxRenderClockTrace.Log("tick next=" + nextTickNeeded.TotalMilliseconds.ToString("F1") '
          '+ "ms interlock=" + (InterlockIsEnabled ? "1" : "0") '
          '+ " state=" + _interlockState.ToString());\n')

D_ANCHOR = "                Render((ICompositionTarget)resizedCompositionTarget);\n"
D_REPL = ('                long __wpfLinuxRcT0 = System.Diagnostics.Stopwatch.GetTimestamp();\n'
          + D_ANCHOR +
          '                WpfLinuxRenderClockTrace.Log("frame #" + WpfLinuxRenderClockTrace.NextFrame() '
          '+ " renderMs=" + ((System.Diagnostics.Stopwatch.GetTimestamp() - __wpfLinuxRcT0) * 1000.0 '
          '/ System.Diagnostics.Stopwatch.Frequency).ToString("F1") '
          '+ " interlock=" + (InterlockIsEnabled ? "1" : "0") '
          '+ " state=" + _interlockState.ToString());\n')

PATCHES = [
    ("A ScheduleNextRenderOp 入口", A_ANCHOR, A_REPL),
    ("B 时钟原始需求值", B_ANCHOR, B_REPL),
    ("C 协商后的调度间隔", C_ANCHOR, C_REPL),
    ("D 每帧渲染耗时", D_ANCHOR, D_REPL),
]


def fail(msg):
    print("ERROR: " + msg, file=sys.stderr)
    sys.exit(1)


def build_source():
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        src = f.read()
    throw_before = src.count("throw ")
    braces_before = src.count("{") - src.count("}")
    for name, anchor, repl in PATCHES:
        n = src.count(anchor)
        if n != 1:
            fail("锚点命中数不符：%s（%d 处）" % (name, n))
        src = src.replace(anchor, repl, 1)
    throw_after = src.count("throw ")
    braces_after = src.count("{") - src.count("}")
    if throw_after != throw_before:
        fail("throw 条数变了：%d → %d" % (throw_before, throw_after))
    if braces_after != braces_before:
        fail("大括号盈亏变了：%d → %d" % (braces_before, braces_after))
    return src


def wiring_state(csproj):
    """纯判据：csproj 里有没有本脚本的接线标记。wired / unwired。"""
    return "wired" if MARKER_BEGIN in csproj else "unwired"


def apply_wiring(csproj):
    """返回接线后的 csproj 文本（幂等）。找不到 </Project> 直接失败。"""
    if MARKER_BEGIN in csproj:
        return csproj
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             '    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/MediaContext.cs" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MediaContext.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    idx = csproj.rfind("</Project>")
    if idx < 0:
        fail("csproj 里找不到 </Project>")
    return csproj[:idx] + block + csproj[idx:]


def wire_csproj(dry_run):
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    if wiring_state(csproj) == "wired":
        return "csproj: 已接线（幂等）"
    if dry_run:
        # PM-0102 修正：此前 dry_run 会无条件报「已接线」，是假阳性。
        return "csproj: 未接线（需先运行本脚本接线；--check 因此判失败）"
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(apply_wiring(csproj))
    return "csproj: 已接线（Remove 上游 + Include 生成物）"


def selftest():
    """红测：喂已知未接线的 csproj，必须报未接线；接线后必须报已接线且幂等。"""
    failures = []
    empty = '<?xml version="1.0" encoding="utf-8"?>\n<Project Sdk="Microsoft.NET.Sdk">\n</Project>\n'

    if wiring_state(empty) != "unwired":
        failures.append("未接线 fixture 被判成 wired")
    wired = apply_wiring(empty)
    if wiring_state(wired) != "wired":
        failures.append("接线后仍被判成 unwired")
    if "MediaContext.Linux.cs" not in wired:
        failures.append("接线块缺 Include MediaContext.Linux.cs")
    if 'Remove="' not in wired:
        failures.append("接线块缺 Remove 上游 MediaContext.cs")
    if apply_wiring(wired) != wired:
        failures.append("接线不幂等")

    # 真实 csproj：只报告状态，不写盘
    try:
        with open(CSPROJ, encoding="utf-8-sig") as f:
            real = wiring_state(f.read())
        print("[自检] 真实 csproj 当前接线状态：%s" % real)
    except Exception as e:  # noqa: BLE001
        failures.append("读真实 csproj 失败：%s" % e)

    for msg in failures:
        print("[自检][FAIL] " + msg)
    print("[自检] %s（%d 项断言）" % ("PASS" if not failures else "FAIL", 6))
    return 0 if not failures else 1



def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只比对，不写盘；生成物陈旧或 csproj 未接线时退出码 1")
    ap.add_argument("--selftest", action="store_true", help="红测：验证接线检查能识别未接线（必须报未接线）")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    src = build_source()
    body = HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(PATCHES))) + src

    if args.check:
        if not os.path.exists(GEN):
            fail("生成物不存在：%s" % GEN)
        with open(GEN, encoding="utf-8-sig") as f:
            old = f.read()
        if old != body:
            fail("生成物与上游不一致（需重跑本脚本）")
        print("[检查] src/MediaContext.Linux.cs：内容已是最新")
        print("[接线] " + wire_csproj(dry_run=True))
        with open(CSPROJ, encoding="utf-8-sig") as f:
            state = wiring_state(f.read())
        if state != "wired":
            print("[检查] FAIL：csproj 未接线，诊断构建会静默使用上游 MediaContext（无插桩）")
            return 1
        return 0

    with open(GEN, "w", encoding="utf-8") as f:
        f.write(body)
    print("[生成] %s（%d 处插桩）" % (GEN, len(PATCHES)))
    print("[接线] " + wire_csproj(dry_run=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
