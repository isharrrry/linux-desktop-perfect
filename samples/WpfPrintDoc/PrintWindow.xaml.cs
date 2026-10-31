using System;
using System.Printing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Xps;

namespace LinuxDesktopPerfect.Samples.WpfPrintDoc
{
    public partial class PrintWindow : Window
    {
        public PrintWindow()
        {
            InitializeComponent();
            string scr = ScrArg();
            Title = "WpfPrintDoc" + (scr == null ? "" : " [" + scr + "]");
            TitleText.Text = Title;
            PrintButton.Click += OnPrint;
        }

        private void OnPrint(object sender, RoutedEventArgs e)
        {
            try
            {
                // 【ACC-024 / K-055】PrintServer/PrintQueue 的两个入口都在托管层 NRE（winspool 根本没被调到），
                // 所以这里走**不需要打印机**的 XPS 路径：XpsDocument + XpsDocumentWriter 直接写文件。
                // 这一半验证的是 XPS 序列化（ReachFramework 594 类型）+ 打包（System.IO.Packaging），
                // 「打印到 CUPS」那半仍未实现，会单独记。
                string xpsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ldp-print-" + DateTime.Now.Ticks + ".xps");
                // 【ACC-024 / K-057】编译期被 NuGet 的 ReachFramework **ref** 程序集挡着（那份只有 5120 字节、
                // XpsDocument 只暴露 Dispose()），所以这里**只用反射**调运行时那 619008 字节的版本。
                // FixedDocument/FixedPage/PageContent 在 PresentationFramework 里，ref 可用，不必反射。
                var rfAsm = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "ReachFramework");
                if (rfAsm == null) { rfAsm = System.Reflection.Assembly.Load("ReachFramework"); }
                var tXps = rfAsm.GetType("System.Windows.Xps.Packaging.XpsDocument", true);
                var tCO  = Type.GetType("System.IO.Packaging.CompressionOption, System.IO.Packaging", true);
                var ctor = tXps.GetConstructor(new Type[] { typeof(string), typeof(System.IO.FileAccess), tCO });
                Console.Error.WriteLine("[PRINT] XpsDocument ctor=" + (ctor != null ? "found" : "NULL") + "  asm=" + rfAsm.GetName().Version);
                var xps = ctor.Invoke(new object[] { xpsPath, System.IO.FileAccess.ReadWrite, Enum.Parse(tCO, "Fast") });
                var mCreate = tXps.GetMethod("CreateXpsDocumentWriter", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new Type[] { tXps }, null);
                Console.Error.WriteLine("[PRINT] CreateXpsDocumentWriter=" + (mCreate != null ? "found" : "NULL"));
                var writer = mCreate.Invoke(null, new object[] { xps });
                var doc = new FixedDocument();
                var page = new FixedPage();
                page.Children.Add(new TextBlock { Text = "打印测试" });
                var pc = new PageContent();
                ((IAddChild)pc).AddChild(page);
                doc.Pages.Add(pc);
                var mWrite = writer.GetType().GetMethod("Write", new Type[] { typeof(FixedDocument) });
                mWrite.Invoke(writer, new object[] { doc });
                var mClose = xps.GetType().GetMethod("Close", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var mDisp  = xps.GetType().GetMethod("Dispose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
                Console.Error.WriteLine("[PRINT] Close=" + (mClose != null ? "found" : "NULL") + " Dispose=" + (mDisp != null ? "found" : "NULL"));
                if (mClose != null) mClose.Invoke(xps, null);
                else if (mDisp != null) mDisp.Invoke(xps, null);
                Console.Error.WriteLine("[PRINT] XPS 已写出：" + xpsPath);
                SetReadouts("job=1 state=done", "pages=1");
            }
            catch (Exception ex)
            {
                SetReadouts("job=0 state=error", "pages=0");
                Console.Error.WriteLine("[PRINT] 失败：" + ex.ToString());
            }
        }

        private void SetReadouts(string job, string pages)
        {
            JobText.Text = job;
            AutomationProperties.SetName(JobText, job);
            PagesText.Text = pages;
            AutomationProperties.SetName(PagesText, pages);
        }

        private static string ScrArg()
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "--scr") return a[i + 1];
            return null;
        }
    }
}
