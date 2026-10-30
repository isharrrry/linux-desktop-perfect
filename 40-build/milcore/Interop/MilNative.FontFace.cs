// Licensed to the .NET Foundation under one or more agreements.
//
// T1/M7c2 · **跨运行时字体面登记**导出。
//
// ============================================================================
//  【问题（T2 实测，不是推测）】
//  T1 之后 MIL 是 NativeAOT 共享库（`wpfgfx_cor3.so`）——**那是另一个 .NET 运行时**，
//  有自己的 GC、自己的静态状态。`MilFontFaceTable` 就活在那个运行时里。
//
//  于是托管侧（HelloWpf 的 PresentationCore）**直接调**
//  `MilFontFaceTable.Register(skTypeface)` 是无效的：那条调用发生在宿主进程的
//  运行时里，只会写进一份 .so 永远看不见的副本。实测后果：
//      BRIDGE_DIAGNOSTICS: status=ProcessLocalOnly registerExport=未找到 allocatorCalls=0
//      → 降级：令牌只在进程内表；MilGlyphRun_GetGlyphOutline 返回 E_HANDLE
//      → **画不出字形轮廓**（窗口里没有字）。
//
//  【解法：把"登记"做成一个 C ABI 导出】
//  跨得过去的只有 C ABI。所以这里在 **MIL 侧**（= .so 自己的运行时里）加一个导出：
//
//      intptr_t MilFontFace_RegisterFromFile(const char* utf8Path,
//                                            int32 faceIndex, int32 simFlags);
//
//  托管侧经 `NativeLibrary.GetExport` / `[DllImport]` 调它，由 .so 的运行时
//  创建 SKTypeface 并登记进**它自己那份** MilFontFaceTable，返回令牌；
//  令牌再交给 MilGlyphRun_GetGlyphOutline（同样在 .so 里执行）就能查到字体面。
//
//  【契约】
//      返回值：非 0 = .so 运行时内的字体面令牌；**0 = 失败**（不是 HRESULT！）
//      失败条件（一律返回 0，**绝不抛异常穿 ABI**）：
//        · utf8Path == NULL / 空串 / 非法 UTF-8
//        · 文件不存在
//        · 文件不是 Skia 能解析的字体（含抛异常的情形）
//        · faceIndex < 0 或越界（Skia 对越界索引返回 null）
//      simFlags：DWrite `DWRITE_FONT_SIMULATIONS` 口径
//        0 = 不模拟 / 1 = Bold / 2 = Oblique / 3 = 两者
//        高位未知位**不报错、原样记录**（可用 MilFontFaceTable.TryGetSimFlags 查），
//        生效的只有 Bold/Oblique 两位。生效点见 MilGlyphRun_GetGlyphOutline。
//
//  ⚠️ **令牌只在 .so 的运行时里有意义**，宿主进程不能拿它去查自己的 MilFontFaceTable
//     （查不到是正常的）。宿主只需把它当不透明句柄透传给其他 MIL 导出。
// ============================================================================

using System;
using System.Text;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// `MilFontFace_RegisterFromFile` 的**最近一次失败原因**（M7c3 新增）。
    ///
    /// 【为什么需要它】上一轮实现把所有异常都 `catch → 0`，于是"文件不存在"、
    /// "Skia 加载不了"、"faceIndex 越界"在调用侧看起来**完全一样** ——
    /// T2 实测拿到的就是这种黑盒：`registerExport=找到` 但 `nativeAllocations=0`。
    /// 真因是 .so 里的 SkiaSharp 找不到 `libSkiaSharp.so`（`DllNotFoundException`），
    /// 却被吞成了 0。这条记录把真因留下，并由
    /// `MilBridge_Diag_LastFontFaceError` / `MilBridge_Diag_LastFontFaceErrorMessage` 导出。
    ///
    /// ⚠️ 函数契约不变：**仍然只返回 0，不抛异常**。这里只是旁路记录。
    /// </summary>
    public static class MilFontFaceDiagnostics
    {
        /// <summary>失败原因码。</summary>
        public enum Failure : int
        {
            /// <summary>没有失败（上一次调用成功，或还没调用过）。</summary>
            None = 0,
            /// <summary>utf8Path == NULL。</summary>
            NullPath = 1,
            /// <summary>空串。</summary>
            EmptyPath = 2,
            /// <summary>路径长度 ≥ 32768 字节（或没有 NUL 结尾）。</summary>
            PathTooLong = 3,
            /// <summary>不是合法 UTF-8。</summary>
            InvalidUtf8 = 4,
            /// <summary>文件不存在。</summary>
            FileNotFound = 5,
            /// <summary>faceIndex &lt; 0。</summary>
            NegativeFaceIndex = 6,
            /// <summary>Skia 解析不了（损坏 / faceIndex 越界 / 不是字体）→ FromFile 返回 null。</summary>
            TypefaceLoadFailed = 7,
            /// <summary>调 Skia 时抛异常（**最常见：DllNotFoundException: libSkiaSharp**）。</summary>
            Exception = 8,
        }

        private static int s_code;
        private static string s_message;
        private static readonly object s_gate = new object();

        /// <summary>最近一次失败码（None 表示上次成功）。</summary>
        public static Failure LastFailure { get { lock (s_gate) return (Failure)s_code; } }

        /// <summary>最近一次失败的详情（含异常类型与消息）；成功时为 null。</summary>
        public static string LastMessage { get { lock (s_gate) return s_message; } }

        /// <summary>记一次成功（清掉上一次的失败记录）。</summary>
        public static void ReportSuccess() { lock (s_gate) { s_code = (int)Failure.None; s_message = null; } }

        /// <summary>记一次失败。</summary>
        public static void ReportFailure(Failure code, string message)
        {
            lock (s_gate) { s_code = (int)code; s_message = message; }
        }

        /// <summary>异常 → 失败记录（类型名 + 消息，足够定位 DllNotFoundException 这类真因）。</summary>
        public static void ReportException(Exception ex)
            => ReportFailure(Failure.Exception, ex.GetType().FullName + ": " + ex.Message);

        // ── [W8 / D-F1c 内存半边] 字体后备缓存的**只读计数**（缺省不影响任何行为）──────────
        //   为什么要有：本项的判据是"`.ttc` 的**映射段数**/Σ虚拟"（`smaps`），而段数是 Skia 侧的；
        //   这里给出**产品侧可归因**的量：
        //     · `FileDataCreatedCount`／`FileDataReusedCount`：按**规范路径**建/复用共享后备的次数；
        //     · `LiveFileDataCount`／`LiveFileDataBytes`：当前缓存里几个文件、共多少字节
        //       （**若它随建面次数线性增长 ⇒ 缓存键没命中** —— 符号链接/路径形态不一致的典型症状）；
        //     · `TypefaceCreatedCount`／`DistinctFaceKeys`：真建了多少个 `SKTypeface`、涉及多少个
        //       `path\0index` 键（"每个面各建一个"是**正常**的；不正常的是**每个面各一份整文件映射**）。
        //   ⚠️ 纯观测：不参与控制流；`MilFontFaceDiagnostics` 的既有契约（最近一次失败原因）一个字不变。
        private static long s_fileDataCreated, s_fileDataReused, s_typefaceCreated;
        private static int s_liveFileDataCount;
        private static long s_liveFileDataBytes;
        private static readonly Dictionary<string, long> s_faceKeys =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly object s_cacheGate = new object();

        internal static void NoteFileDataCreated(string key, long bytes)
        { lock (s_cacheGate) { s_fileDataCreated++; s_liveFileDataCount++; s_liveFileDataBytes += bytes; } }
        internal static void NoteFileDataReused(string key) { lock (s_cacheGate) s_fileDataReused++; }
        internal static void NoteTypefaceCreated(string key, int index)
        {
            lock (s_cacheGate)
            {
                s_typefaceCreated++;
                string k = key + "\u0000" + index;
                s_faceKeys.TryGetValue(k, out long n);
                s_faceKeys[k] = n + 1;
            }
        }
        internal static void NoteFileDataReset() { lock (s_cacheGate) { s_liveFileDataCount = 0; s_liveFileDataBytes = 0; } }
        internal static void NoteFileDataTrimmed(int files)
        { lock (s_cacheGate) { s_liveFileDataCount = Math.Max(0, s_liveFileDataCount - files); } }

        // 【超预算可见（"仪器必须能变红"）】`SkiaFontFileCache` 的字节预算（默认 64 MB）被触发时记一次：
        //   但这**不代表**我们已经把内存压下来了 —— 活面持有的映射**由 Skia 引用计数决定、不由我们决定**
        //   （实测 D/E：Dispose 掉我们的 `SKData` / `Reset()` 之后，面照用、映射照在）。
        //   ⇒ 这三个量是"该由**注册策略**解决"的证据，别读成"缓存没管好"。
        private static long s_overBudgetCount, s_overBudgetBytes;
        private static int s_overBudgetFiles;
        internal static void NoteFileDataOverBudget(int files, long bytes)
        { lock (s_cacheGate) { s_overBudgetCount++; s_overBudgetFiles = files; s_overBudgetBytes = bytes; } }
        /// <summary>字节预算被触发过几次。</summary>
        public static long OverBudgetCount { get { lock (s_cacheGate) return s_overBudgetCount; } }
        /// <summary>最近一次超预算时的缓存文件数。</summary>
        public static int OverBudgetFiles { get { lock (s_cacheGate) return s_overBudgetFiles; } }
        /// <summary>最近一次超预算时的缓存字节数。</summary>
        public static long OverBudgetBytes { get { lock (s_cacheGate) return s_overBudgetBytes; } }

        /// <summary>按规范路径建共享后备的次数（单调）。</summary>
        public static long FileDataCreatedCount { get { lock (s_cacheGate) return s_fileDataCreated; } }
        /// <summary>命中既有共享后备的次数（单调）。</summary>
        public static long FileDataReusedCount { get { lock (s_cacheGate) return s_fileDataReused; } }
        /// <summary>缓存里当前有几个文件的后备。</summary>
        public static int LiveFileDataCount { get { lock (s_cacheGate) return s_liveFileDataCount; } }
        /// <summary>这些后备共多少字节。</summary>
        public static long LiveFileDataBytes { get { lock (s_cacheGate) return s_liveFileDataBytes; } }
        /// <summary>真建出来的 `SKTypeface` 个数（单调）。</summary>
        public static long TypefaceCreatedCount { get { lock (s_cacheGate) return s_typefaceCreated; } }
        /// <summary>涉及多少个 `path\0index` 键。</summary>
        public static int DistinctFaceKeys { get { lock (s_cacheGate) return s_faceKeys.Count; } }

        /// <summary>一行摘要（复取归因用；不含路径，避免日志膨胀）。</summary>
        public static string FontCacheSnapshot()
            => $"fileData(created={FileDataCreatedCount}, reused={FileDataReusedCount}, " +
               $"live={LiveFileDataCount}, bytes={LiveFileDataBytes}) " +
               $"typeface(created={TypefaceCreatedCount}, distinctKeys={DistinctFaceKeys}, " +
               $"liveWeak={SkiaFontFileCache.LiveFaceCount}) " +
               $"overBudget(hits={OverBudgetCount}, files={OverBudgetFiles}, bytes={OverBudgetBytes})";

        /// <summary>清空（测试用）。</summary>
        public static void Reset() => ReportSuccess();
    }

    public static unsafe partial class MilNative
    {
        /// <summary>
        /// 从字体文件登记一个字体面到**本运行时**的字体面表，返回令牌（0 = 失败）。
        ///
        /// 这是唯一能跨越 NativeAOT .so 与宿主进程之间那条静态状态边界的形态 ——
        /// 详细背景见本文件顶部的注释。
        /// </summary>
        /// <param name="utf8Path">UTF-8 编码的字体文件绝对路径。</param>
        /// <param name="faceIndex">字体集合内的面索引（TTC/OTC 用；单面字体传 0）。</param>
        /// <param name="simFlags">DWRITE_FONT_SIMULATIONS：1=Bold，2=Oblique。</param>
        public static IntPtr MilFontFace_RegisterFromFile(byte* utf8Path, int faceIndex, int simFlags)
        {
            if (utf8Path == null)
            {
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.NullPath, "utf8Path == NULL");
                return IntPtr.Zero;
            }

            string path;
            try
            {
                // 先按 NUL 结尾量长度（C 字符串约定）。上限 32768：路径长度的合理上界，
                // 也挡住"调用方传了非 NUL 结尾的缓冲"这种会无限扫描的情形。
                int length = 0;
                while (length < MaxPathBytes && utf8Path[length] != 0) length++;
                if (length == 0)
                {
                    MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.EmptyPath, "空路径");
                    return IntPtr.Zero;
                }
                if (length >= MaxPathBytes)
                {
                    MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.PathTooLong,
                        $"路径长度 ≥ {MaxPathBytes} 字节（或没有 NUL 结尾）");
                    return IntPtr.Zero;
                }

                // 用严格 UTF-8 解码：非法字节序列 → DecoderFallbackException → 返回 0。
                // 不抛给调用方（调用方在 C ABI 另一侧，异常穿不过去也没意义）；
                // 也不能用宽松解码 —— 乱码路径可能静默变成另一个合法路径去查文件。
                path = StrictUtf8.GetString(new ReadOnlySpan<byte>(utf8Path, length));
            }
            catch (Exception ex)
            {
                MilFontFaceDiagnostics.ReportException(ex);
                return IntPtr.Zero;
            }

            IntPtr token = MilFontFaceTable.RegisterFromFile(path, faceIndex, simFlags);
            if (token == IntPtr.Zero && MilFontFaceDiagnostics.LastFailure == MilFontFaceDiagnostics.Failure.None)
            {
                // RegisterFromFile 里已经记过具体原因；这里兜底，保证"返回 0 必有原因"。
                MilFontFaceDiagnostics.ReportFailure(MilFontFaceDiagnostics.Failure.TypefaceLoadFailed,
                    $"RegisterFromFile 返回 0：path={path} faceIndex={faceIndex} simFlags={simFlags}");
            }
            return token;
        }

        /// <summary>路径长度上限（字节）。超过即视为非法参数。</summary>
        private const int MaxPathBytes = 32768;

        /// <summary>
        /// 严格 UTF-8（非法字节序列抛异常，而不是替换成 U+FFFD）——
        /// 否则一个乱码路径会静默变成另一个合法路径去查文件。
        /// </summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    }
}
