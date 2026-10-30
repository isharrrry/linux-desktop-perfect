/* T2 · WIC shim 读路径探针：dlopen libwpfwic.so → 走 *_Proxy 全链 → 与直解像素比对
 * 编译：gcc -O1 -o probe_shim probe_shim.c -ldl
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef int32_t (*fn_factory)(uint32_t, void **);
typedef int32_t (*fn_dec_from_fd)(void *, intptr_t, void *, uint32_t, void **);
typedef int32_t (*fn_u32_out)(void *, uint32_t *);
typedef int32_t (*fn_frame)(void *, uint32_t, void **);
typedef int32_t (*fn_two_u32)(void *, uint32_t *, uint32_t *);
typedef int32_t (*fn_guid)(void *, void *);
typedef int32_t (*fn_two_dbl)(void *, double *, double *);
typedef int32_t (*fn_copypixels)(void *, void *, uint32_t, uint32_t, void *);
typedef int32_t (*fn_selftest)(const char *, int32_t *, int32_t *, uint32_t *);
typedef int32_t (*fn_notimpl)(void);

static const char *HR(int32_t hr) {
    static char b[32];
    if (hr == 0) return "S_OK";
    snprintf(b, sizeof b, "0x%08X", (uint32_t)hr);
    return b;
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    const char *shim = argc > 1 ? argv[1] : "libwpfwic.so";
    const char *png  = argc > 2 ? argv[2] : "/home/links-dev/netTest/wpf-linux-20260906/wpf-linux/samples/HelloMil/screenshot.png";
    const char *notimage = argc > 3 ? argv[3] : "/etc/hostname";   /* 非图像文件 */

    void *h = dlopen(shim, RTLD_NOW);
    if (!h) { printf("DLOPEN_FAIL=%s\n", dlerror()); return 1; }

    fn_factory      f_factory   = (fn_factory)dlsym(h, "WICCreateImagingFactory_Proxy");
    fn_dec_from_fd  f_decfromfd = (fn_dec_from_fd)dlsym(h, "IWICImagingFactory_CreateDecoderFromFileHandle_Proxy");
    fn_u32_out      f_framecnt  = (fn_u32_out)dlsym(h, "IWICBitmapDecoder_GetFrameCount_Proxy");
    fn_frame        f_getframe  = (fn_frame)dlsym(h, "IWICBitmapDecoder_GetFrame_Proxy");
    fn_guid         f_container = (fn_guid)dlsym(h, "IWICBitmapDecoder_GetContainerFormat_Proxy");
    fn_two_u32      f_getsize   = (fn_two_u32)dlsym(h, "IWICBitmapSource_GetSize_Proxy");
    fn_guid         f_getpixfmt = (fn_guid)dlsym(h, "IWICBitmapSource_GetPixelFormat_Proxy");
    fn_two_dbl      f_getres    = (fn_two_dbl)dlsym(h, "IWICBitmapSource_GetResolution_Proxy");
    fn_copypixels   f_copypixels= (fn_copypixels)dlsym(h, "IWICBitmapSource_CopyPixels_Proxy");
    fn_notimpl      f_meta      = (fn_notimpl)dlsym(h, "IWICBitmapFrameDecode_GetMetadataQueryReader_Proxy");
    fn_selftest     f_selftest  = (fn_selftest)dlsym(h, "WicShim_SelfTest");

    printf("EXPORTS_OK=%d (factory=%d decfromfd=%d size=%d copypixels=%d)\n",
           f_factory && f_decfromfd && f_getsize && f_copypixels,
           !!f_factory, !!f_decfromfd, !!f_getsize, !!f_copypixels);
    if (!(f_factory && f_decfromfd && f_framecnt && f_getframe && f_getsize && f_getpixfmt && f_getres && f_copypixels))
        return 2;

    /* ① factory */
    void *factory = NULL;
    int32_t hr_f = f_factory(0x0100, &factory);
    printf("FACTORY=%s handle=%p\n", HR(hr_f), factory);
    if (!factory) return 3;

    /* ② 打开真实 fd 并交给 shim（这正是上游 CreateDecoderFromFileHandle 的用法） */
    int fd = open(png, O_RDONLY);
    printf("FD=%d\n", fd);
    void *decoder = NULL;
    int32_t hr = f_decfromfd(factory, (intptr_t)fd, NULL, 0, &decoder);
    printf("CREATE_FROM_FD=%s decoder=%p\n", HR(hr), decoder);
    if (hr != 0) return 4;

    uint32_t frames = 0;
    int32_t hr_fc = f_framecnt(decoder, &frames);
    printf("FRAME_COUNT=%s n=%u\n", HR(hr_fc), frames);

    void *frame = NULL;
    int32_t hr_gf = f_getframe(decoder, 0, &frame);
    printf("GET_FRAME=%s frame=%p\n", HR(hr_gf), frame);
    if (!frame) return 5;

    uint32_t w = 0, hh = 0;
    int32_t hr_gs = f_getsize(frame, &w, &hh);
    printf("GET_SIZE=%s %ux%u\n", HR(hr_gs), w, hh);

    uint8_t fmt[16] = {0};
    printf("GET_PIXELFORMAT=%s\n", HR(f_getpixfmt(frame, fmt)));
    printf("PIXELFORMAT_D1=%08X\n", *(uint32_t *)fmt);

    double dx = 0, dy = 0;
    int32_t hr_gr = f_getres(frame, &dx, &dy);
    printf("GET_RESOLUTION=%s %gx%g\n", HR(hr_gr), dx, dy);

    /* ③ CopyPixels 全图 */
    size_t need = (size_t)w * 4 * hh;
    unsigned char *buf = malloc(need);
    int32_t hr_cp = f_copypixels(frame, NULL, 0, (uint32_t)need, buf);
    printf("COPY_PIXELS=%s need=%zu\n", HR(hr_cp), need);

    long p2216 = 16L * (long)w * 4 + 22L * 4;
    printf("SHIM_PIXEL_22_16_BGRA=%u,%u,%u,%u\n", buf[p2216], buf[p2216+1], buf[p2216+2], buf[p2216+3]);
    long mid = (long)(hh / 2) * (long)w * 4 + (long)(w / 2) * 4;
    printf("SHIM_CENTER_BGRA=%u,%u,%u,%u\n", buf[mid], buf[mid+1], buf[mid+2], buf[mid+3]);
    long nonWhite = 0;
    for (size_t i = 0; i < need; i += 4) if (!(buf[i]==0xFF && buf[i+1]==0xFF && buf[i+2]==0xFF)) nonWhite++;
    printf("SHIM_NON_WHITE=%ld / %u\n", nonWhite, w * hh);

    /* ④ 与同进程的"直解"（probe_decode 的路径）逐字节比对由 shell 侧 diff 完成；
     *    这里把哈希汇总出来。 */
    uint64_t sum = 1469598103934665603ULL;
    for (size_t i = 0; i < need; i++) { sum ^= buf[i]; sum *= 1099511628211ULL; }
    printf("SHIM_PIXEL_FNV1A=%016llX\n", (unsigned long long)sum);

    /* ⑤ 失败路径 */
    void *d2 = NULL;
    printf("FAIL_BADHANDLE=%s\n", HR(f_decfromfd(factory, -1, NULL, 0, &d2)));

    const char *nonImage = notimage;
    int fd2 = open(nonImage, O_RDONLY);
    void *d3 = NULL;
    printf("FAIL_NOTIMAGE(%s) fd=%d -> %s\n", nonImage, fd2,
           HR(f_decfromfd(factory, (intptr_t)fd2, NULL, 0, &d3)));

    /* 截断的 PNG：只写前 64 字节 */
    char tmp[] = "/tmp/wic-truncated-XXXXXX";
    int tfd = mkstemp(tmp);
    { char head[64]; int s = open(png, O_RDONLY); read(s, head, 64); write(tfd, head, 64); close(s); }
    lseek(tfd, 0, SEEK_SET);
    void *d4 = NULL;
    printf("FAIL_TRUNCATED -> %s\n", HR(f_decfromfd(factory, (intptr_t)tfd, NULL, 0, &d4)));
    unlink(tmp);

    /* ⑥ 未实现面必须是明确 HRESULT */
    printf("NOTIMPL_METADATA=%s (期望 0x88982F04)\n", HR(f_meta()));

    /* ⑦ 自检导出 */
    if (f_selftest) {
        int32_t sw = 0, sh = 0; uint32_t px = 0;
        int32_t hr_st = f_selftest(png, &sw, &sh, &px);
        printf("SELFTEST=%s %dx%d first=%08X\n", HR(hr_st), sw, sh, px);
    }

    free(buf);
    return 0;
}
