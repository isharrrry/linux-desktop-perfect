// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A44（CONTENT-LINEVIS-BRANCH-REACH）：托管侧"视觉/视口链逐跳"只读判别器（`[CHAIN]`）。
//
// 【射程】只打行：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_CHAIN_PROBE=0` ⇒ 整个关掉（逐字回上游行为）。

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxChainProbe
    {
        private const int TraceMax = 200000;
        private static int _n;
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_CHAIN_PROBE"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;
                }
                return _enabled == 1;
            }
        }

        internal static string N(double v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool EnvOn(string name)
        {
            try
            {
                string s = System.Environment.GetEnvironmentVariable(name);
                return (s == "0") ? false : true;      // 缺省＝开；**只有**显式 "0" 才关
            }
            catch (System.Exception)
            {
                return true;
            }
        }

        internal static string Hx(System.IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try
            {
                System.Console.Error.WriteLine(line);
                System.Console.Error.Flush();
            }
            catch (System.Exception)
            {
            }
        }

        internal static void Hit(string site, string detail)
        {
            if (!Enabled) { return; }
            if (_n >= TraceMax)
            {
                if (_n == TraceMax)
                {
                    _n++;
                    Emit("[CHAIN] site=" + site + " trace=suppressed-after-" + TraceMax);
                }
                return;
            }
            _n++;
            Emit("[CHAIN] site=" + site + " " + (detail == null ? "" : detail) + " NOINFO=chain-entry-readonly");
        }
    }
}
