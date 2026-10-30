// BMP 解码（D-018：五格式里的 BMP）—— 只做未压缩的 24/32bpp，其余明确 E_NOTIMPL。
//
// BMP 的像素行是 **自下而上** 存的，且每行按 4 字节对齐；这两点是这个格式最容易踩的坑。

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "png_decode.h"   /* 复用 ldp_png_image（同一个"BGRA8888 位图"约定） */

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

static uint16_t le16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint32_t le32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

int ldp_is_bmp(const uint8_t *data, size_t len)
{
    return len >= 2 && data[0] == 'B' && data[1] == 'M';
}

int32_t ldp_bmp_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap)
{
    if (!data || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    if (!ldp_is_bmp(data, len) || len < 54) { snprintf(error, cap, "不是 BMP 或头不足"); return E_INVALIDARG_; }
    uint32_t pixel_offset = le32(data + 10);
    uint32_t header_size = le32(data + 14);
    if (header_size < 40) { snprintf(error, cap, "只支持 BITMAPINFOHEADER 及以后（got %u）", header_size); return E_NOTIMPL_; }
    int32_t width = (int32_t)le32(data + 18);
    int32_t height_raw = (int32_t)le32(data + 22);
    uint16_t bpp = le16(data + 28);
    uint32_t compression = le32(data + 30);
    if (compression != 0) { snprintf(error, cap, "只支持 BI_RGB（compression=%u）", compression); return E_NOTIMPL_; }
    if (bpp != 24 && bpp != 32) { snprintf(error, cap, "只支持 24/32bpp（got %u）", bpp); return E_NOTIMPL_; }
    if (width <= 0) { snprintf(error, cap, "宽度非法 %d", width); return E_INVALIDARG_; }
    // height 为负 = 自上而下存储（BMP 的另一种布局）
    int top_down = height_raw < 0;
    uint32_t height = (uint32_t)(top_down ? -height_raw : height_raw);
    if (height == 0) { snprintf(error, cap, "高度非法 0"); return E_INVALIDARG_; }

    const size_t src_stride = ((size_t)width * bpp / 8 + 3) & ~(size_t)3;   // 4 字节对齐
    if ((size_t)pixel_offset + src_stride * height > len) { snprintf(error, cap, "像素数据越界"); return E_INVALIDARG_; }

    uint8_t *bgra = (uint8_t *)malloc((size_t)width * height * 4);
    if (!bgra) { snprintf(error, cap, "内存不足"); return E_NOTIMPL_; }
    for (uint32_t y = 0; y < height; ++y) {
        uint32_t src_y = top_down ? y : (height - 1 - y);
        const uint8_t *row = data + pixel_offset + (size_t)src_y * src_stride;
        uint8_t *dst = bgra + (size_t)y * (size_t)width * 4;
        for (int32_t x = 0; x < width; ++x) {
            const uint8_t *p = row + (size_t)x * (bpp / 8);
            // BMP 是 BGR(A) 顺序，与我们的 BGRA 一致；24bpp 时补不透明 alpha
            dst[0] = p[0];
            dst[1] = p[1];
            dst[2] = p[2];
            dst[3] = (bpp == 32) ? p[3] : 255;
            dst += 4;
        }
    }
    out->width = (uint32_t)width;
    out->height = height;
    out->has_alpha = (bpp == 32);
    out->bgra = bgra;
    return 0;
}
