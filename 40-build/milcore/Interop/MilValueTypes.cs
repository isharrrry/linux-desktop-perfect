// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 线格（wire）值类型。
//
// 这些结构体是命令流里的原始字节布局，逐字段复制自上游
// Common/Graphics/wgx_core_types.cs（MilColorF:454 / MilPoint2F:475 /
// MilPoint3F:497 / MilQuaternionF:508 / MilMatrix3x2D:591 / MilRenderOptions:576 /
// MIL_GRADIENTSTOP:895）。
//
// 契约层（Contracts/）只定义了句柄与枚举，没有这些值类型——它们属于互操作层，
// 故放这里。所有布局常量由 tests/.../CommandLayoutTests.cs 用 Marshal.SizeOf 钉死，
// 与上游的逐项比对由 tools/verify-cmd-layout.py 负责。
//
// 一律 [Pack=1]：上游命令结构体是 LayoutKind.Explicit + Pack=1，
// 字段之间没有任何对齐填充，MilRenderOptions 的 28 字节（7×4）就是证据。

using System;
using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Interop
{
    /// <summary>scRGB 颜色，分量顺序 r,g,b,a（上游 wgx_core_types.cs:454）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilColorF : IEquatable<MilColorF>
    {
        public float R;
        public float G;
        public float B;
        public float A;

        public MilColorF(float r, float g, float b, float a)
        {
            R = r; G = g; B = b; A = a;
        }

        public static MilColorF FromSrgb(byte r, byte g, byte b, byte a) =>
            new MilColorF(r / 255f, g / 255f, b / 255f, a / 255f);

        public bool Equals(MilColorF o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object obj) => obj is MilColorF c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public override string ToString() => $"rgba({R},{G},{B},{A})";
    }

    /// <summary>单精度二维点（上游:475）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilPoint2F : IEquatable<MilPoint2F>
    {
        public float X;
        public float Y;

        public MilPoint2F(float x, float y) { X = x; Y = y; }
        public bool Equals(MilPoint2F o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is MilPoint2F p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>单精度三维点（上游:497）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilPoint3F : IEquatable<MilPoint3F>
    {
        public float X;
        public float Y;
        public float Z;

        public MilPoint3F(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool Equals(MilPoint3F o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is MilPoint3F p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X},{Y},{Z})";
    }

    /// <summary>单精度四元数（上游:508）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilQuaternionF : IEquatable<MilQuaternionF>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public MilQuaternionF(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public bool Equals(MilQuaternionF o) => X == o.X && Y == o.Y && Z == o.Z && W == o.W;
        public override bool Equals(object obj) => obj is MilQuaternionF q && Equals(q);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
        public override string ToString() => $"({X},{Y},{Z},{W})";
    }

    /// <summary>3×2 仿射矩阵（上游:591）。布局与 System.Windows.Media.Matrix 一致。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilMatrix3x2D : IEquatable<MilMatrix3x2D>
    {
        public double S_11;
        public double S_12;
        public double S_21;
        public double S_22;
        public double DX;
        public double DY;

        public MilMatrix3x2D(double s11, double s12, double s21, double s22, double dx, double dy)
        {
            S_11 = s11; S_12 = s12; S_21 = s21; S_22 = s22; DX = dx; DY = dy;
        }

        public static MilMatrix3x2D Identity => new MilMatrix3x2D(1, 0, 0, 1, 0, 0);

        public bool IsIdentity => this == Identity;

        public bool Equals(MilMatrix3x2D o) =>
            S_11 == o.S_11 && S_12 == o.S_12 && S_21 == o.S_21 &&
            S_22 == o.S_22 && DX == o.DX && DY == o.DY;

        public override bool Equals(object obj) => obj is MilMatrix3x2D m && Equals(m);
        public override int GetHashCode() =>
            HashCode.Combine(S_11, S_12, S_21, S_22, DX, DY);
        public override string ToString() => $"[{S_11} {S_12} / {S_21} {S_22} / {DX} {DY}]";

        public static bool operator ==(MilMatrix3x2D a, MilMatrix3x2D b) => a.Equals(b);
        public static bool operator !=(MilMatrix3x2D a, MilMatrix3x2D b) => !a.Equals(b);
    }

    /// <summary>双精度点（对应上游命令里的 System.Windows.Point）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilPoint : IEquatable<MilPoint>
    {
        public double X;
        public double Y;

        public MilPoint(double x, double y) { X = x; Y = y; }
        public bool Equals(MilPoint o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is MilPoint p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>双精度尺寸。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilSize : IEquatable<MilSize>
    {
        public double Width;
        public double Height;

        public MilSize(double width, double height) { Width = width; Height = height; }
        public bool Equals(MilSize o) => Width == o.Width && Height == o.Height;
        public override bool Equals(object obj) => obj is MilSize s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(Width, Height);
        public override string ToString() => $"{Width}×{Height}";
    }

    /// <summary>双精度矩形（对应 System.Windows.Rect：X,Y,Width,Height）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilRect : IEquatable<MilRect>
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public MilRect(double x, double y, double width, double height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public static MilRect Empty => new MilRect(0, 0, 0, 0);

        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public bool Equals(MilRect o) =>
            X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;

        public override bool Equals(object obj) => obj is MilRect r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
        public override string ToString() => $"({X},{Y} {Width}×{Height})";
    }

    /// <summary>
    /// 整数矩形。上游 MILCMD_TARGET_INVALIDATE 用的是 MS.Win32.NativeMethods.RECT
    /// （left, top, right, bottom 四个 int，共 16 字节）——见
    /// Common/Graphics/Generated/wgx_commands.cs:376。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilRectI : IEquatable<MilRectI>
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public MilRectI(int left, int top, int right, int bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool Equals(MilRectI o) =>
            Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;

        public override bool Equals(object obj) => obj is MilRectI r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    /// <summary>
    /// 渲染选项（上游:576）。7 个 4 字节枚举 = 28 字节，Pack=1 无填充。
    /// 用于 MilCmdVisualSetRenderOptions(0x21)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilRenderOptions : IEquatable<MilRenderOptions>
    {
        /// <summary>全零 = 全部取默认值（WPF 托管侧 MilRenderOptions 的初始值）。</summary>
        public static MilRenderOptions Default => default;

        public MilRenderOptionFlags Flags;
        public MilEdgeMode EdgeMode;
        public MilCompositingMode CompositingMode;
        public MilBitmapScalingMode BitmapScalingMode;
        public MilClearTypeHint ClearTypeHint;
        public MilTextRenderingMode TextRenderingMode;
        public MilTextHintingMode TextHintingMode;

        public bool Equals(MilRenderOptions o) =>
            Flags == o.Flags && EdgeMode == o.EdgeMode && CompositingMode == o.CompositingMode &&
            BitmapScalingMode == o.BitmapScalingMode && ClearTypeHint == o.ClearTypeHint &&
            TextRenderingMode == o.TextRenderingMode && TextHintingMode == o.TextHintingMode;

        public override bool Equals(object obj) => obj is MilRenderOptions r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(
            Flags, EdgeMode, CompositingMode, BitmapScalingMode, ClearTypeHint,
            TextRenderingMode, TextHintingMode);
    }

    /// <summary>
    /// 渐变停靠点（上游 MIL_GRADIENTSTOP:895）：double Position + MilColorF Color = 24 字节。
    ///
    /// 步长必须是 24 而不是 16：托管侧 LinearGradientBrush.cs 用
    /// sizeof(DUCE.MIL_GRADIENTSTOP)*count 计算 GradientStopsSize，
    /// 原生侧 marshal_generated.cpp 用 % sizeof(MilGradientStop) 校验。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MilGradientStop : IEquatable<MilGradientStop>
    {
        public const int SizeInBytes = 24;

        public double Position;
        public MilColorF Color;

        public MilGradientStop(double position, MilColorF color)
        {
            Position = position;
            Color = color;
        }

        public bool Equals(MilGradientStop o) => Position == o.Position && Color.Equals(o.Color);
        public override bool Equals(object obj) => obj is MilGradientStop g && Equals(g);
        public override int GetHashCode() => HashCode.Combine(Position, Color);
        public override string ToString() => $"{Position}:{Color}";
    }
}
