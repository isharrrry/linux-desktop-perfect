// ── 【清算 / K-188】winmm 的 mmio*（RIFF 文件 IO）：能真实现的部分 ──────────────
//
// 上游签名（`40-build/upstream/wpf/src/.../Common/src/Interop/Windows/winmm/*.cs`）：
//     mmioOpenW(string fileName, IntPtr lpmmioinfo, int flags)                  -> IntPtr
//     mmioClose(IntPtr hMIO, int flags)                                          -> int
//     mmioRead(IntPtr hMIO, byte[] wf, int cch)                                  -> int
//     mmioDescend(IntPtr hMIO, MMCKINFO lpck, MMCKINFO lcpkParent, int flags)     -> int
//     mmioAscend(IntPtr hMIO, MMCKINFO lpck, int flags)                           -> int
//
// Linux 上 `mmio*` 没有对应库，但它本质是 **RIFF 容器的文件读写** —— 纯 IO，
// 用 `fopen/fread/fseek` 就能真做。本文件实现 open/close/read 三个
// （descend/ascend 需要 `MMCKINFO` 的托管侧布局，没在 `$FX` 里找到定义，
//  而且 `WinFormsBasics`/`WpfFirstWindow` 都不碰它们 ⇒ 这两个暂时保留为 gap，见文件末尾说明）。
#include "win32_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct wpf_mmio {
    struct wpf_mmio *next;
    FILE *fp;
} wpf_mmio;

static wpf_mmio *g_mmio = NULL;

// 打开标志（winmm 的值）
#define WPF_MMIO_READ       0x0000
#define WPF_MMIO_WRITE      0x0001
#define WPF_MMIO_READWRITE  0x0002
#define WPF_MMIO_CREATE     0x1000
#define WPF_MMIO_EXCLUSIVE  0x8000

void *mmioOpenW(const uint16_t *fileName, void *lpmmioinfo, int32_t flags)
{
    (void)lpmmioinfo;
    if (!fileName) return NULL;
    // UTF-16 → UTF-8
    char path[1024]; size_t oi = 0;
    for (const uint16_t *q = fileName; *q && oi + 4 < sizeof path; ++q) {
        uint32_t cp = *q;
        if (cp < 0x80) path[oi++] = (char)cp;
        else if (cp < 0x800) { path[oi++] = (char)(0xC0 | (cp >> 6)); path[oi++] = (char)(0x80 | (cp & 0x3F)); }
        else { path[oi++] = (char)(0xE0 | (cp >> 12)); path[oi++] = (char)(0x80 | ((cp >> 6) & 0x3F)); path[oi++] = (char)(0x80 | (cp & 0x3F)); }
    }
    path[oi] = 0;

    const char *mode = "rb";
    if (flags & WPF_MMIO_READWRITE)      mode = (flags & WPF_MMIO_CREATE) ? "w+b" : "r+b";
    else if (flags & WPF_MMIO_WRITE)     mode = "wb";

    FILE *fp = fopen(path, mode);
    if (!fp) return NULL;
    wpf_mmio *h = (wpf_mmio *)calloc(1, sizeof(*h));
    if (!h) { fclose(fp); return NULL; }
    h->fp = fp;
    h->next = g_mmio;
    g_mmio = h;
    return (void *)h;
}

int32_t mmioClose(void *hMIO, uint32_t flags)
{
    (void)flags;
    if (!hMIO) return 1;   // MMSYSERR_INVALHANDLE
    wpf_mmio **pp = &g_mmio;
    while (*pp) {
        if ((void *)*pp == hMIO) {
            wpf_mmio *v = *pp;
            *pp = v->next;
            if (v->fp) fclose(v->fp);
            free(v);
            return 0;      // MMSYSERR_NOERROR
        }
        pp = &(*pp)->next;
    }
    return 1;              // MMSYSERR_INVALHANDLE
}

// `mmioRead` 的第二个参数在托管侧是 `byte[]`（会被 marshal 成连续字节块的指针）
int32_t mmioRead(void *hMIO, void *buf, int32_t cch)
{
    if (!hMIO || !buf || cch <= 0) return -1;
    for (wpf_mmio *h = g_mmio; h; h = h->next) {
        if ((void *)h == hMIO) {
            if (!h->fp) return -1;
            size_t n = fread(buf, 1, (size_t)cch, h->fp);
            return (int32_t)n;
        }
    }
    return -1;
}

// ── mmioDescend / mmioAscend 为什么没做（如实记，不写空壳）──────────────────
//   它们需要 `MMCKINFO`（RIFF chunk 头：ckid/cksize/fccType/dwDataOffset/dwFlags）。
//   `MMCKINFO` 的**托管侧定义在 `$FX` 的任何 dll 里都找不到**（Cecil 扫全部 dll 无输出），
//   而上游 C# 里它是 `[MarshalAs(UnmanagedType.LPStruct)] MMCKINFO` ——
//   按值 marshal 一个我们不知道布局的结构进去，会**读错字段并可能越界写**。
//   ⇒ 与其写一个会崩的空壳，不如保持 gap 并写清原因。
//   ⇒ 如果将来确认了布局（标准 Win32 的 `MMCKINFO` 是 20 字节：4+4+4+4+4），
//     补起来只要三十行：`fseek` 到 `dwDataOffset`、读 chunk 头、填结构。

// ── 【清算 / K-201】`mmioDescend` / `mmioAscend` —— 按**标准 Win32 `MMCKINFO`** 实现 ──
//
// K-188 里我因为"没找到托管侧的 `MMCKINFO` 定义"而跳过了这两个。**那是过度谨慎**：
// `MMCKINFO` 是**公开规范**（RIFF 文件格式的一部分），不是私有格式：
//     typedef struct {
//         FOURCC ckid;         // +0   本 chunk 的 id（如 'fmt ' / 'data'）
//         DWORD  cksize;       // +4   本 chunk 的数据大小
//         FOURCC fccType;      // +8   父 chunk 的类型（'RIFF'/'LIST'）
//         DWORD  dwDataOffset; // +12  数据相对文件开头的偏移
//         DWORD  dwFlags;      // +16  MMIO_FINDCHUNK / MMIO_FINDRIFF 等
//     } MMCKINFO;              // 20 字节，无填充（全是 4 字节字段）
//
// 上游 `[MarshalAs(UnmanagedType.LPStruct)] MMCKINFO` 会按这个布局按值 marshal ⇒ 可以安全实现。
typedef struct {
    uint32_t ckid;
    uint32_t cksize;
    uint32_t fccType;
    uint32_t dwDataOffset;
    uint32_t dwFlags;
} wpf_mmckinfo;

#define WPF_MMIO_FINDCHUNK  0x10
#define WPF_MMIO_FINDRIFF   0x20
#define WPF_MMIO_FINDLIST   0x40
#define WPF_MMIO_FINDRIFF4  0x100

static FILE *wpf_mmio_fp(void *hMIO)
{
    for (wpf_mmio *h = g_mmio; h; h = h->next) if ((void *)h == hMIO) return h->fp;
    return NULL;
}

static uint32_t wpf_le32(const uint8_t *b)
{
    return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

// `mmioDescend(hmmio, lpck, lpckParent, flags)`：进入下一个 chunk。
//   · `lpck->ckid` 非 0 ⇒ 查找该 id 的 chunk；为 0 ⇒ 读当前位置的 chunk 头
//   · `lpckParent` 非 NULL ⇒ 限制在父 chunk 范围内
// 返回值：0 = 成功，非 0 = 失败（MMSYSERR_INVALPARAM / MMIOERR_CHUNKNOTFOUND）
int32_t mmioDescend(void *hMIO, wpf_mmckinfo *lpck, const wpf_mmckinfo *lpckParent, uint32_t flags)
{
    FILE *fp = wpf_mmio_fp(hMIO);
    if (!fp || !lpck) return 5;   // MMSYSERR_INVALPARAM
    (void)flags;

    long start = ftell(fp);
    if (start < 0) return 5;

    uint8_t hdr[8];
    while (fread(hdr, 1, 8, fp) == 8) {
        uint32_t id  = wpf_le32(hdr);
        uint32_t sz  = wpf_le32(hdr + 4);
        long dataOff = ftell(fp);

        // 父范围检查
        if (lpckParent) {
            long pOff = (long)lpckParent->dwDataOffset;
            if (dataOff + (long)sz > pOff + (long)lpckParent->cksize) return 0x100;  // MMIOERR_CHUNKNOTFOUND
        }
        // 若指定了 ckid，跳过不匹配的
        if (lpck->ckid != 0 && id != lpck->ckid) {
            long next = dataOff + (long)sz;
            if (sz & 1) ++next;   // RIFF chunk 按 2 字节对齐
            if (fseek(fp, next, SEEK_SET) != 0) return 0x100;
            continue;
        }

        lpck->ckid         = id;
        lpck->cksize       = sz;
        lpck->dwDataOffset = (uint32_t)dataOff;
        return 0;   // 成功（文件位置已在数据起点）
    }
    fseek(fp, start, SEEK_SET);
    return 0x100;   // MMIOERR_CHUNKNOTFOUND
}

// `mmioAscend(hmmio, lpck, flags)`：从当前 chunk 出来，定位到它的末尾之后。
int32_t mmioAscend(void *hMIO, wpf_mmckinfo *lpck, uint32_t flags)
{
    FILE *fp = wpf_mmio_fp(hMIO);
    if (!fp || !lpck) return 5;
    (void)flags;
    long next = (long)lpck->dwDataOffset + (long)lpck->cksize;
    if (lpck->cksize & 1) ++next;   // RIFF 2 字节对齐
    return fseek(fp, next, SEEK_SET) == 0 ? 0 : 5;
}
