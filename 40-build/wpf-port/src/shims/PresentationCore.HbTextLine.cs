// T1b/B2 · 基于 HarfBuzz 的 `TextLine` 实现（**编译进 PresentationCore**）
// =====================================================================================
// 【为什么必须是 shim（编译进 PC），而不是外部程序集】
//   轨道A 原型（`src/Linux/build/MilBridge/tests/TextLineProto/`，461 行 / 311 代码行）验证了实现本身可控，
//   但它当时**只能用反射**造四个 internal 类型：
//     · `TextBounds` / `TextRunBounds`   —— 构造是 **internal**（实测 public ctor = 0）
//     · `TextCollapsedRange`             —— 构造是 internal
//     · `TextLineBreak`                  —— 构造是 internal（`(TextModifierScope, IntPtr)`）
//     · `GlyphTypeface(Font)`            —— 构造是 **internal**
//   本文件按 `src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.shims.txt` 机制编进 PresentationCore，于是 **internal 可见**。
//
// 【B2 本轮范围】—— 把三个成员做**实**
//   · `GetTextLineBreak()`       —— 普通文本 **null**；只有 TextModifier 情形才发**真的** `TextLineBreak`
//                                   对象（`_breakRecord = IntPtr.Zero`、`_currentScope = null`）。
//   · `Collapse(...)`            —— 真实现：按真机 oracle 的 CharacterEllipsis 折叠。
//   · `GetTextCollapsedRanges()` —— 真实现：未折叠 ⇒ **null**（不是空集合，真机 3222/3222 实测）。
//   · `GetIndexedGlyphRuns()`    —— **已实现**（2026-09-15 主控推翻旧裁定；依据 = 它是真值的观测装置：
//      `GlyphTypeface.FontUri` / `GlyphIndices`(0=`.notdef`) / `AdvanceWidths` 供 C2 判据）。
//   · 断行引擎（ICU 断点集 + 2 条 DWrite 覆盖规则 + 贪心填宽 + 禁则拉字 + 强制断 fallback）
//     从 `src/Linux/build/MilBridge/tests/IcuBreakParity/` **原样搬进本文件** —— 从此**规则只有这一份**，
//     两个 harness（`run.sh icu` 与 `run.sh textline`/`tline`）驱动的都是本文件的 `HbBreakEngine`。
//
// 【口径的真值来源（2026-09-11 主控裁定，不是猜的）】
//   `src/Linux/tests/parity/windows/layout-b34/`（Windows 11 真机 `TextFormatter.FormatLine` 逐属性 dump）。
//   ⚠️ 与那 73 例 CJK oracle **不是同一条路径**：73 例走 DirectWrite `IDWriteTextLayout`，
//      本 oracle 走 WPF 托管栈（`TextFormatter → TextMetrics → lsapi`）。**Linux 侧实现的是
//      `TextLine`/`TextFormatter` 契约 ⇒ 冲突时以本 oracle 为准**（对拍数字见 T1b 报告）。
//   三条记账（独立复算见 `src/Linux/build/MilBridge/gen/layout-b34-accounting.txt`）：
//     ① 行区间**含**硬断字符：`Length` 把它算进去，`NewlineLength` 单独给个数（`\r\n` = 2）；
//     ② 相邻 `\n` **不是零长度行**，而是 `Length=1`、内容就是 `'\n'` 的普通行（`Width=0`，占一行高）；
//     ③ 行尾空白**占区间、不占 `Width`**，差额由 `WidthIncludingTrailingWhitespace` 给出；
//        且 `TrailingWhitespaceLength` **把换行符/EOP 也算进去**。
//     另：**段落最后一行 `Length` = 剩余文本长度 + 1（EOP）**、其 `NewlineLength` 恒 1（EOP 标记）。
//
// 【两种构造模式（同一个源文件编多次，保证"换方式后行为不变"）】
//   `TEXTLINE_SHIM_DIRECT`       定义 → 直构 internal 类型（**只有编进 PC 时才成立**）
//   未定义                          → 反射（让同一份源文件能在 PC 之外编译/运行，用于对照验证）
//   `TEXTLINE_BREAK_ENGINE_ONLY` 定义 → **只编断行引擎 + HarfBuzz 绑定**（不引用 PC），
//                                     供 `IcuBreakParity` 复用同一份规则（保持"零规则副本"）。
//
// 【与既有 shim 的关系】命名空间 `WpfLinux.Shims.PresentationCore`（与 `FontBridge` 同前缀），
//   不占用任何上游命名空间，不与 `Factory.Linux` / `OleApi.Stubs` 撞名。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

#if !TEXTLINE_BREAK_ENGINE_ONLY
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
#endif

namespace WpfLinux.Shims.PresentationCore
{
    /// <summary>本文件的唯一标识 —— harness 用它自证"驱动的是这份源文件"。</summary>
    internal static class HbShimSource
    {
        internal const string File = "src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.HbTextLine.cs";
        internal const string Tag = "T1b/B2 · 2026-09-11 · break-engine + TextLineBreak + Collapse";

        /// <summary>引擎（规则）版本 —— 与 `IcuBreakParity` 的 73 例对拍绑定。</summary>
        internal const string EngineTag = "ICU(UAX#14) + `…`前可断 + 禁则拉字 + 强制断 fallback";
    }

    // ==================================================================================
    //  1. HarfBuzz shaping（无反射、无 PC 依赖 —— 两种模式都要）
    // ==================================================================================

    /// <summary>一次 shaping 的结果。</summary>
    internal sealed class HbShapedRun
    {
        public ushort[] Glyphs;          // 字形 id
        public uint[] Clusters;          // 每个字形 → **UTF-16 码元**索引（HarfBuzz 的 cluster）
        public double[] AdvancesPx;      // 每个字形的步进（设备无关像素）
        public double[] OffsetsXPx;      // 每个字形的 x 偏移（GPOS 定位）
        public uint Upem;
        public double EmSize;
        public string Text;

        /// <summary>
        /// T1d 修法①：本段是否 **RTL**（HB 解析出的方向）。已按逻辑序排列
        /// （见 `HbShaper.Shape` 里的反转），这个标记只用来给 `GlyphRun.BidiLevel` 赋值。
        /// </summary>
        public bool Rtl;

        /// <summary>
        /// R1/T1d：多字体整形时**每个字体子段**的独立结果（`Run.Clusters` 是**本段局部**下标）。
        /// `null` / 空 / 只有一个 ⇒ 单面整形（= 今天的行为，`Draw` 也只发一个 `GlyphRun`）。
        /// </summary>
        public List<HbShapedChunk> Chunks;

        public double TotalWidthPx
        {
            get { double s = 0; foreach (double a in AdvancesPx) s += a; return s; }
        }

        /// <summary>
        /// 「字符 → advance」：把字形 advance 归到它的 cluster 首字符上（连字整段记首字）。
        /// 断行填宽与 `Width`/`WidthIncludingTrailingWhitespace` 都走这条。
        /// </summary>
        internal double[] CharAdvances()
        {
            var adv = new double[Text.Length];
            for (int i = 0; i < Glyphs.Length; i++)
            {
                uint c = Clusters[i];
                if (c < (uint)adv.Length) adv[c] += AdvancesPx[i];
            }
            return adv;
        }

        internal static HbShapedRun Empty(double emSize)
        {
            return new HbShapedRun
            {
                Glyphs = new ushort[0], Clusters = new uint[0], AdvancesPx = new double[0],
                OffsetsXPx = new double[0], Upem = 0, EmSize = emSize, Text = string.Empty,
            };
        }
    }

    /// <summary>HarfBuzz 最小绑定（M7c4 spike 已验证的那套 P/Invoke 面）。</summary>
    internal static unsafe class HbShaper
    {
        private const string Hb = "libharfbuzz.so.0";

        [DllImport(Hb)] internal static extern IntPtr hb_blob_create_from_file(byte* filename);
        // ---- D-F1c5（窗口 6，主控放行）：**活着的 blob 计数**（每处创建 +1、每处销毁 -1）----
        //   用途：`s_faces`(residentCount) 解释不了 1.67 GB（22 面 × 18.6 MB ≈ 410 MB ⇒ 需要 ~90 份活映射）
        //   ⇒ 用它找"谁还持有映射"。（`FACECACHE_DIAG`/摘要里打印，并每跨 10 打一行 stderr 时间序列。）
        internal static long LiveBlobs;
        internal static long LiveBlobsPeak;
        // ---- 窗口 10 更正（主控派单）：`DestroyBlob` 过去对**每一个**销毁都 `--`，但只有 `CreateBlobFromFile` 会 `++`
        //   ⇒ `hb_face_reference_table` 返回的**表子 blob**（HarfBuzz 自己建的；调用点只有 `:905`/`:949` 两处）
        //   也走 DestroyBlob ⇒ 白扣 ⇒ 运行值出现 **-746**（T2 现场）。`peak` 不受影响（只在创建时抬高）。
        //   现在**按地址记账**：命中"我们建的"才 `--`；未命中 ⇒ 计入 `ForeignBlobDestroys`（不是泄漏）。
        // ---- 并按**寿命**分成两格（窗口 10 主线新增了"按文件共享一份 blob"⇒ 出现长期存活的一类）----
        //   · transient：`CreateBlobFromFile`      —— 我们自己**马上**销毁那一份引用（扫描/元数据/覆盖位图）
        //   · shared   ：`CreateSharedBlobFromFile` —— `HbFaceCache` 按 `path` 共享、引用计数、最后一面释放
        //   ⇒ `LiveBlobs/LiveBlobsPeak` 的口径**逐字不变**（只计瞬态那一类 ⇒ 峰值仍 ≈1）；长期那一类由
        //     `HbFaceCache.SharedBlobFiles` 单列，`blobsOutstanding=liveBlobs+sharedBlobFiles` 给出总数。
        internal static long ForeignBlobDestroys;
        private static readonly System.Collections.Generic.HashSet<IntPtr> s_transientBlobs =
            new System.Collections.Generic.HashSet<IntPtr>();
        private static readonly System.Collections.Generic.HashSet<IntPtr> s_sharedBlobs =
            new System.Collections.Generic.HashSet<IntPtr>();
        private static readonly object s_blobGate = new object();

        internal static IntPtr CreateBlobFromFile(byte* filename)
        {
            IntPtr b = hb_blob_create_from_file(filename);
            if (b != IntPtr.Zero)
            {
                long v;
                lock (s_blobGate) { s_transientBlobs.Add(b); v = ++LiveBlobs; }
                if (v > LiveBlobsPeak) LiveBlobsPeak = v;
                if (v % 10 == 0) Console.Error.WriteLine("[LIVEBLOBS] " + v);
            }
            return b;
        }

        /// <summary>窗口 10：**长期存活**那一类（由 `HbFaceCache` 按 path 共享、引用计数）。不计入 `LiveBlobs`。</summary>
        internal static IntPtr CreateSharedBlobFromFile(byte* filename)
        {
            IntPtr b = hb_blob_create_from_file(filename);
            if (b != IntPtr.Zero) lock (s_blobGate) { s_sharedBlobs.Add(b); }
            return b;
        }

        internal static void DestroyBlob(IntPtr blob)
        {
            if (blob == IntPtr.Zero) return;
            bool transient = false, shared = false;
            lock (s_blobGate)
            {
                if (s_transientBlobs.Remove(blob)) { transient = true; --LiveBlobs; }
                else if (s_sharedBlobs.Remove(blob)) { shared = true; }
            }
            if (!transient && !shared) System.Threading.Interlocked.Increment(ref ForeignBlobDestroys);
            hb_blob_destroy(blob);
        }
        [DllImport(Hb)] internal static extern uint hb_blob_get_length(IntPtr blob);
        [DllImport(Hb)] internal static extern IntPtr hb_face_create(IntPtr blob, uint index);
        [DllImport(Hb)] internal static extern uint hb_face_get_upem(IntPtr face);
        [DllImport(Hb)] internal static extern IntPtr hb_font_create(IntPtr face);
        [DllImport(Hb)] internal static extern void hb_ot_font_set_funcs(IntPtr font);
        [DllImport(Hb)] internal static extern void hb_font_set_scale(IntPtr font, int xScale, int yScale);
        [DllImport(Hb)] internal static extern IntPtr hb_buffer_create();
        [DllImport(Hb)] internal static extern void hb_buffer_add_utf16(IntPtr buf, char* text, int textLength, int itemOffset, int itemLength);
        [DllImport(Hb)] internal static extern void hb_buffer_guess_segment_properties(IntPtr buf);
        /// <summary>HB 自己解析出来的方向（T1d 修法①：判定 RTL 的**权威来源**，不靠猜 cluster 序）。</summary>
        [DllImport(Hb)] internal static extern int hb_buffer_get_direction(IntPtr buf);
        private const int HB_DIRECTION_RTL = 5;
        [DllImport(Hb)] internal static extern IntPtr hb_language_from_string(byte* str, int len);
        [DllImport(Hb)] internal static extern void hb_buffer_set_language(IntPtr buf, IntPtr language);
        [DllImport(Hb)] internal static extern void hb_shape(IntPtr font, IntPtr buf, IntPtr features, uint numFeatures);
        [DllImport(Hb)] internal static extern IntPtr hb_buffer_get_glyph_infos(IntPtr buf, out uint length);
        [DllImport(Hb)] internal static extern IntPtr hb_buffer_get_glyph_positions(IntPtr buf, out uint length);
        [DllImport(Hb)] internal static extern IntPtr hb_version_string();
        // ---- R1/T1d：覆盖查询 + 面元数据 + 面集合（**只用 HarfBuzz 自己**，不碰 provider 反射）----
        [DllImport(Hb)] internal static extern uint hb_face_count(IntPtr blob);
        // D-F1c/B2：覆盖位图所需的 4 个入口（`nm -D libharfbuzz.so.0` 实测全部导出 ✓）
        [DllImport(Hb)] internal static extern IntPtr hb_set_create();
        [DllImport(Hb)] internal static extern void hb_set_destroy(IntPtr set);
        [DllImport(Hb)] internal static extern void hb_face_collect_unicodes(IntPtr face, IntPtr set);
        [DllImport(Hb)] internal static extern int hb_set_next(IntPtr set, ref uint codepoint);
        [DllImport(Hb)] internal static extern IntPtr hb_face_reference_table(IntPtr face, uint tag);
        [DllImport(Hb)] internal static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);
        [DllImport(Hb)] internal static extern uint hb_ot_name_get_utf8(IntPtr face, uint nameId, IntPtr language,
                                                                     ref uint textSize, byte* text);
        [DllImport(Hb)] internal static extern int hb_font_get_nominal_glyph(IntPtr font, uint unicode, out uint glyph);
        [DllImport(Hb)] internal static extern void hb_buffer_destroy(IntPtr buf);
        [DllImport(Hb)] internal static extern void hb_font_destroy(IntPtr font);
        [DllImport(Hb)] internal static extern void hb_face_destroy(IntPtr face);
        [DllImport(Hb)] internal static extern void hb_blob_destroy(IntPtr blob);

        [StructLayout(LayoutKind.Sequential)]
        private struct GlyphInfo { public uint Codepoint, Mask, Cluster, Var1, Var2; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GlyphPosition { public int XAdvance, YAdvance, XOffset, YOffset; public uint Var; }

        internal static string Version => Marshal.PtrToStringUTF8(hb_version_string()) ?? "?";

        /// <summary>
        /// 对 <paramref name="text"/> 做一次完整 shaping（GSUB + GPOS）。
        ///
        /// ⚠️ 2026-09-11（T1b）修正：**必须用 `hb_buffer_add_utf16`**。
        ///    原实现用 `hb_buffer_add_utf8` + `Bytes.Length`，于是 HarfBuzz 返回的 cluster 是
        ///    **UTF-8 字节偏移**，却被当成 UTF-16 码元下标去求逆（`InvertClusters`）——
        ///    纯 ASCII 文本看不出来，只要出现一个 3 字节字符（如原型里的 `—`）后面全错位；
        ///    73 例语料全是 CJK ⇒ 这个错会让 B2 整体失准。
        /// </summary>
        internal static HbShapedRun Shape(string fontPath, string text, double emSize, string language = "zh-cn")
            => Shape(fontPath, 0, text, emSize, language);

        /// <summary>
        /// 同上，但**显式指定 TTC 面下标**（R1/T1d）。
        ///
        /// 【为什么面下标必须能传进来】实测：`NotoSansCJK-Regular.ttc` 的 face0(JP) 与 face2(SC)
        ///   对 `文` 给出**不同** glyph id（20035 vs 20036）、`漢` 24227 vs 58935
        ///   ⇒ "路径 + 面下标"合起来才是**一份面**；只传路径就等于偷偷钉死 face0，
        ///   而渲染侧按 `GlyphTypeface`（它自带面下标）取面 ⇒ 两边一旦不同就是"数对了、字错了"。
        /// </summary>
        internal static HbShapedRun Shape(string fontPath, int faceIndex, string text, double emSize, string language = "zh-cn")
        {
            if (string.IsNullOrEmpty(text)) return HbShapedRun.Empty(emSize);   // 空行：不发 HB 调用

            byte[] pathZ = Encoding.UTF8.GetBytes(fontPath + "\0");
            byte[] langZ = Encoding.UTF8.GetBytes((language ?? "zh-cn") + "\0");

            IntPtr blob = IntPtr.Zero, face = IntPtr.Zero, font = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                fixed (byte* p = pathZ) blob = CreateBlobFromFile(p);
                if (blob == IntPtr.Zero || hb_blob_get_length(blob) == 0)
                    throw new InvalidOperationException("HarfBuzz 读不到字体：" + fontPath);

                face = hb_face_create(blob, (uint)faceIndex);
                uint upem = hb_face_get_upem(face);
                font = hb_font_create(face);
                hb_ot_font_set_funcs(font);
                hb_font_set_scale(font, (int)upem, (int)upem);      // advance 以 font unit 返回

                buf = hb_buffer_create();
                fixed (char* t = text) hb_buffer_add_utf16(buf, t, text.Length, 0, text.Length);
                hb_buffer_guess_segment_properties(buf);
                bool rtlDir = hb_buffer_get_direction(buf) == HB_DIRECTION_RTL;   // 修法①：方向来自 HB 自己
                // U1 的硬要求：**必须把 language 传进 buffer**（不给 ⇒ SC/TC 面回落 JP 字形）。
                fixed (byte* lz = langZ) hb_buffer_set_language(buf, hb_language_from_string(lz, -1));
                hb_shape(font, buf, IntPtr.Zero, 0);

                IntPtr infos = hb_buffer_get_glyph_infos(buf, out uint n);
                IntPtr pos = hb_buffer_get_glyph_positions(buf, out uint n2);
                if (infos == IntPtr.Zero || pos == IntPtr.Zero || n != n2)
                    throw new InvalidOperationException("HarfBuzz shaping 失败");
                if (n == 0) return HbShapedRun.Empty(emSize);

                double scale = emSize / upem;
                var r = new HbShapedRun
                {
                    Glyphs = new ushort[n],
                    Clusters = new uint[n],
                    AdvancesPx = new double[n],
                    OffsetsXPx = new double[n],
                    Upem = upem,
                    EmSize = emSize,
                    Text = text,
                };
                int szI = Marshal.SizeOf<GlyphInfo>(), szP = Marshal.SizeOf<GlyphPosition>();
                for (int i = 0; i < (int)n; i++)
                {
                    GlyphInfo gi = Marshal.PtrToStructure<GlyphInfo>(infos + i * szI);
                    r.Glyphs[i] = (ushort)gi.Codepoint;
                    r.Clusters[i] = gi.Cluster;
                    GlyphPosition gp = Marshal.PtrToStructure<GlyphPosition>(pos + i * szP);
                    r.AdvancesPx[i] = gp.XAdvance * scale;
                    r.OffsetsXPx[i] = gp.XOffset * scale;
                }
                // ⭐ T1d 修法①（2026-09-13，主控批准）：**RTL run 以逻辑序交给 GlyphRun**。
                //
                // 【为什么必须反转】HarfBuzz 对 RTL 返回的是**视觉序**（cluster 单调**递减**：
                //   实测 `'שלום עולם'` ⇒ clusters=[8,7,6,5,4,3,2,1,0]）。而 WPF 的 `GlyphRun` 契约要求
                //   `ClusterMap` 个数==字符数、**[0]==0、单调不减**、且每个值 < GlyphCount
                //   （校验见 `GlyphRun.cs:368-395`）；对视觉序数组**任何忠实的字符→字形映射都是递减的**
                //   ⇒ 根本无法满足契约，旧实现于是产出 `[0,0,0,0,0,0,0,8,8]`（**静默错**：7 个字符映射到 glyph 0）。
                //   转成逻辑序后 cluster 单调不减，`InvertClusters` 原样即可给出忠实映射。
                // 【与宿主镜像的关系】宿主对 RTL 段落会传 `InvertAxes.Horizontal`
                //   （`MS/Internal/Text/Line.cs:79/116`），我们 `Draw` 按上游契约 push `x'=段落宽−x`。
                //   上游 `SimpleTextLine` 交的就是**逻辑序** run ⇒ 由镜像产生视觉 RTL。我们也交逻辑序
                //   ⇒ 语义对齐（改前是"视觉序 + 再镜像" = **两次反转**）。
                // 【BidiLevel】**不给**方向：`BuildGlyphRun` 照旧传 0（理由见该处注释——上游同款路径也是 0，
                //   给 1 会让 `ComputeInkBoundingBox` 走 RTL 分支、而渲染侧不读它 ⇒ 账本与实画不自洽）。
                // ⚠️ 只影响**该段的顺序**：advance 之和、`CharAdvances()`（按 cluster 值归并）、
                //   断行填宽、记账**全都不变**（LTR 下这段恒不执行 ⇒ 逐位不变）。
                if (rtlDir && r.Glyphs.Length > 1)
                {
                    Array.Reverse(r.Glyphs);
                    Array.Reverse(r.Clusters);
                    Array.Reverse(r.AdvancesPx);
                    Array.Reverse(r.OffsetsXPx);
                    r.Rtl = true;
                }
                else if (rtlDir)
                {
                    r.Rtl = true;                 // 单字形 RTL：顺序无从反转，但方向要带下去
                }

                ApplyTabStops(r, text, font, 0, 4.0 * r.EmSize);   // 先用框架默认填；`FormatLine` 会用真实 tabInterval/Indent 覆盖（幂等）
                return r;
            }
            finally
            {
                if (buf != IntPtr.Zero) hb_buffer_destroy(buf);
                if (font != IntPtr.Zero) hb_font_destroy(font);
                if (face != IntPtr.Zero) hb_face_destroy(face);
                if (blob != IntPtr.Zero) DestroyBlob(blob);
            }
        }

        /// <summary>
        /// **真机 Tab 口径**（U1 oracle `src/Linux/tests/parity/windows/tab/` 114 例实测）：
        ///   · **A. 默认停靠位间隔 = `4 × emSize`** —— 这就是上游 `TextParagraphProperties.DefaultIncrementalTab`
        ///     的**框架默认实现**（`TextParagraphProperties.cs:111-114`：`4 * DefaultTextRunProperties.FontRenderingEmSize`）；
        ///     实测 12→48.000 / 24→96.000 / 48→192.000；
        ///   · **B. 网格锚在"行原点"**：`Indent` 只移动文本起点、**不移动网格**（`a\tb` + Indent=24 ⇒ 停靠位仍是 96 而非 120）；
        ///   · **C. 前进到"严格大于当前笔位的下一个网格倍数"**；**不受宽度约束影响**（不缩短、不换行，行照常溢出）；
        ///   · **方向无关**：RTL 的"锚右边缘 / 向左前进"由宿主那次镜像完成（修法② 契约；实测 `a\tb\tc`@RTL
        ///     到达 `107.997 / 11.997 = 204−96 / 204−192`）⇒ 本函数一律用"从行原点向右"的算术。
        /// 【为什么旧的 34 例是 0 宽】`layout-b34` 那套 harness 把 `DefaultIncrementalTab` **写死成 0**
        ///   （`src/Linux/tests/parity/windows/layout-b34/src/LayoutOracle/TextModel.cs:167`）⇒ 那 34 例真值就是"无停靠位"。
        ///   ⇒ 本函数用 `tabInterval` 表达这条路：`<= 0` ⇒ 显式无停靠位（advance 0，复现旧语料）；
        ///     `NaN`（未给）⇒ 框架默认 `4 × em`（新 oracle 与真机应用路径）。
        /// `startPenX` = 本 run 起点相对**行原点**的笔位（单面路径由 `FormatLine` 传 `Indent`；多面路径传累积笔位）。
        /// 幂等：每次都从笔位重算并**覆盖** tab 的 advance ⇒ 反复调用（先默认后真值）结果仍正确。
        /// </summary>
        /// <summary>
        /// **D-T1 具名判据 ①**：这个 tab 是不是"本行的第一个东西"（= U1 `Q5` 验证的那一支）。
        /// U1 `tab-anchor-oracle.txt:127-166`（`Q5_line_start_predicate_with_Indent`）原文：
        ///   "The predicate is therefore judged against the INDENTED CONTENT START, not the absolute line origin:
        ///    at Indent = 24 the tab's pen is 24, not 0, yet it still takes the line-start (clamp) branch. …
        ///    A predicate written as `pen == 0` takes the wrong branch for every line with Indent > 0."
        /// 证据：`definitelyClampedSamplesWithIndent = 26` + 恰好压边 `4`。
        /// `penX` = 该 tab 相对**行原点**的笔位；`contentStartX` = 本行内容起点相对行原点的笔位
        /// （段落首行 = `Indent`，其余行 = 0）。**所有"是否行首"的判定只许经过本函数。**
        /// </summary>
        internal static bool IsLineStartTab(double penX, double contentStartX)
            => penX <= contentStartX + 1e-9;

        /// <summary>
        /// **D-T1 具名判据 ②**：钳位目标（行盒内坐标）。U1 `Q6`（`tab-anchor-oracle.txt:167-252`）：
        ///   "the clamped tab's box span is [Indent, container - ParagraphIndent] … its ADVANCE WIDTH is
        ///    (container - ParagraphIndent - Indent) while its FAR EDGE is the line box's far edge."
        /// ⇒ 目标就是**行盒远缘**；笔位自内容起点（`Indent`）起算 ⇒ 等价于"把笔位钳到 `paragraphWidth`"。
        /// ⚠️ 后一等价以"`paragraphWidth` 已是 box 宽（= `container − ParagraphIndent`）"为前提，
        ///    而 `ParagraphIndent` **本机拿不到**（PC 侧从不接线）⇒ `TabClampInset` 恒 0 = **已登记的缺口**
        ///    （报告 §12.3：Q8 说 `ParagraphIndent` 会把网格整体右移，那条路今天没通）。
        /// </summary>
        internal static double TabClampWidthFor(bool wrap, double width, double paragraphIndent)
            => wrap ? width - TabClampInset(paragraphIndent) : double.PositiveInfinity;

        /// <summary>钳位目标里"要减掉的段落级缩进"。真值说该减 `ParagraphIndent`，但本机拿不到 ⇒ 恒 0（缺口已登记）。</summary>
        private static double TabClampInset(double paragraphIndent) => 0.0;

        internal static void ApplyTabStops(HbShapedRun r, string text, IntPtr font, double startPenX, double tabInterval,
                                           double clampWidth = double.PositiveInfinity,
                                           double lineContentStartX = double.NaN)
        {
            if (r == null || text == null || text.IndexOf('\t') < 0) return;
            bool zero = !(tabInterval > 0);
            double lineStart = double.IsNaN(lineContentStartX) ? startPenX : lineContentStartX;
            uint spaceGlyph = 0;
            bool haveSpace = font != IntPtr.Zero
                             && hb_font_get_nominal_glyph(font, 0x20u, out spaceGlyph) != 0 && spaceGlyph != 0;
            double pen = startPenX;
            for (int i = 0; i < r.Glyphs.Length; ++i)
            {
                uint cl = r.Clusters[i];
                if (cl < (uint)text.Length && text[(int)cl] == '\t')
                {
                    // 停靠位 = 严格大于当前笔位的下一个网格倍数；`tabInterval <= 0` ⇒ 显式"无停靠位"（advance 0）
                    double stop = zero ? pen : (Math.Floor(pen / tabInterval) + 1.0) * tabInterval;
                    double adv = stop - pen;
                    // D-T1：**越界才钳，且只在行首钳**；行中越界 ⇒ 不钳（advance 保持到 stop 的原值）
                    //   ⇒ 该候选宽度超行宽 ⇒ 自动失格 ⇒ 填宽循环回落到 `local` 里的 before-tab 候选 ⇒ 断在 tab 之前。
                    //   判据 `stop > clampWidth`（U1 Q6 的 box 坐标越界判据）**一字未改**，只改分支选择。
                    if (!double.IsInfinity(clampWidth))
                    {
                        // 波 `#16` 件 2：与测量侧同款（room 自内容起点起算）；两侧必须一致否则测量/整形再次错位。
                        // 件 2d：与测量侧同款（不再减 pen）。
                        double room = clampWidth - lineStart; if (room < 0) room = 0;
                        if (adv > room && IsLineStartTab(pen, lineStart)) adv = room;
                    }
                    r.AdvancesPx[i] = adv;
                    r.OffsetsXPx[i] = 0;
                    if (haveSpace) r.Glyphs[i] = (ushort)spaceGlyph;               // 控制字符无字形 ⇒ 借用空格
                    pen += adv;
                }
                else pen += r.AdvancesPx[i];
            }
        }

        // ------------------------------------------------------------------ 覆盖查询（R1/T1d）

        /// <summary>
        /// **这份面有没有该码点的字形**（R1/T1d 的覆盖判据）。
        ///
        /// 【为什么必须是 HarfBuzz 自己答】provider 的 `FamilyCoverageQuery.Covers/QueryCoverage/
        ///   TryFindFamilyCovering` 都是**扩展方法** ⇒ 反射里是 `FamilyCoverageQuery` 上的静态方法，
        ///   `typeof(LinuxFontFamily).GetMethod("Covers")` **永远是 null**；按实例方法探测会得到
        ///   一条"永远走降级、而所有读数都绿"的假路径。这里用 `hb_font_get_nominal_glyph != 0`
        ///   从根上绕开那个坑（实测 HB 2.7.4 符号存在）。
        ///
        /// ⚠️ 参数 <paramref name="emSize"/> **不参与判据**：nominal glyph 查询与字号无关
        ///   （advance 才与字号有关）。保留它只为与设计签名一致；覆盖面恒为 faceIndex 0
        ///   （要指定面下标请用 <see cref="CoversFace"/>）。
        /// </summary>
        internal static bool Covers(string fontPath, double emSize, int cp)
            => CoversFace(fontPath, 0, cp);

        /// <summary>同上，但显式指定 TTC 面下标（面 = 路径 + 下标，见 <see cref="Shape(string,int,string,double,string)"/>）。</summary>
        internal static bool CoversFace(string fontPath, int faceIndex, int cp)
        {
            bool cacheHit;
            bool covered = HbFaceCache.Covers(fontPath, faceIndex, cp, out cacheHit);
            HbFallbackDiag.NoteCoverageProbe(cacheHit);
            return covered;
        }

        /// <summary>该码点在这份面里的 glyph id（0 = 无字形 ⇒ `.notdef`）。</summary>
        internal static int NominalGlyph(string fontPath, int faceIndex, int cp)
            => HbFaceCache.NominalGlyph(fontPath, faceIndex, cp);
    }

    // ==================================================================================
    //  1b. ⭐ R1/T1d：run 级面 + 按码点覆盖回退 + 多字体整形（**共享层：不含任何 PC 类型**）
    // ==================================================================================
    //
    //  【它解决的问题】一段文本里出现"当前面没有字形"的码点（本工程：UI 面是拉丁面，
    //    中文全部落 `.notdef`(id 0)）时，按**码点段**换一个"覆盖得上的面"来整形。
    //
    //  【红线（本项目用血换来的）】
    //    ① 覆盖判据**只用 HarfBuzz 自己**（`hb_font_get_nominal_glyph != 0`）：provider 的
    //       `FamilyCoverageQuery.Covers/QueryCoverage/TryFindFamilyCovering` 都是**扩展方法**，
    //       反射里是静态方法 ⇒ `typeof(LinuxFontFamily).GetMethod("Covers")` **恒为 null**，
    //       按实例方法探测会得到"永远走降级、而所有读数都绿"的假路径。
    //    ② 选面粒度 = **码点段**，绝不按段落整体换族（否则就是把问题从中文挪到拉丁）。
    //    ③ 挑面按**覆盖**；同一覆盖下才按 OS/2 的 (字重,拉伸,斜体) 就近 —— **不按名字/文件名**。
    //    ④ 找不到覆盖面 ⇒ **沿用当前面**并计数（`FallbackFailed`），绝不假装成功。
    //    ⑤ 断行规则与行记账**一行不动**：这里只换"advance 由哪份面提供"。
    // ==================================================================================

    /// <summary>
    /// 一份**具体的面** = 字体文件 + TTC 内的面下标。
    ///
    /// ⚠️ 只传路径是不够的：实测 `NotoSansCJK-Regular.ttc` 的 face0(JP) 与 face2(SC) 对
    ///    `文`(U+6587) 给出**不同** glyph id（20035 vs 20036）、`漢` 24227 vs 58935
    ///    ⇒ 整形用的面下标必须与**渲染**用的面下标一致，否则就是"数对了、字错了"。
    /// </summary>
    internal sealed class HbFaceRef : IEquatable<HbFaceRef>
    {
        internal HbFaceRef(string path, int faceIndex)
        {
            Path = path ?? string.Empty;
            FaceIndex = faceIndex;
        }

        public string Path { get; }
        public int FaceIndex { get; }

        public bool Equals(HbFaceRef other)
            => other != null && other.FaceIndex == FaceIndex && string.Equals(other.Path, Path, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as HbFaceRef);
        public override int GetHashCode() => Path.GetHashCode() ^ (FaceIndex * 397);
        public override string ToString() => Path + "#" + FaceIndex;
    }

    /// <summary>多字体整形的一段结果：字符起点（**本段文本内局部**）+ 该段独立 shape 的结果。</summary>
    internal sealed class HbShapedChunk
    {
        /// <summary>本段在"这次整形的那串文本"里的字符起点（UTF-16 码元下标）。</summary>
        public int CharStart;

        /// <summary>本段的 shape 结果（`Clusters` 是**本段局部**下标 ⇒ 正好可以直接给 `GlyphRun`）。</summary>
        public HbShapedRun Run;

        /// <summary>对应 <see cref="HbFontSegment.FaceSlot"/>（= 调用方那张 `GlyphTypeface[]` 的下标）。</summary>
        public int SegmentIndex = -1;

        /// <summary>这一段属于哪个源 run（→ 用哪个 run 的 foreground）。</summary>
        public int RunSlot = -1;
    }

    /// <summary>一段连续文本 + 它用的面（`End` 不含）。</summary>
    internal sealed class HbFontSegment
    {
        public int Start;
        public int End;
        public HbFaceRef Face;

        /// <summary>这一段的面是"按码点覆盖补出来的"（不是 run 原生面）。</summary>
        public bool Fallback;

        /// <summary>
        /// 调用方那张 `GlyphTypeface[]`（渲染面）里的槽位；-1 = 没有（用行自身那个面）。
        /// ⚠️ 必须与 <see cref="Start"/>/<see cref="End"/> 分开保存：`Sub()` 会裁剪/合并段，
        ///   槽位是**身份**，不能随裁剪重编号。
        /// </summary>
        public int FaceSlot = -1;

        /// <summary>
        /// 切出这一段时 run 的 (字重,拉伸,斜体)（OS/2 语义）。
        /// ⚠️ 必须跟着段走：同一族名下的 Regular/Bold 是**不同文件**，
        ///   物化渲染面时若用错三要素会解析到另一个文件 ⇒ 身份校验失败 ⇒ 白回退。
        /// </summary>
        public int Weight = 400, Width = 5, Slant = 0;

        /// <summary>
        /// 这一段**属于哪个源 run**（`HbRunFaceInfo.RunSlot`）。
        /// ⚠️ 与 `FaceSlot` 是两件事：面可以被回退换掉，但**前景色永远跟着源 run 走**
        ///   （上游 `SimpleTextLine.cs:1740` 在 `SimpleRun.Draw` 内取**本 run** 的 `ForegroundBrush`）。
        /// </summary>
        public int RunSlot = -1;

        public override string ToString()
            => "[" + Start + "," + End + ") " + Face + (Fallback ? " (按码点回退)" : "") + (FaceSlot >= 0 ? " slot=" + FaceSlot : "")
               + (RunSlot >= 0 ? " run=" + RunSlot : "");
    }

    /// <summary>
    /// 一段文本的"面计划"：**只含路径/面下标/槽位**（无 PC 类型 ⇒ 断行引擎与各 harness 都能用）。
    /// </summary>
    internal sealed class HbFontPlan
    {
        public readonly List<HbFontSegment> Segments = new List<HbFontSegment>();

        /// <summary>计划对应的文本长度（一致性自检用；`Sub` 之后是切片长度）。</summary>
        public int TextLength = -1;

        public bool IsSingleFace => Segments.Count <= 1;

        /// <summary>
        /// 裁剪到 <c>[start,end)</c> 并**重定基到 0**：返回的计划描述的是 <c>text.Substring(start, end-start)</c>
        /// 这串**局部下标**文本（调用方就是这么用它的：`Visible = text.Substring(...)` 或 `para`）。
        ///
        /// ⚠️ **必须重定基**（实测踩到的严重缺陷）：第一版只裁剪、不重定基 ⇒ 第 2 行以后
        ///    `e = min(段末, 行末) &lt; s = max(段首, 0)` ⇒ 段被整段跳过 ⇒ **0 字形、Width=0**
        ///   （行"消失"）。单字体入口看不见这个（它不走计划）⇒ 只有 `CoverageProbe` 抓到了。
        /// 只改区间，**不改 <see cref="HbFontSegment.FaceSlot"/>/面身份**。
        /// </summary>
        internal HbFontPlan Sub(int start, int end)
        {
            var p = new HbFontPlan { TextLength = Math.Max(0, end - start) };
            foreach (HbFontSegment s in Segments)
            {
                int a = Math.Max(s.Start, start), b = Math.Min(s.End, end);
                if (b <= a) continue;
                a -= start;                                   // ← 重定基
                b -= start;
                HbFontSegment last = p.Segments.Count > 0 ? p.Segments[p.Segments.Count - 1] : null;
                if (last != null && last.End == a && last.Fallback == s.Fallback && last.FaceSlot == s.FaceSlot
                    && last.RunSlot == s.RunSlot && last.Face.Equals(s.Face))
                    last.End = b;
                else
                    p.Segments.Add(new HbFontSegment
                    {
                        Start = a, End = b, Face = s.Face, Fallback = s.Fallback, FaceSlot = s.FaceSlot,
                        Weight = s.Weight, Width = s.Width, Slant = s.Slant, RunSlot = s.RunSlot,
                    });
            }
            return p;
        }

        /// <summary>诊断串（一行，可 grep）。</summary>
        internal string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("segments=").Append(Segments.Count);
            for (int i = 0; i < Segments.Count; ++i)
            {
                sb.Append(' ').Append(i).Append(':').Append(Segments[i]);
                if (i >= 7) { sb.Append(" …(共 ").Append(Segments.Count).Append(" 段)"); break; }
            }
            return sb.ToString();
        }
    }

    /// <summary>多字体整形：**各子段分别 Shape，再拼回同一个 <see cref="HbShapedRun"/></b>。</summary>
    internal static class HbMultiFontShaper
    {
        /// <summary>
        /// 按面计划整形 <paramref name="text"/>。产出仍是**一个** `HbShapedRun`：
        /// glyphs/advances/offsets **顺序拼接**，cluster **平移回这次文本的局部下标**（`+ 段起点`）。
        /// ⇒ `CharAdvances()` / 断行填宽 / 行记账（Length 含硬断、NewlineLength、行尾空白）**一行都不用改**。
        /// </summary>
        internal static HbShapedRun ShapeParagraph(HbFontPlan plan, string text, double emSize, string language = "zh-cn",
                                                    double startPenX = 0, double tabInterval = double.NaN,
                                                    double clampWidth = double.PositiveInfinity,
                                                    double lineContentStartX = double.NaN)
        {
            text = text ?? string.Empty;
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.TextLength >= 0 && plan.TextLength != text.Length)
                throw new InvalidOperationException(
                    "面计划与文本长度不一致（plan=" + plan.TextLength + " text=" + text.Length + "）—— "
                    + "这会让子段与文本错位（宁可响亮失败，也不要静默画错字）");

            var chunks = new List<HbShapedChunk>();
            var glyphs = new List<ushort>();
            var clusters = new List<uint>();
            var advances = new List<double>();
            var offsets = new List<double>();
            uint upem = 0;

            double tab = double.IsNaN(tabInterval) ? 4.0 * emSize : tabInterval;   // NaN ⇒ 框架默认 4×em
            double segPen = startPenX;
            foreach (HbFontSegment seg in plan.Segments)
            {
                int s = Math.Max(0, seg.Start), e = Math.Min(text.Length, seg.End);
                if (e <= s) continue;
                string sub = text.Substring(s, e - s);
                HbShapedRun r = HbShaper.Shape(seg.Face.Path, seg.Face.FaceIndex, sub, emSize, language);
                if (r.Glyphs.Length == 0) continue;                    // 空段：不发 GlyphRun（但也不改变顺序）
                // ★ Tab 网格：本段起点相对**行原点**的笔位才算得对（`Shape` 里按 0 算的那次会被这里覆盖，幂等）
                HbShaper.ApplyTabStops(r, sub, IntPtr.Zero, segPen, tab, clampWidth, lineContentStartX);
                segPen += r.TotalWidthPx;
                if (upem == 0) upem = r.Upem;
                for (int i = 0; i < r.Glyphs.Length; ++i)
                {
                    glyphs.Add(r.Glyphs[i]);
                    clusters.Add(r.Clusters[i] + (uint)s);              // ← 平移回局部下标
                    advances.Add(r.AdvancesPx[i]);
                    offsets.Add(r.OffsetsXPx[i]);
                }
                chunks.Add(new HbShapedChunk
                {
                    CharStart = s, Run = r, SegmentIndex = seg.FaceSlot, RunSlot = seg.RunSlot,
                });
            }

            return new HbShapedRun
            {
                Glyphs = glyphs.ToArray(),
                Clusters = clusters.ToArray(),
                AdvancesPx = advances.ToArray(),
                OffsetsXPx = offsets.ToArray(),
                Upem = upem,
                EmSize = emSize,
                Text = text,
                Chunks = chunks,
                Rtl = chunks.Count == 1 && chunks[0].Run.Rtl,   // 多段混合方向时行级标记无意义（每段各自带）
            };
        }
    }

    /// <summary>
    /// 面的缓存（HarfBuzz 侧）：`(路径, 面下标) → hb_face/hb_font` + **按 (面, 码点) 的覆盖缓存**。
    /// 上限策略同 §10：满了**整表清空**（不是 LRU），并计数。
    /// </summary>
    internal static unsafe class HbFaceCache
    {
        internal sealed class Entry
        {
            public string Path;
            public int FaceIndex;
            public IntPtr Face;
            public IntPtr Font;
            public uint Upem;
            public int GlyphCount;
            public Dictionary<int, bool> Cover;                 // cp → 有没有字形（本面的覆盖缓存）
            public bool MetaLoaded;
            public string Family = "?";
            public int Weight = 400, Width = 5, Slant = 0;
            public override string ToString() => Path + "#" + FaceIndex + " family=" + Family;
        }

        private const int MaxFaces = 512;
        private const int MaxCoverEntries = 262144;
        private static readonly Dictionary<string, Entry> s_faces = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> s_faceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly object s_gate = new object();
        private static long s_coverEntries;

        internal static long FaceLoads, FaceLoadFailures, Evictions, CoverClears;
        // D-F1c3 只读计数（2026-09-15 主控窗口 4）：Release 成功移除的次数；常驻面数（精确）。
        internal static long ReleaseCalls;
        internal static int ResidentCount { get { lock (s_gate) return s_faces.Count; } }

        // ---- D-F1c4（窗口 5，主控放行）：**按文件共享一份 blob** ----
        //   实测（`~/wfp-runs/w5-mmap-probe.log`）：一个 10 面的 `.ttc`（19.1 MB）
        //     · 一文件一 blob + 逐面建 face ⇒ 触碰 19.1 MB 只增 4 MB，face+blob 都销毁后**回到基准（Δ=0）**
        //     · 逐面各建一份 blob ⇒ +192 MB，且 face 未销毁时**不回落**（`hb_face_create` 确实持 blob 引用）
        //   ⇒ 一个文件**一份映射**；处理完该文件**立即销毁**（下一个文件重新建）。
        private static string s_fileBlobPath;
        private static IntPtr s_fileBlob;

        // ---- 窗口 10（主控派单）：**按 `path` 共享一份 blob + 引用计数** ----
        //   症状（T2 用 `--mode=nofb` 差分出来的硬读数）：系统档 `NotoSansCJK-Bold.ttc` **11 段**，
        //   而关掉 shim 回退扫描（`nofb`）后只 **1 段** ⇒ 多出来的 10 段 = 该 10 面 `.ttc` **每个面各一份整文件映射**。
        //   根因就在下面的 `Load()`：它**逐面** `CreateBlobFromFile` 建整文件 blob，`finally` 只丢**我们那一份引用**
        //   （注释"face 自己持有一份引用"）；只要 Entry 还驻留（`s_faces` 上限 512、本机 371 面 ⇒ **永不淘汰**）
        //   ⇒ 每个驻留面各持一份**整文件**映射 ⇒ 面数就是映射数。
        //   修法：同一个 `path` 只建**一份** blob；`hb_face_create(sharedBlob, faceIndex)` 对集合面只建
        //   **同一 blob 的子面**（不新开映射）；最后一个引用释放时才 `hb_blob_destroy`。
        //   判据：系统档 `null` 46 → ≈26 段、1CJK 该 ttc 3 → 2 段，且**渲染/排版读数一律不变**。
        private sealed class SharedBlob
        {
            public IntPtr Blob;
            public int Refs;
        }
        private static readonly Dictionary<string, SharedBlob> s_sharedBlobTable =
            new Dictionary<string, SharedBlob>(StringComparer.Ordinal);

        // 只读计数（窗口 10）：共享 blobs 的**当前份数**/累计获取/累计释放（"谁在按面建映射"一眼可判）
        internal static long SharedBlobAcquires, SharedBlobReleases, SharedBlobFilePeak, SharedBlobCreated;
        internal static int SharedBlobFiles { get { lock (s_gate) return s_sharedBlobTable.Count; } }

        /// <summary>取该文件的共享 blob（没有就建一份），引用计数 +1。返回零 = 建不出来。</summary>
        internal static IntPtr AcquireSharedBlob(string path)
        {
            if (string.IsNullOrEmpty(path)) return IntPtr.Zero;
            lock (s_gate)
            {
                SharedBlob sb;
                if (s_sharedBlobTable.TryGetValue(path, out sb)) { ++sb.Refs; ++SharedBlobAcquires; return sb.Blob; }
                IntPtr blob;
                byte[] pathZ = Encoding.UTF8.GetBytes(path + "\0");
                fixed (byte* pz = pathZ) blob = HbShaper.CreateSharedBlobFromFile(pz);
                if (blob == IntPtr.Zero) return IntPtr.Zero;
                s_sharedBlobTable[path] = new SharedBlob { Blob = blob, Refs = 1 };
                ++SharedBlobAcquires; ++SharedBlobCreated;
                if (s_sharedBlobTable.Count > SharedBlobFilePeak) SharedBlobFilePeak = s_sharedBlobTable.Count;
                return blob;
            }
        }

        /// <summary>引用计数 -1；**归零才真正销毁**那份映射（还有驻留面持有时不销毁 —— 这正是窗口 10 的目的）。</summary>
        internal static void ReleaseSharedBlob(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            IntPtr doomed = IntPtr.Zero;
            lock (s_gate)
            {
                SharedBlob sb;
                if (!s_sharedBlobTable.TryGetValue(path, out sb)) return;
                ++SharedBlobReleases;
                if (--sb.Refs > 0) return;
                doomed = sb.Blob;
                s_sharedBlobTable.Remove(path);
            }
            if (doomed != IntPtr.Zero) HbShaper.DestroyBlob(doomed);
        }

        /// <summary>处理某个文件前调用：取得该文件的**唯一**那份 blob（引用计数 +1）。</summary>
        internal static IntPtr OpenFileBlob(string path)
        {
            CloseFileBlob();
            s_fileBlob = AcquireSharedBlob(path);
            s_fileBlobPath = (s_fileBlob != IntPtr.Zero) ? path : null;
            return s_fileBlob;
        }

        /// <summary>处理完该文件后调用：**引用计数 -1**（若还有驻留面，映射**不**销毁）。</summary>
        internal static void CloseFileBlob()
        {
            if (s_fileBlobPath != null) ReleaseSharedBlob(s_fileBlobPath);
            s_fileBlob = IntPtr.Zero;
            s_fileBlobPath = null;
        }

        /// <summary>D-F1c4：**只读元数据**——用**共享 blob** 建 face、读 3 张表、**立即销毁 face**；
        ///   不建 font、不写 `s_faces`（blob 由调用方按文件销毁 ⇒ 峰值 = 单文件量级）。</summary>
        internal static bool LoadMetaOnly(string path, int faceIndex, IntPtr blob,
                                          out string family, out int weight, out int width, out int slant)
        {
            family = null; weight = 400; width = 5; slant = 0;
            if (blob == IntPtr.Zero) return false;
            IntPtr face = HbShaper.hb_face_create(blob, (uint)faceIndex);
            if (face == IntPtr.Zero) return false;
            try
            {
                var tmp = new Entry { Path = path, FaceIndex = faceIndex, Face = face, Font = IntPtr.Zero,
                                      Upem = HbShaper.hb_face_get_upem(face) };
                LoadMeta(tmp);                                  // 复用既有表读取器（hb_face_reference_table）
                family = tmp.Family; weight = tmp.Weight; width = tmp.Width; slant = tmp.Slant;
                return true;
            }
            catch (Exception) { return false; }
            finally { HbShaper.hb_face_destroy(face); }         // **先 face**（blob 由调用方按文件销毁）
        }

        /// <summary>D-F1c/B1：把某个 (path,face) **立刻从缓存里释放**（销毁句柄）——扫描期用，避免 O(系统面数) 驻留。</summary>
        internal static void Release(string path, int faceIndex)
        {
            lock (s_gate)
            {
                string key = path + "\u0000" + faceIndex;
                Entry e;
                if (!s_faces.TryGetValue(key, out e)) return;
                s_faces.Remove(key);
                System.Threading.Interlocked.Increment(ref ReleaseCalls);
                if (e != null)
                {
                    if (e.Font != IntPtr.Zero) HbShaper.hb_font_destroy(e.Font);
                    if (e.Face != IntPtr.Zero) HbShaper.hb_face_destroy(e.Face);
                    e.Font = IntPtr.Zero; e.Face = IntPtr.Zero;
                    ReleaseSharedBlob(e.Path);      // 窗口 10：**最后一个面**释放时那份映射才销毁
                }
            }
        }

        internal static Entry Get(string path, int faceIndex)
        {
            if (string.IsNullOrEmpty(path) || faceIndex < 0) return null;
            lock (s_gate)
            {
                Entry hit;
                if (s_faces.TryGetValue(path + "\u0000" + faceIndex, out hit)) return hit;
                if (s_faces.Count >= MaxFaces) EvictLocked();
                Entry e = Load(path, faceIndex);
                if (e == null) { ++FaceLoadFailures; return null; }
                s_faces[path + "\u0000" + faceIndex] = e;
                ++FaceLoads;
                return e;
            }
        }

        private static void EvictLocked()
        {
            foreach (Entry e in s_faces.Values)
            {
                if (e.Font != IntPtr.Zero) HbShaper.hb_font_destroy(e.Font);
                if (e.Face != IntPtr.Zero) HbShaper.hb_face_destroy(e.Face);
                ReleaseSharedBlob(e.Path);          // 窗口 10：随 Entry 一起归还那份共享引用
            }
            s_faces.Clear();
            s_coverEntries = 0;
            ++Evictions;
        }

        private static void ClearCoverLocked()
        {
            foreach (Entry e in s_faces.Values) if (e.Cover != null) e.Cover.Clear();
            s_coverEntries = 0;
            ++CoverClears;
        }

        private static Entry Load(string path, int faceIndex)
        {
            Entry e = null;
            IntPtr blob = IntPtr.Zero;
            try
            {
                blob = AcquireSharedBlob(path);         // 窗口 10：同一文件**只有一份**映射（原：逐面各建一份整文件 blob）
                if (blob == IntPtr.Zero || HbShaper.hb_blob_get_length(blob) == 0) return null;
                IntPtr face = HbShaper.hb_face_create(blob, (uint)faceIndex);
                if (face == IntPtr.Zero) return null;
                IntPtr font = HbShaper.hb_font_create(face);
                if (font == IntPtr.Zero) { HbShaper.hb_face_destroy(face); return null; }
                HbShaper.hb_ot_font_set_funcs(font);
                e = new Entry
                {
                    Path = path, FaceIndex = faceIndex, Face = face, Font = font,
                    Upem = HbShaper.hb_face_get_upem(face),
                };
                return e;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                // Entry 建成功 ⇒ 那一份引用**随 Entry 走**（由 Release/EvictLocked/Reset 归还）；
                // 任何失败路径 ⇒ 在这里当场归还（否则引用计数会漏，映射永不释放）。
                if (e == null && blob != IntPtr.Zero) ReleaseSharedBlob(path);
            }
        }

        /// <summary>文件里有多少个面（TTC 可 >1）。0 = 读不出来。</summary>
        internal static int FaceCountOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            lock (s_gate)
            {
                int n;
                if (s_faceCounts.TryGetValue(path, out n)) return n;
                IntPtr blob = AcquireSharedBlob(path);       // 窗口 10：走共享表（同文件不再多开一份映射）
                try
                {
                    n = (blob == IntPtr.Zero || HbShaper.hb_blob_get_length(blob) == 0) ? 0 : (int)HbShaper.hb_face_count(blob);
                }
                catch (Exception) { n = 0; }
                finally { if (blob != IntPtr.Zero) ReleaseSharedBlob(path); }
                s_faceCounts[path] = n;
                return n;
            }
        }

        /// <summary>覆盖查询（带按 (面,码点) 的缓存）。<paramref name="cacheHit"/> 供计数器区分"问过"与"答过"。</summary>
        internal static bool Covers(string path, int faceIndex, int cp, out bool cacheHit)
        {
            cacheHit = false;
            if (cp < 0 || cp > 0x10FFFF) return false;
            Entry e = Get(path, faceIndex);
            if (e == null) return false;
            lock (s_gate)
            {
                bool v;
                if (e.Cover != null && e.Cover.TryGetValue(cp, out v)) { cacheHit = true; return v; }
                if (e.Cover == null) e.Cover = new Dictionary<int, bool>();
                uint g;
                v = HbShaper.hb_font_get_nominal_glyph(e.Font, (uint)cp, out g) != 0;
                if (s_coverEntries >= MaxCoverEntries) ClearCoverLocked();
                e.Cover[cp] = v;
                ++s_coverEntries;
                return v;
            }
        }

        /// <summary>该码点在这份面里的 glyph id；**-1 = 面不可用**（与 0 = `.notdef` 严格分开）。</summary>
        internal static int NominalGlyph(string path, int faceIndex, int cp)        {
            if (cp < 0 || cp > 0x10FFFF) return -1;
            Entry e = Get(path, faceIndex);
            if (e == null) return -1;
            lock (s_gate)
            {
                uint g;
                return HbShaper.hb_font_get_nominal_glyph(e.Font, (uint)cp, out g) != 0 ? (int)g : 0;
            }
        }

        /// <summary>族名 + OS/2 的 (字重,拉伸,斜体) —— 只在需要排序时才读（惰性）。</summary>
        /// <summary>族名（HB 的 name 表，id16 优先、回落 id1）。读不出来返回 null。</summary>
        internal static string FamilyOf(string path, int faceIndex)
        {
            Entry e = Get(path, faceIndex);
            if (e == null) return null;
            LoadMeta(e);
            return e.Family;
        }

        internal static void LoadMeta(Entry e)
        {
            if (e == null || e.MetaLoaded) return;
            lock (s_gate)
            {
                if (e.MetaLoaded) return;
                e.GlyphCount = CountGlyphs(e);
                e.Family = ReadName(e.Face, 16) ?? ReadName(e.Face, 1) ?? "?";
                ReadOs2(e.Face, out e.Weight, out e.Width, out e.Slant);
                e.MetaLoaded = true;
            }
        }

        private static int CountGlyphs(Entry e)
        {
            // maxp 的 numGlyphs（offset 4，uint16）；读不出来就是 0。
            IntPtr blob = HbShaper.hb_face_reference_table(e.Face, Tag('m', 'a', 'x', 'p'));
            if (blob == IntPtr.Zero) return 0;
            try
            {
                uint n; IntPtr p = HbShaper.hb_blob_get_data(blob, out n);
                if (p == IntPtr.Zero || n < 6) return 0;
                byte* b = (byte*)p;
                return (b[4] << 8) | b[5];
            }
            finally { HbShaper.DestroyBlob(blob); }
        }

        private static uint Tag(char a, char b, char c, char d)
            => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;

        private static string ReadName(IntPtr face, uint nameId)
        {
            // ⚠️ 协议坑（实测，2026-09-11）：`hb_ot_name_get_utf8` 在 `text == NULL` 时把
            //   "需要的字节数"当作**返回值**给出来，**不写 `*text_size`**（写进去的是 0）。
            //   第一版按 `*text_size` 判空 ⇒ 永远读不到族名 ⇒ 回退闸门把候选面全拒了
            //   （`fallbackFailed=60`）。这是**闸门正确工作**（不静默用错面）的一次真实拦截。
            uint ignored = 0;
            uint need = HbShaper.hb_ot_name_get_utf8(face, nameId, IntPtr.Zero, ref ignored, null);
            if (need == 0 || need > 4096) return null;
            var buf = new byte[need + 8];
            uint cap = (uint)buf.Length;
            fixed (byte* p = buf)
            {
                uint n = HbShaper.hb_ot_name_get_utf8(face, nameId, IntPtr.Zero, ref cap, p);
                if (n == 0) return null;
                int len = (int)Math.Min(n, cap);
                return Encoding.UTF8.GetString(buf, 0, len);
            }
        }

        private static void ReadOs2(IntPtr face, out int weight, out int width, out int slant)
        {
            weight = 400; width = 5; slant = 0;
            IntPtr blob = HbShaper.hb_face_reference_table(face, Tag('O', 'S', '/', '2'));
            if (blob == IntPtr.Zero) return;
            try
            {
                uint n; IntPtr p = HbShaper.hb_blob_get_data(blob, out n);
                if (p == IntPtr.Zero || n < 8) return;
                byte* b = (byte*)p;
                weight = (b[4] << 8) | b[5];
                width = (b[6] << 8) | b[7];
                if (n >= 64)
                {
                    ushort fs = (ushort)((b[62] << 8) | b[63]);
                    slant = (fs & 1) != 0 ? 2 : (((fs & 512) != 0) ? 1 : 0);
                }
            }
            finally { HbShaper.DestroyBlob(blob); }
        }

        internal static void Reset()
        {
            lock (s_gate)
            {
                EvictLocked();
                s_faceCounts.Clear();
                FaceLoads = FaceLoadFailures = Evictions = CoverClears = 0;
            }
        }
    }

    /// <summary>机器上的"候选面"清单（**目录口径与 `Factory.Linux.ResolveSystemFontDirectories` 同一套**）。</summary>
    internal static class HbFontCandidates
    {
        internal sealed class Cand
        {
            public string Path;
            public int FaceIndex;
            public string Family;
            public int Weight = 400, Width = 5, Slant = 0;
            public override string ToString() => Family + " " + Path + "#" + FaceIndex + " w=" + Weight;
        }

        private static List<Cand> s_all;
        /// <summary>D-F1c/B4：扫描面数上限（可观测；超限 ⇒ `NoteScanCapped`，出口 = `HB_TEXTLINE` 汇总行的 `scanCapped=`）。</summary>
        private const int MaxScanFaces = 4096;
        private static readonly object s_gate = new object();

        internal static long Scans;
        internal static string LastScanInfo = "-";
        internal static int Count { get { EnsureScan(); return s_all.Count; } }

        /// <summary>系统字体目录（**逐字对齐** `src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.Factory.Linux.cs:ResolveSystemFontDirectories`）。</summary>
        internal static string[] ResolveDirs()
        {
            string configured = System.Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR");
            if (!string.IsNullOrEmpty(configured))
            {
                var list = new List<string>();
                foreach (string part in configured.Split(new[] { ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    list.Add(part.Trim());
                if (list.Count > 0) return list.ToArray();
            }
            var c = new List<string>();
            string xdg = System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string home = System.Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(xdg)) c.Add(System.IO.Path.Combine(xdg, "fonts"));
            c.Add("/usr/share/fonts");
            c.Add("/usr/local/share/fonts");
            if (!string.IsNullOrEmpty(home))
            {
                c.Add(System.IO.Path.Combine(home, ".local", "share", "fonts"));
                c.Add(System.IO.Path.Combine(home, ".fonts"));
            }
            return c.ToArray();
        }

        internal static void EnsureScan()
        {
            lock (s_gate)
            {
                if (s_all != null) return;
                ++Scans;
                string[] dirs = ResolveDirs();
                var files = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
                    foreach (string pattern in new[] { "*.ttf", "*.otf", "*.ttc" })
                    {
                        try
                        {
                            foreach (string f in System.IO.Directory.EnumerateFiles(
                                         dir, pattern, System.IO.SearchOption.AllDirectories))
                                if (seen.Add(f)) files.Add(f);
                        }
                        catch (Exception) { /* 权限不足的子目录：跳过（provider 同款） */ }
                    }
                }
                files.Sort(StringComparer.Ordinal);

                var list = new List<Cand>();
                int failed = 0;
                foreach (string f in files)
                {
                    int n = HbFaceCache.FaceCountOf(f);
                    if (n <= 0) { ++failed; continue; }
                    // D-F1c4（窗口 5）：**一个文件一份映射** —— 打开该文件的唯一 blob，逐面读元数据（face 用完即销毁），
                    //   整文件读完**立即销毁 blob**；下一个文件重新建。⇒ 峰值 = 单文件量级，而不是 Σ(文件×面数)。
                    IntPtr fileBlob = HbFaceCache.OpenFileBlob(f);
                    try
                    {
                        for (int i = 0; i < n; ++i)
                        {
                            string fam; int wt, wd, sl;
                            if (!HbFaceCache.LoadMetaOnly(f, i, fileBlob, out fam, out wt, out wd, out sl))
                            { ++failed; continue; }
                            list.Add(new Cand
                            {
                                Path = f, FaceIndex = i, Family = fam,
                                Weight = wt, Width = wd, Slant = sl,
                            });
                            // D-F1c/B4：扫描上限（可观测：超限记数；出口 = `HB_TEXTLINE` 汇总行的 `scanCapped=`）
                            if (list.Count >= MaxScanFaces) { HbFallbackDiag.NoteScanCapped(); break; }
                        }
                    }
                    finally { HbFaceCache.CloseFileBlob(); }       // **按文件销毁**（face 已在上面的 finally 里销毁）
                }
                s_all = list;
                LastScanInfo = "目录=[" + string.Join(",", dirs) + "] 文件=" + files.Count
                             + " 面=" + list.Count + " 读不出=" + failed;
            }
        }

        /// <summary>
        /// 挑一个**覆盖 <paramref name="cp"/>** 的面：主键 = 覆盖（HB 自己答），
        /// 同覆盖下按 `(字重距离, 拉伸距离, 斜体距离, 路径 Ordinal, 面下标)` 取最小。
        /// **不按名字挑** —— 名字只用于诊断输出。
        /// </summary>
        /// <summary>D-F1c/B2：每面 Unicode 覆盖集合（**不持句柄**）。上限 32 个面，超出即整体清空（有界内存）。</summary>
        private static readonly Dictionary<string, HashSet<int>> s_coverCache =
            new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        private const int MaxCoverCache = 32;

        private static bool CoversCpByBitmap(string path, int faceIndex, int cp)
        {
            string key = path + "\u0000" + faceIndex;
            lock (s_gate)
            {
                HashSet<int> hit;
                if (s_coverCache.TryGetValue(key, out hit)) return hit.Contains(cp);
            }
            HashSet<int> set = new HashSet<int>();
            IntPtr blob = IntPtr.Zero, face = IntPtr.Zero, hs = IntPtr.Zero;
            try
            {
                blob = HbFaceCache.AcquireSharedBlob(path);   // 窗口 10：走共享表（同文件不再多开一份映射）
                if (blob == IntPtr.Zero) return false;
                face = HbShaper.hb_face_create(blob, (uint)faceIndex);
                if (face == IntPtr.Zero) return false;
                hs = HbShaper.hb_set_create();
                if (hs == IntPtr.Zero) return false;
                HbShaper.hb_face_collect_unicodes(face, hs);
                uint c = 0xFFFFFFFFu;
                // ⚠️ `hb_set_next` 的游标是 **in/out**：它自己推进；**循环体内绝不能重置**（首版每轮重置 ⇒
                //   每轮都要求"取第一个" ⇒ 永远同一个值 ⇒ **纯用户态死循环**（无 IO、CPU 打满、内存平））。
                while (HbShaper.hb_set_next(hs, ref c) != 0) { set.Add((int)c); }
            }
            catch (Exception) { return false; }
            finally
            {
                if (hs != IntPtr.Zero) HbShaper.hb_set_destroy(hs);          // ← **取完立即销毁**（B2 的核心）
                if (face != IntPtr.Zero) HbShaper.hb_face_destroy(face);
                if (blob != IntPtr.Zero) HbFaceCache.ReleaseSharedBlob(path); // 窗口 10：归还共享引用（不是销毁映射）
            }
            lock (s_gate)
            {
                if (s_coverCache.Count >= MaxCoverCache) s_coverCache.Clear();
                s_coverCache[key] = set;
            }
            return set.Contains(cp);
        }

        internal static bool TryFindCovering(int cp, int weight, int width, int slant,
                                            out Cand hit, out int probed)
        {
            EnsureScan();
            hit = null;
            probed = 0;
            Cand best = null;
            int bw = int.MaxValue, bwd = int.MaxValue, bs = int.MaxValue;
            foreach (Cand c in s_all)
            {
                int dw = Math.Abs(c.Weight - weight);
                if (best != null && dw > bw) continue;              // 主键已更差 ⇒ 连覆盖都不用问
                ++probed;
                // D-F1c/B2-修（主控 2026-09-15 读码定案）：**位图必须排在 `CoversFace` 之前** ——
                //   `CoversFace` ⇒ `HbFaceCache.Covers` ⇒ `Get(path,faceIndex)` 会**建 Entry 并写进 `s_faces`**
                //   （`s_faces` 只在 ≥ `MaxFaces=512` 时淘汰；本机 371 面 ⇒ **永不淘汰**）
                //   ⇒ 若它排在前面，**每个过了主键筛选的候选面都会常驻**（60~371 × ~9 MB ≈ 3.3 GB）。
                //   重排后：只有"位图判为覆盖"的少数候选才会走到 `CoversFace`/`NominalGlyph`（驻留面 ≈1–3）。
                if (!CoversCpByBitmap(c.Path, c.FaceIndex, cp)) continue;
                HbShaper.CoversFace(c.Path, c.FaceIndex, cp);        // 记账 + 预热缓存（语义与计数名不变）
                if (HbFaceCache.NominalGlyph(c.Path, c.FaceIndex, cp) <= 0) continue;
                int dwd = Math.Abs(c.Width - width), ds = Math.Abs(c.Slant - slant);
                bool better = best == null
                              || dw < bw
                              || (dw == bw && dwd < bwd)
                              || (dw == bw && dwd == bwd && ds < bs);
                if (better) { best = c; bw = dw; bwd = dwd; bs = ds; }
                if (bw == 0 && bwd == 0 && bs == 0) break;           // 精确匹配：后面的都只会更差/更远
            }
            hit = best;
            return best != null;
        }

        internal static void Reset()
        {
            lock (s_gate) { s_all = null; Scans = 0; LastScanInfo = "-"; }
        }
    }

    /// <summary>
    /// R1/T1d 的**可观测**面：**独立成类、缺省不输出、绝不碰 `RenderDiagnostics`**
    /// （runner 的通过判据是"未画种类 0"，记进同一本账就会污染判据）。
    ///
    /// ⭐ **"无信息"必须与 "0" 分开报**（本项目反复栽的一条）：`PlanCalls == 0` 时汇总行写
    ///    `未使用(plan=0)`，而不是写一排 0 —— 后者会让"没走这条路"看起来像"走了且全 0"。
    /// </summary>
    internal static class HbFallbackDiag
    {
        // ---- 规格要的六个计数器（+ 几个把"为什么是 0"讲清楚的伴随计数器）----
        internal static long PlanCalls;             // 走了 run 感知路径的段落数
        internal static long RunsCollected;         // 收集到的 run 总数
        internal static long RunGt1Paragraphs;      // **RunCount>1** 的段落数
        internal static long CpUncovered;           // 当前面**不覆盖**的码点数（探针真的问了）
        internal static long CoverageProbe;         // 覆盖查询次数（含缓存命中的那部分）
        internal static long CoverageCacheHit;      // 其中由缓存回答的（**不是"没问"**）
        internal static long FallbackApplied;       // 真的换了面的码点数
        internal static long FallbackFailed;        // 找不到覆盖面 ⇒ 沿用当前面
        internal static long SegmentsBuilt;         // 计划里的面段总数
        internal static long ChunkedLines;          // 吐出 >1 个 GlyphRun 的行数（"一面一 run"可观测）
        internal static long FaceResolveCalls, FaceResolveFailures, FaceResolveCacheHits;
        internal static long CandidateScans;
        /// <summary>D-F1c3：`FaceFromRef` 入口调用次数（判"驻留是否来自段面解析"）。</summary>
        internal static long SegmentFaceResolveCalls;
        internal static void NoteSegmentFaceResolve() => System.Threading.Interlocked.Increment(ref SegmentFaceResolveCalls);
        /// <summary>D-F1c/B4：扫描因触上限而截断的次数（可观测；出口 = `HB_TEXTLINE` 汇总行的 `scanCapped=`）。</summary>
        internal static long ScanCapped;
        internal static void NoteScanCapped() => System.Threading.Interlocked.Increment(ref ScanCapped);
        /// <summary>D-F1b/P2：段面 `GlyphTypeface` 构造失败（身份回落）的次数 —— 不许静默。</summary>
        internal static long SegmentFaceUnresolved;
        internal static void NoteSegmentFaceUnresolved() => System.Threading.Interlocked.Increment(ref SegmentFaceUnresolved);
        /// <summary>D-F1b/P1c：`plan != null` 却拿不到 run 面槽的次数 —— 不许静默。</summary>
        internal static long RunFaceSlotMissing;
        internal static void NoteRunFaceSlotMissing() => System.Threading.Interlocked.Increment(ref RunFaceSlotMissing);
        internal static long FallbackUnrenderable;
        /// <summary>候选来自**被解析出来的其余 run 的面**（规格里的候选②）。</summary>
        internal static long FallbackFromRunFaces;
        /// <summary>候选来自**机器字体目录扫描**（规格里的候选③）。</summary>
        internal static long FallbackFromSystemScan;

        private const int MaxCpSamples = 8;
        private static readonly List<string> s_cpSamples = new List<string>();
        private static readonly Dictionary<string, long> s_targets = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly List<string> s_failSamples = new List<string>();
        private static readonly List<string> s_unrenderableSamples = new List<string>();

        internal static void NoteCoverageProbe(bool cacheHit)
        {
            ++CoverageProbe;
            if (cacheHit) ++CoverageCacheHit;
        }

        internal static void NotePlan(int segments)
        {
            ++PlanCalls;
            SegmentsBuilt += segments;
        }

        internal static void NoteRuns(int runCount)
        {
            RunsCollected += runCount;
            if (runCount > 1) ++RunGt1Paragraphs;
        }

        internal static void NoteUncovered(int cp) => ++CpUncovered;

        /// <summary>记一次"候选面是从哪来的"（②其余 run 的面 / ③机器字体目录扫描）。</summary>
        internal static void NoteCandidateSource(bool fromRunFaces)
        {
            if (fromRunFaces) ++FallbackFromRunFaces; else ++FallbackFromSystemScan;
        }

        internal static void NoteApplied(int cp, HbFaceRef face, string family)
        {
            ++FallbackApplied;
            if (s_cpSamples.Count < MaxCpSamples)
                s_cpSamples.Add("U+" + cp.ToString("X4") + "@" + face);
            string key = (string.IsNullOrEmpty(family) ? "?" : family) + "@" + face.Path + "#" + face.FaceIndex;
            long n;
            s_targets.TryGetValue(key, out n);
            s_targets[key] = n + 1;
        }

        internal static void NoteFailed(int cp)
        {
            ++FallbackFailed;
            if (s_failSamples.Count < MaxCpSamples) s_failSamples.Add("U+" + cp.ToString("X4"));
        }

        /// <summary>候选覆盖面**无法物化成渲染面**（整形面 ≠ 渲染面 ⇒ 宁可不用）。</summary>
        internal static void NoteUnrenderable(int cp, HbFaceRef face)
        {
            ++FallbackUnrenderable;
            if (s_unrenderableSamples.Count < MaxCpSamples)
                s_unrenderableSamples.Add("U+" + cp.ToString("X4") + "@" + face);
        }

        /// <summary>一行汇总。**未走 run 感知路径时明确写"无信息"**，不写 0。</summary>
        internal static string SummaryFragment()
        {
            if (PlanCalls == 0)
                // ★`#23` P2（`D-F2`）：三个"只写不读"的计数器**也必须在这个早退分支出现** ——
                //   否则 `PlanCalls == 0` 的进程里它们照样一个字节都不打印（本波修的就是这条）。
                //   ⚠️ **口径**：本分支**不**触发 `HbFontCandidates.EnsureScan()`（`candidates=` 那行才会，
                //   见 `:1073` 的 `Count` getter）⇒ 这里的 `scanCapped` 是"**至今累计**"的真值，
                //   表示"本进程没有发生过因上限而截断的扫描"，**不是**"没扫过所以不知道"。
                return "multifont=未使用(plan=0) 覆盖/回退各项=**无信息**（不是 0：本进程还没走过 run 感知路径）"
                     + " scanCapped=" + ScanCapped
                     + " segmentFaceUnresolved=" + SegmentFaceUnresolved
                     + " runFaceSlotMissing=" + RunFaceSlotMissing;

            var sb = new StringBuilder();
            sb.Append("multifont=plan=").Append(PlanCalls);
            sb.Append(" runs=").Append(RunsCollected);
            sb.Append(" runGt1=").Append(RunGt1Paragraphs);
            sb.Append(" cpUncovered=").Append(CpUncovered);
            sb.Append(" coverageProbe=").Append(CoverageProbe);
            sb.Append(" liveBlobs=").Append(HbShaper.LiveBlobs).Append("(peak=").Append(HbShaper.LiveBlobsPeak).Append(')');
            // 窗口 10：瞬态（`liveBlobs`，口径不变、峰值仍≈1）与**长期共享**（按文件一份）分开列，
            //   `blobsOutstanding` = 两者之和 = 真正未归还的 blob 引用数（谁在按面建映射，一眼可判）。
            sb.Append(" sharedBlobFiles=").Append(HbFaceCache.SharedBlobFiles)
              .Append("(peak=").Append(HbFaceCache.SharedBlobFilePeak).Append(')')
              .Append(" sharedCreated=").Append(HbFaceCache.SharedBlobCreated)
              .Append(" sharedAcquires=").Append(HbFaceCache.SharedBlobAcquires)
              .Append(" sharedReleases=").Append(HbFaceCache.SharedBlobReleases)
              .Append(" foreignBlobDestroys=").Append(HbShaper.ForeignBlobDestroys)
              .Append(" blobsOutstanding=").Append(HbShaper.LiveBlobs + HbFaceCache.SharedBlobFiles);
            sb.Append(" residentCount=").Append(HbFaceCache.ResidentCount).Append(" releaseCalls=").Append(HbFaceCache.ReleaseCalls);
            sb.Append(" segmentFaceResolveCalls=").Append(SegmentFaceResolveCalls);
            sb.Append(" coverageCacheHit=").Append(CoverageCacheHit);
            sb.Append(" fallbackApplied=").Append(FallbackApplied);
            sb.Append(" fallbackFailed=").Append(FallbackFailed);
            sb.Append(" fallbackUnrenderable=").Append(FallbackUnrenderable);
            sb.Append(" fromRunFaces=").Append(FallbackFromRunFaces);
            sb.Append(" fromSystemScan=").Append(FallbackFromSystemScan);
            sb.Append(" segments=").Append(SegmentsBuilt);
            sb.Append(" chunkedLines=").Append(ChunkedLines);
            sb.Append(" faceResolve=").Append(FaceResolveCalls).Append("/").Append(FaceResolveFailures)
              .Append("(缓存命中").Append(FaceResolveCacheHits).Append(")");
            sb.Append(" candidates=").Append(HbFontCandidates.Count).Append("(扫描").Append(HbFontCandidates.Scans).Append("次)");
            // ★`#23` P2（`D-F2`）：三个"只写不读"的计数器**接出口**。
            //   【为什么必须排在 `candidates=` **之后**】那一行的 `HbFontCandidates.Count` getter 会触发
            //     `EnsureScan()`（`:1073` → `:1100`），而 `ScanCapped` 的**唯一自增点**（`:1147`）在其中
            //     ⇒ 排在它之前读到的会是**扫描前的旧值**（"接了但永远是 0"= 假绿）。
            //   【修前形态】这三个计数器**只在内部自增、从不进任何出口** ⇒ 静止的告警（`D-F2`）。
            sb.Append(" scanCapped=").Append(ScanCapped);
            sb.Append(" segmentFaceUnresolved=").Append(SegmentFaceUnresolved);
            sb.Append(" runFaceSlotMissing=").Append(RunFaceSlotMissing);
            return sb.ToString();
        }

        /// <summary>补充行（码点样本 / 命中的面 / 失败码点）——只在需要时打。</summary>
        internal static string DetailFragment()
        {
            var sb = new StringBuilder();
            sb.Append("  fallbackTargets=");
            if (s_targets.Count == 0) sb.Append("(无)");
            else
            {
                int i = 0;
                foreach (KeyValuePair<string, long> kv in s_targets)
                {
                    if (i++ > 0) sb.Append(" | ");
                    sb.Append(kv.Key).Append("×").Append(kv.Value);
                    if (i >= 4) { sb.Append(" | …"); break; }
                }
            }
            sb.Append("  样例码点=").Append(s_cpSamples.Count == 0 ? "(无)" : string.Join(",", s_cpSamples));
            sb.Append("  未找到覆盖面=").Append(s_failSamples.Count == 0 ? "(无)" : string.Join(",", s_failSamples));
            sb.Append("  覆盖但物化不了=").Append(s_unrenderableSamples.Count == 0 ? "(无)" : string.Join(",", s_unrenderableSamples));
            return sb.ToString();
        }

        internal static void Reset()
        {
            PlanCalls = RunsCollected = RunGt1Paragraphs = CpUncovered = 0;
            CoverageProbe = CoverageCacheHit = FallbackApplied = FallbackFailed = 0;
            SegmentsBuilt = ChunkedLines = 0;
            FaceResolveCalls = FaceResolveFailures = FaceResolveCacheHits = 0;
            CandidateScans = FallbackUnrenderable = 0;
            FallbackFromRunFaces = FallbackFromSystemScan = 0;
            s_cpSamples.Clear(); s_targets.Clear(); s_failSamples.Clear(); s_unrenderableSamples.Clear();
        }
    }

    /// <summary>一个 run 的"面信息"（由 PC 侧把 `Typeface` 翻译成路径/下标/三要素之后交进来）。</summary>
    internal sealed class HbRunFaceInfo
    {
        public int Start = 0;
        public int Length = 0;
        public HbFaceRef Face = null;
        public int Weight = 400, Width = 5, Slant = 0;
        /// <summary>该 run 在段落里的序号（→ `HbFontSegment.RunSlot` → 每段用自己的 foreground）。</summary>
        public int RunSlot = -1;
        public int End => Start + Length;
        public override string ToString() => "[" + Start + "," + End + ") " + Face + " w=" + Weight + "/" + Width + "/" + Slant;
    }

    /// <summary>
    /// 候选面闸门：`(面, 码点, 字重, 拉伸, 斜体) → 这份面能不能既整形又渲染`。
    /// 由 PC 层提供（`GlyphTypeface` 能否物化 + cmap 与整形面逐码点一致）；
    /// **只有过闸的面才会进计划** ⇒ "整形面 ≠ 渲染面"在结构上不可能发生。
    /// </summary>
    internal delegate bool HbFaceGate(HbFaceRef face, int cp, int weight, int width, int slant);

    /// <summary>把 run 的面 + 按码点覆盖，切成"面计划"（**选面粒度 = 码点段**）。</summary>
    internal static class HbFontPlanner
    {
        internal static HbFontPlan Build(string text, List<HbRunFaceInfo> runs, bool allowFallback,
                                        HbFaceGate gate)
        {
            var plan = new HbFontPlan { TextLength = text == null ? 0 : text.Length };
            if (string.IsNullOrEmpty(text) || runs == null || runs.Count == 0) return plan;

            int cp = 0, ri = 0;
            HbFontSegment cur = null;
            while (cp < text.Length)
            {
                while (ri + 1 < runs.Count && cp >= runs[ri].End) ++ri;
                HbRunFaceInfo info = runs[Math.Min(ri, runs.Count - 1)];

                int len = 1;
                if (char.IsHighSurrogate(text[cp]) && cp + 1 < text.Length && char.IsLowSurrogate(text[cp + 1])) len = 2;
                int cpv = char.ConvertToUtf32(text, cp);

                HbFaceRef face = info.Face;
                bool fb = false;
                if (face != null && !HbShaper.CoversFace(face.Path, face.FaceIndex, cpv))
                {
                    HbFallbackDiag.NoteUncovered(cpv);
                    HbFaceRef picked = null;
                    string fam = null;
                    if (allowFallback)
                    {
                        // ⚠️ 候选必须**同时**满足"覆盖"与"能渲染"：`renderable` 是调用方（PC 层）给的
                        //    校验闸门（面能被 `GlyphTypeface` 物化 + cmap 与整形面逐码点一致）。
                        //    只覆盖不可渲染 ⇒ 整形面 ≠ 渲染面 = 像素垃圾，所以这里**当成没找到**。
                        HbFaceRef runPick = PickFromRuns(cpv, runs, face, out fam);
                        if (runPick != null && (gate == null || gate(runPick, cpv, info.Weight, info.Width, info.Slant)))
                        {
                            picked = runPick;
                            HbFallbackDiag.NoteCandidateSource(true);      // 候选②：其余 run 的面
                        }
                        else
                        {
                            HbFontCandidates.Cand cand;
                            int probed;
                            if (HbFontCandidates.TryFindCovering(cpv, info.Weight, info.Width, info.Slant, out cand, out probed))
                            {
                                var f = new HbFaceRef(cand.Path, cand.FaceIndex);
                                if (gate == null || gate(f, cpv, info.Weight, info.Width, info.Slant))
                                {
                                    picked = f; fam = cand.Family;
                                    HbFallbackDiag.NoteCandidateSource(false);   // 候选③：机器字体目录扫描
                                }
                                else HbFallbackDiag.NoteUnrenderable(cpv, f);
                            }
                        }
                    }
                    if (picked != null)
                    {
                        face = picked;
                        fb = true;
                        HbFallbackDiag.NoteApplied(cpv, face, fam);
                    }
                    else
                    {
                        HbFallbackDiag.NoteFailed(cpv);      // 沿用当前面（不假装成功）
                    }
                }

                if (cur != null && cur.End == cp && cur.Fallback == fb && cur.Face != null && cur.Face.Equals(face)
                    && cur.Weight == info.Weight && cur.Width == info.Width && cur.Slant == info.Slant
                    && cur.RunSlot == info.RunSlot)
                    cur.End = cp + len;
                else
                {
                    cur = new HbFontSegment
                    {
                        Start = cp, End = cp + len, Face = face, Fallback = fb,
                        Weight = info.Weight, Width = info.Width, Slant = info.Slant,
                        RunSlot = info.RunSlot,
                    };
                    plan.Segments.Add(cur);
                }
                cp += len;
            }
            HbFallbackDiag.NotePlan(plan.Segments.Count);
            return plan;
        }

        /// <summary>候选优先级②：**被解析出来的其余 run 的面**（同覆盖下才轮到这里）。</summary>
        private static HbFaceRef PickFromRuns(int cp, List<HbRunFaceInfo> runs, HbFaceRef current, out string family)
        {
            family = null;
            foreach (HbRunFaceInfo r in runs)
            {
                if (r.Face == null || r.Face.Equals(current)) continue;
                if (HbShaper.CoversFace(r.Face.Path, r.Face.FaceIndex, cp)) return r.Face;
            }
            return null;
        }
    }

    // ==================================================================================
    //  2. ⭐ 断行引擎 —— 规则集**只有这一份**（B2 的唯一真源）
    // ==================================================================================
    //
    //  【规则集 = 1 个断点集 + 3 条规则】（逐条依据见 `src/Linux/build/MilBridge/tests/IcuBreakParity/` 文件头）
    //    断点集   ICU 70 的 UAX#14 `ubrk_*`（实测 zh-CN 走 root 规则 ⇒ 断行点集与 locale 无关）
    //    规则 1   `…`(U+2026) **之前可以断**（唯一一条按 DWrite 真值覆盖 UAX#14 的表差异）
    //    规则 2   禁则拉字：贪心选的断点会让下一行以"不能做行首"的字开头 ⇒ 断点回退 1 字；
    //             回退位不是合法断点 ⇒ **放弃**；判据 = ICU 原始断点集没有该位置，
    //             另加实测例外 `“`/`‘`（`“` 实测要拉 2 字 ⇒ guard<2）
    //    规则 3   强制断 fallback：断点集里没有能放下的位置 ⇒ 按宽度塞满（至少 1 字）。
    //             它**顺带**复现"容器窄到 1~2 字时放弃禁则"与字符级紧急断行（实测全等）。
    //
    //  ⚠️ 硬断字符（`\n` / `\r\n` / `\u2028` / `\u2029` / `\u0085`）**不进 ICU**：
    //     先按它们切段，段内再跑上面那套 —— 与 `IcuBreakParity` 逐字一致。
    // ==================================================================================

    /// <summary>一行在**源文本索引系**里的位置（`End` **不含**硬断字符；硬断字符由 `HardBreakLen` 给出）。</summary>
    internal sealed class HbLineRange
    {
        public int Start;                // 段落系（源文本下标）
        public int End;                  // 不含硬断字符
        public int HardBreakLen;         // 0 / 1 / 2（`\r\n`）
        public bool HasEop;              // 末段落最后一行 ⇒ 追加 EOP 字符（`Length` +1、`NewlineLength` = 1）
        public bool Forced;              // 由规则 3（强制断 fallback）产出
        public int KinsokuPull;          // 规则 2 拉了几次（0/1/2）

        public int VisibleLength => End - Start;
    }

    internal static class HbBreakEngine
    {
        // ---------------------------------------------------------------- ICU（版本化符号）
        private const int UBRK_LINE = 2;
        private const int UBRK_DONE = -1;

        [DllImport("libicuuc.so.70", EntryPoint = "ubrk_open_70", CharSet = CharSet.Ansi)]
        private static extern unsafe IntPtr ubrk_open(int type, string locale, char* text, int textLength, out int status);
        [DllImport("libicuuc.so.70", EntryPoint = "ubrk_first_70")] private static extern int ubrk_first(IntPtr bi);
        [DllImport("libicuuc.so.70", EntryPoint = "ubrk_next_70")] private static extern int ubrk_next(IntPtr bi);
        [DllImport("libicuuc.so.70", EntryPoint = "ubrk_close_70")] private static extern void ubrk_close(IntPtr bi);
        [DllImport("libicuuc.so.70", EntryPoint = "u_getVersion_70")] private static extern unsafe void u_getVersion(byte* v);

        /// <summary>ICU 版本（`u_getVersion`）；ICU 不在就 `"?"`。</summary>
        internal static string IcuVersion
        {
            get
            {
                try
                {
                    unsafe { byte* v = stackalloc byte[4]; u_getVersion(v); return $"{v[0]}.{v[1]}.{v[2]}.{v[3]}"; }
                }
                catch (Exception) { return "?"; }
            }
        }

        // 计数（引擎侧；供 scaffold 汇总 —— 这些是"引擎真的这么做了"的直接读数）
        internal static long ParagraphsBroken;
        internal static long ForcedBreaks;
        internal static long KinsokuPulls;
        internal static long IcuCalls;
        internal static long IcuFailures;

        /// <summary>硬断字符长度：`\r\n` 占 2 个字符；非硬断返回 0。</summary>
        internal static int HardBreakLengthAt(string text, int i)
        {
            if (i < 0 || i >= text.Length) return 0;
            char c = text[i];
            if (c == '\r') return (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
            if (c == '\n' || c == '\u2028' || c == '\u2029' || c == '\u0085') return 1;
            return 0;
        }

        internal static bool IsHardBreak(char c)
            => c == '\r' || c == '\n' || c == '\u2028' || c == '\u2029' || c == '\u0085';

        // ---------------------------------------------------------------- 断点集 + 覆盖规则

        /// <summary>ICU 的 UAX#14 断点集（段落内下标；含 0）。</summary>
        internal static List<int> IcuBreaks(string text, string locale)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(text)) { result.Add(0); return result; }
            int status = 0;
            System.Threading.Interlocked.Increment(ref IcuCalls);
            unsafe
            {
                fixed (char* p = text)
                {
                    IntPtr bi = ubrk_open(UBRK_LINE, locale, p, text.Length, out status);
                    // ICU 约定：status <= 0 都算成功。实测 zh-CN = -128 (U_USING_FALLBACK_WARNING)
                    // ⇒ 没有 zh-CN 专属断行数据、走 root 规则 ⇒ 断行点集与 locale 无关。
                    if (bi == IntPtr.Zero || status > 0)
                    {
                        if (bi != IntPtr.Zero) ubrk_close(bi);
                        System.Threading.Interlocked.Increment(ref IcuFailures);
                        throw new InvalidOperationException($"ubrk_open(locale={locale}) 失败 status={status}");
                    }
                    try { for (int b = ubrk_first(bi); b != UBRK_DONE; b = ubrk_next(bi)) result.Add(b); }
                    finally { ubrk_close(bi); }
                }
            }
            return result;
        }

        /// <summary>
        /// **规则 4（Tab 口径，真机实测见 T1d 报告 §10）**：每个 `\t` **之前**可断、**之后**不可断。
        ///
        /// 【为什么必须覆盖 ICU】实测 ICU 70（zh-CN/root）对 `'a\tb\t\tc\td'` 给
        ///   `[0,2,5,7,8]`（把 U+0009 当 BA = 其后可断）；而真机在 `w=20` 断在 **4**、`w=30` 断在 **6**
        ///   —— 4 / 6 都恰好是"某个 `\t` 之前"，且 `w=20` 时"`\t` 之后"的 5 明明放得下（18.816 ≤ 20）
        ///   却没被选中 ⇒ **`\t` 之后不是断点**。
        /// 无 `\t` 的文本本函数**恒等**（逐字符判断，非 tab 直接跳过）⇒ 其余 600+ 用例不受影响。
        /// </summary>
        internal static List<int> OverlayTabs(List<int> set, string text)
        {
            for (int i = 0; i < text.Length; ++i)
            {
                if (text[i] != '\t') continue;
                if (!set.Contains(i)) set.Add(i);      // 之前可断
                set.Remove(i + 1);                     // 之后不可断（覆盖 ICU 的 BA）
            }
            set.Sort();
            return set;
        }

        /// <summary>
        /// 行尾空白判据：真机把 **`\t` 排除**在外（实测：行以 `\t` 结尾而 `TrailingWhitespaceLength = 0`）。
        /// 其余空白（空格、全角空格…）照旧计入。
        /// </summary>
        // #12 机制一（2026-09-14）：真机口径 `char.IsWhiteSpace('\u00A0') == true` 但**不把 NBSP 算行尾空白**
        //   ⇒ 证据：T1b2 [ROWD] `A1_nbsp_zwsp_w30#0` 真值 ws=0、WITW−W=0.0000，而 Δ行宽 −4.1587 == adv(NBSP) 4.1600。
        //   ⚠️ 只排除 U+00A0：ZWSP(U+200B) 本来就不是 `char.IsWhiteSpace` ⇒ 不动它（B_nbsp_zwsp_trim#1 两侧 ws 都是 0）。
        internal static bool IsTrailingWhitespace(char c) => char.IsWhiteSpace(c) && c != '\t' && c != '\u00A0';

        /// <summary>**按 DWrite 真值覆盖 UAX#14** 的规则 1（依据见上）。</summary>
        internal static List<int> Overlay(List<int> raw, string text)
        {
            var set = new List<int>(raw);
            for (int i = 0; i < text.Length; ++i)
            {
                if (text[i] == '\u2026' && !set.Contains(i)) set.Add(i);   // 规则 1：`…` 前可断
                // （规则 2「`“` 前禁止断」不放这里 —— 由 CannotStartLine 的禁则拉字统一处理）
            }
            set.Sort();
            return set;
        }

        /// <summary>该位置能否做行首：ICU 原始断点集说不行 ⇒ 不行；另加实测的两个引号例外（`“`/`‘`）。</summary>
        internal static bool CannotStartLine(string text, int index, List<int> raw)
        {
            if (index >= text.Length) return false;
            char ch = text[index];
            if (ch == '\u201C' || ch == '\u2018') return true;   // 规则 2（实测：DWrite 把引号连同前一个字下移）
            return !raw.Contains(index);                          // UAX#14 的禁则编码（实测 40/40 一致）
        }

        /// <summary>逐字 advance（DIP）：HarfBuzz 真 shaping → cluster 求逆。</summary>
        internal static double[] MeasureChars(string text, string fontPath, double emSize)
            => HbShaper.Shape(fontPath, text, emSize).CharAdvances();

        /// <summary>
        /// 同上，但 advance 来自**多字体面计划**（R1/T1d）：每个码点段用各自的面整形。
        /// ⚠️ 断行**规则**一行没变 —— 变的只是"这一串 advance 由哪份面提供"。
        /// </summary>
        internal static double[] MeasureChars(string text, HbFontPlan plan, double emSize)
            => HbMultiFontShaper.ShapeParagraph(plan, text, emSize).CharAdvances();

        // ---------------------------------------------------------------- 断行

        /// <summary>
        /// 段内断行（**不含**硬断字符的处理，硬断由 <see cref="LayoutText"/> 切段）。
        /// 返回的 `Start`/`End` 是**源文本**下标（不是段内下标）。
        /// </summary>
        internal static List<HbLineRange> BreakParagraph(string text, int start, int end,
                                                         double width, double emSize, string fontPath,
                                                         HbFontPlan plan = null, double tabInterval = double.NaN,
                                                         bool wrap = true, double indentDip = 0,
                                                         int modifierOpenIndex = -1, int modifierScopeEnd = -1,
                                                         double paragraphIndentDip = 0)   // D-T2/(C)：尾随可选，默认 0 ⇒ 既有调用点零改动
        {
            var lines = new List<HbLineRange>();
            int len = end - start;
            if (len <= 0) { lines.Add(new HbLineRange { Start = start, End = start }); return lines; }  // 空段落 ⇒ 空行

            System.Threading.Interlocked.Increment(ref ParagraphsBroken);

            string para = text.Substring(start, len);
            // R1/T1d：**规则没变**，只把"逐字 advance 从哪来"换成计划（各码点段各自的面）。
            double[] adv = plan != null
                ? MeasureChars(para, plan.Sub(start, end), emSize)
                : MeasureChars(para, fontPath, emSize);
            // #13：**零宽必须同时落在测量侧**（否则断行仍按未隐藏文本算 ⇒ 行划分与真值对不上：
            //   实测未落此件时 `M_modifier_winf` 行#0 `len=26`，而真值 `len=63`）。
            //   跨度只认客户端覆盖终点 `modifierScopeEnd`（`<0` ⇒ 到段末），**与 `closeIndex` 无关**。
            if (modifierOpenIndex >= 0)
            {
                int kl = Math.Max(0, modifierOpenIndex - start);
                int kh = (modifierScopeEnd < 0 ? len : Math.Min(modifierScopeEnd - start, len));
                for (int i = kl; i < kh && i < adv.Length; ++i) adv[i] = 0;
            }
            List<int> raw = IcuBreaks(para, "zh-CN");
            // 规则 4（tab）：`\t` 之前可断 / 之后不可断。**禁则参考集也用同一份**，
            // 否则"`\t` 之前"这个合法断点会被 `CannotStartLine` 当成禁则位置、触发规则 2 拉字。
            List<int> rawTab = OverlayTabs(new List<int>(raw), para);
            List<int> breaks = Overlay(rawTab, para);

            var local = new List<int>();
            foreach (int b in breaks) if (b >= 0 && b <= len) local.Add(b);
            if (!local.Contains(len)) local.Add(len);                 // 段末必是断点
            local.Sort();
            double tabIntervalUse = double.IsNaN(tabInterval) ? 4.0 * emSize : tabInterval;
            // D-T2/(C)：钳位目标里要减掉的段落级缩进**改成可接线**（值由 `FormatParagraph` 透传；`TabClampInset` 侧
            //   本次按"盒"侧保持 `=> 0.0` ⇒ **本行行为惰性**，仅把线接通，留一行可翻）。
            double clampUse = HbShaper.TabClampWidthFor(wrap, width, paragraphIndentDip);
            var afterTabSet = new HashSet<int>();
            for (int ti = 0; ti < len; ++ti)
                if (para[ti] == '\t') { if (!local.Contains(ti + 1)) local.Add(ti + 1); afterTabSet.Add(ti + 1); }
            local.Sort();

            int pos = 0;
            while (pos < len)
            {
                int best = -1;
                // 本行的内容起点（相对行原点）：**每行都给 `Indent`**
                //   —— 真值口径：上游 `LineServicesCallbacks.cs:376` `lsLineProps.durLeft = settings.TextIndent`；
                //      `i24nl` 臂 `FirstLineInParagraph=false` 时第 2 行仍 `xFromLeftDip=24`。
                //   波 `#16` `D-T2`/(B)：此处原为「仅**段落首行**吃 `Indent`」（T1d 旧注），是缺口本身。
                //   波 `#16` 件 2：**内容起点 = `Indent + ParagraphIndent`**（oracle 每字符 `x` 直证：
                //   `i0p24`⇒24、`i24p0`⇒24、`i24p24`⇒**48**）。
                //   件 2c 追加：**提到 `foreach` 之外**（强制断 fallback `:1784` 也要用它 ⇒ 必须在同一作用域）。
                double lineContentStart = indentDip + paragraphIndentDip;
                foreach (int b in local)
                {
                    if (b <= pos) continue;
                    bool tabClamped;
                    double wb = EffectiveWidthTab(adv, para, pos, b, tabIntervalUse, clampUse, lineContentStart,
                                                  indentDip, out tabClamped);   // 件 1a：网格锚 = indentDip
                    bool afterTab = b > pos && para[b - 1] == '\t' && afterTabSet.Contains(b);
                    // 件 1a：`wb` 已自 `Indent` 起算（网格锚同源）⇒ 远缘 = `wb + PI`（**不能再加 `Indent`**，否则重复计入）。
                    if (wb + paragraphIndentDip > width + 1e-9) break;
                    // ★ 资格：被钳满的 tab（真机 `after-tab`）**或**"无停靠位"配置下的 0 宽 tab
                    //   （`interval <= 0` ⇒ "tab 之后"与"tab 之前"等价；真值 `A1_tabs_w20` 正是在 `\t\t` 之间断）
                    // ★ 段末候选（b == len）**免资格**：真机末行是**包含**行尾那个 Tab 的（`tab-tail@w320` 真值 96.0 = a+Tab 的跳距）
                    if (afterTab && b < len && !(tabClamped || !(tabIntervalUse > 0))) continue;
                    best = b;
                }
                bool forced = false;
                int pulls = 0;
                if (best < 0)
                {
                    // ── 规则 3：强制断 fallback（断点集里没有能放下的位置 ⇒ 按宽度塞满，至少 1 字）──
                    double w = 0; int e = pos;
                    // 波 `#16` 件 2c：强制断的塞满循环同样要**扣掉内容起点**（全文件第二条、也是最后一条
                    //   含 width 的拟合比较；漏它 ⇒ `:1771` 的 break 会被本 fallback 撤销 ⇒ 行数少一行）。
                    // 件 1c①：与 `:1777` **同一把尺子** —— 行中 tab 若"整格放不下"则按整格 `I` 计（⇒ 放不下 ⇒ 停），
                    //   否则照旧用 `adv[e]`（= 段落级整形给的 `stop − pen`：**未钳、锚 0、I_tab = 4×emSize**）。
                    while (e < len)
                    {
                        double ae = adv[e];
                        if (para[e] == '\t' && tabIntervalUse > 0)
                        {
                            // 件 1d：**按行笔位重算**该 tab 的 advance —— `adv[e]` 是**段落级**整形给的
                            //   `stop − pen`（锚 0、未钳），而行首 tab 在本行里的真实 advance 是 `stop − 0`
                            //   ⇒ 直接用它会把行首 tab **少算 `pen_paragraph`** ⇒ 该断的行被塞下（`@w100` 的 2 行）。
                            double penLine = lineContentStart + w;
                            ae = (penLine + tabIntervalUse > width + 1e-9)
                                 ? tabIntervalUse
                                 : (Math.Floor(penLine / tabIntervalUse) + 1.0) * tabIntervalUse - penLine;
                        }
                        if (w + lineContentStart + ae > width + 1e-9) break;
                        w += ae; ++e;
                    }
                    best = e > pos ? e : pos + 1;
                    forced = true;
                    System.Threading.Interlocked.Increment(ref ForcedBreaks);
                }
                else
                {
                    // ── 规则 2：禁则拉字（把"不能做行首"的字连同它的前一个字拉到下一行）──
                    for (int guard = 0; guard < 2; ++guard)
                    {
                        int g = start + best;
                        if (g >= end || !CannotStartLine(text, g, rawTab)) break;
                        int cand = best - 1;
                        if (cand > pos && local.Contains(cand)) { best = cand; ++pulls; } else break;
                    }
                    if (pulls > 0) System.Threading.Interlocked.Add(ref KinsokuPulls, pulls);
                }
                lines.Add(new HbLineRange { Start = start + pos, End = start + best, Forced = forced, KinsokuPull = pulls });
                pos = best;
            }
            return lines;
        }

        /// <summary>
        /// 整段文本的断行（含硬断字符切段）。返回的行**不含**硬断字符 ——
        /// 硬断与 EOP 由 `HardBreakLen` / `HasEop` 表达（= `IcuBreakParity` 的 DWrite 层口径）。
        /// </summary>
        internal static List<HbLineRange> LayoutText(string text, double width, double emSize, string fontPath,
                                                     HbFontPlan plan = null, double tabInterval = double.NaN,
                                                     bool wrap = true, double indentDip = 0,
                                                     int modifierOpenIndex = -1, int modifierScopeEnd = -1,
                                                     double paragraphIndentDip = 0)   // D-T2/(C)：尾随可选，默认 0 ⇒ 既有调用点零改动
        {
            var all = new List<HbLineRange>();
            if (text == null) text = string.Empty;
            int paraStart = 0;
            for (int i = 0; i <= text.Length; ++i)
            {
                bool atEnd = (i == text.Length);
                int hb = atEnd ? 0 : HardBreakLengthAt(text, i);
                if (!atEnd && hb == 0) continue;

                List<HbLineRange> seg = BreakParagraph(text, paraStart, i, width, emSize, fontPath, plan,
                                                       tabInterval, wrap, indentDip, modifierOpenIndex, modifierScopeEnd,
                                                       paragraphIndentDip);   // D-T2/(C) 透传
                if (atEnd)
                {
                    // 末段落：最后一行带 EOP（`Length` +1、`NewlineLength` = 1）
                    seg[seg.Count - 1].HasEop = true;
                }
                else
                {
                    seg[seg.Count - 1].HardBreakLen = hb;      // 硬断字符归**前**一行
                }
                all.AddRange(seg);

                paraStart = i + hb;
                if (atEnd) break;
                i += hb - 1;                                   // `\r\n` 整体跨过
            }
            return all;
        }

        /// <summary>行宽 = 区间内 advance 之和 − **行尾空白**（真机口径：行尾空白不占宽但占区间）。</summary>
        private static double EffectiveWidth(double[] adv, string para, int s, int e)
        {
            double w = 0;
            for (int i = s; i < e; ++i) w += adv[i];
            int last = e;
            while (last > s && IsTrailingWhitespace(para[last - 1])) { w -= adv[last - 1]; --last; }
            return w;
        }

        /// <summary>Tab 感知版（T1d §6/§7）：同骨架 + 同行尾空白扣减；`\t` 那格按"自行起点 s 起算的笔位 + 段落口径"算。
        /// `tabInterval <= 0` ⇒ 0；`>0` ⇒ 下一倍数；`clampWidth` 有限 ⇒ min(跳距, 行宽−笔位) 且 `tabClamped=true`。</summary>
        private static double EffectiveWidthTab(double[] adv, string para, int s, int e, double tabInterval,
                                                double clampWidth, double lineContentStart, double gridAnchor,
                                                out bool tabClamped)
        {
            tabClamped = false;
            bool zero = !(tabInterval > 0);
            // 件 1a：网格锚 = `Indent`（**不含** `PI`）—— 与整形侧 `startPenX = indentDip` 同锚。
            //   oracle 直证：`@i24p24` 的 tab adv = 72 = 96 − 24（与 PI 无关）、x = 48（= I+PI）。
            //   ⇒ 锚错了会把每格高估 `Indent` ⇒ 该留的行被拒（轮 6 组 B 的 6 条）。
            double pen = gridAnchor;
            int n = Math.Min(Math.Min(e, adv.Length), para.Length);
            for (int i = s; i < n; ++i)
            {
                double a = adv[i];
                if (para[i] == '\t')
                {
                    double stop = zero ? pen : (Math.Floor(pen / tabInterval) + 1.0) * tabInterval;
                    a = stop - pen;
                    // 件 1b′（**条件式**）：行中 tab 只有"**完整一格放不下**"时贡献才取整格 `I`
                    //   （⇒ 候选必然超宽 ⇒ 断在 tab 之前）；否则**照旧 `stop − pen`**（= 写出/advance 同值）。
                    //   oracle 判别式（5 条逐位）：`lat-a-t-b@w96/@w100`、`lat-ab-t-c@w96` 的 `pen+I` = 109.35/109.35/122.69 > W ⇒ 取 I ⇒ 断 ✓
                    //                              `mid-tab-a-t-b@w140@i24`、`lead-tab-b-t-c@w220@i24` 的 `pen+I` = 133.35 ≤ W ⇒ 照旧 58.653 ⇒ 1 行 ✓
                    //   （1b 的无条件替换正是被 `@w140@i24` 否掉的：凭空多 96−58.653 = 37.35 ⇒ `[a\tb]` 顶到 146.69 > 140。）
                    bool lineStartTab = HbShaper.IsLineStartTab(pen, lineContentStart);
                    if (!zero && !lineStartTab && pen + tabInterval > clampWidth) a = tabInterval;
                    // D-T1：越界才钳，且**只在行首钳**（判据 = `stop > clampWidth`，见 `ApplyTabStops` 同款注释）
                    if (!double.IsInfinity(clampWidth))
                    {
                        // 波 `#16` 件 2：room 自**内容起点**起算（Q6：钳满 tab 的 advance = container − ParagraphIndent − Indent）。
                        // 件 2d：room **不再减 pen** ⇒ 行首 tab 时 `adv > room` ⟺ `stop > clampWidth`（D-T1 原式）。
                        double room = clampWidth - lineContentStart; if (room < 0) room = 0;
                        // 件 1c②：等号也算"钳满"⇒ `tabClamped=true` ⇒ `:1777` 的 after-tab 资格放行
                        //   （oracle：`lat-a-t-b@w96` 行1 `[\t]` 独占 `w=96.00` 走的就是这条路）。
                        if (a >= room && HbShaper.IsLineStartTab(pen, lineContentStart)) { a = room; tabClamped = true; }
                    }
                }
                pen += a;
            }
            int last = n;
            while (last > s && IsTrailingWhitespace(para[last - 1])) { pen -= adv[last - 1]; --last; }
            return pen;
        }
    }

#if !TEXTLINE_BREAK_ENGINE_ONLY

    // ==================================================================================
    //  3. 脚手架：开关 / 计数器 / 落盘 / LS 边界绊线
    // ==================================================================================

    /// <summary>
    /// 开关、命中计数与落盘。
    ///
    /// **全部 public** —— 可被测试与排障程序读到（主控要求"计数器要能被测试读到，不是只打日志"）。
    /// </summary>
    public static class HbTextLineScaffold
    {
        /// <summary>总开关。**默认关**（未接线时本文件是惰性的）。</summary>
        public const string EnableEnvVar = "WPF_LINUX_TEXTLINE";

        /// <summary>落盘路径：设了就在进程退出时把计数写过去（`run HelloWpf` 取数用）。</summary>
        public const string DumpEnvVar = "WPF_LINUX_TEXTLINE_DUMP";

        /// <summary>严格模式：欠账被命中时**抛异常**而不是回退（bring-up 期定位用，默认关）。</summary>
        public const string StrictEnvVar = "WPF_LINUX_TEXTLINE_STRICT";

        /// <summary>诊断输出（stderr）；默认关。</summary>
        public const string DiagEnvVar = "WPF_LINUX_TEXTLINE_DIAG";

        /// <summary>9 个欠账 override 的名字（顺序固定，计数器按下标对应）。</summary>
        public static readonly string[] OwedMembers =
        {
            "Collapse",
            "GetBackspaceCaretCharacterHit",
            "GetCharacterHitFromDistance",
            "GetDistanceFromCharacterHit",
            "GetIndexedGlyphRuns",
            "GetNextCaretCharacterHit",
            "GetPreviousCaretCharacterHit",
            "GetTextCollapsedRanges",
            "GetTextLineBreak",
        };

        // ⚠️ B2 已把 Collapse / GetTextCollapsedRanges / GetTextLineBreak 做成**真实现**，
        //    这三个的欠账计数**从此恒为 0**（数组保留 9 项是为了汇总行格式不漂移 —— 那是
        //    HelloWpf 绊线脚本 grep 的锚点）。仍真欠账的 **5** 个见 StillOwedMembers（`GetIndexedGlyphRuns` 已实现）。
        internal static readonly string[] StillOwedMembers =
        {
            "GetBackspaceCaretCharacterHit", "GetCharacterHitFromDistance",
            "GetDistanceFromCharacterHit",   // D-F1：`GetIndexedGlyphRuns` 已于 2026-09-15 真实现（移出仍欠账名单）
            "GetNextCaretCharacterHit", "GetPreviousCaretCharacterHit",
        };

        private static readonly long[] s_hits = new long[OwedMembers.Length];
        private static long s_linesConstructed;
        private static long s_drawCalls;
        private static long s_getTextBoundsCalls;
        private static long s_getTextRunSpansCalls;
        private static int s_dumpInstalled;

        // ---------------- B2 新增计数 ----------------
        private static long s_paragraphsFormatted;
        private static long s_breakNullReturned;        // GetTextLineBreak() 返回 null（普通文本，真机主路径）
        private static long s_breakZeroRecordIssued;    // GetTextLineBreak() 发出**零记录** TextLineBreak
        private static long s_modifierLines;            // 带 TextModifierScope 的行（真机 3222 行里只有 2 行）
        private static long s_collapseCalls;
        private static long s_collapseEarlyReturnIneligible;   // !HasOverflowed && !KeepState ⇒ 返回 this
        private static long s_collapseEarlyReturnTooWide;      // 约束宽 > 行宽 ⇒ 返回 this
        private static long s_collapseEmptyArgsThrow;          // 空参 ⇒ ArgumentNullException（上游同款）
        private static long s_collapseApplied;                 // 真折叠发生
        private static long s_collapseUnsupported;             // 非 CharacterEllipsis（本轮无真机真值）
        private static long s_collapsedRangesNull;
        private static long s_collapsedRangesReturned;
        private static long s_forcedBreakLines;                // 规则 3 命中（"内容没放下"）
        private static long s_kinsokuPullTotal;
        private static long s_dependentLengthQueries;
        // ---- P0（RTL）：反镜像绘制 ----
        private static long s_invertedLines;        // 带 InvertAxes != None 画过的行数（镜像生效）
        private static long s_invertedNoWidth;      // 镜像基准未知 ⇒ **降级成 LTR 画**的行数（不静默）
        private static long s_antiMatrixMismatch;   // 自建矩阵与上游函数不一致的次数（DIRECT 形态才可能 >0）
        private static int s_invDiagLeft = 3;       // 降级诊断**前 3 次无条件**打（本项目"故障时没有诊断"栽过）

        // ---------------- 开关（解析口径与 FontLayoutStripping 一致）----------------

        private static int s_envResolved;
        private static bool s_envValue;

        /// <summary>本进程的有效开关（env 说了算；读不出来就是**关**）。</summary>
        public static bool Enabled
        {
            get
            {
                if (System.Threading.Volatile.Read(ref s_envResolved) == 0)
                {
                    s_envValue = ParseOnOff(Environment.GetEnvironmentVariable(EnableEnvVar));
                    System.Threading.Volatile.Write(ref s_envResolved, 1);
                }
                return s_envValue;
            }
        }

        /// <summary>严格模式（欠账命中即抛）。</summary>
        public static bool Strict => ParseOnOff(Environment.GetEnvironmentVariable(StrictEnvVar));

        internal static bool DiagEnabled => ParseOnOff(Environment.GetEnvironmentVariable(DiagEnvVar));

        internal static bool ParseOnOff(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;      // **认不出来/没设 ⇒ 关**
            switch (value.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": return true;
                default: return false;
            }
        }

        // ---------------- 计数 ----------------

        public static long LinesConstructed => System.Threading.Interlocked.Read(ref s_linesConstructed);
        public static long DrawCalls => System.Threading.Interlocked.Read(ref s_drawCalls);
        public static long GetTextBoundsCalls => System.Threading.Interlocked.Read(ref s_getTextBoundsCalls);
        public static long GetTextRunSpansCalls => System.Threading.Interlocked.Read(ref s_getTextRunSpansCalls);
        public static long ParagraphsFormatted => System.Threading.Interlocked.Read(ref s_paragraphsFormatted);
        public static long BreakNullReturned => System.Threading.Interlocked.Read(ref s_breakNullReturned);
        /// <summary>⭐ 发出去的**零记录** `TextLineBreak` 个数（LS 边界绊线的主读数）。</summary>
        public static long BreakZeroRecordIssued => System.Threading.Interlocked.Read(ref s_breakZeroRecordIssued);
        public static long ModifierLines => System.Threading.Interlocked.Read(ref s_modifierLines);
        public static long CollapseCalls => System.Threading.Interlocked.Read(ref s_collapseCalls);
        public static long CollapseEarlyReturnIneligible => System.Threading.Interlocked.Read(ref s_collapseEarlyReturnIneligible);
        public static long CollapseEarlyReturnTooWide => System.Threading.Interlocked.Read(ref s_collapseEarlyReturnTooWide);
        public static long CollapseEmptyArgsThrow => System.Threading.Interlocked.Read(ref s_collapseEmptyArgsThrow);
        public static long CollapseApplied => System.Threading.Interlocked.Read(ref s_collapseApplied);
        public static long CollapseUnsupported => System.Threading.Interlocked.Read(ref s_collapseUnsupported);
        public static long CollapsedRangesNull => System.Threading.Interlocked.Read(ref s_collapsedRangesNull);
        public static long CollapsedRangesReturned => System.Threading.Interlocked.Read(ref s_collapsedRangesReturned);
        public static long ForcedBreakLines => System.Threading.Interlocked.Read(ref s_forcedBreakLines);
        public static long KinsokuPullTotal => System.Threading.Interlocked.Read(ref s_kinsokuPullTotal);
        public static long DependentLengthQueries => System.Threading.Interlocked.Read(ref s_dependentLengthQueries);
        /// <summary>带反镜像（`InvertAxes != None`）画过的**行**数。</summary>
        public static long InvertedLines => System.Threading.Interlocked.Read(ref s_invertedLines);
        /// <summary>**降级**：段落宽未知 ⇒ 按 LTR 画（镜像没做）的行数。</summary>
        public static long InvertedNoWidth => System.Threading.Interlocked.Read(ref s_invertedNoWidth);
        internal static long AntiMatrixMismatch => System.Threading.Interlocked.Read(ref s_antiMatrixMismatch);

        internal static void NoteInvertedLine(InvertAxes inversion)
            => System.Threading.Interlocked.Increment(ref s_invertedLines);

        /// <summary>降级：**不抛、不静默** —— 计数 + 前 3 次无条件 stderr（之后由 DIAG 门控）。</summary>
        internal static void NoteInvertedNoWidth(InvertAxes inversion)
        {
            System.Threading.Interlocked.Increment(ref s_invertedNoWidth);
            bool force = s_invDiagLeft-- > 0;
            if (force || DiagEnabled)
                Diag("RTL 镜像**降级**：宿主要求 InvertAxes=" + inversion + "，但本行段落宽未知（0）"
                     + " ⇒ 按 LTR 画（视觉顺序不对），已计入 invertedNoWidth。"
                     + "（真因：FormatParagraph 没把段落宽传下来；真 bidi 视觉顺序是 M–L 独立立项）");
        }

        internal static void NoteAntiMatrixMismatch()
            => System.Threading.Interlocked.Increment(ref s_antiMatrixMismatch);

        /// <summary>某个欠账成员被命中多少次（名字取自 <see cref="OwedMembers"/>）。</summary>
        public static long HitCount(string member)
        {
            int i = Array.IndexOf(OwedMembers, member);
            return i < 0 ? -1 : System.Threading.Interlocked.Read(ref s_hits[i]);
        }

        /// <summary>全部欠账命中次数之和。</summary>
        public static long TotalFallbackHits
        {
            get
            {
                long n = 0;
                for (int i = 0; i < s_hits.Length; i++) n += System.Threading.Interlocked.Read(ref s_hits[i]);
                return n;
            }
        }

        internal static void NoteConstructed() => System.Threading.Interlocked.Increment(ref s_linesConstructed);
        internal static void NoteDraw() => System.Threading.Interlocked.Increment(ref s_drawCalls);
        internal static void NoteGetTextBounds() => System.Threading.Interlocked.Increment(ref s_getTextBoundsCalls);
        internal static void NoteGetTextRunSpans() => System.Threading.Interlocked.Increment(ref s_getTextRunSpansCalls);
        internal static void NoteParagraphFormatted() => System.Threading.Interlocked.Increment(ref s_paragraphsFormatted);
        internal static void NoteModifierLine() => System.Threading.Interlocked.Increment(ref s_modifierLines);
        internal static void NoteBreakNull() => System.Threading.Interlocked.Increment(ref s_breakNullReturned);
        internal static void NoteDependentLengthQuery() => System.Threading.Interlocked.Increment(ref s_dependentLengthQueries);
        internal static void NoteForcedBreakLine() => System.Threading.Interlocked.Increment(ref s_forcedBreakLines);
        internal static void NoteKinsokuPull(int n) => System.Threading.Interlocked.Add(ref s_kinsokuPullTotal, n);
        internal static void NoteCollapseCall() => System.Threading.Interlocked.Increment(ref s_collapseCalls);
        internal static void NoteCollapseIneligible() => System.Threading.Interlocked.Increment(ref s_collapseEarlyReturnIneligible);
        internal static void NoteCollapseTooWide() => System.Threading.Interlocked.Increment(ref s_collapseEarlyReturnTooWide);
        internal static void NoteCollapseEmptyArgsThrow() => System.Threading.Interlocked.Increment(ref s_collapseEmptyArgsThrow);
        internal static void NoteCollapseApplied() => System.Threading.Interlocked.Increment(ref s_collapseApplied);
        internal static void NoteCollapseUnsupported() => System.Threading.Interlocked.Increment(ref s_collapseUnsupported);
        internal static void NoteCollapsedRangesNull() => System.Threading.Interlocked.Increment(ref s_collapsedRangesNull);
        internal static void NoteCollapsedRangesReturned() => System.Threading.Interlocked.Increment(ref s_collapsedRangesReturned);

        /// <summary>
        /// ⭐ LS 边界绊线（T1b 任务 b2）：把"零记录 `TextLineBreak` 交出去"这件事**当失败上报**。
        ///
        /// 【为什么是失败】路由 B 的 `TextLine` 是插进**可能仍属于 LineServices 的管线**里的。
        ///   零记录一旦被当作 `previousLineBreakRecord` 回传给 LS，就**不会崩在显眼处**，
        ///   而是按错的行断记录排版（最坏的一类）；而本工程的 Win32 shim 里
        ///   **`LoAcquireBreakRecord` / `LoCreateLine` 连符号都没有**（`nm -D libwpfwin32.so` 实测），
        ///   所以真走过去只会是 `EntryPointNotFoundException`。
        ///   ⇒ 这里**记数 + 打诊断**（`WPF_LINUX_TEXTLINE_DIAG=1`），STRICT 下直接抛。
        /// </summary>
        internal static void NoteZeroBreakRecordIssued(string where)
        {
            System.Threading.Interlocked.Increment(ref s_breakZeroRecordIssued);
            Diag($"LS_BOUNDARY 零记录 TextLineBreak 已交出（{where}）；真机 TextMetrics.cs:299-308 只在 modifier 分支 new 它，"
                 + "而我们不伪造原生断行记录 ⇒ 该记录若被回传给 LS 一定会撞 EntryPointNotFoundException"
                 + "（shim 未导出 LoAcquireBreakRecord / LoCreateLine）");
            if (Strict)
                throw new NotSupportedException(
                    "HbTextLine.GetTextLineBreak 交出的是**零记录** TextLineBreak（B2 刻意不伪造 LS 断行记录）；"
                    + "WPF_LINUX_TEXTLINE_STRICT=1 时按失败上报。");
        }

        /// <summary>回落使用一次欠账实现（仍有 6 个真欠账）。</summary>
        internal static void NoteFallback(string member)
        {
            int i = Array.IndexOf(OwedMembers, member);
            if (i >= 0) System.Threading.Interlocked.Increment(ref s_hits[i]);
            InstallDump();
        }

        internal static void Diag(string message)
        {
            if (!DiagEnabled) return;
            try { Console.Error.WriteLine("[TEXTLINE_DIAG] " + message); } catch { }
        }

        /// <summary>清计数（测试用）。</summary>
        public static void ResetCounters()
        {
            for (int i = 0; i < s_hits.Length; i++) System.Threading.Interlocked.Exchange(ref s_hits[i], 0);
            System.Threading.Interlocked.Exchange(ref s_linesConstructed, 0);
            System.Threading.Interlocked.Exchange(ref s_drawCalls, 0);
            System.Threading.Interlocked.Exchange(ref s_getTextBoundsCalls, 0);
            System.Threading.Interlocked.Exchange(ref s_getTextRunSpansCalls, 0);
            System.Threading.Interlocked.Exchange(ref s_paragraphsFormatted, 0);
            System.Threading.Interlocked.Exchange(ref s_breakNullReturned, 0);
            System.Threading.Interlocked.Exchange(ref s_breakZeroRecordIssued, 0);
            System.Threading.Interlocked.Exchange(ref s_modifierLines, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseCalls, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseEarlyReturnIneligible, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseEarlyReturnTooWide, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseEmptyArgsThrow, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseApplied, 0);
            System.Threading.Interlocked.Exchange(ref s_collapseUnsupported, 0);
            System.Threading.Interlocked.Exchange(ref s_collapsedRangesNull, 0);
            System.Threading.Interlocked.Exchange(ref s_collapsedRangesReturned, 0);
            System.Threading.Interlocked.Exchange(ref s_forcedBreakLines, 0);
            System.Threading.Interlocked.Exchange(ref s_kinsokuPullTotal, 0);
            System.Threading.Interlocked.Exchange(ref s_dependentLengthQueries, 0);
            System.Threading.Interlocked.Exchange(ref s_invertedLines, 0);
        }

        /// <summary>
        /// 一行汇总（主控要的"一条汇总行"）。格式稳定，便于 grep/断言。
        /// **前 6 项 + 9 个欠账分量是 2026-09-10 起的既有格式（绊线脚本的锚点），只追加不改名。**
        /// </summary>
        public static string SummaryLine()
        {
            var sb = new StringBuilder();
            sb.Append("HB_TEXTLINE enabled=").Append(Enabled ? 1 : 0);
            sb.Append(" lines=").Append(LinesConstructed);
            sb.Append(" draw=").Append(DrawCalls);
            sb.Append(" getTextBounds=").Append(GetTextBoundsCalls);
            sb.Append(" getTextRunSpans=").Append(GetTextRunSpansCalls);
            sb.Append(" fallbackTotal=").Append(TotalFallbackHits);
            for (int i = 0; i < OwedMembers.Length; i++)
                sb.Append(' ').Append(OwedMembers[i]).Append('=').Append(System.Threading.Interlocked.Read(ref s_hits[i]));
            // ---- B2 追加（纯追加，不动上面的字段）----
            sb.Append(" paragraphs=").Append(ParagraphsFormatted);
            sb.Append(" breakNull=").Append(BreakNullReturned);
            sb.Append(" breakZeroRecords=").Append(BreakZeroRecordIssued);
            sb.Append(" modifierLines=").Append(ModifierLines);
            sb.Append(" collapseCalls=").Append(CollapseCalls);
            sb.Append(" collapseIneligible=").Append(CollapseEarlyReturnIneligible);
            sb.Append(" collapseTooWide=").Append(CollapseEarlyReturnTooWide);
            sb.Append(" collapseEmptyArgsThrow=").Append(CollapseEmptyArgsThrow);
            sb.Append(" collapseApplied=").Append(CollapseApplied);
            sb.Append(" collapseUnsupported=").Append(CollapseUnsupported);
            sb.Append(" collapsedRangesNull=").Append(CollapsedRangesNull);
            sb.Append(" collapsedRangesReturned=").Append(CollapsedRangesReturned);
            sb.Append(" forcedBreakLines=").Append(ForcedBreakLines);
            sb.Append(" kinsokuPulls=").Append(KinsokuPullTotal);
            sb.Append(" dependentLengthQueries=").Append(DependentLengthQueries);
            sb.Append(" invertedLines=").Append(InvertedLines);
            sb.Append(" invertedNoWidth=").Append(InvertedNoWidth);
            sb.Append(" antiMatrixMismatch=").Append(AntiMatrixMismatch);
#if TEXTLINE_SHIM_DIRECT
            sb.Append(' ').Append(HbTextFallback.SummaryFragment());
#endif
            return sb.ToString();
        }

        /// <summary>把汇总写进 `WPF_LINUX_TEXTLINE_DUMP` 指定的文件（进程退出时自动写一次）。</summary>
        public static void Dump(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.AppendAllText(path, SummaryLine() + Environment.NewLine);
            }
            catch
            {
                // 落盘失败不影响主链路
            }
        }

        // ==========================================================================================
        //  逐行 dump（handoff:403 的欠账）：`WPF_LINUX_TEXTLINE_PERLINE=1`
        //    · 缺省**关**；落盘位置 = `WPF_LINUX_TEXTLINE_DUMP` 指定的文件，未配则 stderr。
        //    · **有界**：最多 `PerLineBudget` 条逐行记录 + `SpanQBudget` 条查询补记，之后只打一次"预算用尽"。
        //    · **惰性纪律（本项目的血账：探针把实参提前求值 ⇒ 静态初始化炸）**：
        //      `NoteLine()` 的**第一条语句**就是"缓存整数比较"，关时**不读环境变量、不建字符串、不装箱、不遍历**；
        //      唯一建串处 `HbTextLine.LineDumpTuple()` 只在开关打开后才被调用。
        // ==========================================================================================
        public const string PerLineEnvVar = "WPF_LINUX_TEXTLINE_PERLINE";
        private const int PerLineBudget = 1000;
        private const int SpanQBudget = 200;
        private static int s_perLine = -1;              // -1 未解析 / 0 关 / 1 开
        private static int s_lineSeq;                   // 已写出的逐行条数（同时充当 seq）
        private static int s_spanQSeq;
        private static int s_perLineBudgetMsg;
        private static string s_perLineDest;            // null = 未解析；"" = 用 stderr

        internal static bool PerLineEnabled
        {
            get
            {
                if (s_perLine < 0)
                    s_perLine = ParseOnOff(Environment.GetEnvironmentVariable(PerLineEnvVar)) ? 1 : 0;
                return s_perLine == 1;
            }
        }

        /// <summary>
        /// 逐行 dump 入口（由 `HbTextLine` 构造器末尾调用一次/行）。
        /// 返回本行的 dump 序号（0 = 未 dump），供 `GetTextRunSpans()` 事后补记"这一行被问过"。
        /// </summary>
        internal static int NoteLine(HbTextLine line)
        {
            if (!PerLineEnabled) return 0;                                   // ← 关时到此结束：零分配/零遍历
            int seq = System.Threading.Interlocked.Increment(ref s_lineSeq);
            if (seq > PerLineBudget)
            {
                if (System.Threading.Interlocked.Exchange(ref s_perLineBudgetMsg, 1) == 0)
                    WritePerLine("HBLINE_LINE **预算用尽**（已写 " + PerLineBudget + " 行，后续不再写）");
                return 0;
            }
            WritePerLine(line.LineDumpTuple(seq));                            // 唯一建串处
            return seq;
        }

        /// <summary>某个**已被 dump** 的行事后被问了 `GetTextRunSpans()` ⇒ 补记一条（关时调用方用 `_dumpSeq != 0` 挡掉）。</summary>
        internal static void NoteSpanQueryOnLine(HbTextLine line, int seq)
        {
            if (!PerLineEnabled || seq == 0) return;
            if (System.Threading.Interlocked.Increment(ref s_spanQSeq) > SpanQBudget) return;
            WritePerLine("HBLINE_LINEQ#" + seq
                         + " cpFirst=" + line.LineStartForDiag
                         + " cpLast=" + (line.LineStartForDiag + line.Length)
                         + " spans=1");
        }

        private static void WritePerLine(string s)
        {
            try
            {
                if (s_perLineDest == null)
                {
                    string p = Environment.GetEnvironmentVariable(DumpEnvVar);
                    s_perLineDest = string.IsNullOrEmpty(p) ? string.Empty : p;
                }
                if (s_perLineDest.Length == 0) Console.Error.WriteLine(s);
                else File.AppendAllText(s_perLineDest, s + Environment.NewLine);
            }
            catch
            {
                // 落盘失败不影响主链路
            }
        }

        private static void InstallDump()
        {
            if (System.Threading.Interlocked.Exchange(ref s_dumpInstalled, 1) != 0) return;
            string path = Environment.GetEnvironmentVariable(DumpEnvVar);
            if (string.IsNullOrEmpty(path)) return;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Dump(path);
        }

        /// <summary>进程退出时无条件落盘（设了 dump 路径时）。构造第一行时调用一次。</summary>
        internal static void EnsureDumpInstalled() => InstallDump();
    }

    // ==================================================================================
    //  4. TextRunProperties / TextRun 的最小实现
    // ==================================================================================

    /// <summary>`TextRunProperties` 的最小实现（8 个 abstract 成员）。</summary>
    internal sealed class HbRunProperties : TextRunProperties
    {
        private readonly Typeface _typeface;
        private readonly Brush _foreground;

        internal HbRunProperties(Typeface typeface, double emSize, Brush foreground)
        {
            _typeface = typeface;
            _foreground = foreground;
            FontRenderingEmSizeValue = emSize;
        }

        internal double FontRenderingEmSizeValue { get; set; }

        public override Typeface Typeface => _typeface;
        public override double FontRenderingEmSize => FontRenderingEmSizeValue;
        public override double FontHintingEmSize => FontRenderingEmSizeValue;
        public override CultureInfo CultureInfo => CultureInfo.InvariantCulture;
        public override Brush ForegroundBrush => _foreground;
        public override Brush BackgroundBrush => null;
        public override TextDecorationCollection TextDecorations => null;
        public override TextEffectCollection TextEffects => null;
    }

    /// <summary>`TextRun` 的最小实现（3 个 abstract 成员）。</summary>
    internal sealed class HbTextRun : TextRun
    {
        internal HbTextRun(string text, int offset, int length, TextRunProperties props)
        {
            Text = text; Offset = offset; LengthValue = length; Props = props;
            CharacterBufferReferenceValue = new CharacterBufferReference(text, offset);
        }

        internal string Text { get; }
        internal int Offset { get; }
        internal int LengthValue { get; }
        internal TextRunProperties Props { get; }
        internal CharacterBufferReference CharacterBufferReferenceValue { get; }

        public override CharacterBufferReference CharacterBufferReference => CharacterBufferReferenceValue;
        public override int Length => LengthValue;
        public override TextRunProperties Properties => Props;
    }

    // ==================================================================================
    //  5. HbTextLine —— TextLine 契约实现（B2：三个成员做**实**）
    // ==================================================================================

    /// <summary>
    /// 用 HarfBuzz shaping 驱动的 `TextLine`。
    ///
    /// **契约规模**：`TextLine` 是 public abstract，共 **19 个 abstract 属性 + 13 个 abstract 方法 = 32 个成员**。
    /// 本类真实现：`GetTextRunSpans` / `GetTextBounds` / `Draw` / `Collapse` / `GetTextCollapsedRanges`
    /// / `GetTextLineBreak` / `Dispose` + **19 个属性**；其余 **6 个方法按"安全回退 + 计数"**（B3 做）。
    ///
    /// **对齐哪一条真机实现**：本类对齐 `MS.Internal.TextFormatting.TextMetrics+FullTextLine`
    /// （行断/折叠的真值都出自它）。真机里 `SimpleTextLine` 的 `GetTextLineBreak`/`GetTextCollapsedRanges`
    /// **恒为 null**（`SimpleTextLine.cs:973/983`）—— 那只是快路径；本类不复刻"永远 null"的捷径，
    /// 在**可折叠行**上会真的折叠（= FullTextLine 行为）。
    /// </summary>
    /// <summary>
    /// 「多行摞印」定位用的**只读**逐行插桩（主控 2026-09-11 派单）。
    ///
    /// 【它回答什么】宿主到底让我们画哪一行、**给的原点是多少**、我们把 glyph run 的
    ///   `BaselineOrigin` 设成了什么 —— 三件事放在同一行里，判据一眼可读。
    ///
    /// 【上游契约（判据的规范依据，`SimpleTextLine.cs:572-605` 原文）】
    ///     `double y = origin.Y + Baseline;`
    ///     `run.Draw(dc, IdealToReal(idealXRelativeToOrigin) + origin.X, y, false);`
    ///   ⇒ `TextLine.Draw(dc, origin, inversion)` **必须**把宿主给的 `origin` 带进绘制
    ///     （基线落在 `origin.Y + Baseline`、x 平移 `origin.X`）。本 shim 的 `Draw` 现在只用
    ///     自己算的 `(pen, baseline)`；**是否构成摞印由本插桩的读数回答，不由推断**。
    ///
    /// 【纪律】
    ///   · **缺省关**：只有 `WPF_LINUX_HBLINE_TRACE=1` 才输出，且**直接写 `Console.Error`**
    ///     （不经 `Diag()` 的门控 —— 否则"只设本开关"时一行都不打，本项目栽过）；
    ///   · **每站点独立预算**（T1c 刚踩过"多站点共享预算 ⇒ 最晚的站点永远看不见"）；
    ///   · **纯只读**：不发 HarfBuzz 调用、不取面令牌（`GetDWriteFontAddRef` 会登记）、
    ///     **不碰任何计数器**、不改调用次数与返回结构 ⇒ 对行为零影响。
    /// </summary>
    internal static class HbLineTrace
    {
        internal const string EnvVar = "WPF_LINUX_HBLINE_TRACE";
        private const int BudgetPerSite = 60;
        private static int s_budgetA = BudgetPerSite, s_budgetB = BudgetPerSite, s_budgetC = BudgetPerSite;
        private static int s_budgetD = BudgetPerSite;
        private static int s_budgetE = BudgetPerSite;
        private static int s_on = -1;

        internal static bool Enabled
        {
            get
            {
                if (s_on < 0)
                    s_on = HbTextLineScaffold.ParseOnOff(Environment.GetEnvironmentVariable(EnvVar)) ? 1 : 0;
                return s_on == 1;
            }
        }

        /// <summary>站点 A：`FormatParagraph` 每产出**一行**（此时还没有宿主原点 ⇒ 打行内原点）。</summary>
        internal static void SiteA(int index, HbTextLine line)
            => Emit("A", index, ref s_budgetA, line, line.GlyphRunsForRasterization, 0, 0, false);

        /// <summary>
        /// 站点 B：`Draw` 收到的一次调用。
        /// <paramref name="drawn"/> 是**我们真正交给 `DrawGlyphRun` 的那批 run**（原点已烘进宿主原点）；
        /// 判据 `Δ = firstRunOriginY − (hostOriginY + Baseline)` **期望恒 0**。
        /// </summary>
        internal static void SiteB(int index, HbTextLine line, List<GlyphRun> drawn, Point origin, InvertAxes inv)
            => Emit("B", index, ref s_budgetB, line, drawn, origin.X, origin.Y, true, inv);

        private static void Emit(string site, int index, ref int budget, HbTextLine line,
                                 IList<GlyphRun> runs, double originX, double originY, bool withHostOrigin,
                                 InvertAxes inv = InvertAxes.None)
        {
            if (!Enabled || line == null) return;
            if (System.Threading.Interlocked.Decrement(ref budget) < 0) return;   // 本站点自己的预算

            var sb = new StringBuilder();
            sb.Append("HBLINE ").Append(site).Append('#').Append(index)
              .Append(" cpFirst=").Append(line.LineStartForDiag)
              .Append(" cpLast=").Append(line.LineStartForDiag + line.Length)
              .Append(" len=").Append(line.Length)
              .Append(" nl=").Append(line.NewlineLength)
              .Append(" h=").Append(line.Height.ToString("F4"))
              .Append(" bl=").Append(line.Baseline.ToString("F4"))
              .Append(" w=").Append(line.Width.ToString("F4"));
            if (withHostOrigin)
                sb.Append(" hostOrigin=(").Append(originX.ToString("F4")).Append(',')
                  .Append(originY.ToString("F4")).Append(')');
            sb.Append(" glyphRuns=").Append(runs == null ? 0 : runs.Count);
            sb.Append(withHostOrigin ? " runOrigins(已烘入宿主原点)=[" : " runOrigins(行内)=[");
            double firstY = 0;
            int dumped = 0;
            if (runs != null)
                foreach (GlyphRun gr in runs)
                {
                    Point o = gr.BaselineOrigin;             // 只读：不碰令牌、不碰面
                    if (dumped > 0) sb.Append(' ');
                    sb.Append('(').Append(o.X.ToString("F3")).Append(',').Append(o.Y.ToString("F3")).Append(')');
                    if (dumped == 0) firstY = o.Y;
                    if (++dumped >= 8) { sb.Append(" …"); break; }
                }
            sb.Append(']');
            if (withHostOrigin)
            {
                // ⭐ 主控要的判据：Δ = firstRunOriginY − (hostOriginY + Baseline) ⇒ **期望恒 0**
                sb.Append(" delta(firstRunOriginY-(hostOriginY+Baseline))=")
                  .Append((firstY - (originY + line.Baseline)).ToString("F4"))
                  .Append(" inv=").Append((int)inv);
            }
            // ⭐ T1d §10（2026-09-13）：**段落宽 / 行宽 / 差** —— 三格只读读数。
            //   `pw` = `FormatLine` 给的 `paragraphWidth`（既是换行基准，也是 `Draw` 里反镜像矩阵的 offsetX）；
            //   `W`  = 本行实际宽度（= 原来那个 `w=`，这里再显式给一份便于逐行对照）；
            //   `pw-W` ⇒ **0 附近**说明"镜像就地"（位置不变的纯顺序翻转）；**明显 >0** 说明镜像会把 run 推到行内右侧。
            //   只读：不参与任何计算，不碰计数器，不改调用次数。
            sb.Append(" pw=").Append(line.ParagraphWidthForDiag.ToString("F4"))
              .Append(" W=").Append(line.Width.ToString("F4"))
              .Append(" pw-W=").Append((line.ParagraphWidthForDiag - line.Width).ToString("F4"));
            try { Console.Error.WriteLine(sb.ToString()); } catch { }
            if (budget == 0)
            {
                try { Console.Error.WriteLine("HBLINE 站点" + site + " **预算用尽**（已打 " + BudgetPerSite + " 行，后续不再打）"); }
                catch { }
            }
        }

        /// <summary>
        /// 站点 D：`Draw` 里**每张 GlyphRun 实际拿到的 foreground 画刷**（P0 线索：run 属性有没有跟着子段走）。
        /// 缺省关、独立预算；纯只读（只读 `Brush`，不构造、不改色）。
        /// </summary>
        internal static void SiteD(int index, HbTextLine line, IList<GlyphRun> runs, Brush brush)
        {
            if (!Enabled || line == null) return;
            if (System.Threading.Interlocked.Decrement(ref s_budgetD) < 0) return;

            string col;
            if (brush == null) col = "<null：等于不画>";
            else if (brush is SolidColorBrush scb) col = scb.Color.ToString();
            else col = brush.GetType().Name;

            var sb = new StringBuilder();
            sb.Append("HBLINE D#").Append(index)
              .Append(" cpFirst=").Append(line.LineStartForDiag)
              .Append(" brush=").Append(col)
              .Append(" glyphRuns=").Append(runs == null ? 0 : runs.Count);
            if (runs != null)
            {
                int k = 0;
                foreach (GlyphRun gr in runs)
                {
                    var cs = new StringBuilder();
                    if (gr.Characters != null) foreach (char c in gr.Characters) cs.Append(c);
                    sb.Append(" [r").Append(k++).Append(" face=")
                      .Append(gr.GlyphTypeface != null ? System.IO.Path.GetFileName(gr.GlyphTypeface.FontUri.LocalPath) : "?")
                      .Append(" glyphs=").Append(gr.GlyphIndices.Count)
                      .Append(" chars=\"").Append(cs.Length > 24 ? cs.ToString(0, 24) + "…" : cs.ToString()).Append("\"]");
                    if (k >= 8) break;
                }
            }
            try { Console.Error.WriteLine(sb.ToString()); } catch { }
            if (s_budgetD == 0)
            {
                try { Console.Error.WriteLine("HBLINE 站点D **预算用尽**（已打 " + BudgetPerSite + " 行，后续不再打）"); }
                catch { }
            }
        }

        /// <summary>
        /// 站点 E：`Draw` 的**几何**（宿主 `origin` / `pw` / `W` / `H` / **推入的反演矩阵** / 每个 run 的**行内与宿主坐标区间**）。
        /// 【判据（互斥）】`Σadvance(r0) == W` 且 `x_last − x_first == W` ⇒ **run 的行内区间正常** ⇒
        ///   缩放/左移**不在 shim 内**（差异在宿主或 MIL 层）；若行内区间只有 ~45% ⇒ 在 shim 内。
        /// 【CTM】`DrawingContext` 是**抽象类、无公开 CTM**（`upstream/…/Media/DrawingContext.cs`）⇒ 本行只能报"读不到"。
        /// 纯只读：只读 `GlyphRun.BaselineOrigin` / `AdvanceWidths` / `GlyphIndices.Count`，不碰令牌与面、不改调用次数。
        /// </summary>
        internal static void SiteE(int index, HbTextLine line, IList<GlyphRun> drawn, Point origin,
                                   MatrixTransform anti, InvertAxes inv)
        {
            if (!Enabled || line == null) return;
            if (System.Threading.Interlocked.Decrement(ref s_budgetE) < 0) return;

            var sb = new StringBuilder();
            sb.Append("HBLINE E#").Append(index)
              .Append(" hostOrigin=(").Append(origin.X.ToString("F4")).Append(',').Append(origin.Y.ToString("F4")).Append(')')
              .Append(" pw=").Append(line.ParagraphWidthForDiag.ToString("F4"))
              .Append(" W=").Append(line.Width.ToString("F4"))
              .Append(" H=").Append(line.Height.ToString("F4"))
              .Append(" inv=").Append((int)inv)
              .Append(" anti=");
            if (anti == null) sb.Append("None(未推)");
            else
            {
                Matrix m = anti.Matrix;
                sb.Append('(').Append(m.M11.ToString("F4")).Append(',').Append(m.M22.ToString("F4"))
                  .Append(",offX=").Append(m.OffsetX.ToString("F4")).Append(",offY=").Append(m.OffsetY.ToString("F4")).Append(')');
            }
            sb.Append(" ctm=读不到(契约无公开CTM) runs=").Append(drawn == null ? 0 : drawn.Count);

            double sumAdv = 0, xEnd = 0;
            if (drawn != null)
                for (int i = 0; i < drawn.Count && i < 8; ++i)
                {
                    GlyphRun gr = drawn[i];
                    double adv = 0;
                    IList<double> aw = gr.AdvanceWidths;
                    if (aw != null) for (int k = 0; k < aw.Count; ++k) adv += aw[k];
                    double bx = gr.BaselineOrigin.X, by = gr.BaselineOrigin.Y;
                    double lx0 = bx - origin.X, ly0 = by - origin.Y, lx1 = lx0 + adv;
                    sb.Append(" [r").Append(i)
                      .Append(" bo=(").Append(lx0.ToString("F3")).Append(',').Append(ly0.ToString("F3")).Append(')')
                      .Append(" n=").Append(gr.GlyphIndices != null ? gr.GlyphIndices.Count : 0)
                      .Append(" adv=").Append(adv.ToString("F4"))
                      .Append(" x=[").Append(lx0.ToString("F3")).Append(',').Append(lx1.ToString("F3")).Append(']')
                      .Append(" absX=[").Append(bx.ToString("F3")).Append(',').Append((bx + adv).ToString("F3")).Append("]]");
                    xEnd = lx1;
                    sumAdv += adv;
                }
            // 判据格（**逐 run 累加**，多 run 行才有意义）：
            //   `Σadv(all) − W == 0` 且 `xEnd − W == 0` ⇒ run 的行内区间**正常**（多 run 时各段首尾相接铺满 [0, W]）
            sb.Append(" Σadv(all)-W=").Append((sumAdv - line.Width).ToString("F4"))
              .Append(" xEnd(all)-W=").Append((xEnd - line.Width).ToString("F4"));

            try { Console.Error.WriteLine(sb.ToString()); } catch { }
            if (s_budgetE == 0)
            {
                try { Console.Error.WriteLine("HBLINE 站点E **预算用尽**（已打 " + BudgetPerSite + " 行，后续不再打）"); }
                catch { }
            }
        }

        /// <summary>站点 C：`TryFormatLine` 交出**整段**时（逐行 cp/高/基线打全）。</summary>
        internal static void SiteC(int cpFirst, List<HbTextLine> lines)
        {
            if (!Enabled || lines == null) return;
            if (System.Threading.Interlocked.Decrement(ref s_budgetC) < 0) return;

            var sb = new StringBuilder();
            sb.Append("HBLINE C#0 段起点=").Append(cpFirst).Append(" 行数=").Append(lines.Count).Append(" perLine=[");
            int pos = cpFirst;
            for (int i = 0; i < lines.Count && i < 12; ++i)
            {
                HbTextLine L = lines[i];
                if (i > 0) sb.Append(' ');
                sb.Append(pos).Append(":h").Append(L.Height.ToString("F2")).Append(":bl").Append(L.Baseline.ToString("F2"));
                pos += L.Length;
            }
            sb.Append(']');
            try { Console.Error.WriteLine(sb.ToString()); } catch { }
            if (s_budgetC == 0)
            {
                try { Console.Error.WriteLine("HBLINE 站点C **预算用尽**（已打 " + BudgetPerSite + " 行，后续不再打）"); }
                catch { }
            }
        }
    }

    internal sealed class HbTextLine : TextLine
    {
        private const string Ellipsis = "\u2026";   // 与上游 `TextTrailing*Ellipsis` 的常量一致

        // ---- 构造期固定 ----
        private readonly string _text;              // 源文本（段落系索引就是它的下标）
        private readonly int _lineStart;            // 行起始（**相对** `_text`/`_plan` 的下标）—— 语义与赋值 `#23` **一律不动**
        /// <summary>
        /// ★`#23` P2（`D-T6-b`，**Option 1**）：本行所属段落在**调用方 `TextSource`** 里的原点
        /// （= 收集该段时用的 `cpFirst`；缺省 `0`）。
        /// **帧的绝对系 = `_paragraphOrigin + _lineStart`** —— 真机的"帧"就是
        /// `cases[].lines[].startChar`（真机臂 `src/Linux/tests/parity/windows/tab-anchor/src/Program.cs:443/556`
        /// 传进 `FormatLine` 的 `index`）与 `GetTextBounds` 的**第一个参数**（同一套段落系）。
        ///
        /// 【为什么**新增字段**而不是改 `_lineStart`】`_lineStart` 是**双用字段**（`#22` §4 逐点表）：
        ///   · **相对**（下标进 `_text`/`_plan`）：`:3612` `_text[_lineStart + …]`、
        ///     `:3645` `_text.Substring(_lineStart, …)`、`:3652` `_plan.Sub(_lineStart, …)`；
        ///   · **绝对**（段落系）：`GetTextBounds`、`GetIndexedGlyphRuns`、`_collapsedRange`。
        ///   ⇒ 若按预登记字面写 `_lineStart = paragraphOrigin + range.Start`，折叠路径会拿**绝对**下标去切
        ///   **相对**串（`ArgumentOutOfRangeException` 或切到错文本），而 `Collapse` 在**真机消费路径**上
        ///   （`upstream/wpf/…/PresentationFramework/MS/Internal/Text/Line.cs:165`）⇒ 不是理论风险。
        ///   ⇒ 原点**单存一份**，只让**绝对消费者**改用 `_paragraphOrigin + _lineStart`。
        /// </summary>
        private readonly int _paragraphOrigin;
        private readonly int _visibleLength;        // 可见文本长度（不含硬断字符）
        private readonly int _hardBreakLen;         // 本行末尾硬断字符个数（0/1/2）
        private readonly bool _hasEop;              // 末行 ⇒ EOP 字符（Length +1）
        /// <summary>
        /// T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：段末行的末 span **交回源自己的那个** EOL run
        /// （收集时在段末取到的那个对象），而不是就地新造一个 `TextEndOfParagraph(1)`。
        ///
        /// 【为什么这是承重的】宿主 `Line.FormattingResult`（`Line.cs:928-936`）**只认** `ParagraphBreakRun`／
        ///   `LineBreakRun`（两者都是 `PresentationFramework` 的 internal 类型，本 shim 编在 `PresentationCore`
        ///   造不出来）⇒ 修前段末行恒报 `fsflrOutOfSpace` ⇒ 驱动收不到"段末"，把行宽里那 1 个合成位也累加进
        ///   `dcp`，下一跳就探到**下一个 Block 的 `ElementStart`** ⇒ `Invariant.FailFast`（`LineBase.cs:137`，
        ///   不可捕获）。交回源对象则 `Line.EndOfParagraph`（`Line.cs:841-853`）成立、`FormattingResult` 报
        ///   `fsflrEndOfParagraph` ⇒ 驱动在段末收束，**不越界**。
        /// 【类型安全】`ParagraphBreakRun : TextEndOfParagraph`（`RunClient.cs:203`）⇒ 既有消费者
        ///   `TextBoxLine.EndOfParagraph`（`:362-371`，判 `is TextEndOfParagraph`）**不受影响**。
        /// `null` ⇒ 退回旧行为（合成 run），逐位相同。
        /// </summary>
        private readonly TextRun _eopRun;
        private readonly double[] _charAdvances;    // 可见文本逐字 advance
        private readonly double _startPenX;         // 内容起点（container 系）= **`Indent + ParagraphIndent`**（件 2 起；旧注只写 `Indent`，与代码不符 ⇒ 本次改正）
        // D-O1：**行盒原点** = `ParagraphIndent`。`_width` 是**盒坐标系**的宽（= `Indent` + 内容宽，不含 PI）
        //   ⇒ 内容右缘（container 系）= `_boxOriginX + _width`。`_startPenX` 是合量，**推不出** PI ⇒ 必须单存。
        private readonly double _boxOriginX;
        /// <summary>
        /// `#21`（`D-T6-c`）：本行的**段落缩进** `TextParagraphProperties.ParagraphIndent`（**原始 DIP**）。
        /// 用途**只有一个**：`TextLine.Start` 的法律 —— 真机 `Start ≡ IdealToReal(ParagraphIndent)`，
        /// 由 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/MS/internal/TextFormatting/TextMetrics.cs:355-358`
        /// 的 `IdealToReal(_paragraphToText - _textStart, _pixelsPerDip)` 配上同文件 `:255-263` 的
        /// `default:`(=Left) 分支 `_paragraphToText = pap.ParagraphIndent + _textStart` 而来
        /// —— `_textStart`（"LS origin → text start"，首行缩进/前导空白时**非零**）**精确相消**
        /// ⇒ `Start` 与 `Indent`、与 `_textStart` 是否非零**都无关**，只等于 `ParagraphIndent`。
        ///
        /// 【为什么必须**单存**、不许从 `_boxOriginX` / `_startPenX` 反推】
        ///   `_startPenX = Indent + ParagraphIndent` 是**合量**（见上面 `:2633`）⇒ **推不出** PI。
        ///   `_boxOriginX` 今天的**取值**确实是 PI，但那是**盒几何**语义（渲染布局用）——
        ///   让 `Start` 去依赖一个渲染量，下次动盒坐标就会**静默**改掉 `Start` ⇒ 各存一份是
        ///   "两个语义各自有主"（与本文件 `:2635` 那句"必须单存"同一形态）。
        /// 【未测】折叠行（`BuildCollapsedLine`）在 `PI≠0` 下的 `Start` 真值**本语料没有覆盖**
        ///   ⇒ 那里转发 PI 只是"为保持成员自洽"的选择，**不是实测结论**（见 `#21` 报告"未测清单"）。
        /// </summary>
        private readonly double _paragraphIndentDip;
        private readonly string _fontPath;
        private readonly GlyphTypeface _glyphTypeface;
        private readonly float _pixelsPerDip;
        private readonly TextRunProperties _runProperties;
        private readonly HbTextRun _run;
        private readonly HbShapedRun _shaped;
        private readonly GlyphRun _glyphRun;        // 空行（0 字形）时为 null —— GlyphRun 不接受空列表
        /// <summary>
        /// R1/T1d：本行实际交出去的 `GlyphRun`（**每个字体子段一个**）。
        /// 长度 0 = 空行；长度 1 = 单面（= 今天的行为，逐位相同）。
        ///
        /// 【为什么必须按段拆】**渲染用哪份面是由 `GlyphRun` 自带的 `GlyphTypeface` 决定的**：
        ///   上游 `GlyphRun.cs:1876` `command.pIDWriteFont = (UInt64)_glyphTypeface.GetDWriteFontAddRef`
        ///   → MIL 侧 `MilFontFaceTable` 反查 → `SKTypeface.FromFile(path, faceIndex)`。
        ///   一个 `GlyphRun` 只能带一份面 ⇒ 混排行若只发一个 run，CJK 的 glyph id 会用拉丁面去画
        ///   （普查全绿、像素全是垃圾 —— 这正是本项目反复栽的"假绿"）。
        /// </summary>
        private readonly List<GlyphRun> _glyphRuns;
        /// <summary>与 <see cref="_glyphRuns"/> 一一对应的**段落系**字符起点（诊断用：把"哪一段字"说清楚）。</summary>
        private readonly List<int> _glyphRunCharStart;
        /// <summary>与 <see cref="_glyphRuns"/> 一一对应的**源 run 序号**（决定这张 run 用哪把 foreground）。</summary>
        private readonly List<int> _glyphRunRunSlot;
        /// <summary>每个源 run 的 foreground（`null` = 没传 ⇒ 一律用行级 `_run.Props.ForegroundBrush`，即今天的行为）。</summary>
        private readonly Brush[] _runBrushes;
        /// <summary>把宿主行原点烘进去之后的那批 `GlyphRun`（缓存：同一 origin 复用）。</summary>
        private List<GlyphRun> _positionedRuns;
        private Point _positionedAt;
        /// <summary>`Extent`（墨迹盒高度）的惰性缓存；NaN = 还没算。行构造后不变 ⇒ 算一次即可。</summary>
        private double _extentCache = double.NaN;
        /// <summary>
        /// 本行**所在段落的宽度** —— `InvertAxes.Horizontal` 的镜像基准（上游 `InvertAxes.Horizontal`
        /// 就是 `x' = paragraphWidth − x`，见 `TextFormatterImp.cs:582-586`）。
        /// 0 = 未知 ⇒ 那时**不镜像**（并计数 + 打诊断，绝不静默画错）。
        /// </summary>
        private readonly double _paragraphWidth;
        /// <summary>每个字体子段的渲染面（由 <see cref="HbFontSegment.FaceSlot"/> 索引）。</summary>
        private readonly GlyphTypeface[] _segmentFaces;
        /// <summary>本行所属段落的"面计划"（单面路径为 null；折叠时要按同一套面重整形）。</summary>
        private readonly HbFontPlan _plan;
        private readonly bool _keepState;           // = 上游 StatusFlags.KeepState（AlwaysCollapsible）
        private readonly bool _hasModifierScope;    // 源里有 TextModifier ⇒ GetTextLineBreak 才非 null
        private readonly bool _forcedBreak;         // 规则 3 产出（"内容没放下"的**诊断**信息）

        // ---- 记账（构造后不再变；折后行由 BuildCollapsedLine 直接给覆盖值）----
        private readonly int _length;
        private readonly int _newlineLength;
        private readonly int _trailingWhitespaceLength;
        private readonly double _width;
        private readonly double _widthIncludingTrailingWhitespace;

        // ---- 折叠态 ----
        private TextCollapsedRange _collapsedRange;
        private bool _hasCollapsed;
        private bool _disposed;

        /// <summary>单 run 便捷构造（B1 原型路径 / 整串就是一行）。</summary>
        internal HbTextLine(HbTextRun run, HbShapedRun shaped, GlyphTypeface glyphTypeface, float pixelsPerDip)
            : this(run, shaped, glyphTypeface, pixelsPerDip, shaped.Text, 0, null, 0, false, false, false, false, 0,
                   shaped.Text.Length,
                   0,
                   TrailingWhitespaceCount(shaped.Text),
                   WidthWithoutTrailingWhitespace(shaped),
                   shaped.TotalWidthPx)
        { }

        private HbTextLine(HbTextRun run, HbShapedRun shaped, GlyphTypeface glyphTypeface, float pixelsPerDip,
                           string text, int lineStart, string fontPath, int hardBreakLen, bool hasEop,
                           bool keepState, bool hasModifierScope, bool forcedBreak, double lineHeight,
                           int length, int newlineLength, int trailingWhitespaceLength, double width, double widthIncludingTrailingWhitespace,
                           GlyphTypeface[] segmentFaces = null, HbFontPlan plan = null,
                           double paragraphWidth = 0, TextRunProperties[] runProps = null,
                           double startPenX = 0, double boxOriginX = 0, double paragraphIndentDip = 0,
                           // ★`#23` P2（`D-T6-b`）：段落在**调用方 `TextSource`** 里的原点（= 收集时的 `cpFirst`）。
                           //   尾随可选、缺省 0 ⇒ **既有调用点零改动、逐位等价**（0 = "段落原点就是 0"，与修前同）。
                           int paragraphOrigin = 0,
                           // T-A30：段末行要交回**源自己的** EOL run（见 `_eopRun` 的说明）；缺省 null ⇒ 逐位不变。
                           TextRun eopRun = null)
        {
            _plan = plan;
            _paragraphWidth = paragraphWidth;
            _startPenX = startPenX;
            _boxOriginX = boxOriginX;
            // `#21`：`TextLine.Start` 的唯一驱动量。**带默认值 0** ⇒ 既有调用点（单 run 便捷构造）
            //   **零改动、逐位等价**（0 是"无段落缩进"的正确取值）。
            _paragraphIndentDip = paragraphIndentDip;
            if (runProps != null)
            {
                _runBrushes = new Brush[runProps.Length];
                for (int i = 0; i < runProps.Length; ++i)
                    _runBrushes[i] = runProps[i] != null ? runProps[i].ForegroundBrush : null;
            }
            _run = run;
            _shaped = shaped;
            _text = text ?? string.Empty;
            _lineStart = lineStart;
            // `#23` P2：原点必须在**下面那两处播种**（`:2813` `starts.Add(_paragraphOrigin + …)`、
            //   `:2822` 的兜底 `new List<int> { _paragraphOrigin + lineStart }`）**之前**赋值 ⇒ 位置在此。
            _paragraphOrigin = paragraphOrigin;
            _fontPath = fontPath;
            _hardBreakLen = hardBreakLen;
            _hasEop = hasEop;
            _eopRun = eopRun;
            _glyphTypeface = glyphTypeface;
            _pixelsPerDip = pixelsPerDip;
            _runProperties = run != null ? run.Props : null;
            _keepState = keepState;
            _hasModifierScope = hasModifierScope;
            _forcedBreak = forcedBreak;
            _visibleLength = shaped.Text.Length;
            _charAdvances = shaped.CharAdvances();
            _length = length;
            _newlineLength = newlineLength;
            _trailingWhitespaceLength = trailingWhitespaceLength;
            _width = width;
            _widthIncludingTrailingWhitespace = widthIncludingTrailingWhitespace;

            // ⭐ 真机三条公式（`src/Linux/tests/parity/windows/layout-b34/results-cd2.json` 的 10 例 LineHeight 真值实测）：
            //   · `Height = LineHeight`（**未设 ⇒ 字体自然行高**）
            //   · `TextHeight` **恒为自然文本高**（不跟 LineHeight 走）
            //   · `Baseline = LineHeight × (自然Baseline / 自然Height)`
            //     实测：lh10 → 10×17.1033/21.7933 = 7.848 ≈ 真值 7.8500；lh30 → 23.542 ≈ 23.5467；lh50 → 39.237 ≈ 39.2433 ✓
            double naturalHeight = (glyphTypeface != null ? glyphTypeface.Height : 0) * shaped.EmSize;
            double naturalBaseline = (glyphTypeface != null ? glyphTypeface.Baseline : 0) * shaped.EmSize;
            _textHeight = naturalHeight;
            if (lineHeight > 0 && naturalHeight > 0)
            {
                _height = lineHeight;
                _baseline = lineHeight * (naturalBaseline / naturalHeight);
            }
            else
            {
                _height = naturalHeight;
                _baseline = naturalBaseline;
            }
            double baseline = _baseline;

            if (shaped.Glyphs.Length > 0)
            {
                if (shaped.Chunks == null || shaped.Chunks.Count <= 1)
                {
                    // ---- 单段：一个 GlyphRun、baseline origin = (0, baseline) ----
                    // ★W61A（`D-G57` 修法，2026-09-21）：**画的时候用的面必须与"字形 id 是从哪份面 shape 出来的"同一份**。
                    //   `ShapeParagraph`（本文件 `:653` 一带）是**逐段按 `seg.Face` 整形**的 ⇒ `shaped` 里的
                    //   字形 id 属于**计划里的那一份段面**，**不是**段落主面。旧版这里恒传 `glyphTypeface`
                    //   （= 段落**主面**）⇒ 只要整段恰好落成**一段回退面**（典型：全 CJK 文本 + 默认拉丁主面）
                    //   就会把回退面的字形 id 当成主面的 id 去画 ⇒ 越界 / `.notdef` ⇒ **零墨、零异常**
                    //   （`D-G57` 的判定点；现场读数：`[TEXTLINE_DIAG] R1 面计划 … slot=1` 而
                    //    `HBLINE D#… [r0 face=DejaVuSans.ttf glyphs=4 chars="样式模板"]`）。
                    //   下面 else（多段）分支本来就按 `segmentFaces[ch.SegmentIndex]` 取面 ⇒ 两分支**语义同源**；
                    //   多段时 `Count <= 1` 不成立 ⇒ 本改动**只影响单段行**（多段行逐字节不变）。
                    GlyphTypeface singleFace = glyphTypeface;
                    if (shaped.Chunks != null && shaped.Chunks.Count == 1)
                    {
                        int segSlot = shaped.Chunks[0].SegmentIndex;
                        if (segmentFaces != null && segSlot >= 0 && segSlot < segmentFaces.Length
                            && segmentFaces[segSlot] != null)
                        {
                            singleFace = segmentFaces[segSlot];
                        }
                        else if (segmentFaces != null && segmentFaces.Length == 1 && segmentFaces[0] != null
                                 && plan != null && plan.Segments.Count == 1)
                        {
                            // 就地面计划的单段路径（`BuildSegmentFacesFromPlan`）槽位语义等价 ⇒ 取唯一槽。
                            singleFace = segmentFaces[0];
                        }
                        else if (plan != null)
                        {
                            HbFallbackDiag.NoteRunFaceSlotMissing();   // D-F1b/P1c：有计划却拿不到面槽 ⇒ 记数（不静默）
                        }
                    }
                    _glyphRun = BuildGlyphRun(shaped, singleFace, pixelsPerDip, baseline, startPenX, shaped.Text);
                }
                else
                {
                    // ---- R1/T1d：**每个字体子段一个 GlyphRun**（各自的面 + 各自的 baseline origin）----
                    _segmentFaces = segmentFaces;
                    var runs = new List<GlyphRun>(shaped.Chunks.Count);
                    double[] adv = _charAdvances;
                    double pen = startPenX;                 // ★ Indent＝首行内容起点（真机：Indent 只移动起点，不移动 Tab 网格）
                    foreach (HbShapedChunk ch in shaped.Chunks)
                    {
                        if (ch.Run.Glyphs.Length == 0) continue;
                        GlyphTypeface face = glyphTypeface;
                        if (segmentFaces != null && ch.SegmentIndex >= 0 && ch.SegmentIndex < segmentFaces.Length
                            && segmentFaces[ch.SegmentIndex] != null)
                            face = segmentFaces[ch.SegmentIndex];
                        else if (plan != null) HbFallbackDiag.NoteRunFaceSlotMissing();   // D-F1b/P1c：有计划却拿不到面槽 ⇒ 记数
                        runs.Add(BuildGlyphRun(ch.Run, face, pixelsPerDip, baseline, pen, ch.Run.Text));
                        for (int i = ch.CharStart; i < ch.CharStart + ch.Run.Text.Length && i < adv.Length; ++i)
                            pen += adv[i];
                    }
                    if (runs.Count > 0) _glyphRun = runs[0];
                    _glyphRuns = runs;
                    var starts = new List<int>(runs.Count);
                    var slots = new List<int>(runs.Count);
                    foreach (HbShapedChunk ch in shaped.Chunks)
                        if (ch.Run.Glyphs.Length > 0) { starts.Add(_paragraphOrigin + lineStart + ch.CharStart); slots.Add(ch.RunSlot); }
                    _glyphRunCharStart = starts;
                    _glyphRunRunSlot = slots;
                    if (runs.Count > 1) HbFallbackDiag.ChunkedLines++;
                }
            }
            if (_glyphRuns == null)
                _glyphRuns = _glyphRun != null ? new List<GlyphRun> { _glyphRun } : new List<GlyphRun>();
            if (_glyphRunCharStart == null)
                // `#23` P2：兜底播种也进**绝对系**（否则 `GetIndexedGlyphRuns` 单 run 路径会回落到段内 0）。
                _glyphRunCharStart = _glyphRuns.Count > 0 ? new List<int> { _paragraphOrigin + lineStart } : new List<int>();
            if (_glyphRunRunSlot == null)
                _glyphRunRunSlot = _glyphRuns.Count > 0 ? new List<int> { -1 } : new List<int>();   // -1 ⇒ 走行级画刷

            HbTextLineScaffold.NoteConstructed();
            HbTextLineScaffold.EnsureDumpInstalled();
            if (hasModifierScope) HbTextLineScaffold.NoteModifierLine();
            // ⭐ 逐行 dump（缺省关）：关时 `NoteLine` 第一句就返回 0 ⇒ 本行只多一次静态调用 + 整数比较。
            _dumpSeq = HbTextLineScaffold.NoteLine(this);
        }

        private readonly double _height;
        private readonly double _baseline;
        private readonly double _textHeight;      // 真机：**恒为自然文本高**，不随 LineHeight 变

        /// <summary>
        /// ⭐ B2 的正式构造路径：一行 = 源文本 + 断行结果 + 逐字 advance（**真机口径的记账**）。
        /// </summary>
        internal static HbTextLine FormatLine(
            string text, HbLineRange range, double[] paragraphAdvances, string fontPath,
            double emSize, GlyphTypeface glyphTypeface, float pixelsPerDip, TextRunProperties runProperties,
            bool keepState, bool hasModifierScope, double lineHeight,
            HbFontPlan plan = null, GlyphTypeface[] segmentFaces = null, double paragraphWidth = 0,
            TextRunProperties[] runProps = null, double indentDip = 0, double defaultIncrementalTab = double.NaN,
            bool wrap = true, int modifierOpenIndex = -1, int modifierScopeEnd = -1, int modifierCloseIndex = -1,
            double paragraphIndentDip = 0,   // D-T2/(C)：尾随可选，默认 0 ⇒ 既有调用点零改动
            // ★`#23` P2（`D-T6-b`）：**段落原点** = 本段落在调用方 `TextSource` 里的起点（= `FormatParagraph`
            //   收进来的那个串在 source 里的 `cpFirst`。尾随可选、缺省 0 ⇒ 既有调用点零改动、逐位等价。
            int paragraphOrigin = 0,
            // T-A30：段末行的末 span 交回**源自己的** EOL run（缺省 null ⇒ 就地造 `TextEndOfParagraph(1)`，逐位不变）。
            TextRun eopRun = null)
        {
            int visibleLen = range.VisibleLength;
            string visible = text.Substring(range.Start, visibleLen);
            // R1/T1d：有计划 ⇒ 按码点段多面整形；没有 ⇒ 与今天逐位相同的单面整形。
            double tabInterval = double.IsNaN(defaultIncrementalTab) ? 4.0 * emSize : defaultIncrementalTab;
            HbShapedRun shaped = (plan != null && plan.Segments.Count > 0)
                ? HbMultiFontShaper.ShapeParagraph(plan.Sub(range.Start, range.Start + visibleLen), visible, emSize,
                                                   null, indentDip, tabInterval,
                                                   HbShaper.TabClampWidthFor(wrap, paragraphWidth, paragraphIndentDip),
                                                   indentDip + paragraphIndentDip)   // 件2b：笔位自段落原点(=0)；内容起点 = I + PI
                : HbShaper.Shape(fontPath, visible, emSize);
            // ★ 单面路径：`Shape` 不知道行内起点（Indent）与段落级 Tab 间隔 ⇒ 在这里按真值重算（幂等）
            if (plan == null || plan.Segments.Count == 0)
                HbShaper.ApplyTabStops(shaped, visible, IntPtr.Zero, indentDip, tabInterval,
                                       HbShaper.TabClampWidthFor(wrap, paragraphWidth, paragraphIndentDip),
                                       indentDip + paragraphIndentDip);   // 件2b：笔位自段落原点(=0)；内容起点 = I + PI

            // #13：**零宽只落 `adv[]`**（= `shaped.AdvancesPx`，它同时喂 `witw`/`w` 与 `GlyphRun.AdvanceWidths`），
            //   在**整形之后、算宽之前**落 0；`len`/`nl`/`ws`/`dep` 全部来自**字符区间**、一字不动
            //   ⇒ `len` 仍含 scope 内的字符（真值 `len=63` ✓）。
            //   ⚠️ **保留字形本身**（不把它们从渲染 run 里摘掉）：`Extent` 是**墨迹**度量，摘掉会动 `Extent`，
            //      而判据要求 `Extent`/`Baseline` **不动**（段2 余差 58）。摘字形归 `D-F1`/`GetIndexedGlyphRuns` 那条线。
            if (modifierOpenIndex >= 0)
            {
                int kl = modifierOpenIndex - range.Start;
                int kh = (modifierScopeEnd < 0 ? visibleLen
                                               : Math.Min(modifierScopeEnd, range.Start + visibleLen) - range.Start);
                for (int g = 0; g < shaped.Glyphs.Length; ++g)
                {
                    uint cl = shaped.Clusters[g];
                    if (cl >= (uint)kl && cl < (uint)kh) shaped.AdvancesPx[g] = 0;
                }
            }

            int trailingWs = TrailingWhitespaceCount(visible);
            double wsAdvance = 0;
            for (int i = visibleLen - trailingWs; i < visibleLen; ++i)
                wsAdvance += (i < paragraphAdvances.Length) ? paragraphAdvances[i] : 0;

            double witw = shaped.TotalWidthPx + indentDip;                     // ③ 行尾空白占区间（+ Indent：真机把 Indent 算进行宽）
            double w = witw - wsAdvance;                                       // ③ 但不占 Width
            int eop = range.HasEop ? 1 : 0;
            int length = visibleLen + range.HardBreakLen + eop;                 // ① 含硬断字符 + EOP
            int newlineLength = range.HardBreakLen + eop;                       // 末行 NewlineLength=1 是 EOP
            int trailingWhitespaceLength = trailingWs + range.HardBreakLen + eop;

            TextRunProperties props;
            var hbProps = runProperties as HbRunProperties;
            if (hbProps != null) props = hbProps;
            else props = new HbRunProperties(runProperties != null ? runProperties.Typeface : null, emSize,
                                             runProperties != null ? runProperties.ForegroundBrush : null);
            var run = new HbTextRun(visible, 0, visibleLen, props);

            var line = new HbTextLine(run, shaped, glyphTypeface, pixelsPerDip, text, range.Start,
                                      fontPath, range.HardBreakLen, range.HasEop,
                                      keepState, hasModifierScope, range.Forced, lineHeight,
                                      length, newlineLength, trailingWhitespaceLength, w, witw, segmentFaces, plan,
                                      paragraphWidth, runProps, indentDip + paragraphIndentDip, paragraphIndentDip,
                                      paragraphIndentDip,   // 末三=startPenX(合量)、末二=boxOriginX(盒原点=PI)、末=`TextLine.Start` 的 PI（`#21` 新增）
                                      // ★`#23` P2：第 26 个实参 = **段落原点**（帧的绝对系 = 原点 + `range.Start`）。
                                      //   这里是本 ctor 的**唯一**行构造点 ⇒ 原点只在此接线（折叠路径另有透传）。
                                      paragraphOrigin,
                                      // T-A30：段末行带源自己的 EOL run（`range.HasEop` 为假时该行不产 EOP span ⇒ 无用）
                                      eopRun);
            if (range.Forced) HbTextLineScaffold.NoteForcedBreakLine();
            if (range.KinsokuPull > 0) HbTextLineScaffold.NoteKinsokuPull(range.KinsokuPull);
            return line;
        }

        private static int TrailingWhitespaceCount(string s)
        {
            int n = 0;
            // 真机口径：`\t` **不算**行尾空白（实测：行以 `\t` 结尾而 TrailingWhitespaceLength=0）。
            while (n < s.Length && HbBreakEngine.IsTrailingWhitespace(s[s.Length - 1 - n])) ++n;
            return n;
        }

        private static double WidthWithoutTrailingWhitespace(HbShapedRun shaped)
        {
            double[] adv = shaped.CharAdvances();
            int ws = TrailingWhitespaceCount(shaped.Text);
            double w = 0;
            for (int i = 0; i < adv.Length - ws; ++i) w += adv[i];
            return w;
        }

        /// <summary>
        /// 造一个 `GlyphRun`（cluster 是**这串字符的局部**下标 ⇒ 正好是 `GlyphRun` 要的形状）。
        /// 单面路径与多面分段路径**走同一个函数** —— 这样"换面"不会顺手改掉契约细节。
        /// </summary>
        private static GlyphRun BuildGlyphRun(HbShapedRun r, GlyphTypeface face, float pixelsPerDip,
                                              double baseline, double originX, string charsText)
        {
            var chars = new List<char>((charsText ?? string.Empty).ToCharArray());
            return new GlyphRun(
                // 第二参 = BidiLevel：**保持 0**（与上游"逻辑序 run"的口径一致）。
                //
                // ⚠️ 自我纠正（T1d，2026-09-13，有实测）：我原先的清单写"RTL 段给 1"，**那是错的**：
                //   · 上游同款路径（`SimpleTextLine` 用的 `GlyphTypeface.ComputeUnshapedGlyphRun`）
                //     传的就是 `0, // bidiLevel`（`GlyphTypeface.cs:1405`）——它同样靠宿主的反镜像产生视觉序；
                //   · 本移植里给 1 会让 `GlyphRun.ComputeInkBoundingBox` 走 **RTL 分支**（`IsLeftToRight=false`），
                //     实测墨迹盒从 `(0.271,-11.206)-(65.654,2.299)` 变成 **`(-64.984,…)-(0.398,…)`**（负 x）；
                //     而本移植渲染侧 `MilGlyphRunAdapter` **不读 BidiLevel**（§2.1(d)）⇒ 照旧按正 advance 从左往右画。
                //   ⇒ "账本说负 x、实际画正 x" = 新的不自洽 ⇒ **保持 0**。
                //   （`r.Rtl` 仍保留：它是"这一段是 RTL"的事实，供将来做 caret/命中与真 bidi 时使用。）
                face, 0, false, r.EmSize, pixelsPerDip,
                new List<ushort>(r.Glyphs),
                new Point(originX, baseline),
                new List<double>(r.AdvancesPx),
                MakeOffsets(r),
                chars,
                null,
                InvertClusters(r, chars.Count),
                MakeCaretStops(chars.Count),
                XmlLanguage.GetLanguage("en-US"));
        }

        private static List<Point> MakeOffsets(HbShapedRun shaped)
        {
            var offsets = new List<Point>(shaped.OffsetsXPx.Length);
            foreach (double dx in shaped.OffsetsXPx) offsets.Add(new Point(dx, 0));
            return offsets;
        }

        /// <summary>
        /// clusters：HarfBuzz 给的是「字形→字符」（单调不减），WPF 要的是「字符→字形」——
        /// 必须**求逆**。契约（`GlyphRun.cs:370-392`）：个数 == characters.Count、[0]==0、
        /// 单调不减、每个值 &lt; GlyphCount。
        /// </summary>
        private static List<ushort> InvertClusters(HbShapedRun shaped, int charCount)
        {
            int glyphCount = shaped.Glyphs.Length;
            var map = new List<ushort>(charCount);
            int g = 0;
            for (int c = 0; c < charCount; c++)
            {
                while (g + 1 < glyphCount && shaped.Clusters[g + 1] <= (uint)c) g++;
                int mapped = g;
                if (mapped >= glyphCount) mapped = glyphCount - 1;
                if (mapped < 0) mapped = 0;
                if (map.Count > 0 && mapped < map[map.Count - 1]) mapped = map[map.Count - 1];
                map.Add((ushort)mapped);
            }
            if (map.Count > 0) map[0] = 0;
            return map;
        }

        /// <summary>caretStops 个数 == characters.Count **+ 1**（`GlyphRun.cs:411`）。</summary>
        private static List<bool> MakeCaretStops(int charCount)
        {
            var stops = new List<bool>(charCount + 1);
            for (int i = 0; i <= charCount; i++) stops.Add(true);
            return stops;
        }

        // ==================================================================
        //  真实现的 override
        // ==================================================================

        public override IList<TextSpan<TextRun>> GetTextRunSpans()
        {
            HbTextLineScaffold.NoteGetTextRunSpans();
            // ⭐ 逐行 dump 的"这一行被问过"补记：**只在 `_dumpSeq != 0`（即 PERLINE 打开且本行被 dump）时才调用**，
            //   关时只多一次 int 比较；M7b 在本方法里的 span 构造逻辑**一个字未动**（见下方原文）。
            if (_dumpSeq != 0) HbTextLineScaffold.NoteSpanQueryOnLine(this, _dumpSeq);

            // ── 契约补齐（2026-09-12，主控批准）：**段末行的末 span 必须是 `TextEndOfParagraph`** ──
            // 上游 `TextBoxLine.EndOfParagraph`（`…/documents/TextBoxLine.cs:362-371`）判据是
            //   `_line.NewlineLength != 0 && ((TextSpan<TextRun>)runs[runs.Count - 1]).Value is TextEndOfParagraph`
            // 而 `TextBoxLine.Length = _line.Length - (EndOfParagraph ? 1 : 0)`（同文件 `:378-380`）
            //   ⇒ **只有认出 EOP，宿主才会把合成 EOP 那 1 个位置从行宽里减掉**。
            // 本实现在 `Length` 里**已经**含 EOP（本文件 `FormatLine()` 的记账
            //   `int length = visibleLen + range.HardBreakLen + eop;`，`NewlineLength = HardBreakLen + eop`），
            // 却只回吐一个 `HbTextRun` span ⇒ `EndOfParagraph=false` ⇒ 宿主拿到的行宽比真实内容**多 1**
            //   ⇒ `TextBoxView.FullMeasureTick` 的 `lineOffset += line.Length` 越过容器末位
            //   ⇒ 下一次 `FormatLine` 落在 `dcp = SymbolCount + 1` ⇒ 上游
            //      `TextContainer.GetNodeAndEdgeAtOffset`（`:1284`）"Bogus symbol offset!" ⇒ FailFast。
            // 实测（波 7 权威 PC `b444c5a5c6a7fe39`，`"seed-文本"` = 7 字符）：
            //   `HBLINE A#0 cpFirst=0 cpLast=8 len=8 nl=1` + 断言栈 `SimpleTextLine.Linux.cs:227`
            //   ⇒ 越界 dcp = 8 = SymbolCount(7) + 1，**溢出量恰为 EOP 那 1 个位置**。
            // **副带修正**：改前 spans 总长 = `_visibleLength`，与 `Length`（含硬断字符 + EOP）不等；
            //   改后 = 可见文本 + EOP = `Length`（硬断字符仍不在任何 span 里，属既有欠账，本次不动）。
            // 构造式与 `TextBoxLine.cs:77`（`new TextEndOfParagraph(1)`）逐字一致；
            //   `TextEndOfParagraph(int)` 是 public ctor（已用编译器确认，不靠记忆）。
            var spans = new List<TextSpan<TextRun>>(2);
            if (_visibleLength > 0)
                spans.Add(new TextSpan<TextRun>(_visibleLength, _run));
            if (_hasEop)
            {
                // ── T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：段末行**交回源自己的那个 EOL run** ──
                //   修前恒造 `TextEndOfParagraph(1)`，而宿主 `Line.FormattingResult`（`Line.cs:928-936`）**只认**
                //   `ParagraphBreakRun`／`LineBreakRun` ⇒ 末行恒报 `fsflrOutOfSpace` ⇒ 驱动把行宽里那 1 个合成位
                //   也累加进 `dcp` ⇒ 下一跳探到**下一个 Block 的 ElementStart** ⇒ `Invariant.FailFast`
                //   （`LineBase.cs:137`，不可捕获）；交回源对象后 `Line.EndOfParagraph` 成立、`FormattingResult`
                //   报 `fsflrEndOfParagraph`（=2）⇒ 驱动在段末收束，**不再越界**。
                //   `Length != 1`（非 `_syntheticCharacterLength` 契约）⇒ 本行的 `Length` 记账对不上 ⇒ **不用**，
                //   退回合成 run（保守，绝不静默改字长）。
                spans.Add(new TextSpan<TextRun>(1,
                    (_eopRun != null && _eopRun.Length == 1) ? _eopRun : new TextEndOfParagraph(1)));
            }
            if (spans.Count == 0)
                spans.Add(new TextSpan<TextRun>(_length, _run));   // 兜底：空行且无 EOP（不应发生）
            return spans;
        }

        public override IList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HbTextLine));
            HbTextLineScaffold.NoteGetTextBounds();

            // ⭐ 真机口径（oracle `indexFrames.GetTextBounds_firstArg`）：第一个参数是**段落系**索引，
            //    不是行内偏移 —— 原型版本按行内偏移算，那是错的（传 0 时只有行起点=0 的行有结果）。
            // ★`#23` P2（`D-T6-b`）：对面的段落系原点 = `_paragraphOrigin + _lineStart`
            //   （修前只减 `_lineStart` ⇒ 段落原点 ≠ 0 时**整行读空**；`#22` 实测宽松档 133 帧红 +
            //    `GetTextBounds(cpFirst,1)` 读空 104 行）。
            int localFirst = firstTextSourceCharacterIndex - (_paragraphOrigin + _lineStart);
            if (localFirst < 0 || localFirst > _visibleLength) return new List<TextBounds>();  // 与本行不相交
            int localLen = Math.Min(textLength, _visibleLength - localFirst);
            if (localLen < 0) localLen = 0;

            double x0 = AdvanceBefore(localFirst);
            double x1 = AdvanceBefore(localFirst + localLen);
            var rect = new Rect(x0, 0, Math.Max(0, x1 - x0), _height);

            var runBounds = new List<TextRunBounds>
            {
                HbInternalsFactory.CreateRunBounds(rect, firstTextSourceCharacterIndex,
                                                   firstTextSourceCharacterIndex + localLen, _run),
            };
            return new List<TextBounds>
            {
                HbInternalsFactory.CreateTextBounds(rect, FlowDirection.LeftToRight, runBounds),
            };
        }

        public override void Draw(DrawingContext drawingContext, Point origin, InvertAxes inversion)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HbTextLine));
            if (drawingContext == null) throw new ArgumentNullException(nameof(drawingContext));
            // ⭐ 2026-09-11（P0：RTL 崩溃）：`InvertAxes != None` **不再抛** —— 按上游契约
            //   `SimpleTextLine.Draw`（`SimpleTextLine.cs:482-501`）同款处理：
            //   用 `TextFormatterImp.CreateAntiInversionTransform`（`TextFormatterImp.cs:565-595`）
            //   造一个矩阵，push 到 DrawingContext 上再照常画。
            //   镜像公式（上游原文）：Horizontal ⇒ m11 = −1, offsetX = paragraphWidth；Vertical ⇒ m22 = −1, offsetY = lineHeight。
            //   【为什么会走到这里】`MS/Internal/Text/Line.cs:79` 只要段落方向是 RTL 就置 `_mirror`，
            //   然后 `:116` `line.Draw(ctx, origin, _mirror ? InvertAxes.Horizontal : InvertAxes.None)`
            //   ⇒ **任何 RTL 段落**都会带 Horizontal（与文本内容无关）。
            MatrixTransform anti = null;
            if (inversion != InvertAxes.None)
            {
                bool needX = (inversion & InvertAxes.Horizontal) != 0;
                if (needX && !(_paragraphWidth > 0))
                {
                    // **诚实降级**（不静默）：镜像基准（段落宽）未知 ⇒ 按 LTR 画，但**计数 + 前 3 次无条件打诊断**。
                    HbTextLineScaffold.NoteInvertedNoWidth(inversion);
                }
                else
                {
                    anti = BuildAntiInversion(inversion, _paragraphWidth, _height);
                    if (anti != null) HbTextLineScaffold.NoteInvertedLine(inversion);
                    // ⭐ 修法②（T1d §13，2026-09-13，主控授权）：**我们输出逻辑序 RTL ⇒ 不再抵消宿主的镜像**。
                    //   【镜像计数】宿主对本行**已经**做了一次元素镜像（`FrameworkElement.GetFlowDirectionTransform()`
                    //   ＝ `MatrixTransform(-1,0,0,1,RenderSize.Width,0)`，`FrameworkElement.cs:3940-3948`；桥侧实测
                    //   `[VISTRANS] M11=-1 DX=65.2559 镜像=是`）。屏上顺序 = reverse^(推入次数 + 1)(数组序)：
                    //     · 修法① **之前**：数组 = HB 视觉序 ⇒ 需要 2 次（推反演 + 宿主）⇒ 顺序还原成视觉序 ✓
                    //     · 修法① **之后**：数组 = 逻辑序 ⇒ 只需宿主那 1 次 ⇒ 再推反演 = 2 次 ⇒ **翻成逻辑序（反了）**，
                    //       且把 run 挪出框外（桥侧：`CTM` 的 m11 被抵消成 +1.0417、dx=−24.594）。
                    //   【为什么只摘 Horizontal】LTR 内容（或混合段）在 RTL 段落里仍**需要**这次反演去抵消元素镜像
                    //   （否则拉丁字母会被镜像成反字）；垂直轴与上游语义无关，照旧。
                    //   `_shaped.Rtl` 的取值恰好就是这条判据：单段 RTL ⇒ true（我们输出逻辑序 ⇒ 摘掉 X 分量）；
                    //   LTR 内容 ⇒ false；多段混合 ⇒ 合并处给 false（保守：维持改前行为，混合方向本就不在射程 §8.7）。
                    if (anti != null && needX && _shaped != null && _shaped.Rtl)
                    {
                        Matrix m = anti.Matrix;
                        if (m.M22 == 1 && m.OffsetY == 0) anti = null;                    // 只剩水平分量 ⇒ 干脆不推
                        else anti = new MatrixTransform(1, 0, 0, m.M22, 0, m.OffsetY);    // 保留垂直分量
                    }
                }
                if (anti != null) drawingContext.PushTransform(anti);
            }
            try
            {
                DrawCore(drawingContext, origin, inversion, anti);
            }
            finally
            {
                if (anti != null) drawingContext.Pop();
            }
        }

        /// <summary>
        /// `InvertAxes` → 反镜像矩阵：与上游 `TextFormatterImp.CreateAntiInversionTransform`
        /// （`TextFormatterImp.cs:565-595`）**逐字段同式** —— Horizontal ⇒ `m11=−1, offsetX=paragraphWidth`；
        /// Vertical ⇒ `m22=−1, offsetY=lineHeight`；Both ⇒ 两者。
        ///
        /// 【为什么自己构造而不是直接调上游那个函数】它是 **PC 的 internal** ⇒ 反射形态
        ///   （`HbTextLineParity`/`TextLineProto` 把本文件编进自己）看不见、编不过。
        ///   所以用公开的 `Matrix`/`MatrixTransform` 构造；**DIRECT 形态下每次都与上游函数
        ///   逐字段比对**，不一致就计数（`antiMatrixMismatch`）⇒ "同式"是**被验证**的，不是声称的。
        /// </summary>
        private static MatrixTransform BuildAntiInversion(InvertAxes inversion, double paragraphWidth, double lineHeight)
        {
            if (inversion == InvertAxes.None) return null;

            double m11 = 1, m22 = 1, offsetX = 0, offsetY = 0;
            if ((inversion & InvertAxes.Horizontal) != 0) { m11 = -m11; offsetX = paragraphWidth; }
            if ((inversion & InvertAxes.Vertical) != 0) { m22 = -m22; offsetY = lineHeight; }

#if TEXTLINE_SHIM_DIRECT
            try
            {
                MatrixTransform upstream =
                    MS.Internal.TextFormatting.TextFormatterImp.CreateAntiInversionTransform(
                        inversion, paragraphWidth, lineHeight);
                if (upstream != null)
                {
                    Matrix u = upstream.Matrix;
                    if (u.M11 != m11 || u.M22 != m22 || u.OffsetX != offsetX || u.OffsetY != offsetY)
                        HbTextLineScaffold.NoteAntiMatrixMismatch();
                }
            }
            catch (Exception) { /* 比对是诊断，不影响绘制 */ }
#endif
            return new MatrixTransform(m11, 0, 0, m22, offsetX, offsetY);
        }

        /// <summary>
        /// 第 <paramref name="index"/> 张 `GlyphRun` 该用哪把 foreground：
        /// **有 per-run 画刷就跟着源 run 走**（上游 `SimpleTextLine.cs:1740` 的 per-run 语义），
        /// 否则回落行级 `_run.Props.ForegroundBrush`（= 未传 `runProps` 时的今天行为，逐位不变）。
        /// ⚠️ 面可以被回退换掉，**前景色不跟着面走** —— 只跟着源 run 走。
        /// </summary>
        private Brush BrushForRunIndex(int index)
        {
            Brush fallback = _run.Props != null ? _run.Props.ForegroundBrush : null;
            if (_runBrushes == null || _glyphRunRunSlot == null) return fallback;
            if (index < 0 || index >= _glyphRunRunSlot.Count) return fallback;
            int slot = _glyphRunRunSlot[index];
            if (slot < 0 || slot >= _runBrushes.Length) return fallback;
            return _runBrushes[slot];
        }

        /// <summary>真正的绘制（`Draw` 只负责反镜像变换的 push/pop，与上游同构）。</summary>
        private void DrawCore(DrawingContext drawingContext, Point origin, InvertAxes inversion, MatrixTransform anti)
        {
            HbTextLineScaffold.NoteDraw();
            if (_glyphRuns.Count == 0)                     // 空行（无字形）没什么可画
            {
                HbLineTrace.SiteB(_lineStart, this, _glyphRuns, origin, inversion);
                HbLineTrace.SiteE(_lineStart, this, _glyphRuns, origin, anti, inversion);   // 站点 E（缺省关）
                return;
            }
            // ⭐ 2026-09-11（主控批准的行为改动）：**把宿主给的行原点带进绘制**。
            //   依据 = 上游 `SimpleTextLine.cs:572-605`：`double y = origin.Y + Baseline;`
            //   `run.Draw(dc, IdealToReal(x, ppd) + origin.X, y, false);`
            //   改前我们只画在行内坐标 `(pen, baseline)` ⇒ 宿主逐行递增的 origin 被丢掉
            //   ⇒ 折行段落每行画在同一 y（多行摞印）。**只动"画到哪里"**：
            //   宽度/高度/断行/记账/R1 选面与切段**一行未改**。
            List<GlyphRun> drawn = PositionedRuns(origin);
            HbLineTrace.SiteB(_lineStart, this, drawn, origin, inversion);   // 只读插桩（缺省关）
            HbLineTrace.SiteE(_lineStart, this, drawn, origin, anti, inversion);   // 站点 E：绘制几何（缺省关）
            HbLineTrace.SiteD(_lineStart, this, drawn,
                              _run.Props != null ? _run.Props.ForegroundBrush : null);   // 站点 D（缺省关）
            // R1/T1d：**逐段画**（每段带自己的面）。单面时循环体只有一次 ⇒ 与今天等价。
            for (int i = 0; i < drawn.Count; ++i)
                drawingContext.DrawGlyphRun(BrushForRunIndex(i), drawn[i]);
        }

        /// <summary>
        /// 把宿主给的**行原点**算进 `GlyphRun` 的基线原点 —— 与上游 `SimpleTextLine.DrawTextLine`
        /// 的 `run.Draw(dc, x + origin.X, origin.Y + Baseline, false)` **逐字同构**。
        ///
        /// 【为什么不用 `PushTransform`】本工程两条路都能生效（`MilPushTransform` 有实现、
        ///   `GlyphRunPainter` 走 `canvas.DrawText` ⇒ 吃画布矩阵），但"把原点烘进 run"是上游
        ///   快路径的原形态，而且**判据能直接量在我们真正画出去的那份数据上**（不必解释坐标系）。
        /// 【面不变】**只挪位置**：`GlyphTypeface`/glyph id/advance/offset/cluster/caretStop 全是原样。
        /// 【缓存】同一 `origin` 复用同一批对象（每帧同一 origin ⇒ 零分配）；`(0,0)` 时直接返回
        ///   原来那批（首位行与改前**同一批对象**，行为逐位不变）。
        /// </summary>
        private List<GlyphRun> PositionedRuns(Point origin)
        {
            if (origin.X == 0 && origin.Y == 0) return _glyphRuns;
            if (_positionedRuns != null && _positionedAt.X == origin.X && _positionedAt.Y == origin.Y)
                return _positionedRuns;

            var list = new List<GlyphRun>(_glyphRuns.Count);
            foreach (GlyphRun gr in _glyphRuns) list.Add(ShiftGlyphRun(gr, origin));
            _positionedRuns = list;
            _positionedAt = origin;
            return list;
        }

        private static GlyphRun ShiftGlyphRun(GlyphRun src, Point origin)
        {
            Point bo = src.BaselineOrigin;
            var glyphs = new List<ushort>(src.GlyphIndices.Count);
            for (int i = 0; i < src.GlyphIndices.Count; ++i) glyphs.Add(src.GlyphIndices[i]);
            var adv = new List<double>(src.AdvanceWidths.Count);
            for (int i = 0; i < src.AdvanceWidths.Count; ++i) adv.Add(src.AdvanceWidths[i]);
            var off = new List<Point>(src.GlyphOffsets.Count);
            for (int i = 0; i < src.GlyphOffsets.Count; ++i) off.Add(src.GlyphOffsets[i]);
            var chars = new List<char>(src.Characters.Count);
            for (int i = 0; i < src.Characters.Count; ++i) chars.Add(src.Characters[i]);
            var clusters = new List<ushort>(src.ClusterMap.Count);
            for (int i = 0; i < src.ClusterMap.Count; ++i) clusters.Add(src.ClusterMap[i]);
            var stops = new List<bool>(src.CaretStops.Count);
            for (int i = 0; i < src.CaretStops.Count; ++i) stops.Add(src.CaretStops[i]);

            return new GlyphRun(
                src.GlyphTypeface, src.BidiLevel, src.IsSideways, src.FontRenderingEmSize, (float)src.PixelsPerDip,
                glyphs, new Point(bo.X + origin.X, bo.Y + origin.Y), adv, off, chars,
                src.DeviceFontName, clusters, stops, src.Language);
        }

        /// <summary>原型/测试专用：拿内部 `GlyphRun` 做光栅化取证（空行返回 null）。</summary>
        internal GlyphRun GlyphRunForRasterization => _glyphRun;

        /// <summary>R1/T1d：本行交出去的**全部** `GlyphRun`（每个字体子段一个）。</summary>
        internal IList<GlyphRun> GlyphRunsForRasterization => _glyphRuns;

        /// <summary>
        /// R1/T1d 的**逐 run 可观测**读数：`(glyphCount, face 文件, 面下标, 令牌, baseline origin, 字符)`。
        ///
        /// ⚠️ 读 `token` 会触发 `GlyphTypeface.GetDWriteFontAddRef` —— 那是**幂等登记**
        ///   （同 `(path, faceIndex, simFlags)` 恒得同一句柄，契约见 `MilHandleTables.cs:649`），
        ///   但它终究是"有副作用的观测" ⇒ 只在显式开诊断时才取（缺省 `-`）。
        /// </summary>
        internal string FaceDiag(bool withToken)
        {
            var sb = new StringBuilder();
            sb.Append("glyphRuns=").Append(_glyphRuns.Count);
            for (int i = 0; i < _glyphRuns.Count; ++i)
            {
                GlyphRun gr = _glyphRuns[i];
                GlyphTypeface gt = gr.GlyphTypeface;
                int cps = i < _glyphRunCharStart.Count ? _glyphRunCharStart[i] : -1;
                sb.Append(" [r").Append(i)
                  .Append(" cp=").Append(cps).Append("..").Append(cps + gr.GlyphIndices.Count)
                  .Append(" glyphs=").Append(gr.GlyphIndices.Count)
                  .Append(" face=").Append(gt != null ? gt.FontUri.LocalPath : "<null>")
                  .Append(" faceIndex=").Append(HbFaceInternals.Available && gt != null ? HbFaceInternals.FaceIndexOf(gt).ToString() : "<不可用>")
                  .Append(" token=");
                if (withToken && gt != null) sb.Append(HbFaceInternals.TokenOf(gt));
                else sb.Append(withToken ? "-" : "(未取)");
                sb.Append(" origin=").Append(gr.BaselineOrigin.X.ToString("F3"))
                  .Append(" chars=\"");
                if (gr.Characters != null)
                    for (int ci = 0; ci < gr.Characters.Count; ++ci) sb.Append(gr.Characters[ci]);
                sb.Append("\"]");
            }
            return sb.ToString();
        }

        /// <summary>诊断专用（只读）：本行起始的**段落系**字符下标。</summary>
        internal int LineStartForDiag => _lineStart;

        /// <summary>
        /// 诊断专用（只读）：本行拿到的**段落宽**（= `FormatLine`/`FormatParagraph` 的 `paragraphWidth` 实参）。
        /// 【为什么要它】它同时是**换行基准**与**反镜像矩阵的 offsetX**（`Draw` → `BuildAntiInversion`）——
        /// T1d §9 的判别表要靠它分辨"RTL 行被搬到块外"是**宿主绘制原点**问题还是**镜像基准**问题。
        /// 纯只读：不改语义、不碰计数器、不参与任何计算路径。
        /// </summary>
        internal double ParagraphWidthForDiag => _paragraphWidth;

        /// <summary>测试专用：本行的可见文本（不参与契约）。</summary>
        internal string LineTextForDiag => _shaped != null ? _shaped.Text : string.Empty;

        /// <summary>测试专用：本行的诊断信息（不参与契约）。</summary>
        internal string Diagnostics =>
            $"start={_lineStart} visible={_visibleLength} hardBreak={_hardBreakLen} eop={(_hasEop ? 1 : 0)} " +
            $"forced={(_forcedBreak ? 1 : 0)} keepState={(_keepState ? 1 : 0)} modifier={(_hasModifierScope ? 1 : 0)}";

        /// <summary>本行的逐行 dump 序号（0 = 未被 dump）。**只在 `WPF_LINUX_TEXTLINE_PERLINE=1` 时非 0**。</summary>
        private int _dumpSeq;

        /// <summary>
        /// 逐行机读 tuple（**只有开关打开时才会被调用** —— 见 `HbTextLineScaffold.NoteLine` 的惰性纪律）。
        /// 字段 = 现有 `Diagnostics` 的全部 + `cpFirst/cpLast/len/nl/runs/spans/text`；
        /// `spans=0` 表示"**写下这条记录时**尚未被问过 `GetTextRunSpans()`"，事后被问会另有一条 `HBLINE_LINEQ#<seq>`。
        /// </summary>
        internal string LineDumpTuple(int seq)
        {
            var sb = new StringBuilder(200);
            sb.Append("HBLINE_LINE#").Append(seq)
              .Append(" start=").Append(_lineStart)
              .Append(" cpFirst=").Append(_lineStart)
              .Append(" cpLast=").Append(_lineStart + Length)
              .Append(" len=").Append(Length)
              .Append(" nl=").Append(NewlineLength)
              .Append(" visible=").Append(_visibleLength)
              .Append(" hardBreak=").Append(_hardBreakLen)
              .Append(" eop=").Append(_hasEop ? 1 : 0)
              .Append(" forced=").Append(_forcedBreak ? 1 : 0)
              .Append(" keepState=").Append(_keepState ? 1 : 0)
              .Append(" modifier=").Append(_hasModifierScope ? 1 : 0)
              .Append(" runs=").Append(_glyphRuns.Count)
              .Append(" spans=0")
              .Append(" text=\"")
              .Append(EscapeForDump(_shaped != null ? _shaped.Text : string.Empty, 16))
              .Append('"');
            return sb.ToString();
        }

        /// <summary>转义 + 截断（有界；只在 dump 打开时被调用）。</summary>
        private static string EscapeForDump(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(Math.Min(s.Length, max) + 8);
            for (int i = 0; i < s.Length && i < max; ++i)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            if (s.Length > max) sb.Append('…');
            return sb.ToString();
        }

        private double AdvanceBefore(int charIndex)
        {
            double x = _startPenX;                  // ★ Indent：内容整体右移（Tab 网格不随之移动）
            for (int i = 0; i < charIndex && i < _charAdvances.Length; i++) x += _charAdvances[i];
            return x;
        }

        // ==================================================================
        //  19 个 abstract 属性
        // ==================================================================

        public override double Baseline => _baseline;
        public override double TextBaseline => _baseline;
        public override double Height => _height;
        public override double TextHeight => _textHeight;   // 真机：恒为自然文本高（≠ Height 当 LineHeight>0）
        /// <summary>
        /// ⭐ 真机语义 = **本行墨迹的黑色高度**（ink box height），**不是行高**。
        ///
        /// 【依据（上游原文，逐条）】
        ///   · `SimpleTextLine.cs:1796-1802`：`inkBoundingBox = glyphRun.ComputeInkBoundingBox();`
        ///     再 `inkBoundingBox.X/Y += glyphRun.BaselineOrigin.X/Y`（= 把每张 run 的墨迹盒
        ///     挪到行坐标系里），最后并集；
        ///   · `SimpleTextLine.cs:1071-1083`：`public override double Extent { get { CheckBoundingBox();
        ///     return _boundingBox.Bottom - _boundingBox.Top; } }`，注释原文
        ///     “Client to get the **height of the actual black** of the line”；
        ///   · `FullTextLine.cs:2333-2338 / 2640`：`Extent = _overhang.Extent = boundingBox.Bottom - boundingBox.Top`。
        ///   ⇒ 所以这里用**同一条公式**（同一个 API `ComputeInkBoundingBox`），不是另挑一个近似口径。
        ///
        /// 【为什么逐张 run 各用**它自己那份面**】R1/T1d 之后一行可能由多个字体子段拼成
        ///   （CJK 走回退面）⇒ 每张 `GlyphRun` 的 `GlyphTypeface` 就是它自己那份面，
        ///   墨迹因此与**渲染面**天然一致（不会出现"用 A 面算墨迹、用 B 面画字"）。
        ///
        /// 【空行 / 无墨迹】⇒ 0（上游 `boundingBox.IsEmpty` 分支：`_overhang.Extent = 0`；
        ///   `Rect.Empty` 的 Top=Bottom=0，差也是 0）。
        /// 【缓存】上游也缓存（`CheckBoundingBox()`）；本行构造后不变 ⇒ 首次取值算一次。
        /// **只影响本属性**：`Height`/`TextHeight`/`Baseline`/断行/记账/计数器一行未动。
        /// </summary>
        public override double Extent
        {
            get
            {
                if (double.IsNaN(_extentCache)) _extentCache = ComputeInkExtent();
                return _extentCache;
            }
        }

        /// <summary>本行墨迹盒高度（DIP）：逐张 run 取 `ComputeInkBoundingBox()`、按各自基线原点平移后并集。</summary>
        private double ComputeInkExtent()
        {
            Rect box = Rect.Empty;
            foreach (GlyphRun gr in _glyphRuns)
            {
                Rect b = gr.ComputeInkBoundingBox();
                if (b.IsEmpty) continue;
                b.Offset(gr.BaselineOrigin.X, gr.BaselineOrigin.Y);
                box.Union(b);
            }
            return box.IsEmpty ? 0.0 : box.Bottom - box.Top;
        }

        public override double MarkerHeight => _height;
        public override double MarkerBaseline => _baseline;
        public override double Width => _width;
        public override double WidthIncludingTrailingWhitespace => _widthIncludingTrailingWhitespace;
        /// <summary>
        /// `#21`（`D-T6-c`）**改正的真法律**：`Start == TextParagraphProperties.ParagraphIndent`（DIP，
        /// `TextAlignment=Left`）。
        ///
        /// 【被推翻的旧注释（原文照录，供审计）】本行原写
        ///   `public override double Start => 0;   // 真机实测恒为 0（3222/3222），不是段落内偏移`
        /// —— 那句**是错的**（把**语料性质**当成了**实现性质**，与纪律 34 同族）：
        ///   · 那个数出自 `layout-b34` 语料（614 例 / 3222 行），而该语料四个文件
        ///     （`src/Linux/tests/parity/windows/layout-b34/{cases,cases-cd1,cases-cd2,windows-results}.json`）
        ///     里 `'ParagraphIndent'` 与 `'Indent'` 的出现次数**各为 0** ⇒ 真法律的**唯一非零驱动量
        ///     `ParagraphIndent` 在那份语料上压根没被采样**（取值恒 0）⇒ 真机在那 3222 行上**必然**全给 0。
        ///     ⇒ **"3222/3222 恒为 0"是 `layout-b34` 的语料性质，不是实现规律。**
        ///   · 真法律的三条独立腿见 `docs/WAVE21-PREREGISTRATION.md` §1.1：
        ///     ① 上游公式 `TextMetrics.cs:355-358` + `:255-263`（`_textStart` 精确相消）；
        ///     ② 真机逐行实测 `src/Linux/tests/parity/windows/tab-anchor/out/tab-anchor-raw.json` 的
        ///        `lineStartOffsetsDip`（= `tab-anchor/src/Program.cs:445` 的 `R(line.Start)`）
        ///        在 436 例 / **615 行**上 **615/615** 精确等于 `paragraphIndentDip`
        ///        （`Start==Indent` 只命中 337/615；`PI=0 而 Start≠0` 与 `PI≠0 而 Start==0` **各 0 行**；
        ///         取值域 `{0:444, 24:131, 48:40}` 与语料 `PI∈{0,24,48}` **双射**）；
        ///     ③ 上游忠实移植版 `SimpleTextLine.Linux.cs:1158-1161` 不硬编码 0。
        /// 【本波量到的】`PcLineOracle`（`#21` §1.4 新增的 `Start` 列）在 `script==latin` 的
        ///   288 例 / 421 行上判定；修前 = 我方恒 0 vs 真值 24/48 ⇒ 逐行全红，修后 421/421 绿。
        /// </summary>
        public override double Start => _paragraphIndentDip;
        public override double OverhangAfter => 0;
        public override double OverhangLeading => 0;
        public override double OverhangTrailing => 0;
        public override int Length => _length;
        public override int DependentLength
        {
            get
            {
                // ⚠️ 未实现（登记在报告"未覆盖清单"）：真机 dump 里这一项有 0..3 的真值，
                //    它是 LS 的"下一行依赖长度"，本引擎没有该概念。恒 0 但**可计数**。
                HbTextLineScaffold.NoteDependentLengthQuery();
                return 0;
            }
        }
        public override int NewlineLength => _newlineLength;
        public override int TrailingWhitespaceLength => _trailingWhitespaceLength;
        public override bool HasCollapsed => _hasCollapsed;

        /// <summary>
        /// ⚠️ **本注释曾在 `#16` 的 `D-O1` 落地后变成与实现相反的陈述**（`#19`/W19A 只改文字，未动语义）。
        ///
        /// 真机实测：**3222/3222 行全为 false** —— 那是**真机语料**的分布，**不是**本实现的取值域。
        /// `D-O1` 已把这里改成**三分支真实现**（见 getter 体内的逐行注释）：
        ///   ① 未给段落宽（`_paragraphWidth == 0`，旧调用点）⇒ `false`（老路径逐位不变）；
        ///   ② `_startPenX >= _paragraphWidth` ⇒ `true`（退化族 `Indent + ParagraphIndent ≥ container`）；
        ///   ③ `_boxOriginX + _width > _paragraphWidth + 1e-9` ⇒ `true`（**严格 `>`**，恰好到达边缘 ⇒ `false`）。
        /// ⇒ 本实现**不再恒 false**：旧注释的"本实现恒 false"是**过时**的，若照它读会把③分支的溢出行读成"不可能"。
        ///
        /// "这一行是不是被强制断出来的"这条信息不丢：见 `forcedBreakLines` 计数 + `Diagnostics`。
        /// </summary>
        public override bool HasOverflowed
        {
            get
            {
                // `D-O1`（#16 末件）：真机口径 = **内容右缘超出「行盒远缘」**；**恰好到达边缘（相等）不算溢出**（Q11 反例批）。
                //   退化族 `Indent + ParagraphIndent ≥ container` ⇒ 内容起点本身已在/越过远缘 ⇒ `true`。
                //   未给段落宽（`_paragraphWidth == 0`，旧调用点）⇒ 保持旧行为「恒 false」⇒ 老路径逐位不变。
                if (!(_paragraphWidth > 0)) return false;
                if (_startPenX >= _paragraphWidth) return true;
                return _boxOriginX + _width > _paragraphWidth + 1e-9;   // **严格 >**（相等 ⇒ false）
            }
        }

        public override void Dispose()
        {
            _disposed = true;
            // 本类不持有非托管资源：`HbShapedRun` 里全是托管数组，`GlyphRun` 是托管对象，
            // **没有** LS 句柄要释放 —— 也正因为如此，`GetTextLineBreak` 只发**零记录**
            // （上游 `TextLineBreak.DisposeInternal` 对 `IntPtr.Zero` 不会调 `LoDisposeBreakRecord`）。
        }

        // ==================================================================
        //  ⭐ B2-1：GetTextLineBreak（真机口径：普通文本 ⇒ null）
        // ==================================================================

        /// <summary>
        /// 真机（`TextMetrics.cs:299-308` + oracle 实测 3220 null / 2 非 null）：
        /// **只有"末 run 带 `TextModifierScope` 且不是 `TextEndOfParagraph`"时才 new 一个 `TextLineBreak`**；
        /// 普通文本一律 **null**。
        ///
        /// 我们**不伪造原生断行记录**（`_breakRecord` 恒 `IntPtr.Zero`）——伪造一个假句柄
        /// 就是典型的"不崩但也不对"。零记录一交出去就**记数 + 打诊断**（LS 边界绊线）。
        /// </summary>
        public override TextLineBreak GetTextLineBreak()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HbTextLine));

            if (!_hasModifierScope)
            {
                HbTextLineScaffold.NoteBreakNull();          // 真机主路径（3220/3222）
                return null;
            }

            HbTextLineScaffold.NoteZeroBreakRecordIssued(
                $"HbTextLine.GetTextLineBreak lineStart={_lineStart} length={_length}");
            return HbInternalsFactory.CreateLineBreak();
        }

        // ==================================================================
        //  ⭐ B2-2 / B2-3：Collapse / GetTextCollapsedRanges（真机口径的字符省略号折叠）
        // ==================================================================
        //
        //  依据（`FullTextLine.cs:693-800` + oracle 实测 430 折叠 / 17 不折叠）：
        //    ① `if (!HasOverflowed && (statusFlags & KeepState) == 0) return this;`   ← 先判资格
        //    ② 空参 ⇒ **ArgumentNullException**（oracle：447 行抛 = 恰好是 alwaysCollapsible 的 447 行）
        //    ③ `if (constraintWidth > Width) return this;`                            ← 零宽行落这里（17 行）
        //    ④ `constraintWidth -= symbol.Width`；以"字符级强制断"贪心取可见前缀（**至少 1 字**）
        //    ⑤ 折后 `Length` = **原 Length**；`Width` = 前缀宽 + 符号宽
        //    ⑥ `collapsedRange = { 行起点 + 可见长, 原Length − 可见长, 原Width − 折后Width }`
        //    ⑦ 未折叠 ⇒ `GetTextCollapsedRanges()` 返回 **null**（不是空集合）
        // ==================================================================

        public override TextLine Collapse(params TextCollapsingProperties[] collapsingPropertiesList)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HbTextLine));
            HbTextLineScaffold.NoteCollapseCall();

            if (!HasOverflowed && !_keepState)
            {
                HbTextLineScaffold.NoteCollapseIneligible();
                return this;                                  // ① 真机：不满足资格的行原样返回（不抛）
            }
            if (collapsingPropertiesList == null || collapsingPropertiesList.Length == 0)
            {
                HbTextLineScaffold.NoteCollapseEmptyArgsThrow();
                throw new ArgumentNullException(nameof(collapsingPropertiesList));   // ②
            }

            TextCollapsingProperties collapsingProp = collapsingPropertiesList[0];
            if (collapsingProp == null) { HbTextLineScaffold.NoteCollapseUnsupported(); return this; }

            double constraintWidth = collapsingProp.Width;
            if (constraintWidth > Width)
            {
                HbTextLineScaffold.NoteCollapseTooWide();
                return this;                                  // ③
            }

            // 本轮只实现真机 oracle 覆盖到的那一种（`TextTrailingCharacterEllipsis`，符号恒 U+2026）。
            // `TextTrailingWordEllipsis` 没有真机真值 ⇒ **不猜**，记数后原样返回（登记为缺口）。
            if (collapsingProp.Style != TextCollapsingStyle.TrailingCharacter)
            {
                HbTextLineScaffold.NoteCollapseUnsupported();
                HbTextLineScaffold.Diag($"Collapse: 折叠样式 {collapsingProp.Style} 本轮无真机真值"
                                        + "（只实现 TrailingCharacter）⇒ 原样返回");
                return this;
            }

            // 上游两个 TextTrailing*Ellipsis 的符号**恒为** U+2026（`TextTrailing*Ellipsis.cs` 里的常量）。
            // 我们按同一常量取符号宽（行内字体/字号）；若调用方给了别的符号 ⇒ 记数回退，不猜。
            if (collapsingProp.Symbol != null && collapsingProp.Symbol.Length != Ellipsis.Length)
            {
                HbTextLineScaffold.NoteCollapseUnsupported();
                HbTextLineScaffold.Diag($"Collapse: 折叠符号不是 1 字的 U+2026（Length={collapsingProp.Symbol.Length}）⇒ 原样返回");
                return this;
            }

            double symbolWidth = HbShaper.Shape(_fontPath, Ellipsis, _shaped.EmSize).TotalWidthPx;
            double inner = constraintWidth - symbolWidth;

            // ④ 可见前缀：按 **HarfBuzz 簇**（cluster）贪心 —— **绝不在簇内切**。
            //    实测依据（真机 oracle）：`F_hard_lf_w40` 行 'first '（cw=14.84）真机可见前缀 = 'fi'（2 字），
            //    而 'fi' 在 Noto Sans 里是**一个连字簇**（advance 全记在首字符上，第二字符 0）
            //    ⇒ 按"逐字符"贪心只会拿到 'f' 1 字（本实现第一版就是这样，36/236 行明细不符）。
            //    簇起点 = HarfBuzz 的 `Clusters[]` 去重升序；簇宽 = 该簇所有字形 advance 之和。
            int visibleLen = 0;
            if (inner > 0 && _charAdvances.Length > 0 && _shaped != null && _shaped.Glyphs.Length > 0)
            {
                var cells = new List<int>();
                for (int i = 0; i < _shaped.Clusters.Length; ++i)
                {
                    int c = (int)_shaped.Clusters[i];
                    if (c >= 0 && c < _visibleLength && (cells.Count == 0 || cells[cells.Count - 1] != c)) cells.Add(c);
                }
                if (cells.Count == 0 || cells[0] != 0) cells.Insert(0, 0);
                if (cells[cells.Count - 1] != _visibleLength) cells.Add(_visibleLength);

                double w = 0; int k = 0;
                for (int ci = 0; ci + 1 < cells.Count; ++ci)
                {
                    double cellW = 0;
                    for (int x = cells[ci]; x < cells[ci + 1]; ++x) cellW += _charAdvances[x];
                    if (w + cellW <= inner + 1e-9) { w += cellW; k = cells[ci + 1]; } else break;
                }
                visibleLen = k > 0 ? k : (cells.Count > 1 ? cells[1] : 1);   // **至少 1 簇**
            }

            double prefixWidth = 0;
            for (int i = 0; i < visibleLen; ++i) prefixWidth += _charAdvances[i];
            // 前缀的**行尾空白不计入折后宽度**（真机实测公式，逐例算过）：
            //   `F_lat_words_w120` 行 'lazy dog and '（行起点 35、可见 5 字 = 'lazy '）：
            //   真机折后 W = 41.4400 = w('lazy') + w('…') = 28.7833 + 12.6567（**丢掉了那个空格 4.16**），
            //   而 cr 仍然把该空格算进可见区间（cp=40 ⇒ 可见 [35,40)）⇒ 两件事分开：
            //   可见长度按簇给，宽度按"前缀去掉行尾空白 + 符号"给。
            int prefixTrailingWs = 0;
            while (prefixTrailingWs < visibleLen &&
                   char.IsWhiteSpace(_text[_lineStart + visibleLen - 1 - prefixTrailingWs])) ++prefixTrailingWs;
            double prefixWsWidth = 0;
            for (int i = visibleLen - prefixTrailingWs; i < visibleLen; ++i) prefixWsWidth += _charAdvances[i];
            double collapsedWidth = prefixWidth - prefixWsWidth + symbolWidth;

            HbTextLine collapsed = BuildCollapsedLine(visibleLen, collapsedWidth);
            if (visibleLen < _visibleLength)
            {
                collapsed._collapsedRange = HbInternalsFactory.CreateCollapsedRange(
                    _paragraphOrigin + _lineStart + visibleLen,     // 段落系索引（★`#23` P2：加段落原点）
                    _length - visibleLen,                          // 原 Length − 可见长
                    _width - collapsedWidth);                      // 原 Width − 折后 Width
            }
            collapsed._hasCollapsed = true;
            HbTextLineScaffold.NoteCollapseApplied();
            return collapsed;
        }

        public override IList<TextCollapsedRange> GetTextCollapsedRanges()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HbTextLine));
            if (_collapsedRange == null)
            {
                HbTextLineScaffold.NoteCollapsedRangesNull();
                return null;                                   // ⑦ 真机：**null**，不是空集合（3222/3222）
            }
            HbTextLineScaffold.NoteCollapsedRangesReturned();
            return new TextCollapsedRange[] { _collapsedRange };
        }

        /// <summary>折后行：文本 = 可见前缀 + 省略号；`Length` 保持**原行** Length（上游 `_cchLength = Length`）。</summary>
        private HbTextLine BuildCollapsedLine(int visibleLen, double collapsedWidth)
        {
            string prefix = _text.Substring(_lineStart, visibleLen);
            string collapsedText = prefix + Ellipsis;
            // R1/T1d：折后行按**同一套面计划**重整形（前缀里的 CJK 也要出真字）；
            //   省略号那一段沿用"本行行首那一段的面" ⇒ 整形面与渲染面（`_segmentFaces[slot]`）一致。
            HbFontPlan cp = null;
            if (_plan != null && _plan.Segments.Count > 0)
            {
                cp = _plan.Sub(_lineStart, _lineStart + visibleLen);
                HbFontSegment lead = null;
                foreach (HbFontSegment s in _plan.Segments)
                    if (s.Start <= _lineStart && _lineStart < s.End) { lead = s; break; }
                if (lead == null) lead = _plan.Segments[0];
                cp.Segments.Add(new HbFontSegment
                {
                    Start = visibleLen, End = visibleLen + Ellipsis.Length,
                    Face = lead.Face, Fallback = lead.Fallback, FaceSlot = lead.FaceSlot,
                    Weight = lead.Weight, Width = lead.Width, Slant = lead.Slant,
                });
                cp.TextLength = collapsedText.Length;
            }
            HbShapedRun shaped = cp != null
                ? HbMultiFontShaper.ShapeParagraph(cp, collapsedText, _shaped.EmSize)
                : HbShaper.Shape(_fontPath, collapsedText, _shaped.EmSize);
            TextRunProperties props = _runProperties ?? new HbRunProperties(null, _shaped.EmSize, null);
            var run = new HbTextRun(collapsedText, 0, collapsedText.Length, props);
            var collapsed = new HbTextLine(run, shaped, _glyphTypeface, _pixelsPerDip, collapsedText, 0,
                                           _fontPath, 0, false, false, _hasModifierScope, false, 0,
                                           _length,                       // ⑤ 折后行 Length = 原 Length
                                           _newlineLength,
                                           0,
                                           collapsedWidth,
                                           collapsedWidth, _segmentFaces, cp, _paragraphWidth,
                                           // `#21`（主控口径）：折叠路径**必须一并转发** `_paragraphIndentDip` ——
                                           //   否则同一个段落里"正常行 Start=PI、折叠行 Start=0"两套框架并存。
                                           //   ⚠️ **未测**：折叠行在 `PI≠0` 下的 `Start` 真值本语料**没有覆盖**
                                           //   ⇒ 这是"为保持成员自洽"的选择，**不是实测结论**。
                                           paragraphIndentDip: _paragraphIndentDip,
                                           // ★`#23` P2（**折叠路径必须一并透传** —— `#21` 曾漏了这条被主控拦下）：
                                           //   折后行的文本 = "**原行起点起的**可见前缀 + 省略号" ⇒ 它自己的 `_lineStart` 仍是 0
                                           //   （下标系原点就是原行起点），所以**绝对帧 = 原行的绝对帧** = `_paragraphOrigin + _lineStart`。
                                           //   ⚠️ **未测**：折叠行在 `PI≠0`/原点≠0 下的真值本语料**没有覆盖**（`#21`/`#22` 同款登记）
                                           //   ⇒ 这是"为保持成员自洽"的选择，**不是实测结论**。
                                           paragraphOrigin: _paragraphOrigin + _lineStart);
            return collapsed;
        }

        // ==================================================================
        //  6 个仍然欠账的 override —— **安全回退，不抛**
        // ==================================================================
        //
        //  【为什么不抛】抛异常 = 崩溃；回退 = 降级但可用 —— 产品上是天壤之别。
        //  【严格模式】`WPF_LINUX_TEXTLINE_STRICT=1` 时改为抛异常（bring-up 期定位用）。
        //  【B2 减少的欠账】Collapse / GetTextCollapsedRanges / GetTextLineBreak 已**真实现**（上面）。
        //    剩下的 5 个：光标/命中 5 个（B3）—— `GetIndexedGlyphRuns` 已于 2026-09-15 真实现（`D-F1`）。
        //    主控 2026-09-11 明确不实现 —— 做了也无人验证，只会变成新的谎报面）。
        // ==================================================================

        private static void Owed(string member)
        {
            HbTextLineScaffold.NoteFallback(member);
            if (HbTextLineScaffold.Strict)
                throw new NotSupportedException(
                    $"HbTextLine.{member} 仍是**回退**实现（WPF_LINUX_TEXTLINE_STRICT=1 时抛）");
        }

        /// <summary>
        /// **真实现**（2026-09-15 主控推翻旧裁定：它是**真值的观测装置** —— C2 判据看 `GlyphIndices != 0`）。
        /// 语义：逐 run 给 `IndexedGlyphRun { TextSourceCharacterIndex, TextSourceLength, GlyphRun }`；
        /// `GlyphRun.GlyphTypeface` 是**实际用的那个面**（回退面时就是回退面）、`GlyphIndices`（0 = `.notdef`）、
        /// `AdvanceWidths` 均为真值 —— 这些都来自既有的 `_glyphRuns` / `_glyphRunCharStart`（不是新造的桩）。
        /// </summary>
        /// <summary>`IndexedGlyphRun` 的唯一构造入口：DIRECT 强类型；反射分支（`TextLineProto`/`HbTextLineParity`）
        /// 用**反射**取同一个内部 3 参构造（**不许桩/空值** —— 那会让 C2 观测面在独立配置下失效）。</summary>
        private static IndexedGlyphRun MakeIndexedGlyphRun(int startChar, int len, GlyphRun gr)
        {
#if TEXTLINE_SHIM_DIRECT
            return new IndexedGlyphRun(startChar, len, gr);
#else
            object o = Activator.CreateInstance(typeof(IndexedGlyphRun),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic, null,
                new object[] { startChar, len, gr }, null);
            if (o is IndexedGlyphRun) return (IndexedGlyphRun)o;
            throw new NotSupportedException("IndexedGlyphRun 的 3 参构造反射取不到 —— 宁可响亮失败，也不用桩冒充真值");
#endif
        }

        public override IEnumerable<IndexedGlyphRun> GetIndexedGlyphRuns()
        {
            var list = new List<IndexedGlyphRun>();
            if (_glyphRuns == null || _glyphRuns.Count == 0) return list;
            // ⚠️ 用 `_visibleLength`（不含末行 EOP 的 +1）：`_length` 含 EOP，会让最后一个 run 的
            //   `TextSourceLength` 多 1（自验档实测 `0+4` vs 字符串 3 个码元）。
            // ★`#23` P2：本成员吐的是**段落系绝对**下标（`IndexedGlyphRun.TextSourceCharacterIndex`）
            //   ⇒ 与 `_glyphRunCharStart` 的播种同系，两处都带 `_paragraphOrigin`。
            int lineEnd = _paragraphOrigin + _lineStart + _visibleLength;
            for (int i = 0; i < _glyphRuns.Count; ++i)
            {
                int startChar = (i < _glyphRunCharStart.Count) ? _glyphRunCharStart[i] : (_paragraphOrigin + _lineStart);
                int endChar = (i + 1 < _glyphRunCharStart.Count) ? _glyphRunCharStart[i + 1] : lineEnd;
                int len = endChar - startChar;
                if (len < 0) len = 0;
                GlyphRun gr = _glyphRuns[i];
                if (gr == null) continue;
                list.Add(MakeIndexedGlyphRun(startChar, len, gr));
            }
            return list;
        }

        public override CharacterHit GetCharacterHitFromDistance(double distance)
        {
            Owed("GetCharacterHitFromDistance");
            return new CharacterHit(0, 0);
        }

        public override double GetDistanceFromCharacterHit(CharacterHit characterHit)
        {
            Owed("GetDistanceFromCharacterHit");
            return 0;
        }

        public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit)
        {
            Owed("GetNextCaretCharacterHit");
            return characterHit;
        }

        public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit)
        {
            Owed("GetPreviousCaretCharacterHit");
            return characterHit;
        }

        public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit)
        {
            Owed("GetBackspaceCaretCharacterHit");
            return characterHit;
        }
    }

    /// <summary>
    /// **两种编译形态**下对 `GlyphTypeface` 内部成员的统一访问点。
    ///
    /// 【为什么必须有它】同一份 shim 要编成两种形态：
    ///   · `TEXTLINE_SHIM_DIRECT`（编进 PC / IVT 闸门）⇒ internal 成员可见 ⇒ 真读；
    ///   · 反射形态（`HbTextLineParity` / `TextLineProto` 直接把本文件编进自己）⇒
    ///     `GlyphTypeface.FaceIndex` / `GetDWriteFontAddRef` 是 **PC 的 internal**，
    ///     对外不可见 ⇒ 编不过（实测 CS1061）。**反射形态必须能编过**：那三条
    ///     "拉丁不退步"读数（`73/73`、`T2d 1298/1298`、折叠）全建立在它之上。
    ///
    /// 【诚实降级（不许假装校验过）】反射形态下取不到面下标 ⇒ 按 **0** 处理（= 今天
    ///   整形时的既有假设）并把"这一条校验被跳过"**计数**；令牌同样报"不可用"。
    ///   绝不在没验的情况下声称验过。
    /// </summary>
    internal static class HbFaceInternals
    {
        /// <summary>"面下标/令牌"这两个内部成员在本形态下能不能读。</summary>
        internal static bool Available
        {
#if TEXTLINE_SHIM_DIRECT
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>**被跳过的校验次数**（反射形态下 > 0；DIRECT 形态下恒 0）。</summary>
        internal static long SkippedChecks = 0;

        internal static int FaceIndexOf(GlyphTypeface gt)
        {
#if TEXTLINE_SHIM_DIRECT
            return gt == null ? -1 : gt.FaceIndex;
#else
            ++SkippedChecks;
            return 0;                       // 拓扑降级：与今天"整形固定 face0"的假设一致
#endif
        }

        internal static string TokenOf(GlyphTypeface gt)
        {
#if TEXTLINE_SHIM_DIRECT
            return gt == null ? "-" : "0x" + gt.GetDWriteFontAddRef.ToInt64().ToString("x");
#else
            ++SkippedChecks;
            return "(反射形态不可用)";
#endif
        }
    }

    // ==================================================================================
    //  6. 段落工厂 —— 把一段文本变成一串**真** `HbTextLine`（接线与 harness 的唯一入口）
    // ==================================================================================

    /// <summary>
    /// 段落 → 行。**这是 B2 的对外入口**：主控接线时把挑行那段指到这里即可；
    /// harness 也走同一个入口（所以"harness 测的"就是"接线后跑的"）。
    /// </summary>
    internal static class HbTextLineFactory
    {
        /// <summary>
        /// ★T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：**段末行真的带上了"源自己的" EOL run 的段数**。
        /// 缺它则本改动会落进本项目反复栽的那一类"**接了但没生效**"（`#17` 族）：`eopRun` 是个**尾随可选形参**，
        /// 收集层不传也**编译得过**、且行为与修前**逐位相同**却**看不出来** ⇒ 计数把它变成运行期可读读数。
        /// 出口：`WPF_LINUX_TEXTLINE_LSEM=1` 的具名行 `[TEXTLINE_LSEM]`（有界：前 12 条），另见 `SummaryLine`。
        /// </summary>
        internal static long ParagraphEndRuns;
        private const string LsemEnvVar = "WPF_LINUX_TEXTLINE_LSEM";
        private static int s_lsemLeft = 12;

        /// <summary>排一段文本。返回的行按顺序；`consumedLength` = 各行 `Length` 之和
        /// （真机实测 = 文本长度 + 1，那个 +1 就是末行 EOP）。
        /// </summary>
        /// <summary>D-F1b：`HbFaceRef` → `GlyphTypeface`（**唯一造面入口**）。面号语义照抄上游
        /// `Util.CombineUriWithFaceIndex`（`upstream/wpf/…/MS/internal/FontCache/FontCacheUtil.cs:509-520`）：
        /// faceIndex == 0 ⇒ **原样 Uri（不加 fragment）**；faceIndex &gt; 0 ⇒ canonicalPathUri + '#' + n。
        /// **内联而不直调**上游 helper：`Util` 只在 DIRECT 配置可见 ⇒ 直调会让两配置跑不同代码（违反"测谁就报谁"）。</summary>
        private static GlyphTypeface FaceFromRef(HbFaceRef fref)
        {
            HbFallbackDiag.NoteSegmentFaceResolve();                 // D-F1c3 只读计数（不改行为）
            if (fref == null || string.IsNullOrEmpty(fref.Path)) return null;
            string uriText = "file://" + fref.Path;
            if (fref.FaceIndex != 0)
                uriText = new Uri(uriText).GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped)
                          + "#" + fref.FaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // D-F1b/E1（主控 2026-09-15）：**复用应用路径 `GetResolvedFace` 那条路线**（族名 → `Typeface` →
            //   `TryGetGlyphTypeface`），**不新建第二条路**。原因（实测）：`new GlyphTypeface(new Uri(...))`
            //   对我们**自己 pin 的纯 TTF**（`src/Linux/build/fonts/NotoSans-*.ttf`）也抛 `FileFormatException`
            //   ⇒ 该公开入口在本移植版对**文件字体实质不可用**（记 `D-F2`，本轮不修）。
            try
            {
                string family = HbFaceCache.FamilyOf(fref.Path, fref.FaceIndex);
                if (!string.IsNullOrEmpty(family))
                {
                    var tf = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    GlyphTypeface gt;
                    if (tf.TryGetGlyphTypeface(out gt) && gt != null) return gt;
                }
            }
            catch (Exception) { }
            try { return new GlyphTypeface(new Uri(uriText)); } catch (Exception) { return null; }
        }

        /// <summary>D-F1b 的**共用**段面构造：逐段 `FaceFromRef` 并挂 `FaceSlot`；失败 ⇒ `FaceSlot = -1` **且记账**（不静默）。</summary>
        private static void BuildSegmentFacesFromPlan(HbFontPlan plan, ref GlyphTypeface[] segmentFaces)
        {
            if (plan == null || plan.Segments.Count == 0) return;
            var fcs = new List<GlyphTypeface>(plan.Segments.Count);
            for (int i = 0; i < plan.Segments.Count; ++i)
            {
                GlyphTypeface g = FaceFromRef(plan.Segments[i].Face);
                fcs.Add(g);
                plan.Segments[i].FaceSlot = (g != null) ? i : -1;
                if (g == null) HbFallbackDiag.NoteSegmentFaceUnresolved();     // D-F1b/P2
            }
            segmentFaces = fcs.ToArray();
        }

        internal static List<HbTextLine> FormatParagraph(
            string text,
            string fontPath,
            double emSize,
            double paragraphWidthDip,
            GlyphTypeface glyphTypeface,
            float pixelsPerDip,
            TextRunProperties runProperties,
            bool alwaysCollapsible,
            bool hasModifierScope,
            double lineHeight,          // 0 = 未设（⇒ 用字体自然行高）
            out int consumedLength,
            HbFontPlan plan = null,             // R1/T1d：多字体面计划（null ⇒ 与今天逐位相同的单面路径）
            GlyphTypeface[] segmentFaces = null,
            TextRunProperties[] runProps = null,   // 逐 run 的 props（前景色跟着源 run 走；null ⇒ 与今天逐位相同）
            double indentDip = 0,                 // 真机 `TextParagraphProperties.Indent`（只作用首行）
            double defaultIncrementalTab = double.NaN,   // NaN ⇒ 框架默认 4×em；<=0 ⇒ 显式无停靠位（旧语料）
            bool wrap = true,
            // #13（T1d，2026-09-15）：`TextModifier` 作用域的**两端**（均为**段落相对**码元下标）。
            //   `openIndex` = 客户端 `GetTextRun(i)` **返回 `TextModifier` run** 的下标；
            //   `closeIndex` = 客户端返回**配对 `TextEndOfSegment(1)`** 的下标；**`-1` = 从不关闭**（scope 到段末）
            //   —— 我们 b34 语料就是这一支（T1b 核实：0 命中 `TextEndOfSegment`）。
            //   ⚠️ `TextModifier.Length` **不参与任何计算**（U1 §4：本 arm 里恒为 1 = 合成边缘字符的长度，
            //      而 scope 真实覆盖 A=4 / B=30→34 / C=36 字符，几何由 `C2-scope-visible` 钉死）。
            //   ⚠️ 半开区间：U1 单样本结论 = `closeMarker` 与 `closeMarker+1` 的**分歧输入真机产生不出来**
            //      （零宽标记粘在后一字符上）⇒ **取 `[open, close)` 即可**。
            //   ⚠️ **零宽跨度另有一个来源**（2026-09-15 自验档实测纠正）：客户端覆盖的**字符范围终点**
            //      `modifierScopeEnd`（半开；`-1` ⇒ 到段末）。它**不等于** `closeIndex`：
            //      b34 语料 `open=6, close=-1, 覆盖终点=45`（`cases.json` 的 `modifierEnd=45`），
            //      若拿 `close<0` 当"到段末"，会把 [6,63) 全零宽 ⇒ 实测 `w=43.59`，而真值 `156.9167`
            //      （= 可见 `[0,6)+[45,63)` 共 24 字符）⇒ **跨度只认 `modifierScopeEnd`**，`closeIndex` **只**管 `lbNull`。
            int modifierOpenIndex = -1, int modifierScopeEnd = -1, int modifierCloseIndex = -1,
            double paragraphIndentDip = 0,   // D-T2/(C)：尾随可选，默认 0 ⇒ 既有调用点零改动
            // ★`#23` P2（`D-T6-b`）：本段落在**调用方 `TextSource`** 里的原点（= 收集 `text` 时的 `cpFirst`；
            //   缺省 0 ⇒ **既有调用点零改动、逐位等价**）。它只喂 `HbTextLine._paragraphOrigin`
            //   （帧的绝对系），**不参与**任何度量/整形/断行计算。
            int paragraphOrigin = 0,
            // ★T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：**段末行的末 span 用哪个 run** ——
            //   收集层在段末取到的那个对象（宿主 PtsHost 给的是 `ParagraphBreakRun`）；缺省 null ⇒
            //   与修前逐位相同（就地造 `TextEndOfParagraph(1)`）。
            TextRun eopRun = null)
        {
            text = text ?? string.Empty;
            // D-F1（2026-09-15 主控派单）：**单面路径也先构造计划**（`allowFallback:true`）。
            //   旧行为 = `plan == null` ⇒ 度量/整形都走单面 ⇒ **回退那段根本不会被问到**
            //   ⇒ `与`(U+4E0E) 在 file 字体里 gid=0 落 `.notdef`（advance 9.6 = 0.6 em），
            //      真机 16.0 = 1.0 em（回退到覆盖该码点的已安装面）。
            //   · 候选集/顺序**沿用我们自己的**（`PickFromRuns` → `HbFontCandidates.TryFindCovering`）；
            //   · 找不到 ⇒ `HbFallbackDiag.NoteFailed` ⇒ 沿用当前面（glyph 0）**不假装成功**；
            //   · 真机的搜索顺序取不到 ⇒ **只对齐结果语义**（面自己的字形与 advance）。
            if (plan == null && text.Length > 0)
            {
                int faceIdx = 0;
#if TEXTLINE_SHIM_DIRECT
                try { if (glyphTypeface != null) faceIdx = glyphTypeface.FaceIndex; } catch (Exception) { }
#else
                try                                      // 反射分支：同一个属性，取真值；取不到才 0
                {
                    if (glyphTypeface != null)
                    {
                        var pi = glyphTypeface.GetType().GetProperty("FaceIndex");
                        if (pi != null && pi.PropertyType == typeof(int)) faceIdx = (int)pi.GetValue(glyphTypeface);
                    }
                }
                catch (Exception) { }
#endif
                var infos = new List<HbRunFaceInfo>(1)
                {
                    new HbRunFaceInfo { Start = 0, Length = text.Length, Face = new HbFaceRef(fontPath, faceIdx),
                                        Weight = 400, Width = 5, Slant = 0, RunSlot = 0 }
                };
                // 闸门：单面路径用**宽松闸**（恒真）—— 覆盖面由 `TryFindCovering` 保证（它返回的就是覆盖 `cp` 的面）。
                plan = HbFontPlanner.Build(text, infos, true, (f, cp2, w2, wd2, s2) => true);
                // `segmentFaces`：逐段构造 `GlyphTypeface`（**1 参构造 ⇒ face 0**；`D-F1` 登记的限制：
                //   `.ttc` 多面集合的 faceIndex 不走这里 —— 应用路径的 `GetResolvedFace` 才有）。
                BuildSegmentFacesFromPlan(plan, ref segmentFaces);   // D-F1b：共用 helper（内含面号语义）
                HbFallbackDiag.NoteRuns(1);
            }

            // D-F1b **共用点**：无论计划来自调用方还是就地建，只要调用方没给面表（或缺），就按**计划段自己的面**建
            //   ⇒ 修 T2 实测的 `fb/b34 C1=FAIL`（调用方自造计划时 `FaceSlot` 全 -1 ⇒ `FormatLine` 静默回落段落字体）。
            if (plan != null && (segmentFaces == null || segmentFaces.Length < plan.Segments.Count))
                BuildSegmentFacesFromPlan(plan, ref segmentFaces);
            List<HbLineRange> ranges = HbBreakEngine.LayoutText(text, paragraphWidthDip, emSize, fontPath, plan,
                                                               defaultIncrementalTab, wrap, indentDip,
                                                               modifierOpenIndex, modifierScopeEnd, paragraphIndentDip);   // D-T2/(C) 透传

            var lines = new List<HbTextLine>(ranges.Count);
            consumedLength = 0;
            foreach (HbLineRange r in ranges)
            {
                // advance 的来源：有计划走计划（各码点段各自的面），没计划与今天完全相同。
                double[] adv = (plan != null && plan.Segments.Count > 0)
                    ? HbBreakEngine.MeasureChars(text.Substring(r.Start, r.VisibleLength),
                                                 plan.Sub(r.Start, r.Start + r.VisibleLength), emSize)
                    : HbBreakEngine.MeasureChars(text.Substring(r.Start, r.VisibleLength), fontPath, emSize);
                // #13：**按行**判 `TextLineBreak` 是否非 null（U1 `modifier-scope` 53 例，99/99 行吻合）：
                //   非 null ⟺ **该行不是末行 ∧ 该行结束时 scope 仍打开**
                //   等价写法（U1 §3，"下一行起点"式，更好落地）：
                //     nonNull = !isLastLine && open ≥ 0 && open < nextLineStart && (close < 0 || close ≥ nextLineStart)
                //   ⚠️ 两条被真机否掉的候选：① 段落级 bool 直接下发每行（`A-scope-line0` 的第 1..n 行全 NULL）；
                //      ② "与该行相交即非 null"（同例第 0 行相交且非末行，仍 NULL —— scope 在同一行内就关了）。
                bool isLastLine = ReferenceEquals(r, ranges[ranges.Count - 1]);
                int nextLineStart = r.Start + r.VisibleLength;              // == line.endCharExclusive
                bool lineHasModifier;
                if (modifierOpenIndex < 0) lineHasModifier = hasModifierScope;   // 未给区间 ⇒ 退回今天的段落级语义（旧调用点逐位不变）
                else lineHasModifier = !isLastLine
                                       && modifierOpenIndex < nextLineStart
                                       && (modifierCloseIndex < 0 || modifierCloseIndex >= nextLineStart);
                HbTextLine line = HbTextLine.FormatLine(
                    text, r, adv, fontPath, emSize, glyphTypeface, pixelsPerDip, runProperties,
                    alwaysCollapsible, lineHasModifier, lineHeight, plan, segmentFaces, paragraphWidthDip, runProps,
                    indentDip, defaultIncrementalTab, wrap,      // 波 `#16` `D-T2`/(B)：原为 `lines.Count == 0 ? indentDip : 0`
                    modifierOpenIndex, modifierScopeEnd, modifierCloseIndex, paragraphIndentDip,
                    paragraphOrigin,   // ★`#23` P2：段落原点透到行构造（帧的绝对系 = 原点 + range.Start）
                    // ★T-A30：**只有段末行**（`HasEop`）才带源自己的 EOL run —— 其余行没有 EOP span，传了也用不上。
                    //   有界 ＋ 具名留痕：只用在同一段上算一次计数与（前 12 条）具名行，**绝不静默**。
                    r.HasEop ? NoteParagraphEndRun(eopRun, paragraphOrigin, ranges.Count, r.Start) : null);
                lines.Add(line);
                consumedLength += line.Length;
                HbLineTrace.SiteA(r.Start, line);          // 只读插桩（缺省关）
            }
            HbTextLineScaffold.NoteParagraphFormatted();
            return lines;
        }

        /// <summary>
        /// ★T-A30：**段末行带上源自己的 EOL run** 这件事的**具名留痕 ＋ 计数**（`eopRun == null` ⇒ 什么都没发生，
        /// 退回修前行为，此时**不**计数）。
        ///
        /// 【为什么必须有】`eopRun` 是尾随可选形参 ⇒ "形参加了、收集层没传"会**编译得过**、行为与修前逐位相同，
        /// 却看不出来（本项目 `#17` 族的形态）。⇒ 把"真的带上了"做成运行期可读：`[TEXTLINE_LSEM]` 具名行
        /// （`WPF_LINUX_TEXTLINE_LSEM=1`；**有界**：前 12 条）＋ 计数 `ParagraphEndRuns`。
        /// `type` 是**运行时对象类型名**（宿主 PtsHost 给 `ParagraphBreakRun`，`TextBox` 一族给
        /// `TextEndOfParagraph`）—— 两者都合法，本行把**实际是谁**印出来，不做归一化。
        /// </summary>
        private static TextRun NoteParagraphEndRun(TextRun eopRun, int paraOrigin, int lineCount, int lastLineStart)
        {
            if (eopRun == null) return null;
            ++ParagraphEndRuns;
            if (s_lsemLeft > 0
                && HbTextLineScaffold.ParseOnOff(Environment.GetEnvironmentVariable(LsemEnvVar)))
            {
                --s_lsemLeft;
                try
                {
                    Console.Error.WriteLine("[TEXTLINE_LSEM] paraOrigin=" + paraOrigin
                        + " lines=" + lineCount + " lastLineStart=" + lastLineStart
                        + " eopRunType=" + eopRun.GetType().Name + " eopRunLen=" + eopRun.Length
                        + " paraEndRuns=" + System.Threading.Interlocked.Read(ref ParagraphEndRuns));
                }
                catch (Exception) { }
            }
            return eopRun;
        }
    }

#if TEXTLINE_SHIM_DIRECT
    // ==================================================================================
    //  8. ⭐ D3：`TextFormatterImp` 回退接线点 —— **替换 LS 那条回退路**（不是绕过 TextFormatter）
    // ==================================================================================
    //
    //  【为什么需要】上游只有一条回退（`TextFormatterImp.cs:236-246` 与 `:309`）：
    //      `SimpleTextLine.Create` 返回 null ⇒ `new TextMetrics.FullTextLine(...)` ⇒
    //      `TextFormatterContext` → **`LoCreateContext`** ⇒ 本工程 shim 里该符号**不存在** ⇒
    //      `EntryPointNotFoundException` ⇒ abort(134)。实测：WpfTextDemo 3 连崩，
    //      HelloWpf（小文本，留 SimpleTextLine）正常。
    //
    //  【设计】**先试托管完整路径，接不了就返回 null 让它原样回退 LS**：
    //      · `SimpleTextLine` 快路径**一字不动**；
    //      · 只有"本来要进 LS"的行才可能走到这里；
    //      · 任何不确定的输入（非 TextCharacters 的 run / 字体解析不到文件 / 任何异常）
    //        ⇒ **返回 null**（绝不假装成功），并**逐类计数**，从汇总行可读。
    //      · 开关复用 `WPF_LINUX_TEXTLINE`（关掉 ⇒ 一行不动，回到原行为，便于 A/B 对照）。
    //
    //  【索引协议】`FormatLine` 每行调一次、`firstCharIndex` 由调用方**累加 Length**；
    //      所以我们把**整段**排一次并缓存，按 `cpFirst` 命中缓存逐行交出（同一个 TextSource + 段起点）。
    // ==================================================================================
    internal static class HbTextFallback
    {
        internal static long Calls, Handled, Bailed, BailNoSwitch, BailRunType, BailFont, BailEmpty, BailLong;
        internal static long BailException, MinMaxCalls, MinMaxHandled, MinMaxBailed;
        internal static string LastBail = "-";
        private static int s_diagLeft = 8;
        private const int MaxParagraphChars = 8192;   // 超过就交回 LS（不猜）

        private sealed class ParaCache
        {
            public TextSource Source;
            public int Start;
            public List<HbTextLine> Lines;
            public bool Contains(int cp)
            {
                int pos = Start;
                foreach (HbTextLine L in Lines) { if (pos == cp) return true; pos += L.Length; }
                return false;
            }
            public HbTextLine LineAt(int cp)
            {
                int pos = Start;
                foreach (HbTextLine L in Lines) { if (pos == cp) return L; pos += L.Length; }
                return null;
            }
        }

        private static ParaCache s_cache;

        /// <summary>
        /// 回退开关：**默认开**（主控 2026-09-11 批准的设计）。
        ///
        /// · 不设任何 env ⇒ **生效**（这是接通 LS 崩溃那条路的关键：默认关等于 D3 不存在）；
        /// · `WPF_LINUX_TEXTLINE=0` ⇒ 关（回到接线前行为，A/B 对照用）；
        /// · `WPF_LINUX_TEXTLINE_FALLBACK=0` ⇒ 只关回退，不影响别的读数。
        ///
        /// ⚠️ **为什么必须默认开**（实测，不是偏好）：T3 的 `run-wpftextdemo.sh` 会**主动清空**
        ///   `WPF_LINUX_TEXTLINE*`（其输出原文：`默认档将清空的环境变量：-u WPF_LINUX_TEXTLINE_DUMP
        ///   -u WPF_LINUX_TEXTLINE -u WPF_LINUX_TEXTLINE_DIAG`）⇒ 靠 env 打开的话在那个 runner 里
        ///   **永远打不开**，验收无法进行（第一版就是踩了这个：默认关 + runner 清 env ⇒
        ///   `LoCreateContext` 依旧被查找，D3 看起来"没生效"）。
        ///
        /// 注意：这与 shim 的**总开关**语义（`HbTextLineScaffold.Enabled` 默认关 ⇒ 未接线时惰性）
        /// **不冲突** —— 那是"本文件有没有被接线"的开关，这里是"接线后走不走托管路径"的开关。
        /// </summary>
        internal static bool Enabled
        {
            get
            {
                if (IsExplicitOff(Environment.GetEnvironmentVariable(HbTextLineScaffold.EnableEnvVar))) return false;
                if (IsExplicitOff(Environment.GetEnvironmentVariable(FallbackEnvVar))) return false;
                return true;
            }
        }

        internal const string FallbackEnvVar = "WPF_LINUX_TEXTLINE_FALLBACK";

        /// <summary>
        /// R1/T1d 的**多字体开关**：**缺省开**；显式 `WPF_LINUX_MULTIFONT=0/false/off/no` ⇒ 关。
        ///
        /// 【为什么默认开】与 D3 同一条理由：T3 的 runner **主动清空 `WPF_LINUX_*`**
        ///   ⇒ 靠 env 打开等于在那个 runner 里永远打不开（验收就做不了）。
        /// 【关掉时的语义】**与今天逐位相同** —— 只用第一个 run 的面、单面整形、
        ///   `FormatParagraph` 不多传参数（A/B 对照用；见报告里的 A/B 两组读数）。
        /// </summary>
        internal const string MultiFontEnvVar = "WPF_LINUX_MULTIFONT";

        internal static bool MultiFontEnabled
            => !IsExplicitOff(Environment.GetEnvironmentVariable(MultiFontEnvVar));

        // ==================================================================
        //  R1/T1d：run 级面 + 按码点覆盖回退（**只在这条 run 感知路径上生效**）
        // ==================================================================
        //
        //  【为什么只在这条路上生效】单字体入口 `FormatParagraph(text, fontPath, …)` 是
        //    `run.sh tline`（73 例 CJK + 614 例真机 oracle）走的入口，那些真值是
        //    **Windows 私有文件字体口径**（那边同时也跑复合字体回退）⇒ 在那边掺进回退会
        //    同时动两件事，把"拉丁不退步 1298/1298"这条判据的含义搅浑。故刻意只改应用路径。
        // ==================================================================

        /// <summary>收集到的一个 run（`TextCharacters`）：段落内字符起点 + 长度 + **它自己的** props。</summary>
        internal sealed class CollectedRun
        {
            public int Start;
            public int Length;
            public TextRunProperties Props;
            public override string ToString() => "[" + Start + "," + (Start + Length) + ")";
        }

        /// <summary>一个 run 解析出来的面（渲染面 + 路径/下标 + 三要素）。</summary>
        private sealed class RunFace
        {
            public HbFaceRef FaceRef;
            public GlyphTypeface Typeface;
            public string FontPath;
            public int Weight = 400, Width = 5, Slant = 0;
            public bool Resolved;
            public override string ToString() => (FaceRef != null ? FaceRef.ToString() : "<未解析>") + " w=" + Weight;
        }

        /// <summary>按**引用**比较的字典键（`TextRunProperties` 的子类可能重写 Equals，这里不要那个语义）。</summary>
        private sealed class RefComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly RefComparer<T> Instance = new RefComparer<T>();
            public bool Equals(T x, T y) => object.ReferenceEquals(x, y);
            public int GetHashCode(T o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>候选面 → 渲染面的解析结果（**键含三要素**：同族不同字重是不同文件）。</summary>
        private sealed class ResolvedFace
        {
            public GlyphTypeface Typeface;
            public string How;
            public string Why;
            public bool Ok;
        }

        private static readonly Dictionary<TextRunProperties, RunFace> s_runFaces =
            new Dictionary<TextRunProperties, RunFace>(RefComparer<TextRunProperties>.Instance);
        private static readonly Dictionary<string, ResolvedFace> s_faceResolve =
            new Dictionary<string, ResolvedFace>(StringComparer.Ordinal);

        internal static long RunFontUnresolved, FaceIndexUnknown, CmapMismatch;

        /// <summary>把一个 run 的 props 解析成"面"（按 props **引用**缓存）。</summary>
        private static bool ResolveRunFace(TextRunProperties props, out RunFace rf)
        {
            rf = null;
            if (props == null) return false;
            if (s_runFaces.TryGetValue(props, out rf)) return rf != null && rf.Resolved;

            GlyphTypeface gt; string path;
            if (!TryResolveFont(props, out gt, out path))      // 失败已由 TryResolveFont 计数（Bail）
            {
                s_runFaces[props] = new RunFace { Resolved = false };
                return false;
            }

            int idx = 0;
            try { idx = HbFaceInternals.FaceIndexOf(gt); }
            catch (Exception e)
            {
                ++FaceIndexUnknown;
                HbTextLineScaffold.Diag("R1：GlyphTypeface.FaceIndex 取不到（" + e.GetType().Name + "）⇒ 按 0 处理");
            }
            if (!HbFaceInternals.Available) ++FaceIndexUnknown;

            rf = new RunFace { FaceRef = new HbFaceRef(path, idx), Typeface = gt, FontPath = path, Resolved = true };
            Typeface tf = props.Typeface;
            try
            {
                if (tf != null)
                {
                    rf.Weight = ((FontWeight)tf.Weight).ToOpenTypeWeight();
                    rf.Width = ((FontStretch)tf.Stretch).ToOpenTypeStretch();
                    rf.Slant = tf.Style == FontStyles.Italic ? 2 : (tf.Style == FontStyles.Oblique ? 1 : 0);
                }
            }
            catch (Exception) { /* 三要素取不到就用默认（不影响"能不能出字"，只影响挑哪份面） */ }
            s_runFaces[props] = rf;
            return true;
        }

        /// <summary>
        /// 候选面 → **渲染面**。只用**公开 API**：HB 读族名 → `FontFamily`/`Typeface` →
        /// `TryGetGlyphTypeface`（其 `_font` 来自 provider 的 `SKTypeface.FromFile(path, faceIndex)`，
        /// 令牌因此带上正确的 `(path, faceIndex)` —— 与 T1c 的 (乙) 契约同一条链）。
        ///
        /// ⚠️ **绝不用 `FromBytes` 造出来的面**（`SourcePath == null` ⇒ 路径式令牌分配器拿不到路径
        ///   ⇒ MIL 解析不到 ⇒ 渲染器用错面光栅化我们的 id）。这里全程按路径取面。
        /// </summary>
        private static ResolvedFace GetResolvedFace(HbFaceRef face, int weight, int width, int slant)
        {
            if (face == null || string.IsNullOrEmpty(face.Path)) return new ResolvedFace { Why = "面为空" };
            string key = face + "|" + weight + "|" + width + "|" + slant;
            ResolvedFace hit;
            if (s_faceResolve.TryGetValue(key, out hit))
            {
                ++HbFallbackDiag.FaceResolveCacheHits;
                return hit;
            }

            ++HbFallbackDiag.FaceResolveCalls;
            var res = new ResolvedFace();
            s_faceResolve[key] = res;                 // 先落"失败"：重入/重复失败不再付代价（下同）
            try
            {
                string family = HbFaceCache.FamilyOf(face.Path, face.FaceIndex);
                if (string.IsNullOrEmpty(family) || family == "?")
                {
                    res.Why = "HB 读不到族名";
                }
                else
                {
                    var tf = new Typeface(
                        new FontFamily(family),
                        slant == 2 ? FontStyles.Italic : (slant == 1 ? FontStyles.Oblique : FontStyles.Normal),
                        FontWeight.FromOpenTypeWeight(weight),
                        FontStretch.FromOpenTypeStretch(width));
                    GlyphTypeface gt;
                    if (!tf.TryGetGlyphTypeface(out gt) || gt == null)
                    {
                        res.Why = "TryGetGlyphTypeface 失败（族 " + family + "）";
                    }
                    else
                    {
                        string why;
                        if (!VerifyFaceIdentity(gt, face, out why)) res.Why = why;
                        else { res.Typeface = gt; res.How = "family:" + family; res.Ok = true; }
                    }
                }
            }
            catch (Exception e) { res.Why = "异常 " + e.GetType().Name + ": " + e.Message; }

            if (!res.Ok)
            {
                ++HbFallbackDiag.FaceResolveFailures;
                res.Why = (res.Why ?? "?") + "（面=" + face + "）";
                // ⚠️ 失败原因**必须能看见**：否则"回退没生效"只剩一个数字，没人知道为什么。
                HbTextLineScaffold.Diag("R1 候选面物化失败：" + res.Why
                                        + "｜字重/拉伸/斜体=" + weight + "/" + width + "/" + slant);
            }
            return res;
        }

        /// <summary>面的**身份**校验（文件 + TTC 面下标）：这两条不过 ⇒ 整形面与渲染面不是一份面。</summary>
        private static bool VerifyFaceIdentity(GlyphTypeface gt, HbFaceRef face, out string why)
        {
            why = null;
            try
            {
                Uri u = gt.FontUri;
                string p = (u != null && u.IsFile) ? u.LocalPath : null;
                if (string.IsNullOrEmpty(p) || !string.Equals(p, face.Path, StringComparison.Ordinal))
                {
                    why = "文件不符：" + (p ?? "<null>") + " ≠ " + face.Path;
                    return false;
                }
                int idx = HbFaceInternals.FaceIndexOf(gt);
                if (!HbFaceInternals.Available)
                {
                    // 诚实降级：这一条**没验**（不是"验过了"）。反射形态下整形固定 face0，
                    // 与 FaceIndexOf 的降级值一致 ⇒ 语义与今天相同。
                    return true;
                }
                if (idx != face.FaceIndex)
                {
                    why = "面下标不符：" + idx + " ≠ " + face.FaceIndex + "（" + face.Path + "）";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                why = "取 FontUri/FaceIndex 抛 " + e.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// 逐回退码点验「**整形面 == 渲染面**」：两边对同一码点必须给出同一 glyph id。
        /// 实测依据：`NotoSansCJK-Regular.ttc` face0(JP) 与 face2(SC) 对 `文` 是 20035 vs 20036
        /// ⇒ 面下标错了，id 是对的、字是错的（普查全绿而像素垃圾）。
        /// </summary>
        private static bool VerifyCmap(GlyphTypeface gt, HbFaceRef face, int cp, out string why)
        {
            why = null;
            try
            {
                int hb = HbShaper.NominalGlyph(face.Path, face.FaceIndex, cp);
                IDictionary<int, ushort> map = gt.CharacterToGlyphMap;
                ushort pc;
                if (map == null || !map.TryGetValue(cp, out pc))
                {
                    why = "PC 侧 cmap 没有 U+" + cp.ToString("X4");
                    return false;
                }
                if (pc != hb)
                {
                    why = "U+" + cp.ToString("X4") + "：PC=" + pc + " HB=" + hb;
                    return false;
                }
                return true;
            }
            catch (Exception e) { why = "cmap 比较抛 " + e.GetType().Name; return false; }
        }

        /// <summary>计划闸门：**只有既覆盖、又能物化成同一份渲染面的候选才会进计划**。</summary>
        private static bool FaceGate(HbFaceRef face, int cp, int weight, int width, int slant)
        {
            ResolvedFace rf = GetResolvedFace(face, weight, width, slant);
            if (!rf.Ok) return false;
            string why;
            if (VerifyCmap(rf.Typeface, face, cp, out why))
            {
                if (HbTextLineScaffold.DiagEnabled)
                    HbTextLineScaffold.Diag("R1 回退面通过闸门：U+" + cp.ToString("X4") + " → " + face
                                            + " (" + rf.How + ")");
                return true;
            }
            ++CmapMismatch;
            if (HbTextLineScaffold.DiagEnabled)
                HbTextLineScaffold.Diag("R1 回退面**被闸门拒**：U+" + cp.ToString("X4") + " → " + face + "：" + why);
            return false;
        }

        /// <summary>把计划里的面映射成"槽位 → 渲染面"数组（槽位由计划给出，`Sub()` 不会重编号）。</summary>
        private static GlyphTypeface[] BuildSegmentFaces(HbFontPlan plan, RunFace primary)
        {
            var slotOf = new Dictionary<HbFaceRef, int>();
            var list = new List<GlyphTypeface>();
            if (primary != null && primary.Resolved && primary.FaceRef != null)
            {
                slotOf[primary.FaceRef] = 0;
                list.Add(primary.Typeface);
            }
            foreach (HbFontSegment s in plan.Segments)
            {
                if (s.Face == null) { s.FaceSlot = -1; continue; }
                int slot;
                if (slotOf.TryGetValue(s.Face, out slot)) { s.FaceSlot = slot; continue; }
                ResolvedFace rf = GetResolvedFace(s.Face, s.Weight, s.Width, s.Slant);
                if (!rf.Ok)
                {
                    // 闸门保证过的不该走到这里；真走到就是闸门被绕过 ⇒ **响亮计数**（不静默画错字）。
                    ++HbFallbackDiag.FaceResolveFailures;
                    HbTextLineScaffold.Diag("R1 计划里出现了没通过闸门的段：" + s + "：" + rf.Why);
                    s.FaceSlot = -1;
                    continue;
                }
                slot = list.Count;
                list.Add(rf.Typeface);
                slotOf[s.Face] = slot;
                s.FaceSlot = slot;
            }
            return list.ToArray();
        }

        /// <summary>
        /// 收集 run + 解析面 + 建计划（`TryFormatLine` 与 `TryMinMaxParagraphWidth` 共用）。
        /// 返回 false ⇒ 交回 LS（与接线前一致）。
        /// </summary>
        private static bool TryBuildPlan(TextSource textSource, int cpFirst,
                                         out string text, out HbFontPlan plan, out GlyphTypeface[] faces,
                                         out RunFace primary, out TextRunProperties primaryProps, out double emSize,
                                         out TextRunProperties[] runProps, out TextRun eolRun)
        {
            text = null; plan = null; faces = null; primary = null; primaryProps = null; emSize = 0; runProps = null;
            // ★T-A30：段末取到的那个 EOL run（宿主 PtsHost 给 `ParagraphBreakRun`）⇒ 交给 `FormatParagraph`
            //   放到段末行的末 span 上，让 `Line.FormattingResult` 报 `fsflrEndOfParagraph`（§`_eopRun`）。
            eolRun = null;

            List<CollectedRun> runs;
            if (!TryCollect(textSource, cpFirst, out text, out runs, out eolRun)) return false;
            if (!ResolveRunFace(runs[0].Props, out primary)) return false;   // 与今天一样：第一个 run 的面解析不到就交回
            primaryProps = runs[0].Props;
            emSize = runs[0].Props.FontRenderingEmSize;

            if (!MultiFontEnabled) return true;                             // A/B：关 ⇒ 与今天逐位相同

            HbFallbackDiag.NoteRuns(runs.Count);

            // ---- run 级取面（**解析不到的 run 沿用上一个成功的面 + 计数**，不整体失败）----
            var infos = new List<HbRunFaceInfo>(runs.Count);
            var propsList = new List<TextRunProperties>(runs.Count);
            RunFace last = primary;
            for (int ri = 0; ri < runs.Count; ++ri)
            {
                CollectedRun r = runs[ri];
                RunFace rf;
                if (!ResolveRunFace(r.Props, out rf))
                {
                    ++RunFontUnresolved;
                    rf = last;
                }
                last = rf;
                propsList.Add(r.Props);
                infos.Add(new HbRunFaceInfo
                {
                    Start = r.Start, Length = r.Length, Face = rf.FaceRef,
                    Weight = rf.Weight, Width = rf.Width, Slant = rf.Slant,
                    RunSlot = ri,                      // ← 前景色跟着源 run 走
                });
            }
            runProps = propsList.ToArray();

            plan = HbFontPlanner.Build(text, infos, true, FaceGate);
            faces = BuildSegmentFaces(plan, primary);

            if (HbTextLineScaffold.DiagEnabled)
            {
                var sb = new StringBuilder();
                sb.Append("R1 面计划：runs=").Append(runs.Count).Append(' ');
                for (int i = 0; i < infos.Count && i < 6; ++i) sb.Append('{').Append(infos[i]).Append("} ");
                sb.Append("⇒ ").Append(plan.Describe());
                HbTextLineScaffold.Diag(sb.ToString());
            }
            return true;
        }

        /// <summary>逐行度量即时诊断（**缺省关**；独立开关，不与其他账混）。</summary>
        internal const string LineDiagEnvVar = "WPF_LINUX_TEXTLINE_LINEDIAG";

        private static bool IsExplicitOff(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "0": case "false": case "off": case "no": return true;
                default: return false;
            }
        }

        internal static string SummaryFragment() =>
            "fallbackCalls=" + Calls + " fallbackHandled=" + Handled + " fallbackBailed=" + Bailed
            + " bailNoSwitch=" + BailNoSwitch + " bailRunType=" + BailRunType + " bailFont=" + BailFont
            + " bailEmpty=" + BailEmpty + " bailLong=" + BailLong + " bailException=" + BailException
            + " minmaxCalls=" + MinMaxCalls + " minmaxHandled=" + MinMaxHandled + " minmaxBailed=" + MinMaxBailed
            + " lastBail=\"" + LastBail + "\""
            // ---- R1/T1d 追加（纯追加，不动上面的字段；格式对绊线脚本是安全的）----
            + " multiFontSwitch=" + (MultiFontEnabled ? 1 : 0)
            + " runFontUnresolved=" + RunFontUnresolved
            + " faceIndexUnknown=" + FaceIndexUnknown
            + " cmapMismatch=" + CmapMismatch
            + " faceInternalsSkipped=" + HbFaceInternals.SkippedChecks
            + " faceLoads=" + HbFaceCache.FaceLoads + " faceLoadFailures=" + HbFaceCache.FaceLoadFailures
            + " faceEvictions=" + HbFaceCache.Evictions
            + " " + HbFallbackDiag.SummaryFragment();

        private static int s_stderrLeft = 3;   // **无条件**打前 3 条（见下）

        /// <summary>
        /// 记一次"交回 LS"。
        ///
        /// ⚠️ **前 3 条无条件写 stderr，之后才受 `WPF_LINUX_TEXTLINE_DIAG=1` 门控** —— 理由（主控 2026-09-11）：
        ///   `SummaryLine()` 只在**进程正常退出**时落盘，而接线出问题时进程是 **abort** 的
        ///   ⇒ "诊断只在正常退出时输出" == "故障时没有诊断"。实测就吃过这个：D3 第一版
        ///   因为开关默认关，`bailNoSwitch` 那条原因**永远不显形**，只能靠读代码 + A/B 才找出来。
        ///   另外 T3 的 `run-wpftextdemo.sh` **会清空 `WPF_LINUX_TEXTLINE*`**（含 DIAG）
        ///   ⇒ 只受 DIAG 门控的话，在那个 runner 里照样看不见。故前几条必须无条件。
        /// </summary>
        private static void Bail(ref long counter, string why)
        {
            ++counter; ++Bailed; LastBail = why;
            bool force = s_stderrLeft-- > 0;
            if (force || s_diagLeft-- > 0)
                HbTextLineScaffold.Diag("LS_FALLBACK 交回 LS（"
                    + (force ? "前3条无条件" : "DIAG") + "）：" + why);
        }

        /// <summary>把一个 `TextCharacters` run 的字符取出来（`CharacterBuffer` 是 `IList&lt;char&gt;`）。</summary>
        private static string ExtractCharacters(TextCharacters run)
        {
            CharacterBufferReference cbr = run.CharacterBufferReference;
            MS.Internal.CharacterBuffer buf = cbr.CharacterBuffer;   // 类型在 MS.Internal（PC 内部可见）
            if (buf == null) return null;
            int off = cbr.OffsetToFirstChar;
            var chars = new char[run.Length];
            for (int i = 0; i < run.Length; ++i) chars[i] = buf[off + i];
            return new string(chars);
        }

        /// <summary>
        /// 从 cpFirst 开始收集一个段落（到 EOL/EOP 为止）。只接受 TextCharacters。
        ///
        /// R1/T1d：**每个 run 的 props 都留下**（今天只留第一个 ⇒ 后续 run 的字体信息直接丢）。
        /// 非 `TextCharacters` 仍然**原样 bail**（形状不变："不确定就交回"）。
        /// </summary>
        private static bool TryCollect(TextSource src, int cpFirst, out string text, out List<CollectedRun> runs,
                                       out TextRun eolRun)
        {
            text = null; runs = null;
            eolRun = null;
            var sb = new StringBuilder();
            var list = new List<CollectedRun>();
            int cp = cpFirst;
            for (int guard = 0; guard < 4096; ++guard)
            {
                TextRun run;
                try { run = src.GetTextRun(cp); }
                catch (Exception e) { Bail(ref BailException, "GetTextRun 抛 " + e.GetType().Name); return false; }
                if (run == null) { Bail(ref BailRunType, "GetTextRun 返回 null"); return false; }

                TextCharacters tc = run as TextCharacters;
                if (tc != null)
                {
                    string s = ExtractCharacters(tc);
                    if (s == null) { Bail(ref BailRunType, "CharacterBuffer 取不到"); return false; }
                    // ⭐ R1/T1d：这里是"不再丢后续 run 字体信息"的落点（今天只 `if (props == null)`）。
                    list.Add(new CollectedRun { Start = sb.Length, Length = tc.Length, Props = tc.Properties });
                    sb.Append(s);
                    cp += tc.Length;
                    if (sb.Length > MaxParagraphChars) { Bail(ref BailLong, "段落 > " + MaxParagraphChars + " 字符"); return false; }
                    continue;
                }
                if (run is TextEndOfLine)
                {
                    // ★T-A30：**只认"段末"那一种** EOL（`TextEndOfParagraph` 族 —— 宿主 PtsHost 造的是
                    //   `ParagraphBreakRun : TextEndOfParagraph`）。`LineBreakRun`（硬断）**不算段末** ⇒ 不取，
                    //   免把"段中间的软断"当成"排到段尾"（那会让驱动提前收束 = 假成功方向）。
                    eolRun = (run is TextEndOfParagraph) ? run : null;
                    break;                                                 // EOL / EOP（TextEndOfParagraph : TextEndOfLine）
                }
                Bail(ref BailRunType, "run 类型 " + run.GetType().Name + " 不支持");
                return false;
            }
            text = sb.ToString();
            if (text.Length == 0) { Bail(ref BailEmpty, "空段落"); return false; }
            if (list.Count == 0) { Bail(ref BailRunType, "没有 run properties"); return false; }
            runs = list;
            return true;
        }

        /// <summary>兼容重载：只要第一个 run 的 props（老调用姿势保持不变）。</summary>
        private static bool TryCollect(TextSource src, int cpFirst, out string text, out TextRunProperties props)
        {
            List<CollectedRun> runs;
            TextRun ignoredEol;
            if (!TryCollect(src, cpFirst, out text, out runs, out ignoredEol)) { props = null; return false; }
            props = runs[0].Props;
            return props != null;
        }

        /// <summary>
        /// 该行的字体文件路径从哪来：`Typeface → TryGetGlyphTypeface → GlyphTypeface.FontUri`（public）。
        /// 解析不到**文件**就直接交回 LS（HarfBuzz 需要文件路径）。
        /// </summary>
        private static bool TryResolveFont(TextRunProperties props, out GlyphTypeface glyphTypeface, out string fontPath)
        {
            glyphTypeface = null; fontPath = null;
            Typeface tf = props.Typeface;
            if (tf == null) { Bail(ref BailFont, "Typeface 为 null"); return false; }
            GlyphTypeface gt;
            if (!tf.TryGetGlyphTypeface(out gt) || gt == null) { Bail(ref BailFont, "Typeface 取不到 GlyphTypeface"); return false; }
            Uri u = gt.FontUri;
            if (u == null || !u.IsFile) { Bail(ref BailFont, "FontUri 不是本地文件（" + (u == null ? "null" : u.ToString()) + "）"); return false; }
            string path = u.LocalPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Bail(ref BailFont, "字体文件不存在：" + path); return false; }
            glyphTypeface = gt; fontPath = path; return true;
        }

        /// <summary>
        /// 尝试用托管路径排"从 <paramref name="cpFirst"/> 开始的那一行"。
        /// **返回 null ⇒ 调用方原样走 LS**（行为与接线前完全一致）。
        ///
        /// ── `#19`/W19A（`WAVE19-PREREGISTRATION.md` §0.1 B）：补上**严格档的 indent 缺口** ──
        /// 修前形态：本方法**根本没有缩进入参**，而它转发到的是 `FormatParagraph` 的那个
        /// **不收 indent** 的重载 ⇒ `Indent`/`ParagraphIndent` 在这条路径上被**整条丢掉**。
        /// 而这条路径正是 PC **默认配置下最先尝试**的那一层（生成物 `:565`，先于宽松兜底 `:581`）
        /// ⇒ `#17` 的 P2 只修好了"兜底那一层"，真正先接手的这一层仍是丢的
        /// （现场依据：`src/Linux/build/MilBridge/R17A-recon.md` §1.2 F1）。
        ///
        /// 语义（`#16` 由 oracle 逐字符定下，与宽松档、三支 tab 臂**同一口径**）：
        ///   `indentDip` = **网格锚点**（`startPenX`）；内容起点 = `Indent + ParagraphIndent`。
        ///
        /// ⚠️ **单位必须是原始 DIP** —— 形参名刻意用 `…Dip` 叫出来：PC 侧的
        ///   `settings.Pap.Indent` / `.ParagraphIndent` 是**理想整数 ×300**（`RealToIdeal`）
        ///   ⇒ 传它们会引入一个新的 300 倍错误（`W17B-report.md` §7.6 的近失）。调用点须传
        ///   `paragraphProperties.Indent` / `.ParagraphIndent`。
        ///
        /// ⚠️ **两个新形参必须有默认值 `= 0`**：既有调用点（`CoverageProbe`、
        ///   `PcLineOracle`、本文件内的其它调用）**零改动即逐位等价** —— 这是"射程外不动"的前提。
        /// </summary>
        internal static TextLine TryFormatLine(TextSource textSource, int cpFirst, double paragraphWidth,
                                              double pixelsPerDip, bool alwaysCollapsible, double lineHeight,
                                              double indentDip = 0, double paragraphIndentDip = 0)
        {
            ++Calls;
            if (!Enabled) { Bail(ref BailNoSwitch, "回退开关关（" + HbTextLineScaffold.EnableEnvVar + " 或 " + FallbackEnvVar + "=0）"); return null; }
            if (textSource == null) { Bail(ref BailRunType, "textSource 为 null"); return null; }
            try
            {
                ParaCache c = s_cache;
                if (c != null && ReferenceEquals(c.Source, textSource) && c.Contains(cpFirst))
                {
                    HbTextLine hit = c.LineAt(cpFirst);
                    if (hit != null) { ++Handled; return hit; }
                }

                string text; HbFontPlan plan; GlyphTypeface[] faces; RunFace primaryRun; TextRunProperties primaryProps;
                TextRunProperties[] runProps; double emSize; TextRun eolRun;
                if (!TryBuildPlan(textSource, cpFirst, out text, out plan, out faces, out primaryRun,
                                  out primaryProps, out emSize, out runProps, out eolRun))
                    return null;

                int consumed;
                List<HbTextLine> lines = HbTextLineFactory.FormatParagraph(
                    text, primaryRun.FontPath, emSize, paragraphWidth, primaryRun.Typeface, (float)pixelsPerDip,
                    primaryProps, alwaysCollapsible, false, lineHeight, out consumed,
                    plan, faces, runProps,
                    // ── `#19`/W19A B：**严格档**也把缩进透到工厂（修前这两个槽不存在 ⇒ 缩进全丢）──
                    //   与宽松档（生成物 `WpfLinuxLenientTextFallback`）**逐字同一对槽名**；
                    //   默认 0 ⇒ 既有调用点不传时**逐位等价**（本方法签名见上）。
                    indentDip: indentDip,
                    paragraphIndentDip: paragraphIndentDip,
                    // ── ★`#23` P2（`D-T6-b`）：**严格档**也把**段落原点**透到工厂 ──
                    //   `text` 是 `TryBuildPlan` 从 `cpFirst` 起收集出来的 ⇒ 行内 `range.Start` 是**相对**下标，
                    //   而帧（`GetTextBounds` 第一参数 / 真机 `startChar`）是**段落系绝对**下标
                    //   ⇒ 原点 = `cpFirst`。不传的话缓存未命中/原点≠0 时整段帧错（`#22` §3.4 的 `--prefix 40` 即此）。
                    paragraphOrigin: cpFirst,
                    // ── ★T-A30（`PRECOND-LINEMODEL-ELEMENT-SAFE-STARTS`）：**严格档**也把段末 EOL run 透到工厂 ──
                    //   `eolRun` = `TryCollect` 在段末取到的**源自己的**那个 run（宿主 PtsHost 给 `ParagraphBreakRun`）
                    //   ⇒ 段末行的末 span 用它 ⇒ `Line.FormattingResult` 报 `fsflrEndOfParagraph` ⇒ 驱动在段末收束。
                    eopRun: eolRun);
                s_cache = new ParaCache { Source = textSource, Start = cpFirst, Lines = lines };
                HbTextLine first = s_cache.LineAt(cpFirst);
                if (first == null) { Bail(ref BailException, "缓存里没有该行"); return null; }
                ++Handled;
                HbLineTrace.SiteC(cpFirst, lines);        // 只读插桩（缺省关，独立预算）
                // ---- B：即时诊断 + 首次接手即落盘（被 SIGTERM 也看得见；堵"空=歧义"）----
                //   独立开关 `WPF_LINUX_TEXTLINE_LINEDIAG`（**缺省关**）；只写 stderr/自己的 dump 文件，
                //   **绝不碰 RenderDiagnostics 或任何进 runner 判据的账**。
                if (Handled == 1)
                    HbTextLineScaffold.Dump(Environment.GetEnvironmentVariable(HbTextLineScaffold.DumpEnvVar));
                if (HbTextLineScaffold.ParseOnOff(Environment.GetEnvironmentVariable(LineDiagEnvVar)))
                {
                    // ⚠️ 这里**直写 Console.Error**，不走 `HbTextLineScaffold.Diag()` —— `Diag()` 受
                    //   `WPF_LINUX_TEXTLINE_DIAG` 门控，于是"只设 LINEDIAG=1"时**一行都不打**
                    //   （实测踩过：整轮没有"接手"行，只能靠 dump 里的计数器读出来）。
                    var sb2 = new StringBuilder();
                    sb2.Append("LS_FALLBACK 接手：段起点=").Append(cpFirst)
                       .Append(" 行数=").Append(lines.Count)
                       .Append(" LineHeight=").Append(lineHeight.ToString("F4"))
                       .Append(" 段落宽=").Append(paragraphWidth.ToString("F4"));
                    int pos = cpFirst;
                    for (int li = 0; li < lines.Count && li < 12; ++li)
                    {
                        HbTextLine L = lines[li];
                        sb2.Append("\n  [").Append(li).Append("] 起点=").Append(pos)
                           .Append(" Length=").Append(L.Length)
                           .Append(" Width=").Append(L.Width.ToString("F4"))
                           .Append(" Height=").Append(L.Height.ToString("F4"))
                           .Append(" Baseline=").Append(L.Baseline.ToString("F4"))
                           .Append(" TextHeight=").Append(L.TextHeight.ToString("F4"))
                           .Append(" Extent=").Append(L.Extent.ToString("F4"));
                        pos += L.Length;
                    }
                    sb2.Append("\n  计数器：").Append(SummaryFragment());
                    try { Console.Error.WriteLine("[TEXTLINE_LINEDIAG] " + sb2.ToString()); } catch { }
                }
                return first;
            }
            catch (Exception e)
            {
                Bail(ref BailException, "异常 " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// `FormatMinMaxParagraphWidth` 的托管等价物：
        /// 宽=∞ 排一遍取最长行 ⇒ MaxWidth；宽=0 排一遍（强制断，每行 1 字）取最宽行 ⇒ MinWidth。
        /// 返回 false ⇒ 调用方原样走 LS。
        /// </summary>
        internal static bool TryMinMaxParagraphWidth(TextSource textSource, double pixelsPerDip,
                                                     out double minWidth, out double maxWidth)
        {
            ++MinMaxCalls;
            minWidth = 0; maxWidth = 0;
            if (!Enabled) { Bail(ref BailNoSwitch, "回退开关关（minmax）"); ++MinMaxBailed; return false; }
            if (textSource == null) { ++MinMaxBailed; Bail(ref BailRunType, "textSource 为 null（minmax）"); return false; }
            try
            {
                string text; HbFontPlan plan; GlyphTypeface[] faces; RunFace primaryRun; TextRunProperties primaryProps;
                TextRunProperties[] runProps; double emSize; TextRun eolRun;
                if (!TryBuildPlan(textSource, 0, out text, out plan, out faces, out primaryRun,
                                  out primaryProps, out emSize, out runProps, out eolRun)) { ++MinMaxBailed; return false; }

                int c1, c2;
                List<HbTextLine> wide = HbTextLineFactory.FormatParagraph(text, primaryRun.FontPath, emSize,
                    double.MaxValue, primaryRun.Typeface, (float)pixelsPerDip, primaryProps, false, false, 0, out c1,
                    plan, faces);
                foreach (HbTextLine L in wide) if (L.Width > maxWidth) maxWidth = L.Width;
                List<HbTextLine> narrow = HbTextLineFactory.FormatParagraph(text, primaryRun.FontPath, emSize,
                    0.0, primaryRun.Typeface, (float)pixelsPerDip, primaryProps, false, false, 0, out c2,
                    plan, faces);
                foreach (HbTextLine L in narrow) if (L.Width > minWidth) minWidth = L.Width;
                ++MinMaxHandled;
                return true;
            }
            catch (Exception e)
            {
                ++MinMaxBailed; LastBail = "minmax 异常 " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>排好的整段（给接线后的诊断用）：返回这一段的行数与总长。</summary>
        internal static string CacheInfo()
        {
            ParaCache c = s_cache;
            if (c == null) return "cache=空";
            int total = 0; foreach (HbTextLine L in c.Lines) total += L.Length;
            return "cache=段起点" + c.Start + " 行数" + c.Lines.Count + " 总Length" + total;
        }
    }
#endif

    // ==================================================================================
    //  7. internal 类型的工厂 —— **"反射 → 直构"的唯一差别点**
    // ==================================================================================

    /// <summary>
    /// `TextBounds` / `TextRunBounds` / `TextCollapsedRange` / `TextLineBreak` 的构造都是 **internal**
    /// ⇒ 编进 PC 时可以直接 `new`，编在 PC 之外必须反射。两种模式**只有这里不同**，
    /// 因此"换方式后行为不变"可以用同一串文本喂两条分支、逐项比输出来证明。
    /// </summary>
    internal static class HbInternalsFactory
    {
#if TEXTLINE_SHIM_DIRECT
        // ---- 编进 PresentationCore 时的路径：直接构造（没有任何反射）----
        internal static TextRunBounds CreateRunBounds(Rect r, int cpFirst, int cpEnd, TextRun run)
            => new TextRunBounds(r, cpFirst, cpEnd, run);

        internal static TextBounds CreateTextBounds(Rect r, FlowDirection dir, IList<TextRunBounds> runs)
            => new TextBounds(r, dir, runs);

        internal static TextCollapsedRange CreateCollapsedRange(int cp, int length, double width)
            => new TextCollapsedRange(cp, length, width);

        /// <summary>
        /// `TextLineBreak(TextModifierScope currentScope, IntPtr breakRecord)`（`TextLineBreak.cs:26`）。
        /// **两个参数都是"空"**：`currentScope = null`（我们的行不带 modifier scope）、
        /// `breakRecord = IntPtr.Zero`（**不伪造** LS 断行记录）。
        /// 上游 ctor 对 `Zero` 会 `GC.SuppressFinalize`，且 `DisposeInternal` 里
        /// `if (_breakRecord != IntPtr.Zero)` 才调 `LoDisposeBreakRecord` ⇒ 零记录**永远不会**碰 LS。
        /// </summary>
        internal static TextLineBreak CreateLineBreak()
            => new TextLineBreak(null, IntPtr.Zero);

        internal static bool IsDirect => true;
#else
        // ---- PC 之外的对照路径：反射（**仅测试用**，不是落地形态）----
        private static readonly ConstructorInfo s_runBounds = typeof(TextBounds).Assembly
            .GetType("System.Windows.Media.TextFormatting.TextRunBounds", true)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                            new[] { typeof(Rect), typeof(int), typeof(int), typeof(TextRun) }, null);

        private static readonly ConstructorInfo s_textBounds = typeof(TextBounds)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                            new[] { typeof(Rect), typeof(FlowDirection), typeof(IList<TextRunBounds>) }, null);

        private static readonly ConstructorInfo s_collapsedRange = typeof(TextCollapsedRange)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                            new[] { typeof(int), typeof(int), typeof(double) }, null);

        private static readonly ConstructorInfo s_lineBreak = typeof(TextLineBreak)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                            new[] { typeof(TextLineBreak).GetField("_currentScope",
                                        BindingFlags.Instance | BindingFlags.NonPublic).FieldType,
                                    typeof(IntPtr) }, null);

        internal static TextRunBounds CreateRunBounds(Rect r, int cpFirst, int cpEnd, TextRun run)
            => (TextRunBounds)s_runBounds.Invoke(new object[] { r, cpFirst, cpEnd, run });

        internal static TextBounds CreateTextBounds(Rect r, FlowDirection dir, IList<TextRunBounds> runs)
            => (TextBounds)s_textBounds.Invoke(new object[] { r, dir, runs });

        internal static TextCollapsedRange CreateCollapsedRange(int cp, int length, double width)
            => (TextCollapsedRange)s_collapsedRange.Invoke(new object[] { cp, length, width });

        internal static TextLineBreak CreateLineBreak()
            => (TextLineBreak)s_lineBreak.Invoke(new object[] { null, IntPtr.Zero });

        internal static bool IsDirect => false;
#endif
    }

#endif  // !TEXTLINE_BREAK_ENGINE_ONLY
}
