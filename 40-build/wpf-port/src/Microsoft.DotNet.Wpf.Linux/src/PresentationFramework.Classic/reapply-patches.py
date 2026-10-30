#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PresentationFramework.Classic.Linux.csproj 生成后补丁（可重复执行、幂等）。

背景：src/Linux/build/port-lib.py 每次运行都会**整份重写** src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework.Classic/
PresentationFramework.Classic.Linux.csproj。port-lib 能正确处理本工程的
Compile 清单 / 引用 / 身份 / 签名 / GenerateDependencyFile，但**不处理 XAML `<Page>`**
（它只收集 Compile 与 EmbeddedResource）——而主题程序集的全部价值就在
`Themes/Classic.xaml` 编出来的 BAML。故用本脚本补上标记编译链（做法照 T3/HelloWpf 已验证的那套）：

    python3 src/Linux/build/port-lib.py PresentationFramework.Classic
    python3 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework.Classic/reapply-patches.py
    dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework.Classic/PresentationFramework.Classic.Linux.csproj -m:1

补丁清单
--------
A. XAML 输入项（`<Page Include="Themes\\Classic.xaml">`）
   上游 csproj 的 Page 项带 `Generator=MSBuild:Compile`；`UseWPF=false` 时 SDK 不再有
   XAML 通配（那套通配定义在 Microsoft.NET.Sdk.WindowsDesktop.props，条件是 UseWPF=true），
   故显式声明并补齐元数据（Generator / XamlRuntime=Wpf / SubType=Designer），
   与 src/Linux/samples/HelloWpf（T3 已验证）保持一致。

B. 标记编译链（三件，缺一不可）
   ① `_PresentationBuildTasksAssembly` 指向自产 Linux 版 PBT（Release）；
      必须在 Sdk.targets 之前设值：WinFX.targets 里的定义带
      `Condition="'$(_PresentationBuildTasksAssembly)'==''"`（先设值者胜）。
   ② 显式 `<Import Microsoft.WinFX.targets>`：net10.0 的 TargetPlatformIdentifier 为空，
      SDK **不会**自动导入它 ⇒ MarkupCompilePass1/2、MainResourcesGeneration 整条 target 消失，
      后果是「构建成功但静默不产出 BAML」。
   ③ `<Import src/Linux/build/WpfMarkupCompile.Linux.targets>`：任务程序集不存在就报错而非静默跳过。

C. `PresentationUI` 引用
   上游 `PresentationFramework.Classic.csproj` 引用 `PresentationUI.csproj`
   （Classic.xaml 的两处 `ui:PresentationUIStyleResources`，实测 grep：
    `xmlns:ui="clr-namespace:System.Windows.Documents;assembly=PresentationUI"`）。
   port-lib 找不到 `src/Linux/build/PresentationUI.Linux`（该工程在本移植里由
   src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI 顶替，见 docs/U2-PresentationFramework-prep.md §6.4），
   故这里显式接回；Exists() 条件保证缺产物时留下真实编译错误而非静默掩盖。

D. `NoWarn CS8002`
   本工程公开签名（必须——PF 按 `PresentationFramework.classic, Version=…, PublicKeyToken=…`
   全名加载，见 MS/Internal/ReflectionUtils.GetFullAssemblyNameFromPartialName），
   而 WindowsBase 在本移植工程中未签名 ⇒ CS8002。上游「全签名」故无此警告。

E. 构建期自检（BAML 产出 + 身份一致性）
   · BAML：MainResourcesGeneration 之后断言 `obj/…/Classic.baml` 存在 —— 否则会出现
     「构建成功但主题程序集里没有 BAML」这种最难查的失败（T3 的教训）。
   · 身份：断言产物的 AssemblyName/版本与 PF 侧一致（主题程序集必须能被
     `Assembly.Load("PresentationFramework.classic, Version=<PF 的版本>, PublicKeyToken=<PF 的 PKT>")`
     命中，否则 SystemResources.LoadExternalAssembly 会 FileNotFoundException 被静默吞掉）。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(HERE, "PresentationFramework.Classic.Linux.csproj")
MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN_A = "  <!-- ==== WPF-on-Linux 补丁块 A（Sdk.targets 之前）：reapply-patches.py 追加 ==== -->"
END_A = "  <!-- ==== 补丁块 A 结束 ==== -->"
BEGIN_B = "  <!-- ==== WPF-on-Linux 补丁块 B（Sdk.targets 之后）：reapply-patches.py 追加 ==== -->"
END_B = "  <!-- ==== 补丁块 B 结束 ==== -->"

PATCH_A_BEFORE = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 A：XAML 输入项 + 标记编译链接线（照 T3/HelloWpf 已验证做法）
       ============================================================================
       port-lib 不处理 <Page>（只收集 Compile/EmbeddedResource），而主题程序集的全部价值
       就在 Themes/Classic.xaml 编出来的 BAML ⇒ 这里显式声明 Page 项并接上 Linux 标记编译器。
       ============================================================================ -->
  <PropertyGroup>
    <!-- ① 必须在 Sdk.targets 之前：WinFX.targets 的定义带 Condition="…==''"（先设值者胜） -->
    <PbtLinuxDir>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationBuildTasks/</PbtLinuxDir>
    <PbtLinuxConfiguration Condition="'$(PbtLinuxConfiguration)' == ''">Release</PbtLinuxConfiguration>
    <_PresentationBuildTasksAssembly>$([System.IO.Path]::GetFullPath('$(PbtLinuxDir)bin/$(PbtLinuxConfiguration)/net10.0/PresentationBuildTasks.dll'))</_PresentationBuildTasksAssembly>
    <!-- 关掉 UseWPF 后 WindowsDesktop.targets 不再提供这些默认值 -->
    <DefaultXamlRuntime Condition="'$(DefaultXamlRuntime)' == ''">Wpf</DefaultXamlRuntime>
    <!-- ④ 本工程公开签名 + WindowsBase 未签名（混合签名）→ CS8002；上游全签名故无此警告 -->
    <NoWarn>$(NoWarn);CS8002</NoWarn>
    <!-- 上游 PresentationFramework.Classic.csproj 也设了这一条（port-lib 现已搬运，此处仅作说明） -->
  </PropertyGroup>

  <!-- ③ PresentationUI：上游引用 PresentationUI.csproj（Classic.xaml 的 ui:PresentationUIStyleResources）；
       port-lib 找不到 src/Linux/build/PresentationUI.Linux（本移植由 CycleStub.PresentationUI 顶替）→ 显式接回 -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationUI.dll')">
    <Reference Include="PresentationUI"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationUI.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>

  <ItemGroup>
    <Page Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Classic/Themes/Classic.xaml">
      <Link>Themes\\Classic.xaml</Link>
      <Generator>MSBuild:Compile</Generator>
      <XamlRuntime>Wpf</XamlRuntime>
      <SubType>Designer</SubType>
    </Page>
  </ItemGroup>
'''

PATCH_B_AFTER = '''
  <!-- ============================================================================
       WPF-on-Linux 补丁 B：标记编译链的两处 Import（必须在 Sdk.targets 之后）
       ============================================================================
       ② Microsoft.WinFX.targets：net10.0 下 SDK 不自动导入（见文件头注释），
          不导入 = XAML 不编译且构建"成功"（静默失败）。
       ③ src/Linux/build/WpfMarkupCompile.Linux.targets：接管校验（PBT 缺失即报错）。
       ============================================================================ -->
  <PropertyGroup>
    <_WpfLinuxWinFXTargets>$(MSBuildSDKsPath)/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.WinFX.targets</_WpfLinuxWinFXTargets>
  </PropertyGroup>
  <Import Project="$(_WpfLinuxWinFXTargets)" Condition="Exists('$(_WpfLinuxWinFXTargets)')" />
  <Import Project="$(WpfLinuxRoot)src/Linux/build/WpfMarkupCompile.Linux.targets" />

  <!-- ============================================================================
       WPF-on-Linux 补丁 E：构建期自检（BAML 产出 + 身份一致性）
       ============================================================================ -->
  <Target Name="WpfLinuxClassicAssertMarkupChain" BeforeTargets="PrepareResources">
    <Error Condition="!Exists('$(_WpfLinuxWinFXTargets)')"
           Text="找不到 SDK 的 WPF 标记编译 targets：$(_WpfLinuxWinFXTargets)（UseWPF=false 时 net10.0 不自动导入）" />
    <Error Condition="!Exists('$(_PresentationBuildTasksAssembly)')"
           Text="找不到 Linux 版标记编译器：$(_PresentationBuildTasksAssembly)（先 dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationBuildTasks -c Release）" />
    <Message Importance="high" Text="主题标记编译链：PBT=$(_PresentationBuildTasksAssembly)" />
    <Message Importance="high" Text="主题标记编译链：WinFX=$(_WpfLinuxWinFXTargets)" />
  </Target>

  <!-- ============================================================================
       WPF-on-Linux 补丁 F：摘掉 PBT 生成的 GeneratedInternalTypeHelper.g.cs
       ============================================================================
       实测错误（5 条 CS0507）：
         obj/Debug/GeneratedInternalTypeHelper.g.cs(24,35): error CS0507:
           "GeneratedInternalTypeHelper.CreateInstance(Type, CultureInfo)":
           当重写"protected internal"继承成员
           "InternalTypeHelper.CreateInstance(Type, CultureInfo)"时，无法更改访问修饰符
       根因（两条实测事实叠加，非猜测）：
         ① WindowsBase/System/Windows/Markup/InternalTypeHelper.cs:29 的成员是
            `protected internal abstract`；
         ② WindowsBase/OtherAssemblyAttrs.cs:24 有
            `[assembly: InternalsVisibleTo(BuildInfo.PresentationFrameworkClassic)]`
            —— 主题程序集是 WindowsBase 的**友元**，因此 Roslyn 要求 override 写
            `protected internal`（internal 部分对它可见），而 PBT 生成的是 `protected`
            → 必然 CS0507。上游 Windows 构建里 PBT 的 MarkupCompilePass2 会把该文件
            **清空**（MarkupCompilePass2.cs:663-678：无 internal 需求时重写为空），
            我们这条链没跑到那一步。
       为什么可以直接摘掉（本工程实测）：
         Classic.xaml 引用的本地类型 — ClassicBorderDecorator / DataGridHeaderBorder /
         PlatformCulture / ProgressBarBrushConverter / SystemDropShadowChrome —
         **全部是 public**（grep 实测），helper 只在解析「本程序集 internal 类型/成员」时
         才被 XamlTypeMapper 使用；且 PF 源码自己注明
         "We don't actually use the GeneratedInternalTypeHelper any more."（XamlReader.cs:1076）。
       做法：按**文件名**从 @(Compile) 里摘（不写死路径，故对中间目录布局不敏感）。
       ============================================================================ -->
  <Target Name="WpfLinuxClassicDropInternalTypeHelper"
          AfterTargets="MarkupCompilePass1"
          BeforeTargets="CoreCompile">
    <!-- 双保险：① 从 @(Compile) 摘掉；② 把文件内容清空。
         只用 ① 实测无效（item 时序：PBT 通过 <Output ItemName="Compile"> 注入，
         摘除时机与 csc 取用时机之间存在竞态），故补 ②。
         ② 与上游行为等价：PBT 的 MarkupCompilePass2.cs:663-678 在「本程序集无 internal 需求」时
         就是把该文件重写为空并记 InternalTypeHelperNotRequired。 -->
    <ItemGroup>
      <_WpfClassicIth Include="@(Compile)" Condition="'%(Filename)' == 'GeneratedInternalTypeHelper'" />
    </ItemGroup>
    <ItemGroup>
      <Compile Remove="@(_WpfClassicIth)" />
    </ItemGroup>
    <WriteLinesToFile File="$(IntermediateOutputPath)GeneratedInternalTypeHelper.g.cs"
                      Lines="// WPF-on-Linux：内容按补丁 F 清空（等价上游 PBT 的 InternalTypeHelperNotRequired 行为）。"
                      Overwrite="true"
                      Condition="Exists('$(IntermediateOutputPath)GeneratedInternalTypeHelper.g.cs')" />
    <Message Importance="high"
             Text="GeneratedInternalTypeHelper 处置：@(Compile) 中命中 @(_WpfClassicIth->Count()) 条并已摘除/清空（CS0507 根因：WindowsBase 对主题程序集授了 IVT，protected internal 成员的 override 必须同修饰符）" />
  </Target>

  <Target Name="WpfLinuxClassicAssertBaml" AfterTargets="MainResourcesGeneration">
    <ItemGroup>
      <_WpfLinuxClassicBaml Include="@(Page->'$(IntermediateOutputPath)%(Filename).baml')" />
    </ItemGroup>
    <Error Condition="'@(_WpfLinuxClassicBaml)' == ''" Text="工程里没有 Page 输入项 —— XAML 编译链没接上。" />
    <Error Condition="!Exists('%(_WpfLinuxClassicBaml.Identity)')"
           Text="XAML 未编译成 BAML：%(_WpfLinuxClassicBaml.Identity)（检查 PBT 与 WinFX.targets 是否生效）" />
    <Message Importance="high" Text="已产出主题 BAML：@(_WpfLinuxClassicBaml)" />
  </Target>
'''


def _strip(text, begin, end):
    """摘掉一个补丁块，并**连同其后的空行**一起摘掉（否则每跑一次就多出一行空行，
    实测就是这样把幂等性破坏掉的）。"""
    while begin in text:
        i = text.index(begin)
        j = text.index(end) + len(end)
        while j < len(text) and text[j] in "\r\n":
            j += 1
        text = text[:i] + text[j:]
    return text


def main():
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()

    # 幂等：两个块独立摘除（锚点 Sdk.targets import 不在任何块内，故位置恒定）
    text = _strip(text, BEGIN_A, END_A)
    text = _strip(text, BEGIN_B, END_B)

    if MARKER not in text:
        print("[失败] csproj 里找不到 Sdk.targets import 锚点，请检查生成物")
        return 1

    # 规范化拼接（每个块统一 "…\n" 结尾，_strip 每次恰好摘掉一个尾随换行）⇒ 严格幂等
    block_a = BEGIN_A + "\n" + PATCH_A_BEFORE.strip("\n") + "\n" + END_A + "\n"
    block_b = BEGIN_B + "\n" + PATCH_B_AFTER.strip("\n") + "\n" + END_B
    text = text.replace(MARKER, block_a + MARKER + "\n" + block_b, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"[OK] 已注入补丁 A/B/C/E/F → {CSPROJ}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
