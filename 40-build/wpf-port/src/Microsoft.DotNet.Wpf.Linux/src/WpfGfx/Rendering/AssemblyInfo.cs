// Licensed to the .NET Foundation under one or more agreements.
//
// 渲染层（T4）的程序集级声明。
//
// 契约层 Contracts/ 里的类型（MilVisual / MilDrawInstruction / IRenderBackend /
// MilResourceHandle …）一律是 internal，测试工程要能直接构造它们就必须开友元。
//
// 注意：Interop/AssemblyInfo.cs 已经给 Commands.Tests 开过一次友元，本文件是**追加**
// 而不是替换——C# 允许同一个程序集上有多个 InternalsVisibleTo。
// 放在 Rendering/ 而不是去改 Interop/，是为了守住 handoff §7 的目录边界。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WpfGfx.Linux.Rendering.Tests")]
