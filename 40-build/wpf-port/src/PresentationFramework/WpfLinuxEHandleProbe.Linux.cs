// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// `T-B17`（`PRECOND-TAB3-EHANDLE-CALLSITE`）：`E_HANDLE`(`0x80070006`) 抛点的**只读**捕获器。
//
// 【它答的问题】`T-B16` 现取：闸开腿上 `[HC-UNHANDLED]` **恒 +1 条** `COMException … E_HANDLE`，
//   首帧只能拿到 `MS.Internal.HRESULT.Check(Int32 hr)`（那是**抛点**，不是**调用点**）⇒
//   "谁把 `hr=0x80070006` 交给 `Check`"**没有直读面**。本件用
//   `AppDomain.CurrentDomain.FirstChanceException`（**首次异常**，比 `DispatcherUnhandledException`
//   早、且**不改变**异常是否被处理）只读地把**当时的栈**打出来 ⇒ 调用点具名。
//
// 【不扰动的保证】① 缺省**全关**（`WPF_EHANDLE_PROBE=1` 才装钩子）；② 钩子**只读**（不 rethrow、
//   不改 `Handled`、不吞异常）；③ 输出**有界**（≤8 条 × 栈 ≤28 层）；④ 只写 stderr、内部 try/catch 兜底。

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS.Internal.Documents
{
    internal static class WpfLinuxEHandleProbe
    {
        private const int EHandle = unchecked((int)0x80070006);
        private const int MaxHits = 8;
        private const int MaxFrames = 28;

        private static int _on = -1;
        private static int _hits;

        private static bool On
        {
            get
            {
                if (_on < 0)
                {
                    string s = null;
                    try { s = Environment.GetEnvironmentVariable("WPF_EHANDLE_PROBE"); }
                    catch (Exception) { s = null; }
                    _on = (s == "1") ? 1 : 0;
                }
                return _on == 1;
            }
        }

#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void Install()
        {
            if (!On) { return; }
            try { AppDomain.CurrentDomain.FirstChanceException += OnFirstChance; }
            catch (Exception) { }
        }
#pragma warning restore CA2255

        private static void OnFirstChance(object sender,
            System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            try
            {
                COMException ce = e.Exception as COMException;
                if (ce == null || ce.HResult != EHandle) { return; }
                if (_hits >= MaxHits) { return; }
                _hits++;

                string st = null;
                try { st = ce.StackTrace; } catch (Exception) { st = null; }
                if (string.IsNullOrEmpty(st))
                {
                    try { st = Environment.StackTrace; } catch (Exception) { st = "<no-stack>"; }
                }
                string[] lines = st.Split('\n');
                var sb = new System.Text.StringBuilder();
                sb.Append("[EHANDLE] #").Append(_hits).Append(" msg=").Append(ce.Message);
                for (int i = 0; i < lines.Length && i < MaxFrames; i++)
                {
                    sb.Append("\n[EHANDLE]   ").Append(lines[i].Trim());
                }
                Console.Error.WriteLine(sb.ToString());
                Console.Error.Flush();
            }
            catch (Exception) { }
        }
    }
}
