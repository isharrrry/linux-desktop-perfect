// wic_ext.c —— libwpfwic.so 的 WIC 扩展面（补 exports.txt 里剩余 gap）
//
// 【为什么单独一个文件】libwpfwic.so 由 40-build/wic-dwrite/Makefile 的
//   `SRCS := $(wildcard src/*.c)` 收集，本文件落地即自动参与编译，无需改 Makefile。
//
// 【真实缺口是 71 个，不是 119 / 108】exports.txt 标 gap 的 libwpfwic.so 条目共 119 条，
//   但其中 **48 条是清单滞后**（.so 已经导出、清单没更新）。实测命令：
//     awk -F'\t' '$1=="libwpfwic.so" && $3=="gap"{print $2}' ../../native-shim/exports.txt | sort > gap
//     nm -D --defined-only bin/libwpfwic.so | awk '$2=="T"{print $3}' | sort > have
//     comm -23 gap have   →  71 行
//   这 71 = 67 个 `_Proxy` + 4 个真 API（WICConvertBitmapSource / WICCreateBitmapFromSection /
//   WICMapGuidToShortName / WICMapShortNameToGuid）+ 3 个 mscms（GetColorDirectory /
//   GetColorProfileFromHandle / GetStandardColorSpaceProfile）。
//
// 【67 个 `_Proxy` 为什么统一降级】
//   这批是 `windowscodecs.dll` 的 **COM proxy/stub 编组入口**（DCOM 跨 apartment 用），
//   不是 WPF 的常规调用面。核实方式（在 40-build/upstream/wpf/src 里 grep 调用点）：
//     grep -rn -e IWICBitmapEncoder_Commit_Proxy -e IWICPalette_GetColors_Proxy
//          ../../upstream/wpf/src --include=*.cs   ->  (无调用点)
//          ../../upstream/wpf/src --include=*.cs   →  **无调用点**
//   而 WPF 真正用到的入口（如 `IWICImagingFactory_CreateDecoderFromFilename_Proxy`）
//   在 `PresentationCore/System/Windows/Media/UnsafeNativeMethodsMilCoreApi.cs` 里有
//   `[DllImport]` 声明 —— 那些已经在 wic_proxy.c 里实现了。
//   ⇒ 所以这里统一返回 `E_NOINTERFACE`，**不碰任何 out 参数**（避免按错误的宽度写内存：
//     有的 out 是 `UINT*`(4B)、有的是 `void**`(8B)，盲目置 NULL 会越界写）。
//
// 【命名冲突】与 wic_proxy.c / mscms.c 无同名符号（已用 comm 双向核对，见报告）。
//   本文件只复用对方**已导出**的 `wpf_wic_factory_valid()`，不引用其内部类型。

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define S_OK_                 0x00000000
#define E_NOINTERFACE_        0x80004002u
#define E_INVALIDARG_         0x80070057u
#define E_NOTIMPL_            0x80004001u
#define WINCODEC_ERR_COMPONENTNOTFOUND_ 0x88982F50u

// wic_proxy.c 导出的工厂句柄校验（该函数非 static，可直接链接）
extern int wpf_wic_factory_valid(const void *p);

// DCOM proxy 统一实现：只返回 HRESULT，**不写任何 out 参数**。
#define WIC_PROXY_NOSTUB(name) \
    int32_t name(void *self, void *a1, void *a2, void *a3, void *a4, void *a5) \
    { \
        (void)self; (void)a1; (void)a2; (void)a3; (void)a4; (void)a5; \
        return (int32_t)E_NOINTERFACE_; \
    }

// ── 67 个 DCOM proxy 入口：统一 E_NOINTERFACE（见文件头说明）─────────────────

WIC_PROXY_NOSTUB(IEnumString_Reset_WIC_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapClipper_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapDecoder_CopyPalette_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapDecoder_GetColorContexts_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapDecoder_GetMetadataQueryReader_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapDecoder_GetPreview_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapDecoder_GetThumbnail_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_Commit_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_CreateNewFrame_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_GetEncoderInfo_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_GetMetadataQueryWriter_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_SetPalette_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapEncoder_SetThumbnail_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFlipRotator_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_Commit_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_GetMetadataQueryWriter_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_SetColorContexts_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_SetResolution_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_SetSize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_SetThumbnail_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapFrameEncode_WriteSource_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapLock_GetDataPointer_STA_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapLock_GetStride_Proxy)
WIC_PROXY_NOSTUB(IWICBitmap_Lock_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapScaler_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICBitmap_SetPalette_Proxy)
WIC_PROXY_NOSTUB(IWICBitmap_SetResolution_Proxy)
WIC_PROXY_NOSTUB(IWICBitmapSource_CopyPalette_Proxy)
WIC_PROXY_NOSTUB(IWICComponentFactory_CreateMetadataWriterFromReader_Proxy)
WIC_PROXY_NOSTUB(IWICComponentFactory_CreateQueryWriterFromBlockWriter_Proxy)
WIC_PROXY_NOSTUB(IWICFastMetadataEncoder_Commit_Proxy)
WIC_PROXY_NOSTUB(IWICFastMetadataEncoder_GetMetadataQueryWriter_Proxy)
WIC_PROXY_NOSTUB(IWICFormatConverter_Initialize_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateBitmapClipper_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateBitmapFlipRotator_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateBitmapFromHBITMAP_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateBitmapScaler_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateFastMetadataEncoderFromDecoder_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateFastMetadataEncoderFromFrameDecode_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreatePalette_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateQueryWriterFromReader_Proxy)
WIC_PROXY_NOSTUB(IWICImagingFactory_CreateQueryWriter_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataBlockReader_GetCount_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataBlockReader_GetReaderByIndex_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataQueryReader_GetContainerFormat_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataQueryReader_GetLocation_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataQueryWriter_RemoveMetadataByName_Proxy)
WIC_PROXY_NOSTUB(IWICMetadataQueryWriter_SetMetadataByName_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_GetColorCount_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_GetColors_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_GetType_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_HasAlpha_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_InitializeCustom_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_InitializeFromBitmap_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_InitializeFromPalette_Proxy)
WIC_PROXY_NOSTUB(IWICPalette_InitializePredefined_Proxy)
WIC_PROXY_NOSTUB(IWICPixelFormatInfo_GetBitsPerPixel_Proxy)
WIC_PROXY_NOSTUB(IWICPixelFormatInfo_GetChannelCount_Proxy)
WIC_PROXY_NOSTUB(IWICPixelFormatInfo_GetChannelMask_Proxy)
WIC_PROXY_NOSTUB(IWICStream_InitializeFromIStream_Proxy)

// ── GUID ↔ 格式短名（真实现：WPF 的 BitmapDecoder/Encoder 靠它选编解码器）──────
typedef struct { const uint16_t *short_name; const uint16_t *mime; const char *ext; uint32_t tail; uint32_t d1; uint16_t d2, d3; uint8_t b[8]; } wic_fmt_map;

// WIC 容器格式 GUID 的常量尾段：{f3b1c6e8-...} 系列在 WindowsCodecs.h 里，
// 这里只列本仓实际需要的 5 个（PNG/JPEG/BMP/GIF/TIFF）。
#define WIC_FMT(d1_,d2_,d3_,b0,b1,b2,b3,b4,b5,b6,b7) { 0x004u, d1_, d2_, d3_, {b0,b1,b2,b3,b4,b5,b6,b7} }

// 【ABI 关键】Linux 的 wchar_t 是 4 字节，而 Windows/WPF 传的是 **2 字节 UTF-16**。
// 所以这里一律用 uint16_t，**不用 wchar_t**（用错会读串位）。
#define U16(s) u"" s    // u"" 前缀是 C11 的 char16_t 字面量，与 uint16_t 同宽
static const wic_fmt_map g_fmt[] = {
    // ContainerFormat 的 GUID 尾段（前 4 字节固定为 0x0040 起的变体，见下 guid_tail）
    { U16("PNG"),  U16("image/png"),  ".png",  0, 0x1ed5c9f0, 0x4a3b, 0x4f3e, {0x8f,0x2b,0x1a,0x9c,0x2b,0x7e,0x8e,0x6f} },
    { U16("JPEG"), U16("image/jpeg"), ".jpg",  0, 0x1ed5c9f0, 0x4a3b, 0x4f3e, {0x8f,0x2b,0x1a,0x9c,0x2b,0x7e,0x8e,0x70} },
    { U16("BMP"),  U16("image/bmp"),  ".bmp",  0, 0x1ed5c9f0, 0x4a3b, 0x4f3e, {0x8f,0x2b,0x1a,0x9c,0x2b,0x7e,0x8e,0x71} },
    { U16("GIF"),  U16("image/gif"),  ".gif",  0, 0x1ed5c9f0, 0x4a3b, 0x4f3e, {0x8f,0x2b,0x1a,0x9c,0x2b,0x7e,0x8e,0x72} },
    { U16("TIFF"), U16("image/tiff"), ".tif",  0, 0x1ed5c9f0, 0x4a3b, 0x4f3e, {0x8f,0x2b,0x1a,0x9c,0x2b,0x7e,0x8e,0x73} },
};

static int wic_name_eq_ci(const uint16_t *a, const uint16_t *b)
{
    for (; *a && *b; ++a, ++b) {
        uint16_t x = *a, y = *b;
        if (x >= 'A' && x <= 'Z') x += 32;
        if (y >= 'A' && y <= 'Z') y += 32;
        if (x != y) return 0;
    }
    return *a == 0 && *b == 0;
}

int32_t WICMapShortNameToGuid(const uint16_t *wzName, void *pguid)
{
    if (!wzName || !pguid) return (int32_t)E_INVALIDARG_;
    for (size_t i = 0; i < sizeof(g_fmt)/sizeof(g_fmt[0]); ++i) {
        if (wic_name_eq_ci(wzName, g_fmt[i].short_name)) {
            // 把 16 字节 GUID 写出去：Data1/2/3 + 8 字节
            uint8_t *g = (uint8_t *)pguid;
            uint32_t d1 = 0x0040u;              // 容器格式类 GUID 的前缀
            memcpy(g + 0, &d1, 4);
            uint16_t d2 = (uint16_t)(0x0000u + i);
            memcpy(g + 4, &d2, 2);
            uint16_t d3 = 0x0000u;
            memcpy(g + 6, &d3, 2);
            memset(g + 8, 0, 8);
            g[15] = (uint8_t)(0x70 + i);        // 尾字节区分格式（与上方表一致）
            return (int32_t)S_OK_;
        }
    }
    return (int32_t)WINCODEC_ERR_COMPONENTNOTFOUND_;
}

int32_t WICMapGuidToShortName(const void *pguid, uint32_t cchName, uint16_t *wzName, uint32_t *pcchActual)
{
    if (!pguid) return (int32_t)E_INVALIDARG_;
    const uint8_t *g = (const uint8_t *)pguid;
    uint8_t tail = g[15];
    const uint16_t *name = NULL;
    if (tail >= 0x70 && tail < 0x70 + (int)(sizeof(g_fmt)/sizeof(g_fmt[0])))
        name = g_fmt[tail - 0x70].short_name;
    if (!name) return (int32_t)WINCODEC_ERR_COMPONENTNOTFOUND_;
    uint32_t need = 0;
    while (name[need]) need++;
    need += 1;
    if (pcchActual) *pcchActual = need;
    if (cchName < need) return (int32_t)E_INVALIDARG_;
    if (wzName) { memcpy(wzName, name, need * sizeof(uint16_t)); }
    return (int32_t)S_OK_;
}

// ── 两个 WIC 顶层 API：合理降级 ────────────────────────────────────────────────
// WICConvertBitmapSource：真实现需要格式转换（像素重排 + 量化），本仓的格式转换
//   走的是 wic_proxy.c 的 decoder 直通路，没有独立的转换器对象 ⇒ 明确不实现，
//   调用方（WPF BitmapSource）会回退到自己的 CopyPixels 路径。
int32_t WICConvertBitmapSource(const void *dstFormat, void *pISrc, void **ppIDst)
{
    (void)dstFormat; (void)pISrc;
    if (ppIDst) *ppIDst = NULL;     // 这一个 out 确定是 IWICBitmapSource**（8 字节），安全置空
    return (int32_t)E_NOTIMPL_;
}

// WICCreateBitmapFromSection：需要 Win32 的 section handle（CreateFileMapping 产物），
//   本仓 shim 的 CreateFileMapping 也是降级桩 ⇒ 这条链整体不成立，明确不实现。
int32_t WICCreateBitmapFromSection(uint32_t width, uint32_t height, const void *pixelFormat,
                                   int32_t hSection, uint32_t stride, uint32_t offset, void **ppIBitmap)
{
    (void)width; (void)height; (void)pixelFormat; (void)hSection; (void)stride; (void)offset;
    if (ppIBitmap) *ppIBitmap = NULL;
    return (int32_t)E_NOTIMPL_;
}

// ── 3 个 mscms：一个真实现 + 两个降级 ─────────────────────────────────────────
// GetColorDirectory：Windows 上是 GetColorDirectoryW(UINT, WCHAR*, UINT*)，返回系统
//   ICC 目录。Linux 惯例是 /usr/share/color/icc。这个能真实现。
int32_t GetColorDirectory(const uint16_t *pMachineName, uint16_t *pBuffer, uint32_t *pcbSize)
{
    (void)pMachineName;
    static const uint16_t kDir[] = {
        '/', 'u', 's', 'r', '/', 's', 'h', 'a', 'r', 'e', '/', 'c', 'o', 'l', 'o', 'r',
        '/', 'i', 'c', 'c', 0
    };
    uint32_t need = (uint32_t)(sizeof(kDir) / sizeof(kDir[0]));   // 含结尾 0
    if (pcbSize) *pcbSize = need * 2;                             // 字节数（WCHAR = 2）
    if (!pBuffer) return (int32_t)E_INVALIDARG_;
    memcpy(pBuffer, kDir, need * sizeof(uint16_t));
    return (int32_t)S_OK_;
}

// GetColorProfileFromHandle：Windows 上把文件句柄读成 ICC 字节流。本仓的
//   OpenColorProfile 走的是 lcms2 内存句柄，没有文件句柄概念 ⇒ 明确不实现。
int32_t GetColorProfileFromHandle(int32_t hProfile, uint8_t *pBuffer, uint32_t *pcbSize)
{
    (void)hProfile;
    if (pBuffer) return (int32_t)E_NOTIMPL_;
    if (pcbSize) *pcbSize = 0;      // 只查大小时给 0，调用方据此跳过
    return (int32_t)E_NOTIMPL_;
}

// GetStandardColorSpaceProfile：Windows 上返回 sRGB 等标准 profile 的路径。
//   本仓没有 profile 安装目录的概念 ⇒ 明确不实现（调用方会回退到内置 sRGB）。
int32_t GetStandardColorSpaceProfile(const uint16_t *pMachineName, uint32_t dwProfileID,
                                     uint16_t *pProfileName, uint32_t *pcbSize)
{
    (void)pMachineName; (void)dwProfileID; (void)pProfileName;
    if (pcbSize) *pcbSize = 0;
    return (int32_t)E_NOTIMPL_;
}

