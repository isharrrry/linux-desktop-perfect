// GlyphRun 排版：把 (字形 id, 步进, 偏移) 算成逐字形绘制坐标。
//
// 【位置公式】
//   pos[i] = baselineOrigin + Σ(advance[0..i-1]) + offset[i]
//
//   注意偏移是**叠加在累积步进之上**的绝对偏移，不是"相对上一个字形"。
//   这与 WPF 的 GlyphRun.GlyphOffsets 语义一致：每个 offset 描述该字形相对
//   run 起点的附加位移，用于字距微调（kerning pairs 已经算进 advance 了，
//   offset 剩下的用途是手工排版与字距调整）。
//
// 【步进的优先级】
//   1. 调用方显式给的 AdvanceWidths（WPF shaping 的结果，最权威）
//   2. 字体自身的度量（SKFont.GetGlyphWidths，AdvanceWidths 缺失时兜底）
//   3. 0（字体连度量都给不出来时，至少不要崩）
//
// 【Sideways】
//   MilGlyphRun.Sideways（flags 0x1）表示竖排。M1 只把步进方向从 +X 换成 +Y，
//   不做字形旋转（TrueType 的竖排字形替换 / vert 特性需要 HarfBuzz，M2 再说）。
//   这点在 docs/unimplemented.md 里同步登记。

using System;
using SkiaSharp;

namespace WpfGfx.Linux.Text
{
    internal readonly struct GlyphRunMetrics
    {
        /// <summary>每个字形实际使用的步进宽度（已按优先级解析）。</summary>
        public float[] Advances { get; init; }

        /// <summary>每个字形原点的绘制坐标（第 0 个 = 基线起点 + 偏移[0]）。</summary>
        public SKPoint[] Positions { get; init; }

        /// <summary>全部步进之和，即整段文字占的长度。</summary>
        public float TotalAdvance { get; init; }

        /// <summary>
        /// 超出字体字形数而被兜底成 .notdef(0) 的字形个数。
        /// 非 0 说明这一批字形 id 不是给这份字体用的（多半是 PIDWriteFont 没解出来）。
        /// </summary>
        public int OutOfRangeGlyphs { get; init; }
    }

    internal static class GlyphRunLayout
    {
        public static GlyphRunMetrics Measure(GlyphRunRequest request, SKFont font, SKPaint paint)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (font == null) throw new ArgumentNullException(nameof(font));

            ushort[] glyphs = request.GlyphIndices ?? Array.Empty<ushort>();
            int count = glyphs.Length;

            if (count == 0)
            {
                return new GlyphRunMetrics
                {
                    Advances = Array.Empty<float>(),
                    Positions = Array.Empty<SKPoint>(),
                    TotalAdvance = 0f,
                };
            }

            int glyphCount = font.Typeface == null ? 0 : font.Typeface.GlyphCount;
            var usable = new ushort[count];
            int outOfRange = 0;
            for (int i = 0; i < count; i++)
            {
                // 越界字形 id 会导致 Skia 读到非法字形数据，这里统一兜底成 .notdef。
                // 输入来自 MilGlyphRun 的字节流，属于"系统边界"，必须校验。
                if (glyphCount > 0 && glyphs[i] >= glyphCount)
                {
                    usable[i] = 0;
                    outOfRange++;
                }
                else
                {
                    usable[i] = glyphs[i];
                }
            }

            float[] advances = ResolveAdvances(request, font, paint, usable);
            SKPoint[] positions = new SKPoint[count];

            SKPoint cursor = request.BaselineOrigin;
            float total = 0f;

            for (int i = 0; i < count; i++)
            {
                SKPoint offset = request.GlyphOffsets != null && i < request.GlyphOffsets.Length
                    ? request.GlyphOffsets[i]
                    : SKPoint.Empty;

                positions[i] = new SKPoint(cursor.X + offset.X, cursor.Y + offset.Y);

                // 步进方向：竖排时沿 +Y 累积。
                if (request.Sideways) cursor = new SKPoint(cursor.X, cursor.Y + advances[i]);
                else cursor = new SKPoint(cursor.X + advances[i], cursor.Y);

                total += advances[i];
            }

            return new GlyphRunMetrics
            {
                Advances = advances,
                Positions = positions,
                TotalAdvance = total,
                OutOfRangeGlyphs = outOfRange,
            };
        }

        private static float[] ResolveAdvances(
            GlyphRunRequest request, SKFont font, SKPaint paint, ushort[] glyphs)
        {
            int count = glyphs.Length;
            float[] advances = new float[count];

            float[] fontAdvances = null;
            if (request.AdvanceWidths == null || request.AdvanceWidths.Length < count)
            {
                fontAdvances = QueryFontAdvances(font, paint, glyphs);
            }

            for (int i = 0; i < count; i++)
            {
                if (request.AdvanceWidths != null && i < request.AdvanceWidths.Length)
                {
                    advances[i] = request.AdvanceWidths[i];
                }
                else if (fontAdvances != null && i < fontAdvances.Length)
                {
                    advances[i] = fontAdvances[i];
                }
                else
                {
                    advances[i] = 0f;
                }
            }

            return advances;
        }

        /// <summary>向字体问默认步进。SKFont 已经带字号，返回的宽度单位是像素。</summary>
        private static float[] QueryFontAdvances(SKFont font, SKPaint paint, ushort[] glyphs)
        {
            var widths = new float[glyphs.Length];
            var bounds = new SKRect[glyphs.Length];

            try
            {
                font.GetGlyphWidths(glyphs, widths, bounds, paint);
            }
            catch (Exception)
            {
                // 度量拿不到就全 0，让字形叠在原点上；总比让整帧渲染崩掉好。
                Array.Clear(widths, 0, widths.Length);
            }

            return widths;
        }
    }
}
