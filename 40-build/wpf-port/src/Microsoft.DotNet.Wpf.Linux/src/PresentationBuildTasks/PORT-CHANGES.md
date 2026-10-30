# 相对上游 PresentationBuildTasks.csproj 的改动清单

> 由 `build/port-pbt.sh` 自动生成，请勿手改。上游仓库保持只读。
> 上游文件：`/home/links-dev/netTest/GitProj/WPFOnLinux/upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationBuildTasks/PresentationBuildTasks.csproj`
> 生成时间：2026-10-03T18:16:04Z
> 上游文件 SHA256：`391aaa797ed730e8090ae104dbcb182dc8a0fca56e0a82f12a144fc486f4b151`

## 改动项

**1. TargetFramework**：`$(BundledNETCoreAppTargetFramework);$(NetFrameworkToolCurrent)`（含 .NETFramework 双目标）→ 单目标 `net10.0`。
原因：Linux 无 .NETFramework 参考程序集，且 `NetFrameworkToolCurrent` 等变量由上游 Arcade SDK 提供，此处拿不到。

**2. 路径分隔符**：上游全部 Compile Include 用 Windows 反斜杠（如 `$(WpfSourceDir)PresentationFramework\System\...`）。
Linux 的 MSBuild **不会**把反斜杠当分隔符，会当成文件名的一部分导致文件找不到。已全部转为正斜杠。

**3. 路径变量**：`WpfSourceDir` / `WpfSharedDir` / `WpfCommonDir` 在上游由 Arcade 的 `eng/WpfArcadeSdk/tools/FolderPaths.props` 注入。
此处改为在本 csproj 内显式定义，指向上游只读目录 `/home/links-dev/netTest/GitProj/WPFOnLinux/upstream/wpf` 并用正斜杠结尾。

**6. 资源文件路径**：`&lt;EmbeddedResource Include="Resources/Strings.resx" /&gt;` 是相对「上游项目目录」的路径。
本工程换了目录后该相对路径失效，报 `error MSB3552: Resource file "Resources/Strings.resx" cannot be found.`
已改为用 `$(UpstreamWpfRoot)` 绝对定位，并保留 `Link` 与 `LogicalName`（SR.cs 依赖资源名的完整命名空间）。

**8. 生成 SR 资源类**：上游 WPF 用 Arcade 的 `GenerateCommonSRSource` 目标，在编译期把
`Resources/Strings.resx` 翻译成 `SR.common.cs`（含 `ResourceManager` 属性 + 每条字符串一个
`internal static string Foo => GetResourceString("Foo")`）。手工写的 `SR.cs` 与
`Common/src/System/SR.cs` 只是另一半 partial class，缺了它就会刷 688 个
`error CS0117: 'SR' does not contain a definition for 'Xxx'`。
本工程不引入 Arcade，改用 `build/gen-sr.py` 从**同一个 resx**生成等价的 `SR.g.cs`（239 条），
并显式加入编译（因 `EnableDefaultItems=false`）。

**9. ReflectionHelper.cs 换本地副本 ＋ 新增 NameFallbackResolver.cs**：上游 `Shared/System/Windows/Markup/ReflectionHelper.cs` 的
`Initialize()` 用 `new MetadataLoadContext(new PathAssemblyResolver(assemblyPaths), …)` 解析程序集；`PathAssemblyResolver` 的规矩是
**名字同 且 公钥 token 完全一致**（版本允许 找到的 ≥ 请求的）。
第三方 WPF NuGet 包的 windows 资产按官方 WindowsDesktop 身份请求（`System.Xaml 5.0.0.0/b77a…`、`PresentationFramework 5.0.0.0/31bf…`），
与自产件（`4.0.0.1/31bf…`；System.Xaml 的 token 本身不同）对不上 ⇒ MarkupCompilePass1 抛
`MC1000: Could not find assembly 'System.Xaml, Version=5.0.0.0, …'`，逼着调用方去造身份改版件骗编译器 ——
而改版件在运行期与自产件内部写死的身份对不上 ⇒ 应用全黑。
本工程把该文件复制一份到 `build/PresentationBuildTasks.Linux/ReflectionHelper.cs`（只改那一行：换成 `NameFallbackResolver`），
并新增 `NameFallbackResolver.cs`（先按官方规矩解析，解析不到再退回只按简单名找，见该文件头注释）。
csproj 的 `Compile` 项由 `$(WpfSharedDir)/…/ReflectionHelper.cs` 改为本工程的 `ReflectionHelper.cs`，并追加 `NameFallbackResolver.cs`。
上游 snapshot 一个字节不改。详见 `RT/Links-License-Mgr/PortWpfLinux/TASK-治全黑.md`。

**7. 自身源文件路径**：`MS/Internal/**`、`Microsoft/Build/Tasks/Windows/**`、`SR.cs`、`System/AppContextDefaultValues.cs` 等 29 条
是相对「上游项目目录」的路径（改动 2 只修了分隔符、没修根目录）。本工程换了目录后全部报
`error CS2001: Source file '...' could not be found.`
已统一加 `$(UpstreamPbtDir)` 前缀。注意本工程**不复制任何源码**，直接编译上游只读文件。

**5. 包版本号**：`$(MicrosoftBuildFrameworkPackageVersion)` 等由 Arcade 注入，此处写死。
`Microsoft.Build.Framework` / `Microsoft.Build.Utilities.Core` 沿用上游的 `15.9.20`（IncludeAssets=compile，运行时由 MSBuild 宿主提供，故低于宿主版本是安全的）。
`System.Reflection.MetadataLoadContext` 与 `System.CodeDom` 上游用的是 `11.0.0-rc.1.26411.119`（预览版，镜像源上不一定有），改用稳定版 `9.0.0`。

**4. 移除 Arcade 依赖**：上游 csproj 继承仓库根 `Directory.Build.props`（引入 Microsoft.DotNet.Arcade.Sdk）与 WpfArcadeSdk。
本工程位于 `/home/links-dev/netTest/GitProj/WPFOnLinux/build/..` 下，通过自带 `Directory.Build.props` / `.targets` 切断继承，改为自包含。

  受影响的 Arcade 专属性质（`BinPlaceRuntime`、`PackagingContent`、`PackagingAssemblyContent`、`EnablePInvokeAnalyzer`）在无 Arcade 时为空操作，保留不删以便与上游 diff。

