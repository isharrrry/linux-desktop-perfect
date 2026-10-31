// 【ACC-028 / WinForms 第一道墙】给 **原版 System.Windows.Forms** 补装 DllImport resolver。
//
// 为什么必须有这个文件：
//   `NativeLibrary.SetDllImportResolver` 是**按程序集**生效、且**每个程序集只能装一次**。
//   本仓的 `Win32ShimResolver` 装在了 WindowsBase / PresentationCore 上（`[ModuleInitializer]`），
//   但 **`System.Windows.Forms.dll` 是 Windows 原版**（我们没重编它，也不该重编 ——
//   正是它保持程序集身份，才让 Windows 编译的 WinForms 程序能被加载）。
//   于是它的 `[DllImport("KERNEL32.dll")]`（注意 CsWin32 用**全大写**）没有任何 resolver 接管，
//   实测直接抛：
//       System.DllNotFoundException: Unable to load shared library 'KERNEL32.dll'
//         at Windows.Win32.PInvoke.GetModuleHandle(PCWSTR)
//
// 这里的做法：在 StartupHook 里订阅 `AssemblyLoad`，任何 `System.Windows.Forms*` 一加载就立刻
// 给它装 resolver（必须赶在它第一次 P/Invoke 之前，所以不能等 Main）。
//
// 限制（如实记录）：这只解决"**能不能找到 shim**"，不解决"**shim 里有没有那个函数**"。
//   WinForms 需要的 Win32 面比 WPF 宽得多（窗口/消息/GDI/控件/剪贴板/DPI/主题…），
//   缺的符号会以 `EntryPointNotFoundException` 的形式逐个暴露出来。
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ldp.A11y
{
    internal static class Win32ShimCoverage
    {
        // 与 PresentationCore/WindowsBase 的 `Win32ShimResolver` 保持一致（大小写不敏感）
        private static readonly string[] MappedLibraries =
        {
            "user32.dll", "gdi32.dll", "kernel32.dll", "imm32.dll",
            "comctl32.dll", "comdlg32.dll", "shell32.dll", "ole32.dll", "oleaut32.dll",
            "advapi32.dll", "shcore.dll", "uxtheme.dll", "dwmapi.dll", "winmm.dll",
            "version.dll", "psapi.dll", "dbghelp.dll", "powrprof.dll", "setupapi.dll",
            "wtsapi32.dll", "imm32.dll", "msimg32.dll", "winspool.drv", "mpr.dll",
        };

        private static void Diag(string m) { Console.Error.WriteLine("[SHIM-COVER] " + m); Console.Error.Flush(); }

        private static IntPtr _shim;
        private static bool _installed;

        /// <summary>在 StartupHook 里调用一次。之后任何 WinForms 程序集加载都会被接管。</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryCover(e.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) TryCover(asm);
        }

        private static void TryCover(Assembly asm)
        {
            if (asm == null || asm.IsDynamic) return;
            var n = asm.GetName().Name;
            if (n == null) return;
            // 接管：WinForms 家族 + System.Drawing 家族 + System.Private.Windows.*
            //   【为什么必须抢在 System.Drawing.Common 自己的 cctor 之前】
            //   它的 `PInvokeGdiPlus..cctor` 里会 `SetDllImportResolver(gdiplus.dll → 抛
            //   PlatformNotSupportedException)`，而 `SetDllImportResolver` **每个程序集只能装一次**。
            //   我们在 `AssemblyLoad` 事件里先装，它再装就抛 `InvalidOperationException`
            //   （它自己会吞掉），于是走我们这条 ⇒ 由本仓的 `gdiplus` shim（29 个 Gdip* 导出）提供实现。
            //   这就是绕开 "System.Drawing.Common is not supported on non-Windows platforms" 的路径。
            bool cover = n.StartsWith("System.Windows.Forms", StringComparison.Ordinal)
                      || n.StartsWith("System.Private.Windows", StringComparison.Ordinal);
            // 【不要覆盖 System.Drawing.Common 自己的 resolver】
            //   我们换成 corefx 3.1 编的 Unix 版之后，它自己有 `System.Drawing.LibraryResolver`
            //   （在 `Gdip..cctor` 里 `SetDllImportResolver`）。我们再抢着装会让它抛
            //   `InvalidOperationException: A resolver is already set for the assembly`（实测）。
            //   让它自己装，我们只需在 `$FX/` 提供 `libgdiplus.so` 符号链接指向 `libwpfwin32.so`
            //   （它找的就是 `libgdiplus`，见 `src/misc/ExternDll.Unix.cs` 的 `Gdiplus = "gdiplus"`）。
            if (n.StartsWith("System.Drawing", StringComparison.Ordinal)) return;
            if (!cover) return;
            try
            {
                NativeLibrary.SetDllImportResolver(asm, Resolve);
                Console.Error.WriteLine("[SHIM-COVER] 已给 " + n + " 装 DllImportResolver");
                Console.Error.Flush();
            }
            catch (InvalidOperationException)
            {
                // 已经装过（同程序集只能一次）—— 正常
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SHIM-COVER] " + n + " 装 resolver 失败：" + ex.GetType().Name);
                Console.Error.Flush();
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly requesting, DllImportSearchPath? searchPath)
        {
            if (string.IsNullOrEmpty(libraryName)) return IntPtr.Zero;
            var mapped = false;
            foreach (var m in MappedLibraries)
            {
                if (string.Equals(m, libraryName, StringComparison.OrdinalIgnoreCase)) { mapped = true; break; }
            }
            if (!mapped) { Diag("未映射：" + libraryName + "（请求方 " + requesting.GetName().Name + "）"); return IntPtr.Zero; }

            if (_shim == IntPtr.Zero)
            {
                var path = Environment.GetEnvironmentVariable("WPF_LINUX_WIN32_SHIM");
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    Diag("SHIM 路径无效：" + (path ?? "(null)"));
                    return IntPtr.Zero;
                }
                try { _shim = NativeLibrary.Load(path); Diag("已加载 shim：" + path); }
                catch (Exception ex) { Diag("加载 shim 失败：" + ex.GetType().Name + " " + ex.Message); return IntPtr.Zero; }
            }
            Diag("映射 " + libraryName + " → shim（请求方 " + requesting.GetName().Name + "）");
            return _shim;
        }
    }
}
