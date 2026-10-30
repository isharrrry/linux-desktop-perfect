// Licensed to the .NET Foundation under one or more agreements.
//
// DUCE 资源的托管数据模型。命令解码器（Commands/）把字节流翻译成这里的实例，
// 渲染后端（Rendering/）消费这些实例。
//
// 注意：本文件只建模 M1 主链路需要的资源；D3D / 3D / 媒体 相关命令解码器
// 会返回 E_NOTIMPL 并登记，不产生这里的实例。

using System;
using System.Collections.Generic;
using WpfGfx.Linux.Commands;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Resources
{
    /// <summary>所有 DUCE 资源的基类。引用计数由 MilResourceTable 维护，不在资源自身上。</summary>
    internal abstract class MilResource
    {
        public abstract DUCE.ResourceType Type { get; }

        /// <summary>最近一次设置该资源的命令字（调试用）。</summary>
        public MilCmd LastCommand;
    }

    /// <summary>未知 / 暂未建模的资源类型。解码器对其只记命令字，不解释负载。</summary>
    internal sealed class MilOpaqueResource : MilResource
    {
        private readonly DUCE.ResourceType _type;
        public MilOpaqueResource(DUCE.ResourceType type) { _type = type; }
        public override DUCE.ResourceType Type => _type;
    }

    // ============================ Visual / 内容 ============================

    internal sealed class MilVisualResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_VISUAL;

        /// <summary>句柄在 CreateOrAddRefOnChannel 回填后才可用，故此处可写。</summary>
        public readonly MilVisualNode Visual = new MilVisualNode();
    }

    /// <summary>RenderData 资源：保存原始指令流（RecordHeader + 命令体 的序列）。</summary>
    internal sealed class MilRenderDataResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_RENDERDATA;

        /// <summary>指令流原始字节（记录框 + 载荷）。</summary>
        public byte[] Data = Array.Empty<byte>();

        /// <summary>
        /// 解码后的绘图指令序列。只切记录框 + 填 MilDrawInstruction，
        /// **不含任何绘图行为**（属 skia-render 组）。
        /// </summary>
        public MilRenderData RenderData;
    }

    // ============================ 标量资源 ============================

    internal sealed class MilDoubleResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DOUBLERESOURCE;
        public double Value;
    }

    internal sealed class MilColorResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_COLORRESOURCE;
        public MilColorF Value;
    }

    internal sealed class MilPointResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_POINTRESOURCE;
        public MilPoint Value;
    }

    internal sealed class MilRectResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_RECTRESOURCE;
        public MilRect Value;
    }

    internal sealed class MilSizeResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_SIZERESOURCE;
        public MilSize Value;
    }

    internal sealed class MilMatrixResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_MATRIXRESOURCE;
        public MilMatrix3x2D Value;
    }

    internal sealed class MilPoint3DResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_POINT3DRESOURCE;
        public MilPoint3F Value;
    }

    internal sealed class MilVector3DResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_VECTOR3DRESOURCE;
        public MilPoint3F Value;
    }

    internal sealed class MilQuaternionResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_QUATERNIONRESOURCE;
        public MilQuaternionF Value;
    }

    internal sealed class MilEtwEventResource : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_ETWEVENTRESOURCE;
        public uint Id;
    }

    // ============================ 变换 ============================

    internal abstract class MilTransform : MilResource
    {
        public double AnimValuePlaceholder; // 保留：动画句柄暂未建模
    }

    internal sealed class MilTranslateTransform : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_TRANSLATETRANSFORM;
        public double X, Y;
    }

    internal sealed class MilScaleTransform : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_SCALETRANSFORM;
        public double ScaleX = 1, ScaleY = 1, CenterX, CenterY;
    }

    internal sealed class MilSkewTransform : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_SKEWTRANSFORM;
        public double AngleX, AngleY, CenterX, CenterY;
    }

    internal sealed class MilRotateTransform : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_ROTATETRANSFORM;
        public double Angle, CenterX, CenterY;
    }

    internal sealed class MilMatrixTransform : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_MATRIXTRANSFORM;
        public MilMatrix3x2D Matrix = MilMatrix3x2D.Identity;
    }

    internal sealed class MilTransformGroup : MilTransform
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_TRANSFORMGROUP;
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    // ============================ 画刷 ============================

    internal abstract class MilBrush : MilResource
    {
        public double Opacity = 1.0;
        public DUCE.ResourceHandle Transform;
        public DUCE.ResourceHandle RelativeTransform;
    }

    internal sealed class MilSolidColorBrush : MilBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_SOLIDCOLORBRUSH;
        public MilColorF Color;
    }

    internal sealed class MilLinearGradientBrush : MilBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_LINEARGRADIENTBRUSH;
        public MilPoint StartPoint, EndPoint;
        public MilColorInterpolationMode ColorInterpolationMode;
        public MilBrushMappingMode MappingMode;
        public MilGradientSpreadMethod SpreadMethod;
        public readonly List<MilGradientStop> GradientStops = new List<MilGradientStop>();
    }

    internal sealed class MilRadialGradientBrush : MilBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_RADIALGRADIENTBRUSH;
        public MilPoint Center, GradientOrigin;
        public double RadiusX, RadiusY;
        public MilColorInterpolationMode ColorInterpolationMode;
        public MilBrushMappingMode MappingMode;
        public MilGradientSpreadMethod SpreadMethod;
        public readonly List<MilGradientStop> GradientStops = new List<MilGradientStop>();
    }

    /// <summary>TileBrush 公共体（ImageBrush / DrawingBrush / VisualBrush 共享）。</summary>
    internal abstract class MilTileBrush : MilBrush
    {
        public MilRect Viewport, Viewbox;
        public double CacheInvalidationThresholdMinimum, CacheInvalidationThresholdMaximum;
        public MilBrushMappingMode ViewportUnits, ViewboxUnits;
        public MilStretch Stretch;
        public MilTileMode TileMode;
        public MilAlignmentX AlignmentX;
        public MilAlignmentY AlignmentY;
        public MilCachingHint CachingHint;
    }

    internal sealed class MilImageBrush : MilTileBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_IMAGEBRUSH;
        public DUCE.ResourceHandle ImageSource;
    }

    internal sealed class MilDrawingBrush : MilTileBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DRAWINGBRUSH;
        public DUCE.ResourceHandle Drawing;
    }

    internal sealed class MilVisualBrush : MilTileBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_VISUALBRUSH;
        public DUCE.ResourceHandle Visual;
    }

    internal sealed class MilBitmapCacheBrush : MilBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_BITMAPCACHEBRUSH;
        public DUCE.ResourceHandle BitmapCache;
        public DUCE.ResourceHandle InternalTarget;
    }

    internal sealed class MilImplicitInputBrush : MilBrush
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_IMPLICITINPUTBRUSH;
    }

    // ============================ 几何 ============================

    internal abstract class MilGeometry : MilResource
    {
        public DUCE.ResourceHandle Transform;
    }

    internal sealed class MilLineGeometry : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_LINEGEOMETRY;
        public MilPoint StartPoint, EndPoint;
    }

    internal sealed class MilRectangleGeometry : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_RECTANGLEGEOMETRY;
        public double RadiusX, RadiusY;
        public MilRect Rect;
    }

    internal sealed class MilEllipseGeometry : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_ELLIPSEGEOMETRY;
        public double RadiusX, RadiusY;
        public MilPoint Center;
    }

    internal sealed class MilGeometryGroup : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GEOMETRYGROUP;
        public MilFillRule FillRule;
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
    }

    internal sealed class MilCombinedGeometry : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_COMBINEDGEOMETRY;
        public MilGeometryCombineMode GeometryCombineMode;
        public DUCE.ResourceHandle Geometry1, Geometry2;
    }

    /// <summary>
    /// PathGeometry：原始 FiguresSize 字节的序列化几何体（MIL_PATHGEOMETRY + MIL_PATHFIGURE + 段序列）。
    /// 解析由 Commands/PathGeometrySerializer 负责；这里只保存原始块。
    /// </summary>
    internal sealed class MilPathGeometry : MilGeometry
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_PATHGEOMETRY;
        public MilFillRule FillRule;
        public byte[] SerializedData = Array.Empty<byte>();
    }

    // ============================ 画笔 / 虚线 ============================

    internal sealed class MilDashStyle : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DASHSTYLE;
        public double Offset;
        public readonly List<double> Dashes = new List<double>();
    }

    internal sealed class MilPen : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_PEN;
        public double Thickness = 1.0;
        public double MiterLimit = 10.0;
        public DUCE.ResourceHandle Brush;
        public DUCE.ResourceHandle DashStyle;
        public MilPenLineCap StartLineCap, EndLineCap, DashCap;
        public MilPenLineJoin LineJoin;
    }

    // ============================ Drawing ============================

    internal abstract class MilDrawing : MilResource { }

    internal sealed class MilGeometryDrawing : MilDrawing
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GEOMETRYDRAWING;
        public DUCE.ResourceHandle Brush, Pen, Geometry;
    }

    internal sealed class MilGlyphRunDrawing : MilDrawing
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GLYPHRUNDRAWING;
        public DUCE.ResourceHandle GlyphRun, ForegroundBrush;
    }

    internal sealed class MilImageDrawing : MilDrawing
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_IMAGEDRAWING;
        public MilRect Rect;
        public DUCE.ResourceHandle ImageSource;
    }

    internal sealed class MilVideoDrawing : MilDrawing
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_VIDEODRAWING;
        public MilRect Rect;
        public DUCE.ResourceHandle Player;
    }

    internal sealed class MilDrawingGroup : MilDrawing
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DRAWINGGROUP;
        public double Opacity = 1.0;
        public DUCE.ResourceHandle ClipGeometry, OpacityMask, Transform, GuidelineSet;
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();
        public MilEdgeMode EdgeMode;
        public MilBitmapScalingMode BitmapScalingMode;
        public MilClearTypeHint ClearTypeHint;
    }

    internal sealed class MilDrawingImage : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DRAWINGIMAGE;
        public DUCE.ResourceHandle Drawing;
    }

    // ============================ 效果 / 缓存 ============================

    internal sealed class MilBlurEffect : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_BLUREFFECT;
        public double Radius = 5.0;
        public MilKernelType KernelType;
        public MilRenderingBias RenderingBias;
    }

    internal sealed class MilDropShadowEffect : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_DROPSHADOWEFFECT;
        public double ShadowDepth = 5.0;
        public MilColorF Color;
        public double Direction = 315.0;
        public double Opacity = 1.0;
        public double BlurRadius = 5.0;
        public MilRenderingBias RenderingBias;
    }

    internal sealed class MilBitmapCache : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_BITMAPCACHE;
        public double RenderAtScale = 1.0;
        public bool SnapsToDevicePixels;
        public bool EnableClearType;
    }

    internal sealed class MilGuidelineSet : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GUIDELINESET;
        public readonly List<double> GuidelinesX = new List<double>();
        public readonly List<double> GuidelinesY = new List<double>();
        public bool IsDynamic;
    }

    // ============================ 文本 ============================

    /// <summary>
    /// GlyphRun 资源。M1 只保存头部字段；字形索引/偏移数组跟在命令体之后，
    /// 由 Commands 层解析后填入 GlyphIndices / AdvanceWidths。
    /// </summary>
    internal sealed class MilGlyphRun : MilResource
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GLYPHRUN;
        public ushort Flags;

        /// <summary>
        /// 上游写这个字形 run 时用的**字面标识**，直接来自线格
        /// `MILCMD_GLYPHRUN_CREATE` 的 <c>[FieldOffset(8)] PIDWriteFont</c>。
        /// <para>【T2b 新增】此前**没有这个字段**、dispatcher 也没搬它 ⇒ 面标识在解码时就丢了，
        /// 导致"PC 用哪个面 shaping"这个问题结构上答不了。</para>
        /// <para>⚠ <b>0 是歧义值</b>：可能是真的 0，也可能是"没被填"。所以读它的仪表
        /// （<see cref="WpfGfx.Linux.Rendering.GlyphRunCensus"/>）必须**分别报"全 0 的 run 数"**，
        /// 不能只看非零就去下结论 —— 「静默默认值」是本工程反复踩的一族。</para>
        /// </summary>
        public ulong PIDWriteFont;

        public MilPoint2F Origin;
        public float MuSize;
        public MilRect ManagedBounds;
        public ushort BidiLevel;
        public ushort DWriteTextMeasuringMethod;
        public ushort[] GlyphIndices = Array.Empty<ushort>();
        public float[] AdvanceWidths = Array.Empty<float>();
        public MilPoint[] GlyphOffsets = Array.Empty<MilPoint>();
    }

    // ============================ 呈现目标 ============================

    internal abstract class MilTarget : MilResource
    {
        public DUCE.ResourceHandle Root;
        public MilColorF ClearColor;
        public uint Width, Height;
        public uint Flags;
        public IntPtr NativeWindow;   // X11 Window（M1 用 X11 句柄模拟 HWND）
        public IntPtr SectionHandle;
        public double DpiX = 96.0, DpiY = 96.0;

        // ---- MilCmdTargetUpdateWindowSettings(0x33) ----
        public MilRectI WindowRect;
        public MilWindowLayerType WindowLayerType;
        public MilTransparencyFlags TransparencyMode;
        public float ConstantAlpha;
        public bool IsChild;
        public bool IsRTL;
        public bool RenderingEnabled = true;
        public MilColorF ColorKey;
        public uint DisableCookie;
        public uint GdiBlt;

        // ---- MilCmdTargetInvalidate(0x37) ----
        /// <summary>最近一次 Invalidate 的区域。</summary>
        public MilRectI InvalidRect;

        /// <summary>累计 Invalidate 次数（测试与诊断用）。</summary>
        public int InvalidateCount;
    }

    internal sealed class MilHwndTarget : MilTarget
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_HWNDRENDERTARGET;
        public ulong MasterDevice;
        public DUCE.ResourceHandle Bitmap;
        public uint Stride;
        public uint PixelFormat;
        public int DpiAwarenessContext;
        public bool SuppressLayered;
    }

    internal sealed class MilGenericTarget : MilTarget
    {
        public override DUCE.ResourceType Type => DUCE.ResourceType.TYPE_GENERICRENDERTARGET;
        public ulong RenderTargetPointer;
        public uint Dummy;
    }
}
