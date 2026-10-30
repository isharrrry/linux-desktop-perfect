// WPF-on-Linux：上游 System.Windows.SplashScreen
//   （src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/SplashScreen.cs，435 行）
// 未随自产件移植 —— 它的实现是纯 Win32 分层窗口（CsWin32 绑定 + user32/GDI + BLENDFUNCTION
// + UpdateLayeredWindow），在本移植栈上既缺绑定也没有等价实现。
//
// 本文件给出**等价语义**的 Linux 实现（编译进自产 PresentationFramework：WindowsBase 层级更低，
// 拿不到 Window/Image；上游该类型本身也在 WindowsBase，但这里只能上移到 PF）：
//   · 显示**入口程序集**内由 resourceName 指定的资源图（pack URI，与官方语义一致）
//   · Show(autoClose: true) = 首个"非闪屏"窗口可见后自动关闭（官方是"主窗口渲染后关闭"）
//   · 任何失败都不抛出：闪屏是锦上添花，绝不能阻断应用启动
//   · 关闭前把 Application.MainWindow 交还给真正的主窗口（闪屏先 Show 会占据 MainWindow，
//     否则按 OnMainWindowClose 语义会连带结束应用）
//
// 由 src/WpfGfx.Linux.Native/tools/patch-presentationframework-splashscreen.py 接线进 csproj。
using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace System.Windows
{
    /// <summary>
    /// 窗口启动画面（对应上游 WindowsBase 的 System.Windows.SplashScreen；此处为 WPF-on-Linux 实现）。
    /// </summary>
    public class SplashScreen
    {
        private readonly string _resourceName;
        private Window _window;

        public SplashScreen(string resourceName)
        {
            _resourceName = resourceName;
        }

        public SplashScreen(string resourceName, bool autoClose)
        {
            _resourceName = resourceName;
        }

        public void Show()
        {
            Show(false);
        }

        public void Show(bool autoClose)
        {
            try
            {
                var entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var uri = new Uri($"pack://application:,,,/{entry.GetName().Name};component/{_resourceName}", UriKind.Absolute);
                _window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new Image
                    {
                        Source = new BitmapImage(uri),
                        Stretch = Stretch.None
                    }
                };
                _window.Show();

                if (autoClose)
                {
                    HookAutoClose();
                }
            }
            catch
            {
                // 闪屏失败不得阻止启动
                _window = null;
            }
        }

        public void Close()
        {
            try
            {
                _window?.Close();
            }
            catch
            {
            }

            _window = null;
        }

        private void HookAutoClose()
        {
            var app = Application.Current;
            if (app == null)
            {
                Close();
                return;
            }

            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(150),
                // ⚠ 本栈实测：Background/ApplicationIdle/Inactive 这些**低优先级 op 从不执行**
                //   （同一探针用 Background 一次都不触发、改 Normal 立刻触发）⇒ 闪屏 autoClose 永不触发。
                //   这里提到 Normal；栈侧低优先级调度缺陷已登记。
                DispatcherPriority.Normal,
                (sender, args) =>
                {
                    Window target = null;
                    foreach (Window window in app.Windows)
                    {
                        if (!ReferenceEquals(window, _window) && window.IsVisible)
                        {
                            target = window;
                            break;
                        }
                    }

                    if (target == null)
                    {
                        return;
                    }

                    ((DispatcherTimer)sender).Stop();

                    // 闪屏先 Show 会占据 Application.MainWindow ⇒ 关它之前先交还主窗口
                    if (ReferenceEquals(app.MainWindow, _window))
                    {
                        app.MainWindow = target;
                    }

                    Close();
                },
                app.Dispatcher);
            timer.Start();
        }
    }
}
