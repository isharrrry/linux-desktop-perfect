// ── 【清算 / K-177】用 libgdiplus（cairo）渲染 shim 的填充 ───────────────────
//
// 为什么有这个文件：K-173 用独立程序实证了 `libgdiplus 6.0.4` 的这条链在 X11 上完全可用：
//     GdipCreateFromXDrawable_linux(win, dpy, &g) -> 0
//     GdipCreateSolidFill(argb, &brush)           -> 0
//     GdipFillRectangle(g, brush, x, y, w, h)     -> 0   ← 抗锯齿填充
// 而 shim 现在用的是 `XFillRectangle`（纯色、无抗锯齿）。
//
// 口径：
//   · **只在 `WPF_LINUX_GDIP=1` 时启用**，默认仍走原来的 `XFillRectangle` ⇒ 不影响现有 31/31 判据。
//   · `GpGraphics` 按 hwnd 缓存（复用，避免每帧重建 cairo surface）。
//   · 不做 `GdiplusStartup` —— 我们跑在 .NET 进程里，`System.Drawing.Common` 已经启动过 GDI+；
//     冒然再调一次会返回 `GdiplusNotInitialized`/`InvalidParameter`（K-172 的教训）。
//     如果真没启动过，`GdipCreateFromXDrawable_linux` 会返回非 0，我们如实回退到 X11 路径。
#include "win32_internal.h"
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef void GpGraphics;
typedef void GpBrush;

typedef struct {
    int  inited;
    int  available;                 /* 1 = libgdiplus 可用 */
    int *(*createFromX)(unsigned long, void *, GpGraphics **);
    int  (*createSolidFill)(unsigned int, GpBrush **);
    int  (*fillRectangle)(GpGraphics *, GpBrush *, float, float, float, float);
    int  (*deleteBrush)(GpBrush *);
    int  (*deleteGraphics)(GpGraphics *);
} wpf_gdip_api;

static wpf_gdip_api g_gdip;

/* 按 hwnd 缓存 GpGraphics */
typedef struct wpf_gdip_cache {
    struct wpf_gdip_cache *next;
    void       *hwnd;
    GpGraphics *g;
} wpf_gdip_cache;
static wpf_gdip_cache *g_gdip_cache;

static int wpf_gdip_enabled(void)
{
    static int cached = -1;
    if (cached < 0) {
        const char *e = getenv("WPF_LINUX_GDIP");
        cached = (e && *e && *e != '0') ? 1 : 0;
    }
    return cached;
}

static void wpf_gdip_init(void)
{
    if (g_gdip.inited) return;
    g_gdip.inited = 1;
    void *lib = dlopen("libgdiplus.so.0", RTLD_NOW);
    if (!lib) lib = dlopen("libgdiplus.so", RTLD_NOW);
    if (!lib) {
        if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] dlopen libgdiplus 失败\n");
        return;
    }
    g_gdip.createFromX     = (int *(*)(unsigned long, void *, GpGraphics **))dlsym(lib, "GdipCreateFromXDrawable_linux");
    g_gdip.createSolidFill = (int  (*)(unsigned int, GpBrush **))dlsym(lib, "GdipCreateSolidFill");
    g_gdip.fillRectangle   = (int  (*)(GpGraphics *, GpBrush *, float, float, float, float))dlsym(lib, "GdipFillRectangle");
    g_gdip.deleteBrush     = (int  (*)(GpBrush *))dlsym(lib, "GdipDeleteBrush");
    g_gdip.deleteGraphics  = (int  (*)(GpGraphics *))dlsym(lib, "GdipDeleteGraphics");
    if (!g_gdip.createFromX || !g_gdip.createSolidFill || !g_gdip.fillRectangle) {
        if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] 关键符号缺失\n");
        return;
    }
    g_gdip.available = 1;
    if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] libgdiplus 已就绪\n");
}

static GpGraphics *wpf_gdip_for_hwnd(void *hwnd)
{
    for (wpf_gdip_cache *c = g_gdip_cache; c; c = c->next)
        if (c->hwnd == hwnd && c->g) return c->g;

    if (!g_gdip.available || !g_wpf.dpy || !hwnd) return NULL;
    GpGraphics *g = NULL;
    int rc = g_gdip.createFromX((unsigned long)(uintptr_t)hwnd, (void *)g_wpf.dpy, &g);
    if (rc != 0 || !g) {
        if (getenv("WPF_LINUX_KEY_DIAG"))
            fprintf(stderr, "[GDIP-R] CreateFromXDrawable(hwnd=%p) -> %d\n", hwnd, rc);
        return NULL;
    }
    wpf_gdip_cache *c = (wpf_gdip_cache *)calloc(1, sizeof(*c));
    if (c) { c->hwnd = hwnd; c->g = g; c->next = g_gdip_cache; g_gdip_cache = c; }
    return g;
}

/* 对外的填充接口。返回 1 = 用 cairo 画成功；0 = 不可用（调用方回退到 X11 路径） */
int wpf_gdip_try_fill_rect(HWND hwnd, int32_t x, int32_t y, int32_t w, int32_t h, uint32_t argb)
{
    if (!wpf_gdip_enabled()) return 0;
    wpf_gdip_init();
    if (!g_gdip.available) return 0;
    if (w <= 0 || h <= 0) return 0;
    GpGraphics *g = wpf_gdip_for_hwnd((void *)(uintptr_t)hwnd);
    if (!g) return 0;
    GpBrush *br = NULL;
    if (g_gdip.createSolidFill(argb, &br) != 0 || !br) return 0;
    int rc = g_gdip.fillRectangle(g, br, (float)x, (float)y, (float)w, (float)h);
    if (g_gdip.deleteBrush) g_gdip.deleteBrush(br);
    if (rc != 0) {
        if (getenv("WPF_LINUX_KEY_DIAG"))
            fprintf(stderr, "[GDIP-R] FillRectangle(%d,%d %dx%d) -> %d\n", x, y, w, h, rc);
        return 0;
    }
    return 1;
}

/* 窗口销毁时清缓存（避免对已释放的 X11 窗口持有 cairo surface） */
void wpf_gdip_forget(HWND hwnd)
{
    wpf_gdip_cache **pp = &g_gdip_cache;
    while (*pp) {
        if ((*pp)->hwnd == (void *)(uintptr_t)hwnd) {
            wpf_gdip_cache *victim = *pp;
            *pp = victim->next;
            if (victim->g && g_gdip.deleteGraphics) g_gdip.deleteGraphics(victim->g);
            free(victim);
            return;
        }
        pp = &(*pp)->next;
    }
}
