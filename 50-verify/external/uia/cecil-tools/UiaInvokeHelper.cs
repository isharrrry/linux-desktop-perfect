// UiaInvokeHelper —— 用反射调 internal 的 InvokeProviderWrapper（K-1111 验证可行）
// 【教训】K-1042：手写 IL 做反射会 InvalidProgramException；改用「C# 编译成 DLL + Cecil 注入调用」。
// 【教训】K-1112：AutomationPeer.IsEnabled() 会硬崩溃，不要用；靠 try/catch + 继续遍历。
using System;
using System.Reflection;
using System.Windows.Automation.Peers;

namespace UiaInvokeHelper
{
    public static class Helper
    {
        static MethodInfo _wrap, _invoke;
        static bool _init;
        public static int Tried, Succeeded;

        static void Init()
        {
            if (_init) return;
            _init = true;
            var t = Type.GetType("MS.Internal.Automation.InvokeProviderWrapper, PresentationCore");
            if (t == null) { Console.WriteLine("[INV] wrapper type NOT FOUND"); return; }
            _wrap = t.GetMethod("Wrap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _invoke = t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                  null, Type.EmptyTypes, null);
            Console.WriteLine("[INV] wrapper=" + t.Name + " wrap=" + (_wrap != null) + " invoke=" + (_invoke != null));
        }

        /// <summary>对支持 Invoke 的 peer 触发一次 Invoke。返回 true 表示已成功调用。</summary>
        public static bool TryInvoke(AutomationPeer peer)
        {
            if (peer == null) return false;
            Init();
            if (_wrap == null || _invoke == null) return false;
            try
            {
                var pattern = peer.GetPattern(PatternInterface.Invoke);
                if (pattern == null) return false;
                string name = peer.GetType().Name;
                Tried++;
                string n2 = "(?)"; try { n2 = peer.GetName() ?? "(null)"; } catch { }
                Console.WriteLine("[INV] try#" + Tried + " " + name + " Name=" + n2);
                var wrapper = _wrap.Invoke(null, new object[] { peer, pattern });
                if (wrapper == null) { Console.WriteLine("[INV]  wrapper null"); return false; }
                _invoke.Invoke(wrapper, null);
                Succeeded++;
                Console.WriteLine("[INV] INVOKED ok " + name + " (累计成功 " + Succeeded + ")");
                try { Console.WriteLine("[INV]   Name=" + (peer.GetName() ?? "(null)") + " Id=" + (peer.GetAutomationId() ?? "(null)")); }
                catch (Exception e2) { Console.WriteLine("[INV]   (读 Name 失败: " + e2.GetType().Name + ")"); }
                return true;
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Console.WriteLine("[INV]  reject " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }

        /// <summary>跳过导航类容器，优先找 Button/MenuItem（它们才真正支持 Invoke）。</summary>
        public static bool LooksInvokable(AutomationPeer peer)
        {
            if (peer == null) return false;
            var n = peer.GetType().Name;
            return n.IndexOf("Button", StringComparison.Ordinal) >= 0
                || n.IndexOf("MenuItem", StringComparison.Ordinal) >= 0
                || n.IndexOf("Toggle", StringComparison.Ordinal) >= 0;
        }

        /// <summary>检查 ButtonBase 的 Command 是否可执行（K-1146）。</summary>
        public static void CheckCommand(object button)
        {
            if (button == null) return;
            try
            {
                var t = button.GetType();
                var cmdProp = t.GetProperty("Command", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (cmdProp == null) { Console.WriteLine("[CMD] 无 Command 属性"); return; }
                var cmd = cmdProp.GetValue(button);
                if (cmd == null) { Console.WriteLine("[CMD] Command = null"); return; }
                var can = cmd.GetType().GetMethod("CanExecute", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                var exec = cmd.GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                string canR = "(?)";
                try { canR = can == null ? "(no CanExecute)" : (can.Invoke(cmd, new object[] { null })?.ToString() ?? "(null)"); } catch (Exception e) { canR = "throw:" + e.GetType().Name; }
                Console.WriteLine("[CMD] " + t.Name + " Command=" + cmd.GetType().Name + " CanExecute=" + canR);
            }
            catch (Exception e) { Console.WriteLine("[CMD] 失败: " + e.GetType().Name + ": " + e.Message); }
        }


        /// <summary>反射调用无参方法（K-1118/K-1148：AddRemove 等受限方法）。</summary>
        public static bool CallNoArg(object target, string methodName)
        {
            if (target == null) return false;
            try
            {
                var m = target.GetType().GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null) { Console.WriteLine("[CALL] method NOT FOUND: " + methodName); return false; }
                Console.WriteLine("[CALL] invoke " + target.GetType().Name + "." + methodName + " via reflection");
                m.Invoke(target, null);
                Console.WriteLine("[CALL] OK " + methodName);
                return true;
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Console.WriteLine("[CALL] fail " + methodName + ": " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }

        /// <summary>用 ExpandCollapse pattern 展开（MenuItem 等）。</summary>
        public static bool TryExpand(AutomationPeer peer)
        {
            if (peer == null) return false;
            try
            {
                var p = peer.GetPattern(PatternInterface.ExpandCollapse);
                if (p == null) return false;
                var prov = p as System.Windows.Automation.Provider.IExpandCollapseProvider;
                if (prov == null) return false;
                prov.Expand();
                return true;
            }
            catch (Exception e) { Console.WriteLine("[EXP] reject " + e.GetType().Name); return false; }
        }


        // ── K-1162（T3 第④步）：把 action 传给 shim，并注册回调 ──────────────────
        [System.Runtime.InteropServices.DllImport("libwpfwin32.so", EntryPoint = "wpf_atspi_set_action")]
        static extern void shim_set_action(ulong hwnd, string name, string desc);
        [System.Runtime.InteropServices.DllImport("libwpfwin32.so", EntryPoint = "wpf_atspi_set_action_invoker")]
        static extern void shim_set_invoker(IntPtr fn);

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        delegate void ActionInvokerDelegate(ulong hwnd);

        static ActionInvokerDelegate _invoker;
        static System.Collections.Generic.Dictionary<ulong, AutomationPeer> _peersByHwnd
            = new System.Collections.Generic.Dictionary<ulong, AutomationPeer>();

        static void OnActionInvoked(ulong hwnd)
        {
            try
            {
                Console.WriteLine("[ATSPI-ACT] invoker 被调用 hwnd=0x" + hwnd.ToString("x"));
                AutomationPeer peer;
                if (_peersByHwnd.TryGetValue(hwnd, out peer) && peer != null)
                {
                    Console.WriteLine("[ATSPI-ACT] 调 TryInvoke");
                    TryInvoke(peer);
                }
                else Console.WriteLine("[ATSPI-ACT] 没有 hwnd 对应的 peer");
            }
            catch (Exception e) { Console.WriteLine("[ATSPI-ACT] invoker 异常: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>给指定 hwnd 设置 action="Invoke"，并注册全局 invoker + 记住 peer。</summary>
        public static void SetupAtspiAction(ulong hwnd, AutomationPeer peer)
        {
            try
            {
                if (peer == null) { Console.WriteLine("[ATSPI-ACT] peer null，跳过"); return; }
                _peersByHwnd[hwnd] = peer;
                if (_invoker == null)
                {
                    _invoker = OnActionInvoked;
                    IntPtr fp = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_invoker);
                    shim_set_invoker(fp);
                    Console.WriteLine("[ATSPI-ACT] invoker 已注册");
                }
                shim_set_action(hwnd, "Invoke", "按下按钮");
                Console.WriteLine("[ATSPI-ACT] set_action(hwnd=0x" + hwnd.ToString("x") + ", Invoke)");
            }
            catch (Exception e) { Console.WriteLine("[ATSPI-ACT] Setup 失败: " + e.GetType().Name + ": " + e.Message); }
        }


        /// <summary>K-1165：只调 set_action，不注册回调（隔离 K-1164 的崩溃）。</summary>
        public static void SetupAtspiActionNoInvoker(ulong hwnd, AutomationPeer peer)
        {
            try
            {
                Console.WriteLine("[ATSPI-ACT2] enter hwnd=0x" + hwnd.ToString("x"));
                if (peer != null) _peersByHwnd[hwnd] = peer;
                Console.WriteLine("[ATSPI-ACT2] peer 存好，准备调 set_action");
                shim_set_action(hwnd, "Invoke", "按下按钮");
                Console.WriteLine("[ATSPI-ACT2] set_action OK");
            }
            catch (Exception e) { Console.WriteLine("[ATSPI-ACT2] 失败: " + e.GetType().Name + ": " + e.Message); }
        }


        /// <summary>K-1167：打印控件的 Text/Content/Header 属性（供验证状态栏文字）。</summary>
        public static void DumpText(object el)
        {
            if (el == null) return;
            try
            {
                var t = el.GetType();
                string tn = t.Name;
                string val = null;
                foreach (var name in new[] { "Text", "Content", "Header", "Title" })
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || p.PropertyType != typeof(string)) continue;
                    var v = p.GetValue(el) as string;
                    if (!string.IsNullOrEmpty(v)) { val = v; break; }
                }
                if (val != null) { Console.WriteLine("[TEXT] " + tn + " = \"" + val + "\""); }
                // K-1206：如果这是 MenuItem 且有子项 ⇒ 顺便展开（零 IL 风险）
                var mi = el as System.Windows.Controls.MenuItem;
                if (mi != null && mi.Items.Count > 0 && !mi.IsSubmenuOpen) {
                    try { mi.IsSubmenuOpen = true; Console.WriteLine("[MENU3] 展开: Header=" + (mi.Header ?? "(null)")); }
                    catch (Exception me) { Console.WriteLine("[MENU3] 展开失败: " + me.GetType().Name); }
                }
                if (val != null) return;
                // K-1169：Content 不是 string ⇒ 递归读它的 Text/Content/Header
                var cp = t.GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
                if (cp != null) {
                    var content = cp.GetValue(el);
                    if (content != null) {
                        var ct = content.GetType();
                        foreach (var inner in new[] { "Text", "Content", "Header" }) {
                            var ip = ct.GetProperty(inner, BindingFlags.Public | BindingFlags.Instance);
                            if (ip == null || ip.PropertyType != typeof(string)) continue;
                            var iv = ip.GetValue(content) as string;
                            if (!string.IsNullOrEmpty(iv)) { Console.WriteLine("[TEXT] " + tn + ".Content->" + ct.Name + "." + inner + " = \"" + iv + "\""); return; }
                        }
                    }
                }
            }
            catch { }
        }


        /// <summary>K-1204：递归找第一个 MenuItem 并展开（IsSubmenuOpen = true）。</summary>
        public static void OpenMenu(object root)
        {
            try
            {
                var found = FindMenuItem(root as System.Windows.DependencyObject);
                if (found == null) { Console.WriteLine("[MENU2] 没找到 MenuItem"); return; }
                found.IsSubmenuOpen = true;
                Console.WriteLine("[MENU2] 展开成功: Header=" + (found.Header ?? "(null)"));
            }
            catch (Exception e) { Console.WriteLine("[MENU2] 失败: " + e.GetType().Name + ": " + e.Message); }
        }

        static System.Windows.Controls.MenuItem FindMenuItem(System.Windows.DependencyObject d)
        {
            if (d == null) return null;
            var mi = d as System.Windows.Controls.MenuItem;
            if (mi != null && mi.Items.Count > 0) return mi;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var r = FindMenuItem(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                if (r != null) return r;
            }
            return null;
        }

    }
}