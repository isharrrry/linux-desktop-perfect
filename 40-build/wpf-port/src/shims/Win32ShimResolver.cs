// WPF-on-Linux · M7b · Win32 shim 的 DllImport 解析器
//
// ── 这个文件解决什么问题 ──────────────────────────────────────────────────
//   WindowsBase / PresentationCore 的窗口与消息 API 全是
//       [DllImport("user32.dll")]  [DllImport("gdi32.dll")]  [DllImport("kernel32.dll")]
//   这样的原生 P/Invoke。Linux 上 .NET **不会**把 `user32.dll` 自动映射成
//   `libuser32.so`（主控实测：`DllNotFoundException: Unable to load shared
//   library 'user32.dll'`）。三条可行路线里选定的是
//   `NativeLibrary.SetDllImportResolver` + `[ModuleInitializer]`：
//     · 不用把 ELF 改名成 `user32.dll` 丢在 app 目录（会掩盖真实的 API 缺失）；
//     · 不依赖 app 目录布局（resolver 里可以做多级路径探测 + 环境变量覆盖）。
//
// ── 为什么是**一个**文件编进两个程序集，而不是两个文件 ────────────────────
//   任务书写的是「WindowBase 与 PresentationCore 各自需要一次注册（各自 shim 文件）」。
//   事实核对后是：**注册必须按程序集各做一次**（`SetDllImportResolver` 的作用域
//   就是传入的那个 Assembly），但**解析策略只应该有一份**——两份拷贝必然漂移，
//   而漂移的表现是「WindowsBase 能建窗、PresentationCore 建不了」这类极难查的现象。
//   所以：同一个源文件被两个 csproj 各编一次 → 每个程序集各有一个模块初始化器
//   （ModuleInitializer 本来就是 per-module 的），策略逻辑物理上只有一份。
//   两个程序集里的类型**同名不同命名空间**（靠 DefineConstants 区分：
//   WindowsBase 定义了 WINDOWS_BASE，PresentationCore 定义了 PRESENTATION_CORE），
//   因此在 PresentationCore 内部不会与经 InternalsVisibleTo 可见的 WindowsBase
//   版本撞名（CS0433 那一类问题）。
//
// ── 未映射的名字怎么办 ────────────────────────────────────────────────────
//   返回 `IntPtr.Zero` → 交回默认探测。于是 `wpfgfx_cor3.dll`（MIL，M7a 已用
//   托管实现补齐，接线属于 M7c）、`WindowsCodecs.dll`（WIC，109 条）、
//   `PenIMC_cor3.dll`、`mshwgst.dll`、`ole32.dll` 等**仍然是明确的
//   DllNotFoundException**——这是刻意的：不假装成功，缺失面一眼可见。
//
// ── Windows 平台短路（L3 · `WIN-INTEROP.md` §7.4 ①）───────────────────────
//   同一份产物要"在 Linux 上跑本栈实现、在 Windows 上跑官方件"。本文件是这条路上
//   唯一会**主动劫持** Win32 名字的地方 ⇒ 它是唯一需要平台门的地方。做法：
//     `Register()` 首行 `if (OperatingSystem.IsWindows()) return;`
//   ——Windows 上不注册任何解析器（含 ALC 级钩子），P/Invoke 全交回默认探测 ⇒ 官方件；
//      Linux 上该条件恒 false ⇒ 本文件既有行为**一位不差**。详见
//      `src/Linux/build/MilBridge/P1-wininteropL3-impl-report.md`。

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

#if WINDOWS_BASE
namespace WpfLinux.Shims.WindowsBase
#elif PRESENTATION_CORE
namespace WpfLinux.Shims.PresentationCore
#elif UIAUTOMATIONTYPES || AUTOMATION
// [D1] UIAutomationTypes（DefineConstants 里的 `UIAUTOMATIONTYPES`）与
//      UIAutomationProvider（`AUTOMATION`）也需要这一层：ListBox 选中变化会走到
//      AutomationPeer..cctor → OSVersionHelper..cctor → [DllImport("PresentationNative_cor3.dll")]
//      ⇒ 没有 resolver 就是 DllNotFoundException（T3 实测）。
//      这两个常量**本来就在**它们的 DefineConstants 里 ⇒ 不必动 port-lib 的常量表。
//      被 `#if PRESENTATION_CORE` 门控的 MIL 桥 / WIC 段在这里不会编进来（与 WINDOWS_BASE 分支同理）。
namespace WpfLinux.Shims.UIAutomation
#else
#error "Win32ShimResolver.cs 只能编进 WindowsBase / PresentationCore / UIAutomationTypes / UIAutomationProvider（需要 WINDOWS_BASE / PRESENTATION_CORE / UIAUTOMATIONTYPES / AUTOMATION 常量）"
#endif
{
    /// <summary>
    /// 把 Win32 的 DLL 名映射到本工程的 <c>libwpfwin32.so</c>。
    /// </summary>
    internal static class Win32ShimResolver
    {
        /// <summary>shim 库的文件名（各平台同名，只有 Linux 会走到这里）。</summary>
        internal const string ShimFileName = "libwpfwin32.so";

        /// <summary>
        /// 覆盖 shim 位置的最高优先级环境变量。
        /// 指向 .so 文件本身，或指向包含它的目录，两种都认。
        /// </summary>
        internal const string ShimPathEnv = "WPF_LINUX_WIN32_SHIM";

        /// <summary>
        /// 覆盖「仓库根」的环境变量（找不到时会从程序集目录逐级向上找
        /// <c>src/WpfGfx.Linux.Native</c>）。测试与 sample 在 out-of-tree
        /// 目录里跑时，这条能救回来。
        /// </summary>
        internal const string RepoRootEnv = "WPF_LINUX_ROOT";

        /// <summary>
        /// 映射到 shim 的 DLL 名（**顺序无关**，全部按 OrdinalIgnoreCase 比）。
        ///
        /// user32 / gdi32 / kernel32 是任务书点名的三个。`PresentationNative_cor3.dll`
        /// 是**第 1 步清单逼出来的第四个**：上游 `Shared/MS/Win32/NativeMethodsSetLastError.cs`
        /// 把 GetWindowLong/SetWindowLong/GetWindowLongPtr/SetWindowLongPtr/GetParent/
        /// GetWindow/SetFocus/EnableWindow 全部声明成
        ///   [DllImport("PresentationNative_cor3.dll", EntryPoint = "GetWindowLongPtrWrapper")]
        /// 而 HwndSubclass 的整套 WndProc 链**正是**走这几个（不是 user32 的裸名）。
        /// 不映射它，窗口骨架在 Linux 上就没有落点。
        /// 本 shim 同时导出裸名与 *Wrapper 名，两份调用点都命中。
        /// </summary>
        private static readonly string[] MappedLibraries =
        {
            "user32.dll",
            "gdi32.dll",
            "kernel32.dll",
            "PresentationNative_cor3.dll",
            // M7c 实测新增：UxThemeWrapper（IsThemeActive）与 WTS 会话查询
            //   —— Linux 上分别是"无活动 Windows 主题"与"本地会话"的真话，
            //   不加这两行则新导出在托管层不可达（只能用 app-local 同名 ELF 兜底）。
            "uxtheme.dll",
            "wtsapi32.dll",

            // ── `#35` 波：**第三方应用会自己 P/Invoke 的 5 个名字** ────────────────────
            //   为什么必须补：第三方库（实例：HandyControl）不看我们的映射表，它直接
            //   `[DllImport("shell32.dll")] ExtractIconEx`、`[DllImport("gdiplus.dll")] GdiplusStartup`…
            //   缺符号的后果是**应用被掀掉**（`EntryPointNotFoundException` / `DllNotFoundException`），
            //   不是"少个功能"。对应入口实现见 `src/WpfGfx.Linux.Native/src/win32_oem.c`
            //   与 `win32_gdiplus.c`（口径：能真做的真做，做不到的**如实失败**，GDI+ 只做到"应用能起来"）。
            "shell32.dll",      // ExtractIconEx（窗口默认图标）、SHGetFileInfo、ShellExecute
            //   （窗口建出来了但整屏全黑、`WPF_LINUX_MIL_TRACE` 一行都没有 ⇒ 呈现链根本没跑到）。
            //   同一批读数：撤掉 `gdiplus`/`shell32`/`msimg32`/`dwmapi` 都不影响画面（1274/1275 色），
            //   只有撤掉 `ntdll`（= 让 shim 提供 `ntdll.dll`）会黑屏。⇒ 需要先补齐 ntdll 面
            //   （至少 `RtlGetVersion` 之外的入口）再打开；登记在 `docs/WAVE34-PREREGISTRATION.md` §3t。
            "ntdll.dll",        // RtlGetVersion（系统版本分支；Linux 无真值 ⇒ 报受支持的 Win10 版本，降级在册）
            //   ⚠️ 只对**应用程序集**生效 —— 钩子里排除 CoreLib，见 ResolvingUnmanagedDll 注册处。
            "gdiplus.dll",      // GdiplusStartup 等（第三方图像 helper；查询类返回空结果、创建类如实失败）
            "msimg32.dll",      // AlphaBlend/TransparentBlt（本 shim 的 gdi32 面只占位 ⇒ 如实失败）
            "dwmapi.dll",       // DWM 合成/玻璃（Linux 由 X11 合成器负责 ⇒ 如实返回"不支持"）
        };

        /// <summary>
        /// T2 · WIC shim（`libwpfwic.so`）的映射名。
        /// ⚠ **默认关闭**：只有显式给了 <see cref="WicShimPathEnv"/>（文件或目录）
        /// 或 <see cref="WicEnableEnv"/>=1 才生效。理由：托管侧读路径闭环成立之前打开映射，
        /// 只会把 `DllNotFoundException` 变成 `EntryPointNotFoundException`（诊断更差）。
        /// **打开默认映射的确切条件**：<c>src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WicClosedLoop</c> 的 ①-④
        /// 全绿（见该目录 REPORT.md §15），届时把 <see cref="WicEnabledByDefault"/> 改成 true 即可。
        /// </summary>
        private static readonly string[] WicMappedLibraries =
        {
            "WindowsCodecs.dll",

            // `ole32.dll` 放进 **WIC 组**（不放 Win32 组）——主控 2026-09 裁定，理由三条：
            //   1) PC 的 ole32 声明全编译集只有 2 处，同一个类：
            //      `UnsafeNativeMethodsMilCoreApi.cs:1050 CoInitialize` / `:1054 CoUninitialize`，
            //      调用点是 WIC 解码 bootstrap 的 SafeHandle（`UnknownBitmapDecoder.cs:27,32`）
            //      ⇒ 与 WIC 开关**同生命周期**，放 WIC 组语义自洽（libwpfwic.so 已导出这两个名字）。
            //   2) 若放进 Win32 组（user32/gdi32/kernel32 那组）会**波及 WindowsBase**——
            //      本文件同时编进 WB，而 WB 另有 **5 处** ole32 声明
            //      （`MS/Internal/IO/Packaging/CompoundFile/PrivateUnsafeNativeCompoundFileMethods.cs`，
            //      打包/OLE 族），我们的 shim 没有它们 ⇒ 把 WB 现在**诚实的 DllNotFoundException**
            //      降级成 **EntryPointNotFoundException**（诊断更差），正是本项目一路在避免的。
            //   3) ⚠ 将来打包/OLE 真做实现时：要么给那 5 处补导出，要么把 ole32 挪到 Win32 组
            //      并**同时**处理 WB 那 5 条 —— 别以为这是随手加的一行。
            "ole32.dll",
        };

        /// <summary>WIC shim 文件名（T2 产物）。</summary>
        internal const string WicShimFileName = "libwpfwic.so";

        /// <summary>WIC shim 的显式路径（文件或目录）——给了它就等于打开映射。</summary>
        internal const string WicShimPathEnv = "WPF_LINUX_WIC_SHIM";

        /// <summary>显式开关（=1 打开 WIC 映射，路径走默认候选）。</summary>
        internal const string WicEnableEnv = "WPF_LINUX_WIC";

        /// <summary>是否默认打开 WIC 映射。**当前 false**（见上文的打开条件）。</summary>
        // ⚠️ 2026-09-10 主控改：`const` → `static readonly`。
        //    原因：写 `const false` 时，`IsWicMappingEnabled()` 里的 `if (WicEnabledByDefault) return true;`
        //    是**编译期不可达代码** ⇒ 每个编进本文件的程序集都报 **CS0162**（实测 WB 与 PC 各一条），
        //    破坏本工程"0 错 0 警"的标准。改成 `static readonly` 后：
        //      · **翻开关仍然是改这一行的 `false` → `true`**（T2 原设计的这个性质完整保留）；
        //      · 运行期行为**逐字等价**（同一个布尔值，同一个判断）；
        //      · 不可达代码消失，CS0162 不再出现。
        //    代价：损失"关闭时由编译器整个消除 WIC 分支"的优化 —— 对本场景（解析器里的一次布尔判断）无意义。
        // ✅ 2026-09-10 晚主控翻开关为 true —— 依据（全部为实测，且由主控独立复跑过）：
        //    · WIC 托管闭环 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WicClosedLoop/run-harness.sh` **严格模式**（不设
        //      LD_LIBRARY_PATH）→ `RESULT=PASS`、exit 0；`BYTE_MISMATCH=0 / 1920000`（与 `SKBitmap.Decode` 逐点）；
        //      `PIXEL_FNV1A=62FD64E564953288` 与 C 级探针记录值一致；CHECK1/2/3 = PASS、CHECK4 失败路径 3/3。
        //    · `WIC_SHIM_MAPS=1` / `WIC_SHIM_DOUBLE_TABLE=FALSE`（单实例，双表陷阱有断言守着）。
        //    · MIL 侧 `MILQueryInterface` 放行 + `MILRelease` 转发已落地（`run.sh wic` 54/54）。
        //    · 本分支已用 `#if PRESENTATION_CORE` 守卫 ⇒ 翻开关**只影响 PC**，WB 行为一字不变。
        //    翻开关仍是改这一行；要临时关掉也可用 env（见 IsWicMappingEnabled）。
        private static readonly bool WicEnabledByDefault = true;

        // 句柄缓存：**按 shim 文件名索引**（Win32 shim 与 WIC shim 各自一份，
        // 不再是单个 _cachedHandle/_cachedPath —— 那只能装下一个 shim）。
        private static readonly System.Collections.Generic.Dictionary<string, nint> _loadedHandles =
            new System.Collections.Generic.Dictionary<string, nint>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.Dictionary<string, string> _loadedPaths =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object _loadGate = new object();

        private static string _cachedPath;      // 兼容：只记录 **Win32** shim 的路径（诊断文案不变）
        private static string _lastSearchLog;

        /// <summary>
        /// 模块初始化器：在任何托管代码触碰本模块之前注册 resolver。
        /// （CA2255 是「不要在库里用 ModuleInitializer」的告警；这里正是
        ///   库初始化必须抢先于首个 P/Invoke 的场景，属刻意为之，故就地抑制。）
        ///
        /// ⚠️ 2026-09-16（`#18` 波 · V1 · 登记项 `D-R3`）：`SetDllImportResolver` **对同一个程序集
        ///   只能装一次**，第二次抛 `InvalidOperationException: A resolver is already set for the assembly.`。
        ///   本文件被编进**四个**程序集（WindowsBase / PresentationCore / UIAutomationTypes /
        ///   UIAutomationProvider），每个程序集各得一个本初始化器 ⇒ 一旦外部宿主在拿到该程序集的
        ///   `Assembly` 对象后**抢先**调 `SetDllImportResolver`（实例：`src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WicClosedLoop/Program.cs:123`；
        ///   另一条**可达**的路 = 宿主先 `Assembly.LoadFrom(pc)` 再装 —— 实测模块初始化器**不是**在
        ///   加载期跑的，见 `src/Linux/build/MilBridge/V18A-report.md` §3），**本行抛的就是模块初始化器里的
        ///   未捕获异常 ⇒ 整个模块的每个类型全灭**。修前实测形态（`#18` 波，权威 `pc=df6dbb1c2bfb4162`）：
        ///   `TypeInitializationException: The type initializer for '&lt;Module&gt;' threw an exception.`
        ///   inner = `InvalidOperationException: A resolver is already set for the assembly.`
        ///   ⇒ 这里改成两段式：
        ///   ① **装不上不算失败**（"本程序集已经有人接名字"本身是合法状态 —— 与测试侧 `D-R2` 的
        ///      定案同一口径，见 `src/Linux/tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/X11Guard.cs:175-196`）；
        ///   ② **但不许静默** —— 紧跟一次**真 `[DllImport]`** 自证：赢家不映射我们的名字 ⇒ 当场
        ///      **响亮 + 点名**；`ShimFileName` 缺件 ⇒ 照样硬失败（守卫只容忍"被抢先"，绝不容忍"装置缺件"）。
        /// </summary>
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void Register()
        {
            // ── L3（`WIN-INTEROP.md` §7.4 ①）：**Windows 平台短路** ──────────────────────
            //   本 shim 的全部意义是"在 Linux 上把 user32/gdi32/… 这些 Win32 名字接到
            //   `libwpfwin32.so`"。到了 **Windows**，那些名字本来就由官方系统 DLL 提供，
            //   而 `libwpfwin32.so` 是 ELF（Windows 加载不了）⇒ 不短路就是 §7.4 复现过的码路：
            //   首个 P/Invoke 处 `DllNotFoundException: 'user32.dll' 已映射到 libwpfwin32.so，
            //   但没找到可加载的 shim 库。`（离成因很远地掀掉整个应用）。
            //   ⇒ Windows 上**什么都不注册**：既不装 ALC 级钩子，也不占本程序集的解析器槽位，
            //     一切 P/Invoke 交回默认探测 ⇒ 走的正是**官方系统件**。
            //   ⚠️ 平台判定用运行期 API（`OperatingSystem.IsWindows()`），不是编译期 `#if`
            //     —— 同一份产物要能在两个平台各自行为正确（编译期门控做不到）。
            //   ⚠️ 本行是**本文件唯一**的平台分支点：Linux 上它恒为 false ⇒ 下方所有既有行为
            //     逐字不变（"一位不差"）。
            if (OperatingSystem.IsWindows())
                return;

            // ── `#35` 波：**默认 ALC 级钩子**（第三方程序集的 P/Invoke 也要够得着本 shim）──
            //   `SetDllImportResolver` 是**按程序集**生效的，只对我们自己那几个程序集装了；
            //   第三方应用（`HandyControl.dll` 的 `[DllImport("user32.dll")]`）走**默认探测**
            //   ⇒ `DllNotFoundException`（实测：run 19 的 `user32.dll`、run 17 的 `ExtractIconEx`）。
            //   `AssemblyLoadContext.Default.ResolvingUnmanagedDll` 是**默认 ALC 级**事件，
            //   在默认探测失败之后被问到 ⇒ 正好接住这一类。
            //   ⚠️ 只对**映射表里**的名字作答（`Resolve` 对表外名返回 Zero ⇒ 交回默认行为）；
            //      `Resolve` 在 WIC 缺件时会抛带解释的 `DllNotFoundException` —— 钩子里**吞掉并
            //      退回 Zero**，让"原始缺件"照旧由默认路径响亮报出（不让钩子改变错误形态）。
            try
            {
                AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
                {
                    // ⚠️ **绝不劫持 CoreLib 的查询**：运行期自己会按名找 `ntdll.dll`（内部 P/Invoke）。
                    //   实测（`#35` 逐名二分）：`ntdll` 一进映射表且连 CoreLib 一起接住，第三方应用就
                    //   "窗口建出来但整屏全黑、MIL 轨迹一行都没有"；同一个 DLL 由 app-local 桩提供时画面正常。
                    //   ⇒ 排除 CoreLib 后，钩子只服务"应用的"P/Invoke（那正是它存在的理由）。
                    if (ReferenceEquals(assembly, typeof(object).Assembly)) return IntPtr.Zero;
                    try { return Resolve(name, assembly, null); }
                    catch { return IntPtr.Zero; }
                };
            }
            catch (Exception)
            {
                // 装不上就退回"只有自己的程序集可用"。**不静默**：第三方程序集仍会在
                // 首个真 P/Invoke 处以 `DllNotFoundException` 响亮失败（那是修前就有的行为）。
            }

            bool installedByUs = false;
            try
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(Win32ShimResolver).Assembly, Resolve);
                installedByUs = true;
            }
            catch (InvalidOperationException)
            {
                // 本程序集的解析器槽位已被别人占用（竞态的另一方赢了）。合法状态 ⇒ 不在这里抛，
                // 但**立刻自证**（下面那段）——否则就变成了 `D-R2` 那种"静默吞"。
                ResolverConflict = true;
            }

            // ⚠️ 2026-09-16 15:1x（`#18` 波 · V18A **收窄**，主控裁决 1）：
            //   **自证只在输掉竞态的分支里跑。**
            //   理由（这条理由本身是判据）：无条件自证会在**正常路径**上多一次 dlopen —— 实测
            //   `/proc/self/maps` 里 `libwpfwin32.so` **0→5 行**、`libX11.so.6` **0→6 行**、总行数
            //   246→299（修前同一步是 247→262）—— 而本项目的**内存类主判据是"段数 / Σ虚拟"**
            //   （`D-F1c` 线的口径）⇒ 那等于往一套以"段数"为尺子的判据里塞了一个新映射。
            //   收窄后：正常路径**一位不改**（`libwpfwin32.so` 0→0，见 `src/Linux/build/MilBridge/V18A-report.md` §10）。
            //   **代价（如实记）**：判据①（正常路径真 `[DllImport]` 成功）与判据④（shim 缺件时在
            //   **安装点**响亮）不再由本守卫行使 —— 缺件仍在**首个真 P/Invoke** 处以"候选路径逐条"的
            //   `DllNotFoundException` 响亮失败（那是**修前就有的**行为，不是新引入的静默）；
            //   "赢家不映射我们的名字"这一类（守卫存在的唯一理由）**照旧响亮且点名**。
            if (installedByUs)
                return;

            // ---- 自证（**只在输竞态时**）：用**真的 `[DllImport]`** 走一次 ----
            // ⚠️ 不许用 `NativeLibrary.TryLoad(name, assembly, …)` 做这件事：那个 API 只用程序集定
            //    **搜索路径**，**不经过** `DllImportResolver`（2026-09-15 实测踩到：恒 false ⇒
            //    静态构造抛 ⇒ 33 条用例全灭）。必须用真 `[DllImport]`（下面 `ShimVersionViaUser32`，
            //    它声明在**本程序集内** ⇒ 走的就是本程序集注册的那个解析器）。
            try
            {
                SelfCheckShimVersion = ShimVersionViaUser32();
            }
            catch (EntryPointNotFoundException)
            {
                // 库解析成功、只是导出名不同 ⇒ 赢家映射了我们的名字（与测试侧 `X11Guard` 同一口径）⇒ 无害。
                SelfCheckShimVersion = -1;
            }
            catch (DllNotFoundException ex)
            {
                // **响亮且点名**：我们输了竞态，而赢家不映射我们要的那批名字 ⇒ 本程序集此后每一次
                // `[DllImport("user32.dll")]`/`gdi32`/`kernel32`/… 都会在**深处**变成
                // `DllNotFoundException`（离成因很远）⇒ 就在安装点这里把责任说清楚。
                throw new InvalidOperationException(
                    "WPF-on-Linux: 本程序集的 'user32.dll' 解析不到 —— **当前生效的解析器不是我们这一个**。\n" +
                    "  · 成因：`NativeLibrary.SetDllImportResolver` 对同一程序集只能装一次，本文件的 " +
                    "[ModuleInitializer] 输掉了竞态，而抢先者不映射我们要的那批名字：\n" +
                    "      " + string.Join(" / ", MappedLibraries) + "\n" +
                    "  · 后果：本程序集的窗口/消息面 P/Invoke 将全部以 `DllNotFoundException` 失败。\n" +
                    "  · 修法（二选一）：让本程序集只保留**唯一**安装点；或让抢先者映射同一批名字。\n" +
                    "  · 说明：抢先者装的解析器不会被本文件改写（槽位是它的）。",
                    ex);
            }
        }
#pragma warning restore CA2255

        /// <summary>
        /// 自证用的**真 `[DllImport]`**：shim 在 `user32.dll` 名字下的版本导出
        /// （`src/WpfGfx.Linux.Native/src/win32_exports.c:93`；调用无参数、无副作用，只回一个版本号）。
        /// 必须声明在**本程序集内**才走本程序集的解析器 —— 外部探针程序集里写的 `[DllImport]` 走它自己的。
        /// </summary>
        [DllImport("user32.dll", EntryPoint = "WpfLinuxWin32_ShimVersion")]
        private static extern int ShimVersionViaUser32();

        /// <summary>诊断用：装解析器时**输掉了竞态**（别人先占了这个程序集的槽位）。没输 = false。</summary>
        internal static bool ResolverConflict { get; private set; }

        /// <summary>
        /// 诊断用：**输掉竞态时**自证得到的 `WpfLinuxWin32_ShimVersion()` 返回值
        /// （`0` = 没输竞态、自证未跑；`-1` = 赢家映射了名字但导出名不同 ⇒ 无害）。
        /// </summary>
        internal static int SelfCheckShimVersion { get; private set; }

        /// <summary>
        /// 由 .NET 运行时在**首次**解析某个 DllImport 时调用。
        /// 返回 <see cref="IntPtr.Zero"/> = 「我不管这个名字，走默认探测」。
        /// </summary>
        private static nint Resolve(string libraryName, Assembly assembly,
                                    DllImportSearchPath? searchPath)
        {
#if PRESENTATION_CORE
            // ↓↓↓ T1 新增：MIL Core 的 108 条 [DllImport(DllImport.MilCore)] 走 NativeAOT 实现。
            // 为什么挂在这里而不是自己再装一个解析器：`SetDllImportResolver` 对**同一程序集只能调一次**，
            // 本文件的 [ModuleInitializer] 已占位；再装一个会由"谁先跑谁赢"决定，输的那个抛未捕获异常。
            // （2026-09-16 `#18` 波 · V1 措辞更正，**原意不变**：本文件的 ModuleInitializer 现在**容忍**
            //  输掉竞态（`catch (InvalidOperationException)` + 真 `[DllImport]` 自证）。本条论断因此要读成
            //  "**别人**再装一个会由'谁先跑谁赢'决定" —— T1 的解析器不自己装、只暴露 `TryResolve`
            //  供这里组合，这个设计**照旧**是对的（它躲开了槽位之争，一行都不用改）。
            // T1 的解析器因此刻意不含 ModuleInitializer，只暴露 TryResolve 供这里组合。
            if (WpfGfx.Linux.Bridge.MilCoreDllImportResolver.TryResolve(libraryName, out nint milCore))
                return milCore;
            // ↑↑↑ T1 新增
#else
            // ⚠️ M7b 补的守卫：上面这段 **只能**在 PresentationCore 里编。
            //    实测：WindowsBase 的编译集合里 `[DllImport(DllImport.MilCore)]` 是 **0 条**
            //    （104 条全在 PresentationCore），所以 MIL 桥接对 WindowsBase 毫无意义；
            //    而 WindowsBase 也没有引用 `WpfGfx.Linux.dll`（MilBridge.Resolver 只加进了
            //    PresentationCore.shims.txt）→ 不加守卫就是 WindowsBase 直接编译失败
            //    `CS0103: 当前上下文中不存在名称"WpfGfx"`。
            //    这正是"一个源文件编进两个程序集"这个设计要付的税：**共享的部分必须
            //    对两个程序集都成立**，只对其中一个成立的部分要么用 DefineConstants
            //    隔开（这里），要么就得拆文件。
#endif

            // ---- T2 · WIC shim（PC 专属；由 WicEnabledByDefault 决定默认是否启用）----
            //
            // ⚠️ 2026-09-10 主控加 `#if PRESENTATION_CORE` 守卫：**WIC 是 PresentationCore 的概念**。
            //    本文件同时编进 WindowsBase，而 WicMappedLibraries 里有 `ole32.dll`（PC 侧只有 2 处声明，
            //    调用点是 WIC 的 UnknownBitmapDecoder）；**WB 另有 5 处 `ole32.dll` 声明**
            //    （`MS/Internal/IO/Packaging/CompoundFile/PrivateUnsafeNativeCompoundFileMethods.cs`，
            //    打包/OLE 族），我们的 shim 没有它们。
            //    不加守卫的后果：一旦把 `WicEnabledByDefault` 翻成 true，**WB 也会把 ole32 映射到 libwpfwic.so**，
            //    于是那 5 处从**诚实的 `DllNotFoundException`** 变成 **`EntryPointNotFoundException`**（诊断更差）——
            //    正是本项目一路在避免的降级。加守卫后：映射只在 PC 里生效，WB 的行为**一字不变**。
#if PRESENTATION_CORE
            if (IsWicMapped(libraryName))
            {
                if (!IsWicMappingEnabled())
                    return IntPtr.Zero;      // 未启用 ⇒ 交回默认探测 ⇒ 仍是不变的 DllNotFoundException

                lock (_loadGate)
                {
                    if (_loadedHandles.TryGetValue(WicShimFileName, out nint cachedWic) && cachedWic != nint.Zero)
                        return cachedWic;

                    if (TryLoadShim(WicShimFileName, WicShimPathEnv, WicShimPathEnv, useWicCandidates: true,
                                    out nint wicHandle, out string wicPath))
                    {
                        _loadedHandles[WicShimFileName] = wicHandle;
                        _loadedPaths[WicShimFileName] = wicPath;
                        return wicHandle;
                    }
                }

                throw new DllNotFoundException(
                    $"WPF-on-Linux: '{libraryName}' 已映射到 {WicShimFileName}，但没找到可加载的 WIC shim。\n" +
                    $"搜索过程：\n{_lastSearchLog}\n" +
                    $"修复：python3 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/build-wic-shim.sh；" +
                    $"或用 {WicShimPathEnv}=<{WicShimFileName} 的绝对路径> 显式指定。");
            }
#endif

            // ---- Win32 组（**行为与文案保持不变**）----
            if (!IsMapped(libraryName))
                return IntPtr.Zero;

            if (_loadedHandles.TryGetValue(ShimFileName, out nint cached) && cached != nint.Zero)
                return cached;

            if (TryLoadShim(ShimFileName, ShimPathEnv, RepoRootEnv, useWicCandidates: false,
                            out nint handle, out string loadedPath))
            {
                _loadedHandles[ShimFileName] = handle;
                _loadedPaths[ShimFileName] = loadedPath;
                _cachedPath = loadedPath;
                return handle;
            }

            // 映射到了但加载不到 = 明确的配置错误，给一条能直接照做的诊断。
            throw new DllNotFoundException(
                $"WPF-on-Linux: '{libraryName}' 已映射到 {ShimFileName}，但没找到可加载的 shim 库。\n" +
                $"搜索过程：\n{_lastSearchLog}\n" +
                $"修复：先构建 shim —— src/WpfGfx.Linux.Native/build-shim.sh --all；" +
                $"或用 {ShimPathEnv}=<libwpfwin32.so 的绝对路径> 显式指定。");
        }

        private static bool IsWicMapped(string libraryName)
        {
            if (string.IsNullOrEmpty(libraryName))
                return false;
            foreach (string candidate in WicMappedLibraries)
            {
                if (string.Equals(candidate, libraryName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// WIC 映射是否启用：**显式路径** 或 **显式开关** 二者之一。
        /// 两者都没有 ⇒ 关闭（默认），行为与没有本功能时完全一致。
        /// </summary>
        internal static bool IsWicMappingEnabled()
        {
            if (WicEnabledByDefault) return true;

            string path = Environment.GetEnvironmentVariable(WicShimPathEnv);
            if (!string.IsNullOrEmpty(path)) return true;

            string flag = Environment.GetEnvironmentVariable(WicEnableEnv);
            return flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMapped(string libraryName)
        {
            if (string.IsNullOrEmpty(libraryName))
                return false;
            foreach (string candidate in MappedLibraries)
            {
                if (string.Equals(candidate, libraryName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 路径解析顺序（**先具体后宽泛**，与工程的 X11 加载器同一套思路）：
        ///   1. <c>WPF_LINUX_WIN32_SHIM</c> 环境变量（文件或目录）
        ///   2. 程序集所在目录（app-local 部署；.so 与 dll 放一起就能用）
        ///   3. 仓库 <c>src/WpfGfx.Linux.Native/bin/</c>
        ///      3a. 由 <c>WPF_LINUX_ROOT</c> 直接给出仓库根
        ///      3b. 从程序集目录 / 当前工作目录逐级向上找含
        ///          <c>src/WpfGfx.Linux.Native</c> 的目录
        /// </summary>
        private static bool TryLoadShim(string shimFileName, string pathEnv, string repoRootEnv,
                                        bool useWicCandidates, out nint handle, out string loadedPath)
        {
            handle = nint.Zero;
            loadedPath = null;

            var log = new System.Text.StringBuilder();

            foreach (string candidate in EnumerateCandidates(log, shimFileName, pathEnv, repoRootEnv, useWicCandidates))
            {
                if (!File.Exists(candidate))
                {
                    log.Append("  · 不存在 ").Append(candidate).Append('\n');
                    continue;
                }
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    log.Append("  · 命中   ").Append(candidate).Append('\n');
                    _lastSearchLog = log.ToString();
                    loadedPath = candidate;
                    return true;
                }
                log.Append("  · 存在但加载失败 ").Append(candidate).Append('\n');
            }

            _lastSearchLog = log.ToString();
            return false;
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateCandidates(
            System.Text.StringBuilder log, string shimFileName, string pathEnv, string repoRootEnv,
            bool useWicCandidates)
        {
            string env = Environment.GetEnvironmentVariable(pathEnv);
            if (!string.IsNullOrEmpty(env))
            {
                // 目录 or 文件，两种都给出来试
                string asFile = env;
                string asDir = Path.Combine(env, shimFileName);
                log.Append("  · ").Append(pathEnv).Append(" = ").Append(env).Append('\n');
                yield return asFile;
                yield return asDir;
            }

            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
                yield return Path.Combine(baseDir, shimFileName);

            // ②' 本程序集所在目录 —— **共享框架布局**（`L1 路线①`）专用档。
            //     `windowsdesktop-app-linux-framework.sh` 把 12 件托管件与四个 .so 一起放进
            //     `<dotnet root>/shared/Microsoft.WindowsDesktop.App/<ver>/`；此时
            //     `AppContext.BaseDirectory` 仍是**应用**目录，够不到框架目录 ⇒ 少这一档就必然
            //     `DllNotFoundException: 已映射到 libwpfwin32.so，但没找到可加载的 shim 库`。
            //     语义不变：只**追加**一档（应用目录在前，仍优先）；app-local 部署行为逐字不变。
            string asmDir = AssemblyDirectory;
            if (!string.IsNullOrEmpty(asmDir) && !string.Equals(asmDir, baseDir, StringComparison.Ordinal))
                yield return Path.Combine(asmDir, shimFileName);

            if (useWicCandidates)
            {
                // ③ 仓库 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/（T2 的开发期产物位置）
                foreach (string start in new[] { baseDir, Directory.GetCurrentDirectory() })
                {
                    if (string.IsNullOrEmpty(start)) continue;
                    DirectoryInfo dir;
                    try { dir = new DirectoryInfo(start); } catch { continue; }
                    for (int depth = 0; dir != null && depth < 12; depth++, dir = dir.Parent)
                    {
                        string probe = Path.Combine(dir.FullName, "src", "Microsoft.DotNet.Wpf.Linux", "src", "DirectWrite", "wic-shim");
                        if (Directory.Exists(probe))
                            yield return Path.Combine(probe, shimFileName);
                    }
                }
                yield break;   // WIC 组不走 Win32 的仓库路径
            }

            string root = Environment.GetEnvironmentVariable(repoRootEnv);
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "src", "WpfGfx.Linux.Native", "bin", shimFileName);

            foreach (string start in new[] { baseDir, Directory.GetCurrentDirectory() })
            {
                if (string.IsNullOrEmpty(start))
                    continue;
                DirectoryInfo dir;
                try { dir = new DirectoryInfo(start); }
                catch { continue; }
                for (int depth = 0; dir != null && depth < 12; depth++, dir = dir.Parent)
                {
                    string probe = Path.Combine(dir.FullName, "src", "WpfGfx.Linux.Native");
                    if (Directory.Exists(probe))
                        yield return Path.Combine(probe, "bin", shimFileName);
                }
            }
        }

        /// <summary>诊断用：实际加载到的 **Win32** shim 路径（未加载时为 null）。</summary>
        internal static string LoadedPath => _cachedPath;

        /// <summary>
        /// 本解析器所在程序集的目录。共享框架布局（`L1 路线①`）下 = 框架目录
        /// （`<dotnet root>/shared/Microsoft.WindowsDesktop.App/<ver>/`）。
        /// 单文件发布或取不到 Location 时返回 null（那一档就不产生候选）。
        /// </summary>
        internal static string AssemblyDirectory
        {
            get
            {
                try
                {
                    string loc = typeof(Win32ShimResolver).Assembly.Location;
                    return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>诊断用：实际加载到的 **WIC** shim 路径（未加载时为 null）。</summary>
        internal static string WicLoadedPath =>
            _loadedPaths.TryGetValue(WicShimFileName, out string p) ? p : null;

        /// <summary>诊断用：WIC 映射当前是否启用（T2 harness 用它判断 skip 还是真跑）。</summary>
        internal static bool WicMappingActive => IsWicMappingEnabled();
    }
}
