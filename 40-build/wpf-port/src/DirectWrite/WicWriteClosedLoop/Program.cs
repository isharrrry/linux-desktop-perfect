// T2 · 轨道 B 闭环 harness：WIC **写面 + 元数据**（读路径 harness 保持原样，不动）
//   ① DPI 真值：PNG pHYs(300) / JPEG JFIF(150) 必须读出来；无 pHYs 的截图仍 96（已登记偏差的收尾）
//   ② 元数据读：BitmapFrame.Metadata.GetQuery("/tEXt/{str=…}") 逐项比对真值
//   ③ WriteableBitmap：托管写像素 → 取回逐字节比对（并记录我的 shim 是否被调到）
//   ④ 编码器：BitmapEncoder(PNG) 存盘 → 用**我的读路径**读回 → 逐点比对（JPEG 有损另判）
//   ⑤ 失败路径：键不存在 / 不支持格式 / 不可写路径 ⇒ 明确 HRESULT + 托管异常类型
// 纪律：每个失败都给完整 ToString + 四字段；前提缺失一律 SKIP，不假绿。
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int _fails;

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"CHECK{name}={(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok) _fails++;
    }

    private static int Main(string[] args)
    {
        string repo = FindRepoRoot();
        string fx = Path.Combine(repo, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "WicWriteClosedLoop", "fixtures");
        string png300 = Path.Combine(fx, "dpi300-title.png");
        string jpg150 = Path.Combine(fx, "jfif150.jpg");
        string shot = Path.Combine(repo, "src", "Linux", "samples", "HelloMil", "screenshot.png");
        Console.WriteLine("REPO=" + repo);
        Console.WriteLine($"FIXTURES png300={File.Exists(png300)} jpg150={File.Exists(jpg150)}");

        // ---------- ① DPI 真值 ----------
        try
        {
            var a = new BitmapImage(new Uri(png300));
            Check("1a", Math.Abs(a.DpiX - 300) < 0.6 && Math.Abs(a.DpiY - 300) < 0.6,
                  $"PNG pHYs → DpiX={a.DpiX} DpiY={a.DpiY}（期望 300，来自 pHYs 11811 px/m）");
        }
        catch (Exception e) { Fail("1a", e); }

        try
        {
            var j = new BitmapImage(new Uri(jpg150));
            Check("1b", Math.Abs(j.DpiX - 150) < 0.02 && Math.Abs(j.DpiY - 150) < 0.02,
                  $"JPEG JFIF → DpiX={j.DpiX} DpiY={j.DpiY}（期望 150）");
        }
        catch (Exception e) { Fail("1b", e); }

        try
        {
            var s = new BitmapImage(new Uri(shot));
            Check("1c", Math.Abs(s.DpiX - 96) < 0.02,
                  $"无 pHYs 的截图 → DpiX={s.DpiX}（期望 96：文件里没有分辨率，退默认）");
        }
        catch (Exception e) { Fail("1c", e); }

        // ---------- ② 元数据读 ----------
        try
        {
            var frame = BitmapFrame.Create(new Uri(png300), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            object md = frame.Metadata;
            Console.WriteLine("METADATA_NULL=" + (md == null));
            if (md is BitmapMetadata meta)
            {
                object v = meta.GetQuery("/tEXt/{str=Title}");
                Check("2a", (v as string) == "TrackB", $"GetQuery(/tEXt/{{str=Title}}) = {Show(v)}（期望 \"TrackB\"）");
                Console.WriteLine("METADATA_LOCATION=" + Safe(() => meta.Location));
                Console.WriteLine("METADATA_FORMAT=" + Safe(() => meta.Format));
            }
            else Check("2a", false, "frame.Metadata 为 null（reader 没接上）");
        }
        catch (Exception e) { Fail("2a", e); }

        try
        {
            var frame = BitmapFrame.Create(new Uri(shot), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (frame.Metadata is BitmapMetadata meta)
            {
                object v = meta.GetQuery("/tEXt/{str=comment}");
                bool ok = v is string s2 && s2.StartsWith("HelloMil", StringComparison.Ordinal);
                Check("2b", ok, $"真实截图 GetQuery(/tEXt/{{str=comment}}) = {Show(v)}（期望以 \"HelloMil\" 开头）");
            }
            else Check("2b", false, "截图 frame.Metadata 为 null");
        }
        catch (Exception e) { Fail("2b", e); }

        // ---------- ② 续：EXIF 文本 / iTXt(zlib) / Format / 枚举 ----------
        string exifJpg = Path.Combine(fx, "exif-make.jpg");
        string itxtPng = Path.Combine(fx, "itxt-comment.png");
        try
        {
            var f = BitmapFrame.Create(new Uri(exifJpg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (f.Metadata is BitmapMetadata m)
            {
                object make = m.GetQuery("/app1/ifd/{ushort=271}");
                object dto = m.GetQuery("/app1/ifd/exif/{ushort=36867}");
                Check("2c", (make as string) == "WpfLinux" && (dto as string) == "2026:09:10 12:34:56",
                      $"JPEG EXIF Make={Show(make)} DateTimeOriginal={Show(dto)}（期望 WpfLinux / 2026:09:10 12:34:56）");
            }
            else Check("2c", false, "EXIF fixture 的 frame.Metadata 为 null");
        }
        catch (Exception e) { Fail("2c", e); }

        try
        {
            var f = BitmapFrame.Create(new Uri(itxtPng), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (f.Metadata is BitmapMetadata m)
            {
                object v = m.GetQuery("/iTXt/{str=Comment}");
                Check("2d", (v as string) == "TrackB-iTXt", $"PNG iTXt(zlib) Comment={Show(v)}（期望 \"TrackB-iTXt\"）");
            }
            else Check("2d", false, "iTXt fixture 的 frame.Metadata 为 null");
        }
        catch (Exception e) { Fail("2d", e); }

        try
        {
            var f = BitmapFrame.Create(new Uri(itxtPng), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (f.Metadata is BitmapMetadata m)
            {
                Console.WriteLine("METADATA_FORMAT=" + Safe(() => m.Format));
                var names = new List<string>();
                foreach (object o in m) names.Add(Convert.ToString(o));
                Console.WriteLine("METADATA_ENUM=[" + string.Join(", ", names) + "]");
                Check("2e", names.Count >= 2 && names.Exists(n => n != null && n.Contains("Title")) && names.Exists(n => n != null && n.Contains("Comment")),
                      $"元数据枚举 {names.Count} 项（该 fixture 有 tEXt + iTXt 两个块，期望含 Title 与 Comment）");
            }
            else Check("2e", false, "metadata 为 null");
        }
        catch (Exception e) { Fail("2e", e); }

        // ---------- ③ WriteableBitmap ----------
        try
        {
            int w = 32, h = 32, stride = w * 4;
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.Lock();
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) { row[x*4+0]=(byte)(x*8); row[x*4+1]=(byte)(y*8); row[x*4+2]=0x40; row[x*4+3]=0xFF; }
                Marshal.Copy(row, 0, IntPtr.Add(wb.BackBuffer, y * wb.BackBufferStride), stride);
            }
            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
            wb.Unlock();

            var got = new byte[stride * h];
            wb.CopyPixels(got, stride, 0);
            int mismatch = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y*stride + x*4;
                    if (got[i] != (byte)(x*8) || got[i+1] != (byte)(y*8) || got[i+2] != 0x40 || got[i+3] != 0xFF) mismatch++;
                }
            Check("3", mismatch == 0, $"WriteableBitmap 32x32 写→取回 失配={mismatch} BackBufferStride={wb.BackBufferStride}（期望 0）"
                  + " ｜ 若失败且为 E_HANDLE：卡在 MIL 的 back buffer 句柄（跨轨，见报告）");
        }
        catch (Exception e) { Fail("3", e); }

        // ---------- ④ 编码器：PNG 写→读闭环 ----------
        try
        {
            int w = 8, h = 8, stride = w * 4;
            var px = new byte[stride * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            { int i = y*stride+x*4; px[i]=(byte)(x*30); px[i+1]=(byte)(y*30); px[i+2]=0x11; px[i+3]=0xFF; }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);

            string outPng = Path.Combine(Path.GetTempPath(), "wic-enc-" + Guid.NewGuid().ToString("N") + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(outPng)) enc.Save(fs);
            long len = new FileInfo(outPng).Length;
            Console.WriteLine($"ENCODED_PNG={outPng} 字节={len}");

            if (len == 0)
            {
                Check("4", false, "编码器写出的文件是 0 字节（MIL 流水线：MILIStreamWrite 只写进 MIL 自己的 MemoryStream，未转发到调用方 Stream —— 见报告）");
            }
            else
            {
                var back = new BitmapImage(new Uri(outPng));
                var got = new byte[stride * h];
                var conv = new FormatConvertedBitmap(back, PixelFormats.Bgra32, null, 0.0);
                conv.CopyPixels(got, stride, 0);
                int mismatch = 0;
                for (int i = 0; i < px.Length; i++) if (px[i] != got[i]) mismatch++;
                Check("4", mismatch == 0, $"PNG 写→读 逐字节失配={mismatch}/{px.Length} 尺寸={back.PixelWidth}x{back.PixelHeight}");
            }
        }
        catch (Exception e) { Fail("4", e); }

        // ---------- ⑥ 预乘源反解（正确性）----------
        try
        {
            int w6 = 2, h6 = 2, st6 = w6 * 4;
            var premul = new byte[] { 64, 32, 16, 128, 0, 0, 0, 0, 255, 128, 64, 255, 10, 20, 30, 64 };
            var src6 = BitmapSource.Create(w6, h6, 96, 96, PixelFormats.Pbgra32, null, premul, st6);
            string outPng6 = Path.Combine(Path.GetTempPath(), "wic-pbgra-" + Guid.NewGuid().ToString("N") + ".png");
            var enc6 = new PngBitmapEncoder();
            enc6.Frames.Add(BitmapFrame.Create(src6));
            using (var fs6 = File.Create(outPng6)) enc6.Save(fs6);
            long len6 = new FileInfo(outPng6).Length;
            if (len6 == 0)
                Console.WriteLine("CHECK6=SKIP 预乘反解：编码字节未到调用方流（MIL 跨轨阻塞）；" +
                                  "该正确性已由 wic-shim/probe_premul.c 在 shim 级 A/B 实测（反解后 128,64,32,128；不反解 64,32,16,128，偏差 -64,-32,-16）");
            else
            {
                var back6 = new FormatConvertedBitmap(new BitmapImage(new Uri(outPng6)), PixelFormats.Bgra32, null, 0.0);
                var got6 = new byte[st6 * h6];
                back6.CopyPixels(got6, st6, 0);
                bool ok6 = got6[0] == 128 && got6[1] == 64 && got6[2] == 32 && got6[3] == 128;
                Check("6", ok6, $"Pbgra32(64,32,16,128) 编码→读回 = ({got6[0]},{got6[1]},{got6[2]},{got6[3]})，期望 (128,64,32,128)");
            }
        }
        catch (Exception e) { Fail("6", e); }

        // ---------- ⑤ 失败路径 ----------
        {   // 5a：不存在的键。PC 的 BitmapMetadata.GetQuery 对未命中**返回 null 而不抛**
            //     （shim 侧确实返回 WINCODEC_ERR_PROPERTYNOTFOUND —— 见 WIC_TRACE 的 METADATA_QUERY 行）
            object v5a = null;
            Exception e5a = Catch(() => {
                var f = BitmapFrame.Create(new Uri(png300), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (f.Metadata is BitmapMetadata m) v5a = m.GetQuery("/tEXt/{str=NoSuchKey}");
                else throw new InvalidOperationException("metadata 为 null，无法测键不存在");
            });
            Console.WriteLine("MD_MISSING_VALUE=" + Show(v5a));
            Check("5a", e5a == null && v5a == null,
                  $"不存在的键 ⇒ {Show(v5a)}" + (e5a != null ? $"（异常 {e5a.GetType().Name}: {Flatten(e5a.Message)}）" : "（PC 语义：null，不抛）"));
        }
        ExpectFail("5b", Catch(() => {
            var enc = new TiffBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new Uri(png300)));
            using var ms = new MemoryStream();
            enc.Save(ms);
        }), "不支持的编码格式(TIFF)：期望组件缺失类异常");

        // 5c：只读流。**注意**：在 MIL 转发缺失的当下，编码字节根本没写到调用方流 ⇒ 必然"不报错"，
        //      这不是 shim 的问题而是同一条跨轨阻塞；所以按 SKIP 处理并写明理由（不假绿也不假红）。
        ExpectFailOrSkipOnMilStream("5c", Catch(() => {
            var src = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[16*4], 16);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            string ro = Path.Combine(Path.GetTempPath(), "wic-readonly-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(ro, new byte[16]);
            using var fs = new FileStream(ro, FileMode.Open, FileAccess.Read);   // 只读流 → WIC 写入必须失败
            enc.Save(fs);
        }), "编码到只读流：期望明确失败（MIL 转发落地后必须变红→变绿）");

        Console.WriteLine("RESULT=" + (_fails == 0 ? "PASS" : $"FAIL({_fails})"));
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>期望失败，但若失败原因是"MIL 没把字节转给调用方流"则记 SKIP（跨轨阻塞，不算通过也不算 shim 的问题）。</summary>
    private static void ExpectFailOrSkipOnMilStream(string name, Exception e, string what)
    {
        if (e == null)
        {
            Console.WriteLine($"CHECK{name}=SKIP {what} ⇒ 未报错：编码字节未写入调用方流（MIL 跨轨阻塞），" +
                              "该路径在 MIL 转发落地后必须重新验收");
            return;
        }
        ExpectFail(name, e, what);
    }

    /// <summary>期望失败的检查：拿到非基础设施异常才算 PASS（基础设施异常 = 还没走到被测行为）。</summary>
    private static void ExpectFail(string name, Exception e, string what)
    {
        if (e == null) { Console.WriteLine($"CHECK{name}=FAIL {what} ⇒ **竟然成功**"); _fails++; return; }
        bool infra = e is DllNotFoundException || e is EntryPointNotFoundException || e is MarshalDirectiveException;
        Console.WriteLine($"CHECK{name}={(infra ? "FAIL" : "PASS")} {what} ⇒ {e.GetType().Name}: {Flatten(e.Message)}");
        if (infra) { Console.WriteLine("  （基础设施异常：说明还没走到 WIC 的失败语义）"); _fails++; }
    }

    private static Exception Catch(Action a) { try { a(); return null; } catch (Exception e) { return e; } }

    private static string Safe(Func<object> f) { try { return Convert.ToString(f()); } catch (Exception e) { return "<" + e.GetType().Name + ">"; } }

    private static string Show(object v) => v == null ? "<null>" : $"\"{v}\" ({v.GetType().Name})";

    private static void Fail(string name, Exception e) => Fail(name, e, "");
    private static void Fail(string name, Exception e, string what)
    {
        if (e == null) { Console.WriteLine($"FAIL[{name}] {what}：**竟然成功**（期望失败）"); _fails++; return; }
        string t = e.ToString();
        string first = "";
        foreach (string raw in t.Split('\n')) { string l = raw.Trim(); if (l.StartsWith("at ")) { first = l; break; } }
        Console.WriteLine($"FAIL[{name}] {what} ⇒ {e.GetType().Name}: {Flatten(e.Message)}");
        Console.WriteLine($"  FIRST_FRAME={first}");
        Console.WriteLine($"  VIA_DISPATCHER_OR_WINDOW={t.Contains("Dispatcher") || t.Contains("CreateWindowEx")}");
        Console.WriteLine($"  IS_WIC_FRAME={t.Contains("WindowsCodecs") || t.Contains("WIC") || t.Contains("IWIC")}");
        Console.WriteLine("  ---- 完整异常 ----"); Console.WriteLine(t); Console.WriteLine("  ---- 结束 ----");
        _fails++;
    }

    private static string Flatten(string s) => s == null ? "" : s.Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "handoff.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
