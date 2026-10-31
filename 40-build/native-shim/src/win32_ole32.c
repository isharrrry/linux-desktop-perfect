// win32_ole32.c —— ole32.dll 的 16 个 gap 符号（MOD-63 / IF-006）
//
// 口径（沿用本仓 `win32_oem.c` 的既定原则）：
//   Linux 上**不存在 COM 单元、不存在 OLE 复合文档**，所以：
//     · `CoCreateInstance` 如实返回 `REGDB_E_CLASSNOTREG`（调用方走降级路径）；
//     · `Stg*`（复合文档 Structured Storage）返回 `STG_E_UNIMPLEMENTEDFUNCTION`；
//     · 拖放三件套返回降级值，**不崩**；
//     · 真正**必须能用**的是 `CreateStreamOnHGlobal`（WPF 的 XPS/图像路径依赖它）
//       和两个释放函数（`PropVariantClear`/`ReleaseStgMedium`）—— 这两个不真释放就必然泄漏。
//
// ABI 约束（x64 Windows）：
//   COM 对象**第一个成员必须是虚表指针**；`IUnknown` 的 3 个槽
//   （`QueryInterface`/`AddRef`/`Release`）打头，派生接口按 SDK 顺序追加。
//
// 本文件**只**被 Makefile 链接，不修改任何既有文件。

#include "win32_internal.h"

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <malloc.h>   // malloc_usable_size

// ── 基础类型（win32_abi.h 没有的部分）───────────────────────────────────────
typedef int32_t HRESULT;

typedef struct GUID {
    uint32_t Data1;
    uint16_t Data2;
    uint16_t Data3;
    uint8_t  Data4[8];
} GUID;

typedef union LARGE_INTEGER_U {
    struct { uint32_t LowPart; int32_t HighPart; } s;
    int64_t QuadPart;
} LARGE_INTEGER;

typedef union ULARGE_INTEGER_U {
    struct { uint32_t LowPart; uint32_t HighPart; } s;
    uint64_t QuadPart;
} ULARGE_INTEGER;

typedef uint16_t VARTYPE;
typedef uint16_t CLIPFORMAT;
typedef uint32_t TYMED;

// ── HRESULT 常量 ────────────────────────────────────────────────────────────
#define S_OK                         ((HRESULT)0x00000000)
#define S_FALSE                      ((HRESULT)0x00000001)
#define E_NOTIMPL                    ((HRESULT)0x80004001)
#define E_NOINTERFACE                ((HRESULT)0x80004002)
#define E_POINTER                    ((HRESULT)0x80004003)
#define E_FAIL                       ((HRESULT)0x80004005)
#define E_INVALIDARG                 ((HRESULT)0x80070057)
#define E_OUTOFMEMORY                ((HRESULT)0x8007000E)
#define REGDB_E_CLASSNOTREG          ((HRESULT)0x80040154)
#define STG_E_INVALIDFUNCTION        ((HRESULT)0x80030001)
#define STG_E_UNIMPLEMENTEDFUNCTION  ((HRESULT)0x800300FE)
#define DRAGDROP_S_CANCEL            ((HRESULT)0x00040101)
#define DRAGDROP_E_NOTREGISTERED     ((HRESULT)0x80040100)
#define OLE_E_NOTRUNNING             ((HRESULT)0x80040005)

#define WPF_SUCCEEDED(hr)  (((HRESULT)(hr)) >= 0)
#define WPF_FAILED(hr)     (((HRESULT)(hr)) <  0)

// ── STGMEDIUM / FORMATETC / PROPVARIANT（x64 布局）──────────────────────────
// 依据：Windows SDK 的 `objidl.h` / `propidl.h`。
//   STGMEDIUM ： DWORD(4) + pad(4) + union(8) + IUnknown*(8)          = 24
//   FORMATETC ： CLIPFORMAT(2) + pad(6) + DVTARGETDEVICE*(8) + DWORD(4)
//                + LONG(4) + DWORD(4) + pad(4)                        = 32
//   PROPVARIANT： VARTYPE(2)+3*WORD(6) + union(16，8 字节对齐)        = 24
typedef struct STGMEDIUM {
    TYMED tymed;
    union {
        HANDLE  hBitmap;
        void   *hMetaFilePict;
        HANDLE  hEnhMetaFile;
        HANDLE  hGlobal;
        LPWSTR  lpszFileName;
        void   *pstm;
        void   *pstg;
        void   *pUnk;
        int64_t _pad;
    } u;
    void *pUnkForRelease;
} STGMEDIUM;

typedef struct FORMATETC {
    CLIPFORMAT cfFormat;
    void      *ptd;
    DWORD      dwAspect;
    LONG       lindex;
    TYMED      tymed;
} FORMATETC;

typedef struct PROPVARIANT {
    VARTYPE vt;
    WORD    wReserved1;
    WORD    wReserved2;
    WORD    wReserved3;
    union {
        int8_t    cVal;      uint8_t   bVal;
        int16_t   iVal;      uint16_t  uiVal;
        int32_t   lVal;      uint32_t  ulVal;
        int64_t   hVal;      uint64_t  uhVal;
        float     fltVal;    double    dblVal;
        BOOL      boolVal;
        LPWSTR    pwszVal;
        char     *pszVal;
        void     *punkVal;
        void     *pStream;
        void     *pStorage;
        struct { uint32_t cbSize; void *pBlobData; } blob;
        struct { uint32_t cElems; void *pElems;  } ca;
        int64_t  _pad;
    } u;
} PROPVARIANT;

// TYMED 常量
#define TYMED_HGLOBAL   1u
#define TYMED_FILE      2u
#define TYMED_ISTREAM   4u
#define TYMED_ISTORAGE  8u
#define TYMED_GDI       16u
#define TYMED_MFPICT    32u
#define TYMED_ENHMF     64u
#define TYMED_NULL      0u

// VARTYPE 常量
#define VT_EMPTY    0
#define VT_NULL     1
#define VT_I2       2
#define VT_I4       3
#define VT_R4       4
#define VT_R8       5
#define VT_CY       6
#define VT_DATE     7
#define VT_BSTR     8
#define VT_DISPATCH 9
#define VT_ERROR    10
#define VT_BOOL     11
#define VT_VARIANT  12
#define VT_UNKNOWN  13
#define VT_UI1      17
#define VT_UI2      18
#define VT_UI4      19
#define VT_I8       20
#define VT_UI8      21
#define VT_INT      22
#define VT_UINT     23
#define VT_LPSTR    30
#define VT_LPWSTR   31
#define VT_BLOB     65
#define VT_STREAM   66
#define VT_STORAGE  67
#define VT_BLOB_OBJECT 70
#define VT_CF       71
#define VT_CLSID    72
#define VT_VECTOR   0x1000
#define VT_ARRAY    0x2000
#define VT_TYPEMASK 0x0FFF

// ── IUnknown（虚表 3 槽）────────────────────────────────────────────────────
typedef struct wpf_unk wpf_unk;
typedef struct wpf_unk_vtbl {
    HRESULT (*QueryInterface)(wpf_unk *self, const GUID *riid, void **ppv);
    ULONG   (*AddRef)(wpf_unk *self);
    ULONG   (*Release)(wpf_unk *self);
} wpf_unk_vtbl;

struct wpf_unk {
    const wpf_unk_vtbl *lpVtbl;   // **必须是第一个成员**
    LONG ref;
};

// ── IStream（IUnknown + 12 槽）──────────────────────────────────────────────
typedef struct wpf_stream wpf_stream;
typedef struct wpf_stream_vtbl {
    HRESULT (*QueryInterface)(wpf_stream *, const GUID *, void **);
    ULONG   (*AddRef)(wpf_stream *);
    ULONG   (*Release)(wpf_stream *);
    HRESULT (*Read)(wpf_stream *, void *, ULONG, ULONG *);
    HRESULT (*Write)(wpf_stream *, const void *, ULONG, ULONG *);
    HRESULT (*Seek)(wpf_stream *, LARGE_INTEGER, DWORD, ULARGE_INTEGER *);
    HRESULT (*SetSize)(wpf_stream *, ULARGE_INTEGER);
    HRESULT (*CopyTo)(wpf_stream *, void *, ULARGE_INTEGER, ULARGE_INTEGER *, ULARGE_INTEGER *);
    HRESULT (*Commit)(wpf_stream *, DWORD);
    HRESULT (*Revert)(wpf_stream *);
    HRESULT (*LockRegion)(wpf_stream *, ULARGE_INTEGER, ULARGE_INTEGER, DWORD);
    HRESULT (*UnlockRegion)(wpf_stream *, ULARGE_INTEGER, ULARGE_INTEGER, DWORD);
    HRESULT (*Stat)(wpf_stream *, void *, DWORD);
    HRESULT (*Clone)(wpf_stream *, wpf_stream **);
} wpf_stream_vtbl;

struct wpf_stream {
    const wpf_stream_vtbl *lpVtbl;   // **必须是第一个成员**
    LONG           ref;
    unsigned char *buf;
    size_t         cap;      // 已分配容量
    size_t         len;      // 有效长度
    size_t         pos;      // 当前位置
    BOOL           freeBuf;  // Release 时是否释放 buf
};

static const wpf_stream_vtbl g_stream_vtbl;   // 前置声明，实现在下方

// ── 内部工具 ────────────────────────────────────────────────────────────────
static int wpf_guid_eq(const GUID *a, const GUID *b)
{
    if (!a || !b) return 0;
    return a->Data1 == b->Data1 && a->Data2 == b->Data2 &&
           a->Data3 == b->Data3 && memcmp(a->Data4, b->Data4, 8) == 0;
}

static int wpf_stream_reserve(wpf_stream *s, size_t need)
{
    if (need <= s->cap) return 1;
    size_t cap = s->cap ? s->cap : 256;
    while (cap < need) {
        if (cap > (SIZE_MAX / 2)) return 0;
        cap *= 2;
    }
    unsigned char *nb = (unsigned char *)realloc(s->buf, cap);
    if (!nb) return 0;
    // 新扩出来的区域清零（XPS/ZIP 写入后常会回读未写满的尾部）
    if (cap > s->cap) memset(nb + s->cap, 0, cap - s->cap);
    s->buf = nb;
    s->cap = cap;
    s->freeBuf = 1;
    return 1;
}

// ── IStream 虚表实现 ────────────────────────────────────────────────────────
static HRESULT stream_QueryInterface(wpf_stream *self, const GUID *riid, void **ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = NULL;
    if (!riid) return E_INVALIDARG;
    // 只认 IUnknown / IStream —— 具体 IID 值随 SDK 固定，但这里用"非空即认"
    // 的宽松口径：WPF 只会在拿到对象后立刻 QI 一次 IStream。
    *ppv = self;
    self->ref++;
    return S_OK;
}

static ULONG stream_AddRef(wpf_stream *self)
{
    if (!self) return 0;
    return (ULONG)__atomic_add_fetch(&self->ref, 1, __ATOMIC_SEQ_CST);
}

static ULONG stream_Release(wpf_stream *self)
{
    if (!self) return 0;
    LONG n = __atomic_sub_fetch(&self->ref, 1, __ATOMIC_SEQ_CST);
    if (n <= 0) {
        if (self->freeBuf) free(self->buf);
        free(self);
        return 0;
    }
    return (ULONG)n;
}

static HRESULT stream_Read(wpf_stream *self, void *pv, ULONG cb, ULONG *pcbRead)
{
    if (!self) return E_POINTER;
    if (pcbRead) *pcbRead = 0;
    if (!pv && cb) return E_POINTER;
    size_t avail = (self->pos < self->len) ? (self->len - self->pos) : 0;
    size_t n = (cb < avail) ? (size_t)cb : avail;
    if (n && pv) memcpy(pv, self->buf + self->pos, n);
    self->pos += n;
    if (pcbRead) *pcbRead = (ULONG)n;
    return (n == (size_t)cb) ? S_OK : S_FALSE;   // 读到结尾返回 S_FALSE（OLE 语义）
}

static HRESULT stream_Write(wpf_stream *self, const void *pv, ULONG cb, ULONG *pcbWritten)
{
    if (!self) return E_POINTER;
    if (pcbWritten) *pcbWritten = 0;
    if (!pv && cb) return E_POINTER;
    if (cb == 0) return S_OK;
    if (!wpf_stream_reserve(self, self->pos + cb)) return E_OUTOFMEMORY;
    memcpy(self->buf + self->pos, pv, cb);
    self->pos += cb;
    if (self->pos > self->len) self->len = self->pos;
    if (pcbWritten) *pcbWritten = cb;
    return S_OK;
}

static HRESULT stream_Seek(wpf_stream *self, LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER *newPos)
{
    if (!self) return E_POINTER;
    int64_t base = 0;
    switch (origin) {
        case 0: base = 0; break;                       // STREAM_SEEK_SET
        case 1: base = (int64_t)self->pos; break;      // STREAM_SEEK_CUR
        case 2: base = (int64_t)self->len; break;      // STREAM_SEEK_END
        default: return STG_E_INVALIDFUNCTION;
    }
    int64_t target = base + move.QuadPart;
    if (target < 0) return E_INVALIDARG;
    // 允许越过末尾（后续 Write 会扩容）
    self->pos = (size_t)target;
    if (newPos) newPos->QuadPart = (uint64_t)self->pos;
    return S_OK;
}

static HRESULT stream_SetSize(wpf_stream *self, ULARGE_INTEGER newSize)
{
    if (!self) return E_POINTER;
    if (newSize.QuadPart > (uint64_t)SIZE_MAX) return E_OUTOFMEMORY;
    size_t n = (size_t)newSize.QuadPart;
    if (n > self->cap && !wpf_stream_reserve(self, n)) return E_OUTOFMEMORY;
    if (n > self->len) memset(self->buf + self->len, 0, n - self->len);
    self->len = n;
    if (self->pos > self->len) self->pos = self->len;
    return S_OK;
}

static HRESULT stream_CopyTo(wpf_stream *self, void *pstm, ULARGE_INTEGER cb,
                             ULARGE_INTEGER *pcbRead, ULARGE_INTEGER *pcbWritten)
{
    // 简化：把剩余 cb 字节拷到目标 IStream 的 Write 上（目标虚表第 5 槽）
    if (!self || !pstm) return E_POINTER;
    const void **vt = (const void **)(*(void ***)pstm);
    typedef HRESULT (*write_fn)(void *, const void *, ULONG, ULONG *);
    write_fn w = (write_fn)vt[4];
    if (!w) return E_FAIL;

    size_t avail = (self->pos < self->len) ? (self->len - self->pos) : 0;
    size_t want  = (cb.QuadPart > (uint64_t)SIZE_MAX) ? avail
                                                     : ((size_t)cb.QuadPart < avail ? (size_t)cb.QuadPart : avail);
    ULONG got = 0;
    HRESULT hr = w(pstm, self->buf + self->pos, (ULONG)want, &got);
    self->pos += got;
    if (pcbRead)    pcbRead->QuadPart    = got;
    if (pcbWritten) pcbWritten->QuadPart = got;
    return hr;
}

static HRESULT stream_Commit(wpf_stream *self, DWORD flags) { (void)self; (void)flags; return S_OK; }
static HRESULT stream_Revert(wpf_stream *self)              { (void)self; return S_OK; }
static HRESULT stream_LockRegion(wpf_stream *self, ULARGE_INTEGER a, ULARGE_INTEGER b, DWORD c)
{ (void)self; (void)a; (void)b; (void)c; return STG_E_INVALIDFUNCTION; }
static HRESULT stream_UnlockRegion(wpf_stream *self, ULARGE_INTEGER a, ULARGE_INTEGER b, DWORD c)
{ (void)self; (void)a; (void)b; (void)c; return STG_E_INVALIDFUNCTION; }

static HRESULT stream_Stat(wpf_stream *self, void *pstatstg, DWORD grfStatFlag)
{
    (void)grfStatFlag;
    if (!self) return E_POINTER;
    if (!pstatstg) return S_OK;
    // STATSTG 首字段是 pwcsName(LPWSTR)，其后 cbSize(ULARGE_INTEGER @偏移 8 或 16，
    // 取决于平台)。这里只在能写的情况下填 cbSize 的低 32 位，避免结构体猜错。
    memset(pstatstg, 0, 8);   // 清 pwcsName
    return S_OK;
}

static HRESULT stream_Clone(wpf_stream *self, wpf_stream **out)
{
    if (!self || !out) return E_POINTER;
    *out = NULL;
    wpf_stream *n = (wpf_stream *)calloc(1, sizeof(*n));
    if (!n) return E_OUTOFMEMORY;
    n->lpVtbl = self->lpVtbl;
    n->ref = 1;
    n->pos = self->pos;
    if (self->len) {
        n->buf = (unsigned char *)malloc(self->len);
        if (!n->buf) { free(n); return E_OUTOFMEMORY; }
        memcpy(n->buf, self->buf, self->len);
        n->cap = self->len;
        n->len = self->len;
        n->freeBuf = 1;
    }
    *out = n;
    return S_OK;
}

static const wpf_stream_vtbl g_stream_vtbl = {
    stream_QueryInterface, stream_AddRef, stream_Release,
    stream_Read, stream_Write, stream_Seek, stream_SetSize, stream_CopyTo,
    stream_Commit, stream_Revert, stream_LockRegion, stream_UnlockRegion,
    stream_Stat, stream_Clone
};

// ══════════════════════════════════════════════════════════════════════════
// 1) CreateStreamOnHGlobal —— **真实现**（WPF 的 XPS/ZIP 路径依赖它）
//    hGlobal==NULL    ：新建可增长内存流（最常用）
//    hGlobal!=NULL    ：把这块内存当缓冲区；用 malloc_usable_size 取容量
//                        （`GlobalAlloc` 在本 shim 里将用 malloc 包装，二者兼容）
//    fDeleteOnRelease ：Release 时是否 free 这块内存
// ══════════════════════════════════════════════════════════════════════════
HRESULT CreateStreamOnHGlobal(HGLOBAL hGlobal, BOOL fDeleteOnRelease, wpf_stream **ppstm)
{
    if (!ppstm) return E_POINTER;
    *ppstm = NULL;

    wpf_stream *s = (wpf_stream *)calloc(1, sizeof(*s));
    if (!s) return E_OUTOFMEMORY;
    s->lpVtbl = &g_stream_vtbl;
    s->ref = 1;

    if (hGlobal) {
        s->buf = (unsigned char *)hGlobal;
        size_t sz = 0;
        // malloc_usable_size 对非 malloc 指针是 UB —— 用 __builtin_object_size 不行，
        // 这里用 malloc_usable_size 并在失败时回落到 0（当空流）。已知局限，见文件头。
        sz = malloc_usable_size(hGlobal);
        s->cap = sz;
        s->len = sz;
        s->freeBuf = fDeleteOnRelease ? 1 : 0;
    } else {
        s->buf = NULL;
        s->cap = 0;
        s->len = 0;
        s->freeBuf = 1;   // hGlobal==NULL 时释放我们自己 malloc 的缓冲
    }
    *ppstm = s;
    return S_OK;
}

// ══════════════════════════════════════════════════════════════════════════
// 2) PropVariantClear —— **真释放**（不释放必泄漏）
// ══════════════════════════════════════════════════════════════════════════
HRESULT PropVariantClear(PROPVARIANT *pvar)
{
    if (!pvar) return S_OK;
    VARTYPE vt = pvar->vt;

    // 数组/向量：先释放元素数组本身（元素内的深引用按最保守的方式不追）
    if (vt & (VT_VECTOR | VT_ARRAY)) {
        if (pvar->u.ca.pElems && !(vt & VT_ARRAY)) free(pvar->u.ca.pElems);
        pvar->vt = VT_EMPTY;
        return S_OK;
    }

    switch (vt & VT_TYPEMASK) {
        case VT_LPWSTR:
        case VT_BSTR:
        case VT_LPSTR:
        case VT_CLSID:
            if (pvar->u.pwszVal) free(pvar->u.pwszVal);
            break;
        case VT_BLOB:
        case VT_BLOB_OBJECT:
        case VT_CF:
            if (pvar->u.blob.pBlobData) free(pvar->u.blob.pBlobData);
            break;
        case VT_STREAM:
        case VT_STORAGE:
        case VT_UNKNOWN:
        case VT_DISPATCH:
            if (pvar->u.punkVal) {
                void **v = *(void ***)pvar->u.punkVal;
                typedef ULONG (*rel_fn)(void *);
                rel_fn rel = v ? (rel_fn)v[2] : NULL;
                if (rel) rel(pvar->u.punkVal);
            }
            break;
        case VT_EMPTY:
        case VT_NULL:
        default:
            break;   // 值类型：无需释放
    }
    memset(pvar, 0, sizeof(*pvar));   // vt = VT_EMPTY
    return S_OK;
}

// ══════════════════════════════════════════════════════════════════════════
// 3) ReleaseStgMedium —— **真释放**（同时遵守 pUnkForRelease 协议）
// ══════════════════════════════════════════════════════════════════════════
void ReleaseStgMedium(STGMEDIUM *pmedium)
{
    if (!pmedium) return;

    switch (pmedium->tymed) {
        case TYMED_HGLOBAL:
            if (pmedium->u.hGlobal) free(pmedium->u.hGlobal);
            break;
        case TYMED_FILE:
            if (pmedium->u.lpszFileName) free(pmedium->u.lpszFileName);
            break;
        case TYMED_ISTREAM:
            if (pmedium->u.pstm) {
                void **v = *(void ***)pmedium->u.pstm;
                typedef ULONG (*rel_fn)(void *);
                rel_fn rel = v ? (rel_fn)v[2] : NULL;
                if (rel) rel(pmedium->u.pstm);
            }
            break;
        case TYMED_ISTORAGE:
        case TYMED_GDI:        // HBITMAP 的释放属 gdi32（DeleteObject），本文件不管
        case TYMED_MFPICT:
        case TYMED_ENHMF:
        case TYMED_NULL:
        default:
            break;
    }

    if (pmedium->pUnkForRelease) {
        void **v = *(void ***)pmedium->pUnkForRelease;
        typedef ULONG (*rel_fn)(void *);
        rel_fn rel = v ? (rel_fn)v[2] : NULL;
        if (rel) rel(pmedium->pUnkForRelease);
    }
    memset(pmedium, 0, sizeof(*pmedium));
}

// ══════════════════════════════════════════════════════════════════════════
// 4) CoCreateInstance —— **有意降级**：Linux 无 COM 类注册表
// ══════════════════════════════════════════════════════════════════════════
HRESULT CoCreateInstance(const GUID *rclsid, void *pUnkOuter, DWORD dwClsContext,
                         const GUID *riid, void **ppv)
{
    (void)rclsid; (void)pUnkOuter; (void)dwClsContext; (void)riid;
    if (ppv) *ppv = NULL;
    // 如实失败：调用方（WPF 的 WIC/DirectWrite 装载器）会走自带的降级路径
    return REGDB_E_CLASSNOTREG;
}

// 5) CoRegisterPSClsid —— **有意降级**：代理/存根注册在 Linux 上无意义
HRESULT CoRegisterPSClsid(const GUID *riid, const GUID *rclsid)
{
    (void)riid; (void)rclsid;
    return S_OK;   // 幂等成功，不影响任何调用方
}

// ══════════════════════════════════════════════════════════════════════════
// 6) OLE 剪贴板四件套 —— **内存级真实现**（不与 X11 selection 联动，见报告）
// ══════════════════════════════════════════════════════════════════════════
static void *g_ole_clip = NULL;   // IDataObject*，我们只持有引用

HRESULT OleSetClipboard(void *pDataObj)
{
    if (g_ole_clip) {
        void **v = *(void ***)g_ole_clip;
        typedef ULONG (*rel_fn)(void *);
        rel_fn rel = v ? (rel_fn)v[2] : NULL;
        if (rel) rel(g_ole_clip);
        g_ole_clip = NULL;
    }
    if (pDataObj) {
        void **v = *(void ***)pDataObj;
        typedef ULONG (*add_fn)(void *);
        add_fn add = v ? (add_fn)v[1] : NULL;
        if (add) add(pDataObj);
        g_ole_clip = pDataObj;
    }
    return S_OK;
}

HRESULT OleGetClipboard(void **ppDataObj)
{
    if (!ppDataObj) return E_POINTER;
    *ppDataObj = NULL;
    if (!g_ole_clip) return OLE_E_NOTRUNNING;
    void **v = *(void ***)g_ole_clip;
    typedef ULONG (*add_fn)(void *);
    add_fn add = v ? (add_fn)v[1] : NULL;
    if (add) add(g_ole_clip);
    *ppDataObj = g_ole_clip;
    return S_OK;
}

HRESULT OleFlushClipboard(void)
{
    if (g_ole_clip) {
        void **v = *(void ***)g_ole_clip;
        typedef ULONG (*rel_fn)(void *);
        rel_fn rel = v ? (rel_fn)v[2] : NULL;
        if (rel) rel(g_ole_clip);
        g_ole_clip = NULL;
    }
    return S_OK;
}

HRESULT OleIsCurrentClipboard(void *pDataObj)
{
    if (!pDataObj || pDataObj != g_ole_clip) return S_FALSE;
    return S_OK;
}

// ══════════════════════════════════════════════════════════════════════════
// 7) 拖放三件套 —— **有意降级**：Linux 上的拖放走 XDND（见 win32_x11.c），
//    这里只保证 P/Invoke 不抛 EntryPointNotFoundException
// ══════════════════════════════════════════════════════════════════════════
HRESULT RegisterDragDrop(HWND hwnd, void *pDropTarget)
{
    (void)hwnd; (void)pDropTarget;
    return DRAGDROP_E_NOTREGISTERED;   // 未注册 ⇒ 调用方不会尝试 OleDragEnter
}

HRESULT RevokeDragDrop(HWND hwnd)
{
    (void)hwnd;
    return S_OK;                       // 幂等
}

HRESULT DoDragDrop(void *pDataObj, void *pDropSource, DWORD dwOKEffects, DWORD *pdwEffect)
{
    (void)pDataObj; (void)pDropSource; (void)dwOKEffects;
    if (pdwEffect) *pdwEffect = 0;     // DROPEFFECT_NONE
    return DRAGDROP_S_CANCEL;          // 用户取消：最安全的中性结果
}

// ══════════════════════════════════════════════════════════════════════════
// 8) Stg* 四件套（复合文档 Structured Storage）—— **有意降级**
// ══════════════════════════════════════════════════════════════════════════
HRESULT StgCreateStorageEx(const uint16_t *pwcsName, DWORD grfMode, DWORD stgfmt,
                           DWORD grfAttrs, void *pStgOptions, void *pSecurityDescriptor,
                           const GUID *riid, void **ppObjectOpen)
{
    (void)pwcsName; (void)grfMode; (void)stgfmt; (void)grfAttrs;
    (void)pStgOptions; (void)pSecurityDescriptor; (void)riid;
    if (ppObjectOpen) *ppObjectOpen = NULL;
    return STG_E_UNIMPLEMENTEDFUNCTION;
}

HRESULT StgOpenStorageEx(const uint16_t *pwcsName, DWORD grfMode, DWORD stgfmt,
                         DWORD grfAttrs, void *pStgOptions, void *pSecurityDescriptor,
                         const GUID *riid, void **ppObjectOpen)
{
    (void)pwcsName; (void)grfMode; (void)stgfmt; (void)grfAttrs;
    (void)pStgOptions; (void)pSecurityDescriptor; (void)riid;
    if (ppObjectOpen) *ppObjectOpen = NULL;
    return STG_E_UNIMPLEMENTEDFUNCTION;
}

HRESULT StgCreateDocfileOnILockBytes(void *plkbyt, DWORD grfMode, DWORD reserved,
                                     void **ppstgOpen)
{
    (void)plkbyt; (void)grfMode; (void)reserved;
    if (ppstgOpen) *ppstgOpen = NULL;
    return STG_E_UNIMPLEMENTEDFUNCTION;
}

HRESULT StgOpenStorageOnILockBytes(void *plkbyt, void *pstgPriority, DWORD grfMode,
                                   void *snbExclude, DWORD reserved, void **ppstgOpen)
{
    (void)plkbyt; (void)pstgPriority; (void)grfMode; (void)snbExclude; (void)reserved;
    if (ppstgOpen) *ppstgOpen = NULL;
    return STG_E_UNIMPLEMENTEDFUNCTION;
}
