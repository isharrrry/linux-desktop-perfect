using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pm0138
{
    // PM-0138 (clean build): give the file-list panes their contracted height by
    // enforcing RowDefinition.MinHeight on the row that actually re-allocates.
    //
    // Measured at window 600x518.4 DIP (100 DPI X server => 625x540 px):
    //   split view (WorkingCopySplitFileListContainerView) needs 200.64
    //     = 2 * (28.8 pane header + 71.04 list) + 0.96 splitter
    //   WorkingCopyChangeSetView slot = 187.2, of which 28.8 is its own header row
    //   => the allocating Grid row must be >= 229.44; commit-list row floor 67.2
    //   (header 22 + 2 rows * 20 + 4 padding).
    // Enforcing MinHeight on child elements was measured NOT to re-allocate
    // (the child only overflows its slot), so this build only touches RowDefinitions.
    public static class Hook
    {
        private const double PaneMinHeight = 66.0;
        private const double SplitViewHeight = 200.64;
        private const double ChangeSetHeaderFallback = 28.8;
        private const double CommitRowFloor = 67.2;

        private static readonly ConditionalWeakTable<FrameworkElement, object> Applied =
            new ConditionalWeakTable<FrameworkElement, object>();

        public static void Apply(object view)
        {
            var fe = view as FrameworkElement;
            if (fe == null) return;
            if (fe.IsLoaded) { Adjust(fe); return; }
            fe.Loaded += (s, e) => Adjust(fe);
        }

        private static void Adjust(FrameworkElement view)
        {
            try
            {
                if (Applied.TryGetValue(view, out _)) return;
                Applied.Add(view, new object());

                // 1) per-pane list viewport floor (contract)
                foreach (var name in new[] { "FileListContentPresenter", "FileListContentPresenter2" })
                {
                    var f = view.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var pane = f == null ? null : f.GetValue(view) as FrameworkElement;
                    if (pane == null) continue;
                    var pg = FindGrid(pane, 2);
                    if (pg == null) continue;
                    int pr = RowOfChild(pg.Value.Grid, pg.Value.Child);
                    if (pr >= 0) pg.Value.Grid.RowDefinitions[pr].MinHeight = PaneMinHeight;
                }

                // 2) the row that holds the split view itself
                var inner = FindGrid(view, 2);
                if (inner != null)
                {
                    int r = RowOfChild(inner.Value.Grid, inner.Value.Child);
                    if (r >= 0) inner.Value.Grid.RowDefinitions[r].MinHeight = SplitViewHeight;
                }

                // 3) the row that re-allocates: the Grid directly above WorkingCopyChangeSetView
                FrameworkElement changeSet = null;
                for (var d = VisualTreeHelper.GetParent(view); d != null; d = VisualTreeHelper.GetParent(d))
                {
                    var e = d as FrameworkElement;
                    if (e != null && e.GetType().Name.IndexOf("WorkingCopyChangeSetView", StringComparison.Ordinal) >= 0)
                    {
                        changeSet = e;
                        break;
                    }
                }
                if (changeSet == null) return;

                double header = changeSet.ActualHeight - view.ActualHeight;
                if (header < 0 || header > 120) header = ChangeSetHeaderFallback;

                var outer = FindGrid(changeSet, 2);
                if (outer == null) return;
                int cr = RowOfChild(outer.Value.Grid, outer.Value.Child);
                if (cr < 0) return;

                var rows = outer.Value.Grid.RowDefinitions;
                rows[cr].MinHeight = SplitViewHeight + header;

                // commit-list row floor: the row hosting a ListView* child; fixed toolbars
                // and the 1px splitter row must stay untouched.
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(outer.Value.Grid); i++)
                {
                    var c = VisualTreeHelper.GetChild(outer.Value.Grid, i) as FrameworkElement;
                    if (c == null) continue;
                    int r = Grid.GetRow(c);
                    if (r == cr || r < 0 || r >= rows.Count) continue;
                    if (c.GetType().Name.IndexOf("ListView", StringComparison.Ordinal) >= 0)
                        rows[r].MinHeight = CommitRowFloor;
                }
            }
            catch
            {
                // A layout helper must never take the app down; worst case is today's behaviour.
            }
        }

        private static PathGrid? FindGrid(DependencyObject from, int minRows)
        {
            UIElement child = null;
            var d = from;
            for (int i = 0; i < 16 && d != null; i++)
            {
                var parent = VisualTreeHelper.GetParent(d);
                if (parent is Grid g && g.RowDefinitions.Count >= minRows)
                {
                    if (child == null) child = d as UIElement;
                    return new PathGrid(g, child);
                }
                if (parent != null) child = parent as UIElement;
                d = parent;
            }
            return null;
        }

        private static int RowOfChild(Grid g, UIElement child)
        {
            if (child == null) return -1;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(g); i++)
            {
                if (VisualTreeHelper.GetChild(g, i) == child) return Grid.GetRow(child);
            }
            return -1;
        }

        private struct PathGrid
        {
            public readonly Grid Grid;
            public readonly UIElement Child;
            public PathGrid(Grid grid, UIElement child) { Grid = grid; Child = child; }
        }
    }
}
