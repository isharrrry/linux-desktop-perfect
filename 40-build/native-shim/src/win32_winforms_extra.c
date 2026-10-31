// ── 【ACC-028 / K-155 主代理】WinForms 运行时真正会调、但清单里没有的符号 ────────
//
// 为什么这个文件存在：
//   `exports.txt` 的符号面是从 **WPF 上游**（40-build/upstream/wpf/src）的 `[DllImport]` 扫出来的。
//   **WinForms 的 DllImport 面从来没被纳入** —— 所以有一批符号「清单里查不到，但运行时被调」，
//   触发 `EntryPointNotFoundException`，进而污染 WinForms 的 `ExceptionWhilePainting` 状态，
//   导致窗口永久空白（K-153/K-154）。
//
// 实测来源：
//   1. `GetThreadLocale` —— 子代理的 FirstChanceException 日志 `/tmp/wfb-ex2.log` 里是**第一个**异常。
//      （已在 win32_kernel32.c 里补，本文件不再重复定义。）
//   2. 其余 5 个 —— 主代理扫 `40-build/upstream/winforms/src/**/*.cs` 的 `[DllImport]` 得到：
//        2284 个 .cs，涉及 6 个库（Kernel32/COMDLG32/advapi32/shell32/ntdll），
//        我们该提供 8 个，`.so` 里缺 5 个，就是下面这些。
//
// 口径（诚实降级，不伪造成功）：
//   这 5 个在 Linux 上都**没有对应物**，一律返回"明确的失败 + 正确的错误码"或"中性的成功值"，
//   **不填垃圾数据**。每个函数上方写明理由。
#include "win32_internal.h"
#include <string.h>

// APPMODEL_ERROR_NO_APPLICATION：当前进程没有 AppUserModelID
#define WPF_APPMODEL_ERROR_NO_APPLICATION 15700u

// 【WinForms 用】取当前进程的 AppUserModelID（任务栏分组用）。
//   Linux 没有 AppModel 概念 ⇒ 返回「无 Application」标准错误，调用方会回退到默认分组。
long GetCurrentApplicationUserModelId(uint32_t *applicationUserModelIdLength, uint16_t *applicationUserModelId)
{
    if (applicationUserModelIdLength) *applicationUserModelIdLength = 0;
    (void)applicationUserModelId;
    wpf_set_last_error(WPF_APPMODEL_ERROR_NO_APPLICATION);
    return (long)WPF_APPMODEL_ERROR_NO_APPLICATION;   // 非 0 = 失败（LONG）
}

// PRODUCT_PROFESSIONAL
#define WPF_PRODUCT_PROFESSIONAL 0x00000030u

// 【WinForms 用】取 Windows 产品类型（`SystemInformation` 判 SKU 用）。
//   Linux 无 SKU ⇒ 返回 TRUE 并给「专业版」——这是 WinForms 期望的常见档位，
//   会让它走常规（非 Server/非 Basic）分支，比返回 0 更安全。
int GetProductInfo(uint32_t osMajor, uint32_t osMinor, uint32_t spMajor, uint32_t spMinor, uint32_t *returnedProductType)
{
    (void)osMajor; (void)osMinor; (void)spMajor; (void)spMinor;
    if (returnedProductType) *returnedProductType = WPF_PRODUCT_PROFESSIONAL;
    return 1;   // TRUE
}

// 【WinForms 用】托盘图标（`NotifyIcon`）。
//   本 shim 没有系统托盘（X11 侧未实现 StatusNotifier/AppIndicator）⇒ 返回 TRUE 但**不显示**。
//   ⚠️ 这是「返回成功但没真做」：调用 `NotifyIcon.Visible = true` 会静默无效。
int Shell_NotifyIconW(uint32_t dwMessage, const void *lpData)
{
    (void)dwMessage; (void)lpData;
    return 1;   // TRUE
}

// 【WinForms 用】取进程令牌（`SystemInformation`/权限判断）。
//   Linux 无 Windows 访问令牌 ⇒ 标准失败 + ERROR_CALL_NOT_IMPLEMENTED（120）。
int OpenProcessToken(void *processHandle, uint32_t desiredAccess, void **tokenHandle)
{
    (void)processHandle; (void)desiredAccess;
    if (tokenHandle) *tokenHandle = 0;
    wpf_set_last_error(120);   // ERROR_CALL_NOT_IMPLEMENTED
    return 0;                  // FALSE
}

// 【WinForms 用】取令牌信息。
//   同上 ⇒ 标准失败。注意：`SystemInformation` 对它是 **try/catch 兜底**的，失败是正常路径。
int GetTokenInformation(void *tokenHandle, int tokenInformationClass, void *tokenInformation,
                        uint32_t tokenInformationLength, uint32_t *returnLength)
{
    (void)tokenHandle; (void)tokenInformationClass; (void)tokenInformation; (void)tokenInformationLength;
    if (returnLength) *returnLength = 0;
    wpf_set_last_error(6);     // ERROR_INVALID_HANDLE
    return 0;                  // FALSE
}
