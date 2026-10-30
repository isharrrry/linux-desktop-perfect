// T2 · Phase 1 —— 度量断言（真值 + 三方一致）
// =====================================================================================
// 【三方是哪三方】
//   A. 本工程的 FontMetricsData（权威 = OpenType 表直读）
//   B. **测试自己**直接调 SKFont/SKTypeface 算出来的值（不走 provider 的任何代码）
//   C. 测试自己解析 .ttf 字节拿到的 hhea/OS-2/post/head 字段（连 Skia 都不走）
//   A 必须同时等于 B 与 C。B 与 C 在测试文件里独立实现 —— 这是"同源一致性自证"的
//   关键：如果只在 provider 内部对拍，改坏了口径两边一起改，测试永远绿。
//
// 【为什么这些具体数字也写进断言】
//   只断言"A == B"是不够的：如果两边都读到 0（比如字体加载成了空的），等式依然成立。
//   所以每个字段都有一个**实测到的具体值**断言（Noto Sans 的公开度量，
//   可由任何一个字体工具独立复核）。两者合起来才是"真值"。

using System;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class MetricsTests
    {
        private readonly FontFixture _fixture;

        public MetricsTests(FontFixture fixture) => _fixture = fixture;

        [Fact]
        public void PackagedFonts_MatchChecksums()
        {
            var mismatches = TestLayout.VerifyFontChecksums();
            Assert.True(mismatches.Count == 0, "打包字体哈希不一致：\n" + string.Join("\n", mismatches));
        }

        [Fact]
        public void Metrics_HaveKnownGroundTruthValues()
        {
            FontMetricsData m = _fixture.RegularFace.Metrics;

            // Noto Sans（upem=1000）的公开度量：
            //   hhea: ascender=1069 descender=-293 lineGap=0
            //   OS/2: sCapHeight=714 sxHeight=536 yStrikeoutSize=50 yStrikeoutPosition=326(Regular)
            //   post: underlinePosition=-100 underlineThickness=50
            Assert.Equal(1000, m.DesignUnitsPerEm);
            Assert.Equal(1069, m.Ascent);
            Assert.Equal(293, m.Descent);
            Assert.Equal(0, m.LineGap);
            Assert.Equal(714, m.CapHeight);
            Assert.Equal(536, m.XHeight);
            Assert.Equal(-100, m.UnderlinePosition);
            Assert.Equal(50, m.UnderlineThickness);
            Assert.Equal(50, m.StrikethroughThickness);
            Assert.True(m.StrikethroughPosition > 0, "删除线位置按 DWrite 约定应为正（基线之上）");

            // Baseline / LineSpacing 用的是头文件公式，必须能算出来。
            Assert.Equal(1.069, m.Baseline, 6);
            Assert.Equal(1.362, m.LineSpacing, 6);
        }

        [Fact]
        public void AllFourFaces_HaveSameUpemAndConsistentAscentDescent()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                _fixture.WithFace(font, face =>
                {
                    FontMetricsData m = face.Metrics;
                    Assert.Equal(1000, m.DesignUnitsPerEm);
                    Assert.Equal(1069, m.Ascent);
                    Assert.Equal(293, m.Descent);
                    Assert.Equal(0, m.LineGap);
                    Assert.Equal(714, m.CapHeight);
                    Assert.Equal(-100, m.UnderlinePosition);
                    Assert.Equal(50, m.UnderlineThickness);
                    return 0;
                });
            }

            // Bold 的 x-height 与 Regular/Italic 不同（实测 546 vs 536）——
            // 这正好证明我们读到的是**每个面自己的** OS/2 值，而不是一份共用常量。
            Assert.Equal(536, _fixture.RegularFace.Metrics.XHeight);
            Assert.Equal(546, _fixture.WithFace(_fixture.Bold, f => f.Metrics.XHeight));
        }

        [Fact]
        public void ProviderMetrics_EqualIndependentSkiaComputation()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                _fixture.WithFace(font, face =>
                {
                    FontMetricsData mine = face.Metrics;

                    // ---- 独立通路 B：测试自己问 Skia（不经过 provider 的任何代码）----
                    int upem = face.Typeface.UnitsPerEm;
                    using var skiaFont = new Sky.SKFont(face.Typeface, upem)
                    {
                        LinearMetrics = true,
                        Subpixel = true,
                        Hinting = Sky.SKFontHinting.None,
                    };
                    Sky.SKFontMetrics skia = skiaFont.Metrics;

                    Assert.Equal(mine.Ascent, (int)Math.Round(-skia.Ascent));
                    Assert.Equal(mine.Descent, (int)Math.Round(skia.Descent));
                    Assert.Equal(mine.LineGap, (int)Math.Round(skia.Leading));

                    // 符号约定：Skia 的 underline/strikeout 位置与 OpenType 表符号相反，
                    // provider 已取负归一到 DWrite 约定（负数=基线之下 / 正数=基线之上）。
                    if (skia.UnderlinePosition.HasValue)
                    {
                        Assert.Equal(mine.UnderlinePosition, (int)Math.Round(-skia.UnderlinePosition.Value));
                        Assert.Equal(mine.UnderlineThickness, (int)Math.Round(skia.UnderlineThickness ?? 0));
                    }

                    if (skia.StrikeoutPosition.HasValue)
                    {
                        Assert.Equal(mine.StrikethroughPosition, (int)Math.Round(-skia.StrikeoutPosition.Value));
                        Assert.Equal(mine.StrikethroughThickness, (int)Math.Round(skia.StrikeoutThickness ?? 0));
                    }

                    if (skia.CapHeight > 0) Assert.Equal(mine.CapHeight, (int)Math.Round(skia.CapHeight));
                    if (skia.XHeight > 0) Assert.Equal(mine.XHeight, (int)Math.Round(skia.XHeight));

                    return 0;
                });
            }
        }

        [Fact]
        public void ProviderMetrics_EqualIndependentRawTableParsing()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                _fixture.WithFace(font, face =>
                {
                    FontMetricsData mine = face.Metrics;

                    // ---- 独立通路 C：测试自己解析字体文件字节（连 Skia 都不走）----
                    byte[] sfnt = System.IO.File.ReadAllBytes(font.FaceEntry.FilePath);
                    OpenTypeFontData raw = OpenTypeFontData.FromSfnt(sfnt, font.FaceEntry.FaceIndex);

                    // 有符号/无符号不同，统一升到 int 比较（避免 Assert.Equal 选到奇怪的重载）。
                    Assert.Equal(raw.UnitsPerEm, (int)mine.DesignUnitsPerEm);
                    Assert.Equal((int)raw.HheaAscender, (int)mine.Ascent);
                    Assert.Equal(-(int)raw.HheaDescender, (int)mine.Descent);
                    Assert.Equal((int)raw.HheaLineGap, (int)mine.LineGap);
                    Assert.Equal((int)raw.CapHeight, (int)mine.CapHeight);
                    Assert.Equal((int)raw.XHeight, (int)mine.XHeight);
                    Assert.Equal((int)raw.UnderlinePosition, (int)mine.UnderlinePosition);
                    Assert.Equal((int)raw.UnderlineThickness, (int)mine.UnderlineThickness);
                    Assert.Equal((int)raw.StrikeoutPosition, (int)mine.StrikethroughPosition);
                    Assert.Equal((int)raw.StrikeoutSize, (int)mine.StrikethroughThickness);

                    return 0;
                });
            }
        }

        [Fact]
        public void MetricsProvenance_NamesTheRealTables()
        {
            string provenance = _fixture.RegularFace.Metrics.Provenance;

            // 来源串必须能说清"每个数从哪来"——否则"真值"只是形容词。
            Assert.Contains("upem=head", provenance);
            Assert.Contains("asc/desc/gap=hhea(1069,-293,0)", provenance);
            Assert.Contains("cap=OS/2.sCapHeight", provenance);
            Assert.Contains("xh=OS/2.sxHeight", provenance);
            Assert.Contains("ul=post", provenance);
            Assert.Contains("USE_TYPO_METRICS=1", provenance);
            Assert.Contains("win=(1124,395)", provenance);
        }

        [Fact]
        public void DisplayMetrics_SnapToPixelGrid()
        {
            FontMetricsData design = _fixture.RegularFace.Metrics;

            const double emSize = 12.0;
            const double ppdip = 1.25;
            FontMetricsData display = _fixture.RegularFace.DisplayMetrics(emSize, ppdip);

            double scale = emSize * ppdip / design.DesignUnitsPerEm;

            foreach ((string field, int value) in display.Fields())
            {
                if (field == nameof(FontMetricsData.DesignUnitsPerEm)) continue;

                // 不变式：吸附后"换算成物理像素再取整"必须与原值的像素取整相同。
                //
                // 注意这里断言的是 **round(px) 相等**，而不是"px 恰好是整数"：
                // DWRITE_FONT_METRICS 的字段都是**整数设计单位**，把 round(px) 换算回
                // 设计单位必然要再取整一次（upem=1000、scale=0.015 时 11px → 733.33 → 733，
                // 而 733*0.015 = 10.995）。所以能保住的是"像素网格上的那个整数"，
                // 不是"设计单位值恰好等于整像素"。这一条是本工程的显式口径（见
                // LinuxFontFace 文件头的 GetDisplayGlyphMetrics 说明）。
                int designValue = design.Fields().First(f => f.Field == field).Value;
                double pixels = value * scale;
                Assert.Equal(Math.Round(designValue * scale), Math.Round(pixels));
            }

            // 设计度量本身不能被改动（ToDisplay 返回新对象）。
            Assert.Equal(1069, design.Ascent);
        }

        [Fact]
        public void DisplayMetrics_WithIllegalInput_ReturnsDesignValuesUnchanged()
        {
            FontMetricsData design = _fixture.RegularFace.Metrics;

            FontMetricsData zero = _fixture.RegularFace.DisplayMetrics(0, 1.0);
            Assert.Equal(design.Ascent, zero.Ascent);
            Assert.Equal(design.Descent, zero.Descent);

            FontMetricsData negative = _fixture.RegularFace.DisplayMetrics(12, 0);
            Assert.Equal(design.Ascent, negative.Ascent);
        }

        [Fact]
        public void FontFamilyMetrics_UsesRegularFace()
        {
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];

            // 上游 FontFamily::Metrics = GetFirstMatchingFont(Normal,Normal,Normal)->Metrics。
            Assert.Equal(_fixture.RegularFace.Metrics.Ascent, family.Metrics.Ascent);
            Assert.Equal(_fixture.RegularFace.Metrics.XHeight, family.Metrics.XHeight);

            // DisplayMetrics 也必须能用（Ideal 与 Display 两条路都要能出数）。
            FontMetricsData display = family.DisplayMetrics(16, 1.0);
            Assert.True(display.Ascent > 0);
        }

        [Fact]
        public void FamilyOrdinalName_AndNames_AreReal()
        {
            LinuxFontFamily family = _fixture.Collection["Noto Sans"];
            Assert.Equal("Noto Sans", family.OrdinalName);
            Assert.True(family.IsPhysical);
            Assert.False(family.IsComposite);

            // name 表里的族名（本地化集合），至少要有 en-US。
            Assert.True(family.FamilyNames.Count > 0, "族名集合为空");
            Assert.Contains("Noto Sans", family.FamilyNames.ValuesArray);

            // 子族名：来自 name 表 nameId=2（实测 BoldItalic 面里写的是 "Bold Italic"，带空格）。
            Assert.Equal("Regular", _fixture.Regular.FaceEntry.SubFamilyName);
            Assert.Equal("Bold", _fixture.Bold.FaceEntry.SubFamilyName);
            Assert.Equal("Italic", _fixture.Italic.FaceEntry.SubFamilyName);
            Assert.Equal("Bold Italic", _fixture.BoldItalic.FaceEntry.SubFamilyName);
            Assert.Equal("NotoSans-Regular", _fixture.Regular.FaceEntry.PostScriptName);
        }
    }
}
