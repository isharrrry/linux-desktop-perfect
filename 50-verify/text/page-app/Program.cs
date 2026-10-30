// FlowDocument 分页验证（ACC-015）：构造一段长文本 → 用 DocumentPaginator 分页 → 读 PageCount。
// 输出 JSON 到 /tmp/ldp-page-result.json。判据读它判断"分页是否真的发生"。
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace LdpVerify.Page
{
    public sealed class PageCheckApp : Application
    {
        private readonly string _out;

        public PageCheckApp(string @out) { _out = @out; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string json;
            try
            {
                // 一段足够长的文本 + 固定页尺寸，确保会分成多页
                var doc = new FlowDocument();
                doc.PageWidth = 400;
                doc.PageHeight = 300;
                doc.PagePadding = new Thickness(20);
                doc.ColumnWidth = 400;
                var sb = new StringBuilder();
                for (int i = 0; i < 120; ++i) sb.Append("第 ").Append(i + 1).Append(" 行：LinuxDesktopPerfect 的 FlowDocument 分页验证文本。\n");
                doc.Blocks.Add(new Paragraph(new Run(sb.ToString())));

                DocumentPaginator pag = ((IDocumentPaginatorSource)doc).DocumentPaginator;
                pag.PageSize = new Size(400, 300);
                pag.ComputePageCount();
                int pages = pag.PageCount;

                // 再取第一页的可视内容（验证分页不是"只报数字"）
                DocumentPage first = pag.GetPage(0);
                json = string.Format("{{\"ok\":true,\"page_count\":{0},\"page_size\":[{1},{2}],\"first_page_blank\":{3}}}",
                    pages, (int)pag.PageSize.Width, (int)pag.PageSize.Height,
                    first == DocumentPage.Missing ? "true" : "false");
            }
            catch (Exception ex)
            {
                json = string.Format("{{\"ok\":false,\"type\":\"{0}\",\"error\":\"{1}\"}}",
                    ex.GetType().Name, ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\""));
            }
            File.WriteAllText(_out, json + "\n");
            Shutdown(0);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string output = args.Length > 0 ? args[0] : "/tmp/ldp-page-result.json";
            var app = new PageCheckApp(output);
            app.Run();
        }
    }
}
