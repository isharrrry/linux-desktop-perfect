// libX11 的结构体布局（LP64：long 与指针均为 8 字节）。
//
// 【偏移量是算出来的，不是猜的】
//   每个结构体的字段偏移都按 C 的对齐规则逐字段推导，注释里写清了推导过程。
//   这些偏移一旦错了，症状是"事件类型对但字段是垃圾"，极难定位——所以宁可把
//   推导写下来让人复核，也不要留一个干巴巴的 struct。
//
// 【XEvent 用 Explicit 布局的原因】
//   XEvent 是一个 union，XExposeEvent / XConfigureEvent / XKeyEvent / XButtonEvent /
//   XMotionEvent / XClientMessageEvent 在同一个 192 字节里按各自的方式解释。
//   不同事件的同名字段（比如 x）落在**不同的偏移**上，Sequential 布局没法表达，
//   必须 Explicit + FieldOffset 逐个钉死。

using System.Runtime.InteropServices;

namespace WpfGfx.Linux.Windowing
{
    /// <summary>
    /// XEvent 联合体。Xlib 里定义为 <c>long pad[24]</c>，64 位下 192 字节。
    /// 读取时用 Marshal 读回整个块，再按事件类型解释对应偏移的字段。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct XEvent
    {
        // ---- 所有事件共有（XAnyEvent 头部）----
        // type: 0   serial(unsigned long): 8   send_event(Bool=int): 16   display: 24
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ulong Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public nint Display;

        // ---- XExposeEvent ----
        // window: 32   x: 40   y: 44   width: 48   height: 52   count: 56
        // ---- XKeyEvent / XButtonEvent / XMotionEvent ----
        // window: 32   root: 40   subwindow: 48   time(Time=ulong): 56
        // x: 64   y: 68   x_root: 72   y_root: 76
        // state(uint): 80   detail(uint): 84   same_screen(Bool=int): 88
        //
        // ⚠️ 易错点：C 里 `int x, y;` 是**连续两个 4 字节**，不会各自对齐到 8 字节。
        //    初版把 x/y/x_root/y_root/state/detail 写成 64/72/80/88/96/100（每个字段
        //    都按 8 字节对齐），Expose 事件（只有 1 个字段受影响、恰好没被断言）看不
        //    出来，而 ConfigureNotify 的 width 读到的是 border_width 的位置，
        //    Resize_DeliversConfigureNotify 直接报 Expected 260 / Actual 0 —— 就是这么炸的。
        //    修正依据：/usr/include/X11/Xlib.h 的 XConfigureEvent / XKeyEvent 声明。
        // ---- XClientMessageEvent ----
        // window: 32   message_type(Atom): 40   format(int): 48   data: 56
        [FieldOffset(32)] public ulong Window;

        [FieldOffset(40)] public int ExposeX;
        [FieldOffset(44)] public int ExposeY;
        [FieldOffset(48)] public int ExposeWidth;
        [FieldOffset(52)] public int ExposeHeight;
        [FieldOffset(56)] public int ExposeCount;

        // ---- XConfigureEvent ----
        // event(Window): 32   window(Window): 40
        // x: 48   y: 52   width: 56   height: 60   border_width: 64
        // above(Window): 72   override_redirect(Bool=int): 80
        [FieldOffset(40)] public ulong ConfigureWindow;
        [FieldOffset(48)] public int ConfigureX;
        [FieldOffset(52)] public int ConfigureY;
        [FieldOffset(56)] public int ConfigureWidth;
        [FieldOffset(60)] public int ConfigureHeight;

        // ---- 指针/键盘事件 ----
        [FieldOffset(56)] public ulong Time;
        [FieldOffset(64)] public int PointerX;
        [FieldOffset(68)] public int PointerY;
        [FieldOffset(72)] public int PointerRootX;
        [FieldOffset(76)] public int PointerRootY;
        [FieldOffset(80)] public uint State;

        /// <summary>XKeyEvent.keycode，与 XButtonEvent.button、XMotionEvent.is_hint 同址。</summary>
        [FieldOffset(84)] public uint Detail;

        // ---- XClientMessageEvent ----
        [FieldOffset(40)] public ulong MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public ulong Data0;
        [FieldOffset(64)] public ulong Data1;
        [FieldOffset(72)] public ulong Data2;
        [FieldOffset(80)] public ulong Data3;
        [FieldOffset(88)] public ulong Data4;
    }

    /// <summary>
    /// XWindowAttributes。
    /// x:0 y:4 width:8 height:12 border_width:16 depth:20 visual:24 root:32 class:40
    /// bit_gravity:44 win_gravity:48 backing_store:52 backing_planes:56 backing_pixel:64
    /// save_under:72 → 对齐到 80 colormap:80 map_installed:88 map_state:92
    /// all_event_masks:96 your_event_mask:104 do_not_propagate_mask:112
    /// override_redirect:120 → 对齐到 128 screen:128 → 总长 136
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public nint Visual;
        public ulong Root;
        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public ulong BackingPlanes;
        public ulong BackingPixel;
        public int SaveUnder;
        public int Padding0;
        public ulong Colormap;
        public int MapInstalled;
        public int MapState;
        public long AllEventMasks;
        public long YourEventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public int Padding1;
        public nint Screen;
    }

    /// <summary>
    /// XVisual。
    /// ext_data:0 visualid(unsigned long):8 class(int):16 → 对齐到 24
    /// red_mask:24 green_mask:32 blue_mask:40 bits_per_rgb:48 map_entries:52
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XVisual
    {
        public nint ExtData;
        public ulong VisualId;
        public int Class;
        public int Padding0;
        public ulong RedMask;
        public ulong GreenMask;
        public ulong BlueMask;
        public int BitsPerRgb;
        public int MapEntries;
    }

    /// <summary>
    /// XImage 的头部。只需要前 80 字节（到 obdata），后面的函数指针表我们不碰。
    /// width:0 height:4 xoffset:8 format:12 data:16 byte_order:24 bitmap_unit:28
    /// bitmap_bit_order:32 bitmap_pad:36 depth:40 bytes_per_line:44 bits_per_pixel:48
    /// → 对齐到 56 red_mask:56 green_mask:64 blue_mask:72 obdata:80
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XImageHeader
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public nint Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public int Padding0;
        public ulong RedMask;
        public ulong GreenMask;
        public ulong BlueMask;
        public nint Obdata;
    }
}
