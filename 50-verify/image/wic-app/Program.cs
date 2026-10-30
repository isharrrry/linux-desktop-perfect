// WIC 端到端验证（无 XAML 入口：不触发标记编译，免得为"加载一张 PNG"引入 PBT 依赖）。
// 用自产 WPF 的 BitmapImage 加载 PNG → 读像素尺寸/格式 → 写 JSON → 退出。
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LdpVerify.Wic
{
    public sealed class WicCheckApp : Application
    {
        private readonly string _png;
        private readonly string _out;
        private readonly string _dstIcc;

        public WicCheckApp(string png, string @out, string dstIcc = null) { _png = png; _out = @out; _dstIcc = dstIcc; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string json;
            try
            {
                if (string.IsNullOrEmpty(_png) || !File.Exists(_png)) throw new FileNotFoundException("缺 PNG 输入", _png);
                // 用 BitmapFrame.Create 而不是 BitmapImage：前者带 `ColorContexts`
                //   （色彩转换要用源的内嵌 profile），后者只是"一个位图源"。
                var bmp = BitmapFrame.Create(new Uri(Path.GetFullPath(_png)),
                                             BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (string.IsNullOrEmpty(_dstIcc))
                {
                    json = string.Format("{{\"ok\":true,\"width\":{0},\"height\":{1},\"format\":\"{2}\"}}",
                        bmp.PixelWidth, bmp.PixelHeight, bmp.Format);
                }
                else
                {
                    // 显式色彩转换：源用图内嵌 profile（bmp.ColorContexts），目标用给定的 .icc 文件。
                    // WPF 的 ColorConvertedBitmap 会在渲染/拷贝像素时走 WIC 的色彩转换入口
                    // （WindowsCodecsExt 的 WICCreateColorTransform/Initialize）—— 这正是 ACC-019 要比的对象。
                    var srcCtx = bmp.ColorContexts;
                    if (srcCtx == null || srcCtx.Count == 0) throw new InvalidOperationException("源没有内嵌 ICC profile");
                    var dstCtx = new ColorContext(new Uri(Path.GetFullPath(_dstIcc)));
                    var converted = new ColorConvertedBitmap(bmp, srcCtx[0], dstCtx, PixelFormats.Bgra32);
                    int w = converted.PixelWidth, h = converted.PixelHeight;
                    int stride = w * 4;
                    byte[] px = new byte[stride * h];
                    converted.CopyPixels(px, stride, 0);
                    // 只回前 8 个像素，够做色差比对且不让 JSON 变大
                    var sb = new System.Text.StringBuilder();
                    sb.Append(string.Format("{{\"ok\":true,\"width\":{0},\"height\":{1},\"format\":\"{2}\",\"pixels\":[",
                        w, h, converted.Format));
                    int n = Math.Min(8, w * h);
                    for (int i = 0; i < n; ++i)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(string.Format("[{0},{1},{2},{3}]", px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]));
                    }
                    sb.Append("]}");
                    json = sb.ToString();
                }
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
            string dstIcc = args.Length > 2 ? args[2] : null;   // 给了就做显式色彩转换
            var app = new WicCheckApp(png, output, dstIcc);
            app.Run();
        }
    }
}
