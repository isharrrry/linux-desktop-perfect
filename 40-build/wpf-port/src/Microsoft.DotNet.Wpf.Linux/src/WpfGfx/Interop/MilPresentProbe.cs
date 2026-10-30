// T-B19 只读探针：呈现时的"活投影根树"形状读数（缺省关，零开销）。
//
// 目的：回答"一轮呈现里，页视觉到底在不在被画的树里、它有没有绘制内容"。
//   · `WPF_LINUX_MIL_PRESENT_ALL=1` ⇒ 每次呈现打一行 `[PVA]`（节点数/带内容节点数/指令总数）。
//   · `WPF_LINUX_MIL_ROOTGEO=1`   ⇒ 追加前两层的结构（句柄/子数/内容指令数/偏移/不透明度）。
//   · 直接写 stderr（与托管侧台账同汇），不受 `MilPresentation` 的 400 条预算限制。
// 语义：**只读**，不碰任何渲染出参、不改几何、不写 native 真值。

using System;
using System.Collections.Generic;
using System.Globalization;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Resources;

namespace WpfGfx.Linux.Interop
{
    internal static class MilPresentProbe
    {
        private static int _on = -1;
        private static int _geo = -1;
        private static long _seq;

        internal static bool Enabled
        {
            get
            {
                if (_on < 0) _on = string.Equals(Environment.GetEnvironmentVariable("WPF_LINUX_MIL_PRESENT_ALL"), "1", StringComparison.Ordinal) ? 1 : 0;
                return _on == 1;
            }
        }

        internal static bool Geo
        {
            get
            {
                if (_geo < 0) _geo = string.Equals(Environment.GetEnvironmentVariable("WPF_LINUX_MIL_ROOTGEO"), "1", StringComparison.Ordinal) ? 1 : 0;
                return _geo == 1;
            }
        }

        private static int _full = -1;

        internal static bool Full
        {
            get
            {
                if (_full < 0) _full = string.Equals(Environment.GetEnvironmentVariable("WPF_LINUX_MIL_ROOTFULL"), "1", StringComparison.Ordinal) ? 1 : 0;
                return _full == 1;
            }
        }

        private static int _dangling = -1;

        /// <summary>`WPF_LINUX_MIL_DANGLING=1`：逐次呈现统计"悬空子句柄"数（只读）。</summary>
        internal static bool Dangling
        {
            get
            {
                if (_dangling < 0) _dangling = string.Equals(Environment.GetEnvironmentVariable("WPF_LINUX_MIL_DANGLING"), "1", StringComparison.Ordinal) ? 1 : 0;
                return _dangling == 1;
            }
        }

        private static void Emit(string line)
        {
            try { Console.Error.WriteLine(line); Console.Error.Flush(); } catch { }
        }

        /// <summary>每次呈现后调用（只读）。</summary>
        internal static void OnPresent(long callNo, IntPtr hwnd, int width, int height,
                                       long drawn, long notDrawn, MilVisual root, MilChannel channel)
        {
            if (!Enabled && !Geo && !Dangling) return;
            try
            {
                int nodes = 0, withContent = 0, instr = 0;
                int docNodes = 0, docContent = 0, docInstr = 0;
                var top = new List<MilVisual>();
                var byInstr = new List<(int n, MilVisual v)>();

                var stack = new Stack<(MilVisual v, int d, float ax, float ay)>();
                if (root != null) stack.Push((root, 0, 0f, 0f));
                while (stack.Count > 0)
                {
                    (MilVisual v, int d, float ax, float ay) = stack.Pop();
                    nodes++;
                    float vx = ax + v.Offset.X, vy = ay + v.Offset.Y;
                    bool inDoc = vx >= 240f && vx <= 910f && vy >= 90f && vy <= 480f;
                    if (inDoc) docNodes++;
                    int ic = 0;
                    if (v.Content != null)
                    {
                        withContent++; ic = v.Content.Instructions.Count; instr += ic; byInstr.Add((ic, v));
                        if (inDoc) { docContent++; docInstr += ic; }
                    }
                    if (d == 1) top.Add(v);
                    for (int i = v.Children.Count - 1; i >= 0; i--) stack.Push((v.Children[i], d + 1, vx, vy));
                }

                string head = string.Format(CultureInfo.InvariantCulture,
                    "[PVA] seq={0} call={1} hwnd=0x{2:x} {3}x{4} drawn={5} notDrawn={6} nodes={7} withContent={8} instr={9} docNodes={10} docContent={11} docInstr={12}",
                    ++_seq, callNo, hwnd.ToInt64(), width, height, drawn, notDrawn, nodes, withContent, instr, docNodes, docContent, docInstr);

                // T-B19：**悬空子句柄**读数（父的 Children 里列出了、但资源表里已经没有的句柄）。
                //  画面后果 = `VisualProjection.Project` 里 `childNode == null ⇒ continue`（整棵被跳过）。
                if (Dangling && channel != null)
                {
                    int dangling = 0, danglingVis = 0;
                    var sample = new System.Text.StringBuilder();
                    foreach (KeyValuePair<uint, MilResourceEntry> kv in channel.Resources.Entries)
                    {
                        if (kv.Value?.Resource is not MilVisualResource vr) continue;
                        foreach (DUCE.ResourceHandle ch in vr.Visual.Children)
                        {
                            if (ch.IsNull) continue;
                            if (channel.GetVisual(ch) != null) continue;
                            dangling++;
                            if (sample.Length < 400)
                            {
                                sample.Append(string.Format(CultureInfo.InvariantCulture,
                                    " [dangling parent=0x{0:x} child=0x{1:x}]", kv.Key, ch.Value));
                            }
                        }
                    }
                    head += string.Format(CultureInfo.InvariantCulture,
                        " dangling={0} danglingParents={1}{2}", dangling, danglingVis, sample.ToString());
                }

                if (Geo)
                {
                    var sb = new System.Text.StringBuilder(head);
                    sb.Append("\n    TOP:");
                    foreach (MilVisual c in top)
                    {
                        sb.Append(string.Format(CultureInfo.InvariantCulture,
                            " [h=0x{0:x} k={1} c={2} off={3:0.#},{4:0.#} op={5:0.##}]",
                            c.Handle.Value, c.Children.Count, c.Content == null ? "-" : c.Content.Instructions.Count.ToString(),
                            c.Offset.X, c.Offset.Y, c.Opacity));
                    }
                    byInstr.Sort((a, b) => b.n.CompareTo(a.n));
                    sb.Append("\n    TOPINSTR:");
                    for (int i = 0; i < byInstr.Count && i < 8; i++)
                    {
                        MilVisual v = byInstr[i].v;
                        sb.Append(string.Format(CultureInfo.InvariantCulture,
                            " [h=0x{0:x} instr={1} k={2} off={3:0.#},{4:0.#} op={5:0.##}]",
                            v.Handle.Value, byInstr[i].n, v.Children.Count, v.Offset.X, v.Offset.Y, v.Opacity));
                    }
                    head = sb.ToString();
                }
                if (Full)
                {
                    var fb = new System.Text.StringBuilder("\n    FULL:");
                    var st2 = new Stack<(MilVisual v, int d, float ax, float ay)>();
                    if (root != null) st2.Push((root, 0, 0f, 0f));
                    while (st2.Count > 0)
                    {
                        (MilVisual v, int d, float ax2, float ay2) = st2.Pop();
                        float vx = ax2 + v.Offset.X, vy = ay2 + v.Offset.Y;
                        int ic = v.Content == null ? -1 : v.Content.Instructions.Count;
                        fb.Append(string.Format(CultureInfo.InvariantCulture,
                            "\n      d{0} h=0x{1:x} k={2} off={3:0.#},{4:0.#} instr={5} op={6:0.##}",
                            d, v.Handle.Value, v.Children.Count, vx, vy, ic, v.Opacity));
                        for (int i = v.Children.Count - 1; i >= 0; i--) st2.Push((v.Children[i], d + 1, vx, vy));
                    }
                    Emit(fb.ToString());
                }
                Emit(head);
            }
            catch (Exception ex) { Emit("[PVA] err " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
