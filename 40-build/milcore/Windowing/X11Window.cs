// X11 窗口：创建 / 映射 / 呈现 / 事件泵。
//
// 【呈现为什么走 CPU 位图 + XPutImage】
//   handoff §2 决策 4：M1 只做软件渲染。Skia 的 GPU 后端（GRContext）需要 EGL/GLX
//   上下文，在 Xvfb 里要额外装 Mesa 且容易踩驱动坑。XPutImage 是把一块 CPU 内存
//   直接交给 X server 画到窗口上，依赖最少、行为最可预测，代价是每次 Present 有
//   一次内存拷贝 + 一次 X 请求——M1 不追求性能。
//
// 【像素打包为什么必须读 visual 的 RGB 掩码】
//   X server 只认"按 visual 掩码解释的整数像素值"。24 位深度下常见的是
//   0x00RRGGBB，但 16 位深度是 5-6-5、其他服务器也可能是 BGR 排布。写死
//   0x00RRGGBB 在本机（Xvfb depth 24）能跑，换个深度就整体偏色，而且偏色这件事
//   在"截屏比对"测试里才会暴露——到那时已经很难定位。所以从 XGetWindowAttributes
//   拿 visual，读 red/green/blue_mask，按掩码逐通道打包。
//
// 【M7c：包装一个**已经存在**的窗口】
//   WPF 托管层（HwndWrapper/HwndSource）自己建窗，XID 由 Win32 shim 下发（M7b 已实证
//   HWND == X11 XID）。呈现层要做的是"往那个窗口上画"，而不是再建一个窗口。
//   所以这里补上 `Wrap(display, windowId)`：
//     · **不** XCreateSimpleWindow（窗口已经存在）；
//     · **不** XSelectInput / XSetWMProtocols（事件掩码与 WM 协议是**窗口主人**的事——
//       抢过来会让 HwndWrapper 收不到输入；M7b 的 shim 已经按 WPF 需要选好了掩码）；
//     · **不** XDestroyWindow（Dispose 时只释放我们自己建的 GC）；
//     · 尺寸/深度/RGB 掩码仍然**从 server 读**（不猜）。
//   这是"包装既有窗口"与"自己建窗"的**唯一**语义差别，两者共用同一套呈现实现
//   （像素打包 / XPutImage / 双缓冲），没有复制粘贴。

// 【不支持的 visual】
//   PseudoColor / StaticColor / GrayScale 这类**索引色** visual 的掩码是 0，
//   无法直接算像素值（要先 XAllocColor 建 colormap）。M1 明确不支持，抛带原因的
//   异常而不是画出一屏乱码。Xvfb 默认与 CI 常用的都是 TrueColor/DirectColor。

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WpfGfx.Linux.Windowing
{
    /// <summary>一个 X11 顶层窗口。</summary>
    internal sealed class X11Window : IDisposable
    {
        private const int ZPixmap = 2;

        /// <summary>XImage.data 字段在结构体内的字节偏移（见 X11Structs.XImageHeader）。</summary>
        private const int XImageDataOffset = 16;

        private readonly X11Display _display;

        private nint _gc;
        private bool _mapped;
        private bool _disposed;

        /// <summary>
        /// 本对象是否**拥有**这个 X11 窗口的生命周期。
        /// 自己建的窗口 → true（Dispose 时 XDestroyWindow）；
        /// <see cref="Wrap"/> 包装的既有窗口 → false（只释放 GC，绝不动别人的窗口）。
        /// </summary>
        private bool _ownsWindow = true;

        private int _width;
        private int _height;

        // 像素打包用的 visual 掩码（构造期读一次，visual 不会变）。
        // 不用 readonly：两条构造路径（自己建窗 / Wrap 既有窗口）共用一个
        // InitializeFromServer()，而 readonly 字段不能在普通方法里赋值。
        private ulong _redMask;
        private ulong _greenMask;
        private ulong _blueMask;
        private int _depth;

        // XPutImage 的暂存缓冲区：pin 住复用，避免每帧一次 pinvoke 分配
        private uint[] _pixels = Array.Empty<uint>();
        private GCHandle _pixelsHandle;

        // SKImage → RGBA8888 的中转位图，同样复用
        private SKBitmap _staging;

        public X11Window(X11Display display, int width, int height,
            string title = "WPF on Linux", int x = 0, int y = 0)
        {
            _display = display ?? throw new ArgumentNullException(nameof(display));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            _width = width;
            _height = height;

            ulong background = X11Native.XWhitePixel(display.Handle, display.Screen);
            ulong border = X11Native.XBlackPixel(display.Handle, display.Screen);

            Id = X11Native.XCreateSimpleWindow(
                display.Handle, display.RootWindow, x, y,
                (uint)width, (uint)height, 0, border, background);

            if (Id == 0) throw new InvalidOperationException("XCreateSimpleWindow 返回 0，窗口创建失败");

            if (!string.IsNullOrEmpty(title)) X11Native.XStoreName(display.Handle, Id, title);

            InitializeFromServer(knowSize: true);

            X11Native.XSelectInput(display.Handle, Id, X11EventMask.DefaultMask);

            // 注册 WM_DELETE_WINDOW：没有它，点关闭按钮时窗口管理器会直接 XKillClient，
            // 上层收不到任何通知，没法优雅退出。
            if (display.AtomWmProtocols != 0 && display.AtomWmDeleteWindow != 0)
            {
                X11Native.XSetWMProtocols(
                    display.Handle, Id, new[] { display.AtomWmDeleteWindow }, 1);
            }

            _gc = X11Native.XCreateGC(display.Handle, Id, 0, IntPtr.Zero);
        }

        /// <summary>包装既有窗口用的私有构造。</summary>
        private X11Window(X11Display display, ulong existingWindowId)
        {
            _display = display ?? throw new ArgumentNullException(nameof(display));
            if (existingWindowId == 0)
                throw new ArgumentOutOfRangeException(nameof(existingWindowId));

            Id = existingWindowId;
            _ownsWindow = false;

            // 尺寸也从 server 读（调用方给的尺寸可能与 server 上的不一致，
            // 而"往哪个尺寸的窗口里画"必须以 server 为准）。
            InitializeFromServer(knowSize: false);

            _gc = X11Native.XCreateGC(display.Handle, Id, 0, IntPtr.Zero);
        }

        /// <summary>
        /// 包装一个**已存在**的 X11 窗口（M7c 接窗用）。
        /// <para>
        /// 语义边界（与"自己建窗"的差别，仅此三条）：
        /// 不创建、不销毁、不改事件掩码/ WM 协议。
        /// </para>
        /// </summary>
        /// <param name="display">X server 连接。所有权仍归调用方。</param>
        /// <param name="windowId">已存在的 X11 Window（XID）——M7b 里它就是 HWND。</param>
        internal static X11Window Wrap(X11Display display, ulong windowId)
            => new X11Window(display, windowId);

        /// <summary>
        /// 读 server 上的窗口属性（深度 / RGB 掩码 / 可选尺寸）。
        /// 自己建窗与包装既有窗口**共用这一份**，保证两条路径的像素打包行为完全一致。
        /// </summary>
        /// <param name="knowSize">
        /// true = 调用方已经设好 _width/_height（自己建窗的路径），不覆盖；
        /// false = 从 server 读实际尺寸（包装既有窗口的路径）。
        /// </param>
        private void InitializeFromServer(bool knowSize)
        {
            var attributes = default(XWindowAttributes);
            X11Display.ClearError();
            int ok = X11Native.XGetWindowAttributes(_display.Handle, Id, ref attributes);

            // 错误是**异步**到达的：XGetWindowAttributes 返回 0 之后，BadWindow 要到
            // 下一次 XSync/XFlush 才被 Xlib 分发。所以要显式 Sync 一次再取错误码，
            // 否则会拿到"上一次调用留下的"或"还没到"的状态。
            X11Native.XSync(_display.Handle, 0);
            int errorCode = X11Display.TakeError(out string errorText);

            if (ok == 0 || errorCode != 0)
            {
                // 不再让 Xlib 的默认处理器 exit(1) 把进程带走（见 X11Display 里的
                // 错误处理器注释）；这里把它变成一个**可捕获的托管异常**。
                throw new InvalidOperationException(
                    $"窗口 0x{Id:x} 不是一个有效的 X11 窗口：" +
                    $"XGetWindowAttributes 返回 {ok}，X 错误 = {errorCode}（{errorText ?? "无"}）。" +
                    "M7c 的接窗路径要求 HWND 就是真实的 XID——先用 " +
                    "WpfLinuxWin32_GetX11Window 确认它来自 Win32 shim。");
            }

            _depth = attributes.Depth;

            if (attributes.Visual != IntPtr.Zero)
            {
                XVisual visual = Marshal.PtrToStructure<XVisual>(attributes.Visual);
                _redMask = visual.RedMask;
                _greenMask = visual.GreenMask;
                _blueMask = visual.BlueMask;
            }

            if (_redMask == 0 || _greenMask == 0 || _blueMask == 0)
            {
                throw new NotSupportedException(
                    $"窗口 visual 的 RGB 掩码为 0（depth={_depth}），通常是 PseudoColor / " +
                    "GrayScale 这类索引色 visual。M1 只支持 TrueColor / DirectColor。" +
                    "请让 X server 使用 24 位 TrueColor（Xvfb :99 -screen 0 1280x1024x24）。");
            }

            if (!knowSize)
            {
                // 包装路径：以 server 报的尺寸为准（可能为 0 —— 调用方应先 Map/Resize）
                _width = Math.Max(1, attributes.Width);
                _height = Math.Max(1, attributes.Height);
            }
        }

        /// <summary>X11 窗口 id（XID）。</summary>
        public ulong Id { get; }

        public int Width => _width;

        public int Height => _height;

        public int Depth => _depth;

        public bool IsMapped => _mapped;

        /// <summary>本对象是否拥有窗口生命周期（Wrap 包装的窗口为 false）。</summary>
        public bool OwnsWindow => _ownsWindow;

        /// <summary>visual 的 RGB 掩码。暴露出来是为了让"像素打包"这件事可被断言。</summary>
        public (ulong Red, ulong Green, ulong Blue) Masks => (_redMask, _greenMask, _blueMask);

        public void Map()
        {
            ThrowIfDisposed();
            X11Native.XMapWindow(_display.Handle, Id);
            _mapped = true;
            X11Native.XFlush(_display.Handle);
        }

        public void Unmap()
        {
            ThrowIfDisposed();
            X11Native.XUnmapWindow(_display.Handle, Id);
            _mapped = false;
            X11Native.XFlush(_display.Handle);
        }

        public void SetTitle(string title)
        {
            ThrowIfDisposed();
            X11Native.XStoreName(_display.Handle, Id, title ?? string.Empty);
        }

        public void Resize(int width, int height)
        {
            ThrowIfDisposed();
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            X11Native.XResizeWindow(_display.Handle, Id, (uint)width, (uint)height);
            _width = width;
            _height = height;

            // Sync 保证 server 已经处理完 resize：否则紧接着的 XPutImage 可能画在
            // 还没变大的窗口上，被裁掉一块。
            X11Native.XSync(_display.Handle, 0);
        }

        /// <summary>
        /// 把一帧图像画到窗口上。帧尺寸与窗口尺寸不一致时，只呈现两者重叠的
        /// 左上角区域（X server 自己也会裁，这里显式算清楚，避免画到窗口外）。
        /// </summary>
        public void Present(SKImage frame)
        {
            ThrowIfDisposed();
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            int width = Math.Min(frame.Width, _width);
            int height = Math.Min(frame.Height, _height);
            if (width <= 0 || height <= 0) return;

            EnsureBuffers(width, height);
            ReadFrame(frame, width, height);
            BlitToWindow(width, height);

            X11Native.XFlush(_display.Handle);
        }

        public void Flush() => X11Native.XFlush(_display.Handle);

        public void Sync() => X11Native.XSync(_display.Handle, 0);

        // ================================================================
        //  事件
        // ================================================================

        /// <summary>
        /// 在**本连接**上订阅 Expose + 结构事件（ConfigureNotify/DestroyNotify）。
        /// 与 `SelectInput`（M1 那条"拥有窗口时全量选输入"的路径）分开：
        /// 包装既有窗口时**只**选这两类，既拿得到 resize/expose/closed，
        /// 又不碰输入掩码（输入归 Win32 shim 那条连接）。
        /// </summary>
        internal void SelectPresentationEvents()
        {
            ThrowIfDisposed();
            X11Native.XSelectInput(_display.Handle, Id,
                X11EventMask.ExposureMask | X11EventMask.StructureNotifyMask);
            X11Native.XFlush(_display.Handle);
        }

        public bool HasPendingEvents
        {
            get
            {
                ThrowIfDisposed();
                return X11Native.XPending(_display.Handle) > 0;
            }
        }

        /// <summary>非阻塞取一条事件。没有待处理事件时返回 false。</summary>
        public bool TryNextEvent(out WindowEvent windowEvent)
        {
            ThrowIfDisposed();

            if (X11Native.XPending(_display.Handle) <= 0)
            {
                windowEvent = default;
                return false;
            }

            var native = default(XEvent);
            X11Native.XNextEvent(_display.Handle, ref native);
            windowEvent = Translate(ref native);
            return true;
        }

        /// <summary>
        /// 请求关闭窗口：给自己发一条 WM_DELETE_WINDOW 的 ClientMessage。
        /// 走协议而不是直接 XDestroyWindow，是为了让关闭路径对"自己发"和"窗口
        /// 管理器发"两种情况完全一致——上层只需要处理一种 Closed 事件。
        /// </summary>
        public void RequestClose()
        {
            ThrowIfDisposed();

            var message = default(XEvent);
            message.Type = X11EventType.ClientMessage;
            message.Display = _display.Handle;
            message.Window = Id;
            message.MessageType = _display.AtomWmProtocols;
            message.Format = 32;
            message.Data0 = _display.AtomWmDeleteWindow;
            message.Data1 = 0; // CurrentTime

            X11Native.XSendEvent(_display.Handle, Id, 0, X11EventMask.NoEventMask, ref message);
            X11Native.XFlush(_display.Handle);
        }

        private WindowEvent Translate(ref XEvent native)
        {
            // 统一在**出口**补 WindowId：分支内部不必各自记得（漏一个就会让消费方误判窗口）。
            WindowEvent e = TranslateCore(ref native);
            return e.WindowId == 0 && native.Window != 0
                ? new WindowEvent
                {
                    Kind = e.Kind, WindowId = native.Window, X = e.X, Y = e.Y,
                    Width = e.Width, Height = e.Height, Detail = e.Detail,
                    State = e.State, RawType = e.RawType,
                }
                : e;
        }

        private WindowEvent TranslateCore(ref XEvent native)
        {
            switch (native.Type)
            {
                case X11EventType.Expose:
                    return new WindowEvent
                    {
                        Kind = WindowEventKind.Exposed,
                        X = native.ExposeX,
                        Y = native.ExposeY,
                        Width = native.ExposeWidth,
                        Height = native.ExposeHeight,
                    };

                case X11EventType.ConfigureNotify:
                    // 以 server 报的尺寸为准，而不是我们请求的尺寸。
                    // ⚠️ **这里刻意不写 `_width/_height`**：这条 XEvent 的 `window` 字段才是
                    //    归属依据，而"谁抽到这条事件"**不是** —— 队列是**连接级**的（见
                    //    `WindowEvent.WindowId` 的注释）。旧代码在这里无条件写缓存尺寸，
                    //    正是缺陷 A：弹窗的 ConfigureNotify 被记到了抽事件的**主窗口**头上
                    //    （详见 `ApplyOwnConfigureNotify()` 的注释与现场读数）。
                    //    缓存尺寸的唯一写点是 `ApplyOwnConfigureNotify()`，它在**核实 XID 之后**才写。
                    return new WindowEvent
                    {
                        Kind = WindowEventKind.Resized,
                        X = native.ConfigureX,
                        Y = native.ConfigureY,
                        Width = native.ConfigureWidth,
                        Height = native.ConfigureHeight,
                    };

                case X11EventType.KeyPress:
                case X11EventType.KeyRelease:
                    return new WindowEvent
                    {
                        Kind = native.Type == X11EventType.KeyPress
                            ? WindowEventKind.KeyPressed
                            : WindowEventKind.KeyReleased,
                        Detail = native.Detail,
                        State = native.State,
                        X = native.PointerX,
                        Y = native.PointerY,
                    };

                case X11EventType.ButtonPress:
                case X11EventType.ButtonRelease:
                    return new WindowEvent
                    {
                        Kind = native.Type == X11EventType.ButtonPress
                            ? WindowEventKind.MouseButtonPressed
                            : WindowEventKind.MouseButtonReleased,
                        Detail = native.Detail,
                        State = native.State,
                        X = native.PointerX,
                        Y = native.PointerY,
                    };

                case X11EventType.MotionNotify:
                    return new WindowEvent
                    {
                        Kind = WindowEventKind.PointerMoved,
                        State = native.State,
                        X = native.PointerX,
                        Y = native.PointerY,
                    };

                case X11EventType.ClientMessage:
                    // 只认 WM_PROTOCOLS + WM_DELETE_WINDOW 这一条关闭协议。
                    if (_display.AtomWmProtocols != 0 &&
                        native.MessageType == _display.AtomWmProtocols &&
                        _display.AtomWmDeleteWindow != 0 &&
                        native.Data0 == _display.AtomWmDeleteWindow)
                    {
                        return new WindowEvent { Kind = WindowEventKind.Closed };
                    }

                    return new WindowEvent { Kind = WindowEventKind.Unknown, RawType = native.Type };

                case X11EventType.DestroyNotify:
                    return new WindowEvent { Kind = WindowEventKind.Closed };

                default:
                    return new WindowEvent { Kind = WindowEventKind.Unknown, RawType = native.Type };
            }
        }

        // ================================================================
        //  呈现内部实现
        // ================================================================

        /// <summary>
        /// 把一条 ConfigureNotify 的尺寸记进本窗口的缓存尺寸（<see cref="Width"/>/<see cref="Height"/> 的来源）。
        /// <para>
        /// **归属判据 = 事件自己的 <see cref="WindowEvent.WindowId"/>**：X11 的事件队列是
        /// **连接级**的，`XNextEvent` 会把整条连接上任何窗口的事件取出来，所以
        /// "抽事件的那扇窗"与"事件真正的主人"**可以不是同一扇窗**。不满足归属的事件一律**拒绝**。
        /// </para>
        /// <para>
        /// 【这条拒绝就是缺陷 A 的修法本身】`D-G54` 实测：弹窗（`0x200008`）被配置成
        /// `413x274` 时，它的 ConfigureNotify 是在**主窗口**（`0x200004`）那次抽事件里被取走的，
        /// 旧代码（`TranslateCore` 里那句无条件写）把 `413x274` 写进了**主窗口**的缓存 ⇒
        /// 呈现层判定"X 尺寸变了"并用**弹窗**的尺寸重画了**主窗口**。现场逐字
        /// （`$HOME/hc-fo3-mil.log:7885-7886`）：
        /// <code>
        /// NOTE X11 Resize 生效：HWND 0x200004 800x600 → 413x274（按新尺寸重渲）
        ///   ★ 呈现尺寸变化（免采样）：通道 2 → HWND 0x200004 已呈现 413x274（skia 指令 439 条）
        /// </code>
        /// </para>
        /// <para>
        /// **本方法是 `_width`/`_height` 的唯一写点**（除 `Resize()` 这个"我们自己请求的"路径外）：
        /// `X11Window` 是 `internal`，全仓的事件消费者只有 `MilPresentation.PumpWindowEvents()`
        /// 一处，所以"唯一写点"是可枚举、可核对的。
        /// </para>
        /// </summary>
        /// <param name="ev">刚从连接上取到的事件；调用方按 XID 找到本窗口后原样传入。</param>
        /// <returns>真的记下了（Kind 是 Resized 且归属成立）时为 true；否则 false。</returns>
        internal bool ApplyOwnConfigureNotify(in WindowEvent ev)
        {
            if (ev.Kind != WindowEventKind.Resized) return false;

            // 归属判据：`WindowId == 0` = 事件里没有窗口字段（`Translate` 只在 `native.Window != 0`
            // 时填），那按"本窗口自己的事件"处理；**只要填了窗口就不是本窗口的一律拒绝**。
            if (ev.WindowId != 0 && ev.WindowId != Id) return false;

            _width = ev.Width;
            _height = ev.Height;
            return true;
        }

        private void EnsureBuffers(int width, int height)
        {
            if (_pixels.Length < width * height)
            {
                if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
                _pixels = new uint[width * height];
                _pixelsHandle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            }

            if (_staging == null || _staging.Width != width || _staging.Height != height)
            {
                _staging?.Dispose();
                _staging = new SKBitmap(new SKImageInfo(
                    width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            }
        }

        /// <summary>SKImage → 暂存位图（RGBA8888 预乘）。</summary>
        private void ReadFrame(SKImage frame, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (!frame.ReadPixels(info, _staging.GetPixels(), _staging.RowBytes, 0, 0))
            {
                throw new InvalidOperationException(
                    "SKImage.ReadPixels 失败，无法把帧读进 CPU 位图（帧可能来自 GPU 后端）");
            }
        }

        /// <summary>暂存位图 → X 像素缓冲区 → XPutImage。</summary>
        private unsafe void BlitToWindow(int width, int height)
        {
            // 窗口背景色（XCreateSimpleWindow 用的是 XWhitePixel）。
            const byte bgR = 255, bgG = 255, bgB = 255;

            int redShift = BitOperations.TrailingZeroCount(_redMask);
            int greenShift = BitOperations.TrailingZeroCount(_greenMask);
            int blueShift = BitOperations.TrailingZeroCount(_blueMask);
            int redBits = BitOperations.PopCount(_redMask);
            int greenBits = BitOperations.PopCount(_greenMask);
            int blueBits = BitOperations.PopCount(_blueMask);

            byte* stagingBase = (byte*)_staging.GetPixels().ToPointer();

            for (int y = 0; y < height; y++)
            {
                uint* row = (uint*)(stagingBase + (nint)y * _staging.RowBytes);
                int destRow = y * width;

                for (int x = 0; x < width; x++)
                {
                    uint src = row[x];

                    // RGBA8888 小端：0xAABBGGRR
                    byte r = (byte)(src & 0xFF);
                    byte g = (byte)((src >> 8) & 0xFF);
                    byte b = (byte)((src >> 16) & 0xFF);
                    byte a = (byte)((src >> 24) & 0xFF);

                    if (a != 255)
                    {
                        if (a == 0)
                        {
                            r = bgR; g = bgG; b = bgB;
                        }
                        else
                        {
                            // 预乘 → 直接通道，再与窗口背景做 source-over 合成。
                            // XPutImage 是覆盖式写入，半透明必须自己合，否则透明区变黑。
                            r = (byte)((r * 255 / a) * a / 255 + bgR * (255 - a) / 255);
                            g = (byte)((g * 255 / a) * a / 255 + bgG * (255 - a) / 255);
                            b = (byte)((b * 255 / a) * a / 255 + bgB * (255 - a) / 255);
                        }
                    }

                    _pixels[destRow + x] = (uint)(
                        (Scale(r, redBits) << redShift) |
                        (Scale(g, greenBits) << greenShift) |
                        (Scale(b, blueBits) << blueShift));
                }
            }

            nint image = X11Native.XCreateImage(
                _display.Handle,
                VisualOfWindow(),
                (uint)_depth,
                ZPixmap,
                0,
                _pixelsHandle.AddrOfPinnedObject(),
                (uint)width, (uint)height,
                32,   // bitmap_pad：32 位对齐，与 Xvfb 的 scanline_pad 一致
                0);   // bytes_per_line = 0 → 由 Xlib 按 depth/bpp 计算

            if (image == IntPtr.Zero) throw new InvalidOperationException("XCreateImage 返回 NULL");

            try
            {
                X11Native.XPutImage(
                    _display.Handle, Id, _gc, image, 0, 0, 0, 0, (uint)width, (uint)height);
            }
            finally
            {
                // XDestroyImage 会 XFree 掉 image->data，而那块内存是托管数组。
                // 先把 data 置空再销毁，否则 Xlib 会对 GC 堆指针调 free —— 直接崩。
                Marshal.WriteIntPtr(image, XImageDataOffset, IntPtr.Zero);
                X11Native.XDestroyImage(image);
            }
        }

        /// <summary>8 位通道值 → visual 指定位宽的通道值。</summary>
        private static ulong Scale(byte channel, int bits)
        {
            if (bits >= 8) return (ulong)channel << (bits - 8);
            return (ulong)channel >> (8 - bits);
        }

        private nint VisualOfWindow()
        {
            var attributes = default(XWindowAttributes);
            X11Native.XGetWindowAttributes(_display.Handle, Id, ref attributes);
            return attributes.Visual;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(X11Window));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_gc != IntPtr.Zero)
            {
                X11Native.XFreeGC(_display.Handle, _gc);
                _gc = IntPtr.Zero;
            }

            // 只销毁**自己建的**窗口。包装来的窗口属于上层（WPF 的 HwndWrapper），
            // 由它通过 Win32 shim 的 DestroyWindow 关闭；这里销毁会把它偷偷拆掉。
            if (_ownsWindow)
            {
                X11Native.XDestroyWindow(_display.Handle, Id);
                X11Native.XSync(_display.Handle, 0);
            }

            _staging?.Dispose();
            _staging = null;

            if (_pixelsHandle.IsAllocated) _pixelsHandle.Free();
            _pixels = Array.Empty<uint>();
        }
    }
}
