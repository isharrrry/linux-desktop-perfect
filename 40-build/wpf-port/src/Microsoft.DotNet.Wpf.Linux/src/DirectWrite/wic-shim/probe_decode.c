/* T2 · WIC shim 前置验证：用 Skia C API 从 fd 解出一张 PNG 的真实尺寸/像素
 * 目的：在写 22 个 *_Proxy 之前，先证明 (1) 能 dlopen 到 sk_* 符号；
 *      (2) sk_imageinfo_t 的结构布局假设正确；(3) 解码结果与文件真实内容一致。
 * 编译：gcc -O1 -o probe_decode probe_decode.c -ldl
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* Skia C API 的公开结构与枚举（sk_imageinfo.h / sk_colortype.h），
 * 这里按上游头文件重述 —— 若布局猜错，下面 width/height 会读出垃圾。 */
typedef struct { void *colorspace; int32_t width, height; int colorType; int alphaType; } sk_imageinfo_t;
typedef void sk_data_t; typedef void sk_codec_t;
typedef struct { int fOriginX, fOriginY, fWidth, fHeight; } sk_codec_options_t;

typedef sk_data_t *(*fn_data_new_with_copy)(const void *, size_t);
typedef sk_codec_t *(*fn_codec_new_from_data)(sk_data_t *);
typedef int (*fn_codec_get_info)(sk_codec_t *, sk_imageinfo_t *);
typedef int (*fn_codec_get_pixels)(sk_codec_t *, const sk_imageinfo_t *, void *, size_t, const sk_codec_options_t *);
typedef void (*fn_codec_destroy)(sk_codec_t *);
typedef void (*fn_data_unref)(sk_data_t *);   /* 正确名字是 sk_data_unref（没有 sk_data_destroy）*/

int main(int argc, char **argv) {
    const char *path = argc > 1 ? argv[1] : "/home/links-dev/netTest/wpf-linux-20260906/wpf-linux/samples/HelloMil/screenshot.png";
    const char *lib = argc > 2 ? argv[2] : "libSkiaSharp.so";
    setvbuf(stdout, NULL, _IONBF, 0);   /* 崩溃时也要看到已经走到哪一步 */

    void *h = dlopen(lib, RTLD_NOW);
    if (!h) { printf("DLOPEN_FAIL=%s\n", dlerror()); return 1; }
    printf("DLOPEN_OK=%s\n", lib);

    fn_data_new_with_copy data_new = (fn_data_new_with_copy)dlsym(h, "sk_data_new_with_copy");
    fn_codec_new_from_data codec_new = (fn_codec_new_from_data)dlsym(h, "sk_codec_new_from_data");
    fn_codec_get_info get_info = (fn_codec_get_info)dlsym(h, "sk_codec_get_info");
    fn_codec_get_pixels get_pixels = (fn_codec_get_pixels)dlsym(h, "sk_codec_get_pixels");
    fn_codec_destroy codec_destroy = (fn_codec_destroy)dlsym(h, "sk_codec_destroy");
    fn_data_unref data_unref = (fn_data_unref)dlsym(h, "sk_data_unref");
    printf("SYMS_OK=%d\n", data_new && codec_new && get_info && get_pixels && codec_destroy && data_unref);
    if (!(data_new && codec_new && get_info && get_pixels && codec_destroy && data_unref)) return 2;

    /* ① 用 fd + pread 把整份文件读进内存 —— 这正是 WIC shim 的做法 */
    int fd = open(path, O_RDONLY);
    if (fd < 0) { perror("open"); return 3; }
    off_t size = lseek(fd, 0, SEEK_END); lseek(fd, 0, SEEK_SET);
    unsigned char *buf = malloc((size_t)size);
    ssize_t got = pread(fd, buf, (size_t)size, 0);
    printf("FD=%d SIZE=%ld PREAD=%ld\n", fd, (long)size, (long)got);
    printf("MAGIC=%02X%02X%02X%02X\n", buf[0], buf[1], buf[2], buf[3]);

    /* ② 交给 Skia 解码 */
    sk_data_t *data = data_new(buf, (size_t)size);
    sk_codec_t *codec = codec_new(data);
    printf("CODEC=%s\n", codec ? "ok" : "null");
    if (!codec) return 4;

    sk_imageinfo_t info; memset(&info, 0, sizeof info);
    int ok = get_info(codec, &info);
    printf("GET_INFO_RAW=%d (无 HRESULT 语义，别用它判断成败) WIDTH=%d HEIGHT=%d COLOR_TYPE=%d ALPHA_TYPE=%d\n",
           ok, info.width, info.height, info.colorType, info.alphaType);

    /* ③ 请求 BGRA（WPF 的 Bgra32 就是它）并解像素 */
    /* 不覆盖 colorType：先用 get_info 报的原生格式解一次；
     * 若需要 BGRA（WPF 的 Bgra32），后续在 shim 里做一次字节交换（不要猜枚举值）。 */
    size_t rowBytes = (size_t)info.width * 4;
    unsigned char *pixels = malloc(rowBytes * (size_t)info.height);
    /* 主控实测：options 传 NULL 合法（hr=0）；传"零值结构体"反而 InvalidParameters(5)
     * —— 所以这里**必须**传 NULL。 */
    int got_px = get_pixels(codec, &info, pixels, rowBytes, NULL);
    printf("GET_PIXELS=%d FIRST4=%02X%02X%02X%02X LAST4=%02X%02X%02X%02X\n",
           got_px, pixels[0], pixels[1], pixels[2], pixels[3],
           pixels[rowBytes * info.height - 4], pixels[rowBytes * info.height - 3],
           pixels[rowBytes * info.height - 2], pixels[rowBytes * info.height - 1]);

    /* ④ 抽样：中心像素（用于后面与托管 SkiaSharp 直读的逐点比对） */
    long mid = (long)(info.height / 2) * (long)rowBytes + (long)(info.width / 2) * 4;
    printf("CENTER_BGRA=%02X%02X%02X%02X\n", pixels[mid], pixels[mid+1], pixels[mid+2], pixels[mid+3]);

    /* 主控在 AbiProbe 里测到 (22,16) = BGRA 102,51,34,255：这里独立复核同一个点 */
    long p2216 = 16L * (long)rowBytes + 22L * 4;
    printf("PIXEL_22_16_BGRA=%u,%u,%u,%u\n", pixels[p2216], pixels[p2216+1], pixels[p2216+2], pixels[p2216+3]);

    /* 统计非白像素数：排除"整张图都是空白"的假绿 */
    long nonWhite = 0;
    for (long i = 0; i < (long)info.height * (long)rowBytes; i += 4)
        if (!(pixels[i] == 0xFF && pixels[i+1] == 0xFF && pixels[i+2] == 0xFF)) nonWhite++;
    printf("NON_WHITE_PIXELS=%ld / %d\n", nonWhite, info.width * info.height);

    codec_destroy(codec); data_unref(data); free(pixels); free(buf); close(fd); dlclose(h);
    /* get_info 的返回值**没有 HRESULT 语义**（主控实测是随机值）——只看结构体与 get_pixels。 */
    return got_px == 0 ? 0 : 5;
}
