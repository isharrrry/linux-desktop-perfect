// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A71（PRECOND-NO-TEXT-PARA-IN-CHAIN · 文本段落进链）：托管侧交付器 ＋ 逐格对拍（[PARACHAIN]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_PARACHAIN_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxTextParaChainProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxPages = 256;                // 有界：页身份表的条目上界
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();
        // 页身份：**同一 PageContext 实例** ⇒ **同一序号**（引用相等；只作不透明 token 交 native）。
        private sealed class RefEq : System.Collections.Generic.IEqualityComparer<object>
        {
            bool System.Collections.Generic.IEqualityComparer<object>.Equals(object a, object b) { return object.ReferenceEquals(a, b); }
            int System.Collections.Generic.IEqualityComparer<object>.GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        private static readonly System.Collections.Generic.Dictionary<object, int> _pages =
            new System.Collections.Generic.Dictionary<object, int>(new RefEq());

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_PARACHAIN_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainFeed", ExactSpelling = true)]
        private static extern int PtsParaChainFeed(IntPtr parah, int pageId, int cpFirst, int cpLim);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainFind", ExactSpelling = true)]
        private static extern int PtsParaChainFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainState", ExactSpelling = true)]
        private static extern int PtsParaChainState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainPageId", ExactSpelling = true)]
        private static extern int PtsParaChainPageId(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSubClaimed", ExactSpelling = true)]
        private static extern int PtsParaChainSubClaimed(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainCpFirst", ExactSpelling = true)]
        private static extern int PtsParaChainCpFirst(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainCpLim", ExactSpelling = true)]
        private static extern int PtsParaChainCpLim(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSrcSlot", ExactSpelling = true)]
        private static extern int PtsParaChainSrcSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainSrcHandle", ExactSpelling = true)]
        private static extern IntPtr PtsParaChainSrcHandle(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsParaChainLinkOk", ExactSpelling = true)]
        private static extern int PtsParaChainLinkOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcFind", ExactSpelling = true)]
        private static extern int PtsTextSrcFind(IntPtr parah);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsTextSrcHandle", ExactSpelling = true)]
        private static extern IntPtr PtsTextSrcHandle(int k);

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        private static int PageId(object pageCtx)
        {
            int id;
            if (_pages.TryGetValue(pageCtx, out id)) { return id; }
            if (_pages.Count >= MaxPages) { _pages.Clear(); }   // 有界（超出即重开表；页身份只在同窗内比较）
            id = _pages.Count + 1;
            _pages[pageCtx] = id;
            return id;
        }

        // 把**本段落的链坐标**（段身份／页身份／cp 域）交给 native（一次／段），随即回读并逐格对拍。
        internal static void FeedParagraph(IntPtr parah, BaseParagraph para, object pageCtx)
        {
            if (!Enabled || para == null || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            int pageId = 0, cpFirst = 0, cpLim = 0;
            try
            {
                if (pageCtx == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-page-context"); return; }
                TextElement te = para.Element as TextElement;
                if (te == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=element-not-textelement"); return; }

                string text = new TextRange(te.ContentStart, te.ContentEnd).Text;
                if (text == null) { _done.Remove(key); Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " v=NO-SOURCE reason=null-text"); return; }

                pageId  = PageId(pageCtx);
                cpFirst = para.ParagraphStartCharacterPosition;   // 段起始字符位置（宿主真值）
                cpLim   = cpFirst + text.Length;                  // 段末字符位置（开区间上界）

                int rc = PtsParaChainFeed(parah, pageId, cpFirst, cpLim);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**不在链上**／参数不自洽）：**不是**正读数 ⇒ 单列一行，供反腿与判据点名。
                    Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId + " cp=[" + cpFirst + "," + cpLim
                         + ") rc=" + rc + " v=REJECT-NOT-IN-CHAIN");
                    return;
                }
                int k = PtsParaChainFind(parah);
                int mism = 0;
                int ts   = PtsTextSrcFind(parah);
                IntPtr nth = (ts >= 0) ? PtsTextSrcHandle(ts) : IntPtr.Zero;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsParaChainState(k)      != 2)                     { mism++; }   // 必须 SOURCED
                    if (PtsParaChainPageId(k)     != pageId)                { mism++; }   // 页身份
                    if (PtsParaChainSubClaimed(k) != 1)                     { mism++; }   // 子轨可认领
                    if (PtsParaChainCpFirst(k)    != cpFirst)               { mism++; }   // cp 域下界
                    if (PtsParaChainCpLim(k)      != cpLim)                 { mism++; }   // cp 域上界
                    if (PtsParaChainSrcSlot(k)    != ts)                    { mism++; }   // 与 T-A69 一一对应（槽）
                    if (PtsParaChainSrcHandle(k)  != nth)                   { mism++; }   // 与 T-A69 一一对应（句柄）
                }
                int link = (k >= 0) ? PtsParaChainLinkOk(k) : 0;

                Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId
                     + " cp=[" + cpFirst + "," + cpLim + ") n=" + (cpLim - cpFirst)
                     + " rc=" + rc + " slot=" + k + " state=" + ((k >= 0) ? PtsParaChainState(k) : -1)
                     + " sub=" + ((k >= 0) ? PtsParaChainSubClaimed(k) : -1)
                     + " src_slot=" + ((k >= 0) ? PtsParaChainSrcSlot(k) : -999)
                     + " src=" + Hx((k >= 0) ? PtsParaChainSrcHandle(k) : IntPtr.Zero)
                     + " link=" + link + " mism=" + mism + " v=TEXT-PARA-IN-CHAIN");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[PARACHAIN] mgd parah=" + Hx(parah) + " page=" + pageId + " v=NO-SOURCE reason=exception:"
                     + e.GetType().Name);
            }
        }
    }
}
