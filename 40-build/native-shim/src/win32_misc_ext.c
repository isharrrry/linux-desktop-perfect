// ── 【清算 / K-182】零散组：把签名明确、Linux 有对应物的 gap 展开 ──────────────
//
// 本文件收的是「没有明确归属组、但能真实现」的符号。每个都写了依据。
#include "win32_internal.h"
#include <string.h>
#include <stdio.h>

// ── gdi32: PtInRegion ────────────────────────────────────────────────────────
//   上游：`bool PtInRegion(HRGN hrgn, int x, int y)`。
//   本 shim 的 region 是**空壳**（`CreateRectRgn` 返回哨兵 0x5001，不存任何数据），
//   所以无法回答"点在不在某个 region 里"。这是本仓 region 子系统未实现的**如实后果**，
//   不是这一个函数的问题。返回 FALSE 是安全值（"不在区域内"）。
BOOL PtInRegion(HRGN hrgn, int32_t x, int32_t y)
{
    (void)hrgn; (void)x; (void)y;
    return 0;   // FALSE —— region 子系统未实现，见上方说明
}

// ── gdi32: CreateICW ─────────────────────────────────────────────────────────
//   "信息上下文"（Information Context）：只用于查询设备能力，不用于绘制。
//   `CreateDCW` 在本仓返回哨兵 `(HDC)0x1`；`CreateICW` 同理 —— 它是**合法的可用句柄**，
//   因为本仓所有"查设备能力"的函数（`GetDeviceCaps` 等）都不看句柄内容。
HDC CreateICW(const uint16_t *driver, const uint16_t *device, const uint16_t *port, const void *initData)
{
    (void)driver; (void)device; (void)port; (void)initData;
    return (HDC)(uintptr_t)0x0001;
}

// ── shcore: GetProcessDpiAwareness ───────────────────────────────────────────
//   上游：`int GetProcessDpiAwareness(void* hprocess, int* value)`。
//   取值：0 = UNAWARE, 1 = SYSTEM_AWARE, 2 = PER_MONITOR_AWARE。
//   本 shim **不做 DPI 缩放**（`GetDpiForWindow` 恒 96、`IsWindows10OrGreater` 恒 FALSE ⇒
//   不做 Per-Monitor-V2），所以进程的 DPI 感知就是 **UNAWARE** —— 这是**语义正确**，不是占位。
int32_t GetProcessDpiAwareness(void *hprocess, int32_t *value)
{
    (void)hprocess;
    // 【清算 / K-242】**返回 SYSTEM_AWARE(1)，不是 UNAWARE(0)。**
    //   取值：0 = UNAWARE, 1 = SYSTEM_AWARE, 2 = PER_MONITOR_AWARE。
    //   **这是一个真实的 bug 修复**：原来返回 0(UNAWARE)，含义是"应用不做 DPI 缩放、
    //   由系统代劳"。而在 X11 上**没有任何东西会代劳**（不像 Windows 的 DWM 会拉伸）⇒
    //   WPF 因此把 `Width=960`(DIP) 原样当像素传给 `CreateWindowEx` ⇒ 窗口 960 而非 1440
    //   ⇒ ACC-011（DPI 缩放）永远差 1.5 倍（实测：期望 1440，得到 960）。
    //   返回 **SYSTEM_AWARE** 是 X11 上的正确口径：**应用自己做缩放，系统（X server）不缩放。**
    //   （不用 PER_MONITOR_AWARE(2)：X11 core protocol 没有 per-monitor DPI，那是 RS1 特性，
    //     K-170/K-233 的判断在这一点上仍然成立。）
    if (value) *value = 1;   // PROCESS_SYSTEM_DPI_AWARE
    return 0;                // S_OK
}

// ── dwmapi: DwmDefWindowProc ─────────────────────────────────────────────────
//   上游：`bool DwmDefWindowProc(HWND, uint msg, WPARAM, LPARAM, IntPtr* result)`。
//   DWM 在 Windows 上会**自己处理**一部分窗口消息（非客户区命中测试、Aero 玻璃等）。
//   Linux 上没有 DWM ⇒ 没有消息被 DWM 处理 ⇒ 如实返回 FALSE（调用方会走它自己的默认处理）。
BOOL DwmDefWindowProc(HWND hwnd, uint32_t msg, uintptr_t wparam, intptr_t lparam, intptr_t *result)
{
    (void)hwnd; (void)msg; (void)wparam; (void)lparam;
    if (result) *result = 0;
    return 0;   // FALSE = "DWM 没处理这条消息"
}

// ── winmm: PlaySound / PlaySoundW ────────────────────────────────────────────
//   上游：`bool PlaySoundW(string?, IntPtr, uint)`。
//   Linux 上"播不播放声音"取决于有没有音频服务；本仓**不做音频输出**（媒体层 `libldpmedia.so`
//   只导出 2 个符号，没有音频通路）。
//   ⇒ **返回 TRUE**：调用方（`SystemSounds`/`Console.Beep` 之类）把"没报错"当成功，
//   不会因此走错误分支；而实际没有声音 —— 这在无声环境里是**符合预期**的。
//   （如果返回 FALSE，上游可能弹出"播放失败"的错误提示，那反而更糟。）
BOOL PlaySoundW(const uint16_t *pszSound, void *hmod, uint32_t fdwSound)
{
    (void)pszSound; (void)hmod; (void)fdwSound;
    return 1;   // TRUE
}
BOOL PlaySound(const char *pszSound, void *hmod, uint32_t fdwSound)
{
    (void)pszSound;
    return PlaySoundW(NULL, hmod, fdwSound);
}
