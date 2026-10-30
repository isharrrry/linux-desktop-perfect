// T2 · Phase 2 —— provider（MS.Internal.Text.TextInterface.Linux）↔ 骨架（本程序集）的适配层
// =====================================================================================
// 【为什么需要它】
//   骨架的成员签名是上游 C++/CLI 的形态（FontMetrics 类、GlyphMetrics 显式布局结构体、
//   FontWeight/FontStretch/FontStyle 枚举），provider 那边是等价但独立的托管类型
//   （FontMetricsData/GlyphMetricsData + int）。两边刻意不共享类型：
//     · provider 只依赖 SkiaSharp，不认识 WindowsBase 的 TextFormattingMode，
//       也不认识骨架的枚举（骨架的枚举是上游头文件形态，provider 不该反向依赖它）；
//     · 骨架要保持上游形态（PresentationCore 按类型名与字段布局引用它）。
//   于是映射必须显式写出来 —— 这也正好是"哪几个字段对哪几个字段"的可审计清单。
//
// 【本文件只做三件事】
//   ① 数据映射（provider 的 *Data → 骨架的 FontMetrics/GlyphMetrics/LocalizedStrings）
//   ② 枚举映射（provider 用 int，骨架用上游枚举；字面值逐项对齐）
//   ③ 句柄反查（PresentationCore 传回来的"原生指针"其实是我们自己发的令牌）
//   没有任何字体逻辑 —— 逻辑只有一份，在 provider 里（并被三方交叉验证过）。
//
// 【"未接线"的诚实出口】
//   指针构造路径（Factory 的原生化路径，见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WIRING.md §0.2）
//   拿不到托管对象时，一律抛 NotWired 的异常，**不返回 0/null 假装成功**。
//   这与骨架原有的 [PNSE] 口径一致：宁可响亮地失败，不要静默的半成品。

using System;
using System.Collections.Generic;
using System.Globalization;
using MS.Internal.Text.TextInterface.Linux;

namespace MS.Internal.Text.TextInterface
{
    /// <summary>"这条路径还没接线"的统一出口（与 NotSupported 区分：后者是"本轮不做"）。</summary>
    internal static class NotWired
    {
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static Exception Throw(string member) =>
            throw new PlatformNotSupportedException(
                member + "：该对象不是由托管 provider 构造的（走的是原生指针路径）。" +
                "Linux 侧没有原生 DWrite 对象可解引用 —— 需要先完成 Factory 的托管化，" +
                "见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WIRING.md §0.2（#0-a/#0-b）。");

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        internal static T Throw<T>(string member) => throw Throw(member);
    }

    /// <summary>provider 数据 → 骨架类型的映射。</summary>
    internal static class ProviderAdapters
    {
        // ---------------------------------------------------------------------------------
        //  度量
        // ---------------------------------------------------------------------------------

        /// <summary>FontMetricsData → 骨架的 FontMetrics（逐字段同名同型，10 个字段一个不少）。</summary>
        internal static FontMetrics ToFontMetrics(FontMetricsData source)
        {
            if (source == null) return null;

            return new FontMetrics
            {
                DesignUnitsPerEm = source.DesignUnitsPerEm,
                Ascent = source.Ascent,
                Descent = source.Descent,
                LineGap = source.LineGap,
                CapHeight = source.CapHeight,
                XHeight = source.XHeight,
                UnderlinePosition = source.UnderlinePosition,
                UnderlineThickness = source.UnderlineThickness,
                StrikethroughPosition = source.StrikethroughPosition,
                StrikethroughThickness = source.StrikethroughThickness,
            };
        }

        /// <summary>
        /// GlyphMetricsData → 骨架的 GlyphMetrics
        /// （[StructLayout(Explicit, Size=28)] 的 7 个字段，字段偏移与 DWRITE_GLYPH_METRICS 一致）。
        /// </summary>
        internal static GlyphMetrics ToGlyphMetrics(in GlyphMetricsData source)
        {
            var result = new GlyphMetrics();
            result.LeftSideBearing = source.LeftSideBearing;
            result.AdvanceWidth = source.AdvanceWidth;
            result.RightSideBearing = source.RightSideBearing;
            result.TopSideBearing = source.TopSideBearing;
            result.AdvanceHeight = source.AdvanceHeight;
            result.BottomSideBearing = source.BottomSideBearing;
            result.VerticalOriginY = source.VerticalOriginY;
            return result;
        }

        /// <summary>把一批 provider 度量写进调用方的非托管缓冲区（PresentationCore 传进来的 pGlyphMetrics）。</summary>
        internal static unsafe void WriteGlyphMetrics(GlyphMetricsData[] source, int count, GlyphMetrics* destination)
        {
            for (int i = 0; i < count; i++)
            {
                GlyphMetrics value = ToGlyphMetrics(source[i]);
                destination[i] = value;
            }
        }

        /// <summary>LocalizedStringsData → 骨架的 LocalizedStrings（两边都是只读 IDictionary）。</summary>
        internal static LocalizedStrings ToLocalizedStrings(LocalizedStringsData source)
        {
            if (source == null) return null;

            CultureInfo[] keys = source.KeysArray;
            string[] values = source.ValuesArray;
            return new LocalizedStrings(keys, values);
        }

        // ---------------------------------------------------------------------------------
        //  枚举（provider 用 int / FontFaceKind，骨架用上游枚举；字面值逐项对齐）
        // ---------------------------------------------------------------------------------

        internal static FontFaceType ToFontFaceType(FontFaceKind kind) => kind switch
        {
            FontFaceKind.CFF => FontFaceType.CFF,
            FontFaceKind.TrueType => FontFaceType.TrueType,
            FontFaceKind.TrueTypeCollection => FontFaceType.TrueTypeCollection,
            FontFaceKind.Type1 => FontFaceType.Type1,
            FontFaceKind.Vector => FontFaceType.Vector,
            FontFaceKind.Bitmap => FontFaceType.Bitmap,
            _ => FontFaceType.Unknown,
        };

        internal static FontFileType ToFontFileType(FontFileKind kind) => kind switch
        {
            FontFileKind.CFF => FontFileType.CFF,
            FontFileKind.TrueType => FontFileType.TrueType,
            FontFileKind.TrueTypeCollection => FontFileType.TrueTypeCollection,
            FontFileKind.Type1PFM => FontFileType.Type1PFM,
            FontFileKind.Type1PFB => FontFileType.Type1PFB,
            FontFileKind.Vector => FontFileType.Vector,
            FontFileKind.Bitmap => FontFileType.Bitmap,
            _ => FontFileType.Unknown,
        };

        /// <summary>骨架枚举 → Native 镜像枚举（FontFile.Analyze 的三个 out 参数要用）。</summary>
        internal static Native.DWRITE_FONT_FILE_TYPE ToDwriteFileType(FontFileKind kind)
        {
            switch (kind)
            {
                case FontFileKind.CFF: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_CFF;
                case FontFileKind.TrueType: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TRUETYPE;
                case FontFileKind.TrueTypeCollection: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_OPENTYPE_COLLECTION;
                case FontFileKind.Type1PFM: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TYPE1_PFM;
                case FontFileKind.Type1PFB: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_TYPE1_PFB;
                case FontFileKind.Vector: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_VECTOR;
                case FontFileKind.Bitmap: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_BITMAP;
                default: return Native.DWRITE_FONT_FILE_TYPE.DWRITE_FONT_FILE_TYPE_UNKNOWN;
            }
        }

        internal static Native.DWRITE_FONT_FACE_TYPE ToDwriteFaceType(FontFaceKind kind)
        {
            switch (kind)
            {
                case FontFaceKind.CFF: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_CFF;
                case FontFaceKind.TrueType: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TRUETYPE;
                case FontFaceKind.TrueTypeCollection: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_OPENTYPE_COLLECTION;
                case FontFaceKind.Type1: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_TYPE1;
                case FontFaceKind.Vector: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_VECTOR;
                case FontFaceKind.Bitmap: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_BITMAP;
                default: return Native.DWRITE_FONT_FACE_TYPE.DWRITE_FONT_FACE_TYPE_UNKNOWN;
            }
        }

        internal static FontSimulations ToFontSimulations(int flags) => (FontSimulations)flags;
        internal static int FromFontSimulations(FontSimulations flags) => (int)flags;

        // ---------------------------------------------------------------------------------
        //  句柄反查
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// 把"原生形态的字体面指针"还原成托管字体面。
        ///
        /// PresentationCore 只会把**我们自己发出去的**令牌以 (IDWriteFontFace*) 的形式传回来
        /// （Factory.cs:227 的 `new FontFace((Native.IDWriteFontFace*)dwriteFontFace)`、
        ///  GlyphTypeface.cs:1264 的 `fontFaceDWrite.DWriteFontFaceAddRef`），
        /// 所以这里做的是"令牌 → 对象"的反查，而不是解引用原生指针。
        /// 反查不到 → 抛（**不猜字体**，理由见 src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Text/MilGlyphRunAdapter.cs:1-18）。
        /// </summary>
        internal static unsafe LinuxFontFace ResolveFontFace(Native.IDWriteFontFace* pointer)
        {
            IntPtr token = (IntPtr)pointer;
            if (token == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer), "字体面指针为 null");

            if (FontHandleTable.TryResolveFace(token, out LinuxFontFace face)) return face;

            throw new InvalidOperationException(
                "这不是本进程发出的字体面令牌（0x" + token.ToInt64().ToString("X", CultureInfo.InvariantCulture) + "）。" +
                "Linux 侧没有原生 DWrite 对象可以解引用 —— 见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WIRING.md §0.2。");
        }
    }
}
