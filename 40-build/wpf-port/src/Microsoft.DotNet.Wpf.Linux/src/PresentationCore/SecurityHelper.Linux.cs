// ⚠ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-securityzone.py 生成，**不要手改**。
//
// 它与上游 Shared/MS/Internal/SecurityHelper.cs **逐字相同**，只把
// `MapUrlToZoneWrapper(Uri)` 换成了 Linux 版（直接返回 URLZONE_LOCAL_MACHINE）。
// 原因：上游实现要 marshal urlmon 的 COM 接口指针，.NET 在 Linux 上拒绝 marshal，
// 导致 BitmapImage(fileUri) 在走到 WIC 之前就抛 MarshalDirectiveException。
// 详细推导、实测栈与恢复条件：见生成脚本的文件头。
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

         // ── WPF-on-Linux（T2 · 补丁 H）：Linux 上**没有** URL 安全区域这个概念 ────────────
         //   上游实现会调 urlmon 的 CoInternetCreateSecurityManager
         //   （声明见 Shared/MS/Win32/UnsafeNativeMethodsOther.cs:57-61，带
         //    [MarshalAs(UnmanagedType.Interface)]）—— .NET 在 Linux 上**拒绝 marshal COM 接口指针**，
         //   于是 BitmapImage(fileUri) 在**走到 WIC 之前**就抛
         //   `MarshalDirectiveException: Cannot marshal 'parameter #1'`（实测栈见文件头）。
         //
         //   语义：本方法返回的是 IE/urlmon 的 URL 区域（Internet/Intranet/LocalMachine…）。
         //   Linux 上没有 urlmon、没有 IE zone、没有 Mark-of-the-Web ⇒ 该概念不存在；
         //   上游自己的兜底值就是 URLZONE_LOCAL_MACHINE（注释原文：
         //   "fail securely this is the most priveleged zone"）⇒ Linux 上直接返回它，
         //   即"本机内容、不施加区域限制"—— 这是**如实**表达"限制在本平台不存在"。
         //
         //   这与本工程既有先例同一套逻辑：非 Windows 上不设 XamlAccessLevel（CAS 已不存在）。
         //   ⚠ 不伪造 COM 对象、不假装调用成功；恢复条件见 docs/unimplemented.md（需真正实现 zone 判定）。
         internal static int MapUrlToZoneWrapper(Uri uri)
         {
              return NativeMethods.URLZONE_LOCAL_MACHINE ;
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

