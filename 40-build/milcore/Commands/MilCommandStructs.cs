// Licensed to the .NET Foundation under one or more agreements.
//
// DUCE 命令的**字节布局**定义。
//
// 【权威来源】
//   src/Microsoft.DotNet.Wpf/src/Common/Graphics/Generated/wgx_commands.cs
//
// 本文件是那个文件的逐字段复制：StructLayout(Explicit, Pack=1) + 每个 FieldOffset
// 原样照搬，只把 DUCE.ResourceHandle 换成 WpfGfx.Linux.Contracts 的同名类型，
// 把 MS.Win32.NativeMethods.RECT 换成 MilRectI，把上游 internal 的可见性改成 public。
//
// **不要凭猜测修改任何 FieldOffset。** 所有偏移由 tests/WpfGfx.Linux.Tests/
// CommandLayoutTests.cs 用 Marshal.OffsetOf 逐项校验。
//
// 注意：handoff §3.2 写「命令类型 1 字节」是错的。上游生成的结构里
// MilCmd 是 C# 默认 int 枚举，占 0..3 字节，Handle 从偏移 4 开始。

using System.Runtime.InteropServices;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Commands
{
    internal static class MilCommandStructs
    {
        // ---------- 分区 / 传输 (0x03–0x06, 0x3d) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PARTITION_REGISTERFORNOTIFICATIONS
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public uint Enable;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_CHANNEL_REQUESTTIER
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public uint ReturnCommonMinimum;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PARTITION_SETVBLANKSYNCMODE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public uint Enable;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PARTITION_NOTIFYPRESENT
        {
            [FieldOffset(0)] public MilCmd Type;
            // 上游 wgx_commands.cs:44 是 FieldOffset(4)（Pack=1，故 UInt64 不需要 8 字节对齐）。
            // 此处曾误写为 8，导致该命令整体长度多算 4 字节。
            [FieldOffset(4)] public ulong FrameTime;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PARTITION_NOTIFYPOLICYCHANGEFORNONINTERACTIVEMODE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public uint ShouldRenderEvenWhenNoDisplayDevicesAreAvailable;
        }

        // ---------- 位图源 (0x0c, 0x0d) ----------
        //
        // ⚠️ 这两条的**上游权威源不是 C# 版 wgx_commands.cs，而是 C++ 头文件**：
        //   WpfGfx/include/Generated/wgx_commands.h:86（MILCMD_BITMAP_SOURCE）
        //   WpfGfx/include/Generated/wgx_commands.h:95（MILCMD_BITMAP_INVALIDATE）
        // 上游的 C# 生成版（Common/Graphics/Generated/wgx_commands.cs）里
        // MILCMD_BITMAP_SOURCE **根本不存在**——它只在 C++ 侧有定义，走独立的原生封送路径；
        // MILCMD_BITMAP_INVALIDATE 则两边都有（C# 版在 :62）。
        // 所以下面 BITMAP_SOURCE 的偏移只能照 C++ 头文件抄，没有 C# 版可比对。

        /// <summary>
        /// 上游：`MILCMD Type; HMIL_RESOURCE Handle; IWICBitmapSource* pIBitmap;`
        ///
        /// 【Linux 侧对第 3 个字段的重新定义】
        /// 上游这一列是 IWICBitmapSource 的 COM 接口指针，靠「发送前 AddRef、
        /// 接收方接手该引用」的协议在**同一进程内**传递（WpfGfx/core/uce/apifunc.cpp:739
        /// 的注释："This reference keeps it alive during transport and will be passed
        /// onto the slave bitmap resource"）。Linux 上没有 WIC，且本实现的命令流是
        /// 纯粹字节流（解码器输入是 ReadOnlySpan&lt;byte&gt;），跨进程或重放时
        /// 进程地址毫无意义。
        ///
        /// 因此这里把同样 8 字节的列重新定义为 **位图令牌（BitmapToken）**：
        /// 进程内位图登记表的一个键，语义与上游的引用一一对应——
        ///   上游：AddRef → 传输 → 接收方接手引用 → 替换时释放旧的
        ///   本实现：Register → 传输 → 解码方 Take（取出并注销）→ 替换时 Dispose 旧的
        /// 字节布局（4+4+8=16）与上游完全一致，只是这 8 个字节的解释变了。
        /// 令牌 0 恒为非法值，对应上游 CHECKPTRARG(pIBitmapSource) 的空指针检查。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_BITMAP_SOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public ulong BitmapToken;
        }

        /// <summary>
        /// 上游：`MILCMD Type; HMIL_RESOURCE Handle; BOOL UseDirtyRect; RECT DirtyRect;`
        /// 对应 C# 版 wgx_commands.cs:62 的 MILCMD_BITMAP_INVALIDATE
        /// （DirtyRect 的类型 MS.Win32.NativeMethods.RECT 换成 MilRectI，两者都是 4×int32）。
        /// 4+4+4+16 = 28 字节。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_BITMAP_INVALIDATE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint UseDirtyRect;
            [FieldOffset(12)] public MilRectI DirtyRect;
        }

        // ---------- 标量资源 (0x0e–0x16, 0x19) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DOUBLERESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_COLORRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_POINTRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilPoint Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_RECTRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRect Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SIZERESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilSize Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MATRIXRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilMatrix3x2D Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_POINT3DRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilPoint3F Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VECTOR3DRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilPoint3F Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_QUATERNIONRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilQuaternionF Value;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_ETWEVENTRESOURCE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint Id;
        }

        // ---------- RenderData (0x18) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_RENDERDATA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint CbData;
        }

        // ---------- Visual (0x1b–0x28) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETOFFSET
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double OffsetX;
            [FieldOffset(16)] public double OffsetY;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETTRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETEFFECT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HEffect;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETCACHEMODE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HCacheMode;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETCLIP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HClip;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETALPHA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Alpha;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETRENDEROPTIONS
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRenderOptions RenderOptions;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETCONTENT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HContent;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETALPHAMASK
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HAlphaMask;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_REMOVEALLCHILDREN
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_REMOVECHILD
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HChild;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_INSERTCHILDAT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HChild;
            [FieldOffset(12)] public uint Index;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETGUIDELINECOLLECTION
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public ushort CountX;
            [FieldOffset(12)] public ushort CountY;
            [FieldOffset(15)] private byte BYTEPacking0;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL_SETSCROLLABLEAREACLIP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRect Clip;
            [FieldOffset(40)] public uint IsEnabled;
        }

        // ---------- Target (0x31–0x39) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_HWNDTARGET_CREATE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public ulong Hwnd;
            [FieldOffset(16)] public ulong HSection;
            [FieldOffset(24)] public ulong MasterDevice;
            [FieldOffset(32)] public uint Width;
            [FieldOffset(36)] public uint Height;
            [FieldOffset(40)] public MilColorF ClearColor;
            [FieldOffset(56)] public uint Flags;
            [FieldOffset(60)] public DUCE.ResourceHandle HBitmap;
            [FieldOffset(64)] public uint Stride;
            [FieldOffset(68)] public uint EPixelFormat;
            [FieldOffset(72)] public int DpiAwarenessContext;
            [FieldOffset(76)] public double DpiX;
            [FieldOffset(84)] public double DpiY;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_HWNDTARGET_SUPPRESSLAYERED
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint Suppress;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TARGET_UPDATEWINDOWSETTINGS
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRectI WindowRect;
            [FieldOffset(24)] public MilWindowLayerType WindowLayerType;
            [FieldOffset(28)] public MilTransparencyFlags TransparencyMode;
            [FieldOffset(32)] public float ConstantAlpha;
            [FieldOffset(36)] public uint IsChild;
            [FieldOffset(40)] public uint IsRTL;
            [FieldOffset(44)] public uint RenderingEnabled;
            [FieldOffset(48)] public MilColorF ColorKey;
            [FieldOffset(64)] public uint DisableCookie;
            [FieldOffset(68)] public uint GdiBlt;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GENERICTARGET_CREATE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public ulong Hwnd;
            [FieldOffset(16)] public ulong PRenderTarget;
            [FieldOffset(24)] public uint Width;
            [FieldOffset(28)] public uint Height;
            [FieldOffset(32)] public uint Dummy;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TARGET_SETROOT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HRoot;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TARGET_SETCLEARCOLOR
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF ClearColor;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TARGET_INVALIDATE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRectI Rc;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TARGET_SETFLAGS
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint Flags;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_HWNDTARGET_DPICHANGED
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double DpiX;
            [FieldOffset(16)] public double DpiY;
            [FieldOffset(24)] public uint AfterParent;
        }

        // ---------- GlyphRun (0x3a) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GLYPHRUN_CREATE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public ulong PIDWriteFont;
            [FieldOffset(16)] public ushort GlyphRunFlags;
            [FieldOffset(20)] public MilPoint2F Origin;
            [FieldOffset(28)] public float MuSize;
            [FieldOffset(32)] public MilRect ManagedBounds;
            [FieldOffset(64)] public ushort GlyphCount;
            [FieldOffset(68)] public ushort BidiLevel;
            [FieldOffset(72)] public ushort DWriteTextMeasuringMethod;
            [FieldOffset(75)] private byte BYTEPacking0;
        }

        // ---------- 变换 (0x72–0x77) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TRANSFORMGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint ChildrenSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TRANSLATETRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double X;
            [FieldOffset(16)] public double Y;
            [FieldOffset(24)] public DUCE.ResourceHandle HXAnimations;
            [FieldOffset(28)] public DUCE.ResourceHandle HYAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SCALETRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double ScaleX;
            [FieldOffset(16)] public double ScaleY;
            [FieldOffset(24)] public double CenterX;
            [FieldOffset(32)] public double CenterY;
            [FieldOffset(40)] public DUCE.ResourceHandle HScaleXAnimations;
            [FieldOffset(44)] public DUCE.ResourceHandle HScaleYAnimations;
            [FieldOffset(48)] public DUCE.ResourceHandle HCenterXAnimations;
            [FieldOffset(52)] public DUCE.ResourceHandle HCenterYAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SKEWTRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double AngleX;
            [FieldOffset(16)] public double AngleY;
            [FieldOffset(24)] public double CenterX;
            [FieldOffset(32)] public double CenterY;
            [FieldOffset(40)] public DUCE.ResourceHandle HAngleXAnimations;
            [FieldOffset(44)] public DUCE.ResourceHandle HAngleYAnimations;
            [FieldOffset(48)] public DUCE.ResourceHandle HCenterXAnimations;
            [FieldOffset(52)] public DUCE.ResourceHandle HCenterYAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_ROTATETRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Angle;
            [FieldOffset(16)] public double CenterX;
            [FieldOffset(24)] public double CenterY;
            [FieldOffset(32)] public DUCE.ResourceHandle HAngleAnimations;
            [FieldOffset(36)] public DUCE.ResourceHandle HCenterXAnimations;
            [FieldOffset(40)] public DUCE.ResourceHandle HCenterYAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MATRIXTRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilMatrix3x2D Matrix;
            [FieldOffset(56)] public DUCE.ResourceHandle HMatrixAnimations;
        }

        // ---------- 几何 (0x78–0x7d) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_LINEGEOMETRY
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilPoint StartPoint;
            [FieldOffset(24)] public MilPoint EndPoint;
            [FieldOffset(40)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(44)] public DUCE.ResourceHandle HStartPointAnimations;
            [FieldOffset(48)] public DUCE.ResourceHandle HEndPointAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_RECTANGLEGEOMETRY
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double RadiusX;
            [FieldOffset(16)] public double RadiusY;
            [FieldOffset(24)] public MilRect Rect;
            [FieldOffset(56)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(60)] public DUCE.ResourceHandle HRadiusXAnimations;
            [FieldOffset(64)] public DUCE.ResourceHandle HRadiusYAnimations;
            [FieldOffset(68)] public DUCE.ResourceHandle HRectAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_ELLIPSEGEOMETRY
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double RadiusX;
            [FieldOffset(16)] public double RadiusY;
            [FieldOffset(24)] public MilPoint Center;
            [FieldOffset(40)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(44)] public DUCE.ResourceHandle HRadiusXAnimations;
            [FieldOffset(48)] public DUCE.ResourceHandle HRadiusYAnimations;
            [FieldOffset(52)] public DUCE.ResourceHandle HCenterAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GEOMETRYGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(12)] public MilFillRule FillRule;
            [FieldOffset(16)] public uint ChildrenSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_COMBINEDGEOMETRY
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(12)] public MilGeometryCombineMode GeometryCombineMode;
            [FieldOffset(16)] public DUCE.ResourceHandle HGeometry1;
            [FieldOffset(20)] public DUCE.ResourceHandle HGeometry2;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PATHGEOMETRY
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(12)] public MilFillRule FillRule;
            [FieldOffset(16)] public uint FiguresSize;
        }

        // ---------- 画刷 (0x6d, 0x7e–0x84) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_IMPLICITINPUTBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(20)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(24)] public DUCE.ResourceHandle HRelativeTransform;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SOLIDCOLORBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilColorF Color;
            [FieldOffset(32)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(36)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(40)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(44)] public DUCE.ResourceHandle HColorAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_LINEARGRADIENTBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilPoint StartPoint;
            [FieldOffset(32)] public MilPoint EndPoint;
            [FieldOffset(48)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(52)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(56)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(60)] public MilColorInterpolationMode ColorInterpolationMode;
            [FieldOffset(64)] public MilBrushMappingMode MappingMode;
            [FieldOffset(68)] public MilGradientSpreadMethod SpreadMethod;
            [FieldOffset(72)] public uint GradientStopsSize;
            [FieldOffset(76)] public DUCE.ResourceHandle HStartPointAnimations;
            [FieldOffset(80)] public DUCE.ResourceHandle HEndPointAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_RADIALGRADIENTBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilPoint Center;
            [FieldOffset(32)] public double RadiusX;
            [FieldOffset(40)] public double RadiusY;
            [FieldOffset(48)] public MilPoint GradientOrigin;
            [FieldOffset(64)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(68)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(72)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(76)] public MilColorInterpolationMode ColorInterpolationMode;
            [FieldOffset(80)] public MilBrushMappingMode MappingMode;
            [FieldOffset(84)] public MilGradientSpreadMethod SpreadMethod;
            [FieldOffset(88)] public uint GradientStopsSize;
            [FieldOffset(92)] public DUCE.ResourceHandle HCenterAnimations;
            [FieldOffset(96)] public DUCE.ResourceHandle HRadiusXAnimations;
            [FieldOffset(100)] public DUCE.ResourceHandle HRadiusYAnimations;
            [FieldOffset(104)] public DUCE.ResourceHandle HGradientOriginAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_IMAGEBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilRect Viewport;
            [FieldOffset(48)] public MilRect Viewbox;
            [FieldOffset(80)] public double CacheInvalidationThresholdMinimum;
            [FieldOffset(88)] public double CacheInvalidationThresholdMaximum;
            [FieldOffset(96)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(100)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(104)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(108)] public MilBrushMappingMode ViewportUnits;
            [FieldOffset(112)] public MilBrushMappingMode ViewboxUnits;
            [FieldOffset(116)] public DUCE.ResourceHandle HViewportAnimations;
            [FieldOffset(120)] public DUCE.ResourceHandle HViewboxAnimations;
            [FieldOffset(124)] public MilStretch Stretch;
            [FieldOffset(128)] public MilTileMode TileMode;
            [FieldOffset(132)] public MilAlignmentX AlignmentX;
            [FieldOffset(136)] public MilAlignmentY AlignmentY;
            [FieldOffset(140)] public MilCachingHint CachingHint;
            [FieldOffset(144)] public DUCE.ResourceHandle HImageSource;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DRAWINGBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilRect Viewport;
            [FieldOffset(48)] public MilRect Viewbox;
            [FieldOffset(80)] public double CacheInvalidationThresholdMinimum;
            [FieldOffset(88)] public double CacheInvalidationThresholdMaximum;
            [FieldOffset(96)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(100)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(104)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(108)] public MilBrushMappingMode ViewportUnits;
            [FieldOffset(112)] public MilBrushMappingMode ViewboxUnits;
            [FieldOffset(116)] public DUCE.ResourceHandle HViewportAnimations;
            [FieldOffset(120)] public DUCE.ResourceHandle HViewboxAnimations;
            [FieldOffset(124)] public MilStretch Stretch;
            [FieldOffset(128)] public MilTileMode TileMode;
            [FieldOffset(132)] public MilAlignmentX AlignmentX;
            [FieldOffset(136)] public MilAlignmentY AlignmentY;
            [FieldOffset(140)] public MilCachingHint CachingHint;
            [FieldOffset(144)] public DUCE.ResourceHandle HDrawing;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUALBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public MilRect Viewport;
            [FieldOffset(48)] public MilRect Viewbox;
            [FieldOffset(80)] public double CacheInvalidationThresholdMinimum;
            [FieldOffset(88)] public double CacheInvalidationThresholdMaximum;
            [FieldOffset(96)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(100)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(104)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(108)] public MilBrushMappingMode ViewportUnits;
            [FieldOffset(112)] public MilBrushMappingMode ViewboxUnits;
            [FieldOffset(116)] public DUCE.ResourceHandle HViewportAnimations;
            [FieldOffset(120)] public DUCE.ResourceHandle HViewboxAnimations;
            [FieldOffset(124)] public MilStretch Stretch;
            [FieldOffset(128)] public MilTileMode TileMode;
            [FieldOffset(132)] public MilAlignmentX AlignmentX;
            [FieldOffset(136)] public MilAlignmentY AlignmentY;
            [FieldOffset(140)] public MilCachingHint CachingHint;
            [FieldOffset(144)] public DUCE.ResourceHandle HVisual;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_BITMAPCACHEBRUSH
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(20)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(24)] public DUCE.ResourceHandle HRelativeTransform;
            [FieldOffset(28)] public DUCE.ResourceHandle HBitmapCache;
            [FieldOffset(32)] public DUCE.ResourceHandle HInternalTarget;
        }

        // ---------- 效果 (0x6e, 0x6f) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_BLUREFFECT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Radius;
            [FieldOffset(16)] public DUCE.ResourceHandle HRadiusAnimations;
            [FieldOffset(20)] public MilKernelType KernelType;
            [FieldOffset(24)] public MilRenderingBias RenderingBias;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DROPSHADOWEFFECT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double ShadowDepth;
            [FieldOffset(16)] public MilColorF Color;
            [FieldOffset(32)] public double Direction;
            [FieldOffset(40)] public double Opacity;
            [FieldOffset(48)] public double BlurRadius;
            [FieldOffset(56)] public DUCE.ResourceHandle HShadowDepthAnimations;
            [FieldOffset(60)] public DUCE.ResourceHandle HColorAnimations;
            [FieldOffset(64)] public DUCE.ResourceHandle HDirectionAnimations;
            [FieldOffset(68)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(72)] public DUCE.ResourceHandle HBlurRadiusAnimations;
            [FieldOffset(76)] public MilRenderingBias RenderingBias;
        }

        // ---------- Pen / DashStyle (0x85, 0x86) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DASHSTYLE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Offset;
            [FieldOffset(16)] public DUCE.ResourceHandle HOffsetAnimations;
            [FieldOffset(20)] public uint DashesSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PEN
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Thickness;
            [FieldOffset(16)] public double MiterLimit;
            [FieldOffset(24)] public DUCE.ResourceHandle HBrush;
            [FieldOffset(28)] public DUCE.ResourceHandle HThicknessAnimations;
            [FieldOffset(32)] public MilPenLineCap StartLineCap;
            [FieldOffset(36)] public MilPenLineCap EndLineCap;
            [FieldOffset(40)] public MilPenLineCap DashCap;
            [FieldOffset(44)] public MilPenLineJoin LineJoin;
            [FieldOffset(48)] public DUCE.ResourceHandle HDashStyle;
        }

        // ---------- Drawing (0x71, 0x87–0x8b) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DRAWINGIMAGE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HDrawing;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GEOMETRYDRAWING
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HBrush;
            [FieldOffset(12)] public DUCE.ResourceHandle HPen;
            [FieldOffset(16)] public DUCE.ResourceHandle HGeometry;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GLYPHRUNDRAWING
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HGlyphRun;
            [FieldOffset(12)] public DUCE.ResourceHandle HForegroundBrush;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_IMAGEDRAWING
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRect Rect;
            [FieldOffset(40)] public DUCE.ResourceHandle HImageSource;
            [FieldOffset(44)] public DUCE.ResourceHandle HRectAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VIDEODRAWING
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRect Rect;
            [FieldOffset(40)] public DUCE.ResourceHandle HPlayer;
            [FieldOffset(44)] public DUCE.ResourceHandle HRectAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DRAWINGGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Opacity;
            [FieldOffset(16)] public uint ChildrenSize;
            [FieldOffset(20)] public DUCE.ResourceHandle HClipGeometry;
            [FieldOffset(24)] public DUCE.ResourceHandle HOpacityAnimations;
            [FieldOffset(28)] public DUCE.ResourceHandle HOpacityMask;
            [FieldOffset(32)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(36)] public DUCE.ResourceHandle HGuidelineSet;
            [FieldOffset(40)] public MilEdgeMode EdgeMode;
            [FieldOffset(44)] public MilBitmapScalingMode BitmapScalingMode;
            [FieldOffset(48)] public MilClearTypeHint ClearTypeHint;
        }

        // ---------- GuidelineSet / BitmapCache (0x8c, 0x8d) ----------

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GUIDELINESET
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint GuidelinesXSize;
            [FieldOffset(12)] public uint GuidelinesYSize;
            [FieldOffset(16)] public uint IsDynamic;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_BITMAPCACHE
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double RenderAtScale;
            [FieldOffset(16)] public DUCE.ResourceHandle HRenderAtScaleAnimations;
            [FieldOffset(20)] public uint SnapsToDevicePixels;
            [FieldOffset(24)] public uint EnableClearType;
        }

        // ---------- 3D 视觉树 (0x29–0x30) ----------
        //
        // 逐字段复制自上游 Generated/wgx_commands.cs（同一份文件，3D 资源段之前）。
        // 载荷全是 POD：ResourceHandle(4) / Rect(4×double=32) / UInt32(4)。
        // 上游的 Rect 在这里写作 MilRect，两者都是 4 个连续 double，共 32 字节。

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VIEWPORT3DVISUAL_SETCAMERA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HCamera;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VIEWPORT3DVISUAL_SETVIEWPORT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilRect Viewport;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VIEWPORT3DVISUAL_SET3DCHILD
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HChild;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL3D_SETCONTENT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HContent;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL3D_SETTRANSFORM
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL3D_REMOVEALLCHILDREN
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL3D_REMOVECHILD
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HChild;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_VISUAL3D_INSERTCHILDAT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HChild;
            [FieldOffset(12)] public uint Index;
        }

        // ---------- 3D 资源 (0x57–0x6b) ----------
        //
        // 逐字段复制自上游 Generated/wgx_commands.cs:431-676。
        // 上游的 D3DMATRIX 在这里写作 MilMatrix4x4F（16×float=64 字节），
        // 与 wgx_core_types.cs:963 的定义逐字节一致。
        //
        // 这四个结构体带变长尾部（ChildrenSize / 四段数组），固定部分只是头部。

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_AXISANGLEROTATION3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double Angle;
            [FieldOffset(16)] public MilPoint3F Axis;
            [FieldOffset(28)] public DUCE.ResourceHandle HAxisAnimations;
            [FieldOffset(32)] public DUCE.ResourceHandle HAngleAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_QUATERNIONROTATION3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilQuaternionF Quaternion;
            [FieldOffset(24)] public DUCE.ResourceHandle HQuaternionAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_PERSPECTIVECAMERA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double NearPlaneDistance;
            [FieldOffset(16)] public double FarPlaneDistance;
            [FieldOffset(24)] public double FieldOfView;
            [FieldOffset(32)] public MilPoint3F Position;
            [FieldOffset(44)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(48)] public MilPoint3F LookDirection;
            [FieldOffset(60)] public DUCE.ResourceHandle HNearPlaneDistanceAnimations;
            [FieldOffset(64)] public MilPoint3F UpDirection;
            [FieldOffset(76)] public DUCE.ResourceHandle HFarPlaneDistanceAnimations;
            [FieldOffset(80)] public DUCE.ResourceHandle HPositionAnimations;
            [FieldOffset(84)] public DUCE.ResourceHandle HLookDirectionAnimations;
            [FieldOffset(88)] public DUCE.ResourceHandle HUpDirectionAnimations;
            [FieldOffset(92)] public DUCE.ResourceHandle HFieldOfViewAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_ORTHOGRAPHICCAMERA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double NearPlaneDistance;
            [FieldOffset(16)] public double FarPlaneDistance;
            [FieldOffset(24)] public double Width;
            [FieldOffset(32)] public MilPoint3F Position;
            [FieldOffset(44)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(48)] public MilPoint3F LookDirection;
            [FieldOffset(60)] public DUCE.ResourceHandle HNearPlaneDistanceAnimations;
            [FieldOffset(64)] public MilPoint3F UpDirection;
            [FieldOffset(76)] public DUCE.ResourceHandle HFarPlaneDistanceAnimations;
            [FieldOffset(80)] public DUCE.ResourceHandle HPositionAnimations;
            [FieldOffset(84)] public DUCE.ResourceHandle HLookDirectionAnimations;
            [FieldOffset(88)] public DUCE.ResourceHandle HUpDirectionAnimations;
            [FieldOffset(92)] public DUCE.ResourceHandle HWidthAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MATRIXCAMERA
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilMatrix4x4F ViewMatrix;
            [FieldOffset(72)] public MilMatrix4x4F ProjectionMatrix;
            [FieldOffset(136)] public DUCE.ResourceHandle HTransform;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MODEL3DGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(12)] public uint ChildrenSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_AMBIENTLIGHT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(28)] public DUCE.ResourceHandle HColorAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DIRECTIONALLIGHT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public MilPoint3F Direction;
            [FieldOffset(36)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(40)] public DUCE.ResourceHandle HColorAnimations;
            [FieldOffset(44)] public DUCE.ResourceHandle HDirectionAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_POINTLIGHT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public double Range;
            [FieldOffset(32)] public double ConstantAttenuation;
            [FieldOffset(40)] public double LinearAttenuation;
            [FieldOffset(48)] public double QuadraticAttenuation;
            [FieldOffset(56)] public MilPoint3F Position;
            [FieldOffset(68)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(72)] public DUCE.ResourceHandle HColorAnimations;
            [FieldOffset(76)] public DUCE.ResourceHandle HPositionAnimations;
            [FieldOffset(80)] public DUCE.ResourceHandle HRangeAnimations;
            [FieldOffset(84)] public DUCE.ResourceHandle HConstantAttenuationAnimations;
            [FieldOffset(88)] public DUCE.ResourceHandle HLinearAttenuationAnimations;
            [FieldOffset(92)] public DUCE.ResourceHandle HQuadraticAttenuationAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SPOTLIGHT
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public double Range;
            [FieldOffset(32)] public double ConstantAttenuation;
            [FieldOffset(40)] public double LinearAttenuation;
            [FieldOffset(48)] public double QuadraticAttenuation;
            [FieldOffset(56)] public double OuterConeAngle;
            [FieldOffset(64)] public double InnerConeAngle;
            [FieldOffset(72)] public MilPoint3F Position;
            [FieldOffset(84)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(88)] public MilPoint3F Direction;
            [FieldOffset(100)] public DUCE.ResourceHandle HColorAnimations;
            [FieldOffset(104)] public DUCE.ResourceHandle HPositionAnimations;
            [FieldOffset(108)] public DUCE.ResourceHandle HRangeAnimations;
            [FieldOffset(112)] public DUCE.ResourceHandle HConstantAttenuationAnimations;
            [FieldOffset(116)] public DUCE.ResourceHandle HLinearAttenuationAnimations;
            [FieldOffset(120)] public DUCE.ResourceHandle HQuadraticAttenuationAnimations;
            [FieldOffset(124)] public DUCE.ResourceHandle HDirectionAnimations;
            [FieldOffset(128)] public DUCE.ResourceHandle HOuterConeAngleAnimations;
            [FieldOffset(132)] public DUCE.ResourceHandle HInnerConeAngleAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_GEOMETRYMODEL3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public DUCE.ResourceHandle HTransform;
            [FieldOffset(12)] public DUCE.ResourceHandle HGeometry;
            [FieldOffset(16)] public DUCE.ResourceHandle HMaterial;
            [FieldOffset(20)] public DUCE.ResourceHandle HBackMaterial;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MESHGEOMETRY3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint PositionsSize;
            [FieldOffset(12)] public uint NormalsSize;
            [FieldOffset(16)] public uint TextureCoordinatesSize;
            [FieldOffset(20)] public uint TriangleIndicesSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MATERIALGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint ChildrenSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_DIFFUSEMATERIAL
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public MilColorF AmbientColor;
            [FieldOffset(40)] public DUCE.ResourceHandle HBrush;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SPECULARMATERIAL
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public double SpecularPower;
            [FieldOffset(32)] public DUCE.ResourceHandle HBrush;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_EMISSIVEMATERIAL
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilColorF Color;
            [FieldOffset(24)] public DUCE.ResourceHandle HBrush;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TRANSFORM3DGROUP
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public uint ChildrenSize;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_TRANSLATETRANSFORM3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double OffsetX;
            [FieldOffset(16)] public double OffsetY;
            [FieldOffset(24)] public double OffsetZ;
            [FieldOffset(32)] public DUCE.ResourceHandle HOffsetXAnimations;
            [FieldOffset(36)] public DUCE.ResourceHandle HOffsetYAnimations;
            [FieldOffset(40)] public DUCE.ResourceHandle HOffsetZAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_SCALETRANSFORM3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double ScaleX;
            [FieldOffset(16)] public double ScaleY;
            [FieldOffset(24)] public double ScaleZ;
            [FieldOffset(32)] public double CenterX;
            [FieldOffset(40)] public double CenterY;
            [FieldOffset(48)] public double CenterZ;
            [FieldOffset(56)] public DUCE.ResourceHandle HScaleXAnimations;
            [FieldOffset(60)] public DUCE.ResourceHandle HScaleYAnimations;
            [FieldOffset(64)] public DUCE.ResourceHandle HScaleZAnimations;
            [FieldOffset(68)] public DUCE.ResourceHandle HCenterXAnimations;
            [FieldOffset(72)] public DUCE.ResourceHandle HCenterYAnimations;
            [FieldOffset(76)] public DUCE.ResourceHandle HCenterZAnimations;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_ROTATETRANSFORM3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public double CenterX;
            [FieldOffset(16)] public double CenterY;
            [FieldOffset(24)] public double CenterZ;
            [FieldOffset(32)] public DUCE.ResourceHandle HCenterXAnimations;
            [FieldOffset(36)] public DUCE.ResourceHandle HCenterYAnimations;
            [FieldOffset(40)] public DUCE.ResourceHandle HCenterZAnimations;
            [FieldOffset(44)] public DUCE.ResourceHandle HRotation;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        internal struct MILCMD_MATRIXTRANSFORM3D
        {
            [FieldOffset(0)] public MilCmd Type;
            [FieldOffset(4)] public DUCE.ResourceHandle Handle;
            [FieldOffset(8)] public MilMatrix4x4F Matrix;
        }
    }
}
