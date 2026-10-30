// T1 · M7c5（路线 C）—— **运行期布局表剥离**的验收测试。
// =====================================================================================
// 【本文件对应 T1/M7c5 任务的五条硬指标】
//   ① 与 Python oracle 等价：C# 剥 NotoSans-Regular 的结果 vs src/Linux/build/fonts-ui/UI-NoLayout.ttf
//   ② 闸门 2 真的过了：Noto(21)/DejaVu(23) 剥后掩码 = 0、快路径放行
//   ③ 零额外语义损失：upem / glyphCount / 逐字形 hmtx / 全部 65536 个 BMP 码点 / Skia 一致
//   ④ 关掉时行为不变：stripLayout:false 与改动前逐字节一致
//   ⑤ 失败要明说：非 sfnt / 目录损坏 / 无 GSUB-GPOS → 原字节 + 原因
//
// 【刻意的设计：默认开，但每条"原始文件"断言都显式旁路】
//   FontLayoutStripping 默认开（理由见其文件头）。所以任何"测原始文件"的断言都必须
//   显式传 stripLayout:false —— 这不是把断言改小，而是把**两个语义**分开：
//     · 「原始 Noto 的掩码是 21」      ← stripLayout:false（本文件 §②）
//     · 「运行期剥后 Noto 的掩码是 0」 ← stripLayout:true / 默认（本文件 §②）
//   既有 TypographyGateTests 走的是 OpenTypeFontData.FromSfnt（不经加载路径），
//   语义本来就是"原始字节"，因此不受默认值影响、一行未改。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    public class FontLayoutStrippingTests
    {
        private readonly ITestOutputHelper _output;

        public FontLayoutStrippingTests(ITestOutputHelper output) => _output = output;

        private static string RegularPath => TestLayout.FontPath(TestLayout.RegularFile);
        private static string DejaVuPath => "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

        // =================================================================================
        //  开关口径
        // =================================================================================

        [Fact]
        public void DefaultIsOn_AndEnvParsingFollowsTheDocumentedSpelling()
        {
            Assert.True(FontLayoutStripping.DefaultEnabled, "编译期默认必须是开（理由见 FontLayoutStripping 文件头）");

            foreach (string on in new[] { "1", "true", "TRUE", " on ", "yes" })
                Assert.True(FontLayoutStripping.ParseEnv(on), $"'{on}' 应当解析为开");
            foreach (string off in new[] { "0", "false", "FALSE", " off ", "no" })
                Assert.False(FontLayoutStripping.ParseEnv(off), $"'{off}' 应当解析为关");

            // 认不出来（含空）→ 回默认，不因为打错字就改行为
            foreach (string weird in new[] { "", "  ", "maybe", "2" })
                Assert.Equal(FontLayoutStripping.DefaultEnabled, FontLayoutStripping.ParseEnv(weird));

            Assert.Equal("WPF_LINUX_STRIP_LAYOUT", FontLayoutStripping.EnvVar);
            Assert.Equal(new[] { "GSUB", "GPOS" }, FontLayoutStripping.TablesToStrip);
        }

        // =================================================================================
        //  ① 与 Python oracle（src/Linux/build/fonts-ui/UI-NoLayout.ttf）等价
        // =================================================================================

        [Fact]
        public void StrippedNoto_MatchesPythonOracle_PerTableContent()
        {
            byte[] original = File.ReadAllBytes(RegularPath);
            byte[] fromCSharp = FontTableStripper.StripTables(original, "GSUB", "GPOS");

            Assert.True(File.Exists(TestLayout.DerivedUiFontPath),
                "oracle 缺失：src/Linux/build/fonts-ui/UI-NoLayout.ttf（生成方式 python3 src/Linux/build/gen-ui-font.py）");
            byte[] oracle = File.ReadAllBytes(TestLayout.DerivedUiFontPath);

            // ---- 表集合：逐个 tag 完全相同 ----
            string[] tagsCSharp = FontTableStripper.TableTagsOf(fromCSharp);
            string[] tagsOracle = FontTableStripper.TableTagsOf(oracle);
            _output.WriteLine("C#     表集合: " + string.Join(",", tagsCSharp));
            _output.WriteLine("oracle 表集合: " + string.Join(",", tagsOracle));
            Assert.Equal(tagsOracle, tagsCSharp);

            // ---- 逐表内容：字节全等 ----
            OpenTypeFontData dataCSharp = OpenTypeFontData.FromSfnt(fromCSharp);
            OpenTypeFontData dataOracle = OpenTypeFontData.FromSfnt(oracle);

            var mismatch = new List<string>();
            foreach (string tag in tagsOracle)
            {
                uint t = TableTags.Make(tag);
                bool okC = dataCSharp.TryGet(t, out byte[] bc);
                bool okO = dataOracle.TryGet(t, out byte[] bo);
                Assert.True(okC && okO, $"表 {tag} 在某一侧缺失");

                if (!bc.AsSpan().SequenceEqual(bo))
                {
                    int firstDiff = -1;
                    for (int i = 0; i < Math.Min(bc.Length, bo.Length); i++)
                        if (bc[i] != bo[i]) { firstDiff = i; break; }
                    mismatch.Add($"表 {tag}: len {bc.Length}/{bo.Length} 首个差异偏移={firstDiff} " +
                                 $"(csharp=0x{(firstDiff >= 0 && firstDiff < bc.Length ? bc[firstDiff] : 0):X2} " +
                                 $"oracle=0x{(firstDiff >= 0 && firstDiff < bo.Length ? bo[firstDiff] : 0):X2})");
                }
            }
            Assert.True(mismatch.Count == 0, "逐表内容不一致：\n" + string.Join("\n", mismatch));

            // ---- head.checkSumAdjustment 是**唯一**逐表字节会变的字段 ----
            // OpenType 规范要求：head.checkSumAdjustment = 0xB1B0AFBA − 整份文件 checksum。
            // 剥掉两张表之后整份文件 checksum 变了，**必须**重算它；Python oracle 同样这么做。
            // 所以："逐表字节全等"的准确口径是"除 head 偏移 8 起的 4 字节外全等"。
            {
                Assert.True(dataCSharp.TryGet(TableTags.Head, out byte[] headC));
                Assert.True(dataOracle.TryGet(TableTags.Head, out byte[] headO));

                OpenTypeFontData rawData = OpenTypeFontData.FromSfnt(original);
                Assert.True(rawData.TryGet(TableTags.Head, out byte[] headRaw));

                int diffCount = 0, firstDiff = -1;
                Assert.Equal(headRaw.Length, headC.Length);
                for (int i = 0; i < headRaw.Length; i++)
                    if (headRaw[i] != headC[i]) { diffCount++; if (firstDiff < 0) firstDiff = i; }

                _output.WriteLine($"head 表与原文的差异字节数={diffCount}，首个={firstDiff}" +
                                  $"（checkSumAdjustment 在偏移 8..11）");
                Assert.Equal(4, diffCount);
                Assert.Equal(8, firstDiff);

                // 而 C# 与 oracle 的 head 必须**完全**一致（两边都按同一规范重算了）
                Assert.True(headC.AsSpan().SequenceEqual(headO));
            }

            // ---- 整份文件：字节全等？不等就说清差在哪（要求①允许，但必须点名）----
            bool wholeEqual = fromCSharp.AsSpan().SequenceEqual(oracle);
            _output.WriteLine($"整份文件字节全等 = {wholeEqual}" +
                              $"（C# {fromCSharp.Length} B / oracle {oracle.Length} B）");
            if (!wholeEqual)
            {
                int firstDiff = -1;
                for (int i = 0; i < Math.Min(fromCSharp.Length, oracle.Length); i++)
                    if (fromCSharp[i] != oracle[i]) { firstDiff = i; break; }
                _output.WriteLine($"整份首个差异偏移 = {firstDiff}");

                // 不等时至少要证明"除了表顺序/校验和/填充，没有别的差异"：
                // 逐表内容已经全等（上面断言），这里再确认表集合与每个表长度全等。
                Assert.Equal(oracle.Length, fromCSharp.Length);   // 同一批表 + 同一套 4 字节对齐 ⇒ 长度必须相等
            }
        }

        // =================================================================================
        //  ② 闸门 2 真的过了（核心指标）
        // =================================================================================

        [Theory]
        [InlineData("NotoSans-Regular")]
        [InlineData("NotoSans-Bold")]
        [InlineData("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")]
        public void RuntimeStrip_TurnsGate2MaskToZero_AndAllowsFastPath(string which)
        {
            string path = which.StartsWith("/", StringComparison.Ordinal)
                ? which
                : TestLayout.FontPath(which + ".ttf");
            if (!File.Exists(path)) { _output.WriteLine("字体不存在，跳过：" + path); return; }

            // ---- 原始（旁路）：掩码必须还是非 0，且快路径被拒 ----
            using (var raw = LinuxFontFace.FromFile(path, 0, 0, stripLayout: false))
            {
                TypographyEstimate e = TypographyMaskEstimator.Estimate(raw.OpenType);
                _output.WriteLine($"原始 {Path.GetFileName(path)}: {e.Describe()} 快路径允许={e.FastPathAllowedForFastText}");
                Assert.NotEqual(TypographyBits.None, e.Mask);
                Assert.False(e.FastPathAllowedForFastText);

                // 原始文件的 GSUB/GPOS 必须真的在（否则这条测试没意义）
                Assert.True(raw.TryGetFontTable(TableTags.Gsub, out byte[] gsub) && gsub.Length > 0,
                    "原始字体应当有 GSUB");
            }

            // ---- 运行期剥（走默认开关）：掩码 = 0，快路径放行 ----
            using (var stripped = LinuxFontFace.FromFile(path, 0, 0, stripLayout: null))
            {
                TypographyEstimate e = TypographyMaskEstimator.Estimate(stripped.OpenType);
                _output.WriteLine($"剥后 {Path.GetFileName(path)}: {e.Describe()} 快路径允许={e.FastPathAllowedForFastText}");

                Assert.Equal(TypographyBits.None, e.Mask);
                Assert.True(e.FastPathAllowedForFastText);

                // 给 PC 的表读取口也必须如实报"没有"
                Assert.False(stripped.TryGetFontTable(TableTags.Gsub, out _), "GSUB 必须已被剥离");
                Assert.False(stripped.TryGetFontTable(TableTags.Gpos, out _), "GPOS 必须已被剥离");

                // 但**别的表**必须还在（只丢这两个）
                Assert.True(stripped.TryGetFontTable(TableTags.Gdef, out byte[] gdef) && gdef.Length > 0,
                    "GDEF 不在剥离清单里，必须保留");
                Assert.True(stripped.TryGetFontTable(TableTags.Cmap, out byte[] cmap) && cmap.Length > 0);
                Assert.True(stripped.TryGetFontTable(TableTags.Hmtx, out byte[] hmtx) && hmtx.Length > 0);
            }
        }

        // =================================================================================
        //  ③ 零额外语义损失
        // =================================================================================

        [Fact]
        public void RuntimeStrip_PreservesUpem_GlyphCount_PerGlyphAdvance_Cmap_AndSkiaLoad()
        {
            byte[] original = File.ReadAllBytes(RegularPath);

            using var raw = LinuxFontFace.FromFile(RegularPath, 0, 0, stripLayout: false);
            using var stripped = LinuxFontFace.FromFile(RegularPath, 0, 0, stripLayout: true);

            OpenTypeFontData before = raw.OpenType;
            OpenTypeFontData after = stripped.OpenType;

            // ---- 度量 ----
            Assert.Equal(before.UnitsPerEm, after.UnitsPerEm);
            Assert.Equal(before.NumGlyphs, after.NumGlyphs);
            Assert.Equal(3884, after.NumGlyphs);          // 钉住 Noto 的字形数（题目给的值）
            Assert.Equal(before.HheaAscender, after.HheaAscender);
            Assert.Equal(before.HheaDescender, after.HheaDescender);
            Assert.Equal(before.CapHeight, after.CapHeight);
            Assert.Equal(before.XHeight, after.XHeight);
            Assert.Equal(before.UnderlinePosition, after.UnderlinePosition);

            // ---- 逐字形 hmtx 步进 + lsb ----
            for (ushort g = 0; g < before.NumGlyphs; g++)
            {
                Assert.Equal(before.AdvanceWidth(g), after.AdvanceWidth(g));
                Assert.Equal(before.LeftSideBearing(g), after.LeftSideBearing(g));
            }

            // ---- cmap：全部 65536 个 BMP 码点 ----
            for (int cp = 0; cp <= 0xFFFF; cp++)
                Assert.Equal(before.CmapLookup(cp), after.CmapLookup(cp));

            // ---- Skia 侧的加载一致性（两个 SKTypeface 的对照）----
            using Sky.SKTypeface rawFace = Sky.SKTypeface.FromFile(RegularPath);
            Assert.NotNull(stripped.Typeface);
            Assert.Equal(rawFace.GlyphCount, stripped.Typeface.GlyphCount);
            Assert.Equal(rawFace.UnitsPerEm, stripped.Typeface.UnitsPerEm);
            Assert.Equal(rawFace.FamilyName, stripped.Typeface.FamilyName);
            Assert.Equal(rawFace.FontWeight, stripped.Typeface.FontWeight);

            for (int cp = 0x20; cp <= 0x7E; cp++)
                Assert.Equal(rawFace.GetGlyph(cp), stripped.Typeface.GetGlyph(cp));

            // ---- Skia 的步进逐字形一致（size = upem，LinearMetrics，无 hint）----
            var glyphs = new ushort[Math.Min(3884, 4096)];
            for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)i;

            using var fontBefore = new Sky.SKFont(rawFace, rawFace.UnitsPerEm)
            { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            using var fontAfter = new Sky.SKFont(stripped.Typeface, stripped.Typeface.UnitsPerEm)
            { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };

            var widthsBefore = new float[glyphs.Length];
            var widthsAfter = new float[glyphs.Length];
            var bounds = new Sky.SKRect[glyphs.Length];
            fontBefore.GetGlyphWidths(glyphs, widthsBefore, bounds, null);
            fontAfter.GetGlyphWidths(glyphs, widthsAfter, bounds, null);
            Assert.Equal(widthsBefore, widthsAfter);
        }

        // =================================================================================
        //  ④ 关掉时行为不变
        // =================================================================================

        [Fact]
        public void Bypass_LeavesTheFaceIndistinguishableFromTheRawFile()
        {
            byte[] original = File.ReadAllBytes(RegularPath);

            using var bypassed = LinuxFontFace.FromFile(RegularPath, 0, 0, stripLayout: false);
            using var rawFace = Sky.SKTypeface.FromFile(RegularPath);

            // 表集合与逐表字节都必须与磁盘原文件完全一致（即"没被动过"）
            OpenTypeFontData diskView = OpenTypeFontData.FromSfnt(original);
            string[] diskTags = FontTableStripper.TableTagsOf(original);
            Assert.Equal(diskTags, bypassed.OpenType.TableTagsPresent.Select(TableTags.ToString).OrderBy(s => s, StringComparer.Ordinal).ToArray());

            foreach (string tag in diskTags)
            {
                Assert.True(diskView.TryGet(TableTags.Make(tag), out byte[] expected));
                Assert.True(bypassed.TryGetFontTable(TableTags.Make(tag), out byte[] actual));
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"旁路时表 {tag} 被改动了");
            }

            // 掩码仍是原始的 21
            TypographyEstimate e = TypographyMaskEstimator.Estimate(bypassed.OpenType);
            Assert.Equal(21, (int)e.Mask);

            // Skia 面也与 FromFile 直接建出来的等价
            Assert.Equal(rawFace.GlyphCount, bypassed.Typeface.GlyphCount);
            Assert.Equal(rawFace.UnitsPerEm, bypassed.Typeface.UnitsPerEm);
        }

        [Fact]
        public void EnvOff_TurnsTheRuntimeStripOff_AndRestoresTheRawMask()
        {
            bool? savedOverride = FontLayoutStripping.Override;
            try
            {
                FontLayoutStripping.Override = false;      // 等价于 WPF_LINUX_STRIP_LAYOUT=0
                Assert.False(FontLayoutStripping.Enabled);

                using var face = LinuxFontFace.FromFile(RegularPath, 0, 0, stripLayout: null);   // 不传参数 → 走默认
                Assert.True(face.TryGetFontTable(TableTags.Gsub, out byte[] gsub) && gsub.Length > 0);
                Assert.Equal(21, (int)TypographyMaskEstimator.Estimate(face.OpenType).Mask);

                // 显式 true 仍然能强制剥（逐次旁路的另一方向）
                using var forced = LinuxFontFace.FromFile(RegularPath, 0, 0, stripLayout: true);
                Assert.False(forced.TryGetFontTable(TableTags.Gsub, out _));
                Assert.Equal(TypographyBits.None, TypographyMaskEstimator.Estimate(forced.OpenType).Mask);
            }
            finally
            {
                FontLayoutStripping.Override = savedOverride;
            }
        }

        [Fact]
        public void CollectionBypass_KeepsEveryFaceRaw()
        {
            using var collection = LinuxFontCollection.FromDirectory(TestLayout.FontDir, recurse: false, stripLayout: false);
            Assert.True(collection.Families.Count > 0);

            LinuxFont regular = collection["Noto Sans"].GetFirstMatchingFont(400, 5, 0, FontMatchingRule.Distance);
            using LinuxFontFace face = regular.GetFontFace();

            Assert.True(face.TryGetFontTable(TableTags.Gsub, out byte[] gsub) && gsub.Length > 0,
                "旁路的集合里 GSUB 必须还在");
            Assert.Equal(21, (int)TypographyMaskEstimator.Estimate(face.OpenType).Mask);
        }

        [Fact]
        public void CollectionDefault_StripsEveryFace()
        {
            using var collection = LinuxFontCollection.FromDirectory(TestLayout.FontDir);
            Assert.True(collection.Families.Count > 0);

            // ⚠️ GetFontFace() 返回的是**集合缓存里的那个实例**（每次调用 AddRef），
            //    所以这里**不能** using —— 提前 Dispose 会让后面再取到已释放的对象。
            //    （FontFixture 也是这么用的：RegularFace = Regular?.GetFontFace()，不 Dispose。）
            foreach (LinuxFontFamily family in collection.Families)
            {
                foreach (LinuxFont font in family)
                {
                    LinuxFontFace face = font.GetFontFace();
                    Assert.False(face.TryGetFontTable(TableTags.Gsub, out _), $"{family.FamilyName} 的 GSUB 未剥");
                    Assert.False(face.TryGetFontTable(TableTags.Gpos, out _), $"{family.FamilyName} 的 GPOS 未剥");
                }
            }

            LinuxFont regular = collection["Noto Sans"].GetFirstMatchingFont(400, 5, 0, FontMatchingRule.Distance);
            LinuxFontFace f = regular.GetFontFace();
            Assert.Equal(TypographyBits.None, TypographyMaskEstimator.Estimate(f.OpenType).Mask);
        }

        // =================================================================================
        //  ⑤ 失败要明说：原字节 + 原因，绝不产出半个字体
        // =================================================================================

        [Theory]
        // 非 sfnt：随机字节 → 表目录读出来就是垃圾 ⇒ 越界
        [InlineData("random", FontTableStripper.FailureReason.CorruptDirectory)]
        // 目录完好、但里面没有 head ⇒ 不是可用的 TrueType/OpenType
        [InlineData("nohead", FontTableStripper.FailureReason.NoHeadTable)]
        // 太短
        [InlineData("short", FontTableStripper.FailureReason.TooShort)]
        // 空
        [InlineData("empty", FontTableStripper.FailureReason.TooShort)]
        // 表目录越界（声称 100 张表但文件只有 12 字节）
        [InlineData("truncated", FontTableStripper.FailureReason.CorruptDirectory)]
        // TTC：本轮不处理
        [InlineData("ttc", FontTableStripper.FailureReason.TrueTypeCollection)]
        public void TryStrip_ReturnsOriginalBytes_WithAnExplicitReason(string kind, string expectedReason)
        {
            byte[] input = kind switch
            {
                "random" => Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray(),
                "nohead" => MakeSfntWithoutHead(),
                "short" => new byte[] { 0x00, 0x01, 0x00 },
                "empty" => Array.Empty<byte>(),
                "truncated" => MakeTruncatedSfnt(),
                "ttc" => MakeTtcHeader(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            bool ok = FontTableStripper.TryStripTables(input, out byte[] result, out string reason,
                                                       FontLayoutStripping.TablesToStrip);

            Assert.False(ok, $"{kind}: 应当失败");
            Assert.Equal(expectedReason, reason);
            Assert.True(result.AsSpan().SequenceEqual(input), $"{kind}: 失败时必须**原字节**返回，实际长度 {result.Length} vs {input.Length}");
        }

        [Fact]
        public void TryStrip_OnFontWithoutLayoutTables_ReturnsOriginal_AndSaysSo()
        {
            // UI-NoLayout 本身已经剥过 ⇒ 再剥一次应当明确报"没有可剥的表"，而不是产出等价副本
            byte[] alreadyStripped = File.ReadAllBytes(TestLayout.DerivedUiFontPath);

            bool ok = FontTableStripper.TryStripTables(alreadyStripped, out byte[] result, out string reason,
                                                       FontLayoutStripping.TablesToStrip);

            Assert.False(ok);
            Assert.Equal(FontTableStripper.FailureReason.NoLayoutTables, reason);
            Assert.True(result.AsSpan().SequenceEqual(alreadyStripped));
        }

        [Fact]
        public void RuntimeApply_CountsAndReasonsAreObservable()
        {
            bool? saved = FontLayoutStripping.Override;
            FontLayoutStripping.ResetCounters();
            try
            {
                FontLayoutStripping.Override = true;

                // 剥成功
                FontLayoutStripping.Apply(File.ReadAllBytes(RegularPath), null, out bool did, out string why);
                Assert.True(did);
                Assert.StartsWith("stripped", why);
                Assert.Equal(1, FontLayoutStripping.StrippedCount);

                // 本来就没有布局表 → 计入 NoLayoutTables，不算失败
                FontLayoutStripping.Apply(File.ReadAllBytes(TestLayout.DerivedUiFontPath), null, out bool did2, out string why2);
                Assert.False(did2);
                Assert.Equal(FontTableStripper.FailureReason.NoLayoutTables, why2);
                Assert.Equal(1, FontLayoutStripping.NoLayoutTablesCount);
                Assert.Equal(0, FontLayoutStripping.FailedCount);

                // 损坏 → 计入 Failed
                byte[] junk = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
                byte[] back = FontLayoutStripping.Apply(junk, null, out bool did3, out string why3);
                Assert.False(did3);
                Assert.True(back.AsSpan().SequenceEqual(junk));
                Assert.Equal(1, FontLayoutStripping.FailedCount);
                _output.WriteLine("最近原因 = " + FontLayoutStripping.LastReason);

                // 旁路
                FontLayoutStripping.Apply(File.ReadAllBytes(RegularPath), false, out bool did4, out string why4);
                Assert.False(did4);
                Assert.Equal("bypassed", why4);
                Assert.Equal(1, FontLayoutStripping.BypassedCount);
            }
            finally
            {
                FontLayoutStripping.Override = saved;
                FontLayoutStripping.ResetCounters();
            }
        }

        // =================================================================================

        private static byte[] MakeTruncatedSfnt()
        {
            // 合法 sfnt 头（version 0x00010000）+ numTables=100，但文件只有 16 字节 ⇒ 目录越界
            var b = new byte[16];
            b[0] = 0x00; b[1] = 0x01; b[2] = 0x00; b[3] = 0x00;
            b[4] = 0x00; b[5] = 0x64;      // numTables = 100
            return b;
        }

        /// <summary>目录完好（1 张表：cmap），但没有 head。</summary>
        private static byte[] MakeSfntWithoutHead()
        {
            var b = new byte[12 + 16 + 8];
            b[0] = 0x00; b[1] = 0x01; b[2] = 0x00; b[3] = 0x00;   // version 1.0
            b[4] = 0x00; b[5] = 0x01;                             // numTables = 1
            b[6] = 0x00; b[7] = 0x10;                             // searchRange
            b[8] = 0x00; b[9] = 0x00;                             // entrySelector
            b[10] = 0x00; b[11] = 0x00;                           // rangeShift

            int rec = 12;
            b[rec + 0] = (byte)'c'; b[rec + 1] = (byte)'m'; b[rec + 2] = (byte)'a'; b[rec + 3] = (byte)'p';
            // checksum 任意
            b[rec + 8] = 0; b[rec + 9] = 0; b[rec + 10] = 0; b[rec + 11] = 28;   // offset = 28
            b[rec + 12] = 0; b[rec + 13] = 0; b[rec + 14] = 0; b[rec + 15] = 8;  // length = 8
            return b;
        }

        private static byte[] MakeTtcHeader()
        {
            var b = new byte[32];
            b[0] = (byte)'t'; b[1] = (byte)'t'; b[2] = (byte)'c'; b[3] = (byte)'f';
            return b;
        }
    }
}
