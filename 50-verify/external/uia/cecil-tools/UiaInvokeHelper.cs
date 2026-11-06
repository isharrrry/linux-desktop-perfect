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
                Console.WriteLine("[INV] try#" + Tried + " " + name);
                var wrapper = _wrap.Invoke(null, new object[] { peer, pattern });
                if (wrapper == null) { Console.WriteLine("[INV]  wrapper null"); return false; }
                _invoke.Invoke(wrapper, null);
                Succeeded++;
                Console.WriteLine("[INV] INVOKED ok " + name + " (累计成功 " + Succeeded + ")");
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
    }
}
