// T1 · M7c5（路线 C）—— 验收指标 ② 的**真实 PC 代码路径**取证。
// =====================================================================================
// 【为什么必须另起一个测试类】
//   `FontLayoutStrippingTests` 测的是 **provider 侧**（加载路径 + 掩码估算）。
//   但指标 ② 要的是"经**真实 PC 代码路径**测得的 TypographyAvailabilities = 0，
//   且 CheckFastPathNominalGlyphs = True" —— 那要跑 PC 自己的
//   `MS.Internal.FontCache.FontFaceLayoutInfo` 与 `System.Windows.Media.Typeface`。
//
//   本工程**不重建 PresentationCore**（主控要求，M7b 在用），所以这里复用 T2 既有的
//   独立路子：把 `WiringSmoke` 当子进程拉起，它在真实 PC 装配里跑，并打印
//   `TYPOGRAPHY_MASK=` / `FASTPATH_CHECK=`。
//
// 【为什么两个语料都要测】
//   Noto Sans（掩码 21）与 DejaVu Sans（掩码 23）是**不同**的闸门 2 位组合，
//   只测一个不足以证明"任何字体都能过"。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    public class StripLayoutSmokeTests
    {
        private readonly ITestOutputHelper _output;

        public StripLayoutSmokeTests(ITestOutputHelper output) => _output = output;

        private static string SmokeAssembly => Path.Combine(
            TestLayout.RootPath, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "WiringSmoke", "bin", "Debug",
            "DirectWrite.Linux.WiringSmoke.dll");

        private static readonly (string Label, string Dir, string Family, int RawMask)[] Corpora =
        {
            ("Noto Sans", TestLayout.FontDir, "Noto Sans", 21),
            ("DejaVu Sans", "/usr/share/fonts/truetype/dejavu", "DejaVu Sans", 23),
        };

        [Theory]
        [InlineData("Noto Sans")]
        [InlineData("DejaVu Sans")]
        public void RuntimeStrip_TurnsMaskToZero_AndFastPathToTrue_ThroughRealPc(string label)
        {
            (string _, string dir, string family, int rawMask) = Find(label);
            if (!Directory.Exists(dir)) { _output.WriteLine("语料目录不存在，跳过：" + dir); return; }

            // ---- 关（旁路）：PC 算出来必须是原始掩码，且快路径被拒 ----
            Dictionary<string, string> off = RunSmoke(dir, family, stripLayout: "0");
            _output.WriteLine($"[{label}] strip=0  TYPOGRAPHY_MASK={off["TYPOGRAPHY_MASK"]}  " +
                              $"FASTPATH_CHECK={off["FASTPATH_CHECK"]}  " +
                              $"STRIP_LAST={off.GetValueOrDefault("STRIP_LAYOUT_LAST_REASON")}");
            Assert.Equal(rawMask.ToString(), off["TYPOGRAPHY_MASK"]);
            Assert.StartsWith("False", off["FASTPATH_CHECK"]);
            Assert.Equal("disabled", off["STRIP_LAYOUT_LAST_REASON"]);

            // ---- 开（默认）：掩码 = 0，快路径放行 ----
            Dictionary<string, string> on = RunSmoke(dir, family, stripLayout: "1");
            _output.WriteLine($"[{label}] strip=1  TYPOGRAPHY_MASK={on["TYPOGRAPHY_MASK"]}  " +
                              $"FASTPATH_CHECK={on["FASTPATH_CHECK"]}  " +
                              $"STRIP_LAST={on.GetValueOrDefault("STRIP_LAYOUT_LAST_REASON")}  " +
                              $"stripped={on.GetValueOrDefault("STRIP_LAYOUT_STRIPPED")}");
            Assert.Equal("0", on["TYPOGRAPHY_MASK"]);
            Assert.StartsWith("True", on["FASTPATH_CHECK"]);
            Assert.StartsWith("stripped", on["STRIP_LAYOUT_LAST_REASON"]);
            Assert.True(int.Parse(on["STRIP_LAYOUT_STRIPPED"]) > 0, "必须真的剥过至少一个面");

            // ---- 关键：走的确实是 PC 自己的判定，而不是我们的估算 ----
            Assert.Equal(family, on["FASTPATH_FAMILY"]);
            Assert.Contains("stringLengthFit=", on["FASTPATH_CHECK"]);
        }

        [Fact]
        public void DefaultMode_Strips_WithoutAnyEnvVar()
        {
            // "默认开"这条承诺必须由**不设任何环境变量**的一次运行来证。
            (string _, string dir, string family, int _) = Find("Noto Sans");
            if (!Directory.Exists(dir)) return;

            Dictionary<string, string> dflt = RunSmoke(dir, family, stripLayout: null);
            _output.WriteLine($"默认（env 未设）: MASK={dflt["TYPOGRAPHY_MASK"]} " +
                              $"FASTPATH={dflt["FASTPATH_CHECK"]} env={dflt["STRIP_LAYOUT_ENV"]}");
            Assert.Equal("<unset>", dflt["STRIP_LAYOUT_ENV"]);
            Assert.Equal("True", dflt["STRIP_LAYOUT_DEFAULT_ENABLED"]);
            Assert.Equal("0", dflt["TYPOGRAPHY_MASK"]);
            Assert.StartsWith("True", dflt["FASTPATH_CHECK"]);
        }

        // =================================================================================

        private static (string Label, string Dir, string Family, int RawMask) Find(string label)
        {
            foreach (var c in Corpora)
                if (c.Label == label) return c;
            throw new ArgumentOutOfRangeException(nameof(label), label);
        }

        /// <summary>拉起 WiringSmoke 子进程；<paramref name="stripLayout"/> 为 null 表示**不设**该环境变量。</summary>
        private Dictionary<string, string> RunSmoke(string fontDir, string family, string stripLayout)
        {
            Assert.True(File.Exists(SmokeAssembly),
                $"接线冒烟程序未构建：{SmokeAssembly}（先 dotnet build src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WiringSmoke）");

            var psi = new ProcessStartInfo
            {
                FileName = FindDotnet(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(SmokeAssembly),
            };
            psi.ArgumentList.Add(SmokeAssembly);
            psi.ArgumentList.Add("--font-dir"); psi.ArgumentList.Add(fontDir);
            psi.ArgumentList.Add("--family"); psi.ArgumentList.Add(family);
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["WPF_LINUX_FONT_DIR"] = fontDir;
            if (stripLayout != null) psi.Environment["WPF_LINUX_STRIP_LAYOUT"] = stripLayout;

            using Process process = Process.Start(psi);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0,
                $"WiringSmoke 失败（exit={process.ExitCode}）：\n{stdout}\n{stderr}");

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in stdout.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return map;
        }

        private static string FindDotnet()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string candidate = Path.Combine(home, ".dotnet", "dotnet");
            return File.Exists(candidate) ? candidate : "dotnet";
        }
    }
}
