// DirectWriteForwarder · Linux 托管骨架 —— Native 命名空间镜像
// =====================================================================================
// 为什么需要这个文件
// ------------------
// 上游是 C++/CLI：`CPP/DWriteWrapper/Common.h` 里开的是
//     namespace MS { namespace Internal { namespace Text { namespace TextInterface { namespace Native
// 而 `IDWriteFactory*` / `IDWriteFontFace*` 这些在 C++/CLI 里是**原生** dwrite.h 接口指针。
// PresentationCore 的 C# 侧却直接写：
//     Native.IDWriteFontFile* dwriteFontFile = null;
//     new FontFace((Native.IDWriteFontFace*)dwriteFontFace);
//     Native.DWRITE_FONT_FILE_TYPE dwriteFontFileType;   // out 参数
// 也就是说：C# 编译器必须在**引用程序集**里找到一组托管类型，名字与命名空间与上面完全一致
// （指针参数用 `&` 取址、out 参数都必须精确匹配类型，不能退化成 void*）。
//
// 因此这里声明最小的托管镜像：
//   * 接口镜像 = 只有 vtable 指针字段的 struct（与 PresentationCore 自己的
//     MS.Internal.Interop.DWrite.* 镜像同构，两者之间用显式指针转换互通，C# 允许任意
//     指针类型间的显式转换）；
//   * 枚举镜像 = 值逐项对齐 PresentationCore 的 MS.Internal.Interop.DWrite 同名枚举
//     （in-tree oracle，见各枚举下的注释），保证 DWriteTypeConverter 的互转语义正确。
//
// 本文件不含任何 DWrite 调用：镜像只用于「让指针类型有名字」。
namespace MS.Internal.Text.TextInterface.Native
{
    // ---------------------------------------------------------------------------------
    // COM 接口镜像：唯一字段是 vtable 指针（8 字节），与原生接口指针布局一致。
    // 这些类型在 Linux 上不会被解引用（没有任何 DWrite 运行时），只出现在签名与转换里。
    // ---------------------------------------------------------------------------------
    internal unsafe struct IUnknown
    {
        public void** lpVtbl;
    }

    internal unsafe struct IDWriteFactory { public void** lpVtbl; }
    internal unsafe struct IDWriteFont { public void** lpVtbl; }
    internal unsafe struct IDWriteFontList { public void** lpVtbl; }
    internal unsafe struct IDWriteFontFamily { public void** lpVtbl; }
    internal unsafe struct IDWriteFontCollection { public void** lpVtbl; }
    internal unsafe struct IDWriteFontFace { public void** lpVtbl; }
    internal unsafe struct IDWriteFontFile { public void** lpVtbl; }
    internal unsafe struct IDWriteFontFileStream { public void** lpVtbl; }
    internal unsafe struct IDWriteFontFileLoader { public void** lpVtbl; }
    internal unsafe struct IDWriteLocalFontFileLoader { public void** lpVtbl; }
    internal unsafe struct IDWriteFontCollectionLoader { public void** lpVtbl; }
    internal unsafe struct IDWriteTextAnalyzer { public void** lpVtbl; }
    internal unsafe struct IDWriteNumberSubstitution { public void** lpVtbl; }
    internal unsafe struct IDWriteTextAnalysisSource { public void** lpVtbl; }
    internal unsafe struct IDWriteTextAnalysisSink { public void** lpVtbl; }

    // ---------------------------------------------------------------------------------
    // 枚举镜像：值取自 PresentationCore 的 in-tree 镜像（MS/internal/Interop/DWrite/*.cs），
    // 那是同一份 win32 元数据的另一份拷贝，是本地可用的权威口径。
    // ---------------------------------------------------------------------------------

    /// <summary>DWRITE_FACTORY_TYPE（Shared=0 / Isolated=1）。</summary>
    internal enum DWRITE_FACTORY_TYPE
    {
        DWRITE_FACTORY_TYPE_SHARED,
        DWRITE_FACTORY_TYPE_ISOLATED,
    }

    /// <summary>DWRITE_FONT_SIMULATIONS（None/Bold/Oblique 位标志）。</summary>
    internal enum DWRITE_FONT_SIMULATIONS : uint
    {
        DWRITE_FONT_SIMULATIONS_NONE = 0x0000,
        DWRITE_FONT_SIMULATIONS_BOLD = 0x0001,
        DWRITE_FONT_SIMULATIONS_OBLIQUE = 0x0002,
    }

    /// <summary>DWRITE_FONT_FILE_TYPE（对齐 PresentationCore 镜像 DWRITE_FONT_FILE_TYPE）。</summary>
    internal enum DWRITE_FONT_FILE_TYPE
    {
        DWRITE_FONT_FILE_TYPE_UNKNOWN,
        DWRITE_FONT_FILE_TYPE_CFF,
        DWRITE_FONT_FILE_TYPE_TRUETYPE,
        DWRITE_FONT_FILE_TYPE_OPENTYPE_COLLECTION,
        DWRITE_FONT_FILE_TYPE_TYPE1_PFM,
        DWRITE_FONT_FILE_TYPE_TYPE1_PFB,
        DWRITE_FONT_FILE_TYPE_VECTOR,
        DWRITE_FONT_FILE_TYPE_BITMAP,
        DWRITE_FONT_FILE_TYPE_TRUETYPE_COLLECTION = DWRITE_FONT_FILE_TYPE_OPENTYPE_COLLECTION,
    }

    /// <summary>DWRITE_FONT_FACE_TYPE（对齐 PresentationCore 镜像 DWRITE_FONT_FACE_TYPE）。</summary>
    internal enum DWRITE_FONT_FACE_TYPE
    {
        DWRITE_FONT_FACE_TYPE_CFF,
        DWRITE_FONT_FACE_TYPE_TRUETYPE,
        DWRITE_FONT_FACE_TYPE_OPENTYPE_COLLECTION,
        DWRITE_FONT_FACE_TYPE_TYPE1,
        DWRITE_FONT_FACE_TYPE_VECTOR,
        DWRITE_FONT_FACE_TYPE_BITMAP,
        DWRITE_FONT_FACE_TYPE_UNKNOWN,
        DWRITE_FONT_FACE_TYPE_RAW_CFF,
        DWRITE_FONT_FACE_TYPE_TRUETYPE_COLLECTION = DWRITE_FONT_FACE_TYPE_OPENTYPE_COLLECTION,
    }

    /// <summary>DWRITE_FONT_WEIGHT（100..950，与 FontWeight.h 的托管枚举同值）。</summary>
    internal enum DWRITE_FONT_WEIGHT
    {
        DWRITE_FONT_WEIGHT_THIN = 100,
        DWRITE_FONT_WEIGHT_EXTRA_LIGHT = 200,
        DWRITE_FONT_WEIGHT_ULTRA_LIGHT = 200,
        DWRITE_FONT_WEIGHT_LIGHT = 300,
        DWRITE_FONT_WEIGHT_SEMI_LIGHT = 350,
        DWRITE_FONT_WEIGHT_NORMAL = 400,
        DWRITE_FONT_WEIGHT_REGULAR = 400,
        DWRITE_FONT_WEIGHT_MEDIUM = 500,
        DWRITE_FONT_WEIGHT_DEMI_BOLD = 600,
        DWRITE_FONT_WEIGHT_SEMI_BOLD = 600,
        DWRITE_FONT_WEIGHT_BOLD = 700,
        DWRITE_FONT_WEIGHT_EXTRA_BOLD = 800,
        DWRITE_FONT_WEIGHT_ULTRA_BOLD = 800,
        DWRITE_FONT_WEIGHT_BLACK = 900,
        DWRITE_FONT_WEIGHT_HEAVY = 900,
        DWRITE_FONT_WEIGHT_EXTRA_BLACK = 950,
        DWRITE_FONT_WEIGHT_ULTRA_BLACK = 950,
    }

    /// <summary>DWRITE_FONT_STRETCH（1..9，与 FontStretch.h 同值）。</summary>
    internal enum DWRITE_FONT_STRETCH
    {
        DWRITE_FONT_STRETCH_UNDEFINED = 0,
        DWRITE_FONT_STRETCH_ULTRA_CONDENSED = 1,
        DWRITE_FONT_STRETCH_EXTRA_CONDENSED = 2,
        DWRITE_FONT_STRETCH_CONDENSED = 3,
        DWRITE_FONT_STRETCH_SEMI_CONDENSED = 4,
        DWRITE_FONT_STRETCH_NORMAL = 5,
        DWRITE_FONT_STRETCH_MEDIUM = 5,
        DWRITE_FONT_STRETCH_SEMI_EXPANDED = 6,
        DWRITE_FONT_STRETCH_EXPANDED = 7,
        DWRITE_FONT_STRETCH_EXTRA_EXPANDED = 8,
        DWRITE_FONT_STRETCH_ULTRA_EXPANDED = 9,
    }

    /// <summary>DWRITE_FONT_STYLE（Normal=0 / Oblique=1 / Italic=2，与 FontStyle.h 同值）。</summary>
    internal enum DWRITE_FONT_STYLE
    {
        DWRITE_FONT_STYLE_NORMAL = 0,
        DWRITE_FONT_STYLE_OBLIQUE = 1,
        DWRITE_FONT_STYLE_ITALIC = 2,
    }

    /// <summary>DWRITE_INFORMATIONAL_STRING_ID（与 InformationalStringID.h 的托管枚举同序）。</summary>
    internal enum DWRITE_INFORMATIONAL_STRING_ID
    {
        DWRITE_INFORMATIONAL_STRING_NONE,
        DWRITE_INFORMATIONAL_STRING_COPYRIGHT_NOTICE,
        DWRITE_INFORMATIONAL_STRING_VERSION_STRINGS,
        DWRITE_INFORMATIONAL_STRING_TRADEMARK,
        DWRITE_INFORMATIONAL_STRING_MANUFACTURER,
        DWRITE_INFORMATIONAL_STRING_DESIGNER,
        DWRITE_INFORMATIONAL_STRING_DESIGNER_URL,
        DWRITE_INFORMATIONAL_STRING_DESCRIPTION,
        DWRITE_INFORMATIONAL_STRING_FONT_VENDOR_URL,
        DWRITE_INFORMATIONAL_STRING_LICENSE_DESCRIPTION,
        DWRITE_INFORMATIONAL_STRING_LICENSE_INFO_URL,
        DWRITE_INFORMATIONAL_STRING_WIN32_FAMILY_NAMES,
        DWRITE_INFORMATIONAL_STRING_WIN32_SUBFAMILY_NAMES,
        DWRITE_INFORMATIONAL_STRING_PREFERRED_FAMILY_NAMES,
        DWRITE_INFORMATIONAL_STRING_PREFERRED_SUBFAMILY_NAMES,
        DWRITE_INFORMATIONAL_STRING_SAMPLE_TEXT,
    }

    /// <summary>DWRITE_MEASURING_MODE（Natural=0 / GDI_CLASSIC=1 / GDI_NATURAL=2）。</summary>
    internal enum DWRITE_MEASURING_MODE
    {
        DWRITE_MEASURING_MODE_NATURAL,
        DWRITE_MEASURING_MODE_GDI_CLASSIC,
        DWRITE_MEASURING_MODE_GDI_NATURAL,
    }

    /// <summary>DWRITE_SCRIPT_SHAPES（Default=0 / NoVisual=1）。</summary>
    internal enum DWRITE_SCRIPT_SHAPES : uint
    {
        DWRITE_SCRIPT_SHAPES_DEFAULT = 0,
        DWRITE_SCRIPT_SHAPES_NO_VISUAL = 1,
    }

    /// <summary>DWRITE_TEXTURE_TYPE（Aliased1bpp=0 / ClearType3x1bpp=1）。仅签名占位。</summary>
    internal enum DWRITE_TEXTURE_TYPE
    {
        DWRITE_TEXTURE_ALIASED_1x1,
        DWRITE_TEXTURE_CLEARTYPE_3x1,
    }

    // ---------------------------------------------------------------------------------
    // 结构体镜像（布局取自 dwrite.h / PresentationCore 镜像；仅签名与 out 参数使用）
    // ---------------------------------------------------------------------------------

    /// <summary>DWRITE_MATRIX（3x2 仿射矩阵，6×float = 24 字节）。</summary>
    internal struct DWRITE_MATRIX
    {
        public float M11, M12, M21, M22, dx, dy;
    }

    /// <summary>DWRITE_SCRIPT_ANALYSIS（script:ushort / shapes:uint → 8 字节）。</summary>
    internal struct DWRITE_SCRIPT_ANALYSIS
    {
        public ushort script;
        public uint shapes;
    }

    /// <summary>DWRITE_GLYPH_OFFSET（2×float = 8 字节）。</summary>
    internal struct DWRITE_GLYPH_OFFSET
    {
        public float advanceOffset;
        public float ascenderOffset;
    }

    /// <summary>DWRITE_FONT_METRICS（dwrite.h：11 个字段，含 padding，共 32 字节）。</summary>
    internal struct DWRITE_FONT_METRICS
    {
        public ushort designUnitsPerEm;
        public ushort ascent;
        public ushort descent;
        public short lineGap;
        public ushort capHeight;
        public ushort xHeight;
        public short underlinePosition;
        public ushort underlineThickness;
        public short strikethroughPosition;
        public ushort strikethroughThickness;
    }
}
