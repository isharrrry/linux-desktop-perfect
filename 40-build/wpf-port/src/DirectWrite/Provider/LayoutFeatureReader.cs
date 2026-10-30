// T2 · Phase 3 —— GSUB/GPOS 的脚本-语言-特性清单（TypographyAvailabilities 的原始证据）
// =====================================================================================
// 【为什么需要它】
//   WPF 决定"能不能走名义字形快路径"的**闸门 2** 是
//   `FontFaceLayoutInfo.TypographyAvailabilities`（PresentationCore 侧计算，见
//   Typeface.cs:520-562）。它的算法会问两个问题：
//     · 字体里有没有 `locl`（按脚本+语言）→ 置 FastTextMajorLanguage… / …ExtraLanguage… 位
//     · 字体里有没有 `{ccmp,rlig,liga,clig,calt,kern,mark,mkmk}` 且**覆盖 fast-text 字形范围**
//       → 置 FastTextTypographyAvailable，**这一位一旦置上，快路径就被拒**
//   （FontFaceLayoutInfo.cs:789-800 的 RequiredTypographyFeatures，逐字抄自上游）
//
//   要回答"这个位是**误报**还是字体**真有**这些特性"，就必须能看到字体的原始清单：
//   哪张表（GSUB/GPOS）、哪个脚本、哪个 langSys、哪个 feature tag、覆盖多少 lookup。
//   这个文件就干这件事 —— 它是**只读的证据生成器**，不参与任何 WPF 语义判定。
//
// 【解析规范】OpenType 1.9：ScriptList → Script → LangSys → FeatureIndex → FeatureList。
//   · RequiredFeatureIndex == 0xFFFF 表示"没有必需特性"
//   · FeatureList 的每个 FeatureRecord 有 tag 与 Feature 表（含 lookup 数）
//   · 同一张表里同一 tag 可能被多个脚本/语言引用 —— 全部列出，不做去重（去重会丢证据）
//
// 【不做什么】不解析 Lookup/ Coverage（那是"覆盖到哪些字形"的判定，WPF 由
//   OpenTypeLayout.GetComplexLanguageList 内部做）。本文件只给"存在性"这一层证据；
//   是否覆盖 fast-text 字形范围由 PC 的计算给出权威结果（本工程用反射读它，见 WiringSmoke）。

using System;
using System.Collections.Generic;
using System.Linq;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一条"脚本 + 语言系统 + 特性"的引用记录。</summary>
    public readonly struct LayoutFeatureReference
    {
        public LayoutFeatureReference(string table, string scriptTag, string langSysTag, string featureTag, int lookupCount, bool isRequired)
        {
            Table = table;
            ScriptTag = scriptTag;
            LangSysTag = langSysTag;
            FeatureTag = featureTag;
            LookupCount = lookupCount;
            IsRequired = isRequired;
        }

        /// <summary>"GSUB" 或 "GPOS"。</summary>
        public string Table { get; }

        /// <summary>脚本标签（4 字符，如 "latn"）；"DFLT" 表示默认脚本。</summary>
        public string ScriptTag { get; }

        /// <summary>语言系统标签；"dflt" 表示该脚本的默认语言系统。</summary>
        public string LangSysTag { get; }

        /// <summary>特性标签（4 字符，如 "kern"）。</summary>
        public string FeatureTag { get; }

        /// <summary>该 Feature 表引用的 lookup 数（0 说明特性是空的）。</summary>
        public int LookupCount { get; }

        /// <summary>是否是 RequiredFeatureIndex 指到的那一个。</summary>
        public bool IsRequired { get; }

        public override string ToString() =>
            $"{Table}:{ScriptTag}/{LangSysTag}:{FeatureTag}(lookups={LookupCount}{(IsRequired ? ",required" : "")})";
    }

    /// <summary>GSUB/GPOS 的 ScriptList/FeatureList 解析。</summary>
    public static class LayoutFeatureReader
    {
        /// <summary>WPF 用来判"快路径不可优化"的特性集合（FontFaceLayoutInfo.cs:789-800 逐字抄）。</summary>
        public static readonly string[] RequiredTypographyFeatures =
        {
            "ccmp", "rlig", "liga", "clig", "calt", "kern", "mark", "mkmk",
        };

        /// <summary>WPF 用来判语言敏感本地化形式的特性（locl）。</summary>
        public const string LoclFeature = "locl";

        /// <summary>WPF fast-text 的 Unicode 范围（FontFaceLayoutInfo.cs:806-815 逐字抄）。</summary>
        public static readonly (int Start, int End, string Name)[] FastTextRanges =
        {
            (0x20, 0x7E, "basic latin"),
            (0xA1, 0xFF, "latin-1 supplement"),
            (0x0100, 0x017F, "latin extended-A"),
            (0x0180, 0x024F, "latin extended-B"),
            (0x1E00, 0x1EFF, "latin extended additional"),
            (0x3040, 0x3098, "hiragana"),
            (0x309B, 0x309F, "hiragana"),
            (0x30A0, 0x30FF, "kana"),
        };

        /// <summary>读出一张布局表的全部 (脚本, 语言, 特性) 引用；表不存在或缺损 → 空列表。</summary>
        public static List<LayoutFeatureReference> Read(OpenTypeFontData font, uint tableTag)
        {
            var result = new List<LayoutFeatureReference>();
            string tableName = TableTags.ToString(tableTag);

            byte[] table = font.Get(tableTag);
            if (table == null || table.Length < 10) return result;

            int scriptListOffset = OpenTypeFontData.U16(table, 4);
            int featureListOffset = OpenTypeFontData.U16(table, 6);
            if (scriptListOffset <= 0 || featureListOffset <= 0) return result;
            if (scriptListOffset + 2 > table.Length || featureListOffset + 2 > table.Length) return result;

            // ---- FeatureList：index → (tag, lookupCount) ----
            var featureTags = new List<(string Tag, int LookupCount)>();
            int featureCount = OpenTypeFontData.U16(table, featureListOffset);
            for (int i = 0; i < featureCount; i++)
            {
                int record = featureListOffset + 2 + i * 6;
                if (record + 6 > table.Length) break;

                string tag = TableTags.ToString(OpenTypeFontData.U32(table, record));
                int featureOffset = featureListOffset + OpenTypeFontData.U16(table, record + 4);
                int lookupCount = 0;
                if (featureOffset + 4 <= table.Length)
                    lookupCount = OpenTypeFontData.U16(table, featureOffset + 2);

                featureTags.Add((tag, lookupCount));
            }

            // ---- ScriptList：逐脚本、逐 LangSys 展开 FeatureIndex ----
            int scriptCount = OpenTypeFontData.U16(table, scriptListOffset);
            for (int s = 0; s < scriptCount; s++)
            {
                int scriptRecord = scriptListOffset + 2 + s * 6;
                if (scriptRecord + 6 > table.Length) break;

                string scriptTag = TableTags.ToString(OpenTypeFontData.U32(table, scriptRecord));
                int scriptOffset = scriptListOffset + OpenTypeFontData.U16(table, scriptRecord + 4);
                if (scriptOffset + 4 > table.Length) continue;

                int defaultLangSysOffset = OpenTypeFontData.U16(table, scriptOffset);
                int langSysCount = OpenTypeFontData.U16(table, scriptOffset + 2);

                if (defaultLangSysOffset != 0)
                    ReadLangSys(table, scriptOffset + defaultLangSysOffset, scriptTag, "dflt", featureTags, tableName, result);

                for (int l = 0; l < langSysCount; l++)
                {
                    int langRecord = scriptOffset + 4 + l * 6;
                    if (langRecord + 6 > table.Length) break;

                    string langTag = TableTags.ToString(OpenTypeFontData.U32(table, langRecord));
                    int langOffset = scriptOffset + OpenTypeFontData.U16(table, langRecord + 4);
                    ReadLangSys(table, langOffset, scriptTag, langTag, featureTags, tableName, result);
                }
            }

            return result;
        }

        private static void ReadLangSys(
            byte[] table, int langSysOffset, string scriptTag, string langSysTag,
            List<(string Tag, int LookupCount)> featureTags, string tableName, List<LayoutFeatureReference> result)
        {
            if (langSysOffset + 6 > table.Length) return;

            int requiredIndex = OpenTypeFontData.U16(table, langSysOffset + 2);
            int featureIndexCount = OpenTypeFontData.U16(table, langSysOffset + 4);

            for (int i = 0; i < featureIndexCount; i++)
            {
                int pos = langSysOffset + 6 + i * 2;
                if (pos + 2 > table.Length) break;

                int index = OpenTypeFontData.U16(table, pos);
                if (index >= featureTags.Count) continue;

                result.Add(new LayoutFeatureReference(
                    tableName, scriptTag, langSysTag, featureTags[index].Tag, featureTags[index].LookupCount,
                    isRequired: requiredIndex != 0xFFFF && index == requiredIndex));
            }

            // RequiredFeatureIndex 可能指向 FeatureIndex 数组之外（规范允许只在必需特性里列出）
            if (requiredIndex != 0xFFFF && requiredIndex < featureTags.Count)
            {
                bool alreadyListed = false;
                for (int i = 0; i < featureIndexCount; i++)
                {
                    int pos = langSysOffset + 6 + i * 2;
                    if (pos + 2 > table.Length) break;
                    if (OpenTypeFontData.U16(table, pos) == requiredIndex) { alreadyListed = true; break; }
                }

                if (!alreadyListed)
                {
                    result.Add(new LayoutFeatureReference(
                        tableName, scriptTag, langSysTag, featureTags[requiredIndex].Tag,
                        featureTags[requiredIndex].LookupCount, isRequired: true));
                }
            }
        }

        /// <summary>GSUB + GPOS 的全部引用（GSUB 在前，保持确定性顺序）。</summary>
        public static List<LayoutFeatureReference> ReadAll(OpenTypeFontData font)
        {
            var all = new List<LayoutFeatureReference>();
            all.AddRange(Read(font, TableTags.Gsub));
            all.AddRange(Read(font, TableTags.Gpos));
            return all;
        }

        /// <summary>把引用清单压成确定性文本（报告/摘要用）。</summary>
        public static string Describe(IEnumerable<LayoutFeatureReference> references)
        {
            var lines = references
                .Select(r => r.ToString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            return string.Join("; ", lines);
        }

        /// <summary>
        /// 命中 WPF 的 RequiredTypographyFeatures（或 locl）的那些引用 —— 即"闸门 2 的嫌疑犯"。
        /// 注意：这只是**存在性**证据；WPF 还会再判"是否覆盖 fast-text 字形范围"。
        /// </summary>
        public static List<LayoutFeatureReference> FastTextSuspects(OpenTypeFontData font)
        {
            var suspects = new List<LayoutFeatureReference>();
            foreach (LayoutFeatureReference r in ReadAll(font))
            {
                if (Array.IndexOf(RequiredTypographyFeatures, r.FeatureTag) >= 0 ||
                    r.FeatureTag == LoclFeature)
                {
                    suspects.Add(r);
                }
            }

            return suspects;
        }
    }
}
