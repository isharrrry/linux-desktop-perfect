// Licensed to the .NET Foundation under one or more agreements.
//
// 命令固定部分的长度表，以及 E_NOTIMPL 登记表。
//
// 长度的算法：上游 Generated/wgx_commands.cs 中该命令最后一个字段的
// FieldOffset + 字段大小，再向上取到 4 的倍数（命令流要求 QWORD/DWORD 对齐）。
// 变长命令（带 *Size 尾部数组的）返回的是「固定头」长度，尾部单独处理。
//
// 每个长度都由 tests/WpfGfx.Linux.Tests/CommandLayoutTests.cs 用
// Marshal.SizeOf<T>() 实测校验，不靠人工算术。

using System.Collections.Generic;
using WpfGfx.Linux.Contracts;

namespace WpfGfx.Linux.Commands
{
    internal static class MilCommandLayout
    {
        /// <summary>命令固定部分的最小长度。变长命令返回头部长度；未知命令返回 -1。</summary>
        public static int FixedSize(MilCmd cmd) => cmd switch
        {
            // ---- 分区 / 传输 ----
            // 这 5 条在上游 Generated/wgx_commands.cs 里没有结构体定义：
            // 线上只有 MILCMD Type（+ Handle），8 字节。
            // VisualCreate(0x1a) 与 ValidateStructureOrder(0x8e) 的长度在下面各自的分段里。
            MilCmd.MilCmdTransportSyncFlush => 8,
            MilCmd.MilCmdTransportDestroyResourcesOnChannel => 8,
            MilCmd.MilCmdChannelCreateResource => 8,
            MilCmd.MilCmdChannelDeleteResource => 8,
            MilCmd.MilCmdChannelDuplicateHandle => 8,

            MilCmd.MilCmdPartitionRegisterForNotifications => 8,
            MilCmd.MilCmdChannelRequestTier => 8,
            MilCmd.MilCmdPartitionSetVBlankSyncMode => 8,
            MilCmd.MilCmdPartitionNotifyPresent => 12,   // Type(4) + FrameTime(8)，Pack=1 无填充
            MilCmd.MilCmdPartitionNotifyPolicyChangeForNonInteractiveMode => 8,

            // ---- 位图源 ----
            // 0x0c 上游只在 C++ 头 wgx_commands.h:86 定义（C# 生成版无此结构）：
            //   Type(4) + Handle(4) + IWICBitmapSource*(8) = 16
            //   Linux 侧把最后 8 字节重定义为位图令牌，宽度不变。
            // 0x0d 见 C# 版 wgx_commands.cs:62：Type(4)+Handle(4)+BOOL(4)+RECT(16) = 28
            MilCmd.MilCmdBitmapSource => 16,
            MilCmd.MilCmdBitmapInvalidate => 28,

            // ---- 标量资源 ----
            MilCmd.MilCmdDoubleResource => 16,
            MilCmd.MilCmdColorResource => 24,
            MilCmd.MilCmdPointResource => 24,
            MilCmd.MilCmdRectResource => 40,
            MilCmd.MilCmdSizeResource => 24,
            MilCmd.MilCmdMatrixResource => 56,
            MilCmd.MilCmdPoint3DResource => 20,
            MilCmd.MilCmdVector3DResource => 20,
            MilCmd.MilCmdQuaternionResource => 24,
            MilCmd.MilCmdEtwEventResource => 12,

            // ---- RenderData（变长：+ CbData） ----
            MilCmd.MilCmdRenderData => 12,

            // ---- Visual ----
            MilCmd.MilCmdVisualCreate => 8,
            MilCmd.MilCmdVisualSetOffset => 24,
            MilCmd.MilCmdVisualSetTransform => 12,
            MilCmd.MilCmdVisualSetEffect => 12,
            MilCmd.MilCmdVisualSetCacheMode => 12,
            MilCmd.MilCmdVisualSetClip => 12,
            MilCmd.MilCmdVisualSetAlpha => 16,
            MilCmd.MilCmdVisualSetRenderOptions => 36,
            MilCmd.MilCmdVisualSetContent => 12,
            MilCmd.MilCmdVisualSetAlphaMask => 12,
            MilCmd.MilCmdVisualRemoveAllChildren => 8,
            MilCmd.MilCmdVisualRemoveChild => 12,
            MilCmd.MilCmdVisualInsertChildAt => 16,
            MilCmd.MilCmdVisualSetGuidelineCollection => 16,   // 变长：+ (CountX+CountY)*4
            MilCmd.MilCmdVisualSetScrollableAreaClip => 44,

            // ---- Target ----
            MilCmd.MilCmdHwndTargetCreate => 92,
            MilCmd.MilCmdHwndTargetSuppressLayered => 12,
            MilCmd.MilCmdTargetUpdateWindowSettings => 72,
            MilCmd.MilCmdGenericTargetCreate => 36,
            MilCmd.MilCmdTargetSetRoot => 12,
            MilCmd.MilCmdTargetSetClearColor => 24,
            MilCmd.MilCmdTargetInvalidate => 24,
            MilCmd.MilCmdTargetSetFlags => 12,
            MilCmd.MilCmdHwndTargetDpiChanged => 28,

            // ---- GlyphRun（变长：+ 字形数组） ----
            MilCmd.MilCmdGlyphRunCreate => 76,

            // ---- 变换 ----
            MilCmd.MilCmdTransformGroup => 12,                 // 变长：+ ChildrenSize
            MilCmd.MilCmdTranslateTransform => 32,
            MilCmd.MilCmdScaleTransform => 56,
            MilCmd.MilCmdSkewTransform => 56,
            MilCmd.MilCmdRotateTransform => 44,
            MilCmd.MilCmdMatrixTransform => 60,

            // ---- 几何 ----
            MilCmd.MilCmdLineGeometry => 52,
            MilCmd.MilCmdRectangleGeometry => 72,
            MilCmd.MilCmdEllipseGeometry => 56,
            MilCmd.MilCmdGeometryGroup => 20,                  // 变长：+ ChildrenSize
            MilCmd.MilCmdCombinedGeometry => 24,
            MilCmd.MilCmdPathGeometry => 20,                   // 变长：+ FiguresSize

            // ---- 画刷 ----
            MilCmd.MilCmdImplicitInputBrush => 28,
            MilCmd.MilCmdSolidColorBrush => 48,
            MilCmd.MilCmdLinearGradientBrush => 84,            // 变长：+ GradientStopsSize
            MilCmd.MilCmdRadialGradientBrush => 108,           // 变长：+ GradientStopsSize
            MilCmd.MilCmdImageBrush => 148,
            MilCmd.MilCmdDrawingBrush => 148,
            MilCmd.MilCmdVisualBrush => 148,
            MilCmd.MilCmdBitmapCacheBrush => 36,

            // ---- 效果 ----
            // 0x6c / 0x70（波62 · D-G58）：这两条是**变长**的，下面给的是「**固定头**」长度，
            // 尾部长度由命令头里的 *Size 字段声明；**长度账本身由 MilChannel 的
            // BeginCommand/EndCommand/SendCommand 记**（线上每条命令的字节数由上游
            // `…….BeginCommand(ptr, sizeof(struct), cbExtra)` 直接给出）⇒ dispatcher 不需要
            // 自己解析尾部，`FixedSize` 只用来做「命令至少有多长」的下界校验。
            //   0x6c MILCMD_PIXELSHADER  = Type(4)+Handle(4)+ShaderRenderMode(4)
            //                              +PixelShaderBytecodeSize(4)+CompileSoftwareShader(4) = **20**
            //        尾部 = PixelShaderBytecodeSize 字节（HLSL 字节码）
            //        上游：Effects/PixelShader.cs:180-191（Generated/PixelShader.cs:140）
            //   0x70 MILCMD_SHADEREFFECT = Type(4)+Handle(4)+4×double Padding(32)+hPixelShader(4)
            //                              +DdxUvDdyUvRegisterIndex(4)+8×UInt32 *Size(32) = **80**
            //        尾部 = 那 8 个 *Size 之和（ShaderConstant*/DependencyProperty* 寄存器值）
            //        上游：Effects/ShaderEffect.cs:559-589（Generated/ShaderEffect.cs:162）
            //   ⚠️ 这两个数**已经被两台互不相干的仪器独立核对过**（W52A 的命令层台账，
            //      不是本波自证）：`0x6c len=240 extra=220` ⇒ 头 20 ✓；
            //      `0x70 len=92 extra=12` 与 `len=110 extra=30` ⇒ 头 80 ✓。
            MilCmd.MilCmdPixelShader => 20,
            MilCmd.MilCmdShaderEffect => 80,
            MilCmd.MilCmdBlurEffect => 28,
            MilCmd.MilCmdDropShadowEffect => 80,

            // ---- Pen / DashStyle ----
            MilCmd.MilCmdDashStyle => 24,                      // 变长：+ DashesSize
            MilCmd.MilCmdPen => 52,

            // ---- Drawing ----
            MilCmd.MilCmdDrawingImage => 12,
            MilCmd.MilCmdGeometryDrawing => 20,
            MilCmd.MilCmdGlyphRunDrawing => 16,
            MilCmd.MilCmdImageDrawing => 48,
            MilCmd.MilCmdVideoDrawing => 48,
            MilCmd.MilCmdDrawingGroup => 52,                   // 变长：+ ChildrenSize

            // ---- 其它 ----
            MilCmd.MilCmdGuidelineSet => 20,                   // 变长：+ X/Y doubles
            MilCmd.MilCmdBitmapCache => 28,
            MilCmd.MilCmdValidateStructureOrder => 4,

            // ---- 3D 视觉树 (0x29–0x30)：POD，8 条全部已实现 ----
            MilCmd.MilCmdViewport3DVisualSetCamera => 12,
            MilCmd.MilCmdViewport3DVisualSetViewport => 40,        // Rect = 4 × double
            MilCmd.MilCmdViewport3DVisualSet3DChild => 12,
            MilCmd.MilCmdVisual3DSetContent => 12,
            MilCmd.MilCmdVisual3DSetTransform => 12,
            MilCmd.MilCmdVisual3DRemoveAllChildren => 8,
            MilCmd.MilCmdVisual3DRemoveChild => 12,
            MilCmd.MilCmdVisual3DInsertChildAt => 16,

            // ---- 3D 资源 (0x57–0x6b)：纯 POD，21 条全部已实现 ----
            MilCmd.MilCmdAxisAngleRotation3D => 36,
            MilCmd.MilCmdQuaternionRotation3D => 28,
            MilCmd.MilCmdPerspectiveCamera => 96,
            MilCmd.MilCmdOrthographicCamera => 96,
            MilCmd.MilCmdMatrixCamera => 140,
            MilCmd.MilCmdModel3DGroup => 16,                   // 变长：+ ChildrenSize
            MilCmd.MilCmdAmbientLight => 32,
            MilCmd.MilCmdDirectionalLight => 48,
            MilCmd.MilCmdPointLight => 96,
            MilCmd.MilCmdSpotLight => 136,
            MilCmd.MilCmdGeometryModel3D => 24,
            MilCmd.MilCmdMeshGeometry3D => 24,                 // 变长：+ 4 段顶点/索引数组
            MilCmd.MilCmdMaterialGroup => 12,                  // 变长：+ ChildrenSize
            MilCmd.MilCmdDiffuseMaterial => 44,
            MilCmd.MilCmdSpecularMaterial => 36,
            MilCmd.MilCmdEmissiveMaterial => 28,
            MilCmd.MilCmdTransform3DGroup => 12,               // 变长：+ ChildrenSize
            MilCmd.MilCmdTranslateTransform3D => 44,
            MilCmd.MilCmdScaleTransform3D => 80,
            MilCmd.MilCmdRotateTransform3D => 48,
            MilCmd.MilCmdMatrixTransform3D => 72,

            _ => -1,
        };

        /// <summary>该命令是否带尾部变长数组。</summary>
        public static bool HasVariablePayload(MilCmd cmd) => cmd switch
        {
            MilCmd.MilCmdRenderData => true,
            MilCmd.MilCmdVisualSetGuidelineCollection => true,
            MilCmd.MilCmdGlyphRunCreate => true,
            MilCmd.MilCmdTransformGroup => true,
            MilCmd.MilCmdGeometryGroup => true,
            MilCmd.MilCmdPathGeometry => true,
            MilCmd.MilCmdLinearGradientBrush => true,
            MilCmd.MilCmdRadialGradientBrush => true,
            MilCmd.MilCmdDashStyle => true,
            MilCmd.MilCmdDrawingGroup => true,
            MilCmd.MilCmdGuidelineSet => true,
            // 波62 · D-G58：0x6c 尾部 = PixelShaderBytecodeSize（HLSL 字节码）；
            // 0x70 尾部 = 8 个 *Size 之和。两条**都不是**定长。
            MilCmd.MilCmdPixelShader => true,
            MilCmd.MilCmdShaderEffect => true,
            // 3D：ChildrenSize = N × sizeof(ResourceHandle)(4)；
            // MeshGeometry3D 的四段数组元素宽度见 MilResource3D.cs 的注释。
            MilCmd.MilCmdModel3DGroup => true,
            MilCmd.MilCmdMeshGeometry3D => true,
            MilCmd.MilCmdMaterialGroup => true,
            MilCmd.MilCmdTransform3DGroup => true,
            _ => false,
        };

        // ==================================================================
        //  仍未实现的 5 条
        //
        //  38 → 17 → 9 → 7 → 5。移走的 33 条：
        //    21 条 = 0x57–0x6b 整段 3D 资源命令
        //     8 条 = 0x29–0x30 整段 3D 视觉树命令
        //     2 条 = 0x0c / 0x0d 位图源（见 Commands/MilBitmapSource.cs）
        //     2 条 = 0x6c / 0x70 Shader（波62 · D-G58 —— **不是"实现了 shader"**：
        //            这两条从 E_NOTIMPL 移出，改成「**收得下、按恒等处理、效果不渲染**」，
        //            理由与留痕见 DispatchCore 的两条 case ＋ MilChannel.NoteShaderStubAccepted）
        //  剩下 5 条的逐条分类见 docs/unimplemented.md §0：
        //    C 类 4 条（0x0a/0x0b：D3D 互操作；
        //              0x3b/0x3c：Windows 事件句柄 / 原生对象指针）
        //    B 类 1 条（0x17：媒体播放器）
        // ==================================================================

        private static readonly HashSet<MilCmd> s_notImpl = new HashSet<MilCmd>
        {
            // C 类：D3D / 硬件加速（handoff 决策 4）
            MilCmd.MilCmdD3DImage,
            MilCmd.MilCmdD3DImagePresent,
            // C 类：载荷是 Windows 事件句柄 / 原生 C++ 对象指针（Linux 无对应概念）
            MilCmd.MilCmdDoubleBufferedBitmap,
            MilCmd.MilCmdDoubleBufferedBitmapCopyForward,
            // B 类：媒体播放器（需先有播放器子系统）
            MilCmd.MilCmdMediaPlayer,
        };

        /// <summary>M1 返回 E_NOTIMPL 的命令数。</summary>
        public static int NotImplCount => s_notImpl.Count;

        public static bool IsNotImplemented(MilCmd cmd) => s_notImpl.Contains(cmd);

        public static IEnumerable<MilCmd> NotImplementedCommands => s_notImpl;

        /// <summary>docs/duce-commands.txt 里 118 条命令的总数（0x00–0x3d + 0x57–0x8e）。</summary>
        public const int TotalDuceCommandCount = 118;

        /// <summary>RenderData 绘图命令条数（0x3e–0x56），不在 118 之内但同样需要解码。</summary>
        public const int RenderDrawCommandCount = 25;
    }
}
