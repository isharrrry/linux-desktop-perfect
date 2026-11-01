// ── 【清算 / K-187】urlmon + COM 任务内存：把能真实现的展开 ────────────────────
#include "win32_internal.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

// ── COM 任务分配器 ──────────────────────────────────────────────────────────
//   Win32 的 `CoTaskMemAlloc`/`CoTaskMemFree` 是"进程默认任务分配器"。
//   Linux 上没有 COM 任务堆 ⇒ **用 malloc/free 是最正确的对应物**
//   （语义上它就是"调用方与被调方共享的一块内存，由调用方释放"）。
//   注意：`FindMimeFromData` 等 API 返回的字符串必须由这里分配，
//   否则调用方 `CoTaskMemFree` 会炸。
void *CoTaskMemAlloc(size_t cb) { return malloc(cb); }
void *CoTaskMemRealloc(void *pv, size_t cb) { return realloc(pv, cb); }
void  CoTaskMemFree(void *pv) { free(pv); }

// ── FindMimeFromData：按文件头魔数识别 MIME ─────────────────────────────────
//   上游：`HRESULT FindMimeFromData(LPCWSTR pBC, LPCWSTR pwzUrl, LPVOID pBuffer,
//                                   DWORD cbSize, LPCWSTR pwzMimeProposed,
//                                   DWORD dwMimeFlags, LPWSTR* ppwzMimeOut, DWORD dwReserved)`
//   返回值：S_OK(0) + `*ppwzMimeOut` 指向 `CoTaskMemAlloc` 出来的 UTF-16 字符串。
//   Linux 上没有 IInternetSecurityManager，但**魔数识别本身是纯数据判断**，可以真做。
static const char *wpf_mime_from_magic(const uint8_t *b, uint32_t n)
{
    if (!b || n < 4) return NULL;
    // PNG
    if (n >= 8 && !memcmp(b, "\x89PNG\r\n\x1a\n", 8)) return "image/png";
    // JPEG
    if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
    // GIF
    if (n >= 6 && (!memcmp(b, "GIF87a", 6) || !memcmp(b, "GIF89a", 6))) return "image/gif";
    // BMP
    if (n >= 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
    // TIFF
    if (n >= 4 && ((b[0]=='I'&&b[1]=='I'&&b[2]==0x2A&&b[3]==0x00) ||
                   (b[0]=='M'&&b[1]=='M'&&b[2]==0x00&&b[3]==0x2A))) return "image/tiff";
    // ICO
    if (n >= 4 && b[0]==0x00 && b[1]==0x00 && b[2]==0x01 && b[3]==0x00) return "image/x-icon";
    // PDF
    if (n >= 5 && !memcmp(b, "%PDF-", 5)) return "application/pdf";
    // ZIP（含 docx/xlsx/pptx 的容器层）
    if (n >= 4 && b[0]=='P' && b[1]=='K' && b[2]==0x03 && b[3]==0x04) return "application/zip";
    // GZIP
    if (n >= 3 && b[0]==0x1F && b[1]==0x8B && b[2]==0x08) return "application/x-gzip";
    // 7z
    if (n >= 6 && b[0]=='7' && b[1]=='z' && b[2]==0xBC && b[3]==0xAF) return "application/x-7z-compressed";
    // RAR
    if (n >= 7 && !memcmp(b, "Rar!\x1a\x07", 6)) return "application/x-rar-compressed";
    // XML / HTML（文本判断，看前若干字节）
    {
        uint32_t lim = n < 64 ? n : 64;
        for (uint32_t i = 0; i + 4 < lim; ++i) {
            if (!memcmp(b + i, "<?xml", 5)) return "text/xml";
            if (!memcmp(b + i, "<!DOC", 5)) return "text/html";
            if (!memcmp(b + i, "<html", 5) || !memcmp(b + i, "<HTML", 5)) return "text/html";
        }
    }
    return NULL;
}

int32_t FindMimeFromData(void *pBC, const uint16_t *pwzUrl, const void *pBuffer, uint32_t cbSize,
                         const uint16_t *pwzMimeProposed, uint32_t dwMimeFlags,
                         uint16_t **ppwzMimeOut, uint32_t dwReserved)
{
    (void)pBC; (void)pwzUrl; (void)pwzMimeProposed; (void)dwMimeFlags; (void)dwReserved;
    if (!ppwzMimeOut) return (int32_t)0x80070057;   // E_INVALIDARG
    *ppwzMimeOut = NULL;

    const char *mime = wpf_mime_from_magic((const uint8_t *)pBuffer, cbSize);
    if (!mime) mime = "application/octet-stream";

    size_t len = strlen(mime);
    uint16_t *out = (uint16_t *)CoTaskMemAlloc((len + 1) * sizeof(uint16_t));
    if (!out) return (int32_t)0x8007000E;           // E_OUTOFMEMORY
    for (size_t i = 0; i <= len; ++i) out[i] = (uint16_t)(unsigned char)mime[i];
    *ppwzMimeOut = out;
    return 0;                                       // S_OK
}

// ── 其余 urlmon（真降级，Linux 无对应）──────────────────────────────────────
//   `CoInternet*` 系列是 **IE 的区域/功能控制**（安全区域、功能区升降级）；
//   `ObtainUserAgentString` 返回 IE 的 UA。本仓没有 IE/urlmon 栈，全部如实返回
//   `E_NOTIMPL`，而不是伪造一个 UA 或假的区域判定。
int32_t CoInternetCreateSecurityManager(void *sp, void **sm, uint32_t rs) { (void)sp;(void)sm;(void)rs; return (int32_t)0x80004001; }
int32_t CoInternetIsFeatureEnabled(int32_t f, uint32_t f2) { (void)f;(void)f2; return (int32_t)0x80004001; }
int32_t CoInternetIsFeatureZoneElevationEnabled(const uint16_t *a, const uint16_t *b, void *c, uint32_t d)
{ (void)a;(void)b;(void)c;(void)d; return (int32_t)0x80004001; }
int32_t CoInternetSetFeatureEnabled(int32_t f, uint32_t f2, int32_t e) { (void)f;(void)f2;(void)e; return (int32_t)0x80004001; }
int32_t ObtainUserAgentString(uint32_t dwOption, char *pszUA, uint32_t *pcchUA)
{
    (void)dwOption;
    if (pcchUA) *pcchUA = 0;
    if (pszUA) pszUA[0] = 0;
    return (int32_t)0x80004001;   // E_NOTIMPL
}
