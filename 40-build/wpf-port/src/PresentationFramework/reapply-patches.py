#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PresentationFramework.Linux.csproj 生成后补丁（可重复执行、幂等）。

背景：src/Linux/build/port-lib.py 每次运行都会**整份重写** src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/
PresentationFramework.Linux.csproj，因此必需的手工补丁必须有一份可重放的出处。
重跑 port-lib 之后执行：

    python3 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py

补丁清单
--------
A. 公开签名（PublicSign）+ WCP 公钥
   PresentationFramework 是 WindowsBase / PresentationCore 的 IVT 受益方：
     WindowsBase/OtherAssemblyAttrs.cs:17        InternalsVisibleTo(BuildInfo.PresentationFramework)
     PresentationCore/OtherAssemblyAttrs.cs:11   同上
   而 Shared/RefAssemblyAttrs.cs 定义
     BuildInfo.PresentationFramework = "PresentationFramework, PublicKey=<WCP 公钥>"。
   本工程不签名（公钥为空）时编译器拒绝授予友元访问：
     实测 CS0281 ×1448 行（去重 720 条唯一错误），并连带 CS0122 ×268、CS0538 ×200、CS0115 ×339。
   处置：用只含公钥的 src/Linux/build/keys/WcpPublicKey.snk 公开签名（PublicSign）。
   实测 WcpPublicKey.snk 与 WCP_PUBLIC_KEY_STRING 逐字节相同（160 字节）；
   .NET Core 运行时不校验强名称，公开签名产物在 Linux 可正常加载。

B. 接回两个被 port-lib 丢弃的本地引用（CycleBreakers / System.Printing）
   上游 PresentationFramework.csproj 引用：
     ① $(WpfCycleBreakersDir)PresentationUI\\PresentationUI-PresentationFramework-impl-cycle.csproj
        —— 提供 MS.Internal.Documents.FindToolBar。上游 CycleBreakers 目录在**本上游快照中
           不存在**（`find upstream/wpf -iname "*impl-cycle*"` 为空、$(WpfCycleBreakersDir) 无定义）
           → 结构上不可移植；按主控裁定用 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI 的最小替身顶替
           （AssemblyName=PresentationUI，保留 API 形态、不伪造行为）。
     ② $(WpfSourceDir)System.Printing\\ref\\System.Printing-ref.csproj
        —— port-lib 的 `-ref` 规则跳过它；但它是 System.Printing 的**唯一托管面**（实现是 C++），
           由 src/Microsoft.DotNet.Wpf.Linux/src/System.Printing 产出 System.Printing.dll。
   两个 Reference 都带 Exists() 条件：产物缺失时**不静默掩盖**，而是留下真实编译错误。

已从本脚本移除的两处补丁（主控已修好 port-lib，自动项生效 → 手工补丁成冗余）
--------
· 「NoWarn CA1420;WPF0001」——port-lib 现在会向上遍历上游 Directory.Build.props/Props 收集 NoWarn，
  生成物已自动含 CA1420;WPF0001（实测）。
· 「split.cur / splitopen.cur 的 Non-Resx 元数据」——port-lib 已改为 XML 解析，
  两个 .cur 的 GenerateSource/Type/ManifestResourceName/WithCulture 四项元数据实测全在。

剔除清单：**无需剔除任何源文件**（本轮实测）
--------
上游 csproj 用 <EnableDefaultItems>false</EnableDefaultItems>，port-lib 已按 csproj 原样只纳 1328 个
项目内文件（目录下 1336 个 .cs 里有 8 个上游本就没编入：7 个死文件 + ref/PresentationFramework.cs），
没有 PresentationCore 那种「默认 glob 误纳」问题。故不创建 src/Linux/build/excludes/PresentationFramework.txt。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(HERE, "PresentationFramework.Linux.csproj")
MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN = "  <!-- ==== WPF-on-Linux 补丁开关：以下内容由 reapply-patches.py 追加 ==== -->"
END = "  <!-- ==== WPF-on-Linux 补丁结束 ==== -->"

PATCH_A = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 A：公开签名（PublicSign），通过 WindowsBase/PresentationCore 的 IVT 检查
       ============================================================================
       现象（实测）：CS0281 ×1448 行（去重 720 条），报错原文——
         "友元访问权限由"WindowsBase, Version=4.0.0.1, Culture=neutral,
          PublicKeyToken=null"授予，但是输出程序集('')的公钥与授予程序中
          InternalsVisibleTo 特性指定的公钥不匹配。"
       根因：WindowsBase / PresentationCore 的 OtherAssemblyAttrs.cs 用
         BuildInfo.PresentationFramework = "PresentationFramework, PublicKey=<WCP 公钥>"
       授予友元；本工程未签名 → 全部 internal 访问被拒，并连带
       CS0122 ×268、CS0538 ×200、CS0115 ×339。
       处置：PublicSign + src/Linux/build/keys/WcpPublicKey.snk（只含公钥，非机密）。
       ============================================================================ -->
  <PropertyGroup>
    <SignAssembly>true</SignAssembly>
    <PublicSign>true</PublicSign>
    <AssemblyOriginatorKeyFile>$(WpfLinuxRoot)src/Linux/build/keys/WcpPublicKey.snk</AssemblyOriginatorKeyFile>
    <!-- 两条「本移植工程特有」的警告压制，均为**混合签名/替身基类**造成，不是源码问题：
         · CS8002 ×3：本工程（及 PC/RF/SP）公开签名，而 WindowsBase / UIAutomationTypes /
           UIAutomationProvider 在本移植工程中**未签名** —— 上游是「全签名」，故看不到该警告。
           主控已排期在集成波用统一签名根治，届时可撤。
         · CS0184 ×1（MS/Internal/documents/DocumentGridContextMenu.cs:73）：
           `DocumentViewerOwner is DocumentApplicationDocumentViewer` —— 后者的真类派生自 PF 的
           DocumentViewer，而 PresentationUI 替身不能引用 PF（编译期循环），故替身基类取 UIElement，
           编译期即可判定永假。运行期行为与编译期结论一致（该路径即 DocumentApplication 旧特性，
           在 Linux 上不启用）。真 PresentationUI 移植后基类归位，本条可撤。 -->
    <NoWarn>$(NoWarn);CS8002;CS0184</NoWarn>
    <!-- 上游 PresentationFramework.csproj:12 就有这一条，port-lib 未搬运（同一口径缺口）。
         关闭 deps.json 生成，避免「项目输出 vs reference copy-local」同名键冲突（RF pass 2 实测踩到）。 -->
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
'''

PATCH_C = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 C（`TASK-0304`/`TASK-0305`，车道 W86A）：PTS 缺口 = 具名能力边界
       ============================================================================
       把 `D-G70`（切「富文本」/「流文档」页 ⇒ `rc=134`）从"整进程必死"变成
       "**具名、可判、可见的能力边界**"。三处改动，**全部在托管侧**（native 侧是
       `src/WpfGfx.Linux.Native/src/win32_pts.c`，与本补丁同批）：

        ① `PtsCache.AcquireContextCore`（**`A2`，修 `D-G78`**）：
           `CreatePTSContext` 抛异常时，把**那条半初始化的池项从 `_contextPool` 里移除**
           （今天留着 ⇒ 下一趟布局在 `:182-189` 把它当空闲项复用 ⇒ `PtsHost.Context` 的
           `Invariant.Assert(_context != IntPtr.Zero)` 失败 ⇒ `Environment.FailFast`），
           并立**具名能力闩**（只对"PTS 能力不可用"这**一个**条件生效 ⇒ 后续 `AcquireContext`
           立即抛 `PtsUnavailableException`，不再进 native、不再扫池 ⇒ 不重试风暴）。
           ⚠️ **`Invariant.Assert` 一个都没删、没放宽** —— 目标是让它们**不再被走到**。
        ② `FlowDocumentView`（**`A3`**）：PTS 不可用时给**页级具名占位**（洋红矩形 +
           边框 + 三行说明文字），**不留空白**；且**一次失败只降级一次**（视图级闩 ⇒ 幂等）。
        ③ 两处**只读**打印（`[PTS-UNAVAILABLE]`）⇒ 失败**不许静默**。

       ⚠️ 生成物（`*.Linux.cs`）由本脚本从**上游逐字复制 + needle 校验后改写**：
          锚点找不到 / 命中数不符 ⇒ **报错退出**（绝不静默产出未打补丁的副本）。
       ⚠️ 本块**必须排在 port-lib 之后**（`port-lib.py PresentationFramework` 会整份重写 csproj）。
       ============================================================================ -->
  <ItemGroup>
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/PtsCache.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsCache.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/FlowDocumentView.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentView.Linux.cs" />
    <!-- T-A44（CONTENT-LINEVIS-BRANCH-REACH）：内容段"造行支入口"只读判别器载体 -->
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/TextParaClient.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextParaClient.Linux.cs" />
    <!-- T-A44：视觉/视口链逐跳只读判别器（[CHAIN]）—— 六个宿主 + 判别器类本体 -->
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/FlowDocumentPage.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPage.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/PtsPage.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsPage.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/PtsHelper.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsHelper.Linux.cs" />
    <!-- D-G206：PTS 能力缺口的**托管侧降级**工具（手写件，非生成） -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsDegrade.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/FigureParaClient.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FigureParaClient.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/PtsHost/ContainerParaClient.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/ContainerParaClient.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/FlowDocumentFormatter.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentFormatter.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/FlowDocumentPaginator.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPaginator.Linux.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxChainProbe.Linux.cs" />
    <!-- T-A69（PRECOND-NO-TEXT-SOURCE）：内容源入站喂料器类本体 -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxTextSrcProbe.Linux.cs" />
    <!-- T-A70（契约 C4 cp↔dcp 偏移由宿主给定）：托管侧"宿主"侧映射交付器类本体 -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxCpDcpMapProbe.Linux.cs" />
    <!-- T-A71（PRECOND-NO-TEXT-PARA-IN-CHAIN）：文本段落进链交付器类本体 -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxTextParaChainProbe.Linux.cs" />
    <!-- T-A72（PRECOND-LS-SESSION-DRIVER · LS 会话进链）：会话↔段落对账交付器类本体 -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxLsSessionProbe.Linux.cs" />
    <!-- T-A73（PRECOND-NO-LINE-BREAKER · 行断器）：行断器触发器类本体 -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxLineBreakProbe.Linux.cs" />
    <!-- T-B7（BAML-TYPE-UNRESOLVED）：BAML 类型解析失败的**具名出口**（只补类型名/宿主件，不改语义） -->
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Markup/Baml2006/Baml2006SchemaContext.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Baml2006SchemaContext.Linux.cs" />
    <!-- T-B11（PAGEVIEW-ONSCREEN）：分页视觉宿主（DocumentPageView／DocumentPageHost）的「上屏」接线 ＋ 只读台账 -->
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/DocumentPageView.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageView.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/DocumentPageHost.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageHost.Linux.cs" />
    <!-- T-B12（PAGINATED-PAGE-CONTENT-VISUALS）：分页页视觉「壳内」内容视觉的只读逐跳读数（[PAGEVIS]） -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxPageVisProbe.Linux.cs" />
    <!-- T-B13（PRECOND-TAB3-PAGE-HOST）：FlowDocumentReader 内部页宿主（ReaderPageViewer 的模板）可达接线 ＋ 只读台账 -->
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/FlowDocumentReader.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentReader.Linux.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxReaderPageHost.Linux.cs" />
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/DocumentViewerBase.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentViewerBase.Linux.cs" />
    <!-- T-B17（PRECOND-TAB3-EHANDLE-CALLSITE）：E_HANDLE 抛点的只读捕获器（FirstChanceException） -->
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxEHandleProbe.Linux.cs" />
  </ItemGroup>
'''

PATCH_B = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 B：接回两个 port-lib 丢弃的本地引用
       ============================================================================
       ① PresentationUI（CycleBreakers 替身）：提供 MS.Internal.Documents.FindToolBar。
          上游 CycleBreakers 目录在本上游快照中不存在（$(WpfCycleBreakersDir) 无定义）
          → 结构上不可移植；用 src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI 的最小替身顶替
          （见该工程头部注释：API 形态保留、行为不伪造）。
          <Private>true</Private>：替身即当前运行期的 PresentationUI，需随 PF 进输出目录。
       ② System.Printing：port-lib 的 `-ref` 规则跳过了 System.Printing-ref.csproj，
          而它是 System.Printing 的**唯一托管面**（实现是 C++）→ src/Microsoft.DotNet.Wpf.Linux/src/System.Printing 产出。
       两者都带 Exists() 条件，产物缺失时留下真实编译错误（不静默掩盖）。
       ============================================================================ -->
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationUI.dll')">
    <Reference Include="PresentationUI"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI/bin/$(WpfLinuxSelfBuiltConfiguration)/PresentationUI.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>
  <ItemGroup Condition="Exists('$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/System.Printing/bin/$(WpfLinuxSelfBuiltConfiguration)/System.Printing.dll')">
    <Reference Include="System.Printing"><HintPath>$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/System.Printing/bin/$(WpfLinuxSelfBuiltConfiguration)/System.Printing.dll</HintPath><Private>true</Private></Reference>
  </ItemGroup>
'''


# ══════════════════════════════════════════════════════════════════════════════
#  补丁 C 的**生成物**（`TASK-0304`/`TASK-0305`，车道 W86A）
#
#  【为什么生成物由本脚本产出，而不是新写一个 src/Linux/tools/patch-*.py】
#    PF 这个工程的**唯一**持久注入面就是本脚本：`src/Linux/build/integration-wave.sh:124-126` 在
#    「port-lib 重生成」之后**必定重放** `src/Linux/build/<Proj>.Linux/reapply-patches.py`；而
#    `src/Linux/build/port-lib.py PresentationFramework` 会**整份重写 csproj**（把别人的接线抹掉）。
#    ⇒ 只有挂在这里的接线与生成物能**活过每一波**。本件写域限定 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/**`，
#      所以生成器就放在这里，**不改** `src/WpfGfx.Linux.Native/tools/**`（那是别人的写域）。
#
#  【纪律（与 `src/Linux/tools/patch-presentationframework-*.py` 同一套）】
#    · 每次从**上游**重读，逐字复制 + needle 校验后改写；
#    · needle 命中数不符 ⇒ **报错退出**（rc≠0），绝不静默产出"没打上补丁的副本"；
#    · 生成物头部写明"由谁生成、上游是哪一份、改了哪几处"，**不许手改**。
# ══════════════════════════════════════════════════════════════════════════════

UP_PF = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/"
REPO = os.path.normpath(os.path.join(HERE, "..", "..", "..", ".."))

DERIVED_HEADER = """// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `{up}` 逐字复制 + {n} 处 W86A（`TASK-0304`/`TASK-0305`）改动。
// 每次运行该脚本都会从上游重读重生成；needle 找不到 / 命中数不符时**报错退出**
// （不会静默产出未打补丁的副本）。改动逐处见：
//   {edits}
//
// 背景（`D-G70`/`D-G78`）：本移植没有 PTS/原生 LineServices ⇒ 切「富文本」/「流文档」页
// 曾**整进程 `rc=134`**。本件把它变成「**具名、可判、可见的能力边界**」：
//   · native 侧新增 `src/WpfGfx.Linux.Native/src/win32_pts.c`（6 个入口导出、**如实返回非零 LsErr**、
//     打具名台账 `PTS_GAP entry=… seq=… err=-10000`）；
//   · 本文件（托管侧）负责：**拆掉毒池项**（`D-G78` 的根因）＋ **具名能力闩** ＋ **页级可见降级**。
// ⚠️ `Invariant.Assert` **一个都没删、没放宽** —— 目标是让它们**不再被走到**；
//    若仍被走到，断言照旧响亮（那是新缺陷）。
"""


def _write_atomic(path, text):
    """`temp + rename` 落盘（同目录、同一文件系统 ⇒ `os.replace` 原子）。

    ⏪ `T-A41`：本生成器**所有**写盘路径都走这里 ⇒ 生成件永远不会以"半个文件"的形态留在盘上。
    """
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def _apply_edits(upstream_rel, out_name, edits):
    """从上游重读、逐处 needle 替换、写生成物。needle 命中数不符 ⇒ 抛异常（不静默）。

    ⚠️【2026-10-05 修·结构上游化落点】`upstream_rel` 的**调用方**（`UP_PF`／`DPV_UP`／
       `DPH_UP`／`FDR_UP`／`DVBI_UP`／`CHAIN_FILES`）给的都是**仓根相对**路径
       （`src/Microsoft.DotNet.Wpf/src/PresentationFramework/…`）。R4 把这里的拼接从
       `REPO + "upstream/wpf"` 改成 `REPO + "src/Microsoft.DotNet.Wpf"` 时**没有同时去掉
       调用方的前缀** ⇒ 路径变成
       `…/src/Microsoft.DotNet.Wpf/src/Microsoft.DotNet.Wpf/src/PresentationFramework/…`
       ⇒ `materialize_derived()` 首件即抛 `上游文件不存在` ⇒ `PresentationFramework` 的
       派生源整批不生成（波的第 2 步 `[失败]`）⇒ 它的 csproj 拿不到补丁块（CycleStub/
       System.Printing 引用丢失）⇒ 第 3 步 `CS0246 PrintQueue/FindToolBar`。
       本件与 `upstream_rel` 的**唯一口径**对齐：调用方给仓根相对路径，这里只做 REPO 拼接。"""
    up_abs = os.path.join(REPO, upstream_rel)
    if not os.path.isfile(up_abs):
        raise RuntimeError("上游文件不存在：%s" % up_abs)
    with open(up_abs, encoding="utf-8-sig") as f:
        text = f.read()
    for i, (needle, repl, expect) in enumerate(edits, 1):
        got = text.count(needle)
        if got != expect:
            raise RuntimeError(
                "%s：第 %d 处 needle 命中 %d 次（期望 %d）—— 上游变了或本脚本过期，**拒绝产出**"
                % (out_name, i, got, expect))
        text = text.replace(needle, repl, expect)
    names = " ／ ".join("E%d" % i for i in range(1, len(edits) + 1))
    out_abs = os.path.join(HERE, out_name)
    # ⏪ `T-A41`：落盘一律 **temp + rename**（本仓纪律：写盘原子化 ⇒ 半个文件不会留在盘上；
    #    `T-A39`/`T-A37` 报告里"生成件 temp+rename"的口径由此**在生成器内**成立，不靠调用者）。
    _write_atomic(out_abs, DERIVED_HEADER.format(up=upstream_rel, n=len(edits), edits=names)
                  + text)
    return out_abs, len(edits)


# ── PtsCache.Linux.cs 的三处改动 ──────────────────────────────────────────────
PTSCACHE_E1_NEEDLE = """        private List<ContextDesc> _contextPool;
"""
PTSCACHE_E1_REPL = """        private List<ContextDesc> _contextPool;

        // ==================================================================
        //  W86A `A2`（`D-G78`）：**具名能力闩**
        //  null = "还没失败过"；非 null = "PTS 能力在本进程内已判定不可用"。
        //  只由 AcquireContextCore 的 catch 置位、只被同一个方法的入口读
        //  ⇒ 作用域**刻意收窄**：它**不是**兜底 catch-all，**不**覆盖任何别的失败族
        //    （判据见 `WpfLinuxPtsGap.IsPtsUnavailable`：必须由 native 台账亲口作证）。
        // ==================================================================
        private PtsUnavailableException _ptsUnavailable;
"""

PTSCACHE_E2_NEEDLE = """            int index;

            // Look for the first free PTS Context.
"""
PTSCACHE_E2_REPL = """            int index;

            // ── W86A `A2`：具名能力闩（**只对"PTS 能力不可用"这一个条件生效**）──────────
            //   第一次失败之后，后续布局**不再进 native、不再扫池**：
            //   否则每次 Measure 都抛一次 ⇒ 布局重入 / CPU 打转（W78A §3.4 风险 2）。
            //   ⚠️ 这里**不吞**任何东西：抛的是**具名**异常，谁都能按类型接住并计数。
            if (_ptsUnavailable != null)
            {
                // 每次抛**新的**实例（Stack 指向"这次是谁来问的"），首次失败挂在 InnerException 上
                // —— 抛同一个旧实例会让栈指向**第一次**的调用点，那是误导性读数。
                throw new PtsUnavailableException(
                    _ptsUnavailable.Entry, _ptsUnavailable.ErrorCode,
                    "PTS 能力已在本进程内判定不可用（首次失败见 InnerException）—— 本闩只覆盖这一个具名条件",
                    _ptsUnavailable);
            }

            // Look for the first free PTS Context.
"""

PTSCACHE_E3_NEEDLE = """#pragma warning disable IDE0017
            // Create new PTS Context, if cannot find free one.
            if (index == _contextPool.Count)
            {
                _contextPool.Add(new ContextDesc());
                _contextPool[index].IsOptimalParagraphEnabled = ptsContext.IsOptimalParagraphEnabled;
                _contextPool[index].PtsHost = new PtsHost();
                _contextPool[index].PtsHost.Context = CreatePTSContext(index, textFormattingMode);
            }
#pragma warning restore IDE0017
"""
PTSCACHE_E3_REPL = """#pragma warning disable IDE0017
            // Create new PTS Context, if cannot find free one.
            if (index == _contextPool.Count)
            {
                // W86A `A2`：native 侧**累计入口调用数**的快照（判缺口是不是 native 亲口说的，见 catch）
                int ptsCallsBefore = WpfLinuxPtsGap.NativeCalls();
                ContextDesc created = null;      // 本次新建的池项（**按引用**认，不读它的状态）
                try
                {
                    created = new ContextDesc();
                    _contextPool.Add(created);
                    _contextPool[index].IsOptimalParagraphEnabled = ptsContext.IsOptimalParagraphEnabled;
                    _contextPool[index].PtsHost = new PtsHost();
                    _contextPool[index].PtsHost.Context = CreatePTSContext(index, textFormattingMode);
                }
                catch (Exception e)
                {
                    // ── W86A `A2` ①：**毒池项必须离开池**（`D-G78` 的根因）────────────────
                    //   上游把池项加进去（`:195`）、再把 `CreatePTSContext` 的返回值塞进
                    //   `PtsHost.Context`（`:198`）。中间任何一步抛异常 ⇒ 池里留下一条
                    //   `InUse == false` / `PtsHost.Context == IntPtr.Zero` 的半初始化项；
                    //   下一趟布局在 `:182-189` 的循环里把它当**空闲项**复用（跳过创建），
                    //   于是后面第一次问 `PtsHost.Context` 就撞
                    //   `Invariant.Assert(_context != IntPtr.Zero)` ⇒ `Invariant.FailFast`
                    //   ⇒ **`Environment.FailFast` 不可捕获 ⇒ rc=134**。
                    //
                    //   ⚠️⚠️ **判据只许用"对象身份"，一个字都不许读 `PtsHost.Context`**：
                    //       `PtsHost.Context` 的 **getter 自己就在断言** `_context != IntPtr.Zero`
                    //       （`PtsHost.cs:62-66`）—— 半初始化项**恰好**违反它 ⇒
                    //       用 `xxx.PtsHost.Context == IntPtr.Zero` 当"是不是半初始化"的判据，
                    //       **等于让修复自己在原地触发 `FailFast`**。
                    //       【本车道实测踩到】W86A 第一版就是这么写的，实测栈：
                    //         Invariant.FailFast ← PtsHost.get_Context ← PtsCache.AcquireContextCore
                    //       即 `D-G78` 那条链**被"修法"原样复现了一遍**（读数见 W86A 报告 §3）。
                    //   ⇒ 正确判据：`created` 是**本次刚 new 出来、刚 Add 进去的那一个对象引用**
                    //       （引用相等，`List<T>.Remove(T)` 就是按引用找），根本不需要读它的状态。
                    //       若 `_contextPool.Add` 自己抛（OOM）⇒ `created == null` ⇒ 不删（安全）。
                    if (created != null)
                    {
                        // 半初始化项若已建了 LS 罚分模块，它**已被 SuppressFinalize**
                        // ⇒ 必须在这里显式释放，否则泄漏（上游只在正常拆卸路径 Dispose）。
                        // ⚠️ `TextPenaltyModule` 是**字段**（不是属性）⇒ 读它不会触发任何断言。
                        created.TextPenaltyModule?.Dispose();
                        // ── 格 1（`TASK-0302`／`t12`）：installed-objects 现在**真的是**本地对象 ──────
                        //   格 0 时 `CreateInstalledObjectsInfo` 恒失败 ⇒ `InstalledObjects` 恒 `IntPtr.Zero`
                        //   ⇒ 这条 catch **没有东西可漏**。格 1 让它真的分配之后，上游**唯一**的释放口
                        //   （`DestroyPTSContexts`，`PtsCache.cs:331-332`）**被这条 catch 绕过了**
                        //   ⇒ 不补这一句就是"每失败一次漏一块"（native 侧 `installed_objects_live` 看得见）。
                        //   ⚠️ `InstalledObjects` 是**字段**（`PtsCache.cs:793 internal IntPtr InstalledObjects;`）
                        //      ⇒ 读它**不触发任何断言**（与 `PtsHost.Context` 那条 getter 断言不同，见上）。
                        if (created.InstalledObjects != IntPtr.Zero)
                            PTS.IgnoreError(PTS.DestroyInstalledObjectsInfo(created.InstalledObjects));
                        _contextPool.Remove(created);
                    }

                    // ── W86A `A2` ②：**具名能力闩**（失败**只**对"PTS 缺口"这一族立闩）──────
                    if (WpfLinuxPtsGap.IsPtsUnavailable(e, ptsCallsBefore))
                    {
                        _ptsUnavailable = WpfLinuxPtsGap.Describe(e);
                        throw _ptsUnavailable;
                    }
                    throw;          // 其它异常**原样传播**（不吞、不改名、不立闩）
                }
            }
#pragma warning restore IDE0017
"""

PTSCACHE_E0_NEEDLE = """using System.Threading;                         // Interlocked
"""
PTSCACHE_E0_REPL = """using System.Runtime.InteropServices;           // DllImport / 具名缺口台账（W86A）
using System.Threading;                         // Interlocked

using DllImport = MS.Internal.PresentationFramework.DllImport;
"""

PTSCACHE_E4_NEEDLE = '''        #endregion Private Types
    }
}
'''
PTSCACHE_E4_REPL = '''        #endregion Private Types
    }

    /// <summary>
    /// W86A（`TASK-0304`/`TASK-0305`）：**PTS 能力不可用**的具名异常。
    ///
    /// 【为什么需要一个新类型】上游把"PTS 建不起来"表达成好几种形态：
    ///   `EntryPointNotFoundException`（今天：符号根本不在 shim 里）、`DllNotFoundException`、
    ///   以及 A1 落地后的私有嵌套类型 `PTS.PtsException`（非零 `LsErr` ⇒ `PTS.Validate` 抛）。
    /// 其中 `PtsException` 是**上游的 private 嵌套类**（`Pts.cs:299`）⇒ 本工程**无法按类型引用它**。
    /// 于是今天**没有任何调用方**能只按类型说"这一页不支持"，只能去匹配消息串（本仓最忌讳）。
    /// 本类型把那个条件**命名**：谁接住 `PtsUnavailableException`，谁就是在处理"PTS 缺口"。
    /// </summary>
    internal sealed class PtsUnavailableException : Exception
    {
        internal PtsUnavailableException(string entry, int errorCode, string message, Exception inner)
            : base(message, inner)
        {
            Entry = entry;
            ErrorCode = errorCode;
        }

        /// <summary>缺口入口名（来自 native 侧的具名台账；取不到时是 "unknown"）。</summary>
        internal string Entry { get; }

        /// <summary>native 交回的 LsErr（A1 的 stub 恒为 -10000 = `tserrNotImplemented`）。</summary>
        internal int ErrorCode { get; }
    }

    /// <summary>
    /// W86A：把 native 侧的**具名缺口台账**接进托管侧的两条判据。
    /// 只做三件事：① 判"这次失败是不是 PTS 缺口"；② 把缺口**具名**；③ 打具名行。
    /// ⚠️ 它**不**决定任何降级动作，也**不**吞异常。
    ///
    /// 【判据为什么不是"异常类型表"】A1 落地后真正的异常是 `PTS.PtsException`（private 嵌套类，
    /// 引用不到）。所以判据改成**native 自己作证**：
    ///   `WpfLinuxWin32_PtsGapCalls()` 是 native 侧的**累计入口调用数**（`win32_pts.c` 的 `g_pts_seq`）。
    ///   在 `CreatePTSContext` 之前取一次快照、在 catch 里再取一次：
    ///     **涨了** ⟺ 这一次尝试真的走到过那 6 个入口 ⟺ 缺口是**原生侧亲口说的**
    ///   （不是我们猜的）。没涨 ⇒ 这个异常**与 PTS 缺口无关** ⇒ 不立闩、不改名、原样传播。
    ///   ⚠️ 旧 shim（没有这两个导出）下本函数返回 0 ⇒ 判据退化成"只认那三个 DllImport 异常族"，
    ///      而那时真正的失败恰好就是 `EntryPointNotFoundException` ⇒ 照样命中（不依赖新 shim）。
    /// </summary>
    internal static class WpfLinuxPtsGap
    {
        // ── 与 native 侧同名同形（`src/WpfGfx.Linux.Native/src/win32_pts.c`）──────────
        // `int WpfLinuxWin32_PtsGapCalls(void);`
        // `int WpfLinuxWin32_PtsGapReport(char *buf, int cap);` —— 写一行机读摘要，返回长度。
        // ⚠️ 形参用 `byte[]`（blittable）：**不用 Marshal**，也就不需要额外的 using；
        //    并且**每一次调用都包在 try 里** —— 旧 shim 没有这些导出 ⇒ `EntryPointNotFoundException`，
        //    绝不能让它盖掉真正的失败（"具名"是锦上添花，不是判据的前提）。
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsGapCalls", ExactSpelling = true)]
        private static extern int PtsGapCallsNative();

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsGapReport",
                   CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int PtsGapReportNative([Out] byte[] buf, int cap);

        // ⏪ `t87` 加：**缺口名册的专用读口**（native 侧现取 `win32_pts.c`：`PtsGapCount()` ＝ `g_pts_calls[i] > 0`
        //   的条数；`PtsGapEntryName(idx, buf, cap)` ＝ **按在册表序**第 `idx` 个「有缺口计数」的入口名，成功 `1`／无此 idx `0`）。
        //   ⚠️ 这两个导出在**旧 shim** 上没有 ⇒ 每次调用都包在 `try` 里（与上面同款：具名是锦上添花，不许盖掉真失败）。
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsGapCount", ExactSpelling = true)]
        private static extern int PtsGapCountNative();

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsGapEntryName",
                   CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int PtsGapEntryNameNative(int idx, [Out] byte[] buf, int cap);

        /// <summary>native 侧累计入口调用数（快照用）。取不到 ⇒ 0。</summary>
        internal static int NativeCalls()
        {
            try { return PtsGapCallsNative(); }
            catch (Exception) { return 0; }
        }

        /// <summary>native 缺口台账里**在册表序**最后一个"有缺口计数"的入口名；取不到 ⇒ "unknown"（**不抛**）。</summary>
        // ⏪ `t90`（2026-09-29）**注释-实现对齐**（`t88` 的 `F-4`；原注释写"最近一条缺口"＝**最近调用**，与实现不符）：
        //   现取 native 原文（`win32_pts.c`，行号仅本次有效）：`WpfLinuxWin32_PtsGapEntryName()` ＝ `for (i = 0; i < COUNT; i++)
        //   if (g_pts_calls[i] <= 0) continue; …` ⇒ **按 `k_pts_entries[]` 表序**；而 `wpf_pts_frontier()` ＝ 按 `k_pts_call_order[]`
        //   **调用序**筛 `g_pts_seen[]`。两者**都不是**"最近一次调用"（native 侧根本没有记 recency）。
        //   ⇒ 多缺口态下二者会给出**不同**名字（`t90` 现取：先调 idx7 `LoAcquirePenaltyModule`、后调 idx4 `GetFloaterHandlerInfo`
        //   ⇒ ②路/`anchor=` 给表序名，`frontier=` 给调用序名，"最近调用的"是 `GetFloaterHandlerInfo` 而 ②路给 `LoAcquirePenaltyModule`）。
        private static string NativeEntryName()
        {
            string s = NativeReport();
            // ⏪ `t87`：口径**现取**（`t81` 把 native 行压形成 `anchor=`／`frontier=`；更早的形是 `last=`）：
            //   ① 报表旧形 `last=` ＝ **缺口名册的末名**（最贴近台账 `PTS_GAP entry=<名>`）；
            //   ② **缺口名册专用读口**（`PtsGapCount()`／`PtsGapEntryName(idx)`；`idx = count-1` ＝ **在册表序最后一个有缺口计数**的入口）
            //      —— `g_pts_calls[]` 的口径**只对走 `wpf_pts_gap()` 的 stub 涨**（真实现不涨）⇒ 这才是"缺口"口径；
            //   ③ 报表 `anchor=` ＝ 在册表序**第一个有缺口计数**的入口名（同一缺口口径的另一端）；
            //   ④ 兜底 `frontier=` ＝ `g_pts_seen[]` 的**被问过**口径（真实现也算）—— **不是缺口名**，只在①②③全空时用，且此处如实注明。
            //   四者都取不到 ⇒ `unknown`（**不猜**）。
            // ⏪ `t90`：②路改走 `GapEntryNameAt()`（**带长度纪律**，见该方法的注释）—— 读口本身**可能静默截断**（`F-3`）。
            // ⏪ `t109`（2026-09-29）**`t105` 的 `F-1`（medium）＋ `P1-ptsname-result.md` §8 裁定十二补：多缺口态下不许指错人**。
            //   **现场（`t105` 现取）**：本波第一次同时有两条缺口（台账 `PTS_GAP entry=CreateDocContext seq=5` 与
            //   `entry=LoDisposePenaltyModule seq=6`），而 ②路 `idx = count-1` ⇒ **表序末名** ⇒ 托管
            //   `[PTS-UNAVAILABLE] … entry=` **两次都写 `LoDisposePenaltyModule`**、`CreateDocContext` **一次都没写**
            //   ⇒ 会把第四步靶心（`CreateDocContext`）的功劳记到别人头上。
            //   **新口径（逐字）**：托管侧改取**在册表序第一个有缺口计数的入口名**。理由是**结构事实**：
            //   `k_pts_entries[]` **本身按调用链次序排列**（installed-objects → doc-context → floater/table → Lo* 族 → dispose），
            //   故"表序首"＝**链上最早那一站**＝台账口径（`^PTS_GAP entry=` 行按 `seq=` 排序取最早）下的「**下一跳**」。
            //   多缺口时它与"表序末名"**不同**；而"最近一次缺口调用"在本现场**恰好是另一站** ⇒ 若取它，`CreateDocContext`
            //   仍永远不会被点名，`F-1` 的病**没治**。
            //   ⚠️ **限制如实记**：native 报表**不暴露 recency**（现取字段只有 `anchor=`／`frontier=`／逐条 `calls=`），
            //   且 `src/**` 不在本件写域 ⇒ 托管侧**无法**按"最近一次调用"取名；本件用的是上面那条**表序＝链序**的结构事实。
            //   取值顺序（逐字）：① 旧形 `last=` → ② **`anchor=`（表序首个有缺口计数的入口名）** → ③ **`GapEntryNameAt(0)`**
            //   （老 shim 无 `anchor=` 时同义；带长度纪律）→ ④ 表序末名 `GapEntryNameAt(count-1)`（**仅当**②③都取不到时**兜底**，
            //   保持旧行为）→ ⑤ 兜底 `frontier=`（`g_pts_seen[]` 的**被问过**口径，**不是缺口名**，此处如实注明）→ ⑥ `unknown`（**不猜**）。
            foreach (string key in new string[] { "last=" })
            {
                string nm0 = FieldOf(s, key);
                if (nm0 != null) return nm0;
            }
            foreach (string key in new string[] { "anchor=" })
            {
                string nmA = FieldOf(s, key);
                if (nmA != null) return nmA;
            }
            try
            {
                string nmF = GapEntryNameAt(0, GapNameCap);            // ③ 表序**首个**（＝链上最早那一站）
                if (nmF != null) return nmF;
                int cnt = PtsGapCountNative();
                if (cnt > 0)
                {
                    string nm1 = GapEntryNameAt(cnt - 1, GapNameCap);  // ④ 表序末名：仅兜底（旧行为）
                    if (nm1 != null) return nm1;
                }
            }
            catch (Exception) { }
            foreach (string key in new string[] { "frontier=" })
            {
                string nm2 = FieldOf(s, key);
                if (nm2 != null) return nm2;
            }
            return "unknown";
        }

        /// <summary>`<键>=<值>` 取值（`值` 到空格或串尾；`-`／`unknown`／空 ⇒ `null`；不抛）。</summary>
        private static string FieldOf(string s, string key)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = s.IndexOf(' ', i);
            string name = (j < 0) ? s.Substring(i) : s.Substring(i, j - i);
            return (string.IsNullOrEmpty(name) || name == "-" || name == "unknown") ? null : name;
        }

        /// <summary>nul 结尾字节缓冲 ⇒ 串（不抛）。</summary>
        private static string CStr(byte[] b)
        {
            int n = 0;
            while (n < b.Length && b[n] != 0) n++;
            char[] c = new char[n];
            for (int k = 0; k < n; k++) c[k] = (char)b[k];
            return new string(c);
        }

        /// <summary>缺口名册读口的缓冲长度（**写死**；只有"缓冲有富余"的读数才可信，见 `GapEntryNameAt`）。</summary>
        // ⏪ `t90` 现取：现册最长名 `LoGetPenaltyModuleInternalHandle` ＝ 31 B，《128》有 96 B 富余 ⇒ 富余判据**必真**；
        //   一旦名册出现 ≥127 B 的名字，富余判据会**保守**判"截断 ⇒ unknown"（宁可误报 unknown，不许误信截断名）。
        // ⏪ `t95`（2026-09-29）**`t91` 的 `G-4`：数字更正（逐字，实测现取）**——上一段的 `31 B`／`96 B` **写错**：
        //   现取该名长度 ＝ **32 B**（`awk` 对 `src/WpfGfx.Linux.Native/src/win32_pts.c` 的 `k_pts_entries[]` 逐名取长，
        //   `sort -rn | head -1` ⇒ `32 LoGetPenaltyModuleInternalHandle`；名册共 12 名）⇒ 128 − 32 ＝ **95 B** 富余。
        //   **结论不变**（富余判据在现册必真）；被纠正的只是数字。**原句一字未删**（只增不改），以本段为准。
        private const int GapNameCap = 128;

        /// <summary>带**长度纪律**的缺口名册读口：拿到可信名 ⇒ 该名；截断/越界/形状不合 ⇒ `null`（**不抛**）。</summary>
        // ⏪ `t90`（2026-09-29）**`t88` 的 `F-3`**：native `WpfLinuxWin32_PtsGapEntryName()` 现取原文（`win32_pts.c`，行号仅本次有效）
        //   ＝ `snprintf(buf, (size_t)cap, "%s", k_pts_entries[i]); return 1;` —— **无长度检查**：`cap` 不够时
        //   `snprintf` 静默截断，**仍然返回 1** ⇒ 调用方会拿到**貌似完整的短名**（`cap=5 ⇒ rc=1 name="LoAc"`）。
        //   这在判据面比 `unknown` **更坏**：`unknown` 是诚实的"没读到"，而截断名是一个**看起来可归因的假名**
        //   —— 判据按"名字在不在册"对拍 ⇒ 若某次截断恰好落在**名册里另一个真名**上，就会**假绿**（现状名册 12 名无
        //   前缀包含对 ⇒ 今日现实风险是"假红"而非"假绿"，但这是**名册的偶然性**，不是纪律）。
        //   **纪律（逐字）**：① 只用**富余**读数 —— `strlen(buf) < cap-1`（`snprintf` 截断时字符串长度恒为 `cap-1`
        //   ⇒ 长度等于 `cap-1` 一律**不信**，宁可保守判 `unknown`；名字恰好 `cap-1` 长时也判 `unknown`，属**收紧**）；
        //   ② 名字必须是**入口名形状**（C 标识符）—— 名册里的名字全是这种形，挡住截断产生的怪异串；
        //   ③ 两条任一不满足 ⇒ `null`（调用方回落到 ③/④ 或最终 `unknown`）—— **绝不许**返回截断名。
        //   ⚠️ native 侧的**真修法**（`t88` 建议的"长度不足返 0"）需要改 `src/**`（本件写域**不含**它）⇒ 本件只把纪律落在
        //   **托管侧读口**（判据实际消费的就是这个值），native 侧的加固与建议补丁见载体 `P1-entry-attribution-fix2-report.md`。
        //   ⏩ 事实上 `t92`（P1-W20）**已在 native 侧落地真修**：`WpfLinuxWin32_PtsGapEntryName()` 现在"放不下 ⇒ `0` ＋ 空串"
        //   （`cap ≤ 名长` ⇒ `rc=0`），只读 128/95 B 富余档 ⇒ `rc=1` 全名。
        // ⏪ `t95`（2026-09-29）**`t91` 的 `G-5`：保守边界口径句（不改实现）**。本读口的 ①富余判据是
        //   `nm.Length >= cap - 1 ⇒ null` ⇒ **`cap == 名长 + 1`（native 恰好放得下、`t92` 后也会给全名）同样被判 `null`**
        //   （`t91` 夹具实测：`cap=23 ⇒ <null>`、`cap=24` 才给名）。这是**刻意的保守**，理由三条：
        //    (i) **跨 shim 世代的安全网**：`t92` 之前的那代 `.so` 仍会"截断 ＋ `rc=1`"（`cap=5 ⇒ name="LoAc"`）⇒ 只要本读口
        //        可能落到**旧 shim** 上，`rc==1` 就**不足以**证明"拿到的是全名"；本判据不依赖 native 版本。
        //    (ii) **代价在现盘为零**：生产路 `cap=GapNameCap=128`，现册最长名 **32 B** ⇒ 富余 **95 B** ⇒"恰好放得下"这一档
        //         **在生产不可达**（只有夹具才会把 cap 调到 23/24）。
        //    (iii) 方向正确：多收一档 `null` 是**误报 unknown（收紧）**，而它换掉的是"误信截断名（假绿）"的可能。
        //   ⇒ **实现不动**（改成本该更"准"的"`rc==1` 即真名"会**失去 (i)**：对旧 shim 立刻退化回 `F-3` 的假绿灯）;
        //      若将来确认部署面**永不**加载 `t92` 之前的 shim，可另派单把 ① 放宽为"`rc==1` ⇒ 信"，届时 `cap=23/24` 两档应同值。
        internal static string GapEntryNameAt(int idx, int cap)
        {
            try
            {
                if (cap < 8) return null;                       // 连最短真名都放不下 ⇒ 不读
                byte[] nb = new byte[cap];
                if (PtsGapEntryNameNative(idx, nb, nb.Length) != 1) return null;   // 越界/无此 idx ⇒ 0
                string nm = CStr(nb);
                if (string.IsNullOrEmpty(nm)) return null;
                if (nm.Length >= cap - 1) return null;          // ① 富余判据：长度顶到 cap-1 ⇒ 可能截断 ⇒ 不信
                if (!IsEntryNameShape(nm)) return null;         // ② 形状判据
                return nm;
            }
            catch (Exception) { return null; }
        }

        /// <summary>从 DllImport 三族的异常文本里取**入口名**（取不到 ⇒ `"unknown"`；不抛）。</summary>
        // ⏪ 措辞分支 ＋ 形状校验（`t87`，2026-09-28；现场：旧实现「取最后一个单引号串＋`dll:` 前缀」**无形状校验**
        //   ⇒ 把**入口名**当成库名，产出 `dll:NotImplemented` 这种**不可归因的合成名** ⇒ 判据 `domains=unattributable` 必红）。
        //   **新口径（逐字）**：① 入口名措辞（`named 'X'`；Windows 形 `in DLL 'Y'` ／ Linux 形 `in shared library 'Y'`）
        //   ⇒ 取**入口名原样**（不加前缀），且**必须**是 C 标识符形状（挡掉 `dll:`／路径／含 `:` 的合成串）；
        //      `t95`（`G-2`）：**形状不合 ⇒ 落到②路继续看**（不再整串掐掉；形状合则先返回，入口名优先）。
        //   ② 库名措辞（`Unable to load shared library 'Y'`／`Unable to load DLL 'Y'`／`in shared library 'Y'`／`in DLL 'Y'`）
        //   ⇒ 取库名、去目录、加 `dll:` 前缀，且**只接受真库名形状**（`*.dll`／`*.so`／`*.so.<纯数字段>…`；`t90` 放宽后
        //      **不要求 `lib` 前缀**，见 `IsLibraryNameShape` 的逐字口径）；措辞命中而形状不合 ⇒ **继续扫后面三条**（`t90`，`F-2`）。
        //   ③ 其余（两条路都取不到合法形状）⇒ **`unknown`** —— **绝不许**合成不可归因名。
        private static string EntryNameFromException(Exception e)
        {
            try
            {
                if (e == null) return "unknown";
                string msg = e.Message ?? string.Empty;

                // ① 入口名措辞：`… named 'X' in DLL 'Y'`／`… named 'X' in shared library 'Y'`
                int a = msg.IndexOf("named '", StringComparison.Ordinal);
                if (a >= 0)
                {
                    int b = msg.IndexOf('\\'', a + 7);
                    if (b > a + 7)
                    {
                        string entry = msg.Substring(a + 7, b - a - 7);
                        if (IsEntryNameShape(entry)) return entry;
                    }
                    // ⏪ `t95`（2026-09-29）**`t91` 的 `G-2`：不再短路**。旧码在这里 `return "unknown"` ⇒ 与 `t90` 改过的
                    //   ②路（措辞形状不合 ⇒ `continue`）**不对称**：现场夹具 `"… named 'Lo Ac' in DLL 'x.dll'."` 里
                    //   后段 `in DLL 'x.dll'` 是**合法库名措辞**，却因前段形状不合被整串掐掉。
                    //   新口径：①路**形状不合**只说明"这条入口名措辞给不出可用名" ⇒ **落到②路**继续按库名措辞取；
                    //   ①路**形状合** 仍**先返回**（入口名优先，不被库名盖掉）；②路依旧逐条做**库名形状**校验
                    //   ⇒ 本改动**不产生**"把真入口名当库名"的通路（取回的仍是 `dll:<库名>`，或最终 `unknown`）。
                }

                // ② 库名措辞（四种措辞并列；命中即**只在真库名形状**下加前缀）
                string[] libWording = new string[]
                {
                    "Unable to load shared library '",
                    "Unable to load DLL '",
                    "in shared library '",
                    "in DLL '",
                };
                foreach (string w in libWording)
                {
                    int p = msg.IndexOf(w, StringComparison.Ordinal);
                    if (p < 0) continue;
                    int q = msg.IndexOf('\\'', p + w.Length);
                    if (q > p + w.Length)
                    {
                        string lib = msg.Substring(p + w.Length, q - p - w.Length);
                        int sl = lib.LastIndexOf('/');
                        string baseName = sl >= 0 ? lib.Substring(sl + 1) : lib;
                        if (IsLibraryNameShape(baseName)) return "dll:" + baseName;
                    }
                    // ⏪ `t90`（2026-09-29）**`t88` 的 `F-2`：不再短路**。旧码在本措辞"命中但形状不合"时直接
                    //   `return "unknown"` ⇒ **掐掉后面三条措辞**（现场：`'NotImplemented'` 在前段命中即返回，
                    //   同串后段的 `in DLL 'x.dll'` 这次**合法**归因机会被丢掉）。新口径：本措辞给不出合法库名
                    //   ⇒ 只说明**这条措辞**不作数，**继续扫后面三条**；四条全试完仍无合法库名 ⇒ 末尾统一 `unknown`。
                    //   （闭合引号缺失同理 ⇒ 继续扫，不掐整串；形状校验一格都没放松。）
                    continue;
                }
            }
            catch (Exception) { }
            return "unknown";
        }

        /// <summary>入口名形状：C 标识符（首字符字母/`_`，其余字母数字/`_`）—— 挡掉 `dll:`／路径／含 `:` 的合成串。</summary>
        private static bool IsEntryNameShape(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (int i = 1; i < s.Length; i++)
            {
                char ch = s[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            }
            return true;
        }

        /// <summary>库名形状：基名（已去目录）必须形如 `&lt;茎&gt;.dll` 或 `&lt;茎&gt;.so`／`&lt;茎&gt;.so.&lt;数字段&gt;…`。</summary>
        // ⏪ `t90`（2026-09-29）**`t88` 的 `F-1`：形状放宽到本栈真件名形**。旧码要求 `.so` 形**必须**以 `lib` 起头
        //   ⇒ 本栈真件 `wpfgfx_cor3.so`（在册证据 `evidence/five_pre_g1.txt`：`wpfgfx_cor3.so=4e25e4b27d4d5ae1`）
        //   被判 **`unknown`**，`lib.so` 亦然。新口径**逐字**：
        //   ① **字符面**：只许 `[A-Za-z0-9_.+-]`（挡掉空格／`:`／`=`／`'`／路径分隔符／控制字符）；
        //   ② `*.dll`（大小写不敏感）：茎非空（`t87` 原样，未动）；
        //   ③ `*.so`：茎非空，**不要求 `lib` 前缀**（`wpfgfx_cor3.so` ⇒ **收**）；
        //   ④ `*.so.<数字段>(.<数字段>)…`：`.so` 后**纯数字**段（`libfoo.so.1`／`libfoo.so.1.2` ⇒ 收；`libfoo.so.x`／`libfoo.so.1.beta` ⇒ 拒）；
        //   ⑤ 其余一律 **false** —— 无 `.so`／`.dll` 后缀者（`NotImplemented`／`foo.bar`／`v1.2`／`x.txt`）、
        //      茎为空者（`.so`）、`.so` 后无点者（`foo.sox`）全拒。
        //   **没有**放宽到"任何带点的串"：判据的实质是**后缀**（`.so`／`.so.<纯数字>…`／`.dll`）＋ 字符面，
        //   不是"含点即收"。形状仍然只是**形状**（不证明名字真存在于本栈）⇒ 归因仍靠判据件的在册对拍。
        // ⏪ `t95`（2026-09-29）**`t91` 的 `G-3`：点分段纪律（收紧）**。`t90` 那版只查字符面 ⇒ `a..so`（空段）、
        //   `-x.so`／`+x.so`（前导符号）、`a...so`、`x.-1.so` 全被判 **true**（与注释"茎非空"的字面不符）。
        //   **新增两条（逐字）**：⑥ 按 `.` 切分后**每段非空**（挡 `a..so`／`a...so`／`.so`）；⑦ **每段首字符必须是
        //   字母/数字/`_`**（挡 `-x.so`／`+x.so`／`x.-1.so` 这类以 `-`／`+`／`.` 起头的段）——`-`／`+` 仍**许出现在段内**
        //   （`libfoo-1.2.so`／`libstdc++.so.6` 必须继续收）。⇒ 这是**收紧**：新增拒格全是过去误收的空壳名。
        private static bool IsLibraryNameShape(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.ToLowerInvariant();
            if (t[0] == '.') return false;                                                // 茎不许以点起头（`..so` 这类空壳拒）
            for (int i = 0; i < t.Length; i++)
            {
                char ch = t[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '+' || ch == '-')) return false;
            }
            // ⑥⑦ 点分段纪律（`t95`／`G-3`）：段非空 ＋ 每段首字符 ∈ 字母/数字/`_`
            string[] parts = t.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) return false;
                char c0 = parts[i][0];
                if (!(char.IsLetterOrDigit(c0) || c0 == '_')) return false;
            }
            if (t.EndsWith(".dll", StringComparison.Ordinal)) return t.Length > 4;
            if (t.EndsWith(".so", StringComparison.Ordinal)) return t.Length > 3;          // 茎非空即可，**不要求 `lib`**
            int so = t.LastIndexOf(".so.", StringComparison.Ordinal);
            if (so > 0)
            {
                string rest = t.Substring(so + 4);                                        // `.so.` 之后的数字段
                if (rest.Length == 0) return false;
                string[] seg = rest.Split('.');
                for (int i = 0; i < seg.Length; i++)
                {
                    if (seg[i].Length == 0) return false;
                    for (int k = 0; k < seg[i].Length; k++) if (!char.IsDigit(seg[i][k])) return false;
                }
                return true;
            }
            return false;
        }

        private static int NativeError()
        {
            string s = NativeReport();
            int i = s.IndexOf("err=", StringComparison.Ordinal);
            if (i < 0) return A1_STUB_ERR;
            i += 4;
            int j = i;
            if (j < s.Length && (s[j] == '-' || s[j] == '+')) j++;
            while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
            return (j > i + 1) && int.TryParse(s.Substring(i, j - i), out int v) ? v : A1_STUB_ERR;
        }

        private static string NativeReport()
        {
            try
            {
                // ⏪ 长度纪律（`t87`，2026-09-28；现场：`t81` 让台账有行后本串变长 ⇒ 旧码定长 `byte[256]` ⇒ 整条读不到，
                //   `NativeEntryName()` 退回 `unknown` ⇒ `Describe()` 只能去异常文本里取数 ⇒ 那一路又产出过合成名）。
                //   native 侧**返回约定**（现取原文，`src/WpfGfx.Linux.Native/src/win32_pts.c`）：
                //     `if (n < 0 || n >= cap) { buf[cap - 1] = '\\0'; return -1; }` ／ `return n;`
                //   ⇒ `>0` ＝ **写入长度**；`-1` ＝ **写不下**（**不截断、不静默**，`buf` 已被终结）
                //   ⇒ 读不全的**唯一信号是 `-1`** ⇒ **放大缓冲重试**（`256 → 4 KiB → 64 KiB`，上限写死 `64 KiB`）。
                // ⏪ `t90`（2026-09-29）**`t88` 的 `O-1` 口径句**：`-1` 那一支 native **已经把 `buf[cap-1] = '\\0'` 写掉**
                //   ⇒ "写不下"时 `buf` 里是一条**被截断的、以 nul 结尾的行**（不是空串！现取夹具：`cap=250/251/256`
                //   ⇒ 末字节均 0）。⇒ **忽略 `rc` 的读者会读到一条貌似完整的短行**（与 `F-3` 同族）。托管侧一律以
                //   `rc <= 0 ⇒ continue` 为准（**先看 `rc`，再看 `buf`**），本处即此纪律；`NativeReport()` 之外若有人
                //   直调 `PtsGapReportNative` 也必须照此判。**绝不许**把 `buf` 的 nul 结尾当作"读到了一条完整行"的证据。
                for (int cap = 256; cap <= 65536; cap *= 16)
                {
                    byte[] buf = new byte[cap];
                    int rc = PtsGapReportNative(buf, buf.Length);
                    if (rc <= 0) continue;                        // `-1` ⇒ 写不下 ⇒ 放大再试
                    int n = 0;
                    while (n < buf.Length && buf[n] != 0) n++;
                    char[] c = new char[n];
                    for (int k = 0; k < n; k++) c[k] = (char)buf[k];
                    return new string(c);
                }
                return string.Empty;                              // `64 KiB` 仍写不下 ⇒ 如实"读不到"（不猜）
            }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>判：这次失败是不是"PTS 能力不可用"（**闭集**：DllImport 三族 ∪ native 亲口作证）。</summary>
        internal static bool IsPtsUnavailable(Exception e, int nativeCallsBefore)
        {
            if (e is EntryPointNotFoundException          // 符号不在 shim 里（A1 之前 / 旧 shim）
                || e is DllNotFoundException              // shim 整个没找到
                || e is BadImageFormatException)          // shim 不是可装载的 ELF
            {
                return true;
            }
            // native 侧的累计入口调用数**涨了** ⇒ 这一次尝试真的问过 PTS 上下文族
            return NativeCalls() > nativeCallsBefore;
        }

        // A1 的 stub 唯一取值（= 上游 `Pts.cs:507` `tserrNotImplemented`）
        // ⏪ `t90`（2026-09-29）**`t88` 的 `F-5` 口径句（只登记，不改值）**：本常量与 native 侧真值
        //   `WPF_PTS_ERR_NOT_IMPLEMENTED`（现取 `src/WpfGfx.Linux.Native/src/win32_pts.c`：`#define WPF_PTS_ERR_NOT_IMPLEMENTED (-10000)`）
        //   **同值** ⇒ `NativeError()` 读不到时回落本常量，**读到时**真值也是它 ⇒ **`err=` 面无法自证"到底读到了没有"**
        //   （旧件"读不到"与新件"读到"都打 `err=-10000`）。**本件不改值**：`err=-10000` 是在册面（`leg_*.env` 的
        //   `native_err=-10000`、判据件与台账行都用它），改成"域外哨兵"会**改动判据面** ⇒ 需另派单（改哨兵须同趟
        //   与判据件/在册证据对齐）。⇒ 今日的"读到没读到"只能由**旁边那几格**作证（`entry=`／`native_gap=`／
        //   `PTS_GAP entry=…` 台账行），**不许**只看 `err=`。
        private const int A1_STUB_ERR = -10000;

        /// <summary>把失败**具名**（入口名/错误码来自 native 台账；读不到就如实写 unknown）。</summary>
        internal static PtsUnavailableException Describe(Exception e)
        {
            string entry = NativeEntryName();
            // ⏪ 取数补齐（队长 2026-09-28；`t70` 复核的推荐形态）：native 台账在此刻**必为零行**
            //   （本链第一个会留痕的站 `PTS.CreateDocContext` 还没走到）⇒ 只读台账只会得 `unknown`。
            //   而入口名/**就在原始异常里**（DllImport 三族的 `Message` 自带入口名/DLL 名）⇒ 从此处补一格取数，
            //   使 `entry=` 面**具名**（不带后缀：名字原样，便于与名册直接对拍；来源只记在 msg 里）。
            bool entryFromException = false;
            if (entry == "unknown")
            {
                string fromEx = EntryNameFromException(e);
                // ⏪ `t87`：`EntryNameFromException()` 的"取不到"哨兵由**空串**改为 **`"unknown"`**
                //   （与 `NativeEntryName()` 的哨兵一致 ⇒ 判据侧只认一个哨兵值）；此处按哨兵判，不再按"非空"判。
                if (!string.IsNullOrEmpty(fromEx) && fromEx != "unknown") { entry = fromEx; entryFromException = true; }
            }
            int err = NativeError();
            string msg = "PTS 能力不可用（PTS / 原生 LineServices 未实现 —— D-G70）：entry=" + entry
                       + (entryFromException ? "（入口名取自异常文本：native 台账此行未留痕）" : "")
                       + " err=" + err.ToString("0")
                       + " ⇒ 本次布局**如实失败**，不假装成功";
            return new PtsUnavailableException(entry, err, msg, e);
        }
    }

    /// <summary>
    /// W86A `A3`：把"这一页不支持"打到日志（**不许静默**）。一行、每个视图一次、带入口名。
    /// </summary>
    internal static class WpfLinuxPtsGapTrace
    {
        internal static void Report(string site, PtsUnavailableException e)
        {
            try
            {
                System.Console.Error.WriteLine(
                    "[PTS-UNAVAILABLE] site=" + site + " entry=" + (e.Entry ?? "unknown")
                    + " err=" + e.ErrorCode.ToString("0")
                    + " action=page-placeholder（已画出页级占位；进程继续）");
                System.Console.Error.Flush();
            }
            catch (Exception)
            {
                // 打印失败不许改变行为（例如 stderr 已关闭）。
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ⏪ `t155`（P1-W71 第二跳·第三拍）测量小单 —— `nmp` **托管侧类型**的**只读**仪器
    //
    //  唯一问题：native 侧 `+136 → +168` 吃下的那个 `nmp`，在**托管侧**到底是**什么东西**？
    //    `PtsContext.HandleToObject(nmp)` 的**实际类型**／是否 `BaseParagraph` 族／是否 `ISegment`。
    //
    //  形制照 `t133` 的 `[FSCBK-CANARY]`：**只读**——不写任何字段、不改任何行为、
    //  真实委托的**返回值与 out/ref 参数逐位透传**；失败只打 `NOINFO` 行，绝不静默。
    //
    //  安全性（写死）：`PtsContext.HandleToObject` 内部是三条 `Invariant.Assert`
    //  （`Invariant.FailFast` **不可捕获**）⇒ 本仪器**先**用 `IsValidHandle`
    //  （有界检查、越界返回 false、**不断言**）把门，**只有**它说"活句柄"才查表 ⇒ 断言恒真。
    //  查表与打印全程 `try/catch`；异常只打 `NOINFO`。
    //
    //  预算（写死）：甲＝`GetFirstPara`、乙＝`GetParaProperties`、丙＝`GetNextPara` 各 12 行，
    //  到点打一行 `[NMP-TYPE-CAP]`（防日志过 MB）。`PTS_NMPTYPE_OFF=1` 关掉**打印**
    //  （接线与透传不变）。
    // ══════════════════════════════════════════════════════════════════
    internal static class P1NmpTypeProbe
    {
        private static readonly object _lock = new object();
        private static System.Reflection.PropertyInfo _ctxProp;
        private static bool _ctxPropDone;
        private static int _leftA = 12, _leftB = 12, _leftC = 12;
        private static bool _capA, _capB, _capC;
        private static bool _off, _offKnown;

        private static bool Off()
        {
            if (!_offKnown)
            {
                _offKnown = true;
                try { _off = System.Environment.GetEnvironmentVariable("PTS_NMPTYPE_OFF") == "1"; }
                catch (Exception) { _off = false; }
            }
            return _off;
        }

        private static string Hex(IntPtr h) { return "0x" + ((long)h).ToString("x"); }

        private static PtsContext ContextOf(PtsHost host)
        {
            try
            {
                if (_ctxProp == null && !_ctxPropDone)
                {
                    _ctxPropDone = true;
                    _ctxProp = typeof(PtsHost).GetProperty("PtsContext",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public);
                }
                if (_ctxProp == null || host == null) return null;
                return _ctxProp.GetValue(host, null) as PtsContext;
            }
            catch (Exception) { return null; }
        }

        private static object Lookup(PtsContext ctx, IntPtr h, out string why)
        {
            why = "no-context";
            if (ctx == null) return null;
            try
            {
                if (!ctx.IsValidHandle(h)) { why = "not-a-live-handle"; return null; }
                object o = ctx.HandleToObject(h);
                if (o == null) { why = "handle-obj-null"; return null; }
                return o;
            }
            catch (Exception e) { why = "lookup-threw:" + e.GetType().Name; return null; }
        }

        private static System.Reflection.FieldInfo FindField(System.Type t, string name)
        {
            try
            {
                while (t != null)
                {
                    System.Reflection.FieldInfo fi = t.GetField(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public);
                    if (fi != null) return fi;
                    t = t.BaseType;
                }
            }
            catch (Exception) { }
            return null;
        }

        // 单个句柄的**只读**描述：实际类型 ＋ 两个"是不是" ＋（可达时）`_firstChild` 一格
        private static string Desc(PtsContext ctx, IntPtr h, string tag)
        {
            if (h == IntPtr.Zero) return tag + "=(nil)";
            string why;
            object o = Lookup(ctx, h, out why);
            if (o == null) return tag + "=" + Hex(h) + " TYPE=NOINFO reason=" + why;
            System.Type t = o.GetType();
            bool isSeg = false;
            try
            {
                foreach (System.Type i in t.GetInterfaces())
                {
                    if (i.FullName == "System.Windows.Documents.ISegment" || i.Name == "ISegment") { isSeg = true; break; }
                }
            }
            catch (Exception) { }
            string s = tag + "=" + Hex(h) + " TYPE=" + t.FullName
                + " " + tag + "_isBaseParagraph=" + (o is BaseParagraph ? "1" : "0")
                + " " + tag + "_isISegment=" + (isSeg ? "1" : "0");
            System.Reflection.FieldInfo fi = FindField(t, "_firstChild");
            if (fi == null) return s + " " + tag + "_firstChild=FIELD-ABSENT";
            object child = null;
            try { child = fi.GetValue(o); } catch (Exception) { }
            return s + " " + tag + "_firstChild=" + (child == null ? "null" : child.GetType().FullName);
        }

        // 成对读数：`nms`（段）与 `nmp`（段内首段）的**对象同一性**——回答"`nmp` 是不是 `_firstChild`"
        private static string Pair(PtsContext ctx, IntPtr nms, IntPtr nmp)
        {
            if (nms == IntPtr.Zero || nmp == IntPtr.Zero) return " PAIR=NOINFO reason=handle-zero";
            string w1, w2;
            object seg = Lookup(ctx, nms, out w1);
            object par = Lookup(ctx, nmp, out w2);
            if (seg == null || par == null) return " PAIR=NOINFO reason=seg-or-para-not-live(" + w1 + "/" + w2 + ")";
            System.Reflection.FieldInfo fi = FindField(seg.GetType(), "_firstChild");
            if (fi == null) return " PAIR=NOINFO reason=firstchild-field-absent seg=" + seg.GetType().FullName;
            object child = null;
            try { child = fi.GetValue(seg); } catch (Exception) { }
            return " PAIR seg=" + seg.GetType().FullName
                + " seg_firstChild=" + (child == null ? "null" : child.GetType().FullName)
                + " nmp_eq_seg_firstChild=" + (child != null && ReferenceEquals(child, par) ? "1" : "0");
        }

        private static bool Take(string which)
        {
            lock (_lock)
            {
                if (which == "A")
                {
                    if (_leftA > 0) { _leftA--; return true; }
                    if (!_capA) { _capA = true; Emit("[NMP-TYPE-CAP] slot=GetFirstPara 打印预算到点（后续同槽不再打）"); }
                    return false;
                }
                if (which == "B")
                {
                    if (_leftB > 0) { _leftB--; return true; }
                    if (!_capB) { _capB = true; Emit("[NMP-TYPE-CAP] slot=GetParaProperties 打印预算到点（后续同槽不再打）"); }
                    return false;
                }
                if (_leftC > 0) { _leftC--; return true; }
                if (!_capC) { _capC = true; Emit("[NMP-TYPE-CAP] slot=GetNextPara 打印预算到点（后续同槽不再打）"); }
                return false;
            }
        }

        private static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (Exception) { }
        }

        private static void Print(PtsHost host, string which, string slot, string detail, IntPtr nms, IntPtr nmp)
        {
            if (Off()) return;
            if (!Take(which)) return;
            string line;
            try
            {
                PtsContext ctx = ContextOf(host);
                line = "[NMP-TYPE] slot=" + slot + " " + detail
                    + " " + Desc(ctx, nms, "nms")
                    + " " + Desc(ctx, nmp, "nmp")
                    + Pair(ctx, nms, nmp);
            }
            catch (Exception e)
            {
                line = "[NMP-TYPE] slot=" + slot + " " + detail + " NOINFO reason=instrument-threw:" + e.GetType().Name;
            }
            Emit(line);
        }

        // ── 三个槽的**透传**包装（返回值/out/ref 参数与原方法逐位相同）──────────────
        internal static int GetFirstPara(PtsHost host, IntPtr pfsclient, IntPtr nms, out int fSuccessful, out IntPtr nmp)
        {
            int fserr = host.GetFirstPara(pfsclient, nms, out fSuccessful, out nmp);
            Print(host, "A", "GetFirstPara", "fserr=" + fserr + " fSucc=" + fSuccessful, nms, nmp);
            return fserr;
        }

        internal static int GetNextPara(PtsHost host, IntPtr pfsclient, IntPtr nms, IntPtr nmpCur, out int fFound, out IntPtr nmpNext)
        {
            int fserr = host.GetNextPara(pfsclient, nms, nmpCur, out fFound, out nmpNext);
            Print(host, "C", "GetNextPara", "fserr=" + fserr + " fFound=" + fFound + " nmpCur=" + Hex(nmpCur), nms, nmpNext);
            return fserr;
        }

        internal static int GetParaProperties(PtsHost host, IntPtr pfsclient, IntPtr nmp, ref PTS.FSPAP fspap)
        {
            int fserr = host.GetParaProperties(pfsclient, nmp, ref fspap);
            Print(host, "B", "GetParaProperties", "fserr=" + fserr, IntPtr.Zero, nmp);
            return fserr;
        }
    }
}
'''

# ── `t155`（P1-W71 第二跳·第三拍）仪器：三个读槽改接只读透传包装（现盘手加块，现封进生成器）───
PTSCACHE_E5_NEEDLE = '''            contextInfo.fscbk.cbkgen.pfnGetFirstPara = new PTS.GetFirstPara(ptsHost.GetFirstPara);
            contextInfo.fscbk.cbkgen.pfnGetNextPara = new PTS.GetNextPara(ptsHost.GetNextPara);
'''
PTSCACHE_E5_REPL = '''            // ⏪ `t155` 测量小单：三个**读**槽改接**只读**仪器（透传原方法返回值；实现见文件末 `P1NmpTypeProbe`）
            //    ⚠️ 记账（写死）：接线面的**封送指针**因此换代 ⇒ `[FSCBK-CANARY]` 的槽值只准**同趟内**互比。
            contextInfo.fscbk.cbkgen.pfnGetFirstPara = new PTS.GetFirstPara(
                (IntPtr p1, IntPtr p2, out int o1, out IntPtr o2) => P1NmpTypeProbe.GetFirstPara(ptsHost, p1, p2, out o1, out o2));
            contextInfo.fscbk.cbkgen.pfnGetNextPara = new PTS.GetNextPara(
                (IntPtr p1, IntPtr p2, IntPtr p3, out int o1, out IntPtr o2) => P1NmpTypeProbe.GetNextPara(ptsHost, p1, p2, p3, out o1, out o2));
'''
PTSCACHE_E5B_NEEDLE = '''            contextInfo.fscbk.cbkgen.pfnGetParaProperties = new PTS.GetParaProperties(ptsHost.GetParaProperties);
'''
PTSCACHE_E5B_REPL = '''            contextInfo.fscbk.cbkgen.pfnGetParaProperties = new PTS.GetParaProperties(
                (IntPtr p1, IntPtr p2, ref PTS.FSPAP o1) => P1NmpTypeProbe.GetParaProperties(ptsHost, p1, p2, ref o1));
'''

# ── `t133`（P1-W55）仪器：ConnectCallbacks 末的只读度量调用 + T133DumpFscbkCanary 方法群（现盘手加块）───
PTSCACHE_E6_NEEDLE = '''            contextInfo.fscbk.cbktxt.pfnGetDurFigureAnchor = new PTS.GetDurFigureAnchor(ptsHost.GetDurFigureAnchor);
        }
'''
PTSCACHE_E6_REPL = '''            contextInfo.fscbk.cbktxt.pfnGetDurFigureAnchor = new PTS.GetDurFigureAnchor(ptsHost.GetDurFigureAnchor);
            // ⏪ `t133`（P1-W55）测量小单 —— **托管侧只读仪器**（调用点）：装配**完成之后**打
            //   `[FSCBK-CANARY] …` 机读行（实测偏移 ＋ 每个槽的真实封送值），供 native 侧按**同一
            //   偏移**做**只读回读**、逐字节对拍。⚠️ **只打印**：不写任何字段、**不调用**任何回调。
            T133DumpFscbkCanary(ref contextInfo);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // ⏪ `t133`（P1-W55）测量小单 —— `FSCONTEXTINFO.fscbk` / `FSCBKGEN` 槽偏移的**托管侧仪器**
        //
        //   **为什么需要它**：`FSCONTEXTINFO.fscbk` 是一张 **103 槽、按值嵌入**的回调表（实测
        //   `Marshal.SizeOf(FSCBK)` ＝ 824 ＝ 103×8）；它的**槽偏移**是本族"驱动链"的钥匙，而
        //   **native 侧没有仪器**能测它（夹具＝自证循环；`dladdr` 分不出槽；试调错槽＝崩；拿伪造
        //   `nms`/`nmp` 调真槽 ⇒ 托管侧 `HandleToObject` ⇒ **`Invariant.FailFast` 不可捕获**）。
        //   ⇒ 由**托管侧**给出 ① 运行期布局 API 的**实测偏移**（`Marshal.OffsetOf`/`SizeOf`）
        //     ② 每个槽的**真实封送值**（canary）：`cbkgen` 的槽是委托 ⇒ 打
        //     `Marshal.GetFunctionPointerForDelegate`（＝本进程内该委托会被封送成的函数指针）；
        //     `cbkobj` 的前三槽与整个 `cbkwrd` 声明为 `IntPtr` 且**未接线** ⇒ 打原始值（应为 0）。
        //
        //   **边界（写死）**：本仪器**只读**——不写 `contextInfo` 任何字段、**不调用**任何回调、
        //   不改任何行为、不影响 `return`；失败（反射拿不到类型）只打 `NOINFO` 行，绝不静默。
        //   **表述纪律**：这些读数的绿**只准**读成"偏移测量有了可复核的机器证据"，
        //   **不许**读成"驱动链已通"或"排版打通"。
        // ════════════════════════════════════════════════════════════════════════════════
        private static void T133DumpFscbkCanary(ref PTS.FSCONTEXTINFO info)
        {
            try
            {
                int offFscbk  = T133Off(typeof(PTS.FSCONTEXTINFO), "fscbk");
                int offCbkgen = T133Off(typeof(PTS.FSCBK), "cbkgen");
                int offCbktxt = T133Off(typeof(PTS.FSCBK), "cbktxt");
                int offCbkobj = T133Off(typeof(PTS.FSCBK), "cbkobj");
                int offCbkfig = T133Off(typeof(PTS.FSCBK), "cbkfig");
                int offCbkwrd = T133Off(typeof(PTS.FSCBK), "cbkwrd");
                int szCtx = (int)Marshal.SizeOf(typeof(PTS.FSCONTEXTINFO));
                int szFscbk = (int)Marshal.SizeOf(typeof(PTS.FSCBK));
                System.Console.Error.WriteLine(
                    "[FSCBK-CANARY] sizeof_ctx=" + szCtx + " sizeof_fscbk=" + szFscbk +
                    " fscbk=" + offFscbk + " cbkgen=" + offCbkgen + " cbktxt=" + offCbktxt +
                    " cbkobj=" + offCbkobj + " cbkfig=" + offCbkfig + " cbkwrd=" + offCbkwrd +
                    " sizeof_gen=" + (int)Marshal.SizeOf(typeof(PTS.FSCBKGEN)) +
                    " sizeof_obj=" + (int)Marshal.SizeOf(typeof(PTS.FSCBKOBJ)) +
                    " sizeof_wrd=" + (int)Marshal.SizeOf(typeof(PTS.FSCBKWRD)));

                Type gt = typeof(PTS.FSCBKGEN), ot = typeof(PTS.FSCBKOBJ), wt = typeof(PTS.FSCBKWRD);
                T133Slot("cbkgen.pfnFSkipPage",          offFscbk + offCbkgen + T133Off(gt, "pfnFSkipPage"),          info.fscbk.cbkgen.pfnFSkipPage);
                T133Slot("cbkgen.pfnGetNextSection",     offFscbk + offCbkgen + T133Off(gt, "pfnGetNextSection"),     info.fscbk.cbkgen.pfnGetNextSection);
                T133Slot("cbkgen.pfnGetSectionProperties", offFscbk + offCbkgen + T133Off(gt, "pfnGetSectionProperties"), info.fscbk.cbkgen.pfnGetSectionProperties);
                T133Slot("cbkgen.pfnGetMainTextSegment", offFscbk + offCbkgen + T133Off(gt, "pfnGetMainTextSegment"), info.fscbk.cbkgen.pfnGetMainTextSegment);
                T133Slot("cbkgen.pfnGetFirstPara",       offFscbk + offCbkgen + T133Off(gt, "pfnGetFirstPara"),       info.fscbk.cbkgen.pfnGetFirstPara);
                T133Slot("cbkgen.pfnGetNextPara",        offFscbk + offCbkgen + T133Off(gt, "pfnGetNextPara"),        info.fscbk.cbkgen.pfnGetNextPara);
                T133Slot("cbkgen.pfnGetParaProperties",  offFscbk + offCbkgen + T133Off(gt, "pfnGetParaProperties"),  info.fscbk.cbkgen.pfnGetParaProperties);
                T133Slot("cbkgen.pfnCreateParaclient",   offFscbk + offCbkgen + T133Off(gt, "pfnCreateParaclient"),   info.fscbk.cbkgen.pfnCreateParaclient);
                T133Slot("cbkgen.pfnTransferDisplayInfo", offFscbk + offCbkgen + T133Off(gt, "pfnTransferDisplayInfo"), info.fscbk.cbkgen.pfnTransferDisplayInfo);
                T133Slot("cbkgen.pfnDestroyParaclient",  offFscbk + offCbkgen + T133Off(gt, "pfnDestroyParaclient"),  info.fscbk.cbkgen.pfnDestroyParaclient);
                // 「未接线槽」的位置指纹（声明是 IntPtr、源码里**未赋值** ⇒ 实测必须读到 0）
                T133Ptr("cbkobj.pfnNewPtr",     offFscbk + offCbkobj + T133Off(ot, "pfnNewPtr"),     info.fscbk.cbkobj.pfnNewPtr);
                T133Ptr("cbkobj.pfnDisposePtr", offFscbk + offCbkobj + T133Off(ot, "pfnDisposePtr"), info.fscbk.cbkobj.pfnDisposePtr);
                T133Ptr("cbkobj.pfnReallocPtr", offFscbk + offCbkobj + T133Off(ot, "pfnReallocPtr"), info.fscbk.cbkobj.pfnReallocPtr);
                T133Ptr("cbkwrd.pfnGetSectionHorizMargins", offFscbk + offCbkwrd + T133Off(wt, "pfnGetSectionHorizMargins"), info.fscbk.cbkwrd.pfnGetSectionHorizMargins);
                System.Console.Error.Flush();
            }
            catch (Exception e)
            {
                System.Console.Error.WriteLine("[FSCBK-CANARY] NOINFO reason=" + e.GetType().Name + ":" + e.Message);
            }
        }

        private static int T133Off(Type t, string f)
        {
            try { return (int)Marshal.OffsetOf(t, f); }
            catch (Exception e) { System.Console.Error.WriteLine("[FSCBK-CANARY] NOINFO off_fail " + t.Name + "." + f + " " + e.GetType().Name); return -1; }
        }

        private static void T133Slot<T>(string name, int abs, T d) where T : Delegate
        {
            string fp = (d == null) ? "NULL" : ("0x" + Marshal.GetFunctionPointerForDelegate<T>(d).ToInt64().ToString("x16"));
            System.Console.Error.WriteLine("[FSCBK-CANARY] slot=" + name + " abs=" + abs + " fp=" + fp + " wired=" + (d == null ? "no" : "yes"));
        }

        private static void T133Ptr(string name, int abs, IntPtr p)
        {
            System.Console.Error.WriteLine("[FSCBK-CANARY] slot=" + name + " abs=" + abs + " raw=0x" + p.ToInt64().ToString("x16") + " wired=no");
        }
'''

# ── ⏪ `T-A72`（`PRECOND-LS-SESSION-DRIVER` · **LS 会话进链**）托管侧两处只读接线 ────────────────
#  【为什么必须落在托管侧】LS 会话身份（`LoCreateContext` 的 `ploc`）**只在托管手上有**：
#    `PtsCache.CreatePTSContext` 造 `TextFormatterContext`（`TextFormatterContext.cs:113` 调
#    `LoCreateContext`）⇒ 只有那里能把 **`ploc` ↔ 本文档上下文句柄** 在**同一窗内**配对记档。
#    详见 `src/Linux/build/MilBridge/P1-tail2-lssession-impl-report.md` §2。
PTSCACHE_E7_NEEDLE = """            TextFormatterContext textFormatterContext;
            IntPtr context;
"""
PTSCACHE_E7_REPL = """            // ⏪ `T-A72`：本 PTS 上下文的 **LS 会话身份**（`LoCreateContext` 的 `ploc`）要能交给
            //   下游段落窗去对账 ⇒ 这里先置 `null`（非最优段落的上下文**没有**会话），
            //   在 `CreateDocContext` 之后由 `NoteContext` 记档。**只增、不改任何控制流**。
            TextFormatterContext textFormatterContext = null;
            IntPtr context;
"""

PTSCACHE_E8_NEEDLE = """            // Create PTS Context
            PTS.Validate(PTS.CreateDocContext(ref _contextPool[index].ContextInfo, out context));
"""
PTSCACHE_E8_REPL = """            // Create PTS Context
            PTS.Validate(PTS.CreateDocContext(ref _contextPool[index].ContextInfo, out context));

            // ── ⏪ `T-A72`（`PRECOND-LS-SESSION-DRIVER` · **LS 会话进链**）──────────────────────────
            //  为什么在这里：`textFormatterContext.Ploc`（`LoCreateContext` 的产出；`TextFormatterContext.cs:483`
            //  的 `internal IntPtr Ploc`，PresentationCore 对 PresentationFramework 开了 `InternalsVisibleTo`）
            //  与**本 PTS 文档上下文句柄**（`context`）在**同一个窗内**都在手上 ⇒ 当场把 `docCtx → ploc`
            //  记进只读交付器；下游段落窗（`TextParaClient.ValidateVisual`）据此把 `(ploc, _paraHandle)`
            //  在**同一窗内**交给 native 去做**会话↔段落对账**。
            //  ⚠️ **只记不写**：不改 `contextInfo`／不改 `context`／不改控制流；`textFormatterContext` 为
            //     `null`（非最优段落）⇒ 交付器直接跳过（如实，不冒充"有会话"）。
            WpfLinuxLsSessionProbe.NoteContext(context, (textFormatterContext != null) ? textFormatterContext.Ploc : IntPtr.Zero);
"""

PTSCACHE_EDITS = [
    (PTSCACHE_E0_NEEDLE, PTSCACHE_E0_REPL, 1),
    (PTSCACHE_E1_NEEDLE, PTSCACHE_E1_REPL, 1),
    (PTSCACHE_E2_NEEDLE, PTSCACHE_E2_REPL, 1),
    (PTSCACHE_E3_NEEDLE, PTSCACHE_E3_REPL, 1),
    (PTSCACHE_E4_NEEDLE, PTSCACHE_E4_REPL, 1),
    (PTSCACHE_E5_NEEDLE, PTSCACHE_E5_REPL, 1),
    (PTSCACHE_E5B_NEEDLE, PTSCACHE_E5B_REPL, 1),
    (PTSCACHE_E6_NEEDLE, PTSCACHE_E6_REPL, 1),
    (PTSCACHE_E7_NEEDLE, PTSCACHE_E7_REPL, 1),
    (PTSCACHE_E8_NEEDLE, PTSCACHE_E8_REPL, 1),
]


# ── FlowDocumentView.Linux.cs 的四处改动（`A3`：页级可见降级）──────────────────
#  【为什么落在这里】`FlowDocumentView` 是 `FlowDocument` 的**底流（bottomless）**呈现宿主，
#    `FlowDocumentScrollViewer`（= hc「流文档」页的缺省页签）走的就是它（`FlowDocumentView.cs:344`
#    `_document.BottomlessFormatter`）。它也已经是 `sealed override MeasureOverride/ArrangeOverride`，
#    即**唯一的测量/摆放收口** ⇒ 在这里接住具名异常，能同时做到"不空白、不重入、不进 _formatter"。
FDV_E1_NEEDLE = """            else if (Document != null)
            {
                // Create bottomless formatter, if necessary.
                EnsureFormatter();

                // Format bottomless content.
                _formatter.Format(constraint);
"""
FDV_E1_REPL = """            else if (Document != null)
            {
                // ── W86A `A3`（`D-G70`）：PTS 不可用 ⇒ **页级具名占位** ──────────────────
                //   ① 已判定过 ⇒ 直接给占位尺寸，**不再进 formatter**（幂等：一次失败只降级一次
                //      ⇒ 不会每次 Measure 都抛一次，也不会重入布局）；
                //   ② 第一次 ⇒ 只接 `PtsUnavailableException` 这**一个具名条件**
                //      （**不是** catch-all：别的异常原样向上传播）。
                if (_ptsUnavailable != null)
                {
                    return PtsGapPlaceholderSize;
                }

                // ⚠️ `EnsureFormatter()` **必须在 try 之内**：PTS 上下文是它**间接**建的
                //   （`_document.BottomlessFormatter` → `FlowDocumentFormatter..ctor`
                //     → `FlowDocumentPage..ctor` → `StructuralCache.Section` → `PtsContext..ctor`
                //     → `PtsCache.AcquireContext`），第一跳根本不在 `Format()` 里。
                //   【本车道实测踩到】W86A 第一版把 try 只罩住 `Format()` ⇒ 异常从
                //   `EnsureFormatter()` 逃出去（实测栈见 W86A 报告 §3），占位一次都没画。
                try
                {
                    // Create bottomless formatter, if necessary.
                    EnsureFormatter();

                    // Format bottomless content.
                    _formatter.Format(constraint);
                }
                catch (PtsUnavailableException ptsGap)
                {
                    _ptsUnavailable = ptsGap;
                    WpfLinuxPtsGapTrace.Report("FlowDocumentView.MeasureOverride", ptsGap);
                    InvalidateVisual();              // ⇒ 走 OnRender 的占位分支（非零墨，不是空白）
                    return PtsGapPlaceholderSize;
                }
"""
FDV_E5_NEEDLE = """        internal FlowDocumentPage DocumentPage
        {
            get
            {
                if (_document != null)
                {
                    EnsureFormatter();
                    return _formatter.DocumentPage;
                }
                return null;
            }
        }
"""
FDV_E5_REPL = """        internal FlowDocumentPage DocumentPage
        {
            get
            {
                if (_document != null)
                {
                    // W86A `A3`：这是**第三个**会撞 PTS 的入口（`DocumentPageTextView..ctor`
                    // 走它：`_page = owner.DocumentPage`），而它的第一跳同样是 `EnsureFormatter()`。
                    // 接住并**返回 null**（那个 ctor 只做 `if (_page is IServiceProvider)` ⇒ null 安全）
                    // —— 目的是"**别让具名异常从这里逃出去**"，而不是"假装有页面"。
                    if (_ptsUnavailable != null) return null;
                    try
                    {
                        EnsureFormatter();
                        return _formatter.DocumentPage;
                    }
                    catch (PtsUnavailableException ptsGap)
                    {
                        _ptsUnavailable = ptsGap;
                        WpfLinuxPtsGapTrace.Report("FlowDocumentView.DocumentPage", ptsGap);
                        InvalidateVisual();
                        return null;
                    }
                }
                return null;
            }
        }
"""
FDV_E6_NEEDLE = """                if (Document != null)
                {
                    // Create bottomless formatter, if necessary.
                    EnsureFormatter();

                    // Arrange bottomless content.
"""
FDV_E6_REPL = """                if (Document != null)
                {
                    // Create bottomless formatter, if necessary.
                    // W86A `A3`：`ArrangeOverride` 与 `MeasureOverride` **同一条第一跳**
                    // （同样的 `EnsureFormatter()`）⇒ 也必须接住，否则在"先摆后量"的顺序下照样逃逸。
                    try
                    {
                        EnsureFormatter();
                    }
                    catch (PtsUnavailableException ptsGap)
                    {
                        _ptsUnavailable = ptsGap;
                        WpfLinuxPtsGapTrace.Report("FlowDocumentView.ArrangeOverride", ptsGap);
                        InvalidateVisual();
                        return arrangeSize;
                    }

                    // Arrange bottomless content.
"""
FDV_E2_NEEDLE = """            Size safeArrangeSize = arrangeSize;

            if (!_suspendLayout)
            {
"""
FDV_E2_REPL = """            Size safeArrangeSize = arrangeSize;

            // W86A `A3`：PTS 不可用 ⇒ 只摆占位（`_formatter.DocumentPage` 无效，**一个字段都不许碰**）
            if (_ptsUnavailable != null)
            {
                return arrangeSize;
            }

            if (!_suspendLayout)
            {
"""
FDV_E3_NEEDLE = """        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            return _pageVisual;
        }

        #endregion Protected Methods
"""
FDV_E3_REPL = """        protected override Visual GetVisualChild(int index)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            return _pageVisual;
        }

        /// <summary>
        /// W86A `A3`（`D-G70`）：PTS 不可用时的**页级具名占位** —— 洋红矩形 + 黑边框 + 三行自述文字。
        ///
        /// 【判据读的就是这里的三件事】① **非零墨**：洋红像素数 &gt; 0（"空白"与"已降级"因此**可机读区分**）；
        /// ② **具名**：页面上直接写着"不支持"与缺口入口名（人眼可读，不必翻日志）；
        /// ③ **零墨差**：`_ptsUnavailable == null`（也就是**所有正常页**）时本方法**立即返回**
        ///    ⇒ 正常页的像素**一个都不变**（这是"零回归"能被机器证明的那一半）。
        ///
        /// ⚠️ 这不是"渲染出了文档"：它画的是"**我渲染不出来，原因是这个**"。
        /// </summary>
        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_ptsUnavailable == null) return;      // 正常路径：零墨差（不许改变任何正常页）
            if (drawingContext == null) return;

            Rect box = new Rect(0, 0, PtsGapPlaceholderSize.Width, PtsGapPlaceholderSize.Height);
            // 洋红（`Brushes.Magenta` = #FFFF00FF）是本移植里"能力缺口占位"的保留色。
            drawingContext.DrawRectangle(Brushes.Magenta, new Pen(Brushes.Black, 2.0), box);

            string[] lines = new string[]
            {
                "此页不支持：PTS / 原生 LineServices 未实现（D-G70 / D-G78）",
                "NOT SUPPORTED on this port: PTS is not implemented.",
                "entry=" + (_ptsUnavailable.Entry ?? "unknown")
                    + "  err=" + _ptsUnavailable.ErrorCode.ToString(CultureInfo.InvariantCulture),
            };
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double y = 8;
            for (int i = 0; i < lines.Length; i++)
            {
                FormattedText ft = new FormattedText(
                    lines[i],
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("DejaVu Sans"),
                    i == 0 ? 14.0 : 12.0,
                    Brushes.Black,
                    dip);
                ft.MaxTextWidth = Math.Max(1.0, PtsGapPlaceholderSize.Width - 20.0);
                drawingContext.DrawText(ft, new Point(10.0, y));
                y += ft.Height + 4.0;
            }
        }

        #endregion Protected Methods
"""
FDV_E4_NEEDLE = """        private bool _suspendLayout;                // Layout of the page is suspended.
"""
FDV_E4_REPL = """        private bool _suspendLayout;                // Layout of the page is suspended.

        // W86A `A3`：null = PTS 路径正常（**正常路径的唯一取值**）；非 null = 本视图已降级为具名占位。
        // ⚠️ 它是**视图级**的幂等闩（与 `PtsCache` 的进程级闩各管一段）：进程级闩保证"不再进 native"，
        //    视图级闩保证"不再进 formatter、只画出占位一次"。
        private PtsUnavailableException _ptsUnavailable;
"""

FDV_EDITS = [
    (FDV_E1_NEEDLE, FDV_E1_REPL, 1),
    (FDV_E2_NEEDLE, FDV_E2_REPL, 1),
    (FDV_E3_NEEDLE, FDV_E3_REPL, 1),
    (FDV_E4_NEEDLE, FDV_E4_REPL, 1),
    (FDV_E5_NEEDLE, FDV_E5_REPL, 1),
    (FDV_E6_NEEDLE, FDV_E6_REPL, 1),
]

# 占位尺寸（Draw 与 Measure 用**同一个**值 ⇒ 不会出现"量出来了但没画到"）
FDV_SIZE_NEEDLE = """        internal FlowDocumentView()
        {
        }
"""
FDV_SIZE_REPL = """        // W86A `A3`：页级占位尺寸（Measure 与 OnRender 共用同一个值）。
        private static readonly Size PtsGapPlaceholderSize = new Size(440.0, 132.0);

        internal FlowDocumentView()
        {
        }
"""

FDV_EDITS.append((FDV_SIZE_NEEDLE, FDV_SIZE_REPL, 1))

# `CultureInfo` 在本文件里没有 using（上游只用 `FlowDirection` 等）⇒ 补一条
FDV_USING_NEEDLE = """using System.Windows;                       // Size
"""
FDV_USING_REPL = """using System.Globalization;                 // CultureInfo（W86A A3 的占位文字）
using System.Windows;                       // Size
"""
FDV_EDITS.insert(0, (FDV_USING_NEEDLE, FDV_USING_REPL, 1))


# ── `T-A41`（`P8` 增量：`HOSTED-FSVIEW-VIEWPORT-DRIVE`）**视口驱动 ＋ 只读台账** ────────────────
#  【为什么落在这里】`FlowDocumentFormatter.Arrange` 的**唯一**调用者就是本文件里的
#    `ArrangeOverride`（现取 `grep -rn '_formatter\.Arrange' upstream/**/FlowDocumentView.cs` ⇒ 恰 1 处）
#    ⇒ **视口驱动只能从本视图发起**（本仓自有生成件；不碰 `upstream/**`）。
#  【驱动是什么】把交给 formatter 的**视口**从"可见区"扩为"**可见区 ∪ 整页计算尺寸**"，
#    并在 `viewport.IsEmpty`／非有限值时**直接用整页**（与上游 `FlowDocumentFormatter.Arrange`
#    对空视口的既有兜底同向）；**只动这一个入参**：
#      · **不**改任何 native 几何（`fsrc`／`dvrUsed`／附属对象盒／子页盒**一律不碰**）；
#      · **不**改 `fsupdinf`／`fUpdateInfoForLinesPresent`（本侧仍**如实**填 `0`）；
#      · **不**删／**不**放宽任何 `Invariant.Assert`（若仍被走到，断言照旧响亮 —— 那是新缺陷）。
#  【零假值】`WPF_FSVIEW_VIEWPORT_DRIVE=0` ⇒ **逐字回到上游行为**（反极性腿；缺省＝开）。
#  【同趟的只读台账】`[FSVIEW]`：每趟 arrange 一行（`doc`／`suspend`／`scroll`／`arrange`／
#    `viewport`／`handed`／`page`），使 `A40 §6` 的 `NOINFO-FSVIEW-ARRANGE-TRIGGER` 有直读面。
FDV_E7_NEEDLE = """                    _formatter.Arrange(safeArrangeSize, viewport);
"""
FDV_E7_REPL = """                    // ── `T-A41`（`HOSTED-FSVIEW-VIEWPORT-DRIVE`）：**本视图发起视口驱动** ──────────────
                    //  病情（现取）：`FlowDocumentFormatter.Arrange:130` 之后的整条视口链
                    //  （页轨枚举 → 宿主段 → 附属对象 → 子页轨 → 容器 → 内容段
                    //   `UpdateViewportSimpleLines:3359`）里，**内容段的行视图从未被造出**
                    //  （`[FSQLL] cLines=1` 恒 0）。本视图是**唯一**把 viewport 交给 formatter 的收口。
                    //  驱动 ＝ **视口 ∪ 整页**（空/非有限 ⇒ 直接用整页），**只动这一个入参**：
                    //    · 不碰 native 几何；· 不改 `fsupdinf`／`fUpdateInfoForLinesPresent`；
                    //    · 不删不放宽任何 `Invariant.Assert`。
                    //  `WPF_FSVIEW_VIEWPORT_DRIVE=0` ⇒ 逐字回上游行为（反极性腿；缺省＝开）。
                    Rect fsviewViewport = WpfLinuxFsViewDrive.Effective(viewport, safeArrangeSize, _formatter, _pageVisual);
                    WpfLinuxFsViewDrive.Report("ArrangeOverride", Document != null, _suspendLayout,
                                               _scrollData != null, safeArrangeSize, viewport, fsviewViewport,
                                               _formatter, _pageVisual);
                    try
                    {
                        _formatter.Arrange(safeArrangeSize, fsviewViewport);
                    }
                    catch (System.Exception fsviewEx)
                    {
                        WpfLinuxFsViewDrive.ReportException("ArrangeOverride.Arrange", fsviewEx);
                        throw;
                    }
"""
# 只读台账（`[FSVIEW]`）—— 放在命名空间末尾（本文件末尾的 `}` 之前），**不改任何既有成员**。
FDV_E8_NEEDLE = """        #endregion IServiceProvider Members
    }
}
"""
FDV_E8_REPL = """        #endregion IServiceProvider Members
    }

    /// <summary>
    /// W86A `T-A41`（`HOSTED-FSVIEW-VIEWPORT-DRIVE`）：**视口驱动 ＋ 只读台账**（`D0` 的同趟托管侧面）。
    ///
    /// 【它是"读数"不是"调参"】`Effective()` 只对**本视图自己交给 formatter 的那一个入参**赋值
    /// （视口＝可见区 ∪ 整页计算尺寸）；它**不接触**任何 native 真值（几何／更新信息／断言都不动）⇒
    /// 下游若仍不造行视图，那是**下游的事实**，不是这里"调"出来的。`Enabled` 读 `WPF_FSVIEW_VIEWPORT_DRIVE`
    /// （缺省开，显式 `0` 关）⇒ 反极性腿可在**同一产物**上把驱动点整个撤掉（逐字回上游入参）。
    /// 【台账】`[FSVIEW]` 每趟 arrange 一行：`doc`／`suspend`／`scroll`／`arrange`／`viewport`／`handed`／`page`
    /// ＋ 尾部具名 `NOINFO=` —— 使 `A40 §6` 的 `NOINFO-FSVIEW-ARRANGE-TRIGGER`（"`ArrangeOverride`
    /// 到底有没有被布局系统调到"）有**直读**面。台账上限 `TraceMax` 行（超出只打一条 `suppressed`）。
    /// </summary>
    internal static class WpfLinuxFsViewDrive
    {
        private const int TraceMax = 400;
        private static int _traceCount;
        private static int _enabled = -1;                 // -1＝未读；0＝关；1＝开

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_FSVIEW_VIEWPORT_DRIVE"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;         // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        private static bool IsFinite(Rect r)
        {
            return !double.IsNaN(r.X) && !double.IsInfinity(r.X)
                && !double.IsNaN(r.Y) && !double.IsInfinity(r.Y)
                && !double.IsNaN(r.Width) && !double.IsInfinity(r.Width)
                && !double.IsNaN(r.Height) && !double.IsInfinity(r.Height);
        }

        /// <summary>
        /// 交给 formatter 的视口：关 ⇒ **原样**返回上游算出的 `viewport`；开 ⇒ `可见区 ∪ 整页 ∪ 已实现视觉子树`，
        /// 且空/非有限时用后两者（`arrangeSize`／`DocumentPage.Size`／`GetDescendantBounds(pageVisual)`）。
        /// **不得**在这里改任何其它变量（本方法只读这 4 个入参）。
        ///
        /// 【为什么含"已实现视觉子树"这一项】现取（`T-A41` 三腿）：本页的 `DocumentPage.Size` 只有
        /// `39.81x39.17` DIP，**远小于**可见视口 ⇒ 只写"整页"这一项时 `handed == viewport`（驱动成空转）。
        /// 而本侧 native 声明的附属对象盒（`Figure` `(30000,20000,42000,15000)` 文本 dpi ⇒ `9600x6400` DIP）
        /// **远在**该页盒之外 —— 它们**已经**被实现在 `_pageVisual` 子树里（`Figure` 的背景 `GhostWhite`
        /// 现取 `29637 px` 即其证）⇒ 该项是**从已实现视觉树里读出来的真实范围**（不是常量、不是伪造几何）。
        /// </summary>
        internal static Rect Effective(Rect viewport, Size arrangeSize, FlowDocumentFormatter formatter, Visual pageVisual)
        {
            if (!Enabled)
            {
                return viewport;
            }

            Rect pageRect = new Rect(0, 0, arrangeSize.Width, arrangeSize.Height);
            try
            {
                Size cs = formatter.DocumentPage.Size;      // 只读：本页"计算尺寸"
                Rect csRect = new Rect(0, 0, cs.Width, cs.Height);
                if (!csRect.IsEmpty && IsFinite(csRect))
                {
                    pageRect = Rect.Union(pageRect, csRect);
                }
            }
            catch (System.Exception)
            {
                // 读不到尺寸**不许**改变行为（退回 arrangeSize 一档；不影响下面的判据）。
            }

            Rect visRect = VisualBounds(pageVisual);
            if (!visRect.IsEmpty && IsFinite(visRect))
            {
                pageRect = Rect.Union(pageRect, visRect);
            }

            if (viewport.IsEmpty || !IsFinite(viewport) || !IsFinite(pageRect))
            {
                return pageRect;
            }
            Rect union = Rect.Union(viewport, pageRect);
            return (union.IsEmpty || !IsFinite(union)) ? pageRect : union;
        }

        /// <summary>
        /// 只读：已实现视觉子树的范围（`VisualTreeHelper.GetDescendantBounds`）。读不到 ⇒ `Rect.Empty`
        /// （**绝不当 0/当整页** —— 拿不到就是拿不到）。
        /// </summary>
        internal static Rect VisualBounds(Visual pageVisual)
        {
            try
            {
                if (pageVisual == null) { return Rect.Empty; }
                return VisualTreeHelper.GetDescendantBounds(pageVisual);
            }
            catch (System.Exception)
            {
                return Rect.Empty;
            }
        }

        private static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string R(Rect r)
        {
            if (r.IsEmpty) { return "empty"; }
            return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
        }

        private static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
                // 打印失败不许改变行为（例如 stderr 已关闭）。
            }
        }

        internal static void Report(string site, bool hasDocument, bool suspendLayout, bool hasScrollData,
                                    Size arrangeSize, Rect viewport, Rect handed, FlowDocumentFormatter formatter,
                                    Visual pageVisual)
        {
            if (_traceCount >= TraceMax)
            {
                if (_traceCount == TraceMax)
                {
                    _traceCount++;
                    Emit("[FSVIEW] site=" + site + " trace=suppressed-after-" + TraceMax + "lines");
                }
                return;
            }
            _traceCount++;
            string pageSize = "NA";
            try
            {
                Size cs = formatter.DocumentPage.Size;
                pageSize = N(cs.Width) + "x" + N(cs.Height);
            }
            catch (System.Exception)
            {
                pageSize = "NA";
            }
            Emit("[FSVIEW] site=" + site
                 + " doc=" + (hasDocument ? 1 : 0)
                 + " suspend=" + (suspendLayout ? 1 : 0)
                 + " scroll=" + (hasScrollData ? 1 : 0)
                 + " drive=" + (Enabled ? "on" : "off")
                 + " arrange=" + N(arrangeSize.Width) + "x" + N(arrangeSize.Height)
                 + " viewport=" + R(viewport)
                 + " handed=" + R(handed)
                 + " page=" + pageSize
                 + " visbounds=" + R(VisualBounds(pageVisual))
                 + " NOINFO=fsview-window(managed-side-readonly-ledger)");
        }

        internal static void ReportException(string site, System.Exception e)
        {
            Emit("[FSVIEW] site=" + site + " outcome=exception type="
                 + ((e == null) ? "null" : e.GetType().FullName));
        }
    }
}
"""
FDV_EDITS.append((FDV_E7_NEEDLE, FDV_E7_REPL, 1))
FDV_EDITS.append((FDV_E8_NEEDLE, FDV_E8_REPL, 1))


# ══════════════════════════════════════════════════════════════════════════════
#  `T-B11`（`PAGEVIEW-ONSCREEN`）：分页视觉宿主 `DocumentPageView`／`DocumentPageHost` 的
#  **「上屏」接线 ＋ 只读台账**（`P8` 新增补丁族；生成件 `temp+rename`）
#
#  【现取断点】hc「流文档」页 `tab1`（滚动／`FlowDocumentView`，**仓内已移植**）画得出，
#    `tab2` 单页视图／`tab3` 查看器（上游件 `DocumentPageView` ＋ `DocumentPageHost`）画不出 ——
#    同一帧、同一 `.so`、同一条 `.so` 内的分页页视觉**确已建起**（现取 `[CHAIN] site=PTSP.UpdatePageVisuals`
#    ×8、`FDG.Arrange size=816x1056` ×10、`PH.UpdateParaListVisuals` ×272、`RenderSimpleLines` ×96）
#    ⇒ 真断点 ＝「**页视觉已建 → 上屏帧**」这一跳（`T-B10` §4.3）。
#
#  【`FlowDocumentView` 那条**可用**接线的可复用最小集（现取，件:行见主体报告 §1）】
#    ① 把**页视觉对象本身**接进本控件自己的视觉树：`FlowDocumentView.Linux.cs:247-248`
#       `_pageVisual = (PageVisual)_formatter.DocumentPage.Visual; AddVisualChild(_pageVisual);`
#    ② 给它**显式定位**：`:254` `_pageVisual.Offset = new Vector(-h, -v);`（不依赖内层 `Arrange` 的隐式测量）
#    ③ `GetVisualChild`／`VisualChildrenCount` 与 ①② **同源**（返回**同一个**被 `AddVisualChild` 的对象）。
#    ⇒ 三条的共同点：**宿主自己做挂载与定位**，不把"上屏"寄托在内层元素的隐式 `Measure/Arrange` 上。
#  【本块做什么】`DocumentPageHost.PageVisual` 的 setter 里，**除上游既有的** ContainerVisual 包壳外，
#    把页视觉**也**按 ②③ 的同源方式挂到宿主自己的视觉树上，并把上游算好的位移**显式**写给页视觉
#    （`VisualOffset`），使"挂载／定位"两条与 `FlowDocumentView` **同形**。
#    · **不**删上游任何一步（包壳照旧）；**不**删／**不**放宽任何 `Invariant.Assert`；
#    · **不**改任何 native 几何、不动 PTS 更新信息。
#  【零假值／默认关】`WPF_PAGEVIEW_ONSCREEN=1` ⇒ 才启用本条接线；**未设即关** ⇒ 逐字回上游。
#    ⚠️ 本席已**真跑反极性**：开/关两腿 `tab2` 帧**逐字节相同**（`1fb95eab89966441`）⇒ 本条接线
#    **不是**断点（实测零效果），故**不默认启用**（不许把"零效果"当"修好了"）；见载体报告 §4。
#  【只读台账】`[DPV]`／`[DPH]`：身份／尺寸／子树包围盒／父链 —— 使"页视觉已建 → 上屏帧"
#    这一跳的**每一格**都有直读面（`WPF_PAGEVIEW_PROBE=0` ⇒ 台账不发生）。
DPH_PROBE_NEEDLE = """        internal static void DisconnectPageVisual(Visual pageVisual)
        {
            // There might be a case where a visual associated with a page was 
            // inserted to a visual tree before. It got removed later, but GC did not
            // destroy its parent yet. To workaround this case always check for the parent
            // of page visual and disconnect it, when necessary.
            Visual currentParent = VisualTreeHelper.GetParent(pageVisual) as Visual;
            if (currentParent != null)
            {
                ContainerVisual pageVisualHost = currentParent as ContainerVisual;
"""
DPH_PROBE_REPL = """        internal static void DisconnectPageVisual(Visual pageVisual)
        {
            // There might be a case where a visual associated with a page was 
            // inserted to a visual tree before. It got removed later, but GC did not
            // destroy its parent yet. To workaround this case always check for the parent
            // of page visual and disconnect it, when necessary.
            Visual currentParent = VisualTreeHelper.GetParent(pageVisual) as Visual;
            if (currentParent != null)
            {
                // ── `T-B11`（`PAGEVIEW-ONSCREEN`）：直接挂载形态下，页视觉的父**就是**宿主 ──────
                //  打开驱动时 `PageVisual` setter 把页视觉**本身**挂到宿主（照 `FlowDocumentView`
                //  范式，不经 `ContainerVisual` 包壳）⇒ 必须先认这一形态，否则下游
                //  `currentParent as ContainerVisual == null` 会误判成"父不是 DocumentPageHost"
                //  而**响亮抛错**（那是一处**新缺陷**，不是本增量要的）。关 ⇒ 逐字回上游。
                DocumentPageHost directHost = currentParent as DocumentPageHost;
                if (directHost != null)
                {
                    directHost.PageVisual = null;
                    return;
                }
                ContainerVisual pageVisualHost = currentParent as ContainerVisual;
"""

# `PageVisual` setter：照 `FlowDocumentView` 范式挂载（`WPF_PAGEVIEW_ONSCREEN=1` 才启用；默认关）
DPH_SET_NEEDLE = """            set
            {
                ContainerVisual pageVisualHost;
                if (_pageVisual != null)
                {
                    pageVisualHost = VisualTreeHelper.GetParent(_pageVisual) as ContainerVisual;
                    Invariant.Assert(pageVisualHost != null);
                    pageVisualHost.Children.Clear();
                    this.RemoveVisualChild(pageVisualHost);
                }
                _pageVisual = value;
                if (_pageVisual != null)
                {
                    pageVisualHost = new ContainerVisual();
                    this.AddVisualChild(pageVisualHost);
                    pageVisualHost.Children.Add(_pageVisual);
                    pageVisualHost.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
                }
            }
"""
DPH_SET_REPL = """            set
            {
                ContainerVisual pageVisualHost;
                WpfLinuxPageViewProbe.ReportHostSetSeq(this, _pageVisual, value);
                // ── `T-B11`（`PAGEVIEW-ONSCREEN`）：照 `FlowDocumentView` 范式挂载页视觉 ──────────
                //  `FlowDocumentView` 那条**可用**接线是"宿主**自己**把页视觉对象 `AddVisualChild`
                //  进自己的视觉树 ＋ 显式定位"（`FlowDocumentView.Linux.cs:247-254`），**不**经内层
                //  元素的隐式 `Measure/Arrange`。上游 `DocumentPageHost` 则是把页视觉塞进一个
                //  **新建的 `ContainerVisual` 包壳**、再把包壳 `AddVisualChild`（`:95-98`）。
                //  两种形态在**本移植的成帧面上不等价**：现取（腿 `b11visit`）渲染遍历**确已**走到
                //  `DPV → DPH → 页视觉`（`[PAGEVIEW] site=DPV.GetVisualChild` ＝ `DPH.GetVisualChild`
                //  ＝ 246 次、尺寸/可见性全对），却**一像素也上不了屏** ⇒ 断点在"页视觉子树在本移植的
                //  成帧面上没被合成"。本块把挂载改成与 `FlowDocumentView` **同形**。
                //  ⚠️ **不删**上游任何一步（关闸时逐字保留包壳形态）；**不删／不放宽**任何断言。
                //  零假值／默认关：`WPF_PAGEVIEW_ONSCREEN=1` 才启用；未设 ⇒ **逐字回上游**（反极性腿）。
                if (_pageVisual != null)
                {
                    if (WpfLinuxPageViewProbe.Enabled)
                    {
                        this.RemoveVisualChild(_pageVisual);
                    }
                    else
                    {
                        pageVisualHost = VisualTreeHelper.GetParent(_pageVisual) as ContainerVisual;
                        Invariant.Assert(pageVisualHost != null);
                        pageVisualHost.Children.Clear();
                        this.RemoveVisualChild(pageVisualHost);
                    }
                }
                _pageVisual = value;
                if (_pageVisual != null)
                {
                    if (WpfLinuxPageViewProbe.Enabled)
                    {
                        this.AddVisualChild(_pageVisual);
                        WpfLinuxPageViewProbe.ReportAttach(this, _pageVisual, true);
                    }
                    else
                    {
                        pageVisualHost = new ContainerVisual();
                        this.AddVisualChild(pageVisualHost);
                        pageVisualHost.Children.Add(_pageVisual);
                        pageVisualHost.SetValue(FlowDirectionProperty, FlowDirection.LeftToRight);
                        WpfLinuxPageViewProbe.ReportAttach(this, _pageVisual, false);
                    }
                }
            }
"""

DPH_VISIT_NEEDLE = """        protected override Visual GetVisualChild(int index)
        {
            if (index != 0 || _pageVisual == null)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            return VisualTreeHelper.GetParent(_pageVisual) as Visual;
        }
"""
DPH_VISIT_REPL = """        protected override Visual GetVisualChild(int index)
        {
            WpfLinuxPageViewProbe.ReportVisit("DPH.GetVisualChild", this, _pageVisual, index);
            WpfLinuxPageViewProbe.ReportRenderSub(this, _pageVisual);
            if (index != 0 || _pageVisual == null)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, SR.Visual_ArgumentOutOfRange);
            }
            // T-B11：直接挂载形态下页视觉的父**就是**宿主 ⇒ 子必须是页视觉**本身**
            // （与 `FlowDocumentView` 的 `GetVisualChild` 返回被 `AddVisualChild` 的同一对象**同源**）。
            if (WpfLinuxPageViewProbe.Enabled)
            {
                return _pageVisual;
            }
            return VisualTreeHelper.GetParent(_pageVisual) as Visual;
        }
"""

# 末尾追加只读台账／驱动帮助类（**新建成员**，不改既有成员）
DPH_TAIL_NEEDLE = """        private Visual _pageVisual;
    }
}
"""
DPH_TAIL_REPL = """        private Visual _pageVisual;
    }

    /// <summary>
    /// `T-B11`（`PAGEVIEW-ONSCREEN`）：分页视觉宿主（`DocumentPageView`＋`DocumentPageHost`）的
    /// **只读台账** ＋ **「上屏」驱动闸**。照 `FlowDocumentView` 那条**可用**接线（把页视觉对象本身
    /// `AddVisualChild` 到本控件 ＋ 显式定位）做最小对齐；见本块在 `reapply-patches.py` 内的说明。
    ///
    /// ⚠️ 它**只**做两件事：① 只读打印（身份／尺寸／子树包围盒／父链）；② 在 `WPF_PAGEVIEW_ONSCREEN`
    /// `=1`（**默认关**）下把"页视觉 → 宿主"的挂载／定位按 `FlowDocumentView` 的同形做法补上。
    /// **不删上游任何一步**、**不删不放宽任何 `Invariant.Assert`**、**不碰 native 几何**。
    /// </summary>
    internal static class WpfLinuxPageViewProbe
    {
        private const int TraceMax = 600;
        private static int _trace;
        // ⏪ `T-B13`：渲染遍历读数（`DPV.GetVisualChild`／`DPH.GetVisualChild`，每趟渲染数百行）
        //   **单独记账**（不再吃 `TraceMax` 的共享预算），且**按元素**各记一份 —— 否则先出现的
        //   宿主会把全局额度吃光，把**后面的**宿主（tab3 的 `ReaderPageViewer` 那一份）整片吞掉
        //   （`T-B13` 第一次现取即栽在这里）。
        private const int VisitPerSelfMax = 200;
        private static readonly System.Collections.Generic.Dictionary<int, int> _visitPerSelf
            = new System.Collections.Generic.Dictionary<int, int>();
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_ONSCREEN"); }
                    catch (System.Exception) { s = null; }
                    // ⚠️ `T-B11` **默认关**（只有显式 "1" 才开）：本条接线已被现取**证伪**
                    //   （开/关两腿 `tab2` 帧逐字节相同、具名色同为 0，见载体报告 §4）⇒ 不默认启用，
                    //   不把"零效果"的改动当成"修好了"。保持可复现（`=1` 即开）以备后续复核。
                    _enabled = (s == "1") ? 1 : 0;
                }
                return _enabled == 1;
            }
        }

        internal static bool ProbeOn
        {
            get
            {
                string s = null;
                try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_PROBE"); }
                catch (System.Exception) { s = null; }
                return (s != "0");
            }
        }

        private static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            int h;
            try { h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
            catch (System.Exception) { return "NA"; }
            return "0x" + h.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string S(Size sz)
        {
            return N(sz.Width) + "x" + N(sz.Height);
        }

        internal static string Bounds(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetDescendantBounds(v);
                if (r.IsEmpty) { return "empty"; }
                return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static int Kids(Visual v)
        {
            if (v == null) { return -1; }
            try { return VisualTreeHelper.GetChildrenCount(v); }
            catch (System.Exception) { return -2; }
        }

        internal static string ParentId(Visual v)
        {
            if (v == null) { return "null"; }
            try { return Id(VisualTreeHelper.GetParent(v)); }
            catch (System.Exception) { return "NA"; }
        }

        internal static string Pt(UIElement e, UIElement relativeTo)
        {
            if (e == null || relativeTo == null) { return "null"; }
            try
            {
                Point p = e.TranslatePoint(new Point(0, 0), relativeTo);
                return N(p.X) + "," + N(p.Y);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static string Off(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Vector o = VisualTreeHelper.GetOffset(v);
                return N(o.X) + "," + N(o.Y);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>
        /// 只读：**页视觉的第一层子（＝ PTS 页的 `ContainerVisual`）自身的读数** —— 子数/包围盒/自身
        /// 绘制内容。它把"页视觉**壳**已建"与"页视觉**里真的有内容视觉**"分开：若 `kids=0 ∧ content=empty`，
        /// 那"上不了屏"的成因在**壳之内空**（在分页器造页那一段），而**不在** `DocumentPageView`／
        /// `DocumentPageHost` 的挂载线上。
        /// </summary>
        internal static string ChildInfo(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                int n = VisualTreeHelper.GetChildrenCount(v);
                if (n == 0) { return "nokids"; }
                Visual c = VisualTreeHelper.GetChild(v, 0) as Visual;
                return "c=" + Id(c) + ",kids=" + Kids(c) + ",bounds=" + Bounds(c) + ",content=" + Content(c);
            }
            catch (System.Exception) { return "NA"; }
        }

        private static void SubRec(Visual v, int depth, int lvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > depth || budget <= 0) { return; }
            budget--;
            int k;
            try { k = VisualTreeHelper.GetChildrenCount(v); } catch (System.Exception) { k = -2; }
            sb.Append("L").Append(lvl).Append(':').Append(Id(v)).Append(",k=").Append(k)
              .Append(",b=").Append(Bounds(v)).Append(",c=").Append(Content(v)).Append(" | ");
            for (int i = 0; i < k && i < 6; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (System.Exception) { c = null; }
                SubRec(c, depth, lvl + 1, sb, ref budget);
            }
        }

        /// <summary>只读：页视觉子树**逐层**读数（层号／身份／子数／包围盒／自身绘制内容），有界。</summary>
        internal static string Sub(Visual v, int depth)
        {
            if (v == null) { return "null"; }
            var sb = new System.Text.StringBuilder();
            int budget = 24;
            try { SubRec(v, depth, 0, sb, ref budget); }
            catch (System.Exception) { return "NA"; }
            return sb.ToString();
        }

        // ── `T-B17`：**逐层"类型 + 自身绘制"**读数 ────────────────────────────────
        //  【为什么必须补这一格】`Sub` 只打 `k`（子数）／`b`（包围盒）／`c`（内容包围盒），
        //   而 `VisualTreeHelper.GetContentBounds` 对 `ContainerVisual` 返回的是**子树并集**
        //   ⇒ 一个"空壳容器"与一个"真有绘制的叶子"在 `Sub` 里**长得一模一样**。
        //   本节把两者的差**做成读数**：类型名 ＋（若是 `DrawingVisual`）`Drawing` 的类型。
        //   `draw=null` ⇒ 该叶子**没有绘制指令**（断点在更上游）；`draw=DrawingGroup/…` ⇒
        //   绘制指令在，断点在**呈现**那一跳。缺省常开、只读、有界。
        private static string DrawOf(Visual v)
        {
            try
            {
                DrawingVisual dv = v as DrawingVisual;
                if (dv == null) { return "-"; }
                Drawing d = dv.Drawing;
                return (d == null) ? "none" : d.GetType().Name;
            }
            catch (System.Exception) { return "NA"; }
        }

        private static void TypeRec(Visual v, int depth, int lvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > depth || budget <= 0) { return; }
            budget--;
            int k;
            try { k = VisualTreeHelper.GetChildrenCount(v); } catch (System.Exception) { k = -2; }
            string tn;
            try { tn = (v == null) ? "null" : v.GetType().Name; } catch (System.Exception) { tn = "NA"; }
            sb.Append("T").Append(lvl).Append(':').Append(tn).Append('@').Append(Id(v))
              .Append(",k=").Append(k).Append(",draw=").Append(DrawOf(v)).Append(" | ");
            for (int i = 0; i < k && i < 8; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (System.Exception) { c = null; }
                TypeRec(c, depth, lvl + 1, sb, ref budget);
            }
        }

        internal static string Types(Visual v, int depth)
        {
            if (!TypesOn) { return "(off)"; }
            if (v == null) { return "null"; }
            var sb = new System.Text.StringBuilder();
            int budget = 40;
            try { TypeRec(v, depth, 0, sb, ref budget); }
            catch (System.Exception) { return "NA"; }
            return sb.ToString();
        }

        /// <summary>
        /// `T-B17`（纪律 41「仪器不得扰动被测对象」）：`Types` 会**枚举**视觉树的深层
        /// （`VisualTreeHelper.GetChild`），而这本身会催熟视觉树 ⇒ 本节**缺省关**
        /// （`WPF_PAGEVIEW_TYPES=1` 才开）。**现取**：开/关两腿 `tab1`／`tab3` 帧**逐字节相同**
        /// （`c22457cf663453dd`／`71a93980be1f49a6`）⇒ 本探针在**本形态**下**测得**不扰动；
        /// 缺省关仍是**保守选择**（别的形态未证）。
        /// </summary>
        private static int _typesOn = -1;
        private static bool TypesOn
        {
            get
            {
                if (_typesOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PAGEVIEW_TYPES"); }
                    catch (System.Exception) { s = null; }
                    _typesOn = (s == "1") ? 1 : 0;
                }
                return _typesOn == 1;
            }
        }

        // ── `T-B17`（`PRECOND-TAB3-EHANDLE-CALLSITE`）：`RenderTargetBitmap` 快照建不出来 ⇒ 降级 ──
        //  【现取抛点（`T-B17` 机器证，`FirstChanceException` 抓的栈）】`E_HANDLE` 的调用点是
        //    `BitmapSource.set_WicSourceHandle`（上游 `BitmapSource.cs:579` 的
        //    `HRESULT.Check(MILUnknown.QueryInterface(value, IID_IWICBitmapSource, out _))`），
        //    由 `RenderTargetBitmap.FinalizeCreation()`（`RenderTargetBitmap.cs:256`）触发。
        //    触发链 ＝ `SinglePageViewer.HandleAllBreakRecordsInvalidated`
        //    → `DocumentPageView.DuplicateVisual()` → `DuplicatePageVisual()`（`new RenderTargetBitmap`）。
        //  【语义】`_pageVisualClone` 只是"重分页期间先显示上一张快照"的**可选优化**；上游本块**已经**
        //    把"渲染目标建不出来"当作可降级（那条 `OverflowException` 的注释逐字就是
        //    "render target creation not possible"）⇒ 本移植补同一支。
        //  【零假值】`WPF_DPV_RTB_FALLBACK=0` ⇒ **整块不发生**（`throw;` 照旧 ⇒ 反极性腿）。
        private static int _rtbFallback = -1;
        internal static bool RtbFallback
        {
            get
            {
                if (_rtbFallback < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPV_RTB_FALLBACK"); }
                    catch (System.Exception) { s = null; }
                    // ⚠️ `T-B17` 现取：**缺省关**（只有显式 "1" 才开）。理由（与 `T-B11`/`T-B13`/`T-B16` 同体例）：
                    //   ① 开腿**消除**了 `E_HANDLE`（症状成对 1→0，伴随 `[DPV] site=RtbFallback` 大声记账）——
                    //      这是**真的**；但 ② **帧面判据不成立**：开腿 `tab3` 帧退化为**同腿 `tab1` 的帧**
                    //      （`c22457cf663453dd`，与 `tab1` **逐字节**同），而**缺省/关腿**的 `tab3` 帧带
                    //      "页边框"（`71a93980be1f49a6`，两次独立复现）⇒ 开腿**不再出现**那个页边框 ⇒
                    //      读作"**没再排帧**"（上一 tab 的帧）而不是"页真的上屏"。⇒ 不默认启用。
                    //    `WPF_DPV_RTB_FALLBACK=1` 即开（可复现）。
                    _rtbFallback = (s == "1") ? 1 : 0;
                }
                return _rtbFallback == 1;
            }
        }

        /// <summary>`T-B17`：降级发生时**大声**打一行（点名抛点／类型／HRESULT），不静默吞。</summary>
        internal static void ReportRtbFallback(System.Exception ex)
        {
            try
            {
                string hr = "NA";
                try { hr = "0x" + ex.HResult.ToString("x8", System.Globalization.CultureInfo.InvariantCulture); }
                catch (System.Exception) { hr = "NA"; }
                System.Console.Error.WriteLine("[DPV] site=RtbFallback outcome=degrade-to-live-visual type="
                    + ex.GetType().Name + " hr=" + hr
                    + " cause=BitmapSource.set_WicSourceHandle(MILUnknown.QueryInterface E_HANDLE)"
                    + " NOINFO=dpv-rtb-fallback-readonly");
                System.Console.Error.Flush();
            }
            catch (System.Exception) { }
        }

        // ── `T-B17`：`ArrangeOverride` 末尾**可撤**的 `InvalidateVisual`（把"宿主进场后**没再排帧**"做成成对读数）──
        //  【为什么需要它】`T-B17` 现取：闸开腿（主题字典在场、页宿主被接出）在**缺省**下 `tab3`
        //    帧只有"页边框"（`71a93980be1f49a6`）；把 `RenderTargetBitmap` 那条未处理异常
        //    **降级掉**之后，`tab3` 帧**反而不动**（＝上一 tab 的帧）⇒ **原异常路径正是那一次重绘的
        //    触发者**。本闸把"宿主接上之后**主动**排一帧"做成可证伪的候选：`WPF_DPV_INVALIDATE=1` 才开。
        //  **有界**（每元素 ≤3 次），缺省**关**（不设＝逐字回上游）。
        private static int _invalidateOn = -1;
        private static readonly System.Collections.Generic.Dictionary<int, int> _invalidatePerSelf
            = new System.Collections.Generic.Dictionary<int, int>();
        private static bool InvalidateOn
        {
            get
            {
                if (_invalidateOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPV_INVALIDATE"); }
                    catch (System.Exception) { s = null; }
                    _invalidateOn = (s == "1") ? 1 : 0;
                }
                return _invalidateOn == 1;
            }
        }

        internal static void ReportArrangeEndInvalidate(FrameworkElement view)
        {
            if (!InvalidateOn || view == null) { return; }
            int k;
            try { k = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view); }
            catch (System.Exception) { return; }
            int n;
            _invalidatePerSelf.TryGetValue(k, out n);
            if (n >= 3) { return; }
            _invalidatePerSelf[k] = n + 1;
            // ⚠️ 必须**离开布局趟**再失效：`ArrangeOverride` 内直接 `InvalidateVisual()` 现取**无效**
            //   （`inv1` 腿帧与不失效**逐字节相同**）⇒ 改用 `Dispatcher.BeginInvoke(Render)`。
            try
            {
                view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                    new System.Action(view.InvalidateVisual));
            }
            catch (System.Exception) { }
            Emit("[DPV] site=InvalidateVisual id=" + Id(view) + " n=" + (n + 1)
                 + " via=dispatcher-begininvoke-render NOINFO=dpv-invalidate-readonly");
        }

        private static bool Gate(string tag)
        {
            if (!ProbeOn) { return false; }
            if (_trace >= TraceMax)
            {
                if (_trace == TraceMax)
                {
                    _trace++;
                    Emit("[PAGEVIEW] trace=suppressed-after-" + TraceMax + "lines tag=" + tag);
                }
                return false;
            }
            _trace++;
            return true;
        }

        internal static void ReportMeasure(FrameworkElement view, Size available, object paginator, object page)
        {
            if (!Gate("DPV.Measure")) { return; }
            Emit("[DPV] site=MeasureOverride id=" + Id(view) + " avail=" + S(available)
                 + " pag=" + Id(paginator) + " page=" + Id(page)
                 + " desired=" + S(view.DesiredSize) + " render=" + S(view.RenderSize)
                 + " NOINFO=dpv-measure-readonly");
        }

        internal static void ReportArrange(FrameworkElement view, Size finalSize, object page, Visual pageVisual, FrameworkElement host)
        {
            if (!Gate("DPV.Arrange")) { return; }
            Emit("[DPV] site=ArrangeOverride id=" + Id(view) + " final=" + S(finalSize)
                 + " page=" + Id(page) + " pv=" + Id(pageVisual)
                 + " pvKids=" + Kids(pageVisual) + " pvBounds=" + Bounds(pageVisual)
                 + " host=" + Id(host) + " hostKids=" + Kids(host)
                 + " viewPS=" + PS(view) + " pvPS=" + PS(pageVisual)
                 + " NOINFO=dpv-arrange-readonly");
        }

        internal static void ReportArranged(FrameworkElement view, FrameworkElement host, object page)
        {
            if (!Gate("DPV.Arranged")) { return; }
            Emit("[DPV] site=HostArranged view=" + Id(view)
                 + " host=" + Id(host)
                 + " hostRender=" + ((host == null) ? "null" : S(host.RenderSize))
                 + " hostAt=" + Pt(host, view)
                 + " hostOff=" + Off(host) + " hostXf=" + Xf(host)
                 + " hostKids=" + Kids(host)
                 + " page=" + Id(page)
                 + " NOINFO=dpv-hostarranged-readonly");
        }

        internal static void ReportArrangeEnd(FrameworkElement view, Size finalSize, FrameworkElement host, object page)
        {
            if (!Gate("DPV.ArrangeEnd")) { return; }
            Emit("[DPV] site=ArrangeEnd id=" + Id(view) + " final=" + S(finalSize)
                 + " render=" + S(view.RenderSize)
                 + " host=" + Id(host)
                 + " hostRender=" + ((host == null) ? "null" : S(host.RenderSize))
                 + " page=" + Id(page)
                 + " NOINFO=dpv-arrangeend-readonly");
        }

        // ── `T-B19`：`PageVisual` setter 的**每一次调用**逐条（独立 env，不共享 `Gate` 的 trace 预算）──
        //  【要回答的问题】`tab3` 稳态呈现里页子树**不在** milcore 树里；`[DPH] site=Attach` 只在
        //    `Gate`（TraceMax=600）里各 1 条 ⇒ "setter 被调了几次、旧值被清掉后有没有再挂回来"
        //    没有直读面。本节把它变成**逐条**读数。只读；`WPF_DPH_SET_PROBE=1` 才开（缺省零输出）。
        private static int _setProbeOn = -1;
        internal static bool SetProbeOn2
        {
            get
            {
                if (_setProbeOn < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_DPH_SET_PROBE"); }
                    catch (System.Exception) { s = null; }
                    _setProbeOn = (s == "1") ? 1 : 0;
                }
                return _setProbeOn == 1;
            }
        }

        private static long _setSeqNo;

        internal static void ReportHostSetSeq(Visual host, Visual oldVal, Visual newVal)
        {
            if (!SetProbeOn2) { return; }
            try
            {
                string shell = (oldVal == null) ? "null" : ParentId(oldVal);
                Emit("[DPHSET] seq=" + (++_setSeqNo) + " host=" + Id(host)
                     + " old=" + Id(oldVal) + " new=" + Id(newVal)
                     + " same=" + (ReferenceEquals(oldVal, newVal) ? 1 : 0)
                     + " oldParent=" + shell
                     + " oldKids=" + Kids(oldVal) + " oldBounds=" + Bounds(oldVal)
                     + " newKids=" + Kids(newVal) + " newBounds=" + Bounds(newVal)
                     + " oldPS=" + PS(oldVal) + " newPS=" + PS(newVal)
                     + " NOINFO=dphsetseq-readonly");
            }
            catch (System.Exception) { }
        }

        // ── `T-B19`：把 `T-B12` 的"在屏页被搬空"修复挪到**渲染遍历入口**（见 `DPV_VISIT_REPL` 的说明）──
        //  `DocumentPageView.GetVisualChild` 是渲染/命中遍历枚举页宿主的**唯一出口**；在那里把
        //  "本页自己的段落/浮动视觉"接回本页（只在容器**确已空**时动手；闸沿用 `WPF_PAGEPAGE_REDRIVE`）。
        //  只读之外**只做托管侧换父**；任何失败**不重抛**（它是可选修复路径，不许盖掉页面自身显示）。
        internal static void RedrivePageVisuals(object page)
        {
            if (page == null) { return; }
            try
            {
                MS.Internal.PtsHost.FlowDocumentPage fdp = page as MS.Internal.PtsHost.FlowDocumentPage;
                if (fdp == null) { return; }
                fdp.RedrivePageVisualsOnly();
            }
            catch (System.Exception) { }
        }

        internal static void ReportHostSet(Visual host, Visual pageVisual, Visual newValue)
        {
            if (!Gate("DPH.Set")) { return; }
            Emit("[DPH] site=PageVisual.set host=" + Id(host)
                 + " newValue=" + Id(newValue) + " pv=" + Id(pageVisual)
                 + " pvParent=" + ParentId(pageVisual)
                 + " pvKids=" + Kids(pageVisual) + " pvBounds=" + Bounds(pageVisual)
                 + " NOINFO=dph-pageset-readonly");
        }

        internal static string Content(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetContentBounds(v);
                if (r.IsEmpty) { return "empty"; }
                return N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>
        /// 只读：**该视觉是否连在某个 `PresentationSource`（＝成帧根）上**。`null` ＝ 它所在的视觉树
        /// **没有**接到任何呈现源（也就是说它**不进成帧**——不论树内尺寸算得多对）。
        /// </summary>
        internal static string PS(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(v);
                return Id(ps);
            }
            catch (System.Exception) { return "NA"; }
        }

        /// <summary>只读：到视觉树根的**父链深度**（`-1` ＝ 走不动/异常）。</summary>
        internal static int Depth(Visual v)
        {
            if (v == null) { return -1; }
            try
            {
                int d = 0;
                Visual cur = v;
                while (cur != null)
                {
                    cur = VisualTreeHelper.GetParent(cur) as Visual;
                    d++;
                    if (d > 4096) { break; }
                }
                return d;
            }
            catch (System.Exception) { return -2; }
        }

        /// <summary>
        /// 只读：**该视觉到呈现源根视觉的完整变换矩阵**（`M11,M12,M21,M22,OX,OY`）。
        /// 它把"树里尺寸算得对不对"与"**画到屏上落在哪、多大**"两件事分开：矩阵若把页面映射到
        /// 零面积/界外/非有限值，那"内容已建"与"上不了屏"就同时成立且**可证**。
        /// </summary>
        internal static string Xf(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                PresentationSource ps = PresentationSource.FromVisual(v);
                if (ps == null) { return "nops"; }
                Visual root = ps.RootVisual;
                if (root == null) { return "noroot"; }
                GeneralTransform gt = v.TransformToAncestor(root);
                if (gt == null) { return "noxf"; }
                Rect rb = gt.TransformBounds(new Rect(0, 0, 1, 1));
                return N(rb.X) + "," + N(rb.Y) + "," + N(rb.Width) + "," + N(rb.Height);
            }
            catch (System.Exception) { return "NA"; }
        }

        internal static void ReportAttach(Visual host, Visual pageVisual, bool direct)
        {
            if (!Gate("DPH.Attach")) { return; }
            Emit("[DPH] site=Attach host=" + Id(host) + " pv=" + Id(pageVisual)
                 + " mode=" + (direct ? "direct" : "container")
                 + " pvParent=" + ParentId(pageVisual)
                 + " pvContent=" + Content(pageVisual)
                 + " pvKids=" + Kids(pageVisual)
                 + " pvBound=" + Bounds(pageVisual)
                 + " hostPS=" + PS(host) + " pvPS=" + PS(pageVisual) + " pvDepth=" + Depth(pageVisual)
                 + " hostOff=" + Off(host) + " pvXf=" + Xf(pageVisual)
                 + " pvChild=" + ChildInfo(pageVisual)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " TYPES=" + Types(pageVisual, 5)
                 + " NOINFO=dph-attach-readonly");
        }

        /// <summary>
        /// 只读：`FlowDocumentView`（**画得出**的那条链）的同位对照 —— 页视觉挂在谁身上、
        /// 有没有连到呈现源、子树深度多少。使"两条链的差"有**同口径**读数。
        /// </summary>
        internal static void ReportFdv(FrameworkElement view, Visual pageVisual)
        {
            if (!Gate("FDV.Attach")) { return; }
            Emit("[FDV] site=Attach view=" + Id(view) + " pv=" + Id(pageVisual)
                 + " pvParent=" + ParentId(pageVisual)
                 + " viewPS=" + PS(view) + " pvPS=" + PS(pageVisual) + " pvDepth=" + Depth(pageVisual)
                 + " pvContent=" + Content(pageVisual) + " pvKids=" + Kids(pageVisual)
                 + " pvXf=" + Xf(pageVisual)
                 + " pvChild=" + ChildInfo(pageVisual)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " TYPES=" + Types(pageVisual, 5)
                 + " NOINFO=fdv-attach-readonly");
        }

        /// <summary>
        /// 只读：**渲染遍历（render walk）到底走到了哪一格**。`GetVisualChild` 只由视觉枚举方调用
        /// （渲染／命中测试／变换）⇒ "DPV 有没有被走到"、"DPV→DPH 有没有被走到"这两问因此**有直读面**。
        /// </summary>
        private static int _subOnce;

        /// <summary>只读：**渲染遍历期间**页视觉子树的前若干次逐层读数（确认"壳之内是否真空"）。</summary>
        internal static void ReportRenderSub(FrameworkElement self, Visual pageVisual)
        {
            if (ProbeOn == false) { return; }
            if (_subOnce >= 3) { return; }
            _subOnce++;
            Emit("[DPH] site=RenderSub self=" + Id(self) + " pv=" + Id(pageVisual)
                 + " render=" + S(self.RenderSize)
                 + " SUB=" + Sub(pageVisual, 3)
                 + " NOINFO=dph-rendersub-readonly");
        }

        internal static void ReportPaginator(FrameworkElement view, object paginator)
        {
            if (!Gate("DPV.SetPaginator")) { return; }
            Emit("[DPV] site=SetPaginator id=" + Id(view) + " pag=" + Id(paginator)
                 + " NOINFO=dpv-setpaginator-readonly");
        }

        internal static void ReportCtor(FrameworkElement view)
        {
            if (!Gate("DPV.Ctor")) { return; }
            Emit("[DPV] site=Ctor id=" + Id(view) + " NOINFO=dpv-ctor-readonly");
        }

        /// <summary>
        /// `T-B13`：`DocumentViewerBase.GetPageViewsCollection` 的**只读**读数 —— 直接回答
        /// "这个查看器**有没有页宿主**（`DocumentPageView`）"（＝ tab3 断点的**判决面**）。
        /// </summary>
        internal static void ReportPageViews(object viewer, int n)
        {
            if (!ProbeOn) { return; }
            Emit("[DVBI] site=GetPageViews self=" + Id(viewer) + " selfType=" + TypeName(viewer)
                 + " n=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + " NOINFO=dvbi-readonly");
        }

        internal static string TypeName(object o)
        {
            if (o == null) { return "null"; }
            try { return o.GetType().Name; }
            catch (System.Exception) { return "NA"; }
        }

        internal static void ReportVisit(string site, FrameworkElement self, Visual child, int index)
        {
            // `T-B13`：渲染遍历读数**按元素各记一份**（不再吃 `TraceMax` 的共享额度）
            if (!ProbeOn) { return; }
            int vkey;
            try { vkey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self); }
            catch (System.Exception) { vkey = 0; }
            int vc;
            _visitPerSelf.TryGetValue(vkey, out vc);
            if (vc >= VisitPerSelfMax) { return; }
            _visitPerSelf[vkey] = vc + 1;
            string childRender = "NA";
            try
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null) { childRender = S(fe.RenderSize); }
                else if (child != null) { childRender = "vis"; }
                else { childRender = "null"; }
            }
            catch (System.Exception) { childRender = "NA"; }
            Emit("[PAGEVIEW] site=" + site + " self=" + Id(self) + " idx=" + index
                 + " selfRender=" + S(self.RenderSize) + " selfVis=" + (self.Visibility == Visibility.Visible ? 1 : 0)
                 + " child=" + Id(child) + " childRender=" + childRender
                 + " NOINFO=renderwalk-readonly");
        }
    }
}
"""

DPV_MEASURE_NEEDLE = """            else if (_documentPaginator != null)
            {
                // Reflow content if needed.
"""
DPV_MEASURE_REPL = """            else if (_documentPaginator != null)
            {
                WpfLinuxPageViewProbe.ReportMeasure(this, availableSize, _documentPaginator, _documentPage);
                // Reflow content if needed.
"""

DPV_PAGEVISUAL_NEEDLE = """                pageVisual = _documentPage?.Visual;
"""
DPV_PAGEVISUAL_REPL = """                pageVisual = _documentPage?.Visual;
                WpfLinuxPageViewProbe.ReportArrange(this, finalSize, _documentPage, pageVisual, _pageHost);
"""

DPV_HOSTARRANGE_NEEDLE = """                    _pageHost.Arrange(new Rect(_pageHost.CachedOffset, _documentPage.Size));
"""
DPV_HOSTARRANGE_REPL = """                    _pageHost.Arrange(new Rect(_pageHost.CachedOffset, _documentPage.Size));
                    WpfLinuxPageViewProbe.ReportArranged(this, _pageHost, _documentPage);
"""

DPV_ARRANGEEND_NEEDLE = """            return base.ArrangeOverride(finalSize);
"""
DPV_ARRANGEEND_REPL = """            Size dpvArrangeResult = base.ArrangeOverride(finalSize);
            WpfLinuxPageViewProbe.ReportArrangeEnd(this, finalSize, _pageHost, _documentPage);
            WpfLinuxPageViewProbe.ReportArrangeEndInvalidate(this);
            return dpvArrangeResult;
"""

# 渲染遍历（render walk）直读面：`GetVisualChild` 被谁走到 ⇒ "DPV 被不被走到 / DPV→DPH 被不被走到"
DPV_VISIT_NEEDLE = """        protected override Visual GetVisualChild(int index)
        {
            if (index != 0 || _pageHost == null)
"""
DPV_VISIT_REPL = """        protected override Visual GetVisualChild(int index)
        {
            WpfLinuxPageViewProbe.ReportVisit("DPV.GetVisualChild", this, _pageHost, index);
            // ── `T-B19`（`TAB3-PAGE-RENDER`）：`T-B12` 的"在屏页被搬空"修复**挂晚了** ──────────
            //  【现取】`T-B12` 的 `RedrivePageVisualsForDisplay` 只挂在 `FlowDocumentPage.Visual`
            //   的 **getter** 上（＝`ArrangeOverride` 里读 `_documentPage.Visual` 那一刻）⇒ 本代树上
            //   `[PAGEVIS] site=PTSP.RedrivePageVisuals` **11 趟腿全 0 次**（＝一次都没动手）。
            //   而页子树的容器（`trackVisual`／`floatingElementsVisual`）是在**布局之后、成帧之前**
            //   被搬空的（`[PAGEVIEW] site=PH.UpdParaList.new oldParent=<本页 trackVisual>`）
            //   ⇒ `Arrange` 结束时还"非空"、渲染遍历时已"空"。
            //  【修法（**同源**）】把**同一条**修复挪到**渲染遍历入口**（`GetVisualChild` 是渲染/命中
            //   遍历枚举子节点的唯一出口，`[PAGEVIEW] site=DPV.GetVisualChild` 现取每趟数百次）。
            //   · **不**新增任何几何/native 真值；**不**删/放宽断言；**不**改 `T-B12` 原有的挂钩；
            //   · 修复体**只在"确已空"时**动手（`trackVisual.Children.Count == 0` / `floating.Children.Count == 0`）
            //   · 闸沿用 `T-B12` 的 `WPF_PAGEPAGE_REDRIVE`（缺省开 ⇒ `=0` 即反极性回上游行为）。
            WpfLinuxPageViewProbe.RedrivePageVisuals(_documentPage);
            if (index != 0 || _pageHost == null)
"""

# `T-B12` 只读：`DocumentPaginator` 被接上（＝一个 `DocumentPageView` 被接线）——「tab3 那一路是不是 DPV」的判别
DPV_PAGINATOR_NEEDLE = """                    Invariant.Assert(_documentPage == null);
                    Invariant.Assert(_documentPageAsync == null);
                    _documentPaginator = value;
"""
DPV_PAGINATOR_REPL = """                    Invariant.Assert(_documentPage == null);
                    Invariant.Assert(_documentPageAsync == null);
                    WpfLinuxPageViewProbe.ReportPaginator(this, value);
                    _documentPaginator = value;
"""

# `T-B12` 只读：`DocumentPageView` **构造**（＝「这一路到底有没有 DPV」的最强判别）
DPV_CTOR_NEEDLE = """        public DocumentPageView() : base()
        {
            _pageZoom = 1.0;
        }
"""
DPV_CTOR_REPL = """        public DocumentPageView() : base()
        {
            _pageZoom = 1.0;
            WpfLinuxPageViewProbe.ReportCtor(this);
        }
"""

# ── `T-B17`（`DPV-CLIPTOBOUNDS`）：`DocumentPageView` 的 `ClipToBounds` 覆盖 —— 可撤闸 ────────
#  上游此处**无条件**把 `ClipToBounds` 覆盖为 `true`。`T-B17` 现取：`DPV` 子树的**托管结构**与
#  `FDV`（**画得出**）那条链**同构**（`PageVisual` 自带 `DrawingGroup`、`ParagraphVisual` 叶也带
#  `DrawingGroup`），两链差的只有"挂在谁身上" ⇒ 怀疑本覆盖让整棵子树在本移植的成帧面上被裁掉。
#  `WPF_DPV_CLIPTOBOUNDS=0` ⇒ **跳过覆盖**（＝回 `FrameworkElement` 缺省 `false`），把"是不是裁"
#  做成**成对读数**。⚠️ 缺省仍是**上游行为**（覆盖为 `true`）—— 不设该变量时逐字不变（反极性腿）。
DPV_CLIP_NEEDLE = """        static DocumentPageView()
        {
            ClipToBoundsProperty.OverrideMetadata(typeof(DocumentPageView), new PropertyMetadata(BooleanBoxes.TrueBox));
        }
"""
DPV_CLIP_REPL = """        static DocumentPageView()
        {
            string dpvClip = null;
            try { dpvClip = System.Environment.GetEnvironmentVariable("WPF_DPV_CLIPTOBOUNDS"); }
            catch (System.Exception) { dpvClip = null; }
            if (dpvClip != "0")
            {
                ClipToBoundsProperty.OverrideMetadata(typeof(DocumentPageView), new PropertyMetadata(BooleanBoxes.TrueBox));
            }
        }
"""

# ── `T-B17`（`PRECOND-TAB3-EHANDLE-CALLSITE`）：`RenderTargetBitmap` 快照建不出来 ⇒ **降级** ──────
#  【现取（`T-B17` 机器证）】`WPF_EHANDLE_PROBE=1` 的 `FirstChanceException` 抓到的栈是
#    `MS.Internal.HRESULT.Check` ← `BitmapSource.set_WicSourceHandle`（上游 `BitmapSource.cs:579`
#    的 `HRESULT.Check(MILUnknown.QueryInterface(value, IID_IWICBitmapSource, out _))`）
#    ← `RenderTargetBitmap.FinalizeCreation()`（`RenderTargetBitmap.cs:256`）。
#    触发链 ＝ `SinglePageViewer.HandleAllBreakRecordsInvalidated` → `DocumentPageView.DuplicateVisual()`
#    → `DuplicatePageVisual()`（`new RenderTargetBitmap(…)`）。
#  【为什么"降级"是**上游同源**而不是"吞异常"】上游本块**已经**把"渲染目标建不出来"当可降级 ——
#    紧邻的 `catch(System.OverflowException)` 的注释逐字就是 "render target creation not possible
#    under current memory conditions"。本移植的 WIC 离屏位图**不存在**（`MILQueryInterface` 对
#    RTB 的位图句柄答 `E_HANDLE`）⇒ 属**同一语义**的第二种"建不出来"。降级后 `_pageVisualClone`
#    保持 `null`，`ArrangeOverride` 自然走"显示**实时**页视觉"那一支（不伪造任何几何/内容）。
#  **大声**（点名抛点＋HRESULT），`WPF_DPV_RTB_FALLBACK=0` ⇒ 整块不发生（反极性腿）。
DPV_RTB_NEEDLE = """                catch(System.OverflowException)
                {
                    // Ignore overflow exception - caused by render target creation not possible under current memory conditions.
                }
"""
DPV_RTB_REPL = """                catch(System.OverflowException)
                {
                    // Ignore overflow exception - caused by render target creation not possible under current memory conditions.
                }
                catch(System.Runtime.InteropServices.COMException dpvRtbEx)
                {
                    // `T-B17`：本移植的 `RenderTargetBitmap` 建不出来（见生成器内该块）⇒ 与上一条**同义**降级。
                    if (!WpfLinuxPageViewProbe.RtbFallback)
                    {
                        throw;
                    }
                    WpfLinuxPageViewProbe.ReportRtbFallback(dpvRtbEx);
                }
"""

DPV_EDITS = [
    (DPV_MEASURE_NEEDLE, DPV_MEASURE_REPL, 1),
    (DPV_PAGEVISUAL_NEEDLE, DPV_PAGEVISUAL_REPL, 1),
    (DPV_HOSTARRANGE_NEEDLE, DPV_HOSTARRANGE_REPL, 1),
    (DPV_ARRANGEEND_NEEDLE, DPV_ARRANGEEND_REPL, 1),
    (DPV_VISIT_NEEDLE, DPV_VISIT_REPL, 1),
    (DPV_PAGINATOR_NEEDLE, DPV_PAGINATOR_REPL, 1),
    (DPV_CTOR_NEEDLE, DPV_CTOR_REPL, 1),
    (DPV_CLIP_NEEDLE, DPV_CLIP_REPL, 1),
    (DPV_RTB_NEEDLE, DPV_RTB_REPL, 1),
]
DPH_EDITS = [
    (DPH_PROBE_NEEDLE, DPH_PROBE_REPL, 1),
    (DPH_SET_NEEDLE, DPH_SET_REPL, 1),
    (DPH_VISIT_NEEDLE, DPH_VISIT_REPL, 1),
    (DPH_TAIL_NEEDLE, DPH_TAIL_REPL, 1),
]
# 上游相对路径（`_apply_edits` 会拼 `upstream/wpf/` 前缀）
DPV_UP = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/DocumentPageView.cs"
DPH_UP = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/documents/DocumentPageHost.cs"

# 「画得出」那条链（`FlowDocumentView`）的**同位只读对照**（仅加一行打印）
FDV_E11_NEEDLE = """                        _pageVisual = (PageVisual)_formatter.DocumentPage.Visual;
                        AddVisualChild(_pageVisual);
"""
FDV_E11_REPL = """                        _pageVisual = (PageVisual)_formatter.DocumentPage.Visual;
                        AddVisualChild(_pageVisual);
                        // `T-B11`：让"画得出的那条链"与 `DPV`／`DPH` 有**同口径**读数（只读，不改语义）
                        WpfLinuxPageViewProbe.ReportFdv(this, _pageVisual);
"""
FDV_EDITS.append((FDV_E11_NEEDLE, FDV_E11_REPL, 1))


# ── `T-A44`（`CONTENT-LINEVIS-BRANCH-REACH`）**内容段"造行支入口"只读判别器** ────────────────
#  【为什么落在这里】`T-A43`（`P1-tail2-contentvis-recon.md` §5.1）指认的唯一下一增量是
#    "让内容段落进会发 `[FSQLL]` 的那一支"，但**两因不可分辨**：
#      因 A ＝ `IsDeferredVisualCreationSupported` 为假 ⇒ `UpdateViewport:152` 不进
#              `UpdateViewportSimpleLines`（`via=viewport` 类轮**全段** `[FSQLL]≡0` 与此同向）；
#      因 B ＝ 内容段在 `via=viewport` 类轮里的两次 `[FSQTD]` **不来自** `UpdateViewport`
#              （`FsQueryTextDetails` 是共用入口，单看它不辨调用者）。
#    native 面（`win32_pts.c`）**结构性看不到托管调用者** ⇒ 本判别器只能落在**托管侧的两支
#    入口**（`ValidateVisual`／`UpdateViewport`）与**三个造行支**（`RenderSimpleLines`／
#    `UpdateViewportSimpleLines`／`SyncUpdateDeferredLineVisuals`）上，**只打一条具名行**：
#      `[TPCL] site=… parah=0x… cLines=… composite=… att=… deferred=… … NOINFO=tpcl-entry-readonly`
#    ⇒ "谁到了内容段" ＋ "`IsDeferredVisualCreationSupported` 取何值"**首次可现取**。
#  【零假值】**不改任何行为**：不动出参、不删／不放宽任何 `Invariant.Assert`、
#    不置 `fLinesComposite`／`fUpdateInfoForLinesPresent`；`WPF_TPCL_PROBE=0` 可**整个关掉**
#    判别器（反极性腿：撤掉后逐字回上游行为）。
TPC_E_UV_NEEDLE = """            PTS.FSTEXTDETAILS textDetails;
            PTS.Validate(PTS.FsQueryTextDetails(PtsContext.Context, _paraHandle, out textDetails));
            Invariant.Assert(textDetails.fsktd == PTS.FSKTEXTDETAILS.fsktdFull, "Only 'full' text paragraph type is expected.");

            if (IsDeferredVisualCreationSupported(ref textDetails.u.full))
"""
TPC_E_UV_REPL = """            PTS.FSTEXTDETAILS textDetails;
            PTS.Validate(PTS.FsQueryTextDetails(PtsContext.Context, _paraHandle, out textDetails));
            Invariant.Assert(textDetails.fsktd == PTS.FSKTEXTDETAILS.fsktdFull, "Only 'full' text paragraph type is expected.");

            // ── `T-A44` 只读判别器（**视口支入口**）────────────────────────────────
            WpfLinuxLineVisProbe.Hit("UpdateViewport", _paraHandle, (int)textDetails.u.full.cLines,
                PTS.ToBoolean(textDetails.u.full.fLinesComposite) ? 1 : 0, (int)textDetails.u.full.cAttachedObjects,
                IsDeferredVisualCreationSupported(ref textDetails.u.full) ? 1 : 0,
                "rectV=" + _rect.v + " rectDV=" + _rect.dv + " vpV=" + viewport.v + " vpDV=" + viewport.dv);

            if (IsDeferredVisualCreationSupported(ref textDetails.u.full))
"""

TPC_E_VV_NEEDLE = """            PTS.FSTEXTDETAILS textDetails;
            PTS.Validate(PTS.FsQueryTextDetails(PtsContext.Context, _paraHandle, out textDetails));

            VisualCollection visualChildren = _visual.Children;
            ContainerVisual lineContainerVisual = _visual;
"""
TPC_E_VV_REPL = """            PTS.FSTEXTDETAILS textDetails;
            PTS.Validate(PTS.FsQueryTextDetails(PtsContext.Context, _paraHandle, out textDetails));

            // ── `T-A44` 只读判别器（**视觉支入口**）────────────────────────────────
            WpfLinuxLineVisProbe.Hit("ValidateVisual", _paraHandle, (int)textDetails.u.full.cLines,
                PTS.ToBoolean(textDetails.u.full.fLinesComposite) ? 1 : 0, (int)textDetails.u.full.cAttachedObjects,
                IsDeferredVisualCreationSupported(ref textDetails.u.full) ? 1 : 0,
                "fsktd=" + (int)textDetails.fsktd);

            // ── `T-A45`（`LINEVIS-ON-SCREEN`）：**段落背景视觉的落位** ─────────────────
            //  见生成器内该块的说明；`WPF_LINEVIS_ONSCREEN=0` ⇒ 逐字回上游（反极性腿）。
            if (WpfLinuxChainProbe.EnvOn("WPF_LINEVIS_ONSCREEN"))
            {
                Brush t45BackgroundBrush = (Brush)Paragraph.Element.GetValue(TextElement.BackgroundProperty);
                if (t45BackgroundBrush != null)
                {
                    MbpInfo t45Mbp = MbpInfo.FromElement(Paragraph.Element, Paragraph.StructuralCache.TextFormatterHost.PixelsPerDip);
                    if (ThisFlowDirection != PageFlowDirection)
                    {
                        t45Mbp.MirrorBP();
                    }
                    _visual.DrawBackgroundAndBorder(t45BackgroundBrush, t45Mbp.BorderBrush, t45Mbp.Border,
                                                    _rect.FromTextDpi(), IsFirstChunk, IsLastChunk);
                    WpfLinuxChainProbe.Hit("TPC.ParaBackground", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle)
                        + " rect=" + _rect.u + "," + _rect.v + "," + _rect.du + "," + _rect.dv);
                }
            }

            // ── `T-A69`（`PRECOND-NO-TEXT-SOURCE` · **内容源入站：字符序列**）─────────────
            //  见生成器内该块的说明；`WPF_TEXTSRC_FEED=0` ⇒ 整块不发生（反极性腿）。
            WpfLinuxTextSrcProbe.FeedParagraph(_paraHandle, Paragraph);

            // ── `T-A70`（契约 `C4` · **`cp↔dcp` 偏移由宿主给定**，本侧只校不算）─────────
            //  见生成器内该块的说明；`WPF_CPDCMAP_FEED=0` ⇒ 整块不发生（反极性腿）。
            WpfLinuxCpDcpMapProbe.FeedParagraph(_paraHandle, Paragraph);

            // ── `T-A71`（`PRECOND-NO-TEXT-PARA-IN-CHAIN` · **文本段落进链**）─────────────
            //  见生成器内该块的说明；`WPF_PARACHAIN_FEED=0` ⇒ 整块不发生（反极性腿）。
            WpfLinuxTextParaChainProbe.FeedParagraph(_paraHandle, Paragraph, _pageContext);

            // ── `T-A72`（`PRECOND-LS-SESSION-DRIVER` · **LS 会话进链**）─────────────────
            //  见生成器内该块的说明；`WPF_LSSESS_FEED=0` ⇒ 整块不发生（反极性腿）。
            WpfLinuxLsSessionProbe.Feed(PtsContext.Context, _paraHandle);

            // ── `T-A73`（`PRECOND-NO-LINE-BREAKER` · **行断器**）─────────────────────
            //  见生成器内该块的说明；`WPF_LINEBREAK_FEED=0` ⇒ 整块不发生（反极性腿）。
            WpfLinuxLineBreakProbe.FeedParagraph(_paraHandle);

            VisualCollection visualChildren = _visual.Children;
            ContainerVisual lineContainerVisual = _visual;
"""

TPC_E_RSL_NEEDLE = """            ErrorHandler.Assert(!PTS.ToBoolean(textDetails.fDropCapPresent), ErrorHandler.NotSupportedDropCap);
            int cpTextParaStart = Paragraph.ParagraphStartCharacterPosition;

            if (textDetails.cLines == 0)
                return;
"""
TPC_E_RSL_REPL = """            ErrorHandler.Assert(!PTS.ToBoolean(textDetails.fDropCapPresent), ErrorHandler.NotSupportedDropCap);
            int cpTextParaStart = Paragraph.ParagraphStartCharacterPosition;

            // ── `T-A44` 只读判别器（**造行支：ValidateVisual 派**）────────────────
            WpfLinuxLineVisProbe.Hit("RenderSimpleLines", _paraHandle, (int)textDetails.cLines,
                PTS.ToBoolean(textDetails.fLinesComposite) ? 1 : 0, (int)textDetails.cAttachedObjects, -1,
                "updateInfo=" + (PTS.ToBoolean(textDetails.fUpdateInfoForLinesPresent) ? 1 : 0));

            if (textDetails.cLines == 0)
                return;
"""

TPC_E_LINEGEOM_NEEDLE = """            // Get list of simple lines.
            PTS.FSLINEDESCRIPTIONSINGLE [] arrayLineDesc;
            PtsHelper.LineListSimpleFromTextPara(PtsContext, _paraHandle, ref textDetails, out arrayLineDesc);
"""
TPC_E_LINEGEOM_REPL = """            // Get list of simple lines.
            PTS.FSLINEDESCRIPTIONSINGLE [] arrayLineDesc;
            PtsHelper.LineListSimpleFromTextPara(PtsContext, _paraHandle, ref textDetails, out arrayLineDesc);

            // ── `T-A45` 只读几何判别器（**行盒落位**）──────────────────────────────
            if (arrayLineDesc.Length > 0)
            {
                PTS.FSLINEDESCRIPTIONSINGLE lg0 = arrayLineDesc[0];
                WpfLinuxLineVisProbe.Hit("RenderSimpleLines.Geom", _paraHandle, arrayLineDesc.Length, 0, 0, -1,
                    "urStart=" + lg0.urStart + " vrStart=" + lg0.vrStart + " dur=" + lg0.dur
                    + " asc=" + lg0.dvrAscent + " desc=" + lg0.dvrDescent
                    + " rectU=" + _rect.u + " rectV=" + _rect.v + " rectDU=" + _rect.du + " rectDV=" + _rect.dv);
            }
"""

TPC_E_UVSL_NEEDLE = """            VisualCollection visualChildren = visual.Children;

            Debug.Assert(!PTS.ToBoolean(textDetails.fLinesComposite));
"""
TPC_E_UVSL_REPL = """            VisualCollection visualChildren = visual.Children;

            Debug.Assert(!PTS.ToBoolean(textDetails.fLinesComposite));

            // ── `T-A44` 只读判别器（**造行支：UpdateViewport 派**）────────────────
            WpfLinuxLineVisProbe.Hit("UpdateViewportSimpleLines", _paraHandle, (int)textDetails.cLines,
                0, (int)textDetails.cAttachedObjects, -1,
                "intersects=" + (IntersectsWithRectOnV(ref viewport) ? 1 : 0)
                + " contained=" + (ContainedInRectOnV(ref viewport) ? 1 : 0)
                + " rectV=" + _rect.v + " rectDV=" + _rect.dv + " vpV=" + viewport.v + " vpDV=" + viewport.dv);
"""

TPC_E_SUDLV_NEEDLE = """        private void SyncUpdateDeferredLineVisuals(VisualCollection lineVisuals, ref PTS.FSTEXTDETAILSFULL textDetails, bool ignoreUpdateInfo)
        {
            Debug.Assert(!PTS.ToBoolean(textDetails.fLinesComposite));
"""
TPC_E_SUDLV_REPL = """        private void SyncUpdateDeferredLineVisuals(VisualCollection lineVisuals, ref PTS.FSTEXTDETAILSFULL textDetails, bool ignoreUpdateInfo)
        {
            Debug.Assert(!PTS.ToBoolean(textDetails.fLinesComposite));

            // ── `T-A44` 只读判别器（**造行支：ValidateVisual 的延迟派**）──────────
            WpfLinuxLineVisProbe.Hit("SyncUpdateDeferredLineVisuals", _paraHandle, (int)textDetails.cLines,
                0, (int)textDetails.cAttachedObjects, -1, null);
"""

TPC_E_TAIL_NEEDLE = """        private int _lineIndexFirstVisual = -1;

        #endregion Private Fields
    }
}
"""
TPC_E_TAIL_REPL = """        private int _lineIndexFirstVisual = -1;

        #endregion Private Fields
    }

    /// <summary>
    /// `T-A44`（`CONTENT-LINEVIS-BRANCH-REACH`）**内容段"造行支入口"只读判别器**。
    ///
    /// 【它是什么】一条**只读**台账：在 `TextParaClient` 的两支入口
    /// （`ValidateVisual`／`UpdateViewport`）与三个造行支
    /// （`RenderSimpleLines`／`UpdateViewportSimpleLines`／`SyncUpdateDeferredLineVisuals`）
    /// 各打**一行** `[TPCL]`，输出 `parah`（＝ native `[FSQTD] parah=` 的同一句柄）／
    /// `cLines`／`composite`／`att`／`deferred`（＝ `IsDeferredVisualCreationSupported` 的真值）
    /// ＋ 该点的门操作数。⇒ `T-A43 §5.1(a)` 要分辨的两因
    /// （因 A：`deferred=假`；因 B：`UpdateViewport` 未到内容段）**首次可现取**。
    /// 【它不做什么】不改任何行为：不动出参、不删／不放宽断言、不置任何 native 真值；
    /// `WPF_TPCL_PROBE=0` ⇒ 整个关掉（反极性腿：撤掉后与上游逐字同行为）。
    /// </summary>
    internal static class WpfLinuxLineVisProbe
    {
        private const int TraceMax = 60000;
        private static int _n;
        private static int _enabled = -1;                 // -1＝未读；0＝关；1＝开

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_TPCL_PROBE"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;         // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        private static string H(long v)
        {
            return "0x" + ((ulong)v).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
                // 打印失败不许改变行为（例如 stderr 已关闭）。
            }
        }

        internal static void Hit(string site, System.IntPtr para, int cLines, int composite, int att, int deferred, string extra)
        {
            if (!Enabled) { return; }
            if (_n >= TraceMax)
            {
                if (_n == TraceMax)
                {
                    _n++;
                    Emit("[TPCL] site=" + site + " trace=suppressed-after-" + TraceMax);
                }
                return;
            }
            _n++;
            Emit("[TPCL] site=" + site + " parah=" + H((long)para)
                 + " cLines=" + cLines + " composite=" + composite + " att=" + att + " deferred=" + deferred
                 + (extra == null ? "" : " " + extra)
                 + " NOINFO=tpcl-entry-readonly");
        }
    }
}
"""

TPC_EDITS = [
    (TPC_E_UV_NEEDLE, TPC_E_UV_REPL, 1),
    (TPC_E_VV_NEEDLE, TPC_E_VV_REPL, 1),
    (TPC_E_RSL_NEEDLE, TPC_E_RSL_REPL, 1),
    (TPC_E_LINEGEOM_NEEDLE, TPC_E_LINEGEOM_REPL, 1),
    (TPC_E_UVSL_NEEDLE, TPC_E_UVSL_REPL, 1),
    (TPC_E_SUDLV_NEEDLE, TPC_E_SUDLV_REPL, 1),
    (TPC_E_TAIL_NEEDLE, TPC_E_TAIL_REPL, 1),
]


# ── `T-A44`（`CONTENT-LINEVIS-BRANCH-REACH`）**视觉/视口链逐跳判别器**（`[CHAIN]`）────────────
#  【为什么需要第二层】`[TPCL]` 现取（`evidence-tail2j/disc1`）：**内容段（`cLines=1`）的
#    `ValidateVisual`／`UpdateViewport` 在整个 `FlowDocumentDemo` 阶段（762 轮）一次都没被调**
#    ⇒ `T-A43` 的两因里 **因 B 成立**（不是 `deferred` 门：入口根本没到）。但"入口为什么不到"
#    仍需逐跳定位 —— `TextParaClient` 的两支各有一条长的托管调用链：
#      · 视觉支：`FlowDocumentFormatter.Arrange` → `FlowDocumentPage.Arrange` → `EnsureValidVisuals`
#                → `UpdateVisual` → `PtsPage.GetPageVisual` → `PtsPage.UpdatePageVisuals`
#                → `PtsHelper.UpdateTrackVisuals` → `PtsHelper.UpdateParaListVisuals` → …
#      · 视口支：`FlowDocumentPage.UpdateViewport` → `PtsPage.UpdateViewport`
#                → `PtsHelper.UpdateViewportTrack` → `PtsHelper.UpdateViewportParaList` → …
#    本层在这条链的**每一跳入口**各打一行 `[CHAIN]`（**只读**：不动出参、不删／不放宽断言）。
#  【零假值】`WPF_CHAIN_PROBE=0` ⇒ 整个关掉（逐字回上游行为）。
CHAIN_PROBE_FILE = "WpfLinuxChainProbe.Linux.cs"
CHAIN_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A44（CONTENT-LINEVIS-BRANCH-REACH）：托管侧"视觉/视口链逐跳"只读判别器（`[CHAIN]`）。
//
// 【射程】只打行：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_CHAIN_PROBE=0` ⇒ 整个关掉（逐字回上游行为）。

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxChainProbe
    {
        private const int TraceMax = 200000;
        private static int _n;
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_CHAIN_PROBE"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;
                }
                return _enabled == 1;
            }
        }

        internal static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool EnvOn(string name)
        {
            try
            {
                string s = System.Environment.GetEnvironmentVariable(name);
                return (s == "0") ? false : true;      // 缺省＝开；**只有**显式 "0" 才关
            }
            catch (System.Exception)
            {
                return true;
            }
        }

        internal static string Hx(System.IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
            }
        }

        internal static void Hit(string site, string detail)
        {
            if (!Enabled) { return; }
            if (_n >= TraceMax)
            {
                if (_n == TraceMax)
                {
                    _n++;
                    Emit("[CHAIN] site=" + site + " trace=suppressed-after-" + TraceMax);
                }
                return;
            }
            _n++;
            Emit("[CHAIN] site=" + site + " " + (detail == null ? "" : detail) + " NOINFO=chain-entry-readonly");
        }
    }
}
'''

# ── `T-A69`（`PRECOND-NO-TEXT-SOURCE` · **内容源入站：字符序列**）托管侧喂料器 ─────────────
#  【为什么需要（现取）】native 侧**只接收、只保管、只回读**（`win32_pts.c` 的 `T-A69` 块）；字符序列的
#    **作者是托管**（源在宿主侧 `TextContainer`，`src/Linux/build/MilBridge/P1-layout-content-criteria.md:146` 在册）
#    ⇒ 必须由托管在**同一次调用窗内**把该段的字符序列交给 native（`PtsTextSrcFeed(parah, cp, pwch, cch)`），
#    随后**立刻回读** native 的逐字节副本并**与托管侧真值逐字节对拍**（逐字符 ＋ 独立算的 FNV-1a 64）。
#  【它是什么】一条**只读＋只入站**的台账：`TextParaClient.ValidateVisual` 内，对本段
#    （`Paragraph.Element` 的 `ContentStart..ContentEnd` 文本）喂**一次**（按 `_paraHandle` 去重 ＋ 全局
#    上限 64）；喂完即回读对拍，逐段打一行 `[TEXTSRC] mgd …`。
#  【它不做什么】不改任何出参、不删／不放宽断言、不置任何 native 真值、**不填任何几何**；
#    文本取不到（元素不是 `TextElement`／`TextRange` 抛）⇒ 记 `v=NO-SOURCE`，**不冒充**。
#    `WPF_TEXTSRC_FEED=0` ⇒ 整块不发生（逐字回上游行为 ⇒ 反极性腿）。
TEXTSRC_PROBE_FILE = "WpfLinuxTextSrcProbe.Linux.cs"
TEXTSRC_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A69（PRECOND-NO-TEXT-SOURCE · 内容源入站：字符序列）：托管侧喂料器 ＋ 逐字节对拍（[TEXTSRC]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_TEXTSRC_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxTextSrcProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_TEXTSRC_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFeed", ExactSpelling = true)]
        private static extern int PtsTextSrcFeed(IntPtr parah, int cpOff, IntPtr pwch, int cch);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFind", ExactSpelling = true)]
        private static extern int PtsTextSrcFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcCch", ExactSpelling = true)]
        private static extern int PtsTextSrcCch(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcChar", ExactSpelling = true)]
        private static extern int PtsTextSrcChar(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcState", ExactSpelling = true)]
        private static extern int PtsTextSrcState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcHash", ExactSpelling = true)]
        private static extern ulong PtsTextSrcHash(int k);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // FNV-1a 64：与 native 侧**逐字节**同法（UTF-16LE 字节流：低字节在前）⇒ 两边必须相等。
        private static ulong Fnv1a(string s)
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                h ^= (ulong)(c & 0xFF); h *= 1099511628211UL;
                h ^= (ulong)((c >> 8) & 0xFF); h *= 1099511628211UL;
            }
            return h;
        }

        // 把**本段落的字符序列**交给 native（一次／段），随即回读 native 的副本并逐字节对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int cp = 0;
            try
            {
                TextElement te = para.Element as TextElement;
                if (te == null) { Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                cp = para.ParagraphStartCharacterPosition;   // 该段的起始字符偏移（托管真值）
                int n = text.Length;
                IntPtr p = Marshal.StringToCoTaskMemUni(text);   // UTF-16 ＋ 终止 NUL
                int rc, k = -1, mism = -1;
                ulong want, got = 0UL;
                try
                {
                    rc = PtsTextSrcFeed(parah, cp, p, n);
                    _feeds++;
                    k = PtsTextSrcFind(parah);
                    mism = 0;
                    if (k < 0) { mism = -1; }
                    else if (PtsTextSrcCch(k) != n) { mism = -1; }      // 长度不符 ⇒ 必红
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (PtsTextSrcChar(k, i) != (int)text[i]) { mism++; }
                        }
                    }
                    want = Fnv1a(text);
                    got = (k >= 0) ? PtsTextSrcHash(k) : 0UL;
                }
                finally { Marshal.FreeCoTaskMem(p); }

                Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " cp=" + cp + " cch=" + n + " bytes=" + (n * 2)
                     + " rc=" + rc + " slot=" + k + " mism=" + mism
                     + " hash_match=" + ((want == got) ? 1 : 0)
                     + " state=" + ((k >= 0) ? PtsTextSrcState(k) : -1)
                     + " hash=" + got.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
                     + " v=CONTENT-SOURCE-INBOUND");
            }
            catch (System.Exception e)
            {
                Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " cp=" + cp + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
'''

# ── `T-A70`（契约 `C4` · **`cp↔dcp` 偏移由宿主给定**，native 只校不算）托管侧交付器 ─────────────
#  【为什么需要（现取）】契约 `C4`（`src/Linux/build/MilBridge/P1-ls-provenance-contract.md:72`）规定：`cp↔dcp`
#    偏移**由宿主给定**、native **只校不算**（"本侧**不得**自行推导"）。本移植里"宿主"＝本托管层
#    （`TextParaClient`）：它持有该段落的**字符位置**（`ParagraphStartCharacterPosition`）与**显示位置**
#    样本 ⇒ 必须由它把 **(cp, dcp) 样本对**在**同一次调用窗内**交给 native
#    （`PtsCpDcpMapFeed(parah, cpBase, n, cp, dcp)`），随后 native 当场校验（单调／端点／双射／守恒），
#    不符即**诚实拒绝**。
#  【它是什么】一条**只读＋只入站**的台账：`TextParaClient.ValidateVisual` 内，对本段喂**一次**样本对
#    （连续 `min(len, 16)` 点；按 `_paraHandle` 去重 ＋ 全局上限 64）；喂完即**回读** native 的样本副本
#    并**逐点对拍**，逐段打一行 `[CPDCMAP] mgd …`。
#  【诚实声明】本移植链上**无 LS 隐藏文本**（绕过 LS 造型）⇒ 本层"显示偏移"**真值 ＝ 0**（`dcp == cp`），
#    如实声明、不伪造非零偏移；本侧**不**由 `cp` 推 `dcp`（那正是契约禁止的"自算"）。
#  【它不做什么】不改任何出参、不删／不放宽断言、不置任何 native 真值、**不填任何几何**；
#    `WPF_CPDCMAP_FEED=0` ⇒ 整块不发生（逐字回上游行为 ⇒ 反极性腿）。
CPDCMAP_PROBE_FILE = "WpfLinuxCpDcpMapProbe.Linux.cs"
CPDCMAP_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A70（契约 C4 · cp↔dcp 偏移由宿主给定）：托管侧"宿主"侧映射交付器 ＋ 逐点对拍（[CPDCMAP]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_CPDCMAP_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxCpDcpMapProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxPts   = 16;                 // 单段最多 16 个样本点
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_CPDCMAP_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapFeed", ExactSpelling = true)]
        private static extern int PtsCpDcpMapFeed(IntPtr parah, int cpBase, int n, int[] cp, int[] dcp);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapFind", ExactSpelling = true)]
        private static extern int PtsCpDcpMapFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapNPoints", ExactSpelling = true)]
        private static extern int PtsCpDcpMapNPoints(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapCpBase", ExactSpelling = true)]
        private static extern int PtsCpDcpMapCpBase(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapOffset", ExactSpelling = true)]
        private static extern int PtsCpDcpMapOffset(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapState", ExactSpelling = true)]
        private static extern int PtsCpDcpMapState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapCpAt", ExactSpelling = true)]
        private static extern int PtsCpDcpMapCpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapDcpAt", ExactSpelling = true)]
        private static extern int PtsCpDcpMapDcpAt(int k, int i);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 把**本段落的 (cp,dcp) 偏移映射样本对**交给 native（一次／段），随即回读并逐点对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int cpBase = 0;
            try
            {
                TextElement te = para.Element as TextElement;
                if (te == null) { Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                cpBase = para.ParagraphStartCharacterPosition;   // 段落起始字符位置（宿主真值；端点锚）
                int len = text.Length;
                int n = (len < MaxPts) ? len : MaxPts;            // 连续前 n 个字符作样本点
                int[] cp  = new int[n];
                int[] dcp = new int[n];
                for (int i = 0; i < n; i++)
                {
                    cp[i]  = cpBase + i;
                    dcp[i] = cp[i];   // 本移植链上无 LS 隐藏文本 ⇒ 显示偏移真值 ＝ 0（如实声明，不伪造）
                }

                int rc = PtsCpDcpMapFeed(parah, cpBase, n, (n > 0) ? cp : null, (n > 0) ? dcp : null);
                _feeds++;
                int k = PtsCpDcpMapFind(parah);
                int mism = 0;
                if (k < 0) { mism = -1; }
                else if (PtsCpDcpMapNPoints(k) != n) { mism = -1; }       // 点数不符 ⇒ 必红
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (PtsCpDcpMapCpAt(k, i)  != cp[i])  { mism++; }
                        if (PtsCpDcpMapDcpAt(k, i) != dcp[i]) { mism++; }
                    }
                }

                Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " cp_base=" + cpBase + " n=" + n
                     + " rc=" + rc + " slot=" + k + " mism=" + mism
                     + " pair_match=" + ((k >= 0 && mism == 0) ? 1 : 0)
                     + " off=" + ((k >= 0) ? PtsCpDcpMapOffset(k) : -999)
                     + " state=" + ((k >= 0) ? PtsCpDcpMapState(k) : -1)
                     + " v=CP-DCP-MAP-INBOUND");
            }
            catch (System.Exception e)
            {
                Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " cp_base=" + cpBase + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
'''

# ── `T-A71`（`PRECOND-NO-TEXT-PARA-IN-CHAIN` · **文本段落进链**）托管侧交付器 ────────────────────
#  【为什么需要（现取）】在册前置 `src/Linux/build/MilBridge/P1-layout-content-criteria.md:114`：「`PRECOND-NO-TEXT-PARA-IN-CHAIN`：
#    现链上是容器段落，`TextParaClient` 是**另一族**」；`P1-tail2-dingrecon.md` §3.2 把它列为 `W` 的**第四条**。
#    ⇒ 必须让**文本段落**（`TextParaClient` 所代表的段）在 native 的**链**里可寻址、可逐段现取
#      （段身份／所属页／子轨／`cp` 域／内容源句柄），并与 `T-A69` 的内容源**一一对应**。
#  【它是什么】一条**只读＋只入站**的台账：`TextParaClient.ValidateVisual` 内，对本段喂**一次**
#    （按 `_paraHandle` 去重 ＋ 全局上限 64）：交出**段身份**（`_paraHandle`）＋ **页身份**（该段所在页的
#    同一性序号；同页同号）＋ **`cp` 域**（`[ParagraphStartCharacterPosition, +text.Length)`，宿主真值）。
#    native 侧**当场认领**（段身份必须是本侧链上的子轨对象 ⇒ 否则诚实拒绝 `not-in-chain`）并把
#    **内容源句柄**按同一段身份接到 `T-A69` 的内容源表上（⇒ 一一对应）；喂完即回读并逐格对拍，
#    逐段打一行 `[PARACHAIN] mgd …`。
#  【为什么落在托管侧】段身份／页身份／`cp` 域都在**宿主**手上（`BaseParaClient._paraHandle`／
#    `_pageContext`／`Paragraph.ParagraphStartCharacterPosition`）⇒ 只能由托管发起；native 只做
#    **入站 ＋ 认领校验 ＋ 源解析 ＋ 回读 ＋ 自检**（**不**自造任何一格）。
#  【它不做什么】不改任何出参、不删／不放宽断言、不置任何 native 真值、**不填任何几何**；
#    `WPF_PARACHAIN_FEED=0` ⇒ 整块不发生（逐字回上游行为 ⇒ 反极性腿）。
PARACHAIN_PROBE_FILE = "WpfLinuxTextParaChainProbe.Linux.cs"
PARACHAIN_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A71（PRECOND-NO-TEXT-PARA-IN-CHAIN · 文本段落进链）：托管侧交付器 ＋ 逐格对拍（[PARACHAIN]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_PARACHAIN_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxTextParaChainProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxPages = 256;                // 有界：页身份表的条目上界
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();
        // 页身份：**同一 PageContext 实例** ⇒ **同一序号**（引用相等；只作不透明 token 交 native）。
        private sealed class RefEq : System.Collections.Generic.IEqualityComparer<object>
        {
            bool System.Collections.Generic.IEqualityComparer<object>.Equals(object a, object b) { return object.ReferenceEquals(a, b); }
            int System.Collections.Generic.IEqualityComparer<object>.GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        private static readonly System.Collections.Generic.Dictionary<object, int> _pages =
            new System.Collections.Generic.Dictionary<object, int>(new RefEq());

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PARACHAIN_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainFeed", ExactSpelling = true)]
        private static extern int PtsParaChainFeed(IntPtr parah, int pageId, int cpFirst, int cpLim);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainFind", ExactSpelling = true)]
        private static extern int PtsParaChainFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainState", ExactSpelling = true)]
        private static extern int PtsParaChainState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainPageId", ExactSpelling = true)]
        private static extern int PtsParaChainPageId(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSubClaimed", ExactSpelling = true)]
        private static extern int PtsParaChainSubClaimed(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainCpFirst", ExactSpelling = true)]
        private static extern int PtsParaChainCpFirst(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainCpLim", ExactSpelling = true)]
        private static extern int PtsParaChainCpLim(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSrcSlot", ExactSpelling = true)]
        private static extern int PtsParaChainSrcSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSrcHandle", ExactSpelling = true)]
        private static extern IntPtr PtsParaChainSrcHandle(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainLinkOk", ExactSpelling = true)]
        private static extern int PtsParaChainLinkOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFind", ExactSpelling = true)]
        private static extern int PtsTextSrcFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcHandle", ExactSpelling = true)]
        private static extern IntPtr PtsTextSrcHandle(int k);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        private static int PageId(object pageCtx)
        {
            int id;
            if (_pages.TryGetValue(pageCtx, out id)) { return id; }
            if (_pages.Count >= MaxPages) { _pages.Clear(); }   // 有界（超出即重开表；页身份只在同窗内比较）
            id = _pages.Count + 1;
            _pages[pageCtx] = id;
            return id;
        }

        // 把**本段落的链坐标**（段身份／页身份／cp 域）交给 native（一次／段），随即回读并逐格对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para, object pageCtx)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int pageId = 0, cpFirst = 0, cpLim = 0;
            try
            {
                if (pageCtx == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-page-context"); return; }
                TextElement te = para.Element as TextElement;
                if (te == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                pageId  = PageId(pageCtx);
                cpFirst = para.ParagraphStartCharacterPosition;   // 段起始字符位置（宿主真值）
                cpLim   = cpFirst + text.Length;                  // 段末字符位置（开区间上界）

                int rc = PtsParaChainFeed(parah, pageId, cpFirst, cpLim);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**不在链上**／参数不自洽）：**不是**正读数 ⇒ 单列一行，供反腿与判据点名。
                    Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId + " cp=[" + cpFirst + "," + cpLim
                         + ") rc=" + rc + " v=REJECT-NOT-IN-CHAIN");
                    return;
                }
                int k = PtsParaChainFind(parah);
                int mism = 0;
                int ts   = PtsTextSrcFind(parah);
                IntPtr nth = (ts >= 0) ? PtsTextSrcHandle(ts) : IntPtr.Zero;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsParaChainState(k)      != 2)                     { mism++; }   // 必须 SOURCED
                    if (PtsParaChainPageId(k)     != pageId)                { mism++; }   // 页身份
                    if (PtsParaChainSubClaimed(k) != 1)                     { mism++; }   // 子轨可认领
                    if (PtsParaChainCpFirst(k)    != cpFirst)               { mism++; }   // cp 域下界
                    if (PtsParaChainCpLim(k)      != cpLim)                 { mism++; }   // cp 域上界
                    if (PtsParaChainSrcSlot(k)    != ts)                    { mism++; }   // 与 T-A69 一一对应（槽）
                    if (PtsParaChainSrcHandle(k)  != nth)                   { mism++; }   // 与 T-A69 一一对应（句柄）
                }
                int link = (k >= 0) ? PtsParaChainLinkOk(k) : 0;

                Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId
                     + " cp=[" + cpFirst + "," + cpLim + ") n=" + (cpLim - cpFirst)
                     + " rc=" + rc + " slot=" + k + " state=" + ((k >= 0) ? PtsParaChainState(k) : -1)
                     + " sub=" + ((k >= 0) ? PtsParaChainSubClaimed(k) : -1)
                     + " src_slot=" + ((k >= 0) ? PtsParaChainSrcSlot(k) : -999)
                     + " src=" + Hx((k >= 0) ? PtsParaChainSrcHandle(k) : IntPtr.Zero)
                     + " link=" + link + " mism=" + mism + " v=TEXT-PARA-IN-CHAIN");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
'''

# ── ⏪ `T-A72`（`PRECOND-LS-SESSION-DRIVER` · **LS 会话进链**）托管侧交付器 ─────────────────────
#  【它做什么】把 **LS 会话身份**（`LoCreateContext` 的 `ploc`，由 `NoteContext` 在 `CreatePTSContext`
#    窗内按 `docCtx → ploc` 记档）与**文本段落**（`TextParaClient._paraHandle`）在**同一个调用窗内**
#    交给 native（`WpfLinuxWin32_PtsLsSessFeed`），随即回读并**逐格对拍**（会话身份／所属段／回调面
#    指纹／两结构指针一致性），逐条打一行 `[LSSESS] mgd …`。
#  【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
#  【零假值】`WPF_LSSESS_FEED=0` ⇒ 整块不发生（逐字回上游行为 ⇒ 反极性腿）。
LSSESS_PROBE_FILE = "WpfLinuxLsSessionProbe.Linux.cs"
LSSESS_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A72（PRECOND-LS-SESSION-DRIVER · LS 会话进链）：托管侧会话↔段落交付器 ＋ 逐格对拍（[LSSESS]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_LSSESS_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxLsSessionProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxContexts = 256;             // 有界：上下文→会话 表的条目上界
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        // 上下文（native 文档上下文句柄）→ LS 会话身份（`LoCreateContext` 的 `ploc`）。
        private static readonly System.Collections.Generic.Dictionary<long, long> _sess =
            new System.Collections.Generic.Dictionary<long, long>();
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_LSSESS_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessFeed", ExactSpelling = true)]
        private static extern int PtsLsSessFeed(IntPtr ploc, IntPtr para);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessFind", ExactSpelling = true)]
        private static extern int PtsLsSessFind(IntPtr para);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCount", ExactSpelling = true)]
        private static extern int PtsLsSessCount();
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessState", ExactSpelling = true)]
        private static extern int PtsLsSessState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessSession", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessSession(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessPara", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessPara(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessInfoPtr", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessInfoPtr(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessRedefPtr", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessRedefPtr(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessPtrOk", ExactSpelling = true)]
        private static extern int PtsLsSessPtrOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfState", ExactSpelling = true)]
        private static extern int PtsLsSessCbfState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfMinNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfMinNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfRedefNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfRedefNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfLayOk", ExactSpelling = true)]
        private static extern int PtsLsSessCbfLayOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfFp", ExactSpelling = true)]
        private static extern ulong PtsLsSessCbfFp(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessSrcSlot", ExactSpelling = true)]
        private static extern int PtsLsSessSrcSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessChainSlot", ExactSpelling = true)]
        private static extern int PtsLsSessChainSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessParaLink", ExactSpelling = true)]
        private static extern int PtsLsSessParaLink(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessLiveSessions", ExactSpelling = true)]
        private static extern int PtsLsSessLiveSessions();
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessDistinctSessions", ExactSpelling = true)]
        private static extern int PtsLsSessDistinctSessions();

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 记档：本 PTS 文档上下文句柄 → 它的 **LS 会话身份**（`LoCreateContext` 的 `ploc`）。
        // 调用点 = `PtsCache.CreatePTSContext`（与 `LoCreateContext` **同一窗内**；见生成器内该块说明）。
        internal static void NoteContext(IntPtr docCtx, IntPtr ploc)
        {
            if (!Enabled || docCtx == IntPtr.Zero || ploc == IntPtr.Zero) { return; }
            if (_sess.Count >= MaxContexts) { _sess.Clear(); }   // 有界（超出即重开表）
            _sess[(long)docCtx] = (long)ploc;
            Emit("[LSSESS] ctx doc=" + Hx(docCtx) + " ploc=" + Hx(ploc) + " live=" + PtsLsSessLiveSessions()
                 + " v=LS-SESSION-NOTED");
        }

        // 把本段落的 `(会话, 段)` 在**同一窗内**交给 native，随即回读并逐格对拍。
        internal static void Feed(IntPtr docCtx, IntPtr parah)
        {
            if (!Enabled || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            long ploc = 0;
            try
            {
                if (docCtx == IntPtr.Zero || !_sess.TryGetValue((long)docCtx, out ploc) || ploc == 0)
                {
                    _done.Remove(key);
                    Emit("[LSSESS] mgd parah=" + Hx(parah) + " v=NO-SESSION reason=no-session-for-context");
                    return;
                }

                int rc = PtsLsSessFeed((IntPtr)ploc, parah);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**会话不在本侧登记表**／空参）：**不是**正读数 ⇒ 单列一行，供反腿与判据点名。
                    Emit("[LSSESS] mgd ploc=" + Hx((IntPtr)ploc) + " parah=" + Hx(parah)
                         + " rc=" + rc + " v=REJECT-NOT-IN-CHAIN");
                    return;
                }
                int k = PtsLsSessFind(parah);
                int mism = 0;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsLsSessSession(k)   != (IntPtr)ploc) { mism++; }   // 会话身份
                    if (PtsLsSessPara(k)      != parah)        { mism++; }   // 所属段
                    if (PtsLsSessState(k)     != 2)            { mism++; }   // 必须 BOUND
                    if (PtsLsSessParaLink(k)  != 1)            { mism++; }   // 段在两本台账里可解析
                    if (PtsLsSessPtrOk(k)     != 1)            { mism++; }   // 两结构指针一致性
                    if (PtsLsSessCbfState(k)  != 2)            { mism++; }   // 回调面已值化
                    if (PtsLsSessCbfNonzero(k) <= 0)           { mism++; }   // 回调面指纹：非零槽
                    if (PtsLsSessCbfLayOk(k)  != 1)            { mism++; }   // 回调面指纹：布局自证
                }

                Emit("[LSSESS] mgd ploc=" + Hx((IntPtr)ploc) + " para=" + Hx(parah)
                     + " rc=" + rc + " slot=" + k
                     + " info=" + Hx((k >= 0) ? PtsLsSessInfoPtr(k) : IntPtr.Zero)
                     + " redef=" + Hx((k >= 0) ? PtsLsSessRedefPtr(k) : IntPtr.Zero)
                     + " cbf_state=" + ((k >= 0) ? PtsLsSessCbfState(k) : -1)
                     + " nonzero=" + ((k >= 0) ? PtsLsSessCbfNonzero(k) : -1)
                     + " min=" + ((k >= 0) ? PtsLsSessCbfMinNonzero(k) : -1) + "/9"
                     + " redef_nz=" + ((k >= 0) ? PtsLsSessCbfRedefNonzero(k) : -1)
                     + " layok=" + ((k >= 0) ? PtsLsSessCbfLayOk(k) : -1)
                     + " fp=" + ((k >= 0) ? PtsLsSessCbfFp(k) : 0UL).ToString("x16")
                     + " ptr=" + ((k >= 0) ? PtsLsSessPtrOk(k) : -1)
                     + " src_slot=" + ((k >= 0) ? PtsLsSessSrcSlot(k) : -999)
                     + " chain_slot=" + ((k >= 0) ? PtsLsSessChainSlot(k) : -999)
                     + " link=" + ((k >= 0) ? PtsLsSessParaLink(k) : -1)
                     + " state=" + ((k >= 0) ? PtsLsSessState(k) : -1)
                     + " npara=" + PtsLsSessCount()
                     + " nsess=" + PtsLsSessDistinctSessions()
                     + " mism=" + mism + " v=LS-SESSION-IN-CHAIN");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[LSSESS] mgd parah=" + Hx(parah) + " v=NO-SESSION reason=exception:" + e.GetType().Name);
            }
        }
    }
}
'''

# ── `T-A45`（`LINEVIS-ON-SCREEN`）**在屏页＝有限页**驱动 ────────────────────────────────
#  【现取断点（见 `P1-tail2-onscreen-impl-report.md`）】在屏的是 `FlowDocumentScrollViewer`
#    （`[GEO] TabItem hdr=流文档滚动视图 sel=True`；`[FSVIEW] scroll=1`）⇒ 它走的是**底流**
#    （`FlowDocumentFormatter` → `FlowDocumentPage.FormatBottomless` → native `FsCreatePageBottomless`）。
#    而**底流窗里** `<Figure>` 被 `TextParagraph.GetAttachedObjects`（条件
#    `textElement is Figure && StructuralCache.CurrentFormatContext.FinitePage`）改判成
#    `FloaterParagraph`（现取：`[FSATT-PROBE] where=FsCreatePageBottomless … att0_id=2`＝Floater），
#    其内容只能经 native `FSFLOATERCBK`（`pfnFormatFloaterContentBottomless`）排版 —— 而
#    `GetFloaterHandlerInfo` 在本移植是**具名 GAP**（`return wpf_pts_gap(...)`）⇒ 内容子页永不建
#    ⇒ 在屏页的附属对象**只有背景**（`GhostWhite` 29667 px）、**内容色 0**。
#    同一份文档的**有限页**（`FsCreatePageFinite`）**能**把 `Figure` 内容排出来（现取
#    `[FSATT-CONTENT] where=FsCreatePageFinite … v=SUBPAGE-CREATED`）⇒ 在屏页改用有限页即可让
#    内容视觉落在**在屏**的那一页上（`T-A44` 的驱动把行视觉建在**离屏**的分页器有限页上，帧面不动）。
#  【驱动是什么】**只**把本 formatter 的 `FormatBottomless` 换成 `FormatFinite`（起始断行记录 `null`
#    ＝第一页），页高取 `max(constraint.Height, 2000)`（本页内容约 320 DIP ⇒ 单页装得下）。
#    · **不**改任何 native 几何；· **不**改 `fsupdinf`／`fUpdateInfoForLinesPresent`；
#    · **不**删／**不**放宽任何 `Invariant.Assert`（若仍被走到，断言照旧响亮 —— 那是新缺陷）。
#  【零假值】`WPF_LINEVIS_ONSCREEN=0` ⇒ **逐字回上游行为**（反极性腿；缺省＝开）。
#
#  ── 同趟第二处（在屏页的**段落背景视觉落位**，`TextParaClient.ValidateVisual`）────────────
#  【现取】在屏页改成有限页之后（第一处驱动），`Figure` 内容段**确已**落到在屏视觉树
#    （现取：`[CHAIN] FIG.ValidateVisual` 0→3、`DarkGreen` 0→44 px、帧 `2d89d393157b0df6`
#    →`b99e402a49ea739e`）⇒「行视觉上屏」成立。但**四具名色的第三条**仍缺：`Beige`
#    （＝`<Figure>` 内那个 `<Paragraph Background="Beige">` 的**背景**）恒 `0`。
#  【为什么是 0（件:行）】`TextParaClient.ValidateVisual`（生成件 `:66`）**从不调**
#    `ParagraphVisual.DrawBackgroundAndBorder` —— 全仓现取：该面**只**被 `Figure`／`Floater`／
#    `List`／`Container`／`Subpage`／`Table`／`UIElement` 七个客户端调用（`grep -rn
#    'DrawBackgroundAndBorder' upstream/**/PtsHost/` 逐条现取）⇒ **文本段落的 `Background`
#    视觉没有任何挂点**（本移植的行渲染器也不画 run 背景）⇒ 该声明色**永不上屏**。
#  【修法】**照 `FigureParaClient.ValidateVisual` 的同形调用**把它补在文本段落上（同一面、
#    同一 `ParagraphVisual`，**只**在 `Background` 非空时调）⇒ `<Paragraph Background="Beige">`
#    的背景**首次**落进在屏视觉树。**不**删／**不**放宽任何断言；`Background` 为空 ⇒ 零变化。
#  【零假值】同一闸 `WPF_LINEVIS_ONSCREEN`：`=0` ⇒ 该调用整块不发生（逐字回上游）。
ONS_FORMAT_NEEDLE = """                    _document.StructuralCache.BackgroundFormatInfo.ViewportHeight = constraint.Height;
                    _documentPage.FormatBottomless(pageSize, pageMargin);
"""
ONS_FORMAT_REPL = """                    _document.StructuralCache.BackgroundFormatInfo.ViewportHeight = constraint.Height;
                    // ── `T-A45`（`LINEVIS-ON-SCREEN`）：**把"在屏页"从底流改为有限页** ──────────────
                    //  见生成器内该块的说明；`WPF_LINEVIS_ONSCREEN=0` ⇒ 逐字回上游（反极性腿）。
                    WpfLinuxOnScreenDrive.Format(pageSize, constraint, pageMargin, _documentPage);
"""
ONS_TAIL_NEEDLE = """        #endregion IFlowDocumentFormatter Members
    }
}
"""
ONS_TAIL_REPL = """        #endregion IFlowDocumentFormatter Members
    }

    /// <summary>
    /// `T-A45`（`LINEVIS-ON-SCREEN`）：**在屏页＝有限页**驱动（本移植的底流窗**排不出**附属对象内容，
    /// 见 `reapply-patches.py` 内该块的说明）。零假值：显式 `WPF_LINEVIS_ONSCREEN=0` ⇒ 逐字回上游。
    /// </summary>
    internal static class WpfLinuxOnScreenDrive
    {
        private const double MinimumPageHeight = 2000.0;

        internal static void Format(Size pageSize, Size constraint, Thickness pageMargin, FlowDocumentPage page)
        {
            if (!WpfLinuxChainProbe.EnvOn("WPF_LINEVIS_ONSCREEN"))
            {
                FormatBottomlessDegradable(pageSize, pageMargin, page, "ONS.FormatBottomless(upstream-leg)");
                return;
            }

            Size finiteSize = pageSize;
            double height = constraint.Height;
            if (double.IsNaN(height) || double.IsInfinity(height) || height < MinimumPageHeight)
            {
                height = MinimumPageHeight;
            }
            finiteSize.Height = height;

            WpfLinuxChainProbe.Hit("ONS.FormatFinite",
                "w=" + WpfLinuxChainProbe.N(finiteSize.Width) + " h=" + WpfLinuxChainProbe.N(finiteSize.Height));

            // ── `D-G206`：**托管侧降级** ────────────────────────────────────────────────
            //  本移植没有完整 PTS（native `win32_pts.c` 大量入口如实返回 `WPF_PTS_ERR_NOT_IMPLEMENTED`），
            //  `FormatFinite`（有限页）这条路会撞上未实现的入口 ⇒ 上游 `PTS.Validate()` 抛 `PtsException`。
            //  修前该异常冒泡到 UI 线程 ⇒ FlowDocument 类控件**整体不可用**（五种形态全炸）。
            //  这里降级为「**回退底流**（bottomless）」—— 底流是本移植"能出内容"的形态（滚动/编辑场景够用），
            //  只是分页不精确。零假值：`WPF_PTS_STRICT=1` ⇒ 原样重抛（反极性对照）；
            //  非 PTS 缺口的异常 ⇒ 同样原样重抛（不掩盖真 bug）。
            try
            {
                page.FormatFinite(finiteSize, pageMargin, null);
            }
            catch (Exception ex)
            {
                if (!PtsDegrade.ShouldSwallow(ex, "ONS.FormatFinite"))
                {
                    throw;
                }

                FormatBottomlessDegradable(pageSize, pageMargin, page, "ONS.FormatBottomless(fallback-after-Finite)");
            }
        }

        /// <summary>
        /// 底流格式化，带 `D-G206` 降级：PTS 缺口 ⇒ 吞掉（已记账）；`WPF_PTS_STRICT=1` 或非 PTS 异常 ⇒ 重抛。
        /// </summary>
        private static void FormatBottomlessDegradable(
            Size pageSize, Thickness pageMargin, FlowDocumentPage page, string where)
        {
            try
            {
                page.FormatBottomless(pageSize, pageMargin);
            }
            catch (Exception ex)
            {
                if (!PtsDegrade.ShouldSwallow(ex, where))
                {
                    throw;
                }
            }
        }
    }
}
"""


# ══════════════════════════════════════════════════════════════════════════════
#  `T-B12`（`PAGINATED-PAGE-CONTENT-VISUALS`）：分页页视觉「壳内」内容视觉的
#  **只读逐跳读数**（`[PAGEVIS]`）＋ 新生成件类本体。
#
#  【现取断点（承 `T-B11`）】在屏 `DocumentPageView`／`DocumentPageHost` 收到的页视觉
#    （`DPV.page=0x3ee7093 pv=0x361f531`）**壳内为空**（`[DPH] SUB=… L3:k=0,b=empty`）；
#    而同趟 `FlowDocumentView` 那一页（`pv=0x2a6fa61`）**叶子有真内容**。
#    `[CHAIN]` 现取：分页页在 `FDPaginator.FormatPage` 里**确已**走
#    `FDG.UpdateVisual(needsUpdate=1) → PTSP.GetPageVisual → PTSP.UpdatePageVisuals`
#    并一路下潜到内容段（`RenderSimpleLines cLines=1`）。⇒ 需要逐跳问：
#      · 那次构建**到底把哪个对象**塞进了页视觉（`_visual`／`trackVisual` 身份）；
#      · 之后**谁**把它搬走（`UpdateParaListVisuals` 的 `fskupdNew` 支会把段视觉从**旧父**摘走）；
#      · 页销毁（`FDG.Dispose` 的 `PageVisual.Children.Clear()`）是否发生。
#  【它做什么】**只打行**：在每个钩子点读身份／子数／包围盒／逐层子树。
#    **不**改任何出参、**不**删／**不**放宽任何 `Invariant.Assert`、**不**置任何 native 真值、**不**碰几何。
#  【零假值】`WPF_PAGEVIS_PROBE=0` ⇒ **整块不发生**（逐字回上游行为 ⇒ 反极性腿）。
PAGEVIS_PROBE_FILE = "WpfLinuxPageVisProbe.Linux.cs"
PAGEVIS_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-B12（PAGINATED-PAGE-CONTENT-VISUALS）：分页页视觉「壳内」内容视觉的**只读**逐跳读数（[PAGEVIS]）。
//
// 【射程】只打行：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值、不碰几何。
// `WPF_PAGEVIS_PROBE=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxPageVisProbe
    {
        private const int TraceMax = 20000;
        private static int _n;
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_PAGEVIS_PROBE"); }
                    catch (Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;
                }
                return _enabled == 1;
            }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            try
            {
                return "0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "NA"; }
        }

        internal static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string B(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetDescendantBounds(v);
                return r.IsEmpty ? "empty" : (N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height));
            }
            catch (Exception) { return "NA"; }
        }

        private static string C(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetContentBounds(v);
                return r.IsEmpty ? "empty" : (N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height));
            }
            catch (Exception) { return "NA"; }
        }

        private static int K(Visual v)
        {
            if (v == null) { return -1; }
            try { return VisualTreeHelper.GetChildrenCount(v); } catch (Exception) { return -2; }
        }

        private static void SubRec(Visual v, int lvl, int maxLvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > maxLvl || budget <= 0) { return; }
            budget--;
            int k = K(v);
            sb.Append("L").Append(lvl).Append(':').Append(Id(v)).Append(",k=").Append(k)
              .Append(",b=").Append(B(v)).Append(",c=").Append(C(v)).Append(" | ");
            for (int i = 0; i < k && i < 8; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (Exception) { c = null; }
                SubRec(c, lvl + 1, maxLvl, sb, ref budget);
            }
        }

        internal static void Report(string site, string detail, Visual v)
        {
            if (!Enabled) { return; }
            if (_n >= TraceMax)
            {
                if (_n == TraceMax) { _n++; Emit("[PAGEVIS] site=" + site + " trace=suppressed-after-" + TraceMax); }
                return;
            }
            _n++;
            var sb = new System.Text.StringBuilder();
            int budget = 40;
            try { SubRec(v, 0, 3, sb, ref budget); } catch (Exception) { sb.Append("NA"); }
            Emit("[PAGEVIS] site=" + site + " " + detail + " vis=" + Id(v) + " kids=" + K(v)
                 + " b=" + B(v) + " c=" + C(v) + " SUB=" + sb.ToString()
                 + " NOINFO=pagevis-readonly");
        }

        private static void Emit(string line)
        {
            try { Console.Error.WriteLine(line); Console.Error.Flush(); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// `T-B12`（`PAGINATED-PAGE-CONTENT-VISUALS`）：**在屏页"被搬空"修复**的**驱动闸**。
    /// 逐跳现取见 `reapply-patches.py` 内该块的说明。`WPF_PAGEPAGE_REDRIVE=0` ⇒ 关（反极性腿）。
    /// </summary>
    internal static class WpfLinuxPageVisDrive
    {
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_PAGEPAGE_REDRIVE"); }
                    catch (Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;         // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        /// <summary>
        /// `T-B12`：把"本页**造视觉那一刻**"的 `trackVisual` 子视觉**引用**记下来（显示时换父回去用）。
        /// ⚠️ **不**复制视觉、**不**改任何 native 真值、**不**触发任何 `FsQuery*`（只读托管视觉树）。
        /// </summary>
        internal static void KeepVisuals(ContainerVisual trackVisual, ref System.Collections.Generic.List<Visual> keep)
        {
            if (!Enabled) { return; }
            if (trackVisual == null) { return; }
            int n = trackVisual.Children.Count;
            if (n == 0) { return; }
            var list = new System.Collections.Generic.List<Visual>(n);
            for (int i = 0; i < n; i++) { list.Add(trackVisual.Children[i]); }
            keep = list;
        }

        /// <summary>
        /// `T-B12`：把 `keep` 里的视觉**换父**到 `target`（先把它们从各自旧父摘除，再按序 `Insert`）。
        /// **只**在 `target` 为空时用。**不**查 native、**不**调 `ValidateVisual`（子树内容原样搬回）。
        /// </summary>
        internal static void ReparentInto(ContainerVisual target, System.Collections.Generic.List<Visual> keep)
        {
            if (!Enabled) { return; }
            if (target == null || keep == null || keep.Count == 0) { return; }
            for (int i = 0; i < keep.Count; i++)
            {
                Visual v = keep[i];
                if (v == null) { continue; }
                ContainerVisual oldParent = VisualTreeHelper.GetParent(v) as ContainerVisual;
                if (oldParent != null) { oldParent.Children.Remove(v); }
            }
            for (int i = 0; i < keep.Count; i++)
            {
                Visual v = keep[i];
                if (v == null) { continue; }
                target.Children.Insert(i, v);
            }
        }
    }
}
'''

# 只读钩子（**只增一行**；needle 均落在既有编辑**不碰**的区域）：
PAGEVIS_GETPAGEVIS_NEEDLE = """            else
            {
                _visual.Children.Clear();
            }
            return _visual;
        }
"""
PAGEVIS_GETPAGEVIS_REPL = """            else
            {
                _visual.Children.Clear();
            }
            WpfLinuxPageVisProbe.Report("PTSP.GetPageVisual.ret", "empty=" + (IsEmpty ? 1 : 0), _visual);
            return _visual;
        }
"""

PAGEVIS_UPDVIS_NEEDLE = """                PtsHelper.UpdateTrackVisuals(PtsContext, trackVisual.Children, pageDetails.fskupd, ref pageDetails.u.simple.trackdescr);
            }
"""
PAGEVIS_UPDVIS_REPL = """                PtsHelper.UpdateTrackVisuals(PtsContext, trackVisual.Children, pageDetails.fskupd, ref pageDetails.u.simple.trackdescr);
                WpfLinuxPageVisProbe.Report("PTSP.UpdPageVis.simple", "fskupd=" + (int)fskupd + " track=" + WpfLinuxPageVisProbe.Id(trackVisual), trackVisual);
                WpfLinuxPageVisDrive.KeepVisuals(trackVisual, ref _pageContentKeep);
            }
"""

PAGEVIS_UPDPLV_NEEDLE = """                    // New paragraph - insert new visual node
                    visualCollection.Insert(index, paraClient.Visual);
"""
PAGEVIS_UPDPLV_REPL = """                    // New paragraph - insert new visual node
                    WpfLinuxPageVisProbe.Report("PH.UpdParaList.new", "idx=" + index
                        + " oldParent=" + WpfLinuxPageVisProbe.Id(currentParent), paraClient.Visual);
                    visualCollection.Insert(index, paraClient.Visual);
"""

PAGEVIS_FDG_CHILD_NEEDLE = """                this.PageVisual.Child = pageVisual; // No-op if already connected.
"""
PAGEVIS_FDG_CHILD_REPL = """                this.PageVisual.Child = pageVisual; // No-op if already connected.
                WpfLinuxPageVisProbe.Report("FDG.UpdateVisual.child", "needsUpdate=1 pageId=" + WpfLinuxPageVisProbe.Id(this), pageVisual);
"""

PAGEVIS_FDG_DISPOSE_NEEDLE = """                        DestroyVisualLinks(this.PageVisual);

                        // Clear its drawing context and children collection.
                        this.PageVisual.Children.Clear();
"""
PAGEVIS_FDG_DISPOSE_REPL = """                        DestroyVisualLinks(this.PageVisual);
                        WpfLinuxPageVisProbe.Report("FDG.Dispose.clear", "pageId=" + WpfLinuxPageVisProbe.Id(this), this.PageVisual);

                        // Clear its drawing context and children collection.
                        this.PageVisual.Children.Clear();
"""

# ── `T-B12` 驱动①：`PtsPage` 侧的"在屏页被搬空 ⇒ 接回本页"（同源＝`fskupdNew` 支的动作序列）────
PAGEVIS_REDRIVE_NEEDLE = """            WpfLinuxPageVisProbe.Report("PTSP.GetPageVisual.ret", "empty=" + (IsEmpty ? 1 : 0), _visual);
            return _visual;
        }
"""
PAGEVIS_REDRIVE_REPL = """            WpfLinuxPageVisProbe.Report("PTSP.GetPageVisual.ret", "empty=" + (IsEmpty ? 1 : 0), _visual);
            return _visual;
        }

        // ── `T-B12`（`PAGINATED-PAGE-CONTENT-VISUALS`）：**在屏页的"被搬空"修复** ──────────
        //  【现取机制（逐跳）】分页器造页时，同一 `BaseParaClient.Visual` 会被**后来的页**沿用：
        //   `PtsHelper.UpdateParaListVisuals` 的 `fskupdNew` 支**先"从旧父摘除"再 `Insert`**
        //   （现取 `[PAGEVIS] site=PH.UpdParaList.new oldParent=<本页 trackVisual>`）；
        //   而一个 `Visual` **只能有一个父** ⇒ **先造的那一页的 `trackVisual` 被搬空**。
        //   现场：`tab2` 页 `0x93377a` 的 `trackVisual=0x2d22d7e` 在 `PTSP.UpdPageVis.simple`
        //   时 `kids=1`（`L1:0x2948ddd,k=3`，**有内容**），到 `[DPH] site=Attach` 时已成
        //   `L3:k=0,b=empty`（**空**）—— `DocumentPageView` 显示的恰是这一页 ⇒ 帧面"壳内为空"。
        //  【修法（**同源**）】本页**非空 ∧ `fSimple` ∧ `trackVisual` 恰被搬空**时，用**同一条**造视觉
        //   路径（`PtsHelper.RedriveParaListVisuals` ＝ 上游 `fskupdNew` 支的逐段
        //   `从旧父摘除 → Insert → ValidateVisual(New)`）把**本页自己的**段落视觉**接回本页**。
        //   · **不**改任何 native 真值（几何／更新信息／`fskupd` **一律不写**；`fskupdNew` 只是**托管侧入参**）；
        //   · **不**删／**不**放宽任何 `Invariant.Assert`；
        //   · 只在"确已被搬空（`trackVisual.Children.Count == 0`）"时动手 ⇒ 否则**零动作**。
        //  【零假值】`WPF_PAGEPAGE_REDRIVE=0` ⇒ **整块不发生**（逐字回上游行为 ⇒ 反极性腿）。
        //  ⚠️ **不查 native**（页的 PTS 句柄可能已陈旧 ⇒ `FsQuery*` 会抛）：本页在**造视觉那一刻**
        //     把 `trackVisual` 的**子视觉引用**记下来（`_pageContentKeep`），显示时只做**托管侧的换父**。
        //     失败**如实打一行**、不重抛（它是**可选修复**路径 —— 不许它盖掉页面自身的显示，也不许静默）。
        private System.Collections.Generic.List<Visual> _pageContentKeep;
        private System.Collections.Generic.List<Visual> _pageFloatKeep;

        internal void RedrivePageVisualsForDisplay()
        {
            if (!WpfLinuxPageVisDrive.Enabled) { return; }
            if (_pageContentKeep == null && _pageFloatKeep == null) { return; }
            if (_visual == null || IsEmpty) { return; }
            if (_visual.Children.Count != 2) { return; }
            try
            {
                bool did = false;
                ContainerVisual pageContentVisual = _visual.Children[0] as ContainerVisual;
                ContainerVisual floatingVisual = _visual.Children[1] as ContainerVisual;
                if (pageContentVisual != null && pageContentVisual.Children.Count == 1)
                {
                    ContainerVisual trackVisual = pageContentVisual.Children[0] as ContainerVisual;
                    if (trackVisual != null && trackVisual.Children.Count == 0)
                    {
                        WpfLinuxPageVisDrive.ReparentInto(trackVisual, _pageContentKeep);
                        did = true;
                    }
                }
                if (floatingVisual != null && floatingVisual.Children.Count == 0)
                {
                    WpfLinuxPageVisDrive.ReparentInto(floatingVisual, _pageFloatKeep);
                    did = true;
                }
                if (did)
                {
                    WpfLinuxPageVisProbe.Report("PTSP.RedrivePageVisuals",
                        "content=" + ((_pageContentKeep == null) ? 0 : _pageContentKeep.Count)
                        + " float=" + ((_pageFloatKeep == null) ? 0 : _pageFloatKeep.Count), _visual);
                }
            }
            catch (System.Exception e)
            {
                WpfLinuxPageVisProbe.Report("PTSP.RedrivePageVisuals",
                    "outcome=exception type=" + e.GetType().Name, null);
            }
        }
"""

# ── `T-B12` 驱动③：在"读页视觉"的**显示路径**上跑一次（`UpdateVisual` 之后；建出即非空 ⇒ 零动作）──
PAGEVIS_FLOATKEEP_NEEDLE = """            PtsHelper.UpdateFloatingElementVisuals(floatingElementsVisual, _pageContextOfThisPage.FloatingElementList);
        }
"""
PAGEVIS_FLOATKEEP_REPL = """            PtsHelper.UpdateFloatingElementVisuals(floatingElementsVisual, _pageContextOfThisPage.FloatingElementList);
            WpfLinuxPageVisDrive.KeepVisuals(floatingElementsVisual, ref _pageFloatKeep);
        }
"""
PAGEVIS_REDRIVE_CALL_NEEDLE = """                UpdateVisual();
                return base.Visual;
"""
PAGEVIS_REDRIVE_CALL_REPL = """                UpdateVisual();
                // ── `T-B12` 驱动：在屏页"被搬空"修复（见生成器内该块的说明；默认开，`WPF_PAGEPAGE_REDRIVE=0` 关）
                //   只挂在"**读** `DocumentPage.Visual`"这一条显示路径上（`FlowDocumentView` 的
                //   `EnsureValidVisuals` 不经过这里 ⇒ `tab1` 的链一字不动）。
                if (_ptsPage != null)
                {
                    _ptsPage.RedrivePageVisualsForDisplay();
                }
                return base.Visual;
"""


# 每跳的 (needle, repl, expect)。全部 **只增一行**。
CHAIN_FILES = [
    ("MS/Internal/PtsHost/FlowDocumentPage.cs", "FlowDocumentPage.Linux.cs", [
        # ── `T-B19`：渲染遍历入口用的**轻量**修复入口（只跑 `T-B12` 修复体；不读 native、不 `UpdateVisual`）──
        #   放在列表**首位**：此刻文件仍是**上游原文**，锚点即上游原文。
        ("""        internal void Arrange(Size partitionSize)
        {
""",
         """        // T-B19：`DocumentPageView.GetVisualChild`（渲染/命中遍历入口）用的轻量入口。
        internal void RedrivePageVisualsOnly()
        {
            if (_ptsPage != null) { _ptsPage.RedrivePageVisualsForDisplay(); }
        }

        internal void Arrange(Size partitionSize)
        {
""", 1),
        ("""        internal void Arrange(Size partitionSize)
        {
""",
         """        internal void Arrange(Size partitionSize)
        {
            WpfLinuxChainProbe.Hit("FDG.Arrange", "size=" + WpfLinuxChainProbe.N(partitionSize.Width) + "x" + WpfLinuxChainProbe.N(partitionSize.Height));
""", 1),
        ("""        internal void EnsureValidVisuals()
        {
            Invariant.Assert(!IsDisposed);
            UpdateVisual();
""",
         """        internal void EnsureValidVisuals()
        {
            Invariant.Assert(!IsDisposed);
            WpfLinuxChainProbe.Hit("FDG.EnsureValidVisuals", "needsUpdate=" + (_visualNeedsUpdate ? 1 : 0));
            UpdateVisual();
""", 1),
        ("""        internal void UpdateViewport(ref PTS.FSRECT viewport, bool drawBackground)
        {
            Rect contentViewport;
""",
         """        internal void UpdateViewport(ref PTS.FSRECT viewport, bool drawBackground)
        {
            WpfLinuxChainProbe.Hit("FDG.UpdateViewport", "dbg=" + (drawBackground ? 1 : 0) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            Rect contentViewport;
""", 1),
        ("""        private void UpdateVisual()
        {
            if (this.PageVisual == null)
            {
                SetVisual(new PageVisual(this));
            }
            if (_visualNeedsUpdate)
""",
         """        private void UpdateVisual()
        {
            if (this.PageVisual == null)
            {
                SetVisual(new PageVisual(this));
            }
            WpfLinuxChainProbe.Hit("FDG.UpdateVisual", "needsUpdate=" + (_visualNeedsUpdate ? 1 : 0)
                + " pageId=0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this).ToString("x", System.Globalization.CultureInfo.InvariantCulture)
                + " pvId=" + ((this.PageVisual == null) ? "null" : ("0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.PageVisual).ToString("x", System.Globalization.CultureInfo.InvariantCulture))));
            if (_visualNeedsUpdate)
""", 1),
        # ── `T-B12` 只读钩子（分页页视觉「壳内」内容）────────────────────────────
        (PAGEVIS_FDG_CHILD_NEEDLE, PAGEVIS_FDG_CHILD_REPL, 1),
        (PAGEVIS_FDG_DISPOSE_NEEDLE, PAGEVIS_FDG_DISPOSE_REPL, 1),
        # ── `T-B12` 驱动③：在屏页"被搬空"修复的调用点 ────────────────────────────
        (PAGEVIS_REDRIVE_CALL_NEEDLE, PAGEVIS_REDRIVE_CALL_REPL, 1),
    ]),
    ("MS/Internal/PtsHost/PtsPage.cs", "PtsPage.Linux.cs", [
        ("""        internal ContainerVisual GetPageVisual()
        {
            if (_visual == null)
""",
         """        internal ContainerVisual GetPageVisual()
        {
            WpfLinuxChainProbe.Hit("PTSP.GetPageVisual", "empty=" + (IsEmpty ? 1 : 0) + " visual=" + (_visual == null ? 0 : 1));
            if (_visual == null)
""", 1),
        ("""        private void UpdatePageVisuals(Size arrangeSize)
        {
""",
         """        private void UpdatePageVisuals(Size arrangeSize)
        {
            WpfLinuxChainProbe.Hit("PTSP.UpdatePageVisuals", "size=" + WpfLinuxChainProbe.N(arrangeSize.Width) + "x" + WpfLinuxChainProbe.N(arrangeSize.Height));
""", 1),
        ("""        internal void UpdateViewport(ref PTS.FSRECT viewport)
        {
            if (!IsEmpty)
""",
         """        internal void UpdateViewport(ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("PTSP.UpdateViewport", "empty=" + (IsEmpty ? 1 : 0) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            if (!IsEmpty)
""", 1),
        # ── `T-B12` 只读钩子（分页页视觉「壳内」内容）────────────────────────────
        (PAGEVIS_GETPAGEVIS_NEEDLE, PAGEVIS_GETPAGEVIS_REPL, 1),
        (PAGEVIS_UPDVIS_NEEDLE, PAGEVIS_UPDVIS_REPL, 1),
        # ── `T-B12` 驱动①：在屏页"被搬空 ⇒ 接回本页" ─────────────────────────────
        (PAGEVIS_REDRIVE_NEEDLE, PAGEVIS_REDRIVE_REPL, 1),
        (PAGEVIS_FLOATKEEP_NEEDLE, PAGEVIS_FLOATKEEP_REPL, 1),
    ]),
# ── `T-A47`（`FLOAT-REPARENT`）：浮层视觉**换父** ─────────────────────────────────
#  【现取断点（`T-A47` 诊断腿 `~/tA47-work/diag1`，全栈探针 ＋ 逐 `Add` 身份探针）】：
#    残留 1 条 `[HC-UNHANDLED] ArgumentException: Specified Visual is already a child …`，
#    首帧 `VisualCollection.Add`；抛出点 ＝ **`PtsHelper.UpdateFloatingElementVisuals`** 的
#    `visualChildren.Add(paraVisual)`（本树内唯一"非新建视觉"的 `.Add`；其余 `.Add` 全是 `new …`）。
#    实测身份：**同一个 `FigureParaClient` 实例**先被页 B 的浮层收纳（其 `Visual` 的父＝页 B 的
#    `ContainerVisual`），随后页 C（**另一个 `PtsPage` 对象**）的浮层再 `.Add` 同一 `Visual`
#    ⇒ `_parent != null` ⇒ 抛。
#  【为什么本移植会这样（根因在 native 侧，如实点名）】上游 WPF 里每个页的 `FsQueryAttachedObjectList`
#    交回的是**该页对象图所属**的 `pfsparaclient`（页销毁 ⇒ `Dispose()` ⇒ `RemoveFloatingParaClient`）
#    ⇒ "一个 `BaseParaClient` 同时活在两页的 `FloatingElementList` 里"这一形态**不会出现**；
#    而本移植的附属对象台账（`win32_pts.c` 的 `fl_att[].obj_client`）是 **doc 级、跨页复用**的
#    ⇒ 同一客户端先后在**两个页上下文**里被 `ArrangeFigure/Floater`（`AddFloatingParaClient` 各加一次）
#    ⇒ 其唯一 `Visual` 被两个浮层争用。**native 侧的"页级客户端"是具名下一靶**（不改；见载体 §6）。
#  【修法】**照上游自己的同形先例做换父**：`PtsHelper.UpdateParaListVisuals` 在 `fskupdNew` 支里
#    就**先**把 `paraClient.Visual` 从其旧父摘掉（含同款 `Invariant.Assert(parent is ContainerVisual)`）
#    再 `Insert` —— 本块把**同一个惯用法**补到浮层支上（上游浮层支**假定**"不会换父"）。
#    ⚠️ **不是**把失败吞掉：若旧父**不是** `ContainerVisual`，本块与上游一样**响亮断言**；
#      视觉确实被**搬到**当前正在构建的那一层的语义下（旧页随即 `FsDestroyPage`）。
#  【零假值】`WPF_FLOAT_REPARENT=0` ⇒ **整块不发生**（逐字回上游 ⇒ 反极性腿上残留必回 `[HC-UNHANDLED]=1`）。
    ("MS/Internal/PtsHost/PtsHelper.cs", "PtsHelper.Linux.cs", [
        ("""        internal static void UpdateTrackVisuals(
            PtsContext ptsContext,
            VisualCollection visualCollection,
            PTS.FSKUPDATE fskupdInherited,
            ref PTS.FSTRACKDESCRIPTION trackDesc)
        {
            PTS.FSKUPDATE fskupd = trackDesc.fsupdinf.fskupd;
""",
         """        internal static void UpdateTrackVisuals(
            PtsContext ptsContext,
            VisualCollection visualCollection,
            PTS.FSKUPDATE fskupdInherited,
            ref PTS.FSTRACKDESCRIPTION trackDesc)
        {
            PTS.FSKUPDATE fskupd = trackDesc.fsupdinf.fskupd;
            WpfLinuxChainProbe.Hit("PH.UpdateTrackVisuals", "fskupd=" + (int)trackDesc.fsupdinf.fskupd + " inh=" + (int)fskupdInherited + " pfstrack=" + WpfLinuxChainProbe.Hx(trackDesc.pfstrack));
""", 1),
        # ── `T-B4`：**段落 `v` 逐段现取台账**（`[APLV]`）──────────────────────────────────
        #  【为什么需要】`T-B3` 已让 native `FSPARADESCRIPTION.dvrUsed` 带真段高（`[FSQSPL-DVR]`
        #    `per_para_dvrUsed=[0:33528,1:25146,2:25146] v_rel=[0:0,1:33528,2:58674]`），但帧面读数
        #    `[TPCL] rectV=`（`TextParaClient._rect.v`）**逐段仍为 0** ⇒ "几何真而帧不变"的断点
        #    落在"**哪一处 arrange** 真的消费了这条列表"上，而这一跳**本侧无读数**。
        #  【它是什么】在 `ArrangeParaList` 内**每段**打一行：`n`（本列表长度）／`idx`／
        #    `rcTrackContent.v`／`dvrUsed`／`rcPara.v`（＝宿主真算出来的那个值）＋ `parah`。
        #    与 native `[FSQSPL-DVR]` 的 `per_para_dvrUsed` 逐条对拍 ⇒ "消费/未消费"可判。
        #  【它不做什么】不动出参、不改任何算术（`rcPara.v` 在**本行已算完之后**才读）。
        #    `WPF_CHAIN_PROBE=0` ⇒ 整块不发生（逐字回上游行为）。
        ("""        internal static void ArrangeParaList(
            PtsContext ptsContext,
            PTS.FSRECT rcTrackContent,
            PTS.FSPARADESCRIPTION [] arrayParaDesc,
            uint fswdirTrack)
        {
            // For each paragraph, do following:
            // (1) Retrieve ParaClient object
            // (2) Arrange and update paragraph metrics
            int dvrPara = 0;
""",
         """        internal static void ArrangeParaList(
            PtsContext ptsContext,
            PTS.FSRECT rcTrackContent,
            PTS.FSPARADESCRIPTION [] arrayParaDesc,
            uint fswdirTrack)
        {
            // For each paragraph, do following:
            // (1) Retrieve ParaClient object
            // (2) Arrange and update paragraph metrics
            int dvrPara = 0;
            WpfLinuxChainProbe.Hit("PH.ArrangeParaList-in", "n=" + arrayParaDesc.Length + " rcTrackContent=" + rcTrackContent.u + "," + rcTrackContent.v + "," + rcTrackContent.du + "," + rcTrackContent.dv);
""", 1),
        ("""                rcPara.v += dvrPara + dvrTopSpace;
                rcPara.dv = arrayParaDesc[index].dvrUsed - dvrTopSpace;
""",
         """                rcPara.v += dvrPara + dvrTopSpace;
                rcPara.dv = arrayParaDesc[index].dvrUsed - dvrTopSpace;
                WpfLinuxChainProbe.Hit("PH.ArrangeParaList", "n=" + arrayParaDesc.Length + " idx=" + index
                    + " parah=" + WpfLinuxChainProbe.Hx(arrayParaDesc[index].pfspara)
                    + " rcContent.v=" + rcTrackContent.v + " dvrPara=" + dvrPara
                    + " dvrUsed=" + arrayParaDesc[index].dvrUsed + " dvrTopSpace=" + dvrTopSpace
                    + " rcPara.v=" + rcPara.v + " rcPara.dv=" + rcPara.dv);
""", 1),
        ("""        internal static void UpdateParaListVisuals(
            PtsContext ptsContext,
            VisualCollection visualCollection,
            PTS.FSKUPDATE fskupdInherited,
            PTS.FSPARADESCRIPTION [] arrayParaDesc)
        {
            // For each paragraph, do following:
""",
         """        internal static void UpdateParaListVisuals(
            PtsContext ptsContext,
            VisualCollection visualCollection,
            PTS.FSKUPDATE fskupdInherited,
            PTS.FSPARADESCRIPTION [] arrayParaDesc)
        {
            WpfLinuxChainProbe.Hit("PH.UpdateParaListVisuals", "n=" + arrayParaDesc.Length + " inh=" + (int)fskupdInherited);
            // For each paragraph, do following:
""", 1),
        ("""        internal static void UpdateViewportTrack(
            PtsContext ptsContext,
            ref PTS.FSTRACKDESCRIPTION trackDesc,
            ref PTS.FSRECT viewport)
        {
            // There is possibility to get empty track. (example: large figures)
            if (trackDesc.pfstrack != IntPtr.Zero)
""",
         """        internal static void UpdateViewportTrack(
            PtsContext ptsContext,
            ref PTS.FSTRACKDESCRIPTION trackDesc,
            ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("PH.UpdateViewportTrack", "pfstrack=" + WpfLinuxChainProbe.Hx(trackDesc.pfstrack) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            // There is possibility to get empty track. (example: large figures)
            if (trackDesc.pfstrack != IntPtr.Zero)
""", 1),
        ("""        internal static void UpdateViewportParaList(
            PtsContext ptsContext,
            PTS.FSPARADESCRIPTION [] arrayParaDesc,
            ref PTS.FSRECT viewport)
        {
            for (int index = 0; index < arrayParaDesc.Length; index++)
""",
         """        internal static void UpdateViewportParaList(
            PtsContext ptsContext,
            PTS.FSPARADESCRIPTION [] arrayParaDesc,
            ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("PH.UpdateViewportParaList", "n=" + arrayParaDesc.Length);
            for (int index = 0; index < arrayParaDesc.Length; index++)
""", 1),
        ("""                    Visual paraVisual = floatingElementList[index].Visual;

                    while(visualIndex < visualChildren.Count && visualChildren[visualIndex] != paraVisual)
                    {
                        visualChildren.RemoveAt(visualIndex);
                    }

                    if(visualIndex == visualChildren.Count)
                    {
                        visualChildren.Add(paraVisual);
                    }
""",
         """                    Visual paraVisual = floatingElementList[index].Visual;

                    while(visualIndex < visualChildren.Count && visualChildren[visualIndex] != paraVisual)
                    {
                        visualChildren.RemoveAt(visualIndex);
                    }

                    if(visualIndex == visualChildren.Count)
                    {
                        // ── `T-A47`（`FLOAT-REPARENT`）：**浮层视觉的换父** ────────────────────────
                        //  见生成器内该块的说明；`WPF_FLOAT_REPARENT=0` ⇒ 逐字回上游（反极性腿）。
                        if (WpfLinuxChainProbe.EnvOn("WPF_FLOAT_REPARENT"))
                        {
                            Visual t47Parent = VisualTreeHelper.GetParent(paraVisual) as Visual;
                            if (t47Parent != null)
                            {
                                ContainerVisual t47Cv = t47Parent as ContainerVisual;
                                Invariant.Assert(t47Cv != null, "parent should always derives from ContainerVisual");
                                t47Cv.Children.Remove(paraVisual);
                                WpfLinuxChainProbe.Hit("PH.FloatingReparent", "idx=" + index
                                    + " from=" + t47Parent.GetType().Name + " to=" + visual.GetType().Name);
                            }
                        }
                        visualChildren.Add(paraVisual);
                    }
""", 1),
        # ── `T-B12` 只读钩子（段视觉**从旧父被摘走**的逐跳见证）──────────────────
        (PAGEVIS_UPDPLV_NEEDLE, PAGEVIS_UPDPLV_REPL, 1),
    ]),
    ("MS/Internal/PtsHost/FigureParaClient.cs", "FigureParaClient.Linux.cs", [
        ("""        internal override void UpdateViewport(ref PTS.FSRECT viewport)
        {
            // Query subpage details
            PTS.FSSUBPAGEDETAILS subpageDetails;
""",
         """        internal override void UpdateViewport(ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("FIG.UpdateViewport", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            // Query subpage details
            PTS.FSSUBPAGEDETAILS subpageDetails;
""", 1),
        ("""        internal override void ValidateVisual(PTS.FSKUPDATE fskupdInherited)
        {
            // Figure is always reported as NEW. Override PTS inherited value.
""",
         """        internal override void ValidateVisual(PTS.FSKUPDATE fskupdInherited)
        {
            WpfLinuxChainProbe.Hit("FIG.ValidateVisual", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " inh=" + (int)fskupdInherited);
            // Figure is always reported as NEW. Override PTS inherited value.
""", 1),
        # ── `T-A45` 只读几何判别器（**附属对象盒／剪裁盒**）──────────────────────────────
        ("""            PTS.FSRECT clipRect = new PTS.FSRECT(_paddingRect.u - _contentRect.u, _paddingRect.v - _contentRect.v, _paddingRect.du, _paddingRect.dv);
            PtsHelper.ClipChildrenToRect(_visual, clipRect.FromTextDpi());
""",
         """            PTS.FSRECT clipRect = new PTS.FSRECT(_paddingRect.u - _contentRect.u, _paddingRect.v - _contentRect.v, _paddingRect.du, _paddingRect.dv);
            ContainerVisual t45cv0 = (_visual.Children.Count > 0) ? (_visual.Children[0] as ContainerVisual) : null;
            WpfLinuxChainProbe.Hit("FIG.Geom", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle)
                + " rect=" + _rect.u + "," + _rect.v + "," + _rect.du + "," + _rect.dv
                + " content=" + _contentRect.u + "," + _contentRect.v + "," + _contentRect.du + "," + _contentRect.dv
                + " padding=" + _paddingRect.u + "," + _paddingRect.v + "," + _paddingRect.du + "," + _paddingRect.dv
                + " clip=" + clipRect.u + "," + clipRect.v + "," + clipRect.du + "," + clipRect.dv
                + " off0=" + (t45cv0 == null ? "na" : (WpfLinuxChainProbe.N(t45cv0.Offset.X) + "," + WpfLinuxChainProbe.N(t45cv0.Offset.Y))));
            PtsHelper.ClipChildrenToRect(_visual, clipRect.FromTextDpi());
""", 1),
    ]),
    ("MS/Internal/PtsHost/ContainerParaClient.cs", "ContainerParaClient.Linux.cs", [
        ("""        internal override void ValidateVisual(PTS.FSKUPDATE fskupdInherited)
        {
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
""",
         """        internal override void ValidateVisual(PTS.FSKUPDATE fskupdInherited)
        {
            WpfLinuxChainProbe.Hit("CON.ValidateVisual", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " inh=" + (int)fskupdInherited);
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
""", 1),
        ("""        internal override void UpdateViewport(ref PTS.FSRECT viewport)
        {
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
""",
         """        internal override void UpdateViewport(ref PTS.FSRECT viewport)
        {
            WpfLinuxChainProbe.Hit("CON.UpdateViewport", "parah=" + WpfLinuxChainProbe.Hx(_paraHandle) + " vp=" + viewport.u + "," + viewport.v + "," + viewport.du + "," + viewport.dv);
            // Query paragraph details
            PTS.FSSUBTRACKDETAILS subtrackDetails;
""", 1),
    ]),
    ("MS/Internal/documents/FlowDocumentFormatter.cs", "FlowDocumentFormatter.Linux.cs", [
        ("""        internal void Arrange(Size arrangeSize, Rect viewport)
        {
""",
         """        internal void Arrange(Size arrangeSize, Rect viewport)
        {
            WpfLinuxChainProbe.Hit("FDF.Arrange", "size=" + WpfLinuxChainProbe.N(arrangeSize.Width) + "x" + WpfLinuxChainProbe.N(arrangeSize.Height) + " vp=" + WpfLinuxChainProbe.N(viewport.X) + "," + WpfLinuxChainProbe.N(viewport.Y) + "," + WpfLinuxChainProbe.N(viewport.Width) + "," + WpfLinuxChainProbe.N(viewport.Height));
""", 1),
        # ── `T-A45`（`LINEVIS-ON-SCREEN`）**在屏页＝有限页**驱动 ＋ 其只读台账 ───────────────
        (ONS_FORMAT_NEEDLE, ONS_FORMAT_REPL, 1),
        (ONS_TAIL_NEEDLE, ONS_TAIL_REPL, 1),
    ]),
    # ── `T-A44` **驱动**（方案 3）：分页器这条宿主路径缺的"页视觉帧"那一跳 ────────────────────
    #  【现取断点】`FlowDocumentFormatter.Arrange`（另一条宿主路径）在 `Arrange` 之后**必**再调
    #    `EnsureValidVisuals()`（上游次序：`Arrange` → `EnsureValidVisuals` → `UpdateViewport`）；
    #    而分页器这条路（`FlowDocumentPaginator.FormatPage`）**只调 `Arrange`** ⇒ 该页对象的
    #    `_visualNeedsUpdate`（由 `FormatFinite` 的 `OnAfterFormatPage` 置真）**永不被消费**
    #    ⇒ 页视觉帧（`GetPageVisual`／`UpdatePageVisuals`）不建 ⇒ `UpdateTrackVisuals`
    #    → `UpdateParaListVisuals` → 段落的 `ValidateVisual` 全不发生（`[CHAIN]`／`[TPCL]` 现取）。
    #  【驱动是什么】在 `Arrange` 之后补一次 `EnsureValidVisuals()`（**幂等**：`UpdateVisual` 自看
    #    `_visualNeedsUpdate`）。**不**伪造任何几何、**不**删／**不**放宽任何 `Invariant.Assert`。
    #  【零假值】`WPF_LINEVIS_DRIVE=0` ⇒ 整个撤掉（反极性腿；缺省＝开）。
    ("MS/Internal/documents/FlowDocumentPaginator.cs", "FlowDocumentPaginator.Linux.cs", [
        ("""            breakRecordOut = page.FormatFinite(pageSize, pageMargin, breakRecordIn);
            page.Arrange(pageSize);

            // NOTE: May execute external code, so it is possible to get
""",
         """            breakRecordOut = page.FormatFinite(pageSize, pageMargin, breakRecordIn);
            page.Arrange(pageSize);

            // ── `T-A44`（`CONTENT-LINEVIS-BRANCH-REACH`）**驱动：补上"页视觉帧"缺的那一跳** ──
            //  见生成器内该块的说明；`WPF_LINEVIS_DRIVE=0` ⇒ 逐字回上游（反极性腿）。
            if (WpfLinuxChainProbe.EnvOn("WPF_LINEVIS_DRIVE"))
            {
                WpfLinuxChainProbe.Hit("DRIVE.EnsurePageVisuals", "site=FDPaginator.FormatPage page=" + page.GetHashCode());
                page.EnsureValidVisuals();
            }

            // NOTE: May execute external code, so it is possible to get
""", 1),
        # ── `D-G206` **托管侧降级**：分页器造页时 PTS 引擎未实现的入口不再打死分页 ────────────
        #  【抛点（`WPF_PTS_TRAP=1` 现取）】后台分页 `OnBackgroundPagination` → `FormatPage` →
        #    `FlowDocumentPage.Arrange` → `PtsPage.ArrangePage` → `PtsHelper.ArrangeTrack/ParaList`
        #    → `PtsHelper.ParaListFromSubtrack` → `PTS.Validate(FsQuerySubtrackDetails)` ⇒
        #    `PtsException("Page formatting engine did not complete formatting operation. Error code: '-10000'.")`
        #    ⇒ 冒泡到 `Dispatcher.ProcessQueue` ⇒ `DispatcherUnhandledException` ⇒ 五形态里
        #    `FlowDocumentPageViewer`/`FlowDocumentReader`/`DocumentPaginator` 全部不可用。
        #  【为什么不直接返回 `DocumentPage.Missing`】`FormatPagesTill` 的循环条件是
        #    `while (!_brt.HasPageBreakRecord(pageNumber) && !_brt.IsClean) FormatPage(_brt.Count);`
        #    ⇒ 若失败后**不** `_brt.UpdateEntry`，**必然死循环**。所以降级语义是：
        #    **吞掉 PTS 缺口、跳过出错的那一步（页可能内容不全/空白）、但让分页表照常前进**。
        #  【零假值】`WPF_PTS_STRICT=1` ⇒ 全部原样重抛（反极性腿）；非 PTS 缺口的异常同样原样重抛。
        ("""            breakRecordOut = page.FormatFinite(pageSize, pageMargin, breakRecordIn);
            page.Arrange(pageSize);
""",
         """            // ── `D-G206`：`FormatFinite` / `Arrange` 遇 PTS 未实现入口 ⇒ 降级（不抛、页可能空白）──
            try
            {
                breakRecordOut = page.FormatFinite(pageSize, pageMargin, breakRecordIn);
            }
            catch (Exception exBreak)
            {
                if (!PtsDegrade.ShouldSwallow(exBreak, "FDPaginator.FormatPage.FormatFinite"))
                {
                    throw;
                }
                breakRecordOut = breakRecordIn;
            }

            try
            {
                page.Arrange(pageSize);
            }
            catch (Exception exArrange)
            {
                if (!PtsDegrade.ShouldSwallow(exArrange, "FDPaginator.FormatPage.Arrange"))
                {
                    throw;
                }
            }
""", 1),
        ("""                WpfLinuxChainProbe.Hit("DRIVE.EnsurePageVisuals", "site=FDPaginator.FormatPage page=" + page.GetHashCode());
                page.EnsureValidVisuals();
""",
         """                WpfLinuxChainProbe.Hit("DRIVE.EnsurePageVisuals", "site=FDPaginator.FormatPage page=" + page.GetHashCode());
                try
                {
                    page.EnsureValidVisuals();
                }
                catch (Exception exVis)
                {
                    if (!PtsDegrade.ShouldSwallow(exVis, "FDPaginator.FormatPage.EnsureValidVisuals"))
                    {
                        throw;
                    }
                }
""", 1),
        # ── `D-G206` **最后一道兜底**：后台分页这一层是 DispatcherOperation 的入口 ──────────────
        #  【为什么还要这一层】`FormatPage` 里已包住 `FormatFinite`/`Arrange`/`EnsureValidVisuals`，
        #    但 `OnBackgroundPagination` 循环里还会经 `AsyncRequest.Process()` 与
        #    `PtsPage.UpdatePageVisuals`/`GetPageVisual` 等**其它**PTS 入口（`WPF_PTS_TRAP=1` 现取：
        #    第 3 次 `PtsHelper.ParaListFromSubtrack` 从 `PtsPage.UpdatePageVisuals` 冒出来）。
        #    抛出点一旦离开本方法就会进 `Dispatcher.ProcessQueue` ⇒ `DispatcherUnhandledException`
        #    ⇒ 整个分页文档不可用。故在此再兜一层：**只吞 PTS 缺口**、其余原样重抛。
        #  【零假值】`WPF_PTS_STRICT=1` ⇒ 原样重抛（反极性腿）。
        ("""                finally
                {
                    _document.StructuralCache.IsFormattingInProgress = false;    // Clear reentrancy flag.
                }
            }

            return null;
        }

        /// <summary>
        /// Compute size for the page.
""",
         """                catch (Exception exBg)
                {
                    // `D-G206`：PTS 引擎未实现的入口 ⇒ 降级（已记账），不要让 Dispatcher 抛出去。
                    if (!PtsDegrade.ShouldSwallow(exBg, "FDPaginator.OnBackgroundPagination"))
                    {
                        throw;
                    }
                }
                finally
                {
                    _document.StructuralCache.IsFormattingInProgress = false;    // Clear reentrancy flag.
                }
            }

            return null;
        }

        /// <summary>
        /// Compute size for the page.
""", 1),
    ]),
    # ── ⏪ `T-B7`（`BAML-TYPE-UNRESOLVED`）：BAML 类型解析失败的**具名出口** ────────────────
    #  【它做什么】`Baml2006SchemaContext.ResolveBamlType` 解析不出类型时，上游抛的是**无参**
    #    `NotImplementedException`（消息恒为 "The method or operation is not implemented."）——
    #    **没有类型名、没有宿主程序集**；而它外面还套着 `System.Xaml` 的 InitializationGuard /
    #    ProvideValue 三层 `XamlParseException` ⇒ 现场（hc demo）只剩不可读的 5 条 `[HC-UNHANDLED]`。
    #  【本处只补信息，不改语义】仍抛 `NotImplementedException`（调用方逐字不动），
    #    但带上 类型名 / 程序集 id / 程序集名，并打一条**只读**诊断行 `[BAML-TYPE-UNRESOLVED]`。
    #  【零假值】只加打印与异常消息，不吞异常、不改解析结果、不碰任何 `Invariant.Assert`。
    ("System/Windows/Markup/Baml2006/Baml2006SchemaContext.cs", "Baml2006SchemaContext.Linux.cs", [
        ("""            throw new NotImplementedException();
""",
         """            // ── WPF-on-Linux `T-B7`：BAML 类型解析失败的**具名出口**（只加名字，不改语义）──
            //   上游此处是**无参** `NotImplementedException` ⇒ 日志只剩一句
            //   "The method or operation is not implemented."（**没有类型名、没有宿主件**）；
            //   现场 hc demo 里它被三层 XamlParseException 套娃包住 ⇒ 完全不可读。
            //   本处**只补信息**：仍抛 `NotImplementedException`（调用方语义一字不动），
            //   但带上 类型名 / 程序集 id / 程序集名，并打一条**只读**诊断行 ⇒ 失败不许静默。
            string _wpfBamlAsm = null;
            try { _wpfBamlAsm = GetAssemblyName(bamlType.AssemblyId); }
            catch (System.Exception) { _wpfBamlAsm = null; }
            System.Console.Error.WriteLine(
                "[BAML-TYPE-UNRESOLVED] type='" + bamlType.Name + "'"
                + " assemblyId=" + bamlType.AssemblyId
                + " assembly='" + (_wpfBamlAsm ?? "<unknown>") + "'"
                + " —— 该类型在其 BAML 记录的宿主程序集里 GetType 取不到"
                + "（常见因：宿主件**版本/身份**与 BAML 记录不一致，或该类型未编入本移植）");
            System.Console.Error.Flush();
            throw new NotImplementedException(
                "BAML type not resolvable: '" + bamlType.Name + "'"
                + " (assemblyId=" + bamlType.AssemblyId + ", assembly='" + (_wpfBamlAsm ?? "<unknown>") + "')");
""", 1),
    ]),
]


# ── ⏪ `T-A73`（`PRECOND-NO-LINE-BREAKER` · **行断器**）托管侧触发器 ──────────────────────────────
#  【它做什么】在 `TextParaClient.ValidateVisual` 窗内，按**段身份**触发 native 的**行断点台账**收口
#    （`WpfLinuxWin32_PtsLineBreakFeed`）—— native 当场：认领段（`T-A71` 进链牙）＋ 核内容源（`T-A69`）
#    ＋ 读**托管真排版**交回的行记录（`fl_line[]`，`T-A33` 回填源）⇒ 产出**断点 cp 序列 ＋ 逐断点最小
#    `LsLInfo` 骨架**，并把 native 从**内容源**自算的**强制断行**计数（`n_hard`）与真排版行数（`n_brk`）
#    做**跨域计数不变量**对拍（`n_brk >= n_hard`）＋ 真断点**域内自洽**对拍（严格递增／首断点 > 0）。
#    ⇒ 随后托管**立即回读并逐格对拍**，逐条打一行 `[LINEBREAK] mgd …`。
#  【射程】只触发 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 `Invariant.Assert`、不置任何 native 真值。
#  【零假值】`WPF_LINEBREAK_FEED=0` ⇒ 整块不发生（逐字回上游行为 ⇒ 反极性腿）。
LBREAK_PROBE_FILE = "WpfLinuxLineBreakProbe.Linux.cs"
LBREAK_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A73（PRECOND-NO-LINE-BREAKER · 行断器）：托管侧触发器 ＋ 逐格回读对拍（[LINEBREAK]）。
//
// 【射程】只触发 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_LINEBREAK_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxLineBreakProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多触发 64 段
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_LINEBREAK_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFeed", ExactSpelling = true)]
        private static extern int PtsLineBreakFeed(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFind", ExactSpelling = true)]
        private static extern int PtsLineBreakFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakState", ExactSpelling = true)]
        private static extern int PtsLineBreakState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakSeq", ExactSpelling = true)]
        private static extern int PtsLineBreakSeq(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpFirst", ExactSpelling = true)]
        private static extern int PtsLineBreakCpFirst(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpLim", ExactSpelling = true)]
        private static extern int PtsLineBreakCpLim(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCch", ExactSpelling = true)]
        private static extern int PtsLineBreakCch(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakNHard", ExactSpelling = true)]
        private static extern int PtsLineBreakNHard(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakNBrk", ExactSpelling = true)]
        private static extern int PtsLineBreakNBrk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakMono", ExactSpelling = true)]
        private static extern int PtsLineBreakMono(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFirstOk", ExactSpelling = true)]
        private static extern int PtsLineBreakFirstOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCoverOk", ExactSpelling = true)]
        private static extern int PtsLineBreakCoverOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakDcpAt", ExactSpelling = true)]
        private static extern int PtsLineBreakDcpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpAt", ExactSpelling = true)]
        private static extern int PtsLineBreakCpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakInfo", ExactSpelling = true)]
        private static extern int PtsLineBreakInfo(int k, int i, int field);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 按**段身份**触发 native 的行断点台账收口（一次／段），随即回读并逐格对拍。
        internal static void FeedParagraph(IntPtr parah)
        {
            if (!Enabled || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            try
            {
                int rc = PtsLineBreakFeed(parah);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**不在链上**／无内容源／无行台账／断点不符）：**不是**正读数 ⇒ 单列一行。
                    Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " rc=" + rc + " v=REJECT-LINE-BREAKER");
                    return;
                }
                int k  = PtsLineBreakFind(parah);
                int nb = (k >= 0) ? PtsLineBreakNBrk(k) : -1;
                int nh = (k >= 0) ? PtsLineBreakNHard(k) : -1;
                int mism = 0;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsLineBreakState(k)   != 2) { mism++; }     // 必须 VALUE
                    if (PtsLineBreakMono(k)    != 1) { mism++; }     // 域内自洽：严格递增
                    if (PtsLineBreakFirstOk(k) != 1) { mism++; }     // 域内自洽：首断点 > 0
                    if (PtsLineBreakCoverOk(k) != 1) { mism++; }     // 跨域计数：n_brk >= n_hard
                    if (nb <= 0)                     { mism++; }
                    for (int i = 0; i < nb; i++)
                    {
                        // cp 序列 = cp_first + dcp（逐断点）
                        if (PtsLineBreakCpAt(k, i)  != PtsLineBreakCpFirst(k) + PtsLineBreakDcpAt(k, i)) { mism++; }
                        // 最小 LsLInfo 骨架：cpLim ＝ 该断点 cp
                        if (PtsLineBreakInfo(k, i, 1) != PtsLineBreakCpAt(k, i)) { mism++; }
                        // 最小 LsLInfo 骨架：cpFirstVis ＝ 该行起始 cp（i==0 ⇒ cp_first）
                        if (PtsLineBreakInfo(k, i, 0) != ((i == 0) ? PtsLineBreakCpFirst(k) : PtsLineBreakCpAt(k, i - 1))) { mism++; }
                        // 最小 LsLInfo 骨架：fFirstLineInPara ＝ (i==0)
                        if (PtsLineBreakInfo(k, i, 2) != ((i == 0) ? 1 : 0)) { mism++; }
                    }
                }

                Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " rc=" + rc + " slot=" + k
                     + " cp=[" + ((k >= 0) ? PtsLineBreakCpFirst(k) : -1) + "," + ((k >= 0) ? PtsLineBreakCpLim(k) : -1) + ")"
                     + " cch=" + ((k >= 0) ? PtsLineBreakCch(k) : -1)
                     + " n_brk=" + nb + " n_hard=" + nh
                     + " mono=" + ((k >= 0) ? PtsLineBreakMono(k) : -1)
                     + " first=" + ((k >= 0) ? PtsLineBreakFirstOk(k) : -1)
                     + " cover=" + ((k >= 0) ? PtsLineBreakCoverOk(k) : -1)
                     + " dcp0=" + ((k >= 0) ? PtsLineBreakDcpAt(k, 0) : -1)
                     + " dcpN=" + ((k >= 0) ? PtsLineBreakDcpAt(k, nb - 1) : -1)
                     + " seq=" + ((k >= 0) ? PtsLineBreakSeq(k) : -1)
                     + " mism=" + mism + " v=LINE-BREAKER-SET");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=exception:" + e.GetType().Name);
            }
        }
    }
}
'''


# ══════════════════════════════════════════════════════════════════════════════
#  `T-B13`（`PRECOND-TAB3-PAGE-HOST`）：`FlowDocumentReader`（hc「流文档查看器」）的**页宿主**接线
# ══════════════════════════════════════════════════════════════════════════════
#  【现取的断点】上游 `FlowDocumentReader.GetViewerFromMode`（件:行
#    `upstream/wpf/.../Controls/FlowDocumentReader.cs:1150-1156`）给内部的 `ReaderPageViewer`
#    （`MS/Internal/documents/IFlowDocumentViewer.cs:452`：`FlowDocumentPageViewer` 子类，
#     ＝「查看器」的内容宿主）显式设
#    `Style` ＝ `ComponentResourceKey(typeof(PresentationUIStyleResources), "PUIPageViewStyleKey")`
#    （键定义见同件 `:2043-2050`）。该资源的**唯一**出处是 PresentationUI 的主题字典
#    （`upstream/wpf/.../PresentationUI/Themes/Generic.xaml:8675-8699`），其模板里含 `DocumentPageView`。
#    本移植的 PresentationUI 是**替身**（`src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.PresentationUI`，**无** `Themes/`、
#    无 `PresentationUIStyleResources` 的类型字典）⇒ 该键解析为空；而 `Style` 已被**本地设值**
#    （`SetResourceReference`）⇒ 连类型隐式样式也被绕过 ⇒ `ReaderPageViewer` **没有 `ControlTemplate`**
#    ⇒ 它**从不构造 `DocumentPageView`** ⇒ 分页出来的 `DocumentPage.Visual` **没有宿主**去读
#    ⇒「查看器」区**空白**（tab2／tab1 各走别的宿主，故不受影响）。
#
#  【修（`T-B12` 同范式：同一机制 ＋ 可撤 ＋ 只读台账）】把那一行换成
#    `MS.Internal.Documents.WpfLinuxReaderPageHost.ApplyPageViewerStyle(_pageViewer, PageViewStyleKey)`：
#    「先**只读**查该键 ⇒ 查到＝照旧设资源引用（逐字回上游）；查不到＝**补一份与上游等价的模板**
#      （页宿主 `DocumentPageView`）」。⚠️ 不复制视觉、不改几何、不碰 native、不删／不放宽任何 `Invariant.Assert`。
#    闸 `WPF_READER_PAGEHOST`（**缺省开**；只有显式 `=0` 才关 ⇒ 整块不发生）。

FDR_UP = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/FlowDocumentReader.cs"

FDR_E1_NEEDLE = """                        _pageViewer = new ReaderPageViewer();
                        _pageViewer.SetResourceReference(StyleProperty, PageViewStyleKey);
"""
FDR_E1_REPL = """                        _pageViewer = new ReaderPageViewer();
                        // `T-B13`（`PRECOND-TAB3-PAGE-HOST`）：本移植的 PresentationUI 是替身（无 Themes/Generic.xaml）
                        //   ⇒ `PUIPageViewStyleKey` 解析为空 ⇒ 页宿主（模板里的 `DocumentPageView`）**从不存在**。
                        //   改为「先只读查资源：查到＝照旧；查不到＝补等价模板」。见 `WpfLinuxReaderPageHost` 头注。
                        MS.Internal.Documents.WpfLinuxReaderPageHost.ApplyPageViewerStyle(_pageViewer, PageViewStyleKey);
"""
# 只读台账：`SwitchViewingModeCore` 的"接上新查看器"那一步（判"到底有没有接上内容宿主"）
FDR_E2_NEEDLE = """                    // Attach new viewer
                    _contentHost.Child = feViewer;
                    AttachViewer(viewer);
"""
FDR_E2_REPL = """                    // Attach new viewer
                    _contentHost.Child = feViewer;
                    AttachViewer(viewer);
                    // `T-B13`：只读台账 —— 已接上／内容宿主类型（判"查看器到底有没有接上内容宿主"）
                    MS.Internal.Documents.WpfLinuxReaderPageHost.ReportAttached(feViewer, _contentHost);
"""
FDR_EDITS = [
    (FDR_E1_NEEDLE, FDR_E1_REPL, 1),
    (FDR_E2_NEEDLE, FDR_E2_REPL, 1),
]

DVBI_UP = "src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/DocumentViewerBase.cs"
DVBI_E1_NEEDLE = """            changed = true;
            return new ReadOnlyCollection<DocumentPageView>(pageViewList);
"""
DVBI_E1_REPL = """            // `T-B13`：只读台账 —— 这个查看器到底收到几个页宿主（判决 tab3「页宿主是谁」）
            MS.Internal.Documents.WpfLinuxPageViewProbe.ReportPageViews(this, pageViewList.Count);
            changed = true;
            return new ReadOnlyCollection<DocumentPageView>(pageViewList);
"""
DVBI_EDITS = [
    (DVBI_E1_NEEDLE, DVBI_E1_REPL, 1),
]

READERPAGE_PROBE_FILE = "WpfLinuxReaderPageHost.Linux.cs"
READERPAGE_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-B13（`PRECOND-TAB3-PAGE-HOST`）：`FlowDocumentReader` 内部页宿主的接线 ＋ 只读台账（[READERHOST]）。
// 断点、件:行与依据见生成器内同名块。`WPF_READER_PAGEHOST=0` ⇒ 整块不发生（反极性腿）。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace MS.Internal.Documents
{
    internal static class WpfLinuxReaderPageHost
    {
        private static int _enabled = -1;
        private static Style _fallback;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_READER_PAGEHOST"); }
                    catch (Exception) { s = null; }
                    // ⚠️ `T-B13` **默认关**（只有显式 "1" 才开）：本条接线**已现取**它把页宿主
                    //   **接出来了**（`[DVBI] selfType=ReaderPageViewer n=1`），但**帧面不绿**
                    //   （`tab3` 色数／具名色不变）且**新增一条 `COMException E_HANDLE`** ⇒ 不默认启用，
                    //   不把"接出来了却仍不上屏"当成"修好了"。保持可复现（`=1` 即开）以备后续复核。
                    _enabled = (s == "1") ? 1 : 0;
                }
                return _enabled == 1;
            }
        }

        /// <summary>
        /// 上游行为 ＝ 无条件 `SetResourceReference(StyleProperty, key)`。本移植里该键（`PUIPageViewStyleKey`）
        /// 在 PresentationUI 替身中**不存在** ⇒ 先**只读**查一次：查到 ⇒ 逐字回上游；查不到 ⇒ 补等价模板。
        /// </summary>
        internal static void ApplyPageViewerStyle(FrameworkElement viewer, ResourceKey key)
        {
            if (viewer == null) { return; }
            if (!Enabled)
            {
                viewer.SetResourceReference(FrameworkElement.StyleProperty, key);
                return;
            }
            object found = null;
            try { found = viewer.TryFindResource(key); }
            catch (Exception) { found = null; }
            if (found is Style)
            {
                viewer.SetResourceReference(FrameworkElement.StyleProperty, key);
                Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                     + " key=PUIPageViewStyleKey found=1 fallback=0 viewer=" + Id(viewer));
                return;
            }
            // ② 该键缺失 ⇒ **先**用"控件类型隐式样式"（＝"上游在 Windows 上本来会拿到的那份等价物"，
            //    只是由 app 提供；查到就用它，等价于**不给 `Style` 留本地值** ⇒ 隐式查找照旧生效）。
            Style implicitStyle = null;
            try { implicitStyle = viewer.TryFindResource(typeof(FlowDocumentPageViewer)) as Style; }
            catch (Exception) { implicitStyle = null; }
            if (implicitStyle != null)
            {
                viewer.Style = implicitStyle;
                Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                     + " key=PUIPageViewStyleKey found=0 fallback=implicit targetType="
                     + (implicitStyle.TargetType == null ? "null" : implicitStyle.TargetType.Name)
                     + " viewer=" + Id(viewer));
                return;
            }
            Style fb = Fallback;
            viewer.Style = fb;
            Emit("[READERHOST] site=ApplyViewerStyle viewerType=" + TypeOf(viewer)
                 + " key=PUIPageViewStyleKey found=0 fallback=equiv targetType="
                 + (fb.TargetType == null ? "null" : fb.TargetType.Name)
                 + " viewer=" + Id(viewer));
        }

        /// <summary>
        /// 与上游 `PresentationUI/Themes/Generic.xaml:8675-8699` 的 `PUIPageViewStyleKey` **等价**的
        /// `FlowDocumentPageViewer` 样式：`AdornerDecorator(ClipToBounds)` → `Border` →
        /// `DocumentPageView(IsMasterPage=True, ClipToBounds)`。**只**在该资源键解析为空时使用。
        /// </summary>
        private static Style Fallback
        {
            get
            {
                if (_fallback == null)
                {
                    FrameworkElementFactory dpv = new FrameworkElementFactory(typeof(DocumentPageView));
                    dpv.SetValue(UIElement.ClipToBoundsProperty, true);
                    dpv.SetValue(DocumentViewerBase.IsMasterPageProperty, true);
                    FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
                    border.AppendChild(dpv);
                    FrameworkElementFactory adorner = new FrameworkElementFactory(typeof(AdornerDecorator));
                    adorner.SetValue(UIElement.ClipToBoundsProperty, true);
                    adorner.AppendChild(border);
                    ControlTemplate tmpl = new ControlTemplate(typeof(FlowDocumentPageViewer));
                    tmpl.VisualTree = adorner;
                    Style st = new Style(typeof(FlowDocumentPageViewer));
                    st.Setters.Add(new Setter(Control.TemplateProperty, tmpl));
                    _fallback = st;
                }
                return _fallback;
            }
        }

        /// <summary>`T-B13`：只读台账 —— 「接上新查看器」那一步的现场（查看器／内容宿主各是什么）。</summary>
        internal static void ReportAttached(FrameworkElement viewer, object contentHost)
        {
            if (!Enabled) { return; }
            Emit("[READERHOST] site=Attach viewerType=" + TypeOf(viewer) + " viewer=" + Id(viewer)
                 + " contentHostType=" + TypeOf(contentHost));
        }

        private static string TypeOf(object o)
        {
            if (o == null) { return "null"; }
            try { return o.GetType().Name; }
            catch (Exception) { return "NA"; }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            try
            {
                return "0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)
                    .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "NA"; }
        }

        private static void Emit(string line)
        {
            try { Console.Error.WriteLine(line); Console.Error.Flush(); }
            catch (Exception) { }
        }
    }
}
'''


EHANDLE_PROBE_FILE = "WpfLinuxEHandleProbe.Linux.cs"
EHANDLE_PROBE_TEXT = '''// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// `T-B17`（`PRECOND-TAB3-EHANDLE-CALLSITE`）：`E_HANDLE`(`0x80070006`) 抛点的**只读**捕获器。
//
// 【它答的问题】`T-B16` 现取：闸开腿上 `[HC-UNHANDLED]` **恒 +1 条** `COMException … E_HANDLE`，
//   首帧只能拿到 `MS.Internal.HRESULT.Check(Int32 hr)`（那是**抛点**，不是**调用点**）⇒
//   "谁把 `hr=0x80070006` 交给 `Check`"**没有直读面**。本件用
//   `AppDomain.CurrentDomain.FirstChanceException`（**首次异常**，比 `DispatcherUnhandledException`
//   早、且**不改变**异常是否被处理）只读地把**当时的栈**打出来 ⇒ 调用点具名。
//
// 【不扰动的保证】① 缺省**全关**（`WPF_EHANDLE_PROBE=1` 才装钩子）；② 钩子**只读**（不 rethrow、
//   不改 `Handled`、不吞异常）；③ 输出**有界**（≤8 条 × 栈 ≤28 层）；④ 只写 stderr、内部 try/catch 兜底。

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS.Internal.Documents
{
    internal static class WpfLinuxEHandleProbe
    {
        private const int EHandle = unchecked((int)0x80070006);
        private const int MaxHits = 8;
        private const int MaxFrames = 28;

        private static int _on = -1;
        private static int _hits;

        private static bool On
        {
            get
            {
                if (_on < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_EHANDLE_PROBE"); }
                    catch (Exception) { s = null; }
                    _on = (s == "1") ? 1 : 0;
                }
                return _on == 1;
            }
        }

#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void Install()
        {
            if (!On) { return; }
            try { AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; }
            catch (Exception) { }
        }
#pragma warning restore CA2255

        private static void OnFirstChance(object sender,
            System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            try
            {
                COMException ce = e.Exception as COMException;
                if (ce == null || ce.HResult != EHandle) { return; }
                if (_hits >= MaxHits) { return; }
                _hits++;

                string st = null;
                try { st = ce.StackTrace; } catch (Exception) { st = null; }
                if (string.IsNullOrEmpty(st))
                {
                    try { st = Environment.StackTrace; } catch (Exception) { st = "<no-stack>"; }
                }
                string[] lines = st.Split('\\n');
                var sb = new System.Text.StringBuilder();
                sb.Append("[EHANDLE] #").Append(_hits).Append(" msg=").Append(ce.Message);
                for (int i = 0; i < lines.Length && i < MaxFrames; i++)
                {
                    sb.Append("\\n[EHANDLE]   ").Append(lines[i].Trim());
                }
                Console.Error.WriteLine(sb.ToString());
                Console.Error.Flush();
            }
            catch (Exception) { }
        }
    }
}
'''


def materialize_derived():
    """生成补丁 C 的两个派生源文件。needle 校验失败 ⇒ 抛（由 main 转成 rc≠0）。"""
    made = []
    a, n = _apply_edits(UP_PF + "MS/Internal/PtsHost/PtsCache.cs", "PtsCache.Linux.cs", PTSCACHE_EDITS)
    made.append((a, n))
    b, n = _apply_edits(UP_PF + "MS/Internal/documents/FlowDocumentView.cs",
                        "FlowDocumentView.Linux.cs", FDV_EDITS)
    made.append((b, n))
    c, n = _apply_edits(UP_PF + "MS/Internal/PtsHost/TextParaClient.cs",
                        "TextParaClient.Linux.cs", TPC_EDITS)
    made.append((c, n))
    for up_rel, out_name, edits in CHAIN_FILES:
        p, n = _apply_edits(UP_PF + up_rel, out_name, edits)
        made.append((p, n))
    # ⏪ `T-B11`：分页视觉宿主（`DocumentPageView`／`DocumentPageHost`）的「上屏」接线 ＋ 只读台账
    p, n = _apply_edits(DPV_UP, "DocumentPageView.Linux.cs", DPV_EDITS)
    made.append((p, n))
    p, n = _apply_edits(DPH_UP, "DocumentPageHost.Linux.cs", DPH_EDITS)
    made.append((p, n))
    # 判别器类本体（**非派生**：新建件，`temp+rename`）
    probe_abs = os.path.join(HERE, CHAIN_PROBE_FILE)
    _write_atomic(probe_abs, CHAIN_PROBE_TEXT)
    made.append((probe_abs, 0))
    # ⏪ `T-A69`：内容源入站喂料器类本体（**非派生**：新建件，`temp+rename`）
    textsrc_abs = os.path.join(HERE, TEXTSRC_PROBE_FILE)
    _write_atomic(textsrc_abs, TEXTSRC_PROBE_TEXT)
    made.append((textsrc_abs, 0))
    # ⏪ `T-A70`：`cp↔dcp` 偏移映射交付器类本体（**非派生**：新建件，`temp+rename`）
    cpdc_abs = os.path.join(HERE, CPDCMAP_PROBE_FILE)
    _write_atomic(cpdc_abs, CPDCMAP_PROBE_TEXT)
    made.append((cpdc_abs, 0))
    # ⏪ `T-A71`：文本段落进链交付器类本体（**非派生**：新建件，`temp+rename`）
    pc_abs = os.path.join(HERE, PARACHAIN_PROBE_FILE)
    _write_atomic(pc_abs, PARACHAIN_PROBE_TEXT)
    made.append((pc_abs, 0))
    # ⏪ `T-A72`：LS 会话↔段落对账交付器类本体（**非派生**：新建件，`temp+rename`）
    lss_abs = os.path.join(HERE, LSSESS_PROBE_FILE)
    _write_atomic(lss_abs, LSSESS_PROBE_TEXT)
    made.append((lss_abs, 0))
    # ⏪ `T-A73`：行断器触发器类本体（**非派生**：新建件，`temp+rename`）
    lb_abs = os.path.join(HERE, LBREAK_PROBE_FILE)
    _write_atomic(lb_abs, LBREAK_PROBE_TEXT)
    made.append((lb_abs, 0))
    # ⏪ `T-B12`：分页页视觉「壳内」内容视觉的只读逐跳读数类本体（**非派生**：新建件，`temp+rename`）
    pagevis_abs = os.path.join(HERE, PAGEVIS_PROBE_FILE)
    _write_atomic(pagevis_abs, PAGEVIS_PROBE_TEXT)
    made.append((pagevis_abs, 0))
    # ⏪ `T-B13`：`FlowDocumentReader` 内部页宿主（`ReaderPageViewer` 的模板）可达接线 ＋ 台账
    p, n = _apply_edits(FDR_UP, "FlowDocumentReader.Linux.cs", FDR_EDITS)
    made.append((p, n))
    p, n = _apply_edits(DVBI_UP, "DocumentViewerBase.Linux.cs", DVBI_EDITS)
    made.append((p, n))
    readerpage_abs = os.path.join(HERE, READERPAGE_PROBE_FILE)
    _write_atomic(readerpage_abs, READERPAGE_PROBE_TEXT)
    made.append((readerpage_abs, 0))
    # ⏪ `T-B17`：`E_HANDLE` 抛点/调用点的只读捕获器（首次异常）本体（**非派生**：新建件）
    ehandle_abs = os.path.join(HERE, EHANDLE_PROBE_FILE)
    _write_atomic(ehandle_abs, EHANDLE_PROBE_TEXT)
    made.append((ehandle_abs, 0))
    return made


def main():
    # ① 先materialize产出派生源（**先于** csproj 接线：源没产出就不该接线）
    try:
        made = materialize_derived()
    except RuntimeError as e:
        print("[失败] 派生源生成失败：%s" % e)
        return 1
    for path, n in made:
        print("[OK] 生成 %s（%d 处改动，needle 全部命中）" % (os.path.relpath(path, REPO), n))

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

    block = BEGIN + "\n" + PATCH_A + "\n" + PATCH_B + "\n" + PATCH_C + "\n" + END + "\n"
    text = text.replace(MARKER, block + MARKER)
    _write_atomic(CSPROJ, text)          # ⏪ `T-A41`：csproj 同样 temp+rename
    print(f"[OK] 已注入补丁 A/B/C → {CSPROJ}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
