// T2 · Phase 3 —— 布局 lookup 的 coverage 覆盖检查（判"闸门 2 的 bit 4 是否合法"的关键一步）
// =====================================================================================
// 【为什么"存在特性"还不够】
//   WPF 的 `LayoutEngine.GetComplexLanguageList` 不是"看到 kern 就置位"：
//   它先把请求特性引用的 lookup 标记出来，再对每个被标记的 lookup 调
//   `IsLookupCovered(table, glyphBits, minGlyphId, maxGlyphId)` —— 只有当 lookup 的
//   coverage **覆盖了 fast-text 字形范围（[minGlyphId,maxGlyphId] ∩ glyphBits）** 时才保留。
//   所以"bit 4 = FastTextTypographyAvailable 是否合法"取决于：
//       该特性 → lookup → subtable → Coverage 表里，**有没有 Latin 字形**。
//
//   这一步就是那个检查的**独立**实现（只做判定所需的子集，不追求覆盖 OpenType 全部格式）：
//     · GSUB: type 1(Single) / 2(Multiple) / 4(Ligature) / 6(Chaining ctx fmt1/2/3) / 7(Extension)
//     · GPOS: type 1(Single) / 2(Pair fmt1/2) / 4(MarkBase) / 6(MarkMark) / 9(Extension)
//   其余类型/格式 → 返回 Unknown（如实"没判"，不猜 true/false）。
//
// 【输出】每个命中特性的 coverage 覆盖区间与"是否含 Latin 取样字形"。

using System;
using System.Collections.Generic;
using System.Linq;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一个 lookup 的 coverage 判定结果。</summary>
    public readonly struct LookupCoverage
    {
        public LookupCoverage(string table, int lookupIndex, int lookupType, int glyphMin, int glyphMax, int coveredGlyphCount, bool unknown, string note)
        {
            Table = table;
            LookupIndex = lookupIndex;
            LookupType = lookupType;
            GlyphMin = glyphMin;
            GlyphMax = glyphMax;
            CoveredGlyphCount = coveredGlyphCount;
            Unknown = unknown;
            Note = note;
        }

        public string Table { get; }
        public int LookupIndex { get; }
        public int LookupType { get; }
        public int GlyphMin { get; }
        public int GlyphMax { get; }
        public int CoveredGlyphCount { get; }
        /// <summary>格式不认识 → 本次没判（不是"覆盖"也不是"未覆盖"）。</summary>
        public bool Unknown { get; }
        public string Note { get; }

        public bool Covers(int glyph) => !Unknown && glyph >= GlyphMin && glyph <= GlyphMax;

        public override string ToString() =>
            Unknown
                ? $"{Table}[{LookupIndex}] type={LookupType} <未知格式: {Note}>"
                : $"{Table}[{LookupIndex}] type={LookupType} coverage=[{GlyphMin},{GlyphMax}] n={CoveredGlyphCount}";
    }

    /// <summary>lookup → coverage 的读取（T2 的判据工具）。</summary>
    public static class LayoutLookupCoverageReader
    {
        /// <summary>读出某张布局表里每个 lookup 的 coverage 覆盖区间。</summary>
        public static List<LookupCoverage> Read(OpenTypeFontData font, uint tableTag)
        {
            var result = new List<LookupCoverage>();
            byte[] table = font.Get(tableTag);
            string name = TableTags.ToString(tableTag);
            if (table == null || table.Length < 10) return result;

            int lookupListOffset = OpenTypeFontData.U16(table, 8);
            if (lookupListOffset + 2 > table.Length) return result;

            int lookupCount = OpenTypeFontData.U16(table, lookupListOffset);
            for (int i = 0; i < lookupCount; i++)
            {
                int rec = lookupListOffset + 2 + i * 2;
                if (rec + 2 > table.Length) break;

                int lookupOffset = lookupListOffset + OpenTypeFontData.U16(table, rec);
                if (lookupOffset + 4 > table.Length) continue;

                // Lookup 表布局（OpenType 1.9 §Lookup）：lookupType@0 / lookupFlag@2 /
                // subTableCount@4 / subtableOffsets[]@6 —— **不是** @2/@4。
                // （对照 PC 的权威字段偏移：OpenTypeCommon.cs:1672-1676
                //   offsetLookupType=0 / offsetLookupFlags=2 / offsetSubtableCount=4 / offsetSubtableArray=6）
                int type = OpenTypeFontData.U16(table, lookupOffset);
                int subtableCount = OpenTypeFontData.U16(table, lookupOffset + 4);

                int min = int.MaxValue, max = int.MinValue, total = 0;
                bool unknown = false;
                string note = null;

                for (int s = 0; s < subtableCount; s++)
                {
                    int subRec = lookupOffset + 6 + s * 2;
                    if (subRec + 2 > table.Length) break;

                    int subOffset = lookupOffset + OpenTypeFontData.U16(table, subRec);
                    int effectiveType = type;

                    // Extension（GSUB type 7 / GPOS type 9）：跳到真实类型与 32 位偏移
                    if ((name == "GSUB" && type == 7) || (name == "GPOS" && type == 9))
                    {
                        if (subOffset + 8 > table.Length) { unknown = true; note = "extension 头越界"; continue; }
                        effectiveType = OpenTypeFontData.U16(table, subOffset + 2);
                        subOffset += (int)OpenTypeFontData.U32(table, subOffset + 4);
                    }

                    if (!TryReadCoverage(table, subOffset, effectiveType, name, ref min, ref max, ref total, out string subNote))
                    {
                        unknown = true;
                        note = subNote;
                    }
                }

                result.Add(new LookupCoverage(name, i,
                    type,
                    min == int.MaxValue ? 0 : min,
                    max == int.MinValue ? 0 : max,
                    total,
                    unknown && total == 0,
                    note));
            }

            return result;
        }

        /// <summary>按 subtable 类型找 Coverage 表；返回 false 表示"格式不认识"。</summary>
        private static bool TryReadCoverage(byte[] table, int subtableOffset, int type, string tableName,
                                            ref int min, ref int max, ref int total, out string note)
        {
            note = null;
            if (subtableOffset + 4 > table.Length) { note = "subtable 越界"; return false; }

            int format = OpenTypeFontData.U16(table, subtableOffset);
            int coverageOffset = -1;

            if (tableName == "GSUB")
            {
                switch (type)
                {
                    case 1: case 2: case 4:                    // Single / Multiple / Ligature
                        if (format == 1 || format == 2) coverageOffset = OpenTypeFontData.U16(table, subtableOffset + 2);
                        break;
                    case 6:                                    // Chaining contextual
                        if (format == 1 || format == 2) coverageOffset = OpenTypeFontData.U16(table, subtableOffset + 2);
                        else if (format == 3) coverageOffset = OpenTypeFontData.U16(table, subtableOffset + 2);   // 第一个 coverage
                        break;
                    default:
                        note = $"GSUB type {type} 未实现";
                        return false;
                }
            }
            else // GPOS
            {
                switch (type)
                {
                    case 1: case 2: case 3: case 5: case 6: case 7: case 8:
                        coverageOffset = OpenTypeFontData.U16(table, subtableOffset + 2);
                        break;
                    case 4:                                    // MarkToBase
                        coverageOffset = OpenTypeFontData.U16(table, subtableOffset + 2);
                        break;
                    default:
                        note = $"GPOS type {type} 未实现";
                        return false;
                }
            }

            if (coverageOffset <= 0 || subtableOffset + coverageOffset + 4 > table.Length)
            {
                note = "coverage 偏移越界";
                return false;
            }

            return TryReadCoverageTable(table, subtableOffset + coverageOffset, ref min, ref max, ref total, out note);
        }

        private static bool TryReadCoverageTable(byte[] table, int offset, ref int min, ref int max, ref int total, out string note)
        {
            note = null;
            int format = OpenTypeFontData.U16(table, offset);

            if (format == 1)
            {
                int count = OpenTypeFontData.U16(table, offset + 2);
                for (int i = 0; i < count; i++)
                {
                    int pos = offset + 4 + i * 2;
                    if (pos + 2 > table.Length) break;
                    int g = OpenTypeFontData.U16(table, pos);
                    if (g < min) min = g;
                    if (g > max) max = g;
                    total++;
                }

                return true;
            }

            if (format == 2)
            {
                int rangeCount = OpenTypeFontData.U16(table, offset + 2);
                for (int i = 0; i < rangeCount; i++)
                {
                    int pos = offset + 4 + i * 6;
                    if (pos + 6 > table.Length) break;
                    int start = OpenTypeFontData.U16(table, pos);
                    int end = OpenTypeFontData.U16(table, pos + 2);
                    if (start < min) min = start;
                    if (end > max) max = end;
                    total += end - start + 1;
                }

                return true;
            }

            note = $"coverage format {format} 未知";
            return false;
        }

        /// <summary>
        /// 判据：给定特性集合与"取样字形"，字体里是否存在**覆盖到这些字形**的 lookup。
        /// 这就是 WPF `GetComplexLanguageList` 保留 lookup 的那一步的等价判定（不含 script 归属）。
        /// </summary>
        public static bool AnyFeatureCoversGlyphs(
            OpenTypeFontData font, IEnumerable<string> featureTags, IReadOnlyCollection<ushort> sampleGlyphs,
            out string evidence)
        {
            var tags = new HashSet<string>(featureTags, StringComparer.Ordinal);
            var lines = new List<string>();

            foreach (uint tableTag in new[] { TableTags.Gsub, TableTags.Gpos })
            {
                var refs = LayoutFeatureReader.Read(font, tableTag);
                var coverages = Read(font, tableTag).ToDictionary(c => c.LookupIndex, c => c);

                // 特性 → 它引用的 lookup（特性记录里的 lookupCount/LookupIndex 由 LayoutFeatureReader 给出的是计数，
                // 这里直接按 tag 匹配后回到表里取 lookup 索引列表）
                var featureLookups = ReadFeatureLookupIndices(font.Get(tableTag), tableTag);
                var matched = refs.Where(r => tags.Contains(r.FeatureTag)).ToList();

                foreach (LayoutFeatureReference reference in matched)
                {
                    if (!featureLookups.TryGetValue(reference.FeatureTag, out List<int> lookups)) continue;

                    foreach (int lookupIndex in lookups)
                    {
                        if (!coverages.TryGetValue(lookupIndex, out LookupCoverage coverage)) continue;
                        if (coverage.Unknown) continue;

                        foreach (ushort glyph in sampleGlyphs)
                        {
                            if (!coverage.Covers(glyph)) continue;

                            evidence = $"{reference.Table}/{reference.ScriptTag}/{reference.LangSysTag}:{reference.FeatureTag} → lookup[{lookupIndex}] " +
                                       $"coverage=[{coverage.GlyphMin},{coverage.GlyphMax}] 覆盖字形 {glyph}";
                            return true;
                        }
                    }
                }

                if (matched.Count > 0)
                    lines.Add($"{TableTags.ToString(tableTag)}: {string.Join(",", matched.Select(m => m.FeatureTag).Distinct())} 但未覆盖取样字形");
            }

            evidence = lines.Count > 0 ? string.Join("; ", lines) : "没有命中特性";
            return false;
        }

        /// <summary>特性 tag → 它引用的 lookup 索引列表。</summary>
        private static Dictionary<string, List<int>> ReadFeatureLookupIndices(byte[] table, uint tableTag)
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
}
