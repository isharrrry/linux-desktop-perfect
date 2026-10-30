// T2 · Phase 1 —— 字符串 → 字形索引（UTF-16 语义是这里唯一的难点）
// =====================================================================================
// 【为什么不直接用 SKTypeface.GetGlyphs(string)】
//   实测（SkiaSharp 2.88.9 + NotoSans-Regular）：
//     t.GetGlyphs("A\uD83D\uDE00x")  → [36, 0, 91]    4 个 UTF-16 单元 → 3 个字形（代理对=1 个字形）
//     t.GetGlyphs("A\uD800x")        → []             **孤立高代理 → 整串返回空数组**
//     t.GetGlyphs("A\uDC00x")        → []             **孤立低代理 → 整串返回空数组**
//   第二、三条是致命的：文本里出现一个孤立代理（截断的字形串、被切开的 UTF-16、
//   恶意输入都能造成），Skia 的字符串入口会**静默返回 0 个字形**——上层拿到空数组
//   后画出来的是一片空白，而没有任何异常或错误码。这类"静默丢字"是本项目
//   明令禁止的失败模式（见 src/Linux/build/excludes/WindowsBase.txt 的 SplashScreen 教训）。
//
//   所以本工程自己解码 UTF-16：显式区分「成对代理」「孤立代理」「BMP 码点」，
//   再用 **码点数组** 入口 `SKFont.GetGlyphs(ReadOnlySpan<int>, Span<ushort>)`
//   做映射。代价是多一次数组分配，收益是任何输入都有确定、可断言的行为。
//
// 【孤立代理怎么处理】
//   按 Unicode 的约定，孤立代理不是有效码点。我们的策略：
//     · 解码时把它当成一个独立的"伪码点"（值就是 0xD800..0xDFFF 本身）；
//     · 映射时它一定落不到任何 cmap 项 → glyph 0（.notdef）；
//     · **它占一个字形位置**，因此 glyphCount == 伪码点数，charCount 与 glyphCount
//       的对应关系在 ClusterMap 里是显式的。
//   这样"丢了一个字"变成"画了一个 .notdef"，是可见的、可断言的。
//
// 【clusterMap 的语义（DWrite GetGlyphs 的输出之一）—— 已按上游源码核对】
//   上游 TextAnalyzer.cpp 的 GetGlyphs 里 clusterMap 是 **UINT16[textLength]**：
//   逐个**字符**给出"该字符所在簇的第一个字形在 glyph 数组里的下标"。核对结论：
//     · **没有** 0xFFFE / DWRITE_CLUSTER_MAP 之类的哨兵（全树 grep 无此物）；
//       表达"同一簇"的唯一方式就是**数值重复**；
//     · 消费方的用法（GlyphTypeface.cs:362-399 断言 clusterMap[0]==0、单调不减、
//       每项 < GlyphCount；TextShapeableCharacters.cs:139-157 用相邻项是否**变化**
//       来决定插入符停靠点）—— 因此"代理对的两个 UTF-16 单元给同一个值"
//       正是正确行为：中间不会多出一个插入符停靠点。
//   本工程的 <see cref="ShapedGlyphs.Utf16ToGlyph"/> 就是这份无 shaping 时的 clusterMap
//   （逐 UTF-16 单元 → 所属簇的首字形下标）。差别只有长度：
//     DWrite 的按"字符"给（textLength），我们按"UTF-16 单元"给（text.Length）。
//     纯 BMP 文本两者完全相同；含代理对时，我们的那份在 pair 的两个单元上是重复值，
//     折算成 DWrite 形状只需取每个码点的起始单元那一项。
//
// 【symbol 字体】
//   DWrite 对 IsSymbolFont（存在 cmap(3,0) 子表）的字体，会把 U+0020..U+00FF 的字符
//   按 +0xF000 偏移再查一次。本实现照做（先试偏移、再试原值）。

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>一次字符串 shaping 的结果（无 GSUB/GPOS：只有码点→字形与簇映射）。</summary>
    public sealed class ShapedGlyphs
    {
        /// <summary>字形 id 序列（每个码点一个字形；无 shaping 替换）。</summary>
        public ushort[] GlyphIndices { get; init; } = Array.Empty<ushort>();

        /// <summary>
        /// 第 i 个字形对应的簇起始 UTF-16 下标（长度 == 字形数）。
        /// 注：这是"逐字形"的簇起点；DWrite 的 clusterMap 是"逐字符"的首字形下标，
        /// 见 <see cref="Utf16ToGlyph"/>（无 shaping 时两者互为反函数）。
        /// </summary>
        public int[] ClusterMapUtf16 { get; init; } = Array.Empty<int>();

        /// <summary>码点序列（代理对已合成，孤立代理保留为伪码点）。</summary>
        public int[] CodePoints { get; init; } = Array.Empty<int>();

        /// <summary>第 i 个码点对应的起始 UTF-16 下标（长度 == 码点数）。</summary>
        public int[] CodePointUtf16Index { get; init; } = Array.Empty<int>();

        /// <summary>
        /// 每个 UTF-16 单元归属的字形下标（长度 == 输入字符串长度）。
        /// 代理对的第二个单元与第一个指向同一字形 —— 用于命中测试/插入符定位。
        ///
        /// **这就是无 shaping 时的 DWrite clusterMap**（UINT16[textLength]，逐字符给出
        /// "该字符所在簇的第一个字形下标"；上游没有哨兵值，同簇靠数值重复表达）。
        /// 满足上游消费方断言的三个不变式：clusterMap[0]==0（非空输入）、单调不减、
        /// 每项 &lt; 字形总数 —— 由 ClusterMapInvariantsHold 测试逐条断言。
        /// </summary>
        public int[] Utf16ToGlyph { get; init; } = Array.Empty<int>();

        /// <summary>落成 .notdef(0) 的字形个数（未映射字符 + 孤立代理）。</summary>
        public int NotDefCount { get; init; }

        /// <summary>孤立代理的个数（单独计数：它是"输入有问题"，不是"字体缺字"）。</summary>
        public int UnpairedSurrogateCount { get; init; }

        /// <summary>成对代理（>U+FFFF 真码点）的个数。</summary>
        public int SurrogatePairCount { get; init; }

        /// <summary>确定性文本形式（跨进程哈希用）。</summary>
        public override string ToString() =>
            $"glyphs=[{string.Join(",", GlyphIndices)}] clusters=[{string.Join(",", ClusterMapUtf16)}] " +
            $"cps=[{string.Join(",", CodePoints)}] notdef={NotDefCount} lone={UnpairedSurrogateCount} pairs={SurrogatePairCount}";
    }

    /// <summary>UTF-16 → 码点 → 字形。</summary>
    public static class GlyphMapper
    {
        /// <summary>
        /// 解码 UTF-16 到码点。返回码点数，写入 <paramref name="codePoints"/> 与
        /// <paramref name="utf16Index"/>（调用方需保证容量 &gt;= text.Length）。
        /// <paramref name="unpairedSurrogates"/> 返回孤立代理个数。
        /// </summary>
        public static int DecodeUtf16(string text, Span<int> codePoints, Span<int> utf16Index, out int unpairedSurrogates)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (codePoints.Length < text.Length) throw new ArgumentException("codePoints 容量不足", nameof(codePoints));
            if (utf16Index.Length < text.Length) throw new ArgumentException("utf16Index 容量不足", nameof(utf16Index));

            int count = 0;
            unpairedSurrogates = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        codePoints[count] = char.ConvertToUtf32(c, text[i + 1]);
                        utf16Index[count] = i;
                        count++;
                        i++;                       // 吃掉低位代理
                        continue;
                    }

                    // 孤立高代理：保留为伪码点（一定映射到 .notdef，但**不丢位置**）。
                    unpairedSurrogates++;
                    codePoints[count] = c;
                    utf16Index[count] = i;
                    count++;
                    continue;
                }

                if (char.IsLowSurrogate(c))
                {
                    // 孤立低代理（前面没有高代理）。
                    unpairedSurrogates++;
                    codePoints[count] = c;
                    utf16Index[count] = i;
                    count++;
                    continue;
                }

                codePoints[count] = c;
                utf16Index[count] = i;
                count++;
            }

            return count;
        }

        /// <summary>
        /// 码点 → 字形。用码点数组入口（不是字符串入口），因此孤立代理不会导致整串丢失。
        /// <paramref name="blankGlyphIndex"/> 用于替换"未映射"的字形（DWrite 的 blankGlyphIndex；
        /// 传 0 就是标准 .notdef）。
        /// </summary>
        public static void MapCodePoints(LinuxFontFace face, ReadOnlySpan<int> codePoints, Span<ushort> glyphs, ushort blankGlyphIndex = 0)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));
            if (glyphs.Length < codePoints.Length) throw new ArgumentException("glyphs 容量不足", nameof(glyphs));

            using SKFont font = face.CreateFont(face.UnitsPerEm);   // 映射与字号无关
            font.GetGlyphs(codePoints, glyphs);

            if (blankGlyphIndex == 0) return;

            for (int i = 0; i < codePoints.Length; i++)
                if (glyphs[i] == 0) glyphs[i] = blankGlyphIndex;
        }

        /// <summary>
        /// 完整映射：字符串 → 字形序列 + 簇映射 + 码点信息。
        /// 这是 TextAnalyzer.GetGlyphs 在没有 shaping 时**能真实兑现的那一部分**。
        /// </summary>
        public static ShapedGlyphs MapString(LinuxFontFace face, string text, ushort blankGlyphIndex = 0)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));

            if (string.IsNullOrEmpty(text))
            {
                return new ShapedGlyphs
                {
                    GlyphIndices = Array.Empty<ushort>(),
                    ClusterMapUtf16 = Array.Empty<int>(),
                    CodePoints = Array.Empty<int>(),
                    CodePointUtf16Index = Array.Empty<int>(),
                    Utf16ToGlyph = Array.Empty<int>(),
                };
            }

            int[] codePoints = new int[text.Length];
            int[] codePointUtf16 = new int[text.Length];
            int codePointCount = DecodeUtf16(text, codePoints, codePointUtf16, out int unpaired);

            var glyphs = new ushort[codePointCount];
            MapCodePoints(face, new ReadOnlySpan<int>(codePoints, 0, codePointCount), glyphs, blankGlyphIndex);

            var clusters = new int[codePointCount];
            Array.Copy(codePointUtf16, clusters, codePointCount);

            // UTF-16 单元 → 字形下标：代理对的第二个单元指回同一个字形。
            var utf16ToGlyph = new int[text.Length];
            for (int i = 0; i < text.Length; i++) utf16ToGlyph[i] = -1;

            int notDef = 0;
            int pairs = 0;
            for (int i = 0; i < codePointCount; i++)
            {
                int start = codePointUtf16[i];
                int utf16Length = i + 1 < codePointCount
                    ? codePointUtf16[i + 1] - start
                    : text.Length - start;

                if (utf16Length == 2) pairs++;

                for (int k = 0; k < utf16Length; k++) utf16ToGlyph[start + k] = i;
                if (glyphs[i] == 0) notDef++;
            }

            return new ShapedGlyphs
            {
                GlyphIndices = glyphs,
                ClusterMapUtf16 = clusters,
                CodePoints = codePoints[0..codePointCount],
                CodePointUtf16Index = codePointUtf16[0..codePointCount],
                Utf16ToGlyph = utf16ToGlyph,
                NotDefCount = notDef,
                UnpairedSurrogateCount = unpaired,
                SurrogatePairCount = pairs,
            };
        }

        /// <summary>
        /// DWrite 的 <c>Font.HasCharacter</c> / <c>FontFace.GetArrayOfGlyphIndices</c> 语义：
        /// 码点是否有对应字形（.notdef 视为没有）。symbol 字体带 +0xF000 二次查找。
        /// </summary>
        public static bool HasCharacter(LinuxFontFace face, int codePoint)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));
            return face.GlyphForCodePoint(codePoint) != 0;
        }
    }
}
