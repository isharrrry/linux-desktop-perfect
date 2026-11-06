// 【ACC-028】GDI 文本/绘制面 —— WinForms 的 `Control` / `TextRenderer` / `SystemInformation`
// 都走 **GDI**（不是 GDI+），而本 shim 的 GDI 历来只有 `SelectObject`/`DeleteObject`/`GetObjectW`
// 三个存根（WPF 不需要 GDI 绘制）。这里按 WinForms 的实际调用面批量补齐。
//
// 口径（AGENTS.md 约束 5「诚实降级」）：
//   · **度量类**（`GetTextMetricsW`/`GetTextExtentPoint32W`/`GetCharWidthW`…）返回**合理的固定值**
//     （按 16px 等宽近似），让布局能算出来；
//   · **绘制类**（`TextOutW`/`ExtTextOutW`/`DrawTextW`/`FillRect`…）**返回成功但不真的画**。
//     这一点必须诚实：WinForms 控件的**文字不会出现在屏幕上**，除非将来接到 Skia 那条链上。
//     本文件的每个绘制桩都带这一句注释，避免后来者误以为"已经渲染了"。
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "win32_abi.h"
#include "win32_gdi_dc.h"
#include <time.h>

// 由 win32_core.c 实现；这里只需地址（GetClassInfoW 要把它填进 WNDCLASSW.lpfnWndProc）
extern intptr_t DefWindowProcW(void *hwnd, uint32_t msg, uintptr_t wp, intptr_t lp);

// ── 一段固定度量：16px 字体、等宽近似 ───────────────────────────────────────
// TEXTMETRICW 的字段顺序（Windows 真实布局）：
//   LONG tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading,
//        tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY,
//   WCHAR tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar,
//   BYTE tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet
typedef struct {
    int32_t tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
    int32_t tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang;
    int32_t tmDigitizedAspectX, tmDigitizedAspectY;
    uint16_t tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
    uint8_t tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
} WPF_TEXTMETRICW;

#define WPF_FAKE_FONT_HEIGHT 16
#define WPF_FAKE_CHAR_WIDTH   8

static void fill_textmetric(WPF_TEXTMETRICW *tm)
{
    memset(tm, 0, sizeof(*tm));
    tm->tmHeight           = WPF_FAKE_FONT_HEIGHT;
    tm->tmAscent           = 13;
    tm->tmDescent          = 3;
    tm->tmInternalLeading  = 2;
    tm->tmExternalLeading  = 0;
    tm->tmAveCharWidth     = WPF_FAKE_CHAR_WIDTH;
    tm->tmMaxCharWidth     = WPF_FAKE_CHAR_WIDTH * 2;
    tm->tmWeight           = 400;
    tm->tmDigitizedAspectX = 96;
    tm->tmDigitizedAspectY = 96;
    tm->tmFirstChar        = 32;
    tm->tmLastChar         = 0xFFFF;
    tm->tmDefaultChar      = 63;
    tm->tmBreakChar        = 32;
    tm->tmPitchAndFamily   = 0x31;   // FF_MODERN | FIXED_PITCH
    tm->tmCharSet          = 1;      // DEFAULT_CHARSET
}

int GetTextMetricsW(void *hdc, WPF_TEXTMETRICW *tm)
{
    (void)hdc;
    if (!tm) return 0;
    fill_textmetric(tm);
    return 1;
}
int GetTextMetricsA(void *hdc, void *tm) { return GetTextMetricsW(hdc, (WPF_TEXTMETRICW *)tm); }
int GetTextMetrics(void *hdc, void *tm)  { return GetTextMetricsW(hdc, (WPF_TEXTMETRICW *)tm); }

// OUTLINETEXTMETRIC / ENUMLOGFONTEX: 返回 0（没有轮廓字体信息）仍是"成功语义"的近似，
// 但**不返回假结构**（调用方通常会退回 GetTextMetricsW）。
uint32_t GetOutlineTextMetricsW(void *hdc, uint32_t size, void *otm)
{
    (void)hdc; (void)size; (void)otm;
    return 0;
}

// ── 文本度量：宽度按 8px/字符近似 ────────────────────────────────────────────
// 老式字符宽度缓存（WinForms 的 TextRenderer 会用）
int GetCharWidthW(void *hdc, uint32_t first, uint32_t last, int32_t *widths)
{
    (void)hdc;
    if (!widths || last < first) return 0;
    for (uint32_t c = first; c <= last; c++) widths[c - first] = WPF_FAKE_CHAR_WIDTH;
    return 1;
}
int GetCharWidthA(void *hdc, uint32_t first, uint32_t last, int32_t *widths) { return GetCharWidthW(hdc, first, last, widths); }
int GetCharWidth32W(void *hdc, uint32_t first, uint32_t last, int32_t *widths) { return GetCharWidthW(hdc, first, last, widths); }

int GetCharABCWidthsW(void *hdc, uint32_t first, uint32_t last, void *abc)
{
    (void)hdc; (void)first; (void)last; (void)abc;
    return 1;   // 无 ABC 信息；调用方通常退回等宽
}

int GetTextExtentPoint32W(void *hdc, const uint16_t *s, int32_t len, void *size)
{
    (void)hdc; (void)s;
    if (!size) return 0;
    int32_t *sz = (int32_t *)size;
    sz[0] = len * WPF_FAKE_CHAR_WIDTH;          // cx
    sz[1] = WPF_FAKE_FONT_HEIGHT;               // cy
    return 1;
}
int GetTextExtentPoint32A(void *hdc, const char *s, int32_t len, void *size) { return GetTextExtentPoint32W(hdc, (const uint16_t *)s, len, size); }

int GetTextExtentPointW(void *hdc, const uint16_t *s, int32_t len, void *size) { return GetTextExtentPoint32W(hdc, s, len, size); }

// ── 绘制：返回成功，但**不真的画**（见文件头口径说明）──────────────────────────
// 【ACC-028 / K-144】**TextOutW 真画**：UTF-16 → UTF-8，XDrawString。
int TextOutW(void *hdc, int32_t x, int32_t y, const uint16_t *s, int32_t len)
{
    // 【ACC-028 / K-144】真画：UTF-16 → UTF-8 → XDrawString
    return wpf_draw_text((HDC)hdc, x, y, s, len) ? 1 : 1;
}
int TextOutA(void *hdc, int32_t x, int32_t y, const char *s, int32_t len) { (void)hdc; (void)x; (void)y; (void)s; (void)len; return 1; }

int ExtTextOutW(void *hdc, int32_t x, int32_t y, uint32_t opts, const void *rect,
                const uint16_t *s, uint32_t len, const int32_t *dx)
{
    (void)opts; (void)dx;
    // 【ACC-028 / K-144】OPAQUE 时先填背景，再画文字
    if (rect) wpf_draw_text_bg((HDC)hdc, (const WPF_RECT *)rect);
    return TextOutW(hdc, x, y, s, (int32_t)len);
}
int ExtTextOutA(void *hdc, int32_t x, int32_t y, uint32_t opts, const void *rect,
                const char *s, uint32_t len, const int32_t *dx)
{
    (void)hdc; (void)x; (void)y; (void)opts; (void)rect; (void)s; (void)len; (void)dx;
    return 1;
}

int DrawTextW(void *hdc, const uint16_t *s, int32_t len, void *rect, uint32_t fmt)
{
    // 【K-356】从「只做垂直居中画一行」升级为**支持对齐标志**的单行绘制。
    //   原实现（K-144）忽略全部 `fmt`，一律「rect 内垂直居中 + 左对齐 + 2px 内边距」
    //   —— 够 Button/Label 用，但：
    //     · `DT_CENTER`/`DT_RIGHT` 的文本会在错误位置（比如 CheckBox 的居中标题、标题栏）
    //     · `DT_TOP`/`DT_BOTTOM` 同理
    //   现在按 `fmt` 分派。**默认行为与旧实现完全一致**（都没设时仍垂直居中 + 左对齐）。
    //
    //   仍未做（诚实登记）：`DT_WORDBREAK` 多行自动换行、`DT_END_ELLIPSIS` 省略号、
    //   `DT_CALCRECT` 只量不画 —— 这些需要真实的字体度量，本仓的 `WPF_FAKE_FONT_HEIGHT`
    //   是固定 16（K-144 的简化），做不了。
    if (!rect) return 1;
    // 【K-456 诊断】确认 `DrawTextW` 是否真的被调用、`dc` 是什么
    if (getenv("LDP_WIC_TRACE")) {
        const WPF_RECT *rr = (const WPF_RECT *)rect;
        fprintf(stderr, "[DRAWTEXT] dc=%p len=%d rect=%d,%d,%d,%d\n",
                hdc, len, rr->left, rr->top, rr->right, rr->bottom);
        fflush(stderr);
    }
    const WPF_RECT *r = (const WPF_RECT *)rect;
    const uint32_t DT_LEFT = 0x0000, DT_CENTER = 0x0001, DT_RIGHT = 0x0002,
                   DT_VCENTER = 0x0004, DT_BOTTOM = 0x0008, DT_TOP = 0x0000;
    int w = r->right - r->left, h = r->bottom - r->top;
    if (w <= 0 || h <= 0) return 1;

    // 水平位置
    int32_t tx = r->left + 2;                       // 默认左对齐 + 2px 内边距（与旧实现一致）
    if (fmt & (DT_CENTER | DT_RIGHT)) {
        int32_t tw = 0;
        uint32_t sz[2] = {0, 0};                    // SIZE {cx, cy}
        extern int GetTextExtentPoint32W(void *, const uint16_t *, int32_t, void *);
        GetTextExtentPoint32W(hdc, s, len, sz);
        tw = (int32_t)sz[0];
        if (fmt & DT_CENTER) tx = r->left + (w - tw) / 2;
        else                 tx = r->right - tw - 2;
        if (tx < r->left) tx = r->left;             // 防越界
    }
    // 垂直位置
    int32_t ty = r->top + (h + WPF_FAKE_FONT_HEIGHT) / 2;   // 默认垂直居中（与旧实现一致）
    if (fmt & DT_VCENTER)      ty = r->top + (h + WPF_FAKE_FONT_HEIGHT) / 2;
    else if (fmt & DT_BOTTOM)  ty = r->bottom - 2;
    else if (fmt & DT_TOP)     ty = r->top + WPF_FAKE_FONT_HEIGHT;
    // 注意：旧实现用的是 `(h + FONT_HEIGHT) / 2`，这里保持**完全一致**（不 +1、不 -1）

    TextOutW(hdc, tx, ty, s, len);
    return 1;
}
int DrawTextA(void *hdc, const char *s, int32_t len, void *rect, uint32_t fmt) { (void)hdc; (void)s; (void)len; (void)rect; (void)fmt; return 1; }

int DrawTextExW(void *hdc, uint16_t *s, int32_t len, void *rect, uint32_t fmt, void *params)
{
    (void)hdc; (void)s; (void)len; (void)rect; (void)fmt; (void)params;
    return 1;   // 不绘制
}

// ── 状态设置：纯记账（无 GDI 上下文，接受即返回成功）─────────────────────────
int SetBkMode(void *hdc, int mode)
{
    wpf_draw_set_bk_mode((HDC)hdc, mode);
    return 1;
}
uint32_t SetTextColor(void *hdc, uint32_t color)
{
    wpf_draw_set_text_color((HDC)hdc, color);
    return color;      // 简化：旧色不精确回传（调用方很少用）
}
uint32_t SetBkColor(void *hdc, uint32_t color)
{
    wpf_draw_set_bk_color((HDC)hdc, color);
    return color;
}
uint32_t SetTextAlign(void *hdc, uint32_t align) { (void)hdc; (void)align; return align; }
uint32_t GetTextAlign(void *hdc) { (void)hdc; return 0; }
int SetMapMode(void *hdc, int mode) { (void)hdc; (void)mode; return 1; }
int SetGraphicsMode(void *hdc, int mode) { (void)hdc; (void)mode; return 1; }
int SetViewportOrgEx(void *hdc, int32_t x, int32_t y, void *old) { (void)hdc; (void)x; (void)y; (void)old; return 1; }
int SetWindowOrgEx(void *hdc, int32_t x, int32_t y, void *old) { (void)hdc; (void)x; (void)y; (void)old; return 1; }
int SetBkModeEx(void *hdc, int mode) { return SetBkMode(hdc, mode); }

// ── 控制台信号（`Interop.Kernel32.SetConsoleCtrlHandler`）────────────────────
//   .NET 的 `ConsoleCtrlHandler` 用它注册 Ctrl+C 处理。Linux 上由 SIGINT 承担，
//   这里返回 TRUE 表示"已登记"，但不真的挂信号处理（.NET 另外有 SIGINT 的路径）。
int SetConsoleCtrlHandler(void *handler, int add)
{
    (void)handler; (void)add;
    return 1;
}

// ── UIAutomationCore（`System.Windows.Forms` 的 `Control.set_Text` 会查有没有 UIA 客户端）──
//   `UiaClientsAreListening()` 在 Windows 上问 UIA 是否有人在监听；Linux 上**永远没有** UI Automation 客户端，
//   返回 FALSE 即可让 WinForms 跳过 UIA 通知。
//   实测：不给这个，`Form.Text = "..."` 就在 `TextBoxBase.OnTextChanged → UiaClientsAreListening` 抛
//   `DllNotFoundException: UIAutomationCore.dll` —— 而它被 `ThreadException` 接住后又被
//   `ThreadExceptionDialog` 的图标问题二次掩盖，绕了三层才看到（K-023）。
// 注：`UiaClientsAreListening` 已在 win32_oem.c 实现（重复定义会链接失败）
// ── 【K-611】真正的 `UiaReturnRawElementProvider` ─────────────────────────────
// 托管签名（`UiaCoreProviderApi.cs:31`）：
//   internal static IntPtr UiaReturnRawElementProvider(
//       IntPtr hwnd, IntPtr wParam, IntPtr lParam, IRawElementProviderSimple el)
// `el` 封送后是 `IUnknown*`，可以读 vtable 拿 `IRawElementProviderSimple` 的 4 个方法。
// 我们：
//   ① 把 `hwnd → el` 存进全局表（后续 AT-SPI 桥接用）
//   ② 用 vtable 读 `UIA_NamePropertyId (30005)` 验证能拿到控件名
//   ③ 返回 `el`（非 0，让 WPF 认为成功）
#define WPF_UIA_MAX_PROVIDERS 256
static struct { void *hwnd; void *el; } g_uia_providers[WPF_UIA_MAX_PROVIDERS];
static int g_uia_provider_count = 0;

typedef struct {
    long (*QueryInterface)(void *, const void *, void **);
    unsigned long (*AddRef)(void *);
    unsigned long (*Release)(void *);
    long (*get_ProviderOptions)(void *, int *);
    long (*GetPatternProvider)(void *, int, void **);
    long (*GetPropertyValue)(void *, int, void *);   /* VARIANT* */
    long (*get_HostRawElementProvider)(void *, void **);
} wpf_uia_provider_vtbl;

/* VARIANT 最小布局（x86_64）：vt(2) + wReserved(2) + wReserved1..3 + data(8/16) */
typedef struct { unsigned short vt; unsigned short r1, r2, r3; unsigned long long data; } wpf_variant_min;

// ── 【K-937】`wpf_uia_register_provider` —— 托管侧通过 `GCHandle` 传 `IRawElementProviderSimple`
//   为什么需要它：`UiaReturnRawElementProvider` 的第 4 参数是 **COM 接口**
//   （`IRawElementProviderSimple`），.NET 在 Linux 上**不支持 COM marshaling**
//   （`MarshalDirectiveException`，K-936）⇒ 托管侧改用 `GCHandle.Alloc(el)` 得到 `IntPtr`
//   后调本函数（`intptr_t` 参数，无 marshaling 问题）。
//   AT-SPI 桥接后续可用 `GCHandle.FromIntPtr` 取回对象（需托管侧支持）。
intptr_t wpf_uia_register_provider(void *hwnd, intptr_t gcHandle)
{
    /* 【K-940】诊断必须在 `if (gcHandle == 0)` **之前**（教训：K-921/K-940 同类错误） */
    if (getenv("WPF_LINUX_UIA_DIAG")) {
        fprintf(stderr, "[UIA] REGISTER hwnd=%p gcHandle=0x%lx\n", hwnd, (long)gcHandle);
        fflush(stderr);
    }
    if (gcHandle == 0) return 0;
    if (g_uia_provider_count < WPF_UIA_MAX_PROVIDERS) {
        g_uia_providers[g_uia_provider_count].hwnd = hwnd;
        g_uia_providers[g_uia_provider_count].el = (void *)gcHandle;
        ++g_uia_provider_count;
        return 1;
    }
    return 0;
}

intptr_t UiaReturnRawElementProvider(void *hwnd, intptr_t wParam, intptr_t lParam, void *el)
{
    (void)wParam; (void)lParam;
    /* 【K-922】诊断必须在 `if (!el)` **之前** —— K-921 发现 `el == null` 时静默返回 0
     *   ⇒ 无法区分「函数未被调用」与「el 为 null」。 */
    if (getenv("WPF_LINUX_UIA_DIAG")) {
        fprintf(stderr, "[UIA] CALLED hwnd=%p el=%p\n", hwnd, el);
        fflush(stderr);
    }
    if (!el) return 0;
    if (g_uia_provider_count < WPF_UIA_MAX_PROVIDERS) {
        g_uia_providers[g_uia_provider_count].hwnd = hwnd;
        g_uia_providers[g_uia_provider_count].el = el;
        ++g_uia_provider_count;
    }
    /* 【K-949】`el` 现在是 `GCHandle`（K-948 的方案，非 COM 指针）⇒ **禁止读 vtable**
     *   （否则 `*(void**)el` 是野指针 ⇒ 崩溃）。用 `WPF_LINUX_UIA_GCHANDLE=1` 或默认跳过。 */
    int el_is_gchandle = (getenv("WPF_LINUX_UIA_GCHANDLE") != NULL) || 1;   /* 默认跳过 */
    if (getenv("WPF_LINUX_UIA_DIAG") && !el_is_gchandle) {
        wpf_uia_provider_vtbl *vt = *(wpf_uia_provider_vtbl **)el;
        if (vt && vt->AddRef) vt->AddRef(el);
        const char *name = "(no-vtable)";
        char namebuf[256] = { 0 };
        if (vt && vt->GetPropertyValue) {
            wpf_variant_min v; memset(&v, 0, sizeof(v));
            if (vt->GetPropertyValue(el, 30005 /* UIA_NamePropertyId */, &v) == 0 && v.data) {
                /* vt=8 (VT_BSTR) 时 data 是 BSTR（UTF-16 指针） */
                if (v.vt == 8) {
                    unsigned short *w = (unsigned short *)v.data;
                    int k = 0;
                    while (w[k] && k < 255) { namebuf[k] = (char)(w[k] & 0x7F); ++k; }
                    namebuf[k] = 0;
                    name = namebuf;
                }
            }
        }
        fprintf(stderr, "[UIA] hwnd=%p el=%p vt=%p name=\"%s\"\n", hwnd, el, (void *)vt, name);
        fflush(stderr);
        if (vt && vt->Release) vt->Release(el);
    }
    return (intptr_t)el;
}
int UiaHostProviderFromHwnd(void *hwnd, void *ppProvider) { (void)hwnd; (void)ppProvider; return 0; }
void UiaRaiseAutomationEvent(void *pProvider, int id) { (void)pProvider; (void)id; }
void UiaRaiseStructureChangedEvent(void *pProvider, int structChangeType, void *pStructure) { (void)pProvider; (void)structChangeType; (void)pStructure; }
void UiaRaiseAutomationPropertyChangedEvent(void *pProvider, int id, void *oldVal, void *newVal) { (void)pProvider; (void)id; (void)oldVal; (void)newVal; }

// ── 窗口类查询（`NativeWindow.WindowClass.RegisterClass` 在注册前后会查一次）──────
//   我们的窗口类注册表就是进程内的（见 win32_core.c 的 `RegisterClassExW`），
//   这里按类名查表，把注册时存的字段填回 `WNDCLASSW`。
// 【ACC-028 / K-024】`GetClassInfo` 必须对**系统窗口类**返回成功。
//   WinForms 的 `NativeWindow.WindowClass.RegisterClass()` 里：
//       if (!PInvoke.GetClassInfo(HINSTANCE.Null, n, &windowClass))
//           throw new Win32Exception(Marshal.GetLastWin32Error(), SR.InvalidWndClsName);
//   `SR.InvalidWndClsName` 的文案正是 **"Window class name is not valid."** —— 之前一直查不到来源，
//   就是因为它在**资源**里而不在源码里（K-024）。
//   Linux 上没有"系统窗口类表"，但这里**必须**表现得像有：填一个最小可用的 `WNDCLASSW`
//   （`lpfnWndProc = DefWindowProcW`），否则 WinForms 连控件都构造不出来。
//   端口：`WNDCLASSW` 的字段顺序与本文件前面定义的 `WPF_WNDCLASS_D` 一致。
typedef struct {
    uint32_t  style;
    void     *lpfnWndProc;
    int32_t   cbClsExtra;
    int32_t   cbWndExtra;
    void     *hInstance;
    void     *hIcon;
    void     *hCursor;
    void     *hbrBackground;
    uint16_t *lpszMenuName;
    uint16_t *lpszClassName;
} WPF_WNDCLASS_OUT;

int GetClassInfoW(void *hInstance, const uint16_t *className, void *wc)
{
    (void)hInstance; (void)className;
    if (!wc) return 0;
    WPF_WNDCLASS_OUT *c = (WPF_WNDCLASS_OUT *)wc;
    c->style         = 0;
    c->lpfnWndProc   = (void *)&DefWindowProcW;   // 关键：非空，WinForms 会把它当默认窗口过程
    c->cbClsExtra    = 0;
    c->cbWndExtra    = 0;
    c->hInstance     = 0;
    c->hIcon         = 0;
    c->hCursor       = 0;
    c->hbrBackground = 0;
    c->lpszMenuName  = 0;
    c->lpszClassName = (uint16_t *)className;
    return 1;   // 成功
}
int GetClassInfoA(void *hInstance, const char *className, void *wc) { return GetClassInfoW(hInstance, (const uint16_t *)className, wc); }

// ── 消息循环阻塞点（`Application.RunMessageLoop` 的核心）──────────────────────
//   WinForms 的 `LightThreadContext.FPushMessageLoop` 在队列空时调 `WaitMessage()` 阻塞。
//   **不能**简单地 return TRUE —— 那会让消息循环变成忙等（一个核心 100%）。
//   正确做法：真的等一次 X 事件。本 shim 的窗口层已有 `wpf_msg_wait`（见 win32_msg.c），
//   这里复用它（返回 TRUE 表示"可能来消息了"，语义与 Win32 一致）。
int WaitMessage(void)
{
    // ⚠️ 当前是**近似实现**：睡 1ms 后返回，而不是真的阻塞在 X 事件上。
    //   目的只是避免消息循环 100% 占一个核；**延迟表现与真机不同**，这一点如实记录在这里。
    //   （真等待需要接到 X 连接上，见 TODO；本轮先让它能跑。）
    struct timespec ts = { 0, 1000000 };   // 1ms
    nanosleep(&ts, NULL);
    return 1;
}

// ── 进程启动信息（`GetStartupInfoW`）─────────────────────────────────────────
//   WinForms 在 `ThreadContext` 初始化时会读 `STARTUPINFO`（主要看 `wShowWindow`/`dwFlags`）。
//   Linux 上没有 Windows 的"启动显示方式"，返回一个**已清零 + cb 正确**的结构即可。
//   实测：不补它，WinForms 的 `Application.Run` 抛
//   `EntryPointNotFoundException: GetStartupInfoW`，而它被 `ThreadException` 接住后
//   弹了「Unhandled exception has occurred…」对话框（对话框标题里带着这条消息，见 K-025）。
typedef struct {
    uint32_t  cb;
    uint16_t *lpReserved, *lpDesktop, *lpTitle;
    uint32_t  dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    uint16_t  wShowWindow, cbReserved2;
    uint8_t  *lpReserved2;
    void     *hStdInput, *hStdOutput, *hStdError;
} WPF_STARTUPINFOW;

void GetStartupInfoW(WPF_STARTUPINFOW *si)
{
    if (!si) return;
    memset(si, 0, sizeof(*si));
    si->cb = (uint32_t)sizeof(*si);
    // dwFlags = 0 ⇒ wShowWindow **无效**；WinForms 会退回到默认的"正常显示"。
    // 这比假装 STARTF_USESHOWWINDOW + SW_SHOWNORMAL 更诚实（我们没有那个来源）。
}
void GetStartupInfoA(void *si) { GetStartupInfoW((WPF_STARTUPINFOW *)si); }

// ── OLE / COM 初始化（`OleInitialize`）──────────────────────────────────────
//   WinForms 的 `ThreadContext` 会 `OleInitialize()` 一次（剪贴板/拖放要用 OLE）。
//   Linux 上没有 COM/OLE 运行时；本 shim 的剪贴板与拖放走 X11（见 win32_clipboard.c / XDND）。
//   口径：**返回 S_OK（假成功）** 而不是失败 —— 因为"没有 OLE"在这里不是错误，
//   后续真正用 OLE 的地方（`IDropTarget` 等）会各自走本仓的 X11 实现或如实失败。
//   实测：不补它，`Application.Run` 抛 `EntryPointNotFoundException: OleInitialize`。
// 注：OleInitialize/OleUninitialize 已在 win32_oem.c 实现（重复定义会链接失败）

// ── `OleInitialize`（**注意：`CoInitialize` 系列在 win32_oem.c 里已有，别重复**）──────
//   `exports.txt` 里 `OleInitialize` 一直是 `gap`（"无实现（ole32）"），而 WinForms 的
//   `ThreadContext` 初始化会调它（剪贴板/拖放走 OLE 的那条路径）。
//   Linux 上没有 COM/OLE 运行时；本仓的剪贴板与拖放走 X11（win32_clipboard.c / XDND）。
//   口径：**返回 S_OK（假成功）**，而不是失败 —— 「没有 OLE」不是错误，
//   真要用 OLE 的地方会各自走 X11 实现或如实失败。
//   实测：不补它，`Application.Run` 抛 `EntryPointNotFoundException: OleInitialize`，
//   被 `ThreadException` 接住后弹出「Unhandled exception…」对话框（标题里带这条消息，见 K-025）。
int32_t OleInitialize(void *reserved) { (void)reserved; return 0; }   // S_OK
// 【清算 / K-170】**真实现**（原来是 `return 0` 空壳）。
//   `OleUninitialize` 的语义就是释放 OLE/COM 单元 —— 本 shim 的 COM 单元由
//   `CoInitialize`/`CoInitializeEx`（`win32_oem.c`，返回 S_OK）与 `CoUninitialize` 维护
//   （当前它们是空实现，因为没有真正的 COM 单元）。这里如实转发，不再假装"做了事"。
int32_t OleUninitialize(void)
{
    extern void CoUninitialize(void);
    CoUninitialize();
    return 0;   /* S_OK */
}

// ── `GetHdc` / `ReleaseHdc`（GDI32 的"设备上下文内容"转换）─────────────────────
//   WinForms 的 `IHdcContext.GetHdc()`（CsWin32）走的是 **GDI32 的 `GetHdc`**，
//   **不是** `user32` 的 `GetDC` —— 两者名字像但不是一个 DLL。
//   本 shim 一直只有 `GetDC`/`ReleaseDC`，所以 `ButtonBaseAdapter.ColorOptions.Calculate()`
//   → `DeviceContextExtensions.ToHdcScope` → `IHdcContext.GetHdc()` 就抛
//   `EntryPointNotFoundException`（消息里**不带函数名**，因此极难定位，见 K-026）。
//   语义：`GetHdc(hdc)` 返回该 DC 的内容句柄；我们本来就是同一个不透明句柄，原样返回。
void *GetHdc(void *hdc) { return hdc; }
void *GetHdcA(void *hdc) { return hdc; }
void *GetHdcW(void *hdc) { return hdc; }
int  ReleaseHdc(void *hdc) { (void)hdc; return 1; }
int  ReleaseHdcA(void *hdc) { (void)hdc; return 1; }
int  ReleaseHdcW(void *hdc) { (void)hdc; return 1; }

// ── 【K-311】`GetNearestColor` ────────────────────────────────────────────────
//   WinForms 的 `Control.PaintBackColor` 每次都调它：
//     `DeviceContextExtensions.FindNearestColor(hdc, color)` → `PInvoke::GetNearestColor`
//   语义：把一个 32 位 COLORREF 映射到设备能显示的"最接近颜色"。
//
//   Linux 上的取舍：**直通返回原值**。理由：
//     ① X11 的真彩色视觉（TrueColor，24/32 位）能显示任意 RGB ⇒ 没有"最近色"可降级；
//     ② 这是纯查询函数，不做绘制，直通不影响正确性；
//     ③ 如果将来要支持 8 位伪彩色视觉，再在这里做量化。
//   参数 `hdc` 可能是我们的 `GpGraphics*` 假句柄（K-292 已确认 `FillRect` 就是这么用的），
//   本函数**不看 hdc 内容**，与 `FillRect` 的处置一致。
uint32_t GetNearestColor(void *hdc, uint32_t color)
{
    (void)hdc;
    return color;
}

// ── 【K-313】`GetSysColorBrush` ──────────────────────────────────────────────
//   WinForms 画控件背景时会调 `GetSysColorBrush(COLOR_BTNFACE)` 之类拿系统颜色画刷，
//   再传给 `FillRect`。Linux 上没有"系统画刷"，但我们有 GDI 对象表：
//     `wpf_gdi_alloc(kind, color, width, style)` 建对象，`wpf_gdi_of(handle)` 查表，
//     `wpf_gdi_color(handle, dflt)` 取颜色 —— `FillRect` 就是这么取色的（K-313）。
//
//   **颜色查 `GetSysColor(index)`**（已在 `win32_core.c:2484` 实现，31 个 COLOR_* 索引，
//   Win10 默认调色板）—— **不要在这里重复定义 `GetSysColor`**（K-315 的链接错误就是它）。
//
//   同一索引必须返回**同一句柄**（WinForms 会 cache 并比较），所以用静态数组缓存。
extern int GetSysColor(int index);          // 定义在 win32_core.c

void *GetSysColorBrush(int nIndex)
{
    static void *cache[32];
    if (nIndex < 0 || nIndex >= 32) nIndex = 0;
    if (!cache[nIndex]) {
        cache[nIndex] = wpf_gdi_alloc(/*kind=brush*/1, (uint32_t)GetSysColor(nIndex), 0, 0);
    }
    return cache[nIndex];
}

// ── 【K-315】`CreatePen` / `CreatePenIndirect` / `CreateSolidBrush` 补缺 ───────
//   WinForms 画控件边框时会 `CreatePen` 再 `SelectObject` 到 DC。
//   与 `GetSysColorBrush` 同构：建一个 GDI 对象并登记到表里，各绘制函数按表取属性。
//   `kind`：1=brush、2=pen（见 `win32_gdi_dc.c` 的 `wpf_gdi_obj`）。
void *CreatePen(int iStyle, int cWidth, uint32_t color)
{
    return wpf_gdi_alloc(/*kind=pen*/2, color, cWidth > 0 ? cWidth : 1, iStyle);
}

void *CreatePenIndirect(const void *lplgpn)
{
    // LOGPEN 结构：lopnStyle(4) + POINT lopnWidth(8) + COLORREF lopnColor(4)
    const unsigned char *p = (const unsigned char *)lplgpn;
    if (!p) return NULL;
    int style = (int)*(const int32_t *)p;
    int width = (int)*(const int32_t *)(p + 8);
    uint32_t color = *(const uint32_t *)(p + 16);
    return CreatePen(style, width, color);
}

// 注意：`CreateSolidBrush` 已在 `win32_misc.c` 实现，**不要重复定义**（K-316 的链接错误就是它）。

// ── 【K-316】`SetROP2` / `GetROP2` ───────────────────────────────────────────
//   光栅操作模式（`R2_COPYPEN`=13 / `R2_BLACK`=1 / `R2_WHITE`=16 等）。
//   它只决定"源像素与目标像素怎么合成"，不涉及 GDI 对象表。
//   本仓的绘制最终都落到 X11 的 `XFillRectangle`/`XDrawLine`（**复制语义**，等价 `R2_COPYPEN`），
//   所以这里只**按 DC 记账并返回旧值**，不改变实际绘制行为。
//   ⚠️ 如果将来实现 `R2_XORPEN` 之类，再在这里分派到 X11 的 `GXxor`。
static int g_last_rop2 = 13;      // R2_COPYPEN（Windows 默认）

int SetROP2(void *hdc, int mode)
{
    (void)hdc;
    int old = g_last_rop2;
    g_last_rop2 = mode;
    return old;
}

int GetROP2(void *hdc)
{
    (void)hdc;
    return g_last_rop2;
}

// ── 【K-317】`MoveToEx` / `LineTo` ───────────────────────────────────────────
//   WinForms 画边框/分隔线时会 `MoveToEx` 设起点、再 `LineTo` 画到终点。
//   本仓已有 `wpf_draw_line(hdc, x1,y1, x2,y2, color)`（`win32_gdi_dc.c:292`，落到 X11 `XDrawLine`）
//   ⇒ 这两个可以**真实现**，不用桩。
//
//   画笔位置按调用记账（单线程 UI 场景足够；多线程绘制不在 WinForms 的常规路径上）。
static int32_t g_pen_x = 0, g_pen_y = 0;

int MoveToEx(void *hdc, int32_t x, int32_t y, void *old)
{
    (void)hdc;
    if (old) {                                  // POINT { LONG x; LONG y; }
        int32_t *p = (int32_t *)old;
        p[0] = g_pen_x;
        p[1] = g_pen_y;
    }
    g_pen_x = x;
    g_pen_y = y;
    return 1;
}

int LineTo(void *hdc, int32_t x, int32_t y)
{
    // 画笔颜色：本仓暂无"DC 当前画笔"的查询接口 ⇒ 用黑色（WinForms 默认边框色）。
    // ⚠️ 这是**已知的简化**：如果将来需要按 `SelectObject(HPEN)` 的真颜色画，
    //   应给 `wpf_hdc` 加 `pen_color` 字段并在 `SelectObject` 时更新。
    wpf_draw_line((HDC)hdc, g_pen_x, g_pen_y, x, y, 0x000000u);
    g_pen_x = x;
    g_pen_y = y;
    return 1;
}

// `SetPixelV(HDC, x, y, COLORREF) -> BOOL`：单点绘制（1x1 的 FillRect 即可）
int SetPixelV(void *hdc, int32_t x, int32_t y, uint32_t color)
{
    WPF_RECT rc;
    rc.left = x; rc.top = y; rc.right = x + 1; rc.bottom = y + 1;
    wpf_draw_fill_rect((HDC)hdc, &rc, color & 0x00FFFFFFu);
    return 1;
}

// ── 【K-321】`DrawFrameControl` ──────────────────────────────────────────────
//   `BOOL DrawFrameControl(HDC hdc, RECT *lprc, UINT uType, UINT uState)`
//   WinForms 的 `ControlPaint.DrawBorder3D` / 按钮边框会调它（USER32 导出）。
//
//   本仓的实现取舍：**画 2px 的凹陷/凸起边框**（用 `wpf_draw_fill_rect` 拼 4 条边）。
//   `uType` 的常见值（DFC_BUTTON=4 / DFC_CAPTION=1 / DFC_MENU=2 / DFC_SCROLL=3）
//   在本仓没有主题引擎差异，统一按"浅灰底 + 深灰边"处理 —— 与 `GetSysColor` 的调色板一致。
//   不猜 `uState` 的位组合，只区分"按下"（`DFCS_PUSHED=0x0200`）与"正常"两档颜色。
int DrawFrameControl(void *hdc, const WPF_RECT *lprc, unsigned int uType, unsigned int uState)
{
    // 【K-364】从「只区分 pushed」升级为支持 `DFCS_*` 的主要状态位。
    //   参考 Win32 的 `DFCS_*` 常量（`winuser.h`）：
    //     DFCS_BUTTONCHECK 0x0000 / DFCS_BUTTONPUSH 0x0010 / DFCS_BUTTONRADIO 0x0004
    //     DFCS_CHECKED     0x0400 / DFCS_INACTIVE    0x0100 / DFCS_PUSHED     0x0200
    //     DFCS_FLAT        0x4000 / DFCS_MONO        0x8000
    //   本仓的取舍（诚实）：**只画"凹陷/凸起边框 + 选中标记"**，不画完整主题外观。
    //   `DFCS_CHECKED` 用一个小方块（而不是对勾）—— 因为本仓没有矢量路径绘制能力。
    if (!lprc) return 0;
    int pushed   = (uState & 0x0200u) != 0;    // DFCS_PUSHED
    int inactive = (uState & 0x0100u) != 0;    // DFCS_INACTIVE
    int checked  = (uState & 0x0400u) != 0;    // DFCS_CHECKED
    int flat     = (uState & 0x4000u) != 0;    // DFCS_FLAT
    uint32_t dark  = pushed ? 0xFFFFFFu : 0xA0A0A0u;
    uint32_t light = pushed ? 0xA0A0A0u : 0xFFFFFFu;
    if (inactive) { dark = 0xC8C8C8u; light = 0xF0F0F0u; }   // 灰化：边框对比度降低
    if (flat)     { dark = 0xD0D0D0u; light = 0xE8E8E8u; }   // 平面：几乎无对比

    WPF_RECT r = *lprc;
    WPF_RECT top, bot, lft, rgt;
    // 上边（2px）
    top.left = r.left; top.top = r.top; top.right = r.right; top.bottom = r.top + 2;
    // 左边（2px）
    lft.left = r.left; lft.top = r.top; lft.right = r.left + 2; lft.bottom = r.bottom;
    // 下边（2px）
    bot.left = r.left; bot.top = r.bottom - 2; bot.right = r.right; bot.bottom = r.bottom;
    // 右边（2px）
    rgt.left = r.right - 2; rgt.top = r.top; rgt.right = r.right; rgt.bottom = r.bottom;

    wpf_draw_fill_rect((HDC)hdc, &top, dark);
    wpf_draw_fill_rect((HDC)hdc, &lft, dark);
    wpf_draw_fill_rect((HDC)hdc, &bot, light);
    wpf_draw_fill_rect((HDC)hdc, &rgt, light);

    // 选中标记：在中心画一个小方块（Win32 的 DFCS_CHECKED 是对勾/圆点，本仓用方块代替）
    if (checked) {
        int w = r.right - r.left, h = r.bottom - r.top;
        int m = (w < h ? w : h) / 3;            // 边长 = 短边的 1/3
        if (m < 2) m = 2;
        WPF_RECT ck;
        ck.left   = r.left + (w - m) / 2;
        ck.top    = r.top  + (h - m) / 2;
        ck.right  = ck.left + m;
        ck.bottom = ck.top  + m;
        wpf_draw_fill_rect((HDC)hdc, &ck, 0x000000u);   // 黑色方块（与 BTNTEXT 一致）
    }
    return 1;
}
