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

            // 【ACC-028 / K-165】把被 catch 掉的异常打到 stderr（纯诊断，不改业务行为）。
            //   为什么需要：WinForms 的未处理异常对话框在「画面空白」时期完全看不见（K-160 的教训），
            //   而 FirstChanceException 外部钩子在本仓环境下会 abort（K-164）。
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
