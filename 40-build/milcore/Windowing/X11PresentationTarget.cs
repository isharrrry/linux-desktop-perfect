// IPresentationTarget 的 X11 实现。
//
// 【这个类是 T5 对外的全部】
//   handoff §3.4 把 IPresentationTarget 定为三个跨组接口之一。T9（端到端）只需要
//   对着这个接口编程：拿 NativeHandle 当 HWND 的替身，拿 Width/Height 做布局，
//   渲染完把 SKImage 交给 Present。X11 的细节全关在这一个文件后面。
//
// 【Display 的所有权】
//   构造期传入的 X11Display **不归本对象所有**——一个进程通常只开一个连接，
//   多个窗口共享它。谁开的谁关，避免"关掉一个窗口把整条连接掐了"。
//
// 【Resize 之后上层要重新渲染】
//   X11 的 resize 是 server 侧动作，窗口内容不会自动缩放。上层收到
//   WindowEventKind.Resized 之后必须按新尺寸重渲一帧再 Present，否则窗口里
//   会留着旧尺寸的图 + 一块空白。这一点写在文档注释里，因为它不是"实现细节"，
//   而是使用契约的一部分。

using System;
using SkiaSharp;
using WpfGfx.Linux.Contracts;

namespace WpfGfx.Linux.Windowing
{
    /// <summary>把 SKImage 呈现到一个真实 X11 窗口上。</summary>
    internal sealed class X11PresentationTarget : IPresentationTarget, IDisposable
    {
        private readonly X11Window _window;
        private bool _disposed;

        /// <param name="display">X server 连接。所有权仍归调用方。</param>
        public X11PresentationTarget(X11Display display, int width, int height,
            string title = "WPF on Linux", int x = 0, int y = 0)
        {
            _window = new X11Window(display, width, height, title, x, y);
        }

        private X11PresentationTarget(X11Window window) => _window = window;

        /// <summary>
        /// **包装一个已存在的 X11 窗口**（M7c 接窗）。
        ///
        /// 【为什么需要这条路径】
        ///   WPF 托管层的窗口是它自己建的（`HwndWrapper` → Win32 shim 的
        ///   `CreateWindowEx` → `XCreateSimpleWindow`），XID 就是 HWND（M7b 实证）。
        ///   呈现层不能"再建一个窗口"，只能往**那个**窗口上画。
        ///
        /// 【语义差别只有三条，且都在 X11Window.Wrap 里】
        ///   不创建 / 不销毁 / 不改事件掩码与 WM 协议。呈现实现（像素打包、XPutImage、
        ///   双缓冲）完全共用，所以"画上去的像素"与 M1 既有的窗口路径逐字节一致。
        ///
        /// 【所有权】
        ///   `Dispose` 只释放 GC，**不销毁窗口**——那归 `DestroyWindow` 管。
        /// </summary>
        /// <param name="display">X server 连接。所有权仍归调用方。</param>
        /// <param name="windowId">已存在的 X11 Window（XID）。</param>
        internal static X11PresentationTarget WrapExisting(X11Display display, ulong windowId)
            => new X11PresentationTarget(X11Window.Wrap(display, windowId));

        /// <summary>本目标是否拥有窗口生命周期（WrapExisting 建的为 false）。</summary>
        internal bool OwnsWindow => _window.OwnsWindow;

        /// <summary>X11 窗口 id。</summary>
        public ulong WindowId => _window.Id;

        /// <summary>
        /// 契约要求的原生句柄。WPF 的 HwndTarget 拿 HWND 当窗口身份，
        /// 在 Linux 上 X11 Window（XID）就是它的对等物。
        /// </summary>
        public nint NativeHandle => (nint)_window.Id;

        public int Width => _window.Width;

        public int Height => _window.Height;

        public bool IsMapped => _window.IsMapped;

        /// <summary>底层窗口，需要订阅事件时用。</summary>
        public X11Window Window => _window;

        public void Resize(int w, int h)
        {
            ThrowIfDisposed();
            _window.Resize(w, h);
        }

        public void Present(SKImage frame)
        {
            ThrowIfDisposed();
            _window.Present(frame);
        }

        /// <summary>
        /// 为**本呈现连接**订阅 Expose / 结构事件（M7c 轨道 C）。
        ///
        /// 【为什么可以这么做而不打扰 Win32 shim —— 这是 X11 的一条硬性质】
        ///   **事件掩码是每个客户端一份的**：同一个窗口在不同 X 连接上各有自己的
        ///   `XSelectInput` 掩码，互不覆盖。shim 那条连接选的是输入类事件
        ///   （KeyPress/PointerMotion/…），本层只加 `ExposureMask | StructureNotifyMask`
        ///   ⇒ shim 的输入分发一字不改，`WrapExisting` 那句"不改事件掩码"依然成立
        ///   （它指的是**不动别人的**掩码）。
        ///
        /// 【为什么需要它】债务 #3：X11 的 resize 是 **server 侧**动作 ——
        ///   窗口变大后多出来的那块是 X 的窗口底色，**内容不会自己重画**。
        ///   没有这条订阅，"接窗"就只是一次性的贴图。
        /// </summary>
        internal void EnableEventNotifications()
        {
            ThrowIfDisposed();
            _window.SelectPresentationEvents();
        }

        /// <summary>映射（显示）窗口。构造后、首次 Present 前调用。</summary>
        public void Map()
        {
            ThrowIfDisposed();
            _window.Map();
        }

        public void Unmap()
        {
            ThrowIfDisposed();
            _window.Unmap();
        }

        /// <summary>等 X server 处理完所有请求。截图/退出前调用。</summary>
        public void Sync() => _window.Sync();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(X11PresentationTarget));
        }
    }
}
