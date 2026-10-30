// T2 · Phase 1 —— FontMetrics 的两条通路与交叉验证
// =====================================================================================
// 【通路 A（权威）】OpenTypeFontData → FontMetricsData
//   hhea.ascender/descender/lineGap、OS/2.sxHeight/sCapHeight/yStrikeout*、
//   post.underlinePosition/Thickness、head.unitsPerEm。
//   选它当权威的理由：**这正是 DWrite 自己读的东西**（DWrite 的 FontFace::GetMetrics
//   对 TrueType 就是走 hhea+OS/2），而且它不随 Skia 版本漂移。
//
// 【通路 B（交叉验证）】SKTypeface/SKFont → FontMetricsData
//   把 SKFont 的 size 设成 **等于 UnitsPerEm**，此时 Skia 返回的度量正好是设计单位
//   （Skia 的度量 = 设计单位 × size/upem，size==upem 时系数为 1）。
//   实测（NotoSans-Regular，upem=1000）：Ascent=-1069 / Descent=293 / Leading=0，
//   与 hhea 的 1069 / -293 / 0 逐位一致。
//
// 【符号约定：这里有一个必须写死的坑】
//   Skia 的 fUnderlinePosition / fStrikeoutPosition 与 OpenType 表**符号相反**：
//     post.underlinePosition        = -100  →  Skia UnderlinePosition   = +100
//     OS/2.yStrikeoutPosition       = +328  →  Skia StrikeoutPosition   = -328
//   DWrite 的约定是「下划线负数=基线之下、删除线正数=基线之上」，
//   与 OpenType 表一致，因此取 Skia 值时必须**取负**。
//   这两个符号如果搞反，画出来的下划线会跑到基线上面去，而数值大小全对 ——
//   是最难靠肉眼发现的一类错，所以这里显式取负并在测试里逐面断言。
//
// 【CapHeight/XHeight 的三级回落】
//   OS/2 v2+ 的 sxHeight/sCapHeight → Skia 的 XHeight/CapHeight → 量 'H'/'x' 的墨迹盒。
//   三级都要能说出"当前用的是哪一级"，Provenance 字段就是干这个的。

using System;
using System.Globalization;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>
    /// Skia 直算的度量快照（设计单位，符号已归一到 DWrite 约定）。
    /// 这是 **独立于本工程实现** 的第二条通路：测试直接调 <see cref="AtUpem"/>，
    /// 不经过 <see cref="LinuxFontFace.Metrics"/>，因此"我们的值 == Skia 直算值"
    /// 这句话才是可证伪的。
    /// </summary>
    public readonly struct SkiaMetricsSnapshot
    {
        public SkiaMetricsSnapshot(
            int unitsPerEm, double ascent, double descent, double leading,
            double capHeight, double xHeight, bool capFromSkia, bool xHeightFromSkia,
            double underlinePosition, double underlineThickness,
            double strikeoutPosition, double strikeoutThickness,
            bool hasUnderline, bool hasStrikeout)
        {
            UnitsPerEm = unitsPerEm;
            Ascent = ascent; Descent = descent; Leading = leading;
            CapHeight = capHeight; XHeight = xHeight;
            CapHeightFromSkia = capFromSkia; XHeightFromSkia = xHeightFromSkia;
            UnderlinePosition = underlinePosition; UnderlineThickness = underlineThickness;
            StrikeoutPosition = strikeoutPosition; StrikeoutThickness = strikeoutThickness;
            HasUnderline = hasUnderline; HasStrikeout = hasStrikeout;
        }

        public int UnitsPerEm { get; }
        public double Ascent { get; }
        public double Descent { get; }
        public double Leading { get; }
        public double CapHeight { get; }
        public double XHeight { get; }
        public bool CapHeightFromSkia { get; }
        public bool XHeightFromSkia { get; }
        public double UnderlinePosition { get; }
        public double UnderlineThickness { get; }
        public double StrikeoutPosition { get; }
        public double StrikeoutThickness { get; }
        public bool HasUnderline { get; }
        public bool HasStrikeout { get; }
    }

    /// <summary>度量计算（通路 A 权威 + 通路 B 交叉验证）。</summary>
    public static class MetricsFactory
    {
        /// <summary>
        /// 通路 B：让 Skia 在 size == UnitsPerEm 上报度量，得到**设计单位**的值。
        /// 用 LinearMetrics=true + Hinting=None 关掉一切栅格化影响，保证可复现。
        /// </summary>
        public static SkiaMetricsSnapshot AtUpem(SKTypeface typeface)
        {
            if (typeface == null) throw new ArgumentNullException(nameof(typeface));

            int upem = typeface.UnitsPerEm;
            if (upem <= 0) throw new InvalidOperationException("字体的 UnitsPerEm 非法：" + upem);

            using var font = new SKFont(typeface, upem)
            {
                LinearMetrics = true,
                Subpixel = true,
                Hinting = SKFontHinting.None,
                Embolden = false,
                BaselineSnap = false,
            };

            SKFontMetrics m = font.Metrics;

            // 符号：Skia 的 ascent 是**负数**（向上为负）、descent 是**正数**；
            // DWrite/OpenType 两个都是正数（ascent 向上、descent 向下）。
            // 实测 NotoSans-Regular@upem：Ascent=-1069 / Descent=293，与 hhea 的 1069 / -293 一致。
            double ascent = -m.Ascent;
            double descent = m.Descent;
            double leading = m.Leading;

            double underlinePosition = m.UnderlinePosition.HasValue ? -m.UnderlinePosition.Value : 0.0;
            double underlineThickness = m.UnderlineThickness ?? 0.0;
            double strikeoutPosition = m.StrikeoutPosition.HasValue ? -m.StrikeoutPosition.Value : 0.0;
            double strikeoutThickness = m.StrikeoutThickness ?? 0.0;

            return new SkiaMetricsSnapshot(
                upem, ascent, descent, leading,
                m.CapHeight, m.XHeight,
                capFromSkia: m.CapHeight > 0, xHeightFromSkia: m.XHeight > 0,
                underlinePosition, underlineThickness,
                strikeoutPosition, strikeoutThickness,
                hasUnderline: m.UnderlinePosition.HasValue,
                hasStrikeout: m.StrikeoutPosition.HasValue);
        }

        /// <summary>
        /// 通路 A：从 OpenType 表算度量（权威）。<paramref name="skia"/> 只用于
        /// CapHeight/XHeight 的二级回落与来源标注，其余字段完全不看它。
        /// </summary>
        public static FontMetricsData FromOpenType(OpenTypeFontData ot, SkiaMetricsSnapshot? skia = null, SKTypeface fallbackForMeasurement = null)
        {
            if (ot == null) throw new ArgumentNullException(nameof(ot));
            if (ot.UnitsPerEm == 0) throw new InvalidOperationException("head.unitsPerEm 为 0，字体不可用");

            var metrics = new FontMetricsData { DesignUnitsPerEm = ot.UnitsPerEm };

            // ---- 行盒：hhea（与 Skia 的 Ascent/Descent/Leading 同源） ----
            int ascent = ot.HheaAscender;
            int descent = -ot.HheaDescender;                 // hhea.descender 是负数，DWrite 的 descent 是正数
            int lineGap = ot.HheaLineGap;

            if (ascent == 0 && descent == 0 && ot.WinAscent > 0)
            {
                // 极端情况：hhea 全 0（有些子集化工具会写坏）。退回 OS/2 的 win 值，
                // 并在 Provenance 里标出来 —— 静默换成另一套口径是不可接受的。
                ascent = ot.WinAscent;
                descent = ot.WinDescent;
                lineGap = 0;
                metrics.Provenance += "ascent/descent 回落到 OS/2.usWin*（hhea 全 0）; ";
            }

            metrics.Ascent = ClampToUShort(ascent);
            metrics.Descent = ClampToUShort(descent);
            metrics.LineGap = (short)Math.Clamp(lineGap, short.MinValue, short.MaxValue);

            // ---- CapHeight / XHeight：OS/2 v2+ → Skia → 实测墨迹 ----
            string capSource = "OS/2.sCapHeight", xSource = "OS/2.sxHeight";
            int capHeight = ot.CapHeight;
            int xHeight = ot.XHeight;

            if (capHeight <= 0 && skia.HasValue && skia.Value.CapHeightFromSkia)
            {
                capHeight = (int)Math.Round(skia.Value.CapHeight);
                capSource = "Skia.fCapHeight";
            }

            if (xHeight <= 0 && skia.HasValue && skia.Value.XHeightFromSkia)
            {
                xHeight = (int)Math.Round(skia.Value.XHeight);
                xSource = "Skia.fXHeight";
            }

            if ((capHeight <= 0 || xHeight <= 0) && fallbackForMeasurement != null)
            {
                if (capHeight <= 0)
                {
                    capHeight = MeasureInkHeight(fallbackForMeasurement, 'H', ot.UnitsPerEm);
                    capSource = "实测 'H' 墨迹高度";
                }

                if (xHeight <= 0)
                {
                    xHeight = MeasureInkHeight(fallbackForMeasurement, 'x', ot.UnitsPerEm);
                    xSource = "实测 'x' 墨迹高度";
                }
            }

            metrics.CapHeight = ClampToUShort(capHeight);
            metrics.XHeight = ClampToUShort(xHeight);

            // ---- 下划线 / 删除线：post 与 OS/2，符号已经是 DWrite 约定 ----
            metrics.UnderlinePosition = ot.UnderlinePosition;
            metrics.UnderlineThickness = ClampToUShort(ot.UnderlineThickness);
            metrics.StrikethroughPosition = ot.StrikeoutPosition;
            metrics.StrikethroughThickness = ClampToUShort(ot.StrikeoutSize);

            // ---- 来源取证 ----
            var provenance = new System.Text.StringBuilder();
            provenance.Append("upem=head; asc/desc/gap=hhea(")
                      .Append(ot.HheaAscender).Append(',').Append(ot.HheaDescender).Append(',').Append(ot.HheaLineGap).Append("); ");
            provenance.Append("cap=").Append(capSource).Append("; xh=").Append(xSource).Append("; ");
            provenance.Append("ul=post; st=OS/2; ");
            provenance.Append("USE_TYPO_METRICS=").Append((ot.FsSelection & 0x0080) != 0 ? "1" : "0")
                      .Append("; typo=(").Append(ot.TypoAscender).Append(',').Append(ot.TypoDescender).Append(',').Append(ot.TypoLineGap).Append("); ");
            provenance.Append("win=(").Append(ot.WinAscent).Append(',').Append(ot.WinDescent).Append(')');
            metrics.Provenance = provenance.ToString();

            return metrics;
        }

        private static int MeasureInkHeight(SKTypeface typeface, char probe, int upem)
        {
            using var font = new SKFont(typeface, upem) { LinearMetrics = true, Subpixel = true, Hinting = SKFontHinting.None };
            ushort glyph = font.GetGlyph(probe);
            if (glyph == 0) return 0;

            var widths = new float[1];
            var bounds = new SKRect[1];
            font.GetGlyphWidths(new ReadOnlySpan<ushort>(new[] { glyph }), widths, bounds, null);
            return (int)Math.Round(-bounds[0].Top);
        }

        /// <summary>
        /// 把设计单位度量吸附到物理像素网格（DWrite 的 GetGdiCompatibleMetrics 语义）。
        /// 每个字段都按 <c>round(round(v * emSize * ppdip / upem) * upem / (emSize * ppdip))</c>
        /// 处理，于是"像素值"恒为整数 —— 这正是 GDI 兼容模式的全部意义。
        /// emSize 或 pixelsPerDip 非法（&lt;= 0）时原样返回设计度量，不做静默替换。
        /// </summary>
        public static FontMetricsData ToDisplay(FontMetricsData design, double emSize, double pixelsPerDip)
        {
            if (design == null) throw new ArgumentNullException(nameof(design));

            double scale = emSize * pixelsPerDip / design.DesignUnitsPerEm;
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return design.Clone();

            int Snap(int value) => (int)Math.Round(Math.Round(value * scale) / scale, MidpointRounding.AwayFromZero);

            var result = new FontMetricsData
            {
                DesignUnitsPerEm = design.DesignUnitsPerEm,
                Ascent = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.Ascent))),
                Descent = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.Descent))),
                LineGap = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Snap(design.LineGap))),
                CapHeight = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.CapHeight))),
                XHeight = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.XHeight))),
                UnderlinePosition = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Snap(design.UnderlinePosition))),
                UnderlineThickness = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.UnderlineThickness))),
                StrikethroughPosition = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Snap(design.StrikethroughPosition))),
                StrikethroughThickness = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Snap(design.StrikethroughThickness))),
                Provenance = design.Provenance + $"; display(em={emSize},ppdip={pixelsPerDip})",
            };

            return result;
        }

        /// <summary>
        /// 逐字段比较两条通路，返回人类可读的差异列表（空 = 完全一致）。
        /// 测试用它把"同源一致性"证成一个可数的事实，而不是一句形容词。
        /// </summary>
        public static System.Collections.Generic.List<string> Compare(FontMetricsData a, SkiaMetricsSnapshot b, int tolerance = 0)
        {
            var diffs = new System.Collections.Generic.List<string>();

            void Check(string field, double left, double right)
            {
                if (Math.Abs(left - right) > tolerance)
                    diffs.Add($"{field}: 实现={left} Skia={right} 差={left - right}");
            }

            Check(nameof(FontMetricsData.Ascent), a.Ascent, Math.Round(b.Ascent));
            Check(nameof(FontMetricsData.Descent), a.Descent, Math.Round(b.Descent));
            Check(nameof(FontMetricsData.LineGap), a.LineGap, Math.Round(b.Leading));

            // CapHeight/XHeight 只有在 Skia 确实给出非 0 值时才比较（否则是"我们没有低于 Skia"，
            // 不是一个不一致）。
            if (b.CapHeightFromSkia && a.CapHeight > 0) Check(nameof(FontMetricsData.CapHeight), a.CapHeight, Math.Round(b.CapHeight));
            if (b.XHeightFromSkia && a.XHeight > 0) Check(nameof(FontMetricsData.XHeight), a.XHeight, Math.Round(b.XHeight));

            if (b.HasUnderline)
            {
                Check(nameof(FontMetricsData.UnderlinePosition), a.UnderlinePosition, Math.Round(b.UnderlinePosition));
                Check(nameof(FontMetricsData.UnderlineThickness), a.UnderlineThickness, Math.Round(b.UnderlineThickness));
            }

            if (b.HasStrikeout)
            {
                Check(nameof(FontMetricsData.StrikethroughPosition), a.StrikethroughPosition, Math.Round(b.StrikeoutPosition));
                Check(nameof(FontMetricsData.StrikethroughThickness), a.StrikethroughThickness, Math.Round(b.StrikeoutThickness));
            }

            return diffs;
        }

        private static ushort ClampToUShort(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);
    }
}
