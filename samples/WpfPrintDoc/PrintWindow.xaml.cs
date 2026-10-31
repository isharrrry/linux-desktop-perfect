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
                using (var xps = new System.Windows.Xps.Packaging.XpsDocument(xpsPath, System.IO.FileAccess.ReadWrite, System.IO.Packaging.CompressionOption.Fast))
                {
                    var writer = System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(xps);
                    var doc = new FixedDocument();
                    var page = new FixedPage();
                    page.Children.Add(new TextBlock { Text = "打印测试" });
                    var pc = new PageContent();
                    ((IAddChild)pc).AddChild(page);
                    doc.Pages.Add(pc);
                    writer.Write(doc);
                }
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
