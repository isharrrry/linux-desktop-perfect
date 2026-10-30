// T2 · 补丁 G（伴生）—— 字体面令牌桥：把骨架发出的 IntPtr 令牌接到 MIL 的句柄表
// =====================================================================================
// 【要解决的问题】
//   `GlyphTypeface.cs:1264` 把 `FontFace.DWriteFontFaceAddRef`（IntPtr）交给
//   `MilGlyphRun_GetGlyphOutline`；`GlyphRun.cs:1876` 把 `Font.DWriteFontAddRef`
//   写进 `MilCmdGlyphRunCreate.pIDWriteFont`。MIL 侧（M7a）用
//   `MilFontFaceTable.TryResolve(pFontFace, out SKTypeface)` 反查；
//   反查不到 → `HResult.E_HANDLE`（画不出轮廓，但不会画错）。
//
// 【⚠ 关键事实：MIL 现在是**另一个运行时**（这条改变了一切）】
//   T1 把 MIL 做成了 NativeAOT 共享库 `wpfgfx_cor3.so`
//   （`src/Linux/build/MilBridge/src/MilBridge.Linux/MilBridge.Linux.csproj`：`PublishAot=true`、
//    `NativeLib=Shared`、`AssemblyName=wpfgfx_cor3`、`ProjectReference` 到 WpfGfx.Linux）。
//   于是 `MilFontFaceTable` 的**静态字段活在 .so 自己的运行时里**，
//   与 PresentationCore 进程里的任何同名类型**不是同一份存储**。
//   实测证据（本目录 REPORT.md §3 有完整输出）：
//     $ nm -D wpfgfx_cor3.so | grep MilGlyphRun_GetGlyphOutline
//       T MilGlyphRun_GetGlyphOutline          ← MIL 的原生入口
//     $ grep -i fontface gen/export-symbols.txt → **没有**任何字体面登记导出
//   因此：**在托管侧给 MilFontFaceTable 装一个委托，跨不过这条边界**
//   （那只会写进一个 .so 永远看不见的副本）。跨运行时唯一可行的形态是
//   「由 .so 自己分配令牌」——即 .so 侧导出一个登记函数，托管侧 P/Invoke 它。
//
// 【本文件因此做两件事】
//   ① 把 `FontHandleTable.TokenAllocator` **装上**（宿主/测试可观察、可断言），
//      其实现是：**优先调用 .so 的导出**（若存在），否则返回 IntPtr.Zero 让它回落到
//      进程内表（此时 MIL 会返回 E_HANDLE —— 明确降级，不是静默成功）。
//   ② 把降级状态做成**可查询的**：`FontBridge.Status` / `FontBridge.Diagnostics`，
//      测试与运行期诊断都能一眼看出"桥接当前是哪一档"。
//
// 【给 .so 侧的契约（本文件已按此实现调用侧；.so 侧尚未实现）】
//   导出（cdecl，UTF-8 路径）：
//       intptr_t MilFontFace_RegisterFromFile(const char* utf8Path,
//                                             int32_t faceIndex,
//                                             int32_t simulationFlags);
//   语义：.so 用**自己的 Skia** 从路径加载字体面并登记，返回非 0 令牌；
//         失败返回 0。令牌在进程生命周期内稳定（同一路径+面下标 → 同一令牌）。
//   为什么是"路径"而不是"SKTypeface 指针"：两个运行时各有一份 Skia 绑定，
//   跨运行时传 `SkTypeface*` 需要假定底层 `libSkiaSharp.so` 是同一份被加载的实例
//   —— 那是个不该赌的前提。路径是运行时无关的，且 .so 侧本来就要自己加载字体。
//   （这条导出需要改 `src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/*` 与重建 MilBridge ——
//     都属于 T2 的写入边界之外，清单见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/REPORT.md §3.4）
//
// 【为什么用 [ModuleInitializer] 而不是改 Win32ShimResolver】
//   `NativeLibrary.SetDllImportResolver` 对同一程序集只能装一次，而
//   `src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs` 已经占用了那个槽位（M7b）。
//   `[ModuleInitializer]` 可以有**多个**，互不干扰 —— 这里只装我们自己的委托，
//   不碰它的注册逻辑。若 M7b/T1 的解析器将来改名，本文件不受影响。

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MS.Internal.Text.TextInterface.Linux;

namespace WpfLinux.Shims.PresentationCore
{
    /// <summary>令牌桥的档位（可查询，便于测试与运行期诊断）。</summary>
    internal enum FontFaceBridgeMode
    {
        /// <summary>还没安装（模块初始化未跑）。</summary>
        NotInstalled = 0,

        /// <summary>
        /// 装上了，但 MIL 的 .so 没有提供登记导出 → 令牌只在进程内表里，
        /// MIL 反查不到（`MilGlyphRun_GetGlyphOutline` 返回 E_HANDLE）。
        /// </summary>
        ProcessLocalOnly = 1,

        /// <summary>装上了并且 .so 的登记导出可用 → 令牌由 MIL 分配，可被反查。</summary>
        NativeAotExport = 2,

        /// <summary>
        /// **被 A/B 开关显式关掉**（`WPF_LINUX_FACE_HANDOFF=0`）⇒ 路径式分配器**不装**，
        /// 令牌来自 `FontHandleTable` 的进程内表 ⇒ MIL 反查不到
        /// ⇒ 渲染器应读到「**有句柄但解析失败**」（这就是 (乙) 的"关"档基线）。
        /// </summary>
        DisabledBySwitch = 3,
    }

    /// <summary>字体面令牌桥的安装与状态。</summary>
    internal static class FontFaceBridge
    {
        /// <summary>MIL 侧的登记导出名（契约见文件头；.so 侧尚未实现）。</summary>
        internal const string RegisterExportName = "MilFontFace_RegisterFromFile";

        /// <summary>MIL 的共享库文件名（与 T1 的 MilCoreDllImportResolver 同一个名字）。</summary>
        internal const string MilCoreSharedObjectName = "wpfgfx_cor3.so";

        /// <summary>
        /// (乙)/(债务 #14) 的 **A/B 开关**：`WPF_LINUX_FACE_HANDOFF`。
        /// **未设/空 ⇒ 开**（= 修法生效：路径式分配器装上，令牌由 MIL 分配）；
        /// `0`/`false`/`off`/`no` ⇒ 关（令牌退回进程内表 ⇒ MIL 解析不到 ⇒ 渲染器读"有句柄但解析失败"）。
        /// 语义是**纯函数** <see cref="ParseHandoff"/>，可用断言直接钉住（见 `Diagnostics` 的自报原文）。
        /// </summary>
        internal const string HandoffEnvVar = "WPF_LINUX_FACE_HANDOFF";

        /// <summary>诊断开关 `WPF_LINUX_FACE_HANDOFF_DIAG`：**未设/空 ⇒ 关**；开了打**有界**几行（缺省零输出）。</summary>
        internal const string HandoffDiagEnvVar = "WPF_LINUX_FACE_HANDOFF_DIAG";

        private static readonly bool s_handoff = ParseHandoff(Environment.GetEnvironmentVariable(HandoffEnvVar));
        private static readonly bool s_handoffDiag = ParseHandoffDiag(Environment.GetEnvironmentVariable(HandoffDiagEnvVar));

        /// <summary>路径式分配器是否启用（**未设环境变量时为 true**）。</summary>
        internal static bool HandoffEnabled => s_handoff;

        /// <summary>未设/空白 ⇒ **true**；"0"/"false"/"off"/"no"（不分大小写）⇒ false；其余 ⇒ true。</summary>
        internal static bool ParseHandoff(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string v = value.Trim();
            if (v.Length == 0) return true;
            if (v == "0") return false;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "no", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>未设/空白 ⇒ **false**；"1"/"true"/"on"/"yes"（不分大小写）⇒ true；其余 ⇒ false。</summary>
        internal static bool ParseHandoffDiag(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim();
            if (v == "1") return true;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int s_diagLines;

        /// <summary>
        /// 有界诊断（≤ <see cref="MaxDiagLines"/> 行）：**"可判性"的唯一出口** ——
        /// 在它之前，`Status/AllocatorCalls/NativeAllocations` 在真应用里**读不到**（本文件没有消费者）。
        /// 前缀固定 `[FACE_HANDOFF]`，行里带**机器可读**键值，便于 runner/复验脚本 grep。
        /// </summary>
        private const int MaxDiagLines = 12;

        private static void HandoffDiag(string message)
        {
            if (!s_handoffDiag) return;
            if (System.Threading.Interlocked.Increment(ref s_diagLines) > MaxDiagLines) return;
            try { Console.Error.WriteLine("[FACE_HANDOFF] " + message); Console.Error.Flush(); } catch (Exception) { }
        }

        private static IntPtr s_milHandle;
        private static bool s_exportProbed;
        private static IntPtr s_registerExport;
        private static int s_allocatorCalls;
        private static int s_nativeAllocations;
        private static string s_diagnostics = "未安装";

        /// <summary>当前档位。</summary>
        internal static FontFaceBridgeMode Status { get; private set; } = FontFaceBridgeMode.NotInstalled;

        /// <summary>人读诊断（含 .so 句柄、导出探测结果、调用计数）。</summary>
        internal static string Diagnostics => s_diagnostics;

        /// <summary>分配器被调用的次数（测试用它证明"钩子确实生效"）。</summary>
        internal static int AllocatorCalls => s_allocatorCalls;

        /// <summary>由 .so 分配成功的次数。</summary>
        internal static int NativeAllocations => s_nativeAllocations;

        /// <summary>
        /// 幂等安装。可以重复调用（第二次起只更新诊断，不重复装委托）。
        /// </summary>
        internal static void Install()
        {
            if (Status != FontFaceBridgeMode.NotInstalled)
            {
                RefreshDiagnostics();
                return;
            }

            // (乙) A/B 开关：关掉就**不装**路径式分配器 ⇒ 令牌退回进程内表
            //   ⇒ 渲染器（`TryResolveExact`，不回落）应读到「有句柄但解析失败」。
            if (!s_handoff)
            {
                Status = FontFaceBridgeMode.DisabledBySwitch;
                RefreshDiagnostics();
                HandoffDiag("Install: " + HandoffEnvVar + "="
                            + (Environment.GetEnvironmentVariable(HandoffEnvVar) ?? "<未设>")
                            + " ⇒ 解析为 **关** ⇒ 不装路径式分配器（令牌来自进程内表；MIL 将解析不到）");
                return;
            }

            // 装**路径式**钩子：它不需要 SkiaSharp 类型（PresentationCore 没有该引用），
            // 而且正好对应跨运行时唯一可靠的标识（见文件头的契约）。
            FontHandleTable.PathTokenAllocator = AllocateTokenFromMil;
            ProbeMilExport();

            Status = s_registerExport != IntPtr.Zero
                ? FontFaceBridgeMode.NativeAotExport
                : FontFaceBridgeMode.ProcessLocalOnly;

            RefreshDiagnostics();

            HandoffDiag("Install: " + HandoffEnvVar + "="
                        + (Environment.GetEnvironmentVariable(HandoffEnvVar) ?? "<未设>")
                        + " ⇒ 解析为 **开**（未设时就是开）; " + s_diagnostics);
        }

        /// <summary>
        /// 令牌分配器：优先问 MIL 的 .so，拿不到就返回 IntPtr.Zero
        /// （`FontHandleTable` 会回落到进程内表 —— 明确降级，不静默成功）。
        /// </summary>
        /// <summary>
        /// 路径式分配器：`(路径, 面下标, 模拟标志) → 令牌`。
        /// 拿不到 .so 的登记导出（或调用失败）→ 返回 IntPtr.Zero，
        /// 由 <c>FontHandleTable</c> 回落到进程内表。
        /// </summary>
        private static IntPtr AllocateTokenFromMil(string sourcePath, int faceIndex, int simulationFlags)
        {
            int call = ++s_allocatorCalls;

            if (string.IsNullOrEmpty(sourcePath))
            {
                HandoffDiag($"登记#{call} path 为空 ⇒ 不接管（回落进程内表）");
                return IntPtr.Zero;
            }

            ProbeMilExport();
            if (s_registerExport == IntPtr.Zero)
            {
                HandoffDiag($"登记#{call} 找不到导出 {RegisterExportName} ⇒ 回落进程内表"
                            + "（MIL 反查不到 ⇒ 渲染器读『有句柄但解析失败』）; path={sourcePath} faceIndex={faceIndex}");
                return IntPtr.Zero;
            }

            IntPtr token = CallRegisterExport(sourcePath, faceIndex, simulationFlags);

            if (token != IntPtr.Zero)
            {
                s_nativeAllocations++;
                // ⭐ 面身份（债务 #14 缺的那一格）：**登记时的 (token, path, faceIndex)** ——
                //   它就是"PC 整形时真正用的那份面"的标识；与逐 run 的 `pid` 对齐即可证完。
                HandoffDiag($"登记#{call} token=0x{token.ToInt64():x} path={sourcePath}"
                            + $" faceIndex={faceIndex} simFlags={simulationFlags} (nativeAllocations={s_nativeAllocations})");
            }
            else
            {
                HandoffDiag($"登记#{call} 导出返回 0（失败） path={sourcePath} faceIndex={faceIndex}");
            }

            return token;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RegisterFromFileDelegate(IntPtr utf8Path, int faceIndex, int simulationFlags);

        private static IntPtr CallRegisterExport(string path, int faceIndex, int simulationFlags)
        {
            try
            {
                var register = (RegisterFromFileDelegate)Marshal.GetDelegateForFunctionPointer(
                    s_registerExport, typeof(RegisterFromFileDelegate));

                IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(path);
                try
                {
                    return register(utf8, faceIndex, simulationFlags);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(utf8);
                }
            }
            catch (Exception)
            {
                // 导出存在但调用失败：明确降级（不抛给调用方 —— 字体解析不该因为桥接问题而失败）
                return IntPtr.Zero;
            }
        }

        private static void ProbeMilExport()
        {
            if (s_exportProbed) return;
            s_exportProbed = true;

            try
            {
                // 用与 T1 解析器相同的库名/路径探测：先试已加载的模块，再试同目录文件。
                foreach (string candidate in CandidateMilLibraryPaths())
                {
                    if (!NativeLibrary.TryLoad(candidate, out IntPtr handle)) continue;

                    s_milHandle = handle;
                    if (NativeLibrary.TryGetExport(handle, RegisterExportName, out IntPtr export))
                        s_registerExport = export;
                    break;
                }
            }
            catch (Exception)
            {
                // 探测失败 → 保持"仅进程内"档位（诊断里会写出来）
            }
        }

        private static string[] CandidateMilLibraryPaths()
        {
            var candidates = new System.Collections.Generic.List<string>();
            string baseDirectory = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDirectory, MilCoreSharedObjectName));

            // T1 的产物目录（开发/测试期的常见位置）
            var dir = new DirectoryInfo(baseDirectory);
            while (dir != null)
            {
                candidates.Add(Path.Combine(dir.FullName, "src", "Linux", "build", "MilBridge", ".artifacts", "publish",
                                           "MilBridge.Linux", "release_linux-x64", MilCoreSharedObjectName));
                candidates.Add(Path.Combine(dir.FullName, "src", "Linux", "build", "MilBridge", ".artifacts", "bin",
                                           "MilBridge.Linux", "release_linux-x64", "native", MilCoreSharedObjectName));
                dir = dir.Parent;
            }

            // 纯名字形式（交给动态链接器搜索 LD_LIBRARY_PATH / 系统路径）
            candidates.Add(MilCoreSharedObjectName);
            return candidates.ToArray();
        }

        private static void RefreshDiagnostics()
        {
            s_diagnostics =
                "handoff=" + (s_handoff ? "on(未设即开)" : "off(开关关闭)") +
                " status=" + Status +
                " milHandle=0x" + s_milHandle.ToInt64().ToString("X") +
                " exportProbed=" + s_exportProbed +
                " registerExport=" + (s_registerExport != IntPtr.Zero ? "找到(" + RegisterExportName + ")" : "未找到") +
                " allocatorCalls=" + s_allocatorCalls +
                " nativeAllocations=" + s_nativeAllocations;

            // 每一档都点名说清"接下来会怎样"：
            //   ProcessLocalOnly 是**明确降级**：MIL 会 E_HANDLE（画不出轮廓），
            //   需要 .so 侧提供登记导出才能跨过运行时边界。
            if (Status == FontFaceBridgeMode.ProcessLocalOnly)
            {
                s_diagnostics += " → 降级：令牌只在进程内表；MilGlyphRun_GetGlyphOutline 会返回 E_HANDLE。" +
                                 "跨运行时桥接需要 " + MilCoreSharedObjectName + " 导出 " + RegisterExportName + "（见文件头契约）";
            }
            else if (Status == FontFaceBridgeMode.NativeAotExport)
            {
                s_diagnostics += " → 已跨运行时接通（令牌由 MIL 分配）";
            }
            else if (Status == FontFaceBridgeMode.DisabledBySwitch)
            {
                s_diagnostics += " → **被 " + HandoffEnvVar + " 显式关掉**（A/B 的\"关\"档）："
                               + "路径式分配器未装，令牌来自进程内表 ⇒ 渲染器应读到「有句柄但解析失败」。"
                               + "不设该变量即为\"开\"。";
            }
        }
    }

    /// <summary>
    /// 模块初始化：程序集（PresentationCore）加载时自动安装令牌桥。
    /// 与 M7b 的 Win32ShimResolver 的 [ModuleInitializer] 并存 —— ModuleInitializer
    /// 可以有多个，且本类**不**调用 SetDllImportResolver，不会与它抢槽位。
    /// </summary>
    internal static class FontFaceBridgeModuleInitializer
    {
        // CA2255 是「不要在库里用 ModuleInitializer」的告警；这里正是**刻意的**用法
        // —— 与 src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 同一处置（同一个程序集里第二个
        // ModuleInitializer 是合法的，两者互不干扰）。
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void InstallFontFaceBridge() => FontFaceBridge.Install();
#pragma warning restore CA2255
    }
}
