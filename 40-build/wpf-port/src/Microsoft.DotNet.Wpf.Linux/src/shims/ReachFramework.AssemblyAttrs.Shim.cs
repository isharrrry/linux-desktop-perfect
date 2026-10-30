// WPF-on-Linux 兼容层：ReachFramework 的**程序集级特性**补全。
//
// 与 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationFramework.AssemblyAttrs.Shim.cs 同一处置口径：
//   · 上游 ReachFramework 源码里有成员级 [CLSCompliant(false)]；
//   · 程序集级 CLSCompliant 由 Arcade 生成 AssemblyInfo 时补上，不在代码树内；
//   · 本移植工程按设计切断 Arcade 继承（src/Linux/build/Directory.Upstream.props）→ 缺失 → CS3021。
// 取值 false 的依据：PresentationFramework 轮实测二选一 ——
//   [assembly: CLSCompliant(true)]  → 新增数百条 CS3001/CS3003；
//   [assembly: CLSCompliant(false)] → CS3021 归零、新增 0。
// 该特性只影响编译期 CLS 合规分析，不产生 IL/运行期差异。

using System;

[assembly: CLSCompliant(false)]
