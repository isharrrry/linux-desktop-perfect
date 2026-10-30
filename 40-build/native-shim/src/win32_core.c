// WPF-on-Linux · M7b · Win32 shim —— 核心状态与窗口生命周期
//
// 本文件实现「窗口这张表」以及所有只跟表打交道的 user32 API。
// X11 调用一律走 win32_x11.c，消息队列一律走 win32_msg.c。

#define _GNU_SOURCE
#include "win32_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdarg.h>   // 【波 58】wpf_wmsize_diag 的可变参数
#include <stddef.h>   // ★W70A（D-G72）：GetMonitorInfoW 要用 offsetof 读 cbSize 边界
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

wpf_global g_wpf;

// ── LastError ──────────────────────────────────────────────────────────────
// .NET 的 `SetLastError=true` 在 Unix 上走 `Marshal.GetLastPInvokeError`，
// 它读的是 runtime 自己维护的一个 TLS 槽。原生侧必须调 `pal_errno` 的等价物
// 才能被托管侧读到——.NET 在 Unix 上把这个槽映射到 **errno**。
// 所以：set_last_error 就是设置 errno；这是 .NET 运行时在 Unix 上的既定约定
// （见 runtime 的 `System.Native` / `Marshal.GetLastPInvokeError` 实现）。
// 我们不臆造别的机制。
void wpf_set_last_error(uint32_t code) { errno = (int)code; }
uint32_t wpf_get_last_error(void) { return (uint32_t)errno; }

// ── 时钟 ───────────────────────────────────────────────────────────────────
// Win32 的 GetTickCount 是「自系统启动以来的毫秒数，32 位回绕」。我们给的是
// 「自 shim 首次调用以来的毫秒数」，并且**在 32 位空间里单调**（用 clock_bias_ms
// 保证跨过 2^31 不会回绕成负数，Dispatcher.PromoteTimers 拿它跟 dueTime 比大小）。
uint64_t wpf_now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000u + (uint64_t)(ts.tv_nsec / 1000000);
}

static pthread_once_t g_init_once = PTHREAD_ONCE_INIT;

static void wpf_do_global_init(void)
{
    pthread_mutexattr_t attr;
    pthread_mutexattr_init(&attr);
    pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(&g_wpf.lock, &attr);
    pthread_mutexattr_destroy(&attr);

    g_wpf.dpy = NULL;
    g_wpf.screen = 0;
    g_wpf.root = 0;
    g_wpf.xfd = -1;
    g_wpf.x_failed = 0;
    g_wpf.windows = NULL;
    g_wpf.classes = NULL;
    g_wpf.timers = NULL;
    g_wpf.threads = NULL;
    g_wpf.next_atom = 0xC000;          // 用户类的 atom 从 0xC000 起（Win32 惯例）
    g_wpf.next_registered_msg = WM_APP; // RegisterWindowMessage 的私有段
    g_wpf.msg_extra_info = 0;
    g_wpf.clock_bias_ms = 0;
    g_wpf.x_error[0] = 0;
}

// 前向声明（定义在下方，紧随 wpf_global_init）
static void wpf_win_diag_selftest(void);

void wpf_global_init(void)
{
    pthread_once(&g_init_once, wpf_do_global_init);
    wpf_win_diag_selftest();   // [1400 诊断] WPF_LINUX_WIN_DIAG=selftest 时合成一条，验证打印管线
}

// ── [1400 诊断] 建窗失败时把**真原因**打到 stderr ──────────────────────────────
// 【为什么默认开】`1400` 是本 shim **自己映射**的码（1400 也用于"hwnd 查不到"等 11+ 处），
//   仅凭它分不清「X 不可用 / `XCreateWindow` 失败 / 句柄非法」。而这条路径**只在建窗失败时**
//   打（那本来就意味着应用起不来）⇒ 默认开不会污染正常日志，且"下次真出现 1400 时一定有输出"。
// 【有界】每进程最多 8 条；`WPF_LINUX_WIN_DIAG=1` 另开成功路径（本函数不管成功路径）。
// 【selftest】`WPF_LINUX_WIN_DIAG=selftest` ⇒ 初始化时合成一条，用来验证"打印管线本身没坏"
//   （牙齿：把下面的格式串改坏 ⇒ selftest 里那条 `dpy_error=` 消失 ⇒ 自检必红）。
static int s_win_diag_lines = 0;
void wpf_win_diag_report(const char *where, const char *cls, HWND parent, uint32_t err)
{
    if (s_win_diag_lines >= 8) return;
    s_win_diag_lines++;
    fprintf(stderr,
            "[WIN_DIAG] CreateWindowEx 失败：where=%s last_error=%u（**本 shim 自己映射的码**；"
            "1400=ERROR_INVALID_WINDOW_HANDLE 在这里是复用值，真原因见下）\n",
            where ? where : "?", (unsigned)err);
    fprintf(stderr,
            "[WIN_DIAG]   X 连接: dpy=%s x_failed=%d dpy_error=\"%s\"\n",
            g_wpf.dpy ? "有" : "无", g_wpf.x_failed,
            g_wpf.dpy_error[0] ? g_wpf.dpy_error : "(空)");
    fprintf(stderr,
            "[WIN_DIAG]   Xlib 异步错误: %s\n",
            g_wpf.x_error[0] ? g_wpf.x_error : "(无：请求还没回错误事件，或不是 X 请求失败)");
    fprintf(stderr,
            "[WIN_DIAG]   窗口参数: cls=\"%s\" parent=0x%llx（message_only=%d）\n",
            cls ? cls : "(null)", (unsigned long long)(uintptr_t)parent,
            wpf_is_message_only_parent(parent) ? 1 : 0);
    fflush(stderr);
}

static void wpf_win_diag_selftest(void)
{
    // ⚠️ 重入护栏（实测踩到，2026-09-13）：`xerr` 分支内部会调 `wpf_x11_ensure()`
    //   → `wpf_global_init()` → 又回到本函数 ⇒ **无限递归 ⇒ SIGSEGV(139)**。
    //   自检只允许跑一次，且不允许重入。
    static int s_running = 0, s_done = 0;
    if (s_running || s_done) return;
    const char *e = getenv("WPF_LINUX_WIN_DIAG");
    if (!e) return;
    if (strcmp(e, "xerr") == 0) {          // 牙齿：真 X 错误路径（坏 window id）
        s_running = 1; s_done = 1;
        wpf_x11_diag_selftest_xerr();
        s_running = 0;
        return;
    }
    if (strcmp(e, "selftest") != 0) return;
    s_running = 1; s_done = 1;
    // 合成一条：验证"诊断管线"本身可用（不需要真的建窗失败，也不需要 X）。
    snprintf(g_wpf.dpy_error, sizeof(g_wpf.dpy_error),
             "SELFTEST：这是合成原因串，用于验证 [WIN_DIAG] 打印管线（不是真的建窗失败）");
    wpf_win_diag_report("selftest", "HwndWrapper[selftest]", (HWND)0, 1400);
    s_running = 0;
}

// 全局锁：可重入（PTHREAD_MUTEX_RECURSIVE）。理由：合法的递归路径真实存在——
//   SendMessage → 窗口过程 → PostMessage/GetClientRect/SetWindowLongPtr → 再进锁。
// 用普通互斥量会在那条路径上自死锁，而且只在特定消息下复现，极难定位。
// 调用约定：所有对 g_wpf 的访问都必须持锁；**调用托管窗口过程时不得持锁**
// （WndProc 会回调进来），所以 wpf_dispatch_to_window 是先取快照再放锁再调。
void wpf_lock(void)
{
    wpf_global_init();
    pthread_mutex_lock(&g_wpf.lock);
}

// ── 线程状态 ───────────────────────────────────────────────────────────────
static pthread_key_t g_tls_key;
static pthread_once_t g_tls_once = PTHREAD_ONCE_INIT;

static void wpf_thread_destroy(void *p)
{
    wpf_thread *t = (wpf_thread *)p;
    if (!t) return;
    // 【W146A · TASK-0211 · 关闭第 3 处（fd 生命周期）】修前两行在**锁外** close() 且**不置 -1**
    //   （`-1` 只在创建路径 :210 写）⇒ 站点 A/B 修好之后仍有一条缝：
    //     T 关掉 fd（此处）→ P 取锁、读到**还非空**的 owner_thread（清属主在更晚的 :180-185）
    //     → P 持锁 push → push 末尾 wpf_queue_wake（win32_msg.c:134）的判据是 `t->wake_write < 0`（:152）
    //     —— 而 wake_write 还是那个**已关闭、可能已被别人 pipe()/open() 复用**的 fd 号
    //     ⇒ write() 落到无关 fd 上（后果比 UAF 轻，但不是零）。
    //   修法：**锁内先置 -1 再 close**（次序不可反：先 close 后置 -1 仍留一个"半开"窗口）。
    wpf_lock();
    if (t->wake_read >= 0) { int fd = t->wake_read; t->wake_read = -1; close(fd); }
    if (t->wake_write >= 0) { int fd = t->wake_write; t->wake_write = -1; close(fd); }
    pthread_mutex_unlock(&g_wpf.lock);
    // 【W136A · TASK-0209 · F3「写坏者」堵源】旧实现只 `free(t)` 而**从不把线程从
    //   `g_wpf.threads` 摘链**（全仓 `g_wpf.threads` 只有 :61 初始化与 :191 头插两处写）⇒
    //   线程一死，链上就留下一个指向已释放内存的 `wpf_thread*`：
    //     · `PostThreadMessageW` 按 id 走这条链找人（win32_msg.c 现场件）；
    //     · 任何诊断遍历也会踩它；
    //     · 而 `free(t)` 后的 0x40 块会被下一次 `malloc(0x40)` 复用成消息节点（同尺寸）
    //       ⇒ 队列指针读出来就是消息号（本波崩点 rax=0x102 的来历）。
    //   摘链后：悬挂指针不再挂在全局表上 ⇒ "同一块内存又被当成线程队列来写"的第一条路径断掉。
    wpf_lock();
    if (g_wpf.threads == t) {
        g_wpf.threads = t->next;
    } else {
        for (wpf_thread *q = g_wpf.threads; q; q = q->next)
            if (q->next == t) { q->next = t->next; break; }
    }
    pthread_mutex_unlock(&g_wpf.lock);

    // 【W136A · TASK-0209 · F3b】把**窗口 / 定时器**上指向本线程的 owner_thread 一并清掉。
    //   依据（逐字）：win32_msg.c:779 `target = w ? (wpf_thread *)w->owner_thread : NULL;`
    //   紧接着 :803 `wpf_queue_push(target, &m);` —— **没有任何"target 还活着吗"的检查**；
    //   而全仓 `grep -rn "owner_thread" src/*.c` = 6 处**读**、**0 处置空** ⇒ 线程一死，
    //   它的窗口仍握着一个指向已释放 wpf_thread 的指针，任何线程 `PostMessageW(那个 hwnd)` 都会
    //   把它送进 `wpf_queue_push`（与 D-G109 同一形态）。
    //   实测（本件 sandbox/repro/repro_owner.c：真窗口 + 真 PostMessageW + 真线程退出）：
    //     修前 窗口仍在表里=1 owner_thread=悬挂线程 ⇒ rc=139，崩点仍在链遍历。
    //   置空后：PostMessageW 走 `if (!target)` 分支 ⇒ 窗口在表里 ⇒ `target = wpf_thread_self()`
    //     ⇒ 消息落到**调用者队列**（不再写已释放内存；"这个窗口的线程已经死了"本来就是事实）。
    wpf_lock();
    for (wpf_window *w = g_wpf.windows; w; w = w->next)
        if (w->owner_thread == (void *)t) w->owner_thread = NULL;
    for (wpf_timer *it = g_wpf.timers; it; it = it->next)
        if (it->owner_thread == (void *)t) it->owner_thread = NULL;
    pthread_mutex_unlock(&g_wpf.lock);

    wpf_msg_node *n = t->head;                     // ⚠️ 次序：先遍历释放节点，**再**归零
    while (n) { wpf_msg_node *nx = n->next; free(n); n = nx; }
    t->head = NULL; t->tail = NULL;                // 显式归零（不依赖"反正要 free"）
    free(t);
}

static void wpf_make_tls(void)
{
    pthread_key_create(&g_tls_key, wpf_thread_destroy);
}

wpf_thread *wpf_thread_self(void)
{
    wpf_global_init();
    pthread_once(&g_tls_once, wpf_make_tls);

    wpf_thread *t = (wpf_thread *)pthread_getspecific(g_tls_key);
    if (t) return t;

    t = (wpf_thread *)calloc(1, sizeof(wpf_thread));
    t->self = t;
    t->head = t->tail = NULL;
    t->quit_code = -1;
    t->wake_read = t->wake_write = -1;

    int fds[2];
    if (pipe(fds) == 0) {
        t->wake_read = fds[0];
        t->wake_write = fds[1];
        // 非阻塞读端：唤醒水位的字节我们只是「抽干」，不希望 poll 假醒后 read 阻塞。
        int fl = fcntl(t->wake_read, 3 /*F_GETFL*/, 0);
        fcntl(t->wake_read, 4 /*F_SETFL*/, fl | 04000 /*O_NONBLOCK*/);
        fl = fcntl(t->wake_write, 3, 0);
        fcntl(t->wake_write, 4, fl | 04000);
    }

    pthread_setspecific(g_tls_key, t);
    wpf_lock();
    t->next = g_wpf.threads;
    g_wpf.threads = t;
    pthread_mutex_unlock(&g_wpf.lock);
    return t;
}

// ── UTF-16 ↔ UTF-8 ─────────────────────────────────────────────────────────
// .NET 在 Unix 上把 `CharSet.Unicode` 的 string 编成 **UTF-16LE，2 字节/单元**
// （不是 4 字节 wchar_t）。所以这里自己写转换，不碰 wchar_t。
char *wpf_utf16_to_utf8_dup(const uint16_t *s)
{
    if (!s) return NULL;
    size_t cap = 1;
    for (const uint16_t *p = s; *p; p++) cap += 4;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; s[i]; i++) {
        uint32_t cp = s[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (s[++i] - 0xDC00);
        }
        if (cp < 0x80) {
            out[o++] = (char)cp;
        } else if (cp < 0x800) {
            out[o++] = (char)(0xC0 | (cp >> 6));
            out[o++] = (char)(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            out[o++] = (char)(0xE0 | (cp >> 12));
            out[o++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[o++] = (char)(0x80 | (cp & 0x3F));
        } else {
            out[o++] = (char)(0xF0 | (cp >> 18));
            out[o++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            out[o++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[o++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    out[o] = 0;
    return out;
}

uint16_t *wpf_utf8_to_utf16_dup(const char *s)
{
    if (!s) return NULL;
    size_t n = strlen(s);
    uint16_t *out = (uint16_t *)malloc((n + 1) * sizeof(uint16_t));
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < n;) {
        uint8_t c = (uint8_t)s[i];
        uint32_t cp;
        int extra;
        if (c < 0x80)        { cp = c;        extra = 0; }
        else if (c < 0xE0)   { cp = c & 0x1F; extra = 1; }
        else if (c < 0xF0)   { cp = c & 0x0F; extra = 2; }
        else                 { cp = c & 0x07; extra = 3; }
        i++;
        for (int k = 0; k < extra && i < n; k++, i++)
            cp = (cp << 6) | ((uint8_t)s[i] & 0x3F);
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out[o++] = (uint16_t)(0xD800 + (cp >> 10));
            out[o++] = (uint16_t)(0xDC00 + (cp & 0x3FF));
        } else {
            out[o++] = (uint16_t)cp;
        }
    }
    out[o] = 0;
    return out;
}

// ── 窗口表 ─────────────────────────────────────────────────────────────────
wpf_window *wpf_window_find(HWND hwnd)
{
    for (wpf_window *w = g_wpf.windows; w; w = w->next)
        if (w->hwnd == hwnd) return w;
    return NULL;
}

wpf_window *wpf_window_add(HWND hwnd)
{
    wpf_window *w = (wpf_window *)calloc(1, sizeof(wpf_window));
    w->hwnd = hwnd;
    w->created_ms = wpf_now_ms();
    w->width = w->height = 0;
    w->next = g_wpf.windows;
    g_wpf.windows = w;
    return w;
}

void wpf_window_remove(HWND hwnd)
{
    wpf_window **pp = &g_wpf.windows;
    while (*pp) {
        if ((*pp)->hwnd == hwnd) {
            wpf_window *victim = *pp;
            *pp = victim->next;
            wpf_prop *p = victim->props;
            while (p) { wpf_prop *nx = p->next; free(p->name); free(p); p = nx; }
            free(victim);
            return;
        }
        pp = &(*pp)->next;
    }
}

wpf_class *wpf_class_find_atom(ATOM atom)
{
    for (wpf_class *c = g_wpf.classes; c; c = c->next)
        if (c->atom == atom) return c;
    return NULL;
}

wpf_class *wpf_class_find_name(const char *name)
{
    if (!name) return NULL;
    for (wpf_class *c = g_wpf.classes; c; c = c->next)
        if (c->name && strcmp(c->name, name) == 0) return c;
    return NULL;
}

int wpf_is_message_only_parent(HWND parent)
{
    return parent == HWND_MESSAGE;
}

// ── 窗口类注册 ─────────────────────────────────────────────────────────────
// X11 没有「窗口类」这个概念（XCreateSimpleWindow 直接建窗）。但托管侧
// HwndWrapper 的整个生命周期都围着它转：RegisterClassEx 拿到 atom →
// CreateWindowEx(className) → 销毁时 UnregisterClass(atom)。所以这里做一个
// **纯进程内的类注册表**：atom 单调分配、类名唯一、lpfnWndProc 存下来作为
// 该窗口的「默认窗口过程」。语义上等价于 Win32 的那部分，且不需要 X11 配合。
ATOM RegisterClassExW(const WPF_WNDCLASSEX_D *wc)
{
    wpf_global_init();
    if (!wc) { wpf_set_last_error(87); return 0; }

    char *name = wpf_utf16_to_utf8_dup(wc->lpszClassName);
    if (!name || name[0] == 0) { free(name); wpf_set_last_error(87); return 0; }

    wpf_lock();
    if (wpf_class_find_name(name)) {
        // Win32 里同名类重复注册返回 0 + ERROR_CLASS_ALREADY_EXISTS(1410)。
        // 托管侧 HwndWrapper 用 GUID 做类名，正常不会撞；撞了就是真错误。
        pthread_mutex_unlock(&g_wpf.lock);
        free(name);
        wpf_set_last_error(1410);
        return 0;
    }
    wpf_class *c = (wpf_class *)calloc(1, sizeof(wpf_class));
    c->atom = g_wpf.next_atom++;
    c->name = name;
    c->wndproc = wc->lpfnWndProc;
    c->style = wc->style;
    c->background = wc->hbrBackground;
    c->cb_wnd_extra = wc->cbWndExtra;
    c->next = g_wpf.classes;
    g_wpf.classes = c;
    ATOM atom = c->atom;
    pthread_mutex_unlock(&g_wpf.lock);
    return atom;
}
ATOM RegisterClassExA(const WPF_WNDCLASSEX_D *wc) { return RegisterClassExW(wc); }
ATOM RegisterClassEx(const WPF_WNDCLASSEX_D *wc) { return RegisterClassExW(wc); }

// UnregisterClass 的 lpClassName 参数在托管侧是 `IntPtr atomString`——
// HwndWrapper 传的是**低 16 位装着 atom 的整数**（见 HwndWrapper.UnregisterClass
// 的注释：「this function is defined as taking a type lpClassName - but this can
// be an atom. 2 Low Bytes are the atom」），PresentationCore 侧则可能传真字符串指针。
// 两种都要认：值 < 0x10000 且能查到 atom → 按 atom 处理；否则当字符串指针。
int UnregisterClassW(const void *lpClassName, HINSTANCE hInstance)
{
    (void)hInstance;
    wpf_global_init();
    wpf_lock();
    wpf_class **pp = &g_wpf.classes;
    intptr_t raw = (intptr_t)lpClassName;
    while (*pp) {
        int hit = 0;
        if (raw > 0 && raw < 0x10000) {
            hit = ((*pp)->atom == (ATOM)raw);
        } else if (lpClassName) {
            char *nm = wpf_utf16_to_utf8_dup((const uint16_t *)lpClassName);
            hit = (nm && (*pp)->name && strcmp(nm, (*pp)->name) == 0);
            free(nm);
        }
        if (hit) {
            wpf_class *victim = *pp;
            *pp = victim->next;
            free(victim->name);
            free(victim);
            pthread_mutex_unlock(&g_wpf.lock);
            return 1;
        }
        pp = &(*pp)->next;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    wpf_set_last_error(1411);   // ERROR_CLASS_DOES_NOT_EXIST
    return 0;
}
int UnregisterClassA(const void *p, HINSTANCE h) { return UnregisterClassW(p, h); }
int UnregisterClass(const void *p, HINSTANCE h) { return UnregisterClassW(p, h); }

// ── 建窗 ───────────────────────────────────────────────────────────────────
// 【字符集这条坑必须先讲清楚，否则整个窗口骨架都建不起来】
//   托管侧的 DllImport 有两种 CharSet：
//     · `CharSet.Unicode`（如 RegisterClassEx）→ 运行时探测 `名W` 再探测 `名`，
//       string 编成 **UTF-16**（2 字节/单元）；
//     · `CharSet.Auto`（如 CreateWindowEx / RegisterWindowMessage / SetProp /
//       SetWindowText / MessageBox / LoadImage / ExtractIconEx）在 **Unix 上折叠为
//       Ansi**，运行时探测 `名`（裸名）再探测 `名A`，string 编成 **UTF-8**。
//   ⚠️ 本轮实测踩过：最初把裸名一律转发到 `...W`，于是 CreateWindowEx 收到的
//      UTF-8 字节被当成 UTF-16 解释，类名变成乱码 → RegisterClassEx 注册的类
//      查不到 → `ERROR_CLASS_DOES_NOT_EXIST (1411)`，HwndWrapper 构造函数抛
//      Win32Exception，连 `Dispatcher.CurrentDispatcher` 都建不出来。
//   所以本文件的约定是：**裸名 == A（UTF-8）实现**，`...W` 负责先转码。
//   同名类/属性/标题的字符串在两个变体之间走的是同一张表。

// ── CW_USEDEFAULT：Win32 的"用默认几何"哨兵 ─────────────────────────────────
// 【为什么必须处理它（这一条卡住了"任何 WPF 窗口"）】
//   托管侧 `Window`/`HwndSource` 在没显式指定几何时会传 **CW_USEDEFAULT**
//   （= 0x80000000 = INT32_MIN）当 x/y/width/height。首版 shim 把它**原样存下**，
//   于是：
//       w->width  = -2147483648  →  GetClientRect 返回 right = -2147483648
//       → HwndTarget.UpdateWindowAndClientCoordinates() 把客户区映射到屏幕坐标
//       → HwndTarget.CreateUCEResources() 里 `new Rect(0,0,right-left,bottom-top)`
//         **int 溢出**成负数 → `ArgumentException: Width and Height must be non-negative.`
//   实测栈（M7c Phase 2）：HwndTarget.cs:773 ← HwndTarget..ctor:274
//   ← HwndSource.Initialize:267 ← Window.CreateSourceWindow:2519。
//   也就是说：只要应用**不显式**给 Width/Height，窗口就建不出来。
//
// 【正确语义】Win32 里 CW_USEDEFAULT 的含义是"由窗口管理器选位置/尺寸"。
//   X11 上对等的做法是给一组**真实可用**的默认值（WPF 随后会自己 SetWindowPos
//   到真实尺寸）。所以这里把它归一到 800×600、位置 0,0 —— 不是伪造一个尺寸，
//   而是补上"没有窗口管理器时该由谁决定"这一环。
#define WPF_CW_USEDEFAULT ((int32_t)0x80000000)

// 位置：CW_USEDEFAULT → 0（X11 下无层叠管理器，用原点即可；WPF 会立即重定位）
static int32_t normalize_position(int32_t v)
{
    return v == WPF_CW_USEDEFAULT ? 0 : v;
}

// 尺寸：CW_USEDEFAULT 或任何 <= 0 的值 → 默认值。
// X11 不允许 0/负尺寸的窗口（XCreateSimpleWindow 会报 BadValue），
// 所以这里必须兜住，而不是把非法值传给 server。
static int32_t normalize_extent(int32_t v, int32_t fallback)
{
    if (v == WPF_CW_USEDEFAULT || v <= 0) return fallback;
    return v;
}

#define WPF_DEFAULT_WINDOW_W 800
#define WPF_DEFAULT_WINDOW_H 600

// ══════════════════════════════════════════════════════════════════════════
//  【波 58】顶层窗"装得下"：钳制**策略**（屏幕/工作区读数与 X 写入在 win32_x11.c）
// ══════════════════════════════════════════════════════════════════════════
//
// ── 为什么钳在"建窗/改尺寸"这一层（而不是在托管侧或 X 层里各钳一半）────────────
//   · **必须在客户区尺寸被送到 X 之前**：xfwm4 的自动最大化发生在 **map** 那一刻，
//     依据是"帧装不进屏幕"；窗口一旦以 800×600 被 map，WM 就已经最大化了
//     （实测：`windowsize`/`windowmove` 到那时全无效）。所以钳制要早于 `XMapWindow`。
//   · **必须同时改 shim 自己的窗口表**：`GetClientRect`/`GetWindowRect` 是托管侧布局的
//     读数来源；只改 X 侧会让"我们以为 800×600、实际 784×560" ⇒ 二次不一致。
//     这里三条改尺寸的入口（建窗 / MoveWindow / SetWindowPos）走**同一个**函数。
//   · **只在顶层窗上做**：message-only 窗口不 map（无关）；子窗口的坐标/尺寸是父窗口
//     客户区坐标系，与屏幕无关（钳它会破坏 WPF 的子窗口布局）。
//
// ── 为什么不钳"位置"到工作区内**全部**范围 ─────────────────────────────────────
//   只把**负**原点抬到 0（"标题栏别从屏幕外开始"）。正方向上一律不动：
//   Windows 允许窗口故意伸出屏幕右边（无边框/贴边窗靠这个），把它拉回来是另一处行为改变，
//   与本缺陷无关 ⇒ 不做。
//
// ── 与 WM_GETMINMAXINFO 的关系 ────────────────────────────────────────────────
//   上限只有**一个来源**：`wpf_x11_client_size_limit()`（工作区 − 装饰余量）。
//   `WM_GETMINMAXINFO` 的默认值（fill_minmaxinfo_defaults）与 X 的 `WM_NORMAL_HINTS`
//   （wpf_x11_apply_wm_hints）用的都是它 ⇒ 三条路（钳制/提示/消息）不会互相打架。
static int wpf_wmsize_diag_on(void)
{
    static int cached = -1;
    if (cached < 0) { const char *e = getenv("WPF_LINUX_CREATE_DIAG"); cached = (e && *e && *e != '0') ? 1 : 0; }
    return cached;
}
static void wpf_wmsize_diag(const char *fmt, ...)
{
    if (!wpf_wmsize_diag_on()) return;
    static int n = 0;
    if (n++ >= 40) return;
    va_list ap;
    va_start(ap, fmt);
    fputs("[WMSIZE_DIAG] ", stderr);
    vfprintf(stderr, fmt, ap);
    fputc('\n', stderr);
    va_end(ap);
    fflush(stderr);
}

// ── 【TASK-0109 仪器】窗态站点（`wpf_core_window_state`）的**分支自报** ──────────────
//   为什么必须有：该站点与 `wpf_x11_moveresize_window()` **同因**（都问
//   `wpf_x11_has_ewmh_wm()`），但此前站点内部**没有任何打点** ⇒ 死 WM 残留时
//   "最大/还原静默失效"这件事在读数上与"命令没送到"长得一样（`W130A` §5 ★）。
//   `site=wmstate`：走 EWMH ⇒ `branch=ewmh`；走无 WM 退化支 ⇒ `branch=fallback`
//   并**打出它给 `MoveWindow` 的目标矩形**（判据侧据此判 `at_target`）。
//   env = `WPF_LINUX_WINSTATE_DIAG`（与 `win32_x11.c` 的窗态诊断同一个开关；不新增 env）。
static int wpf_wmck_diag_on(void)
{
    static int cached = -1;
    if (cached < 0) { const char *e = getenv("WPF_LINUX_WINSTATE_DIAG"); cached = (e && *e && *e != '0') ? 1 : 0; }
    return cached;
}
static void wpf_wmck_diag(HWND hwnd, const char *site, int mode, const char *branch,
                          int x, int y, int w, int h)
{
    if (!wpf_wmck_diag_on()) return;
    static int n = 0;
    if (n++ >= 60) return;
    fprintf(stderr, "[WMCK_DIAG] hwnd=0x%llx site=%s mode=%d branch=%s target=%d,%d,%dx%d\n",
            (unsigned long long)(uintptr_t)hwnd, site, mode, branch, x, y, w, h);
    fflush(stderr);
}

// 顶层判定用**显式参数**（建窗时 hwnd 还没分配 ⇒ 不能查表）
static int clamp_toplevel_extent_flags(int is_msgonly, HWND parent, int64_t style,
                                       const char *where, int *x, int *y, int *w, int *h)
{
    if (is_msgonly || parent != NULL) return 0;
    if ((style & (int64_t)(int32_t)WS_CHILD) != 0) return 0;
    if (!w && !h && !x && !y) return 0;          // 没有要钳的东西（调用方给 NULL = 不碰那一半）

    int lim_w = 0, lim_h = 0;
    wpf_x11_client_size_limit(&lim_w, &lim_h);
    if (lim_w <= 0 || lim_h <= 0) return 0;      // 无 X / 不知道 ⇒ **不钳**（绝不是"钳成 0"）

    int req_w = w ? *w : -1, req_h = h ? *h : -1;
    int req_x = x ? *x : 0, req_y = y ? *y : 0;
    int changed = 0;
    if (w && *w > lim_w) { *w = lim_w; changed = 1; }
    if (h && *h > lim_h) { *h = lim_h; changed = 1; }
    if (x && *x < 0) { *x = 0; changed = 1; }
    if (y && *y < 0) { *y = 0; changed = 1; }
    if (changed)
        wpf_wmsize_diag("%s: req=%dx%d@%d,%d → sent=%dx%d@%d,%d (limit=%dx%d)",
                        where, req_w, req_h, req_x, req_y,
                        w ? *w : -1, h ? *h : -1, x ? *x : 0, y ? *y : 0, lim_w, lim_h);
    return changed;
}

// 改尺寸路径用（从窗口表快照顶层判定；**不持 g_wpf.lock** 调 X 读数，避免锁序反转）
static int clamp_toplevel_extent(HWND hwnd, const char *where, int *x, int *y, int *w, int *h)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *win = wpf_window_find(hwnd);
    int      is_msgonly = win ? win->is_message_only : 1;
    HWND     parent     = win ? win->parent : NULL;
    int64_t  style      = win ? win->style : 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return clamp_toplevel_extent_flags(is_msgonly, parent, style, where, x, y, w, h);
}

// ══════════════════════════════════════════════════════════════════════════
//  【波 59】窗口状态机 / 命中测试 / "自绘 chrome"判定（A/B/C 三条修的公共部分）
// ══════════════════════════════════════════════════════════════════════════

// ── "应用自己画窗框"的 sticky 判定 ────────────────────────────────────────────
// 【为什么需要它（这是波 59 里唯一"必须自己找信号"的一处）】用户报的第二件事是
//   **双层窗框**：应用自绘了标题栏（`WindowChrome`），外面又被 WM 套了一层。
//   派单书给的判据是"`WS_CAPTION` 不在 style 里 ⇒ 去装饰"，但**实测该判据对 hc 示例
//   不成立**：`hc:Window` 用的是 `WindowStyle=SingleBorderWindow`（默认值，它只设置
//   `WindowChrome` 附加属性），建窗读数 `[CREATE_DIAG] style=0x2cf0000` 里
//   `WS_CAPTION(0x00C00000)` **在**（`0x2cf0000 & 0xC00000 == 0xC00000`）⇒ 判据永不触发。
// 【那真正的信号是什么】X11 上没有 DWM，`WindowChrome` 在 Windows 上"把客户区铺满整个
//   窗口（不再画系统标题栏）"这件事，靠的是 `DwmExtendFrameIntoClientArea`；而本 shim 的
//   `DwmIsCompositionEnabled` 返回 **FALSE** ⇒ `_isGlassEnabled=false` ⇒ 那条路**根本不走**
//   （grep 过：`WindowChromeWorker.cs` 的两处调用都在 `_isGlassEnabled` 分支里）。
//   但 `WindowChromeWorker` 还有一条**与合成无关**的动作：`_ApplyNewCustomChrome()` 里
//   `SetWindowPos(…, _SwpFlags)`，其中 `_SwpFlags = FRAMECHANGED|NOSIZE|NOMOVE|NOZORDER|
//   NOOWNERZORDER|NOACTIVATE`（`WindowChromeWorker.cs:28/246`）—— 语义正是"我重算了自己的
//   非客户区（窗框），请系统按新框重算"。**Win32 侧它就是自定义 chrome 的声明**，
//   X11 侧没有对应机制 ⇒ 这里把 `SWP_FRAMECHANGED` 当成"应用自绘窗框"的声明。
// 【为什么要加"首次 map 之前"这个限定】`HwndStyleManager.Flush()`（`Window.cs:6845-6867`）
//   在**样式真的变了**时也会发一条带 `FRAMECHANGED` 的 `SetWindowPos`（实测标志位 0x37）。
//   两条的区分点是**时序**：chrome 那条在 `ShowWindow(SW_SHOW)` 之前（建窗/初始化期），
//   样式管理那条在窗口已经 map 之后（运行期改 `WindowStyle/ResizeMode`）。
//   ⇒ 只认"首次 map 之前"的 `FRAMECHANGED`（sticky）；map 之后的只做"刷新装饰"，
//   不会把普通窗口的窗框剥掉。普通窗口（无 `WindowChrome`）建窗期**不发**这条 ⇒ 照旧被装饰。
void wpf_core_note_framechanged(HWND hwnd)
{
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    int became = 0;
    if (w && !w->custom_chrome) {
        w->custom_chrome = 1;
        became = 1;
    }
    int was_mapped = w ? w->mapped : 0;
    pthread_mutex_unlock(&g_wpf.lock);
    if (became && was_mapped) wpf_x11_set_decorations(hwnd, 0);   // 运行期才声明 ⇒ 当场去掉
}

int wpf_core_custom_chrome(HWND hwnd)
{
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    int v = w ? w->custom_chrome : 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return v;
}

// ── 发 `WM_NCHITTEST` 问窗口过程"这一点算什么" ─────────────────────────────────
// 【返回码从哪来】`WindowChromeWorker._HandleNCHitTest`（自绘 chrome：标题栏 → `HT.CAPTION`、
//   缩放边 → `HTTOPLEFT/HTBOTTOMRIGHT/…`、声明了 `IsHitTestVisibleInChrome` 的元素 →
//   `HTCLIENT`）与 `Window.WmNcHitTest`（`ResizeMode=CanResizeWithGrip` 的 `ResizeGrip` →
//   `HTBOTTOMRIGHT`）。两者都是 HwndSource 钩子，**只有我们把这条消息发进去**才会跑。
// 【为什么只问顶层非 message-only 窗口】子窗口/消息窗的坐标是父窗口客户区坐标系，
//   与屏幕坐标不同源；普通顶层窗口由 WM 画框，它的 `WM_NCHITTEST` 只会答"客户区"或
//   "ResizeGrip"（都是 Win32 的真实语义），所以问它无害；`message-only` 窗口根本没 map。
static int wpf_core_is_toplevel(HWND hwnd)
{
    int top = 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) top = (!w->is_message_only && w->parent == NULL &&
                  ((w->style & (int64_t)(int32_t)WS_CHILD) == 0)) ? 1 : 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return top;
}

int wpf_core_nc_hit_test(HWND hwnd, int x_root, int y_root)
{
    if (!hwnd) return HTCLIENT;
    if (!wpf_core_is_toplevel(hwnd)) return HTCLIENT;
    // lParam = 屏幕坐标（Win32：`MAKELPARAM(x, y)`，低 16 位 x、高 16 位 y，带符号语义）
    LPARAM lp = (LPARAM)(uint32_t)(((uint32_t)(x_root & 0xFFFF)) |
                                   (((uint32_t)(y_root & 0xFFFF)) << 16));
    LRESULT r = wpf_dispatch_to_window(hwnd, WM_NCHITTEST, 0, lp);
    int ht = (int)r;
    if (ht <= 0) ht = HTCLIENT;    // 没答（0/NULL）⇒ 按 Win32 的 DefWindowProc 口径 = 客户区
    return ht;
}

// ── 窗口状态：真最大化 / 最小化 / 还原 ────────────────────────────────────────
// 【为什么这件事必须由 shim 做（这是用户报的第一件事的真正根因，见报告 §1）】
//   应用侧"最大化"按钮走的是 `hc:Window` 的 `CommandBinding`：`WindowState = Maximized`
//   （`HandyControl_Shared/Controls/Window/Window.cs:297`）——不是 `WM_SYSCOMMAND`。
//   上游 `Window.OnWindowStateChanged` 对 `Maximized` 的唯一动作是
//   `UnsafeNativeMethods.ShowWindow(hr, SW_MAXIMIZE)`（`Window.cs:5231`，条件是
//   `(_Style & WS_MAXIMIZE) != WS_MAXIMIZE`）⇒ **本 shim 修前 `ShowWindow` 把 SW_MAXIMIZE
//   当成"map 一下"**（`win32_core.c` 的老代码 `case SW_SHOWMAXIMIZED: map = 1;`）
//   ⇒ 按下去什么都不会发生。实测读数见报告 §2 反极性格。
// 【为什么还要写 `w->style` 的状态位】`Window._Style` 的 getter 在 `Manager == null` 时
//   **直接读 `GetWindowLong(GWL_STYLE)`**（`Window.cs:3242-3255`）⇒ 还原路径那句
//   `if ((style & WS_MAXIMIZE) == WS_MAXIMIZE) ShowWindow(SW_RESTORE)` 才会成立；
//   不写这两位就会出现"能最大化、按还原没反应"。这同时也是 Win32 的真实行为
//   （系统会在窗口样式里置 WS_MAXIMIZE/WS_MINIMIZE）。
// 【几何由谁算】有 EWMH WM ⇒ 交给 WM（`_NET_WM_STATE`）；没有 ⇒ 自己按工作区改几何
//   并补发 `WM_SIZE`/`WM_MOVE`（Xvfb 裸跑时也要能用）。两条路都会让之后的
//   `ConfigureNotify` 按状态位发出 `WM_SIZE(SIZE_MAXIMIZED|RESTORED)`。
void wpf_core_window_state(HWND hwnd, int mode)
{
    if (!hwnd) return;
    wpf_global_init();

    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock); return; }
    int is_top = (!w->is_message_only && w->parent == NULL &&
                  ((w->style & (int64_t)(int32_t)WS_CHILD) == 0)) ? 1 : 0;
    if (mode == WPF_WS_MAX && !w->maximized) {          // 记下还原矩形（只在进入最大化时记）
        w->rc_x = w->x; w->rc_y = w->y; w->rc_w = w->width; w->rc_h = w->height;
    }
    if (mode == WPF_WS_MAX)      { w->maximized = 1; w->iconified = 0;
                                   w->style |= (int64_t)(int32_t)WS_MAXIMIZE;
                                   w->style &= ~(int64_t)(int32_t)WS_MINIMIZE; }
    else if (mode == WPF_WS_MIN) { w->iconified = 1;
                                   w->style |= (int64_t)(int32_t)WS_MINIMIZE; }
    else                         { w->maximized = 0; w->iconified = 0;
                                   w->style &= ~((int64_t)(int32_t)(WS_MAXIMIZE | WS_MINIMIZE)); }
    int rx = w->rc_x, ry = w->rc_y, rw = w->rc_w, rh = w->rc_h;
    pthread_mutex_unlock(&g_wpf.lock);

    if (!is_top) return;                                 // 子窗口/消息窗没有"最大化"语义

    if (mode == WPF_WS_MIN) { wpf_x11_iconify(hwnd); return; }

    // 【TASK-0109】这一问是**两个站点共用**的判据（谓词语义已在 `win32_x11.c` 升级为
    //   "属性在 ∧ 检查窗在树里"）⇒ 死 WM 残留时它**不再为真** ⇒ 本函数第一次能走到
    //   下面那条**早就写好**的"没有 EWMH WM"退化支（旧版在这里 `return` 去等一个
    //   **永远不会来的** `ConfigureNotify` ⇒ 最大/还原静默失效）。
    if (wpf_x11_has_ewmh_wm()) {
        wpf_x11_apply_wm_state(hwnd, mode == WPF_WS_MAX);
        wpf_wmck_diag(hwnd, "wmstate", mode, "ewmh", 0, 0, 0, 0);
        return;                                          // 几何由 WM 改 ⇒ 等 ConfigureNotify
    }

    // ── 没有 EWMH WM：自己按工作区算（Xvfb 裸跑 / 非 EWMH WM 的退化路径）────────
    if (mode == WPF_WS_MAX) {
        int wx = 0, wy = 0, ww = 0, wh = 0;
        wpf_x11_workarea(&wx, &wy, &ww, &wh);
        if (ww <= 0 || wh <= 0) {
            wpf_wmck_diag(hwnd, "wmstate", mode, "fallback-no-workarea", 0, 0, 0, 0);
            return;
        }
        wpf_wmck_diag(hwnd, "wmstate", mode, "fallback", wx, wy, ww, wh);
        MoveWindow(hwnd, wx, wy, ww, wh, 1);
    } else if (rw > 0 && rh > 0) {
        wpf_wmck_diag(hwnd, "wmstate", mode, "fallback", rx, ry, rw, rh);
        MoveWindow(hwnd, rx, ry, rw, rh, 1);
    } else {
        wpf_wmck_diag(hwnd, "wmstate", mode, "fallback-no-rect", 0, 0, 0, 0);
    }
}

// ── WM_GETMINMAXINFO 的默认值（= USER32 发这条消息前的填法）──────────────────────
// Win32 语义：USER32 先把结构体按"最大化后的尺寸/位置"填好再发给窗口过程，应用只改它
//   关心的字段。这里照做，且**必须**填 —— 托管侧 `Window.WmGetMinMaxInfo`
//   （upstream `Window.cs:4876-4888`）会把收到的 mmi **无条件**存进
//   `_trackMaxWidthDeviceUnits`/`_windowMaxWidthDeviceUnits` 等缓存，再据它算布局的
//   最小/最大尺寸 ⇒ 给它一份**全 0** 的结构体会把"窗口最大尺寸"缓存成 0。
//   （这也是"空返回"的真正后果：不是"没有约束"，而是"约束 = 0"。）
static void fill_minmaxinfo_defaults(WPF_MINMAXINFO *mmi)
{
    if (!mmi) return;
    memset(mmi, 0, sizeof(*mmi));
    int wx = 0, wy = 0, ww = 0, wh = 0;
    wpf_x11_workarea(&wx, &wy, &ww, &wh);
    // ── 【波 59 · A】`ptMaxTrackSize` 的默认值改成**屏幕尺寸**，不再是钳制上限 ────────
    //   修前（波 58）：`ptMaxTrackSize = 工作区 − 装饰余量`（= `clamp_toplevel_extent` 的同一上限），
    //   并且**无条件**拿它去顶破窗口过程给的值 ⇒ 这个"最大尺寸"被托管侧
    //   `Window.WmGetMinMaxInfo` 缓存成 `_trackMaxWidthDeviceUnits`，
    //   同时经 `WM_NORMAL_HINTS(PMaxSize)` 落到 WM 侧 ⇒ **窗口永远不可能比"装得下"更大**：
    //   实测 1280x1024 屏上 `xdotool windowsize 1280 1024` 只到 1264x984（报告 §2 有读数）。
    //   现在：默认值取 `SM_CXMAXTRACK` 的语义（屏幕尺寸），钳制**只**作用于初始尺寸；
    //   窗口过程若真声明了上限（`Window.MaxWidth` 等），那个值照样原样传给 X。
    int sw = 0, sh = 0;
    wpf_x11_screen_size(&sw, &sh);
    if (ww <= 0 || wh <= 0) { ww = 1280; wh = 1024; }   // 无 X 的兜底（与 GetSystemMetrics 同口径）
    if (sw <= 0 || sh <= 0) { sw = ww; sh = wh; }
    mmi->ptMaxSize.x     = ww;      // 最大化后的客户区尺寸 = 工作区
    mmi->ptMaxSize.y     = wh;
    mmi->ptMaxPosition.x = wx;      // 最大化后放在工作区原点
    mmi->ptMaxPosition.y = wy;
    mmi->ptMinTrackSize.x = 1;      // X 的最小可建窗；Win32 的 SM_CXMINTRACK 在 X 上没有对应物
    mmi->ptMinTrackSize.y = 1;
    mmi->ptMaxTrackSize.x = sw;     // "最大可拖到的尺寸" = 屏幕（Windows 同口径：SM_*MAXTRACK）
    mmi->ptMaxTrackSize.y = sh;
}

// ── 【波 50 · `D-G83` 修法 H1】"WM_GETMINMAXINFO 的一拍" = 一个函数 ───────────────
// 【为什么抽出来】修前这一拍**只在建窗里发生过一次**（见 `create_window_utf8` 的注释）：
//   那一刻 WPF 的写回块被它自己的守卫挡住（`Window.cs:4885` 的 `!IsSourceWindowNull`
//   ∧ `!IsCompositionTargetInvalid`）⇒ 窗口过程回填 == 我们填的默认值 ⇒ `app_declared` 恒假
//   ⇒ `PMaxSize` 永不发（W81A 实测 9/9 次回填 == 默认值）。修法 = **首次 map 之后**
//   （`_swh` 与 `CompositionTarget` 都已就位）**再问一次**，就地问出应用"真的声明了什么"。
//   两处调用点必须走**同一段代码**：否则"建窗那一拍"与"map 后那一拍"的语义会各自漂移，
//   而本仓的判据正是建在这两拍**可对照**上（`[WMSIZE_DIAG]` 行随 `where` 区分）。
// 【返回值（波 51 改成出参）】这一拍**算出**的那组值（`*out_max_w/*out_max_h == 0` = "不发 `PMaxSize`"）。
//   ⚠️ **本函数不再自己写 X**：写 X 的**唯一**入口是 `wpf_hints_publish()`（幂等发布）。
//   为什么必须拆开：旧码在每次"补问"之后**无条件**调 `wpf_x11_apply_wm_hints`，
//   而"补问"的停止条件是"终态 ＋ 次数上限"（计的是**问**、不是**改**）⇒ W93A 实测两类吞法。
//   拆开之后"要不要落 X"由**值的比较**决定，与"问了几次"彻底解耦（`D-G88` 的修法本体）。
static void wpf_ask_minmaxinfo_apply_hints(HWND hwnd, const char *where,
                                           int *out_min_w, int *out_min_h,
                                           int *out_max_w, int *out_max_h)
{
    WPF_MINMAXINFO mmi;
    fill_minmaxinfo_defaults(&mmi);
    int d_min_w = mmi.ptMinTrackSize.x, d_min_h = mmi.ptMinTrackSize.y;
    int d_max_w = mmi.ptMaxTrackSize.x, d_max_h = mmi.ptMaxTrackSize.y;
    wpf_dispatch_to_window(hwnd, WM_GETMINMAXINFO, 0, (LPARAM)&mmi);
    int min_w = mmi.ptMinTrackSize.x, min_h = mmi.ptMinTrackSize.y;
    int max_w = mmi.ptMaxTrackSize.x, max_h = mmi.ptMaxTrackSize.y;
    (void)where;
    // ── 只把"应用**真的改过**的上限"传给 X ───────────────────────────────────────
    //   怎么判"真的改过"：拿窗口过程回填后的值与**我们填进去的默认值**比。
    //   相等 ⇒ 应用没声明约束（WPF 的 `Window.WmGetMinMaxInfo` 在没有
    //   `MaxWidth/MaxHeight` 时把 `maxSizeLogicalUnits`（= 我们给的默认值）原样写回）
    //   ⇒ 这时**不发 `PMaxSize`**，而不是像波 58 那样拿"装得下"的钳制值顶上去
    //   （那正是"最大也放不大"的根因，也是本件判据的 `reg58` 那一格）。
    int app_declared = (max_w != d_max_w) || (max_h != d_max_h);
    if (min_w < 1) min_w = 1;
    if (min_h < 1) min_h = 1;
    if (app_declared) {
        if (max_w < min_w) max_w = min_w;
        if (max_h < min_h) max_h = min_h;
    } else {
        max_w = 0; max_h = 0;      // 0 = "不发 PMaxSize"
    }
    if (out_min_w) *out_min_w = min_w;
    if (out_min_h) *out_min_h = min_h;
    if (out_max_w) *out_max_w = max_w;
    if (out_max_h) *out_max_h = max_h;
    if (where) {   // 诊断行仍由**落 X 前**这一拍打（`where` 决定前缀；见 `wpf_hints_publish`）
        wpf_wmsize_diag("%s: 默认 min=%dx%d max(=屏幕)=%dx%d → 窗口过程回填 min=%dx%d max=%dx%d "
                        "⇒ 应用%s声明上限",
                        where, d_min_w, d_min_h, d_max_w, d_max_h,
                        mmi.ptMinTrackSize.x, mmi.ptMinTrackSize.y,
                        mmi.ptMaxTrackSize.x, mmi.ptMaxTrackSize.y,
                        app_declared ? "**有**" : "**没有**");
    }
}

// ── 【波 51 · `D-G88` 落地 `P1`＋`P4`】幂等发布：**值真的变了才 `XSetWMNormalHints`** ──────
// 【为什么删掉波 50 的那两个停止条件】W93A 实测（`src/Linux/build/MilBridge/W93A-report.md` §2.2/§2.5）：
//   · `hints_map_declared`（"问出过一次声明"即**终态**）：`W1` 首次问出后 ⇒ 运行期再改**永不重发**；
//   · `hints_map_asks < WPF_HINTS_REASK_MAX(3)`：计的是"**问**"不是"**改**" —— `W3` 被 4 次
//     无意义的 `HIDE/SHOW` 花光预算 ⇒ **第一次真声明也永久发不出去**。
//   两者都是"用**次数**近似**值变化**"；现在直接比较**上次已发布的那组值**，不需要近似。
// 【新的不变量（波 51 的先写判据 `I1`）】`XSetWMNormalHints` 的调用次数 == "值真的变了的次数"。
// 【`P4` 重入闸】下面这一拍会**同步回调托管代码**（`WM_GETMINMAXINFO` ⇒ `Window.WmGetMinMaxInfo`），
//   而托管回调里可能再次走到 `SetWindowPos` ⇒ 没有闸就是**递归**（本仓 `D-G54` 同族教训：
//   跨层标记必须在最内层兑现）。
// 【调用点】① 建窗那一拍（Win32 顺序，`create_window_utf8`）②首次 map 之后（`ShowWindow`）
//   ③**运行期**改尺寸真的变了时（`MoveWindow`/`SetWindowPos`，`P2`）
//   ④**托管侧的声明告示**（`WPF_LINUX_WM_HINTS_CHANGED`，波 51 `/TASK-0108` 的 `P3`；调用点见
//      `win32_msg.c` 的 `wpf_dispatch_to_window`）—— 这一拍补的正是"改**大**/不伴随 resize"
//      那一半（①②③ 都到不了它）。⇒ 因此 `wpf_hints_publish` **不再** `static`（`win32_internal.h` 有原型）。
void wpf_hints_publish(HWND hwnd, const char *where)
{
    char clsbuf[256];
    clsbuf[0] = 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    int skip = 1;
    if (w && !w->is_message_only && w->parent == NULL && (w->style & WS_CHILD) == 0
        && !w->hints_in_refresh) {
        w->hints_in_refresh = 1;    // ★ P4：闸在整个"问 + 比 + 发"期间保持
        skip = 0;
        // `WM_CLASS` 那一项由类名给（旧码在建窗那一拍直接把调用方的 `cls` 传下去；按 atom
        // 查回来等价，且对"调用方传的是 ATOM 而不是字符串"那种形态更稳）。
        wpf_class *k = wpf_class_find_atom(w->class_atom);
        if (k && k->name) snprintf(clsbuf, sizeof(clsbuf), "%s", k->name);
    }
    pthread_mutex_unlock(&g_wpf.lock);
    if (skip) return;

    int min_w = 1, min_h = 1, max_w = 0, max_h = 0;
    wpf_ask_minmaxinfo_apply_hints(hwnd, where, &min_w, &min_h, &max_w, &max_h);

    wpf_lock();
    wpf_window *w2 = wpf_window_find(hwnd);
    int changed = 0;
    if (w2) {
        changed = !w2->hints_pub_valid
               || w2->hints_pub_min_w != min_w || w2->hints_pub_min_h != min_h
               || w2->hints_pub_max_w != max_w || w2->hints_pub_max_h != max_h;
        if (changed) {
            w2->hints_pub_valid = 1;
            w2->hints_pub_min_w = min_w; w2->hints_pub_min_h = min_h;
            w2->hints_pub_max_w = max_w; w2->hints_pub_max_h = max_h;
        }
        w2->hints_in_refresh = 0;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    if (changed)   // ★ 只有值真的变了才落 X ⇒ 幂等、无消息风暴
        wpf_x11_apply_wm_hints(hwnd, clsbuf[0] ? clsbuf : NULL, min_w, min_h, max_w, max_h);
    wpf_wmsize_diag("%s: X 提示 min=%dx%d PMaxSize=%s ｜ %s",
                    where, min_w, min_h,
                    (max_w > 0 && max_h > 0) ? "已发" : "**未发**（不再用钳制值顶替）",
                    changed ? "**值变了 ⇒ 落 X**" : "与上次已发布的一致 ⇒ **不发 X**（幂等）");
}

// ── 【波 50 · `D-G83` 修法 H1】首次 map 之后的补问 —— 波 51 起由 `wpf_hints_publish` 承担 ──
// 【为什么必须存在】`fill_minmaxinfo_defaults` 的注释与 W81A 报告 §1.4 的静态链条：
//   建窗那一拍问早了（`_swh` 还没赋值）⇒ 永远问不出"应用声明的值"；**而修前 shim 全仓
//   只问那一次**（`grep -n 'WM_GETMINMAXINFO' src/**` 的派发点只有建窗那一处）⇒
//   这条通道对"应用声明的值"整体失效（上、下限都到不了 X，W81A 实测 `minonly` 也是 `1 by 1`）。
// 【波 51 删掉了什么】波 50 在这里放了两个停止条件（`hints_map_declared` 终态 ＋
//   `hints_map_asks < WPF_HINTS_REASK_MAX(=3)` 次数上限）。W93A 实测它们**两侧都错**：
//   `W1`/`W2` 被"终态"锁死、`W3` 被"预算"锁死 ⇒ **运行期改动永远发不出去**。
//   现在改由 `wpf_hints_publish` 的**值比较**兜底（幂等 ⇒ 不需要次数上限，也不会有风暴）。
// 【只对顶层**非** message-only 窗口】与建窗那一拍同一谓词（子窗口/消息窗没有"最大化"语义）。
//   ⚠️ 这条谓词现在写在 `wpf_hints_publish` 里（**唯一**一份），三处调用点共用。

static HWND create_window_utf8(uint32_t dwExStyle, const char *cls,
                               const char *title, uint32_t dwStyle,
                               int32_t X, int32_t Y, int32_t nWidth, int32_t nHeight,
                               HWND hWndParent, HMENU hMenu, HINSTANCE hInstance, void *lpParam)
{
    wpf_global_init();
    (void)hMenu; (void)hInstance; (void)lpParam;

    if (!wpf_x11_ensure()) {
        wpf_set_last_error(1400);
        wpf_win_diag_report("X11 不可用（wpf_x11_ensure 失败）", cls, hWndParent, 1400);
        return NULL;
    }

    wpf_lock();
    wpf_class *klass = NULL;
    if (cls) {
        klass = wpf_class_find_name(cls);
        if (!klass && (intptr_t)cls > 0 && (intptr_t)cls < 0x10000)
            klass = wpf_class_find_atom((ATOM)(intptr_t)cls);
    }
    if (!klass) {
        pthread_mutex_unlock(&g_wpf.lock);
        wpf_set_last_error(1411);   // ERROR_CLASS_DOES_NOT_EXIST
        return NULL;
    }

    wpf_window *w = wpf_window_add(0);      // hwnd 待 X 分配后回填
    w->class_atom = klass->atom;
    w->class_wndproc = klass->wndproc;
    w->wndproc = klass->wndproc;
    w->style = (int64_t)(int32_t)dwStyle;
    w->exstyle = (int64_t)(int32_t)dwExStyle;
    // ── 【波 59 · C】"没有 caption"的窗口 ⇒ 自绘 chrome（派单书给的判据，保留）──────
    //   `WindowStyle=None`（WPF 的 `CorrectStyleForBorderlessWindowCase` 会把 WS_CAPTION 去掉）
    //   明确表示"没有系统标题栏" ⇒ WM 不该再套一层装饰。注意这条**单独对 hc 示例不成立**
    //   （它的 WindowStyle 是 SingleBorderWindow，WS_CAPTION 在），另一条判定见
    //   `wpf_core_note_framechanged`（`SWP_FRAMECHANGED`）。
    w->custom_chrome = ((dwStyle & WS_CAPTION) == 0) ? 1 : 0;
    w->parent = hWndParent;
    w->is_message_only = wpf_is_message_only_parent(hWndParent);
    // ── 【波 58】先钳到"装得下"，再建 X 窗口 ─────────────────────────────────
    //   必须在 `wpf_x11_create_window` **之前**：X 窗口一建出来就是最终客户区尺寸，
    //   而 WM 的自动最大化判决在 map 那一刻按"尺寸 + 装饰"做。钳完**同一组值**落回
    //   窗口表（GetClientRect/GetWindowRect 与 X 一致）。
    int32_t wx = normalize_position(X), wy = normalize_position(Y);
    int32_t ww = normalize_extent(nWidth, WPF_DEFAULT_WINDOW_W);
    int32_t wh = normalize_extent(nHeight, WPF_DEFAULT_WINDOW_H);
    clamp_toplevel_extent_flags(w->is_message_only, hWndParent, w->style,
                                "CreateWindowEx", &wx, &wy, &ww, &wh);
    w->x = wx;
    w->y = wy;
    w->width = ww;
    w->height = wh;
    w->owner_thread = (void *)wpf_thread_self();
    pthread_mutex_unlock(&g_wpf.lock);

    uint64_t xid = wpf_x11_create_window(w, title ? title : "");
    if (xid == 0) {
        wpf_lock(); wpf_window_remove(w->hwnd); pthread_mutex_unlock(&g_wpf.lock);
        wpf_set_last_error(1400);
        wpf_win_diag_report("XCreateWindow 返回 0", cls, hWndParent, 1400);
        return NULL;
    }

    wpf_lock();
    w->hwnd = (HWND)(uintptr_t)xid;
    pthread_mutex_unlock(&g_wpf.lock);

    // ── 建窗消息序列：WM_NCCREATE → WM_CREATE。**message-only 窗口也必须发** ──
    // 【首版为什么错，以及错成了什么】
    //   首版判断"message-only 窗口不需要 hooks"就跳过了这两条消息。结果：
    //   HwndWrapper 构造函数里那个 `initialWndProc` 委托
    //     NativeMethods.WndProc initialWndProc = new(hwndSubclass.SubclassWndProc);
    //     wc_d.lpfnWndProc = initialWndProc;
    //     …
    //     GC.KeepAlive(initialWndProc);      // ← 构造函数返回后就只剩"类记录里的函数指针"
    //   只在构造函数存活期内被 KeepAlive。而上游 HwndSubclass 的自我挂载**依赖第一条
    //   消息**（SubclassWndProc 里 `if (_bond == Bond.Unattached) HookWindowProc(...)`）。
    //   跳过消息 ⇒ 永远不挂载 ⇒ 窗口的 GWL_WNDPROC 一直是那个已经不可达的 thunk。
    //   之后任何一次 DispatchMessageW 都会调用一个**已被 GC 回收的委托**：
    //     Process terminated.
    //     A callback was made on a garbage collected delegate of type
    //     'WindowsBase!MS.Win32.NativeMethods+WndProc::Invoke'.
    //     at MS.Win32.UnsafeNativeMethods.DispatchMessage(...)
    //     at System.Windows.Threading.Dispatcher.TranslateAndDispatchMessage(...)
    //   —— 进程直接终止（不是异常，是 fail-fast），且只在"Dispatcher 的
    //   message-only 窗口第一次真正收到消息"时才复现。这是本轮最深的一个坑。
    // 【正确做法】Win32 本来就给 message-only 窗口发 WM_NCCREATE/WM_CREATE，
    //   照发即可；"不 map" 才是 message-only 与普通窗口的区别。
    wpf_dispatch_to_window(w->hwnd, WM_NCCREATE, 0, 0);
    wpf_dispatch_to_window(w->hwnd, WM_CREATE, 0, 0);

    // ── 【波 58】WM_GETMINMAXINFO + X 提示（**必须在 map 之前**）──────────────────
    // 【修前的事实】本 shim 从建窗到销毁**从不发** `WM_GETMINMAXINFO`
    //   （全仓 `grep -rn WM_GETMINMAXINFO src/` 只有三处：`win32_internal.h:56` 的定义、
    //     `win32_msg.c:469` 的调试名表、`win32_core.c` 的 `DefWindowProcW` 分支）
    //   ⇒ `DefWindowProcW` 里那个 `case WM_GETMINMAXINFO: return 0;` 是**不可达的死码**：
    //   把它填对**单独不会改变任何行为**（这一点与派单书里"尺寸约束从未生效"的表述
    //   不完全一致，如实写在报告 §4）。真正缺的是"**问一声**"这一步。
    // ⚠️ 【波 50 · `D-G83` 更正（推翻上一段最后两句）】"那个 case 是**不可达的死码**"
    //   **被实测证伪**：W82A 用 `[WMSIZE_DIAG] DefWindowProcW(WM_GETMINMAXINFO): 进来时 …`
    //   读到它**每次派发都被走到**，而且是在**窗口过程写好之后** ⇒ 波 58 把默认值填进
    //   `DefWindowProcW` 反而成了"事后覆盖"，把应用声明的值（`521x417/667x500`）抹成默认值。
    //   该 case 已改回 no-op（详见它的注释），默认值由**发消息方**（本函数/`wpf_x11_apply_*`）
    //   在派发**之前**填 —— 这才是 Win32 的顺序。
    // 【Win32 顺序】USER32 在建窗过程中发 WM_GETMINMAXINFO（上游 `Window.cs:4244-4252`
    //   的注释明确写了"我们可能在 CreateWindowEx 期间同步收到它"，且它的处理函数
    //   允许此刻 `_swh == null`）⇒ 这里按同一顺序补上：先填默认值，再让窗口过程回填
    //   （WPF 的 `Window` 会按 Min/MaxWidth 改写），最后把**结果**落成 X 的
    //   `WM_NORMAL_HINTS`。X 侧没有"尺寸约束"的其他来源，这是唯一的。
    if (!w->is_message_only && hWndParent == NULL && (dwStyle & WS_CHILD) == 0) {
        // ── 【波 50 · `D-G83`】这一拍**原样保留**（Win32 顺序：USER32 在建窗过程中发它）──
        //   它今天仍然**问不出**应用声明的值（`_swh` 此刻还没赋值 ⇒ 写回被 `Window.cs:4885`
        //   的守卫挡住 ⇒ `app_declared` 恒假 ⇒ 不发 `PMaxSize`），**但它不是多余的**：
        //     · 它把 `ptMaxSize/ptMaxTrackSize` 的默认值交到托管侧
        //       （`Window.WmGetMinMaxInfo` 会**无条件**把它存进 `_trackMaxWidthDeviceUnits`
        //       等缓存 —— 见 `fill_minmaxinfo_defaults` 的注释），给全 0 会把"最大尺寸"
        //       缓存成 0；
        //     · 它落的 `WM_NORMAL_HINTS` 是**唯一**在建窗/map 之前设的那一次。
        //   真正缺的那一步在 `ShowWindow` 里（首次 map 之后补问），见 `wpf_hints_publish`。
        //   ⚠️ 【波 51】这里仍走**同一段代码**（`wpf_hints_publish`）⇒ "建窗那一拍"与
        //   "map 后那一拍"的语义不会各自漂移（本仓判据正建在这两拍可对照上）。
        //   首拍时 `hints_pub_valid == 0` ⇒ 一定会落一次 X（Win32 顺序不变）。
        wpf_hints_publish(w->hwnd, "WM_GETMINMAXINFO");
    }

    if (!w->is_message_only && (dwStyle & WS_VISIBLE) != 0)
        wpf_x11_map(w->hwnd, 1);

    return w->hwnd;
}

HWND CreateWindowExA(uint32_t e, const char *c, const char *n, uint32_t s,
                     int32_t x, int32_t y, int32_t w, int32_t h, HWND p, HMENU m,
                     HINSTANCE i, void *lp)
{ return create_window_utf8(e, c, n, s, x, y, w, h, p, m, i, lp); }

// 裸名 == A：CharSet.Auto 在 Unix 上是 Ansi，运行时会先探到裸名。
HWND CreateWindowEx(uint32_t e, const char *c, const char *n, uint32_t s,
                    int32_t x, int32_t y, int32_t w, int32_t h, HWND p, HMENU m,
                    HINSTANCE i, void *lp)
{ return create_window_utf8(e, c, n, s, x, y, w, h, p, m, i, lp); }

HWND CreateWindowExW(uint32_t e, const uint16_t *c, const uint16_t *n, uint32_t s,
                     int32_t x, int32_t y, int32_t w, int32_t h, HWND p, HMENU m,
                     HINSTANCE i, void *lp)
{
    char *cls = wpf_utf16_to_utf8_dup(c);
    char *title = wpf_utf16_to_utf8_dup(n);
    HWND r = create_window_utf8(e, cls, title, s, x, y, w, h, p, m, i, lp);
    free(cls); free(title);
    return r;
}

// ── 销毁 ───────────────────────────────────────────────────────────────────
BOOL DestroyWindow(HWND hwnd)
{
    wpf_global_init();
    if (!hwnd) { wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }

    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    if (w->in_destroy) { pthread_mutex_unlock(&g_wpf.lock); return 1; }
    w->in_destroy = 1;
    pthread_mutex_unlock(&g_wpf.lock);

    // Win32 语义：DestroyWindow 同步发出 WM_DESTROY，随后 WM_NCDESTROY 并销毁 HWND。
    // 托管侧靠 WM_NCDESTROY 触发 HwndWrapper.Dispose(isHwndBeingDestroyed: true)，
    // 少了这条消息，窗口对象就永远不释放（且 ManagedWndProcTracker 也不会解绑）。
    // message-only 窗口**同样**要发 —— 理由与建窗那两条完全对称（见上面 CreateWindowExW
    // 的注释）：HwndSubclass 靠 WM_NCDESTROY 走 CriticalDetach(true) 摘掉自己的
    // WndProc，不发就等于把一个指向托管委托的指针永久留在窗口上。
    wpf_dispatch_to_window(hwnd, WM_DESTROY, 0, 0);
    wpf_dispatch_to_window(hwnd, WM_NCDESTROY, 0, 0);

    // 清掉挂在这个窗口上的定时器（Win32 会自动杀）。
    wpf_lock();
    wpf_timer **tp = &g_wpf.timers;
    while (*tp) {
        if ((*tp)->hwnd == hwnd) {
            wpf_timer *v = *tp; *tp = v->next; free(v);
        } else tp = &(*tp)->next;
    }
    pthread_mutex_unlock(&g_wpf.lock);

    wpf_x11_destroy_window(hwnd);

    wpf_lock();
    wpf_window_remove(hwnd);
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

BOOL IsWindow(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    int ok = wpf_window_find(hwnd) != NULL;
    pthread_mutex_unlock(&g_wpf.lock);
    return ok;
}

BOOL IsWindowVisible(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    int v = w && w->mapped;
    pthread_mutex_unlock(&g_wpf.lock);
    return v;
}

BOOL IsWindowEnabled(HWND hwnd) { (void)hwnd; return 1; }   // 无「禁用」语义，见 README
// PresentationNative_cor3.dll 的 EnableWindowWrapper（先 SetLastError(0) 再转发）。
// Linux 上没有「窗口被禁用」这个状态：X11 的输入掩码是应用自己选的，
// 不存在 server 侧的 enable/disable。返回 TRUE = 「当前是启用态」，这是真话。
BOOL EnableWindow(HWND hwnd, BOOL enable) { (void)hwnd; (void)enable; return 1; }
BOOL EnableWindowWrapper(HWND hwnd, BOOL enable) { wpf_set_last_error(0); return EnableWindow(hwnd, enable); }
// 托管侧用 IsWindowUnicode 决定该用 ...W 还是 ...A 变体。我们全部按 Unicode 处理。
BOOL IsWindowUnicode(HWND hwnd) { (void)hwnd; return 1; }

BOOL IsChild(HWND parent, HWND child)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *c = wpf_window_find(child);
    int ok = c && c->parent == parent;
    pthread_mutex_unlock(&g_wpf.lock);
    return ok;
}

// ── 显隐 / 位置 / 尺寸 ─────────────────────────────────────────────────────
// 【波 46 仪器】显示腿事件日志（`SetWindowPos`/`ShowWindow`），env `WPF_LINUX_CREATE_DIAG=1`，≤40 行，只打印。
static void wpf_show_diag(const char *where, HWND hwnd, long a, long b)
{
    static int cached = -1, n = 0;
    if (cached < 0) { const char *e = getenv("WPF_LINUX_CREATE_DIAG"); cached = (e && *e && *e != '0') ? 1 : 0; }
    if (!cached || n++ >= 40) return;
    fprintf(stderr, "[SHOW_DIAG] %s hwnd=0x%llx a=%ld b=%ld\n", where, (unsigned long long)(uintptr_t)hwnd, a, b);
    fflush(stderr);
}

BOOL ShowWindow(HWND hwnd, int nCmdShow)
{
    wpf_global_init();
    wpf_show_diag("ShowWindow", hwnd, (long)nCmdShow, 0);
    int map;
    // ── 【波 59】SW_MAXIMIZE / SW_MINIMIZE / SW_RESTORE 必须**真的**改状态 ─────────
    //   修前三个都退化成"map 一下"（注释写着"无 WM/图标化，退化为 map"）——那句话在
    //   "窗口管理器会帮我们做"的假设下才成立，而 X11 上**没有任何东西**会把
    //   `ShowWindow(SW_MAXIMIZE)` 变成最大化：它不是 ICCCM/EWMH 消息，只是个 Win32 API。
    //   上游 `Window.OnWindowStateChanged` 却**只**走这条路（见 `wpf_core_window_state` 注释）。
    int want_state = -1;
    switch (nCmdShow) {
        case SW_HIDE: map = 0; break;
        case SW_SHOWMINIMIZED: want_state = WPF_WS_MIN; map = 1; break;
        case SW_SHOWMAXIMIZED: want_state = WPF_WS_MAX; map = 1; break;
        case SW_MINIMIZE:      want_state = WPF_WS_MIN; map = 1; break;
        case SW_RESTORE:       want_state = WPF_WS_NORMAL; map = 1; break;
        default: map = 1; break;
    }
    wpf_x11_map(hwnd, map);
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    int was = w->mapped;
    w->mapped = map;
    pthread_mutex_unlock(&g_wpf.lock);
    if (map) wpf_dispatch_to_window(hwnd, WM_SHOWWINDOW, 1, 0);
    // ── 【波 50 · `D-G83` 修法 H1】首次 map 之后补问 `WM_GETMINMAXINFO` ──────────────
    // 【为什么落在这里】`ShowWindow(map=1)` 是"窗口**真的**要出现"的那一拍，而它**必然晚于**
    //   `Window.CreateSourceWindow` 里的 `_swh = new SourceWindowHelper(source)`
    //   （`Window.cs:2521`；`Show()` → `SafeCreateWindowDuringShow` → … → `ShowWindow`，`:5548`）
    //   ⇒ 此刻 WPF 的写回块不再被 `_swh == null` 挡住，声明值才问得出来。
    // 【为什么不在 `wpf_x11_map` 里做】那一层在**建窗路径**里也会被调（`create_window_utf8`
    //   的 `WS_VISIBLE` 分支）—— 那一拍正是不该问的那一拍；要区分就得引入"正在建窗"的跨层标记，
    //   而在 **`WM_CREATE` 里再建窗**时那种标记很容易被嵌套冲掉（本仓 `D-G54` 同族教训）。
    //   落在 `ShowWindow` 则天然按"显隐语义"区分，且 `SetWindowPos(SWP_SHOWWINDOW)` 也会
    //   走到这里（它自己就 `ShowWindow(hwnd, SW_SHOW)`，见 `:1075`）。
    // 【波 51】这里**每次都问**，但只有"值真的变了"才落 X（幂等）⇒ 既不需要次数上限，
    //   也不会消息风暴。旧的"问出即终态 / 最多问 3 次"两个条件见 `wpf_hints_publish` 的注释。
    if (map) wpf_hints_publish(hwnd, "WM_GETMINMAXINFO(after-map)");
    if (want_state >= 0) wpf_core_window_state(hwnd, want_state);
    return was;   // Win32: 返回「此前是否可见」
}
BOOL ShowWindowAsync(HWND hwnd, int nCmdShow) { return ShowWindow(hwnd, nCmdShow); }

BOOL MoveWindow(HWND hwnd, int x, int y, int w, int h, BOOL repaint)
{
    (void)repaint;
    wpf_global_init();
    wpf_lock();
    wpf_window *win = wpf_window_find(hwnd);
    if (!win) { pthread_mutex_unlock(&g_wpf.lock);
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    x = normalize_position(x); y = normalize_position(y);
    w = normalize_extent(w, win->width > 0 ? win->width : WPF_DEFAULT_WINDOW_W);
    h = normalize_extent(h, win->height > 0 ? win->height : WPF_DEFAULT_WINDOW_H);
    pthread_mutex_unlock(&g_wpf.lock);

    // 【波 58】显式改尺寸 ⇒ 钳制（clamp_toplevel_extent 自己取锁，故必须在解锁之后调）
    clamp_toplevel_extent(hwnd, "MoveWindow", &x, &y, &w, &h);

    wpf_lock();
    win = wpf_window_find(hwnd);
    if (!win) { pthread_mutex_unlock(&g_wpf.lock);
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    int old_w = win->width, old_h = win->height;      // ★ 波 51：改前尺寸（判"真的变了"）
    win->x = x; win->y = y; win->width = w; win->height = h;
    pthread_mutex_unlock(&g_wpf.lock);
    wpf_x11_move_resize(hwnd, x, y, w, h);
    // ── 【波 51 · `P2`】改尺寸路径上的**触发器**（`D-G88` 的"运行期"那一半）──────────
    //   上游 `Window.cs:5879-5884` 在 `MaxWidth` 变**小**时**一定**走 `SetWindowPos`
    //   （`UpdateHwndSizeOnWidthHeightChange`）⇒ 这一拍必到（W93A 的 `W2-DECLARE` 几何
    //   `625x521→521x417` 就是它）。放在 `wpf_x11_move_resize` **之后**：应用侧的答案可能
    //   依赖"当前尺寸"，先落尺寸再问才问得准。
    if (w != old_w || h != old_h) wpf_hints_publish(hwnd, "after-MoveWindow");
    return 1;
}

BOOL SetWindowPos(HWND hwnd, HWND after, int x, int y, int cx, int cy, UINT flags)
{
    (void)after;
    wpf_global_init();
    const UINT SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004;
    const UINT SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    const UINT SWP_FRAMECHANGED = 0x0020;
    wpf_show_diag("SetWindowPos", hwnd, (long)flags, ((long)cx << 16) | ((long)cy & 0xffffL));
    (void)SWP_NOZORDER; (void)SWP_NOACTIVATE;

    // ── 【波 59 · C】`SWP_FRAMECHANGED` = "应用重算了自己的窗框" ───────────────────
    //   语义与为什么把它当"自绘 chrome"的声明：见 `wpf_core_note_framechanged` 的注释。
    //   时序上限定"首次 map 之前"由调用方（该函数）内部判断。
    if ((flags & SWP_FRAMECHANGED) && !wpf_core_custom_chrome(hwnd)) {
        wpf_lock();
        wpf_window *wf = wpf_window_find(hwnd);
        int mapped = wf ? wf->mapped : 1;      // 查不到就当"已 map"（保守：不据此改装饰）
        pthread_mutex_unlock(&g_wpf.lock);
        if (!mapped) wpf_core_note_framechanged(hwnd);
        else {
            // 运行期才来的 FRAMECHANGED：可能是 chrome 重建模板，也可能是普通窗口改样式。
            // 只要**已经**判定为自绘 chrome，就顺手刷新一次装饰（幂等）；否则不动。
            if (wpf_core_custom_chrome(hwnd)) wpf_x11_set_decorations(hwnd, 0);
        }
    }

    wpf_lock();
    wpf_window *win = wpf_window_find(hwnd);
    if (!win) { pthread_mutex_unlock(&g_wpf.lock);
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    int nx = (flags & SWP_NOMOVE) ? win->x : normalize_position(x);
    int ny = (flags & SWP_NOMOVE) ? win->y : normalize_position(y);
    int nw = (flags & SWP_NOSIZE) ? win->width
                                  : normalize_extent(cx, win->width > 0 ? win->width : WPF_DEFAULT_WINDOW_W);
    int nh = (flags & SWP_NOSIZE) ? win->height
                                  : normalize_extent(cy, win->height > 0 ? win->height : WPF_DEFAULT_WINDOW_H);
    pthread_mutex_unlock(&g_wpf.lock);

    // 【波 58】钳制**只作用于"调用方真的在改尺寸/位置"的那一半**：
    //   `SWP_NOSIZE` 表示"尺寸不变" ⇒ 不钳 —— 那条路上尺寸可能正是 **WM 自己**给的值
    //   （最大化态下 ConfigureNotify 已经把客户区改小过），去"纠正"它会与 WM 打架
    //   （我们缩、WM 再最大化 ⇒ 震荡）。同理 `SWP_NOMOVE` 时不动原点。
    clamp_toplevel_extent(hwnd, "SetWindowPos",
                          (flags & SWP_NOMOVE) ? NULL : &nx,
                          (flags & SWP_NOMOVE) ? NULL : &ny,
                          (flags & SWP_NOSIZE) ? NULL : &nw,
                          (flags & SWP_NOSIZE) ? NULL : &nh);

    wpf_lock();
    win = wpf_window_find(hwnd);
    if (!win) { pthread_mutex_unlock(&g_wpf.lock);
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    int old_w = win->width, old_h = win->height;      // ★ 波 51：改前尺寸（判"真的变了"）
    win->x = nx; win->y = ny; win->width = nw; win->height = nh;
    pthread_mutex_unlock(&g_wpf.lock);

    // ── 【波 52 · W114A · `D-G98`】`SWP_NOSIZE|SWP_NOMOVE` ⇒ 这次调用**不改几何** ────────
    //   【Win32 语义】两个位**同时**置位时，`SetWindowPos` 对位置与尺寸**什么都不做**；
    //   上游正是这么用的：`WindowChromeWorker._ApplyNewCustomChrome()` 的
    //   `_SwpFlags = FRAMECHANGED|NOSIZE|NOMOVE|NOZORDER|NOOWNERZORDER|NOACTIVATE`
    //   （`upstream/wpf/…/WindowChromeWorker.cs:28/246`），以及 `HwndStyleManager.Flush()`
    //   （`Window.cs:6845-6867`，实测标志位 `0x37`）—— 它们要的是"**请按新窗框重算非客户区**"，
    //   **几何意图为零**。（两条的标志族在本仓 `:544-548` 已登记。）
    //   【修前为什么错】这里**无条件**落一次 `wpf_x11_move_resize`，而 `nx/ny/nw/nh` 在上面
    //   全部取自**窗口表缓存**（`SWP_NOMOVE`/`SWP_NOSIZE` 分支）⇒ 一次"本应无副作用"的调用
    //   被落成一次**真实的 X 几何写**，写的是**缓存里的矩形**。
    //   【它怎么变成 `D-G98`】应用点"还原" ⇒ `ShowWindow(SW_RESTORE)` ⇒ `_NET_WM_STATE` REMOVE；
    //   同一拍的下一次 `SetWindowPos(0x237)` 若在 X 泵把 WM 的还原 `ConfigureNotify` 采纳进缓存
    //   **之前**到达（缓存值的竞态），写出的就是**陈旧的最大化矩形**，而这条客户端请求排在
    //   `REMOVE` **之后** ⇒ WM 先完成 unmaximize（几何回基准），**再**顺从它把窗口改回最大化
    //   ⇒ 终态 = `_NET_WM_STATE_FOCUSED`（窗态已还原）＋ client 与 frame **双双卡在**
    //   `1280x1024@+0+0`。**这就是 `D-G98`。**
    //   ⇒ 两个位同时置位时**不落这次 X 写**。（窗框/装饰的副作用由本函数上半段的
    //   `SWP_FRAMECHANGED` 分支承担，**不受影响**；`win->x/y/width/height` 的自赋值在
    //   这两个位下是**恒等赋值**，一并保留。）
    if (!((flags & SWP_NOSIZE) && (flags & SWP_NOMOVE)))
        wpf_x11_move_resize(hwnd, nx, ny, nw, nh);
    // ── 【波 51 · `P2`】同上：**只有调用方真的改了尺寸**才补一拍（`SWP_NOSIZE` 那条路上
    //   尺寸可能正是 WM 自己给的值，那条路不该触发重问）。`SWP_SHOWWINDOW` 会自己走
    //   `ShowWindow` ⇒ 那一拍由 after-map 那条路覆盖（重入闸保证不会叠加成风暴）。
    if (!(flags & SWP_NOSIZE) && (nw != old_w || nh != old_h))
        wpf_hints_publish(hwnd, "after-SetWindowPos");
    if (flags & SWP_SHOWWINDOW) ShowWindow(hwnd, SW_SHOW);
    if (flags & SWP_HIDEWINDOW) ShowWindow(hwnd, SW_HIDE);

    // Win32 在 SetWindowPos 之后会发 WM_SIZE / WM_MOVE（除非 NOSIZE/NOMOVE）。
    if (!(flags & SWP_NOSIZE)) wpf_dispatch_to_window(hwnd, WM_SIZE, 0,
                                   (LPARAM)(((uint32_t)nw & 0xFFFF) | ((uint32_t)nh << 16)));
    if (!(flags & SWP_NOMOVE)) wpf_dispatch_to_window(hwnd, WM_MOVE, 0,
                                   (LPARAM)(((uint32_t)(int16_t)nx) | ((uint32_t)(int16_t)ny << 16)));
    return 1;
}

BOOL GetClientRect(HWND hwnd, WPF_RECT *rc)
{
    wpf_global_init();
    if (!rc) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    rc->left = 0; rc->top = 0; rc->right = w->width; rc->bottom = w->height;
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

BOOL GetWindowRect(HWND hwnd, WPF_RECT *rc)
{
    wpf_global_init();
    if (!rc) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    rc->left = w->x; rc->top = w->y;
    rc->right = w->x + w->width + 2 * w->border;
    rc->bottom = w->y + w->height + 2 * w->border;
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

// 非客户区在 X11 上由窗口管理器（Xvfb 下没有 WM）负责，我们只有客户区，
// 所以 ScreenToClient/ClientToScreen 就是「减去/加上窗口原点」。
BOOL ScreenToClient(HWND hwnd, WPF_POINT *pt)
{
    wpf_global_init();
    if (!pt) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock); return 0; }
    pt->x -= w->x; pt->y -= w->y;
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

BOOL ClientToScreen(HWND hwnd, WPF_POINT *pt)
{
    wpf_global_init();
    if (!pt) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock); return 0; }
    pt->x += w->x; pt->y += w->y;
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

// ── 窗口长整型（GWL_*）─────────────────────────────────────────────────────
// 只支持真的被用到的槽位；其余返回 0 并设 ERROR_INVALID_INDEX，**不假装成功**。
static int gwl_slot(int nIndex, int64_t **out, wpf_window *w)
{
    static __thread int64_t scratch;
    switch (nIndex) {
        case GWL_WNDPROC:    return -1;    // 由调用方特殊处理
        case GWL_STYLE:      *out = &w->style;    return 1;
        case GWL_EXSTYLE:    *out = &w->exstyle;  return 1;
        case GWL_USERDATA:   *out = (int64_t *)&w->userdata; return 1;
        case GWL_ID:         *out = &scratch;     return 1;   // 无子窗口 ID 语义
        case GWL_HINSTANCE:
        case GWL_HWNDPARENT: *out = (int64_t *)&w->parent; return 1;
        default: return 0;
    }
}

INT_PTR GetWindowLongPtrW(HWND hwnd, int nIndex)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    if (nIndex == GWL_WNDPROC) {
        INT_PTR r = (INT_PTR)w->wndproc;
        pthread_mutex_unlock(&g_wpf.lock);
        return r;
    }
    int64_t *slot = NULL;
    if (gwl_slot(nIndex, &slot, w) == 1) {
        INT_PTR r = (INT_PTR)*slot;
        pthread_mutex_unlock(&g_wpf.lock);
        return r;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    wpf_set_last_error(87);   // ERROR_INVALID_PARAMETER
    return 0;
}
INT_PTR GetWindowLongPtrA(HWND h, int i) { return GetWindowLongPtrW(h, i); }
INT_PTR GetWindowLongPtr(HWND h, int i) { return GetWindowLongPtrW(h, i); }

// 32 位变体：截断返回，但**保留低 32 位语义**（托管侧 GetWindowLong 用它读 STYLE）。
LONG GetWindowLongW(HWND hwnd, int nIndex)
{
    return (LONG)(int32_t)GetWindowLongPtrW(hwnd, nIndex);
}
LONG GetWindowLongA(HWND h, int i) { return GetWindowLongW(h, i); }
LONG GetWindowLong(HWND h, int i) { return GetWindowLongW(h, i); }

INT_PTR SetWindowLongPtrW(HWND hwnd, int nIndex, INT_PTR dwNewLong)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    if (nIndex == GWL_WNDPROC) {
        INT_PTR old = (INT_PTR)w->wndproc;
        w->wndproc = (WNDPROC)dwNewLong;
        pthread_mutex_unlock(&g_wpf.lock);
        return old;
    }
    int64_t *slot = NULL;
    if (gwl_slot(nIndex, &slot, w) == 1 && nIndex != GWL_ID) {
        INT_PTR old = (INT_PTR)*slot;
        *slot = (int64_t)dwNewLong;
        pthread_mutex_unlock(&g_wpf.lock);
        return old;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    wpf_set_last_error(87);
    return 0;
}
INT_PTR SetWindowLongPtrA(HWND h, int i, INT_PTR v) { return SetWindowLongPtrW(h, i, v); }
INT_PTR SetWindowLongPtr(HWND h, int i, INT_PTR v) { return SetWindowLongPtrW(h, i, v); }

// PresentationNative_cor3.dll 里这四对是 **Wrapper 后缀**的导出名；M7b 把它们
// 也映射到本 shim，托管侧同一条调用链就能落地（详见 src/Microsoft.DotNet.Wpf.Linux/src/shims 的 resolver）。
// Wrapper 与裸名语义相同：唯一区别是 Windows 上 Wrapper 会先 SetLastError(0)。
INT_PTR GetWindowLongPtrWrapper(HWND h, int i) { wpf_set_last_error(0); return GetWindowLongPtrW(h, i); }
INT_PTR GetWindowLongWrapper(HWND h, int i)    { wpf_set_last_error(0); return (INT_PTR)(int32_t)GetWindowLongW(h, i); }
INT_PTR SetWindowLongPtrWrapper(HWND h, int i, INT_PTR v) { return SetWindowLongPtrW(h, i, v); }
INT_PTR SetWindowLongWrapper(HWND h, int i, INT_PTR v)    { return (INT_PTR)(int32_t)SetWindowLongPtrW(h, i, (int32_t)v); }

LONG SetWindowLongW(HWND hwnd, int nIndex, LONG dwNewLong)
{
    return (LONG)(int32_t)SetWindowLongPtrW(hwnd, nIndex, (INT_PTR)(int32_t)dwNewLong);
}
LONG SetWindowLongA(HWND h, int i, LONG v) { return SetWindowLongW(h, i, v); }
LONG SetWindowLong(HWND h, int i, LONG v) { return SetWindowLongW(h, i, v); }

// ── 父子 / 兄弟 / 焦点 / 捕获 ───────────────────────────────────────────────
HWND GetParent(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    HWND p = NULL;
    if (w && w->parent != HWND_MESSAGE && w->parent != NULL) p = w->parent;
    pthread_mutex_unlock(&g_wpf.lock);
    return p;
}

HWND SetParent(HWND hwnd, HWND parent)
{
    wpf_global_init();
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return NULL; }
    HWND old = w->parent;
    w->parent = parent;
    pthread_mutex_unlock(&g_wpf.lock);
    return old;
}

HWND GetAncestor(HWND hwnd, UINT flags)
{
    wpf_global_init();
    const UINT GA_PARENT = 1, GA_ROOT = 2, GA_ROOTOWNER = 3;
    (void)GA_ROOT; (void)GA_ROOTOWNER;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    HWND r = NULL;
    if (w) {
        if (flags == GA_PARENT) r = (w->parent == HWND_MESSAGE) ? NULL : w->parent;
        else r = w->hwnd;   // GA_ROOT/GA_ROOTOWNER：我们没有嵌套的顶层/属主链，返回自身
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

HWND GetWindow(HWND hwnd, UINT uCmd)
{
    // GW_OWNER(4)/GW_HWNDNEXT(2) 等在 X11 上没有对应物。返回 NULL 是**明确的
    // “没有这个窗口”**，调用方（HwndTarget）只在 IsWindow 之后才用，安全。
    (void)uCmd;
    wpf_global_init();
    wpf_lock();
    int known = wpf_window_find(hwnd) != NULL;
    pthread_mutex_unlock(&g_wpf.lock);
    if (!known) wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE);
    return NULL;
}

HWND GetDesktopWindow(void)
{
    wpf_global_init();
    if (!wpf_x11_ensure()) return NULL;
    return (HWND)(uintptr_t)g_wpf.root;      // X11 的 root window 就是「桌面」
}

HWND GetActiveWindow(void) { return GetDesktopWindow(); }
HWND GetForegroundWindow(void) { return GetDesktopWindow(); }
HWND SetActiveWindow(HWND hwnd) { (void)hwnd; return GetForegroundWindow(); }
HWND SetForegroundWindow(HWND hwnd) { (void)hwnd; return GetForegroundWindow(); }

// BringWindowToTop：X11 上的等价物是 `XRaiseWindow`（把窗口提到同层栈顶）。
// 【为什么必须有】AvalonDock 的 `LayoutAutoHideWindowControl.BuildWindowCore` 直接
//   `[DllImport("user32.dll")] BringWindowToTop`（`AvalonDock.Win32Helper`）；本 shim
//   原先没有这个导出 ⇒ 每次建这个 HwndHost 子窗都抛 `EntryPointNotFoundException`
//   ⇒ 自动隐藏面板的可视子树整棵建不起来。
BOOL BringWindowToTop(HWND hwnd)
{
    wpf_global_init();
    if (!hwnd) { wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    wpf_lock();
    int known = wpf_window_find(hwnd) != NULL;
    pthread_mutex_unlock(&g_wpf.lock);
    if (!known) { wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    if (!wpf_x11_ensure()) return 0;
    XRaiseWindow(g_wpf.dpy, (Window)(uintptr_t)hwnd);
    wpf_x11_flush();
    return 1;
}

// 焦点/捕获在 X11 上是「每个输入设备一个，且由 server 持有」的概念；
// 没有窗口管理器时 PointerRoot 焦点无意义。这里退化为**进程内的软状态**：
// 记住最近一次 SetFocus/SetCapture 的窗口并原样返回，语义自洽（Set→Get 一致），
// 但不影响真实键盘焦点。这是一处**明确的降级**，已登记到 README/report。
HWND g_focus_window = NULL;
HWND g_capture_window = NULL;

HWND GetFocus(void)
{
    wpf_global_init();
    wpf_lock();
    HWND h = g_focus_window;
    pthread_mutex_unlock(&g_wpf.lock);
    return h;
}

HWND SetFocus(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    HWND old = g_focus_window;
    g_focus_window = hwnd;
    pthread_mutex_unlock(&g_wpf.lock);
    // 补丁 F2：Win32 的 SetFocus 语义含"系统键盘输入焦点转到该窗口" ⇒ 同步到 X server。
    //   （只改内部状态的话，真键盘注入到不了我们；见 win32_x11.c 的 wpf_x11_set_input_focus 注释）
    if (hwnd) wpf_x11_set_input_focus(hwnd);
    if (old && old != hwnd) wpf_dispatch_to_window(old, WM_KILLFOCUS, (WPARAM)hwnd, 0);
    // ⚠️⚠️【`D-G66`（2026-09-20）】**`old != hwnd` 守卫必须有** —— Win32 的 `SetFocus` 对"**已经拥有焦点**
    //   的窗口"是**空操作**（既不发 `WM_KILLFOCUS` 也不发 `WM_SETFOCUS`），而上游**逐字依赖**这条不变式：
    //   `upstream/…/PresentationCore/System/Windows/Interop/HwndKeyboardInputProvider.cs:114-124`
    //   写着"已经拿到 Win32 焦点的 HWND **不会**再收到 `WM_SETFOCUS`"。
    //   修前本行**无条件**派发 ⇒ 我们的 `SetFocus` 又**同步**回调 WndProc（`win32_msg.c:512-528` 的
    //   `return proc(hwnd,msg,wp,lp);`）⇒ 形成**闭合托管递归环**（车道 W55A 实测 `Repeated 3265 times:`）：
    //     SetFocus ← TrySetFocus ← AcquireFocus ← TryChangeFocus ← Focus ← CheckForDisconnectedFocus
    //     ← PreNotifyInput ← ProcessStagingArea ← ReportInput ← OnSetFocus ← FilterMessage ← WndProc
    //     ← SubclassWndProc ← NativeMethodsSetLastError.SetFocus ← … （回到 SetFocus）
    //   ⇒ 现象 = 托管 `Stack overflow.`（rc=134）或裸 SIGSEGV（rc=139）；**最小复现只要 2 击**（先点一个导航项、再点一个页签）。
    //   正控（W55A）：`SETFOCUS → XSetInputFocus` 共 358 行，其中 **357 行是同一个窗口**（旧值 == 新值）。
    //   注意：X 侧那句 `wpf_x11_set_input_focus` **保留** —— 它不派发消息，且是"把 X 焦点抢回来"的唯一手段
    //   （`D-G50` 的焦点回声抑制依赖它）；本修法**只**去掉"对同一窗口重复派发 `WM_SETFOCUS`"这一步。
    if (hwnd && old != hwnd) wpf_dispatch_to_window(hwnd, WM_SETFOCUS, (WPARAM)old, 0);
    return old;
}
HWND SetFocusWrapper(HWND h) { wpf_set_last_error(0); return SetFocus(h); }
HWND GetParentWrapper(HWND h) { wpf_set_last_error(0); return GetParent(h); }
HWND GetWindowWrapper(HWND h, UINT c) { wpf_set_last_error(0); return GetWindow(h, c); }
HWND GetAncestorWrapper(HWND h, UINT f) { return GetAncestor(h, f); }

HWND GetCapture(void)
{
    wpf_global_init();
    wpf_lock();
    HWND h = g_capture_window;
    pthread_mutex_unlock(&g_wpf.lock);
    return h;
}

HWND SetCapture(HWND hwnd)
{
    wpf_global_init();
    wpf_lock();
    HWND old = g_capture_window;
    g_capture_window = hwnd;
    pthread_mutex_unlock(&g_wpf.lock);
    // 【波 47 · D-G55】Win32 语义：**失去捕获**的窗口收到 `WM_CAPTURECHANGED`，`lParam` = 新捕获窗口。
    //   为什么必须有：上游 `MouseDevice` 清内部捕获状态**只认** `RawMouseAction.CancelCapture`
    //   （`MouseDevice.cs:386-394`），而该动作的唯一来源是 `HwndMouseInputProvider` 处理
    //   `WM_CAPTURECHANGED`（`:719-735`）。本 shim 之前只改软状态、**一条消息都不派发** ⇒
    //   `Mouse.Captured` 永不复位 ⇒ **点过一次之后，后续点击全被路由到那个控件**
    //   （实测：同一命中梯子冷态 `captured=null`；点一次 TextBox 后 11 个位置全是 `captured=TextBox`）。
    if (old && old != hwnd) wpf_dispatch_to_window(old, WM_CAPTURECHANGED, 0, (LPARAM)hwnd);
    return old;
}

BOOL ReleaseCapture(void)
{
    wpf_global_init();
    wpf_lock();
    HWND old = g_capture_window;
    g_capture_window = NULL;
    pthread_mutex_unlock(&g_wpf.lock);
    // 【波 47 · D-G55】同 `SetCapture`：释放也要派发，否则上游的 `Mouse.Captured` 不复位
    //   （`lParam = 0` ⇒ 命中上游 `!IsOurWindow(lParam) && _active` 分支 ⇒ `CancelCapture`）。
    if (old) wpf_dispatch_to_window(old, WM_CAPTURECHANGED, 0, 0);
    return 1;
}

// ── 文本 / 类名 / 属性 ─────────────────────────────────────────────────────
int GetClassNameW(HWND hwnd, uint16_t *buf, int nMaxCount)
{
    wpf_global_init();
    if (!buf || nMaxCount <= 0) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    wpf_class *c = w ? wpf_class_find_atom(w->class_atom) : NULL;
    int n = 0;
    if (c && c->name) {
        uint16_t *u = wpf_utf8_to_utf16_dup(c->name);
        if (u) {
            while (u[n] && n < nMaxCount - 1) { buf[n] = u[n]; n++; }
            free(u);
        }
    }
    buf[n] = 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return n;
}

// CharSet.Auto 在 Unix 上折叠为 Ansi → StringBuilder 是 **UTF-8 字节缓冲**。
int GetClassNameA(HWND hwnd, char *buf, int nMaxCount)
{
    wpf_global_init();
    if (!buf || nMaxCount <= 0) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    wpf_class *c = w ? wpf_class_find_atom(w->class_atom) : NULL;
    int n = 0;
    if (c && c->name) {
        while (c->name[n] && n < nMaxCount - 1) { buf[n] = c->name[n]; n++; }
    }
    buf[n] = 0;
    pthread_mutex_unlock(&g_wpf.lock);
    return n;
}
int GetClassName(HWND h, char *b, int n) { return GetClassNameA(h, b, n); }

// CharSet.Auto（Unix→Ansi）→ 裸名收 UTF-8；W 变体先转码后转发。
BOOL SetWindowTextA(HWND hwnd, const char *text) { wpf_x11_set_title(hwnd, text ? text : ""); return 1; }
BOOL SetWindowText(HWND h, const char *t) { return SetWindowTextA(h, t); }
BOOL SetWindowTextW(HWND hwnd, const uint16_t *text)
{
    char *t = wpf_utf16_to_utf8_dup(text);
    BOOL r = SetWindowTextA(hwnd, t ? t : "");
    free(t);
    return r;
}

int GetWindowTextW(HWND hwnd, uint16_t *buf, int nMaxCount)
{
    // 标题的**权威副本在 X server**；本 shim 不缓存。XFetchName 是同步往返，
    // 在 Dispatcher 线程里代价可接受（WPF 只在建窗/改名时读）。
    if (!buf || nMaxCount <= 0) return 0;
    char name[512];
    name[0] = 0;
    wpf_x11_query_title(hwnd, name, sizeof(name));
    uint16_t *u = wpf_utf8_to_utf16_dup(name);
    int n = 0;
    if (u) { while (u[n] && n < nMaxCount - 1) { buf[n] = u[n]; n++; } free(u); }
    buf[n] = 0;
    return n;
}
int GetWindowTextA(HWND hwnd, char *buf, int nMaxCount)
{
    if (!buf || nMaxCount <= 0) return 0;
    char name[512];
    name[0] = 0;
    wpf_x11_query_title(hwnd, name, sizeof(name));
    int n = 0;
    while (name[n] && n < nMaxCount - 1) { buf[n] = name[n]; n++; }
    buf[n] = 0;
    return n;
}
// CharSet.Auto → 裸名收 UTF-8（StringBuilder 在 Unix 上就是字节缓冲）
int GetWindowText(HWND h, char *b, int n) { return GetWindowTextA(h, b, n); }

int GetWindowTextLengthW(HWND hwnd)
{
    uint16_t buf[512];
    return GetWindowTextW(hwnd, buf, 512);
}
// [EntryPoint="GetWindowTextLengthWrapper", CharSet=Auto] → 裸名，无字符串参数
int GetWindowTextLengthWrapper(HWND h) { wpf_set_last_error(0); return GetWindowTextLengthW(h); }
// [EntryPoint="GetWindowTextWrapper", CharSet=Auto] → StringBuilder 是 **UTF-8 字节缓冲**
int GetWindowTextWrapper(HWND h, char *buf, int n) { wpf_set_last_error(0); return GetWindowTextA(h, buf, n); }

static BOOL set_prop_utf8(HWND hwnd, const char *utf8, HANDLE data)
{
    if (!utf8) { wpf_set_last_error(87); return 0; }
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock);
              wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE); return 0; }
    for (wpf_prop *p = w->props; p; p = p->next) {
        if (strcmp(p->name, utf8) == 0) {
            HANDLE old = p->value;
            p->value = data;
            pthread_mutex_unlock(&g_wpf.lock);
            return old == NULL;   // Win32: 覆盖已存在的属性返回 FALSE
        }
    }
    wpf_prop *p = (wpf_prop *)calloc(1, sizeof(wpf_prop));
    p->name = strdup(utf8);
    p->value = data;
    p->next = w->props;
    w->props = p;
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

static HANDLE get_prop_utf8(HWND hwnd, const char *utf8)
{
    HANDLE r = NULL;
    if (!utf8) return NULL;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w)
        for (wpf_prop *p = w->props; p; p = p->next)
            if (strcmp(p->name, utf8) == 0) { r = p->value; break; }
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

static HANDLE remove_prop_utf8(HWND hwnd, const char *utf8)
{
    HANDLE r = NULL;
    if (!utf8) return NULL;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) {
        wpf_prop **pp = &w->props;
        while (*pp) {
            if (strcmp((*pp)->name, utf8) == 0) {
                wpf_prop *v = *pp;
                *pp = v->next;
                r = v->value;
                free(v->name); free(v);
                break;
            }
            pp = &(*pp)->next;
        }
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

// CharSet.Auto（Unix→Ansi）：裸名与 A 变体都收 UTF-8
BOOL SetPropA(HWND h, const char *n, HANDLE d) { return set_prop_utf8(h, n, d); }
BOOL SetProp(HWND h, const char *n, HANDLE d)  { return set_prop_utf8(h, n, d); }
HANDLE GetPropA(HWND h, const char *n) { return get_prop_utf8(h, n); }
HANDLE GetProp(HWND h, const char *n)  { return get_prop_utf8(h, n); }
HANDLE RemovePropA(HWND h, const char *n) { return remove_prop_utf8(h, n); }
HANDLE RemoveProp(HWND h, const char *n)  { return remove_prop_utf8(h, n); }

// UTF-16 变体：先转码再走同一条路径
BOOL SetPropW(HWND hwnd, const uint16_t *name, HANDLE data)
{
    char *utf8 = wpf_utf16_to_utf8_dup(name);
    BOOL r = set_prop_utf8(hwnd, utf8, data);
    free(utf8);
    return r;
}

HANDLE GetPropW(HWND hwnd, const uint16_t *name)
{
    char *utf8 = wpf_utf16_to_utf8_dup(name);
    HANDLE r = get_prop_utf8(hwnd, utf8);
    free(utf8);
    return r;
}

HANDLE RemovePropW(HWND hwnd, const uint16_t *name)
{
    char *utf8 = wpf_utf16_to_utf8_dup(name);
    HANDLE r = remove_prop_utf8(hwnd, utf8);
    free(utf8);
    return r;
}

int GetWindowThreadProcessId(HWND hwnd, int32_t *pid)
{
    wpf_global_init();
    if (pid) *pid = (int32_t)getpid();
    return (int)wpf_thread_id_of_window(hwnd);
}

// 进程内线程 id：Win32 返回的是「创建该窗口的线程 id」。
// 这里用 pthread_self 的数值（x86-64/glibc 上就是一个指针值）低 31 位。
uint32_t wpf_thread_id_of_window(HWND hwnd)
{
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    uint32_t r = w ? WPF_THREAD_ID(w->owner_thread) : WPF_THREAD_ID(wpf_thread_self());
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

// ── 默认窗口过程 ───────────────────────────────────────────────────────────
// 真实现：对 shim 自己产生的消息给出 Win32 的默认结果；其余返回 0。
// **不是**空壳——HwndSubclass 的整个回退链（CallWindowProc(DefWndProc, ...)）
// 都落在这里，返回错误值会直接改变上层可见行为。
LRESULT DefWindowProcW(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg) {
        case WM_NCCREATE:  return 1;    // 允许建窗（HwndWrapper 依赖）
        case WM_CLOSE:     DestroyWindow(hwnd); return 0;
        case WM_ERASEBKGND: return 1;   // 说“我擦过了”，避免重绘风暴
        case WM_MOUSEACTIVATE: return 3; // MA_NOACTIVATE
        case WM_NCHITTEST: return 1;    // HTCLIENT
        case WM_SETCURSOR: return 1;
        // ── 【波 59】`WM_SYSCOMMAND`：Win32 的"系统命令"入口 ───────────────────────
        //   【修前】本 shim **完全没有**这条 case ⇒ `SystemCommands.MaximizeWindow(...)`
        //   （`Microsoft.Windows.Shell/SystemCommands.cs:37` ⇒ `PostMessage(hwnd, WM_SYSCOMMAND,
        //   SC_MAXIMIZE)`）落到 `default: return 0` ⇒ **什么都不会发生**。
        //   本示例走的是托管 `WindowState` 那条路（见 `wpf_core_window_state`），但
        //   `WM_SYSCOMMAND` 是"任何 Win32 应用的最大化/最小化/关闭/还原"的公共入口，
        //   而且**双击标题栏最大化**在 Windows 上也正是 DefWindowProc 处理
        //   `WM_NCLBUTTONDBLCLK(HTCAPTION)` ⇒ 发 `SC_MAXIMIZE`（下面那条）。
        //   比较前必须 `& 0xFFF0`（Win32：低 4 位被系统用于内部标志）。
        case WM_SYSCOMMAND: {
            UINT cmd = (UINT)(wParam & 0xFFF0);
            switch (cmd) {
                case SC_MAXIMIZE: wpf_core_window_state(hwnd, WPF_WS_MAX);    return 0;
                case SC_MINIMIZE: wpf_core_window_state(hwnd, WPF_WS_MIN);    return 0;
                case SC_RESTORE:  wpf_core_window_state(hwnd, WPF_WS_NORMAL); return 0;
                case SC_CLOSE:    wpf_dispatch_to_window(hwnd, WM_CLOSE, 0, 0); return 0;
                default:          return 0;   // SC_MOVE/SC_SIZE 等：X11 上没有对应的模态循环
            }
        }
        // ── 【波 59】双击非客户区：Win32 的"双击标题栏 ⇒ 最大化/还原" ────────────────
        case WM_NCLBUTTONDBLCLK:
            if ((int)wParam == HTCAPTION) {
                wpf_lock();
                wpf_window *wd = wpf_window_find(hwnd);
                int was_max = wd ? wd->maximized : 0;
                pthread_mutex_unlock(&g_wpf.lock);
                wpf_core_window_state(hwnd, was_max ? WPF_WS_NORMAL : WPF_WS_MAX);
            }
            return 0;
        case WM_GETMINMAXINFO:
            // ── 【波 50 · `D-G83` 真因】本 case 必须是 **no-op**（`return 0;` 什么都不填）──
            // 【Win32 语义】这条消息是**系统发给窗口过程**的，而结构体在**发之前**就由
            //   USER32 按"最大化后的尺寸/位置"填好了（"the system ... sets the default values
            //   ... before sending"）；`DefWindowProc` 对它的默认处理是**什么都不做**。
            //   ⇒ "填默认值"这件事属于**发消息方**，不属于 `DefWindowProc`。
            // 【波 58 的错在哪 —— 本件实测反证】波 58 把这里从 `return 0;` 改成
            //   `fill_minmaxinfo_defaults(lParam);`，理由是"这个 case 是**死码**"
            //   （旧注释逐字：本 shim 从不发这条消息 ⇒ DefWindowProc 的分支不可达）。
            //   **那个前提是错的**：W82A 的读数证明本 case **每次派发都被走到**，而且是在
            //   **窗口过程已经写好之后**（配对读数，`WPF_LINUX_CREATE_DIAG=1`）：
            //       [WMSIZE_DIAG] WM_GETMINMAXINFO(after-map): 默认 min=1x1 max(=屏幕)=1280x1024
            //                    → 窗口过程回填 min=1x1 max=1280x1024 ⇒ 应用**没有**声明上限 …
            //       [WMSIZE_DIAG] DefWindowProcW(WM_GETMINMAXINFO): 进来时 **min=521x417 max=667x500**
            //                    → 本 case 会把它**就地改成**默认值
            //   ⇒ 托管侧明明写对了（`521x417/667x500` 正是 `MinWidth=500/MinHeight=400` 与
            //   `MaxWidth=640/MaxHeight=480` × dpi `1.041667` 的换算结果），却被**我们自己的
            //   `DefWindowProcW` 覆盖回默认值** ⇒ `app_declared` 恒假 ⇒ `PMaxSize` 永不发。
            //   这条通道"对应用声明的值失效"的最后一跳就在这里（不是"问早了"那一跳）。
            // 【为什么窗口过程写对了却还走到 DefWindowProc】托管侧**故意**把它复位成
            //   `handled = false`：`WindowFilterMessage`（上游 `Window.cs:4234-4302`）第一段
            //   `switch` 在 `:4250-4252` 把 `handled = WmGetMinMaxInfo(lParam)`（返回 `true`），
            //   紧接着 `:4258` 的 `if(_swh != null && _swh.CompositionTarget != null)` **第二段**
            //   `switch`（`:4272-4298`）里**没有** `WM_GETMINMAXINFO` 这一格 ⇒ 落到
            //   `default: handled = false;` ⇒ 覆盖掉前一格的 `true`（W82A 的反射直调读数：
            //   `handled=False` 而结构体**确实被写好**）。**这不是我们的错、也不是要改托管侧**：
            //   在真 Windows 上 `handled=false` 只意味着"顺带也请默认过程过一遍"，而默认过程
            //   对这条消息本来就是 no-op ⇒ **只要本 case 回到 no-op，语义就与 Windows 一致**。
            if (lParam) {
                WPF_MINMAXINFO *pin = (WPF_MINMAXINFO *)(uintptr_t)lParam;
                wpf_wmsize_diag("DefWindowProcW(WM_GETMINMAXINFO): 进来时 min=%dx%d max=%dx%d "
                                "⇒ 本 case 是 no-op（默认值由**发消息方**填）⇒ 原样保留",
                                pin->ptMinTrackSize.x, pin->ptMinTrackSize.y,
                                pin->ptMaxTrackSize.x, pin->ptMaxTrackSize.y);
            }
            return 0;
        case WM_PAINT:     return 0;
        default:           return 0;
    }
}
LRESULT DefWindowProcA(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }
LRESULT DefWindowProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }
LRESULT DefWindowProcWorker(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

// CallWindowProc：直接调函数指针。这是 HwndSubclass 的窗口过程链全部依赖的入口。
LRESULT CallWindowProcW(WNDPROC proc, HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (!proc) return DefWindowProcW(hwnd, msg, wParam, lParam);
    return proc(hwnd, msg, wParam, lParam);
}
LRESULT CallWindowProcA(WNDPROC p, HWND h, UINT m, WPARAM w, LPARAM l) { return CallWindowProcW(p, h, m, w, l); }
LRESULT CallWindowProc(WNDPROC p, HWND h, UINT m, WPARAM w, LPARAM l) { return CallWindowProcW(p, h, m, w, l); }

// ── 监控器 / 系统度量 ──────────────────────────────────────────────────────
// 无 XRandR 时的真值来源是 X server 的 screen 尺寸（XDisplayWidth/Height）。
int GetSystemMetrics(int nIndex)
{
    wpf_global_init();
    int sw = 1280, sh = 1024;
    if (wpf_x11_ensure()) {
        sw = DisplayWidth(g_wpf.dpy, g_wpf.screen);
        sh = DisplayHeight(g_wpf.dpy, g_wpf.screen);
    }
    switch (nIndex) {
        case SM_CXSCREEN: case SM_CXVIRTUALSCREEN: return sw;
        case SM_CYSCREEN: case SM_CYVIRTUALSCREEN: return sh;
        case SM_XVIRTUALSCREEN: case SM_YVIRTUALSCREEN: return 0;
        case SM_CMONITORS: return 1;
        case SM_MOUSEWHEELPRESENT: return 1;
        case SM_SWAPBUTTON: return 0;
        case SM_MENUDROPALIGNMENT: return 0;
        case SM_REMOTESESSION: return 0;
        case SM_CXICON: case SM_CYICON: return 32;
        case SM_CXSMICON: case SM_CYSMICON: return 16;
        case SM_CXDOUBLECLK: case SM_CYDOUBLECLK: return 4;
        case SM_CXDRAG: case SM_CYDRAG: return 4;
        case SM_CYCAPTION: return 0;    // 无 WM 装饰
        case SM_CXBORDER: case SM_CYBORDER: return 1;
        default: return 0;
    }
}

HMONITOR MonitorFromWindow(HWND hwnd, UINT flags) { (void)hwnd; (void)flags; return (HMONITOR)(uintptr_t)1; }
HMONITOR MonitorFromPoint(WPF_POINT pt, UINT flags) { (void)pt; (void)flags; return (HMONITOR)(uintptr_t)1; }
HMONITOR MonitorFromRect(const WPF_RECT *rc, UINT flags) { (void)rc; (void)flags; return (HMONITOR)(uintptr_t)1; }

BOOL GetMonitorInfoW(HMONITOR mon, WPF_MONITORINFOEX *info)
{
    (void)mon;
    wpf_global_init();
    if (!info) { wpf_set_last_error(87); return 0; }
    // ★W70A（`D-G72`）：**必须先读调用方填的 `cbSize`**。
    //   托管侧有**两套形状**：`MONITORINFO`（40 B：cbSize/rcMonitor/rcWork/dwFlags，**没有 szDevice**）
    //   与 `MONITORINFOEX`（72 B，含 `szDevice[32]`）。旧版**从不读 cbSize**，无条件写 `szDevice`
    //   ⇒ 对 40 B 的调用方**越界写 32 字节**。实测（W70A 的 dlopen 探针 `$HOME/w70a/bin/wgmon.c`，
    //   调用方是 hc 的 `InteropValues.MONITORINFO`，cbSize=40）：
    //       `CASE cbsize40 ret=1 … OVERWRITE_BYTES_PAST_STRUCT=32`
    //   `docs/U2-M7b-report.md:226` 早在 U2 就写下过这条预测（"症状会是 GetMonitorInfo 把 32 字节
    //   写越界踩掉后面的栈"），一直没兑现成读数；本趟兑现了。
    //   Win32 语义：`cbSize` 既不是 `sizeof(MONITORINFO)` 也不是 `sizeof(MONITORINFOEX)`
    //   ⇒ `SetLastError(ERROR_INVALID_PARAMETER=87)` ＋ **FALSE 且一个字节都不写**。
    //   这里取"≥ 基线 40 即合法、只写调用方声明装得下的那部分"的**保守**读法：
    //   永不越界；`cbSize` 不足 ⇒ 如实失败（不许静默、不许越界）。
    const uint32_t cb = (uint32_t)info->cbSize;
    const size_t need_base = offsetof(WPF_MONITORINFOEX, szDevice);   // = 40
    const size_t need_ex   = sizeof(WPF_MONITORINFOEX);               // = 72
    if (cb < need_base) { wpf_set_last_error(87); return 0; }
    int sw = 1280, sh = 1024;
    if (wpf_x11_ensure()) {
        sw = DisplayWidth(g_wpf.dpy, g_wpf.screen);
        sh = DisplayHeight(g_wpf.dpy, g_wpf.screen);
    }
    info->rcMonitor.left = 0; info->rcMonitor.top = 0;
    info->rcMonitor.right = sw; info->rcMonitor.bottom = sh;
    /* ── 【`D-G147` · 修法 B】`rcWork` **不再**恒等于 `rcMonitor` ────────────────
       Win32 语义：`rcMonitor` = 整块显示器；`rcWork` = 桌面可用区（排除任务栏/停靠区）。
       修前是 `info->rcWork = info->rcMonitor;` ⇒ 凡按工作区布局的消费者
       （`SystemParameters.WorkArea`、`Window.WorkAreaBoundsForNearestMonitor`
       = `upstream …/Window.cs:7385-7401` 的 `rcWork`）在此桥上拿到整块显示器
       ⇒ 最大化/贴边/钳制会压到面板之下。
       现在工作区取自 `wpf_x11_workarea()`（`_NET_WORKAREA` 当前桌面格）；
       没有 WM 或属性缺失/畸形 ⇒ **显式声明的**回退 == 显示器（"无 WM 会加装饰"的现场
       本来就装得下），并且**逐趟上屏**（`wpf_x11_workarea_declare`）——
       ⚠️ "回退"与"静默恒等"的差别**只**在这一行声明上（`criteria.md` §2③）。 */
    {
        int wx = 0, wy = 0, ww = 0, wh = 0;
        wpf_x11_workarea(&wx, &wy, &ww, &wh);
        if (ww > 0 && wh > 0) {
            info->rcWork.left = wx; info->rcWork.top = wy;
            info->rcWork.right = wx + ww; info->rcWork.bottom = wy + wh;
        } else {
            info->rcWork = info->rcMonitor;          // 无 X：与 rcMonitor 同（并由 declare 声明）
        }
        wpf_x11_workarea_declare("GetMonitorInfo", &info->rcMonitor, 1,
                                 info->rcWork.left, info->rcWork.top,
                                 info->rcWork.right, info->rcWork.bottom);
    }
    /* `MONITORINFOF_PRIMARY`：本平台 `MonitorFromWindow/Point/Rect` 恒返回同一个句柄
       且 `EnumDisplayMonitors` 恒报 1 个显示器（`win32_core.c`）⇒ 这**唯一**一个显示器
       就是主显示器 ⇒ `dwFlags = 1` 正确。将来真做多显示器枚举时，本字段必须按"是否主屏"
       逐台计算（判据见 `criteria.md` §3 射程，本条不在本修法射程内）。 */
    info->dwFlags = 1;   // MONITORINFOF_PRIMARY
    // szDevice **只在调用方声明了 MONITORINFOEX 时才写**（否则就是上面那条越界）。
    if (cb >= need_ex) {
        // szDevice 在 Unix 上是 **char[32]（ANSI/UTF-8）**，不是 uint16_t[32]：
        // 托管侧 [MarshalAs(ByValArray, SizeConst=32)] char[] 配 CharSet.Auto，
        // 而 Unix 上 Auto 折叠为 Ansi。写错会踩掉结构体后面的内存。
        memset(info->szDevice, 0, sizeof(info->szDevice));
        const char *dev = "X11";
        for (int i = 0; i < (int)sizeof(info->szDevice) - 1 && dev[i]; i++)
            info->szDevice[i] = dev[i];
    }
    return 1;
}
BOOL GetMonitorInfoA(HMONITOR m, WPF_MONITORINFOEX *i) { return GetMonitorInfoW(m, i); }
BOOL GetMonitorInfo(HMONITOR m, WPF_MONITORINFOEX *i) { return GetMonitorInfoW(m, i); }

BOOL EnumDisplayMonitors(HDC hdc, const WPF_RECT *clip, void *proc, LPARAM lParam)
{
    // 回调类型是 NativeMethods.MonitorEnumProc：
    //   bool (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT, IntPtr dwData)
    // 只有 1 个监控器 → 调一次。
    (void)hdc; (void)clip;
    if (!proc) return 0;
    wpf_global_init();
    int sw = 1280, sh = 1024;
    if (wpf_x11_ensure()) {
        sw = DisplayWidth(g_wpf.dpy, g_wpf.screen);
        sh = DisplayHeight(g_wpf.dpy, g_wpf.screen);
    }
    WPF_RECT rc = { 0, 0, sw, sh };
    typedef BOOL (*enumproc_t)(HMONITOR, HDC, WPF_RECT *, LPARAM);
    return ((enumproc_t)proc)((HMONITOR)(uintptr_t)1, NULL, &rc, lParam) ? 1 : 0;
}

// ── 键盘 / 光标 / 其它已降级项 ──────────────────────────────────────────────
// 这些 API 的返回值在上层只用来「挑分支」，没有可观察的副作用；
// 给常量比给 0 更接近 Windows 行为，且不会让调用方走进错误分支。
int GetDoubleClickTime(void) { return 500; }
// ── [D-K1 · 波 24] `GetKeyState` **逐次调用探针**（默认关；有界；触顶看得见）──────────
// 要回答的问题（主控 2026-09-14 批准的那条读数设计）：整批档 `Ctrl+A` 不生效，
//   到底是"**WPF 在派发之外读表**"还是"**下游另有原因**"？
// 本探针把**每一次** `GetKeyState`／`GetAsyncKeyState` 调用照原样打一行：
//   · `t=`   调用时刻（`wpf_now_ms()` = `CLOCK_MONOTONIC` 毫秒）——与 MSGFLOW 的
//            `dispatch … t=` 行**同一个时钟**，两边按 `t` 直接对齐；
//   · `在dispatch中=` 此刻是否在 `DispatchMessageW` 之内（`wpf_msg_in_dispatch()`）；
//   · `快照=` 这条消息的修饰快照**有没有登记到**（与上一项**是两件事**，必须分开看）；
//   · `返回=` 最终返回位；`实时表字节=` 同一时刻实时表的原始字节。
// ⇒ 三种情形一眼可分：①`在dispatch中=否` ⇒ 读发生在派发之外（预处理/泵循环）；
//   ②`在dispatch中=是 快照=无` ⇒ 派发期但这条消息没登记到快照 ⇒ 落到实时表；
//   ③`快照=已登记(0x2)` 且 `返回=0x8000` ⇒ 修饰位**读得到** ⇒ 失败在下游。
// 【默认关 & 零开销】开关只在**首次**调用时读一次环境变量（`WPF_LINUX_KEYSTATE_TRACE=1`）；
//   关着时每次调用只多一条静态判断，不取时间、不拼串、不加锁、不动计数器。
// 【有界 & 触顶看得见】最多打印 `WPF_KS_TRACE_MAX` 行，随后**打一条"已达上限"并继续计数**
//   （"报 0 ≠ 不存在"：被抑制的调用数写在那条提示里，不静默丢失）。
// 【波 24 主控要求 3（L12 现场重演）】第一版把**所有** VK 都打 ⇒ 整批档 368 次调用把 200 行额度吃满，
//   第二档**没有逐行读数**。现在：**只打印"修饰键类"的调用**（含通用位与左右专有位），
//   非修饰键**按设计不打印、只计数**；额度只被修饰键行消耗 ⇒ 修法乙的验证有逐行证据。
//   `GetKeyboardState` 不按 VK，因此在**有修饰键按下时**才打（那才是"第二个缺口"会被咬到的情形）。
#define WPF_KS_TRACE_MAX 200
static int  s_ks_trace = -1;      // -1=还没读过环境变量；0=关；1=开
static long s_ks_max = -1;        // 生效的打印额度（默认 WPF_KS_TRACE_MAX；可用 env 抬）
static long s_ks_calls;           // 开探针以来**发生过的调用总数**（含不打印与被抑制的）
static long s_ks_mod_calls;       // 其中**修饰键类**（探针只打印这一类）
static long s_ks_nonmod_calls;    // 其中**非修饰键**（按设计不打，只计数）
static long s_ks_kbs_calls;       // 其中 `GetKeyboardState`（不按 VK，单独计）
static long s_ks_shown;           // 已经打印过的行数（含那条上限提示）

// 【额度可覆盖（波 24 追加，回应"别只有一个 DP.Text"）】实测整批两档**一趟**里 WPF 会问修饰键
//   **308 次**（`汇总`行原文）⇒ 默认 200 行看不全两档的注入窗口。`WPF_LINUX_KEYSTATE_TRACE_MAX=<n>`
//   可把额度抬到需要的行数；非法值/非正值**回落到默认**（不静默变成 0 行 —— 那正是"报 0 ≠ 不存在"要防的）。
static long wpf_ks_max_lines(void)
{
    if (s_ks_max < 0) {
        const char *e = getenv("WPF_LINUX_KEYSTATE_TRACE_MAX");
        long v = (e && e[0]) ? strtol(e, NULL, 10) : 0;
        s_ks_max = (v > 0 && v <= 100000) ? v : WPF_KS_TRACE_MAX;
    }
    return s_ks_max;
}

// 退出时的**真实总数**（"报 0 ≠ 不存在"：被抑制的调用也必须有地方看得见）。
// 只在探针开启时由 `atexit` 登记一次；关着时连登记都不会发生。
static void wpf_ks_summary(void)
{
    long cap = wpf_ks_max_lines();
    long shown = (s_ks_shown > cap) ? cap : s_ks_shown;
    fprintf(stderr, "[KEYSTATE] 汇总：本次运行共 %ld 次调用（修饰键 %ld 次，已打印 %ld 行；"
                    "非修饰键 %ld 次按设计不打；GetKeyboardState %ld 次）｜行上限 %ld\n",
            s_ks_calls, s_ks_mod_calls, shown, s_ks_nonmod_calls, s_ks_kbs_calls, cap);
}

static int wpf_ks_trace_on(void)
{
    if (s_ks_trace < 0) {
        const char *e = getenv("WPF_LINUX_KEYSTATE_TRACE");
        s_ks_trace = (e && e[0] == '1') ? 1 : 0;
        if (s_ks_trace) atexit(wpf_ks_summary);   // 只在开探针时登记一次：退出时给**真实总数**
    }
    return s_ks_trace;
}
static int wpf_ks_is_modifier(int vk)
{
    switch (vk) {
    case 0x10: case 0xA0: case 0xA1:   // Shift（通用 / L / R）
    case 0x11: case 0xA2: case 0xA3:   // Control
    case 0x12: case 0xA4: case 0xA5:   // Alt / Menu
    case 0x5B: case 0x5C:              // Win
        return 1;
    default:
        return 0;
    }
}
static const char *wpf_ks_vk_name(int vk)
{
    switch (vk) {
    case 0x10: case 0xA0: case 0xA1: return "VK_SHIFT";
    case 0x11: case 0xA2: case 0xA3: return "VK_CONTROL";
    case 0x12: case 0xA4: case 0xA5: return "VK_MENU";
    case 0x5B: case 0x5C:            return "VK_LWIN";
    default:                          return "-";
    }
}
// 只管"打印额度"与触顶提示（调用侧的计数在各自分支里做，避免重复计）
static int wpf_ks_begin_print(void)
{
    long cap = wpf_ks_max_lines();
    if (s_ks_shown >= cap) {
        if (s_ks_shown == cap) {     // 第一次触顶：把"有行被抑制"这件事本身说出来
            s_ks_shown++;
            fprintf(stderr, "[KEYSTATE] …已达上限 %ld 行（**只对修饰键类行计数**）：后续修饰键调用"
                            "**只计数、不再打印**（触顶这一刻：修饰键 %ld 次／总调用 %ld 次；"
                            "退出时会打一条汇总，要全量请分批跑或用 WPF_LINUX_KEYSTATE_TRACE_MAX 抬额度重跑）\n",
                    cap, s_ks_mod_calls, s_ks_calls);
        }
        return 0;
    }
    s_ks_shown++;
    return 1;
}
static void wpf_ks_trace(const char *api, int vk, short ret, int have_snap, uint8_t snap, uint8_t live)
{
    if (!wpf_ks_trace_on()) return;
    s_ks_calls++;
    if (!wpf_ks_is_modifier(vk)) { s_ks_nonmod_calls++; return; }   // 波 24 要求 3：非修饰键**按设计不打**
    s_ks_mod_calls++;
    if (!wpf_ks_begin_print()) return;
    fprintf(stderr, "[KEYSTATE] #%ld %s vk=0x%02x(%s) 返回=0x%04x 在dispatch中=%s 快照=%s(0x%x) "
                    "实时表字节=0x%02x 实时表 ctrl=%d shift=%d alt=%d t=%llums\n",
            s_ks_calls, api, vk, wpf_ks_vk_name(vk), (unsigned)(uint16_t)ret,
            wpf_msg_in_dispatch() ? "是" : "否", have_snap ? "已登记" : "无", (unsigned)snap,
            (unsigned)live,
            (g_wpf.key_state[0x11] & 0x80) ? 1 : 0, (g_wpf.key_state[0x10] & 0x80) ? 1 : 0,
            (g_wpf.key_state[0x12] & 0x80) ? 1 : 0, (unsigned long long)wpf_now_ms());
}

// [D-K1 修法] 从状态表读（表由 `win32_x11.c` 的翻译层维护）：
//   高位 bit15 = 此刻按下（WPF 的 `Keyboard.Modifiers` 只读这一位）；
//   低位 bit0  = 锁定态（CapsLock/NumLock，Win32 同义）。
//   返回值语义与 Windows 一致：`(short)(0x8000 | toggle)`。
static short wpf_keystate_read(int vk, const char *api)
{
    if (vk < 0 || vk > 0xFF) return 0;
    // 【波 44 · D-G49】鼠标五键**不能**读键盘表（那张表只由键盘翻译层填 ⇒ 它们恒 0）。
    //   上游 `Win32MouseDevice:61` 用 `GetKeyState(VK_LBUTTON) & 0x8000` 判 `MouseButtonState`，
    //   读不到 ⇒ 恒 `Released` ⇒ `ButtonBase` 只 `Focus()` 不激活（复选框不勾/下拉不开/滑块不动）。
    //   ⇒ 只对这五个 VK 改问 X 的真实指针按键态；**键盘路径（下面 from_snap/live 两段）逐字未动**。
    if (vk == 0x01 || vk == 0x02 || vk == 0x04 || vk == 0x05 || vk == 0x06) {
        short mret = wpf_x11_mouse_keystate(vk);
        wpf_ks_trace(api, vk, mret, 0, 0, 0);
        return mret;
    }
    // [D-K1 · 波 20] **优先**读"正在派发的那条消息"的修饰快照：
    //   实测一次 `xdotool key ctrl+a` 的 4 个 X 事件在同一次泵里被整批抽干，而消息之后才逐条派发
    //   ⇒ 派发期若读"实时表"，读到的是**最后**一个事件的状态（Ctrl 已抬起）⇒ WPF 看不到修饰键。
    uint8_t snap = 0;
    int have_snap = wpf_keystate_dispatch_mods(&snap);   // 纯读静态变量、无副作用 ⇒ 探针可无条件取一次
    uint8_t live = g_wpf.key_state[vk];                  // 同上：与旧写法一样是不加锁的原始字节读
    short ret;
    int from_snap = 0;
    if (have_snap) {
        switch (vk) {
        case 0x10: case 0xA0: case 0xA1: ret = (short)((snap & 0x1) ? (short)0x8000 : 0); from_snap = 1; break;
        case 0x11: case 0xA2: case 0xA3: ret = (short)((snap & 0x2) ? (short)0x8000 : 0); from_snap = 1; break;
        case 0x12: case 0xA4: case 0xA5: ret = (short)((snap & 0x4) ? (short)0x8000 : 0); from_snap = 1; break;
        case 0x5B: case 0x5C:            ret = (short)((snap & 0x8) ? (short)0x8000 : 0); from_snap = 1; break;
        default: break;   // 非修饰键：仍走实时表
        }
    }
    if (!from_snap) ret = (short)(((live & 0x80) ? 0x8000 : 0) | (live & 0x01));
    wpf_ks_trace(api, vk, ret, have_snap, snap, live);
    return ret;
}
short GetKeyState(int vk)      { return wpf_keystate_read(vk, "GetKeyState"); }
// 简化（如实登记）：Win32 的 `GetAsyncKeyState` 与 `GetKeyState` 的差别在"消息队列内外"，
// 本 shim 只有一张实时表 ⇒ 两者读同一来源（对 WPF 的用法等价）。
// （波 24：两条各打各的 `api=` 标签，免得探针把调用点混成一个。）
short GetAsyncKeyState(int vk) { return wpf_keystate_read(vk, "GetAsyncKeyState"); }

// 顺手补上**此前完全缺失**的 `GetKeyboardState`（布局 = Win32：每字节 bit7=按下、bit0=toggle）。
// [D-K1 · 波 24] 探针**只加一行日志、不改语义**：本 API 按 Win32 语义是"整表复制"，
//   它**不叠**派发快照（与 `GetKeyState` 的取数口径不同）。整批场景里若有调用方指望
//   从它看到 Ctrl，那这里就是第二个缺口 —— 探针把它显式标出来，避免"只查 GetKeyState"漏判。
BOOL GetKeyboardState(uint8_t *out)
{
    if (!out) { wpf_set_last_error(87); return 0; }
    wpf_lock();
    memcpy(out, g_wpf.key_state, sizeof(g_wpf.key_state));
    pthread_mutex_unlock(&g_wpf.lock);
    if (wpf_ks_trace_on()) {
        s_ks_kbs_calls++;
        s_ks_calls++;
        // [D-K1 · 波 24 要求 3] 本 API **不按 VK** ⇒ 不参与"只打修饰键"的过滤；但**只在有修饰键按下时打**
        //   （那才是"第二个缺口"会被咬到的情形），否则只计数、不占额度。
        int any_mod = ((g_wpf.key_state[0x11] | g_wpf.key_state[0x10] |
                        g_wpf.key_state[0x12] | g_wpf.key_state[0x5B]) & 0x80) ? 1 : 0;
        if (any_mod && wpf_ks_begin_print())
            fprintf(stderr, "[KEYSTATE] #%ld GetKeyboardState 返回=1 在dispatch中=%s 实时表 ctrl=%d shift=%d alt=%d t=%llums"
                            "（⚠ 本 API 整表复制、**不叠**派发快照）\n",
                    s_ks_calls, wpf_msg_in_dispatch() ? "是" : "否",
                    (g_wpf.key_state[0x11] & 0x80) ? 1 : 0, (g_wpf.key_state[0x10] & 0x80) ? 1 : 0,
                    (g_wpf.key_state[0x12] & 0x80) ? 1 : 0, (unsigned long long)wpf_now_ms());
    }
    return 1;
}
void  keybd_event(uint8_t vk, uint8_t scan, uint32_t flags, UINT_PTR extra)
{ (void)vk; (void)scan; (void)flags; (void)extra; }          // 注入走 XTest（M1 已有），不在这里伪造成 XSendEvent
void  mouse_event(uint32_t flags, int32_t dx, int32_t dy, uint32_t data, UINT_PTR extra)
{ (void)flags; (void)dx; (void)dy; (void)data; (void)extra; }

HKL GetKeyboardLayout(uint32_t threadId)
{
    (void)threadId;
    return (HKL)(uintptr_t)0x0409;   // en-US；X11 的键盘映射由 XKB 管，见 README
}
int GetKeyboardLayoutList(int size, HKL *list)
{
    if (list && size > 0) list[0] = (HKL)(uintptr_t)0x0409;
    return 1;
}
int GetKeyboardLayoutListWrapper(int size, HKL *list) { wpf_set_last_error(0); return GetKeyboardLayoutList(size, list); }
HKL ActivateKeyboardLayout(HKL hkl, UINT flags) { (void)flags; return hkl; }
int MapVirtualKeyW(UINT code, UINT mapType) { (void)mapType; return (int)code; }
int MapVirtualKeyA(UINT code, UINT mapType) { return MapVirtualKeyW(code, mapType); }
int MapVirtualKey(UINT code, UINT mapType) { return MapVirtualKeyW(code, mapType); }

BOOL GetCursorPos(WPF_POINT *pt)
{
    wpf_global_init();
    if (!pt) return 0;
    if (!wpf_x11_ensure()) return 0;
    Window root_ret, child_ret;
    int rx = 0, ry = 0, wx = 0, wy = 0;
    unsigned int mask = 0;
    if (!XQueryPointer(g_wpf.dpy, g_wpf.root, &root_ret, &child_ret,
                       &rx, &ry, &wx, &wy, &mask))
        return 0;
    pt->x = rx; pt->y = ry;      // XQueryPointer 的 root 坐标就是屏幕坐标
    return 1;
}

HCURSOR GetCursor(void) { return (HCURSOR)(uintptr_t)1; }
HCURSOR SetCursor(HCURSOR c) { HCURSOR old = (HCURSOR)(uintptr_t)1; (void)c; return old; }
// CharSet.Auto → 裸名收 UTF-8（我们本来就不解析光标名，只是把编码约定摆正）
HCURSOR LoadCursorA(HINSTANCE h, const char *name) { (void)h; (void)name; return (HCURSOR)(uintptr_t)1; }
HCURSOR LoadCursor(HINSTANCE h, const char *name) { return LoadCursorA(h, name); }
HCURSOR LoadCursorW(HINSTANCE h, const uint16_t *name) { (void)h; (void)name; return (HCURSOR)(uintptr_t)1; }
BOOL DestroyCursor(HCURSOR c) { (void)c; return 1; }
int ShowCursor(BOOL show) { (void)show; return 0; }

// GetSysColor：原实现不看 index、一律回 0x00F0F0F0 ⇒ 托管侧 `SystemColors` 的每一项
// （含 WindowText / Window / ButtonFace / ButtonShadow）都退化成同一个灰值。
// 后果：凡"靠 SystemColors 取色"的控件（经典主题模板 —— Label/TextBox/PasswordBox/CheckBox…）
// 字色与底色同值 ⇒ 控件在可视树里尺寸/模板齐全却**什么都看不见**（实测：ICDStudio 登录窗）。
// 这里按 Win32 的 COLOR_* 索引给出 Win10 默认调色板；值 = R | G<<8 | B<<16（COLORREF 口径）。
int GetSysColor(int index)
{
    switch (index) {
    case 0:  return 0x00C8C8C8;   // COLOR_SCROLLBAR
    case 1:  return 0x00000000;   // COLOR_BACKGROUND（桌面）
    case 2:  return 0x00D1B499;   // COLOR_ACTIVECAPTION
    case 3:  return 0x00DBCDBF;   // COLOR_INACTIVECAPTION
    case 4:  return 0x00F0F0F0;   // COLOR_MENU
    case 5:  return 0x00FFFFFF;   // COLOR_WINDOW
    case 6:  return 0x00646464;   // COLOR_WINDOWFRAME
    case 7:  return 0x00000000;   // COLOR_MENUTEXT
    case 8:  return 0x00000000;   // COLOR_WINDOWTEXT
    case 9:  return 0x00000000;   // COLOR_CAPTIONTEXT
    case 10: return 0x00F0F0F0;   // COLOR_ACTIVEBORDER
    case 11: return 0x00F0F0F0;   // COLOR_INACTIVEBORDER
    case 12: return 0x00ABABAB;   // COLOR_APPWORKSPACE
    case 13: return 0x00D77800;   // COLOR_HIGHLIGHT
    case 14: return 0x00FFFFFF;   // COLOR_HIGHLIGHTTEXT
    case 15: return 0x00F0F0F0;   // COLOR_BTNFACE（Control）
    case 16: return 0x00A0A0A0;   // COLOR_BTNSHADOW（ControlDark）
    case 17: return 0x006D6D6D;   // COLOR_GRAYTEXT
    case 18: return 0x00000000;   // COLOR_BTNTEXT（ControlText）
    case 19: return 0x00000000;   // COLOR_INACTIVECAPTIONTEXT
    case 20: return 0x00FFFFFF;   // COLOR_BTNHIGHLIGHT（ControlLightLight）
    case 21: return 0x00696969;   // COLOR_3DDKSHADOW（ControlDarkDark）
    case 22: return 0x00E3E3E3;   // COLOR_3DLIGHT（ControlLight）
    case 23: return 0x00000000;   // COLOR_INFOTEXT
    case 24: return 0x00E1FFFF;   // COLOR_INFOBK
    case 26: return 0x00CC6600;   // COLOR_HOTLIGHT
    case 27: return 0x00EAD1B9;   // COLOR_GRADIENTACTIVECAPTION
    case 28: return 0x00F2E4D7;   // COLOR_GRADIENTINACTIVECAPTION
    case 29: return 0x00FF9933;   // COLOR_MENUHILIGHT
    case 30: return 0x00F0F0F0;   // COLOR_MENUBAR
    }
    return 0x00F0F0F0;
}

int MessageBeep(UINT type) { (void)type; return 1; }             // 静音环境下无对象可做

// TrackMouseEvent：X11 侧的 WM_MOUSELEAVE 由 LeaveNotify 事件驱动（我们在
// win32_x11.c 里按 EnterNotify/LeaveNotify 直接产生），所以这里只需要记住
// 「调用方想要 TME_LEAVE」并且**返回成功**——语义确实被满足了。
BOOL TrackMouseEvent(WPF_TRACKMOUSEEVENT *tme)
{
    if (!tme) { wpf_set_last_error(87); return 0; }
    if (tme->dwFlags & TME_CANCEL) return 1;
    return 1;
}

// ── `D-G64`（2026-09-20）：`WindowFromPoint` 的**下降**判据 ──────────────────────
// 【为什么必须有这一层】Win32 里**没有"框架窗"这一层**（框架即窗口本身），而 X11 上**会重定父的
//   窗口管理器**（实测 `xfwm4`；用户会话里的 gnome-shell/mutter 同类）会在 root 与我们的客户窗
//   之间插一层**框架窗** ⇒ 原先 `XTranslateCoordinates(root → root)` 返回的 root 直接子窗口是
//   **框架**（实测 `0x200264`）而不是客户窗（`0x200004`）。
//   上游 `HwndMouseInputProvider.ReportInput`
//   （`upstream/…/PresentationCore/System/Windows/Interop/HwndMouseInputProvider.cs:1285-1320`）
//   把本函数的返回值与它自己的 `hwnd` 比对：**不等** ⇒ 打 `Spurious mouse event` 并 `return false`
//   ⇒ 而 `_active = true` / `Actions.Activate` 在 `:1329`，**永远到不了** ⇒ **每一击都被吞、且永不恢复**。
//   现场（车道 W53A 四格矩阵）：无 WM 的 Xvfb 上五步点击全绿；`Xvfb + xfwm4` 上**同一坐标、
//   客户坐标逐条相同**的 12 次点击 `preMouseDown=0`、`AE=0`、`LB sel` 恒 `-1/31`（应用 `alive=yes`）；
//   同一进程里 `kill -TERM <xfwm4>` ⇒ X 把客户还给 root ⇒ **下一次点击立刻恢复**（单变量强证）。
//   ⚠️ 这就是用户报的"界面里点击没反应"：**无 WM 的验收装置永远复现不出来**。
// 【口径（与 Win32 对齐）】从 root 的直接子窗口**逐层下沉**，取"含该点、且**属于本进程**"的
//   **最深**窗口；若整条链上一个本进程窗口都没有（例如别的应用真的盖在上面）⇒ **原样返回顶层窗口**
//   （那正是 Win32 的行为 ⇒ 上游会正确地把那一击判为"不属于自己"）。
// 【可证伪】env `WPF_LINUX_WFP_DIAG=1` ⇒ 每进程 ≤ 40 行打印 `pt/top/own_top/best/depth`（**只打印**）；
//   反极性 = 修前 `best=top=`框架、修后 `best=`客户窗（见 `src/Linux/build/MilBridge/W54A-report.md`）。
static int wpf_point_in_window(Window win, int rx, int ry)
{
    XWindowAttributes at;
    if (!XGetWindowAttributes(g_wpf.dpy, win, &at)) return 0;
    if (at.map_state != IsViewable) return 0;      // 未映射/不可见 ⇒ Win32 的 WindowFromPoint 也不返回它
    if (at.width <= 0 || at.height <= 0) return 0;
    int cx = 0, cy = 0;
    Window junk = 0;
    if (!XTranslateCoordinates(g_wpf.dpy, g_wpf.root, win, rx, ry, &cx, &cy, &junk)) return 0;
    return (cx >= 0 && cy >= 0 && cx < at.width && cy < at.height) ? 1 : 0;
}

// 本进程窗口判定：`wpf_window_find` **要求持锁** ⇒ 自己拿/放，且**刻意不跨 X 调用持锁**
//   （避免与 `win32_x11.c` 的 `XLOCK()` 之间形成锁序依赖）。`wpf_lock` 本身可重入。
static int wpf_is_own_window(Window win)
{
    wpf_lock();
    int own = (wpf_window_find((HWND)(uintptr_t)win) != NULL) ? 1 : 0;
    wpf_unlock();
    return own;
}

HWND WindowFromPoint(WPF_POINT pt)
{
    wpf_global_init();
    if (!wpf_x11_ensure()) return NULL;
    Window child = 0;
    // XTranslateCoordinates 到 root 的子窗口：拿到「最上层、含该点的窗口」——**注意**它可能是
    //   WM 的**框架**（有 WM 时就是），所以下面必须继续下沉（见上面的 `D-G64` 说明）。
    int rx = 0, ry = 0;
    XTranslateCoordinates(g_wpf.dpy, g_wpf.root, g_wpf.root,
                          pt.x, pt.y, &rx, &ry, &child);
    if (!child) return NULL;

    int own_top = wpf_is_own_window(child);
    HWND top = (HWND)(uintptr_t)child;
    HWND best = own_top ? top : NULL;    // 无 WM 的常规情形：顶层就是客户窗
    const char *d = getenv("WPF_LINUX_WFP_DIAG");
    int want_diag = (d && *d && *d != '0') ? 1 : 0;
    static int diag_n = 0;

    Window cur = child;
    int depth = 0;
    while (depth++ < 8) {                // 深度上限 8：防御性（正常 ≤ 2 层：框架 → 客户）
        Window r = 0, p = 0, *kids = NULL;
        unsigned int n = 0;
        if (!XQueryTree(g_wpf.dpy, cur, &r, &p, &kids, &n) || n == 0) {
            if (kids) XFree(kids);
            break;
        }
        Window hit = 0;
        for (int i = (int)n - 1; i >= 0; --i) {   // XQueryTree 的表**自下而上** ⇒ 从末尾（最上层）往回找
            if (wpf_point_in_window(kids[i], rx, ry)) { hit = kids[i]; break; }
        }
        XFree(kids);
        if (!hit) break;
        cur = hit;
        if (wpf_is_own_window(cur)) best = (HWND)(uintptr_t)cur;
    }

    HWND ret = best ? best : top;
    if (want_diag && diag_n++ < 40) {
        fprintf(stderr, "[WFP_DIAG] pt=%d,%d top=0x%lx own_top=%d ret=0x%lx depth=%d\n",
                pt.x, pt.y, (unsigned long)child, own_top,
                (unsigned long)(uintptr_t)ret, depth);
        fflush(stderr);
    }
    return ret;
}

BOOL EnumThreadWindows(uint32_t threadId, void *proc, LPARAM lParam)
{
    if (!proc) return 0;
    typedef BOOL (*cb_t)(HWND, LPARAM);
    wpf_lock();
    for (wpf_window *w = g_wpf.windows; w; w = w->next) {
        if (WPF_THREAD_ID(w->owner_thread) != threadId) continue;
        if (!((cb_t)proc)(w->hwnd, lParam)) break;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}

// MapWindowPoints：把 cPoints 个点从 hWndFrom 的客户坐标系搬到 hWndTo 的客户坐标系。
// 我们没有非客户区，坐标系只差窗口原点，所以就是「加上 from 原点、减去 to 原点」。
// 注意 Win32 的语义：缓冲区被当作一个 **POINT 数组**，cPoints 是**点的个数**——
// 所以托管侧传 `ref RECT, cPoints=2` 时正好是「矩形的左上 + 右下」两个点，
// 一套实现同时覆盖 POINT / RECT / Win32Rect / Win32Point 四种声明。
int MapWindowPoints(HWND hWndFrom, HWND hWndTo, WPF_POINT *pts, int cPoints)
{
    wpf_global_init();
    if (!pts) return 0;
    int dx = 0, dy = 0;
    wpf_lock();
    if (hWndFrom && hWndFrom != HWND_DESKTOP) {
        wpf_window *w = wpf_window_find(hWndFrom);
        if (w) { dx -= w->x; dy -= w->y; }
    }
    if (hWndTo && hWndTo != HWND_DESKTOP) {
        wpf_window *w = wpf_window_find(hWndTo);
        if (w) { dx += w->x; dy += w->y; }
    }
    pthread_mutex_unlock(&g_wpf.lock);
    for (int i = 0; i < cPoints; i++) {
        pts[i].x += dx;
        pts[i].y += dy;
    }
    // Win32 返回值的低 16 位是 x 位移的绝对值、高 16 位是 y 的（坐标有符号，取绝对值）
    return (int)(((uint32_t)(dy < 0 ? -dy : dy) & 0xFFFF) << 16) |
           (int)((uint32_t)(dx < 0 ? -dx : dx) & 0xFFFF);
}

// PresentationNative_cor3.dll 的包装名（先 SetLastError(0) 再转发）
int MapWindowPointsWrapper(HWND from, HWND to, WPF_POINT *pts, int cPoints)
{
    wpf_set_last_error(0);
    return MapWindowPoints(from, to, pts, cPoints);
}

BOOL AdjustWindowRectEx(WPF_RECT *rc, uint32_t style, BOOL menu, uint32_t exStyle)
{
    // X11 下非客户区由 WM 负责，我们只有客户区 → 矩形不变。
    // 这正是「无 WM 时」的正确答案，不是空实现。
    (void)style; (void)menu; (void)exStyle;
    return rc ? 1 : 0;
}
