// 一次文本绘制请求：字形 id + 度量覆盖 + 基线起点 + 字体身份。
//
// 【为什么是"字形 id + 位置"而不是"字符串 + 起点"】
//   WPF 传到 MIL 的 GlyphRun **已经排版好了**：托管层用 DirectWrite 把文本 shaping
//   成 (glyphIndices, advanceWidths, glyphOffsets) 三元组，MIL 只负责按给定的量摆
//   位置、光栅化。所以这一层的输入就是字形 id 序列，不是字符串。
//   `FromText` 只是给测试与自绘场景用的**便捷入口**（内部用 Skia 做 shaping）。
//
// 【AdvanceWidths / GlyphOffsets 为什么允许为 null】
//   M1 的 MilCmdGlyphRunCreate 只解出了头部定长字段，变长的步进/偏移数组还是空的
//   （见 docs/unimplemented.md，属 Commands/ 范围，本轮不越界改）。所以渲染器必须
//   能在"调用方没给度量"时退回字体自身的 advance，否则一个字都画不出来。
//   null = 用字体度量；给了就用调用方的（WPF 的显式 advance 优先级更高）。

using System;
using SkiaSharp;

namespace WpfGfx.Linux.Text
{
    internal sealed class GlyphRunRequest
    {
        /// <summary>字体身份。</summary>
        public TextFontDescription Font { get; set; }

        /// <summary>字号（em size，像素）。WPF 侧对应 MilGlyphRun.MuSize。</summary>
        public float FontSize { get; set; } = 12f;

        /// <summary>基线起点（第一个字形原点的位置）。</summary>
        public SKPoint BaselineOrigin { get; set; }

        /// <summary>MilGlyphRun.Sideways（0x1）：竖排，步进方向改为 +Y。</summary>
        public bool Sideways { get; set; }

        /// <summary>字形 id 序列。</summary>
        public ushort[] GlyphIndices { get; set; } = Array.Empty<ushort>();

        /// <summary>显式步进宽度。null 或长度不足时用字体度量。</summary>
        public float[] AdvanceWidths { get; set; }

        /// <summary>逐字形附加偏移。null 或长度不足时按 0 处理。</summary>
        public SKPoint[] GlyphOffsets { get; set; }

        /// <summary>
        /// 便捷入口：把一段文本 shaping 成字形 id 序列。
        /// shaping 用的是传入的 typeface，因此调用方必须保证它和
        /// <paramref name="font"/> 描述的是同一份字体，否则绘制时会用另一份字体的
        /// 字形 id 去查表，画出一堆 .notdef。
        /// </summary>
        public static GlyphRunRequest FromText(
            string text, SKTypeface typeface, TextFontDescription font, float size, SKPoint baselineOrigin)
        {
            if (typeface == null) throw new ArgumentNullException(nameof(typeface));
            if (string.IsNullOrEmpty(text))
            {
                return new GlyphRunRequest
                {
                    Font = font,
                    FontSize = size,
                    BaselineOrigin = baselineOrigin,
                    GlyphIndices = Array.Empty<ushort>(),
                };
            }

            return new GlyphRunRequest
            {
                Font = font,
                FontSize = size,
                BaselineOrigin = baselineOrigin,
                GlyphIndices = typeface.GetGlyphs(text),
            };
        }
    }
}
