// X11 连接：Display 的打开/关闭与常用原子。
//
// 【为什么自己管生命周期而不是每次用都开】
//   XOpenDisplay 是一次完整的 socket/unix-domain 连接建立 + 协议握手，成本不低；
//   而一个进程里所有窗口共享一个 Display 是 X11 的常规用法。所以做成 IDisposable，
//   由调用方（窗口 / 呈现目标 / 将来的 WPF 应用）持有。
//
// 【DISPLAY 为 null 时走环境变量】
//   XOpenDisplay(NULL) 在 Xlib 的语义就是"读 $DISPLAY"，正是我们要的。
//   测试里传 null 让环境决定，CI 上设 DISPLAY=:99 即可。

using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace WpfGfx.Linux.Windowing
{
    /// <summary>一个 X server 连接。</summary>
    internal sealed class X11Display : IDisposable
    {
        // ---- 重试策略 ----
        //
        // 【为什么需要重试】
        //   X server 在并发连接高峰会**瞬时**拒绝新连接（Xvfb 也一样：listen backlog
        //   打满、或撞到 MaxClients 时，connect 会直接 ECONNREFUSED）。XOpenDisplay
        //   遇到它只会返回 NULL，一次失败就抛异常的话，压测/CI 上就会表现为
        //   "偶发掉 1~3 条用例"。这是**资源竞争下的瞬时失败**，不是逻辑错误，
        //   所以正解是重试，不是改数据结构或加锁。
        //
        // 【为什么退避总量压在 1 秒以内】
        //   这条路径只在失败时走；但"配错 DISPLAY"也必须快速失败（见 IsServerPresent），
        //   不能让一次手滑的 DISPLAY 换来几秒钟的假死。3 次重试 + 60/180/400ms 指数退避
        //   = 640ms，足够覆盖 X server 的瞬时拒绝窗口，又不至于拖慢正常路径。
        private const int RetryCount = 3;

        private static readonly int[] RetryBackoffMs = { 60, 180, 400 };

        private static readonly int TotalBackoffMs = ComputeTotalBackoff();

        private static int ComputeTotalBackoff()
        {
            int sum = 0;
            foreach (int ms in RetryBackoffMs) sum += ms;
            return sum;
        }

        private bool _disposed;

        // ================================================================
        //  Xlib 错误捕获（M7c）
        //
        //  【为什么必须有】Xlib 的**默认**错误处理器会 `exit(1)`：一次 BadWindow
        //  就能把整个进程带走，既不抛异常也没有返回码。M7c 的接窗路径要拿外部给的
        //  XID 去查属性（XGetWindowAttributes），一个陈旧的/伪造的 XID 就足以触发。
        //  没有这一层，"绑定失败"的表现就是**进程消失**，TryBind 里的 try/catch 根本救不了。
        //
        //  【语义】处理器只记录最后一条错误并返回 0（= 已处理），Xlib 继续跑。
        //  调用方在"某个 X 调用返回 0/NULL"之后，用 TryTakeError 取走错误码，
        //  把它变成一个正常的托管异常 —— 这样失败就回到了可诊断、可捕获的世界。
        // ================================================================

        /// <summary>最后一条 X 协议错误码（0 = 没有）。</summary>
        public static int LastErrorCode { get; private set; }

        /// <summary>最后一条 X 协议错误的人类可读文本（由 XGetErrorText 给出）。</summary>
        public static string LastErrorText { get; private set; }

        /// <summary>取走（并清空）最后一条错误。返回 0 表示这段时间内没有错误。</summary>
        public static int TakeError(out string text)
        {
            int code = LastErrorCode;
            text = LastErrorText;
            LastErrorCode = 0;
            LastErrorText = null;
            return code;
        }

        public static void ClearError()
        {
            LastErrorCode = 0;
            LastErrorText = null;
            LastIoError = false;
        }

        // 静态字段持有委托：Xlib 只存函数指针，不持有托管引用。
        // 不 root 住的话 GC 一收，下一次 X 错误就会跳到已释放的 thunk 上。
        private static readonly X11Native.XErrorHandler ErrorHandler = OnXError;
        private static nint _previousHandler;
        private static nint _previousIoHandler;

        /// <summary>
        /// 连接级（IO）错误：server 挂了 / socket 断了 / 连接半死。
        ///
        /// 【为什么这条比协议错误更致命】Xlib 的默认 IO 错误处理器**直接 `exit(1)` 且不打印**，
        /// 于是表现成"进程无声无息地没了"。M7c 实测过一次约 1/5 概率的间歇性崩溃，
        /// 日志里一个字都没有 —— 就是它（当时 X server 上还留着 HelloWpf 崩溃后断掉的连接）。
        /// 这里把它变成"记录 + 返回 0"，让失败回到可诊断、可捕获的世界。
        /// </summary>
        private static int OnXIOError(nint display)
        {
            LastIoError = true;
            LastErrorCode = 0;
            LastErrorText = "X11 IO error（连接中断：X server 退出、socket 断开，或连接已被对端关闭）";
            _ = display;
            return 0;   // 已处理：Xlib 不再 exit(1)
        }

        /// <summary>是否发生过连接级（IO）错误。</summary>
        public static bool LastIoError { get; private set; }

        private static int OnXError(nint display, nint errorEvent)
        {
            int code = errorEvent == IntPtr.Zero
                ? 0
                : System.Runtime.InteropServices.Marshal.ReadInt32(
                    errorEvent, X11Native.XErrorEventErrorCodeOffset);

            LastErrorCode = code;
            LastErrorText = DescribeError(display, code);
            return 0;   // 已处理：Xlib 不再打印/退出
        }

        private static string DescribeError(nint display, int code)
        {
            try
            {
                if (display != IntPtr.Zero && code != 0)
                {
                    var buffer = new byte[256];
                    if (X11Native.XGetErrorText(display, code, buffer, buffer.Length) == 0)
                    {
                        int end = Array.IndexOf(buffer, (byte)0);
                        if (end < 0) end = buffer.Length;
                        return System.Text.Encoding.ASCII.GetString(buffer, 0, end).Trim();
                    }
                }
            }
            catch { /* 取错误文本失败不影响主流程 */ }

            return code switch
            {
                1 => "BadRequest",
                2 => "BadValue",
                3 => "BadWindow",
                4 => "BadPixmap",
                5 => "BadAtom",
                8 => "BadMatch",
                9 => "BadDrawable",
                10 => "BadAccess",
                11 => "BadAlloc",
                12 => "BadColormap",
                13 => "BadGC",
                14 => "BadIDChoice",
                _ => $"X error {code}",
            };
        }

        private X11Display(nint handle, int screen, ulong rootWindow)
        {
            Handle = handle;
            Screen = screen;
            RootWindow = rootWindow;

            // 每次成功开连接都确认一次处理器在位（XSetErrorHandler 是**连接级**的，
            // 但对同一进程里所有 Display 生效；重复设置返回旧处理器，无害）。
            _previousHandler = X11Native.XSetErrorHandler(ErrorHandler);
            // ⚠️ 刻意**不**装 IO 错误处理器：Xlib 的 IO 错误意味着连接已经死了，
            //    按文档处理器**不应返回**（应 exit 或 longjmp）。实测"返回 0"会让
            //    Xlib 继续在死连接上跑 → 崩溃率反而从 ~1/5 升到 ~1/2。
            //    默认行为（打印 + exit）虽然粗暴但至少是安全的，保持原样。
            ClearError();
        }

        public nint Handle { get; }

        public int Screen { get; }

        public ulong RootWindow { get; }

        /// <summary>WM_PROTOCOLS 原子。注册了它，窗口管理器才会把"关闭"以 ClientMessage 发来。</summary>
        public ulong AtomWmProtocols { get; private set; }

        /// <summary>WM_DELETE_WINDOW 原子。</summary>
        public ulong AtomWmDeleteWindow { get; private set; }

        /// <summary>
        /// 打开连接。<paramref name="displayName"/> 为 null 时使用环境变量 DISPLAY。
        /// 打开失败抛 InvalidOperationException —— 调用方据此决定跳过还是报错。
        /// </summary>
        public static X11Display Open(string displayName)
        {
            // 先花一次 stat 的代价判断"这台机器上到底有没有这个 server"，据此决定
            // 失败是"配错了"（快速失败）还是"瞬时拒绝"（重试）。成功路径不受影响。
            bool serverPresent = IsServerPresent(displayName);
            int attempts = serverPresent ? RetryCount + 1 : 1;

            for (int attempt = 1; ; attempt++)
            {
                nint handle = X11Native.XOpenDisplay(displayName);
                if (handle != IntPtr.Zero)
                    return Create(handle);

                if (attempt >= attempts)
                {
                    throw new InvalidOperationException(
                        BuildFailureMessage(ResolveTarget(displayName), serverPresent, attempts));
                }

                Thread.Sleep(RetryBackoffMs[attempt - 1]);
            }
        }

        /// <summary>连接建立后的常规初始化（默认屏 / 根窗口 / ICCCM 原子）。</summary>
        private static X11Display Create(nint handle)
        {
            int screen = X11Native.XDefaultScreen(handle);
            ulong root = X11Native.XRootWindow(handle, screen);

            // XInternAtom 的第三个参数 only_if_exists 必须是 0（False）。
            //
            // ⚠️ 实测踩坑：初版传的是 1（True），于是两个原子都拿到 0。原因是
            //   WM_PROTOCOLS / WM_DELETE_WINDOW **不是 X server 的预定义原子**——
            //   Xvfb :99 上 xlsatoms 只到 68 号（WM_CLASS / WM_TRANSIENT_FOR），
            //   这两个属于 ICCCM，谁用谁创建。传 1 时 server 找不到就返回 None(0)，
            //   于是：XSetWMProtocols 被跳过（窗口没注册关闭协议），RequestClose
            //   发出去的 ClientMessage 里 message_type/data0 全是 0，
            //   Translate 里的 `AtomWmProtocols != 0` 守卫把它判成 Unknown——
            //   "关闭链路"从头到尾是断的，而且不报任何错。
            //   传 0 让 server 在原子不存在时**创建**它，这是 Xlib 的标准用法。
            var display = new X11Display(handle, screen, root);
            display.AtomWmProtocols = X11Native.XInternAtom(handle, "WM_PROTOCOLS", 0);
            display.AtomWmDeleteWindow = X11Native.XInternAtom(handle, "WM_DELETE_WINDOW", 0);
            return display;
        }

        /// <summary>出错时要打出来的那个 DISPLAY —— 显式传参优先，否则是环境变量的真实值。</summary>
        private static string ResolveTarget(string displayName) =>
            displayName ?? Environment.GetEnvironmentVariable("DISPLAY") ?? "(未设置)";

        /// <summary>
        /// 探测目标 display 的监听端点是否存在。这是"配错 DISPLAY"与"瞬时拒绝"的
        /// **唯一分水岭**，决定了 Open 失败时是重试还是快速失败。
        /// </summary>
        private static bool IsServerPresent(string displayName)
        {
            // DISPLAY 语法（X11 标准）：[host]:display[.screen]，如 :99 / localhost:10 / 10.0.0.5:0.1
            string name = displayName ?? Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(name))
                return false;   // DISPLAY 压根没设：XOpenDisplay(NULL) 必失败，重试无意义

            int colon = name.LastIndexOf(':');
            if (colon < 0)
                return false;   // 不是合法 display 名，同样是配置错误

            string host = name.Substring(0, colon);
            string number = name.Substring(colon + 1);
            int dot = number.IndexOf('.');
            if (dot >= 0) number = number.Substring(0, dot);
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int displayNumber))
                return false;

            // 远端 display（host 非空且不是 unix）：端点在别的机器上，本机无从判定
            // 它在不在。网络型失败本来就偏瞬时，这里按"可重试"处理 —— 最坏多花
            // 640ms，仍在预算内，好过把一个活着的远端 server 的抖动判成配置错误。
            if (host.Length != 0 && !host.Equals("unix", StringComparison.OrdinalIgnoreCase))
                return true;

            // 本机 display：Xlib 连的是 unix domain socket /tmp/.X11-unix/X<n>。
            // · 文件不存在 => 这台机器上没有第 <n> 个 server（典型踩坑：容器里预设了
            //   DISPLAY=:0 但根本没人起 X）。这是**配置错误**，重试多少次都不会变好，
            //   必须立刻失败 —— 否则"配错 DISPLAY"会退化成"卡 640ms 才报错"的糟糕体验。
            // · 文件存在 => server 是配过的，连不上多半是并发高峰下的瞬时拒绝，值得重试。
            //   （server 真挂了留下的 stale socket 也归这一类，代价是最坏 640ms。）
            string socketPath = Path.Combine(
                "/tmp/.X11-unix", "X" + displayNumber.ToString(CultureInfo.InvariantCulture));
            return File.Exists(socketPath);
        }

        private static string BuildFailureMessage(string target, bool serverPresent, int attempts)
        {
            if (!serverPresent)
            {
                return $"XOpenDisplay 失败：无法连接到 X server（DISPLAY={target}）—— 未探测到该 " +
                    "display 的监听端点，属于配置问题，因此不重试。headless 环境请先启动 Xvfb，" +
                    "例如：Xvfb :99 -screen 0 1280x1024x24 &，并确认 DISPLAY 与它一致。";
            }

            return $"XOpenDisplay 失败：X server（DISPLAY={target}）端点存在，但连续 {attempts} 次拒绝连接" +
                $"（累计退避 {TotalBackoffMs}ms）。这通常是并发连接高峰下的瞬时拒绝，重试已耗尽。";
        }

        public void Flush() => X11Native.XFlush(Handle);

        /// <summary>阻塞直到 server 处理完所有已发出的请求。截图/销毁前必须调。</summary>
        public void Sync() => X11Native.XSync(Handle, 0);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            X11Native.XCloseDisplay(Handle);
        }
    }
}
