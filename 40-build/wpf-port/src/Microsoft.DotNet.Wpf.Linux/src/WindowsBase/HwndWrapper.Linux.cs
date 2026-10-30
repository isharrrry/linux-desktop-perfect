// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-shared-hwndwrapper-diag.py **生成**，不要手改。
//
// 内容 = 上游 `Shared/MS/Win32/HwndWrapper.cs` 逐字复制 + **补丁 P 的一处诊断接线**：
//   在**上游本来就有的** `_handle == 0`（建窗失败）分支里，多打一行 stderr，内容是 shim 的
//   `WpfLinuxWin32_LastError()`（`dpy_error` + `XSetErrorHandler` 抓到的 `x_error`）。
//   不抛、不吞、不改返回值、不动 `CreateWindowEx` 调用 —— 只是"让失败自己说话"。
// 目标程序集 = **WindowsBase**（`Dispatcher..ctor → MessageOnlyHwndWrapper → HwndWrapper` 都在这）。
// 每次运行该脚本都从上游重读重生成；锚点计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// ↓↓↓ 以下为上游原文（仅补丁 P 的一处有改动）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MS.Internal;
using MS.Internal.Interop;

namespace MS.Win32
{
    internal class HwndWrapper : DispatcherObject, IDisposable
    {
        static HwndWrapper()
        {
            s_msgGCMemory = UnsafeNativeMethods.RegisterWindowMessage("HwndWrapper.GetGCMemMessage");
        }

        public HwndWrapper(
            int classStyle,
            int style,
            int exStyle,
            int x,
            int y,
            int width,
            int height,
            string name,
            IntPtr parent,
            HwndWrapperHook[] hooks)
        {
            _ownerThreadID = Environment.CurrentManagedThreadId;


            // First, add the set of hooks.  This allows the hooks to receive the
            // messages sent to the window very early in the process.
            if(hooks != null)
            {
                for(int i = 0, iEnd = hooks.Length; i < iEnd; i++)
                {
                    if(null != hooks[i])
                        AddHook(hooks[i]);
                }
            }


            _wndProc = new HwndWrapperHook(WndProc);

            // We create the HwndSubclass object so that we can use its
            // window proc directly.  We will not be "subclassing" the
            // window we create.
            HwndSubclass hwndSubclass = new(_wndProc);
            
            // Register a unique window class for this instance.
            NativeMethods.WNDCLASSEX_D wc_d = new NativeMethods.WNDCLASSEX_D();

            IntPtr hNullBrush = UnsafeNativeMethods.CriticalGetStockObject(NativeMethods.NULL_BRUSH);

            if (hNullBrush == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            IntPtr hInstance = UnsafeNativeMethods.GetModuleHandle( null );

            // We need to keep the Delegate object alive through the call to CreateWindowEx().
            // Subclass.WndProc will install a better delegate (to the same function) when it
            // processes the first message.
            // But this first delegate needs be held alive until then.
            NativeMethods.WndProc initialWndProc = new NativeMethods.WndProc(hwndSubclass.SubclassWndProc);

            // The class name is a concat of AppName, ThreadName, and RandomNumber.
            // Register will fail if the string gets over 255 in length.
            // So limit each part to a reasonable amount.
            string appName;
            string currentDomainFriendlyName = AppDomain.CurrentDomain.FriendlyName;
            if (null != currentDomainFriendlyName && 128 <= currentDomainFriendlyName.Length)
                appName = currentDomainFriendlyName[..128];
            else
                appName = currentDomainFriendlyName;

            string threadName;
            if(null != Thread.CurrentThread.Name && 64 <= Thread.CurrentThread.Name.Length)
                threadName = Thread.CurrentThread.Name.Substring(0, 64);
            else
                threadName = Thread.CurrentThread.Name;

            // Create a suitable unique class name.
            _classAtom = 0;
            string randomName = Guid.NewGuid().ToString();
            string className = String.Format(CultureInfo.InvariantCulture, "HwndWrapper[{0};{1};{2}]", appName, threadName, randomName);

            wc_d.cbSize        = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX_D));
            wc_d.style         = classStyle;
            wc_d.lpfnWndProc   = initialWndProc;
            wc_d.cbClsExtra    = 0;
            wc_d.cbWndExtra    = 0;
            wc_d.hInstance     = hInstance;
            wc_d.hIcon         = IntPtr.Zero;
            wc_d.hCursor       = IntPtr.Zero;
            wc_d.hbrBackground = hNullBrush;
            wc_d.lpszMenuName  = "";
            wc_d.lpszClassName = className;
            wc_d.hIconSm       = IntPtr.Zero;

            // Register the unique class for this instance.
            // Note we use a GUID in the name so we are confident that
            // the class name should be unique.  And RegisterClassEx won't
            // fail (for that reason).
            _classAtom = UnsafeNativeMethods.RegisterClassEx(wc_d);

            // call CreateWindow
            _isInCreateWindow = true;
            try {
                _handle = UnsafeNativeMethods.CreateWindowEx(exStyle,
                    className,
                    name,
                    style,
                    x,
                    y,
                    width,
                    height,
                    new HandleRef(null,parent),
                    new HandleRef(null,IntPtr.Zero),
                    new HandleRef(null,IntPtr.Zero),
                    null);
            }
            finally
            {
                _isInCreateWindow = false;
                if(_handle == 0)
                {
                    // Because the HwndSubclass is pinned, but the HWND creation failed,
                    // we need to manually clean it up.
                    // [M7b 补丁 P] 诊断接线（**只多打一行 stderr，行为不变**）：把 shim 侧真原因带出来。
                    //   读点是 shim 的 `WpfLinuxWin32_LastError()`（dpy_error + XSetErrorHandler 抓的 x_error）。
                    WpfLinuxShimDiag.ReportCreateFailure(className, name, parent);
                    hwndSubclass.Dispose();
                }
            }
            GC.KeepAlive(initialWndProc);
        }


        ~HwndWrapper()
        {
            Dispose(/*disposing = */ false, 
                    /*isHwndBeingDestroyed = */ false);
        }
        
        public virtual void Dispose()
        {
            //             VerifyAccess();

            Dispose(/*disposing = */ true, 
                    /*isHwndBeingDestroyed = */ false);
            GC.SuppressFinalize(this);
        }            

        // internal Dispose(bool, bool)
        private void Dispose(bool disposing, bool isHwndBeingDestroyed)
        {
            if (_isDisposed)
            {
                // protect against re-entrancy:  Calling DestroyWindow here will send
                // a WM_NCDESTROY -- WndProc may catch this and call Dispose again.
                return;
            }

            if(disposing)
            {
                // diposing == false means we're being called from the finalizer
                // and can't follow any reference types that may themselves be
                // finalizable - thus don't call the Disposed callback.

                // Notify listeners that we are being disposed.
                if(Disposed != null)
                {
                    Disposed(this, EventArgs.Empty);
                }
            }

            // We are now considered disposed.
            _isDisposed = true;

            
            if (isHwndBeingDestroyed)
            {
                // The window is in the process of being destroyed.  We can't call UnregisterClass yet
                // so we'll ask the Dispatcher to do it later when the window is gone.
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, (DispatcherOperationCallback)UnregisterClass, _classAtom);
            }
            else if (_handle != 0)
            {
                // The window isn't in the process of being destroyed and it hasn't been destroyed yet
                // (we know this since we're listening for WM_NCDESTROY).  Since we're being disposed
                // we destroy it now.

                if(Environment.CurrentManagedThreadId == _ownerThreadID)
                {
                    // We are the owner thread, we can safely destroy the window and unregister
                    // the class
                    DestroyWindow(new DestroyWindowArgs(_handle, _classAtom));
                }
                else
                {
                    // Post a DispatcherOperation to ask the owner thread to destroy the window for us.
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        (DispatcherOperationCallback)DestroyWindow,
                        new DestroyWindowArgs(_handle, _classAtom));
                }
            }

         
            _classAtom = 0;
            _handle = default;
        }

        public IntPtr Handle => _handle;

        public event EventHandler Disposed;

        public void AddHook(HwndWrapperHook hook)
        {
            _hooks ??= [];
            _hooks.Insert(0, hook);
        }

        internal void AddHookLast(HwndWrapperHook hook)
        {
            _hooks ??= [];
            _hooks.Add(hook);
        }

        public void RemoveHook(HwndWrapperHook hook) => _hooks?.Remove(hook);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // The default result for messages we handle is 0.
            IntPtr result = IntPtr.Zero;
            WindowMessage message = (WindowMessage)msg;
        
            // Call all of the hooks
            if(_hooks is not null)
            {
                foreach(HwndWrapperHook hook in _hooks)
                {
                    result = hook(hwnd, msg, wParam, lParam, ref handled);

                    CheckForCreateWindowFailure(result, handled);

                    if(handled)
                    {
                        break;
                    }
                }
            }

            if (message == WindowMessage.WM_NCDESTROY)
            {
                Dispose(/*disposing = */ true, 
                        /*isHwndBeingDestroyed = */ true);
                GC.SuppressFinalize(this);

                // We want the default window proc to process this message as
                // well, so we mark it as unhandled.
                handled = false;
            }
            else if (message == s_msgGCMemory)
            {
                // This is a special message we respond to by forcing a GC Collect.  This
                // is used by test apps and such.
                IntPtr lHeap = (IntPtr)GC.GetTotalMemory((wParam == new IntPtr(1) )? true : false);
                result =  lHeap;
                handled = true;
            }

            CheckForCreateWindowFailure(result, true);

            // return our result
            return result;
        }

        private void CheckForCreateWindowFailure( IntPtr result, bool handled )
        {
            if( ! _isInCreateWindow )
                return;
            
            if( IntPtr.Zero != result )
            {
                System.Diagnostics.Debug.WriteLine("Non-zero WndProc result=" + result);
                if( handled )
                {
                    if( System.Diagnostics.Debugger.IsAttached )
                        System.Diagnostics.Debugger.Break();
                    else
                        throw new InvalidOperationException();
                }
            }
        }


        /// <summary>
        /// Destroys the window with the given handle and class atom and unregisters its window class
        /// </summary>
        /// <param name="args">A DestrowWindowParams instance</param>
        internal static object DestroyWindow(object args)
        {
            nint handle = ((DestroyWindowArgs)args).Handle;
            ushort classAtom = ((DestroyWindowArgs)args).ClassAtom;

            Invariant.Assert(handle != 0,
               "Attempting to destroy an invalid hwnd");

            UnsafeNativeMethods.DestroyWindow(new HandleRef(null, handle));

            UnregisterClass((object)classAtom);

            return null;
        }

        /// <summary>
        /// Unregisters the window class represented by classAtom
        /// </summary>
        /// <param name="arg">A ushort representing the class atom</param>
        internal static object UnregisterClass(object arg)
        {
            ushort classAtom = (ushort)arg;

            if (classAtom != 0)
            {
                IntPtr hInstance = UnsafeNativeMethods.GetModuleHandle(null);
                UnsafeNativeMethods.UnregisterClass(
                                new IntPtr(classAtom), //* this function is defined as taking a type lpClassName - but this can be an atom. 2 Low Bytes are the atom*/ 
                                hInstance);
            }

            return null;
        }

        // This is used only so that DestroyWindow can take a single object parameter
        // in order for it to be called by a DispatcherOperationCallback
        internal class DestroyWindowArgs
        {
            public DestroyWindowArgs(IntPtr handle, ushort classAtom)
            {
                _handle = handle;
                _classAtom = classAtom;
            }

            public IntPtr Handle => _handle;

            public ushort ClassAtom => _classAtom;

            private readonly IntPtr _handle;
            private readonly ushort _classAtom;
        }
        

        private IntPtr _handle;
        private UInt16 _classAtom;
        private WeakReferenceList _hooks;
        private int _ownerThreadID;
        
        private HwndWrapperHook _wndProc;
        private bool _isDisposed;

        private bool _isInCreateWindow = false;     // debugging variable (temporary)

        // Message to cause a dispose.  We need this to ensure we destroy the window on the right thread.
        private static WindowMessage s_msgGCMemory;
    } // class RawWindow

    /// <summary>
    /// [M7b 补丁 P] 建窗失败诊断：`HwndWrapper` 只说 `Win32Exception(1400)`，而 1400 是本工程
    /// **自己映射**的复用值 ⇒ 真原因要问 shim（`WpfLinuxWin32_LastError()`：X 连接不可用 + 异步 X 错误）。
    /// 只打 stderr、有界（≤8 行）、任何异常都吞在诊断里（诊断不许把失败路径搞坏）。
    /// </summary>
    internal static class WpfLinuxShimDiag
    {
        private static int s_lines;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WpfLinuxWin32_LastError", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr ShimLastErrorRaw();

        internal static void ReportCreateFailure(string className, string title, System.IntPtr parent)
        {
            if (s_lines >= 8) return;
            s_lines++;
            string reason;
            try
            {
                System.IntPtr p = ShimLastErrorRaw();
                reason = (p == System.IntPtr.Zero) ? "(空)" :
                         (System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p) ?? "(空)");
            }
            catch (System.Exception e) { reason = "(读 shim LastError 抛 " + e.GetType().Name + ")"; }

            try
            {
                System.Console.Error.WriteLine(
                    "[SHIM_DIAG] HwndWrapper 建窗失败（CreateWindowEx 返回 0） cls=\"" + className
                    + "\" title=\"" + title + "\" parent=0x" + parent.ToInt64().ToString("x"));
                System.Console.Error.WriteLine("[SHIM_DIAG]   shim 侧真原因："
                    + (string.IsNullOrEmpty(reason) ? "(空：X 层没记原因)" : reason));
                System.Console.Error.Flush();
            }
            catch { }
        }
    }
}

