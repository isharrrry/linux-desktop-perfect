// Licensed to the .NET Foundation under one or more agreements.
//
// MIL 画刷 → SKPaint（填充样式）。
//
// 支持的四种（对应 MilResourceType 0x7e/0x7f/0x80/0x7d，即
// TYPE_SOLIDCOLORBRUSH / TYPE_LINEARGRADIENTBRUSH / TYPE_RADIALGRADIENTBRUSH /
// TYPE_IMAGEBRUSH）：
//   SolidColorBrush     → 纯色
//   LinearGradientBrush → SKShader.CreateLinearGradient
//   RadialGradientBrush → 圆用 CreateRadialGradient；椭圆/偏移焦点用两点圆锥
//   ImageBrush          → SKShader.CreateBitmap 平铺（Viewbox/Viewport/Stretch/Alignment）
//
// 已知的、诚实的简化（都没有偷偷吞掉，见 docs/unimplemented.md 的 T4 段）：
//   1. ColorInterpolationMode（scRGB/sRGB 插值空间）未实现——Skia 的渐变只在
//      固定色彩空间插值，WPF 的 SRgbLinearInterpolation 与之不等价。
//   2. DrawingBrush / VisualBrush / BitmapCacheBrush 未实现——需要 Drawing 子图
//      与视觉树嵌套渲染的位图资源模型，本层只返回 null（= 不填充）。
//   3. RelativeToBoundingBox 需要调用方传 bounds；传空矩形时退化成单位映射。
//   4. 【已在 T2b 修正，保留原委】FlipX/FlipY 曾用「两轴都 Mirror」近似 ⇒ FlipX 与 FlipY
//      输出逐字节相同（旧 golden 两张同 sha256 0cc3ea27…）。Skia 的平铺模式是按轴独立的，
//      (Mirror, Repeat) 即 WPF 的 FlipX、(Repeat, Mirror) 即 FlipY、(Mirror, Mirror) 即 FlipXY。
//      见 ImageTileMode 的注释与 tests/parity/brushes/ 的真机 oracle。
//   5. 【仍未实现，登记】Viewport 用**绝对单位**时，WPF 以「被填充图形的局部坐标系」为原点
//      （原点 = 包围盒左上角），本文件目前按画布原点解释 ⇒ 见 TileViewport 的注释与差距登记。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class SkiaBrush
    {
        /// <summary>
        /// 句柄 → 填充用的 SKPaint。句柄为空、资源缺失、或画刷类型未支持时返回 null。
        /// </summary>
        /// <param name="bounds">几何包围盒（用户坐标），RelativeToBoundingBox 映射需要。</param>
        public static SKPaint CreateFill(
            MilResourceProvider provider, MilResourceHandle handle, SKRect bounds, bool antialias,
            MilBitmapScalingMode scaling = MilBitmapScalingMode.Unspecified)
        {
            if (handle.IsNull || provider == null) return null;
            if (provider.Lookup(handle) is not MilBrush brush) return null;

            float opacity = (float)brush.Opacity;
            if (opacity <= 0f) return null;

            // ⚠ TileBrush 的取样滤波器：**只对 ImageBrush 开**，见 ImageFilterQuality 的注释。
            var paint = new SKPaint { IsAntialias = antialias, Style = SKPaintStyle.Fill };

            switch (brush)
            {
                case MilSolidColorBrush solid:
                    paint.Color = SkiaColor.WithOpacity(SkiaColor.FromMilColorF(solid.Color), opacity);
                    return paint;

                case MilLinearGradientBrush linear:
                {
                    SKShader shader = CreateLinearGradient(provider, linear, bounds, opacity);
                    if (shader == null) { paint.Dispose(); return null; }
                    paint.Shader = shader;
                    return paint;
                }

                case MilRadialGradientBrush radial:
                {
                    SKShader shader = CreateRadialGradient(provider, radial, bounds, opacity);
                    if (shader == null) { paint.Dispose(); return null; }
                    paint.Shader = shader;
                    return paint;
                }

                case MilImageBrush imageBrush:
                {
                    SKFilterQuality quality = ImageFilterQuality(scaling, imageBrush, bounds, provider);
                    SKShader shader = CreateImageFill(provider, imageBrush, bounds, opacity, quality);
                    if (shader == null) { paint.Dispose(); return null; }
                    paint.Shader = shader;
                    paint.FilterQuality = quality;
                    return paint;
                }

                case MilDrawingBrush drawingBrush:
                {
                    SKShader shader = CreateDrawingFill(provider, drawingBrush, bounds, opacity);
                    if (shader == null) { paint.Dispose(); return null; }
                    paint.Shader = shader;
                    return paint;
                }

                case MilBitmapCacheBrush cacheBrush:
                {
                    // 软件渲染下**没有缓存层**：与"直接渲染 Target"等价 ⇒ 透传 InternalTarget。
                    // ⚠ 缓存语义（位图缓存、CachingHint、缩放重采样缓存）**未实现**，已登记；
                    //   这里不假装有缓存——画出来必须与直接渲染目标逐像素一致。
                    paint.Dispose();
                    if (cacheBrush.InternalTarget.IsNull) return null;
                    SKPaint target = CreateFill(provider, new MilResourceHandle((uint)cacheBrush.InternalTarget),
                                                bounds, antialias);
                    if (target == null) return null;
                    if (cacheBrush.Opacity < 1.0)
                    {
                        // 缓存画刷自身的 Opacity：着色器画刷折进 alpha，纯色画刷直接乘 alpha。
                        if (target.Shader != null)
                        {
                            SKShader filtered = SKShader.CreateColorFilter(target.Shader, AlphaFilter((float)cacheBrush.Opacity));
                            target.Shader.Dispose();
                            target.Shader = filtered;
                        }
                        else target.Color = target.Color.WithAlpha((byte)Math.Round(target.Color.Alpha * cacheBrush.Opacity));
                    }
                    return target;
                }

                case MilVisualBrush visualBrush:
                {
                    SKShader shader = CreateVisualFill(provider, visualBrush, bounds, opacity);
                    if (shader == null) { paint.Dispose(); return null; }
                    paint.Shader = shader;
                    return paint;
                }

                default:
                    paint.Dispose();
                    return null;
            }
        }

        private static SKShader CreateLinearGradient(
            MilResourceProvider provider, MilLinearGradientBrush brush, SKRect bounds, float opacity)
        {
            (SKColor[] colors, float[] positions) = Stops(brush.GradientStops, opacity);
            if (colors == null) return null;

            SKMatrix mapping = MappingMatrix(brush.MappingMode, bounds);
            SKMatrix brushMatrix = BrushMatrix(provider, brush);

            // 行向量约定：p·mapping·brushMatrix —— 先做 [0,1]→包围盒 的单位映射，再套画刷变换。
            SKShader shader = SKShader.CreateLinearGradient(
                new SKPoint((float)brush.StartPoint.X, (float)brush.StartPoint.Y),
                new SKPoint((float)brush.EndPoint.X, (float)brush.EndPoint.Y),
                colors, positions, TileMode(brush.SpreadMethod));

            return ApplyMatrix(shader, SKMatrix.Concat(mapping, brushMatrix));
        }

        private static SKShader CreateRadialGradient(
            MilResourceProvider provider, MilRadialGradientBrush brush, SKRect bounds, float opacity)
        {
            (SKColor[] colors, float[] positions) = Stops(brush.GradientStops, opacity);
            if (colors == null) return null;

            float rx = (float)brush.RadiusX;
            float ry = (float)brush.RadiusY;
            if (rx <= 0f || ry <= 0f) return null;

            SKMatrix mapping = MappingMatrix(brush.MappingMode, bounds);
            SKMatrix brushMatrix = BrushMatrix(provider, brush);

            SKPoint center = new SKPoint((float)brush.Center.X, (float)brush.Center.Y);
            SKPoint origin = new SKPoint((float)brush.GradientOrigin.X, (float)brush.GradientOrigin.Y);
            SKShaderTileMode tile = TileMode(brush.SpreadMethod);

            SKShader shader;
            if (origin == center && Math.Abs(rx - ry) < 1e-6f)
            {
                shader = SKShader.CreateRadialGradient(center, rx, colors, positions, tile);
            }
            else
            {
                // 椭圆半径：先做径向（圆），再用矩阵把 y 压成 ry/rx —— 与 WPF 的椭圆渐变一致。
                // 焦点偏移（GradientOrigin≠Center）：用两点圆锥，起点半径 0 即 WPF 的焦点语义。
                SKMatrix ellipse = SKMatrix.CreateScale(1f, ry / rx, center.X, center.Y);
                shader = SKShader.CreateTwoPointConicalGradient(origin, 0f, center, rx, colors, positions, tile);
                return ApplyMatrix(shader, SKMatrix.Concat(SKMatrix.Concat(mapping, ellipse), brushMatrix));
            }

            return ApplyMatrix(shader, SKMatrix.Concat(mapping, brushMatrix));
        }

        private static SKShader ApplyMatrix(SKShader shader, SKMatrix matrix)
        {
            if (shader == null) return null;
            if (matrix.IsIdentity) return shader;
            SKShader local = shader.WithLocalMatrix(matrix);
            shader.Dispose();
            return local;
        }

        // ============================ ImageBrush（TileBrush） ============================

        /// <summary>
        /// ImageBrush → 位图平铺着色器。WPF TileBrush 语义链：
        /// Viewbox 选源区 → Stretch/Alignment 映射到 Viewport 基块 → TileMode 平铺 → 画刷变换。
        /// opacity 折进 alpha（Skia 的 shader 不消费 paint.Color，必须显式乘）。
        /// </summary>
        private static SKShader CreateImageFill(
            MilResourceProvider provider, MilImageBrush brush, SKRect bounds, float opacity,
            SKFilterQuality quality = SKFilterQuality.None)
        {
            SKBitmap bitmap = provider.LookupBitmap(new MilResourceHandle((uint)brush.ImageSource));
            if (bitmap == null) return null;

            SKRect viewbox = TileViewbox(brush, bitmap);
            if (viewbox.Width <= 0 || viewbox.Height <= 0) return null;
            SKRect viewport = TileViewport(brush, bounds);
            if (viewport.Width <= 0 || viewport.Height <= 0) return null;

            SKMatrix mapping = TileBrushMapping(
                viewbox, viewport, brush.Stretch, brush.AlignmentX, brush.AlignmentY);
            SKMatrix brushMatrix = BrushMatrix(provider, brush);

            // 【TileMode.None：不走着色器平铺，改用"基准格离屏栅格"】
            //   真机（批次1 core_image_none_v1same 的 (17,17)）是**纯 #FFD02020**，
            //   即"**边缘钳位**的双线性"；而 Skia 的 Decal 会让滤波抽头看到透明，
            //   实测得 #FFB54040（= 0.666×红 + 0.334×背景灰 #FF808080）—— 差得很远。
            //   正解 = "先按边缘钳位求出基准格，再按基准格裁剪"：
            //     ① 把源按 mapping 画进一张**基准格大小**的离屏位图（DrawBitmap 天然在源边界钳位）；
            //     ② 再用这张 1:1 的栅格做 Decal 着色器 —— 基准格之外透明 ⇒ 背景透出来。
            //   基准格之外**不画**（不是钳位延伸），这条与真机规则 4 一致。
            if (brush.TileMode == MilTileMode.None)
            {
                SKShader noneShader = CreateNoneBaseTileShader(
                    bitmap, viewbox, mapping, brushMatrix, viewport, quality);
                if (noneShader == null) return null;
                if (opacity < 1f)
                {
                    SKShader filtered0 = SKShader.CreateColorFilter(noneShader, AlphaFilter(opacity));
                    noneShader.Dispose();
                    return filtered0;
                }
                return noneShader;
            }

            (SKShaderTileMode tileX, SKShaderTileMode tileY) = ImageTileMode(brush.TileMode);
            // ⚠ Concat(a, b) 的语义是 b 先作用（SkiaSharp 实测，别按"从左到右"理解）。
            // 画刷变换作用在 mapping 的输出空间上：先 Viewbox→Viewport 映射，再套画刷变换。
            SKShader shader = SKShader.CreateBitmap(
                bitmap, tileX, tileY, SKMatrix.Concat(brushMatrix, mapping));
            if (shader == null) return null;

            if (opacity < 1f)
            {
                SKShader filtered = SKShader.CreateColorFilter(shader, AlphaFilter(opacity));
                shader.Dispose();
                return filtered;
            }
            return shader;
        }

        /// <summary>
        /// BitmapScalingMode → <see cref="SKFilterQuality"/>。
        ///
        /// 【为什么只有这几个值】SkiaSharp 被 T0 锁在 2.88.9：`SKShader.CreateBitmap/CreateImage`
        /// 只有 4 个重载且**都不带采样参数**（`SKFilterMode`/`SKSamplingOptions` 类型不存在），
        /// 所以着色器侧无法指定采样，唯一旋钮是**使用该着色器的 paint** 上的 `FilterQuality`。
        ///
        /// 【真机口径（tests/parity/brushes 批次 2，variantComparisons）】
        ///   · `Unspecified ≡ Linear`：**0 个采样点差异** ⇒ 默认档必须按双线性实现；
        ///   · `NearestNeighbor` 与它们差 68（放大）/187（缩小）点 ⇒ 必须真是最近邻；
        ///   · `HighQuality` **只在缩小时**与 Linear 不同（缩小 409 点），放大时与 Linear 相同（0 点）
        ///     ⇒ 放大退化成 Linear。
        /// 注意 `MilBitmapScalingMode` 里 `Linear == LowQuality`、`HighQuality == Fant`
        /// 是**同值别名**（与上游一致），所以 case 无法也不需要分开写。
        /// </summary>
        internal static SKFilterQuality FilterQualityFor(MilBitmapScalingMode mode, float scale)
        {
            switch (mode)
            {
                case MilBitmapScalingMode.NearestNeighbor:
                    return SKFilterQuality.None;

                // Linear / LowQuality（同值）/ Unspecified：双线性
                case MilBitmapScalingMode.Unspecified:
                case MilBitmapScalingMode.LowQuality:
                    return SKFilterQuality.Low;

                // HighQuality / Fant（同值）：只在**缩小**时升级，放大退化成 Linear
                case MilBitmapScalingMode.HighQuality:
                    return scale < 1f ? SKFilterQuality.High : SKFilterQuality.Low;

                default:
                    return SKFilterQuality.Low;
            }
        }

        /// <summary>
        /// 取 ImageBrush 的**有效缩放比**（源像素 → 设备像素），用于判断放大/缩小。
        /// 由 Viewbox→Viewport 的映射给出，再乘上画刷变换的尺度（若可分解）。
        /// </summary>
        private static float EffectiveScale(
            SKRect viewbox, SKRect viewport, SKMatrix brushMatrix)
        {
            float sx = viewbox.Width > 0f ? viewport.Width / viewbox.Width : 1f;
            float sy = viewbox.Height > 0f ? viewport.Height / viewbox.Height : 1f;
            float s = Math.Min(Math.Abs(sx), Math.Abs(sy));
            float bx = Math.Abs(brushMatrix.ScaleX), by = Math.Abs(brushMatrix.ScaleY);
            float bs = Math.Min(bx > 0f ? bx : 1f, by > 0f ? by : 1f);
            return s * bs;
        }

        /// <summary>ImageBrush 的取样档位（含有效缩放，供 HighQuality 判放大/缩小）。</summary>
        private static SKFilterQuality ImageFilterQuality(
            MilBitmapScalingMode scaling, MilImageBrush brush, SKRect bounds, MilResourceProvider provider)
        {
            // ⚠ 不要在这里为 NearestNeighbor 加"早返回"捷径：那会让 FilterQualityFor 里的
            //   NN 分支变成**死代码**，而"死代码看起来是活的"正是本工程反复踩的坑
            //   （T2b 的突变实验就被它骗过一次：改了 FilterQualityFor 却毫无效果，
            //   于是"断言有牙"这个结论当时是**假的**）。档位判定只有 FilterQualityFor 一处。
            SKBitmap bitmap = provider.LookupBitmap(new MilResourceHandle((uint)brush.ImageSource));
            if (bitmap == null) return FilterQualityFor(scaling, 1f);

            SKRect viewbox = TileViewbox(brush, bitmap);
            SKRect viewport = TileViewport(brush, bounds);
            return FilterQualityFor(scaling, EffectiveScale(viewbox, viewport, BrushMatrix(provider, brush)));
        }

        /// <summary>
        /// `TileMode.None` 专用的基准格着色器：把源按 mapping 画进一张**基准格大小**的离屏位图
        /// （`DrawBitmap` 天然在源边界做**边缘钳位**），再用它做 1:1 的 Decal 着色器。
        /// 基准格之外透明 ⇒ 背景透出来（真机规则 4：None 只画基准格，其余是背景，不是钳位延伸）。
        /// </summary>
        private static SKShader CreateNoneBaseTileShader(
            SKBitmap bitmap, SKRect viewbox, SKMatrix mapping, SKMatrix brushMatrix,
            SKRect viewport, SKFilterQuality quality)
        {
            int w = (int)Math.Ceiling(viewport.Width);
            int h = (int)Math.Ceiling(viewport.Height);
            if (w <= 0 || h <= 0 || (long)w * h > 4096L * 4096L) return null;

            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            using SKSurface surface = SKSurface.Create(info);
            if (surface == null) return null;
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // 基准格局部坐标 (0,0)-(w,h) → 对应的画刷空间位置，再交回 mapping 的逆去取源像素。
            // 用"画刷空间 → 基准格局部"的逆矩阵做画布变换，等价于把 mapping 作用在源上。
            // 【为什么不用 SetMatrix + 逆矩阵】首版把 mapping 求逆当 CTM，结果画了个空；
            //   改成 CTM=mapping 又变成了错比例的采样（实测 (17,17) 得 #FF5E37B5，落在紫三角附近）。
            //   ⇒ 不再玩矩阵方向，直接用 **src/dest 矩形版 DrawBitmap**：语义无歧义，
            //     而且它在源位图边界外天然**钳位**（正是规则 4/7 要的"边缘钳位"）。
            //
            //   dest = mapping(viewbox) 再平移 -viewport 原点 = 内容在**基准格局部**里的落位。
            //   Stretch=None/Uniform 时 dest 小于基准格 ⇒ 其余部分保持透明（与 WPF 一致）。
            SKRect src = viewbox;
            SKRect dest = MapRect(SKMatrix.Concat(brushMatrix, mapping), src);
            dest.Offset(-viewport.Left, -viewport.Top);

            using var paint = new SKPaint { FilterQuality = quality, IsAntialias = false };
            canvas.DrawBitmap(bitmap, src, dest, paint);
            canvas.Flush();

            using SKImage snapshot = surface.Snapshot();
            // 已经 1:1 栅格化好 ⇒ 用 Nearest 取回，不再二次重采样
            return SKShader.CreateImage(snapshot, SKShaderTileMode.Decal, SKShaderTileMode.Decal,
                                        SKMatrix.CreateTranslation(viewport.Left, viewport.Top));
        }

        /// <summary>alpha 缩放滤镜：只改 A 通道（用于把画刷 Opacity 乘到 shader 输出上）。</summary>
        private static SKColorFilter AlphaFilter(float opacity) => SKColorFilter.CreateColorMatrix(new[]
        {
            1f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, opacity, 0f,
        });

        /// <summary>Viewbox（源区）。空矩形 = 整幅位图；RelativeToBoundingBox 按位图尺寸折算。</summary>
        private static SKRect TileViewbox(MilImageBrush brush, SKBitmap bitmap) =>
            TileViewbox(brush, new SKRect(0, 0, bitmap.Width, bitmap.Height));

        /// <summary>Viewbox（源区）通用版：源尺寸由调用方给（位图像素 / Drawing 的包围盒）。</summary>
        private static SKRect TileViewbox(MilTileBrush brush, SKRect source)
        {
            SKRect v = ToSkRect(brush.Viewbox);
            if (brush.ViewboxUnits == MilBrushMappingMode.RelativeToBoundingBox)
            {
                v.Left = source.Left + v.Left * source.Width;
                v.Top = source.Top + v.Top * source.Height;
                v.Right = source.Left + v.Right * source.Width;
                v.Bottom = source.Top + v.Bottom * source.Height;
            }
            if (v.Width <= 0 || v.Height <= 0) return source;
            return v;
        }

        // ============================ DrawingBrush ============================

        /// <summary>
        /// DrawingBrush → 把 Drawing 录成 SKPicture、离屏渲染成 SKImage，再走**与 ImageBrush 完全相同**的
        /// TileBrush 链路（Viewbox=Drawing 包围盒 → Viewport 基块 → Stretch/Alignment → TileMode → 画刷变换）。
        /// 形制照 CreateImageFill 抄（含 `Concat(brushMatrix, mapping)` 的 b-先作用 语义与 None→Decal）。
        /// </summary>
        private static SKShader CreateDrawingFill(
            MilResourceProvider provider, MilDrawingBrush brush, SKRect bounds, float opacity)
        {
            SKRect source = DrawingBounds(provider, brush.Drawing, 0);
            if (source.Width <= 0 || source.Height <= 0) return null;

            SKImage image = RenderDrawing(provider, brush.Drawing, source);
            if (image == null) return null;

            SKRect viewbox = TileViewbox(brush, new SKRect(0, 0, image.Width, image.Height));
            SKRect viewport = TileViewport(brush, bounds);
            if (viewbox.Width <= 0 || viewbox.Height <= 0) return null;
            if (viewport.Width <= 0 || viewport.Height <= 0) { image.Dispose(); return null; }

            SKMatrix mapping = TileBrushMapping(
                viewbox, viewport, brush.Stretch, brush.AlignmentX, brush.AlignmentY);
            SKMatrix brushMatrix = BrushMatrix(provider, brush);

            (SKShaderTileMode tileX, SKShaderTileMode tileY) = ImageTileMode(brush.TileMode);
            SKShader shader = SKShader.CreateImage(image, tileX, tileY, SKMatrix.Concat(brushMatrix, mapping));
            image.Dispose();
            if (shader == null) return null;

            if (opacity < 1f)
            {
                SKShader filtered = SKShader.CreateColorFilter(shader, AlphaFilter(opacity));
                shader.Dispose();
                return filtered;
            }
            return shader;
        }

        // ============================ VisualBrush ============================

        // 递归防护：**显式**的两道闸——深度上限 + 当前渲染链上的 Visual 访问集。
        // 自引用（Visual A 的内容里有一个指向 A 的 VisualBrush）会在这里被拦下，
        // 结果是"该层 tile 画空"而不是栈溢出。
        [ThreadStatic] private static HashSet<uint> s_visualStack;
        [ThreadStatic] private static int s_visualDepth;
        private const int VisualMaxDepth = 6;

        /// <summary>
        /// VisualBrush → 把一个 Visual 离屏渲染成 tile，再走与 Image/DrawingBrush 相同的 TileBrush 链路。
        /// 视觉渲染本身由 provider 的 VisualImageResolver 提供（后端挂载），本函数只负责
        /// **递归防护 + 平铺语义**。
        /// </summary>
        private static SKShader CreateVisualFill(
            MilResourceProvider provider, MilVisualBrush brush, SKRect bounds, float opacity)
        {
            if (provider.VisualImageResolver == null || brush.Visual.IsNull) return null;
            uint key = (uint)brush.Visual;

            s_visualStack ??= new HashSet<uint>();
            if (s_visualDepth >= VisualMaxDepth || s_visualStack.Contains(key))
                return null;                       // 拦下：自引用/环形引用/超深 ⇒ 不画，不递归

            s_visualStack.Add(key);
            s_visualDepth++;
            SKImage image;
            try { image = provider.VisualImageResolver(new MilResourceHandle(key)); }
            finally
            {
                s_visualDepth--;
                s_visualStack.Remove(key);
            }
            if (image == null) return null;

            SKRect viewbox = TileViewbox(brush, new SKRect(0, 0, image.Width, image.Height));
            SKRect viewport = TileViewport(brush, bounds);
            if (viewbox.Width <= 0 || viewbox.Height <= 0) { image.Dispose(); return null; }
            if (viewport.Width <= 0 || viewport.Height <= 0) { image.Dispose(); return null; }

            SKMatrix mapping = TileBrushMapping(
                viewbox, viewport, brush.Stretch, brush.AlignmentX, brush.AlignmentY);
            SKMatrix brushMatrix = BrushMatrix(provider, brush);
            (SKShaderTileMode tileX, SKShaderTileMode tileY) = ImageTileMode(brush.TileMode);
            SKShader shader = SKShader.CreateImage(image, tileX, tileY, SKMatrix.Concat(brushMatrix, mapping));
            image.Dispose();
            if (shader == null) return null;

            if (opacity < 1f)
            {
                SKShader filtered = SKShader.CreateColorFilter(shader, AlphaFilter(opacity));
                shader.Dispose();
                return filtered;
            }
            return shader;
        }

        /// <summary>
        /// 供 <see cref="VisualBrushSource"/> 复用：Drawing 的包围盒（VisualBrush 的自然尺寸要把
        /// MilDrawDrawing 指令算进去，必须与 DrawingBrush 的 Viewbox 用**同一把尺子**，
        /// 否则同一条 Drawing 在两种画刷下会得到不同的源尺寸）。
        /// </summary>
        internal static SKRect DrawingBoundsOf(MilResourceProvider provider, MilResourceHandle handle) =>
            handle.IsNull ? SKRect.Empty : DrawingBounds(provider, new DUCE.ResourceHandle(handle.Value), 0);

        /// <summary>Drawing 的包围盒（用于 Viewbox 与离屏画布尺寸）。描边按笔宽外扩半个笔宽。</summary>
        private static SKRect DrawingBounds(MilResourceProvider provider, DUCE.ResourceHandle handle, int depth)
        {
            if (depth > 8) return SKRect.Empty;
            if (provider.Lookup(new MilResourceHandle((uint)handle)) is not MilDrawing drawing) return SKRect.Empty;

            switch (drawing)
            {
                case MilGeometryDrawing geometryDrawing:
                {
                    using SKPath path = SkiaGeometry.ToPath(provider, new MilResourceHandle((uint)geometryDrawing.Geometry));
                    if (path == null) return SKRect.Empty;
                    SKRect r = path.Bounds;
                    if (!geometryDrawing.Pen.IsNull &&
                        provider.Lookup(new MilResourceHandle((uint)geometryDrawing.Pen)) is MilPen pen)
                    {
                        float half = (float)(pen.Thickness / 2.0);
                        r.Inflate(half, half);
                    }
                    return r;
                }
                case MilImageDrawing imageDrawing:
                    return ToSkRect(imageDrawing.Rect);
                case MilGlyphRunDrawing glyphRunDrawing:
                {
                    // 字形包围盒 = `MilGlyphRun.ManagedBounds`（上游托管侧 GlyphRun.Bounds，
                    // 由 MilCommandDispatcher.cs:906 填入）。**不用在这里算文字度量**，
                    // 也就不必碰 Text/**。此前本分支缺失 ⇒ 只含字形的 Drawing 包围盒为空
                    // ⇒ DrawingBrush/VisualBrush 的 Viewbox 退化
                    // （Rendering.Tests 的 drawing_with_only_glyph_run_bounds_need_text_metrics 正因此跳过）。
                    if (provider.Lookup(new MilResourceHandle((uint)glyphRunDrawing.GlyphRun)) is not MilGlyphRun run)
                        return SKRect.Empty;
                    return ToSkRect(run.ManagedBounds);
                }
                case MilDrawingGroup group:
                {
                    SKRect r = SKRect.Empty;
                    foreach (DUCE.ResourceHandle child in group.Children)
                    {
                        SKRect c = DrawingBounds(provider, child, depth + 1);
                        if (c.Width <= 0 || c.Height <= 0) continue;
                        r = (r.Width <= 0 && r.Height <= 0) ? c : SKRect.Union(r, c);
                    }
                    return r;
                }
                default:
                    return SKRect.Empty;
            }
        }

        /// <summary>把 Drawing 录进 SKPicture 并离屏渲染成 SKImage（源坐标系平移到 (0,0)）。</summary>
        private static SKImage RenderDrawing(MilResourceProvider provider, DUCE.ResourceHandle handle, SKRect source)
        {
            using var recorder = new SKPictureRecorder();
            SKCanvas canvas = recorder.BeginRecording(source);
            DrawDrawing(provider, canvas, handle, 0);
            using SKPicture picture = recorder.EndRecording();

            int w = (int)Math.Ceiling(source.Width);
            int h = (int)Math.Ceiling(source.Height);
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;   // 防炸：超大 drawing 不渲染

            using SKSurface surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface == null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(-source.Left, -source.Top);
            surface.Canvas.DrawPicture(picture);
            return surface.Snapshot();
        }

        /// <summary>递归绘制 Drawing（GeometryDrawing / ImageDrawing / DrawingGroup）。</summary>
        private static void DrawDrawing(MilResourceProvider provider, SKCanvas canvas, DUCE.ResourceHandle handle, int depth)
        {
            if (depth > 8) return;
            if (provider.Lookup(new MilResourceHandle((uint)handle)) is not MilDrawing drawing) return;

            switch (drawing)
            {
                case MilGeometryDrawing geometryDrawing:
                {
                    using SKPath path = SkiaGeometry.ToPath(provider, new MilResourceHandle((uint)geometryDrawing.Geometry));
                    if (path == null) break;
                    SKRect pathBounds = path.Bounds;
                    if (!geometryDrawing.Brush.IsNull)
                    {
                        SKPaint fill = CreateFill(provider, new MilResourceHandle((uint)geometryDrawing.Brush), pathBounds, true);
                        if (fill != null) { canvas.DrawPath(path, fill); fill.Dispose(); }
                    }
                    if (!geometryDrawing.Pen.IsNull)
                    {
                        SKPaint stroke = SkiaPen.CreateStroke(provider, new MilResourceHandle((uint)geometryDrawing.Pen), pathBounds, true);
                        if (stroke != null) { canvas.DrawPath(path, stroke); stroke.Dispose(); }
                    }
                    break;
                }
                case MilImageDrawing imageDrawing:
                {
                    SKBitmap bitmap = provider.LookupBitmap(new MilResourceHandle((uint)imageDrawing.ImageSource));
                    if (bitmap != null) canvas.DrawBitmap(bitmap, ToSkRect(imageDrawing.Rect));
                    break;
                }
                case MilDrawingGroup group:
                {
                    int save = canvas.Save();

                    // ① Transform（绝对变换）：WPF 里它作用于**整个组**（含组的 Clip 与子项）。
                    //    不接它的后果是**静默错渲**（不报错、不空白，只是位置/朝向错），所以优先接。
                    SKMatrix groupMatrix = SKMatrix.CreateIdentity();
                    bool hasGroupMatrix = !group.Transform.IsNull;
                    if (hasGroupMatrix)
                    {
                        groupMatrix = provider.ResolveTransform(new MilResourceHandle((uint)group.Transform));
                        canvas.Concat(ref groupMatrix);
                    }

                    // ② Opacity / OpacityMask 都需要一个 layer（内容先画进去，再整体调制 alpha）。
                    bool layered = group.Opacity < 1.0 || !group.OpacityMask.IsNull;
                    SKRect maskRect = SKRect.Empty;
                    if (!group.OpacityMask.IsNull)
                    {
                        // mask 画刷作用在**组的内容包围盒**上；组自身有 Transform 时包围盒随之变换。
                        maskRect = DrawingBounds(provider, handle, depth);
                        if (hasGroupMatrix) maskRect = groupMatrix.MapRect(maskRect);
                    }
                    if (layered)
                    {
                        using var layerPaint = new SKPaint
                        {
                            Color = SKColors.White.WithAlpha((byte)Math.Round(255 * (group.Opacity < 1.0 ? group.Opacity : 1.0))),
                        };
                        canvas.SaveLayer(layerPaint);
                    }

                    if (!group.ClipGeometry.IsNull)
                    {
                        using SKPath clip = SkiaGeometry.ToPath(provider, new MilResourceHandle((uint)group.ClipGeometry));
                        if (clip != null) canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                    }
                    foreach (DUCE.ResourceHandle child in group.Children)
                        DrawDrawing(provider, canvas, child, depth + 1);

                    // ③ OpacityMask：把 mask 画刷以 DstIn 画在内容上 ⇒ dst.a *= src.a（常规路子）。
                    if (!group.OpacityMask.IsNull && maskRect.Width > 0 && maskRect.Height > 0)
                    {
                        SKPaint maskPaint = CreateFill(provider, new MilResourceHandle((uint)group.OpacityMask),
                                                       maskRect, antialias: true);
                        if (maskPaint != null)
                        {
                            using var dstIn = new SKPaint
                            {
                                BlendMode = SKBlendMode.DstIn,
                                Shader = maskPaint.Shader,
                                Color = maskPaint.Color,
                                IsAntialias = true,
                            };
                            canvas.DrawRect(maskRect, dstIn);
                            maskPaint.Dispose();
                        }
                    }

                    canvas.RestoreToCount(save);
                    break;
                }
                case MilGlyphRunDrawing glyphRunDrawing:
                {
                    // 与 SkiaRenderBackend 里 MilGlyphRunDrawing 的处理同法（:501-508）：
                    // 前景画刷 + provider 的字形扩展点。没有注册渲染器时**画不出来**——
                    // 这正是"静默不画"的来源，故此处与后端保持同一路径，不再各写一份。
                    using SKPaint foreground = CreateFill(
                        provider, new MilResourceHandle((uint)glyphRunDrawing.ForegroundBrush), SKRect.Empty, true);
                    if (foreground != null)
                    {
                        provider.TryRenderGlyphRun(
                            canvas, new MilResourceHandle((uint)glyphRunDrawing.GlyphRun), foreground);
                    }
                    break;
                }

                default:
                    break;
            }
        }

        /// <summary>
        /// Viewport（目标基块）。空矩形 = 整个包围盒。
        ///
        /// ⚠ **绝对单位的原点是"被填充图形的局部坐标系"（= 包围盒左上角），不是画布 (0,0)。**
        ///   真机 A/B 假设检验（tests/parity/brushes/windows-results.json 的
        ///   units_vpabs_viewboxabs_tile / units_vpabs_viewboxrel_tile）：
        ///     · 「包围盒局部原点」 194/194 吻合 ⇒ 瓦片原点实测 (26,26) = (16,16) + Viewport(10,10)
        ///     · 「画布原点」       20/216 吻合（即错的那个）
        ///   所以 Absolute 档要和 RelativeToBoundingBox 一样**加上 bounds 原点**，
        ///   只是不加尺寸缩放。曾按画布原点解释 ⇒ 瓦片网格整体错位一个包围盒偏移。
        /// </summary>
        private static SKRect TileViewport(MilTileBrush brush, SKRect bounds)
        {
            SKRect v = ToSkRect(brush.Viewport);
            if (brush.ViewportUnits == MilBrushMappingMode.RelativeToBoundingBox)
            {
                v.Left = bounds.Left + v.Left * bounds.Width;
                v.Top = bounds.Top + v.Top * bounds.Height;
                v.Right = bounds.Left + v.Right * bounds.Width;
                v.Bottom = bounds.Top + v.Bottom * bounds.Height;
            }
            else
            {
                // Absolute：平移包围盒原点，尺寸原样（真机 (26,26) 那条）
                v.Left += bounds.Left;
                v.Right += bounds.Left;
                v.Top += bounds.Top;
                v.Bottom += bounds.Top;
            }
            if (v.Width <= 0 || v.Height <= 0)
                return bounds;
            return v;
        }

        /// <summary>
        /// Viewbox → Viewport 的映射矩阵（列向量约定：先平移去源区原点，再缩放，后平移到目标）。
        /// Stretch: None=1:1（对齐忽略）；Fill=两轴各自拉伸（对齐忽略）；
        /// Uniform=等比内接、UniformToFill=等比外接（按 AlignmentX/Y 定位）。
        /// </summary>
        private static SKMatrix TileBrushMapping(
            SKRect viewbox, SKRect viewport, MilStretch stretch,
            MilAlignmentX alignX, MilAlignmentY alignY)
        {
            double sx = viewport.Width / viewbox.Width;
            double sy = viewport.Height / viewbox.Height;

            float scaleX, scaleY, ox = 0f, oy = 0f;
            switch (stretch)
            {
                case MilStretch.None:
                    scaleX = scaleY = 1f;
                    break;
                case MilStretch.Fill:
                    scaleX = (float)sx; scaleY = (float)sy;
                    break;
                case MilStretch.UniformToFill:
                {
                    float s = (float)Math.Max(sx, sy);
                    scaleX = scaleY = s;
                    ox = AlignOffset(viewport.Width, viewbox.Width * s, alignX);
                    oy = AlignOffset(viewport.Height, viewbox.Height * s, alignY);
                    break;
                }
                default: // Uniform
                {
                    float s = (float)Math.Min(sx, sy);
                    scaleX = scaleY = s;
                    ox = AlignOffset(viewport.Width, viewbox.Width * s, alignX);
                    oy = AlignOffset(viewport.Height, viewbox.Height * s, alignY);
                    break;
                }
            }

            // ⚠ Concat(a, b) 的语义是 b 先作用（SkiaSharp 实测）。本链要求的应用顺序：
            // 先减源区原点 → 再缩放 → 后平移（列向量 M = T·S·T(-vb)），故按此倒序嵌套：
            // step1 = Concat(Scale, T(-vb))  → T(-vb) 先、Scale 后
            // m     = Concat(T(vp+o), step1) → step1 先、T(vp+o) 后
            // 曾踩过坑：写成 Concat(Scale, T(vp)) 会把平移也乘上缩放
            // （实测 HelloMil 棋盘格整体偏移 + 钳位到边角）。
            SKMatrix step1 = SKMatrix.Concat(
                SKMatrix.CreateScale(scaleX, scaleY),
                SKMatrix.CreateTranslation(-viewbox.Left, -viewbox.Top));
            return SKMatrix.Concat(
                SKMatrix.CreateTranslation(viewport.Left + ox, viewport.Top + oy), step1);
        }

        private static float AlignOffset(double viewportExtent, double contentExtent, MilAlignmentX align)
            => align switch
            {
                MilAlignmentX.Center => (float)((viewportExtent - contentExtent) / 2),
                MilAlignmentX.Right => (float)(viewportExtent - contentExtent),
                _ => 0f,
            };

        private static float AlignOffset(double viewportExtent, double contentExtent, MilAlignmentY align)
            => align switch
            {
                MilAlignmentY.Center => (float)((viewportExtent - contentExtent) / 2),
                MilAlignmentY.Bottom => (float)(viewportExtent - contentExtent),
                _ => 0f,
            };

        /// <summary>
        /// MilTileMode → Skia 平铺（**逐轴独立**给出，这是修好单轴翻转的关键）。
        ///
        /// 【曾经错在哪】旧实现把 FlipX/FlipY/FlipXY 一律映射成「两轴都 Mirror」。
        ///   Skia 的 Mirror 是**按各自轴的格索引奇偶**独立镜像的，两轴同时开 = WPF 的 FlipXY，
        ///   于是 FlipX / FlipY / FlipXY 三档映射到同一个 shader ⇒ **FlipX 与 FlipY 输出逐字节相同**
        ///   （旧 golden 两张 sha256 都是 0cc3ea27…，这本身就是"锁错了"的铁证）。
        ///
        /// 【为什么"单轴"不需要自绘平铺】前置交接说明写过"要在 mapping 之外自绘平铺"——
        ///   那个前提是错的：Skia 的 x/y 平铺模式是**两个独立参数**，
        ///   (Mirror, Repeat) 就是「x 按奇偶镜像、y 只重复不镜像」= WPF 的 FlipX。
        ///   真机 oracle 的独立模型（tests/parity/brushes/src/Program.cs:508-511）正是
        ///   `flipX = (FlipX|FlipXY) && i%2!=0`、`flipY = (FlipY|FlipXY) && j%2!=0` —— 逐轴独立。
        ///
        /// None=Decal：只画基准格（index (0,0)），其余区域透明 ⇒ 背景透出来。
        ///   真机判别点实测 TileMode=None 四点全 #FF808080（背景色），**不是**"钳位延伸"。
        /// </summary>
        // ==================================================================
        //  【T2b 实测记录 · 取样滤波器 —— 结论：当前仍是 Nearest，未修】
        //  SkiaSharp 版本被 T0 锁在 2.88.9（csproj 注释：4.x 有破坏性 API 变更，禁止升级）。
        //  2.88.9 里 `SKShader.CreateBitmap/CreateImage` **只有 4 个重载、都不带采样参数**
        //  （反射枚举确认，且 `SKFilterMode` / `SKSamplingOptions` 类型根本不存在）⇒
        //  着色器侧无法要求双线性，**默认采样就是 Nearest**。
        //
        //  实测（tests/parity/brushes 批次 1 `core_image_tile_v2tiling`，全 443 个采样点）：
        //    当前 Nearest：吻合 375 / 失配 68（失配点全是"真机是混色、我们是纯色"）
        //    例：(120,20) 真机 #FF926262，2×2 双线性解析预测 #FF926363（Δ=1 LSB 取整），
        //        我们 #FFD02020（= 最近的那个 texel 本身）
        //
        //  唯一可用的旋钮是**使用该 shader 的 paint** 上的 `SKPaint.FilterQuality`
        //  （隔离实验已验证 `Low` 确实产生双线性：硬边 0→255 变成 0,0,40,88,135,183,231,255）。
        //  **但整体打开 `Low` 会回归**（T2b 实测，故未落地）：
        //    · DrawingBrush/VisualBrush 是**矢量**源，真机五档逐点相同；我们对它们也开 Low 后
        //      变成随 TileMode 变（矢量对照用例当场变红）⇒ 必须**只对 ImageBrush** 开。
        //    · `TileMode.None` 真机是"边缘钳位的双线性"，而 Skia 的 Decal 让滤波抽头看到透明，
        //      于是 (17,17) 得 #FFB54040（= 0.666×红 + 0.334×背景 #FF808080）而真机是纯 #FFD02020
        //      ⇒ None 档需要"先按边缘钳位求出基准格、再按基准格裁剪"的**另一套构造**。
        //    · 即便只对 ImageBrush 开 Low，Tile/FlipX/FlipY 仍差 Δ1–2：
        //        真机 #FF9A4635 vs 我们 #FF9B4534（Δ=1,−1）
        //        真机 #FFBE2532 vs 我们 #FFC02531（Δ=2,0）
        //      ⇒ Skia 的 Low 核与 WPF 的位图缩放核**不是逐位相同**（这正是"核是否一致"的答案：
        //        **不一致**，且这不是批次 2 的 BitmapScalingMode 数据能回答的问题，见交付说明）。
        // ==================================================================

        private static (SKShaderTileMode X, SKShaderTileMode Y) ImageTileMode(MilTileMode m) => m switch
        {
            MilTileMode.Tile => (SKShaderTileMode.Repeat, SKShaderTileMode.Repeat),
            MilTileMode.FlipX => (SKShaderTileMode.Mirror, SKShaderTileMode.Repeat),
            MilTileMode.FlipY => (SKShaderTileMode.Repeat, SKShaderTileMode.Mirror),
            MilTileMode.FlipXY => (SKShaderTileMode.Mirror, SKShaderTileMode.Mirror),
            _ => (SKShaderTileMode.Decal, SKShaderTileMode.Decal),
        };

        /// <summary>把矩形按矩阵变换后取轴对齐包围盒（量四个角；仿射下够用）。</summary>
        private static SKRect MapRect(SKMatrix m, SKRect r)
        {
            SKPoint p0 = m.MapPoint(r.Left, r.Top);
            SKPoint p1 = m.MapPoint(r.Right, r.Top);
            SKPoint p2 = m.MapPoint(r.Right, r.Bottom);
            SKPoint p3 = m.MapPoint(r.Left, r.Bottom);
            return new SKRect(
                Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
                Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
                Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
                Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
        }

        private static SKRect ToSkRect(MilRect r) =>
            new SKRect((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);

        /// <summary>
        /// BrushMappingMode → 着色器前置矩阵。Absolute 是恒等；相对模式把 [0,1] 铺到包围盒。
        ///
        /// ⚠ 参数顺序是**唯一正确**的那个，别再"顺手调正"（U1a 真机对照实测过）：
        ///   SKMatrix.Concat(a, b) 的语义是 a∘b = "**b 先作用**，再 a"（SkiaSharp 实测，
        ///   与同文件 TileBrushMapping 的注释一致）。要把 [0,1] 铺到包围盒，必须
        ///   **先缩放、后平移**，即 a=平移、b=缩放。
        ///
        /// 曾写反（a=Scale, b=Translate）的后果：平移被缩放乘进去了——
        /// 包围盒 (8,8,116,240) 会得到 Tx=8*116=928、Ty=8*240=1920，整块渐变被推到
        /// 着色器空间的远处，[0,1] 之外一律钳位成末停靠色。
        /// 真机证据（U1a scene05_radial_gradient，Windows RenderTargetBitmap）：
        ///   反例 35137/65536 = 53.6% 像素差异、6 个探针 4 个失配（整块渲染成 #FF1040A0）；
        ///   修正后 336/65536 = 0.513%、探针 6/6（残差全部落在椭圆边缘，见
        ///   docs/U1a-parity-report.md）。
        /// </summary>
        private static SKMatrix MappingMatrix(MilBrushMappingMode mode, SKRect bounds)
        {
            if (mode != MilBrushMappingMode.RelativeToBoundingBox) return SKMatrix.Identity;
            return SKMatrix.Concat(
                SKMatrix.CreateTranslation(bounds.Left, bounds.Top),
                SKMatrix.CreateScale(bounds.Width, bounds.Height));
        }

        /// <summary>RelativeTransform 先作用，再 Transform（WPF 的合成顺序）。</summary>
        private static SKMatrix BrushMatrix(MilResourceProvider provider, MilBrush brush)
        {
            SKMatrix relative = provider.ResolveTransform(new MilResourceHandle((uint)brush.RelativeTransform));
            SKMatrix transform = provider.ResolveTransform(new MilResourceHandle((uint)brush.Transform));
            return SKMatrix.Concat(relative, transform);
        }

        /// <summary>
        /// 渐变停靠点 → Skia 的 (colors, positions)。
        /// Skia 要求 positions 单调不减，WPF 的 GradientStopCollection 可能乱序，故这里排序。
        /// 画刷不透明度按 WPF 语义乘到每个停靠点的 alpha 上。
        /// </summary>
        private static (SKColor[], float[]) Stops(List<MilGradientStop> stops, float opacity)
        {
            if (stops == null || stops.Count == 0) return (null, null);
            if (stops.Count == 1)
            {
                // 单点渐变在 WPF 里就是纯色；Skia 不接受长度 1 的数组，补一个同色点。
                SKColor only = SkiaColor.WithOpacity(SkiaColor.FromMilColorF(stops[0].Color), opacity);
                return (new[] { only, only }, new[] { 0f, 1f });
            }

            var sorted = new List<MilGradientStop>(stops);
            sorted.Sort((a, b) => a.Position.CompareTo(b.Position));

            var colors = new SKColor[sorted.Count];
            var positions = new float[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                colors[i] = SkiaColor.WithOpacity(SkiaColor.FromMilColorF(sorted[i].Color), opacity);
                positions[i] = (float)sorted[i].Position;
            }

            // 单调化：相等位置会让 Skia 直接返回空着色器，这里推一个极小增量。
            for (int i = 1; i < positions.Length; i++)
            {
                if (positions[i] <= positions[i - 1]) positions[i] = positions[i - 1] + 1e-5f;
                if (positions[i] > 1f) positions[i] = 1f;
            }

            return (colors, positions);
        }

        private static SKShaderTileMode TileMode(MilGradientSpreadMethod m) => m switch
        {
            MilGradientSpreadMethod.Reflect => SKShaderTileMode.Mirror,
            MilGradientSpreadMethod.Repeat => SKShaderTileMode.Repeat,
            _ => SKShaderTileMode.Clamp,
        };
    }
}
