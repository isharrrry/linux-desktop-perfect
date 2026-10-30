// Licensed to the .NET Foundation under one or more agreements.
//
// 渲染层取资源的入口。
//
// 为什么需要这一层抽象：
//   IRenderBackend 的签名里只有 (MilVisual, SKCanvas, RenderContext)，而绘图指令里
//   的画刷/画笔/几何/位图全都是**句柄**。要把句柄变成 SKPaint / SKPath 就得查资源表，
//   但契约层的 RenderContext 是 sealed 且不可扩展，没法从参数里带进来。
//   于是把「资源解析」做成后端**构造期**的依赖注入——这是唯一不破契约的做法。
//
// 变换解析为什么不在契约里：
//   TransformResolver 需要 MilChannel 才能递归解 TransformGroup，而 IMilResourceTable
//   只给 Lookup。接口上分开两个方法，让后端不必知道通道的存在。
//
// 两个扩展点（BitmapResolver / GlyphRunRenderer）默认返回「做不到」：
//   T3 的资源模型里没有位图源（MilCmdBitmapSource 返回 E_NOTIMPL，
//   TYPE_BITMAPSOURCE 只会落到 MilOpaqueResource），字形渲染又归 T6 的 Text/ 组。
//   这里留委托而不是直接写死，是为了让 T5/T6 能在不改动本目录的前提下接进来。

using System;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    /// <summary>渲染层解析 MIL 资源句柄的抽象。</summary>
    internal interface IMilResourceProvider
    {
        /// <summary>句柄 → 资源对象；句柄为空或不存在返回 null。</summary>
        object Lookup(MilResourceHandle handle);

        /// <summary>变换句柄 → SKMatrix；空句柄返回单位矩阵。</summary>
        SKMatrix ResolveTransform(MilResourceHandle handle);
    }

    internal abstract class MilResourceProvider : IMilResourceProvider
    {
        /// <summary>
        /// 位图源扩展点。句柄 → SKBitmap。
        /// 缺省为 null：此时 MilDrawImage / MilDrawImageAnimate 会被记为不支持。
        /// </summary>
        public Func<MilResourceHandle, SKBitmap> BitmapResolver { get; set; }

        /// <summary>
        /// 字形扩展点。返回 true 表示已绘制。
        /// 缺省为 null：此时 MilDrawGlyphRun 会被记为不支持（T6 Text/ 组负责实现）。
        /// </summary>
        public Func<SKCanvas, object, SKPaint, bool> GlyphRunRenderer { get; set; }

        /// <summary>
        /// 效果扩展点。句柄 → SKImageFilter；返回 null 表示"这个句柄不是我认识的效果"，
        /// 后端会继续走内置实现（Blur / DropShadow），都认不出来才退化成无效果。
        /// <para>与 BitmapResolver 同范式：外部组（将来接 ShaderEffect 的人）挂上自己的
        /// 工厂即可，不必改本目录。缺省为 null。</para>
        /// </summary>
        public Func<MilResourceHandle, SKImageFilter> EffectResolver { get; set; }

        /// <summary>
        /// 视觉扩展点：Visual 句柄 → 离屏渲染好的 SKImage（VisualBrush 的 tile 内容）。
        /// 与 BitmapResolver / GlyphRunRenderer 同一范式：缺省 null = 认不出，VisualBrush 记为不支持。
        /// 由渲染后端（SkiaRenderBackend）在构造时挂上——它本来就有 RenderVisualTree。
        /// </summary>
        public Func<MilResourceHandle, SKImage> VisualImageResolver { get; set; }

        public abstract object Lookup(MilResourceHandle handle);

        public abstract SKMatrix ResolveTransform(MilResourceHandle handle);

        public SKBitmap LookupBitmap(MilResourceHandle handle) =>
            handle.IsNull ? null : BitmapResolver?.Invoke(handle);

        /// <summary>尝试绘制字形；没有注册渲染器或句柄为空返回 false。</summary>
        public bool TryRenderGlyphRun(SKCanvas canvas, MilResourceHandle glyphRun, SKPaint foreground)
        {
            if (glyphRun.IsNull || GlyphRunRenderer == null) return false;
            object resource = Lookup(glyphRun);
            return resource != null && GlyphRunRenderer(canvas, resource, foreground);
        }

        /// <summary>
        /// 效果句柄 → SKImageFilter。先问扩展点，再退回内置的 Blur / DropShadow。
        /// 返回 null 表示认不出来，调用方按"无效果"处理。
        /// </summary>
        public SKImageFilter LookupEffect(MilResourceHandle handle)
        {
            if (handle.IsNull) return null;
            return EffectResolver?.Invoke(handle);
        }
    }

    /// <summary>基于 mil-core 的 MilChannel：查资源表 + 复用 TransformResolver。</summary>
    internal sealed class MilChannelResourceProvider : MilResourceProvider
    {
        private readonly MilChannel _channel;

        public MilChannelResourceProvider(MilChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        public MilChannel Channel => _channel;

        public override object Lookup(MilResourceHandle handle) =>
            handle.IsNull ? null : _channel.Resources.Lookup(new DUCE.ResourceHandle(handle.Value));

        public override SKMatrix ResolveTransform(MilResourceHandle handle) =>
            handle.IsNull
                ? SKMatrix.Identity
                : TransformResolver.Resolve(_channel, new DUCE.ResourceHandle(handle.Value));
    }
}
