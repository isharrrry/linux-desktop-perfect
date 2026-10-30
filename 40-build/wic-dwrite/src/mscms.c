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
    return h;
}

int CloseColorProfile(void *h)
{
    ldp_hprofile *p = (ldp_hprofile *)h;
    if (!p || p->magic != PROFILE_MAGIC) return 0;
    probe_lcms();
    if (s_lcms.ok && p->profile) s_lcms.close_profile(p->profile);
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
