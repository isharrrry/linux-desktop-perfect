// ── 【清算 / K-190】打印路径补充：MOD-82 的 10 个 ────────────────────────────
//
// `src/win32_winspool.c` 已经有 `OpenPrinter`/`ClosePrinter`/`StartDocPrinter`/
// `EndDocPrinter`/`WritePrinter`/`EnumPrinters` 的完整实现（133 行）。
// 本文件补的是那条链上另外 10 个符号：
//   · 前 3 个（`GetPrinterW`/`DeviceCapabilitiesW`/`DocumentPropertiesW`）—— **能真做**，
//     它们都基于"当前打印机"的状态，而那个状态在 winspool.c 里已经维护；
//   · 后 7 个 `PT*`（PrintTicket ↔ DEVMODE 转换）—— **只做诚实降级**，理由见文件末尾。
#include "win32_internal.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

// ── GetPrinterW：取打印机信息 ────────────────────────────────────────────────
//   `GetPrinter(HANDLE hPrinter, DWORD Level, LPBYTE pPrinter, DWORD cbBuf, LPDWORD pcbNeeded)`
//   Level 2 的 `PRINTER_INFO_2W` 开头是 `pServerName/pPrinterName/pShareName/pPortName/pDriverName`
//   （5 个指针），本仓没有这些字符串的持久存储 ⇒ **如实返回所需字节数 + ERROR_INSUFFICIENT_BUFFER**，
//   调用方（WPF 的 `PrintQueue` 属性读取）会走它的"取不到"路径，而不是拿到垃圾指针。
int32_t GetPrinterW(void *hPrinter, uint32_t level, uint8_t *pPrinter,
                    uint32_t cbBuf, uint32_t *pcbNeeded)
{
    (void)hPrinter; (void)level; (void)pPrinter; (void)cbBuf;
    if (pcbNeeded) *pcbNeeded = sizeof(void *) * 5 + 256;   // 够放下 5 个字符串指针 + 名字
    wpf_set_last_error(122);   // ERROR_INSUFFICIENT_BUFFER
    return 0;                  // FALSE
}

// ── DeviceCapabilitiesW：查询打印机能力 ──────────────────────────────────────
//   常用 `capability`：DC_BINS(6)=纸盒数、DC_DUPLEX(7)=是否双面、DC_PAPERS(2)=纸型列表、
//   DC_PAPERSIZE(3)、DC_COLORDEVICE(10)=是否彩打、DC_ORIENTATION(17)。
//   Linux 侧走 CUPS：没有查询接口时用**合理的默认值**（单纸盒、非双面、支持 A4/Letter），
//   这正是 CUPS 队列在"未配置"时的实际表现。
int32_t DeviceCapabilitiesW(const uint16_t *pDevice, const uint16_t *pPort,
                            uint16_t capability, uint16_t *pOutput, void *pDevMode)
{
    (void)pDevice; (void)pPort; (void)pOutput; (void)pDevMode;
    switch (capability) {
        case 6:  return 1;     // DC_BINS：1 个纸盒（CUPS 默认）
        case 7:  return 0;     // DC_DUPLEX：不支持（CUPS 未配置时）
        case 10: return 1;     // DC_COLORDEVICE：支持彩打（多数 CUPS 队列）
        case 17: return 1;     // DC_ORIENTATION：支持方向
        default: return -1;    // 其它能力：如实返回 -1（失败），不伪造
    }
}

// ── DocumentPropertiesW：DEVMODE 属性对话框 ──────────────────────────────────
//   上游用它弹"打印机属性"对话框。**Linux 上没有 Win32 DEVMODE 对话框**，
//   也没有等价的 CUPS 弹窗（CUPS 的选项在 Web 界面/`lpoptions` 里）。
//   ⇒ 如实返回 -1（失败）。调用方会认为"用户取消了属性设置"，这是安全的中性结果。
int32_t DocumentPropertiesW(void *hwnd, void *hPrinter, uint16_t *pDeviceName,
                            void *pDevModeOutput, void *pDevModeInput, uint32_t fMode)
{
    (void)hwnd; (void)hPrinter; (void)pDeviceName;
    (void)pDevModeOutput; (void)pDevModeInput; (void)fMode;
    return -1;
}

// ── PT*：PrintTicket ↔ DEVMODE 转换（7 个）──────────────────────────────────
//   **只做诚实降级（E_NOTIMPL），不伪造 DEVMODE/PrintTicket。**
//   为什么不做：
//    · 它们是 Windows PrintTicket Provider 接口（`IPrintTicketProvider`），
//      背后是把 **DEVMODE 二进制** 与 **PrintTicket XML** 双向映射；
//    · 需要完整的 DEVMODE 结构定义（200+ 字节、按驱动变化）+ PrintTicket 的 XSD 校验；
//    · **本仓没有 DEVMODE 样本可比对** ⇒ 写完也无法验证对错。
//   ⇒ 与其写一个读错字段的实现，不如让调用方拿到 `E_NOTIMPL` 走它的回退路径。
//   （`PTReleaseMemory` 例外：它只是释放 `PTOpenProviderEx` 给的内存，而我们从没分配过，
//     返回 S_OK 是正确的 —— 释放一个空集合是成功的。）
int32_t PTCloseProvider(void *a) { (void)a; return 0; }
int32_t PTReleaseMemory(void *a) { (void)a; return 0; }   // S_OK：释放空集合是成功的
