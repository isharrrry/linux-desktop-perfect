// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-uiautomationtypes-reservedvalue.py **生成**，不要手改。
//
// 内容 = 上游 `UIAutomation/UIAutomationTypes/MS/Internal/Automation/UiaCoreTypesApi.cs` 逐字复制 + **D2** 的两处短路：
//   · `UiaGetReservedNotSupportedValue()` → 返回进程内哨兵 `ReservedNotSupportedValue`
//   · `UiaGetReservedMixedAttributeValue()` → 返回进程内哨兵 `ReservedMixedAttributeValue`
// 为什么必须短路：那两个 raw P/Invoke 的 out 参数是 `[MarshalAs(UnmanagedType.IUnknown)]`
//   —— **COM 接口指针在 Linux 上不可封送**，实测抛 `MarshalDirectiveException`
//   （T3 的 WPF 级用例：`UiaCoreTypesApi.UiaGetReservedMixedAttributeValue`）。
// 语义：Windows 上它们是 UIA 核心的保留 COM 对象，客户端按**引用相等**判断"不支持 / 混合值"；
//   这里返回**本进程内唯一**的哨兵 ⇒ 同一侧引用相等仍然自洽。
//   ⚠️ **这是降级、不是对齐**：跨进程/跨 COM 的身份语义不存在。
// 每次运行该脚本都会从上游重读重生成；锚点计数对不上时**报错退出**。
//
// ↓↓↓ 以下为上游原文（仅 D2 的两处有改动）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Description: Imports from unmanaged UiaCore DLL


using System;
using System.Runtime.InteropServices;
using MS.Internal.UIAutomationTypes;
using MS.Win32;

namespace MS.Internal.Automation
{
    internal static class UiaCoreTypesApi
    {
        //------------------------------------------------------
        //
        //  Other API types
        //
        //------------------------------------------------------

        #region Other
        internal enum AutomationIdType
        {
            Property,
            Pattern,
            Event,
            ControlType,
            TextAttribute
        }

        internal const int UIA_E_ELEMENTNOTENABLED = unchecked((int)0x80040200);
        internal const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);
        internal const int UIA_E_NOCLICKABLEPOINT = unchecked((int)0x80040202);
        internal const int UIA_E_PROXYASSEMBLYNOTLOADED = unchecked((int)0x80040203);

        #endregion Other

        //------------------------------------------------------
        //
        //  Internal Methods
        //
        //------------------------------------------------------

        // [D2] 两个保留值的进程内哨兵（见上面两个方法的注释：**降级不是对齐**）。
        private static readonly object ReservedNotSupportedValue = new object();
        private static readonly object ReservedMixedAttributeValue = new object();

        #region Internal Methods

        //
        // Support methods...
        //

        internal static int UiaLookupId(AutomationIdType type, ref Guid guid)
        {   
            return RawUiaLookupId( type, ref guid );
        }

        internal static object UiaGetReservedNotSupportedValue()
        {
            // [D2] Linux：**短路**（上游是 UIA 核心的保留 COM 对象，这里没有 UIA 核心，
            //      而 raw P/Invoke 的 `IUnknown` out 参数在 Linux 上不可封送 ⇒ MarshalDirectiveException）。
            //      返回**本进程内唯一**的哨兵：同一个 API 每次给同一个对象 ⇒ 引用相等语义在这一侧自洽。
            //      ⚠️ **降级不是对齐**：跨进程/跨 COM 的身份语义不存在。
            return ReservedNotSupportedValue;
        }

        internal static object UiaGetReservedMixedAttributeValue()
        {
            // [D2] 同上：短路成进程内哨兵（详见 UiaGetReservedNotSupportedValue 的注释）。
            return ReservedMixedAttributeValue;
        }

        internal static bool SupportsWin7Identifiers()
        {
            IntPtr automationCoreHandle = LoadLibraryHelper.SecureLoadLibraryEx(DllImport.UIAutomationCore, IntPtr.Zero, UnsafeNativeMethods.LoadLibraryFlags.LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (automationCoreHandle != IntPtr.Zero)
            {
                IntPtr entryPoint = UnsafeNativeMethods.GetProcAddressNoThrow(new HandleRef(null, automationCoreHandle), StartListeningExportName);
                if (entryPoint != IntPtr.Zero)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion Internal Methods

        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        #region Private Methods
        
        /// Check hresult for error...
        private static void CheckError(int hr)
        {
            if (hr >= 0)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(hr, (IntPtr)(-1));
        }

        [DllImport(DllImport.UIAutomationCore, EntryPoint = "UiaLookupId", CharSet = CharSet.Unicode)]
        private static extern int RawUiaLookupId(AutomationIdType type, ref Guid guid);

        // [D2] Linux 不可用（IUnknown out-param 不可封送）—— 保留原文以便核对：
        // [DllImport(DllImport.UIAutomationCore, EntryPoint = "UiaGetReservedNotSupportedValue", CharSet = CharSet.Unicode)]
        // private static extern int RawUiaGetReservedNotSupportedValue([MarshalAs(UnmanagedType.IUnknown)] out object notSupportedValue);

        // [D2] Linux 不可用（IUnknown out-param 不可封送）—— 保留原文以便核对：
        // [DllImport(DllImport.UIAutomationCore, EntryPoint = "UiaGetReservedMixedAttributeValue", CharSet = CharSet.Unicode)]
        // private static extern int RawUiaGetReservedMixedAttributeValue([MarshalAs(UnmanagedType.IUnknown)] out object mixedAttributeValue);

        #endregion Private Methods

        #region Private Constants

        private const string StartListeningExportName = "SynchronizedInputPattern_StartListening";

        #endregion Private Constants
    }
}
