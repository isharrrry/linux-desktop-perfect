// Licensed to the .NET Foundation under one or more agreements.
//
// M7a：MilUtility_* 几何导出的**真实几何实现**。
//
// 这一层与 Interop/MilNative.Geometry.cs 分开，是因为它不碰任何互操作细节：
// 输入输出都是 SkiaSharp 的 SKPath / SKMatrix / 普通 double，因此可以单独做
// 数值断言（见 tests/.../MilExportTests.cs 的几何组）。
//
// 【为什么落在 Interop/ 而不是 Rendering/】
//   M7a 的改动边界是「Interop/ 下新文件」；Rendering/ 由别的组负责，本轮不碰。
//   本文件只**读**用 Rendering/PathGeometryParser（同程序集 internal 可见）。
//
// 【算法来源（逐条标注，便于核对）】
//   Arc → Bezier       : WpfGfx/core/geometry/utils.cpp:503 ArcToBezier()
//                        + GetArcAngle()/GetBezierDistance()/AcceptRadius()（同文件 300-500）
//                        这是 SVG 端点参数化椭圆的"最多 4 段三次贝塞尔"标准逼近。
//   展平（Flatten）     : de Casteljau 递归细分 + 平坦度判据（Skia 没有公开的 flatten API）。
//   布尔运算           : SKPath.Op（Skia 自己的路径裁剪，与 D2D 的 combine 同语义族）。
//   描边外扩           : SKPaint.GetFillPath（Skia 的"笔 + 路径 → 填充轮廓"）。
//   面积               : **梯形分解**（把所有顶点的 y 排序切带，逐带算交点的
//                        winding/parity 前缀和）。对多边形是精确的，并且同时支持
//                        EvenOdd 与 Nonzero 两种填充规则——直接对曲线积分做不到这点。
//   点包含             : 射线法 winding + 边界 epsilon 判定。
//   TileBrush 映射     : WpfGfx/core/resources/TileBrushUtils.cpp:43
//                        CalculateTileBrushMapping / CalculateViewboxToViewportMapping，
//                        + BrushTypeUtils.cpp:306 AdjustRelativeRectangle / :47 GetBrushTransform。
//
// 【精度口径】
//   MIL 原生内部用 FLOAT（单精度）算弧，最后把 float 点变换后输出。这里弧的中间量
//   也用 float 以便与上游逐步对齐，最终矩阵变换用 double。差异只在末位，且不改变
//   段数（段数由 large-arc/sweep 两个 bool 与弦长决定）。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Rendering;

namespace WpfGfx.Linux.Interop
{
    /// <summary>一条已展平的轮廓（闭合多边形）。</summary>
    internal sealed class MilContour
    {
        public readonly List<MilPointD> Points = new List<MilPointD>();

        /// <summary>显式 Close 过。填充/面积计算一律按闭合处理（与 SkPath 的 fill 语义一致）。</summary>
        public bool IsClosed;

        public int Count => Points.Count;
    }

    /// <summary>回传给 MilAddFigureCallback 的图形。</summary>
    internal sealed class MilFigureData
    {
        public bool IsFilled = true;
        public bool IsClosed;

        /// <summary>figure 起点。</summary>
        public MilPointF StartPoint;

        /// <summary>段类型字节（MILCoreSegFlags：SegTypeLine=1 / SegTypeBezier=2）。</summary>
        public readonly List<byte> Types = new List<byte>();

        /// <summary>段点：Line 段 1 个点，Bezier 段 3 个点（均不含起点）。</summary>
        public readonly List<MilPointF> Points = new List<MilPointF>();

        public bool HasCurves => Types.Contains((byte)MilCoreSegFlags.SegTypeBezier);
    }

    internal static class MilGeometryEngine
    {
        // ------------------------------------------------------------------
        //  常量（与上游 wgx_core_types.cs 一致）
        // ------------------------------------------------------------------

        public const int PathGeometryHeaderSize = 48;
        public const int PathFigureHeaderSize = 40;
        public const int SegmentHeaderSize = 16;
        public const int SegmentLineSize = 32;
        public const int SegmentBezierSize = 64;

        /// <summary>MIL_FUZZ / FUZZ：上游 geometry/utils.cpp 的 1e-6 量级。</summary>
        private const float Fuzz = 1e-6f;

        /// <summary>4/3 —— GetBezierDistance 的常数。</summary>
        private const double FourThirds = 4.0 / 3.0;

        private const double PiOver180 = Math.PI / 180.0;
        private const double TwoPi = Math.PI * 2.0;

        /// <summary>展平递归深度上限。极端退化的控制点会让平坦度判据失效。</summary>
        private const int MaxSubdivisionDepth = 20;

        // ==================================================================
        //  MilMatrix3x2D*（double[6]）↔ SKMatrix
        // ==================================================================

        /// <summary>
        /// double* 指向 6 个连续 double：S_11 S_12 S_21 S_22 DX DY（上游 MilMatrix3x2D 布局）。
        /// 字段映射与 Resources/VisualProjection.cs 里 TransformResolver.FromMil 完全一致：
        ///   ScaleX=S_11 SkewX=S_21 SkewY=S_12 ScaleY=S_22 TransX=DX TransY=DY
        /// </summary>
        public static unsafe SKMatrix ToSkMatrix(double* m)
        {
            if (m == null) return SKMatrix.Identity;

            return new SKMatrix
            {
                ScaleX = (float)m[0],
                SkewY = (float)m[1],
                SkewX = (float)m[2],
                ScaleY = (float)m[3],
                TransX = (float)m[4],
                TransY = (float)m[5],
                Persp2 = 1f,
            };
        }

        // ==================================================================
        //  序列化路径数据 ↔ SKPath
        // ==================================================================

        /// <summary>ptr + size 形式的 MIL_PATHGEOMETRY → SKPath。坏数据返回 null。</summary>
        public static unsafe SKPath ParsePathData(byte* pData, uint size, MilFillRule fillRule,
            bool fillableOnly = false)
        {
            if (pData == null || size < PathGeometryHeaderSize) return null;

            var bytes = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)pData, bytes, 0, (int)size);
            return PathGeometryParser.Parse(bytes, fillRule, fillableOnly);
        }

        /// <summary>
        /// SKPath → MIL_PATHGEOMETRY 字节块（<see cref="ParsePathData"/> 的逆）。
        ///
        /// 只输出 Line / Bezier 两种段（上游 MilGlyphRun_GetGlyphOutline 也只会产出这两类：
        /// 曲线一律做三次贝塞尔）。conic 段按标准公式降成三次贝塞尔（w=1 时精确）。
        /// BackSize / OffsetToLastSegment 按上游 ByteStreamGeometryContext 的语义回填，
        /// 便于原生侧反向遍历（托管侧的正向解析不用它们）。
        /// </summary>
        public static byte[] SerializePath(SKPath path)
        {
            if (path == null) return null;

            List<MilFigureData> figures = CollectFigures(path, stampSegClosedOnLast: true, out bool hasCurves);

            int total = PathGeometryHeaderSize;
            foreach (MilFigureData f in figures) total += PathFigureHeaderSize + SegmentBytesOf(f);

            var buffer = new byte[total];
            Span<byte> span = buffer.AsSpan();

            int offset = PathGeometryHeaderSize;
            var figureSizes = new int[figures.Count];

            for (int i = 0; i < figures.Count; i++)
            {
                MilFigureData f = figures[i];
                int figSize = PathFigureHeaderSize + SegmentBytesOf(f);
                figureSizes[i] = figSize;

                WriteU32(span, offset + 4, (uint)FigureFlagsOf(f));
                WriteU32(span, offset + 8, (uint)f.Types.Count);
                WriteU32(span, offset + 12, (uint)figSize);
                WriteDouble(span, offset + 16, f.StartPoint.X);
                WriteDouble(span, offset + 24, f.StartPoint.Y);
                WriteU32(span, offset + 36, 0);                                  // ForcePacking

                int cursor = offset + PathFigureHeaderSize;
                int pointIndex = 0;
                uint lastSegmentSize = 0;

                for (int s = 0; s < f.Types.Count; s++)
                {
                    byte type = f.Types[s];
                    if (type == (byte)MilCoreSegFlags.SegTypeBezier)
                    {
                        WriteU32(span, cursor + 0, (uint)MilSegmentType.Bezier);
                        WriteU32(span, cursor + 4, type);
                        WriteU32(span, cursor + 8, lastSegmentSize);
                        for (int k = 0; k < 3; k++)
                        {
                            MilPointF pt = f.Points[pointIndex + k];
                            WriteDouble(span, cursor + 16 + k * 16, pt.X);
                            WriteDouble(span, cursor + 24 + k * 16, pt.Y);
                        }
                        pointIndex += 3;
                        lastSegmentSize = SegmentBezierSize;
                        cursor += SegmentBezierSize;
                    }
                    else
                    {
                        WriteU32(span, cursor + 0, (uint)MilSegmentType.Line);
                        WriteU32(span, cursor + 4, type);
                        WriteU32(span, cursor + 8, lastSegmentSize);
                        MilPointF pt = f.Points[pointIndex];
                        WriteDouble(span, cursor + 16, pt.X);
                        WriteDouble(span, cursor + 24, pt.Y);
                        pointIndex += 1;
                        lastSegmentSize = SegmentLineSize;
                        cursor += SegmentLineSize;
                    }
                }

                // BackSize = 上一个图形的大小（上游 ByteStreamGeometryContext.cs:82），第一个图形为 0。
                WriteU32(span, offset + 0, i == 0 ? 0u : (uint)figureSizes[i - 1]);
                if (f.Types.Count > 0)
                    WriteU32(span, offset + 32, (uint)(figSize - LastSegmentBytes(f)));

                offset += figSize;
            }

            SKRect tight = path.GetTightBounds(out SKRect bounds) ? bounds : SKRect.Empty;

            WriteU32(span, 0, (uint)total);
            WriteU32(span, 4, (uint)GeometryFlagsOf(hasCurves));
            WriteDouble(span, 8, tight.Left);
            WriteDouble(span, 16, tight.Top);
            WriteDouble(span, 24, tight.Right);
            WriteDouble(span, 32, tight.Bottom);
            WriteU32(span, 40, (uint)figures.Count);
            WriteU32(span, 44, 0);

            _ = bounds;
            return buffer;
        }

        private static int SegmentBytesOf(MilFigureData f)
        {
            int bytes = 0;
            foreach (byte t in f.Types)
                bytes += t == (byte)MilCoreSegFlags.SegTypeBezier ? SegmentBezierSize : SegmentLineSize;
            return bytes;
        }

        private static int LastSegmentBytes(MilFigureData f) =>
            f.Types.Count == 0
                ? 0
                : (f.Types[f.Types.Count - 1] == (byte)MilCoreSegFlags.SegTypeBezier
                    ? SegmentBezierSize
                    : SegmentLineSize);

        private static MilPathFigureFlags FigureFlagsOf(MilFigureData f)
        {
            MilPathFigureFlags flags = MilPathFigureFlags.IsFillable;
            if (f.IsClosed) flags |= MilPathFigureFlags.IsClosed;
            if (f.HasCurves) flags |= MilPathFigureFlags.HasCurves;
            return flags;
        }

        private static MilPathGeometryFlags GeometryFlagsOf(bool hasCurves) =>
            MilPathGeometryFlags.BoundsValid |
            (hasCurves ? MilPathGeometryFlags.HasCurves : 0);

        private static void WriteU32(Span<byte> span, int offset, uint value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), value);

        private static void WriteDouble(Span<byte> span, int offset, double value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(offset, 8), value);

        // ==================================================================
        //  SKPath → 图形列表
        // ==================================================================

        /// <summary>
        /// 把 SKPath 拆成图形列表。曲线保持曲线（Bezier 段 = 3 个点）。
        /// 二次曲线用升阶公式（精确）；conic 用标准的三次逼近（w=1 时与升阶一致）。
        /// </summary>
        public static List<MilFigureData> CollectFigures(
            SKPath path, bool stampSegClosedOnLast, out bool hasCurves)
        {
            var figures = new List<MilFigureData>();
            hasCurves = false;
            if (path == null) return figures;

            MilFigureData current = null;
            var points = new SKPoint[4];
            bool finished = false;

            using (SKPath.RawIterator it = path.CreateRawIterator())
            {
                while (!finished)
                {
                    SKPathVerb verb = it.Next(points);
                    switch (verb)
                    {
                        case SKPathVerb.Move:
                            current = new MilFigureData
                            {
                                StartPoint = new MilPointF(points[0].X, points[0].Y),
                            };
                            figures.Add(current);
                            break;

                        case SKPathVerb.Line:
                            if (current == null) break;
                            current.Types.Add((byte)MilCoreSegFlags.SegTypeLine);
                            current.Points.Add(new MilPointF(points[1].X, points[1].Y));
                            break;

                        case SKPathVerb.Quad:
                        {
                            if (current == null) break;
                            AddQuadAsCubic(current, LastPointOf(current), points[1], points[2]);
                            hasCurves = true;
                            break;
                        }

                        case SKPathVerb.Conic:
                        {
                            if (current == null) break;
                            float w = it.ConicWeight();
                            SKPoint p0 = LastPointOf(current);
                            SKPoint c1, c2;
                            if (Math.Abs(w - 1f) < 1e-6f)
                            {
                                c1 = new SKPoint(p0.X + 2f / 3f * (points[1].X - p0.X),
                                                 p0.Y + 2f / 3f * (points[1].Y - p0.Y));
                                c2 = new SKPoint(points[2].X + 2f / 3f * (points[1].X - points[2].X),
                                                 points[2].Y + 2f / 3f * (points[1].Y - points[2].Y));
                            }
                            else
                            {
                                float k = 2f * w / (3f * (1f + w));
                                c1 = new SKPoint(p0.X + k * (points[1].X - p0.X),
                                                 p0.Y + k * (points[1].Y - p0.Y));
                                c2 = new SKPoint(points[2].X + k * (points[1].X - points[2].X),
                                                 points[2].Y + k * (points[1].Y - points[2].Y));
                            }
                            AddCubic(current, c1, c2, points[2]);
                            hasCurves = true;
                            break;
                        }

                        case SKPathVerb.Cubic:
                            if (current == null) break;
                            AddCubic(current, points[1], points[2], points[3]);
                            hasCurves = true;
                            break;

                        case SKPathVerb.Close:
                            if (current != null)
                            {
                                current.IsClosed = true;
                                if (stampSegClosedOnLast && current.Types.Count > 0)
                                {
                                    int last = current.Types.Count - 1;
                                    current.Types[last] |= (byte)MilCoreSegFlags.SegClosed;
                                }
                            }
                            break;

                        case SKPathVerb.Done:
                            finished = true;
                            break;
                    }
                }
            }

            return figures;
        }

        private static void AddCubic(MilFigureData f, SKPoint c1, SKPoint c2, SKPoint end)
        {
            f.Types.Add((byte)MilCoreSegFlags.SegTypeBezier);
            f.Points.Add(new MilPointF(c1.X, c1.Y));
            f.Points.Add(new MilPointF(c2.X, c2.Y));
            f.Points.Add(new MilPointF(end.X, end.Y));
        }

        private static void AddQuadAsCubic(MilFigureData f, SKPoint p0, SKPoint p1, SKPoint p2)
        {
            // 二次 → 三次升阶（精确）：c = p + 2/3 (control - p)
            var c1 = new SKPoint(p0.X + 2f / 3f * (p1.X - p0.X), p0.Y + 2f / 3f * (p1.Y - p0.Y));
            var c2 = new SKPoint(p2.X + 2f / 3f * (p1.X - p2.X), p2.Y + 2f / 3f * (p1.Y - p2.Y));
            AddCubic(f, c1, c2, p2);
        }

        private static SKPoint LastPointOf(MilFigureData f)
        {
            if (f.Points.Count > 0)
            {
                MilPointF last = f.Points[f.Points.Count - 1];
                return new SKPoint(last.X, last.Y);
            }
            return new SKPoint(f.StartPoint.X, f.StartPoint.Y);
        }

        // ==================================================================
        //  展平
        // ==================================================================

        /// <summary>
        /// 把 SKPath 展平成轮廓列表。曲线用 de Casteljau 递归细分，
        /// 平坦度判据是"控制点到弦的最大距离 ≤ tolerance"。
        /// tolerance ≤ 0 时取 0.1。
        /// </summary>
        public static List<MilContour> Flatten(SKPath path, double tolerance)
        {
            var contours = new List<MilContour>();
            if (path == null) return contours;

            double tol = tolerance > 0 ? tolerance : 0.1;
            var points = new SKPoint[4];

            MilContour current = null;
            bool finished = false;

            using (SKPath.RawIterator it = path.CreateRawIterator())
            {
                while (!finished)
                {
                    SKPathVerb verb = it.Next(points);
                    switch (verb)
                    {
                        case SKPathVerb.Move:
                            current = new MilContour();
                            contours.Add(current);
                            current.Points.Add(new MilPointD(points[0].X, points[0].Y));
                            break;

                        case SKPathVerb.Line:
                            EnsureContour(contours, ref current, points[0]);
                            current.Points.Add(new MilPointD(points[1].X, points[1].Y));
                            break;

                        case SKPathVerb.Quad:
                            EnsureContour(contours, ref current, points[0]);
                            FlattenQuad(current.Points, points[0], points[1], points[2], tol, 0);
                            break;

                        case SKPathVerb.Conic:
                        {
                            EnsureContour(contours, ref current, points[0]);
                            float w = it.ConicWeight();
                            if (Math.Abs(w - 1f) < 1e-6f)
                            {
                                FlattenQuad(current.Points, points[0], points[1], points[2], tol, 0);
                            }
                            else
                            {
                                float k = 2f * w / (3f * (1f + w));
                                var c1 = new SKPoint(points[0].X + k * (points[1].X - points[0].X),
                                                     points[0].Y + k * (points[1].Y - points[0].Y));
                                var c2 = new SKPoint(points[2].X + k * (points[1].X - points[2].X),
                                                     points[2].Y + k * (points[1].Y - points[2].Y));
                                FlattenCubic(current.Points, points[0], c1, c2, points[2], tol, 0);
                            }
                            break;
                        }

                        case SKPathVerb.Cubic:
                            EnsureContour(contours, ref current, points[0]);
                            FlattenCubic(current.Points, points[0], points[1], points[2], points[3], tol, 0);
                            break;

                        case SKPathVerb.Close:
                            if (current != null) current.IsClosed = true;
                            break;

                        case SKPathVerb.Done:
                            finished = true;
                            break;
                    }
                }
            }

            return contours;
        }

        private static void EnsureContour(List<MilContour> contours, ref MilContour current, SKPoint first)
        {
            if (current != null) return;
            current = new MilContour();
            contours.Add(current);
            current.Points.Add(new MilPointD(first.X, first.Y));
        }

        private static void FlattenQuad(
            List<MilPointD> sink, SKPoint p0, SKPoint p1, SKPoint p2, double tol, int depth)
        {
            if (depth >= MaxSubdivisionDepth || DistanceToLine(p1, p0, p2) <= tol)
            {
                sink.Add(new MilPointD(p2.X, p2.Y));
                return;
            }

            SKPoint a = Mid(p0, p1), b = Mid(p1, p2), c = Mid(a, b);
            FlattenQuad(sink, p0, a, c, tol, depth + 1);
            FlattenQuad(sink, c, b, p2, tol, depth + 1);
        }

        private static void FlattenCubic(
            List<MilPointD> sink, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, double tol, int depth)
        {
            if (depth >= MaxSubdivisionDepth ||
                (DistanceToLine(p1, p0, p3) <= tol && DistanceToLine(p2, p0, p3) <= tol))
            {
                sink.Add(new MilPointD(p3.X, p3.Y));
                return;
            }

            SKPoint a = Mid(p0, p1), b = Mid(p1, p2), c = Mid(p2, p3);
            SKPoint d = Mid(a, b), e = Mid(b, c);
            SKPoint f = Mid(d, e);
            FlattenCubic(sink, p0, a, d, f, tol, depth + 1);
            FlattenCubic(sink, f, e, c, p3, tol, depth + 1);
        }

        private static SKPoint Mid(SKPoint a, SKPoint b) =>
            new SKPoint((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

        private static double DistanceToLine(SKPoint p, SKPoint a, SKPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 1e-18)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double cross = (double)(p.X - a.X) * dy - (double)(p.Y - a.Y) * dx;
            return Math.Abs(cross) / Math.Sqrt(len2);
        }

        // ==================================================================
        //  点包含 / 面积 / 包围盒
        // ==================================================================

        /// <summary>
        /// 射线法点包含。落在边界上（到任一线段距离 ≤ 1e-9 相对量）一律算命中——
        /// 与 WPF/D2D 的填充语义一致（边界属于填充区域）。
        /// </summary>
        public static bool Contains(List<MilContour> contours, double x, double y, MilFillRule rule)
        {
            if (contours == null) return false;

            foreach (MilContour c in contours)
            {
                if (OnBoundary(c, x, y)) return true;
            }

            int winding = 0;
            foreach (MilContour c in contours)
            {
                List<MilPointD> pts = c.Points;
                if (pts.Count < 2) continue;

                for (int i = 0; i < pts.Count; i++)
                {
                    MilPointD a = pts[i];
                    MilPointD b = pts[(i + 1) % pts.Count];

                    if (a.Y <= y)
                    {
                        if (b.Y > y && IsLeft(a, b, x, y) > 0) winding++;
                    }
                    else
                    {
                        if (b.Y <= y && IsLeft(a, b, x, y) < 0) winding--;
                    }
                }
            }

            return rule == MilFillRule.Nonzero ? winding != 0 : (winding & 1) != 0;
        }

        private static double IsLeft(MilPointD a, MilPointD b, double x, double y) =>
            (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);

        private static bool OnBoundary(MilContour c, double x, double y)
        {
            List<MilPointD> pts = c.Points;
            if (pts.Count < 2) return false;

            for (int i = 0; i < pts.Count; i++)
            {
                if (OnSegment(pts[i], pts[(i + 1) % pts.Count], x, y)) return true;
            }
            return false;
        }

        private static bool OnSegment(MilPointD a, MilPointD b, double x, double y)
        {
            const double eps = 1e-9;

            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            if (x < minX - eps || x > maxX + eps || y < minY - eps || y > maxY + eps) return false;

            double cross = (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            return Math.Abs(cross) <= eps * Math.Max(1.0, len);
        }

        /// <summary>
        /// 填充区域的面积。**梯形分解**：把所有顶点的 y 排序切成水平带，每个带内取
        /// 中线 y 求所有边与该线的交点，按 x 排序后做 winding/parity 前缀扫描，
        /// 落在填充内的 x 区间宽度乘以带高即该带面积。
        /// 对多边形精确，且同时支持两种填充规则（含自交图形）。
        /// </summary>
        public static double Area(List<MilContour> contours, MilFillRule rule)
        {
            if (contours == null || contours.Count == 0) return 0.0;

            var ys = new List<double>();
            foreach (MilContour c in contours)
            {
                foreach (MilPointD p in c.Points) ys.Add(p.Y);
            }
            if (ys.Count < 2) return 0.0;

            // 【U1c 真机对拍纠正】关键 y 必须同时包含**自交点的 y**。
            // 只按顶点 y 分带时，一个带内部如果藏着自交点，带中点取到的 crossing
            // 里会出现两条重合项（蝴蝶结在 y=5 处两条边交于 x=5），
            // parity 前缀扫描会把 [0,5] 和 [5,10] 都算成"内部" → 面积翻倍。
            // 真机 area_bowtie_* 给 50，本工程初版给 100，就是缺了这一层细分。
            AddSelfIntersectionYs(contours, ys);

            ys.Sort();

            var crossings = new List<(double X, int Dir)>();
            double area = 0.0;

            for (int i = 0; i + 1 < ys.Count; i++)
            {
                double y0 = ys[i], y1 = ys[i + 1];
                if (!(y1 > y0)) continue;

                double ymid = 0.5 * (y0 + y1);
                crossings.Clear();

                foreach (MilContour c in contours)
                {
                    List<MilPointD> pts = c.Points;
                    if (pts.Count < 2) continue;

                    for (int k = 0; k < pts.Count; k++)
                    {
                        MilPointD a = pts[k];
                        MilPointD b = pts[(k + 1) % pts.Count];
                        if (a.Y == b.Y) continue;

                        bool crosses = (a.Y <= ymid && b.Y > ymid) || (b.Y <= ymid && a.Y > ymid);
                        if (!crosses) continue;

                        double t = (ymid - a.Y) / (b.Y - a.Y);
                        crossings.Add((a.X + t * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                    }
                }

                if (crossings.Count < 2) continue;
                crossings.Sort((l, r) => l.X.CompareTo(r.X));

                int winding = 0, parity = 0;
                for (int k = 0; k + 1 < crossings.Count; k++)
                {
                    winding += crossings[k].Dir;
                    parity ^= 1;

                    bool inside = rule == MilFillRule.Nonzero ? winding != 0 : parity != 0;
                    if (inside)
                    {
                        double w = crossings[k + 1].X - crossings[k].X;
                        if (w > 0) area += w * (y1 - y0);
                    }
                }
            }

            return Math.Abs(area);
        }

        /// <summary>
        /// 把所有"真交点"的 y 追加进关键 y 列表（自适应分带的细分依据）。
        /// 只处理严格相交（两侧参数都在 (0,1) 开区间）——端点相交的 y 已经是顶点 y；
        /// 共线重叠段不产生新的关键 y（重叠段的端点在顶点集合里已经有了）。
        /// 用包围盒先筛一遍，避免 O(n²) 在展平圆这种上千条边的输入上失控。
        /// </summary>
        private static void AddSelfIntersectionYs(List<MilContour> contours, List<double> ys)
        {
            var edges = new List<(double X1, double Y1, double X2, double Y2,
                double MinX, double MaxX, double MinY, double MaxY)>();
            foreach (MilContour c in contours)
            {
                List<MilPointD> pts = c.Points;
                if (pts.Count < 2) continue;
                for (int k = 0; k < pts.Count; k++)
                {
                    MilPointD a = pts[k];
                    MilPointD b = pts[(k + 1) % pts.Count];
                    if (a.X == b.X && a.Y == b.Y) continue;
                    edges.Add((a.X, a.Y, b.X, b.Y,
                        Math.Min(a.X, b.X), Math.Max(a.X, b.X),
                        Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                }
            }

            int n = edges.Count;
            if (n < 4 || n > 20000) return;   // 规模保护：极端输入宁可退回纯顶点分带

            for (int i = 0; i < n; i++)
            {
                var e = edges[i];
                for (int j = i + 1; j < n; j++)
                {
                    var f = edges[j];
                    if (e.MaxX < f.MinX || f.MaxX < e.MinX) continue;
                    if (e.MaxY < f.MinY || f.MaxY < e.MinY) continue;

                    double rx = e.X2 - e.X1, ry = e.Y2 - e.Y1;
                    double sx = f.X2 - f.X1, sy = f.Y2 - f.Y1;
                    double denom = rx * sy - ry * sx;
                    if (denom == 0.0) continue;

                    double qpx = f.X1 - e.X1, qpy = f.Y1 - e.Y1;
                    double t = (qpx * sy - qpy * sx) / denom;
                    double u = (qpx * ry - qpy * rx) / denom;
                    if (!(t > 0.0 && t < 1.0 && u > 0.0 && u < 1.0)) continue;

                    double y = e.Y1 + t * ry;
                    if (!double.IsNaN(y) && !double.IsInfinity(y)) ys.Add(y);
                }
            }
        }

        /// <summary>轮廓的紧包围盒；无点返回 (0,0,0,0)。</summary>
        public static MilRectD Bounds(List<MilContour> contours)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            bool any = false;

            if (contours != null)
            {
                foreach (MilContour c in contours)
                {
                    foreach (MilPointD p in c.Points)
                    {
                        any = true;
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                }
            }

            return any ? new MilRectD(minX, minY, maxX, maxY) : MilRectD.Empty;
        }

        /// <summary>展平容差：fRelative 时按包围盒最大边长缩放（WPF ToleranceType.Relative 口径）。</summary>
        public static double ResolveTolerance(double tolerance, bool relative, MilRectD bounds)
        {
            double tol = tolerance > 0 ? tolerance : 0.1;
            if (!relative) return tol;

            double extent = Math.Max(bounds.Width, bounds.Height);
            if (!(extent > 0)) extent = 1.0;
            return tol * extent;
        }

        // ==================================================================
        //  Arc → Bezier（上游 WpfGfx/core/geometry/utils.cpp:503 的逐行移植）
        // ==================================================================

        /// <summary>
        /// SVG 端点参数化椭圆弧 → 最多 4 段三次贝塞尔。
        /// 返回值：-1 = 退化成点；0 = 退化成直线（pPt[0] 写终点）；1..4 = 段数。
        /// pPt 需要 12 个 MilPointD 的空间；结果已乘 pMatrix。
        /// </summary>
        public static unsafe int ArcToBezier(
            MilPointD ptStart, MilSizeD radii, double rotation,
            bool fLargeArc, bool fSweepUp, MilPointD ptEnd,
            double* pMatrix, MilPointD* pPt)
        {
            if (pPt == null) return -1;

            float xStart = (float)ptStart.X, yStart = (float)ptStart.Y;
            float xEnd = (float)ptEnd.X, yEnd = (float)ptEnd.Y;
            float xRadius = (float)radii.Width, yRadius = (float)radii.Height;
            float rRotation = (float)rotation;

            float x, y, rHalfChord2, rCos, rSin, rCosArcAngle, rSinArcAngle,
                  xCenter, yCenter, rBezDist;
            float rFuzz2 = Fuzz * Fuzz;
            bool fZeroCenter = false;
            int cPieces = -1;

            var pieces = new MilPointD[12];

            // Transform 1：原点移到弦的中点
            x = 0.5f * (xEnd - xStart);
            y = 0.5f * (yEnd - yStart);
            rHalfChord2 = x * x + y * y;

            // 退化：弦长 ~ 0 → 退化成点
            if (rHalfChord2 < rFuzz2) return -1;

            // 退化：半径 ~ 0 → 退化成直线
            if (!AcceptRadius(rHalfChord2, rFuzz2, ref xRadius) ||
                !AcceptRadius(rHalfChord2, rFuzz2, ref yRadius))
            {
                pieces[0] = new MilPointD(xEnd, yEnd);
                TransformResult(pMatrix, pieces, 1, pPt);
                return 0;
            }

            // Transform 2：旋转到椭圆的坐标系
            if (Math.Abs(rRotation) < Fuzz)
            {
                rCos = 1.0f;
                rSin = 0.0f;
            }
            else
            {
                rRotation = (float)(-rRotation * PiOver180);
                rCos = (float)Math.Cos(rRotation);
                rSin = (float)Math.Sin(rRotation);

                float rTemp = x * rCos - y * rSin;
                y = x * rSin + y * rCos;
                x = rTemp;
            }

            // Transform 3：缩放到单位圆
            x /= xRadius;
            y /= yRadius;
            rHalfChord2 = x * x + y * y;

            if (rHalfChord2 > 1.0f)
            {
                float rTemp = (float)Math.Sqrt(rHalfChord2);
                xRadius *= rTemp;
                yRadius *= rTemp;
                xCenter = yCenter = 0;
                fZeroCenter = true;

                x /= rTemp;
                y /= rTemp;
            }
            else
            {
                float rTemp = (float)Math.Sqrt((1.0f - rHalfChord2) / rHalfChord2);
                if (fLargeArc != fSweepUp)
                {
                    xCenter = -rTemp * y;
                    yCenter = rTemp * x;
                }
                else
                {
                    xCenter = rTemp * y;
                    yCenter = -rTemp * x;
                }
            }

            // Transform 4：原点移到圆心；起点 (-x,-y)，终点 (x,y)
            var ptStartU = new MilPointF(-x - xCenter, -y - yCenter);
            var ptEndU = new MilPointF(x - xCenter, y - yCenter);

            // 回到原坐标系的矩阵（上面 4 步变换的逆）
            double m00 = rCos * xRadius, m01 = -rSin * xRadius;
            double m10 = rSin * yRadius, m11 = rCos * yRadius;
            double m20 = 0.5 * (xEnd + xStart), m21 = 0.5 * (yEnd + yStart);
            if (!fZeroCenter)
            {
                m20 += m00 * xCenter + m10 * yCenter;
                m21 += m01 * xCenter + m11 * yCenter;
            }

            GetArcAngle(ptStartU, ptEndU, fLargeArc, fSweepUp,
                out rCosArcAngle, out rSinArcAngle, ref cPieces);

            rBezDist = (float)GetBezierDistance(rCosArcAngle, 1.0f);
            if (!fSweepUp) rBezDist = -rBezDist;

            var vecToBez1 = new MilPointF(-rBezDist * ptStartU.Y, rBezDist * ptStartU.X);

            int j = 0;
            for (int i = 1; i < cPieces; i++)
            {
                var ptPieceEnd = new MilPointF(
                    ptStartU.X * rCosArcAngle - ptStartU.Y * rSinArcAngle,
                    ptStartU.X * rSinArcAngle + ptStartU.Y * rCosArcAngle);
                var vecToBez2 = new MilPointF(-rBezDist * ptPieceEnd.Y, rBezDist * ptPieceEnd.X);

                pieces[j++] = TransformPoint(m00, m01, m10, m11, m20, m21,
                    ptStartU.X + vecToBez1.X, ptStartU.Y + vecToBez1.Y);
                pieces[j++] = TransformPoint(m00, m01, m10, m11, m20, m21,
                    ptPieceEnd.X - vecToBez2.X, ptPieceEnd.Y - vecToBez2.Y);
                pieces[j++] = TransformPoint(m00, m01, m10, m11, m20, m21, ptPieceEnd.X, ptPieceEnd.Y);

                ptStartU = ptPieceEnd;
                vecToBez1 = vecToBez2;
            }

            var vecLast = new MilPointF(-rBezDist * ptEndU.Y, rBezDist * ptEndU.X);
            pieces[j++] = TransformPoint(m00, m01, m10, m11, m20, m21,
                ptStartU.X + vecToBez1.X, ptStartU.Y + vecToBez1.Y);
            pieces[j++] = TransformPoint(m00, m01, m10, m11, m20, m21,
                ptEndU.X - vecLast.X, ptEndU.Y - vecLast.Y);
            pieces[j] = new MilPointD(xEnd, yEnd);

            TransformResult(pMatrix, pieces, 3 * cPieces, pPt);
            return cPieces;
        }

        /// <summary>把点乘以 pMatrix（null 表示不变换），写入 pPt。</summary>
        private static unsafe void TransformResult(double* pMatrix, MilPointD[] pieces, int count, MilPointD* pPt)
        {
            if (pMatrix == null)
            {
                for (int i = 0; i < count; i++) pPt[i] = pieces[i];
                return;
            }

            SKMatrix m = ToSkMatrix(pMatrix);
            for (int i = 0; i < count; i++)
            {
                SKPoint mapped = m.MapPoint(new SKPoint((float)pieces[i].X, (float)pieces[i].Y));
                pPt[i] = new MilPointD(mapped.X, mapped.Y);
            }
        }

        private static MilPointD TransformPoint(
            double m00, double m01, double m10, double m11, double m20, double m21,
            float px, float py) =>
            new MilPointD(px * m00 + py * m10 + m20, px * m01 + py * m11 + m21);

        /// <summary>上游 utils.cpp:443 AcceptRadius：半径相对弦长太小时拒绝（NaN 放行）。</summary>
        private static bool AcceptRadius(float rHalfChord2, float rFuzz2, ref float rRadius)
        {
            bool accept = !(rRadius * rRadius <= rHalfChord2 * rFuzz2);
            if (accept && rRadius < 0) rRadius = -rRadius;
            return accept;
        }

        /// <summary>上游 utils.cpp:461 GetArcAngle：把弧切成 ≤90° 的等分片并回填片数。</summary>
        private static void GetArcAngle(
            MilPointF start, MilPointF end, bool fLargeArc, bool fSweepUp,
            out float rCosArcAngle, out float rSinArcAngle, ref int cPieces)
        {
            rCosArcAngle = start.X * end.X + start.Y * end.Y;
            rSinArcAngle = start.X * end.Y - start.Y * end.X;

            if (rCosArcAngle >= 0)
            {
                if (fLargeArc) cPieces = 4;                 // 270°..360°
                else { cPieces = 1; return; }               // 0°..90°：cos/sin 已是答案
            }
            else if (fLargeArc) cPieces = 3;                // 180°..270°
            else cPieces = 2;                               // 90°..180°

            float angle = (float)Math.Atan2(rSinArcAngle, rCosArcAngle);
            if (fSweepUp)
            {
                if (angle < 0) angle += (float)TwoPi;
            }
            else
            {
                if (angle > 0) angle -= (float)TwoPi;
            }
            angle /= cPieces;
            rCosArcAngle = (float)Math.Cos(angle);
            rSinArcAngle = (float)Math.Sin(angle);
        }

        /// <summary>上游 utils.cpp:308 GetBezierDistance：控制点向量长度占半径的比例。</summary>
        private static double GetBezierDistance(double rDot, double rRadius)
        {
            double radSquared = rRadius * rRadius;
            double a = 0.5 * (radSquared + rDot);
            if (a < 0) return 0.0;

            double denomSquared = radSquared - a;
            if (denomSquared <= 0) return 0.0;

            double denom = Math.Sqrt(denomSquared);
            double numer = FourThirds * (rRadius - Math.Sqrt(a));
            if (numer <= denom * Fuzz) return 0.0;
            return numer / denom;
        }

        // ==================================================================
        //  TileBrush 映射（上游 TileBrushUtils.cpp:43 / BrushTypeUtils.cpp）
        // ==================================================================

        /// <summary>
        /// 计算 TileBrush 的 Viewport/Viewbox 绝对化 + Content→World 矩阵。
        /// 与上游 MIL 导出一样：内容缩放固定 1.0（那只对 ImageBrush 有意义）。
        /// </summary>
        public static unsafe void GetTileBrushMapping(
            D3DMATRIX* transform, D3DMATRIX* relativeTransform,
            MilStretch stretch, MilAlignmentX alignmentX, MilAlignmentY alignmentY,
            MilBrushMappingMode viewportUnits, MilBrushMappingMode viewboxUnits,
            MilPointAndSizeD* shapeFillBounds, MilPointAndSizeD* contentBounds,
            ref MilPointAndSizeD viewport, ref MilPointAndSizeD viewbox,
            out D3DMATRIX contentToWorld, out int brushIsEmpty)
        {
            brushIsEmpty = 0;
            contentToWorld = D3DMATRIX.Identity;

            MilPointAndSizeD sizing = shapeFillBounds != null ? *shapeFillBounds : MilPointAndSizeD.Empty;
            MilPointAndSizeD content = contentBounds != null ? *contentBounds : MilPointAndSizeD.Empty;

            // ---- GetAbsoluteViewRectangles ----
            if (viewportUnits == MilBrushMappingMode.RelativeToBoundingBox)
                AdjustRelativeRectangle(sizing, ref viewport);
            if (viewboxUnits == MilBrushMappingMode.RelativeToBoundingBox)
                AdjustRelativeRectangle(content, ref viewbox);

            // 规范：Viewbox 或 Viewport 为空 → 这个笔刷什么都不画。
            //
            // ⚠️ 与上游 IsRectEmptyOrInvalid 的差异（刻意加固，已在报告里标注）：
            //   上游那个判断只把"Rect.Empty 形态或含 NaN/负宽高"当空，**0 宽或 0 高
            //   不算空**——于是紧跟着的 Viewport.Width / Viewbox.Width 会除以 0，
            //   产出 +INF/NaN 的矩阵。WPF 的 TileBrush 文档把"Viewbox/Viewport 为空"
            //   描述成"什么都不画"，零面积矩形在语义上就是空，所以这里把
            //   Width <= 0 || Height <= 0 也并入空判断，宁可少画也不要产出 NaN 矩阵。
            if (viewport.IsEmptyOrInvalid || viewbox.IsEmptyOrInvalid ||
                viewport.Width <= 0 || viewport.Height <= 0 ||
                viewbox.Width <= 0 || viewbox.Height <= 0)
            {
                brushIsEmpty = 1;
                return;
            }

            // ---- Content → Viewport（内容缩放 1.0）----
            D3DMATRIX contentToViewport = Multiply(
                D3DMATRIX.Identity,
                CalculateViewboxToViewportMapping(viewport, viewbox, stretch, alignmentX, alignmentY));

            // ---- Viewport → World ----
            D3DMATRIX viewportToWorld = GetBrushTransform(relativeTransform, transform, sizing);

            contentToWorld = Multiply(contentToViewport, viewportToWorld);
        }

        /// <summary>上游 BrushTypeUtils.cpp:306 AdjustRelativeRectangle。</summary>
        private static void AdjustRelativeRectangle(MilPointAndSizeD boundingBox, ref MilPointAndSizeD rect)
        {
            if (boundingBox.IsEmptyOrInvalid || rect.IsEmptyOrInvalid)
            {
                rect = MilPointAndSizeD.Empty;
                return;
            }

            rect.X = boundingBox.X + rect.X * boundingBox.Width;
            rect.Y = boundingBox.Y + rect.Y * boundingBox.Height;
            rect.Width *= boundingBox.Width;
            rect.Height *= boundingBox.Height;
        }

        /// <summary>上游 TileBrushUtils.cpp:268 CalculateViewboxToViewportMapping。</summary>
        private static D3DMATRIX CalculateViewboxToViewportMapping(
            MilPointAndSizeD viewport, MilPointAndSizeD viewbox,
            MilStretch stretch, MilAlignmentX halign, MilAlignmentY valign)
        {
            double scaleX = 1.0, scaleY = 1.0;
            double transX = 0, transY = 0;
            double alignX = 0, alignY = 0;

            if (stretch != MilStretch.None)
            {
                scaleX = viewport.Width / viewbox.Width;
                scaleY = viewport.Height / viewbox.Height;

                switch (stretch)
                {
                    case MilStretch.Uniform:
                        scaleX = scaleY = Math.Min(scaleX, scaleY);
                        break;
                    case MilStretch.UniformToFill:
                        scaleX = scaleY = Math.Max(scaleX, scaleY);
                        break;
                }
            }

            switch (halign)
            {
                case MilAlignmentX.Left:
                    transX = -viewbox.X;
                    alignX = viewport.X;
                    break;
                case MilAlignmentX.Center:
                    transX = -(viewbox.X + viewbox.Width / 2.0);
                    alignX = viewport.X + viewport.Width / 2.0;
                    break;
                case MilAlignmentX.Right:
                    transX = -(viewbox.X + viewbox.Width);
                    alignX = viewport.X + viewport.Width;
                    break;
            }

            switch (valign)
            {
                case MilAlignmentY.Top:
                    transY = -viewbox.Y;
                    alignY = viewport.Y;
                    break;
                case MilAlignmentY.Center:
                    transY = -(viewbox.Y + viewbox.Height / 2.0);
                    alignY = viewport.Y + viewport.Height / 2.0;
                    break;
                case MilAlignmentY.Bottom:
                    // 【U1c 真机对拍修】原实现误写 `viewport.Y + viewbox.Height`
                    // （上方 halign 的 Right 分支是 viewport.Width，这里被抄成了
                    //  viewbox.Height）——上游是 `prcViewport->Y + prcViewport->Height`
                    // （TileBrushUtils.cpp:329-333）。viewport 与 viewbox 高度相等的
                    // 输入掩盖了这个错；真机 tb_uniform_right_bottom 的 M42=0、
                    // 本工程初版 -50（viewport.Height=100 / viewbox.Height=50 / scale=2）。
                    transY = -(viewbox.Y + viewbox.Height);
                    alignY = viewport.Y + viewport.Height;
                    break;
            }

            return D3DMATRIX.FromAffine(
                (float)scaleX, 0f, 0f, (float)scaleY,
                (float)(transX * scaleX + alignX), (float)(transY * scaleY + alignY));
        }

        /// <summary>
        /// 上游 BrushTypeUtils.cpp:47 GetBrushTransform。
        /// RelativeTransform 只在包围盒宽高都非 0 时生效（退化形状上按"未设置"处理）。
        /// </summary>
        private static unsafe D3DMATRIX GetBrushTransform(
            D3DMATRIX* relative, D3DMATRIX* absolute, MilPointAndSizeD boundingBox)
        {
            bool set = false;
            D3DMATRIX result = D3DMATRIX.Identity;

            if (relative != null && boundingBox.Width != 0.0 && boundingBox.Height != 0.0)
            {
                result = ConvertRelativeTransformToAbsolute(boundingBox, *relative);
                set = true;
            }

            if (absolute != null)
            {
                result = set ? Multiply(result, *absolute) : *absolute;
                set = true;
            }

            return set ? result : D3DMATRIX.Identity;
        }

        /// <summary>
        /// 相对变换绝对化（上游 BrushTypeUtils.cpp:ConvertRelativeTransformToAbsolute）。
        ///
        /// 语义：把"以 [0,1] 比例为单位"的矩阵换算成"以绝对坐标为单位"的矩阵。
        /// 设包围盒原点 (ox,oy)、尺寸 (w,h)，令 u=(x−ox)/w、v=(y−oy)/h，则
        ///     n' = (u,v,1)·M    →    p' = (ox + w·n'x, oy + h·n'y)
        /// 展开后：
        ///     x' = ox + w·M41 + (x−ox)·M11 + (y−oy)·(w/h)·M21
        ///     y' = oy + h·M42 + (x−ox)·(h/w)·M12 + (y−oy)·M22
        /// 即
        ///     A11=M11  A12=M12·h/w  A21=M21·w/h  A22=M22
        ///     DX = ox + w·M41 − ox·A11 − oy·A21
        ///     DY = oy + h·M42 − ox·A12 − oy·A22
        /// 例：包围盒 (0,0,100,100)、相对平移 (0.1,0.2) → 绝对平移 (10,20)。
        /// </summary>
        private static D3DMATRIX ConvertRelativeTransformToAbsolute(MilPointAndSizeD bounds, D3DMATRIX relative)
        {
            double w = bounds.Width, h = bounds.Height;
            double ox = bounds.X, oy = bounds.Y;

            double a11 = relative.M11;
            double a12 = relative.M12 * h / w;
            double a21 = relative.M21 * w / h;
            double a22 = relative.M22;

            double dx = ox + w * relative.M41 - ox * a11 - oy * a21;
            double dy = oy + h * relative.M42 - ox * a12 - oy * a22;

            return D3DMATRIX.FromAffine(
                (float)a11, (float)a12, (float)a21, (float)a22, (float)dx, (float)dy);
        }

        /// <summary>D3DMATRIX 行主序相乘（a 后接 b，即 a·b）。</summary>
        public static D3DMATRIX Multiply(D3DMATRIX a, D3DMATRIX b)
        {
            var r = new D3DMATRIX();
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) sum += (double)a[i * 4 + k] * b[k * 4 + j];
                    Set(ref r, i * 4 + j, (float)sum);
                }
            }
            return r;
        }

        private static void Set(ref D3DMATRIX m, int index, float value)
        {
            switch (index)
            {
                case 0: m.M11 = value; break; case 1: m.M12 = value; break;
                case 2: m.M13 = value; break; case 3: m.M14 = value; break;
                case 4: m.M21 = value; break; case 5: m.M22 = value; break;
                case 6: m.M23 = value; break; case 7: m.M24 = value; break;
                case 8: m.M31 = value; break; case 9: m.M32 = value; break;
                case 10: m.M33 = value; break; case 11: m.M34 = value; break;
                case 12: m.M41 = value; break; case 13: m.M42 = value; break;
                case 14: m.M43 = value; break; case 15: m.M44 = value; break;
            }
        }

        // ==================================================================
        //  笔 → SKPaint（描边 / widen / 命中测试共用）
        // ==================================================================

        /// <summary>MIL_PEN_DATA（+ 可选虚线数组）→ Style=Stroke 的 SKPaint。</summary>
        public static unsafe SKPaint CreateStrokePaint(MIL_PEN_DATA* penData, double* dashArray)
        {
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };

            if (penData == null)
            {
                paint.StrokeWidth = 1f;
                return paint;
            }

            MIL_PEN_DATA pen = *penData;

            // 【U1c 真机对拍纠正】上游笔宽取绝对值：geometry_api.cpp 的
            // InitializePen 把 Thickness 原样塞进 CPlainPen，而 CPlainPen 的
            // 描边宽度是 fabs()（负笔宽与正笔宽同形）。真机 widen_negative_thickness
            // 输出与 +2 完全一致。
            double thickness = Math.Abs(pen.Thickness);
            paint.StrokeWidth = (float)thickness;
            paint.StrokeMiter = (float)pen.MiterLimit;
            paint.StrokeCap = pen.StartLineCap switch
            {
                MilPenLineCap.Square => SKStrokeCap.Square,
                MilPenLineCap.Round => SKStrokeCap.Round,
                _ => SKStrokeCap.Butt,
            };
            paint.StrokeJoin = pen.LineJoin switch
            {
                MilPenLineJoin.Bevel => SKStrokeJoin.Bevel,
                MilPenLineJoin.Round => SKStrokeJoin.Round,
                _ => SKStrokeJoin.Miter,
            };

            // DashArraySize 是**字节数**（上游 geometry_api.cpp:141 `cDash = DashArraySize / sizeof(double)`）。
            int dashCount = (int)(pen.DashArraySize / sizeof(double));
            if (dashCount > 0 && dashArray != null)
            {
                // 上游 SetPenDoubleDashArray：奇数条会被复制一遍凑成偶数。
                int n = (dashCount & 1) != 0 ? dashCount * 2 : dashCount;
                var intervals = new float[n];
                bool any = false;
                for (int i = 0; i < dashCount; i++)
                {
                    // WPF 的 DashStyle.Dashes 是"相对笔宽"的比例，乘 Thickness 才是长度
                    // （上游 strokefigure.cpp:3705 `pen.GetDash(i) * rPenWidth`），
                    // 且上游对每一条取 fabs（cpen.cpp:292）。数量级保护用 0 兜底：
                    // 负厚度已经在上面取过绝对值。
                    intervals[i] = (float)(Math.Abs(dashArray[i]) * thickness);
                    if (intervals[i] > 0) any = true;
                }
                if (n != dashCount) Array.Copy(intervals, 0, intervals, dashCount, dashCount);
                if (any)
                    paint.PathEffect = SKPathEffect.CreateDash(intervals, (float)pen.DashOffset);
            }

            return paint;
        }

        /// <summary>
        /// 描边外扩：路径 + 笔 → 填充轮廓路径。
        ///
        /// 【U1c 真机对拍纠正】笔宽为 0 时上游**成功但什么都不产出**
        /// （真机 widen_zero_thickness：HRESULT=S_OK、0 个图形）。Skia 把
        /// StrokeWidth=0 当"hairline"处理，GetFillPath 会失败；这里提前返回空路径，
        /// 让调用方按"0 个图形 + S_OK"上报。
        /// </summary>
        public static unsafe SKPath Widen(SKPath path, MIL_PEN_DATA* penData, double* dashArray)
        {
            if (path == null) return null;
            if (penData != null && penData->Thickness == 0.0) return new SKPath();

            using SKPaint paint = CreateStrokePaint(penData, dashArray);
            var result = new SKPath();
            if (!paint.GetFillPath(path, result))
            {
                result.Dispose();
                return null;
            }
            return result;
        }

        /// <summary>自交/空心消解：按填充规则解算后的区域轮廓（上游 Outline 的语义）。</summary>
        public static SKPath Outline(SKPath path)
        {
            if (path == null) return null;

            using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            var result = new SKPath();
            if (!paint.GetFillPath(path, result))
            {
                result.Dispose();
                return null;
            }
            return result;
        }

        /// <summary>布尔运算；结果为空路径时返回合法的空 SKPath（不是 null）。运算失败才返回 null。</summary>
        public static SKPath Combine(SKPath a, SKPath b, MilGeometryCombineMode mode, MilFillRule fillRule)
        {
            var result = new SKPath { FillType = FillTypeOf(fillRule) };
            if (a == null && b == null) return result;

            if (a == null) { result.AddPath(b); return result; }
            if (b == null) { result.AddPath(a); return result; }

            SKPathOp op = mode switch
            {
                MilGeometryCombineMode.Intersect => SKPathOp.Intersect,
                MilGeometryCombineMode.Xor => SKPathOp.Xor,
                MilGeometryCombineMode.Exclude => SKPathOp.Difference,
                _ => SKPathOp.Union,
            };

            if (!a.Op(b, op, result))
            {
                result.Dispose();
                return null;
            }

            // 【U1c 真机对拍】Skia 的 Op 结果用 **EvenOdd** 表达"捏合"的边界
            //（相交矩形的 XOR 会被写成"8 字形合并轮廓 + 反向的公共方块"，
            //  只有在 EvenOdd 下两者才互相抵消）。而上游 Combine 的
            // *pOutFillRule 恒为 Winding（CShape 默认填充模式，
            // shape.cpp:225 / shape.h:57；真机 22/22 个用例都回 1），
            // 所以在 Nonzero 语义下必须先把结果规范成"互不重叠、朝向一致"的轮廓集，
            // 否则发射出去的几何在 Nonzero 下会多算面积
            // （实测：两个相交矩形的 XOR 面积 175 而不是 150）。
            // Skia 的 Simplify 正好做这件事，产物就是上游那种"每块一个干净轮廓"的形态。
            if (result.FillType == SKPathFillType.EvenOdd)
            {
                var simplified = new SKPath { FillType = SKPathFillType.Winding };
                if (result.Simplify(simplified) && !simplified.IsEmpty)
                {
                    result.Dispose();
                    return simplified;
                }
                simplified.Dispose();
            }
            return result;
        }

        public static SKPathFillType FillTypeOf(MilFillRule rule) =>
            rule == MilFillRule.Nonzero ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
    }
}
