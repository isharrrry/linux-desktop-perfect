// Licensed to the .NET Foundation under one or more agreements.
//
// 脏区跟踪：用"几何维 / 绘制维"两组签名算出**这一帧到底哪里需要重画**。
//
// ── 为什么需要它（根因）──────────────────────────────────────────────────────
//   桥的呈现路径此前每帧从 root 整树重画、整窗 `canvas.Clear` + 整窗 blit。
//   Skia CPU 光栅下这就是"整窗像素级重绘"；WPF 只要有动画在跑就按 ~20fps 调 Present
//   ⇒ UI 线程被帧渲染占满、消息泵饿死。实测：动图在跑时点击/悬停/布局全部冻结，
//   而单帧整窗渲染在本机要 200–370ms。upstream milcore 按脏矩形画，本桥缺这层 ⇒ 补上。
//
// ── 判定（纯读，无副作用）────────────────────────────────────────────────────
//   每个节点算两份签名：
//   · **几何维** `ownGeom`：Offset / Opacity / 变换矩阵 6 分量 / Clip 4 分量 /
//     AlphaMask / Effect / Viewport3D 句柄 / 子节点数。**用值**（不用资源版本）：
//     同一个变换资源可能被多处共享，值才是"这个视觉摆在哪"的事实。
//   · **绘制维** `ownPaint`：投影时算好的 `SrcPaintStamp`（内容资源 + 内容指令里的
//     画刷/画笔/几何 + 遮罩/效果 的 `MilResource.Version`）。GIF/图片那类更新改的正是
//     这里（`ImageBrush.Source` 换了像素），几何完全没动。
//   再逐层聚合出 `subGeom`（含子树），于是：
//   · `subGeom` 变了（位移/缩放/裁剪/层级）⇒ **整窗**（旧位置残影、新位置被裁，不赌）。
//   · 只有某节点 `ownPaint` 变了且几何没变 ⇒ 脏区并上**该节点自己的内容包围盒**
//     （几何没动 ⇒ 上一帧那块就是这一帧要清的块）。
//
// ── 安全网（宁可整窗，绝不留残影）────────────────────────────────────────────
//   1. 资源写计数前进了、但所有签名都没变 ⇒ 改的是没盖进签名的资源 ⇒ 整窗。
//   2. 变了的节点量不出包围盒（内容是纯路径/字形/3D）⇒ 整窗。
//   3. 脏区面积超过窗口 `MaxDirtyRatio` ⇒ 整窗（partial 的簿记不值得）。
//   4. 节点数超过 `MaxNodes`、出现无句柄节点 ⇒ 整窗。
//
// 本文件只算矩形与哈希，**不做任何绘图、不改任何资源**。

using System;
using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal sealed class DirtyRegionTracker
    {
        private sealed class NodeState
        {
            public long OwnGeom;
            public long OwnPaint;
            public long SubGeom;
            public SKRect Bounds;        // 本节点**自己的内容**在设备空间的块（不含子节点）
            public SKRect SubBounds;     // 本节点 + 子树在设备空间的块
            public long Gen;             // 代际：本帧访问过的节点才更新，其余在帧末剪掉
        }

        // ⚠ 复用同一批 NodeState（代际标记），**不在每帧 new** —— 修前每帧每节点 new 一个，
        //   在 CoverView（9.7 万色、节点极多）页实测把首帧入场分配从 1.08MB 抬到 92.95MB，
        //   直接把该页从 PASS 打成 FAIL（判据 alloc_enter_mb>64）。
        private readonly Dictionary<uint, NodeState> _state = new Dictionary<uint, NodeState>();
        private long _gen;

        /// <summary>签名遍历的节点上限；超过则整窗（防超大树的遍历比整画还贵）。</summary>
        public int MaxNodes = 6000;

        /// <summary>脏区面积超过窗口这个比例就走整窗。</summary>
        public float MaxDirtyRatio = 0.55f;

        /// <summary>换了 surface 尺寸/底色后必须重置（旧状态已无意义）。</summary>
        public void Reset()
        {
            _state.Clear();
            _gen = 0;
        }

        /// <summary>
        /// 计算本帧脏区。
        /// 返回 true  ⇒ `dirty` 有意义（`nothingChanged == true` 时 `dirty` 为空 ⇒ 整帧可跳过）；
        /// 返回 false ⇒ 调用方必须整窗重画（`why` 说明原因，诊断行会带出去）。
        /// </summary>
        public bool TryCompute(MilChannel channel, MilVisual root, MilResourceProvider provider,
                               int width, int height, long writeEpoch, ref long lastEpoch,
                               out SKRectI dirty, out string why, out bool nothingChanged)
        {
            dirty = SKRectI.Empty;
            why = null;
            nothingChanged = false;

            if (root == null || width <= 0 || height <= 0)
            {
                why = "no-root-or-size";
                dirty = new SKRectI(0, 0, Math.Max(width, 0), Math.Max(height, 0));
                return false;
            }

            nothingChanged = writeEpoch == lastEpoch;

            _gen++;
            var st = new WalkState(provider);
            Walk(root, SKMatrix.Identity, ref st);
            lastEpoch = writeEpoch;
            PruneStale();

            if (st.NeedFull)
            {
                why = st.FullWhy ?? "bounds-unavailable";
                dirty = new SKRectI(0, 0, width, height);
                return false;
            }

            if (nothingChanged)
            {
                why = "no-writes";
                return true;            // dirty 为空 ⇒ 整帧跳过
            }

            if (st.Dirty.IsEmpty || !IsFinite(st.Dirty))
            {
                why = "writes-but-no-visual-area";
                dirty = new SKRectI(0, 0, width, height);
                return false;
            }

            var d = new SKRectI(
                (int)MathF.Floor(st.Dirty.Left),
                (int)MathF.Floor(st.Dirty.Top),
                (int)MathF.Ceiling(st.Dirty.Right),
                (int)MathF.Ceiling(st.Dirty.Bottom));
            d = SKRectI.Intersect(d, new SKRectI(0, 0, width, height));
            if (d.Width <= 0 || d.Height <= 0)
            {
                why = "dirty-clipped-empty";
                dirty = new SKRectI(0, 0, width, height);
                return false;
            }

            long area = (long)d.Width * d.Height;
            long total = (long)width * height;
            if (total > 0 && area * 100 >= total * (long)MathF.Round(MaxDirtyRatio * 100f))
            {
                why = $"dirty-too-big({area * 100 / total}%)";
                dirty = new SKRectI(0, 0, width, height);
                return false;
            }

            dirty = d;
            why = $"dirty({d.Left},{d.Top} {d.Width}x{d.Height} = {area * 100 / Math.Max(total, 1)}%)";
            return true;
        }

        // ---------------------------------------------------------------- 遍历

        private sealed class WalkState
        {
            public readonly MilResourceProvider Provider;
            public SKRect Dirty = SKRect.Empty;
            public bool NeedFull;
            public string FullWhy;
            public int Nodes;
            public WalkState(MilResourceProvider provider) => Provider = provider;
        }

        private void Walk(MilVisual v, SKMatrix parentWorld, ref WalkState st)
        {
            if (v == null) return;

            if (++st.Nodes > MaxNodes)
            {
                st.NeedFull = true;
                st.FullWhy ??= $"too-many-visuals(>{MaxNodes})";
                return;
            }

            uint key = v.Handle.Value;
            if (key == 0)
            {
                st.NeedFull = true;
                st.FullWhy ??= "visual-without-handle";
                return;
            }

            if (!_state.TryGetValue(key, out NodeState ns))
            {
                ns = new NodeState();
                _state[key] = ns;
            }
            // prev 的语义 = "上一帧见过这个节点"（Gen 不是本代且非 0）
            bool seenBefore = ns.Gen != 0 && ns.Gen != _gen;
            // 快照上一帧的值（随后会被本帧覆盖）
            long prevOwnPaint = ns.OwnPaint;
            long prevSubGeom = ns.SubGeom;
            SKRect prevBounds = ns.Bounds;

            SKMatrix world = SKMatrix.Concat(
                SKMatrix.Concat(parentWorld, v.Transform),
                SKMatrix.CreateTranslation(v.Offset.X, v.Offset.Y));

            // ---- 几何维（用值）----
            long ownGeom = 1469598103934665603L;
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Offset.X));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Offset.Y));
            Mix(ref ownGeom, BitConverter.DoubleToInt64Bits(v.Opacity));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.ScaleX));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.SkewX));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.SkewY));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.ScaleY));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.TransX));
            Mix(ref ownGeom, BitConverter.SingleToInt32Bits(v.Transform.TransY));
            if (v.Clip is { } clip)
            {
                Mix(ref ownGeom, 1);
                Mix(ref ownGeom, BitConverter.SingleToInt32Bits(clip.Left));
                Mix(ref ownGeom, BitConverter.SingleToInt32Bits(clip.Top));
                Mix(ref ownGeom, BitConverter.SingleToInt32Bits(clip.Right));
                Mix(ref ownGeom, BitConverter.SingleToInt32Bits(clip.Bottom));
            }
            else Mix(ref ownGeom, 0);
            Mix(ref ownGeom, v.AlphaMask.Value);
            Mix(ref ownGeom, v.Effect?.Value ?? 0u);
            Mix(ref ownGeom, v.Viewport3D?.Value ?? 0u);
            Mix(ref ownGeom, v.Children.Count);

            // ---- 绘制维 ----
            long ownPaint = 1469598103934665603L;
            Mix(ref ownPaint, v.SrcPaintStamp);
            Mix(ref ownPaint, v.ContentHandle);
            Mix(ref ownPaint, (long)v.RenderOptions.BitmapScalingMode);
            Mix(ref ownPaint, (long)v.RenderOptions.EdgeMode);

            bool ownPaintChanged = !seenBefore || prevOwnPaint != ownPaint;

            // 只有"绘制维变了"才需要自己的块；且只看**自己**，不受子节点影响。
            SKRect ownBounds = SKRect.Empty;
            if (ownPaintChanged)
            {
                ownBounds = ContentRect(v, world, out bool measurable, out bool hasStrokeOrEffect);
                if (!measurable)
                {
                    st.NeedFull = true;
                    st.FullWhy ??= $"changed-node-without-bounds(handle=0x{key:x})";
                }
                else if (hasStrokeOrEffect)
                {
                    // 描边/阴影/遮罩会**画到矩形外**：只清矩形块会留下旧笔画 ⇒ 保守整窗。
                    st.NeedFull = true;
                    st.FullWhy ??= $"changed-node-with-stroke-or-effect(handle=0x{key:x})";
                }
            }

            // ---- 子节点 ----
            long childGeomAgg = 0;
            SKRect childBounds = SKRect.Empty;
            for (int i = 0; i < v.Children.Count; i++)
            {
                MilVisual child = v.Children[i];
                if (child == null) continue;
                Walk(child, world, ref st);
                if (_state.TryGetValue(child.Handle.Value, out NodeState cs) && cs.Gen == _gen)
                {
                    childGeomAgg = childGeomAgg * 31 + cs.SubGeom;
                    childBounds = Union(childBounds, cs.SubBounds);
                }
            }

            long subGeom = ownGeom * 31 + childGeomAgg;
            bool geomChanged = seenBefore && prevSubGeom != subGeom;

            // 几何变了 ⇒ 整窗（残影/裁切风险不赌）。
            if (geomChanged)
            {
                st.NeedFull = true;
                st.FullWhy ??= $"geometry-changed(handle=0x{key:x})";
            }

            // 只有绘制维变了、几何没变 ⇒ 只脏本节点自己的内容块（并上上一帧那块）。
            if (!st.NeedFull && seenBefore && ownPaintChanged && !geomChanged)
            {
                st.Dirty = Union(st.Dirty, ownBounds);
                if (prevBounds.Width > 0 && prevBounds.Height > 0)
                    st.Dirty = Union(st.Dirty, prevBounds);
            }

            ns.OwnGeom = ownGeom;
            ns.OwnPaint = ownPaint;
            ns.SubGeom = subGeom;
            ns.Bounds = ownBounds;
            ns.SubBounds = Union(ownBounds, childBounds);
            ns.Gen = _gen;
        }

        // ---------------------------------------------------------------- 包围盒

        /// <summary>
        /// 本节点**自己的内容**在设备空间的包围盒（从 RenderData 指令里的 `Rect` 推）。
        /// 无内容的纯容器：可测且为空（面积由子节点贡献）。有内容却一条矩形指令都没有
        /// （纯路径/字形）⇒ `measurable=false`，调用方据此整窗。
        /// </summary>
        private static SKRect ContentRect(MilVisual v, SKMatrix world, out bool measurable, out bool hasStrokeOrEffect)
        {
            measurable = true;
            hasStrokeOrEffect = v.Effect != null || v.AlphaMask.Value != 0;
            if (v.Content == null) return SKRect.Empty;

            IReadOnlyList<MilDrawInstruction> ins = v.Content.Instructions;
            if (ins == null || ins.Count == 0) return SKRect.Empty;

            SKRect acc = SKRect.Empty;
            bool any = false;
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].Pen.Value != 0) hasStrokeOrEffect = true;
                SKRect r = ins[i].Rect;
                if (r.Width > 0 && r.Height > 0)
                {
                    acc = Union(acc, MapRect(r, world));
                    any = true;
                }
            }

            if (!any)
            {
                measurable = false;    // 有内容但量不出块
                return SKRect.Empty;
            }
            return acc;
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>帧末剪掉本帧没访问过的节点（被销毁/移出树）。只在需要时分配临时表。</summary>
        private void PruneStale()
        {
            if (_state.Count == 0) return;
            List<uint> stale = null;
            foreach (KeyValuePair<uint, NodeState> kv in _state)
            {
                if (kv.Value.Gen == _gen) continue;
                (stale ??= new List<uint>(8)).Add(kv.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) _state.Remove(stale[i]);
        }

        private static void Mix(ref long h, long x)
        {
            h ^= x;
            h *= 1099511628211L;
        }

        private static bool IsFinite(SKRect r)
            => !(float.IsNaN(r.Left) || float.IsNaN(r.Top) || float.IsNaN(r.Right) || float.IsNaN(r.Bottom) ||
                 float.IsInfinity(r.Left) || float.IsInfinity(r.Top) || float.IsInfinity(r.Right) || float.IsInfinity(r.Bottom));

        private static SKRect Union(SKRect a, SKRect b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return SKRect.Create(
                MathF.Min(a.Left, b.Left),
                MathF.Min(a.Top, b.Top),
                MathF.Max(a.Right, b.Right) - MathF.Min(a.Left, b.Left),
                MathF.Max(a.Bottom, b.Bottom) - MathF.Min(a.Top, b.Top));
        }

        private static SKRect MapRect(SKRect r, SKMatrix m)
        {
            SKPoint p0 = m.MapPoint(r.Left, r.Top);
            SKPoint p1 = m.MapPoint(r.Right, r.Top);
            SKPoint p2 = m.MapPoint(r.Right, r.Bottom);
            SKPoint p3 = m.MapPoint(r.Left, r.Bottom);
            float l = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
            float t = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
            float rr = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
            float bb = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
            return SKRect.Create(l, t, rr - l, bb - t);
        }
    }
}
