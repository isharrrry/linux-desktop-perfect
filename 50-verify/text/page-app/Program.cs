// FlowDocument 分页验证（ACC-015）：构造一段长文本 → 用 DocumentPaginator 分页 → 读 PageCount。
// 输出 JSON 到 /tmp/ldp-page-result.json。判据读它判断"分页是否真的发生"。
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LdpVerify.Page
{
    public sealed class PageCheckApp : Application
    {
        private readonly string _out;

        public PageCheckApp(string @out) { _out = @out; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 看门狗：分页测量在字体回退缺口下可能**卡住**（D-019，实测 40 s 不返回）。
            //   卡住比失败更难查 —— 所以这里主动在 20 s 后写一份"超时"读数并退出，
            //   让判据拿到的是**读数**而不是"进程被 kill、什么都没有"。
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(20000);
                try
                {
                    File.WriteAllText(_out,
                        "{\"ok\":false,\"type\":\"WatchdogTimeout\",\"error\":\"分页测量 20 s 未返回（D-019）\"}\n");
                }
                catch { }
                // 用 WPF 的正常退出通道（Environment.Exit 会绕过 WPF 清理，实测 SIGABRT）
                try { Current.Dispatcher.Invoke(() => Shutdown(3)); } catch { Environment.Exit(3); }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
            string json;
            try
            {
                // 一段足够长的文本 + 固定页尺寸，确保会分成多页
                var doc = new FlowDocument();
                doc.PageWidth = 400;
                doc.PageHeight = 300;
                doc.PagePadding = new Thickness(20);
                doc.ColumnWidth = 400;
                // 显式指定一个**可解析**的族：日志里 DEJAVU SANS 的 fallback=no/resolved 成功，
                //   而 GEORGIA / GLOBAL USER INTERFACE 都是 resolved=none。分页要逐字测量，
                //   落在"解析不到"的族上就可能反复重试（D-019 的现象）。
                doc.FontFamily = new FontFamily("DejaVu Sans");
                doc.FontSize = 12;
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
