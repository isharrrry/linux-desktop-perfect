#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c 补丁 M **+ 补丁 O** 的应用器：TSF 的 `Registry` null 守卫 + Linux「无 TSF」的契约返回 null
（**无参运行 = 应用**，与家族其余应用器一致）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textservices.py
        → 生成 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/TextServicesLoader.Linux.cs **并**给 csproj 接线（幂等）

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textservices.py --check
        → 只检查（不写盘）：生成物是否最新、csproj 是否已接线；**未就位 ⇒ 退出码 1**

    附加（可与上面共用）：--out PATH 额外写一份便于核对；--diff 打印改动行；--csproj PATH 覆盖 csproj（自测用）

【为什么"无参必须 = 应用"】（2026-09 主控实测的假绿，跨了两波）
  本脚本第一版把"没给动作"当成"什么都不做但成功退出"（`未指定动作…` + `exit 0`）。
  集成波按**退出码**判成败 ⇒ 波打印了 `✅ 补丁 M`，**而它一个字节都没改**：
  `TextServicesLoader.Linux.cs` 不存在、csproj 仍是编译上游原文件 ⇒ WindowsBase.dll 里没有修法。
  家族约定是"**其余 7 个应用器无参运行 = 生成文件**"，本脚本现已回归该约定（并接受波的空操作标记护栏）。

【为什么每次都重做 csproj 接线】波的第 1 步 `port-lib` 会**重生成 csproj**，
  接线只能靠应用器**每次重放**才活得下来（幂等：已接线就跳过，不重复注入）。

【它挡的是什么（实测：真 WPF 应用一收到鼠标输入就 SIGABRT）】
    Unhandled exception. System.NullReferenceException
       at MS.Internal.TextServicesLoader.TIPsWantToRun()             TextServicesLoader.cs:192
       at MS.Internal.TextServicesLoader.get_ServicesInstalled()     :133
       at System.Windows.Input.TextServicesManager.PreProcessInput   TextServicesManager.cs:118
       at System.Windows.Input.InputManager.ProcessStagingArea()     InputManager.Linux.cs:728
       at System.Windows.Interop.HwndMouseInputProvider.ReportInput  :1422
       → Application.Run → 进程 SIGABRT（134）
  ⇒ **任何一次鼠标移动**都够触发。M2 验收全程没有输入，所以从没撞到。

【根因：与补丁 G / J 同一家族】
  Unix 上 `Microsoft.Win32.Registry.CurrentUser` / `LocalMachine` / `ClassesRoot` 返回 **null**
  （不是抛异常）。第 192 行是 `Registry.CurrentUser.OpenSubKey("Software\\\\Microsoft\\\\CTF", false)`，
  调用方一个 catch 都没有。补丁 J 修了同一家族的 5 处，**漏了这一处** —— 因为它不在
  "输入设备初始化"那条路上，而在"**每次输入都过一次**"那条路上。

【修法：`?.` —— 把"根键可能不存在"写对，不是吞异常】
  · `:192` `Registry.CurrentUser.OpenSubKey(...)` → `Registry.CurrentUser?.OpenSubKey(...)`
  · `:205` `IterateSubKeys(Registry.LocalMachine, …)` → 先取局部变量，为 null ⇒ `return false`
  语义：本方法的契约就是 "true if one or more text services are installed for the current user"、
  "If this method returns false, Load is guaranteed to return null"。
  Linux 上没有 CTF/TIP 注册表 ⇒ "没有安装任何文本服务"是**真话**，
  `ServicesInstalled == false` ⇒ `TextServicesManager` 走的是**上游为"Windows 上没装 TIP 的机器"写的那条分支**。
  Windows 上两个根键恒非 null ⇒ `?.` 与 null 分支永不触发，**行为逐字不变**。

【补丁 O（2026-09-12 主控批准）：Linux 上 `Load()` 按契约返回 null，**在 STA 断言之前**】
  症状（T3 wave-8 实测、`--only=textbox-edit`）：`Invariant failure: Load called on MTA thread!`
    → FailFast ⇒ exit=134。栈：`TextEditor.InitTextStore:1529` ← `DispatcherOperation.InvokeImpl`
    ← `Dispatcher.Run` ← `Application.Run` ⇒ **样例只调了 `_tb.Focus()`**（TextBox 获得键盘焦点时装 TSF 的常规动作）。
  根因：上游 `Load()` 的**第一句**就是
      `Invariant.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Load called on MTA thread!");`
    它排在 `if (ServicesInstalled)` **之前** ⇒ **与补丁 M 守卫出来的 `ServicesInstalled` 取值无关**
    （这就是补丁 M 挡不住它的原因）。而该断言在 .NET on Unix 上**不可满足**（实测 net10.0/linux-x64）：
      进入主线程 `GetApartmentState() == Unknown`（不是 MTA）；
      `SetApartmentState(STA)` 抛 `PlatformNotSupportedException: COM Interop is not supported on this platform`；
      `TrySetApartmentState(STA)` 返回 **False**、状态仍 `Unknown`。
  修法（方案 a）：在断言**之前**加"非 Windows ⇒ `return null`"。它走的是上游**自己文档化的合法路径**：
      · `Load()` 的 XML 文档：`May return null if no text services are available.`
      · `ServicesInstalled` 的 remarks（上游 `:121`，原文拼写）：`If this method returns false,
        TextServicesLoader.Load is guarenteed to return null.`
      · 调用方本来就处理 null：`TextEditor.cs:1529` 拿到后紧跟 `if (threadManager != null) { … }`；
        `TextServicesHost.cs:303` 的注释也写明 `might return null`。
    ⇒ **不是"把断言吞掉继续跑"**：断言原文**一行不改**（自检里强制它仍在、且在守卫之后），
      Windows 路径（含断言次序）逐字不变，本守卫在 Windows 上不触发。
  为什么不用"把断言限定到 Windows"（方案 b）：那会让上游不变量变成平台相关；(a) 更贴合契约。
  为什么用运行时 `System.OperatingSystem.IsWindows()` 而不是 `#if`：`#if` 要往 csproj 加 define，
    而 csproj 会被 `port-lib.py` 重生成 ⇒ 运行时判定把接线面收敛为 0（本文件的头注已记这条教训）。

  ⚠️ **目标程序集是 WindowsBase，不是 PresentationCore**（实测：启动钩子逐程序集找类型时 PC 里没有它）：
     `src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj:281` 正是那句
     `<Compile Include="$(UpstreamWpfRoot)src/…/Shared/MS/Internal/TextServicesLoader.cs" />`。
     崩溃栈里它被 PC 的 `TextServicesManager` 调到，是因为 PC 对 WB 的 internal 有 IVT。
     **打错程序集 = 一行都不生效。**

【重放顺序】port-lib.py → reapply-patches.py(F/D/G) → patch-presentationcore-apartment.py(H)
            → patch-presentationcore-fontcache.py(I) → patch-presentationcore-registry.py(J)
            → patch-presentationcore-olecontext.py(K) → patch-presentationframework-xamlaccess.py(L)
            → **本脚本(M + O)**
"""

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
TARGET = os.path.join(WB_DIR, "TextServicesLoader.Linux.cs")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")

UPSTREAM_REL = "Shared/MS/Internal/TextServicesLoader.cs"
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src", UPSTREAM_REL)

# csproj 接线：锚点与"上游 Include 行"都逐字取自现状，改不动就报错（不猜）
CSPROJ_MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
CSPROJ_UPSTREAM_INCLUDE = ('    <Compile Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/'
                           + UPSTREAM_REL + '" />')

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7c 补丁 M：TSF 的 Registry null 守卫"
                "（由 src/Linux/tools/patch-presentationcore-textservices.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7c 补丁 M 结束 ==== -->"

# 两处锚点：逐字来自 upstream，改不动就报错
ANCHOR_CU = '            key = Registry.CurrentUser.OpenSubKey("Software\\\\Microsoft\\\\CTF", false);'
ANCHOR_LM = ('            tipsWantToRun = IterateSubKeys(Registry.LocalMachine, '
             '"SOFTWARE\\\\Microsoft\\\\CTF\\\\TIP",new IterateHandler(SingleTIPWantsToRun), true) '
             '== EnableState.Enabled;')

REPL_CU = ('            // [M7c 补丁 M] Unix 上 Registry.CurrentUser 是 **null**（不是抛异常）——\n'
           '            //   补丁 J 修的是同一个家族，这一处当时漏了。`?.` 之后走的是上游**原有的**\n'
           '            //   "没有这个键"分支（下面每一处都判了 key != null）。\n'
           '            key = Registry.CurrentUser?.OpenSubKey("Software\\\\Microsoft\\\\CTF", false);')

REPL_LM = ('            // [M7c 补丁 M] Unix 上 Registry.LocalMachine 也是 null；没有 HKLM 就是\n'
           '            //   "这台机器没有 TIP"，按本方法的契约（返回 false ⇒ Load 返回 null）返回 false。\n'
           '            //   Windows 上恒非 null ⇒ 这一支是死代码，行为逐字不变。\n'
           '            RegistryKey hklm = Registry.LocalMachine;\n'
           '            if (hklm == null)\n'
           '            {\n'
           '                return false;\n'
           '            }\n'
           '\n'
           '            tipsWantToRun = IterateSubKeys(hklm, '
           '"SOFTWARE\\\\Microsoft\\\\CTF\\\\TIP",new IterateHandler(SingleTIPWantsToRun), true) '
           '== EnableState.Enabled;')

# ── 补丁 O 的锚点与替换（第三处）────────────────────────────────────────────
# 锚点 = 上游 `Load()` 的第一句（逐字）。替换 = **守卫 + 断言原句**（断言原文一字不改）。
ANCHOR_STA = ('            Invariant.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, '
              '"Load called on MTA thread!");')

# 守卫本体（单独抽出，供自检"守卫必须排在断言之前"用）
GUARD_O = ('            if (!System.OperatingSystem.IsWindows())\n'
           '            {\n'
           '                return null;\n'
           '            }\n')

REPL_STA = (
    '            // [M7c 补丁 O] Linux：没有 CTF/Cicero（无 msctf / TF_CreateThreadMgr）⇒ 按本方法**自己的契约**返回 null。\n'
    '            //   依据①本方法 XML 文档："May return null if no text services are available."\n'
    '            //   依据②`ServicesInstalled` 的 remarks（上游 :121，原文拼写）："If this method returns false,\n'
    '            //          TextServicesLoader.Load is guarenteed to return null."\n'
    '            //          ⇒ 返回 null 是**契约内的合法路径**，不是"把断言吞掉继续跑"。\n'
    '            //   依据③调用方本来就处理 null：TextEditor.cs:1529 `threadManager = TextServicesLoader.Load();`\n'
    '            //          紧跟 `if (threadManager != null) { … }`（上游原文已核）。\n'
    '            //   为什么必须在断言**之前**：下面那条断言在 .NET on Unix 上**不可满足** —— 实测（net10.0/linux-x64）：\n'
    '            //     进入主线程 `GetApartmentState() == Unknown`（不是 MTA）；`SetApartmentState(STA)` 抛\n'
    '            //     PlatformNotSupportedException；`TrySetApartmentState(STA)` 返回 False 且状态仍 Unknown\n'
    '            //     ⇒ 任何"聚焦 TextBox"（TextEditor.InitTextStore → Load）都会 FailFast（T3 wave-8 实测 exit=134）。\n'
    '            //   **断言原文一行不改**：Windows 上（含断言次序）逐字不变；本守卫在 Windows 上不触发。\n'
    + GUARD_O +
    '\n'
    + ANCHOR_STA)

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textservices.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 + **补丁 M 的两处守卫 + 补丁 O 的一处守卫**：
//   ① `Registry.CurrentUser.OpenSubKey(...)`            → `Registry.CurrentUser?.OpenSubKey(...)`
//   ② `IterateSubKeys(Registry.LocalMachine, ...)`      → 先取局部变量，null ⇒ `return false`
//   ③ `Load()`：`Invariant.Assert(… STA …)` **之前**插入 非 Windows ⇒ `return null`
// 为什么必须打 ①②：Unix 上 `Microsoft.Win32.Registry.CurrentUser/LocalMachine` 返回 **null**
//   （不是抛异常），而上游直接解引用 ⇒ NullReferenceException ⇒ 真应用收到**任何鼠标输入**都 SIGABRT
//   （栈：TextServicesManager.PreProcessInput → TextServicesLoader.TIPsWantToRun → :192）。
// 为什么必须打 ③：`Load()` 的 STA 断言排在 `if (ServicesInstalled)` **之前**（⇒ 与 ①② 的取值无关），
//   而 .NET on Unix 上该断言**不可满足**（实测：主线程 `GetApartmentState() == Unknown`；
//   `SetApartmentState(STA)` 抛 PlatformNotSupportedException；`TrySetApartmentState(STA)` 返回 False）
//   ⇒ 任何"聚焦 TextBox"（TextEditor.InitTextStore → Load）都 FailFast（T3 wave-8 实测 exit=134）。
//   ③ 走的是上游**文档化的合法返回值**（`Load()` 的 "May return null…" + `ServicesInstalled` remarks
//   "false ⇒ Load is guarenteed to return null"），**断言原文一字未改**。
// 语义：Linux 上没有 CTF/TIP 注册表 ⇒ "没有安装文本服务"（本方法契约里的合法返回值）；
//   Windows 上两个根键恒非 null、且 `OperatingSystem.IsWindows()` 为真 ⇒ 三处守卫永不触发，行为逐字不变。
// 每次运行该脚本都会从上游重读重生成；锚点计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// ↓↓↓ 以下为上游原文（仅补丁 M 的两处 + 补丁 O 的一处有改动）↓↓↓
"""


def build_patched(text):
    """返回打过补丁的文本；锚点缺失/重复一律抛 SystemExit；三处修法各带自检。"""
    for anchor, name in ((ANCHOR_CU, "①（Registry.CurrentUser.OpenSubKey）"),
                         (ANCHOR_LM, "②（IterateSubKeys(Registry.LocalMachine…)）"),
                         (ANCHOR_STA, "③（Load() 的 STA 断言）")):
        if text.count(anchor) != 1:
            raise SystemExit(f"[补丁 M/O] 锚点{name}出现 {text.count(anchor)} 次，期望 1 次 —— 上游变了？")

    patched = (text.replace(ANCHOR_CU, REPL_CU)
                   .replace(ANCHOR_LM, REPL_LM)
                   .replace(ANCHOR_STA, REPL_STA))

    # 自检 A（补丁 M）：TIPsWantToRun 里不应再有未加守卫的 Registry 根键解引用
    body = patched[patched.index("private static bool TIPsWantToRun()"):]
    body = body[:body.index("private static EnableState SingleTIPWantsToRun")]
    bad = re.findall(r"Registry\.(CurrentUser|LocalMachine|ClassesRoot)\.", body)
    if bad:
        raise SystemExit(f"[补丁 M] 自检失败：TIPsWantToRun 里仍有未加守卫的 Registry 根键解引用 {bad}")

    # 自检 B（补丁 O）：STA 断言原文必须**仍在** —— 防"把不变量削弱/删掉换绿灯"
    if patched.count(ANCHOR_STA) != 1:
        raise SystemExit("[补丁 O] 自检失败：STA 断言原文被改动/丢失"
                         "（补丁 O 只允许在它**之前**加守卫，不许削弱上游不变量）")
    # 自检 C（补丁 O）：守卫必须**排在断言之前**（排在后面 ⇒ Linux 上仍会先撞断言）
    if patched.count(GUARD_O) != 1:
        raise SystemExit(f"[补丁 O] 自检失败：Linux 守卫出现 {patched.count(GUARD_O)} 次，期望 1 次")
    if patched.index(GUARD_O) > patched.index(ANCHOR_STA):
        raise SystemExit("[补丁 O] 自检失败：守卫没有排在 STA 断言之前 ⇒ Linux 上仍会先 FailFast")
    return patched


def wire_csproj(csproj_path, check_only):
    """把补丁 M 的 Remove/Include 注入 csproj（幂等）。返回 0/1。"""
    if not os.path.exists(csproj_path):
        print(f"[失败] 找不到 csproj：{csproj_path}")
        return 1

    with open(csproj_path, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次本脚本（无参即应用）")
        return 1

    if CSPROJ_MARKER not in csproj:
        print(f"[失败] csproj 里找不到 Sdk.targets 锚点：{CSPROJ_MARKER}")
        return 1
    if CSPROJ_UPSTREAM_INCLUDE not in csproj:
        print(f"[失败] csproj 里找不到上游 Include 行，接线锚点对不上：\n       {CSPROJ_UPSTREAM_INCLUDE}")
        return 1

    block = "\n".join([
        MARKER_BEGIN,
        "  <ItemGroup>",
        f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/{UPSTREAM_REL}" />',
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/TextServicesLoader.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
        "",
    ])
    csproj = csproj.replace(CSPROJ_MARKER, block + CSPROJ_MARKER, 1)
    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(csproj_path, ROOT)}"
          "（Remove 上游 TextServicesLoader.cs + Include 生成物）")
    return 0


def generate(check_only, out_path=None, csproj_path=None, show_diff=False):
    if not os.path.isfile(UPSTREAM):
        print(f"[失败] 找不到上游文件：{UPSTREAM}")
        return 1

    with open(UPSTREAM, encoding="utf-8-sig") as f:
        original = f.read()

    patched = build_patched(original)
    content = HEADER.format(upstream=UPSTREAM_REL) + patched

    print(f"[补丁 M] 上游：{os.path.relpath(UPSTREAM, ROOT)}")
    print("[补丁 M] 改动：TIPsWantToRun: Registry.CurrentUser → ?. ；"
          "Registry.LocalMachine → 局部变量 + null ⇒ return false")
    print("[补丁 O] 改动：Load(): STA 断言**之前**插入 `if (!System.OperatingSystem.IsWindows()) return null;`"
          "（断言原文不改；三处自检：锚点计数=1 / 断言仍在 / 守卫在断言之前）")
    print(f"[补丁 M] 行数 {len(original.splitlines())} → {len(patched.splitlines())}"
          f"（+{len(patched.splitlines()) - len(original.splitlines())}，不含生成头）")

    if show_diff:
        for i, (a, b) in enumerate(zip(original.splitlines(), patched.splitlines()), 1):
            if a != b:
                print(f"  L{i}\n    - {a}\n    + {b}")

    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[补丁 M] 已另写一份（核对用）：{out_path}")

    up_to_date = os.path.exists(TARGET)
    if up_to_date:
        with open(TARGET, encoding="utf-8") as f:
            up_to_date = (f.read() == content)

    if check_only:
        print(f"[检查] {os.path.relpath(TARGET, ROOT)}：{'内容已是最新' if up_to_date else '缺失/与上游不同步'}")
        rc = 0 if up_to_date else 1
    elif up_to_date:
        print(f"[生成] {os.path.relpath(TARGET, ROOT)}：内容已是最新（未重写）")
        rc = 0
    else:
        os.makedirs(os.path.dirname(TARGET), exist_ok=True)
        with open(TARGET, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[生成] {os.path.relpath(TARGET, ROOT)}：已从上游重生成")
        rc = 0

    wire_rc = wire_csproj(csproj_path or CSPROJ, check_only)
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj -m:1")
    return 0 if (rc == 0 and wire_rc == 0) else 1


def main():
    ap = argparse.ArgumentParser(
        description="补丁 M 应用器（**无参运行 = 生成 + csproj 接线**，与家族其余应用器一致）")
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件（未就位 ⇒ 退出码 1）")
    ap.add_argument("--out", metavar="PATH", help="额外写一份生成物到指定路径（便于核对）")
    ap.add_argument("--diff", action="store_true", help="打印改动前后的那些行")
    ap.add_argument("--csproj", metavar="PATH",
                    help="覆盖 csproj 路径（自测用；默认 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj）")
    args = ap.parse_args()
    return generate(args.check, args.out, args.csproj, args.diff)


if __name__ == "__main__":
    sys.exit(main())
