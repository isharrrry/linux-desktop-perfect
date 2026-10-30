// T2 · Phase 3 —— TypographyAvailabilities 的**独立**估算（供无 PC 环境下取数/回归）
// =====================================================================================
// 【与 PC 的关系】这不是替代 PC 的实现，而是"同一算法的独立复算"，用于：
//   · 在没有 PC 运行环境时给出可比对的数字；
//   · 在测试里把"闸门 2 的语义"钉成断言（防回归）。
//   PC 的权威实现是 FontFaceLayoutInfo.ComputeTypographyAvailabilities（PC 侧，只读）。
//
// 【算法（逐字对齐 FontFaceLayoutInfo.cs:387-545）】
//   step 0  取 fast-text 范围的码点 → 经 cmap 得字形集合（glyphBits）+ min/max
//   step 1  用 {locl} 查"有覆盖的"脚本/语言：命中者按 MajorLanguages 判 → bit8 / bit16
//   step 2  用 {ccmp,rlig,liga,clig,calt,kern,mkmk} 查同样的东西：非空 → bit4
//   step 3  glyphBits 全 1，用 {locl,+step2 的 7 个} 查：返回的脚本里
//             'hani' → bit2；其它 → bit1
//           最后若 mask != 0 再置 bit1
//
// 【本估算的边界（诚实标注）】
//   · "有覆盖"用 LookupCoverage 判定（GSUB/GPOS 常见类型）；未知格式按"未覆盖"处理 →
//     可能**低估** bit4。因此本估算给出的是**下界**。
//   · MajorLanguages 名单按 FontFaceLayoutInfo.cs 的表核对过（见 MajorLanguagesAreMajor）。
//   · 不做 TextFormattingMode / culture 相关分支（那些不在这三个 step 里）。

using System;
using System.Collections.Generic;
using System.Linq;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>独立估算的 TypographyAvailabilities 位。</summary>
    [Flags]
    public enum TypographyBits
    {
        None = 0,
        Available = 1,
        IdeoTypographyAvailable = 2,
        FastTextTypographyAvailable = 4,
        FastTextMajorLanguageLocalizedFormAvailable = 8,
        FastTextExtraLanguageLocalizedFormAvailable = 16,
    }

    /// <summary>估算结果 + 证据。</summary>
    public sealed class TypographyEstimate
    {
        public TypographyBits Mask { get; init; }
        public string Step1Evidence { get; init; }
        public string Step2Evidence { get; init; }
        public string Step3Evidence { get; init; }
        public int FastTextGlyphCount { get; init; }
        public int FastTextGlyphMin { get; init; }
        public int FastTextGlyphMax { get; init; }

        public string Describe() =>
            $"mask={(int)Mask} [{Mask}] fastTextGlyphs={FastTextGlyphCount} range=[{FastTextGlyphMin},{FastTextGlyphMax}]";

        /// <summary>
        /// 闸门 2 的判定（Typeface.cs:520-562 的 FastText 分支，逐字对齐）：
        ///   bit4 或 bit8 置位            → 拒（"Considered too risky to optimize"）
        ///   else bit16 置位              → 返回 MajorLanguages.Contains(cultureInfo)
        ///   else                        → 放行
        /// 注意 **bit16 不是无条件拒**：字体只带"非主流语言的 locl"时，主流语言（如 en-US）
        /// 的文本仍然可以走快路径 —— 这正是上游那条 else-if 的语义。
        /// </summary>
        /// <param name="cultureIsMajorLanguage">输入文本的语言是否属于上游的 MajorLanguages（en/ja/ko/zh/de/fr/…）。</param>
        public bool FastPathAllowedForFastTextFor(bool cultureIsMajorLanguage)
        {
            if ((Mask & (TypographyBits.FastTextTypographyAvailable |
                         TypographyBits.FastTextMajorLanguageLocalizedFormAvailable)) != 0)
                return false;

            if ((Mask & TypographyBits.FastTextExtraLanguageLocalizedFormAvailable) != 0)
                return cultureIsMajorLanguage;

            return true;
        }

        /// <summary>默认按"主流语言"（HelloWpf 的场景就是 en-US）判定。</summary>
        public bool FastPathAllowedForFastText => FastPathAllowedForFastTextFor(cultureIsMajorLanguage: true);
    }

    /// <summary>TypographyAvailabilities 的独立估算。</summary>
    public static class TypographyMaskEstimator
    {
        private static readonly string[] RequiredTypographyFeatures =
            LayoutFeatureReader.RequiredTypographyFeatures;

        private static readonly string[] RequiredFeatures =
            new[] { LayoutFeatureReader.LoclFeature }.Concat(RequiredTypographyFeatures).ToArray();

        /// <summary>估算一份字体（单面）的 TypographyAvailabilities。</summary>
        public static TypographyEstimate Estimate(OpenTypeFontData font)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));

            // ---- step 0：fast-text 字形集合与 min/max ----
            var glyphs = new SortedSet<ushort>();
            foreach ((int start, int end, string _) in LayoutFeatureReader.FastTextRanges)
            {
                for (int cp = start; cp <= end; cp++)
                {
                    ushort g = font.CmapLookup(cp);
                    if (g != 0) glyphs.Add(g);
                }
            }

            var allGsubGposScripts = ScriptsWithCoveredFeatures(font, RequiredFeatures, glyphs, allGlyphs: false, out string step3EvidenceRaw);

            // ---- step 1：locl ----
            TypographyBits mask = TypographyBits.None;
            var loclScripts = ScriptsWithCoveredFeatures(font, new[] { LayoutFeatureReader.LoclFeature }, glyphs, allGlyphs: false,
                                                         out string step1Raw);
            string step1Evidence = step1Raw;
            foreach ((string Script, string LangSys) in loclScripts)
            {
                if (MajorLanguages.IsMajor(Script, LangSys))
                    mask |= TypographyBits.FastTextMajorLanguageLocalizedFormAvailable;
                else
                    mask |= TypographyBits.FastTextExtraLanguageLocalizedFormAvailable;
            }

            // ---- step 2：RequiredTypographyFeatures ----
            var fastTextScripts = ScriptsWithCoveredFeatures(font, RequiredTypographyFeatures, glyphs, allGlyphs: false,
                                                             out string step2Raw);
            string step2Evidence = step2Raw;
            if (fastTextScripts.Count > 0) mask |= TypographyBits.FastTextTypographyAvailable;

            // ---- step 3：glyphBits 全 1 → 覆盖判定退化为"该特性是否有任何覆盖" ----
            var allScripts = ScriptsWithCoveredFeatures(font, RequiredFeatures, glyphs, allGlyphs: true, out string step3Raw);
            foreach ((string Script, string LangSys) in allScripts)
            {
                if (Script == "hani") mask |= TypographyBits.IdeoTypographyAvailable;
                else mask |= TypographyBits.Available;
            }

            if (mask != TypographyBits.None) mask |= TypographyBits.Available;

            _ = allGsubGposScripts;
            return new TypographyEstimate
            {
                Mask = mask,
                Step1Evidence = step1Evidence,
                Step2Evidence = step2Evidence,
                Step3Evidence = step3Raw + "（全字形模式）",
                FastTextGlyphCount = glyphs.Count,
                FastTextGlyphMin = glyphs.Count > 0 ? glyphs.Min : 0,
                FastTextGlyphMax = glyphs.Count > 0 ? glyphs.Max : 0,
            };
        }

        /// <summary>
        /// 返回"引用了请求特性、且该特性的 lookup 覆盖了目标字形集合"的 (script, langSys) 集合。
        /// <paramref name="allGlyphs"/>=true 模拟 step 3 的 glyphBits 全 1（只看 coverage 是否存在）。
        /// </summary>
        private static List<(string Script, string LangSys)> ScriptsWithCoveredFeatures(
            OpenTypeFontData font, string[] featureTags, IReadOnlyCollection<ushort> fastTextGlyphs,
            bool allGlyphs, out string evidence)
        {
            var tags = new HashSet<string>(featureTags, StringComparer.Ordinal);
            var hits = new List<(string, string)>();
            var notes = new List<string>();

            foreach (uint tableTag in new[] { TableTags.Gsub, TableTags.Gpos })
            {
                string tableName = TableTags.ToString(tableTag);
                byte[] table = font.Get(tableTag);
                if (table == null) continue;

                var refs = LayoutFeatureReader.Read(font, tableTag);
                Dictionary<string, List<int>> featureLookups = FeatureLookups(table);
                Dictionary<int, LookupCoverage> coverages = LayoutLookupCoverageReader.Read(font, tableTag)
                    .ToDictionary(c => c.LookupIndex, c => c);

                foreach (LayoutFeatureReference reference in refs.Where(r => tags.Contains(r.FeatureTag)))
                {
                    if (!featureLookups.TryGetValue(reference.FeatureTag, out List<int> lookups)) continue;

                    bool covered = false;
                    foreach (int lookupIndex in lookups)
                    {
                        if (!coverages.TryGetValue(lookupIndex, out LookupCoverage coverage) || coverage.Unknown) continue;

                        if (allGlyphs)
                        {
                            // step 3：glyphBits 全 1 → 只要 coverage 非空就算覆盖
                            if (coverage.CoveredGlyphCount > 0) { covered = true; notes.Add($"{tableName}:{reference.ScriptTag}/{reference.LangSysTag}:{reference.FeatureTag}→lookup[{lookupIndex}] 有 coverage"); break; }
                        }
                        else
                        {
                            foreach (ushort glyph in fastTextGlyphs)
                            {
                                if (!coverage.Covers(glyph))
                                    continue;
                                covered = true;
                                notes.Add($"{tableName}:{reference.ScriptTag}/{reference.LangSysTag}:{reference.FeatureTag}→lookup[{lookupIndex}] coverage=[{coverage.GlyphMin},{coverage.GlyphMax}] 覆盖字形 {glyph}");
                                break;
                            }
                        }

                        if (covered) break;
                    }

                    if (covered && !hits.Contains((reference.ScriptTag, reference.LangSysTag)))
                        hits.Add((reference.ScriptTag, reference.LangSysTag));
                }
            }

            evidence = notes.Count > 0 ? string.Join(" | ", notes.Take(4)) : "无命中";
            return hits;
        }

        private static Dictionary<string, List<int>> FeatureLookups(byte[] table)
        {
            var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            if (table == null || table.Length < 10) return map;

            int featureListOffset = OpenTypeFontData.U16(table, 6);
            if (featureListOffset + 2 > table.Length) return map;

            int featureCount = OpenTypeFontData.U16(table, featureListOffset);
            for (int i = 0; i < featureCount; i++)
            {
                int rec = featureListOffset + 2 + i * 6;
                if (rec + 6 > table.Length) break;

                string tag = TableTags.ToString(OpenTypeFontData.U32(table, rec));
                int featureOffset = featureListOffset + OpenTypeFontData.U16(table, rec + 4);
                if (featureOffset + 4 > table.Length) continue;

                int lookupCount = OpenTypeFontData.U16(table, featureOffset + 2);
                for (int j = 0; j < lookupCount; j++)
                {
                    int pos = featureOffset + 4 + j * 2;
                    if (pos + 2 > table.Length) break;

                    int lookupIndex = OpenTypeFontData.U16(table, pos);
                    if (!map.TryGetValue(tag, out List<int> list)) map[tag] = list = new List<int>();
                    if (!list.Contains(lookupIndex)) list.Add(lookupIndex);
                }
            }

            return map;
        }
    }

    /// <summary>
    /// PC 侧的 MajorLanguages（FontFaceLayoutInfo.cs:981-988 的 majorLanguages 表，**逐条抄自上游**，
    /// 以及同文件 943-949 的 Contains(script, langSys) 语义）：
    ///
    ///     (Latin 'latn', English 'ENG ')  (Latin 'latn', German 'DEU ')
    ///     (CJKIdeographic 'hani', Japanese 'JAN ')  (Hiragana 'kana', Japanese 'JAN ')
    ///
    /// 判据：script 相同且 (langSys == LanguageTags.Default('dflt') 或 langSys == 表中那一个)。
    /// ⚠ 我第一版把 "cyrl/dflt、grek/dflt、latn/dflt…" 都当成主流语言，导致 Noto 的
    /// `cyrl/dflt:locl` 误置 bit8（估 29，而 PC 实测 21）。按上游这 4 条修正后两边一致。
    /// </summary>
    public static class MajorLanguages
    {
        /// <summary>上游 LanguageTags 里与这些主流语言对应的 langSys 标签。</summary>
        private static readonly (string Script, string LangSys)[] Table =
        {
            ("latn", "ENG "),
            ("latn", "DEU "),
            ("hani", "JAN "),
            ("kana", "JAN "),
        };

        public static bool IsMajor(string script, string langSys)
        {
            foreach ((string s, string l) in Table)
            {
                if (!string.Equals(s, script, StringComparison.Ordinal)) continue;

                // 上游：langSys == LanguageTags.Default 也算命中（Default == 'dflt'）
                if (langSys == "dflt" || string.Equals(l, langSys, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}
