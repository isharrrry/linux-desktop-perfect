// T2 · Phase 1 —— 摘要生成器（**同一个源文件**被探针与测试各编一份）
// =====================================================================================
// 【为什么要一份源码两边编，而不是让测试调探针的 API】
//   跨进程一致性断言的语义是"**同一段计算**在两个独立进程里给出同一个结果"。
//   如果测试进程用的是另一份实现（哪怕只是重构过的拷贝），那比对的是两份代码，
//   而不是"同一份代码在两次运行里是否稳定"。
//   所以 digest 的生成逻辑放在这个文件里，Probe 与 Tests 两个 csproj **各自编它**
//   （Tests 用 <Compile Include="ProbeDigest.cs">，见 Tests.csproj）。
//   这与仓库既有做法一致：src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 也是"一份源码编进两个程序集"。
//
// 【摘要里放什么，为什么】
//   目标是"同一个输入永远出同一个输出"。所以摘要必须覆盖 provider 的**每一条输出通路**：
//     · 字体身份（族名/字重/拉伸/斜体/upem/字形数/表清单）
//     · 10 个度量字段 + 来源串
//     · 每张 OpenType 表的 SHA-256（前 16 位）—— 表字节本身也必须是确定的
//     · 前 512 个字形 + 前 64 个字形的设计度量
//     · 语料串的字形索引 / 簇映射 / notdef 计数
//     · 两种排版模式（Ideal / Display）× 3 个字号 × 2 个 ppdip 的 advance 与总长
//     · name 表信息（版本、版权、描述）与文件分析结果
//     · 匹配规则在 10 个字重 × 3 种 style 上的选择结果
//     · 20000 字符长串的字形统计
//
// 【摘要里绝对不能出现什么】
//   · 绝对路径（不同工作目录会变）→ 只放文件名
//   · 对象哈希码 / Dictionary 枚举顺序 → 一律排序后输出
//   · 时间、Guid、进程 id、环境变量
//   · 浮点数的默认 ToString()（受当前文化影响）→ 统一用 R + InvariantCulture
//
// 【为什么用 SHA-256 而不是直接比字符串】
//   摘要文本有几百行，直接比会打印一屏；哈希一行就能断言、能写进报告、能当基线存档。
//   但 <see cref="Build"/> 会同时返回完整摘要，出问题时可逐行 diff。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MS.Internal.Text.TextInterface.Linux.Probe
{
    /// <summary>确定性摘要的生成与哈希。</summary>
    public static class ProbeDigest
    {
        /// <summary>确定性语料。每一条都对应一类必须稳定的输入。</summary>
        public static readonly string[] Corpus =
        {
            "",                                     // 空串
            "Hello WPF on Linux",                   // 常规 ASCII
            "AVATAR To Wa",                         // 潜在字距对（本轮无 kerning → 必须等于 hmtx 之和）
            "iiiiiiiiii WWWWWWWWWW",                // 宽度差异大的对照
            " ",                                    // 单个空格
            "\t\n\r",                               // 其它空白
            "\u00e9\u00e8\u00ea\u00eb",             // Latin-1 重音
            "\u4e2d\u6587\u5b57\u4f53",             // CJK：Noto Sans 未覆盖 → .notdef
            "\u041f\u0440\u0438\u0432\u0435\u0442",  // 西里尔（覆盖）
            "\u03b1\u03b2\u03b3\u03b4",             // 希腊（覆盖）
            "\U00010780\U00010781",                 // 增补平面：**真值**代理对（本字体覆盖 88 个码点）
            "A\U0001F600x",                         // 增补平面：未覆盖（emoji）
            "A\uD800x",                             // 孤立高代理（Skia 字符串入口会整串丢）
            "A\uDC00x",                             // 孤立低代理
            "\uD83D\uDE00\uD83D\uDE00",             // 两个连续代理对（都不覆盖）
            "e\u0301",                              // 组合字符（无 shaping → 两个字形）
            "The quick brown fox jumps over the lazy dog 0123456789",
        };

        /// <summary>超长串长度：覆盖"长串不退化/不截断"这一类边界。</summary>
        public const int LongStringLength = 20000;

        /// <summary>
        /// WPF 传给 TextAnalyzer.GetGlyphPlacements 的 scalingFactor 真值：
        /// LineServices.cs:1290 `DefaultRealToIdeal = 28800.0/96` = 300，
        /// TextFormatterImp.ToIdeal = 1/DefaultIdealToReal = 300。
        /// 用 1 会把 Ideal 与 Display 的取整差异抹平，测不出 Display 的像素吸附。
        /// </summary>
        public const double WpfIdealToReal = 300.0;

        /// <summary>生成完整摘要文本。</summary>
        public static string Build(string fontDir, List<string> facts = null)
        {
            var sb = new StringBuilder();
            facts?.Add("# 探针字体目录: " + fontDir);

            // T1/M7c5：探针是**原始语料**的诊断（它逐表打印 TABLES/TABLE len/sha），
            // 因此**显式旁路**运行期剥离 —— 否则 T2 已提交的
            // artifacts/probe-summary.txt 与 probe-digest.txt 会随默认值变化而失真。
            // 剥离后的行为由 FontLayoutStrippingTests 与 WiringSmoke 负责取证。
            using var collection = LinuxFontCollection.FromDirectory(fontDir, recurse: false, stripLayout: false);

            Line(sb, "VERSION provider=1 corpus=" + Corpus.Length + " longString=" + LongStringLength);
            Line(sb, "COLLECTION families=" + collection.FamilyCount + " faces=" + collection.Entries.Count
                      + " files=" + string.Join(",", collection.FileNames)
                      + " warnings=" + collection.LoadWarnings.Count);

            facts?.Add("# 集合: " + collection);

            foreach (LinuxFontFamily family in collection.Families)
            {
                var faceDescriptions = family.Faces
                    .Select(f => $"{(f.FilePath == null ? "<bytes>" : Path.GetFileName(f.FilePath))}#{f.FaceIndex}:w{f.Weight}/d{f.Width}/s{f.Slant}")
                    .ToArray();

                Line(sb, $"FAMILY name={family.FamilyName} faces={family.Faces.Count} [{string.Join(" ", faceDescriptions)}]"
                          + $" ordinal={family.OrdinalName} physical={family.IsPhysical} composite={family.IsComposite}");

                facts?.Add($"族 {family.FamilyName}: {family.Faces.Count} 面 [{string.Join(" ", faceDescriptions)}]");
            }

            foreach (LinuxFontFamily family in collection.Families)
                foreach (LinuxFont font in family)
                    EmitFace(sb, facts, font);

            // 匹配规则：WPF 的 10 个字重 × 3 种 style，两条规则的选择结果。
            foreach (int weight in new[] { 100, 200, 300, 400, 500, 600, 700, 800, 900, 950 })
            {
                foreach (int style in new[] { 0, 1, 2 })
                {
                    foreach (LinuxFontFamily family in collection.Families)
                    {
                        if (family.Count == 0) continue;

                        LinuxFont distance = family.GetFirstMatchingFont(weight, 5, style, FontMatchingRule.Distance);
                        LinuxFont bucket = family.GetFirstMatchingFont(weight, 5, style, FontMatchingRule.BoldBucket);

                        Line(sb, $"MATCH family={family.FamilyName} w={weight} style={style} "
                                  + $"distance=w{distance.Weight}/s{distance.Style} bucket=w{bucket.Weight}/s{bucket.Style}");
                    }
                }
            }

            // 长串（跨字形边界）。
            foreach (LinuxFontFamily family in collection.Families)
            {
                if (family.Count == 0) continue;

                LinuxFont font = family.GetFirstMatchingFont(400, 5, 0, FontMatchingRule.Distance);
                LinuxFontFace face = font.GetFontFace();
                try
                {
                    string longText = BuildLongString();
                    ShapedGlyphs shaped = GlyphMapper.MapString(face, longText);
                    Line(sb, $"LONG family={family.FamilyName} chars={longText.Length} glyphs={shaped.GlyphIndices.Length} "
                              + $"notdef={shaped.NotDefCount} pairs={shaped.SurrogatePairCount} "
                              + $"first={string.Join(",", shaped.GlyphIndices.Take(16))} "
                              + $"last={string.Join(",", shaped.GlyphIndices.Skip(shaped.GlyphIndices.Length - 16))}");

                    facts?.Add($"长串 {longText.Length} 字符 → {shaped.GlyphIndices.Length} 字形（notdef={shaped.NotDefCount}）");
                }
                finally
                {
                    face.Release();
                }
            }

            return sb.ToString();
        }

        /// <summary>摘要的 SHA-256（64 位小写 hex）。</summary>
        public static string Digest(string fontDir) => Sha256(Build(fontDir));

        public static string BuildLongString()
        {
            const string unit = "WPF-Linux abcXYZ 0123 \u00e9\u03b1\u0416";
            var sb = new StringBuilder(LongStringLength + unit.Length);
            while (sb.Length < LongStringLength) sb.Append(unit);
            return sb.ToString(0, LongStringLength);
        }

        private static void EmitFace(StringBuilder sb, List<string> facts, LinuxFont font)
        {
            LinuxFontFace face = font.GetFontFace();
            try
            {
                string fileName = font.FaceEntry.FilePath == null ? "<bytes>" : Path.GetFileName(font.FaceEntry.FilePath);

                Line(sb, $"FACE file={fileName} face={face.FaceIndex} family={font.FamilyName} sub={font.FaceEntry.SubFamilyName} "
                          + $"ps={font.FaceEntry.PostScriptName} w={font.Weight} d={font.Stretch} s={font.Style} "
                          + $"kind={face.Type} symbol={face.IsSymbolFont} upem={face.UnitsPerEm} glyphs={face.GlyphCount} sim=0x{face.SimulationFlags:X}");

                FontMetricsData metrics = face.Metrics;
                Line(sb, "METRICS " + metrics);
                Line(sb, "PROVENANCE " + metrics.Provenance);
                Line(sb, "BASELINE " + Num(metrics.Baseline) + " LINESPACING " + Num(metrics.LineSpacing));

                facts?.Add($"{fileName}#{face.FaceIndex}: {font.FamilyName} w={font.Weight} s={font.Style} "
                           + $"upem={face.UnitsPerEm} glyphs={face.GlyphCount}");
                facts?.Add($"    度量 {metrics}");
                facts?.Add($"    来源 {metrics.Provenance}");

                var tags = face.OpenType.TableTagsPresent
                    .Select(TableTags.ToString)
                    .OrderBy(t => t, StringComparer.Ordinal)
                    .ToArray();
                Line(sb, "TABLES " + string.Join(",", tags));

                foreach (string tag in tags)
                {
                    if (!face.TryGetFontTable(TableTags.Make(tag), out byte[] data) || data == null) continue;
                    Line(sb, $"TABLE {tag} len={data.Length} sha={Sha256(data).Substring(0, 16)}");
                }

                if (face.ReadFontEmbeddingRights(out ushort fsType))
                    Line(sb, "FSTYPE 0x" + fsType.ToString("X4", CultureInfo.InvariantCulture));

                Line(sb, "VERSION font=" + Num(font.Version) + " headRevision=" + Num(face.OpenType.FontRevision));

                var fileAnalysis = GetFileAnalysis(font);
                Line(sb, $"FILEKIND file={fileAnalysis.FileKind} face={fileAnalysis.FaceKind} faces={fileAnalysis.NumberOfFaces}");

                Line(sb, "INFO copyright=" + Escape(Truncate(SafeInfo(font, InformationalStrings.CopyrightNotice), 48))
                          + " vendor=" + Escape(Truncate(SafeInfo(font, InformationalStrings.FontVendorURL), 48))
                          + " desc=" + Escape(Truncate(SafeInfo(font, InformationalStrings.Description), 48)));

                // 前 512 个字形 + 前 64 个字形的设计度量。
                int advanceCount = Math.Min(face.GlyphCount, 512);
                var advanceParts = new List<string>(advanceCount);
                for (ushort g = 0; g < advanceCount; g++)
                    advanceParts.Add(face.DesignAdvance(g).ToString(CultureInfo.InvariantCulture));
                Line(sb, "ADVANCES512 " + string.Join(",", advanceParts));

                var metricParts = new List<string>();
                for (ushort g = 0; g < Math.Min(face.GlyphCount, 64); g++)
                    metricParts.Add(g + ":" + face.DesignMetricsOf(g));
                Line(sb, "DESIGNMETRICS64 " + string.Join(" ", metricParts));

                Line(sb, "CMAP supplementary=" + face.OpenType.CountSupplementaryCodePoints()
                          + " firstSupplementary=U+" + face.OpenType.FindFirstSupplementaryCodePoint().ToString("X", CultureInfo.InvariantCulture));

                facts?.Add($"    表 {tags.Length} 张；增补平面码点 {face.OpenType.CountSupplementaryCodePoints()} 个；"
                           + $"fsType=0x{fsType:X4}；font.Version={Num(font.Version)}；文件分析={fileAnalysis.FileKind}/{fileAnalysis.FaceKind}/{fileAnalysis.NumberOfFaces}");

                foreach (string text in Corpus)
                {
                    ShapedGlyphs shaped = GlyphMapper.MapString(face, text);
                    Line(sb, $"MAP text={Escape(text)} glyphs=[{string.Join(",", shaped.GlyphIndices)}] "
                              + $"clusters=[{string.Join(",", shaped.ClusterMapUtf16)}] notdef={shaped.NotDefCount} "
                              + $"lone={shaped.UnpairedSurrogateCount} pairs={shaped.SurrogatePairCount}");
                }

                foreach (double size in new[] { 12.0, 13.333, 16.0 })
                {
                    foreach (double ppdip in new[] { 1.0, 1.25 })
                    {
                        foreach (string text in new[] { "Hello WPF on Linux", "AVATAR", "\U00010780\U00010781", "  " })
                        {
                            ShapedGlyphs shaped = GlyphMapper.MapString(face, text);
                            GlyphPlacementData ideal = GlyphPositioner.Place(
                                face, shaped.GlyphIndices, size, WpfIdealToReal, false, useDisplayNatural: true, pixelsPerDip: ppdip);
                            GlyphPlacementData display = GlyphPositioner.Place(
                                face, shaped.GlyphIndices, size, WpfIdealToReal, false, useDisplayNatural: false, pixelsPerDip: ppdip);

                            Line(sb, $"PLACE size={Num(size)} ppdip={Num(ppdip)} text={Escape(text)} "
                                      + $"ideal=[{string.Join(",", ideal.Advances)}]/{ideal.TotalAdvance} "
                                      + $"display=[{string.Join(",", display.Advances)}]/{display.TotalAdvance} "
                                      + $"idealExact=[{string.Join(",", ideal.AdvancesExact.Select(v => Num(Math.Round(v, 6))))}] "
                                      + $"displayExact=[{string.Join(",", display.AdvancesExact.Select(v => Num(Math.Round(v, 6))))}]");
                        }
                    }
                }
            }
            finally
            {
                face.Release();
            }
        }

        private static FontFileAnalysis GetFileAnalysis(LinuxFont font)
        {
            LinuxFontFace face = font.GetFontFace();
            try
            {
                LinuxFontFile file = face.GetFileZero();
                return file.Analyze(out FontFileAnalysis analysis) ? analysis : default;
            }
            finally
            {
                face.Release();
            }
        }

        private static string SafeInfo(LinuxFont font, int id) =>
            font.GetInformationalStrings(id, out LocalizedStringsData strings) && strings.Count > 0
                ? strings.ValuesArray[0]
                : "";

        private static string Truncate(string value, int length) =>
            string.IsNullOrEmpty(value) ? "" : (value.Length <= length ? value : value.Substring(0, length));

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                if (c < 32 || c == '"' || c == '\\' || c > 126)
                    sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>数字一律用 R + 不变文化（默认 ToString 受当前文化影响，会跨进程/跨机器漂移）。</summary>
        private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');

        public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

        public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
