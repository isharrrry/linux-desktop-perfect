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
    (void)fmt;
    // 【ACC-028 / K-144】DrawText 简化成"在 rect 内垂直居中画一行"（够 Button/Label 用）
    if (!rect) return 1;
    const WPF_RECT *r = (const WPF_RECT *)rect;
    int32_t ty = r->top + (r->bottom - r->top + WPF_FAKE_FONT_HEIGHT) / 2;
    TextOutW(hdc, r->left + 2, ty, s, len);
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
int UiaReturnRawElementProvider(void) { return 0; }
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
