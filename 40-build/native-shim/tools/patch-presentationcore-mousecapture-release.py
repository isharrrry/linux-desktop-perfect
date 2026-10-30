#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`D-G85` 修法应用器：**主动要求释放捕获时，当场清掉托管侧的捕获状态**。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mousecapture-release.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mousecapture-release.py            # 缺省 = M1（正修）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mousecapture-release.py --variant=m1,m2   # ＋M2 实验

======================================================================================
【缺陷（`D-G85`，"点了没反应"那一族）】
  点选 `ComboBox` 下拉项之后（`combo.closed` 已出现）`Mouse.Captured` **恒为 `ComboBox`**
  ⇒ 之后窗口内的点击**全被路由给 ComboBox**（`freshhit=Border` 却收到 `EVT combo.down`）
  ⇒ `ListBox` 项点不中、`TextBox` 拿不到焦点（用户看到的就是"点了没反应"）。

  车道 W87A 的**只读**诊断（`src/Linux/build/MilBridge/W87A-report.md` `f1fb2a8894486248`）取到了分界读数：
    · **受控 A/B**：同一条腿型、**同一个释放消息**（`hwnd=0x200005 / wp=0x0 / lp=0x0` 都派发过）——
      下拉"点外面"关（按下落**主窗口**）⇒ `captured=null`；下拉"点选项"关（按下落**弹窗窗口**）⇒ `captured=ComboBox`。
    · **唯一自变量 = 按下落在哪个 X 窗口** ⇒ 失败点在**托管侧**：按下落在弹窗 ⇒ 活跃输入源翻转到弹窗，
      而清捕获这件事上游**完全托付给 `WM_CAPTURECHANGED` 回声**，回声要过两道门：
        **门(i)** `HwndMouseInputProvider.cs:730`  `if(!IsOurWindow(lParam) && _active)`
        **门(ii)** `MouseDevice.cs:1444-1445`      `if((_inputSource is not null) && (report.InputSource == _inputSource))`
      两道门都由"活跃源是弹窗"关上 ⇒ 回声（从**主窗口源**发出）被整段丢掉。

【M1（缺省，本件的修法）—— 唯一落点】
  `PresentationCore/System/Windows/Input/MouseDevice.cs` 的 `Capture(...)` 里那个
  "**我们主动要求释放**"的分支（上游 `:386-394`），在 `mouseInputProvider.ReleaseMouseCapture();`
  之后补一句 `ChangeMouseCapture(null, null, CaptureMode.None, timeStamp);`。

  为什么是这一处：`:388` 是全进程**唯一**知道"我们刚刚主动要求了释放"的位置；门(i)/(ii) 都建立在
  "回声必须来自**活跃源**"这个前提上，而这个前提在**跨窗口**（弹窗）场景下**不成立**。

  语义保全（逐条核过）：
    · **幂等**：`ChangeMouseCapture` 首行就是 `if(mouseCapture != _mouseCapture)`（上游 `:1032`）
      ⇒ 回声若真来了，第二次调用是**空操作**；
    · **不新增/不删除任何事件**：它与回声路径**同一个函数** ⇒ 一样会 `_providerCapture = null`、
      摘/挂三个 DP 回调、发 `LostMouseCapture`/`GotMouseCapture`、`Synchronize()`；
    · **不碰 `timeStamp` 的来源**：用的是同一函数作用域里那个 `int timeStamp = Environment.TickCount;`。

  ⚠️ **同趟必查**（W87A §5.1 点名，本变体**不**改 `Popup`）：`Popup.cs:1209-1240 OnLostMouseCapture`
  在 `reestablishCapture = e.OriginalSource != root && Mouse.Captured == null && GetCapture() == IntPtr.Zero`
  为真时会 `EstablishPopupCapture()`，而它可能把捕获**重新**取到 `PopupRoot` 上（`:1168-1174`）；
  若修后 `captured=` 普查里出现 `PopupRoot` ⇒ 就是这一支（判据必须能分辨 ⇒ 见 W88A 报告 §3）。

【M2（`--variant=m2`，**可证伪的实验**，不是修法）】
  只放宽**门(i)**：把 `HwndMouseInputProvider.cs:730` 的 `&& _active` 去掉。
  W87A 的预测：**单做 M2 不变绿** —— 报出来的 `RawMouseInputReport.InputSource` 仍是**主窗口源**，
  会被**门(ii)** 整段丢掉。本变体存在的唯一目的 = 把这个预测变成**可证伪的读数**
  （W88A 报告 §1 记了它被证伪与否）。缺省变体**只含 M1** ⇒ 后续波自动兜底也不会带上 M2。

【家族纪律（本项目用血换来的）】
  · 生成物 = 上游**逐字复制** ＋ 1 处插入；锚点必须**恰好出现 1 次**，否则报错退出（不静默产出未打补丁的副本）；
  · **机械证据**：生成物的 `throw ` 条数与 `{`/`}` 条数与上游**逐字相同**（M1/M2 都不改控制流、不加花括号）；
  · 无参运行 = **真的应用**（写生成物 ＋ csproj 接线），幂等；`--check` **只读**；
  · **接线必须在每次波重放后仍生效**：波的第 1 步 `port-lib.py` 会重写 csproj，
    只有由本应用器重放才活得下来（本件名字落在 `patch-presentation*` 通配里 ⇒ 自动兜底也会执行它）；
  · 未被选中的变体 ⇒ **把它的生成物删掉、把它的接线块拆掉**（"只保留被声明的那个状态"，不留半件）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")

XAML_IMPORT_ANCHOR = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'

# =====================================================================================
#  变体 M1：MouseDevice.cs —— "我们主动要求释放"的分支
# =====================================================================================

UP_REL_M1 = ("src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/MouseDevice.cs")
UP_M1 = os.path.join(ROOT, UP_REL_M1)
GEN_M1 = os.path.join(PC_DIR, "MouseDevice.Linux.cs")

ANCHOR_M1 = "                        mouseInputProvider.ReleaseMouseCapture();\n"

REPLACEMENT_M1 = '''                        mouseInputProvider.ReleaseMouseCapture();

                        // ── WPF-on-Linux（D-G85 修法 · 车道 W88A）：**主动要求释放时，当场清托管捕获状态**。
                        //    ⚠️ 上游在这里把"清 `_mouseCapture`"完全托付给下面那三行注释描述的
                        //    `RawMouseAction.CancelCapture` **回声**。而回声要过两道门：
                        //      · `HwndMouseInputProvider.cs:730` 的 `!IsOurWindow(lParam) && _active`；
                        //      · `MouseDevice.cs:1444-1445` 的 `report.InputSource == _inputSource`。
                        //    **跨窗口**场景（点 ComboBox 下拉项：那一下按下落在**弹窗窗口**上）会让活跃
                        //    输入源翻转到弹窗 ⇒ 从**主窗口源**发出的回声被整段丢掉 ⇒ `Mouse.Captured`
                        //    恒为 ComboBox ⇒ 之后窗口内的点击全被误路由到它（用户："点了没反应"）。
                        //    ⇒ 这里补一次**当场**的状态变更（幂等：`ChangeMouseCapture` 首行即
                        //    `if(mouseCapture != _mouseCapture)`；回声若真来了，第二次是空操作）。
                        ChangeMouseCapture(null, null, CaptureMode.None, timeStamp);
'''

EDITS_M1 = [
    ("① `Capture(null)` 的释放分支：补一次 ChangeMouseCapture(null,…)", ANCHOR_M1, REPLACEMENT_M1),
]

REQUIRED_M1 = [
    ("M1 调用点存在", "ChangeMouseCapture(null, null, CaptureMode.None, timeStamp);"),
    ("M1 调用点在 ReleaseMouseCapture 之后", "ReleaseMouseCapture();\n\n                        // ── WPF-on-Linux（D-G85 修法"),
    ("M1 仍保留上游那段回声注释（语义未改写）",
     "// cause a RawMouseAction.CancelCapture to be processed, which will"),
    ("M1 仍置 success = true（返回值语义不变）", "                        success = true;"),
]

# =====================================================================================
#  变体 M2（实验）：HwndMouseInputProvider.cs —— 门(i) 的 `&& _active`
# =====================================================================================

UP_REL_M2 = ("src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/"
             "HwndMouseInputProvider.cs")
UP_M2 = os.path.join(ROOT, UP_REL_M2)
GEN_M2 = os.path.join(PC_DIR, "HwndMouseInputProvider.Linux.cs")

ANCHOR_M2 = "                        if(!IsOurWindow(lParam) && _active)\n"

REPLACEMENT_M2 = '''                        // ── WPF-on-Linux（D-G85 · **M2 可证伪实验**，车道 W88A）：上游原式为
                        //      `if(!IsOurWindow(lParam) && _active)`
                        //    —— 本变体**只**去掉 `&& _active`（放宽门(i)），别的一字不动。
                        //    目的：把"回声根本没被报告"（门 i）与"报告了但被整段丢掉"（门 ii）劈开。
                        if(!IsOurWindow(lParam))
'''

EDITS_M2 = [
    ("① 门(i)：去掉 `&& _active`", ANCHOR_M2, REPLACEMENT_M2),
]

REQUIRED_M2 = [
    ("M2 条件已放宽", "if(!IsOurWindow(lParam))\n"),
    ("M2 原式仍逐字留在注释里（可追溯）", "`if(!IsOurWindow(lParam) && _active)`"),
    ("M2 报告点仍在（未删 CancelCapture 上报）", "RawMouseActions.CancelCapture,"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-mousecapture-release.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}`
//        逐字复制 + 1 处 `D-G85` 注入（{what}）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 计数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打（`D-G85`，见 `src/Linux/build/MilBridge/W87A-report.md`）：
//   点选 ComboBox 下拉项之后 `Mouse.Captured` 恒为 ComboBox ⇒ 之后窗口内的点击被误路由。
//   分界读数指向**托管侧**：清捕获被上游托付给 `WM_CAPTURECHANGED` 回声，而回声要过
//   `HwndMouseInputProvider.cs:730`（`_active`）与 `MouseDevice.cs:1444-1445`（活跃源门前）两道门。
"""

# ⚠️ `TARGETS` **只声明缺省变体（M1）** —— 它是给 `src/Linux/build/MilBridge/tools/applier-audit.py` 读的
#    "被登记了就必须生效"声明表（A 级：每条 `repl` 在生成物里必须**恰好出现 1 次**）。
#    M2（`--variant=m2` 的可证伪实验）**故意不声明**：缺省状态下它**必须不存在**
#    （生成物被删、接线被拆），若把它写进来，审计会对一个**合法状态**报 miss（实测：
#    `tier=A ok=2 miss=3 detail=缺生成物 HwndMouseInputProvider.Linux.cs; …`）。
#    代价（明说）：M2 变体**不在审计覆盖面内**；它是**一趟性实验**，不是交付的修法。
TARGETS = [
    # (上游相对路径（审计用 hint，必须以 src/Microsoft.DotNet.Wpf/ 开头）, 生成物绝对路径, 编辑表)
    (UP_REL_M1, GEN_M1, EDITS_M1),
]

# 仅供审计/读者参考：M2 变体的编辑表（**不进 `TARGETS`**，理由见上）
TARGETS_EXPERIMENTAL = [
    (UP_REL_M2, GEN_M2, EDITS_M2),
]

VARIANT_ATTR = {
    "m1": dict(
        key="m1",
        title="M1（正修）：MouseDevice 主动释放时当场清托管捕获",
        up=UP_M1, up_rel=UP_REL_M1, gen=GEN_M1,
        anchor=ANCHOR_M1, repl=REPLACEMENT_M1,
        required=REQUIRED_M1,
        marker_begin=("  <!-- ==== WPF-on-Linux D-G85-M1：MouseDevice 主动释放捕获时同步清托管状态"
                      "（由 src/Linux/tools/patch-presentationcore-mousecapture-release.py 注入）==== -->"),
        marker_end="  <!-- ==== WPF-on-Linux D-G85-M1 结束 ==== -->",
        banner_what="在 `Capture(null)` 的释放分支补一次 `ChangeMouseCapture(null, null, CaptureMode.None, timeStamp)`",
    ),
    "m2": dict(
        key="m2",
        title="M2（实验，非修法）：放宽 HwndMouseInputProvider 的 `&& _active`",
        up=UP_M2, up_rel=UP_REL_M2, gen=GEN_M2,
        anchor=ANCHOR_M2, repl=REPLACEMENT_M2,
        required=REQUIRED_M2,
        marker_begin=("  <!-- ==== WPF-on-Linux D-G85-M2 实验：放宽门(i) 的 `&& _active`"
                      "（由 src/Linux/tools/patch-presentationcore-mousecapture-release.py 注入）==== -->"),
        marker_end="  <!-- ==== WPF-on-Linux D-G85-M2 结束 ==== -->",
        banner_what="去掉门(i) 的 `&& _active`（**M2 可证伪实验**，不是修法）",
    ),
}

ORDER = ["m1", "m2"]   # csproj 接线块的排列顺序（确定性）

# ⚠️ 审计（`applier-audit.py` 的"接线"那一格）读的是**模块级** `MARKER_BEGIN` 这个**单值**；
#    本应用器有 2 个可能的接线块（M1/M2）⇒ 这里暴露**缺省变体**（M1）的那一个（与 `TARGETS` 同口径）。
MARKER_BEGIN = VARIANT_ATTR["m1"]["marker_begin"]
MARKER_END = VARIANT_ATTR["m1"]["marker_end"]


def _count(haystack, needle):
    return haystack.count(needle)


def _remove_block(text, begin, end):
    """把 begin..end（含两端行）整块拆掉；返回 (新文本, 拆掉了几块)。"""
    n = 0
    while True:
        i = text.find(begin)
        if i < 0:
            return text, n
        j = text.find(end, i)
        if j < 0:
            # 只有头没有尾 ⇒ 畸形接线，不能猜：报错由调用方处理
            raise ValueError("接线块只有 MARKER_BEGIN 没有 MARKER_END：%s" % begin.strip())
        j = text.find("\n", j)
        j = len(text) if j < 0 else j + 1
        text = text[:i] + text[j:]
        n += 1


def apply_variant(spec, check_only):
    """生成/撤销一个变体；返回 (rc, 说明)。"""
    if not os.path.exists(spec["up"]):
        print("[失败] 找不到上游 %s" % spec["up"])
        return 1, None

    with open(spec["up"], encoding="utf-8-sig") as f:
        text = f.read()

    n = _count(text, spec["anchor"])
    print("[锚点] %s：上游出现 %d 次（要求 1）" % (spec["title"], n))
    if n != 1:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？本变体未应用（**不做任何静默降级**）。")
        return 1, None

    out = text.replace(spec["anchor"], spec["repl"], 1)

    for name, needle in spec["required"]:
        if needle not in out:
            print("[失败] 生成物缺少结构断言：%s" % name)
            return 1, None

    # ---- 机械证据：不改控制流（throw / 花括号逐字相同）----
    for tok, label in (("throw ", "throw"), ("{", "{"), ("}", "}")):
        a, b = _count(text, tok), _count(out, tok)
        if a != b:
            print("[失败] `%s` 条数变了：上游 %d → 生成物 %d" % (label, a, b))
            return 1, None
    print("[断言] `throw `/`{`/`}` 三种计数 上游 == 生成物（注入不改控制流）")
    print("[断言] 上游 %d 行 → 生成物 %d 行（+%d 行，全部是本注入的注释与那一句）"
          % (len(text.splitlines()), len(out.splitlines()),
             len(out.splitlines()) - len(text.splitlines())))

    banner = HEADER.format(rel=spec["up_rel"], what=spec["banner_what"])
    output = banner + out

    up_to_date = False
    if os.path.exists(spec["gen"]):
        with open(spec["gen"], encoding="utf-8") as f:
            up_to_date = (f.read() == output)

    if not check_only:
        if up_to_date:
            print("[生成] %s：内容已是最新（未重写）" % os.path.relpath(spec["gen"], ROOT))
        else:
            with open(spec["gen"], "w", encoding="utf-8") as f:
                f.write(output)
            print("[生成] %s：已从上游重生成" % os.path.relpath(spec["gen"], ROOT))
    else:
        print("[检查] %s：%s" % (os.path.relpath(spec["gen"], ROOT),
                                "内容已是最新" if up_to_date else "缺失/与上游不同步（需要重新生成）"))

    return 0, dict(up_to_date=up_to_date, gen=spec["gen"])


def revoke_variant(spec, check_only):
    """把未被选中的变体恢复成"上游原样"：删生成物 ＋ 拆接线块。"""
    rc = 0
    gen_exists = os.path.exists(spec["gen"])
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    wired = spec["marker_begin"] in csproj

    if not gen_exists and not wired:
        print("[撤销] %s：无生成物、无接线（已是上游原样）" % spec["title"])
        return 0, dict(changed=False)

    if check_only:
        print("[撤销·检查] %s：**未撤销**（生成物=%s 接线=%s）⇒ 与所选变体不一致"
              % (spec["title"], gen_exists, wired))
        return 1, dict(changed=True)

    if wired:
        csproj2, n = _remove_block(csproj, spec["marker_begin"], spec["marker_end"])
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj2)
        print("[撤销] %s：已从 csproj 拆掉 %d 个接线块" % (spec["title"], n))
    if gen_exists:
        os.remove(spec["gen"])
        print("[撤销] %s：已删除生成物 %s" % (spec["title"], os.path.relpath(spec["gen"], ROOT)))
    return rc, dict(changed=True)


def wire_csproj(check_only, specs):
    """保证被选中变体的接线块存在（幂等）。"""
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if _count(csproj, XAML_IMPORT_ANCHOR) != 1:
        print("[失败] csproj 里 Sdk.targets 锚点出现 %d 次（要求 1）"
              % _count(csproj, XAML_IMPORT_ANCHOR))
        return 1

    missing = [s for s in specs if s["marker_begin"] not in csproj]
    if not missing:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0

    if check_only:
        for s in missing:
            print("[接线] csproj **未接线** %s —— 需要运行一次不带 --check 的本脚本" % s["title"])
        return 1

    block = ""
    for s in missing:
        # ⚠️ ① 整块（含 <ItemGroup>）插在 Sdk.targets 锚点**行之前**：否则 <ItemGroup> 会嵌套到
        #      上一个块里 ⇒ MSB4232；
        #    ② Remove/Include 按文档顺序求值 ⇒ 上游那条 Include 必须先出现，Remove 落在它之后才生效
        #      （插在 Sdk.targets 之前 ⇒ 天然满足）。
        block += (s["marker_begin"] + "\n"
                  "  <ItemGroup>\n"
                  '    <Compile Remove="$(UpstreamWpfRoot)%s" />\n' % s["up_rel"]
                  + '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/%s" />\n'
                  % os.path.basename(s["gen"])
                  + "  </ItemGroup>\n"
                  + s["marker_end"] + "\n")
    csproj = csproj.replace(XAML_IMPORT_ANCHOR, block + XAML_IMPORT_ANCHOR, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 %d 个块到 %s" % (len(missing), os.path.relpath(CSPROJ, ROOT)))
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--apply", action="store_true", help="显式表示要写盘（默认行为，等价）")
    ap.add_argument("--variant", default="m1",
                    help="逗号分隔的变体集合（m1=正修，m2=可证伪实验，none=回到上游原样）；缺省 m1")
    args = ap.parse_args()

    want = [v.strip() for v in args.variant.split(",") if v.strip()]
    if want == ["none"]:
        want = []          # 显式"回到上游原样"（反极性/回退证用）
    bad = [v for v in want if v not in VARIANT_ATTR]
    if bad:
        print("[失败] 未知变体：%s（合法：%s）" % (",".join(bad), ",".join(ORDER)))
        return 2
    want = [v for v in ORDER if v in want]          # 规范化顺序 + 去重
    print("[变体] 选中 = %s（未选中的会被**撤销**成上游原样）" % (",".join(want) or "none（上游原样）"))

    rc = 0

    # ① 未选中的变体 ⇒ 撤销（幂等：已经是原样就什么都不做）
    for k in ORDER:
        if k in want:
            continue
        r, _ = revoke_variant(VARIANT_ATTR[k], args.check)
        rc = rc or r

    # ② 选中的变体 ⇒ 生成（或 --check 核对）
    for k in want:
        r, _ = apply_variant(VARIANT_ATTR[k], args.check)
        rc = rc or r

    # ③ csproj 接线（选中的变体必须都在）
    if rc == 0:
        rc = wire_csproj(args.check, [VARIANT_ATTR[k] for k in want])

    if rc == 0:
        print("\n下一步（**不要在这里重建权威 PC 之外的东西**）：")
        print("  dotnet msbuild src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1 --nologo \\")
        print("      -getItem:Compile -p:Configuration=Release | grep -i -e MouseDevice -e MouseInputProvider")
        print("  # 期望：被选中的变体只剩生成物那条（上游那条被 Remove 掉）")
    return rc


if __name__ == "__main__":
    sys.exit(main())
