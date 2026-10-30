// TIFF 解码（D-018：五格式里的 TIFF）—— 复用系统的 libtiff（dlopen，不静态依赖）。
//
// 【为什么用 dlopen 而不是自写】TIFF 的标签体系 + 多种压缩（LZW/PackBits/Deflate/JPEG/CCITT…）
//   自写等于重造一个 libtiff；而系统本来就带 `libtiff.so.5`。这里的取舍是：
//   **能借成熟实现就借，但要把"借不到"的情形做成诚实的失败**（E_NOTIMPL），不静默降级。
//
// 【API 选择】只用 4 个入口：`TIFFOpen/TIFFGetField/TIFFReadRGBAImage/TIFFClose`。
//   刻意**不碰** `TIFFClientOpen` 与任何 struct 字段 —— 那些跨版本 ABI 不稳。
//   代价：需要把一个临时文件写到磁盘（`TIFFOpen` 只吃路径）。临时文件在 /tmp 下，用完即删。

#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include "png_decode.h"

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

int ldp_is_tiff(const uint8_t *d, size_t len)
{
    if (len < 4) return 0;
    return (d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) ||
           (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42);
}

typedef struct tiff TIFF;
typedef TIFF *(*fn_open)(const char *, const char *);
typedef int (*fn_getfield)(TIFF *, uint32_t, ...);
typedef int (*fn_readrgba)(TIFF *, uint32_t, uint32_t, uint32_t *, int);
typedef void (*fn_close)(TIFF *);

typedef struct {
    int ok;
    fn_open open;
    fn_getfield getfield;
    fn_readrgba readrgba;
    fn_close close;
} tiff_api;

static tiff_api s_api;
static int s_probed;

static void probe_tiff(void)
{
    if (s_probed) return;
    s_probed = 1;
    void *h = dlopen("libtiff.so.5", RTLD_NOW | RTLD_LOCAL);
    if (!h) h = dlopen("libtiff.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) h = dlopen("libtiff.so.6", RTLD_NOW | RTLD_LOCAL);
    if (!h) return;
    s_api.open = (fn_open)dlsym(h, "TIFFOpen");
    s_api.getfield = (fn_getfield)dlsym(h, "TIFFGetField");
    s_api.readrgba = (fn_readrgba)dlsym(h, "TIFFReadRGBAImage");
    s_api.close = (fn_close)dlsym(h, "TIFFClose");
    s_api.ok = (s_api.open && s_api.getfield && s_api.readrgba && s_api.close) ? 1 : 0;
}

int32_t ldp_tiff_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap)
{
    if (!data || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    if (!ldp_is_tiff(data, len)) { snprintf(error, cap, "不是 TIFF（头不匹配）"); return E_INVALIDARG_; }
    probe_tiff();
    if (!s_api.ok) {
        snprintf(error, cap, "系统没有可用的 libtiff（dlopen libtiff.so.5/.so 失败）—— 如实失败，不自写半个 TIFF");
        return E_NOTIMPL_;
    }
    // TIFFOpen 只吃路径：写一个临时文件
    char path[] = "/tmp/ldp-tiff-XXXXXX";
    int fd = mkstemp(path);
    if (fd < 0) { snprintf(error, cap, "建临时文件失败"); return E_NOTIMPL_; }
    size_t written = 0;
    while (written < len) {
        ssize_t n = write(fd, data + written, len - written);
        if (n <= 0) { close(fd); unlink(path); snprintf(error, cap, "写临时文件失败"); return E_NOTIMPL_; }
        written += (size_t)n;
    }
    close(fd);

    TIFF *tif = s_api.open(path, "r");
    if (!tif) { unlink(path); snprintf(error, cap, "libtiff 打不开这个 TIFF"); return E_INVALIDARG_; }
    uint32_t w = 0, h = 0;
    if (!s_api.getfield(tif, 256, &w) || !s_api.getfield(tif, 257, &h) || !w || !h) {
        s_api.close(tif); unlink(path);
        snprintf(error, cap, "取不到 ImageWidth/ImageLength");
        return E_INVALIDARG_;
    }
    uint32_t *raster = (uint32_t *)malloc((size_t)w * h * 4);
    if (!raster) { s_api.close(tif); unlink(path); return E_NOTIMPL_; }
    // stopOnError=0：有坏条带也尽量给结果（libtiff 会返回部分数据）
    int ok = s_api.readrgba(tif, w, h, raster, 0);
    s_api.close(tif);
    unlink(path);
    if (!ok) { free(raster); snprintf(error, cap, "TIFFReadRGBAImage 失败"); return E_NOTIMPL_; }

    uint8_t *bgra = (uint8_t *)malloc((size_t)w * h * 4);
    if (!bgra) { free(raster); return E_NOTIMPL_; }
    // libtiff 的 RGBA 是 **ABGR 打包进 uint32**（小端下内存序 = R,G,B,A）→ 转 BGRA
    //   并且 TIFF 的行序是**自下而上**（raster[0] 是最后一行）—— 与 BMP 同一个坑。
    for (uint32_t y = 0; y < h; ++y) {
        const uint8_t *src = (const uint8_t *)(raster + (size_t)(h - 1 - y) * w);
        uint8_t *dst = bgra + (size_t)y * w * 4;
        for (uint32_t x = 0; x < w; ++x) {
            dst[0] = src[2];   // B
            dst[1] = src[1];   // G
            dst[2] = src[0];   // R
            dst[3] = src[3];   // A
            src += 4;
            dst += 4;
        }
    }
    free(raster);
    out->width = w;
    out->height = h;
    out->has_alpha = 1;
    out->bgra = bgra;
    return 0;
}
