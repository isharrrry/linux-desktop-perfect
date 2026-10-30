/* probe_premul.c —— 预乘（32bppPBGRA）反解的 **A/B 实测**：
 * 同一份预乘字节，(a) 按 PBGRA 声明 → 期望反解成直通 alpha；(b) 按 BGRA 声明 → 原样编码（=反解缺失时的行为）。
 * 两者与"数学期望的直通值"比较，把"会偏多少"量出来。*/
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
static guid_t PNG  = {0x1b7cfaf4,0x713f,0x473c,{0xbb,0xcd,0x61,0x37,0x42,0x5f,0xae,0xaf}};
static guid_t BGRA = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f}};
static guid_t PBGRA= {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
int main(int argc, char **argv)
{
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen\n"); return 2; }
    int32_t (*mkf)(uint32_t, void **) = dlsym(h, "WICCreateImagingFactory_Proxy");
    int32_t (*mke)(void *, guid_t *, guid_t *, void **) = dlsym(h, "IWICImagingFactory_CreateEncoder_Proxy");
    int32_t (*mks)(void *, void **) = dlsym(h, "IWICImagingFactory_CreateStream_Proxy");
    int32_t (*ini)(void *, void *, int32_t) = dlsym(h, "IWICBitmapEncoder_Initialize_Proxy");
    int32_t (*mkfr)(void *, void **, void **) = dlsym(h, "IWICBitmapEncoder_CreateNewFrame_Proxy");
    int32_t (*ssz)(void *, int32_t, int32_t) = dlsym(h, "IWICBitmapFrameEncode_SetSize_Proxy");
    int32_t (*sfm)(void *, guid_t *) = dlsym(h, "IWICBitmapFrameEncode_SetPixelFormat_Proxy");
    int32_t (*wpx)(void *, uint32_t, uint32_t, uint32_t, unsigned char *) = dlsym(h, "IWICBitmapFrameEncode_WritePixels_Proxy");
    int32_t (*fcm)(void *) = dlsym(h, "IWICBitmapFrameEncode_Commit_Proxy");
    int32_t (*ecm)(void *) = dlsym(h, "IWICBitmapEncoder_Commit_Proxy");
    int32_t (*sb)(intptr_t, unsigned char **, size_t *) = dlsym(h, "WicShim_StreamBytes");
    int32_t (*st)(const char *, int32_t *, int32_t *, uint32_t *) = dlsym(h, "WicShim_SelfTest");
    if (!mkf || !mke || !mks || !ini || !mkfr || !wpx || !ecm || !sb || !st) { printf("SKIP 导出缺失\n"); return 2; }

    /* 2x2 预乘 BGRA：首像素 = (64,32,16, alpha=128) ⇒ 数学上的直通值 = (128,64,32,128) */
    unsigned char px[16] = { 64,32,16,128,  0,0,0,0,  255,128,64,255,  10,20,30,64 };
    const char *names[2] = { "PBGRA(声明为预乘)", "BGRA(声明为直通)" };
    guid_t fmts[2] = { PBGRA, BGRA };
    unsigned got[2] = {0,0};

    for (int k = 0; k < 2; k++) {
        void *f = NULL, *enc = NULL, *stream = NULL, *frame = NULL, *opts = NULL;
        guid_t jv = {0,0,0,{0}};
        mkf(0x01000000u, &f);
        if (mke(f, &PNG, &jv, &enc) || mks(f, &stream) || ini(enc, stream, 0) ||
            mkfr(enc, &frame, &opts) || ssz(frame, 2, 2) || sfm(frame, &fmts[k]) ||
            wpx(frame, 2, 8, sizeof px, px) || fcm(frame) || ecm(enc)) { printf("FAIL 编码链 %s\n", names[k]); return 1; }
        unsigned char *buf = NULL; size_t n = 0;
        if (sb((intptr_t)stream, &buf, &n) || !n) { printf("FAIL 流为空\n"); return 1; }
        char path[256]; snprintf(path, sizeof path, "/tmp/wic-premul-%d.png", k);
        FILE *fp = fopen(path, "wb"); fwrite(buf, 1, n, fp); fclose(fp);
        int32_t w = 0, hh = 0; uint32_t p0 = 0;
        st(path, &w, &hh, &p0);
        got[k] = p0;
        printf("  %-18s → 首像素 BGRA=(%u,%u,%u,%u)  [%zu 字节]\n", names[k],
               p0 & 0xFF, (p0 >> 8) & 0xFF, (p0 >> 16) & 0xFF, (p0 >> 24) & 0xFF, n);
    }
    unsigned want = 128u | (64u << 8) | (32u << 16) | (128u << 24);   /* 期望直通值 */
    printf("EXPECTED_UNPREMUL=0x%08X (BGRA=128,64,32,128)\n", want);
    printf("AS_PBGRA=0x%08X  AS_BGRA=0x%08X\n", got[0], got[1]);
    int ok = (got[0] == want) && (got[1] != want);
    printf("偏差证据：反解缺失时（按 BGRA 编码）首像素 = (%u,%u,%u,%u)，与期望差 (%+d,%+d,%+d)\n",
           got[1] & 0xFF, (got[1] >> 8) & 0xFF, (got[1] >> 16) & 0xFF, (got[1] >> 24) & 0xFF,
           (int)(got[1] & 0xFF) - 128, (int)((got[1] >> 8) & 0xFF) - 64, (int)((got[1] >> 16) & 0xFF) - 32);
    printf("%s\n", ok ? "UNPREMUL_AB=PASS（反解生效，且未反解确实会偏）" : "UNPREMUL_AB=FAIL");
    return ok ? 0 : 1;
}
