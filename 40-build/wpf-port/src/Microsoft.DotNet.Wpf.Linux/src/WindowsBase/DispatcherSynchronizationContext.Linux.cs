// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-windowsbase-focus-wait.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/DispatcherSynchronizationContext.cs` 逐字复制 + **1 处修法**（`D-G65`：`Wait` 的两分支合一）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，
// 不会静默产出未打补丁的副本（那会让"修了"变成一行都没改而门禁照绿）。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;

namespace System.Windows.Threading
{
    /// <summary>
    ///     SynchronizationContext subclass used by the Dispatcher.
    /// </summary>
    public sealed class DispatcherSynchronizationContext : SynchronizationContext
    {
        /// <summary>
        ///     Constructs a new instance of the DispatcherSynchroniazationContext
        ///     using the current Dispatcher and normal post priority.
        /// </summary>
        public DispatcherSynchronizationContext(): this(Dispatcher.CurrentDispatcher, DispatcherPriority.Normal)
        {
        }

        /// <summary>
        ///     Constructs a new instance of the DispatcherSynchroniazationContext
        ///     using the specified Dispatcher and normal post priority.
        /// </summary>
        public DispatcherSynchronizationContext(Dispatcher dispatcher): this(dispatcher, DispatcherPriority.Normal)
        {
        }

        /// <summary>
        ///     Constructs a new instance of the DispatcherSynchroniazationContext
        ///     using the specified Dispatcher and the specified post priority.
        /// </summary>
        public DispatcherSynchronizationContext(Dispatcher dispatcher, DispatcherPriority priority)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);

            Dispatcher.ValidatePriority(priority, "priority");
            
            _dispatcher = dispatcher;
            _priority = priority;

            // Tell the CLR to call us when blocking.
            SetWaitNotificationRequired();        
        }
        

        /// <summary>
        ///     Synchronously invoke the callback in the SynchronizationContext.
        /// </summary>
        public override void Send(SendOrPostCallback d, Object state)
        {
            // Call the Invoke overload that preserves the behavior of passing
            // exceptions to Dispatcher.UnhandledException.  
            if(BaseCompatibilityPreferences.GetInlineDispatcherSynchronizationContextSend() && _dispatcher.CheckAccess())
            {
                // Same-thread, use send priority to avoid any reentrancy.
                _dispatcher.Invoke(DispatcherPriority.Send, d, state);
            }
            else
            {
                // Cross-thread, use the cached priority.
                _dispatcher.Invoke(_priority, d, state);
            }
        }

        /// <summary>
        ///     Asynchronously invoke the callback in the SynchronizationContext.
        /// </summary>
        public override void Post(SendOrPostCallback d, Object state)
        {
            // Call BeginInvoke with the cached priority.  Note that BeginInvoke
            // preserves the behavior of passing exceptions to
            // Dispatcher.UnhandledException unlike InvokeAsync.  This is
            // desireable because there is no way to await the call to Post, so
            // exceptions are hard to observe.
            _dispatcher.BeginInvoke(_priority, d, state);
        }

        /// <summary>
        ///     Wait for a set of handles.
        /// </summary>
        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            // ══ WPF-on-Linux 修法（在册债务 `D-G65`）：上游的两分支**合一**，都走托管 `WaitHelper` ══
            //
            // 上游按 Dispatcher 的「禁用处理」计数分叉：非零时改调 Win32 的等待 API。
            // 那条路的**目的**是 Windows 专属的 —— 避免 CLR 的锁等待替我们泵消息
            // （Windows 上被 SENT 到本窗口的消息会在 CLR 等待期间被派发 ⇒ 不可预期的重入）。
            //
            // Linux 上这个目的不需要靠它，而它本身**在原理上不可能成立**：
            //   ① 传进去的"句柄"是 .NET 运行期等待子系统（`WaitSubsystem`）内部的对象 id，
            //      **不是** Win32 HANDLE ⇒ 宿主的 C 代码无法 wait 它。本工程的 shim 只能
            //      返回失败（`src/WpfGfx.Linux.Native/src/win32_misc.c` 的失败桩），
            //      而失败即抛：`MS.Win32.UnsafeNativeMethods` 的包装在 `result == WAIT_FAILED`
            //      时抛 `Win32Exception`（`Shared/MS/Win32/UnsafeNativeMethodsOther.cs:135`）。
            //   ② 于是只要 UI 线程在锁上被争用（CLR 把"等待"通知给当前 SynchronizationContext），
            //      而此刻该计数非零 ⇒ `Win32Exception (50)` ⇒ 未处理异常 ⇒ **进程死**。
            //      最小复现与两极化读数见 `src/Linux/build/MilBridge/W56A-report.md`。
            //   ③ 托管 `SynchronizationContext.WaitHelper` 在 Linux 上**真等待**（实测：
            //      无信号 300 ms ⇒ 返回 258 `WAIT_TIMEOUT`；400 ms 后置信号 ⇒ 返回 0
            //      `WAIT_OBJECT_0`），且它只碰 .NET 的等待子系统、**不碰任何 Win32 消息队列**
            //      ⇒ 上游那个"防重入"的目的在本平台由它满足。
            //
            // ⇒ 两分支的差别只在 Windows 上成立，本平台**合一**。
            //   ⚠️ 同族的第二个站点 `Shared/MS/Internal/ReaderWriterLockWrapper.cs:287`
            //   （`NonPumpingSynchronizationContext.Wait`）**也是无条件**调那条原生等待，
            //   不在本应用器写域内 ⇒ 由 shim 那一层兜底（见 W56A 报告 §1）。
            return SynchronizationContext.WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }

        /// <summary>
        ///     Create a copy of this SynchronizationContext.
        /// </summary>
        public override SynchronizationContext CreateCopy()
        {
            DispatcherSynchronizationContext copy;
            
            if(BaseCompatibilityPreferences.GetReuseDispatcherSynchronizationContextInstance())
            {
                copy = this;
            }
            else
            {
                if(BaseCompatibilityPreferences.GetFlowDispatcherSynchronizationContextPriority())
                {
                    copy = new DispatcherSynchronizationContext(_dispatcher, _priority);
                }
                else
                {
                    copy = new DispatcherSynchronizationContext(_dispatcher, DispatcherPriority.Normal);
                }
            }

            return copy;
        }


        internal Dispatcher _dispatcher;
        private DispatcherPriority _priority;
    }
}

