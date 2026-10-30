// T2 · Phase 1 —— 探针入口（薄壳：只做参数解析与输出）
// =====================================================================================
// 全部摘要逻辑在 ProbeDigest.cs（**同一个源文件**也被 Tests 编一份，见 Tests.csproj），
// 这里只负责：
//   · 解析参数（-font-dir / -out / -json）
//   · 定位打包字体目录（找不到就抛，绝不回落系统字体）
//   · 按固定格式打印结果（Tests 用正则抓 `DIGEST <hex>`）

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MS.Internal.Text.TextInterface.Linux.Probe
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string fontDir = null;
            string outPath = null;
            bool json = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--font-dir": fontDir = Next(args, ref i); break;
                    case "--out": outPath = Next(args, ref i); break;
                    case "--json": json = true; break;
                    case "--help":
                        Console.WriteLine("用法: DirectWrite.Linux.Probe [--font-dir <dir>] [--out <file>] [--json]");
                        return 0;
                    default:
                        Console.Error.WriteLine("未知参数: " + args[i]);
                        return 2;
                }
            }

            try
            {
                fontDir ??= ResolveFontDirectory();

                var facts = new List<string>();
                string digestText = ProbeDigest.Build(fontDir, facts);
                string digest = ProbeDigest.Sha256(digestText);

                if (outPath != null)
                    File.WriteAllText(outPath, digestText + "\nDIGEST " + digest + "\n");

                if (json)
                    foreach (string fact in facts)
                        Console.WriteLine(fact);

                // Tests 用这一行抓结果：格式固定，不要改。
                Console.WriteLine("FONTDIR " + Path.GetFileName(fontDir.TrimEnd('/')));
                Console.WriteLine("LINES " + digestText.Split('\n').Length);
                Console.WriteLine("DIGEST " + digest);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("探针失败: " + ex);
                return 1;
            }
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("参数 " + args[i] + " 缺少值");
            return args[++i];
        }

        /// <summary>
        /// 定位打包字体目录：从可执行文件所在目录向上找 src/Linux/build/fonts/SHA256SUMS。
        /// 找不到就抛（不静默用系统字体）。
        /// </summary>
        private static string ResolveFontDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (dir.Name == "fonts" && File.Exists(Path.Combine(dir.FullName, "SHA256SUMS")))
                    return dir.FullName;

                string candidate = Path.Combine(dir.FullName, "src", "Linux", "build", "fonts");
                if (File.Exists(Path.Combine(candidate, "SHA256SUMS"))) return candidate;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "找不到 src/Linux/build/fonts/SHA256SUMS（从 " + AppContext.BaseDirectory + " 向上）");
        }
    }
}
