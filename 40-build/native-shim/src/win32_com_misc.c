// ── 【清算 / K-200】A 类：COM 单元 + 进程信息 —— 真实现 ────────────────────────
#include "win32_internal.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <X11/Xlib.h>
#include <X11/Xatom.h>

// ── CoIncrementMTAUsage / CoDecrementMTAUsage ───────────────────────────────
//   语义：把当前进程"钉"在 MTA（多线程套间）里，并返回一个 cookie 供 `CoDecrementMTAUsage` 归还。
//   Linux 上没有 COM 套间，但**它是一个引用计数** —— 真实现就是维护这个计数，
//   当计数 > 0 时表示"MTA 已存在"。`CoGetObjectContext` 在没有对象上下文时返回
//   `CO_E_NOTINITIALIZED`，这正是我们该给调用方的诚实答复。
static int g_mta_refs = 0;

int32_t CoIncrementMTAUsage(void **pCookie)
{
    if (!pCookie) return (int32_t)0x80070057;
    ++g_mta_refs;
    *pCookie = (void *)(intptr_t)g_mta_refs;   // cookie 就是当次计数值
    return 0;   // S_OK
}

int32_t CoDecrementMTAUsage(void *cookie)
{
    if (cookie && g_mta_refs > 0) --g_mta_refs;
    return 0;   // S_OK
}

//   `CoGetObjectContext(REFIID, void** ppv)`：取当前线程所在套间的对象上下文。
//   本 shim 没有 COM 套间 ⇒ **如实返回 CO_E_NOTINITIALIZED(0x800401F0)**，
//   而不是伪造一个对象（伪造会让上游拿它去 QueryInterface 然后崩）。
int32_t CoGetObjectContext(const void *riid, void **ppv)
{
    (void)riid;
    if (ppv) *ppv = NULL;
    return (int32_t)0x800401F0;   // CO_E_NOTINITIALIZED
}

// ── NtQueryInformationProcess ───────────────────────────────────────────────
//   `NTSTATUS NtQueryInformationProcess(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG)`
//   Linux 上进程信息在 `/proc/self/`。常用类别：
//     0  = ProcessBasicInformation（PBI：ExitStatus/PebBaseAddress/AffinityMask/BasePriority/…）
//     7  = ProcessDebugPort（有调试器则非 0）
//     26 = ProcessCommandLineInformation
//   ⇒ 只把能给出**真实值**的类别做对（7、0 的少数字段），其余返回
//     `STATUS_INVALID_INFO_CLASS(0xC0000003)` —— 这是调用方能理解并处理的错误，
//     比"填一堆假数据"安全。
int32_t NtQueryInformationProcess(void *hProcess, uint32_t infoClass,
                                  void *buf, uint32_t bufLen, uint32_t *retLen)
{
    (void)hProcess;
    if (retLen) *retLen = 0;
    switch (infoClass) {
        case 7: {   /* ProcessDebugPort */
            if (!buf || bufLen < sizeof(void *)) return (int32_t)0xC0000004;  // STATUS_INFO_LENGTH_MISMATCH
            int traced = 0;
            FILE *f = fopen("/proc/self/status", "r");
            if (f) {
                char line[256];
                while (fgets(line, sizeof line, f))
                    if (!strncmp(line, "TracerPid:", 10)) { traced = atoi(line + 10) != 0; break; }
                fclose(f);
            }
            *(void **)buf = traced ? (void *)(intptr_t)1 : NULL;
            if (retLen) *retLen = sizeof(void *);
            return 0;
        }
        case 0: {   /* ProcessBasicInformation（6 个 ULONG_PTR + 1 个 ULONG_PTR ExitStatus）*/
            if (!buf || bufLen < 6 * sizeof(void *)) return (int32_t)0xC0000004;
            memset(buf, 0, 6 * sizeof(void *));
            ((void **)buf)[1] = NULL;              // PebBaseAddress（Linux 无 PEB）
            ((void **)buf)[2] = (void *)(intptr_t)-1; // AffinityMask（全核可用）
            if (retLen) *retLen = 6 * sizeof(void *);
            return 0;
        }
        default:
            return (int32_t)0xC0000003;   // STATUS_INVALID_INFO_CLASS
    }
}

// ── GetProcessHandleFromHwnd ────────────────────────────────────────────────
//   `oleacc.dll` 的：由窗口句柄拿到拥有它的进程句柄（供无障碍客户端读对方进程）。
//   Linux 上窗口的 PID 就在 `_NET_WM_PID` 属性里（EWMH 标准）⇒ **真实现**。
//   返回的"句柄"用 `(HANDLE)(intptr_t)pid` 表示（本 shim 的进程句柄约定）。
//   实现直接用 `XGetWindowProperty` 读 `_NET_WM_PID`（EWMH 标准属性）—— 不依赖别的模块。
int32_t wpf_x11_query_pid(void *hwnd)
{
    if (!g_wpf.dpy || !hwnd) return -1;
    Atom a = XInternAtom(g_wpf.dpy, "_NET_WM_PID", True);
    if (a == None) return -1;
    Atom actual = None;
    int fmt = 0;
    unsigned long n = 0, left = 0;
    unsigned char *data = NULL;
    if (XGetWindowProperty(g_wpf.dpy, (Window)(uintptr_t)hwnd, a, 0, 1, False,
                           XA_CARDINAL, &actual, &fmt, &n, &left, &data) != Success) return -1;
    int32_t pid = -1;
    if (data && fmt == 32 && n >= 1) pid = (int32_t)((unsigned long *)data)[0];
    if (data) XFree(data);
    return pid;
}

void *GetProcessHandleFromHwnd(void *hwnd)
{
    int32_t pid = wpf_x11_query_pid(hwnd);
    if (pid <= 0) return NULL;
    return (void *)(intptr_t)pid;
}
