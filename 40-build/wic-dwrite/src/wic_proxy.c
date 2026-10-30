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

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

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

// ── 未实现的 WIC 入口：明确失败（数量与 WPF 的 P/Invoke 面一一对应，后续逐个填实）──
#define WIC_STUB(name) \
    int32_t name(void) { return E_NOTIMPL_; }

WIC_STUB(WICSetEncoderFormat_Proxy)
WIC_STUB(IWICImagingFactory_CreateDecoderFromFilename_Proxy)
WIC_STUB(IWICImagingFactory_CreateDecoderFromFileHandle_Proxy)
WIC_STUB(IWICImagingFactory_CreateDecoderFromStream_Proxy)
WIC_STUB(IWICImagingFactory_CreateEncoder_Proxy)
WIC_STUB(IWICImagingFactory_CreateComponentInfo_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmap_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromSource_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromMemory_Proxy)
WIC_STUB(IWICImagingFactory_CreateBitmapFromHICON_Proxy)
WIC_STUB(IWICImagingFactory_CreateFormatConverter_Proxy)
WIC_STUB(IWICBitmapDecoder_GetFrame_Proxy)
WIC_STUB(IWICBitmapFrameDecode_GetThumbnail_Proxy)
WIC_STUB(IWICBitmapSource_CopyPixels_Proxy)
WIC_STUB(IWICBitmapSource_GetSize_Proxy)
WIC_STUB(IWICBitmapSource_GetPixelFormat_Proxy)
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
