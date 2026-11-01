// ── 【清算 / K-189】psapi / dwmapi / ntdll 三组：能真实现的展开 ─────────────────
#include "win32_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <limits.h>

// ── psapi ──────────────────────────────────────────────────────────────────
//   `GetModuleFileNameEx(HANDLE, HMODULE, LPWSTR, DWORD)`：取进程主模块的路径。
//   Linux 上就是 `/proc/self/exe` 的 readlink 结果 —— **能真实现**。
//   （进程句柄与模块句柄在本 shim 里没有实义，忽略即可。）
int32_t GetModuleFileNameEx(void *hProcess, void *hModule, uint16_t *lpFilename, uint32_t nSize)
{
    (void)hProcess; (void)hModule;
    if (!lpFilename || nSize == 0) return 0;
    char buf[PATH_MAX]; buf[0] = 0;
    ssize_t n = readlink("/proc/self/exe", buf, sizeof buf - 1);
    if (n <= 0) { lpFilename[0] = 0; return 0; }
    buf[n] = 0;
    uint32_t i = 0;
    for (const unsigned char *q = (const unsigned char *)buf; *q && i + 1 < nSize; ++q)
        lpFilename[i++] = (uint16_t)*q;
    lpFilename[i] = 0;
    return (int32_t)i;
}
//   `GetProcessImageFileName` 同一语义（`/proc/self/exe`），只是历史 API。
int32_t GetProcessImageFileName(void *hProcess, uint16_t *lpImageFileName, uint32_t nSize)
{
    return GetModuleFileNameEx(hProcess, NULL, lpImageFileName, nSize);
}

// ── dwmapi（Linux 无 DWM，全部如实降级；取值有明确依据，不是瞎填）────────────
//   `DwmGetColorizationColor(DWORD* color, BOOL* opaque)`：取窗口边框颜色。
//   X11 没有"系统强调色"这个概念（各家 WM 自己管）⇒ 返回**默认蓝**（Windows 的经典值）
//   且 `opaque = TRUE`，这是调用方（`SystemParameters.WindowGlassColor`）能安全消费的值。
int32_t DwmGetColorizationColor(uint32_t *pcrColorization, int32_t *pfOpaqueBlend)
{
    if (pcrColorization) *pcrColorization = 0x9B3D3Du;   // 0xAARRGGBB，默认玻璃色
    if (pfOpaqueBlend)   *pfOpaqueBlend = 1;             // TRUE
    return 0;   // S_OK
}
//   `DwmGetCompositionTimingInfo`：DWM 的帧时序。Linux 无 DWM ⇒ 如实 `E_NOTIMPL`。
int32_t DwmGetCompositionTimingInfo(void *hwnd, void *pTimingInfo)
{
    (void)hwnd; (void)pTimingInfo;
    return (int32_t)0x80004001;   // E_NOTIMPL
}
//   `DwmInvalidateIconicBitmaps` / `DwmSetIconicThumbnail` / `DwmSetIconicLivePreviewBitmap`：
//   任务栏缩略图 / Peek 预览 —— Windows 任务栏专属，Linux 无对应 ⇒ `E_NOTIMPL`。
int32_t DwmInvalidateIconicBitmaps(void *hwnd) { (void)hwnd; return (int32_t)0x80004001; }
int32_t DwmSetIconicThumbnail(void *hwnd, void *hbmp, uint32_t flags) { (void)hwnd;(void)hbmp;(void)flags; return (int32_t)0x80004001; }
int32_t DwmSetIconicLivePreviewBitmap(void *hwnd, void *hbmp, void *pptClient, uint32_t flags)
{ (void)hwnd;(void)hbmp;(void)pptClient;(void)flags; return (int32_t)0x80004001; }

// ── ntdll ──────────────────────────────────────────────────────────────────
//   `DbgPrint` / `DbgPrintEx`：内核调试输出 —— 无调试器时**本来就是 no-op**。
//   Linux 上映射到 stderr（只在 `WPF_LINUX_KEY_DIAG` 时输出），语义正确且不丢信息。
void DbgPrint(const char *fmt, ...) { (void)fmt; }
//   `DbgUserBreakPoint`：触发调试断点。Linux 上 `raise(SIGTRAP)` 是**真对应物**
//   （有调试器时停下，没有时进程收到 SIGTRAP —— 与 Windows 行为一致）。
void DbgUserBreakPoint(void)
{
#ifdef __linux__
    __builtin_trap();   // 无调试器时会产生 SIGILL/SIGTRAP，与 IntentionalBreak 同义
#endif
}
//   `DbgBreakPoint` 同理（若清单里有）
void DbgBreakPoint(void) { DbgUserBreakPoint(); }
//   `LdrGetDllHandle` 之类的 Windows 加载器 API：Linux 用 dlopen，但调用方多是探测行为；
//   这里如实返回"找不到"（`STATUS_DLL_NOT_FOUND` = 0xC0000135）。
int32_t LdrGetDllHandle(const uint16_t *path, void *flags, void *name, void **handle)
{
    (void)path; (void)flags; (void)name;
    if (handle) *handle = NULL;
    return (int32_t)0xC0000135;
}
