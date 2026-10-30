// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-textservices.py **生成**，不要手改。
//
// 内容 = 上游 `Shared/MS/Internal/TextServicesLoader.cs` 逐字复制 + **补丁 M 的两处守卫 + 补丁 O 的一处守卫**：
//   ① `Registry.CurrentUser.OpenSubKey(...)`            → `Registry.CurrentUser?.OpenSubKey(...)`
//   ② `IterateSubKeys(Registry.LocalMachine, ...)`      → 先取局部变量，null ⇒ `return false`
//   ③ `Load()`：`Invariant.Assert(… STA …)` **之前**插入 非 Windows ⇒ `return null`
// 为什么必须打 ①②：Unix 上 `Microsoft.Win32.Registry.CurrentUser/LocalMachine` 返回 **null**
//   （不是抛异常），而上游直接解引用 ⇒ NullReferenceException ⇒ 真应用收到**任何鼠标输入**都 SIGABRT
//   （栈：TextServicesManager.PreProcessInput → TextServicesLoader.TIPsWantToRun → :192）。
// 为什么必须打 ③：`Load()` 的 STA 断言排在 `if (ServicesInstalled)` **之前**（⇒ 与 ①② 的取值无关），
//   而 .NET on Unix 上该断言**不可满足**（实测：主线程 `GetApartmentState() == Unknown`；
//   `SetApartmentState(STA)` 抛 PlatformNotSupportedException；`TrySetApartmentState(STA)` 返回 False）
//   ⇒ 任何"聚焦 TextBox"（TextEditor.InitTextStore → Load）都 FailFast（T3 wave-8 实测 exit=134）。
//   ③ 走的是上游**文档化的合法返回值**（`Load()` 的 "May return null…" + `ServicesInstalled` remarks
//   "false ⇒ Load is guarenteed to return null"），**断言原文一字未改**。
// 语义：Linux 上没有 CTF/TIP 注册表 ⇒ "没有安装文本服务"（本方法契约里的合法返回值）；
//   Windows 上两个根键恒非 null、且 `OperatingSystem.IsWindows()` 为真 ⇒ 三处守卫永不触发，行为逐字不变。
// 每次运行该脚本都会从上游重读重生成；锚点计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// ↓↓↓ 以下为上游原文（仅补丁 M 的两处 + 补丁 O 的一处有改动）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// 
//
// Description: Creates ITfThreadMgr instances, the root object of the Text
//              Services Framework.
//
//  
//
//

using System.Threading;
using Microsoft.Win32;
using MS.Win32;

#if WINDOWS_BASE
#elif PRESENTATION_CORE
    using MS.Internal.PresentationCore;
#elif PRESENTATIONFRAMEWORK
    using MS.Internal.PresentationFramework;
#elif DRT
    using MS.Internal.Drt;
#else
using MS.Internal.YourAssemblyName;
#endif

namespace MS.Internal
{
    // Creates ITfThreadMgr instances, the root object of the Text Services
    // Framework.
    internal class TextServicesLoader
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        // Private ctor to prevent anyone from instantiating this static class.
        private TextServicesLoader() {}

        #endregion Constructors
 
        //------------------------------------------------------
        //
        //  Public Methods
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Public Properties
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Public Events
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Protected Methods
        //
        //------------------------------------------------------
 
        //------------------------------------------------------
        //
        //  Internal Methods
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Internal Properties
        //
        //------------------------------------------------------

        #region Internal Properties
        
        /// <summary>
        /// Loads an instance of the Text Services Framework.
        /// </summary>
        /// <returns>
        /// May return null if no text services are available.
        /// </returns>
        internal static UnsafeNativeMethods.ITfThreadMgr Load()
        {
            UnsafeNativeMethods.ITfThreadMgr threadManager;
            
            // [M7c 补丁 O] Linux：没有 CTF/Cicero（无 msctf / TF_CreateThreadMgr）⇒ 按本方法**自己的契约**返回 null。
            //   依据①本方法 XML 文档："May return null if no text services are available."
            //   依据②`ServicesInstalled` 的 remarks（上游 :121，原文拼写）："If this method returns false,
            //          TextServicesLoader.Load is guarenteed to return null."
            //          ⇒ 返回 null 是**契约内的合法路径**，不是"把断言吞掉继续跑"。
            //   依据③调用方本来就处理 null：TextEditor.cs:1529 `threadManager = TextServicesLoader.Load();`
            //          紧跟 `if (threadManager != null) { … }`（上游原文已核）。
            //   为什么必须在断言**之前**：下面那条断言在 .NET on Unix 上**不可满足** —— 实测（net10.0/linux-x64）：
            //     进入主线程 `GetApartmentState() == Unknown`（不是 MTA）；`SetApartmentState(STA)` 抛
            //     PlatformNotSupportedException；`TrySetApartmentState(STA)` 返回 False 且状态仍 Unknown
            //     ⇒ 任何"聚焦 TextBox"（TextEditor.InitTextStore → Load）都会 FailFast（T3 wave-8 实测 exit=134）。
            //   **断言原文一行不改**：Windows 上（含断言次序）逐字不变；本守卫在 Windows 上不触发。
            if (!System.OperatingSystem.IsWindows())
            {
                return null;
            }

            Invariant.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Load called on MTA thread!");

            if (ServicesInstalled)
            {
                // NB: a COMException here means something went wrong initialzing Cicero.
                // Cicero will throw an exception if it doesn't think it should have been
                // loaded (no TIPs to run), you can check that in msctf.dll's NoTipsInstalled
                // which lives in nt\windows\advcore\ctf\lib\immxutil.cpp.  If that's the
                // problem, ServicesInstalled is out of sync with Cicero's thinking.
                if (UnsafeNativeMethods.TF_CreateThreadMgr(out threadManager) == NativeMethods.S_OK)
                {
                    return threadManager;
                }
            }

            return null;
        }

        /// <summary>
        /// Informs the caller if text services are installed for the current user.
        /// </summary>
        /// <returns>
        /// true if one or more text services are installed for the current user, otherwise false.
        /// </returns>
        /// <remarks>
        /// If this method returns false, TextServicesLoader.Load is guarenteed to return null.
        /// Callers can use this information to avoid overhead that would otherwise be
        /// required to support text services.
        /// </remarks>
        internal static bool ServicesInstalled
        {
            get
            {
                lock (s_servicesInstalledLock)
                {
                    if (s_servicesInstalled == InstallState.Unknown)
                    {
                        s_servicesInstalled = TIPsWantToRun() ? InstallState.Installed : InstallState.NotInstalled;
                    }
                }

                return (s_servicesInstalled == InstallState.Installed);
            }
        }

        #endregion Internal Properties

        //------------------------------------------------------
        //
        //  Internal Events
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        #region Private Methods

        //
        // This method tries to stop Avalon from loading Cicero when there are no TIPs to run.
        // The perf tradeoff is a typically small number of registry checks versus loading and
        // initializing cicero.
        //
        // The Algorithm:
        //
        // Do a quick check vs. the global disable flag, return false if it is set.
        // For each key under HKLM\SOFTWARE\Microsoft\CTF\TIP (a TIP or category clsid)
        //  If the the key has a LanguageProfile subkey (it's a TIP clsid)
        //      Iterate under the matching TIP entry in HKCU.
        //          For each key under the LanguageProfile (a particular LANGID)
        //              For each key under the LANGID (an assembly GUID)
        //                  Try to read the Enable value.
        //                  If the value is set non-zero, then stop all processing and return true.
        //                  If the value is set zero, continue.
        //                  If the value does not exist, continue (default is disabled).
        //      If any Enable values were found under HKCU for the TIP, then stop all processing and return false.
        //      Else, no Enable values have been found thus far and we keep going to investigate HKLM.
        //      Iterate under the TIP entry in HKLM.
        //          For each key under the LanguageProfile (a particular LANGID)
        //              For each key under the LANGID (an assembly GUID)
        //                  Try to read the Enable value.
        //                  If the value is set non-zero, then stop all processing and return true.
        //                  If the value does not exist, then stop all processing and return true (default is enabled).
        //                  If the value is set zero, continue.
        // If we finish iterating all entries under HKLM without returning true, return false.
        //

        private static bool TIPsWantToRun()
        {
            object obj;
            RegistryKey key;
            bool tipsWantToRun = false;

            // [M7c 补丁 M] Unix 上 Registry.CurrentUser 是 **null**（不是抛异常）——
            //   补丁 J 修的是同一个家族，这一处当时漏了。`?.` 之后走的是上游**原有的**
            //   "没有这个键"分支（下面每一处都判了 key != null）。
            key = Registry.CurrentUser?.OpenSubKey("Software\\Microsoft\\CTF", false);

            // Is cicero disabled completely for the current user?
            if (key != null)
            {
                obj = key.GetValue("Disable Thread Input Manager");

                if (obj is int && (int)obj != 0)
                    return false;
            }

            // Loop through all the TIP entries for machine and current user.
            // [M7c 补丁 M] Unix 上 Registry.LocalMachine 也是 null；没有 HKLM 就是
            //   "这台机器没有 TIP"，按本方法的契约（返回 false ⇒ Load 返回 null）返回 false。
            //   Windows 上恒非 null ⇒ 这一支是死代码，行为逐字不变。
            RegistryKey hklm = Registry.LocalMachine;
            if (hklm == null)
            {
                return false;
            }

            tipsWantToRun = IterateSubKeys(hklm, "SOFTWARE\\Microsoft\\CTF\\TIP",new IterateHandler(SingleTIPWantsToRun), true) == EnableState.Enabled;

            return tipsWantToRun;
        }

        // Returns EnableState.Enabled if one or more TIPs are installed and
        // enabled for the current user.
        private static EnableState SingleTIPWantsToRun(RegistryKey keyLocalMachine, string subKeyName, bool localMachine)
        {
            EnableState result;

            if (subKeyName.Length != CLSIDLength)
                return EnableState.Disabled;

            // We want subkey\LanguageProfile key.
            // Loop through all the langid entries for TIP.

            // First, check current user.
            result = IterateSubKeys(Registry.CurrentUser, "SOFTWARE\\Microsoft\\CTF\\TIP\\" + subKeyName + "\\LanguageProfile", new IterateHandler(IsLangidEnabled), false);

            // Any explicit value short circuits the process.
            // Otherwise check local machine.
            if (result == EnableState.None || result == EnableState.Error)
            {
                result = IterateSubKeys(keyLocalMachine, subKeyName + "\\LanguageProfile", new IterateHandler(IsLangidEnabled), true);

                if (result == EnableState.None)
                {
                    result = EnableState.Enabled;
                }
            }

            return result;
        }

        // Returns EnableState.Enabled if the supplied subkey is a valid LANGID key with enabled
        // cicero assembly.
        private static EnableState IsLangidEnabled(RegistryKey key, string subKeyName, bool localMachine)
        {
            if (subKeyName.Length != LANGIDLength)
                return EnableState.Error;

            // Loop through all the assembly entries for the langid
            return IterateSubKeys(key, subKeyName, new IterateHandler(IsAssemblyEnabled), localMachine);
        }

        // Returns EnableState.Enabled if the supplied assembly key is enabled.
        private static EnableState IsAssemblyEnabled(RegistryKey key, string subKeyName, bool localMachine)
        {
            RegistryKey subKey;
            object obj;

            if (subKeyName.Length != CLSIDLength)
                return EnableState.Error;

            // Open the local machine assembly key.
            subKey = key.OpenSubKey(subKeyName);

            if (subKey == null)
                return EnableState.Error;

            // Try to read the "Enable" value.
            obj = subKey.GetValue("Enable");

            if (obj is int)
            {
                return ((int)obj == 0) ? EnableState.Disabled : EnableState.Enabled;
            }

            return EnableState.None;
        }

        // Calls the supplied delegate on each of the children of keyBase.
        private static EnableState IterateSubKeys(RegistryKey keyBase, string subKey, IterateHandler handler, bool localMachine)
        {
            RegistryKey key;
            string[] subKeyNames;
            EnableState state;

            key = keyBase.OpenSubKey(subKey, false);

            if (key == null)
                return EnableState.Error;

            subKeyNames = key.GetSubKeyNames();
            state = EnableState.Error;

            foreach (string name in subKeyNames)
            {
                switch (handler(key, name, localMachine))
                {
                    case EnableState.Error:
                        break;
                    case EnableState.None:
                        if (localMachine) // For lm, want to return here right away.
                            return EnableState.None;

                        // For current user, remember that we found no Enable value.
                        if (state == EnableState.Error)
                        {
                            state = EnableState.None;
                        }
                        break;
                    case EnableState.Disabled:
                        state = EnableState.Disabled;
                        break;
                    case EnableState.Enabled:
                        return EnableState.Enabled;
                }
            }

            return state;
        }

        #endregion Private Methods

        //------------------------------------------------------
        //
        //  Private Properties
        //
        //------------------------------------------------------

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields

        // String consts used to validate registry entires.
        private const int CLSIDLength = 38;  // {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
        private const int LANGIDLength = 10; // 0x12345678

        // Status of a TIP assembly.
        private enum EnableState
        { 
            Error,      // Invalid entry.
            None,       // No explicit Enable entry on the assembly.
            Enabled,    // Assembly is enabled.
            Disabled    // Assembly is disabled.
        };

        // Callback delegate for the IterateSubKeys method.
        private delegate EnableState IterateHandler(RegistryKey key, string subKeyName, bool localMachine);

        // Install state.
        private enum InstallState
        { 
            Unknown,        // Haven't checked to see if any TIPs are installed yet.
            Installed,      // Checked and installed.
            NotInstalled    // Checked and not installed.
        }

        // Cached install state value.
        // Writes are not thread safe, but we don't mind the neglible perf hit
        // of potentially writing it twice.
        private static InstallState s_servicesInstalled = InstallState.Unknown;
        private static object s_servicesInstalledLock = new object();

        #endregion Private Fields
    }
}
