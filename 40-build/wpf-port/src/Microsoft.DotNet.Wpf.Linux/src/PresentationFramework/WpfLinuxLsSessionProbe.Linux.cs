// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/reapply-patches.py **生成**，不要手改。
//
// T-A72（PRECOND-LS-SESSION-DRIVER · LS 会话进链）：托管侧会话↔段落交付器 ＋ 逐格对拍（[LSSESS]）。
//
// 【射程】只入站 ＋ 只回读对拍：不改任何出参、不删／不放宽任何 Invariant.Assert、不置任何 native 真值。
// `WPF_LSSESS_FEED=0` ⇒ 整个关掉（逐字回上游行为）。

using System;
using System.Runtime.InteropServices;
using DllImport = MS.Internal.PresentationFramework.DllImport;

namespace MS.Internal.PtsHost
{
    internal static class WpfLinuxLsSessionProbe
    {
        private const int MaxFeeds = 64;                 // 有界：单进程最多喂 64 段
        private const int MaxContexts = 256;             // 有界：上下文→会话 表的条目上界
        private static int _feeds;
        private static int _enabled = -1;                // -1＝未读；0＝关；1＝开
        // 上下文（native 文档上下文句柄）→ LS 会话身份（`LoCreateContext` 的 `ploc`）。
        private static readonly System.Collections.Generic.Dictionary<long, long> _sess =
            new System.Collections.Generic.Dictionary<long, long>();
        private static readonly System.Collections.Generic.Dictionary<long, int> _done =
            new System.Collections.Generic.Dictionary<long, int>();

        internal static bool Enabled
        {
            get
            {
                if (_enabled < 0)
                {
                    string s = null;
                    try { s = System.Environment.GetEnvironmentVariable("WPF_LSSESS_FEED"); }
                    catch (System.Exception) { s = null; }
                    _enabled = (s == "0") ? 0 : 1;       // 缺省＝开；**只有**显式 "0" 才关
                }
                return _enabled == 1;
            }
        }

        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessFeed", ExactSpelling = true)]
        private static extern int PtsLsSessFeed(IntPtr ploc, IntPtr para);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessFind", ExactSpelling = true)]
        private static extern int PtsLsSessFind(IntPtr para);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCount", ExactSpelling = true)]
        private static extern int PtsLsSessCount();
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessState", ExactSpelling = true)]
        private static extern int PtsLsSessState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessSession", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessSession(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessPara", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessPara(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessInfoPtr", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessInfoPtr(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessRedefPtr", ExactSpelling = true)]
        private static extern IntPtr PtsLsSessRedefPtr(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessPtrOk", ExactSpelling = true)]
        private static extern int PtsLsSessPtrOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfState", ExactSpelling = true)]
        private static extern int PtsLsSessCbfState(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfMinNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfMinNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfRedefNonzero", ExactSpelling = true)]
        private static extern int PtsLsSessCbfRedefNonzero(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfLayOk", ExactSpelling = true)]
        private static extern int PtsLsSessCbfLayOk(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessCbfFp", ExactSpelling = true)]
        private static extern ulong PtsLsSessCbfFp(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessSrcSlot", ExactSpelling = true)]
        private static extern int PtsLsSessSrcSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessChainSlot", ExactSpelling = true)]
        private static extern int PtsLsSessChainSlot(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessParaLink", ExactSpelling = true)]
        private static extern int PtsLsSessParaLink(int k);
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessLiveSessions", ExactSpelling = true)]
        private static extern int PtsLsSessLiveSessions();
        [DllImport(DllImport.PresentationNative, EntryPoint = "WpfLinuxWin32_PtsLsSessDistinctSessions", ExactSpelling = true)]
        private static extern int PtsLsSessDistinctSessions();

        internal static string Hx(IntPtr p)
        {
            return "0x" + ((ulong)(long)p).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static void Emit(string line)
        {
            try { System.Console.Error.WriteLine(line); System.Console.Error.Flush(); }
            catch (System.Exception) { }
        }

        // 记档：本 PTS 文档上下文句柄 → 它的 **LS 会话身份**（`LoCreateContext` 的 `ploc`）。
        // 调用点 = `PtsCache.CreatePTSContext`（与 `LoCreateContext` **同一窗内**；见生成器内该块说明）。
        internal static void NoteContext(IntPtr docCtx, IntPtr ploc)
        {
            if (!Enabled || docCtx == IntPtr.Zero || ploc == IntPtr.Zero) { return; }
            if (_sess.Count >= MaxContexts) { _sess.Clear(); }   // 有界（超出即重开表）
            _sess[(long)docCtx] = (long)ploc;
            Emit("[LSSESS] ctx doc=" + Hx(docCtx) + " ploc=" + Hx(ploc) + " live=" + PtsLsSessLiveSessions()
                 + " v=LS-SESSION-NOTED");
        }

        // 把本段落的 `(会话, 段)` 在**同一窗内**交给 native，随即回读并逐格对拍。
        internal static void Feed(IntPtr docCtx, IntPtr parah)
        {
            if (!Enabled || parah == IntPtr.Zero) { return; }
            if (_feeds >= MaxFeeds) { return; }
            long key = (long)parah;
            if (_done.ContainsKey(key)) { return; }
            _done[key] = 1;

            long ploc = 0;
            try
            {
                if (docCtx == IntPtr.Zero || !_sess.TryGetValue((long)docCtx, out ploc) || ploc == 0)
                {
                    _done.Remove(key);
                    Emit("[LSSESS] mgd parah=" + Hx(parah) + " v=NO-SESSION reason=no-session-for-context");
                    return;
                }

                int rc = PtsLsSessFeed((IntPtr)ploc, parah);
                _feeds++;
                if (rc != 0)
                {
                    // 诚实拒绝（**会话不在本侧登记表**／空参）：**不是**正读数 ⇒ 单列一行，供反腿与判据点名。
                    Emit("[LSSESS] mgd ploc=" + Hx((IntPtr)ploc) + " parah=" + Hx(parah)
                         + " rc=" + rc + " v=REJECT-NOT-IN-CHAIN");
                    return;
                }
                int k = PtsLsSessFind(parah);
                int mism = 0;
                if (k < 0) { mism = -1; }
                else
                {
                    if (PtsLsSessSession(k)   != (IntPtr)ploc) { mism++; }   // 会话身份
                    if (PtsLsSessPara(k)      != parah)        { mism++; }   // 所属段
                    if (PtsLsSessState(k)     != 2)            { mism++; }   // 必须 BOUND
                    if (PtsLsSessParaLink(k)  != 1)            { mism++; }   // 段在两本台账里可解析
                    if (PtsLsSessPtrOk(k)     != 1)            { mism++; }   // 两结构指针一致性
                    if (PtsLsSessCbfState(k)  != 2)            { mism++; }   // 回调面已值化
                    if (PtsLsSessCbfNonzero(k) <= 0)           { mism++; }   // 回调面指纹：非零槽
                    if (PtsLsSessCbfLayOk(k)  != 1)            { mism++; }   // 回调面指纹：布局自证
                }

                Emit("[LSSESS] mgd ploc=" + Hx((IntPtr)ploc) + " para=" + Hx(parah)
                     + " rc=" + rc + " slot=" + k
                     + " info=" + Hx((k >= 0) ? PtsLsSessInfoPtr(k) : IntPtr.Zero)
                     + " redef=" + Hx((k >= 0) ? PtsLsSessRedefPtr(k) : IntPtr.Zero)
                     + " cbf_state=" + ((k >= 0) ? PtsLsSessCbfState(k) : -1)
                     + " nonzero=" + ((k >= 0) ? PtsLsSessCbfNonzero(k) : -1)
                     + " min=" + ((k >= 0) ? PtsLsSessCbfMinNonzero(k) : -1) + "/9"
                     + " redef_nz=" + ((k >= 0) ? PtsLsSessCbfRedefNonzero(k) : -1)
                     + " layok=" + ((k >= 0) ? PtsLsSessCbfLayOk(k) : -1)
                     + " fp=" + ((k >= 0) ? PtsLsSessCbfFp(k) : 0UL).ToString("x16")
                     + " ptr=" + ((k >= 0) ? PtsLsSessPtrOk(k) : -1)
                     + " src_slot=" + ((k >= 0) ? PtsLsSessSrcSlot(k) : -999)
                     + " chain_slot=" + ((k >= 0) ? PtsLsSessChainSlot(k) : -999)
                     + " link=" + ((k >= 0) ? PtsLsSessParaLink(k) : -1)
                     + " state=" + ((k >= 0) ? PtsLsSessState(k) : -1)
                     + " npara=" + PtsLsSessCount()
                     + " nsess=" + PtsLsSessDistinctSessions()
                     + " mism=" + mism + " v=LS-SESSION-IN-CHAIN");
            }
            catch (System.Exception e)
            {
                _done.Remove(key);
                Emit("[LSSESS] mgd parah=" + Hx(parah) + " v=NO-SESSION reason=exception:" + e.GetType().Name);
            }
        }
    }
}
