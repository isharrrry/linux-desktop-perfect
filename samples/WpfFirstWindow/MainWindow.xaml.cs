using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace LinuxDesktopPerfect.Samples.WpfFirstWindow
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            string scr = ScrArg();
            Title = "WpfFirstWindow" + (scr == null ? "" : " [" + scr + "]");
            TitleText.Text = Title;
            Loaded += (s, e) =>
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                int d = (int)Math.Round(dpi.PixelsPerInchX);
                int pct = (int)Math.Round(dpi.PixelsPerInchX / 96.0 * 100.0);
                string text = "DPI " + d.ToString("000") + " (" + pct.ToString() + "%)";
                DpiText.Text = text;
                AutomationProperties.SetName(DpiText, text);
            };
        }

        // DragOver 必须把 Effects 设成非 None，否则 WPF 不会触发 Drop
        private void OnFileDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            string text = "drop=none";
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) text = "drop=" + files[0];
            }
            DropText.Text = text;
            AutomationProperties.SetName(DropText, text);
            e.Handled = true;
        }

        private static string ScrArg()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
                if (args[i] == "--scr") return args[i + 1];
            return null;
        }
    }
}
