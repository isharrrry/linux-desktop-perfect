// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 B：MilGlyphRun_* / MilGlyphCache_* 导出（6 个）。
//
// 【语义落点】Text/ 现有能力面 + 本文件里的 MIL 路径数据序列化：
//   · 字体身份   → MilFontFaceTable（DWrite 字体面指针 → SKTypeface 的显式映射；
//                  未登记时用 DefaultTypeface，与 Text/MilGlyphRunAdapter 的口径一致）
//   · 字形轮廓   → SKFont.GetGlyphPath（Skia 的字体轮廓，TrueType 出二次曲线、
//                  CFF 出三次曲线；统一升成三次贝塞尔后序列化）
//   · 序列化     → Interop/MilGeometryEngine.SerializePath，输出**上游 MIL_PATHGEOMETRY
//                  字节布局**，可以再被 Rendering/PathGeometryParser 解析回来
//                  （测试里就是这么断言往返一致的）
//
// 【内存所有权】MilGlyphRun_GetGlyphOutline 用 Marshal.AllocHGlobal 分配，
//   调用方必须用 MilGlyphRun_ReleasePathGeometryData 归还——与上游一致
//   （PresentationCore/.../GlyphTypeface.cs:1263/1283 正是这样的配对）。
//   本文件额外记录未释放的分配，重复释放返回 E_INVALIDARG 而不是让进程崩掉。
//
// 【*_AtRenderTime 三件套】上游第一个参数是**原生从端对象的裸指针**。
//   Linux 侧没有原生从端对象，改用 MilRenderTimeTargetTable 下发的句柄，
//   命令按 Begin → Append* → End 累积在目标上（状态机与 MilChannel 的批次机同构），
//   由上层渲染线程取走执行。这是"身份映射 + 真状态机"，不是空壳。

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// MilGlyphRun_GetGlyphOutline 分配出来的 MIL_PATHGEOMETRY 块登记表。
    /// 记录每一块的指针与长度，使 <see cref="MilNative.MilGlyphRun_ReleasePathGeometryData"/>
    /// 能识别"不是我们发的指针"与"重复释放"，而不是把它们变成未定义行为。
    /// </summary>
    public static class MilPathGeometryDataTable
    {
        private static readonly ConcurrentDictionary<IntPtr, int> _allocations =
            new ConcurrentDictionary<IntPtr, int>();

        /// <summary>分配一块非托管内存并拷入数据，返回首地址；数据为空返回 IntPtr.Zero。</summary>
        public static IntPtr Allocate(byte[] data)
        {
            if (data == null || data.Length == 0) return IntPtr.Zero;

            IntPtr pointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pointer, data.Length);
            _allocations[pointer] = data.Length;
            return pointer;
        }

        /// <summary>取该指针的块长度；不是本表发出去的返回 -1。</summary>
        public static int SizeOf(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return -1;
            return _allocations.TryGetValue(pointer, out int size) ? size : -1;
        }

        /// <summary>释放。返回 false 表示指针无效或已被释放过。</summary>
        public static bool Release(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return false;
            if (!_allocations.TryRemove(pointer, out _)) return false;

            Marshal.FreeHGlobal(pointer);
            return true;
        }

        public static int OutstandingCount => _allocations.Count;

        public static void Reset()
        {
            foreach (IntPtr key in new System.Collections.Generic.List<IntPtr>(_allocations.Keys))
            {
                if (_allocations.TryRemove(key, out _)) Marshal.FreeHGlobal(key);
            }
        }
    }

    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  字形轮廓
        // ==================================================================

        /// <summary>
        /// 取单个字形的轮廓，输出上游 MIL_PATHGEOMETRY 字节块。
        ///
        /// pFontFace 是"字体面指针"：Linux 上由 MilFontFaceTable 显式映射到 SKTypeface；
        /// 未登记时退回 MilFontFaceTable.DefaultTypeface；两者都没有 → E_HANDLE
        /// （不猜字体，理由见 Text/MilGlyphRunAdapter.cs 的注释）。
        ///
        /// sideways=true 时轮廓绕原点顺时针旋转 90°（竖排字形）。
        /// 输出填充规则恒为 Nonzero（TrueType/CFF 轮廓的填充约定）。
        /// </summary>
        public static int MilGlyphRun_GetGlyphOutline(
            IntPtr pFontFace,
            ushort glyphIndex,
            bool sideways,
            double renderingEmSize,
            out byte* pPathGeometryData,
            out uint pSize,
            out MilFillRule pFillRule)
        {
            pPathGeometryData = null;
            pSize = 0;
            pFillRule = MilFillRule.Nonzero;

            if (renderingEmSize <= 0) return HResult.E_INVALIDARG;
            if (!MilFontFaceTable.TryResolve(pFontFace, out SKTypeface typeface) || typeface == null)
                return HResult.E_HANDLE;

            using var font = new SKFont(typeface, (float)renderingEmSize);

            // 字体模拟（DWRITE_FONT_SIMULATIONS）：登记时记在字体面上，取轮廓时施加。
            // 未登记模拟（simFlags == 0，含所有既有登记路径）时两个开关都不动 ——
            // 既有行为逐位不变。
            if (MilFontFaceTable.TryGetSimFlags(pFontFace, out int simFlags) && simFlags != 0)
            {
                if ((simFlags & MilFontFaceTable.SimulationBold) != 0) font.Embolden = true;
                // -0.25 ≈ 14° 的倾斜，与 DWrite 的 OBLIQUE 模拟同量级。
                if ((simFlags & MilFontFaceTable.SimulationOblique) != 0) font.SkewX = -0.25f;
            }

            using SKPath glyph = font.GetGlyphPath(glyphIndex);
            if (glyph == null) return HResult.E_FAIL;

            SKPath source = glyph;

            SKPath rotated = null;
            if (sideways)
            {
                // y 向下的坐标系里，"顺时针 90°" 的矩阵是 (x,y) → (-y,x)，
                // 等价于 SKMatrix.CreateRotationDegrees(90)。
                var matrix = SKMatrix.CreateRotationDegrees(90f);
                rotated = new SKPath();
                glyph.Transform(matrix, rotated);
                source = rotated;
            }

            try
            {
                byte[] data = MilGeometryEngine.SerializePath(source);
                if (data == null) return HResult.E_UNEXPECTED;

                IntPtr pointer = MilPathGeometryDataTable.Allocate(data);
                if (pointer == IntPtr.Zero) return HResult.E_OUTOFMEMORY;

                pPathGeometryData = (byte*)pointer;
                pSize = (uint)data.Length;
                return HResult.S_OK;
            }
            finally
            {
                rotated?.Dispose();
            }
        }

        /// <summary>
        /// 释放 <see cref="MilGlyphRun_GetGlyphOutline"/> 返回的内存块。
        /// 无效指针或重复释放 → E_INVALIDARG（上游是直接 free，这里刻意做成可检出的错误）。
        /// </summary>
        public static int MilGlyphRun_ReleasePathGeometryData(byte* pPathGeometryData)
        {
            if (pPathGeometryData == null) return HResult.E_INVALIDARG;

            return MilPathGeometryDataTable.Release((IntPtr)pPathGeometryData)
                ? HResult.S_OK
                : HResult.E_INVALIDARG;
        }

        // ==================================================================
        //  渲染时命令（glyph run / glyph cache）
        // ==================================================================

        /// <summary>
        /// 在渲染时给一个字形运行对象设置几何。命令字节按整条投递到目标上
        /// （对应上游"渲染线程直接往从端对象写一条命令"）。
        /// 目标句柄无效 → E_HANDLE；命令为空 → E_INVALIDARG；有未闭合命令 → E_UNEXPECTED。
        /// </summary>
        public static int MilGlyphRun_SetGeometryAtRenderTime(
            IntPtr pMilGlyphRunTarget,
            byte* pCmd,
            uint cbCmd)
        {
            MilRenderTimeTarget target = MilRenderTimeTargetTable.Resolve(pMilGlyphRunTarget);
            if (target == null) return HResult.E_HANDLE;
            if (pCmd == null || cbCmd == 0) return HResult.E_INVALIDARG;

            return target.Send(new ReadOnlySpan<byte>(pCmd, (int)cbCmd));
        }

        /// <summary>
        /// 渲染时开一条字形缓存命令：写 cbSize 字节头部并预留 cbExtra 字节变长载荷。
        /// 错误码与 MilChannel_BeginCommand 同一套（E_UNEXPECTED / E_INVALIDARG / E_HANDLE）。
        /// </summary>
        public static int MilGlyphCache_BeginCommandAtRenderTime(
            IntPtr pMilSlaveGlyphCacheTarget,
            byte* pbData,
            uint cbSize,
            uint cbExtra)
        {
            MilRenderTimeTarget target = MilRenderTimeTargetTable.Resolve(pMilSlaveGlyphCacheTarget);
            if (target == null) return HResult.E_HANDLE;
            if (pbData == null || cbSize == 0) return HResult.E_INVALIDARG;

            return target.Begin(new ReadOnlySpan<byte>(pbData, (int)cbSize), cbExtra);
        }

        /// <summary>续写渲染时命令的变长载荷；不得超过 Begin 声明的 cbExtra。</summary>
        public static int MilGlyphCache_AppendCommandDataAtRenderTime(
            IntPtr pMilSlaveGlyphCacheTarget,
            byte* pbData,
            uint cbSize)
        {
            MilRenderTimeTarget target = MilRenderTimeTargetTable.Resolve(pMilSlaveGlyphCacheTarget);
            if (target == null) return HResult.E_HANDLE;
            if (pbData == null || cbSize == 0) return HResult.E_INVALIDARG;

            return target.Append(new ReadOnlySpan<byte>(pbData, (int)cbSize));
        }

        /// <summary>闭合渲染时命令。</summary>
        public static int MilGlyphCache_EndCommandAtRenderTime(IntPtr pMilSlaveGlyphCacheTarget)
        {
            MilRenderTimeTarget target = MilRenderTimeTargetTable.Resolve(pMilSlaveGlyphCacheTarget);
            if (target == null) return HResult.E_HANDLE;

            return target.End();
        }
    }
}
