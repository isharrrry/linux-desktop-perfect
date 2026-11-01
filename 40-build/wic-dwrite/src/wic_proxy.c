// libwpfwic.so —— WindowsCodecs / mscms 的最小面（D-018 的骨架）
//
// 【口径】WPF 的 WIC 调用是**扁平 P/Invoke**（`DllImport(WindowsCodecs, EntryPoint="IWICImagingFactory_CreateDecoderFromStream_Proxy")`，
//   参数是 `IntPtr` 句柄），不是 ComImport 接口方法 —— 见
//   `PresentationCore/System/Windows/Media/UnsafeNativeMethodsMilCoreApi.cs` 的 `WICImagingFactory` 类。
//   所以这一层可以纯 C 实现，不需要 COM。
//
// 【已有 / 未做】
//   已有：`WICCreateImagingFactory_Proxy` 返回一个**不透明工厂句柄**（本文件分配的标记对象），
//         让 WPF 的"工厂创建"这一步能过；其余入口一律**明确返回 E_NOTIMPL**（不返回假成功）。
//   未做：解码器/编码器/帧/像素/元数据/颜色管理（D-018）。下一步是在这里接 PNG/JPEG 解码
//         （IStream 也是 COM 接口，需要先做一个自管的 IStream 模拟）。
//
// 【诚实纪律】任何未实现的入口都返回 `E_NOTIMPL(0x80004001)` 并 `SetLastError`，绝不返回"看起来有效的假句柄"。

#define _GNU_SOURCE
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include "png_decode.h"

// 统一的分派：按文件头选解码器（PNG 签名 / BMP "BM"）。新增格式在这里加一行。
int ldp_bmp_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap);
int ldp_is_bmp(const uint8_t *data, size_t len);
int ldp_gif_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap);
int ldp_is_gif(const uint8_t *data, size_t len);
int ldp_is_gif89(const uint8_t *data, size_t len);
int ldp_tiff_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap);
int ldp_is_tiff(const uint8_t *data, size_t len);
int32_t ldp_jpeg_decode(const uint8_t *data, size_t len, ldp_png_image *out, char *error, size_t cap);
int ldp_is_jpeg(const uint8_t *data, size_t len);
#define COLOR_TRANSFORM_MAGIC_ 0x4c445433u   /* LDT3，与 mscms.c 一致 */

static int32_t wic_decode_any(const uint8_t *buf, size_t len, ldp_png_image *img, char *err, size_t cap)
{
    if (ldp_is_bmp(buf, len)) return ldp_bmp_decode(buf, len, img, err, cap);
    if (ldp_is_gif(buf, len) || ldp_is_gif89(buf, len)) return ldp_gif_decode(buf, len, img, err, cap);
    if (ldp_is_tiff(buf, len)) return ldp_tiff_decode(buf, len, img, err, cap);
    // JPEG：解析骨架已就位（SOF0/DQT/DHT/SOS + 霍夫曼 + IDCT），但 MCU 组装/上采样
    //   还没接上 ⇒ 这里会返回 E_NOTIMPL 并带明确原因（比"签名不匹配"更准确）。
    if (ldp_is_jpeg(buf, len)) return ldp_jpeg_decode(buf, len, img, err, cap);
    return ldp_png_decode(buf, len, img, err, cap);
}

#define E_NOTIMPL_ 0x80004001
#define S_OK_      0

// 工厂句柄：本层自管的标记对象（WPF 只把它原样传回来）
typedef struct { uint32_t magic; } wic_factory;
#define WIC_FACTORY_MAGIC 0x57494346u   /* 'WICF' */

int32_t WICCreateImagingFactory_Proxy(uint32_t sdkVersion, void **factory)
{
    (void)sdkVersion;
    if (!factory) return E_NOTIMPL_;
    wic_factory *f = (wic_factory *)malloc(sizeof(wic_factory));
    if (!f) return E_NOTIMPL_;
    f->magic = WIC_FACTORY_MAGIC;
    *factory = f;
    return S_OK_;
}

int wpf_wic_factory_valid(const void *p)
{
    const wic_factory *f = (const wic_factory *)p;
    return (f && f->magic == WIC_FACTORY_MAGIC) ? 1 : 0;
}

// ── PNG 解码器对象（D-018 第二步）─────────────────────────────────────────────
// WPF 的主路径是 `CreateDecoderFromStream`（IStream 由 milcore 的 MILCreateStream 造），
// 这里先实现**文件路径**入口（`CreateDecoderFromFilename`）把解码链路打通：
// 探针可以直接验证"PNG 能解出正确像素"，之后再把 stream 入口接到同一个解码器上。
// 对象头 wic_object 定义在 png_decode.h（mscms.c 也要用同一套跟踪）。
typedef struct wic_decoder {
    wic_object hdr;            // 必须是第一个字段（与 ldp_color_transform 共用同一个跟踪机制）
    ldp_png_image img;
} wic_decoder;
#define WIC_DECODER_MAGIC 0x57494344u   /* 'WICD' */

static wic_object *s_live = NULL;
static int s_live_count = 0;
static int s_peak_count = 0;
static pthread_mutex_t s_live_lock = PTHREAD_MUTEX_INITIALIZER;

void wic_track_add(void *obj)
{
    wic_object *d = (wic_object *)obj;
    pthread_mutex_lock(&s_live_lock);
    d->refcount = 1;
    d->next = s_live;
    s_live = d;
    s_live_count++;
    if (s_live_count > s_peak_count) s_peak_count = s_live_count;
    pthread_mutex_unlock(&s_live_lock);
}

static int wic_track_drop(wic_object *d)
{
    pthread_mutex_lock(&s_live_lock);
    wic_object **pp = &s_live;
    while (*pp && *pp != d) pp = &(*pp)->next;
    if (*pp == d) { *pp = d->next; s_live_count--; }
    pthread_mutex_unlock(&s_live_lock);
    return 1;
}



int32_t IWICImagingFactory_CreateDecoderFromFilename_Proxy(void *factory, const uint16_t *wpath,
                                                            void *guidVendor, uint32_t access,
                                                            uint32_t metadataOptions, void **out)
{
    (void)guidVendor; (void)access; (void)metadataOptions;
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    if (!wpf_wic_factory_valid(factory) || !wpath) return 0x80070057;   /* E_INVALIDARG */
    // UTF-16 → UTF-8（WPF 传的是 LPCWSTR）
    char path[4096];
    size_t o = 0;
    for (size_t i = 0; wpath[i] && o + 4 < sizeof(path); ++i) {
        uint32_t c = wpath[i];
        if (c < 0x80) path[o++] = (char)c;
        else if (c < 0x800) { path[o++] = (char)(0xC0 | (c >> 6)); path[o++] = (char)(0x80 | (c & 0x3F)); }
        else { path[o++] = (char)(0xE0 | (c >> 12)); path[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); path[o++] = (char)(0x80 | (c & 0x3F)); }
    }
    path[o] = '\0';
    FILE *f = fopen(path, "rb");
    if (!f) return 0x80070002;   /* E_FILE_NOT_FOUND */
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *buf = (uint8_t *)malloc((size_t)(n > 0 ? n : 1));
    if (!buf) { fclose(f); return E_NOTIMPL_; }
    size_t rd = fread(buf, 1, (size_t)(n > 0 ? n : 0), f);
    fclose(f);
    wic_decoder *d = (wic_decoder *)calloc(1, sizeof(wic_decoder));
    if (!d) { free(buf); return E_NOTIMPL_; }
    char err[256] = { 0 };
    int32_t hr = wic_decode_any(buf, rd, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->hdr.magic = WIC_DECODER_MAGIC;
    wic_track_add(d);
    *out = d;
    return S_OK_;
}

int32_t IWICBitmapDecoder_GetFrame_Proxy(void *decoder, uint32_t index, void **frame)
{
    if (!frame) return E_NOTIMPL_;
    *frame = NULL;
    wic_decoder *d = (wic_decoder *)decoder;
    if (!d || d->hdr.magic != WIC_DECODER_MAGIC) return 0x80070057;
    if (index != 0) return E_NOTIMPL_;   /* 只支持单帧 */
    *frame = d;                          /* 帧就是解码器本身（单图 PNG 的常见口径） */
    return S_OK_;
}

// 位图源三入口要同时接受两类对象：解码器（WICD）与色彩转换对象（LDT3）。
//   两者共用同一个对象头（wic_object），所以这里只按"头 + 载荷布局"分派。
typedef struct { wic_object hdr; uint8_t *bgra; uint32_t width, height; } ldp_ct_view;

// ── 【K-422】指针可读性检查 ──────────────────────────────────────────────
//   WPF 托管侧会通过 dlsym 调这些 `_Proxy` 入口并把「native 句柄」当第一个参数传进来。
//   实测（K-417）：Ink 样本里传进来的 `source = 0x20000005` —— 一个**明显不是堆指针**的值
//   （`0x20000000 | 0x5`，像是标志位/枚举值被误当成指针），而 `*_Proxy` 直接做
//   `o->magic` 解引用 ⇒ SIGSEGV（`si_code=1 SEGV_MAPERR`）。
//
//   ⇒ 在**每个** `_Proxy` 入口先调本函数：地址必须是「可能有效的用户态指针」。
//     这不能替代上游修复（真正的 bug 在 WPF 托管的句柄生成），但能让
//     **坏参数变成干净的 HRESULT 失败**，而不是进程崩溃。
//
//   判定用 `mincore(2)`：它对未映射页返回 ENOMEM，且**不会**触发 SIGSEGV。
static int wic_ptr_readable(const void *p)
{
    if (!p) return 0;
    uintptr_t a = (uintptr_t)p;
    // 用户态指针的粗略范围：排除 0 附近、内核空间、以及明显的小整数
    if (a < 0x10000u) return 0;                 // 前 64KB（含 0x20000005 之外的常见小值）
    if (a > 0x7fffffffffffULL) return 0;         // 超过用户态上限
    // 按页检查：p 所在页必须已映射
    long ps = sysconf(_SC_PAGESIZE);
    if (ps <= 0) ps = 4096;
    void *page = (void *)(a & ~((uintptr_t)ps - 1));
    unsigned char vec = 0;
    if (mincore(page, (size_t)ps, &vec) != 0) return 0;   // ENOMEM = 未映射
    return 1;
}

int32_t IWICBitmapSource_GetSize_Proxy(void *source, uint32_t *w, uint32_t *h)
{
    if (!wic_ptr_readable(source)) return 0x80070057;
    wic_object *o = (wic_object *)source;
    if (o->magic == WIC_DECODER_MAGIC) {
        wic_decoder *d = (wic_decoder *)source;
        if (w) *w = d->img.width;
        if (h) *h = d->img.height;
        return S_OK_;
    }
    if (o->magic == COLOR_TRANSFORM_MAGIC_) {
        ldp_ct_view *t = (ldp_ct_view *)source;
        if (w) *w = t->width;
        if (h) *h = t->height;
        return S_OK_;
    }
    return 0x80070057;
}

int32_t IWICBitmapSource_GetPixelFormat_Proxy(void *source, void *guid)
{
    if (!wic_ptr_readable(source)) return 0x80070057;
    wic_object *o = (wic_object *)source;
    if (o->magic != WIC_DECODER_MAGIC && o->magic != COLOR_TRANSFORM_MAGIC_) return 0x80070057;
    // GUID_WICPixelFormat32bppBGRA = {6fddc324-4e03-4bfe-b185-3d77768dc90f}
    static const uint8_t bgra_guid[16] = {
        0x24, 0xc3, 0xdd, 0x6f, 0x03, 0x4e, 0xfe, 0x4b,
        0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0f
    };
    if (guid) memcpy(guid, bgra_guid, 16);
    return S_OK_;
}

int32_t IWICBitmapSource_CopyPixels_Proxy(void *source, const void *rect, uint32_t stride,
                                          uint32_t bufferSize, uint8_t *buffer)
{
    if (!source || !buffer) return 0x80070057;
    wic_object *o = (wic_object *)source;
    if (o->magic != WIC_DECODER_MAGIC && o->magic != COLOR_TRANSFORM_MAGIC_) return 0x80070057;
    uint32_t w, h;
    const uint8_t *src_bgra;
    if (o->magic == WIC_DECODER_MAGIC) {
        wic_decoder *d = (wic_decoder *)source;
        w = d->img.width; h = d->img.height; src_bgra = d->img.bgra;
    } else {
        ldp_ct_view *t = (ldp_ct_view *)source;
        w = t->width; h = t->height; src_bgra = t->bgra;
    }
    // rect：NULL 或"整幅"都按整幅拷；**部分矩形**明确不支持（返回 E_NOTIMPL，不做近似）
    uint32_t rx = 0, ry = 0, rw = w, rh = h;
    if (rect != NULL) {
        const int32_t *r = (const int32_t *)rect;
        if (r[0] != 0 || r[1] != 0 || r[2] != (int32_t)w || r[3] != (int32_t)h) return E_NOTIMPL_;
    }
    size_t row = (size_t)w * 4;
    if (stride < row || bufferSize < row * h) return 0x80070057;
    (void)rx; (void)ry; (void)rw; (void)rh;
    for (uint32_t y = 0; y < h; ++y)
        memcpy(buffer + (size_t)y * stride, src_bgra + (size_t)y * row, row);
    return S_OK_;
}

// ── 流入口（D-018 第三步）─────────────────────────────────────────────────────
// WPF 主路径：`CreateDecoderFromStream(factory, IStream*, …)`，而那个 `IStream*` 在本工程里
// 是 milcore 的 `MilStreamObject` 句柄 —— 读字节要通过 bridge 的扁平导出 `LdpMilStreamRead`。
// 这里用 dlopen/dlsym 在运行时拿到它（libwpfwic 不静态依赖 wpfgfx_cor3）。
typedef int32_t (*ldp_read_fn)(void *handle, uint8_t *buffer, uint32_t cb, uint32_t *cbRead);

static ldp_read_fn wic_stream_read_fn(void)
{
    static ldp_read_fn cached = (ldp_read_fn)-1;
    if (cached != (ldp_read_fn)-1) return cached;
    cached = NULL;
    // ①先查进程全局符号（探针用 -rdynamic 把 LdpMilStreamRead 导出在可执行文件里；真跑时
    //   bridge 的 so 可能以 RTLD_GLOBAL 加载，也能命中）
    cached = (ldp_read_fn)dlsym(RTLD_DEFAULT, "LdpMilStreamRead");
    if (cached) return cached;
    // ②再查已加载的 wpfgfx_cor3（.NET 通常用 RTLD_LOCAL 加载）
    void *h = dlopen("wpfgfx_cor3.so", RTLD_NOW | RTLD_NOLOAD);
    if (!h) h = dlopen("wpfgfx_cor3.dll", RTLD_NOW | RTLD_NOLOAD);
    if (!h) return NULL;
    cached = (ldp_read_fn)dlsym(h, "LdpMilStreamRead");
    return cached;
}

int32_t IWICImagingFactory_CreateDecoderFromStream_Proxy(void *factory, void *stream,
                                                          void *guidVendor, uint32_t metadataOptions,
                                                          void **out)
{
    (void)guidVendor; (void)metadataOptions;
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    if (!wpf_wic_factory_valid(factory) || !stream) return 0x80070057;
    ldp_read_fn read_fn = wic_stream_read_fn();
    if (!read_fn) return E_NOTIMPL_;   // bridge 不在（没有 LdpMilStreamRead）⇒ 诚实失败
    // 把流整段读出来（上限 64 MB，防止坏 handled 撑爆内存）
    size_t cap = 1 << 20, len = 0;
    uint8_t *buf = (uint8_t *)malloc(cap);
    if (!buf) return E_NOTIMPL_;
    for (;;) {
        if (len + 65536 > cap) {
            if (cap >= (64u << 20)) { free(buf); return E_NOTIMPL_; }
            cap *= 2;
            uint8_t *p = (uint8_t *)realloc(buf, cap);
            if (!p) { free(buf); return E_NOTIMPL_; }
            buf = p;
        }
        uint32_t got = 0;
        int32_t hr = read_fn(stream, buf + len, (uint32_t)(cap - len), &got);
        if (hr != 0) { free(buf); return hr; }
        if (got == 0) break;     // EOF
        len += got;
    }
    wic_decoder *d = (wic_decoder *)calloc(1, sizeof(wic_decoder));
    if (!d) { free(buf); return E_NOTIMPL_; }
    char err[256] = { 0 };
    int32_t hr = wic_decode_any(buf, len, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->hdr.magic = WIC_DECODER_MAGIC;
    wic_track_add(d);
    *out = d;
    return S_OK_;
}

// ── 文件句柄入口（WPF 的 FileStream 路径走这条）────────────────────────────────
// WPF 的 `BitmapDecoder` 在 `FileStream` 支持 SafeFileHandle 时会走
// `CreateDecoderFromFileHandle(factory, SafeFileHandle, ref Guid, flags, out decoder)`。
// Linux 上 SafeFileHandle 就是 **fd**（`CreateFileW` 在本 shim 里是 stub，WPF 用托管 FileStream）。
// 用 `pread` 从 0 读全文件（不改 fd 偏移，避免影响调用方后续读）。
int32_t IWICImagingFactory_CreateDecoderFromFileHandle_Proxy(void *factory, void *hFile,
                                                              void *guidVendor, uint32_t metadataFlags,
                                                              void **out)
{
    (void)guidVendor; (void)metadataFlags;
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    if (!wpf_wic_factory_valid(factory) || !hFile) return 0x80070057;
    int fd = (int)(intptr_t)hFile;
    off_t size = lseek(fd, 0, SEEK_END);
    if (size <= 0) return 0x80070057;
    if (lseek(fd, 0, SEEK_SET) < 0) return 0x80070057;
    uint8_t *buf = (uint8_t *)malloc((size_t)size);
    if (!buf) return E_NOTIMPL_;
    ssize_t got = read(fd, buf, (size_t)size);
    if (got != (ssize_t)size) { free(buf); return E_NOTIMPL_; }
    wic_decoder *d = (wic_decoder *)calloc(1, sizeof(wic_decoder));
    if (!d) { free(buf); return E_NOTIMPL_; }
    char err[256] = { 0 };
    int32_t hr = wic_decode_any(buf, (size_t)size, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->hdr.magic = WIC_DECODER_MAGIC;
    wic_track_add(d);
    *out = d;
    return S_OK_;
}

// ── 句柄归属/引用计数契约（bridge 的 MilExternalHandleBridge 要这 5 个导出）──────────
// 为什么必须有：WPF 把 WIC 返回的指针包进 `SafeMILHandle`，释放时走 `MILRelease` →
//   bridge 的 `MilExternalHandleBridge.TryReleaseExternal` 会 dlsym 这五个名字来决定
//   "这个句柄是不是 libwpfwic 的、该由谁回收"。缺它们 ⇒ bridge 归不了账 ⇒
//   `MILRelease` 返回 `E_HANDLE(0x80070006)`（实测：WIC 链路走到这一步就抛 COMException）。
int WicShim_OwnsHandle(void *handle)
{
    wic_object *d = (wic_object *)handle;
    if (!d) return 0;
    pthread_mutex_lock(&s_live_lock);
    int owned = 0;
    for (wic_object *it = s_live; it; it = it->next) if (it == d) { owned = 1; break; }
    pthread_mutex_unlock(&s_live_lock);
    return owned;
}

int WicShim_AddRef(void *handle)
{
    wic_object *d = (wic_object *)handle;
    if (!d) return 0;
    pthread_mutex_lock(&s_live_lock);
    d->refcount++;
    int n = d->refcount;
    pthread_mutex_unlock(&s_live_lock);
    return n;
}

int WicShim_Release(void *handle)
{
    wic_object *d = (wic_object *)handle;
    if (!d) return 0;
    pthread_mutex_lock(&s_live_lock);
    int n = --d->refcount;
    pthread_mutex_unlock(&s_live_lock);
    if (n > 0) return n;
    wic_track_drop(d);
    if (d->magic == WIC_DECODER_MAGIC) {
        ldp_png_free(&((wic_decoder *)d)->img);
    } else if (d->magic == 0x4c445433u /* LDT3，color transform */) {
        void **payload = (void **)((char *)d + sizeof(wic_object));   // bgra 指针紧随头部
        if (payload && *payload) { free(*payload); *payload = NULL; }
    }
    d->magic = 0;
    free(d);
    return 0;
}

int WicShim_HandleCount(void)
{
    pthread_mutex_lock(&s_live_lock);
    int n = s_live_count;
    pthread_mutex_unlock(&s_live_lock);
    return n;
}

int WicShim_PeakHandleCount(void)
{
    pthread_mutex_lock(&s_live_lock);
    int n = s_peak_count;
    pthread_mutex_unlock(&s_live_lock);
    return n;
}

// ── 组件信息（WPF 建好解码器后要它：GetDecoderInfo → IWICComponentInfo_*）─────────────
// 这些入口返回的是**编解码器元信息**（CLSID/作者/版本/友好名），不是像素；
// 按 WIC 的真实语义填 PNG 解码器的值（CLSID 用 Windows 的 PNG 解码器 CLSID，便于调用方比对）。
typedef struct { uint32_t magic; } wic_component_info;
#define WIC_INFO_MAGIC 0x57494349u   /* 'WICI' */

int32_t IWICBitmapDecoder_GetDecoderInfo_Proxy(void *decoder, void **out)
{
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    wic_decoder *d = (wic_decoder *)decoder;
    if (!d || d->hdr.magic != WIC_DECODER_MAGIC) return 0x80070057;
    // 本实现只有一个解码器（PNG）⇒ 组件信息可以是个静态单例（调用方只读）
    static wic_component_info s_info;
    s_info.magic = WIC_INFO_MAGIC;
    *out = &s_info;
    return S_OK_;
}

int32_t IWICComponentInfo_GetCLSID_Proxy(void *info, uint8_t *clsid)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC || !clsid) return 0x80070057;
    // CLSID_WICPngDecoder = {389ea17b-5078-4cde-b6ef-25c15175c751}
    static const uint8_t png_clsid[16] = {
        0x7b, 0xa1, 0x9e, 0x38, 0x78, 0x50, 0xde, 0x4c,
        0xb6, 0xef, 0x25, 0xc1, 0x51, 0x75, 0xc7, 0x51
    };
    memcpy(clsid, png_clsid, 16);
    return S_OK_;
}

// GetAuthor/GetVersion/GetSpecVersion/GetFriendlyName 的共同形状：
//   (THIS, cchBuf, StringBuilder(=wchar_t*), out actual)；cchBuf 是**含结尾 0** 的容量。
static int32_t copy_component_string(const char *src, uint32_t cchBuf, uint16_t *buf, uint32_t *actual)
{
    if (!actual) return 0x80070057;
    size_t n = strlen(src);
    *actual = (uint32_t)n + 1;   // 语义：需要的字符数（含结尾 0）
    if (!buf || cchBuf == 0) return S_OK_;   // 只问长度
    if (cchBuf < n + 1) return 0x80070057 + 0;  // 缓冲区不足（如实失败）
    for (size_t i = 0; i < n; ++i) buf[i] = (uint16_t)(unsigned char)src[i];
    buf[n] = 0;
    return S_OK_;
}

int32_t IWICComponentInfo_GetAuthor_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_component_string("LinuxDesktopPerfect (WIC shim)", cch, buf, actual);
}

int32_t IWICComponentInfo_GetVersion_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_component_string("1.0.0.0", cch, buf, actual);
}

int32_t IWICComponentInfo_GetSpecVersion_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_component_string("1.0.0.0", cch, buf, actual);
}

int32_t IWICComponentInfo_GetFriendlyName_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_component_string("PNG Decoder (LinuxDesktopPerfect)", cch, buf, actual);
}

// ── 编解码器能力信息（IWICBitmapCodecInfo_*）─────────────────────────────────
// WPF 拿到解码器后会问"这是什么容器/支持哪些 MIME/是否支持动画"等。对 PNG：
//   container=GUID_ContainerFormatPng、mime=image/png、ext=.png、lossless=true、动画/多帧=false。
static int32_t copy_ascii_string(const char *src, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    return copy_component_string(src, cch, buf, actual);
}

int32_t IWICBitmapCodecInfo_GetContainerFormat_Proxy(void *info, uint8_t *guid)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC || !guid) return 0x80070057;
    // GUID_ContainerFormatPng = {1b7cfaf4-713f-473c-bbcd-6137425faeaf}
    static const uint8_t png_container[16] = {
        0xf4, 0xaf, 0x7c, 0x1b, 0x3f, 0x71, 0x3c, 0x47,
        0xbb, 0xcd, 0x61, 0x37, 0x42, 0x5f, 0xae, 0xaf
    };
    memcpy(guid, png_container, 16);
    return S_OK_;
}

int32_t IWICBitmapCodecInfo_GetDeviceManufacturer_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_ascii_string("LinuxDesktopPerfect", cch, buf, actual);
}

int32_t IWICBitmapCodecInfo_GetDeviceModels_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_ascii_string("", cch, buf, actual);
}

int32_t IWICBitmapCodecInfo_GetMimeTypes_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_ascii_string("image/png", cch, buf, actual);
}

int32_t IWICBitmapCodecInfo_GetFileExtensions_Proxy(void *info, uint32_t cch, uint16_t *buf, uint32_t *actual)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    return copy_ascii_string(".png", cch, buf, actual);
}

// 这四个查询的签名相同：(THIS, out BOOL)
int32_t IWICBitmapCodecInfo_DoesSupportAnimation_Proxy(void *info, int32_t *supported)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    if (supported) *supported = 0;   // PNG（单帧）不支持动画
    return S_OK_;
}

int32_t IWICBitmapCodecInfo_DoesSupportLossless_Proxy(void *info, int32_t *supported)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    if (supported) *supported = 1;   // PNG 是无损
    return S_OK_;
}

int32_t IWICBitmapCodecInfo_DoesSupportMultiframe_Proxy(void *info, int32_t *supported)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    if (supported) *supported = 0;
    return S_OK_;
}

int32_t IWICBitmapCodecInfo_DoesSupportChroma_Proxy(void *info, int32_t *supported)
{
    wic_component_info *ci = (wic_component_info *)info;
    if (!ci || ci->magic != WIC_INFO_MAGIC) return 0x80070057;
    if (supported) *supported = 0;
    return S_OK_;
}

// ── ole32 的两个入口（WIC 组把 `ole32.dll` 映射到本 so）───────────────────────────
// resolver 的 `WicMappedLibraries` 把 `ole32.dll` 放进 **WIC 组**（理由见那里的三条裁定：
// PC 侧 ole32 声明只有 `CoInitialize/CoUninitialize` 两处、调用点是 WIC bootstrap 的 SafeHandle）。
// 所以这两个名字必须由 **libwpfwic.so** 导出 —— 修前没有 ⇒
// `EntryPointNotFoundException: CoInitialize in ole32.dll`（实测：WIC 链路一路补到编解码器
// 信息之后撞上它）。Linux 上没有 COM 单元，"初始化成功"是真话。
int32_t CoInitialize(void *pvReserved) { (void)pvReserved; return 0; }        /* S_OK */
int32_t CoInitializeEx(void *pvReserved, uint32_t flags) { (void)pvReserved; (void)flags; return 0; }
void    CoUninitialize(void) { }
void    CoFreeUnusedLibraries(void) { }

// ── 位图物化：CreateBitmapFromSource / CreateBitmapFromMemory ────────────────────
// WPF 的 `CachedBitmap.InitFromMemoryPtr` 会用这两个入口把"WIC 源"落成一块内存位图。
// 我们的语义：**拷贝像素**到新的 wic_decoder（引用计数各自独立），不共享 buffer。
int32_t IWICImagingFactory_CreateBitmapFromSource_Proxy(void *factory, void *source,
                                                         uint32_t options, void **out)
{
    (void)options;
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    if (!wpf_wic_factory_valid(factory) || !source) return 0x80070057;
    wic_decoder *src = (wic_decoder *)source;
    if (src->hdr.magic != WIC_DECODER_MAGIC) return 0x80070057;
    wic_decoder *d = (wic_decoder *)calloc(1, sizeof(wic_decoder));
    if (!d) return E_NOTIMPL_;
    size_t bytes = (size_t)src->img.width * src->img.height * 4;
    uint8_t *copy = (uint8_t *)malloc(bytes ? bytes : 1);
    if (!copy) { free(d); return E_NOTIMPL_; }
    if (src->img.bgra) memcpy(copy, src->img.bgra, bytes);
    d->img.width = src->img.width;
    d->img.height = src->img.height;
    d->img.has_alpha = src->img.has_alpha;
    d->img.bgra = copy;
    d->hdr.magic = WIC_DECODER_MAGIC;
    wic_track_add(d);
    *out = d;
    return S_OK_;
}

int32_t IWICImagingFactory_CreateBitmapFromMemory_Proxy(void *factory, uint32_t width, uint32_t height,
                                                         void *pixelFormatGuid, uint32_t stride,
                                                         uint32_t bufferSize, uint8_t *pixels,
                                                         void **out)
{
    if (!out) return E_NOTIMPL_;
    *out = NULL;
    if (!wpf_wic_factory_valid(factory) || !pixels || !width || !height) return 0x80070057;
    // 只接受 32bppBGRA（GUID {6fddc324-4e03-4bfe-b185-3d77768dc90f}）；其它格式明确失败
    if (pixelFormatGuid) {
        static const uint8_t bgra[16] = { 0x24,0xc3,0xdd,0x6f,0x03,0x4e,0xfe,0x4b,0xb1,0x85,0x3d,0x77,0x76,0x8d,0xc9,0x0f };
        if (memcmp(pixelFormatGuid, bgra, 16) != 0) return E_NOTIMPL_;
    }
    size_t row = (size_t)width * 4;
    if (stride < row || (size_t)bufferSize < row * height) return 0x80070057;
    wic_decoder *d = (wic_decoder *)calloc(1, sizeof(wic_decoder));
    if (!d) return E_NOTIMPL_;
    uint8_t *copy = (uint8_t *)malloc(row * height);
    if (!copy) { free(d); return E_NOTIMPL_; }
    for (uint32_t y = 0; y < height; ++y) memcpy(copy + y * row, pixels + (size_t)y * stride, row);
    d->img.width = width;
    d->img.height = height;
    d->img.has_alpha = 1;
    d->img.bgra = copy;
    d->hdr.magic = WIC_DECODER_MAGIC;
    wic_track_add(d);
    *out = d;
    return S_OK_;
}

// ── context → profile 字节 的自管表 ─────────────────────────────────────────────
// 【为什么自管】色彩转换要从两个 color context 取 profile 字节；bridge 的
//   `IWICColorContext_GetProfileBytes_Proxy` 在实测里返回 E_INVALIDARG（长度却回填正确），
//   与其依赖那条跨模块路径，不如在**本 shim 内部**留一份映射：登记 context 时把字节存下来。
typedef struct { void *ctx; uint8_t *data; uint32_t len; } wic_ctx_profile;
static wic_ctx_profile s_ctx_profiles[64];
static int s_ctx_profile_count;
static pthread_mutex_t s_ctx_lock = PTHREAD_MUTEX_INITIALIZER;

void wic_ctx_profile_set(void *ctx, const uint8_t *data, uint32_t len)
{
    if (!ctx || !data || len == 0) return;
    pthread_mutex_lock(&s_ctx_lock);
    for (int i = 0; i < s_ctx_profile_count; ++i) {
        if (s_ctx_profiles[i].ctx == ctx) {
            free(s_ctx_profiles[i].data);
            s_ctx_profiles[i].data = (uint8_t *)malloc(len);
            if (s_ctx_profiles[i].data) { memcpy(s_ctx_profiles[i].data, data, len); s_ctx_profiles[i].len = len; }
            else { s_ctx_profiles[i].len = 0; }
            pthread_mutex_unlock(&s_ctx_lock);
            return;
        }
    }
    if (s_ctx_profile_count < 64) {
        wic_ctx_profile *e = &s_ctx_profiles[s_ctx_profile_count++];
        e->ctx = ctx;
        e->data = (uint8_t *)malloc(len);
        if (e->data) { memcpy(e->data, data, len); e->len = len; }
    }
    pthread_mutex_unlock(&s_ctx_lock);
}

// 供 mscms.c 取（返回内部缓冲的地址与长度；调用方只读）
const uint8_t *wic_ctx_profile_get(void *ctx, uint32_t *len)
{
    const uint8_t *p = NULL;
    pthread_mutex_lock(&s_ctx_lock);
    for (int i = 0; i < s_ctx_profile_count; ++i) {
        if (s_ctx_profiles[i].ctx == ctx) { p = s_ctx_profiles[i].data; if (len) *len = s_ctx_profiles[i].len; break; }
    }
    pthread_mutex_unlock(&s_ctx_lock);
    return p;
}

// ── 颜色上下文（ICC）────────────────────────────────────────────────────────────
// 两个入口是一对：`WICCreateColorContext` 造**空**上下文，`InitializeFromMemory` 再填 profile。
// WIC 的语义就是"填充已有对象"（不是创建），所以句柄统一登记在 bridge 的 MilColorContextTable，
// 由 `LdpMilColorContextFromProfile/SetProfile` 两个导出操作。
// 找到 bridge（wpfgfx_cor3.so）在本进程里的**绝对路径**再 dlopen。
// 【为什么必须走 /proc/self/maps】.NET 用 RTLD_LOCAL 加载 bridge，符号不进全局符号表；
//   而 `dlopen("wpfgfx_cor3.so", RTLD_NOLOAD)` 按**文件名**匹配，.NET 加载时用的却是
//   完整路径 ⇒ 裸名查不到（实测：handle/sym 全 NULL）。maps 里有真路径。
static void *wic_bridge_handle(void)
{
    static void *cached = (void *)-1;
    if (cached != (void *)-1) return cached;
    cached = NULL;
    FILE *f = fopen("/proc/self/maps", "r");
    if (!f) return NULL;
    char line[4096];
    static char path_buf[4096];
    while (fgets(line, sizeof(line), f)) {
        char *slash = strstr(line, "/");
        if (!slash) continue;
        if (!strstr(slash, "wpfgfx_cor3")) continue;
        size_t n = strlen(slash);
        while (n && (slash[n - 1] == '\n' || slash[n - 1] == ' ')) slash[--n] = 0;
        snprintf(path_buf, sizeof(path_buf), "%s", slash);
        cached = dlopen(path_buf, RTLD_NOW | RTLD_NOLOAD);
        break;
    }
    fclose(f);
    return cached;
}

static void *wic_color_context_bridge(const char *name)
{
    void *p = dlsym(RTLD_DEFAULT, name);
    if (!p) {
        void *h = wic_bridge_handle();
        if (h) p = dlsym(h, name);
        if (getenv("LDP_WIC_TRACE")) {
            fprintf(stderr, "[WIC-ICC]   bridge lookup %s: handle=%p sym=%p\n", name, h, p);
            fflush(stderr);
        }
    }
    return p;
}

int32_t WICCreateColorContext_Proxy(void *factory, void **context)
{
    if (!context) return E_NOTIMPL_;
    *context = NULL;
    if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-ICC] WICCreateColorContext\n"); fflush(stderr); }
    if (getenv("LDP_WIC_TRACE")) {
        fprintf(stderr, "[WIC-ICC] WICCreateColorContext(factory=%p valid=%d)\n", factory, wpf_wic_factory_valid(factory));
        fflush(stderr);
    }
    if (!wpf_wic_factory_valid(factory)) return 0x80070057;
    typedef void *(*fn_new)(uint8_t *, uint32_t, uint32_t);
    fn_new make = (fn_new)wic_color_context_bridge("LdpMilColorContextFromProfile");
    if (!make) return E_NOTIMPL_;   // 没有 bridge ⇒ 造不出可用的上下文（如实失败）
    void *h = make(NULL, 0, 0);     // 空上下文
    if (!h) return E_NOTIMPL_;
    // 若刚有过一次 OpenColorProfile（`ColorContext(new Uri(icc))` 的实测路径），
    //   把那份 profile 预登记到这个新 context 上 —— 见 mscms.c 里 wic_last_opened_profile 的注释。
    extern const uint8_t *wic_last_opened_profile(uint32_t *len);
    uint32_t plen = 0;
    const uint8_t *prof = wic_last_opened_profile(&plen);
    if (prof && plen) {
        wic_ctx_profile_set(h, prof, plen);
        if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-ICC]   新 context 预登记 profile %u 字节\n", plen); fflush(stderr); }
    }
    *context = h;
    return S_OK_;
}

int32_t IWICColorContext_InitializeFromMemory_Proxy(void *context, uint8_t *profile, uint32_t length)
{
    if (!context || !profile || length == 0) return 0x80070057;
    typedef int32_t (*fn_set)(void *, uint8_t *, uint32_t, uint32_t);
    fn_set set = (fn_set)wic_color_context_bridge("LdpMilColorContextSetProfile");
    if (!set) return E_NOTIMPL_;
    int32_t hr = set(context, profile, length, 2 /* WICColorContextProfile */);
    return (int32_t)hr;
}

// ── context → profile 字节 的自管表 ─────────────────────────────────────────────
// 【为什么自管】色彩转换要从两个 color context 取 profile 字节；bridge 的
//   `IWICColorContext_GetProfileBytes_Proxy` 在实测里返回 E_INVALIDARG（长度却回填正确），
//   与其依赖那条跨模块路径，不如在**本 shim 内部**留一份映射：登记 context 时把字节存下来。
// ── 颜色上下文（ICC）：PNG 无内嵌 profile ⇒ 实际数量 0（不是"忘了实现"）─────────────
// WPF 用它判断"要不要做色彩管理"。返回 0 个上下文是**真值**：本解码器只解像素，
// 不做 ICC 解析（D-018 的 ICC 分支仍缺，见已知坑清单）。
int32_t IWICBitmapFrameDecode_GetColorContexts_Proxy(void *decoder, uint32_t count,
                                                     void **contexts, uint32_t *actual)
{
    if (actual) *actual = 0;
    wic_decoder *d = (wic_decoder *)decoder;
    if (getenv("LDP_WIC_TRACE")) {
        fprintf(stderr, "[WIC-ICC] GetColorContexts(count=%u, contexts=%p ctx0=%p decoder=%p magic_ok=%d icc_len=%u)\n",
                count, (void *)contexts, (contexts && count > 0) ? contexts[0] : NULL,
                (void *)decoder, (d && d->hdr.magic == WIC_DECODER_MAGIC) ? 1 : 0,
                (d && d->hdr.magic == WIC_DECODER_MAGIC) ? d->img.icc_len : 0);
        fflush(stderr);
    }
    if (!d || d->hdr.magic != WIC_DECODER_MAGIC) return 0x80070057;
    // 没有内嵌 ICC ⇒ 0 个上下文（真值）
    if (!d->img.icc || d->img.icc_len == 0) return S_OK_;
    if (count == 0 || contexts == NULL) { if (actual) *actual = 1; return S_OK_; }   // 只问数量
    // WIC 语义：**填充调用方给的已有 context**（不是创建新的）。若没给 context 就只报数量。
    if (contexts[0] == NULL) {
        if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-ICC]   ctx0 为 NULL ⇒ 只能回填 0 个\n"); fflush(stderr); }
        if (actual) *actual = 0;
        return S_OK_;
    }
    // 句柄必须登记进 bridge 的 MilColorContextTable，WPF 后面的
    // IWICColorContext_GetProfileBytes/GetType（落在 MilCore）才查得到。
    // ⚠️ 这里只需要 SetProfile：句柄是 WPF 通过 `WICCreateColorContext` 拿的、
    //   由本 shim 转手 bridge 登记的。修前这里还留着一段按**裸名** dlopen 找
    //   `LdpMilColorContextFromProfile` 的旧代码，找不到就 `return S_OK_` 早退 ——
    //   于是 SetProfile 永远调不到（实测 trace 里只剩第一次调用）。
    typedef int32_t (*fn_set)(void *, uint8_t *, uint32_t, uint32_t);
    fn_set set = (fn_set)wic_color_context_bridge("LdpMilColorContextSetProfile");
    if (!set) {
        if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-ICC]   bridge 缺 LdpMilColorContextSetProfile\n"); fflush(stderr); }
        return S_OK_;
    }
    int32_t hr = set(contexts[0], d->img.icc, d->img.icc_len, 2 /* WICColorContextProfile */);
    if (hr == 0) wic_ctx_profile_set(contexts[0], d->img.icc, d->img.icc_len);   // 自管一份供色彩转换
    if (getenv("LDP_WIC_TRACE")) {
        fprintf(stderr, "[WIC-ICC]   SetProfile(ctx=%p) => 0x%08x\n", contexts[0], (unsigned)hr);
        fflush(stderr);
    }
    if (hr == 0) { if (actual) *actual = 1; }
    return S_OK_;
}

// ── 缩略图：PNG 没有内嵌缩略图 ⇒ 按 WIC 语义返回 WINCODEC_ERR_CODECNOTHUMBNAIL ──────
// 为什么要用这个专用码而不是 E_NOTIMPL：WPF 的 `EnsureThumbnail` 认这个码为"确实没有缩略图"
// 并**正常跳过**；返回 E_NOTIMPL 会被当成"实现缺失"抛 NotImplementedException（实测踩过）。
int32_t IWICBitmapFrameDecode_GetThumbnail_Proxy(void *decoder, void **thumb)
{
    if (thumb) *thumb = NULL;
    (void)decoder;
    return (int32_t)0x88982F44;   /* WINCODEC_ERR_CODECNOTHUMBNAIL（上游 wgx_error.cs 的值） */
}

#define COLOR_TRANSFORM_MAGIC_ 0x4c445433u   /* 'LDT3'，与 mscms.c 一致 */
// ── 分辨率（UpdateCachedSettings 会问；PNG 没存 DPI 时按 WIC 的默认 96）────────────
int32_t IWICBitmapSource_GetResolution_Proxy(void *source, double *dpiX, double *dpiY)
{
    if (!wic_ptr_readable(source)) return 0x80070057;
    wic_object *o = (wic_object *)source;
    // 解码器与色彩转换对象都按 96 DPI 报（transform 没有自己的 DPI 概念）
    if (o->magic != WIC_DECODER_MAGIC && o->magic != COLOR_TRANSFORM_MAGIC_) return 0x80070057;
    if (dpiX) *dpiX = 96.0;
    if (dpiY) *dpiY = 96.0;
    return S_OK_;
}

// ── 帧计数（WPF 建好解码器后会问"有几帧"）───────────────────────────────────
int32_t IWICBitmapDecoder_GetFrameCount_Proxy(void *decoder, uint32_t *count)
{
    wic_decoder *d = (wic_decoder *)decoder;
    if (!d || d->hdr.magic != WIC_DECODER_MAGIC || !count) return 0x80070057;
    *count = 1;   // PNG 是单帧
    return S_OK_;
}

// ── 未实现的 WIC 入口：明确失败（数量与 WPF 的 P/Invoke 面一一对应，后续逐个填实）──
#define WIC_STUB(name) \
    int32_t name(void) { \
        if (getenv("LDP_WIC_TRACE")) { fprintf(stderr, "[WIC-STUB] %s 被调用（返回 E_NOTIMPL）\n", #name); fflush(stderr); } \
        return E_NOTIMPL_; \
    }

WIC_STUB(WICSetEncoderFormat_Proxy)
WIC_STUB(IWICImagingFactory_CreateEncoder_Proxy)
WIC_STUB(IWICImagingFactory_CreateComponentInfo_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmap_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromHICON_Proxy)
WIC_STUB(IWICImagingFactory_CreateFormatConverter_Proxy)
WIC_STUB(IWICBitmapFrameDecode_GetMetadataQueryReader_Proxy)
WIC_STUB(IWICMetadataQueryReader_GetMetadataByName_Proxy)
WIC_STUB(IWICMetadataQueryReader_GetEnumerator_Proxy)
WIC_STUB(IEnumString_Next_WIC_Proxy)
WIC_STUB(IPropertyBag2_Write_Proxy)
WIC_STUB(IPropertyBag2_Read_Proxy)
WIC_STUB(CheckBitmapBits)

// 对外暴露的 bridge 符号查找（mscms.c 的 IWICColorTransform_Initialize 要用 bridge 的 GetProfileBytes）
void *wic_bridge_lookup(const char *name) { return wic_color_context_bridge(name); }
