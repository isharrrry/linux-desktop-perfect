// WPF-on-Linux · RTL 位移取证：**MIL 视觉变换命令**的只读诊断（缺省关、有界）
// ============================================================================
// 【为什么需要它（2026-09-13，RTL 位移最后一段）】
//   前两段已判死：① shim 交出的字形是**完整宽度**（站点 E `Σadv−W=0`，不是压缩）；
//   ② 宿主的镜像**按契约下发**（PF 探针：`M1' M11=−1 M22=1 OffsetX=65.2559`、`ApplyMirrorTransform ⇒ True`）。
//   而桥侧 glyph-run 普查显示 **CTM 的 `m11` 两侧都是 +1.0417（正，没有镜像）** ⇒ 镜像在
//   `SkiaRenderBackend.cs:204-207` 的 `Concat(parentWorld, v.Transform, Translate(v.Offset))` 里**丢了**。
//   本诊断把"**PC 到底有没有把镜像交给 MIL**"变成读数：在**命令入口**打印
//   `MilCmdVisualSetTransform(0x1c)` / `MilCmdVisualSetOffset(0x1b)` 的实参，
//   并在 SetTransform 时顺便打出**解析后的矩阵**（走的就是投影期用的同一个 `TransformResolver`）。
//
// 【判据（互斥，三选一）】
//   · 该视觉**从来没有 SetTransform 行**            ⇒ **PC 没把镜像交给 MIL** ⇒ 修 PC/PF 侧（`Visual.cs` 的
//                                                     `VisualTransform` → MIL 那一步），不在桥里；
//   · 有 SetTransform 行且矩阵是 `M11=−1`（或 `DX≈86.26` 之类）而最终 `world.m11=+1`
//                                                   ⇒ **桥的 `TransformResolver`/投影丢了它** ⇒ 修桥；
//   · 有 SetTransform 行但矩阵本来就不是镜像（Identity/纯平移） ⇒ 往 PC 侧那个矩阵的**来源**查
//                                                     （本诊断会把"解析前的资源类型 + 原始字段"一并给出）。
//
// 【开关】`WPF_LINUX_VISTRANS_TRACE=1`（未设/空白/`0` ⇒ 一行不打）。与 `KEY_DIAG`/`MSGFLOW` 同族。
// 【过滤口径（如实登记）】命令层**拿不到"这是不是一个文本视觉"**（`Content` 只到 `MilRenderDataResource`
//   这一层，看不出里面是不是 GlyphRun）⇒ 退化为"**关注类必打 + 其余采样**"：
//     · 关注类 = `SetOffset` 带**负偏移**（镜像/右对齐的签名）、或 `SetTransform` 的**解析矩阵非单位**
//       （含任何镜像/平移/缩放）⇒ **必打**（上限 200 行）；
//     · 其余（Identity 变换、非负 offset）⇒ **只采样前 40 条**，之后静默但计数；
//     · **触顶会说话**：第一次被丢弃时打一行汇总（含累计 SetTransform/SetOffset 条数），不静默截断。
//   每行都附 `content=<Content 资源类型名>`，作为"是不是文本"的**代理线索**（文本视觉的 Content 通常非 null）。
// 【只读】只读资源表、只调 `TransformResolver.Resolve`（纯函数式解析，不改任何状态）；
//   写 `v.Visual.*` 的两行**在原逻辑之后**，本诊断不参与任何赋值。

using System;
using System.Globalization;
using System.Threading;
using SkiaSharp;
using WpfGfx.Linux.Interop;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Commands
{
    internal static class MilVisualTransformDiag
    {
        internal const string EnvVar = "WPF_LINUX_VISTRANS_TRACE";
        private const int MaxInteresting = 200;   // 关注类上限
        private const int MaxSampled = 40;        // 非关注类采样上限

        private static int s_on = -1;
        private static int s_lines;
        private static int s_sampled;
        private static int s_setTransform;
        private static int s_setOffset;
        private static int s_capReported;
        private static readonly object s_gate = new object();

        internal static bool Enabled
        {
            get
            {
                if (s_on < 0)
                {
                    string v = Environment.GetEnvironmentVariable(EnvVar);
                    s_on = (string.IsNullOrWhiteSpace(v) || v == "0") ? 0 : 1;
                }
                return s_on == 1;
            }
        }

        /// <summary>累计条数（不依赖开关；供探针/报告核账）。</summary>
        internal static int SetTransformCount => Volatile.Read(ref s_setTransform);
        internal static int SetOffsetCount => Volatile.Read(ref s_setOffset);

        internal static void NoteOffset(MilChannel ch, DUCE.ResourceHandle visual, double x, double y)
        {
            Interlocked.Increment(ref s_setOffset);
            if (!Enabled) return;
            bool interesting = x < 0 || y < 0;     // 负偏移 = 镜像/右对齐的签名
            Emit(interesting,
                 "SetOffset visual=0x" + visual.Value.ToString("x", CultureInfo.InvariantCulture)
                 + " offset=(X=" + F(x) + ", Y=" + F(y) + ")"
                 + " 负偏移=" + (interesting ? "是" : "否")
                 + " content=" + DescribeContent(ch, visual));
        }

        internal static void NoteTransform(MilChannel ch, DUCE.ResourceHandle visual, DUCE.ResourceHandle hTransform)
        {
            Interlocked.Increment(ref s_setTransform);
            if (!Enabled) return;

            SKMatrix m = hTransform.IsNull ? SKMatrix.Identity : TransformResolver.Resolve(ch, hTransform);
            bool mirror = m.ScaleX < 0f || m.ScaleY < 0f;
            bool nonIdentity = m.ScaleX != 1f || m.ScaleY != 1f || m.SkewX != 0f || m.SkewY != 0f
                            || m.TransX != 0f || m.TransY != 0f;
            MilResource raw = hTransform.IsNull ? null : ch.Resources.Lookup(hTransform);

            Emit(nonIdentity || mirror,
                 "SetTransform visual=0x" + visual.Value.ToString("x", CultureInfo.InvariantCulture)
                 + " hTransform=0x" + hTransform.Value.ToString("x", CultureInfo.InvariantCulture)
                 + " resKind=" + (raw == null ? (hTransform.IsNull ? "(null：无变换)" : "(表里查不到)") : raw.GetType().Name)
                 + " 原始字段=" + DescribeRaw(raw)
                 + " 解析矩阵=[M11=" + F(m.ScaleX) + " M12=" + F(m.SkewY) + " M21=" + F(m.SkewX)
                 + " M22=" + F(m.ScaleY) + " DX=" + F(m.TransX) + " DY=" + F(m.TransY) + "]"
                 + " 镜像=" + (mirror ? "是" : "否") + " 非单位=" + (nonIdentity ? "是" : "否")
                 + " content=" + DescribeContent(ch, visual));
        }

        /// <summary>解析**之前**的资源类型与原始字段（拿不到就写"读不到"）。</summary>
        private static string DescribeRaw(MilResource r)
        {
            switch (r)
            {
                case null: return "读不到（无资源）";
                case MilMatrixTransform mt:
                    return "Matrix[M11=" + F(mt.Matrix.S_11) + " M12=" + F(mt.Matrix.S_12)
                         + " M21=" + F(mt.Matrix.S_21) + " M22=" + F(mt.Matrix.S_22)
                         + " DX=" + F(mt.Matrix.DX) + " DY=" + F(mt.Matrix.DY) + "]";
                case MilTranslateTransform tt: return "Translate[X=" + F(tt.X) + " Y=" + F(tt.Y) + "]";
                case MilScaleTransform st:
                    return "Scale[SX=" + F(st.ScaleX) + " SY=" + F(st.ScaleY)
                         + " center=(" + F(st.CenterX) + "," + F(st.CenterY) + ")]";
                case MilRotateTransform rt:
                    return "Rotate[angle=" + F(rt.Angle) + " center=(" + F(rt.CenterX) + "," + F(rt.CenterY) + ")]";
                case MilSkewTransform kt: return "Skew[AX=" + F(kt.AngleX) + " AY=" + F(kt.AngleY) + "]";
                case MilTransformGroup g: return "TransformGroup[children=" + g.Children.Count + "]";
                default: return r.GetType().Name + "（未知变换类型）";
            }
        }

        /// <summary>该视觉的 `Content` 资源类型名 —— "是不是文本"的**代理线索**（命令层看不进 RenderData 内部）。</summary>
        private static string DescribeContent(MilChannel ch, DUCE.ResourceHandle visual)
        {
            try
            {
                if (!(ch.Resources.Lookup(visual) is MilVisualResource vr)) return "(非 Visual 资源)";
                DUCE.ResourceHandle c = vr.Visual.Content;
                if (c.IsNull) return "(Content=null)";
                MilResource cr = ch.Resources.Lookup(c);
                return cr == null ? "(Content 查不到)" : cr.GetType().Name;
            }
            catch (Exception e)
            {
                return "(读 content 抛 " + e.GetType().Name + ")";
            }
        }

        private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        private static void Emit(bool interesting, string line)
        {
            lock (s_gate)
            {
                int n = Volatile.Read(ref s_lines);
                if (interesting)
                {
                    if (n >= MaxInteresting) { ReportCapOnce(n, interesting: true); return; }
                    Volatile.Write(ref s_lines, n + 1);
                }
                else
                {
                    if (Volatile.Read(ref s_sampled) >= MaxSampled) { ReportCapOnce(n, interesting: false); return; }
                    Interlocked.Increment(ref s_sampled);
                    Volatile.Write(ref s_lines, n + 1);
                }
                try
                {
                    Console.Error.WriteLine("[VISTRANS] #" + (n + 1) + " " + line);
                    Console.Error.Flush();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>触顶通知（只打一次）：不静默截断 —— 报告里要能看到"后面还有多少条没打"。</summary>
        private static void ReportCapOnce(int printed, bool interesting)
        {
            if (Interlocked.Exchange(ref s_capReported, 1) != 0) return;
            try
            {
                Console.Error.WriteLine("[VISTRANS] 触顶：" + (interesting ? "关注类" : "非关注类")
                    + "已达上限（已打 " + printed + " 行；采样 " + Volatile.Read(ref s_sampled) + "/" + MaxSampled + "）"
                    + " ⇒ 之后不再打印。累计 SetTransform=" + SetTransformCount + " SetOffset=" + SetOffsetCount
                    + "（设 " + EnvVar + "=1 复跑可再取证）");
                Console.Error.Flush();
            }
            catch (Exception)
            {
            }
        }
    }
}
