// Licensed to the .NET Foundation under one or more agreements.
//
// 契约层（Contracts/）的所有类型都是 internal——对外只暴露 MilNative 这 13 个
// MIL 导出函数（public，签名对齐 exports.cs）。单元测试要走 internal，故放开友元。
//
// 注意：本文件在 Interop/ 下，不修改 Contracts/ 里的任何文件（handoff §7）。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WpfGfx.Linux.Commands.Tests")]
[assembly: InternalsVisibleTo("WpfGfx.Linux.Rendering.Tests")]
// T9-A3 的 HelloMil demo。samples/HelloMil 不依赖 WPF 运行时，直接用本程序集的
// MilChannel / MilRenderData / SkiaRenderBackend / X11PresentationTarget 构造视觉树
// 并渲染，但这些契约层类型全是 internal，故在此开友元。
// 注意：HelloMil 的**公开** API 不能泄漏 internal 类型（那会触发 CS0053），
// 它对外只暴露 SKBitmap / SKImage / string 等 Skia 与 BCL 的公共类型。
[assembly: InternalsVisibleTo("HelloMil")]
// HelloMil.Tests 的 X11 探针（U10）：与 Windowing.Tests 的 X11Probe 共用同一探针
// 实现 X11Display.Open（带重试 + 失败分类），避免两套测试对"X 是否可用"判断不一致。
[assembly: InternalsVisibleTo("WpfGfx.Linux.HelloMil.Tests")]
// M7c 端到端（接窗 + 呈现）：驱动 DUCE 命令流 → 真 X11 窗口 → xwd 截屏。
// 它需要 MilCommandEncoder（internal，用来按 DUCE 线上格式造命令）、
// MilPresentation（内部绑定表）、以及 Windowing 的 X11 类型 —— IVT 是**按程序集**生效的，
// 所以这一行同时覆盖 Interop/ 与 Windowing/ 下的 internal 类型。
[assembly: InternalsVisibleTo("WpfGfx.Linux.Presentation.Tests")]

// M7c 轨道 C：真接窗测试（ManagedLayer.Tests/M7cRealAttachmentTests.cs）要直接驱动
// MilPresentation / MilCommandEncoder / DUCE.ResourceHandle 这些 internal 类型 ——
// 它验的是"Attach 之后那个 X 窗口真的收到像素、Detach 之后真的停"，必须直接调这一层，
// 而不是只断言登记表（那正是要被替换掉的旧门面）。
[assembly: InternalsVisibleTo("WpfGfx.Linux.ManagedLayer.Tests")]
