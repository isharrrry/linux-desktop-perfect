// DirectWriteForwarder · Linux 托管骨架 —— 托管面
// =====================================================================================
// 逐类型对齐上游 CPP/DWriteWrapper/*.h（3.5k 行，形态可查）。语义分三档，逐成员标注：
//   [真实现] 纯托管算术/枚举映射 —— 公式取自头文件（FontMetrics.Baseline/LineSpacing、
//            DWriteTypeConverter 的一对一映射），Linux 上语义与 Windows 完全一致；
//   [数据]   纯数据容器（LocalizedStrings / DWriteFontFeature / GlyphMetrics / Span）；
//   [PNSE]   需要 DWrite 原生对象或字形数据 —— 一律抛 PlatformNotSupportedException，
//            消息指向替代路线（M1 Skia 文本栈），**不返回假数据**。
//   ↓ T2 更新（2026-09-10）：62 条 PNSE 中的 A/B/C 档共 47 条已改为**委托给 Linux provider
//     的真实现**（src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider，Skia/FreeType 承载），标签由 [PNSE] 改为
//     [provider]；剩余 D 档 15 条（shaping / COM 回调 / 子集化）仍为 [PNSE]。
//     分类依据与逐条证据：src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/PNSE-INVENTORY.md；接线清单：WIRING.md。
//
//   【生命周期：Release 绝不能映射成 Dispose】
//     上游 Font::GetFontFace() 每次调用都返回 **+1 引用**的 FontFace
//     （Font.cpp:GetFontFace：缓存命中 → AddRef；未命中 → AddRef 后入缓存），
//     PresentationCore 在 7 个不同的 finally 里 Release 掉自己那一份
//     （GlyphTypeface.cs:93,967,1079,1103,1275,1562,1703）。
//     所以本文件的 Release() 一律转 provider 的 Release（只递减计数、不销毁对象），
//     Dispose() 也只是 => Release()。映射成"销毁"就是 use-after-free。
//
// 这一档划分是刻意的：编译里程碑可以带着 [PNSE] 前进，但绝不能让「返回空集合/0」
// 之类假成功混进来（SplashScreen 教训，见 src/Linux/build/excludes/WindowsBase.txt）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MS.Internal.Text.TextInterface.Native;
using MS.Internal.Text.TextInterface.Linux;   // T2：Skia/FreeType 承载的真实现

namespace MS.Internal
{
    /// <summary>
    /// 非泛型 <c>Span</c>（上游 <c>CPP/DWriteWrapper/ItemSpan.h</c> 的
    /// <c>private ref struct Span sealed</c>）：元素 + 长度，用于文本分段结果。
    /// </summary>
    /// <remarks>
    /// 上游是 C++/CLI 的 ref class（引用类型），字段 <c>element</c>/<c>length</c> 直接可见；
    /// PresentationCore 的 MS/internal/Span.cs 与 TextFormatting 用它做分段容器。
    /// 该类型在树内**没有** C# 定义，是 DWriteForwarder 经 IVT 暴露的 —— 这正是
    /// 「GlyphOffset/Span 凭空缺失」的根因（M4 实测，见 docs/U2-PresentationCore-prep.md §5）。
    /// </remarks>
    internal sealed class Span
    {
        internal object element;   // 上游：Object^ element
        internal int length;       // 上游：int length

        internal Span(object element, int length)
        {
            this.element = element;
            this.length = length;
        }
    }
}

namespace MS.Internal.Text.TextInterface
{
    /// <summary>[PNSE] 未实现的统一出口：消息固定带替代路线，避免"静默假成功"。</summary>
    internal static class NotSupported
    {
        /// <summary>
        /// D 档（本轮不做）的统一文案。
        /// T2 之后剩下的 PNSE 只有 15 条，全部属于"需要 shaping 引擎 / COM 回调 / 子集化器"
        /// —— 简单文本路径不经过它们（依据见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/PNSE-INVENTORY.md）。
        /// </summary>
        internal const string Reason =
            "Linux 侧该成员属 T2 分类的 D 档（本轮不做）：需要排版 shaping 引擎（GSUB/GPOS）、" +
            "COM 回调或 TrueType 子集化器。A/B/C 档 47 条已由 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider" +
            "（Skia/FreeType 承载）实现，分类与证据见该目录的 PNSE-INVENTORY.md 与 WIRING.md。";

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static Exception Throw(string member) =>
            throw new PlatformNotSupportedException(member + "：" + Reason);

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static T Throw<T>(string member) =>
            throw new PlatformNotSupportedException(member + "：" + Reason);
    }

    // =================================================================================
    // 枚举（值逐项取自上游同名头文件）
    // =================================================================================

    /// <summary>FactoryType.h：[真实现] Shared / Isolated。</summary>
    internal enum FactoryType { Shared, Isolated }

    /// <summary>FontSimulation.h：[真实现] 位标志 None/Bold/Oblique。</summary>
    [Flags]
    internal enum FontSimulations
    {
        None = 0x0000,
        Bold = 0x0001,
        Oblique = 0x0002,
    }

    /// <summary>FontWeight.h：[真实现] 100..950。</summary>
    internal enum FontWeight
    {
        Thin = 100, ExtraLight = 200, UltraLight = 200, Light = 300, Normal = 400,
        Regular = 400, Medium = 500, DemiBold = 600, SemiBOLD = 600, Bold = 700,
        ExtraBold = 800, UltraBold = 800, Black = 900, Heavy = 900,
        ExtraBlack = 950, UltraBlack = 950,
    }

    /// <summary>FontStretch.h：[真实现] Undefined=0 .. UltraExpanded=9。</summary>
    internal enum FontStretch
    {
        Undefined = 0, UltraCondensed = 1, ExtraCondensed = 2, Condensed = 3,
        SemiCondensed = 4, Normal = 5, Medium = 5, SemiExpanded = 6, Expanded = 7,
        ExtraExpanded = 8, UltraExpanded = 9,
    }

    /// <summary>FontStyle.h：[真实现] Normal/Oblique/Italic。</summary>
    internal enum FontStyle { Normal = 0, Oblique = 1, Italic = 2 }

    /// <summary>FontFaceType.h：[真实现] CFF/TrueType/TrueTypeCollection/Type1/Vector/Bitmap/Unknown。</summary>
    internal enum FontFaceType { CFF, TrueType, TrueTypeCollection, Type1, Vector, Bitmap, Unknown }

    /// <summary>FontFileType.h：[真实现] Unknown/CFF/TrueType/TrueTypeCollection/Type1PFM/Type1PFB/Vector/Bitmap。</summary>
    internal enum FontFileType { Unknown, CFF, TrueType, TrueTypeCollection, Type1PFM, Type1PFB, Vector, Bitmap }

    /// <summary>InformationalStringID.h：[真实现] 16 项，顺序与 DWRITE_INFORMATIONAL_STRING_ID 一致。</summary>
    internal enum InformationalStringID
    {
        None, CopyrightNotice, VersionStrings, Trademark, Manufacturer, Designer,
        DesignerURL, Description, FontVendorURL, LicenseDescription, LicenseInfoURL,
        WIN32FamilyNames, Win32SubFamilyNames, PreferredFamilyNames, PreferredSubFamilyNames, SampleText,
    }

    /// <summary>
    /// OpenTypeTableTag.h：[真实现] OpenType 表标签。
    /// 值按 <c>DWRITE_MAKE_OPENTYPE_TAG(a,b,c,d) = (a&lt;&lt;24)|(b&lt;&lt;16)|(c&lt;&lt;8)|d</c> 由头文件逐项展开。
    /// </summary>
    internal enum OpenTypeTableTag : uint
    {
        CharToIndexMap = 0x636D6170u /* 'cmap' */,
        ControlValue = 0x63767420u /* 'cvt ' */,
        BitmapData = 0x45424454u /* 'EBDT' */,
        BitmapLocation = 0x45424C43u /* 'EBLC' */,
        BitmapScale = 0x45425343u /* 'EBSC' */,
        Editor0 = 0x65647430u /* 'edt0' */,
        Editor1 = 0x65647431u /* 'edt1' */,
        Encryption = 0x63727970u /* 'cryp' */,
        FontHeader = 0x68656164u /* 'head' */,
        FontProgram = 0x6670676Du /* 'fpgm' */,
        GridfitAndScanProc = 0x67617370u /* 'gasp' */,
        GlyphDirectory = 0x67646972u /* 'gdir' */,
        GlyphData = 0x676C7966u /* 'glyf' */,
        HoriDeviceMetrics = 0x68646D78u /* 'hdmx' */,
        HoriHeader = 0x68686561u /* 'hhea' */,
        HorizontalMetrics = 0x686D7478u /* 'hmtx' */,
        IndexToLoc = 0x6C6F6361u /* 'loca' */,
        Kerning = 0x6B65726Eu /* 'kern' */,
        LinearThreshold = 0x4C545348u /* 'LTSH' */,
        MaxProfile = 0x6D617870u /* 'maxp' */,
        NamingTable = 0x6E616D65u /* 'name' */,
        OS_2 = 0x4F532F32u /* 'OS/2' */,
        Postscript = 0x706F7374u /* 'post' */,
        PreProgram = 0x70726570u /* 'prep' */,
        VertDeviceMetrics = 0x56444D58u /* 'VDMX' */,
        VertHeader = 0x76686561u /* 'vhea' */,
        VerticalMetrics = 0x766D7478u /* 'vmtx' */,
        PCLT = 0x50434C54u /* 'PCLT' */,
        TTO_GSUB = 0x47535542u /* 'GSUB' */,
        TTO_GPOS = 0x47504F53u /* 'GPOS' */,
        TTO_GDEF = 0x47444546u /* 'GDEF' */,
        TTO_BASE = 0x42415345u /* 'BASE' */,
        TTO_JSTF = 0x4A535446u /* 'JSTF' */,
    }

    /// <summary>
    /// [真实现] <c>DWRITE_MAKE_OPENTYPE_TAG(a,b,c,d) = (a&lt;&lt;24)|(b&lt;&lt;16)|(c&lt;&lt;8)|d</c>
    /// （定义见 dwrite.h / Common.h），把 OpenType 的四字符标签折成 uint。
    /// 放在独立静态类里是因为 C# 枚举不能带方法。
    /// </summary>
    internal static class OpenTypeTag
    {
        internal static uint Make(char a, char b, char c, char d) =>
            ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
    }

    /// <summary>
    /// DWriteFontFeatureTag.h：[真实现] OpenType 特性标签。
    /// 成员名与字面值**由脚本从上游该头文件逐行机械提取**（不是手抄、不是凭记忆）。
    /// </summary>
    internal enum DWriteFontFeatureTag : uint
    {
        AlternativeFractions = 0x63726661u,   // 'afrc'
        PetiteCapitalsFromCapitals = 0x63703263u,   // 'c2pc'
        SmallCapitalsFromCapitals = 0x63733263u,   // 'c2sc'
        ContextualAlternates = 0x746c6163u,   // 'calt'
        CaseSensitiveForms = 0x65736163u,   // 'case'
        GlyphCompositionDecomposition = 0x706d6363u,   // 'ccmp'
        ContextualLigatures = 0x67696c63u,   // 'clig'
        CapitalSpacing = 0x70737063u,   // 'cpsp'
        ContextualSwash = 0x68777363u,   // 'cswh'
        CursivePositioning = 0x73727563u,   // 'curs'
        Default = 0x746c6664u,   // 'dflt'
        DiscretionaryLigatures = 0x67696c64u,   // 'dlig'
        ExpertForms = 0x74707865u,   // 'expt'
        Fractions = 0x63617266u,   // 'frac'
        FullWidth = 0x64697766u,   // 'fwid'
        HalfForms = 0x666c6168u,   // 'half'
        HalantForms = 0x6e6c6168u,   // 'haln'
        AlternateHalfWidth = 0x746c6168u,   // 'halt'
        HistoricalForms = 0x74736968u,   // 'hist'
        HorizontalKanaAlternates = 0x616e6b68u,   // 'hkna'
        HistoricalLigatures = 0x67696c68u,   // 'hlig'
        HalfWidth = 0x64697768u,   // 'hwid'
        HojoKanjiForms = 0x6f6a6f68u,   // 'hojo'
        JIS04Forms = 0x3430706au,   // 'jp04'
        JIS78Forms = 0x3837706au,   // 'jp78'
        JIS83Forms = 0x3338706au,   // 'jp83'
        JIS90Forms = 0x3039706au,   // 'jp90'
        Kerning = 0x6e72656bu,   // 'kern'
        StandardLigatures = 0x6167696cu,   // 'liga'
        LiningFigures = 0x6d756e6cu,   // 'lnum'
        LocalizedForms = 0x6c636f6cu,   // 'locl'
        MarkPositioning = 0x6b72616du,   // 'mark'
        MathematicalGreek = 0x6b72676du,   // 'mgrk'
        MarkToMarkPositioning = 0x6b6d6b6du,   // 'mkmk'
        AlternateAnnotationForms = 0x746c616eu,   // 'nalt'
        NLCKanjiForms = 0x6b636c6eu,   // 'nlck'
        OldStyleFigures = 0x6d756e6fu,   // 'onum'
        Ordinals = 0x6e64726fu,   // 'ordn'
        ProportionalAlternateWidth = 0x746c6170u,   // 'palt'
        PetiteCapitals = 0x70616370u,   // 'pcap'
        ProportionalFigures = 0x6d756e70u,   // 'pnum'
        ProportionalWidths = 0x64697770u,   // 'pwid'
        QuarterWidths = 0x64697771u,   // 'qwid'
        RequiredLigatures = 0x67696c72u,   // 'rlig'
        RubyNotationForms = 0x79627572u,   // 'ruby'
        StylisticAlternates = 0x746c6173u,   // 'salt'
        ScientificInferiors = 0x666e6973u,   // 'sinf'
        SmallCapitals = 0x70636d73u,   // 'smcp'
        SimplifiedForms = 0x6c706d73u,   // 'smpl'
        StylisticSet1 = 0x31307373u,   // 'ss01'
        StylisticSet2 = 0x32307373u,   // 'ss02'
        StylisticSet3 = 0x33307373u,   // 'ss03'
        StylisticSet4 = 0x34307373u,   // 'ss04'
        StylisticSet5 = 0x35307373u,   // 'ss05'
        StylisticSet6 = 0x36307373u,   // 'ss06'
        StylisticSet7 = 0x37307373u,   // 'ss07'
        StylisticSet8 = 0x38307373u,   // 'ss08'
        StylisticSet9 = 0x39307373u,   // 'ss09'
        StylisticSet10 = 0x30317373u,   // 'ss10'
        StylisticSet11 = 0x31317373u,   // 'ss11'
        StylisticSet12 = 0x32317373u,   // 'ss12'
        StylisticSet13 = 0x33317373u,   // 'ss13'
        StylisticSet14 = 0x34317373u,   // 'ss14'
        StylisticSet15 = 0x35317373u,   // 'ss15'
        StylisticSet16 = 0x36317373u,   // 'ss16'
        StylisticSet17 = 0x37317373u,   // 'ss17'
        StylisticSet18 = 0x38317373u,   // 'ss18'
        StylisticSet19 = 0x39317373u,   // 'ss19'
        StylisticSet20 = 0x30327373u,   // 'ss20'
        Subscript = 0x73627573u,   // 'subs'
        Superscript = 0x73707573u,   // 'sups'
        Swash = 0x68737773u,   // 'swsh'
        Titling = 0x6c746974u,   // 'titl'
        TraditionalNameForms = 0x6d616e74u,   // 'tnam'
        TabularFigures = 0x6d756e74u,   // 'tnum'
        TraditionalForms = 0x64617274u,   // 'trad'
        ThirdWidths = 0x64697774u,   // 'twid'
        Unicase = 0x63696e75u,   // 'unic'
        SlashedZero = 0x6f72657au,   // 'zero'
    }

    // =================================================================================
    // 结构体 / 数据容器
    // =================================================================================

    /// <summary>DWriteFontFeature.h：[数据] 布局 Sequential（nameTag:uint + parameter:uint = 8 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DWriteFontFeature
    {
        internal DWriteFontFeatureTag nameTag;
        internal uint parameter;

        internal DWriteFontFeature(DWriteFontFeatureTag nameTag, uint parameter)
        {
            this.nameTag = nameTag;
            this.parameter = parameter;
        }
    }

    /// <summary>GlyphOffset.h：[数据] 布局 Sequential（du:int + dv:int = 8 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlyphOffset
    {
        internal int du;
        internal int dv;
    }

    /// <summary>GlyphMetrics.h：[数据] 显式布局，7 字段共 28 字节（与头文件 FieldOffset 逐项一致）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct GlyphMetrics
    {
        [FieldOffset(0)] internal int LeftSideBearing;
        [FieldOffset(4)] internal uint AdvanceWidth;
        [FieldOffset(8)] internal int RightSideBearing;
        [FieldOffset(12)] internal int TopSideBearing;
        [FieldOffset(16)] internal uint AdvanceHeight;
        [FieldOffset(20)] internal int BottomSideBearing;
        [FieldOffset(24)] internal int VerticalOriginY;
    }

    /// <summary>DWriteMatrix.h：[数据] 3x2 仿射矩阵（6×float）。</summary>
    internal sealed class DWriteMatrix
    {
        internal float M11, M12, M21, M22, DX, DY;
    }

    /// <summary>
    /// FontMetrics.h：[真实现] 显式布局的度量容器，<c>Baseline</c>/<c>LineSpacing</c>
    /// 的公式与头文件逐字一致（纯算术，Linux 上语义相同）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal sealed class FontMetrics
    {
        [FieldOffset(0)] public ushort DesignUnitsPerEm;
        [FieldOffset(2)] public ushort Ascent;
        [FieldOffset(4)] public ushort Descent;
        [FieldOffset(8)] public short LineGap;
        [FieldOffset(10)] public ushort CapHeight;
        [FieldOffset(12)] public ushort XHeight;
        [FieldOffset(14)] public short UnderlinePosition;
        [FieldOffset(16)] public ushort UnderlineThickness;
        [FieldOffset(18)] public short StrikethroughPosition;
        [FieldOffset(20)] public ushort StrikethroughThickness;

        /// <summary>[真实现] 头文件公式：(Ascent + LineGap * 0.5) / DesignUnitsPerEm。</summary>
        public double Baseline => (Ascent + LineGap * 0.5) / DesignUnitsPerEm;

        /// <summary>[真实现] 头文件公式：(Ascent + Descent + LineGap) / DesignUnitsPerEm。
        /// ⚠ 必须显式 (double) 强转：Ascent/Descent 是 ushort、LineGap 是 short、DesignUnitsPerEm 是 ushort，
        /// 不转就是 **int / int 先做整数除法**再隐式转 double —— 实测 Noto Sans 行高会从 1.362 变成 **1**（截断），
        /// 多行文本行距全错且"数值看着合理"。上游 FontMetrics.h:124 正是 (double) 强转写法。
        /// （Baseline 因 `LineGap * 0.5` 已把分子提升为 double，不受影响。）</summary>
        public double LineSpacing => (double)(Ascent + Descent + LineGap) / DesignUnitsPerEm;
    }

    /// <summary>
    /// LocalizedStrings.h：[数据] 本地化字符串集合，实现
    /// <c>IDictionary&lt;CultureInfo, string&gt;</c>（只读语义，写入抛 NotSupportedException）。
    /// 构造由 DWriteForwarder 内部完成；Linux 侧因字体数据不可得而不会真正产生实例。
    /// </summary>
    internal sealed class LocalizedStrings : IDictionary<CultureInfo, string>
    {
        private readonly Dictionary<CultureInfo, string> _map;

        public LocalizedStrings() => _map = new Dictionary<CultureInfo, string>();

        internal LocalizedStrings(CultureInfo[] cultures, string[] strings)
        {
            _map = new Dictionary<CultureInfo, string>();
            int n = Math.Min(cultures?.Length ?? 0, strings?.Length ?? 0);
            for (int i = 0; i < n; i++)
                _map[cultures[i]] = strings[i];
        }

        internal uint StringsCount => (uint)_map.Count;
        internal CultureInfo[] KeysArray => new List<CultureInfo>(_map.Keys).ToArray();
        internal string[] ValuesArray => new List<string>(_map.Values).ToArray();

        public string this[CultureInfo key]
        {
            get => _map[key];
            set => throw new NotSupportedException("LocalizedStrings 为只读集合");
        }

        public ICollection<CultureInfo> Keys => _map.Keys;
        public ICollection<string> Values => _map.Values;
        public int Count => _map.Count;
        public bool IsReadOnly => true;

        public bool ContainsKey(CultureInfo key) => _map.ContainsKey(key);
        public bool TryGetValue(CultureInfo key, out string value) => _map.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<CultureInfo, string>> GetEnumerator() => _map.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _map.GetEnumerator();
        public void Add(CultureInfo key, string value) => throw new NotSupportedException("LocalizedStrings 为只读集合");
        public bool Remove(CultureInfo key) => throw new NotSupportedException("LocalizedStrings 为只读集合");
        public void Add(KeyValuePair<CultureInfo, string> item) => throw new NotSupportedException("LocalizedStrings 为只读集合");
        public void Clear() => throw new NotSupportedException("LocalizedStrings 为只读集合");
        public bool Contains(KeyValuePair<CultureInfo, string> item) => _map.ContainsKey(item.Key);
        public void CopyTo(KeyValuePair<CultureInfo, string>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<CultureInfo, string>>)_map).CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<CultureInfo, string> item) => throw new NotSupportedException("LocalizedStrings 为只读集合");
    }

    /// <summary>
    /// IClassification.h：[接口] 字符分类回调（PresentationCore 的
    /// <c>MS.Internal.Classification</c> 实现它）。
    /// </summary>
    internal interface IClassification
    {
        void GetCharAttribute(
            int unicodeScalar,
            out bool isCombining,
            out bool needsCaretInfo,
            out bool isIndic,
            out bool isDigit,
            out bool isLatin,
            out bool isStrong);
    }

    /// <summary>IFontSource.h：[接口] 字体文件来源抽象（PresentationCore 侧实现/使用）。</summary>
    internal interface IFontSource
    {
        void TestFileOpenable();
        UnmanagedMemoryStream GetUnmanagedStream();
        DateTime GetLastWriteTimeUtc();
        Uri Uri { get; }
        bool IsComposite { get; }
    }

    /// <summary>IFontSource.h：[接口] 字体来源工厂。</summary>
    internal interface IFontSourceFactory
    {
        IFontSource Create(string uri);
    }

    // =================================================================================
    // 字体对象模型（[PNSE]：全部依赖 DWrite 原生对象）
    // =================================================================================

    /// <summary>ItemProps.h：[PNSE] 逐项文本属性（脚本分析 + 数字替换 + 字符分类标志）。</summary>
    internal sealed unsafe class ItemProps
    {
        private readonly void* _numberSubstitution;
        private readonly void* _scriptAnalysis;
        private readonly CultureInfo _digitCulture;
        private readonly bool _hasCombiningMark, _needsCaretInfo, _hasExtendedCharacter, _isIndic, _isLatin;

        public ItemProps()
        {
            _digitCulture = null;
        }

        private ItemProps(void* scriptAnalysis, void* numberSubstitution, CultureInfo digitCulture,
                          bool hasCombiningMark, bool needsCaretInfo, bool hasExtendedCharacter,
                          bool isIndic, bool isLatin)
        {
            _scriptAnalysis = scriptAnalysis;
            _numberSubstitution = numberSubstitution;
            _digitCulture = digitCulture;
            _hasCombiningMark = hasCombiningMark;
            _needsCaretInfo = needsCaretInfo;
            _hasExtendedCharacter = hasExtendedCharacter;
            _isIndic = isIndic;
            _isLatin = isLatin;
        }

        internal static unsafe ItemProps Create(
            void* scriptAnalysis, void* numberSubstitution, CultureInfo digitCulture,
            bool hasCombiningMark, bool needsCaretInfo, bool hasExtendedCharacter,
            bool isIndic, bool isLatin) =>
            new ItemProps(scriptAnalysis, numberSubstitution, digitCulture, hasCombiningMark,
                          needsCaretInfo, hasExtendedCharacter, isIndic, isLatin);

        public void* NumberSubstitutionNoAddRef => _numberSubstitution;
        public void* ScriptAnalysis => _scriptAnalysis;
        public CultureInfo DigitCulture => _digitCulture;
        public bool HasExtendedCharacter => _hasExtendedCharacter;
        public bool NeedsCaretInfo => _needsCaretInfo;
        public bool IsIndic => _isIndic;
        public bool IsLatin => _isLatin;
        public bool HasCombiningMark => _hasCombiningMark;

        /// <summary>[真实现] 头文件语义：字符属性等价即可共形。</summary>
        public bool CanShapeTogether(ItemProps other) =>
            other is not null
            && _scriptAnalysis == other._scriptAnalysis
            && _numberSubstitution == other._numberSubstitution
            && Equals(_digitCulture, other._digitCulture)
            && _hasCombiningMark == other._hasCombiningMark
            && _needsCaretInfo == other._needsCaretInfo
            && _hasExtendedCharacter == other._hasExtendedCharacter
            && _isIndic == other._isIndic
            && _isLatin == other._isLatin;
    }

    /// <summary>
    /// FontMetrics 之外的字形度量辅助（上游 FontFace 的静态构造入口所在文件），
    /// 由 <see cref="GlyphMetrics"/> 承载，无独立类型。
    /// </summary>

    /// <summary>FontFace.h：[PNSE] 一个字体面（含度量、字形索引、字体表访问）。</summary>
    internal sealed unsafe class FontFace : IDisposable
    {
        private readonly IDWriteFontFace* _fontFace;

        /// <summary>[provider] 真正的字体面（Skia/FreeType 承载）。生命周期归 Font/FontCollection。</summary>
        private readonly LinuxFontFace _linuxFace;

        private FontMetrics _fontMetrics;
        private int _refCount = 1;

        /// <summary>
        /// 从"原生形态的指针"构造：反查我们自己发出去的令牌。
        /// 反查不到即抛 —— Linux 侧没有原生 DWrite 对象可以解引用。
        /// </summary>
        internal FontFace(IDWriteFontFace* fontFace)
        {
            _fontFace = fontFace;
            _linuxFace = ProviderAdapters.ResolveFontFace(fontFace);
            _refCount = 1;
        }

        /// <summary>[provider] 直接用托管字体面构造（Factory 的托管化路径、以及 Font 的缓存路径用）。</summary>
        internal FontFace(LinuxFontFace linuxFace)
        {
            _linuxFace = linuxFace ?? throw new ArgumentNullException(nameof(linuxFace));
            _fontFace = (IDWriteFontFace*)linuxFace.Token;
            _refCount = 1;
        }

        /// <summary>[provider] 托管字体面（Font 的内部缓存返回值、以及 FindFontFromFontFace 用）。</summary>
        internal LinuxFontFace LinuxFace => _linuxFace;

        internal IDWriteFontFace* DWriteFontFaceNoAddRef => _fontFace;

        /// <summary>
        /// [provider] 上游语义：AddRef 后返回原生指针
        /// （GlyphTypeface.cs:1264 把它交给 MilGlyphRun_GetGlyphOutline，
        ///   注释原文 "Released in this native code function"）。
        /// 返回的是**句柄表令牌**，MIL 侧用 MilFontFaceTable 反查 —— 桥接见 WIRING.md §4。
        /// </summary>
        internal IntPtr DWriteFontFaceAddRef
        {
            get
            {
                IntPtr token = _linuxFace.Token;
                FontHandleTable.AddRef(token);
                return token;
            }
        }

        internal FontFaceType Type => ProviderAdapters.ToFontFaceType(_linuxFace.Type);
        internal uint Index => (uint)_linuxFace.FaceIndex;
        internal FontSimulations SimulationFlags => ProviderAdapters.ToFontSimulations(_linuxFace.SimulationFlags);
        internal bool IsSymbolFont => _linuxFace.IsSymbolFont;

        internal FontMetrics Metrics => _fontMetrics ??= ProviderAdapters.ToFontMetrics(_linuxFace.Metrics);

        internal ushort GlyphCount => (ushort)_linuxFace.GlyphCount;
        internal FontFile GetFileZero() => new FontFile(_linuxFace.GetFileZero());
        internal void AddRef() => _refCount++;

        /// <summary>
        /// [provider] DWrite 的 Release。**只递减引用、不销毁对象**（见文件头的生命周期说明）。
        /// </summary>
        internal void Release()
        {
            _refCount--;
            _linuxFace.Release();
        }
        internal void GetDesignGlyphMetrics(ushort* pGlyphIndices, uint glyphCount, GlyphMetrics* pGlyphMetrics)
        {
            if (glyphCount == 0) return;

            var glyphs = new ReadOnlySpan<ushort>(pGlyphIndices, (int)glyphCount);
            var metrics = new GlyphMetricsData[glyphCount];
            _linuxFace.GetDesignGlyphMetrics(glyphs, metrics);
            ProviderAdapters.WriteGlyphMetrics(metrics, (int)glyphCount, pGlyphMetrics);
        }
        internal void GetDisplayGlyphMetrics(ushort* pGlyphIndices, uint glyphCount, GlyphMetrics* pGlyphMetrics,
                                            float emSize, bool useDisplayNatural, bool isSideways, float pixelsPerDip)
        {
            if (glyphCount == 0) return;

            var glyphs = new ReadOnlySpan<ushort>(pGlyphIndices, (int)glyphCount);
            var metrics = new GlyphMetricsData[glyphCount];
            _linuxFace.GetDisplayGlyphMetrics(glyphs, metrics, emSize, useDisplayNatural, isSideways, pixelsPerDip);
            ProviderAdapters.WriteGlyphMetrics(metrics, (int)glyphCount, pGlyphMetrics);
        }
        internal void GetArrayOfGlyphIndices(uint* pCodePoints, uint glyphCount, ushort* pGlyphIndices)
        {
            if (glyphCount == 0) return;

            var codePoints = new int[glyphCount];
            for (int i = 0; i < glyphCount; i++) codePoints[i] = (int)pCodePoints[i];

            var glyphs = new ushort[glyphCount];
            _linuxFace.GetArrayOfGlyphIndices(codePoints, glyphs);

            for (int i = 0; i < glyphCount; i++) pGlyphIndices[i] = glyphs[i];
        }

        /// <summary>[provider] 表标签是同一个 uint 语义（DWRITE_MAKE_OPENTYPE_TAG），直接透传。</summary>
        internal bool TryGetFontTable(OpenTypeTableTag openTypeTableTag, out byte[] tableData) =>
            _linuxFace.TryGetFontTable((uint)openTypeTableTag, out tableData);

        /// <summary>[provider] OS/2 偏移 8 处的 fsType（与上游 FontFace.cpp:228-258 同构）。</summary>
        internal bool ReadFontEmbeddingRights(out ushort fsType) => _linuxFace.ReadFontEmbeddingRights(out fsType);

        /// <summary>[真实现] 上游 C++/CLI 的 ~FontFace() 即 IDisposable.Dispose；这里只递减引用计数。</summary>
        public void Dispose() => Release();
    }

    /// <summary>FontFile.h：[PNSE] 字体文件（路径/分析信息来自 DWrite）。</summary>
    internal sealed unsafe class FontFile : IDisposable
    {
        private readonly IDWriteFontFile* _fontFile;

        /// <summary>[provider] 真正的字体文件（路径 + OpenType 分析信息）。</summary>
        private readonly LinuxFontFile _linuxFile;

        /// <summary>从"原生形态的指针"构造：反查我们自己发出去的令牌。</summary>
        internal FontFile(IDWriteFontFile* fontFile)
        {
            _fontFile = fontFile;
            if (fontFile != null && FontHandleTable.TryResolveFile((IntPtr)fontFile, out LinuxFontFile file))
                _linuxFile = file;
        }

        /// <summary>[provider] 直接用托管字体文件构造。</summary>
        internal FontFile(LinuxFontFile linuxFile)
        {
            _linuxFile = linuxFile ?? throw new ArgumentNullException(nameof(linuxFile));
            _fontFile = (IDWriteFontFile*)linuxFile.Token;
        }

        internal IDWriteFontFile* DWriteFontFileNoAddRef => _fontFile;

        /// <summary>[provider] 托管字体文件（Factory 的托管化路径用）。</summary>
        internal LinuxFontFile LinuxFile => _linuxFile;

        /// <summary>
        /// [provider] DWrite 的 FontFile.Analyze。
        /// 文件不可读/不是字体 → 返回 false（与 DWRITE_E_FILEFORMAT 的语义一致），**不抛**：
        /// 上游 Factory.cs:201 正是用这个 false 分支去走"WPF 旧逻辑"的兜底（UnauthorizedAccessException）。
        /// </summary>
        internal bool Analyze(out DWRITE_FONT_FILE_TYPE dwriteFontFileType,
                              out DWRITE_FONT_FACE_TYPE dwriteFontFaceType,
                              out uint numberOfFaces,
                              int* hr)
        {
            dwriteFontFileType = default;
            dwriteFontFaceType = default;
            numberOfFaces = 0;
            if (hr != null) *hr = 0;

            if (_linuxFile == null || !_linuxFile.Analyze(out FontFileAnalysis analysis)) return false;

            dwriteFontFileType = ProviderAdapters.ToDwriteFileType(analysis.FileKind);
            dwriteFontFaceType = ProviderAdapters.ToDwriteFaceType(analysis.FaceKind);
            numberOfFaces = (uint)analysis.NumberOfFaces;
            return true;
        }

        /// <summary>[provider] 本地文件路径；字节流加载时为空串（不拿标签冒充路径）。</summary>
        internal string GetUriPath() => _linuxFile == null ? string.Empty : _linuxFile.GetUriPath();

        /// <summary>[真实现] 上游 C++/CLI 的 ~FontFile() 即 IDisposable.Dispose；无原生对象，空实现。</summary>
        public void Dispose() { }
    }

    /// <summary>Font.h：[PNSE] 一个具体字体（族 + 字重/拉伸/样式 + 字体面缓存）。</summary>
    internal sealed unsafe class Font
    {
        private readonly IDWriteFont* _font;

        /// <summary>[provider] 真正的字面（族 + 字重/拉伸/样式 + 惰性字体面缓存）。</summary>
        private readonly LinuxFont _linuxFont;

        /// <summary>指针路径构造：Linux 上没有原生 IDWriteFont，故托管状态为空（成员会响亮地失败）。</summary>
        internal Font(IDWriteFont* font) => _font = font;

        /// <summary>[provider] 用托管字面构造。</summary>
        internal Font(LinuxFont linuxFont)
        {
            _linuxFont = linuxFont ?? throw new ArgumentNullException(nameof(linuxFont));
            _font = (IDWriteFont*)linuxFont.DWriteFontAddRef;
        }

        /// <summary>[provider] 托管字面。</summary>
        internal LinuxFont LinuxFont => _linuxFont;

        /// <summary>
        /// 指针路径构造（Linux 上无原生 IDWriteFont）时，成员一律**响亮地失败**，
        /// 而不是 NullReferenceException 或者返回 0 —— 后者正是本项目明令禁止的"静默假成功"。
        /// </summary>
        private LinuxFont Require => _linuxFont ?? NotWired.Throw<LinuxFont>(nameof(Font));

        /// <summary>
        /// [provider] GlyphRun.cs:1876 把它写进 <c>command.pIDWriteFont</c> 交给 MIL；
        /// MIL 侧（M7a）用 MilFontFaceTable 反查 SKTypeface 画轮廓 —— 桥接见 WIRING.md §4。
        /// </summary>
        internal IntPtr DWriteFontAddRef => Require.DWriteFontAddRef;

        internal FontFamily Family => Require.Family == null ? null : new FontFamily(_linuxFont.Family);
        internal FontWeight Weight => (FontWeight)Require.Weight;
        internal FontStretch Stretch => (FontStretch)Require.Stretch;
        internal FontStyle Style => (FontStyle)Require.Style;
        internal bool IsSymbolFont => Require.IsSymbolFont;
        internal LocalizedStrings FaceNames => ProviderAdapters.ToLocalizedStrings(Require.FaceNames);
        internal FontSimulations SimulationFlags => ProviderAdapters.ToFontSimulations(Require.SimulationFlags);
        internal FontMetrics Metrics => ProviderAdapters.ToFontMetrics(Require.Metrics);
        internal double Version => Require.Version;

        /// <summary>[provider] 上游 Font.cpp:DisplayMetrics → GetGdiCompatibleMetrics 口径（吸附像素网格）。</summary>
        internal FontMetrics DisplayMetrics(float emSize, float pixelsPerDip) =>
            ProviderAdapters.ToFontMetrics(Require.DisplayMetrics(emSize, pixelsPerDip));

        internal static void ResetFontFaceCache() { /* 无原生缓存：no-op 是真实现，不是假成功 */ }

        /// <summary>
        /// [provider] 上游语义：**每次调用返回 +1 引用**的字体面（Font.cpp:GetFontFace）。
        /// 调用方（PresentationCore 的 7 个 finally）负责 Release 掉自己那一份。
        /// </summary>
        internal FontFace GetFontFace() => new FontFace(Require.GetFontFace());

        internal bool GetInformationalStrings(InformationalStringID informationalStringID,
                                              out LocalizedStrings informationalStrings)
        {
            informationalStrings = null;
            if (!Require.GetInformationalStrings((int)informationalStringID, out LocalizedStringsData data))
                return false;

            informationalStrings = ProviderAdapters.ToLocalizedStrings(data);
            return informationalStrings != null;
        }

        internal bool HasCharacter(uint unicodeValue) => Require.HasCharacter((int)unicodeValue);
    }

    /// <summary>FontList.h：[PNSE] 字体列表（可枚举）。</summary>
    internal class FontList : IEnumerable<Font>
    {
        /// <summary>[provider] 托管列表。指针路径构造时为空（成员会响亮地失败）。</summary>
        private readonly LinuxFontList _linuxList;

        internal FontList() { }

        /// <summary>[provider] 用托管列表构造。</summary>
        internal FontList(LinuxFontList linuxList) => _linuxList = linuxList;

        /// <summary>[provider] 托管列表。</summary>
        internal LinuxFontList LinuxList => _linuxList;

        /// <summary>指针/空构造时成员响亮失败（见 Font.Require 的说明）。</summary>
        private LinuxFontList RequireList => _linuxList ?? NotWired.Throw<LinuxFontList>(nameof(FontList));

        internal virtual Font this[uint index] =>
            new Font(RequireList[(int)index]);

        internal virtual uint Count => (uint)RequireList.Count;

        internal virtual FontCollection FontsCollection =>
            RequireList.FontsCollection == null ? null : new FontCollection(RequireList.FontsCollection);

        /// <summary>
        /// [provider] 上游 FontList.h 的 FontsEnumerator。
        /// 注意：C# 的 yield 迭代器在"未开始取 Current"时抛 InvalidOperationException，
        /// 而上游抛的是 LocalizedErrorMsgs 的固定文案 + InvalidOperationException。
        /// 异常**类型**一致、消息不同；PresentationCore 侧没有依赖该文案的调用点
        /// （见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/PNSE-INVENTORY.md 的调用点普查）。
        /// </summary>
        public IEnumerator<Font> GetEnumerator()
        {
            foreach (LinuxFont font in RequireList) yield return new Font(font);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>FontFamily.h：[PNSE] 字体族（物理族或复合族）。</summary>
    internal sealed class FontFamily : FontList
    {
        private readonly LinuxFontFamily _linuxFamily;

        internal FontFamily() { }

        /// <summary>[provider] 用托管族构造。</summary>
        internal FontFamily(LinuxFontFamily linuxFamily)
            : base(linuxFamily)
        {
            _linuxFamily = linuxFamily;
        }

        /// <summary>[provider] 托管族。</summary>
        internal LinuxFontFamily LinuxFamily => _linuxFamily;

        internal LocalizedStrings FamilyNames => ProviderAdapters.ToLocalizedStrings(_linuxFamily.FamilyNames);
        internal bool IsPhysical => _linuxFamily.IsPhysical;
        internal bool IsComposite => _linuxFamily.IsComposite;
        internal string OrdinalName => _linuxFamily.OrdinalName;
        internal FontMetrics Metrics => ProviderAdapters.ToFontMetrics(_linuxFamily.Metrics);

        internal FontMetrics DisplayMetrics(float emSize, float pixelsPerDip) =>
            ProviderAdapters.ToFontMetrics(_linuxFamily.DisplayMetrics(emSize, pixelsPerDip));

        /// <summary>
        /// [provider] 匹配规则用 Distance（DWrite 风格：字重距离 → 拉伸 → 样式，平局取更轻）。
        /// 实测与 M1 <c>TextFontDescription</c> 的粗体桶口径在 WPF FontWeight 的
        /// 全部 10 个枚举值 × 3 种 style 上结论**完全相同**（30/30，见 T2 的
        /// M1ConsistencyTests）；差异只在 551..599 这 49 个非枚举整数上。
        /// 若要与 M1 逐值一致，把 FontMatchingRule.Distance 换成 FontMatchingRule.BoldBucket。
        /// </summary>
        internal Font GetFirstMatchingFont(FontWeight weight, FontStretch stretch, FontStyle style) =>
            new Font(_linuxFamily.GetFirstMatchingFont((int)weight, (int)stretch, (int)style, FontMatchingRule.Distance));

        internal FontList GetMatchingFonts(FontWeight weight, FontStretch stretch, FontStyle style) =>
            new FontList(_linuxFamily.GetMatchingFonts((int)weight, (int)stretch, (int)style));
    }

    /// <summary>FontCollection.h：[PNSE] 字体集合（系统集合或自定义集合）。</summary>
    internal sealed unsafe class FontCollection
    {
        private readonly IDWriteFontCollection* _collection;

        /// <summary>[provider] 托管字体集合。指针路径构造时为空。</summary>
        private readonly LinuxFontCollection _linuxCollection;

        /// <summary>指针路径构造：Linux 上没有原生集合可解引用，故托管状态为空。</summary>
        internal FontCollection(IDWriteFontCollection* collection) => _collection = collection;

        /// <summary>[provider] 用托管集合构造。</summary>
        internal FontCollection(LinuxFontCollection linuxCollection) =>
            _linuxCollection = linuxCollection ?? throw new ArgumentNullException(nameof(linuxCollection));

        /// <summary>
        /// [provider] 宿主（Factory 的托管化路径）按目录建集合。
        /// 确定性三条与 M1 FontSet 完全一致：只认显式目录、按文件名 Ordinal 排序、
        /// 坏文件跳过但记账（见 LinuxFontCollection.cs 的文件头）。
        /// </summary>
        internal static FontCollection FromDirectory(string directory) =>
            new FontCollection(LinuxFontCollection.FromDirectory(directory));

        /// <summary>[provider] 托管集合（宿主可直接取用）。</summary>
        internal LinuxFontCollection LinuxCollection => _linuxCollection;

        /// <summary>
        /// [provider] 族数量。指针路径（Linux 上无原生集合）→ 响亮失败，
        /// **不返回 0**（0 会被上层当成"系统里一个字体都没有"，是静默假成功）。
        /// </summary>
        internal uint FamilyCount
        {
            get
            {
                if (_linuxCollection == null)
                    throw NotWired.Throw(nameof(FamilyCount));
                return (uint)_linuxCollection.FamilyCount;
            }
        }

        internal FontFamily this[uint familyIndex]
        {
            get
            {
                // 越界由 provider 的列表抛出（不返回空族）
                return new FontFamily(_linuxCollection[(int)familyIndex]);
            }
        }

        /// <summary>
        /// [provider] 按族名取族。找不到返回 **null**（上游 DWrite 走 FindFamilyName 的 false 分支，
        /// WPF 自己有兜底逻辑）；不回落系统字体、不造空族。
        /// </summary>
        internal FontFamily this[string familyName]
        {
            get
            {
                LinuxFontFamily family = _linuxCollection?[familyName];
                return family == null ? null : new FontFamily(family);
            }
        }

        internal bool FindFamilyName(string familyName, out uint index)
        {
            index = 0;
            if (_linuxCollection == null)
                throw NotWired.Throw(nameof(FindFamilyName));

            bool found = _linuxCollection.FindFamilyName(familyName, out int i);
            index = found ? (uint)i : 0;
            return found;
        }

        internal Font GetFontFromFontFace(FontFace fontFace)
        {
            if (_linuxCollection == null || fontFace == null) return null;

            LinuxFont font = _linuxCollection.GetFontFromFontFace(fontFace.LinuxFace);
            return font == null ? null : new Font(font);
        }
    }


    // ---------------------------------------------------------------------------------
    // 文本分析回调委托（站内 oracle：PresentationCore/MS/internal/TextFormatting/LineServices.cs
    // 的 4 个 [DllImport(PresentationNative)] 方法，作为方法组传入 TextAnalyzer.Itemize）
    // ---------------------------------------------------------------------------------

    /// <summary>[签名] 对应 PresentationNative 导出 CreateTextAnalysisSource。</summary>
    internal unsafe delegate int CreateTextAnalysisSource(
        char* text, uint length, char* culture, void* factory, bool isRightToLeft,
        char* numberCulture, bool ignoreUserOverride, uint numberSubstitutionMethod,
        void** ppTextAnalysisSource);

    /// <summary>[签名] 对应 PresentationNative 导出 CreateTextAnalysisSink。</summary>
    internal unsafe delegate void* CreateTextAnalysisSink();

    /// <summary>[签名] 对应 PresentationNative 导出 GetScriptAnalysisList。</summary>
    internal unsafe delegate void* GetScriptAnalysisList(void* textAnalysisSink);

    /// <summary>[签名] 对应 PresentationNative 导出 GetNumberSubstitutionList。</summary>
    internal unsafe delegate void* GetNumberSubstitutionList(void* textAnalysisSink);

    /// <summary>
    /// LocalizedErrorMsgs.h：[PNSE] 本地化异常消息（字体枚举器的两条固定文案）。
    /// </summary>
    internal static class LocalizedErrorMsgs
    {
        private static string _enumeratorNotStarted;
        private static string _enumeratorReachedEnd;

        // 上游的这两条文案由 PresentationCore 的 DWriteFactory 静态构造赋值
        // （DWriteFactory.cs:25-26，只写不读）。这里给英文兜底而不是抛异常：
        // 原实现"未赋值就抛"在今天是死代码，但只要有谁先读后写就会在启动期炸，
        // 而那条路径（FontList 的枚举器）现在由 provider 的托管列表承担。
        internal static string EnumeratorNotStarted
        {
            get => _enumeratorNotStarted ?? "Enumerator not started.";
            set => _enumeratorNotStarted = value;
        }

        internal static string EnumeratorReachedEnd
        {
            get => _enumeratorReachedEnd ?? "Enumerator reached the end.";
            set => _enumeratorReachedEnd = value;
        }
    }

    /// <summary>Factory.h 的 InternalFactory：[PNSE] 工厂级操作（建文件、判本地 URI）。</summary>
    internal static unsafe class InternalFactory
    {
        /// <summary>[真实现] 纯托管判定：file:// 且非 UNC 视为本地（与头文件语义一致）。</summary>
        internal static bool IsLocalUri(Uri uri) => uri is not null && uri.IsFile && !uri.IsUnc;

        /// <summary>
        /// [provider] 对应上游 Factory.h 的 CreateFontFile：把本地文件 URI 变成一个
        /// "字体文件句柄"。**只产出句柄，不解析字体**（与 DWrite 的 CreateFontFileReference 同层次）。
        ///
        /// 返回码约定（与上游一致）：
        ///   0                          = S_OK，*dwriteFontFile 是有效令牌
        ///   0x88985000 (DWRITE_E_FILEFORMAT) = 文件打开成功但不是字体 / 面下标越界
        ///   0x80070002 (ERROR_FILE_NOT_FOUND) = 不是本地 URI 或文件不存在
        /// 上游 Factory.cs:143-160 正是用这两个失败码去走 WPF 的旧逻辑（抛 UnauthorizedAccessException）。
        /// </summary>
        internal static int CreateFontFile(IDWriteFactory* factory, FontFileLoader fontFileLoader,
                                           Uri filePathUri, IDWriteFontFile** dwriteFontFile)
        {
            const int S_OK = 0;
            const int DWRITE_E_FILEFORMAT = unchecked((int)0x88985000);
            const int ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);

            if (dwriteFontFile == null) return ERROR_FILE_NOT_FOUND;
            *dwriteFontFile = null;
            if (filePathUri == null || !IsLocalUri(filePathUri)) return ERROR_FILE_NOT_FOUND;

            string path = filePathUri.LocalPath;
            if (!System.IO.File.Exists(path)) return ERROR_FILE_NOT_FOUND;

            // 先用不依赖 Skia 的通路判一次"这是不是一个字体文件"（AnalyzeFile 内部解析 SFNT 目录）。
            if (!LinuxFontFile.AnalyzeFile(path, 0, out FontFileAnalysis _, out OpenTypeFontData _))
                return DWRITE_E_FILEFORMAT;   // 不是字体文件（上游注释里的 DWRITE_E_FILEFORMAT 分支）

            var file = LinuxFontFile.FromPath(path, 0);
            *dwriteFontFile = (IDWriteFontFile*)FontHandleTable.RegisterFile(file);
            return S_OK;
        }

        internal static DWRITE_MATRIX GetIdentityTransform() =>
            new DWRITE_MATRIX { M11 = 1, M22 = 1 };   // [真实现] 单位矩阵

        /// <summary>上游 Factory.h 的本地 URI 判定入口名（PresentationCore 用 IsLocalUri）。</summary>
        internal static bool IsLocalUri(string path) => Uri.TryCreate(path, UriKind.Absolute, out var u) && IsLocalUri(u);
    }

    /// <summary>
    /// DWriteTypeConverter.h：[真实现] 托管枚举 ↔ 原生 DWrite 枚举的一对一映射。
    /// 映射表按两侧枚举的字面顺序逐项对齐（值来源：托管枚举取上游同名 .h，
    /// 原生枚举取 PresentationCore 的 in-tree 镜像 MS/internal/Interop/DWrite/*.cs），
    /// 因此这些转换在 Linux 上与 Windows 语义一致，可安全用于编译期与纯托管路径。
    /// </summary>
    internal static class DWriteTypeConverter
    {
        internal static DWRITE_FACTORY_TYPE Convert(FactoryType factoryType) => factoryType switch
        {
            FactoryType.Shared => DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED,
            FactoryType.Isolated => DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_ISOLATED,
            _ => throw new ArgumentOutOfRangeException(nameof(factoryType)),
        };

        internal static byte Convert(FontSimulations fontSimulations) => (byte)fontSimulations;

        internal static FontSimulations Convert(DWRITE_FONT_SIMULATIONS fontSimulations) =>
            (FontSimulations)(uint)fontSimulations;

        internal static DWRITE_FONT_WEIGHT Convert(FontWeight fontWeight) => (DWRITE_FONT_WEIGHT)(int)fontWeight;
        internal static FontWeight Convert(DWRITE_FONT_WEIGHT fontWeight) => (FontWeight)(int)fontWeight;
        internal static DWRITE_FONT_STRETCH Convert(FontStretch fontStretch) => (DWRITE_FONT_STRETCH)(int)fontStretch;
        internal static FontStretch Convert(DWRITE_FONT_STRETCH fontStretch) => (FontStretch)(int)fontStretch;
        internal static DWRITE_FONT_STYLE Convert(FontStyle fontStyle) => (DWRITE_FONT_STYLE)(int)fontStyle;
        internal static FontStyle Convert(DWRITE_FONT_STYLE fontStyle) => (FontStyle)(int)fontStyle;

        internal static FontFaceType Convert(DWRITE_FONT_FACE_TYPE fontFaceType) => fontFaceType switch
        {
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_CFF => FontFaceType.CFF,
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TRUETYPE => FontFaceType.TrueType,
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_OPENTYPE_COLLECTION => FontFaceType.TrueTypeCollection,
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TYPE1 => FontFaceType.Type1,
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_VECTOR => FontFaceType.Vector,
            DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_BITMAP => FontFaceType.Bitmap,
            _ => FontFaceType.Unknown,
        };

        internal static DWRITE_FONT_FACE_TYPE Convert(FontFaceType fontFaceType) => fontFaceType switch
        {
            FontFaceType.CFF => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_CFF,
            FontFaceType.TrueType => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TRUETYPE,
            FontFaceType.TrueTypeCollection => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_OPENTYPE_COLLECTION,
            FontFaceType.Type1 => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TYPE1,
            FontFaceType.Vector => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_VECTOR,
            FontFaceType.Bitmap => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_BITMAP,
            _ => DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_UNKNOWN,
        };

        internal static FontFileType Convert(DWRITE_FONT_FILE_TYPE fileType) => fileType switch
        {
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_CFF => FontFileType.CFF,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TRUETYPE => FontFileType.TrueType,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_OPENTYPE_COLLECTION => FontFileType.TrueTypeCollection,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TYPE1_PFM => FontFileType.Type1PFM,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TYPE1_PFB => FontFileType.Type1PFB,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_VECTOR => FontFileType.Vector,
            DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_BITMAP => FontFileType.Bitmap,
            _ => FontFileType.Unknown,
        };

        internal static DWRITE_INFORMATIONAL_STRING_ID Convert(InformationalStringID id) => (DWRITE_INFORMATIONAL_STRING_ID)(int)id;
        internal static InformationalStringID Convert(DWRITE_INFORMATIONAL_STRING_ID id) => (InformationalStringID)(int)id;

        internal static DWRITE_MEASURING_MODE Convert(System.Windows.Media.TextFormattingMode measuringMode) =>
            measuringMode switch
            {
                System.Windows.Media.TextFormattingMode.Display => DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_GDI_CLASSIC,
                _ => DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
            };

        internal static System.Windows.Media.TextFormattingMode Convert(DWRITE_MEASURING_MODE mode) =>
            mode == DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_GDI_CLASSIC
                ? System.Windows.Media.TextFormattingMode.Display
                : System.Windows.Media.TextFormattingMode.Ideal;

        internal static FontMetrics Convert(DWRITE_FONT_METRICS m) => new FontMetrics
        {
            DesignUnitsPerEm = m.designUnitsPerEm,
            Ascent = m.ascent,
            Descent = m.descent,
            LineGap = m.lineGap,
            CapHeight = m.capHeight,
            XHeight = m.xHeight,
            UnderlinePosition = m.underlinePosition,
            UnderlineThickness = m.underlineThickness,
            StrikethroughPosition = m.strikethroughPosition,
            StrikethroughThickness = m.strikethroughThickness,
        };

        internal static DWRITE_FONT_METRICS Convert(FontMetrics m) => new DWRITE_FONT_METRICS
        {
            designUnitsPerEm = m.DesignUnitsPerEm,
            ascent = m.Ascent,
            descent = m.Descent,
            lineGap = m.LineGap,
            capHeight = m.CapHeight,
            xHeight = m.XHeight,
            underlinePosition = m.UnderlinePosition,
            underlineThickness = m.UnderlineThickness,
            strikethroughPosition = m.StrikethroughPosition,
            strikethroughThickness = m.StrikethroughThickness,
        };

        internal static DWRITE_MATRIX Convert(DWriteMatrix matrix) => new DWRITE_MATRIX
        {
            M11 = matrix.M11, M12 = matrix.M12, M21 = matrix.M21, M22 = matrix.M22, dx = matrix.DX, dy = matrix.DY,
        };

        internal static DWriteMatrix Convert(DWRITE_MATRIX matrix) => new DWriteMatrix
        {
            M11 = matrix.M11, M12 = matrix.M12, M21 = matrix.M21, M22 = matrix.M22, DX = matrix.dx, DY = matrix.dy,
        };

        internal static System.Windows.Point Convert(DWRITE_GLYPH_OFFSET offset) =>
            new System.Windows.Point(offset.advanceOffset, offset.ascenderOffset);
    }

    /// <summary>
    /// TextAnalyzer.h：[PNSE] 文本分析（itemize / 字形索引 / 字形定位）。
    /// 这是全文排版的入口，Linux 侧将由 M1 Skia 文本栈替代。
    /// </summary>
    internal sealed unsafe class TextAnalyzer
    {
        private readonly IDWriteTextAnalyzer* _analyzer;

        internal TextAnalyzer(IDWriteTextAnalyzer* textAnalyzer) => _analyzer = textAnalyzer;

        /// <summary>[真实现] 上游字面量：连字符 '-'（FormattedTextSymbols/TextFormatterContext 用它做断行）。</summary>
        internal const char CharHyphen = '\x002d';

        /// <summary>
        /// [PNSE · D 档] Itemize 需要**脚本分段**（DWrite 的 AnalyzeScript）。
        /// 但请注意：简单文本路径（SimpleTextLine → ComputeUnshapedGlyphRun）**不经过**本方法
        /// （TextCharacters.cs:236 直接 new ItemProps()），所以它不阻塞屏幕上的普通文本。
        /// </summary>
        internal static IList<Span> Itemize(
            char* text, uint length, CultureInfo culture, IDWriteFactory* pDWriteFactory,
            bool isRightToLeftParagraph, CultureInfo numberCulture, bool ignoreUserOverride,
            uint numberSubstitutionMethod, IClassification classificationUtility,
            CreateTextAnalysisSink pfnCreateTextAnalysisSink,
            GetScriptAnalysisList pfnGetScriptAnalysisList,
            GetNumberSubstitutionList pfnGetNumberSubstitutionList,
            CreateTextAnalysisSource pfnCreateTextAnalysisSource) =>
            NotSupported.Throw<IList<Span>>(nameof(Itemize));

        internal static IList<Span> AnalyzeExtendedAndItemize(
            object textItemizer, char* text, uint length, CultureInfo numberCulture, IClassification classification) =>
            NotSupported.Throw<IList<Span>>(nameof(AnalyzeExtendedAndItemize));

        internal static void ReleaseItemizationNativeResources(
            IDWriteFactory** ppFactory, IDWriteTextAnalyzer** ppTextAnalyzer,
            IDWriteTextAnalysisSource** ppTextAnalysisSource, IDWriteTextAnalysisSink** ppTextAnalysisSink) =>
            throw NotSupported.Throw(nameof(ReleaseItemizationNativeResources));

        internal void GetGlyphs(
            char* textString, uint textLength, Font font, ushort blankGlyphIndex, bool isSideways,
            bool isRightToLeft, CultureInfo cultureInfo, DWriteFontFeature[][] features, uint[] featureRangeLengths,
            uint maxGlyphCount, System.Windows.Media.TextFormattingMode textFormattingMode, ItemProps itemProps,
            ushort* clusterMap, ushort* textProps, ushort* glyphIndices, uint* glyphProps,
            int* pfCanGlyphAlone, out uint actualGlyphCount)
        {
            actualGlyphCount = 0;
            throw NotSupported.Throw(nameof(GetGlyphs));
        }

        internal void GetGlyphPlacements(
            char* textString, ushort* clusterMap, ushort* textProps, uint textLength,
            ushort* glyphIndices, uint* glyphProps, uint glyphCount, Font font,
            double fontEmSize, double scalingFactor, bool isSideways, bool isRightToLeft,
            CultureInfo cultureInfo, DWriteFontFeature[][] features, uint[] featureRangeLengths,
            System.Windows.Media.TextFormattingMode textFormattingMode, ItemProps itemProps,
            float pixelsPerDip, int* glyphAdvances, out GlyphOffset[] glyphOffsets)
        {
            glyphOffsets = null;
            throw NotSupported.Throw(nameof(GetGlyphPlacements));
        }

        internal void GetGlyphsAndTheirPlacements(
            char* textString, uint textLength, Font font, ushort blankGlyphIndex, bool isSideways,
            bool isRightToLeft, CultureInfo cultureInfo, DWriteFontFeature[][] features,
            uint[] featureRangeLengths, double fontEmSize, double scalingFactor, float pixelsPerDip,
            System.Windows.Media.TextFormattingMode textFormattingMode, ItemProps itemProps,
            out ushort[] clusterMap, out ushort[] glyphIndices, out int[] glyphAdvances, out GlyphOffset[] glyphOffsets) =>
            throw NotSupported.Throw(nameof(GetGlyphsAndTheirPlacements));
    }
}

namespace MS.Internal.Text.TextInterface.Interfaces
{
    using System.Runtime.InteropServices;

    /// <summary>
    /// DWriteInterfaces.h：[接口] DWrite 字体文件流镜像。
    /// 上游由 C++/CLI 声明为 COM 可见接口，PresentationCore 的
    /// <c>FontFileStream</c> 实现它；Linux 侧无 DWrite 回调，仅保留形状。
    /// </summary>
    [ComImport, Guid("5eaf3a3c-5e9b-4c1b-8a3a-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDWriteFontFileStreamMirror
    {
        [PreserveSig] int ReadFileFragment(out IntPtr fragmentStart, ulong fileOffset, ulong fragmentSize, out IntPtr fragmentContext);
        [PreserveSig] void ReleaseFileFragment(IntPtr fragmentContext);
        [PreserveSig] int GetFileSize(out ulong fileSize);
        [PreserveSig] int GetLastWriteTime(out long lastWriteTime);
    }

    /// <summary>DWriteInterfaces.h：[接口] DWrite 字体文件加载器镜像。</summary>
    [ComImport, Guid("727cad4e-d6af-4c9e-8a08-d695b11caa49"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IDWriteFontFileLoaderMirror
    {
        [PreserveSig] int CreateStreamFromKey(void* fontFileReferenceKey, uint fontFileReferenceKeySize, out IntPtr fontFileStream);
    }

    /// <summary>DWriteInterfaces.h：[接口] DWrite 字体文件枚举器镜像。</summary>
    [ComImport, Guid("727cad4e-d6af-4c9e-8a08-d695b11caa50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDWriteFontFileEnumeratorMirror
    {
        [PreserveSig] int MoveNext(out bool hasCurrentFile);
        [PreserveSig] int GetCurrentFontFile(out IntPtr fontFile);
    }
}

namespace MS.Internal.Text.TextInterface
{
    using MS.Internal.Text.TextInterface.Interfaces;

    /// <summary>
    /// FontFileLoader.h：[PNSE] 自定义字体文件加载器（上游是 COM 可见类，
    /// 供 DWrite 回调 WPF 的字体源）。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed unsafe class FontFileLoader : IDWriteFontFileLoaderMirror
    {
        private readonly IFontSourceFactory _fontSourceFactory;

        internal FontFileLoader() => throw NotSupported.Throw(nameof(FontFileLoader));

        internal FontFileLoader(IFontSourceFactory fontSourceFactory) => _fontSourceFactory = fontSourceFactory;

        public int CreateStreamFromKey(void* fontFileReferenceKey, uint fontFileReferenceKeySize, out IntPtr fontFileStream)
        {
            fontFileStream = IntPtr.Zero;
            throw NotSupported.Throw(nameof(CreateStreamFromKey));
        }
    }

    /// <summary>FontFileStream.h：[PNSE] 字体文件流（DWrite 回调实现）。</summary>
    internal sealed unsafe class FontFileStream : IDWriteFontFileStreamMirror
    {
        private readonly IFontSource _fontSource;

        internal FontFileStream(IFontSource fontSource) => _fontSource = fontSource;

        public int ReadFileFragment(out IntPtr fragmentStart, ulong fileOffset, ulong fragmentSize, out IntPtr fragmentContext)
        {
            fragmentStart = IntPtr.Zero;
            fragmentContext = IntPtr.Zero;
            throw NotSupported.Throw(nameof(ReadFileFragment));
        }
        public void ReleaseFileFragment(IntPtr fragmentContext) => throw NotSupported.Throw(nameof(ReleaseFileFragment));
        public int GetFileSize(out ulong fileSize)
        {
            fileSize = 0;
            throw NotSupported.Throw(nameof(GetFileSize));
        }
        public int GetLastWriteTime(out long lastWriteTime)
        {
            lastWriteTime = 0;
            throw NotSupported.Throw(nameof(GetLastWriteTime));
        }
    }

    /// <summary>FontFileEnumerator.h：[PNSE] 字体文件枚举器（DWrite 回调实现）。</summary>
    internal sealed unsafe class FontFileEnumerator : IDWriteFontFileEnumeratorMirror
    {
        private readonly IFontSource _unused;

        internal FontFileEnumerator() { }

        /// <summary>
        /// FontFileEnumerator.h：<c>(IEnumerable&lt;IFontSource&gt;^, FontFileLoader^, IDWriteFactory*)</c>。
        /// PresentationCore 的 FontCollectionLoader 用 (IFontSourceCollection, FontFileLoader, factory) 构造
        /// —— IFontSourceCollection 派生自 IEnumerable&lt;IFontSource&gt;，故参数按上游的 IEnumerable 声明。
        /// </summary>
        internal FontFileEnumerator(System.Collections.Generic.IEnumerable<IFontSource> fontSourceCollection,
                                    FontFileLoader fontFileLoader, IDWriteFactory* factory)
            => _unused = null;

        public int MoveNext(out bool hasCurrentFile)
        {
            hasCurrentFile = false;
            throw NotSupported.Throw(nameof(MoveNext));
        }

        public int GetCurrentFontFile(out IntPtr fontFile)
        {
            fontFile = IntPtr.Zero;
            throw NotSupported.Throw(nameof(GetCurrentFontFile));
        }
    }
}

namespace MS.Internal
{
    /// <summary>
    /// 上游 DirectWriteForwarder/main.cpp 里的 <c>MS::Internal::NativeWPFDLLLoader</c>：
    /// PresentationCore 的 ModuleInitializer 会调用 <see cref="LoadDwrite"/>。
    /// </summary>
    /// <remarks>
    /// [真实现 = no-op] 上游这个方法的实体只是给编译器一个"保留方法"的引用锚点
    /// （main.cpp 注释原文：Used to force the compiler to keep LoadDwrite in Release
    /// because it is called from PresentationCore），真正的 dwrite.dll 装载由托管侧
    /// DWriteLoader.LoadDWrite() 完成 —— 后者在 Linux 上会因找不到 dwrite.dll 抛
    /// DllNotFoundException（那才是诚实的行为）。故这里不伪造装载，只是空实现。
    /// </remarks>
    internal static class NativeWPFDLLLoader
    {
        internal static void LoadDwrite() { }
    }
}

namespace MS.Internal
{
    /// <summary>
    /// 上游 <c>CPP/TrueTypeSubsetter/truetype.h</c> 的字体子集化器
    /// （<c>public ref class TrueTypeSubsetter abstract sealed</c>，方法为 internal）。
    /// 上游注释说明「类声明为 public 是为了防止 Release 优化掉，方法 internal 防止 WPF 之外的调用者使用」。
    /// </summary>
    /// <remarks>
    /// [PNSE] PresentationCore 的 <c>FontDriver.ComputeFontSubset</c> 调用它。
    /// Linux 侧子集化应由 M1 字体栈承担；这里抛异常而不是返回未子集化的整份字体
    /// —— 返回整份字体属于"静默改变语义"，本项目明确拒绝。
    /// </remarks>
    public static unsafe class TrueTypeSubsetter
    {
        internal static byte[] ComputeSubset(void* fontData, int fileSize, Uri sourceUri, int directoryOffset, ushort[] glyphArray)
            => MS.Internal.Text.TextInterface.NotSupported.Throw<byte[]>(
                nameof(ComputeSubset) + "：TrueType 子集化需要重写 glyf/loca/cmap 表，" +
                "且只有 XPS/IDeviceFont 序列化路径需要（FontDriver.cs:263），不影响屏幕渲染");
    }
}
