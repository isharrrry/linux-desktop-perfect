/* 流入口探针（D-018 第三步）：不依赖托管，直接构造一个符合上游 `StreamDescriptor` 布局的
 * 描述符（15 个槽：pfnDispose/pfnRead/pfnSeek/…），把 PNG 当成"流"喂给
 * `IWICImagingFactory_CreateDecoderFromStream_Proxy`，验证：
 *   ①工厂能建；②流入口能读；③解码出的尺寸正确。
 *
 * 口径说明：这里模拟的是 **milcore bridge 的 LdpMilStreamRead 约定**（句柄 → 读字节），
 * 而真正的 bridge 由 C# 那边提供；探针用同样的约定自证 libwpfwic 的流路径成立。 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint8_t *data; size_t len, pos; } fake_stream;

/* 与 StreamAsIStream.StreamDescriptor 的槽序一致：0=Dispose 1=Read 2=Seek 3=Stat 4=Write … */
typedef void (*dispose_fn)(void *sd);
typedef int32_t (*read_fn_t)(void *sd, uint8_t *buf, uint32_t cb, uint32_t *read);
typedef int32_t (*seek_fn_t)(void *sd, int64_t offset, uint32_t origin, int64_t *pos);

typedef struct {
    dispose_fn pfnDispose;
    read_fn_t  pfnRead;
    seek_fn_t  pfnSeek;
    void *pfnStat, *pfnWrite, *pfnCopyTo, *pfnSetSize, *pfnRevert, *pfnCommit;
    void *pfnLockRegion, *pfnUnlockRegion, *pfnClone, *pfnCanWrite, *pfnCanSeek;
    fake_stream *stream;
} fake_sd;

static void sd_dispose(void *sd) { (void)sd; }
static int32_t sd_read(void *sd, uint8_t *buf, uint32_t cb, uint32_t *read)
{
    fake_sd *d = (fake_sd *)sd;
    size_t left = d->stream->len - d->stream->pos;
    size_t n = cb < left ? cb : left;
    memcpy(buf, d->stream->data + d->stream->pos, n);
    d->stream->pos += n;
    if (read) *read = (uint32_t)n;
    return 0;
}
static int32_t sd_seek(void *sd, int64_t offset, uint32_t origin, int64_t *pos)
{
    fake_sd *d = (fake_sd *)sd;
    size_t base = (origin == 0) ? 0 : (origin == 1 ? d->stream->pos : d->stream->len);
    long np = (long)base + offset;
    if (np < 0) np = 0;
    if ((size_t)np > d->stream->len) np = (long)d->stream->len;
    d->stream->pos = (size_t)np;
    if (pos) *pos = np;
    return 0;
}

/* 让 libwpfwic 的 dlopen("wpfgfx_cor3.so", RTLD_NOLOAD) 能找到 LdpMilStreamRead：
 * 探针把同名符号定义在**自己的可执行文件**里并导出（-rdynamic）。 */
int32_t LdpMilStreamRead(void *handle, uint8_t *buffer, uint32_t cb, uint32_t *read)
{
    return sd_read(handle, buffer, cb, read);
}

typedef int32_t (*fn_create)(uint32_t, void **);
typedef int32_t (*fn_fromstream)(void *, void *, void *, uint32_t, void **);
typedef int32_t (*fn_getsize)(void *, uint32_t *, uint32_t *);

int main(int argc, char **argv)
{
    const char *file = argc > 1 ? argv[1] : "testdata/rgb-7x5.png";
    FILE *f = fopen(file, "rb");
    if (!f) { printf("{\"ok\":false,\"error\":\"打不开 %s\"}\n", file); return 1; }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *data = (uint8_t *)malloc((size_t)n);
    if (fread(data, 1, (size_t)n, f) != (size_t)n) { fclose(f); return 2; }
    fclose(f);
    fake_stream fs = { data, (size_t)n, 0 };
    fake_sd sd;
    memset(&sd, 0, sizeof(sd));
    sd.pfnDispose = sd_dispose;
    sd.pfnRead = sd_read;
    sd.pfnSeek = sd_seek;
    sd.stream = &fs;

    void *h = dlopen("40-build/wic-dwrite/bin/libwpfwic.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) h = dlopen("bin/libwpfwic.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("{\"ok\":false,\"error\":\"libwpfwic 打不开\"}\n"); return 3; }
    fn_create create = (fn_create)dlsym(h, "WICCreateImagingFactory_Proxy");
    fn_fromstream fromstream = (fn_fromstream)dlsym(h, "IWICImagingFactory_CreateDecoderFromStream_Proxy");
    fn_getsize getsize = (fn_getsize)dlsym(h, "IWICBitmapSource_GetSize_Proxy");
    void *factory = NULL;
    int32_t hr_create = create ? create(0x0230, &factory) : -1;
    void *decoder = NULL;
    int32_t hr_stream = fromstream ? fromstream(factory, &sd, NULL, 0, &decoder) : -1;
    uint32_t w = 0, h2 = 0;
    int32_t hr_size = (getsize && decoder) ? getsize(decoder, &w, &h2) : -1;
    printf("{\"hr_create\":%d,\"hr_stream\":%d,\"decoder_nonnull\":%s,\"hr_size\":%d,\"dims\":[%u,%u]}\n",
           hr_create, hr_stream, decoder ? "true" : "false", hr_size, w, h2);
    free(data);
    return (hr_create == 0 && decoder && hr_size == 0 && w > 0) ? 0 : 4;
}
