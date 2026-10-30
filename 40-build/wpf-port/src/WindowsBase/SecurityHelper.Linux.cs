// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SecurityHelper.cs`
//        逐字复制 + 插入 1 行 null 守卫（见下方 PATCH G 标记）。
// 每次运行 reapply-patches.py 都会从上游重读重生成；上游锚点找不到时会**报错退出**，
// 不会静默产出一个未打补丁的副本。
//
// 为什么必须打这个补丁：
//   Unix 上 `Microsoft.Win32.Registry.CurrentUser` 返回 **null**（不是抛异常），
//   而 `ReadRegistryValue` 直接 `baseRegistryKey.OpenSubKey(...)` → NullReferenceException。
//   触发链（T3 跑 HelloWpf 时定位到行、M7b 独立复核）：
//     MS.Internal.AvTrace.IsWpfTracingEnabledInRegistry()      AvTrace.cs:213
//       → SecurityHelper.ReadRegistryValue(Registry.CurrentUser, …)  SecurityHelper.cs:215
//       → MS.Internal.TraceDependencyProperty..cctor            AvTraceMessages.cs:11
//       → System.Windows.PresentationSource 静态构造失败 → HwndSource 不可用。
//   语义：Linux 上没有 Windows 注册表，"读不到 = 未配置"是真话；调用方拿到 null 后
//   走的正是"未启用 tracing"分支，**不丢日志**（Linux 上不存在 managed tracing 的配置源）。
//
// ↓↓↓ 以下为上游原文（仅插入处有 PATCH G 标记）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/***************************************************************************\
*
* Purpose:  Helper functions that require elevation but are safe to use.
*
\***************************************************************************/

using System.Security;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

#if !PBTCOMPILER
using MS.Win32;
using System.IO.Packaging;
#endif

#if PRESENTATION_CORE
using MS.Internal.AppModel;
#endif

#if PRESENTATIONFRAMEWORK_ONLY
using System.Diagnostics;
using System.Windows;
using MS.Internal.Utility;      // BindUriHelper
using MS.Internal.AppModel;
#endif

#if REACHFRAMEWORK
using MS.Internal.Utility;
#endif
#if WINDOWS_BASE
// This existed originally to allow FontCache service to 
// see the WindowsBase variant of this class. We no longer have
// a FontCache service, but over time other parts of WPF might
// have started to depend on this, so we leave it as-is for 
// compat. 
#endif

// The SecurityHelper class differs between assemblies and could not actually be
//  shared, so it is duplicated across namespaces to prevent name collision.
// This duplication seems hardly necessary now. We should continue
// trying to reduce it by pushing things from Framework to Core (whenever it makes sense).
#if WINDOWS_BASE
namespace MS.Internal.WindowsBase
#elif PRESENTATION_CORE
using MS.Internal.PresentationCore;
namespace MS.Internal // Promote the one from PresentationCore as the default to use.
#elif PRESENTATIONFRAMEWORK
namespace MS.Internal.PresentationFramework
#elif PBTCOMPILER
namespace MS.Internal.PresentationBuildTasks
#elif REACHFRAMEWORK
namespace MS.Internal.ReachFramework
#elif DRT
namespace MS.Internal.Drt
#else
#error Class is being used from an unknown assembly.
#endif
{
    internal static class SecurityHelper
    {

#if PRESENTATION_CORE

        internal static Uri GetBaseDirectory(AppDomain domain)
        {
            Uri appBase = null;
            appBase = new Uri(domain.BaseDirectory);
            return( appBase );
        }

         internal static int MapUrlToZoneWrapper(Uri uri)
         {
              int targetZone = NativeMethods.URLZONE_LOCAL_MACHINE ; // fail securely this is the most priveleged zone
              int hr = NativeMethods.S_OK ;
              object curSecMgr = null;
              hr = UnsafeNativeMethods.CoInternetCreateSecurityManager(
                                                                       null,
                                                                       out curSecMgr ,
                                                                       0 );
              if ( NativeMethods.Failed( hr ))
                  throw new Win32Exception( hr ) ;

              UnsafeNativeMethods.IInternetSecurityManager pSec = (UnsafeNativeMethods.IInternetSecurityManager) curSecMgr;

              string uriString = BindUriHelper.UriToString( uri ) ;
              //
              // special case the condition if file is on local machine or UNC to ensure that content with mark of the web
              // does not yield with an internet zone result
              //
              if (uri.IsFile)
              {
                  pSec.MapUrlToZone( uriString, out targetZone, MS.Win32.NativeMethods.MUTZ_NOSAVEDFILECHECK );
              }
              else
              {
                  pSec.MapUrlToZone( uriString, out targetZone, 0 );
              }
              //
              // This is the condition for Invalid zone
              //
              if (targetZone < 0)
              {
                throw new SecurityException( SR.Invalid_URI );
              }
              pSec = null;
              curSecMgr = null;
              return targetZone;
        }
#endif

#if DRT
        /// <remarks> The LinkDemand on Marshal.SizeOf() was removed in v4. </remarks>
        internal static int SizeOf(Type t)
        {
            return Marshal.SizeOf(t);
        }
#endif

#if DRT
        internal static int SizeOf(Object o)
        {
            return Marshal.SizeOf(o);
        }
#endif

#if WINDOWS_BASE || PRESENTATION_CORE || PRESENTATIONFRAMEWORK
        internal static Exception GetExceptionForHR(int hr)
        {
            return Marshal.GetExceptionForHR(hr, new IntPtr(-1));
        }
#endif

#if WINDOWS_BASE || PRESENTATION_CORE
        internal static void ThrowExceptionForHR(int hr)
        {
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
        }
        
        internal static int GetHRForException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            // GetHRForException fills a per thread IErrorInfo object with data from the exception
            // The exception may contain security sensitive data like full file paths that we do not
            // want to leak into an IErrorInfo
            int hr = Marshal.GetHRForException(exception);

            // Call GetHRForException a second time with a security safe exception object
            // to make sure the per thread IErrorInfo is cleared of security sensitive data
            Marshal.GetHRForException(new Exception());

            return hr;
        }

#endif

#if PRESENTATIONFRAMEWORK

        /// <summary>
        /// A helper method to do the necessary work to display a standard MessageBox.  This method performs
        /// and necessary elevations to make the dialog work as well.
        /// </summary>
        internal
        static
        void
        ShowMessageBoxHelper(
            System.Windows.Window parent,
            string text,
            string title,
            System.Windows.MessageBoxButton buttons,
            System.Windows.MessageBoxImage image
            )
        {
            // if we have a known parent window set, let's use it when alerting the user.
            if (parent != null)
            {
                System.Windows.MessageBox.Show(parent, text, title, buttons, image);
            }
            else
            {
                System.Windows.MessageBox.Show(text, title, buttons, image);
            }
        }
        /// <summary>
        /// A helper method to do the necessary work to display a standard MessageBox.  This method performs
        /// and necessary elevations to make the dialog work as well.
        /// </summary>
        internal
        static
        void
        ShowMessageBoxHelper(
            IntPtr parentHwnd,
            string text,
            string title,
            System.Windows.MessageBoxButton buttons,
            System.Windows.MessageBoxImage image
            )
        {
            // NOTE: the last param must always be MessageBoxOptions.None for this to be considered TreatAsSafe
            System.Windows.MessageBox.ShowCore(parentHwnd, text, title, buttons, image, MessageBoxResult.None, MessageBoxOptions.None);
        }
#endif

#if WINDOWS_BASE
        ///
        /// Read and return a registry value.
       internal static object ReadRegistryValue( RegistryKey baseRegistryKey, string keyName, string valueName )
       {
            object value = null;

            // ── WPF-on-Linux 补丁 G（由 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py 插入）──
            // Unix 上 Microsoft.Win32.Registry.CurrentUser 返回 **null**（不是抛异常），
            // 而上游只判了 OpenSubKey 的返回值 → 这里会 NRE。实测链路：
            //   AvTrace.IsWpfTracingEnabledInRegistry → SecurityHelper.ReadRegistryValue
            //   → TraceDependencyProperty..cctor → PresentationSource 静态构造失败 → HwndSource 不可用。
            // Linux 上没有 Windows 注册表："读不到 = 未配置" 是真话，调用方按"未启用 tracing" 处理。
            if (baseRegistryKey is null) { return null; }
            RegistryKey key = baseRegistryKey.OpenSubKey(keyName);
            if (key != null)
            {
                using( key )
                {
                    value = key.GetValue(valueName);
                }
            }

            return value;
        }
#endif // WINDOWS_BASE
}
}

