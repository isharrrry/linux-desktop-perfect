// Licensed to the .NET Foundation under one or more agreements.
//
// 全局 using。
//
// 契约层 Contracts/Interfaces.cs 是按「开启 ImplicitUsings」写的（用了裸
// ReadOnlySpan<> / List<> / IReadOnlyList<>），而本项目 csproj 关闭了 ImplicitUsings。
// 契约文件不可修改（handoff §7），因此在这里用 global using 补上，
// 效果等价于 ImplicitUsings，但不触碰契约与 csproj。
//
// 注：本文件必须在 Interop/ 之外无依赖，编译顺序无关（global using 是编译期全局的）。

global using System;
global using System.Collections.Generic;
