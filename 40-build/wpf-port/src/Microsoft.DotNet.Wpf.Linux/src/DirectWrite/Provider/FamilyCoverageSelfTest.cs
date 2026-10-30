// T1c · 覆盖查询的**常设自检**（主控 2026-09-11 批准；**独立新文件**，不改 `FamilyCoverage.cs` 的既有逻辑）
// =====================================================================================
// 【它补的是哪条红】
//   T1c 报告 §5 挂着一条："**降级链 b（直读字体文件 cmap）没有运行期实证** —— `CmapQueries=0`"。
//   原因：覆盖感知回退（方案 A）当前**不在活路上**（`WrapperMapCalls=0`），所以整条查询路径
//   **一行运行期读数都没有**。本文件让那两条链可以在**命令行上被真跑一遍**并互相对照。
//
// 【它做什么】对一组取样码点，各答两次并对照：
//   a) **provider 面**：`FamilyCoverageQuery.QueryCoverage(family, cp)` 与
//      `QueryCoverage(collection, cp, out family)`（三态：Covered / NotCovered / **Unknown**）
//   b) **直读字体文件 cmap**：本文件自带的**独立第二实现**（format 4 / format 12 + TTC 面下标）
//   不一致 ⇒ 计入报告（`disagreements`）。**Unknown 一律不当成"没有"**（只在"两侧都答得出且不同"时才算不一致）。
//
// 【纪律（逐条对应主控给的四条条件）】
//   ① 独立成新文件：本文件；`FamilyCoverage.cs` 的既有逻辑**一个字节都没改**（只用它的 public 面）。
//   ② **缺省关**：`WPF_LINUX_COVERAGE_SELFTEST` 未设/空白 ⇒ `Enabled=false` ⇒ `Run()` **不产生任何输出、
//      不调用任何 provider 查询**（`Run` 直接返回，连 `FamilyCoverageQuery.Stats` 都不碰）。
//   ③ "缺省下逐字节不改变行为"：由探针 `src/Linux/build/MilBridge/tests/FamilyCoverageSelfTest/` 的
//      `SELFTEST_DEFAULT_ASSERT` 行**断言**（比较 `Enabled`、输出长度、`FamilyCoverageQuery.Stats.Queries`
//      三项在调用 `Run()` 前后的值）—— **不是文档自称**。
//   ④ 命令行 + 原始输出：见文件末尾的用法（探针两档：缺省 / 打开）。
//
// 【为什么自带的 cmap 读取器算"独立"，而不是"重复一套"】
//   它的用途**只是对照**：provider 的覆盖结论来自 Skia（`SKFont.GetGlyph`），本读取器直接解 cmap 表
//   ⇒ 两条**实现来源不同**的读数若不一致，就是必须查的信号。**它不是产品路径**（产品路径里的那份读取器
//   在 PC 侧，另经 T1c 的自检变体实测过，读数见 `src/Linux/build/MilBridge/T1c-report.md` §8.2）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>自检的一次运行结果（可被断言：<see cref="Ran"/> / <see cref="Lines"/> / <see cref="QueriesBefore"/>,<see cref="QueriesAfter"/>）。</summary>
    public sealed class FamilyCoverageSelfTestReport
    {
        /// <summary>`WPF_LINUX_COVERAGE_SELFTEST` 的原始值（&lt;未设&gt; 表示没设）。</summary>
        public string RawEnvValue { get; internal set; } = "<未设>";

        /// <summary>开关解析结果（**未设 ⇒ false**）。</summary>
        public bool Enabled { get; internal set; }

        /// <summary>是否**真的跑了**（缺省关时为 false ⇒ 没有输出、没有查询）。</summary>
        public bool Ran { get; internal set; }

        /// <summary>取样码点数 / 两条链结论不一致的个数。</summary>
        public int Samples { get; internal set; }
        public int Disagreements { get; internal set; }

        /// <summary>`FamilyCoverageQuery.Stats.Queries` 在自检前后的值（"没跑就不许变"的断言依据）。</summary>
        public long QueriesBefore { get; internal set; }
        public long QueriesAfter { get; internal set; }

        /// <summary>provider 面的**探测原文**（方法签名逐条），原样进输出。</summary>
        public string Probe { get; internal set; } = "";

        /// <summary>输出行（**缺省关时为空**；探针直接断言 `Lines.Count == 0`）。</summary>
        public List<string> Lines { get; } = new List<string>();

        public override string ToString() =>
            $"enabled={Enabled} ran={Ran} samples={Samples} disagreements={Disagreements} " +
            $"queries={QueriesBefore}->{QueriesAfter} lines={Lines.Count} raw={RawEnvValue}";
    }

    /// <summary>覆盖查询自检（常设；**缺省关**）。</summary>
    public static class FamilyCoverageSelfTest
    {
        /// <summary>开关名。**未设/空白 ⇒ 关**。</summary>
        public const string EnableEnvVar = "WPF_LINUX_COVERAGE_SELFTEST";

        private static readonly string s_rawValue = ReadRaw();
        private static readonly bool s_enabled = ParseOnOff(s_rawValue);

        /// <summary>开关是否打开（**缺省 false**）。</summary>
        public static bool Enabled => s_enabled;

        /// <summary>未设/空白 ⇒ **false**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
        public static bool ParseOnOff(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            if (v == "1") return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ReadRaw()
        {
            try { return Environment.GetEnvironmentVariable(EnableEnvVar) ?? "<未设>"; }
            catch (Exception) { return "<读不到>"; }
        }

        /// <summary>取样码点（拉丁 / 拉丁补充 / CJK / CJK 标点 / 全角 / 假名 / 谚文 / BMP 外 emoji）。</summary>
        public static readonly int[] SampleCodePoints =
        {
            0x0041,   // A
            0x00DF,   // ß
            0x4E2D,   // 中
            0x6587,   // 文
            0x3002,   // 。
            0xFF0C,   // ，
            0x3042,   // あ
            0xAC00,   // 가
            0x1F600,  // 😀
        };

        /// <summary>
        /// 跑一次自检。**缺省关 ⇒ 立刻返回且什么都不做**（不输出、不查询）。
        /// </summary>
        /// <param name="collection">要检查的字体集合（可为 null ⇒ 只做单族那半）。</param>
        /// <param name="baseFamily">"基线族"（通常是当前正在用的那个族；可为 null）。</param>
        public static FamilyCoverageSelfTestReport Run(LinuxFontCollection collection, LinuxFontFamily baseFamily)
        {
            var report = new FamilyCoverageSelfTestReport
            {
                RawEnvValue = s_rawValue,
                Enabled = s_enabled,
            };

            if (!s_enabled) return report;      // ← ② 缺省关：**不输出、不查询**（QueriesBefore/After 保持 0）

            report.Ran = true;
            report.QueriesBefore = FamilyCoverageQuery.Stats.Queries;
            report.Probe = ProbeReading();

            report.Lines.Add($"[COVERAGE_SELFTEST] enabled={s_enabled} raw={s_rawValue} " +
                             $"baseFamily=\"{baseFamily?.FamilyName ?? "<无>"}\" " +
                             $"collectionFamilies={(collection == null ? -1 : collection.FamilyCount)} " +
                             $"samples={SampleCodePoints.Length}");
            report.Lines.Add($"[COVERAGE_SELFTEST] probe: {report.Probe}");

            for (int i = 0; i < SampleCodePoints.Length; i++)
            {
                int cp = SampleCodePoints[i];
                report.Samples++;

                // a) provider 面（族级三态）
                string providerFamilyVerdict = "<无集合>";
                if (baseFamily != null)
                {
                    FamilyCoverage v = baseFamily.QueryCoverage(cp);
                    providerFamilyVerdict = v.ToString();
                }

                // b) 直读 cmap（**独立第二实现**；族级 = 任一面覆盖即覆盖）
                string cmapFamilyVerdict = VerdictToString(CmapCoverage(baseFamily, cp));

                // 集合级"谁覆盖它"：provider 面 vs 只走 cmap
                string coveringProvider = "<无集合>";
                string coveringCmap = "<无集合>";
                if (collection != null)
                {
                    LinuxFontFamily f1;
                    FamilyCoverage cv = collection.QueryCoverage(cp, out f1);
                    coveringProvider = (cv == FamilyCoverage.Covered && f1 != null)
                        ? f1.FamilyName : "<无>(" + cv + ")";

                    string f2 = CoveringFamilyByCmap(collection, cp);
                    coveringCmap = f2 ?? "<无>";
                }

                // 不一致只在"两侧都答得出、且不同"时计（**Unknown 不当成"没有"**）
                if (providerFamilyVerdict != "<无集合>" && cmapFamilyVerdict != "Unknown"
                    && providerFamilyVerdict != "Unknown" && providerFamilyVerdict != cmapFamilyVerdict)
                {
                    report.Disagreements++;
                }

                report.Lines.Add($"[COVERAGE_SELFTEST] U+{cp.ToString("X4", CultureInfo.InvariantCulture)}" +
                                 $" base:provider={providerFamilyVerdict} cmap={cmapFamilyVerdict}" +
                                 $" | 覆盖族:provider={coveringProvider} cmap={coveringCmap}");
            }

            report.QueriesAfter = FamilyCoverageQuery.Stats.Queries;
            report.Lines.Add($"[COVERAGE_SELFTEST] stats: queries={report.QueriesBefore}->{report.QueriesAfter}" +
                             $" covered={FamilyCoverageQuery.Stats.Covered}" +
                             $" notCovered={FamilyCoverageQuery.Stats.NotCovered}" +
                             $" unknown={FamilyCoverageQuery.Stats.Unknown}" +
                             $" disagreements={report.Disagreements}");

            return report;
        }

        /// <summary>把报告写出去（**缺省关时一行都不写**）。</summary>
        public static void Write(FamilyCoverageSelfTestReport report, TextWriter output)
        {
            if (report == null || output == null || !report.Ran) return;
            for (int i = 0; i < report.Lines.Count; i++) output.WriteLine(report.Lines[i]);
            output.Flush();
        }

        /// <summary>provider 能力探测原文（按**扩展方法的静态类**找；按实例方法会永远找不到 ⇒ 假降级）。</summary>
        public static string ProbeReading()
        {
            try
            {
                Type query = typeof(FamilyCoverageQuery);
                var familyCovers = query.GetMethod("Covers", new[] { typeof(LinuxFontFamily), typeof(int) });
                var familyQuery = query.GetMethod("QueryCoverage", new[] { typeof(LinuxFontFamily), typeof(int) });
                var collectionQuery = query.GetMethod("QueryCoverage",
                    new[] { typeof(LinuxFontCollection), typeof(int), typeof(LinuxFontFamily).MakeByRefType() });
                var findCovering = query.GetMethod("TryFindFamilyCovering",
                    new[] { typeof(LinuxFontCollection), typeof(int), typeof(LinuxFontFamily).MakeByRefType() });

                return "face=" + query.FullName + "@" + query.Assembly.GetName().Name
                     + " Covers(Family,int)=" + (familyCovers != null ? "有" : "无")
                     + " QueryCoverage(Family,int)=" + (familyQuery != null ? "有" : "无")
                     + " QueryCoverage(Collection,int,out Family)=" + (collectionQuery != null ? "有" : "无")
                     + " TryFindFamilyCovering=" + (findCovering != null ? "有" : "无");
            }
            catch (Exception e)
            {
                return "探测抛 " + e.GetType().Name + ": " + e.Message;
            }
        }

        // =================================================================================
        //  独立第二实现：直读字体文件 cmap（format 4 / format 12；TTC 按面下标）
        // =================================================================================

        private static string VerdictToString(bool? covered) =>
            covered == null ? "Unknown" : (covered.Value ? "Covered" : "NotCovered");

        /// <summary>族级覆盖：**任一面**覆盖即覆盖；一个面都读不了 ⇒ null（Unknown，不当成"没有"）。</summary>
        private static bool? CmapCoverage(LinuxFontFamily family, int codePoint)
        {
            if (family == null) return null;

            IReadOnlyList<FontFaceEntry> faces = family.Faces;
            if (faces == null || faces.Count == 0) return null;

            bool anyAnswered = false;
            for (int i = 0; i < faces.Count; i++)
            {
                FontFaceEntry face = faces[i];
                if (face == null || string.IsNullOrEmpty(face.FilePath)) continue;

                CmapTable cmap = CmapTable.Load(face.FilePath, face.FaceIndex);
                if (cmap == null) continue;

                anyAnswered = true;
                if (cmap.Covers(codePoint)) return true;
            }

            return anyAnswered ? (bool?)false : null;
        }

        /// <summary>集合级：按集合既有族序找第一个 cmap 覆盖它的族（确定性）。</summary>
        private static string CoveringFamilyByCmap(LinuxFontCollection collection, int codePoint)
        {
            if (collection == null) return null;

            for (int i = 0; i < collection.FamilyCount; i++)
            {
                LinuxFontFamily family = collection[i];
                if (family == null) continue;
                if (CmapCoverage(family, codePoint) == true) return family.FamilyName;
            }

            return null;
        }

        /// <summary>最小 SFNT/TTC cmap 读取（format 4 / 12）。读不了 ⇒ null（**不抛**）。</summary>
        private sealed class CmapTable
        {
            private int _format;
            private int[] _starts, _ends, _deltas, _rangeOffsets, _startGlyphs;
            private int _rangeOffsetBase;
            private byte[] _data;

            private static readonly Dictionary<string, CmapTable> Cache =
                new Dictionary<string, CmapTable>(StringComparer.Ordinal);

            internal static CmapTable Load(string path, int faceIndex)
            {
                string key = path + "|" + faceIndex.ToString(CultureInfo.InvariantCulture);
                lock (Cache)
                {
                    CmapTable cached;
                    if (Cache.TryGetValue(key, out cached)) return cached;

                    CmapTable table = null;
                    try
                    {
                        if (File.Exists(path)) table = Parse(File.ReadAllBytes(path), faceIndex);
                    }
                    catch (Exception)
                    {
                        table = null;
                    }

                    if (Cache.Count < 64) Cache[key] = table;      // 有界；满了就不再入表（不影响正确性）
                    return table;
                }
            }

            internal bool Covers(int codePoint)
            {
                if (_starts == null || _starts.Length == 0) return false;

                int lo = 0, hi = _starts.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (codePoint < _starts[mid]) hi = mid - 1;
                    else if (codePoint > _ends[mid]) lo = mid + 1;
                    else
                    {
                        if (_format == 12)
                        {
                            long glyph = (long)_startGlyphs[mid] + (codePoint - _starts[mid]);
                            return glyph != 0;
                        }
                        return GlyphId(codePoint, mid) != 0;
                    }
                }

                return false;
            }

            private int GlyphId(int codePoint, int segment)
            {
                if (_deltas == null || _data == null) return 0;

                int delta = _deltas[segment];
                if (_rangeOffsets[segment] == 0) return (codePoint + delta) & 0xFFFF;

                int index = _rangeOffsetBase + segment * 2 + _rangeOffsets[segment]
                          + (codePoint - _starts[segment]) * 2;
                if (index < 0 || index + 2 > _data.Length) return 0;

                int glyph = (_data[index] << 8) | _data[index + 1];
                return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
            }

            private static CmapTable Parse(byte[] data, int faceIndex)
            {
                if (data == null || data.Length < 12) return null;

                int sfnt = 0;
                if (data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f')
                {
                    int numFonts = ReadU32(data, 8);
                    if (numFonts <= 0) return null;
                    if (faceIndex < 0 || faceIndex >= numFonts) faceIndex = 0;
                    sfnt = ReadU32(data, 12 + 4 * faceIndex);
                    if (sfnt <= 0 || sfnt + 12 > data.Length) return null;
                }

                bool ok = (data[sfnt] == 0 && data[sfnt + 1] == 1 && data[sfnt + 2] == 0 && data[sfnt + 3] == 0)
                       || (data[sfnt] == (byte)'O' && data[sfnt + 1] == (byte)'T'
                           && data[sfnt + 2] == (byte)'T' && data[sfnt + 3] == (byte)'O');
                if (!ok) return null;

                int numTables = ReadU16(data, sfnt + 4);
                int cmapOffset = -1;
                for (int i = 0; i < numTables; i++)
                {
                    int rec = sfnt + 12 + 16 * i;
                    if (rec + 16 > data.Length) break;
                    if (data[rec] == (byte)'c' && data[rec + 1] == (byte)'m'
                        && data[rec + 2] == (byte)'a' && data[rec + 3] == (byte)'p')
                    {
                        cmapOffset = ReadU32(data, rec + 8);
                        break;
                    }
                }

                if (cmapOffset <= 0 || cmapOffset + 4 > data.Length) return null;

                int subTables = ReadU16(data, cmapOffset + 2);
                int best12 = -1, best4 = -1, rank12 = -1, rank4 = -1;

                for (int i = 0; i < subTables; i++)
                {
                    int rec = cmapOffset + 4 + 8 * i;
                    if (rec + 8 > data.Length) break;

                    int platform = ReadU16(data, rec);
                    int encoding = ReadU16(data, rec + 2);
                    int sub = cmapOffset + ReadU32(data, rec + 4);
                    if (sub <= 0 || sub + 2 > data.Length) continue;

                    int format = ReadU16(data, sub);
                    if (format == 12)
                    {
                        int rank = (platform == 3 && encoding == 10) ? 4
                                 : (platform == 0 && (encoding == 4 || encoding == 6)) ? 3
                                 : (platform == 3 && encoding == 1) ? 2
                                 : (platform == 0) ? 1 : 0;
                        if (rank > rank12) { rank12 = rank; best12 = sub; }
                    }
                    else if (format == 4)
                    {
                        int rank = (platform == 3 && encoding == 1) ? 4
                                 : (platform == 0 && encoding == 3) ? 3
                                 : (platform == 0) ? 2
                                 : (platform == 3) ? 1 : 0;
                        if (rank > rank4) { rank4 = rank; best4 = sub; }
                    }
                }

                if (best12 >= 0) return Parse12(data, best12);
                if (best4 >= 0) return Parse4(data, best4);
                return null;
            }

            private static CmapTable Parse12(byte[] data, int offset)
            {
                if (offset + 16 > data.Length) return null;

                int groups = ReadU32(data, offset + 12);
                if (groups <= 0 || groups > 0x100000) return null;
                if (offset + 16 + groups * 12 > data.Length) return null;

                var t = new CmapTable { _format = 12, _data = data };
                t._starts = new int[groups];
                t._ends = new int[groups];
                t._startGlyphs = new int[groups];

                for (int i = 0; i < groups; i++)
                {
                    int g = offset + 16 + 12 * i;
                    t._starts[i] = ReadU32(data, g);
                    t._ends[i] = ReadU32(data, g + 4);
                    t._startGlyphs[i] = ReadU32(data, g + 8);
                }

                return t;
            }

            private static CmapTable Parse4(byte[] data, int offset)
            {
                if (offset + 14 > data.Length) return null;

                int length = ReadU16(data, offset + 2);
                int segCountX2 = ReadU16(data, offset + 6);
                int segCount = segCountX2 / 2;

                if (segCount <= 0 || segCount > 0x8000) return null;
                if (offset + length > data.Length) return null;
                if (offset + 16 + segCount * 8 > data.Length) return null;

                int endBase = offset + 14;
                int startBase = endBase + segCountX2 + 2;
                int deltaBase = startBase + segCountX2;
                int rangeBase = deltaBase + segCountX2;

                var t = new CmapTable { _format = 4, _data = data, _rangeOffsetBase = rangeBase };
                t._starts = new int[segCount];
                t._ends = new int[segCount];
                t._deltas = new int[segCount];
                t._rangeOffsets = new int[segCount];

                for (int i = 0; i < segCount; i++)
                {
                    t._ends[i] = ReadU16(data, endBase + i * 2);
                    t._starts[i] = ReadU16(data, startBase + i * 2);
                    t._deltas[i] = (short)ReadU16(data, deltaBase + i * 2);
                    t._rangeOffsets[i] = ReadU16(data, rangeBase + i * 2);
                }

                return t;
            }

            private static int ReadU16(byte[] data, int index) => (data[index] << 8) | data[index + 1];

            private static int ReadU32(byte[] data, int index) =>
                (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
        }
    }
}
