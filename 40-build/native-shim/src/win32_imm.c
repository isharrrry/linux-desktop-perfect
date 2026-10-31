// 【ACC-017 / D-014 第三层】IMM32 最小实现（见 win32_imm.h 的说明）。
//   设计原则：**不发明语义**。每个函数都按 Windows 的契约做最少的事，
//   数据来源只有一个 —— XIM 回调写进来的那块缓冲。
#include "win32_imm.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

// Windows 侧常量（取值与 sdkddkver/imm.h 一致）
#define WM_IME_STARTCOMPOSITION 0x010D
#define WM_IME_ENDCOMPOSITION   0x010E
#define WM_IME_COMPOSITION      0x010F
#define GCS_COMPSTR   0x0008
#define GCS_CURSORPOS 0x0080
#define GCS_RESULTSTR 0x0800

typedef struct {
    int      available;       // 有 IME 服务器？
    int      open;            // IME 开关（ImmGetOpenStatus）
    int      in_composition;
    uint16_t compstr[1024];   // 预编辑（UTF-16），**不含**结尾 0
    int      compstr_chars;
    int      cursor;
    uint16_t result[1024];    // 提交结果
    int      result_chars;
    void    *hwnd;            // 投递目标
} wpf_himc;

static wpf_himc       g_himc;
static wpf_imm_stats  g_stats;

// 诊断（与 shim 其它模块一致，用 WPF_LINUX_KEY_DIAG 门控）
static int imm_diag_on(void)
{
    static int cached = -1;
    if (cached < 0) { const char *v = getenv("WPF_LINUX_KEY_DIAG"); cached = (v && *v && *v != '0') ? 1 : 0; }
    return cached;
}
static void imm_diag(const char *fmt, ...)
{
    if (!imm_diag_on()) return;
    va_list ap; va_start(ap, fmt);
    fputs("[IME] ", stderr); vfprintf(stderr, fmt, ap); fputc('\n', stderr);
    va_end(ap); fflush(stderr);
}

// libwpfwin32.so 内部已实现 PostMessageW；这里只声明（避免把 win32_abi.h 的整套 typedef 拖进来）
extern int PostMessageW(void *hwnd, unsigned msg, uintptr_t wparam, intptr_t lparam);

static void post_ime(unsigned msg, intptr_t lparam)
{
    if (!g_himc.hwnd) return;
    PostMessageW(g_himc.hwnd, msg, 0, lparam);
    if (msg == WM_IME_COMPOSITION) g_stats.composition_msgs++;
}

void wpf_imm_set_available(int avail) { g_himc.available = avail ? 1 : 0; }
int  wpf_imm_is_available(void)       { return g_himc.available; }
void wpf_imm_bind_hwnd(void *hwnd)    { g_himc.hwnd = hwnd; }

void wpf_imm_preedit_start(void)
{
    g_himc.in_composition = 1;
    g_himc.compstr_chars = 0;
    g_himc.cursor = 0;
    g_stats.preedit_starts++;
    imm_diag("WM_IME_STARTCOMPOSITION → hwnd=%p", g_himc.hwnd);
    post_ime(WM_IME_STARTCOMPOSITION, 0);
}

void wpf_imm_preedit_update(const uint16_t *text, int chars, int cursor)
{
    if (chars < 0) chars = 0;
    if (chars > (int)(sizeof(g_himc.compstr) / sizeof(g_himc.compstr[0]))) chars = (int)(sizeof(g_himc.compstr) / sizeof(g_himc.compstr[0]));
    if (text && chars > 0) memcpy(g_himc.compstr, text, (size_t)chars * sizeof(uint16_t));
    g_himc.compstr_chars = chars;
    g_himc.cursor = cursor;
    g_stats.preedit_updates++;
    imm_diag("WM_IME_COMPOSITION(GCS_COMPSTR) chars=%d cursor=%d", g_himc.compstr_chars, g_himc.cursor);
    // WPF 在 WM_IME_COMPOSITION 里按 lParam 的位判断要读哪一段
    post_ime(WM_IME_COMPOSITION, GCS_COMPSTR | GCS_CURSORPOS);
}

void wpf_imm_preedit_done(void)
{
    g_himc.in_composition = 0;
    g_stats.preedit_dones++;
    imm_diag("WM_IME_ENDCOMPOSITION");
    post_ime(WM_IME_ENDCOMPOSITION, 0);
}

void wpf_imm_commit(const uint16_t *text, int chars)
{
    if (chars < 0) chars = 0;
    if (chars > (int)(sizeof(g_himc.result) / sizeof(g_himc.result[0]))) chars = (int)(sizeof(g_himc.result) / sizeof(g_himc.result[0]));
    if (text && chars > 0) memcpy(g_himc.result, text, (size_t)chars * sizeof(uint16_t));
    g_himc.result_chars = chars;
    g_himc.compstr_chars = 0;      // 提交后预编辑清空
    g_stats.commits++;
    imm_diag("WM_IME_COMPOSITION(GCS_RESULTSTR) chars=%d", g_himc.result_chars);
    post_ime(WM_IME_COMPOSITION, GCS_RESULTSTR);
}

void wpf_imm_get_stats(wpf_imm_stats *out) { if (out) *out = g_stats; }

// ── IMM32 导出面 ───────────────────────────────────────────────────────────

void *ImmGetContext(void *hwnd)
{
    imm_diag("ImmGetContext hwnd=%p available=%d", hwnd, g_himc.available);
    if (!g_himc.available) return NULL;
    if (hwnd) g_himc.hwnd = hwnd;
    return (void *)&g_himc;
}

int ImmReleaseContext(void *hwnd, void *himc) { (void)hwnd; (void)himc; return 1; }

int ImmAssociateContext(void *hwnd, void *himc)
{
    // 返回"原来的 HIMC"；调用方一般不检查内容
    (void)hwnd;
    return (int)(intptr_t)himc;
}

void *ImmGetDefaultIMEWnd(void *hwnd) { return hwnd; }
int ImmGetProperty(void *himc, unsigned index) { (void)himc; (void)index; return 0; }
int ImmConfigureIME(void *hwnd, void *himc, unsigned mode, void *data) { (void)hwnd; (void)himc; (void)mode; (void)data; return 0; }

int ImmGetOpenStatus(void *himc) { (void)himc; return g_himc.open; }
int ImmSetOpenStatus(void *himc, int open) { (void)himc; g_himc.open = open ? 1 : 0; return 1; }

int ImmGetConversionStatus(void *himc, unsigned *conv, unsigned *sentence)
{
    (void)himc;
    if (conv) *conv = 0;          // IME_CMODE_NATIVE 等按 0（拼音）返回
    if (sentence) *sentence = 0;  // IME_SMODE_NONE
    return 1;
}

int ImmSetConversionStatus(void *himc, unsigned conv, unsigned sentence)
{
    (void)himc; (void)conv; (void)sentence; return 1;
}

// 读预编辑/提交文本。返回**字节数**（Windows 语义：UTF-16 字节数，不含结尾 0）。
int ImmGetCompositionStringW(void *himc, unsigned index, void *buf, unsigned len)
{
    (void)himc;
    const uint16_t *src = NULL;
    int chars = 0, cursor = 0;
    switch (index) {
        case GCS_COMPSTR:   src = g_himc.compstr; chars = g_himc.compstr_chars; break;
        case GCS_RESULTSTR: src = g_himc.result;  chars = g_himc.result_chars;  break;
        case GCS_CURSORPOS: cursor = g_himc.cursor; break;
        default: return 0;
    }
    if (index == GCS_CURSORPOS) {
        // CURSORPOS 的返回值是"字符偏移"，不写缓冲
        return cursor;
    }
    int bytes = chars * 2;
    if (!buf || len == 0) return bytes;          // 只问长度
    unsigned copy = (len < (unsigned)bytes) ? len : (unsigned)bytes;
    memcpy(buf, src, copy);
    return (int)copy;
}

int ImmGetCompositionStringA(void *himc, unsigned index, void *buf, unsigned len)
{
    // WPF 用 W 版；A 版给最小可用（ASCII 近似）
    return ImmGetCompositionStringW(himc, index, buf, len);
}

int ImmGetCompositionString(void *himc, unsigned index, void *buf, unsigned len)
{
    return ImmGetCompositionStringW(himc, index, buf, len);
}

int ImmSetCompositionStringW(void *himc, unsigned index, const void *comp, unsigned compLen,
                             const void *read, unsigned readLen)
{
    (void)himc; (void)index; (void)comp; (void)compLen; (void)read; (void)readLen;
    return 0;
}

// 通知 IME 做动作：NI_COMPOSITIONSTR/CPS_CANCEL(=0x10/0x1) 表示取消预编辑
int ImmNotifyIME(void *himc, unsigned action, unsigned index, unsigned value)
{
    (void)himc; (void)value;
    // NI_COMPOSITIONSTR = 0x0015, CPS_CANCEL = 0x0001
    if (action == 0x0015 && index == 0x0001) {
        g_himc.compstr_chars = 0;
        g_himc.in_composition = 0;
        g_stats.cancellations++;
        return 1;
    }
    return 1;
}

// 候选窗/组合窗定位：Linux 侧由 IME 自己画，这里只记录（判据读 calling 次数）
int ImmSetCompositionWindow(void *himc, const void *form) { (void)himc; (void)form; g_stats.candidate_window_calls++; return 1; }
int ImmSetCandidateWindow(void *himc, const void *form)  { (void)himc; (void)form; g_stats.candidate_window_calls++; return 1; }

// ── 【ACC-028】SxS 激活上下文（`EnableVisualStyles` 需要）────────────────────────
//   WinForms 的 `Application.EnableVisualStyles()` → `ThemingScope.CreateActivationContext`
//   → `CreateActCtxW` → `ActivateActCtx`。Windows 上这用来激活 comctl32 的 manifest。
//   **Linux 上没有 SxS，激活上下文没有对应语义** ⇒ 这里返回"成功 + 一个非零假句柄"，
//   让调用方按"已激活"继续走下去（视觉样式的真正的呈现由我们的渲染承担）。
//   实测：不加这两个，WinForms 程序在 `ApplicationConfiguration.Initialize()` 就抛
//   `EntryPointNotFoundException: CreateActCtxW`。
static int g_fake_actctx = 1;   // 假句柄的实体（地址稳定即可）

void *CreateActCtxW(const void *pActCtx)
{
    (void)pActCtx;
    imm_diag("CreateActCtxW → 假句柄（Linux 无 SxS）");
    return (void *)&g_fake_actctx;
}

void *CreateActCtxA(const void *pActCtx) { return CreateActCtxW(pActCtx); }
void *CreateActCtx(const void *pActCtx)  { return CreateActCtxW(pActCtx); }

int ActivateActCtx(void *hActCtx, uintptr_t *lpCookie)
{
    (void)hActCtx;
    if (lpCookie) *lpCookie = 0x4C4450u;   // 'LDP'，任意非零值
    return 1;                               // TRUE
}

// 注： 已在 win32_misc.c:1276 实现（此处不重复定义，否则链接重复符号）
void ReleaseActCtx(void *hActCtx) { (void)hActCtx; }
int  AddRefActCtx(void *hActCtx)  { (void)hActCtx; return 1; }

// 【ACC-028】`GetCurrentActCtx`：返回当前激活上下文。Linux 无 SxS ⇒ 返回"成功 + 假句柄"。
//   WinForms 的 `ThemingScope..ctor` 在 `Control.CreateHandle()` 里调它，缺了就抛
//   `EntryPointNotFoundException: GetCurrentActCtx`（实测）。
int GetCurrentActCtx(void **lphActCtx)
{
    if (lphActCtx) *lphActCtx = (void *)&g_fake_actctx;
    return 1;
}

// ── 【ACC-028】窗口站 / 桌面（WinForms 的 `SystemInformation.UserInteractive` 需要）──
//   Windows 上进程属于某个窗口站（WinSta0）和桌面（Default）；Linux 没有这个概念。
//   这里返回**固定的非零假句柄**，并让 `GetUserObjectInformationW` 对"名字"查询返回 "WinSta0"/"Default"，
//   对"标志"查询返回 0（表示没有 WSF_VISIBLE 之外的语义）。
//   实测：不补这两个，WinForms 在 `ApplicationConfiguration.Initialize()` 就抛
//   `EntryPointNotFoundException: GetProcessWindowStation`。
static int g_fake_winsta = 1;
static int g_fake_desktop = 2;

void *GetProcessWindowStation(void) { return (void *)&g_fake_winsta; }
void *GetThreadDesktop(uint32_t tid) { (void)tid; return (void *)&g_fake_desktop; }

// nIndex: 2 = UOI_NAME, 1 = UOI_FLAGS, 3 = UOI_TYPE, 4 = UOI_USER_SID
int GetUserObjectInformationW(void *hObj, int nIndex, uint16_t *pvInfo, uint32_t nLength, uint32_t *pNeeded)
{
    const char *name = (hObj == (void *)&g_fake_desktop) ? "Default" : "WinSta0";
    if (nIndex == 2 || nIndex == 3) {            // UOI_NAME / UOI_TYPE
        if (!pvInfo || nLength < 2) { if (pNeeded) *pNeeded = 2; return 0; }
        int i = 0;
        while (name[i] && (uint32_t)(i + 1) * 2 <= nLength) { pvInfo[i] = (uint16_t)name[i]; i++; }
        pvInfo[i] = 0;
        if (pNeeded) *pNeeded = (uint32_t)(i + 1) * 2;
        return 1;
    }
    if (nIndex == 1) {                            // UOI_FLAGS → USEROBJECTFLAGS{ fInherit, fReserved, dwFlags }
        if (!pvInfo || nLength < 12) { if (pNeeded) *pNeeded = 12; return 0; }
        int32_t *f = (int32_t *)pvInfo;
        f[0] = 0; f[1] = 0; f[2] = 1;             // dwFlags = WSF_VISIBLE
        if (pNeeded) *pNeeded = 12;
        return 1;
    }
    if (pNeeded) *pNeeded = 0;
    return 0;
}

void *GetProcessWindowStationA(void) { return GetProcessWindowStation(); }
int GetUserObjectInformationA(void *h, int i, void *p, uint32_t n, uint32_t *pn) { return GetUserObjectInformationW(h, i, (uint16_t *)p, n, pn); }

// ── 【ACC-028】GDI 字体（WinForms 的 `System.Windows.Forms` 用 GDI，不是 GDI+）─────────
//   WinForms 的 `Control`/`TextRenderer` 走 `CreateFontIndirectW` → `SelectObject` → `TextOut`/`GetTextMetrics`。
//   本 shim 的 GDI 历来只有 `SelectObject`/`DeleteObject`/`GetObjectW` 三个存根（WPF 不需要 GDI 绘制）。
//   Linux 上真正的文字渲染在 Skia/DirectWrite 那条链上；这里给字体**一个稳定的假句柄**，
//   让控件能构造出来。**诚实标注**：用这个句柄做 `TextOut` 不会真的画出字形（见 K-016 的口径）。
//   实测：不补它，WinForms 在 `Control..ctor` → `CreateFontIndirect` 就抛 `EntryPointNotFoundException`。
static int g_fake_font = 1;

void *CreateFontIndirectW(const void *lplf)
{
    (void)lplf;
    return (void *)&g_fake_font;
}

void *CreateFontIndirectA(const void *lplf) { return CreateFontIndirectW(lplf); }
void *CreateFontIndirect(const void *lplf)  { return CreateFontIndirectW(lplf); }

void *CreateFontW(int32_t h, int32_t w, int32_t esc, int32_t orient, int32_t weight,
                  uint32_t italic, uint32_t underline, uint32_t strike,
                  uint32_t charset, uint32_t outPrec, uint32_t clipPrec, uint32_t quality,
                  uint32_t pitch, const uint16_t *face)
{
    (void)h; (void)w; (void)esc; (void)orient; (void)weight; (void)italic; (void)underline;
    (void)strike; (void)charset; (void)outPrec; (void)clipPrec; (void)quality; (void)pitch; (void)face;
    return (void *)&g_fake_font;
}
void *CreateFontA(int32_t h, int32_t w, int32_t esc, int32_t orient, int32_t weight,
                  uint32_t italic, uint32_t underline, uint32_t strike,
                  uint32_t charset, uint32_t outPrec, uint32_t clipPrec, uint32_t quality,
                  uint32_t pitch, const char *face)
{
    return CreateFontW(h, w, esc, orient, weight, italic, underline, strike, charset, outPrec, clipPrec, quality, pitch, (const uint16_t *)face);
}
