// 【ACC-028 / K-144】真 HDC 基础设施 —— 见 win32_gdi_dc.h 的说明。
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "win32_internal.h"
#include "win32_gdi_dc.h"

#ifndef FALSE
#define FALSE 0
#endif

// HDC：绑到某个窗口的 X11 Window + GC（K-144）
struct wpf_hdc {
    struct wpf_hdc *next;
    HWND        hwnd;           // 0 = 离屏（CreateCompatibleDC）
    Drawable    drawable;
    GC          gc;
    Pixmap      pixmap;         // 离屏时非 0
    int32_t     width, height;
    uint32_t    text_color;     // COLORREF 0x00BBGGRR
    uint32_t    bk_color;
    int32_t     bk_mode;        // 1=OPAQUE 2=TRANSPARENT
    HBRUSH      cur_brush;
    HPEN        cur_pen;
    HFONT       cur_font;
};

// GDI 对象（brush/pen）：只需记住颜色（最小实现够用）
typedef struct wpf_gdi_obj {
    struct wpf_gdi_obj *next;
    int      kind;      // 1=brush 2=pen 3=font
    uint32_t color;     // COLORREF
    int32_t  width;
    int      style;
} wpf_gdi_obj;

static wpf_hdc     *g_hdcs = NULL;
static wpf_gdi_obj *g_objs = NULL;

// COLORREF（0x00BBGGRR）→ X11 像素（0xRRGGBB）
uint32_t wpf_colorref_to_x(uint32_t c)
{
    if (c == 0xFFFFFFFFu) return 0x000000;      // CLR_INVALID → 黑
    return ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
}

// UTF-16 → UTF-8（含代理对）。返回写入字节数。
int wpf_utf16_to_utf8(const uint16_t *s, int32_t len, char *out, int outsz)
{
    if (!s || !out || outsz <= 0) return 0;
    if (len < 0) { len = 0; while (s[len]) len++; }
    int n = 0;
    for (int32_t i = 0; i < len; ++i) {
        uint32_t cp = s[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < len) {
            uint32_t lo = s[i + 1];
            if (lo >= 0xDC00 && lo <= 0xDFFF) { cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00); ++i; }
        }
        int need = cp < 0x80 ? 1 : (cp < 0x800 ? 2 : (cp < 0x10000 ? 3 : 4));
        if (n + need >= outsz) break;
        if (need == 1) out[n++] = (char)cp;
        else if (need == 2) { out[n++] = (char)(0xC0 | (cp >> 6)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
        else if (need == 3) { out[n++] = (char)(0xE0 | (cp >> 12)); out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (cp >> 18)); out[n++] = (char)(0x80 | ((cp >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[n++] = (char)(0x80 | (cp & 0x3F)); }
    }
    out[n] = 0;
    return n;
}

// ── GDI 对象 ────────────────────────────────────────────────────────────────
wpf_gdi_obj *wpf_gdi_alloc(int kind, uint32_t color, int32_t width, int style)
{
    wpf_gdi_obj *o = (wpf_gdi_obj *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->kind = kind; o->color = color; o->width = width; o->style = style;
    wpf_lock();
    o->next = g_objs; g_objs = o;
    wpf_unlock();
    return o;
}

wpf_gdi_obj *wpf_gdi_of(HGDIOBJ o)
{
    if (!o) return NULL;
    // ① 精确匹配
    for (wpf_gdi_obj *p = g_objs; p; p = p->next)
        if (p == (wpf_gdi_obj *)o) return p;

    // 【ACC-028 / K-159】**同样的低 32 位兜底**。
    //   `HBRUSH`/`HPEN` 也是 `void*`，过托管边界时同样被 `(int)` 截断（K-158）。
    //   实测证据：修好 HDC 之后 `[GDI] FillRect` 的 `color` 仍是 0xffffff（默认值），
    //   说明 `wpf_gdi_of(br)` 返回 NULL ⇒ brush 没匹配上。
    //   安全约束：命中多于一个返回 NULL。
    {
        uint32_t lo = (uint32_t)(uintptr_t)o;
        wpf_gdi_obj *hit = NULL; int n = 0;
        for (wpf_gdi_obj *p = g_objs; p; p = p->next) {
            if ((uint32_t)(uintptr_t)p == lo) { hit = p; ++n; }
        }
        if (n == 1) return hit;
    }
    return NULL;
}

static void wpf_gdi_free(wpf_gdi_obj *target)
{
    wpf_gdi_obj **pp = &g_objs;
    while (*pp) { if (*pp == target) { *pp = target->next; free(target); return; } pp = &(*pp)->next; }
}

// ── HDC ────────────────────────────────────────────────────────────────────
static wpf_hdc *wpf_hdc_new(void)
{
    wpf_hdc *h = (wpf_hdc *)calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->text_color = 0x000000;   // COLORREF 黑
    h->bk_color   = 0xFFFFFF;   // 白
    h->bk_mode    = 2;          // TRANSPARENT（WinForms 默认对文字如此更安全）
    wpf_lock();
    h->next = g_hdcs; g_hdcs = h;
    wpf_unlock();
    return h;
}

// 在 Display 上建 GC；失败返回 0
static int wpf_hdc_make_gc(wpf_hdc *h)
{
    if (!g_wpf.dpy || h->drawable == 0) return 0;
    h->gc = XCreateGC(g_wpf.dpy, h->drawable, 0, NULL);
    if (!h->gc) return 0;
    XSetForeground(g_wpf.dpy, h->gc, wpf_colorref_to_x(h->text_color));
    XSetBackground(g_wpf.dpy, h->gc, wpf_colorref_to_x(h->bk_color));
    // 字体：用 "fixed"（几乎必有）；失败就不设，XDrawString 用默认
    XFontStruct *fs = XLoadQueryFont(g_wpf.dpy, "fixed");
    if (fs) XSetFont(g_wpf.dpy, h->gc, fs->fid);
    return 1;
}

wpf_hdc *wpf_hdc_get(HWND hwnd)
{
    if (!hwnd) return NULL;
    wpf_lock();
    for (wpf_hdc *h = g_hdcs; h; h = h->next) {
        if (h->hwnd == hwnd && h->drawable != 0) { wpf_unlock(); return h; }
    }
    wpf_unlock();

    wpf_window *w = wpf_window_find(hwnd);
    if (!w) return NULL;

    wpf_hdc *h = wpf_hdc_new();
    if (!h) return NULL;
    h->hwnd = hwnd;
    h->drawable = (Drawable)(uintptr_t)hwnd;   // shim 里 HWND 就是 X11 Window
    h->width  = w->width;
    h->height = w->height;
    if (!wpf_hdc_make_gc(h)) {
        if (getenv("WPF_LINUX_KEY_DIAG")) { fprintf(stderr, "[GDI] wpf_hdc_get hwnd=%p 建 GC 失败\n", (void*)hwnd); fflush(stderr); }
        wpf_hdc_destroy(h); return NULL;
    }
    if (getenv("WPF_LINUX_KEY_DIAG")) {
        static int n = 0;
        if (n++ < 6) { fprintf(stderr, "[GDI] wpf_hdc_get hwnd=%p -> drawable=0x%lx gc=%p %dx%d\n",
            (void*)hwnd, (unsigned long)h->drawable, (void*)h->gc, h->width, h->height); fflush(stderr); }
    }
    return h;
}

wpf_hdc *wpf_hdc_compat(HDC src)
{
    wpf_hdc *s = wpf_hdc_of(src);
    int32_t w = s ? s->width : 1, hgt = s ? s->height : 1;
    if (w <= 0) w = 1; if (hgt <= 0) hgt = 1;
    if (!g_wpf.dpy) return NULL;

    wpf_hdc *h = wpf_hdc_new();
    if (!h) return NULL;
    h->hwnd = 0;
    h->pixmap = XCreatePixmap(g_wpf.dpy, g_wpf.root, (unsigned)w, (unsigned)hgt,
                              (unsigned)DefaultDepth(g_wpf.dpy, DefaultScreen(g_wpf.dpy)));
    if (!h->pixmap) { wpf_hdc_destroy(h); return NULL; }
    h->drawable = h->pixmap;
    h->width = w; h->height = hgt;
    if (s) { h->text_color = s->text_color; h->bk_color = s->bk_color; h->bk_mode = s->bk_mode; }
    if (!wpf_hdc_make_gc(h)) { wpf_hdc_destroy(h); return NULL; }
    return h;
}

wpf_hdc *wpf_hdc_of(HDC dc)
{
    if (!dc) return NULL;
    // ① 精确匹配（正常路径）
    for (wpf_hdc *h = g_hdcs; h; h = h->next)
        if (h == (wpf_hdc *)dc) return h;

    // 【ACC-028 / K-159】**兜底：按低 32 位再匹配一次**。
    //   依据（K-158 实测）：
    //     shim 的 GetDC 返回 0x55cdeb86b690，
    //     托管侧传给 FillRect 的却是 0xffffffffeb86b690 ——
    //   这是 `(int)` 截断 + 符号扩展，**低 32 位完全一致**。
    //   ⇒ 高 32 位已经丢了、找不回来；但低 32 位足以在链表里唯一定位。
    //   安全约束：**命中多于一个就返回 NULL**（宁可 no-op，也不要画错窗口）。
    {
        uint32_t lo = (uint32_t)(uintptr_t)dc;
        wpf_hdc *hit = NULL; int n = 0;
        for (wpf_hdc *h = g_hdcs; h; h = h->next) {
            if ((uint32_t)(uintptr_t)h == lo) { hit = h; ++n; }
        }
        if (n == 1) {
            if (getenv("WPF_LINUX_KEY_DIAG")) {
                static int logged = 0;
                if (logged++ < 6) {
                    fprintf(stderr, "[HDC] 低32位兜底命中 dc=%p -> %p\n", (void *)dc, (void *)hit);
                    fflush(stderr);
                }
            }
            return hit;
        }
        if (n > 1 && getenv("WPF_LINUX_KEY_DIAG")) {
            fprintf(stderr, "[HDC] 低32位歧义 dc=%p（命中 %d 个）-> 返回 NULL\n", (void *)dc, n);
            fflush(stderr);
        }
    }
    return NULL;
}

void wpf_hdc_release(wpf_hdc *h)
{
    if (!h || !g_wpf.dpy) return;
    XFlush(g_wpf.dpy);   // 关键：把绘制命令真正发出去
}

void wpf_hdc_destroy(wpf_hdc *h)
{
    if (!h) return;
    if (g_wpf.dpy && h->gc)      XFreeGC(g_wpf.dpy, h->gc);
    if (g_wpf.dpy && h->pixmap)  XFreePixmap(g_wpf.dpy, h->pixmap);
    wpf_lock();
    for (wpf_hdc **pp = &g_hdcs; *pp; pp = &(*pp)->next) {
        if (*pp == h) { *pp = h->next; break; }
    }
    wpf_unlock();
    free(h);
}

void wpf_hdc_destroy_all(void)
{
    wpf_lock();
    wpf_hdc *h = g_hdcs; g_hdcs = NULL;
    wpf_gdi_obj *o = g_objs; g_objs = NULL;
    wpf_unlock();
    while (h) { wpf_hdc *n = h->next;
        if (g_wpf.dpy && h->gc) XFreeGC(g_wpf.dpy, h->gc);
        if (g_wpf.dpy && h->pixmap) XFreePixmap(g_wpf.dpy, h->pixmap);
        free(h); h = n; }
    while (o) { wpf_gdi_obj *n = o->next; free(o); o = n; }
}

// ── 对外绘制封装（调用方只需 HDC）────────────────────────────────────────────
int wpf_draw_set_text_color(HDC dc, uint32_t c) { wpf_hdc *d = wpf_hdc_of(dc); if (!d) return 0; d->text_color = c; return 1; }
int wpf_draw_set_bk_color(HDC dc, uint32_t c)   { wpf_hdc *d = wpf_hdc_of(dc); if (!d) return 0; d->bk_color = c;   return 1; }
int wpf_draw_set_bk_mode(HDC dc, int m)         { wpf_hdc *d = wpf_hdc_of(dc); if (!d) return 0; d->bk_mode = m;    return 1; }
int wpf_draw_flush(HDC dc) { wpf_hdc *d = wpf_hdc_of(dc); if (!d) return 0; wpf_hdc_release(d); return 1; }

int wpf_draw_fill_rect(HDC dc, const WPF_RECT *rc, uint32_t colorref)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (getenv("WPF_LINUX_KEY_DIAG")) {
        static int n = 0;
        if (n++ < 10) { fprintf(stderr, "[GDI] FillRect dc=%p hdc=%p rect=%s color=0x%06x\n",
            (void*)dc, (void*)d, rc ? "ok" : "null", colorref); fflush(stderr); }
    }
    if (!d || !rc || !g_wpf.dpy) return 0;
    int w = rc->right - rc->left, h = rc->bottom - rc->top;
    if (w <= 0 || h <= 0) return 0;
    XSetForeground(g_wpf.dpy, d->gc, wpf_colorref_to_x(colorref));
    XFillRectangle(g_wpf.dpy, d->drawable, d->gc, rc->left, rc->top, (unsigned)w, (unsigned)h);
    return 1;
}

int wpf_draw_frame_rect(HDC dc, const WPF_RECT *rc, uint32_t colorref)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (!d || !rc || !g_wpf.dpy) return 0;
    int w = rc->right - rc->left, h = rc->bottom - rc->top;
    if (w <= 0 || h <= 0) return 0;
    XSetForeground(g_wpf.dpy, d->gc, wpf_colorref_to_x(colorref));
    XDrawRectangle(g_wpf.dpy, d->drawable, d->gc, rc->left, rc->top, (unsigned)(w - 1), (unsigned)(h - 1));
    return 1;
}

int wpf_draw_line(HDC dc, int32_t x1, int32_t y1, int32_t x2, int32_t y2, uint32_t colorref)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (!d || !g_wpf.dpy) return 0;
    XSetForeground(g_wpf.dpy, d->gc, wpf_colorref_to_x(colorref));
    XDrawLine(g_wpf.dpy, d->drawable, d->gc, x1, y1, x2, y2);
    return 1;
}

int wpf_draw_text(HDC dc, int32_t x, int32_t y, const uint16_t *s, int32_t len)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (!d || !s || !g_wpf.dpy) return 0;
    char buf[1024];
    int n = wpf_utf16_to_utf8(s, len, buf, (int)sizeof(buf));
    if (n <= 0) return 0;
    XSetForeground(g_wpf.dpy, d->gc, wpf_colorref_to_x(d->text_color));
    if (d->bk_mode == 1) XSetBackground(g_wpf.dpy, d->gc, wpf_colorref_to_x(d->bk_color));
    XDrawString(g_wpf.dpy, d->drawable, d->gc, x, y, buf, n);
    return 1;
}

int wpf_draw_select_object(HDC dc, HGDIOBJ obj, int kind)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (!d) return 0;
    if (kind == 1)      d->cur_brush = (HBRUSH)obj;
    else if (kind == 2) d->cur_pen   = (HPEN)obj;
    else if (kind == 3) d->cur_font  = (HFONT)obj;
    return 1;
}

int wpf_gdi_kind(HGDIOBJ o) { wpf_gdi_obj *p = wpf_gdi_of(o); return p ? p->kind : 0; }

int wpf_draw_text_bg(HDC dc, const WPF_RECT *rc)
{
    wpf_hdc *d = wpf_hdc_of(dc);
    if (!d || !rc || d->bk_mode != 1) return 0;   // 只有 OPAQUE 才填
    return wpf_draw_fill_rect(dc, rc, d->bk_color);
}

// 【ACC-028 / K-146】给托管侧（自编 System.Drawing.Common）的查询入口：
//   `Graphics.FromHdc(hdc)` 拿到的是我们 shim 的真 HDC（绑 X11 Window+GC），
//   但 libgdiplus 的 `GdipCreateFromHDC` **不认识它** ⇒ 要转成 `FromXDrawable(hwnd, display)`。
//   本函数返回该 HDC 绑定的 hwnd（X11 Window），不是我们的 HDC 就返回 0。
void *wpf_hdc_hwnd(void *hdc)
{
    wpf_hdc *d = wpf_hdc_of((HDC)hdc);
    return d ? (void *)(uintptr_t)d->hwnd : NULL;
}

// 同理：返回该 HDC 的 X11 Drawable（离屏 DC 时是 Pixmap）
void *wpf_hdc_drawable(void *hdc)
{
    wpf_hdc *d = wpf_hdc_of((HDC)hdc);
    return d ? (void *)(uintptr_t)d->drawable : NULL;
}

HGDIOBJ wpf_gdi_new_brush(uint32_t colorref) { return (HGDIOBJ)wpf_gdi_alloc(1, colorref, 0, 0); }
HGDIOBJ wpf_gdi_new_pen(uint32_t colorref, int32_t width, int style) { return (HGDIOBJ)wpf_gdi_alloc(2, colorref, width, style); }
uint32_t wpf_gdi_color(HGDIOBJ o, uint32_t dflt) { wpf_gdi_obj *p = wpf_gdi_of(o); return p ? p->color : dflt; }

// ── 【清算 / K-201】`BitBlt` —— 用 X11 的 `XCopyArea` 真实现 ────────────────────
//   `BitBlt(HDC hdcDest, int x, int y, int w, int h, HDC hdcSrc, int x1, int y1, DWORD rop)`
//   我们的 HDC 背后就是 X11 `Window`/`Pixmap` + `GC`（K-144）⇒ 直接 `XCopyArea`。
//   只支持 `SRCCOPY`(0x00CC0020) 与其等价 ROP；其它 ROP 返回 FALSE（不猜）。
BOOL BitBlt(HDC hdcDest, int32_t x, int32_t y, int32_t w, int32_t h,
            HDC hdcSrc, int32_t x1, int32_t y1, uint32_t rop)
{
    wpf_hdc *d = wpf_hdc_of(hdcDest);
    wpf_hdc *s = wpf_hdc_of(hdcSrc);
    if (!d || !s || w <= 0 || h <= 0) return 0;
    if (rop != 0x00CC0020u) return 0;   // 只做 SRCCOPY
    Drawable src = s->drawable ? s->drawable : s->pixmap;
    Drawable dst = d->drawable ? d->drawable : d->pixmap;
    if (!src || !dst) return 0;
    if (g_wpf.dpy) {
        XCopyArea(g_wpf.dpy, src, dst, d->gc, x1, y1, (unsigned)w, (unsigned)h, x, y);
        XFlush(g_wpf.dpy);
        return 1;
    }
    return 0;
}

// ── 【清算 / K-202】`GdipCreateHICONFromBitmap` —— 如实降级，但**必须导出** ─────
//   它是 GDI+ 的函数（`gdiplus.dll`），签名 `GpStatus GdipCreateHICONFromBitmap(GpBitmap*, HICON*)`。
//   **为什么不做真实现**：参数 `GpBitmap*` 是 **libgdiplus 的内部结构**（不是 Win32 的 HBITMAP），
//   它的像素数据布局没有公开文档；而 HICON 在 Linux 上也没有对应物（图标是窗口管理器的事）。
//   ⇒ 要把 bitmap 像素取出来需要 `GdipGetImageWidth/Height` + `GdipBitmapLockBits`
//     （这两个 libgdiplus 都有），再做一张 X11 `Pixmap` —— **技术上可行，但收益极低**：
//     本仓没有任何调用方（`grep` 上游无引用），且 WPF 的图标走 `BitmapFrame`/`IconBitmapDecoder`，
//     不走这条 GDI+ 路径。
//   **⇒ 折中：导出 + 返回 `GdiplusNotImplemented(1)`**，
//     这满足「不可实现路径必须返回明确错误码」的硬约束，也不会让调用方崩。
//   如果将来真需要：`GdipGetImageWidth` + `GdipBitmapLockBits` 取像素 → `XCreatePixmap` +
//   `XPutImage` → 把 Pixmap 当 HICON 返回（本 shim 的 HICON 约定本来就是 X11 Pixmap）。
int32_t GdipCreateHICONFromBitmap(void *bitmap, void **hIcon)
{
    (void)bitmap;
    if (hIcon) *hIcon = NULL;
    return 1;   // GdiplusNotImplemented
}
