// MOD-81 的 AutomationPeerAdapter + MOD-86 的 TreeSnapshotter（托管侧实现）
//
// 把活的 WPF 结构树按 IF-071 形状导出：
//   { app:{name}, window:{title,id}, env:{runner,dpi,theme}, seq, nodes:[{path,role,name,states,actions,bounds,value,relations,child_count}] }
//
// 口径（与 50-verify/a11y/snapshot.mjs 的 diff 一致）：
//   · path：按树序编号；**没有 AutomationPeer 的元素不占 path 段**（Panel/Decorator 直接下沉到子元素），
//     这样路径与《界面设计说明》元素属性表对齐（StackPanel 不出现）；
//   · role：UIA `AutomationControlType` 经 TBL-012 映射到 AT-SPI 角色名；
//   · name：`AutomationPeer.GetName()`（空名**不补默认值**，按缺陷处理，IF-051）；
//   · states：enabled/sensitive/visible/editable/focusable/focused/showing/active/!iconified；
//   · actions：click（按钮）、set-text/focus（文本）、focus（窗口）；
//   · bounds：屏幕绝对像素（DIP × DPI/96）。
//
// 诚实边界：这是 UIA 侧的树，不是 AT-SPI 总线上的对象。AT-SPI 投影（D-Bus 服务）在 MOD-81 的第二半，
// 尚未实现；读数 env 里写 runner=uia-startup-hook，不得当 AT-SPI 真桥读数使用。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ldp.A11y
{
    internal static class TreeDumper
    {
        internal static void Run(string outPath, string scr)
        {
            var t = new Thread(() => Loop(outPath, scr)) { IsBackground = true, Name = "ldp-a11y-dump" };
            t.Start();
        }

        private static readonly string DebugPath = Environment.GetEnvironmentVariable("LDP_A11Y_DEBUG") ?? "";

        private static void Debug(string msg)
        {
            if (string.IsNullOrEmpty(DebugPath)) return;
            try { File.AppendAllText(DebugPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n"); } catch { }
        }

        private static void Loop(string outPath, string scr)
        {
            Debug("loop start out=" + outPath + " scr=" + scr);
            // 最多等 60 s：Xvfb 上 WPF 从 Main 到 MainWindow.IsVisible 约 5~9 s（字体/主题初始化）。
            // 注意：`Window.IsVisible` / `VisualTreeHelper` 都是**线程亲和**的，必须在 UI 线程上读；
            // 后台线程直接读会抛 InvalidOperationException（实测）。
            for (int i = 0; i < 600; i++)
            {
                bool done = false;
                try
                {
                    Application app = Application.Current;
                    if (app == null)
                    {
                        if (i % 20 == 0) Debug("tick " + i + " app=null");
                    }
                    else
                    {
                        Dispatcher d = app.Dispatcher;
                        d.Invoke(new Action(() =>
                        {
                            Window win = app.MainWindow;
                            if (win == null || !win.IsVisible)
                            {
                                if (i % 20 == 0) Debug("tick " + i + " win=" + (win != null) + " visible=" + (win != null && win.IsVisible));
                                return;
                            }
                            InstallInputTrace(win);   // 必须在 UI 线程上注册事件处理器（跨线程会 InvalidOperationException）
                            Debug("dumping i=" + i);
                            // 快照前把窗口激活：Xvfb 无窗口管理器时焦点不由 WM 给，
                            // 不激活就读不到 active（AT-SPI 语义里 frame 应有 active）
                            try { win.Activate(); } catch { }
                            Dump(win, outPath, scr, app);
                            Debug("dumped");
                            // 事件回放判据要在输入后重新读数：LDP_A11Y_REPEAT=1 时每 400 ms 重写同一文件
                            if (Environment.GetEnvironmentVariable("LDP_A11Y_REPEAT") == "1")
                            {
                                StartRepeat(win, outPath, scr, app);
                            }
                            done = true;
                        }), DispatcherPriority.Background);
                    }
                }
                catch (Exception e)
                {
                    if (i % 20 == 0) Debug("tick " + i + " exception " + e.GetType().Name + ": " + e.Message);
                }
                if (done) return;
                Thread.Sleep(100);
            }
            try
            {
                File.WriteAllText(outPath, "{\"error\":\"timeout: MainWindow 未在 60 s 内出现\"}");
            }
            catch
            {
            }
        }

        private static bool g_repeatStarted;
        private static bool g_inputTraceInstalled;

        // 输入链追踪（仅 LDP_A11Y_DEBUG 设置时）：把 WPF 侧真正看到的鼠标/键盘输入记进 debug 文件。
        // 用途：判据「点击后控件不聚焦」要能区分「WPF 根本没收到鼠标事件」与「收到了但命中测试/焦点没落对」。
        private static void InstallInputTrace(Window win)
        {
            if (g_inputTraceInstalled || string.IsNullOrEmpty(DebugPath)) return;
            g_inputTraceInstalled = true;
            try
            {
                // 路由事件层追踪：比 InputManager.PreProcessInput 更靠后、更接近控件实际收到的事件，
                // 用来判定「事件到窗口了但命中的是别的元素」还是「根本没到控件」。
                win.PreviewMouseDown += (s, e) => Debug("Routed MouseDown pos=" + e.GetPosition(win) + " src=" + (e.OriginalSource == null ? "null" : e.OriginalSource.GetType().Name) + " btn=" + e.ChangedButton + " state=" + e.ButtonState + " left=" + Mouse.LeftButton + " active=" + win.IsActive + " stylus=" + (e.StylusDevice == null ? "null" : e.StylusDevice.Name) + " devActive=" + MouseDeviceActive() + " ksLB=" + ((ushort)NativeGetKeyState(0x01)).ToString("x4") + " handled=" + e.Handled);
                win.PreviewMouseUp += (s, e) => Debug("Routed MouseUp pos=" + e.GetPosition(win) + " src=" + (e.OriginalSource == null ? "null" : e.OriginalSource.GetType().Name));
                win.PreviewMouseMove += (s, e) => Debug("Routed MouseMove pos=" + e.GetPosition(win));
            }
            catch { }
            try
            {
                InputManager.Current.PreProcessInput += (s, e) =>
                {
                    try
                    {
                        InputEventArgs input = e.StagingItem == null ? null : e.StagingItem.Input;
                        if (input is MouseButtonEventArgs mb)
                        {
                            Debug("INPUT " + mb.RoutedEvent.Name + " button=" + mb.ChangedButton + " pos=" + mb.GetPosition(null));
                        }
                        else if (input is MouseEventArgs mv)
                        {
                            Debug("INPUT " + mv.RoutedEvent.Name + " pos=" + mv.GetPosition(null));
                        }
                        else if (input is KeyboardFocusChangedEventArgs kf)
                        {
                            Debug("INPUT Focus " + kf.RoutedEvent.Name + " new=" + (kf.NewFocus == null ? "null" : kf.NewFocus.GetType().Name));
                        }
                    }
                    catch { }
                };
            }
            catch { }
        }

        // 重复导出：后台线程每 400 ms 在 UI 线程上重写同一个 JSON（seq 递增）。
        // 用途 = 事件回放判据（ACC-010）：输入前取一次、输入后再取一次，比 value 字段。
        private static void StartRepeat(Window win, string outPath, string scr, Application app)
        {
            if (g_repeatStarted) return;
            g_repeatStarted = true;
            Dispatcher d = app.Dispatcher;
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(400);
                    try
                    {
                        d.Invoke(new Action(() =>
                        {
                            if (win.IsVisible) Dump(win, outPath, scr, app);
                        }), DispatcherPriority.Background);
                    }
                    catch
                    {
                        return; // 应用在退出：停止重复
                    }
                }
            })
            { IsBackground = true, Name = "ldp-a11y-repeat" };
            t.Start();
        }

        private static int g_seq;

        private static void Dump(Window win, string outPath, string scr, Application app)
        {
            var nodes = new List<Node>();
            // 根节点 = 窗口本身（path "0"，AT-SPI 的 frame）；其子节点从 "0/0" 起
            var winPeer = UIElementAutomationPeer.CreatePeerForElement(win) ?? new WindowAutomationPeer(win);
            var root = BuildNode(win, winPeer, "0", win);
            nodes.Add(root);
            Walk(win, "0", nodes);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"app\": {\"name\": ").Append(J(win.Title)).Append("},\n");
            sb.Append("  \"window\": {\"title\": ").Append(J(win.Title)).Append(", \"scr\": ").Append(J(scr)).Append("},\n");
            sb.Append("  \"env\": {\"runner\": \"uia-startup-hook\", \"dpi\": ").Append(DpiOf(win).ToString(CultureInfo.InvariantCulture))
              .Append(", \"theme\": ").Append(J(ThemeInfo(app)))
              .Append(", \"wm\": ").Append(win.IsActive ? "true" : "false")
              .Append(", \"note\": \"UIA 树（AutomationPeer），非 AT-SPI 总线\"},\n");
            sb.Append("  \"seq\": ").Append(++g_seq).Append(",\n");
            // child_count：按 path 段前缀统计（与 50-verify/a11y/snapshot.mjs 的 childCounts 同口径）
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Node n in nodes) counts[n.Path] = 0;
            foreach (Node n in nodes)
            {
                int slash = n.Path.LastIndexOf('/');
                if (slash <= 0) continue;
                string parent = n.Path.Substring(0, slash);
                if (counts.ContainsKey(parent)) counts[parent] = counts[parent] + 1;
            }
            foreach (Node n in nodes) n.ChildCount = counts[n.Path];
            sb.Append("  \"nodes\": [");
            for (int i = 0; i < nodes.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append(nodes[i].ToJson());
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            GC.KeepAlive(app);
        }

        // 树序遍历：有 peer 的元素占一个 path；没有 peer 的下沉（子元素接续父 path 段）
        private static void Walk(DependencyObject root, string path, List<Node> outNodes)
        {
            int childIndex = 0;
            foreach (DependencyObject child in VisualChildren(root))
            {
                var el = child as UIElement;
                // Separator 是纯装饰：AT-SPI 侧没有对应角色，且《界面设计说明》的元素属性表里也不列它。
                if (el is Separator) continue;
                AutomationPeer peer = el != null ? PeerFor(el) : null;
                if (peer != null)
                {
                    string childPath = path + "/" + childIndex.ToString(CultureInfo.InvariantCulture);
                    childIndex++;
                    outNodes.Add(BuildNode(el, peer, childPath, child));
                    // 只对「容器」继续递归：控件（Button/TextBox 等）的内部视觉件（ContentPresenter/TextBlock/
                    // ScrollViewer）不是独立 accessible 对象，递归会让路径与元素属性表错位。
                    if (IsContainer(el)) Walk(child, childPath, outNodes);
                }
                else
                {
                    Walk(child, path, outNodes);
                }
            }
        }

        private static bool IsContainer(UIElement el)
        {
            if (el is Window) return true;
            if (el is Canvas) return true;
            if (el is StatusBar) return true;
            if (el is Panel) return true;
            return false;
        }

        private static AutomationPeer PeerFor(UIElement el)
        {
            try
            {
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(el);
                if (peer != null) return peer;
                // Canvas 这类没有默认 peer 的元素：按属性表口径要投影成 drawing area
                if (el is Canvas) return new UIElementAutomationPeer(el);
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<DependencyObject> VisualChildren(DependencyObject d)
        {
            int n = 0;
            try
            {
                n = VisualTreeHelper.GetChildrenCount(d);
            }
            catch
            {
                yield break;
            }
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = null;
                try
                {
                    c = VisualTreeHelper.GetChild(d, i);
                }
                catch
                {
                }
                if (c != null) yield return c;
            }
        }

        private static double DpiOf(Visual v)
        {
            try
            {
                return VisualTreeHelper.GetDpi(v).PixelsPerInchX;
            }
            catch
            {
                return 96.0;
            }
        }

        private static Node BuildNode(UIElement el, AutomationPeer peer, string path, DependencyObject visual)
        {
            var n = new Node { Path = path };
            n.Role = RoleOf(peer, el);
            try
            {
                n.Name = peer.GetName() ?? string.Empty;
            }
            catch
            {
                n.Name = string.Empty;
            }

            var states = new List<string>();
            var actions = new List<string>();
            var fe = el as FrameworkElement;
            var ctl = el as Control;

            bool enabled = fe == null || fe.IsEnabled;
            bool visible = fe == null || fe.IsVisible;

            if (el is Window w)
            {
                if (visible) states.Add("showing");
                if (w.IsActive) states.Add("active");
                if (w.WindowState == WindowState.Minimized) states.Add("iconified");
                else states.Add("!iconified");
                actions.Add("focus");
            }
            else if (el is TextBox tb)
            {
                if (enabled) { states.Add("enabled"); states.Add("sensitive"); }
                else { states.Add("!enabled"); states.Add("!sensitive"); }
                if (!tb.IsReadOnly) states.Add("editable");
                if (tb.Focusable) states.Add("focusable");
                if (visible) states.Add("visible");
                actions.Add("set-text");
                actions.Add("focus");
            }
            else if (el is ButtonBase b)
            {
                if (enabled) { states.Add("enabled"); states.Add("sensitive"); }
                else { states.Add("!enabled"); states.Add("!sensitive"); }
                if (visible) states.Add("visible");
                if (enabled) actions.Add("click");
            }
            else
            {
                if (visible) states.Add("visible");
                if (ctl != null && !ctl.IsEnabled) states.Add("!enabled");
            }
            // 焦点是**跨控件通用**的状态（AT-SPI 里就是 FOCUSED），不只在窗口分支里判
            bool focused = false;
            try { focused = el.IsKeyboardFocused; } catch { }
            if (focused && !states.Contains("focused")) states.Add("focused");
            n.States = states;
            n.Actions = actions;

            n.Value = el is TextBox t ? t.Text : null;
            n.Relations = new List<string>();
            n.ChildCount = 0; // 由写 JSON 时按 path 统计补齐（与 snapshot.mjs 的 childCounts 同口径）

            try
            {
                var v = (Visual)el;
                double dpi = DpiOf(v) / 96.0;
                Point p = v.PointToScreen(new Point(0, 0));
                n.Bounds = new[] { (int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(fe != null ? fe.ActualWidth * dpi : 0), (int)Math.Round(fe != null ? fe.ActualHeight * dpi : 0) };
            }
            catch
            {
                n.Bounds = new[] { 0, 0, 0, 0 };
            }
            GC.KeepAlive(visual);

            // 状态位排序：与期望文件对齐（enabled, sensitive, visible, editable, focusable）
            states.Sort((a, b) => string.CompareOrdinal(a, b));
            return n;
        }

        // TBL-012 的 UIA ControlType → AT-SPI role 映射（子集；未列出的按 unknown 记，不猜）
        private static string RoleOf(AutomationPeer peer, UIElement el)
        {
            if (el is Canvas) return "drawing area";
            if (el is Window) return "frame";
            if (el is StatusBar) return "status bar";
            AutomationControlType ct;
            try
            {
                ct = peer.GetAutomationControlType();
            }
            catch
            {
                return "unknown";
            }
            switch (ct)
            {
                case AutomationControlType.Button: return "push button";
                case AutomationControlType.Edit: return "entry";
                case AutomationControlType.Text: return "label";
                case AutomationControlType.Window: return "frame";
                case AutomationControlType.Pane: return "panel";
                case AutomationControlType.StatusBar: return "status bar";
                case AutomationControlType.CheckBox: return "check box";
                case AutomationControlType.RadioButton: return "radio button";
                case AutomationControlType.ComboBox: return "combo box";
                case AutomationControlType.List: return "list";
                case AutomationControlType.ListItem: return "list item";
                case AutomationControlType.Menu: return "menu";
                case AutomationControlType.MenuBar: return "menu bar";
                case AutomationControlType.MenuItem: return "menu item";
                case AutomationControlType.ProgressBar: return "progress bar";
                case AutomationControlType.ScrollBar: return "scroll bar";
                case AutomationControlType.Slider: return "slider";
                case AutomationControlType.Spinner: return "spin button";
                case AutomationControlType.Tab: return "page tab list";
                case AutomationControlType.TabItem: return "page tab";
                case AutomationControlType.Table: return "table";
                case AutomationControlType.DataGrid: return "table";
                case AutomationControlType.Image: return "image";
                case AutomationControlType.Hyperlink: return "link";
                case AutomationControlType.ToolBar: return "tool bar";
                case AutomationControlType.ToolTip: return "tool tip";
                case AutomationControlType.Tree: return "tree";
                case AutomationControlType.TreeItem: return "tree item";
                case AutomationControlType.Group: return "panel";
                default: return "unknown";
            }
        }

        // 主题字典的真实状态：加载了哪一本、有多少条目。ACC-020 的结构判据靠这行区分
        //   「--theme 没生效（字典 0 本）」与「字典加载了但控件模板没换」。
        private static string ThemeInfo(Application app)
        {
            try
            {
                var md = app.Resources.MergedDictionaries;   // 类型是 internal 的 ResourceDictionaryCollection：用 var
                if (md == null || md.Count == 0) return "(none)";
                var sb = new StringBuilder();
                for (int i = 0; i < md.Count; i++)
                {
                    ResourceDictionary d = md[i];
                    if (i > 0) sb.Append("; ");
                    sb.Append(d.Source == null ? "(inline)" : d.Source.OriginalString);
                    sb.Append("#").Append(d.Count.ToString(CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "(error: " + e.GetType().Name + ")";
            }
        }

        // MouseDevice.IsActive 是 internal（= _inputSource != null）：反射读，用于区分
        //   「事件到了但 MouseDevice 认为没激活」与「激活了但按钮态没更新」。
        [DllImport("user32.dll", EntryPoint = "GetKeyState")]
        private static extern short NativeGetKeyState(int vk);

        private static string MouseDeviceActive()
        {
            try
            {
                InputDevice dev = InputManager.Current.PrimaryMouseDevice;
                if (dev == null) return "nodev";
                PropertyInfo pi = dev.GetType().GetProperty("IsActive", BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi == null) return "noprop";
                return ((bool)pi.GetValue(dev)) ? "true" : "false";
            }
            catch (Exception e)
            {
                return "(err:" + e.GetType().Name + ")";
            }
        }

        private static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private sealed class Node
        {
            public string Path;
            public string Role;
            public string Name;
            public List<string> States;
            public List<string> Actions;
            public string Value;
            public List<string> Relations;
            public int[] Bounds;
            public int ChildCount;

            public string ToJson()
            {
                var sb = new StringBuilder("    {");
                sb.Append("\"path\": ").Append(J(Path));
                sb.Append(", \"role\": ").Append(J(Role));
                sb.Append(", \"name\": ").Append(J(Name));
                sb.Append(", \"states\": [");
                for (int i = 0; i < States.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(J(States[i]));
                sb.Append("]");
                sb.Append(", \"actions\": [");
                for (int i = 0; i < Actions.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(J(Actions[i]));
                sb.Append("]");
                sb.Append(", \"bounds\": [").Append(Bounds[0]).Append(", ").Append(Bounds[1]).Append(", ").Append(Bounds[2]).Append(", ").Append(Bounds[3]).Append("]");
                sb.Append(", \"value\": ").Append(Value == null ? "null" : J(Value));
                sb.Append(", \"relations\": []");
                sb.Append(", \"child_count\": ").Append(ChildCount);
                sb.Append("}");
                return sb.ToString();
            }
        }
    }
}
