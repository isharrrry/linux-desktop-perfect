using System;
using System.Windows.Forms;

namespace LinuxDesktopPerfect.Samples.WinFormsBasics
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string scr = ScrArg(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(scr));
        }

        // --scr SCR-0x：只影响窗体标题（骨架不实现业务逻辑）
        private static string ScrArg(string[] args)
        {
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
