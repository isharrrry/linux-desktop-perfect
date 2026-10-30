#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""拦路虎 ① 的一键应用器：去掉 PresentationCore `InputManager` 的硬 STA 检查（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py [--apply]

【为什么需要这个补丁：根因（已实测，非推断）】
  1. `PresentationCore/System/Windows/Input/InputManager.cs:144`（私有构造函数）：
         if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
             throw new InvalidOperationException(SR.RequiresSTA);
     —— 这是**硬检查**，没有任何 AppContext 开关可以绕过。
  2. Linux 上这个条件**恒真**：ManagedLayer.Tests 的
     `ApartmentState_NeverReportsSTA_OnLinux` 实测
       主线程 GetApartmentState()      = Unknown
       新建线程 GetApartmentState()    = Unknown
       SetApartmentState(STA)         不被接受（抛 PlatformNotSupportedException）
     —— .NET 在 Unix 上不提供 STA。
  3. `HwndSource.Initialize`（HwndSource.cs:212/213）**无条件**创建
        _mouse    = new HwndMouseInputProvider(this);
        _keyboard = new HwndKeyboardInputProvider(this);
     两者的构造函数第一句都是 `InputManager.Current`（各自 .cs:21 / :18），
     于是 `HwndSource` 在 Linux 上**永远构造不出来** —— 没有开关、没有分支能绕开。

  ⇒ 任何 WPF 窗口（HelloWpf 也好，真实的 WPF 应用也好）都卡在这一步。

【修法：1 行】
     - if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
     + if (OperatingSystem.IsWindows() &&
     +     Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
  语义论证：这条检查的**理由**写在它自己的注释里 ——
     "Avalon doesn't necessarily require STA, but many components do.
      Examples include Cicero, OLE, COM, etc."
  这三样（TSF/Cicero、OLE、COM apartment）在 Linux 上**都不存在**：
  TSF 走 `TextServicesLoader`（本工程已按"无 TSF"分支处理）、OLE 剪贴板/DnD 已被 M4
  裁决为 `PlatformNotSupportedException` 的最小诚实 stub、COM 在 Linux 上没有 apartment 概念。
  所以在非 Windows 上跳过这个检查**不是"放水"**，而是"这条前置条件在这里没有对象"。
  `OperatingSystem.IsWindows()` 在 Linux 上恒 false ⇒ 完全不影响 Windows 语义。

【为什么用"生成"而不是"改上游"】
  与 `src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py` 的补丁 G 同一套机制：
    · 从 `upstream/` 逐字读入 `InputManager.cs`；
    · 锚点找不到 → **报错退出**（不静默产出一个没打补丁的副本）；
    · 写出 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputManager.Linux.cs`（与本目录的
      `SR.g.cs` 同为生成物），csproj 侧 Remove 上游 + Include 生成物。
  于是上游仍是唯一事实源，`port-lib.py` 重生成后重跑本脚本即可恢复。

【幂等 / 回滚】
  · 重复运行 0 改动（比对内容，相同就不重写，避免动时间戳触发全量重编）。
  · 回滚 = 删掉 csproj 里的那 3 行（`<Compile Remove>` + `<Compile Include>` 与注释）
    并删除生成物；或重跑 `port-lib.py PresentationCore` 后**不要**再跑本脚本。

【T1c 第 2 批（2026-09-13）追加：P2 + P5 只读插桩】
  生成物 `InputManager.Linux.cs` 的**唯一写者仍是本脚本**（单写者纪律：不另起一个 applier
  去写同一个文件，否则"谁后跑谁赢"会**静默**吃掉别人的补丁）。本脚本因此在补丁 H 之外
  多了 4 处**只插入**的 T1c 探针（都在原语句**之间**，不替换、不包裹）：
    · P2  `ProcessInput(InputEventArgs)` 入口：报告类型/RoutedEvent/Source + **焦点在谁身上**
          （`KeyboardDevice.FocusedElement`）；返回处再打一行 handled（只对 Text 类）。
    · P5  `ProcessStagingArea` 的 `RaiseEvent` 之前/之后：**事件名 + 目标元素** + Handled
          （`TextInput`/`PreviewTextInput` 必打；其它事件采样——每个输入事件都会经过这里）。
  跨命名空间注意：插桩类 `WpfLinuxInputTrace` 在 `System.Windows.Interop`，本文件是
  `System.Windows.Input` ⇒ 调用点**必须全限定**。
  机械保证：`--prove` 逆序回代后与上游 **逐字节相同**；`throw` 条数与**大括号盈亏**都不变。
}

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py --prove   # 「只插入」证明（只读）
"""

import argparse
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
GENERATED = os.path.join(PC_DIR, "InputManager.Linux.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf",
                        "src", "PresentationCore", "System", "Windows", "Input", "InputManager.cs")

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux M7b 补丁 H：InputManager 的 STA 检查（由 src/Linux/tools/patch-presentationcore-apartment.py 注入）==== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux M7b 补丁 H 结束 ==== -->"

ANCHOR = "            if(Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)"
REPLACEMENT = """            // ── WPF-on-Linux M7b 补丁 H（由 src/Linux/tools/patch-presentationcore-apartment.py 插入）──
            // Linux 上线程永远报告 ApartmentState.Unknown，且 SetApartmentState(STA) 不被接受
            // （实测见 src/Linux/tests/.../ManagedLayer.Tests/LinuxEnvironmentDiagnosticsTests.cs 的
            //   ApartmentState_NeverReportsSTA_OnLinux）。这条检查在非 Windows 上没有对象：
            // 它注释里点名的 Cicero(TSF)/OLE/COM 三样在 Linux 上都不存在
            // （TSF 走"无 TSF"分支、OLE 已是 PlatformNotSupportedException 的诚实 stub）。
            // 不跳过的话：HwndSource.Initialize 无条件建 HwndMouse/KeyboardInputProvider
            // → InputManager.Current → 这里抛异常 ⇒ **HwndSource 永远构造不出来**。
            if(OperatingSystem.IsWindows() &&
               Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)"""

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-apartment.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/InputManager.cs`
//        逐字复制 + 在 STA 检查上插入 `OperatingSystem.IsWindows() &&`（见下方补丁 H 标记）
//        + 4 处 T1c 第 2 批**只读插桩**（P2 入口/返回、P5 RaiseEvent 之前/之后）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么必须打这个补丁（实测根因）：
//   1. InputManager.cs:144 硬检查 `Thread.CurrentThread.GetApartmentState() != STA` → throw；
//   2. Linux 上 **没有任何线程能报告 STA**（GetApartmentState 恒 Unknown，
//      SetApartmentState(STA) 抛 PlatformNotSupportedException）⇒ 条件恒真；
//   3. HwndSource.Initialize（HwndSource.cs:212/213）**无条件**创建
//      HwndMouseInputProvider / HwndKeyboardInputProvider，两者构造函数第一句都是
//      `InputManager.Current` ⇒ **HwndSource 在 Linux 上永远构造不出来**。
//   语义：该检查的理由（Cicero/OLE/COM）在 Linux 上都没有对象，跳过不是放水。
//   `OperatingSystem.IsWindows()` 在 Linux 恒 false ⇒ 完全不影响 Windows 语义。
//
// T1c 第 2 批插桩（为什么在这里）：
//   第 1 批实跑表明 char 到了 OnPreprocessMessage、`_eatCharMessages` 全 False、三步均未
//   置 handled，最后 `ProcessTextInputAction ⇒ handled=True` 而 TextBox 文本未变。
//   P2/P5 回答"报告交出去之后，**焦点在谁身上**、**TextInput 有没有 raise、raise 给了谁**"。
//   缺省关（WPF_LINUX_INPUT_TRACE）、有界、只打印；调用点**全限定**（类在 System.Windows.Interop）。
//
// ↓↓↓ 以下为上游原文（仅插入处有补丁 H 标记 / T1c 标记）↓↓↓
"""

# --------------------------------------------------------------------------------------
#  T1c 第 2 批：4 处**只插入**探针（锚点各要求上游恰好命中 1 次）
# --------------------------------------------------------------------------------------
P2_ENTRY_ANCHOR = '''        public bool ProcessInput(InputEventArgs input)
        {
            //             VerifyAccess();
'''

P2_RESULT_ANCHOR = '''            // Now drain the staging area up to the marker we pushed.
            bool handled = ProcessStagingArea();
            return handled;
'''

P5_RAISE_ANCHOR = '''                        if (eventSource != null)
                        {
                            if (eventSource is UIElement e)
                            {
                                e.RaiseEvent(input, true); // Call the "trusted" flavor of RaiseEvent. 
'''

P5_RESULT_ANCHOR = '''                            else if (eventSource is UIElement3D e3D)
                            {
                                e3D.RaiseEvent(input, true); // Call the "trusted" flavor of RaiseEvent
                            }
'''

# (名字, 锚点, 替换) —— 顺序即应用顺序；每处都要求锚点在上游**恰好 1 次**。
EDITS = [
    ("H STA 守卫（M7b 原有，**逐字未改**）", ANCHOR, REPLACEMENT),
    ("T1c P2 ProcessInput 入口", P2_ENTRY_ANCHOR, '''        public bool ProcessInput(InputEventArgs input)
        {
            // ── T1c 第 2 批 P2（只读插桩）：报告类型 + **焦点在谁身上** ──
            //    跨命名空间 ⇒ **全限定**（WpfLinuxInputTrace 在 System.Windows.Interop）
            System.Windows.Interop.WpfLinuxInputTrace.B2ProcessInputEntry(input, PrimaryKeyboardDevice);

            //             VerifyAccess();
'''),
    ("T1c P2' ProcessInput 返回", P2_RESULT_ANCHOR, '''            // Now drain the staging area up to the marker we pushed.
            bool handled = ProcessStagingArea();
            // ── T1c P2'（只读插桩）：返回的 handled（只对 Text 类报告打）──
            System.Windows.Interop.WpfLinuxInputTrace.B2ProcessInputResult(input, handled);
            return handled;
'''),
    ("T1c P5 RaiseEvent 之前", P5_RAISE_ANCHOR, '''                        if (eventSource != null)
                        {
                            // ── T1c 第 2 批 P5（只读插桩）：事件名 + **目标元素** ──
                            //    TextInput/PreviewTextInput 必打；其它事件采样
                            System.Windows.Interop.WpfLinuxInputTrace.B2RaiseInput(input, eventSource);

                            if (eventSource is UIElement e)
                            {
                                e.RaiseEvent(input, true); // Call the "trusted" flavor of RaiseEvent. 
'''),
    ("T1c P5' RaiseEvent 之后", P5_RESULT_ANCHOR, '''                            else if (eventSource is UIElement3D e3D)
                            {
                                e3D.RaiseEvent(input, true); // Call the "trusted" flavor of RaiseEvent
                            }

                            // ── T1c P5'（只读插桩）：RaiseEvent 之后的 Handled（只对文本事件打）──
                            System.Windows.Interop.WpfLinuxInputTrace.B2RaiseInputResult(input, input.Handled);
'''),
]

REQUIRED = [
    ("补丁 H 逐字在位", "            if(OperatingSystem.IsWindows() &&\n               Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)"),
    ("P2 入口调用点（全限定）",
     "System.Windows.Interop.WpfLinuxInputTrace.B2ProcessInputEntry(input, PrimaryKeyboardDevice);"),
    ("P2' 返回调用点（全限定）",
     "System.Windows.Interop.WpfLinuxInputTrace.B2ProcessInputResult(input, handled);"),
    ("P5 调用点（全限定）",
     "System.Windows.Interop.WpfLinuxInputTrace.B2RaiseInput(input, eventSource);"),
    ("P5' 调用点（全限定）",
     "System.Windows.Interop.WpfLinuxInputTrace.B2RaiseInputResult(input, input.Handled);"),
    ("上游 RaiseEvent 三条语句逐字未动", "e.RaiseEvent(input, true); // Call the \"trusted\" flavor of RaiseEvent."),
]


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def build(check_only=False):
    """读上游 → 锚点自检 → 应用 EDITS → 机械断言。返回 (生成物全文 或 None, rc)。"""
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return None, 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] InputManager {name}：上游出现 {n} 次（要求 1）")
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

    output = HEADER + out
    for name, needle in REQUIRED:
        if needle not in output:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中（含「补丁 H 逐字在位」）")
    return output, 0


def prove():
    """「只插入」机械证明：EDITS 逆序回代后必须与上游 sha256 逐字节相同。

    注意：补丁 H 本身是**一处替换**（不是纯插入），所以证明的做法是
    "把**所有**编辑（含补丁 H）逆序还原" —— 还原得回上游，就说明这批改动**没有**动
    任何原有语句的顺序/结构（除了补丁 H 那一行，它是本 applier 的原生职责）。
    """
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()
    if os.path.exists(GENERATED):
        with open(GENERATED, encoding="utf-8") as f:
            body = f.read()
        if body.startswith(HEADER):
            body = body[len(HEADER):]
            where = f"落盘生成物 {os.path.relpath(GENERATED, ROOT)}"
        else:
            print("[注意] 落盘生成物的**文件头与当前脚本不一致**（说明它是上一版脚本产的、还没重放）"
                  "⇒ 本证明退化为**内存**证明，不对落盘件背书。")
            body = None
    else:
        body = None

    if body is None:
        full, rc = build(check_only=True)
        if rc:
            return 1
        body = full[len(HEADER):]
        where = "**内存**（生成物尚未产出/已过时；重放后再跑一次才是对落盘件取的真值）"

    cur = body
    for name, anchor, repl in reversed(EDITS):
        n = _count(cur, repl)
        if n != 1:
            print(f"[失败] 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）⇒ 生成物被手改过？")
            return 1
        cur = cur.replace(repl, anchor, 1)
    if cur != text:
        print("[失败] 逆代后与上游**不一致** ⇒ 别信这份生成物。")
        return 1
    print(f"[① 只插入] 取自 {where}")
    print(f"[① 只插入] EDITS 逆序回代后与上游**逐字节相同** ✓ sha256={_sha(cur)}")
    print(f"[① 只插入] 上游 sha256={_sha(text)}（两条相同 ⇒ 只动了锚点本身与插入行）")
    return 0


def generate(check_only):
    output, rc = build(check_only=check_only)
    if rc:
        return rc

    up_to_date = False
    if os.path.exists(GENERATED):
        with open(GENERATED, encoding="utf-8") as f:
            up_to_date = (f.read() == output)

    if check_only:
        print(f"[检查] {os.path.relpath(GENERATED, ROOT)}："
              + ("内容已是最新" if up_to_date else "缺失/与上游不同步（需要重新生成）"))
    else:
        if up_to_date:
            print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：内容已是最新（未重写，不动时间戳）")
        else:
            with open(GENERATED, "w", encoding="utf-8") as f:
                f.write(output)
            print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：已从上游重生成"
                  f"（补丁 H + {len(EDITS) - 1} 处 T1c 插桩）sha256={_sha(output)}")

    # ── csproj 接线 ────────────────────────────────────────────────────────
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    wired = MARKER_BEGIN in csproj
    if wired:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0 if (up_to_date or not check_only) else 1

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if anchor not in csproj:
        print("[失败] csproj 里找不到 Sdk.targets 锚点")
        return 1

    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             '    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/InputManager.cs" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputManager.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1")
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
