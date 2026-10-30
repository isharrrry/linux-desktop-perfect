/* W79A 探针：GIF 多帧解码＋帧时序（走 **shim 的 *_Proxy 导出**，与 PresentationCore 调用同一批入口）。
 *
 * 用法：probe_frames <libwpfwic.so> <image> <outdir>
 * 输出：KEY=VALUE 行（人读 + 判据读）＋ <outdir>/<stem>.shimf<i>.bgra（逐帧像素，供逐位比对）
 *
 * 为什么是这个层次：PC 的 `BitmapDecoder.cs:1438` 调 `IWICBitmapDecoder_GetFrameCount`、
 * `BitmapFrame` 建帧时调 `GetFrame`、取像素走 `IWICBitmapSource.CopyPixels`
 * ⇒ 本探针调的就是这三条 + 帧元数据入口，**判据与产品路径同一个面**。
 *
 * 探针刻意只"报事实"，不判绿红：判词由 frames-check.sh 算（三态）。
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef struct { uint16_t vt, r1, r2, r3; uint64_t v1, v2; } pv_t;

static void *H;
static int32_t (*sym_factory)(uint32_t, void **);
static int32_t (*sym_dec_fd)(void *, intptr_t, void *, uint32_t, void **);
static int32_t (*sym_count)(void *, uint32_t *);
static int32_t (*sym_getframe)(void *, uint32_t, void **);
static int32_t (*sym_copypixels)(void *, void *, uint32_t, uint32_t, void *);
static int32_t (*sym_size)(void *, uint32_t *, uint32_t *);
static int32_t (*sym_pixfmt)(void *, void *);
static int32_t (*sym_framemeta)(void *, void **);
static int32_t (*sym_metaby)(void *, uint16_t *, pv_t *);
static int32_t (*sym_container)(void *, void *);

static void load(void *h, const char *name, void **out)
{
    *out = dlsym(h, name);
    if (!*out) printf("SYM_MISSING=%s\n", name);
}

static uint64_t fnv(const unsigned char *p, size_t n)
{
    uint64_t h = 1469598103934665603ULL;
    for (size_t i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211ULL; }
    return h;
}

static void utf16(const char *s, uint16_t *out)
{
    for (int i = 0; s[i]; i++) out[i] = (uint16_t)(unsigned char)s[i];
    out[strlen(s)] = 0;
}

int main(int argc, char **argv)
{
    if (argc < 4) { printf("USAGE=probe_frames <libwpfwic.so> <image> <outdir>\n"); return 2; }
    setvbuf(stdout, NULL, _IONBF, 0);
    H = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!H) { printf("DLOPEN_HR=1 DLOPEN_ERR=%s\n", dlerror()); return 2; }
    load(H, "WICCreateImagingFactory_Proxy", (void **)&sym_factory);
    load(H, "IWICImagingFactory_CreateDecoderFromFileHandle_Proxy", (void **)&sym_dec_fd);
    load(H, "IWICBitmapDecoder_GetFrameCount_Proxy", (void **)&sym_count);
    load(H, "IWICBitmapDecoder_GetFrame_Proxy", (void **)&sym_getframe);
    load(H, "IWICBitmapSource_CopyPixels_Proxy", (void **)&sym_copypixels);
    load(H, "IWICBitmapSource_GetSize_Proxy", (void **)&sym_size);
    load(H, "IWICBitmapSource_GetPixelFormat_Proxy", (void **)&sym_pixfmt);
    load(H, "IWICBitmapFrameDecode_GetMetadataQueryReader_Proxy", (void **)&sym_framemeta);
    load(H, "IWICMetadataQueryReader_GetMetadataByName_Proxy", (void **)&sym_metaby);
    load(H, "IWICBitmapDecoder_GetContainerFormat_Proxy", (void **)&sym_container);
    if (!sym_factory || !sym_dec_fd || !sym_count || !sym_getframe || !sym_copypixels || !sym_size) {
        printf("PROBE=NOINFO reason=symbols-missing\n");
        return 2;
    }

    const char *img = argv[2], *outdir = argv[3];
    const char *stem_full = strrchr(img, '/');
    stem_full = stem_full ? stem_full + 1 : img;
    char stem[256];
    snprintf(stem, sizeof stem, "%s", stem_full);
    char *dot = strrchr(stem, '.');
    if (dot) *dot = 0;

    printf("PROBE=W79A-FRAMES\nIMAGE=%s\nSHIM=%s\n", img, argv[1]);

    void *fac = NULL;
    int32_t hr = sym_factory(0x0100, &fac);
    printf("FACTORY_HR=%d\n", hr);
    if (hr != 0 || !fac) return 2;

    int fd = open(img, O_RDONLY);
    if (fd < 0) { printf("OPEN_FAIL=1\n"); return 2; }
    void *dec = NULL;
    hr = sym_dec_fd(fac, (intptr_t)fd, NULL, 0, &dec);
    printf("DECODER_HR=%d\n", hr);
    if (hr != 0 || !dec) return 2;

    if (sym_container) {
        unsigned char g[16]; memset(g, 0, sizeof g);
        int32_t ch = sym_container(dec, g);
        printf("CONTAINER_HR=%d CONTAINER_GUID=", ch);
        for (int i = 0; i < 16; i++) printf("%02X", g[i]);
        printf("\n");
    }

    uint32_t n = 0;
    hr = sym_count(dec, &n);
    printf("GETFRAMECOUNT_HR=%d COUNT=%u\n", hr, n);

    uint32_t dw = 0, dh = 0;
    if (sym_size(dec, &dw, &dh) == 0) printf("DECODER_SIZE=%ux%u\n", dw, dh);

    /* 越界帧：必须**如实失败**（不许返回一个"看起来能用"的帧） */
    void *oof = NULL;
    int32_t oor = sym_getframe(dec, n, &oof);
    printf("OOR_INDEX=%u OOR_HR=%d OOR_HANDLE=%s\n", n, oor, oof ? "nonnull" : "null");

    /* 先倒序取一遍（帧号与解码顺序无关性），再正序取一遍 */
    for (int pass = 0; pass < 2; pass++) {
        for (uint32_t k = 0; k < n; k++) {
            uint32_t i = (pass == 0) ? (n - 1 - k) : k;
            void *frm = NULL;
            int32_t fhr = sym_getframe(dec, i, &frm);
            printf("GETFRAME i=%u PASS=%d HR=%d HANDLE=%s\n", i, pass, fhr, frm ? "nonnull" : "null");
            if (fhr != 0 || !frm) continue;

            uint32_t w = 0, h = 0;
            int32_t shr = sym_size(frm, &w, &h);
            unsigned char fmt[16]; memset(fmt, 0, sizeof fmt);
            int32_t phr = sym_pixfmt ? sym_pixfmt(frm, fmt) : -1;
            size_t rowBytes = (size_t)w * 4, need = rowBytes * (size_t)h;
            unsigned char *buf = calloc(need ? need : 1, 1);
            int32_t chr = sym_copypixels(frm, NULL, (uint32_t)rowBytes, (uint32_t)need, buf);
            printf("FRAME i=%u PASS=%d SIZE_HR=%d %ux%u PIXFMT_HR=%d COPY_HR=%d FIRST=%02X%02X%02X%02X FNV=%016llx\n",
                   i, pass, shr, w, h, phr, chr, buf[0], buf[1], buf[2], buf[3],
                   (unsigned long long)fnv(buf, need));
            if (chr == 0 && need) {
                char p[1024];
                /* 两趟各落一份（p0 = 倒序先取、p1 = 正序）：判据靠**字节**比"次序无关"，
                 * 只比状态码抓不住"换个顺序内容就变"这种脏。*/
                snprintf(p, sizeof p, "%s/%s.shimf%up%d.bgra", outdir, stem, i, pass);
                FILE *fp = fopen(p, "wb");
                if (fp) { fwrite(buf, 1, need, fp); fclose(fp); }
            }
            free(buf);

            /* 帧时序：/grctlext/Delay（GIF 的 GCE 延迟，单位 1/100 s） */
            if (pass == 1 && sym_framemeta && sym_metaby) {
                void *rdr = NULL;
                int32_t mhr = sym_framemeta(frm, &rdr);
                printf("FRAMEMETA i=%u HR=%d READER=%s\n", i, mhr, rdr ? "nonnull" : "null");
                if (mhr == 0 && rdr) {
                    uint16_t name[64]; utf16("/grctlext/Delay", name);
                    pv_t pv; memset(&pv, 0, sizeof pv);
                    int32_t qhr = sym_metaby(rdr, name, &pv);
                    uint32_t val = (uint32_t)(pv.v1 & 0xFFFFu);
                    printf("DELAY i=%u VT=%u HR=%d VALUE=%u\n", i, pv.vt, qhr, val);
                }
            }
        }
    }

    close(fd);
    printf("PROBE_DONE=1\n");
    return 0;
}
