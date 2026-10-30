/* probe_foreign.c —— 外来源（MIL 所有的 IWICBitmapSource 句柄）**派发**的 shim 级验收
 *
 * 【形态】单一判据 + 逐条 printf + 每步 fail 计数（照 probe_lock.c）。
 *   刻意**不用** honesty_ok 那种复合判据 —— 上一版就是被它误导的：改的 bytes_ok 不是生效的那条。
 * 【v1/v2 两代契约并存，这是要点】
 *   · 带像素登记 ⇒ CopyPixels 必须**逐字节相等**（v2：借用 MIL 的真缓冲，不拷贝）
 *   · NULL 登记   ⇒ CopyPixels 必须 UNSUPPORTEDOPERATION（v1 的诚实拒绝**必须保留**，
 *                    用来证明"守卫没被 v2 顺手删掉"）
 * 【记账边界】外来源不混进本 shim 的 HandleCount；OwnsHandle 对外来条目返 0（记账留在 MilDeviceObjectTable）。
 * 【环境】本仓文件含 NUL 字节：读文本一律 grep -an。
 */
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
typedef struct { int32_t x, y, w, h; } wic_rect;

static guid_t PBGRA = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
static int fails;

static int check(const char *label, int ok, const char *extra)
{
    printf("  %-46s %s%s%s\n", label, ok ? "PASS" : "**FAIL**",
           extra ? "  " : "", extra ? extra : "");
    if (!ok) fails++;
    return ok;
}

int main(int argc, char **argv)
{
    if (argc < 2) { printf("用法: probe_foreign <libwpfwic.so>\n"); return 2; }
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen: %s\n", dlerror()); return 2; }

    int32_t (*reg)(intptr_t, const guid_t *, uint32_t, uint32_t, int32_t, const void *, uint32_t) =
        dlsym(h, "WicShim_RegisterForeignSource");
    int32_t (*unreg)(intptr_t) = dlsym(h, "WicShim_UnregisterForeignSource");
    int32_t (*owns)(intptr_t) = dlsym(h, "WicShim_OwnsHandle");
    int32_t (*hcount)(void) = dlsym(h, "WicShim_HandleCount");
    int32_t (*fcount)(void) = dlsym(h, "WicShim_ForeignSourceCount");
    int32_t (*borrows)(void) = dlsym(h, "WicShim_ForeignPixelBorrows");
    int32_t (*getpf)(void *, guid_t *) = dlsym(h, "IWICBitmapSource_GetPixelFormat_Proxy");
    int32_t (*getsz)(void *, uint32_t *, uint32_t *) = dlsym(h, "IWICBitmapSource_GetSize_Proxy");
    int32_t (*copy)(void *, wic_rect *, uint32_t, uint32_t, void *) = dlsym(h, "IWICBitmapSource_CopyPixels_Proxy");
    int32_t (*lock_)(void *, wic_rect *, uint32_t, void **) = dlsym(h, "IWICBitmap_Lock_Proxy");
    int32_t (*dptr)(void *, uint32_t *, void **) = dlsym(h, "IWICBitmapLock_GetDataPointer_STA_Proxy");
    if (!reg||!unreg||!owns||!hcount||!fcount||!borrows||!getpf||!getsz||!copy||!lock_||!dptr) {
        printf("SKIP 导出缺失\n"); return 2;
    }

    const intptr_t MIL = (intptr_t)0x2000000f;      /* 模拟 MIL 后缓冲句柄（带像素）*/
    const intptr_t MIL_NOPX = (intptr_t)0x20000012; /* 模拟"未借像素"的登记 */
    const uint32_t W = 4, H = 3, STRIDE = W * 4;
    const int32_t E_INVALIDARG = (int32_t)0x80070057;
    const int32_t E_UNSUPPORTED = (int32_t)0x88982F81;

    unsigned char px[4*3*4];
    for (uint32_t i = 0; i < W*H; i++) { px[i*4+0]=(unsigned char)(10*i); px[i*4+1]=0x40; px[i*4+2]=0x80; px[i*4+3]=0xFF; }

    printf("== 外来源派发（v2 契约）==\n");

    /* 1. fail-safe：未登记句柄 */
    guid_t g; memset(&g, 0xEE, sizeof g);
    int32_t hr = getpf((void *)MIL, &g);
    check("1. 登记前 GetPixelFormat ⇒ E_INVALIDARG（fail-safe）", hr == E_INVALIDARG, NULL);

    /* 2~5. 带像素登记 */
    int32_t hc0 = hcount();
    hr = reg(MIL, &PBGRA, W, H, 0, px, STRIDE);
    check("2. Register(带像素) ⇒ S_OK", hr == 0, NULL);
    check("3. HandleCount 不变（外来源不混进本 shim 计数）", hcount() == hc0, NULL);
    check("4. ForeignSourceCount == 1", fcount() == 1, NULL);
    check("5. OwnsHandle(外来) == 0（记账留在 MIL）", owns(MIL) == 0, NULL);

    memset(&g, 0, sizeof g);
    hr = getpf((void *)MIL, &g);
    check("6. 登记后 GetPixelFormat = Pbgra32", hr == 0 && memcmp(&g, &PBGRA, 16) == 0, NULL);
    uint32_t gw = 0, gh = 0;
    hr = getsz((void *)MIL, &gw, &gh);
    check("7. GetSize = 4x3", hr == 0 && gw == W && gh == H, NULL);

    /* 8. v2 核心：带像素 ⇒ 逐字节相等 */
    unsigned char out[sizeof px]; memset(out, 0, sizeof out);
    wic_rect full = {0,0,(int32_t)W,(int32_t)H};
    hr = copy((void *)MIL, &full, STRIDE, sizeof out, out);
    check("8. CopyPixels(带像素) ⇒ 逐字节相等（v2：借用字节原样交付）",
          hr == 0 && memcmp(out, px, sizeof px) == 0, NULL);

    /* 9. 借用计数 1 */
    check("9. ForeignPixelBorrows == 1（登记后）", borrows() == 1, NULL);

    /* 10~11. 外来源写穿：Lock 指针必须通向真实缓冲 */
    void *lk = NULL; hr = lock_((void *)MIL, NULL, 0, &lk);
    if (check("10. Lock(外来源,已借像素) ⇒ S_OK", hr == 0 && lk != NULL, NULL)) {
        uint32_t cb = 0; void *p = NULL;
        dptr(lk, &cb, &p);
        if (check("11a. GetDataPointer(外来源) 非空", p != NULL, NULL)) {
            ((unsigned char *)p)[0] = 0xCD;
            unsigned char out2[sizeof px]; memset(out2, 0, sizeof out2);
            copy((void *)MIL, &full, STRIDE, sizeof out2, out2);
            check("11b. 写穿 0xCD ⇒ CopyPixels 读回 0xCD（非副本）", out2[0] == 0xCD, NULL);
            check("11c. 同一块内存直读也是 0xCD", px[0] == 0xCD, NULL);
        }
    }

    /* 12. NULL 登记 ⇒ v1 诚实拒绝必须保留 */
    hr = reg(MIL_NOPX, &PBGRA, W, H, 0, NULL, 0);
    check("12a. Register(NULL 像素) ⇒ S_OK（登记本身合法）", hr == 0, NULL);
    unsigned char out3[sizeof px]; memset(out3, 0, sizeof out3);
    hr = copy((void *)MIL_NOPX, &full, STRIDE, sizeof out3, out3);
    check("12b. NULL 登记 CopyPixels ⇒ UNSUPPORTEDOPERATION（守卫未被删）", hr == E_UNSUPPORTED, NULL);
    void *lk_np = NULL; hr = lock_((void *)MIL_NOPX, NULL, 0, &lk_np);
    check("12c. NULL 登记 Lock ⇒ UNSUPPORTEDOPERATION 且不发指针", hr == E_UNSUPPORTED && lk_np == NULL, NULL);
    check("12d. 借用计数仍为 1（NULL 登记不计借用）", borrows() == 1, NULL);

    /* 13. 注销 MIL_NOPX：计数不变；注销 MIL：计数归 0 */
    unreg(MIL_NOPX);
    check("13a. 注销 NULL 登记后 ForeignSourceCount == 1", fcount() == 1, NULL);
    check("13b. 借用计数仍 1", borrows() == 1, NULL);
    unreg(MIL);
    check("13c. 注销带像素登记后 ForeignPixelBorrows == 0", borrows() == 0, NULL);
    check("13d. ForeignSourceCount == 0", fcount() == 0, NULL);

    /* 14. 注销后一律回到 fail-safe */
    memset(&g, 0, sizeof g);
    hr = getpf((void *)MIL, &g);
    check("14a. 注销后 GetPixelFormat ⇒ E_INVALIDARG", hr == E_INVALIDARG, NULL);
    void *lk_after = NULL; hr = lock_((void *)MIL, NULL, 0, &lk_after);
    check("14b. 注销后 Lock ⇒ 失败且不发指针", hr != 0 && lk_after == NULL, NULL);

    printf("%s（fail=%d）\n", fails == 0 ? "FOREIGN_DISPATCH=PASS" : "FOREIGN_DISPATCH=FAIL", fails);
    return fails == 0 ? 0 : 1;
}
