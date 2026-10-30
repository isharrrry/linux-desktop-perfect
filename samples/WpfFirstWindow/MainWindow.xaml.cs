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
            // DPI 读数：结构判据的数据源。文案格式由元素属性表定死（DPI 1xx (yyy%)），别改。
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

        // --scr SCR-0x：只影响窗口标题，不做其他事（骨架不实现业务逻辑）
        private static string ScrArg()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--scr")
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
