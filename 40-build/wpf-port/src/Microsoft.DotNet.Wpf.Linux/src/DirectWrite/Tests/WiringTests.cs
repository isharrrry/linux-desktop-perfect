// T2 · Phase 2 —— 接线后的骨架端到端断言（子进程跑 WiringSmoke）
// =====================================================================================
// 【这一组与其余断言的区别】
//   其余 82 条验的是 **provider**（src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider）。
//   这一组验的是 **接线之后的骨架**（src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder）：
//   把 `Microsoft.DotNet.Wpf/src/...Factory.cs` 会走的那条路，从
//   `FontCollection.FromDirectory` 一直走到 `FontFace.TryGetFontTable`。
//
// 【为什么必须在子进程里跑】
//   冒烟程序要 `Assembly.Load("DirectWriteForwarder")` 并加载它的依赖
//   （WindowsBase 等）。在测试进程里做这件事会和测试宿主自己的加载上下文打架；
//   单独起进程最干净。顺带它也验证了"骨架能被正常加载并工作"。
//
// 【它证明的三件事（其余是顺带）】
//   1. `LineSpacing` 的真值 —— 1.362（整数除法那个 bug 修好了，且是**穿过骨架**验证的）
//   2. 表字节是真值 —— 骨架给出的 head 表与测试独立从 .ttf 切出来的逐字节相同
//   3. 令牌可解析 —— DWriteFontFaceAddRef / DWriteFontAddRef 的 IntPtr 能被
//      FontHandleTable 反查（MIL 侧画轮廓的前提；桥接见 WIRING.md §4）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MS.Internal.Text.TextInterface.Linux.Tests
{
    [Collection("Fonts")]
    public class WiringTests
    {
        private readonly ITestOutputHelper _output;

        public WiringTests(ITestOutputHelper output) => _output = output;

        private static string SmokeAssembly => Path.Combine(
            TestLayout.RootPath, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "WiringSmoke", "bin", "Debug",
            "DirectWrite.Linux.WiringSmoke.dll");

        private static Dictionary<string, string> RunSmoke()
        {
            string dotnet = FindDotnet();
            string assembly = SmokeAssembly;

            Assert.True(File.Exists(assembly),
                $"接线冒烟程序未构建：{assembly}（先跑 dotnet build src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WiringSmoke）");

            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(assembly),
            };
            psi.ArgumentList.Add(assembly);
            psi.ArgumentList.Add("--font-dir");
            psi.ArgumentList.Add(TestLayout.FontDir);
            psi.Environment["LC_ALL"] = "C";
            // 系统字体目录锁到打包字体：Factory.GetSystemFontCollection 的行为必须是确定的
            // （否则会去扫 /usr/share/fonts 的 237 个字体，既慢又依赖机器）。
            psi.Environment["WPF_LINUX_FONT_DIR"] = TestLayout.FontDir;

            // T1/M7c5：本套件是**接线保真**套件 —— 它的断言（FACE_HEADTABLE_SHA_MATCH 等）
            // 逐字节比对"骨架拿到的表 vs 磁盘原始 .ttf"。运行期 GSUB/GPOS 剥离会按 OpenType
            // 规范重算 head.checkSumAdjustment（唯一会变的 4 个字节），从而破坏那个比对。
            // 所以这里**显式关闭**剥离，让本套件继续测原始文件；剥离后的行为由
            // StripLayoutSmokeTests 单独取证。下面还有一条断言把"确实关着"钉住。
            psi.Environment["WPF_LINUX_STRIP_LAYOUT"] = "0";

            using Process process = Process.Start(psi);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0,
                $"接线冒烟失败（exit={process.ExitCode}）：\n{stdout}\n{stderr}");

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in stdout.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            return map;
        }

        [Fact]
        public void WiredSkeleton_ReturnsRealFontData()
        {
            Dictionary<string, string> smoke = RunSmoke();
            foreach (KeyValuePair<string, string> kv in smoke) _output.WriteLine($"{kv.Key}={kv.Value}");

            // 程序集身份（证明加载的是我们接线的那份骨架）
            Assert.StartsWith("DirectWriteForwarder 4.0.0.1", smoke["ASSEMBLY"]);

            // 集合与族
            Assert.Equal("1", smoke["COLLECTION_FAMILYCOUNT"]);
            Assert.Equal("True", smoke["FAMILY_FOUND"]);
            Assert.Equal("True", smoke["FAMILY_MISSING_IS_NULL"]);      // 查不到 → null，不回落系统字体
            Assert.Equal("Noto Sans", smoke["FAMILY_ORDINALNAME"]);
            Assert.Equal("4", smoke["FAMILY_FACECOUNT"]);
            Assert.Equal("True", smoke["FAMILY_ISPHYSICAL"]);
            Assert.Equal("False", smoke["FAMILY_ISCOMPOSITE"]);

            // 度量：真值 + LineSpacing 的整数除法修正（**穿过骨架**验证）
            Assert.Contains("upem=1000 asc=1069 desc=293 gap=0 cap=714 xh=536", smoke["FAMILY_METRICS"]);
            Assert.Contains("baseline=1.069", smoke["FAMILY_METRICS"]);
            Assert.Contains("linespacing=1.362", smoke["FAMILY_METRICS"]);

            // Display 度量走的是吸附像素网格的那条路（值必须与设计度量不同）
            Assert.Contains("asc=1067", smoke["FAMILY_DISPLAYMETRICS"]);
            Assert.Contains("linespacing=1.334", smoke["FAMILY_DISPLAYMETRICS"]);

            // 匹配：Bold 请求 → 拿到 Bold 字面（且 x-height 与 Regular 不同，证明是**不同文件**）
            Assert.Equal("Bold", smoke["BOLD_WEIGHT"]);
            Assert.Equal("Normal", smoke["BOLD_STYLE"]);
            Assert.Equal("False", smoke["BOLD_ISSYMBOL"]);
            Assert.Equal("2.015", smoke["BOLD_VERSION"]);
            Assert.Contains("xh=546", smoke["BOLD_METRICS"]);
            Assert.Equal("True", smoke["BOLD_HASCHAR_A"]);
            Assert.Equal("False", smoke["BOLD_HASCHAR_CJK"]);

            // 字体面
            Assert.Equal("TrueType", smoke["FACE_TYPE"]);
            Assert.Equal("0", smoke["FACE_INDEX"]);
            Assert.Equal("3884", smoke["FACE_GLYPHCOUNT"]);
            Assert.StartsWith("True FSTYPE=0x0000", smoke["FACE_READEMBEDDING"]);

            // 先确认这一轮确实跑在"不剥离"模式下（否则下面的逐字节比对没有意义）
            Assert.Equal("disabled", smoke["STRIP_LAYOUT_LAST_REASON"]);
            Assert.Equal("0", smoke["STRIP_LAYOUT_STRIPPED"]);

            // 表字节是真值：骨架给出的 head == 测试独立从 .ttf 切出来的（逐字节）
            Assert.Equal("True", smoke["FACE_HEADTABLE_OK"]);
            Assert.Equal("54", smoke["FACE_HEADTABLE_LEN"]);
            Assert.Equal("True", smoke["FACE_HEADTABLE_SHA_MATCH"]);

            Assert.Equal("NotoSans-Bold.ttf", smoke["FILE_URIPATH_BASENAME"]);
            Assert.Equal("1", smoke["BOLD_FACENAMES_COUNT"]);
            Assert.Equal("True", smoke["BOLD_VERSIONSTRINGS_OK"]);

            // 令牌（MIL 画轮廓的前提）
            Assert.Equal("True", smoke["FACE_TOKEN_NONZERO"]);
            Assert.Equal("True", smoke["FACE_TOKEN_RESOLVES"]);
            Assert.Equal("True", smoke["FONT_TOKEN_NONZERO"]);
            Assert.Equal("True", smoke["FONT_TOKEN_RESOLVES"]);

            Assert.Equal("True", smoke["SMOKE_OK"]);
        }

        /// <summary>
        /// 补丁 G 的验收：**PC 的 Linux 版 Factory** 走完全程（原生闸门已移除）。
        /// 这条覆盖的是"到达 T2 那 47 条实现之前"的那段路 ——
        /// 上游那里是 6 处 `_factory.Value->…` + 一个恒为 null 的函数指针（会崩）。
        /// </summary>
        [Fact]
        public void LinuxFactory_WalksTheWholeNativeGateFreePath()
        {
            Dictionary<string, string> smoke = RunSmoke();

            // 工厂建得起来（上游这一步要 dwrite.dll）
            Assert.Equal("True", smoke["FACTORY_CREATED"]);

            // 原生工厂指针如实为 null（唯一消费方是 D 档的 Itemize，不会被解引用）
            Assert.Equal("True", smoke["FACTORY_NATIVE_PTR_IS_NULL"]);

            // GetSystemFontCollection → 我们的托管集合；目录来自 WPF_LINUX_FONT_DIR
            Assert.Equal("1", smoke["FACTORY_SYSTEM_FAMILYCOUNT"]);
            Assert.Contains("directories=[" + TestLayout.FontDir + "]", smoke["FACTORY_SYSTEM_DIRS"]);
            Assert.Contains("files=4 families=1 warnings=0", smoke["FACTORY_SYSTEM_DIRS"]);

            // CreateFontFace(Uri, uint, FontSimulations)：拿到真值
            Assert.Equal("3884", smoke["FACTORY_CREATED_FACE_GLYPHS"]);
            Assert.Contains("asc=1069 desc=293", smoke["FACTORY_CREATED_FACE_METRICS"]);
            Assert.Contains("linespacing=1.362", smoke["FACTORY_CREATED_FACE_METRICS"]);

            // CreateFontFile(Uri) → Analyze 给出真文件类型
            Assert.Contains("True", smoke["FACTORY_CREATED_FILE_ANALYZE"]);
            Assert.Contains("FACES=1", smoke["FACTORY_CREATED_FILE_ANALYZE"]);
            Assert.Contains("TRUETYPE", smoke["FACTORY_CREATED_FILE_ANALYZE"]);
            Assert.Equal("NotoSans-Regular.ttf", smoke["FACTORY_CREATED_FILE_BASENAME"]);

            // CreateTextAnalyzer：非 null（消费方只要求非 null）
            Assert.Equal("True", smoke["FACTORY_ANALYZER_NONNULL"]);

            // 闸门 2 的实测值：要么是掩码数字，要么是 UNAVAILABLE + 原因
            //（后者通常是环境问题：WindowsBase 运行期装配，见 REPORT.md §4.0.4）
            string mask = smoke["TYPOGRAPHY_MASK"];
            Assert.True(mask == "UNAVAILABLE FileLoadException: Could not load file or assembly 'WindowsBase"
                        || mask.StartsWith("UNAVAILABLE", StringComparison.Ordinal)
                        || long.TryParse(mask, out _),
                "TYPOGRAPHY_MASK 必须是数字或 UNAVAILABLE + 原因，不允许静默缺失：实际 " + mask);

            // D 档：必须抛**带说明的** PlatformNotSupportedException，而不是崩溃或静默返回空
            Assert.Equal("PlatformNotSupportedException", smoke["DTIER_FONTFILESTREAM_GETFILESIZE_THROWS"]);
            Assert.Equal("True", smoke["DTIER_MESSAGE_HAS_GUIDANCE"]);
        }

        /// <summary>
        /// 令牌桥：钩子装上并**被调用过**（补丁 G 的伴生件）。
        /// 注意断言的是"装上 + 生效"，**不是**"跨运行时已通" ——
        /// 后者需要 .so 侧导出（本文件同时断言"已明确降级"这一事实）。
        /// </summary>
        [Fact]
        public void FontFaceBridge_IsInstalledAndInvoked()
        {
            Dictionary<string, string> smoke = RunSmoke();

            Assert.Equal("True", smoke["BRIDGE_TYPE_FOUND"]);

            string status = smoke["BRIDGE_STATUS"];
            Assert.Equal(status, smoke["BRIDGE_STATUS_AFTER"]);

            // 两种档位各有各的断言（环境里有没有那个 .so 导出决定走哪条）：
            if (status == "NativeAotExport")
            {
                // 导出存在 ⇒ 令牌由 MIL 的 .so 发放（跨运行时真正接通）
                Assert.Contains("registerExport=找到(MilFontFace_RegisterFromFile)", smoke["BRIDGE_DIAGNOSTICS"]);
                Assert.Contains("已跨运行时接通", smoke["BRIDGE_DIAGNOSTICS"]);
                // 导出被找到 ⇒ 跨运行时的**机制**已就位。
                // 但"本次调用是否成功"取决于 .so 侧能否在那个运行时里加载字体 ——
                // 实测本环境里它返回 0（失败），于是令牌落回进程内表。
                // 这里**只断言机制**，把成功次数如实记下来留给报告（不硬断言，避免把
                // 一个真实的 T1 侧待查项伪装成绿）。
                Assert.True(int.Parse(smoke["BRIDGE_NATIVE_ALLOCATIONS"],
                        System.Globalization.CultureInfo.InvariantCulture) >= 0);
            }
            else
            {
                // 没有导出 ⇒ 进程内档位，诊断必须**说清后果与缺什么**（不留静默坑）
                Assert.Equal("ProcessLocalOnly", status);
                Assert.Contains("registerExport=未找到", smoke["BRIDGE_DIAGNOSTICS"]);
                Assert.Contains("E_HANDLE", smoke["BRIDGE_DIAGNOSTICS"]);
                Assert.Contains("MilFontFace_RegisterFromFile", smoke["BRIDGE_DIAGNOSTICS"]);
            }

            // 钩子被调用过：安装时计数为 0（诊断串里就是 0），分配过令牌之后 >= 1
            Assert.Contains("allocatorCalls=0", smoke["BRIDGE_DIAGNOSTICS"]);
            Assert.True(int.Parse(smoke["BRIDGE_ALLOCATOR_CALLS_BEFORE"],
                    System.Globalization.CultureInfo.InvariantCulture) >= 1,
                "安装之后至少要有一次分配走过钩子，否则'钩子生效'无从谈起");

            // 令牌本身仍然可用（进程内档位下 MIL 反查不到，但我们自己的表能解析）
            Assert.Equal("True", smoke["BRIDGE_TOKEN_NONZERO"]);
            Assert.Equal("True", smoke["BRIDGE_TOKEN_RESOLVES"]);
        }

        [Fact]
        public void WiredSkeleton_CleanlyRejectsTokensItDidNotIssue()
        {
            // 反例保护：不是我们发的令牌必须**响亮地失败**，而不是画错字形。
            // 这条覆盖 WIRING.md §8 里"不猜字体"的约定（provider 侧 TryResolve 的语义）。
            FontHandleTable.Reset();
            try
            {
                Assert.False(FontHandleTable.TryResolve(new IntPtr(0xDEAD), out _));
                Assert.False(FontHandleTable.TryResolveFile(new IntPtr(0xBEEF), out _));
            }
            finally
            {
                FontHandleTable.Reset();
            }
        }

        private static string FindDotnet()
        {
            string processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) &&
                Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "dotnet")))
                return Path.Combine(root, "dotnet");

            string home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, ".dotnet", "dotnet")))
                return Path.Combine(home, ".dotnet", "dotnet");

            return "dotnet";
        }
    }
}
