// T1 · MilBridge —— 与 M7b Win32 shim 解析器**合并**的写法（接线时二选一）。
//
// 背景（实测）：`NativeLibrary.SetDllImportResolver(assembly, resolver)` 对同一个程序集
// **只能调用一次**，第二次抛 `InvalidOperationException`。
// 而 M7b 的 Win32 shim 也要在 PresentationCore.Linux 里注册解析器
// （user32.dll / gdi32.dll / kernel32.dll …）。两者不能各自 [ModuleInitializer] 自装。
//
// 所以接线时有两种落地形态：
//
//   形态 1（本目录 MilCoreDllImportResolver.cs 自己装）
//     —— 只有 MilCore 一个解析器时用。If 已有别的解析器：InstallConflict=true 并静默让位，
//        此时必须改用形态 2。
//
//   形态 2（推荐，最终形态）—— 全程序集只装一个解析器，内部按库名分派：
//
//     [ModuleInitializer]
//     internal static void Install()
//         => NativeLibrary.SetDllImportResolver(
//                typeof(Win32ShimResolver).Assembly, Resolve);
//
//     private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
//     {
//         // ① MIL Core：交给 MilBridge 侧的幂等入口
//         if (WpfGfx.Linux.Bridge.MilCoreDllImportResolver.TryResolve(libraryName, out IntPtr mil))
//             return mil;
//
//         // ② Win32 子集：M7b 的 shim
//         if (Win32ShimResolver.TryResolve(libraryName, out IntPtr win32))
//             return win32;
//
//         // ③ 其余交回默认探测
//         return IntPtr.Zero;
//     }
//
// 本文件只是把形态 2 的骨架写下来，供主控接线时直接抄。
