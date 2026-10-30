#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7b · 把 Win32 shim 的 DllImportResolver 编进 WindowsBase / PresentationCore（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/wire-managed-layer.py          # 注入
    python3 src/WpfGfx.Linux.Native/tools/wire-managed-layer.py --check  # 只检查，不改

【为什么需要这个脚本，而不是直接改 csproj】
    `src/Linux/build/*.Linux/*.csproj` 是 **port-lib.py 的生成物**（每次运行整份重写），
    所以任何额外内容都必须有「可重放的出处」。本工程的既有出处有两处：
      · `src/Microsoft.DotNet.Wpf.Linux/src/shims/<Name>.shims.txt`（每行一个 shim 源文件）—— port-lib 会读它；
      · `src/Linux/build/<Name>.Linux/reapply-patches.py` —— 生成后再补的块。
    M7b 已经把 `src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs` 追加进了**两个** shims.txt，
    所以**下一次 port-lib 重生成会自动带上这一行**。

【那为什么还要这个脚本】
    `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/` 当前有另一个 agent 在做资源审计，
    直接 `port-lib.py PresentationCore` 会整份重写它正在动的目录（有竞态风险）。
    本脚本只做**一件事**：把缺失的那一行 `<Compile Include>` 插进去；
    已经存在（例如 port-lib 刚重生成过）就原样不动。
    改动量 = 1 行/工程，天然可逆，不会覆盖任何其它内容。

【幂等性保证】
    判据是「文件里是否已有指向 Win32ShimResolver.cs 的 Compile 项」，
    与插入位置无关。重复运行 0 改动。
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

SHIM_REL = "src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs"
SHIM_LINE = '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs" />'

TARGETS = [
    ("WindowsBase", "src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj"),
    ("PresentationCore", "src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj"),
]

# 插在「shim ItemGroup」的末尾：找到 __shim_anchor__ 标记的 ItemGroup 的开标签，
# 或者任意一个已经包含 src/Microsoft.DotNet.Wpf.Linux/src/shims/ 的 ItemGroup。都不在时退回 Sdk.targets 之前。
ITEMGROUP_RE = re.compile(r"^  <ItemGroup>\n(?:.*\n)*?  </ItemGroup>$", re.M)


def find_shim_itemgroup_inner_end(text):
    """返回 shim ItemGroup **闭合标签之前**的插入位置；找不到返回 None。

    注意：必须是 `</ItemGroup>` 的**起点**，不是块的终点——插到终点就落到
    ItemGroup 之外，MSBuild 会报 MSB4067「无法识别元素 <Project> 下面的元素 <Compile>」
    （本轮真实踩过）。"""
    for m in ITEMGROUP_RE.finditer(text):
        block = m.group(0)
        if "src/Microsoft.DotNet.Wpf.Linux/src/shims/" in block:
            close = block.rindex("  </ItemGroup>")
            return m.start() + close
    return None


def patch(path, check_only):
    full = os.path.join(ROOT, path)
    if not os.path.exists(full):
        print(f"[跳过] 不存在：{path}")
        return 0
    with open(full, encoding="utf-8-sig") as f:
        text = f.read()

    if "Win32ShimResolver.cs" in text:
        print(f"[已就位] {path}")
        return 0

    if check_only:
        print(f"[缺失] {path} 里没有 {SHIM_REL} 的 Compile 项")
        return 1

    pos = find_shim_itemgroup_inner_end(text)
    if pos is not None:
        new_text = text[:pos] + SHIM_LINE + "\n" + text[pos:]
        how = "追加到既有 shim ItemGroup"
    else:
        marker = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
        if marker not in text:
            print(f"[失败] {path}：既没有 shim ItemGroup，也找不到 Sdk.targets 锚点")
            return 1
        block = ('  <!-- M7b：Win32 子集 shim 的 DllImportResolver（见 src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs） -->\n'
                 '  <ItemGroup>\n' + SHIM_LINE + '\n  </ItemGroup>\n')
        new_text = text.replace(marker, block + marker, 1)
        how = "新建 ItemGroup（Sdk.targets 之前）"

    with open(full, "w", encoding="utf-8") as f:
        f.write(new_text)
    print(f"[注入] {path} ← {SHIM_REL}（{how}）")
    return 0


def main():
    check_only = "--check" in sys.argv
    rc = 0
    for name, path in TARGETS:
        rc |= patch(path, check_only)
    if rc == 0:
        print("\n两个程序集都已带上 Win32 shim 的模块初始化器。"
              "（resolver 按程序集生效，各注册一次；策略源码只有一份。）")
    return rc


if __name__ == "__main__":
    sys.exit(main())
