// T1 · MilBridge —— **独立宿主**用的自装器。
//
// 只有"这个程序集没有别的 DllImportResolver"时才用本文件。
// PresentationCore.Linux **不要**编译本文件 —— 它已经有 M7b 的
// src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 占了 SetDllImportResolver 这个槽位，
// 两个自装器会互相踩（第二次调用抛 InvalidOperationException，
// 而 Win32ShimResolver.Register 没有 catch → 整个模块初始化失败）。
// PresentationCore 走的是「在 Win32ShimResolver.Resolve 里调 TryResolve」的形态。
//
// 使用者：src/Linux/build/MilBridge/tests/ClosedLoop（模拟托管层）+ 将来的独立示例/工具。

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Bridge
{
    /// <summary>给独立宿主用的 DllImport 解析器自装器。</summary>
    internal static class MilCoreStandaloneInstaller
    {
        /// <summary>true 表示该程序集已经有解析器，本类让位（此时必须用组合形态）。</summary>
        public static bool InstallConflict { get; private set; }

        [ModuleInitializer]
        internal static void Install()
        {
            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(MilCoreStandaloneInstaller).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                InstallConflict = true;
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
            => MilCoreDllImportResolver.TryResolve(libraryName, out IntPtr h) ? h : IntPtr.Zero;
    }
}
