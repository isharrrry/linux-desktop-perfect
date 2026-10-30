// Licensed to the .NET Foundation under one or more agreements.
//
// MilVisual — 视觉树节点。这是命令层（T2/T3）交给渲染层（T4）的唯一数据模型。
//
// 字段语义与命令字一一对应：
//   MilCmdVisualSetOffset(0x1b)      -> OffsetX / OffsetY
//   MilCmdVisualSetTransform(0x1c)   -> Transform
//   MilCmdVisualSetEffect(0x1d)      -> Effect
//   MilCmdVisualSetCacheMode(0x1e)   -> CacheMode
//   MilCmdVisualSetClip(0x1f)        -> Clip
//   MilCmdVisualSetAlpha(0x20)       -> Alpha
//   MilCmdVisualSetRenderOptions(0x21)-> RenderOptions
//   MilCmdVisualSetContent(0x22)     -> Content  (RenderData 句柄)
//   MilCmdVisualSetAlphaMask(0x23)   -> AlphaMask
//   MilCmdVisualInsertChildAt(0x26)  -> Children
//   MilCmdVisualSetGuidelineCollection(0x27) -> GuidelinesX / GuidelinesY
//   MilCmdVisualSetScrollableAreaClip(0x28)  -> ScrollableAreaClip / ScrollableAreaClipEnabled

using System.Collections.Generic;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Resources
{
    internal sealed class MilVisualNode
    {
        /// <summary>本 Visual 在所属通道上的资源句柄。</summary>
        public DUCE.ResourceHandle Handle;

        public double OffsetX;
        public double OffsetY;

        /// <summary>变换资源句柄（0 = 无变换）。可用 Channel 解析为 MilTransform。</summary>
        public DUCE.ResourceHandle Transform;

        public DUCE.ResourceHandle Effect;
        public DUCE.ResourceHandle CacheMode;
        public DUCE.ResourceHandle Clip;

        public double Alpha = 1.0;

        public MilRenderOptions RenderOptions = MilRenderOptions.Default;

        /// <summary>RenderData 资源句柄（0 = 无内容）。</summary>
        public DUCE.ResourceHandle Content;

        public DUCE.ResourceHandle AlphaMask;

        /// <summary>子 Visual 句柄，按 Z 序排列（索引 0 最底层）。</summary>
        public readonly List<DUCE.ResourceHandle> Children = new List<DUCE.ResourceHandle>();

        /// <summary>SetGuidelineCollection 的 X/Y 参考线（已排序的 float 值）。</summary>
        public float[] GuidelinesX = System.Array.Empty<float>();
        public float[] GuidelinesY = System.Array.Empty<float>();

        public MilRect ScrollableAreaClip;
        public bool ScrollableAreaClipEnabled;

        public MilVisualNode() { }

        public MilVisualNode(DUCE.ResourceHandle handle) { Handle = handle; }

        public override string ToString() =>
            $"MilVisual({Handle}) offset=({OffsetX},{OffsetY}) alpha={Alpha} children={Children.Count} content={Content}";
    }
}
