#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c 补丁 L 的一键应用器：PresentationFramework 主题字典加载路径上的 `XamlAccessLevel`（幂等）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-xamlaccess.py --check
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationframework-xamlaccess.py [--apply]

【它挡的是什么（#T 落地后的**第一个**新卡点，实测栈）】
  #T 把主题程序集 `PresentationFramework.Classic` 建好并部署之后，窗口第一次取主题样式
  就会走到 `SystemResources.ResourceDictionaries.LoadDictionary`，在那里：

    Unhandled exception. System.Windows.Markup.XamlParseException:
      'Initialization of 'System.Windows.Controls.TextBlock' threw an exception.' L16 P39
     ---> System.PlatformNotSupportedException:
          System.Windows.Extensions types are not supported on this platform.
       at System.Xaml.Permissions.XamlAccessLevel.AssemblyAccessTo(Assembly assembly)
       at System.Windows.SystemResources.ResourceDictionaries.LoadDictionary(...)  SystemResources.cs:938
       at System.Windows.SystemResources.ResourceDictionaries.LoadThemedDictionary(Boolean) SystemResources.cs:631
       at System.Windows.SystemResources.FindDictionaryResource(...)               SystemResources.cs:364
       at System.Windows.SystemResources.FindResourceInternal(Object)              SystemResources.cs:169
       at System.Windows.StyleHelper.GetThemeStyle(FrameworkElement, FrameworkContentElement) StyleHelper.cs:213
       at System.Windows.FrameworkElement.UpdateThemeStyleProperty()               FrameworkElement.cs:640
       at System.Windows.FrameworkElement.OnInitialized(EventArgs)                 FrameworkElement.cs:5469
  ⇒ 挡在**任何控件第一次取主题样式**的路上（离"窗口出内容"只差这一步）。

【为什么在 Linux 上必然抛】
  `System.Xaml.Permissions.XamlAccessLevel` 这个类型**不在** WPF 的源码树里，它由
  NuGet 包 `System.Windows.Extensions` 提供（`System.Xaml.csproj:91` 的 PackageReference）；
  而该包在非 Windows 上对这批类型**显式抛** PNSE（消息就是
  "System.Windows.Extensions types are not supported on this platform."）。
  上游 WPF 不守卫它，因为 WPF 只在 Windows 上跑。

【修法：非 Windows 上**不设置** AccessLevel —— 放宽一条已经不存在的安全限制】
  `XamlAccessLevel` 是 CAS（代码访问安全）时代的产物："只允许某个程序集访问它自己的
  internal 类型"。.NET Core 里 CAS 已经不存在，`AccessLevel` 的语义退化成一个**可空**的
  提示值：
    · `XamlObjectWriterSettings.AccessLevel` 的默认值就是 `null`（不限制）；
    · System.Xaml 自己处处按"可能为 null"处理 ——
      `ObjectWriterContext.LocalAssembly` 就是
      `if (result is null && _settings?.AccessLevel is not null) result = Assembly.Load(_settings.AccessLevel.AssemblyAccessToAssemblyName);`
      （ObjectWriterContext.cs:114-117）；
    · 而 `LoadDictionary` 这条路径上，**LocalAssembly 本来就由 reader 侧给出**：
      同一个函数上面几行写了 `Baml2006ReaderSettings { LocalAssembly = assembly }`
      （SystemResources.cs:928-931）。
  ⇒ 在非 Windows 上跳过这次赋值 = 从"限制某程序集"变成"不限制"，是**放宽**，
    不是伪造一个成功的返回值，也不是吞掉异常继续跑。

【生成式补丁（与 F/G/H/I/J/K 同一套机制）】
  从 upstream 逐字读入 → 只把那一行包进 `if (System.OperatingSystem.IsWindows())` →
  写到 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SystemResources.Linux.cs`，
  csproj 侧 Remove 上游 + Include 生成物。锚点/计数对不上 → **报错退出**。
  重放顺序：port-lib.py → reapply-patches.py(F/D/G) → patch-presentationcore-apartment.py(H)
            → patch-presentationcore-fontcache.py(I) → patch-presentationcore-registry.py(J)
            → patch-presentationcore-olecontext.py(K) → 本脚本(L)

【只改一处，不改第二处 —— 有理由】
  全仓 `XamlAccessLevel.AssemblyAccessTo` 只有两个**构造点**：
    ① `SystemResources.cs:938`（主题字典；**本补丁**，实测挡路）
    ② `System/Xaml/Markup/XamlReader.cs:1098`（`internalTypeHelper != null` 分支；
       实测 HelloWpf 的 BAML 已经加载成功 ⇒ 当前路径没走到它）
  ② 不动：那个分支的存在意义正是"允许 internal 类型"，在那里把 AccessLevel 置 null
  会削弱它的语义，而它现在还没挡路。它一旦挡路，按同一手法处理，并在报告里登记。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PF_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationFramework")
CSPROJ = os.path.join(PF_DIR, "PresentationFramework.Linux.csproj")
UP = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src",
                  "PresentationFramework")
UP_REL = "System/Windows/SystemResources.cs"
GEN_NAME = "SystemResources.Linux.cs"

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7c 补丁 L：主题字典路径上的 XamlAccessLevel"
                "（由 src/Linux/tools/patch-presentationframework-xamlaccess.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7c 补丁 L 结束 ==== -->"

OLD = "                        owSettings.AccessLevel = XamlAccessLevel.AssemblyAccessTo(assembly);\n"
NEW = """                        // ↓↓↓ WPF-on-Linux M7c 补丁 L：非 Windows 上不设置 AccessLevel ↓↓↓
                        // `XamlAccessLevel` 由 NuGet 包 System.Windows.Extensions 提供，它在非 Windows 上
                        // **显式抛** PlatformNotSupportedException（实测消息见
                        // docs/U2-M7c-report.md §4.10）。CAS 在 .NET Core 上已不存在，AccessLevel 的默认值
                        // 就是 null（不限制），System.Xaml 处处按可空处理
                        // （ObjectWriterContext.cs:114-117），而本路径的 LocalAssembly 由上面的
                        // `Baml2006ReaderSettings { LocalAssembly = assembly }` 提供。
                        // ⇒ 跳过赋值 = 把"限制某程序集"放宽成"不限制"，不是伪造成功。
                        if (System.OperatingSystem.IsWindows())
                        {
                            owSettings.AccessLevel = XamlAccessLevel.AssemblyAccessTo(assembly);
                        }
                        // ↑↑↑ 补丁 L 结束 ↑↑↑
"""

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-xamlaccess.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 +
//        把 `owSettings.AccessLevel = XamlAccessLevel.AssemblyAccessTo(assembly);`
//        包进 `if (System.OperatingSystem.IsWindows())`（见下方补丁 L 标记）。
// 每次运行该脚本都会从上游重读重生成；锚点计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// 为什么必须打：`XamlAccessLevel`（System.Windows.Extensions 包）在非 Windows 上显式抛
//   PlatformNotSupportedException，而它在 **任何控件第一次取主题样式** 的路上
//   （StyleHelper.GetThemeStyle → SystemResources.LoadThemedDictionary → LoadDictionary:938）。
// 语义：CAS 已不存在，AccessLevel 默认就是 null（不限制），且本路径的 LocalAssembly 由
//   reader 侧给出 ⇒ 跳过赋值是**放宽**一条不存在的安全限制，不是伪造。
//
// ↓↓↓ 以下为上游原文（仅补丁 L 那一处有改动）↓↓↓
"""


def generate(check_only):
    src = os.path.join(UP, UP_REL)
    gen = os.path.join(PF_DIR, GEN_NAME)
    if not os.path.exists(src):
        print(f"[失败] 找不到上游 {src}")
        return 1
    with open(src, encoding="utf-8-sig") as f:
        text = f.read()

    got = text.count(OLD)
    if got != 1:
        print(f"[失败] 上游 {UP_REL}：期望 1 处补丁 L 锚点，实际 {got} 处 —— "
              f"上游改过这段，补丁 L 不能盲目应用。")
        return 1
    content = HEADER.format(upstream=UP_REL) + text.replace(OLD, NEW)

    up_to_date = os.path.exists(gen)
    if up_to_date:
        with open(gen, encoding="utf-8") as f:
            up_to_date = (f.read() == content)
    if check_only:
        print(f"[检查] {GEN_NAME}：{'内容已是最新' if up_to_date else '缺失/与上游不同步'}")
    elif up_to_date:
        print(f"[生成] {GEN_NAME}：内容已是最新（未重写）")
    else:
        with open(gen, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[生成] {GEN_NAME}：已从上游重生成")

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

    lines = [
        MARKER_BEGIN,
        "  <ItemGroup>",
        f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/{UP_REL}" />',
        f'    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/{GEN_NAME}" />',
        "  </ItemGroup>",
        MARKER_END,
    ]
    csproj = csproj.replace(anchor, "\n".join(lines) + "\n" + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print("[接线] 已注入 2 行到 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PresentationFramework.Linux.csproj -m:1")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    args = ap.parse_args()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
