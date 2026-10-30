// 把排好版的 GlyphRun 画到 SKCanvas 上。
//
// 【为什么走 SKTextBlob 而不是 canvas.DrawText(string)】
//   DrawText(string) 会让 Skia 重新做一次 shaping，而 WPF 传下来的已经是 shaping
//   结果（字形 id + 逐字步进）。重排一次既丢掉了 WPF 的排版意图（显式 advance、
//   字距偏移），又引入了第二套 shaping 实现，两边的差异会以"字距微妙不同"的形式
//   渗进 golden 图。SKTextBlobBuilder.AddPositionedRun 接受**字形 id + 绝对坐标**，
//   正好是 MilGlyphRun 的语义，一步到位。
//
// 【颜色/抗锯齿为什么来自调用方的 paint】
//   挂载点是 MilResourceProvider.GlyphRunRenderer，T4 已经把前景画刷解析成
//   SKPaint 传进来了（SkiaRenderBackend.cs:270）。文本不该自己决定颜色。

using System;
using SkiaSharp;

namespace WpfGfx.Linux.Text
{
    internal static class GlyphRunPainter
    {
        /// <summary>绘制一段已经排好版的文本。空序列返回 true（画了 0 个字形，不算失败）。</summary>
        public static bool Draw(SKCanvas canvas, GlyphRunRequest request, SKFont font, SKPaint paint)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (font == null) throw new ArgumentNullException(nameof(font));
            if (paint == null) throw new ArgumentNullException(nameof(paint));

            GlyphFaceCensus.NoteEntry("GlyphRunPainter.Draw");
            ushort[] glyphs = request.GlyphIndices ?? Array.Empty<ushort>();
            if (glyphs.Length == 0) return true;

            GlyphRunMetrics metrics = GlyphRunLayout.Measure(request, font, paint);

            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(glyphs, font, metrics.Positions);

            using SKTextBlob blob = builder.Build();
            if (blob == null) return false;

            // 坐标已经写在 blob 里，这里的 (0,0) 是"不再额外平移"。
            canvas.DrawText(blob, 0f, 0f, paint);
            return true;
        }
    }
}
