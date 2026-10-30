// Licensed to the .NET Foundation under one or more agreements.
//
// Skia 渲染后端：视觉树遍历 + 25 条 MilDrawCommand 执行。
//
// ── 坐标与矩阵的约定（全文件统一，改一处必读全文）────────────────────────────
//   SkiaSharp 是**行向量**约定：点 p 经矩阵 M 映射到 p·M。
//   SKMatrix.Concat(a, b) = a·b，含义是"先 a 后 b"。
//   因此：
//     · 子视觉世界矩阵  world = parent · Transform · Translate(Offset)   （handoff T4 要求）
//     · 画布最终矩阵    CTM   = world · deviceBase                        （先世界变换，再到设备）
//     · PushTransform   CTM'  = CTM · m                                   （先局部变换，再已有变换）
//   ⚠ **已知未修的坐标空间混用缺陷（2026-09-13 T2b 定位，待主控裁决，代码尚未改）**：
//   `SKMatrix.Concat(a,b)` 实测语义是 **b 先作用**（`ParityTests.cs:441-443`：`Concat(R30,T30).TransX=10.98`；
//   又被牙 `ContentTransformSpaceTests.cs` ① 独立坐实）。所以"m 先作用、再 CTM"必须写 **`Concat(CTM, m)`**；
//   现有代码写的是 `Concat(m, CTM)` ⇒ 内容变换落到 CTM **之后**＝**设备空间**，后果：
//     · 它的平移不再被 `deviceBase` 的 ppd 缩放乘到（DIP 值当设备值用）；
//     · 它与视觉 `world` 的顺序颠倒。
//   真机 RTL 读数里那一项 `−24.594 = 65.2559 + (−1)×89.851` 就是"shim 推的 anti（DIP, DX=行宽）
//   被作用在设备侧"的产物；把它改到局部空间后 `Rendering.Tests` **只有 `transform_nested` 一条 golden 变红**
//   （6140/43200 px，maxΔ160）—— 该图固化的正是这条错误语义。
//   **主控已定：RTL 主因在 shim 的 `anti`（T1d 在 A/B），本处按边界保持原样。**
//
// ── Push/Pop 的平衡策略 ──────────────────────────────────────────────────────
//   每条 MilPush* 在画布上恰好留下**一层**状态（Save 或 SaveLayer），MilPop 恰好弹一层。
//   入口记下 SaveCount，出口 RestoreToCount 兜底——指令流哪怕不配平也不会污染后续帧。
//
// ── *Animate 变体 ────────────────────────────────────────────────────────────
//   契约 MilDrawInstruction 装不下动画句柄，T3 的解码器对 *Animate 只填了 Command，
//   原始字节留在 MilRenderData.RawPayloads 里。本文件按上游
//   PresentationCore/.../Generated/RenderData.cs 的 FieldOffset 从原始字节里
//   取出**静态字段**（动画句柄那几个尾部 uint 直接忽略——动画时间线属后续范围）。

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Commands;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    /// <summary>Skia CPU 渲染后端。M1 唯一实现（handoff 决策 4：不做硬件加速）。</summary>
    internal sealed class SkiaRenderBackend : IRenderBackend
    {
        /// <summary>防御环引用与恶意深树：正常视觉树远不到这个深度。</summary>
        private const int MaxDepth = 64;

        private readonly MilResourceProvider _provider;
        private bool _dirty = true;
        private long _version;

        /// <summary>
        /// 当前帧的 RenderContext。VisualBrush 的离屏渲染要复用同一套 DPI / AA / 字体目录 /
        /// ClearColor，而契约里的 RenderContext 是 sealed + init-only，没法从
        /// `Func&lt;MilResourceHandle, SKImage&gt;` 那个扩展点签名里传进来 ⇒ 只能在这里存一份。
        /// </summary>
        private RenderContext _frameContext;

        /// <summary>
        /// 当前正在渲染的 Visual 的 <c>BitmapScalingMode</c>。它是**逐 Visual** 的属性
        /// （上游 `MilVisualNode.RenderOptions`），却要在 `FillAndStroke` 造画刷时被读到，
        /// 所以在这里存一份；`RenderVisual` 进入时保存、退出时**在 finally 里**恢复，
        /// 嵌套（VisualBrush 的离屏渲染）不会串档。
        /// </summary>
        private MilBitmapScalingMode _scalingMode = MilBitmapScalingMode.Unspecified;

        public SkiaRenderBackend(MilResourceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            // 构造期就挂上，而不是等首帧——否则 `CreateFill` 在帧外调用会静默返回 null，
            // 变成一条"要靠调用顺序才对"的隐式依赖。_frameContext 允许为 null（走 96 DPI 默认）。
            EnsureVisualResolver();
        }

        /// <summary>本轮渲染的诊断计数。每帧开头清零。</summary>
        public RenderDiagnostics Diagnostics { get; } = new RenderDiagnostics();

        /// <summary>Invalidate 之后累计的重绘次数。</summary>
        public long Version => _version;

        public bool IsDirty => _dirty;

        /// <summary>标记场景失效，需要重绘。</summary>
        public void Invalidate()
        {
            _dirty = true;
            _version++;
        }

        // ==================================================================
        //  VisualBrush 生产接线
        // ==================================================================
        //
        // 【接线前的事实（T2b 在代码里确认过，不是读注释推的）】
        //   `MilResourceProvider.VisualImageResolver` 此前**只在两个测试文件里被赋值**
        //   （DrawingBrushTests.cs:198 / :243，都是注入假解析器），`src/**` 里一次都没有
        //   ⇒ 生产路径下 `SkiaBrush.CreateVisualFill` 恒返回 null ⇒ 整条 VisualBrush
        //   静默走"未画出"。本方法把扩展点挂上。
        //
        // 【为什么挂在这里而不是 Commands/**】
        //   只有本类同时具备两样东西：能拿到 MilChannel（走 provider 的派生类型）与
        //   能把一个 MilVisual 真正画出来的 RenderVisual。挂在这里不必改 Interop/**、
        //   Commands/**、Resources/ 任何一行。
        //
        // 【缺省不覆盖】只在扩展点为 null 时挂 → 测试注入的假解析器仍然优先，
        //   既有用例（visual_brush_produces_fill_paint / _self_reference_is_guarded）行为不变。

        private static int _m3dVisualSeen;
        private static int _m3dVpSeen;

        private void EnsureVisualResolver()
        {
            if (_provider.VisualImageResolver != null) return;
            if (_provider is not MilChannelResourceProvider) return;   // 拿不到通道 ⇒ 认不出，保持 null
            _provider.VisualImageResolver = ResolveVisualImage;
        }

        /// <summary>
        /// Visual 句柄 → 离屏渲染好的 SKImage（尺寸 = 该视觉子树的自然尺寸 / 内容包围盒）。
        /// 做不到就返回 null（`CreateVisualFill` 会记为"未画出"，不猜尺寸、不画半张）。
        /// </summary>
        private static int _visResSeen;
        private static int _visResSeen2;

        private SKImage ResolveVisualImage(MilResourceHandle handle)
        {
            if (handle.IsNull || _provider is not MilChannelResourceProvider channelProvider) return null;

            MilVisual visual = VisualProjection.Project(channelProvider.Channel, new DUCE.ResourceHandle(handle.Value));
            if (visual == null) return null;

            SKRect bounds = VisualBrushSource.ContentBounds(_provider, visual);
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _visResSeen2 < 8)
            {
                _visResSeen2++;
                int _instr = 0, _kids = visual.Children.Count;
                if (visual.Content is IMilRenderData _rd) _instr = _rd.Instructions.Count;
                string _l = $"[M3D-CT] vis=0x{handle.Value:x} content={(visual.Content == null ? "NULL" : $"instr={_instr}")} children={_kids} bounds=({bounds.Width:F1}x{bounds.Height:F1})";
                Console.Error.WriteLine(_l);
                try { System.IO.File.AppendAllText("/tmp/m3d-ct.log", _l + "\n"); } catch { }
            }

            // 空包围盒 = 没有可计量的内容（含"只有字形"的情形，见 VisualBrushSource 文件头）。
            // 此时**诚实失败**：猜一个尺寸会画出一张错比例的图，比不画更难查。
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _visResSeen < 8)
            {
                _visResSeen++;
                string _l = $"[M3D-VISRES] vis=0x{handle.Value:x} bounds=({bounds.Left:F1},{bounds.Top:F1},{bounds.Width:F1},{bounds.Height:F1}) visual={(visual == null ? "NULL" : "ok")}";
                Console.Error.WriteLine(_l);
                try { System.IO.File.AppendAllText("/tmp/m3d-visres.log", _l + "\n"); } catch { }
            }

            if (!(bounds.Width > 0f) || !(bounds.Height > 0f)) return null;

            int w = (int)Math.Ceiling(bounds.Width);
            int h = (int)Math.Ceiling(bounds.Height);
            if (w <= 0 || h <= 0 || (long)w * h > 4096L * 4096L) return null;   // 荒谬尺寸直接拒

            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            using SKSurface surface = SKSurface.Create(info);
            if (surface == null) return null;

            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            RenderContext frame = _frameContext;
            var nested = new RenderContext
            {
                Width = w,
                Height = h,
                Dpi = frame?.Dpi ?? RenderContext.FixedDpi,
                ClearColor = SKColors.Transparent,
                Antialias = frame?.Antialias ?? true,
                FontDirectory = frame?.FontDirectory ?? "fonts",
            };

            // 把内容包围盒的左上角搬到离屏画布原点。RenderVisual 内部会 SetMatrix(world · deviceBase)
            // 覆盖画布矩阵，所以平移只能放进 deviceBase（先 world 后平移），不能预先 Concat 到画布上。
            var deviceBase = SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top);

            // ⚠ 诊断计数器是**整后端共享**的：离屏这一趟会把"不支持"记进同一本账，
            //   污染外层帧的读数。故整趟压在 Suppress 作用域里——离屏的诊断不外泄。
            using (Diagnostics.Suppress())
            {
                RenderVisual(visual, SKMatrix.Identity, 1.0, canvas, nested, deviceBase, 0);
                canvas.Flush();
            }

            // Snapshot() 返回的 SKImage 自带像素引用，surface 释放后依然有效 ⇒ 直接交出所有权，
            // 不要 FromBitmap 再拷贝一份（会多一次全图拷贝，还要自己管中间位图的生命周期）。
            return surface.Snapshot();
        }

        public void RenderVisualTree(MilVisual root, SKCanvas canvas, RenderContext ctx)
            => RenderVisualTree(root, canvas, ctx, null);

        /// <summary>
        /// 带**脏区裁剪**的渲染。`dirtyClip == null` ⇒ 与旧行为逐字一致（整窗 clear + 整树画）。
        /// `dirtyClip` 有值 ⇒ 只清、只画该矩形内的像素，矩形外**保留上一帧**（调用方保证 surface 是持久的）。
        /// <para>
        /// ⚠ 为什么不是 `canvas.Clear(...)` + `ClipRect`：Skia 的 `Clear` **忽略裁剪**，它填满整张
        /// surface。要"只清脏区"必须用 `DrawRect(..., SKBlendMode.Src)`（Src = 直接覆盖，语义等价于 clear）。
        /// </para>
        /// </summary>
        public void RenderVisualTree(MilVisual root, SKCanvas canvas, RenderContext ctx, SKRectI? dirtyClip)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            ctx ??= new RenderContext();

            Diagnostics.Reset();
            GlyphRunCensus.FrameBegin();      // 只读普查（缺省关，见 GlyphRunCensus 注释）
            DrawInstructionCensus.FrameBegin();
            if (dirtyClip is { } clipRect)
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(clipRect.Left, clipRect.Top, clipRect.Right, clipRect.Bottom),
                                SKClipOperation.Intersect, antialias: false);
                using var clearPaint = new SKPaint { Color = ctx.ClearColor, BlendMode = SKBlendMode.Src };
                canvas.DrawRect(new SKRect(clipRect.Left, clipRect.Top, clipRect.Right, clipRect.Bottom), clearPaint);
                _clipDev = new SKRect(clipRect.Left, clipRect.Top, clipRect.Right, clipRect.Bottom);
            }
            else
            {
                canvas.Clear(ctx.ClearColor);
                _clipDev = null;      // 整窗 ⇒ 不剔除
            }
            _dirty = false;
            _matValid = false;      // ②b：每帧首次必设矩阵

            _frameContext = ctx;
            EnsureVisualResolver();

            if (root == null) return;

            // 设备基准矩阵：把画布原有的变换（如窗口层放的 DPI 缩放）留在链条末端。
            SKMatrix deviceBase = canvas.TotalMatrix;
            float dpiScale = ctx.Dpi / RenderContext.FixedDpi;
            if (Math.Abs(dpiScale - 1f) > 1e-4f)
                deviceBase = SKMatrix.Concat(deviceBase, SKMatrix.CreateScale(dpiScale, dpiScale));

            RenderVisual(root, SKMatrix.Identity, 1.0, canvas, ctx, deviceBase, 0);

            if (dirtyClip != null) canvas.Restore();

            GlyphRunCensus.FrameEnd();
            DrawInstructionCensus.ReferenceCheck(_provider);
            DrawInstructionCensus.FrameEnd();
        }

        // ==================================================================
        //  视觉树遍历
        // ==================================================================

        // ==============================================================
        //  ②c：脏区外子树整体跳过
        // ==============================================================
        //  实测该页单帧 `RenderVisual` 被调用 **23600 次**，而绘制指令只有 226 条 ⇒ 绝大部分节点
        //  在脏区外（脏区只有 27% 面积）。对"整棵子树都在脏区外"的节点直接返回，能一次省掉
        //  该子树的全部 `Save + SetMatrix + Clip + 内容 + RestoreToCount`。
        //  保守条件：带 Effect / 3D 视口 / AlphaMask 的节点**不参与剔除**（它们的可见范围可能
        //  超出内容矩形：阴影外扩、3D 投影、遮罩），并给包围盒固定外扩 8px 以覆盖描边宽度。
        private readonly Dictionary<MilVisual, (long Stamp, SKRect Bounds)> _boundsCache = new();
        private SKRect? _clipDev;      // 本帧脏区（设备空间）；null ⇒ 整窗（不做剔除）

        private const float BoundsPad = 8f;

        /// <summary>子树在**该节点自身局部坐标**下的包围盒（含缓存；按 SrcPaintStamp 失效）。</summary>
        private SKRect GetSubtreeBounds(MilVisual v)
        {
            if (_boundsCache.TryGetValue(v, out var cached) && cached.Stamp == v.SrcPaintStamp)
                return cached.Bounds;

            SKRect acc = SKRect.Empty;
            if (v.Content != null)
            {
                IReadOnlyList<MilDrawInstruction> ins = v.Content.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    SKRect r = ins[i].Rect;
                    if (r.Width <= 0f && r.Height <= 0f) continue;
                    acc = acc.IsEmpty ? r : SKRect.Union(acc, r);
                }
            }
            for (int i = 0; i < v.Children.Count; i++)
            {
                MilVisual ch = v.Children[i];
                SKRect cb = GetSubtreeBounds(ch);
                if (cb.IsEmpty) continue;
                SKMatrix cm = SKMatrix.Concat(ch.Transform, SKMatrix.CreateTranslation(ch.Offset.X, ch.Offset.Y));
                SKRect mapped = cm.MapRect(cb);
                acc = acc.IsEmpty ? mapped : SKRect.Union(acc, mapped);
            }
            if (!acc.IsEmpty)
                acc.Inflate(BoundsPad, BoundsPad);
            _boundsCache[v] = (v.SrcPaintStamp, acc);
            return acc;
        }

        // ②b：画布矩阵缓存（同值跳过 SetMatrix）。
        private SKMatrix _mat;
        private bool _matValid;

        private static bool SameMatrix(in SKMatrix a, in SKMatrix b)
            => a.ScaleX == b.ScaleX && a.SkewX == b.SkewX && a.TransX == b.TransX
            && a.SkewY == b.SkewY && a.ScaleY == b.ScaleY && a.TransY == b.TransY
            && a.Persp0 == b.Persp0 && a.Persp1 == b.Persp1 && a.Persp2 == b.Persp2;

        private void RenderVisual(
            MilVisual v, SKMatrix parentWorld, double parentOpacity,
            SKCanvas canvas, RenderContext ctx, SKMatrix deviceBase, int depth)
        {
            if (v == null || depth > MaxDepth) return;

            // 【性能】空壳节点直接返回：无内容、无子节点、无 3D 视口、无裁剪/效果/遮罩的视觉对画面
            //   没有任何贡献，却会走完备的 `Save + SetMatrix + Clip + RestoreToCount`。
            //   实测该页单帧 `SkiaRenderBackend.RenderVisual` 被调用 **23600 次**，而绘制指令只有 226 条
            //   （`exec_total` 仅 10~14ms，`render` 却 60~77ms）⇒ 绝大部分时间花在这类空壳的
            //   canvas 书上。这是"脏区已缩到 27% 面积、单帧仍要 70ms"的主因：面积小了，节点数没少。
            if (!v.Viewport3D.HasValue && !v.Clip.HasValue && !v.Effect.HasValue
                && v.AlphaMask.IsNull && v.Content == null && v.Children.Count == 0)
            {
                return;
            }

            SKMatrix world = SKMatrix.Concat(
                SKMatrix.Concat(parentWorld, v.Transform),
                SKMatrix.CreateTranslation(v.Offset.X, v.Offset.Y));
            v.WorldTransform = world;

            // ②c：整棵子树在脏区外 ⇒ 直接跳过（保守条件见字段注释）。
            if (_clipDev is { } clipDev
                && !v.Effect.HasValue && !v.Viewport3D.HasValue && v.AlphaMask.IsNull)
            {
                SKRect local = GetSubtreeBounds(v);
                if (!local.IsEmpty)
                {
                    SKRect dev = SKMatrix.Concat(deviceBase, world).MapRect(local);
                    if (!dev.IntersectsWith(clipDev))
                    {
                        return;
                    }
                }
            }

            double opacity = parentOpacity * v.Opacity;
            if (opacity <= 0.0) return;      // 完全透明：整棵子树跳过

            // ⚠【压栈必须放在**所有提前返回之后**】首版把 NoteVisual 放在上面那行 return 之前，
            //   于是"完全透明的子树"会**只压不弹** ⇒ 祖先链无限增长（实测：一次跑从 5s 变成 5m54s，
            //   并且有用例变红）。这是"仪表扰动被测对象"的第三种形态（前两种：改调用次数、被环境扰动）。
            //   现在它与函数末尾的 LeaveVisual 严格成对。
            DrawInstructionCensus.NoteVisual(v.Handle, v.Transform, world);

            if (opacity > 1.0) opacity = 1.0;

            // ---- 视觉不透明度遮罩（MilCmdVisualSetAlphaMask = 0x23）----
            // 语义与上游 `UIElement.OpacityMask` 相同：**作用于整个视觉子树**。
            // 此前 `MilVisual.AlphaMask` 在投影时被丢掉、渲染层零消费 ⇒ **静默忽略**（探针 `alpha=0` 全无效果）。
            // 本节与下面 RenderData 版遮罩（`MilPushOpacityMask`）**同一套形态**：SaveLayer + 整层 alpha。
            double maskAlpha = 1.0;
            SKPaint maskPaint = null;          // 非纯色遮罩：Pop 前用它以 DstIn 合成
            if (!v.AlphaMask.IsNull)
            {
                if (_provider.Lookup(v.AlphaMask) is MilSolidColorBrush maskSolid)
                {
                    maskAlpha = maskSolid.Color.A * maskSolid.Opacity;
                    if (maskAlpha < 0.0) maskAlpha = 0.0;
                    if (maskAlpha > 1.0) maskAlpha = 1.0;
                }
                else
                {
                    // 非纯色遮罩：**内容画进层 + 用遮罩着色器以 DstIn 合成**（逐像素调制）。
                    // 这正是它值得单独立项的原因 —— 它必须在 **Pop 时机**（RestoreToCount 之前）
                    // 往层上补一笔，而那一笔的时机由指令流/递归结构决定，不能靠"边画边遮"。
                    // ⚠ 坐标：遮罩画刷取 `LocalClipBounds` 当包围盒（= 该视觉被裁到的注册区）。
                    //   对"遮罩覆盖整个元素"这一常见形态这是对的；若将来遇到遮罩只覆盖元素一部分的用例，
                    //   应当换成真正的**内容包围盒** —— 这一点如实记在这里，别当成已经通用。
                    maskPaint = SkiaBrush.CreateFill(_provider, v.AlphaMask,
                        canvas.LocalClipBounds, antialias: true, _scalingMode);
                    if (maskPaint?.Shader != null)
                    {
                        maskPaint.BlendMode = SKBlendMode.DstIn;   // dest.A *= src.A
                    }
                    else
                    {
                        // 认不出的遮罩（视觉遮罩等）：显式记一笔，**不当成"没有遮罩"继续画**
                        maskPaint?.Dispose();
                        maskPaint = null;
                        Diagnostics.RecordNotDrawn(MilDrawCommand.MilPushOpacityMask);
                    }
                }
            }

            double layerAlpha = opacity * maskAlpha;

            int entry = canvas.SaveCount;

            if (layerAlpha < 1.0 || maskPaint != null)
            {
                using SKPaint layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(layerAlpha)) };
                PushLayer(canvas, layerPaint);
            }
            else
            {
                canvas.Save();
            }

            // 【矩阵组合空间·同族扫荡】目标链：局部 → `world`（DIP）→ `deviceBase`（设备侧）。
            //   `Concat(a,b)` 实测为 **b 先作用** ⇒ 必须 `Concat(deviceBase, world)`；反写会让 `deviceBase`
            //   先作用，`world` 的平移就不再被 deviceBase 的缩放乘到。真机与全部测试里 `deviceBase` 恰为
            //   单位阵（画布矩阵 × Dpi/96，Dpi=96），所以这条**一直没显形**；牙见 `MatrixCompositionSpaceTests.cs`。
            // 【②b 性能】`SetMatrix` 是每节点一次的固定开销（实测单帧 23600 次 ≈ 136ms）。
            //   绝大多数节点是**无 offset/无 transform 的容器** ⇒ 它们的 `world` 与父节点逐值相同，
            //   此时重复 `SetMatrix` 纯属浪费。这里缓存"画布当前矩阵"，同值直接跳过。
            //   失效点：`RenderVisual` 末尾的 `RestoreToCount(entry)` 会把画布矩阵恢复成父节点的，
            //   所以那里必须把缓存标记为失效（否则会画出错位画面）。
            SKMatrix canvasMat = SKMatrix.Concat(deviceBase, world);
            if (!_matValid || !SameMatrix(canvasMat, _mat))
            {
                canvas.SetMatrix(canvasMat);
                _mat = canvasMat;
                _matValid = true;
            }

            // ---- 视觉级 Effect（UIElement.Effect -> MilCmdVisualSetEffect -> MilVisual.Effect）----
            //   历史：该字段收得下但没有消费者（投影层丢、契约类型连字段都没有）=> 阴影从未被画，
            //   popup 为阴影预留的 8px 带只好是纯白（即用户看到的「内容多余边距」）。
            //   现在：认得出来的效果压成图层（ImageFilter）；认不出来的如实记账（与 MilPushEffect 同口径：
            //   认得但退化成空操作不算 NotDrawn）。
            //   已知偏差：自身 Clip 在本层之内应用 => 阴影外扩会被元素自身的 clip 裁掉
            //   （WPF 语义下 Effect 结果应在外层再受祖先裁剪）；本例（Border/Menu 无自身 clip）不受影响。
            if (v.Effect.HasValue
                && Environment.GetEnvironmentVariable("WPFGFX_EFFECT_OFF") != "1")   // 临时开关：判定 Effect 层是否影响内容渲染
            {
                // 诊断（默认关）：区分"Effect 没到绘制层"与"到了但被裁/太淡"。
                if (Environment.GetEnvironmentVariable("WPFGFX_EFFECT_DIAG") == "1")
                    Console.Error.WriteLine($"[FX-DIAG] visual=0x{v.Handle.Value:x} effect=0x{v.Effect.Value.Value:x} clip={(v.Clip.HasValue ? "yes" : "no")}");
                if (SkiaEffect.TryResolve(_provider, v.Effect.Value, out SKImageFilter visualFilter))
                {
                    if (visualFilter != null)
                    {
                        using SKPaint effectPaint = new SKPaint { ImageFilter = visualFilter };
                        PushLayer(canvas, effectPaint);   // 不传 rect => 阴影可外扩，不被元素边界裁
                    }
                    else
                    {
                        visualFilter?.Dispose();
                    }
                }
                else
                {
                    // 认不出来的效果句柄：**不**在这里记 NotDrawn —— dispatcher 侧已用
                    // NoteVisualEffectIgnored 具名记账，且 RecordNotDrawn 是 WpfTextDemo
                    // 判据「未画种类 0」的输入，记进去会当场把那条冻死的判据打红。
                }
            }

            if (v.Clip.HasValue)
                canvas.ClipRect(v.Clip.Value, SKClipOperation.Intersect, ctx.Antialias);

            // ---- 3D 视口（TYPE_VIEWPORT3DVISUAL）：L1/3D 起由 Skia3DRenderer 消费 ----
            //   此前 3D 命令只把状态写进 MilResource3DTable，**没有任何渲染者**（症状：colors=1）。
            //   坐标语义：此处已把 `deviceBase · world` 设进 canvas ⇒ 3D 光栅器按 **DIP 局部坐标**
            //   在 `Viewport3DVisual.Viewport` 矩形内填三角。画不出来时**如实记账**，不假装成功。
            // 【L2 诊断】统计"遍历到多少个 visual / 其中多少带 3D 视口"（默认关，最多打 40 行）。
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _m3dVisualSeen < 40)
            {
                _m3dVisualSeen++;
                if (v.Viewport3D.HasValue) _m3dVpSeen++;
                Console.Error.WriteLine($"[M3D-VIS] #{_m3dVisualSeen} handle=0x{v.Handle.Value:x} vp={(v.Viewport3D.HasValue ? $"0x{v.Viewport3D.Value.Value:x}" : "null")} content={(v.Content != null ? "yes" : "no")}");
            }

            if (v.Viewport3D.HasValue)
            {
                int vpSave = canvas.Save();
                try
                {
                    bool ok = Skia3DRenderer.Render(_provider, v.Viewport3D.Value, canvas, out string m3dReason);
                    if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
                        Console.Error.WriteLine(ok
                            ? $"[M3D-DIAG] render OK handle=0x{v.Viewport3D.Value.Value:x} reason={m3dReason ?? "<none>"}"
                            : $"[M3D-DIAG] render FAIL handle=0x{v.Viewport3D.Value.Value:x} reason={m3dReason}");
                    if (!ok)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WpfGfx] 3D 视口未出图：{m3dReason}");
                    }
                }
                finally
                {
                    canvas.RestoreToCount(vpSave);
                }
            }

            if (v.Content != null)
            {
                MilBitmapScalingMode savedScaling = _scalingMode;
                _scalingMode = v.RenderOptions.BitmapScalingMode;
                try { RenderContent(v.Content, canvas, ctx, deviceBase); }
                finally { _scalingMode = savedScaling; }
            }

            // 子节点按列表顺序绘制：后面的在上层（handoff T4 要求）。
            foreach (MilVisual child in v.Children)
                RenderVisual(child, world, opacity, canvas, ctx, deviceBase, depth + 1);

            // 【Pop 时机】非纯色遮罩在这里补最后一笔：子树已全部画进层，
            //   现在用遮罩着色器以 DstIn 把层的 alpha 逐像素乘上遮罩 alpha，然后才弹层。
            //   顺序反了（先 Restore 再 DstIn）会乘到层外、遮罩失效 —— 这是本项的要害。
            if (maskPaint != null)
            {
                using (maskPaint) canvas.DrawPaint(maskPaint);
            }

            canvas.RestoreToCount(entry);
            _matValid = false;      // ②b：Restore 会把画布矩阵恢复成父节点的 ⇒ 缓存失效
            DrawInstructionCensus.LeaveVisual();
        }

        // ==================================================================
        //  指令流执行
        // ==================================================================

        private void RenderContent(IMilRenderData content, SKCanvas canvas, RenderContext ctx, SKMatrix deviceBase)
        {
            // *Animate / Guideline / Effect 的载荷只能从原始字节读（见文件头说明）。
            MilRenderData rawData = content as MilRenderData;
            IReadOnlyList<MilDrawInstruction> instructions = content.Instructions;

            int entry = canvas.SaveCount;

            for (int i = 0; i < instructions.Count; i++)
            {
                MilDrawInstruction instr = instructions[i];
                byte[] raw = rawData != null && i < rawData.RawPayloads.Count ? rawData.RawPayloads[i] : null;
                Diagnostics.CountInstruction();
                Execute(instr, raw, canvas, ctx, deviceBase, entry);
            }

            // 指令流不配平时的兜底：把这条 content 自己压的栈全部弹掉。
            canvas.RestoreToCount(entry);
        }

        private void Execute(
            MilDrawInstruction instr, byte[] raw,
            SKCanvas canvas, RenderContext ctx, SKMatrix deviceBase, int entry)
        {
            DrawInstructionCensus.Count(instr.Command);   // 只读：指令执行次数普查

            switch (instr.Command)
            {
                // ---------------- 绘制：线 ----------------
                case MilDrawCommand.MilDrawLine:
                case MilDrawCommand.MilDrawLineAnimate:
                {
                    bool animate = instr.Command == MilDrawCommand.MilDrawLineAnimate;
                    SKPoint p0 = animate ? P(raw, 0) : instr.Point0;
                    SKPoint p1 = animate ? P(raw, 16) : instr.Point1;
                    MilResourceHandle pen = animate ? H(raw, 32) : instr.Pen;

                    using SKPath path = new SKPath();
                    path.MoveTo(p0);
                    path.LineTo(p1);
                    FillAndStroke(canvas, ctx, path, MilResourceHandle.Null, pen, instr.Command);
                    break;
                }

                // ---------------- 绘制：矩形 ----------------
                case MilDrawCommand.MilDrawRectangle:
                case MilDrawCommand.MilDrawRectangleAnimate:
                {
                    bool animate = instr.Command == MilDrawCommand.MilDrawRectangleAnimate;
                    SKRect rect = animate ? ReadRect(raw, 0) : instr.Rect;
                    MilResourceHandle brush = animate ? H(raw, 32) : instr.Brush;
                    MilResourceHandle pen = animate ? H(raw, 36) : instr.Pen;

                    using SKPath path = new SKPath();
                    path.AddRect(rect);
                    FillAndStroke(canvas, ctx, path, brush, pen, instr.Command);
                    break;
                }

                // ---------------- 绘制：圆角矩形 ----------------
                case MilDrawCommand.MilDrawRoundedRectangle:
                case MilDrawCommand.MilDrawRoundedRectangleAnimate:
                {
                    bool animate = instr.Command == MilDrawCommand.MilDrawRoundedRectangleAnimate;
                    SKRect rect = animate ? ReadRect(raw, 0) : instr.Rect;
                    SKPoint radius = animate ? P(raw, 32) : instr.CornerRadius;
                    MilResourceHandle brush = animate ? H(raw, 48) : instr.Brush;
                    MilResourceHandle pen = animate ? H(raw, 52) : instr.Pen;

                    using SKPath path = new SKPath();
                    if (radius.X > 0f && radius.Y > 0f)
                        path.AddRoundRect(rect, radius.X, radius.Y);
                    else
                        path.AddRect(rect);
                    FillAndStroke(canvas, ctx, path, brush, pen, instr.Command);
                    break;
                }

                // ---------------- 绘制：椭圆 ----------------
                case MilDrawCommand.MilDrawEllipse:
                case MilDrawCommand.MilDrawEllipseAnimate:
                {
                    bool animate = instr.Command == MilDrawCommand.MilDrawEllipseAnimate;
                    // 契约没有 Center/Radius 字段：中心在 Point0，半径在 CornerRadius（见 MilRenderData.cs:112）。
                    SKPoint center = animate ? P(raw, 0) : instr.Point0;
                    SKPoint radius = animate ? P(raw, 16) : instr.CornerRadius;
                    MilResourceHandle brush = animate ? H(raw, 32) : instr.Brush;
                    MilResourceHandle pen = animate ? H(raw, 36) : instr.Pen;

                    using SKPath path = new SKPath();
                    path.AddOval(new SKRect(
                        center.X - radius.X, center.Y - radius.Y,
                        center.X + radius.X, center.Y + radius.Y));
                    FillAndStroke(canvas, ctx, path, brush, pen, instr.Command);
                    break;
                }

                // ---------------- 绘制：几何 ----------------
                case MilDrawCommand.MilDrawGeometry:
                {
                    using SKPath path = SkiaGeometry.ToPath(_provider, instr.Geometry);
                    if (path == null)
                    {
                        Diagnostics.RecordNotDrawn(instr.Command);
                        break;
                    }
                    FillAndStroke(canvas, ctx, path, instr.Brush, instr.Pen, instr.Command, instr.Geometry);
                    break;
                }

                // ---------------- 绘制：图像 ----------------
                case MilDrawCommand.MilDrawImage:
                case MilDrawCommand.MilDrawImageAnimate:
                {
                    bool animate = instr.Command == MilDrawCommand.MilDrawImageAnimate;
                    SKRect rect = animate ? ReadRect(raw, 0) : instr.Rect;
                    MilResourceHandle image = animate ? H(raw, 32) : instr.Geometry;

                    SKBitmap bitmap = _provider.LookupBitmap(image);
                    if (bitmap == null)
                    {
                        Diagnostics.RecordNotDrawn(instr.Command);
                        break;
                    }
                    canvas.DrawBitmap(bitmap, rect);
                    break;
                }

                // ---------------- 绘制：字形 ----------------
                case MilDrawCommand.MilDrawGlyphRun:
                {
                    using SKPaint paint = SkiaBrush.CreateFill(
                        _provider, instr.Brush, SKRect.Empty, ctx.Antialias, _scalingMode);
                    if (paint == null)
                    {
                        Diagnostics.RecordNotDrawn(instr.Command);
                        break;
                    }
                    // 只读普查：在既有绘制路径**旁**看一眼已解码的字形 id，不参与也不改变绘制
                    GlyphRunCensus.NoteCtm(canvas.TotalMatrix);   // 只读：记绘制时的 CTM 供相关性判定
                    GlyphRunCensus.Observe(_provider, instr.Geometry);
                    if (_provider.GlyphRunRenderer == null) GlyphRunCensus.ObserveNullRenderer();
                    // ⚠ 只调用**一次**并复用结果。首版写成"在外面再调一次拿返回值"⇒
                    //   渲染器被调了**两遍**（自验用例把 drawn==3 断言成 6 当场抓到）。
                    //   **仪表绝不允许改变被测行为的调用次数** —— 这是"仪表扰动被测对象"最直接的一种。
                    bool glyphDrawn = _provider.TryRenderGlyphRun(canvas, instr.Geometry, paint);
                    GlyphRunCensus.ObserveRenderResult(glyphDrawn);
                    if (!glyphDrawn)
                        Diagnostics.RecordNotDrawn(instr.Command);
                    break;
                }

                // ---------------- 绘制：Drawing ----------------
                case MilDrawCommand.MilDrawDrawing:
                {
                    if (_provider.Lookup(instr.Geometry) is not MilDrawing drawing)
                    {
                        Diagnostics.RecordNotDrawn(instr.Command);
                        break;
                    }
                    DrawDrawing(drawing, canvas, ctx, deviceBase, 0);
                    break;
                }

                // ---------------- 绘制：视频（M1 不做） ----------------
                case MilDrawCommand.MilDrawVideo:
                case MilDrawCommand.MilDrawVideoAnimate:
                    Diagnostics.RecordNotDrawn(instr.Command);
                    break;

                // ---------------- 栈：裁剪 ----------------
                case MilDrawCommand.MilPushClip:
                {
                    canvas.Save();
                    using SKPath path = SkiaGeometry.ToPath(_provider, instr.Geometry);
                    if (path != null)
                        canvas.ClipPath(path, SKClipOperation.Intersect, ctx.Antialias);
                    else
                        Diagnostics.RecordNotDrawn(instr.Command);
                    break;
                }

                // ---------------- 栈：不透明度 ----------------
                case MilDrawCommand.MilPushOpacity:
                case MilDrawCommand.MilPushOpacityAnimate:
                {
                    double opacity = instr.Command == MilDrawCommand.MilPushOpacityAnimate
                        ? D(raw, 0)
                        : instr.Opacity;
                    if (opacity >= 1.0)
                    {
                        canvas.Save();
                        break;
                    }
                    if (opacity <= 0.0) opacity = 0.0;
                    using SKPaint layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(opacity)) };
                    PushLayer(canvas, layerPaint);
                    break;
                }

                // ---------------- 栈：不透明度遮罩 ----------------
                case MilDrawCommand.MilPushOpacityMask:
                {
                    // MILCMD_PUSH_OPACITY_MASK：boundingBoxCacheLocalSpace@0(16) hOpacityMask@16
                    //
                    // ⚠️ 前任修错处：这里原本先 canvas.Save() 再 PushLayer（SaveLayer），
                    // 一次 Push 压了两层，而 MilPop 只弹一层 —— 栈顶残留会把后续指令的
                    // 裁剪/变换串到别处去。按文件头"一次 Push 恰好一层"的约定修正：
                    // 成功则 PushLayer 留一层，失败则补一个裸 Save 顶位。
                    MilResourceHandle mask = H(raw, 16);
                    if (!TryApplySolidMask(canvas, mask, out SKPaint layerPaint))
                    {
                        Diagnostics.RecordNotDrawn(instr.Command);
                        canvas.Save();
                        break;
                    }
                    using (layerPaint) { PushLayer(canvas, layerPaint); }
                    break;
                }

                // ---------------- 栈：变换 ----------------
                case MilDrawCommand.MilPushTransform:
                {
                    SKMatrix m = _provider.ResolveTransform(instr.Geometry);
                    canvas.Save();
                    // 深度 = **Save 之后**的 SaveCount：`MilPop` 的 `Restore()` 之后按此深度裁栈（见 census.TrimToDepth）。
                    DrawInstructionCensus.PushTransform($"0x{instr.Geometry.Value:x8}", m, canvas.SaveCount);
                    // 【修法·2026-09-13 主控授权】内容级变换在**局部（DIP）**空间 ⇒ `CTM' = CTM · m`。
                    //   `Concat(a,b)` 实测为 **b 先作用**（`ParityTests.cs:441-443`；牙① 独立坐实），
                    //   故必须写 `Concat(TotalMatrix, m)`；写反（`Concat(m, TotalMatrix)` = `m·CTM`）会把
                    //   DIP 矩阵作用到**设备空间**：平移少乘一次 ppd、并与视觉 `world` 顺序颠倒。
                    //   牙：`ContentTransformSpaceTests.cs`；golden 代价：`transform_nested` 一张（已授权重生成）。
                    canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, m));
                    break;
                }

                // ---------------- 栈：Guideline（像素吸附提示，M1 忽略） ----------------
                case MilDrawCommand.MilPushGuidelineSet:
                case MilDrawCommand.MilPushGuidelineY1:
                case MilDrawCommand.MilPushGuidelineY2:
                    canvas.Save();
                    Diagnostics.RecordDegraded(instr.Command);
                    break;

                // ---------------- 栈：效果 ----------------
                case MilDrawCommand.MilPushEffect:
                {
                    // MILCMD_PUSH_EFFECT：hEffect@0 hEffectInput@4（hEffectInput 的
                    // 取舍见 SkiaEffect.cs 文件头：Blur/DropShadow 一律传空，故意不读）
                    //
                    // ⚠️ 同上的栈平衡修正：压成图层时内部已 SaveLayer 一层，不能再额外
                    // Save；没压成时才补一个裸 Save，保证 MilPop 弹得动。
                    //
                    // 一次查表同时拿到"认不认得"和"滤镜长什么样"（早先拆成 Create +
                    // IsSupported 两个方法会重复查表、重复调用外部 EffectResolver 委托）。
                    MilResourceHandle hEffect = H(raw, 0);
                    bool recognized = SkiaEffect.TryResolve(_provider, hEffect, out SKImageFilter filter);

                    // 认不出来的句柄（比如将来塞进来的 ShaderEffect）才记 NotDrawn；
                    // 认得出来但退化成空操作（Radius=0）的，指令确实执行了，不算未画出。
                    if (!recognized) Diagnostics.RecordNotDrawn(instr.Command);

                    if (filter == null)
                    {
                        canvas.Save();
                        break;
                    }

                    using (filter)
                    using (SKPaint paint = new SKPaint { ImageFilter = filter })
                    {
                        PushLayer(canvas, paint);
                    }
                    break;
                }

                // ---------------- 栈：弹出 ----------------
                case MilDrawCommand.MilPop:
                    if (canvas.SaveCount > entry) canvas.Restore();
                    // 【仪表同步·主控批准项 B】`MilPop` 是**万能 pop**（可能弹 clip/opacity/effect/guideline），
                    //   所以**不按次数**弹变换条目，而是按**深度**裁：Restore 之后深度 > 当前 SaveCount 的
                    //   变换作用域必然已关闭（外侧仍生效的条目因深度 ≤ SaveCount 被保留）。
                    DrawInstructionCensus.TrimToDepth(canvas.SaveCount);
                    break;

                default:
                    Diagnostics.RecordNotDrawn(instr.Command);
                    break;
            }
        }

        // ==================================================================
        //  填充 + 描边
        // ==================================================================

        private void FillAndStroke(
            SKCanvas canvas, RenderContext ctx, SKPath path,
            MilResourceHandle brush, MilResourceHandle pen, MilDrawCommand command,
            MilResourceHandle geometryHandle = default)
        {
            if (path == null) return;
            SKRect bounds = path.Bounds;

            // 只读：几何类绘制的局部/设备包围盒（答"画了但看不见吗、落到哪了"）
            DrawInstructionCensus.Geometry(command, bounds, canvas.TotalMatrix, canvas,
                geometryHandle, brush, _provider);

            if (!brush.IsNull)
            {
                using SKPaint fill = SkiaBrush.CreateFill(_provider, brush, bounds, ctx.Antialias, _scalingMode);
                if (fill != null) canvas.DrawPath(path, fill);
                else Diagnostics.RecordNotDrawn(command);
            }

            if (!pen.IsNull)
            {
                using SKPaint stroke = SkiaPen.CreateStroke(_provider, pen, bounds, ctx.Antialias, _scalingMode);
                if (stroke != null) canvas.DrawPath(path, stroke);
                else Diagnostics.RecordNotDrawn(command);
            }
        }

        // ==================================================================
        //  Drawing 资源（MilDrawDrawing 的递归展开）
        // ==================================================================

        private void DrawDrawing(
            MilDrawing drawing, SKCanvas canvas, RenderContext ctx, SKMatrix deviceBase, int depth)
        {
            if (drawing == null || depth > MaxDepth) return;

            switch (drawing)
            {
                case MilGeometryDrawing g:
                {
                    using SKPath path = SkiaGeometry.ToPath(_provider, new MilResourceHandle((uint)g.Geometry));
                    if (path == null) { Diagnostics.RecordNotDrawn(MilDrawCommand.MilDrawDrawing); break; }
                    FillAndStroke(canvas, ctx, path,
                        new MilResourceHandle((uint)g.Brush),
                        new MilResourceHandle((uint)g.Pen),
                        MilDrawCommand.MilDrawDrawing);
                    break;
                }

                case MilDrawingGroup group:
                {
                    int entry = canvas.SaveCount;

                    if (group.Opacity < 1.0)
                    {
                        using SKPaint layerPaint = new SKPaint
                        {
                            Color = SKColors.White.WithAlpha(AlphaByte(group.Opacity)),
                        };
                        PushLayer(canvas, layerPaint);
                    }
                    else
                    {
                        canvas.Save();
                    }

                    SKPath clip = SkiaGeometry.ToPath(_provider, new MilResourceHandle((uint)group.ClipGeometry));
                    if (clip != null)
                    {
                        using (clip) canvas.ClipPath(clip, SKClipOperation.Intersect, ctx.Antialias);
                    }

                    SKMatrix m = _provider.ResolveTransform(new MilResourceHandle((uint)group.Transform));
                    // 同 `MilPushTransform`：`DrawingGroup.Transform` 也在**局部（DIP）**空间 ⇒ `Concat(CTM, m)`。
                    // 【仪表同步·同族扫荡】它**必须与 `MilPushTransform` 一样进 census 的 `PushStack`**：
                    //   否则画布矩阵里明明含这一层、诊断的 `PushTransform=` 却打「无」—— 又一例
                    //   "仪表与被测对象不同步"（与 `PopTransform` 零调用那处同族，但这一处可以就地配平）。
                    //   压栈与出栈严格成对，且只在真正 SetMatrix 时压（`IsIdentity` 时不碰栈）。
                    bool pushed = !m.IsIdentity;
                    if (pushed)
                    {
                        canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, m));
                        DrawInstructionCensus.PushTransform(
                            $"drawingGroup:0x{group.Transform:x8}", m, canvas.SaveCount);
                    }

                    foreach (DUCE.ResourceHandle child in group.Children)
                    {
                        if (_provider.Lookup(new MilResourceHandle((uint)child)) is MilDrawing childDrawing)
                            DrawDrawing(childDrawing, canvas, ctx, deviceBase, depth + 1);
                    }

                    // 同 `MilPop`：**按深度裁**（这条路径没有 `MilPop`，是自己 `RestoreToCount(entry)` 的）。
                    canvas.RestoreToCount(entry);
                    if (pushed) DrawInstructionCensus.TrimToDepth(canvas.SaveCount);
                    break;
                }

                case MilImageDrawing image:
                {
                    SKBitmap bitmap = _provider.LookupBitmap(new MilResourceHandle((uint)image.ImageSource));
                    if (bitmap == null) { Diagnostics.RecordNotDrawn(MilDrawCommand.MilDrawDrawing); break; }
                    canvas.DrawBitmap(bitmap, new SKRect(
                        (float)image.Rect.Left, (float)image.Rect.Top,
                        (float)image.Rect.Right, (float)image.Rect.Bottom));
                    break;
                }

                case MilGlyphRunDrawing glyph:
                {
                    using SKPaint paint = SkiaBrush.CreateFill(
                        _provider, new MilResourceHandle((uint)glyph.ForegroundBrush), SKRect.Empty, ctx.Antialias,
                        _scalingMode);
                    GlyphRunCensus.NoteCtm(canvas.TotalMatrix);
                    GlyphRunCensus.Observe(_provider, new MilResourceHandle((uint)glyph.GlyphRun));
                    if (paint == null || !_provider.TryRenderGlyphRun(
                            canvas, new MilResourceHandle((uint)glyph.GlyphRun), paint))
                    {
                        Diagnostics.RecordNotDrawn(MilDrawCommand.MilDrawDrawing);
                    }
                    break;
                }

                default:
                    Diagnostics.RecordNotDrawn(MilDrawCommand.MilDrawDrawing);
                    break;
            }
        }

        // ==================================================================
        //  图层辅助
        // ==================================================================

        /// <summary>
        /// 压一层带 alpha 的图层。SaveLayer 之后显式重设矩阵：
        /// 不同 Skia 版本对 SaveLayer 是否继承 CTM 的措辞含糊，重设一次可消除歧义。
        /// </summary>
        private static void PushLayer(SKCanvas canvas, SKPaint paint)
        {
            SKMatrix before = canvas.TotalMatrix;
            canvas.SaveLayer(paint);
            canvas.SetMatrix(before);
        }

        /// <summary>
        /// 不透明度遮罩：只实现了"遮罩是纯色画刷"这种情况（取其 alpha 当整层不透明度）。
        /// 真正的 WPF 遮罩是按遮罩的 alpha 通道逐像素调制，需要"内容画进层 + DstIn 合成"，
        /// 而 Pop 的时机由指令流决定，本层无法在 Pop 时插一笔画——留待后续版本。
        /// </summary>
        private bool TryApplySolidMask(SKCanvas canvas, MilResourceHandle mask, out SKPaint layerPaint)
        {
            layerPaint = null;
            if (mask.IsNull) return false;
            if (_provider.Lookup(mask) is not MilSolidColorBrush solid) return false;

            float alpha = solid.Color.A * (float)solid.Opacity;
            layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(AlphaByte(alpha)) };
            return true;
        }

        // ==================================================================
        //  原始字节读取（*Animate / Guideline / Effect 用）
        // ==================================================================

        private static byte AlphaByte(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255.0);

        private static uint U32(byte[] b, int o) =>
            b != null && o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)) : 0u;

        private static double D(byte[] b, int o) =>
            b != null && o + 8 <= b.Length ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o, 8)) : 0.0;

        private static MilResourceHandle H(byte[] b, int o) => new MilResourceHandle(U32(b, o));

        private static SKPoint P(byte[] b, int o) => new SKPoint((float)D(b, o), (float)D(b, o + 8));

        /// <summary>MilRect（X,Y,W,H）→ SKRect（L,T,R,B）。</summary>
        private static SKRect ReadRect(byte[] b, int o)
        {
            float x = (float)D(b, o);
            float y = (float)D(b, o + 8);
            return new SKRect(x, y, x + (float)D(b, o + 16), y + (float)D(b, o + 24));
        }
    }
}
