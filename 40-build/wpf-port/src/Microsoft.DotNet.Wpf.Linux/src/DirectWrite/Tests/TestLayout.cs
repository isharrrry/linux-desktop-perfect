// T2 · Phase 1 —— 测试用的仓库内固定路径
// =====================================================================================
// 【为什么再写一份路径解析】
//   与 src/Linux/tests/.../Windowing.Tests/TestLayout.cs 和 Rendering.Tests/RepoLayout.cs 是
//   **刻意重复**：那两个是其它组的测试工程内的 internal 类型，我们读不到也不该去改
//   （handoff §7 目录边界）。重复 30 行路径解析，代价远小于越界。
//
// 【为什么字体目录必须来自 src/Linux/build/fonts】
//   本工程的每一条断言都建立在"同一份字体文件"之上。一旦允许系统字体参与，
//   /usr/share/fonts 的有无、版本、hinting 配置都会改变度量与字形 id —— 断言会
//   变成随机飘的红/绿。所以这里做的第一件事就是校验 SHA256SUMS。

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    internal static class TestLayout
    {
        private static readonly Lazy<string> Root = new Lazy<string>(FindRoot);

        /// <summary>仓库根：向上找到第一个含 handoff.md 的目录。</summary>
        public static string RootPath => Root.Value;

        /// <summary>本目录：src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Tests。</summary>
        public static string TestsDir => Path.Combine(RootPath, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "Tests");

        /// <summary>打包字体目录 src/Linux/build/fonts（**正好 4 个基准字体**，不许再加文件）。</summary>
        public static string FontDir => Path.Combine(RootPath, "src", "Linux", "build", "fonts");

        /// <summary>
        /// 派生 UI 字体（剥离 GSUB/GPOS）所在的**独立目录**。
        /// 为什么不放 src/Linux/build/fonts：派生件与基准件同族同字重，会被 FontSet 的
        /// (族名,字重,斜体) 索引互相遮蔽（先加载者被后加载者覆盖）⇒ 基准解析结果被改。
        /// </summary>
        public static string DerivedUiFontDir => Path.Combine(RootPath, "src", "Linux", "build", "fonts-ui");

        /// <summary>派生 UI 字体的完整路径（由 src/Linux/build/gen-ui-font.py 生成）。</summary>
        public static string DerivedUiFontPath => Path.Combine(DerivedUiFontDir, "UI-NoLayout.ttf");

        /// <summary>探针程序（Tests 把它当子进程拉起）。</summary>
        public static string ProbeAssembly => Path.Combine(
            RootPath, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "Probe", "bin", "Debug", "DirectWrite.Linux.Probe.dll");

        /// <summary>产物目录（摘要、报告证据落盘）。</summary>
        public static string ArtifactDir => Path.Combine(RootPath, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "artifacts");

        /// <summary>M1 Text/ 目录（**只读**，由 csproj 以 Compile Link 方式编进本程序集）。</summary>
        public static string M1TextDir => Path.Combine(RootPath, "src", "WpfGfx.Linux", "Text");

        /// <summary>M1 被链接的 4 个源文件（用于"我们验证的是哪一版"的取证）。</summary>
        public static readonly string[] M1LinkedFiles =
        {
            "TextFontDescription.cs", "GlyphRunRequest.cs", "GlyphRunLayout.cs", "FontSet.cs",
        };

        public const string RegularFile = "NotoSans-Regular.ttf";
        public const string BoldFile = "NotoSans-Bold.ttf";
        public const string ItalicFile = "NotoSans-Italic.ttf";
        public const string BoldItalicFile = "NotoSans-BoldItalic.ttf";

        public static string FontPath(string fileName) => Path.Combine(FontDir, fileName);

        public static void EnsureArtifactDir() => Directory.CreateDirectory(ArtifactDir);

        public static string Sha256File(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>校验 src/Linux/build/fonts 下全部字体与 SHA256SUMS 一致；返回不一致清单。</summary>
        public static List<string> VerifyFontChecksums()
        {
            var mismatches = new List<string>();
            string sums = Path.Combine(FontDir, "SHA256SUMS");
            if (!File.Exists(sums))
            {
                mismatches.Add("缺少 SHA256SUMS：" + sums);
                return mismatches;
            }

            foreach (string rawLine in File.ReadAllLines(sums))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                string[] parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;

                string expected = parts[0].Trim().ToLowerInvariant();
                string name = parts[1].Trim();
                string path = Path.Combine(FontDir, name);
                if (!File.Exists(path))
                {
                    mismatches.Add(name + ": 文件不存在");
                    continue;
                }

                string actual = Sha256File(path);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"{name}: 期望 {expected} 实际 {actual}");
            }

            return mismatches;
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "handoff.md"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"无法从 {AppContext.BaseDirectory} 向上定位仓库根（未找到 handoff.md）");
        }
    }
}
