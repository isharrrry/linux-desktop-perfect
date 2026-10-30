// libX11 的 P/Invoke 声明。
//
// 【为什么手写 P/Invoke 而不是引第三方绑定】
//   handoff 明确要求不用 XlibSharp 一类的包：NuGet 受限（GitHub/NuGet.org 的 TLS
//   被阻断，只有华为镜像），而我们真正需要的函数只有二十来个。引一整个绑定库
//   换来的是版本绑定风险和一堆用不上的 API。
//
// 【为什么用 NativeLibrary 而不是裸 DllImport("libX11.so.6")】
//   裸 DllImport 走的是系统的默认查找逻辑，遇到只有 libX11.so（没有 .so.6）或
//   装在非标准路径的发行版就会 DllNotFoundException。这里注册一个
//   DllImportResolver，按 libX11.so.6 → libX11.so 的顺序显式 TryLoad，
//   失败再抛一个**带明确提示**的异常，而不是让调用方对着默认错误信息猜。
//
// 【ABI 前提】
//   全部按 LP64（Linux x86-64 / arm64）布局：long 与指针 8 字节。
//   Xlib 里 Window/Atom/Time 都是 unsigned long（8 字节），用 ulong 对应。

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Windowing
{
    internal static class X11Native
    {
        public const string LibraryName = "libX11.so.6";

        // 显式静态构造函数：保证任何一次 P/Invoke 之前 resolver 已经注册。
        // （没用 [ModuleInitializer]，因为它是给应用代码用的，在库里会触发 CA2255，
        //   而本工程的基线上限是 0 警告。）
        //
        // ⚠️ 2026-09-16（`#18` 波 · V2 · 登记项 `D-R3`）：本构造函数是**产品源里唯一的静态构造安装点**
        //   （R17C 只读审计 §3-C4），而 `SetDllImportResolver` **对同一程序集只能装一次**：
        //   别人先占了槽位 ⇒ 本行抛 ⇒ **静态构造抛 ⇒ 类型被投毒**
        //   （`TypeInitializationException` 覆盖 `WpfGfx.Linux` 的全部 X11/呈现路径）。
        //   修前实测（`#18` 波，权威件 `WpfGfx.Linux.dll = 0c597fb6ec1eec70`）：
        //     `CCTOR_THROW=System.TypeInitializationException: The type initializer for
        //      'WpfGfx.Linux.Windowing.X11Native' threw an exception.
        //      | inner=System.InvalidOperationException: A resolver is already set for the assembly.`
        //   ⇒ 两段式：① 装不上**不算失败**（"本程序集已经有人接 `libX11.so.6`"本身是合法状态）；
        //   ② **但不许静默** —— 紧跟一次**真 `[DllImport]`** 自证（`X11NameResolves`），
        //   "我们的名字解析不到"当场**响亮 + 点名**；libX11 缺件照样硬失败。
        static X11Native()
        {
            bool installedByUs = false;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(X11Native).Assembly, X11LibraryResolver.Resolve);
                installedByUs = true;
            }
            catch (InvalidOperationException)
            {
                // 槽位已被别人占用（竞态的另一方赢了）。合法状态 ⇒ 不在这里抛，但**立刻自证**。
                ResolverConflict = true;
            }

            // ⚠️ 2026-09-16 15:1x（`#18` 波 · V18A **收窄**，主控裁决 1）：**自证只在输掉竞态时跑。**
            //   实测（收窄前）：无条件自证在**正常路径**上就把 `libX11.so.6` 载进进程 ——
            //   `/proc/self/maps` 里 `libX11` **0→6 行**、总行数 244→278；而**修前**同一趟是
            //   246→246（静态构造什么都不载）。本项目的内存类主判据是"段数 / Σ虚拟"（`D-F1c` 口径）
            //   ⇒ 收窄后正常路径**逐位回到修前**（`libX11` 0→0，见 `build/MilBridge/V18A-report.md` §10）。
            //   **代价（如实记）**：判据①/④ 不再由本守卫行使 —— libX11 缺件仍会在**首个真 P/Invoke**
            //   处由 `X11LibraryResolver.Resolve` 抛"带修复提示的 `DllNotFoundException`"（修前既有行为）。
            if (installedByUs)
                return;

            // ---- 自证（**只在输竞态时**）：用**真的 `[DllImport]`** 走一次 ----
            // ⚠️ 不许用 `NativeLibrary.TryLoad(name, assembly, …)` 做这件事：那个 API 只用程序集定
            //    **搜索路径**，**不经过** `DllImportResolver`（2026-09-15 实测踩到：恒 false ⇒
            //    静态构造抛 ⇒ 33 条用例全灭）。必须用真 `[DllImport]`（下面的 `SelfCheckProbe`，
            //    它声明在**本程序集内** ⇒ 走的就是本程序集注册的那个解析器）。
            if (X11NameResolves())
            {
                SelfCheckX11NameResolved = true;   // 赢家把我们的名字接上了 ⇒ 无害（诊断位留痕）
                return;
            }

            // 输掉竞态 **且** 我们的名字解析不到 ⇒ **响亮且点名**。
            // 边界（`#18` 波实测，见 `build/MilBridge/V18A-report.md` §6）：`libX11.so.6` 是
            // **系统可解析名**（`ldconfig -p` 里有它）⇒ "赢家映射别的名字"这一种**单独判不出来**
            // （默认探测照样成功、功能不受影响）⇒ 那种情形我们记诊断位、不抛（抛会在真实的无害场景里误报）。
            // 会**响亮**的是真正有害的那一种：赢家不映射我们的名字、**且**这个名字也解析不到
            // （例如赢家把 `libX11.so.6` 指到一个不可加载的位置）。
            throw new InvalidOperationException(
                "WPF-on-Linux: 本程序集的 '" + LibraryName + "' 解析不到 —— **当前生效的解析器不是我们这一个**。\n" +
                "  · 成因：`NativeLibrary.SetDllImportResolver` 对同一程序集只能装一次，本类型静态构造输掉了竞态，" +
                "而抢先者既不映射 '" + LibraryName + "'，默认探测也找不到可加载的 " + LibraryName + "。\n" +
                "  · 后果：`WpfGfx.Linux` 全部 X11/呈现路径都拿不到 Xlib。\n" +
                "  · 修法（二选一）：让本程序集只保留**唯一**安装点；或让抢先者把 '" + LibraryName + "' 映射到可加载的 Xlib。\n" +
                "  · 说明：抢先者装的解析器不会被本文件改写（槽位是它的）。");
        }

        /// <summary>
        /// 自证用的真 `[DllImport]`：**故意不存在的导出名** ⇒ 只证"库解析成功"，**绝不调用任何真函数**。
        /// （为什么不调真导出：Xlib 里没有"无参数无副作用"的现成函数；用不存在的导出名可以让运行时
        ///   在**解析成功后**立刻抛 `EntryPointNotFoundException`，恰好把"库解析"这一步单独隔离出来。）
        /// 声明在**本程序集内**才走本程序集的解析器。
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "WpfLinuxX11__SelfCheck_NoSuchExport")]
        private static extern void SelfCheckProbe();

        /// <summary>自证：把 <see cref="LibraryName"/> 当 `DllImport` 走一次，看它解析得到不到。</summary>
        private static bool X11NameResolves()
        {
            try
            {
                SelfCheckProbe();
                return true;   // 到不了（那个导出不存在）
            }
            catch (EntryPointNotFoundException)
            {
                return true;   // **库解析成功**、只是导出名不同 ⇒ 映射/默认探测已把库接上
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        /// <summary>诊断用：装解析器时**输掉了竞态**（别人先占了这个程序集的槽位）。没输 = false。</summary>
        internal static bool ResolverConflict { get; private set; }

        /// <summary>诊断用：**输掉竞态时**自证 `libX11.so.6` 是否解析成功（没输竞态 ⇒ 保持 false、自证未跑）。</summary>
        internal static bool SelfCheckX11NameResolved { get; private set; }

        /// <summary>Xlib 的 XID 类型在托管侧的对应（unsigned long）。</summary>
        public const int Success = 0;

        // ---------------- 连接 ----------------

        [DllImport(LibraryName, EntryPoint = "XOpenDisplay")]
        public static extern nint XOpenDisplay(string displayName);

        [DllImport(LibraryName, EntryPoint = "XCloseDisplay")]
        public static extern int XCloseDisplay(nint display);

        [DllImport(LibraryName, EntryPoint = "XFlush")]
        public static extern int XFlush(nint display);

        [DllImport(LibraryName, EntryPoint = "XSync")]
        public static extern int XSync(nint display, int discard);

        [DllImport(LibraryName, EntryPoint = "XFree")]
        public static extern int XFree(nint data);

        // ---------------- 屏幕 / 窗口 ----------------

        [DllImport(LibraryName, EntryPoint = "XDefaultScreen")]
        public static extern int XDefaultScreen(nint display);

        [DllImport(LibraryName, EntryPoint = "XRootWindow")]
        public static extern ulong XRootWindow(nint display, int screen);

        [DllImport(LibraryName, EntryPoint = "XBlackPixel")]
        public static extern ulong XBlackPixel(nint display, int screen);

        [DllImport(LibraryName, EntryPoint = "XWhitePixel")]
        public static extern ulong XWhitePixel(nint display, int screen);

        [DllImport(LibraryName, EntryPoint = "XCreateSimpleWindow")]
        public static extern ulong XCreateSimpleWindow(
            nint display, ulong parent, int x, int y, uint width, uint height,
            uint borderWidth, ulong border, ulong background);

        [DllImport(LibraryName, EntryPoint = "XDestroyWindow")]
        public static extern int XDestroyWindow(nint display, ulong window);

        [DllImport(LibraryName, EntryPoint = "XMapWindow")]
        public static extern int XMapWindow(nint display, ulong window);

        [DllImport(LibraryName, EntryPoint = "XUnmapWindow")]
        public static extern int XUnmapWindow(nint display, ulong window);

        [DllImport(LibraryName, EntryPoint = "XResizeWindow")]
        public static extern int XResizeWindow(nint display, ulong window, uint width, uint height);

        [DllImport(LibraryName, EntryPoint = "XStoreName")]
        public static extern int XStoreName(nint display, ulong window, string windowName);

        [DllImport(LibraryName, EntryPoint = "XSelectInput")]
        public static extern int XSelectInput(nint display, ulong window, long eventMask);

        [DllImport(LibraryName, EntryPoint = "XGetWindowAttributes")]
        public static extern int XGetWindowAttributes(nint display, ulong window, ref XWindowAttributes attributes);

        // ---------------- 事件 ----------------

        [DllImport(LibraryName, EntryPoint = "XPending")]
        public static extern int XPending(nint display);

        [DllImport(LibraryName, EntryPoint = "XNextEvent")]
        public static extern int XNextEvent(nint display, ref XEvent xevent);

        [DllImport(LibraryName, EntryPoint = "XSendEvent")]
        public static extern int XSendEvent(nint display, ulong window, int propagate, long eventMask, ref XEvent xevent);

        // ---------------- 原子与 WM 协议 ----------------

        [DllImport(LibraryName, EntryPoint = "XInternAtom")]
        public static extern ulong XInternAtom(nint display, string atomName, int onlyIfExists);

        [DllImport(LibraryName, EntryPoint = "XSetWMProtocols")]
        public static extern int XSetWMProtocols(nint display, ulong window, ulong[] protocols, int count);

        // ---------------- 绘图上下文 ----------------

        [DllImport(LibraryName, EntryPoint = "XCreateGC")]
        public static extern nint XCreateGC(nint display, ulong drawable, ulong valueMask, nint values);

        [DllImport(LibraryName, EntryPoint = "XFreeGC")]
        public static extern int XFreeGC(nint display, nint gc);

        // ---------------- 图像 ----------------

        [DllImport(LibraryName, EntryPoint = "XCreateImage")]
        public static extern nint XCreateImage(
            nint display, nint visual, uint depth, int format, int offset,
            nint data, uint width, uint height, int bitmapPad, int bytesPerLine);

        [DllImport(LibraryName, EntryPoint = "XPutImage")]
        public static extern int XPutImage(
            nint display, ulong drawable, nint gc, nint image,
            int srcX, int srcY, int destX, int destY, uint width, uint height);

        [DllImport(LibraryName, EntryPoint = "XDestroyImage")]
        public static extern int XDestroyImage(nint image);

        // ---------------- 错误 ----------------

        [DllImport(LibraryName, EntryPoint = "XGetErrorText")]
        public static extern int XGetErrorText(nint display, int code, byte[] bufferReturn, int length);

        /// <summary>
        /// Xlib 的错误处理器签名（<c>int (*XErrorHandler)(Display*, XErrorEvent*)</c>）。
        /// 返回 0 = 已处理，Xlib 不采取进一步动作。
        /// </summary>
        public delegate int XErrorHandler(nint display, nint errorEvent);

        /// <summary>
        /// 装一个错误处理器。
        ///
        /// 【为什么必须装】Xlib 的**默认**错误处理器做的事是：往 stderr 打印一行，
        /// 然后 **exit(1)** —— 也就是说"一次 BadWindow"会直接干掉整个进程，
        /// 既不抛异常也不返回错误码。Win32 那边失败了还能 `Marshal.GetLastWin32Error`，
        /// X11 这边失败了进程就没了。
        /// M7c 的接窗路径会拿外部给的 XID 去查属性（`XGetWindowAttributes`），
        /// 一个陈旧的/伪造的 XID 就足以触发它，所以这一层不能没有。
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "XSetErrorHandler")]
        public static extern nint XSetErrorHandler(XErrorHandler handler);

        /// <summary>上一行错误在 XErrorEvent 里的字段偏移：type@0 display@8 resourceid@16 serial@24 error_code@32 request_code@36 minor_code@40</summary>
        public const int XErrorEventErrorCodeOffset = 32;

        /// <summary>
        /// Xlib 的 **IO** 错误处理器签名（<c>int (*XIOErrorHandler)(Display*)</c>）。
        /// 与 <see cref="XErrorHandler"/> 是两回事：前者管"协议错误"（BadWindow 之类），
        /// 后者管"连接本身坏了"（server 挂掉、socket 断了）。
        /// </summary>
        public delegate int XIOErrorHandler(nint display);

        /// <summary>
        /// 装一个 IO 错误处理器。
        ///
        /// 【为什么必须装】Xlib 的**默认** IO 错误处理器比默认错误处理器更狠：它直接
        /// `exit(1)`，**不打印任何东西**。症状是"测试宿主进程崩溃"、日志里一个字都没有 ——
        /// 这正是本工程 M7c 阶段实测到的那次**间歇性无输出崩溃**（约 1/5）。
        /// 触发场景很现实：X server 上还有别的客户端刚异常退出（本工程的 HelloWpf runner
        /// 崩溃时会留下断掉的连接），此时 `XOpenDisplay`/`XCloseDisplay` 可能拿到一个
        /// 已经半死的连接 → IO 错误 → 整个进程消失。
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "XSetIOErrorHandler")]
        public static extern nint XSetIOErrorHandler(XIOErrorHandler handler);
    }

    /// <summary>libX11 的加载器。由 X11Native 的静态构造函数注册。</summary>
    internal static class X11LibraryResolver
    {
        /// <summary>libX11 加载失败时的完整诊断信息（供测试与报错用）。</summary>
        public static string LoadError { get; private set; }

        internal static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, X11Native.LibraryName, StringComparison.Ordinal))
                return nint.Zero;

            // 按 soname 从具体到宽泛地试；有些发行版只装 libX11.so（dev 包）。
            foreach (string candidate in new[] { "libX11.so.6", "libX11.so" })
            {
                if (NativeLibrary.TryLoad(candidate, out nint handle))
                {
                    LoadError = null;
                    return handle;
                }
            }

            LoadError =
                "加载 libX11 失败：已尝试 libX11.so.6 与 libX11.so。X11 窗口功能需要 Xlib " +
                "（Debian/Ubuntu: apt install libx11-6，RHEL/Fedora: dnf install libX11）。" +
                "无 X server 的测试请把用例标记为 Category=X11 并跳过。";

            throw new DllNotFoundException(LoadError);
        }
    }
}
