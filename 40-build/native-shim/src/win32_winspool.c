// 【ACC-024 / K-052】`winspool.drv` 的最小实现 —— 让 WPF 的打印链能走完。
//
// 背景：`System.Printing` 的 `LocalPrintServer` / `PrintQueue.CreateXpsDocumentWriter` 底层会走
// `winspool.drv`（`OpenPrinter` / `StartDocPrinter` / `WritePrinter` / `EndDocPrinter` /
// `EnumPrinters` / `GetDefaultPrinter`）。本仓此前**一个都没有**（`exports.txt` 里 winspool 0 条）。
//
// 口径（AGENTS.md 约束 5「诚实降级」）：
//   · **不假装有真实打印机**：`EnumPrinters` 返回一个**固定的假队列名**（"Fake CUPS 队列"，
//     与样本 SCR-05 的下拉框文案一致）；
//   · **`WritePrinter` 接受数据并返回成功**，但不真的送 CUPS —— 判据只要求 `job=`/`pages=` 读数变化，
//     不要求落成真 PDF。数据**不丢**：写到 `$LDP_PRINT_SPOOL`（默认 `/tmp/ldp-print.spool`）
//     以便日后接 CUPS 时能验证内容。
//   · 所有"成功"都带 `[WINSPOOL]` 诊断（`WPF_LINUX_KEY_DIAG=1` 时打印），便于区分"走了桩"还是"真打了"。
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "win32_internal.h"

static int g_fake_printer = 1;
static int g_job_id = 1;

static void spool_diag(const char *msg, const char *name)
{
    if (!getenv("WPF_LINUX_KEY_DIAG")) return;
    fprintf(stderr, "[WINSPOOL] %s %s\n", msg, name ? name : "");
    fflush(stderr);
}

// ── 打印机句柄：返回固定假句柄 ────────────────────────────────────────────────
int OpenPrinterW(uint16_t *name, void **phPrinter, void *defaults)
{
    (void)defaults;
    spool_diag("OpenPrinter", name ? "(有名字)" : "(null)");
    if (phPrinter) *phPrinter = (void *)&g_fake_printer;
    return 1;   // TRUE
}
int OpenPrinterA(const char *name, void **phPrinter, void *defaults) { return OpenPrinterW((uint16_t *)name, phPrinter, defaults); }
int OpenPrinter(const char *name, void **phPrinter, void *defaults)  { return OpenPrinterA(name, phPrinter, defaults); }

int ClosePrinter(void *hPrinter) { (void)hPrinter; return 1; }

// ── 文档/页 ─────────────────────────────────────────────────────────────────
// DOC_INFO_1W { LPWSTR pDocName; LPWSTR pOutputFile; LPWSTR pDatatype; }
int StartDocPrinterW(void *hPrinter, int level, void *pDocInfo)
{
    (void)hPrinter; (void)level; (void)pDocInfo;
    spool_diag("StartDocPrinter -> job", NULL);
    return g_job_id;
}
int StartDocPrinterA(void *hPrinter, int level, void *pDocInfo) { return StartDocPrinterW(hPrinter, level, pDocInfo); }
int StartDocPrinter(void *hPrinter, int level, void *pDocInfo)  { return StartDocPrinterW(hPrinter, level, pDocInfo); }

int StartPagePrinter(void *hPrinter) { (void)hPrinter; return 1; }
int EndPagePrinter(void *hPrinter)   { (void)hPrinter; return 1; }
int EndDocPrinter(void *hPrinter)
{
    (void)hPrinter;
    spool_diag("EndDocPrinter -> done", NULL);
    g_job_id++;
    return 1;
}

// ── 数据：接受并落到 spool 文件（不送 CUPS）─────────────────────────────────
int WritePrinter(void *hPrinter, const void *data, uint32_t cb, uint32_t *pcbWritten)
{
    (void)hPrinter;
    const char *path = getenv("LDP_PRINT_SPOOL");
    if (!path || !path[0]) path = "/tmp/ldp-print.spool";
    FILE *f = fopen(path, "ab");
    if (f) { if (data && cb) fwrite(data, 1, cb, f); fclose(f); }
    if (pcbWritten) *pcbWritten = cb;
    spool_diag("WritePrinter bytes", NULL);
    if (getenv("WPF_LINUX_KEY_DIAG")) { fprintf(stderr, "[WINSPOOL]   %u 字节 -> %s\n", cb, path); fflush(stderr); }
    return 1;
}

// ── 枚举 / 默认打印机：返回一个固定的假队列（与 SCR-05 的下拉框一致）──────────
// ENUM_PRINTERS 的 level 4（PRINTER_INFO_4W { LPWSTR pPrinterName; LPWSTR pServerName; DWORD Attributes; }）
int EnumPrintersW(uint32_t flags, uint16_t *name, uint32_t level, uint8_t *buf,
                  uint32_t cbBuf, uint32_t *pcbNeeded, uint32_t *pcReturned)
{
    (void)flags; (void)name;
    // 我们只提供 level 4 的"名字列表"，其它 level 报 0 条（调用方通常会退回 level 4）
    const char *printerName = "Fake CUPS Queue";
    uint32_t need = 0;
    if (level == 4) {
        uint32_t strBytes = (uint32_t)(strlen(printerName) + 1) * 2;   // UTF-16
        need = sizeof(void *) + sizeof(void *) + 4 + strBytes;
    }
    if (pcbNeeded) *pcbNeeded = need;
    if (!buf || cbBuf < need || level != 4) {
        if (pcReturned) *pcReturned = 0;
        // 【K-054】**缓冲区不足时必须设 last error = ERROR_INSUFFICIENT_BUFFER(122)**，
        // 否则 System.Printing 会把"需要更大缓冲"误判成"没有打印机" ⇒
        // LocalPrintServer.DefaultPrintQueue 为 null ⇒ 调用方 NullReferenceException。
        if (need > 0 && (!buf || cbBuf < need)) wpf_set_last_error(122);
        return 0;
    }
    // 铺一段 UTF-16 名字
    uint8_t *p = buf;
    uint16_t *namePtr = (uint16_t *)(p + sizeof(void *) * 2 + 4);
    for (size_t i = 0; i <= strlen(printerName); i++) namePtr[i] = (uint16_t)(unsigned char)printerName[i];
    *(void **)(p) = namePtr;                       // pPrinterName
    *(void **)(p + sizeof(void *)) = 0;            // pServerName
    *(uint32_t *)(p + sizeof(void *) * 2) = 0;     // Attributes
    if (pcReturned) *pcReturned = 1;
    spool_diag("EnumPrinters -> Fake CUPS Queue", NULL);
    return 1;
}
int EnumPrintersA(uint32_t flags, const char *name, uint32_t level, uint8_t *buf,
                  uint32_t cbBuf, uint32_t *pcbNeeded, uint32_t *pcReturned)
{
    return EnumPrintersW(flags, (uint16_t *)name, level, buf, cbBuf, pcbNeeded, pcReturned);
}
int EnumPrinters(uint32_t flags, const char *name, uint32_t level, uint8_t *buf,
                 uint32_t cbBuf, uint32_t *pcbNeeded, uint32_t *pcReturned)
{
    return EnumPrintersW(flags, (uint16_t *)name, level, buf, cbBuf, pcbNeeded, pcReturned);
}

int GetDefaultPrinterW(uint16_t *buf, uint32_t *pcchBuffer)
{
    const char *n = "Fake CUPS Queue";
    uint32_t need = (uint32_t)(strlen(n) + 1);
    if (pcchBuffer) *pcchBuffer = need;
    if (!buf) return 0;
    for (uint32_t i = 0; i <= (uint32_t)strlen(n); i++) buf[i] = (uint16_t)(unsigned char)n[i];
    spool_diag("GetDefaultPrinter -> Fake CUPS Queue", NULL);
    return 1;
}
int GetDefaultPrinterA(char *buf, uint32_t *pcchBuffer) { return GetDefaultPrinterW((uint16_t *)buf, pcchBuffer); }
int GetDefaultPrinter(char *buf, uint32_t *pcchBuffer)  { return GetDefaultPrinterA(buf, pcchBuffer); }
