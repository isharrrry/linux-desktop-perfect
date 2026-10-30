// PresentationCore · 程序集级特性补丁（M4）
// =====================================================================================
// 为什么需要
// ----------
// 实测 37 条 CS3021：「由于程序集没有 CLSCompliant 特性，因此"XXX"…」。
// 根因：上游 PresentationCore 的 assembly 特性（含 [assembly: CLSCompliant(false)]）
// 由 Arcade 的 CreateGeneratedAssemblyInfo/GenerateAssemblyInfo 生成，**不在树内**，
// port-lib 的 DROP_TARGETS 把该 Target 丢弃 → 特性缺失，而源码里存在带
// [CLSCompliant(...)] 的公开成员，于是编译器逐成员报 CS3021。
// 对照：WindowsBase 树内有 checked-in 的 LibraryAssemblyInfo.cs（含同一特性），
// 所以 WindowsBase.Linux 是 0 警 —— 这条差异正是根因证据。
//
// 口径说明
// --------
//   * 这里**只**补 CLSCompliant：与上游 System.Windows.Primitives/AssemblyInfo.cs
//     的 [assembly: CLSCompliant(false)] 同值（WPF 全仓口径：公开面大量使用非 CLS 类型）。
//   * **不声明** AssemblyVersion/FileVersion/InformationalVersion：
//     由 src/Microsoft.DotNet.Wpf.Linux/src/shims/LinuxAssemblyIdentity.cs 统一提供（4.0.0.1），重复声明会 CS0579。
//   * 恢复条件：若将来 port-lib 支持搬运 Arcade 生成的 assembly 特性，本文件可删除。
using System;

[assembly: CLSCompliant(false)]
