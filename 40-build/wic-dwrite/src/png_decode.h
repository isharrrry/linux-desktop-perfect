#ifndef LDP_PNG_DECODE_H
#define LDP_PNG_DECODE_H
#include <stdint.h>
#include <stddef.h>

// 所有"能被 WPF 当位图源包进 SafeMILHandle"的对象共用的头（定义在 wic_proxy.c）。
// 第一个字段必须是 magic：WicShim_Release 靠它决定回收哪部分载荷。
typedef struct wic_object {
    uint32_t magic;
    int refcount;
    struct wic_object *next;
} wic_object;

// 把一个对象登记进 WIC 的活句柄表（引用计数置 1）。
void wic_track_add(void *obj);

typedef struct {
    uint32_t width, height;
    int has_alpha;
    uint8_t *bgra;          // width*height*4，BGRA8888
    uint8_t *icc;           // iCCP 内嵌的 ICC profile（无则为 NULL）
    uint32_t icc_len;
} ldp_png_image;

int32_t ldp_png_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t error_cap);
void ldp_png_free(ldp_png_image *img);

#endif
