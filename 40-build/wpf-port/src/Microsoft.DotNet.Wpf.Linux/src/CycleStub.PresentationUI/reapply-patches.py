#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CycleStub.PresentationUI.Linux.csproj 生成后补丁（可重复执行、幂等）。

`T-B16`（`PRECOND-TAB3-PAGE-RENDER`）：把 PresentationUI 主题字典里**唯一**缺失的
`PUIPageViewStyleKey`（`FlowDocumentReader` 内部分页查看器的页宿主样式）按**上游同源**
补进本移植运行期的 PresentationUI 程序集。

背景（件:行，见 `src/Linux/build/MilBridge/P1-hctab3-impl-report.md` §1）：
  · `FlowDocumentReader.GetViewerFromMode` 给内部 `ReaderPageViewer` 显式设
    `Style = ComponentResourceKey(typeof(PresentationUIStyleResources), "PUIPageViewStyleKey")`
    （`upstream/.../System/Windows/Controls/FlowDocumentReader.cs:2043-2052` 定义键，
     `:1150-1156` 设值）。该键的**唯一**出处是 PresentationUI 的主题字典
    （`upstream/.../PresentationUI/Themes/Generic.xaml:8675-8699`）。
  · 本移植的 PresentationUI 是**替身**（本目录，原无 `Themes/`、无 `ThemeInfo`）⇒
    `SystemResources.FindDictionaryResource`（`System/Windows/SystemResources.cs:339-386`
    ＋ `:742-759` `LoadDictionaryLocations`）按 `ThemeInfoAttribute` 找该程序集的
    `themes/generic.baml`，找不到 ⇒ 键解析为空 ⇒ `ReaderPageViewer` **没有 ControlTemplate**
    ⇒ 它**从不构造 `DocumentPageView`** ⇒ 「查看器」区空白。

本补丁（三条，全部**生成**、`temp+rename` 落盘）：
  A. `Themes/Generic.xaml` —— **逐字**取上游 `:8675-8699` 那一个 `Style` 块（`PUIPageViewStyleKey`），
     包一层 `<ResourceDictionary>`；**不**复制视觉、**不**改几何。**最小必要面** ＝ 这一个键。
  B. `ThemeInfo.Linux.cs` —— `[assembly: ThemeInfo(None, SourceAssembly)]`（键位与上游
     `PresentationUI/OtherAssemblyAttrs.cs:11` 同源；上游是 `(SourceAssembly, SourceAssembly)`，
     本移植**不经**主题字典（`themed`）面 ⇒ themed 位置取 `None`，generic 取 `SourceAssembly`）。
  C. csproj 补丁块 A/B —— ① `Page` 输入项 ＋ 标记编译链（照 `PresentationFramework.Classic.Linux`
     已验证的一套：`_PresentationBuildTasksAssembly` → Linux 版 PBT、显式 WinFX.targets、
     `WpfMarkupCompile.Linux.targets`）；② 指向自产 PF 的 `<Reference>`（**只**为标记编译期解析
     `FlowDocumentPageViewer`/`DocumentPageView`/`AdornerDecorator` 三个类型；用 `HintPath`
     引用**已建成的** PF，故不构成工程引用环）。

调用（与 `PresentationFramework.Classic.Linux` 同形）：
    python3 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/reapply-patches.py
    dotnet build src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/CycleStub.PresentationUI.Linux.csproj -c Release -m:1
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
UPSTREAM_GENERIC = os.path.join(
    REPO, "src/Microsoft.DotNet.Wpf/src/PresentationUI/Themes/Generic.xaml")
CSPROJ = os.path.join(HERE, "CycleStub.PresentationUI.Linux.csproj")
THEMES_DIR = os.path.join(HERE, "Themes")
GENERIC_XAML = os.path.join(THEMES_DIR, "Generic.xaml")
THEMEINFO_CS = os.path.join(HERE, "ThemeInfo.Linux.cs")

# 上游那个 Style 块的**内容锚**（不用行号：纪律 31）
KEY_ANCHOR = "ResourceId=PUIPageViewStyleKey}"

MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN_A = "  <!-- ==== WPF-on-Linux 补丁块 A（T-B16，Sdk.targets 之前）：reapply-patches.py 追加 ==== -->"
END_A = "  <!-- ==== 补丁块 A 结束（T-B16） ==== -->"
BEGIN_B = "  <!-- ==== WPF-on-Linux 补丁块 B（T-B16，Sdk.targets 之后）：reapply-patches.py 追加 ==== -->"
END_B = "  <!-- ==== 补丁块 B 结束（T-B16） ==== -->"

XAML_HEAD = (
    "<!-- ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/reapply-patches.py **生成**，不要手改。\n"
    "     `T-B16`：从上游 `PresentationUI/Themes/Generic.xaml` 逐字取出的 `PUIPageViewStyleKey` 块。 -->\n"
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"'
    ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'
    ' xmlns:ui="clr-namespace:System.Windows.Documents">\n'
)
XAML_TAIL = "</ResourceDictionary>\n"

THEMEINFO_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/reapply-patches.py **生成**，不要手改。
//
// `T-B16`：主题字典位置声明。键位与上游 `PresentationUI/OtherAssemblyAttrs.cs:11` 同源
//   （上游：`ThemeInfo(SourceAssembly, SourceAssembly)`）。本移植的 PresentationUI 只提供
//   generic 字典（`Themes/Generic.xaml`），不经「themed」面 ⇒ themed 位置取 `None`。
// 消费方：`SystemResources.ResourceDictionaries.LoadDictionaryLocations`
//   （`upstream/.../System/Windows/SystemResources.cs:742-759`）。
using System.Windows;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
'''


def _extract_style_block():
    """从上游 Generic.xaml 逐字取出 `PUIPageViewStyleKey` 那个 `<Style>…</Style>`（含缩进）。"""
    with open(UPSTREAM_GENERIC, encoding="utf-8") as f:
        text = f.read()
    i = text.find(KEY_ANCHOR)
    if i < 0:
        raise RuntimeError("上游 Generic.xaml 里找不到内容锚 %r" % KEY_ANCHOR)
    # 向前找该块的起始 `<Style `（行首 4 空格缩进）
    head = text.rfind("\n    <Style ", 0, i)
    if head < 0:
        raise RuntimeError("找不到该 Style 块的起始行")
    head += 1  # 跳过换行，保留 4 空格缩进
    # 向后找同缩进的结束标记
    tail = text.find("\n    </Style>", i)
    if tail < 0:
        raise RuntimeError("找不到该 Style 块的结束行")
    block = text[head:tail + len("\n    </Style>")]
    if KEY_ANCHOR not in block or "DocumentPageView" not in block:
        raise RuntimeError("取出的块不自洽（缺内容锚或 DocumentPageView）")
    if block.count("<Style ") != 1 or block.count("</Style>") != 1:
        raise RuntimeError("取出的块不是恰好一个 Style")
    return block


def _write_atomic(path, text):
    """`temp+rename`（同目录，避免跨设备 rename）。"""
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    tmp = path + ".tmp.%d" % os.getpid()
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def _strip(text, begin, end):
    """摘掉一个补丁块，并连同其后的空行一起摘掉（否则每跑一次多一行空行 ⇒ 幂等被破坏）。"""
    while begin in text:
        i = text.index(begin)
        j = text.index(end) + len(end)
        while j < len(text) and text[j] in "\r\n":
            j += 1
        text = text[:i] + text[j:]
    return text


def _block_a():
    return '''  <!-- ============================================================================
       WPF-on-Linux 补丁 A（`T-B16`）：主题字典输入项 ＋ 标记编译链 ＋ 标记编译期类型源
       ============================================================================
       ① `_PresentationBuildTasksAssembly` 必须在 Sdk.targets 之前设值
          （Microsoft.WinFX.targets 的定义带 Condition="…==''"，先设值者胜）。
       ② 自产 PF 的 `<Reference>`：**只**为标记编译期解析 `FlowDocumentPageViewer` /
          `DocumentPageView` / `AdornerDecorator`（都在 PF）——用 HintPath 引用**已建成的** PF，
          不是 ProjectReference ⇒ 不引入「PF ⇄ PresentationUI」工程引用环。
          `Private=false`：不要把 PF 复制进本替身的输出目录。
       ③ `<Page Themes/Generic.xaml>`：`Generator=MSBuild:Compile`；`UseWPF=false` 时 SDK 不再
          提供 XAML 通配（那套定义在 Microsoft.NET.Sdk.WindowsDesktop.props，条件 UseWPF=true）
          ⇒ 必须显式声明（照 `PresentationFramework.Classic.Linux` 已验证的一套）。
       ============================================================================ -->
  <PropertyGroup>
    <PbtLinuxDir>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationBuildTasks/</PbtLinuxDir>
    <PbtLinuxConfiguration Condition="'$(PbtLinuxConfiguration)' == ''">Release</PbtLinuxConfiguration>
    <_PresentationBuildTasksAssembly>$([System.IO.Path]::GetFullPath('$(PbtLinuxDir)bin/$(PbtLinuxConfiguration)/net10.0/PresentationBuildTasks.dll'))</_PresentationBuildTasksAssembly>
    <DefaultXamlRuntime Condition="'$(DefaultXamlRuntime)' == ''">Wpf</DefaultXamlRuntime>
    <!-- ⚠️ 闸：**缺省关**（照 T-B11/T-B13 的处置）。开着时页宿主确实被接出来了，但
         (a) tab3 帧面判据不成立、(b) 该序形态下 tab2 帧变、(c) 新增一条 `E_HANDLE` ⇒
         不默认启用。`-p:WpfLinuxPresentationUITheme=true` 才开（可复现）。 -->
    <WpfLinuxPresentationUITheme Condition="'$(WpfLinuxPresentationUITheme)' == ''">false</WpfLinuxPresentationUITheme>
  </PropertyGroup>

  <ItemGroup Condition="'$(WpfLinuxPresentationUITheme)' == 'true'">
    <Reference Include="PresentationFramework">
      <HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/Release/PresentationFramework.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup Condition="'$(WpfLinuxPresentationUITheme)' == 'true'">
    <Compile Include="ThemeInfo.Linux.cs" />
  </ItemGroup>

  <ItemGroup Condition="'$(WpfLinuxPresentationUITheme)' == 'true'">
    <Page Include="Themes/Generic.xaml">
      <Link>Themes\\Generic.xaml</Link>
      <Generator>MSBuild:Compile</Generator>
      <XamlRuntime>Wpf</XamlRuntime>
      <SubType>Designer</SubType>
    </Page>
  </ItemGroup>
'''

def _block_b():
    return '''
  <!-- ============================================================================
       WPF-on-Linux 补丁 B（`T-B16`，Sdk.targets 之后）：标记编译链 ＋ 构建期自检
       ============================================================================
       ⚠️ net10.0 的 `TargetPlatformIdentifier` 为空，SDK **不会**自动导入 Microsoft.WinFX.targets
          ⇒ MarkupCompilePass1/2、MainResourcesGeneration 整条 target 消失
          ⇒ 后果是「构建成功但静默不产出 BAML」（Classic 的 T3 教训）。
       ============================================================================ -->
  <PropertyGroup>
    <_WpfLinuxWinFXTargets>$(MSBuildSDKsPath)/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.WinFX.targets</_WpfLinuxWinFXTargets>
  </PropertyGroup>
  <Import Project="$(_WpfLinuxWinFXTargets)" Condition="Exists('$(_WpfLinuxWinFXTargets)')" />
  <Import Project="$(WpfLinuxRoot)src/Linux/build/WpfMarkupCompile.Linux.targets" />

  <!-- 摘掉 PBT 生成的 GeneratedInternalTypeHelper.g.cs（同 Classic 补丁 F：WindowsBase 对本
       程序集授了 IVT ⇒ override 必须 `protected internal`，而 PBT 生成 `protected` ⇒ CS0507；
       本 XAML 引用的本地类型 `PresentationUIStyleResources` 是 public，helper 用不到）。 -->
  <Target Name="WpfLinuxPuiDropInternalTypeHelper"
          AfterTargets="MarkupCompilePass1" BeforeTargets="CoreCompile">
    <ItemGroup>
      <_WpfPuiIth Include="@(Compile)" Condition="'%(Filename)' == 'GeneratedInternalTypeHelper'" />
    </ItemGroup>
    <ItemGroup>
      <Compile Remove="@(_WpfPuiIth)" />
    </ItemGroup>
    <WriteLinesToFile File="$(IntermediateOutputPath)GeneratedInternalTypeHelper.g.cs"
                      Lines="// WPF-on-Linux：内容按 `T-B16` 清空（等价 PBT 的 InternalTypeHelperNotRequired 行为）。"
                      Overwrite="true"
                      Condition="Exists('$(IntermediateOutputPath)GeneratedInternalTypeHelper.g.cs')" />
  </Target>

  <!-- 构建期自检：BAML 真的产出了吗（「构建成功但没有 BAML」是最难查的失败）。
       只在闸开时自检（闸关时工程里没有 Page 项，自检会误报）。 -->
  <Target Name="WpfLinuxPuiAssertBamlAndThemeInfo" AfterTargets="MainResourcesGeneration"
          Condition="'$(WpfLinuxPresentationUITheme)' == 'true'">
    <Error Condition="!Exists('$(_WpfLinuxWinFXTargets)')"
           Text="找不到 SDK 的 WPF 标记编译 targets：$(_WpfLinuxWinFXTargets)" />
    <Error Condition="!Exists('$(_PresentationBuildTasksAssembly)')"
           Text="找不到 Linux 版标记编译器：$(_PresentationBuildTasksAssembly)" />
    <ItemGroup>
      <_WpfPuiBaml Include="@(Page->'$(IntermediateOutputPath)%(RelativeDir)%(Filename).baml')" />
    </ItemGroup>
    <Error Condition="'@(_WpfPuiBaml)' == ''" Text="工程里没有 Page 输入项 —— XAML 编译链没接上。" />
    <Error Condition="!Exists('%(_WpfPuiBaml.Identity)')"
           Text="XAML 未编译成 BAML：%(_WpfPuiBaml.Identity)" />
    <Message Importance="high" Text="PresentationUI 主题 BAML：@(_WpfPuiBaml)" />
  </Target>
'''


def main():
    # ① 生成 Themes/Generic.xaml（逐字取上游那一个 Style 块）
    try:
        block = _extract_style_block()
    except RuntimeError as e:
        print("[失败] 上游取块失败：%s" % e)
        return 1
    _write_atomic(GENERIC_XAML, XAML_HEAD + block + "\n" + XAML_TAIL)
    print("[OK] 生成 %s（%d 字节，1 个 Style，key=PUIPageViewStyleKey）"
          % (os.path.relpath(GENERIC_XAML, REPO), len(XAML_HEAD + block + XAML_TAIL)))

    # ② 生成 ThemeInfo.Linux.cs
    _write_atomic(THEMEINFO_CS, THEMEINFO_TEXT)
    print("[OK] 生成 %s（ThemeInfo(None, SourceAssembly)）" % os.path.relpath(THEMEINFO_CS, REPO))

    # ③ 幂等注入 csproj 补丁块 A/B
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()
    text = _strip(text, BEGIN_A, END_A)
    text = _strip(text, BEGIN_B, END_B)
    if MARKER not in text:
        print("[失败] csproj 里找不到 Sdk.targets import 锚点")
        return 1
    block_a = BEGIN_A + "\n" + _block_a().strip("\n") + "\n" + END_A + "\n"
    block_b = BEGIN_B + "\n" + _block_b().strip("\n") + "\n" + END_B
    text = text.replace(MARKER, block_a + MARKER + "\n" + block_b, 1)
    _write_atomic(CSPROJ, text)
    print("[OK] 已注入补丁 A/B → %s" % os.path.relpath(CSPROJ, REPO))
    return 0


if __name__ == "__main__":
    sys.exit(main())
