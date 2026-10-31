// win32_kernel32.c —— kernel32 组 21 个 gap 符号的最小实现
//
// 【为什么单独成文件】按集成约定，本组符号全部落在本文件内，不改 `win32_misc.c` /
// `win32_core.c` / 任何公共头 / Makefile —— 集成由主代理统一做。
//
// 【实现口径】能真实现的真实现（getpid/memmove/mmap/malloc/UTF-16→UTF-8），
// 确实在 Linux 上没有对应语义的（Windows 版本号、WOW64、跨进程内存、内核命名对象）
// 返回**明确的失败或合理假值**，并在函数上方一行注释写清理由 —— 不写会崩的空壳。
//
// 【不修改公共头】缺的类型（SIZE_T/LPCVOID/ULONG_PTR/LPDWORD/SYSTEM_INFO/OSVERSIONINFOW…）
// 在本文件内 typedef，避免动 `win32_abi.h`。

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <stdint.h>
#include <sys/mman.h>
#include <time.h>

#include "win32_internal.h"

// ── 本文件内补的 Win32 类型（公共头没有的）──────────────────────────────────
typedef uint64_t SIZE_T;                 // LP64 下与 size_t 同宽
typedef uint64_t DWORD_PTR;
typedef const void *LPCVOID;
typedef uint64_t ULONG_PTR;
typedef DWORD *LPDWORD;
typedef int *LPBOOL;
typedef uint16_t WCHAR;
typedef uint16_t WORD;

#define WPF_FALSE 0
#define WPF_TRUE  1

// Win32 错误码（`SetLastError` 的取值，只列本文件用到的）
#define WPF_ERROR_INVALID_PARAMETER 87u
#define WPF_ERROR_INVALID_HANDLE    6u
#define WPF_ERROR_NOT_ENOUGH_MEMORY 8u
#define WPF_ERROR_FILE_NOT_FOUND    2u
#define WPF_ERROR_CALL_NOT_IMPLEMENTED 120u
#define WPF_ERROR_PARTIAL_COPY      299u
#define WPF_ERROR_NOT_SUPPORTED     50u

// WaitForSingleObject 返回值
#define WPF_WAIT_OBJECT_0  0u
#define WPF_WAIT_TIMEOUT   258u
#define WPF_WAIT_FAILED    0xFFFFFFFFu

// GlobalAlloc 的 flags（只关心 movable/zeroinit 的语义差异，见下）
#define WPF_GMEM_FIXED      0x0000u
#define WPF_GMEM_MOVEABLE   0x0002u
#define WPF_GMEM_ZEROINIT   0x0040u
#define WPF_GMEM_MODIFY     0x0080u

// VirtualAlloc 的 flAllocationType / flProtect
#define WPF_MEM_COMMIT      0x1000u
#define WPF_MEM_RESERVE     0x2000u
#define WPF_MEM_RELEASE     0x8000u
#define WPF_MEM_DECOMMIT    0x4000u
#define WPF_MEM_FREE        0x10000u
#define WPF_PAGE_NOACCESS   0x01u
#define WPF_PAGE_READWRITE  0x04u

// GetSystemInfo 用的结构（x64 布局，共 48 字节；字段顺序与 Win32 一致）
typedef struct wpf_SYSTEM_INFO {
    union {
        DWORD dwOemId;
        struct {
            WORD wProcessorArchitecture;
            WORD wReserved;
        };
    };
    DWORD     dwPageSize;
    LPVOID    lpMinimumApplicationAddress;
    LPVOID    lpMaximumApplicationAddress;
    DWORD_PTR dwActiveProcessorMask;
    DWORD     dwNumberOfProcessors;
    DWORD     dwProcessorType;
    DWORD     dwAllocationGranularity;
    WORD      wProcessorLevel;
    WORD      wProcessorRevision;
} WPF_SYSTEM_INFO;

// GetVersionEx 用的结构（80+256 字节：5 个 DWORD + WCHAR[128]）
typedef struct wpf_OSVERSIONINFOW {
    DWORD dwOSVersionInfoSize;
    DWORD dwMajorVersion;
    DWORD dwMinorVersion;
    DWORD dwBuildNumber;
    DWORD dwPlatformId;
    WCHAR szCSDVersion[128];
} WPF_OSVERSIONINFOW;

// ── 小工具：按 Win32 的 `LPWSTR` 约定（本仓定义为 `uint8_t*`）读写 UTF-16 ─────
static uint16_t wpf_rw16(const uint8_t *p, size_t i)
{
    return (uint16_t)((uint16_t)p[2 * i] | ((uint16_t)p[2 * i + 1] << 8));
}

static void wpf_ww16(uint8_t *p, size_t i, uint16_t v)
{
    p[2 * i]     = (uint8_t)(v & 0xFF);
    p[2 * i + 1] = (uint8_t)((v >> 8) & 0xFF);
}

// ═══════════════════════════════════════════════════════════════════════════
// 一、进程 / 内存信息
// ═══════════════════════════════════════════════════════════════════════════

// 【真实现】Linux 的 pid 与 Windows 的进程 ID 语义等价（都是正整数标识）。
DWORD GetCurrentProcessId(void)
{
    return (DWORD)getpid();
}

// 【真实现】用 Linux 的 `sysconf` 填；架构字段给 x64（0x0009 = PROCESSOR_ARCHITECTURE_AMD64）。
void GetSystemInfo(WPF_SYSTEM_INFO *lpSystemInfo)
{
    if (!lpSystemInfo) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return;
    }
    memset(lpSystemInfo, 0, sizeof(*lpSystemInfo));
    long nproc = sysconf(_SC_NPROCESSORS_ONLN);
    long pagesz = sysconf(_SC_PAGESIZE);
    lpSystemInfo->wProcessorArchitecture = 0x0009;   // PROCESSOR_ARCHITECTURE_AMD64（aarch64 也按 64 位填）
    lpSystemInfo->dwPageSize = (DWORD)(pagesz > 0 ? pagesz : 4096);
    lpSystemInfo->lpMinimumApplicationAddress = (LPVOID)(uintptr_t)0x10000;
    lpSystemInfo->lpMaximumApplicationAddress = (LPVOID)(uintptr_t)0x7FFFFFFFFFFFULL;
    lpSystemInfo->dwActiveProcessorMask = (nproc > 0 && nproc < 64) ? ((1ULL << nproc) - 1ULL) : ~0ULL;
    lpSystemInfo->dwNumberOfProcessors = (DWORD)(nproc > 0 ? nproc : 1);
    lpSystemInfo->dwProcessorType = 0xFFFF;          // PROCESSOR_ARCHITECTURE_UNKNOWN 占位（Win32 已不推荐使用本字段）
    lpSystemInfo->dwAllocationGranularity = (DWORD)(lpSystemInfo->dwPageSize * 16);   // Windows 上是 64KB
    lpSystemInfo->wProcessorLevel = 6;
    lpSystemInfo->wProcessorRevision = 0x3A09;
}

// 【合理假值】Linux 没有"Windows 版本号"这一概念。上游（OSVersionHelper）只拿它做
// 兼容性判断，给 Windows 10（10.0.19041）能让它走"现代代码路径"，比返回 0 更安全。
BOOL GetVersionEx(WPF_OSVERSIONINFOW *lpVersionInformation)
{
    if (!lpVersionInformation) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return WPF_FALSE;
    }
    // 上游会先写 dwOSVersionInfoSize 再调用；这里只填版本字段，不动 size。
    lpVersionInformation->dwMajorVersion = 10;
    lpVersionInformation->dwMinorVersion = 0;
    lpVersionInformation->dwBuildNumber  = 19041;
    lpVersionInformation->dwPlatformId   = 2;   // VER_PLATFORM_WIN32_NT
    memset(lpVersionInformation->szCSDVersion, 0, sizeof(lpVersionInformation->szCSDVersion));
    return WPF_TRUE;
}

// 【合理假值】本机是 64 位 Linux 上直接运行 64 位 CoreCLR，不存在 WOW64 子系统。
// 返回 FALSE（"不是 WOW64 进程"）是**语义上正确**的，而不是占位。
BOOL IsWow64Process(HANDLE hProcess, LPBOOL lpWow64Process)
{
    (void)hProcess;
    if (!lpWow64Process) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return WPF_FALSE;
    }
    *lpWow64Process = WPF_FALSE;
    return WPF_TRUE;
}

// ═══════════════════════════════════════════════════════════════════════════
// 二、堆：GlobalAlloc / GlobalReAlloc / GlobalSize
// ═══════════════════════════════════════════════════════════════════════════
//
// Win32 的 HGLOBAL 是"句柄"，`GMEM_MOVEABLE` 时还要能 `GlobalLock`/`GlobalUnlock`。
// 本仓既没有 `GlobalLock`/`GlobalUnlock` 的 gap 项，上游也只用到 `GMEM_MOVEABLE`
// 拿一块可写内存，所以这里统一用 `malloc` 并维护一张 {句柄, 尺寸} 表。

typedef struct wpf_hglobal_rec {
    struct wpf_hglobal_rec *next;
    void   *ptr;
    SIZE_T  size;
} wpf_hglobal_rec;

static wpf_hglobal_rec *g_hglobals = NULL;

static wpf_hglobal_rec *wpf_hglobal_find(void *ptr)
{
    for (wpf_hglobal_rec *r = g_hglobals; r; r = r->next) {
        if (r->ptr == ptr) return r;
    }
    return NULL;
}

// 【真实现】malloc + 句柄表；GMEM_ZEROINIT 时清零。
HGLOBAL GlobalAlloc(UINT uFlags, SIZE_T dwBytes)
{
    if (dwBytes == 0) {
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    void *p = malloc((size_t)dwBytes);
    if (!p) {
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    if (uFlags & WPF_GMEM_ZEROINIT) memset(p, 0, (size_t)dwBytes);

    wpf_hglobal_rec *r = (wpf_hglobal_rec *)calloc(1, sizeof(*r));
    if (!r) {                                  // 记账失败则整体失败，不泄漏块
        free(p);
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    r->ptr  = p;
    r->size = dwBytes;
    wpf_lock();
    r->next = g_hglobals;
    g_hglobals = r;
    wpf_unlock();
    return (HGLOBAL)p;
}

// 【真实现】realloc 并更新表里的尺寸。注意：realloc 可能搬移地址，
// Win32 上 `GMEM_MOVEABLE` 本来就允许句柄不变而指针变，这里直接把新指针当句柄返回。
HGLOBAL GlobalReAlloc(HGLOBAL hMem, SIZE_T dwBytes, UINT uFlags)
{
    (void)uFlags;
    if (!hMem) return GlobalAlloc(uFlags, dwBytes);
    if (dwBytes == 0) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return NULL;
    }
    wpf_lock();
    wpf_hglobal_rec *r = wpf_hglobal_find(hMem);
    void *oldp = hMem;
    wpf_unlock();
    if (!r) {
        // 不是我们分配的块：Win32 会返回 NULL + ERROR_INVALID_HANDLE
        wpf_set_last_error(WPF_ERROR_INVALID_HANDLE);
        return NULL;
    }
    void *np = realloc(oldp, (size_t)dwBytes);
    if (!np) {
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    wpf_lock();
    r = wpf_hglobal_find(oldp);
    if (r) { r->ptr = np; r->size = dwBytes; }
    wpf_unlock();
    return (HGLOBAL)np;
}

// 【真实现】查表返回尺寸；不是我们的块返回 0。
SIZE_T GlobalSize(HGLOBAL hMem)
{
    if (!hMem) return 0;
    wpf_lock();
    wpf_hglobal_rec *r = wpf_hglobal_find(hMem);
    SIZE_T sz = r ? r->size : 0;
    wpf_unlock();
    return sz;
}

// ═══════════════════════════════════════════════════════════════════════════
// 三、虚拟内存：VirtualAlloc / VirtualFree（含 Ex 变体）
// ═══════════════════════════════════════════════════════════════════════════
//
// 用 mmap/munmap。Win32 的 reserve/commit 两段式在 Linux 上没有直接对应：
// 这里把 MEM_RESERVE|MEM_COMMIT 都映射成"立刻映射可读写"，满足上游"拿到可用内存"的需求。

typedef struct wpf_vm_rec {
    struct wpf_vm_rec *next;
    void  *addr;
    SIZE_T size;
} wpf_vm_rec;

static wpf_vm_rec *g_vms = NULL;

static wpf_vm_rec *wpf_vm_find(void *addr)
{
    for (wpf_vm_rec *r = g_vms; r; r = r->next) {
        if (r->addr == addr) return r;
    }
    return NULL;
}

static LPVOID wpf_vm_alloc(LPVOID lpAddress, SIZE_T dwSize, DWORD flAllocationType, DWORD flProtect)
{
    if (dwSize == 0) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return NULL;
    }
    long pagesz = sysconf(_SC_PAGESIZE);
    size_t page = (size_t)(pagesz > 0 ? pagesz : 4096);
    size_t size = (size_t)dwSize;
    size = (size + page - 1) / page * page;          // 向上取整到页

    int prot = PROT_READ | PROT_WRITE;
    if ((flProtect & WPF_PAGE_READWRITE) == 0 && (flProtect & WPF_PAGE_NOACCESS) != 0) {
        prot = PROT_NONE;
    }
    // MAP_FIXED 只在调用方明确给了地址、且要求"就放在这"时用；否则让内核选。
    void *want = lpAddress;
    void *p = mmap(want, size, prot,
                   MAP_PRIVATE | MAP_ANONYMOUS | (want ? MAP_FIXED : 0), -1, 0);
    if (p == MAP_FAILED) {
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    // MEM_RESERVE 单用时内容未定义；MEM_COMMIT 带 0 则清过（mmap 天然清零），符合 MEM_COMMIT 语义。
    (void)flAllocationType;

    wpf_vm_rec *r = (wpf_vm_rec *)calloc(1, sizeof(*r));
    if (!r) {
        munmap(p, size);
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return NULL;
    }
    r->addr = p;
    r->size = (SIZE_T)size;
    wpf_lock();
    r->next = g_vms;
    g_vms = r;
    wpf_unlock();
    return p;
}

static BOOL wpf_vm_free(LPVOID lpAddress, DWORD dwFreeType)
{
    if (!lpAddress) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return WPF_FALSE;
    }
    (void)dwFreeType;   // MEM_RELEASE / MEM_DECOMMIT 在 mmap 下都按"整块释放"处理
    wpf_lock();
    wpf_vm_rec *r = wpf_vm_find(lpAddress);
    SIZE_T size = r ? r->size : 0;
    wpf_unlock();
    if (!r) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return WPF_FALSE;
    }
    if (munmap(lpAddress, (size_t)size) != 0) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return WPF_FALSE;
    }
    wpf_lock();
    for (wpf_vm_rec **pp = &g_vms; *pp; pp = &(*pp)->next) {
        if (*pp == r) { *pp = r->next; break; }
    }
    wpf_unlock();
    free(r);
    return WPF_TRUE;
}

// 【真实现】mmap 一块可读写内存。
LPVOID VirtualAlloc(LPVOID lpAddress, SIZE_T dwSize, DWORD flAllocationType, DWORD flProtect)
{
    return wpf_vm_alloc(lpAddress, dwSize, flAllocationType, flProtect);
}

// 【真实现·限本进程】本机不支持给**别的进程**分配内存（Linux 无 ptrace 以外的合法手段，
// 且上游只对 `GetCurrentProcess()` 用）。只认伪句柄 `(HANDLE)-1`，其余明确失败。
LPVOID VirtualAllocEx(HANDLE hProcess, LPVOID lpAddress, SIZE_T dwSize,
                      DWORD flAllocationType, DWORD flProtect)
{
    if ((uintptr_t)hProcess != (uintptr_t)-1) {
        wpf_set_last_error(WPF_ERROR_NOT_SUPPORTED);
        return NULL;
    }
    return wpf_vm_alloc(lpAddress, dwSize, flAllocationType, flProtect);
}

// 【真实现】munmap。
BOOL VirtualFree(LPVOID lpAddress, SIZE_T dwSize, DWORD dwFreeType)
{
    (void)dwSize;
    return wpf_vm_free(lpAddress, dwFreeType);
}

// 【真实现·限本进程】同 VirtualAllocEx 的理由。
BOOL VirtualFreeEx(HANDLE hProcess, LPVOID lpAddress, SIZE_T dwSize, DWORD dwFreeType)
{
    if ((uintptr_t)hProcess != (uintptr_t)-1) {
        wpf_set_last_error(WPF_ERROR_NOT_SUPPORTED);
        return WPF_FALSE;
    }
    (void)dwSize;
    return wpf_vm_free(lpAddress, dwFreeType);
}

// ═══════════════════════════════════════════════════════════════════════════
// 四、跨进程内存读写
// ═══════════════════════════════════════════════════════════════════════════
//
// 本机没有合法的跨进程内存访问通道（`/proc/<pid>/mem` 需要 ptrace 权限，
// 且从托管代码里做不安全）。**只对当前进程的伪句柄 `(HANDLE)-1` 真实现**，
// 其余明确返回 ERROR_NOT_SUPPORTED —— 不假装成功。

// 【真实现·限本进程】memmove（允许区间重叠，和 RtlMoveMemory 语义一致）。
BOOL ReadProcessMemory(HANDLE hProcess, LPCVOID lpBaseAddress, LPVOID lpBuffer,
                       SIZE_T nSize, SIZE_T *lpNumberOfBytesRead)
{
    if ((uintptr_t)hProcess != (uintptr_t)-1) {
        wpf_set_last_error(WPF_ERROR_NOT_SUPPORTED);
        if (lpNumberOfBytesRead) *lpNumberOfBytesRead = 0;
        return WPF_FALSE;
    }
    if (!lpBaseAddress || !lpBuffer) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        if (lpNumberOfBytesRead) *lpNumberOfBytesRead = 0;
        return WPF_FALSE;
    }
    memmove(lpBuffer, lpBaseAddress, (size_t)nSize);
    if (lpNumberOfBytesRead) *lpNumberOfBytesRead = nSize;
    return WPF_TRUE;
}

// 【真实现·限本进程】同上。
BOOL WriteProcessMemory(HANDLE hProcess, LPVOID lpBaseAddress, LPCVOID lpBuffer,
                        SIZE_T nSize, SIZE_T *lpNumberOfBytesWritten)
{
    if ((uintptr_t)hProcess != (uintptr_t)-1) {
        wpf_set_last_error(WPF_ERROR_NOT_SUPPORTED);
        if (lpNumberOfBytesWritten) *lpNumberOfBytesWritten = 0;
        return WPF_FALSE;
    }
    if (!lpBaseAddress || !lpBuffer) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        if (lpNumberOfBytesWritten) *lpNumberOfBytesWritten = 0;
        return WPF_FALSE;
    }
    memmove(lpBaseAddress, lpBuffer, (size_t)nSize);
    if (lpNumberOfBytesWritten) *lpNumberOfBytesWritten = nSize;
    return WPF_TRUE;
}

// 【真实现】就是 memmove —— `RtlMoveMemory` 在 Win32 里明确允许重叠。
void RtlMoveMemory(LPVOID Destination, LPCVOID Source, SIZE_T Length)
{
    if (!Destination || !Source || Length == 0) return;
    memmove(Destination, Source, (size_t)Length);
}

// ═══════════════════════════════════════════════════════════════════════════
// 五、字符串：WideCharToMultiByte / lstrlen / FormatMessageW
// ═══════════════════════════════════════════════════════════════════════════

#define WPF_CP_ACP   0u
#define WPF_CP_UTF8  65001u
#define WPF_CP_UTF16 1200u

// 【真实现】UTF-16（LPWSTR 在本仓是 uint8_t*，按小端字节读）→ UTF-8。
// `lpMultiByteStr == NULL` 时按 Win32 约定返回所需字节数（不含结尾 0）。
int WideCharToMultiByte(UINT CodePage, DWORD dwFlags,
                        LPCWSTR lpWideCharStr, int cchWideChar,
                        LPSTR lpMultiByteStr, int cbMultiByte,
                        LPCSTR lpDefaultChar, LPBOOL lpUsedDefaultChar)
{
    (void)dwFlags;
    (void)lpDefaultChar;
    if (lpUsedDefaultChar) *lpUsedDefaultChar = WPF_FALSE;

    if (!lpWideCharStr) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return 0;
    }
    if (CodePage != WPF_CP_UTF8 && CodePage != WPF_CP_ACP) {
        // 本实现只支持 UTF-8（CP_ACP 在 Linux 上也按 UTF-8 处理，因为 locale 是 UTF-8）
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return 0;
    }

    size_t wlen = (cchWideChar < 0) ? 0 : (size_t)cchWideChar;
    if (cchWideChar < 0) {                       // -1：以 NUL 结尾
        while (wpf_rw16(lpWideCharStr, wlen) != 0) wlen++;
    }

    const uint8_t *w = lpWideCharStr;
    int need = 0;
    for (size_t i = 0; i < wlen; i++) {
        uint32_t cp = wpf_rw16(w, i);
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < wlen) {
            uint32_t lo = wpf_rw16(w, i + 1);
            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                i++;
            }
        }
        need += (cp < 0x80) ? 1 : (cp < 0x800 ? 2 : (cp < 0x10000 ? 3 : 4));
    }

    if (!lpMultiByteStr || cbMultiByte <= 0) return need;   // 查询长度
    if (need > cbMultiByte) need = cbMultiByte;             // 截断到容量（Win32 语义：不越界写）

    int n = 0;
    for (size_t i = 0; i < wlen && n < need; i++) {
        uint32_t cp = wpf_rw16(w, i);
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < wlen) {
            uint32_t lo = wpf_rw16(w, i + 1);
            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                i++;
            }
        }
        uint8_t *o = lpMultiByteStr;
        if (cp < 0x80) {
            if (n + 1 > need) break;
            o[n++] = (uint8_t)cp;
        } else if (cp < 0x800) {
            if (n + 2 > need) break;
            o[n++] = (uint8_t)(0xC0 | (cp >> 6));
            o[n++] = (uint8_t)(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            if (n + 3 > need) break;
            o[n++] = (uint8_t)(0xE0 | (cp >> 12));
            o[n++] = (uint8_t)(0x80 | ((cp >> 6) & 0x3F));
            o[n++] = (uint8_t)(0x80 | (cp & 0x3F));
        } else {
            if (n + 4 > need) break;
            o[n++] = (uint8_t)(0xF0 | (cp >> 18));
            o[n++] = (uint8_t)(0x80 | ((cp >> 12) & 0x3F));
            o[n++] = (uint8_t)(0x80 | ((cp >> 6) & 0x3F));
            o[n++] = (uint8_t)(0x80 | (cp & 0x3F));
        }
    }
    return n;
}

// 【真实现】`lstrlen` 在清单里没有 W/A 后缀 ⇒ 按 ANSI（`LPCSTR`）实现，返回字节数。
// 上游若需要宽字符版本会调 `lstrlenW`（清单里没有该项，故不导出，避免多余导出）。
int lstrlen(LPCSTR lpString)
{
    if (!lpString) return 0;
    return (int)strlen((const char *)lpString);
}

// 【真实现·简化版】把消息 ID 渲染成 "错误码 N" 的 UTF-16 文本。
// 本机没有 Windows 的 message table（`FormatMessage` 的本质是查 `System32` 下的消息资源），
// 所以**不做精确文案翻译**，但返回**格式合法的成功结果**而不是失败。
DWORD FormatMessageW(DWORD dwFlags, LPCVOID lpSource, DWORD dwMessageId,
                     DWORD dwLanguageId, LPWSTR lpBuffer, DWORD nSize, void *Arguments)
{
    (void)dwFlags;
    (void)lpSource;
    (void)dwLanguageId;
    (void)Arguments;
    if (!lpBuffer || nSize == 0) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return 0;
    }
    // 组装 ASCII 文本 "error N"，再写进 UTF-16 缓冲
    char ascii[64];
    int k = snprintf(ascii, sizeof(ascii), "error %u", (unsigned)dwMessageId);
    if (k < 0) k = 0;

    uint8_t *out = lpBuffer;
    DWORD cap = nSize - 1;                    // 留出结尾 NUL
    DWORD i = 0;
    for (; i < (DWORD)k && i < cap; i++) {
        wpf_ww16(out, i, (uint16_t)(uint8_t)ascii[i]);
    }
    wpf_ww16(out, i, 0);
    return i;                                 // 返回写入的 UTF-16 字符数（不含 NUL）
}

// ═══════════════════════════════════════════════════════════════════════════
// 六、同步 / 内核对象
// ═══════════════════════════════════════════════════════════════════════════

// 【合理假值】本机没有 Windows 的内核等待对象（事件/互斥/信号量）。
// 上游只对 `dwMilliseconds == 0` 做"非阻塞探测"，此时按 Win32 语义返回
// `WAIT_TIMEOUT`（对象未 signaling）是**安全且诚实**的；
// 其它情况返回 `WAIT_FAILED` + `ERROR_INVALID_HANDLE`，让调用方走错误分支而不是死等。
DWORD WaitForSingleObject(HANDLE hHandle, DWORD dwMilliseconds)
{
    if (!hHandle || (uintptr_t)hHandle == (uintptr_t)-1 || (uintptr_t)hHandle == (uintptr_t)-2) {
        // 伪句柄（当前进程/线程）在 Windows 上也不会因为"等待自己"而 signaling。
        if (dwMilliseconds == 0) return WPF_WAIT_TIMEOUT;
        wpf_set_last_error(WPF_ERROR_INVALID_HANDLE);
        return WPF_WAIT_FAILED;
    }
    if (dwMilliseconds == 0) return WPF_WAIT_TIMEOUT;
    wpf_set_last_error(WPF_ERROR_INVALID_HANDLE);
    return WPF_WAIT_FAILED;
}

// 【合理假值】命名文件映射对象是 Windows 内核命名空间里的东西，Linux 上没有对应物。
// 返回 NULL + ERROR_FILE_NOT_FOUND（Win32 在对象不存在时的标准错误），而不是假装成功。
HANDLE OpenFileMapping(DWORD dwDesiredAccess, BOOL bInheritHandle, LPCWSTR lpName)
{
    (void)dwDesiredAccess;
    (void)bInheritHandle;
    (void)lpName;
    wpf_set_last_error(WPF_ERROR_FILE_NOT_FOUND);
    return NULL;
}

// ═══════════════════════════════════════════════════════════════════════════
// 七、全局原子
// ═══════════════════════════════════════════════════════════════════════════
//
// Windows 的 atom 是"字符串 ↔ 16 位整数"的进程/系统级映射。
// 这里维护**本进程内**的字符串表，编号从 0xC000 起（Win32 自定义 atom 的起点）。

typedef struct wpf_atom_rec {
    struct wpf_atom_rec *next;
    ATOM  atom;
    char *str;
} wpf_atom_rec;

static wpf_atom_rec *g_atoms = NULL;
static ATOM g_atom_next = 0xC000;

static char *wpf_k32_utf16_dup(LPCWSTR w)
{
    if (!w) return NULL;
    size_t wlen = 0;
    while (wpf_rw16((const uint8_t *)w, wlen) != 0) wlen++;
    // 最坏 3 字节/字符
    char *out = (char *)malloc(wlen * 3 + 1);
    if (!out) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < wlen; i++) {
        uint32_t cp = wpf_rw16((const uint8_t *)w, i);
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < wlen) {
            uint32_t lo = wpf_rw16((const uint8_t *)w, i + 1);
            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                i++;
            }
        }
        if (cp < 0x80) out[n++] = (char)cp;
        else if (cp < 0x800) {
            out[n++] = (char)(0xC0 | (cp >> 6));
            out[n++] = (char)(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            out[n++] = (char)(0xE0 | (cp >> 12));
            out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[n++] = (char)(0x80 | (cp & 0x3F));
        } else {
            out[n++] = (char)(0xF0 | (cp >> 18));
            out[n++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            out[n++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            out[n++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    out[n] = 0;
    return out;
}

// 【真实现】同一个字符串返回同一个 atom（幂等），和 Win32 语义一致。
ATOM GlobalAddAtom(LPCWSTR lpString)
{
    char *s = wpf_k32_utf16_dup(lpString);
    if (!s) {
        wpf_set_last_error(WPF_ERROR_INVALID_PARAMETER);
        return 0;
    }
    wpf_lock();
    for (wpf_atom_rec *r = g_atoms; r; r = r->next) {
        if (strcmp(r->str, s) == 0) {
            ATOM a = r->atom;
            wpf_unlock();
            free(s);
            return a;
        }
    }
    wpf_atom_rec *r = (wpf_atom_rec *)calloc(1, sizeof(*r));
    if (!r || g_atom_next == 0) {
        wpf_unlock();
        free(s);
        wpf_set_last_error(WPF_ERROR_NOT_ENOUGH_MEMORY);
        return 0;
    }
    r->atom = g_atom_next++;
    r->str  = s;
    r->next = g_atoms;
    g_atoms = r;
    ATOM a = r->atom;
    wpf_unlock();
    return a;
}

// ═══════════════════════════════════════════════════════════════════════════
// 八、错误码
// ═══════════════════════════════════════════════════════════════════════════

// 【真实现】写进 shim 的线程局部 last-error（与 `GetLastError` 成对）。
void SetLastError(DWORD dwErrCode)
{
    wpf_set_last_error(dwErrCode);
}

// ── 【ACC-028 / K-154 主代理补】GetThreadLocale ─────────────────────────────
//   来源：**不是 WPF 上游声明的**（grep 40-build/upstream/wpf/src 为空），
//   而是 WinForms / CoreCLR 侧在运行时调 KERNEL32!GetThreadLocale。
//   实测（/tmp/wfb-ex2.log 的 FirstChanceException 钩子）：
//     EntryPointNotFoundException: Unable to find an entry point named
//     'GetThreadLocale' in shared library 'KERNEL32.dll'.
//   它是**第一个**首 chance 异常，顺着 ButtonBaseAdapter.ColorOptions.Calculate()
//   -> DeviceContextExtensions.ToHdcScope() 一路污染 WinForms 的 ExceptionWhilePainting 状态。
//   语义：返回当前线程的 LCID。Linux 无 LCID 概念，按 LANG/LC_ALL 粗映射，
//   缺省 0x0409（en-US）—— WinForms 期望的中性英文，不触发 RTL/复杂脚本分支。
DWORD GetThreadLocale(void)
{
    static DWORD cached = 0;
    if (cached) return cached;
    const char *lang = getenv("LC_ALL");
    if (!lang || !*lang) lang = getenv("LC_CTYPE");
    if (!lang || !*lang) lang = getenv("LANG");
    if (lang && lang[0] == 'z' && lang[1] == 'h') {
        // zh_CN -> 0x0804, zh_TW -> 0x0404
        cached = (lang[3] == 'T' || lang[3] == 't' || (lang[3] == '_' && lang[4] == 'T')) ? 0x0404u : 0x0804u;
    } else if (lang && lang[0] == 'j' && lang[1] == 'a') {
        cached = 0x0411u;   // ja-JP
    } else {
        cached = 0x0409u;   // en-US（缺省）
    }
    return cached;
}
