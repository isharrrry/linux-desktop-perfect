// Licensed to the .NET Foundation under one or more agreements.
//
// PathGeometry 序列化块 → SKPath。
//
// 【字节来源】
//   MilCmdPathGeometry(0x5c) 的载荷尾部 FiguresSize 字节，被 T3 原样存进
//   MilPathGeometry.SerializedData（见 Commands/MilCommandDispatcher.cs:599，
//   注释明确写着"由渲染层解释——本层不猜段布局"）。所以解析活落在 T4 头上。
//
// 【布局】逐字段抄自上游：
//   Common/Graphics/wgx_core_types.cs:1010  MIL_PATHGEOMETRY  (48 字节)
//        Size@0  Flags@4  Bounds@8(MilRectD 32B)  FigureCount@40  ForcePacking@44
//   Common/Graphics/wgx_core_types.cs:1020  MIL_PATHFIGURE     (40 字节)
//        BackSize@0 Flags@4 Count@8 Size@12 StartPoint@16(Point 16B)
//        OffsetToLastSegment@32 ForcePacking@36
//   Common/Graphics/wgx_core_types.cs:1041+ MIL_SEGMENT_*
//        公共头：Type@0  Flags@4  BackSize@8
//        Line            32 = 16 + Point×1
//        Bezier          64 = 16 + Point×3
//        QuadraticBezier 48 = 16 + Point×2
//        Arc             64 = 16 + LargeArc(4) + Point(16) + Size(16) + XRotation(8) + Sweep(4) + Pad(4)
//        Poly            16 + Count(4) + Point×Count
//
// 【遍历方向】
//   上游 ByteStreamGeometryContext 是**向前** AppendData 的（figure 头 → 段 → 段 …），
//   BackSize/OffsetToLastSegment 只是给原生层反向走用的。既然每个段都能自算出长度，
//   这里直接向前扫，用 MIL_PATHFIGURE.Size 跳到下一个 figure。

using System;
using System.Buffers.Binary;
using SkiaSharp;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Rendering
{
    /// <summary>PathGeometry 序列化块的解析器。</summary>
    internal static class PathGeometryParser
    {
        private const int PathGeometryHeaderSize = 48;   // MIL_PATHGEOMETRY
        private const int PathFigureHeaderSize = 40;     // MIL_PATHFIGURE
        private const int SegmentHeaderSize = 16;        // MIL_SEGMENT 公共头（含 4 字节 ForcePacking）
        private const int PointSize = 16;                // System.Windows.Point = 2×double

        /// <summary>解析结果为 null 表示字节块非法（不抛异常，坏数据不该拖垮整帧）。</summary>
        public static SKPath Parse(byte[] data, MilFillRule fillRule) =>
            Parse(data, fillRule, false);

        /// <summary>
        /// <paramref name="fillableOnly"/>=true 时跳过 Flags 里没有 IsFillable 的图形。
        /// 【U1c 真机对拍】上游 CShapeBase::GetFillBounds(rect, fFillOnly, pMatrix) 在
        /// fFillOnly 时会 `if (!fFillOnly || figure.IsFillable())` 过滤
        /// （shapebase.cpp:1438），这就是 MilUtility_PathGeometryBounds 的
        /// fSkipHollows 参数的真身。真机 bounds_hollow_skip_true 只到 10（只算可填充图形）。
        /// </summary>
        public static SKPath Parse(byte[] data, MilFillRule fillRule, bool fillableOnly)
        {
            if (data == null || data.Length < PathGeometryHeaderSize) return null;

            var path = new SKPath { FillType = FillTypeOf(fillRule) };

            int figureCount = (int)U32(data, 40);
            int offset = PathGeometryHeaderSize;

            for (int f = 0; f < figureCount; f++)
            {
                if (offset + PathFigureHeaderSize > data.Length) { path.Dispose(); return null; }

                uint flags = U32(data, offset + 4);
                int segmentCount = (int)U32(data, offset + 8);
                int figureSize = (int)U32(data, offset + 12);
                if (figureSize < PathFigureHeaderSize || offset + figureSize > data.Length)
                {
                    path.Dispose();
                    return null;
                }

                if (fillableOnly && (flags & (uint)MilPathFigureFlags.IsFillable) == 0)
                {
                    offset += figureSize;
                    continue;
                }

                double startX = D(data, offset + 16);
                double startY = D(data, offset + 24);
                path.MoveTo((float)startX, (float)startY);

                int cursor = offset + PathFigureHeaderSize;
                int figureEnd = offset + figureSize;
                for (int s = 0; s < segmentCount; s++)
                {
                    if (cursor + SegmentHeaderSize > figureEnd) { path.Dispose(); return null; }
                    int size = SegmentSize(data, cursor, figureEnd);
                    if (size < 0) { path.Dispose(); return null; }

                    var type = (MilSegmentType)U32(data, cursor);
                    uint segFlags = U32(data, cursor + 4);
                    bool isAGap = (segFlags & (uint)MilCoreSegFlags.SegIsAGap) != 0;
                    bool isClosed = (segFlags & (uint)MilCoreSegFlags.SegClosed) != 0;

                    // SegIsAGap：这一段只是"抬笔移动"，不产生墨迹 —— 用 MoveTo 表达。
                    if (isAGap)
                    {
                        if (type == MilSegmentType.None) { /* 无点可去 */ }
                        else path.MoveTo((float)D(data, cursor + 16), (float)D(data, cursor + 24));
                    }
                    else
                    {
                        AppendSegment(path, data, cursor, type);
                    }

                    if (isClosed) path.Close();

                    cursor += size;
                }

                if ((flags & (uint)MilPathFigureFlags.IsClosed) != 0) path.Close();

                offset += figureSize;
            }

            return path;
        }

        private static void AppendSegment(SKPath path, byte[] data, int cursor, MilSegmentType type)
        {
            switch (type)
            {
                case MilSegmentType.Line:
                    path.LineTo((float)D(data, cursor + 16), (float)D(data, cursor + 24));
                    break;

                case MilSegmentType.Bezier:
                    path.CubicTo(
                        P(data, cursor + 16), P(data, cursor + 32), P(data, cursor + 48));
                    break;

                case MilSegmentType.QuadraticBezier:
                    path.QuadTo(P(data, cursor + 16), P(data, cursor + 32));
                    break;

                case MilSegmentType.Arc:
                    AppendArc(path, data, cursor);
                    break;

                case MilSegmentType.PolyLine:
                {
                    int count = (int)U32(data, cursor + 12);
                    for (int i = 0; i < count; i++)
                    {
                        int p = cursor + 16 + i * PointSize;
                        path.LineTo((float)D(data, p), (float)D(data, p + 8));
                    }
                    break;
                }

                case MilSegmentType.PolyBezier:
                {
                    int count = (int)U32(data, cursor + 12);
                    // PolyBezierSegment 的 Points 按 3 个一组（控制点×2 + 终点）。
                    for (int i = 0; i + 2 < count; i += 3)
                    {
                        path.CubicTo(
                            P(data, cursor + 16 + i * PointSize),
                            P(data, cursor + 16 + (i + 1) * PointSize),
                            P(data, cursor + 16 + (i + 2) * PointSize));
                    }
                    break;
                }

                case MilSegmentType.PolyQuadraticBezier:
                {
                    int count = (int)U32(data, cursor + 12);
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        path.QuadTo(
                            P(data, cursor + 16 + i * PointSize),
                            P(data, cursor + 16 + (i + 1) * PointSize));
                    }
                    break;
                }

                default:
                    break;   // None / 未知段类型：跳过，不产生墨迹
            }
        }

        /// <summary>
        /// Arc 段 → Skia ArcTo。
        ///
        /// ⚠️ 存疑处（对外诚实标注）：
        ///   WPF 的 ArcSegment 语义是 SVG 的 elliptical arc（端点参数化 + large-arc/sweep），
        ///   Skia 的 ArcTo 也是 SVG 语义，两者应当等价；但 Skia 在半径过小同样会做
        ///   "放大半径" 修正，而 WPF 的修正在 MIL 原生层，两者是否逐像素一致**没有验证过**。
        ///   XRotation 直接透传为 xAxisRotate。
        /// </summary>
        private static void AppendArc(SKPath path, byte[] data, int cursor)
        {
            bool largeArc = U32(data, cursor + 12) != 0;
            float x = (float)D(data, cursor + 16);
            float y = (float)D(data, cursor + 24);
            float rx = (float)D(data, cursor + 32);
            float ry = (float)D(data, cursor + 40);
            float xRotation = (float)D(data, cursor + 48);
            bool sweep = U32(data, cursor + 56) != 0;

            path.ArcTo(
                new SKPoint(rx, ry),
                xRotation,
                largeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
                sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                new SKPoint(x, y));
        }

        private static int SegmentSize(byte[] data, int cursor, int figureEnd)
        {
            var type = (MilSegmentType)U32(data, cursor);
            switch (type)
            {
                case MilSegmentType.Line: return 32;
                case MilSegmentType.Bezier: return 64;
                case MilSegmentType.QuadraticBezier: return 48;
                case MilSegmentType.Arc: return 64;
                case MilSegmentType.None: return 16;
                case MilSegmentType.PolyLine:
                case MilSegmentType.PolyBezier:
                case MilSegmentType.PolyQuadraticBezier:
                {
                    int count = (int)U32(data, cursor + 12);
                    if (count < 0) return -1;
                    long size = 16L + (long)count * PointSize;
                    if (cursor + size > figureEnd) return -1;
                    return (int)size;
                }
                default:
                    return -1;   // 未知段类型：无法向前推进，放弃整个几何
            }
        }

        private static SKPathFillType FillTypeOf(MilFillRule rule) =>
            rule == MilFillRule.Nonzero ? SKPathFillType.Winding : SKPathFillType.EvenOdd;

        private static uint U32(byte[] b, int o) =>
            o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0u;

        private static double D(byte[] b, int o) =>
            o + 8 <= b.Length ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o, 8)) : 0.0;

        private static SKPoint P(byte[] b, int o) => new SKPoint((float)D(b, o), (float)D(b, o + 8));
    }
}
