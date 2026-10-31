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
                var server = new LocalPrintServer();
                PrintQueue queue = server.DefaultPrintQueue;
                XpsDocumentWriter writer = PrintQueue.CreateXpsDocumentWriter(queue);
                var doc = new FixedDocument();
                var page = new FixedPage();
                page.Children.Add(new TextBlock { Text = "打印测试" });
                var pc = new PageContent();
                ((IAddChild)pc).AddChild(page);
                doc.Pages.Add(pc);
                writer.Write(doc);
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
