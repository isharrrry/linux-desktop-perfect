// JPEG 基线解码（D-018：五格式的最后一格）—— 自写，不借 libjpeg。
//
// 【为什么自写】libjpeg 的公开 API 要求调用方精确知道 `jpeg_decompress_struct` 的大小
//   （`jpeg_CreateDecompress` 会校验相等），而本机只有运行库、没有 `jpeglib.h`；
//   实测扫 structsize 560..900 全失败、400 会栈溢出（见已知坑 D-018 第十三步）。
//   ⇒ 只能自写基线（Baseline sequential）解码。
//
// 【范围】Baseline 顺序、8 位、Huffman、Y/Cb/Cr 或灰度（1 或 3 分量）、无渐进/算术。
//   其它（渐进 SOF2、算术、12-bit、CMYK）**明确返回 E_NOTIMPL** —— 不猜。

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "png_decode.h"

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

int ldp_is_jpeg(const uint8_t *d, size_t len)
{
    return len >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
}

// ── 霍夫曼表：用 (码长, 码字) → 符号 的查表法 ──
typedef struct {
    int      mincode[17];   // 每个码长能解出的最小码字
    int      maxcode[17];   // 最大码字（-1 表示该码长没有码字）
    int      valptr[17];    // 该码长第一个符号在 vals 里的下标
    uint8_t  vals[256];
    int      count;
} jpeg_huff;

static void jpeg_huff_build(jpeg_huff *h, const uint8_t *bits, const uint8_t *vals, int nvals)
{
    // 标准做法：按码长累进计算每个长度的 mincode/maxcode/valptr，符号表直接拷 vals。
    int code = 0, k = 0;
    for (int len = 1; len <= 16; ++len) {
        h->valptr[len] = k;
        h->mincode[len] = code;
        if (bits[len - 1]) {
            h->maxcode[len] = code + bits[len - 1] - 1;
            code += bits[len - 1];
        } else {
            h->maxcode[len] = -1;
        }
        // ⚠️ 这里必须累加 k（该码长贡献了 bits[len-1] 个符号）。
        //   修前漏了这一步 ⇒ valptr 全是 0 ⇒ 所有码长都从 vals[0] 起算 ⇒
        //   AC 表解出的符号张冠李戴（全黑图的 EOB 被解成非零系数，一路读到 k>=64）。
        k += bits[len - 1];
        code <<= 1;
    }
    h->maxcode[16] = 0x7FFFFFFF;
    for (int i = 0; i < nvals && i < 256; ++i) h->vals[i] = vals[i];
    h->count = nvals < 256 ? nvals : 256;
}

// ── 解码器状态 ──
typedef struct {
    const uint8_t *data;
    size_t         size, pos;
    uint32_t       bitbuf;
    int            bitcnt;

    int            width, height;
    int            ncomp;
    int            hmax, vmax;
    int            progressive;
    int            restart_interval;

    // 每个分量
    struct {
        int id, h, v, tq;
        jpeg_huff *dc, *ac, *dc_prev_table;
        uint16_t q[64];
        int dc_pred;
        int16_t *coef;      // 每个 8x8 块的量化后系数（本实现只保留当前块）
        uint8_t *plane;     // 该分量的全图采样（上采样前）
        int pw, ph;         // 该分量的采样尺寸
    } comp[4];

    jpeg_huff dc_tables[4], ac_tables[4];
    int has_dc[4], has_ac[4];
    uint16_t q_tables[4][64];
    int has_q[4];
} jpeg_ctx;

static int jpeg_getc(jpeg_ctx *c)
{
    if (c->pos >= c->size) return -1;
    return c->data[c->pos++];
}

static int jpeg_getbit(jpeg_ctx *c)
{
    if (c->bitcnt == 0) {
        int b = jpeg_getc(c);
        if (b < 0) return -1;
        if (b == 0xFF) {
            int b2 = jpeg_getc(c);
            if (b2 != 0x00) { c->pos--; return -1; }   // 撞到标记
        }
        c->bitbuf = (uint32_t)b;
        c->bitcnt = 8;
    }
    c->bitcnt--;
    return (int)((c->bitbuf >> c->bitcnt) & 1);
}

static int jpeg_getbits(jpeg_ctx *c, int n)
{
    int v = 0;
    for (int i = 0; i < n; ++i) {
        int b = jpeg_getbit(c);
        if (b < 0) return 0;
        v = (v << 1) | b;
    }
    return v;
}

static int jpeg_decode_huff(jpeg_ctx *c, jpeg_huff *h)
{
    int code = 0;
    for (int len = 1; len <= 16; ++len) {
        int b = jpeg_getbit(c);
        if (b < 0) return -1;
        code = (code << 1) | b;
        if (h->maxcode[len] >= 0 && code <= h->maxcode[len]) {
            int idx = h->valptr[len] + (code - h->mincode[len]);
            if (idx < 0 || idx >= h->count) return -1;
            return h->vals[idx];
        }
    }
    return -1;
}

static int jpeg_extend(int v, int n)
{
    if (n == 0) return 0;
    return (v < (1 << (n - 1))) ? v - ((1 << n) - 1) : v;
}

// IDCT：用 double 的朴素实现（性能不重要，正确优先）
static void jpeg_idct(const int32_t *coef, const uint16_t *q, uint8_t *out, int stride)
{
    static const double C[8][8] = {
        {0.35355339059327373,0.35355339059327373,0.35355339059327373,0.35355339059327373,0.35355339059327373,0.35355339059327373,0.35355339059327373,0.35355339059327373},
        {0.49039264020161522,0.41573480615127262,0.27778511650980114,0.09754516100806417,-0.09754516100806417,-0.27778511650980114,-0.41573480615127262,-0.49039264020161522},
        {0.46193976625564337,0.19134171618254492,-0.19134171618254492,-0.46193976625564337,-0.46193976625564337,-0.19134171618254492,0.19134171618254492,0.46193976625564337},
        {0.41573480615127262,-0.09754516100806417,-0.49039264020161522,-0.27778511650980114,0.27778511650980114,0.49039264020161522,0.09754516100806417,-0.41573480615127262},
        {0.35355339059327373,-0.35355339059327373,-0.35355339059327373,0.35355339059327373,0.35355339059327373,-0.35355339059327373,-0.35355339059327373,0.35355339059327373},
        {0.27778511650980114,-0.49039264020161522,0.09754516100806417,0.41573480615127262,-0.41573480615127262,-0.09754516100806417,0.49039264020161522,-0.27778511650980114},
        {0.19134171618254492,-0.46193976625564337,0.46193976625564337,-0.19134171618254492,-0.19134171618254492,0.46193976625564337,-0.46193976625564337,0.19134171618254492},
        {0.09754516100806417,-0.27778511650980114,0.41573480615127262,-0.49039264020161522,0.49039264020161522,-0.41573480615127262,0.27778511650980114,-0.09754516100806417},
    };
    double tmp[64];
    for (int i = 0; i < 64; ++i) tmp[i] = (double)(coef[i] * (int32_t)q[i]);
    double row[64];
    for (int y = 0; y < 8; ++y)
        for (int x = 0; x < 8; ++x) {
            double s = 0.0;
            for (int u = 0; u < 8; ++u) s += C[x][u] * tmp[y * 8 + u];
            row[y * 8 + x] = s;
        }
    for (int x = 0; x < 8; ++x)
        for (int y = 0; y < 8; ++y) {
            double s = 0.0;
            for (int vv = 0; vv < 8; ++vv) s += C[y][vv] * row[vv * 8 + x];
            int val = (int)(s + 128.5);
            if (val < 0) val = 0;
            if (val > 255) val = 255;
            out[y * stride + x] = (uint8_t)val;
        }
}

// ── 解析 + 熵解码 ───────────────────────────────────────────────────────────────
static int jpeg_read_marker(jpeg_ctx *c)
{
    int b = jpeg_getc(c);
    while (b >= 0 && b != 0xFF) b = jpeg_getc(c);      // 找 0xFF
    while (b == 0xFF) b = jpeg_getc(c);                // 跳过填充的 0xFF
    return b;
}

static int jpeg_parse_dqt(jpeg_ctx *c, int len)
{
    int end = (int)c->pos + len - 2;
    while ((int)c->pos < end) {
        int pq = jpeg_getc(c);
        if (pq < 0) return -1;
        int prec = pq >> 4, tq = pq & 0x0F;
        if (tq > 3) return -1;
        for (int i = 0; i < 64; ++i) {
            int v = prec ? ((jpeg_getc(c) << 8) | jpeg_getc(c)) : jpeg_getc(c);
            if (v < 0) return -1;
            c->q_tables[tq][i] = (uint16_t)v;
        }
        c->has_q[tq] = 1;
    }
    return 0;
}

static int jpeg_parse_dht(jpeg_ctx *c, int len)
{
    int end = (int)c->pos + len - 2;
    while ((int)c->pos < end) {
        int tc_th = jpeg_getc(c);
        if (tc_th < 0) return -1;
        int tc = tc_th >> 4, th = tc_th & 0x0F;
        if (th > 3) return -1;
        uint8_t bits[16];
        int total = 0;
        for (int i = 0; i < 16; ++i) { bits[i] = (uint8_t)jpeg_getc(c); total += bits[i]; }
        if (total > 256) return -1;
        uint8_t vals[256];
        for (int i = 0; i < total; ++i) vals[i] = (uint8_t)jpeg_getc(c);
        if (tc == 0) { jpeg_huff_build(&c->dc_tables[th], bits, vals, total); c->has_dc[th] = 1; }
        else         { jpeg_huff_build(&c->ac_tables[th], bits, vals, total); c->has_ac[th] = 1; }
    }
    return 0;
}

static int jpeg_parse_sof(jpeg_ctx *c, int len, int progressive)
{
    (void)len;
    c->progressive = progressive;
    int prec = jpeg_getc(c);
    c->height = (jpeg_getc(c) << 8) | jpeg_getc(c);
    c->width = (jpeg_getc(c) << 8) | jpeg_getc(c);
    c->ncomp = jpeg_getc(c);
    if (prec != 8 || c->ncomp < 1 || c->ncomp > 4 || !c->width || !c->height) return -1;
    c->hmax = c->vmax = 1;
    for (int i = 0; i < c->ncomp; ++i) {
        c->comp[i].id = jpeg_getc(c);
        int hv = jpeg_getc(c);
        c->comp[i].h = hv >> 4;
        c->comp[i].v = hv & 0x0F;
        c->comp[i].tq = jpeg_getc(c);
        if (c->comp[i].h > c->hmax) c->hmax = c->comp[i].h;
        if (c->comp[i].v > c->vmax) c->vmax = c->vmax;
        if (c->comp[i].v > c->vmax) c->vmax = c->comp[i].v;
        c->comp[i].pw = (c->width * c->comp[i].h + c->hmax - 1) / c->hmax;
        c->comp[i].ph = (c->height * c->comp[i].v + c->vmax - 1) / c->vmax;
        c->comp[i].dc_pred = 0;
    }
    return 0;
}

static int jpeg_decode_block(jpeg_ctx *c, int comp_idx, int32_t *coef)
{
    memset(coef, 0, 64 * sizeof(int32_t));
    if (!c->has_dc[0] && !c->has_dc[1] && !c->has_dc[2] && !c->has_dc[3]) return -1;
    jpeg_huff *dc = c->comp[comp_idx].dc;
    jpeg_huff *ac = c->comp[comp_idx].ac;
    if (!dc || !ac) return -1;
    if (getenv("LDP_JPEG_TRACE")) {
        fprintf(stderr, "[JPEG] block comp=%d pos=%zu bitcnt=%d dcmax[1..3]=%d,%d,%d dcmax[4..6]=%d,%d,%d acmax[1]=%d count_dc=%d count_ac=%d\n",
                comp_idx, c->pos, c->bitcnt,
                dc->maxcode[1], dc->maxcode[2], dc->maxcode[3],
                dc->maxcode[4], dc->maxcode[5], dc->maxcode[6],
                ac->maxcode[1], dc->count, ac->count);
        fflush(stderr);
    }
    int t = jpeg_decode_huff(c, dc);
    if (getenv("LDP_JPEG_TRACE")) { fprintf(stderr, "[JPEG]   DC t=%d\n", t); fflush(stderr); }
    if (t < 0) { if (getenv("LDP_JPEG_TRACE")) fprintf(stderr, "[JPEG]   FAIL at DC\n"); return -1; }
    int diff = jpeg_extend(jpeg_getbits(c, t), t);
    c->comp[comp_idx].dc_pred += diff;
    coef[0] = c->comp[comp_idx].dc_pred;
    if (getenv("LDP_JPEG_TRACE")) { fprintf(stderr, "[JPEG]   entering AC loop (ac=%p)\n", (void *)ac); fflush(stderr); }
    for (int k = 1; k < 64; ) {
        if (getenv("LDP_JPEG_TRACE")) {
            fprintf(stderr, "[JPEG]   AC k=%d bits=%d: ", k, c->bitcnt);
            for (int L = 1; L <= 8; ++L) fprintf(stderr, "len%d[%d..%d]@%d ", L, ac->mincode[L], ac->maxcode[L], ac->valptr[L]);
            fprintf(stderr, "\n"); fflush(stderr);
        }
        int rs = jpeg_decode_huff(c, ac);
        if (rs < 0) return -1;
        int r = rs >> 4, s = rs & 0x0F;
        if (s == 0) {
            if (r == 15) { k += 16; continue; }   // ZRL
            break;                                 // EOB
        }
        k += r;
        if (k >= 64) return -1;
        coef[k] = jpeg_extend(jpeg_getbits(c, s), s);
        k++;
    }
    return 0;
}

// ── MCU 循环 + 上采样 + 色彩转换 ────────────────────────────────────────────────
// 【当前实现范围】Baseline、8 位、1 或 3 分量（灰度 / YCbCr）。上采样用**最近邻**
//   （不做 fancy upsampling —— 那是"更好看"，不是"能解码"；先保证像素正确再说）。
static int jpeg_decode_scan(jpeg_ctx *c, int nscans, char *error, size_t cap)
{
    const int mcu_w = 8 * c->hmax;          // 一个 MCU 覆盖的像素宽度
    const int mcu_h = 8 * c->vmax;
    const int mcus_x = (c->width + mcu_w - 1) / mcu_w;
    const int mcus_y = (c->height + mcu_h - 1) / mcu_h;

    for (int i = 0; i < nscans; ++i) {
        int pw = c->comp[i].pw, ph = c->comp[i].ph;
        c->comp[i].plane = (uint8_t *)calloc((size_t)pw * ph, 1);
        if (!c->comp[i].plane) { snprintf(error, cap, "内存不足（plane）"); return -1; }
    }

    int32_t coef[64];
    int mcu_idx = 0;
    for (int my = 0; my < mcus_y; ++my) {
        for (int mx = 0; mx < mcus_x; ++mx) {
            // restart interval：到点就重置 DC 预测并跳过重启标记
            if (c->restart_interval && mcu_idx && mcu_idx % c->restart_interval == 0) {
                c->bitcnt = 0;
                int m = jpeg_read_marker(c);
                if (m < 0 || m < 0xD0 || m > 0xD7) {
                    snprintf(error, cap, "重启标记异常（0x%02x）", m);
                    return -1;
                }
                for (int i = 0; i < c->ncomp; ++i) c->comp[i].dc_pred = 0;
            }
            mcu_idx++;

            for (int ci = 0; ci < c->ncomp; ++ci) {
                int bh = c->comp[ci].h, bv = c->comp[ci].v;
                for (int by = 0; by < bv; ++by) {
                    for (int bx = 0; bx < bh; ++bx) {
                        if (jpeg_decode_block(c, ci, coef) < 0) {
                            snprintf(error, cap, "熵解码失败（MCU %d,%d 分量 %d）", mx, my, ci);
                            return -1;
                        }
                        uint8_t blk[64];
                        jpeg_idct(coef, c->comp[ci].q, blk, 8);
                        int px0 = (mx * bh + bx) * 8;
                        int py0 = (my * bv + by) * 8;
                        for (int y = 0; y < 8; ++y) {
                            int py = py0 + y;
                            if (py >= c->comp[ci].ph) break;
                            for (int x = 0; x < 8; ++x) {
                                int px = px0 + x;
                                if (px >= c->comp[ci].pw) break;
                                c->comp[ci].plane[(size_t)py * c->comp[ci].pw + px] = blk[y * 8 + x];
                            }
                        }
                    }
                }
            }
        }
    }
    return 0;
}

int32_t ldp_jpeg_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap)
{
    if (!data || !out) return E_INVALIDARG_;
    memset(out, 0, sizeof(*out));
    if (!ldp_is_jpeg(data, len)) { snprintf(error, cap, "不是 JPEG（签名不匹配）"); return E_INVALIDARG_; }

    jpeg_ctx *c = (jpeg_ctx *)calloc(1, sizeof(jpeg_ctx));
    if (!c) return E_NOTIMPL_;
    c->data = data; c->size = len; c->pos = 2;

    int ok = 0, nscans = 0;
    for (;;) {
        int m = jpeg_read_marker(c);
        if (m < 0) break;
        if (m == 0xD9) break;
        if (m == 0xD8) continue;
        int lo = jpeg_getc(c), hi = jpeg_getc(c);
        if (lo < 0 || hi < 0) break;
        int seglen = (lo << 8) | hi;
        if (seglen < 2) break;
        if (m == 0xDB) { if (jpeg_parse_dqt(c, seglen) < 0) break; }
        else if (m == 0xC4) { if (jpeg_parse_dht(c, seglen) < 0) break; }
        else if (m == 0xC0) { if (jpeg_parse_sof(c, seglen, 0) < 0) break; }
        else if (m == 0xC2) { snprintf(error, cap, "渐进式 JPEG（SOF2）未实现"); free(c); return E_NOTIMPL_; }
        else if (m == 0xDD) { c->restart_interval = (jpeg_getc(c) << 8) | jpeg_getc(c); }
        else if (m == 0xDA) {
            int ns = jpeg_getc(c);
            if (ns < 1 || ns > c->ncomp) break;
            for (int i = 0; i < ns; ++i) {
                int cs = jpeg_getc(c);
                int tdta = jpeg_getc(c);
                int ci = -1;
                for (int k = 0; k < c->ncomp; ++k) if (c->comp[k].id == cs) ci = k;
                if (ci < 0) break;
                int td = tdta >> 4, ta = tdta & 0x0F;
                c->comp[ci].dc = c->has_dc[td] ? &c->dc_tables[td] : NULL;
                c->comp[ci].ac = c->has_ac[ta] ? &c->ac_tables[ta] : NULL;
                if (getenv("LDP_JPEG_TRACE")) {
                    fprintf(stderr, "[JPEG] SOS comp=%d td=%d ta=%d has_dc=%d has_ac=%d -> dc=%p ac=%p\n",
                            ci, td, ta, c->has_dc[td], c->has_ac[ta],
                            (void *)c->comp[ci].dc, (void *)c->comp[ci].ac);
                    fflush(stderr);
                }
                for (int q = 0; q < 64; ++q) c->comp[ci].q[q] = c->q_tables[c->comp[ci].tq][q];
            }
            // 跳过 SOS 尾部的 3 个字节（Ss, Se, Ah/Al）
            jpeg_getc(c); jpeg_getc(c); jpeg_getc(c);
            c->bitcnt = 0;
            nscans = ns;
            if (jpeg_decode_scan(c, nscans, error, cap) < 0) { ok = -1; break; }
            ok = 1;
            break;
        } else {
            c->pos += (size_t)(seglen - 2);
        }
    }

    if (ok != 1 || nscans < 1) {
        if (ok == 0) snprintf(error, cap, "JPEG 解析失败（未遇到可用的 SOS）");
        for (int i = 0; i < c->ncomp; ++i) free(c->comp[i].plane);
        free(c);
        return E_INVALIDARG_;
    }

    // 上采样（最近邻）+ 色彩转换 → BGRA
    uint8_t *bgra = (uint8_t *)malloc((size_t)c->width * c->height * 4);
    if (!bgra) {
        for (int i = 0; i < c->ncomp; ++i) free(c->comp[i].plane);
        free(c);
        return E_NOTIMPL_;
    }
    for (int y = 0; y < c->height; ++y) {
        for (int x = 0; x < c->width; ++x) {
            uint8_t *dst = bgra + ((size_t)y * c->width + x) * 4;
            if (c->ncomp == 1) {
                int sx = x * c->comp[0].pw / c->width;
                int sy = y * c->comp[0].ph / c->height;
                uint8_t g = c->comp[0].plane[(size_t)sy * c->comp[0].pw + sx];
                dst[0] = dst[1] = dst[2] = g;
                dst[3] = 255;
            } else {
                int sx0 = x * c->comp[0].pw / c->width, sy0 = y * c->comp[0].ph / c->height;
                int sx1 = x * c->comp[1].pw / c->width, sy1 = y * c->comp[1].ph / c->height;
                int sx2 = x * c->comp[2].pw / c->width, sy2 = y * c->comp[2].ph / c->height;
                int Y  = c->comp[0].plane[(size_t)sy0 * c->comp[0].pw + sx0];
                int Cb = c->comp[1].plane[(size_t)sy1 * c->comp[1].pw + sx1] - 128;
                int Cr = c->comp[2].plane[(size_t)sy2 * c->comp[2].pw + sx2] - 128;
                int r = Y + (int)(1.402 * Cr);
                int g = Y - (int)(0.344136 * Cb + 0.714136 * Cr);
                int b = Y + (int)(1.772 * Cb);
                if (r < 0) r = 0; if (r > 255) r = 255;
                if (g < 0) g = 0; if (g > 255) g = 255;
                if (b < 0) b = 0; if (b > 255) b = 255;
                dst[0] = (uint8_t)b; dst[1] = (uint8_t)g; dst[2] = (uint8_t)r; dst[3] = 255;
            }
        }
    }
    for (int i = 0; i < c->ncomp; ++i) free(c->comp[i].plane);

    out->width = (uint32_t)c->width;
    out->height = (uint32_t)c->height;
    out->has_alpha = 0;
    out->bgra = bgra;
    free(c);
    return 0;
}
