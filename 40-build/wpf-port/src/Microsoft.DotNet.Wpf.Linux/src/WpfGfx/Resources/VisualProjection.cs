// Licensed to the .NET Foundation under one or more agreements.
//
// 句柄图 → 契约 MilVisual（Contracts/Interfaces.cs）的投影。
//
// 为什么要投影：命令层（T2/T3）收到的是**句柄**——Visual 的 Content/Transform/Clip
// 都是 uint 句柄，子节点也是句柄列表；而渲染层（T4）消费的契约 MilVisual 要的是
// 已经解析好的 SKMatrix / SKRect / IMilRenderData / 子节点对象。
// 两边是同一份数据的两种视图，投影在 IMilChannel.Root 上按需生成一次。
//
// 本文件只做「句柄解析 + 矩阵运算」，**不做任何绘图**。

using System.Collections.Generic;
using SkiaSharp;
using WpfGfx.Linux.Commands;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Resources
{
    internal static class VisualProjection
    {
        private static int _projSeen;
        private const int MaxDepth = 64;   // 防御环引用，正常视觉树远不到这个深度

        /// <summary>把句柄指向的 Visual 子图投影成契约 MilVisual；句柄无效返回 null。</summary>
        public static MilVisual Project(MilChannel channel, DUCE.ResourceHandle handle)
        {
            MilVisualNode node = channel.GetVisual(handle);
            return node == null ? null : Project(channel, node, new HashSet<uint>(), 0);
        }

        private static MilVisual Project(MilChannel ch, MilVisualNode node, HashSet<uint> visiting, int depth)
        {
            if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1" && _projSeen < 60)
            {
                _projSeen++;
                object _res = ch.Resources.Lookup(node.Handle);
                { string _line = $"[M3D-PROJ] #{_projSeen} h=0x{node.Handle.Value:x} res={(_res?.GetType().Name ?? "<null>")}";
                  Console.Error.WriteLine(_line);
                  try { System.IO.File.AppendAllText("/tmp/m3d-diag.log", _line + "\n"); } catch { } }
            }

            var result = new MilVisual
            {
                Handle = new MilResourceHandle((uint)node.Handle),
                Offset = new SKPoint((float)node.OffsetX, (float)node.OffsetY),
                Opacity = node.Alpha,
                Transform = TransformResolver.Resolve(ch, node.Transform),
                // ⚠ 加字段**不会**编译报错，漏填只会静默为默认值 —— 所以这里显式复制。
                // 全仓库 `new MilVisual` 的生产构造点**只有这一处**（T2b 已枚举核对）。
                RenderOptions = node.RenderOptions,
                // 同理显式搬：漏填只会静默默认（句柄 0 = 无遮罩），正是"投影时丢字段"那一族
                AlphaMask = new MilResourceHandle((uint)node.AlphaMask),
                // 视觉级效果：同样显式搬，漏填只会静默默认（= 无效果），正是投影丢字段那一族
                Effect = node.Effect.IsNull ? (MilResourceHandle?)null : new MilResourceHandle((uint)node.Effect),
                // 脏区判定用：内容句柄 + 资源版本戳（漏填只会退化成"每帧都脏"，不会画错）
                ContentHandle = node.Content.IsNull ? 0u : (uint)node.Content.Value,
                SrcPaintStamp = VersionStamp(ch, node),
            };

            // 只读留痕：把"这个节点的变换是哪份资源、原始值多少"带出投影（缺省关）。
            // 没有它就无法区分"上游发的 scale=0"与"我们解析出的 0" —— 见 TransformProvenance 文件头。
            TransformProvenance.Record(ch, (uint)node.Handle, node.Transform);

            result.Clip = ResolveClip(ch, node.Clip);
            result.Content = ResolveContent(ch, node.Content);

            // 3D 视口（L1）：本节点既是 2D 容器视觉，又是 3D 场景根 ⇒ 渲染层据此调 3D 光栅器。
            if (ch.Resources.Lookup(node.Handle) is MilViewport3DVisualResource)
            {
                result.Viewport3D = new MilResourceHandle((uint)node.Handle);
                if (Environment.GetEnvironmentVariable("WPFGFX_M3D_DIAG") == "1")
                    Console.Error.WriteLine($"[M3D-DIAG] projected viewport3d handle=0x{node.Handle.Value:x}");
            }

            if (depth >= MaxDepth) return result;

            uint key = (uint)node.Handle;
            if (!visiting.Add(key)) return result;      // 环：到此为止
            foreach (DUCE.ResourceHandle child in node.Children)
            {
                MilVisualNode childNode = ch.GetVisual(child);
                if (childNode == null) continue;        // 悬空句柄：跳过而不是崩
                result.Children.Add(Project(ch, childNode, visiting, depth + 1));
            }
            visiting.Remove(key);

            return result;
        }

        /// <summary>Clip 句柄 → SKRect。目前只认 RectangleGeometry；其余（Path/Group）返回 null。</summary>
        private static SKRect? ResolveClip(MilChannel ch, DUCE.ResourceHandle clip)
        {
            if (clip.IsNull) return null;
            if (ch.Resources.Lookup(clip) is MilRectangleGeometry r)
            {
                return new SKRect((float)r.Rect.X, (float)r.Rect.Y,
                                  (float)r.Rect.Right, (float)r.Rect.Bottom);
            }
            return null;
        }

        /// <summary>把本节点直接引用的几份资源的版本揉成一个戳（脏区判定用）。</summary>
        private static long VersionStamp(MilChannel ch, MilVisualNode node)
        {
            static long V(MilChannel c, DUCE.ResourceHandle h)
                => h.IsNull ? 0L : (c.Resources.Lookup(h)?.Version ?? 0);

            // ⚠ 只盖"绘制"这一维：几何（offset/transform/clip/alpha 的**值**）由脏区跟踪器
            //   直接比投影结果里的字段，比资源版本更准（同一个 transform 资源可能被多视觉共享）。
            long s = V(ch, node.Content);
            s = s * 31 + V(ch, node.AlphaMask);
            s = s * 31 + V(ch, node.Effect);

            // ★ 内容**内部**引用的资源也要进戳：GIF/图片那类更新改的是"RenderData 里那把画刷"
            //   （例如 `ImageBrush.Source`），视觉节点与 RenderData 资源本身都可能不变
            //   ⇒ 只看节点/内容句柄会**漏判**（实测：每帧都落到 `writes-but-no-visual-area` 的整窗回落）。
            //   代价：每个有内容的视觉每帧遍历一遍指令 + 几次字典查找（相对整窗光栅可忽略）。
            if (!node.Content.IsNull && ch.Resources.Lookup(node.Content) is MilRenderDataResource rd)
            {
                IMilRenderData content = rd.RenderData ??= RenderDataDecoder.Decode(rd.Data);
                IReadOnlyList<MilDrawInstruction> ins = content?.Instructions;
                if (ins != null)
                {
                    for (int i = 0; i < ins.Count; i++)
                    {
                        MilDrawInstruction d = ins[i];
                        s = s * 31 + V(ch, new DUCE.ResourceHandle(d.Brush.Value));
                        s = s * 31 + V(ch, new DUCE.ResourceHandle(d.Pen.Value));
                        s = s * 31 + V(ch, new DUCE.ResourceHandle(d.Geometry.Value));
                    }
                }
            }
            return s;
        }

        private static IMilRenderData ResolveContent(MilChannel ch, DUCE.ResourceHandle content)
        {
            if (content.IsNull) return null;
            if (ch.Resources.Lookup(content) is not MilRenderDataResource rd) return null;

            // 命令流里可能只改了 Data 而没重新解码（例如直接写资源对象），这里兜底。
            return rd.RenderData ??= RenderDataDecoder.Decode(rd.Data);
        }
    }

    /// <summary>变换资源句柄 → SKMatrix。动画句柄与未知类型按单位矩阵处理。</summary>
    internal static class TransformResolver
    {
        public static SKMatrix Resolve(MilChannel ch, DUCE.ResourceHandle handle)
        {
            return Resolve(ch, handle, 0);
        }

        private static SKMatrix Resolve(MilChannel ch, DUCE.ResourceHandle handle, int depth)
        {
            if (handle.IsNull || depth > 16) return SKMatrix.Identity;

            MilResource r = ch.Resources.Lookup(handle);
            switch (r)
            {
                case MilTranslateTransform t:
                    return Translation((float)t.X, (float)t.Y);

                case MilScaleTransform s:
                    return AroundCenter(
                        new SKMatrix { ScaleX = (float)s.ScaleX, ScaleY = (float)s.ScaleY, Persp2 = 1 },
                        (float)s.CenterX, (float)s.CenterY);

                case MilRotateTransform ro:
                    return AroundCenter(Rotation((float)ro.Angle),
                                        (float)ro.CenterX, (float)ro.CenterY);

                case MilSkewTransform k:
                    return AroundCenter(Skew((float)k.AngleX, (float)k.AngleY),
                                        (float)k.CenterX, (float)k.CenterY);

                case MilMatrixTransform m:
                    return FromMil(m.Matrix);

                case MilTransformGroup g:
                {
                    // ⚠ 顺序陷阱（债务 #12，2026-09-10 由 U1a 真机对照的源码侧证据发现并修）：
                    //   SKMatrix 是**列向量**约定（p' = M·p，平移在最后一列），因此
                    //   「先作用 A 再作用 B」= B·A；而本类的 Mul(a,b) = a·b = **b 先作用**。
                    //   上游 TransformGroup.Value（TransformGroup.cs:31）是
                    //   `transform = c0; for i=1..n: transform *= c_i` → 即 **c0 先作用在几何上**。
                    //   故累积必须写成 Mul(child, acc)（新子放右侧＝后作用）；
                    //   写成 Mul(acc, child) 会把子变换**整体反序**（AroundCenter 那支不受影响，故此前一直未被发现）。
                    SKMatrix acc = SKMatrix.Identity;
                    foreach (DUCE.ResourceHandle child in g.Children)
                        acc = Mul(Resolve(ch, child, depth + 1), acc);
                    return acc;
                }

                default:
                    return SKMatrix.Identity;
            }
        }

        /// <summary>
        /// MilMatrix3x2D → SKMatrix。
        ///
        /// 字段映射（上游 PresentationCore/System/Windows/Media/Composition.cs
        /// TransformToMilMatrix3x2D / MilMatrix3x2DToMatrix 为证，是直接对应、无交换）：
        ///   S_11 = WPF M11, S_12 = WPF M12, S_21 = WPF M21, S_22 = WPF M22
        ///
        /// WPF 语义（行向量 p' = p·M）：
        ///   x' = M11·x + M21·y + DX      y' = M12·x + M22·y + DY
        /// Skia 语义（列向量 p' = M·p）：
        ///   x' = ScaleX·x + SkewX·y + TransX
        ///   y' = SkewY·x  + ScaleY·y + TransY
        ///
        /// 二者对齐后：ScaleX=M11, SkewX=M21, SkewY=M12, ScaleY=M22。
        /// 注意 SkewX 取的是 M21 而不是 M12——M12/M21 在这里是**交叉**映射，不是同名直连。
        /// 对称矩阵（M12 == M21）下两种写法无差别，只有非对称矩阵才能暴露。
        /// </summary>
        public static SKMatrix FromMil(MilMatrix3x2D m) => new SKMatrix
        {
            ScaleX = (float)m.S_11,
            SkewX = (float)m.S_21,
            TransX = (float)m.DX,
            SkewY = (float)m.S_12,
            ScaleY = (float)m.S_22,
            TransY = (float)m.DY,
            Persp0 = 0,
            Persp1 = 0,
            Persp2 = 1,
        };

        public static SKMatrix Translation(float x, float y) => new SKMatrix
        {
            ScaleX = 1, SkewX = 0, TransX = x,
            SkewY = 0, ScaleY = 1, TransY = y,
            Persp0 = 0, Persp1 = 0, Persp2 = 1,
        };

        public static SKMatrix Rotation(float degrees)
        {
            double rad = degrees * System.Math.PI / 180.0;
            float cos = (float)System.Math.Cos(rad);
            float sin = (float)System.Math.Sin(rad);
            return new SKMatrix
            {
                ScaleX = cos, SkewX = -sin, TransX = 0,
                SkewY = sin, ScaleY = cos, TransY = 0,
                Persp0 = 0, Persp1 = 0, Persp2 = 1,
            };
        }

        public static SKMatrix Skew(float angleX, float angleY) => new SKMatrix
        {
            ScaleX = 1,
            SkewX = (float)System.Math.Tan(angleX * System.Math.PI / 180.0),
            TransX = 0,
            SkewY = (float)System.Math.Tan(angleY * System.Math.PI / 180.0),
            ScaleY = 1,
            TransY = 0,
            Persp0 = 0, Persp1 = 0, Persp2 = 1,
        };

        /// <summary>把 inner 变换搬到以 (cx,cy) 为中心：T(c) · inner · T(-c)。</summary>
        public static SKMatrix AroundCenter(SKMatrix inner, float cx, float cy) =>
            Mul(Mul(Translation(cx, cy), inner), Translation(-cx, -cy));

        /// <summary>仿射矩阵复合：结果 = 先 a 后 b（行向量约定 p·a·b）。</summary>
        public static SKMatrix Mul(SKMatrix a, SKMatrix b) => new SKMatrix
        {
            ScaleX = a.ScaleX * b.ScaleX + a.SkewX * b.SkewY,
            SkewX = a.ScaleX * b.SkewX + a.SkewX * b.ScaleY,
            TransX = a.ScaleX * b.TransX + a.SkewX * b.TransY + a.TransX,
            SkewY = a.SkewY * b.ScaleX + a.ScaleY * b.SkewY,
            ScaleY = a.SkewY * b.SkewX + a.ScaleY * b.ScaleY,
            TransY = a.SkewY * b.TransX + a.ScaleY * b.TransY + a.TransY,
            Persp0 = 0,
            Persp1 = 0,
            Persp2 = 1,
        };
    }
}
