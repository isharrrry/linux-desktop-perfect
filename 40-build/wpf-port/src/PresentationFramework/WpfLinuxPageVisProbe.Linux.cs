// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-B12（PAGINATED-PAGE-CONTENT-VISUALS）：分页页视觉「壳内」内容视觉的**只读**逐跳读数（[PAGEVIS]）。
//
// 【射程】只打行：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值、不碰几何。
// `WPF_PAGEVIS_PROBE=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxPageVisProbe
    {
        private const int TraceMax = 20000;
        private static int _n;
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_PAGEVIS_PROBE"); }
                    catch (Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;
                }
                return _enabled == 1;
            }
        }

        internal static string Id(object o)
        {
            if (o == null) { return "null"; }
            try
            {
                return "0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString("x", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "NA"; }
        }

        internal static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string B(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetDescendantBounds(v);
                return r.IsEmpty ? "empty" : (N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height));
            }
            catch (Exception) { return "NA"; }
        }

        private static string C(Visual v)
        {
            if (v == null) { return "null"; }
            try
            {
                Rect r = VisualTreeHelper.GetContentBounds(v);
                return r.IsEmpty ? "empty" : (N(r.X) + "," + N(r.Y) + "," + N(r.Width) + "," + N(r.Height));
            }
            catch (Exception) { return "NA"; }
        }

        private static int K(Visual v)
        {
            if (v == null) { return -1; }
            try { return VisualTreeHelper.GetChildrenCount(v); } catch (Exception) { return -2; }
        }

        private static void SubRec(Visual v, int lvl, int maxLvl, System.Text.StringBuilder sb, ref int budget)
        {
            if (v == null || lvl > maxLvl || budget <= 0) { return; }
            budget--;
            int k = K(v);
            sb.Append("L").Append(lvl).Append(':').Append(Id(v)).Append(",k=").Append(k)
              .Append(",b=").Append(B(v)).Append(",c=").Append(C(v)).Append(" | ");
            for (int i = 0; i < k && i < 8; i++)
            {
                Visual c;
                try { c = VisualTreeHelper.GetChild(v, i) as Visual; } catch (Exception) { c = null; }
                SubRec(c, lvl + 1, maxLvl, sb, ref budget);
            }
        }

        internal static void Report(string site, string detail, Visual v)
        {
            if (!Enabled) { return; }
            if (_n >= TraceMax)
            {
                if (_n == TraceMax) { _n++; Emit("[PAGEVIS] site=" + site + " trace=suppressed-after-" + TraceMax); }
                return;
            }
            _n++;
            var sb = new System.Text.StringBuilder();
            int budget = 40;
            try { SubRec(v, 0, 3, sb, ref budget); } catch (Exception) { sb.Append("NA"); }
            Emit("[PAGEVIS] site=" + site + " " + detail + " vis=" + Id(v) + " kids=" + K(v)
                 + " b=" + B(v) + " c=" + C(v) + " SUB=" + sb.ToString()
                 + " NOINFO=pagevis-readonly");
        }

        private static void Emit(string line)
        {
            try { Console.Error.WriteLine(line); Console.Error.Flush(); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// `T-B12`（`PAGINATED-PAGE-CONTENT-VISUALS`）：**在屏页"被搬空"修复**的**驱动闸**。
    /// 逐跳现取见 `reapply-patches.py` 内该块的说明。`WPF_PAGEPAGE_REDRIVE=0` ⇒ 关（反极性腿）。
    /// </summary>
    internal static class WpfLinuxPageVisDrive
    {
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_PAGEPAGE_REDRIVE"); }
                    catch (Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;         // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        /// <summary>
        /// `T-B12`：把"本页**造视觉那一刻**"的 `trackVisual` 子视觉**引用**记下来（显示时换父回去用）。
        /// ⚠️ **不**复制视觉、**不**改任何 native 真值、**不**触发任何 `FsQuery*`（只读托管视觉树）。
        /// </summary>
        internal static void KeepVisuals(ContainerVisual trackVisual, ref System.Collections.Generic.List<Visual> keep)
        {
            if (!Enabled) { return; }
            if (trackVisual == null) { return; }
            int n = trackVisual.Children.Count;
            if (n == 0) { return; }
            var list = new System.Collections.Generic.List<Visual>(n);
            for (int i = 0; i < n; i++) { list.Add(trackVisual.Children[i]); }
            keep = list;
        }

        /// <summary>
        /// `T-B12`：把 `keep` 里的视觉**换父**到 `target`（先把它们从各自旧父摘除，再按序 `Insert`）。
        /// **只**在 `target` 为空时用。**不**查 native、**不**调 `ValidateVisual`（子树内容原样搬回）。
        /// </summary>
        internal static void ReparentInto(ContainerVisual target, System.Collections.Generic.List<Visual> keep)
        {
            if (!Enabled) { return; }
            if (target == null || keep == null || keep.Count == 0) { return; }
            for (int i = 0; i < keep.Count; i++)
            {
                Visual v = keep[i];
                if (v == null) { continue; }
                ContainerVisual oldParent = VisualTreeHelper.GetParent(v) as ContainerVisual;
                if (oldParent != null) { oldParent.Children.Remove(v); }
            }
            for (int i = 0; i < keep.Count; i++)
            {
                Visual v = keep[i];
                if (v == null) { continue; }
                target.Children.Insert(i, v);
            }
        }
    }
}
