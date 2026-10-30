#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdio.h>
#include <stdint.h>
typedef int32_t (*fn_create)(uint32_t, void **);
typedef int32_t (*fn_fromfile)(void *, const uint16_t *, void *, uint32_t, uint32_t, void **);
typedef int32_t (*fn_getsize)(void *, uint32_t *, uint32_t *);
int main(int argc, char **argv)
{
    const char *file = argc > 1 ? argv[1] : "/tmp/ldp-test.png";
    void *h = dlopen("40-build/wic-dwrite/bin/libwpfwic.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) h = dlopen("bin/libwpfwic.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("{\"loaded\":false}\n"); return 1; }
    fn_create create = (fn_create)dlsym(h, "WICCreateImagingFactory_Proxy");
    fn_fromfile fromfile = (fn_fromfile)dlsym(h, "IWICImagingFactory_CreateDecoderFromFilename_Proxy");
    fn_getsize getsize = (fn_getsize)dlsym(h, "IWICBitmapSource_GetSize_Proxy");
    void *f = NULL;
    int32_t hr_create = create ? create(0x0230, &f) : -1;
    static uint16_t w[1024];
    for (int i = 0; file[i] && i < 1023; ++i) w[i] = (uint16_t)file[i];
    void *d = NULL;
    int32_t hr_file = fromfile ? fromfile(f, w, NULL, 0, 0, &d) : -1;
    uint32_t ww = 0, hh = 0;
    int32_t hr_size = (getsize && d) ? getsize(d, &ww, &hh) : -1;
    printf("{\"loaded\":true,\"hr_create\":%d,\"decoder_nonnull\":%s,\"hr_fromfile\":%d,\"hr_size\":%d,\"dims\":[%u,%u]}\n",
           hr_create, d ? "true" : "false", hr_file, hr_size, ww, hh);
    return (hr_create == 0 && d && hr_size == 0 && ww > 0) ? 0 : 2;
}
