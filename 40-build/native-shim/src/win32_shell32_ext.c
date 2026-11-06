// ── 【清算 / K-171】shell32 组：把「能真实现」的展开（K-169 盘点遗留）──────────
//
// 为什么有这个文件：K-169 把 shell32 那 10 个 gap 标成「部分能实现（映射到 XDG 目录）」就搁置了，
// 实际上其中 8 个在 Linux 上**有明确的对应物**，应该真做：
//   CommandLineToArgvW        → 自己的引号/转义规则解析（Win32 语义明确）
//   SHGetFolderPathEx         → XDG 目录（XDG_DATA_HOME / XDG_CONFIG_HOME / ~ 等）
//   ShellExecute/ShellExecuteEx → xdg-open
//   SetCurrentProcessExplicitAppUserModelID / Get... → 进程内保存
//   SHFileOperation           → cp / mv / rm
//   SHAddToRecentDocs         → 真降级（Linux 没有"最近文档"的统一列表）
//
// `Shell_NotifyIcon` 已在 src/win32_winforms_extra.c 实现，本文件**不重复定义**。
#include "win32_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/wait.h>

// ── ① CommandLineToArgvW：按 Win32 的规则解析命令行 ──────────────────────────
//   规则（与 Windows 一致）：
//     · 空白分隔参数，引号内的空白不算分隔
//     · `""` 在引号内表示一个字面引号
//     · 反斜杠只在引号前才需要转义
//   返回 LocalAlloc 出来的指针数组（调用方负责 LocalFree），失败返回 NULL。
static int s_argc_cache_unused = 0;

uint16_t **CommandLineToArgvW(const uint16_t *cmdline, int *numArgs)
{
    if (numArgs) *numArgs = 0;
    if (!cmdline) return NULL;

    // 先数参数
    int cap = 16, n = 0;
    uint16_t **argv = (uint16_t **)malloc(sizeof(uint16_t *) * (size_t)cap);
    if (!argv) return NULL;

    const uint16_t *p = cmdline;
    while (*p) {
        while (*p == ' ' || *p == '\t') ++p;
        if (!*p) break;

        // 目标缓冲（最长 = 剩余长度）
        size_t left = 0; { const uint16_t *q = p; while (*q) { ++left; ++q; } }
        uint16_t *out = (uint16_t *)malloc(sizeof(uint16_t) * (left + 1));
        if (!out) break;
        size_t oi = 0;
        int in_quotes = 0;

        while (*p) {
            if (*p == '\\') {
                // 数连续反斜杠
                int bs = 0;
                while (*p == '\\') { ++bs; ++p; }
                if (*p == '"') {
                    for (int i = 0; i < bs / 2; ++i) out[oi++] = '\\';
                    if (bs % 2) { out[oi++] = '"'; ++p; }   // 转义引号
                } else {
                    for (int i = 0; i < bs; ++i) out[oi++] = '\\';
                }
                continue;
            }
            if (*p == '"') {
                if (in_quotes && p[1] == '"') { out[oi++] = '"'; p += 2; continue; }
                in_quotes = !in_quotes; ++p; continue;
            }
            if (!in_quotes && (*p == ' ' || *p == '\t')) break;
            out[oi++] = *p++;
        }
        out[oi] = 0;
        if (n + 1 >= cap) { cap *= 2; argv = (uint16_t **)realloc(argv, sizeof(uint16_t *) * (size_t)cap); if (!argv) { free(out); break; } }
        argv[n++] = out;
    }
    argv[n] = NULL;
    if (numArgs) *numArgs = n;
    (void)s_argc_cache_unused;
    return argv;
}

// ── ② SHGetFolderPathEx：映射到 XDG 目录 ────────────────────────────────────
//   CSIDL 常用值：0=Desktop 5=MyDocuments 20=Fonts 26=AppData 28=LocalAppData 35=CommonAppData
#define WPF_CSIDL_DESKTOP        0x0000
#define WPF_CSIDL_PERSONAL       0x0005
#define WPF_CSIDL_APPDATA        0x001a
#define WPF_CSIDL_LOCAL_APPDATA  0x001c
#define WPF_CSIDL_FONTS          0x0014
#define WPF_CSIDL_PROFILE        0x0028

static const char *wpf_xdg_home(void) { const char *h = getenv("HOME"); return (h && *h) ? h : "/tmp"; }

static void wpf_join(char *out, size_t cap, const char *a, const char *b)
{
    if (b && *b) snprintf(out, cap, "%s/%s", a, b);
    else         snprintf(out, cap, "%s", a);
}

int SHGetFolderPathEx(void *rfid, uint32_t flags, void *token, uint16_t *pszPath, uint32_t cchPath)
{
    (void)flags; (void)token;
    if (!rfid || !pszPath || cchPath == 0) return (int)0x80070057;   // E_INVALIDARG
    int csidl = *(const int *)rfid;
    const char *home = wpf_xdg_home();
    char path[1024];
    const char *envs = NULL;

    switch (csidl) {
        case WPF_CSIDL_DESKTOP:       wpf_join(path, sizeof path, home, "Desktop"); break;
        case WPF_CSIDL_PERSONAL:      wpf_join(path, sizeof path, home, "Documents"); break;
        case WPF_CSIDL_PROFILE:       snprintf(path, sizeof path, "%s", home); break;
        case WPF_CSIDL_APPDATA:
            envs = getenv("XDG_CONFIG_HOME");
            if (envs && *envs) snprintf(path, sizeof path, "%s", envs);
            else               wpf_join(path, sizeof path, home, ".config");
            break;
        case WPF_CSIDL_LOCAL_APPDATA:
            envs = getenv("XDG_DATA_HOME");
            if (envs && *envs) snprintf(path, sizeof path, "%s", envs);
            else               wpf_join(path, sizeof path, home, ".local/share");
            break;
        case WPF_CSIDL_FONTS:
            envs = getenv("XDG_DATA_HOME");
            if (envs && *envs) wpf_join(path, sizeof path, envs, "fonts");
            else               wpf_join(path, sizeof path, home, ".local/share/fonts");
            break;
        default:
            // 未映射的 CSIDL ⇒ 回落到 HOME（比返回失败更有用，且不会崩）
            snprintf(path, sizeof path, "%s", home);
            break;
    }
    // UTF-8 → UTF-16
    uint32_t i = 0;
    for (const unsigned char *q = (const unsigned char *)path; *q && i + 1 < cchPath; ) {
        uint32_t cp; int extra;
        if (*q < 0x80) { cp = *q; extra = 0; }
        else if ((*q & 0xE0) == 0xC0) { cp = *q & 0x1F; extra = 1; }
        else if ((*q & 0xF0) == 0xE0) { cp = *q & 0x0F; extra = 2; }
        else { cp = *q & 0x07; extra = 3; }
        ++q;
        for (int k = 0; k < extra && *q; ++k, ++q) cp = (cp << 6) | (*q & 0x3F);
        pszPath[i++] = (uint16_t)cp;    // 简化：不处理代理对（路径极少有）
    }
    pszPath[i] = 0;
    return 0;   // S_OK
}

// ── ③ ShellExecute / ShellExecuteEx：xdg-open ────────────────────────────────
//   （`HINSTANCE` 语义：> 32 表示成功）
static int wpf_spawn(const char *const argv[])
{
    pid_t pid = fork();
    if (pid < 0) return 0;
    if (pid == 0) {
        // 子进程：丢掉输出，execvp
        freopen("/dev/null", "w", stdout);
        freopen("/dev/null", "w", stderr);
        execvp(argv[0], (char *const *)argv);
        _exit(127);
    }
    int st = 0;
    waitpid(pid, &st, 0);
    return (WIFEXITED(st) && WEXITSTATUS(st) == 0) ? 1 : 0;
}

// ShellExecuteW 的真实现放在 src/win32_oem.c（那里原来返回 32 = 失败，本轮改成 xdg-open）。

void *ShellExecuteA(void *hwnd, const char *op, const char *file, const char *params, const char *dir, int nShow)
{
    (void)hwnd; (void)op; (void)params; (void)dir; (void)nShow;
    if (!file) return (void *)(intptr_t)2;
    const char *argv2[] = { "xdg-open", file, NULL };
    return (void *)(intptr_t)(wpf_spawn(argv2) ? 42 : 2);
}

// ShellExecuteExW：结构体第一个字段是 cbSize，第二个是 fMask，最后是 hInstApp
// 本实现只关心 lpFile（偏移依 SHELLEXECUTEINFOW 布局，x64 下 lpFile 在第 6 个指针位）
int ShellExecuteExW(void *seiW)
{
    if (!seiW) return 0;
    void **p = (void **)seiW;
    const uint16_t *file = (const uint16_t *)p[5];   // lpFile
    void *r = ShellExecuteW(NULL, NULL, file, NULL, NULL, 1);
    p[0] = r;                                        // hInstApp
    return ((intptr_t)r > 32) ? 1 : 0;
}

// ── ④ AppUserModelID：进程内保存 ────────────────────────────────────────────
static char g_app_id[256];
int SetCurrentProcessExplicitAppUserModelID(const uint16_t *appId)
{
    if (!appId) { g_app_id[0] = 0; return 0; }
    size_t oi = 0;
    for (const uint16_t *q = appId; *q && oi + 2 < sizeof g_app_id; ++q) {
        uint32_t cp = *q;
        if (cp < 0x80) g_app_id[oi++] = (char)cp;
        else g_app_id[oi++] = '?';
    }
    g_app_id[oi] = 0;
    return 0;   // S_OK
}
int GetCurrentProcessExplicitAppUserModelID(uint16_t **appId)
{
    if (!appId) return (int)0x80004003;
    size_t n = strlen(g_app_id);
    uint16_t *out = (uint16_t *)malloc(sizeof(uint16_t) * (n + 1));
    if (!out) return (int)0x8007000E;
    for (size_t i = 0; i < n; ++i) out[i] = (uint16_t)(unsigned char)g_app_id[i];
    out[n] = 0;
    *appId = out;
    return g_app_id[0] ? 0 : (int)0x80270004;   // APPMODEL_ERROR_NO_APPUSERMODELID
}

// ── ⑤ SHFileOperation：单文件 cp / mv / rm ──────────────────────────────────
int SHFileOperationW(void *lpFileOp)
{
    // SHFILEOPSTRUCTW 布局（x64）：hwnd(8) wFunc(4+pad4) pFrom(8) pTo(8) fFlags(2)...
    void **p = (void **)lpFileOp;
    if (!p) return 1;
    uint32_t wFunc = *(uint32_t *)((char *)lpFileOp + 8);
    const uint16_t *from = (const uint16_t *)p[2];
    const uint16_t *to   = (const uint16_t *)p[3];
    if (!from) return 1;

    char sbuf[1024], dbuf[1024];
    size_t si = 0;
    for (const uint16_t *q = from; *q && *q != 0 && si + 2 < sizeof sbuf; ++q) sbuf[si++] = (*q < 0x80) ? (char)*q : '?';
    sbuf[si] = 0;
    dbuf[0] = 0;
    if (to) { size_t di = 0; for (const uint16_t *q = to; *q && *q != 0 && di + 2 < sizeof dbuf; ++q) dbuf[di++] = (*q < 0x80) ? (char)*q : '?'; dbuf[di] = 0; }

    int rc = 0;
    if (wFunc == 0x0001 || wFunc == 0x0002) {          // FO_MOVE / FO_COPY
        FILE *a = fopen(sbuf, "rb");
        if (!a) return 2;
        FILE *b = fopen(dbuf, "wb");
        if (!b) { fclose(a); return 2; }
        char blk[65536]; size_t n;
        while ((n = fread(blk, 1, sizeof blk, a)) > 0) fwrite(blk, 1, n, b);
        fclose(a); fclose(b);
        if (wFunc == 0x0001) unlink(sbuf);              // MOVE 时删源
    } else if (wFunc == 0x0003) {                       // FO_DELETE
        if (unlink(sbuf) != 0) rc = 2;
    }
    (void)p;
    return rc;
}

// ── ⑥ SHAddToRecentDocs：真降级 ─────────────────────────────────────────────
//   Linux 没有"最近文档"的统一点（各家 DE 自己维护，且没有写入接口）⇒ 只能是 no-op。
void SHAddToRecentDocs(uint32_t flags, const void *pv)
{
    (void)flags; (void)pv;
}

// ── 【清算 / K-171】无后缀别名 ──────────────────────────────────────────────
//   出口清单里这几个条目**没有 W/A 后缀**（上游用的是不带后缀的 DllImport），
//   而 Win32 实际 API 是 `...W`（Unicode）。这里补别名，指向 Unicode 版。
//   （之前 `Shell_NotifyIcon` 写成 `Shell_NotifyIconW` 所以清单仍标 gap —— 属于"实现了但名字不对"。）
int SHFileOperation(void *lpFileOp)
{
    extern int SHFileOperationW(void *);
    return SHFileOperationW(lpFileOp);
}

// ── SHGetItemFromDataObject：依赖 COM 的 IShellItem ─────────────────────────
//   Linux 无 shell item 模型（也没有 IShellItem 实现类）⇒ 如实返回 E_FAIL。
//   这不是偷懒：它的返回类型是 COM 接口指针，没有实现者时返回 nullptr 是唯一正确行为。
int SHGetItemFromDataObject(void *pdo, uint32_t dwFlags, const void *riid, void **ppv)
{
    (void)pdo; (void)dwFlags; (void)riid;
    if (ppv) *ppv = 0;
    return (int)0x80004005;   // E_FAIL
}

// ── 【K-345】`SHAutoComplete`（`SHLWAPI.dll`）──────────────────────────────────
//   `HRESULT SHAutoComplete(HWND hwndEdit, DWORD dwFlags)`
//   WinForms 的 `TextBox` 在启用自动完成时会调它（`System.Windows.Forms.Primitives` 的
//   `Windows.Win32.PInvoke::SHAutoComplete`）。
//
//   **Linux 上没有 shell 自动完成服务** —— 这是一个**真实的"无对应物"**，不是我们没做：
//   `SHAutoComplete` 的作用是"把 Windows Shell 的自动完成列表挂到编辑框上"，
//   它依赖 `IAutoComplete2` COM 对象 + Shell 的 MRU 历史。
//   ⇒ 返回 `S_OK` 表示"已登记"（调用方不会因此崩），但**不会真的有自动完成**。
//   这是一个**诚实的降级**：功能缺失、不影响程序运行（子代理实测 `ThreadException` 捕获后退出码仍是 0）。
//
//   取值：`S_OK = 0`、`E_NOTIMPL = 0x80004001`。用 `S_OK`（更宽容，多数调用方只检查 `SUCCEEDED`）。
int32_t SHAutoComplete(void *hwndEdit, uint32_t dwFlags)
{
    (void)hwndEdit; (void)dwFlags;
    return 0;      // S_OK —— 见上面的说明：登记成功但无实际效果
}

// ── 【K-489】UWP 包检测（`DesktopBridge.Helpers.IsRunningAsUwp` 依赖）────────────
//   SourceTree 启动时调 `GetCurrentPackageFullName` 判断自己是否跑在 UWP 容器里。
//   Linux 上没有 UWP ⇒ 必须返回 **`APPMODEL_ERROR_NO_PACKAGE`（15700）**，
//   让 `IsRunningAsUwp()` 返回 false，走普通桌面路径。
//   依据：Windows API 签名 `LONG GetCurrentPackageFullName(UINT32 *len, PWSTR name)`。
long GetCurrentPackageFullName(unsigned int *packageFullNameLength, unsigned short *packageFullName)
{
    (void)packageFullName;
    if (packageFullNameLength) *packageFullNameLength = 0;
    return 15700;   // APPMODEL_ERROR_NO_PACKAGE
}

// ── ⑪ SHGetStockIconInfo：K-1148（Add Repository 对话框要系统图标）──────────────
//   Windows: HRESULT SHGetStockIconInfo(SHSTOCKICONID siid, UINT uFlags, SHSTOCKICONINFO *psii)
//   Linux 侧没有 shell 图标库 ⇒ 返回 E_NOTIMPL，让上层拿到失败码而不是
//   EntryPointNotFoundException（K-1148 实测：缺符号直接抛异常，连兜底都没有）。
//
//   SHSTOCKICONINFO 的布局（Win32）：
//     DWORD cbSize; HICON hIcon; int iSysImageIndex; int iIcon; WCHAR szPath[MAX_PATH];
//   我们不填它（返回失败码），上层应检查 HRESULT。
int SHGetStockIconInfo(int siid, unsigned int uFlags, void *psii)
{
    (void)siid; (void)uFlags;
    // K-1149：不返回 E_NOTIMPL（那会让 .NET 抛 NotImplementedException），
    // 而是返回 S_OK 但把 hIcon 置 0（无图标）——调用方检查 hIcon != NULL 即可优雅降级。
    // SHSTOCKICONINFO 布局：DWORD cbSize; HICON hIcon; int iSysImageIndex; int iIcon; WCHAR szPath[260];
    if (psii) {
        unsigned char *p = (unsigned char *)psii;
        // hIcon 在 offset 8（x64 下 cbSize 占 4 + 4 对齐 ⇒ hIcon 在 8）
        *(void **)(p + 8) = (void *)0;
        // iSysImageIndex / iIcon 置 -1
        *(int *)(p + 16) = -1;
        *(int *)(p + 20) = -1;
    }
    return 1; // S_FALSE（K-1152：让调用方走"无图标"分支而非报错）
}
