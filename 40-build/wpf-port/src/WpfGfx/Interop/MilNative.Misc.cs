// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 E/G：版本 / 引用计数 / 流与事件代理 / 反向 P/Invoke / 进程级开关 /
//   通道别名 / 资源引用计数（21 个）。
//
// 【逐条实现深度】
//   MilVersionCheck                真实现：比对 MIL_SDK_VERSION(0x200184C0)，
//                                  不等返回 WGXERR_UNSUPPORTEDVERSION（apifunc.cpp:38）
//   MILCreateFactory / MILAddRef / MILRelease / MILQueryInterface
//                                  真实现：引用计数对象表 + IID_IUnknown 特判，
//                                  其余 IID 返回 E_NOINTERFACE（我们的对象不实现 COM 接口）
//   MILCreateEventProxy            身份映射：登记描述符指针，下发句柄
//   MILCreateStreamFromStreamDescriptor
//                                  身份映射 + 真数据面（MILIStreamWrite 真的写进内存流）
//   MilCreateReversePInvokeWrapper / MilReleasePInvokePtrBlocking
//                                  身份映射：本工程的"原生层"就是托管，函数指针两侧同值，
//                                  包装只是登记/注销（不需要 native thunk）
//   GetNextPerfElementId           真实现：进程内单调计数器
//   WpfGfx_SetDisableBoundsCheckProtection / RenderOptions_*
//                                  真实现：进程级开关的存取（值可往返）
//   MILUpdateSystemParametersInfo  S_OK no-op + 计数（Linux 没有 SystemParametersInfo）
//   MilChannel_CloseBatch / MilChannel_CommitChannel
//                                  显式别名：语义与既有的
//                                  MilConnection_CloseBatch / MilConnection_CommitChannel 完全相同。
//                                  存在的理由是**导出名**——上游这两个 EntryPoint 叫
//                                  MilChannel_*，而 C# 方法名叫 MilConnection_*；
//                                  补齐导出名才能让 .so 的导出表与托管需求集一一对应。
//   MilResource_GetRefCountOnChannel  真实现：查资源表的引用计数
//
// 【关于 SafeMILHandle】
//   上游这些函数的参数是 SafeMILHandle（SafeHandle 的子类）。本工程按 MilNative.cs
//   已定的契约把它们退化成 IntPtr——句柄由 MilDeviceObjectTable / MilPixelBufferTable
//   下发，是进程内逻辑句柄，不是内核对象，不能跨进程、不能 CloseHandle。

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Interop
{
    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  版本
        // ==================================================================

        /// <summary>
        /// 校验调用方编译时的 MIL SDK 版本是否与本地一致。
        /// S_OK = 一致；WGXERR_UNSUPPORTEDVERSION(0x88982F0B) = 不一致。
        /// </summary>
        public static int MilVersionCheck(uint uiCallerMilSdkVersion)
        {
            return uiCallerMilSdkVersion == MilErrors.MilSdkVersion
                ? HResult.S_OK
                : MilErrors.WGXERR_UNSUPPORTEDVERSION;
        }

        // ==================================================================
        //  COM 式引用计数
        // ==================================================================

        /// <summary>
        /// AddRef，返回新的引用计数。句柄为空或未登记返回 0
        /// （上游对空指针不会调用，因为 SafeMILHandle 保证非空）。
        ///
        /// 【M7c 轨道 B：为什么这里也要认"后台缓冲令牌"】
        ///   成功 QI 出来的接口指针就是**令牌本身**（`MILQueryInterface` 返回同一对象），
        ///   而上游会**直接对 WicSourceHandle 调 AddRef**（实例：`D3DImage.cs:796`
        ///   `AddRef(_softwareCopy.WicSourceHandle)`，随后把句柄塞进 DUCE 命令交给原生侧，
        ///   由原生侧负责 Release）。若这里对令牌返回 0（什么都没加），
        ///   那次迟到的 Release 就会把**唯一的**一根引用减到 0 ⇒ 对象被提前摘除、
        ///   再释放拿到 E_HANDLE。所以令牌必须和 QI/Release 走**同一张账**。
        ///   未登记的句柄仍然返回 0（fail-safe 未变）。
        /// </summary>
        public static uint MILAddRef(IntPtr pIUnkown)
        {
            uint n = MilDeviceObjectTable.AddRef(pIUnkown);
            if (n != 0) return n;

            IntPtr dev = MilBackBufferSourceTable.ResolveDevice(pIUnkown);
            return dev != IntPtr.Zero ? MilDeviceObjectTable.AddRef(dev) : 0;
        }

        /// <summary>
        /// Release。空句柄按 COM 惯例是 no-op → S_OK；
        /// 未登记的句柄 → E_HANDLE（进程内句柄表里查不到，说明调用方用错了句柄）。
        ///
        /// 【外部句柄（WIC）· T1/M7c6】WIC 句柄**转发**到所有者：`WicShim_Release`（−1，归零即回收）。
        /// 与 `MILQueryInterface` 放行时的 `WicShim_AddRef`（+1）严格配对；
        /// 完整契约（含"为什么不转发是硬失败"）见 <see cref="MilExternalHandleBridge"/> 类注释。
        /// </summary>
        public static int MILRelease(IntPtr pIUnkown)
        {
            if (pIUnkown == IntPtr.Zero) return HResult.S_OK;

            // M7c 轨道 B：像素缓冲令牌（后台缓冲）→ 别名到 MIL 设备对象，走**原有的**引用计数账。
            //
            // 【为什么这一段**必须排在外部句柄转发之前**】
            //   令牌是 **MIL 自己下发的**，按主控给的硬约束它一次都不该落到 `WicShim_*` 那条路上
            //   （两边账本各记一笔 = 单边，且"谁负责回收"会变成两个真相）。
            //   放在前面 ⇒ 这类句柄**连探测都不会去探测 WIC**（`OwnedHits`/`ProbeCount` 一个不动，
            //   这正是测试里配平断言能抓住的因果，而不只是"稳态计数恰好相等"）。
            IntPtr devHandle = MilBackBufferSourceTable.ResolveDevice(pIUnkown);
            if (devHandle != IntPtr.Zero && MilDeviceObjectTable.Resolve(devHandle) != null)
            {
                long devHr = MilDeviceObjectTable.Release(devHandle);
                // 设备对象销毁时顺手摘掉别名，避免悬挂条目（与通道句柄同一口径：不复用、不悬挂）。
                if (MilDeviceObjectTable.Resolve(devHandle) == null)
                    MilBackBufferSourceTable.Remove(pIUnkown);
                return devHr < 0 ? HResult.E_HANDLE : HResult.S_OK;
            }

            // 外部句柄（WIC）：**必须转发**到所有者，否则 shim 的 256 槽表会被解满
            // （第 257 次创建就失败 —— 是硬失败，不是缓慢泄漏）。见 MilExternalHandleBridge 类注释。
            if (MilDeviceObjectTable.Resolve(pIUnkown) == null &&
                MilExternalHandleBridge.TryReleaseExternal(pIUnkown))
            {
                return HResult.S_OK;
            }

            return MilDeviceObjectTable.Release(pIUnkown) < 0
                ? HResult.E_HANDLE
                : HResult.S_OK;
        }

        /// <summary>IID_IUnknown。</summary>
        public static readonly Guid IID_IUnknown =
            new Guid(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

        /// <summary>
        /// IID_IWICBitmapSource。
        ///
        /// **取值来源**：上游 `Common/Graphics/wgx_exports.cs:268` 的
        /// `MILGuidData.IID_IWICBitmapSource`（`WpfGfx/include/wgx_exports.cs:308` 是同一份副本）。
        /// 它是 PresentationCore 里的 `internal static readonly`，**WpfGfx.Linux 在编译期拿不到**
        /// （引用 PC 会把整个 PC 拉进 AOT 镜像，绝不可行；AOT 运行时里也没有 PC 可反射）。
        /// 所以这里是**逐字抄写 + 由测试机械核对**：`MilWicQueryInterfaceTests` 会重新解析
        /// 上游那一行并与本常量比对，上游一改、测试立刻红 —— 比"手抄一份没人管"可靠，
        /// 又不必为此在上游只读树上加生成步骤。
        /// </summary>
        public static readonly Guid IID_IWICBitmapSource =
            new Guid(0x00000120, 0xa8f2, 0x4877, 0xba, 0x0a, 0xfd, 0x2b, 0x66, 0x45, 0xfb, 0x94);

        /// <summary>
        /// QueryInterface。
        ///
        /// 【两类句柄，两种语义】
        ///
        /// ① **MIL 自己下发的对象**（`MilDeviceObjectTable` 里登记过的）：
        ///    本后端的这些对象不实现任何 COM 接口，所以
        ///      · `IID_IUnknown` → 返回同一句柄并 AddRef（COM 标准行为）
        ///      · 其它 IID       → E_NOINTERFACE
        ///    （这段语义自 M7a 起未变。）
        ///
        /// ③ **MIL 下发的"可作为位图源"的不透明令牌**（M7c 轨道 B）：后台缓冲句柄
        ///    （`MILSwDoubleBufferedBitmapGetBackBuffer` 的产物）在设备对象表里查不到，
        ///    但经 <see cref="MilBackBufferSourceTable"/> 别名到一个 `WicBitmapSource`
        ///    设备对象上 ⇒ 额外答 `IID_IWICBitmapSource`。AddRef/Release 与 ① 共用同一张表。
        ///
        /// ② **外部句柄（WIC）**：`Resolve()` 查不到、但句柄的所有者（`libwpfwic.so`）
        ///    认领它时，交给所有者回答 —— 见 <see cref="MilExternalHandleBridge"/>。
        ///
        ///    为什么必须支持它：上游 PC 在**纯上游代码路径**上就这么调 ——
        ///    `BitmapSource.set_WicSourceHandle`（`BitmapSource.cs:581-586`）与
        ///    `BitmapSourceSafeMILHandle.ComputeEstimatedSize`（`:78-82`）都对 **WIC 源句柄**
        ///    调 `MILUnknown.QueryInterface(value, IID_IWICBitmapSource, out _)`，而这个 P/Invoke
        ///    的 EntryPoint `MILQueryInterface` **落在本 .so**。
        ///    上游 Windows 上这一步能过，是因为那里的句柄是真正的 COM 指针、QI 由对象自己的
        ///    vtable 应答；Linux 上 WIC 句柄是本 shim 下发的**不透明整数**，必须由所有者回答。
        ///    （`:88-91` 原先写的"本后端下发的对象不实现任何 COM 接口"只对 ① 成立。）
        ///
        /// **fail-safe**：所有者库找不到 / 符号取不到 / 句柄不属于它 → **原样退回 E_HANDLE**，
        /// 未启用 WIC 时的行为与改动前**一字不差**。
        /// </summary>
        public static int MILQueryInterface(IntPtr pIUnknown, ref Guid guid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;

            if (pIUnknown == IntPtr.Zero) return HResult.E_INVALIDARG;

            // ---- ① MIL 自己的设备对象 ----
            // M7c 轨道 B：**像素缓冲令牌**（后台缓冲）在这个表里查不到，但它别名到一个
            //   `WicBitmapSource` 设备对象上 —— 先把它换成设备对象的句柄再走同一套账，
            //   这样"成功的 QI 会 AddRef、返回的指针一定由 MILRelease 释放"这条契约
            //   仍然由**同一张表**兑现（不落进外部句柄那条路，避免两边账本单边）。
            IntPtr resolved = pIUnknown;
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(resolved);
            if (obj == null)
            {
                IntPtr dev = MilBackBufferSourceTable.ResolveDevice(pIUnknown);
                if (dev != IntPtr.Zero)
                {
                    obj = MilDeviceObjectTable.Resolve(dev);
                    if (obj != null) resolved = dev;
                }
            }
            if (obj != null)
            {
                // 只有"可作为位图源"的对象才答 IWICBitmapSource；其余仍严格地只答 IUnknown
                // （M7a 那条语义不能被放宽成"什么 IID 都答"）。
                bool wicOk = guid == IID_IWICBitmapSource &&
                             obj.Kind == MilDeviceObjectKind.WicBitmapSource;
                if (guid != IID_IUnknown && !wicOk) return HResult.E_NOINTERFACE;

                MilDeviceObjectTable.AddRef(resolved);
                ppvObject = pIUnknown;    // COM 语义：返回**同一个对象**的接口指针
                return HResult.S_OK;
            }

            // ---- ② 外部句柄（WIC）：问它的所有者 ----
            return MilExternalHandleBridge.QueryInterface(pIUnknown, ref guid, out ppvObject);
        }

        // ==================================================================
        //  事件代理 / 流
        // ==================================================================

        /// <summary>
        /// 建事件代理。pEventProxyDescriptor 是托管侧 EventProxyDescriptor 结构体的地址
        /// （含两个函数指针 + 一个 GCHandle），本工程只登记这个地址，不下发回调——
        /// 真正的回调路由由托管适配层负责（两侧都是托管，不需要 native 代理对象）。
        /// </summary>
        public static int MILCreateEventProxy(IntPtr pEventProxyDescriptor, out IntPtr ppEventProxy)
        {
            ppEventProxy = IntPtr.Zero;
            if (pEventProxyDescriptor == IntPtr.Zero) return HResult.E_INVALIDARG;

            MilDeviceObject proxy = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.EventProxy, pEventProxyDescriptor, "event proxy");

            ppEventProxy = proxy.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// 用一个 StreamDescriptor 建 IStream 包装。
        ///
        /// 【M7c 轨道 B 修复：为什么现在要把 `pfnWrite` 取出来】见
        /// `MilStreamObject.WriteCallback` 的注释 —— 一句话：`ref` 传进来的结构体地址是**临时的**，
        /// 但里面的**函数指针**在托管委托存活期内一直有效，所以**在 Create 时取值**、
        /// 之后 `MILIStreamWrite` 直接用它把字节交还调用方。
        /// </summary>
        public static int MILCreateStreamFromStreamDescriptor(IntPtr pStreamDescriptor, out IntPtr ppStream)
        {
            ppStream = IntPtr.Zero;
            if (pStreamDescriptor == IntPtr.Zero) return HResult.E_INVALIDARG;

            // 【为什么要先判"这个地址有没有可能是个真结构体"】
            //   取值这一步**会解引用调用方给的地址**（上游原生侧同样是"进来就把整个
            //   StreamDescriptor 按值拷走"，见 exports.cpp:873-916 的 CMILStream 构造）。
            //   于是"描述符地址是假的"这件事，在原生侧表现为**进程崩**（SIGSEGV），
            //   在托管侧表现为 `AccessViolationException` —— 后者 .NET **捕获不了**
            //   （try/catch 抓不到访问违例），整个测试宿主会直接死掉，后面的用例一条都不跑。
            //   实测：Commands.Tests 的 `CreateStreamFromStreamDescriptor与IStreamWrite真写入`
            //   传的就是 `new IntPtr(0xDEF0)`（占位地址，不是真描述符）⇒ 整轮中止（510/562）。
            //
            //   所以这里挡一道**物理上不可能有效**的地址：Linux 上低于 `mmap_min_addr`
            //   （本机 65536）的地址在本进程里**无法被映射**，0xDEF0 正落在里面。
            //   挡下来之后的语义是**改动前的老行为**（没有可交付对象 ⇒ 只进台账），
            //   不是"假装成功"。
            //
            //   ⚠️ 边界（别当成万能校验）：这只挡住"物理上不可能"的那一类。
            //      地址**已映射但不是描述符**时仍然会崩 —— 与上游原生实现一致，
            //      这种输入本来就是调用方的 UB。
            bool plausible = pStreamDescriptor.ToInt64() >= MmapMinAddr;

            var stream = new MilStreamObject();

            if (!plausible)
            {
                // 不解引用（见上）：没有可交付对象，保持老行为。
                stream.Descriptor = IntPtr.Zero;
                stream.WriteCallback = IntPtr.Zero;
            }
            else
            {
                // 【为什么必须**拷一份**，而不是存下调用方给的地址】
                //   上游原生侧就是这么做的：`exports.cpp:873` 是
                //     `IStream* pStream = new CManagedStreamWrapper(*pSD);`
                //   —— `*pSD` 是**按值拷贝**，wrapper 之后每次回调都传 `&m_sd`（它自己那份）。
                //   原因是 `ref StreamDescriptor` 在跨边界时给原生的是**一份临时副本**的地址：
                //   调用返回后那块内存随时可能被复用。实测（真 PC + AOT 桥）：
                //   存下原地址、回调时再传回去 ⇒ 上游 `StreamAsIStream.FromSD`
                //   读到的 `m_handle` 已经是垃圾/0 ⇒ 触发上游断言
                //   `Debug.Assert(((IntPtr)sd.m_handle) != IntPtr.Zero, "Stream is disposed.")`
                //   ⇒ 进程 FailFast（"Assertion failed. Stream is disposed."）。
                //   所以：Create 时把整个结构体拷进**本工程持有**的内存，
                //   之后回调一律传这份拷贝的地址；释放时（MilStreamObject.Dispose）再 free。
                stream.Descriptor = System.Runtime.InteropServices.Marshal.AllocHGlobal(StreamDescriptorSize);
                stream.DescriptorOwned = true;
                for (int i = 0; i < StreamDescriptorSlotCount; i++)
                {
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(
                        stream.Descriptor, i * IntPtr.Size,
                        System.Runtime.InteropServices.Marshal.ReadIntPtr(pStreamDescriptor, i * IntPtr.Size));
                }
                stream.WriteCallback = System.Runtime.InteropServices.Marshal.ReadIntPtr(
                    stream.Descriptor, StreamWriteCallbackOffset);
            }

            MilDeviceObject obj = MilDeviceObjectTable.Register(
                MilDeviceObjectKind.Stream, stream, "istream");

            ppStream = obj.Handle;
            return HResult.S_OK;
        }

        /// <summary>
        /// `StreamDescriptor` 里 `pfnWrite` 的偏移（字节）。**不手抄**：
        /// `M7cMilStreamTests.StreamDescriptor_pfnWrite偏移与上游字段顺序一致` 会解析
        /// 上游 `StreamAsIStream.cs` 的字段顺序并机械核对。
        /// </summary>
        public const int StreamWriteCallbackOffset = 32;

        /// <summary>
        /// `StreamDescriptor` 的槽位数（14 个函数指针 + 1 个 `GCHandle`）与字节数。
        /// **不手抄**：`M7cMilStreamTests` 解析上游 `StreamAsIStream.cs` 的字段数并与这里比对。
        /// </summary>
        public const int StreamDescriptorSlotCount = 15;

        /// <summary>`StreamDescriptor` 的字节数（x64：15 × 8 = 120）。</summary>
        public const int StreamDescriptorSize = StreamDescriptorSlotCount * 8;

        /// <summary>
        /// 本机"用户态可映射的最低地址"（Linux `vm.mmap_min_addr`，读不到时按内核默认 65536）。
        /// 低于它的地址在本进程里**不可能**被映射 ⇒ 不可能是真的 `StreamDescriptor`。
        /// </summary>
        public static readonly long MmapMinAddr = ReadMmapMinAddr();

        private static long ReadMmapMinAddr()
        {
            try
            {
                string text = System.IO.File.ReadAllText("/proc/sys/vm/mmap_min_addr").Trim();
                if (long.TryParse(text, out long v) && v > 0) return v;
            }
            catch
            {
                // 读不到（容器里没挂 /proc、权限等）⇒ 内核默认值。
            }
            return 65536;
        }

        /// <summary>
        /// 上游 `StreamDescriptor.Write` 的托管签名（`ref StreamDescriptor` = 结构体指针）。
        /// 用 `CallingConvention.Winapi`：.NET 在 Unix 上把 Winapi 映射到平台的默认约定（cdecl），
        /// 与 CLR 为托管委托生成的调用 thunk 一致。
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(
            System.Runtime.InteropServices.CallingConvention.Winapi)]
        public delegate int StreamWriteCallback(
            IntPtr pStreamDescriptor, byte[] buffer, uint cb, out uint cbWritten);

        /// <summary>
        /// 往流里写字节。
        ///
        /// 【M7c 轨道 B 修复 —— 修的是"用户流收到 0 字节"】
        ///   修前：只把字节写进 `MilStreamObject.Data`（进程内的内存流），**从不交还调用方**。
        ///   于是 WPF 级链路"看起来全通"（编码成功、字节数正确），而**用户的 `FileStream` 收到 0 字节**；
        ///   更荒谬的是"编码到只读流竟然成功"——因为压根没往调用方的流里写。
        ///   修后：字节**交还描述符的 `pfnWrite`**（调用方自己的 Stream/FIleStream 就写在里面），
        ///   内存副本降级为**台账**（`MilStreamBytes` 仍可读，用于诊断与自证）。
        ///
        /// 【为什么选"回调"而不是"给 WIC shim 加一个取字节的导出"】
        ///   ① 这是**上游契约本身**：原生 MilCore 对 IStream 的每一次写都回调描述符（pfnWrite/pfnStat/…），
        ///      描述符才是"调用方的流"的唯一权威；内存流只是我们自己的旁路。
        ///   ② 它服务**所有**调用方，而不是只有 WIC：`BitmapEncoder.Save(FileStream)` 这条
        ///      典型路径根本不经过 WIC shim，加 WIC 专用导出对它毫无帮助（正是本轮的现象）。
        ///   ③ 加第二条字节通道会让"字节到底从哪出去"出现两个真相，日后必然分叉。
        ///   失败语义：回调返回失败 ⇒ **原样返回它的 HRESULT**（不吞），并且**不**回填 cbWritten。
        ///   没有回调（描述符没给 pfnWrite，只读流）⇒ 保持原行为（只写台账）并返回 S_OK。
        /// </summary>
        public static int MILIStreamWrite(
            IntPtr pStream,
            byte[] buffer,
            uint cb,
            out uint cbWritten)
        {
            cbWritten = 0;

            MilDeviceObject obj = MilDeviceObjectTable.Resolve(pStream);
            if (obj == null || obj.Kind != MilDeviceObjectKind.Stream) return HResult.E_HANDLE;

            var stream = (MilStreamObject)obj.Payload;
            if (stream == null) return HResult.E_HANDLE;
            if (buffer == null && cb > 0) return HResult.E_INVALIDARG;
            if (cb > (buffer?.Length ?? 0)) return HResult.E_INVALIDARG;

            // 台账：内存副本（诊断用；**不是**交付路径）
            stream.Data.Write(buffer, 0, (int)cb);
            stream.BytesWritten += cb;

            if (stream.WriteCallback == IntPtr.Zero)
            {
                // 描述符没给 pfnWrite：没有可交付的对象（只读流）。保持旧行为，不伪造"写出去了"。
                cbWritten = cb;
                return HResult.S_OK;
            }

            try
            {
                var write = System.Runtime.InteropServices.Marshal
                    .GetDelegateForFunctionPointer<StreamWriteCallback>(stream.WriteCallback);
                int hr = write(stream.Descriptor, buffer, cb, out uint written);
                if (HResult.Failed(hr))
                {
                    MilDiagnostics.Note($"MILIStreamWrite: 调用方 pfnWrite 失败 hr=0x{hr:x8}（{cb} 字节未交付）");
                    return hr;
                }
                stream.BytesDelivered += written;
                cbWritten = written;
                return HResult.S_OK;
            }
            catch (Exception ex)
            {
                // 回调本身抛（不是返回 HRESULT）⇒ 不把异常抛回原生边界，转成失败码并留痕。
                MilDiagnostics.Note($"MILIStreamWrite: 调用方 pfnWrite 抛异常：{ex.GetType().Name}: {ex.Message}");
                return HResult.E_FAIL;
            }
        }

        /// <summary>从流对象取回已写入的字节（本工程给上层/测试用的辅助入口，不是上游导出）。</summary>
        public static byte[] MilStreamBytes(IntPtr pStream)
        {
            MilDeviceObject obj = MilDeviceObjectTable.Resolve(pStream);
            return (obj?.Payload as MilStreamObject)?.Data.ToArray() ?? Array.Empty<byte>();
        }

        // ==================================================================
        //  反向 P/Invoke 包装
        // ==================================================================

        /// <summary>
        /// 为托管函数指针建一个"原生可调用"的包装。本工程的原生层就是托管，
        /// 两侧函数指针同值，所以包装 = 登记 + 下发同一地址（含引用计数）。
        /// </summary>
        public static int MilCreateReversePInvokeWrapper(IntPtr pFcn, out IntPtr reversePInvokeWrapper)
        {
            reversePInvokeWrapper = IntPtr.Zero;
            if (pFcn == IntPtr.Zero) return HResult.E_INVALIDARG;

            reversePInvokeWrapper = MilReversePInvokeTable.Create(pFcn);
            return HResult.S_OK;
        }

        /// <summary>void 导出：释放反向包装。未登记的句柄静默忽略（上游也是 void）。</summary>
        public static void MilReleasePInvokePtrBlocking(IntPtr reversePInvokeWrapper)
        {
            MilReversePInvokeTable.Release(reversePInvokeWrapper);
        }

        // ==================================================================
        //  诊断 / 进程级开关
        // ==================================================================

        /// <summary>下一个性能元素 id。进程内单调，从 1 开始。</summary>
        public static long GetNextPerfElementId() => MilCompositionEngineState.NextPerfElementId();

        /// <summary>
        /// 关闭边界检查保护。上游这是给"受信任的性能敏感路径"用的开关；
        /// 本实现只记录值（.NET 侧没有对应的保护可关），值可往返。
        /// </summary>
        public static void WpfGfx_SetDisableBoundsCheckProtection(bool value)
        {
            MilCompositionEngineState.DisableBoundsCheckProtection = value;
        }

        /// <summary>
        /// 重新读取系统参数。Linux 没有 SystemParametersInfo，也没有需要刷新的
        /// 全局度量缓存 → 恒 S_OK 的空操作（但会计数，便于验证"被调用过"）。
        /// </summary>
        public static int MILUpdateSystemParametersInfo()
        {
            MilCompositionEngineState.NotifySystemParametersUpdate();
            return HResult.S_OK;
        }

        /// <summary>为本进程强制软件渲染。值可往返；本后端的渲染路径恒为软件。</summary>
        public static void RenderOptions_ForceSoftwareRenderingModeForProcess(bool fForce)
        {
            MilCompositionEngineState.ForceSoftwareRendering = fForce;
        }

        /// <summary>
        /// 本进程是否被强制软件渲染。返回上面那个开关的值。
        /// ⚠️ 注意：**false 不代表会走硬件路径**——M1/M2 的后端只有 Skia 软件光栅
        /// （handoff 决策 4），这个导出只是把上游的开关语义保留下来。
        /// </summary>
        public static bool RenderOptions_IsSoftwareRenderingForcedForProcess()
        {
            return MilCompositionEngineState.ForceSoftwareRendering;
        }

        /// <summary>RDP 场景下是否允许硬件加速。Linux 无 RDP 路径，只记录值。</summary>
        public static void RenderOptions_EnableHardwareAccelerationInRdp(bool value)
        {
            MilCompositionEngineState.EnableHardwareAccelerationInRdp = value;
        }

        // ==================================================================
        //  通道导出名别名 + 资源引用计数
        // ==================================================================

        /// <summary>
        /// MilChannel_CloseBatch —— 与既有的 <see cref="MilNative.MilConnection_CloseBatch"/>
        /// 语义完全相同（上游 exports.cs:137 的 EntryPoint 就叫这个名字，C# 方法名才是
        /// MilConnection_CloseBatch）。补齐它，是为了让**导出名**这一侧不留缺口。
        /// </summary>
        public static int MilChannel_CloseBatch(IntPtr channelHandle) =>
            MilConnection_CloseBatch(channelHandle);

        /// <summary>MilChannel_CommitChannel —— 与 MilConnection_CommitChannel 语义相同。</summary>
        public static int MilChannel_CommitChannel(IntPtr channelHandle) =>
            MilConnection_CommitChannel(channelHandle);

        /// <summary>
        /// 取通道上某个资源的引用计数。句柄无效 → E_HANDLE；
        /// 资源句柄为空或不在表里 → E_HANDLE（与 MilResource_ReleaseOnChannel 同一口径），
        /// 此时 refCount = 0。
        /// </summary>
        public static int MilResource_GetRefCountOnChannel(
            IntPtr pChannel,
            DUCE.ResourceHandle hResource,
            out uint refCount)
        {
            refCount = 0;

            Resources.MilChannel channel = Resources.MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;
            if (hResource.IsNull) return HResult.E_HANDLE;
            if (channel.Resources.LookupEntry(hResource) == null) return HResult.E_HANDLE;

            refCount = channel.Resources.GetRefCount(hResource);
            return HResult.S_OK;
        }
    }

    // ======================================================================
    //  T1/M7c6 · **外部句柄归属桥**（WIC）
    // ======================================================================
    //
    //  【它解决什么】
    //    上游 PC 在**纯上游代码路径**上就对 WIC 源句柄调 `MILQueryInterface`，而这个
    //    P/Invoke 的 EntryPoint 落在本 .so：
    //      · `BitmapSource.set_WicSourceHandle`（`BitmapSource.cs:581-586`）
    //      · `BitmapSourceSafeMILHandle.ComputeEstimatedSize`（`:78-82`）
    //      · 调用者 `BitmapFrameDecode.FinalizeCreation`（`BitmapFrameDecode.cs:448`）
    //    WIC 句柄既不在 `MilDeviceObjectTable` 里、也不是 COM 指针，所以必须由
    //    **句柄的所有者**回答 —— 即 `libwpfwic.so`（T2 的 WIC shim）。
    //
    //  【为什么是惰性 dlopen 而不是反向依赖】
    //    MIL（`WpfGfx.Linux`）**不能**引用 WIC shim 的任何东西：那会把 WIC 变成 MIL 的硬依赖，
    //    而没有图像的应用根本不该需要它。反过来让 shim 反向调用 MIL 也不行（循环）。
    //    所以走 dlsym 弱耦合。
    //    （备选路线"MIL 提供外部句柄登记导出、由 WIC shim 主动注册"的评估见 T1 报告 §；
    //      本轮按主控裁定仍用惰性 dlopen。）
    //
    //  【fail-safe —— 默认行为一字不变】
    //    `.so` 找不到 / `dlopen` 失败 / **三个必需符号缺任何一个** / 句柄不属于它
    //    ⇒ **一律 `E_HANDLE`**，与改动前逐字相同。
    //    ⚠️ **三个符号必须齐全**才算"接上"：只有 `OwnsHandle` 而没有 AddRef/Release 的旧版 .so
    //      会让放行变成**单边账本**（放行了却减不掉引用）—— 那正是最不该出现的形态。
    //
    //  ====================================================================
    //  ⭐ 引用计数契约（T1/M7c6 定案）
    //  ====================================================================
    //
    //  【PC 的形态：成功的 QI 会 AddRef，且原句柄与 QI 句柄**各自**被释放】
    //    证据链（上游原文）：
    //      1. `BitmapFrameDecode.EnsureSource`（`BitmapFrameDecode.cs:701`）
    //           `_frameSource = new BitmapSourceSafeMILHandle(frameDecode);`
    //         `BitmapSourceSafeMILHandle : SafeMILHandle`，构造注释写明
    //         "**SafeMILHandle owns the release of the parameter**"（`:46`）。
    //         ⇒ 原句柄 `_frameSource` **会被释放一次**。
    //      2. `BitmapFrameDecode.FinalizeCreation`（`:448`）
    //           `WicSourceHandle = _frameSource;`      ← 触发 `BitmapSource.cs:581-586` 的 QI
    //         紧接着 `:453` 又 `WicSourceHandle = CreateCachedBitmap(...)` **覆盖** `_wicSource`
    //         ⇒ 第 1 次 QI 得到的句柄**立刻被释放**。
    //      3. `SafeMILHandle.ReleaseHandle()`（`SafeMILHandle.cs:63`）
    //           → `MILUnknown.ReleaseInterface(ref handle)` → **本 .so 的 `MILRelease`**。
    //    ⇒ 账本：**创建 refs=1 → QI +1 = 2 → 释放 QI 句柄 = 1 → 释放原句柄 = 0 → 回收**。
    //
    //  ⚠️ **因此"放行但不 AddRef"是不安全的**（不是风格问题，是会 use-after-free）：
    //     若 QI 不 +1：创建 1 →（不 +1）1 → 释放 QI 句柄 → **0，对象当场被回收** →
    //     随后 `_frameSource` 再释放一次 → 引用计数下溢 / 二次释放。
    //     本桥**只实现 AddRef + 转发 Release** 这一种自洽形态。
    //
    //  【本桥的契约】
    //    · **MIL 自己下发的对象**：`MilDeviceObjectTable.AddRef/Release`，**完全不变**。
    //    · **WIC 句柄**：
    //        QI 放行  → 调 `WicShim_AddRef(h)`（+1）；返回同一句柄 + `S_OK`
    //        Release  → 调 `WicShim_Release(h)`（−1）；返回 `S_OK`
    //      **两本账都配平**：N 次 (QI + Release) 之后 refs 回到原值，
    //      且"释放到 0 即回收"由 shim 负责（这也正是 `WIC_OBJ_MAX=256` 不会被解满的原因）。
    //
    //  【不转发 Release 的后果是硬失败，不是缓慢泄漏】
    //    shim 的对象表上限 `WIC_OBJ_MAX = 256`；第 257 次 `obj_new` 返回 0 ⇒
    //    `CreateDecoderFromFileHandle` 直接失败（解满 256 张图就崩）。
    //    `MilWicQueryInterfaceTests` 里有一条"连着建 300 个再各释放"的实测专门钉住这点。
    //  ====================================================================
    /// <summary>外部（非 MIL）句柄的归属桥。当前只有 WIC 一族。</summary>
    public static class MilExternalHandleBridge
    {
        /// <summary>所有者库名。与 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim` 的产物一致。</summary>
        public const string WicLibraryName = "libwpfwic.so";

        /// <summary>归属判定入口。</summary>
        public const string OwnsHandleEntryPoint = "WicShim_OwnsHandle";
        /// <summary>引用计数 +1（**必需**：缺它就不算接上，见 fail-safe）。</summary>
        public const string AddRefEntryPoint = "WicShim_AddRef";
        /// <summary>引用计数 −1（归零即回收）。**必需**。</summary>
        public const string ReleaseEntryPoint = "WicShim_Release";
        /// <summary>活句柄数（配平判据）。</summary>
        public const string HandleCountEntryPoint = "WicShim_HandleCount";
        /// <summary>活句柄高水位（防止"计数相等但一直涨"）。</summary>
        public const string PeakHandleCountEntryPoint = "WicShim_PeakHandleCount";

        /// <summary>显式指定 `.so` 绝对路径（部署在非标准位置时用）。</summary>
        public const string WicPathEnvVar = "MILBRIDGE_WIC_SO";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IntPtrToIntFn(IntPtr handle);      // OwnsHandle / AddRef / Release
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NoArgToIntFn();                   // HandleCount / PeakHandleCount

        private static readonly object s_gate = new object();
        private static bool s_probed;
        private static IntPtr s_library;
        private static IntPtrToIntFn s_ownsHandle;
        private static IntPtrToIntFn s_addRef;
        private static IntPtrToIntFn s_release;
        private static NoArgToIntFn s_handleCount;
        private static NoArgToIntFn s_peakHandleCount;

        // ---------------- 诊断计数（测试与排障用）----------------
        private static long s_probeCount;
        private static long s_ownedHits;
        private static long s_externalQiOk;
        private static long s_externalQiNoIface;
        private static long s_externalAddRefs;
        private static long s_externalReleases;

        /// <summary>所有者库是否已接上（三个必需符号齐全）。false ⇒ 一切外部句柄都退回 E_HANDLE。</summary>
        public static bool IsConnected { get { EnsureProbed(); return s_release != null; } }

        public static long ProbeCount => System.Threading.Interlocked.Read(ref s_probeCount);
        public static long OwnedHits => System.Threading.Interlocked.Read(ref s_ownedHits);
        public static long ExternalQueryInterfaceSucceeded => System.Threading.Interlocked.Read(ref s_externalQiOk);
        public static long ExternalQueryInterfaceNoInterface => System.Threading.Interlocked.Read(ref s_externalQiNoIface);
        public static long ExternalAddRefs => System.Threading.Interlocked.Read(ref s_externalAddRefs);
        public static long ExternalReleases => System.Threading.Interlocked.Read(ref s_externalReleases);

        // ==================================================================
        //  【T2 交办的接线】外来像素源：把"MIL 建的后缓冲句柄"登记进 shim 的**派发**表
        //
        //  【为什么需要它（要解决的问题本身）】
        //    上游的 WIC proxy 是"**转发 vtable 调用**"：句柄是任意 COM 对象，谁的对象谁答。
        //    我们的 shim 是"**表查找**"：`as_source(handle)` 在自己表里查不到就返回 E_INVALIDARG。
        //    于是 `WriteableBitmap` 路径上
        //      `BitmapSource.UpdateCachedSettings → IWICBitmapSource_GetPixelFormat_Proxy(后缓冲句柄)`
        //    必然失败（PC 那边表现为 `ArgumentException`）。
        //    ⇒ 任何**被当成 IWICBitmapSource 交出去的 MIL 句柄**，proxy 层都得能派发。
        //      本组做的就是那一次"登记"。
        //
        //  【⛔ 与记账的边界（主控点名，不许动）】
        //    这只是**派发**登记。`WicShim_OwnsHandle` 对外来条目**返回 0**、`HandleCount`
        //    **不数**它们 ⇒ **引用计数仍然全在 `MilDeviceObjectTable`**（Qi/AddRef/Release 一字未改）。
        //    派发与记账是两件事，别因为这条登记去改任何 AddRef/Release。
        //
        //  【fail-safe】取不到这两个导出（旧版 shim / 没装 WIC）⇒ **只跳过**，
        //    不让它变成启动期硬失败；行为与接线前逐字一致（`GetPixelFormat` 仍 E_INVALIDARG）。
        // ==================================================================

        /// <summary>登记外来像素源（派发用）。所有者：T2 的 `libwpfwic.so`。</summary>
        public const string ForeignSourceRegisterEntryPoint = "WicShim_RegisterForeignSource";
        /// <summary>注销外来像素源。</summary>
        public const string ForeignSourceUnregisterEntryPoint = "WicShim_UnregisterForeignSource";
        /// <summary>当前登记着几个外来像素源（**派发面**的计数，不是记账面）。</summary>
        public const string ForeignSourceCountEntryPoint = "WicShim_ForeignSourceCount";
        /// <summary>
        /// `WicShim_DescribeHandle(intptr_t h, char* buf, size_t cap)` —— **D-d 取证出口**：
        /// "这个句柄在本 shim 里到底是什么"（种类/尺寸/格式/是否外来/引用数）。
        /// 只能在**应用进程内**调（`probe_describe` 是独立进程，看不到本进程的 shim 表 —— 实测过）。
        /// </summary>
        public const string DescribeHandleEntryPoint = "WicShim_DescribeHandle";

        /// <summary>
        /// `WicShim_RegisterForeignSource(intptr_t ext, const guid_t* pixelFormat, uint32_t width,
        /// uint32_t height, int32_t isOpaque, const void* pixels, uint32_t rowBytes)`。
        /// `guid_t` 是 16 字节 POD，与托管 `Guid` 同布局（按引用传）。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ForeignSourceRegisterFn(
            IntPtr ext, ref Guid pixelFormat, uint width, uint height, int isOpaque,
            IntPtr pixels, uint rowBytes);

        /// <summary>`int32_t WicShim_UnregisterForeignSource(intptr_t ext)`。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IntPtrToIntCdeclFn(IntPtr handle);

        private static ForeignSourceRegisterFn s_foreignRegister;
        private static IntPtrToIntCdeclFn s_foreignUnregister;
        private static NoArgToIntFn s_foreignCount;
        private static DescribeHandleFn s_describeHandle;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DescribeHandleFn(IntPtr handle, IntPtr buffer, IntPtr capacity);

        /// <summary>
        /// 在**本进程**里描述一个 WIC 句柄（只读）。不可用/失败返回 null。
        /// **缺省零开销**：调用方只在诊断开关打开时调它。
        /// </summary>
        public static string DescribeHandle(IntPtr handle)
        {
            EnsureProbed();
            DescribeHandleFn fn = s_describeHandle;
            if (fn == null || handle == IntPtr.Zero) return null;

            IntPtr buf = IntPtr.Zero;
            try
            {
                const int Cap = 512;
                buf = Marshal.AllocHGlobal(Cap);
                Marshal.WriteByte(buf, 0, 0);
                int hr = fn(handle, buf, (IntPtr)Cap);
                string text = Marshal.PtrToStringAnsi(buf);
                return string.IsNullOrEmpty(text) ? $"<空>（hr=0x{hr:x8}）" : text;
            }
            catch (Exception ex)
            {
                return $"<异常：{ex.GetType().Name}>";
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>
        /// 当前登记着几个外来像素源；未接上返回 −1。
        /// **这是派发面的计数，不是记账面**：`WicShim_HandleCount()` 不数它们
        /// （正因为如此，它才能用来断言"登记都注销干净了"而不与记账混淆）。
        /// </summary>
        public static int ForeignSourceCount()
        {
            EnsureProbed();
            NoArgToIntFn fn = s_foreignCount;
            return fn == null ? -1 : SafeCall(fn, -1);
        }

        private static long s_foreignRegistrations;
        private static long s_foreignUnregistrations;
        private static int s_foreignLastHr = int.MinValue;

        /// <summary>成功登记过的外来源次数（测试断言用：证明"这一次调用真的发出去了"）。</summary>
        public static long ForeignSourceRegistrations => System.Threading.Interlocked.Read(ref s_foreignRegistrations);

        /// <summary>注销次数。</summary>
        public static long ForeignSourceUnregistrations => System.Threading.Interlocked.Read(ref s_foreignUnregistrations);

        /// <summary>最近一次登记/注销的 HRESULT（诊断；从未调用过为 int.MinValue）。</summary>
        public static int ForeignSourceLastHr => s_foreignLastHr;

        /// <summary>外来源登记是否可用（三个必需导出齐全后才会被解析）。</summary>
        public static bool ForeignSourceDispatchAvailable
        {
            get { EnsureProbed(); return s_foreignRegister != null; }
        }

        /// <summary>登记一个外来像素源。返回 shim 的 HRESULT；不可用/异常返回 <see cref="HResult.E_NOTIMPL"/>。</summary>
        public static int RegisterForeignSource(
            IntPtr handle, ref Guid pixelFormat, uint width, uint height, bool isOpaque,
            IntPtr pixels = default, uint rowBytes = 0)
        {
            EnsureProbed();
            ForeignSourceRegisterFn fn = s_foreignRegister;
            if (fn == null) return HResult.E_NOTIMPL;
            try
            {
                // ⚠️ `pixels` 是**借给 shim 的指针**：注销之前它必须一直有效
                //   （调用方 = `MILSwDoubleBufferedBitmapCreate`，见那里的注释与
                //    `MilDoubleBufferedState.Dispose()` 的拆除顺序）。
                //   传 NULL（默认）仍然是合法的降级形态：shim 只答元数据，
                //   `Lock` 诚实地返回 UNSUPPORTEDOPERATION，而不是编一份数据出来。
                int hr = fn(handle, ref pixelFormat, width, height, isOpaque ? 1 : 0, pixels, rowBytes);
                s_foreignLastHr = hr;
                if (HResult.Succeeded(hr)) System.Threading.Interlocked.Increment(ref s_foreignRegistrations);
                return hr;
            }
            catch (Exception ex)
            {
                s_foreignLastHr = HResult.E_NOTIMPL;
                MilDiagnostics.Note($"RegisterForeignSource 抛异常（按未实现处理）：{ex.GetType().Name}: {ex.Message}");
                return HResult.E_NOTIMPL;
            }
        }

        /// <summary>注销外来像素源。不可用时返回 <see cref="HResult.E_NOTIMPL"/>（调用方只记台账）。</summary>
        public static int UnregisterForeignSource(IntPtr handle)
        {
            EnsureProbed();
            IntPtrToIntCdeclFn fn = s_foreignUnregister;
            if (fn == null) return HResult.E_NOTIMPL;
            try
            {
                int hr = fn(handle);
                s_foreignLastHr = hr;
                if (HResult.Succeeded(hr)) System.Threading.Interlocked.Increment(ref s_foreignUnregistrations);
                return hr;
            }
            catch (Exception ex)
            {
                s_foreignLastHr = HResult.E_NOTIMPL;
                MilDiagnostics.Note($"UnregisterForeignSource 抛异常（按未实现处理）：{ex.GetType().Name}: {ex.Message}");
                return HResult.E_NOTIMPL;
            }
        }

        // ---- #24：把 WIC 句柄的像素**拉出来**（物化用；三个都只读）----
        /// <summary>`IWICBitmapSource_GetSize_Proxy`。</summary>
        public const string BitmapSourceGetSizeEntryPoint = "IWICBitmapSource_GetSize_Proxy";
        /// <summary>`IWICBitmapSource_GetPixelFormat_Proxy`。</summary>
        public const string BitmapSourceGetPixelFormatEntryPoint = "IWICBitmapSource_GetPixelFormat_Proxy";
        /// <summary>`IWICBitmapSource_CopyPixels_Proxy`。</summary>
        public const string BitmapSourceCopyPixelsEntryPoint = "IWICBitmapSource_CopyPixels_Proxy";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetSizeFn(IntPtr source, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetPixelFormatFn(IntPtr source, ref Guid pixelFormat);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CopyPixelsFn(IntPtr source, IntPtr prc, uint stride, uint bufferSize, IntPtr pixels);

        private static GetSizeFn s_bsGetSize;
        private static GetPixelFormatFn s_bsGetPixelFormat;
        private static CopyPixelsFn s_bsCopyPixels;

        /// <summary>三个只读 proxy 是否齐（不齐 ⇒ 物化路径整体不可用，fail-safe）。</summary>
        public static bool BitmapSourceReadAvailable
        {
            get { EnsureProbed(); return s_bsGetSize != null && s_bsGetPixelFormat != null && s_bsCopyPixels != null; }
        }

        /// <summary>取 WIC 源的尺寸。不可用/失败返回 false。</summary>
        public static bool TryGetWicSize(IntPtr source, out uint width, out uint height)
        {
            width = height = 0;
            EnsureProbed();
            GetSizeFn fn = s_bsGetSize;
            if (fn == null) return false;
            try { return fn(source, out width, out height) == HResult.S_OK && width > 0 && height > 0; }
            catch { return false; }
        }

        /// <summary>取 WIC 源的像素格式。不可用/失败返回 false。</summary>
        public static bool TryGetWicPixelFormat(IntPtr source, out Guid format)
        {
            format = Guid.Empty;
            EnsureProbed();
            GetPixelFormatFn fn = s_bsGetPixelFormat;
            if (fn == null) return false;
            try { return fn(source, ref format) == HResult.S_OK; }
            catch { return false; }
        }

        /// <summary>把整幅像素拷进调用方给的缓冲区。不可用/失败返回 false。</summary>
        public static bool TryCopyWicPixels(IntPtr source, uint stride, uint bufferSize, IntPtr pixels)
        {
            EnsureProbed();
            CopyPixelsFn fn = s_bsCopyPixels;
            if (fn == null || pixels == IntPtr.Zero) return false;
            try { return fn(source, IntPtr.Zero, stride, bufferSize, pixels) == HResult.S_OK; }
            catch { return false; }
        }

        /// <summary>
        /// "这个句柄是不是 WIC 所有者（libwpfwic.so）的？"——**只读探测**，不记 AddRef/Release。
        /// 给诊断用（例如 `MilResource_CreateCWICWrapperBitmap` 收到不认识句柄时说明它的归属）。
        /// 未接上所有者时返回 false。
        /// </summary>
        public static bool OwnsHandleProbe(IntPtr handle)
        {
            EnsureProbed();
            IntPtrToIntFn fn = s_ownsHandle;
            if (fn == null || handle == IntPtr.Zero) return false;
            try { return fn(handle) != 0; }
            catch { return false; }
        }

        /// <summary>查询所有者侧的活句柄数；未接上返回 −1。</summary>
        public static int ExternalHandleCount()
        {
            EnsureProbed();
            NoArgToIntFn fn = s_handleCount;
            return fn == null ? -1 : SafeCall(fn, -1);
        }

        /// <summary>查询所有者侧的活句柄高水位；未接上返回 −1。</summary>
        public static int ExternalPeakHandleCount()
        {
            EnsureProbed();
            NoArgToIntFn fn = s_peakHandleCount;
            return fn == null ? -1 : SafeCall(fn, -1);
        }

        private static int SafeCall(NoArgToIntFn fn, int fallback)
        {
            try { return fn(); } catch { return fallback; }
        }

        public static void ResetCounters()
        {
            System.Threading.Interlocked.Exchange(ref s_probeCount, 0);
            System.Threading.Interlocked.Exchange(ref s_ownedHits, 0);
            System.Threading.Interlocked.Exchange(ref s_externalQiOk, 0);
            System.Threading.Interlocked.Exchange(ref s_externalQiNoIface, 0);
            System.Threading.Interlocked.Exchange(ref s_externalAddRefs, 0);
            System.Threading.Interlocked.Exchange(ref s_externalReleases, 0);
        }

        private static string s_searchDirectory;

        /// <summary>
        /// 由宿主（NativeAOT 桥接层）注入"本 .so 自己所在的目录"。
        ///
        /// 【为什么必须有这一条】`.so` 是一个 **NativeAOT 共享库**，它内部的
        /// `AppContext.BaseDirectory` **不是宿主应用的目录**（实测：跑 `dotnet app.dll` 时
        /// 它会指到 dotnet 根目录）—— 这正是 M7c3 给 `libSkiaSharp` 踩过的同一个坑。
        /// 桥接层已经用 `dladdr` 算出了 .so 的真实目录并通过 `MilBridge_SetNativeDir` 送进来，
        /// 这里把它也接上，`libwpfwic.so` 就按"与 .so 同目录"找（部署形态）。
        ///
        /// 幂等：可以重复调用。若此前探测过但**没接上**，则允许重新探测一次。
        /// </summary>
        public static void SetSearchDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            lock (s_gate)
            {
                s_searchDirectory = directory;
                if (s_release == null)
                {
                    s_ownsHandle = s_addRef = s_release = null;
                    s_handleCount = s_peakHandleCount = null;
                    s_foreignRegister = null; s_foreignUnregister = null; s_foreignCount = null;
                s_describeHandle = null;
                    s_describeHandle = null;
                s_bsGetSize = null; s_bsGetPixelFormat = null; s_bsCopyPixels = null;
                    s_bsGetSize = null; s_bsGetPixelFormat = null; s_bsCopyPixels = null;
                    s_library = IntPtr.Zero;
                    s_probed = false;      // 允许用新目录重探
                }
            }
        }

        /// <summary>当前注入的搜索目录（诊断用）。</summary>
        public static string SearchDirectory => s_searchDirectory;

        /// <summary>测试用：断开并允许重新探测（模拟"没装 WIC"）。</summary>
        public static void ResetForTests()
        {
            lock (s_gate)
            {
                s_ownsHandle = s_addRef = s_release = null;
                s_handleCount = s_peakHandleCount = null;
                s_foreignRegister = null; s_foreignUnregister = null; s_foreignCount = null;
                s_describeHandle = null;
                s_bsGetSize = null; s_bsGetPixelFormat = null; s_bsCopyPixels = null;
                s_library = IntPtr.Zero;
                s_probed = false;
            }
            ResetCounters();
        }

        // ------------------------------------------------------------------

        private static void EnsureProbed()
        {
            if (s_probed) return;

            lock (s_gate)
            {
                if (s_probed) return;
                s_probed = true;
                System.Threading.Interlocked.Increment(ref s_probeCount);

                foreach (string candidate in CandidatePaths())
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    try
                    {
                        if (!NativeLibrary.TryLoad(candidate, out IntPtr lib) || lib == IntPtr.Zero) continue;

                        // **三个必需符号必须齐全**，否则视为"没接上"（fail-safe）：
                        // 只有 OwnsHandle 而没有 AddRef/Release 的旧版 .so 会让放行变成单边账本。
                        if (!TryGet(lib, OwnsHandleEntryPoint, out IntPtr pOwns)) continue;
                        if (!TryGet(lib, AddRefEntryPoint, out IntPtr pAdd)) continue;
                        if (!TryGet(lib, ReleaseEntryPoint, out IntPtr pRel)) continue;

                        s_ownsHandle = Marshal.GetDelegateForFunctionPointer<IntPtrToIntFn>(pOwns);
                        s_addRef = Marshal.GetDelegateForFunctionPointer<IntPtrToIntFn>(pAdd);
                        s_release = Marshal.GetDelegateForFunctionPointer<IntPtrToIntFn>(pRel);

                        // 可选（只有配平诊断用；缺了不影响放行）
                        s_handleCount = TryGet(lib, HandleCountEntryPoint, out IntPtr pHc)
                            ? Marshal.GetDelegateForFunctionPointer<NoArgToIntFn>(pHc) : null;
                        s_peakHandleCount = TryGet(lib, PeakHandleCountEntryPoint, out IntPtr pPk)
                            ? Marshal.GetDelegateForFunctionPointer<NoArgToIntFn>(pPk) : null;

                        // 可选（T2 的外来源派发；缺了只是"这道墙还在"，不影响放行与记账）
                        s_foreignRegister = TryGet(lib, ForeignSourceRegisterEntryPoint, out IntPtr pFr)
                            ? Marshal.GetDelegateForFunctionPointer<ForeignSourceRegisterFn>(pFr) : null;
                        s_foreignUnregister = TryGet(lib, ForeignSourceUnregisterEntryPoint, out IntPtr pFu)
                            ? Marshal.GetDelegateForFunctionPointer<IntPtrToIntCdeclFn>(pFu) : null;
                        s_foreignCount = TryGet(lib, ForeignSourceCountEntryPoint, out IntPtr pFc)
                            ? Marshal.GetDelegateForFunctionPointer<NoArgToIntFn>(pFc) : null;
                        s_describeHandle = TryGet(lib, DescribeHandleEntryPoint, out IntPtr pDh)
                            ? Marshal.GetDelegateForFunctionPointer<DescribeHandleFn>(pDh) : null;

                        // 可选（#24 物化用；缺了只是"这条墙还在"）
                        s_bsGetSize = TryGet(lib, BitmapSourceGetSizeEntryPoint, out IntPtr pGs)
                            ? Marshal.GetDelegateForFunctionPointer<GetSizeFn>(pGs) : null;
                        s_bsGetPixelFormat = TryGet(lib, BitmapSourceGetPixelFormatEntryPoint, out IntPtr pGf)
                            ? Marshal.GetDelegateForFunctionPointer<GetPixelFormatFn>(pGf) : null;
                        s_bsCopyPixels = TryGet(lib, BitmapSourceCopyPixelsEntryPoint, out IntPtr pCp)
                            ? Marshal.GetDelegateForFunctionPointer<CopyPixelsFn>(pCp) : null;

                        s_library = lib;
                        return;
                    }
                    catch
                    {
                        // 换下一个候选；全部失败就保持"未接上"
                    }
                }
            }
        }

        private static bool TryGet(IntPtr lib, string name, out IntPtr fn)
            => NativeLibrary.TryGetExport(lib, name, out fn) && fn != IntPtr.Zero;

        /// <summary>
        /// 候选路径（顺序即优先级）。**任何一档失败都只是继续下一档**，
        /// 全部失败 ⇒ 退回 E_HANDLE（未启用 WIC 的默认行为）。
        /// </summary>
        private static string[] CandidatePaths()
        {
            string app = AppContext.BaseDirectory;

            var list = new System.Collections.Generic.List<string>();
            string explicitPath = Environment.GetEnvironmentVariable(WicPathEnvVar);
            if (!string.IsNullOrEmpty(explicitPath)) list.Add(explicitPath);

            // ① 宿主注入的"本 .so 所在目录"（部署形态：与 wpfgfx_cor3.so 同目录）
            string injected = s_searchDirectory;
            if (!string.IsNullOrEmpty(injected)) list.Add(Path.Combine(injected, WicLibraryName));

            // ② AppContext.BaseDirectory —— ⚠️ 在 AOT 共享库里它**不是**宿主应用目录，
            //    所以只当兜底，不能当主路径。
            if (!string.IsNullOrEmpty(app)) list.Add(Path.Combine(app, WicLibraryName));

            string ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (!string.IsNullOrEmpty(ldPath))
            {
                foreach (string dir in ldPath.Split(':'))
                    if (!string.IsNullOrEmpty(dir)) list.Add(Path.Combine(dir, WicLibraryName));
            }

            list.Add(WicLibraryName);   // 裸名：交给动态链接器
            return list.ToArray();
        }

        /// <summary>该句柄是否由 WIC shim 认领。库/符号不可用 ⇒ 恒 false（fail-safe）。</summary>
        public static bool IsOwnedByWic(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;

            EnsureProbed();
            IntPtrToIntFn fn = s_ownsHandle;
            if (fn == null || s_release == null) return false;

            try
            {
                if (fn(handle) == 0) return false;
                System.Threading.Interlocked.Increment(ref s_ownedHits);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 对外部句柄做 QueryInterface。
        ///
        /// **GUID 白名单只有两个**：`IID_IUnknown` 与 `IID_IWICBitmapSource`。
        /// 其它一律 `E_NOINTERFACE` —— 绝不"任何 GUID 都返 S_OK"，那才是伪造 COM 成功。
        ///
        /// 放行时调 `WicShim_AddRef`（**COM 契约要求 QI 必须 AddRef**，且见类注释的账本推导）。
        /// AddRef 返回 ≤ 0（句柄在判定与 AddRef 之间已失效）⇒ 不放行，退回 `E_HANDLE`。
        /// </summary>
        public static int QueryInterface(IntPtr pIUnknown, ref Guid guid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;

            if (!IsOwnedByWic(pIUnknown))
            {
                // 既不是 MIL 设备对象、也不是 WIC 的 → 未登记句柄，原语义
                return HResult.E_HANDLE;
            }

            if (guid != MilNative.IID_IUnknown && guid != MilNative.IID_IWICBitmapSource)
            {
                System.Threading.Interlocked.Increment(ref s_externalQiNoIface);
                return HResult.E_NOINTERFACE;
            }

            int refs;
            try { refs = s_addRef(pIUnknown); }
            catch { return HResult.E_HANDLE; }

            if (refs <= 0) return HResult.E_HANDLE;   // 已失效，不假装成功

            System.Threading.Interlocked.Increment(ref s_externalAddRefs);
            System.Threading.Interlocked.Increment(ref s_externalQiOk);
            ppvObject = pIUnknown;
            return HResult.S_OK;
        }

        /// <summary>
        /// 对外部句柄做 Release（**必须转发**，否则 `WIC_OBJ_MAX=256` 会被解满）。
        /// 返回 S_OK 表示"已交给所有者处理"；句柄不属于它时返回 false（调用方走原路径）。
        /// </summary>
        public static bool TryReleaseExternal(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;

            EnsureProbed();
            IntPtrToIntFn fn = s_release;
            if (fn == null) return false;

            try
            {
                if (s_ownsHandle == null || s_ownsHandle(handle) == 0) return false;
                fn(handle);                              // 归零时由 shim 回收
                System.Threading.Interlocked.Increment(ref s_externalReleases);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// `wpfgfx_cor3.so`（AOT 桥）里 **SkiaSharp 的 `libSkiaSharp`** 的 P/Invoke 解析器。
    ///
    /// 【它解决什么（R-GATE 现场；见 `src/Linux/build/MilBridge/P1-rgate-regress-report.md`）】
    ///   `wpfgfx_cor3.so` 是 NativeAOT 共享库 —— 它内部的 `AppContext.BaseDirectory` **不是**
    ///   宿主应用目录（实测是 dotnet 根目录 `~/.dotnet`）⇒ SkiaSharp 的 `[DllImport("libSkiaSharp")]`
    ///   在默认探测里必然落空 ⇒ SkiaSharp 类型初始化抛 `TypeInitializationException`
    ///   ⇒ `RenderChannel` 抛 ⇒ 呈现返回 `E_FAIL` ⇒ `DUCE.Channel.Commit()` 抛未捕获
    ///   `COMException(0x80004005)` ⇒ **应用 abort**（`WM_SIZE` 第一帧就死，R-GATE 因此恒红）。
    ///
    /// 【为什么候选里必须有"应用局部 NuGet 原生布局"这一档】
    ///   部署契约是"`libSkiaSharp.so` 与 `wpfgfx_cor3.so` 同目录"。而 .NET SDK 把
    ///   `SkiaSharp.NativeAssets.Linux` 的原生资产放进 `runtimes/&lt;rid&gt;/native/`（**不是**输出根）
    ///   ⇒ "整目录拷贝一个应用目录"装配出来的宿主里，`libSkiaSharp.so` **只可能**出现在
    ///   `&lt;应用目录&gt;/runtimes/&lt;rid&gt;/native/`。只看"与 .so 同目录"的旧解析器在这类宿主上
    ///   恒加载失败（历史绿靠 `src/Linux/samples/*/bin` 里**手工留的陈旧副本**撑着）。
    ///
    /// 【与 `MilBridge.NativeSearchPath` 的关系（**不是第二份判据**）】
    ///   `SetDllImportResolver` **按程序集**生效且只能装一次。MilBridge 给 SkiaSharp 装过一次；
    ///   本文件的 `[ModuleInitializer]` 实测先跑（`WpfGfx.Linux` 是依赖方）⇒ 由本文件接管，
    ///   MilBridge 那次 `SetDllImportResolver` 抛 `InvalidOperationException` 并被它自己 catch（它的既有设计）。
    ///   ⇒ 本件的候选集**必须覆盖** MilBridge 原本覆盖的那几档（同目录 ＋ `dladdr` 自定位 ＋
    ///   `AppContext.BaseDirectory` ＋ `LD_LIBRARY_PATH`），**只追加** `runtimes/&lt;rid&gt;/native/` 一档。
    ///   ⚠️ 全部落空时**返回 0**（交回默认探测，让它报带完整路径的错）——**不伪造成功**。
    /// </summary>
    internal static unsafe class MilBridgeSkiaResolver
    {
        /// <summary>SkiaSharp 的 P/Invoke 目标库名（其 `DllImport` 用的裸名）。</summary>
        private const string SkiaLibraryName = "libSkiaSharp.so";

        /// <summary>显式覆盖（诊断/多版本并存场景；与 `MILBRIDGE_MILCORE_SO` 同族）。</summary>
        private const string SkiaPathEnvVar = "MILBRIDGE_SKIA_SO";

        private static int _installed;

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Install()
        {
            if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0) return;
            try
            {
                // SkiaSharp 是被 AOT 编进本镜像的；它的 P/Invoke 归**它自己那个程序集**的解析器管。
                System.Reflection.Assembly skia = typeof(SkiaSharp.SKBitmap).Assembly;
                NativeLibrary.SetDllImportResolver(skia, Resolve);
            }
            catch (InvalidOperationException)
            {
                // 已有别的解析器（MilBridge 抢先）——让位，退回它的候选集（与该文件独立时的历史一致）。
            }
            catch
            {
                // 解析器装不上不影响主链路：默认探测照旧
            }
        }

        private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly,
                                      DllImportSearchPath? searchPath)
        {
            if (string.IsNullOrEmpty(libraryName) ||
                libraryName.IndexOf("SkiaSharp", StringComparison.Ordinal) < 0)
            {
                return IntPtr.Zero;   // 不是我们管的库
            }

            foreach (string candidate in Candidates())
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try
                {
                    if (System.IO.File.Exists(candidate)) return NativeLibrary.Load(candidate);
                }
                catch
                {
                    // 试下一档
                }
            }

            return IntPtr.Zero;       // 找不到 ⇒ 交回默认探测（**报错必须响亮**）
        }

        private static string[] Candidates()
        {
            var list = new System.Collections.Generic.List<string>();
            string[] rids = Rids();

            void AddDir(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                list.Add(System.IO.Path.Combine(dir, SkiaLibraryName));                       // 部署契约：与 .so 同目录
                foreach (string rid in rids)
                    list.Add(System.IO.Path.Combine(dir, "runtimes", rid, "native", SkiaLibraryName)); // NuGet/应用局部原生布局
            }

            string env = Environment.GetEnvironmentVariable(SkiaPathEnvVar);
            if (!string.IsNullOrEmpty(env)) list.Add(env);

            AddDir(SelfDirectory);                                // 本 .so 所在目录（dladdr 自定位）
            AddDir(MilExternalHandleBridge.SearchDirectory);      // 宿主注入的"本 .so 所在目录"
            AddDir(AppContext.BaseDirectory);                     // 宿主应用目录（AOT 里常是 dotnet 根 ⇒ 兜底）

            string ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            if (!string.IsNullOrEmpty(ldPath))
                foreach (string dir in ldPath.Split(':'))
                    if (!string.IsNullOrEmpty(dir)) list.Add(System.IO.Path.Combine(dir, SkiaLibraryName));

            return list.ToArray();
        }

        private static string[] Rids()
        {
            string rid = RuntimeInformation.RuntimeIdentifier;
            if (string.IsNullOrEmpty(rid)) return new[] { "linux-x64" };
            // Linux/x64 上 `linux-x64` 是唯一实用值；其余（arm64…）以本机 RID 为准，仍带上 x64 兜底。
            return string.Equals(rid, "linux-x64", StringComparison.Ordinal)
                ? new[] { rid } : new[] { rid, "linux-x64" };
        }

        // ==================================================================
        //  自我定位（dladdr）—— 与 `MilBridge.NativeSearchPath.SelfDirectory` 同一手法：
        //  取本镜像里某个 `[UnmanagedCallersOnly]` 方法的地址，dladdr 据此回报 .so 路径
        //  （纯 C 宿主直接 dlopen 本 .so、没有托管解析器注入目录时也能定位）。
        // ==================================================================
        private static string _selfDir;
        private static bool _selfDirProbed;

        internal static string SelfDirectory
        {
            get
            {
                if (!_selfDirProbed)
                {
                    _selfDirProbed = true;
                    try
                    {
                        delegate* unmanaged<byte*, int> self = &SelfProbe;
                        if (TryDladdr((IntPtr)self, out string path) && !string.IsNullOrEmpty(path))
                            _selfDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                    }
                    catch
                    {
                        // 定位不了 ⇒ 退回落链的后面几档
                    }
                }
                return _selfDir;
            }
        }

        /// <summary>只为提供"本 .so 里一个稳定地址"（无 EntryPoint ⇒ **不新增任何导出**）。</summary>
        [UnmanagedCallersOnly]
        private static int SelfProbe(byte* ignored) => 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct DlInfo
        {
            public IntPtr dli_fname;
            public IntPtr dli_fbase;
            public IntPtr dli_sname;
            public IntPtr dli_saddr;
        }

        [DllImport("libdl.so.2", EntryPoint = "dladdr")]
        private static extern int DladdrLibdl(IntPtr addr, out DlInfo info);

        [DllImport("libc.so.6", EntryPoint = "dladdr")]
        private static extern int DladdrLibc(IntPtr addr, out DlInfo info);

        private static bool TryDladdr(IntPtr addr, out string path)
        {
            path = null;
            DlInfo info;
            int rc;
            try
            {
                rc = DladdrLibdl(addr, out info);
            }
            catch (DllNotFoundException)
            {
                // glibc ≥ 2.34 把 dladdr 并进 libc（libdl 只是兼容壳）
                try { rc = DladdrLibc(addr, out info); }
                catch { return false; }
            }
            catch
            {
                return false;
            }

            if (rc == 0 || info.dli_fname == IntPtr.Zero) return false;
            path = Marshal.PtrToStringUTF8(info.dli_fname);
            return !string.IsNullOrEmpty(path);
        }
    }
}
