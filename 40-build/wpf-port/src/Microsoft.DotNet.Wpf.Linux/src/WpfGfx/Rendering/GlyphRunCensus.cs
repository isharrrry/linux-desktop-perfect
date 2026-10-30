// Licensed to the .NET Foundation under one or more agreements.
//
// 字形 run 的**只读普查**（T2b 诊断仪表）。
//
// 【它回答什么】"本帧所有 MilGlyphRun" 的三个数：
//   ① 本帧有多少个 glyph run（按**绘制次数**，另附不同句柄数），其中**全为 .notdef(id==0)** 的有几个；
//   ② 字形 id 的分布：id==0 的个数、最大 id、按区间分桶（尤其**非拉丁区**）；
//   ③ 每个 run 的**面标识** —— ⚠ 见下面「做不到的一条」。
//
// 【为什么不扰动被测对象（这条是纪律，M7b 在"装探针"上栽过一次）】
//   1. **不替换、不包装** `GlyphRunRenderer` 扩展点：本类只在 T4 既有的
//      `MilDrawGlyphRun` / `MilGlyphRunDrawing` 两条**绘制路径旁**读一眼**已经解码好的**
//      `MilGlyphRun` 资源（`GlyphIndices` 由 MilCommandDispatcher.cs:909 填好，本类不参与解码）。
//      ⇒ 被测对象（PC 的 shaping 结果、渲染器的面选择）**一个字节都没被改**。
//   2. **缺省全关**：`WPF_LINUX_GLYPH_CENSUS` 未设时 `Enabled == false`，
//      调用点只多一次静态 bool 判断，不分配、不格式化、不写任何输出。
//   3. **绝不碰 `RenderDiagnostics`**：runner 的判据里有 `未画种类 0`，
//      普查若记进同一本账就会污染那条判据。本类是**独立**的静态计数。
//   4. **只写 stderr**，且有界（每帧最多 N 行、只打前 F 帧）。不写文件、不改资源表。
//   5. 内部 try/catch 兜底：诊断自身出错也不许把渲染带崩。
//
// 【做不到的一条（如实登记）】
//   ③ 每个 run 的**面标识**在当前数据结构里**拿不到**：`MilGlyphRun`（Resources/MilResources.cs）
//   没有面字段，而线格 `MILCMD_GLYPHRUN_CREATE` 里**是有** `[FieldOffset(8)] PIDWriteFont` 的
//   （Commands/MilCommandStructs.cs:435 附近），只是 `MilCommandDispatcher.cs:897-909`
//   填 `MilGlyphRun` 时**没有搬它**。⇒ 要答 ③ 需要：
//     · `Resources/MilResources.cs` 给 `MilGlyphRun` 加一个 `PIDWriteFont` 字段（**我的车道**）；
//     · `Commands/MilCommandDispatcher.cs` 加一行 `g.PIDWriteFont = s.PIDWriteFont;`（**不是我的车道**，需主控协调）。
//   本类已预留 `PidWriteFont` 的采集与输出，字段一加上就会自动出现在读数里。

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Rendering
{
    internal static class GlyphRunCensus
    {
        private const string EnvVar = "WPF_LINUX_GLYPH_CENSUS";
        private const int MaxDetailLines = 40;   // 每帧最多逐 run 打印多少行
        private const int MaxFrames = 3;         // 只打印前几帧，避免动画帧刷屏

        /// <summary>
        /// 计数状态**按线程**保存。
        ///
        /// ⚠【为什么必须 ThreadStatic —— 这里踩过一次】首版用全局静态字段，
        ///   结果 `GlyphRunCensusTests` 在 xUnit 的**并行测试类**下**三次里红一次**：
        ///   别的测试类在同一进程里并发渲染，它们的 `FrameBegin()` 把我这边的计数清零了。
        ///   这是"**仪表被环境扰动**"那一族（与"观测对象与你以为的不是同一个"同源）。
        ///   生产路径每帧单线程渲染，按线程保存与语义一致；测试与生产都不再互相串。
        /// </summary>
        private sealed class State
        {
            public bool Enabled;
            public int Frame, Runs, AllNotDef, Glyphs, IdZero, MaxId;
            public int PidZero;                                   // PIDWriteFont == 0 的 run 数
            public string RendererType;                           // 注册的 GlyphRunRenderer 是谁（只读）
            public int HookTrue, HookFalse, HookNullRenderer, HookNullResource;
            public readonly List<double> OriginYs = new List<double>();   // 逐 run 原点 Y（去重后判定"行是否推进"）
            // 【相关性判据】逐 run 一对：originDIP.y 与"用绘制时的 CTM 把 Origin 映射到设备后的 Y"。
            // 目的：区分"上游就把多行放在同一 Y"（设备 Y 也相同）与"origin 只是行内相对量"（设备 Y 各不相同）。
            public readonly List<(double OriginY, double DeviceY)> YPairs = new List<(double, double)>();
            public SKMatrix Ctm = SKMatrix.Identity;
            public readonly Dictionary<ulong, int> PidCounts = new Dictionary<ulong, int>();
            public readonly int[] Buckets = new int[5];
            public readonly HashSet<uint> Handles = new HashSet<uint>();
            public readonly List<string> Detail = new List<string>();
            public (int Runs, int Distinct, int AllNotDef, int Glyphs, int IdZero, int MaxId, int NonLatin,
                    int PidZero, int PidDistinct, ulong PidFirst) Last;
        }

        [ThreadStatic] private static State s_state;

        private static State St => s_state ??= new State { Enabled = ReadBool(EnvVar) };

        public static bool Enabled => St.Enabled;

        /// <summary>供测试显式开关（生产路径一律由环境变量决定，见 ReadBool）。</summary>
        internal static void ForceEnabled(bool on)
        {
            St.Enabled = on;
            Reset();
        }

        /// <summary>上一帧的读数（测试用；不依赖 stderr 解析）。</summary>
        /// <summary>上一帧逐 run 的原点 Y（DIP；测试用，不依赖 stderr 解析）。</summary>
        internal static IReadOnlyList<double> LastOriginYs => St.OriginYs;

        /// <summary>上一帧 (originDIP.y, 设备Y) 成对读数（测试用）。</summary>
        internal static IReadOnlyList<(double OriginY, double DeviceY)> LastYPairs => St.YPairs;

        /// <summary>上一帧逐 run 明细行（测试用，直接断言 devY/CTM 是否如实带出）。</summary>
        internal static IReadOnlyList<string> LastDetailLines => St.Detail;

        internal static (int Runs, int DistinctHandles, int AllNotDef, int Glyphs,
                         int IdZero, int MaxId, int NonLatin,
                         int PidZero, int PidDistinct, ulong PidFirst) LastFrame => St.Last;

        private static void Reset()
        {
            State st = St;
            st.Runs = 0; st.AllNotDef = 0; st.Glyphs = 0; st.IdZero = 0; st.MaxId = 0;
            st.PidZero = 0; st.PidCounts.Clear();
            st.HookTrue = 0; st.HookFalse = 0; st.HookNullRenderer = 0; st.HookNullResource = 0;
            st.OriginYs.Clear();
            st.YPairs.Clear();
            st.Handles.Clear(); st.Detail.Clear();
            Array.Clear(st.Buckets, 0, st.Buckets.Length);
        }

        /// <summary>帧开始：清零本期计数。</summary>
        public static void FrameBegin()
        {
            if (!St.Enabled) return;
            Reset();
        }

        /// <summary>在既有的字形绘制路径旁读一眼已解码的 MilGlyphRun。只读，不改任何状态。</summary>
        /// <summary>记下本次字形绘制时的画布 CTM（只读；供把 Origin 映射到设备以做相关性判定）。</summary>
        public static void NoteCtm(SKMatrix ctm)
        {
            State st = St;
            if (!st.Enabled) return;
            try { st.Ctm = ctm; } catch { }
        }

        public static void Observe(MilResourceProvider provider, MilResourceHandle handle)
        {
            State st = St;
            if (!st.Enabled) return;
            try
            {
                if (st.RendererType == null)
                {
                    // 只读：把"到底是谁在画字形"记下来。null = 没人注册。
                    st.RendererType = provider?.GlyphRunRenderer == null
                        ? "(null：没有注册 GlyphRunRenderer)"
                        : (provider.GlyphRunRenderer.Method.DeclaringType?.FullName ?? "?");
                }
                object raw = provider?.Lookup(handle);
                if (raw == null) st.HookNullResource++;
                if (raw is not MilGlyphRun run) return;

                ushort[] ids = run.GlyphIndices ?? Array.Empty<ushort>();
                st.Runs++;
                st.Handles.Add(handle.Value);

                int zeros = 0, localMax = 0;
                foreach (ushort id in ids)
                {
                    if (id == 0) { zeros++; st.IdZero++; }
                    if (id > localMax) localMax = id;
                    if (id > st.MaxId) st.MaxId = id;
                    st.Buckets[Bucket(id)]++;
                }
                st.Glyphs += ids.Length;
                if (ids.Length > 0 && zeros == ids.Length) st.AllNotDef++;

                // 面标识：0 是**歧义值**（可能是真 0，也可能是没被填）⇒ 单独计数，
                // 由读数区分"全 0 ⇒ 无信息"与"有多个不同值 ⇒ 确实在按面区分"。
                if (run.PIDWriteFont == 0) st.PidZero++;
                st.PidCounts.TryGetValue(run.PIDWriteFont, out int pc);
                st.PidCounts[run.PIDWriteFont] = pc + 1;

                st.OriginYs.Add(run.Origin.Y);
                // 同一次绘制：把 Origin 用**当时的 CTM** 映射到设备坐标（映射用的 CTM 也一并报，
                // 免得读者以为设备 Y 是"我们凭空算的"）。单位：设备像素。
                SKPoint devPt = st.Ctm.MapPoint(run.Origin.X, run.Origin.Y);
                st.YPairs.Add((run.Origin.Y, devPt.Y));

                if (st.Detail.Count < MaxDetailLines)
                {
                    var sb = new StringBuilder();
                    sb.Append("[GLYPH_CENSUS] run#").Append(st.Runs - 1)
                      .Append(" handle=0x").Append(handle.Value.ToString("x8"))
                      .Append(" n=").Append(ids.Length)
                      .Append(" id0=").Append(zeros)
                      .Append(" max=").Append(localMax)
                      .Append(" pid=0x").Append(run.PIDWriteFont.ToString("x"))
                      // 原点：直接取 MilGlyphRun.Origin（线格搬运来的**DIP**，未乘 DPI 缩放）
                      .Append(" originDIP=(").Append(run.Origin.X.ToString("F3")).Append(',')
                      .Append(run.Origin.Y.ToString("F3")).Append(')')
                      // 该 run 的**设备位置**（用绘制当时的 CTM 映射 Origin）与该 CTM 本身。
                      // 判"行位置有没有带上"：同段落相邻行的 devY 与 CTM 的 Ty(f) 是否**逐行递增**。
                      .Append(" devX=").Append(devPt.X.ToString("F3"))
                      .Append(" devY=").Append(devPt.Y.ToString("F3"))
                      .Append(" CTM=[").Append(st.Ctm.ScaleX.ToString("F4")).Append(',')
                      .Append(st.Ctm.SkewY.ToString("F4")).Append(',')
                      .Append(st.Ctm.SkewX.ToString("F4")).Append(',')
                      .Append(st.Ctm.ScaleY.ToString("F4")).Append(',')
                      .Append(st.Ctm.TransX.ToString("F3")).Append(',')
                      .Append(st.Ctm.TransY.ToString("F3")).Append(']')
                      .Append(ids.Length > 0 && zeros == ids.Length ? "  <= 全 .notdef" : "")
                      // 视觉来源（句柄 + 本节点 Transform + 累积 world）：字形绘制没有 `Geometry()` 那条
                      // `来源=` 行，这一格补上"这些字形画在哪个视觉下、那一代 world 有没有镜像"。
                      // 缺省关时是空串 ⇒ 关掉即逐字回到原行（见 Census 里那段的说明）。
                      .Append(DrawInstructionCensus.CurrentVisualProvenance)
                      .Append(" first16=");
                    for (int i = 0; i < ids.Length && i < 16; i++)
                        sb.Append(i == 0 ? "" : ",").Append(ids[i]);
                    st.Detail.Add(sb.ToString());
                }
            }
            catch { /* 诊断自身出错不许影响渲染 */ }
        }

        /// <summary>
        /// 只读：记一次"扩展点到底有没有真的把字形画出去"。
        /// 这一位是**判定"字形走的是哪条路"的关键**：若一路 false，说明
        /// `GlyphRunRenderer` 没被成功调用（注册为空/资源查不到/渲染器返回 false），
        /// 那么"在 TextRenderer 里加 census 看不到东西"就有了直接解释。
        /// </summary>
        public static void ObserveRenderResult(bool ok)
        {
            State st = St;
            if (!st.Enabled) return;
            try { if (ok) st.HookTrue++; else st.HookFalse++; } catch { }
        }

        public static void ObserveNullRenderer()
        {
            State st = St;
            if (!st.Enabled) return;
            try { st.HookNullRenderer++; } catch { }
        }

        /// <summary>帧结束：把本帧读数一次性写到 stderr。</summary>
        public static void FrameEnd()
        {
            State st = St;
            if (!st.Enabled) return;
            ulong pidFirst = 0; bool pidFirstSet = false;
            foreach (ulong k in st.PidCounts.Keys)
                if (!pidFirstSet || k < pidFirst) { pidFirst = k; pidFirstSet = true; }
            st.Last = (st.Runs, st.Handles.Count, st.AllNotDef, st.Glyphs, st.IdZero, st.MaxId,
                       st.Buckets[3] + st.Buckets[4], st.PidZero, st.PidCounts.Count, pidFirst);
            st.Frame++;
            if (st.Frame > MaxFrames) return;
            try
            {
                Console.Error.WriteLine(
                    $"[GLYPH_CENSUS] frame={st.Frame} runs(绘制次数)={st.Runs} 不同句柄={st.Handles.Count} " +
                    $"全notdef的run={st.AllNotDef} glyphs={st.Glyphs} id0={st.IdZero} maxId={st.MaxId} " +
                    $"非拉丁(id>=0x1000)={st.Buckets[3] + st.Buckets[4]} " +
                    $"桶[0]={st.Buckets[0]} [1,FF]={st.Buckets[1]} [100,FFF]={st.Buckets[2]} " +
                    $"[1000,3FFF]={st.Buckets[3]} [>=4000]={st.Buckets[4]} " +
                    $"面标识: pid==0的run={st.PidZero} 不同面数={st.PidCounts.Count} " +
                    $"渲染器={st.RendererType ?? "(未知)"} hook成功={st.HookTrue} hook失败={st.HookFalse} " +
                    $"无渲染器={st.HookNullRenderer} 资源查不到={st.HookNullResource}" +
                    (st.PidCounts.Count <= 1
                        ? "  [无信息：所有 run 的面标识相同 —— 若全为 0 则可能是字段没被填]"
                        : "  [有信息：确有多个不同面]"));
                // 【行推进判据】同一段落 N 行的原点 Y 若全相同 ⇒ "多行摞印"；这条把它变成一眼可判。
                var ys = new List<double>(st.OriginYs);
                ys.Sort();
                var distinct = new List<double>();
                foreach (double y in ys)
                    if (distinct.Count == 0 || Math.Abs(distinct[distinct.Count - 1] - y) > 1e-6) distinct.Add(y);
                Console.Error.WriteLine(
                    $"[GLYPH_CENSUS] 原点Y汇总: runs={st.OriginYs.Count} distinct_origin_y={distinct.Count}" +
                    (distinct.Count <= 8
                        ? "  Y值(DIP)=[" + string.Join(",", distinct.ConvertAll(v => v.ToString("F3"))) + "]"
                        : "  Y值(DIP)=[前8个:" + string.Join(",", distinct.GetRange(0, 8).ConvertAll(v => v.ToString("F3"))) + "…]"));

                // 【相关性一行】按 originDIP.y 分组：每组有几个 run、它们的设备 Y 去重后有几个。
                // 判据：同一 originY 组里 distinctDeviceY == 1 ⇒ 上游就叠在同一处；
                //        distinctDeviceY > 1 ⇒ origin 只是行内相对量，真凶在变换/绘制链。
                var groups = new List<(double Y, int Runs, List<double> DevYs)>();
                foreach ((double oy, double dy) in st.YPairs)
                {
                    int gi = groups.FindIndex(g => Math.Abs(g.Y - oy) <= 1e-6);
                    if (gi < 0) { groups.Add((oy, 0, new List<double>())); gi = groups.Count - 1; }
                    (double Y, int Runs, List<double> DevYs) g = groups[gi];
                    g.Runs++;
                    if (!g.DevYs.Exists(v => Math.Abs(v - dy) <= 1e-6)) g.DevYs.Add(dy);
                    groups[gi] = g;
                }
                groups.Sort((a, b) => a.Y.CompareTo(b.Y));
                foreach ((double Y, int Runs, List<double> DevYs) g in groups)
                    Console.Error.WriteLine(
                        $"[GLYPH_CENSUS] 相关性: originDIP.y={g.Y:F3} 的 run 数={g.Runs} " +
                        $"其设备Y去重数={g.DevYs.Count}" +
                        (g.DevYs.Count <= 4 ? " 设备Y=[" + string.Join(",", g.DevYs.ConvertAll(v => v.ToString("F3"))) + "]" : "") +
                        (g.DevYs.Count == 1
                            ? "  ⇒ **设备Y全同 = 上游就叠在同一处**"
                            : "  ⇒ **设备Y各不相同 = origin 只是行内相对量**"));

                foreach (string line in st.Detail) Console.Error.WriteLine(line);
            }
            catch { }
        }

        private static int Bucket(ushort id) =>
            id == 0 ? 0 : id < 0x100 ? 1 : id < 0x1000 ? 2 : id < 0x4000 ? 3 : 4;

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
