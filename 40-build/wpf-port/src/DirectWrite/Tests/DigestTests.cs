// T2 · Phase 1 —— 确定性断言（同进程两次 + **跨进程**两次）
// =====================================================================================
// 【两级确定性，缺一不可】
//   同进程：同一段代码跑两遍 → 排除"进程内可变状态污染"（静态缓存、惰性初始化顺序）。
//   跨进程：把探针当子进程拉起两次 → 排除 .NET **每进程随机化的字符串哈希**
//           导致的字典枚举顺序差异、以及任何依赖启动顺序/环境的值。
//   实测：跨进程这条是能真抓到东西的 —— 摘要文本里一旦混进 Dictionary 的枚举顺序，
//   同进程两次可能仍然一致（同一个哈希种子），而两个进程立刻不一致。
//
// 【为什么断言的是 SHA-256 而不是文本相等】
//   摘要 299 行，直接比会打印一屏。哈希不等时再把两边文本落盘 diff（这里也这么做了：
//   失败信息里给出两端摘要的落盘路径）。
//
// 【反向断言不可少】
//   "两次都是同一个哈希"如果哈希函数是常量就永远成立。所以还有一条
//   Digest_IsSensitiveToInput：换一个输入必须换一个哈希。

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MS.Internal.Text.TextInterface.Linux.Probe;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class DigestTests
    {
        private readonly ITestOutputHelper _output;

        public DigestTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Digest_SameProcessTwice_IsIdentical()
        {
            string first = ProbeDigest.Digest(TestLayout.FontDir);
            string second = ProbeDigest.Digest(TestLayout.FontDir);

            _output.WriteLine("同进程 digest = " + first);
            Assert.Equal(first, second);
            Assert.Equal(64, first.Length);
        }

        [Fact]
        public void Digest_IsSensitiveToInput()
        {
            // 反例保护：哈希函数不能是"什么都返回同一个值"。
            // 换字体目录（空目录 → 空集合）→ 摘要必须不同。
            string empty = Path.Combine(Path.GetTempPath(), "dw-empty-fonts-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(empty);
            try
            {
                string baseline = ProbeDigest.Digest(TestLayout.FontDir);
                string different = ProbeDigest.Digest(empty);
                Assert.NotEqual(baseline, different);
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }

        [Fact]
        public void Digest_AcrossTwoProcesses_IsIdentical()
        {
            string digestA = RunProbeAndGetDigest(out string stdoutA, out int exitA);
            string digestB = RunProbeAndGetDigest(out string stdoutB, out int exitB);

            _output.WriteLine("进程 1 digest = " + digestA);
            _output.WriteLine("进程 2 digest = " + digestB);

            if (digestA != digestB)
            {
                TestLayout.EnsureArtifactDir();
                string pathA = Path.Combine(TestLayout.ArtifactDir, "probe-stdout-1.txt");
                string pathB = Path.Combine(TestLayout.ArtifactDir, "probe-stdout-2.txt");
                File.WriteAllText(pathA, stdoutA);
                File.WriteAllText(pathB, stdoutB);

                Assert.Fail($"跨进程摘要不一致。\n进程 1: {digestA}\n进程 2: {digestB}\n" +
                            $"两次 stdout 已落盘：{pathA} / {pathB}（可 diff 出第一处差异）");
            }

            Assert.Equal(0, exitA);
            Assert.Equal(0, exitB);

            // 同一个 digest 也要和进程内算出来的一致（证明测试编的那份 ProbeDigest
            // 与探针进程跑的是同一段计算）。
            Assert.Equal(ProbeDigest.Digest(TestLayout.FontDir), digestA);

            TestLayout.EnsureArtifactDir();
            File.WriteAllText(Path.Combine(TestLayout.ArtifactDir, "probe-digest.txt"),
                $"in-process: {ProbeDigest.Digest(TestLayout.FontDir)}\nprocess-1:  {digestA}\nprocess-2:  {digestB}\n");
        }

        [Fact]
        public void Probe_ReportsItsOwnFontDirectoryAndLineCount()
        {
            RunProbeAndGetDigest(out string stdout, out int exit);

            Assert.Equal(0, exit);
            Assert.Contains("FONTDIR fonts", stdout);

            Match lines = Regex.Match(stdout, @"LINES (\d+)");
            Assert.True(lines.Success, "探针必须报出摘要行数");

            int lineCount = int.Parse(lines.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(lineCount >= 290, $"摘要只有 {lineCount} 行，覆盖的通路可能被删了");
            _output.WriteLine("摘要行数 = " + lineCount);
        }

        // ---------------------------------------------------------------------------------

        private static string RunProbeAndGetDigest(out string stdout, out int exitCode)
        {
            string dotnet = FindDotnet();
            string probe = TestLayout.ProbeAssembly;

            Assert.True(File.Exists(probe),
                $"探针未构建：{probe}（先跑 dotnet build src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Probe）");

            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(probe),
            };
            psi.ArgumentList.Add(probe);
            psi.ArgumentList.Add("--font-dir");
            psi.ArgumentList.Add(TestLayout.FontDir);

            // 环境变量的顺序/内容不参与摘要，但这里显式固定 LC_ALL，避免文化相关输出漂移。
            psi.Environment["LC_ALL"] = "C";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            using Process process = Process.Start(psi);
            string outText = process.StandardOutput.ReadToEnd();
            string errText = process.StandardError.ReadToEnd();
            process.WaitForExit();

            stdout = outText + errText;
            exitCode = process.ExitCode;

            Match match = Regex.Match(outText, @"DIGEST ([0-9a-f]{64})");
            Assert.True(match.Success, "探针输出里没有 DIGEST 行：\n" + stdout);
            return match.Groups[1].Value;
        }

        /// <summary>找到 dotnet 宿主：优先与环境一致的那一个（测试与探针必须同一套运行时）。</summary>
        private static string FindDotnet()
        {
            string processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) &&
                Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root))
            {
                string candidate = Path.Combine(root, "dotnet");
                if (File.Exists(candidate)) return candidate;
            }

            string home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                string candidate = Path.Combine(home, ".dotnet", "dotnet");
                if (File.Exists(candidate)) return candidate;
            }

            // 最后交给 PATH
            return "dotnet";
        }
    }
}
