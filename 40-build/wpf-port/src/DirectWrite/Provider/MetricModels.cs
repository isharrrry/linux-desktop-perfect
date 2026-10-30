// T2 · Phase 1 —— DWrite 形态的数据模型
// =====================================================================================
// 【字段名为什么必须和 ManagedSurface.cs 里的一模一样】
//   这些类型的唯一用途是**喂给** src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/ManagedSurface.cs
//   里那几个 [PNSE] 成员。字段名/类型对齐后，接线就是机械的一对一赋值
//   （见 WIRING.md 里给出的适配代码），不需要再想"这个字段对应哪个"。
//
//   对照关系（骨架 ← 本工程）：
//     MS.Internal.Text.TextInterface.FontMetrics      ← FontMetricsData
//     MS.Internal.Text.TextInterface.GlyphMetrics     ← GlyphMetricsData
//     MS.Internal.Text.TextInterface.LocalizedStrings ← LocalizedStringsData
//     MS.Internal.Text.TextInterface.FontFaceType     ← FontFaceKind
//     MS.Internal.Text.TextInterface.FontFileType     ← FontFileKind
//     MS.Internal.Text.TextInterface.GlyphOffset      ← GlyphOffsetData
//
// 【本文件不做的事】
//   不引用骨架（方向是骨架引用本工程）；不用 [StructLayout] 去仿原生布局
//   —— 这些值在 Linux 上不参与任何原生互操作，去仿 28 字节显式布局只会
//   引入"字段顺序被谁改了"这类无谓风险。布局的正确性由骨架侧的
//   GlyphMetrics（Size=28）保证，本工程只保证**值**正确。
//
// 【Provenance 字段为什么存在】
//   T2 的核心要求是"必须真值，不再是 PNSE"。要证明"真"，就得能说清楚每个数
//   是从哪张表的哪个字段来的、以及 Skia 是否给了同一个数。所以每个度量对象都带
//   一份来源说明，测试会把它们打进报告。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>
    /// 字体面的整体度量（设计单位）。
    /// 字段与 DWRITE_FONT_METRICS / 骨架的 FontMetrics **逐项同名同型同序**。
    /// </summary>
    public sealed class FontMetricsData
    {
        /// <summary>每 em 的设计单位数（head.unitsPerEm）。</summary>
        public ushort DesignUnitsPerEm;

        /// <summary>基线之上的高度（设计单位）。</summary>
        public ushort Ascent;

        /// <summary>基线之下（向下的正数）的高度（设计单位）。</summary>
        public ushort Descent;

        /// <summary>行间额外空隙；可能为负。DWRITE 约定：行距 = Ascent + Descent + LineGap。</summary>
        public short LineGap;

        public ushort CapHeight;
        public ushort XHeight;

        /// <summary>下划线位置；DWRITE 约定：**负数表示在基线之下**。</summary>
        public short UnderlinePosition;

        public ushort UnderlineThickness;

        /// <summary>删除线位置；DWRITE 约定：**正数表示在基线之上**。</summary>
        public short StrikethroughPosition;

        public ushort StrikethroughThickness;

        /// <summary>头文件公式（骨架里已是 [真实现]）：(Ascent + LineGap*0.5) / DesignUnitsPerEm。</summary>
        public double Baseline => (Ascent + LineGap * 0.5) / DesignUnitsPerEm;

        /// <summary>
        /// 头文件公式（FontMetrics.h:124）：<c>(double)(Ascent + Descent + LineGap) / DesignUnitsPerEm</c>。
        ///
        /// 【这个 (double) 强转是必须的 —— 上游有，骨架漏了】
        ///   上游原文是 `return (double)(this->Ascent + this->Descent + this->LineGap) / DesignUnitsPerEm;`
        ///   而 src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder/ManagedSurface.cs:319 把它写成了
        ///       public double LineSpacing => (Ascent + Descent + LineGap) / DesignUnitsPerEm;
        ///   在 C# 里 Ascent/Descent 是 ushort、LineGap 是 short、DesignUnitsPerEm 是 ushort，
        ///   于是 `int / int` 先做**整数除法**再隐式转 double：
        ///     Noto Sans: (1069 + 293 + 0) / 1000 = 1   ← 真值是 1.362
        ///   即骨架的那个"[真实现]"成员在 Linux 上会返回 1.0 而不是 1.362，
        ///   直接影响 PhysicalFontFamily 的行高（FontMetrics.LineSpacing → Typeface.LineSpacing）。
        ///   本工程保留强转，并在 REPORT.md 里把骨架这一处报给主控。
        /// </summary>
        public double LineSpacing => (double)(Ascent + Descent + LineGap) / DesignUnitsPerEm;

        /// <summary>每个字段的来源（哪张表的哪个字段 / Skia 是否给同一个数）。仅用于取证。</summary>
        public string Provenance { get; set; } = string.Empty;

        public FontMetricsData Clone() => (FontMetricsData)MemberwiseClone();

        /// <summary>确定性文本形式：跨进程哈希的输入之一。</summary>
        public override string ToString() =>
            $"upem={DesignUnitsPerEm} asc={Ascent} desc={Descent} gap={LineGap} cap={CapHeight} xh={XHeight} " +
            $"ul=({UnderlinePosition},{UnderlineThickness}) st=({StrikethroughPosition},{StrikethroughThickness})";

        public IEnumerable<(string Field, int Value)> Fields()
        {
            yield return (nameof(DesignUnitsPerEm), DesignUnitsPerEm);
            yield return (nameof(Ascent), Ascent);
            yield return (nameof(Descent), Descent);
            yield return (nameof(LineGap), LineGap);
            yield return (nameof(CapHeight), CapHeight);
            yield return (nameof(XHeight), XHeight);
            yield return (nameof(UnderlinePosition), UnderlinePosition);
            yield return (nameof(UnderlineThickness), UnderlineThickness);
            yield return (nameof(StrikethroughPosition), StrikethroughPosition);
            yield return (nameof(StrikethroughThickness), StrikethroughThickness);
        }
    }

    /// <summary>
    /// 单个字形的度量（设计单位）。
    /// 字段与 DWRITE_GLYPH_METRICS / 骨架的 GlyphMetrics（[StructLayout(Explicit, Size=28)]）逐项对齐。
    /// </summary>
    public struct GlyphMetricsData
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing;
        public int TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing;
        public int VerticalOriginY;

        public override string ToString() =>
            $"lsb={LeftSideBearing} adv={AdvanceWidth} rsb={RightSideBearing} tsb={TopSideBearing} advH={AdvanceHeight} bsb={BottomSideBearing} voy={VerticalOriginY}";
    }

    /// <summary>
    /// 字形偏移（DesignUnits × 缩放后的偏移量，取整）。
    /// 对应 DWrite 的 <c>DWRITE_GLYPH_OFFSET</c> / 骨架的 <c>GlyphOffset</c>（du/dv）。
    /// </summary>
    public struct GlyphOffsetData
    {
        /// <summary>水平偏移（沿文本前进方向）。</summary>
        public int du;

        /// <summary>垂直偏移（向上为正）。</summary>
        public int dv;

        public override string ToString() => $"({du},{dv})";
    }

    /// <summary>字体面的种类（对应骨架的 FontFaceType，逐项同名同序）。</summary>
    public enum FontFaceKind { CFF, TrueType, TrueTypeCollection, Type1, Vector, Bitmap, Unknown }

    /// <summary>字体文件的种类（对应骨架的 FontFileType，逐项同名同序）。</summary>
    public enum FontFileKind { Unknown, CFF, TrueType, TrueTypeCollection, Type1PFM, Type1PFB, Vector, Bitmap }

    /// <summary>
    /// 本地化字符串集合。与骨架的 <c>LocalizedStrings</c> 一样实现
    /// <see cref="IDictionary{TKey,TValue}"/> 且**只读**（写入抛 NotSupportedException）。
    /// </summary>
    public sealed class LocalizedStringsData : IDictionary<CultureInfo, string>
    {
        private readonly Dictionary<CultureInfo, string> _map;

        public LocalizedStringsData() => _map = new Dictionary<CultureInfo, string>();

        public LocalizedStringsData(IDictionary<CultureInfo, string> source)
        {
            _map = new Dictionary<CultureInfo, string>();
            if (source != null)
                foreach (KeyValuePair<CultureInfo, string> kv in source) _map[kv.Key] = kv.Value;
        }

        public LocalizedStringsData(CultureInfo[] cultures, string[] strings)
        {
            _map = new Dictionary<CultureInfo, string>();
            int n = Math.Min(cultures?.Length ?? 0, strings?.Length ?? 0);
            for (int i = 0; i < n; i++) _map[cultures[i]] = strings[i];
        }

        /// <summary>骨架里叫 StringsCount（uint）。</summary>
        public uint StringsCount => (uint)_map.Count;

        /// <summary>骨架里叫 KeysArray。</summary>
        public CultureInfo[] KeysArray => new List<CultureInfo>(_map.Keys).ToArray();

        /// <summary>骨架里叫 ValuesArray。</summary>
        public string[] ValuesArray => new List<string>(_map.Values).ToArray();

        public string this[CultureInfo key]
        {
            get => _map[key];
            set => throw new NotSupportedException("LocalizedStringsData 为只读集合");
        }

        public ICollection<CultureInfo> Keys => _map.Keys;
        public ICollection<string> Values => _map.Values;
        public int Count => _map.Count;
        public bool IsReadOnly => true;

        public bool ContainsKey(CultureInfo key) => _map.ContainsKey(key);
        public bool TryGetValue(CultureInfo key, out string value) => _map.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<CultureInfo, string>> GetEnumerator() => _map.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _map.GetEnumerator();

        public void Add(CultureInfo key, string value) => throw new NotSupportedException("LocalizedStringsData 为只读集合");
        public bool Remove(CultureInfo key) => throw new NotSupportedException("LocalizedStringsData 为只读集合");
        public void Add(KeyValuePair<CultureInfo, string> item) => throw new NotSupportedException("LocalizedStringsData 为只读集合");
        public void Clear() => throw new NotSupportedException("LocalizedStringsData 为只读集合");
        public bool Contains(KeyValuePair<CultureInfo, string> item) => _map.ContainsKey(item.Key);
        public void CopyTo(KeyValuePair<CultureInfo, string>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<CultureInfo, string>>)_map).CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<CultureInfo, string> item) => throw new NotSupportedException("LocalizedStringsData 为只读集合");

        /// <summary>确定性文本形式（跨进程哈希用）。</summary>
        public string ToDeterministicString()
        {
            var keys = new List<CultureInfo>(_map.Keys);
            keys.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            var sb = new System.Text.StringBuilder();
            foreach (CultureInfo key in keys)
            {
                sb.Append(key.Name).Append('=').Append(_map[key]).Append('\u001f');
            }

            return sb.ToString();
        }
    }

    /// <summary>FontFile.Analyze 的输出（骨架里是三个 out 参数）。</summary>
    public readonly struct FontFileAnalysis
    {
        public FontFileAnalysis(FontFileKind fileKind, FontFaceKind faceKind, int numberOfFaces)
        {
            FileKind = fileKind;
            FaceKind = faceKind;
            NumberOfFaces = numberOfFaces;
        }

        public FontFileKind FileKind { get; }
        public FontFaceKind FaceKind { get; }
        public int NumberOfFaces { get; }
    }

    /// <summary>
    /// DWrite 的 InformationalStringID → name 表 nameId 的映射。
    /// 骨架的 InformationalStringID 顺序取自 InformationalStringID.h（与 DWRITE 一致），
    /// 这里按其字面值逐项对应 name 表的 nameId（OpenType 规范）。
    /// </summary>
    public static class InformationalStrings
    {
        public const int None = 0;
        public const int CopyrightNotice = 1;
        public const int VersionStrings = 2;
        public const int Trademark = 3;
        public const int Manufacturer = 4;
        public const int Designer = 5;
        public const int DesignerURL = 6;
        public const int Description = 7;
        public const int FontVendorURL = 8;
        public const int LicenseDescription = 9;
        public const int LicenseInfoURL = 10;
        public const int WIN32FamilyNames = 11;
        public const int Win32SubFamilyNames = 12;
        public const int PreferredFamilyNames = 13;
        public const int PreferredSubFamilyNames = 14;
        public const int SampleText = 15;

        /// <summary>DWRITE 的 InformationalStringID → OpenType name 表的 nameId。</summary>
        public static bool TryMapNameId(int informationalStringId, out ushort nameId)
        {
            switch (informationalStringId)
            {
                case CopyrightNotice: nameId = 0; return true;
                case VersionStrings: nameId = 5; return true;
                case Trademark: nameId = 7; return true;
                case Manufacturer: nameId = 8; return true;
                case Designer: nameId = 9; return true;
                case Description: nameId = 10; return true;
                case FontVendorURL: nameId = 11; return true;
                case DesignerURL: nameId = 12; return true;
                case LicenseDescription: nameId = 13; return true;
                case LicenseInfoURL: nameId = 14; return true;
                case WIN32FamilyNames: nameId = 1; return true;
                case Win32SubFamilyNames: nameId = 2; return true;
                case PreferredFamilyNames: nameId = 16; return true;
                case PreferredSubFamilyNames: nameId = 17; return true;
                case SampleText: nameId = 19; return true;
                default: nameId = 0; return false;
            }
        }
    }

    /// <summary>字体族/字面的匹配规则。</summary>
    public enum FontMatchingRule
    {
        /// <summary>
        /// 距离优先（DWrite 风格）：|请求 - 候选| 在 (weight, stretch, style) 上字典序最小，
        /// 平局取更轻的字重。**本工程默认**。
        /// </summary>
        Distance = 0,

        /// <summary>
        /// 粗体桶（M1 Text/ 的现有口径）：weight &gt;= 600 视为粗体、否则视为正体，
        /// 再做 (粗体, 斜体) 二元匹配。用于与 M1 <c>FontSet.TryResolve</c> 逐值一致。
        /// </summary>
        BoldBucket = 1,
    }
}
