// T2 · Phase 2 —— SystemFonts 端到端探针
// =====================================================================================
// 【验的是什么】
//   `SystemFonts.MessageFontFamily`（PresentationFramework）→
//   `new FontFamily(SystemParameters.NonClientMetrics.lfMessageFont.lfFaceName)`。
//   这条链同时穿过三样东西：
//     ① M7b 的 `SystemParametersInfoW`（SPI_GETNONCLIENTMETRICS 出参）→ 提供 face name；
//     ② T2 的字体解析（补丁 G 的 Linux Factory + Provider）→ 把 face name 解析成字体族；
//     ③ 确定性：本探针用 WPF_LINUX_FONT_DIR 把系统字体目录锁到打包字体。
//   所以结论必须**分项**给出：谁通了、谁没通、异常原文是什么。
//
// 【为什么每项都单独 try】
//   一个属性抛异常不该掩盖其它属性的结果 —— 报告里要能说"这几个能用、那个不行"。

using System;
using System.Globalization;
using System.IO;
using System.Runtime.Loader;

namespace MS.Internal.Text.TextInterface.Linux.SystemFontsProbe
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // ── 探针本地的依赖解析兜底 ────────────────────────────────────────────────
            // PC/PF 是通过 <Reference><HintPath> 引 WindowsBase 的，本探针的 deps.json
            // 里**没有** WindowsBase 条目（框架门面遮蔽导致引用被摘掉），
            // 于是运行期即使文件就在旁边也不会被探到。
            // 这只影响"诊断探针"的装配，不影响产品路径；这里显式给一个解析回退。
            // （部署教训已写进 REPORT.md：产品 app 必须让 deps.json 里带上 WindowsBase。）
            string baseDirectory = AppContext.BaseDirectory;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                if (!string.Equals(name.Name, "WindowsBase", StringComparison.OrdinalIgnoreCase)) return null;

                string candidate = Path.Combine(baseDirectory, "WindowsBase.dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };

            Console.WriteLine("FONT_DIR_ENV=" + (Environment.GetEnvironmentVariable("WPF_LINUX_FONT_DIR") ?? "<未设置>"));

            // 装配诊断：把关键程序集的**实际身份**打出来。
            // 运行期加载失败时，"请求的身份 vs 磁盘上的身份"是唯一能定位问题的一行证据。
            foreach (string name in new[] { "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll",
                                            "DirectWriteForwarder.dll", "DirectWrite.Linux.Provider.dll" })
            {
                string path = Path.Combine(baseDirectory, name);
                try
                {
                    Console.WriteLine($"ASSEMBLY {name} = {(File.Exists(path) ? System.Reflection.AssemblyName.GetAssemblyName(path).FullName : "<文件不存在>")}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"ASSEMBLY {name} = <读取失败 {e.GetType().Name}>");
                }
            }

            Probe("MessageFontFamily", () => System.Windows.SystemFonts.MessageFontFamily?.Source ?? "<null>");
            Probe("MessageFontWeight", () => System.Windows.SystemFonts.MessageFontWeight.ToString());
            Probe("MessageFontSize", () => System.Windows.SystemFonts.MessageFontSize.ToString(CultureInfo.InvariantCulture));
            Probe("MessageFontStyle", () => System.Windows.SystemFonts.MessageFontStyle.ToString());
            Probe("CaptionFontFamily", () => System.Windows.SystemFonts.CaptionFontFamily?.Source ?? "<null>");
            Probe("IconFontFamily", () => System.Windows.SystemFonts.IconFontFamily?.Source ?? "<null>");

            // 直接量一次：把解析出来的族拿去做一次真实的度量（这才叫"可用"）
            Probe("MessageFontFamily_Metrics", () =>
            {
                System.Windows.Media.FontFamily family = System.Windows.SystemFonts.MessageFontFamily;
                if (family == null) return "<null>";
                System.Windows.Media.Typeface typeface = new System.Windows.Media.Typeface(
                    family,
                    System.Windows.SystemFonts.MessageFontStyle,
                    System.Windows.SystemFonts.MessageFontWeight,
                    System.Windows.FontStretches.Normal);
                bool ok = typeface.TryGetGlyphTypeface(out System.Windows.Media.GlyphTypeface g);
                if (!ok || g == null) return "TryGetGlyphTypeface=False";

                return "TryGetGlyphTypeface=True"
                     + " faceNames=" + g.FaceNames.Count
                     + " baseline=" + g.Baseline.ToString(CultureInfo.InvariantCulture)
                     + " height=" + g.Height.ToString(CultureInfo.InvariantCulture)
                     + " glyphs=" + g.GlyphCount;
            });

            // ── SPI 直探（绕开 WindowsBase 装配问题）────────────────────────────────
            // SystemFonts.MessageFontFamily = new FontFamily(NonClientMetrics.lfMessageFont.lfFaceName)，
            // 所以"SPI 给不给 face name"是这条链的第一个环节。这里直接 P/Invoke
            // M7b 的 libwpfwin32.so，把 NONCLIENTMETRICSW 里的 lfMessageFont.lfFaceName 读出来
            // —— 这样即使托管装配没打通，也能把"字体侧 / SPI 侧"两段分别定性。
            ProbeSpi();

            Console.WriteLine("PROBE_DONE=True");
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("libwpfwin32.so", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                                                 EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint action, uint param, IntPtr data, uint winIni);

        /// <summary>SPI_GETNONCLIENTMETRICS = 0x0029；NONCLIENTMETRICSW 的 cbSize = 504。</summary>
        private const uint SPI_GETNONCLIENTMETRICS = 0x0029;
        private const int NonClientMetricsSize = 504;
        private const int LfMessageFontOffset = 408;      // 见下方注释的逐字段偏移
        private const int LfFaceNameOffsetInLogFont = 28; // LOGFONTW 的 lfFaceName 偏移

        private static void ProbeSpi()
        {
            // NONCLIENTMETRICSW 布局（Win32）：
            //   0 cbSize | 4 iBorderWidth | 8 iScrollWidth | 12 iScrollHeight |
            //   16 iCaptionWidth | 20 iCaptionHeight | 24 lfCaptionFont(92) |
            //   116 iSmCaptionWidth | 120 iSmCaptionHeight | 124 lfSmCaptionFont(92) |
            //   216 iMenuWidth | 220 iMenuHeight | 224 lfMenuFont(92) |
            //   316 lfStatusFont(92) | 408 lfMessageFont(92) | 500 iPaddedBorderWidth → 504
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(NonClientMetricsSize);
            try
            {
                for (int i = 0; i < NonClientMetricsSize; i++) System.Runtime.InteropServices.Marshal.WriteByte(buffer, i, 0);
                System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 0, NonClientMetricsSize);   // cbSize

                bool ok = SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, NonClientMetricsSize, buffer, 0);
                Console.WriteLine("SPI_CALL_OK=" + ok);

                if (ok)
                {
                    string faceName = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                        IntPtr.Add(buffer, LfMessageFontOffset + LfFaceNameOffsetInLogFont), 32);
                    int weight = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, LfMessageFontOffset + 16);
                    byte charset = System.Runtime.InteropServices.Marshal.ReadByte(buffer, LfMessageFontOffset + 23);

                    Console.WriteLine("SPI_LFMESSAGEFONT_FACENAME=" + (faceName ?? "<null>"));
                    Console.WriteLine("SPI_LFMESSAGEFONT_WEIGHT=" + weight);
                    Console.WriteLine("SPI_LFMESSAGEFONT_CHARSET=" + charset);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("SPI_CALL_OK=FAIL " + e.GetType().Name + ": " + Flatten(e.Message));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Probe(string name, Func<string> body)
        {
            try
            {
                string value = body();
                Console.WriteLine($"{name}=OK {value}");
            }
            catch (Exception e)
            {
                Exception inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : e;
                Console.WriteLine($"{name}=FAIL {inner.GetType().Name}: {Flatten(inner.Message)}");
            }
        }

        private static string Flatten(string message) =>
            message == null ? "" : message.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
