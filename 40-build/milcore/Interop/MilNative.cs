// Licensed to the .NET Foundation under one or more agreements.
//
// 13 个 MIL 导出函数的 Linux 实现（另 3 个 M1 返回 E_NOTIMPL）。
//
// 【接入方式】handoff 决策 3：整个移植只改一个文件——把 Common/Graphics/exports.cs
// 里的 [DllImport(DllImport.MilCore)] 换成对本类同名方法的调用即可，
// 签名逐字对齐上游（exports.cs:113-204），**不要改调用方**。
//
// 差异只有两点，都不影响调用方：
//   1. SafeMediaHandle / BitmapSourceSafeMILHandle 退化成 IntPtr（M1 不接媒体与位图）
//   2. WindowMessage 退化成 uint（M1 不投递窗口消息）
//
// 通道句柄 IntPtr 由 MilChannelRegistry 下发（见 Resources/MilChannel.cs），是进程内唯一的
// 逻辑句柄：不是真实的内核对象，不能跨进程、不能 CloseHandle；注销后该值永不复用，
// 因此陈旧句柄只会拿到 E_HANDLE，不会误伤后来新建的通道。

using System;
using System.Collections.Generic;
using WpfGfx.Linux.Commands;
using WpfGfx.Linux.Contracts;
using WpfGfx.Linux.Resources;
using SkiaSharp;
using S = WpfGfx.Linux.Commands.MilCommandStructs;

namespace WpfGfx.Linux.Interop
{
    // ------------------------------------------------------------------
    //  M7a：本类改为 partial，新增的 94 个导出放在同目录的
    //    MilNative.Geometry.cs / MilNative.Glyph.cs / MilNative.Window.cs /
    //    MilNative.Offscreen.cs / MilNative.Media.cs / MilNative.Misc.cs /
    //    MilNative.Exports.cs
    //  里（**上面这 16 个函数一行未动**：签名、语义、HRESULT 全部保持原样）。
    //  加 partial 是唯一能让新导出与既有导出共享 MilNative 这个名字的手段，
    //  也是 handoff 决策 3「把 [DllImport] 换成对本类同名方法的调用」的前提。
    // ------------------------------------------------------------------
    public static unsafe partial class MilNative
    {
        // ==================================================================
        //  通道生命周期
        // ==================================================================

        /// <summary>
        /// 建通道。hChannel 为参考通道句柄时共享其分区（此时两通道可 DuplicateHandle）；
        /// 为 IntPtr.Zero 时新建分区。
        /// </summary>
        public static int MilConnection_CreateChannel(IntPtr pTransport, IntPtr hChannel, out IntPtr channelHandle)
        {
            channelHandle = IntPtr.Zero;

            MilPartition partition = null;
            if (hChannel != IntPtr.Zero)
            {
                MilChannel reference = MilChannelRegistry.Resolve(hChannel);
                if (reference == null) return HResult.E_HANDLE;
                partition = reference.Partition;
            }

            var channel = new MilChannel(partition)
            {
                Dispatcher = new MilCommandDispatcher(),
                Connection = pTransport,
            };

            // 债务 #1 的长期仪器（缺省关）：**建通道之前**报"此刻已经活着几个通道" ——
            // 有些套件不调 `ResetProcessStateForTests()`，所以这个挂点比只挂 Reset 更普遍。
            // 只读快照，行为不变。
            Resources.MilChannelRegistry.ReportLeaks("MilConnection_CreateChannel 之前");

            channelHandle = MilChannelRegistry.Register(channel);
            return HResult.S_OK;
        }

        /// <summary>销毁通道：清空资源表并注销句柄。</summary>
        public static int MilConnection_DestroyChannel(IntPtr channelHandle)
        {
            MilChannel channel = MilChannelRegistry.Resolve(channelHandle);
            if (channel == null) return HResult.E_HANDLE;

            channel.Resources.Clear();
            // T1/M7c：通知窗口登记随通道一起摘掉（句柄单调不复用，不摘会变成永久悬挂条目）。
            MilChannelNotificationRegistry.Unregister(channelHandle);
            MilChannelRegistry.Unregister(channelHandle);
            MilPresentation.Trace(
                $"[destroy] 通道 0x{(long)channelHandle:x}（Id={channel.Id}）已注销；registry.Count={MilChannelRegistry.Count}");
            return HResult.S_OK;
        }

        // 【提交前预检】**已下沉**到 `MilChannel.PreflightPendingCommands()`（波46 · `D-G54`）。
        //   它原先是本文件里一个走**反射**读 `MilChannel` 私有批次字段的方法，因为
        //   `_batch` / `_commandLengths` 不属本组边界；现在它长在 `MilChannel` 自己身上
        //   （同名字段直接可见）⇒ 反射与 `using System.Reflection` 一并**不需要**了。
        //   输出格式逐字沿用（见 `MilChannel.PreflightPendingCommands` 的注释）。

        /// <summary>
        /// "摘除前冲刷"：把该通道已入队但未提交的命令先处理掉。
        /// · **不在 commit 过程内部做**（`_commitDepth > 0`）—— 那时批次正在被逐条派发，
        ///   再冲一次会把整批**重复执行**；
        /// · 失败只记账（`MilDiagnostics` + 诊断汇），**不改变 release 自己的返回值** ——
        ///   release 的契约是"引用计数减一"，命令批次的失败属于那次批次，不冒充 release 的结果。
        /// </summary>
        private static void FlushPendingCommandsBeforeInvalidation(MilChannel channel)
        {
            if (channel.PendingCommandCount == 0) return;
            if (_commitDepth.ContainsKey(channel.Handle)) return;

            int hr = channel.Commit();
            if (HResult.Failed(hr))
                MilDiagnostics.Note(
                    $"摘除前冲刷通道 {channel.Id} 的待处理命令失败 hr=0x{hr:x8}" +
                    "（命令引用的资源在更早之前就已摘除 —— 这是真实失败，未吞）");
            if (MilPresentation.DiagnosticSinkEnabled)
                MilPresentation.Trace(
                    $"[flush-before-release] 通道 {channel.Id} 冲刷待处理命令 ⇒ hr=0x{hr:x8}");
        }

        /// <summary>当前有 commit 正在进行的通道（句柄 → 重入深度）。防止冲刷重入导致整批重复执行。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, int> _commitDepth =
            new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, int>();

        /// <summary>关闭当前批次。本实现里批次边界无副作用，只校验没有未闭合的命令。</summary>
        public static int MilConnection_CloseBatch(IntPtr channelHandle)
        {
            MilChannel channel = MilChannelRegistry.Resolve(channelHandle);
            if (channel == null)
            {
                MilPresentation.Trace(
                    $"[closebatch] ★E_HANDLE Resolve(0x{(long)channelHandle:x}) == null（registry.Count={MilChannelRegistry.Count}）");
                return HResult.E_HANDLE;
            }
            int hr = channel.CloseBatch();
            if (MilPresentation.DiagnosticSinkEnabled)
                MilPresentation.Trace(
                    $"[closebatch] 通道 {channel.Id} 待处理 {channel.PendingCommandCount} 条 ⇒ hr=0x{hr:x8}");
            return hr;
        }

        /// <summary>
        /// 提交：按入队顺序解码执行整批命令，**并把结果呈现到窗口上**。
        ///
        /// 【M7c 收尾轮：为什么呈现要挂在这里】上游 Windows 上，"把这一批画到屏幕上"
        /// 这一步**不在托管层**：托管只调 `MilConnection_CommitChannel`，随后由
        /// **原生 MilCore 的渲染线程**完成渲染 + `Present`（这也是为什么托管代码里
        /// `Channel.Present()` 只在离屏的 `Renderer.cs` 里出现，真实窗口从不调它）。
        /// 本工程的 MilCore 端（AOT 桥）**没有渲染线程**，于是提交完就断了 ——
        /// 实测后果：通道里 `committed=65 / 资源=44` 却一帧都上不了屏。
        /// ⇒ 在这里同步补上那一步：**提交成功后，若该通道绑定了窗口就渲染并呈现**。
        ///
        /// 语义边界（与 `WgxConnection_SameThreadPresent` 完全一致，都用
        /// `MilPresentation.PresentChannel`）：
        ///   · 无窗口目标的通道（离屏/纯命令）→ 什么都不做，S_OK；
        ///   · 有窗口但没 `TargetSetRoot` → S_OK + 诊断记录；
        ///   · 有窗口有根但渲染/呈现失败 → **返回失败码**，绝不假装成功。
        /// </summary>
        public static int MilConnection_CommitChannel(IntPtr channelHandle)
        {
            // 【三处 E_HANDLE 的定位台账】主控要求"钉死是哪一处"，所以三个出口各留一条
            // 可区分的证据（`WPF_LINUX_MIL_LOG=<path>` 会把它们写进文件，测试宿主里也读得到）。
            MilChannel channel = MilChannelRegistry.Resolve(channelHandle);
            if (channel == null)
            {
                MilPresentation.Trace(
                    $"[commit] ★E_HANDLE#1 Resolve(0x{(long)channelHandle:x}) == null ⇒ 通道**未注册或已被销毁**" +
                    $"（registry.Count={MilChannelRegistry.Count}）");
                return HResult.E_HANDLE;
            }

            // 【提交前预检】已**下沉**到 `MilChannel.Commit()`（波46 · `D-G54` 台账盲区）：
            //   原来只长在本函数里 ⇒ `WgxConnection_SameThreadPresent` / 收尾 flush /
            //   `MilComposition_SyncFlush` 三条提交路径**不打印**，实测把弹窗那条
            //   `SetRoot` 漏在预检之外（预检里 `id=0x35` 只有 1 条，像是"没进缓冲"）。
            //   下沉后由 `Commit()` 统一打印，输出格式逐字不变。
            _commitDepth.AddOrUpdate(channel.Handle, 1, (_, n) => n + 1);
            int hr;
            try { hr = channel.Commit(); }
            finally
            {
                if (_commitDepth.AddOrUpdate(channel.Handle, 0, (_, n) => n - 1) <= 1)
                    _commitDepth.TryRemove(channel.Handle, out _);
            }
            if (HResult.Failed(hr))
            {
                MilPresentation.Trace(
                    $"[commit] ★E_HANDLE#2 channel.Commit() 失败 hr=0x{hr:x8}（通道 {channel.Id}）"
                    + (MilPresentation.DiagnosticSinkEnabled ? "；待提交命令见上面的预检" : ""));
                return hr;
            }

            // ↓↓↓ 对应原生 MilCore 渲染线程那一步（没有它，像素永远留在通道里）↓↓↓
            int presented = MilPresentation.PresentChannel(channel);
            if (HResult.Failed(presented))
            {
                MilPresentation.Trace(
                    $"[commit] ★E_HANDLE#3 PresentChannel 失败 hr=0x{presented:x8}（通道 {channel.Id}）");
                return presented;
            }
            return hr;
        }

        /// <summary>
        /// 提交该连接上的所有通道，**并把它渲染出来呈现到窗口上**（上游语义：
        /// WgxConnection::PresentCommon → 每个通道 ProcessRemoves/ProcessCommands → Present）。
        ///
        /// 【M7a → M7c 的差别】
        ///   M7a：只 `channel.Commit()`（解码执行命令，**不产生任何像素**）。
        ///   M7c：Commit 之后，若该通道绑定了窗口（`MilCmdHwndTargetCreate` 给了非空 HWND）
        ///        且已 `TargetSetRoot`，就把根视觉渲染成 SKImage 并 `Present` 到那个
        ///        X11 窗口，最后 `XSync`。
        ///
        /// 【"什么都不做"的两种情况，都不是静默失败】
        ///   · 通道没有窗口目标（离屏 / 纯命令通道）→ S_OK。确实没有窗口可画。
        ///     M7a 的既有用例（`SameThreadPresent提交所有通道`）走的正是这条，
        ///     所以**行为与 M7a 逐条一致**。
        ///   · 通道有窗口目标但没 `TargetSetRoot` → S_OK + 诊断记录。没有内容可画。
        ///   反之，"有窗口、有根、但渲染或 Present 失败" → **返回失败码**，
        ///   绝不假装成功（原因同时还留在 <see cref="MilDiagnostics"/> 里）。
        /// </summary>
        public static int WgxConnection_SameThreadPresent(IntPtr pConnection)
        {
            int hr = HResult.S_OK;
            foreach (MilChannel channel in MilChannelRegistry.ChannelsOfConnection(pConnection))
            {
                int one = channel.Commit();
                if (HResult.Failed(one) && HResult.Succeeded(hr)) hr = one;
                if (HResult.Failed(one)) continue;

                int presented = MilPresentation.PresentChannel(channel);
                if (HResult.Failed(presented) && HResult.Succeeded(hr)) hr = presented;
            }
            return hr;
        }

        public static int MilChannel_GetMarshalType(IntPtr channelHandle, out ChannelMarshalType marshalType)
        {
            marshalType = ChannelMarshalType.ChannelMarshalTypeInvalid;
            MilChannel channel = MilChannelRegistry.Resolve(channelHandle);
            if (channel == null) return HResult.E_HANDLE;

            marshalType = channel.MarshalType;
            return HResult.S_OK;
        }

        // ==================================================================
        //  资源句柄
        // ==================================================================

        /// <summary>句柄为空时按类型建资源（RefCount=1），否则对既有句柄 AddRef。</summary>
        public static int MilResource_CreateOrAddRefOnChannel(
            IntPtr pChannel, DUCE.ResourceType type, ref DUCE.ResourceHandle hResource)
        {
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;

            channel.Resources.CreateOrAddRef(type, ref hResource);
            if (MilPresentation.DiagnosticSinkEnabled)
                MilPresentation.Trace(
                    $"[create] 通道 {channel.Id}(0x{(long)pChannel:x}) 句柄 0x{hResource.Value:x8} 类型 {type} " +
                    $"⇒ 表内总数 {channel.Resources.Count}");
            return HResult.S_OK;
        }

        /// <summary>
        /// RefCount--；归零时从表里摘除，deleted=1。
        ///
        /// 【M7c 收尾轮：摘除之前必须先"冲刷"已入队的命令 —— 修一个真实的拆除期缺陷】
        /// 实测（`ManagedWindowTests.HwndSource_Characterization_OnLinux`，5/5 稳定复现）：
        ///   `MediaContext.Dispose → RemoveChannels → Channel.Close() → CommitChannel()`
        ///   抛 `COMException 0x80070006 (E_HANDLE)`。进程内台账把因果链钉死为：
        ///     [create] 句柄 0x2/0x3/0x4 建好 → **[release] 三个句柄全部摘除** →
        ///     [closebatch] 通道 2 **待处理 7 条** → [commit] 这 7 条的目标句柄全都不在表里 → E_HANDLE
        ///   即：**一个从未闭合的批次**（7 条 HwndTarget/SetRoot/UpdateWindowSettings…）
        ///   一直躺在队列里没人处理，而拆除先把资源摘了，最后 `Close()` 才把它派发出去。
        ///
        /// 【上游为什么不会这样】原生 MilCore 的渲染线程是**流式**处理命令的：append 进去的命令
        ///   很快被处理，而 UI 线程的 release 只是"排队摘除"，两者在
        ///   `WgxConnection::PresentCommon` 里分成两个阶段（ProcessRemoves / ProcessCommands）。
        ///   本移植**没有渲染线程**、`Commit()` 是唯一处理点 ⇒ 未闭合批次会一直滞留。
        ///
        /// 【修法】在**任何会摘除资源的操作之前**，先把已入队的命令处理掉 —— 这是"流式处理"的
        ///   等价物，**不是吞错**：命令若引用的是**更早已经真正摘除**的资源，照旧返回 E_HANDLE
        ///   （`Require&lt;T&gt;` 的行为一字未改）。只有"排着队、还没来得及处理"的那一批会被救回来。
        /// </summary>
        public static int MilResource_ReleaseOnChannel(
            IntPtr pChannel, DUCE.ResourceHandle hResource, out int deleted)
        {
            deleted = 0;
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;
            if (hResource.IsNull) return HResult.E_INVALIDARG;
            if (channel.Resources.Lookup(hResource) == null) return HResult.E_HANDLE;

            FlushPendingCommandsBeforeInvalidation(channel);

            channel.Resources.Release(hResource, out bool removed);
            deleted = removed ? 1 : 0;
            if (MilPresentation.DiagnosticSinkEnabled)
                MilPresentation.Trace(
                    $"[release] 通道 {channel.Id} 句柄 0x{hResource.Value:x8} ⇒ 摘除={removed} " +
                    $"表内总数 {channel.Resources.Count}");
            return HResult.S_OK;
        }

        /// <summary>跨通道共享同一资源实例；跨分区被拒（上游语义：句柄只在分区内有效）。</summary>
        public static int MilResource_DuplicateHandle(
            IntPtr pSourceChannel, DUCE.ResourceHandle original,
            IntPtr pTargetChannel, ref DUCE.ResourceHandle duplicate)
        {
            MilChannel source = MilChannelRegistry.Resolve(pSourceChannel);
            MilChannel target = MilChannelRegistry.Resolve(pTargetChannel);
            if (source == null || target == null) return HResult.E_HANDLE;
            if (!ReferenceEquals(source.Partition, target.Partition)) return HResult.E_INVALIDARG;

            MilResource shared = source.Resources.Lookup(original);
            if (shared == null) return HResult.E_HANDLE;

            target.Resources.Duplicate(shared, ref duplicate);
            if (MilPresentation.DiagnosticSinkEnabled)
                MilPresentation.Trace(
                    $"[dup] 0x{original.Value:x8} 从通道 {source.Id} → 通道 {target.Id}，" +
                    $"新句柄 0x{duplicate.Value:x8}（目标表内总数 {target.Resources.Count}）");
            return HResult.S_OK;
        }

        // ==================================================================
        //  命令流
        // ==================================================================

        /// <summary>整条命令一次写入。sendInSeparateBatch=true 时立即执行。</summary>
        public static int MilResource_SendCommand(
            byte* pbData, uint cbSize, bool sendInSeparateBatch, IntPtr pChannel)
        {
            // 波46 · D-G54：这条是 `SetRoot` 的**唯一入口**（见 MilChannel.SendCommand 注释）。
            // 本行回答"命令有没有到桥"；`Resolve == null` 那一支就是**静默丢命令**。
            {
                int id0 = (pbData != null && cbSize >= 4) ? *(int*)pbData : -1;
                // ⚠️ 仪器缺口（设计稿 §3.4，W46F 点名、本趟顺手补）：载荷为空
                //   （`pbData == null || cbSize == 0`）时 `id0 = -1`，而 `CmdLogWanted(-1)`
                //   在白名单模式下**恒 false** ⇒ "载荷为空被丢"这一形态在
                //   `WPF_LINUX_CMDLOG_ID=…` 跑法下**看不见**。这一支属于"命令被拒"类台账
                //   （与下面的 `E_HANDLE 被丢弃` 同族）⇒ **不受 id 过滤**。只加打印，不改语义。
                if (id0 < 0)
                {
                    WpfGfx.Linux.Resources.MilChannel.CmdLog(
                        $"[native] ★载荷为空 MilResource_SendCommand pChannel=0x{(long)pChannel:x} " +
                        $"pbData={(pbData == null ? "null" : "非空")} cbSize={cbSize} ⇒ 返回 E_INVALIDARG（命令**没进桥**）");
                }
                else if (WpfGfx.Linux.Resources.MilChannel.CmdLogWanted(id0))
                {
                    WpfGfx.Linux.Resources.MilChannel.CmdLog(
                        $"[native] MilResource_SendCommand pChannel=0x{(long)pChannel:x} id=0x{id0:x} cbSize={cbSize} 独立批={sendInSeparateBatch}");
                }
            }
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null)
            {
                WpfGfx.Linux.Resources.MilChannel.CmdLog(
                    $"[native] ★E_HANDLE MilResource_SendCommand Resolve(0x{(long)pChannel:x}) == null ⇒ 命令**被丢弃**（registry.Count={MilChannelRegistry.Count}）");
                return HResult.E_HANDLE;
            }
            if (pbData == null) return HResult.E_INVALIDARG;
            if (cbSize == 0) return HResult.E_INVALIDARG;

            return channel.SendCommand(new ReadOnlySpan<byte>(pbData, (int)cbSize), sendInSeparateBatch);
        }

        /// <summary>开一条命令：先写 cbSize 字节头部，并预留 cbExtra 字节的变长载荷。</summary>
        public static int MilChannel_BeginCommand(
            IntPtr pChannel, byte* pbData, uint cbSize, uint cbExtra)
        {
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;
            if (pbData == null) return HResult.E_INVALIDARG;
            if (cbSize == 0) return HResult.E_INVALIDARG;

            return channel.BeginCommand(new ReadOnlySpan<byte>(pbData, (int)cbSize), cbExtra);
        }

        /// <summary>续写变长载荷；累计字节数不得超过 BeginCommand 声明的 cbExtra。</summary>
        public static int MilChannel_AppendCommandData(IntPtr pChannel, byte* pbData, uint cbSize)
        {
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;
            if (pbData == null) return HResult.E_INVALIDARG;
            if (cbSize == 0) return HResult.E_INVALIDARG;

            return channel.AppendCommandData(new ReadOnlySpan<byte>(pbData, (int)cbSize));
        }

        public static int MilChannel_EndCommand(IntPtr pChannel)
        {
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            return channel == null ? HResult.E_HANDLE : channel.EndCommand();
        }

        // ==================================================================
        //  通道通知窗口（T1/M7c 落地：NotImpl → State）
        // ==================================================================

        /// <summary>
        /// 把通知窗口 `(hwnd, message)` 登记到通道上。**幂等、可查询、可解绑。**
        ///
        /// 【M7c 侦察结论（为什么这条挡着 M2）】
        ///   `MediaContext` 在 `CreateChannels()` 里**必然**调用它
        ///   （`MediaContextNotificationWindow.SetAsChannelNotificationWindow`），
        ///   所以它是"任何 WPF 应用启动路径"上的必经点。M1 起返回 E_NOTIMPL，
        ///   被 `HRESULT.Check` 转成 NotImplementedException ⇒ Application 起不来。
        ///
        /// 【为什么改这里必须重建 .so】
        ///   真正承接这条 P/Invoke 的是 T1 的 NativeAOT 桥接
        ///   （`build/MilBridge/`，产物 `wpfgfx_cor3.so`）——
        ///   `build/MilBridge/src/MilBridge.Linux/Exports.g.cs` 里那层
        ///   `[UnmanagedCallersOnly(EntryPoint = "MilChannel_SetNotificationWindow")]`
        ///   把这个方法**AOT 烘焙进 .so**。只改托管源码不重建 .so 不生效（M7c 实测）。
        ///
        /// 【语义（HRESULT 表，落点见 MilChannelNotificationRegistry 的类注释）】
        ///   pChannel 无法解析          → E_HANDLE
        ///   hwnd == 0                  → 解绑；S_OK（幂等，未登记过也是 S_OK）
        ///   hwnd != 0 且 message == 0  → E_INVALIDARG
        ///   其余（含同值重复登记）      → 登记/覆盖登记；S_OK
        ///
        /// 【不真的 PostMessage】原因见 MilChannelNotificationRegistry 类注释：
        ///   本工程 DUCE 传输是**进程内**的，同线程呈现下没有需要唤醒的线程。
        /// </summary>
        public static int MilChannel_SetNotificationWindow(IntPtr pChannel, IntPtr hwnd, uint message)
        {
            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_HANDLE;

            // hwnd == 0：解绑。上游 DetachNotificationWindow 走的就是这条路。
            if (hwnd == IntPtr.Zero)
            {
                MilChannelNotificationRegistry.Unregister(pChannel);
                return HResult.S_OK;
            }

            // 非 0 窗口 + WM_NULL：这条消息永远不会到达，登记了也等不到唤醒 —— 视为非法参数。
            if (message == 0) return HResult.E_INVALIDARG;

            MilChannelNotificationRegistry.Register(pChannel, hwnd, message);
            return HResult.S_OK;
        }

        // ==================================================================
        //  M1 未实现的 2 个：返回 E_NOTIMPL（登记见 docs/unimplemented.md）
        // ==================================================================

        /// <summary>媒体命令（SafeMediaHandle）。M1 不接 MediaPlayer(0x17)，见 handoff 决策 4。</summary>
        public static int MilResource_SendCommandMedia(
            DUCE.ResourceHandle handle, IntPtr pMedia, IntPtr pChannel, bool notifyUceDirect)
            => HResult.E_NOTIMPL;

        /// <summary>
        /// 位图源命令：把 `pBitmapSource` 的内容作为 `MilCmdBitmapSource`(0x0c) 发到通道上。
        ///
        /// 【上游语义（逐跳可查）】
        ///   `DUCE.Channel.SendCommandBitmapSource`（`Common/Graphics/exports.cs:763-772`）
        ///   → `MilResource_SendCommandBitmapSource(imageHandle, pBitmapSource, _hChannel)`
        ///   → 原生 `apifunc.cpp:711-760`：`CHECKPTRARG(pIBitmapSource)` / `CHECKPTRARG(pChannel)`
        ///     都是 **E_INVALIDARG**；随后填 `MILCMD_BITMAP_SOURCE{Type,Handle,pIBitmapSource}` 并发出去。
        ///   接收侧与之配对的是 `CMilSlaveBitmap::ProcessSource`（本工程 `Commands/MilBitmapSource.cs:151`），
        ///   它拿不到可用位图时返回 **E_HANDLE**（IFCNULL 的语义）—— 两侧的错误码**不同**，别混。
        ///
        /// 【Linux 侧怎么传】上游那一列是 `IWICBitmapSource*`，靠"发送前 AddRef、接收方接手"
        ///   在**同一进程内**传引用（`apifunc.cpp:739` 的注释）。本实现的命令流是纯字节流
        ///   （解码器吃 `ReadOnlySpan<byte>`，可跨进程/可重放），所以那一列在 Linux 侧重定义为
        ///   **位图令牌**（宽度不变，见 `MilCommandLayout.cs` 的 0x0c 条目）：
        ///   发送时把位图登记进 `MilBitmapSourceTable` 领一个令牌，接收侧 `ProcessSource` 取走。
        ///
        /// 【为什么这里**拷一份**】`MilBitmapSourceTable.Register` 的契约是**所有权转移**
        ///   （接收侧最终会 Dispose 它），而 MIL 像素缓冲/CWIC wrapper 的位图是**别处拥有**的
        ///   ⇒ 直接交出去会双重释放。`BitmapSource` 在 MIL 侧是**只读**语义，
        ///   拷一份不改变任何可观察行为（与 #24 物化、与 v2 借用 的三处取舍一致：
        ///   只读⇒拷贝、读写⇒借用）。
        /// </summary>
        public static int MilResource_SendCommandBitmapSource(
            DUCE.ResourceHandle handle, IntPtr pBitmapSource, IntPtr pChannel)
        {
            // 上游 apifunc.cpp 的 CHECKPTRARG 语义：两个指针参数各自为空 ⇒ E_INVALIDARG
            if (pBitmapSource == IntPtr.Zero) return HResult.E_INVALIDARG;

            MilChannel channel = MilChannelRegistry.Resolve(pChannel);
            if (channel == null) return HResult.E_INVALIDARG;   // 同上：CHECKPTRARG(pChannel)

            if (!TryResolveBitmapForCommand(pBitmapSource, out SKBitmap source))
            {
                // 句柄认识不了 ⇒ 发送侧无法构造命令。接收侧对"拿不到可用位图"用的是 E_HANDLE
                // （`MilBitmapSource.cs:151` 的注释追到了 IFCNULL），这里沿用同一个码并在台账说明。
                MilDiagnostics.Note(
                    $"MilResource_SendCommandBitmapSource: 句柄 0x{(long)pBitmapSource:x} 解析不出位图 ⇒ E_HANDLE" +
                    "（MIL 像素缓冲 / CWIC wrapper 设备对象 / WIC 句柄 三者都不是）");
                return HResult.E_HANDLE;
            }

            SKBitmap copy = null;
            ulong token = 0;
            try
            {
                copy = source.Copy();                 // 所有权转移给登记表 ⇒ 必须是**我们自己**的一份
                token = MilBitmapSourceTable.Register(copy);
                if (token == 0) return HResult.E_HANDLE;

                S.MILCMD_BITMAP_SOURCE cmd = default;
                cmd.Type = MilCmd.MilCmdBitmapSource;
                cmd.Handle = handle;
                cmd.BitmapToken = token;

                int hr = channel.SendCommand(
                    new ReadOnlySpan<byte>(&cmd, sizeof(S.MILCMD_BITMAP_SOURCE)),
                    sendInSeparateBatch: false);
                if (HResult.Failed(hr))
                {
                    // 命令没发出去 ⇒ 令牌还挂在登记表上，必须摘掉（否则那张表只增不减）
                    MilBitmapSourceTable.Discard(token);
                    MilDiagnostics.Note(
                        $"MilResource_SendCommandBitmapSource: 通道 {channel.Id} 发送失败 hr=0x{hr:x8}，" +
                        "已撤回令牌（避免登记表只增不减）");
                    return hr;
                }

                copy = null;   // 已交给登记表 ⇒ 这里不再持有
                return HResult.S_OK;
            }
            finally
            {
                copy?.Dispose();
            }
        }

        /// <summary>
        /// 给位图源命令解析"这个句柄背后是哪张位图"（与 #24 同一集合）：
        /// ① MIL 像素缓冲（`MilPixelBufferTable`）；② CWIC wrapper 设备对象（物化位图）；
        /// ③ WIC shim 的句柄（现物化一份）。
        /// </summary>
        private static bool TryResolveBitmapForCommand(IntPtr handle, out SKBitmap bitmap)
        {
            bitmap = null;
            if (handle == IntPtr.Zero) return false;

            SKBitmap direct = MilPixelBufferTable.Resolve(handle);
            if (direct != null) { bitmap = direct; return true; }

            SKBitmap cwic = MilCwicWrapperTable.BitmapOf(handle);
            if (cwic != null) { bitmap = cwic; return true; }

            if (MilExternalHandleBridge.IsConnected && MilExternalHandleBridge.OwnsHandleProbe(handle) &&
                TryMaterializeWicSource(handle, out SKBitmap materialized, out string why))
            {
                bitmap = materialized;
                return true;
            }
            return false;
        }

        // ==================================================================
        //  诊断
        // ==================================================================

        /// <summary>当前存活的通道数。</summary>
        public static int ChannelCount => MilChannelRegistry.Count;
    }
}

// ======================================================================
//  M7a 缺口清单（复现命令见 docs/U2-PresentationCore-scan.md 附录，口径已修正）
//
//  【扫描口径】上游 Managed 编译集合 = PresentationCore in-tree 8 个文件
//  （UnsafeNativeMethodsMilCoreApi 44 / Composition 8 / SafeNativeMethodsMilCoreApi 3 /
//   MILUtilities 2 / MediaContextNotificationWindow 2 / HwndTarget 2 /
//   StreamAsIStream 1 / EventProxy 1 = 63 条属性）
//  ∪ Common/Graphics（exports.cs 19 + wgx_exports.cs 28 = 47 条属性）。
//  注意：U2 报告 §3.4 写的「in-tree 63 + Common/Graphics 41」漏算了 Common/Graphics 的 6 条，
//  实际属性总数是 **110**，去重后的导出名是 **108**（不是 102），方法名 106 个。
//  （WpfGfx/include/{exports,wgx_exports}.cs 是 C++ 工程里的同名副本，导出名集合与
//   Common/Graphics 完全一致，不额外增加名字。）
//
//  【M7a 之前的存量】MilNative 的 16 个方法覆盖了 16 个**方法名**、
//  14 个**导出名**（MilConnection_CloseBatch/CommitChannel 的 EntryPoint 分别叫
//  MilChannel_CloseBatch/MilChannel_CommitChannel，所以按导出名算只命中 14 个）。
//
//  【缺口】按导出名 **94 个**（= 108 − 14），按方法名 **90 个**（= 106 − 16）。
//  两者差的 4 个是：MilChannel_CloseBatch、MilChannel_CommitChannel（语义已由既有
//  MilConnection_CloseBatch/CommitChannel 提供，本轮补两个显式别名），以及
//  MILCopyPixelBuffer / MILRenderTargetBitmapGetBitmap 这类"方法名与导出名不同形"
//  的条目——本轮一律以**导出名**为准命名方法，使原生导出表与托管调用点同名。
//
//  【本轮新增 94 个导出，分组与语义落点】
//   · MilUtility_* 几何 15 个        → 真几何（SkiaPath + 自写展平/面积/命中）
//   · MilGlyphRun_/MilGlyphCache_ 6 个 → Text/ 的 SKTypeface + MIL 路径数据序列化
//   · HWND 绑定 4 个                 → MilHwndRegistry（身份映射，接窗留给后续 milestone）
//   · 连接/后向消息/锁 12 个          → MilConnectionTable + 通道批次语义 + Monitor（可重入）
//   · 离屏位图/工厂 15 个             → SkiaSharp 位图 + 引用计数对象表（D3D 互操作除外）
//   · 媒体 21 个                     → E_NOTIMPL（handoff 的 U3 口径）+ 未实现台账
//   · WIC 色彩上下文 3 个             → MilColorContextTable（真实 profile 字节存储）
//   · 版本/引用计数/杂项 18 个         → 真实现或身份映射，逐条见各文件头注释
//
//  逐条明细与"当前实现深度"见 Interop/MilNative.Exports.cs 的 ExportDepth 表。
// ======================================================================
