using System;
using System.Windows;

namespace LinuxDesktopPerfect.Samples.WpfTextDemo
{
    public partial class TextWindow : Window
    {
        public TextWindow()
        {
            InitializeComponent();
            string scr = ScrArg();
            Title = "WpfTextDemo" + (scr == null ? "" : " [" + scr + "]");
            TitleText.Text = Title;
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
