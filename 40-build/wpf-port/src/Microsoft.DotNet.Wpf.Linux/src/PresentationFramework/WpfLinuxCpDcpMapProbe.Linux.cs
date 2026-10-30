// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A70（契约 C4 · cp↔dcp 偏移由宿主给定）：托管侧"宿主"侧映射交付器 ＋ 逐点对拍（[CPDCMAP]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_CPDCMAP_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxCpDcpMapProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxPts   = 16;                 // 单段最多 16 个样本点
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_CPDCMAP_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapFeed", ExactSpelling = true)]
        private static extern int PtsCpDcpMapFeed(IntPtr parah, int cpBase, int n, int[] cp, int[] dcp);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapFind", ExactSpelling = true)]
        private static extern int PtsCpDcpMapFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapNPoints", ExactSpelling = true)]
        private static extern int PtsCpDcpMapNPoints(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapCpBase", ExactSpelling = true)]
        private static extern int PtsCpDcpMapCpBase(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapOffset", ExactSpelling = true)]
        private static extern int PtsCpDcpMapOffset(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapState", ExactSpelling = true)]
        private static extern int PtsCpDcpMapState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapCpAt", ExactSpelling = true)]
        private static extern int PtsCpDcpMapCpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsCpDcpMapDcpAt", ExactSpelling = true)]
        private static extern int PtsCpDcpMapDcpAt(int k, int i);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 把**本段落的 (cp,dcp) 偏移映射样本对**交给 native（一次／段），随即回读并逐点对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int cpBase = 0;
            try
            {
                TextElement te = para.Element as TextElement;
                if (te == null) { Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                cpBase = para.ParagraphStartCharacterPosition;   // 段落起始字符位置（宿主真值；端点锚）
                int len = text.Length;
                int n = (len < MaxPts) ? len : MaxPts;            // 连续前 n 个字符作样本点
                int[] cp  = new int[n];
                int[] dcp = new int[n];
                for (int i = 0; i < n; i++)
                {
                    cp[i]  = cpBase + i;
                    dcp[i] = cp[i];   // 本移植链上无 LS 隐藏文本 ⇒ 显示偏移真值 ＝ 0（如实声明，不伪造）
                }

                int rc = PtsCpDcpMapFeed(parah, cpBase, n, (n > 0) ? cp : null, (n > 0) ? dcp : null);
                _feeds++;
                int k = PtsCpDcpMapFind(parah);
                int mism = 0;
                if (k < 0) { mism = -1; }
                else if (PtsCpDcpMapNPoints(k) != n) { mism = -1; }       // 点数不符 ⇒ 必红
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (PtsCpDcpMapCpAt(k, i)  != cp[i])  { mism++; }
                        if (PtsCpDcpMapDcpAt(k, i) != dcp[i]) { mism++; }
                    }
                }

                Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " cp_base=" + cpBase + " n=" + n
                     + " rc=" + rc + " slot=" + k + " mism=" + mism
                     + " pair_match=" + ((k >= 0 && mism == 0) ? 1 : 0)
                     + " off=" + ((k >= 0) ? PtsCpDcpMapOffset(k) : -999)
                     + " state=" + ((k >= 0) ? PtsCpDcpMapState(k) : -1)
                     + " v=CP-DCP-MAP-INBOUND");
            }
            catch (System.Exception e)
            {
                Emit("[CPDCMAP] mgd parah=" + Hx(parah) + " cp_base=" + cpBase + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
