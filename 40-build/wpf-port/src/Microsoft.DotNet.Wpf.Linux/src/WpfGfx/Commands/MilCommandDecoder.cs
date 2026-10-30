// Licensed to the .NET Foundation under one or more agreements.
//
// 命令解码器。
//
// 固定部分直接用 MemoryMarshal.Read<T> 读 MilCommandStructs.cs 里的线格结构体
// （那份结构体是上游 Generated/wgx_commands.cs 的逐字复制），
// 变长尾部按上游 *Resource.cs 的 MarshalTo 实现解析：
//   - TransformGroup / GeometryGroup / DrawingGroup 的 ChildrenSize = N × sizeof(ResourceHandle)(4)
//   - DashStyle 的 DashesSize = N × sizeof(double)(8)
//   - GuidelineSet 的 GuidelinesXSize/YSize = N × sizeof(double)(8)
//   - LinearGradient/RadialGradient 的 GradientStopsSize = N × sizeof(MIL_GRADIENTSTOP)(24)
//   - VisualSetGuidelineCollection 尾部 = (CountX + CountY) × sizeof(float)(4)
//   - PathGeometry 的 FiguresSize = 序列化几何体原始字节数
//   - RenderData 的 cbData = 指令流原始字节数

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Interop;

namespace WpfGfx.Linux.Commands
{
    internal static class MilCommandDecoder
    {
        public const int HeaderTypeOffset = 0;
        public const int HeaderHandleOffset = 4;
        public const int MinCommandSize = 8;

        public static MilCmd ReadType(ReadOnlySpan<byte> cmd) =>
            (MilCmd)BinaryPrimitives.ReadInt32LittleEndian(cmd.Slice(HeaderTypeOffset, 4));

        public static DUCE.ResourceHandle ReadHandle(ReadOnlySpan<byte> cmd) =>
            new DUCE.ResourceHandle(BinaryPrimitives.ReadUInt32LittleEndian(cmd.Slice(HeaderHandleOffset, 4)));

        /// <summary>把命令的固定部分按线格结构体读出。调用方必须先用 FixedSize 校验长度。</summary>
        public static T ReadFixed<T>(ReadOnlySpan<byte> cmd) where T : struct =>
            MemoryMarshal.Read<T>(cmd.Slice(0, Math.Min(cmd.Length, Marshal.SizeOf<T>())));

        public static DUCE.ResourceHandle ReadHandleAt(ReadOnlySpan<byte> cmd, int offset) =>
            new DUCE.ResourceHandle(BinaryPrimitives.ReadUInt32LittleEndian(cmd.Slice(offset, 4)));

        public static double ReadDoubleAt(ReadOnlySpan<byte> cmd, int offset) =>
            BinaryPrimitives.ReadDoubleLittleEndian(cmd.Slice(offset, 8));

        public static float ReadSingleAt(ReadOnlySpan<byte> cmd, int offset) =>
            BinaryPrimitives.ReadSingleLittleEndian(cmd.Slice(offset, 4));

        /// <summary>读尾部句柄数组（每个 4 字节）。</summary>
        public static DUCE.ResourceHandle[] ReadHandles(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 4;
            var result = new DUCE.ResourceHandle[n];
            for (int i = 0; i < n; i++) result[i] = ReadHandleAt(cmd, offset + i * 4);
            return result;
        }

        /// <summary>读尾部 double 数组。</summary>
        public static double[] ReadDoubles(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 8;
            var result = new double[n];
            for (int i = 0; i < n; i++) result[i] = ReadDoubleAt(cmd, offset + i * 8);
            return result;
        }

        /// <summary>读尾部 float 数组。</summary>
        public static float[] ReadFloats(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 4;
            var result = new float[n];
            for (int i = 0; i < n; i++) result[i] = ReadSingleAt(cmd, offset + i * 4);
            return result;
        }

        /// <summary>读尾部 MIL_GRADIENTSTOP 数组（每个 24 字节：double Position + MilColorF Color）。</summary>
        public static MilGradientStop[] ReadGradientStops(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / MilGradientStop.SizeInBytes;
            var result = new MilGradientStop[n];
            for (int i = 0; i < n; i++)
            {
                int o = offset + i * MilGradientStop.SizeInBytes;
                result[i] = new MilGradientStop(ReadDoubleAt(cmd, o), ReadColorAt(cmd, o + 8));
            }
            return result;
        }

        public static MilColorF ReadColorAt(ReadOnlySpan<byte> cmd, int offset) => new MilColorF(
            ReadSingleAt(cmd, offset),
            ReadSingleAt(cmd, offset + 4),
            ReadSingleAt(cmd, offset + 8),
            ReadSingleAt(cmd, offset + 12));

        // ---- 3D 用（0x57–0x6b） ----

        /// <summary>读尾部 MilPoint3F 数组（每个 12 字节）。</summary>
        public static MilPoint3F[] ReadPoint3Fs(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 12;
            var result = new MilPoint3F[n];
            for (int i = 0; i < n; i++)
            {
                int o = offset + i * 12;
                result[i] = new MilPoint3F(ReadSingleAt(cmd, o), ReadSingleAt(cmd, o + 4), ReadSingleAt(cmd, o + 8));
            }
            return result;
        }

        /// <summary>读尾部 MilPoint（双精度 2D 点）数组（每个 16 字节）。</summary>
        public static MilPoint[] ReadPoints(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 16;
            var result = new MilPoint[n];
            for (int i = 0; i < n; i++)
            {
                int o = offset + i * 16;
                result[i] = new MilPoint(ReadDoubleAt(cmd, o), ReadDoubleAt(cmd, o + 8));
            }
            return result;
        }

        /// <summary>读尾部 int32 数组（每个 4 字节）。</summary>
        public static int[] ReadInt32s(ReadOnlySpan<byte> cmd, int offset, int byteCount)
        {
            int n = byteCount / 4;
            var result = new int[n];
            for (int i = 0; i < n; i++) result[i] = BinaryPrimitives.ReadInt32LittleEndian(cmd.Slice(offset + i * 4, 4));
            return result;
        }

        /// <summary>读尾部 ushort 数组。</summary>
        public static ushort[] ReadUInt16Array(ReadOnlySpan<byte> cmd, int offset, int count)
        {
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(cmd.Slice(offset + i * 2, 2));
            return result;
        }
    }

    /// <summary>
    /// RenderData 指令流的解析器。
    ///
    /// 指令流格式（上游 CMilDataStreamReader 的注释 + RenderData.cs WriteDataRecord）：
    ///     [ Size : int32 ][ Id : MilCmd ][ data : Size-8 字节 ]
    /// Size 含头自身，且必须是 8 的倍数（WriteDataRecord 有 Debug.Assert）。
    /// data 部分用 PresentationCore/System/Windows/Media/Generated/RenderData.cs 里的
    /// 托管结构体解释——**这些结构体不含 type 字段**（type 已在 Id 里）。
    /// </summary>
    internal static class RenderDataStream
    {
        public const int RecordHeaderSize = 8;   // int Size + MilCmd Id

        public static int RecordSize(ReadOnlySpan<byte> s, int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset, 4));

        public static MilDrawCommand RecordId(ReadOnlySpan<byte> s, int offset) =>
            (MilDrawCommand)BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset + 4, 4));

        /// <summary>把整条指令流切成 (Id, data) 序列。流损坏时抛 FormatException。</summary>
        public static void Enumerate(ReadOnlySpan<byte> stream, Action<MilDrawCommand, ReadOnlySpan<byte>> visit)
        {
            int offset = 0;
            while (offset < stream.Length)
            {
                if (offset + RecordHeaderSize > stream.Length)
                    throw new FormatException($"RenderData 指令流在偏移 {offset} 处被截断（不足 8 字节头）");

                int size = RecordSize(stream, offset);
                MilDrawCommand id = RecordId(stream, offset);

                if (size < RecordHeaderSize || size % 4 != 0 || offset + size > stream.Length)
                    throw new FormatException($"RenderData 记录头非法：offset={offset} size={size}");

                visit(id, stream.Slice(offset + RecordHeaderSize, size - RecordHeaderSize));
                offset += size;
            }
        }
    }
}
