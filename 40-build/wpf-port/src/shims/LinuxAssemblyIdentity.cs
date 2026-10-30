// 自产 WPF 程序集的**身份版本**——WPF-on-Linux 全项目共用的一个 shim。
//
// ── 为什么必须有这个文件 ────────────────────────────────────────────────
// net10.0 的框架里带一份**同名空门面**：
//   ~/.dotnet/shared/Microsoft.NETCore.App/10.0.11/WindowsBase.dll （16 KB，
//   AssemblyVersion 4.0.0.0，只有 1 个 TypeDef + 30 个 ExportedType 类型转发）
// 而我们的自产 WindowsBase 因上游 csproj 沿用 `GenerateAssemblyInfo=false`，
// 默认 AssemblyVersion = 0.0.0.0。于是：
//   · 编译期：SDK 的引用冲突解析按「版本高者胜」**静默丢弃**我们的
//     `<Reference><HintPath>`，报错只表现为 `System.Windows.Interop.MSG` CS0234
//     （完全看不出是 HintPath 失效）；
//   · 运行期：host 绑回 4.0.0.0 门面 → TypeLoadException（门面没有真实类型）。
// 实测反证：把版本抬到 4.0.0.1 后，同一程序立刻绑定到 app-local 自产程序集，
// MSG 正常解析（详见 handoff U2 专节 / src/Linux/build/Directory.Upstream.props 的注释）。
//
// 因此：**自产 WPF 程序集的 AssemblyVersion 必须严格大于 4.0.0.0**。
// 版本号取 4.0.0.1：与 WPF 历史上的 4.0.0.0 同主次版本，仅补丁位 +1，
// 既赢得冲突解析，又不改变任何 API/绑定语义（这些程序集均未强命名）。
//
// ── 为什么手写属性而不是打开 GenerateAssemblyInfo ───────────────────────
// 上游源码自带 LibraryAssemblyInfo.cs / GlobalUsings.cs 等属性文件，打开 SDK
// 自动生成会与它们重复（CS0579）。这里只补版本三属性，其余一律不动。
// 已核实：本工程编译集内不存在其它 AssemblyVersion/AssemblyFileVersion/
// AssemblyInformationalVersion 声明（唯一一处 `Shared/Tracing/mcwpf/mcwpf.cs`
// 不在任何 *.Linux 工程的编译列表里）。

using System.Reflection;

[assembly: AssemblyVersion("4.0.0.1")]
[assembly: AssemblyFileVersion("4.0.0.1")]
[assembly: AssemblyInformationalVersion("4.0.0.1-wpf-linux")]
