// WPF-on-Linux 兼容层：PresentationFramework 的**程序集级特性**补全。
//
// ── 需要什么 ────────────────────────────────────────────────────────────
// 上游 PresentationFramework 的源码里有多处**成员级** [CLSCompliant(false)]
//   （System/Windows/Documents/DocumentReferenceCollection.cs:18、
//     Documents/DocumentSequence.cs:481、Controls/Primitives/DocumentViewerBase.cs:218、
//     TriggerActionCollection.cs:177 …）
// 但**程序集级**的 CLSCompliant 特性不在任何 checked-in 文件里：上游由 Arcade
// (Microsoft.DotNet.Arcade.Wpf.Sdk) 生成 AssemblyInfo 时补上。本移植工程按设计切断
// Arcade 继承（src/Linux/build/Directory.Upstream.props），于是程序集没有该特性 →
// 编译器对上述 4 处成员级标注逐一报
//     CS3021 "由于程序集没有 CLSCompliant 特性，因此 X 不需要 CLSCompliant 特性"
// 实测：CS3021 ×4（唯一）。
//
// ── 为什么取 false 而不是 true（**实测二选一，不是猜**）────────────────
// 两种取值都用 /tmp 探针（-p:CustomAfterMicrosoftCommonTargets 注入一个 Compile 项）
// 在本工程实测：
//   · [assembly: CLSCompliant(true)]  → 新增 CS3001/CS3003 **数百条**
//       （如 Window.cs:522 "参数类型 DependencyObject 不符合 CLS"、
//        Window.cs:995 "Window.LeftProperty 的类型不符合 CLS"，逐成员刷屏）
//   · [assembly: CLSCompliant(false)] → CS3021 4 条归零，**新增 0**
// 上游构建显然不存在那数百条 CS3001（上游 csproj/Directory.Build.Props 的 NoWarn 里
// 没有 CS30xx）⇒ 上游的程序集级取值必为 false。故本 shim 取 false。
// 该特性只影响编译期的 CLS 合规分析，不产生任何 IL/运行期差异。
//
// ── 为什么放在这里的而不是共用身份文件 ──────────────────────────────────
// src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs 是所有 *.Linux 工程共用的（另一个 agent 所有），
// 本轮按文件所有权约束不修改它。**建议**（见 docs/U2-PresentationFramework-prep.md §5）：
// 把 [assembly: CLSCompliant(false)] 提升到该共用文件，PresentationCore 的 37 条
// CS3021 会一并消失（同一根因）。

using System;

[assembly: CLSCompliant(false)]
