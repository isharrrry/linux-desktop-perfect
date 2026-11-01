/*
 * milqi_shim.c —— 【K-473】`MILQueryInterface` 的 LD_PRELOAD 拦截器
 *
 * 背景（K-472 决定性实测）：
 *   `BitmapVisualManager.Render` 调 `QueryInterface(rtb, IID_IMILRenderTargetBitmap)`
 *   返回 `0x80004002`（`E_NOINTERFACE`）⇒ 官方 `wpfgfx_cor3.so` 里的
 *   `CSwRenderTargetBitmap` **没有实现**那个接口（虽然仓内源码 `swsurfrt.cpp:2020` 里有）。
 *
 * 做法：
 *   拦截 `MILQueryInterface`。当 `riid == IID_IMILRenderTargetBitmap` 且底层返回
 *   `E_NOINTERFACE` 时，返回一个**我们自己的最小 COM 对象**：
 *     - vtable 前 3 项：`QueryInterface` / `AddRef` / `Release`
 *     - 之后所有方法返回 `E_NOTIMPL`（不真的渲染，只让流程走下去）
 *
 * 这样 `BitmapVisualManager.Render` 能继续执行到 `Renderer.Render`，
 * 我们就能观察下一层失败点（或让它静默通过）。
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

/* IID_IMILRenderTargetBitmap = {00000201-a8f2-4877-ba0a-fd2b6645fb94} */
static const uint8_t IID_IMIL_RTB[16] = {
    0x01, 0x02, 0x00, 0x00, 0xf2, 0xa8, 0x77, 0x48,
    0xba, 0x0a, 0xfd, 0x2b, 0x66, 0x45, 0xfb, 0x94
};
/* IID_IUnknown = {00000000-0000-0000-c000-000000000046} */
static const uint8_t IID_IUNKNOWN[16] = {
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
};

typedef struct mil_fake_rtb mil_fake_rtb;
typedef long (*qi_fn)(void *self, const void *riid, void **ppv);
typedef unsigned long (*addref_fn)(void *self);
typedef unsigned long (*release_fn)(void *self);

struct mil_fake_rtb {
    void **vtable;          /* 必须是第一个成员 */
    long   refcount;
};

/* 通用方法：全部返回 E_NOTIMPL（0x80004001），不真的渲染 */
static long fake_notimpl(void *self, ...) { (void)self; return 0x80004001L; }

/* QueryInterface */
static long fake_qi(void *self, const void *riid, void **ppv)
{
    if (!ppv) return 0x80004003L;            /* E_POINTER */
    if (!riid) return 0x80004003L;
    const uint8_t *g = (const uint8_t *)riid;
    if (memcmp(g, IID_IUNKNOWN, 16) == 0 || memcmp(g, IID_IMIL_RTB, 16) == 0) {
        *ppv = self;
        fprintf(stderr, "[MILQI] QueryInterface(%s) -> OUR FAKE OBJECT %p\n",
                memcmp(g, IID_IUNKNOWN, 16) == 0 ? "IUnknown" : "IMILRenderTargetBitmap", self);
        fflush(stderr);
        return 0;                            /* S_OK */
    }
    fprintf(stderr, "[MILQI] QueryInterface(<other>) -> E_NOINTERFACE\n");
    fflush(stderr);
    return 0x80004002L;                      /* E_NOINTERFACE */
}

static unsigned long fake_addref(void *self)
{
    mil_fake_rtb *o = (mil_fake_rtb *)self;
    return (unsigned long)__sync_add_and_fetch(&o->refcount, 1);
}

static unsigned long fake_release(void *self)
{
    mil_fake_rtb *o = (mil_fake_rtb *)self;
    long n = __sync_sub_and_fetch(&o->refcount, 1);
    if (n <= 0) { fprintf(stderr, "[MILQI] fake object released\n"); fflush(stderr); }
    return (unsigned long)n;
}

/* vtable：前 3 项固定，后面 128 项全 E_NOTIMPL（远超 IMILRenderTargetBitmap 的实际方法数） */
#define VTABLE_SLOTS 131
static void *g_fake_vtable[VTABLE_SLOTS];
static mil_fake_rtb g_fake_obj;
static int g_inited = 0;

static void ensure_init(void)
{
    if (g_inited) return;
    g_fake_vtable[0] = (void *)fake_qi;
    g_fake_vtable[1] = (void *)fake_addref;
    g_fake_vtable[2] = (void *)fake_release;
    for (int i = 3; i < VTABLE_SLOTS; ++i) g_fake_vtable[i] = (void *)fake_notimpl;
    g_fake_obj.vtable = g_fake_vtable;
    g_fake_obj.refcount = 1;
    g_inited = 1;
}

/* 真实符号（懒加载） */
static qi_fn real_qi = NULL;

long MILQueryInterface(void *pIUnknown, const void *riid, void **ppvObject)
{
    ensure_init();
    if (!real_qi) {
        real_qi = (qi_fn)dlsym(RTLD_NEXT, "MILQueryInterface");
        if (!real_qi) {
            fprintf(stderr, "[MILQI] dlsym(RTLD_NEXT, MILQueryInterface) 失败: %s\n", dlerror());
            fflush(stderr);
        }
    }
    if (!riid) {
        return real_qi ? real_qi(pIUnknown, riid, ppvObject) : 0x80004003L;
    }
    if (memcmp((const uint8_t *)riid, IID_IMIL_RTB, 16) == 0) {
        long hr = real_qi ? real_qi(pIUnknown, riid, ppvObject) : 0x80004002L;
        fprintf(stderr, "[MILQI] IMILRenderTargetBitmap 真实 hr=0x%lx\n", (unsigned long)hr);
        fflush(stderr);
        if (hr == 0x80004002L) {             /* E_NOINTERFACE ⇒ 用我们的假对象 */
            *ppvObject = &g_fake_obj;
            fprintf(stderr, "[MILQI] *** 替换为 OUR FAKE OBJECT %p ***\n", (void *)&g_fake_obj);
            fflush(stderr);
            return 0;                        /* S_OK */
        }
        return hr;
    }
    return real_qi ? real_qi(pIUnknown, riid, ppvObject) : 0x80004002L;
}

/* ── 【K-474】拦截 `dlsym` ────────────────────────────────────────────────
 * 实测（`LD_DEBUG=bindings`）：`MILQueryInterface` **零动态绑定** ⇒
 * .NET 是用 `NativeLibrary.GetExport`（`dlsym`）拿函数指针直接调，
 * 所以 `LD_PRELOAD` 导出同名符号**拦不住**。
 *
 * ⇒ 这里拦截 `dlsym`：当 `.NET` 查 `MILQueryInterface` 时返回我们的函数。
 */
typedef void *(*dlsym_fn)(void *handle, const char *symbol);
static dlsym_fn real_dlsym = NULL;

/* ⚠️ 递归防护：`dlsym` 内部又调 `dlsym(RTLD_NEXT, "dlsym")` 会无限递归
 *   ⇒ 用 `dlvsym`（glibc 自带）拿真实符号，它**不经过**我们的 wrapper。
 *   如果 `dlvsym` 也不可用，用静态标志兜底。 */
extern void *dlvsym(void *handle, const char *symbol, const char *version);

void *dlsym(void *handle, const char *symbol)
{
    if (!real_dlsym) {
        real_dlsym = (dlsym_fn)dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.2.5");
        if (!real_dlsym) real_dlsym = (dlsym_fn)dlvsym(RTLD_NEXT, "dlsym", "GLIBC_2.34");
        if (!real_dlsym) return NULL;           /* **不再递归**；实在拿不到就返回 NULL */
    }
    if (symbol && strcmp(symbol, "MILQueryInterface") == 0) {
        fprintf(stderr, "[MILQI] dlsym 拦截: %s -> 我们的实现\n", symbol);
        fflush(stderr);
        return (void *)MILQueryInterface;
    }
    return real_dlsym(handle, symbol);
}
