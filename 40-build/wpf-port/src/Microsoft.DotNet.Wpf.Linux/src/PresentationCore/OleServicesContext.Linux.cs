// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-olecontext.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/OleServicesContext.cs`
//        逐字复制 + 5 处 Linux 守卫（见下方补丁 K 标记）。
// 每次运行该脚本都会从上游重读重生成；锚点对不上时**报错退出**。
//
// 为什么必须打：
//   Linux 上线程永远不是 STA（GetApartmentState() 恒 Unknown）⇒ 5 处硬 STA 检查全部必然抛
//   ThreadStateException。其中 `SetDispatcherThread` 挡在
//   `Window.ShowHelper → HwndSource.Initialize:334 → DragDrop.RegisterDropTarget` 上，
//   即**每一个 WPF 窗口第一次 Show()**。
//
// ↓↓↓ 以下为上游原文（仅 5 处有补丁 K 标记）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Description: Ole Services for DragDrop and Clipboard.

using MS.Win32;
using MS.Internal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Input;

using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace System.Windows;

/// <summary>
///  This class manages Ole services for DragDrop and Clipboard.
///  The instance of OleServicesContext class is created per Thread...Dispatcher.
/// </summary>
/// <remarks>
/// </remarks>
// threading issues
//  - This class needs to be modified to marshal calls over to the Dispatcher/STA thread.
//  - Once we have an event we can listen to when a Dispatcher
//    shuts down, we should use that.  We currently listen to Dispatcher shutdown, which has no thread
//    affinity -- it could happen on any thread, which breaks us.
internal class OleServicesContext
{
    // This is a slot to store OleServicesContext class per thread.
    private static readonly LocalDataStoreSlot s_threadDataSlot = Thread.AllocateDataSlot();

#if DEBUG
    // Ref count of calls to OleInitialize/OleUnitialize.
    private int _debugOleInitializeRefCount;
#endif // DEBUG

    /// <summary>
    /// Instantiates a OleServicesContext.
    /// </summary>
    private OleServicesContext()
    {
        // We need to get the Dispatcher Thread in order to get OLE DragDrop and Clipboard services that
        // require STA.
        SetDispatcherThread();
    }

    /// <summary>
    ///  Get the ole services context associated with the current Thread.
    /// </summary>
    internal static OleServicesContext CurrentOleServicesContext
    {
        get
        {
            // Get the ole services context from the Thread data slot.
            OleServicesContext oleServicesContext = (OleServicesContext)Thread.GetData(s_threadDataSlot);

            if (oleServicesContext is null)
            {
                // Create OleSErvicesContext instance.
                oleServicesContext = new OleServicesContext();

                // Save the ole services context into the UIContext data slot.
                Thread.SetData(s_threadDataSlot, oleServicesContext);
            }

            return oleServicesContext;
        }
    }

    /// <summary>
    ///  Ensure the current thread is STA and OLE is initialized.
    /// </summary>
    internal static void EnsureThreadState()
    {
        // Poke the CurrentOleServicesContext to ensure it is created, this will throw if the current thread is not STA.
        _ = CurrentOleServicesContext;
    }

    /// <summary>
    ///  Call OLE Interop DoDragDrop().  Initiate OLE DragDrop.
    /// </summary>
    internal void OleDoDragDrop(
        IComDataObject dataObject,
        UnsafeNativeMethods.IOleDropSource dropSource,
        int allowedEffects,
        int[] finalEffect)
    {
        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // Linux 上线程永远不是 STA，而且 OLE 拖放**本身不存在**。
        // 抛 PlatformNotSupportedException（而不是 ThreadStateException）：后者会
        // 误导成"调用方线程用错了"，而真相是"这台机器上没有 OLE" ——
        // 与 M4 对 OLE 公开 API 的裁决同一口径（见 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs）。
        if (!System.OperatingSystem.IsWindows())
        {
            throw new System.PlatformNotSupportedException(
                "WPF-on-Linux: OLE 拖放（DoDragDrop）在 Linux 上不可用（U13）。");
        }

        InputManager inputManager = (InputManager)Dispatcher.CurrentDispatcher.InputManager;
        inputManager?.InDragDrop = true;

        try
        {
            UnsafeNativeMethods.DoDragDrop(dataObject, dropSource, allowedEffects, finalEffect);
        }
        finally
        {
            inputManager?.InDragDrop = false;
        }
    }

    /// <summary>
    ///  Call OLE Interop RegisterDragDrop()
    /// </summary>
    internal int OleRegisterDragDrop(HandleRef windowHandle, UnsafeNativeMethods.IOleDropTarget dropTarget)
    {
        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 没有 OLE drop target 可注册。注意：`HwndSource.Initialize`（HwndSource.cs:334）
        // **无条件**调用 DragDrop.RegisterDropTarget → 这里 ⇒ **不能抛**，否则
        // 每一个 WPF 窗口都建不出来。返回 S_OK = "没有对象需要注册"，这是真话。
        if (!System.OperatingSystem.IsWindows())
        {
            return 0;   // S_OK
        }

        return UnsafeNativeMethods.RegisterDragDrop(windowHandle, dropTarget);
    }

    /// <summary>
    ///  Call OLE Interop RevokeDragDrop()
    /// </summary>
    internal int OleRevokeDragDrop(HandleRef windowHandle)
    {
        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 与 OleRegisterDragDrop 对称：没有注册过，也就没有可撤销的。
        if (!System.OperatingSystem.IsWindows())
        {
            return 0;   // S_OK
        }

        return UnsafeNativeMethods.RevokeDragDrop(windowHandle);
    }

    /// <summary>
    ///  Initialize OleServicesContext that will call Ole initialize for ole services(DragDrop and Clipboard)
    ///  and add the disposed event handler of Dispatcher to clean up resources and uninitalize Ole.
    /// </summary>
    private void SetDispatcherThread()
    {
        int hr;

        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // Linux 上线程永远不是 STA（补丁 H 已为 InputManager 解过同一类问题），
        // 而且没有 ole32 可 OleInitialize。这里跳过两件事，但**保留**挂
        // ShutdownFinished 的结构 —— 与 OnDispatcherShutdown 的配平关系不变。
        if (!System.OperatingSystem.IsWindows())
        {
            Dispatcher.CurrentDispatcher.ShutdownFinished += new EventHandler(OnDispatcherShutdown);
            return;
        }

        // Initialize Ole services.
        // Balanced with OleUninitialize call in OnDispatcherShutdown.
        hr = OleInitialize();

        if (!NativeMethods.Succeeded(hr))
        {
            throw new SystemException(SR.Format(SR.OleServicesContext_oleInitializeFailure, hr));
        }

        // Add Dispatcher.Shutdown event handler. 
        // We will call ole Uninitialize and clean up the resource when UIContext is terminated.
        Dispatcher.CurrentDispatcher.ShutdownFinished += new EventHandler(OnDispatcherShutdown);
    }

    /// <summary>
    ///  This is a callback when the <see cref="Dispatcher"/> is shut down.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This method must be called before shutting down the application on the dispatcher thread. It must be called
    ///   by the same thread running the dispatcher and the thread must have its ApartmentState property set to
    ///   <see cref="ApartmentState.STA"/>.
    ///  </para>
    /// </remarks>
    private void OnDispatcherShutdown(object sender, EventArgs args)
    {
        // ── WPF-on-Linux M7c 补丁 K ──────────────────────────────────────────
        // 没有 OleInitialize 需要 OleUninitialize 配平（见 SetDispatcherThread）。
        if (!System.OperatingSystem.IsWindows())
        {
            return;
        }

        // Uninitialize Ole services.
        // Balanced with OleInitialize call in SetDispatcherThread.
        OleUninitialize();
    }

    // Wrapper for UnsafeNativeMethods.OleInitialize, useful for debugging.
    private int OleInitialize()
    {
#if DEBUG
        _debugOleInitializeRefCount++;
#endif // DEBUG
        return UnsafeNativeMethods.OleInitialize();
    }

    // Wrapper for UnsafeNativeMethods.OleUninitialize, useful for debugging.
    private int OleUninitialize()
    {
        int hr = UnsafeNativeMethods.OleUninitialize();
#if DEBUG
        _debugOleInitializeRefCount--;
        Invariant.Assert(_debugOleInitializeRefCount >= 0, "Unbalanced call to OleUnitialize!");
#endif // DEBUG

        return hr;
    }
}
