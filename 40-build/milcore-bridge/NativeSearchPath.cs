// T1 · MilBridge —— NativeAOT 镜像内部的**依赖库搜索路径**修正。
//
// 【问题（实测）】
//   wpfgfx_cor3.so 里的 SkiaSharp 走 [DllImport("libSkiaSharp")]。.so 被托管进程
//   dlopen 进来之后，NativeAOT 的 P/Invoke 探测用的是**宿主进程的**
//   AppContext.BaseDirectory（跑 `dotnet app.dll` 时就是 ~/.dotnet/），
//   不是 .so 所在目录，于是：
//     DllNotFoundException: libSkiaSharp
//       tried /home/links-dev/.dotnet/libSkiaSharp.so ...
//   而 libSkiaSharp.so 其实就躺在 wpfgfx_cor3.so 旁边（pubilsh 输出里）。
//   `LD_LIBRARY_PATH=<publish dir>` 能绕过，但要求调用方配环境变量 —— 不可接受。
//
// 【解法】
//   在 AOT 镜像内给 **SkiaSharp 程序集**注册一个 DllImportResolver，
//   把 libSkiaSharp 指到"wpfgfx_cor3.so 所在目录/libSkiaSharp.so"。
//   目录由托管侧的 MilCoreDllImportResolver 在 dlopen 成功后通过
//   MilBridge_SetNativeDir 传进来（它本来就刚算过这个路径，零额外探测）。
//
//   注意：NativeLibrary.SetDllImportResolver 是**按程序集**生效的，
//   所以这里对 SkiaSharp 注册不会与 MilCore 的解析器冲突。

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MilBridge
{
    /// <summary>AOT 镜像内部依赖库（SkiaSharp）的搜索路径修正。</summary>
    public static unsafe class NativeSearchPath
    {
        private static string s_nativeDir;
        private static bool s_installed;

        /// <summary>MilCore .so 所在目录（由托管侧传入）。</summary>
        public static string NativeDir => s_nativeDir;

        [ModuleInitializer]
        internal static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            try
            {
                // SkiaSharp 是被 AOT 编进本镜像的，它的 P/Invoke 归它自己那条解析器管。
                Assembly skia = typeof(SkiaSharp.SKBitmap).Assembly;
                NativeLibrary.SetDllImportResolver(skia, Resolve);
            }
            catch (InvalidOperationException)
            {
                // 已经有别的解析器（理论上不会）—— 让位，退回 LD_LIBRARY_PATH。
            }
        }

        /// <summary>托管侧在加载 wpfgfx_cor3.so 成功后调用，告知自己的目录。UTF-8。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_SetNativeDir")]
        public static int SetNativeDir(byte* utf8Dir)
        {
            s_nativeDir = utf8Dir == null ? null : Marshal.PtrToStringUTF8((nint)utf8Dir);

            // 顺带把同一个目录告诉 MIL 侧的外部句柄桥（WIC）：它要找 libwpfwic.so，
            // 而 .so 内部的 AppContext.BaseDirectory 不是宿主应用目录（M7c3 踩过的坑）。
            // 两者同在一个 AOT 镜像里，直接调 public 静态方法即可。
            try { WpfGfx.Linux.Interop.MilExternalHandleBridge.SetSearchDirectory(s_nativeDir); }
            catch { /* 诊断/兼容：桥不可用不影响主链路 */ }

            return 0;
        }

        /// <summary>该目录是否已设置（1/0），诊断用。</summary>
        [UnmanagedCallersOnly(EntryPoint = "MilBridge_Diag_NativeDirSet")]
        public static int NativeDirSet() => string.IsNullOrEmpty(s_nativeDir) ? 0 : 1;

        /// <summary>
        /// dladdr 自定位到的目录，UTF-8 C 字符串（诊断用）。
        /// 指向**进程内静态缓冲**，下一次调用前一直有效 —— 调用方不要 free。
        /// （对外导出在 Diagnostics.cs 的 MilBridge_Diag_SelfDirectory。）
        /// </summary>
        public static byte* SelfDirectoryPtr()
        {
            string d = SelfDirectory;
            if (d == null) return null;
            byte[] utf8 = Encoding.UTF8.GetBytes(d + "\0");
            fixed (byte* src = utf8)
            {
                int n = Math.Min(utf8.Length, s_selfDirBuffer.Length);
                for (int i = 0; i < n; i++) s_selfDirBuffer[i] = src[i];
            }
            fixed (byte* p = s_selfDirBuffer) { return p; }
        }

        private static readonly byte[] s_selfDirBuffer = new byte[4096];

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (string.IsNullOrEmpty(libraryName) ||
                libraryName.IndexOf("SkiaSharp", StringComparison.Ordinal) < 0)
            {
                return IntPtr.Zero;   // 不是我们管的库，交回默认探测
            }

            foreach (string candidate in Candidates())
            {
                try
                {
                    if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
                }
                catch
                {
                    // 试下一个候选
                }
            }

            return IntPtr.Zero;       // 找不到就让默认探测去报带完整信息的错
        }

        private static string[] Candidates()
        {
            string app = AppContext.BaseDirectory;
            string self = SelfDirectory;   // dladdr 定位自己的 .so（见下）
            return new[]
            {
                // 1) 托管解析器显式注入的目录（MilBridge_SetNativeDir）
                string.IsNullOrEmpty(s_nativeDir) ? null : Path.Combine(s_nativeDir, "libSkiaSharp.so"),
                // 2) **自己的 .so 所在目录** —— 不依赖任何宿主。见 SelfDirectory 的说明。
                string.IsNullOrEmpty(self) ? null : Path.Combine(self, "libSkiaSharp.so"),
                // 3) 宿主应用目录
                string.IsNullOrEmpty(app) ? null : Path.Combine(app, "libSkiaSharp.so"),
                string.IsNullOrEmpty(app) ? null : Path.Combine(app, "runtimes", "linux-x64", "native", "libSkiaSharp.so"),
            };
        }

        // ==================================================================
        //  自我定位（dladdr）—— **M7c3 修的正是这一条**
        // ==================================================================
        //
        //  【之前为什么会失败】
        //  在 M7c3 之前，AOT 镜像只有在托管侧的 MilCoreDllImportResolver 跑过一次
        //  （它调 MilBridge_SetNativeDir 注入目录）之后才知道自己在哪。
        //  而 T2 的 FontBridge.ProbeMilExport 是**直接**
        //  `NativeLibrary.TryLoad(<绝对路径>)` 加载 .so 的，**完全绕开了解析器** ——
        //  于是 s_nativeDir 为空、AppContext.BaseDirectory 又是宿主应用目录（没有
        //  libSkiaSharp.so）⇒ SkiaSharp 的 DllImport 抛 DllNotFoundException
        //  ⇒ RegisterFromFile 按契约把它吞成 0（"找到导出但 nativeAllocations=0"）。
        //  纯 C 宿主（dlopen）同理。
        //
        //  【修法】用 dladdr 在**自己的导出函数**上取本 .so 的路径 —— 与宿主无关，
        //  与调用顺序无关，托管宿主/纯 C 宿主都一样。
        private static string s_selfDir;
        private static bool s_selfDirProbed;

        /// <summary>本 .so 所在目录（dladdr 自定位；失败时 null）。</summary>
        public static string SelfDirectory
        {
            get
            {
                if (!s_selfDirProbed)
                {
                    s_selfDirProbed = true;
                    try
                    {
                        // 取自己某个 [UnmanagedCallersOnly] 导出的地址 —— 它必然落在
                        // 本 .so 的映射区间里，dladdr 据此回报 dli_fname = .so 路径。
                        delegate* unmanaged<byte*, int> self = &SetNativeDir;
                        if (TryDladdr((IntPtr)self, out string path) && !string.IsNullOrEmpty(path))
                        {
                            s_selfDir = Path.GetDirectoryName(Path.GetFullPath(path));
                        }
                    }
                    catch
                    {
                        // 定位不了就退回落链的后面几档
                    }
                }
                return s_selfDir;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DlInfo
        {
            public IntPtr dli_fname;
            public IntPtr dli_fbase;
            public IntPtr dli_sname;
            public IntPtr dli_saddr;
        }

        [DllImport("libdl.so.2", EntryPoint = "dladdr")]
        private static extern int DladdrLibdl(IntPtr addr, out DlInfo info);

        private static bool TryDladdr(IntPtr addr, out string path)
        {
            path = null;
            DlInfo info;
            int rc;
            try
            {
                rc = DladdrLibdl(addr, out info);
            }
            catch (DllNotFoundException)
            {
                // 少数发行版把 dladdr 并进 libc（glibc ≥ 2.34 起 libdl 只是兼容壳）。
                try { rc = DladdrLibc(addr, out info); }
                catch { return false; }
            }
            catch
            {
                return false;
            }

            if (rc == 0 || info.dli_fname == IntPtr.Zero) return false;
            path = Marshal.PtrToStringUTF8(info.dli_fname);
            return !string.IsNullOrEmpty(path);
        }

        [DllImport("libc.so.6", EntryPoint = "dladdr")]
        private static extern int DladdrLibc(IntPtr addr, out DlInfo info);
    }
}
