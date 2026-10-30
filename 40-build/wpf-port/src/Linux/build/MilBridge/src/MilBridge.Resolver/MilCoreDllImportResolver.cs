// T1 · MilBridge —— `wpfgfx_cor3.dll` → `wpfgfx_cor3.so` 的 DllImport 解析器（**可组合形态**）。
//
// ============================================================================
//  【本文件不含 [ModuleInitializer]，这是刻意的】
//
//  NativeLibrary.SetDllImportResolver 对同一个程序集**只能调用一次**，
//  第二次抛 InvalidOperationException。而 PresentationCore.Linux 里
//  **已经有**一个自装解析器：src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs（M7b 的 Win32 子集 shim），
//  它的 [ModuleInitializer] 会在模块初始化时抢占这个槽位，而且**没有 catch**。
//
//  若本文件也自装，谁先跑谁赢：一旦本文件先跑，Win32ShimResolver.Register 会
//  抛未捕获的 InvalidOperationException，**整个模块初始化失败** → 拖垮 M7b。
//  所以本文件只提供**幂等入口** TryResolve，由既有解析器调用：
//
//      // src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 的 Resolve() 开头加 3 行：
//      if (WpfGfx.Linux.Bridge.MilCoreDllImportResolver.TryResolve(libraryName, out nint milCore))
//          return milCore;
//
//  独立宿主（测试、示例）需要自装时，用同目录的
//  MilCoreStandaloneInstaller.cs —— 只有它才会去 SetDllImportResolver。
// ============================================================================
//
//  【接线位置】把本文件加进 src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.shims.txt（见 docs/T1-report.md）。
//  目标程序集只有 PresentationCore.Linux 一个：108 条 [DllImport(DllImport.MilCore)]
//  全在它里面，WindowsBase 一条都没有（实测扫描，见 gen/milcore-dllimports.json）。

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfGfx.Linux.Bridge
{
    /// <summary>把 MIL Core 的 P/Invoke 目标库名映射到本工程的 NativeAOT 实现。</summary>
    public static unsafe class MilCoreDllImportResolver
    {
        /// <summary>托管层 108 条 [DllImport] 用的库名（Windows 上的 wpfgfx_cor3.dll）。</summary>
        public const string MilCoreLibraryName = "wpfgfx_cor3.dll";

        /// <summary>Linux 侧实际文件（NativeAOT 共享库）。</summary>
        public const string MilCoreSharedObjectName = "wpfgfx_cor3.so";

        private static IntPtr s_handle;
        private static bool s_loaded;
        private static bool s_loadFailed;
        private static readonly object s_gate = new object();

        /// <summary>实际加载到的 .so 绝对路径（诊断用；未加载为 null）。</summary>
        public static string LoadedPath { get; private set; }

        /// <summary>本类是否已经真的加载到实现库。</summary>
        public static bool IsLoaded => s_loaded && !s_loadFailed && s_handle != IntPtr.Zero;

        /// <summary>
        /// 幂等解析入口。任何 DllImportResolver 都可以直接调用它：
        /// 非本库名时返回 false 且 handle = Zero，调用方继续走自己的逻辑。
        /// </summary>
        public static bool TryResolve(string libraryName, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (!string.Equals(libraryName, MilCoreLibraryName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!s_loaded)
            {
                lock (s_gate)
                {
                    if (!s_loaded)
                    {
                        s_loaded = true;   // 先置位：NotifyNativeDir 的 P/Invoke 会重入这里
                        string path = FindSharedObject();
                        if (path == null)
                        {
                            // 找不到文件：**不抛**，让默认探测去报 DllNotFoundException，
                            // 错误信息里会带真实库名，便于排查。
                            s_loadFailed = true;
                            return false;
                        }
                        s_handle = NativeLibrary.Load(path);
                        LoadedPath = path;

                        // 让 AOT 镜像知道自己的目录：镜像内部对 libSkiaSharp 的 P/Invoke 需要它
                        // （否则会去宿主进程的 AppContext.BaseDirectory 找，必然失败 —— 见
                        //  ../MilBridge.Linux/NativeSearchPath.cs 的说明）。
                        NotifyNativeDir(Path.GetDirectoryName(path));
                    }
                }
            }

            if (s_loadFailed || s_handle == IntPtr.Zero) return false;
            handle = s_handle;
            return true;
        }

        // ------------------------------------------------------------------
        //  文件查找顺序（先精确、后宽松；每一档都有明确理由）
        // ------------------------------------------------------------------
        private static string FindSharedObject()
        {
            // 1) 显式覆盖：测试与多版本并存场景。
            string env = Environment.GetEnvironmentVariable("MILBRIDGE_MILCORE_SO");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            // 2) 附加目录（可放多个，用 ':' 分隔）—— 打包布局用。
            string dirs = Environment.GetEnvironmentVariable("MILBRIDGE_MILCORE_DIR");
            if (!string.IsNullOrEmpty(dirs))
            {
                foreach (string d in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = Path.Combine(d, MilCoreSharedObjectName);
                    if (File.Exists(p)) return p;
                }
            }

            // 3) 应用目录（与 .dll 同级）—— 默认发布布局。
            string app = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(app))
            {
                string p = Path.Combine(app, MilCoreSharedObjectName);
                if (File.Exists(p)) return p;

                // 4) NuGet 原生资源布局。
                p = Path.Combine(app, "runtimes", "linux-x64", "native", MilCoreSharedObjectName);
                if (File.Exists(p)) return p;
            }

            // 4b) 本程序集所在目录 —— **共享框架布局**（`L1 路线①`）专用档：
            //     12 件托管件与四个 .so 一起放进
            //     `<dotnet root>/shared/Microsoft.WindowsDesktop.App/<ver>/` 时，
            //     `AppContext.BaseDirectory`（= 应用目录）够不到框架目录 ⇒ 多这一档。
            //     语义不变：只**追加**一档，前面的候选顺序逐字不动。
            try
            {
                string asmLoc = typeof(MilCoreDllImportResolver).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLoc))
                {
                    string asmDir = Path.GetDirectoryName(asmLoc);
                    if (!string.IsNullOrEmpty(asmDir) && !string.Equals(asmDir, app, StringComparison.Ordinal))
                    {
                        string p = Path.Combine(asmDir, MilCoreSharedObjectName);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch
            {
                // 取不到 Location（单文件发布等）就当没有这一档
            }

            // 5) 开发树布局：从应用目录向上找 src/Linux/build/MilBridge/.artifacts/publish/**。
            //    只为"在仓库里直接 dotnet run"方便，找不到就放弃。
            //
            //    ⚠️ 上溯层数随**落点加深**重算（结构上游化把测试/探针从仓根挪到
            //    `src/Linux/{tests,build/MilBridge/tests}/…` ⇒ 应用目录到仓根多了 2～3 级）。
            //    实测：`ManagedLayer.Tests/bin/Release/net10.0` 到仓根是 **8** 级，
            //    而旧上界 `8` 最多只走到 `…/src` ⇒ 开发树这一档形同不存在 ⇒
            //    DP1/M7c 一批用例报 `DllNotFoundException: wpfgfx_cor3.dll`。
            //    与 `src/Linux/tests/**` 里那些 `handoff.md` 上溯循环同口径取 12（留余量）。
            try
            {
                var di = new DirectoryInfo(app);
                for (int up = 0; up < 12 && di != null; up++, di = di.Parent)
                {
                    string root = Path.Combine(di.FullName, "src", "Linux", "build", "MilBridge", ".artifacts", "publish");
                    if (Directory.Exists(root))
                    {
                        string[] hits = Directory.GetFiles(root, MilCoreSharedObjectName, SearchOption.AllDirectories);
                        if (hits.Length > 0) return hits[0];
                    }
                }
            }
            catch
            {
                // 目录不可读就当作找不到
            }

            return null;
        }

        // ------------------------------------------------------------------
        //  与 AOT 镜像的握手：告知它自己的 .so 目录（镜像内部 SkiaSharp 探测用）
        // ------------------------------------------------------------------
        [DllImport(MilCoreLibraryName, EntryPoint = "MilBridge_SetNativeDir")]
        private static extern int MilBridgeSetNativeDir(byte* utf8Dir);

        private static void NotifyNativeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(dir + "\0");
                fixed (byte* p = utf8) { MilBridgeSetNativeDir(p); }
            }
            catch (EntryPointNotFoundException)
            {
                // 旧版/精简版 .so 没有这个导出：不影响主链路，退回 LD_LIBRARY_PATH。
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}
