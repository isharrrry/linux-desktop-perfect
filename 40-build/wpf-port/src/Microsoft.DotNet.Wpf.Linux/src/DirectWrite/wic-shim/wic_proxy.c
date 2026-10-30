/* T2 · WIC → Skia shim：`*_Proxy` 的 Linux 实现（读路径）
 * ============================================================================
 * 【它是什么】
 *   PresentationCore 通过 109 条 `[DllImport(DllImport.WindowsCodecs, EntryPoint="IWIC…_Proxy")]`
 *   调 WIC。Linux 上没有 WindowsCodecs.dll ⇒ 今天任何一条都是
 *   `DllNotFoundException`。本文件实现其中**读路径**所需的那批导出，
 *   解码用 Skia 的 C API（`libSkiaSharp.so`，实测导出 856 个 `sk_*` 符号）。
 *
 * 【为什么可以这样接（seam 已实测）】
 *   `BitmapDecoder` 对 `file://`（同步、可 seek 的 FileStream）走
 *   `CreateDecoderFromFileHandle`，而 `FileStream.SafeFileHandle` 在 Linux 上是
 *   **真 OS fd**（实测：句柄 32 → `readlink /proc/self/fd/32` 指向那个 PNG，
 *   `pread` 得到 89504E470D0A1A0A）⇒ 本 shim 直接 `pread` 即可，
 *   与 `libwpfwin32.so`、MilCore **零耦合**。
 *
 * 【实测钉死的 ABI 事实（主控 AbiProbe + 本目录 probe_decode.c 双向复核）】
 *   1. `sk_imageinfo_t` 真实布局 = { colorspace, width, height, colorType, alphaType }，
 *      **24 字节**；公开头文件里的 {colorType,alphaType,colorspace,w,h} 顺序在
 *      SkiaSharp 2.88.9 的 .so 上是**错的**（会读出 w=6,h=1 这种垃圾）。
 *   2. `sk_codec_get_info` 的返回值**没有 HRESULT 语义**（实测随机值），别拿它判断成败。
 *   3. `sk_codec_get_pixels(codec, &info, pixels, rowBytes, NULL)` —— **options 传 NULL**；
 *      传"零值结构体"会得到 `InvalidParameters(5)`（这是我第一版踩过的坑）。
 *   4. `sk_colortype_t` 的 C API 值 ≠ 托管 `SKColorType` 值（从 8 起分叉）；
 *      `Bgra8888 = 6` 两边恰好一致，本 shim 只用它。
 *   5. 释放数据用 `sk_data_unref`（**没有** `sk_data_destroy`）。
 *   6. `sk_alphatype_t`：Unknown=0, Opaque=1, Premul=2, Unpremul=3。
 *
 * 【句柄语义】
 *   `*_Proxy` 的句柄参数对托管侧是**不透明 IntPtr**（只回传、从不解引用），
 *   所以本 shim 用"下标+1"当句柄（绝不为 0），对象存在自己的表里 —— **不需要真 COM**。
 *
 * 【未实现的面】
 *   一律返回 `WINCODEC_ERR_NOTIMPLEMENTED = 0x88982F04`（明确 HRESULT，不是静默、
 *   也不是崩溃）。清单与"用到会怎样"见 REPORT.md §14。
 *
 * 构建：见同目录 build-wic-shim.sh（gcc -shared -fPIC -ldl）
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>
#include <zlib.h>   /* iTXt 的 zlib 解压（PNG 规范：iTXt 允许压缩文本）*/

#define S_OK 0
#define E_INVALIDARG ((int32_t)0x80070057)
#define E_OUTOFMEMORY ((int32_t)0x8007000E)
#define E_UNEXPECTED ((int32_t)0x8000FFFF)
/* ⚠ 修正：0x88982F04 在上游枚举里是 **WINCODEC_ERR_WRONGSTATE**，不是 NOTIMPLEMENTED
 * （我原先拿它当"未实现"，于是 PC 的 ConvertHRToException 把未实现报成
 *  InvalidOperationException「Operation caused an invalid state」——又是一句会撒谎的错误信息）。
 * 未实现就该报未实现：E_NOTIMPL ⇒ .NET 映射 NotImplementedException。 */
#define WINCODEC_ERR_NOTIMPLEMENTED ((int32_t)0x80004001)   /* E_NOTIMPL */
/* 取值一律抄上游 wgx_error.cs（PC 的 ConvertHRToException 按这些值选异常类型）。
 * 修正：0x88982F0B 在上游是 **UNSUPPORTEDVERSION**，不是 UNSUPPORTEDOPERATION——
 * 错标号让「非图像/不支持」经 PC 映射成 FileLoadException("Mismatched versions")。*/
/* wincodec.h：FACILITY_WINCODEC_ERR + 0x58（与 UNSUPPORTEDOPERATION=0x81、COMPONENTNOTFOUND=0x50 同族）。*/
#define WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT ((int32_t)0x88982F58)
#define WINCODEC_ERR_UNSUPPORTEDOPERATION ((int32_t)0x88982f81)
#define WINCODEC_ERR_UNKNOWNIMAGEFORMAT ((int32_t)0x88982f07)   /* → FileFormatException */
#define WINCODEC_ERR_BADIMAGE ((int32_t)0x88982f60)   /* → 图像损坏 */
#define WINCODEC_ERR_GENERIC_ERROR ((int32_t)0x80004005)   /* shim 基础设施失败 = E_FAIL */
#define WINCODEC_ERR_PROPERTYNOTFOUND ((int32_t)0x88982f40)
#define WINCODEC_ERR_WRONGSTATE_PLACEHOLDER() ((int32_t)0x88982f04)
#define WINCODEC_ERR_COMPONENTNOTFOUND ((int32_t)0x88982f50)   /* 上游 wgx_error.cs:36：没有该容器编码器 */   /* 上游 wgx_error.cs:21 真值（状态非法）*/   /* 上游 wgx_error.cs:27 → PC 映射 ArgumentException */

/* PROPVARIANT：24 字节，布局与托管 PropVariant.cs:63-86 的 [FieldOffset] 完全一致
 * （vt@0 / wReserved 2,4,6 / 联合体@8，x64 下 16 字节）——这是从源码抄的，不是猜的 */
typedef struct { uint16_t vt, r1, r2, r3; uint64_t v1, v2; } propvariant_t;
#define VT_EMPTY 0
#define VT_UI2   18   /* GIF `/grctlext/Delay` 在 WIC 里是 ushort（上游 PropVariant.cs:18 "ushort <=> VT_UI2"）*/
#define VT_UI4   19
#define VT_LPSTR 30
#define VT_LPWSTR 31
#define WINCODEC_ERR_WRONGSTATE ((int32_t)0x88982F04 | 0)   /* 占位：本 shim 未用 */

#define WIC_OBJ_MAX 256
#define FILE_SIZE_CAP (256u * 1024u * 1024u)

enum { KIND_NONE = 0, KIND_FACTORY, KIND_DECODER, KIND_FRAME, KIND_CONVERTER, KIND_CODECINFO, KIND_META,
       KIND_ENCODER, KIND_FRAMEENC, KIND_OPTIONS, KIND_STREAM, KIND_METAW, KIND_ENUM , KIND_LOCK,
       KIND_CLIPPER };

typedef struct { int32_t x, y, w, h; } wic_rect;   /* 与托管 Int32Rect 的四字段一致 */

typedef struct {
    int kind;
    int refs;   /* 引用计数：契约见文件末「引用计数契约」一节 */
    int has_memFormat;          /* 非 0 ⇒ 内存位图（CreateBitmapFromMemory）：像素已就绪 */
    unsigned char memFormat[16]; /* 调用方指定的像素格式 GUID（16 字节，与托管 ref Guid 同布局；
                                  * 不用 guid_t 是因为它在文件后段才 typedef）*/
    double dpiX, dpiY;          /* SetResolution 设定值；未设时按 96 报（见 REPORT §14.2 偏差）*/
    int has_dpi;
    intptr_t parent_h;          /* KIND_META/KIND_FRAMEENC/KIND_METAW：所属对象句柄 */

    /* ---- 写面 ---- */
    int enc_format;             /* 1=PNG 2=JPEG（由 CreateEncoder 的容器 GUID 决定）*/
    int quality;                /* JPEG 质量（默认 90；可由 IPropertyBag2 覆盖）*/
    intptr_t stream_h;          /* Initialize 收到的流句柄（MIL 流对象，或我们自己的 KIND_STREAM）*/
    int lines_written;          /* WritePixels 累计行数（PC 是按行带写的）*/
    int meta_count;             /* 已登记的元数据项（写面）*/
    char meta_keys[8][48];
    char meta_vals[8][192];
    unsigned char *stream_buf;  /* KIND_STREAM：自己的内存流 */
    size_t stream_len, stream_cap;
    int is_pbgra;               /* 源是 32bppPBGRA（预乘）⇒ 编码前要反解，否则半透明像素偏 */
    int pixels_borrowed;
    int is_foreign;             /* 外来（MIL 所有）句柄的派发条目：不计数、不拥有、不释放 */        /* pixels 是调用方（MIL）的内存，obj_free 不许 free 它 */
    int enum_count, enum_idx;   /* KIND_ENUM：字符串枚举器 */
    char enum_names[16][96];
    int fd;
    unsigned char *bytes;  size_t size;            /* 整份文件（pread 出来） */
    void *data; void *codec;                       /* sk_data_t* / sk_codec_t* */
    int32_t width, height, colorType, alphaType;
    unsigned char *pixels; size_t rowBytes;        /* 惰性解码后的 BGRA 像素 */
    int decoded;
    int32_t frame_index;   /* KIND_FRAME：本帧帧号（0 = 静态图 / 第 0 帧）；由 GetFrame 设定 */
    int32_t frame_count;   /* KIND_DECODER/KIND_FRAME：已探明帧数（0 = 未探明 ⇒ 当 1）*/
} wic_obj;

static wic_obj *g_objs[WIC_OBJ_MAX];

/* 调用计数：harness 用它判定 BitmapDecoder 实际走的是 fd 路径还是 Stream 路径。
 * [0]=CreateDecoderFromFileHandle  [1]=CreateStream **与 CreateDecoderFromStream**  [2]=其它入口 */
static int32_t g_calls[3];

/* 句柄表互斥与活句柄计数（见文件末「引用计数契约」）。
 * 只保护**结构变更与计数**：插入/摘除/增删引用/读计数。
 * decode_open / decode_pixels 的 Skia 调用仍不加锁——与既有行为一致，未新增风险面；
 * 「多线程并发解码」仍是已登记缺口，不在本轮范围。*/
static pthread_mutex_t g_table_lock = PTHREAD_MUTEX_INITIALIZER;
static int32_t g_live, g_peak;
static void table_note_add(void)    { if (++g_live > g_peak) g_peak = g_live; }
static void table_note_remove(void) { if (g_live > 0) --g_live; }

static void obj_drop(intptr_t h, wic_obj *o);   /* 定义在文件末 */
static int inherit_source_bytes(wic_obj *dst, wic_obj *src);   /* 前向声明：GetFrame 比定义更早使用 */
static wic_obj *as_source(void *handle);         /* 定义在「源对象」一节（本文件后段）*/
/* 外来源登记表（dispatch，不是记账）——实现见文件末 */
#define WIC_FOREIGN_MAX 64
static struct { intptr_t ext; intptr_t slot; } g_foreign[WIC_FOREIGN_MAX];
static wic_obj *foreign_lookup(intptr_t ext);
static int32_t g_foreign_borrows;   /* 处于借用状态的外来源条数 */
static int32_t g_trace_budget = 40;   /* 入口 trace 限流（防热路径刷屏）*/
static int decode_open(wic_obj *o);              /* 定义在「解码核心」一节 */
static int decode_pixels(wic_obj *o);

static intptr_t obj_new(int kind)
{
    pthread_mutex_lock(&g_table_lock);
    for (int i = 0; i < WIC_OBJ_MAX; i++) {
        if (g_objs[i]) continue;
        wic_obj *o = (wic_obj *)calloc(1, sizeof(wic_obj));
        if (!o) { pthread_mutex_unlock(&g_table_lock); return 0; }
        o->kind = kind; o->fd = -1;
        o->refs = 1;   /* 初始引用归**调用方**（PC 侧 SafeMILHandle）*/
        g_objs[i] = o;
        table_note_add();
        pthread_mutex_unlock(&g_table_lock);
        return (intptr_t)(i + 1);
    }
    pthread_mutex_unlock(&g_table_lock);
    return 0;   /* ⚠ 表满（WIC_OBJ_MAX=256）→ 调用方看到创建失败，见「引用计数契约」 */
}

static wic_obj *obj_get(intptr_t h)
{
    if (h <= 0 || h > WIC_OBJ_MAX) return NULL;
    return g_objs[h - 1];
}

/* ===========================================================================
 * 元数据：PNG（pHYs / tEXt / iTXt）与 JPEG（JFIF APP0 density / EXIF IFD0）自带解析
 *
 * 为什么自带解析：Skia 的 C API 不暴露元数据（只有像素/尺寸/格式），
 * 而 WPF 的 BitmapMetadata 与 DPI 都要求真值。字节我们本来就整份读进来了（o->bytes）。
 * =========================================================================== */
static uint32_t be32(const unsigned char *p) { return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3]; }
static uint16_t be16(const unsigned char *p) { return (uint16_t)(((uint16_t)p[0]<<8)|p[1]); }

static int is_png_bytes(const unsigned char *b, size_t n)
{ return n >= 8 && b[0]==0x89 && b[1]=='P' && b[2]=='N' && b[3]=='G'; }
static int is_jpeg_bytes(const unsigned char *b, size_t n)
{ return n >= 3 && b[0]==0xFF && b[1]==0xD8 && b[2]==0xFF; }
static int is_gif_bytes(const unsigned char *b, size_t n)
{ return n >= 6 && b[0]=='G' && b[1]=='I' && b[2]=='F' && b[3]=='8'; }

/* ---------------------------------------------------------------------------
 * GIF：取出**第 index 帧**的 GCE 延迟（单位 1/100 秒，即 WIC 的 `/grctlext/Delay`）。
 *
 * 【为什么要自己走一遍结构】Skia 的 C API 只给帧的**像素与帧数**
 * （`sk_codec_get_frame_count`；帧时长只能从 `sk_codec_frame_info_t` 的 +4 取，
 *  那是另一处未文档化的 ABI 面）。`/grctlext/Delay` 本来就是 **GIF 容器自己**的语义
 * ⇒ 按 GIF89a 规范走块结构，读 Graphic Control Extension 的 Delay 字段。
 * 命中返回 1（含"确实有 GCE、延迟就是 0"），没有 GCE / 结构坏 / 帧号越界返回 0。
 * 只读、不改、不分配；数据来自父对象的 `o->bytes`（整份文件）。
 * --------------------------------------------------------------------------- */
static int gif_frame_delay_cs(const unsigned char *b, size_t n, int32_t index, uint16_t *out)
{
    if (!b || n < 13 || !is_gif_bytes(b, n) || index < 0) return 0;

    size_t pos = 13;
    unsigned char packed = b[10];
    if (packed & 0x80) {                                  /* 全局色表 */
        size_t sz = (size_t)3 * (1u << ((packed & 7) + 1));
        if (pos + sz > n) return 0;
        pos += sz;
    }

    int32_t frame_no = 0;
    int have_gce = 0;
    uint16_t delay = 0;

    while (pos < n) {
        unsigned char blk = b[pos];
        if (blk == 0x3B) break;                           /* trailer */
        if (blk == 0x21) {                                /* 扩展块 */
            if (pos + 2 > n) return 0;
            unsigned char label = b[pos + 1];
            size_t p = pos + 2;
            uint16_t gce_delay = 0;
            int gce_here = 0;
            while (p < n && b[p] != 0) {                  /* 一个扩展 = 一串子块 */
                size_t len = b[p];
                if (p + 1 + len > n) return 0;
                if (label == 0xF9 && len >= 4) {          /* Graphic Control Extension */
                    gce_here = 1;
                    gce_delay = (uint16_t)(b[p + 2] | ((uint16_t)b[p + 3] << 8));
                }
                p += 1 + len;
            }
            if (p >= n) return 0;
            pos = p + 1;                                  /* 跳过块终结符 */
            if (gce_here) { have_gce = 1; delay = gce_delay; }
            continue;
        }
        if (blk == 0x2C) {                                /* 图像描述符 = 一帧 */
            if (pos + 10 > n) return 0;
            unsigned char ip = b[pos + 9];
            size_t p = pos + 10;
            if (ip & 0x80) {                              /* 局部色表 */
                size_t sz = (size_t)3 * (1u << ((ip & 7) + 1));
                if (p + sz > n) return 0;
                p += sz;
            }
            if (p >= n) return 0;
            p += 1;                                       /* LZW 最小码长 */
            while (p < n && b[p] != 0) {                  /* 数据子块 */
                size_t len = b[p];
                if (p + 1 + len > n) return 0;
                p += 1 + len;
            }
            if (p >= n) return 0;
            pos = p + 1;

            if (frame_no == index) {                      /* 这就是要查的那一帧 */
                if (!have_gce) return 0;                  /* 没有 GCE ⇒ 没有该属性 */
                if (out) *out = delay;
                return 1;
            }
            frame_no++;
            have_gce = 0; delay = 0;
            continue;
        }
        return 0;                                         /* 结构不认识：如实说"没有" */
    }
    return 0;
}

/* PNG：按 chunk 走。key 匹配 tEXt/iTXt 的关键字（PNG 关键字大小写敏感）。
 * 命中返回 malloc 的字符串（NUL 结尾），否则 NULL。 */
static char *png_get_text(const unsigned char *b, size_t n, const char *key)
{
    size_t i = 8, klen = strlen(key);
    while (i + 12 <= n) {
        uint32_t ln = be32(b + i);
        const unsigned char *type = b + i + 4;
        const unsigned char *data = b + i + 8;
        if (i + 12 + (size_t)ln > n) break;
        if (memcmp(type, "tEXt", 4) == 0 || memcmp(type, "iTXt", 4) == 0) {
            size_t k = 0; while (k < ln && data[k] != 0) k++;
            if (k == klen && k < ln && memcmp(data, key, klen) == 0) {
                size_t off = k + 1;
                if (memcmp(type, "iTXt", 4) == 0) {
                    /* keyword\0 compFlag compMethod langTag\0 translatedKeyword\0 text */
                    if (off + 2 > ln) return NULL;
                    int compressed = data[off];
                    off += 2;
                    while (off < ln && data[off] != 0) off++;   /* languageTag */
                    if (off < ln) off++;
                    while (off < ln && data[off] != 0) off++;   /* translatedKeyword */
                    if (off < ln) off++;
                    if (getenv("WPF_LINUX_WIC_TRACE"))
                        fprintf(stderr, "WIC_TRACE ITXT key=%s comp=%d ln=%u off=%zu first=%02x %02x %02x %02x\n",
                                key, compressed, (unsigned)ln, off,
                                ln > off ? data[off] : 0, ln > off+1 ? data[off+1] : 0,
                                ln > off+2 ? data[off+2] : 0, ln > off+3 ? data[off+3] : 0);
                    if (compressed) {                           /* iTXt 的文本是 zlib 流（PNG 规范）*/
                        if (off + 4 > ln) return NULL;
                        uLongf outLen = (uLongf)(ln * 8 + 256);
                        unsigned char *ub = (unsigned char *)malloc(outLen);
                        if (!ub) return NULL;
                        if (uncompress(ub, &outLen, data + off, (uLong)(ln - off)) != Z_OK) { free(ub); return NULL; }
                        char *z = (char *)malloc(outLen + 1);
                        if (!z) { free(ub); return NULL; }
                        memcpy(z, ub, outLen); z[outLen] = 0; free(ub);
                        return z;
                    }
                }
                if (off > ln) return NULL;
                size_t tlen = ln - off;
                char *out = (char *)malloc(tlen + 1);
                if (!out) return NULL;
                memcpy(out, data + off, tlen); out[tlen] = 0;
                return out;
            }
        }
        if (memcmp(type, "IEND", 4) == 0) break;
        i += 12 + (size_t)ln;
    }
    return NULL;
}

/* PNG pHYs：xppu(4) yppu(4) unit(1)；unit==1 表示"像素/米" */
static int png_get_phys(const unsigned char *b, size_t n, double *dx, double *dy)
{
    size_t i = 8;
    while (i + 12 <= n) {
        uint32_t ln = be32(b + i);
        const unsigned char *type = b + i + 4;
        const unsigned char *data = b + i + 8;
        if (i + 12 + (size_t)ln > n) break;
        if (memcmp(type, "pHYs", 4) == 0 && ln >= 9 && data[8] == 1) {
            *dx = be32(data)     * 0.0254;   /* 像素/米 → 像素/英寸 */
            *dy = be32(data + 4) * 0.0254;
            return 1;
        }
        if (memcmp(type, "IEND", 4) == 0) break;
        i += 12 + (size_t)ln;
    }
    return 0;
}

/* JPEG：JFIF APP0（units=1 dpi / units=2 dots-per-cm）*/
static int jpeg_get_jfif(const unsigned char *b, size_t n, double *dx, double *dy)
{
    size_t i = 2;
    while (i + 4 <= n) {
        if (b[i] != 0xFF) break;
        unsigned char m = b[i+1];
        if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
        if (m == 0xDA || m == 0xD9) break;             /* 进入压缩数据 */
        uint16_t seglen = be16(b + i + 2);
        if (seglen < 2 || i + 2 + seglen > n) break;
        if (m == 0xE0 && seglen >= 14 && memcmp(b + i + 4, "JFIF", 4) == 0) {
            unsigned char units = b[i + 4 + 7];
            uint16_t xd = be16(b + i + 4 + 8), yd = be16(b + i + 4 + 10);
            if (units == 1 && xd && yd) { *dx = xd; *dy = yd; return 1; }
            if (units == 2 && xd && yd) { *dx = xd * 2.54; *dy = yd * 2.54; return 1; }
            return 0;                                   /* units==0：只给纵横比，不是 DPI */
        }
        i += 2 + seglen;
    }
    return 0;
}

/* 设备分辨率真值：PNG pHYs / JPEG JFIF；解析不到返回 0（调用方再退 96）*/
static int meta_resolution(wic_obj *o, double *dx, double *dy)
{
    int hr = decode_open(o);
    if (hr != S_OK || !o->bytes || o->size < 8) return 0;
    if (is_png_bytes(o->bytes, o->size))  return png_get_phys(o->bytes, o->size, dx, dy);
    if (is_jpeg_bytes(o->bytes, o->size)) return jpeg_get_jfif(o->bytes, o->size, dx, dy);
    return 0;
}

static void pv_set_lpstr(propvariant_t *pv, const char *s, size_t len)
{
    char *copy = (char *)malloc(len + 1);
    pv->vt = VT_LPSTR; pv->r1 = pv->r2 = pv->r3 = 0;
    if (!copy) { pv->vt = VT_EMPTY; pv->v1 = pv->v2 = 0; return; }
    memcpy(copy, s, len); copy[len] = 0;
    pv->v1 = (uint64_t)(uintptr_t)copy; pv->v2 = 0;
}
static void pv_set_empty(propvariant_t *pv)
{ pv->vt = VT_EMPTY; pv->r1 = pv->r2 = pv->r3 = 0; pv->v1 = pv->v2 = 0; }

/* VT_UI2：值放联合体首 2 字节（x64 下 = v1 的低 16 位）*/
static void pv_set_ui2(propvariant_t *pv, uint16_t v)
{ pv->vt = VT_UI2; pv->r1 = pv->r2 = pv->r3 = 0; pv->v1 = v; pv->v2 = 0; }

/* 查询串是 ASCII（WIC 的查询语言），非 ASCII 字节按 '?' 落——本轮不做完整 UTF-8 解码（登记）*/
static void utf16_to_utf8(const uint16_t *w, char *out, size_t cap)
{
    size_t i = 0;
    for (; w && w[i] && i + 1 < cap; i++) out[i] = (w[i] < 0x80) ? (char)w[i] : '?';
    out[i] = 0;
}

static char *jpeg_exif_text(wic_obj *o, uint32_t tag, int sub);   /* 定义在 EXIF 一节 */

/* 该对象所属文件里有没有**我们能提供**的元数据（本轮：PNG 的 tEXt/iTXt）*/
static int meta_has_any(wic_obj *o)
{
    if (decode_open(o) != S_OK || !o->bytes) return 0;
    /* `#50` 新增：GIF 的**帧**能提供 `/grctlext/Delay`（GCE 延迟）。
     * 只对 KIND_FRAME 生效 —— 解码器级元数据查询面（`IWICBitmapDecoder_GetMetadataQueryReader`）
     * 行为一字不变（PC 那条路只读 decoder 的容器级元数据）。*/
    if (o->kind == KIND_FRAME && is_gif_bytes(o->bytes, o->size)) {
        uint16_t cs = 0;
        return gif_frame_delay_cs(o->bytes, o->size, o->frame_index, &cs);
    }
    if (is_jpeg_bytes(o->bytes, o->size)) {              /* JPEG：有 EXIF 文本标签才算"有元数据" */
        static const uint32_t t0[] = { 270, 271, 272, 305, 306, 315, 316, 33432 };
        static const uint32_t t1[] = { 36867, 36868, 42036 };
        for (size_t k = 0; k < sizeof t0 / sizeof t0[0]; k++) { char *v = jpeg_exif_text(o, t0[k], 0); if (v) { free(v); return 1; } }
        for (size_t k = 0; k < sizeof t1 / sizeof t1[0]; k++) { char *v = jpeg_exif_text(o, t1[k], 1); if (v) { free(v); return 1; } }
        return 0;
    }
    if (!is_png_bytes(o->bytes, o->size)) return 0;
    size_t i = 8;
    while (i + 12 <= o->size) {
        uint32_t ln = be32(o->bytes + i);
        const unsigned char *type = o->bytes + i + 4;
        if (i + 12 + (size_t)ln > o->size) break;
        if (memcmp(type, "tEXt", 4) == 0 || memcmp(type, "iTXt", 4) == 0) return 1;
        if (memcmp(type, "IEND", 4) == 0) break;
        i += 12 + (size_t)ln;
    }
    return 0;
}

/* ---- JPEG EXIF：APP1 → TIFF → IFD0 / ExifIFD 的 ASCII 标签 ----
 * 类型码（TIFF 规范）：2=ASCII, 3=SHORT, 4=LONG, 5=RATIONAL。本轮只取 ASCII（文本）。*/
static const unsigned char *jpeg_exif_tiff(const unsigned char *b, size_t n, size_t *pTiffLen)
{
    size_t i = 2;
    while (i + 4 <= n) {
        if (b[i] != 0xFF) return NULL;
        unsigned char m = b[i + 1];
        if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
        if (m == 0xDA || m == 0xD9) return NULL;
        uint16_t seglen = be16(b + i + 2);
        if (seglen < 2 || i + 2 + seglen > n) return NULL;
        if (m == 0xE1 && seglen >= 8 && memcmp(b + i + 4, "Exif\0\0", 6) == 0) {
            *pTiffLen = seglen - 2 - 6;
            return b + i + 4 + 6;
        }
        i += 2 + seglen;
    }
    return NULL;
}

static uint32_t tiff_u16(const unsigned char *t, int le, size_t off)
{ return le ? (uint32_t)(t[off] | (t[off+1] << 8)) : (uint32_t)((t[off] << 8) | t[off+1]); }
static uint32_t tiff_u32(const unsigned char *t, int le, size_t off)
{ return le ? ((uint32_t)t[off] | ((uint32_t)t[off+1]<<8) | ((uint32_t)t[off+2]<<16) | ((uint32_t)t[off+3]<<24))
            : (((uint32_t)t[off]<<24) | ((uint32_t)t[off+1]<<16) | ((uint32_t)t[off+2]<<8) | (uint32_t)t[off+3]); }

/* 在指定 IFD 里找 tag 的 ASCII 值；返回 malloc 字符串 */
static char *tiff_ifd_ascii(const unsigned char *t, size_t tn, int le, size_t ifdOff, uint32_t want, size_t *pValOff)
{
    if (ifdOff + 2 > tn) return NULL;
    uint32_t cnt = tiff_u16(t, le, ifdOff);
    size_t e = ifdOff + 2;
    for (uint32_t k = 0; k < cnt && e + 12 <= tn; k++, e += 12) {
        uint32_t tag = tiff_u16(t, le, e);
        uint32_t type = tiff_u16(t, le, e + 2);
        uint32_t count = tiff_u32(t, le, e + 4);
        if (tag != want) continue;
        if (type == 4 && count == 1 && pValOff) { *pValOff = tiff_u32(t, le, e + 8); return NULL; }  /* 子 IFD 指针 */
        if (type != 2 || count == 0) return NULL;
        size_t voff = (count <= 4) ? (e + 8) : tiff_u32(t, le, e + 8);
        if (voff + count > tn) return NULL;
        char *out = (char *)malloc(count + 1);
        if (!out) return NULL;
        memcpy(out, t + voff, count); out[count] = 0;
        return out;
    }
    return NULL;
}

/* 解析 "/app1/ifd/{ushort=N}" 与 "/app1/ifd/exif/{ushort=N}" 与 "/app1/ifd/gps/{ushort=N}" */
static int exif_tag_from_query(const char *q, uint32_t *pTag, int *pSub)
{
    *pSub = 0;
    if (strncmp(q, "/app1/ifd/", 10) != 0) return 0;
    const char *r = q + 10;
    if (strncmp(r, "exif/", 5) == 0) { *pSub = 1; r += 5; }
    else if (strncmp(r, "gps/", 4) == 0) { *pSub = 2; r += 4; }
    if (strncmp(r, "{ushort=", 8) != 0) return 0;
    *pTag = (uint32_t)strtoul(r + 8, NULL, 10);
    return 1;
}

static char *jpeg_exif_text(wic_obj *o, uint32_t tag, int sub)
{
    size_t tn = 0;
    const unsigned char *t = jpeg_exif_tiff(o->bytes, o->size, &tn);
    if (!t || tn < 8) return NULL;
    int le;
    if (t[0] == 'I' && t[1] == 'I') le = 1;
    else if (t[0] == 'M' && t[1] == 'M') le = 0;
    else return NULL;
    if (tiff_u16(t, le, 2) != 42) return NULL;
    size_t ifd0 = tiff_u32(t, le, 4);
    if (sub == 0) return tiff_ifd_ascii(t, tn, le, ifd0, tag, NULL);
    size_t subOff = 0;
    uint32_t ptr = (sub == 1) ? 0x8769u : 0x8825u;      /* ExifIFD / GPS IFD 指针标签 */
    if (!tiff_ifd_ascii(t, tn, le, ifd0, ptr, &subOff) && subOff == 0) {
        /* tiff_ifd_ascii 对指针标签返回 NULL 但会写 subOff */
    }
    if (subOff == 0 || subOff + 2 > tn) return NULL;
    return tiff_ifd_ascii(t, tn, le, subOff, tag, NULL);
}

/* 把 WPF 的查询串落到 PNG 关键字：支持 "/tEXt/{str=Title}" 与 "/tEXt/Title" 两种写法 */
static const char *meta_key_from_query(const char *q, size_t *pKeyLen)
{
    if (!q) return NULL;
    if (*q == '/') q++;
    if (strncmp(q, "tEXt/", 5) == 0) q += 5;
    else if (strncmp(q, "iTXt/", 5) == 0) q += 5;
    else return NULL;

    if (strncmp(q, "{str=", 5) == 0) {
        const char *end = strchr(q, '}');
        if (!end) return NULL;
        q += 5;
        *pKeyLen = (size_t)(end - q);
    } else {
        *pKeyLen = strlen(q);
    }
    return q;
}


/* ------------------------------------------------------------------ Skia C API 惰性绑定 */
typedef void sk_data_t; typedef void sk_codec_t;
typedef struct { void *colorspace; int32_t width, height; int colorType; int alphaType; } sk_imageinfo_t;

typedef sk_data_t *(*pfn_data_new_with_copy)(const void *, size_t);
typedef void (*pfn_data_unref)(sk_data_t *);           /* 注意：不是 sk_data_destroy */
typedef sk_codec_t *(*pfn_codec_new_from_data)(sk_data_t *);
typedef void (*pfn_codec_destroy)(sk_codec_t *);
typedef int (*pfn_codec_get_info)(sk_codec_t *, sk_imageinfo_t *);         /* 返回值无语义 */
typedef int (*pfn_codec_get_pixels)(sk_codec_t *, const sk_imageinfo_t *, void *, size_t, void *); /* options=NULL */
typedef int (*pfn_codec_get_frame_count)(sk_codec_t *);   /* `#50`：多帧图的真实帧数 */

/* SkCodec::Options 的**真实布局**（`#50` 反推自 libSkiaSharp.so，**不是**照抄头文件）：
 *   +0  int32  fZeroInitialized（Skia 默认 1 = kNo_ZeroInitialized）
 *   +8  ptr    fSubset（SkIRect*，默认 NULL）
 *   +16 int32  fFrameIndex（默认 0）——**选帧就靠这一格**
 *   +20 int32  fPriorFrame（默认 -1 = kNoFrame）
 * 依据：`sk_codec_get_pixels` 收 NULL 时在 .so 内构造"默认 options"的那几条 store
 *   （`movl $1,0x30(%rsp)` ／ `movq $0,0x38(%rsp)` ／ `movabs $0xffffffff00000000` 写到 +0x40
 *    ⇒ +16=0、+20=-1），且 +8 被当指针解引用（fSubset）。24 字节。
 * ⚠ 逐字段照抄；顺序/宽度改一个字就会退化成 InvalidParameters(5)。*/
typedef struct {
    int32_t zero_initialized;
    int32_t _pad;
    void   *subset;
    int32_t frame_index;
    int32_t prior_frame;
} sk_codec_options_t;

static void *g_skia;
static pfn_data_new_with_copy sk_data_new_with_copy;
static pfn_data_unref         sk_data_unref;
static pfn_codec_new_from_data sk_codec_new_from_data;
static pfn_codec_destroy      sk_codec_destroy;
static pfn_codec_get_info     sk_codec_get_info;
static pfn_codec_get_pixels   sk_codec_get_pixels;
static pfn_codec_get_frame_count sk_codec_get_frame_count;   /* 缺失 ⇒ 只能当静态图（具名降级，见 decode_frame_count）*/

#define SK_BGRA_8888 6   /* C API 的 sk_colortype_t：Bgra8888=6（与托管同值，实测一致） */

/* 用 dladdr 自定位：拿到本 shim 的目录，优先找同目录的 libSkiaSharp.so
 *（主控的部署口径就是"只要两个 .so 同目录"）。 */
static void self_dir(char *out, size_t cap)
{
    Dl_info info;
    out[0] = 0;
    if (!dladdr((void *)&self_dir, &info) || !info.dli_fname) return;
    const char *slash = strrchr(info.dli_fname, '/');
    if (!slash) return;
    size_t n = (size_t)(slash - info.dli_fname);
    if (n + 1 > cap) n = cap - 1;
    memcpy(out, info.dli_fname, n);
    out[n] = 0;
}

/* 本机架构对应的 .NET RID（用于下面的 runtimes/<rid>/native/ 搜索）*/
#if defined(__x86_64__)
#  define WIC_RID_ARCH "x64"
#elif defined(__aarch64__)
#  define WIC_RID_ARCH "arm64"
#elif defined(__arm__)
#  define WIC_RID_ARCH "arm"
#else
#  define WIC_RID_ARCH "x64"
#endif

static int skia_load(void)
{
    if (g_skia) return 1;

    /* 候选顺序：① WPF_LINUX_WIC_SKIA ② **本 shim 同目录**（主控的部署口径）
     *          ③ 同目录下的 .NET native-asset 约定目录 runtimes/<rid>/native/
     *          ④ 裸名（交给 ld.so 的 rpath / LD_LIBRARY_PATH）
     *
     * ⚠ 为什么必须有 ③：包化部署（WpfLinux.Sdk + SkiaSharp.NativeAssets.Linux）里，
     *   我们的 shim（libwpfwic.so）被复制到输出根，而 SkiaSharp 的 native 资产按 NuGet/.NET 约定
     *   落在 `<app>/runtimes/linux-x64/native/libSkiaSharp.so` —— **不在根目录**。
     *   只有 ② 时 skia_load 恒失败 ⇒ 每个 CreateDecoderFromStream 都返回
     *   0x80004005(GENERIC_ERROR) ⇒ XAML 里 `<Image Source=…>`（pack 资源位图）整棵模板建不起来
     *   （实测：ICDStudioSdkProbe 主界面只剩标题，内容区空白）。*/
    const char *env = getenv("WPF_LINUX_WIC_SKIA");
    char beside[4096];
    if (env && *env) {
        g_skia = dlopen(env, RTLD_NOW | RTLD_GLOBAL);
    }
    if (!g_skia) {
        self_dir(beside, sizeof beside);
        if (beside[0]) {
            char candidate[4300];
            snprintf(candidate, sizeof candidate, "%s/libSkiaSharp.so", beside);
            g_skia = dlopen(candidate, RTLD_NOW | RTLD_GLOBAL);
        }
    }
    if (!g_skia && beside[0]) {
        char candidate[4300];
        static const char *rids[] = { "linux-" WIC_RID_ARCH, "linux-musl-" WIC_RID_ARCH };
        for (size_t i = 0; i < sizeof(rids) / sizeof(rids[0]) && !g_skia; i++) {
            snprintf(candidate, sizeof candidate, "%s/runtimes/%s/native/libSkiaSharp.so", beside, rids[i]);
            g_skia = dlopen(candidate, RTLD_NOW | RTLD_GLOBAL);
        }
    }
    if (!g_skia) g_skia = dlopen("libSkiaSharp.so", RTLD_NOW | RTLD_GLOBAL);
    if (!g_skia) return 0;

    sk_data_new_with_copy  = (pfn_data_new_with_copy)dlsym(g_skia, "sk_data_new_with_copy");
    sk_data_unref          = (pfn_data_unref)dlsym(g_skia, "sk_data_unref");
    sk_codec_new_from_data = (pfn_codec_new_from_data)dlsym(g_skia, "sk_codec_new_from_data");
    sk_codec_destroy       = (pfn_codec_destroy)dlsym(g_skia, "sk_codec_destroy");
    sk_codec_get_info      = (pfn_codec_get_info)dlsym(g_skia, "sk_codec_get_info");
    sk_codec_get_pixels    = (pfn_codec_get_pixels)dlsym(g_skia, "sk_codec_get_pixels");
    /* 可选绑定：**不进**下面那条 return 的与式 —— 老 Skia 没有它时不该整个解码面失效，
     * 而是"帧数退化为 1"并由 decode_frame_count 打具名台账（本机实测该符号存在）。*/
    sk_codec_get_frame_count = (pfn_codec_get_frame_count)dlsym(g_skia, "sk_codec_get_frame_count");

    return sk_data_new_with_copy && sk_data_unref && sk_codec_new_from_data &&
           sk_codec_destroy && sk_codec_get_info && sk_codec_get_pixels;
}

/* ------------------------------------------------------------------ 解码核心 */
/* 从 fd 读整份文件并交给 Skia；成功后把尺寸/格式填进 o（像素等到 CopyPixels 再解）。 */
/* 从**已经在 o->bytes/o->size 里的字节**建 Skia 解码器（file 路径与 stream 路径共用）。
 * 幂等：`decode_open` 在本文件里有 17 个调用点，全部依赖"重复调用不出错"。*/
static int decode_open_bytes(wic_obj *o)
{
    if (o->has_memFormat) return S_OK;   /* 内存位图：无字节可开，像素已就绪 */
    if (o->codec) return S_OK;           /* 已开过（幂等）*/
    if (!o->bytes || o->size == 0) return E_UNEXPECTED;
    if (!skia_load()) return WINCODEC_ERR_GENERIC_ERROR;

    o->data = sk_data_new_with_copy(o->bytes, o->size);
    if (!o->data) return E_OUTOFMEMORY;

    o->codec = sk_codec_new_from_data((sk_data_t *)o->data);
    if (!o->codec) return WINCODEC_ERR_UNKNOWNIMAGEFORMAT;   /* 非图像 / 不支持的格式 */

    sk_imageinfo_t info; memset(&info, 0, sizeof info);
    sk_codec_get_info((sk_codec_t *)o->codec, &info);          /* 返回值无语义，只信结构体 */
    o->width = info.width; o->height = info.height;
    o->colorType = info.colorType; o->alphaType = info.alphaType;
    if (o->width <= 0 || o->height <= 0) return WINCODEC_ERR_BADIMAGE;

    o->rowBytes = (size_t)o->width * 4;                        /* 我们一律要 Bgra8888 */
    return S_OK;
}

/* 从 fd 读整份文件并交给 Skia；成功后把尺寸/格式填进 o（像素等到 CopyPixels 再解）。
 * ⚠️ 流路径（`CreateDecoderFromStream`）建出来的对象 **fd == -1**、字节已在 o->bytes 里
 *    ⇒ 本函数必须先看 `o->codec` 再判 fd，否则重复调用会掉进"无 fd"分支返回
 *    E_UNEXPECTED（那会让 GetSize/GetPixelFormat 这些后续入口全部假红）。*/
static int decode_open(wic_obj *o)
{
    if (o->has_memFormat) return S_OK;
    if (o->codec) return S_OK;

    /* 字节已经在手上（流路径建出来的 decoder，或由 inherit_source_bytes 复制过字节的
     * frame/converter）⇒ 直接开，**不要**再去看 fd：这些对象的 fd 就是 -1。*/
    if (o->bytes && o->size) return decode_open_bytes(o);

    if (o->fd < 0) return E_UNEXPECTED;
    if (!skia_load()) return WINCODEC_ERR_GENERIC_ERROR;

    off_t size = lseek(o->fd, 0, SEEK_END);
    if (size <= 0 || (unsigned long)size > FILE_SIZE_CAP) return WINCODEC_ERR_WRONGSTATE;
    if (lseek(o->fd, 0, SEEK_SET) < 0) return E_UNEXPECTED;

    o->bytes = (unsigned char *)malloc((size_t)size);
    if (!o->bytes) return E_OUTOFMEMORY;

    ssize_t got = pread(o->fd, o->bytes, (size_t)size, 0);
    if (got != size) { free(o->bytes); o->bytes = NULL; return E_UNEXPECTED; }
    o->size = (size_t)size;

    return decode_open_bytes(o);
}

/* 惰性解出 BGRA 像素（CopyPixels 时用） */
static int decode_pixels(wic_obj *o)
{
    if (o->has_memFormat) return o->pixels ? S_OK : E_UNEXPECTED;
    if (o->pixels) return S_OK;
    int hr = decode_open(o);
    if (hr != S_OK) return hr;

    size_t need = o->rowBytes * (size_t)o->height;
    o->pixels = (unsigned char *)malloc(need);
    if (!o->pixels) return E_OUTOFMEMORY;

    sk_imageinfo_t info; memset(&info, 0, sizeof info);
    info.colorspace = NULL; info.width = o->width; info.height = o->height;
    info.colorType = SK_BGRA_8888; info.alphaType = o->alphaType;

    /* 第 0 帧：options 继续传 NULL ⇒ 调用与改动前**逐字相同**（静态图零回归由构造保证）。
     * 逐帧：options 带 fFrameIndex（见 sk_codec_options_t 的布局来源）。
     * 实测（`#50`，两份夹具 × 4 种 options 变体）：`fPriorFrame=-1` 的**单帧直解**与
     * "从 0 重放"逐位相同 ⇒ 不必重放（Skia 自己按 frameInfo.requiredFrame 补前置帧）。*/
    int res;
    if (o->frame_index > 0) {
        sk_codec_options_t opts;
        memset(&opts, 0, sizeof opts);
        opts.zero_initialized = 1;          /* = Skia 默认 kNo_ZeroInitialized */
        opts.subset = NULL;
        opts.frame_index = o->frame_index;
        opts.prior_frame = -1;              /* = kNoFrame */
        res = sk_codec_get_pixels((sk_codec_t *)o->codec, &info, o->pixels, o->rowBytes, &opts);
        if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
            fprintf(stderr, "WIC_TRACE DECODE_FRAME index=%d res=%d\n", o->frame_index, res);
    } else {
        /* 关键：options 传 NULL（传零值结构体会得到 InvalidParameters=5，实测） */
        res = sk_codec_get_pixels((sk_codec_t *)o->codec, &info, o->pixels, o->rowBytes, NULL);
    }
    if (res != 0) { free(o->pixels); o->pixels = NULL; return E_UNEXPECTED; }
    o->decoded = 1;
    return S_OK;
}

static void obj_free(wic_obj *o)
{
    if (!o) return;
    if (o->pixels && !o->pixels_borrowed) free(o->pixels);   /* 借来的（外来源）不许 free */
    if (o->codec && sk_codec_destroy) sk_codec_destroy((sk_codec_t *)o->codec);
    if (o->data && sk_data_unref) sk_data_unref((sk_data_t *)o->data);
    if (o->bytes) free(o->bytes);
    if (o->fd >= 0) close(o->fd);
    free(o);
}

/* ===========================================================================
 *  写面：Skia 编码 + 流写入（MIL 流句柄 / 自己的 WIC 流）+ PNG chunk 注入
 * =========================================================================== */
static uint32_t crc32_bytes(const unsigned char *b, size_t n)
{
    uint32_t c = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; i++) {
        c ^= b[i];
        for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint32_t)(-(int32_t)(c & 1)));
    }
    return c ^ 0xFFFFFFFFu;
}

static void put_be32(unsigned char *p, uint32_t v)
{ p[0]=(unsigned char)(v>>24); p[1]=(unsigned char)(v>>16); p[2]=(unsigned char)(v>>8); p[3]=(unsigned char)v; }

/* MIL 的 IStream 写入入口：MILIStreamWrite 的 AOT 包装是 C 可调的
 * （src/Linux/build/MilBridge/src/MilBridge.Linux/Exports.g.cs:204-205 → void*, byte*, uint, uint*）。*/
typedef int32_t (*pfn_mil_stream_write)(void *, unsigned char *, uint32_t, uint32_t *);
static pfn_mil_stream_write g_mil_write;
static int mil_write_load(void)
{
    if (g_mil_write) return 1;
    void *h = dlopen(NULL, RTLD_NOW);                 /* 主命名空间（MIL 若 RTLD_GLOBAL 就在）*/
    if (h) g_mil_write = (pfn_mil_stream_write)dlsym(h, "MILIStreamWrite");
    if (!g_mil_write) {
        /* 退路：从 /proc/self/maps 找到已加载的 wpfgfx_cor3.so，按路径 dlopen 取符号
         * （同一路径 ⇒ 同一实例，不会造出第二份）*/
        FILE *f = fopen("/proc/self/maps", "r");
        if (f) {
            char line[4096];
            while (fgets(line, sizeof line, f)) {
                char *slash = strchr(line, '/');
                if (!slash) continue;
                char *nl = strchr(slash, '\n'); if (nl) *nl = 0;
                if (!strstr(slash, "wpfgfx_cor3.so")) continue;
                void *lib = dlopen(slash, RTLD_NOW | RTLD_LOCAL);
                if (lib) { g_mil_write = (pfn_mil_stream_write)dlsym(lib, "MILIStreamWrite"); if (g_mil_write) break; }
            }
            fclose(f);
        }
    }
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE MIL_WRITE=%s\n", g_mil_write ? "ok" : "MISSING");
    return g_mil_write != NULL;
}

/* 往流里写字节：自己的 KIND_STREAM 直接追加；否则交给 MIL 的 MILIStreamWrite */
static int32_t stream_write_bytes(intptr_t h, const unsigned char *b, size_t n, const char **why)
{
    *why = "";
    if (h <= 0) { *why = "stream 句柄为空（Initialize 没被调用？）"; return E_INVALIDARG; }
    wic_obj *o = obj_get(h);
    if (o && o->kind == KIND_STREAM) {
        if (o->stream_len + n > o->stream_cap) {
            size_t cap = o->stream_cap ? o->stream_cap : 4096;
            while (cap < o->stream_len + n) cap *= 2;
            unsigned char *nb = (unsigned char *)realloc(o->stream_buf, cap);
            if (!nb) { *why = "自己的流扩容失败"; return E_OUTOFMEMORY; }
            o->stream_buf = nb; o->stream_cap = cap;
        }
        memcpy(o->stream_buf + o->stream_len, b, n);
        o->stream_len += n;
        return S_OK;
    }
    if (!mil_write_load()) { *why = "取不到 MILIStreamWrite"; return WINCODEC_ERR_GENERIC_ERROR; }
    uint32_t written = 0;
    int32_t hr = g_mil_write((void *)h, (unsigned char *)b, (uint32_t)n, &written);
    if (hr != S_OK) { *why = "MILIStreamWrite 返回失败"; return hr; }
    if (written != n) { *why = "MILIStreamWrite 写入字节数不符"; return E_UNEXPECTED; }
    return S_OK;
}

/* Skia 编码侧绑定（**枚举值实测得来**：PNG=4 / JPEG=3，见 probe_encode.c 输出）*/
typedef void sk_image_t;
static sk_image_t *(*sk_image_new_raster_copy)(const sk_imageinfo_t *, const void *, size_t);
static sk_data_t   *(*sk_image_encode_specific)(const sk_image_t *, int, int);
static const void  *(*sk_data_get_data)(const sk_data_t *);
static size_t       (*sk_data_get_size)(const sk_data_t *);
static void         (*sk_data_unref_fn)(sk_data_t *);
#define SKIA_FMT_JPEG 3
#define SKIA_FMT_PNG  4

static int skia_load_encode(void)
{
    if (!g_skia) { if (!skia_load()) return 0; }
    if (!sk_image_new_raster_copy) {
        sk_image_new_raster_copy = (sk_image_t *(*)(const sk_imageinfo_t *, const void *, size_t))dlsym(g_skia, "sk_image_new_raster_copy");
        sk_image_encode_specific = (sk_data_t *(*)(const sk_image_t *, int, int))dlsym(g_skia, "sk_image_encode_specific");
        sk_data_get_data = (const void *(*)(const sk_data_t *))dlsym(g_skia, "sk_data_get_data");
        sk_data_get_size = (size_t (*)(const sk_data_t *))dlsym(g_skia, "sk_data_get_size");
        sk_data_unref_fn = (void (*)(sk_data_t *))dlsym(g_skia, "sk_data_unref");
    }
    if (!sk_image_new_raster_copy || !sk_image_encode_specific || !sk_data_get_data || !sk_data_get_size) {
        if (getenv("WPF_LINUX_WIC_TRACE")) fprintf(stderr, "WIC_TRACE SKIA_ENCODE missing\n");
        return 0;
    }
    return 1;
}

/* 编码出的 PNG 里注入 pHYs（SetResolution）与 tEXt（元数据写面）——Skia 不写这些 chunk */
static int png_inject(wic_obj *enc, const unsigned char *src, size_t n,
                      unsigned char **out, size_t *outn)
{
    size_t cap = n + 4096 + (size_t)enc->meta_count * 512;
    unsigned char *buf = (unsigned char *)malloc(cap);
    if (!buf) return 0;
    size_t o = 0;
    if (n < 8 || !is_png_bytes(src, n)) { free(buf); return 0; }
    memcpy(buf, src, 8); o = 8;
    size_t i = 8; int injected = 0;
    while (i + 12 <= n) {
        uint32_t ln = be32(src + i);
        if (i + 12 + (size_t)ln > n) break;
        const unsigned char *type = src + i + 4;
        int is_idat = (memcmp(type, "IDAT", 4) == 0);
        if (is_idat && !injected) {
            injected = 1;
            if (enc->has_dpi && enc->dpiX > 0) {          /* pHYs：像素/米，unit=1 */
                unsigned char data[9]; uint32_t x = (uint32_t)(enc->dpiX / 0.0254 + 0.5), y = (uint32_t)(enc->dpiY / 0.0254 + 0.5);
                put_be32(data, x); put_be32(data + 4, y); data[8] = 1;
                unsigned char *q = buf + o;
                put_be32(q, 9); memcpy(q + 4, "pHYs", 4); memcpy(q + 8, data, 9);
                uint32_t c = crc32_bytes(q + 4, 13); put_be32(q + 17, c); o += 21;
            }
            for (int m = 0; m < enc->meta_count; m++) {   /* tEXt: keyword\0text */
                size_t kl = strlen(enc->meta_keys[m]), vl = strlen(enc->meta_vals[m]);
                size_t dl = kl + 1 + vl;
                if (o + 12 + dl + 512 > cap) break;
                unsigned char *q = buf + o;
                put_be32(q, (uint32_t)dl); memcpy(q + 4, "tEXt", 4);
                memcpy(q + 8, enc->meta_keys[m], kl); q[8 + kl] = 0; memcpy(q + 8 + kl + 1, enc->meta_vals[m], vl);
                uint32_t c = crc32_bytes(q + 4, 4 + dl); put_be32(q + 8 + dl, c);
                o += 12 + dl;
            }
        }
        size_t total = 12 + (size_t)ln;
        if (o + total > cap) { free(buf); return 0; }
        memcpy(buf + o, src + i, total); o += total;
        if (memcmp(type, "IEND", 4) == 0) { i += total; break; }
        i += total;
    }
    *out = buf; *outn = o;
    return 1;
}

/* ------------------------------------------------------------------ GUID 常量 */
/* WIC 的 GUID 用托管 {Data1(u32), Data2(u16), Data3(u16), Data4[8]} 顺序（小端落内存），
 * 这里按 C 结构体逐字段赋值，与托管侧 ref Guid 的布局一致。 */
typedef struct { uint32_t d1; uint16_t d2, d3; uint8_t d4[8]; } guid_t;

/* ⚠ 取值一律抄上游 `Common/Graphics/wgx_exports.cs`（PC/MIL 两侧同一份）：
 *   0x1b7cfaf4-713f-473c-bbcd-6137425faeaf = GUID_ContainerFormatPng  (:334)
 *   0x19e4a5aa-5662-4fc5-a0c0-1758028e1057 = GUID_ContainerFormatJpeg (:332)
 * 我原先用的 {0xb96b3caa…} 是**另一个** GUID：读路径的 GetContainerFormat 因此也一直报错值，
 * 直到写面 CreateEncoder 比对容器 GUID 才暴露出来。*/
#define GUID_PNG    {0x1b7cfaf4,0x713f,0x473c,{0xbb,0xcd,0x61,0x37,0x42,0x5f,0xae,0xaf}}
#define GUID_JPEG   {0x19e4a5aa,0x5662,0x4fc5,{0xa0,0xc0,0x17,0x58,0x02,0x8e,0x10,0x57}}
#define GUID_BMP    {0xb96b3cab,0x0728,0x11d3,{0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e}}
#define GUID_GIF    {0xb96b3cb0,0x0728,0x11d3,{0x9d,0x7b,0x00,0x00,0xf8,0x1e,0xf3,0x2e}}
#define GUID_TIFF   {0x163bcc30,0xe2e9,0x4f0b,{0x96,0x1d,0xa3,0xe9,0xfd,0xb7,0x88,0xa3}}
#define GUID_32BPPBGRA {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f}}
/* 32bppPBGRA（预乘）—— 本文件另有两处内联字面量（FrameEncode_SetPixelFormat / 编码预乘判定），
 * 真值同源：Common/Graphics/wgx_exports.cs:231。这里只为 CreateBitmap 收字节序用。*/
#define GUID_32BPPPBGRA {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}}

static guid_t guid_png(void)  { guid_t g = GUID_PNG;  return g; }
static guid_t guid_jpeg(void) { guid_t g = GUID_JPEG; return g; }
static guid_t guid_bmp(void)  { guid_t g = GUID_BMP;  return g; }
static guid_t guid_gif(void)  { guid_t g = GUID_GIF;  return g; }
static guid_t guid_tiff(void) { guid_t g = GUID_TIFF; return g; }

/* 从文件头嗅探容器格式（不解码；给 GetContainerFormat / GetDecoderInfo 用） */
static guid_t sniff_container(const unsigned char *b, size_t n)
{
    if (n >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return guid_png();
    if (n >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return guid_jpeg();
    if (n >= 2 && b[0] == 'B' && b[1] == 'M') return guid_bmp();
    if (n >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return guid_gif();
    if (n >= 4 && ((b[0]=='I'&&b[1]=='I'&&b[2]==0x2A) || (b[0]=='M'&&b[1]=='M'&&b[2]==0x00&&b[3]==0x2A)))
        return guid_tiff();
    guid_t none; memset(&none, 0, sizeof none); return none;
}

/* ------------------------------------------------------------------ 导出：IMG 工厂 */
__attribute__((visibility("default")))
int32_t WICCreateImagingFactory_Proxy(uint32_t sdkVersion, void **ppICodecFactory)
{
    (void)sdkVersion;
    if (!ppICodecFactory) return E_INVALIDARG;
    intptr_t h = obj_new(KIND_FACTORY);
    if (!h) return E_OUTOFMEMORY;
    *ppICodecFactory = (void *)h;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateDecoderFromFileHandle_Proxy(void *factory, intptr_t hFile,
                                                             guid_t *vendor, uint32_t metadataFlags,
                                                             void **ppIDecode)
{
    (void)metadataFlags; (void)vendor;
    if (!obj_get((intptr_t)factory) || !ppIDecode) return E_INVALIDARG;
    if (hFile <= 0) return E_INVALIDARG;

    int fd = dup((int)hFile);          /* 复制一份，shim 自己管理生命周期，不动调用方的 fd */
    if (fd < 0) return E_INVALIDARG;

    g_calls[0]++;
    intptr_t h = obj_new(KIND_DECODER);
    if (!h) { close(fd); return E_OUTOFMEMORY; }

    wic_obj *o = obj_get(h);
    o->fd = fd;

    int hr = decode_open(o);            /* 打开解码器 + 读尺寸（不是懒的：GetSize 马上要用） */
    if (hr != S_OK) { obj_drop(h, o); return hr; }

    *ppIDecode = (void *)h;
    return S_OK;
}

/* T2 补：**流路径**的解码入口（上游 `BitmapDecoder` 走 `GetIStreamFromStream` → 本入口）。
 *
 * 为什么必须补：上游 `BitmapDecoder.cs:1145` 有两个分支 —— 有 `SafeFileHandle` 走
 *   `CreateDecoderFromFileHandle`，**否则**走 `CreateDecoderFromStream`。本 shim 此前只实现了
 *   前者 ⇒ file:// 能跑，而 `pack://` 资源流（XAML 里 `Source="...png"`、`Icon="...ico"`）
 *   一路撞 `EntryPointNotFoundException`（实测：第三方应用 HandyControl 示例工程主窗口）。
 *
 * 实参形态：这里的 pIStream **不是外来 COM 对象**，而是**我们自己**的 KIND_STREAM 句柄 ——
 *   上游 `StreamAsIStream.IStreamFrom(IntPtr memoryBuffer, int cb)` 先调
 *   `WICImagingFactory.CreateStream` 再 `IWICStream::InitializeFromMemory`。
 *   ⇒ 直接取它的 `stream_buf/stream_len`，**复制**一份字节给解码器（流对象的生命周期归调用方，
 *   解码器不许持有它的指针），再走与 file 路径**同一个** `decode_open_bytes`
 *   —— 不新增第二条解码实现，尺寸/格式/像素语义因此与 file 路径逐位一致。*/
__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateDecoderFromStream_Proxy(void *factory, void *pIStream,
                                                         guid_t *vendor, uint32_t metadataFlags,
                                                         void **ppIDecode)
{
    (void)metadataFlags; (void)vendor;
    if (!obj_get((intptr_t)factory) || !ppIDecode) return E_INVALIDARG;

    wic_obj *st = obj_get((intptr_t)pIStream);
    if (!st || st->kind != KIND_STREAM || !st->stream_buf || st->stream_len == 0) return E_INVALIDARG;

    g_calls[1]++;   /* 记到 Stream 槽：语义与 CreateStream 一致（"这次走的是视频/图像流路径"）*/

    if (getenv("WPF_LINUX_WIC_TRACE")) {
        unsigned iw = 0, ih = 0;
        if (st->stream_len >= 24 && st->stream_buf[0] == 0x89 && st->stream_buf[1] == 'P') {
            iw = ((unsigned)st->stream_buf[16] << 24) | ((unsigned)st->stream_buf[17] << 16) |
                 ((unsigned)st->stream_buf[18] << 8) | st->stream_buf[19];
            ih = ((unsigned)st->stream_buf[20] << 24) | ((unsigned)st->stream_buf[21] << 16) |
                 ((unsigned)st->stream_buf[22] << 8) | st->stream_buf[23];
        }
        fprintf(stderr, "WIC_TRACE CDS len=%zu magic=%02X%02X%02X%02X%02X%02X%02X%02X ihdr=%ux%u\n",
                st->stream_len,
                st->stream_len>0?st->stream_buf[0]:0, st->stream_len>1?st->stream_buf[1]:0,
                st->stream_len>2?st->stream_buf[2]:0, st->stream_len>3?st->stream_buf[3]:0,
                st->stream_len>4?st->stream_buf[4]:0, st->stream_len>5?st->stream_buf[5]:0,
                st->stream_len>6?st->stream_buf[6]:0, st->stream_len>7?st->stream_buf[7]:0,
                iw, ih);
    }

    intptr_t h = obj_new(KIND_DECODER);
    if (!h) return E_OUTOFMEMORY;

    wic_obj *o = obj_get(h);
    o->bytes = (unsigned char *)malloc(st->stream_len);
    if (!o->bytes) { obj_drop(h, o); return E_OUTOFMEMORY; }
    memcpy(o->bytes, st->stream_buf, st->stream_len);
    o->size = st->stream_len;

    int hr = decode_open_bytes(o);
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE CDS_HR hr=0x%08X w=%d h=%d\n", (unsigned)hr, o->width, o->height);
    if (hr != S_OK) { obj_drop(h, o); return hr; }

    *ppIDecode = (void *)h;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateFormatConverter_Proxy(void *factory, void **ppFormatConverter)
{
    if (!obj_get((intptr_t)factory) || !ppFormatConverter) return E_INVALIDARG;
    intptr_t h = obj_new(KIND_CONVERTER);
    if (!h) return E_OUTOFMEMORY;
    *ppFormatConverter = (void *)h;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateComponentInfo_Proxy(void *factory, guid_t *clsid, void **ppIComponentInfo)
{
    (void)clsid;
    if (!obj_get((intptr_t)factory) || !ppIComponentInfo) return E_INVALIDARG;
    intptr_t h = obj_new(KIND_CODECINFO);
    if (!h) return E_OUTOFMEMORY;
    *ppIComponentInfo = (void *)h;
    return S_OK;
}

/* ---------------------------------------------------------------------------
 * IWICImagingFactory::CreateBitmapFromMemory —— **真实现**（不是桩）
 *
 * 为什么必须有：`CachedBitmap.InitFromMemoryPtr`（CachedBitmap.cs:365-372）走的就是它，
 * 而 WPF 的 `CacheOption=OnLoad` / `RecoverFromDecodeFailure` 两条路都从这里建缓存位图。
 * 语义：把调用方缓冲**拷一份**（我们不持有调用方内存），像素格式**原样回报**——
 * 不做 BGRA 强转（那是 FormatConverter 的活），这样 CopyPixels 出去的就是同一份字节。 */
__attribute__((visibility("default")))
/* ---------------------------------------------------------------------------
 * IWICImagingFactory::CreateBitmap —— **真实现**（原来只是 NOT_IMPL 桩）
 *
 * 调用点（实测）：`RenderTargetBitmap..ctor` —— `MS.Internal.AppModel.IconHelper.GenerateBitmapSource`
 *   先要一张**空白离屏位图**，随后 MIL 往里面画（窗口图标、缩略图、任何"渲染到内存"都用它）。
 *   第三方应用 HandyControl 示例工程的窗口 `Icon=` 一走到这里就炸：
 *   `COMException 0x80070006 (E_HANDLE)`（桩返回 NOTIMPLEMENTED，上游 HRESULT.Check 换成 E_HANDLE 文案）。
 * 语义：新建 width×height、**零填充**、像素缓冲归 shim 所有的位图；像素格式 GUID **原样**记进
 *   `memFormat`（与 `CreateBitmapFromMemory` 同族，只是不拷贝来源）。
 *   `IWICBitmap::Lock` / `CopyPixels` / `GetSize` / `GetPixelFormat` 都会走既有 mem 位图路径。
 * 口径（明说，不沉默扩权）：本 shim 一律以 32bpp 交付 ⇒ **只接受** BGRA 与 PBGRA 两种 GUID；
 *   其它格式诚实返回 `WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT`，不"收下再按 BGRA 编"。
 *   `RenderTargetBitmap` 默认就是 `PixelFormats.Pbgra32`，两条都要收。
 * --------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateBitmap_Proxy(void *factory, uint32_t width, uint32_t height,
                                             guid_t *pguidPixelFormat, int32_t options,
                                             void **ppIBitmap)
{
    (void)options;
    if (!obj_get((intptr_t)factory) || !ppIBitmap || !pguidPixelFormat) return E_INVALIDARG;
    if (width == 0 || height == 0) return E_INVALIDARG;

    guid_t bgra = (guid_t)GUID_32BPPBGRA, pbg = (guid_t)GUID_32BPPPBGRA;
    int is_premul = (memcmp(pguidPixelFormat, &pbg, 16) == 0);
    if (!is_premul && memcmp(pguidPixelFormat, &bgra, 16) != 0)
        return WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;

    intptr_t h = obj_new(KIND_FRAME);        /* 带像素的源对象（frame 即源）*/
    if (!h) return E_OUTOFMEMORY;

    wic_obj *o = obj_get(h);
    size_t bytes = (size_t)width * 4u * (size_t)height;
    o->pixels = (unsigned char *)calloc(1, bytes);   /* **零填充**：新建位图必须是确定内容 */
    if (!o->pixels) { obj_drop(h, o); return E_OUTOFMEMORY; }
    o->rowBytes = (size_t)width * 4u;
    o->width = (int32_t)width; o->height = (int32_t)height;
    o->colorType = 6; o->alphaType = 3;      /* Skia BGRA / unpremul —— 仅诊断用 */
    o->decoded = 1;
    o->is_pbgra = is_premul;                 /* 预乘标记：编码/交付时要反解，否则半透明偏暗 */
    o->has_memFormat = 1; memcpy(o->memFormat, pguidPixelFormat, 16);
    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
        fprintf(stderr, "WIC_TRACE CREATE_BITMAP h=%lld %ux%u premul=%d\n",
                (long long)h, width, height, is_premul);
    *ppIBitmap = (void *)h;
    return S_OK;
}

int32_t IWICImagingFactory_CreateBitmapFromMemory_Proxy(
        void *factory, uint32_t width, uint32_t height, guid_t *pguidPixelFormat,
        uint32_t cbStride, uint32_t cbBufferSize, unsigned char *pbBuffer, void **ppIBitmap)
{
    if (!obj_get((intptr_t)factory) || !ppIBitmap || !pguidPixelFormat || !pbBuffer) return E_INVALIDARG;
    if (width == 0 || height == 0) return E_INVALIDARG;
    if (cbStride < width * 4u) return E_INVALIDARG;                 /* 我们只接受 ≥4 字节/像素 */
    if ((uint64_t)cbStride * height > cbBufferSize) return E_INVALIDARG;

    intptr_t h = obj_new(KIND_FRAME);        /* 带像素的源对象（frame 即源）*/
    if (!h) return E_OUTOFMEMORY;

    wic_obj *o = obj_get(h);
    size_t bytes = (size_t)cbStride * height;
    o->pixels = (unsigned char *)malloc(bytes);
    if (!o->pixels) { obj_drop(h, o); return E_OUTOFMEMORY; }
    memcpy(o->pixels, pbBuffer, bytes);
    o->rowBytes = cbStride;
    o->width = (int32_t)width; o->height = (int32_t)height;
    o->colorType = 6; o->alphaType = 3;      /* Skia BGRA / unpremul —— 仅诊断用 */
    o->decoded = 1;
    o->has_memFormat = 1; memcpy(o->memFormat, pguidPixelFormat, 16);
    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0) fprintf(stderr, "WIC_TRACE CREATE_BITMAP_FROM_MEMORY h=%lld %ux%u stride=%u\n", (long long)h, width, height, cbStride);
    *ppIBitmap = (void *)h;
    return S_OK;
}

/* ---------------------------------------------------------------------------
 * IWICImagingFactory::CreateBitmapFromSource —— **真实现**（原来只在桩清单里）
 *
 * 调用点：`BitmapSource.CreateCachedBitmap` / `CachedBitmap` 族（CacheOption=OnLoad 必走）。
 * 语义：**立即物化**——把源解码成像素后拷一份进新对象。
 *   · 对 `WICBitmapCacheOnLoad` 这正是上游语义；
 *   · 对 `WICBitmapCacheOnDemand` 只是提前缓存（结果相同，多占一份内存）——登记为已知偏差。
 * 不做"共享 pixels / 透传 fd"：源可能是内存位图（无 fd），共享 pixels 会双重释放。
 * --------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateBitmapFromSource_Proxy(void *factory, void *pISource,
                                                       int32_t cacheOption, void **ppIBitmap)
{
    (void)cacheOption;
    if (!obj_get((intptr_t)factory) || !ppIBitmap) return E_INVALIDARG;

    wic_obj *src = as_source(pISource);
    if (!src) return E_INVALIDARG;

    int hr = decode_open(src);
    if (hr != S_OK) return hr;
    hr = decode_pixels(src);
    if (hr != S_OK) return hr;
    if (!src->pixels) return E_UNEXPECTED;

    intptr_t h = obj_new(KIND_CONVERTER);
    if (!h) return E_OUTOFMEMORY;

    wic_obj *o = obj_get(h);
    size_t bytes = src->rowBytes * (size_t)src->height;
    o->pixels = (unsigned char *)malloc(bytes);
    if (!o->pixels) { obj_drop(h, o); return E_OUTOFMEMORY; }
    memcpy(o->pixels, src->pixels, bytes);
    o->rowBytes = src->rowBytes;
    o->width = src->width; o->height = src->height;
    o->colorType = src->colorType; o->alphaType = src->alphaType;
    o->decoded = 1; o->has_memFormat = 1;
    guid_t bgra = (guid_t)GUID_32BPPBGRA;      /* 我们交付的就是 Bgra32 */
    memcpy(o->memFormat, &bgra, 16);
    if (src->has_dpi) { o->dpiX = src->dpiX; o->dpiY = src->dpiY; o->has_dpi = 1; }
    else { double dx, dy; if (meta_resolution(src, &dx, &dy)) { o->dpiX = dx; o->dpiY = dy; o->has_dpi = 1; } }
    *ppIBitmap = (void *)h;
    return S_OK;
}

/* IWICBitmap::SetResolution —— 新增导出（原先**根本没有**这个符号：
 * PC 建缓存位图时会调，缺了就 EntryPointNotFoundException）。记下来供 GetResolution 回报。*/
__attribute__((visibility("default")))
int32_t IWICBitmap_SetResolution_Proxy(void *bitmap, double dpiX, double dpiY)
{
    wic_obj *o = as_source(bitmap);
    if (!o) return E_INVALIDARG;
    o->dpiX = dpiX; o->dpiY = dpiY; o->has_dpi = 1;
    return S_OK;
}

/* ---------------------------------------------------------------------------
 * IWICImagingFactory::CreateBitmapClipper ＋ IWICBitmapClipper::Initialize —— **真实现**
 * （原先 `CreateBitmapClipper` 是 NOT_IMPL 桩；`IWICBitmapClipper_Initialize_Proxy`
 *   **根本没有这个导出** ⇒ 桩时代连下一步都走不到。）
 *
 * 调用点（已确证）：`CroppedBitmap.FinalizeCreation`（PresentationCore）
 *     CreateBitmapClipper(factory, out clipper) → Initialize(clipper, source.WicSourceHandle, rect)
 *   终点是"雪碧图逐帧"这类用法：`new CroppedBitmap(_source, CalDisplayRect())`。
 *   桩时代的症状：`new CroppedBitmap(...)` 抛 `NotImplementedException`
 *   （0x80004001 = WINCODEC_ERR_NOTIMPLEMENTED → PC 的 ConvertHRToException）。
 *
 * 语义（WIC 契约）：
 *   · `CreateBitmapClipper` 只造一个空对象；**初始化在 Initialize**，且允许重复调用 = 重置裁剪区。
 *   · `prc == NULL` ⇒ 整幅；`w/h == 0` ⇒ 取到右/下边界（与本文件 `IWICBitmapSource_CopyPixels_Proxy`
 *     既有的"0 = 全额"口径保持一致）。
 *   · **负偏移 / 零尺寸 / 越界 ⇒ E_INVALIDARG（如实拒绝）**：不越界读、不静默截断、不补零。
 *   · 像素格式与预乘标记**继承源**（本 shim 一律 32bpp 交付：BGRA 或 PBGRA）。
 *
 * 实现选择（明说）：**在 Initialize 时物化一份裁剪后的像素**，而非"惰性视图 + 每次 CopyPixels 算偏移"。
 *   理由：① 复用既有 mem 位图路径 ⇒ `GetSize / GetPixelFormat / GetResolution / CopyPixels`
 *   这四个 `IWICBitmapSource_*` 代理**一行都不用改**（`has_memFormat + pixels` 即可自洽）；
 *   ② 源只解一次（`decode_pixels` 命中缓存），之后每个 clipper 只做一次 memcpy
 *   ⇒ 雪碧图逐帧**不重复解码**（这是本任务点名的要求）；
 *   ③ 不把 stride/偏移的复杂度摊到热路径上。
 *   代价：每个 clipper 多占 rw×rh×4 字节（WIC 真实现是惰性视图）——登记为已知偏差。
 *   未初始化就取尺寸/像素：`decode_open` 走不到（无 memFormat/无 fd/无字节）⇒ 返回 E_UNEXPECTED
 *   （WIC 真值是 WINCODEC_ERR_WRONGSTATE；偏差已登记，不崩、不伪造数据）。
 * --------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int32_t IWICImagingFactory_CreateBitmapClipper_Proxy(void *factory, void **ppIBitmapClipper)
{
    if (!obj_get((intptr_t)factory) || !ppIBitmapClipper) return E_INVALIDARG;
    *ppIBitmapClipper = NULL;

    intptr_t h = obj_new(KIND_CLIPPER);
    if (!h) return E_OUTOFMEMORY;
    *ppIBitmapClipper = (void *)h;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapClipper_Initialize_Proxy(void *clipper, void *pISource, wic_rect *prc)
{
    wic_obj *c = obj_get((intptr_t)clipper);
    if (!c || c->kind != KIND_CLIPPER) return E_INVALIDARG;

    wic_obj *src = as_source(pISource);
    if (!src || src == c) return E_INVALIDARG;         /* 拿自己当源：无意义，如实拒绝 */

    int hr = decode_open(src);
    if (hr != S_OK) return hr;
    if (src->is_foreign && !src->pixels)
        return WINCODEC_ERR_UNSUPPORTEDOPERATION;      /* 外来源未借像素：与 CopyPixels 同口径，诚实失败 */
    hr = decode_pixels(src);
    if (hr != S_OK) return hr;
    if (!src->pixels || src->width <= 0 || src->height <= 0) return E_UNEXPECTED;

    /* ---- rect 归一 + 边界校验（越界报错，绝不越界读）---- */
    int32_t rx = prc ? prc->x : 0;
    int32_t ry = prc ? prc->y : 0;
    int32_t rw = (prc && prc->w > 0) ? prc->w : src->width - rx;
    int32_t rh = (prc && prc->h > 0) ? prc->h : src->height - ry;
    if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 ||
        (int64_t)rx + (int64_t)rw > (int64_t)src->width ||
        (int64_t)ry + (int64_t)rh > (int64_t)src->height) {
        if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
            fprintf(stderr, "WIC_TRACE CLIPPER_REFUSE reason=invalid-rect rect=(%d,%d,%dx%d) src=%dx%d\n",
                    rx, ry, rw, rh, src->width, src->height);
        return E_INVALIDARG;
    }

    /* 交付一律 32bpp；行宽口径与 `IWICBitmapSource_CopyPixels_Proxy` 一致（rowBytes / width） */
    size_t bpp = (size_t)(src->rowBytes / (size_t)src->width);
    if (bpp == 0) bpp = 4u;
    size_t row = (size_t)rw * bpp;
    size_t need = row * (size_t)rh;

    unsigned char *buf = (unsigned char *)malloc(need ? need : 1);
    if (!buf) return E_OUTOFMEMORY;
    for (int32_t j = 0; j < rh; j++)
        memcpy(buf + (size_t)j * row,
               src->pixels + (size_t)(ry + j) * src->rowBytes + (size_t)rx * bpp,
               row);

    /* ---- 提交（重复 Initialize = 重置：先放掉上一轮缓冲）---- */
    if (c->pixels && !c->pixels_borrowed) free(c->pixels);
    c->pixels = buf;
    c->rowBytes = row;
    c->width = rw; c->height = rh;
    c->colorType = src->colorType; c->alphaType = src->alphaType;
    c->decoded = 1;
    c->has_memFormat = 1;
    if (src->has_memFormat) memcpy(c->memFormat, src->memFormat, 16);
    else { guid_t bgra = (guid_t)GUID_32BPPBGRA; memcpy(c->memFormat, &bgra, 16); }
    c->is_pbgra = src->is_pbgra;
    if (src->has_dpi) { c->dpiX = src->dpiX; c->dpiY = src->dpiY; c->has_dpi = 1; }
    else { double dx, dy; if (meta_resolution(src, &dx, &dy)) { c->dpiX = dx; c->dpiY = dy; c->has_dpi = 1; } }
    c->parent_h = (intptr_t)pISource;   /* 仅诊断：本 kind 不使用 parent_h 的其它语义 */

    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
        fprintf(stderr, "WIC_TRACE CLIPPER h=%lld src=%lld rect=(%d,%d,%dx%d) dst=%zux%zu premul=%d\n",
                (long long)(intptr_t)clipper, (long long)(intptr_t)pISource, rx, ry, rw, rh, row, need, c->is_pbgra);
    return S_OK;
}

/* ------------------------------------------------------------------ 导出：decoder */

/* 真实帧数。**单一取数点**：`sk_codec_get_frame_count`（幂等，结果缓存在 o->frame_count）。
 * 三条具名降级（都不是静默）：① Skia 没这个符号 ② 返回 <= 0（该 codec 不报帧数）
 * ⇒ 一律按"静态图 1 帧"并打 `FRAME_COUNT_NOINFO` 台账；③ 解码器开不出来 ⇒ 返回那个 hr。
 * 【`#50` 修法核心】原实现恒 `*pFrameCount = 1;` ⇒ 多帧图（GIF）只出第 0 帧。*/
static int32_t decode_frame_count(wic_obj *o)
{
    if (o->frame_count > 0) return o->frame_count;

    int32_t n = 1;
    if (o->codec && sk_codec_get_frame_count) {
        int fc = sk_codec_get_frame_count((sk_codec_t *)o->codec);
        if (fc > 1) n = (int32_t)fc;
        else if (fc <= 0 && getenv("WPF_LINUX_WIC_TRACE"))
            fprintf(stderr, "WIC_TRACE FRAME_COUNT_NOINFO reason=codec-reports-no-frame-info fc=%d -> 1\n", fc);
    } else if (getenv("WPF_LINUX_WIC_TRACE")) {
        fprintf(stderr, "WIC_TRACE FRAME_COUNT_NOINFO reason=libSkiaSharp-missing-sk_codec_get_frame_count -> 1\n");
    }
    o->frame_count = n;
    return n;
}

__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetFrameCount_Proxy(void *decoder, uint32_t *pFrameCount)
{
    wic_obj *o = obj_get((intptr_t)decoder);
    if (!o || o->kind != KIND_DECODER || !pFrameCount) return E_INVALIDARG;

    int hr = decode_open(o);
    if (hr != S_OK) return hr;            /* 开不出解码器 ⇒ 如实报错（PC 只在解码器建成功后调这里）*/

    int32_t n = decode_frame_count(o);
    *pFrameCount = (uint32_t)n;
    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
        fprintf(stderr, "WIC_TRACE GET_FRAME_COUNT h=%lld -> %d\n", (long long)(intptr_t)decoder, n);
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetFrame_Proxy(void *decoder, uint32_t index, void **ppIFrameDecode)
{
    wic_obj *o = obj_get((intptr_t)decoder);
    if (!o || o->kind != KIND_DECODER || !ppIFrameDecode) return E_INVALIDARG;

    int hr = decode_open(o);
    if (hr != S_OK) return hr;

    int32_t n = decode_frame_count(o);
    if (index >= (uint32_t)n) {
        /* 越界帧**如实失败**（不改判据、也不返回一个"看起来能用"的帧）。
         * 上游 wgx_error.cs 里没有 FRAMEMISSING 这一类 ⇒ 沿用本 shim 既有的 E_INVALIDARG。*/
        if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
            fprintf(stderr, "WIC_TRACE GET_FRAME_REFUSE index=%u count=%d\n", index, n);
        return E_INVALIDARG;
    }

    intptr_t h = obj_new(KIND_FRAME);
    if (!h) return E_OUTOFMEMORY;

    wic_obj *f = obj_get(h);
    hr = inherit_source_bytes(f, o);   /* frame 自己持一份（fd 或字节），与 decoder 解耦 */
    if (hr != S_OK) { obj_drop(h, f); return hr; }
    f->frame_index = (int32_t)index;   /* ← 逐帧访问的落点：这一帧要解第 index 帧 */
    f->frame_count = n;
    *ppIFrameDecode = (void *)h;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetContainerFormat_Proxy(void *decoder, guid_t *pguidContainerFormat)
{
    wic_obj *o = obj_get((intptr_t)decoder);
    if (!o || o->kind != KIND_DECODER || !pguidContainerFormat) return E_INVALIDARG;

    int hr = decode_open(o);
    if (hr != S_OK) return hr;
    *pguidContainerFormat = sniff_container(o->bytes, o->size);
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetDecoderInfo_Proxy(void *decoder, void **ppIDecoderInfo)
{
    wic_obj *o = obj_get((intptr_t)decoder);
    if (!o || o->kind != KIND_DECODER || !ppIDecoderInfo) return E_INVALIDARG;

    int hr = decode_open(o);
    if (hr != S_OK) return hr;

    intptr_t h = obj_new(KIND_CODECINFO);
    if (!h) return E_OUTOFMEMORY;
    *ppIDecoderInfo = (void *)h;
    return S_OK;
}

/* ------------------------------------------------------------------ 导出：bitmap source（frame 也是 source） */
/* 让"派生态对象"（frame / converter）能自己解码，且与源对象**解耦**。
 *
 * 历史行为是 `f->fd = dup(o->fd)` —— 只在**文件**路径成立。而 `CreateDecoderFromStream`
 * 建出来的 decoder 的 fd 就是 -1 ⇒ `dup(-1)` 之后派生对象既没 fd 又没字节 ⇒
 * `decode_open` 返回 E_UNEXPECTED，表现是上游 `PixelFormat.GetPixelFormat` 抛
 * `COMException 0x8000FFFF`（实测：第三方应用 HandyControl 示例工程的窗口图标 .ico）。
 * ⇒ 有 fd 就照旧 dup；没有 fd（流路径）就把**字节**复制一份过去，语义等价（解耦、各持一份）。*/
static int inherit_source_bytes(wic_obj *dst, wic_obj *src)
{
    if (!dst || !src) return E_INVALIDARG;
    if (src->fd >= 0) { dst->fd = dup(src->fd); return (dst->fd >= 0) ? S_OK : E_UNEXPECTED; }
    if (!src->bytes || src->size == 0) return E_UNEXPECTED;
    dst->bytes = (unsigned char *)malloc(src->size);
    if (!dst->bytes) return E_OUTOFMEMORY;
    memcpy(dst->bytes, src->bytes, src->size);
    dst->size = src->size;
    return S_OK;
}

static wic_obj *as_source(void *handle)
{
    wic_obj *o = obj_get((intptr_t)handle);
    if (!o) o = foreign_lookup((intptr_t)handle);   /* 外来（MIL 所有）句柄：按登记派发 */
    if (!o) return NULL;
    return o;
}

static int32_t source_resolve(wic_obj *o, wic_obj **out)
{
    /* frame 自己解码；converter 目前等价于"源已经解成 BGRA"，故直接返回自身 */
    *out = o;
    return decode_open(o);
}

__attribute__((visibility("default")))
int32_t IWICBitmapSource_GetSize_Proxy(void *source, uint32_t *puiWidth, uint32_t *puiHeight)
{
    wic_obj *o = as_source(source);
    if (!o || !puiWidth || !puiHeight) return E_INVALIDARG;

    wic_obj *s; int hr = source_resolve(o, &s);
    if (hr != S_OK) return hr;
    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0) fprintf(stderr, "WIC_TRACE GET_SIZE h=%lld -> %dx%d (foreign=%d)\n", (long long)(intptr_t)source, s->width, s->height, s->is_foreign);
    *puiWidth = (uint32_t)s->width; *puiHeight = (uint32_t)s->height;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapSource_GetPixelFormat_Proxy(void *source, guid_t *pPixelFormatEnum)
{
    wic_obj *o = as_source(source);
    if (!o || !pPixelFormatEnum) return E_INVALIDARG;

    if (o->has_memFormat) { memcpy(pPixelFormatEnum, o->memFormat, 16); return S_OK; }

    int hr = decode_open(o);
    if (hr != S_OK) {
        if (getenv("WPF_LINUX_WIC_TRACE"))
            fprintf(stderr, "WIC_TRACE FAIL GetPixelFormat kind=%d fd=%d mem=%d -> 0x%08X\n",
                    o->kind, o->fd, o->has_memFormat, (unsigned)hr);
        return hr;
    }
    *pPixelFormatEnum = (guid_t)GUID_32BPPBGRA;   /* 本 shim 一律以 Bgra32 交付 */
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapSource_GetResolution_Proxy(void *source, double *pDpiX, double *pDpiY)
{
    wic_obj *o = as_source(source);
    if (!o || !pDpiX || !pDpiY) return E_INVALIDARG;

    if (o->has_dpi) { *pDpiX = o->dpiX; *pDpiY = o->dpiY; return S_OK; }

    double rx = 0, ry = 0;
    if (meta_resolution(o, &rx, &ry)) {
        *pDpiX = rx; *pDpiY = ry;
        if (getenv("WPF_LINUX_WIC_TRACE"))
            fprintf(stderr, "WIC_TRACE DPI_FROM_FILE %gx%g (kind=%d)\n", rx, ry, o->kind);
        return S_OK;
    }

    int hr = decode_open(o);
    if (hr != S_OK) return hr;
    *pDpiX = 96.0; *pDpiY = 96.0;   /* 文件里没有 pHYs/JFIF ⇒ 退 96（不再是"恒定 96"）*/
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapSource_CopyPixels_Proxy(void *source, wic_rect *prc, uint32_t cbStride,
                                          uint32_t cbBufferSize, void *pvPixels)
{
    wic_obj *o = as_source(source);
    if (!o || !pvPixels) return E_INVALIDARG;
    char prc_txt[48];
    if (prc) snprintf(prc_txt, sizeof prc_txt, "(%d,%d,%dx%d)", prc->x, prc->y, prc->w, prc->h);
    else snprintf(prc_txt, sizeof prc_txt, "null");
    if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0) fprintf(stderr, "WIC_TRACE COPY_PIXELS h=%lld %dx%d foreign=%d prc=%s\n", (long long)(intptr_t)source, o->width, o->height, o->is_foreign, prc_txt);
    if (o->is_foreign && !o->pixels)
        return WINCODEC_ERR_UNSUPPORTEDOPERATION;   /* 外来源未借像素：诚实失败，不编数据 */

    int hr = decode_pixels(o);
    if (hr != S_OK) { if (getenv("WPF_LINUX_WIC_TRACE")) fprintf(stderr, "WIC_TRACE FAIL CopyPixels decode_pixels -> 0x%08X\n", (unsigned)hr); return hr; }

    /* 约定：托管侧对全图取像素（prc 为 0 或等于 (0,0,w,h)）。子矩形会把行首对齐搞复杂，
     * 本轮明确只支持整图 —— 不支持的形态给 UNSUPPORTEDOPERATION，不静默截断。 */
    int full = (!prc) || (prc->x == 0 && prc->y == 0 &&
                          (prc->w == 0 || (uint32_t)prc->w == (uint32_t)o->width) &&
                          (prc->h == 0 || (uint32_t)prc->h == (uint32_t)o->height));
    (void)full;   /* 整图判定保留但不再早退：子矩形由下方统一处理（D-d 修法 A）*/
    /* ---- 子矩形（含上游那处 (0,0,1,1) 的 1x1 探针）：整图路径语义一字不变 ---- */
    if (prc) {
        int32_t rx = prc->x, ry = prc->y;
        int32_t rw = prc->w ? prc->w : o->width;
        int32_t rh = prc->h ? prc->h : o->height;
        size_t bpp = (o->width > 0) ? (o->rowBytes / (size_t)o->width) : 4u;
        if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 ||
            (int64_t)rx + rw > (int64_t)o->width || (int64_t)ry + rh > (int64_t)o->height) {
            if (getenv("WPF_LINUX_WIC_TRACE"))
                fprintf(stderr, "WIC_TRACE COPY_PIXELS_REFUSE reason=invalid-rect prc=%s src=%dx%d\n", prc_txt, o->width, o->height);
            return E_INVALIDARG;   /* 越界/非法：诚实拒绝，不静默截断、不补零 */
        }
        if (!(rx == 0 && ry == 0 && rw == o->width && rh == o->height)) {
            size_t dst = cbStride ? cbStride : o->rowBytes;
            if (cbBufferSize < dst * (size_t)(rh - 1) + (size_t)rw * bpp) return E_INVALIDARG;
            for (int32_t j = 0; j < rh; j++)
                memcpy((unsigned char *)pvPixels + (size_t)j * dst,
                       o->pixels + (size_t)(ry + j) * o->rowBytes + (size_t)rx * bpp,
                       (size_t)rw * bpp);
            if (getenv("WPF_LINUX_WIC_TRACE") && g_trace_budget-- > 0)
                fprintf(stderr, "WIC_TRACE COPY_PIXELS_SUBRECT prc=%s dst=%zu cb=%u\n", prc_txt, dst, cbBufferSize);
            return S_OK;
        }
    }

    size_t need = o->rowBytes * (size_t)o->height;
    if (cbBufferSize < need) return E_INVALIDARG;
    if (cbStride && cbStride != o->rowBytes) {
        /* 调用方给了自己的行距：逐行拷贝 */
        size_t copyRow = cbStride < o->rowBytes ? cbStride : o->rowBytes;
        for (int32_t y = 0; y < o->height; y++)
            memcpy((unsigned char *)pvPixels + (size_t)y * cbStride,
                   o->pixels + (size_t)y * o->rowBytes, copyRow);
    } else {
        memcpy(pvPixels, o->pixels, need);
    }

    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapFrameDecode_GetMetadataQueryReader_Proxy(void *frame, void **ppIQueryReader)
{
    if (ppIQueryReader) *ppIQueryReader = NULL;
    wic_obj *f = obj_get((intptr_t)frame);
    if (!f || !ppIQueryReader) return E_INVALIDARG;
    if (!meta_has_any(f)) return WINCODEC_ERR_UNSUPPORTEDOPERATION;  /* ⚠ PC 容忍这个码（BitmapFrameDecode.cs:631-638）*/

    intptr_t h = obj_new(KIND_META);
    if (!h) return E_OUTOFMEMORY;
    obj_get(h)->parent_h = (intptr_t)frame;
    *ppIQueryReader = (void *)h;
    return S_OK;
}

int32_t IWICBitmapDecoder_GetMetadataQueryReader_Proxy(void *decoder, void **ppIQueryReader)
{
    if (ppIQueryReader) *ppIQueryReader = NULL;
    wic_obj *d = obj_get((intptr_t)decoder);
    if (!d || !ppIQueryReader) return E_INVALIDARG;
    if (!meta_has_any(d)) return WINCODEC_ERR_UNSUPPORTEDOPERATION;

    intptr_t h = obj_new(KIND_META);
    if (!h) return E_OUTOFMEMORY;
    obj_get(h)->parent_h = (intptr_t)decoder;
    *ppIQueryReader = (void *)h;
    return S_OK;
}

/* GetMetadataByName：**同一个导出名**服务两个托管声明
 * （GetMetadataByName(…, ref PROPVARIANT) 与 ContainsMetadataByName(…, IntPtr)）。
 * 传 NULL 就是"存在性查询"（那正是 PC 的 ContainsMetadataByName 用法）。*/
int32_t IWICMetadataQueryReader_GetMetadataByName_Proxy(void *reader, uint16_t *wzName, propvariant_t *propValue)
{
    if (propValue) pv_set_empty(propValue);

    wic_obj *m = obj_get((intptr_t)reader);
    if (!m || m->kind != KIND_META || !wzName) return E_INVALIDARG;
    wic_obj *parent = obj_get(m->parent_h);
    if (!parent) return E_INVALIDARG;

    char q[512]; utf16_to_utf8(wzName, q, sizeof q);
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE METADATA_QUERY \"%s\"%s\n", q, propValue ? "" : " (存在性)");

    int hr = decode_open(parent);
    if (hr != S_OK || !parent->bytes) return WINCODEC_ERR_PROPERTYNOTFOUND;

    uint32_t exifTag = 0; int exifSub = 0;
    if (is_jpeg_bytes(parent->bytes, parent->size) && exif_tag_from_query(q, &exifTag, &exifSub)) {
        char *v = jpeg_exif_text(parent, exifTag, exifSub);
        if (v) { if (propValue) pv_set_lpstr(propValue, v, strlen(v)); free(v); return S_OK; }
        return WINCODEC_ERR_PROPERTYNOTFOUND;
    }

    /* `#50`：GIF 帧的 `/grctlext/Delay`（GCE 延迟，单位 1/100 秒）。
     * 上游口径：PC 的 `BitmapMetadata.GetQuery` 把它读成 `ushort`（PropVariant.cs:18/516）。*/
    if (is_gif_bytes(parent->bytes, parent->size) &&
        (strcasecmp(q, "/grctlext/Delay") == 0 || strcasecmp(q, "grctlext/Delay") == 0)) {
        uint16_t cs = 0;
        int32_t fi = (parent->kind == KIND_FRAME) ? parent->frame_index : 0;
        if (!gif_frame_delay_cs(parent->bytes, parent->size, fi, &cs)) return WINCODEC_ERR_PROPERTYNOTFOUND;
        if (propValue) pv_set_ui2(propValue, cs);
        if (getenv("WPF_LINUX_WIC_TRACE"))
            fprintf(stderr, "WIC_TRACE METADATA_DELAY \"%s\" frame=%d -> %u cs\n", q, fi, (unsigned)cs);
        return S_OK;
    }

    size_t klen = 0;
    const char *key = meta_key_from_query(q, &klen);
    if (key && klen > 0 && is_png_bytes(parent->bytes, parent->size)) {
        char kbuf[128];
        if (klen < sizeof kbuf) {
            memcpy(kbuf, key, klen); kbuf[klen] = 0;
            char *v = png_get_text(parent->bytes, parent->size, kbuf);
            if (v) {
                if (propValue) pv_set_lpstr(propValue, v, strlen(v));
                free(v);
                return S_OK;
            }
        }
    }
    return WINCODEC_ERR_PROPERTYNOTFOUND;   /* 上游 wgx_error.cs:27 → PC 映射 ArgumentException */
}

/* ===========================================================================
 *  短名 ↔ 容器 GUID（PC 的 BitmapMetadata.Format 走 WICMapGuidToShortName；
 *  构造 BitmapMetadata(string) 走 WICMapShortNameToGuid。见 BitmapMetadata.cs:586,899）
 * =========================================================================== */
/* ⚠ 导出名 = PC 的 EntryPoint **原名**：上游这两处是 `EntryPoint = "WICMapShortNameToGuid"` /
 * `"WICMapGuidToShortName"`（**不带 _Proxy**，与其余 IWIC*_Proxy 不同名）——
 * 我先前按 _Proxy 命名，于是 BitmapMetadata.Format 一直报 EntryPointNotFoundException。*/
__attribute__((visibility("default")))
int32_t WICMapShortNameToGuid(uint16_t *wzName, guid_t *pguid);
__attribute__((visibility("default")))
int32_t WICMapGuidToShortName(guid_t *pguid, uint32_t cchName, uint16_t *wzName, uint32_t *pcchActual);

__attribute__((visibility("default")))
int32_t WICMapShortNameToGuid(uint16_t *wzName, guid_t *pguid)
{
    if (!wzName || !pguid) return E_INVALIDARG;
    char name[64]; utf16_to_utf8(wzName, name, sizeof name);
    guid_t g;
    if      (!strcasecmp(name, "PNG"))  g = guid_png();
    else if (!strcasecmp(name, "JPEG") || !strcasecmp(name, "JPG")) g = guid_jpeg();
    else if (!strcasecmp(name, "TIFF")) g = guid_tiff();
    else if (!strcasecmp(name, "BMP"))  g = guid_bmp();
    else if (!strcasecmp(name, "GIF"))  g = guid_gif();
    else return WINCODEC_ERR_COMPONENTNOTFOUND;      /* 上游 wgx_error.cs:36 */
    memcpy(pguid, &g, 16);
    return S_OK;
}

__attribute__((visibility("default")))
int32_t WICMapGuidToShortName(guid_t *pguid, uint32_t cchName, uint16_t *wzName, uint32_t *pcchActual)
{
    if (!pguid || !pcchActual) return E_INVALIDARG;
    const char *name = NULL;
    guid_t g;
    g = guid_png();  if (memcmp(pguid, &g, 16) == 0) name = "PNG";
    if (!name) { g = guid_jpeg(); if (memcmp(pguid, &g, 16) == 0) name = "JPEG"; }
    if (!name) { g = guid_tiff(); if (memcmp(pguid, &g, 16) == 0) name = "TIFF"; }
    if (!name) { g = guid_bmp();  if (memcmp(pguid, &g, 16) == 0) name = "BMP"; }
    if (!name) { g = guid_gif();  if (memcmp(pguid, &g, 16) == 0) name = "GIF"; }
    if (!name) return WINCODEC_ERR_COMPONENTNOTFOUND;

    size_t len = strlen(name);
    *pcchActual = (uint32_t)(len + 1);
    if (cchName >= len + 1 && wzName)
        for (size_t i = 0; i <= len; i++) wzName[i] = (uint16_t)(unsigned char)name[i];
    else if (wzName && cchName > 0) wzName[0] = 0;
    return S_OK;
}

/* ===========================================================================
 *  元数据枚举（BitmapMetadataEnumerator）：句柄化的 IEnumString
 *  PC 声明：IWICMetadataQueryReader_GetEnumerator_Proxy / IEnumString_Next_WIC_Proxy
 *           / IEnumString_Reset_WIC_Proxy（都是 WindowsCodecs → 我们）
 * =========================================================================== */
static void enum_collect(wic_obj *parent, wic_obj *en)
{
    en->enum_count = 0; en->enum_idx = 0;
    if (decode_open(parent) != S_OK || !parent->bytes) return;
    if (is_png_bytes(parent->bytes, parent->size)) {
        size_t i = 8;
        while (i + 12 <= parent->size && en->enum_count < 16) {
            uint32_t ln = be32(parent->bytes + i);
            const unsigned char *type = parent->bytes + i + 4;
            if (i + 12 + (size_t)ln > parent->size) break;
            if (memcmp(type, "tEXt", 4) == 0 || memcmp(type, "iTXt", 4) == 0) {
                const unsigned char *d = parent->bytes + i + 8;
                size_t k = 0; while (k < ln && d[k] != 0) k++;
                if (k > 0 && k < 60) {
                    char key[64]; memcpy(key, d, k); key[k] = 0;
                    snprintf(en->enum_names[en->enum_count], sizeof en->enum_names[0], "/tEXt/{str=%s}", key);
                    en->enum_count++;
                }
            }
            if (memcmp(type, "IEND", 4) == 0) break;
            i += 12 + (size_t)ln;
        }
    } else if (is_jpeg_bytes(parent->bytes, parent->size)) {
        static const uint32_t ifd0_tags[] = { 270, 271, 272, 305, 306, 315, 316, 33432 };
        static const uint32_t exif_tags[] = { 36867, 36868, 42036 };
        for (size_t k = 0; k < sizeof ifd0_tags / sizeof ifd0_tags[0] && en->enum_count < 16; k++) {
            char *v = jpeg_exif_text(parent, ifd0_tags[k], 0);
            if (v) { free(v); snprintf(en->enum_names[en->enum_count], sizeof en->enum_names[0], "/app1/ifd/{ushort=%u}", ifd0_tags[k]); en->enum_count++; }
        }
        for (size_t k = 0; k < sizeof exif_tags / sizeof exif_tags[0] && en->enum_count < 16; k++) {
            char *v = jpeg_exif_text(parent, exif_tags[k], 1);
            if (v) { free(v); snprintf(en->enum_names[en->enum_count], sizeof en->enum_names[0], "/app1/ifd/exif/{ushort=%u}", exif_tags[k]); en->enum_count++; }
        }
    }
}

int32_t IWICMetadataQueryReader_GetEnumerator_Proxy(void *reader, void **ppIEnumString)
{
    if (ppIEnumString) *ppIEnumString = NULL;
    wic_obj *m = obj_get((intptr_t)reader);
    if (!m || m->kind != KIND_META || !ppIEnumString) return E_INVALIDARG;
    wic_obj *parent = obj_get(m->parent_h);
    if (!parent) return E_INVALIDARG;

    intptr_t h = obj_new(KIND_ENUM);
    if (!h) return E_OUTOFMEMORY;
    wic_obj *en = obj_get(h);
    enum_collect(parent, en);
    if (en->enum_count == 0) { obj_drop(h, en); return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
    *ppIEnumString = (void *)h;
    return S_OK;
}

int32_t IEnumString_Next_WIC_Proxy(void *en, int32_t celt, uint16_t **rgElt, int32_t *pceltFetched)
{
    wic_obj *e = obj_get((intptr_t)en);
    if (!e || e->kind != KIND_ENUM || !rgElt) return E_INVALIDARG;
    int n = 0;
    for (; n < celt && e->enum_idx < e->enum_count; n++, e->enum_idx++) {
        const char *nm = e->enum_names[e->enum_idx];
        size_t len = strlen(nm);
        uint16_t *w = (uint16_t *)malloc((len + 1) * sizeof(uint16_t));   /* 调用方按 CoTaskMem 释放 */
        if (!w) break;
        for (size_t i = 0; i <= len; i++) w[i] = (uint16_t)(unsigned char)nm[i];
        rgElt[n] = w;
    }
    if (pceltFetched) *pceltFetched = n;
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE ENUM_NEXT celt=%d got=%d (idx=%d/%d)\n", celt, n, e->enum_idx, e->enum_count);
    return n == celt ? S_OK : 1 /*S_FALSE*/;
}

int32_t IEnumString_Reset_WIC_Proxy(void *en)
{
    wic_obj *e = obj_get((intptr_t)en);
    if (!e || e->kind != KIND_ENUM) return E_INVALIDARG;
    e->enum_idx = 0;
    return S_OK;
}

/* ===========================================================================
 *  导出：编码器写面
 * =========================================================================== */
int32_t IWICImagingFactory_CreateEncoder_Proxy(void *factory, guid_t *guidContainerFormat,
                                               guid_t *guidVendor, void **ppICodec)
{
    (void)guidVendor;
    if (!obj_get((intptr_t)factory) || !ppICodec || !guidContainerFormat) return E_INVALIDARG;
    guid_t png = guid_png(), jpg = guid_jpeg();
    int fmt = 0;
    if (memcmp(guidContainerFormat, &png, sizeof(guid_t)) == 0) fmt = 1;
    else if (memcmp(guidContainerFormat, &jpg, sizeof(guid_t)) == 0) fmt = 2;
    if (!fmt)
        return WINCODEC_ERR_COMPONENTNOTFOUND;   /* 上游 wgx_error.cs:36 = 0x88982f50：没有该容器编码器 */

    intptr_t h = obj_new(KIND_ENCODER);
    if (!h) return E_OUTOFMEMORY;
    wic_obj *e = obj_get(h);
    e->enc_format = fmt; e->quality = 90; e->stream_h = 0;
    *ppICodec = (void *)h;
    return S_OK;
}

int32_t IWICImagingFactory_CreateStream_Proxy(void *factory, void **ppIStream)
{
    if (!obj_get((intptr_t)factory) || !ppIStream) return E_INVALIDARG;
    intptr_t h = obj_new(KIND_STREAM);
    if (!h) return E_OUTOFMEMORY;
    *ppIStream = (void *)h;
    return S_OK;
}

int32_t IWICStream_InitializeFromMemory_Proxy(void *stream, unsigned char *pbBuffer, uint32_t cbBufferSize)
{
    wic_obj *st = obj_get((intptr_t)stream);
    if (!st || st->kind != KIND_STREAM || !pbBuffer) return E_INVALIDARG;
    if (st->stream_len + cbBufferSize > st->stream_cap) {
        unsigned char *nb = (unsigned char *)realloc(st->stream_buf, st->stream_len + cbBufferSize);
        if (!nb) return E_OUTOFMEMORY;
        st->stream_buf = nb; st->stream_cap = st->stream_len + cbBufferSize;
    }
    memcpy(st->stream_buf + st->stream_len, pbBuffer, cbBufferSize);
    st->stream_len += cbBufferSize;
    return S_OK;
}

int32_t IWICBitmapEncoder_Initialize_Proxy(void *encoder, void *pStream, int32_t option)
{
    (void)option;
    wic_obj *e = obj_get((intptr_t)encoder);
    if (!e || e->kind != KIND_ENCODER) return E_INVALIDARG;
    /* ⚠ PC 传进来的是 MILCreateStreamFromStreamDescriptor 造的**句柄表对象**（不是 COM vtable）：
     * 写出去要经 MILIStreamWrite（见 stream_write_bytes 与报告里的跨轨说明）。*/
    e->stream_h = (intptr_t)pStream;
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE ENCODER_INITIALIZE stream=0x%llx\n", (unsigned long long)(uintptr_t)pStream);
    if (!pStream) return E_INVALIDARG;
    return S_OK;
}

int32_t IWICBitmapEncoder_CreateNewFrame_Proxy(void *encoder, void **ppIFrameEncode, void **ppIEncoderOptions)
{
    if (ppIFrameEncode) *ppIFrameEncode = NULL;
    if (ppIEncoderOptions) *ppIEncoderOptions = NULL;
    wic_obj *e = obj_get((intptr_t)encoder);
    if (!e || e->kind != KIND_ENCODER || !ppIFrameEncode) return E_INVALIDARG;

    intptr_t h = obj_new(KIND_FRAMEENC);
    if (!h) return E_OUTOFMEMORY;
    wic_obj *f = obj_get(h);
    f->parent_h = (intptr_t)encoder;
    *ppIFrameEncode = (void *)h;

    if (ppIEncoderOptions) {                       /* 真给一个 options 对象，PC 会往里写质量 */
        intptr_t oh = obj_new(KIND_OPTIONS);
        if (oh) { obj_get(oh)->parent_h = (intptr_t)encoder; *ppIEncoderOptions = (void *)oh; }
    }
    return S_OK;
}

int32_t IPropertyBag2_Write_Proxy(void *bag, uint32_t cProperties, void *propBag, propvariant_t *propValue)
{
    wic_obj *b = obj_get((intptr_t)bag);
    if (!b || b->kind != KIND_OPTIONS || !propBag || !propValue) return E_INVALIDARG;
    /* PROPBAG2 的第一个字段是 pstrName（LPWSTR）。只认 ImageQuality；其余**忽略**（WIC 语义允许）*/
    uint16_t **nameField = (uint16_t **)propBag;
    char name[128]; utf16_to_utf8(nameField[0], name, sizeof name);
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE OPTION_WRITE \"%s\" vt=%u\n", name, (unsigned)propValue->vt);
    if (strstr(name, "Quality") || strstr(name, "quality")) {
        wic_obj *e = obj_get(b->parent_h);
        if (e && e->kind == KIND_ENCODER) {
            int q = (int)propValue->v1;
            if (propValue->vt == 4 /*VT_R4*/) { float fv; uint32_t raw = (uint32_t)propValue->v1; memcpy(&fv, &raw, 4); q = (int)fv; }
            if (q >= 1 && q <= 100) e->quality = q;
        }
    }
    return S_OK;
}

/* IWICBitmapFrameEncode::Initialize(IPropertyBag2*) —— PC 在建帧之后、写像素之前必调它。
 * 我们只需要"接受"：选项已由 IPropertyBag2_Write_Proxy 直接记到编码器上（质量）。*/
/* WICSetEncoderFormat(pSourceIn, pIPalette, pIFrameEncode, out ppSourceOut)
 * 调用点 BitmapEncoder.cs:695。上游由 WindowsCodecs 实现：按源选像素格式、必要时做转换。
 * 我们的读路径**一律以 Bgra32 交付** ⇒ 只需把 Bgra32 告诉帧编码器，源原样返回（无需转换）。*/
int32_t WICSetEncoderFormat_Proxy(void *pSourceIn, void *pIPalette, void *pIFrameEncode, void **ppSourceOut)
{
    if (ppSourceOut) *ppSourceOut = NULL;
    (void)pIPalette;
    wic_obj *src = as_source(pSourceIn);
    wic_obj *fe  = obj_get((intptr_t)pIFrameEncode);
    if (!src || !fe || fe->kind != KIND_FRAMEENC || !ppSourceOut) return E_INVALIDARG;
    int hr = decode_open(src);
    if (hr != S_OK) return hr;

    guid_t bgra = (guid_t)GUID_32BPPBGRA;
    guid_t pbg0 = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
    fe->is_pbgra = (memcmp(fe->memFormat, &pbg0, 16) == 0);   /* 源是预乘 ⇒ 编码前反解 */
    memcpy(fe->memFormat, &bgra, 16); fe->has_memFormat = 1;
    if (fe->width <= 0) { fe->width = src->width; fe->height = src->height; }
    if (fe->rowBytes == 0) fe->rowBytes = (size_t)fe->width * 4u;
    *ppSourceOut = pSourceIn;                  /* 源格式已兼容：不转换 */
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE SET_ENCODER_FORMAT %dx%d → Bgra32（源原样返回）\n", src->width, src->height);
    return S_OK;
}

int32_t IWICBitmapFrameEncode_Initialize_Proxy(void *fe, void *pIEncoderOptions)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC) return E_INVALIDARG;
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE FRAMEENC_INITIALIZE options=0x%llx\n", (unsigned long long)(uintptr_t)pIEncoderOptions);
    return S_OK;
}

int32_t IWICBitmapFrameEncode_SetSize_Proxy(void *fe, int32_t width, int32_t height)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC) return E_INVALIDARG;
    if (width <= 0 || height <= 0) return E_INVALIDARG;
    f->width = width; f->height = height;
    return S_OK;
}

int32_t IWICBitmapFrameEncode_SetPixelFormat_Proxy(void *fe, guid_t *pPixelFormat)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC || !pPixelFormat) return E_INVALIDARG;
    f->has_memFormat = 1; memcpy(f->memFormat, pPixelFormat, 16);   /* 原样记下，编码时说明偏差 */
    {   /* 32bppPBGRA（预乘）真值见 Common/Graphics/wgx_exports.cs:231 → …c910 */
        guid_t pbg = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
        f->is_pbgra = (memcmp(pPixelFormat, &pbg, 16) == 0);
    }
    guid_t bgra = (guid_t)GUID_32BPPBGRA;
    if (memcmp(pPixelFormat, &bgra, 16) != 0 && getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE FRAMEENC_PIXELFORMAT 非 BGRA（按 BGRA 编码，未做预乘反解）\n");
    return S_OK;
}

int32_t IWICBitmapFrameEncode_SetResolution_Proxy(void *fe, double dpiX, double dpiY)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC) return E_INVALIDARG;
    f->dpiX = dpiX; f->dpiY = dpiY; f->has_dpi = 1;
    return S_OK;
}

int32_t IWICBitmapFrameEncode_WritePixels_Proxy(void *fe, uint32_t lineCount, uint32_t cbStride,
                                                uint32_t cbBufferSize, unsigned char *pbPixels)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC || !pbPixels) return E_INVALIDARG;
    if (f->width <= 0 || f->height <= 0) return WINCODEC_ERR_WRONGSTATE_PLACEHOLDER();
    if (f->rowBytes == 0) f->rowBytes = cbStride ? cbStride : (size_t)f->width * 4u;
    if (f->lines_written + (int)lineCount > f->height) return E_INVALIDARG;
    if (cbBufferSize < lineCount * (uint32_t)f->rowBytes) return E_INVALIDARG;

    size_t need = f->rowBytes * (size_t)f->height;
    if (!f->pixels) { f->pixels = (unsigned char *)calloc(1, need); if (!f->pixels) return E_OUTOFMEMORY; }
    memcpy(f->pixels + (size_t)f->lines_written * f->rowBytes, pbPixels, (size_t)lineCount * f->rowBytes);
    f->lines_written += (int)lineCount;
    f->decoded = (f->lines_written >= f->height);
    return S_OK;
}

int32_t IWICBitmapFrameEncode_WriteSource_Proxy(void *fe, void *pIBitmapSource, wic_rect *r)
{
    (void)r;
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC) return E_INVALIDARG;
    wic_obj *src = as_source(pIBitmapSource);
    if (!src) return E_INVALIDARG;
    int hr = decode_open(src); if (hr != S_OK) return hr;
    hr = decode_pixels(src); if (hr != S_OK) return hr;
    if (f->width <= 0) { f->width = src->width; f->height = src->height; }
    if (f->rowBytes == 0) f->rowBytes = src->rowBytes;
    size_t need = f->rowBytes * (size_t)f->height;
    f->pixels = (unsigned char *)malloc(need);
    if (!f->pixels) return E_OUTOFMEMORY;
    if (src->rowBytes == f->rowBytes && src->height == f->height) memcpy(f->pixels, src->pixels, need);
    else {
        size_t copyRow = src->rowBytes < f->rowBytes ? src->rowBytes : f->rowBytes;
        for (int32_t y = 0; y < f->height && y < src->height; y++)
            memcpy(f->pixels + (size_t)y * f->rowBytes, src->pixels + (size_t)y * src->rowBytes, copyRow);
    }
    {   /* 源本身是预乘（32bppPBGRA）⇒ 让编码前反解；PBGRA GUID 真值见 Common/Graphics/wgx_exports.cs:231 */
        guid_t _pbg = {0x6fddc324,0x4e03,0x4bfe,{0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x10}};
        if (src->has_memFormat && memcmp(src->memFormat, &_pbg, 16) == 0) f->is_pbgra = 1;
    }
    if (!f->has_dpi && src->has_dpi) { f->dpiX = src->dpiX; f->dpiY = src->dpiY; f->has_dpi = 1; }
    if (!f->has_dpi) { double dx, dy; if (meta_resolution(src, &dx, &dy)) { f->dpiX = dx; f->dpiY = dy; f->has_dpi = 1; } }
    f->decoded = 1; f->lines_written = f->height;
    return S_OK;
}

int32_t IWICBitmapFrameEncode_Commit_Proxy(void *fe)
{
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC) return E_INVALIDARG;
    if (!f->pixels) return WINCODEC_ERR_WRONGSTATE_PLACEHOLDER();   /* 没写像素就提交：状态非法 */
    return S_OK;
}

int32_t IWICBitmapEncoder_Commit_Proxy(void *encoder)
{
    wic_obj *e = obj_get((intptr_t)encoder);
    if (!e || e->kind != KIND_ENCODER) return E_INVALIDARG;

    /* 找本编码器下已提交的帧 */
    wic_obj *frame = NULL;
    for (int i = 0; i < WIC_OBJ_MAX; i++)
        if (g_objs[i] && g_objs[i]->kind == KIND_FRAMEENC && g_objs[i]->parent_h == (intptr_t)encoder) frame = g_objs[i];
    if (!frame || !frame->pixels) return WINCODEC_ERR_WRONGSTATE_PLACEHOLDER();

    if (!skia_load_encode()) return WINCODEC_ERR_GENERIC_ERROR;

    /* 预乘源（PBGRA）必须先反解成直通 alpha，否则半透明像素会偏暗/偏色。
     * 反解公式：C = min(255, (C_pre * 255 + a/2) / a)，a==0 → 0。*/
    unsigned char *unpremul = NULL;
    const unsigned char *encode_pixels = frame->pixels;
    if (frame->is_pbgra) {
        size_t bytes = frame->rowBytes * (size_t)frame->height;
        unpremul = (unsigned char *)malloc(bytes);
        if (!unpremul) return E_OUTOFMEMORY;
        memcpy(unpremul, frame->pixels, bytes);
        for (size_t i = 0; i + 3 < bytes; i += 4) {
            unsigned a = unpremul[i + 3];
            if (a == 0 || a == 255) { if (a == 0) { unpremul[i] = unpremul[i+1] = unpremul[i+2] = 0; } continue; }
            for (int c = 0; c < 3; c++) {
                unsigned v = (unpremul[i + c] * 255u + a / 2) / a;
                unpremul[i + c] = (unsigned char)(v > 255 ? 255 : v);
            }
        }
        encode_pixels = unpremul;
        if (getenv("WPF_LINUX_WIC_TRACE"))
            fprintf(stderr, "WIC_TRACE UNPREMULTIPLY 已反解（源为 32bppPBGRA）\n");
    }

    sk_imageinfo_t info = { 0, frame->width, frame->height, 6 /*BGRA8888*/, 3 /*unpremul*/ };
    sk_image_t *img = sk_image_new_raster_copy(&info, encode_pixels, frame->rowBytes);
    if (!img) return E_UNEXPECTED;
    int fmt = (e->enc_format == 2) ? SKIA_FMT_JPEG : SKIA_FMT_PNG;
    sk_data_t *d = sk_image_encode_specific(img, fmt, e->quality > 0 ? e->quality : 90);
    if (!d) return E_UNEXPECTED;

    const unsigned char *bytes = (const unsigned char *)sk_data_get_data(d);
    size_t n = sk_data_get_size(d);
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE ENCODED %s %zu 字节（%dx%d q=%d）\n",
                e->enc_format == 2 ? "JPEG" : "PNG", n, frame->width, frame->height, e->quality);

    if (frame->has_dpi && !e->has_dpi) { e->dpiX = frame->dpiX; e->dpiY = frame->dpiY; e->has_dpi = 1; }
    if (frame->meta_count > 0 && e->meta_count == 0) {      /* 元数据也可能写在 frame 上 */
        e->meta_count = frame->meta_count;
        memcpy(e->meta_keys, frame->meta_keys, sizeof e->meta_keys);
        memcpy(e->meta_vals, frame->meta_vals, sizeof e->meta_vals);
    }

    int32_t hr = S_OK; const char *why = "";
    if (e->enc_format == 1 && (e->has_dpi || e->meta_count > 0)) {
        unsigned char *inj = NULL; size_t inn = 0;
        if (png_inject(e, bytes, n, &inj, &inn)) {
            if (frame->has_dpi && !e->has_dpi) { e->dpiX = frame->dpiX; e->dpiY = frame->dpiY; e->has_dpi = 1; }
            if (getenv("WPF_LINUX_WIC_TRACE"))
                fprintf(stderr, "WIC_TRACE PNG_INJECT pHYs=%d tEXt=%d → %zu 字节\n",
                        e->has_dpi ? 1 : 0, e->meta_count, inn);
            hr = stream_write_bytes(e->stream_h, inj, inn, &why);
            free(inj);
        } else hr = stream_write_bytes(e->stream_h, bytes, n, &why);
    } else {
        if (frame->has_dpi && !e->has_dpi) { e->dpiX = frame->dpiX; e->dpiY = frame->dpiY; e->has_dpi = 1; }
        hr = stream_write_bytes(e->stream_h, bytes, n, &why);
    }
    sk_data_unref_fn ? sk_data_unref_fn(d) : (void)0;
    if (unpremul) free(unpremul);
    if (hr != S_OK && getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE STREAM_WRITE_FAILED %s\n", why);
    return hr;
}

int32_t IWICMetadataQueryWriter_SetMetadataByName_Proxy(void *writer, uint16_t *wzName, propvariant_t *propValue)
{
    wic_obj *w = obj_get((intptr_t)writer);
    if (!w || w->kind != KIND_METAW || !wzName || !propValue) return E_INVALIDARG;
    wic_obj *enc = obj_get(w->parent_h);
    if (!enc || enc->kind != KIND_ENCODER) return E_INVALIDARG;

    char q[512]; utf16_to_utf8(wzName, q, sizeof q);
    size_t klen = 0;
    const char *key = meta_key_from_query(q, &klen);
    if (!key || klen == 0 || klen >= sizeof enc->meta_keys[0]) return WINCODEC_ERR_UNSUPPORTEDOPERATION;
    if (propValue->vt != VT_LPSTR || !propValue->v1) return WINCODEC_ERR_UNSUPPORTEDOPERATION;
    if (enc->meta_count >= 8) return E_OUTOFMEMORY;

    memcpy(enc->meta_keys[enc->meta_count], key, klen);
    enc->meta_keys[enc->meta_count][klen] = 0;
    const char *val = (const char *)(uintptr_t)propValue->v1;
    size_t vl = strlen(val); if (vl >= sizeof enc->meta_vals[0]) vl = sizeof enc->meta_vals[0] - 1;
    memcpy(enc->meta_vals[enc->meta_count], val, vl);
    enc->meta_vals[enc->meta_count][vl] = 0;
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE META_WRITE \"%s\" = \"%s\"\n", enc->meta_keys[enc->meta_count], enc->meta_vals[enc->meta_count]);
    enc->meta_count++;
    return S_OK;
}

int32_t IWICBitmapFrameEncode_GetMetadataQueryWriter_Proxy(void *fe, void **ppIQueryWriter)
{
    if (ppIQueryWriter) *ppIQueryWriter = NULL;
    wic_obj *f = obj_get((intptr_t)fe);
    if (!f || f->kind != KIND_FRAMEENC || !ppIQueryWriter) return E_INVALIDARG;
    intptr_t h = obj_new(KIND_METAW);
    if (!h) return E_OUTOFMEMORY;
    obj_get(h)->parent_h = f->parent_h;                 /* 元数据记在编码器上，Commit 时注入 */
    *ppIQueryWriter = (void *)h;
    return S_OK;
}

/* 写面里本轮不做的（明确不支持，不静默）：缩略图/调色板/色彩上下文/编码器信息 */
int32_t IWICBitmapEncoder_GetEncoderInfo_Proxy(void *encoder, void **ppIEncoderInfo)
{ (void)encoder; if (ppIEncoderInfo) *ppIEncoderInfo = NULL; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapEncoder_SetPalette_Proxy(void *encoder, void *pIPalette)
{ (void)encoder; (void)pIPalette; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapEncoder_SetThumbnail_Proxy(void *encoder, void *pIThumbnail)
{ (void)encoder; (void)pIThumbnail; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapEncoder_GetMetadataQueryWriter_Proxy(void *encoder, void **ppIQueryWriter)
{ (void)encoder; if (ppIQueryWriter) *ppIQueryWriter = NULL; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapFrameEncode_SetPalette_Proxy(void *fe, void *pIPalette)
{ (void)fe; (void)pIPalette; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapFrameEncode_SetThumbnail_Proxy(void *fe, void *pIThumbnail)
{ (void)fe; (void)pIThumbnail; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }
int32_t IWICBitmapFrameEncode_SetColorContexts_Proxy(void *fe, uint32_t n, void **ctx)
{ (void)fe; (void)n; (void)ctx; return WINCODEC_ERR_UNSUPPORTEDOPERATION; }

/* 供 WicShim 自测读出自己的流内容 */
__attribute__((visibility("default")))
int32_t WicShim_StreamBytes(intptr_t h, unsigned char **ppBuf, size_t *pLen)
{
    wic_obj *st = obj_get(h);
    if (!st || st->kind != KIND_STREAM || !ppBuf || !pLen) return E_INVALIDARG;
    *ppBuf = st->stream_buf; *pLen = st->stream_len;
    return S_OK;
}

int32_t IWICMetadataQueryReader_GetLocation_Proxy(void *reader, uint32_t cchLocation,
                                                  uint16_t *wzNamespace, uint32_t *pcchActual)
{
    wic_obj *m = obj_get((intptr_t)reader);
    if (!m || m->kind != KIND_META) return E_INVALIDARG;
    if (pcchActual) *pcchActual = 2;                       /* "/" + NUL */
    if (wzNamespace) {
        if (cchLocation >= 2) { wzNamespace[0] = '/'; wzNamespace[1] = 0; }
        else if (cchLocation == 1) wzNamespace[0] = 0;
    }
    return S_OK;
}

int32_t IWICMetadataQueryReader_GetContainerFormat_Proxy(void *reader, guid_t *pguidContainerFormat)
{
    wic_obj *m = obj_get((intptr_t)reader);
    if (!m || m->kind != KIND_META || !pguidContainerFormat) return E_INVALIDARG;
    wic_obj *parent = obj_get(m->parent_h);
    if (!parent) return E_INVALIDARG;
    if (decode_open(parent) != S_OK || !parent->bytes) return E_INVALIDARG;
    if (is_png_bytes(parent->bytes, parent->size))  { *pguidContainerFormat = guid_png();  return S_OK; }
    if (is_jpeg_bytes(parent->bytes, parent->size)) { *pguidContainerFormat = guid_jpeg(); return S_OK; }
    return WINCODEC_ERR_UNSUPPORTEDOPERATION;
}


/* ------------------------------------------------------------------ 导出：format converter */
__attribute__((visibility("default")))
int32_t IWICFormatConverter_Initialize_Proxy(void *converter, void *source, guid_t *dstFormat,
                                             int32_t dither, void *bitmapPalette,
                                             double alphaThreshold, int32_t paletteTranslate)
{
    (void)dither; (void)bitmapPalette; (void)alphaThreshold; (void)paletteTranslate;
    wic_obj *c = obj_get((intptr_t)converter);
    if (!c || c->kind != KIND_CONVERTER || !source || !dstFormat) return E_INVALIDARG;

    wic_obj *src = as_source(source);
    if (!src) return E_INVALIDARG;

    int hr = decode_open(src);
    if (hr != S_OK) return hr;

    /* 本 shim 只产出 Bgra32；要求转成别的格式 → 明确不支持（不静默给错格式） */
    guid_t bgra = (guid_t)GUID_32BPPBGRA;
    if (memcmp(dstFormat, &bgra, sizeof(guid_t)) != 0) return WINCODEC_ERR_UNSUPPORTEDOPERATION;

    if (src->fd >= 0) {
        inherit_source_bytes(c, src);  /* 透传源：fd 或字节二选一（见 inherit_source_bytes）*/
        return S_OK;
    }

    /* 源**没有 fd** —— 内存位图（CreateBitmapFromMemory / CreateBitmapFromSource 的产物）。
     * 本轮踩过：透传得到 fd=-1，随后 GetPixelFormat→decode_open 报 E_UNEXPECTED
     * （FormatConvertedBitmap 建不出来）。这里直接**接管像素**（拷一份）。*/
    hr = decode_pixels(src);
    if (hr != S_OK) return hr;
    if (!src->pixels) return E_UNEXPECTED;
    {
        size_t bytes = src->rowBytes * (size_t)src->height;
        c->pixels = (unsigned char *)malloc(bytes);
        if (!c->pixels) return E_OUTOFMEMORY;
        memcpy(c->pixels, src->pixels, bytes);
        c->rowBytes = src->rowBytes;
        c->width = src->width; c->height = src->height;
        c->colorType = src->colorType; c->alphaType = src->alphaType;
        c->decoded = 1; c->has_memFormat = 1;
        guid_t bgra2 = (guid_t)GUID_32BPPBGRA;
        memcpy(c->memFormat, &bgra2, 16);
        if (src->has_dpi) { c->dpiX = src->dpiX; c->dpiY = src->dpiY; c->has_dpi = 1; }
        else { double dx, dy; if (meta_resolution(src, &dx, &dy)) { c->dpiX = dx; c->dpiY = dy; c->has_dpi = 1; } }
    }
    return S_OK;
}

/* ------------------------------------------------------------------ 导出：codec info（最小字符串面） */
/* 把 UTF-16 字符串写进调用方的缓冲（MarshalAs LPWStr ⇒ wchar_t* 在 Linux 上是 4 字节，
 * 但托管 marshal 的 LPWStr 在非 Windows 上是 **UTF-16**，所以这里按 uint16_t 写）。 */
static int32_t write_lpstr(const char *ascii, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    size_t len = strlen(ascii);
    if (pcchActual) *pcchActual = (uint32_t)len + 1;
    if (!wz || cch == 0) return S_OK;          /* 只问长度：这是 WIC 的常规两段式调用 */
    if (cch < len + 1) return E_INVALIDARG;
    for (size_t i = 0; i < len; i++) wz[i] = (uint16_t)(unsigned char)ascii[i];
    wz[len] = 0;
    return S_OK;
}

static const char *container_short_name(const guid_t *g)
{
    guid_t png = (guid_t)GUID_PNG, jpg = (guid_t)GUID_JPEG, bmp = (guid_t)GUID_BMP,
           gif = (guid_t)GUID_GIF, tif = (guid_t)GUID_TIFF;
    if (!memcmp(g, &png, sizeof(guid_t))) return "PNG";
    if (!memcmp(g, &jpg, sizeof(guid_t))) return "JPEG";
    if (!memcmp(g, &bmp, sizeof(guid_t))) return "BMP";
    if (!memcmp(g, &gif, sizeof(guid_t))) return "GIF";
    if (!memcmp(g, &tif, sizeof(guid_t))) return "TIFF";
    return "Unknown";
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetContainerFormat_Proxy(void *codecInfo, guid_t *pguidContainerFormat)
{
    if (!obj_get((intptr_t)codecInfo) || !pguidContainerFormat) return E_INVALIDARG;
    *pguidContainerFormat = (guid_t)GUID_PNG;   /* codecInfo 未绑定具体源；PNG 为默认 */
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetFriendlyName_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr("WPF-on-Linux Skia WIC codec", cch, wz, pcchActual);
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetMimeTypes_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr("image/png,image/jpeg,image/bmp,image/gif,image/tiff", cch, wz, pcchActual);
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetFileExtensions_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr(".png,.jpg,.jpeg,.bmp,.gif,.tif,.tiff", cch, wz, pcchActual);
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetDeviceManufacturer_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr("", cch, wz, pcchActual);
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetDeviceModels_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr("", cch, wz, pcchActual);
}

__attribute__((visibility("default")))
int32_t IWICBitmapCodecInfo_GetColorManagementVersion_Proxy(void *codecInfo, uint32_t cch, uint16_t *wz, uint32_t *pcchActual)
{
    if (!obj_get((intptr_t)codecInfo)) return E_INVALIDARG;
    return write_lpstr("1.0.0.0", cch, wz, pcchActual);
}

/* ------------------------------------------------------------------ 导出：pixel format info */
__attribute__((visibility("default")))
int32_t IWICPixelFormatInfo_GetBitsPerPixel_Proxy(void *pIPixelFormatInfo, uint32_t *puiBitsPerPixel)
{
    if (!obj_get((intptr_t)pIPixelFormatInfo) || !puiBitsPerPixel) return E_INVALIDARG;
    *puiBitsPerPixel = 32;   /* Bgra32 */
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICPixelFormatInfo_GetChannelCount_Proxy(void *pIPixelFormatInfo, uint32_t *puiChannelCount)
{
    if (!obj_get((intptr_t)pIPixelFormatInfo) || !puiChannelCount) return E_INVALIDARG;
    *puiChannelCount = 4;
    return S_OK;
}

/* ------------------------------------------------------------------ 未实现面：统一 NOTIMPLEMENTED */
/* 未实现被调用时，WPF_LINUX_WIC_TRACE=1 会打出来 —— 否则"未实现"会伪装成业务失败
 * （本轮就是这样：恢复路径撞上 CreateBitmapFromMemory 的桩，报的却是"invalid state"）。*/
static int32_t stub_hit(const char *name)
{
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE STUB %s -> 0x%08X\n", name, (unsigned)WINCODEC_ERR_NOTIMPLEMENTED);
    return WINCODEC_ERR_NOTIMPLEMENTED;
}

#define NOT_IMPL(name) \
    __attribute__((visibility("default"))) int32_t name(void) { return stub_hit(#name); }

/* 元数据读（BitmapMetadata）——本轮明确不做 */
/* IWICMetadataQueryReader_GetMetadataByName_Proxy —— 已真实现 */
/* IWICMetadataQueryReader_GetLocation_Proxy —— 已真实现 */
/* IWICMetadataQueryReader_GetContainerFormat_Proxy —— 已真实现 */
/* IWICBitmapDecoder_GetMetadataQueryReader_Proxy —— 已真实现 */
/* IWICMetadataQueryWriter_SetMetadataByName_Proxy —— 已实现 */
NOT_IMPL(IWICMetadataBlockReader_GetCount_Proxy)
NOT_IMPL(IWICFastMetadataEncoder_Commit_Proxy)
/* 编码/保存（BitmapEncoder）—— 本轮不做 */
/* IWICImagingFactory_CreateEncoder_Proxy —— 已实现 */
/* IWICBitmapEncoder_Initialize_Proxy —— 已实现 */
/* IWICBitmapEncoder_CreateNewFrame_Proxy —— 已实现 */
/* 已实现 */
/* IWICBitmapFrameEncode_WriteSource_Proxy —— 已实现 */
/* IWICBitmapFrameEncode_Commit_Proxy —— 已实现 */
/* IWICBitmapEncoder_Commit_Proxy —— 已实现 */
/* 其它冷门面 */

/* IWICImagingFactory_CreateBitmap_Proxy —— 已真实现（见上面「真实现」一节：RenderTargetBitmap 必经）*/
/* IWICImagingFactory_CreateBitmapFromMemory_Proxy —— 已真实现（见上面「真实现」一节），不再是桩 */
/* IWICImagingFactory_CreateBitmapFromSource_Proxy —— 已真实现，不再是桩 */
NOT_IMPL(IWICImagingFactory_CreateBitmapFromHBITMAP_Proxy)
NOT_IMPL(IWICImagingFactory_CreateBitmapFromHICON_Proxy)
NOT_IMPL(IWICImagingFactory_CreateBitmapScaler_Proxy)
/* IWICImagingFactory_CreateBitmapClipper_Proxy ＋ IWICBitmapClipper_Initialize_Proxy —— 已真实现
 * （见上面「真实现」一节：`CroppedBitmap` 必经；位图裁剪）*/
NOT_IMPL(IWICImagingFactory_CreateBitmapFlipRotator_Proxy)
NOT_IMPL(IWICImagingFactory_CreatePalette_Proxy)
NOT_IMPL(IWICColorContext_InitializeFromFilename_Proxy)
NOT_IMPL(WICConvertBitmapSource_Proxy)
NOT_IMPL(WICCreateBitmapFromSection_Proxy)
/* 已实现 */
/* WICMapShortNameToGuid_Proxy —— 已实现 */
/* WICMapGuidToShortName_Proxy —— 已实现 */

/* CreateStream：StreamAsIStream 路径的入口 —— 单独记数，便于 harness 判定分支 */
__attribute__((visibility("default")))
/* 旧桩已删：真实现见上 */

/* 自检导出：C 探针用它确认"我们连的是同一份 Skia、ABI 假设成立" */
/* harness 用：读/清调用计数（counts 长度 >= 3） */
__attribute__((visibility("default")))
int32_t WicShim_CallCounts(int32_t *counts)
{
    if (!counts) return E_INVALIDARG;
    counts[0] = g_calls[0]; counts[1] = g_calls[1]; counts[2] = g_calls[2];
    return S_OK;
}

__attribute__((visibility("default")))
int32_t WicShim_SelfTest(const char *pngPath, int32_t *pWidth, int32_t *pHeight, uint32_t *pFirstPixelBGRA)
{
    if (!skia_load()) return WINCODEC_ERR_GENERIC_ERROR;

    int fd = open(pngPath, O_RDONLY);
    if (fd < 0) return E_INVALIDARG;

    intptr_t h = obj_new(KIND_DECODER);
    if (!h) { close(fd); return E_OUTOFMEMORY; }
    wic_obj *o = obj_get(h);
    o->fd = fd;

    int hr = decode_pixels(o);
    if (hr != S_OK) { obj_drop(h, o); return hr; }

    if (pWidth) *pWidth = o->width;
    if (pHeight) *pHeight = o->height;
    if (pFirstPixelBGRA && o->pixels)
        *pFirstPixelBGRA = (uint32_t)o->pixels[0] | ((uint32_t)o->pixels[1] << 8) |
                           ((uint32_t)o->pixels[2] << 16) | ((uint32_t)o->pixels[3] << 24);

    obj_drop(h, o);
    return S_OK;
}

/* ---------------------------------------------------------------------------
 * 〇 · ole32 公寓初始化（WICCodec.CoInitialize / CoUninitialize）
 *
 * 上游 `UnsafeNativeMethodsMilCoreApi.cs:1050,1054` 用 `[DllImport("ole32.dll")]`
 * 声明了这两个函数，DWF/PC 的 WIC 路径一进门就会调用 CoInitialize。
 * Linux 上没有 COM 公寓模型：WIC 侧我们走 Skia，不需要任何公寓；这里提供
 * **语义正确的空实现**——CoInitialize 直接返回 S_OK(=0)，CoUninitialize 空操作。
 * 这不是"骗闸门"：公寓模型在本平台不存在，S_OK 表示"调用方可以继续"，与实际
 * 情况一致（无公寓可初始化）。全 PC 编译集里 ole32.dll 的 DllImport 只有这 2 处，
 * 映射爆炸半径=这 2 处（见 REPORT.md 的普查）。
 * --------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int32_t CoInitialize(void *pvReserved)
{
    (void)pvReserved;
    return 0; /* S_OK */
}

__attribute__((visibility("default")))
void CoUninitialize(void)
{
}

/* ---------------------------------------------------------------------------
 * 〇-b · 句柄归属判定（给 MILCore 的 MILQueryInterface 用）
 *
 * PC 的 `BitmapSource.set_WicSourceHandle`（BitmapSource.cs:581-586）对 WIC 源句柄调用
 * `MILUnknown.QueryInterface(value, IID_IWICBitmapSource, out wicSource)`，该 P/Invoke 落在
 * **MilCore**（wgfx 导出 MILQueryInterface）。WIC 句柄是本 shim 下发的**不透明句柄**，
 * 既不在 MIL 设备对象表里，也不是 COM 指针。上游 Windows 上这一步能过，是因为那里的句柄是
 * 真正的 COM 指针、QI 由对象自己的 vtable 应答；Linux 上必须由句柄的**所有者**来回答。
 * 本导出即该判定：句柄属于本 shim 的对象表 → 1，否则 0。
 * MIL 侧只需：MILQueryInterface 里 Resolve 失败时，dlopen 本 .so 取 WicShim_OwnsHandle，
 * 为真且 guid ∈ {IID_IUnknown, IID_IWICBitmapSource} 时返回同一句柄 + AddRef。
 * --------------------------------------------------------------------------- */
__attribute__((visibility("default")))
/* ---- 外来源登记（dispatch，不是记账）：MIL 单向把描述交进来 --------------------
 * 具体契约见文件末 WicShim_RegisterForeignSource 的实现与 REPORT §21。
 * ⚠ 未登记句柄仍 E_INVALIDARG；WicShim_OwnsHandle 对外来条目返 0（记账留在 MIL）。*/
/* 活句柄高水位（判「有没有回收」：HandleCount 相等但 Peak 一直涨 = 花架子）*/
__attribute__((visibility("default")))
int32_t WicShim_PeakHandleCount(void)
{
pthread_mutex_lock(&g_table_lock);
int32_t n = g_peak;
pthread_mutex_unlock(&g_table_lock);
return n;
} 
/* obj_drop 定义：摘表 + 计数 + 释放（obj_new 之后三处释放点共用）*/
static void obj_drop(intptr_t h, wic_obj *o)
{
pthread_mutex_lock(&g_table_lock);
g_objs[h - 1] = NULL;
table_note_remove();
pthread_mutex_unlock(&g_table_lock);
obj_free(o);
} 
/* ---------------------------------------------------------------------------
 * 导出助手：把**这一条解码链**（Skia C API）借给别的原生面用（`#41` F：GDI+ 图像族）
 *
 *   ⚠️ **口径：本文件是唯一的解码实现。** `libwpfwin32.so` 的 GDI+ 家族要"真解码图片"时，
 *      通过 `dlopen("libwpfwic.so")` 调**这里**，**不许**再写第二份解码
 *      （同族教训：同一个语义存在多处 ⇒ 必然分叉 —— 本仓反复登记过）。
 *
 *   返回码（**如实失败**，调用方翻成 GDI+ 的 `GpStatus`）：
 *     0 = 成功：`*pixels` 为 malloc 出来的 **BGRA32** 缓冲（调用方负责 `free`），`*w/*h/*stride` 已填
 *     1 = 参数非法　2 = 内存/句柄表不足　3 = 打开失败（不存在/权限）　4 = 解码失败（Skia 拒绝该字节流）
 * ------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int WpfWic_DecodeFileToBgra(const char *utf8_path, unsigned char **pixels,
                            int32_t *w, int32_t *h, int32_t *stride)
{
    if (!utf8_path || !pixels || !w || !h || !stride) return 1;
    *pixels = NULL; *w = 0; *h = 0; *stride = 0;

    /* 文件形态的 decoder（与 `CreateDecoderFromFileHandle` 同一条路：KIND_DECODER + fd） */
    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    /* ⚠️ `obj_new` 返回的是 **1-based 句柄索引**（`g_objs[h-1]`），**不是对象指针**：
       本助手第一版把它当指针用 ⇒ `o = 0x1` ⇒ 段错误（探针在 `WpfWic_DecodeFileToBgra`
       里第一次调用就抓到，这正是它该有的作用）。 */
    wic_obj *o = obj_get(handle);      /* 仓里既有的句柄→对象访问器 */
    if (!o) return 2;
    o->fd = open(utf8_path, O_RDONLY);
    if (o->fd < 0) { obj_drop(handle, o); return 3; }

    int hr = decode_pixels(o);
    if (hr != S_OK || !o->pixels || o->width <= 0 || o->height <= 0) { obj_drop(handle, o); return 4; }

    size_t need = (size_t)o->rowBytes * (size_t)o->height;
    unsigned char *out = (unsigned char *)malloc(need ? need : 1);
    if (!out) { obj_drop(handle, o); return 2; }
    memcpy(out, o->pixels, need);
    *pixels = out;
    *w = o->width; *h = o->height; *stride = (int32_t)o->rowBytes;
    obj_drop(handle, o);
    return 0;
}

/* ---------------------------------------------------------------------------
 * 〇-d · 多帧旁路助手（GDI+ 面的第二个消费者；`#50` 多帧能力的对外出口）
 *
 * 背景：HandyControl 的 `hc:GifImage` 走 **GDI+ 面**（`[DllImport("gdiplus.dll")]`），而 GDI+ 面
 *   （`src/WpfGfx.Linux.Native/src/win32_gdiplus.c`）只能 `dlopen`/`dlsym` 本 shim 的导出
 *   ⇒ 多帧能力必须在**这里**开三个薄出口（GDI+ 面不自己写第二份解码，沿用既有的
 *   `decode_open`/`decode_pixels`/`decode_frame_count`/`gif_frame_delay_cs`）。
 *
 * 返回码口径与 `WpfWic_DecodeFileToBgra` **逐码一致**：
 *   0 = 成功　1 = 参数非法　2 = 内存/句柄表不足　3 = 打开失败　4 = 解码失败
 * ------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int WpfWic_FrameCount(const char *utf8_path, int32_t *out_count)
{
    if (!utf8_path || !out_count) return 1;
    *out_count = 1;

    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    wic_obj *o = obj_get(handle);
    if (!o) return 2;
    o->fd = open(utf8_path, O_RDONLY);
    if (o->fd < 0) { obj_drop(handle, o); return 3; }

    int hr = decode_open(o);
    if (hr != S_OK) { obj_drop(handle, o); return 4; }

    *out_count = decode_frame_count(o);      /* 单帧图恒 1；多帧图 = Skia 报告的真实帧数 */
    obj_drop(handle, o);
    return 0;
}

/* 第 `index` 帧的显示时长（毫秒）。按时长表缺失 ⇒ 返回 4（**不编造**默认时长，
 * 由调用方决定"没有时长信息"时怎么办）。 */
__attribute__((visibility("default")))
int WpfWic_FrameDelayMs(const char *utf8_path, int32_t index, int32_t *out_ms)
{
    if (!utf8_path || !out_ms || index < 0) return 1;
    *out_ms = 0;

    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    wic_obj *o = obj_get(handle);
    if (!o) return 2;
    o->fd = open(utf8_path, O_RDONLY);
    if (o->fd < 0) { obj_drop(handle, o); return 3; }

    int hr = decode_open(o);                 /* 读入 o->bytes（延时表就在 GIF 字节里）*/
    if (hr != S_OK) { obj_drop(handle, o); return 4; }

    uint16_t cs = 0;
    if (!gif_frame_delay_cs(o->bytes, o->size, index, &cs)) { obj_drop(handle, o); return 4; }

    *out_ms = (int32_t)cs * 10;              /* 厘秒 → 毫秒 */
    obj_drop(handle, o);
    return 0;
}

/* 解**指定帧**为 BGRA32。与 `WpfWic_DecodeFileToBgra` 的唯一差别是 `o->frame_index = index`
 * （`decode_pixels` 的逐帧分支：`opts.frame_index` 走 `sk_codec_get_pixels`）。 */
__attribute__((visibility("default")))
int WpfWic_DecodeFrameToBgra(const char *utf8_path, int32_t index,
                             unsigned char **pixels, int32_t *w, int32_t *h, int32_t *stride)
{
    if (!utf8_path || !pixels || !w || !h || !stride || index < 0) return 1;
    *pixels = NULL; *w = 0; *h = 0; *stride = 0;

    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    wic_obj *o = obj_get(handle);
    if (!o) return 2;
    o->fd = open(utf8_path, O_RDONLY);
    if (o->fd < 0) { obj_drop(handle, o); return 3; }

    o->frame_index = index;                  /* ← 唯一的差别：逐帧分支 */

    int hr = decode_pixels(o);
    if (hr != S_OK || !o->pixels || o->width <= 0 || o->height <= 0) { obj_drop(handle, o); return 4; }

    size_t need = (size_t)o->rowBytes * (size_t)o->height;
    unsigned char *out = (unsigned char *)malloc(need ? need : 1);
    if (!out) { obj_drop(handle, o); return 2; }
    memcpy(out, o->pixels, need);
    *pixels = out;
    *w = o->width; *h = o->height; *stride = (int32_t)o->rowBytes;
    obj_drop(handle, o);
    return 0;
}

/* ---------------------------------------------------------------------------
 * 〇-e · 从**内存字节**解码（流/pack 资源的场景）
 *
 * 为什么需要：HandyControl 的 `hc:GifImage` 在 XAML 里给 `Uri`（pack 资源）时走
 *   `GetGifStreamFromPack → CreateSourceFromStream → GdipCreateBitmapFromStream(new GPStream(stream), …)`，
 *   也就是说**字节在内存里、没有文件路径**。文件路径那三个助手用不上，故补这两个。
 *   实现与 `WpfWic_DecodeFrameToBgra` 同构：唯一差别是"字节由调用方给"（`o->bytes` 走
 *   `decode_open_bytes` 分支），仍然复用 `decode_open`/`decode_pixels`/`decode_frame_count`。
 * ------------------------------------------------------------------------- */
__attribute__((visibility("default")))
int WpfWic_BytesFrameCount(const unsigned char *bytes, size_t n, int32_t *out_count)
{
    if (!bytes || !n || !out_count) return 1;
    *out_count = 1;

    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    wic_obj *o = obj_get(handle);
    if (!o) return 2;
    o->bytes = (unsigned char *)malloc(n);
    if (!o->bytes) { obj_drop(handle, o); return 2; }
    memcpy(o->bytes, bytes, n);
    o->size = n;
    o->fd = -1;                      /* 字节路径：不看 fd（decode_open 会走 decode_open_bytes）*/

    int hr = decode_open(o);
    if (hr != S_OK) { obj_drop(handle, o); return 4; }

    *out_count = decode_frame_count(o);
    obj_drop(handle, o);
    return 0;
}

__attribute__((visibility("default")))
int WpfWic_DecodeBytesToBgra(const unsigned char *bytes, size_t n, int32_t index,
                             unsigned char **pixels, int32_t *w, int32_t *h, int32_t *stride)
{
    if (!bytes || !n || !pixels || !w || !h || !stride || index < 0) return 1;
    *pixels = NULL; *w = 0; *h = 0; *stride = 0;

    intptr_t handle = obj_new(KIND_DECODER);
    if (!handle) return 2;
    wic_obj *o = obj_get(handle);
    if (!o) return 2;
    o->bytes = (unsigned char *)malloc(n);
    if (!o->bytes) { obj_drop(handle, o); return 2; }
    memcpy(o->bytes, bytes, n);
    o->size = n;
    o->fd = -1;
    o->frame_index = index;          /* 逐帧：走 decode_pixels 的 opts.frame_index 分支 */

    int hr = decode_pixels(o);
    if (hr != S_OK || !o->pixels || o->width <= 0 || o->height <= 0) { obj_drop(handle, o); return 4; }

    size_t need = (size_t)o->rowBytes * (size_t)o->height;
    unsigned char *out = (unsigned char *)malloc(need ? need : 1);
    if (!out) { obj_drop(handle, o); return 2; }
    memcpy(out, o->pixels, need);
    *pixels = out;
    *w = o->width; *h = o->height; *stride = (int32_t)o->rowBytes;
    obj_drop(handle, o);
    return 0;
}

/* ---------------------------------------------------------------------------
* 〇-c · 读路径补齐：缩略图 / 预览 / 色彩上下文
*
* 每个码都是**按上游调用点的容忍逻辑**选的，不是随手填（见各函数注释）：
*   · EnsureThumbnail（BitmapFrameDecode.cs:570-574）：`hr != CODECNOTHUMBNAIL` 才 Check
*     ⇒ 无缩略图时**必须**返回 CODECNOTHUMBNAIL，否则抛异常。
*   · BitmapDecoder.get_Preview（:737-743）：容忍的是 **UNSUPPORTEDOPERATION**（不是上面那个）。
*   · 色彩上下文：我们暂不解析 iCCP ⇒ 诚实回"0 个"，不编造数据。
* --------------------------------------------------------------------------- */
#define WINCODEC_ERR_CODECNOTHUMBNAIL ((int32_t)0x88982f44)
/*（定义已上移到文件头错误码区，供前面的元数据代码使用）*/ 
/* Skia 不暴露内嵌缩略图 ⇒ CODECNOTHUMBNAIL 就是 Windows 对"该编码器没有缩略图"的语义 */
__attribute__((visibility("default")))
int32_t IWICBitmapFrameDecode_GetThumbnail_Proxy(void *frame, void **ppIThumbnail)
{
(void)frame;
if (ppIThumbnail) *ppIThumbnail = NULL;
return WINCODEC_ERR_CODECNOTHUMBNAIL;
} 
__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetThumbnail_Proxy(void *decoder, void **ppIThumbnail)
{
(void)decoder;
if (ppIThumbnail) *ppIThumbnail = NULL;
return WINCODEC_ERR_CODECNOTHUMBNAIL;
} 
/* ⚠ 这个用 UNSUPPORTEDOPERATION（上游 get_Preview 只容忍它） */
__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetPreview_Proxy(void *decoder, void **ppIBitmapSource)
{
(void)decoder;
if (ppIBitmapSource) *ppIBitmapSource = NULL;
return WINCODEC_ERR_UNSUPPORTEDOPERATION;
} 
/* 色彩上下文：0 个（诚实；未解析 PNG iCCP / JPEG ICC —— 已在未覆盖清单登记）*/
__attribute__((visibility("default")))
int32_t IWICBitmapFrameDecode_GetColorContexts_Proxy(void *frame, uint32_t count,
void **ppIColorContext, uint32_t *pActualCount)
{
(void)frame; (void)count; (void)ppIColorContext;
if (pActualCount) *pActualCount = 0;
return S_OK;
} 
__attribute__((visibility("default")))
int32_t IWICBitmapDecoder_GetColorContexts_Proxy(void *decoder, uint32_t count,
void **ppIColorContext, uint32_t *pActualCount)
{
(void)decoder; (void)count; (void)ppIColorContext;
if (pActualCount) *pActualCount = 0;
return S_OK;
} 
/* ===========================================================================
 *  句柄归属 + 引用计数（T1 的 MIL 桥依赖这三件套：OwnsHandle / AddRef / Release）
 *  契约（REPORT §18.2）：初始引用归调用方（PC 的 SafeMILHandle）；QI 放行时 AddRef，
 *  MILRelease 必须转发 Release；归零即回收。表上限 WIC_OBJ_MAX=256，不转发会撞上限。
 *  ⚠ 本节曾被一次误删从源码丢失（只剩注释），此处按原语义恢复；行为与 466c0b06 版本一致。
 * =========================================================================== */
__attribute__((visibility("default")))
int32_t WicShim_OwnsHandle(intptr_t h)
{
    wic_obj *_o = obj_get(h);
    if (_o != NULL && _o->is_foreign) return 0;   /* 外来条目：记账留在 MilDeviceObjectTable */
    return _o != NULL ? 1 : 0;
}

__attribute__((visibility("default")))
int32_t WicShim_AddRef(intptr_t h)
{
    pthread_mutex_lock(&g_table_lock);
    wic_obj *o = (h > 0 && h <= WIC_OBJ_MAX) ? g_objs[h - 1] : NULL;
    if (!o) { pthread_mutex_unlock(&g_table_lock); return 0; }
    int32_t n = ++o->refs;
    pthread_mutex_unlock(&g_table_lock);
    return n;
}

__attribute__((visibility("default")))
int32_t WicShim_Release(intptr_t h)
{
    pthread_mutex_lock(&g_table_lock);
    wic_obj *o = (h > 0 && h <= WIC_OBJ_MAX) ? g_objs[h - 1] : NULL;
    if (!o) { pthread_mutex_unlock(&g_table_lock); return 0; }
    int32_t n = --o->refs;
    if (n > 0) { pthread_mutex_unlock(&g_table_lock); return n; }
    g_objs[h - 1] = NULL; table_note_remove();
    pthread_mutex_unlock(&g_table_lock);
    obj_free(o);
    return 0;
}

/* 活句柄数（T1 的配平判据：500 次 QI+Release 前后必须相等）*/
__attribute__((visibility("default")))
int32_t WicShim_HandleCount(void)
{
    pthread_mutex_lock(&g_table_lock);
    int32_t n = 0;
    for (int _i = 0; _i < WIC_OBJ_MAX; _i++)
        if (g_objs[_i] && !g_objs[_i]->is_foreign) n++;   /* 外来源另计 */
    pthread_mutex_unlock(&g_table_lock);
    return n;
}

/* ===========================================================================
 *  外来源登记（MIL → shim 单向；dispatch，不是记账）
 *  为什么：上游 proxy 是"转发 vtable 调用"，任何被当成 IWICBitmapSource 交出去的句柄，
 *  proxy 层都得能派发。MIL 把后缓冲句柄交给 PC 后，PC 会调
 *  IWICBitmapSource_GetPixelFormat_Proxy(该句柄) —— 表里查不到 ⇒ E_INVALIDARG（PC 抛 ArgumentException）。
 *  只加不放松：未登记句柄仍 E_INVALIDARG；OwnsHandle 对外来条目返 0（引用计数留在 MIL）；
 *  工厂/双缓冲主对象问 IID_IWICBitmapSource 仍须 E_NOINTERFACE（MIL 侧断言，本改动不碰）。
 *  v2：**借用**调用方像素（pixels/rowBytes；注销前有效、obj_free 不释放）；未借像素时 CopyPixels/Lock 诚实拒绝。
 *     此时 CopyPixels 返回 UNSUPPORTEDOPERATION。
 *  ⚠ isOpaque 由调用方按 pixelFormatGuid == MilPixelFormats.Bgr32 判定传入 ——
 *     这是"只认这一种不透明格式"的**近似**，不是通用判定。
 * =========================================================================== */
static wic_obj *foreign_lookup(intptr_t ext)
{
    if (ext == 0) return NULL;
    for (int i = 0; i < WIC_FOREIGN_MAX; i++)
        if (g_foreign[i].ext == ext && g_foreign[i].slot != 0) return obj_get(g_foreign[i].slot);
    return NULL;
}

__attribute__((visibility("default")))
int32_t WicShim_ForeignSourceCount(void)
{
    pthread_mutex_lock(&g_table_lock);
    int32_t n = 0;
    for (int i = 0; i < WIC_FOREIGN_MAX; i++) if (g_foreign[i].slot != 0) n++;
    pthread_mutex_unlock(&g_table_lock);
    return n;
}

__attribute__((visibility("default")))
int32_t WicShim_RegisterForeignSource(intptr_t ext, const guid_t *pixelFormat,
                                      uint32_t width, uint32_t height, int32_t isOpaque,
                                      const void *pixels, uint32_t rowBytes)
{
    /* v2：像素**借用**（生命周期由调用方保证，注销前有效）*/
    if (ext == 0 || !pixelFormat || width == 0 || height == 0) return E_INVALIDARG;

    /* ⚠ 槽位必须在**加锁之前**建：obj_new 内部自己会取 g_table_lock（非递归锁）⇒ 持锁再调它会死锁
     * （首版就踩了这个坑：probe_foreign 直接 60s 超时）。*/
    intptr_t slot = 0;
    for (int i = 0; i < WIC_FOREIGN_MAX; i++)
        if (g_foreign[i].ext == ext && g_foreign[i].slot != 0) { slot = g_foreign[i].slot; break; }
    if (slot == 0)
    {
        slot = obj_new(KIND_FRAME);                     /* 复用"源"种类：派发路径不必改 */
        if (!slot) return E_OUTOFMEMORY;
        int placed = 0;
        pthread_mutex_lock(&g_table_lock);
        for (int i = 0; i < WIC_FOREIGN_MAX; i++)
            if (g_foreign[i].slot == 0) { g_foreign[i].ext = ext; g_foreign[i].slot = slot; placed = 1; break; }
        pthread_mutex_unlock(&g_table_lock);
        if (!placed) { obj_drop(slot, obj_get(slot)); return E_OUTOFMEMORY; }
    }
    wic_obj *o = obj_get(slot);
    if (!o) return E_OUTOFMEMORY;
    pthread_mutex_lock(&g_table_lock);
    o->is_foreign = 1;
    o->has_memFormat = 1;
    memcpy(o->memFormat, pixelFormat, 16);
    o->width = (int32_t)width;
    o->height = (int32_t)height;
    o->rowBytes = rowBytes ? rowBytes : (size_t)width * 4u;
    if (o->pixels) g_foreign_borrows--;   /* 重复登记先退旧账 */
    o->pixels = (unsigned char *)pixels;
    o->pixels_borrowed = pixels ? 1 : 0;
    if (pixels) g_foreign_borrows++;
    /*（v1 的 pixels_borrowed=0 已由上两行取代）*/
    o->alphaType = isOpaque ? 1 : 3;
    o->decoded = 0;
    o->dpiX = 96.0; o->dpiY = 96.0; o->has_dpi = 0;     /* 外来源无 pHYs/JFIF：按 96 退化 */
    pthread_mutex_unlock(&g_table_lock);

    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE FOREIGN_SOURCE_REGISTER ext=0x%llx %ux%u opaque=%d pixels=%p rowBytes=%u borrows=%d\n",
                (unsigned long long)(uintptr_t)ext, width, height, isOpaque, (const void *)pixels, rowBytes, g_foreign_borrows);
    return S_OK;
}

__attribute__((visibility("default")))
int32_t WicShim_UnregisterForeignSource(intptr_t ext)
{
    pthread_mutex_lock(&g_table_lock);
    intptr_t slot = 0;
    for (int i = 0; i < WIC_FOREIGN_MAX; i++)
        if (g_foreign[i].ext == ext && g_foreign[i].slot != 0) { slot = g_foreign[i].slot; g_foreign[i].ext = 0; g_foreign[i].slot = 0; break; }
    pthread_mutex_unlock(&g_table_lock);
    if (slot == 0) return E_INVALIDARG;
    wic_obj *o = obj_get(slot);
    if (o && o->pixels) g_foreign_borrows--;   /* 先注销登记，再让对方释放 */
    obj_drop(slot, o);
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE FOREIGN_SOURCE_UNREGISTER ext=0x%llx borrows=%d\n",
                (unsigned long long)(uintptr_t)ext, g_foreign_borrows);
    return S_OK;
}

/* ===========================================================================
 *  IWICBitmap::Lock / IWICBitmapLock::GetDataPointer / GetStride
 *  调用点：WriteableBitmap.Lock()（WriteableBitmap.cs:256/270/278），紧随 MIL 后缓冲的
 *  QI + GetPixelFormat/GetSize（M7b 已接外来源登记，AOT 实证 hr=0x0）。
 *  语义：把自己创建的位图的「像素 + 行距」交给调用方；**外来源 v1 未借像素 ⇒ 诚实
 *  UNSUPPORTEDOPERATION**（不伪造指针）；只支持整图锁（子矩形 ⇒ UNSUPPORTEDOPERATION，不静默裁剪）。
 *  记账边界不变：锁对象只借用像素（pixels_borrowed=1），obj_free 不 free 它。
 * =========================================================================== */
__attribute__((visibility("default")))
int32_t IWICBitmap_Lock_Proxy(void *bitmap, wic_rect *prcLock, uint32_t flags, void **ppILock)
{
    (void)flags;
    if (ppILock) *ppILock = NULL;
    wic_obj *o = as_source(bitmap);
    if (!o || !ppILock) return E_INVALIDARG;

    if (prcLock && (prcLock->x != 0 || prcLock->y != 0 ||
                    (prcLock->w != 0 && (uint32_t)prcLock->w != (uint32_t)o->width) ||
                    (prcLock->h != 0 && (uint32_t)prcLock->h != (uint32_t)o->height)))
        return WINCODEC_ERR_UNSUPPORTEDOPERATION;

    if (!o->pixels) return WINCODEC_ERR_UNSUPPORTEDOPERATION;

    intptr_t h = obj_new(KIND_LOCK);
    if (!h) return E_OUTOFMEMORY;
    wic_obj *l = obj_get(h);
    l->parent_h = (intptr_t)bitmap;
    l->pixels = o->pixels;                     /* 借用，不拥有 */
    l->pixels_borrowed = 1;
    l->rowBytes = o->rowBytes;
    l->width = o->width; l->height = o->height;
    l->has_memFormat = 1; memcpy(l->memFormat, o->memFormat, 16);
    *ppILock = (void *)h;
    if (getenv("WPF_LINUX_WIC_TRACE"))
        fprintf(stderr, "WIC_TRACE BITMAP_LOCK %dx%d rowBytes=%zu\n", o->width, o->height, o->rowBytes);
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapLock_GetDataPointer_STA_Proxy(void *lock, uint32_t *pcbBufferSize, void **ppbData)
{
    wic_obj *l = obj_get((intptr_t)lock);
    if (!l || l->kind != KIND_LOCK) return E_INVALIDARG;
    if (pcbBufferSize) *pcbBufferSize = (uint32_t)(l->rowBytes * (size_t)l->height);
    if (ppbData) *ppbData = l->pixels;
    return S_OK;
}

__attribute__((visibility("default")))
int32_t IWICBitmapLock_GetStride_Proxy(void *lock, uint32_t *pcbStride)
{
    wic_obj *l = obj_get((intptr_t)lock);
    if (!l || l->kind != KIND_LOCK || !pcbStride) return E_INVALIDARG;
    *pcbStride = (uint32_t)l->rowBytes;
    return S_OK;
}


/* 借用计数：>0 = 有外来源正把像素借给本 shim（此时调用方不得先释放那块内存）。*/
__attribute__((visibility("default")))
int32_t WicShim_ForeignPixelBorrows(void)
{
    pthread_mutex_lock(&g_table_lock);
    int32_t n = g_foreign_borrows;
    pthread_mutex_unlock(&g_table_lock);
    return n;
}
/* ===========================================================================
 *  WicShim_DescribeHandle —— D-d 取证出口：**一个句柄在本 shim 里到底是什么**
 *  为什么需要：读路径上 PC 传下来的句柄，只有本 shim 知道它是什么（种类/尺寸/格式/是否外来/引用数）。
 *  没有它就只能靠"读出来的数值"猜（M7b 那次 1x1 的读数就是这么来的）。
 *  语义：返回 S_OK 并把描述写进 buf（NUL 结尾）；句柄不在表里 ⇒ E_INVALIDARG 且 buf 写 "<unknown>"。
 *  另：**外来条目的 ext 句柄与它映射到的内部槽位都会列出**（避免"看到 3 却不知是 ext 还是 slot"）。
 * =========================================================================== */
static const char *kind_name(int k)
{
    switch (k) {
        case KIND_FACTORY: return "Factory";
        case KIND_DECODER: return "Decoder";
        case KIND_FRAME:   return "Frame/Source";
        case KIND_CONVERTER: return "FormatConverter";
        case KIND_CODECINFO: return "CodecInfo";
        case KIND_META: return "MetadataQueryReader";
        case KIND_ENCODER: return "Encoder";
        case KIND_FRAMEENC: return "FrameEncode";
        case KIND_OPTIONS: return "PropertyBag2";
        case KIND_STREAM: return "WICStream";
        case KIND_METAW: return "MetadataQueryWriter";
        case KIND_ENUM: return "EnumString";
        case KIND_LOCK: return "BitmapLock";
        default: return "?";
    }
}

__attribute__((visibility("default")))
int32_t WicShim_DescribeHandle(intptr_t h, char *buf, size_t cap)
{
    if (!buf || cap == 0) return E_INVALIDARG;
    buf[0] = 0;

    wic_obj *o = obj_get(h);                     /* 直接是内部槽位句柄？ */
    int via = 0;
    if (!o) { o = foreign_lookup(h); via = 1; }  /* 否则当"外来源 ext 句柄"查 */
    if (!o) { snprintf(buf, cap, "<unknown> h=%lld", (long long)h); return E_INVALIDARG; }

    char fmt[40] = "-";
    if (o->has_memFormat)
        snprintf(fmt, sizeof fmt, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                 o->memFormat[3], o->memFormat[2], o->memFormat[1], o->memFormat[0],
                 o->memFormat[5], o->memFormat[4], o->memFormat[7], o->memFormat[6],
                 o->memFormat[8], o->memFormat[9], o->memFormat[10], o->memFormat[11],
                 o->memFormat[12], o->memFormat[13], o->memFormat[14], o->memFormat[15]);

    snprintf(buf, cap,
             "h=%lld via=%s kind=%s foreign=%d ownedByWic=%d refs=%d size=%dx%d rowBytes=%zu fmt=%s pixels=%s decoded=%d",
             (long long)h, via ? "foreignExt" : "slot", kind_name(o->kind),
             o->is_foreign, (o->is_foreign ? 0 : 1), o->refs, o->width, o->height, o->rowBytes, fmt,
             o->pixels ? "yes" : "null", o->decoded);
    return S_OK;
}
