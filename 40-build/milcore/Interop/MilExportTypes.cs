// Licensed to the .NET Foundation under one or more agreements.
//
// M7a：补齐 94 个缺失 MIL 原生导出所需的值类型 / 枚举 / 委托。
//
// 【为什么这些类型是新的一份，而不是复用 MilValueTypes.cs 里的】
//   MilValueTypes.cs 里的 MilPoint / MilSize / MilRect / MilMatrix3x2D 都是
//   **internal**，而 C# 的可访问性规则（CS0051）不允许 public 方法签名里出现
//   internal 类型——即使开了 InternalsVisibleTo 也不行（已实测）。新导出全部是
//   public（要和 MilNative.cs 里既有的 16 个保持同一可见性），所以签名里只能用
//   public 类型。命名上刻意避开既有 internal 类型，避免 CS0101 重名：
//
//     internal MilPoint2F  ←→  public MilPointF      （float 点）
//     internal MilPoint    ←→  public MilPointD      （double 点）
//     internal MilSize     ←→  public MilSizeD       （double 尺寸）
//     internal MilRect     ←→  public MilPointAndSizeD（X,Y,Width,Height double）
//     internal MilMatrix3x2D → 新导出用 double*（6 个连续 double，布局逐字节一致）
//
// 【布局来源】逐字段抄自上游：
//   Common/Graphics/wgx_core_types.cs : MilRectD(903) / MilRectF(954) / D3DMATRIX(962)
//   WpfGfx/include/processed/wgx_core_types.cs : MIL_PEN_DATA(238) / MILRTInitializationFlags(68)
//   PresentationCore/.../MILUtilities.cs : MILRect3D(117)
//   PresentationCore/.../UnsafeNativeMethodsMilCoreApi.cs : WICColorContextType(918)
//   PresentationCore/.../PathGeometry.cs : AddFigureToListDelegate(546)
//   Common/Graphics/exports.cs : MilMessage(240)
//
// 所有类型都有 LayoutTests 式的 Marshal.SizeOf 断言（见 tests/.../MilExportTests.cs）。

using System;
using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Interop
{
    // ==================================================================
    //  点 / 尺寸 / 矩形
    // ==================================================================

    /// <summary>double 二维点。布局等价于 System.Windows.Point 与原生 MilPoint2D。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilPointD : IEquatable<MilPointD>
    {
        public double X;
        public double Y;

        public MilPointD(double x, double y) { X = x; Y = y; }

        public bool Equals(MilPointD o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is MilPointD p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>float 二维点。布局等价于原生 MilPoint2F（addFigure 回调里的点）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilPointF : IEquatable<MilPointF>
    {
        public float X;
        public float Y;

        public MilPointF(float x, float y) { X = x; Y = y; }

        public bool Equals(MilPointF o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is MilPointF p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>double 尺寸。布局等价于 System.Windows.Size 与原生 MilPoint2D(radii)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilSizeD : IEquatable<MilSizeD>
    {
        public double Width;
        public double Height;

        public MilSizeD(double width, double height) { Width = width; Height = height; }

        public bool Equals(MilSizeD o) => Width == o.Width && Height == o.Height;
        public override bool Equals(object obj) => obj is MilSizeD s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(Width, Height);
        public override string ToString() => $"{Width}×{Height}";
    }

    /// <summary>
    /// X/Y/Width/Height 的 double 矩形。布局等价于 System.Windows.Rect 与原生的
    /// MilPointAndSizeD——TileBrush 的 Viewport/Viewbox 用的就是这个。
    /// 注意与 <see cref="MilRectD"/>（left/top/right/bottom）**语义不同**，别混用。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilPointAndSizeD : IEquatable<MilPointAndSizeD>
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public MilPointAndSizeD(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public double Right => X + Width;
        public double Bottom => Y + Height;

        /// <summary>原生 MilEmptyPointAndSizeD：Rect.Empty 形态（+INF,+INF,-INF,-INF）。</summary>
        public static MilPointAndSizeD Empty =>
            new MilPointAndSizeD(double.PositiveInfinity, double.PositiveInfinity,
                                 double.NegativeInfinity, double.NegativeInfinity);

        public bool IsEmptyOrInvalid =>
            !(Width >= 0.0 && Height >= 0.0 && !double.IsNaN(X) && !double.IsNaN(Y));

        public bool Equals(MilPointAndSizeD o) =>
            X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;

        public override bool Equals(object obj) => obj is MilPointAndSizeD r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
        public override string ToString() => $"({X},{Y} {Width}×{Height})";
    }

    /// <summary>left/top/right/bottom 的 double 矩形。上游 Common/Graphics/wgx_core_types.cs:903。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilRectD : IEquatable<MilRectD>
    {
        public double Left;
        public double Top;
        public double Right;
        public double Bottom;

        public MilRectD(double left, double top, double right, double bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public static MilRectD Empty => new MilRectD(0, 0, 0, 0);

        public double Width => Right - Left;
        public double Height => Bottom - Top;

        public bool Equals(MilRectD o) =>
            Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;

        public override bool Equals(object obj) => obj is MilRectD r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override string ToString() => $"LTRB({Left},{Top},{Right},{Bottom})";
    }

    /// <summary>left/top/right/bottom 的 float 矩形。上游 wgx_core_types.cs:954（MilRectF）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilRectF : IEquatable<MilRectF>
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;

        public MilRectF(float left, float top, float right, float bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public bool Equals(MilRectF o) =>
            Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;

        public override bool Equals(object obj) => obj is MilRectF r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override string ToString() => $"LTRB({Left},{Top},{Right},{Bottom})";
    }

    /// <summary>整数矩形 X/Y/Width/Height（System.Windows.Int32Rect）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MilInt32Rect : IEquatable<MilInt32Rect>
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public MilInt32Rect(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public static MilInt32Rect Empty => new MilInt32Rect(0, 0, 0, 0);

        public int Right => X + Width;
        public int Bottom => Y + Height;

        public bool Equals(MilInt32Rect o) =>
            X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;

        public override bool Equals(object obj) => obj is MilInt32Rect r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
        public override string ToString() => $"({X},{Y} {Width}×{Height})";
    }

    // ==================================================================
    //  矩阵与笔
    // ==================================================================

    /// <summary>
    /// 4×4 float 矩阵，行主序 [_11.._14 / _21.._24 / _31.._34 / _41.._44]。
    /// 上游 wgx_core_types.cs:962。二维仿射矩阵的映射见 MILUtilities.ConvertToD3DMATRIX：
    ///   _11=M11 _12=M12 _21=M21 _22=M22 _41=OffsetX _42=OffsetY，其余取单位矩阵值。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3DMATRIX
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static D3DMATRIX Identity => new D3DMATRIX
        {
            M11 = 1, M22 = 1, M33 = 1, M44 = 1,
        };

        /// <summary>二维仿射（m11,m12,m21,m22,offsetX,offsetY）→ D3DMATRIX。</summary>
        public static D3DMATRIX FromAffine(
            float m11, float m12, float m21, float m22, float offsetX, float offsetY) =>
            new D3DMATRIX
            {
                M11 = m11, M12 = m12, M21 = m21, M22 = m22,
                M33 = 1, M44 = 1, M41 = offsetX, M42 = offsetY,
            };

        /// <summary>第 0 行第 <paramref name="index"/> 列的 float（等价于 ((float*)this)[index]）。</summary>
        public float this[int index]
        {
            get => index switch
            {
                0 => M11, 1 => M12, 2 => M13, 3 => M14,
                4 => M21, 5 => M22, 6 => M23, 7 => M24,
                8 => M31, 9 => M32, 10 => M33, 11 => M34,
                12 => M41, 13 => M42, 14 => M43, 15 => M44,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }

        public override string ToString() =>
            $"[{M11} {M12} {M13} {M14} / {M21} {M22} {M23} {M24} / " +
            $"{M31} {M32} {M33} {M34} / {M41} {M42} {M43} {M44}]";
    }

    /// <summary>
    /// 笔数据。上游 WpfGfx/include/processed/wgx_core_types.cs:238。
    /// 布局：3×double + 4×int(枚举) + uint，自然对齐，共 48 字节
    /// （24 + 16 + 4 = 44，按 8 字节对齐补到 48）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIL_PEN_DATA
    {
        public double Thickness;
        public double MiterLimit;
        public double DashOffset;
        public MilPenLineCap StartLineCap;
        public MilPenLineCap EndLineCap;
        public MilPenLineCap DashCap;
        public MilPenLineJoin LineJoin;

        /// <summary>
        /// pDashArray 的**字节数**（不是元素个数！0 表示实线）。
        ///
        /// 【U1c 真机对拍纠正】上游 initializePen 用的是
        /// `UINT cDash = pData-&gt;DashArraySize / sizeof(double);`
        /// （WpfGfx/core/uce/geometry_api.cpp:141），托管侧写的是
        /// `pData-&gt;DashArraySize = (UInt32)count * sizeof(double);`
        /// （PresentationCore/.../DashStyle.cs:71）。所以这里是字节数。
        /// 本工程 M7a 初版按"元素个数"解释，真机对拍时 2 元素的虚线数组
        /// 被上游算成 `2 / 8 = 0` 条 → 虚线整个失效（widen_dash 对拍暴露）。
        /// </summary>
        public uint DashArraySize;
    }

    /// <summary>3D 包围盒（float）。上游 PresentationCore/.../MILUtilities.cs:117。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MILRect3D
    {
        public float X;
        public float Y;
        public float Z;
        public float LengthX;
        public float LengthY;
        public float LengthZ;
    }

    // ==================================================================
    //  枚举
    // ==================================================================

    /// <summary>System.Windows.Media.SweepDirection 的互操作镜像。</summary>
    public enum MilSweepDirection
    {
        Counterclockwise = 0,
        Clockwise = 1,
    }

    /// <summary>System.Windows.Media.IntersectionDetail 的互操作镜像（上游 IntersectionDetail.cs:24）。</summary>
    public enum MilIntersectionDetail
    {
        NotCalculated = 0,
        Empty = 1,
        FullyInside = 2,
        FullyContains = 3,
        Intersects = 4,
    }

    /// <summary>渲染目标初始化标志。上游 processed/wgx_core_types.cs:68。</summary>
    [Flags]
    public enum MILRTInitializationFlags
    {
        MIL_RT_INITIALIZE_DEFAULT = 0x00000000,
        MIL_RT_SOFTWARE_ONLY = 0x00000001,
        MIL_RT_HARDWARE_ONLY = 0x00000002,
        MIL_RT_NULL = 0x00000003,
        MIL_RT_TYPE_MASK = 0x00000003,
        MIL_RT_PRESENT_IMMEDIATELY = 0x00000004,
        MIL_RT_PRESENT_RETAIN_CONTENTS = 0x00000008,
        MIL_RT_FULLSCREEN = 0x00000010,
    }

    /// <summary>像素格式。上游 Common/Graphics/wgx_render.cs:69（只列本工程会用到的值）。</summary>
    public enum MilPixelFormatEnum
    {
        Default = 0x0,
        Extended = 0x0,
        Indexed1 = 0x1,
        Indexed2 = 0x2,
        Indexed4 = 0x3,
        Indexed8 = 0x4,
        BlackWhite = 0x5,
        Gray2 = 0x6,
        Gray4 = 0x7,
        Gray8 = 0x8,
        Bgr555 = 0x9,
        Bgr565 = 0xA,
        Gray16 = 0xB,
        Bgr24 = 0xC,
        Rgb24 = 0xD,
        Bgr32 = 0xE,
        Bgra32 = 0xF,
        Pbgra32 = 0x10,
        Bgr101010 = 0x11,
        Rgb48 = 0x12,
        Rgba64 = 0x13,
        Prgba64 = 0x14,
        Gray32Float = 0x15,
        Rgb128Float = 0x16,
        Rgba128Float = 0x17,
        Prgba128Float = 0x18,
        Cmyk32 = 0x19,
    }

    /// <summary>IWICColorContext 的类型。上游 UnsafeNativeMethodsMilCoreApi.cs:918。</summary>
    public enum MilWicColorContextType : uint
    {
        WICColorContextUninitialized = 0,
        WICColorContextProfile = 1,
        WICColorContextExifColorSpace = 2,
    }

    /// <summary>P/Invoke 反向包装的释放/事件投递（M1 只用句柄，不真的回调）。</summary>
    public enum MilPresentationResults
    {
        Vsync = 0,
        NoPresent = 1,
        VsyncUnsupported = 2,
        Dwm = 3,
    }

    // ==================================================================
    //  回调 / 回传结构
    // ==================================================================

    /// <summary>
    /// PathGeometry 的图形回传回调。
    /// 上游签名（PathGeometry.cs:546）：
    ///   delegate void AddFigureToListDelegate(bool isFilled, bool isClosed,
    ///                                         MilPoint2F* pPoints, uint pointCount,
    ///                                         byte* pTypes, uint typeCount)
    /// pPoints[0] 是图形起点；pTypes[i] 取 MILCoreSegFlags（SegTypeLine 每段 1 点、
    /// SegTypeBezier 每段 3 点），可带 SegIsAGap / SegSmoothJoin 位。
    /// </summary>
    public unsafe delegate void MilAddFigureCallback(
        bool isFilled, bool isClosed, MilPointF* pPoints, uint pointCount,
        byte* pTypes, uint typeCount);

    /// <summary>后向通道消息的数据体。上游 exports.cs:288（union，最大成员 Presented 16 字节）。</summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)]
    public struct MilMessageCapsData
    {
        [FieldOffset(0)] public int CommonMinimumCaps;
        [FieldOffset(4)] public uint DisplayUniqueness;
        [FieldOffset(8)] public int Caps;
    }

    /// <summary>分区已失效消息。</summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 4)]
    public struct MilMessagePartitionIsZombie
    {
        [FieldOffset(0)] public int HRESULTFailureCode;
    }

    /// <summary>同步模式状态消息。</summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 4)]
    public struct MilMessageSyncModeStatus
    {
        [FieldOffset(0)] public int Enabled;
    }

    /// <summary>呈现结果消息。</summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 16)]
    public struct MilMessagePresented
    {
        [FieldOffset(0)] public int PresentationResults;
        [FieldOffset(4)] public int RefreshRate;
        [FieldOffset(8)] public long PresentationTime;
    }

    /// <summary>后向通道消息 ID。上游 exports.cs:240。</summary>
    public enum MilMessageType
    {
        Invalid = 0x00,
        SyncFlushReply = 0x01,
        Caps = 0x04,
        PartitionIsZombie = 0x06,
        SyncModeStatus = 0x09,
        Presented = 0x0A,
        BadPixelShader = 0x10,
    }

    /// <summary>
    /// 后向通道消息的 union。上游 exports.cs:288：Type@0 / Reserved@4 / 数据体@8。
    /// 总 24 字节（Presented 是最大成员：4+4+8=16，8+16=24）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 24)]
    public struct MilMessage
    {
        [FieldOffset(0)] public MilMessageType Type;
        [FieldOffset(4)] public int Reserved;
        [FieldOffset(8)] public MilMessageCapsData Caps;
        [FieldOffset(8)] public MilMessagePartitionIsZombie HRESULTFailure;
        [FieldOffset(8)] public MilMessageSyncModeStatus SyncModeStatus;
        [FieldOffset(8)] public MilMessagePresented Presented;
    }
}
