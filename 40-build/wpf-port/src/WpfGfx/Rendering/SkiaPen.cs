// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 画笔（Pen）→ SKPaint（描边样式）。
//
// MilPen 的字段来自 T3 的 Resources/MilResources.cs：
//   Thickness / MiterLimit / Brush / DashStyle / StartLineCap / EndLineCap / DashCap / LineJoin
//
// 已知简化（都不是静默丢弃）：
//   1. DashCap（虚线端点形状）未实现——Skia 的 PathEffect.CreateDash 只画矩形端头，
//      没有 WPF 的 round/triangle dash cap 概念。
//   2. MilPenLineCap.Triangle 未实现——Skia 无三角线帽，退化成 Butt。
//   3. Thickness ≤ 0 视为"不描边"（WPF 里 0 宽度笔画不产生输出）。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class SkiaPen
    {
        /// <summary>
        /// 句柄 → 描边用的 SKPaint。返回 null 表示这条指令不需要描边
        /// （空句柄 / 资源缺失 / 线宽为 0 / 画刷类型未支持）。
        /// </summary>
        public static SKPaint CreateStroke(
            MilResourceProvider provider, MilResourceHandle handle, SKRect bounds, bool antialias,
            MilBitmapScalingMode scaling = MilBitmapScalingMode.Unspecified)
        {
            if (handle.IsNull || provider == null) return null;
            if (provider.Lookup(handle) is not MilPen pen) return null;
            if (pen.Thickness <= 0.0) return null;

            // 线宽不参与包围盒：描边画刷的 RelativeToBoundingBox 以几何包围盒为准（WPF 同）。
            SKPaint paint = SkiaBrush.CreateFill(
                provider, new MilResourceHandle((uint)pen.Brush), bounds, antialias, scaling);
            if (paint == null) return null;

            float thickness = (float)pen.Thickness;

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thickness;
            paint.StrokeCap = Cap(pen.StartLineCap);
            paint.StrokeJoin = Join(pen.LineJoin);
            paint.StrokeMiter = (float)pen.MiterLimit;

            // StartLineCap 与 EndLineCap 不同时 Skia 无法表达（只有一个 StrokeCap），
            // 取起始端为准——WPF 的实际观感差异仅在非对称端点时出现。
            SKPathEffect dash = CreateDash(provider, new MilResourceHandle((uint)pen.DashStyle), thickness);
            if (dash != null) paint.PathEffect = dash;

            return paint;
        }

        /// <param name="thickness">画笔线宽。WPF 的虚线长度是**线宽的倍数**，不是绝对长度。</param>
        private static SKPathEffect CreateDash(
            MilResourceProvider provider, MilResourceHandle handle, float thickness)
        {
            if (handle.IsNull) return null;
            if (provider.Lookup(handle) is not MilDashStyle style) return null;

            List<double> dashes = style.Dashes;
            if (dashes == null || dashes.Count == 0) return null;

            // 【关键语义：虚线长度相对线宽】
            //   上游 WpfGfx/core/geometry/strokefigure.cpp:3685/3705
            //       rDashOffset = pen.GetDashOffset() * rPenWidth;
            //       m_rgDashes[i+1] = m_rgDashes[i] + pen.GetDash(i) * rPenWidth;
            //   也就是 DashStyle.Dashes 里的每个数与 Offset 都要乘线宽，与
            //   MSDN "The values are relative to the thickness of the Pen" 一致。
            //   Skia 的 CreateDash 间隔是**路径坐标下的绝对长度**，不做这步乘法会让
            //   线宽一改虚线节奏就错——这是前任代码的一处真实缺陷（已修）。
            var intervals = new float[dashes.Count];
            bool anyPositive = false;
            for (int i = 0; i < dashes.Count; i++)
            {
                intervals[i] = (float)(dashes[i] * thickness);
                if (intervals[i] > 0f) anyPositive = true;
            }
            if (!anyPositive) return null;

            // 奇数个间隔：上游 strokefigure.cpp:3682 直接 `IFC(E_INVALIDARG)` 拒绝
            // （count < 2 或奇数都报错，即 WPF 里"奇数虚线"是非法输入，画不出来）。
            // 这里不抛异常——渲染层不该因一条非法指令拖垮整帧——而是退化成把列表
            // 首尾相接重复一遍，凑成偶数。与 WPF 的"拒绝渲染"不同，属已知的宽容偏差。
            if (intervals.Length % 2 == 1)
            {
                var doubled = new float[intervals.Length * 2];
                Array.Copy(intervals, 0, doubled, 0, intervals.Length);
                Array.Copy(intervals, 0, doubled, intervals.Length, intervals.Length);
                intervals = doubled;
            }

            return SKPathEffect.CreateDash(intervals, (float)(style.Offset * thickness));
        }

        private static SKStrokeCap Cap(MilPenLineCap c) => c switch
        {
            MilPenLineCap.Square => SKStrokeCap.Square,
            MilPenLineCap.Round => SKStrokeCap.Round,
            // Flat=0 与 Triangle=3：Skia 没有三角线帽，两者都退到 Butt。
            _ => SKStrokeCap.Butt,
        };

        private static SKStrokeJoin Join(MilPenLineJoin j) => j switch
        {
            MilPenLineJoin.Bevel => SKStrokeJoin.Bevel,
            MilPenLineJoin.Round => SKStrokeJoin.Round,
            _ => SKStrokeJoin.Miter,
        };
    }
}
