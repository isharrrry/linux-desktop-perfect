// X11 事件 → 上层可消费的最小事件抽象。
//
// 【为什么要有这一层】
//   XEvent 是一个 192 字节的 union，字段偏移随事件类型而变，直接把它暴露给上层
//   等于把 Xlib 的 ABI 细节泄漏到整个渲染栈。上层（将来的 T9 托管层接入）只需要
//   知道"要重绘了 / 尺寸变了 / 按了什么键 / 点在哪 / 该关了"。
//
// 【M1 的范围】
//   handoff §5 给 T5 定的 M1 目标是"能开窗口、能重绘、能关闭"，对应 Exposed /
//   Resized / Closed 三类。键鼠事件的**转换代码**一并写在这里（成本几乎为零），
//   但**没有测试覆盖**——要真正验证得用 XTest 扩展注入输入事件，M1 不做。
//   这一点在交付报告里如实标注，不冒充已验证。

using System;

namespace WpfGfx.Linux.Windowing
{
    /// <summary>X11 事件类型常量（X.h）。</summary>
    internal static class X11EventType
    {
        public const int KeyPress = 2;
        public const int KeyRelease = 3;
        public const int ButtonPress = 4;
        public const int ButtonRelease = 5;
        public const int MotionNotify = 6;
        public const int Expose = 12;
        public const int DestroyNotify = 17;
        public const int MapNotify = 19;
        public const int ConfigureNotify = 22;
        public const int ClientMessage = 33;
    }

    /// <summary>X11 事件选择掩码（X.h）。</summary>
    internal static class X11EventMask
    {
        public const long NoEventMask = 0L;
        public const long KeyPressMask = 1L << 0;
        public const long KeyReleaseMask = 1L << 1;
        public const long ButtonPressMask = 1L << 2;
        public const long ButtonReleaseMask = 1L << 3;
        public const long PointerMotionMask = 1L << 6;
        public const long ExposureMask = 1L << 15;
        public const long StructureNotifyMask = 1L << 17;

        /// <summary>M1 订阅的全部事件。</summary>
        public const long DefaultMask =
            ExposureMask | StructureNotifyMask | KeyPressMask | KeyReleaseMask |
            ButtonPressMask | ButtonReleaseMask | PointerMotionMask;
    }

    internal enum WindowEventKind
    {
        /// <summary>未能识别的事件。保留原始 type 便于排查。</summary>
        Unknown = 0,

        /// <summary>Expose：窗口露出，需要重绘。</summary>
        Exposed,

        /// <summary>ConfigureNotify：尺寸/位置变化。</summary>
        Resized,

        KeyPressed,
        KeyReleased,
        MouseButtonPressed,
        MouseButtonReleased,
        PointerMoved,

        /// <summary>收到 WM_DELETE_WINDOW，或窗口已被销毁。</summary>
        Closed,
    }

    internal readonly struct WindowEvent
    {
        public WindowEventKind Kind { get; init; }

        /// <summary>
        /// 事件属于哪扇窗（XID）。**这不是冗余信息**：X11 的事件队列是**连接级**的，
        /// `XNextEvent` 会把整条连接上任何窗口的事件都取出来 —— 所以"在这扇窗上抽事件"
        /// 实际可能抽到**别的窗口**的事件（实测踩过：上一扇窗销毁事件让当前目标被误判成 Closed）。
        /// 消费方必须按本字段过滤。
        /// </summary>
        public ulong WindowId { get; init; }

        /// <summary>Expose 的脏矩形；ConfigureNotify 时是新尺寸。</summary>
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>键码（X11 keycode）或鼠标键号（1=左 2=中 3=右）。</summary>
        public uint Detail { get; init; }

        /// <summary>修饰键状态（Shift/Control/Alt 等 X11 位掩码）。</summary>
        public uint State { get; init; }

        /// <summary>未能识别时的原始 X11 事件类型，用于诊断。</summary>
        public int RawType { get; init; }

        public override string ToString() =>
            Kind == WindowEventKind.Unknown
                ? $"Unknown(raw={RawType}, win=0x{WindowId:x})"
                : $"{Kind}(win=0x{WindowId:x} {X},{Y} {Width}×{Height} detail={Detail} state=0x{State:x})";
    }
}
