// Licensed to the .NET Foundation under one or more agreements.
//
// MilCmdRenderData(0x18) 载荷的解码。
//
// 【本层的职责边界】
//   只解**外层**结构（命令头 + CbData），并把内层指令流按记录框切成一条条
//   MilDrawCommand；**不执行任何绘图**——那是 skia-render 组（T4）的活。
//
// 【内层记录框格式】
//   [ Size : int32 ][ Id : MILCMD int32 ][ payload : Size-8 字节 ]
//   Size 含头自身。来源：PresentationCore/System/Windows/Media/RenderData.cs
//   的 WriteDataRecord（有 Debug.Assert(size % 8 == 0)）。
//
// 【内层指令体布局】
//   来源：PresentationCore/System/Windows/Media/Generated/RenderData.cs 的
//   MILCMD_DRAW_* / MILCMD_PUSH_* 结构体。这些结构体**不含 Type 字段**
//   （Type 已在记录头的 Id 里），偏移从 0 开始。
//
// 【未覆盖的部分】
//   契约 Contracts/MilDrawInstruction 是强类型的，没有"原始字节"字段，
//   因此 *Animate 变体、Guideline 系列、Effect 等无法无损映射到它上面。
//   这些指令只填 Command，原始字节一律保留在 RawPayloads 里交给渲染层。
//   详见 docs/unimplemented.md。

using System.Buffers.Binary;
using System.Collections.Generic;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Commands
{
    /// <summary>一段已解码的 RenderData：指令序列 + 每条指令的原始字节。</summary>
    internal sealed class MilRenderData : IMilRenderData
    {
        /// <summary>指令流原始字节（记录框 + 载荷），与命令里收到的一致。</summary>
        public byte[] Data = System.Array.Empty<byte>();

        private readonly List<MilDrawInstruction> _instructions = new List<MilDrawInstruction>();

        /// <summary>与 Instructions 一一对应的原始载荷字节（不含 8 字节记录头）。</summary>
        public readonly List<byte[]> RawPayloads = new List<byte[]>();

        public IReadOnlyList<MilDrawInstruction> Instructions => _instructions;

        /// <summary>解码器内部用的可写视图。</summary>
        internal List<MilDrawInstruction> InstructionList => _instructions;

        public int Count => _instructions.Count;
    }

    internal static class RenderDataDecoder
    {
        /// <summary>把 RenderData 的指令流切成记录并尽力填充 MilDrawInstruction。</summary>
        public static MilRenderData Decode(byte[] data)
        {
            var result = new MilRenderData { Data = data ?? System.Array.Empty<byte>() };

            RenderDataStream.Enumerate(result.Data, (id, payload) =>
            {
                byte[] raw = payload.ToArray();
                result.RawPayloads.Add(raw);
                result.InstructionList.Add(DecodeInstruction(id, raw));
            });

            return result;
        }

        private static MilDrawInstruction DecodeInstruction(MilDrawCommand id, byte[] p)
        {
            // 读取工具：越界一律返回 0，坏流不至于把整个进程带崩。
            static uint U32(byte[] b, int o) =>
                o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0u;

            static double D(byte[] b, int o) =>
                o + 8 <= b.Length ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o, 8)) : 0.0;

            switch (id)
            {
                case MilDrawCommand.MilDrawLine:
                    // point0@0(16) point1@16(16) hPen@32
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Point0 = new SkiaSharp.SKPoint((float)D(p, 0), (float)D(p, 8)),
                        Point1 = new SkiaSharp.SKPoint((float)D(p, 16), (float)D(p, 24)),
                        Pen = new MilResourceHandle(U32(p, 32)),
                    };

                case MilDrawCommand.MilDrawRectangle:
                    // rectangle@0(32) hBrush@32 hPen@36
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Rect = RectOf(D(p, 0), D(p, 8), D(p, 16), D(p, 24)),
                        Brush = new MilResourceHandle(U32(p, 32)),
                        Pen = new MilResourceHandle(U32(p, 36)),
                    };

                case MilDrawCommand.MilDrawRoundedRectangle:
                    // rectangle@0 radiusX@32 radiusY@40 hBrush@48 hPen@52
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Rect = RectOf(D(p, 0), D(p, 8), D(p, 16), D(p, 24)),
                        CornerRadius = new SkiaSharp.SKPoint((float)D(p, 32), (float)D(p, 40)),
                        Brush = new MilResourceHandle(U32(p, 48)),
                        Pen = new MilResourceHandle(U32(p, 52)),
                    };

                case MilDrawCommand.MilDrawEllipse:
                    // center@0 radiusX@16 radiusY@24 hBrush@32 hPen@36
                    // 契约没有 Center/Radius 字段：中心放 Point0，半径放 CornerRadius。
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Point0 = new SkiaSharp.SKPoint((float)D(p, 0), (float)D(p, 8)),
                        CornerRadius = new SkiaSharp.SKPoint((float)D(p, 16), (float)D(p, 24)),
                        Brush = new MilResourceHandle(U32(p, 32)),
                        Pen = new MilResourceHandle(U32(p, 36)),
                    };

                case MilDrawCommand.MilDrawGeometry:
                    // hBrush@0 hPen@4 hGeometry@8
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Brush = new MilResourceHandle(U32(p, 0)),
                        Pen = new MilResourceHandle(U32(p, 4)),
                        Geometry = new MilResourceHandle(U32(p, 8)),
                    };

                case MilDrawCommand.MilDrawImage:
                    // rectangle@0(32) hImageSource@32 —— 位图句柄暂寄存在 Geometry 字段
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Rect = RectOf(D(p, 0), D(p, 8), D(p, 16), D(p, 24)),
                        Geometry = new MilResourceHandle(U32(p, 32)),
                    };

                case MilDrawCommand.MilDrawGlyphRun:
                    // hForegroundBrush@0 hGlyphRun@4 —— 字形句柄暂寄存在 Geometry 字段
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Brush = new MilResourceHandle(U32(p, 0)),
                        Geometry = new MilResourceHandle(U32(p, 4)),
                    };

                case MilDrawCommand.MilDrawDrawing:
                    // hDrawing@0 —— 暂存 Geometry 字段
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Geometry = new MilResourceHandle(U32(p, 0)),
                    };

                case MilDrawCommand.MilPushClip:
                    // hClipGeometry@0
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Geometry = new MilResourceHandle(U32(p, 0)),
                    };

                case MilDrawCommand.MilPushOpacity:
                    // opacity@0
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Opacity = D(p, 0),
                    };

                case MilDrawCommand.MilPushTransform:
                    // hTransform@0 —— 句柄暂存 Geometry；矩阵要在渲染层结合资源表解析
                    return new MilDrawInstruction
                    {
                        Command = id,
                        Geometry = new MilResourceHandle(U32(p, 0)),
                    };

                case MilDrawCommand.MilPop:
                    return new MilDrawInstruction { Command = id };

                default:
                    // *Animate 变体、Guideline 系列、Effect、Video 等：
                    // 只记命令字，原始字节在 RawPayloads 里，留给渲染层。
                    return new MilDrawInstruction { Command = id };
            }
        }

        private static SkiaSharp.SKRect RectOf(double x, double y, double w, double h) =>
            new SkiaSharp.SKRect((float)x, (float)y, (float)(x + w), (float)(y + h));
    }
}
