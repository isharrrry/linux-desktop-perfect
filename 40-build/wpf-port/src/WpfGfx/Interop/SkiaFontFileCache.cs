// [W8 / D-F1c 内存半边] Skia 字体面的**按路径共享后备**缓存。
//
// ── 病灶（实测，别重新推导）────────────────────────────────────────────────
//   `SKTypeface.FromFile(path, faceIndex)` **每建一个面就 mmap 一整份文件**：
//   对 10 面的 `.ttc`（18.6 MB）实测 **段数 = 10、Σ虚拟 = 185.9 MB = 10 × 18.586**
//   （`/proc/self/smaps` 里 10 段、全 `r--p`、`off=0`、每段 size=18.586 MB），
//   而 **RSS 只涨 9.6 MB**（mmap 后只有被触碰的页计入 RSS）——所以"用 RSS 看"会漏掉这个缺陷。
//   生产档 T2 的读数：`NotoSansCJK-Regular.ttc` **12 段、Σ虚拟 223.0 MB**。
//
// ── 修法（实测，10 面 vs 10 面）──────────────────────────────────────────────
//   **每个规范路径一份 `SKData`**（`SKData.Create(path)` ⇒ 一份整文件只读映射、全进程共享）
//   **+ 逐面 `SKTypeface.FromData(data, faceIndex)`**：
//       段数 **10 → 1**、Σ虚拟 **185.9 → 18.6 MB**、ΣRss 9.6 → 2.9 MB（10 面全部存活时）。
//   面数归一化：`FromFile` 是 **O(面数)**（1 面 1 段 / 10 面 10 段）；本缓存是 **O(1)**（1 面 1 段 / 10 面仍 1 段）。
//
// ── 为什么这**不会改面身份**（判据是"同一 (path,faceIndex) 拿到同一张 face"）──────
//   · `SKTypeface.FromData(data, index)` 与 `FromFile(path, index)` 取的都是**同一段字节**里的**同一个 face 索引**；
//     差别只在"谁持有那份字节"（一次共享 mmap vs 每次各 mmap 一份）。
//   · **实测证据**：对 10 面的 ttc 逐面取指纹（`FamilyName`＋字重/宽/斜/等宽＋
//     `U+0041/U+4E00/U+3042/U+30A2/U+1F600` 的 glyph id），`FromFile` 与"共享 data + `FromData`"
//     **10/10 逐位相同**（面孔序实测为 JP/KR/SC/TC/HK 与 Mono JP/KR/SC/TC/HK）。
//   · 调用方（`MilFontFaceTable.RegisterFromFile`）的**同 key 幂等**逻辑一字未动：
//     `(path, faceIndex, simFlags)` 相同仍复用同一句柄。
//
// ── 生命周期与上界（**别把泄漏换成"永不释放的缓存"**）────────────────────────
//   · `SKTypeface` 的持有者仍是 `MilFontFaceTable._faces`（登记后面用完进程生命周期，今天就是这样）；
//     本缓存**只多持有 `SKData`**（每路径一份）。
//   · **失效入口**：`Reset()`（测试/字体目录切换用）会释放并清空全部缓存；
//     `TrimCollected()` 是**软上界**：只淘汰"该文件的面**都已回收**（弱引用全死）"的条目
//     —— 绝不淘汰仍被面引用的后备（那条路会改面身份）。
//   · 实测（微实验 B4）：把托管引用全丢 + `GC.Collect()`×2 之后，那份映射**仍在**
//     ⇒ Skia 侧对 typeface 有自己的持有。**故本缓存不假装"能靠 GC 自动缩小"**：
//     它靠 `Reset()`/`TrimCollected()` 显式管理，并把 `LiveFileDataCount`/`LiveFileDataBytes` **暴露出来**（见下）。
//   · `MilFontFaceDiagnostics` 的两个只读计数（创建/复用/存活）可用于归因：
//     若 `LiveFileDataCount` 随建面次数线性增长 ⇒ **缓存键没命中**（多半是符号链接/路径形态不一致）。
//
// 【本文件只读缓存、不改渲染】：段数/Σ虚拟是唯一该动的量；字形、advance、行宽、CR、golden **必须一字不变**。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace WpfGfx.Linux.Interop
{
    internal static class SkiaFontFileCache
    {
        private sealed class Entry
        {
            public SKData Data;
            /// <summary>从这个后备建出来的面（弱引用，仅用于"该文件还有没有活面"的判定）。</summary>
            public readonly List<WeakReference<SKTypeface>> Faces = new List<WeakReference<SKTypeface>>();
        }

        /// <summary>软上界：只在"超过它且有条目的面全已回收"时才淘汰（默认 64 个文件，正常远用不到）。</summary>
        private const int MaxCachedFiles = 64;

        /// <summary>
        /// **按字节预算**（默认 64 MB）。触发时执行 `TrimCollectedLocked()`（只淘汰"面全已回收"的条目）。
        ///
        /// 【为什么预算不能淘汰"还有活面"的条目 —— 这是**实测**不是推理】
        ///   微实验 D：`SKData.Create` → `FromData` 建 10 面 → **把我们的 `SKData` Dispose 掉** ⇒ 10 面的逐面指纹**全部不变**，
        ///     且该文件的映射**仍在** ⇒ Skia 的 `MakeFromData` **自己引用了那份 `SKData`**（引用计数）。
        ///   微实验 E：经本缓存建 10 面 → `Reset()`（清空缓存）⇒ 指纹**全部不变**，映射仍在。
        ///   ⇒ 结论：**活面持有的映射不由我们决定**，"淘汰活面条目"既**释放不了内存**，又会让后续建面**重新映射**（更糟）。
        ///   故本预算只淘汰"面已回收"的条目；活面条目造成的占用由**注册策略**决定（系统档实测见文件头）。
        /// </summary>
        private const long MaxCachedBytes = 64L * 1024 * 1024;

        private static readonly Dictionary<string, Lazy<Entry>> s_byPath =
            new Dictionary<string, Lazy<Entry>>(StringComparer.Ordinal);
        private static readonly object s_gate = new object();

        /// <summary>缓存里当前有几个文件的后备。</summary>
        public static int CachedFileCount { get { lock (s_gate) return s_byPath.Count; } }

        /// <summary>这些后备一共多少字节（≈ 字体文件总字节）。</summary>
        public static long CachedBytes
        {
            get { lock (s_gate) return CachedBytesLocked(); }
        }

        private static long CachedBytesLocked()
        {
            long n = 0;
            foreach (Lazy<Entry> l in s_byPath.Values)
                if (l.IsValueCreated && l.Value?.Data != null) n += (long)l.Value.Data.Size;
            return n;
        }

        /// <summary>仍然活着的 `SKTypeface` 数（弱引用口径；GC 之前偏大，仅作诊断）。</summary>
        public static int LiveFaceCount
        {
            get
            {
                int alive = 0;
                lock (s_gate)
                    foreach (Lazy<Entry> l in s_byPath.Values)
                    {
                        if (!l.IsValueCreated) continue;
                        foreach (WeakReference<SKTypeface> w in l.Value.Faces)
                            if (w.TryGetTarget(out _)) alive++;
                    }
                return alive;
            }
        }

        /// <summary>
        /// 规范键：**必须解符号链接**。1CJK 档的字体目录里那份 `.ttc` 就是指向 `/usr/share/...` 的**符号链接**，
        /// 不解析就会出现"同一份物理文件两份后备"（修法在真应用里直接打折）。
        /// </summary>
        public static string CanonicalKey(string path)
        {
            try
            {
                FileSystemInfo target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                if (target != null) path = target.FullName;
            }
            catch { /* 不是符号链接 / 权限不足：退回原路径 */ }
            return Path.GetFullPath(path);
        }

        /// <summary>
        /// 取/建该路径的共享后备，再从中建第 <paramref name="faceIndex"/> 面。
        /// 失败（后备建不出、或 index 越界/文件不是字体）返回 <c>null</c> —— 与今天 `FromFile` 的"返回 null"同义，
        /// 由调用方映射成 `MilFontFaceDiagnostics` 的失败码。
        /// </summary>
        public static SKTypeface CreateFace(string path, int faceIndex)
        {
            string key = CanonicalKey(path);

            Lazy<Entry> lazy;
            bool createdHere = false;
            lock (s_gate)
            {
                if (!s_byPath.TryGetValue(key, out lazy))
                {
                    // ⚠️ 用 `Lazy` 而不是 `GetOrAdd(key, factory)`：后者在并发下**可能把工厂调多次**
                    //    ⇒ 会多建几份整文件映射再丢弃（正是本项要消灭的东西）。
                    lazy = new Lazy<Entry>(() => new Entry { Data = SKData.Create(key) },
                                           LazyThreadSafetyMode.ExecutionAndPublication);
                    s_byPath[key] = lazy;
                    createdHere = true;
                }
            }

            Entry entry;
            try { entry = lazy.Value; }
            catch (Exception) { lock (s_gate) { if (createdHere) s_byPath.Remove(key); } throw; }
            if (entry?.Data == null || entry.Data.Size <= 0)
            {
                lock (s_gate) { if (createdHere) s_byPath.Remove(key); }
                return null;
            }

            SKTypeface face = SKTypeface.FromData(entry.Data, faceIndex);
            if (face == null) return null;

            lock (s_gate)
            {
                entry.Faces.Add(new WeakReference<SKTypeface>(face));
                long bytes = CachedBytesLocked();
                if (s_byPath.Count > MaxCachedFiles || bytes > MaxCachedBytes) TrimCollectedLocked();
                bytes = CachedBytesLocked();
                if (bytes > MaxCachedBytes) MilFontFaceDiagnostics.NoteFileDataOverBudget(s_byPath.Count, bytes);
            }

            if (createdHere) MilFontFaceDiagnostics.NoteFileDataCreated(key, (long)entry.Data.Size);
            else MilFontFaceDiagnostics.NoteFileDataReused(key);
            MilFontFaceDiagnostics.NoteTypefaceCreated(key, faceIndex);
            return face;
        }

        /// <summary>显式失效：释放并清空全部后备（测试、字体目录切换时用）。</summary>
        public static void Reset()
        {
            lock (s_gate)
            {
                foreach (Lazy<Entry> l in s_byPath.Values)
                    if (l.IsValueCreated) l.Value?.Data?.Dispose();
                s_byPath.Clear();
            }
            MilFontFaceDiagnostics.NoteFileDataReset();
        }

        /// <summary>软上界：只淘汰"该文件的面**全部已回收**"的条目（调用者须持锁）。</summary>
        private static void TrimCollectedLocked()
        {
            var drop = new List<string>();
            foreach (KeyValuePair<string, Lazy<Entry>> kv in s_byPath)
            {
                if (!kv.Value.IsValueCreated) { drop.Add(kv.Key); continue; }
                bool anyAlive = false;
                foreach (WeakReference<SKTypeface> w in kv.Value.Value.Faces)
                    if (w.TryGetTarget(out _)) { anyAlive = true; break; }
                if (!anyAlive) drop.Add(kv.Key);
            }
            foreach (string k in drop)
            {
                if (s_byPath.TryGetValue(k, out Lazy<Entry> l) && l.IsValueCreated) l.Value?.Data?.Dispose();
                s_byPath.Remove(k);
            }
            if (drop.Count > 0) MilFontFaceDiagnostics.NoteFileDataTrimmed(drop.Count);
        }
    }
}
