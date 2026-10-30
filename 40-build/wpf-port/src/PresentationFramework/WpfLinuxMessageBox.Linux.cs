// 手写件（非生成物）：任务 E —— `MessageBox.Show` 的**自产模态实现**。
//
// 上游 `MessageBox.ShowCore` 走 `user32!MessageBox`（P/Invoke）。本移植的 shim 没有 X11 模态对话框，
// 只往 stderr 写一行并按"用户按了确定"返回（刻意降级，防 headless 死锁）⇒ `MessageBox.Show` 从不弹窗、
// 也从不阻塞。本文件用自产 `Window` + `ShowDialog()` 补上这一格：
//   · 真窗口、真阻塞（`ShowDialog` 推一层 `DispatcherFrame`，用户响应前不返回）；
//   · `MessageBoxButton` 全枚举 + `MessageBoxImage`（图标是**自绘文本圆点**，不求与 Windows 像素一致）；
//   · 支持 `defaultResult`（默认按钮）与 Enter/Esc；
//   · 返回值与 Win32 一致（`IDOK=1 … IDCONTINUE=11`），调用方（上游 `Win32ToMessageBoxResult`）一个字不改；
//   · **无 `DISPLAY` 或构造失败 ⇒ 照旧降级不阻塞**（写 stderr、返回默认按钮码）——headless 硬要求。
//
// 由 `src/WpfGfx.Linux.Native/tools/patch-presentationframework-messagebox.py` 接线进
// `PresentationFramework.Linux.csproj`。

using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace System.Windows
{
    /// <summary>任务 E：`MessageBox` 的自产模态实现（`Show` 参数/返回值与 Win32 `MessageBox` 的 `style`/`ID*` 对齐）。</summary>
    internal static class WpfLinuxMessageBox
    {
        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDABORT = 3;
        private const int IDRETRY = 4;
        private const int IDIGNORE = 5;
        private const int IDYES = 6;
        private const int IDNO = 7;
        private const int IDTRYAGAIN = 10;
        private const int IDCONTINUE = 11;

        private struct ButtonSpec
        {
            internal readonly string Text;
            internal readonly int Code;

            internal ButtonSpec(string text, int code)
            {
                Text = text;
                Code = code;
            }
        }

        /// <summary>
        /// 弹一个模态消息框并阻塞到用户响应。参数与 Win32 `MessageBox(hwnd, text, caption, type)` 同形：
        /// <paramref name="style"/> = 按钮(低 4 位) | 图标(0xF0) | 默认按钮(0x100/0x200) | 选项(高位)。
        /// 返回 Win32 `ID*` 码。
        /// </summary>
        internal static int Show(IntPtr owner, string messageBoxText, string caption, int style)
        {
            MessageBoxButton button = (MessageBoxButton)(style & 0xF);
            MessageBoxImage icon = (MessageBoxImage)(style & 0xF0);
            int defaultButtonNumber = (style >> 8) & 0xF;

            if (!IsInteractive())
            {
                return Degrade(messageBoxText, caption, button, defaultButtonNumber);
            }

            try
            {
                return ShowModal(owner, messageBoxText, caption, button, icon, defaultButtonNumber);
            }
            catch (Exception e)
            {
                // 构造/显示失败（无 X 连接、线程模型不对…）⇒ 与 shim 旧口径一致：降级不阻塞。
                Console.Error.WriteLine("[wpfwin32] MessageBox(降级，不阻塞，模态窗构造失败): [{0}] {1} -- {2}",
                                        caption ?? "", messageBoxText ?? "", e.Message);
                return DefaultButtonCode(button, defaultButtonNumber);
            }
        }

        /// <summary>桌面上有 X 才去连：没有 `DISPLAY`/`WAYLAND_DISPLAY` ⇒ 非交互（headless）。</summary>
        private static bool IsInteractive()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return true;
            return false;
        }

        /// <summary>非交互/失败路径：写 stderr（与 shim 旧行只多一层说明），返回默认按钮码，**不阻塞**。</summary>
        private static int Degrade(string messageBoxText, string caption, MessageBoxButton button, int defaultButtonNumber)
        {
            Console.Error.WriteLine("[wpfwin32] MessageBox(降级，不阻塞): [{0}] {1}", caption ?? "", messageBoxText ?? "");
            return DefaultButtonCode(button, defaultButtonNumber);
        }

        private static int ShowModal(IntPtr owner, string messageBoxText, string caption,
                                     MessageBoxButton button, MessageBoxImage icon, int defaultButtonNumber)
        {
            ButtonSpec[] specs = ButtonsOf(button);
            int defaultIndex = (defaultButtonNumber >= 0 && defaultButtonNumber < specs.Length) ? defaultButtonNumber : 0;

            Window w = new Window();
            w.Title = caption ?? string.Empty;
            w.SizeToContent = SizeToContent.WidthAndHeight;
            // 正文很短时窗口会窄到把标题栏文字省略掉（Windows 也一样）；给一个下限让标题始终可见。
            w.MinWidth = 260;
            w.ResizeMode = ResizeMode.NoResize;
            w.ShowInTaskbar = false;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (owner != IntPtr.Zero)
            {
                // 归属（WM_TRANSIENT_FOR + 弹窗期间禁用属主）；失败不影响"能弹能阻塞"这一格。
                try
                {
                    new WindowInteropHelper(w).Owner = owner;
                }
                catch (Exception)
                {
                }
            }

            int result = 0;
            bool decided = false;
            Action<int> closeWith = delegate(int code)
            {
                if (decided) return;
                decided = true;
                result = code;
                w.Close();
            };

            StackPanel outer = new StackPanel();
            outer.Margin = new Thickness(16);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;

            UIElement iconVisual = IconVisual(icon);
            if (iconVisual != null) row.Children.Add(iconVisual);

            TextBlock body = new TextBlock();
            body.Text = messageBoxText ?? string.Empty;
            body.TextWrapping = TextWrapping.Wrap;
            body.MaxWidth = 420;
            row.Children.Add(body);
            outer.Children.Add(row);

            StackPanel buttonRow = new StackPanel();
            buttonRow.Orientation = Orientation.Horizontal;
            buttonRow.HorizontalAlignment = HorizontalAlignment.Right;
            buttonRow.Margin = new Thickness(0, 20, 0, 0);

            Button cancelButton = null;
            Button defaultButton = null;
            for (int i = 0; i < specs.Length; i++)
            {
                ButtonSpec spec = specs[i];
                Button b = new Button();
                b.Content = spec.Text;
                b.MinWidth = 88;
                b.Margin = new Thickness(8, 0, 0, 0);
                if (i == defaultIndex)
                {
                    b.IsDefault = true;
                    defaultButton = b;
                }
                if (spec.Code == IDCANCEL) cancelButton = b;
                int code = spec.Code;
                b.Click += delegate(object s, RoutedEventArgs e) { closeWith(code); };
                buttonRow.Children.Add(b);
            }
            outer.Children.Add(buttonRow);
            w.Content = outer;

            // 键盘要先有焦点元素才进路由（自产栈实测：无焦点元素时 `PreviewKeyDown` 一次都不来）
            // ⇒ 窗口一 Loaded 就把焦点给默认按钮。
            if (defaultButton != null)
            {
                w.Loaded += delegate(object s, RoutedEventArgs e) { defaultButton.Focus(); };
            }

            // Esc：有取消按钮 ⇒ 取消；没有 ⇒ 与 Windows 一致地按"默认按钮"收场。
            // Enter：默认按钮（`defaultResult` 决定的那个）。
            int escCode = (cancelButton != null) ? IDCANCEL : specs[defaultIndex].Code;
            int enterCode = specs[defaultIndex].Code;
            w.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    closeWith(enterCode);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    closeWith(escCode);
                    e.Handled = true;
                }
            };

            w.ShowDialog();

            // 用户用窗管的关闭按钮结束（`DialogResult` 保持 null）⇒ 按取消（无取消按钮则按默认按钮）。
            if (!decided) result = escCode;
            return result;
        }

        /// <summary>图标：自绘"彩底 + 白色 ASCII 记号"的圆点（**不求与 Windows 像素一致**）。</summary>
        private static UIElement IconVisual(MessageBoxImage icon)
        {
            string glyph;
            Brush fill;
            // ⚠️ `MessageBoxImage` 的 `Hand/Stop/Error`、`Exclamation/Warning`、`Asterisk/Information`
            //    是**同一取值**的别名 ⇒ switch 里每种取值只能写一个标签。
            switch (icon)
            {
                case MessageBoxImage.Hand:
                    glyph = "X"; fill = Brushes.Firebrick; break;
                case MessageBoxImage.Question:
                    glyph = "?"; fill = Brushes.SteelBlue; break;
                case MessageBoxImage.Exclamation:
                    glyph = "!"; fill = Brushes.Goldenrod; break;
                case MessageBoxImage.Asterisk:
                    glyph = "i"; fill = Brushes.RoyalBlue; break;
                default:
                    return null;
            }

            Grid g = new Grid();
            g.Width = 24;
            g.Height = 24;
            g.Margin = new Thickness(0, 0, 12, 0);
            g.VerticalAlignment = VerticalAlignment.Top;

            Ellipse circle = new Ellipse();
            circle.Fill = fill;
            g.Children.Add(circle);

            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.Foreground = Brushes.White;
            t.FontWeight = FontWeights.Bold;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);

            return g;
        }

        /// <summary>`MessageBoxButton` → 按钮表（文案 + Win32 码），顺序与 Windows 一致。</summary>
        private static ButtonSpec[] ButtonsOf(MessageBoxButton button)
        {
            switch (button)
            {
                case MessageBoxButton.OKCancel:
                    return new ButtonSpec[] { new ButtonSpec("确定", IDOK), new ButtonSpec("取消", IDCANCEL) };
                case MessageBoxButton.AbortRetryIgnore:
                    return new ButtonSpec[] { new ButtonSpec("中止", IDABORT), new ButtonSpec("重试", IDRETRY), new ButtonSpec("忽略", IDIGNORE) };
                case MessageBoxButton.YesNoCancel:
                    return new ButtonSpec[] { new ButtonSpec("是", IDYES), new ButtonSpec("否", IDNO), new ButtonSpec("取消", IDCANCEL) };
                case MessageBoxButton.YesNo:
                    return new ButtonSpec[] { new ButtonSpec("是", IDYES), new ButtonSpec("否", IDNO) };
                case MessageBoxButton.RetryCancel:
                    return new ButtonSpec[] { new ButtonSpec("重试", IDRETRY), new ButtonSpec("取消", IDCANCEL) };
                case MessageBoxButton.CancelTryContinue:
                    return new ButtonSpec[] { new ButtonSpec("取消", IDCANCEL), new ButtonSpec("再试一次", IDTRYAGAIN), new ButtonSpec("继续", IDCONTINUE) };
                default:   // MessageBoxButton.OK
                    return new ButtonSpec[] { new ButtonSpec("确定", IDOK) };
            }
        }

        private static int DefaultButtonCode(MessageBoxButton button, int defaultButtonNumber)
        {
            ButtonSpec[] specs = ButtonsOf(button);
            int idx = (defaultButtonNumber >= 0 && defaultButtonNumber < specs.Length) ? defaultButtonNumber : 0;
            return specs[idx].Code;
        }
    }
}
