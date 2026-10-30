// Licensed to the .NET Foundation under one or more agreements.
//
// 变换的**来源留痕**：把"某个视觉节点的变换到底是哪份资源、原始值是多少"从投影里带出来。
//
// 【为什么需要它（零缩放 CTM 归因）】
//   `VisualProjection.cs:38` 原来是 `Transform = TransformResolver.Resolve(ch, node.Transform)` ——
//   **解析完就把句柄丢了**，于是当某个节点的 world scale 变成 0 时，我们只能说
//   "是这一代自己的变换 scale 为 0"，**说不出是哪份资源、也说不出"上游发的 0"还是"我们解析出的 0"**。
//   这与 `PIDWriteFont` 是同一类缺口（**投影时丢字段**），登记为一个类别。
//
// 【为什么用旁表而不是给契约 `MilVisual` 加字段】
//   ① 这是**纯诊断**信息，只有 census 用；给契约加字段会让每个消费者都多背一个字段，
//      还要重跑构造点枚举 / 线格校验（本项目为 `RenderOptions` 走过一遍）。
//   ② 旁表在 `Resources/**`（本车道）内自洽，**不碰 `Contracts/**`、不产生新的分层依赖**。
//   ③ 关掉时**一个字节的额外行为都没有**：`Enabled` 为假 ⇒ `Record` 立即返回、字典始终为空。
//
// 【不扰动的保证】
//   · 缺省关（`WPF_LINUX_DRAW_CENSUS` 未设 ⇒ `Enabled=false`）；
//   · `Record` 只写一张**有上限**的字典，不改变任何调用次数、不改变任何绘制行为；
//   · 只读被解析的资源对象，**不修改**它；
//   · 查不到资源时报 `无信息`，**不报 0**（本项目最富的一族）。

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Resources
{
    /// <summary>视觉节点 → 它的变换资源句柄 + 解析前的原始值（只读诊断留痕）。</summary>
    internal static class TransformProvenance
    {
        private const int MaxEntries = 8192;   // 防御无界增长；超出后新条目不记（宁可少记，不可泄漏）

        private static bool s_enabled =
            Environment.GetEnvironmentVariable("WPF_LINUX_DRAW_CENSUS") == "1";

        private static readonly ConcurrentDictionary<uint, string> s_map =
            new ConcurrentDictionary<uint, string>();

        public static bool Enabled => s_enabled;

        /// <summary>供测试显式开关（生产路径一律由环境变量决定）。</summary>
        internal static void ForceEnabledForTest(bool on) { s_enabled = on; if (!on) Reset(); }

        /// <summary>视觉句柄 → "句柄 + 资源类型 + 原始值" 的可读描述；未记录返回 null。</summary>
        public static string Lookup(uint visualHandle) =>
            s_enabled && s_map.TryGetValue(visualHandle, out string v) ? v : null;

        /// <summary>
        /// 在投影处调用一次：记下这个视觉节点的变换资源句柄与**解析前的原始字段值**。
        /// 关掉时立即返回。
        /// </summary>
        public static void Record(MilChannel ch, uint visualHandle, DUCE.ResourceHandle transformHandle)
        {
            if (!s_enabled) return;
            try
            {
                if (s_map.Count >= MaxEntries && !s_map.ContainsKey(visualHandle)) return;
                s_map[visualHandle] = Describe(ch, transformHandle);
            }
            catch { /* 诊断自身出错不许影响投影 */ }
        }

        /// <summary>
        /// 把变换资源**解析前的原始值**读成一行。查不到就报"无信息"，不报 0。
        /// 这些字段就是 `TransformResolver` 的输入 —— 有了它才能判"上游发的 0"还是"我们解析出的 0"。
        /// </summary>
        public static string Describe(MilChannel ch, DUCE.ResourceHandle handle)
        {
            if (handle.IsNull) return "变换=无(句柄 0 ⇒ 单位阵)";

            object res = ch?.Resources.Lookup(handle);
            switch (res)
            {
                case MilScaleTransform t:
                    return $"变换句柄=0x{handle.Value:x8} 类型=Scale 原始=(ScaleX={F(t.ScaleX)} ScaleY={F(t.ScaleY)} " +
                           $"CenterX={F(t.CenterX)} CenterY={F(t.CenterY)})";
                case MilTranslateTransform t:
                    return $"变换句柄=0x{handle.Value:x8} 类型=Translate 原始=(X={F(t.X)} Y={F(t.Y)})";
                case MilRotateTransform t:
                    return $"变换句柄=0x{handle.Value:x8} 类型=Rotate 原始=(Angle={F(t.Angle)} " +
                           $"CenterX={F(t.CenterX)} CenterY={F(t.CenterY)})";
                case MilSkewTransform t:
                    return $"变换句柄=0x{handle.Value:x8} 类型=Skew 原始=(AngleX={F(t.AngleX)} AngleY={F(t.AngleY)} " +
                           $"CenterX={F(t.CenterX)} CenterY={F(t.CenterY)})";
                case MilMatrixTransform t:
                    return $"变换句柄=0x{handle.Value:x8} 类型=Matrix 原始=(M11={F(t.Matrix.S_11)} M12={F(t.Matrix.S_12)} " +
                           $"M21={F(t.Matrix.S_21)} M22={F(t.Matrix.S_22)} DX={F(t.Matrix.DX)} DY={F(t.Matrix.DY)})";
                case MilTransformGroup g:
                    // ⚠ 组类型的"原始值"只有**子数**，而子数**证不了镜像活下来没有**：
                    //   `TransformResolver.Resolve` 对"查不到的子树 / 空子表 / 超过深度"一律
                    //   **静默返回单位阵**，于是 `子数=1` 照样打得出来、矩阵却已经是单位阵。
                    //   ⇒ 必须把**解析后**的矩阵也打出来；这一行才是"投影丢字段"那一族的判据读数。
                    return $"变换句柄=0x{handle.Value:x8} 类型=TransformGroup 原始=(子数={g.Children.Count}) " +
                           $"解析={DescribeResolved(ch, handle)}";
                case null:
                    return $"变换句柄=0x{handle.Value:x8} **无信息**(资源表里查不到这个句柄 ⇒ 无法判定原始值)";
                default:
                    return $"变换句柄=0x{handle.Value:x8} 类型={res.GetType().Name}(未识别的变换类型)";
            }
        }

        /// <summary>
        /// 组（以及任何句柄）**解析后**的矩阵一行。与"原始值"分开是刻意的：
        /// 原始值答"上游发了什么"，解析值答"我们最终算出什么"——
        /// 0 缩放那次的教训就是这两者必须分开读（见本文件头注）。
        /// </summary>
        private static string DescribeResolved(MilChannel ch, DUCE.ResourceHandle handle)
        {
            try
            {
                SKMatrix m = TransformResolver.Resolve(ch, handle);
                var kinds = new StringBuilder();
                if (ch?.Resources.Lookup(handle) is MilTransformGroup g)
                {
                    foreach (DUCE.ResourceHandle child in g.Children)
                    {
                        object cr = ch.Resources.Lookup(child);
                        if (kinds.Length > 0) kinds.Append(',');
                        kinds.Append(cr == null ? "**无信息**" : cr.GetType().Name.Replace("Mil", ""));
                    }
                }
                return $"[M11={F(m.ScaleX)} M12={F(m.SkewY)} M21={F(m.SkewX)} M22={F(m.ScaleY)} " +
                       $"DX={F(m.TransX)} DY={F(m.TransY)}] 子类型=[{kinds}]" +
                       (m.ScaleX < 0.0 ? " **含镜像**" : " 不含镜像");
            }
            catch (Exception ex) { return $"(解析抛异常：{ex.GetType().Name})"; }
        }

        /// <summary>诊断用清空（测试隔离用；生产只在关掉时本来就不记）。</summary>
        internal static void Reset() => s_map.Clear();

        private static string F(double v) =>
            // 退化的 0 要显眼：非 0 也照原样给，避免"四舍五入把 0.4 显示成 0"这种自己造出来的误读
            v == 0.0 ? "**0**" : v.ToString("G9", CultureInfo.InvariantCulture);
    }
}
