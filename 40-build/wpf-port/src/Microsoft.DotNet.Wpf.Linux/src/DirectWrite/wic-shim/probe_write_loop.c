
/* probe_write_loop.c —— shim 级 **写→读闭环**：走我自己的 WIC 编码器全族 + 自己的流对象，
 * 再把我自己的读路径读回来比对。不需要 PC/X/harness。
 * 为什么用"自己的流对象"：PC 在 BitmapEncoder.Save(Stream) 里给的是 MIL 的流句柄，
 * 而 MIL 的 MILIStreamWrite 只写进它自己的 MemoryStream（不转发给调用方 Stream）——
 * 那条要 MIL 侧配合；本探针先钉住"编码+写出+读回"这一段的正确性。*/
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;
typedef struct { int32_t x,y,w,h; } wic_rect;
static guid_t PNG = { 0x1b7cfaf4, 0x713f, 0x473c, { 0xbb, 0xcd, 0x61, 0x37, 0x42, 0x5f, 0xae, 0xaf } };
static guid_t JPEG = { 0x19e4a5aa, 0x5662, 0x4fc5, { 0xa0, 0xc0, 0x17, 0x58, 0x02, 0x8e, 0x10, 0x57 } };
static guid_t TIFF = { 0x163bcc30, 0xe2e9, 0x4f0b, { 0x96, 0x1d, 0xa3, 0xe9, 0xfd, 0xb7, 0x88, 0xa3 } };
int main(int argc, char **argv)
{
    if (argc < 2) { printf("用法: probe_write_loop <libwpfwic.so>\n"); return 2; }
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen: %s\n", dlerror()); return 2; }
    int32_t (*mk_factory)(uint32_t, void **) = dlsym(h, "WICCreateImagingFactory_Proxy");
    int32_t (*mk_encoder)(void *, guid_t *, guid_t *, void **) = dlsym(h, "IWICImagingFactory_CreateEncoder_Proxy");
    int32_t (*mk_stream)(void *, void **) = dlsym(h, "IWICImagingFactory_CreateStream_Proxy");
    int32_t (*enc_init)(void *, void *, int32_t) = dlsym(h, "IWICBitmapEncoder_Initialize_Proxy");
    int32_t (*mk_frame)(void *, void **, void **) = dlsym(h, "IWICBitmapEncoder_CreateNewFrame_Proxy");
    int32_t (*set_size)(void *, int32_t, int32_t) = dlsym(h, "IWICBitmapFrameEncode_SetSize_Proxy");
    int32_t (*set_fmt)(void *, guid_t *) = dlsym(h, "IWICBitmapFrameEncode_SetPixelFormat_Proxy");
    int32_t (*set_dpi)(void *, double, double) = dlsym(h, "IWICBitmapFrameEncode_SetResolution_Proxy");
    int32_t (*write_px)(void *, uint32_t, uint32_t, uint32_t, unsigned char *) = dlsym(h, "IWICBitmapFrameEncode_WritePixels_Proxy");
    int32_t (*fe_commit)(void *) = dlsym(h, "IWICBitmapFrameEncode_Commit_Proxy");
    int32_t (*enc_commit)(void *) = dlsym(h, "IWICBitmapEncoder_Commit_Proxy");
    int32_t (*stream_bytes)(intptr_t, unsigned char **, size_t *) = dlsym(h, "WicShim_StreamBytes");
    int32_t (*selftest)(const char *, int32_t *, int32_t *, uint32_t *) = dlsym(h, "WicShim_SelfTest");
    if (!mk_factory || !mk_encoder || !mk_stream || !enc_init || !mk_frame || !write_px || !enc_commit || !stream_bytes) {
        printf("SKIP 导出缺失\n"); return 2; }

    void *factory = NULL;
    if (mk_factory(0x01000000u, &factory) != 0) { printf("FAIL 建工厂\n"); return 1; }
    int fails = 0;

    /* 不支持的容器：必须给 COMPONENTNOTFOUND(0x88982f50)，不能静默 */
    guid_t jv = {0,0,0,{0}};
    void *bogus = NULL;
    int32_t hr = mk_encoder(factory, &TIFF, &jv, &bogus);
    printf("FAILPATH tiff_encoder hr=0x%08X %s\n", (unsigned)hr, hr == (int32_t)0x88982f50 ? "(COMPONENTNOTFOUND ✓)" : "(**码不对**)");
    if (hr != (int32_t)0x88982f50) fails++;

    const int W = 8, H = 8, STRIDE = W * 4;
    unsigned char px[8*8*4];
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
        int i = y*STRIDE + x*4; px[i]=(unsigned char)(x*30); px[i+1]=(unsigned char)(y*30); px[i+2]=0x11; px[i+3]=0xFF; }

    const char *names[2] = {"PNG", "JPEG"};
    guid_t fmts[2] = {PNG, JPEG};
    for (int k = 0; k < 2; k++) {
        void *enc = NULL, *stream = NULL, *frame = NULL, *opts = NULL;
        if (mk_encoder(factory, &fmts[k], &jv, &enc) != 0) { printf("FAIL 建 %s 编码器\n", names[k]); fails++; continue; }
        if (mk_stream(factory, &stream) != 0) { printf("FAIL 建流\n"); fails++; continue; }
        if (enc_init(enc, stream, 0) != 0) { printf("FAIL Initialize\n"); fails++; continue; }
        if (mk_frame(enc, &frame, &opts) != 0 || !frame) { printf("FAIL CreateNewFrame\n"); fails++; continue; }
        if (set_size && set_size(frame, W, H) != 0) { printf("FAIL SetSize\n"); fails++; continue; }
        if (set_fmt && set_fmt(frame, &(guid_t){0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f}}) != 0) printf("  注：SetPixelFormat 返回非 0\n");
        if (set_dpi) set_dpi(frame, 300, 300);
        hr = write_px(frame, H, STRIDE, sizeof px, px);
        if (hr != 0) { printf("FAIL WritePixels hr=0x%08X\n", (unsigned)hr); fails++; continue; }
        if (fe_commit && fe_commit(frame) != 0) { printf("FAIL FrameEncode.Commit\n"); fails++; continue; }
        hr = enc_commit(enc);
        if (hr != 0) { printf("FAIL Encoder.Commit hr=0x%08X\n", (unsigned)hr); fails++; continue; }

        unsigned char *buf = NULL; size_t n = 0;
        if (stream_bytes((intptr_t)stream, &buf, &n) != 0 || !buf || n == 0) { printf("FAIL 流里没有字节\n"); fails++; continue; }
        int is_png = n > 8 && buf[0]==0x89 && buf[1]=='P' && buf[2]=='N' && buf[3]=='G';
        int is_jpg = n > 3 && buf[0]==0xFF && buf[1]==0xD8 && buf[2]==0xFF;
        char path[256]; snprintf(path, sizeof path, "/tmp/wic-loop-%s.bin", names[k]);
        FILE *f = fopen(path, "wb"); fwrite(buf, 1, n, f); fclose(f);
        printf("ENCODE %s -> %zu 字节 png_magic=%d jpeg_magic=%d\n", names[k], n, is_png, is_jpg);
        if (k == 0) {
            if (!is_png) { printf("FAIL PNG magic\n"); fails++; }
            /* 注入检查：pHYs 与 tEXt 是否进了 PNG */
            int has_phys = 0; for (size_t i = 0; i + 4 <= n; i++) if (!memcmp(buf+i, "pHYs", 4)) has_phys = 1;
            printf("  PNG_PHYS_INJECTED=%d\n", has_phys);
            if (!has_phys) { printf("FAIL SetResolution 没写进 PNG（pHYs 缺失）\n"); fails++; }
            if (selftest) {
                int32_t w=0, hh=0; uint32_t p0=0;
                int32_t srh = selftest(path, &w, &hh, &p0);
                printf("  READBACK selftest hr=0x%08X %dx%d first=0x%08X (期望 8x8 first=0xFF110000)\n", (unsigned)srh, w, hh, p0);
                if (srh != 0 || w != W || hh != H || p0 != 0xFF110000u) { printf("FAIL 写→读不一致\n"); fails++; }
                else printf("  OK 写→读一致（尺寸 + 首像素）\n");
            }
        } else if (!is_jpg) { printf("FAIL JPEG magic\n"); fails++; }
    }
    printf("%s\n", fails == 0 ? "WRITE_LOOP=PASS" : "WRITE_LOOP=FAIL");
    return fails == 0 ? 0 : 1;
}
