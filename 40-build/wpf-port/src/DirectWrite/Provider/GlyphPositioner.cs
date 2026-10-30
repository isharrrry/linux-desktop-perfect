// T2 · Phase 1 —— 字形 advance / 定位（公式直接取自上游 TextAnalyzer.cpp）
// =====================================================================================
// 【公式不是我们编的：逐字抄自上游】
//   上游 CPP/DWriteWrapper/TextAnalyzer.cpp::GetGlyphPlacements 的收尾段（原文）：
//
//     if (textFormattingMode == TextFormattingMode::Ideal)
//     {
//         glyphAdvances[i] = (int)Math::Round(dwriteGlyphAdvances[i] * fontEmSize * scalingFactor / fontEmSizeFloat);
//         glyphOffsets[i].du = (int)(dwriteGlyphOffsets[i].advanceOffset * scalingFactor);
//         glyphOffsets[i].dv = (int)(dwriteGlyphOffsets[i].ascenderOffset * scalingFactor);
//     }
//     else   // Display
//     {
//         glyphAdvances[i] = (int)Math::Round(dwriteGlyphAdvances[i] * scalingFactor);
//     }
//
//   其中 Ideal 分支的 dwriteGlyphAdvances 来自 IDWriteTextAnalyzer::GetGlyphPlacements
//   （**FLOAT**、单位 = fontEmSize 的 DIP），Display 分支来自
//   GetGdiCompatibleGlyphPlacements(..., useGdiNatural = FALSE)（吸附到像素网格）。
//   又因为 `fontEmSizeFloat = (FLOAT)fontEmSize`，Ideal 的
//   `* fontEmSize * scalingFactor / fontEmSizeFloat` 在数学上就是 `* scalingFactor`；
//   但**我们照样把这两个因子原样写进去**，因为
//     (double)(float)13.333 != 13.333
//   这个 float 截断会让 13.333 这种字号出现 1e-8 级别的偏差，进而在
//   Math::Round 的临界点上翻到另一个整数。看起来像吹毛求疵，但"逐字形 advance 与
//   Windows 差 1 个 ideal 单位"正是 WPF 文本布局最难查的一类差异，而抄下来零成本。
//
//   【三个必须照抄的细节】
//     1. Math::Round 是 **银行家舍入（to-even）**，不是 AwayFromZero：
//        C++/CLI 的 Math::Round 就是 .NET 的 Math.Round(double)。
//        我们这里用 Math.Round(x)（默认 ToEven），而**不是** AwayFromZero。
//     2. advances 用 Math::Round；**offsets 用的是 C 风格强转 (int) = 向零截断**。
//        两者不一致是上游原样如此（TextAnalyzer.cpp:753-754），照抄。
//     3. Ideal 模式下 fontEmSize 在除法里被约掉，**不**参与缩放结果；
//        它只用来告诉 DWrite"按多大的 em 去算"。所以字号变化只通过
//        dwriteAdvance（DWrite 返回的 em 相对值）影响结果。
//
//   同一文件里 GetGlyphPlacementsForControlCharacters（**空格/软连字符走的就是这条**）
//   给出了像素吸附的原文公式：
//
//     double approximatedHyphenAW = Math::Round(glyphMetrics.advanceWidth * fontEmSize
//                                                / font->Metrics->DesignUnitsPerEm * pixelsPerDip) / pixelsPerDip;
//     hyphenAdvanceWidth = (int)Math::Round(approximatedHyphenAW * scalingFactor);
//
//   注意它把 glyphMetrics.advanceWidth 当**设计单位**用（Ideal 取 GetDesignGlyphMetrics，
//   Display 取 GetGdiCompatibleGlyphMetrics）—— 这正好印证了
//   LinuxFontFace.GetDisplayGlyphMetrics 返回设计单位是正确口径。
//
//   于是本文件的公式（两条，各自可被测试证伪）：
//     Ideal  ：advance = round_bankers( (float)(design*emSize/upem) * emSize * sf / (float)emSize )
//     Display：advance = round_bankers( (float)(round(design*emSize*ppdip/upem)/ppdip) * sf )
//
//   【scalingFactor 的真值 = 300】
//     两个真实调用点（LineServicesCallbacks.cs:1713、FullTextLine.cs:731）传的都是
//     TextFormatterImp.ToIdeal = Constants.DefaultRealToIdeal = 28800/96 = 300
//     （LineServices.cs:1290）。也就是说**输出单位是 DIP × 300**，
//     消费方（FormattedTextSymbols.cs:336-360）再用 ToReal = 1/300 折回 DIP。
//     探针与测试都用 300 而不是 1：用 1 会把 Ideal 与 Display 的取整差异抹平。
//
// 【为什么没有 kerning / 没有 GPOS 偏移】
//   kerning 与 mark 定位来自 GSUB/GPOS，属于 shaping。Noto Sans 的 kern 特性在 GPOS 里
//   （表清单实测：GDEF/GPOS/GSUB 都在，但没有 kern 表），而 SkiaSharp 2.88 的
//   GetKerningPairAdjustments 只读旧式 `kern` 表 → 实测 HasGetKerningPairAdjustments=False。
//   所以本轮 advance 就是 hmtx 的步进，glyphOffsets 全 0。
//   这是**明确登记的降级**（REPORT.md 降级清单 #2），不是"忘了"。

using System;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一次定位计算的结果。</summary>
    public sealed class GlyphPlacementData
    {
        /// <summary>逐字形 advance（int，单位 = fontEmSize 的 DIP × scalingFactor，与上游一致）。</summary>
        public int[] Advances { get; init; } = Array.Empty<int>();

        /// <summary>逐字形偏移（du/dv，取整后的 DIP × scalingFactor）。本轮全 0。</summary>
        public GlyphOffsetData[] Offsets { get; init; } = Array.Empty<GlyphOffsetData>();

        /// <summary>未取整的 advance（DIP），用于测试与将来的亚像素排版。</summary>
        public double[] AdvancesExact { get; init; } = Array.Empty<double>();

        /// <summary>整段文字的总长度（取整后的 advance 之和）。</summary>
        public int TotalAdvance { get; init; }

        /// <summary>未取整的总长度。</summary>
        public double TotalAdvanceExact { get; init; }

        public bool IsSideways { get; init; }
    }

    /// <summary>字体面 + 字形序列 → advance / 偏移。</summary>
    public static class GlyphPositioner
    {
        /// <summary>
        /// 按上游公式算逐字形 advance。
        /// <paramref name="useDisplayNatural"/>：true = Ideal（线性），false = Display（像素吸附）。
        /// <paramref name="scalingFactor"/>：真实调用点传 300（= ToIdeal），输出单位 = DIP × scalingFactor。
        /// </summary>
        public static GlyphPlacementData Place(
            LinuxFontFace face,
            ReadOnlySpan<ushort> glyphIndices,
            double fontEmSize,
            double scalingFactor = 1.0,
            bool isSideways = false,
            bool useDisplayNatural = true,
            double pixelsPerDip = 1.0)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));

            int count = glyphIndices.Length;
            var advances = new int[count];
            var offsets = new GlyphOffsetData[count];
            var exact = new double[count];

            if (count == 0)
            {
                return new GlyphPlacementData
                {
                    IsSideways = isSideways,
                };
            }

            // 设计单位步进：竖排且有 vmtx 时用 advanceHeight（DWrite 的 isSideways 语义）。
            var design = new int[count];
            face.GetDesignAdvances(glyphIndices, design);

            OpenTypeFontData ot = face.OpenType;
            bool vertical = isSideways && ot.HasVerticalMetrics;
            if (vertical)
                for (int i = 0; i < count; i++) design[i] = ot.AdvanceHeight(glyphIndices[i]);

            double upem = ot.UnitsPerEm;

            // 上游 TextAnalyzer.cpp:623 的 `FLOAT fontEmSizeFloat = (FLOAT)fontEmSize;`
            // 与 Ideal 分支 `* fontEmSize * scalingFactor / fontEmSizeFloat` 逐字对应。
            double fontEmSizeFloat = (float)fontEmSize;

            double totalExact = 0.0;
            int total = 0;

            for (int i = 0; i < count; i++)
            {
                double designUnits = design[i];

                // DWrite 返回的 FLOAT em 相对 advance（单位 = fontEmSize 的 DIP）。
                // Ideal  = 线性；Display = 先吸附到物理像素网格再折回 DIP（上游控制字符分支公式）。
                double dwriteAdvance;
                if (useDisplayNatural)
                {
                    dwriteAdvance = designUnits * fontEmSize / upem;
                }
                else if (pixelsPerDip > 0)
                {
                    dwriteAdvance = Math.Round(designUnits * fontEmSize / upem * pixelsPerDip) / pixelsPerDip;
                }
                else
                {
                    dwriteAdvance = designUnits * fontEmSize / upem;
                }

                // 关键：DWrite 的 dwriteGlyphAdvances 是 FLOAT，先降到 float 再参与后续运算。
                float dwriteAdvanceFloat = (float)dwriteAdvance;

                // Ideal：`Round(adv * fontEmSize * scalingFactor / fontEmSizeFloat)`
                //   Display：`Round(adv * scalingFactor)`
                // 两者都用**银行家舍入**（Math.Round 默认 ToEven，对应 C++/CLI 的 Math::Round）。
                double scaled = useDisplayNatural
                    ? dwriteAdvanceFloat * fontEmSize * scalingFactor / fontEmSizeFloat
                    : dwriteAdvanceFloat * scalingFactor;

                exact[i] = scaled;
                advances[i] = RoundAdvance(scaled);
                offsets[i] = default;   // 无 GPOS：偏移恒 0（降级清单 #2）
                total += advances[i];
                totalExact += scaled;
            }

            return new GlyphPlacementData
            {
                Advances = advances,
                Offsets = offsets,
                AdvancesExact = exact,
                TotalAdvance = total,
                TotalAdvanceExact = totalExact,
                IsSideways = isSideways,
            };
        }

        /// <summary>
        /// advance 的取整口径：**银行家舍入**（Math.Round 默认 MidpointRounding.ToEven）。
        ///
        /// 上游用的是 C++/CLI 的 <c>Math::Round</c>，它映射到的就是 .NET 的
        /// <c>Math.Round(double)</c> = ToEven。绝大多数人第一直觉会写成"四舍五入"
        /// （AwayFromZero），两者在 n.5 上会差 1 —— 那个差异会以"某几个字的字距差
        /// 1/300 DIP"的形式出现，几乎不可能靠肉眼发现。
        ///
        /// 单独抽成方法是为了让这条口径**可以被直接断言**：
        /// RoundAdvance(2.5)==2 / RoundAdvance(3.5)==4 / RoundAdvance(-2.5)==-2。
        /// </summary>
        public static int RoundAdvance(double scaled) => (int)Math.Round(scaled);

        /// <summary>
        /// 与 <see cref="Place"/> 等价的 Skia 直算路径：让 SKFont 自己报步进。
        /// 测试用它做"同源一致性"：<c>SkiaAdvancePx(size, glyph) == 设计步进 * size/upem</c>。
        /// </summary>
        public static double[] SkiaAdvancesPx(LinuxFontFace face, ReadOnlySpan<ushort> glyphIndices, double size)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));
            if (glyphIndices.Length == 0) return Array.Empty<double>();

            using SKFont font = face.CreateFont(size);
            var widths = new float[glyphIndices.Length];
            var bounds = new SKRect[glyphIndices.Length];
            font.GetGlyphWidths(glyphIndices, widths, bounds, null);

            var result = new double[widths.Length];
            for (int i = 0; i < widths.Length; i++) result[i] = widths[i];
            return result;
        }
    }
}
