// Licensed to the .NET Foundation under one or more agreements.
//
// VisualBrush 的 **natural size**（= WPF 的"视觉内容包围盒"）。
//
// 为什么需要它：`SkiaBrush.CreateVisualFill` 要拿"这个 Visual 有多大"当源尺寸，
// 才能走 Viewbox → Viewport → Stretch 的 TileBrush 链路。WPF 里 VisualBrush 的
// 默认 Viewbox 是 `RelativeToBoundingBox (0,0,1,1)`，它的"bounding box"就是
// **该 Visual 子树的绘制内容包围盒**（descendant bounds），不是窗口、不是画布。
//
// 本文件只算矩形，**不做任何绘图**——与 Resources/VisualProjection 同一分工。
//
// 【字形包围盒：已解决（T2b）】
//   `MilDrawGlyphRun` 用 `MilGlyphRun.ManagedBounds` —— 它就是上游托管侧 `GlyphRun.Bounds`，
//   由 `MilCommandDispatcher.cs:906` 从命令体填入，**不需要在这里算文字度量**，
//   因此不必碰 Text/**（那是 M7b 的车道）。见 GeometryBounds 里的 MilDrawGlyphRun 分支。
//
// ⚠️【仍未覆盖，登记不藏】
//   1. `MilDrawVideo` 未计入（视频层尚未实现）。
//   2. Clip 不参与包围盒（WPF 的 descendant bounds 不含 clip 的收窄）。
//      这一条**未与真机对照过**，属推断，登记为待验证。

using System;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class VisualBrushSource
    {
        private const int MaxDepth = 64;

        /// <summary>
        /// 视觉子树的内容包围盒（**该 Visual 自身的局部坐标系**，含子节点的
        /// Transform/Offset 累积）。没有任何可计量的内容时返回 <see cref="SKRect.Empty"/>。
        /// </summary>
        public static SKRect ContentBounds(MilResourceProvider provider, MilVisual visual)
        {
            SKRect acc = SKRect.Empty;
            Accumulate(provider, visual, SKMatrix.Identity, 0, ref acc);
            return acc;
        }

        private static void Accumulate(
            MilResourceProvider provider, MilVisual v, SKMatrix parent, int depth, ref SKRect acc)
        {
            if (v == null || depth > MaxDepth) return;

            // 与 SkiaRenderBackend.RenderVisual 的 world 公式同形：
            //   world = parent · Transform · Translate(Offset)
            SKMatrix world = SKMatrix.Concat(
                SKMatrix.Concat(parent, v.Transform),
                SKMatrix.CreateTranslation(v.Offset.X, v.Offset.Y));

            if (v.Content != null)
                AccumulateContent(provider, v.Content, world, ref acc);

            foreach (MilVisual child in v.Children)
                Accumulate(provider, child, world, depth + 1, ref acc);
        }

        /// <summary>
        /// 指令流里有 PushTransform / Pop，所以必须跟着维护一个"内容局部 → 视觉局部"的
        /// 矩阵栈；直接拿 world 去量每条指令会在带变换的视觉上算错。
        /// </summary>
        private static void AccumulateContent(
            MilResourceProvider provider, IMilRenderData content, SKMatrix world, ref SKRect acc)
        {
            SKMatrix current = SKMatrix.Identity;
            var stack = new System.Collections.Generic.Stack<SKMatrix>();

            foreach (MilDrawInstruction instr in content.Instructions)
            {
                switch (instr.Command)
                {
                    case MilDrawCommand.MilPushTransform:
                        stack.Push(current);
                        // 【矩阵组合空间·同族扫荡】与 `Execute` 的 `MilPushTransform` **必须同序**：
                        //   内容级变换在局部（DIP）空间 ⇒ `m` 先作用、`current` 后作用。
                        //   `Concat(a,b)` 实测为 **b 先作用** ⇒ `Concat(current, m)`（此前写反成 `Concat(m, current)`，
                        //   与 `Execute` 曾经的错误同族；两处必须同时正确，否则 VisualBrush 的包围盒与直接渲染**不一致**）。
                        current = SKMatrix.Concat(current, provider.ResolveTransform(instr.Geometry));
                        continue;

                    case MilDrawCommand.MilPop:
                        if (stack.Count > 0) current = stack.Pop();
                        continue;

                    case MilDrawCommand.MilDrawVideo:
                    case MilDrawCommand.MilDrawVideoAnimate:
                        continue;      // 见文件头「已知不足」2
                }

                if (!GeometryBounds(provider, instr, out SKRect local)) continue;

                // 内容局部 → 视觉局部：先走内容级 push 链（`current`），再走视觉的 `world`。
                //   `Concat(a,b)` = b 先作用 ⇒ 要"`current` 先、`world` 后"必须写 `Concat(world, current)`。
                SKMatrix m = SKMatrix.Concat(world, current);
                acc = Union(acc, Transform(m, local));
            }
        }

        /// <summary>单条指令的几何包围盒（**内容局部**坐标）。取法与 Execute 逐条对齐。</summary>
        private static bool GeometryBounds(
            MilResourceProvider provider, MilDrawInstruction instr, out SKRect rect)
        {
            switch (instr.Command)
            {
                case MilDrawCommand.MilDrawLine:
                case MilDrawCommand.MilDrawLineAnimate:
                    rect = RectOf(instr.Point0, instr.Point1);
                    return true;

                case MilDrawCommand.MilDrawRectangle:
                case MilDrawCommand.MilDrawRectangleAnimate:
                case MilDrawCommand.MilDrawRoundedRectangle:
                case MilDrawCommand.MilDrawRoundedRectangleAnimate:
                    rect = instr.Rect;
                    return true;

                case MilDrawCommand.MilDrawEllipse:
                case MilDrawCommand.MilDrawEllipseAnimate:
                {
                    // 契约没有 Center/Radius 字段：中心在 Point0、半径在 CornerRadius
                    SKPoint c = instr.Point0, r = instr.CornerRadius;
                    rect = new SKRect(c.X - r.X, c.Y - r.Y, c.X + r.X, c.Y + r.Y);
                    return true;
                }

                case MilDrawCommand.MilDrawGeometry:
                {
                    using SKPath path = SkiaGeometry.ToPath(provider, instr.Geometry);
                    if (path == null) { rect = SKRect.Empty; return false; }
                    rect = path.Bounds;
                    return true;
                }

                case MilDrawCommand.MilDrawImage:
                case MilDrawCommand.MilDrawImageAnimate:
                    rect = instr.Rect;
                    return true;

                case MilDrawCommand.MilDrawGlyphRun:
                {
                    // 字形句柄在 instr.Geometry（与 SkiaRenderBackend 的 MilDrawGlyphRun 一致）。
                    // **不去算文字度量**——`MilGlyphRun.ManagedBounds` 就是上游托管侧
                    // `GlyphRun.Bounds`，由 MilCommandDispatcher.cs:906 从命令体填进来，
                    // 是现成的真值。缺它才需要退回"算度量"（那要动 Text/**，不属本车道）。
                    if (provider.Lookup(instr.Geometry) is not MilGlyphRun run) { rect = SKRect.Empty; return false; }
                    MilRect b = run.ManagedBounds;
                    rect = new SKRect((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom);
                    return !rect.IsEmpty;
                }

                case MilDrawCommand.MilDrawDrawing:
                {
                    // Drawing 的包围盒与 DrawingBrush 走同一条递归（描边按半笔宽外扩）
                    rect = SkiaBrush.DrawingBoundsOf(provider, instr.Geometry);
                    return rect.Width > 0 || rect.Height > 0;
                }

                default:
                    rect = SKRect.Empty;
                    return false;
            }
        }

        private static SKRect RectOf(SKPoint a, SKPoint b) =>
            new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        private static SKRect Union(SKRect a, SKRect b)
        {
            if (b.Width <= 0 && b.Height <= 0) return a;
            if (a.Width <= 0 && a.Height <= 0) return b;
            return new SKRect(
                Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
        }

        /// <summary>把矩形按矩阵变换后取轴对齐包围盒（量四个角，透视为仿射，够用）。</summary>
        private static SKRect Transform(SKMatrix m, SKRect r)
        {
            SKPoint p0 = m.MapPoint(r.Left, r.Top);
            SKPoint p1 = m.MapPoint(r.Right, r.Top);
            SKPoint p2 = m.MapPoint(r.Right, r.Bottom);
            SKPoint p3 = m.MapPoint(r.Left, r.Bottom);
            return new SKRect(
                Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
                Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
                Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
                Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
        }
    }
}
