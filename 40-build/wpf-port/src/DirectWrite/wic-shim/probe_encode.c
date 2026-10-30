/* probe_encode.c —— **实测** Skia 编码 C API 的枚举值与行为（不猜结构体/枚举）*/
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef void sk_image_t; typedef void sk_data_t;
typedef struct { void *colorspace; int32_t width, height, colorType, alphaType; } sk_imageinfo_t;
int main(int argc, char **argv)
{
    void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!h) { printf("SKIP dlopen: %s\n", dlerror()); return 2; }
    sk_image_t *(*new_raster_copy)(const sk_imageinfo_t *, const void *, size_t) = dlsym(h, "sk_image_new_raster_copy");
    sk_data_t *(*encode_specific)(const sk_image_t *, int, int) = dlsym(h, "sk_image_encode_specific");
    const void *(*data_get_data)(const sk_data_t *) = dlsym(h, "sk_data_get_data");
    size_t (*data_get_size)(const sk_data_t *) = dlsym(h, "sk_data_get_size");
    void (*data_unref)(sk_data_t *) = dlsym(h, "sk_data_unref");
    if (!new_raster_copy || !encode_specific || !data_get_data || !data_get_size) { printf("SKIP 符号缺失\n"); return 2; }
    unsigned char px[2*2*4];
    for (int i = 0; i < 4; i++) { px[i*4+0]=0x10*i; px[i*4+1]=0x20*i; px[i*4+2]=0x30*i; px[i*4+3]=0xFF; }
    sk_imageinfo_t info = {0, 2, 2, 6 /*BGRA8888*/, 3 /*unpremul*/};
    sk_image_t *img = new_raster_copy(&info, px, 2*4);
    if (!img) { printf("FAIL sk_image_new_raster_copy 返回空（colorType/alphaType 取值可能不对）\n"); return 1; }
    printf("IMG_OK\n");
    for (int fmt = 0; fmt <= 12; fmt++) {
        sk_data_t *d = encode_specific(img, fmt, 90);
        if (!d) { printf("  fmt=%-2d -> (null)\n", fmt); continue; }
        const unsigned char *p = (const unsigned char *)data_get_data(d);
        size_t n = data_get_size(d);
        char magic[9] = {0};
        for (int i = 0; i < 8 && (size_t)i < n; i++) magic[i] = (p[i] >= 32 && p[i] < 127) ? (char)p[i] : '.';
        printf("  fmt=%-2d -> %zu 字节 magic=%s  PNG=%d JPEG=%d\n", fmt, n, magic,
               (n>4 && p[0]==0x89 && p[1]=='P' && p[2]=='N' && p[3]=='G'),
               (n>3 && p[0]==0xFF && p[1]==0xD8 && p[2]==0xFF));
        if (data_unref) data_unref(d);
    }
    return 0;
}
