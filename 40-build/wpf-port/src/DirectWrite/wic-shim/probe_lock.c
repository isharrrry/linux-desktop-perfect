/* probe_lock.c —— IWICBitmap::Lock / GetDataPointer / GetStride 的 shim 级验收
 * 断言刻意做成**可现场推翻**：写穿 GetDataPointer 返回的指针后，必须能从同一张位图的
 * CopyPixels 读回该字节 —— 若 shim 返回的是副本/伪造指针，这条立刻红。*/
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
typedef struct { int32_t x, y, w, h; } wic_rect;
static guid_t BGRA = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f}};
int main(int argc, char **argv)
{
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen: %s\n", dlerror()); return 2; }
    int32_t (*mkf)(uint32_t, void **) = dlsym(h, "WICCreateImagingFactory_Proxy");
    int32_t (*mkbm)(void *, uint32_t, uint32_t, guid_t *, uint32_t, uint32_t, unsigned char *, void **) =
        dlsym(h, "IWICImagingFactory_CreateBitmapFromMemory_Proxy");
    int32_t (*lock_)(void *, wic_rect *, uint32_t, void **) = dlsym(h, "IWICBitmap_Lock_Proxy");
    int32_t (*dp)(void *, uint32_t *, void **) = dlsym(h, "IWICBitmapLock_GetDataPointer_STA_Proxy");
    int32_t (*stride)(void *, uint32_t *) = dlsym(h, "IWICBitmapLock_GetStride_Proxy");
    int32_t (*getsz)(void *, uint32_t *, uint32_t *) = dlsym(h, "IWICBitmapSource_GetSize_Proxy");
    int32_t (*copy)(void *, wic_rect *, uint32_t, uint32_t, void *) = dlsym(h, "IWICBitmapSource_CopyPixels_Proxy");
    int32_t (*reg)(intptr_t, const guid_t *, uint32_t, uint32_t, int32_t, const void *, uint32_t) = dlsym(h, "WicShim_RegisterForeignSource");
    int32_t (*unreg)(intptr_t) = dlsym(h, "WicShim_UnregisterForeignSource");
    int32_t (*borrows)(void) = dlsym(h, "WicShim_ForeignPixelBorrows");
    if (!mkf||!mkbm||!lock_||!dp||!stride||!getsz||!copy||!reg||!unreg||!borrows) { printf("SKIP 导出缺失\n"); return 2; }

    int fails = 0;
    void *factory = NULL; mkf(0x01000000u, &factory);
    const uint32_t W = 8, H = 4, STRIDE = W*4;
    unsigned char px[8*4*4];
    for (uint32_t i = 0; i < W*H; i++) { px[i*4+0]=(unsigned char)i; px[i*4+1]=0x11; px[i*4+2]=0x22; px[i*4+3]=0xFF; }

    void *bm = NULL;
    int32_t hr = mkbm(factory, W, H, &BGRA, STRIDE, sizeof px, px, &bm);
    if (hr != 0 || !bm) { printf("FAIL 建内存位图 hr=0x%08X\n", (unsigned)hr); return 1; }

    uint32_t gw=0, gh=0; getsz(bm, &gw, &gh);
    printf("GetSize = %ux%u（期望 %ux%u）\n", gw, gh, W, H);
    if (gw != W || gh != H) fails++;

    void *lk = NULL;
    hr = lock_(bm, NULL, 0, &lk);
    printf("Lock(整图) hr=0x%08X lock=%s\n", (unsigned)hr, lk ? "非空 ✓" : "**空**");
    if (hr != 0 || !lk) { printf("LOCK_PROBE=FAIL（Lock 失败，后续断言无法进行）\n"); return 1; }

    uint32_t st = 0; hr = stride(lk, &st);
    printf("GetStride hr=0x%08X stride=%u（期望 %u，且须 == GetSize.width*4）\n", (unsigned)hr, st, STRIDE);
    if (hr != 0 || st != STRIDE || st != gw*4) fails++;

    uint32_t cb = 0; void *p = NULL; hr = dp(lk, &cb, &p);
    printf("GetDataPointer hr=0x%08X cb=%u（期望 %u） ptr=%s\n", (unsigned)hr, cb, STRIDE*H, p ? "非空 ✓" : "**空**");
    if (hr != 0 || cb != STRIDE*H || !p) fails++;
    else {
        /* 可现场推翻：写穿指针 → 必须能从 CopyPixels 读回同一字节 */
        ((unsigned char *)p)[0] = 0xAB;
        unsigned char out[sizeof px]; memset(out, 0, sizeof out);
        wic_rect full = {0,0,(int32_t)W,(int32_t)H};
        copy(bm, &full, STRIDE, sizeof out, out);
        printf("写穿指针后 CopyPixels 首字节 = 0x%02X（期望 0xAB）%s\n", out[0], out[0]==0xAB ? "✓ 指针即真实缓冲" : "**是副本/伪造指针**");
        if (out[0] != 0xAB) fails++;
    }

    wic_rect sub = {1,1,2,2}; void *lk2 = NULL;
    hr = lock_(bm, &sub, 0, &lk2);
    printf("Lock(子矩形 1,1,2x2) hr=0x%08X %s\n", (unsigned)hr, hr == (int32_t)0x88982F81 ? "=UNSUPPORTEDOPERATION ✓（不静默裁剪）" : "**码不对**");
    if (hr != (int32_t)0x88982F81) fails++;

    const intptr_t MIL = (intptr_t)0x20000010;   /* 模拟 MIL 后缓冲（登记时不借像素）*/
    reg(MIL, &BGRA, W, H, 0, NULL, 0);
    void *lk3 = NULL; hr = lock_(MIL == 0 ? NULL : (void *)MIL, NULL, 0, &lk3);
    printf("Lock(外来源,v1未借像素) hr=0x%08X %s\n", (unsigned)hr, hr == (int32_t)0x88982F81 ? "=UNSUPPORTEDOPERATION ✓（不伪造指针）" : "**码不对**");
    if (hr != (int32_t)0x88982F81) fails++;

    void *lk4 = NULL; hr = lock_((void *)(intptr_t)0xDEADBEEF, NULL, 0, &lk4);
    printf("Lock(未登记句柄) hr=0x%08X %s\n", (unsigned)hr, hr == (int32_t)0x80070057 ? "=E_INVALIDARG ✓" : "**码不对**");
    if (hr != (int32_t)0x80070057) fails++;

    /* ---- v2：外来源借用像素 + **写穿**（主控条件 2/3）---- */
    const intptr_t MIL2 = (intptr_t)0x20000011;
    unsigned char backbuf[8*4*4]; memset(backbuf, 0x5A, sizeof backbuf);
    reg(MIL2, &BGRA, W, H, 0, backbuf, STRIDE);
    printf("ForeignPixelBorrows(登记后) = %d（期望 1）\n", borrows());
    if (borrows() != 1) fails++;
    void *lk5 = NULL; hr = lock_((void *)MIL2, NULL, 0, &lk5);
    printf("Lock(外来源,已借像素) hr=0x%08X %s\n", (unsigned)hr, (hr==0&&lk5) ? "✓" : "**失败**");
    if (hr != 0 || !lk5) fails++;
    else {
        void *p2 = NULL; uint32_t cb2 = 0; dp(lk5, &cb2, &p2);
        if (!p2) { printf("FAIL GetDataPointer(外来源) 为空\n"); fails++; }
        else {
            ((unsigned char *)p2)[0] = 0xCD;
            unsigned char out2[sizeof backbuf]; memset(out2, 0, sizeof out2);
            wic_rect full2 = {0,0,(int32_t)W,(int32_t)H};
            copy((void *)MIL2, &full2, STRIDE, sizeof out2, out2);
            printf("外来源写穿：写 0xCD → CopyPixels 读回 0x%02X %s\n", out2[0],
                   out2[0]==0xCD ? "✓ 指针通向真实后缓冲（不是副本）" : "**不通向真实缓冲 = 假绿**");
            if (out2[0] != 0xCD) fails++;
            printf("  同一块内存直读 backbuf[0] = 0x%02X（应与上一致）\n", backbuf[0]);
            if (backbuf[0] != 0xCD) fails++;
        }
    }
    unreg(MIL2);
    printf("ForeignPixelBorrows(注销后) = %d（期望 0）\n", borrows());
    if (borrows() != 0) fails++;
    void *lk6 = NULL; hr = lock_((void *)MIL2, NULL, 0, &lk6);
    printf("注销后 Lock(外来源) hr=0x%08X lk=%s %s\n", (unsigned)hr, lk6?"非空":"NULL",
           (hr != 0 && !lk6) ? "✓ 不再发指针" : "**仍在发指针**");
    if (hr == 0 || lk6) fails++;

    printf("%s\n", fails == 0 ? "LOCK_PROBE=PASS" : "LOCK_PROBE=FAIL");
    return fails == 0 ? 0 : 1;
}
