/*
 * wic_stream.c —— 【K-499】最小 `IStream` 实现 + `CreateStream`/`InitializeFromMemory`
 *
 * 背景（K-498 实测）：
 *   SourceTree 的 XAML 里有 `<Image Source="pack://...">`，加载时走
 *   `ImageSourceConverter` → `BitmapFrame.CreateFromUriOrStream` → `BitmapDecoder`。
 *   `StreamAsIStream.IStreamFrom(memoryBuffer, bufferSize)` 先调
 *   `IWICImagingFactory_CreateStream`（**我们原来是 E_NOINTERFACE 空桩**），
 *   再调 `IWICStream_InitializeFromMemory`（**同样是空桩**）⇒ 返回 `IntPtr.Zero`
 *   ⇒ `InvalidOperationException: The codec cannot use the type of stream provided`。
 *
 * 做法：实现一个最小的 **COM IStream**（只读、内存后端），满足 `BitmapDecoder` 的需要：
 *   QueryInterface / AddRef / Release / Read / Seek / Stat / Clone
 *   其余方法返回 `E_NOTIMPL`（解码器不会调）。
 */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#define S_OK_            0
#define E_NOINTERFACE_   ((int32_t)0x80004002)
#define E_NOTIMPL_       ((int32_t)0x80004001)
#define E_POINTER_       ((int32_t)0x80004003)
#define E_INVALIDARG_    ((int32_t)0x80070057)
#define E_FAIL_          ((int32_t)0x80004005)

/* ── IStream vtable（14 项，按 Windows 顺序）──
 * 0 QueryInterface  1 AddRef  2 Release
 * 3 Read  4 Write  5 Seek  6 SetSize  7 CopyTo  8 Commit  9 Revert
 * 10 LockRegion 11 UnlockRegion 12 Stat 13 Clone
 */
typedef struct wic_stream {
    void **vtable;
    long refcount;
    const uint8_t *data;   /* 指向调用方给的内存（**不拷贝**，BitmapDecoder 持有流期间源缓冲区有效）*/
    uint64_t size;
    uint64_t pos;
} wic_stream;

/* STATSTG 的最小布局（WPF 只读 cbSize / type）*/
typedef struct { uint16_t *pwcsName; uint32_t type; uint64_t cbSize; uint64_t mtime, ctime, atime; uint32_t grfMode, grfLocksSupported; uint32_t clsid[4]; uint32_t grfStateBits, reserved; } wic_statstg;

static long s_qi(void *self, const void *riid, void **ppv)
{
    (void)riid;
    if (!ppv) return E_POINTER_;
    *ppv = self;                       /* 认所有 IID（解码器只查 IStream）*/
    return S_OK_;
}
static unsigned long s_addref(void *self){ return (unsigned long)__sync_add_and_fetch(&((wic_stream*)self)->refcount, 1); }
static unsigned long s_release(void *self){
    wic_stream *s = (wic_stream*)self;
    long n = __sync_sub_and_fetch(&s->refcount, 1);
    if (n <= 0) free(s);
    return (unsigned long)n;
}
static long s_read(void *self, void *pv, uint32_t cb, uint32_t *pcbRead)
{
    wic_stream *s = (wic_stream*)self;
    if (!pv) return E_POINTER_;
    uint64_t avail = (s->pos < s->size) ? (s->size - s->pos) : 0;
    uint32_t n = (cb < avail) ? cb : (uint32_t)avail;
    if (n) memcpy(pv, s->data + s->pos, n);
    s->pos += n;
    if (pcbRead) *pcbRead = n;
    return S_OK_;
}
static long s_seek(void *self, int64_t move, uint32_t origin, uint64_t *newPos)
{
    wic_stream *s = (wic_stream*)self;
    int64_t base;
    switch (origin) {
        case 0: base = 0; break;                    /* STREAM_SEEK_SET */
        case 1: base = (int64_t)s->pos; break;      /* CUR */
        case 2: base = (int64_t)s->size; break;     /* END */
        default: return E_INVALIDARG_;
    }
    int64_t np = base + move;
    if (np < 0) return E_INVALIDARG_;
    s->pos = (uint64_t)np;
    if (newPos) *newPos = s->pos;
    return S_OK_;
}
static long s_stat(void *self, wic_statstg *st, uint32_t flags)
{
    wic_stream *s = (wic_stream*)self;
    (void)flags;
    if (!st) return E_POINTER_;
    memset(st, 0, sizeof(*st));
    st->type = 2;                 /* STGTY_STREAM */
    st->cbSize = s->size;
    st->grfMode = 0;              /* 只读 */
    return S_OK_;
}
static long s_notimpl(void){ return E_NOTIMPL_; }

static void *g_vtable[14];
static int g_inited = 0;
static void ensure_vtable(void)
{
    if (g_inited) return;
    g_vtable[0] = (void*)s_qi;      g_vtable[1] = (void*)s_addref;  g_vtable[2] = (void*)s_release;
    g_vtable[3] = (void*)s_read;    g_vtable[4] = (void*)s_notimpl; g_vtable[5] = (void*)s_seek;
    g_vtable[6] = (void*)s_notimpl; g_vtable[7] = (void*)s_notimpl; g_vtable[8] = (void*)s_notimpl;
    g_vtable[9] = (void*)s_notimpl; g_vtable[10] = (void*)s_notimpl; g_vtable[11] = (void*)s_notimpl;
    g_vtable[12] = (void*)s_stat;   g_vtable[13] = (void*)s_notimpl;
    g_inited = 1;
}

/* ── IWICImagingFactory::CreateStream ───────────────────────────────────────
 * 签名（WindowsCodecs）：HRESULT CreateStream(IWICImagingFactory*, IWICStream **ppIStream)
 * 我们的 proxy 是 6 个 void* 参数（ABI 统一），只用 a1。
 */
int32_t IWICImagingFactory_CreateStream_Proxy(void *self, void *a1, void *a2, void *a3, void *a4, void *a5)
{
    (void)self; (void)a2; (void)a3; (void)a4; (void)a5;
    ensure_vtable();
    if (!a1) return E_POINTER_;
    wic_stream *s = (wic_stream*)calloc(1, sizeof(wic_stream));
    if (!s) return E_FAIL_;
    s->vtable = g_vtable;
    s->refcount = 1;
    s->data = NULL; s->size = 0; s->pos = 0;
    *(void**)a1 = s;
    if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-STREAM] CreateStream -> %p\n", (void*)s); fflush(stderr); }
    return S_OK_;
}

/* ── IWICStream::InitializeFromMemory ───────────────────────────────────────
 * 签名：HRESULT InitializeFromMemory(IWICStream*, BYTE *pbBuffer, DWORD cbBufferSize)
 */
int32_t IWICStream_InitializeFromMemory_Proxy(void *self, void *a1, void *a2, void *a3, void *a4, void *a5)
{
    (void)a3; (void)a4; (void)a5;
    if (!self) return E_POINTER_;
    wic_stream *s = (wic_stream*)self;
    s->data = (const uint8_t*)a1;
    s->size = (uint64_t)(uintptr_t)a2;
    s->pos = 0;
    if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-STREAM] InitializeFromMemory %p size=%llu\n", a1, (unsigned long long)s->size); fflush(stderr); }
    return S_OK_;
}

/* ── 【K-500】给 `wic_proxy.c` 的 `CreateDecoderFromStream` 用 ───────────────
 * `CreateDecoderFromStream` 原本只认 `LdpMilStreamRead` bridge（MIL 句柄），
 * 但我们新实现的 `wic_stream`（IStream）是**我们自己的对象**，bridge 不认识。
 * ⇒ 导出两个函数：判断是不是我们的流 / 直接拿数据指针 + 长度。
 */
int wic_stream_own(const void *p)
{
    if (!p) return 0;
    const wic_stream *s = (const wic_stream*)p;
    return s->vtable == g_vtable;
}
const uint8_t *wic_stream_data(const void *p, size_t *len)
{
    if (!p) { if (len) *len = 0; return NULL; }
    const wic_stream *s = (const wic_stream*)p;
    if (s->vtable != g_vtable) { if (len) *len = 0; return NULL; }
    if (len) *len = (size_t)s->size;
    return s->data;
}
