// T2 · Phase 1 —— OpenType 表的直读层（Skia 之外的第二条独立数据通路）
// =====================================================================================
// 【为什么要有这一层：不能"只问 Skia"】
//   ManagedSurface 里的 [PNSE] 成员要变成真实现，最容易滑向的做法是"Skia 给什么就
//   返回什么"。那会有一个致命问题：**没有 oracle**。如果 Skia 某天改了某个度量的
//   口径（或者把 underlinePosition 的符号约定换一下），我们的实现会跟着悄悄变，
//   而测试全绿——因为测试也是问 Skia。
//
//   所以这一层的职责是：**绕过 Skia，直接从字体文件字节里把权威值读出来**。
//     · 度量   → head.unitsPerEm / hhea.ascender,descender,lineGap / OS-2.sTypo* /
//                OS-2.usWin* / OS-2.sxHeight,sCapHeight,fsSelection / post.underline*
//     · 字形   → cmap（format 4 覆盖 BMP，format 12 覆盖增补平面）→ glyph id
//     · 步进   → hmtx.advanceWidth
//     · 轮廓盒 → loca + glyf 的 xMin/yMin/xMax/yMax（GetDesignGlyphMetrics 的原始数据）
//     · 名字   → name 表（family/subfamily/version/copyright…，本地化）
//
//   两条通路的关系（测试里逐项断言，见 Tests/OpenTypeOracleTests.cs）：
//     我们的实现  ← 以本层为权威（因为它就是 DWrite 读的东西）
//                 ← 与 Skia 通路逐项比对（同源一致性自证）
//                 ← 与直接读文件字节比对（同上，但连 Skia 的表访问都不经过）
//
// 【统一大端】SFNT 全部是 big-endian；本文件所有读取都走 U16/S16/U32/… 辅助函数，
//   不允许出现 BitConverter（它跟随宿主字节序，是跨平台移植的经典坑）。
//
// 【TTC】字体集合（'ttcf'）里多个字体共享一份表目录，本层支持按 faceIndex 取。
//   Noto Sans 的四个文件都不是 TTC，因此 TTC 分支只有单元测试覆盖（构造合成数据），
//   这一点在 REPORT.md 的"未验"清单里已登记。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>OpenType/SFNT 表标签（值 = DWRITE_MAKE_OPENTYPE_TAG 的语义）。</summary>
    public static class TableTags
    {
        public static uint Make(string tag)
        {
            if (tag == null || tag.Length != 4) throw new ArgumentException("OpenType 标签必须是 4 个字符", nameof(tag));
            return Make(tag[0], tag[1], tag[2], tag[3]);
        }

        public static uint Make(char a, char b, char c, char d) =>
            ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

        public static string ToString(uint tag) =>
            new string(new[] { (char)((tag >> 24) & 0xFF), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF) });

        public static readonly uint Cmap = Make("cmap");
        public static readonly uint Head = Make("head");
        public static readonly uint Hhea = Make("hhea");
        public static readonly uint Hmtx = Make("hmtx");
        public static readonly uint Maxp = Make("maxp");
        public static readonly uint Name = Make("name");
        public static readonly uint OS2 = Make("OS/2");
        public static readonly uint Post = Make("post");
        public static readonly uint Loca = Make("loca");
        public static readonly uint Glyf = Make("glyf");
        public static readonly uint Vhea = Make("vhea");
        public static readonly uint Vmtx = Make("vmtx");
        public static readonly uint Gsub = Make("GSUB");
        public static readonly uint Gpos = Make("GPOS");
        public static readonly uint Gdef = Make("GDEF");
        public static readonly uint Kern = Make("kern");
        public static readonly uint Cff = Make("CFF ");
        public static readonly uint SfntTrueType = 0x00010000u;
        public static readonly uint SfntTrue = Make("true");
        public static readonly uint SfntTyp1 = Make("typ1");
        public static readonly uint SfntOtto = Make("OTTO");
        public static readonly uint SfntTtcf = Make("ttcf");
    }

    /// <summary>name 表里的一条记录（已解码为字符串）。</summary>
    public readonly struct NameRecord
    {
        public NameRecord(ushort platformId, ushort encodingId, ushort languageId, ushort nameId, string value)
        {
            PlatformId = platformId;
            EncodingId = encodingId;
            LanguageId = languageId;
            NameId = nameId;
            Value = value;
        }

        public ushort PlatformId { get; }
        public ushort EncodingId { get; }
        public ushort LanguageId { get; }
        public ushort NameId { get; }
        public string Value { get; }

        public CultureInfo Culture
        {
            get
            {
                // Windows 平台（3）的 languageID 就是 LCID；Mac 平台（1）的 languageID 是
                // Mac 语言码，和 LCID 不是一套，这里不硬凑，统一按不变文化处理。
                if (PlatformId != 3) return CultureInfo.InvariantCulture;
                try { return new CultureInfo(LanguageId); }
                catch (Exception) { return CultureInfo.InvariantCulture; }
            }
        }

        public override string ToString() => $"nameId={NameId} plat={PlatformId}/{EncodingId} lang=0x{LanguageId:X4} \"{Value}\"";
    }

    /// <summary>
    /// 一个字体面的 OpenType 原始数据（表目录 + 现成的度量值）。
    /// 构造方式有两种，**互相独立**：
    ///   · <see cref="FromTypeface"/>：经 Skia 的表访问（TryGetTableData）；
    ///   · <see cref="FromSfnt"/>：直接解析调用方给的字节（测试里的"绕过 Skia"通路）。
    /// </summary>
    public sealed class OpenTypeFontData
    {
        private readonly Dictionary<uint, byte[]> _tables = new Dictionary<uint, byte[]>();
        private readonly List<NameRecord> _names = new List<NameRecord>();
        private int[] _advanceWidths;      // hmtx，展开到每个字形
        private int[] _leftSideBearings;   // hmtx，展开到每个字形
        private readonly List<uint> _tableTags = new List<uint>();
        private byte[] _locaTable;
        private byte[] _glyfTable;
        private bool _boundsResolved;
        private (bool HasBounds, short XMin, short YMin, short XMax, short YMax)[] _bounds;

        /// <summary>SFNT 的版本标签：0x00010000 / 'true' / 'OTTO' / 'ttcf' / 'typ1'。</summary>
        public uint SfntVersion { get; private set; }

        /// <summary>字体集合里的下标（非 TTC 恒为 0）。</summary>
        public int FaceIndex { get; private set; }

        /// <summary>TTC 里的字体个数；非 TTC 为 1。</summary>
        public int FaceCount { get; private set; } = 1;

        public ushort UnitsPerEm { get; private set; }
        public ushort NumGlyphs { get; private set; }
        public ushort NumberOfHMetrics { get; private set; }
        public short IndexToLocFormat { get; private set; }

        /// <summary>head.fontRevision（16.16 Fixed）。</summary>
        public double FontRevision { get; private set; }

        /// <summary>hhea：字体设计者建议的行盒（DWrite 的 ascent/descent/lineGap 走这三个）。</summary>
        public short HheaAscender { get; private set; }
        public short HheaDescender { get; private set; }
        public short HheaLineGap { get; private set; }

        /// <summary>OS/2 v0+：Windows 的 usWinAscent/usWinDescent（裁剪框，不等于行盒）。</summary>
        public ushort WinAscent { get; private set; }
        public ushort WinDescent { get; private set; }

        /// <summary>OS/2：Typographic 度量（USE_TYPO_METRICS 置位时才是权威行盒）。</summary>
        public short TypoAscender { get; private set; }
        public short TypoDescender { get; private set; }
        public short TypoLineGap { get; private set; }

        /// <summary>OS/2.fsSelection（bit 0=ITALIC, 5=BOLD, 6=REGULAR, 7=USE_TYPO_METRICS）。</summary>
        public ushort FsSelection { get; private set; }

        /// <summary>OS/2.fsType：嵌入权限位（bit 1 = restricted license embedding）。</summary>
        public ushort FsType { get; private set; }

        public ushort Os2Version { get; private set; }

        /// <summary>OS/2.usWeightClass / usWidthClass（100..950 / 1..9）。</summary>
        public ushort WeightClass { get; private set; }
        public ushort WidthClass { get; private set; }

        /// <summary>OS/2 v2+：sxHeight / sCapHeight；缺失时为 0。</summary>
        public short XHeight { get; private set; }
        public short CapHeight { get; private set; }

        /// <summary>OS/2：删除线位置/粗细。位置约定：**正数 = 基线之上**。</summary>
        public short StrikeoutPosition { get; private set; }
        public short StrikeoutSize { get; private set; }

        /// <summary>post：下划线位置/粗细。位置约定：**负数 = 基线之下**。</summary>
        public short UnderlinePosition { get; private set; }
        public short UnderlineThickness { get; private set; }

        /// <summary>cmap 里是否存在 (3,0) 符号子表 —— DWrite 的 IsSymbolFont 判据。</summary>
        public bool HasSymbolCmap { get; private set; }

        /// <summary>是否存在 vhea+vmtx（竖排度量）。没有时 DWrite 会合成，见 GlyphMetricsFor。</summary>
        public bool HasVerticalMetrics => _tables.ContainsKey(TableTags.Vhea) && _tables.ContainsKey(TableTags.Vmtx);

        public IReadOnlyList<uint> TableTagsPresent => _tableTags;

        private OpenTypeFontData() { }

        // ---------------------------------------------------------------------------------
        //  构造：经 Skia
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 经 Skia 的 <c>SKTypeface.TryGetTableData</c> 取表。
        /// 表集合与字节内容与 <see cref="FromSfnt"/> 必须完全一致（测试逐表断言）。
        /// </summary>
        public static OpenTypeFontData FromTypeface(SkiaSharp.SKTypeface typeface)
        {
            if (typeface == null) throw new ArgumentNullException(nameof(typeface));

            var data = new OpenTypeFontData();
            foreach (uint tag in typeface.GetTableTags()) data._tableTags.Add(tag);
            data._tableTags.Sort();

            foreach (uint tag in data._tableTags)
            {
                if (typeface.TryGetTableData(tag, out byte[] bytes) && bytes != null)
                    data._tables[tag] = bytes;
            }

            // SFNT 版本：Skia 不直接暴露，从 head 表长度/存在性与 CFF 表推断。
            data.SfntVersion = data._tables.ContainsKey(TableTags.Cff)
                ? TableTags.SfntOtto
                : TableTags.SfntTrueType;

            data.ParseCommon();
            return data;
        }

        // ---------------------------------------------------------------------------------
        //  构造：直接解析字节（不经过 Skia）
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 解析一份 SFNT/TTC 字节流。TTC 用 <paramref name="faceIndex"/> 选面。
        /// 这是"绕过 Skia"的独立通路：OpenTypeOracleTests 用它交叉验证 Skia 的表访问。
        /// </summary>
        public static OpenTypeFontData FromSfnt(byte[] sfnt, int faceIndex = 0)
        {
            if (sfnt == null) throw new ArgumentNullException(nameof(sfnt));
            if (sfnt.Length < 12) throw new InvalidOperationException("字体数据太短（不足 SFNT header）");

            var data = new OpenTypeFontData();
            uint version = U32(sfnt, 0);
            data.SfntVersion = version;
            data.FaceIndex = 0;

            int directory = 0;
            if (version == TableTags.SfntTtcf)
            {
                uint numFonts = U32(sfnt, 8);
                data.FaceCount = checked((int)numFonts);
                if (faceIndex < 0 || faceIndex >= data.FaceCount)
                    throw new ArgumentOutOfRangeException(nameof(faceIndex), $"TTC 只有 {data.FaceCount} 个面，请求了 {faceIndex}");

                data.FaceIndex = faceIndex;
                directory = checked((int)U32(sfnt, 12 + faceIndex * 4));
                data.SfntVersion = U32(sfnt, directory);
            }

            int numTables = U16(sfnt, directory + 4);
            for (int i = 0; i < numTables; i++)
            {
                int rec = directory + 12 + i * 16;
                if (rec + 16 > sfnt.Length) throw new InvalidOperationException("表目录越界");
                uint tag = U32(sfnt, rec);
                uint offset = U32(sfnt, rec + 8);
                uint length = U32(sfnt, rec + 12);
                if (offset + length > (uint)sfnt.Length)
                    throw new InvalidOperationException($"表 {TableTags.ToString(tag)} 越界（offset={offset} length={length} fileSize={sfnt.Length}）");

                var bytes = new byte[length];
                Array.Copy(sfnt, (int)offset, bytes, 0, (int)length);
                data._tables[tag] = bytes;
                data._tableTags.Add(tag);
            }

            data._tableTags.Sort();
            data.ParseCommon();
            return data;
        }

        // ---------------------------------------------------------------------------------
        //  公共解析
        // ---------------------------------------------------------------------------------

        private void ParseCommon()
        {
            byte[] head = Get(TableTags.Head);
            if (head == null || head.Length < 54) throw new InvalidOperationException("缺 head 表（不是可用的 TrueType/OpenType 字体）");

            UnitsPerEm = U16(head, 18);
            FontRevision = U32(head, 4) / 65536.0;
            IndexToLocFormat = S16(head, 50);

            byte[] maxp = Get(TableTags.Maxp);
            NumGlyphs = maxp != null && maxp.Length >= 6 ? U16(maxp, 4) : (ushort)0;

            byte[] hhea = Get(TableTags.Hhea);
            if (hhea != null && hhea.Length >= 36)
            {
                HheaAscender = S16(hhea, 4);
                HheaDescender = S16(hhea, 6);
                HheaLineGap = S16(hhea, 8);
                NumberOfHMetrics = U16(hhea, 34);
            }

            byte[] os2 = Get(TableTags.OS2);
            if (os2 != null && os2.Length >= 78)
            {
                Os2Version = U16(os2, 0);
                WeightClass = U16(os2, 4);
                WidthClass = U16(os2, 6);
                FsType = U16(os2, 8);
                StrikeoutSize = S16(os2, 26);
                StrikeoutPosition = S16(os2, 28);
                FsSelection = U16(os2, 62);
                TypoAscender = S16(os2, 68);
                TypoDescender = S16(os2, 70);
                TypoLineGap = S16(os2, 72);
                WinAscent = U16(os2, 74);
                WinDescent = U16(os2, 76);
                if (os2.Length >= 90)
                {
                    XHeight = S16(os2, 86);
                    CapHeight = S16(os2, 88);
                }
            }

            byte[] post = Get(TableTags.Post);
            if (post != null && post.Length >= 12)
            {
                UnderlinePosition = S16(post, 8);
                UnderlineThickness = S16(post, 10);
            }

            ParseHmtx();
            ParseNames();
            DetectSymbolCmap();
        }

        private void ParseHmtx()
        {
            byte[] hmtx = Get(TableTags.Hmtx);
            int count = NumGlyphs;
            _advanceWidths = new int[count];
            _leftSideBearings = new int[count];
            if (hmtx == null || count == 0) return;

            int metrics = NumberOfHMetrics;
            if (metrics == 0) metrics = count;
            if (metrics > count) metrics = count;

            int lastAdvance = 0;
            for (int g = 0; g < count; g++)
            {
                int offset = g < metrics ? g * 4 : metrics * 4 + (g - metrics) * 2;
                if (g < metrics)
                {
                    if (offset + 4 > hmtx.Length) break;
                    lastAdvance = U16(hmtx, offset);
                    _advanceWidths[g] = lastAdvance;
                    _leftSideBearings[g] = S16(hmtx, offset + 2);
                }
                else
                {
                    // 超出 numberOfHMetrics 的字形复用最后一个 advance，只有 lsb 单独给。
                    _advanceWidths[g] = lastAdvance;
                    if (offset + 2 > hmtx.Length) break;
                    _leftSideBearings[g] = S16(hmtx, offset);
                }
            }
        }

        private void ParseNames()
        {
            byte[] name = Get(TableTags.Name);
            if (name == null || name.Length < 6) return;

            int count = U16(name, 2);
            int stringOffset = U16(name, 4);

            for (int i = 0; i < count; i++)
            {
                int rec = 6 + i * 12;
                if (rec + 12 > name.Length) break;

                ushort platformId = U16(name, rec);
                ushort encodingId = U16(name, rec + 2);
                ushort languageId = U16(name, rec + 4);
                ushort nameId = U16(name, rec + 6);
                int length = U16(name, rec + 8);
                int offset = U16(name, rec + 10);
                int start = stringOffset + offset;
                if (start < 0 || start + length > name.Length) continue;

                Encoding encoding = (platformId == 0 || platformId == 3)
                    ? Encoding.BigEndianUnicode          // Unicode / Windows：UTF-16BE
                    : Encoding.Latin1;                   // Mac Roman 的 ASCII 子集（够读 family/subfamily）

                string value = encoding.GetString(name, start, length);
                _names.Add(new NameRecord(platformId, encodingId, languageId, nameId, value));
            }
        }

        private void DetectSymbolCmap()
        {
            byte[] cmap = Get(TableTags.Cmap);
            if (cmap == null || cmap.Length < 4) return;

            int count = U16(cmap, 2);
            for (int i = 0; i < count; i++)
            {
                int rec = 4 + i * 8;
                if (rec + 8 > cmap.Length) break;
                if (U16(cmap, rec) == 3 && U16(cmap, rec + 2) == 0) { HasSymbolCmap = true; return; }
            }
        }

        // ---------------------------------------------------------------------------------
        //  查询
        // ---------------------------------------------------------------------------------

        /// <summary>取表的原始字节（副本的所有权归调用方，可安全缓存）。不存在返回 null。</summary>
        public byte[] Get(uint tag) => _tables.TryGetValue(tag, out byte[] bytes) ? bytes : null;

        public bool TryGet(uint tag, out byte[] bytes) => _tables.TryGetValue(tag, out bytes);

        /// <summary>hmtx.advanceWidth（设计单位）。越界返回 0。</summary>
        public int AdvanceWidth(ushort glyph) =>
            glyph < _advanceWidths.Length ? _advanceWidths[glyph] : 0;

        /// <summary>hmtx.leftSideBearing（设计单位）。越界返回 0。</summary>
        public int LeftSideBearing(ushort glyph) =>
            glyph < _leftSideBearings.Length ? _leftSideBearings[glyph] : 0;

        /// <summary>vmtx.advanceHeight（设计单位）；没有 vmtx 时返回 0。</summary>
        public int AdvanceHeight(ushort glyph)
        {
            byte[] vmtx = Get(TableTags.Vmtx);
            byte[] vhea = Get(TableTags.Vhea);
            if (vmtx == null || vhea == null || vhea.Length < 36) return 0;

            int numVMetrics = U16(vhea, 34);
            if (numVMetrics == 0) return 0;

            if (glyph < numVMetrics)
            {
                int offset = glyph * 4;
                return offset + 4 <= vmtx.Length ? U16(vmtx, offset) : 0;
            }

            // 与 hmtx 同样的省略规则：超出部分复用最后一个 advanceHeight。
            int last = (numVMetrics - 1) * 4;
            return last + 4 <= vmtx.Length ? U16(vmtx, last) : 0;
        }

        /// <summary>vmtx.topSideBearing；没有 vmtx 时返回 <see cref="int.MinValue"/> 表示"无值"。</summary>
        public int TopSideBearing(ushort glyph, int advanceHeight)
        {
            byte[] vmtx = Get(TableTags.Vmtx);
            byte[] vhea = Get(TableTags.Vhea);
            if (vmtx == null || vhea == null || vhea.Length < 36) return int.MinValue;

            int numVMetrics = U16(vhea, 34);
            if (numVMetrics == 0) return int.MinValue;

            if (glyph < numVMetrics)
            {
                int offset = glyph * 4;
                return offset + 4 <= vmtx.Length ? S16(vmtx, offset + 2) : int.MinValue;
            }

            int tsbOffset = numVMetrics * 4 + (glyph - numVMetrics) * 2;
            return tsbOffset + 2 <= vmtx.Length ? S16(vmtx, tsbOffset) : int.MinValue;
        }

        /// <summary>
        /// loca + glyf 里该字形的外接盒。空轮廓（如空格）返回 false，四个值全 0。
        /// 这是 GetDesignGlyphMetrics 的 rightSideBearing/topSideBearing 的原始数据。
        /// </summary>
        public bool TryGetGlyphBounds(ushort glyph, out short xMin, out short yMin, out short xMax, out short yMax)
        {
            xMin = yMin = xMax = yMax = 0;
            ResolveBounds();
            if (_bounds == null || glyph >= _bounds.Length) return false;

            var b = _bounds[glyph];
            if (!b.HasBounds) return false;
            xMin = b.XMin; yMin = b.YMin; xMax = b.XMax; yMax = b.YMax;
            return true;
        }

        private void ResolveBounds()
        {
            if (_boundsResolved) return;
            _boundsResolved = true;

            _locaTable = Get(TableTags.Loca);
            _glyfTable = Get(TableTags.Glyf);
            if (_locaTable == null || _glyfTable == null || NumGlyphs == 0) return;

            bool longFormat = IndexToLocFormat == 1;
            int entrySize = longFormat ? 4 : 2;
            if (_locaTable.Length < (NumGlyphs + 1) * entrySize) return;

            _bounds = new (bool, short, short, short, short)[NumGlyphs];
            for (int g = 0; g < NumGlyphs; g++)
            {
                long start = longFormat ? U32(_locaTable, g * 4) : (uint)(U16(_locaTable, g * 2) * 2);
                long end = longFormat ? U32(_locaTable, (g + 1) * 4) : (uint)(U16(_locaTable, (g + 1) * 2) * 2);

                // 空轮廓：loca 里 offset 相等（glyf 里没有该字形的数据）。
                if (end <= start || start + 10 > _glyfTable.Length) continue;

                _bounds[g] = (true, S16(_glyfTable, (int)start + 2), S16(_glyfTable, (int)start + 4),
                                    S16(_glyfTable, (int)start + 6), S16(_glyfTable, (int)start + 8));
            }
        }

        /// <summary>
        /// cmap 查表：码点 → 字形（未映射返回 0 = .notdef）。
        /// 优先 format 12（覆盖增补平面），退回 format 4（BMP）。这**不是** Skia 的 API。
        /// </summary>
        public ushort CmapLookup(int codePoint)
        {
            if (codePoint < 0 || codePoint > 0x10FFFF) return 0;

            byte[] cmap = Get(TableTags.Cmap);
            if (cmap == null || cmap.Length < 4) return 0;

            int count = U16(cmap, 2);
            int format12 = -1, format4 = -1, format6 = -1;
            for (int i = 0; i < count; i++)
            {
                int rec = 4 + i * 8;
                if (rec + 8 > cmap.Length) break;

                ushort platform = U16(cmap, rec);
                ushort encoding = U16(cmap, rec + 2);
                int sub = (int)U32(cmap, rec + 4);
                if (sub + 2 > cmap.Length) continue;

                // Unicode 平台(0) 或 Windows 的 UCS-2/UCS-4 编码(1/10) 才是我们要的。
                bool unicode = platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10));
                if (!unicode) continue;

                switch (U16(cmap, sub))
                {
                    case 12: if (format12 < 0) format12 = sub; break;
                    case 4: if (format4 < 0) format4 = sub; break;
                    case 6: if (format6 < 0) format6 = sub; break;
                }
            }

            if (codePoint > 0xFFFF)
                return format12 >= 0 ? LookupFormat12(cmap, format12, codePoint) : (ushort)0;

            if (format4 >= 0)
            {
                ushort g = LookupFormat4(cmap, format4, codePoint);
                if (g != 0) return g;
            }

            if (format6 >= 0)
            {
                ushort g = LookupFormat6(cmap, format6, codePoint);
                if (g != 0) return g;
            }

            return format4 >= 0 ? LookupFormat4(cmap, format4, codePoint) : (ushort)0;
        }

        private static ushort LookupFormat4(byte[] cmap, int sub, int codePoint)
        {
            int segCountX2 = U16(cmap, sub + 6);
            int segCount = segCountX2 / 2;
            int endCodes = sub + 14;
            int startCodes = endCodes + segCountX2 + 2;      // +2 跳过 reservedPad
            int idDeltas = startCodes + segCountX2;
            int idRangeOffsets = idDeltas + segCountX2;

            if (idRangeOffsets + segCountX2 > cmap.Length) return 0;
            if (codePoint > 0xFFFF) return 0;

            for (int s = 0; s < segCount; s++)
            {
                ushort end = U16(cmap, endCodes + s * 2);
                if (codePoint > end) continue;

                ushort start = U16(cmap, startCodes + s * 2);
                if (codePoint < start) return 0;

                short delta = S16(cmap, idDeltas + s * 2);
                int rangeOffsetPos = idRangeOffsets + s * 2;
                ushort rangeOffset = U16(cmap, rangeOffsetPos);

                if (rangeOffset == 0)
                    return (ushort)((codePoint + delta) & 0xFFFF);

                int glyphPos = rangeOffsetPos + rangeOffset + (codePoint - start) * 2;
                if (glyphPos + 2 > cmap.Length) return 0;

                ushort g = U16(cmap, glyphPos);
                return g == 0 ? (ushort)0 : (ushort)((g + delta) & 0xFFFF);
            }

            return 0;
        }

        private static ushort LookupFormat12(byte[] cmap, int sub, int codePoint)
        {
            uint groups = U32(cmap, sub + 12);
            int lo = 0;
            int hi = checked((int)groups) - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int g = sub + 16 + mid * 12;
                if (g + 12 > cmap.Length) return 0;

                uint start = U32(cmap, g);
                uint end = U32(cmap, g + 4);
                if (codePoint < start) { hi = mid - 1; continue; }
                if (codePoint > end) { lo = mid + 1; continue; }

                return (ushort)(U32(cmap, g + 8) + (uint)(codePoint - (int)start));
            }

            return 0;
        }

        private static ushort LookupFormat6(byte[] cmap, int sub, int codePoint)
        {
            int first = U16(cmap, sub + 6);
            int entryCount = U16(cmap, sub + 8);
            int index = codePoint - first;
            if (index < 0 || index >= entryCount) return 0;

            int pos = sub + 10 + index * 2;
            return pos + 2 <= cmap.Length ? U16(cmap, pos) : (ushort)0;
        }

        /// <summary>增补平面（&gt; U+FFFF）里被覆盖的第一个码点；没有则返回 -1。用于代理对用例。</summary>
        public int FindFirstSupplementaryCodePoint()
        {
            byte[] cmap = Get(TableTags.Cmap);
            if (cmap == null || cmap.Length < 4) return -1;

            int count = U16(cmap, 2);
            for (int i = 0; i < count; i++)
            {
                int rec = 4 + i * 8;
                if (rec + 8 > cmap.Length) break;

                ushort platform = U16(cmap, rec);
                ushort encoding = U16(cmap, rec + 2);
                if (!(platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10)))) continue;

                int sub = (int)U32(cmap, rec + 4);
                if (sub + 2 > cmap.Length || U16(cmap, sub) != 12) continue;

                uint groups = U32(cmap, sub + 12);
                for (uint g = 0; g < groups; g++)
                {
                    int go = sub + 16 + (int)g * 12;
                    if (go + 12 > cmap.Length) break;

                    uint start = U32(cmap, go);
                    uint end = U32(cmap, go + 4);
                    if (end > 0xFFFF) return (int)Math.Max(start, 0x10000u);
                }
            }

            return -1;
        }

        /// <summary>增补平面被覆盖的码点总数（用于报告"这份字体有没有代理对真值可用"）。</summary>
        public int CountSupplementaryCodePoints()
        {
            byte[] cmap = Get(TableTags.Cmap);
            if (cmap == null || cmap.Length < 4) return 0;

            int count = U16(cmap, 2);
            int total = 0;
            // 同一张子表会被多个 platform/encoding 记录指向（实测 Noto Sans：platform 0/4 与
            // platform 3/10 都指向 offset 3288 的同一张 format 12）。必须按 subtable 去重，
            // 否则统计值会翻倍 —— 这是"看起来对、实际错一倍"的典型坑。
            var seen = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                int rec = 4 + i * 8;
                if (rec + 8 > cmap.Length) break;

                ushort platform = U16(cmap, rec);
                ushort encoding = U16(cmap, rec + 2);
                if (!(platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10)))) continue;

                int sub = (int)U32(cmap, rec + 4);
                if (sub + 2 > cmap.Length || U16(cmap, sub) != 12) continue;
                if (!seen.Add(sub)) continue;

                uint groups = U32(cmap, sub + 12);
                for (uint g = 0; g < groups; g++)
                {
                    int go = sub + 16 + (int)g * 12;
                    if (go + 12 > cmap.Length) break;

                    uint start = U32(cmap, go);
                    uint end = U32(cmap, go + 4);
                    if (end > 0xFFFF)
                        total += (int)(Math.Min(end, 0x10FFFFu) - Math.Max(start, 0x10000u) + 1);
                }
            }

            return total;
        }

        // ---------------------------------------------------------------------------------
        //  name 表查询
        // ---------------------------------------------------------------------------------

        public IReadOnlyList<NameRecord> Names => _names;

        /// <summary>按 nameId 取字符串（优先 Windows 平台 en-US，其次任意 Unicode 平台）。</summary>
        public string GetNameString(ushort nameId)
        {
            NameRecord? fallback = null;
            foreach (NameRecord record in _names)
            {
                if (record.NameId != nameId) continue;
                if (record.PlatformId == 3 && record.LanguageId == 0x0409) return record.Value;
                if (record.PlatformId == 3 && fallback == null) fallback = record;
                else if (fallback == null) fallback = record;
            }

            return fallback?.Value;
        }

        /// <summary>按 nameId 收集全部本地化字符串（DWrite 的 LocalizedStrings 来源）。</summary>
        public Dictionary<CultureInfo, string> GetNameStrings(ushort nameId)
        {
            var map = new Dictionary<CultureInfo, string>();
            foreach (NameRecord record in _names)
            {
                if (record.NameId != nameId) continue;
                map[record.Culture] = record.Value;   // 同文化后出现的覆盖前面的（与 DWrite 一致：取最后一个）
            }

            return map;
        }

        // ---------------------------------------------------------------------------------
        //  大端读取
        // ---------------------------------------------------------------------------------

        public static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        public static short S16(byte[] b, int o) => (short)U16(b, o);
        public static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        public static int S32(byte[] b, int o) => (int)U32(b, o);
    }
}
