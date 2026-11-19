using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LdpFix
{
    // PM-0109 档1：托管侧控件注册桥。
    // 平台在 shim 侧提供 wpf_atspi_register_control / wpf_atspi_clear_controls；
    // 控件层只有托管侧看得见，所以由本 helper 在 UI 线程遍历整窗的 AutomationPeer，
    // 把可命名控件逐个注册进 AT-SPI 树。仅 WPF_LINUX_ATSPI=1 时工作。
    public static class UiaBridge
    {
        [DllImport("libwpfwin32.so", EntryPoint = "wpf_atspi_register_control", CallingConvention = CallingConvention.Cdecl)]
        private static extern int RegisterControl(IntPtr hwnd, int id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string role,
            int roleId, int x, int y, int w, int h, int hasAction);

        [DllImport("libwpfwin32.so", EntryPoint = "wpf_atspi_clear_controls", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ClearControls(IntPtr hwnd);

        private static FrameworkElement s_seed;
        private static DispatcherTimer s_timer;
        private static bool s_disabled;
        private static int s_tick;

        public static void Install(object element)
        {
            try
            {
                if (s_disabled) return;
                if (Environment.GetEnvironmentVariable("WPF_LINUX_ATSPI") != "1") return;
                var fe = element as FrameworkElement;
                if (fe == null) return;
                if (s_seed == null) s_seed = fe;
                if (s_timer == null)
                {
                    s_timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    s_timer.Tick += (s, e) => RegisterNow();
                    s_timer.Start();
                }
                RegisterNow();
            }
            catch (Exception e)
            {
                s_disabled = true;
                Console.Error.WriteLine("[UIA] install EX " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void RegisterNow()
        {
            if (s_disabled || s_seed == null) return;
            try
            {
                Window win = Window.GetWindow(s_seed);
                DependencyObject root = (DependencyObject)win ?? s_seed;
                IntPtr hwnd = HwndOf(win);
                if (hwnd == IntPtr.Zero) return;

                ClearControls(hwnd);
                int id = 1, registered = 0, visited = 0, peers = 0, empty = 0, emptyNamed = 0,
                    roleSkip = 0, maxDepth = 0;
                var sample = new System.Text.StringBuilder();
                var emptySample = new System.Text.StringBuilder();
                Walk(root, hwnd, ref id, ref registered, ref visited, ref peers, ref empty,
                    ref emptyNamed, ref roleSkip, ref maxDepth, 0, sample, emptySample);
                s_tick++;
                Console.Error.WriteLine("[UIA] tick=" + s_tick + " hwnd=0x" + hwnd.ToString("x")
                    + " visited=" + visited + " peers=" + peers + " empty=" + empty
                    + " empty_named=" + emptyNamed + " role_skip=" + roleSkip
                    + " controls=" + registered + " maxdepth=" + maxDepth);
                if (s_tick <= 3 && registered > 0)
                    Console.Error.WriteLine("[UIA] sample=[" + sample + "]");
                if (s_tick <= 3 && emptySample.Length > 0)
                    Console.Error.WriteLine("[UIA] empty_sample=[" + emptySample + "]");
            }
            catch (Exception e)
            {
                s_disabled = true;
                Console.Error.WriteLine("[UIA] register EX " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static IntPtr HwndOf(Window win)
        {
            if (win == null) return IntPtr.Zero;
            IntPtr h = new WindowInteropHelper(win).Handle;
            if (h != IntPtr.Zero) return h;
            var src = HwndSource.FromVisual(win) as HwndSource;
            return src != null ? src.Handle : IntPtr.Zero;
        }

        private static void Walk(DependencyObject node, IntPtr hwnd,
            ref int id, ref int registered, ref int visited, ref int peers,
            ref int empty, ref int emptyNamed, ref int roleSkip, ref int maxDepth,
            int depth, System.Text.StringBuilder sample, System.Text.StringBuilder emptySample)
        {
            if (node == null || depth > 60) return;
            visited++;
            if (depth > maxDepth) maxDepth = depth;

            try
            {
                var ui = node as UIElement;
                if (ui != null)
                {
                    AutomationPeer peer = null;
                    try { peer = UIElementAutomationPeer.CreatePeerForElement(ui); }
                    catch (Exception) { peer = null; }
                    if (peer != null)
                    {
                        peers++;
                        string name = null;
                        try { name = peer.GetName(); } catch (Exception) { name = null; }

                        string role;
                        int roleId;
                        bool hasRole = RoleOf(peer, out role, out roleId);

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (hasRole) { Register(hwnd, ref id, name, role, roleId, peer, ref registered, sample); }
                            else { roleSkip++; }
                        }
                        else
                        {
                            empty++;
                            // 空名兜底：从可视子树取可见文本（例如列表行的模板 TextBlock）
                            string derived = TextOf(ui, 0);
                            if (!string.IsNullOrEmpty(derived) && hasRole)
                            {
                                emptyNamed++;
                                Register(hwnd, ref id, derived, role, roleId, peer, ref registered, sample);
                            }
                            else if (emptySample.Length < 600)
                            {
                                emptySample.Append(ui.GetType().Name)
                                    .Append('|').Append(derived ?? "-").Append(';');
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch (Exception) { count = 0; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(node, i); } catch (Exception) { }
                if (child != null)
                    Walk(child, hwnd, ref id, ref registered, ref visited, ref peers, ref empty,
                        ref emptyNamed, ref roleSkip, ref maxDepth, depth + 1, sample, emptySample);
            }
        }

        private static void Register(IntPtr hwnd, ref int id, string name, string role, int roleId,
            AutomationPeer peer, ref int registered, System.Text.StringBuilder sample)
        {
            Rect r = Rect.Empty;
            try { r = peer.GetBoundingRectangle(); } catch (Exception) { }
            RegisterControl(hwnd, id++, name, role, roleId,
                (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, 0);
            registered++;
            if (sample.Length < 700) sample.Append(role).Append(':').Append(name).Append(';');
        }

        // 只在 peer 名字为空时用：收集可视子树里的可见文本（最多 4 层、取第一段非空）
        private static string TextOf(DependencyObject node, int depth)
        {
            if (node == null || depth > 4) return null;
            var tb = node as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text)) return tb.Text;
            var acc = node as AccessText;
            if (acc != null && !string.IsNullOrEmpty(acc.Text)) return acc.Text;
            int n;
            try { n = VisualTreeHelper.GetChildrenCount(node); } catch (Exception) { return null; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = null;
                try { c = VisualTreeHelper.GetChild(node, i); } catch (Exception) { }
                string t = TextOf(c, depth + 1);
                if (!string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }

        private static bool RoleOf(AutomationPeer peer, out string role, out int roleId)
        {
            role = null;
            roleId = 0;
            AutomationControlType t;
            try { t = peer.GetAutomationControlType(); } catch (Exception) { return false; }
            switch (t)
            {
                case AutomationControlType.Button: role = "push button"; roleId = 43; return true;
                case AutomationControlType.TabItem: role = "page tab"; roleId = 37; return true;
                case AutomationControlType.Tab: role = "page tab list"; roleId = 38; return true;
                case AutomationControlType.DataItem: role = "table cell"; roleId = 56; return true;
                case AutomationControlType.ListItem: role = "list item"; roleId = 32; return true;
                case AutomationControlType.Text: role = "text"; roleId = 61; return true;
                case AutomationControlType.Window: role = "frame"; roleId = 23; return true;
                case AutomationControlType.MenuItem: role = "menu item"; roleId = 35; return true;
                case AutomationControlType.CheckBox: role = "check box"; roleId = 7; return true;
                case AutomationControlType.Edit: role = "entry"; roleId = 79; return true;
                case AutomationControlType.ToolBar: role = "tool bar"; roleId = 53; return true;
                case AutomationControlType.Tree: role = "tree"; roleId = 68; return true;
                case AutomationControlType.TreeItem: role = "tree item"; roleId = 69; return true;
                case AutomationControlType.ComboBox: role = "combo box"; roleId = 11; return true;
                case AutomationControlType.List: role = "list"; roleId = 31; return true;
                default: return false;
            }
        }
    }
}
