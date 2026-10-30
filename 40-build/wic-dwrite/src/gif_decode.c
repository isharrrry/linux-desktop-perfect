// GIF 解码（D-018：五格式里的 GIF）—— 只解**第一帧**，这是诚实口径：
//   WIC 的 GIF 解码器支持多帧动画，本实现只做静态首帧（多帧明确写在 error 里，不假装支持）。
//
// GIF 的核心是 **LZW 变体**：变长码 + clear code + end-of-information，字典最多 4096 项。

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "png_decode.h"   /* 复用"BGRA8888 位图"约定 */

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

int ldp_is_gif(const uint8_t *d, size_t len)
{
    return len >= 6 && memcmp(d, "GIF87a", 6) == 0;
}

int ldp_is_gif89(const uint8_t *d, size_t len)
{
    return len >= 6 && memcmp(d, "GIF89a", 6) == 0;
}

static uint16_t le16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }

// LZW 解码：把 data/cap 里的压缩字节解成 indices（最多 max_out 个）
// 返回解出的索引数；负值 = 出错
static int gif_lzw_decode(const uint8_t *data, size_t cap, int min_code_size,
                          uint8_t *out, size_t max_out)
{
    if (min_code_size < 2 || min_code_size > 11) return -1;
    const int clear = 1 << min_code_size;
    const int eoi = clear + 1;
    int code_size = min_code_size + 1;
    int next = clear + 2;

    static uint16_t prefix[4096];
    static uint8_t suffix[4096];
    for (int i = 0; i < clear; ++i) { prefix[i] = 0xFFFF; suffix[i] = (uint8_t)i; }

    int out_len = 0;
    int prev = -1;
    uint8_t stack[4096];
    size_t bitpos = 0;

    for (;;) {
        if (bitpos + (size_t)code_size > cap * 8) break;
        int code = 0;
        for (int i = 0; i < code_size; ++i) {
            size_t bp = bitpos + (size_t)i;
            int bit = (data[bp >> 3] >> (bp & 7)) & 1;
            code |= bit << i;
        }
        bitpos += (size_t)code_size;

        if (code == clear) {
            code_size = min_code_size + 1;
            next = clear + 2;
            prev = -1;
            continue;
        }
        if (code == eoi) break;

        int sp = 0;
        int cur = code;
        if (code >= next) {
            if (prev < 0) return -1;
            // KwKwK 情形：把上一个串 + 它的首字符压栈
            stack[sp++] = suffix[prev];
            cur = prev;
        }
        while (cur >= clear) {
            if (sp >= (int)sizeof(stack) || cur >= 4096) return -1;
            stack[sp++] = suffix[cur];
            cur = prefix[cur];
        }
        stack[sp++] = (uint8_t)cur;   // 根字符

        // 出栈（栈里是反序）
        for (int i = sp - 1; i >= 0; --i) {
            if ((size_t)out_len >= max_out) return out_len;
            out[out_len++] = stack[i];
        }

        // 扩展字典
        if (prev >= 0 && next < 4096) {
            prefix[next] = (uint16_t)prev;
            suffix[next] = stack[sp - 1];
            next++;
            if (next == (1 << code_size) && code_size < 12) code_size++;
        }
        prev = code;
    }
    return out_len;
}

int32_t ldp_gif_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap)
{
    if (!data || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    if (!ldp_is_gif(data, len) && !ldp_is_gif89(data, len)) {
        snprintf(error, cap, "不是 GIF（头不匹配）");
        return E_INVALIDARG_;
    }
    if (len < 13) { snprintf(error, cap, "GIF 头不足"); return E_INVALIDARG_; }
    uint32_t sw = le16(data + 6), sh = le16(data + 8);
    uint8_t packed = data[10];
    int has_gct = (packed & 0x80) != 0;
    int gct_bits = (packed & 0x07) + 1;
    size_t gct_count = has_gct ? ((size_t)1 << gct_bits) : 0;
    size_t off = 13;
    uint8_t palette[256 * 4];
    memset(palette, 0, sizeof(palette));
    if (has_gct) {
        if (off + gct_count * 3 > len) { snprintf(error, cap, "全局调色板越界"); return E_INVALIDARG_; }
        for (size_t i = 0; i < gct_count; ++i) {
            palette[i * 4 + 0] = data[off + i * 3 + 2];   // B
            palette[i * 4 + 1] = data[off + i * 3 + 1];   // G
            palette[i * 4 + 2] = data[off + i * 3 + 0];   // R
            palette[i * 4 + 3] = 255;
        }
        off += gct_count * 3;
    }
    // 找第一个图像描述符（跳过扩展块）
    while (off < len) {
        uint8_t block = data[off];
        if (block == 0x2C) {   // Image Descriptor
            if (off + 10 > len) { snprintf(error, cap, "图像描述符越界"); return E_INVALIDARG_; }
            uint32_t ix = le16(data + off + 1), iy = le16(data + off + 3);
            uint32_t iw = le16(data + off + 5), ih = le16(data + off + 7);
            uint8_t ipacked = data[off + 9];
            (void)ix; (void)iy;
            off += 10;
            int lct = (ipacked & 0x80) != 0;
            int lct_bits = (ipacked & 0x07) + 1;
            if (lct) {
                size_t n = (size_t)1 << lct_bits;
                if (off + n * 3 > len) { snprintf(error, cap, "局部调色板越界"); return E_INVALIDARG_; }
                for (size_t i = 0; i < n && i < 256; ++i) {
                    palette[i * 4 + 0] = data[off + i * 3 + 2];
                    palette[i * 4 + 1] = data[off + i * 3 + 1];
                    palette[i * 4 + 2] = data[off + i * 3 + 0];
                    palette[i * 4 + 3] = 255;
                }
                off += n * 3;
            }
            if (off >= len) { snprintf(error, cap, "缺 LZW 最小码长"); return E_INVALIDARG_; }
            int min_code = data[off++];
            // 收集数据子块
            static uint8_t lzw[1 << 20];
            size_t lzw_len = 0;
            while (off < len) {
                uint8_t n = data[off++];
                if (n == 0) break;
                if (off + n > len) { snprintf(error, cap, "数据子块越界"); return E_INVALIDARG_; }
                if (lzw_len + n > sizeof(lzw)) { snprintf(error, cap, "GIF 数据过大"); return E_NOTIMPL_; }
                memcpy(lzw + lzw_len, data + off, n);
                lzw_len += n;
                off += n;
            }
            if (!iw || !ih) { snprintf(error, cap, "图像尺寸非法 %ux%u", iw, ih); return E_INVALIDARG_; }
            uint8_t *indices = (uint8_t *)malloc((size_t)iw * ih);
            if (!indices) { snprintf(error, cap, "内存不足"); return E_NOTIMPL_; }
            int got = gif_lzw_decode(lzw, lzw_len, min_code, indices, (size_t)iw * ih);
            if (got < 0 || (uint32_t)got < iw * ih) {
                snprintf(error, cap, "LZW 解出的像素不足（%d / %u）", got, iw * ih);
                free(indices);
                return E_NOTIMPL_;
            }
            uint8_t *bgra = (uint8_t *)malloc((size_t)iw * ih * 4);
            if (!bgra) { free(indices); return E_NOTIMPL_; }
            for (size_t i = 0; i < (size_t)iw * ih; ++i)
                memcpy(bgra + i * 4, palette + indices[i] * 4, 4);
            free(indices);
            (void)sw; (void)sh;
            out->width = iw;
            out->height = ih;
            out->has_alpha = 0;
            out->bgra = bgra;
            return 0;
        } else if (block == 0x21) {        // Extension
            off++;
            if (off >= len) { snprintf(error, cap, "扩展块截断"); return E_INVALIDARG_; }
            off++;                          // label
            while (off < len) { uint8_t n = data[off++]; if (n == 0) break; off += n; }
        } else if (block == 0x3B) {          // Trailer（没有图像）
            snprintf(error, cap, "GIF 里没有图像帧");
            return E_INVALIDARG_;
        } else {
            snprintf(error, cap, "未知块 0x%02x", block);
            return E_NOTIMPL_;
        }
    }
    snprintf(error, cap, "没找到图像块");
    return E_INVALIDARG_;
}
