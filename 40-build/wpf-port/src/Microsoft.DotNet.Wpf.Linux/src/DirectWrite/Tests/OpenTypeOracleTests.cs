// T2 · Phase 1 —— OpenType 表通路的三方一致（Skia 表访问 vs 原始文件字节 vs 我们的解析）
// =====================================================================================
// 【这一组的价值】
//   provider 的度量/字形/步进全部来自"表直读"。如果表本身读错了（偏移算错、
//   大端读反、loca 格式判错），后面的一切都会错，而且错得很像一个"合理的字体"。
//   所以这里做两件独立的事：
//     1. **表字节本身**：provider 经 Skia 拿到的表字节，必须与测试从 .ttf 文件里
//        按 SFNT 目录表自己切出来的字节**逐字节相同**（含 SHA-256 与长度）；
//     2. **表内容解读**：hmtx 的 advance、cmap 的码点→字形，必须与 Skia 自己的
//        查询结果一致（Skia 是另一套独立实现，读到同一个值才说明我们没读错）。
//
// 【全字形扫描而不是抽样】
//   Noto Sans Regular 有 3884 个字形、cmap 覆盖 BMP + 88 个增补平面码点。
//   这里对**全部字形**与**全部 BMP 码点**做扫描断言（65k 次 cmap 查询 + 3884 次
//   advance 比对，实测都在百毫秒级）。抽样只能证明"抽到的那几个对"，
//   而偏移算错这类 bug 往往只在某一段区间上暴露。

using System;
using System.Collections.Generic;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class OpenTypeOracleTests
    {
        private readonly FontFixture _fixture;

        public OpenTypeOracleTests(FontFixture fixture) => _fixture = fixture;

        /// <summary>从原始 .ttf 字节里按 SFNT 目录表切出一张表（测试侧独立实现）。</summary>
        private static Dictionary<string, byte[]> SliceTablesFromFile(string path, int faceIndex)
        {
            byte[] sfnt = System.IO.File.ReadAllBytes(path);
            uint version = OpenTypeFontData.U32(sfnt, 0);
            int directory = 0;

            if (version == TableTags.Make("ttcf"))
            {
                int count = (int)OpenTypeFontData.U32(sfnt, 8);
                Assert.True(faceIndex < count, "TTC 面下标越界");
                directory = (int)OpenTypeFontData.U32(sfnt, 12 + faceIndex * 4);
            }

            int numTables = OpenTypeFontData.U16(sfnt, directory + 4);
            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            for (int i = 0; i < numTables; i++)
            {
                int rec = directory + 12 + i * 16;
                string tag = TableTags.ToString(OpenTypeFontData.U32(sfnt, rec));
                int offset = (int)OpenTypeFontData.U32(sfnt, rec + 8);
                int length = (int)OpenTypeFontData.U32(sfnt, rec + 12);

                var bytes = new byte[length];
                Array.Copy(sfnt, offset, bytes, 0, length);
                tables[tag] = bytes;
            }

            return tables;
        }

        [Fact]
        public void TableBytes_FromSkia_MatchRawFileBytes_AllFaces()
        {
            int comparedTables = 0;

            foreach (LinuxFont font in _fixture.AllFaces)
            {
                Dictionary<string, byte[]> raw = SliceTablesFromFile(font.FaceEntry.FilePath, font.FaceEntry.FaceIndex);

                _fixture.WithFace(font, face =>
                {
                    var fromSkia = face.OpenType.TableTagsPresent
                        .Select(TableTags.ToString)
                        .OrderBy(t => t, StringComparer.Ordinal)
                        .ToArray();

                    Assert.Equal(raw.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray(), fromSkia);

                    foreach (string tag in fromSkia)
                    {
                        Assert.True(face.TryGetFontTable(TableTags.Make(tag), out byte[] mine), tag + " 取不到");
                        byte[] theirs = raw[tag];

                        Assert.Equal(theirs.Length, mine.Length);
                        Assert.Equal(Convert.ToHexString(theirs), Convert.ToHexString(mine));
                        comparedTables++;
                    }

                    return 0;
                });
            }

            // 4 个面 × 13 张表
            Assert.Equal(52, comparedTables);
        }

        [Fact]
        public void UnknownTable_ReturnsFalseAndNullOrData()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.False(face.TryGetFontTable(TableTags.Make("ZZZZ"), out byte[] data));
                Assert.Null(data);
                return 0;
            });
        }

        [Fact]
        public void HmtxAdvances_MatchSkiaAdvancesForEveryGlyph()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                _fixture.WithFace(font, face =>
                {
                    int glyphCount = face.GlyphCount;
                    var glyphs = new ushort[glyphCount];
                    for (int i = 0; i < glyphCount; i++) glyphs[i] = (ushort)i;

                    // 独立通路：Skia 在 size == upem 时给出的 advance 就是设计单位（浮点）。
                    using var skiaFont = new Sky.SKFont(face.Typeface, face.UnitsPerEm)
                    {
                        LinearMetrics = true,
                        Subpixel = true,
                        Hinting = Sky.SKFontHinting.None,
                    };

                    var widths = new float[glyphCount];
                    var bounds = new Sky.SKRect[glyphCount];
                    skiaFont.GetGlyphWidths(glyphs, widths, bounds, null);

                    int mismatches = 0;
                    for (int i = 0; i < glyphCount; i++)
                    {
                        int mine = face.DesignAdvance((ushort)i);
                        int skia = (int)Math.Round(widths[i]);

                        // 允许 1 个设计单位的差（Skia 内部用定点数，实测偏差 <= 0.005）。
                        if (Math.Abs(mine - skia) > 1) mismatches++;
                    }

                    Assert.True(mismatches == 0,
                        $"{font.FaceEntry.SubFamilyName}: {mismatches}/{glyphCount} 个字形的 hmtx advance 与 Skia 差超过 1 个设计单位");

                    return 0;
                });
            }
        }

        [Fact]
        public void CmapLookup_MatchesSkiaGlyphLookup_EntireBmp()
        {
            foreach (LinuxFont font in _fixture.AllFaces)
            {
                _fixture.WithFace(font, face =>
                {
                    OpenTypeFontData ot = face.OpenType;
                    int mismatch = 0;
                    string firstMismatch = null;

                    for (int cp = 0; cp <= 0xFFFF; cp++)
                    {
                        ushort fromTable = ot.CmapLookup(cp);
                        ushort fromSkia = face.GlyphForCodePoint(cp);
                        if (fromTable == fromSkia) continue;

                        mismatch++;
                        firstMismatch ??= $"U+{cp:X4}: 表={fromTable} Skia={fromSkia}";
                    }

                    Assert.True(mismatch == 0,
                        $"{font.FaceEntry.SubFamilyName}: 全部 BMP 里有 {mismatch} 个码点的 cmap 表直读与 Skia 不一致；首个：{firstMismatch}");

                    return 0;
                });
            }
        }

        [Fact]
        public void CmapLookup_MatchesSkia_SupplementaryPlane()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                OpenTypeFontData ot = face.OpenType;
                int covered = ot.CountSupplementaryCodePoints();
                int first = ot.FindFirstSupplementaryCodePoint();

                // Noto Sans Regular 的增补平面覆盖：实测 88 个码点，从 U+10780 开始。
                Assert.Equal(88, covered);
                Assert.Equal(0x10780, first);

                // 抽查覆盖区间里的码点：表直读 == Skia == 非 .notdef。
                // 注意覆盖**不连续**：实测 U+10780..U+10790 里缺 U+10786，
                // 所以这里用"已知被覆盖的一批码点"点名，而不是假设连续区间。
                var coveredCodePoints = new[]
                {
                    0x10780, 0x10781, 0x10782, 0x10785, 0x10787, 0x1078A, 0x10790,
                };

                foreach (int cp in coveredCodePoints)
                {
                    ushort fromTable = ot.CmapLookup(cp);
                    ushort fromSkia = face.GlyphForCodePoint(cp);
                    Assert.Equal(fromTable, fromSkia);
                    Assert.NotEqual(0, fromTable);
                }

                // 缺的那个洞（U+10786）两边都必须是 .notdef。
                Assert.Equal(0, ot.CmapLookup(0x10786));
                Assert.Equal(0, face.GlyphForCodePoint(0x10786));

                // 未覆盖的增补码点（emoji）必须两边都给 .notdef。
                Assert.Equal(0, ot.CmapLookup(0x1F600));
                Assert.Equal(0, face.GlyphForCodePoint(0x1F600));

                return 0;
            });
        }

        [Fact]
        public void GlyfBounds_MatchLocaOffsets_AndAreSelfConsistent()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                OpenTypeFontData ot = face.OpenType;
                int withBounds = 0, empty = 0;

                for (ushort g = 0; g < ot.NumGlyphs; g++)
                {
                    if (!ot.TryGetGlyphBounds(g, out short xMin, out short yMin, out short xMax, out short yMax))
                    {
                        empty++;
                        continue;
                    }

                    Assert.True(xMax >= xMin && yMax >= yMin, $"字形 {g} 的外接盒非法");
                    withBounds++;
                }

                // 实测 NotoSans-Regular：3884 个字形里 34 个空轮廓（loca[g]==loca[g+1]），
                // 例如 glyph 1/2/3（.notdef 变体与空格）与一批无轮廓的控制/组合字形。
                Assert.Equal(34, empty);
                Assert.Equal(ot.NumGlyphs - 34, withBounds);

                // 逐个点名核对（避免"总数对了但错在别处"）：这几个必须是空的，
                // 而 'A'(36) 必须非空。
                foreach (ushort emptyGlyph in new ushort[] { 1, 2, 3, 98, 1531, 2844, 3753 })
                    Assert.False(ot.TryGetGlyphBounds(emptyGlyph, out _, out _, out _, out _), $"字形 {emptyGlyph} 应为空轮廓");
                Assert.True(ot.TryGetGlyphBounds(36, out _, out _, out _, out _), "字形 36 ('A') 应有轮廓");

                // 空轮廓的字形仍然有 advance（例如空格 = 260）——"没有轮廓"不等于"没有步进"。
                Assert.Equal(260, ot.AdvanceWidth(3));

                // 'A'（glyph 36）的实测 bbox = (0,0,638,717)，与 hmtx lsb=0/advance=639 自洽。
                Assert.True(ot.TryGetGlyphBounds(36, out short ax0, out short ay0, out short ax1, out short ay1));
                Assert.Equal(0, ax0);
                Assert.Equal(638, ax1);
                Assert.Equal(717, ay1);
                Assert.Equal(ot.LeftSideBearing(36), ax0);

                return 0;
            });
        }

        [Fact]
        public void ReadFontEmbeddingRights_MatchesOs2FsType()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                Assert.True(face.ReadFontEmbeddingRights(out ushort fsType));

                // 直接从 OS/2 表的偏移 8 读（测试侧独立实现）。
                byte[] os2 = System.IO.File.ReadAllBytes(_fixture.Regular.FaceEntry.FilePath);
                OpenTypeFontData raw = OpenTypeFontData.FromSfnt(os2, 0);
                byte[] table = raw.Get(TableTags.OS2);
                ushort expected = (ushort)((table[8] << 8) | table[9]);

                Assert.Equal(expected, fsType);
                Assert.Equal(0x0000, fsType);   // Noto Sans 的 fsType = 0（无嵌入限制）
                return 0;
            });
        }

        [Fact]
        public void FontFileAnalysis_ReportsRealKindAndFaceCount()
        {
            _fixture.WithFace(_fixture.Regular, face =>
            {
                LinuxFontFile file = face.GetFileZero();
                Assert.True(file.Analyze(out FontFileAnalysis analysis));

                Assert.Equal(FontFileKind.TrueType, analysis.FileKind);
                Assert.Equal(FontFaceKind.TrueType, analysis.FaceKind);
                Assert.Equal(1, analysis.NumberOfFaces);
                Assert.Equal(TestLayout.FontPath(TestLayout.RegularFile), file.GetUriPath());
                return 0;
            });

            // 不经过 Skia 的文件分析入口（InternalFactory.CreateFontFile 接线要用）。
            Assert.True(LinuxFontFile.AnalyzeFile(
                TestLayout.FontPath(TestLayout.RegularFile), 0, out FontFileAnalysis direct, out OpenTypeFontData parsed));
            Assert.Equal(FontFileKind.TrueType, direct.FileKind);
            Assert.Equal(1, direct.NumberOfFaces);
            Assert.Equal(1000, parsed.UnitsPerEm);

            // 非字体文件 → false（与 DWrite 的 DWRITE_E_FILEFORMAT 等价），不抛。
            string notAFont = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw-not-a-font-" + Guid.NewGuid().ToString("n") + ".ttf");
            System.IO.File.WriteAllText(notAFont, "这不是字体");
            try
            {
                Assert.False(LinuxFontFile.AnalyzeFile(notAFont, 0, out _, out _));
            }
            finally
            {
                System.IO.File.Delete(notAFont);
            }
        }

        [Fact]
        public void InformationalStrings_ComeFromNameTable()
        {
            // 版本串来自 name 表 nameId=5（"Version 2.015"），上游 Font::Version 解析的就是它。
            Assert.True(_fixture.Regular.GetInformationalStrings(InformationalStrings.VersionStrings, out LocalizedStringsData versions));
            Assert.True(versions.Count > 0);
            Assert.Contains("Version 2.015", versions.ValuesArray);

            Assert.Equal(2.015, _fixture.Regular.Version, 6);

            Assert.True(_fixture.Regular.GetInformationalStrings(InformationalStrings.CopyrightNotice, out LocalizedStringsData copyright));
            Assert.Contains("Noto Project Authors", copyright.ValuesArray[0]);

            // 实测 Noto Sans 的 name 表里有 nameId 0..14 与 256..259，
            // 但**没有** 16/17（PreferredFamily/SubFamily）与 19（SampleText）。
            Assert.True(_fixture.Regular.GetInformationalStrings(InformationalStrings.Description, out LocalizedStringsData description));
            Assert.True(description.Count > 0);

            Assert.True(_fixture.Regular.GetInformationalStrings(InformationalStrings.WIN32FamilyNames, out LocalizedStringsData win32Family));
            Assert.Contains("Noto Sans", win32Family.ValuesArray);

            Assert.True(_fixture.Regular.GetInformationalStrings(InformationalStrings.Win32SubFamilyNames, out LocalizedStringsData win32Sub));
            Assert.Contains("Regular", win32Sub.ValuesArray);

            // 不存在的 id 返回 false 且不抛
            Assert.False(_fixture.Regular.GetInformationalStrings(InformationalStrings.PreferredFamilyNames, out LocalizedStringsData preferred));
            Assert.Null(preferred);
            Assert.False(_fixture.Regular.GetInformationalStrings(InformationalStrings.SampleText, out LocalizedStringsData sample));
            Assert.Null(sample);
        }
    }
}
