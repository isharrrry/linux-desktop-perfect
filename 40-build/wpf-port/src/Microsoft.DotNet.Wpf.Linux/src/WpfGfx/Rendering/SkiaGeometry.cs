// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 几何 → SKPath。
//
// 覆盖 handoff §T4 要求的四类：Line / Rectangle / Ellipse / Path（含 PathGeometry
// 的线段与贝塞尔），另外把 GeometryGroup 与 CombinedGeometry 也做了——它们本来就是
// MilDrawGeometry 会拿到的句柄类型，不做就是静默丢图形。
//
// 几何自带的 Transform（MilGeometry.Transform）在这里合成进路径点，
// 而不是留给 canvas：因为同一条路径还要拿去算 RelativeToBoundingBox 的包围盒，
// 矩阵必须先落定。

using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class SkiaGeometry
    {
        /// <summary>句柄 → SKPath；空句柄 / 资源缺失 / 类型未支持返回 null。</summary>
        public static SKPath ToPath(MilResourceProvider provider, MilResourceHandle handle)
        {
            if (handle.IsNull || provider == null) return null;
            if (provider.Lookup(handle) is not MilGeometry geometry) return null;
            return ToPath(provider, geometry);
        }

        public static SKPath ToPath(MilResourceProvider provider, MilGeometry geometry)
        {
            SKPath path = Build(provider, geometry);
            if (path == null) return null;

            SKMatrix transform = provider.ResolveTransform(new MilResourceHandle((uint)geometry.Transform));
            if (!transform.IsIdentity)
            {
                SKPath transformed = new SKPath();
                path.Transform(transform, transformed);
                path.Dispose();
                return transformed;
            }
            return path;
        }

        private static SKPath Build(MilResourceProvider provider, MilGeometry geometry)
        {
            switch (geometry)
            {
                case MilLineGeometry line:
                {
                    var path = new SKPath();
                    path.MoveTo((float)line.StartPoint.X, (float)line.StartPoint.Y);
                    path.LineTo((float)line.EndPoint.X, (float)line.EndPoint.Y);
                    return path;
                }

                case MilRectangleGeometry rect:
                {
                    var path = new SKPath();
                    SKRect r = new SKRect(
                        (float)rect.Rect.Left, (float)rect.Rect.Top,
                        (float)rect.Rect.Right, (float)rect.Rect.Bottom);
                    if (rect.RadiusX > 0.0 && rect.RadiusY > 0.0)
                        path.AddRoundRect(r, (float)rect.RadiusX, (float)rect.RadiusY);
                    else
                        path.AddRect(r);
                    return path;
                }

                case MilEllipseGeometry ellipse:
                {
                    var path = new SKPath();
                    path.AddOval(new SKRect(
                        (float)(ellipse.Center.X - ellipse.RadiusX),
                        (float)(ellipse.Center.Y - ellipse.RadiusY),
                        (float)(ellipse.Center.X + ellipse.RadiusX),
                        (float)(ellipse.Center.Y + ellipse.RadiusY)));
                    return path;
                }

                case MilPathGeometry pg:
                    return PathGeometryParser.Parse(pg.SerializedData, pg.FillRule);

                case MilGeometryGroup group:
                {
                    var combined = new SKPath
                    {
                        FillType = group.FillRule == MilFillRule.Nonzero
                            ? SKPathFillType.Winding
                            : SKPathFillType.EvenOdd,
                    };
                    foreach (DUCE.ResourceHandle child in group.Children)
                    {
                        SKPath childPath = ToPath(provider, new MilResourceHandle((uint)child));
                        if (childPath == null) continue;
                        combined.AddPath(childPath);
                        childPath.Dispose();
                    }
                    return combined;
                }

                case MilCombinedGeometry combined:
                {
                    SKPath a = ToPath(provider, new MilResourceHandle((uint)combined.Geometry1));
                    SKPath b = ToPath(provider, new MilResourceHandle((uint)combined.Geometry2));
                    if (a == null && b == null) return null;
                    if (a == null) return b;
                    if (b == null) return a;

                    var result = new SKPath();
                    bool ok = a.Op(b, PathOpOf(combined.GeometryCombineMode), result);
                    a.Dispose();
                    b.Dispose();
                    if (!ok) { result.Dispose(); return null; }
                    return result;
                }

                default:
                    return null;   // 3D / 流几何等未建模类型
            }
        }

        /// <summary>
        /// GeometryCombineMode → SKPathOp。
        /// ⚠️ Exclude 的映射存疑：D2D 的 EXCLUDE 语义是"从第一个区域里挖掉第二个"，
        /// 这里按 Difference 处理；WPF 文档对 Exclude 的描述与 Xor 容易混淆，未经真机比对。
        /// </summary>
        private static SKPathOp PathOpOf(MilGeometryCombineMode mode) => mode switch
        {
            MilGeometryCombineMode.Intersect => SKPathOp.Intersect,
            MilGeometryCombineMode.Xor => SKPathOp.Xor,
            MilGeometryCombineMode.Exclude => SKPathOp.Difference,
            MilGeometryCombineMode.Union => SKPathOp.Union,
            _ => SKPathOp.Union,
        };
    }
}
