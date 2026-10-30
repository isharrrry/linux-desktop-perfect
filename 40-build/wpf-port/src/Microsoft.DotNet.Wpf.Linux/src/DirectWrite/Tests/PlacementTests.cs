// T2 · Phase 1 —— advance / 定位 / 字形度量
// =====================================================================================
// 【公式保真度是这一组的唯一主题】
//   上游 TextAnalyzer.cpp:748-765 的两条分支各有细节，任何一条抄错都会让
//   Linux 上的文本比 Windows 差一点点（差 1 个 ideal 单位 ≈ 1/300 DIP），
//   累积到一行文字就是可见的错位。所以这里为每个细节写一条断言：
//     1. Ideal ：Round(adv * fontEmSize * scalingFactor / (float)fontEmSize)，
//                **fontEmSize 被约掉**（字号只通过 adv 影响结果）；
//     2. Display：Round(Round(design*em*ppdip/upem)/ppdip * scalingFactor)，
//                输出恒为 1/ppdip 的整数倍对应的 ideal 单位；
//     3. 舍入是 **银行家舍入**（Math.Round 默认 ToEven），不是四舍五入 AwayFromZero；
//     4. scalingFactor 真值 = 300（LineServices.cs:1290 / TextFormatterImp.ToIdeal）。
//   第 3 条尤其容易抄错：WPF 上游用的是 .NET 的 Math.Round(double)（ToEven），
//   而"四舍五入"是大多数人的第一直觉。构造一个 x.5 的输入就能把两者分开。
//
// 【与 Skia 的关系】
//   advance 的**设计单位**来自 hmtx（权威），Skia 在 size=upem 时的浮点值取整后
//   必须相同（全字形扫描已在 OpenTypeOracleTests 里断言）。这一组再用
//   GlyphPositioner.SkiaAdvancesPx 做一次"任意字号下的等比一致性"验证。

using System;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class PlacementTests
    {
        private const double WpfIdealToReal = 300.0;   // TextFormatterImp.ToIdeal 真值

        private readonly FontFixture _fixture;

        public PlacementTests(FontFixture fixture) => _fixture = fixture;

        [Fact]
        public void IdealAdvance_EqualsRoundedDesignAdvanceTimesScaling()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                // 'H' 的 hmtx advance = 741；字号 12 → 741*12/1000 = 8.892 DIP
                //                              → ×300 = 2667.6 ideal 单位 → 2668
                ushort h = face.GlyphForCodePoint('H');
                Assert.Equal(741, face.DesignAdvance(h));

                GlyphPlacementData placement = GlyphPositioner.Place(
                    face, new[] { h }, 12.0, WpfIdealToReal, false, useDisplayNatural: true);

                Assert.Equal(2668, placement.Advances[0]);

                // 未取整值是 2667.60006 而不是精确的 2667.6 —— 差异来自上游公式里
                // `(FLOAT)dwriteGlyphAdvances` 的 float 截断（TextAnalyzer.cpp:623/:752），
                // 是**刻意保真**的结果，不是误差累积。容差取 3 位。
                Assert.Equal(2667.6, placement.AdvancesExact[0], 3);
                return 0;
            });
        }

        [Fact]
        public void FontEmSize_CancelsInIdealMode()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                ushort h = face.GlyphForCodePoint('H');

                // 上游 Ideal 分支：Round(adv * fontEmSize * sf / (float)fontEmSize)
                // 数学上等于 Round(adv * sf)，fontEmSize **不**缩放结果。
                // adv 本身已经含了 fontEmSize 的缩放（DWrite 返回 em 相对值）。
                int previous = -1;
                foreach (double size in new[] { 8.0, 12.0, 16.0, 24.0, 48.0 })
                {
                    GlyphPlacementData placement = GlyphPositioner.Place(
                        face, new[] { h }, size, WpfIdealToReal, false, useDisplayNatural: true);

                    // 期望 = Round(design * size/upem * 300)
                    int expected = (int)Math.Round(741.0 * size / 1000.0 * WpfIdealToReal);
                    Assert.Equal(expected, placement.Advances[0]);

                    // 顺带确认它不是常量（否则上面这条会退化成"两边都是 0"）
                    Assert.True(placement.Advances[0] > previous, "advance 应随字号单调增大");
                    previous = placement.Advances[0];
                }

                return 0;
            });
        }

        [Fact]
        public void DisplayAdvance_SnapsToPixelGrid()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                ushort h = face.GlyphForCodePoint('H');   // design 741

                // 字号 12、ppdip 1.25：741*12/1000 = 8.892 DIP = 11.115 px
                //   → 吸附 11 px → 8.8 DIP → ×300 = 2640
                GlyphPlacementData display = GlyphPositioner.Place(
                    face, new[] { h }, 12.0, WpfIdealToReal, false, useDisplayNatural: false, pixelsPerDip: 1.25);

                Assert.Equal(2640, display.Advances[0]);
                Assert.Equal(2640.0, display.AdvancesExact[0], 3);

                // 不变式：吸附之后，advance 换算成物理像素必须是整数的 1/300（即 ideal 单位是 300/ppdip 的倍数）。
                double pixels = display.AdvancesExact[0] / WpfIdealToReal * 1.25;
                Assert.Equal(11.0, Math.Round(pixels));       // 吸附到 11 整像素

                // Ideal 与 Display 在这个输入上必须**不同**（否则说明 Display 分支没生效）。
                GlyphPlacementData ideal = GlyphPositioner.Place(
                    face, new[] { h }, 12.0, WpfIdealToReal, false, useDisplayNatural: true, pixelsPerDip: 1.25);

                Assert.NotEqual(ideal.Advances[0], display.Advances[0]);
                return 0;
            });
        }

        [Fact]
        public void Advances_UseBankersRounding_NotAwayFromZero()
        {
            // 直接断言取整口径：C++/CLI 的 Math::Round == .NET Math.Round(double) == ToEven。
            // 2.5 → 2、3.5 → 4、-2.5 → -2 是 ToEven 的指纹；AwayFromZero 会给 3/4/-3。
            Assert.Equal(2, GlyphPositioner.RoundAdvance(2.5));
            Assert.Equal(4, GlyphPositioner.RoundAdvance(3.5));
            Assert.Equal(-2, GlyphPositioner.RoundAdvance(-2.5));
            Assert.Equal(0, GlyphPositioner.RoundAdvance(0.4));
            Assert.Equal(1, GlyphPositioner.RoundAdvance(0.6));

            // 明确区分两种口径：至少存在一个输入两者不同（否则上面那条断言没有意义）。
            Assert.NotEqual(
                (int)Math.Round(2.5, MidpointRounding.AwayFromZero),
                GlyphPositioner.RoundAdvance(2.5));

            // 端到端：Place 的取整结果必须等于"按同一公式算出的未取整值 + ToEven 取整"。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                ushort h = face.GlyphForCodePoint('H');
                const double size = 12.0;
                const double sf = 300.0;

                GlyphPlacementData placement = GlyphPositioner.Place(
                    face, new[] { h }, size, sf, false, useDisplayNatural: true);

                // 与实现同构的公式（含 float 折损）
                double expectedExact = (float)(741.0 * size / 1000.0) * size * sf / (float)size;
                Assert.Equal(expectedExact, placement.AdvancesExact[0], 9);
                Assert.Equal((int)Math.Round(expectedExact), placement.Advances[0]);
                return 0;
            });
        }

        [Fact]
        public void ScaledAdvances_MatchSkiaAdvanceRatiowise()
        {
            // Skia 在**任意字号**下报的 advance（像素）必须等于 设计单位 × size/upem。
            // 这条把"设计单位通路"和"Skia 绘制通路"钉在一起：
            // MIL 用 Skia 画字形时用的步进，正是我们算出来的那个。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                ushort[] glyphs = Enumerable.Range(0, 200).Select(i => (ushort)i).ToArray();

                foreach (double size in new[] { 8.0, 12.0, 16.0, 21.333, 96.0 })
                {
                    double[] skia = GlyphPositioner.SkiaAdvancesPx(face, glyphs, size);

                    for (int i = 0; i < glyphs.Length; i++)
                    {
                        double expected = face.DesignAdvance(glyphs[i]) * size / face.UnitsPerEm;
                        Assert.True(Math.Abs(skia[i] - expected) < 0.01,
                            $"字形 {glyphs[i]} @ {size}px: Skia={skia[i]} 期望={expected}");
                    }
                }

                return 0;
            });
        }

        [Fact]
        public void TotalAdvance_EqualsSumOfAdvances()
        {
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, "Hello WPF on Linux");

            foreach (double ppdip in new[] { 1.0, 1.25 })
            {
                foreach (bool natural in new[] { true, false })
                {
                    GlyphPlacementData placement = GlyphPositioner.Place(
                        _fixture.RegularFace, shaped.GlyphIndices, 13.333, WpfIdealToReal,
                        isSideways: false, useDisplayNatural: natural, pixelsPerDip: ppdip);

                    Assert.Equal(placement.Advances.Sum(), placement.TotalAdvance);
                    Assert.Equal(placement.AdvancesExact.Sum(), placement.TotalAdvanceExact, 6);
                }
            }
        }

        [Fact]
        public void Sideways_WithoutVerticalMetrics_UsesHorizontalAdvances()
        {
            // Noto Sans 没有 vhea/vmtx（实测表清单），所以竖排只能退回水平 advance。
            // 这是**登记的降级**，这里断言的是"退回是显式且可预测的"，不是"竖排正确"。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.False(face.OpenType.HasVerticalMetrics);

                ShapedGlyphs shaped = GlyphMapper.MapString(face, "AV");
                GlyphPlacementData horizontal = GlyphPositioner.Place(
                    face, shaped.GlyphIndices, 12, WpfIdealToReal, isSideways: false, useDisplayNatural: true);
                GlyphPlacementData sideways = GlyphPositioner.Place(
                    face, shaped.GlyphIndices, 12, WpfIdealToReal, isSideways: true, useDisplayNatural: true);

                Assert.True(sideways.IsSideways);
                Assert.Equal(horizontal.Advances, sideways.Advances);
                return 0;
            });
        }

        [Fact]
        public void DesignGlyphMetrics_AreConsistentWithHmtxAndBbox()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                for (ushort g = 0; g < 200; g++)
                {
                    GlyphMetricsData gm = face.DesignMetricsOf(g);

                    int advance = face.OpenType.AdvanceWidth(g);
                    int lsb = face.OpenType.LeftSideBearing(g);
                    Assert.Equal((uint)advance, gm.AdvanceWidth);
                    Assert.Equal(lsb, gm.LeftSideBearing);

                    if (face.OpenType.TryGetGlyphBounds(g, out short xMin, out short yMin, out short xMax, out short yMax))
                    {
                        // rsb = advance - lsb - inkWidth（上游 GlyphMetrics 的定义）
                        Assert.Equal(advance - lsb - (xMax - xMin), gm.RightSideBearing);

                        // 垂直方向：无 vmtx → 合成规则 advanceHeight = ascent - descent，
                        // tsb = (advanceHeight - inkHeight)/2，voy = yMax + tsb
                        int advanceHeight = face.Metrics.Ascent - face.Metrics.Descent;
                        int inkHeight = yMax - yMin;
                        Assert.Equal((uint)advanceHeight, gm.AdvanceHeight);
                        Assert.Equal((advanceHeight - inkHeight) / 2, gm.TopSideBearing);
                        Assert.Equal(yMax + gm.TopSideBearing, gm.VerticalOriginY);
                    }
                    else
                    {
                        // 空轮廓（如空格）：inkWidth = 0 → rsb = advance - lsb
                        Assert.Equal(advance - lsb, gm.RightSideBearing);
                    }
                }

                return 0;
            });
        }

        [Fact]
        public void DisplayGlyphMetrics_NaturalVsSnapped()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                ushort h = face.GlyphForCodePoint('H');
                var glyphs = new[] { h };

                var natural = new GlyphMetricsData[1];
                face.GetDisplayGlyphMetrics(glyphs, natural, emSize: 12, useDisplayNatural: true, isSideways: false, pixelsPerDip: 1.25);
                Assert.Equal(face.DesignMetricsOf(h).AdvanceWidth, natural[0].AdvanceWidth);   // 线性 = 设计单位

                var snapped = new GlyphMetricsData[1];
                face.GetDisplayGlyphMetrics(glyphs, snapped, emSize: 12, useDisplayNatural: false, isSideways: false, pixelsPerDip: 1.25);

                double scale = 12.0 * 1.25 / face.UnitsPerEm;
                double pixels = snapped[0].AdvanceWidth * scale;
                Assert.Equal(11.0, Math.Round(pixels));           // 吸附到 11 整像素
                Assert.NotEqual(natural[0].AdvanceWidth, snapped[0].AdvanceWidth);

                return 0;
            });
        }

        [Fact]
        public void GetDesignGlyphMetrics_BatchEqualsSingle()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                var glyphs = new ushort[300];
                for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)i;

                var batch = new GlyphMetricsData[glyphs.Length];
                face.GetDesignGlyphMetrics(glyphs, batch);

                for (int i = 0; i < glyphs.Length; i++)
                    Assert.Equal(face.DesignMetricsOf(glyphs[i]).ToString(), batch[i].ToString());

                return 0;
            });
        }

        [Fact]
        public void GlyphMetricsFields_MatchDwriteLayoutSize()
        {
            // 骨架的 GlyphMetrics 是 [StructLayout(Explicit, Size=28)]：7 个字段
            // 0/4/8/12/16/20/24 各 4 字节。这里断言我们的字段**数量与类型**与之一致
            // （接线时是逐字段赋值，少一个字段就会编不过）。
            var fields = typeof(GlyphMetricsData).GetFields();
            Assert.Equal(7, fields.Length);
            Assert.Equal(28, fields.Sum(f => System.Runtime.InteropServices.Marshal.SizeOf(f.FieldType)));
            Assert.Equal(
                new[] { "LeftSideBearing", "AdvanceWidth", "RightSideBearing", "TopSideBearing", "AdvanceHeight", "BottomSideBearing", "VerticalOriginY" },
                fields.Select(f => f.Name).ToArray());
        }

        [Fact]
        public void GlyphOffsetFields_MatchDwriteLayoutSize()
        {
            var fields = typeof(GlyphOffsetData).GetFields();
            Assert.Equal(2, fields.Length);
            Assert.Equal(new[] { "du", "dv" }, fields.Select(f => f.Name).ToArray());
            Assert.Equal(8, fields.Sum(f => System.Runtime.InteropServices.Marshal.SizeOf(f.FieldType)));
        }
    }
}
