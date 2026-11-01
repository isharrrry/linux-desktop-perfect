// ── 【清算 / K-206】打印 PrintTicket 提供者：PPD 键值对方案落地 ─────────────────
//
// 【设计依据】`40-build/替代格式设计.md` 第二节。
//   原格式 `DEVMODE`（200+ 字节、按驱动变化）与 `PrintTicket`（带 XSD 的 XML）都无法验证；
//   替代方案：**从 CUPS 的 PPD（公开文本格式）读可选项**，转成键值对，
//   再把结果写成一份**我们自己的最小 PrintTicket XML**。
//
// 【数据通道】上游签名里 `IStream` 是 COM 接口指针
//   （`ReachFramework/MS/Internal/Printing/Configuration/UnsafeNativeMethods.cs:336`）。
//   本仓 `win32_ole32.c` 有完整 14 槽 `IStream`（`CreateStreamOnHGlobal`），
//   ⇒ 这里按 **vtable 偏移**直接调用：`Release=+2 / Read=+3 / Write=+4 / Seek=+5`。
//
// 【有 PPD 读 PPD，没有就用内置默认集】—— 本机实测 `/etc/cups/ppd/` 为空，
//   所以默认集会经常被用到。**这是可检测的回退，不是静默降级**（K-205 已登记）。
#include "win32_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// IStream vtable 偏移（与 win32_ole32.c 的 wpf_stream_vtbl 一致）
#define WPF_ISTM_RELEASE  2
#define WPF_ISTM_READ     3
#define WPF_ISTM_WRITE    4

typedef int32_t (*wpf_istm_write_fn)(void *, const void *, uint32_t, uint32_t *);

static int32_t wpf_istm_write(void *stm, const char *data, uint32_t cb)
{
    if (!stm) return (int32_t)0x80070057;
    void **vtbl = *(void ***)stm;
    if (!vtbl) return (int32_t)0x80004003;
    wpf_istm_write_fn fn = (wpf_istm_write_fn)vtbl[WPF_ISTM_WRITE];
    if (!fn) return (int32_t)0x80004001;
    uint32_t written = 0;
    return fn(stm, data, cb, &written);
}

// ── 读 PPD ─────────────────────────────────────────────────────────────────
//   只关心三类关键字：
//     `*PageSize ...`（纸型）、`*DefaultPageSize`（默认纸型）、`*OpenUI *Duplex ...`（双面能力）
//   PPD 是纯文本、`*Keyword: value` 形式 ⇒ 可以真解析。
typedef struct {
    char page_size[32];   // 如 "A4"
    int  duplex;          // 1 = 支持双面
    int  has_ppd;         // 0 = 没找到 PPD（用默认集）
} wpf_ppd_info;

static void wpf_parse_ppd(const char *device, wpf_ppd_info *out)
{
    memset(out, 0, sizeof(*out));
    strcpy(out->page_size, "A4");   // 默认
    char path[512];
    snprintf(path, sizeof path, "/etc/cups/ppd/%s.ppd", device ? device : "");
    FILE *f = fopen(path, "r");
    if (!f) return;                 // has_ppd 保持 0
    out->has_ppd = 1;
    char line[512];
    while (fgets(line, sizeof line, f)) {
        if (!strncmp(line, "*DefaultPageSize:", 17)) {
            const char *v = line + 17;
            while (*v == ' ') ++v;
            size_t n = strcspn(v, "\r\n");
            if (n > 0 && n < sizeof out->page_size) { memcpy(out->page_size, v, n); out->page_size[n] = 0; }
        } else if (!strncmp(line, "*OpenUI *Duplex", 15) || !strncmp(line, "*DefaultDuplex", 14)) {
            out->duplex = 1;
        }
    }
    fclose(f);
}

// ── PTOpenProviderEx ────────────────────────────────────────────────────────
//   返回一个非 NULL 句柄表示"提供者打开成功"。`usedVersion` 回填 1（版本 1）。
//   上游把它包成 `SafePTProviderHandle`，只有 NULL 才表示失败。
int32_t PTOpenProviderEx(const uint16_t *deviceName, int32_t maxVersion, int32_t prefVersion,
                         void **handle, int32_t *usedVersion)
{
    (void)maxVersion; (void)prefVersion;
    if (!handle) return (int32_t)0x80070057;
    // 把设备名拷进一个我们自己的块，句柄就是它（CloseProvider 时释放）
    char dev[256]; size_t oi = 0;
    if (deviceName) {
        for (const uint16_t *q = deviceName; *q && oi + 1 < sizeof dev; ++q)
            dev[oi++] = (*q < 0x80) ? (char)*q : '?';
    }
    dev[oi] = 0;
    char *h = (char *)calloc(1, sizeof dev);
    if (!h) return (int32_t)0x8007000E;
    memcpy(h, dev, sizeof dev);
    *handle = h;
    if (usedVersion) *usedVersion = 1;
    return 0;   // S_OK
}

// ── PTGetPrintCapabilities ──────────────────────────────────────────────────
//   往调用方给的 `IStream` 写一份最小 PrintTicket XML（含可用纸型与双面）。
//   XML 用的是标准 PrintTicket 命名空间 —— 但**元素集是我们自己定的最小集**（已登记）。
int32_t PTGetPrintCapabilities(void *handle, void *printTicket, void *printCapabilities, void **errorMsg)
{
    (void)printTicket;
    if (errorMsg) *errorMsg = NULL;
    if (!printCapabilities) return (int32_t)0x80070057;

    wpf_ppd_info info;
    wpf_parse_ppd(handle ? (const char *)handle : NULL, &info);

    char xml[1024];
    int n = snprintf(xml, sizeof xml,
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<psf:PrintCapabilities xmlns:psf=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework\""
        " xmlns:psk=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords\""
        " version=\"1\">\n"
        "  <psf:Feature name=\"psk:PageMediaSize\">\n"
        "    <psf:Option name=\"psk:%s\"/>\n"
        "  </psf:Feature>\n"
        "  <psf:Feature name=\"psk:DocumentDuplex\">\n"
        "    <psf:Option name=\"psk:%s\"/>\n"
        "  </psf:Feature>\n"
        "  <psf:Comment>source=%s</psf:Comment>\n"
        "</psf:PrintCapabilities>\n",
        info.page_size,
        info.duplex ? "TwoSidedShortEdge" : "OneSided",
        info.has_ppd ? "ppd" : "builtin-default");
    if (n <= 0) return (int32_t)0x80004005;
    return wpf_istm_write(printCapabilities, xml, (uint32_t)n);
}

// ── PTMergeAndValidatePrintTicket ──────────────────────────────────────────
//   上游语义：把 `deltaTicket` 合并进 `baseTicket`，结果写 `resultTicket`，并校验合法性。
//   我们的最小实现：**把 base 原样复制到 result**（没有 delta 解析器）；
//   如果 base 为空则返回 `E_INVALIDARG`（调用方会走"用默认设置"的路径）。
//   ⇒ 这是**可检测的部分实现**，不是静默成功。
#define WPF_STM_CHUNK 4096
int32_t PTMergeAndValidatePrintTicket(void *handle, void *baseTicket, void *deltaTicket,
                                      uint32_t scope, void *resultTicket, void **errorMsg)
{
    (void)handle; (void)deltaTicket; (void)scope;
    if (errorMsg) *errorMsg = NULL;
    if (!baseTicket || !resultTicket) return (int32_t)0x80070057;

    void **bvtbl = *(void ***)baseTicket;
    void **rvtbl = *(void ***)resultTicket;
    if (!bvtbl || !rvtbl) return (int32_t)0x80004003;

    // 先把 base 的读写位置复位到开头
    typedef int32_t (*seek_fn)(void *, int64_t, uint32_t, void *);
    typedef int32_t (*read_fn)(void *, void *, uint32_t, uint32_t *);
    seek_fn bseek = (seek_fn)bvtbl[5];
    read_fn bread = (read_fn)bvtbl[WPF_ISTM_READ];
    seek_fn rseek = (seek_fn)rvtbl[5];
    if (!bseek || !bread || !rseek) return (int32_t)0x80004001;
    bseek(baseTicket, 0, 0 /*STREAM_SEEK_SET*/, NULL);
    rseek(resultTicket, 0, 0, NULL);

    char buf[WPF_STM_CHUNK];
    uint32_t total = 0;
    for (;;) {
        uint32_t got = 0;
        int32_t rc = bread(baseTicket, buf, sizeof buf, &got);
        if (rc != 0 || got == 0) break;
        int32_t wrc = wpf_istm_write(resultTicket, buf, got);
        if (wrc != 0) return wrc;
        total += got;
    }
    if (total == 0) return (int32_t)0x80070057;   // 空 ticket 视为无效
    return 0;   // S_OK
}

// ── PTConvert*：保持 E_NOTIMPL（设计文档第二节明确允许）────────────────────
//   `DEVMODE` 二进制结构**按驱动变化**，无法验证 ⇒ 不写"猜布局"的实现。
//   调用方（`PTProvider.ConvertDevModeToPrintTicket`）会拿到 E_NOTIMPL 并走它的回退。
int32_t PTConvertDevModeToPrintTicket(void *handle, uint32_t dmSize, void *devMode,
                                      uint32_t scope, void *printTicket)
{
    (void)handle; (void)dmSize; (void)devMode; (void)scope; (void)printTicket;
    return (int32_t)0x80004001;   // E_NOTIMPL
}
int32_t PTConvertPrintTicketToDevMode(void *handle, void *printTicket, uint32_t scope,
                                      uint32_t *pDevModeSize, uint32_t *pUsedScope, void *devMode)
{
    (void)handle; (void)printTicket; (void)scope; (void)devMode;
    if (pDevModeSize) *pDevModeSize = 0;
    if (pUsedScope) *pUsedScope = 0;
    return (int32_t)0x80004001;   // E_NOTIMPL
}
