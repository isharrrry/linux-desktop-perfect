// T2 · Phase 3 —— 闸门 2（TypographyAvailabilities → fast path）的语义断言
// =====================================================================================
// 【这一组钉的是什么】
//   WPF 决定"能否走名义字形快路径"的**闸门 2** 是
//   `Typeface.CheckFastPathNominalGlyphs`（Typeface.cs:520-562）读
//   `FontFaceLayoutInfo.TypographyAvailabilities`（PC 侧算，读 GSUB/GPOS）。
//   快路径被拒 ⇒ 回落到 LineServices（Linux 上那 110 条 Lo*/Fs*/Nl* 未实现）。
//
//   本组把三件事变成可回归的断言：
//     ① ASCII 文本在打包字体（Noto Sans）上**算出的掩码**与**是否允许快路径**的联合判定；
//     ② 该掩码不是"报多了"——命中的特性在字体里**真实存在且覆盖到 fast-text 字形**
//        （逐条给出 table/script/feature/lookup/coverage 区间作为证据）；
//     ③ 一条**可验证的候选修法**：把 GSUB/GPOS 从字体里剥离后掩码归零、快路径放行，
//        而字形 id / 步进 / cmap / 度量逐项不变（剥离只去掉"快路径本来就不会用"的特性）。
//
// 【判据来源】
//   · 位定义：FontFaceLayoutInfo.cs:1017-1055（Available=1, Ideo=2, FastText=4,
//     FastTextMajorLanguageLocalizedForm=8, FastTextExtraLanguageLocalizedForm=16）
//   · step1/2/3 的特性集合：FontFaceLayoutInfo.cs:782-800（LoclFeature / RequiredTypographyFeatures
//     = {ccmp,rlig,liga,clig,calt,kern,mark,mkmk} / RequiredFeatures = locl + 上面 8 个）
//   · fast-text 范围：FontFaceLayoutInfo.cs:806-815
//   · 闸门判定：Typeface.cs:527（bit4|bit8 命中即拒）
//   本工程的独立复算在 Provider/TypographyMaskEstimator.cs（含边界说明：未知 lookup 格式按
//   "未覆盖"处理 → 结果是**下界**）。
//
// 【与 PC 实测的交叉验证】
//   主控（M7b）在 PC 上实测 DejaVu Sans 的 `TypographyAvailabilities = 23`；
//   本工程的独立复算给出的也是 **23**（逐位相同）→ 说明表字节/读取路径没有问题。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sky = SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class TypographyGateTests
    {
        private readonly FontFixture _fixture;
        private readonly ITestOutputHelper _output;

        public TypographyGateTests(FontFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>主控在 PC 上实测到的 DejaVu Sans 掩码（用于交叉验证）。</summary>
        private const int PcMeasuredDejaVuMask = 23;

        private static readonly (string Name, string Path)[] SystemCandidates =
        {
            ("DejaVu Sans", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
            ("Liberation Sans", "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"),
            ("Liberation Mono", "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf"),
        };

        private static OpenTypeFontData LoadFromFile(string path) =>
            OpenTypeFontData.FromSfnt(File.ReadAllBytes(path));

        // =================================================================================
        //  ① 打包字体（Noto Sans）上的联合判定
        // =================================================================================

        [Fact]
        public void PackagedNotoSans_DeniesFastPath_BecauseOfRealTypographyFeatures()
        {
            foreach (string file in new[] { TestLayout.RegularFile, TestLayout.BoldFile })
            {
                OpenTypeFontData font = LoadFromFile(TestLayout.FontPath(file));
                TypographyEstimate estimate = TypographyMaskEstimator.Estimate(font);

                _output.WriteLine($"{file}: {estimate.Describe()}");
                _output.WriteLine($"    step1(locl): {estimate.Step1Evidence}");
                _output.WriteLine($"    step2(fast-text 特性): {estimate.Step2Evidence}");
                _output.WriteLine($"    step3(全字形): {estimate.Step3Evidence}");

                // **精确值断言**：PC 实测（HintPath 引用形态下）NotoSans-Bold = 21 = 1|4|16，
                // 本工程独立复算在修正 MajorLanguages 后也是 21 —— 两边逐位一致。
                Assert.Equal(21, (int)estimate.Mask);

                // bit 4 必须置位 —— 这是"快路径被拒"的直接原因
                Assert.True((estimate.Mask & TypographyBits.FastTextTypographyAvailable) != 0,
                    $"{file}: 预期 FastTextTypographyAvailable(4) 置位（字体带 ccmp/liga 且覆盖 fast-text 字形），实际掩码 {estimate.Mask}");

                // 联合判定：ASCII（fast-text 分支）**不允许**走快路径
                Assert.False(estimate.FastPathAllowedForFastText,
                    $"{file}: 掩码 {(int)estimate.Mask} 含 bit4 ⇒ 闸门 2 应当拒绝快路径");

                // 证据必须能追到具体特性与 lookup（不是"因为所以"）
                Assert.Contains("ccmp", estimate.Step2Evidence);
                Assert.Contains("lookup[", estimate.Step2Evidence);

                // Noto Sans **没有** hani 脚本 → bit2（Ideo）不该被它置位
                Assert.False((estimate.Mask & TypographyBits.IdeoTypographyAvailable) != 0,
                    $"{file}: Noto Sans 没有 hani 脚本，不该置 IdeoTypographyAvailable(2)");
            }
        }

        [Fact]
        public void PackagedNotoSans_HasNoHaniScript_ButHasFastTextFeaturesOnLatin()
        {
            OpenTypeFontData font = LoadFromFile(TestLayout.FontPath(TestLayout.RegularFile));
            List<LayoutFeatureReference> references = LayoutFeatureReader.ReadAll(font);

            string[] scripts = references.Select(r => r.ScriptTag).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            _output.WriteLine("Noto Sans 布局脚本: " + string.Join(",", scripts));

            Assert.DoesNotContain("hani", scripts);
            Assert.Contains("latn", scripts);

            // latn 上命中的 fast-text 特性（存在性证据）
            string[] hits = references
                .Where(r => r.ScriptTag == "latn" &&
                            Array.IndexOf(LayoutFeatureReader.RequiredTypographyFeatures, r.FeatureTag) >= 0)
                .Select(r => r.FeatureTag)
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            _output.WriteLine("latn 上的 required 特性: " + string.Join(",", hits));
            Assert.Contains("ccmp", hits);
            Assert.Contains("liga", hits);
        }

        // =================================================================================
        //  ② 与 PC 实测值交叉验证（DejaVu Sans）
        // =================================================================================

        [Fact]
        public void DejaVuSans_IndependentEstimate_MatchesPcMeasurement()
        {
            const string path = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            if (!File.Exists(path)) return;   // 系统字体：没有就跳过（本工程不依赖系统字体做判据）

            OpenTypeFontData font = LoadFromFile(path);
            TypographyEstimate estimate = TypographyMaskEstimator.Estimate(font);

            _output.WriteLine($"DejaVu Sans: {estimate.Describe()}");
            _output.WriteLine($"    step2: {estimate.Step2Evidence}");

            // 逐位相等：主控在 PC 上实测 23，本工程独立复算也必须是 23
            Assert.Equal(PcMeasuredDejaVuMask, (int)estimate.Mask);

            // bit2（Ideo）**不是**误报：DejaVu Sans 真的有 hani/kana 脚本与对应特性
            List<LayoutFeatureReference> references = LayoutFeatureReader.ReadAll(font);
            string[] scripts = references.Select(r => r.ScriptTag).Distinct().ToArray();
            Assert.Contains("hani", scripts);
            Assert.Contains("kana", scripts);
            Assert.Contains(references, r => r.ScriptTag == "hani" && r.FeatureTag == "ccmp");

            // bit16（非主流语言 locl）也不是误报：locl 挂在 ISM/NSM/KSM 这些语言系统上
            Assert.Contains(references, r => r.FeatureTag == "locl" && r.ScriptTag == "latn" && r.LangSysTag != "dflt");

            Assert.False(estimate.FastPathAllowedForFastText);
        }

        // =================================================================================
        //  ③ 候选修法：剥离 GSUB/GPOS（快路径放行，且字形/度量不变）
        // =================================================================================

        [Fact]
        public void StrippedNoto_AllowsFastPath_AndPreservesGlyphsMetricsAndAdvances()
        {
            byte[] original = File.ReadAllBytes(TestLayout.FontPath(TestLayout.RegularFile));
            byte[] stripped = FontTableStripper.StripTables(original, "GSUB", "GPOS");

            OpenTypeFontData before = OpenTypeFontData.FromSfnt(original);
            OpenTypeFontData after = OpenTypeFontData.FromSfnt(stripped);

            // ---- 掩码归零：没有布局表 ⇒ 三次 GetComplexLanguageList 都不调用 ⇒ None ----
            TypographyEstimate estimate = TypographyMaskEstimator.Estimate(after);
            _output.WriteLine($"剥离后: {estimate.Describe()} 快路径允许={estimate.FastPathAllowedForFastText}");
            Assert.Equal(TypographyBits.None, estimate.Mask);
            Assert.True(estimate.FastPathAllowedForFastText);

            // ---- 表目录：只剩非布局表 ----
            string[] tags = FontTableStripper.TableTagsOf(stripped);
            Assert.DoesNotContain("GSUB", tags);
            Assert.DoesNotContain("GPOS", tags);
            Assert.Contains("glyf", tags);
            Assert.Contains("cmap", tags);
            Assert.Contains("hmtx", tags);
            Assert.Equal(original.Length > 0 ? 11 : 0, tags.Length);   // 13 - 2

            // ---- 字形/cmap/度量/步进逐项不变 ----
            Assert.Equal(before.UnitsPerEm, after.UnitsPerEm);
            Assert.Equal(before.NumGlyphs, after.NumGlyphs);
            Assert.Equal(before.HheaAscender, after.HheaAscender);
            Assert.Equal(before.HheaDescender, after.HheaDescender);
            Assert.Equal(before.CapHeight, after.CapHeight);
            Assert.Equal(before.XHeight, after.XHeight);
            Assert.Equal(before.UnderlinePosition, after.UnderlinePosition);

            for (ushort g = 0; g < before.NumGlyphs; g++)
            {
                Assert.Equal(before.AdvanceWidth(g), after.AdvanceWidth(g));
                Assert.Equal(before.LeftSideBearing(g), after.LeftSideBearing(g));
            }

            for (int cp = 0; cp <= 0xFFFF; cp++)
                Assert.Equal(before.CmapLookup(cp), after.CmapLookup(cp));

            // ---- Skia 也必须能加载剥离后的字体（不是"我自己的解析器认可"就行）----
            using Sky.SKData originalData = Sky.SKData.CreateCopy(original);
            using Sky.SKTypeface originalFace = Sky.SKTypeface.FromData(originalData);
            using Sky.SKData strippedData = Sky.SKData.CreateCopy(stripped);
            using Sky.SKTypeface strippedFace = Sky.SKTypeface.FromData(strippedData);

            Assert.NotNull(strippedFace);
            Assert.Equal(originalFace.GlyphCount, strippedFace.GlyphCount);
            Assert.Equal(originalFace.UnitsPerEm, strippedFace.UnitsPerEm);
            Assert.Equal(originalFace.FamilyName, strippedFace.FamilyName);

            for (int cp = 0x20; cp <= 0x7E; cp++)
                Assert.Equal(originalFace.GetGlyph(cp), strippedFace.GetGlyph(cp));

            // 步进：Skia 在 size=upem 下的宽度也必须一致
            var glyphs = new ushort[256];
            for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)i;

            using var fontBefore = new Sky.SKFont(originalFace, originalFace.UnitsPerEm) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            using var fontAfter = new Sky.SKFont(strippedFace, strippedFace.UnitsPerEm) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            var widthsBefore = new float[glyphs.Length];
            var widthsAfter = new float[glyphs.Length];
            var bounds = new Sky.SKRect[glyphs.Length];
            fontBefore.GetGlyphWidths(glyphs, widthsBefore, bounds, null);
            fontAfter.GetGlyphWidths(glyphs, widthsAfter, bounds, null);
            Assert.Equal(widthsBefore, widthsAfter);
        }

        /// <summary>
        /// **随仓库交付的派生件**（`src/Linux/build/fonts/UI-NoLayout.ttf`，由 `src/Linux/build/gen-ui-font.py` 生成）：
        /// 掩码必须为 0、快路径放行，且与打包的 NotoSans-Regular 逐项等价（除布局表）。
        /// 这条断言把"派生字体"钉成防回退的资产 —— 它变了就必须重新解释为什么。
        /// </summary>
        [Fact]
        public void ShippedDerivedUiFont_HasZeroMask_AndPreservesEverything()
        {
            string derivedPath = TestLayout.DerivedUiFontPath;
            if (!File.Exists(derivedPath))
            {
                _output.WriteLine("派生件不存在（跳过）：" + derivedPath +
                                  "（生成方式：python3 src/Linux/build/gen-ui-font.py）");
                return;
            }

            byte[] derived = File.ReadAllBytes(derivedPath);
            OpenTypeFontData original = LoadFromFile(TestLayout.FontPath(TestLayout.RegularFile));
            OpenTypeFontData after = OpenTypeFontData.FromSfnt(derived);

            // ① 掩码 = 0（PC 实测与独立复算都是 0）⇒ 闸门 2 放行
            TypographyEstimate estimate = TypographyMaskEstimator.Estimate(after);
            _output.WriteLine($"UI-NoLayout.ttf: {estimate.Describe()} 快路径允许={estimate.FastPathAllowedForFastText}");
            Assert.Equal(0, (int)estimate.Mask);
            Assert.True(estimate.FastPathAllowedForFastText);

            // ② 布局表确实不在了，核心表还在
            string[] tags = FontTableStripper.TableTagsOf(derived);
            Assert.DoesNotContain("GSUB", tags);
            Assert.DoesNotContain("GPOS", tags);
            foreach (string required in new[] { "glyf", "loca", "hmtx", "cmap", "head", "hhea", "maxp", "name", "OS/2", "post" })
                Assert.Contains(required, tags);

            // ③ 字形 / 步进 / cmap / 度量逐项不变
            Assert.Equal(original.UnitsPerEm, after.UnitsPerEm);
            Assert.Equal(original.NumGlyphs, after.NumGlyphs);
            Assert.Equal(original.HheaAscender, after.HheaAscender);
            Assert.Equal(original.HheaDescender, after.HheaDescender);
            Assert.Equal(original.CapHeight, after.CapHeight);
            Assert.Equal(original.XHeight, after.XHeight);
            Assert.Equal(original.UnderlinePosition, after.UnderlinePosition);
            Assert.Equal(original.UnderlineThickness, after.UnderlineThickness);

            for (ushort g = 0; g < original.NumGlyphs; g++)
            {
                Assert.Equal(original.AdvanceWidth(g), after.AdvanceWidth(g));
                Assert.Equal(original.LeftSideBearing(g), after.LeftSideBearing(g));
            }

            for (int cp = 0; cp <= 0xFFFF; cp++)
                Assert.Equal(original.CmapLookup(cp), after.CmapLookup(cp));

            // ④ Skia 必须能加载它，且同一批字形的步进一致
            using Sky.SKData data = Sky.SKData.CreateCopy(derived);
            using Sky.SKTypeface face = Sky.SKTypeface.FromData(data);
            Assert.NotNull(face);
            Assert.Equal(original.NumGlyphs, face.GlyphCount);
            Assert.Equal("Noto Sans", face.FamilyName);

            var glyphs = new ushort[256];
            for (int i = 0; i < glyphs.Length; i++) glyphs[i] = (ushort)i;

            using Sky.SKTypeface originalFace = Sky.SKTypeface.FromFile(TestLayout.FontPath(TestLayout.RegularFile));
            using var fontA = new Sky.SKFont(originalFace, originalFace.UnitsPerEm) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            using var fontB = new Sky.SKFont(face, face.UnitsPerEm) { LinearMetrics = true, Hinting = Sky.SKFontHinting.None };
            var widthsA = new float[glyphs.Length];
            var widthsB = new float[glyphs.Length];
            var bounds = new Sky.SKRect[glyphs.Length];
            fontA.GetGlyphWidths(glyphs, widthsA, bounds, null);
            fontB.GetGlyphWidths(glyphs, widthsB, bounds, null);
            Assert.Equal(widthsA, widthsB);
        }

        [Fact]
        public void StrippedFont_FileRoundTrip_IsAValidSfnt()
        {
            byte[] original = File.ReadAllBytes(TestLayout.FontPath(TestLayout.BoldFile));
            byte[] stripped = FontTableStripper.StripTables(original, "GSUB", "GPOS");

            string path = Path.Combine(Path.GetTempPath(), "dw-stripped-" + Guid.NewGuid().ToString("n") + ".ttf");
            try
            {
                FontTableStripper.WriteFile(stripped, path);
                var analysis = LinuxFontFile.AnalyzeFile(path, 0, out FontFileAnalysis info, out OpenTypeFontData data);
                Assert.True(analysis, "剥离后的字体必须是可解析的 SFNT");
                Assert.Equal(FontFileKind.TrueType, info.FileKind);
                Assert.Equal(3884, data.NumGlyphs);

                // head.checkSumAdjustment 必须被重算（否则严格校验器会拒）
                byte[] head = data.Get(TableTags.Head);
                uint adjustment = OpenTypeFontData.U32(head, 8);
                Assert.NotEqual(0u, adjustment);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // =================================================================================
        //  ④ 系统字体候选（有就验，没有就跳过 —— 判据不依赖系统字体）
        // =================================================================================

        [Fact]
        public void SystemFontCandidates_FastPathVerdicts_AreMeasured()
        {
            foreach ((string name, string path) in SystemCandidates)
            {
                if (!File.Exists(path)) continue;

                TypographyEstimate estimate = TypographyMaskEstimator.Estimate(LoadFromFile(path));
                _output.WriteLine($"{name}: {estimate.Describe()} 快路径允许={estimate.FastPathAllowedForFastText}");

                // 不硬编码每个字体的结论（系统字体版本会变），只断言"结论与其掩码自洽"
                bool expected = estimate.Mask == TypographyBits.None;
                Assert.Equal(expected, estimate.FastPathAllowedForFastText);
            }
        }

        /// <summary>
        /// 联合判定的**语义**断言：掩码 → 快路径结论的映射就是 Typeface.cs:527 那条规则。
        /// 用构造掩码逐位验证，避免"只有一两个字体碰巧对上"。
        /// </summary>
        [Fact]
        public void FastPathRule_MatchesUpstreamGate_BitByBit()
        {
            // 逐位：只有 (4|8) 命中才拒；(16) 命中时看语言是否 major；其余位不影响
            foreach (TypographyBits bit in new[]
                     {
                         TypographyBits.Available,
                         TypographyBits.IdeoTypographyAvailable,
                     })
            {
                var estimate = new TypographyEstimate { Mask = bit };
                Assert.True(estimate.FastPathAllowedForFastText, $"掩码 {bit} 不该拒绝快路径（Typeface.cs:527 只看 bit4/bit8）");
            }

            // bit16 是**有条件的**：上游 return MajorLanguages.Contains(cultureInfo)
            //   → 主流语言（en-US）放行；非主流语言拒绝
            var extraOnly = new TypographyEstimate { Mask = TypographyBits.FastTextExtraLanguageLocalizedFormAvailable };
            Assert.True(extraOnly.FastPathAllowedForFastTextFor(cultureIsMajorLanguage: true),
                "只带 bit16 的字体，主流语言文本仍可走快路径（上游 else-if 的语义）");
            Assert.False(extraOnly.FastPathAllowedForFastTextFor(cultureIsMajorLanguage: false),
                "只带 bit16 的字体，非主流语言文本必须被拒");

            foreach (TypographyBits bit in new[]
                     {
                         TypographyBits.FastTextTypographyAvailable,
                         TypographyBits.FastTextMajorLanguageLocalizedFormAvailable,
                     })
            {
                var estimate = new TypographyEstimate { Mask = bit };
                Assert.False(estimate.FastPathAllowedForFastText, $"掩码 {bit} 必须拒绝快路径");
            }
        }
    }
}
