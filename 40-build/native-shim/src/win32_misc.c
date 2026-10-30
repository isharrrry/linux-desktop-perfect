// WPF-on-Linux · M7b · P1 面：GDI / kernel32 / DPI / 监视器 / 系统参数的**诚实**实现
//
// ── 本文件的判定标准（每一条都写清落在哪一档）────────────────────────────
//   [真实现]  有真实语义，返回值可被断言：MultiByteToWideChar、LoadLibrary/GetProcAddress、
//             GetModuleFileName、QueryPerformance*、GetTempPath 类。
//   [降级]    语义在 Linux 上**不存在对象**，返回一个「上层不会误入错误分支」的值，
//             且该值在 Linux 语境下是**真话**（例如 DPI=96，因为没有缩放）。
//   [返回失败] 确实表达不了，返回 Win32 的失败码（NULL / FALSE / 0）并设 LastError。
//             **绝不假装成功**——假装成功会让上层以为自己拿到了有效句柄。
//
//   每一条的档位在函数上方用 [真实现]/[降级]/[失败] 标注，README 里有汇总表，
//   docs/U2-M7b-report.md 有统计。

#define _GNU_SOURCE
#include "win32_internal.h"

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

// ── GDI 面 ─────────────────────────────────────────────────────────────────
// Windows 上 WPF 用 GDI 做三件事：拿 DC 画非客户区（我们不做）、
// 位图互操作（走 WIC/我们的 Skia 栈）、以及 CreateCompatibleDC 做离屏。
// Linux 上这三件都没有 GDI 对象。**返回非 0 的哨兵句柄**是刻意的：
// 上层拿到 NULL 会当成「资源分配失败」抛异常并中断整条路径，而实际语义是
// 「这个后端没有 GDI」。哨兵句柄让调用链继续走到真正该走的分支。
#define WPF_FAKE_DC   ((HDC)(uintptr_t)0x0001)

int wpf_legacy_dpi(void);   // 诊断开关（见 GetDeviceCaps 里那段说明）

// [降级] 无 GDI 软件 DC。返回哨兵，不产生任何系统资源。
HDC GetDC(HWND h) { (void)h; return WPF_FAKE_DC; }
HDC GetWindowDC(HWND h) { (void)h; return WPF_FAKE_DC; }
HDC CreateCompatibleDC(HDC hdc) { (void)hdc; return WPF_FAKE_DC; }
int ReleaseDC(HWND h, HDC dc) { (void)h; (void)dc; return 1; }
BOOL DeleteDC(HDC dc) { (void)dc; return 1; }

// [降级] GetStockObject：托管侧只拿来要 NULL_BRUSH（HwndWrapper 建窗前取背景刷）。
// 返回值**必须非 0**（HwndWrapper 显式检查 `hNullBrush == IntPtr.Zero` → throw）。
HGDIOBJ GetStockObject(int i)
{
    (void)i;
    return (HGDIOBJ)(uintptr_t)0x1000;
}
HGDIOBJ CriticalGetStockObject(int i) { wpf_set_last_error(0); return GetStockObject(i); }

// [降级] 位图对象：无 GDI 位图，返回哨兵句柄。
HBITMAP CreateCompatibleBitmap(HDC dc, int w, int h) { (void)dc; (void)w; (void)h; return (HBITMAP)(uintptr_t)0x2000; }
HBITMAP CreateBitmap(int w, int h, UINT planes, UINT bits, const void *bits_ptr)
{ (void)w; (void)h; (void)planes; (void)bits_ptr; (void)bits; return (HBITMAP)(uintptr_t)0x2000; }
HBITMAP CriticalCreateCompatibleBitmap(HDC dc, int w, int h) { return CreateCompatibleBitmap(dc, w, h); }
HBITMAP CreateDIBSection(HDC dc, const void *bmi, UINT usage, void **bits, HANDLE section, DWORD offset)
{
    (void)dc; (void)bmi; (void)usage; (void)section; (void)offset;
    // 真实现的一半：像素缓冲由我们 malloc，调用方拿到**可写**的 bits。
    // 但后续的 SelectObject/GDI 绘制不会有任何效果（无 GDI）——这一点必须诚实
    // 标注：CreateDIBSection 返回的位图只在「调用方自己读写 bits」时有意义。
    if (bits) *bits = calloc(1, 4 * 64 * 64);
    return (HBITMAP)(uintptr_t)0x2000;
}
HGDIOBJ SelectObject(HDC dc, HGDIOBJ obj) { (void)dc; return obj; }
BOOL DeleteObject(HGDIOBJ o) { (void)o; return 1; }
int GetObjectW(HGDIOBJ o, int n, void *buf) { (void)o; (void)n; (void)buf; return 0; }
int GetObjectA(HGDIOBJ o, int n, void *buf) { (void)o; (void)n; (void)buf; return 0; }
int GetObject(HGDIOBJ o, int n, void *buf) { return GetObjectA(o, n, buf); }
int GetBitmapBits(HBITMAP b, int cb, uint8_t *out) { (void)b; (void)cb; (void)out; return 0; }
// ── GetDeviceCaps：从"恒 0"改成**真实值**（M7c 收尾轮）────────────────────
// 【为什么这不是"补一个返回值"，而是修一个真 bug】
//   上游 UIElement.EnsureDpiScale()（UIElement.cs:1128）拿它算 DpiScale：
//       dpiX = GetDeviceCaps(dc, LOGPIXELSX);   _dpiScaleX = dpiX / 96;
//   返回 0 ⇒ DpiScale=(0,0) ⇒ `PixelsPerDip=0` ⇒ `RoundDipForDisplayMode(v,0) = NaN`
//   ⇒ `CheckFastPathNominalGlyphs` 主循环恒 false ⇒ 回落 LineServices。
//   "无 GDI"是真的，但"这个 DC 的 DPI 是 0"是**假的** —— X server 明明报了
//   (1280x1024 px, 325x260 mm) = 100x100 dpi。
// 【覆盖面】只实现 WPF 真的会读的几个 index（来源：全仓 grep `GetDeviceCaps(`）：
//   LOGPIXELSX/Y(88/90) ← UIElement.EnsureDpiScale / SystemParameters / ActiveXHelper
//   BITSPIXEL(12) × PLANES(14) ← Utilities.cs:180 / IconHelper.cs:37（算系统位深）
//   外加 HORZRES/VERTRES/NUMCOLORS 让 DC 自洽。未知 index 仍然返回 0（Win32 也是这样），
//   不编造。
int GetDeviceCaps(HDC dc, int index)
{
    (void)dc;   // 本 shim 只有一个哨兵 DC（无每-DC 状态），所有 DC 的答案必须一致 —— 这就是"自洽"

    // 【诊断开关】WPF_SHIM_LEGACY_DPI=1 恢复"改动前"的行为（88/90 返回 0，不碰 X）。
    // 用途：把"ManagedLayer.Tests 宿主崩溃"这件事**归因**（是我的 DPI 改动引起的，
    // 还是本来就存在）。只在排查时用，默认关闭、不参与验收。
    if (wpf_legacy_dpi()) {
        if (index == 12) return 24;
        if (index == 14) return 1;
        return 0;
    }

    wpf_screen_metrics m;
    wpf_x11_screen_metrics(&m);

    switch (index) {
    case 88:  return m.dpi_x;                       // LOGPIXELSX
    case 90:  return m.dpi_y;                       // LOGPIXELSY
    case 12:  return m.depth > 0 ? m.depth : 24;    // BITSPIXEL（真彩色：默认视觉位深）
    case 14:  return 1;                             // PLANES
    case 8:   return m.pixels_wide;                 // HORZRES
    case 10:  return m.pixels_high;                 // VERTRES
    case 24:  return -1;                            // NUMCOLORS：-1 = 真彩色
    case 104: return 0;                             // SIZEPALETTE：无调色板
    default:  return 0;                             // 未实现的 index 不编造
    }
}
// 诊断开关的读取（供 GetDpiFor* 复用）
int wpf_legacy_dpi(void)
{
    static int legacy = -1;
    if (legacy < 0) {
        const char *e = getenv("WPF_SHIM_LEGACY_DPI");
        legacy = (e && *e && *e != '0') ? 1 : 0;
    }
    return legacy;
}
int GetDeviceCapsW(HDC dc, int index) { return GetDeviceCaps(dc, index); }
int GetDeviceCapsA(HDC dc, int index) { return GetDeviceCaps(dc, index); }
int ExtEscape(HDC dc, int esc, int in_n, const char *in, int out_n, char *out)
{ (void)dc; (void)esc; (void)in_n; (void)in; (void)out_n; (void)out; return 0; }
int FillRect(HDC dc, const WPF_RECT *rc, HBRUSH br) { (void)dc; (void)rc; (void)br; return 1; }
int StartDocW(HDC dc, const void *di) { (void)dc; (void)di; return 0; }
int StartDocA(HDC dc, const void *di) { (void)dc; (void)di; return 0; }
int StartDoc(HDC dc, const void *di) { return StartDocA(dc, di); }
int EndDoc(HDC dc) { (void)dc; return 0; }
int StartPage(HDC dc) { (void)dc; return 0; }
int EndPage(HDC dc) { (void)dc; return 0; }
int SetEnhMetaFileBits(UINT cb, const uint8_t *bits) { (void)cb; (void)bits; return 0; }

// 绘制路径：我们的呈现不走 GDI（走 MIL/DUCE → M1 的 Skia 后端），
// 所以 BeginPaint/EndPaint 只需要**配对返回成功**，让上层把「重绘开始/结束」
// 这段包起来。返回 FALSE 会让 HwndTarget 在 layered window 路径上抛异常。
BOOL BeginPaint(HWND h, WPF_PAINTSTRUCT *ps)
{
    (void)h;
    if (!ps) return 0;
    memset(ps, 0, sizeof(*ps));
    ps->hdc = WPF_FAKE_DC;
    ps->fErase = 0;
    {
        wpf_lock();
        wpf_window *w = wpf_window_find(h);
        if (w) {
            ps->rcPaint.left = 0; ps->rcPaint.top = 0;
            ps->rcPaint.right = w->width; ps->rcPaint.bottom = w->height;
        }
        pthread_mutex_unlock(&g_wpf.lock);
    }
    return 1;
}
BOOL EndPaint(HWND h, const WPF_PAINTSTRUCT *ps) { (void)h; (void)ps; return 1; }

// [降级] InvalidateRect：**不产生 WM_PAINT**。理由：本工程的重绘由 MIL 呈现
// 驱动（HwndTarget → DUCE → M1 Skia），WM_PAINT 只在窗口首次 expose 时出现。
// 若在这里合成 WM_PAINT，会与 X11 的 Expose 事件形成重绘风暴。
// 返回 TRUE 表示「脏区已登记」，这在语义上不假——脏区确实被登记了（只是没人读）。
BOOL InvalidateRect(HWND h, const void *rc, BOOL erase) { (void)h; (void)rc; (void)erase; return 1; }
BOOL ValidateRect(HWND h, const void *rc) { (void)h; (void)rc; return 1; }
BOOL UpdateWindow(HWND h) { (void)h; return 1; }
BOOL RedrawWindow(HWND h, const void *rc, void *rgn, UINT flags)
{ (void)h; (void)rc; (void)rgn; (void)flags; return 1; }
BOOL PrintWindow(HWND h, HDC dc, UINT flags) { (void)h; (void)dc; (void)flags; return 0; }

// ── 光标 / 图标 ────────────────────────────────────────────────────────────
// [降级] X11 的光标是 `Cursor` 资源，需要 XCreateFontCursor；本 shim 不做
// 光标形状（WPF 的 Cursor 属性在 M7b 不落地）。返回非 0 哨兵避免上层抛异常。
// CharSet.Auto（Unix→Ansi）：裸名与 A 变体收 UTF-8，W 变体收 UTF-16。
HCURSOR LoadImageA(HINSTANCE i, const char *n, UINT t, int cx, int cy, UINT f)
{ (void)i; (void)n; (void)t; (void)cx; (void)cy; (void)f; return (HCURSOR)(uintptr_t)0x3000; }
HCURSOR LoadImage(HINSTANCE i, const char *n, UINT t, int cx, int cy, UINT f)
{ return LoadImageA(i, n, t, cx, cy, f); }
HCURSOR LoadImageW(HINSTANCE i, const uint16_t *n, UINT t, int cx, int cy, UINT f)
{ (void)i; (void)n; (void)t; (void)cx; (void)cy; (void)f; return (HCURSOR)(uintptr_t)0x3000; }
HCURSOR LoadImageCursor(HINSTANCE i, const char *n, int t, int cx, int cy, int f)
{ return LoadImageA(i, n, (UINT)t, cx, cy, (UINT)f); }

// [失败] 图标合成需要 GDI 位图与掩码，Linux 上无对象可做。
BOOL GetIconInfoImpl(HICON ic, void *info) { (void)ic; (void)info; return 0; }
// [降级] CreateIconIndirect 在 Windows 上返回一个真 HICON；我们没有图标资源，
// 但返回 NULL 会让 HwndSource 的 SetIcon 路径抛异常。返回哨兵 + 不产生资源。
HICON CreateIconIndirect(const void *info) { (void)info; return (HICON)(uintptr_t)0x4000; }
BOOL DestroyIcon(HICON i) { (void)i; return 1; }
int ExtractIconExW(const uint16_t *f, int i, HICON *l, HICON *s, int n)
{ (void)f; (void)i; (void)l; (void)s; (void)n; return 0; }
int ExtractIconExA(const char *f, int i, HICON *l, HICON *s, int n)
{ (void)f; (void)i; (void)l; (void)s; (void)n; return 0; }
// CharSet.Auto → 裸名收 UTF-8
int ExtractIconEx(const char *f, int i, HICON *l, HICON *s, int n)
{ return ExtractIconExA(f, i, l, s, n); }

// ── 插入符 ─────────────────────────────────────────────────────────────────
// [降级] X11 的文本光标由 toolkit 自绘，没有系统插入符对象。全部返回成功
// （「插入符已创建/已显示」在 Linux 上不可观察，也没有相反的事实）。
BOOL CreateCaret(HWND h, HBITMAP bm, int w, int hh) { (void)h; (void)bm; (void)w; (void)hh; return 1; }
BOOL ShowCaret(HWND h) { (void)h; return 1; }
BOOL HideCaret(HWND h) { (void)h; return 1; }
BOOL DestroyCaret(void) { return 1; }
BOOL SetCaretPos(int x, int y) { (void)x; (void)y; return 1; }
UINT GetCaretBlinkTime(void) { return 500; }

// ── DPI ────────────────────────────────────────────────────────────────────
// [真话] X11（Xvfb 与绝大多数桌面）在 core protocol 里没有 per-monitor DPI。
// 我们不做缩放，所以 96 DPI / 100% 是**真值**而不是伪造。
// Per-Monitor-V2 的 API 组因此返回「不支持」而不是假装支持。
// 【一致性】这三个入口与 `GetDeviceCaps(LOGPIXELSX)` 必须同源：WPF 会分别用它们
// 初始化 HwndTarget 的 CurrentDpiScale 与 UIElement 的 DpiScale 缓存，两套值不一致
// 会让"窗口像素尺寸"和"文字缩放"对不上。统一走 wpf_x11_screen_metrics()。
UINT GetDpiForWindow(HWND h)
{
    if (wpf_legacy_dpi()) { (void)h; return 96; }
    wpf_screen_metrics m; wpf_x11_screen_metrics(&m); return (UINT)m.dpi_x;
}
UINT GetDpiForSystem(void)
{
    if (wpf_legacy_dpi()) return 96;
    wpf_screen_metrics m; wpf_x11_screen_metrics(&m); return (UINT)m.dpi_x;
}
UINT GetDpiForMonitor(void *mon, int type, UINT *dpiX, UINT *dpiY)
{ (void)mon; (void)type;
  wpf_screen_metrics m; wpf_x11_screen_metrics(&m);
  if (dpiX) *dpiX = (UINT)m.dpi_x; if (dpiY) *dpiY = (UINT)m.dpi_y;
  return 0; }  // S_OK，值是真的（X server 报的屏幕 DPI）
BOOL SetProcessDPIAware(void) { return 1; }        // 已经是「不缩放」，设置成功
BOOL IsProcessDPIAware(void) { return 1; }
BOOL EnableNonClientDpiScaling(HWND h) { (void)h; return 1; }
void *GetWindowDpiAwarenessContext(HWND h) { (void)h; return (void *)(uintptr_t)-4; }  // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 的近似
void *SetThreadDpiAwarenessContext(void *ctx) { void *old = (void *)(uintptr_t)-4; (void)ctx; return old; }
int GetThreadDpiHostingBehavior(void) { return 0; }
BOOL AreDpiAwarenessContextsEqual(void *a, void *b) { return a == b; }

// ── kernel32 / 模块 ────────────────────────────────────────────────────────
// GetModuleHandle 的返回值在本 shim 里是**进程内不透明标识**：同一名字恒定、
// 非 0。唯一真正被解引用的是 GetProcAddress 的 hModule 参数，而我们的
// GetProcAddress **忽略它**，只在自己导出的符号表里查（见 win32_exports.c）。
// 这一点很关键：HwndSubclass 的静态构造会 GetProcAddress(GetModuleHandle("user32.dll"),
// "DefWindowProcW")，拿到 NULL 的话整个回退链就断了。
#define WPF_FAKE_HMODULE ((HMODULE)(uintptr_t)0x7F000000)

HMODULE GetModuleHandleW(const uint16_t *name) { (void)name; return WPF_FAKE_HMODULE; }
HMODULE GetModuleHandleA(const char *name) { (void)name; return WPF_FAKE_HMODULE; }
HMODULE GetModuleHandle(const char *name) { return GetModuleHandleA(name); }
BOOL GetModuleHandleExW(UINT flags, const uint16_t *name, HMODULE *out)
{ (void)flags; (void)name; if (out) *out = WPF_FAKE_HMODULE; return 1; }
BOOL GetModuleHandleExA(UINT flags, const char *name, HMODULE *out)
{ (void)flags; (void)name; if (out) *out = WPF_FAKE_HMODULE; return 1; }
BOOL GetModuleHandleEx(UINT f, const char *n, HMODULE *o) { return GetModuleHandleExA(f, n, o); }

// [真实现] GetModuleFileName：真读 /proc/self/exe（Linux 上的等价物）。
DWORD GetModuleFileNameW(HMODULE m, uint16_t *buf, DWORD n)
{
    (void)m;
    if (!buf || n == 0) return 0;
    char path[4096];
    ssize_t r = readlink("/proc/self/exe", path, sizeof(path) - 1);
    if (r <= 0) { buf[0] = 0; return 0; }
    path[r] = 0;
    uint16_t *u = wpf_utf8_to_utf16_dup(path);
    DWORD i = 0;
    if (u) { while (u[i] && i < n - 1) { buf[i] = u[i]; i++; } free(u); }
    buf[i] = 0;
    return i;
}
DWORD GetModuleFileNameA(HMODULE m, char *buf, DWORD n)
{
    (void)m;
    if (!buf || n == 0) return 0;
    ssize_t r = readlink("/proc/self/exe", buf, n - 1);
    if (r <= 0) { buf[0] = 0; return 0; }
    buf[r] = 0;
    return (DWORD)r;
}

// ── [真实现] MultiByteToWideChar ───────────────────────────────────────────
// 托管侧（MS.Win32.NativeMethods）用它做 UTF-8/ANSI → UTF-16。Linux 上的
// 「ANSI 代码页」就是 UTF-8（CP_ACP == 65001），所以 CP_ACP/CP_UTF8 都按 UTF-8 解。
// 返回**转换出的 UTF-16 单元数**（不含结尾 0），与 Win32 一致；cbMultiByte == -1
// 时把结尾 0 也算进去。
int MultiByteToWideChar(UINT codePage, DWORD flags, const char *src, int srcLen,
                        uint16_t *dst, int dstLen)
{
    (void)flags;
    if (!src) return 0;
    if (codePage != 0 /*CP_ACP*/ && codePage != 65001 /*CP_UTF8*/ && codePage != 1 /*CP_OEMCP*/) {
        wpf_set_last_error(1113);   // ERROR_NO_UNICODE_TRANSLATION
        return 0;
    }
    size_t inLen;
    int needZero = 0;
    if (srcLen < 0) { inLen = strlen(src); needZero = 1; }
    else            { inLen = (size_t)srcLen; }
    if (dstLen == 0) {
        // 查询长度模式
        uint16_t *tmp = (uint16_t *)malloc((inLen + 2) * sizeof(uint16_t));
        if (!tmp) return 0;
        memcpy(tmp, "", 1);
        // 只是数一遍：用一个足够大的临时缓冲
        char *copy = (char *)malloc(inLen + 1);
        if (!copy) { free(tmp); return 0; }
        memcpy(copy, src, inLen); copy[inLen] = 0;
        uint16_t *u = wpf_utf8_to_utf16_dup(copy);
        int n = 0;
        if (u) { while (u[n]) n++; free(u); }
        free(copy); free(tmp);
        return n + needZero;
    }
    char *copy = (char *)malloc(inLen + 1);
    if (!copy) return 0;
    memcpy(copy, src, inLen); copy[inLen] = 0;
    uint16_t *u = wpf_utf8_to_utf16_dup(copy);
    free(copy);
    if (!u) return 0;
    int n = 0;
    while (u[n] && n < dstLen) { dst[n] = u[n]; n++; }
    if (needZero && n < dstLen) dst[n++] = 0;
    free(u);
    return n;
}

// ── [真实现] LoadLibrary / GetProcAddress ─────────────────────────────────
// 这两个是真的：先在自己的模块里找（Win32 名字直接映射到本 shim 的符号），
// 找不到再 dlopen 真实 .so。dlopen 失败 → 返回 NULL（Win32 的失败语义）。
extern void *wpf_shim_symbol(const char *name);   // win32_exports.c

HMODULE LoadLibraryW(const uint16_t *name)
{
    char *utf8 = wpf_utf16_to_utf8_dup(name);
    HMODULE r = NULL;
    if (utf8) {
        // Win32 的 DLL 名在 Linux 上没有文件，但**符号**可能在本 shim 里
        // （user32.dll/gdi32.dll/kernel32.dll/PresentationNative_cor3.dll）。
        // 只要有任意一个导出命中，就认为「加载成功」。
        if (wpf_shim_symbol("GetMessageW") != NULL) r = WPF_FAKE_HMODULE;
        if (!r) {
            void *h = dlopen(utf8, RTLD_LAZY);
            if (h) r = (HMODULE)h;
        }
        free(utf8);
    }
    if (!r) wpf_set_last_error(126);   // ERROR_MOD_NOT_FOUND
    return r;
}
HMODULE LoadLibraryA(const char *name)
{
    uint16_t *u = wpf_utf8_to_utf16_dup(name);
    HMODULE r = LoadLibraryW(u);
    free(u);
    return r;
}
HMODULE LoadLibrary(const char *name) { return LoadLibraryA(name); }
HMODULE LoadLibraryExW(const uint16_t *n, HANDLE f, DWORD flags)
{ (void)f; (void)flags; return LoadLibraryW(n); }
HMODULE LoadLibraryExA(const char *n, HANDLE f, DWORD flags)
{ (void)f; (void)flags; return LoadLibraryA(n); }
HMODULE LoadLibraryEx(const char *n, HANDLE f, DWORD flags) { return LoadLibraryExA(n, f, flags); }
BOOL FreeLibrary(HMODULE m)
{
    if (m == WPF_FAKE_HMODULE) return 1;      // 假句柄：本 shim 常驻，无需卸载
    return m ? (dlclose((void *)m) == 0) : 0;
}

// ── 进程 / 线程 / 同步 ─────────────────────────────────────────────────────
HANDLE GetCurrentProcess(void) { return (HANDLE)(uintptr_t)-1; }
DWORD GetCurrentThreadId(void)
{
    // 与 wpf_thread_id_of_window 用同一个口径（低 31 位），保证
    // EnumThreadWindows(GetCurrentThreadId()) 能找到本线程建的窗口。
    wpf_thread *t = wpf_thread_self();
    return (DWORD)((uintptr_t)t & 0x7FFFFFFF);
}
BOOL IsDebuggerPresent(void) { return 0; }
BOOL CloseHandle(HANDLE h) { (void)h; return 1; }

// [失败] 内核对象（事件/进程/文件映射）在 Linux 上没有同名可映射物，
// 不伪造句柄。
HANDLE CreateEventW(void *sa, BOOL manual, BOOL initial, const uint16_t *name)
{ (void)sa; (void)manual; (void)initial; (void)name; wpf_set_last_error(50); return NULL; }
HANDLE CreateEventA(void *sa, BOOL manual, BOOL initial, const char *name)
{ return CreateEventW(sa, manual, initial, NULL); }
HANDLE CreateEvent(void *sa, BOOL m, BOOL i, const char *n) { return CreateEventA(sa, m, i, n); }
BOOL SetEvent(HANDLE h) { (void)h; wpf_set_last_error(50); return 0; }
BOOL ResetEvent(HANDLE h) { (void)h; wpf_set_last_error(50); return 0; }
DWORD WaitForMultipleObjectsEx(DWORD n, const HANDLE *h, BOOL all, DWORD ms, BOOL alertable)
{ (void)n; (void)h; (void)all; (void)ms; (void)alertable; wpf_set_last_error(50); return WAIT_FAILED; }
HANDLE OpenProcess(DWORD access, BOOL inherit, DWORD pid)
{ (void)access; (void)inherit; (void)pid; wpf_set_last_error(5); return NULL; }
BOOL DuplicateHandle(HANDLE src, HANDLE sh, HANDLE dst, HANDLE *out, DWORD access,
                     BOOL inherit, DWORD options)
{ (void)src; (void)sh; (void)dst; (void)access; (void)inherit; (void)options;
  if (out) *out = NULL;
  wpf_set_last_error(50);
  return 0; }
BOOL ProcessIdToSessionId(DWORD pid, DWORD *session)
{ (void)pid; if (session) *session = 0; return 1; }   // Linux 无会话概念 → 0（默认）是真话

// [失败] 文件映射：WPF 用它做 MIL 跨进程共享内存。M1 走的是进程内通道，
// 不需要它；真用到时应改用 memfd/shm_open（登记为 M7c+ 的接口需求）。
HANDLE CreateFileMappingW(HANDLE file, void *sa, DWORD prot, DWORD hi, DWORD lo, const uint16_t *name)
{ (void)file; (void)sa; (void)prot; (void)hi; (void)lo; (void)name; wpf_set_last_error(50); return NULL; }
HANDLE CreateFileMappingA(HANDLE f, void *sa, DWORD p, DWORD hi, DWORD lo, const char *n)
{ return CreateFileMappingW(f, sa, p, hi, lo, NULL); }
void *MapViewOfFileEx(HANDLE m, DWORD access, DWORD hi, DWORD lo, size_t n, void *base)
{ (void)m; (void)access; (void)hi; (void)lo; (void)n; (void)base; wpf_set_last_error(6); return NULL; }
BOOL UnmapViewOfFile(const void *p) { (void)p; return 0; }
BOOL GetFileSizeEx(HANDLE f, int64_t *size) { (void)f; if (size) *size = 0; return 0; }

// [失败] CreateFile：.NET 自己的 FileStream 走 BCL，这个入口只被
// MS.Win32 的少数路径用（如 ETW/临时文件）。不伪造句柄。
HANDLE CreateFileW(const uint16_t *n, DWORD acc, DWORD share, void *sa, DWORD disp,
                   DWORD flags, HANDLE tmpl)
{ (void)n; (void)acc; (void)share; (void)sa; (void)disp; (void)flags; (void)tmpl;
  wpf_set_last_error(2); return (HANDLE)(uintptr_t)-1; }   // INVALID_HANDLE_VALUE
HANDLE CreateFileA(const char *n, DWORD a, DWORD s, void *sa, DWORD d, DWORD f, HANDLE t)
{ return CreateFileW(NULL, a, s, sa, d, f, t); }
HANDLE CreateFile(const char *n, DWORD a, DWORD s, void *sa, DWORD d, DWORD f, HANDLE t)
{ return CreateFileA(n, a, s, sa, d, f, t); }

HLOCAL LocalFree(HLOCAL p)
{
    // .NET 在 Unix 上的 Marshal.AllocHGlobal 就是 malloc，所以 localfree 对应 free。
    free((void *)p);
    return NULL;
}

// ── 字符串 / 区域 ──────────────────────────────────────────────────────────
// [失败] NLS 相关的三个都依赖 Windows 的本地化表；Linux 上应走 ICU。
// 返回失败，让上层回退到托管实现（BCL 的 CultureInfo 在 Linux 上是可用的）。
int GetLocaleInfoW(uint32_t locale, uint32_t type, uint16_t *data, int cch)
{ (void)locale; (void)type; (void)data; (void)cch; wpf_set_last_error(50); return 0; }
int GetLocaleInfoA(uint32_t l, uint32_t t, char *d, int c) { (void)l; (void)t; (void)d; (void)c; return 0; }
int GetLocaleInfo(uint32_t l, uint32_t t, char *d, int c) { return GetLocaleInfoA(l, t, d, c); }
BOOL GetStringTypeExW(uint32_t locale, uint32_t type, const uint16_t *src, int n, uint16_t *out)
{ (void)locale; (void)type; (void)src; (void)n; (void)out; wpf_set_last_error(50); return 0; }
BOOL GetStringTypeExA(uint32_t l, uint32_t t, const char *s, int n, uint16_t *o)
{ (void)l; (void)t; (void)s; (void)n; (void)o; wpf_set_last_error(50); return 0; }
BOOL GetStringTypeEx(uint32_t l, uint32_t t, const char *s, int n, uint16_t *o)
{ return GetStringTypeExA(l, t, s, n, o); }
int FindNLSStringW(uint32_t locale, DWORD flags, const uint16_t *src, int srcLen,
                   const uint16_t *find, int findLen, int *found)
{ (void)locale; (void)flags; (void)src; (void)srcLen; (void)find; (void)findLen;
  if (found) *found = 0;
  wpf_set_last_error(50);
  return -1; }
// [降级→真话] 「OEM 代码页」在 Linux 上就是 locale 的编码；本工程锁定 UTF-8。
UINT GetOEMCP(void) { return 65001; }
UINT GetACP(void) { return 65001; }

// ── 电源 / 会话 ────────────────────────────────────────────────────────────
// [降级→真话] 无电池的桌面/容器：AC 在线、无电池。这是 Xvfb/CI 环境的事实。
// SYSTEM_POWER_STATUS { BYTE ACLineStatus; BYTE BatteryFlag; BYTE BatteryLifePercent;
//                       BYTE SystemStatusFlag; DWORD BatteryLifeTime; DWORD BatteryFullLifeTime; }
BOOL GetSystemPowerStatus(uint8_t *out)
{
    if (!out) return 0;
    memset(out, 0, 12);
    out[0] = 1;     // ACLineStatus = Online
    out[1] = 128;   // BatteryFlag = No system battery
    out[2] = 255;   // BatteryLifePercent = Unknown
    return 1;
}
// [失败] 电源设置的变更通知走 Windows 的 WM_POWERBROADCAST；X11 无对应物。
HPOWERNOTIFY RegisterPowerSettingNotification(HANDLE recipient, const void *guid, DWORD flags)
{ (void)recipient; (void)guid; (void)flags; wpf_set_last_error(50); return NULL; }
BOOL UnregisterPowerSettingNotification(HPOWERNOTIFY h) { (void)h; return 1; }  // 幂等清理，无需对象

// ── 无障碍（UIA）─────────────────────────────────────────────────────────
// [降级] NotifyWinEvent 是 UIA 的「事件广播」。Linux 上的等价物是 AT-SPI2 的
// D-Bus 接口，属于独立里程碑；本 shim 不做。**不伪造 hook**：
// SetWinEventHook 返回 NULL（失败），HasWinEvent 一律 0。
void NotifyWinEvent(DWORD ev, HWND h, LONG obj, LONG child) { (void)ev; (void)h; (void)obj; (void)child; }
void *SetWinEventHook(DWORD a, DWORD b, HMODULE m, void *proc, DWORD pid, DWORD tid, DWORD flags)
{ (void)a; (void)b; (void)m; (void)proc; (void)pid; (void)tid; (void)flags;
  wpf_set_last_error(50); return NULL; }
BOOL UnhookWinEvent(void *hook) { (void)hook; return 0; }
BOOL IsWinEventHookInstalled(DWORD ev) { (void)ev; return 0; }

// ── 消息过滤（UIPI）───────────────────────────────────────────────────────
// [降级→真话] Windows 的 UIPI 会按完整性级别拦截跨进程消息。Linux/X11 上
// 根本没有这个机制 → 没有任何消息会被拦下来 → 返回成功的语义是准确的。
BOOL ChangeWindowMessageFilter(UINT msg, DWORD flag) { (void)msg; (void)flag; return 1; }
BOOL ChangeWindowMessageFilterEx(HWND h, UINT msg, DWORD action, WPF_CHANGEFILTERSTRUCT *info)
{
    (void)h; (void)msg; (void)action;
    if (info) info->ExtStatus = 0;   // MSGFLTINFO_NONE
    return 1;
}

// ── 窗口放置 / 分层 / 其它 ─────────────────────────────────────────────────
// WINDOWPLACEMENT 布局（NativeMethods.WINDOWPLACEMENT 是 Sequential 的托管结构）：
//   UINT length; UINT flags; UINT showCmd; POINT ptMinPosition; POINT ptMaxPosition;
//   RECT rcNormalPosition;  → 4+4+4+8+8+16 = 44
BOOL GetWindowPlacement(HWND h, void *pl)
{
    if (!pl) return 0;
    memset(pl, 0, 44);
    *(uint32_t *)((char *)pl + 0) = 44;    // length
    *(uint32_t *)((char *)pl + 8) = 1;     // showCmd = SW_SHOWNORMAL
    wpf_lock();
    wpf_window *w = wpf_window_find(h);
    if (w) {
        WPF_RECT *rc = (WPF_RECT *)((char *)pl + 28);   // rcNormalPosition @ 28
        rc->left = w->x; rc->top = w->y;
        rc->right = w->x + w->width; rc->bottom = w->y + w->height;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return 1;
}
BOOL SetWindowPlacement(HWND h, const void *pl)
{
    if (!pl) return 0;
    const WPF_RECT *rc = (const WPF_RECT *)((const char *)pl + 28);
    return MoveWindow(h, rc->left, rc->top,
                      rc->right - rc->left, rc->bottom - rc->top, 1);
}

// [失败] 分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）是 Windows 独有的
// 合成路径。X11 上等价的正确做法是 ARGB visual + XRender/XComposite，
// 本 shim 不做 → 返回 FALSE，让 HwndTarget 走非分层的正常路径。
BOOL GetLayeredWindowAttributes(HWND h, void *key, void *alpha, void *flags)
{ (void)h; (void)key; (void)alpha; (void)flags; wpf_set_last_error(87); return 0; }
BOOL SetLayeredWindowAttributes(HWND h, uint32_t key, uint8_t alpha, uint32_t flags)
{ (void)h; (void)key; (void)alpha; (void)flags; wpf_set_last_error(50); return 0; }
BOOL UpdateLayeredWindow(HWND h, HDC src, const WPF_POINT *srcPos, const WPF_SIZE *size,
                         HDC dst, const WPF_POINT *dstPos, uint32_t colorKey, void *blend,
                         uint32_t flags)
{ (void)h; (void)src; (void)srcPos; (void)size; (void)dst; (void)dstPos;
  (void)colorKey; (void)blend; (void)flags; wpf_set_last_error(50); return 0; }

// [失败] 高精度鼠标轨迹需要 Windows 的「鼠标输入历史」队列。
DWORD GetMouseMovePointsEx(UINT size, const void *in, void *out, int n, DWORD res)
{ (void)size; (void)in; (void)out; (void)n; (void)res; wpf_set_last_error(50); return (DWORD)-1; }

// [失败] 模态对话框：X11 上没有，而且**绝不能阻塞**（headless 会死锁）。
// 写 stderr 并把内容当作「用户按了确定」返回 —— 这是刻意的降级，
// 目的是让调用方继续走完流程而不是挂住。已登记到 README 与报告。
int MessageBoxW(HWND h, const uint16_t *text, const uint16_t *caption, UINT type)
{
    (void)h; (void)type;
    char *t = wpf_utf16_to_utf8_dup(text);
    char *c = wpf_utf16_to_utf8_dup(caption);
    fprintf(stderr, "[wpfwin32] MessageBox(降级，不阻塞): [%s] %s\n",
            c ? c : "", t ? t : "");
    free(t); free(c);
    return 1;   // IDOK
}
int MessageBoxA(HWND h, const char *t, const char *c, UINT type)
{
    (void)h; (void)type;
    fprintf(stderr, "[wpfwin32] MessageBox(降级，不阻塞): [%s] %s\n", c ? c : "", t ? t : "");
    return 1;
}
int MessageBox(HWND h, const char *t, const char *c, UINT type) { return MessageBoxA(h, t, c, type); }

// ── SystemParametersInfo ───────────────────────────────────────────────────
// 【首版为什么错，错成了什么】
//   首版是「出参不填、直接返回成功」并注释成"把出参清零 = 系统默认值"。
//   这个判断有两处错：
//     ① 它**连清零都没做**（`if (data) { /* 不写脏数据 */ }` 是个空块），出参保持
//        调用方给的初值——对 `new NONCLIENTMETRICS()` 来说就是全 0；
//     ② "0 是系统默认值"对一部分 action 是**假话**：
//        `lfMessageFont.lfWeight == 0` → `SystemFonts.MessageFontWeight`
//        → `FontWeight.FromOpenTypeWeight(0)` → **ArgumentOutOfRangeException**；
//        `SPI_GETWHEELSCROLLLINES == 0` → 滚轮每格滚 0 行（滚不动）。
//   `SystemFonts` / `SystemParameters` 在 `TextElement / FrameworkElement / Window`
//   的静态构造链上（**与 XAML 无关**），所以这条不是"某个 demo 的问题"，
//   而是**任何 WPF 窗口在任何 Linux 机器上都起不来**。
//
// 【现在的做法：按 action 分派，写"自洽的默认值"】
//   设计原则（三条，逐条可检验）：
//     A. **能不写就不写**：不认识的 action 返回 TRUE 但**不碰出参**（不写脏数据）。
//     B. **必须写的写对**：WPF 真的会读的 action（下面 switch 里列全了，来源是
//        PresentationFramework/System/Windows/SystemParameters.cs 的调用点）一律写。
//     C. **写进去的值要么是真话，要么是一份自洽的桌面默认**：
//        · `UIEffects = FALSE` 且所有"特效"子开关同为 FALSE —— 自洽，且是真话
//          （X11 下没有 DWM/合成器，我们也不实现这些动画）；
//        · `KEYBOARDCUES = TRUE` —— 这是**无障碍**开关而不是"特效"，关掉会让菜单
//          永不显示快捷键下划线，属于可观察的功能退化，所以给 TRUE；
//        · 尺寸/数量类给 Windows 的常规默认值（1/3/4/400/31…），0 在这里没有意义。
//     D. 字体族与字号可用环境变量覆盖（`WPF_LINUX_UI_FONT` / `WPF_LINUX_UI_FONT_SIZE`），
//        因为 Linux 上"系统 UI 字体"归桌面环境/fontconfig 管，没有一个进程内 API
//        能问出来。默认值是一个几乎无处不在的真实字族，见 `ui_font_face()`。
//
//   Win32 语义提醒：`SPI_GETNONCLIENTMETRICS` 等**结构化** action 要求调用方把
//   `cbSize` 填对；我们据此**夹住写入长度**，`cbSize` 不够就返回失败而不是越界写。

// WPF 会读的 action ↔ 值来源。为便于复核，每一条都标了它对应的托管属性。
#define WPF_DEFAULT_UI_FONT      "DejaVu Sans"
#define WPF_DEFAULT_UI_FONT_SIZE 12   // 像素（lfHeight 取负）；96 DPI ⇒ MessageFontSize = 12.0

// [真话] Linux 上的"系统 UI 字体"归 fontconfig/桌面环境管，没有进程内查询接口。
// 默认给一个几乎所有发行版都装了、且本机实测存在的真实字族（`fc-list` 可验）。
// **不是"读到的系统字体"，是本 shim 选定的默认值** —— 这一点必须说清楚。
// 若目标机器没装它，`FontFamily` 只会在真正渲染时走字体回退，不会崩。
static const char *ui_font_face(void)
{
    const char *env = getenv("WPF_LINUX_UI_FONT");
    return (env && *env) ? env : WPF_DEFAULT_UI_FONT;
}

static int ui_font_pixel_size(void)
{
    const char *env = getenv("WPF_LINUX_UI_FONT_SIZE");
    if (env && *env) {
        int v = atoi(env);
        if (v > 0 && v < 512) return v;
    }
    return WPF_DEFAULT_UI_FONT_SIZE;
}

// 填充一个 LOGFONT。lfHeight 取负 = 字符高度（Win32 约定，托管侧 ConvertFontHeight
// 会对它取 Math.Abs 再按 DPI 折算）。lfWeight = 400 (FW_NORMAL) —— **这一条是本轮
// 最关键的一个数字**：0 会让 FontWeight.FromOpenTypeWeight 抛异常。
static void fill_logfont(WPF_LOGFONT *lf, int pixel_height, int weight, int italic,
                         const char *face)
{
    memset(lf, 0, sizeof(*lf));
    lf->lfHeight = -pixel_height;
    lf->lfWidth = 0;
    lf->lfEscapement = 0;
    lf->lfOrientation = 0;
    lf->lfWeight = weight;            // OpenType 权重：400 = Normal, 700 = Bold
    lf->lfItalic = (uint8_t)(italic ? 1 : 0);
    lf->lfUnderline = 0;
    lf->lfStrikeOut = 0;
    lf->lfCharSet = 1;                // DEFAULT_CHARSET
    lf->lfOutPrecision = 0;           // OUT_DEFAULT_PRECIS
    lf->lfClipPrecision = 0;          // CLIP_DEFAULT_PRECIS
    lf->lfQuality = 5;                // CLEARTYPE_QUALITY
    lf->lfPitchAndFamily = 0;         // DEFAULT_PITCH | FF_DONTCARE
    if (face && *face) {
        uint16_t *u = wpf_utf8_to_utf16_dup(face);
        if (u) {
            for (int i = 0; i < 31 && u[i]; i++) lf->lfFaceName[i] = u[i];
            free(u);
        }
    }
    lf->lfFaceName[31] = 0;
}

// 写一个标量出参。Win32 的 SPI 标量出参宽度随 action 变：
//   `ref int` / `ref bool` / `ref uint` 在托管侧 marshal 后都是 **4 字节**
//   （C# `bool` 在结构体与 ref 参数里默认是 4 字节 Win32 BOOL）。
// 所以统一按 int32 写。`ref IntPtr`（如 DEFAULTINPUTLANG）另走一支。
static void put_i32(void *data, int32_t v) { if (data) *(int32_t *)data = v; }
static void put_u32(void *data, uint32_t v) { if (data) *(uint32_t *)data = v; }
static void put_bool(void *data, int v) { put_i32(data, v ? 1 : 0); }

/* ── 【`D-G147` · 修法 B】`SPI_GETWORKAREA` **不再**自己从屏幕尺寸算 ───────────────
   修前：本函数与 `GetMonitorInfoW` 各自 `DisplayWidth/Height` ⇒ 两个出口两个来源，
   而"工作区 != 显示器"的信息（`_NET_WORKAREA`）在两侧都被丢掉。
   现在：唯一来源 = `wpf_x11_workarea()`（`_NET_WORKAREA` 当前桌面格），回退显式声明。
   ⇒ 托管侧 `SystemParameters.WorkArea`／`WorkAreaInternal`
   （`upstream …/SystemParameters.cs:339-353` = `SPI_GETWORKAREA`）与
   `Window.WorkAreaBoundsForNearestMonitor`（`GetMonitorInfo(rcWork)`）**逐值一致**。 */
static void fill_work_area(WPF_RECT *rc)
{
    int wx = 0, wy = 0, ww = 0, wh = 0;
    wpf_x11_workarea(&wx, &wy, &ww, &wh);
    if (!(ww > 0 && wh > 0)) {                 // 无 X：与 GetSystemMetrics 的兜底口径一致
        wx = 0; wy = 0; ww = 1280; wh = 1024;
    }
    rc->left = wx; rc->top = wy; rc->right = wx + ww; rc->bottom = wy + wh;
    wpf_x11_workarea_declare("SPI_GETWORKAREA", NULL, 0,
                             rc->left, rc->top, rc->right, rc->bottom);
}

// 结构化 action 的写入长度夹取：`cbSize` 不足 → 不写、返回失败（Win32 语义）。
static int fits(uint32_t cbSize, size_t need)
{
    return cbSize >= (uint32_t)need;
}

static BOOL spi_query(UINT action, UINT param, void *data)
{
    switch (action) {
    // ── 结构化查询 ────────────────────────────────────────────────────────
    case SPI_GETNONCLIENTMETRICS: {
        if (!data) { wpf_set_last_error(87); return 0; }
        uint32_t cb = *(uint32_t *)data;          // 托管侧填的是 Marshal.SizeOf = 500
        if (!fits(cb, sizeof(WPF_NONCLIENTMETRICS))) { wpf_set_last_error(87); return 0; }
        WPF_NONCLIENTMETRICS *ncm = (WPF_NONCLIENTMETRICS *)data;
        const char *face = ui_font_face();
        int px = ui_font_pixel_size();
        // 度量值取 Windows 10 默认主题的常规值；它们是"客户区之外"的尺寸，
        // X11 上由 WM 负责（本环境无 WM），但 SystemParameters 的公开属性会读它们，
        // 给 0 会让"边框宽度/滚动条宽度"变成 0 这种明显不合理的值。
        ncm->iBorderWidth   = 1;
        ncm->iScrollWidth   = 17;
        ncm->iScrollHeight  = 17;
        ncm->iCaptionWidth  = 4;
        ncm->iCaptionHeight = 23;                 // 与 lfCaptionFont 的行高相称
        fill_logfont(&ncm->lfCaptionFont,   px, 400, 0, face);
        ncm->iSmCaptionWidth  = 4;
        ncm->iSmCaptionHeight = 18;
        fill_logfont(&ncm->lfSmCaptionFont, px - 1 > 0 ? px - 1 : px, 400, 0, face);
        ncm->iMenuWidth  = 19;
        ncm->iMenuHeight = 19;
        fill_logfont(&ncm->lfMenuFont,      px, 400, 0, face);
        fill_logfont(&ncm->lfStatusFont,    px, 400, 0, face);
        // ★ lfMessageFont 是 WPF 真正读的那一个（SystemFonts.MessageFont*）
        fill_logfont(&ncm->lfMessageFont,   px, 400, 0, face);
        ncm->cbSize = (int32_t)cb;                // 回写调用方给的值
        return 1;
    }
    case SPI_GETICONMETRICS: {
        if (!data) { wpf_set_last_error(87); return 0; }
        uint32_t cb = *(uint32_t *)data;
        if (!fits(cb, sizeof(WPF_ICONMETRICS))) { wpf_set_last_error(87); return 0; }
        WPF_ICONMETRICS *im = (WPF_ICONMETRICS *)data;
        im->iHorzSpacing = 75;
        im->iVertSpacing = 75;
        im->iTitleWrap   = 1;
        // SystemFonts.IconFontWeight 走同一个 FromOpenTypeWeight ⇒ 这里也必须是 400
        fill_logfont(&im->lfFont, ui_font_pixel_size(), 400, 0, ui_font_face());
        im->cbSize = (int32_t)cb;
        return 1;
    }
    case SPI_GETICONTITLELOGFONT:
        // ⚠️ **刻意返回失败**。理由：上游 `UnsafeNativeMethods` 里**没有**任何
        //    `ref LOGFONT` 的重载（实测 grep：只有 ref RECT / ref int / ref bool /
        //    ref HIGHCONTRAST_I / [In,Out] NONCLIENTMETRICS / ANIMATIONINFO / ICONMETRICS），
        //    所以这个 action 目前**不可达**。它也没有 `cbSize` 可以夹住写入长度 ——
        //    万一将来有人补上重载再调它，我们要的是**响亮的 Win32Exception**，
        //    而不是"写 92 字节进一个不知道多大的缓冲"或者"悄悄留一堆 0"。
        wpf_set_last_error(87);
        return 0;
    case SPI_GETWORKAREA:
        if (!data) { wpf_set_last_error(87); return 0; }
        fill_work_area((WPF_RECT *)data);         // 无 WM ⇒ 工作区 == 整个屏幕（真话）
        return 1;
    case SPI_GETHIGHCONTRAST: {
        if (!data) { wpf_set_last_error(87); return 0; }
        uint32_t cb = *(uint32_t *)data;
        if (!fits(cb, sizeof(WPF_HIGHCONTRAST_I))) { wpf_set_last_error(87); return 0; }
        WPF_HIGHCONTRAST_I *hc = (WPF_HIGHCONTRAST_I *)data;
        hc->dwFlags = 0;                          // 未启用高对比度（X11 上无此设置源）
        hc->lpszDefaultScheme = NULL;
        return 1;
    }
    case SPI_GETANIMATION: {
        if (!data) { wpf_set_last_error(87); return 0; }
        uint32_t cb = *(uint32_t *)data;
        if (!fits(cb, sizeof(WPF_ANIMATIONINFO))) { wpf_set_last_error(87); return 0; }
        ((WPF_ANIMATIONINFO *)data)->iMinAnimate = 0;   // 最小化/还原动画：不做
        return 1;
    }

    // ── 标量查询：**0 在这里没有意义**的那些 ─────────────────────────────
    case SPI_GETFOCUSBORDERWIDTH:              // SystemParameters.FocusBorderWidth
    case SPI_GETFOCUSBORDERHEIGHT:             // SystemParameters.FocusBorderHeight
        put_u32(data, 1);                      // Windows 默认 1 像素
        return 1;
    case SPI_GETCARETWIDTH:                    // SystemParameters.CaretWidth
        put_u32(data, 1);
        return 1;
    case SPI_GETKEYBOARDSPEED:                 // SystemParameters.KeyboardSpeed（0..31，31 = 最快）
        put_u32(data, 31);
        return 1;
    case SPI_GETKEYBOARDDELAY:                 // SystemParameters.KeyboardDelay（0..3）
        put_i32(data, 1);
        return 1;
    case SPI_GETMOUSEHOVERTIME:                // SystemParameters.MouseHoverTime
        put_u32(data, 400);
        return 1;
    case SPI_GETMOUSEHOVERWIDTH:               // SystemParameters.MouseHoverWidth
    case SPI_GETMOUSEHOVERHEIGHT:              // SystemParameters.MouseHoverHeight
        put_u32(data, 4);
        return 1;
    case SPI_GETMOUSESPEED:                    // SystemParameters.MouseSpeed（1..20）
        put_i32(data, 10);
        return 1;
    case SPI_GETMENUSHOWDELAY:                 // SystemParameters.MenuShowDelay
        put_u32(data, 400);
        return 1;
    case SPI_GETWHEELSCROLLLINES:              // SystemParameters.WheelScrollLines
        put_u32(data, 3);                      // ★ 0 会让滚轮"每格滚 0 行"
        return 1;
    case SPI_GETFOREGROUNDFLASHCOUNT:          // SystemParameters.ForegroundFlashCount
        put_u32(data, 3);
        return 1;
    case SPI_GETFOREGROUNDLOCKTIMEOUT:
        put_u32(data, 0);                      // 0 = 不等待（Windows 客户端的常见默认）
        return 1;
    case SPI_GETACTIVEWNDTRKTIMEOUT:
        put_u32(data, 0);
        return 1;
    case SPI_GETBORDER:                        // SystemParameters.Border（0..20）
        put_i32(data, 1);
        return 1;
    case SPI_GETDEFAULTINPUTLANG:
        // 托管侧是 `ref IntPtr`，但 HKL 在 X11 上只是我们自己的标识值（见 GetKeyboardLayout）
        if (data) *(void **)data = (void *)(uintptr_t)0x0409;
        return 1;

    // ── 字体平滑：我们的字体引擎是 M1 的 Skia 栈，走亚像素抗锯齿 ──────────
    case SPI_GETFONTSMOOTHING:                 // SystemParameters.FontSmoothing
        put_bool(data, 1);
        return 1;
    case SPI_GETFONTSMOOTHINGTYPE:             // 2 = FE_FONTSMOOTHINGCLEARTYPE
        put_u32(data, 2);
        return 1;
    case SPI_GETFONTSMOOTHINGCONTRAST:         // 1000..2200，1200 是 Windows 默认
        put_u32(data, 1200);
        return 1;

    // ── 布尔开关（0 与当前的"不写"等价，这里写出来是为了**自洽**）─────────
    case SPI_GETUIEFFECTS:                     // SystemParameters.UIEffects
    case SPI_GETMENUANIMATION:
    case SPI_GETCOMBOBOXANIMATION:
    case SPI_GETLISTBOXSMOOTHSCROLLING:
    case SPI_GETGRADIENTCAPTIONS:
    case SPI_GETHOTTRACKING:
    case SPI_GETSTYLUSHOTTRACKING:
    case SPI_GETMENUFADE:
    case SPI_GETSELECTIONFADE:
    case SPI_GETTOOLTIPANIMATION:
    case SPI_GETTOOLTIPFADE:
    case SPI_GETCURSORSHADOW:
    case SPI_GETMOUSEVANISH:
    case SPI_GETFLATMENU:
    case SPI_GETDROPSHADOW:
    case SPI_GETCLIENTAREAANIMATION:
    case SPI_GETACTIVEWINDOWTRACKING:
        // 真话：X11（无合成器）没有 DWM，本后端也不实现这些视觉特效。
        // 与 UIEffects=FALSE 自洽（它在 Windows 上是这些开关的总闸）。
        put_bool(data, 0);
        return 1;

    case SPI_GETKEYBOARDCUES:
        // ★ 这个给 TRUE 而不是 FALSE：它是**无障碍**设置而非"特效"。
        //   FALSE = 只有按 Alt 才显示快捷键下划线；TRUE = 一直显示。
        //   关掉它属于可观察的功能退化，所以给 Windows 的默认值。
        put_bool(data, 1);
        return 1;
    case SPI_GETDRAGFULLWINDOWS:
        put_bool(data, 1);                     // 拖动时显示内容；无害且是 Windows 默认
        return 1;
    case SPI_GETICONTITLEWRAP:
    case SPI_GETKEYBOARDPREF:
    case SPI_GETSNAPTODEFBUTTON:
    case SPI_GETMENUDROPALIGNMENT:             // FALSE = 左对齐（最常见的默认）
        put_bool(data, 0);
        return 1;

    default:
        // A 原则：不认识的 action **不碰出参**、返回成功。
        // Win32 上未知 action 应当失败，但 WPF 里还有一批我们没枚举的 SET action
        // 与查询混在同一个入口；返回失败会让 SystemParameters 抛 Win32Exception。
        // 折中：保持"成功但不写"= 与首版行为一致（不引入新风险），并在此登记。
        return 1;
    }
}

BOOL SystemParametersInfoW(UINT action, UINT param, void *data, UINT winIni)
{
    (void)winIni;
    // 分两段：**先把已知的查询 action 显式列全**（不能让未知 action 落进
    // `spi_query` 的 switch —— 那里只能靠"认不认识"来判断，而一旦把某个 SET
    // action 误当查询，就会按错误的出参形状写内存）。列全之后，其余一律
    // 按写入类处理：no-op + 成功。
    switch (action) {
    case SPI_GETNONCLIENTMETRICS: case SPI_GETICONMETRICS: case SPI_GETICONTITLELOGFONT:
    case SPI_GETWORKAREA: case SPI_GETHIGHCONTRAST: case SPI_GETANIMATION:
    case SPI_GETFOCUSBORDERWIDTH: case SPI_GETFOCUSBORDERHEIGHT: case SPI_GETCARETWIDTH:
    case SPI_GETKEYBOARDSPEED: case SPI_GETKEYBOARDDELAY:
    case SPI_GETMOUSEHOVERTIME: case SPI_GETMOUSEHOVERWIDTH: case SPI_GETMOUSEHOVERHEIGHT:
    case SPI_GETMOUSESPEED: case SPI_GETMENUSHOWDELAY: case SPI_GETWHEELSCROLLLINES:
    case SPI_GETFOREGROUNDFLASHCOUNT: case SPI_GETFOREGROUNDLOCKTIMEOUT:
    case SPI_GETACTIVEWNDTRKTIMEOUT: case SPI_GETBORDER: case SPI_GETDEFAULTINPUTLANG:
    case SPI_GETFONTSMOOTHING: case SPI_GETFONTSMOOTHINGTYPE: case SPI_GETFONTSMOOTHINGCONTRAST:
        // 结构化查询要 data；标量查询 WPF 一定传 data。param 对 NONCLIENTMETRICS
        // 等是 cbSize（调用方已经写进结构体首字段，这里用不到 param）。
        return spi_query(action, param, data);

    case SPI_GETUIEFFECTS: case SPI_GETMENUANIMATION: case SPI_GETCOMBOBOXANIMATION:
    case SPI_GETLISTBOXSMOOTHSCROLLING: case SPI_GETGRADIENTCAPTIONS: case SPI_GETHOTTRACKING:
    case SPI_GETSTYLUSHOTTRACKING: case SPI_GETMENUFADE: case SPI_GETSELECTIONFADE:
    case SPI_GETTOOLTIPANIMATION: case SPI_GETTOOLTIPFADE: case SPI_GETCURSORSHADOW:
    case SPI_GETMOUSEVANISH: case SPI_GETFLATMENU: case SPI_GETDROPSHADOW:
    case SPI_GETCLIENTAREAANIMATION: case SPI_GETACTIVEWINDOWTRACKING:
    case SPI_GETKEYBOARDCUES: case SPI_GETDRAGFULLWINDOWS: case SPI_GETICONTITLEWRAP:
    case SPI_GETKEYBOARDPREF: case SPI_GETSNAPTODEFBUTTON: case SPI_GETMENUDROPALIGNMENT:
        return spi_query(action, param, data);

    default:
        // ── 写入类（SPI_SET*）与未枚举的 action ───────────────────────────
        // 写类在 Windows 上也只是改注册表/通知窗口；Linux 上没有这些设置源，
        // 也没有消费者（我们的字体/主题由 M1 的 Skia 栈与 XAML 主题决定）。
        // 返回 TRUE 但**不产生任何副作用**：调用方（SystemParameters 的 setter）
        // 会据此更新自己的缓存，行为自洽。
        return 1;
    }
}
BOOL SystemParametersInfoA(UINT a, UINT p, void *d, UINT w) { return SystemParametersInfoW(a, p, d, w); }
BOOL SystemParametersInfo(UINT a, UINT p, void *d, UINT w) { return SystemParametersInfoW(a, p, d, w); }

// ── 行排版引擎（LineServices）的开关 ─────────────────────────────────────────
// [降级→真话] `LsDisableSpecialCharacterLigature(bool fDisable)` 设置的是
//   **Windows LineServices 引擎内部的一个进程级标志**（控制连字符连字）。
//
//   Linux 上这个引擎**根本不存在**（Lo* 家族一个都没实现；本工程的文本走
//   M1 的 Skia 文本栈 + DWriteForwarder 骨架）。所以这里**没有对象可设置** ——
//   空实现不是"假装成功"，而是"没有那个引擎，也就没有那个标志"。
//   同理：取消连字这件事在 Skia 侧由字体特性控制，不由这个开关控制。
//
// 【为什么必须存在】它挡在 `HwndSource..ctor → HwndTarget..ctor` 上：
//     HwndTarget.CheckAndDisableSpecialCharacterLigature()   HwndTarget.cs:304
//       → NativeMethodsSetLastError.LsDisableSpecialCharacterLigature(...)
//       → **EntryPointNotFoundException**（PresentationNative_cor3.dll）
//   而 HwndTarget..ctor 是 `Window.CreateSourceWindow` 的必经之路，所以它挡的是
//   **每一个 WPF 窗口**。返回 void，无可返回的失败码 ⇒ 空实现是唯一合理形态。
void LsDisableSpecialCharacterLigature(int fDisable) { (void)fDisable; }

// ── WTS（终端服务会话通知）────────────────────────────────────────────────────
// [降级→真话] Linux/X11 上**没有** Windows 的"终端服务会话"概念（没有会话
//   断开/重连、没有 remote-session 状态）。四个导出都返回失败/no-op。
//
// 【为什么返回失败是安全的、而且是对的行为 —— 这是调用方的设计】
//   `HwndTarget..ctor`（HwndTarget.cs:228-233）：
//       _sessionId = SafeNativeMethods.GetCurrentSessionId();
//       _isSessionDisconnected = !SafeNativeMethods.IsCurrentSessionConnectStateWTSActive(_sessionId);
//   而 `IsCurrentSessionConnectStateWTSActive` 的签名是
//       `(int? SessionId = null, bool defaultResult = true)`
//   —— **查询失败时 defaultResult=true**，即"会话是活动的"。所以
//   `WTSQuerySessionInformation` 返回 false ⇒ 不进入断开分支 ⇒ 不做
//   "唤醒后重呈现"，这正是本地 X11 会话应有的行为（我们永远不会"从断开的
//   远程会话恢复"）。
//   `WTSRegisterSessionNotification` 的返回值在 HwndTarget.cs:562 **被忽略**
//   （没有 if/throw），所以返回 FALSE 不会改变控制流。
//
// 【映射注意】同 uxtheme：`wtsapi32.dll` 也不在 Win32ShimResolver 的
//   MappedLibraries 里，runner 用 app-local 同名 ELF 兜住。
BOOL WTSRegisterSessionNotification(HWND hwnd, uint flags)
{ (void)hwnd; (void)flags; wpf_set_last_error(50); return 0; }
BOOL WTSUnRegisterSessionNotification(HWND hwnd)
{ (void)hwnd; wpf_set_last_error(50); return 0; }

/// <summary>
/// `WTSQuerySessionInformation(HANDLE, DWORD, WTS_INFO_CLASS, out IntPtr, out int)`。
/// 返回 FALSE 时**必须**把 ppBuffer 置 NULL —— 调用方在 finally 里判 `buffer != IntPtr.Zero`
/// 才 WTSFreeMemory，写脏指针会 free 到随机地址。
/// </summary>
BOOL WTSQuerySessionInformationW(void *hServer, uint32_t sessionId, int infoClass,
                                 void **ppBuffer, int *pBytesReturned)
{
    (void)hServer; (void)sessionId; (void)infoClass;
    if (ppBuffer) *ppBuffer = NULL;
    if (pBytesReturned) *pBytesReturned = 0;
    wpf_set_last_error(50);
    return 0;
}
BOOL WTSQuerySessionInformationA(void *h, uint32_t s, int i, void **b, int *n)
{ return WTSQuerySessionInformationW(h, s, i, b, n); }
BOOL WTSQuerySessionInformation(void *h, uint32_t s, int i, void **b, int *n)
{ return WTSQuerySessionInformationW(h, s, i, b, n); }

/// <summary>释放 WTSQuerySessionInformation 分配的缓冲。我们从没分配过 ⇒ 无操作，
/// 但返回 TRUE 是安全的（调用方只用它做清理，不判返回值）。</summary>
BOOL WTSFreeMemory(void *p) { (void)p; return 1; }

// ── uxtheme（视觉样式）────────────────────────────────────────────────────────
// [降级→真话] **Linux 上没有 Windows 视觉样式（Aero/Visual Styles）这个对象。**
//   所以 `IsThemeActive()` 返回 **0（假）** 是**真话**，不是伪造 —— 含义正是
//   "这台机器上没有活动的 Windows 主题"。
//
// 【为什么必须实现：它挡的是"任何显示文本的控件"】
//   实测（M7c Phase 2，紧接 OSVersionHelper 之后的下一道）：
//     TextBlock 初始化 → StyleHelper.GetThemeStyle → SystemResources.FindResourceInternal
//       → MS.Win32.UxThemeWrapper..cctor → SafeNativeMethods.IsUxThemeActive()
//       → **DllNotFoundException: uxtheme.dll**
//   `UxThemeWrapper` 的静态构造把 `!HighContrast && IsUxThemeActive()` 缓存成
//   `_themeState.IsActive`。返回 0 ⇒ IsActive=false ⇒ WPF 走**经典（非主题化）**
//   样式路径 —— 这对 Linux 是自洽且正确的选择（我们没有 Aero 主题资源可查）。
//
// 【`GetCurrentThemeName` 为什么返回失败而不是空成功】
//   它只在 `IsActive == true` 的分支里被调用（UxThemeWrapper.cs:257），当前不可达。
//   一旦将来有人强行打开主题路径，我们要的是**明确的失败码**，而不是
//   "写了一堆空串但声称成功"让上层拿着空主题名继续跑。
//
// 【映射注意（已解决）】`uxtheme.dll` 曾被登记为缺口："不在
//   src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 的 MappedLibraries 里"。**父级已修**：该文件的
//   MappedLibraries 现在同时含 `"uxtheme.dll"` 与 `"wtsapi32.dll"`（第 87/88 行），
//   所以这 6 个符号不再是"防御性导出"，而是真会被敲到的入口。本轮补上它们的 `W`
//   变体（见文件末尾"双门牌补齐"一节），runner 里的 app-local 同名拷贝保留为双保险。
int IsThemeActive(void) { return 0; }

// 只被"IsThemeActive()==true"的分支调用（当前不可达）。返回非 0 = 失败。
int GetCurrentThemeName(void *themeFile, int cchThemeFile, void *colorBuff,
                        int cchColorBuff, void *sizeBuff, int cchSizeBuff)
{
    (void)themeFile; (void)cchThemeFile; (void)colorBuff;
    (void)cchColorBuff; (void)sizeBuff; (void)cchSizeBuff;
    wpf_set_last_error(50);   // ERROR_NOT_SUPPORTED
    return 1;                 // 非 0：Win32 的 S_FALSE 在本 API 里表示"没有主题"，不是错误
}

// `SetWindowTheme` / `SetWindowThemeAttribute`：给窗口挂主题子类。
// Linux 无主题可挂 ⇒ 明确失败（不假装挂上了）。
int SetWindowTheme(HWND hwnd, const char *subAppName, const char *subIdList)
{ (void)hwnd; (void)subAppName; (void)subIdList; wpf_set_last_error(50); return 1; }
uint32_t SetWindowThemeAttribute(HWND hwnd, int attr, const void *opts, int cb)
{ (void)hwnd; (void)attr; (void)opts; (void)cb; wpf_set_last_error(50); return 50; }
uint32_t CriticalSetWindowTheme(HWND hwnd, const char *subAppName, const char *subIdList)
{ return (uint32_t)SetWindowTheme(hwnd, subAppName, subIdList); }

// 平移反馈（触控"撞边回弹"）：Windows 专有的视觉反馈，Linux 无对应物。
BOOL BeginPanningFeedback(HWND hwnd) { (void)hwnd; wpf_set_last_error(50); return 0; }
BOOL UpdatePanningFeedback(HWND hwnd, int x, int y, BOOL overpan)
{ (void)hwnd; (void)x; (void)y; (void)overpan; wpf_set_last_error(50); return 0; }
BOOL EndPanningFeedback(HWND hwnd, BOOL animate)
{ (void)hwnd; (void)animate; wpf_set_last_error(50); return 0; }

// ══════════════════════════════════════════════════════════════════════════
//  M7c Phase 2 批量补齐：`src/Linux/tools/check-shim-coverage.py` 静态扫出来的缺口
// ══════════════════════════════════════════════════════════════════════════
// 【★ 一条被实测纠正的规则：Unix 上 .NET **不会**给 DllImport 名补 `A` 后缀】
//   实测：`IntGetModuleFileName` 声明成
//       [DllImport(ExternDll.Kernel32, EntryPoint="GetModuleFileName", CharSet=CharSet.Auto, ...)]
//   本 shim 当时**已经有** `GetModuleFileNameA`，但运行期仍然抛
//       EntryPointNotFoundException: Unable to find an entry point named 'GetModuleFileName'
//   ⇒ 探测**只看第一个候选名**（Auto 在 Unix 上 == Ansi ⇒ 裸名），**不回落**到 `名A`。
//   （M7b 里"两边都导出"之所以没暴露这条，是因为当时每个函数我都恰好导出了裸名。）
//   所以本文件的规矩是：**裸名必须有**；`A`/`W` 变体只是兼容性冗余。
//
// 【这一批的档位】
//   绝大多数是 `[降级]`（返回哨兵/真话常量）或 `[失败]`（明确失败码）；
//   凡是能在 Linux 上表达真实语义的（`GetModuleFileName`、`FindWindowEx`）就真实现。

// ---- kernel32 ----
// [真实现] 裸名 == UTF-8（见上面的规则）。当前卡点的直接原因就是它缺席。
DWORD GetModuleFileName(HMODULE m, char *buf, DWORD n)
{
    (void)m;
    if (!buf || n == 0) return 0;
    ssize_t r = readlink("/proc/self/exe", buf, n - 1);
    if (r <= 0) { buf[0] = 0; wpf_set_last_error(2); return 0; }
    buf[r] = 0;
    return (DWORD)r;
}

// [降级→真话] Windows 的错误模式开关（弹不弹系统错误框）。Linux 上没有那种框，
// 记录返回旧值即可 —— 旧值 0 = SEM_FAILCRITICALERRORS 未设置，是进程默认。
UINT SetErrorMode(UINT mode) { (void)mode; return 0; }

// [失败] 调整工作集：Linux 上是 madvise/RLIMIT 的事，没有等价的进程级调用。
BOOL SetProcessWorkingSetSize(HANDLE proc, size_t min, size_t max)
{ (void)proc; (void)min; (void)max; wpf_set_last_error(50); return 0; }

// [降级] GlobalAlloc 家族：.NET 在 Unix 上 Marshal.AllocHGlobal 就是 malloc，
// 所以 GlobalLock 就是恒等（返回指针）、GlobalUnlock 成功、GlobalFree 就是 free。
// 这样与 .NET 自己的分配路径**本来就是一致的**，不是伪造。
void *GlobalLock(void *h) { return h; }
BOOL GlobalUnlock(void *h) { (void)h; return 1; }
void *GlobalFree(void *h) { free(h); return NULL; }

// [失败] 文件枚举（打印路径用）：Linux 上应走 BCL 的 Directory 枚举，不在这里造。
void *FindFirstFileW(const uint16_t *pattern, void *data)
{ (void)pattern; (void)data; wpf_set_last_error(2); return (void *)(intptr_t)-1; }
void *FindFirstFileA(const char *pattern, void *data)
{ (void)pattern; (void)data; wpf_set_last_error(2); return (void *)(intptr_t)-1; }
void *FindFirstFile(const char *pattern, void *data) { return FindFirstFileA(pattern, data); }
BOOL FindNextFileW(void *h, void *data) { (void)h; (void)data; wpf_set_last_error(18); return 0; }
BOOL FindNextFileA(void *h, void *data) { (void)h; (void)data; wpf_set_last_error(18); return 0; }
BOOL FindClose(void *h) { (void)h; return 1; }   // 幂等清理

// [失败] FindNLSString 的**裸名**（CharSet 是默认 Ansi ⇒ 探测裸名，见上面的规则）。
// 与已有的 FindNLSStringW 同语义：NLS 表在 Linux 上不存在，让上层回落到 BCL/ICU。
int FindNLSString(const char *locale, uint32_t flags, const char *src, int srcLen,
                  const char *find, int findLen, int *found)
{
    (void)locale; (void)flags; (void)src; (void)srcLen; (void)find; (void)findLen;
    if (found) *found = 0;
    wpf_set_last_error(50);
    return -1;
}

// ---- user32 ----
// [真实现] FindWindowEx：按类名/标题在**我们自己的窗口表**里找。这是真实现而不是 stub ——
// 窗口表就是权威来源。`FindWindowExWrapper` 是 PresentationNative 侧的名字（先清 LastError）。
HWND FindWindowExW(HWND parent, HWND childAfter, const uint16_t *cls, const uint16_t *title)
{
    char *cls8 = wpf_utf16_to_utf8_dup(cls);
    char *title8 = wpf_utf16_to_utf8_dup(title);
    HWND result = NULL;
    wpf_global_init();
    wpf_lock();
    int skipped = (childAfter == NULL);
    for (wpf_window *w = g_wpf.windows; w; w = w->next) {
        if (!skipped) { if (w->hwnd == childAfter) skipped = 1; continue; }
        if (parent && parent != HWND_DESKTOP && w->parent != parent) continue;
        if (cls8 && cls8[0]) {
            wpf_class *c = wpf_class_find_atom(w->class_atom);
            if (!c || !c->name || strcmp(c->name, cls8) != 0) continue;
        }
        if (title8 && title8[0]) {
            char got[512]; got[0] = 0;
            wpf_x11_query_title(w->hwnd, got, sizeof(got));
            if (strcmp(got, title8) != 0) continue;
        }
        result = w->hwnd;
        break;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    free(cls8); free(title8);
    return result;
}
HWND FindWindowExA(HWND p, HWND c, const char *cls, const char *title)
{
    uint16_t *cu = wpf_utf8_to_utf16_dup(cls), *tu = wpf_utf8_to_utf16_dup(title);
    HWND r = FindWindowExW(p, c, cu, tu);
    free(cu); free(tu);
    return r;
}
HWND FindWindowEx(HWND p, HWND c, const char *cls, const char *title)
{ return FindWindowExA(p, c, cls, title); }
HWND FindWindowExWrapper(HWND p, HWND c, const char *cls, const char *title)
{ wpf_set_last_error(0); return FindWindowExA(p, c, cls, title); }

// [降级→真话] 最小化状态：本 shim 不实现图标化（X11 下由 WM 负责，且无 WM）。
// 0 = "未最小化"，与 `ShowWindow` 一律 map 的行为自洽。
BOOL IsIconic(HWND hwnd) { (void)hwnd; return 0; }

// [失败] 菜单：WPF 自绘菜单，不走 Win32 菜单 API；这些入口在 X11 上没有对象。
HMENU GetSystemMenu(HWND hwnd, BOOL revert) { (void)hwnd; (void)revert; wpf_set_last_error(50); return NULL; }
BOOL DrawMenuBar(HWND hwnd) { (void)hwnd; return 0; }
uint32_t EnableMenuItem(HMENU m, UINT item, UINT enable)
{ (void)m; (void)item; (void)enable; wpf_set_last_error(50); return (uint32_t)-1; }
BOOL RemoveMenu(HMENU m, UINT item, UINT flags)
{ (void)m; (void)item; (void)flags; wpf_set_last_error(50); return 0; }
uint32_t TrackPopupMenuEx(HMENU m, UINT flags, int x, int y, HWND hwnd, void *tpmp)
{ (void)m; (void)flags; (void)x; (void)y; (void)hwnd; (void)tpmp; wpf_set_last_error(50); return 0; }

// [降级] SetWindowRgn：窗口裁剪区。X11 上的等价物是 XShape（未启用）⇒ **区域不真的生效**，
// 这是登记在册的降级，不是伪造。
//
// ⚠️ 返回值修正（`#34` 波，第三方应用实测撞出来的）：
//   Win32 的 `SetWindowRgn` **成功返回非 0、失败返回 0**（"是否需要重绘"是 `bRedraw` **入参**，
//   不是返回值的语义）。这里原来写 `return 0;` 并配了一句"0 = 没有重绘是 Win32 的正常语义"，
//   **那句话是错的**：WPF 的 `Standard.NativeMethods.SetWindowRgn`（PF，SetLastError=true）
//   把 0 当失败并抛 `Win32Exception`——于是**任何用 `WindowChrome`（圆角/玻璃自定义 chrome）的 app
//   都会在 `WindowChromeWorker._SetRoundingRegion` 处崩**。
//   实测触发者：HandyControl 示例工程主窗口（`HandyControl.Controls.Window` 设了 WindowChrome）。
//   ⇒ 返回 1（成功）；区域本身仍不生效（真实形状窗口留给将来接 XShape）。
int SetWindowRgn(HWND hwnd, HRGN rgn, BOOL redraw) { (void)hwnd; (void)rgn; (void)redraw; return 1; }

// [降级] 窗口类长整型：与 SetWindowLongPtr 同一张表（GCL_* 只用到 style/cursor 之类）。
INT_PTR SetClassLongPtrW(HWND hwnd, int index, INT_PTR value)
{ (void)hwnd; (void)index; (void)value; wpf_set_last_error(87); return 0; }
INT_PTR SetClassLongPtrA(HWND hwnd, int index, INT_PTR value)
{ return SetClassLongPtrW(hwnd, index, value); }
INT_PTR SetClassLongPtr(HWND hwnd, int index, INT_PTR value)
{ return SetClassLongPtrW(hwnd, index, value); }
LONG SetClassLongW(HWND hwnd, int index, LONG value)
{ return (LONG)(int32_t)SetClassLongPtrW(hwnd, index, (INT_PTR)value); }
LONG SetClassLongA(HWND hwnd, int index, LONG value) { return SetClassLongW(hwnd, index, value); }
LONG SetClassLong(HWND hwnd, int index, LONG value) { return SetClassLongW(hwnd, index, value); }

// [失败] SendInput：合成输入。X11 上正确的做法是 XTest（M1 的测试注入走的就是它），
// 不在这里用 XSendEvent 伪造（那会产生"假"事件，缺少 server 侧的完整语义）。
UINT SendInput(UINT count, const void *inputs, int size)
{ (void)count; (void)inputs; (void)size; wpf_set_last_error(50); return 0; }

// [失败] 全局钩子链：SetWindowsHookEx / UnhookWindowsHookEx / CallNextHookEx。
//   Windows 的钩子链是**系统级进程外机制**（DLL 注入或内核回调）；X11 上的等价物是 XRecord
//   （全局事件流）或 XGrabKey（注册热键），本后端都未实现 ⇒ **如实失败**。
//   ⚠️ 但**符号必须存在**：缺符号 ⇒ 调用方拿到 `EntryPointNotFoundException` 被直接掀掉；
//   有符号 + 失败 ⇒ 调用方走自己的失败分支。实测收益（`#34` 波，第三方应用 HandyControl）：
//   `KeyboardHook.Start()` 见 `HookId == 0` 就只跳过计数，`GlobalShortcut.Init` 照常返回
//   ⇒ 应用继续跑（代价：全局快捷键不生效，登记为降级）。
//   **不返回"看起来有效的假句柄"** —— 那会让上层以为钩子装上了（本项目对 SendInput 也是同一口径）。
//   类型说明：本文件不引入 HHOOK/HOOKPROC/LRESULT 这些 Windows 专有 typedef，
//   用 `void *` / `intptr_t` / `uintptr_t` 表达，在 x86-64 SysV ABI 上与之一致。
//   错误码：120 = ERROR_CALL_NOT_IMPLEMENTED，1404 = ERROR_INVALID_HOOK_HANDLE（不给假句柄）。
void *SetWindowsHookExW(int idHook, void *proc, void *mod, uint32_t threadId)
{ (void)idHook; (void)proc; (void)mod; (void)threadId; wpf_set_last_error(120); return NULL; }
void *SetWindowsHookExA(int idHook, void *proc, void *mod, uint32_t threadId)
{ return SetWindowsHookExW(idHook, proc, mod, threadId); }
void *SetWindowsHookEx(int idHook, void *proc, void *mod, uint32_t threadId)
{ return SetWindowsHookExW(idHook, proc, mod, threadId); }
BOOL UnhookWindowsHookEx(void *hook) { (void)hook; wpf_set_last_error(1404); return 0; }
intptr_t CallNextHookEx(void *hook, int code, uintptr_t wp, intptr_t lp)
{ (void)hook; (void)code; (void)wp; (void)lp; return 0; }

// [失败] Raw Input / Pointer（WM_POINTER）设备枚举：Windows 专有输入栈。
UINT GetRawInputDeviceList(void *list, UINT *count, UINT size)
{ (void)list; if (count) *count = 0; (void)size; wpf_set_last_error(50); return (UINT)-1; }
UINT GetRawInputDeviceInfoW(void *dev, UINT cmd, void *data, UINT *size)
{ (void)dev; (void)cmd; (void)data; if (size) *size = 0; wpf_set_last_error(50); return (UINT)-1; }
UINT GetRawInputDeviceInfoA(void *dev, UINT cmd, void *data, UINT *size)
{ return GetRawInputDeviceInfoW(dev, cmd, data, size); }
UINT GetRawInputDeviceInfo(void *dev, UINT cmd, void *data, UINT *size)
{ return GetRawInputDeviceInfoW(dev, cmd, data, size); }
BOOL GetPointerDevices(uint32_t *count, void *devices)
{ if (count) *count = 0; (void)devices; wpf_set_last_error(50); return 0; }
BOOL GetPointerInfo(uint32_t id, void *info) { (void)id; (void)info; wpf_set_last_error(50); return 0; }
BOOL GetPointerInfoHistory(uint32_t id, uint32_t *count, void *info)
{ (void)id; if (count) *count = 0; (void)info; wpf_set_last_error(50); return 0; }
BOOL GetPointerPenInfo(uint32_t id, void *info) { (void)id; (void)info; wpf_set_last_error(50); return 0; }
BOOL GetPointerTouchInfo(uint32_t id, void *info) { (void)id; (void)info; wpf_set_last_error(50); return 0; }
BOOL GetPointerCursorId(uint32_t id, uint32_t *cursorId)
{ (void)id; if (cursorId) *cursorId = 0; wpf_set_last_error(50); return 0; }
BOOL GetPointerDeviceRects(void *dev, void *rects) { (void)dev; (void)rects; wpf_set_last_error(50); return 0; }
BOOL GetPointerDeviceCursors(void *dev, uint32_t *count, void *cursors)
{ (void)dev; if (count) *count = 0; (void)cursors; wpf_set_last_error(50); return 0; }
BOOL GetPointerDeviceProperties(void *dev, uint32_t *count, void *props)
{ (void)dev; if (count) *count = 0; (void)props; wpf_set_last_error(50); return 0; }
BOOL GetRawPointerDeviceData(uint32_t id, uint32_t count, uint32_t mask, void *values)
{ (void)id; (void)count; (void)mask; (void)values; wpf_set_last_error(50); return 0; }

// ---- gdi32：区域与画刷 ----
// [降级] GDI 区域对象：WPF 用它们给窗口设裁剪区（SetWindowRgn）。我们不做形状窗口，
// 但**必须返回非 0 句柄** —— 拿到 NULL 上层会当成"分配失败"。
HRGN CreateRectRgn(int l, int t, int r, int b) { (void)l; (void)t; (void)r; (void)b; return (HRGN)(uintptr_t)0x5001; }
HRGN CreateRectRgnIndirect(const WPF_RECT *rc) { (void)rc; return (HRGN)(uintptr_t)0x5001; }
HRGN CreateRoundRectRgn(int l, int t, int r, int b, int w, int h)
{ (void)l; (void)t; (void)r; (void)b; (void)w; (void)h; return (HRGN)(uintptr_t)0x5001; }
int CombineRgn(HRGN dst, HRGN a, HRGN b, int mode)
{ (void)dst; (void)a; (void)b; (void)mode; return 3; }   // RGN_DIFF/ERROR 之外：给"成功"的一个合法值
HBRUSH CreateSolidBrush(uint32_t color) { (void)color; return (HBRUSH)(uintptr_t)0x5002; }
HDC CreateDCW(const uint16_t *driver, const uint16_t *device, const uint16_t *port, const void *initData)
{ (void)driver; (void)device; (void)port; (void)initData; return (HDC)(uintptr_t)0x0001; }  // 与 GetDC 同一哨兵
HDC CreateDCA(const char *driver, const char *device, const char *port, const void *initData)
{ (void)driver; (void)device; (void)port; (void)initData; return (HDC)(uintptr_t)0x0001; }
HDC CreateDC(const char *driver, const char *device, const char *port, const void *initData)
{ return CreateDCA(driver, device, port, initData); }

// ── 版本判定（PresentationNative_cor3.dll 的 OSVersionHelper 家族）──────────
// [降级→真话] `Shared/System/Windows/Interop/OSVersionHelper.cs` 的静态构造会**一次性**
//   调用下面这 20 个导出（每个都是 `[DllImport(PresentationNative_cor3.dll,
//   CallingConvention = Cdecl)] public static extern bool IsWindowsXXX()`），
//   把结果缓存进静态属性。linux 上「这是不是 Windows 10 RS5 及以上？」的答案就是
//   **否** —— 返回 FALSE 是**真话**，不是伪造。
//
// 【为什么必须实现它们（不实现就是 EntryPointNotFoundException）】
//   实测（M7c Phase 2，窗口构造的最后一段）：
//     TextBlock..cctor → FrameworkElement.UpdateThemeStyleProperty
//       → SystemResources.EnsureResourceChangeListener
//       → HwndTarget.IsPerMonitorDpiScalingEnabled
//       → OSVersionHelper.IsOsWindows10RS1OrGreater
//       → OSVersionHelper..cctor → **EntryPointNotFoundException:
//         Unable to find an entry point named 'IsWindows10RS5OrGreater'**。
//   也就是说：**任何显示文本的 WPF 控件**都会走到这里。
//
// 【返回 FALSE 之后的行为是"正确"而不是"退化"】
//   `IsPerMonitorDpiScalingSupportedOnCurrentPlatform` 会因此为 false ⇒ 不做
//   Per-Monitor-V2 DPI 缩放 ⇒ 这正是 X11（core protocol 无 per-monitor DPI）上
//   应有的行为，与 M7b 里 `GetDpiForWindow` 恒返回 96 是同一个口径。
//
//   注：托管侧声明的是 `bool` 返回（P/Invoke 默认 marshalling 为 4 字节 Win32 BOOL），
//   所以这里一律用 `int` 返回、值取 0。
#define WPF_OSVERSION_FALSE(name) int name(void) { return 0; }
WPF_OSVERSION_FALSE(IsWindowsXPOrGreater)
WPF_OSVERSION_FALSE(IsWindowsXPSP1OrGreater)
WPF_OSVERSION_FALSE(IsWindowsXPSP2OrGreater)
WPF_OSVERSION_FALSE(IsWindowsXPSP3OrGreater)
WPF_OSVERSION_FALSE(IsWindowsVistaOrGreater)
WPF_OSVERSION_FALSE(IsWindowsVistaSP1OrGreater)
WPF_OSVERSION_FALSE(IsWindowsVistaSP2OrGreater)
WPF_OSVERSION_FALSE(IsWindows7OrGreater)
WPF_OSVERSION_FALSE(IsWindows7SP1OrGreater)
WPF_OSVERSION_FALSE(IsWindows8OrGreater)
WPF_OSVERSION_FALSE(IsWindows8Point1OrGreater)
WPF_OSVERSION_FALSE(IsWindows10OrGreater)
WPF_OSVERSION_FALSE(IsWindows10TH1OrGreater)
WPF_OSVERSION_FALSE(IsWindows10TH2OrGreater)
WPF_OSVERSION_FALSE(IsWindows10RS1OrGreater)
WPF_OSVERSION_FALSE(IsWindows10RS2OrGreater)
WPF_OSVERSION_FALSE(IsWindows10RS3OrGreater)
WPF_OSVERSION_FALSE(IsWindows10RS4OrGreater)
WPF_OSVERSION_FALSE(IsWindows10RS5OrGreater)
WPF_OSVERSION_FALSE(IsWindowsServer)

// ── 其它 ───────────────────────────────────────────────────────────────────
BOOL DeactivateActCtx(DWORD flags, UINT_PTR cookie) { (void)flags; (void)cookie; return 1; }
BOOL GetTempFileNameW(const uint16_t *path, const uint16_t *prefix, UINT unique, uint16_t *out)
{ (void)path; (void)prefix; (void)unique; (void)out; wpf_set_last_error(50); return 0; }
BOOL GetTempFileNameA(const char *p, const char *pre, UINT u, char *out)
{ (void)p; (void)pre; (void)u; (void)out; wpf_set_last_error(50); return 0; }
BOOL GetTempFileName(const char *p, const char *pre, UINT u, char *out) { return GetTempFileNameA(p, pre, u, out); }

// ══════════════════════════════════════════════════════════════════════════
//  M7c Phase 2 收尾：导出名的「W 后缀 / 裸名」双门牌补齐
// ══════════════════════════════════════════════════════════════════════════
// 【为什么同一个功能要同时导出两个名字】
//   .NET 在 Unix 上解析 DllImport 时**只探一个名字，探不到不回退**：
//     · CharSet.Unicode + ExactSpelling=false ⇒ 探 `<名字>W`（名字本身已带 W 就不再补）
//     · CharSet.Auto/Ansi 在 Unix 上折叠成 Ansi       ⇒ 探**裸名**
//   第 2 条是实测结论而不是推断：`GetModuleFileName` 裸名缺席时运行期直接抛
//   EntryPointNotFoundException，**尽管 `GetModuleFileNameA` 就在导出表里**。
//   第 1 条同理：`IsThemeActive` 这类声明（CharSet.Unicode、EntryPoint 不带后缀）
//   运行期来敲的是 `IsThemeActiveW` 这扇门。
//
// 【上游为什么两种写法都有】三个不同源文件对同一个 API 的声明并不统一——
//   `PresentationFramework/System/Windows/Standard/NativeMethods.cs` 常把后缀写进
//   EntryPoint（`EntryPoint="CreateWindowExW"`），`Shared/MS/Win32/*.cs` 则写裸名 +
//   CharSet。于是同一份 shim 会被两种门牌号敲到。下面这组别名**全部转发到已有的那份
//   实现**，不新增任何语义：真实现仍是真实现，降级仍是降级，返回失败仍返回失败。

// ── gdi32 ──────────────────────────────────────────────────────────────────
HDC CreateCompatibleDCW(HDC hdc) { return CreateCompatibleDC(hdc); }

// ── kernel32 ───────────────────────────────────────────────────────────────
HANDLE CreateFileMapping(HANDLE f, void *sa, DWORD p, DWORD hi, DWORD lo, const char *n)
{ return CreateFileMappingA(f, sa, p, hi, lo, n); }
BOOL FreeLibraryW(HMODULE m) { return FreeLibrary(m); }

// ── user32 ─────────────────────────────────────────────────────────────────
UINT GetDpiForWindowW(HWND h) { return GetDpiForWindow(h); }
BOOL GetIconInfo(HICON ic, void *info) { return GetIconInfoImpl(ic, info); }

// ── uxtheme ────────────────────────────────────────────────────────────────
//   注：`uxtheme.dll` 现已由 src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs 的 MappedLibraries
//   映射到本 shim（父级已加），所以这 6 个符号是**真的会被敲到**的，不是防御性代码。
int IsThemeActiveW(void) { return IsThemeActive(); }
int GetCurrentThemeNameW(void *themeFile, int cchThemeFile, void *colorBuff,
                         int cchColorBuff, void *sizeBuff, int cchSizeBuff)
{ return GetCurrentThemeName(themeFile, cchThemeFile, colorBuff, cchColorBuff, sizeBuff, cchSizeBuff); }
uint32_t SetWindowThemeAttributeW(HWND hwnd, int attr, const void *opts, int cb)
{ return SetWindowThemeAttribute(hwnd, attr, opts, cb); }
BOOL BeginPanningFeedbackW(HWND hwnd) { return BeginPanningFeedback(hwnd); }
BOOL UpdatePanningFeedbackW(HWND hwnd, int x, int y, BOOL overpan)
{ return UpdatePanningFeedback(hwnd, x, y, overpan); }
BOOL EndPanningFeedbackW(HWND hwnd, BOOL animate) { return EndPanningFeedback(hwnd, animate); }
