#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""D2 应用器：短路 `UiaGetReservedNotSupportedValue` / `UiaGetReservedMixedAttributeValue`
（**无参运行 = 应用**，与家族约定一致）。

    python3 src/WpfGfx.Linux.Native/tools/patch-uiautomationtypes-reservedvalue.py           # = 应用（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-uiautomationtypes-reservedvalue.py --check   # 只读（未就位 ⇒ rc=1）

【它挡的是什么（T3 实测）】
  过了 D1（resolver）之后，`UiaCoreTypesApi.UiaGetReservedMixedAttributeValue()` 抛
  **`MarshalDirectiveException`**：那两个 raw P/Invoke 的 out 参数标着
  `[MarshalAs(UnmanagedType.IUnknown)]` —— **COM 接口指针在 Linux 上不可封送**。
  T3 只能 catch 住并大声登记（打印 `WPTD_KNOWN_DEFECT=uia-com-marshal`）。

【修法：短路那两个方法，返回**进程内哨兵**（照补丁 H 的手法）】
  上游 Windows 上这俩返回的是 **UIA 核心的保留 COM 对象**，语义是"这个属性不支持 / 这个属性是混合值"：
  客户端拿它做**引用相等**判断（`attr == AutomationElement.NotSupported`）。Linux 上没有 UIA 核心，
  所以：返回**本进程内唯一**的哨兵对象 —— 引用相等语义在这一侧仍然自洽
  （同一个 API 每次返回同一个对象）。
  **⚠️ 这是降级，不是对齐**（照实写进代码与报告）：
    · 跨进程/跨 COM 的身份语义**不存在**（没有 UIA 核心，也没有 COM 编组）；
    · 若某天真有 UIA 客户端从外部连进来，这两个值必须重新实现，而不是沿用哨兵。

【为什么只短路这两个方法、不删 raw P/Invoke】
  删掉声明会让"上游改了这段"变得不可见；这里**保留声明并逐字注释说明它为什么在 Linux 上不可用**
  （`#if false` 包裹 + 原注释），改动面最小、可逆、可核对。

【csproj 接线】`src/Microsoft.DotNet.Wpf.Linux/src/UIAutomationTypes/UIAutomationTypes.Linux.csproj:43` 是上游那句；
  本脚本注入自己的块（Remove 上游 + Include 生成物），**波的第 1 步 port-lib 会重生成 csproj ⇒ 每次波都要重放本脚本**。

【重放顺序】port-lib.py → wire-uiautomation-resolver.py(D1) → **本脚本(D2)**
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

UA_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "UIAutomationTypes")
TARGET = os.path.join(UA_DIR, "UiaCoreTypesApi.Linux.cs")
CSPROJ = os.path.join(UA_DIR, "UIAutomationTypes.Linux.csproj")

UPSTREAM_REL = ("UIAutomation/UIAutomationTypes/MS/Internal/Automation/UiaCoreTypesApi.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src", UPSTREAM_REL)

CSPROJ_MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux D2：UIA 保留值短路"
                "（由 src/Linux/tools/patch-uiautomationtypes-reservedvalue.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux D2 结束 ==== -->"

ANCHOR_NOTSUPPORTED = '''        internal static object UiaGetReservedNotSupportedValue()
        {
            object notSupportedValue;
            CheckError(RawUiaGetReservedNotSupportedValue(out notSupportedValue));
            return notSupportedValue;
        }'''

ANCHOR_MIXED = '''        internal static object UiaGetReservedMixedAttributeValue()
        {
            object mixedAttributeValue;
            CheckError(RawUiaGetReservedMixedAttributeValue(out mixedAttributeValue));
            return mixedAttributeValue;
        }'''

REPL_NOTSUPPORTED = '''        internal static object UiaGetReservedNotSupportedValue()
        {
            // [D2] Linux：**短路**（上游是 UIA 核心的保留 COM 对象，这里没有 UIA 核心，
            //      而 raw P/Invoke 的 `IUnknown` out 参数在 Linux 上不可封送 ⇒ MarshalDirectiveException）。
            //      返回**本进程内唯一**的哨兵：同一个 API 每次给同一个对象 ⇒ 引用相等语义在这一侧自洽。
            //      ⚠️ **降级不是对齐**：跨进程/跨 COM 的身份语义不存在。
            return ReservedNotSupportedValue;
        }'''

REPL_MIXED = '''        internal static object UiaGetReservedMixedAttributeValue()
        {
            // [D2] 同上：短路成进程内哨兵（详见 UiaGetReservedNotSupportedValue 的注释）。
            return ReservedMixedAttributeValue;
        }'''

SENTINELS = '''        // [D2] 两个保留值的进程内哨兵（见上面两个方法的注释：**降级不是对齐**）。
        private static readonly object ReservedNotSupportedValue = new object();
        private static readonly object ReservedMixedAttributeValue = new object();

'''

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-uiautomationtypes-reservedvalue.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 + **D2** 的两处短路：
//   · `UiaGetReservedNotSupportedValue()` → 返回进程内哨兵 `ReservedNotSupportedValue`
//   · `UiaGetReservedMixedAttributeValue()` → 返回进程内哨兵 `ReservedMixedAttributeValue`
// 为什么必须短路：那两个 raw P/Invoke 的 out 参数是 `[MarshalAs(UnmanagedType.IUnknown)]`
//   —— **COM 接口指针在 Linux 上不可封送**，实测抛 `MarshalDirectiveException`
//   （T3 的 WPF 级用例：`UiaCoreTypesApi.UiaGetReservedMixedAttributeValue`）。
// 语义：Windows 上它们是 UIA 核心的保留 COM 对象，客户端按**引用相等**判断"不支持 / 混合值"；
//   这里返回**本进程内唯一**的哨兵 ⇒ 同一侧引用相等仍然自洽。
//   ⚠️ **这是降级、不是对齐**：跨进程/跨 COM 的身份语义不存在。
// 每次运行该脚本都会从上游重读重生成；锚点计数对不上时**报错退出**。
//
// ↓↓↓ 以下为上游原文（仅 D2 的两处有改动）↓↓↓
"""


def build_patched(text):
    if text.count(ANCHOR_NOTSUPPORTED) != 1:
        raise SystemExit(f"[D2] 锚点①（UiaGetReservedNotSupportedValue）出现 {text.count(ANCHOR_NOTSUPPORTED)} 次，期望 1 次 —— 上游变了？")
    if text.count(ANCHOR_MIXED) != 1:
        raise SystemExit(f"[D2] 锚点②（UiaGetReservedMixedAttributeValue）出现 {text.count(ANCHOR_MIXED)} 次，期望 1 次 —— 上游变了？")

    out = text.replace(ANCHOR_NOTSUPPORTED, REPL_NOTSUPPORTED).replace(ANCHOR_MIXED, REPL_MIXED)

    # 哨兵字段插在 `#region Internal Methods` 之前（类体里、字段区）
    anchor_region = "        #region Internal Methods"
    if out.count(anchor_region) != 1:
        raise SystemExit(f"[D2] 找不到唯一锚点 `{anchor_region}`")
    out = out.replace(anchor_region, SENTINELS + anchor_region, 1)

    # 两个 raw P/Invoke 逐字注释掉（保留原文，便于核对）
    for raw in (
        '        [DllImport(DllImport.UIAutomationCore, EntryPoint = "UiaGetReservedNotSupportedValue", CharSet = CharSet.Unicode)]',
        '        [DllImport(DllImport.UIAutomationCore, EntryPoint = "UiaGetReservedMixedAttributeValue", CharSet = CharSet.Unicode)]',
    ):
        if out.count(raw) != 1:
            raise SystemExit(f"[D2] 找不到 raw P/Invoke 行：{raw}")
        out = out.replace(raw, "        // [D2] Linux 不可用（IUnknown out-param 不可封送）—— 保留原文以便核对：\n        // " + raw.strip(), 1)

    for extern in (
        "        private static extern int RawUiaGetReservedNotSupportedValue([MarshalAs(UnmanagedType.IUnknown)] out object notSupportedValue);",
        "        private static extern int RawUiaGetReservedMixedAttributeValue([MarshalAs(UnmanagedType.IUnknown)] out object mixedAttributeValue);",
    ):
        if out.count(extern) != 1:
            raise SystemExit(f"[D2] 找不到 raw extern 行：{extern}")
        out = out.replace(extern, "        // " + extern.strip(), 1)

    # 自检：短路之后，这两个名字不应再出现在**可执行位置**（只允许出现在注释里）
    for line in out.splitlines():
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        if "RawUiaGetReservedNotSupportedValue" in stripped or "RawUiaGetReservedMixedAttributeValue" in stripped:
            raise SystemExit(f"[D2] 自检失败：仍有一处活引用 `{stripped}`")
    return out


def wire_csproj(csproj_path, check_only):
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

    upstream_line = ('    <Compile Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/'
                     + UPSTREAM_REL + '" />')
    if upstream_line not in csproj:
        print(f"[失败] csproj 里找不到上游 Include 行：\n       {upstream_line}")
        return 1
    if CSPROJ_MARKER not in csproj:
        print(f"[失败] csproj 里找不到 Sdk.targets 锚点：{CSPROJ_MARKER}")
        return 1

    block = "\n".join([
        MARKER_BEGIN,
        "  <ItemGroup>",
        f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/{UPSTREAM_REL}" />',
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/UIAutomationTypes/UiaCoreTypesApi.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
        "",
    ])
    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(csproj.replace(CSPROJ_MARKER, block + CSPROJ_MARKER, 1))
    print(f"[接线] 已注入 2 行到 {os.path.relpath(csproj_path, ROOT)}")
    return 0


def generate(check_only, csproj_path=None, out_path=None):
    if not os.path.isfile(UPSTREAM):
        print(f"[失败] 找不到上游文件：{UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        original = f.read()

    patched = build_patched(original)
    content = HEADER.format(upstream=UPSTREAM_REL) + patched

    print(f"[D2] 上游：{os.path.relpath(UPSTREAM, ROOT)}")
    print("[D2] 改动：两个 Reserved* API 短路成进程内哨兵；两处 raw P/Invoke 逐字注释（**降级不是对齐**）")

    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[D2] 已另写一份：{out_path}")

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
    print("\n下一步：python3 src/Linux/build/port-lib.py UIAutomationTypes && dotnet build src/Microsoft.DotNet.Wpf.Linux/src/UIAutomationTypes/UIAutomationTypes.Linux.csproj -m:1")
    return 0 if (rc == 0 and wire_rc == 0) else 1


def main():
    ap = argparse.ArgumentParser(description="D2 应用器（**无参运行 = 生成 + csproj 接线**）")
    ap.add_argument("--check", action="store_true", help="只检查，不写盘（未就位 ⇒ 退出码 1）")
    ap.add_argument("--out", metavar="PATH", help="额外写一份生成物（核对用）")
    ap.add_argument("--csproj", metavar="PATH", help="覆盖 csproj 路径（自测用）")
    args = ap.parse_args()
    return generate(args.check, args.csproj, args.out)


if __name__ == "__main__":
    sys.exit(main())
