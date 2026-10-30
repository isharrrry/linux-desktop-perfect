// PNG 解码（D-018 第二步）：不依赖 libpng，直接用 zlib 的 inflate。
//
// 只做"WIC 需要的那部分"：8-bit RGB/RGBA/灰度、非隔行。其它形态（16-bit、调色板、隔行）
// 一律明确返回 E_NOTIMPL —— **不猜、不近似**（猜出来的像素比报错更糟）。

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zlib.h>
#include "png_decode.h"

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

static uint32_t be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}

static int paeth(int a, int b, int c)
{
    int p = a + b - c;
    int pa = abs(p - a), pb = abs(p - b), pc = abs(p - c);
    if (pa <= pb && pa <= pc) return a;
    if (pb <= pc) return b;
    return c;
}

int32_t ldp_png_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t error_cap)
{
    if (!data || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    static const uint8_t sig[8] = { 0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a };
    if (len < 8 || memcmp(data, sig, 8) != 0) {
        snprintf(error, error_cap, "不是 PNG（签名不匹配）");
        return E_INVALIDARG_;
    }
    size_t off = 8;
    uint32_t width = 0, height = 0;
    int bit_depth = 0, color_type = 0, interlace = 0;
    uint8_t *idat = NULL;
    size_t idat_len = 0, idat_cap = 0;
    while (off + 12 <= len) {
        uint32_t chunk_len = be32(data + off);
        const char *type = (const char *)(data + off + 4);
        if (off + 12 + (size_t)chunk_len > len) {
            snprintf(error, error_cap, "chunk 越界（off=%zu len=%u）", off, chunk_len);
            free(idat);
            return E_INVALIDARG_;
        }
        const uint8_t *body = data + off + 8;
        if (memcmp(type, "IHDR", 4) == 0 && chunk_len >= 13) {
            width = be32(body);
            height = be32(body + 4);
            bit_depth = body[8];
            color_type = body[9];
            interlace = body[12];
        } else if (memcmp(type, "IDAT", 4) == 0) {
            if (idat_len + chunk_len > idat_cap) {
                size_t cap = idat_cap ? idat_cap : 65536;
                while (cap < idat_len + chunk_len) cap *= 2;
                uint8_t *p = (uint8_t *)realloc(idat, cap);
                if (!p) { snprintf(error, error_cap, "内存不足"); free(idat); return E_NOTIMPL_; }
                idat = p;
                idat_cap = cap;
            }
            memcpy(idat + idat_len, body, chunk_len);
            idat_len += chunk_len;
        } else if (memcmp(type, "IEND", 4) == 0) {
            break;
        }
        off += 12 + (size_t)chunk_len;
    }
    if (!width || !height || !idat) {
        snprintf(error, error_cap, "缺 IHDR/IDAT");
        free(idat);
        return E_INVALIDARG_;
    }
    if (bit_depth != 8 || interlace != 0) {
        snprintf(error, error_cap, "只支持 8-bit 非隔行（bit_depth=%d interlace=%d）", bit_depth, interlace);
        free(idat);
        return E_NOTIMPL_;
    }
    int channels;
    switch (color_type) {
        case 0: channels = 1; break;   // 灰度
        case 2: channels = 3; break;   // RGB
        case 6: channels = 4; break;   // RGBA
        default:
            snprintf(error, error_cap, "不支持的颜色类型 %d（调色板等未实现）", color_type);
            free(idat);
            return E_NOTIMPL_;
    }

    const size_t stride = (size_t)width * (size_t)channels;
    const size_t raw_len = (stride + 1) * (size_t)height;
    uint8_t *raw = (uint8_t *)malloc(raw_len);
    if (!raw) { snprintf(error, error_cap, "内存不足（raw）"); free(idat); return E_NOTIMPL_; }
    uLongf dest_len = (uLongf)raw_len;
    int zr = uncompress(raw, &dest_len, idat, (uLong)idat_len);
    free(idat);
    if (zr != Z_OK || dest_len < raw_len) {
        snprintf(error, error_cap, "zlib 解压失败（zr=%d，得到 %lu / 期望 %zu）", zr, (unsigned long)dest_len, raw_len);
        free(raw);
        return E_NOTIMPL_;
    }

    // iCCP：`name\0` + 1 字节压缩方式 + zlib 压缩的 ICC profile
    uint8_t *icc = NULL;
    uint32_t icc_len = 0;
    {
        size_t o2 = 8;
        while (o2 + 12 <= len) {
            uint32_t cl = be32(data + o2);
            const char *ty = (const char *)(data + o2 + 4);
            if (memcmp(ty, "iCCP", 4) == 0 && cl > 2) {
                const uint8_t *body = data + o2 + 8;
                size_t name_len = 0;
                while (name_len < cl && body[name_len] != 0) name_len++;
                if (name_len + 2 <= cl && body[name_len + 1] == 0) {
                    const uint8_t *z = body + name_len + 2;
                    size_t zlen = cl - name_len - 2;
                    // 解压到一个宽松的缓冲（ICC profile 通常 < 1 MB）
                    size_t cap2 = 1 << 20;
                    uint8_t *out2 = (uint8_t *)malloc(cap2);
                    if (out2) {
                        uLongf dl = (uLongf)cap2;
                        if (uncompress(out2, &dl, z, (uLong)zlen) == Z_OK && dl > 128) {
                            icc = out2;
                            icc_len = (uint32_t)dl;
                        } else {
                            free(out2);   // 解不出来就当没有（不假装成功）
                        }
                    }
                }
            }
            o2 += 12 + (size_t)cl;
        }
    }

    // 第一步：反滤波（逐字节，按 channels 处理）→ 得到未滤波的扫描行
    uint8_t *plain = (uint8_t *)malloc(stride * (size_t)height);
    if (!plain) { snprintf(error, error_cap, "内存不足（plain）"); free(raw); return E_NOTIMPL_; }
    for (uint32_t y = 0; y < height; ++y) {
        const uint8_t *src = raw + (size_t)y * (stride + 1);
        uint8_t filter = src[0];
        src += 1;
        uint8_t *cur = plain + (size_t)y * stride;
        const uint8_t *prev = (y > 0) ? (plain + (size_t)(y - 1) * stride) : NULL;
        for (size_t x = 0; x < stride; ++x) {
            int left = (x >= (size_t)channels) ? cur[x - channels] : 0;
            int up = prev ? prev[x] : 0;
            int upleft = (prev && x >= (size_t)channels) ? prev[x - channels] : 0;
            int v;
            switch (filter) {
                case 0: v = src[x]; break;
                case 1: v = src[x] + left; break;
                case 2: v = src[x] + up; break;
                case 3: v = src[x] + ((left + up) >> 1); break;
                case 4: v = src[x] + paeth(left, up, upleft); break;
                default:
                    snprintf(error, error_cap, "未知滤波类型 %d（第 %u 行）", filter, y);
                    free(raw); free(plain);
                    return E_NOTIMPL_;
            }
            cur[x] = (uint8_t)(v & 0xFF);
        }
    }
    free(raw);

    // 第二步：转 BGRA8888（WIC 的 GUID_WICPixelFormat32bppBGRA）
    uint8_t *bgra = (uint8_t *)malloc((size_t)width * (size_t)height * 4);
    if (!bgra) { snprintf(error, error_cap, "内存不足（bgra）"); free(plain); return E_NOTIMPL_; }
    for (size_t i = 0; i < (size_t)width * (size_t)height; ++i) {
        const uint8_t *p = plain + i * (size_t)channels;
        uint8_t *d = bgra + i * 4;
        if (channels == 1) { d[0] = d[1] = d[2] = p[0]; d[3] = 255; }
        else if (channels == 3) { d[0] = p[2]; d[1] = p[1]; d[2] = p[0]; d[3] = 255; }
        else { d[0] = p[2]; d[1] = p[1]; d[2] = p[0]; d[3] = p[3]; }
    }
    free(plain);
    out->width = width;
    out->height = height;
    out->has_alpha = (channels == 4);
    out->bgra = bgra;
    out->icc = icc;
    out->icc_len = icc_len;
    return 0;
}

void ldp_png_free(ldp_png_image *img)
{
    if (!img) return;
    if (img->bgra) { free(img->bgra); img->bgra = NULL; }
    if (img->icc) { free(img->icc); img->icc = NULL; }
    img->icc_len = 0;
}
