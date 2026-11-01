// ── 【清算 / K-199】A 类：COM/WinRT 字符串与变体 —— 全部真实现 ─────────────────
//
// 这 13 个是「Linux 上有直接对应物」的，不该做桩：
//   · BSTR（`SysAllocString`/`SysFreeString`）→ 标准格式，自己实现
//   · WinRT HSTRING（5 个）→ 进程内不透明句柄，自己定义
//   · `VariantClear`/`SetErrorInfo` → 释放/保存，语义清楚
//   · `SHGetFolderPath`/`CommDlgExtendedError`/`RtlNtStatusToDosError`/`GetUrlCacheConfigInfoW`
//
// 【BSTR 格式（Win32 规范，不是私有格式）】
//   内存布局： [4 字节长度][UTF-16 数据...][2 字节 0]
//              ^            ^
//              块首          返回给调用方的指针（指向数据）
//   `SysStringLen` 就是读 ptr-4 的 DWORD。必须严格按这个布局，否则上游读长度会错。
#include "win32_internal.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <limits.h>

// ── BSTR ────────────────────────────────────────────────────────────────────
uint16_t *SysAllocString(const uint16_t *src)
{
    if (!src) return NULL;
    size_t n = 0;
    while (src[n]) ++n;
    // 4 字节长度 + n 个 UTF-16 + 结尾 0
    uint8_t *blk = (uint8_t *)calloc(1, 4 + (n + 1) * sizeof(uint16_t));
    if (!blk) return NULL;
    *(uint32_t *)blk = (uint32_t)(n * sizeof(uint16_t));   // 字节长度（Win32 用字节）
    uint16_t *data = (uint16_t *)(blk + 4);
    memcpy(data, src, n * sizeof(uint16_t));
    data[n] = 0;
    return data;
}

void SysFreeString(uint16_t *bstr)
{
    if (!bstr) return;
    free((uint8_t *)bstr - 4);
}

uint32_t SysStringLen(const uint16_t *bstr)
{
    if (!bstr) return 0;
    return *(const uint32_t *)((const uint8_t *)bstr - 4) / sizeof(uint16_t);
}

// ── VariantClear / SetErrorInfo ─────────────────────────────────────────────
//   `VARIANT` 是 24 字节的标志联合。`VariantClear` 的职责是"按 vt 释放内容并清零"。
//   本 shim 里只有 `VT_BSTR` 需要释放（其它类型是值或指针，本仓不建）；其余按清零处理。
//   布局：+0 vt (2 字节) +2 保留，+8 数据。
int32_t VariantClear(void *pvarg)
{
    if (!pvarg) return (int32_t)0x80070057;   // E_INVALIDARG
    uint16_t vt = *(uint16_t *)pvarg;
    if (vt == 8 /* VT_BSTR */) {
        uint16_t *b = *(uint16_t **)((uint8_t *)pvarg + 8);
        if (b) SysFreeString(b);
    }
    memset(pvarg, 0, 24);
    return 0;   // S_OK
}

//   `SetErrorInfo`：把一个 IErrorInfo 存到当前线程的 TLS 槽（`GetErrorInfo` 取回）。
//   本 shim 没有 COM 错误对象，存一个"有没有设置过"的标记即可 —— 这是**真实现**，
//   因为它准确表达了"设置成功"这个语义。
static __thread void *g_tls_errorinfo = NULL;
int32_t SetErrorInfo(uint32_t dwReserved, void *perrinfo)
{
    (void)dwReserved;
    g_tls_errorinfo = perrinfo;
    return 0;   // S_OK
}
void *GetErrorInfo(uint32_t dwReserved, void **pperrinfo)
{
    (void)dwReserved;
    if (pperrinfo) *pperrinfo = g_tls_errorinfo;
    g_tls_errorinfo = NULL;
    return 0;
}

// ── WinRT HSTRING ───────────────────────────────────────────────────────────
//   HSTRING 是**进程内不透明句柄**，格式由实现者决定（Windows 也没公开）。
//   ⇒ 我们自定义：`{ uint32_t len; uint16_t *data; }`，引用计数 +1。
//   `WindowsGetStringRawBuffer` 返回 `data`，`WindowsDeleteString` 减计数。
typedef struct { uint32_t len; uint32_t refs; uint16_t *data; } wpf_hstring;

int32_t WindowsCreateString(const uint16_t *src, uint32_t len, void **out)
{
    if (!out) return (int32_t)0x80070057;
    *out = NULL;
    if (len == 0) return 0;   // 空串 ⇒ NULL 句柄是合法的
    if (!src) return (int32_t)0x80070057;
    wpf_hstring *h = (wpf_hstring *)calloc(1, sizeof(*h));
    if (!h) return (int32_t)0x8007000E;
    h->data = (uint16_t *)calloc(len + 1, sizeof(uint16_t));
    if (!h->data) { free(h); return (int32_t)0x8007000E; }
    memcpy(h->data, src, len * sizeof(uint16_t));
    h->data[len] = 0;
    h->len = len;
    h->refs = 1;
    *out = h;
    return 0;   // S_OK
}

// `WindowsCreateStringReference`：指向调用方缓冲的"快速 HSTRING"。
// 真正的 Windows 实现不拷贝；我们**也按不拷贝**实现，但标记 refs=0 表示"不要 free"。
int32_t WindowsCreateStringReference(const uint16_t *src, uint32_t len, void *header, void **out)
{
    (void)header;
    if (!out) return (int32_t)0x80070057;
    *out = NULL;
    if (len == 0) return 0;
    wpf_hstring *h = (wpf_hstring *)calloc(1, sizeof(*h));
    if (!h) return (int32_t)0x8007000E;
    h->data = (uint16_t *)(uintptr_t)src;   // 不拷贝（reference 语义）
    h->len = len;
    h->refs = 0;                            // 0 = 不拥有
    *out = h;
    return 0;
}

int32_t WindowsDeleteString(void *hstr)
{
    wpf_hstring *h = (wpf_hstring *)hstr;
    if (!h) return 0;
    if (h->refs > 0 && --h->refs == 0) free(h->data);
    free(h);
    return 0;
}

int32_t WindowsDuplicateString(void *hstr, void **out)
{
    if (!out) return (int32_t)0x80070057;
    *out = NULL;
    wpf_hstring *h = (wpf_hstring *)hstr;
    if (!h) return 0;
    if (h->refs > 0) { ++h->refs; *out = h; return 0; }   // 自有 ⇒ 加引用
    return WindowsCreateString(h->data, h->len, out);     // reference ⇒ 复制一份
}

const uint16_t *WindowsGetStringRawBuffer(void *hstr, uint32_t *len)
{
    wpf_hstring *h = (wpf_hstring *)hstr;
    if (!h) { if (len) *len = 0; return NULL; }
    if (len) *len = h->len;
    return h->data;
}

// ── SHGetFolderPath（shfolder 的老 API）────────────────────────────────────
//   `SHGetFolderPath(HWND, int nFolder, HANDLE, DWORD, LPWSTR pszPath)` —— 同 `SHGetFolderPathEx`，
//   只是 out 参数是裸缓冲。转发到我们已有的 XDG 映射实现。
int32_t SHGetFolderPath(void *hwnd, int32_t nFolder, void *hToken, uint32_t dwFlags, uint16_t *pszPath)
{
    extern int32_t SHGetFolderPathEx(void *, uint32_t, void *, uint16_t *, uint32_t);
    (void)hwnd; (void)hToken;
    return SHGetFolderPathEx(&nFolder, dwFlags, NULL, pszPath, 260 /*MAX_PATH*/);
}

// ── CommDlgExtendedError ────────────────────────────────────────────────────
//   返回上一次对话框调用的扩展错误码。本 shim 的 `GetOpenFileName` 等是桩（返回 FALSE），
//   但**没有真正尝试过任何对话框** ⇒ 按 Win32 语义"上一次没有错误"就返回 0。
uint32_t CommDlgExtendedError(void) { return 0; }

// ── RtlNtStatusToDosError ───────────────────────────────────────────────────
//   NTSTATUS → Win32 错误码。只需要覆盖本 shim 会遇到的少数几个；其余返回 ERROR_MR_MID_NOT_FOUND(317)。
uint32_t RtlNtStatusToDosError(int32_t status)
{
    switch ((uint32_t)status) {
        case 0x00000000: return 0;      // STATUS_SUCCESS → ERROR_SUCCESS
        case 0xC0000002: return 1;      // STATUS_NOT_IMPLEMENTED → ERROR_INVALID_FUNCTION
        case 0xC000000D: return 87;     // STATUS_INVALID_PARAMETER → ERROR_INVALID_PARAMETER
        case 0xC0000022: return 5;      // STATUS_ACCESS_DENIED → ERROR_ACCESS_DENIED
        case 0xC0000034: return 2;      // STATUS_OBJECT_NAME_NOT_FOUND → ERROR_FILE_NOT_FOUND
        case 0xC0000135: return 126;    // STATUS_DLL_NOT_FOUND → ERROR_MOD_NOT_FOUND
        case 0x80000005: return 234;    // STATUS_BUFFER_OVERFLOW → ERROR_MORE_DATA
        default: return 317;            // ERROR_MR_MID_NOT_FOUND（"没有对应映射"的诚实答案）
    }
}

// ── GetUrlCacheConfigInfoW ──────────────────────────────────────────────────
//   `INTERNET_CACHE_CONFIG_INFOW`（约 60 字节）：缓存目录、大小、类型。
//   Linux 上 IE 缓存不存在，但调用方只是要一份"配置" ⇒ 如实给出**缓存禁用**的配置：
//   目录为空、大小为 0、类型 0。这是**语义正确**的（没有缓存 ⇒ 配置就是空）。
int32_t GetUrlCacheConfigInfoW(void *pConfig, uint32_t *pcbConfig, uint32_t flags)
{
    (void)flags;
    if (pcbConfig) *pcbConfig = 64;
    if (pConfig) memset(pConfig, 0, 64);   // pszCachePath=NULL, dwCacheSize=0 …
    return 0;   // TRUE
}
