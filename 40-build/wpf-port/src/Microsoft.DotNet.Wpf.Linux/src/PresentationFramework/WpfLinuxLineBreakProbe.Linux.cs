// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A73（PRECOND-NO-LINE-BREAKER · 行断器）：托管侧触发器 ＋ 逐格回读对拍（[LINEBREAK]）。
//
// 【射程】只触发 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_LINEBREAK_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxLineBreakProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多触发 64 段
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
                    try { s = System.Environment.GetEnvironmentVariable("WPF_LINEBREAK_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFeed", ExactSpelling = true)]
        private static extern int PtsLineBreakFeed(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFind", ExactSpelling = true)]
        private static extern int PtsLineBreakFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakState", ExactSpelling = true)]
        private static extern int PtsLineBreakState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakSeq", ExactSpelling = true)]
        private static extern int PtsLineBreakSeq(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpFirst", ExactSpelling = true)]
        private static extern int PtsLineBreakCpFirst(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpLim", ExactSpelling = true)]
        private static extern int PtsLineBreakCpLim(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCch", ExactSpelling = true)]
        private static extern int PtsLineBreakCch(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakNHard", ExactSpelling = true)]
        private static extern int PtsLineBreakNHard(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakNBrk", ExactSpelling = true)]
        private static extern int PtsLineBreakNBrk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakMono", ExactSpelling = true)]
        private static extern int PtsLineBreakMono(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakFirstOk", ExactSpelling = true)]
        private static extern int PtsLineBreakFirstOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCoverOk", ExactSpelling = true)]
        private static extern int PtsLineBreakCoverOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakDcpAt", ExactSpelling = true)]
        private static extern int PtsLineBreakDcpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakCpAt", ExactSpelling = true)]
        private static extern int PtsLineBreakCpAt(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLineBreakInfo", ExactSpelling = true)]
        private static extern int PtsLineBreakInfo(int k, int i, int field);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 按**段身份**触发 native 的行断点台账收口（一次／段），随即回读并逐格对拍。
        internal static void FeedParagraph(IntPtr parah)
        {
            if (!Enabled || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            try
            {
                int rc = PtsLineBreakFeed(parah);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**不在链上**／无内容源／无行台账／断点不符）：**不是**正读数 ⇒ 单列一行。
                    Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " rc=" + rc + " v=REJECT-LINE-BREAKER");
                    return;
                }
                int k  = PtsLineBreakFind(parah);
                int nb = (k >= 0) ? PtsLineBreakNBrk(k) : -1;
                int nh = (k >= 0) ? PtsLineBreakNHard(k) : -1;
                int mism = 0;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsLineBreakState(k)   != 2) { mism++; }     // 必须 VALUE
                    if (PtsLineBreakMono(k)    != 1) { mism++; }     // 域内自洽：严格递增
                    if (PtsLineBreakFirstOk(k) != 1) { mism++; }     // 域内自洽：首断点 > 0
                    if (PtsLineBreakCoverOk(k) != 1) { mism++; }     // 跨域计数：n_brk >= n_hard
                    if (nb <= 0)                     { mism++; }
                    for (int i = 0; i < nb; i++)
                    {
                        // cp 序列 = cp_first + dcp（逐断点）
                        if (PtsLineBreakCpAt(k, i)  != PtsLineBreakCpFirst(k) + PtsLineBreakDcpAt(k, i)) { mism++; }
                        // 最小 LsLInfo 骨架：cpLim ＝ 该断点 cp
                        if (PtsLineBreakInfo(k, i, 1) != PtsLineBreakCpAt(k, i)) { mism++; }
                        // 最小 LsLInfo 骨架：cpFirstVis ＝ 该行起始 cp（i==0 ⇒ cp_first）
                        if (PtsLineBreakInfo(k, i, 0) != ((i == 0) ? PtsLineBreakCpFirst(k) : PtsLineBreakCpAt(k, i - 1))) { mism++; }
                        // 最小 LsLInfo 骨架：fFirstLineInPara ＝ (i==0)
                        if (PtsLineBreakInfo(k, i, 2) != ((i == 0) ? 1 : 0)) { mism++; }
                    }
                }

                Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " rc=" + rc + " slot=" + k
                     + " cp=[" + ((k >= 0) ? PtsLineBreakCpFirst(k) : -1) + "," + ((k >= 0) ? PtsLineBreakCpLim(k) : -1) + ")"
                     + " cch=" + ((k >= 0) ? PtsLineBreakCch(k) : -1)
                     + " n_brk=" + nb + " n_hard=" + nh
                     + " mono=" + ((k >= 0) ? PtsLineBreakMono(k) : -1)
                     + " first=" + ((k >= 0) ? PtsLineBreakFirstOk(k) : -1)
                     + " cover=" + ((k >= 0) ? PtsLineBreakCoverOk(k) : -1)
                     + " dcp0=" + ((k >= 0) ? PtsLineBreakDcpAt(k, 0) : -1)
                     + " dcpN=" + ((k >= 0) ? PtsLineBreakDcpAt(k, nb - 1) : -1)
                     + " seq=" + ((k >= 0) ? PtsLineBreakSeq(k) : -1)
                     + " mism=" + mism + " v=LINE-BREAKER-SET");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[LINEBREAK] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=exception:" + e.GetType().Name);
            }
        }
    }
}
