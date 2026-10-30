// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-shared-invariant-failfast.py **生成**，不要手改。
//
// 内容 = 上游 `Shared/MS/Internal/Invariant.cs` 逐字复制 + **补丁 N** 的两处：
//   ① `Registry.LocalMachine?.OpenSubKey(…)` —— Unix 上根键为 null，修前会让 **FailFast 自己 NRE**；
//   ② 失败路径**主动打印断言原文**（`Debug.Fail` 在 Release 下被编译掉，原文否则会丢），
//      并把原文拼进 FailFast 文本。
// 语义：**仍然 FailFast**（进程照旧终止），**不吞消息、不把 assert 变成 no-op**。
//
// ↓↓↓ 以下为上游原文（仅补丁 N 的两处有改动）↓↓↓
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace MS.Internal
{
    /// <summary>
    ///  Provides methods that assert an application is in a valid state.
    /// </summary>
    internal static class Invariant
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        /// <summary>
        /// Static ctor.  Initializes the Strict property.
        /// </summary>
        static Invariant()
        {
            _strict = _strictDefaultValue;

#if PRERELEASE
            //
            // Let the user override the inital value of the Strict property from the registry.
            //
            RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryKeys.WPF);

            if (key != null)
            {
                object obj = key.GetValue("InvariantStrict");

                if (obj is int)
                {
                    _strict = (int)obj != 0;
                }
            }
#endif // PRERELEASE
        }

        #endregion Constructors

        //------------------------------------------------------
        //
        //  Internal Methods
        //
        //------------------------------------------------------

        #region Internal Methods

        /// <summary>
        /// Checks for a condition and shuts down the application if false.
        /// </summary>
        /// <param name="condition">
        /// If condition is true, does nothing.
        ///
        /// If condition is false, raises an assert dialog then shuts down the
        /// process unconditionally.
        /// </param>
        internal static void Assert(bool condition)
        {
            if (!condition)
            {
                FailFast(message: null, detailMessage: null);
            }
        }

        /// <summary>
        ///  Checks <paramref name="value"/> for <see langword="null"/> and shuts down the application if true.
        /// </summary>
        internal static void AssertNotNull([NotNull] object value)
        {
            if (value is null)
            {
                FailFast("Value should not be null", null);
            }
        }

        /// <summary>
        /// Checks for a condition and shuts down the application if false.
        /// </summary>
        /// <param name="condition">
        /// If condition is true, does nothing.
        ///
        /// If condition is false, raises an assert dialog then shuts down the
        /// process unconditionally.
        /// </param>
        /// <param name="invariantMessage">
        /// Message to display before shutting down the application.
        /// </param>
        internal static void Assert(bool condition, string invariantMessage)
        {
            if (!condition)
            {
                FailFast(invariantMessage, null);
            }
        }

        /// <summary>
        /// Checks for a condition and shuts down the application if false.
        /// </summary>
        /// <param name="condition">
        /// If condition is true, does nothing.
        ///
        /// If condition is false, raises an assert dialog then shuts down the
        /// process unconditionally.
        /// </param>
        /// <param name="invariantMessage">
        /// Message to display before shutting down the application.
        /// </param>
        /// <param name="detailMessage">
        /// Additional message to display before shutting down the application.
        /// </param>
        internal static void Assert(bool condition, string invariantMessage, string detailMessage)
        {
            if (!condition)
            {
                FailFast(invariantMessage, detailMessage);
            }
        }

        #endregion Internal Methods

        //------------------------------------------------------
        //
        //  Internal Properties
        //
        //------------------------------------------------------

        #region Internal Properties

        /// <summary>
        /// Property specifying whether or not the user wants to enable expensive
        /// verification diagnostics.  The Strict property is rarely used -- only
        /// when performance profiling shows a real problem.
        ///
        /// Default value is false on FRE builds, true on CHK builds.
        ///
        /// On any build flavor the user may override this by setting
        /// [HKLM\Software\Microsoft\Avalon] InvariantStrict in the registry.
        /// (0 to disable strict asserts, 1 to enable them.)
        ///
        /// Example:
        ///
        ///  // Cheap assert always runs...
        ///  Invariant.Assert(_array.Length > 0, "_array should never be zero length!");
        ///  // Expensive assert only runs when full diagnostics are enabled.
        ///  if (Invariant.Strict)
        ///  {
        ///      for (int i=0; i != _array.Length; i++)
        ///      {
        ///          Invariant.Assert(_array[i] != 0, "_array contains zero value!");
        ///      }
        ///  }
        /// </summary>
        internal static bool Strict
        {
            get { return _strict; }

            set { _strict = value; }
        }

        #endregion Internal Properties

        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        #region Private Methods

        /// <summary>
        ///     Shuts down the process immediately, with no chance for additional
        ///     code to run.
        /// 
        ///     In debug we raise a Debug.Assert dialog before shutting down.
        /// </summary>
        /// <param name="message">
        ///     Message to display before shutting down the application.
        /// </param>
        /// <param name="detailMessage">
        ///     Additional message to display before shutting down the application.
        /// </param>
        [DoesNotReturn]
        private static void FailFast(string message, string detailMessage)
        {
            // [补丁 N] **先把原文写出去**：`Debug.Fail` 带 `[Conditional("DEBUG")]`，Release 下整句被编译掉，
            //   而 `Environment.FailFast(SR.InvariantFailure)` 只有一个通用串 ⇒ 不加这一步，**原文照样丢**。
            //   语义不变：仍然 FailFast（进程照旧终止），只是把"哪个不变量、什么条件"带出来。
            PrintInvariantFailure(message, detailMessage);

            if (IsDialogOverrideEnabled)
            {
                // This is the override for stress and other automation.
                // Automated systems can't handle a popup-dialog, so let
                // them jump straight into the debugger.
                Debugger.Break();
            }

            Debug.Fail($"Invariant failure: {message}", detailMessage);
            Environment.FailFast(BuildInvariantFailureText(message, detailMessage));
        }

        // [补丁 N] 新增：把断言原文（不变量条件 + 明细）打到 stderr；Linux 上这是**唯一**能看到原文的通道。
        private static void PrintInvariantFailure(string message, string detailMessage)
        {
            try
            {
                System.Console.Error.WriteLine(BuildInvariantFailureText(message, detailMessage));
                System.Console.Error.Flush();
            }
            catch
            {
                // 输出失败不改变语义（仍然 FailFast）。
            }
        }

        // [补丁 N] 新增：拼出带原文的终止文本（`SR.InvariantFailure` 是通用串，单靠它定位不到现场）。
        private static string BuildInvariantFailureText(string message, string detailMessage)
        {
            string text = SR.InvariantFailure;
            if (!string.IsNullOrEmpty(message))
            {
                text += ": " + message;
            }
            if (!string.IsNullOrEmpty(detailMessage))
            {
                text += " — " + detailMessage;
            }
            return text;
        }

        #endregion Private Methods

        //------------------------------------------------------
        //
        //  Private Properties
        //
        //------------------------------------------------------

        #region Private Properties

        // Returns true if the default assert failure dialog has been disabled
        // on this machine.
        //
        // The dialog may be disabled by
        //   Installing a JIT debugger to the [HKEY_LOCAL_MACHINE\Software\Microsoft\.NETFramework]
        //     DbgJITDebugLaunchSetting and DbgManagedDebugger registry keys.
        private static bool IsDialogOverrideEnabled
        {
            get
            {
                RegistryKey key;
                bool enabled;

                enabled = false;

                //extracting all the data under an elevation.
                // [补丁 N] Unix 上 Registry.LocalMachine 是 **null**（不是抛异常）——
                //   这一处落在 **assert 的失败路径**上 ⇒ 修前是 "准备报错时自己 NRE"，
                //   把真正的断言原文吃掉了。
                key = Registry.LocalMachine?.OpenSubKey("Software\\Microsoft\\.NETFramework");
                //
                // Check for the enable.
                //
                if (key != null)
                {
                    object dbgJITDebugLaunchSettingValue = key.GetValue("DbgJITDebugLaunchSetting");

                    //
                    // Only count the enable if there's a JIT debugger to launch.
                    //
                    enabled = (dbgJITDebugLaunchSettingValue is int && ((int)dbgJITDebugLaunchSettingValue & 2) != 0);
                    if (enabled)
                    {
                        enabled = key.GetValue("DbgManagedDebugger") is string dbgManagedDebuggerValue && dbgManagedDebuggerValue.Length > 0;
                    }
                }
                return enabled;
            }
        }

        #endregion Private Properties

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields

        // Property specifying whether or not the user wants to enable expensive
        // verification diagnostics.
        private static bool _strict;

        // Used to initialize the default value of _strict in the static ctor.
        private const bool _strictDefaultValue
#if DEBUG
            = true;     // Enable strict asserts by default on CHK builds.
#else
            = false;
#endif

        #endregion Private Fields
    }
}

