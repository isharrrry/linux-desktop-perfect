// WPF-on-Linux · M7b · Win32 shim 内部共享状态
//
// 【HWND 是什么】
//   本 shim 里 **HWND == X11 Window（XID）**，不做任何再包装。选这条的理由：
//     · M7a 的 `MilVisualTarget_AttachToHwnd(hwnd)` / `MilContent_AttachToHwnd(hwnd)`
//       在 `src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Window.cs` 里就是**身份映射**，
//       它记下的 nint 将来要能落到一个真实 X11 窗口上；让 XID 直接当 HWND，
//       M7c 接线时零转换（`X11PresentationTarget.NativeHandle` 也已经是 nint）。
//     · `GetDesktopWindow()` 可以直接返回 `XRootWindow(dpy, screen)` —— 那本来
//       就是一个合法的 XID，不是伪造值。
//     · XID 由 X server 分配、非 0、进程内不复用（我们用 XID 作为表的键就是
//       身份，不额外造句柄空间）。
//   代价：`HWND_MESSAGE`（= -3）这个「父窗口哨兵」不是 XID，必须特判，见
//   `wpf_is_message_only_parent()`；message-only 窗口照样建一个不 map 的真窗口。
//
// 【不做 HWND→XID 查表的原因（诚实说明）】
//   如果 HWND 是自造的不透明句柄，我们就得在 shim 里维护一张双向表，而任何
//   跨进程/跨模块把 HWND 传给 M1 的路径都要多一次转换。直接同值最省事，
//   代价是「HWND 的值会暴露 X11 语义」——本工程里这不是问题（Linux 上没有
//   其它窗口系统需要共存）。

#ifndef WPFWIN32_INTERNAL_H
#define WPFWIN32_INTERNAL_H

#include "win32_abi.h"
#include <X11/Xlib.h>
#include <pthread.h>
#include <stddef.h>
#include <time.h>

// ── Win32 常量（只列 shim 用到的）──────────────────────────────────────────
#define WM_NULL          0x0000
#define WM_CREATE        0x0001
#define WM_DESTROY       0x0002
#define WM_MOVE          0x0003
#define WM_SIZE          0x0005
#define WM_ACTIVATE      0x0006
#define WM_SETFOCUS      0x0007
#define WM_KILLFOCUS     0x0008
#define WM_CAPTURECHANGED 0x0215   // 波47 · D-G55：捕获易主/释放时发给**失去捕获**的窗口（lParam=新捕获窗口，可为 0）
#define WM_CLOSE         0x0010
#define WM_QUIT          0x0012
#define WM_ERASEBKGND    0x0014
#define WM_SYSCOLORCHANGE 0x0015
#define WM_SHOWWINDOW    0x0018
#define WM_WININICHANGE  0x001A
#define WM_SETTINGCHANGE WM_WININICHANGE
#define WM_DEVMODECHANGE 0x001B
#define WM_ACTIVATEAPP   0x001C
#define WM_FONTCHANGE    0x001D
#define WM_TIMECHANGE    0x001E
#define WM_CANCELMODE    0x001F
#define WM_SETCURSOR     0x0020
#define WM_MOUSEACTIVATE 0x0021
#define WM_CHILDACTIVATE 0x0022
#define WM_GETMINMAXINFO 0x0024
#define WM_PAINT         0x000F
#define WM_DISPLAYCHANGE 0x007E
#define WM_NCCREATE      0x0081
#define WM_NCDESTROY     0x0082
#define WM_NCCALCSIZE    0x0083
#define WM_NCHITTEST     0x0084
#define WM_NCACTIVATE    0x0086
#define WM_NCMOUSEMOVE   0x00A0
// ── 【波 59】非客户区按钮 + 系统命令（自绘 chrome 的"拖标题栏/拖边框"必须走这条）──
#define WM_NCLBUTTONDOWN   0x00A1
#define WM_NCLBUTTONUP     0x00A2
#define WM_NCLBUTTONDBLCLK 0x00A3
#define WM_NCRBUTTONDOWN   0x00A4
#define WM_NCRBUTTONUP     0x00A5
#define WM_SYSCOMMAND      0x0112
// WM_SIZE 的 wParam（SIZE_*）：WPF 的 `Window.WmSize` 只认这三个值
#define WM_SIZECODE_RESTORED  0
#define WM_SIZECODE_MINIMIZED 1
#define WM_SIZECODE_MAXIMIZED 2
#define WM_KEYDOWN       0x0100
#define WM_KEYUP         0x0101
#define WM_CHAR          0x0102
#define WM_SYSKEYDOWN    0x0104
#define WM_SYSKEYUP      0x0105
#define WM_SYSCHAR       0x0106
#define WM_KEYLAST       0x0109
#define WM_TIMER         0x0113
#define WM_HSCROLL       0x0114
#define WM_VSCROLL       0x0115
#define WM_INITMENU      0x0116
#define WM_MENUSELECT    0x011F
#define WM_ENTERIDLE     0x0121
#define WM_MOUSEMOVE     0x0200
#define WM_LBUTTONDOWN   0x0201
#define WM_LBUTTONUP     0x0202
#define WM_LBUTTONDBLCLK 0x0203
#define WM_RBUTTONDOWN   0x0204
#define WM_RBUTTONUP     0x0205
#define WM_RBUTTONDBLCLK 0x0206
#define WM_MBUTTONDOWN   0x0207
#define WM_MBUTTONUP     0x0208
#define WM_MBUTTONDBLCLK 0x0209
#define WM_MOUSEWHEEL    0x020A
#define WM_XBUTTONDOWN   0x020B
#define WM_XBUTTONUP     0x020C
#define WM_MOUSEHWHEEL   0x020E
#define WM_MOUSELEAVE    0x02A3
#define WM_DPICHANGED    0x02E0
#define WM_APP           0x8000
#define WM_USER          0x0400

// ── 波 51 · `TASK-0108` 的 `P3`：托管侧"尺寸提示声明变了"的**私有通知消息** ──────────────
// 【为什么要有它】应用在**运行期**改 `Min/MaxWidth/Height` 而**不伴随 resize** 时，上游 `Window`
//   自己什么都不做（`PresentationFramework/System/Windows/Window.cs` 的 `OnMaxHeightChanged` /
//   `OnMaxWidthChanged` 里 `maxHeight/maxWidth < logicalSize` 那一支才动作，注释原文：
//   "no need to do anything"）⇒ 我方**没有触发器** ⇒ X 侧的 `WM_NORMAL_HINTS` 只能等
//   "下一次窗口活动"（W101A 实测：`W3-DECLARE` 应用侧 `469x365` 而 X 侧 `<缺席>`；
//   `A3 W1-REVERT` 期望 `667x500` 实际停在 `521 by 417`）。
// 【消息号为什么取 WM_APP + 0x7F00】`WM_APP`(0x8000) 起是"应用私有"段；本 shim 的
//   `RegisterWindowMessageW` 也从 `WM_APP` 起**递增**分配（见 `win32_core.c` 的
//   `next_registered_msg`）⇒ 这里从该段**上界往下**取，登记消息不可能涨到 0xFF00
//   （要 32512 次注册）。
// 【谁拦它】`win32_msg.c` 的 `wpf_dispatch_to_window()` —— `SendMessageW` 与 `DispatchMessageW`
//   的**共同落点** ⇒ 发/投两条路都覆盖，且**不再进**托管窗口过程（托管侧不需要认识它）。
#define WPF_LINUX_WM_HINTS_CHANGED (WM_APP + 0x7F00)   /* = 0xFF00 */

#define WS_VISIBLE       0x10000000L
#define WS_CHILD         0x40000000L
#define WS_POPUP         0x80000000L
#define WS_CLIPCHILDREN  0x02000000L
#define WS_CLIPSIBLINGS  0x04000000L
#define WS_MINIMIZEBOX   0x00020000L
#define WS_MAXIMIZEBOX   0x00010000L
#define WS_THICKFRAME    0x00040000L
#define WS_CAPTION       0x00C00000L
// 【波 59】最大化/最小化的"窗口状态位"。WPF 的 `Window._Style` 在多数路径上**直接读
//   `GetWindowLong(GWL_STYLE)`**（`Window.cs:3242` 的 getter：Manager==null ⇒ StyleFromHwnd）
//   ⇒ 这两个位必须由 shim 在真正的状态切换时同步，否则"最大化之后再点还原"
//   会因为 `(_Style & WS_MAXIMIZE) == 0` 而**一条 ShowWindow 都不发**（实测见报告 §1）。
#define WS_MAXIMIZE      0x01000000L
#define WS_MINIMIZE      0x20000000L

// ── 【波 59】WM_NCHITTEST 的返回码（Win32 原值）────────────────────────────────
#define HTCLIENT        1
#define HTCAPTION       2
#define HTLEFT          10
#define HTRIGHT         11
#define HTTOP           12
#define HTTOPLEFT       13
#define HTTOPRIGHT      14
#define HTBOTTOM        15
#define HTBOTTOMLEFT    16
#define HTBOTTOMRIGHT   17

// ── 【波 59】WM_SYSCOMMAND 的 wParam（比较前必须 `& 0xFFF0`：低 4 位被系统占用）──
#define SC_SIZE     0xF000
#define SC_MOVE     0xF010
#define SC_MINIMIZE 0xF020
#define SC_MAXIMIZE 0xF030
#define SC_CLOSE    0xF060
#define SC_RESTORE  0xF120

// ── 【波 59】EWMH `_NET_WM_MOVERESIZE` 的方向码（EWMH 1.4 表）──────────────────
#define WPF_MR_SIZE_TOPLEFT     0
#define WPF_MR_SIZE_TOP         1
#define WPF_MR_SIZE_TOPRIGHT    2
#define WPF_MR_SIZE_RIGHT       3
#define WPF_MR_SIZE_BOTTOMRIGHT 4
#define WPF_MR_SIZE_BOTTOM      5
#define WPF_MR_SIZE_BOTTOMLEFT  6
#define WPF_MR_SIZE_LEFT        7
#define WPF_MR_MOVE             8
#define WPF_MR_CANCEL           11

#define GWL_WNDPROC     (-4)
#define GWL_HWNDPARENT  (-8)
#define GWL_ID          (-12)
#define GWL_STYLE       (-16)
#define GWL_EXSTYLE     (-20)
#define GWL_USERDATA    (-21)
#define GWL_HINSTANCE   (-6)
#define GWLP_USERDATA   (-21)
#define GWLP_ID         (-12)
#define GWLP_WNDPROC    (-4)
#define GWLP_HINSTANCE  (-6)
#define GWLP_HWNDPARENT (-8)

#define HWND_DESKTOP    ((HWND)0)
#define HWND_MESSAGE    ((HWND)(intptr_t)-3)
#define HWND_TOP        ((HWND)0)
#define HWND_BOTTOM     ((HWND)1)
#define HWND_TOPMOST    ((HWND)(intptr_t)-1)
#define HWND_NOTOPMOST  ((HWND)(intptr_t)-2)

#define SW_HIDE            0
#define SW_SHOWNORMAL      1
#define SW_SHOWMINIMIZED   2
#define SW_SHOWMAXIMIZED   3
#define SW_SHOWNOACTIVATE  4
#define SW_SHOW            5
#define SW_MINIMIZE        6
#define SW_RESTORE         9

#define PM_NOREMOVE 0x0000
#define PM_REMOVE   0x0001
#define PM_NOYIELD  0x0002

#define QS_KEY         0x0001
#define QS_MOUSEMOVE   0x0002
#define QS_MOUSEBUTTON 0x0004
#define QS_POSTMESSAGE 0x0008
#define QS_TIMER       0x0010
#define QS_PAINT       0x0020
#define QS_INPUT       (QS_MOUSEMOVE | QS_MOUSEBUTTON | QS_KEY)
#define QS_EVENT       0x2000
#define MWMO_INPUTAVAILABLE 0x0004
#define MWMO_WAITALL        0x0001

#define WAIT_OBJECT_0  0
#define WAIT_TIMEOUT   258
#define WAIT_FAILED    ((DWORD)0xFFFFFFFF)

#define SM_CXSCREEN     0
#define SM_CYSCREEN     1
#define SM_CXVSCROLL    2
#define SM_CYHSCROLL    3
#define SM_CYCAPTION    4
#define SM_CXBORDER     5
#define SM_CYBORDER     6
#define SM_CXEDGE       45
#define SM_CYEDGE       46
#define SM_CXDOUBLECLK  36
#define SM_CYDOUBLECLK  37
#define SM_CXDRAG       68
#define SM_CYDRAG       69
#define SM_CXICON       11
#define SM_CYICON       12
#define SM_CXSMICON     49
#define SM_CYSMICON     50
#define SM_CMONITORS    80
#define SM_MOUSEWHEELPRESENT 75
#define SM_XVIRTUALSCREEN 76
#define SM_YVIRTUALSCREEN 77
#define SM_CXVIRTUALSCREEN 78
#define SM_CYVIRTUALSCREEN 79
#define SM_SWAPBUTTON   23
#define SM_MENUDROPALIGNMENT 40
#define SM_REMOTESESSION 0x1000

#define NULL_BRUSH  5
#define WHITE_BRUSH 0
#define BLACK_BRUSH 4
#define HOLLOW_BRUSH NULL_BRUSH

#define MONITOR_DEFAULTTONULL       0
#define MONITOR_DEFAULTTOPRIMARY    1
#define MONITOR_DEFAULTTONEAREST    2

#define TME_HOVER  0x00000001
#define TME_LEAVE  0x00000002
#define TME_CANCEL 0x80000000

// ── SystemParametersInfo 的 action 码与默认值相关的常量 ─────────────────────
// 数值取自上游 `Shared/MS/Win32/NativeMethodsCLR.cs` 的 SystemParametersInfo enum
// （`grep -oE "SPI_[A-Z0-9_]+ *= *(0x[0-9A-Fa-f]+|[0-9]+)"`），不是凭记忆写的。
#define SPI_GETBORDER                  0x0005
#define SPI_GETKEYBOARDSPEED           0x000A
#define SPI_GETKEYBOARDDELAY           0x0016
#define SPI_GETICONTITLEWRAP           0x0019
#define SPI_GETMENUDROPALIGNMENT       0x001B
#define SPI_GETICONTITLELOGFONT        0x001F
#define SPI_GETDRAGFULLWINDOWS         38
#define SPI_GETNONCLIENTMETRICS        41
#define SPI_GETICONMETRICS             0x002D
#define SPI_GETKEYBOARDPREF            0x0044
#define SPI_GETANIMATION               0x0048
#define SPI_GETFONTSMOOTHING           0x004A
#define SPI_GETWORKAREA                48
#define SPI_GETHIGHCONTRAST            66
#define SPI_GETMOUSEHOVERWIDTH         0x0062
#define SPI_GETMOUSEHOVERHEIGHT        0x0064
#define SPI_GETMOUSEHOVERTIME          0x0066
#define SPI_GETMENUSHOWDELAY           0x006A
#define SPI_GETMOUSESPEED              0x0070
#define SPI_GETDEFAULTINPUTLANG        89
#define SPI_GETSNAPTODEFBUTTON         95
#define SPI_GETWHEELSCROLLLINES        104
#define SPI_GETACTIVEWINDOWTRACKING    0x1000
#define SPI_GETMENUANIMATION           0x1002
#define SPI_GETCOMBOBOXANIMATION       0x1004
#define SPI_GETLISTBOXSMOOTHSCROLLING  0x1006
#define SPI_GETGRADIENTCAPTIONS        0x1008
#define SPI_GETKEYBOARDCUES            0x100A
#define SPI_GETHOTTRACKING             0x100E
#define SPI_GETSTYLUSHOTTRACKING       0x1010
#define SPI_GETMENUFADE                0x1012
#define SPI_GETSELECTIONFADE           0x1014
#define SPI_GETTOOLTIPANIMATION        0x1016
#define SPI_GETTOOLTIPFADE             0x1018
#define SPI_GETCURSORSHADOW            0x101A
#define SPI_GETMOUSEVANISH             0x1020
#define SPI_GETFLATMENU                0x1022
#define SPI_GETDROPSHADOW              0x1024
#define SPI_GETUIEFFECTS               0x103E
#define SPI_GETCLIENTAREAANIMATION     0x1042
#define SPI_GETFOREGROUNDLOCKTIMEOUT   0x2000
#define SPI_GETACTIVEWNDTRKTIMEOUT     0x2002
#define SPI_GETFOREGROUNDFLASHCOUNT    0x2004
#define SPI_GETCARETWIDTH              0x2006
#define SPI_GETFONTSMOOTHINGTYPE       0x200A
#define SPI_GETFONTSMOOTHINGCONTRAST   0x200C
#define SPI_GETFOCUSBORDERWIDTH        0x200E
#define SPI_GETFOCUSBORDERHEIGHT       0x2010

#define ERROR_INVALID_WINDOW_HANDLE 1400
#define ERROR_SUCCESS 0

// ── 窗口/类记录 ────────────────────────────────────────────────────────────
#define WPF_MAX_EXTRA_LONGS 8   // 覆盖 GWL_* / GWLP_* 的全部用量

typedef struct wpf_prop {
    char            *name;
    void            *value;
    struct wpf_prop *next;
} wpf_prop;

typedef struct wpf_window {
    HWND      hwnd;                 // == X11 Window
    ATOM      class_atom;
    WNDPROC   wndproc;              // GWL_WNDPROC 当前值
    WNDPROC   class_wndproc;        // 注册类时给的 lpfnWndProc
    int64_t   style;
    int64_t   exstyle;
    void     *userdata;
    HWND      parent;
    int32_t   x, y;                 // 客户区左上角在 root 上的坐标
    int32_t   width, height;        // 客户区尺寸
    int32_t   border;
    int       mapped;               // XMapWindow 过且没 Unmap
    int       is_message_only;      // 父窗口是 HWND_MESSAGE
    // ── 【波 59】窗口状态与"自绘 chrome"判定 ──────────────────────────────────
    int       maximized;            // 我们请求过 _NET_WM_STATE 最大化且还没还原
    int       iconified;            // 已被最小化（XIconifyWindow）
    int       custom_chrome;        // 应用声明"我自己画窗框"⇒ WM 侧不加装饰（详见 win32_x11.c）
    // ── 【波 50 · `D-G83` 修法 H1／波 51 · `D-G88` 落地 `P1`＋`P4`】提示通道的台账 ────
    //   ⚠️ 波 51 **删掉了**波 50 的那两个停止条件（`hints_map_asks` 次数上限、
    //   `hints_map_declared` 终态）：W93A 实测它们都是"**用次数近似值变化**"，
    //   而近似在两侧都出错 —— ①问出过一次声明即**终态** ⇒ 运行期再改**永不重发**；
    //   ②上限计的是"**问**"不是"**改**" ⇒ 4 次无意义的 `HIDE/SHOW` 花光预算后
    //   **连第一次真声明都发不出去**（`src/Linux/build/MilBridge/W93A-report.md` §2.5）。
    //   现在直接比较**上次已发布的那组值**（下面 5 个字段）⇒ 幂等、无消息风暴。
    //   见 `win32_core.c` 的 `wpf_hints_publish`（写 X 的**唯一**入口）。
    int       hints_pub_valid;      // 是否已有"上次发布"（首次发布前 = 0 ⇒ 一定要发一次）
    int       hints_pub_min_w, hints_pub_min_h;
    int       hints_pub_max_w, hints_pub_max_h;  // 0,0 = "不发 PMaxSize" 也是**一种已发布状态**
    int       hints_in_refresh;     // ★ `P4` 重入闸：刷新会**同步回调托管代码**，可能再进 `SetWindowPos`
    int       nc_press_active;      // 当前这次按下已被判成非客户区（抬起要配对成 WM_NCLBUTTONUP）
    int32_t   rc_x, rc_y, rc_w, rc_h;   // 最大化前的客户区矩形（还原用）
    int       in_destroy;           // 正在走 DestroyWindow（防重入）
    int       display_devices_notified; // 是否已代发 DisplayDevicesAvailabilityChanged（win32_msg.c）
    uint64_t  created_ms;
    void     *owner_thread;         // 创建它的 wpf_thread*（见 wpf_thread.self 的说明）
    // ── ⏪ `T-A66`：`SetScrollPosWrapper` 的**滚动位置状态位**（本侧窗口模型自持）───────────────
    //   语义＝ Win32 `SetScrollPos` 的"位置存取"：`[SB_HORZ(0)]`／`[SB_VERT(1)]` 各一格，
    //   调用返回**旧位置**（首次调用前为 0，`calloc` 初值）。本侧无滚动条视觉 ⇒ **不**声称重绘
    //   （具名 `NOINFO-WRAPPER-REDRAW`）；**共用**窗口表的句柄校验（未知 hwnd ⇒ 失败面）。
    int       scroll_pos[2];
    wpf_prop *props;
    struct wpf_window *next;
} wpf_window;

typedef struct wpf_class {
    ATOM     atom;
    char    *name;                  // UTF-8（RegisterClassExW 收 UTF-16，转过来存）
    WNDPROC  wndproc;
    int64_t  style;
    HBRUSH   background;
    int64_t  cb_wnd_extra;
    struct wpf_class *next;
} wpf_class;

typedef struct wpf_timer {
    HWND      hwnd;
    UINT_PTR  id;
    uint64_t  deadline_ms;
    uint32_t  elapse_ms;
    TIMERPROC proc;
    void     *owner_thread;         // 创建它的 wpf_thread*
    struct wpf_timer *next;
} wpf_timer;

typedef struct wpf_msg_node {
    WPF_MSG msg;
    // [D-K1 · 波 20 修法] **该消息入队时刻的修饰键快照**（bits: 1=Shift 2=Ctrl 4=Alt 8=Win）。
    //   为什么不能放进 `WPF_MSG`：那是与托管 `MSG` 的**跨边界布局**（托管侧自己分配、我们只填），
    //   加字段会越界写。放在**纯内部**的队列节点上则零风险。
    uint8_t  mods;
    // [D-K1 · 波 24 修法乙] `mods` 是否**翻译层刚为某个 X 事件**盖的戳。
    //   `PostMessageW`/`SetTimer` 等**非翻译层**入队会沿用上一次残留的 `s_push_mods`
    //   ⇒ 修法乙若不加这道闸，一条被 post 的按键消息就会用**陈旧戳**改写实时表（真回归）。
    uint8_t  mods_valid;
    // 【W136A · TASK-0209 · F2「写坏者」取证】占原 padding 6 字节 ⇒ **sizeof 不变（仍 64）**：
    //   magic    —— 节点魔数：写坏者若覆盖它，校验即失配 ⇒ **可区分「链本来坏」与「运行期被写坏」**；
    //   push_seq —— 入队序号（进程内单调）：台账里可指名「第几个节点、由哪个 tid 写入」。
    uint16_t magic;
    uint32_t push_seq;
    struct wpf_msg_node *next;
} wpf_msg_node;
_Static_assert(offsetof(wpf_msg_node, next) == 56, "W136A: next 必须仍在 0x38（崩点 mov 0x38(%rax),%rax 的口径）");
_Static_assert(sizeof(wpf_msg_node) == 64, "W136A: 节点仍须 0x40（尺寸变化会改变与 wpf_thread 的复用关系）");

typedef struct wpf_thread {
    // 线程标识：**就是本结构体自身的地址**。选它的理由——托管侧的
    // GetCurrentThreadId / GetWindowThreadProcessId / PostThreadMessage 需要一个
    // 能唯一标识线程的整数，而 `wpf_thread*` 在进程内唯一、生命周期与线程绑定
    // （TLS destructor 里才 free），低 31 位当 id 完全够用，且不需要额外的
    // 「pthread_t → 线程表」映射。窗口/定时器里记的 owner 也是这个指针。
    struct wpf_thread *self;
    wpf_msg_node *head, *tail;      // 已投递、未取走的消息队列
    int           quit_code;        // >=0 表示 PostQuitMessage 过
    int           wake_read, wake_write;
    DWORD         last_msg_time;
    int32_t       last_pt_x, last_pt_y;
    int           owns_x_connection;
    struct wpf_thread *next;
} wpf_thread;

// 线程 id 的口径：`(uint32_t)((uintptr_t)wpf_thread* & 0x7FFFFFFF)`。
#define WPF_THREAD_ID(t) ((uint32_t)((uintptr_t)(t) & 0x7FFFFFFFu))

// ── 全局状态 ───────────────────────────────────────────────────────────────
typedef struct {
    pthread_mutex_t lock;           // 可重入；保护下面所有字段
    Display        *dpy;
    int             screen;
    Window          root;
    int             xfd;
    int             x_failed;
    char            dpy_error[512];
    // [1400 诊断] Xlib 异步错误（XSetErrorHandler 捕获）：error_code/request_code/minor_code + 文案。
    //   为什么需要：`XCreateWindow` 这类请求的失败是**异步**的（X 协议先回 void），
    //   调用点拿到 0 时真正的原因在 X 错误处理里，不进 errno/LastError。
    char            x_error[256];
    // [D-K1] 按键状态表（**Win32 `GetKeyboardState` 布局**：bit7=按下、bit0=锁定态）。
    //   为什么必须自己维护：X11 的修饰键掩码只在**事件**里出现（`ev.xkey.state`），
    //   而托管侧是**事后**用 `GetKeyState(VK_*)` 问的（`HwndKeyboardInputProvider.cs:667/673/679`）。
    //   修法前 `GetKeyState` 是 `return 0` 的桩 ⇒ 上层永远看不到 Ctrl/Shift/Alt ⇒ 所有快捷键失效。
    uint8_t         key_state[256];

    wpf_window     *windows;
    wpf_class      *classes;
    wpf_timer      *timers;
    wpf_thread     *threads;

    ATOM            next_atom;
    uint32_t        next_registered_msg;   // RegisterWindowMessage 的私有消息段
    uint32_t        msg_extra_info;        // GetMessageExtraInfo 的进程级值
    int64_t         clock_bias_ms;         // 保证 GetTickCount 单调且从 0 起算
} wpf_global;

extern wpf_global g_wpf;

// 每个线程的队列状态。用 pthread_key 而不是 __thread，这样线程退出时
// 能挂 destructor 关掉 self-pipe（否则 fd 泄漏）。
wpf_thread *wpf_thread_self(void);

// win32_core.c 里的全局单例初始化（pthread_once）。
void wpf_global_init(void);

// 焦点/捕获的软状态（win32_core.c 定义，win32_x11.c 在 FocusIn/FocusOut 时同步）。
extern HWND g_focus_window;
extern HWND g_capture_window;

// win32_core.c：某个窗口的创建线程 id（口径 = wpf_thread* 地址的低 31 位）。
uint32_t wpf_thread_id_of_window(HWND hwnd);

// win32_core.c：**幂等发布**（写 X 的**唯一**入口，`D-G88` 的 `P1`）。
// 【为什么在这里声明】波 51 起它多了一个调用点 —— `win32_msg.c` 的 `wpf_dispatch_to_window()`
//   在拦下 `WPF_LINUX_WM_HINTS_CHANGED`（托管侧的声明变化告示）时**复用同一个入口**
//   （连同 `P1` 的 `hints_pub_*` 缓存一起复用，**不另写一套**发布路径）。
//   `where` 只影响 `[WMSIZE_DIAG]` 的诊断前缀。
void wpf_hints_publish(HWND hwnd, const char *where);

// win32_msg.c：记录「最近取出的消息」的位置与时间，供 GetMessagePos/Time 用。
void wpf_note_message(const WPF_MSG *m);

// win32_msg.c：代发 `DisplayDevicesAvailabilityChanged`（M7c Phase 2 的渲染起搏器）。
// 详见 win32_msg.c 里那段长注释 —— 一句话：Windows 上这条消息由原生 MilCore 发，
// 而我们的 MilCore 端没有消息循环也没有 X 连接，所以由 shim（两者都有）按**真值**代发。
void wpf_notify_display_devices_available(void);

// win32_unicode_tables.c（**自动生成**，见 src/Linux/tools/gen-unicode-tables.py）：
// `MILGetClassificationTables` 要交出去的那几张真表。声明必须与生成物逐字一致 ——
// 尤其是 `const uintptr_t *const`：第二级条目是"小整数(<472) 或叶子指针"共用一格。
#ifndef WPF_TBL
// 生成物与头文件都要用它；两边都用 #ifndef 包住，避免"重定义"（同体重复定义在 C 里合法，
// 但一旦哪边改了字面量就会变成难查的宏冲突 —— 直接挡住）。
#define WPF_TBL __attribute__((visibility("hidden")))
#endif
extern WPF_TBL const uintptr_t *const k_wpf_uni_planes[17];
extern WPF_TBL const WPF_CHAR_ATTR    k_wpf_char_attr[];
extern WPF_TBL const int              k_wpf_char_attr_count;
extern WPF_TBL const uintptr_t *const k_wpf_mirror_planes[17];

// win32_classification.c：#U 的导出与自检/对拍访问器
void MILGetClassificationTables(WPF_RAW_CLASSIFICATION_TABLES *ct);
int  WpfLinuxWin32_UnicodeClassOf(uint32_t scalar);
int  WpfLinuxWin32_CharAttrOf(int unicode_class, uint8_t out[8]);
int  WpfLinuxWin32_ClassificationClassCount(void);
int  WpfLinuxWin32_ClassificationSelfCheck(void);

// ── 屏幕度量（M7c 收尾轮 · DPI 面）─────────────────────────────────────────
// `GetDeviceCaps(LOGPIXELSX/LOGPIXELSY)` / `GetDpiFor*` 的**唯一数据源**。
// 为什么必须统一到一处：这些入口若互相不一致（例如 GetDeviceCaps 报 100 而
// GetDpiForWindow 报 96），WPF 会把两套比例混用，症状是"窗口尺寸/文字缩放对不上"。
// 由 win32_x11.c 用 X server 的 (pixels, millimeters) 算出；拿不到就回退 96。
#define WPF_DEFAULT_SCREEN_DPI 96
typedef struct {
    int dpi_x;          // 0 = 未知（由调用方回退）
    int dpi_y;
    int depth;          // 默认视觉的位深（BITSPIXEL）
    int pixels_wide;    // 屏幕像素宽
    int pixels_high;
} wpf_screen_metrics;

void wpf_x11_screen_metrics(wpf_screen_metrics *out);

// win32_classification.c：LoGetEscString（LineServices 的转义字符表）
void LoGetEscString(WPF_ESCSTRING *esc);
int  WpfLinuxWin32_EscStringSelfCheck(void);

// win32_misc.c：诊断/失败信息
int wpf_is_message_only_parent(HWND parent);

// win32_exports.c：在本 .so 的导出符号里查名字（GetProcAddress 的落点）。
void *wpf_shim_symbol(const char *name);

// ── 全局锁 ─────────────────────────────────────────────────────────────────
// wpf_lock 是**可重入**的（见 win32_core.c 的说明）。wpf_unlock 直接展开，
// 保证「加锁/放锁」在源码里成对可见。
void wpf_lock(void);
#define wpf_unlock() pthread_mutex_unlock(&g_wpf.lock)

// ── 跨文件用到的 Win32 入口原型 ────────────────────────────────────────────
// 这些是**其它 .c 文件也要调**的导出函数。它们同时也是 .so 的动态符号，
// 但对内必须显式声明，否则 GCC 会按隐式 int 处理（返回指针的函数会截断）。
LRESULT DefWindowProcW(HWND, UINT, WPARAM, LPARAM);
LRESULT DefWindowProcA(HWND, UINT, WPARAM, LPARAM);
LRESULT DefWindowProc(HWND, UINT, WPARAM, LPARAM);
LRESULT CallWindowProcW(WNDPROC, HWND, UINT, WPARAM, LPARAM);
HWND    GetDesktopWindow(void);
HWND    GetForegroundWindow(void);
BOOL    DestroyWindow(HWND);
BOOL    ShowWindow(HWND, int);
BOOL    MoveWindow(HWND, int, int, int, int, BOOL);
BOOL    PostMessageW(HWND, UINT, WPARAM, LPARAM);
uint32_t RegisterWindowMessageW(const uint16_t *);
int     MapWindowPoints(HWND, HWND, WPF_POINT *, int);
LONG    GetWindowLongW(HWND, int);
INT_PTR SetWindowLongPtrW(HWND, int, INT_PTR);
UINT_PTR SetTimer(HWND, UINT_PTR, UINT, TIMERPROC);
BOOL    KillTimer(HWND, UINT_PTR);
FARPROC GetProcAddressW(HMODULE, const char *);
HMODULE GetModuleHandleW(const uint16_t *);
int     wpf_x11_ensure(void);
void    wpf_x11_set_input_focus(HWND hwnd);   // 补丁 F2：把 X 输入焦点真的设过去（原实现从不调 XSetInputFocus）
// 【波 44 · D-G49】鼠标五键的 `GetKeyState` 口径：问 X 的真实指针按键态。
//   上游 `Win32MouseDevice` 判按钮状态**只看** `GetKeyState(VK_LBUTTON) & 0x8000`；
//   本 shim 的 `key_state[]` 只由键盘翻译层填 ⇒ 鼠标五键恒 0 ⇒ 控件永不激活（只拿焦点）。
short   wpf_x11_mouse_keystate(int vk);       // VK_LBUTTON/RBUTTON/MBUTTON/XBUTTON1/2 → 0x8000 表示按下，否则 0
void    wpf_x11_install_error_handler(void);   // [1400 诊断] 装 XSetErrorHandler（幂等）
void    wpf_keystate_note_key(uint32_t vk, int is_up);        // [D-K1] 按键状态落表（按下/抬起 + 锁定键 toggle）
void    wpf_keystate_sync_modifiers(uint32_t x_state);      // [D-K1] 用 X11 掩码同步 Ctrl/Shift/Alt/Lock/Win
// [D-K1 · 波 20] 逐消息修饰键快照（修"整批抽干 ⇒ 派发时状态已被后续事件改掉"）
uint32_t wpf_keystate_event_mods(uint32_t x_state, uint32_t vk, int is_up);  // 由事件算"此刻"的修饰位
void     wpf_keystate_set_push_mods(uint32_t mods);   // 入队前设置（翻译层，单线程）
// [D-K1 · 波 24 修法乙] 取出**按键类消息**时，让实时表采纳"这条消息时刻"的修饰键状态
//   （Win32 的 `GetKeyState` 是**消息队列语义**）。**调用者必须已持 `g_wpf.lock`**（本函数不取锁）。
void     wpf_keystate_apply_queue_mods(uint8_t mods);
void     wpf_keystate_note_pop_mods(const WPF_MSG *m, uint8_t mods);  // 出队时**按消息四元组**登记
int      wpf_keystate_lookup_mods(const WPF_MSG *m, uint8_t *out);     // 派发期按四元组查回快照（0=没匹配到）
int      wpf_keystate_dispatch_mods(uint8_t *out);    // 正在派发的那条消息的快照（0=无）
int      wpf_msg_in_dispatch(void);   // [D-K1 · 波 24] 此刻是否在 DispatchMessageW 之内（探针用；与"快照有无"是两件事）
void    wpf_win_diag_report(const char *where, const char *cls, HWND parent, uint32_t err);   // [1400 诊断] 建窗失败时把真原因打到 stderr
void    wpf_x11_diag_selftest_xerr(void);   // [1400 诊断·牙齿] 用坏 window id 触发真 X 错误（WPF_LINUX_WIN_DIAG=xerr）


// 复用的可变参数格式化（不做 C 字符串安全检查，只用于内部拼字符串）
uint64_t wpf_now_ms(void);
void     wpf_set_last_error(uint32_t code);
uint32_t wpf_get_last_error(void);

// UTF-16 ↔ UTF-8（RegisterClassExW / CreateWindowExW 的类名与标题）
char    *wpf_utf16_to_utf8_dup(const uint16_t *s);
uint16_t *wpf_utf8_to_utf16_dup(const char *s);

// X11 后端（win32_x11.c）
int      wpf_x11_ensure(void);                 // 0 = 失败（错误在 g_wpf.dpy_error）
void     wpf_x11_flush(void);
int      wpf_x11_pending(void);
int      wpf_x11_pump_into_queue(wpf_thread *t);   // 把待处理 X 事件转成 MSG 入队，返回条数
uint64_t wpf_x11_create_window(wpf_window *w, const char *title);
void     wpf_x11_destroy_window(HWND hwnd);
void     wpf_x11_map(HWND hwnd, int map);
void     wpf_x11_move_resize(HWND hwnd, int x, int y, int w, int h);
void     wpf_x11_set_title(HWND hwnd, const char *title);
int      wpf_x11_grab_probe(HWND hwnd);   // 诊断：探测是否有别的客户端持有指针抓取

// ── 【波 58 · 屏幕适配】顶层窗"装得下"：读数与 X 提示 ────────────────────────────
// 【为什么需要这一组】Win32 里窗口装饰由 USER32 画在**屏幕坐标系内**（客户区 800×600
//   的窗口，外框也在屏幕内）；X11 里装饰由 **窗口管理器**加在客户区**外面**
//   ⇒ "客户区 800×600 + 标题栏/边框" 一旦超过屏幕，xfwm4/metacity 这类 WM 会判定
//   "这窗装不下" 并**自动最大化**它（用户实测：800×600 屏上 800×600 窗 ⇒
//   `_NET_WM_STATE_MAXIMIZED_HORZ|VERT`、标题栏按钮跑到 y=-5、拖不动也缩不了）。
// 【分工】屏幕/工作区**读数**与 X 提示的**写**在 win32_x11.c（唯一碰 Xlib 的地方）；
//   "钳哪些窗口、钳到多少"的**策略**在 win32_core.c（clamp_toplevel_extent）。
void     wpf_x11_workarea(int *x, int *y, int *w, int *h);      // _NET_WORKAREA，缺 ⇒ 屏幕
/* ── 【`D-G147`】工作区的**来源**与声明口（修法 A）─────────────────────────────
   修前"工作区 == 显示器"是**静默恒等**（`win32_core.c` 的 `GetMonitorInfoW` 直接
   `info->rcWork = info->rcMonitor;`），判据无从取证。现在来源可查、可逐趟上屏。
   `== "net-workarea"` ⇒ 取自 root 的 `_NET_WORKAREA`；`fallback-screen` ⇒ 无 WM/属性缺；
   `fallback-malformed` ⇒ 属性在但畸形（`n<4`／`format!=32`／宽高<=0）；`no-x` ⇒ 无 X。 */
const char *wpf_x11_workarea_src(void);
int         wpf_x11_workarea_prop(int *n_out);   // 返回 prop_present
void        wpf_x11_workarea_declare(const char *who, const WPF_RECT *mon, int mon_present,
                                      int l, int t, int r, int b);
void     wpf_x11_client_size_limit(int *max_w, int *max_h);     // 工作区 − 装饰余量；0,0 = 不限制
void     wpf_x11_screen_size(int *sw, int *sh);                 // 屏幕（不是工作区）
void     wpf_x11_apply_wm_hints(HWND hwnd, const char *cls,
                                int min_w, int min_h, int max_w, int max_h);
// ── 【波 59】窗口状态 / 自绘 chrome / 移动缩放（都在 win32_x11.c；唯一碰 Xlib 的地方）──
//   `decorated=0` ⇒ 写 `_MOTIF_WM_HINTS`（**属性类型必须是 `_MOTIF_WM_HINTS` 这个 atom 本身**，
//   写成 CARDINAL 时 xfwm4 的 `XGetWindowProperty(req_type=atom)` 会拿到 nitems=0 ⇒ 完全忽略 ——
//   实测见报告 §1/§4）。`decorated=1` ⇒ 删除该属性（回到 WM 默认装饰）。
void     wpf_x11_set_decorations(HWND hwnd, int decorated);
void     wpf_x11_apply_wm_state(HWND hwnd, int maximize);       // `_NET_WM_STATE` 加/去 MAXIMIZED_HORZ|VERT
void     wpf_x11_iconify(HWND hwnd);                            // XIconifyWindow（最小化）
// 【TASK-0109】判据 = "`_NET_SUPPORTING_WM_CHECK` 属性在 **∧** 它指向的那个检查窗
//   **此刻真的在树里**（且 ≠ `0x0`、≠ root）" —— **不是**"属性在不在"：WM 死后属性会残留，
//   只查属性会让下游两条路（`win32_x11.c` 的 moveresize ／ `win32_core.c:653` 的窗态）
//   都走 `XSendEvent` 而**静默失效**。射程 = 这两个调用点（全仓再无第三处）。
int      wpf_x11_has_ewmh_wm(void);
// 把客户区改到 (x,y,w,h) —— **走 WM**（`_NET_MOVERESIZE_WINDOW`）：WM 会连窗框一起摆。
//   【为什么不用 `_NET_WM_MOVERESIZE`】那条"把拖动交给 WM"的消息虽然在 xfwm4 的
//   `_NET_SUPPORTED` 里，但**实测无效**（按住左键期间从外部单独发它，窗口纹丝不动；
//   而 `_NET_MOVERESIZE_WINDOW` 同条件立刻生效）⇒ 拖动必须由我们自己按 motion 驱动。
int      wpf_x11_moveresize_window(HWND hwnd, int x, int y, int w, int h);
//   X 层指针抓取（NC 拖动期间用：指针离开窗口后仍要收到 motion/release）
void     wpf_x11_pointer_grab(HWND hwnd, int grab);

// Win32 MINMAXINFO（40 字节；托管侧 PresentationFramework 的
// `System/Windows/Standard/NativeMethods.cs:1841` 逐字段对等：5 个 POINT）。
// 【为什么定义在内部头而不是 win32_abi.h】本波写域只到这三个文件；该结构体只在
//   本文件与 win32_core.c 内部使用（不新增导出符号），偏移由下面两条断言钉死。
typedef struct { int32_t x, y; } WPF_MMI_POINT;
typedef struct {
    WPF_MMI_POINT ptReserved;
    WPF_MMI_POINT ptMaxSize;
    WPF_MMI_POINT ptMaxPosition;
    WPF_MMI_POINT ptMinTrackSize;
    WPF_MMI_POINT ptMaxTrackSize;
} WPF_MINMAXINFO;
_Static_assert(sizeof(WPF_MMI_POINT) == 8,  "POINT 8");
_Static_assert(sizeof(WPF_MINMAXINFO) == 40, "MINMAXINFO 40（5×POINT）");
void     wpf_x11_query_title(HWND hwnd, char *buf, size_t cap);
void     wpf_x11_query_geometry(HWND hwnd, int *x, int *y, int *w, int *h, int *mapped);

// ── 【波 59】窗口状态机（win32_core.c）─────────────────────────────────────────
//   `WPF_WS_NORMAL`/`WPF_WS_MAX`/`WPF_WS_MIN`：还原 / 真最大化 / 最小化。
//   语义：EWMH 可用时**交给 WM**（几何由 WM 算，`ConfigureNotify` 回填窗口表并按状态派发
//   `WM_SIZE(SIZE_MAXIMIZED|MINIMIZED|RESTORED)`）；没有 EWMH WM 时退回"自己按工作区改几何"。
#define WPF_WS_NORMAL  0
#define WPF_WS_MAX     1
#define WPF_WS_MIN     2
void     wpf_core_window_state(HWND hwnd, int mode);
//   发 `WM_NCHITTEST` 问窗口过程"这一点算什么"（屏幕坐标）；返回 HT* 码。
//   **只对顶层非 message-only 窗口问**：普通窗口（有 caption、窗框由 WM 画）保持 HTCLIENT，
//   免得把"客户区点击"错判成非客户区而劫走。
int      wpf_core_nc_hit_test(HWND hwnd, int x_root, int y_root);
//   "应用自己画窗框"的 sticky 判定（依据与理由见 win32_core.c 的 SetWindowPos 注释）。
void     wpf_core_note_framechanged(HWND hwnd);
int      wpf_core_custom_chrome(HWND hwnd);

// 窗口表（win32_core.c）
wpf_window *wpf_window_find(HWND hwnd);        // 调用者需持锁
wpf_window *wpf_window_add(HWND hwnd);
void        wpf_window_remove(HWND hwnd);
wpf_class  *wpf_class_find_atom(ATOM atom);
wpf_class  *wpf_class_find_name(const char *name);

// 队列（win32_msg.c）
void    wpf_queue_push(wpf_thread *t, const WPF_MSG *m);
int     wpf_queue_pop(wpf_thread *t, WPF_MSG *out, HWND filter, UINT lo, UINT hi);
void    wpf_queue_wake(wpf_thread *t);
int     wpf_queue_count(wpf_thread *t);
int64_t wpf_timer_next_deadline(void);         // -1 = 没有定时器
void    wpf_timer_fire_due(wpf_thread *t);     // 到期的定时器 → WM_TIMER / 回调
LRESULT wpf_dispatch_to_window(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp);

#endif // WPFWIN32_INTERNAL_H
