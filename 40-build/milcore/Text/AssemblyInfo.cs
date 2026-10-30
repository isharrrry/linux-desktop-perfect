// T6（Text/）与 T5（Windowing/）的程序集级声明。
//
// 契约层 Contracts/ 的类型（IPresentationTarget、MilResourceHandle、RenderContext…）
// 以及 Resources/ 的 MilGlyphRun、Rendering/ 的 MilResourceProvider 都是 internal，
// 测试工程要直接构造它们就得开友元。
//
// C# 允许同一个程序集上有多个 InternalsVisibleTo，这里是**追加**：
// Interop/AssemblyInfo.cs 给了 Commands.Tests，Rendering/AssemblyInfo.cs 给了
// Rendering.Tests。放在 Windowing/ 下而不去改那两个文件，是为了守住 handoff §7
// 的目录边界。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WpfGfx.Linux.Windowing.Tests")]
