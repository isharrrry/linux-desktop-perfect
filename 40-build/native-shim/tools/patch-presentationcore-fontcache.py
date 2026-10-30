#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c 补丁 I 的一键应用器：PresentationCore `FontCache.Util` 的 Windows 字体目录 URI（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-fontcache.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-fontcache.py [--apply]

【根因（实测，非推断）】
  `PresentationCore/MS/internal/FontCache/FontCacheUtil.cs:304`
      static Util()
      {
          string s = Environment.GetEnvironmentVariable(WinDir) + @"\\Fonts\\";
          _windowsFontsLocalPath = s.ToUpperInvariant();
          _windowsFontsUriObject = new Uri(_windowsFontsLocalPath, UriKind.Absolute);   // ← 抛
          ...
      }
  Linux 上 `windir` 未设置 ⇒ `s == "\\Fonts\\"` ⇒ **不是合法的绝对 URI**
  ⇒ `UriFormatException: Invalid URI: The format of the URI could not be determined.`

  触发链（跑 src/Linux/samples/HelloWpf 实测，是本项目的**第 4 条**同级拦路虎）：
      App.Main → new Application() → ApplicationInit
        → BaseUriHelper..cctor                    （补丁 G 修掉注册表 NRE 之后走到这里）
        → Window..cctor → FrameworkElement..cctor → TextElement..cctor
        → SystemFonts.get_ThemeMessageFontSize → SystemFonts.get_MessageFontSize
        → SystemParameters.get_Dpi → MS.Internal.FontCache.Util.get_Dpi
        → Util..cctor → **UriFormatException**
  注意 `TextElement` 在 **Window 的静态构造链**上 ⇒ 与"要不要显示文字"无关：
  任何 WPF 窗口都建不出来。（`SystemFonts.MessageFontSize` 这一步能走到，
  说明 M7b 补丁轮的 `SystemParametersInfo` 修复已经生效。）

【修法：只改静态构造在非 Windows 上的行为】
  Windows 上逐字不变；Linux 上把"平台字体目录"取成真实目录（默认 /usr/share/fonts/，
  可用 WPF_LINUX_FONTS_DIR 覆盖），并且**不做 ToUpperInvariant**。

  为什么是"改成真目录"而不是"随便造一个合法 URI"：
    `Util.WindowsFontsUriObject` 的角色就是「**平台字体目录**」——它是
    `windir\\Fonts\\` 在 Windows 上的语义。Linux 上的对等物就是 /usr/share/fonts。
    消费点（FontFamily.cs:428/507、Fonts.cs:241/262、DWriteFactory.cs:71）拿它
    枚举平台字体、拼字体文件 URI、和字体位置比较 —— 指向真实目录才语义正确。
    造一个假 URI（例如 `file:///nonexistent/`）能让异常消失，但会让
    `FamilyCollection.FromWindowsFonts` **静默变空**，那是"为了让它过而绕过"。

  为什么**不能**保留 ToUpperInvariant：
    "路径统一大写"是 Windows 大小写不敏感文件系统的假设。Linux 区分大小写，
    `/USR/SHARE/FONTS/` 是个不存在的目录 ⇒ 与造假 URI 同样的静默退化。

【为什么用生成式补丁】
  与补丁 G/H 同一套机制：从 upstream 逐字读入、只替换静态构造、写到
  `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FontCacheUtil.Linux.cs`（与 SR.g.cs 同为生成物），
  csproj 侧 Remove 上游 + Include 生成物。锚点找不到 → **报错退出**（不静默降级）。
  重放顺序：port-lib.py → reapply-patches.py(F/D/G) → patch-presentationcore-apartment.py(H)
            → 本脚本(I)
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
GENERATED = os.path.join(PC_DIR, "FontCacheUtil.Linux.cs")
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                        "PresentationCore", "MS", "internal", "FontCache", "FontCacheUtil.cs")

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7c 补丁 I：FontCache.Util 的字体目录 URI"
                "（由 src/Linux/tools/patch-presentationcore-fontcache.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7c 补丁 I 结束 ==== -->"

# 逐字锚点：上游静态构造的前三行（含缩进）。
ANCHOR = (
    '        static Util()\n'
    '        {\n'
    '            string s = Environment.GetEnvironmentVariable(WinDir) + @"\\Fonts\\";\n'
    '\n'
    '            _windowsFontsLocalPath = s.ToUpperInvariant();\n'
)

REPLACEMENT = '''        static Util()
        {
            // ── WPF-on-Linux M7c 补丁 I（由 src/Linux/tools/patch-presentationcore-fontcache.py 插入）──
            // Linux 上没有 windir："windir"(null) + "\\Fonts\\" == "\\FONTS\\"，它不是合法
            // 的绝对 URI ⇒ `new Uri(..., UriKind.Absolute)` 抛 UriFormatException ⇒
            // MS.Internal.FontCache.Util 的静态构造失败 ⇒ TextElement / FrameworkElement /
            // Window 全线建不出来（**任何** WPF 窗口，与是否显示文字无关）。
            //
            // Windows 分支逐字保持上游；非 Windows 用**平台字体目录**：
            //   · 这个属性的角色就是"平台字体目录"（windir\\Fonts\\ 的语义），
            //     消费点拿它枚举平台字体 / 拼字体文件 URI / 比对字体位置
            //     （FontFamily.cs:428/507、Fonts.cs:241/262、DWriteFactory.cs:71）；
            //   · 指向真实目录才语义正确 —— 造个假 URI 能消掉异常，但会让
            //     FamilyCollection.FromWindowsFonts 静默变空，那是绕过而不是修复；
            //   · **不做 ToUpperInvariant**："统一大写"是 Windows 大小写不敏感文件系统的
            //     假设，Linux 上 /USR/SHARE/FONTS/ 并不存在，等于同样的静默退化。
            //   可用 WPF_LINUX_FONTS_DIR 覆盖。
            if (!OperatingSystem.IsWindows())
            {
                string linuxFonts = Environment.GetEnvironmentVariable("WPF_LINUX_FONTS_DIR");
                if (string.IsNullOrEmpty(linuxFonts)) linuxFonts = "/usr/share/fonts/";
                if (!linuxFonts.EndsWith("/", StringComparison.Ordinal)) linuxFonts += "/";

                _windowsFontsLocalPath = linuxFonts;
                _windowsFontsUriObject = new Uri(linuxFonts, UriKind.Absolute);
                _windowsFontsUriString = _windowsFontsUriObject.GetComponents(
                    UriComponents.AbsoluteUri, UriFormat.SafeUnescaped);
                return;
            }

            string s = Environment.GetEnvironmentVariable(WinDir) + @"\\Fonts\\";
'''

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-fontcache.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/FontCache/FontCacheUtil.cs`
//        逐字复制 + 在 `static Util()` 里插入非 Windows 分支（见下方补丁 I 标记）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么要打：
//   Linux 上 `windir` 未设置 ⇒ `Environment.GetEnvironmentVariable("windir") + "\\Fonts\\"`
//   == `"\\FONTS\\"` ⇒ 不是合法绝对 URI ⇒ `new Uri(..., UriKind.Absolute)` 抛
//   UriFormatException ⇒ `MS.Internal.FontCache.Util` 静态构造失败 ⇒
//   TextElement → FrameworkElement → Window 全线建不出来（任何 WPF 窗口）。
// 修法语义：把这个属性指向**平台字体目录**（/usr/share/fonts/，可 WPF_LINUX_FONTS_DIR 覆盖），
//   不造假 URI、不做 Linux 上无意义的大写归一。Windows 分支逐字不变。
//
// ↓↓↓ 以下为上游原文（仅静态构造处有补丁 I 标记）↓↓↓
"""


def generate(check_only):
    if not os.path.exists(UPSTREAM):
        print(f"[失败] 找不到上游 {UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        text = f.read()

    if ANCHOR not in text:
        print("[失败] 上游 FontCacheUtil.cs 里找不到锚点，补丁 I 无法应用（上游改过这段？）。")
        print("       期望锚点前三行：")
        for line in ANCHOR.splitlines()[:3]:
            print("         " + line)
        return 1

    output = HEADER + text.replace(ANCHOR, REPLACEMENT, 1)

    up_to_date = False
    if os.path.exists(GENERATED):
        with open(GENERATED, encoding="utf-8") as f:
            up_to_date = (f.read() == output)

    if check_only:
        print(f"[检查] {os.path.relpath(GENERATED, ROOT)}："
              + ("内容已是最新" if up_to_date else "缺失/与上游不同步（需要重新生成）"))
    elif up_to_date:
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：内容已是最新（未重写）")
    else:
        with open(GENERATED, "w", encoding="utf-8") as f:
            f.write(output)
        print(f"[生成] {os.path.relpath(GENERATED, ROOT)}：已从上游重生成（插入非 Windows 分支）")

    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0

    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if anchor not in csproj:
        print("[失败] csproj 里找不到 Sdk.targets 锚点")
        return 1

    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             '    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/FontCache/FontCacheUtil.cs" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FontCacheUtil.Linux.cs" />\n'
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
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
