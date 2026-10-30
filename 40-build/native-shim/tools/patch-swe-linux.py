#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 7 个工程的 `System.Windows.Extensions` **整条**换成 Linux 原生替身（`#36` 波）。

【为什么】官方包 `System.Windows.Extensions` 在**非 Windows** 上把
`System.Xaml.Permissions.XamlAccessLevel`（以及 `System.Media.SoundPlayer`/`SystemSounds`）
实现成**直接抛** `PlatformNotSupportedException` 的桩，只有 `runtimes/win/lib/` 才是真实现。
而 `XamlReader.LoadBaml` 只要程序集里生成了 `GeneratedInternalTypeHelper` 就**必然**调 `XamlAccessLevel.AssemblyAccessTo`
⇒ 任何正常构建的第三方 WPF 程序集一装 BAML 就崩（实测：HandyControl 示例工程）。

本仓已有 Linux 原生替身 `src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/`（AssemblyName 同名、
`AssemblyVersion=9.0.0.0` 与 deps.json 对齐、**不抛**实现，见该目录 `PORT-CHANGES.md`）。

⚠️ **为什么不能只加 `ExcludeAssets="runtime"` 而保留包引用**：实测那样 RAR 仍认包资产是权威，
**不会**把我们那份拷进输出（九件套四个 `bin/Debug/` 里 `System.Windows.Extensions.dll` 两样都没有）。
⇒ 本应用器把官方 `<PackageReference …/>` **整条换掉**，只留指向替身的 `<Reference>`。

# ⚠️【`#38` 修：HintPath 里**不许写死配置**】`#36` 那版写死 `bin/Release/`，而 `integration-wave.sh`
#   把替身建在 **Debug**（波自己的配置）⇒ 引用**落空** ⇒ RAR 回落到**官方包**（assets 里还在）⇒
#   编出来的 `System.Xaml.dll` 带着**包的签名身份**（`PublicKeyToken=cc7b13ffcd2ddd51`），
#   而 `PresentationFramework` 报 `CS0012`（"类型 XamlAccessLevel 在未引用的程序集中定义"）。
#   ⇒ 改成 `bin/$(Configuration)/`：**跟随消费方的配置**（Debug 消费 Debug、Release 消费 Release）。
#   ⚠️ 同时必须**清掉陈旧的 `obj/project.assets.json`** —— 否则包资产仍在图里、可能继续赢
#   （`#38` 实测：`System.Xaml`/`WindowsBase` 的 assets 里各有 14 处 SWE，`PC`/`PF` 是 0 ⇒ 同一批工程状态不一致）。

【用法】
    python3 src/WpfGfx.Linux.Native/tools/patch-swe-linux.py [--check] [--root <树根>]
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

TARGETS = [
    "src/Microsoft.DotNet.Wpf.Linux/src/System.Xaml/System.Xaml.Linux.csproj",
    "src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj",
    "src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj",
    "src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj",
    "src/Linux/samples/HelloWpf/HelloWpf.csproj",
    "src/Linux/samples/WpfFeatureProbe/WpfFeatureProbe.csproj",
    "src/Linux/samples/WpfTextDemo/WpfTextDemo.csproj",
]

# ① 原始形态：官方包引用（整条换掉）
PLAIN_RE = re.compile(
    r'^(?P<ind>[ \t]*)<PackageReference Include="System\.Windows\.Extensions" Version="9\.0\.0" />[ \t]*$',
    re.M)

# ② 本应用器的**中间形态**（`#36` 第一版：留包引用 + ExcludeAssets + Reference）——一并迁移走
OLD_RE = re.compile(
    r'[ \t]*<!-- ── `#36` 波：官方包的\*\*运行期\*\*换成 Linux 原生替身.*?</Reference>\n'
    r'|(?P<exc>[ \t]*<PackageReference Include="System\.Windows\.Extensions" Version="9\.0\.0" ExcludeAssets="runtime" />\n)'
    r'|(?P<exc2>[ \t]*<PackageReference Include="System\.Windows\.Extensions" Version="9\.0\.0" ExcludeAssets="compile;runtime" />\n)'
    r'|(?P<plainref>[ \t]*<Reference Include="System\.Windows\.Extensions">\n(?:[ \t]*<[^>]*>[^<]*</[^>]*>\n)*[ \t]*</Reference>\n)',
    re.S)

# 排除包资产的占位包引用（**只许有一处口径**：状态机与 NEW_BLOCK 都用它）
EXCL_LINE = '<PackageReference Include="System.Windows.Extensions" Version="9.0.0" ExcludeAssets="compile;runtime" />'

# 新形态的**唯一标记**（只在新块里出现）——`--check` 用它判"已就位"
MARKER = "（与 deps.json 对齐）"   # 只在新块里出现


NEW_BLOCK = '''{ind}<!-- ── `#38` 波：官方包换成 Linux 原生替身（**编译期也不许用包**）──────────────
{ind}    官方包在非 Windows 上把 `XamlAccessLevel`/`SoundPlayer` 等实现成**必抛**桩，只有 `runtimes/win` 是真的
{ind}    ⇒ 任何生成 `GeneratedInternalTypeHelper` 的程序集一装 BAML 就崩（实测：第三方应用 HandyControl）。
{ind}
{ind}    ⚠️ **为什么留着包引用、却把 compile/runtime 资产排除掉**（`#38` 实测的根因）：
{ind}      `System.Security.Permissions 9.0.0` **传递依赖** `System.Windows.Extensions 9.0.0`
{ind}      ⇒ 就算把直接包引用**整条删掉**，restore 仍会把包拉回来（`project.assets.json` 里 SWE 命中 14 处），
{ind}      RAR 于是**按包**解析（包是**签名的**，`PublicKeyToken=cc7b13ffcd2ddd51`），
{ind}      而 `System.Xaml` 的 `TypeForwardedTo(XamlAccessLevel)` 也就带着**包的身份**，
{ind}      最终 `PresentationFramework` 报 `CS0012`（"类型 XamlAccessLevel 在未引用的程序集中定义"）。
{ind}      ⇒ 必须让**包在编译期一个字都不出现**：`ExcludeAssets="compile;runtime"`（只留它作依赖图的占位）。
{ind}      ⚠️ `#36` 只试过 `ExcludeAssets="runtime"` —— **编译资产还在** ⇒ 包照样赢（那条否证不适用于本形态）。
{ind}
{ind}    实现由下面的 `<Reference>` 提供：同名程序集、`AssemblyVersion=9.0.0.0`（与 deps.json 对齐）、**不抛**。
{ind}    依赖图里 SWE 的**运行期**资产被排除 ⇒ 包那份 dll **不会**被拷进输出，替身会（`<Private>true</Private>`）。 -->
{ind}{excl}
{ind}<Reference Include="System.Windows.Extensions">
{ind}  <HintPath>{prefix}src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/bin/$(Configuration)/System.Windows.Extensions.dll</HintPath>
{ind}  <Private>true</Private>
{ind}</Reference>
'''

def _prefix(rel):
    """库工程（自产覆盖层落点 `src/Microsoft.DotNet.Wpf.Linux/src/<名>/`）import
       `src/Linux/build/Directory.Upstream.props` ⇒ 该 props 提供 `$(WpfLinuxRoot)`，用变量更稳；
       而**样本工程不 import 那份 props**（实测）⇒ 用 `$(MSBuildThisFileDirectory)../../`（样本在 `src/Linux/samples/<名>/`）。

    ⚠️【2026-10-05 修·结构上游化落点】旧判据是 `rel.startswith("src/Linux/build/")` —— 那是
       "阶段 3 试过一版 `src.Linux/` 落点"的中间形态，**从未是最终落点**。结构上游化后库工程在
       `src/Microsoft.DotNet.Wpf.Linux/src/<名>/` ⇒ 旧判据恒 False ⇒ 库工程被写成
       `$(MSBuildThisFileDirectory)../../src/Microsoft.DotNet.Wpf.Linux/src/…`（多出一段
       `src/Microsoft.DotNet.Wpf.Linux/`）⇒ 替身 `<Reference>` 落空 ⇒ `System.Xaml` 报
       `CS0234/CS0246 XamlAccessLevel`（包资产已被排除 ⇒ 类型整体消失）。
       ⇒ 把**最终落点**认出来（旧形态保留，兼容旧树）。"""
    rel = rel.replace(os.sep, "/")
    if rel.startswith("src/Microsoft.DotNet.Wpf.Linux/src/") or rel.startswith("src/Linux/build/"):
        return "$(WpfLinuxRoot)"
    return "$(MSBuildThisFileDirectory)../../"


def main():
    ap = argparse.ArgumentParser(description="System.Windows.Extensions 替身接线（7 个工程）")
    ap.add_argument("--check", action="store_true", help="只检查，不修改（0=全部已就位）")
    ap.add_argument("--root", default=DEFAULT_ROOT, help="树根（默认按本文件位置推）")
    args = ap.parse_args()
    root = os.path.abspath(args.root)

    missing, todo, done = [], [], []
    for rel in TARGETS:
        p = os.path.join(root, rel)
        if not os.path.exists(p):
            missing.append(rel)
            continue
        with open(p, encoding="utf-8-sig") as f:
            s = f.read()
        want = _prefix(rel) + "src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/"
        # ⚠️ 判据要**整条正确**、不能只看前缀：实测前缀曾被重复拼过
        #   （`…Linux/src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/bin/…`）而"前缀在"照样为真 ⇒ 会被判成"已就位"。
        want_line = want + "bin/$(Configuration)/System.Windows.Extensions.dll</HintPath>"   # `want` 已含 `src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/`
        # ⚠️【`#38`】"已就位"必须**两件都在**：① 替身 `<Reference>` 的 HintPath 正确；
        #   ② **排除包资产的占位包引用**（`ExcludeAssets="compile;runtime"`）——
        #   缺后者时，`System.Security.Permissions` 的传递依赖会把**包**拉回来，RAR 按包解析 ⇒ `CS0012`。
        #   只判 ① 会让"半接线"被误判成"已就位"（本波实测：应用器报"无需改动"却没补上 ②）。
        want_excl = EXCL_LINE.strip()
        if MARKER in s and want_line in s and want_excl in s:
            done.append(rel)
            continue
        if MARKER in s and not (want_line in s and want_excl in s):
            # 已接线但**形态不全/前缀不对**（早期版本对样本也用了 $(WpfLinuxRoot)）⇒ 就地补齐
            # 直接**整行重写** HintPath（前缀可能被改过多次 ⇒ 逐段拼容易重复；实测踩过）
            correct = want + "bin/$(Configuration)/System.Windows.Extensions.dll"   # 同上：不要重复拼前缀
            fixed = re.sub(r'(?m)^(\s*<HintPath>)[^<]*System\.Windows\.Extensions\.Linux[^<]*(</HintPath>)',
                           lambda m: m.group(1) + correct + m.group(2), s, count=1)
            if want_excl not in fixed:
                # 在替身 `<Reference Include="System.Windows.Extensions">` 之前插入占位包引用
                fixed2 = re.sub(r'(?m)^([ \t]*)(<Reference Include="System\.Windows\.Extensions">)',
                                lambda m: m.group(1) + EXCL_LINE.strip() + "\n" + m.group(1) + m.group(2),
                                fixed, count=1)
                if fixed2 == fixed:
                    print(f"[失败] {rel}：找不到可插入占位包引用的锚点（<Reference Include=\"System.Windows.Extensions\">）")
                    return 1
                fixed = fixed2
            if fixed != s:
                with open(p, "w", encoding="utf-8") as f:
                    f.write(fixed)
                print(f"[接线] {rel}：补齐形态（HintPath 配置 + 排除包资产的占位包引用）")
            done.append(rel)
            continue
        plain = list(PLAIN_RE.finditer(s))
        old = list(OLD_RE.finditer(s))
        if len(plain) + len(old) != 1:
            print(f"[失败] {rel}：原形态 {len(plain)} 处 / 中间形态 {len(old)} 处（合计应为 1）"
                  f" —— port-lib 重写过 csproj？")
            return 1
        todo.append((rel, p, s, plain[0] if plain else None, old[0] if old else None))

    if missing:
        for rel in missing:
            print(f"[失败] 找不到工程 {rel}")
        return 1

    print(f"[接线] 已就位 {len(done)}/{len(TARGETS)}"
          + ("：" + "、".join(os.path.basename(d) for d in done) if done else "（无）"))
    if args.check:
        if todo:
            print("[检查] **未接线**：" + "、".join(os.path.basename(t[0]) for t in todo)
                  + " ⇒ 需要跑一次不带 --check 的本脚本")
            return 1
        print("[检查] 全部已接线 ✅")
        return 0

    for rel, p, s, plain, old in todo:
        if plain is not None:
            ind = plain.group("ind")
            out = s[:plain.start()] + NEW_BLOCK.replace("{ind}", ind).replace("{prefix}", _prefix(rel)).replace("{excl}", EXCL_LINE) + s[plain.end():]
            how = "官方包引用 → 替身 <Reference>"
        else:
            ind = re.match(r"(?P<ind>[ \t]*)", old.group(0)).group("ind")
            out = s[:old.start()] + NEW_BLOCK.replace("{ind}", ind).replace("{prefix}", _prefix(rel)).replace("{excl}", EXCL_LINE) + s[old.end():]
            how = "中间形态（ExcludeAssets）→ 整条换掉"
        with open(p, "w", encoding="utf-8") as f:
            f.write(out)
        print(f"[接线] {rel}：{how}")
    if not todo:
        print("[接线] 无需改动（幂等）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
