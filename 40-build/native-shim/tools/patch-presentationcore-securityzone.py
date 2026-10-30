#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 PresentationCore 的 `SecurityHelper.MapUrlToZoneWrapper` 换成 Linux 版（短路 URL 安全区域）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py            # 应用
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py --check    # 只校验（不写）

============================================================================
为什么必须改（实测证据，不是推断）
============================================================================
`BitmapImage(fileUri)` 在 Linux 上**走不到 WIC**：四个 CHECK 的完整异常栈首帧逐字相同
（harness: src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WicClosedLoop，`DISPLAY=:99`）：

    at MS.Win32.UnsafeNativeMethods.CoInternetCreateSecurityManager(
           Object pIServiceProvider, Object& ppISecurityManager, Int32 dwReserved)
    "Cannot marshal 'parameter #1': Invalid managed/unmanaged type combination
     (Marshaling to and from COM interface pointers isn't supported)"

声明处 `Shared/MS/Win32/UnsafeNativeMethodsOther.cs:57-61` 用了
`[MarshalAs(UnmanagedType.Interface)]` ⇒ **显式要求 marshal 成 COM 接口指针**，
而 .NET 在 Linux 上直接拒绝。唯一调用点就是本脚本要改的
`Shared/MS/Internal/SecurityHelper.cs:75 MapUrlToZoneWrapper`。

（旁证：主控对 110 条 WIC 声明做了参数类型全量普查，**没有一条**是 COM 接口指针 ⇒
这个阻塞与 WIC 无关。`IS_WIC_FRAME=False`、`VIA_DISPATCHER_OR_WINDOW=False` 两个判定字段也印证。）

============================================================================
为什么这么改是**诚实**的（不是"糊过去"）
============================================================================
语义上：`MapUrlToZoneWrapper` 返回的是 **IE/urlmon 的 URL 安全区域**（Internet/Intranet/
LocalMachine…）。Linux 上**没有 urlmon、没有 IE zone、没有 Mark-of-the-Web** ⇒ 这个概念
不存在。上游自己的兜底值就是 `URLZONE_LOCAL_MACHINE`，且注释写着
`// fail securely this is the most priveleged zone` —— 也就是**最宽松**的那一档。
所以 Linux 上直接返回它 = "本机内容、不施加区域限制"，是**如实**表达"这个限制在本平台不存在"，
而**不是**伪造一个 COM 对象或假装调用成功。

本工程既有同一套逻辑的先例：非 Windows 上不设 `XamlAccessLevel`（理由：CAS 已不存在）；
`src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py` 的补丁 G 也是"从这里摘掉上游文件、换成 Linux 版"。
**注意**：`UnsafeNativeMethodsOther.cs` 那条 urlmon 声明**保留不动** —— 改完之后它不可达，留着无害；
删它反而会牵动别的程序集（该文件也是共享源）。

============================================================================
生成约定（与既有应用器一致）
============================================================================
  · 从上游**逐字读入** → **只替换那一个方法** → 写 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SecurityHelper.Linux.cs`
  · csproj：`<Compile Remove>` 上游路径 + `<Compile Include>` 生成物（带 marker，幂等）
  · 锚点/计数不符 → **报错退出**（绝不"改到一半还继续"）
  · `--check`：重新生成到内存并与磁盘上的生成物逐字节比较 + 检查 csproj 两条目；不一致退出码 2

⚠ 生成物由本脚本负责刷新，**不要手改**。
⚠ 顺序：`port-lib.py` 会整份重写 csproj ⇒ 本脚本必须**在它之后**运行
  （`reapply-patches.py` 之后即可；两者改的是不同区块）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # …/src/WpfGfx.Linux.Native/tools -> 仓库根

UPSTREAM = os.path.join(
    REPO, "src", "Microsoft.DotNet.Wpf", "src",
    "Shared", "MS", "Internal", "SecurityHelper.cs")

GEN_DIR = os.path.join(REPO, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
GEN_FILE = os.path.join(GEN_DIR, "SecurityHelper.Linux.cs")
CSPROJ = os.path.join(GEN_DIR, "PresentationCore.Linux.csproj")

# csproj 里的上游引用（PC 实际引用的那条路径，逐字；Remove 必须精确匹配它）
CSPROJ_UPSTREAM_PATH = "$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SecurityHelper.cs"
CSPROJ_GEN_PATH = "$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SecurityHelper.Linux.cs"

MARKER_BEGIN = "  <!-- ==== T2 · 补丁 H：URL 安全区域短路（SecurityHelper） BEGIN ==== -->"
MARKER_END = "  <!-- ==== T2 · 补丁 H：URL 安全区域短路（SecurityHelper） END ==== -->"

ANCHOR_SIG = "internal static int MapUrlToZoneWrapper(Uri uri)"

LINUX_BODY = '''         // ── WPF-on-Linux（T2 · 补丁 H）：Linux 上**没有** URL 安全区域这个概念 ────────────
         //   上游实现会调 urlmon 的 CoInternetCreateSecurityManager
         //   （声明见 Shared/MS/Win32/UnsafeNativeMethodsOther.cs:57-61，带
         //    [MarshalAs(UnmanagedType.Interface)]）—— .NET 在 Linux 上**拒绝 marshal COM 接口指针**，
         //   于是 BitmapImage(fileUri) 在**走到 WIC 之前**就抛
         //   `MarshalDirectiveException: Cannot marshal 'parameter #1'`（实测栈见文件头）。
         //
         //   语义：本方法返回的是 IE/urlmon 的 URL 区域（Internet/Intranet/LocalMachine…）。
         //   Linux 上没有 urlmon、没有 IE zone、没有 Mark-of-the-Web ⇒ 该概念不存在；
         //   上游自己的兜底值就是 URLZONE_LOCAL_MACHINE（注释原文：
         //   "fail securely this is the most priveleged zone"）⇒ Linux 上直接返回它，
         //   即"本机内容、不施加区域限制"—— 这是**如实**表达"限制在本平台不存在"。
         //
         //   这与本工程既有先例同一套逻辑：非 Windows 上不设 XamlAccessLevel（CAS 已不存在）。
         //   ⚠ 不伪造 COM 对象、不假装调用成功；恢复条件见 docs/unimplemented.md（需真正实现 zone 判定）。
         internal static int MapUrlToZoneWrapper(Uri uri)
         {
              return NativeMethods.URLZONE_LOCAL_MACHINE ;
         }
'''


def die(msg):
    print("[失败] " + msg)
    sys.exit(1)


def read_upstream():
    if not os.path.exists(UPSTREAM):
        die("找不到上游文件：" + UPSTREAM)
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        return f.read()


def brace_match(text, open_pos):
    """从 '{' 位置出发做花括号配对，返回配对的 '}' 的下标（跳过字符串/字符/注释不做处理，
    但本文件这段方法体里没有字符串里的花括号，故足够；锚点计数会兜底）。"""
    depth = 0
    for i in range(open_pos, len(text)):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    die("花括号不配对（上游文件结构变了？）")


def generate():
    text = read_upstream()

    # ── 锚点唯一性：签名只出现一次 ──
    n = text.count(ANCHOR_SIG)
    if n != 1:
        die(f"锚点 {ANCHOR_SIG!r} 出现 {n} 次（期望 1 次）——上游结构变了，拒绝继续")

    sig_pos = text.index(ANCHOR_SIG)

    # 找到签名所在行的行首，以及方法体的 '{'（签名与 '{' 之间可能跨行）
    line_start = text.rfind("\n", 0, sig_pos) + 1
    open_pos = text.index("{", sig_pos)
    close_pos = brace_match(text, open_pos)

    # 生成物 = 之前 + Linux 版方法 + 之后
    out = text[:line_start] + LINUX_BODY + text[close_pos + 1:]

    # ── 计数校验：旧实现的标志物必须**全部消失**，新实现的标志必须出现 ──
    #   注意：只在"未触碰的两段"里查（LINUX_BODY 的解释性注释里**故意**提到了
    #   CoInternetCreateSecurityManager 这个名字 —— 在整份生成物里查会误报自己）。
    untouched = text[:line_start] + text[close_pos + 1:]
    for marker in ("CoInternetCreateSecurityManager", "IInternetSecurityManager", "MUTZ_NOSAVEDFILECHECK"):
        if marker in untouched:
            die(f"替换范围之外仍残留上游标志物 {marker!r} —— 替换范围不对")
    if "URLZONE_LOCAL_MACHINE" not in out:
        die("替换后没有找到 URLZONE_LOCAL_MACHINE —— 替换失败")
    if "URLZONE_LOCAL_MACHINE" not in out:
        die("替换后没有找到 URLZONE_LOCAL_MACHINE —— 替换失败")

    # ── 其它方法必须一字未动：逐段核对被替换区间之外的内容 ──
    if text[:line_start] != out[:line_start]:
        die("方法之前的内容被改动了")
    if text[close_pos + 1:] != out[len(out) - (len(text) - close_pos - 1):]:
        die("方法之后的内容被改动了")

    header = (
        "// ⚠ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py 生成，**不要手改**。\n"
        "//\n"
        "// 它与上游 Shared/MS/Internal/SecurityHelper.cs **逐字相同**，只把\n"
        "// `MapUrlToZoneWrapper(Uri)` 换成了 Linux 版（直接返回 URLZONE_LOCAL_MACHINE）。\n"
        "// 原因：上游实现要 marshal urlmon 的 COM 接口指针，.NET 在 Linux 上拒绝 marshal，\n"
        "// 导致 BitmapImage(fileUri) 在走到 WIC 之前就抛 MarshalDirectiveException。\n"
        "// 详细推导、实测栈与恢复条件：见生成脚本的文件头。\n"
    )
    return header + out


def patch_csproj(check_only):
    if not os.path.exists(CSPROJ):
        die("找不到 csproj：" + CSPROJ)
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()

    # 幂等：先摘掉上一次注入的整块
    if MARKER_BEGIN in text:
        i = text.index(MARKER_BEGIN)
        j = text.index(MARKER_END) + len(MARKER_END)
        # 连同后面的换行一起摘掉
        while j < len(text) and text[j] in "\r\n":
            j += 1
        text = text[:i] + text[j:]

    anchor = f'    <Compile Include="{CSPROJ_UPSTREAM_PATH}" />'
    if anchor not in text:
        die("csproj 里找不到锚点（上游 SecurityHelper 的 Compile 项）：\n  " + anchor)

    block = (
        MARKER_BEGIN + "\n"
        "  <!-- T2 · 补丁 H：Linux 上不存在 URL 安全区域 ⇒ 短路 MapUrlToZoneWrapper。\n"
        "       上游实现要 marshal urlmon 的 COM 接口指针（[MarshalAs(UnmanagedType.Interface)]），\n"
        "       .NET 在 Linux 上拒绝 ⇒ BitmapImage(fileUri) 在走到 WIC 之前就抛\n"
        "       MarshalDirectiveException。生成物 = 上游逐字 + 只换那一个方法。 -->\n"
        "  <ItemGroup>\n"
        f'    <Compile Remove="{CSPROJ_UPSTREAM_PATH}" />\n'
        f'    <Compile Include="{CSPROJ_GEN_PATH}" />\n'
        "  </ItemGroup>\n"
        + MARKER_END + "\n"
    )

    # ⚠ 位置有两条硬约束（两条都是实测踩出来的）：
    #   ① 不能插在锚点行**前面** —— 锚点行在 <ItemGroup> 内部，把整块 <ItemGroup> 塞进去
    #      就是嵌套 ItemGroup，MSBuild 报 MSB4232。
    #   ② 必须插在锚点所在的 </ItemGroup> **之后** —— MSBuild 的 `Remove` 与 `Include`
    #      按**文档顺序**求值：Remove 若出现在上游 Include 之前，后者会把文件再加回来
    #      （实测：`-getItem:Compile` 里上游条目仍在）。WindowsBase 的补丁 G 正是这个形制：
    #      Include 在文件前部、Remove+Include(生成物) 在文件后部的补丁区。
    anchor_pos = text.index(anchor)
    close_pos = text.find("</ItemGroup>", anchor_pos)
    if close_pos < 0:
        die("找不到包住锚点的 </ItemGroup> —— csproj 结构变了")
    insert_at = close_pos + len("</ItemGroup>")
    while insert_at < len(text) and text[insert_at] in " \t":
        insert_at += 1
    if insert_at < len(text) and text[insert_at] == "\n":
        insert_at += 1
    text = text[:insert_at] + block + text[insert_at:]

    if check_only:
        return text

    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)
    return text


def main():
    ap = argparse.ArgumentParser(description="PresentationCore: 短路 URL 安全区域（SecurityHelper）")
    ap.add_argument("--check", action="store_true", help="只校验生成物与 csproj，不写盘")
    args = ap.parse_args()

    print(f"[输入] 上游 {os.path.relpath(UPSTREAM, REPO)}")
    generated = generate()

    if args.check:
        ok = True
        if not os.path.exists(GEN_FILE):
            print(f"[失败] 生成物不存在：{os.path.relpath(GEN_FILE, REPO)}")
            ok = False
        else:
            with open(GEN_FILE, encoding="utf-8") as f:
                on_disk = f.read()
            if on_disk != generated:
                print("[失败] 生成物与逐字重生成的结果不一致（有人手改过？上游变了没重跑？）")
                ok = False
            else:
                print(f"[OK] 生成物与重新生成的结果**逐字节一致**：{os.path.relpath(GEN_FILE, REPO)}")

        text = patch_csproj(check_only=True)
        with open(CSPROJ, encoding="utf-8") as f:
            current = f.read()
        if text != current:
            print("[失败] csproj 与预期不一致（补丁 H 区块缺失或锚点被改）")
            ok = False
        else:
            print("[OK] csproj 已含补丁 H 的两条目（Remove 上游 + Include 生成物）")

        if not ok:
            sys.exit(2)
        print("[OK] --check 通过")
        return 0

    os.makedirs(GEN_DIR, exist_ok=True)
    with open(GEN_FILE, "w", encoding="utf-8") as f:
        f.write(generated)
    print(f"[写出] {os.path.relpath(GEN_FILE, REPO)}（{len(generated)} 字符）")

    patch_csproj(check_only=False)
    print(f"[写出] {os.path.relpath(CSPROJ, REPO)}（补丁 H 区块已注入，幂等）")

    print()
    print("[下一步] 重建 PresentationCore（由主控在合并波里做；本脚本不触发构建）：")
    print("  dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1")
    return 0


if __name__ == "__main__":
    sys.exit(main())
