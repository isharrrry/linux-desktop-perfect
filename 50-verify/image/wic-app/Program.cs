// WIC 端到端验证（无 XAML 入口：不触发标记编译，免得为"加载一张 PNG"引入 PBT 依赖）。
// 用自产 WPF 的 BitmapImage 加载 PNG → 读像素尺寸/格式 → 写 JSON → 退出。
using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LdpVerify.Wic
{
    public sealed class WicCheckApp : Application
    {
        private readonly string _png;
        private readonly string _out;

        public WicCheckApp(string png, string @out) { _png = png; _out = @out; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string json;
            try
            {
                if (string.IsNullOrEmpty(_png) || !File.Exists(_png)) throw new FileNotFoundException("缺 PNG 输入", _png);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(Path.GetFullPath(_png));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                json = string.Format("{{\"ok\":true,\"width\":{0},\"height\":{1},\"format\":\"{2}\"}}",
                    bmp.PixelWidth, bmp.PixelHeight, bmp.Format);
            }
            catch (Exception ex)
            {
                json = string.Format("{{\"ok\":false,\"type\":\"{0}\",\"error\":\"{1}\",\"stack\":\"{2}\"}}",
                    ex.GetType().Name, ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    (ex.StackTrace ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " | "));
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
            string png = args.Length > 0 ? args[0] : null;
            string output = args.Length > 1 ? args[1] : "/tmp/ldp-wic-result.json";
            var app = new WicCheckApp(png, output);
            app.Run();
        }
    }
}
