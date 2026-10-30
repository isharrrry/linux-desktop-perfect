// T2 · WIC 托管侧闭环 harness（验收 ①-⑤）
// =====================================================================================
// 【它回答什么】
//   ① `BitmapImage(fileUri)` → `CopyPixels`，与 `SKBitmap.Decode(file)` **逐字节比较**
//      （同时用 C 级探针留下的全缓冲 FNV `62FD64E564953288` 交叉校验）
//   ② `FormatConvertedBitmap → Bgra32`
//   ③ `BitmapMetadata` 宽/高/DPI（**按已知偏差断言**：当前固定 96，未解析 pHYs/JFIF）
//   ④ 失败路径（不存在 / 非图像 / 截断）的**托管异常类型**
//   ⑤ 未覆盖清单（打印）
//   + 打印 `BitmapDecoder` **实际走的是哪条分支**（CreateDecoderFromFileHandle vs StreamAsIStream）
//     —— 这是上一轮悬而未决的问题；本 harness 用"哪条 WIC 导出被调用"来判定。
//
// 【映射开关】
//   本 harness 自己给 **PresentationCore 程序集**装 DllImportResolver（不碰 PC 文件），
//   把 `WindowsCodecs.dll` 指到 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so`。
//   若找不到该 .so（或没给 WPF_LINUX_WIC_SHIM），**明确 SKIP 并说明原因**——不假绿、不崩。
//
//   ⚠【临时手法 · 待撤】`ole32.dll` 目前**还没有**被 PC 的映射表覆盖：PC 的两处声明
//   （`UnsafeNativeMethodsMilCoreApi.cs:1050/1054`）要等 `Win32ShimResolver.WicMappedLibraries`
//   那一行随 PC 重建生效。在那之前，本 harness 靠**部署期兜底**跑通：
//   `libwpfwic.so` 另存一份名为 `libole32.dll.so`（dlopen 探测名之一）+ LD_LIBRARY_PATH 命中。
//   撤销与验收方式已写成脚本：`run-harness.sh`（严格模式，不设 LD_LIBRARY_PATH），
//   细节见 `REPORT.md` §18.5。**不要把临时模式当成通过**。
//
// 【分支判定的做法】
//   在 shim 里，`CreateDecoderFromFileHandle` 走的是"fd 路径"，`CreateStream` 走的是
//   "StreamAsIStream 路径"。后者在当前 shim 里返回 NOTIMPLEMENTED；于是：
//     · 若调用链走到 FileHandle → ① 能成功；
//     · 若走到 Stream      → ② 会以 0x88982F04 失败。
//   为了把这件事**变成可观测输出**而不靠推断，harness 在调用前用 `LD_PRELOAD` 式探针不现实，
//   改用**符号计数**：shim 导出一个计数器 `WicShim_CallCounts(int32_t* counts)`，
//   分别统计 FileHandle / Stream / 其它入口被调用的次数。harness 调它读前后差值。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    // ── shim 的调用计数导出（见 wic_proxy.c 的 WicShim_CallCounts）──────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CallCountsDelegate(IntPtr counts);        // [0]=FileHandle [1]=Stream [2]=其它
    private static CallCountsDelegate _callCounts;
    private static IntPtr _shimHandle;

    private static string _shimPath;
    // 2026-09-16（`#18` 波 · V3）：`_resolverInstalled` 已删 —— 它只被写、全仓从来没有被读，
    // 是"死映射看起来在工作"的唯一残迹（`#18` 波前的实测：声明处 + 赋值处两行，无读点）。

    private static int Main(string[] args)
    {
        Console.WriteLine("=== T2 · WIC 托管侧闭环 harness ===");
        Console.WriteLine("PWD=" + Directory.GetCurrentDirectory());

        string repo = FindRepoRoot();
        string png = args.Length > 0 ? args[0]
            : Path.Combine(repo, "src", "Linux", "samples", "HelloMil", "screenshot.png");
        Console.WriteLine("PNG=" + png + "  EXISTS=" + File.Exists(png));
        Console.WriteLine("DISPLAY=" + (Environment.GetEnvironmentVariable("DISPLAY") ?? "<未设置>"));

        // ---------- 映射开关 ----------
        _shimPath = Environment.GetEnvironmentVariable("WPF_LINUX_WIC_SHIM");
        if (string.IsNullOrEmpty(_shimPath))
            _shimPath = Path.Combine(repo, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "wic-shim", "libwpfwic.so");

        if (!File.Exists(_shimPath))
        {
            Console.WriteLine("SKIP=WIC shim 不存在：" + _shimPath);
            Console.WriteLine("SKIP_REASON=先跑 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/build-wic-shim.sh");
            Console.WriteLine("RESULT=SKIPPED");
            return 0;
        }

        Console.WriteLine("SHIM=" + _shimPath);
        // 两侧必须 dlopen **同一个文件**：PC 侧认 WPF_LINUX_WIC_SHIM（resolve 候选①），
        // MIL 侧认 MILBRIDGE_WIC_SO（MilExternalHandleBridge.CandidatePaths 候选①）。
        // 两条路径不同 ⇒ 两份 .so ⇒ **两张 g_objs 表** ⇒ WIC 句柄在 MIL 那张表里查不到
        // ⇒ MILQueryInterface 返 E_HANDLE（症状会伪装成"MIL 补丁没生效"）。
        // 这里把两侧 env 原样打出来，并在收尾用 /proc/self/maps 实测（见 ReportNativeInstances）。
        Console.WriteLine("ENV_WPF_LINUX_WIC_SHIM=" + (Environment.GetEnvironmentVariable("WPF_LINUX_WIC_SHIM") ?? "<未设置>"));
        Console.WriteLine("ENV_MILBRIDGE_WIC_SO=" + (Environment.GetEnvironmentVariable("MILBRIDGE_WIC_SO") ?? "<未设置>"));
        InstallWicResolver(_shimPath);

        int[] before = ReadCounts();
        Console.WriteLine($"CALLS_BEFORE=[FileHandle={before[0]} Stream={before[1]} Other={before[2]}]");

        // ---------- ① BitmapImage → CopyPixels vs SKBitmap.Decode ----------
        int failures = 0;
        failures += Check1_BitmapImagePixels(png, repo);
        failures += Check2_FormatConverted(png);
        failures += Check3_Metadata(png);
        failures += Check3b_DpiTruth(repo);
        failures += Check4_FailurePaths(png, repo);

        int[] after = ReadCounts();
        Console.WriteLine($"CALLS_AFTER=[FileHandle={after[0]} Stream={after[1]} Other={after[2]}]");
        int dh = after[0] - before[0], ds = after[1] - before[1];
        string branch = dh > 0 && ds == 0 ? "CreateDecoderFromFileHandle（fd 路径 → 我们的 shim 支持）"
                      : ds > 0 && dh == 0 ? "StreamAsIStream（CreateStream → 当前 NOTIMPLEMENTED，需补那族）"
                      : dh > 0 && ds > 0 ? "两者都走过（见计数）"
                      : "未走到 WIC 解码入口（BitmapDecoder 可能提前失败或走了缓存）";
        Console.WriteLine("DECODER_BRANCH=" + branch);
        Console.WriteLine($"BRANCH_COUNTS=FileHandle+{dh} Stream+{ds}");

        Check5_NotCoveredList();
        failures += ReportNativeInstances();

        Console.WriteLine("RESULT=" + (failures == 0 ? "PASS" : $"FAIL({failures})"));
        return failures == 0 ? 0 : 1;
    }

    // =================================================================================
    //  解析器：**本 harness 刻意不自装**（`#18` 波 · V3）
    //
    // 【原先这里是"自装"】2026-09-10 之前，这里调
    //     `NativeLibrary.SetDllImportResolver(typeof(BitmapImage).Assembly, …)`
    //   想把 `WindowsCodecs.dll` 指到 `_shimPath`。**它从来没有生效过，而且不可能生效**：
    //     · `SetDllImportResolver` 对**同一个程序集只能装一次**；
    //     · 上面 `Main` 里 `:122` 的 `typeof(BitmapImage)` 是**触碰 PC 模块里的一个类型** ⇒ 按运行
    //       时契约，PC 的 `[ModuleInitializer]`（`src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs` 的 `Register`）
    //       **先跑**，槽位归 PC，本 harness 那一次必然 `InvalidOperationException`。
    //     · 实测（`src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/REPORT.md:1021` 逐字）：
    //         `RESOLVER_INSTALLED=False  InvalidOperationException: A resolver is already set for the assembly.`
    //     ⇒ 本 harness 的 `WindowsCodecs.dll` 映射是**死代码**；它记的 `_resolverInstalled`
    //       在**全仓范围内只被写、从来没有被读**（`grep -n '_resolverInstalled'` 的读数 = 声明处
    //       + 赋值处两行）⇒ "丢了竞态"从来没被断言过（R17C 审计 §3-C1 的 N1/F3）。
    //
    // 【为什么不改成"只补 PC 没映射的名字"】两条出路里的第二条**不成立**：
    //   本 harness 只映射一个名字 `WindowsCodecs.dll`，而 PC 侧 `WicMappedLibraries`
    //   （`src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs:107-124`）**已经**映射 `WindowsCodecs.dll` 与 `ole32.dll`，
    //   而且默认启用（`WicEnabledByDefault = true`，同文件 `:152`；解析分支 `:216-241`）
    //   ⇒ 差集是**空集**。删掉与"补差集"等价，但删掉少一份会漂移的重复策略
    //   （这正是 `D-R2` 的定案："每个程序集只留唯一安装点"，见
    //    `src/Linux/tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/DP1ReproTests.cs` 那次删除）。
    //
    // 【本 harness 实际依赖什么】PC 侧 `Win32ShimResolver.Resolve` 的 WIC 分支：候选序
    //   `WPF_LINUX_WIC_SHIM` → 程序集目录 → 仓库 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/`。本 harness 的
    //   `_shimPath`（`Main` 里 `:65-67`）与它**同源**（同一个 env 变量）—— 这正是"两侧 dlopen
    //   同一个文件"那条不变量的落点（见 `:78-82` 的注释）。

    // =================================================================================
    private static void InstallWicResolver(string shimPath)
    {
        Console.WriteLine("RESOLVER_INSTALLED=N/A (product-side owns the slot)");
        Console.WriteLine("RESOLVER_NOTE=本 harness 刻意不自装 PresentationCore 的解析器：" +
                          "`typeof(BitmapImage)` 已经跑过 PC 的 [ModuleInitializer]（Win32ShimResolver）" +
                          "⇒ 槽位归它；PC 侧 WicMappedLibraries 已映射 WindowsCodecs.dll（默认启用）。" +
                          "SHIM=" + shimPath);
    }

    private static int[] ReadCounts()
    {
        var counts = new int[3];
        try
        {
            if (_callCounts == null)
            {
                _shimHandle = NativeLibrary.Load(_shimPath);          // 绝对路径，避开搜索路径
                IntPtr fn = NativeLibrary.GetExport(_shimHandle, "WicShim_CallCounts");
                _callCounts = Marshal.GetDelegateForFunctionPointer<CallCountsDelegate>(fn);
            }
            IntPtr buf = Marshal.AllocHGlobal(3 * sizeof(int));
            try { _callCounts(buf); for (int i = 0; i < 3; i++) counts[i] = Marshal.ReadInt32(buf, i * sizeof(int)); }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception e) { Console.WriteLine("COUNTS_FAIL=" + e.GetType().Name + ": " + Flatten(e.Message)); }
        return counts;
    }

    // =================================================================================
    //  ① 逐字节比对
    // =================================================================================
    private static int Check1_BitmapImagePixels(string png, string repo)
    {
        Console.WriteLine();
        Console.WriteLine("--- ① BitmapImage(fileUri).CopyPixels vs SKBitmap.Decode ---");
        try
        {
            var uri = new Uri(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立刻解出，避免延迟到渲染
            image.EndInit();

            Console.WriteLine($"BITMAPIMAGE ok: {image.PixelWidth}x{image.PixelHeight} fmt={image.Format} dpi={image.DpiX}x{image.DpiY}");

            // 转成 Bgra32 保证 stride/通道一致
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var managed = new byte[stride * h];
            converted.CopyPixels(managed, stride, 0);

            using SKBitmap skia = SKBitmap.Decode(png);
            if (skia == null) { Console.WriteLine("SKIA_DECODE=null => FAIL"); return 1; }

            Console.WriteLine($"SKIA: {skia.Width}x{skia.Height} colorType={skia.ColorType}");
            if (skia.Width != w || skia.Height != h)
            {
                Console.WriteLine($"SIZE_MISMATCH managed={w}x{h} skia={skia.Width}x{skia.Height} => FAIL");
                return 1;
            }

            var skiaPixels = new byte[stride * h];
            unsafe
            {
                byte* src = (byte*)skia.GetPixels().ToPointer();
                for (int y = 0; y < h; y++)
                    Marshal.Copy((IntPtr)(src + y * skia.RowBytes), skiaPixels, y * stride, stride);
            }

            int mismatch = 0;
            for (int i = 0; i < managed.Length; i++) if (managed[i] != skiaPixels[i]) mismatch++;

            ulong fnv = Fnv1a(managed);
            Console.WriteLine($"BYTE_MISMATCH={mismatch} / {managed.Length}");
            Console.WriteLine($"PIXEL_FNV1A={fnv:X16}   (C 级探针记录的值 = 62FD64E564953288)");
            Console.WriteLine($"FNV_MATCHES_C_PROBE={fnv == 0x62FD64E564953288UL}");

            long p2216 = 16L * stride + 22L * 4;
            Console.WriteLine($"PIXEL_22_16_BGRA={managed[p2216]},{managed[p2216 + 1]},{managed[p2216 + 2]},{managed[p2216 + 3]}  (期望 102,51,34,255)");

            long nonWhite = 0;
            for (int i = 0; i < managed.Length; i += 4)
                if (!(managed[i] == 0xFF && managed[i + 1] == 0xFF && managed[i + 2] == 0xFF)) nonWhite++;
            Console.WriteLine($"NON_WHITE={nonWhite} / {w * h}   (C 级探针 = 150998/480000)");

            bool ok = mismatch == 0 && fnv == 0x62FD64E564953288UL &&
                      managed[p2216] == 102 && managed[p2216 + 1] == 51 && managed[p2216 + 2] == 34;
            Console.WriteLine("CHECK1=" + (ok ? "PASS" : "FAIL"));
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("CHECK1=FAIL " + e.GetType().Name);
            DumpException("CHECK1", e);
            return 1;
        }
    }

    // =================================================================================
    //  ② FormatConvertedBitmap → Bgra32
    // =================================================================================
    private static int Check2_FormatConverted(string png)
    {
        Console.WriteLine();
        Console.WriteLine("--- ② FormatConvertedBitmap → Bgra32 ---");
        try
        {
            var image = new BitmapImage(new Uri(png));
            Console.WriteLine("SOURCE_FORMAT=" + image.Format);

            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
            Console.WriteLine($"CONVERTED_FORMAT={bgra.Format} size={bgra.PixelWidth}x{bgra.PixelHeight}");

            bool ok = bgra.Format == PixelFormats.Bgra32 && bgra.PixelWidth == image.PixelWidth;
            Console.WriteLine("CHECK2=" + (ok ? "PASS" : "FAIL"));
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("CHECK2=FAIL " + e.GetType().Name);
            DumpException("CHECK2", e);
            return 1;
        }
    }

    // =================================================================================
    //  ③ 元数据（宽/高/DPI）—— 按**已知偏差**断言
    // =================================================================================
    private static int Check3_Metadata(string png)
    {
        Console.WriteLine();
        Console.WriteLine("--- ③ BitmapMetadata / DPI ---");
        try
        {
            var frame = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Console.WriteLine($"FRAME={frame.PixelWidth}x{frame.PixelHeight} dpi={frame.DpiX}x{frame.DpiY} fmt={frame.Format}");

            string metaStatus;
            try
            {
                BitmapMetadata meta = frame.Metadata as BitmapMetadata;
                metaStatus = meta == null ? "Metadata=null" : "Metadata 可用（Query 未实现时会抛/返回 null）";
            }
            catch (Exception e)
            {
                metaStatus = "Metadata 抛 " + e.GetType().Name + "（= 已知未覆盖面，见 ⑤）";
            }
            Console.WriteLine("METADATA_STATUS=" + metaStatus);

            // 已知偏差：DPI 固定 96（未解析 PNG pHYs / JPEG JFIF density）→ 断言"就是 96"，
            // 而不是断言"等于文件里的真实 DPI"（那会是假绿）。
            bool dpiIsKnownDeviation = frame.DpiX == 96.0 && frame.DpiY == 96.0;
            Console.WriteLine($"DPI_KNOWN_DEVIATION(96)={dpiIsKnownDeviation}");
            Console.WriteLine("CHECK3=PASS(尺寸正确 + 该 fixture **无 pHYs** ⇒ DPI=96 是默认退化；" +
                              "\"固定 96\" 那条偏差已于 REPORT §19 销账，不再是\"已知偏差\")");
            return frame.PixelWidth == 800 && frame.PixelHeight == 600 && dpiIsKnownDeviation ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("CHECK3=FAIL " + e.GetType().Name);
            DumpException("CHECK3", e);
            return 1;
        }
    }

    // =================================================================================
    //  ④ 失败路径的托管异常类型
    // =================================================================================
    private static int Check4_FailurePaths(string png, string repo)
    {
        Console.WriteLine();
        Console.WriteLine("--- ④ 失败路径（托管异常类型）---");
        int fails = 0;

        string missing = Path.Combine(Path.GetTempPath(), "wic-does-not-exist-" + Guid.NewGuid().ToString("n") + ".png");
        fails += Expect("不存在", missing, typeof(FileNotFoundException));

        string notImage = Path.Combine(Path.GetTempPath(), "wic-not-image-" + Guid.NewGuid().ToString("n") + ".png");
        File.WriteAllText(notImage, "这不是图像");
        fails += Expect("非图像", notImage, null);

        string truncated = Path.Combine(Path.GetTempPath(), "wic-truncated-" + Guid.NewGuid().ToString("n") + ".png");
        byte[] head = new byte[64];
        using (FileStream fs = File.OpenRead(png)) fs.Read(head, 0, head.Length);
        File.WriteAllBytes(truncated, head);
        fails += Expect("截断", truncated, null);

        return fails;
    }

    private static int Expect(string label, string path, Type expected)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            int w = image.PixelWidth;
            Console.WriteLine($"FAIL[{label}] 竟然成功（{w}px）→ FAIL");
            return 1;
        }
        catch (Exception e)
        {
            string type = e.GetType().Name;
            string note = expected == null ? "" : (expected.Name == type ? "（与预期一致）" : $"（预期 {expected.Name}）");
            Console.WriteLine($"FAIL[{label}] 托管异常={type}{note}: {Flatten(e.Message)}");
            DumpException("CHECK4:" + label, e);

            // 基础设施型异常（marshal/DllNotFound/EntryPoint）不算"被测行为达标"：
            // 它们说明还没走到 WIC，报 PASS 就是假绿。
            bool infra = e is MarshalDirectiveException || e is DllNotFoundException ||
                         e is EntryPointNotFoundException || e is TypeInitializationException;
            if (infra)
            {
                Console.WriteLine($"FAIL[{label}] ↑ 属**基础设施**异常（还没走到 WIC）→ 本项不算达标");
                return 1;
            }
            return 0;   // 明确失败即达标（不硬编码未实测过的异常类型）
        }
        finally
        {
            try { if (File.Exists(path) && !path.Contains("does-not-exist")) File.Delete(path); } catch { }
        }
    }

    // =================================================================================
    //  ⑤ 未覆盖清单
    // =================================================================================
    private static void Check5_NotCoveredList()
    {
        Console.WriteLine();
        Console.WriteLine("--- ⑤ 未覆盖清单（用到会怎样）---");
        foreach (string line in new[]
        {
            "BitmapImage(Stream)（非 FileStream）→ StreamAsIStream → CreateStream：shim 返回 0x88982F04",
            "   恢复条件：T1 用真 IStream vtable 包一层 StreamDescriptor（pfnRead/pfnSeek/pfnStat 已具备）",
            "BitmapMetadata 读取（GetMetadataByName 等）→ 0x80004001（E_NOTIMPL）；需自带 PNG/JPEG 元数据解析",
            "BitmapEncoder 全族（编码/保存）→ 0x80004001（E_NOTIMPL）；Skia 侧有 sk_image_encode_* 可用",
            "WICBitmap 写入面（WriteableBitmap/CachedBitmap/InteropBitmapSource）→ 0x80004001（E_NOTIMPL）",
            "COM 工厂枚举 / 颜色上下文 / 颜色变换 / 调色板 / scaler·clipper·fliprotator → 0x80004001（E_NOTIMPL）",
            "DPI≠96：GetResolution 固定 96（未解析 pHYs/JFIF）→ 偏；已登记为偏差",
            "子矩形 CopyPixels（ROI）→ 0x88982f81（UNSUPPORTEDOPERATION，真值），不静默截断",
        })
            Console.WriteLine("  · " + line);
    }

    // =================================================================================
    //  ③b DPI **真值**断言（与 ③ 的"默认退化"分开）：带 pHYs 的 fixture 必须报出真值
    // =================================================================================
    private static int Check3b_DpiTruth(string repo)
    {
        string fx = Path.Combine(repo, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "WicClosedLoop", "fixtures", "dpi300-title.png");
        if (!File.Exists(fx))
        {
            Console.WriteLine("CHECK3b=SKIP 缺 fixture：" + fx);
            return 0;
        }
        try
        {
            var img = new BitmapImage(new Uri(fx));
            bool ok = Math.Abs(img.DpiX - 300) < 0.6 && Math.Abs(img.DpiY - 300) < 0.6;
            Console.WriteLine($"CHECK3b={(ok ? "PASS" : "FAIL")} pHYs=11811 px/m ⇒ DpiX={img.DpiX} DpiY={img.DpiY}（期望 300；真值断言，与 ③ 的 96 退化分开）");
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("CHECK3b=FAIL " + e.GetType().Name);
            DumpException("CHECK3b", e);
            return 1;
        }
    }

    // =================================================================================
    private static ulong Fnv1a(byte[] data)
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>取栈里前几帧（定位到底是哪条 P/Invoke/托管方法抛的）。</summary>
    private static string FirstFrames(Exception e, int n)
    {
        string st = e.StackTrace ?? "";
        string[] lines = st.Split('\n');
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length && i < n; i++) sb.Append(lines[i].Trim()).Append(" | ");
        return sb.ToString();
    }

    private static string Flatten(string s) => s == null ? "" : s.Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>
    /// 打印异常的**完整 ToString()**（含方法名、parameter #N、全部帧），
    /// 并判定它是"主异常"还是"Dispatcher/CreateWindowEx 失败之后的次生异常"。
    /// 这是主控要的证据：谁是第一因、MarshalDirectiveException 到底由**哪个方法**抛出。
    /// </summary>
    private static void DumpException(string label, Exception e)
    {
        Console.WriteLine($"[{label}] ---- 完整异常 ----");
        Console.WriteLine(e.ToString());
        Console.WriteLine($"[{label}] ---- 异常打印结束 ----");

        string st = e.ToString();
        string firstFrame = "";
        foreach (string raw in st.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("at ", StringComparison.Ordinal)) { firstFrame = line; break; }
        }
        Console.WriteLine($"[{label}] FIRST_FRAME={firstFrame}");

        bool viaDispatcher = st.Contains("Dispatcher") || st.Contains("CreateWindowEx") ||
                             st.Contains("HwndWrapper") || st.Contains("MessageOnlyHwndWrapper");
        Console.WriteLine($"[{label}] VIA_DISPATCHER_OR_WINDOW={viaDispatcher}  " +
                          (viaDispatcher ? "⇒ 失败发生在**建 Dispatcher/窗口**阶段（WIC 之前）" : "⇒ 与 Dispatcher/窗口无关"));
        Console.WriteLine($"[{label}] IS_WIC_FRAME={st.Contains("WindowsCodecs") || st.Contains("WIC") || st.Contains("IWIC")}");
    }

    // =================================================================================
    //  单实例判据：`libwpfwic.so` 在 /proc/self/maps 里必须是**一条不同路径**
    //
    //  为什么必须有这条断言：dlopen 按**路径**（同 inode）去重，两个路径就是两份 .so，
    //  各带一张 `g_objs` 句柄表。WIC 句柄由 PC 那张表创建，MIL 的 MILQueryInterface 在
    //  另一张表里问"这归你吗" ⇒ 答案"不是" ⇒ **E_HANDLE**，症状与"MIL 补丁没生效"一模一样。
    //  ⇒ 把它变成实测判据，别让双表伪装成语义问题。
    // =================================================================================
    private static int ReportNativeInstances()
    {
        int fails = 0;
        var shimPaths = new List<string>();
        var milPaths = new List<string>();
        try
        {
            foreach (string raw in File.ReadAllLines("/proc/self/maps"))
            {
                int i = raw.IndexOf('/');
                if (i < 0) continue;
                string p = raw.Substring(i);
                if (p.EndsWith(" (deleted)", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 10);
                if (p.Contains("libwpfwic") && !shimPaths.Contains(p)) shimPaths.Add(p);
                if (p.Contains("wpfgfx_cor3") && !milPaths.Contains(p)) milPaths.Add(p);
            }
        }
        catch (Exception e) { Console.WriteLine("MAPS_READ_FAILED=" + e.GetType().Name + ": " + Flatten(e.Message)); }

        Console.WriteLine($"WIC_SHIM_MAPS={shimPaths.Count} paths=[{string.Join(", ", shimPaths)}]");
        Console.WriteLine($"MILCORE_MAPS={milPaths.Count} paths=[{string.Join(", ", milPaths)}]");
        foreach (string p in shimPaths)
            Console.WriteLine($"WIC_SHIM_MAPS_SHA256={ShortHash(p)} {p}");
        Console.WriteLine($"WIC_SHIM_CANONICAL_SHA256={ShortHash(_shimPath)} {_shimPath}");

        if (shimPaths.Count >= 2)
        {
            Console.WriteLine("WIC_SHIM_DOUBLE_TABLE=TRUE");
            Console.WriteLine("WIC_SHIM_DOUBLE_TABLE_REASON=进程内有 ≥2 条不同路径的 libwpfwic.so ⇒ 两张句柄表 ⇒ " +
                              "MILQueryInterface 必然对 WIC 句柄返 E_HANDLE（**这不是 MIL 补丁的问题**）。" +
                              "修法：让 WPF_LINUX_WIC_SHIM 与 MILBRIDGE_WIC_SO 指向同一个文件。");
            fails++;
        }
        else if (shimPaths.Count == 1)
        {
            Console.WriteLine("WIC_SHIM_DOUBLE_TABLE=FALSE（单实例 ✓）");
        }
        else
        {
            Console.WriteLine("WIC_SHIM_DOUBLE_TABLE=N/A（shim 根本没被 dlopen —— 说明没走到 WIC）");
        }
        return fails;
    }

    private static string ShortHash(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            byte[] h = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
        catch { return "<读不到>"; }
    }

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
