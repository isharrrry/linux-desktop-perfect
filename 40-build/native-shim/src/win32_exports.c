// WPF-on-Linux · M7b · 符号查找 + 给 M7c 的桥接导出
//
// ── 为什么 GetProcAddress 要能查到本 shim ────────────────────────────────
//   托管侧 HwndSubclass 的**静态构造**做这件事：
//       HMODULE u32 = GetModuleHandle("user32.dll");
//       DefWndProc  = GetProcAddress(u32, "DefWindowProcW");
//   拿到 NULL 的话，DefWndProcWrapper → CallWindowProc(NULL, …) 就退化成
//   「什么都不做」，整个窗口过程回退链断掉。所以 GetProcAddress 必须返回
//   **本 shim 里 DefWindowProcW 的真实地址**。
//
// ── 怎么拿到「本 .so 自己的符号」而不依赖加载标志 ────────────────────────
//   .NET 的 NativeLibrary.Load 用 `dlopen(name, RTLD_LAZY)`（LOCAL scope），
//   所以 `dlsym(RTLD_DEFAULT, …)` 查不到我们的符号。可靠做法是：
//     1. `dladdr(&本文件里的一个函数, &info)` 拿到自己的 .so 路径；
//     2. `dlopen(path, RTLD_NOW | RTLD_NOLOAD)` 拿回自己的 handle（不重复加载）；
//     3. `dlsym(handle, name)`。
//   这样**任何**导出符号都能被查到，不需要维护第二份名字表（表会漂移）。
//   仍然保留一张常用名的小表做快路径，避免每次 GetProcAddress 都 dlopen。

#define _GNU_SOURCE
#include "win32_internal.h"

#include <dlfcn.h>
#include <poll.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

// ── 符号查找 ───────────────────────────────────────────────────────────────
// 这里**刻意不维护第二份「导出名 → 地址」的表**。理由：那张表会和真实的导出
// 集合漂移——改了函数名、加了 A/W 变体、删了一个 stub，表不会报错，只会在
// 运行期返回 NULL，而 NULL 的表现是「窗口过程链静默失效」，是最难查的一类 bug。
// 直接问动态链接器要符号，永远和编译产物一致。
//
// 顺序：
//   1. dlsym(RTLD_DEFAULT, name) —— 如果 .so 是以 RTLD_GLOBAL 加载的（有宿主
//      程序这么做），这一步就命中；
//   2. dlsym(自己的 handle, name) —— .NET 用 RTLD_LOCAL 加载，所以实际走这条。

static void *wpf_self_handle(void)
{
    static void *handle = NULL;
    static int tried = 0;
    if (tried) return handle;
    tried = 1;

    Dl_info info;
    if (dladdr((void *)(uintptr_t)&wpf_self_handle, &info) && info.dli_fname) {
        // RTLD_NOLOAD：本 .so 已经加载，只要回一个 handle，不要重新加载。
        handle = dlopen(info.dli_fname, RTLD_NOW | RTLD_NOLOAD);
    }
    if (!handle) handle = dlopen(NULL, RTLD_NOW);   // 退路：全局作用域
    return handle;
}

void *wpf_shim_symbol(const char *name)
{
    if (!name) return NULL;

    void *p = dlsym(RTLD_DEFAULT, name);
    if (p) return p;

    void *h = wpf_self_handle();
    if (h) p = dlsym(h, name);
    return p;
}

FARPROC GetProcAddressW(HMODULE module, const char *name)
{
    (void)module;   // 我们的 GetModuleHandle 返回不透明句柄，符号只在本 shim 里查
    void *p = wpf_shim_symbol(name);
    if (!p) wpf_set_last_error(127);   // ERROR_PROC_NOT_FOUND
    return (FARPROC)p;
}
FARPROC GetProcAddressA(HMODULE m, const char *n) { return GetProcAddressW(m, n); }
FARPROC GetProcAddress(HMODULE m, const char *n) { return GetProcAddressW(m, n); }
FARPROC IntGetProcAddress(HMODULE m, const char *n) { return GetProcAddressW(m, n); }
FARPROC GetProcAddressNoThrow(HMODULE m, const char *n) { return GetProcAddressW(m, n); }

// ══════════════════════════════════════════════════════════════════════════
//  给 M7c 的桥接导出
// ══════════════════════════════════════════════════════════════════════════
//   M7c（端到端 HelloWpf）需要知道的唯一一件事是：
//   **托管侧拿到的 HWND 就是 X11 的 Window XID**，所以
//   `MilVisualTarget_AttachToHwnd(hwnd)` 里的 hwnd 可以直接喂给 M1 的
//   `X11PresentationTarget`。下面这几个导出是为了让 M7c 能：
//     · 自证拿到了合法的 HWND（GetX11Window 返回 0 = 不是本 shim 的窗口）；
//     · 在需要时自己驱动一次 X 事件泵（而不是依赖 Dispatcher）；
//     · 在没有 X 时拿到**明确的原因字符串**（而不是一个空指针）。

#define WPFWIN32_SHIM_VERSION 1

int WpfLinuxWin32_ShimVersion(void) { return WPFWIN32_SHIM_VERSION; }

const char *WpfLinuxWin32_LastError(void)
{
    // [1400 诊断] 合成两个来源：`dpy_error`（X 连接不可用）+ `x_error`（XSetErrorHandler 抓到的
    //   异步 X 错误：error_code/request_code/minor_code）。两者都有时拼在一起给出；只有其一时直接返回。
    //   为什么必须合成：建窗失败可能是"X 不可用"（dpy_error 有、x_error 空），也可能是
    //   "请求被 server 拒了"（dpy_error 空、x_error 有）—— 只回 dpy_error 会让后一种情况看起来"没原因"。
    static char buf[832];
    if (g_wpf.dpy_error[0] && g_wpf.x_error[0]) {
        snprintf(buf, sizeof(buf), "%s | Xlib: %s", g_wpf.dpy_error, g_wpf.x_error);
        return buf;
    }
    if (g_wpf.dpy_error[0]) return g_wpf.dpy_error;
    if (g_wpf.x_error[0]) return g_wpf.x_error;
    return "";
}

int WpfLinuxWin32_EnsureX11(void) { return wpf_x11_ensure(); }

void *WpfLinuxWin32_GetX11Display(void)
{
    return wpf_x11_ensure() ? (void *)g_wpf.dpy : NULL;
}

int WpfLinuxWin32_GetX11Screen(void) { return wpf_x11_ensure() ? g_wpf.screen : -1; }

int WpfLinuxWin32_GetX11ConnectionNumber(void)
{
    return wpf_x11_ensure() ? g_wpf.xfd : -1;
}

// HWND → X11 Window。窗口不在表里（含 WPF 还没建窗）时返回 0。
unsigned long WpfLinuxWin32_GetX11Window(HWND hwnd)
{
    if (!hwnd) return 0;
    wpf_global_init();
    wpf_lock();
    int known = wpf_window_find(hwnd) != NULL;
    pthread_mutex_unlock(&g_wpf.lock);
    return known ? (unsigned long)(uintptr_t)hwnd : 0;
}

// X11 root window（== GetDesktopWindow 的返回值）。可直接当「桌面」靶子。
unsigned long WpfLinuxWin32_GetX11RootWindow(void)
{
    return wpf_x11_ensure() ? (unsigned long)g_wpf.root : 0;
}

// 手动泵一次：把当前待处理的 X 事件翻译成 MSG 投进**调用线程**的队列，
// 返回产生的消息条数。timeout_ms < 0 表示只做非阻塞轮询。
int WpfLinuxWin32_PumpOnce(int timeout_ms)
{
    wpf_thread *t = wpf_thread_self();
    if (timeout_ms > 0) {
        struct pollfd fds[1];
        int n = 0;
        if (t->wake_read >= 0) { fds[n].fd = t->wake_read; fds[n].events = POLLIN; fds[n].revents = 0; n++; }
        if (g_wpf.dpy && g_wpf.xfd >= 0) {
            wpf_x11_flush();
            fds[n].fd = g_wpf.xfd; fds[n].events = POLLIN; fds[n].revents = 0; n++;
        }
        if (n) poll(fds, (nfds_t)n, timeout_ms);
    }
    if (!g_wpf.dpy) return 0;
    wpf_lock();
    int n = wpf_x11_pump_into_queue(t);
    pthread_mutex_unlock(&g_wpf.lock);
    return n;
}

// 当前活着的窗口数（含 message-only）。M7c 可用来断言「窗口没泄漏」。
int WpfLinuxWin32_WindowCount(void)
{
    wpf_global_init();
    wpf_lock();
    int n = 0;
    for (wpf_window *w = g_wpf.windows; w; w = w->next) n++;
    pthread_mutex_unlock(&g_wpf.lock);
    return n;
}

// 把一条 X11 ClientMessage/自定义消息直接投进**创建该窗口的线程**队列。
// 提供给「从别的线程触发 UI 线程动作」的场景（等价于 PostMessage 的简化版）。
int WpfLinuxWin32_PostUserMessage(HWND hwnd, unsigned int msg, intptr_t wparam, intptr_t lparam)
{
    return PostMessageW(hwnd, msg, (WPARAM)wparam, (LPARAM)lparam) ? 1 : 0;
}

// 该窗口当前的窗口过程地址（0 = 未安装）。A/B 断言 WndProc 链是否真的挂上了。
intptr_t WpfLinuxWin32_GetWndProc(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    intptr_t r = w ? (intptr_t)w->wndproc : 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

// 该窗口注册的类名（UTF-8，指向静态缓冲，仅供诊断打印）。
const char *WpfLinuxWin32_GetClassName(HWND hwnd)
{
    static char buf[256];
    buf[0] = 0;
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    wpf_class *c = w ? wpf_class_find_atom(w->class_atom) : NULL;
    if (c && c->name) snprintf(buf, sizeof(buf), "%s", c->name);
    pthread_mutex_unlock(&g_wpf.lock);
    return buf;
}

// ── 跨边界布局探针（M7b 的硬证据）────────────────────────────────────────
//   单纯「原生侧打印自己的 offset」只能证明原生侧自洽；真正要证明的是
//   **原生布局 == 托管布局**。所以这里把原生的逐字段 offset 交出去，
//   由托管测试拿 `Marshal.OffsetOf` / `Marshal.SizeOf` 的**同一批结构体**
//   逐项比对（见 src/Linux/tests/.../ManagedLayer.Tests/Win32AbiLayoutTests.cs）。
//
//   为什么不用「托管侧去读原生静态变量」：静态变量只能给值，给不了 offset；
//   而 offset 正是这里唯一值得断言的东西。
//
//   返回：写入 out 的整数个数；未知名字返回 -1。
//   每个结构的 out[0] = sizeof，其后是逐字段 offset（顺序见下表）。
int WpfLinuxWin32_AbiLayout(const char *what, int32_t *out, int count)
{
    if (!what || !out) return -1;
    int32_t buf[24];
    int n = 0;
    buf[n++] = (int32_t)sizeof(WPF_MSG);
    if (strcmp(what, "MSG") == 0) {
        buf[n++] = (int32_t)offsetof(WPF_MSG, hwnd);
        buf[n++] = (int32_t)offsetof(WPF_MSG, message);
        buf[n++] = (int32_t)offsetof(WPF_MSG, wParam);
        buf[n++] = (int32_t)offsetof(WPF_MSG, lParam);
        buf[n++] = (int32_t)offsetof(WPF_MSG, time);
        buf[n++] = (int32_t)offsetof(WPF_MSG, pt_x);
        buf[n++] = (int32_t)offsetof(WPF_MSG, pt_y);
    } else if (strcmp(what, "WNDCLASSEX_D") == 0) {
        buf[0] = (int32_t)sizeof(WPF_WNDCLASSEX_D);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, cbSize);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, style);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, lpfnWndProc);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, cbClsExtra);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, cbWndExtra);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, hInstance);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, hIcon);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, hCursor);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, hbrBackground);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, lpszMenuName);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, lpszClassName);
        buf[n++] = (int32_t)offsetof(WPF_WNDCLASSEX_D, hIconSm);
    } else if (strcmp(what, "RECT") == 0) {
        buf[0] = (int32_t)sizeof(WPF_RECT);
        buf[n++] = (int32_t)offsetof(WPF_RECT, left);
        buf[n++] = (int32_t)offsetof(WPF_RECT, top);
        buf[n++] = (int32_t)offsetof(WPF_RECT, right);
        buf[n++] = (int32_t)offsetof(WPF_RECT, bottom);
    } else if (strcmp(what, "WINDOWPOS") == 0) {
        // 字段顺序 == 托管 `NativeMethods.WINDOWPOS`（不是真 Win32 的形态，见 win32_abi.h）
        buf[0] = (int32_t)sizeof(WPF_WINDOWPOS);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, hwnd);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, hwndInsertAfter);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, x);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, y);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, cx);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, cy);
        buf[n++] = (int32_t)offsetof(WPF_WINDOWPOS, flags);
    } else if (strcmp(what, "TRACKMOUSEEVENT") == 0) {
        buf[0] = (int32_t)sizeof(WPF_TRACKMOUSEEVENT);
        buf[n++] = (int32_t)offsetof(WPF_TRACKMOUSEEVENT, cbSize);
        buf[n++] = (int32_t)offsetof(WPF_TRACKMOUSEEVENT, dwFlags);
        buf[n++] = (int32_t)offsetof(WPF_TRACKMOUSEEVENT, hwndTrack);
        buf[n++] = (int32_t)offsetof(WPF_TRACKMOUSEEVENT, dwHoverTime);
    } else if (strcmp(what, "RAWCLASSIFICATION") == 0) {
        // #U：Unicode 分类表的跨边界结构（Classification.cs:161-199）
        buf[0] = (int32_t)sizeof(WPF_RAW_CLASSIFICATION_TABLES);
        buf[n++] = (int32_t)offsetof(WPF_RAW_CLASSIFICATION_TABLES, unicode_classes);
        buf[n++] = (int32_t)offsetof(WPF_RAW_CLASSIFICATION_TABLES, character_attributes);
        buf[n++] = (int32_t)offsetof(WPF_RAW_CLASSIFICATION_TABLES, mirroring);
        buf[n++] = (int32_t)offsetof(WPF_RAW_CLASSIFICATION_TABLES, combining_marks);
    } else if (strcmp(what, "CHARATTR") == 0) {
        // #U：CharacterAttribute 是 Pack=1（托管侧显式声明），字段 offset 必须逐字节对上
        buf[0] = (int32_t)sizeof(WPF_CHAR_ATTR);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, script);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, item_class);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, flags);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, break_type);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, bidi);
        buf[n++] = (int32_t)offsetof(WPF_CHAR_ATTR, line_break);
    } else if (strcmp(what, "COMBININGMARKS") == 0) {
        buf[0] = (int32_t)sizeof(WPF_COMBINING_MARKS);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combining_chars_indexes);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combining_chars_indexes_table_length);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combining_chars_indexes_table_segment_length);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combining_mark_indexes);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combining_mark_indexes_table_length);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combination_chars);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combination_chars_base_count);
        buf[n++] = (int32_t)offsetof(WPF_COMBINING_MARKS, combination_chars_mark_count);
    } else if (strcmp(what, "PAINTSTRUCT") == 0) {
        // 托管 PAINTSTRUCT 把 by-value 的 RECT 摊平成了 4 个 int
        // （NativeMethodsCLR.cs:1617 的 rcPaint_left/top/right/bottom），
        // 所以这里按**摊平后的字段名**报偏移，才能和 Marshal.OffsetOf 一一对上。
        buf[0] = (int32_t)sizeof(WPF_PAINTSTRUCT);
        buf[n++] = (int32_t)offsetof(WPF_PAINTSTRUCT, hdc);
        buf[n++] = (int32_t)offsetof(WPF_PAINTSTRUCT, fErase);
        buf[n++] = (int32_t)(offsetof(WPF_PAINTSTRUCT, rcPaint) + offsetof(WPF_RECT, left));
        buf[n++] = (int32_t)(offsetof(WPF_PAINTSTRUCT, rcPaint) + offsetof(WPF_RECT, top));
        buf[n++] = (int32_t)(offsetof(WPF_PAINTSTRUCT, rcPaint) + offsetof(WPF_RECT, right));
        buf[n++] = (int32_t)(offsetof(WPF_PAINTSTRUCT, rcPaint) + offsetof(WPF_RECT, bottom));
        buf[n++] = (int32_t)offsetof(WPF_PAINTSTRUCT, fRestore);
        buf[n++] = (int32_t)offsetof(WPF_PAINTSTRUCT, fIncUpdate);
        buf[n++] = (int32_t)offsetof(WPF_PAINTSTRUCT, rgbReserved);
    } else if (strcmp(what, "LOGFONT") == 0) {
        buf[0] = (int32_t)sizeof(WPF_LOGFONT);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfHeight);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfWidth);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfEscapement);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfOrientation);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfWeight);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfItalic);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfUnderline);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfStrikeOut);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfCharSet);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfOutPrecision);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfClipPrecision);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfQuality);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfPitchAndFamily);
        buf[n++] = (int32_t)offsetof(WPF_LOGFONT, lfFaceName);
    } else if (strcmp(what, "NONCLIENTMETRICS") == 0) {
        buf[0] = (int32_t)sizeof(WPF_NONCLIENTMETRICS);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, cbSize);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iBorderWidth);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iScrollWidth);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iScrollHeight);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iCaptionWidth);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iCaptionHeight);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, lfCaptionFont);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iSmCaptionWidth);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iSmCaptionHeight);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, lfSmCaptionFont);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iMenuWidth);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, iMenuHeight);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, lfMenuFont);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, lfStatusFont);
        buf[n++] = (int32_t)offsetof(WPF_NONCLIENTMETRICS, lfMessageFont);
    } else if (strcmp(what, "ICONMETRICS") == 0) {
        buf[0] = (int32_t)sizeof(WPF_ICONMETRICS);
        buf[n++] = (int32_t)offsetof(WPF_ICONMETRICS, cbSize);
        buf[n++] = (int32_t)offsetof(WPF_ICONMETRICS, iHorzSpacing);
        buf[n++] = (int32_t)offsetof(WPF_ICONMETRICS, iVertSpacing);
        buf[n++] = (int32_t)offsetof(WPF_ICONMETRICS, iTitleWrap);
        buf[n++] = (int32_t)offsetof(WPF_ICONMETRICS, lfFont);
    } else if (strcmp(what, "MONITORINFOEX") == 0) {
        buf[0] = (int32_t)sizeof(WPF_MONITORINFOEX);
        buf[n++] = (int32_t)offsetof(WPF_MONITORINFOEX, cbSize);
        buf[n++] = (int32_t)offsetof(WPF_MONITORINFOEX, rcMonitor);
        buf[n++] = (int32_t)offsetof(WPF_MONITORINFOEX, rcWork);
        buf[n++] = (int32_t)offsetof(WPF_MONITORINFOEX, dwFlags);
        buf[n++] = (int32_t)offsetof(WPF_MONITORINFOEX, szDevice);
    } else {
        return -1;
    }
    if (count < n) return -1;
    for (int i = 0; i < n; i++) out[i] = buf[i];
    return n;
}
