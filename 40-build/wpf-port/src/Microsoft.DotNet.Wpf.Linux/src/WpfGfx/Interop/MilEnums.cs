// Licensed to the .NET Foundation under one or more agreements.
//
// MIL / DUCE 枚举。取值逐项复制自上游，未做任何改动。
//
// 来源：
//   Common/Graphics/wgx_core_types.cs                    (MilRenderOptionFlags, MilPathGeometryFlags ...)
//   Common/Graphics/exports.cs                           (ChannelMarshalType)
//
// 注意：MILCMD 命令字不再在此定义。顶层命令用契约 Contracts/MilCmd.cs（118 条），
// RenderData 绘图指令用契约 Contracts/MilDrawCommand.cs（25 条）——handoff §1 明确
// 要求两者不得混用。
//   PresentationCore/System/Windows/Media/Generated/*.cs (EdgeMode, FillRule, BrushMappingMode ...)
//   PresentationCore/ref/PresentationCore.cs             (PenLineCap, PenLineJoin)

namespace WpfGfx.Linux.Interop
{
    /// <summary>通道封送模式。上游 exports.cs:57，与契约 MilChannelMarshalType 取值一致。</summary>
    public enum ChannelMarshalType
    {
        ChannelMarshalTypeInvalid = 0x0,
        ChannelMarshalTypeSameThread = 0x1,
        ChannelMarshalTypeCrossThread = 0x2,
    }

    [System.Flags]
    public enum MilRenderOptionFlags
    {
        None = 0x00000000,
        BitmapScalingMode = 0x00000001,
        EdgeMode = 0x00000002,
        CompositingMode = 0x00000004,
        ClearTypeHint = 0x00000008,
        TextRenderingMode = 0x00000010,
        TextHintingMode = 0x00000020,
    }

    public enum MilEdgeMode { Unspecified = 0, Aliased = 1 }

    public enum MilCompositingMode
    {
        SourceOver = 0, SourceCopy = 1, SourceAdd = 2,
        SourceAlphaMultiply = 3, SourceInverseAlphaMultiply = 4, SourceUnder = 5,
    }

    public enum MilBitmapScalingMode
    {
        Unspecified = 0, LowQuality = 1, HighQuality = 2,
        Linear = 1, Fant = 2, NearestNeighbor = 3,
    }

    public enum MilClearTypeHint { Auto = 0, Enabled = 1 }
    public enum MilTextRenderingMode { Auto = 0, Aliased = 1, Grayscale = 2, ClearType = 3 }
    public enum MilTextHintingMode { Auto = 0, Fixed = 1, Animated = 2 }

    public enum MilFillRule { EvenOdd = 0, Nonzero = 1 }
    public enum MilBrushMappingMode { Absolute = 0, RelativeToBoundingBox = 1 }
    public enum MilGradientSpreadMethod { Pad = 0, Reflect = 1, Repeat = 2 }
    public enum MilColorInterpolationMode { ScRgbLinearInterpolation = 0, SRgbLinearInterpolation = 1 }
    public enum MilGeometryCombineMode { Union = 0, Intersect = 1, Xor = 2, Exclude = 3 }
    public enum MilStretch { None = 0, Fill = 1, Uniform = 2, UniformToFill = 3 }
    public enum MilTileMode { None = 0, Tile = 4, FlipX = 1, FlipY = 2, FlipXY = 3 }
    public enum MilAlignmentX { Left = 0, Center = 1, Right = 2 }
    public enum MilAlignmentY { Top = 0, Center = 1, Bottom = 2 }
    public enum MilCachingHint { Unspecified = 0, Cache = 1 }
    public enum MilPenLineCap { Flat = 0, Square = 1, Round = 2, Triangle = 3 }
    public enum MilPenLineJoin { Miter = 0, Bevel = 1, Round = 2 }
    public enum MilKernelType { Gaussian = 0, Box = 1 }
    public enum MilRenderingBias { Performance = 0, Quality = 1 }
    public enum MilShaderRenderMode { Auto = 0, SoftwareOnly = 1, HardwareOnly = 2 }

    public enum MilWindowLayerType { NotLayered = 0, SystemManagedLayer = 1, ApplicationManagedLayer = 2 }

    [System.Flags]
    public enum MilTransparencyFlags
    {
        Opaque = 0x0, ConstantAlpha = 0x1, PerPixelAlpha = 0x2, ColorKey = 0x4,
    }

    [System.Flags]
    public enum MilPathGeometryFlags
    {
        HasCurves = 0x00000001, BoundsValid = 0x00000002, HasGaps = 0x00000004,
        HasHollows = 0x00000008, IsRegionData = 0x00000010, Mask = 0x0000001F,
    }

    [System.Flags]
    public enum MilPathFigureFlags
    {
        HasGaps = 0x00000001, HasCurves = 0x00000002, IsClosed = 0x00000004,
        IsFillable = 0x00000008, IsRectangleData = 0x00000010, Mask = 0x0000001F,
    }

    /// <summary>MIL_SEGMENT_TYPE — 路径段类型。上游 wgx_core_types.cs:8。</summary>
    public enum MilSegmentType
    {
        None = 0,
        Line = 1,
        Bezier = 2,
        QuadraticBezier = 3,
        Arc = 4,
        PolyLine = 5,
        PolyBezier = 6,
        PolyQuadraticBezier = 7,
    }

    /// <summary>MILCoreSegFlags — 路径段标志。上游 wgx_core_types.cs:22。</summary>
    [System.Flags]
    public enum MilCoreSegFlags
    {
        SegTypeLine = 0x00000001,
        SegTypeBezier = 0x00000002,
        SegTypeMask = 0x00000003,
        SegIsAGap = 0x00000004,
        SegSmoothJoin = 0x00000008,
        SegClosed = 0x00000010,
        SegIsCurved = 0x00000020,
    }
}
