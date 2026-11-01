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
    int  (*startup)(void *, const void *, void *);     /* GdiplusStartup(&token, input, NULL) */
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
    g_gdip.startup         = (int  (*)(void *, const void *, void *))dlsym(lib, "GdiplusStartup");
    g_gdip.createFromX     = (int *(*)(unsigned long, void *, GpGraphics **))dlsym(lib, "GdipCreateFromXDrawable_linux");
    g_gdip.createSolidFill = (int  (*)(unsigned int, GpBrush **))dlsym(lib, "GdipCreateSolidFill");
    g_gdip.fillRectangle   = (int  (*)(GpGraphics *, GpBrush *, float, float, float, float))dlsym(lib, "GdipFillRectangle");
    g_gdip.deleteBrush     = (int  (*)(GpBrush *))dlsym(lib, "GdipDeleteBrush");
    g_gdip.deleteGraphics  = (int  (*)(GpGraphics *))dlsym(lib, "GdipDeleteGraphics");
    if (!g_gdip.createFromX || !g_gdip.createSolidFill || !g_gdip.fillRectangle) {
        if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] 关键符号缺失\n");
        return;
    }
    // 【清算 / K-179】**必须自己 GdiplusStartup** —— 实测：不 startup 时
    //   `GdipCreateFromXDrawable_linux` 返回 0（能建 GpGraphics），
    //   但 `GdipFillRectangle` 返回 **1**（GenericError），什么都画不出来。
    //   参数顺序（K-172 的教训）：`GdiplusStartup(&token, input, NULL)` —— token 在前！
    if (g_gdip.startup) {
        unsigned char input[24]; memset(input, 0, sizeof input);
        *(unsigned int *)input = 1;                       /* GdiplusVersion = 1 */
        unsigned long long token = 0;
        int rc = g_gdip.startup(&token, input, NULL);
        if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] GdiplusStartup -> %d\n", rc);
        /* 0=Ok；17=已被初始化过（正常，SDC 可能先启动过）—— 两者都算可用 */
        if (rc != 0 && rc != 17 && rc != 2) { if (getenv("WPF_LINUX_KEY_DIAG")) fprintf(stderr, "[GDIP-R] startup 失败，禁用\n"); return; }
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
    // 【清算 / K-178】不信任调用方传的尺寸 —— 实测有窗口的 `pw->width/height` 是 `INT_MAX`
    //   （WinForms 用 `CW_USEDEFAULT`(0x80000000) 建窗，我们直接存进 wpf_window 了）。
    //   ⇒ 直接用 `XGetGeometry` 查 X11 的**真实**几何，这是唯一可靠的来源。
    if (g_wpf.dpy) {
        int32_t gx = 0, gy = 0; unsigned int gw = 0, gh = 0, gb = 0, gd = 0;
        Window groot = 0;
        if (XGetGeometry(g_wpf.dpy, (Window)(uintptr_t)hwnd, &groot, &gx, &gy, &gw, &gh, &gb, &gd)) {
            if ((int32_t)gw > 0 && (int32_t)gh > 0) { w = (int32_t)gw; h = (int32_t)gh; }
        }
    }
    // 【清算 / K-178】**clamp**：还没 resize 的 X11 窗口可能是 65535x65535，
    //   cairo 建不了那么大的 surface（`GdipFillRectangle` 返回 1 = 失败）。
    //   这里按「屏幕尺寸上界」截断，超出的部分本来也画不到。
    if (g_wpf.dpy) {
        int sw = DisplayWidth(g_wpf.dpy, DefaultScreen(g_wpf.dpy));
        int sh = DisplayHeight(g_wpf.dpy, DefaultScreen(g_wpf.dpy));
        if (sw > 0 && w > sw) w = sw;
        if (sh > 0 && h > sh) h = sh;
    }
    if (getenv("WPF_LINUX_KEY_DIAG"))
        fprintf(stderr, "[GDIP-R] try hwnd=%p size=%dx%d argb=0x%08x\n", (void*)(uintptr_t)hwnd, w, h, argb);
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
