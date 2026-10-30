#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c 补丁 J 的一键应用器：PresentationCore 输入栈里「Registry.* 在 Unix 上是 null」家族（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-registry.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-registry.py [--apply]

【根因：与 M7b 的补丁 G **完全相同**的那一类缺陷】
  Unix 上 `Microsoft.Win32.Registry.CurrentUser` / `LocalMachine` / `ClassesRoot` 返回
  **null**（不是抛异常）。上游多处直接 `Registry.X.OpenSubKey(...)`，而 catch 子句只捕
  `IOException`/`SecurityException` ⇒ **NullReferenceException**，谁也接不住。

  M7b 已经在 `Shared/MS/Internal/SecurityHelper.cs` 上修过一次（补丁 G，AvTrace 链）。
  本轮 Phase 2 实测又在**输入栈**撞上同一个坑，位置：
    · StylusLogic.cs:285   `IsPointerEnabledInRegistry`  ← HwndSource.Initialize:309 直接调
    · StylusLogic.cs:333   `WispPenSystemEventParametersKey`
    · StylusLogic.cs:347   `WispTouchConfigKey`
    · WispTabletDeviceCollection.cs:99  `Registry.ClassesRoot.OpenSubKey(...)`
    · TextCompositionManager.cs:903     `Registry.CurrentUser.OpenSubKey("Control Panel\\Input Method")`
  实测栈：
    NullReferenceException
      at System.Windows.Input.StylusLogic.get_IsPointerEnabledInRegistry()  StylusLogic.cs:285
      at System.Windows.Input.StylusLogic.get_IsPointerStackEnabled()       StylusLogic.cs:206
      at System.Windows.Interop.HwndSource.Initialize(HwndSourceParameters) HwndSource.cs:309
      at System.Windows.Interop.HwndSource..ctor(...)                       HwndSource.cs:203
      at System.Windows.Window.CreateSourceWindow(Boolean)                  Window.cs:2519
      at System.Windows.Window.ShowHelper(Object)                           Window.cs:5483
  ⇒ 它挡在 **任何 WPF 窗口第一次 Show()** 的路上。
  （全量清单不止这 5 处：编译集里一共 **15 处**未加守卫的 `Registry.*`，横跨
    WindowsBase / PresentationCore / PresentationFramework，逐条列在
    docs/U2-M7c-report.md 的缺口 #0'。本脚本只修**当前挡路且同属输入栈**的这 3 个文件；
    其余请按同一条 `?.` 手法一并处理。）

【修法：`?.` —— 不是绕过，是把"根键可能不存在"这件事写对】
  把
      Registry.CurrentUser.OpenSubKey(...)
  改成
      Registry.CurrentUser?.OpenSubKey(...)
  为什么这是**语义精确**的而不是"吞掉错误"：
    · 上游每一处拿到 `OpenSubKey` 的结果后都判了 `if (key != null)`，
      或用了 `?? 0`（见 StylusLogic.cs:285 的 `... ?? 0`）；
    · 所以 `?.` 让整条链返回 null 之后，上游**原有的**"没有这个键"分支会自然生效 ——
      这正是 Linux 上的**真话**：没有 Windows 注册表，也就没有"强制启用 WM_POINTER /
      wisptis / EnableHexNumpad"这些设置；
    · 结果与 Windows 上"这些键没被设置过"**逐条一致**（都是走默认分支）。
  对比补丁 G：那里是插一行 `if (baseRegistryKey is null) return null;`；
  这里更省，一个 `?` 字符，而且不改变任何控制流结构。

【为什么用生成式补丁】
  与补丁 G/H/I 同一套机制：从 upstream 逐字读入 → 只做上述替换 → 写到
  `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/<Name>.Linux.cs`（与 SR.g.cs 同为生成物），
  csproj 侧 Remove 上游 + Include 生成物。锚点/替换计数对不上 → **报错退出**（不静默降级）。
  重放顺序：port-lib.py → reapply-patches.py(F/D/G) → patch-presentationcore-apartment.py(H)
            → patch-presentationcore-fontcache.py(I) → 本脚本(J)
            （顺序与 T1c 插桩无关：两批插桩的锚点与补丁 J 的 `?.` 锚点互不重叠；但**必须都跑**。）

【T1c 第 2 批（2026-09-13）追加：TextCompositionManager 的 P3a/P3b/P4a/P4b 只读插桩】
  生成物 `TextCompositionManager.Linux.cs` 的**唯一写者仍是本脚本**（单写者纪律：不另起
  applier 写同一个文件，否则"谁后跑谁赢"会**静默**吃掉别人的补丁）。除补丁 J 的 `?.` 外，
  本脚本现在多了 5 处**只插入**的探针（都在原语句**之间**，不替换、不包裹）：
    P3a `Raw to StartComposition` 段**条件之前**（报告类型 / RoutedEvent ⇒ 条件真假可判）
    P3b **进了**分支体（码点 + IsControl/IsDead/IsSystem + StagingItem.Source）
    P4a 新建的 `TextComposition` 的 **Text 与目标元素**
    P4b `UnsafeStartComposition` 的**返回值**（正常字符 / dead char 各一处）
  跨命名空间：插桩类在 `System.Windows.Interop` ⇒ 调用点**必须全限定**。
  机械保证：`--prove` 逆序回代后与上游 **逐字节相同**；`throw` 条数与**大括号盈亏**都不变。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
UP = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                  "PresentationCore", "System", "Windows", "Input")

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7c 补丁 J：输入栈的 Registry null 守卫"
                "（由 src/Linux/tools/patch-presentationcore-registry.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7c 补丁 J 结束 ==== -->"

# (上游相对路径, 生成物文件名, [(原文, 替换, 期望出现次数)])
PATCHES = [
    ("Stylus/Common/StylusLogic.cs", "StylusLogic.Linux.cs", [
        ("Registry.CurrentUser.OpenSubKey", "Registry.CurrentUser?.OpenSubKey", 3),
        ("Registry.LocalMachine.OpenSubKey", "Registry.LocalMachine?.OpenSubKey", 0),
    ]),
    ("Stylus/Wisp/WispTabletDeviceCollection.cs", "WispTabletDeviceCollection.Linux.cs", [
        ("Registry.ClassesRoot.OpenSubKey", "Registry.ClassesRoot?.OpenSubKey", 1),
    ]),
    ("TextCompositionManager.cs", "TextCompositionManager.Linux.cs", [
        ("Registry.CurrentUser.OpenSubKey", "Registry.CurrentUser?.OpenSubKey", 1),
        # ── T1c 第 2 批（2026-09-13）：P3a/P3b/P4a/P4b 只读插桩 ─────────────────────
        #  第 1 批实跑：char 到了 OnPreprocessMessage、`_eatCharMessages` 全 False、
        #  三步都没置 handled，最后 `ProcessTextInputAction ⇒ handled=True` 而 TextBox
        #  文本没变 ⇒ 第 2 批要回答"TextInput 有没有被 raise、raise 给了谁"。
        #  这四格钉在 `Raw → StartComposition` 这条路上（**只插入**，不改任何原有语句）。
        #  注意：本文件是 `System.Windows.Input`，插桩类在 `System.Windows.Interop`
        #        ⇒ 调用点**必须全限定**（否则 CS0103）。
        ("""            InputReportEventArgs input = e.StagingItem.Input as InputReportEventArgs;
            if(input != null)
            {
""",
         """            InputReportEventArgs input = e.StagingItem.Input as InputReportEventArgs;
            if(input != null)
            {
                // ── T1c 第 2 批 P3a（只读插桩）：到达 Raw 段（**条件之前** ⇒ 条件真假可判）──
                System.Windows.Interop.WpfLinuxInputTrace.B2RawReport(input);

""", 1),
        ("""                    RawTextInputReport textInput;
                    textInput = (RawTextInputReport)input.Report;
""",
         """                    RawTextInputReport textInput;
                    textInput = (RawTextInputReport)input.Report;
                    // ── T1c P3b：**进了** Raw→StartComposition 分支体（码点 + 三标志 + 目标元素）──
                    System.Windows.Interop.WpfLinuxInputTrace.B2RawBranch(textInput, e);
""", 1),
        ("""                                TextComposition composition = new TextComposition(_inputManager, (IInputElement)e.StagingItem.Input.Source, inputText, TextCompositionAutoComplete.On, InputManager.Current.PrimaryKeyboardDevice);
""",
         """                                TextComposition composition = new TextComposition(_inputManager, (IInputElement)e.StagingItem.Input.Source, inputText, TextCompositionAutoComplete.On, InputManager.Current.PrimaryKeyboardDevice);
                                // ── T1c P4a：新建出来的 composition 的 **Text 与目标元素** ──
                                System.Windows.Interop.WpfLinuxInputTrace.B2CompositionCreated(composition);
""", 1),
        ("""                                input.Handled = UnsafeStartComposition(composition);
""",
         """                                input.Handled = UnsafeStartComposition(composition);
                                // ── T1c P4b：`UnsafeStartComposition` 的**返回值**（handled）──
                                System.Windows.Interop.WpfLinuxInputTrace.B2StartComposition("正常字符", input.Handled, composition);
""", 1),
        ("""                            input.Handled = UnsafeStartComposition(_deadCharTextComposition);
""",
         """                            input.Handled = UnsafeStartComposition(_deadCharTextComposition);
                            // ── T1c P4b（dead char 支路：只有真来 dead char 才会出现）──
                            System.Windows.Interop.WpfLinuxInputTrace.B2StartComposition("deadchar", input.Handled, _deadCharTextComposition);
""", 1),
    ]),
]

T1C_REQUIRED = {
    "TextCompositionManager.Linux.cs": [
        ("P3a Raw 段（条件之前）",
         "System.Windows.Interop.WpfLinuxInputTrace.B2RawReport(input);"),
        ("P3b 分支体", "System.Windows.Interop.WpfLinuxInputTrace.B2RawBranch(textInput, e);"),
        ("P4a 新建 composition", "System.Windows.Interop.WpfLinuxInputTrace.B2CompositionCreated(composition);"),
        ("P4b 正常字符返回", 'System.Windows.Interop.WpfLinuxInputTrace.B2StartComposition("正常字符", input.Handled, composition);'),
        ("P4b dead char 返回", 'System.Windows.Interop.WpfLinuxInputTrace.B2StartComposition("deadchar", input.Handled, _deadCharTextComposition);'),
        ("上游 StartComposition 两条语句逐字未动",
         "input.Handled = UnsafeStartComposition(composition);"),
    ],
}

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-registry.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 +
//        把 `Registry.<根键>.OpenSubKey` 改成 `Registry.<根键>?.OpenSubKey`（见下方补丁 J 标记）。
// 每次运行该脚本都会从上游重读重生成；替换计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// 为什么必须打：
//   Unix 上 `Microsoft.Win32.Registry.CurrentUser/LocalMachine/ClassesRoot` 返回 **null**
//   （不是抛异常），而上游直接 `Registry.X.OpenSubKey(...)`，catch 只捕 IOException
//   ⇒ NullReferenceException。实测它挡在 **任何 WPF 窗口第一次 Show()** 的路上
//   （Window.ShowHelper → HwndSource.Initialize:309 → StylusLogic.IsPointerEnabledInRegistry）。
// 语义：上游拿到 OpenSubKey 的结果后本来就判 `!= null` / 用 `?? 0`，所以 `?.` 之后
//   自然走"这个键没被设置过"的默认分支 —— 这正是 Linux 上的真话，与 Windows 上
//   "没配过这些键"逐条一致。
//
// ↓↓↓ 以下为上游原文（仅 Registry 处有补丁 J 的 `?.`）↓↓↓
"""


def generate(check_only):
    csproj_added = []
    stale = []
    for rel, gen_name, subs in PATCHES:
        src = os.path.join(UP, rel)
        gen = os.path.join(PC_DIR, gen_name)
        if not os.path.exists(src):
            print(f"[失败] 找不到上游 {src}")
            return 1
        with open(src, encoding="utf-8-sig") as f:
            text = f.read()

        out = text
        for old, new, expect in subs:
            if expect == 0:
                continue
            got = out.count(old)
            if got != expect:
                print(f"[失败] {os.path.basename(rel)}：期望 {expect} 处 `{old}`，实际 {got} 处 —— "
                      f"上游改过这段，补丁 J 不能盲目应用。")
                return 1
            out = out.replace(old, new)

        content = HEADER.format(upstream=rel) + out

        # ── 机械断言（T1c 第 2 批加）：只插入/替换不该改结构与异常面 ──
        if out.count("throw ") != text.count("throw "):
            print(f"[失败] {rel}：`throw` 条数变了 {text.count('throw ')} → {out.count('throw ')}")
            return 1
        if (out.count("{") - out.count("}")) != (text.count("{") - text.count("}")):
            print(f"[失败] {rel}：大括号盈亏变了（只读插桩不该改结构）")
            return 1
        need = T1C_REQUIRED.get(gen_name, [])
        for needle_name, needle in need:
            if needle not in content:
                print(f"[失败] {gen_name} 缺少结构断言：{needle_name}")
                return 1
        if need:
            print(f"[断言] {gen_name}：结构断言 {len(need)}/{len(need)} 全中；"
                  f"`throw` {out.count('throw ')}=={text.count('throw ')}；"
                  f"大括号 {out.count('{')}/{out.count('}')} 盈亏一致")

        up_to_date = os.path.exists(gen)
        if up_to_date:
            with open(gen, encoding="utf-8") as f:
                up_to_date = (f.read() == content)

        if check_only:
            print(f"[检查] {gen_name}：{'内容已是最新' if up_to_date else '缺失/与上游不同步'}")
        elif up_to_date:
            print(f"[生成] {gen_name}：内容已是最新（未重写）")
        else:
            with open(gen, "w", encoding="utf-8") as f:
                f.write(content)
            print(f"[生成] {gen_name}：已从上游重生成")
        if not up_to_date:
            stale.append(gen_name)
        csproj_added.append((rel, gen_name))

    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        if check_only and stale:
            print(f"[检查] 生成物过时（需要重新生成）：{', '.join(stale)} ⇒ rc=1")
            return 1
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if anchor not in csproj:
        print("[失败] csproj 里找不到 Sdk.targets 锚点")
        return 1

    lines = [MARKER_BEGIN, "  <ItemGroup>"]
    for rel, gen_name in csproj_added:
        lines.append(f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/'
                     f'PresentationCore/System/Windows/Input/{rel}" />')
        lines.append(f'    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/{gen_name}" />')
    lines.append("  </ItemGroup>")
    lines.append(MARKER_END)
    csproj = csproj.replace(anchor, "\n".join(lines) + "\n" + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 {len(csproj_added) * 2} 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1")
    return 0


def prove():
    """「只插入」机械证明（针对 TextCompositionManager.Linux.cs）：逆序回代后与上游逐字节相同。

    做法：从**落盘生成物**里按 PATCHES 的替换**逆序**回代（`new → old`，每处要求恰好命中 1 次），
    再与上游比 sha256。文件头与当前脚本不一致（=上一版脚本产的、还没重放）时退化为**内存**证明，
    并明说不给落盘件背书。
    """
    rel, gen_name, subs = None, None, None
    for r, g, ss in PATCHES:
        if g == "TextCompositionManager.Linux.cs":
            rel, gen_name, subs = r, g, ss
    if rel is None:
        print("[失败] PATCHES 里找不到 TextCompositionManager")
        return 1

    src = os.path.join(UP, rel)
    gen = os.path.join(PC_DIR, gen_name)
    with open(src, encoding="utf-8-sig") as f:
        text = f.read()

    applied = [(o, n) for (o, n, e) in subs if e != 0]
    head = HEADER.format(upstream=rel)

    def reverse(body):
        """逆序回代；返回 (还原后的文本 或 None, 失败说明)。"""
        cur = body
        for old, new in reversed(applied):
            c = cur.count(new)
            if c != 1:
                return None, f"逆向回代命中 {c} 次（要求 1）；片段={new[:60]!r}"
            cur = cur.replace(new, old, 1)
        return cur, None

    body = None
    where = None
    if os.path.exists(gen):
        with open(gen, encoding="utf-8") as f:
            raw = f.read()
        if raw.startswith(head):
            body = raw[len(head):]
            where = f"落盘生成物 src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/{gen_name}"

    if body is not None:
        restored, why = reverse(body)
        if restored == text:
            print(f"[① 只插入] 取自 {where}")
            print("[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256="
                  f"{hashlib.sha256(restored.encode('utf-8')).hexdigest()}")
            print(f"[① 只插入] 上游 sha256={hashlib.sha256(text.encode('utf-8')).hexdigest()}"
                  "（两条相同 ⇒ 只动了锚点本身、`?.` 与插入行）")
            return 0
        print(f"[注意] 落盘生成物**不是本轮脚本产的**（{why}）⇒ 本证明退化为**内存**证明，"
              "不对落盘件背书（重放后再跑一次才是对落盘件取的真值）。")

    out = text
    for old, new in applied:
        out = out.replace(old, new)
    restored, why = reverse(out)
    if restored is None or restored != text:
        print(f"[失败] **内存**证明也没过：{why if why else '逆代后与上游不一致'}")
        return 1
    print("[① 只插入] 取自 **内存**（生成物尚未产出/已过时）")
    print("[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256="
          f"{hashlib.sha256(restored.encode('utf-8')).hexdigest()}")
    print(f"[① 只插入] 上游 sha256={hashlib.sha256(text.encode('utf-8')).hexdigest()}"
          "（两条相同 ⇒ 只动了锚点本身、`?.` 与插入行）")
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
