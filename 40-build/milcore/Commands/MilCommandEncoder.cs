// Licensed to the .NET Foundation under one or more agreements.
//
// 命令编码器：按**显式字节偏移**把命令写成字节流。
//
// 这里的偏移常量是从上游 Generated/wgx_commands.cs 的 FieldOffset 手工誊写的。
// 它与 MilCommandStructs.cs（逐字复制的结构体）是两份独立的实现，
// 由 tests/WpfGfx.Linux.Tests/CommandLayoutTests.cs 双向校验：
//     Marshal.OffsetOf<MILCMD_XXX>("Field") == 这里的常量
//     Encode(values) → Decode(struct) → 字段值相等
// 任一侧写错都会让测试红。

using System;
using System.Buffers.Binary;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Commands
{
    /// <summary>带绝对定位写入能力的命令缓冲。</summary>
    internal sealed class MilCommandBuffer
    {
        private byte[] _buf;
        private int _len;

        public MilCommandBuffer(int capacity = 256) { _buf = new byte[capacity]; }

        public int Length => _len;

        /// <summary>
        /// 命令字。WriteHeader 时记下，用于把命令补齐到线格结构体的完整长度。
        ///
        /// 【为什么必须补】命令流里的每条命令都必须是完整长度：上游是按
        /// sizeof(MILCMD_XXX) 整块 memcpy 的，动画句柄等尾部字段即使为 0 也要占字节。
        /// 逐字段写入的编码器只写"有意义的"字段，会写出比结构体短的命令，
        /// 解码侧 MemoryMarshal.Read 就会越界。
        /// </summary>
        private MilCmd _type = MilCmd.MilCmdInvalid;
        private bool _hasType;

        public void SetType(MilCmd type)
        {
            _type = type;
            _hasType = true;
        }

        public void Reserve(int total)
        {
            if (total > _buf.Length) Array.Resize(ref _buf, Math.Max(total, _buf.Length * 2));
            if (total > _len) _len = total;
        }

        /// <summary>补齐到命令固定部分的长度（变长尾部不受影响，只会更长）。</summary>
        private void PadToFixedSize()
        {
            if (!_hasType) return;
            int fixedSize = MilCommandLayout.FixedSize(_type);
            if (fixedSize > 0) Reserve(fixedSize);
        }

        private void Ensure(int offset, int size) => Reserve(offset + size);

        public void WriteInt32(int offset, int v)
        {
            Ensure(offset, 4);
            BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteUInt32(int offset, uint v)
        {
            Ensure(offset, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteUInt16(int offset, ushort v)
        {
            Ensure(offset, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteUInt64(int offset, ulong v)
        {
            Ensure(offset, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteSingle(int offset, float v)
        {
            Ensure(offset, 4);
            BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteDouble(int offset, double v)
        {
            Ensure(offset, 8);
            BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(offset), v);
        }

        public void WriteHandle(int offset, DUCE.ResourceHandle h) => WriteUInt32(offset, (uint)h);

        public void WriteColor(int offset, MilColorF c)
        {
            WriteSingle(offset, c.R);
            WriteSingle(offset + 4, c.G);
            WriteSingle(offset + 8, c.B);
            WriteSingle(offset + 12, c.A);
        }

        public void WritePoint(int offset, MilPoint p)
        {
            WriteDouble(offset, p.X);
            WriteDouble(offset + 8, p.Y);
        }

        public void WritePoint2F(int offset, MilPoint2F p)
        {
            WriteSingle(offset, p.X);
            WriteSingle(offset + 4, p.Y);
        }

        public void WritePoint3F(int offset, MilPoint3F p)
        {
            WriteSingle(offset, p.X);
            WriteSingle(offset + 4, p.Y);
            WriteSingle(offset + 8, p.Z);
        }

        public void WriteQuaternion(int offset, MilQuaternionF q)
        {
            WriteSingle(offset, q.X);
            WriteSingle(offset + 4, q.Y);
            WriteSingle(offset + 8, q.Z);
            WriteSingle(offset + 12, q.W);
        }

        /// <summary>4×4 单精度矩阵（上游 D3DMATRIX）：16 个 float 连续排列，共 64 字节。</summary>
        public void WriteMatrix4x4F(int offset, MilMatrix4x4F m)
        {
            WriteSingle(offset, m.M11); WriteSingle(offset + 4, m.M12);
            WriteSingle(offset + 8, m.M13); WriteSingle(offset + 12, m.M14);
            WriteSingle(offset + 16, m.M21); WriteSingle(offset + 20, m.M22);
            WriteSingle(offset + 24, m.M23); WriteSingle(offset + 28, m.M24);
            WriteSingle(offset + 32, m.M31); WriteSingle(offset + 36, m.M32);
            WriteSingle(offset + 40, m.M33); WriteSingle(offset + 44, m.M34);
            WriteSingle(offset + 48, m.M41); WriteSingle(offset + 52, m.M42);
            WriteSingle(offset + 56, m.M43); WriteSingle(offset + 60, m.M44);
        }

        public void WriteRect(int offset, MilRect r)
        {
            WriteDouble(offset, r.X);
            WriteDouble(offset + 8, r.Y);
            WriteDouble(offset + 16, r.Width);
            WriteDouble(offset + 24, r.Height);
        }

        public void WriteSize(int offset, MilSize s)
        {
            WriteDouble(offset, s.Width);
            WriteDouble(offset + 8, s.Height);
        }

        public void WriteMatrix(int offset, MilMatrix3x2D m)
        {
            WriteDouble(offset, m.S_11);
            WriteDouble(offset + 8, m.S_12);
            WriteDouble(offset + 16, m.S_21);
            WriteDouble(offset + 24, m.S_22);
            WriteDouble(offset + 32, m.DX);
            WriteDouble(offset + 40, m.DY);
        }

        public void WriteRectI(int offset, MilRectI r)
        {
            WriteInt32(offset, r.Left);
            WriteInt32(offset + 4, r.Top);
            WriteInt32(offset + 8, r.Right);
            WriteInt32(offset + 12, r.Bottom);
        }

        public void WriteRenderOptions(int offset, MilRenderOptions o)
        {
            WriteUInt32(offset, (uint)o.Flags);
            WriteUInt32(offset + 4, (uint)o.EdgeMode);
            WriteUInt32(offset + 8, (uint)o.CompositingMode);
            WriteUInt32(offset + 12, (uint)o.BitmapScalingMode);
            WriteUInt32(offset + 16, (uint)o.ClearTypeHint);
            WriteUInt32(offset + 20, (uint)o.TextRenderingMode);
            WriteUInt32(offset + 24, (uint)o.TextHintingMode);
        }

        public void WriteBytes(int offset, ReadOnlySpan<byte> data)
        {
            Ensure(offset, data.Length);
            data.CopyTo(_buf.AsSpan(offset));
        }

        public byte[] ToArray() { PadToFixedSize(); return _buf.AsSpan(0, _len).ToArray(); }

        public ReadOnlySpan<byte> AsSpan() { PadToFixedSize(); return _buf.AsSpan(0, _len); }
    }

    /// <summary>
    /// 命令编码器。每个方法按上游布局写出一条完整命令（含尾部变长数组）。
    /// 方法名与 MilCmd 一一对应。
    /// </summary>
    internal static class MilCommandEncoder
    {
        // ---- 通用头 ----
        private const int OffType = 0;
        private const int OffHandle = 4;

        private static void WriteHeader(MilCommandBuffer b, MilCmd type, DUCE.ResourceHandle handle)
        {
            b.SetType(type);
            b.WriteInt32(OffType, (int)type);
            b.WriteHandle(OffHandle, handle);
        }

        // ================= Visual =================

        // MILCMD_VISUAL_SETOFFSET: Type@0 Handle@4 offsetX@8 offsetY@16
        public static byte[] VisualSetOffset(DUCE.ResourceHandle h, double x, double y)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdVisualSetOffset, h);
            b.WriteDouble(8, x);
            b.WriteDouble(16, y);
            return b.ToArray();
        }

        // MILCMD_VISUAL_SETALPHA: Type@0 Handle@4 alpha@8
        public static byte[] VisualSetAlpha(DUCE.ResourceHandle h, double alpha)
        {
            var b = new MilCommandBuffer(16);
            WriteHeader(b, MilCmd.MilCmdVisualSetAlpha, h);
            b.WriteDouble(8, alpha);
            return b.ToArray();
        }

        // MILCMD_VISUAL_SETTRANSFORM / SETEFFECT / SETCACHEMODE / SETCLIP / SETCONTENT / SETALPHAMASK
        private static byte[] VisualSetSingleHandle(MilCmd cmd, DUCE.ResourceHandle h, DUCE.ResourceHandle value)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, cmd, h);
            b.WriteHandle(8, value);
            return b.ToArray();
        }

        public static byte[] VisualSetTransform(DUCE.ResourceHandle h, DUCE.ResourceHandle t) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetTransform, h, t);
        public static byte[] VisualSetEffect(DUCE.ResourceHandle h, DUCE.ResourceHandle e) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetEffect, h, e);
        public static byte[] VisualSetCacheMode(DUCE.ResourceHandle h, DUCE.ResourceHandle c) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetCacheMode, h, c);
        public static byte[] VisualSetClip(DUCE.ResourceHandle h, DUCE.ResourceHandle c) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetClip, h, c);
        public static byte[] VisualSetContent(DUCE.ResourceHandle h, DUCE.ResourceHandle c) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetContent, h, c);
        public static byte[] VisualSetAlphaMask(DUCE.ResourceHandle h, DUCE.ResourceHandle m) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualSetAlphaMask, h, m);
        public static byte[] VisualRemoveChild(DUCE.ResourceHandle h, DUCE.ResourceHandle child) =>
            VisualSetSingleHandle(MilCmd.MilCmdVisualRemoveChild, h, child);

        // MILCMD_VISUAL_REMOVEALLCHILDREN: 8 bytes
        public static byte[] VisualRemoveAllChildren(DUCE.ResourceHandle h)
        {
            var b = new MilCommandBuffer(8);
            WriteHeader(b, MilCmd.MilCmdVisualRemoveAllChildren, h);
            return b.ToArray();
        }

        // MILCMD_VISUAL_INSERTCHILDAT: Type@0 Handle@4 hChild@8 index@12
        public static byte[] VisualInsertChildAt(DUCE.ResourceHandle h, DUCE.ResourceHandle child, uint index)
        {
            var b = new MilCommandBuffer(16);
            WriteHeader(b, MilCmd.MilCmdVisualInsertChildAt, h);
            b.WriteHandle(8, child);
            b.WriteUInt32(12, index);
            return b.ToArray();
        }

        // MILCMD_VISUAL_SETRENDEROPTIONS: Type@0 Handle@4 renderOptions@8 (7×4=28)
        public static byte[] VisualSetRenderOptions(DUCE.ResourceHandle h, MilRenderOptions o)
        {
            var b = new MilCommandBuffer(36);
            WriteHeader(b, MilCmd.MilCmdVisualSetRenderOptions, h);
            b.WriteRenderOptions(8, o);
            return b.ToArray();
        }

        // MILCMD_VISUAL_SETSCROLLABLEAREACLIP: Type@0 Handle@4 Clip@8 (32) IsEnabled@40
        public static byte[] VisualSetScrollableAreaClip(DUCE.ResourceHandle h, MilRect clip, bool enabled)
        {
            var b = new MilCommandBuffer(44);
            WriteHeader(b, MilCmd.MilCmdVisualSetScrollableAreaClip, h);
            b.WriteRect(8, clip);
            b.WriteUInt32(40, enabled ? 1u : 0u);
            return b.ToArray();
        }

        // MILCMD_VISUAL_SETGUIDELINECOLLECTION: Type@0 Handle@4 countX@8 countY@12, 尾部 (X+Y) 个 float
        public static byte[] VisualSetGuidelineCollection(DUCE.ResourceHandle h, float[] x, float[] y)
        {
            int countX = x?.Length ?? 0;
            int countY = y?.Length ?? 0;
            int total = 16 + (countX + countY) * 4;
            var b = new MilCommandBuffer(total);
            WriteHeader(b, MilCmd.MilCmdVisualSetGuidelineCollection, h);
            b.WriteUInt16(8, (ushort)countX);
            b.WriteUInt16(12, (ushort)countY);
            int off = 16;
            for (int i = 0; i < countX; i++, off += 4) b.WriteSingle(off, x[i]);
            for (int i = 0; i < countY; i++, off += 4) b.WriteSingle(off, y[i]);
            return b.ToArray();
        }

        // ================= Target =================

        // MILCMD_TARGET_SETROOT: Type@0 Handle@4 hRoot@8
        public static byte[] TargetSetRoot(DUCE.ResourceHandle h, DUCE.ResourceHandle root)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdTargetSetRoot, h);
            b.WriteHandle(8, root);
            return b.ToArray();
        }

        // MILCMD_TARGET_SETCLEARCOLOR: Type@0 Handle@4 clearColor@8 (16)
        public static byte[] TargetSetClearColor(DUCE.ResourceHandle h, MilColorF color)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdTargetSetClearColor, h);
            b.WriteColor(8, color);
            return b.ToArray();
        }

        // MILCMD_TARGET_INVALIDATE: Type@0 Handle@4 rc@8 (RECT 16)
        public static byte[] TargetInvalidate(DUCE.ResourceHandle h, MilRectI rc)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdTargetInvalidate, h);
            b.WriteRectI(8, rc);
            return b.ToArray();
        }

        // MILCMD_TARGET_SETFLAGS: Type@0 Handle@4 flags@8
        public static byte[] TargetSetFlags(DUCE.ResourceHandle h, uint flags)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdTargetSetFlags, h);
            b.WriteUInt32(8, flags);
            return b.ToArray();
        }

        // MILCMD_HWNDTARGET_CREATE: 92 bytes
        public static byte[] HwndTargetCreate(
            DUCE.ResourceHandle h, ulong hwnd, ulong hSection, ulong masterDevice,
            uint width, uint height, MilColorF clearColor, uint flags,
            DUCE.ResourceHandle hBitmap, uint stride, uint pixelFormat,
            int dpiAwarenessContext, double dpiX, double dpiY)
        {
            var b = new MilCommandBuffer(92);
            WriteHeader(b, MilCmd.MilCmdHwndTargetCreate, h);
            b.WriteUInt64(8, hwnd);
            b.WriteUInt64(16, hSection);
            b.WriteUInt64(24, masterDevice);
            b.WriteUInt32(32, width);
            b.WriteUInt32(36, height);
            b.WriteColor(40, clearColor);
            b.WriteUInt32(56, flags);
            b.WriteHandle(60, hBitmap);
            b.WriteUInt32(64, stride);
            b.WriteUInt32(68, pixelFormat);
            b.WriteInt32(72, dpiAwarenessContext);
            b.WriteDouble(76, dpiX);
            b.WriteDouble(84, dpiY);
            return b.ToArray();
        }

        // MILCMD_GENERICTARGET_CREATE: 36 bytes
        public static byte[] GenericTargetCreate(
            DUCE.ResourceHandle h, ulong hwnd, ulong pRenderTarget, uint width, uint height, uint dummy)
        {
            var b = new MilCommandBuffer(36);
            WriteHeader(b, MilCmd.MilCmdGenericTargetCreate, h);
            b.WriteUInt64(8, hwnd);
            b.WriteUInt64(16, pRenderTarget);
            b.WriteUInt32(24, width);
            b.WriteUInt32(28, height);
            b.WriteUInt32(32, dummy);
            return b.ToArray();
        }

        // MILCMD_HWNDTARGET_DPICHANGED: Type@0 Handle@4 DpiX@8 DpiY@16 AfterParent@24
        public static byte[] HwndTargetDpiChanged(DUCE.ResourceHandle h, double dpiX, double dpiY, bool afterParent)
        {
            var b = new MilCommandBuffer(28);
            WriteHeader(b, MilCmd.MilCmdHwndTargetDpiChanged, h);
            b.WriteDouble(8, dpiX);
            b.WriteDouble(16, dpiY);
            b.WriteUInt32(24, afterParent ? 1u : 0u);
            return b.ToArray();
        }

        // ================= 标量资源 =================

        public static byte[] DoubleResource(DUCE.ResourceHandle h, double v)
        {
            var b = new MilCommandBuffer(16);
            WriteHeader(b, MilCmd.MilCmdDoubleResource, h);
            b.WriteDouble(8, v);
            return b.ToArray();
        }

        public static byte[] ColorResource(DUCE.ResourceHandle h, MilColorF v)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdColorResource, h);
            b.WriteColor(8, v);
            return b.ToArray();
        }

        public static byte[] PointResource(DUCE.ResourceHandle h, MilPoint v)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdPointResource, h);
            b.WritePoint(8, v);
            return b.ToArray();
        }

        public static byte[] RectResource(DUCE.ResourceHandle h, MilRect v)
        {
            var b = new MilCommandBuffer(40);
            WriteHeader(b, MilCmd.MilCmdRectResource, h);
            b.WriteRect(8, v);
            return b.ToArray();
        }

        public static byte[] SizeResource(DUCE.ResourceHandle h, MilSize v)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdSizeResource, h);
            b.WriteSize(8, v);
            return b.ToArray();
        }

        public static byte[] MatrixResource(DUCE.ResourceHandle h, MilMatrix3x2D v)
        {
            var b = new MilCommandBuffer(56);
            WriteHeader(b, MilCmd.MilCmdMatrixResource, h);
            b.WriteMatrix(8, v);
            return b.ToArray();
        }

        public static byte[] Point3DResource(DUCE.ResourceHandle h, MilPoint3F v)
        {
            var b = new MilCommandBuffer(20);
            WriteHeader(b, MilCmd.MilCmdPoint3DResource, h);
            b.WritePoint3F(8, v);
            return b.ToArray();
        }

        public static byte[] Vector3DResource(DUCE.ResourceHandle h, MilPoint3F v)
        {
            var b = new MilCommandBuffer(20);
            WriteHeader(b, MilCmd.MilCmdVector3DResource, h);
            b.WritePoint3F(8, v);
            return b.ToArray();
        }

        public static byte[] QuaternionResource(DUCE.ResourceHandle h, MilQuaternionF v)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdQuaternionResource, h);
            b.WriteQuaternion(8, v);
            return b.ToArray();
        }

        public static byte[] EtwEventResource(DUCE.ResourceHandle h, uint id)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdEtwEventResource, h);
            b.WriteUInt32(8, id);
            return b.ToArray();
        }

        // ================= RenderData =================

        // MILCMD_RENDERDATA: Type@0 Handle@4 cbData@8，后接指令流
        public static byte[] RenderData(DUCE.ResourceHandle h, ReadOnlySpan<byte> stream)
        {
            var b = new MilCommandBuffer(12 + stream.Length);
            WriteHeader(b, MilCmd.MilCmdRenderData, h);
            b.WriteUInt32(8, (uint)stream.Length);
            b.WriteBytes(12, stream);
            return b.ToArray();
        }

        // ================= 变换 =================

        // MILCMD_TRANSLATETRANSFORM: X@8 Y@16 hXAnimations@24 hYAnimations@28
        public static byte[] TranslateTransform(DUCE.ResourceHandle h, double x, double y,
            DUCE.ResourceHandle hx = default, DUCE.ResourceHandle hy = default)
        {
            var b = new MilCommandBuffer(32);
            WriteHeader(b, MilCmd.MilCmdTranslateTransform, h);
            b.WriteDouble(8, x);
            b.WriteDouble(16, y);
            b.WriteHandle(24, hx);
            b.WriteHandle(28, hy);
            return b.ToArray();
        }

        // MILCMD_SCALETRANSFORM: ScaleX@8 ScaleY@16 CenterX@24 CenterY@32 + 4 handles@40..52
        public static byte[] ScaleTransform(DUCE.ResourceHandle h, double sx, double sy, double cx, double cy)
        {
            var b = new MilCommandBuffer(56);
            WriteHeader(b, MilCmd.MilCmdScaleTransform, h);
            b.WriteDouble(8, sx);
            b.WriteDouble(16, sy);
            b.WriteDouble(24, cx);
            b.WriteDouble(32, cy);
            return b.ToArray();
        }

        // MILCMD_SKEWTRANSFORM: AngleX@8 AngleY@16 CenterX@24 CenterY@32 + 4 handles@40..52
        public static byte[] SkewTransform(DUCE.ResourceHandle h, double ax, double ay, double cx, double cy)
        {
            var b = new MilCommandBuffer(56);
            WriteHeader(b, MilCmd.MilCmdSkewTransform, h);
            b.WriteDouble(8, ax);
            b.WriteDouble(16, ay);
            b.WriteDouble(24, cx);
            b.WriteDouble(32, cy);
            return b.ToArray();
        }

        // MILCMD_ROTATETRANSFORM: Angle@8 CenterX@16 CenterY@24 + 3 handles@32..40
        public static byte[] RotateTransform(DUCE.ResourceHandle h, double angle, double cx, double cy)
        {
            var b = new MilCommandBuffer(44);
            WriteHeader(b, MilCmd.MilCmdRotateTransform, h);
            b.WriteDouble(8, angle);
            b.WriteDouble(16, cx);
            b.WriteDouble(24, cy);
            return b.ToArray();
        }

        // MILCMD_MATRIXTRANSFORM: Matrix@8 (48) hMatrixAnimations@56
        public static byte[] MatrixTransform(DUCE.ResourceHandle h, MilMatrix3x2D m)
        {
            var b = new MilCommandBuffer(60);
            WriteHeader(b, MilCmd.MilCmdMatrixTransform, h);
            b.WriteMatrix(8, m);
            return b.ToArray();
        }

        // MILCMD_TRANSFORMGROUP: ChildrenSize@8，后接 N 个句柄
        public static byte[] TransformGroup(DUCE.ResourceHandle h, DUCE.ResourceHandle[] children)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(12 + n * 4);
            WriteHeader(b, MilCmd.MilCmdTransformGroup, h);
            b.WriteUInt32(8, (uint)(n * 4));
            for (int i = 0; i < n; i++) b.WriteHandle(12 + i * 4, children[i]);
            return b.ToArray();
        }

        // ================= 几何 =================

        // MILCMD_LINEGEOMETRY: StartPoint@8 EndPoint@24 hTransform@40 + 2 anim handles
        public static byte[] LineGeometry(DUCE.ResourceHandle h, MilPoint start, MilPoint end, DUCE.ResourceHandle transform = default)
        {
            var b = new MilCommandBuffer(52);
            WriteHeader(b, MilCmd.MilCmdLineGeometry, h);
            b.WritePoint(8, start);
            b.WritePoint(24, end);
            b.WriteHandle(40, transform);
            return b.ToArray();
        }

        // MILCMD_RECTANGLEGEOMETRY: RadiusX@8 RadiusY@16 Rect@24 (32) hTransform@56 + 3 anim
        public static byte[] RectangleGeometry(DUCE.ResourceHandle h, MilRect rect, double rx, double ry, DUCE.ResourceHandle transform = default)
        {
            var b = new MilCommandBuffer(72);
            WriteHeader(b, MilCmd.MilCmdRectangleGeometry, h);
            b.WriteDouble(8, rx);
            b.WriteDouble(16, ry);
            b.WriteRect(24, rect);
            b.WriteHandle(56, transform);
            return b.ToArray();
        }

        // MILCMD_ELLIPSEGEOMETRY: RadiusX@8 RadiusY@16 Center@24 hTransform@40 + 3 anim
        public static byte[] EllipseGeometry(DUCE.ResourceHandle h, MilPoint center, double rx, double ry, DUCE.ResourceHandle transform = default)
        {
            var b = new MilCommandBuffer(56);
            WriteHeader(b, MilCmd.MilCmdEllipseGeometry, h);
            b.WriteDouble(8, rx);
            b.WriteDouble(16, ry);
            b.WritePoint(24, center);
            b.WriteHandle(40, transform);
            return b.ToArray();
        }

        // MILCMD_COMBINEDGEOMETRY: hTransform@8 mode@12 hGeometry1@16 hGeometry2@20
        public static byte[] CombinedGeometry(DUCE.ResourceHandle h, MilGeometryCombineMode mode,
            DUCE.ResourceHandle g1, DUCE.ResourceHandle g2, DUCE.ResourceHandle transform = default)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdCombinedGeometry, h);
            b.WriteHandle(8, transform);
            b.WriteUInt32(12, (uint)mode);
            b.WriteHandle(16, g1);
            b.WriteHandle(20, g2);
            return b.ToArray();
        }

        // MILCMD_GEOMETRYGROUP: hTransform@8 FillRule@12 ChildrenSize@16，后接 N 个句柄
        public static byte[] GeometryGroup(DUCE.ResourceHandle h, MilFillRule fillRule, DUCE.ResourceHandle[] children, DUCE.ResourceHandle transform = default)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(20 + n * 4);
            WriteHeader(b, MilCmd.MilCmdGeometryGroup, h);
            b.WriteHandle(8, transform);
            b.WriteUInt32(12, (uint)fillRule);
            b.WriteUInt32(16, (uint)(n * 4));
            for (int i = 0; i < n; i++) b.WriteHandle(20 + i * 4, children[i]);
            return b.ToArray();
        }

        // MILCMD_PATHGEOMETRY: hTransform@8 FillRule@12 FiguresSize@16，后接序列化路径数据
        public static byte[] PathGeometry(DUCE.ResourceHandle h, MilFillRule fillRule, ReadOnlySpan<byte> figures, DUCE.ResourceHandle transform = default)
        {
            var b = new MilCommandBuffer(20 + figures.Length);
            WriteHeader(b, MilCmd.MilCmdPathGeometry, h);
            b.WriteHandle(8, transform);
            b.WriteUInt32(12, (uint)fillRule);
            b.WriteUInt32(16, (uint)figures.Length);
            b.WriteBytes(20, figures);
            return b.ToArray();
        }

        // ================= 画刷 =================

        // MILCMD_SOLIDCOLORBRUSH: Opacity@8 Color@16 hOpacityAnimations@32 hTransform@36 hRelativeTransform@40 hColorAnimations@44
        public static byte[] SolidColorBrush(DUCE.ResourceHandle h, MilColorF color, double opacity = 1.0,
            DUCE.ResourceHandle transform = default, DUCE.ResourceHandle relative = default)
        {
            var b = new MilCommandBuffer(48);
            WriteHeader(b, MilCmd.MilCmdSolidColorBrush, h);
            b.WriteDouble(8, opacity);
            b.WriteColor(16, color);
            b.WriteHandle(36, transform);
            b.WriteHandle(40, relative);
            return b.ToArray();
        }

        // MILCMD_LINEARGRADIENTBRUSH: Opacity@8 StartPoint@16 EndPoint@32 hOpacityAnimations@48
        //   hTransform@52 hRelativeTransform@56 ColorInterpolationMode@60 MappingMode@64
        //   SpreadMethod@68 GradientStopsSize@72 hStartPointAnimations@76 hEndPointAnimations@80
        //   尾部 GradientStopsSize 字节 = N × MIL_GRADIENTSTOP(24)
        public static byte[] LinearGradientBrush(
            DUCE.ResourceHandle h, MilPoint start, MilPoint end, MilGradientStop[] stops,
            double opacity = 1.0, MilColorInterpolationMode cim = MilColorInterpolationMode.SRgbLinearInterpolation,
            MilBrushMappingMode mapping = MilBrushMappingMode.RelativeToBoundingBox,
            MilGradientSpreadMethod spread = MilGradientSpreadMethod.Pad,
            DUCE.ResourceHandle transform = default, DUCE.ResourceHandle relative = default)
        {
            int n = stops?.Length ?? 0;
            var b = new MilCommandBuffer(84 + n * MilGradientStop.SizeInBytes);
            WriteHeader(b, MilCmd.MilCmdLinearGradientBrush, h);
            b.WriteDouble(8, opacity);
            b.WritePoint(16, start);
            b.WritePoint(32, end);
            b.WriteHandle(52, transform);
            b.WriteHandle(56, relative);
            b.WriteUInt32(60, (uint)cim);
            b.WriteUInt32(64, (uint)mapping);
            b.WriteUInt32(68, (uint)spread);
            b.WriteUInt32(72, (uint)(n * MilGradientStop.SizeInBytes));
            for (int i = 0; i < n; i++)
            {
                int off = 84 + i * MilGradientStop.SizeInBytes;
                b.WriteDouble(off, stops[i].Position);
                b.WriteColor(off + 8, stops[i].Color);
            }
            return b.ToArray();
        }

        // MILCMD_RADIALGRADIENTBRUSH: Opacity@8 Center@16 RadiusX@32 RadiusY@40 GradientOrigin@48
        //   hOpacityAnimations@64 hTransform@68 hRelativeTransform@72 CIM@76 Mapping@80
        //   Spread@84 GradientStopsSize@88 hCenterAnimations@92 ... 尾部 stops
        public static byte[] RadialGradientBrush(
            DUCE.ResourceHandle h, MilPoint center, MilPoint origin, double rx, double ry,
            MilGradientStop[] stops, double opacity = 1.0,
            MilColorInterpolationMode cim = MilColorInterpolationMode.SRgbLinearInterpolation,
            MilBrushMappingMode mapping = MilBrushMappingMode.RelativeToBoundingBox,
            MilGradientSpreadMethod spread = MilGradientSpreadMethod.Pad,
            DUCE.ResourceHandle transform = default, DUCE.ResourceHandle relative = default)
        {
            int n = stops?.Length ?? 0;
            var b = new MilCommandBuffer(108 + n * MilGradientStop.SizeInBytes);
            WriteHeader(b, MilCmd.MilCmdRadialGradientBrush, h);
            b.WriteDouble(8, opacity);
            b.WritePoint(16, center);
            b.WriteDouble(32, rx);
            b.WriteDouble(40, ry);
            b.WritePoint(48, origin);
            b.WriteHandle(68, transform);
            b.WriteHandle(72, relative);
            b.WriteUInt32(76, (uint)cim);
            b.WriteUInt32(80, (uint)mapping);
            b.WriteUInt32(84, (uint)spread);
            b.WriteUInt32(88, (uint)(n * MilGradientStop.SizeInBytes));
            for (int i = 0; i < n; i++)
            {
                int off = 108 + i * MilGradientStop.SizeInBytes;
                b.WriteDouble(off, stops[i].Position);
                b.WriteColor(off + 8, stops[i].Color);
            }
            return b.ToArray();
        }

        // ================= Pen / DashStyle =================

        // MILCMD_PEN: Thickness@8 MiterLimit@16 hBrush@24 hThicknessAnimations@28
        //   StartLineCap@32 EndLineCap@36 DashCap@40 LineJoin@44 hDashStyle@48
        public static byte[] Pen(
            DUCE.ResourceHandle h, DUCE.ResourceHandle brush, double thickness = 1.0,
            DUCE.ResourceHandle dashStyle = default, double miterLimit = 10.0,
            MilPenLineCap startCap = MilPenLineCap.Flat, MilPenLineCap endCap = MilPenLineCap.Flat,
            MilPenLineCap dashCap = MilPenLineCap.Flat, MilPenLineJoin join = MilPenLineJoin.Miter)
        {
            var b = new MilCommandBuffer(52);
            WriteHeader(b, MilCmd.MilCmdPen, h);
            b.WriteDouble(8, thickness);
            b.WriteDouble(16, miterLimit);
            b.WriteHandle(24, brush);
            b.WriteUInt32(32, (uint)startCap);
            b.WriteUInt32(36, (uint)endCap);
            b.WriteUInt32(40, (uint)dashCap);
            b.WriteUInt32(44, (uint)join);
            b.WriteHandle(48, dashStyle);
            return b.ToArray();
        }

        // MILCMD_DASHSTYLE: Offset@8 hOffsetAnimations@16 DashesSize@20，后接 N 个 double
        public static byte[] DashStyle(DUCE.ResourceHandle h, double offset, double[] dashes)
        {
            int n = dashes?.Length ?? 0;
            var b = new MilCommandBuffer(24 + n * 8);
            WriteHeader(b, MilCmd.MilCmdDashStyle, h);
            b.WriteDouble(8, offset);
            b.WriteUInt32(20, (uint)(n * 8));
            for (int i = 0; i < n; i++) b.WriteDouble(24 + i * 8, dashes[i]);
            return b.ToArray();
        }

        // ================= Drawing =================

        // MILCMD_GEOMETRYDRAWING: hBrush@8 hPen@12 hGeometry@16
        public static byte[] GeometryDrawing(DUCE.ResourceHandle h, DUCE.ResourceHandle brush, DUCE.ResourceHandle pen, DUCE.ResourceHandle geometry)
        {
            var b = new MilCommandBuffer(20);
            WriteHeader(b, MilCmd.MilCmdGeometryDrawing, h);
            b.WriteHandle(8, brush);
            b.WriteHandle(12, pen);
            b.WriteHandle(16, geometry);
            return b.ToArray();
        }

        // MILCMD_GLYPHRUNDRAWING: hGlyphRun@8 hForegroundBrush@12
        public static byte[] GlyphRunDrawing(DUCE.ResourceHandle h, DUCE.ResourceHandle glyphRun, DUCE.ResourceHandle foreground)
        {
            var b = new MilCommandBuffer(16);
            WriteHeader(b, MilCmd.MilCmdGlyphRunDrawing, h);
            b.WriteHandle(8, glyphRun);
            b.WriteHandle(12, foreground);
            return b.ToArray();
        }

        // MILCMD_IMAGEDRAWING: Rect@8 hImageSource@40 hRectAnimations@44
        public static byte[] ImageDrawing(DUCE.ResourceHandle h, MilRect rect, DUCE.ResourceHandle imageSource)
        {
            var b = new MilCommandBuffer(48);
            WriteHeader(b, MilCmd.MilCmdImageDrawing, h);
            b.WriteRect(8, rect);
            b.WriteHandle(40, imageSource);
            return b.ToArray();
        }

        // MILCMD_DRAWINGIMAGE: hDrawing@8
        public static byte[] DrawingImage(DUCE.ResourceHandle h, DUCE.ResourceHandle drawing)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdDrawingImage, h);
            b.WriteHandle(8, drawing);
            return b.ToArray();
        }

        // MILCMD_DRAWINGGROUP: Opacity@8 ChildrenSize@16 hClipGeometry@20 hOpacityAnimations@24
        //   hOpacityMask@28 hTransform@32 hGuidelineSet@36 EdgeMode@40 BitmapScalingMode@44
        //   ClearTypeHint@48，后接 N 个句柄
        public static byte[] DrawingGroup(
            DUCE.ResourceHandle h, double opacity, DUCE.ResourceHandle[] children,
            DUCE.ResourceHandle clip = default, DUCE.ResourceHandle opacityMask = default,
            DUCE.ResourceHandle transform = default, DUCE.ResourceHandle guidelineSet = default,
            MilEdgeMode edgeMode = MilEdgeMode.Unspecified,
            MilBitmapScalingMode scaling = MilBitmapScalingMode.Unspecified,
            MilClearTypeHint ctHint = MilClearTypeHint.Auto)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(52 + n * 4);
            WriteHeader(b, MilCmd.MilCmdDrawingGroup, h);
            b.WriteDouble(8, opacity);
            b.WriteUInt32(16, (uint)(n * 4));
            b.WriteHandle(20, clip);
            b.WriteHandle(28, opacityMask);
            b.WriteHandle(32, transform);
            b.WriteHandle(36, guidelineSet);
            b.WriteUInt32(40, (uint)edgeMode);
            b.WriteUInt32(44, (uint)scaling);
            b.WriteUInt32(48, (uint)ctHint);
            for (int i = 0; i < n; i++) b.WriteHandle(52 + i * 4, children[i]);
            return b.ToArray();
        }

        // ================= 其它 =================

        // MILCMD_GUIDELINESET: GuidelinesXSize@8 GuidelinesYSize@12 IsDynamic@16，后接 X 再 Y 个 double
        public static byte[] GuidelineSet(DUCE.ResourceHandle h, double[] x, double[] y, bool isDynamic)
        {
            int nx = x?.Length ?? 0, ny = y?.Length ?? 0;
            var b = new MilCommandBuffer(20 + (nx + ny) * 8);
            WriteHeader(b, MilCmd.MilCmdGuidelineSet, h);
            b.WriteUInt32(8, (uint)(nx * 8));
            b.WriteUInt32(12, (uint)(ny * 8));
            b.WriteUInt32(16, isDynamic ? 1u : 0u);
            for (int i = 0; i < nx; i++) b.WriteDouble(20 + i * 8, x[i]);
            for (int i = 0; i < ny; i++) b.WriteDouble(20 + nx * 8 + i * 8, y[i]);
            return b.ToArray();
        }

        // MILCMD_BITMAPCACHE: RenderAtScale@8 hRenderAtScaleAnimations@16 SnapsToDevicePixels@20 EnableClearType@24
        public static byte[] BitmapCache(DUCE.ResourceHandle h, double renderAtScale, bool snaps, bool clearType)
        {
            var b = new MilCommandBuffer(28);
            WriteHeader(b, MilCmd.MilCmdBitmapCache, h);
            b.WriteDouble(8, renderAtScale);
            b.WriteUInt32(20, snaps ? 1u : 0u);
            b.WriteUInt32(24, clearType ? 1u : 0u);
            return b.ToArray();
        }

        // MILCMD_BLUREFFECT: Radius@8 hRadiusAnimations@16 KernelType@20 RenderingBias@24
        public static byte[] BlurEffect(DUCE.ResourceHandle h, double radius, MilKernelType kernel, MilRenderingBias bias)
        {
            var b = new MilCommandBuffer(28);
            WriteHeader(b, MilCmd.MilCmdBlurEffect, h);
            b.WriteDouble(8, radius);
            b.WriteUInt32(20, (uint)kernel);
            b.WriteUInt32(24, (uint)bias);
            return b.ToArray();
        }

        // MILCMD_DROPSHADOWEFFECT: 80 bytes
        public static byte[] DropShadowEffect(DUCE.ResourceHandle h, double shadowDepth, MilColorF color,
            double direction, double opacity, double blurRadius, MilRenderingBias bias)
        {
            var b = new MilCommandBuffer(80);
            WriteHeader(b, MilCmd.MilCmdDropShadowEffect, h);
            b.WriteDouble(8, shadowDepth);
            b.WriteColor(16, color);
            b.WriteDouble(32, direction);
            b.WriteDouble(40, opacity);
            b.WriteDouble(48, blurRadius);
            b.WriteUInt32(76, (uint)bias);
            return b.ToArray();
        }

        // MILCMD_GLYPHRUN_CREATE: 76 bytes 头（见 MilCommandStructs.MILCMD_GLYPHRUN_CREATE）
        public static byte[] GlyphRunCreate(
            DUCE.ResourceHandle h, ulong pFont, ushort flags, MilPoint2F origin, float muSize,
            MilRect bounds, ushort[] glyphIndices, ushort bidiLevel, ushort measuringMethod)
        {
            int n = glyphIndices?.Length ?? 0;
            var b = new MilCommandBuffer(76 + n * 2);
            WriteHeader(b, MilCmd.MilCmdGlyphRunCreate, h);
            b.WriteUInt64(8, pFont);
            b.WriteUInt16(16, flags);
            b.WritePoint2F(20, origin);
            b.WriteSingle(28, muSize);
            b.WriteRect(32, bounds);
            b.WriteUInt16(64, (ushort)n);
            b.WriteUInt16(68, bidiLevel);
            b.WriteUInt16(72, measuringMethod);
            for (int i = 0; i < n; i++) b.WriteUInt16(76 + i * 2, glyphIndices[i]);
            return b.ToArray();
        }

        // ================= 3D 视觉树 (0x29–0x30) =================
        //
        // 偏移常量逐条誊自上游 Generated/wgx_commands.cs（3D 资源段之前那一段）。
        // 与结构体的双向校验由 Command3DTests 的 round-trip 负责。

        // MILCMD_VIEWPORT3DVISUAL_SETCAMERA: hCamera@8
        public static byte[] Viewport3DVisualSetCamera(DUCE.ResourceHandle h, DUCE.ResourceHandle hCamera)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdViewport3DVisualSetCamera, h);
            b.WriteHandle(8, hCamera);
            return b.ToArray();
        }

        // MILCMD_VIEWPORT3DVISUAL_SETVIEWPORT: Viewport@8（4 × double = 32）
        public static byte[] Viewport3DVisualSetViewport(DUCE.ResourceHandle h, MilRect viewport)
        {
            var b = new MilCommandBuffer(40);
            WriteHeader(b, MilCmd.MilCmdViewport3DVisualSetViewport, h);
            b.WriteRect(8, viewport);
            return b.ToArray();
        }

        // MILCMD_VIEWPORT3DVISUAL_SET3DCHILD: hChild@8
        public static byte[] Viewport3DVisualSet3DChild(DUCE.ResourceHandle h, DUCE.ResourceHandle hChild)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdViewport3DVisualSet3DChild, h);
            b.WriteHandle(8, hChild);
            return b.ToArray();
        }

        // MILCMD_VISUAL3D_SETCONTENT: hContent@8
        public static byte[] Visual3DSetContent(DUCE.ResourceHandle h, DUCE.ResourceHandle hContent)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdVisual3DSetContent, h);
            b.WriteHandle(8, hContent);
            return b.ToArray();
        }

        // MILCMD_VISUAL3D_SETTRANSFORM: hTransform@8
        public static byte[] Visual3DSetTransform(DUCE.ResourceHandle h, DUCE.ResourceHandle hTransform)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdVisual3DSetTransform, h);
            b.WriteHandle(8, hTransform);
            return b.ToArray();
        }

        // MILCMD_VISUAL3D_REMOVEALLCHILDREN: 无载荷（8 字节命令头）
        public static byte[] Visual3DRemoveAllChildren(DUCE.ResourceHandle h)
        {
            var b = new MilCommandBuffer(8);
            WriteHeader(b, MilCmd.MilCmdVisual3DRemoveAllChildren, h);
            return b.ToArray();
        }

        // MILCMD_VISUAL3D_REMOVECHILD: hChild@8
        public static byte[] Visual3DRemoveChild(DUCE.ResourceHandle h, DUCE.ResourceHandle hChild)
        {
            var b = new MilCommandBuffer(12);
            WriteHeader(b, MilCmd.MilCmdVisual3DRemoveChild, h);
            b.WriteHandle(8, hChild);
            return b.ToArray();
        }

        // MILCMD_VISUAL3D_INSERTCHILDAT: hChild@8 index@12
        public static byte[] Visual3DInsertChildAt(DUCE.ResourceHandle h, DUCE.ResourceHandle hChild, uint index)
        {
            var b = new MilCommandBuffer(16);
            WriteHeader(b, MilCmd.MilCmdVisual3DInsertChildAt, h);
            b.WriteHandle(8, hChild);
            b.WriteUInt32(12, index);
            return b.ToArray();
        }

        // ================= 3D 资源 (0x57–0x6b) =================
        //
        // 偏移常量逐条誊自上游 Generated/wgx_commands.cs:431-676。
        // 与结构体的双向校验由 Command3DTests 的 round-trip 负责。

        // MILCMD_AXISANGLEROTATION3D: angle@8 axis@16 hAxisAnimations@28 hAngleAnimations@32
        public static byte[] AxisAngleRotation3D(DUCE.ResourceHandle h, double angle, MilPoint3F axis,
            DUCE.ResourceHandle hAxisAnim = default, DUCE.ResourceHandle hAngleAnim = default)
        {
            var b = new MilCommandBuffer(36);
            WriteHeader(b, MilCmd.MilCmdAxisAngleRotation3D, h);
            b.WriteDouble(8, angle);
            b.WritePoint3F(16, axis);
            b.WriteHandle(28, hAxisAnim);
            b.WriteHandle(32, hAngleAnim);
            return b.ToArray();
        }

        // MILCMD_QUATERNIONROTATION3D: quaternion@8 hQuaternionAnimations@24
        public static byte[] QuaternionRotation3D(DUCE.ResourceHandle h, MilQuaternionF q,
            DUCE.ResourceHandle hAnim = default)
        {
            var b = new MilCommandBuffer(28);
            WriteHeader(b, MilCmd.MilCmdQuaternionRotation3D, h);
            b.WriteQuaternion(8, q);
            b.WriteHandle(24, hAnim);
            return b.ToArray();
        }

        // MILCMD_PERSPECTIVECAMERA: near@8 far@16 fov@24 position@32 htransform@44
        //   lookDirection@48 hNearAnim@60 upDirection@64 hFarAnim@76
        //   hPosAnim@80 hLookAnim@84 hUpAnim@88 hFovAnim@92
        public static byte[] PerspectiveCamera(DUCE.ResourceHandle h,
            double nearPlaneDistance, double farPlaneDistance, double fieldOfView,
            MilPoint3F position, MilPoint3F lookDirection, MilPoint3F upDirection,
            DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(96);
            WriteHeader(b, MilCmd.MilCmdPerspectiveCamera, h);
            b.WriteDouble(8, nearPlaneDistance);
            b.WriteDouble(16, farPlaneDistance);
            b.WriteDouble(24, fieldOfView);
            b.WritePoint3F(32, position);
            b.WriteHandle(44, hTransform);
            b.WritePoint3F(48, lookDirection);
            b.WritePoint3F(64, upDirection);
            return b.ToArray();
        }

        // MILCMD_ORTHOGRAPHICCAMERA: 同 PerspectiveCamera，但 fov 位换成 width，末句柄是 hWidthAnimations
        public static byte[] OrthographicCamera(DUCE.ResourceHandle h,
            double nearPlaneDistance, double farPlaneDistance, double width,
            MilPoint3F position, MilPoint3F lookDirection, MilPoint3F upDirection,
            DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(96);
            WriteHeader(b, MilCmd.MilCmdOrthographicCamera, h);
            b.WriteDouble(8, nearPlaneDistance);
            b.WriteDouble(16, farPlaneDistance);
            b.WriteDouble(24, width);
            b.WritePoint3F(32, position);
            b.WriteHandle(44, hTransform);
            b.WritePoint3F(48, lookDirection);
            b.WritePoint3F(64, upDirection);
            return b.ToArray();
        }

        // MILCMD_MATRIXCAMERA: viewMatrix@8 (64) projectionMatrix@72 (64) htransform@136
        public static byte[] MatrixCamera(DUCE.ResourceHandle h, MilMatrix4x4F view,
            MilMatrix4x4F projection, DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(140);
            WriteHeader(b, MilCmd.MilCmdMatrixCamera, h);
            b.WriteMatrix4x4F(8, view);
            b.WriteMatrix4x4F(72, projection);
            b.WriteHandle(136, hTransform);
            return b.ToArray();
        }

        // MILCMD_MODEL3DGROUP: htransform@8 ChildrenSize@12，后接 N 个句柄@16
        public static byte[] Model3DGroup(DUCE.ResourceHandle h, DUCE.ResourceHandle[] children,
            DUCE.ResourceHandle hTransform = default)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(16 + n * 4);
            WriteHeader(b, MilCmd.MilCmdModel3DGroup, h);
            b.WriteHandle(8, hTransform);
            b.WriteUInt32(12, (uint)(n * 4));
            for (int i = 0; i < n; i++) b.WriteHandle(16 + i * 4, children[i]);
            return b.ToArray();
        }

        // MILCMD_AMBIENTLIGHT: color@8 htransform@24 hColorAnimations@28
        public static byte[] AmbientLight(DUCE.ResourceHandle h, MilColorF color,
            DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(32);
            WriteHeader(b, MilCmd.MilCmdAmbientLight, h);
            b.WriteColor(8, color);
            b.WriteHandle(24, hTransform);
            return b.ToArray();
        }

        // MILCMD_DIRECTIONALLIGHT: color@8 direction@24 htransform@36 hColorAnim@40 hDirectionAnim@44
        public static byte[] DirectionalLight(DUCE.ResourceHandle h, MilColorF color, MilPoint3F direction,
            DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(48);
            WriteHeader(b, MilCmd.MilCmdDirectionalLight, h);
            b.WriteColor(8, color);
            b.WritePoint3F(24, direction);
            b.WriteHandle(36, hTransform);
            return b.ToArray();
        }

        // MILCMD_POINTLIGHT: color@8 range@24 const@32 linear@40 quad@48 position@56 htransform@68
        public static byte[] PointLight(DUCE.ResourceHandle h, MilColorF color,
            double range, double constantAttenuation, double linearAttenuation, double quadraticAttenuation,
            MilPoint3F position, DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(96);
            WriteHeader(b, MilCmd.MilCmdPointLight, h);
            b.WriteColor(8, color);
            b.WriteDouble(24, range);
            b.WriteDouble(32, constantAttenuation);
            b.WriteDouble(40, linearAttenuation);
            b.WriteDouble(48, quadraticAttenuation);
            b.WritePoint3F(56, position);
            b.WriteHandle(68, hTransform);
            return b.ToArray();
        }

        // MILCMD_SPOTLIGHT: color@8 range@24 const@32 linear@40 quad@48
        //   outerCone@56 innerCone@64 position@72 htransform@84 direction@88
        public static byte[] SpotLight(DUCE.ResourceHandle h, MilColorF color,
            double range, double constantAttenuation, double linearAttenuation, double quadraticAttenuation,
            double outerConeAngle, double innerConeAngle,
            MilPoint3F position, MilPoint3F direction, DUCE.ResourceHandle hTransform = default)
        {
            var b = new MilCommandBuffer(136);
            WriteHeader(b, MilCmd.MilCmdSpotLight, h);
            b.WriteColor(8, color);
            b.WriteDouble(24, range);
            b.WriteDouble(32, constantAttenuation);
            b.WriteDouble(40, linearAttenuation);
            b.WriteDouble(48, quadraticAttenuation);
            b.WriteDouble(56, outerConeAngle);
            b.WriteDouble(64, innerConeAngle);
            b.WritePoint3F(72, position);
            b.WriteHandle(84, hTransform);
            b.WritePoint3F(88, direction);
            return b.ToArray();
        }

        // MILCMD_GEOMETRYMODEL3D: htransform@8 hgeometry@12 hmaterial@16 hbackMaterial@20
        public static byte[] GeometryModel3D(DUCE.ResourceHandle h,
            DUCE.ResourceHandle hTransform, DUCE.ResourceHandle hGeometry,
            DUCE.ResourceHandle hMaterial, DUCE.ResourceHandle hBackMaterial)
        {
            var b = new MilCommandBuffer(24);
            WriteHeader(b, MilCmd.MilCmdGeometryModel3D, h);
            b.WriteHandle(8, hTransform);
            b.WriteHandle(12, hGeometry);
            b.WriteHandle(16, hMaterial);
            b.WriteHandle(20, hBackMaterial);
            return b.ToArray();
        }

        // MILCMD_MESHGEOMETRY3D: 四个 Size@8..20，尾部按顺序跟四段数组：
        //   Positions(12×N) / Normals(12×N) / TextureCoordinates(16×N) / TriangleIndices(4×N)
        public static byte[] MeshGeometry3D(DUCE.ResourceHandle h,
            MilPoint3F[] positions, MilPoint3F[] normals,
            MilPoint[] textureCoordinates, int[] triangleIndices)
        {
            int np = positions?.Length ?? 0;
            int nn = normals?.Length ?? 0;
            int nt = textureCoordinates?.Length ?? 0;
            int ni = triangleIndices?.Length ?? 0;

            int oP = 24;
            int oN = oP + np * 12;
            int oT = oN + nn * 12;
            int oI = oT + nt * 16;

            var b = new MilCommandBuffer(oI + ni * 4);
            WriteHeader(b, MilCmd.MilCmdMeshGeometry3D, h);
            b.WriteUInt32(8, (uint)(np * 12));
            b.WriteUInt32(12, (uint)(nn * 12));
            b.WriteUInt32(16, (uint)(nt * 16));
            b.WriteUInt32(20, (uint)(ni * 4));

            for (int i = 0; i < np; i++) b.WritePoint3F(oP + i * 12, positions[i]);
            for (int i = 0; i < nn; i++) b.WritePoint3F(oN + i * 12, normals[i]);
            for (int i = 0; i < nt; i++) b.WritePoint(oT + i * 16, textureCoordinates[i]);
            for (int i = 0; i < ni; i++) b.WriteInt32(oI + i * 4, triangleIndices[i]);
            return b.ToArray();
        }

        // MILCMD_MATERIALGROUP: ChildrenSize@8，后接 N 个句柄@12
        public static byte[] MaterialGroup(DUCE.ResourceHandle h, DUCE.ResourceHandle[] children)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(12 + n * 4);
            WriteHeader(b, MilCmd.MilCmdMaterialGroup, h);
            b.WriteUInt32(8, (uint)(n * 4));
            for (int i = 0; i < n; i++) b.WriteHandle(12 + i * 4, children[i]);
            return b.ToArray();
        }

        // MILCMD_DIFFUSEMATERIAL: color@8 ambientColor@24 hbrush@40
        public static byte[] DiffuseMaterial(DUCE.ResourceHandle h, MilColorF color,
            MilColorF ambientColor, DUCE.ResourceHandle hBrush = default)
        {
            var b = new MilCommandBuffer(44);
            WriteHeader(b, MilCmd.MilCmdDiffuseMaterial, h);
            b.WriteColor(8, color);
            b.WriteColor(24, ambientColor);
            b.WriteHandle(40, hBrush);
            return b.ToArray();
        }

        // MILCMD_SPECULARMATERIAL: color@8 specularPower@24 hbrush@32
        public static byte[] SpecularMaterial(DUCE.ResourceHandle h, MilColorF color,
            double specularPower, DUCE.ResourceHandle hBrush = default)
        {
            var b = new MilCommandBuffer(36);
            WriteHeader(b, MilCmd.MilCmdSpecularMaterial, h);
            b.WriteColor(8, color);
            b.WriteDouble(24, specularPower);
            b.WriteHandle(32, hBrush);
            return b.ToArray();
        }

        // MILCMD_EMISSIVEMATERIAL: color@8 hbrush@24
        public static byte[] EmissiveMaterial(DUCE.ResourceHandle h, MilColorF color,
            DUCE.ResourceHandle hBrush = default)
        {
            var b = new MilCommandBuffer(28);
            WriteHeader(b, MilCmd.MilCmdEmissiveMaterial, h);
            b.WriteColor(8, color);
            b.WriteHandle(24, hBrush);
            return b.ToArray();
        }

        // MILCMD_TRANSFORM3DGROUP: ChildrenSize@8，后接 N 个句柄@12
        public static byte[] Transform3DGroup(DUCE.ResourceHandle h, DUCE.ResourceHandle[] children)
        {
            int n = children?.Length ?? 0;
            var b = new MilCommandBuffer(12 + n * 4);
            WriteHeader(b, MilCmd.MilCmdTransform3DGroup, h);
            b.WriteUInt32(8, (uint)(n * 4));
            for (int i = 0; i < n; i++) b.WriteHandle(12 + i * 4, children[i]);
            return b.ToArray();
        }

        // MILCMD_TRANSLATETRANSFORM3D: offsetX@8 offsetY@16 offsetZ@24 + 3 anim handles@32..40
        public static byte[] TranslateTransform3D(DUCE.ResourceHandle h, double x, double y, double z)
        {
            var b = new MilCommandBuffer(44);
            WriteHeader(b, MilCmd.MilCmdTranslateTransform3D, h);
            b.WriteDouble(8, x);
            b.WriteDouble(16, y);
            b.WriteDouble(24, z);
            return b.ToArray();
        }

        // MILCMD_SCALETRANSFORM3D: scaleXYZ@8..24 centerXYZ@32..48 + 6 anim handles@56..76
        public static byte[] ScaleTransform3D(DUCE.ResourceHandle h,
            double sx, double sy, double sz, double cx, double cy, double cz)
        {
            var b = new MilCommandBuffer(80);
            WriteHeader(b, MilCmd.MilCmdScaleTransform3D, h);
            b.WriteDouble(8, sx);
            b.WriteDouble(16, sy);
            b.WriteDouble(24, sz);
            b.WriteDouble(32, cx);
            b.WriteDouble(40, cy);
            b.WriteDouble(48, cz);
            return b.ToArray();
        }

        // MILCMD_ROTATETRANSFORM3D: centerXYZ@8..24 + 3 anim handles@32..40 + hrotation@44
        public static byte[] RotateTransform3D(DUCE.ResourceHandle h,
            double cx, double cy, double cz, DUCE.ResourceHandle hRotation)
        {
            var b = new MilCommandBuffer(48);
            WriteHeader(b, MilCmd.MilCmdRotateTransform3D, h);
            b.WriteDouble(8, cx);
            b.WriteDouble(16, cy);
            b.WriteDouble(24, cz);
            b.WriteHandle(44, hRotation);
            return b.ToArray();
        }

        // MILCMD_MATRIXTRANSFORM3D: matrix@8 (64)
        public static byte[] MatrixTransform3D(DUCE.ResourceHandle h, MilMatrix4x4F m)
        {
            var b = new MilCommandBuffer(72);
            WriteHeader(b, MilCmd.MilCmdMatrixTransform3D, h);
            b.WriteMatrix4x4F(8, m);
            return b.ToArray();
        }
    }
}
