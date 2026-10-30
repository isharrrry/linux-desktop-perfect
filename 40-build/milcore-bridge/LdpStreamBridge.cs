// 流桥：把 milcore 的 `MilStreamObject`（`MILCreateStreamFromStreamDescriptor` 造出来的
// 那个句柄）暴露成"能读字节"的扁平导出，供 `libwpfwic.so` 的 `CreateDecoderFromStream` 使用。
//
// 【为什么需要这一层】WPF 的 `BitmapDecoder` 把图片数据包成 `IStream*` 传给 WIC，而那个
// `IStream*` 在本工程的实现里其实是 **Mil 设备对象表的句柄**（不是 COM vtable）——
// 见 `MilNative.MILCreateStreamFromStreamDescriptor` 的注释。libwpfwic 是 C 代码、拿不到托管对象，
// 于是这里提供 `LdpMilStreamRead/Seek/Size` 三个扁平函数当作两侧之间的约定。
//
// 【槽位约定】**不手抄**：`StreamAsIStream.StreamDescriptor` 的字段顺序是
//   pfnDispose(0) pfnRead(1) pfnSeek(2) pfnStat(3) pfnWrite(4) …（见上游 StreamAsIStream.cs:33-39），
//   与 `MilNative.StreamWriteCallbackOffset = 32`（= slot 4）互相印证。

using System;
using System.Runtime.InteropServices;
using WpfGfx.Linux.Interop;

namespace MilBridge
{
    public static unsafe class LdpStreamBridge
    {
        private const int SLOT_READ = 1;
        private const int SLOT_SEEK = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ReadDelegate(IntPtr pSD, byte* buffer, uint cb, out uint cbRead);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int SeekDelegate(IntPtr pSD, long offset, uint origin, out long newPos);

        private static MilStreamObject StreamOf(void* handle)
        {
            if (handle == null) return null;
            MilDeviceObject obj = MilDeviceObjectTable.Resolve((IntPtr)handle);
            if (obj == null) return null;
            MilStreamObject ms = obj.Payload as MilStreamObject;
            if (ms == null || ms.Descriptor == IntPtr.Zero) return null;
            return ms;
        }

        /// <summary>从流里读最多 cb 字节；返回 HRESULT，读到的字节数写进 cbRead。</summary>
        [UnmanagedCallersOnly(EntryPoint = "LdpMilStreamRead")]
        public static int LdpMilStreamRead(void* handle, byte* buffer, uint cb, uint* cbRead)
        {
            if (cbRead != null) *cbRead = 0;
            if (buffer == null) return unchecked((int)0x80070057);   // E_INVALIDARG
            MilStreamObject ms = StreamOf(handle);
            if (ms == null) return unchecked((int)0x80070057);
            IntPtr pfn = Marshal.ReadIntPtr(ms.Descriptor, SLOT_READ * IntPtr.Size);
            if (pfn == IntPtr.Zero) return unchecked((int)0x80004001); // E_NOTIMPL
            var del = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(pfn);
            uint read = 0;
            int hr = del(ms.Descriptor, buffer, cb, out read);
            if (cbRead != null) *cbRead = read;
            return hr;
        }

        /// <summary>
        /// 用一段 ICC profile 字节登记一个「颜色上下文」，返回它的句柄。
        /// 供 libwpfwic 的 `GetColorContexts` 使用：WPF 拿到句柄后会调
        /// `IWICColorContext_GetProfileBytes/GetType`（这两个导出在 bridge 里），
        /// 所以句柄必须登记进 `MilColorContextTable` 才查得到 —— 这正是本函数存在的理由。
        /// type: 0=Uninitialized 1=ExifColorSpace 2=Profile（与 WIC 的 WICColorContextType 同序）。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "LdpMilColorContextFromProfile")]
        public static IntPtr LdpMilColorContextFromProfile(byte* profile, uint length, uint type)
        {
            // length == 0 合法：`WICCreateColorContext` 的语义就是"先造一个空上下文，
            // 之后由 `IWICColorContext_InitializeFromMemory` 填 profile"。
            if (profile == null || length == 0) {
                var empty = new MilColorContext
                {
                    ProfileBytes = Array.Empty<byte>(),
                    Type = MilWicColorContextType.WICColorContextUninitialized,
                    ExifColorSpace = 0,
                };
                return MilColorContextTable.Register(empty);
            }
            byte[] bytes = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)profile, bytes, 0, (int)length);
            var ctx = new MilColorContext
            {
                ProfileBytes = bytes,
                Type = type == 2 ? MilWicColorContextType.WICColorContextProfile
                                 : (type == 1 ? MilWicColorContextType.WICColorContextExifColorSpace
                                              : MilWicColorContextType.WICColorContextUninitialized),
                ExifColorSpace = 0,
            };
            return MilColorContextTable.Register(ctx);
        }

        /// <summary>
        /// 给一个已登记的「颜色上下文」填 profile（对应 WIC 的
        /// `IWICColorContext_InitializeFromMemory` —— 那个语义是**填充已有对象**，不是创建）。
        /// 返回 S_OK(0) / E_HANDLE（句柄不认识）/ E_INVALIDARG。
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "LdpMilColorContextSetProfile")]
        public static int LdpMilColorContextSetProfile(IntPtr handle, byte* profile, uint length, uint type)
        {
            if (handle == IntPtr.Zero) return unchecked((int)0x80070057);
            MilColorContext ctx = MilColorContextTable.Resolve(handle);
            if (ctx == null) return unchecked((int)0x80070006);   // E_HANDLE
            if (profile == null || length == 0) return unchecked((int)0x80070057);
            byte[] bytes = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)profile, bytes, 0, (int)length);
            ctx.ProfileBytes = bytes;
            ctx.Type = type == 2 ? MilWicColorContextType.WICColorContextProfile
                                 : (type == 1 ? MilWicColorContextType.WICColorContextExifColorSpace
                                              : MilWicColorContextType.WICColorContextUninitialized);
            return 0;
        }

        /// <summary>在流里定位；origin 用 COM 的 STREAM_SEEK_* 语义（0=SET 1=CUR 2=END）。</summary>
        [UnmanagedCallersOnly(EntryPoint = "LdpMilStreamSeek")]
        public static int LdpMilStreamSeek(void* handle, long offset, uint origin, long* newPos)
        {
            if (newPos != null) *newPos = 0;
            MilStreamObject ms = StreamOf(handle);
            if (ms == null) return unchecked((int)0x80070057);
            IntPtr pfn = Marshal.ReadIntPtr(ms.Descriptor, SLOT_SEEK * IntPtr.Size);
            if (pfn == IntPtr.Zero) return unchecked((int)0x80004001);
            var del = Marshal.GetDelegateForFunctionPointer<SeekDelegate>(pfn);
            long pos = 0;
            int hr = del(ms.Descriptor, offset, origin, out pos);
            if (newPos != null) *newPos = pos;
            return hr;
        }
    }
}
