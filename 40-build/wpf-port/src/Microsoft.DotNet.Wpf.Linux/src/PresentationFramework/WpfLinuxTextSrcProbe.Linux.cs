// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A69（PRECOND-NO-TEXT-SOURCE · 内容源入站：字符序列）：托管侧喂料器 ＋ 逐字节对拍（[TEXTSRC]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_TEXTSRC_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxTextSrcProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
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
                    try { s = System.Environment.GetEnvironmentVariable("WPF_TEXTSRC_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFeed", ExactSpelling = true)]
        private static extern int PtsTextSrcFeed(IntPtr parah, int cpOff, IntPtr pwch, int cch);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFind", ExactSpelling = true)]
        private static extern int PtsTextSrcFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcCch", ExactSpelling = true)]
        private static extern int PtsTextSrcCch(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcChar", ExactSpelling = true)]
        private static extern int PtsTextSrcChar(int k, int i);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcState", ExactSpelling = true)]
        private static extern int PtsTextSrcState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcHash", ExactSpelling = true)]
        private static extern ulong PtsTextSrcHash(int k);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // FNV-1a 64：与 native 侧**逐字节**同法（UTF-16LE 字节流：低字节在前）⇒ 两边必须相等。
        private static ulong Fnv1a(string s)
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                h ^= (ulong)(c & 0xFF); h *= 1099511628211UL;
                h ^= (ulong)((c >> 8) & 0xFF); h *= 1099511628211UL;
            }
            return h;
        }

        // 把**本段落的字符序列**交给 native（一次／段），随即回读 native 的副本并逐字节对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int cp = 0;
            try
            {
                TextElement te = para.Element as TextElement;
                if (te == null) { Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                cp = para.ParagraphStartCharacterPosition;   // 该段的起始字符偏移（托管真值）
                int n = text.Length;
                IntPtr p = Marshal.StringToCoTaskMemUni(text);   // UTF-16 ＋ 终止 NUL
                int rc, k = -1, mism = -1;
                ulong want, got = 0UL;
                try
                {
                    rc = PtsTextSrcFeed(parah, cp, p, n);
                    _feeds++;
                    k = PtsTextSrcFind(parah);
                    mism = 0;
                    if (k < 0) { mism = -1; }
                    else if (PtsTextSrcCch(k) != n) { mism = -1; }      // 长度不符 ⇒ 必红
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (PtsTextSrcChar(k, i) != (int)text[i]) { mism++; }
                        }
                    }
                    want = Fnv1a(text);
                    got = (k >= 0) ? PtsTextSrcHash(k) : 0UL;
                }
                finally { Marshal.FreeCoTaskMem(p); }

                Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " cp=" + cp + " cch=" + n + " bytes=" + (n * 2)
                     + " rc=" + rc + " slot=" + k + " mism=" + mism
                     + " hash_match=" + ((want == got) ? 1 : 0)
                     + " state=" + ((k >= 0) ? PtsTextSrcState(k) : -1)
                     + " hash=" + got.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
                     + " v=CONTENT-SOURCE-INBOUND");
            }
            catch (System.Exception e)
            {
                Emit("[TEXTSRC] mgd parah=" + Hx(parah) + " cp=" + cp + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
