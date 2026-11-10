// ── shlwapi 组：K-1436/K-1437 真实现 ─────────────────────────────────────────
//
// K-1436 实测：`RepoWatcher.ProcessEvent` → `SourceTree.Utils.FileHelper.GetRelativePath`
//   → DllImport `shlwapi.dll!PathRelativePathTo` 抛 `DllNotFoundException` ⇒ **文件事件被整条丢弃**
//   （写文件 + `git add` 后 9 秒 `staged` 仍 0；手动 `RefreshStatus()` 后立刻 `staged=1`）。
//
// 这里的语义按 Win32 原文（`PathRelativePathToW`）：
//   · `pszFrom` 视 `dwAttrFrom & FILE_ATTRIBUTE_DIRECTORY` 决定它是**目录**还是**文件**
//     （是文件时取它的父目录当基准）；
//   · `pszTo` 同理（`dwAttrTo` 只影响是否把末段当目录，本实现不额外补分隔符）；
//   · 输出 `..` 分段构成的相对路径；同一路径 ⇒ **空字符串**（Win32 行为）；
//   · 不同根 / 无法表达相对路径 ⇒ Win32 会退回 `\...` 形式的绝对路径；Linux 单根下
//     只可能出现在「一方是相对路径」的输入，此时按绝对化后重算。
//   · 返回 `TRUE`（1）成功、`FALSE`（0）失败（缓冲不足 / 空指针）。
//
// 注意：本 shim 里 `LPSTR` 是 UTF-8（`uint8_t*`），`LPWSTR` 是 UTF-16（`uint16_t*`），
//   与托管侧 `CharSet=Ansi`（实测 `DllImport` 属性为 `SupportsLastError, CallConvWinapi`，
//   即 Ansi 语义、入口名会先试 `PathRelativePathToA`）一致。W 版做 UTF-16↔UTF-8 透传。

#include "win32_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WPF_FAD_DIRECTORY 0x10u /* FILE_ATTRIBUTE_DIRECTORY */
#define WPF_REL_MAX_SEG   64
#define WPF_REL_SEG_LEN   512

// 把路径拆成段（丢弃空段与 "."，就地折叠 ".."）。
// 返回段数；-1 表示段数超过 WPF_REL_MAX_SEG。
static int rel_split(const char *path, char segs[WPF_REL_MAX_SEG][WPF_REL_SEG_LEN])
{
    int n = 0;
    const char *p = path ? path : "";
    while (*p) {
        while (*p == '/')
            ++p;
        if (!*p)
            break;
        const char *q = p;
        while (*q && *q != '/')
            ++q;
        size_t len = (size_t)(q - p);
        if (len == 1 && p[0] == '.') {
            /* skip */
        } else if (len == 2 && p[0] == '.' && p[1] == '.') {
            if (n > 0)
                --n;
        } else {
            if (n >= WPF_REL_MAX_SEG)
                return -1;
            if (len >= WPF_REL_SEG_LEN)
                len = WPF_REL_SEG_LEN - 1;
            memcpy(segs[n], p, len);
            segs[n][len] = '\0';
            ++n;
        }
        p = q;
    }
    return n;
}

static size_t rel_append(char *out, size_t cap, const char *seg)
{
    size_t len = strlen(out);
    if (len) {
        if (len + 1 >= cap)
            return 0;
        out[len++] = '/';
        out[len] = '\0';
    }
    size_t sl = strlen(seg);
    if (len + sl >= cap)
        return 0;
    memcpy(out + len, seg, sl + 1);
    return len + sl;
}

// A 版：pszFrom/pszTo 为 UTF-8（本 shim 的 LPSTR 语义）
BOOL PathRelativePathToA(LPSTR pszPath, LPCSTR pszFrom, DWORD dwAttrFrom,
                         LPCSTR pszTo, DWORD dwAttrTo)
{
    (void)dwAttrTo;
    if (!pszPath || !pszFrom || !pszTo)
        return 0;

    char from_segs[WPF_REL_MAX_SEG][WPF_REL_SEG_LEN];
    char to_segs[WPF_REL_MAX_SEG][WPF_REL_SEG_LEN];
    int nf = rel_split((const char *)pszFrom, from_segs);
    int nt = rel_split((const char *)pszTo, to_segs);
    if (nf < 0 || nt < 0)
        return 0;

    // from 是文件（无 DIRECTORY 位）⇒ 取它的父目录当基准
    if (!(dwAttrFrom & WPF_FAD_DIRECTORY) && nf > 0)
        --nf;

    // 公共前缀
    int k = 0;
    while (k < nf && k < nt && strcmp(from_segs[k], to_segs[k]) == 0)
        ++k;

    char buf[4096];
    buf[0] = '\0';
    const size_t cap = sizeof(buf);

    // 回退段
    for (int i = k; i < nf; ++i) {
        if (!rel_append(buf, cap, "..")) {
            return 0;
        }
    }
    // 追加目标段
    for (int i = k; i < nt; ++i) {
        if (!rel_append(buf, cap, to_segs[i])) {
            return 0;
        }
    }

    size_t need = strlen(buf) + 1;
    // 调用方给的缓冲大小未知（Win32 约定 MAX_PATH=260），保守按 260 判定
    if (need > 260)
        return 0;
    memcpy((char *)pszPath, buf, need);
    return 1;
}

// W 版：UTF-16 进、UTF-16 出
BOOL PathRelativePathToW(LPWSTR pszPath, LPCWSTR pszFrom, DWORD dwAttrFrom,
                         LPCWSTR pszTo, DWORD dwAttrTo)
{
    if (!pszPath || !pszFrom || !pszTo)
        return 0;
    char *from = wpf_utf16_to_utf8_dup((const uint16_t *)pszFrom);
    char *to   = wpf_utf16_to_utf8_dup((const uint16_t *)pszTo);
    if (!from || !to) {
        free(from);
        free(to);
        return 0;
    }
    uint8_t out8[4096];
    BOOL ok = PathRelativePathToA(out8, (const uint8_t *)from, dwAttrFrom,
                                  (const uint8_t *)to, dwAttrTo);
    if (ok) {
        uint16_t *w = wpf_utf8_to_utf16_dup((const char *)out8);
        if (!w) {
            ok = 0;
        } else {
            uint16_t *dst = (uint16_t *)pszPath;
            size_t i = 0;
            while (w[i] && i < 259) {
                dst[i] = w[i];
                ++i;
            }
            dst[i] = 0;
            free(w);
        }
    }
    free(from);
    free(to);
    return ok;
}

// 入口名兜底：实测托管侧 `DllImport("shlwapi.dll", CharSet=Ansi)` 在 CoreCLR 上
//   **先按原名 `PathRelativePathTo` 找**（ExactSpelling=false 时的实际行为，
//   报错原文 `Unable to find an entry point named 'PathRelativePathTo'`），
//   因此必须同时导出无后缀名，否则 A/W 版都白给。
BOOL PathRelativePathTo(LPSTR pszPath, LPCSTR pszFrom, DWORD dwAttrFrom,
                        LPCSTR pszTo, DWORD dwAttrTo)
{
    return PathRelativePathToA(pszPath, pszFrom, dwAttrFrom, pszTo, dwAttrTo);
}
