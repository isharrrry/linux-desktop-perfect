#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PresentationCore.Linux.csproj 生成后补丁（可重复执行、幂等）。

背景：src/Linux/build/port-lib.py 每次运行都会**整份重写** src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/
PresentationCore.Linux.csproj，因此生成物之外必需的改动必须有可重放的出处。
本脚本就是那个出处。标准链（M4 验收链）：

    python3 src/Linux/build/port-lib.py PresentationCore
    python3 src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/reapply-patches.py
    dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj

补丁清单（M4 第二轮；基线＝主控已修 port-lib 的 4 处缺口）
----------------------------------------------------------
B. 公开签名（PublicSign）—— **必需**
   WindowsBase.dll 的 [InternalsVisibleTo("PresentationCore, PublicKey=<WCP>")]
   要求消费方带 Microsoft WCP 公钥；未签名消费方实测 CS0281 ×290，并连带
   CS0115/CS0122/CS0426 等 400 余条。用只含公钥的 src/Linux/build/keys/WcpPublicKey.snk
   公开签名即可（.NET Core 不校验强名称签名）。

G. Linux 版 Factory + 字体面令牌桥 —— **必需（T2 · 2026-09-10 起）**
   上游 MS/internal/Text/TextInterface/Factory.cs 围绕一个**原生 IDWriteFactory 指针**
   写成：初始化取 dwrite.dll 的 DWriteCreateFactory 函数指针，之后 6 处解引用 vtable
   （RegisterFontFileLoader:73 / RegisterFontCollectionLoader:84 / CreateFontFace:214 /
    GetSystemFontCollection:281 / CreateCustomFontCollection:312 / CreateTextAnalyzer:328）。
   Linux 上 dwrite.dll 不存在（DWriteLoader.LoadDWrite 抛 DllNotFoundException），
   函数指针恒为 null → 调用即崩。处置：**编译期替换**该文件为
   src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.Factory.Linux.cs（同命名空间/同类型名/同 internal 面，
   全部落到 T2 的托管 provider），并顺带编入字体面令牌桥
   src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.FontBridge.cs（[ModuleInitializer] 安装，不碰
   Win32ShimResolver 的 DllImportResolver 槽位）。

F. 接上 DirectWrite.Linux.Provider —— **必需（T2 · 2026-09-10 起）**
   DirectWriteForwarder 的托管面现在把 A/B/C 档 47 条 PNSE 成员委托给
   src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider（Skia/FreeType 承载，见该目录 REPORT.md）。
   于是 DWF 的这几个类型上出现了**参数/返回类型来自 Provider 的成员**
   （FontFile/FontFace/FontCollection 的托管构造重载、Font.LinuxFont 等）。
   C# 在绑定 `new FontFile(dwriteFontFile)` 这类调用时会检查**整个重载集**，
   只要重载集里有"未引用程序集里的类型"，就报 CS0012（实测 PresentationCore 4 条：
   Factory.cs:162/227/285/321）—— 所以 PresentationCore 必须直接引用 Provider。

D. 接上 DirectWriteForwarder.Linux —— **必需**
   上游 DirectWriteForwarder 是 C++/CLI（.vcxproj），port-lib 对 .vcxproj 一律丢弃，
   其托管面（41 个类型）在 Linux 上整体缺失 —— 实测是 PresentationCore 0 错的唯一
   成规模阻塞（102 条错误）。本工程按上游树内 CPP/DWriteWrapper/*.h 落地了
   src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder，这里把它的产物接成本工程的本地引用。

已移除（主控已根治，保留说明防回退）
------------------------------------
A. 门面程序集遮蔽：Microsoft.NETCore.App.Ref 的 WindowsBase.dll 门面（v4.0.0.0）曾按
   「版本高者胜」压过自产 WindowsBase（0.0.0.0），造成 6000 条级联错误。现由
   src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs（AssemblyVersion 4.0.0.1）根治，port-lib 自动
   注入到每个移植工程 → A 变冗余，不再注入。
C. 9 条 Compile Remove（A 类误纳文件）：port-lib 现在同时识别
   <EnableDefaultItems>false</EnableDefaultItems>，且 excludes 过滤已移到流水线末尾
   → 生成物里 A 类 0 个、OLE 8 文件按 src/Linux/build/excludes/PresentationCore.txt 剔除，
   无需 csproj 侧兜底。
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(HERE, "PresentationCore.Linux.csproj")
MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN = "  <!-- ==== WPF-on-Linux 补丁开关：以下内容由 reapply-patches.py 追加 ==== -->"
END = "  <!-- ==== WPF-on-Linux 补丁结束 ==== -->"

PATCH_B = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 B：公开签名（PublicSign）以通过 WindowsBase 的 IVT 检查
       ============================================================================
       现象（实测）：WindowsBase.dll 里 [InternalsVisibleTo("PresentationCore,
       PublicKey=0024...")] 授予的是 Microsoft WCP 公钥（上游
       Shared/RefAssemblyAttrs.cs 的 WCP_PUBLIC_KEY_STRING）。本工程输出未签名
       （公钥为空），C# 编译器拒绝把 WindowsBase 的 internal 成员暴露给未签名程序集：
       实测 CS0281 ×290，并连带 CS0115（不能重写 internal 虚成员）、CS0426
       （internal 嵌套类型不可见）等 400 余条级联。
       处置：用「只含公钥」的 .snk 公开签名（PublicSign）。.NET Core 运行时不校验
       强名称签名，公开签名产物在 Linux 上可正常加载；上游 dotnet/wpf 在 Linux 上
       构建也走同一机制（Arcade 的公开签名）。公钥从 WindowsBase.dll 的 IVT 串提取
       （非机密，160 字节），落在 src/Linux/build/keys/WcpPublicKey.snk。
       ============================================================================ -->
  <PropertyGroup>
    <SignAssembly>true</SignAssembly>
    <PublicSign>true</PublicSign>
    <AssemblyOriginatorKeyFile>$(WpfLinuxRoot)src/Linux/build/keys/WcpPublicKey.snk</AssemblyOriginatorKeyFile>
    <!-- CS8002「引用程序集没有强名称」：本工程按 IVT 要求公开签名后，被引用方
         （WindowsBase / System.Xaml / UIAutomationTypes / UIAutomationProvider /
         System.Windows.Input.Manipulations，均由其它里程碑产出）目前仍是未签名程序集，
         于是逐引用报 5 条 CS8002。.NET Core 不校验强名称，这些警告不影响加载与运行。
         根治方式：让所有 *.Linux 工程统一公开签名（把 SignAssembly/PublicSign 加进
         port-lib 生成的 PropertyGroup，或把本补丁搬进共享 props），然后删掉这条抑制。 -->
    <NoWarn>$(NoWarn);CS8002</NoWarn>
  </PropertyGroup>
'''

PATCH_E = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 E：资源管线两处保真修复（M4 资源审计，实测驱动）
       ============================================================================
       E1. 复合字体必须进 `PresentationCore.g.resources`（WPF 资源流），不是普通清单资源。
           实测事实链：
             * 上游 csproj 用的是 `<Resource Include="Fonts\\*.CompositeFont">`
               （**WPF Resource**，宏编译期由 Microsoft.WinFX.targets 打进 .g.resources）；
             * port-lib 把它搬成了 `<EmbeddedResource>`，而 `.CompositeFont` 扩展名在
               SDK 的 CreateManifestResourceNames 里类型未知 → **4 个字体一个都没进程序集**
               （实测 GetManifestResourceNames() = 3 项，无字体）；
             * 运行期 PC 的取法是
               MS/internal/FontCache/FontSource.cs:394
                 new ResourceManager($"{asm}.g", asm).GetStream($"fonts/{filename.ToLowerInvariant()}")
               即需要**名为 `<AssemblyName>.g.resources` 的流**，键为 `fonts/<小写文件名>`。
           处置：摘掉那 4 条 EmbeddedResource，改用内联任务在编译前生成
           `PresentationCore.g.resources`（ResourceWriter；键 fonts/globaluserinterface.compositefont 等）
           并以 `LogicalName=$(AssemblyName).g.resources` 嵌入。
       E2. SR 资源基名对齐：生成的 SR.g.cs 用 `new ResourceManager("PresentationCore.Resources.Strings", ...)`，
           而 port-lib 生成的 EmbeddedResource 实测被打成 `MS.Internal.Strings.resources`
           （RootNamespace=MS.Internal + 路径推导），**两者不一致** → SR 查不到资源表，
           只能靠 SR.cs 里 catch(MissingManifestResourceException) 回落到内联默认串
           （不崩，但资源/本地化全部失效）。处置：给该 resx 补 ManifestResourceName，
           使清单名精确等于 `PresentationCore.Resources.Strings.resources`。
       ============================================================================ -->
  <!-- E1 的输入：上游复合字体（只读） -->
  <ItemGroup>
    <WpfCompositeFontSource Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/Fonts/*.CompositeFont" />
  </ItemGroup>

  <UsingTask TaskName="WpfLinuxGenerateGResources" TaskFactory="RoslynCodeTaskFactory"
             AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
    <ParameterGroup>
      <Sources ParameterType="Microsoft.Build.Framework.ITaskItem[]" Required="true" />
      <OutputFile ParameterType="System.String" Required="true" />
    </ParameterGroup>
    <Task>
      <Using Namespace="System" />
      <Using Namespace="System.IO" />
      <Using Namespace="System.Resources" />
      <Code Type="Fragment" Language="cs"><![CDATA[
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
        using (var fs = File.Create(OutputFile))
        using (var writer = new ResourceWriter(fs))
        {
            foreach (var item in Sources)
            {
                string key = "fonts/" + Path.GetFileName(item.ItemSpec).ToLowerInvariant();
                // 必须以 **Stream** 形式写入：运行期 PC 走的是
                // ResourceManager.GetStream("fonts/...")，条目若是 byte[] 会抛
                // InvalidOperationException —— 与 .g.resources 里 WPF 自身
                // ResourcesGenerator 的写法一致（Application.GetResourceStream 同理）。
                writer.AddResource(key, new MemoryStream(File.ReadAllBytes(item.ItemSpec), writable: false));
            }
        }
      ]]></Code>
    </Task>
  </UsingTask>

  <Target Name="WpfLinux_GenerateGResources" BeforeTargets="CreateManifestResourceNames;PrepareResources">
    <!-- IntermediateOutputPath 由 Sdk.targets 定义，只能在 Target 内取（顶层取到空串） -->
    <PropertyGroup>
      <WpfGResourcesFile>$(IntermediateOutputPath)$(AssemblyName).g.resources</WpfGResourcesFile>
    </PropertyGroup>
    <!-- E1a：摘掉 port-lib 搬来的 .CompositeFont 普通嵌入式资源（实测不产生任何清单项）。
         Remove/Update 带元数据条件必须写在 Target 内（ItemGroup 顶层不允许 %(Extension) 批处理）。 -->
    <ItemGroup>
      <!-- 用显式 spec 移除（实测：带元数据条件的 Remove 在 Target 内不生效） -->
      <EmbeddedResource Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/Fonts/GlobalMonospace.CompositeFont;
                                $(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/Fonts/GlobalSansSerif.CompositeFont;
                                $(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/Fonts/GlobalSerif.CompositeFont;
                                $(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/Fonts/GlobalUserInterface.CompositeFont" />
      <!-- E2：SR 资源基名对齐（SR.g.cs 期望 PresentationCore.Resources.Strings） -->
      <EmbeddedResource Update="@(EmbeddedResource)"
                        Condition="'%(EmbeddedResource.Filename)%(EmbeddedResource.Extension)' == 'Strings.resx'">
        <ManifestResourceName>$(AssemblyName).Resources.Strings</ManifestResourceName>
      </EmbeddedResource>
    </ItemGroup>
    <!-- E1b：生成 WPF 资源流并以 LogicalName 嵌入 -->
    <WpfLinuxGenerateGResources Sources="@(WpfCompositeFontSource)" OutputFile="$(WpfGResourcesFile)" />
    <ItemGroup>
      <!-- 必须带 Type=Non-Resx + WithCulture=false：SDK 的
           ManifestNonResxWithNoCultureOnDisk（Microsoft.Common.CurrentVersion.targets:3512）
           只收这两条元数据齐全的项；缺了就会被静默丢弃
           —— 这正是 port-lib 搬来的 .CompositeFont 一个都没进程序集的原因（实测）。 -->
      <EmbeddedResource Include="$(WpfGResourcesFile)" LogicalName="$(AssemblyName).g.resources"
                        Type="Non-Resx" WithCulture="false" />
    </ItemGroup>
    <Message Importance="high" Text="WPF-on-Linux: 生成 $(WpfGResourcesFile)（@(WpfCompositeFontSource->Count()) 个复合字体 → fonts/*）" />
  </Target>
'''

PATCH_D = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 D：接上 DirectWriteForwarder.Linux（上游 C++/CLI 的托管等价物）
       ============================================================================
       上游 PresentationCore.csproj 有一个 <ProjectReference> 指向
       DirectWriteForwarder.vcxproj（C++/CLI）。port-lib 对 .vcxproj 一律丢弃，于是
       MS.Internal.Text.TextInterface.* / MS.Internal.Span / GlyphOffset 等托管类型在
       Linux 上整体缺失 —— 实测是 PresentationCore 0 错的唯一成规模阻塞（102 条错误，
       见 docs/U2-PresentationCore-prep.md §5 阻塞 #1）。
       处置：src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder 按上游树内 CPP/DWriteWrapper/*.h 落地这些
       类型（纯算术/枚举映射真实现，依赖 DWrite 的部分抛 PlatformNotSupportedException），
       产物与本工程同一把公钥公开签名，并声明
       [InternalsVisibleTo("PresentationCore", <WCP>)] → internal 类型可见
       （与上游 C++/CLI 的 IVT 等价）。
       ============================================================================ -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWriteForwarder.dll')">
    <Reference Include="DirectWriteForwarder">
      <HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWriteForwarder.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
'''


PATCH_F = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 F：接上 DirectWrite.Linux.Provider（T2 · Skia/FreeType 字体实现）
       ============================================================================
       T2 把 DirectWriteForwarder 托管面里 A/B/C 档共 47 条 PlatformNotSupportedException
       占位换成了对 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider 的委托（字体解析/匹配/度量/字形索引/
       字形度量与 advance/OpenType 表访问）。分类与证据：src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/PNSE-INVENTORY.md。
       副作用是本工程必须能看见 Provider 程序集：DWF 的类型上现在有参数类型来自 Provider 的
       构造重载（FontFile/FontFace/FontCollection），C# 绑定 `new FontFace(ptr)` 时会检查
       整个重载集 → 未引用即 CS0012（实测 4 条：Factory.cs:162/227/285/321）。
       ============================================================================ -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWrite.Linux.Provider.dll')">
    <Reference Include="DirectWrite.Linux.Provider">
      <HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWrite.Linux.Provider.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
'''


PATCH_G = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 G：Linux 版 Factory（替换原生工厂路径）+ 字体面令牌桥（T2）
       ============================================================================
       上游 Factory.cs 的 6 处 `_factory.Value->…` 在 Linux 上没有落点（dwrite.dll 不存在，
       函数指针恒 null）。这里把它整体移除，换成
       src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.Factory.Linux.cs —— 同命名空间、同类型名、同 internal 面，
       实现全部落到 T2 的 provider（字体解析/匹配/度量/字形索引/表访问）。
       FontBridge 是 [ModuleInitializer] 安装的令牌桥（不占用 Win32ShimResolver 的
       DllImportResolver 槽位：ModuleInitializer 可以有多个，SetDllImportResolver 只能一次）。
       ============================================================================ -->
  <ItemGroup>
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/Text/TextInterface/Factory.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.Factory.Linux.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.FontBridge.cs" />
  </ItemGroup>
'''


def main():
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()

    # 幂等：先摘掉上一次注入的整块
    if BEGIN in text:
        i = text.index(BEGIN)
        j = text.index(END) + len(END) + 1
        text = text[:i] + text[j:]

    if MARKER not in text:
        print("[失败] csproj 里找不到 Sdk.targets import 锚点，请检查生成物")
        return 1

    # ⚠ 补丁 E（资源管线：复合字体 → .g.resources、SR 清单名对齐）已于 2026-09-10 **退役**：
    #   port-lib 现在原生就做这两件事（`<Resource>` → `WpfLinuxResourceSource` + `WpfLinux_GenerateGResources`
    #   目标；`EmbeddedResource` 一律显式 `ManifestResourceName`）。两套同时注入会定义两个同名
    #   `WpfLinuxGenerateGResources` 任务与两个同名 Target，补丁 E 那份传进来的条目没有 `WpfLinuxKey`
    #   元数据 → 空键重复 → `MSB4018 ArgumentException: An item with the same key has already been added. Key: `。
    #   故此处只注入 B（签名）、D（DirectWriteForwarder 引用）、F（T2 的 Provider 引用）
    #   与 G（T2 的 Linux 版 Factory + 令牌桥）。
    block = BEGIN + "\n" + PATCH_B + "\n" + PATCH_D + "\n" + PATCH_F + "\n" + PATCH_G + END + "\n"
    text = text.replace(MARKER, block + MARKER)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)

    root = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
    _sbc = os.path.join(root, "src", "Linux", "build", "SelfBuiltConfig.props")
    try:
        cfg = re.search(r"<WpfLinuxSelfBuiltConfiguration[^>]*>([^<]*)<",
                        open(_sbc, encoding="utf-8").read()).group(1).strip()
    except (OSError, AttributeError):
        cfg = "<声明解析失败>"
    dwf = os.path.join(root, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWriteForwarder", "bin", cfg, "DirectWriteForwarder.dll")
    print(f"[OK] 已注入补丁 B（PublicSign）+ D（DirectWriteForwarder 引用）"
          f"+ F（DirectWrite.Linux.Provider 引用）+ G（Linux 版 Factory + 令牌桥）；"
          f"补丁 E 已退役（port-lib 原生覆盖资源管线）→ {CSPROJ}")
    if not os.path.exists(dwf):
        print(f"[注意] {dwf} 尚不存在 → 补丁 D 的 ItemGroup 条件不满足；"
              f"请先构建 src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/DirectWriteForwarder.Linux.csproj")

    provider = os.path.join(root, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "Provider", "bin", cfg,
                            "DirectWrite.Linux.Provider.dll")
    if not os.path.exists(provider):
        print(f"[注意] {provider} 尚不存在 → 补丁 F 的 ItemGroup 条件不满足；"
              f"请先构建 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/DirectWrite.Linux.Provider.csproj")
    return 0


if __name__ == "__main__":
    sys.exit(main())
