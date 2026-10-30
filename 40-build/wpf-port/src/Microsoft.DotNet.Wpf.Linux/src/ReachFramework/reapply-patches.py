#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ReachFramework.Linux.csproj 生成后补丁（可重复执行、幂等）。

背景：src/Linux/build/port-lib.py 每次运行都会**整份重写** src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/
ReachFramework.Linux.csproj，因此手工补丁必须可重放。重跑 port-lib 之后执行：

    python3 src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/reapply-patches.py

补丁清单
--------
A. 公开签名（PublicSign）+ WCP 公钥
   ReachFramework 是 WindowsBase / PresentationCore / System.Printing 的 IVT 受益方：
     WindowsBase/OtherAssemblyAttrs.cs:26   [assembly: InternalsVisibleTo(BuildInfo.ReachFramework)]
     System.Printing/ref/System.Printing.internals.cs:9
                                            [assembly: InternalsVisibleTo(Microsoft.Internal.BuildInfo.ReachFramework)]
   两者都用 WCP 公钥（Shared/RefAssemblyAttrs.cs 的 WCP_PUBLIC_KEY_STRING，
   实测与 src/Linux/build/keys/WcpPublicKey.snk 逐字节相同）。不签名实测 CS0281（友元公钥不匹配）
   并连带 CS0122 / CS0538 等。
B. 程序集级 [assembly: CLSCompliant(false)]
   同 PresentationFramework：上游由 Arcade 生成 AssemblyInfo 时补上；本移植工程切断 Arcade
   继承后缺失，成员级 [CLSCompliant(false)] 会报 CS3021。取值 false 经 PresentationFramework
   轮实测二选一确定（true 会新增数百条 CS3001/CS3003）。
C. PresentationCore 之外的两个本地引用（CycleBreakers 缺目录 → 见文档 §CycleBreakers）
   上游 ReachFramework.csproj:351/352 引用
     $(WpfCycleBreakersDir)PresentationFramework\PresentationFramework-ReachFramework-impl-cycle.csproj
     $(WpfSourceDir)System.Printing\ref\System.Printing-ref.csproj
   —— 前者所在的 CycleBreakers 目录**在本上游快照中不存在**；后者被 port-lib 的 `-ref` 规则跳过。
   本补丁在产物存在时把它们显式接回来（不存在则自动不接，不掩盖缺产物的真实错误）。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(HERE, "ReachFramework.Linux.csproj")
MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN = "  <!-- ==== WPF-on-Linux 补丁开关：以下内容由 reapply-patches.py 追加 ==== -->"
END = "  <!-- ==== WPF-on-Linux 补丁结束 ==== -->"

PATCH_A = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 A：公开签名（PublicSign），通过 WindowsBase/PresentationCore/
       System.Printing 的 IVT 检查
       ============================================================================
       根因同 PresentationFramework：三家都用 BuildInfo.ReachFramework
       （Shared/RefAssemblyAttrs.cs：`$"ReachFramework, PublicKey={WCP_PUBLIC_KEY_STRING}"`）
       授予友元；不签名实测 CS0281 并连带 CS0122/CS0538。
       ============================================================================ -->
  <PropertyGroup>
    <SignAssembly>true</SignAssembly>
    <PublicSign>true</PublicSign>
    <AssemblyOriginatorKeyFile>$(WpfLinuxRoot)src/Linux/build/keys/WcpPublicKey.snk</AssemblyOriginatorKeyFile>
    <!-- CS8002：本工程公开签名（身份口径）而 WindowsBase 在本移植工程中未签名；
         上游是「全签名」，故看不到这条警告。压制以保证 0 警门禁口径一致。 -->
    <NoWarn>$(NoWarn);CS8002</NoWarn>
    <!-- 上游 ReachFramework.csproj:9 就有这一条，port-lib 未搬运。
         pass 2（引用真 PF）时 deps.json 生成会抛
           MSB4018 System.ArgumentException: An item with the same key has already been added. Key: ReachFramework
         （同一个程序集同时出现在项目输出与 reference copy-local 列表里）→ 按上游关闭 deps 生成。 -->
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
'''

PATCH_B = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 B：程序集级 [assembly: CLSCompliant(false)]
       ============================================================================
       上游由 Arcade 生成 AssemblyInfo 时补上；本移植工程切断 Arcade 继承后缺失 →
       成员级 [CLSCompliant(false)] 报 CS3021。取值 false 经实测二选一确定
       （true 会新增数百条 CS3001/CS3003，见 docs/U2-PresentationFramework-prep.md §4.2）。
       ============================================================================ -->
  <ItemGroup>
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/ReachFramework.AssemblyAttrs.Shim.cs" />
  </ItemGroup>
'''

PATCH_C = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 C：接回 port-lib 丢弃的两个本地引用
       ============================================================================
       ① PresentationFramework-ReachFramework-impl-cycle：上游 CycleBreakers 目录在
          本快照中**不存在**（`find upstream/wpf -iname "*impl-cycle*"` 为空）→ 按最小 stub
          口径自建（保留 API 形态，见 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationFramework/）。
       ② System.Printing-ref：port-lib 的 `-ref` 规则跳过，但它是 System.Printing 的
          **唯一托管面**（实现是 C++）→ 由 src/Microsoft.DotNet.Wpf.Linux/src/System.Printing 产出 System.Printing.dll。
       Exists() 条件保证缺产物时不静默掩盖，而是留下真实编译错误。
       ============================================================================ -->
  <!-- ① PresentationFramework：**自举（bootstrap）两步**
       pass 1：真 PF 尚不存在 → 用 CycleStub.PresentationFramework（编译期替身，<Private>false</Private>）。
       pass 2：真 PF 产出后，本条件自动改指真 PF → 再编一遍 RF，由编译器逐条复核
               「RF 对替身的全部使用在真 PF 上依然成立」（这是替身保真度的硬验证：
                任何成员签名/继承链差异都会变成编译错误，不会留到运行期）。
       两步都是同一条命令：python3 src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/reapply-patches.py && dotnet build … -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationFramework.dll')">
    <Reference Include="PresentationFramework"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationFramework.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>
  <ItemGroup Condition="!Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationFramework.dll') and Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationFramework/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationFramework.dll')">
    <Reference Include="PresentationFramework"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationFramework/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationFramework.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/System.Printing/bin/$(WpfLinuxSelfBuiltConfiguration)/System.Printing.dll')">
    <Reference Include="System.Printing"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/System.Printing/bin/$(WpfLinuxSelfBuiltConfiguration)/System.Printing.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>
  <!-- ③ DirectWriteForwarder：上游 RF 引用 DirectWriteForwarder.vcxproj（port-lib 丢弃 vcxproj）。
       实测 RF 经 PresentationCore 的公开 API 拿到 DWrite 类型 →
       报 CS0012 "类型 Font 在未引用的程序集 DirectWriteForwarder 中定义"。
       PC 轮产出的托管等价程序集已在，直接接回。 -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWriteForwarder.dll')">
    <Reference Include="DirectWriteForwarder"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$(WpfLinuxSelfBuiltConfiguration)/DirectWriteForwarder.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>
'''


def main():
    with open(CSPROJ, encoding="utf-8") as f:
        text = f.read()

    if BEGIN in text:
        i = text.index(BEGIN)
        j = text.index(END) + len(END) + 1
        text = text[:i] + text[j:]

    if MARKER not in text:
        print("[失败] csproj 里找不到 Sdk.targets import 锚点，请检查生成物")
        return 1

    block = BEGIN + "\n" + PATCH_A + "\n" + PATCH_B + "\n" + PATCH_C + "\n" + END + "\n"
    text = text.replace(MARKER, block + MARKER)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"[OK] 已注入补丁 A/B/C → {CSPROJ}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
