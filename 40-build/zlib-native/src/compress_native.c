// ── 【清算 / K-185】`libSystem.IO.Compression.Native` 的 zlib 适配层 ───────────
//
// 为什么需要这个独立 .so：
//   .NET 的 `System.IO.Compression` 通过 `[DllImport("libSystem.IO.Compression.Native")]`
//   调这 8 个符号（K-184 用 Cecil 按 EntryPoint 扫出来的）。库名带 `lib` 前缀 ——
//   所以**不能塞进 `libwpfwin32.so`**（那是另一个库名），必须单独出。
//   .NET 运行时**并不自带**这个 .so（`find $DOTNET_ROOT -name "*Compression*Native*"` 为空）。
//
// 为什么不能直接转发到 zlib：
//   .NET 的 `ZLibNative/ZStream` 是**简化结构（40 字节 / 6 字段）**，
//   zlib 的 `z_stream` 是 **112 字节 / 14 字段**，偏移完全不同：
//     .NET  +0 nextIn / +8 nextOut / +16 msg / +24 internalState / +32 availIn / +36 availOut
//     zlib  +0 next_in / +8 avail_in / +16 total_in / +24 next_out / +32 avail_out / +40 total_out
//           / +48 msg / +56 state / +64 zalloc / +72 zfree / +80 opaque / +88 data_type
//           / +96 adler / +104 reserved
//   ⇒ 必须写适配层：每次调用前后在两侧同步那 4 个字段，真正的 `z_stream` 挂在 `internalState`。
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <zlib.h>

// .NET 的 ZStream（40 字节，布局见上）
typedef struct {
    void    *nextIn;
    void    *nextOut;
    void    *msg;
    void    *internalState;   // ← 我们挂 wpf_zctx 的地方
    uint32_t availIn;
    uint32_t availOut;
} wpf_net_zstream;

typedef struct {
    z_stream z;
    int      kind;            /* 1 = deflate, 2 = inflate */
} wpf_zctx;

/* .NET 的 ErrorCode 枚举（zlib 的返回值映射过去；0 = Ok） */
typedef enum {
    WPF_Z_OK = 0, WPF_Z_STREAM_END = 1, WPF_Z_NEED_DICT = 2,
    WPF_Z_ERRNO = -1, WPF_Z_STREAM_ERROR = -2, WPF_Z_DATA_ERROR = -3,
    WPF_Z_MEM_ERROR = -4, WPF_Z_BUF_ERROR = -5, WPF_Z_VERSION_ERROR = -6,
} wpf_zcode;

static void wpf_sync_in(wpf_zctx *c, wpf_net_zstream *s)
{
    c->z.next_in  = (Bytef *)s->nextIn;
    c->z.avail_in = s->availIn;
    c->z.next_out  = (Bytef *)s->nextOut;
    c->z.avail_out = s->availOut;
}

static void wpf_sync_out(wpf_zctx *c, wpf_net_zstream *s)
{
    s->nextIn    = (void *)c->z.next_in;
    s->availIn   = c->z.avail_in;
    s->nextOut   = (void *)c->z.next_out;
    s->availOut  = c->z.avail_out;
    s->msg       = (void *)c->z.msg;
    s->internalState = c;   /* 保持挂载点 */
}

int32_t CompressionNative_DeflateInit2_(wpf_net_zstream *s, int level, int method,
                                        int windowBits, int memLevel, int strategy)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)calloc(1, sizeof(*c));
    if (!c) return WPF_Z_MEM_ERROR;
    c->kind = 1;
    int rc = deflateInit2(&c->z, level, method, windowBits, memLevel, strategy);
    if (rc != Z_OK) { free(c); return (int32_t)rc; }
    s->internalState = c;
    s->msg = NULL;
    return WPF_Z_OK;
}

int32_t CompressionNative_Deflate(wpf_net_zstream *s, int flush)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)s->internalState;
    if (!c || c->kind != 1) return WPF_Z_STREAM_ERROR;
    wpf_sync_in(c, s);
    int rc = deflate(&c->z, flush);
    wpf_sync_out(c, s);
    return (int32_t)rc;
}

int32_t CompressionNative_DeflateEnd(wpf_net_zstream *s)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)s->internalState;
    if (!c || c->kind != 1) return WPF_Z_STREAM_ERROR;
    int rc = deflateEnd(&c->z);
    free(c);
    s->internalState = NULL;
    return (int32_t)rc;
}

int32_t CompressionNative_InflateInit2_(wpf_net_zstream *s, int windowBits)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)calloc(1, sizeof(*c));
    if (!c) return WPF_Z_MEM_ERROR;
    c->kind = 2;
    int rc = inflateInit2(&c->z, windowBits);
    if (rc != Z_OK) { free(c); return (int32_t)rc; }
    s->internalState = c;
    s->msg = NULL;
    return WPF_Z_OK;
}

int32_t CompressionNative_InflateReset2_(wpf_net_zstream *s, int windowBits)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)s->internalState;
    if (!c || c->kind != 2) return WPF_Z_STREAM_ERROR;
    return (int32_t)inflateReset2(&c->z, windowBits);
}

int32_t CompressionNative_Inflate(wpf_net_zstream *s, int flush)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)s->internalState;
    if (!c || c->kind != 2) return WPF_Z_STREAM_ERROR;
    wpf_sync_in(c, s);
    int rc = inflate(&c->z, flush);
    wpf_sync_out(c, s);
    return (int32_t)rc;
}

int32_t CompressionNative_InflateEnd(wpf_net_zstream *s)
{
    if (!s) return WPF_Z_STREAM_ERROR;
    wpf_zctx *c = (wpf_zctx *)s->internalState;
    if (!c || c->kind != 2) return WPF_Z_STREAM_ERROR;
    int rc = inflateEnd(&c->z);
    free(c);
    s->internalState = NULL;
    return (int32_t)rc;
}

uint32_t CompressionNative_Crc32(uint32_t crc, const uint8_t *buffer, int32_t len)
{
    return (uint32_t)crc32((uLong)crc, (const Bytef *)buffer, (uInt)len);
}
