// Licensed to the .NET Foundation under one or more agreements.
//
// T1/M7c · 通道通知窗口登记表 —— `MilChannel_SetNotificationWindow` 的落点。
//
// ============================================================================
//  【为什么必须有这张表】这不是"补一个 E_NOTIMPL"，而是 WPF 启动路径上的必经点：
//
//    MediaContext.CreateChannels()
//      → MediaContext.HookNotifications()
//        → MediaContextNotificationWindow.SetAsChannelNotificationWindow()
//          → DUCE.Channel.SetNotificationWindow(hwnd, s_channelNotifyMessage)
//            → [DllImport("wpfgfx_cor3.dll", "MilChannel_SetNotificationWindow")]
//
//  它返回一次失败，PresentationCore 的 HRESULT.Check 就抛 NotImplementedException，
//  Application 直接起不来（M7c 实测：HelloWpf 卡在 CreateChannels）。
// ============================================================================
//
//  【契约（照 M7c 的语义设计，见 docs/U2-M7c-report.md §3.4）】
//
//    | 入参                            | 结果                          |
//    |---------------------------------|-------------------------------|
//    | pChannel 无法解析                | E_HANDLE（沿用既有约定）      |
//    | hwnd == 0                        | **解绑**；S_OK，幂等          |
//    | hwnd != 0 且 message == 0        | E_INVALIDARG                  |
//    | hwnd != 0 且 message != 0        | **登记**；S_OK；同值重复登记幂等 |
//    | hwnd != 0，同通道换一个 (hwnd,msg)| 覆盖登记；S_OK               |
//
//  【不真的 PostMessage —— 这是刻意的，不是偷懒】
//  上游 Windows 版 `CChannel::SetNotificationWindow` 除了记下 (hwnd, msg)，还会在
//  有后向通道消息时 `::PostMessage(hwnd, msg, ...)` 去**唤醒另一个线程**。
//  而本工程的 DUCE 传输从 M7a 起就定为**进程内**（MilChannelBackChannel 队列 +
//  MilComposition_PeekNextMessage 出队），通道的 MarshalType 是
//  `ChannelMarshalTypeSameThread` —— 同线程呈现下不存在"另一个线程在等"，
//  入队即送达，没有需要唤醒的对象。所以这里**只登记**，投递交给
//  MilChannelBackChannel（M7b/M7c 的进程内路径）。
//
//  ⚠️ 若将来引入 `ChannelMarshalType.CrossThread`，**必须**回到
//     `MilChannelBackChannel.Post` 里按本条登记去唤醒 UI 线程 ——
//     那里已经预留了调用点（见 MilNative.Window.cs 的 Post），
//     本类的 OnBackChannelPosted 是它的落点。
// ============================================================================

using System;
using System.Collections.Concurrent;

namespace WpfGfx.Linux.Interop
{
    /// <summary>
    /// 通道 → 通知窗口 `(hwnd, message)` 的进程内登记表。
    ///
    /// 【为什么是旁表】`Resources/MilChannel.cs` 不在本组改动边界内，而通道句柄是
    ///   **单调下发、永不复用**的 IntPtr（见 docs/unimplemented.md §2.5），
    ///   天然是稳定键 —— 与 MilChannelBackChannel 同一套路。
    ///   通道销毁时由 MilConnection_DestroyChannel 显式摘除，不留悬挂条目。
    /// </summary>
    public static class MilChannelNotificationRegistry
    {
        /// <summary>一条登记。</summary>
        public readonly struct Registration
        {
            /// <summary>通知窗口句柄（Win32 shim 下 HWND == X11 XID）。</summary>
            public readonly IntPtr Hwnd;

            /// <summary>通知消息（`RegisterWindowMessage("MilChannelNotify")` 的返回值）。</summary>
            public readonly uint Message;

            /// <summary>该通道被 SetNotificationWindow 成功登记/重复登记的累计次数。</summary>
            public readonly long SetCount;

            internal Registration(IntPtr hwnd, uint message, long setCount)
            {
                Hwnd = hwnd;
                Message = message;
                SetCount = setCount;
            }

            public override string ToString() => $"hwnd=0x{Hwnd.ToInt64():X} msg=0x{Message:X4} setCount={SetCount}";
        }

        private static readonly ConcurrentDictionary<IntPtr, Registration> _byChannel =
            new ConcurrentDictionary<IntPtr, Registration>();

        private static long _crossThreadPokes;

        /// <summary>
        /// 登记（或覆盖登记）一个通道的通知窗口。
        /// 同值重复登记是**幂等**的：只累加 <see cref="Registration.SetCount"/>，不产生第二条记录。
        /// </summary>
        public static void Register(IntPtr channelHandle, IntPtr hwnd, uint message)
        {
            if (channelHandle == IntPtr.Zero) return;

            _byChannel.AddOrUpdate(
                channelHandle,
                _ => new Registration(hwnd, message, 1),
                (_, prev) => new Registration(hwnd, message, prev.SetCount + 1));
        }

        /// <summary>
        /// 解绑。幂等：未登记过也返回 false 而不是抛异常（调用方据此仍回 S_OK）。
        /// </summary>
        public static bool Unregister(IntPtr channelHandle)
        {
            if (channelHandle == IntPtr.Zero) return false;
            return _byChannel.TryRemove(channelHandle, out _);
        }

        /// <summary>查询该通道是否登记了通知窗口（"可查询"契约）。</summary>
        public static bool TryGet(IntPtr channelHandle, out IntPtr hwnd, out uint message)
        {
            hwnd = IntPtr.Zero;
            message = 0;
            if (channelHandle == IntPtr.Zero) return false;
            if (!_byChannel.TryGetValue(channelHandle, out Registration reg)) return false;

            hwnd = reg.Hwnd;
            message = reg.Message;
            return true;
        }

        /// <summary>查询完整登记记录（含登记次数，测试/诊断用）。</summary>
        public static bool TryGetRegistration(IntPtr channelHandle, out Registration registration)
        {
            registration = default;
            if (channelHandle == IntPtr.Zero) return false;
            return _byChannel.TryGetValue(channelHandle, out registration);
        }

        /// <summary>当前已登记的通道数。</summary>
        public static int RegisteredCount => _byChannel.Count;

        /// <summary>
        /// 后向通道消息入队后的唤醒钩子（由 <see cref="MilChannelBackChannel.Post"/> 调用）。
        ///
        /// 同线程传输（当前唯一形态）：入队即送达，**什么都不做**，计数器不动。
        /// CrossThread：这里就是"按登记 PostMessage 唤醒 UI 线程"的位置 ——
        /// 在没有真正的跨线程传输之前，只累加 <see cref="CrossThreadPokes"/>，
        /// 让"本该校验的路径"可被测试观测，而不是假装已经唤醒。
        /// </summary>
        public static void OnBackChannelPosted(IntPtr channelHandle, MilMessage message)
        {
            if (!IsCrossThreadForWake(channelHandle)) return;
            System.Threading.Interlocked.Increment(ref _crossThreadPokes);
        }

        /// <summary>
        /// 该通道是否需要"跨线程唤醒"（= CrossThread 且已登记通知窗口）。
        /// 目前恒为 false，因为通道 MarshalType 只可能是 SameThread。
        /// </summary>
        public static bool IsCrossThreadForWake(IntPtr channelHandle)
        {
            if (!_byChannel.ContainsKey(channelHandle)) return false;
            Resources.MilChannel channel = Resources.MilChannelRegistry.Resolve(channelHandle);
            return channel != null &&
                   channel.MarshalType == ChannelMarshalType.ChannelMarshalTypeCrossThread;
        }

        /// <summary>本应该投递跨线程唤醒的次数（当前形态下恒为 0；见类注释）。</summary>
        public static long CrossThreadPokes => System.Threading.Interlocked.Read(ref _crossThreadPokes);

        /// <summary>通道注销后摘掉它的登记（避免进程级表随通道数增长）。</summary>
        public static bool DropIfChannelGone(IntPtr channelHandle)
        {
            if (channelHandle == IntPtr.Zero) return false;
            if (Resources.MilChannelRegistry.Resolve(channelHandle) != null) return false;
            return _byChannel.TryRemove(channelHandle, out _);
        }

        /// <summary>清空全表（测试用，由 MilNative.ResetProcessStateForTests 调用）。</summary>
        public static void Reset()
        {
            _byChannel.Clear();
            System.Threading.Interlocked.Exchange(ref _crossThreadPokes, 0);
        }
    }
}
