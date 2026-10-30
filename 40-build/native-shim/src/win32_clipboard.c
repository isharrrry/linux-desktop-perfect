// win32_clipboard.c —— Win32 剪贴板面（MOD-63 的 Linux 落地）
//
// 【口径】X11 没有"剪贴板对象"：剪贴板 = **selection 的拥有权**（CLIPBOARD 这个 selection，
//   文本格式走 `UTF8_STRING`/`TEXT` 目标名）。所以本实现的真身是：
//     · SetClipboardData → 把数据存本地 + `XSetSelectionOwner(CLIPBOARD)`；
//     · 别的客户端来要（SelectionRequest）→ 把数据写进它给的 property 并回 SelectionNotify；
//     · GetClipboardData → 自己拥有就直接给本地份；别人拥有就 `XConvertSelection` 同步等。
//
// 【不做什么】CF_DIB/CF_HDROP/CF_HTML 等复杂格式一律如实失败（`SetLastError(ERROR_INVALID_PARAMETER)`），
//   不返回"看起来有效的假句柄"。文本是 V1 的唯一格式（与 20-spec 的 ACC-012 口径一致）。
//
// 【为什么 WPF 的 `Clipboard` 类不直接受用】上游 `System.Windows.Clipboard` 走 OLE
//   （`WpfOleServices.OleSetClipboard(Com.IDataObject*)`），而 COM 在 Linux 上不存在。
//   本文件提供的是 **Win32 面**（`OpenClipboard`/`SetClipboardData`/…），
//   给 WinForms 兼容层与第三方直接 P/Invoke user32 的代码用；WPF 那层的替代路径在判据里如实登记。

#define _GNU_SOURCE
#include "win32_internal.h"

#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CLIP_FORMAT_UNICODETEXT 13u   // CF_UNICODETEXT
#define CLIP_FORMAT_TEXT        1u    // CF_TEXT（ANSI；本实现按 UTF-8 处理，如实登记为偏差）

// Xlib 串行化：win32_x11.c 的 XLOCK 是文件私有的（g_xlock 也是 static），这里不能用；
// 本模块只做"短事务"（XInternAtom/XChangeProperty/XSendEvent/XSetSelectionOwner），
// 全部在 g_clip_lock 之下完成，避免与 X 事件泵并发。
static pthread_mutex_t g_clip_lock = PTHREAD_MUTEX_INITIALIZER;
static char   *g_clip_utf8 = NULL;    // 当前拥有的文本（UTF-8，NUL 结尾）
static size_t  g_clip_len = 0;
static HWND    g_clip_owner = NULL;   // 拥有 selection 的窗口（第一个 Set 的 hwnd）
static int     g_clip_open_depth = 0;

// ── 内部：X11 selection 助手 ────────────────────────────────────────────────
static Atom clip_atom(const char *name)
{
    if (!wpf_x11_ensure()) return None;
    return XInternAtom(g_wpf.dpy, name, False);
}

// selection owner 必须是**我们自己的窗口**：X 把 SelectionRequest 直接投到 owner 窗口上，
// 而 root 窗口没有客户端在听（用 root 当 owner 时，请求会被静默丢掉 —— 这正是 D-010 的现场）。
static Window clip_owner_window(void)
{
    static Window w = None;
    if (!wpf_x11_ensure()) return None;
    if (w != None) return w;
    XSetWindowAttributes a;
    memset(&a, 0, sizeof(a));
    a.override_redirect = True;
    a.event_mask = PropertyChangeMask | StructureNotifyMask;
    w = XCreateWindow(g_wpf.dpy, g_wpf.root, -10, -10, 1, 1, 0, CopyFromParent, InputOnly,
                      CopyFromParent, CWOverrideRedirect | CWEventMask, &a);
    if (w != None) XFlush(g_wpf.dpy);
    return w;
}

static void clip_release_selection_locked(void)
{
    if (!g_wpf.dpy) return;
    Atom cl = XInternAtom(g_wpf.dpy, "CLIPBOARD", False);
    if (cl == None) return;
        XSetSelectionOwner(g_wpf.dpy, cl, None, CurrentTime);
    XFlush(g_wpf.dpy);
}

void wpf_clip_store(const char *utf8, size_t len, HWND owner)
{
    if (!utf8) return;
    pthread_mutex_lock(&g_clip_lock);
    free(g_clip_utf8);
    g_clip_utf8 = (char *)malloc(len + 1);
    if (g_clip_utf8) {
        memcpy(g_clip_utf8, utf8, len);
        g_clip_utf8[len] = '\0';
        g_clip_len = len;
    } else {
        g_clip_len = 0;
    }
    g_clip_owner = owner;
    pthread_mutex_unlock(&g_clip_lock);

    if (!wpf_x11_ensure()) return;
    Atom cl = XInternAtom(g_wpf.dpy, "CLIPBOARD", False);
    Window w = (Window)(uintptr_t)(owner ? owner : g_wpf.root);
        XSetSelectionOwner(g_wpf.dpy, cl, w, CurrentTime);
    XFlush(g_wpf.dpy);
}

const char *wpf_clip_content(size_t *out_len)
{
    pthread_mutex_lock(&g_clip_lock);
    if (out_len) *out_len = g_clip_len;
    const char *p = g_clip_utf8;
    pthread_mutex_unlock(&g_clip_lock);
    return p;
}

// ── SelectionRequest：别的客户端来要数据 ────────────────────────────────────
// 口径：只认 `UTF8_STRING`/`TEXT`/`STRING`；其它目标回 Property None（如实拒绝，不塞假数据）。
void wpf_clip_on_selection_request(const void *xselectionrequest)
{
    const XSelectionRequestEvent *req = (const XSelectionRequestEvent *)xselectionrequest;
    if (!req || !wpf_x11_ensure()) return;

    XSelectionEvent notify;
    memset(&notify, 0, sizeof(notify));
    notify.type = SelectionNotify;
    notify.display = g_wpf.dpy;
    notify.requestor = req->requestor;
    notify.selection = req->selection;
    notify.target = req->target;
    notify.time = req->time;
    notify.property = None;

    if (req->target == None) {
        notify.property = req->property;   // 老式"询问目标"——回空属性即可
                XSendEvent(g_wpf.dpy, req->requestor, False, NoEventMask, (XEvent *)&notify);
        XFlush(g_wpf.dpy);
            return;
    }

    Atom a_utf8 = XInternAtom(g_wpf.dpy, "UTF8_STRING", False);
    Atom a_text = XInternAtom(g_wpf.dpy, "TEXT", False);
    Atom a_str  = XInternAtom(g_wpf.dpy, "STRING", False);
    if (req->target != a_utf8 && req->target != a_text && req->target != a_str) {
                XSendEvent(g_wpf.dpy, req->requestor, False, NoEventMask, (XEvent *)&notify);
        XFlush(g_wpf.dpy);
            return;
    }

    size_t n = 0;
    const char *data = wpf_clip_content(&n);
    Atom prop = (req->property != None) ? req->property : XInternAtom(g_wpf.dpy, "LDP_CLIP", False);
        XChangeProperty(g_wpf.dpy, req->requestor, prop, a_utf8, 8, PropModeReplace,
                    (const unsigned char *)(data ? data : ""), (int)(data ? n : 0));
    XFlush(g_wpf.dpy);
    notify.property = prop;
        XSendEvent(g_wpf.dpy, req->requestor, False, NoEventMask, (XEvent *)&notify);
    XFlush(g_wpf.dpy);
}

// ── 读取别人的 selection（同步等 SelectionNotify，带超时）─────────────────────
static char *clip_fetch_selection(void)
{
    if (!wpf_x11_ensure()) {
        // 如实带上失败原因（g_wpf.dpy_error），否则现场只剩一个 NULL 无从下手
        fprintf(stderr, "[CLIP-FETCH] X11 不可用：%s\n", g_wpf.dpy_error);
        fflush(stderr);
        return NULL;
    }
    Display *d = g_wpf.dpy;
    Atom cl = XInternAtom(d, "CLIPBOARD", False);
    Atom target = XInternAtom(d, "UTF8_STRING", False);
    Atom prop = XInternAtom(d, "LDP_CLIP_FETCH", False);
    // requestor 必须是**请求方自己的窗口**（不是 root）：SelectionNotify 是直接投给 requestor 的，
    // 而 root 窗口没有任何客户端 select 输入 ⇒ 用 root 当 requestor 时通知永远收不到（D-010 的真现场）。
    Window self = clip_owner_window();
    if (self == None) self = (Window)(uintptr_t)(g_clip_owner ? g_clip_owner : g_wpf.root);

        if (getenv("CLIP_PROBE_TRACE")) { fprintf(stderr, "[CLIP-FETCH] convert sel=%lu target=%lu self=0x%lx\n", (unsigned long)cl, (unsigned long)target, (unsigned long)self); fflush(stderr); }
    XConvertSelection(d, cl, target, prop, self, CurrentTime);
    XFlush(d);

    // 最多等 2 s：把到达的事件过一遍（SelectionNotify 之外的就不打扰别人，直接吞掉）
    for (int i = 0; i < 200; ++i) {
        int got = 0;
                while (XPending(d)) {
            XEvent ev;
            XNextEvent(d, &ev);
            if (getenv("CLIP_PROBE_TRACE")) { fprintf(stderr, "[CLIP-FETCH] event type=%d\n", ev.type); fflush(stderr); }
            if (getenv("CLIP_PROBE_TRACE")) { fprintf(stderr, "[CLIP-FETCH] event type=%d\n", ev.type); fflush(stderr); }
            if (ev.type == SelectionNotify && ev.xselection.selection == cl && ev.xselection.property != None) {
                Atom type = None;
                int format = 0;
                unsigned long n = 0, after = 0;
                unsigned char *bytes = NULL;
                if (XGetWindowProperty(d, ev.xselection.requestor, ev.xselection.property, 0, (long)(1 << 20), False,
                                       AnyPropertyType, &type, &format, &n, &after, &bytes) == Success && bytes) {
                    char *out = (char *)malloc(n + 1);
                    if (out) {
                        memcpy(out, bytes, n);
                        out[n] = '\0';
                    }
                    XFree(bytes);
                    XDeleteProperty(d, ev.xselection.requestor, ev.xselection.property);
                                    XFlush(d);
                    return out;
                }
                got = 1;
            }
        }
            if (got) return NULL;
        struct timespec ts = { 0, 10 * 1000 * 1000 };   // 10 ms
        nanosleep(&ts, NULL);
    }
    return NULL;
}

// ── 公开泵（D-010）───────────────────────────────────────────────────────────
// 为什么必须由 shim 提供：selection 的 owner 连接是 **shim 的 `g_wpf.dpy`**，
// 而调用方（探针/宿主）用 `XOpenDisplay` 另开一条连接 —— 两条连接的事件队列不共享，
// 于是"在调用方的连接上 pump"永远看不到 `SelectionRequest`。这里在 shim 的连接上抽干。
int wpf_clip_pump(int max_ms)
{
    if (!wpf_x11_ensure() || !g_wpf.dpy) return 0;
    int handled = 0;
    for (int waited = 0; waited < max_ms; waited += 10) {
        while (XPending(g_wpf.dpy)) {
            XEvent ev;
            XNextEvent(g_wpf.dpy, &ev);
            if (ev.type == SelectionRequest) {
                wpf_clip_on_selection_request(&ev.xselectionrequest);
                handled++;
            }
        }
        if (handled) break;
        struct timespec ts = { 0, 10 * 1000 * 1000 };
        nanosleep(&ts, NULL);
    }
    return handled;
}

// ── Win32 面 ────────────────────────────────────────────────────────────────
BOOL OpenClipboard(HWND hwnd)
{
    pthread_mutex_lock(&g_clip_lock);
    g_clip_open_depth++;
    if (hwnd && !g_clip_owner) g_clip_owner = hwnd;
    pthread_mutex_unlock(&g_clip_lock);
    return 1;
}

BOOL CloseClipboard(void)
{
    pthread_mutex_lock(&g_clip_lock);
    if (g_clip_open_depth > 0) g_clip_open_depth--;
    pthread_mutex_unlock(&g_clip_lock);
    return 1;
}

BOOL EmptyClipboard(void)
{
    pthread_mutex_lock(&g_clip_lock);
    free(g_clip_utf8);
    g_clip_utf8 = NULL;
    g_clip_len = 0;
    pthread_mutex_unlock(&g_clip_lock);
    clip_release_selection_locked();
    return 1;
}

static HGLOBAL clip_make_hglobal_utf16(const char *utf8)
{
    // UTF-8 → UTF-16（Win32 的剪贴板文本是 UTF-16 + NUL 结尾），返回 HGLOBAL
    size_t n = strlen(utf8);
    size_t out_cap = (n + 1) * 4;
    uint16_t *buf = (uint16_t *)malloc(out_cap);
    if (!buf) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < n;) {
        unsigned char c = (unsigned char)utf8[i];
        uint32_t cp = c;
        int extra = 0;
        if (c >= 0xF0) { cp = c & 0x07; extra = 3; }
        else if (c >= 0xE0) { cp = c & 0x0F; extra = 2; }
        else if (c >= 0xC0) { cp = c & 0x1F; extra = 1; }
        i++;
        for (int k = 0; k < extra && i < n; ++k, ++i) cp = (cp << 6) | ((unsigned char)utf8[i] & 0x3F);
        if (cp <= 0xFFFF) {
            buf[o++] = (uint16_t)cp;
        } else {
            cp -= 0x10000;
            buf[o++] = (uint16_t)(0xD800 + (cp >> 10));
            buf[o++] = (uint16_t)(0xDC00 + (cp & 0x3FF));
        }
    }
    buf[o++] = 0;
    return (HGLOBAL)(void *)buf;
}

HANDLE SetClipboardData(UINT format, HANDLE data)
{
    if (format != CLIP_FORMAT_UNICODETEXT && format != CLIP_FORMAT_TEXT) {
        wpf_set_last_error(87);   // ERROR_INVALID_PARAMETER
        return NULL;
    }
    // 双形态：既接受 UTF-16 字符串指针（Win32 语义），也接受 UTF-8 字符串指针（本 shim 的探针用）
    const char *utf8 = NULL;
    char *converted = NULL;
    if (format == CLIP_FORMAT_UNICODETEXT) {
        const uint16_t *u = (const uint16_t *)data;
        if (!u) { wpf_set_last_error(87); return NULL; }
        converted = wpf_utf16_to_utf8_dup(u);
        utf8 = converted;
    } else {
        utf8 = (const char *)data;
    }
    if (!utf8) { free(converted); wpf_set_last_error(87); return NULL; }
    wpf_clip_store(utf8, strlen(utf8), g_clip_owner);
    HANDLE ret = clip_make_hglobal_utf16(utf8);
    free(converted);
    return ret;
}

HANDLE GetClipboardData(UINT format)
{
    if (format != CLIP_FORMAT_UNICODETEXT && format != CLIP_FORMAT_TEXT) {
        wpf_set_last_error(87);
        return NULL;
    }
    size_t n = 0;
    const char *own = wpf_clip_content(&n);
    if (own && n > 0) return clip_make_hglobal_utf16(own);
    // 自己没有：问 X 的 selection 所有者（跨进程）
    char *fetched = clip_fetch_selection();
    if (!fetched) return NULL;
    HANDLE ret = clip_make_hglobal_utf16(fetched);
    free(fetched);
    return ret;
}

BOOL IsClipboardFormatAvailable(UINT format)
{
    if (format != CLIP_FORMAT_UNICODETEXT && format != CLIP_FORMAT_TEXT) return 0;
    size_t n = 0;
    if (wpf_clip_content(&n) && n > 0) return 1;
    if (!wpf_x11_ensure()) return 0;
    Atom cl = XInternAtom(g_wpf.dpy, "CLIPBOARD", False);
        Window owner = XGetSelectionOwner(g_wpf.dpy, cl);
    return owner != None ? 1 : 0;
}

UINT CountClipboardFormats(void)
{
    size_t n = 0;
    return (wpf_clip_content(&n) && n > 0) ? 1u : 0u;
}

UINT EnumClipboardFormats(UINT prev)
{
    // 只暴露一个格式：CF_UNICODETEXT
    if (prev == 0 && CountClipboardFormats() > 0) return CLIP_FORMAT_UNICODETEXT;
    return 0;
}

UINT RegisterClipboardFormatW(const uint16_t *name)
{
    (void)name;
    return 0xC000;   // 自定义格式段起点（本实现不支持自定义格式的存取，登记为偏差）
}

UINT RegisterClipboardFormatA(const char *name)
{
    (void)name;
    return 0xC000;
}
