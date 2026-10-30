// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-inputsite-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/InputProviderSite.cs` 逐字复制 + 1 处 T1c **只读插桩**（第 2 批 P1）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：**打字到不了 TextBox**。第 1 批已证明字符到了 OnPreprocessMessage 且没被
//   `_eatCharMessages` 吞掉，最后停在 `ProcessTextInputAction ⇒ handled=True` 而文本未变。
//   P1 钉在 `ProcessTextInputAction` 的下一句（`_site.ReportInput(report)`）——
//   **"报告有没有交出去"的分水岭**：P1 有 ⇒ 顺着 P2→P3→P5 往下；P1 无 ⇒ provider 侧提前返回。
//   开关与有界：见插桩类本体（`WPF_LINUX_INPUT_TRACE`；第 2 批预算**独立**于第 1 批，Text 类必打）。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Windows.Input
{
    /// <summary>
    ///     The object which input providers use to report input to the input
    ///     manager.
    /// </summary>
    internal class InputProviderSite : IDisposable
    {
        internal InputProviderSite(InputManager inputManager, IInputProvider inputProvider)
        {
            _inputManager = inputManager;
            _inputProvider = inputProvider;
        }

        /// <summary>
        ///     Returns the input manager that this site is attached to.
        /// </summary>
        public InputManager InputManager
        {
            get
            {
                return CriticalInputManager;
            }
        }

        /// <summary>
        ///     Returns the input manager that this site is attached to.
        /// </summary>
        internal InputManager CriticalInputManager => _inputManager;

        /// <summary>
        ///     Unregisters this input provider.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (!_isDisposed)
            {
                _isDisposed = true;

                if (_inputManager is not null && _inputProvider is not null)
                {
                    _inputManager.UnregisterInputProvider(_inputProvider);
                }

                _inputManager = null;
                _inputProvider = null;
            }
        }

        /// <summary>
        /// Returns true if the CompositionTarget is disposed.
        /// </summary>
        public bool IsDisposed
        {
            get
            {
                return _isDisposed;
            }
        }

        /// <summary>
        ///     Reports input to the input manager.
        /// </summary>
        /// <returns>
        ///     Whether or not any event generated as a consequence of this
        ///     event was handled.
        /// </returns>
        /// <remarks>
        ///  Do we really need this?  Make the "providers" call InputManager.ProcessInput themselves.
        ///  we currently need to map back to providers for other reasons.
        /// </remarks>
        public bool ReportInput(InputReport inputReport)
        {
            if(IsDisposed)
            {
                throw new ObjectDisposedException(SR.InputProviderSiteDisposed);
            }

            bool handled = false;

            InputReportEventArgs input = new InputReportEventArgs(null, inputReport)
            {
                RoutedEvent = InputManager.PreviewInputReportEvent
            };

            if (_inputManager is not null)
            {
                handled = _inputManager.ProcessInput(input);
            }

            // ── T1c 第 2 批 P1（只读插桩）：报告类型 + 返回的 handled ──
            //    跨命名空间 ⇒ **全限定**（WpfLinuxInputTrace 在 System.Windows.Interop）
            System.Windows.Interop.WpfLinuxInputTrace.B2ReportInput(inputReport, handled, _inputManager);

            return handled;
        }

        private bool _isDisposed;
        private InputManager _inputManager;
        private IInputProvider _inputProvider;
    }
}

