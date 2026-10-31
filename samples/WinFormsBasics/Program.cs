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

            // 【ACC-028 / K-165】把被 catch 掉的异常打到 stderr。
            //   背景：K-164 从截图里发现 WinFormsBasics 弹了 .NET 未处理异常对话框，
            //   而那个对话框在「画面空白」时期完全看不见（K-160 的教训）。
            //   这里只加诊断，不改任何业务行为。
            Application.ThreadException += (s, e) =>
            {
                Console.Error.WriteLine("[EX] ThreadException: " + e.Exception);
                Console.Error.Flush();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.Error.WriteLine("[EX] Unhandled: " + e.ExceptionObject);
                Console.Error.Flush();
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

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
