// M7c 收尾轮 · P0（默认配置无文字）的 **B 步**：让渲染器请求的字族与 WPF 侧**同源**。
//
// ============================================================================
//  【问题（主控实测，我复核过第一现场）】
//    不设任何字体 env（= **真应用默认拿到的路径**）跑 HelloWpf：文字整段不画
//    （台账只有一行 `未画种类 1`）。而 `MilPresentation.EnsureGlyphRenderer` 当时是：
//        目录 = WPF_LINUX_TEXT_FONT_DIR → WPF_LINUX_FONT_DIR → /usr/share/fonts
//        字族 = WPF_LINUX_TEXT_FONT_FAMILY → WPF_LINUX_UI_FONT → **"Noto Sans"（硬编码）**
//    `"/usr/share/fonts"` 里没有真正的 `Noto Sans` 族，而 WPF 侧解析出的 message font 是
//    `DejaVu Sans` ⇒ **字形 id 来自一个面、光栅化用另一个面**（违反 handoff:1539 的不变量）。
//
//  【为什么改成"问 SPI"而不是再猜一个族名】
//    Windows 真机 oracle（主控转来）给了真值支撑：
//      `SPI_GETNONCLIENTMETRICS.lfMessageFont.lfFaceName`
//      = `HKCU\Control Panel\Desktop\WindowMetrics\MessageFont`（92 字节 LOGFONTW blob）
//      = `SystemFonts.MessageFontFamily.Source`
//    —— **三处逐字相同**，且该族必在 `Fonts.SystemFontFamilies` 里。
//    我们的 shim 已经用同一个源回答 SPI（`win32_misc.c` 的 `WPF_DEFAULT_UI_FONT = "DejaVu Sans"`
//    / `WPF_LINUX_UI_FONT` 覆盖），**WPF 的 message font 就是从这里来的**
//    ⇒ 渲染器问 SPI = 问"WPF 正在用的那个族"，两边**同源**，不再是两个独立的猜测。
//    env 覆盖顺序不变（`WPF_LINUX_TEXT_FONT_FAMILY` → SPI → 最后兜底常量）。
//
//  【为什么走 dlopen + TryGetExport 而不是 [DllImport]】
//    本程序集在**两个环境**里跑：① 真应用（AOT 镜像，.so 目录由桥注入）；
//    ② 测试进程（JIT，shim 在测试输出目录）。`AppContext.BaseDirectory` 在 AOT 镜像里
//    指向的是 dotnet 根目录（这个坑本项目已经踩过两次：libSkiaSharp、libwpfwic）。
//    所以按 `MilExternalHandleBridge` 的同一套"候选路径 + TryLoad + TryGetExport"来取，
//    **取不到就退回常量**（fail-safe：绝不因为拿不到字族而让渲染器起不来）。
//
//  【它不是什么】这不是"面由 run 决定"（那是 P0 的 C 步，跨车道，本轮不做）。
//    这只是让"渲染器请求的族"与"WPF 实际用的族"**对齐到同一个来源**。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfGfx.Linux.Interop
{
    /// <summary>从 Win32 shim 的 `SystemParametersInfo(SPI_GETNONCLIENTMETRICS)` 取 UI 字族。</summary>
    internal static class Win32UiFont
    {
        /// <summary>`SPI_GETNONCLIENTMETRICS`（`win32_internal.h:204` = 41）。</summary>
        private const uint SPI_GETNONCLIENTMETRICS = 41;

        /// <summary>`WPF_NONCLIENTMETRICS` 的字节数（`win32_abi.h:284` = 500）。</summary>
        private const int NonClientMetricsSize = 500;

        /// <summary>`lfMessageFont` 在 `WPF_NONCLIENTMETRICS` 里的偏移（`win32_abi.h:283` = 408）。</summary>
        private const int MessageFontOffset = 408;

        /// <summary>`lfFaceName` 在 `WPF_LOGFONT` 里的偏移（`win32_abi.h` = 28）。</summary>
        private const int FaceNameOffsetInLogFont = 28;

        /// <summary>`lfFaceName` 的 UTF-16 单元数（32，含结尾 0）。</summary>
        private const int FaceNameChars = 32;

        /// <summary>显式指定 shim 路径（与既有 `MILBRIDGE_WIC_SO` 同风格）。</summary>
        public const string ShimPathEnvVar = "WPF_LINUX_WIN32_SHIM";

        private const string ShimFileName = "libwpfwin32.so";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SystemParametersInfoWFn(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        private static readonly object Gate = new object();
        private static bool _probed;
        private static SystemParametersInfoWFn _spi;

        /// <summary>最近一次取到的族名（null = 还没成功取过）；诊断用。</summary>
        public static string LastFamily { get; private set; }

        /// <summary>最近一次的状态说明（诊断用）。</summary>
        public static string LastDetail { get; private set; } = "尚未探测";

        /// <summary>
        /// 取 WPF 正在用的 UI 字族（= `SystemParametersInfo(SPI_GETNONCLIENTMETRICS).lfMessageFont.lfFaceName`）。
        /// 取不到返回 null（调用方自己兜底）。
        /// </summary>
        public static string TryGetMessageFontFamily()
        {
            EnsureProbed();
            SystemParametersInfoWFn spi = _spi;
            if (spi == null)
            {
                LastDetail = "跳过：找不到 SystemParametersInfoW 导出";
                return null;
            }

            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(NonClientMetricsSize);
                for (int i = 0; i < NonClientMetricsSize; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, NonClientMetricsSize);   // cbSize

                int ok = spi(SPI_GETNONCLIENTMETRICS, NonClientMetricsSize, buffer, 0);
                if (ok == 0)
                {
                    LastDetail = "跳过：SystemParametersInfoW 返回 0";
                    return null;
                }

                string family = Marshal.PtrToStringUni(
                    IntPtr.Add(buffer, MessageFontOffset + FaceNameOffsetInLogFont), FaceNameChars);
                if (string.IsNullOrEmpty(family))
                {
                    LastDetail = "跳过：lfMessageFont.lfFaceName 为空";
                    return null;
                }

                family = family.TrimEnd('\0').Trim();
                if (family.Length == 0)
                {
                    LastDetail = "跳过：lfMessageFont.lfFaceName 只有空白";
                    return null;
                }

                LastFamily = family;
                LastDetail = $"命中：lfMessageFont.lfFaceName = {family}";
                return family;
            }
            catch (Exception ex)
            {
                LastDetail = $"跳过：{ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static void EnsureProbed()
        {
            if (_probed) return;
            lock (Gate)
            {
                if (_probed) return;
                _probed = true;
                foreach (string candidate in CandidatePaths())
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    try
                    {
                        if (!NativeLibrary.TryLoad(candidate, out IntPtr lib) || lib == IntPtr.Zero) continue;
                        if (!NativeLibrary.TryGetExport(lib, "SystemParametersInfoW", out IntPtr fn) || fn == IntPtr.Zero)
                            continue;
                        _spi = Marshal.GetDelegateForFunctionPointer<SystemParametersInfoWFn>(fn);
                        return;
                    }
                    catch
                    {
                        // 换下一个候选；全失败就保持"取不到"
                    }
                }
            }
        }

        /// <summary>测试用：允许重新探测。</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _probed = false;
                _spi = null;
                LastFamily = null;
                LastDetail = "尚未探测（已重置）";
            }
        }

        private static string[] CandidatePaths()
        {
            var list = new System.Collections.Generic.List<string>();
            string explicitPath = Environment.GetEnvironmentVariable(ShimPathEnvVar);
            if (!string.IsNullOrEmpty(explicitPath))
            {
                list.Add(explicitPath);
                if (!explicitPath.EndsWith(ShimFileName, StringComparison.Ordinal))
                    list.Add(System.IO.Path.Combine(explicitPath, ShimFileName));
            }

            // 桥注入的 .so 目录（AOT 镜像里唯一可靠的"自己的目录"来源）
            string searchDir = MilExternalHandleBridge.SearchDirectory;
            if (!string.IsNullOrEmpty(searchDir)) list.Add(System.IO.Path.Combine(searchDir, ShimFileName));

            // 测试进程 / 普通 JIT 宿主：应用目录（AOT 镜像里它是 dotnet 根目录，所以只当兜底）
            // ⚠️ 刻意**不用** `Assembly.Location`：AOT/单文件下它恒为空串，裁剪分析器会报 IL3000
            //    （实测：只警告，但"0 错 0 警"是这个工程的标准，所以直接不给它机会）。
            list.Add(System.IO.Path.Combine(AppContext.BaseDirectory ?? ".", ShimFileName));
            list.Add(ShimFileName);
            return list.ToArray();
        }
    }
}
