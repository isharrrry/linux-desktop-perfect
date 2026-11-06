// UiaInvokeHelper —— 用反射调 internal 的 InvokeProviderWrapper（K-1042 的 C# 版重试）
// 不手写 IL：反射在 C# 源码里是标准做法，避开了之前 InvalidProgramException 的坑。
using System;
using System.Reflection;
using System.Windows.Automation.Peers;

namespace UiaInvokeHelper
{
    public static class Helper
    {
        static MethodInfo _wrap, _invoke;
        static bool _init;

        static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                var t = Type.GetType("MS.Internal.Automation.InvokeProviderWrapper, PresentationCore");
                if (t == null) { Console.WriteLine("[INV] wrapper type NOT FOUND"); return; }
                _wrap = t.GetMethod("Wrap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _invoke = t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                Console.WriteLine("[INV] wrapper type=" + t.FullName + " wrap=" + (_wrap != null) + " invoke=" + (_invoke != null));
            }
            catch (Exception e) { Console.WriteLine("[INV] init fail: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>对支持 Invoke 的 peer 触发一次 Invoke。返回 true 表示已调用。</summary>
        public static bool TryInvoke(AutomationPeer peer)
        {
            if (peer == null) return false;
            Init();
            if (_wrap == null || _invoke == null) return false;
            try
            {
                var pattern = peer.GetPattern(PatternInterface.Invoke);
                if (pattern == null) return false;
                var wrapper = _wrap.Invoke(null, new object[] { peer, pattern });
                if (wrapper == null) { Console.WriteLine("[INV] wrapper null"); return false; }
                _invoke.Invoke(wrapper, null);
                Console.WriteLine("[INV] INVOKED ok peer=" + peer.GetType().Name);
                return true;
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Console.WriteLine("[INV] fail: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }
    }
}
