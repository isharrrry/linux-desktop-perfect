// T2 · Phase 1 —— 字体面句柄表（DWriteFontFaceAddRef / DWriteFontAddRef 的 Linux 等价物）
// =====================================================================================
// 【为什么必须有这一层】
//   PresentationCore 会把这个 IntPtr 一路带到 MIL 的原生接口上：
//     GlyphTypeface.cs:1264
//       UnsafeNativeMethods.MilCoreApi.MilGlyphRun_GetGlyphOutline(
//           fontFaceDWrite.DWriteFontFaceAddRef,   // ← IntPtr，注释原文：
//           glyphIndex, sideways, renderingEmSize, ...);   //    "Released in this native code function"
//     GlyphRun.cs:1876
//       command.pIDWriteFont = (UInt64)_glyphTypeface.GetDWriteFontAddRef;
//
//   MIL 侧（M7a 已实现）拿到这个 IntPtr 后要反查 SKTypeface：
//     src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Glyph.cs:110
//       if (!MilFontFaceTable.TryResolve(pFontFace, out SKTypeface typeface)) return HResult.E_HANDLE;
//
//   所以「骨架发出去的令牌」与「MIL 能解析的令牌」**必须是同一套**。
//   本文件的设计是：
//     · Register 产生稳定令牌（同一个 face 反复 Register 得到同一个值，可重复 GetGlyphOutline）；
//     · 若宿主安装了 <see cref="TokenAllocator"/>（PresentationCore 的 ModuleInitializer
//       里一行 `FontHandleTable.TokenAllocator = MilFontFaceTable.Register;` 即可），
//       令牌就由 MIL 的句柄表发放 —— 于是 MIL 天然能解析，不需要任何额外注册；
//     · 未安装时用本地表，行为完全一样（只是 MIL 解析不了 —— 那属于"没接线"，
//       会明确返回 E_HANDLE，而不是画错字形）。
//
// 【为什么不用 IntPtr = SKTypeface.Handle】
//   Skia 的 native 句柄在 typeface 被释放后会被复用/悬空；而且它没有"这是谁"的信息。
//   递增的令牌 + 显式表，让"用了已释放的字体面"变成一个可检测的 false，而不是 UB。
//   （与 MilHandleTables.cs 的口径一致：不假装成功。）

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SkiaSharp;

namespace MS.Internal.Text.TextInterface.Linux
{
    /// <summary>字体面/字体对象的 IntPtr 令牌 → 托管对象映射。</summary>
    public static class FontHandleTable
    {
        private static readonly ConcurrentDictionary<IntPtr, Entry> _entries = new ConcurrentDictionary<IntPtr, Entry>();
        private static readonly ConcurrentDictionary<object, IntPtr> _byObject = new ConcurrentDictionary<object, IntPtr>(ReferenceEqualityComparer.Instance);
        private static long _next;

        /// <summary>
        /// 令牌分配钩子。PresentationCore 装 <c>MilFontFaceTable.Register</c> 进来之后，
        /// 骨架发出的令牌与 MIL 能解析的令牌就是同一个（见 WIRING.md §3）。
        /// 返回 IntPtr.Zero 表示"这个钩子不接管"，则回落到本地表。
        /// </summary>
        public static Func<SKTypeface, IntPtr> TokenAllocator { get; set; }

        /// <summary>
        /// **路径式**令牌分配钩子：<c>(字体文件路径, 面下标, 模拟标志) → 令牌</c>。
        ///
        /// 为什么除了 <see cref="TokenAllocator"/> 还要有它：
        ///   当 MIL 是**另一个运行时**（NativeAOT 共享库 wpfgfx_cor3.so）时，
        ///   跨边界唯一可靠的标识是**路径**（两个运行时各有一份 Skia 绑定，
        ///   传 SkTypeface* 需要假定底层 libSkiaSharp.so 是同一个实例 —— 不该赌）。
        ///   而且本钩子不需要引用 SkiaSharp 类型，消费方（PresentationCore）
        ///   不必为了装钩子而新增 SkiaSharp 依赖。
        ///
        /// 语义：返回 IntPtr.Zero 表示"本钩子不接管"，由 <see cref="TokenAllocator"/>
        /// 或进程内表接手（**明确降级**，不静默成功）。
        /// </summary>
        public static Func<string, int, int, IntPtr> PathTokenAllocator { get; set; }

        private sealed class Entry
        {
            public Entry(IntPtr token, object owner, SKTypeface typeface, LinuxFontFace face)
            {
                Token = token; Owner = owner; Typeface = typeface; Face = face;
            }

            public IntPtr Token { get; }
            public object Owner { get; }
            public SKTypeface Typeface { get; }
            public LinuxFontFace Face { get; }
            public int RefCount;
        }

        private sealed class ReferenceEqualityComparer : System.Collections.Generic.IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>为字体面登记令牌。同一个 face 反复调用返回同一个令牌（幂等）。</summary>
        public static IntPtr Register(LinuxFontFace face)
        {
            if (face == null) return IntPtr.Zero;

            if (_byObject.TryGetValue(face, out IntPtr existing) && _entries.ContainsKey(existing))
                return existing;

            IntPtr token = Allocate(face.Typeface, face.SourcePath, face.FaceIndex, face.SimulationFlags);
            var entry = new Entry(token, face, face.Typeface, face) { RefCount = 1 };
            _entries[token] = entry;
            _byObject[face] = token;
            return token;
        }

        /// <summary>为一个裸 SKTypeface 登记令牌（Font 对象没有 face 时用）。</summary>
        public static IntPtr Register(SKTypeface typeface)
        {
            if (typeface == null) return IntPtr.Zero;

            if (_byObject.TryGetValue(typeface, out IntPtr existing) && _entries.ContainsKey(existing))
                return existing;

            IntPtr token = Allocate(typeface);
            _entries[token] = new Entry(token, typeface, typeface, null) { RefCount = 1 };
            _byObject[typeface] = token;
            return token;
        }

        private static IntPtr Allocate(SKTypeface typeface, string sourcePath = null, int faceIndex = 0, int simulationFlags = 0)
        {
            // ① 路径式（跨运行时可用，见 PathTokenAllocator 的说明）
            Func<string, int, int, IntPtr> pathAllocator = PathTokenAllocator;
            if (pathAllocator != null && !string.IsNullOrEmpty(sourcePath))
            {
                IntPtr byPath = pathAllocator(sourcePath, faceIndex, simulationFlags);
                if (byPath != IntPtr.Zero) return byPath;
            }

            // ② 对象式（同运行时可用）
            Func<SKTypeface, IntPtr> allocator = TokenAllocator;
            if (allocator != null)
            {
                IntPtr external = allocator(typeface);
                if (external != IntPtr.Zero) return external;
            }

            // ③ 进程内表（降级：MIL 若在别的运行时里，反查不到 → E_HANDLE）
            return (IntPtr)System.Threading.Interlocked.Increment(ref _next);
        }

        /// <summary>令牌 → SKTypeface。未登记返回 false（**不回落默认字体**）。</summary>
        public static bool TryResolve(IntPtr token, out SKTypeface typeface)
        {
            typeface = null;
            if (token == IntPtr.Zero) return false;
            if (!_entries.TryGetValue(token, out Entry entry)) return false;

            typeface = entry.Typeface;
            return typeface != null;
        }

        /// <summary>令牌 → 字体面（MIL 侧的 GetGlyphOutline 也可以用它拿更多信息）。</summary>
        public static bool TryResolveFace(IntPtr token, out LinuxFontFace face)
        {
            face = null;
            if (token == IntPtr.Zero) return false;
            if (!_entries.TryGetValue(token, out Entry entry)) return false;

            face = entry.Face;
            return face != null;
        }

        /// <summary>AddRef 等价：令牌的引用计数 +1（骨架的 DWriteFontFaceAddRef 语义）。</summary>
        public static void AddRef(IntPtr token)
        {
            if (token == IntPtr.Zero) return;
            if (_entries.TryGetValue(token, out Entry entry))
                System.Threading.Interlocked.Increment(ref entry.RefCount);
        }

        /// <summary>撤销一个令牌。返回 false 表示"不是我们发的"（可检测，不是 UB）。</summary>
        public static bool Unregister(IntPtr token)
        {
            if (token == IntPtr.Zero) return false;
            if (!_entries.TryRemove(token, out Entry entry)) return false;

            _byObject.TryRemove(entry.Owner, out _);
            return true;
        }

        /// <summary>当前登记数（测试用来断言"没有泄漏"）。</summary>
        public static int Count => _entries.Count;

        /// <summary>
        /// 已登记令牌的快照（诊断与"令牌 → 来源路径"反查用；调用方不应缓存它）。
        /// 加它是因为 SkiaSharp 2.88 的 SKTypeface **不暴露来源路径**，
        /// 而跨运行时的令牌桥需要路径（见 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.FontBridge.cs 的契约）。
        /// </summary>
        public static IReadOnlyList<IntPtr> RegisteredTokens => new List<IntPtr>(_entries.Keys);

        public static void Reset()
        {
            _entries.Clear();
            _byObject.Clear();
            _files.Clear();
            _fileByObject.Clear();
            PathTokenAllocator = null;
            TokenAllocator = null;
            System.Threading.Interlocked.Exchange(ref _next, 0);
        }

        // =================================================================================
        //  文件对象的令牌（骨架的 FontFile / InternalFactory.CreateFontFile 用）
        // =================================================================================

        private static readonly ConcurrentDictionary<IntPtr, LinuxFontFile> _files =
            new ConcurrentDictionary<IntPtr, LinuxFontFile>();
        private static readonly ConcurrentDictionary<object, IntPtr> _fileByObject =
            new ConcurrentDictionary<object, IntPtr>(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// 为字体文件登记令牌（幂等：同一个文件对象反复登记返回同一个令牌）。
        /// 对应上游把 IDWriteFontFile* 从 CreateFontFileReference 一路带到
        /// IDWriteFactory::CreateFontFace 的那条链路（Factory.cs:162/212）。
        /// </summary>
        public static IntPtr RegisterFile(LinuxFontFile file)
        {
            if (file == null) return IntPtr.Zero;

            if (_fileByObject.TryGetValue(file, out IntPtr existing) && _files.ContainsKey(existing))
                return existing;

            IntPtr token = (IntPtr)System.Threading.Interlocked.Increment(ref _next);
            _files[token] = file;
            _fileByObject[file] = token;
            return token;
        }

        /// <summary>令牌 → 字体文件。未登记返回 false（**不猜、不造空文件**）。</summary>
        public static bool TryResolveFile(IntPtr token, out LinuxFontFile file)
        {
            file = null;
            if (token == IntPtr.Zero) return false;
            return _files.TryGetValue(token, out file) && file != null;
        }

        /// <summary>撤销文件令牌。返回 false 表示"不是我们发的"。</summary>
        public static bool UnregisterFile(IntPtr token)
        {
            if (token == IntPtr.Zero) return false;
            if (!_files.TryRemove(token, out LinuxFontFile file)) return false;

            _fileByObject.TryRemove(file, out _);
            return true;
        }

        /// <summary>当前登记的文件数。</summary>
        public static int FileCount => _files.Count;
    }
}
