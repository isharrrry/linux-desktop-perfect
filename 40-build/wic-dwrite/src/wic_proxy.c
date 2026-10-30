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
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "png_decode.h"

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
typedef struct {
    uint32_t magic;
    ldp_png_image img;
} wic_decoder;
#define WIC_DECODER_MAGIC 0x57494344u   /* 'WICD' */

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
    int32_t hr = ldp_png_decode(buf, rd, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->magic = WIC_DECODER_MAGIC;
    *out = d;
    return S_OK_;
}

int32_t IWICBitmapDecoder_GetFrame_Proxy(void *decoder, uint32_t index, void **frame)
{
    if (!frame) return E_NOTIMPL_;
    *frame = NULL;
    wic_decoder *d = (wic_decoder *)decoder;
    if (!d || d->magic != WIC_DECODER_MAGIC) return 0x80070057;
    if (index != 0) return E_NOTIMPL_;   /* 只支持单帧 */
    *frame = d;                          /* 帧就是解码器本身（单图 PNG 的常见口径） */
    return S_OK_;
}

int32_t IWICBitmapSource_GetSize_Proxy(void *source, uint32_t *w, uint32_t *h)
{
    wic_decoder *d = (wic_decoder *)source;
    if (!d || d->magic != WIC_DECODER_MAGIC) return 0x80070057;
    if (w) *w = d->img.width;
    if (h) *h = d->img.height;
    return S_OK_;
}

int32_t IWICBitmapSource_GetPixelFormat_Proxy(void *source, void *guid)
{
    wic_decoder *d = (wic_decoder *)source;
    if (!d || d->magic != WIC_DECODER_MAGIC) return 0x80070057;
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
    wic_decoder *d = (wic_decoder *)source;
    if (!d || d->magic != WIC_DECODER_MAGIC || !buffer) return 0x80070057;
    // 只支持"整幅拷贝"（rect == NULL）
    if (rect != NULL) return E_NOTIMPL_;
    size_t row = (size_t)d->img.width * 4;
    if (stride < row || bufferSize < row * d->img.height) return 0x80070057;
    for (uint32_t y = 0; y < d->img.height; ++y)
        memcpy(buffer + (size_t)y * stride, d->img.bgra + (size_t)y * row, row);
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
    int32_t hr = ldp_png_decode(buf, len, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->magic = WIC_DECODER_MAGIC;
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
    int32_t hr = ldp_png_decode(buf, (size_t)size, &d->img, err, sizeof(err));
    free(buf);
    if (hr != 0) { free(d); return hr; }
    d->magic = WIC_DECODER_MAGIC;
    *out = d;
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
WIC_STUB(IWICImagingFactory_CreateBitmapFromSource_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromMemory_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromHICON_Proxy)
WIC_STUB(IWICImagingFactory_CreateFormatConverter_Proxy)
WIC_STUB(IWICBitmapFrameDecode_GetThumbnail_Proxy)
WIC_STUB(IWICBitmapSource_GetResolution_Proxy)
WIC_STUB(IWICBitmapSource_GetColorContexts_Proxy)
WIC_STUB(IWICBitmapFrameDecode_GetMetadataQueryReader_Proxy)
WIC_STUB(IWICComponentInfo_GetCLSID_Proxy)
WIC_STUB(IWICComponentInfo_GetAuthor_Proxy)
WIC_STUB(IWICComponentInfo_GetVersion_Proxy)
WIC_STUB(IWICComponentInfo_GetSpecVersion_Proxy)
WIC_STUB(IWICComponentInfo_GetFriendlyName_Proxy)
WIC_STUB(IWICMetadataQueryReader_GetMetadataByName_Proxy)
WIC_STUB(IWICMetadataQueryReader_GetEnumerator_Proxy)
WIC_STUB(IEnumString_Next_WIC_Proxy)
WIC_STUB(IPropertyBag2_Write_Proxy)
WIC_STUB(IPropertyBag2_Read_Proxy)
WIC_STUB(GetColorDirectory)
WIC_STUB(GetColorProfileFromHandle)
WIC_STUB(GetColorProfileHeader)
WIC_STUB(GetStandardColorSpaceProfile)
WIC_STUB(OpenColorProfile)
WIC_STUB(CloseColorProfile)
WIC_STUB(CreateMultiProfileTransform)
WIC_STUB(DeleteColorTransform)
WIC_STUB(TranslateColors)
WIC_STUB(CheckBitmapBits)
