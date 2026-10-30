// Licensed to the .NET Foundation under one or more agreements.
//
// M7a · 分组 C：窗口绑定 / 连接 / 后向通道消息 / 合成引擎锁（16 个）。
//
// 【当前实现到哪一步 —— 请务必读完这一段再看代码】
//
//   MilVisualTarget_AttachToHwnd / DetachFromHwnd 与
//   MilContent_AttachToHwnd / DetachFromHwnd
//     → 身份映射语义（登记、查表、解绑、幂等、冲突拒绝、句柄不复用）落在
//       MilHwndRegistry（进程内表）。
//     → **M7c 轨道 C 起，这里不再止于身份映射**（本段原话是 M7a 时期的
//       "把这个 HWND 对到真实的 X11Window……那些属于后续 milestone"，
//       那句话已经过期，别再照它下判断）：
//         · Attach → `MilPresentation.TryBind(hwnd)`（:136）—— hwnd 就是 X11 Window
//           （M7b 实证 HWND == XID），于是真的 `X11Window.Wrap` + `X11PresentationTarget`
//           挂上去，并 `SelectPresentationEvents()` 订阅 Expose/StructureNotify；
//         · Detach → `MilPresentation.TryUnbind(hwnd)`（:152）—— 只释放我们建的 GC，
//           **不销毁窗口**（窗口生命周期归 Win32 shim 的 DestroyWindow）。
//       绑定失败**不改 HRESULT**：M7a 已把 Attach 的契约定成"占用/登记"，
//       "窗口系统是否就绪"不属于这条契约；失败原因记进 MilPresentation/BindError。
//     → 真像素证据（不是"登记表里有这条"）：见
//       `tests/.../ManagedLayer.Tests/M7cRealAttachmentTests.cs` —— 真 HwndWrapper 窗口 +
//       独立进程 `xwd` 抓帧 + 逐像素计数：Attach 后纯色像素数 == W×H、Detach 后**一个像素都不动**、
//       resize 后内容跟着重渲且无 X 底色空白条。
//     → HRESULT 逐条对齐上游 WpfGfx/core/uce/vt_api.cpp:24/67：
//         Attach 已存在 → E_ACCESSDENIED；Detach 不存在 → E_INVALIDARG。
//       这两个码是 PresentationCore 的 HwndTarget 用来识别"窗口已被占用"的
//       （HwndTarget.cs:544 判 E_ACCESSDENIED 抛 WindowAlreadyHasContent），
//       所以必须保真，不能一律返回 S_OK。
//
//   WgxConnection_Create / Disconnect
//     → 真实现：建/销连接对象（MilConnectionTable），requestSynchronousTransport
//       映射到 MilMarshalType::SameThread/CrossThread（上游 apifunc.cpp:186）。
//       连接句柄可被既有的 WgxConnection_SameThreadPresent 使用（它按
//       channel.Connection 索引，Connection 就是这里下发的句柄）。
//
//   MilComposition_PeekNextMessage / SyncFlush / WaitForNextMessage
//     → 真实现后向通道队列（进程内），语义逐条对齐 clientchannel.cpp:921：
//       SyncFlush = 提交当前批次 + 投递一条 SyncFlushReply；
//       PeekNextMessage = **取出**队首（上游就是 dequeue，名字叫 peek 而已），
//       cbSize 小于 sizeof(MIL_MESSAGE) 时按 min 部分拷贝。
//     → **没做**：WaitForNextMessage 的等待句柄数组。Linux 侧没有内核事件对象，
//       句柄数组只做校验不参与等待；消息不可用且 nCount>0 时走超时分支。
//       这一点在报告与 docs/unimplemented.md 建议条目里都写明。
//
//   MilCompositionEngine_* 锁
//     → 真实现：可重入锁（上游是 Win32 CRITICAL_SECTION，Monitor 同为可重入），
//       并暴露重入深度供断言。分区管理器只做登记（Linux/M1 无独立合成线程）。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// 通道的后向通道消息队列（进程内旁表）。
    ///
    /// 【为什么是旁表】Resources/MilChannel.cs 是其它组的文件，M7a 的改动边界不允许
    ///   改它；而通道句柄是单调下发、永不复用的 IntPtr，天然适合当稳定键
    ///   （与 Commands/MilBitmapSource.cs 用资源实例做弱键是同一套路，只是这里的
    ///   键不需要弱引用——通道销毁后调用方不会再拿这个句柄来查）。
    ///   通道销毁时不会收到通知，所以这里在每次访问时顺手清理已失效的通道条目。
    /// </summary>
    public static class MilChannelBackChannel
    {
        private static readonly ConcurrentDictionary<IntPtr, ConcurrentQueue<MilMessage>> _queues =
            new ConcurrentDictionary<IntPtr, ConcurrentQueue<MilMessage>>();

        public static void Post(IntPtr channelHandle, MilMessage message)
        {
            if (channelHandle == IntPtr.Zero) return;
            _queues.GetOrAdd(channelHandle, _ => new ConcurrentQueue<MilMessage>()).Enqueue(message);

            // ── T1/M7c：通知窗口的**唯一**消费者就是这个入队点 ──────────────────
            //   同线程传输（当前唯一形态，MarshalType == SameThread）：入队即送达，
            //   没有"另一个线程在等"，所以这里什么都不做。
            //   ⚠️ 若将来引入 ChannelMarshalType.CrossThread，**必须**在这里按
            //      MilChannel_SetNotificationWindow 登记的那对 (hwnd, message)
            //      去唤醒 UI 线程（上游 Windows 版就是 ::PostMessage(hwnd, msg)）。
            //      落点已经预留：MilChannelNotificationRegistry.OnBackChannelPosted，
            //      它现在只累加 CrossThreadPokes（恒 0），不假装已经唤醒。
            MilChannelNotificationRegistry.OnBackChannelPosted(channelHandle, message);
        }

        /// <summary>尝试取出队首消息（上游 PeekNextMessage 也是取出）。</summary>
        public static bool TryDequeue(IntPtr channelHandle, out MilMessage message)
        {
            message = default;
            if (channelHandle == IntPtr.Zero) return false;
            if (!_queues.TryGetValue(channelHandle, out ConcurrentQueue<MilMessage> queue)) return false;
            return queue.TryDequeue(out message);
        }

        public static bool HasMessage(IntPtr channelHandle) =>
            channelHandle != IntPtr.Zero &&
            _queues.TryGetValue(channelHandle, out ConcurrentQueue<MilMessage> queue) &&
            !queue.IsEmpty;

        public static int PendingCount(IntPtr channelHandle) =>
            channelHandle != IntPtr.Zero &&
            _queues.TryGetValue(channelHandle, out ConcurrentQueue<MilMessage> queue)
                ? queue.Count
                : 0;

        /// <summary>通道注销后清掉它的队列（避免进程级表随通道数增长）。</summary>
        public static void DropIfChannelGone(IntPtr channelHandle)
        {
            if (channelHandle == IntPtr.Zero) return;
            if (Resources.MilChannelRegistry.Resolve(channelHandle) == null)
                _queues.TryRemove(channelHandle, out _);
        }

        public static int ChannelCount => _queues.Count;

        public static void Reset() => _queues.Clear();
    }

    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  HWND 绑定
        // ==================================================================

        /// <summary>
        /// 把一个 HWND 登记为呈现目标。已被别的目标占用的 HWND 返回 E_ACCESSDENIED
        /// （上游 vt_api.cpp:24 的 g_hwndMap 冲突分支）。
        /// </summary>
        public static int MilVisualTarget_AttachToHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return HResult.E_INVALIDARG;

            if (!MilHwndRegistry.TryAttachVisualTarget(hwnd, out _))
                return MilErrors.E_ACCESSDENIED;

            // ── M7c：除身份登记外，为这个**已存在的 XID** 绑定一个呈现目标 ──
            // hwnd 就是 X11 Window（M7b 实证），所以这里能直接 X11Window.Wrap 它。
            //
            // ⚠️ 绑定失败**不改变本函数的 HRESULT**：M7a 已把 Attach 的语义定成
            //    "占用/登记"（HwndTarget 靠 E_ACCESSDENIED 判 WindowAlreadyHasContent），
            //    而"窗口系统是否就绪"不属于这条契约。失败原因被记进 MilPresentation，
            //    并会在真正要出像素时（WgxConnection_SameThreadPresent）变成失败码 ——
            //    即"登记可以宽容，出图必须诚实"。
            MilPresentation.TryBind(hwnd, out string bindError);
            if (bindError != null)
                MilDiagnostics.Note($"MilVisualTarget_AttachToHwnd: HWND 0x{(long)hwnd:x} 身份登记成功，但 X11 呈现目标绑定失败：{bindError}");

            return HResult.S_OK;
        }

        /// <summary>解绑呈现目标。不在表里返回 E_INVALIDARG（上游 vt_api.cpp:67）。</summary>
        public static int MilVisualTarget_DetachFromHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return HResult.E_INVALIDARG;

            if (!MilHwndRegistry.TryDetachVisualTarget(hwnd)) return HResult.E_INVALIDARG;

            // 同步解绑呈现目标：只释放我们建的 GC，**不销毁窗口**
            // （窗口生命周期归 Win32 shim 的 DestroyWindow）。
            MilPresentation.TryUnbind(hwnd);
            return HResult.S_OK;
        }

        /// <summary>
        /// MIL 内容提示（Windows 上是 DwmAttachMilContent；非 DWM 平台恒 S_OK）。
        /// Linux 上没有 DWM，这里保持"恒 S_OK + 登记身份"，语义是幂等的。
        /// </summary>
        public static int MilContent_AttachToHwnd(IntPtr hwnd)
        {
            MilHwndRegistry.AttachContent(hwnd);
            return HResult.S_OK;
        }

        /// <summary>移除 MIL 内容提示。幂等，恒 S_OK（与上游一致）。</summary>
        public static int MilContent_DetachFromHwnd(IntPtr hwnd)
        {
            MilHwndRegistry.DetachContent(hwnd);
            return HResult.S_OK;
        }

        // ==================================================================
        //  连接
        // ==================================================================

        /// <summary>
        /// 建连接。requestSynchronousTransport=true 对应上游
        /// MilMarshalType::SameThread（同线程呈现），false 对应 CrossThread。
        /// </summary>
        public static int WgxConnection_Create(bool requestSynchronousTransport, out IntPtr ppConnection)
        {
            MilConnectionObject connection = MilConnectionTable.Create(requestSynchronousTransport);
            ppConnection = connection.Handle;
            return HResult.S_OK;
        }

        /// <summary>断开连接。空句柄 → E_INVALIDARG（上游 CHECKPTRARG）；未知句柄 → E_HANDLE。</summary>
        public static int WgxConnection_Disconnect(IntPtr pTranspManager)
        {
            if (pTranspManager == IntPtr.Zero) return HResult.E_INVALIDARG;

            return MilConnectionTable.Destroy(pTranspManager) ? HResult.S_OK : HResult.E_HANDLE;
        }

        /// <summary>
        /// 是否要为"图形流客户端"强制软件渲染。上游只在 Vista（非 Win7+）上
        /// 检测到图形流客户端时才为 true（apifunc.cpp:150）；Linux 上恒 false。
        /// 注意这不代表本后端会走硬件路径——M1 只有软件渲染（handoff 决策 4）。
        /// </summary>
        public static bool WgxConnection_ShouldForceSoftwareForGraphicsStreamClient()
        {
            return false;
        }

        // ==================================================================
        //  后向通道消息
        // ==================================================================

        /// <summary>
        /// 刷新通道并等待同步回复。实现 = 提交当前批次（批次内命令立即执行）
        /// + 投递一条 SyncFlushReply 到后向通道队列。
        /// 通道句柄无效 → E_HANDLE；句柄为空 → E_INVALIDARG（上游 CHECKPTRARG）。
        /// </summary>
        public static int MilComposition_SyncFlush(IntPtr pChannel)
        {
            if (pChannel == IntPtr.Zero) return HResult.E_INVALIDARG;

            Resources.MilChannel channel = Resources.MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;

            int hr = channel.Commit();
            if (HResult.Failed(hr)) return hr;

            if (MilConnectionTable.Resolve(channel.Connection) is MilConnectionObject connection)
                connection.SyncFlushCount++;

            MilChannelBackChannel.Post(pChannel, new MilMessage
            {
                Type = MilMessageType.SyncFlushReply,
                Reserved = 0,
            });

            return HResult.S_OK;
        }

        /// <summary>
        /// 取下一条后向通道消息。**会出队**（上游 clientchannel.cpp:921 就是
        /// RemoveHeadList）。cbSize 小于消息大小时按 min 部分拷贝（上游 RtlCopyMemory
        /// 也是 min）。
        /// </summary>
        public static int MilComposition_PeekNextMessage(
            IntPtr pChannel,
            out MilMessage message,
            IntPtr messageSize,
            out int messageRetrieved)
        {
            message = default;
            messageRetrieved = 0;

            if (pChannel == IntPtr.Zero) return HResult.E_INVALIDARG;

            Resources.MilChannel channel = Resources.MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;

            if (!MilChannelBackChannel.TryDequeue(pChannel, out MilMessage pending))
                return HResult.S_OK;      // 队列空：retrieved=0，消息清零

            long size = messageSize.ToInt64();
            int copy = (int)Math.Min(size <= 0 ? 0 : size, sizeof(MilMessage));

            if (copy >= sizeof(MilMessage))
            {
                message = pending;
            }
            else if (copy > 0)
            {
                // 部分拷贝：按字节复制前 copy 个字节（上游 min(cbSize, sizeof) 语义）
                Span<byte> source = System.Runtime.InteropServices.MemoryMarshal
                    .AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref pending, 1));
                Span<byte> destination = System.Runtime.InteropServices.MemoryMarshal
                    .AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref message, 1));
                source.Slice(0, copy).CopyTo(destination);
            }

            messageRetrieved = 1;
            return HResult.S_OK;
        }

        /// <summary>上游 MAXIMUM_WAIT_OBJECTS - 1（clientchannel.cpp:1004）。</summary>
        public const int MaximumWaitObjects = 63;

        /// <summary>Windows 的 WAIT_TIMEOUT。</summary>
        public const int WaitTimeoutStatus = 258;

        /// <summary>等待的返回值上限（WAIT_OBJECT_0 + 62）。</summary>
        private const int WaitObject0 = 0;

        /// <summary>
        /// 等待后向通道消息或句柄集合。
        ///
        /// 【本实现的边界（必须明说）】
        ///   Linux 侧没有 Windows 的内核事件对象，pHandles 只能做**校验**，不能真的
        ///   参与等待。等待语义只落在"后向通道消息"这一路：
        ///     · 消息已经可用 → 立即返回 waitReturn = WAIT_OBJECT_0(0)
        ///     · 否则按 waitTimeout 毫秒轮询（1ms 粒度），超时返回 WAIT_TIMEOUT(258)
        ///   校验规则与失败码逐条对齐上游 clientchannel.cpp:989：
        ///     pWaitReturn 为空 / nCount>0 而 pHandles 为空 / nCount > 63 → E_INVALIDARG。
        ///   waitTimeout = uint.MaxValue（上游 waitInfinite）表示无限等待。
        /// </summary>
        public static int MilComposition_WaitForNextMessage(
            IntPtr pChannel,
            int nCount,
            IntPtr[] handles,
            int bWaitAll,
            uint waitTimeout,
            out int waitReturn)
        {
            waitReturn = 0;

            if (pChannel == IntPtr.Zero) return HResult.E_INVALIDARG;

            Resources.MilChannel channel = Resources.MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;

            if (nCount > 0 && handles == null) return HResult.E_INVALIDARG;
            if (nCount > MaximumWaitObjects) return HResult.E_INVALIDARG;
            if (handles != null && nCount > handles.Length) return HResult.E_INVALIDARG;

            _ = bWaitAll;   // 见上：句柄不参与等待

            bool infinite = waitTimeout == uint.MaxValue;
            long deadline = infinite ? long.MaxValue : Environment.TickCount64 + waitTimeout;

            while (true)
            {
                if (MilChannelBackChannel.HasMessage(pChannel))
                {
                    waitReturn = WaitObject0;
                    return HResult.S_OK;
                }

                if (!infinite && Environment.TickCount64 >= deadline)
                {
                    waitReturn = WaitTimeoutStatus;
                    return HResult.S_OK;
                }

                Thread.Sleep(1);
            }
        }

        // ==================================================================
        //  合成引擎锁 / 分区管理器
        // ==================================================================

        /// <summary>进入合成引擎锁。上游是 CRITICAL_SECTION.Enter()——**可重入**。</summary>
        public static void MilCompositionEngine_EnterCompositionEngineLock() =>
            MilCompositionEngineState.EnterCompositionEngineLock();

        /// <summary>退出合成引擎锁。未持有时抛异常（见 MilCompositionEngineState 的注释）。</summary>
        public static void MilCompositionEngine_ExitCompositionEngineLock() =>
            MilCompositionEngineState.ExitCompositionEngineLock();

        /// <summary>进入媒体系统锁。同为可重入锁。</summary>
        public static void MilCompositionEngine_EnterMediaSystemLock() =>
            MilCompositionEngineState.EnterMediaSystemLock();

        /// <summary>退出媒体系统锁。</summary>
        public static void MilCompositionEngine_ExitMediaSystemLock() =>
            MilCompositionEngineState.ExitMediaSystemLock();

        /// <summary>
        /// 初始化分区管理器（上游会建调度器 + 一组合成工作线程）。
        /// Linux/M1 的合成是同步的，这里只登记"已初始化"与优先级；重复调用幂等。
        /// </summary>
        public static int MilCompositionEngine_InitializePartitionManager(int nPriority) =>
            MilCompositionEngineState.InitializePartitionManager(nPriority);

        /// <summary>释放分区管理器。上游恒返回 S_OK（apifunc.cpp:110），这里保持一致。</summary>
        public static int MilCompositionEngine_DeinitializePartitionManager() =>
            MilCompositionEngineState.DeinitializePartitionManager();
    }
}
