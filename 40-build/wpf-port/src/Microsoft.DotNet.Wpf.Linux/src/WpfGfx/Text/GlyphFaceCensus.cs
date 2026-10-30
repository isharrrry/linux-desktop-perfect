// 字形 census（#24/(乙) 归因用）：**缺省关、独立成类、只读**。
//
// 【它回答的问题（主控转 T2b 的判据）】
//   T2b 的普查量到了 **PC 侧给了几个面**（它看到两个 `pid`：0x20000003 七个 run、0x20000004 四十一个 run），
//   但**量不到"渲染器实际用了几个面"** —— 因为 `pid` 是上游不透明 id，我们的资源表里没有 font/typeface 类型。
//   那一环在 `Interop/**` / `Text/**`（本车道）。本类就是补那一环的**只读**读数。
//
// 【纪律（都是踩过坑换来的）】
//   · **缺省关**：只有 `WPF_LINUX_GLYPH_CENSUS=1` 才记录（默认零开销、零输出）。
//   · **不替换/不包装渲染器解析器**：只在绘制路径**旁**读一眼已经解码好的 `GlyphIndices`
//     和"本次实际拿到的那份面"（T2b 的 `GlyphRunCensus` 就是这个形态）。
//   · **不碰 `RenderDiagnostics`**：runner 的通过判据是 `未画种类 0`，任何东西记进那本账都会污染判据。
//     这里直接打 stderr，`WPF_LINUX_GLYPH_CENSUS` 不开时一行都不打。
//   · **不改任何行为**：所有调用点都是"读 + 计数"，返回值一律丢弃。

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;

namespace WpfGfx.Linux.Text
{
    /// <summary>本帧渲染器**实际用了哪些面、每个面覆盖了多少 run**（只读诊断）。</summary>
    internal static class GlyphFaceCensus
    {
        /// <summary>
        /// 开关：**`WPF_LINUX_GLYPH_FACE_CENSUS=1`**。
        ///
        /// 【为什么改名（2026-09 主控点名）】原先叫 `WPF_LINUX_GLYPH_CENSUS`，与 **T2b 的
        /// `DrawingCensus`/`GlyphRunCensus` 撞名** ⇒ 两个仪器会**天然同时开**，
        /// 而 T2b 的闸门 B 要求"关/开输出**逐字相同**" ⇒ 多一份输出就破坏了那条语义。
        /// **旧名 `WPF_LINUX_GLYPH_CENSUS` 已不再被本类读取**（下一个人按旧名找会找不到，故写在这里）。
        /// </summary>
        public const string EnableEnvVar = "WPF_LINUX_GLYPH_FACE_CENSUS";

        /// <summary>开关值。</summary>
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable(EnableEnvVar) == "1";

        private sealed class FaceEntry
        {
            public string Family;
            public string File;
            public int FaceIndex = -1;

            /// <summary>该面**是否覆盖 U+4E2D（中）**（三态：null = 没测）。</summary>
            public bool? CoversCjkSample;
            public long Runs;
            public long Glyphs;
            public long ZeroIds;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<IntPtr, FaceEntry> Faces = new Dictionary<IntPtr, FaceEntry>();
        private static long _runs;
        private static long _glyphs;
        private static long _zeroIds;
        private static long _reported;
        private static long _unresolvedRuns;
        private static long _faceHitByHandle;
        private static long _faceFallbackToFamily;
        private static long _faceHandleUnresolved;
        private static string _unresolvedFamily;

        /// <summary>
        /// 记一次绘制：这份面（<paramref name="face"/>）画了这个 run（<paramref name="ids"/>）。
        /// **纯读**：只看 id、不做任何替换/回退。
        /// </summary>
        public static void NoteRun(SKTypeface face, ushort[] ids)
        {
            if (!Enabled || face == null) return;

            ushort[] glyphs = ids ?? Array.Empty<ushort>();
            long zeros = 0;
            foreach (ushort id in glyphs) if (id == 0) zeros++;

            lock (Gate)
            {
                _runs++;
                _glyphs += glyphs.Length;
                _zeroIds += zeros;

                if (!Faces.TryGetValue(face.Handle, out FaceEntry entry))
                {
                    entry = new FaceEntry { Family = face.FamilyName };
                    Faces[face.Handle] = entry;
                }
                entry.Runs++;
                entry.Glyphs += glyphs.Length;
                entry.ZeroIds += zeros;
            }
        }

        private static readonly HashSet<string> SeenEntries = new HashSet<string>();

        /// <summary>
        /// 入口记号：**"这个入口在真应用里到底有没有被调到"**。
        /// 【为什么要它】判定性检查显示 `TextRenderer` 在真应用里 run 数 = 0（连 `Draw` 都是 0），
        ///   于是必须回答"**真应用的字形到底是谁画的**"。在每个已知入口打一次记号最简单也最不含糊：
        ///   谁响了就是谁在画；都不响 ⇒ 在渲染层的别处（不是本车道）。
        /// 每个入口只打一次（不刷屏），缺省关。
        /// </summary>
        public static void NoteEntry(string name)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                if (!SeenEntries.Add(name)) return;
            }
            Console.Error.WriteLine($"[glyph-census] 入口命中：{name}");
            Console.Error.Flush();
        }

        /// <summary>按句柄解析成功（债务 #14 的目标态）。</summary>
        public static void NoteFaceHitByHandle() { if (Enabled) lock (Gate) _faceHitByHandle++; }

        /// <summary>**没有句柄**（`PIDWriteFont == 0`，PC 还没接线）⇒ 回落族名。</summary>
        public static void NoteFaceFallbackToFamily() { if (Enabled) lock (Gate) _faceFallbackToFamily++; }

        /// <summary>**有句柄但解析失败**（注册与 run 不同步）—— 与上一条是两种故障。</summary>
        public static void NoteFaceHandleUnresolved() { if (Enabled) lock (Gate) _faceHandleUnresolved++; }

        /// <summary>
        /// 记一次"**解析不到面**"（`TryGetFont` 失败）。**这条必须存在**：
        /// 否则探针恰好在它最该报的场景（面解析出问题）下变成哑巴 —— 那正是上一版的缺陷。
        /// </summary>
        public static void NoteUnresolved(GlyphRunRequest request)
        {
            if (!Enabled) return;
            lock (Gate)
            {
                _unresolvedRuns++;
                _unresolvedFamily = request?.Font.FamilyName ?? "<unknown>";
            }
        }

        /// <summary>
        /// 这份面**覆盖不覆盖 U+4E2D（中）**。用途：一眼判"48 个 run 是不是用**拉丁面**画的 CJK"
        /// —— 这正是 (乙) 要回答的那件事，而族名（`DejaVu Sans`）看不出这一点。
        /// **只读**：用 `SKTypeface.GetGlyphs("中")` 映射一次字形 id，`!= 0` 即覆盖。
        /// </summary>
        public static void NoteFaceCoverage(SKTypeface face, string sample = "中")
        {
            if (!Enabled || face == null) return;
            bool covers;
            try
            {
                ushort[] glyphs = face.GetGlyphs(sample);
                covers = glyphs != null && glyphs.Length > 0 && glyphs[0] != 0;
            }
            catch { return; }

            lock (Gate)
            {
                if (!Faces.TryGetValue(face.Handle, out FaceEntry entry))
                {
                    entry = new FaceEntry { Family = face.FamilyName };
                    Faces[face.Handle] = entry;
                }
                entry.CoversCjkSample = covers;
            }
        }

        /// <summary>把"这份面来自哪个文件 / 第几个 face"补上（由 `FontSet` 加载时、以及 `MilFontFace_RegisterFromFile` 成功时告知）。</summary>
        public static void NoteFaceSource(SKTypeface face, string file, int faceIndex)
        {
            if (!Enabled || face == null) return;
            lock (Gate)
            {
                if (!Faces.TryGetValue(face.Handle, out FaceEntry entry))
                {
                    entry = new FaceEntry { Family = face.FamilyName };
                    Faces[face.Handle] = entry;
                }
                entry.File = file;
                entry.FaceIndex = faceIndex;
            }
        }

        /// <summary>
        /// 打一次汇总。**触发时机很要紧**：必须在"**真的画过 run 之后**"才报。
        ///
        /// 【为什么（本轮的自我纠正）】上一版是"进 `RenderChannel` 就报、且只报一次" ⇒
        ///   它**永远在第一帧**（那一帧一条绘制都没有）打印 ⇒ 报出 `共 0 个 run`。
        ///   我据此得出的"`TextRenderer` 不是真路径"**是错的**：入口记号显示
        ///   `DrawResource / Draw / GlyphRunPainter.Draw` **全都命中了**（见报告 §4.35）。
        ///   ⇒ **观测窗口选错了，不是被观测的路径不存在**（同族缺陷：空帧下的 0、探针被门控）。
        /// 现在的规则：**至少画过一个 run 才报**（`_runs > 0`）；报过就不再报。
        /// </summary>
        public static void Report(string context)
        {
            if (!Enabled) return;
            lock (Gate) { if (_runs <= 0) return; }                       // ← 没画过就不报（关键修正）
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            var sb = new StringBuilder();
            lock (Gate)
            {
                sb.Append($"[glyph-census] {context}：渲染器实际用了 **{Faces.Count}** 份面；");
                sb.Append($"共 {_runs} 个 run / {_glyphs} 个字形，其中 id==0（.notdef）{_zeroIds} 个；" +
                          $"**解析不到面的 run = {_unresolvedRuns}**（最近一次请求的族={_unresolvedFamily ?? "<无>"}）。\n" +
                          $"[glyph-census] 面来源（债务 #14）：**按句柄命中 = {_faceHitByHandle}** / " +
                          $"**回落族名（无句柄）= {_faceFallbackToFamily}** / " +
                          $"**有句柄但解析失败 = {_faceHandleUnresolved}**");
                foreach (KeyValuePair<IntPtr, FaceEntry> kv in Faces)
                {
                    FaceEntry e = kv.Value;
                    string cjk = e.CoversCjkSample == null ? "<未测>" : (e.CoversCjkSample.Value ? "是" : "否");
                    sb.Append($"\n[glyph-census]   面 0x{kv.Key.ToInt64():x} family={e.Family} " +
                              $"file={(e.File ?? "<未知>")} faceIndex={e.FaceIndex} 覆盖U+4E2D={cjk} " +
                              $"runs={e.Runs} glyphs={e.Glyphs} zeroIds={e.ZeroIds}");
                }
            }
            Console.Error.WriteLine(sb.ToString());
            Console.Error.Flush();
        }

        /// <summary>测试用：清零。</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Faces.Clear();
                _runs = _glyphs = _zeroIds = 0;
                _unresolvedRuns = 0;
                _faceHitByHandle = _faceFallbackToFamily = _faceHandleUnresolved = 0;
                _unresolvedFamily = null;
                SeenEntries.Clear();
                System.Threading.Interlocked.Exchange(ref _reported, 0);
            }
        }
    }
}
