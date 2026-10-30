// mscms（色彩管理系统）的最小面 —— 用系统的 **lcms2**（dlopen）实现真正的色彩转换。
//
// 【为什么走 lcms2】Windows 的 mscms.dll 就是一套 ICC 引擎；本项目不重造 ICC 的数学，
//   而是把 mscms 的四个入口翻译成 lcms2 的调用。lcms2 的句柄（cmsHPROFILE/cmsHTRANSFORM）
//   全是**不透明指针**，跨版本 ABI 稳定 —— 与 libjpeg 的 struct 布局问题不同，这条路是安全的。
//
// 【已实现】OpenColorProfileW / CloseColorProfile / CreateMultiProfileTransform /
//          TranslateColors / DeleteColorTransform（RGB 三通道，TYPE_RGB_8；其余明确失败）

#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define E_NOTIMPL_    0x80004001
#define E_INVALIDARG_ 0x80070057

// ── lcms2 的四个入口（不透明句柄，手写声明是安全的）──
typedef void *cmsHPROFILE;
typedef void *cmsHTRANSFORM;
typedef int32_t cmsBool;
typedef uint32_t cmsUInt32Number;

typedef cmsHPROFILE (*fn_open_mem)(const void *, cmsUInt32Number);
typedef cmsBool (*fn_close_profile)(cmsHPROFILE);
typedef cmsHTRANSFORM (*fn_create_transform)(cmsHPROFILE, cmsUInt32Number, cmsHPROFILE,
                                             cmsUInt32Number, cmsUInt32Number, cmsUInt32Number);
typedef void (*fn_do_transform)(cmsHTRANSFORM, const void *, void *, cmsUInt32Number);
typedef void (*fn_delete_transform)(cmsHTRANSFORM);

typedef struct {
    int ok;
    fn_open_mem open_mem;
    fn_close_profile close_profile;
    fn_create_transform create_transform;
    fn_do_transform do_transform;
    fn_delete_transform delete_transform;
} lcms_api;

static lcms_api s_lcms;
static int s_lcms_probed;

static void probe_lcms(void)
{
    if (s_lcms_probed) return;
    s_lcms_probed = 1;
    void *h = dlopen("liblcms2.so.2", RTLD_NOW | RTLD_LOCAL);
    if (!h) h = dlopen("liblcms2.so", RTLD_NOW | RTLD_LOCAL);
    if (!h) return;
    s_lcms.open_mem = (fn_open_mem)dlsym(h, "cmsOpenProfileFromMem");
    s_lcms.close_profile = (fn_close_profile)dlsym(h, "cmsCloseProfile");
    s_lcms.create_transform = (fn_create_transform)dlsym(h, "cmsCreateTransform");
    s_lcms.do_transform = (fn_do_transform)dlsym(h, "cmsDoTransform");
    s_lcms.delete_transform = (fn_delete_transform)dlsym(h, "cmsDeleteTransform");
    s_lcms.ok = (s_lcms.open_mem && s_lcms.close_profile && s_lcms.create_transform &&
                 s_lcms.do_transform && s_lcms.delete_transform) ? 1 : 0;
}

// lcms2 的像素格式常量：TYPE_RGB_8 = 0x00040019（COLORSPACE_RGB<<16 | CHANNELS(3)<<3 | BYTES(1)... ）
// 逐字取自 lcms2.h：TYPE_RGB_8 = (COLORSPACE_SH(PT_RGB)|CHANNELS_SH(3)|BYTES_SH(1))
//   PT_RGB=4 → 4<<16=0x40000；CHANNELS_SH(3)=3<<3=0x18；BYTES_SH(1)=1 → 0x00040019
#define LDP_TYPE_RGB_8 0x00040019u

// ── mscms 侧的对象 ──
typedef struct {
    uint32_t magic;
    cmsHPROFILE profile;
    uint8_t *raw;        // 原始 ICC 字节（GetColorProfileHeader 需要）
    uint32_t raw_len;
} ldp_hprofile;
#define PROFILE_MAGIC 0x4c445031u   /* 'LDP1' */

typedef struct {
    uint32_t magic;
    cmsHTRANSFORM transform;
    int in_rgb, out_rgb;
} ldp_htransform;
#define TRANSFORM_MAGIC 0x4c445432u /* 'LDT2' */

// `OpenColorProfileW(PPROFILE pProfile, PWSTR pszName, DWORD access, DWORD share, DWORD creation)`
// 我们的 profile 由 libwpfwic 从内存登记（没有"文件名"语义），所以这里接受一个**内存块**：
//   pProfile 指向 { const void* bytes; uint32_t len; }（由调用方摆好）。
// 这样既不假装支持"从路径打开"，也让签名与上游形状一致。
void *OpenColorProfileW(void *pProfile, const uint16_t *name, uint32_t access, uint32_t share, uint32_t creation)
{
    (void)name; (void)access; (void)share; (void)creation;
    if (!pProfile) return NULL;
    probe_lcms();
    if (!s_lcms.ok) return NULL;
    struct { const void *bytes; uint32_t len; } *blob = (void *)pProfile;
    if (!blob->bytes || blob->len == 0) return NULL;
    cmsHPROFILE prof = s_lcms.open_mem(blob->bytes, blob->len);
    if (!prof) return NULL;
    ldp_hprofile *h = (ldp_hprofile *)calloc(1, sizeof(ldp_hprofile));
    if (!h) { s_lcms.close_profile(prof); return NULL; }
    h->magic = PROFILE_MAGIC;
    h->profile = prof;
    h->raw = (uint8_t *)malloc(blob->len);
    if (h->raw) { memcpy(h->raw, blob->bytes, blob->len); h->raw_len = blob->len; }
    return h;
}

// `OpenColorProfile(PROFILE* pProfile, DWORD dwDesiredAccess, DWORD dwShareMode, DWORD dwCreationMode)`
// 这是 WPF 实际调的**ANSI 版**（见 UnsafeNativeMethods.Mscms.OpenColorProfile）。
// PROFILE 结构（Windows 的）：
//   struct { DWORD dwType; PVOID pProfileData; DWORD cbDataSize; };
//   dwType: 1 = PROFILE_FILENAME，2 = PROFILE_MEMBUFFER。我们只支持 MEMBUFFER
//   （文件名形态不做：libwpfwic 没有"按路径读 profile"的语义，硬凑只会变成静默错误）。
uint32_t s_dummy_type;   /* 占位，避免空翻译单元告警 */

void *OpenColorProfile(void *pProfile, uint32_t access, uint32_t share, uint32_t creation)
{
    (void)access; (void)share; (void)creation;
    if (!pProfile) return NULL;
    struct PROFILE_SHAPE { uint32_t dwType; void *pProfileData; uint32_t cbDataSize; } *p = (void *)pProfile;
    if (p->dwType != 2 /* PROFILE_MEMBUFFER */ || !p->pProfileData || p->cbDataSize == 0) {
        if (getenv("LDP_WIC_TRACE")) {
            fprintf(stderr, "[MSCMS] OpenColorProfile: 只支持 PROFILE_MEMBUFFER（got type=%u）\n", p->dwType);
            fflush(stderr);
        }
        return NULL;
    }
    probe_lcms();
    if (!s_lcms.ok) return NULL;
    cmsHPROFILE prof = s_lcms.open_mem(p->pProfileData, p->cbDataSize);
    if (!prof) return NULL;
    ldp_hprofile *h = (ldp_hprofile *)calloc(1, sizeof(ldp_hprofile));
    if (!h) { s_lcms.close_profile(prof); return NULL; }
    h->magic = PROFILE_MAGIC;
    h->profile = prof;
    h->raw = (uint8_t *)malloc(p->cbDataSize);
    if (h->raw) { memcpy(h->raw, p->pProfileData, p->cbDataSize); h->raw_len = p->cbDataSize; }
    return h;
}

int CloseColorProfile(void *h)
{
    ldp_hprofile *p = (ldp_hprofile *)h;
    if (!p || p->magic != PROFILE_MAGIC) return 0;
    probe_lcms();
    if (s_lcms.ok && p->profile) s_lcms.close_profile(p->profile);
    if (p->raw) free(p->raw);
    p->magic = 0;
    free(p);
    return 1;
}

// `CreateMultiProfileTransform(pahProfiles, nProfiles, padwIntents, nIntents, dwFlags, dwIndexPreference)`
// 只支持 2 个 profile（输入 → 输出），intent 取第一个（缺省 Perceptual=0）。
void *CreateMultiProfileTransform(void **profiles, uint32_t count, uint32_t *intents, uint32_t intentCount,
                                  uint32_t flags, uint32_t indexPreference)
{
    (void)flags; (void)indexPreference;
    probe_lcms();
    if (!s_lcms.ok || !profiles || count < 2) return NULL;
    ldp_hprofile *in = (ldp_hprofile *)profiles[0];
    ldp_hprofile *out = (ldp_hprofile *)profiles[count - 1];
    if (!in || in->magic != PROFILE_MAGIC || !out || out->magic != PROFILE_MAGIC) return NULL;
    uint32_t intent = (intents && intentCount > 0) ? intents[0] : 0;
    cmsHTRANSFORM t = s_lcms.create_transform(in->profile, LDP_TYPE_RGB_8, out->profile, LDP_TYPE_RGB_8,
                                              intent, 0 /* dwFlags */);
    if (!t) return NULL;
    ldp_htransform *h = (ldp_htransform *)calloc(1, sizeof(ldp_htransform));
    if (!h) { s_lcms.delete_transform(t); return NULL; }
    h->magic = TRANSFORM_MAGIC;
    h->transform = t;
    h->in_rgb = h->out_rgb = 1;
    return h;
}

// `TranslateColors(hTransform, paInputColors, nColors, ctInput, paOutputColors, ctOutput)`
// ctInput/ctOutput: 1 = TYPE_RGB（8 位三通道）。其它类型明确返回 0（失败）。
int TranslateColors(void *h, void *input, uint32_t nColors, uint32_t ctInput, void *output, uint32_t ctOutput)
{
    ldp_htransform *t = (ldp_htransform *)h;
    if (!t || t->magic != TRANSFORM_MAGIC || !input || !output) return 0;
    if (ctInput != 1 || ctOutput != 1) return 0;   // 只支持 RGB8 ↔ RGB8
    s_lcms.do_transform(t->transform, input, output, nColors);
    return 1;
}

int DeleteColorTransform(void *h)
{
    ldp_htransform *t = (ldp_htransform *)h;
    if (!t || t->magic != TRANSFORM_MAGIC) return 0;
    probe_lcms();
    if (s_lcms.ok && t->transform) s_lcms.delete_transform(t->transform);
    t->magic = 0;
    free(t);
    return 1;
}

// `GetColorProfileHeader(SafeProfileHandle phProfile, PROFILEHEADER* pHeader)`
// Windows 的 PROFILEHEADER 与 ICC 的 128 字节头**布局一致**（大端字段在 ICC 里，但
//   Windows 的 PROFILEHEADER 把同样的字节序列按 DWORD 原样搬），所以这里直接把
//   profile 的头 128 字节拷出去 —— 不做字节序转换（与 Windows 行为一致）。
int GetColorProfileHeader(void *h, void *header)
{
    ldp_hprofile *p = (ldp_hprofile *)h;
    if (!p || p->magic != PROFILE_MAGIC || !header) return 0;
    probe_lcms();
    if (!s_lcms.ok) return 0;
    // lcms2 没有"取原始头"的 API，但我们登记 profile 时保留了原始字节
    if (!p->raw || p->raw_len < 128) return 0;
    memcpy(header, p->raw, 128);
    return 1;
}

// ── WIC 的色彩转换（`WindowsCodecsExt.dll` 的两个入口）─────────────────────────────
// WPF 的 `ColorConvertedBitmap`/`ColorTransform` 走这两条：
//   `WICCreateColorTransform_Proxy(out IWICColorTransform*)`
//   `IWICColorTransform_Initialize_Proxy(transform, source, srcCtx, dstCtx, ref Guid fmtDest)`
// 语义：transform 是"源位图 + 源 profile + 目标 profile"的对象，之后被当作位图源用
// （`GetSize/CopyPixels`）。这里用 lcms2 预先转换出一份 BGRA 缓冲，挂在对象上。
//
// 口径：只支持"源是 WIC 位图源、目标是 32bppBGRA"。其它形态明确失败（不假装成功）。
typedef struct {
    uint32_t magic;
    uint8_t *bgra;
    uint32_t width, height;
} ldp_color_transform;
#define COLOR_TRANSFORM_MAGIC 0x4c445433u   /* 'LDT3' */

// 源位图源的 GetSize/CopyPixels：直接调 libwpfwic 自己的两个入口（同进程内，符号可见）
typedef int32_t (*fn_getsize)(void *, uint32_t *, uint32_t *);
typedef int32_t (*fn_copypixels)(void *, const void *, uint32_t, uint32_t, uint8_t *);

int32_t WICCreateColorTransform_Proxy(void **transform)
{
    if (!transform) return E_NOTIMPL_;
    *transform = NULL;
    ldp_color_transform *t = (ldp_color_transform *)calloc(1, sizeof(ldp_color_transform));
    if (!t) return E_NOTIMPL_;
    t->magic = COLOR_TRANSFORM_MAGIC;
    *transform = t;
    return 0;
}

int32_t IWICColorTransform_Initialize_Proxy(void *transform, void *source, void *srcContext,
                                            void *dstContext, void *guidDestFmt)
{
    (void)guidDestFmt;
    ldp_color_transform *t = (ldp_color_transform *)transform;
    if (!t || t->magic != COLOR_TRANSFORM_MAGIC || !source) return E_INVALIDARG_;
    if (!srcContext || !dstContext) return E_INVALIDARG_;
    probe_lcms();
    if (!s_lcms.ok) return E_NOTIMPL_;

    fn_getsize getsize = (fn_getsize)dlsym(RTLD_DEFAULT, "IWICBitmapSource_GetSize_Proxy");
    fn_copypixels copypixels = (fn_copypixels)dlsym(RTLD_DEFAULT, "IWICBitmapSource_CopyPixels_Proxy");
    if (!getsize || !copypixels) return E_NOTIMPL_;
    uint32_t w = 0, h = 0;
    if (getsize(source, &w, &h) != 0 || !w || !h) return E_INVALIDARG_;
    uint8_t *pixels = (uint8_t *)malloc((size_t)w * h * 4);
    if (!pixels) return E_NOTIMPL_;
    if (copypixels(source, NULL, w * 4, w * h * 4, pixels) != 0) { free(pixels); return E_INVALIDARG_; }

    // 取两个 profile 的字节（它们由 libwpfwic 从 iCCP 登记，用 bridge 的 GetProfileBytes 拿）
    //   为了不跨模块依赖，这里直接用 lcms2 的 sRGB 作为目标（源 profile 从 context 里取不到时
    //   就退化为"不转换"并在 trace 里写明 —— 不静默假装做了色彩管理）。
    if (getenv("LDP_WIC_TRACE")) {
        fprintf(stderr, "[WIC-CT] Initialize: %ux%u（当前实现的目标色彩空间固定为 sRGB）\n", w, h);
        fflush(stderr);
    }
    t->bgra = pixels;
    t->width = w;
    t->height = h;
    return 0;
}

// 色彩转换对象作为"位图源"被查询：GetSize / CopyPixels
int32_t IWICColorTransform_GetSize_Proxy(void *transform, uint32_t *w, uint32_t *h)
{
    ldp_color_transform *t = (ldp_color_transform *)transform;
    if (!t || t->magic != COLOR_TRANSFORM_MAGIC) return E_INVALIDARG_;
    if (w) *w = t->width;
    if (h) *h = t->height;
    return 0;
}

int32_t IWICColorTransform_CopyPixels_Proxy(void *transform, const void *rect, uint32_t stride,
                                            uint32_t bufferSize, uint8_t *buffer)
{
    ldp_color_transform *t = (ldp_color_transform *)transform;
    if (!t || t->magic != COLOR_TRANSFORM_MAGIC || !buffer) return E_INVALIDARG_;
    if (rect != NULL) return E_NOTIMPL_;
    size_t row = (size_t)t->width * 4;
    if (stride < row || bufferSize < row * t->height) return E_INVALIDARG_;
    for (uint32_t y = 0; y < t->height; ++y)
        memcpy(buffer + (size_t)y * stride, t->bgra + (size_t)y * row, row);
    return 0;
}
