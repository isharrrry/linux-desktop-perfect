// Licensed to the .NET Foundation under one or more agreements.
//
// 绘制指令普查（T2b 诊断仪表 · D-c 归因用）。
//
// 【为什么需要它】D-c 的现象是"⑤ 卡片里的 Rectangle/Ellipse/Path 一个都没画"，而台账已证明
//   **几何资源是在通道里的**（`MilEllipseGeometry×1` + `MilLinearGradientBrush×1` 正好对上那张卡），
//   并且整卡标题正常显示 ⇒ **不是没投影、也不是整卡被剪**。剩下要区分的是：
//     (a) 绘制指令**根本没进 render data**（资源建了但没引用/没提交）；还是
//     (b) 指令画了，但**画到了视口外 / 被 clip 掉 / 尺寸为 0**。
//   这两者的判据完全不同，且 (b) 里还分"视口外"与"被剪"。
//
// 【它量什么】
//   1. 每条 `MilDrawCommand` 的**执行次数**（全 25 条）⇒ 答 (a)：该指令到底出现过没有；
//   2. 每次**几何类**绘制（矩形/圆角矩形/椭圆/几何/图像/Drawing/线）记录
//      **局部包围盒 + 当时的 CTM + 映射后的设备包围盒** ⇒ 答 (b)：
//      设备包围盒若落在窗口外/为空，就是"画了但看不见"，并直接给出落在哪。
//
// 【不扰动的保证（与 GlyphRunCensus 同一族纪律）】
//   1. **只读**：在既有的 `Execute` / `FillAndStroke` 旁读一眼**已经算出来的** path.Bounds 与
//      canvas.TotalMatrix，**不改变**任何绘制行为、不替换任何扩展点；
//   2. **缺省全关**（`WPF_LINUX_DRAW_CENSUS` 未设 ⇒ 调用点只多一次 bool 判断）；
//   3. **绝不写 `RenderDiagnostics`**（runner 判据②是"未画种类 0"，污染它就是把判据做假）；
//   4. 输出有界（每帧一张表 + 每条几何最多 N 行）、只写 stderr、内部 try/catch 兜底；
//   5. 状态 **ThreadStatic**（首版 GlyphRunCensus 用全局静态，在并行测试下三次红一次 —— 这里不再犯）。

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class DrawInstructionCensus
    {
        private const string EnvVar = "WPF_LINUX_DRAW_CENSUS";
        private const int MaxGeomLines = 60;
        private const int MaxFrames = 3;

        private sealed class State
        {
            public bool Enabled;
            public int Frame;
            public readonly Dictionary<MilDrawCommand, int> Counts = new Dictionary<MilDrawCommand, int>();
            public readonly List<string> Geom = new List<string>();
            // 每条记录带**压栈时的 `canvas.SaveCount`（深度）**：`MilPop` 是"万能 pop"（可能弹 clip/opacity/
            // effect/guideline 中的任何一种），所以**不能按"pop 次数"弹**，只能按**深度裁** ——
            // Restore 之后凡 `深度 > 当前 SaveCount` 的条目都已被撤销（见 `TrimToDepth`）。
            public readonly List<(int Depth, string Handle, SKMatrix M)> PushStack =
                new List<(int, string, SKMatrix)>();
            public string VisualDesc = "-";
            public SKMatrix VisualTransform = SKMatrix.Identity;
            public SKMatrix AccumWorld = SKMatrix.Identity;     // 累积 world（祖先链合成）
            public string LastHealthyAncestor = "-";            // 最后一个**非退化**的祖先
            // 祖先链（句柄 + 该节点的累积 world）。用来答"零是从哪一代开始出现的"。
            public readonly List<(string Handle, SKMatrix Own, SKMatrix Accum)> Chain =
                new List<(string, SKMatrix, SKMatrix)>();
        }

        [ThreadStatic] private static State s_state;
        private static State St => s_state ??= new State { Enabled = ReadBool(EnvVar) };

        public static bool Enabled => St.Enabled;

        /// <summary>
        /// **当前正在渲染的视觉**的来源读数（句柄 + 本节点 Transform + 累积 world），
        /// 供别处的仪表（`GlyphRunCensus`）拼进自己的行里。**只读，不改变任何状态**；
        /// 缺省关时返回**空串**（拼上去等于没拼），所以关掉即逐字回到原行为。
        ///
        /// 【为什么需要这一格】
        ///   `Geometry()` 打的 `来源=` 行**只有几何命令**才有；字形绘制走的是
        ///   `GlyphRunCensus` 那条路，于是日志里"这些字形画在哪个视觉下、那一代的
        ///   world 有没有镜像"是**空白**。RTL 镜像丢失要定位的恰恰是这两代之间：
        ///   若同一行里 `累积world` 含镜像而 `CTM` 不含 ⇒ 丢失发生在**字形绘制路径**
        ///   （有人重设了画布矩阵 / 画到了另一个画布）；若 `累积world` 也不含 ⇒
        ///   丢失发生在**投影或更上游**。没有这一格，两种形态在日志里长得一模一样。
        /// </summary>
        public static string CurrentVisualProvenance
        {
            get
            {
                State st = St;
                if (!st.Enabled) return string.Empty;
                try
                {
                    var sb = new System.Text.StringBuilder(" 来源=");
                    sb.Append(st.VisualDesc)
                      .Append(" 本节点Transform=[").Append(st.VisualTransform.ScaleX.ToString("F2")).Append(',')
                      .Append(st.VisualTransform.SkewY.ToString("F2")).Append(',')
                      .Append(st.VisualTransform.SkewX.ToString("F2")).Append(',')
                      .Append(st.VisualTransform.ScaleY.ToString("F2")).Append(',')
                      .Append(st.VisualTransform.TransX.ToString("F1")).Append(',')
                      .Append(st.VisualTransform.TransY.ToString("F1")).Append(']')
                      .Append(" 累积world=[").Append(st.AccumWorld.ScaleX.ToString("F2")).Append(',')
                      .Append(st.AccumWorld.SkewY.ToString("F2")).Append(',')
                      .Append(st.AccumWorld.SkewX.ToString("F2")).Append(',')
                      .Append(st.AccumWorld.ScaleY.ToString("F2")).Append(',')
                      .Append(st.AccumWorld.TransX.ToString("F1")).Append(',')
                      .Append(st.AccumWorld.TransY.ToString("F1")).Append(']');
                    // `PushTransform=` **无条件**打出：栈为空时打 `无`。
                    // 【为什么必须无条件】T3 报"本轮全日志 0 行 `PushTransform=`" —— 那不是仪表消失，而是
                    //   **条件式打印把"没有 push"这件事一并藏掉了**：字段只在 `PushStack.Count > 0` 时出现，
                    //   于是"这个位置根本没有 MilPushTransform"与"仪表没接上"在日志里**长得一模一样**。
                    //   这与本项目"查不到就报无信息、不报 0"是同一条纪律。
                    //   栈每帧由 `FrameBegin → Reset()` 清空，空栈是**正常且有意义**的读数
                    //   （T1d 修掉 shim 的 anti 之后，RTL 行上就该是 `无`）。
                    sb.Append(" PushTransform=")
                      .Append(st.PushStack.Count > 0 ? st.PushStack[st.PushStack.Count - 1].Handle : "无");
                    return sb.ToString();
                }
                catch { return string.Empty; }
            }
        }

        internal static void ForceEnabled(bool on) { St.Enabled = on; Reset(); }

        /// <summary>上一帧各指令的执行次数（测试用，不依赖 stderr）。</summary>
        internal static IReadOnlyDictionary<MilDrawCommand, int> LastCounts => St.Counts;

        /// <summary>上一帧几何类绘制的描述行（测试用）。</summary>
        internal static IReadOnlyList<string> LastGeometryLines => St.Geom;

        private static void Reset()
        {
            State st = St;
            st.Counts.Clear();
            st.Geom.Clear();
            // 防御性：帧首清空辅助栈。**正常路径下它本就该是空的**（NoteVisual/LeaveVisual 成对）；
            // 清它只是保证"某一帧万一失衡"不会污染下一帧。注意：清了以后 LeaveVisual 可能多弹，
            // 所以 LeaveVisual 里有 Count>0 保护。
            st.Chain.Clear();
            st.PushStack.Clear();
        }

        public static void FrameBegin()
        {
            if (!St.Enabled) return;
            Reset();
        }

        /// <summary>当前正在渲染的视觉（用于把"变换从哪来"归到具体节点）。</summary>
        public static void NoteVisual(MilResourceHandle visual, SKMatrix transform, SKMatrix accumulatedWorld)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                st.VisualDesc = $"visual=0x{visual.Value:x8}";
                st.VisualTransform = transform;
                st.AccumWorld = accumulatedWorld;
                if (Math.Abs(accumulatedWorld.ScaleX) > 1e-6 && Math.Abs(accumulatedWorld.ScaleY) > 1e-6)
                    st.LastHealthyAncestor = $"0x{visual.Value:x8}";
                // 祖先链：每进一个视觉就压一条（句柄 + 该节点累积 world 的 scale）
                st.Chain.Add(($"0x{visual.Value:x8}", transform, accumulatedWorld));
                if (st.Chain.Count > 64) st.Chain.RemoveAt(0);
            }
            catch { }
        }

        /// <summary>退出一个视觉：弹掉祖先链末条（与 NoteVisual 成对）。</summary>
        public static void LeaveVisual()
        {
            State st = St;
            if (!st.Enabled) return;
            try { if (st.Chain.Count > 0) st.Chain.RemoveAt(st.Chain.Count - 1); } catch { }
        }

        /// <summary>
        /// 设备包围盒为 0 的归因。
        /// 首版只要“设备为 0”就打“零缩放 CTM”，但设备为 0 有两个不同的因：
        /// (1) 局部本来就是 0（与变换无关）；(2) 局部非 0、CTM 退化把它塌成 0。
        /// 把两者说成同一件事就是混因；现在只有 (2) 才叫零缩放 CTM，也只有 (2) 才打祖先链。
        /// </summary>
        private static string ZeroVerdict(SKRect local, SKRect dev, SKMatrix ctm, out bool ctmDegenerate)
        {
            ctmDegenerate = false;
            if (dev.Width > 0 && dev.Height > 0) return "";

            bool localZero = local.Width <= 0 || local.Height <= 0;
            bool scaleZero = Math.Abs(ctm.ScaleX) < 1e-6 || Math.Abs(ctm.ScaleY) < 1e-6;

            if (localZero && !scaleZero)
                return "  <= 设备为 0，但**因是局部尺寸为 0**，CTM 非退化 ⇒ **不是零缩放 CTM**";
            if (!localZero && scaleZero)
            {
                ctmDegenerate = true;
                return "  <= 局部非 0 而设备为 0 ⇒ **零缩放 CTM**（CTM 的 ScaleX 或 ScaleY 为 0）";
            }
            if (localZero && scaleZero)
            {
                ctmDegenerate = true;
                return "  <= 局部与 CTM **两者都**为 0（归因不可分）";
            }
            return "  <= 设备为 0，但局部与 CTM 都不为 0 ⇒ **无信息**（未预期的形态）";
        }

        /// <summary>把祖先链压成一行，并**标出零是从哪一代开始的**（这才是有用的那一位）。</summary>
        private static string ChainText(State st)
        {
            if (st.Chain.Count == 0) return "祖先链=(空)";
            var sb = new StringBuilder("祖先链=");
            int zeroAt = -1;
            for (int i = 0; i < st.Chain.Count; i++)
            {
                (string h, SKMatrix own, SKMatrix acc) = st.Chain[i];
                bool degenerate = Math.Abs(acc.ScaleX) < 1e-6 || Math.Abs(acc.ScaleY) < 1e-6;
                if (degenerate && zeroAt < 0) zeroAt = i;
                if (i > 0) sb.Append(" → ");
                if (i < 10 || i >= st.Chain.Count - 2 || i == zeroAt)
                    sb.Append(h)
                      .Append(" 本节点=[").Append(own.ScaleX.ToString("F2")).Append(',')
                      .Append(own.ScaleY.ToString("F2")).Append(',')
                      .Append(own.TransX.ToString("F0")).Append(',')
                      .Append(own.TransY.ToString("F0")).Append("] 累积=[")
                      .Append(acc.ScaleX.ToString("F2")).Append(',')
                      .Append(acc.ScaleY.ToString("F2")).Append(',')
                      .Append(acc.TransX.ToString("F0")).Append(',')
                      .Append(acc.TransY.ToString("F0")).Append(']');
                else if (i == 10) sb.Append('…');

                // 变换来源留痕（缺省关时 Lookup 恒返回 null ⇒ 这一行只是多一次 null 判断）：
                // 打出"这个节点的变换是哪份资源、解析前的原始值是多少"，用来判"上游发的 0"还是"我们解析出的 0"。
                string prov = WpfGfx.Linux.Resources.TransformProvenance.Lookup(
                    uint.Parse(h.AsSpan(2), System.Globalization.NumberStyles.HexNumber));
                sb.Append("  ").Append(prov ?? "变换=**无信息**(该节点未留痕)");
            }

            if (zeroAt < 0)
                return sb.Append("  **累积链上无退化** ⇒ 零来自本节点或 PushTransform").ToString();

            // 决定性的一位：零引入的那一代，**它自己的 Transform** 是不是退化的？
            (string zh, SKMatrix zown, SKMatrix zacc) = st.Chain[zeroAt];
            bool ownDegenerate = Math.Abs(zown.ScaleX) < 1e-6 || Math.Abs(zown.ScaleY) < 1e-6;
            sb.Append("  **零引入点=").Append(zh).Append("** 该代本节点Transform scale=(")
              .Append(zown.ScaleX.ToString("F2")).Append(',').Append(zown.ScaleY.ToString("F2")).Append(") ⇒ ");
            sb.Append(ownDegenerate
                ? "**零来自这一代自己的变换**（不是我们的累积逻辑；是上游/布局给的值还是解析算的，需该变换资源的原始值 —— 句柄在投影时被丢，已登记）"
                : "**这一代自己的变换是健康的** ⇒ 零不是它自己的变换 ⇒ **嫌疑转向我们的累积逻辑或某个上游输入**");
            return sb.ToString();
        }

        public static void PushTransform(string handle, SKMatrix m, int saveCount)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                st.PushStack.Add((saveCount, handle, m));
                // 有界：仪表自身的栈不许无限增长（正常嵌套远不到 64 层）。
                if (st.PushStack.Count > 64) st.PushStack.RemoveAt(0);
            }
            catch { }
        }

        /// <summary>
        /// **按深度裁栈**（`MilPop`/`RestoreToCount` 之后调用，参数传**还原后**的 `canvas.SaveCount`）。
        ///
        /// 【为什么不按调用次数弹】`MilPop` 是**万能 pop**：它弹的画布状态可能是 clip / opacity / effect /
        /// guideline 中的任何一种。若"见一次 pop 就弹一条变换"，那么"推了非变换、弹一次"会把**仍然生效**的
        /// 变换条目误弹掉 ⇒ 制造**另一种**"仪表与被测对象不同步"。
        /// 【为什么按深度裁是对的】条目记录了压栈时的 SaveCount；还原到 `saveCount` 之后，凡
        /// `记录深度 > saveCount` 的作用域都已关闭 ⇒ 该条目**必然**不再生效，而外层的（深度 ≤ saveCount）保留。
        /// 这同时覆盖了嵌套变换（内层先被裁掉）与"非变换 pop"（裁不到仍生效的变换）。
        /// </summary>
        public static void TrimToDepth(int saveCount)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                for (int i = st.PushStack.Count - 1; i >= 0; i--)
                    if (st.PushStack[i].Depth > saveCount)
                        st.PushStack.RemoveAt(i);
            }
            catch { }
        }

        /// <summary>兼容入口：显式弹一条（仅在"确定弹的就是变换"时用；否则请用 <see cref="TrimToDepth"/>）。</summary>
        public static void PopTransform()
        {
            State st = St;
            if (!st.Enabled) return;
            try { if (st.PushStack.Count > 0) st.PushStack.RemoveAt(st.PushStack.Count - 1); } catch { }
        }

        public static void Count(MilDrawCommand command)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                st.Counts.TryGetValue(command, out int n);
                st.Counts[command] = n + 1;
            }
            catch { }
        }

        public static void Geometry(
            MilDrawCommand command, SKRect localBounds, SKMatrix ctm, SKCanvas canvas,
            MilResourceHandle geometryHandle, MilResourceHandle brushHandle, MilResourceProvider provider)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                if (st.Geom.Count >= MaxGeomLines) return;
                SKRect dev = MapRect(ctm, localBounds);

                string clipText;
                try
                {
                    SKRectI cb = canvas.DeviceClipBounds;
                    clipText = cb.Width <= 0 || cb.Height <= 0
                        ? $"clip=空({cb.Left},{cb.Top},{cb.Width}x{cb.Height})"
                        : $"clip=({cb.Left},{cb.Top},{cb.Right},{cb.Bottom})";
                }
                catch { clipText = "clip=**无信息**（读 DeviceClipBounds 抛异常）"; }

                string geomText = geometryHandle.IsNull ? "几何=内联矩形" : $"几何=0x{geometryHandle.Value:x8}";
                string brushText = "画刷=无";
                if (!brushHandle.IsNull)
                {
                    object res = provider?.Lookup(brushHandle);
                    brushText = res switch
                    {
                        MilSolidColorBrush sc => $"画刷=0x{brushHandle.Value:x8}:纯色 R={sc.Color.R:F3} G={sc.Color.G:F3} B={sc.Color.B:F3} A={sc.Color.A:F3}",
                        null => $"画刷=0x{brushHandle.Value:x8}:**无信息**(查不到资源)",
                        _ => $"画刷=0x{brushHandle.Value:x8}:{res.GetType().Name}",
                    };
                }

                string from;
                if (st.PushStack.Count > 0)
                {
                    (int d, string h, SKMatrix m) = st.PushStack[st.PushStack.Count - 1];
                    from = $"来源=PushTransform({h}) [{m.ScaleX:F2},{m.SkewY:F2},{m.SkewX:F2},{m.ScaleY:F2},{m.TransX:F1},{m.TransY:F1}]";
                }
                else
                {
                    from = $"来源={st.VisualDesc} 本节点Transform=[{st.VisualTransform.ScaleX:F2}," +
                           $"{st.VisualTransform.SkewY:F2},{st.VisualTransform.SkewX:F2}," +
                           $"{st.VisualTransform.ScaleY:F2},{st.VisualTransform.TransX:F1},{st.VisualTransform.TransY:F1}] " +
                           $"累积world=[{st.AccumWorld.ScaleX:F2},{st.AccumWorld.SkewY:F2},{st.AccumWorld.SkewX:F2}," +
                           $"{st.AccumWorld.ScaleY:F2},{st.AccumWorld.TransX:F1},{st.AccumWorld.TransY:F1}] " +
                           $"最后非退化祖先={st.LastHealthyAncestor}";
                }

                st.Geom.Add($"[DRAW_CENSUS] {command,-26} 局部=({localBounds.Left:F1},{localBounds.Top:F1}," +
                            $"{localBounds.Width:F1}x{localBounds.Height:F1}) " +
                            $"CTM=[{ctm.ScaleX:F2},{ctm.SkewY:F2},{ctm.SkewX:F2},{ctm.ScaleY:F2}," +
                            $"{ctm.TransX:F1},{ctm.TransY:F1}] " +
                            $"设备=({dev.Left:F1},{dev.Top:F1},{dev.Width:F1}x{dev.Height:F1}) " +
                            $"{geomText} {brushText} {clipText} {from}" +
                            (localBounds.Width <= 0 || localBounds.Height <= 0 ? "  <= 局部尺寸为 0！" : "") +
                            ZeroVerdict(localBounds, dev, ctm, out bool degenerate));
                if (degenerate) st.Geom.Add("        " + ChainText(st));
            }
            catch { }
        }

        public static void ReferenceCheck(MilResourceProvider provider)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                if (provider is not MilChannelResourceProvider cp) return;
                var table = cp.Channel.Resources;

                var targets = new List<(uint Handle, string Kind)>();
                foreach (KeyValuePair<uint, MilResourceEntry> e in table.Entries)
                {
                    string kind = e.Value.Resource switch
                    {
                        MilEllipseGeometry => "EllipseGeometry",
                        MilPathGeometry => "PathGeometry",
                        MilRectangleGeometry => "RectangleGeometry",
                        MilLinearGradientBrush => "LinearGradientBrush",
                        _ => null,
                    };
                    if (kind != null) targets.Add((e.Key, kind));
                }
                if (targets.Count == 0)
                {
                    Console.Error.WriteLine("[DRAW_CENSUS·REF] **无信息**：资源表里没有几何/渐变资源可查");
                    return;
                }

                var refs = new Dictionary<uint, int>();
                foreach ((uint h, string _) in targets) refs[h] = 0;
                int scanned = 0;
                foreach (KeyValuePair<uint, MilResourceEntry> e in table.Entries)
                {
                    if (e.Value.Resource is not MilRenderDataResource rd || rd.RenderData == null) continue;
                    scanned++;
                    foreach (MilDrawInstruction ins in rd.RenderData.Instructions)
                    {
                        Bump(refs, ins.Geometry.Value);
                        Bump(refs, ins.Brush.Value);
                        Bump(refs, ins.Pen.Value);
                    }
                }

                if (scanned == 0)
                {
                    Console.Error.WriteLine(
                        $"[DRAW_CENSUS·REF] **无信息**：一条 render data 都没扫到" +
                        $"（几何/渐变资源在册 {targets.Count} 个）⇒ 引用数不可判，**不是 0**");
                    return;
                }

                Console.Error.WriteLine($"[DRAW_CENSUS·REF] 扫了 {scanned} 条 render data；" +
                                        $"几何/渐变资源 {targets.Count} 个（引用数=被多少条指令引用）：");
                foreach ((uint h, string kind) in targets)
                    Console.Error.WriteLine($"[DRAW_CENSUS·REF]   {kind,-22} 0x{h:x8} 引用={refs[h]}" +
                                            (refs[h] == 0 ? "   <= **零引用 ⇒ 资源在册但没人用**" : ""));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DRAW_CENSUS·REF] **无信息**：{ex.GetType().Name}");
            }

            static void Bump(Dictionary<uint, int> d, uint h)
            {
                if (h != 0 && d.ContainsKey(h)) d[h]++;
            }
        }

        public static void FrameEnd()
        {
            State st = St;
            if (!st.Enabled) return;
            st.Frame++;
            if (st.Frame > MaxFrames) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append($"[DRAW_CENSUS] frame={st.Frame} 指令种类={st.Counts.Count} 总执行=");
                int total = 0;
                foreach (int v in st.Counts.Values) total += v;
                sb.Append(total).Append("  ");
                var keys = new List<MilDrawCommand>(st.Counts.Keys);
                keys.Sort();
                foreach (MilDrawCommand k in keys)
                    sb.Append(k.ToString().Replace("MilDraw", "")).Append('×').Append(st.Counts[k]).Append(' ');
                Console.Error.WriteLine(sb.ToString());
                foreach (string line in st.Geom) Console.Error.WriteLine(line);
            }
            catch { }
        }

        private static SKRect MapRect(SKMatrix m, SKRect r)
        {
            SKPoint p0 = m.MapPoint(r.Left, r.Top), p1 = m.MapPoint(r.Right, r.Top);
            SKPoint p2 = m.MapPoint(r.Right, r.Bottom), p3 = m.MapPoint(r.Left, r.Bottom);
            return new SKRect(
                Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
                Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
                Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
                Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
        }

        private static bool ReadBool(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                return !string.IsNullOrWhiteSpace(v) && v != "0" &&
                       !v.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
