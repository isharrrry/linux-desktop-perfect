// T2 · Phase 1 —— 字形索引与边界用例
// =====================================================================================
// 【这一组要钉死的三件事】
//   1. **代理对**：>U+FFFF 的码点必须产生 **1 个字形**，不是 2 个；
//      Noto Sans 真的覆盖了 88 个增补平面码点（U+10780 起），所以这里断言的是
//      "真值代理对"（非 .notdef 的真字形），不是"能跑不崩"。
//   2. **孤立代理**：Skia 的字符串入口对孤立代理会**整串返回空数组**（实测
//      t.GetGlyphs("A\uD800x") == []）。这是静默丢字，绝对不能接受。
//      我们的实现必须给出 3 个字形（中间那个是 .notdef），且不抛异常。
//   3. **notdef 语义**：未映射字符 → glyph 0，且**占一个字形位置**
//      （不是被跳过）。Noto Sans 没有 CJK，正好用来测。
//
// 【clusterMap 的不变式（来自上游消费方 GlyphTypeface.cs:362-399）】
//   clusterMap[0] == 0、单调不减、每项 < GlyphCount、长度 == 字符数。
//   没有哨兵值；同一簇靠数值重复表达（所以代理对的两个 UTF-16 单元给同一个值）。

using System;
using System.Linq;
using System.Text;
using Sky = SkiaSharp;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class GlyphIndexTests
    {
        private readonly FontFixture _fixture;

        public GlyphIndexTests(FontFixture fixture) => _fixture = fixture;

        [Fact]
        public void EmptyString_ProducesNoGlyphs_AndDoesNotThrow()
        {
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, "");

            Assert.Empty(shaped.GlyphIndices);
            Assert.Empty(shaped.ClusterMapUtf16);
            Assert.Empty(shaped.CodePoints);
            Assert.Empty(shaped.Utf16ToGlyph);
            Assert.Equal(0, shaped.NotDefCount);

            ShapedGlyphs nullShaped = GlyphMapper.MapString(_fixture.RegularFace, null);
            Assert.Empty(nullShaped.GlyphIndices);

            // 空串的 advance 也必须是空数组（不是 null，也不是一个 0）。
            GlyphPlacementData placement = GlyphPositioner.Place(_fixture.RegularFace, ReadOnlySpan<ushort>.Empty, 12);
            Assert.Empty(placement.Advances);
            Assert.Equal(0, placement.TotalAdvance);
        }

        [Fact]
        public void AsciiText_MapsOneGlyphPerChar_WithIdentityClusterMap()
        {
            const string text = "Hello WPF on Linux";
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(text.Length, shaped.GlyphIndices.Length);
            Assert.Equal(0, shaped.NotDefCount);

            // 逐字形去 Skia 单码点入口核对（独立通路）。
            for (int i = 0; i < text.Length; i++)
            {
                Assert.Equal(_fixture.RegularFace.GlyphForCodePoint(text[i]), shaped.GlyphIndices[i]);
                Assert.Equal(i, shaped.ClusterMapUtf16[i]);
                Assert.Equal(i, shaped.Utf16ToGlyph[i]);
            }

            // 'H' 的字形 id 实测为 43（Noto Sans 的 cmap 决定的固定值）。
            Assert.Equal(43, shaped.GlyphIndices[0]);
        }

        [Fact]
        public void UnmappedCharacter_MapsToNotDef_AndKeepsItsPosition()
        {
            // Noto Sans 没有 CJK 字形。
            const string text = "a\u4E2D\u6587b";
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(4, shaped.GlyphIndices.Length);   // 不跳过，占位
            Assert.Equal(0, shaped.GlyphIndices[1]);
            Assert.Equal(0, shaped.GlyphIndices[2]);
            Assert.Equal(2, shaped.NotDefCount);
            Assert.Equal(0, shaped.UnpairedSurrogateCount);

            // .notdef 也是一个真字形，有它自己的 advance（Noto Sans 的 .notdef 宽 600）。
            Assert.Equal(600, _fixture.RegularFace.DesignAdvance(0));
        }

        [Fact]
        public void SurrogatePair_ProducesExactlyOneGlyph_ForCoveredCodePoint()
        {
            // U+10780 在 Noto Sans 里**真的有字形**（实测 glyph 3796）。
            const string text = "\U00010780";
            Assert.Equal(2, text.Length);          // UTF-16 长度 2

            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(1, shaped.GlyphIndices.Length);      // 1 个码点 → 1 个字形
            Assert.Equal(1, shaped.SurrogatePairCount);
            Assert.Equal(0, shaped.UnpairedSurrogateCount);
            Assert.Equal(0, shaped.NotDefCount);              // 真字形，不是 .notdef
            Assert.Equal(3796, shaped.GlyphIndices[0]);

            // 码点解码正确（0x10780），且簇起点是 0。
            Assert.Equal(0x10780, shaped.CodePoints[0]);
            Assert.Equal(0, shaped.ClusterMapUtf16[0]);

            // 两个 UTF-16 单元都归属同一个字形（= DWrite clusterMap 的重复值语义）。
            Assert.Equal(new[] { 0, 0 }, shaped.Utf16ToGlyph);

            // 与 Skia 的字符串入口一致（它对这个输入也给 1 个字形）。
            Assert.Equal(new ushort[] { 3796 }, _fixture.RegularFace.Typeface.GetGlyphs(text));
        }

        [Fact]
        public void SurrogatePair_UncoveredCodePoint_StillOneGlyph()
        {
            // U+1F600 不在 Noto Sans 里 → 1 个 .notdef，不是 2 个。
            const string text = "\U0001F600";
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(1, shaped.GlyphIndices.Length);
            Assert.Equal(0, shaped.GlyphIndices[0]);
            Assert.Equal(1, shaped.NotDefCount);
            Assert.Equal(1, shaped.SurrogatePairCount);
        }

        [Fact]
        public void LoneSurrogate_DoesNotSilentlyDropTheWholeString()
        {
            // Skia 的字符串入口在这里返回**空数组** —— 记录这个事实，
            // 后面几条断言证明我们的实现与它不同。
            Assert.Empty(_fixture.RegularFace.Typeface.GetGlyphs("A\uD800x"));
            Assert.Empty(_fixture.RegularFace.Typeface.GetGlyphs("A\uDC00x"));

            ShapedGlyphs high = GlyphMapper.MapString(_fixture.RegularFace, "A\uD800x");
            Assert.Equal(3, high.GlyphIndices.Length);         // 一个都没丢
            Assert.Equal(1, high.UnpairedSurrogateCount);      // 但也如实报告"这是坏输入"
            Assert.Equal(1, high.NotDefCount);
            Assert.Equal(0, high.SurrogatePairCount);
            Assert.Equal(36, high.GlyphIndices[0]);            // 'A'
            Assert.Equal(0, high.GlyphIndices[1]);             // 孤立代理 → .notdef
            Assert.Equal(91, high.GlyphIndices[2]);            // 'x'

            ShapedGlyphs low = GlyphMapper.MapString(_fixture.RegularFace, "A\uDC00x");
            Assert.Equal(3, low.GlyphIndices.Length);
            Assert.Equal(1, low.UnpairedSurrogateCount);

            // 两个孤立代理相邻（高+低以外的情况）：高 高 也必须各占一位。
            ShapedGlyphs twoHigh = GlyphMapper.MapString(_fixture.RegularFace, "\uD800\uD800");
            Assert.Equal(2, twoHigh.GlyphIndices.Length);
            Assert.Equal(2, twoHigh.UnpairedSurrogateCount);

            // 反向顺序（低+高）不成对，也是两个坏码点。
            ShapedGlyphs reversed = GlyphMapper.MapString(_fixture.RegularFace, "\uDC00\uD800");
            Assert.Equal(2, reversed.GlyphIndices.Length);
            Assert.Equal(2, reversed.UnpairedSurrogateCount);
        }

        [Fact]
        public void MultipleSurrogatePairs_ProduceCompressedClusters()
        {
            const string text = "A\U00010780\U00010781B";   // 1 + 2 + 2 + 1 = 6 UTF-16 单元
            Assert.Equal(6, text.Length);

            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(4, shaped.GlyphIndices.Length);
            Assert.Equal(2, shaped.SurrogatePairCount);
            Assert.Equal(new[] { 0, 1, 3, 5 }, shaped.ClusterMapUtf16);
            // 逐 UTF-16 单元 → 字形下标：'A'→0，两个代理对各→1 和 2，'B'→3。
            // （注意这与 ClusterMapUtf16 不是一回事：后者是"逐字形 → 簇起始单元"，即 [0,1,3,5]。）
            Assert.Equal(new[] { 0, 1, 1, 2, 2, 3 }, shaped.Utf16ToGlyph);

            // clusterMap 的三个不变式（上游 GlyphTypeface.cs:362-399 断言的）：
            Assert.Equal(0, shaped.Utf16ToGlyph[0]);                       // 首项为 0
            for (int i = 1; i < shaped.Utf16ToGlyph.Length; i++)
                Assert.True(shaped.Utf16ToGlyph[i] >= shaped.Utf16ToGlyph[i - 1], "clusterMap 必须单调不减");
            foreach (int g in shaped.Utf16ToGlyph)
                Assert.True(g >= 0 && g < shaped.GlyphIndices.Length, "clusterMap 项必须落在字形数组范围内");
        }

        [Fact]
        public void ClusterMapInvariants_HoldForWholeCorpus()
        {
            var corpus = new[]
            {
                "Hello", "\u4E2D\u6587", "\U00010780", "A\U0001F600x", "\uD800", "\uDC00",
                "\uD83D\uDE00\uD83D\uDE00", "e\u0301", " ", "\t\n\r", "AVATAR",
            };

            foreach (string text in corpus)
            {
                ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);
                Assert.Equal(text.Length, shaped.Utf16ToGlyph.Length);

                if (text.Length == 0) continue;

                Assert.Equal(0, shaped.Utf16ToGlyph[0]);
                for (int i = 1; i < shaped.Utf16ToGlyph.Length; i++)
                    Assert.True(shaped.Utf16ToGlyph[i] >= shaped.Utf16ToGlyph[i - 1]);

                foreach (int g in shaped.Utf16ToGlyph)
                    Assert.InRange(g, 0, shaped.GlyphIndices.Length - 1);

                // 码点数 == 字形数（无 shaping 时成立；shaping 之后不再成立，届时要改这条）。
                Assert.Equal(shaped.CodePoints.Length, shaped.GlyphIndices.Length);
            }
        }

        [Fact]
        public void EveryBmpCodePoint_MapsIdenticallyToSkiaSingleCodePointLookup()
        {
            // 全 BMP 扫描：65536 个码点，我们与 Skia 的单码点入口逐一致。
            // 这条同时覆盖了"未映射 → 0"的语义（BMP 里绝大多数码点都是未映射）。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                var codePoints = new int[0x10000];
                for (int i = 0; i < codePoints.Length; i++) codePoints[i] = i;

                var mine = new ushort[codePoints.Length];
                face.GetArrayOfGlyphIndices(codePoints, mine);

                int mapped = 0;
                for (int cp = 0; cp < codePoints.Length; cp++)
                {
                    Assert.Equal(face.GlyphForCodePoint(cp), mine[cp]);
                    Assert.Equal(face.OpenType.CmapLookup(cp), mine[cp]);
                    if (mine[cp] != 0) mapped++;
                }

                // 实测 NotoSans-Regular 的 BMP 覆盖数（cmap format 4 的映射码点数）。
                Assert.True(mapped > 2000, $"BMP 覆盖只有 {mapped} 个码点，不正常");
                return 0;
            });
        }

        [Fact]
        public void VeryLongString_HandledWithoutTruncation()
        {
            var builder = new StringBuilder();
            const string unit = "WPF-Linux abcXYZ 0123 \u00E9\u03B1\u0416";
            while (builder.Length < 200_000) builder.Append(unit);

            string text = builder.ToString(0, 200_000);
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            Assert.Equal(text.Length, shaped.GlyphIndices.Length);
            Assert.Equal(0, shaped.NotDefCount);

            // 两次调用必须逐字节一致（长串不引入任何非确定性）。
            ShapedGlyphs again = GlyphMapper.MapString(_fixture.RegularFace, text);
            Assert.Equal(shaped.GlyphIndices, again.GlyphIndices);
            Assert.Equal(shaped.ClusterMapUtf16, again.ClusterMapUtf16);

            // 长串的定位也要能算（不进位溢出、总长不塌成负数）。
            GlyphPlacementData placement = GlyphPositioner.Place(_fixture.RegularFace, shaped.GlyphIndices, 12, 300.0);
            Assert.Equal(text.Length, placement.Advances.Length);
            Assert.True(placement.TotalAdvance > 0);
        }

        [Fact]
        public void MixedContent_IsNotDefCountedCorrectly()
        {
            // 一段"什么都有"的文本：ASCII + CJK(缺) + 希腊(有) + 代理对(有) + 孤立代理(坏)
            const string text = "Hi \u4E2D \u03B1 \U00010780 A\uDC00z";
            ShapedGlyphs shaped = GlyphMapper.MapString(_fixture.RegularFace, text);

            // 逐个码点核对（测试侧独立解码，与 provider 的实现无关）。
            int expectedCodePoints = 0;
            int expectedPairs = 0;
            int expectedLone = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    expectedCodePoints++;
                    expectedPairs++;
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    expectedCodePoints++;
                    expectedLone++;
                }
                else
                {
                    expectedCodePoints++;
                }
            }

            Assert.Equal(expectedCodePoints, shaped.GlyphIndices.Length);
            Assert.Equal(expectedPairs, shaped.SurrogatePairCount);
            Assert.Equal(expectedLone, shaped.UnpairedSurrogateCount);

            // 未映射的码点：CJK 1 个 + 孤立代理 1 个 = 2
            Assert.Equal(2, shaped.NotDefCount);
        }

        [Fact]
        public void SymbolFontDetection_IsFalseForNotoSans()
        {
            // Noto Sans 没有 cmap(3,0) 符号子表 → IsSymbolFont=false。
            // 而且它的 U+0020..U+00FF 都必须按原码点查表（不走 +0xF000 偏移）。
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.False(face.IsSymbolFont);
                for (int cp = 0x20; cp <= 0xFF; cp++)
                    Assert.Equal(face.OpenType.CmapLookup(cp), face.GlyphForCodePoint(cp));
                return 0;
            });
        }

        [Fact]
        public void HasCharacter_AgreesWithCmap()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.True(GlyphMapper.HasCharacter(face, 'A'));
                Assert.True(GlyphMapper.HasCharacter(face, 0x10780));
                Assert.False(GlyphMapper.HasCharacter(face, 0x4E2D));   // CJK 缺失
                Assert.False(GlyphMapper.HasCharacter(face, 0x1F600));  // emoji 缺失
                Assert.False(GlyphMapper.HasCharacter(face, -1));       // 非法码点
                Assert.False(GlyphMapper.HasCharacter(face, 0x110000)); // 超出 Unicode
                return 0;
            });

            // Font.HasCharacter 与 FontFace 的口径一致。
            Assert.True(_fixture.Regular.HasCharacter('A'));
            Assert.False(_fixture.Regular.HasCharacter(0x4E2D));
        }

        [Fact]
        public void DecodeUtf16_MatchesNetFrameworkDecoding()
        {
            var corpus = new[]
            {
                "", "A", "\uD800", "\uDC00", "\uD800\uDC00", "A\uD800\uDC00B",
                "\uD800\uD800\uDC00", "\uDBFF\uDFFF", "\U0010FFFF",
            };

            foreach (string text in corpus)
            {
                var codePoints = new int[Math.Max(1, text.Length)];
                var utf16Index = new int[Math.Max(1, text.Length)];
                int count = GlyphMapper.DecodeUtf16(text, codePoints, utf16Index, out int lone);

                // 独立通路：.NET 的 StringInfo/Rune 解码。
                var expected = new System.Collections.Generic.List<(int Cp, int Index)>();
                for (int i = 0; i < text.Length; i++)
                {
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        expected.Add((char.ConvertToUtf32(text[i], text[i + 1]), i));
                        i++;
                    }
                    else
                    {
                        expected.Add((text[i], i));
                    }
                }

                Assert.Equal(expected.Count, count);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(expected[i].Cp, codePoints[i]);
                    Assert.Equal(expected[i].Index, utf16Index[i]);
                }

                int expectedLone = expected.Count(e => e.Cp >= 0xD800 && e.Cp <= 0xDFFF);
                Assert.Equal(expectedLone, lone);
            }
        }

        [Fact]
        public void SurrogateHandling_MatchesSkiaStringPath_WhereSkiaIsSane()
        {
            // Skia 的字符串入口在"没有孤立代理"时是可靠的，可以当对照；
            // 我们用它对全部良性输入做一次交叉验证。
            var good = new[]
            {
                "Hello WPF", "\U00010780\U00010781", "A\U00010780B",
                "\u00E9\u03B1\u0416", "0123456789",
            };

            foreach (string text in good)
            {
                ushort[] skia = _fixture.RegularFace.Typeface.GetGlyphs(text);
                ShapedGlyphs mine = GlyphMapper.MapString(_fixture.RegularFace, text);

                Assert.Equal(skia, mine.GlyphIndices);
            }
        }
    }
}
