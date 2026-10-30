// =====================================================================================
// 每路径**共享一份 `SKData`** + **逐面 `SKTypeface.FromData(data, faceIndex)`**
//   —— 替换 `SKTypeface.FromFile(path, faceIndex)`（T2，2026-09-15，主控窗口 9）
//
// 【为什么】（实测，不是推理）`SKTypeface.FromFile(path, i)` 对**每个面**各 mmap **整份文件**：
//   · 峰值 `maps` 段数：`NotoSansCJK-Bold.ttc` **20 段**、`Regular` **11 段**、`Black`/`DemiLight` 各 **10 段** …
//   · 系统档合计**字体 Σ虚拟 2,215 MB** ↔ `smaps_rollup.shared_clean` 1,709 MB；1CJK 档 `Regular.ttc` 13 段/241.6 MB
//   ⇒ 这就是 `D-F1c` 那 1.67 GB 的来源；M7b 在 `src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/**` 的修法**不在这条链上**
//     （峰值件里 `WpfGfx.Linux.dll` 段数 = 0 ⇒ 本进程没加载 MIL 渲染侧）。
//
// 【口径】`FromData` 与 `FromFile` 拿到的是**同一张面**（M7b 的 B1/B3 微实验：10 面 10 段 → 1 段，且
//   `U+0041/U+4E00/U+3042/U+30A2/U+1F600` 的 glyph id 逐位相同）；本类只改**映射份数**，不改面号语义。
//
// 【有界】`MaxTotalBytes` = 64 MB；超界按 **LRU 淘汰**（`Evictions` 计数）；键 = **规范路径**（解符号链接）。
// 【诊断】`WPF_LINUX_PROVIDER_DIAG=1` 时打 `[SKIADATA] files=… bytes=… hits=… misses=… evictions=…`（首调 + 每 20 次 miss）。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux      // 与 Provider 的两个调用点同命名空间（实测：写成别的名字会 CS0103）
{
    internal static class SkiaFontDataCache
    {
        private const long MaxTotalBytes = 64L * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly LinkedList<string> Lru = new LinkedList<string>();
        private static long _bytes;
        private static long _hits, _misses, _evictions;

        private sealed class Entry
        {
            internal SKData Data;
            internal LinkedListNode<string> Node;
            internal long Bytes;
        }

        /// <summary>规范键：绝对路径 + 解符号链接（同一个文件的不同写法必须命中同一条）。</summary>
        internal static string CanonicalKey(string path)
        {
            string full = Path.GetFullPath(path);
            try
            {
                FileSystemInfo fi = File.ResolveLinkTarget(full, returnFinalTarget: true);
                if (fi != null) full = fi.FullName;
            }
            catch (Exception) { /* 解不了就用 GetFullPath 的结果（不因此失败） */ }
            return full;
        }

        /// <summary>取该路径**唯一一份** `SKData`（缓存持有；调用方不得 Dispose）。</summary>
        internal static SKData Get(string path)
        {
            string key = CanonicalKey(path);
            lock (Gate)
            {
                if (Map.TryGetValue(key, out Entry hit))
                {
                    ++_hits;
                    Lru.Remove(hit.Node); Lru.AddLast(hit.Node);
                    Diag(key, "hit");
                    return hit.Data;
                }
                ++_misses;
                SKData data = SKData.Create(key);
                if (data == null) return null;
                long size = (long)data.Size;
                while (_bytes + size > MaxTotalBytes && Lru.Count > 0)
                {
                    LinkedListNode<string> oldest = Lru.First;
                    Lru.RemoveFirst();
                    if (Map.TryGetValue(oldest.Value, out Entry ev))
                    {
                        Map.Remove(oldest.Value);
                        _bytes -= ev.Bytes;
                        try { ev.Data.Dispose(); } catch (Exception) { }
                        ++_evictions;
                    }
                }
                var node = Lru.AddLast(key);
                Map[key] = new Entry { Data = data, Node = node, Bytes = size };
                _bytes += size;
                Diag(key, "miss");
                return data;
            }
        }

        /// <summary>逐面打开：与 `SKTypeface.FromFile(path, faceIndex)` 同语义，但**只映射一份**。</summary>
        internal static SKTypeface OpenFace(string path, int faceIndex)
        {
            SKData data = Get(path);
            return data == null ? null : SKTypeface.FromData(data, faceIndex);
        }

        internal static int CachedFileCount { get { lock (Gate) return Map.Count; } }
        internal static long CachedBytes { get { lock (Gate) return _bytes; } }
        internal static long Hits { get { lock (Gate) return _hits; } }
        internal static long Misses { get { lock (Gate) return _misses; } }
        internal static long Evictions { get { lock (Gate) return _evictions; } }

        private static void Diag(string key, string what)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("WPF_LINUX_PROVIDER_DIAG"), "1", StringComparison.Ordinal)) return;
            if (_misses == 1 || (_misses % 20) == 0)
                Console.Error.WriteLine($"[SKIADATA] files={Map.Count} bytes={_bytes} hits={_hits} misses={_misses} evictions={_evictions} last={what}:{Path.GetFileName(key)}");
        }
    }
}
