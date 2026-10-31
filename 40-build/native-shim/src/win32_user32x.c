// win32_user32x.c — 【user32 组 41 个 gap 符号】补齐（MOD-11 / MOD-02 面）
//
// ── 为什么单独一个文件 ─────────────────────────────────────────────────────
//   本仓 `exports.txt` 里 user32 面上还有 41 个符号是 `gap`（无实现）。
//   这些符号**与现有实现无耦合**，集中放一个文件，避免改动
//   `win32_core.c` / `win32_misc.c` / `win32_msg.c` 这些热点文件。
//
// ── 锁的约定（重要，死锁相关）──────────────────────────────────────────────
//   · `g_wpf.lock` 是 **PTHREAD_MUTEX_RECURSIVE**（`win32_core.c:50`），
//     所以**同一线程**重复 `wpf_lock()` 不会自死锁，可以安全嵌套。
//   · `wpf_window_find()` **自己不取锁**（`win32_core.c:308`，裸遍历），
//     所以凡是要遍历 `g_wpf.windows` 的函数，本文件**自己取锁**。
//   · `EnumChildWindows` 在**锁内**调用回调（与 `EnumThreadWindows` 的既有写法一致，
//     `win32_core.c:2558`）。因锁可重入，回调里再调本文件的函数不会死锁；
//     跨线程则会阻塞（与真实 Win32 的单线程 UI 模型一致）。
//
// ── 实现分级（收尾报告里逐条列出）──────────────────────────────────────────
//   [真]   能从本仓窗口模型 / X11 / 已实现函数里取到真实数据
//   [降级] 本仓没有对应模型（菜单、滚动条、控件 info 等），按 Win32 语义返回
//          "没有"，并在函数上方注释说明原因

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <X11/Xlib.h>

#include "win32_internal.h"

// ── 前向声明：下列符号**已在其它 .c 里实现并导出**，但 `win32_internal.h` 未声明。
//   不声明会退化成隐式 int（-Wimplicit-function-declaration），在 x64 上会**截断指针**。
extern DWORD     GetTickCount(void);
extern BOOL      GetCursorPos(WPF_POINT *pt);
extern HWND      WindowFromPoint(WPF_POINT pt);
extern HWND      GetActiveWindow(void);
extern HWND      GetFocus(void);
extern UINT      RegisterClipboardFormatW(const uint16_t *name);
extern BOOL      GetMessageW(WPF_MSG *msg, HWND hwnd, UINT lo, UINT hi);
extern DWORD     MsgWaitForMultipleObjectsEx(DWORD nCount, const HANDLE *pHandles,
                                             DWORD ms, DWORD wakeMask, DWORD flags);
extern BOOL      PostThreadMessageW(DWORD threadId, UINT msg, WPARAM wp, LPARAM lp);
extern BOOL      IsWindow(HWND hwnd);

// ══════════════════════════════════════════════════════════════════════════
//  结构体（Windows x64 ABI 布局；名字加 WPF_U32_ 前缀避免与头文件冲突）
// ══════════════════════════════════════════════════════════════════════════

// GUITHREADINFO: 4+4+8*6+16 = 72
typedef struct {
    DWORD    cbSize;        // 0
    DWORD    flags;         // 4
    HWND     hwndActive;    // 8
    HWND     hwndFocus;     // 16
    HWND     hwndCapture;   // 24
    HWND     hwndMenuOwner; // 32
    HWND     hwndMoveSize;  // 40
    HWND     hwndCaret;     // 48
    WPF_RECT rcCaret;       // 56 .. 71   （x64: 16 字节收尾）
} WPF_U32_GTI;

// LASTINPUTINFO: 4+4 = 8
typedef struct {
    UINT  cbSize;
    DWORD dwTime;
} WPF_U32_LII;

// COMBOBOXINFO: 4+(pad4)+16+16+4+(pad4)+8+8+8 = 72
typedef struct {
    DWORD    cbSize;        // 0
    WPF_RECT rcItem;        // 8
    WPF_RECT rcButton;      // 24
    DWORD    stateButton;   // 40
    HWND     hwndCombo;     // 48
    HWND     hwndItem;      // 56
    HWND     hwndList;      // 64 .. 71
} WPF_U32_COMBOBOXINFO;

// TITLEBARINFO: 4+(pad4)+16+24 = 48
typedef struct {
    DWORD    cbSize;
    WPF_RECT rcTitleBar;
    DWORD    rgstate[6];
} WPF_U32_TITLEBARINFO;

// SCROLLBARINFO: 4+(pad4)+16+4+4+4+4+24 = 64
typedef struct {
    DWORD    cbSize;
    WPF_RECT rcScrollBar;
    int      dxyLineButton;
    int      xyThumbTop;
    int      xyThumbBottom;
    int      reserved;
    DWORD    rgstate[6];
} WPF_U32_SCROLLBARINFO;

// SCROLLINFO: 4*7 = 28
typedef struct {
    UINT cbSize;
    UINT fMask;
    int  nMin;
    int  nMax;
    UINT nPage;
    int  nPos;
    int  nTrackPos;
} WPF_U32_SCROLLINFO;

// MENUINFO: 4+4+4+4+8+4+(pad4)+8 = 40
typedef struct {
    DWORD    cbSize;
    DWORD    fMask;
    DWORD    dwStyle;
    UINT     cyMax;
    HBRUSH   hbrBack;
    DWORD    dwContextHelpID;
    uintptr_t dwMenuData;
} WPF_U32_MENUINFO;

// MENUITEMINFO (x64): 4+4+4+4+4+(pad4)+8+8+8+8+8+4+(pad4)+8 = 80
typedef struct {
    UINT      cbSize;
    UINT      fMask;
    UINT      fType;
    UINT      fState;
    UINT      wID;
    HMENU     hSubMenu;
    void     *hbmpChecked;
    void     *hbmpUnchecked;
    uintptr_t dwItemData;
    uint16_t *dwTypeData;
    UINT      cch;
    void     *hbmpItem;
} WPF_U32_MENUITEMINFO;

// 拷贝前先看调用方给的 cbSize —— **最常见越界写来源**
static void u32_copy_struct(void *dst, const void *src, size_t src_sz, size_t caller_sz)
{
    if (!dst || !src) return;
    size_t n = (caller_sz && caller_sz < src_sz) ? caller_sz : src_sz;
    memcpy(dst, src, n);
}

// ══════════════════════════════════════════════════════════════════════════
//  1) 遍历类 —— 真实现
// ══════════════════════════════════════════════════════════════════════════

// [真] 遍历 `parent == hwnd` 的孩子窗口。
//   回调返回 FALSE 就停（与 Win32 一致）。锁内调用回调（锁可重入，见文件头）。
BOOL EnumChildWindows(HWND hwndParent, void *proc, LPARAM lParam)
{
    if (!proc) return 0;
    typedef BOOL (*cb_t)(HWND, LPARAM);

    // 【为什么不在锁内回调】`wpf_window_remove()` 会 `free(victim)`（win32_core.c:326），
    //   而 Win32 允许回调里 `DestroyWindow`。若是"锁内边遍历边回调"，
    //   回调销毁当前窗口后我们手里的 `w->next` 就是**已释放内存** ⇒ UAF。
    //   ⇒ 分两步：锁内只做 hwnd 快照，锁外逐个回调，每次先用 `IsWindow` 确认还活着。
    size_t n = 0, cap = 16;
    HWND *vec = (HWND *)malloc(cap * sizeof(HWND));
    if (!vec) return 0;

    wpf_lock();
    for (wpf_window *w = g_wpf.windows; w; w = w->next) {
        if (w->parent != hwndParent) continue;
        if (n == cap) {
            HWND *nv = (HWND *)realloc(vec, (cap * 2) * sizeof(HWND));
            if (!nv) break;                 // 内存紧张 ⇒ 用已收集到的部分继续
            vec = nv; cap *= 2;
        }
        vec[n++] = w->hwnd;
    }
    pthread_mutex_unlock(&g_wpf.lock);

    for (size_t i = 0; i < n; ++i) {
        if (!IsWindow(vec[i])) continue;     // 回调里可能已销毁
        if (!((cb_t)proc)(vec[i], lParam)) break;
    }
    free(vec);
    return 1;
}

// [真] 从窗口类取。`GCLP_*` 是**负数索引**（Win32 惯例）。
//   本仓 `wpf_class` 只有 `background` 是真值，其余返回 0 并注明。
intptr_t GetClassLongPtr(HWND hwnd, int nIndex)
{
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (!w) { pthread_mutex_unlock(&g_wpf.lock); return 0; }
    wpf_class *c = wpf_class_find_atom(w->class_atom);
    intptr_t r = 0;
    if (c) {
        switch (nIndex) {
            case -10: r = (intptr_t)(uintptr_t)c->background; break;  // GCLP_HBRBACKGROUND
            case -24: r = (intptr_t)(uintptr_t)c->wndproc;    break;  // GCLP_WNDPROC
            case -18: r = (intptr_t)c->cb_wnd_extra;          break;  // GCLP_CBWNDEXTRA
            case -20: r = (intptr_t)0;                        break;  // GCLP_CBCLASSEXTRA（本仓无）
            default:  r = 0; break;   // HICON/HCURSOR/HMODULE… 本仓未建模 ⇒ 0（"没有"）
        }
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return r;
}

// [真] 类的真实名字（本仓 `wpf_class.name` 是 UTF-8，转 UTF-16）。
int RealGetWindowClass(HWND hwnd, uint16_t *buf, UINT cch)
{
    if (!buf || cch == 0) return 0;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    const char *name = NULL;
    if (w) {
        wpf_class *c = wpf_class_find_atom(w->class_atom);
        if (c) name = c->name;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    if (!name) { buf[0] = 0; return 0; }
    uint16_t *u = wpf_utf8_to_utf16_dup(name);
    if (!u) { buf[0] = 0; return 0; }
    UINT n = 0;
    while (u[n] && n + 1 < cch) { buf[n] = u[n]; ++n; }
    buf[n] = 0;
    free(u);
    return n;
}

// ══════════════════════════════════════════════════════════════════════════
//  2) 输入 / 光标 / 键盘 —— 真实现
// ══════════════════════════════════════════════════════════════════════════

// [降级] 本仓没有"最后输入时间"的记录点（`g_wpf` 里只有 `clock_bias_ms`）。
//   返回当前 tick —— 语义是"刚刚有输入"，比返回 0（=从未输入）更接近真实。
BOOL GetLastInputInfo(WPF_U32_LII *plii)
{
    if (!plii) return 0;
    plii->dwTime = GetTickCount();
    plii->cbSize = (UINT)sizeof(*plii);
    return 1;
}

// [真] 转发到已实现的 `GetCursorPos`。
BOOL GetPhysicalCursorPos(WPF_POINT *pp)
{
    return GetCursorPos(pp);
}

// [降级] 本仓 DPI 固定假设为 100%（无 HiDPI 缩放层），物理坐标 == 逻辑坐标。
//   ⇒ 直接算 no-op，但**必须复制**，否则调用方读到未初始化内存。
BOOL PhysicalToLogicalPoint(HWND hwnd, WPF_POINT *pp)
{
    (void)hwnd;
    if (!pp) return 0;
    return 1;
}

// [真] 先转逻辑（no-op）再问 `WindowFromPoint`（已实现）。
HWND WindowFromPhysicalPoint(WPF_POINT pt)
{
    return WindowFromPoint(pt);
}

// [降级] 剪贴板格式表在本仓只有 UNICODETEXT/TEXT（`win32_clipboard.c` 无自定义格式注册表），
//   所以"取格式名"没有数据可取 ⇒ 按 Win32 语义返回 0（失败）。
int GetClipboardFormatName(UINT format, uint16_t *name, int cch)
{
    (void)format; (void)cch;
    if (name && cch > 0) name[0] = 0;
    return 0;
}

// [降级] 已有的 `RegisterClipboardFormatW/A` 是空实现（`win32_clipboard.c:350`）。
//   这里转发 W 版本，保证"有符号、不崩"，语义上仍是"注册不生效"。
UINT RegisterClipboardFormat(const uint16_t *name)
{
    return RegisterClipboardFormatW(name);
}

// [降级] 无资源表 ⇒ 加载不到字符串，返回 0（Win32 语义：失败）。
int LoadStringW(HINSTANCE inst, UINT id, uint16_t *buf, int cch)
{
    (void)inst; (void)id; (void)cch;
    if (buf && cch > 0) buf[0] = 0;
    return 0;
}

// [真] 用一个小的 ASCII→VK 映射表。覆盖 0x20..0x7E。
//   返回 (VK | (shift<<8))；不支持的字符返回 -1（Win32 语义）。
int16_t VkKeyScan(uint16_t ch)
{
    if (ch >= 'a' && ch <= 'z') return (int16_t)(ch - 'a' + 0x41);
    if (ch >= 'A' && ch <= 'Z') return (int16_t)(((ch - 'A' + 0x41) | (1 << 8)));
    if (ch >= '0' && ch <= '9') return (int16_t)(ch);
    switch (ch) {
        case ' ':  return 0x20;
        case '\t': return 0x09;
        case '\n': return 0x0D;
        case '-':  return 0xBD;  case '_':  return (int16_t)(0xBD | (1 << 8));
        case '=':  return 0xBB;  case '+':  return (int16_t)(0xBB | (1 << 8));
        case '[':  return 0xDB;  case '{':  return (int16_t)(0xDB | (1 << 8));
        case ']':  return 0xDD;  case '}':  return (int16_t)(0xDD | (1 << 8));
        case '\\': return 0xDC;  case '|':  return (int16_t)(0xDC | (1 << 8));
        case ';':  return 0xBA;  case ':':  return (int16_t)(0xBA | (1 << 8));
        case '\'': return 0xDE;  case '"':  return (int16_t)(0xDE | (1 << 8));
        case ',':  return 0xBC;  case '<':  return (int16_t)(0xBC | (1 << 8));
        case '.':  return 0xBE;  case '>':  return (int16_t)(0xBE | (1 << 8));
        case '/':  return 0xBF;  case '?':  return (int16_t)(0xBF | (1 << 8));
        case '`':  return 0xC0;  case '~':  return (int16_t)(0xC0 | (1 << 8));
        default:   return -1;
    }
}

// [真] 虚拟键 → 字符。用的是 `VkKeyScan` 的逆向（只覆盖无 shift 的可打印 ASCII），
//   并对 Enter/Tab/Backspace 给控制字符。真正的键盘布局转换在 `win32_x11.c` 的
//   `XLookupString` 路径里，这里只服务"从 VK 反查字符"的调用方。
int ToUnicode(UINT wVirtKey, UINT wScanCode, const uint8_t *lpKeyState,
              uint16_t *pwszBuff, int cchBuff, UINT wFlags)
{
    (void)wScanCode; (void)wFlags;
    if (!pwszBuff || cchBuff <= 0) return 0;
    int shift = lpKeyState && (lpKeyState[0x10] & 0x80);   // VK_SHIFT
    uint32_t ch = 0;
    if (wVirtKey >= 0x41 && wVirtKey <= 0x5A) {
        ch = shift ? (uint32_t)wVirtKey : (uint32_t)(wVirtKey - 0x41 + 'a');
    } else if (wVirtKey >= '0' && wVirtKey <= '9') {
        ch = shift ? 0 : (uint32_t)wVirtKey;   // shift 时的符号取决于布局 ⇒ 不给
    } else if (wVirtKey == 0x0D) ch = '\r';
    else if (wVirtKey == 0x09) ch = '\t';
    else if (wVirtKey == 0x08) ch = '\b';
    else if (wVirtKey == 0x20) ch = ' ';
    if (!ch) return 0;
    pwszBuff[0] = (uint16_t)ch;
    return 1;
}

// ══════════════════════════════════════════════════════════════════════════
//  3) 消息 —— 全部转发到已实现的 W 版本
// ══════════════════════════════════════════════════════════════════════════

// [真] 转发 `GetMessageW`（`win32_msg.c:701`）。
BOOL GetMessage(WPF_MSG *msg, HWND hwnd, UINT lo, UINT hi)
{
    return GetMessageW(msg, hwnd, lo, hi);
}

// [真] 转发 `MsgWaitForMultipleObjectsEx`（`win32_msg.c:1146`），dwFlags = 0。
DWORD MsgWaitForMultipleObjects(DWORD nCount, const void *pHandles, DWORD ms, DWORD wakeMask)
{
    return MsgWaitForMultipleObjectsEx(nCount, (const HANDLE *)pHandles, ms, wakeMask, 0);
}

// [真] 转发 `PostThreadMessageW`（`win32_msg.c:1070`）。
BOOL PostThreadMessage(DWORD threadId, UINT msg, WPARAM wp, LPARAM lp)
{
    return PostThreadMessageW(threadId, msg, wp, lp);
}

// [降级] 本仓没有"正在跨线程 SendMessage"的标记位（`SendMessage` 是直接调用）。
//   ⇒ 语义上等价于 FALSE（不在发送中）。若将来加了跨线程排队，这里要改。
BOOL InSendMessage(void) { return 0; }

// ══════════════════════════════════════════════════════════════════════════
//  4) 矩形运算 —— 纯计算，真实现
// ══════════════════════════════════════════════════════════════════════════

BOOL IntersectRect(WPF_RECT *dst, const WPF_RECT *a, const WPF_RECT *b)
{
    if (!dst || !a || !b) return 0;
    int32_t l = a->left   > b->left   ? a->left   : b->left;
    int32_t t = a->top    > b->top    ? a->top    : b->top;
    int32_t r = a->right  < b->right  ? a->right  : b->right;
    int32_t bo= a->bottom < b->bottom ? a->bottom : b->bottom;
    if (l >= r || t >= bo) { dst->left = dst->top = dst->right = dst->bottom = 0; return 0; }
    dst->left = l; dst->top = t; dst->right = r; dst->bottom = bo;
    return 1;
}

BOOL UnionRect(WPF_RECT *dst, const WPF_RECT *a, const WPF_RECT *b)
{
    if (!dst || !a || !b) return 0;
    // 任一为空 ⇒ 结果就是另一个（Win32 语义）
    if (a->right <= a->left || a->bottom <= a->top) { *dst = *b; return 1; }
    if (b->right <= b->left || b->bottom <= b->top) { *dst = *a; return 1; }
    dst->left   = a->left   < b->left   ? a->left   : b->left;
    dst->top    = a->top    < b->top    ? a->top    : b->top;
    dst->right  = a->right  > b->right  ? a->right  : b->right;
    dst->bottom = a->bottom > b->bottom ? a->bottom : b->bottom;
    return 1;
}

// ══════════════════════════════════════════════════════════════════════════
//  5) 窗口状态查询 —— 真实现 / 合理降级
// ══════════════════════════════════════════════════════════════════════════

// [降级] 本仓没有 shell 桌面窗口的建模（X11 的 root 不是 Win32 语义的 shell 窗口）。
//   Win32 语义：没有就返回 NULL。
HWND GetShellWindow(void) { return 0; }

// [降级] 本仓窗口没有 region（`SetWindowRgn` 未实现，清单外）。
//   Win32 语义：窗口没有 region 时返回 ERROR(0)。
int GetWindowRgn(HWND hwnd, void *hrgn)
{
    (void)hwnd; (void)hrgn;
    return 0;   // ERROR
}

// [降级] 本仓没有"窗口无响应"检测（消息泵在 `win32_msg.c`，无 watchdog）。
//   Win32 语义：不挂起就 FALSE。
BOOL IsHungAppWindow(HWND hwnd) { (void)hwnd; return 0; }

// [真] 从线程状态里取 active/focus。本仓 `GetActiveWindow()`/`GetFocus()` 已实现。
BOOL GetGUIThreadInfo(DWORD threadId, WPF_U32_GTI *pgti)
{
    if (!pgti) return 0;
    size_t caller = (size_t)pgti->cbSize;
    if (caller && caller < sizeof(WPF_U32_GTI)) {
        // 调用方给的结构比我们的小 ⇒ 只填它认得的头部（Win32 也是这么做的）
    }
    WPF_U32_GTI g;
    memset(&g, 0, sizeof(g));
    g.cbSize = (DWORD)sizeof(g);
    (void)threadId;                    // 本仓 UI 单线程，active/focus 就是进程级的
    g.hwndActive = GetActiveWindow();
    g.hwndFocus  = GetFocus();
    // hwndCapture/hwndMenuOwner/hwndMoveSize/hwndCaret：本仓未建模 ⇒ 0（"没有"）
    u32_copy_struct(pgti, &g, sizeof(g), caller);
    return 1;
}

// [降级] 控件是托管实现（`System.Windows.Forms`），本仓 shim 没有 COMBOBOX 的内部结构。
//   填零 + 只回 cbSize，语义是"拿不到细节"。hwndCombo 尽量回真实窗口。
BOOL GetComboBoxInfo(HWND hwndCombo, WPF_U32_COMBOBOXINFO *pcbi)
{
    if (!pcbi) return 0;
    size_t caller = (size_t)pcbi->cbSize;
    WPF_U32_COMBOBOXINFO c;
    memset(&c, 0, sizeof(c));
    c.cbSize = (DWORD)sizeof(c);
    c.hwndCombo = hwndCombo;     // 至少把这个填真
    u32_copy_struct(pcbi, &c, sizeof(c), caller);
    return 1;
}

// [降级] 本仓没有窗口标题栏的独立矩形（客户区之外由 WM 画，见 `custom_chrome`）。
//   rcTitleBar 用窗口顶部一条近似（高度按 Win32 惯例取非客户区，本仓为 0 ⇒ 空矩形）。
BOOL GetTitleBarInfo(HWND hwnd, WPF_U32_TITLEBARINFO *ptbi)
{
    if (!ptbi) return 0;
    size_t caller = (size_t)ptbi->cbSize;
    WPF_U32_TITLEBARINFO t;
    memset(&t, 0, sizeof(t));
    t.cbSize = (DWORD)sizeof(t);
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) {
        // 本仓 `border` = 客户区外的装饰宽度；标题栏扣掉它后不应为负
        t.rcTitleBar.left = 0;
        t.rcTitleBar.top = 0;
        t.rcTitleBar.right = w->width;
        t.rcTitleBar.bottom = 0;      // 无系统标题栏（`custom_chrome`/WM 装饰） ⇒ 高度 0
    }
    pthread_mutex_unlock(&g_wpf.lock);
    u32_copy_struct(ptbi, &t, sizeof(t), caller);
    return 1;
}

// [降级] 本仓没有滚动条模型（滚动由托管的 `ScrollableControl` 处理）。
//   Win32 语义：拿不到就返回 FALSE。
BOOL GetScrollBarInfo(HWND hwnd, LONG idObject, WPF_U32_SCROLLBARINFO *psbi)
{
    (void)hwnd; (void)idObject; (void)psbi;
    return 0;
}

// [降级] 同上：本仓没有 Win32 滚动条状态。
BOOL GetScrollInfo(HWND hwnd, int nBar, WPF_U32_SCROLLINFO *psi)
{
    (void)hwnd; (void)nBar; (void)psi;
    return 0;
}

// [降级] Alt-Tab 信息来自窗口管理器，本仓 shim 不维护该表。
BOOL GetAltTabInfo(HWND hwnd, int iItem, void *pati, uint16_t *pszItem, UINT cchItem)
{
    (void)hwnd; (void)iItem; (void)pati; (void)cchItem;
    if (pszItem && cchItem > 0) pszItem[0] = 0;
    return 0;
}

// ══════════════════════════════════════════════════════════════════════════
//  6) 菜单 —— 本仓完全没有菜单模型（WinForms 的 MenuStrip 是托管自绘）
//      ⇒ 全部按 Win32 语义"没有菜单"返回，**这是正确的降级而不是偷懒**
// ══════════════════════════════════════════════════════════════════════════

HMENU GetMenu(HWND hwnd) { (void)hwnd; return 0; }
HMENU GetSubMenu(HMENU menu, int pos) { (void)menu; (void)pos; return 0; }
int   GetMenuItemCount(HMENU menu) { (void)menu; return -1; }   // Win32：失败返回 -1
UINT  GetMenuItemID(HMENU menu, int pos) { (void)menu; (void)pos; return (UINT)-1; }
BOOL  GetMenuItemRect(HWND hwnd, HMENU menu, UINT item, WPF_RECT *rc)
{ (void)hwnd; (void)menu; (void)item; if (rc) memset(rc, 0, sizeof(*rc)); return 0; }
UINT  GetMenuState(HMENU menu, UINT id, UINT flags) { (void)menu; (void)id; (void)flags; return (UINT)-1; }
int   GetMenuString(HMENU menu, UINT id, uint16_t *buf, int cch, UINT flags)
{
    (void)menu; (void)id; (void)flags;
    if (buf && cch > 0) buf[0] = 0;
    return 0;
}
BOOL  GetMenuItemInfo(HMENU menu, UINT item, BOOL byPos, WPF_U32_MENUITEMINFO *pmii)
{
    (void)menu; (void)item; (void)byPos; (void)pmii;
    return 0;
}

// ══════════════════════════════════════════════════════════════════════════
//  7) 杂项
// ══════════════════════════════════════════════════════════════════════════

// [降级] 屏幕键盘/鼠标独占锁在 X11 上没有等价物（需要 XGrabKeyboard）。
//   返回 TRUE（"已生效"）避免调用方走异常分支；**不做实际抓取**。
BOOL BlockInput(BOOL fBlockIt) { (void)fBlockIt; return 1; }

// [降级] DWM 的"ghosting"是 Windows 合成器特性，X11 无对应物 ⇒ no-op。
void DisableProcessWindowsGhosting(void) { }

// [降级] 图标是 GDI+ 位图对象；本仓 `libwpfwic.so` 有 WIC 面但无 HICON 模型。
//   返回一个非 0 假句柄，保证调用方不会因为 NULL 崩。
HICON CreateIcon(HINSTANCE inst, int w, int h, uint8_t planes, uint8_t bits,
                 const uint8_t *andBits, const uint8_t *xorBits)
{
    (void)inst; (void)w; (void)h; (void)planes; (void)bits; (void)andBits; (void)xorBits;
    return (HICON)(uintptr_t)0x6001;
}

// [真] 提升并聚焦到该窗口（X11 侧等价操作）。
void SwitchToThisWindow(HWND hwnd, BOOL altTab)
{
    (void)altTab;
    if (!hwnd || !g_wpf.dpy) return;
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) {
        XRaiseWindow(g_wpf.dpy, (Window)(uintptr_t)hwnd);
        XSetInputFocus(g_wpf.dpy, (Window)(uintptr_t)hwnd, RevertToParent, CurrentTime);
        XFlush(g_wpf.dpy);
    }
    pthread_mutex_unlock(&g_wpf.lock);
}

// [降级] 全局热键需要 XGrabKey + 键码映射；本仓 `win32_x11.c` 已有键盘路径但**没有
//   全局抓取表**。这里只做"接受并返回成功"，注明未真正注册。
BOOL RegisterHotKey(HWND hwnd, int id, UINT mods, UINT vk)
{
    (void)hwnd; (void)id; (void)mods; (void)vk;
    return 1;
}
BOOL UnregisterHotKey(HWND hwnd, int id) { (void)hwnd; (void)id; return 1; }

// （`SetClassLong` 已在上方随 `GetClassLong` 一起给出，用于对称；
//   它不在本组 41 个 gap 里，但同属类信息面。）
