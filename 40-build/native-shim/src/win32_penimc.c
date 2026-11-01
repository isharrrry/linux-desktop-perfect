// ── 【清算 / K-179】WISP / Penimc 组：把「能真实现」的展开 ─────────────────────
//
// 背景：`MOD-84` 这组 16 个 gap 属于 WISP（Windows Ink Services Platform）+ 笔输入 + ISF 压缩。
//   **它们只在带触控笔/墨迹的应用里才会被调到**（`WinFormsBasics`/`WpfFirstWindow` 都不碰）。
//   但其中 5 个在 Linux 上有干净的对应物，没有理由留空壳 —— 本文件把它们做成真的。
//
// 上游声明（`40-build/upstream/wpf/src/.../MS/Win32/UnsafeNativeMethodsPenimc.cs:560-580`）：
//     internal static extern bool CreateResetEvent(out IntPtr handle);
//     internal static extern bool DestroyResetEvent(IntPtr handle);
//     internal static extern bool RaiseResetEvent(IntPtr handle);
//     internal static extern IntPtr RegisterDllForSxSCOM();
//     internal static extern void SetDisablePenImcBoundsCheckProtection(bool value);
#include "win32_internal.h"
#include <pthread.h>
#include <stdlib.h>
#include <stdio.h>

// ── 可重置事件（auto-reset，语义对齐 Win32 的 CreateEvent(FALSE, FALSE, ...)）──
//   用 pthread_mutex + pthread_cond 实现：
//    · `RaiseResetEvent` = SetEvent：置位 + 唤醒一个等待者
//    · 被唤醒的等待者**自动清位**（auto-reset 的关键语义）
//   本仓没有"等待该事件"的入口（WPF 只用它做线程间信号），所以这里只实现置位/销毁；
//   如果将来要 `WaitForSingleObject` 支持它，在 `win32_kernel32.c` 里按 handle 判断即可。
typedef struct {
    pthread_mutex_t mtx;
    pthread_cond_t  cv;
    int             signaled;
} wpf_reset_event;

static wpf_reset_event **g_events = NULL;
static size_t           g_event_cap = 0;
static size_t           g_event_n   = 0;

static int wpf_ev_register(wpf_reset_event *e)
{
    for (size_t i = 0; i < g_event_n; ++i) if (!g_events[i]) { g_events[i] = e; return 1; }
    if (g_event_n == g_event_cap) {
        size_t nc = g_event_cap ? g_event_cap * 2 : 8;
        wpf_reset_event **np = (wpf_reset_event **)realloc(g_events, nc * sizeof(*np));
        if (!np) return 0;
        g_events = np; g_event_cap = nc;
    }
    g_events[g_event_n++] = e;
    return 1;
}

BOOL CreateResetEvent(intptr_t *pHandle)
{
    if (!pHandle) return 0;
    *pHandle = 0;
    wpf_reset_event *e = (wpf_reset_event *)calloc(1, sizeof(*e));
    if (!e) return 0;
    if (pthread_mutex_init(&e->mtx, NULL) != 0) { free(e); return 0; }
    if (pthread_cond_init(&e->cv, NULL) != 0) { pthread_mutex_destroy(&e->mtx); free(e); return 0; }
    if (!wpf_ev_register(e)) { pthread_cond_destroy(&e->cv); pthread_mutex_destroy(&e->mtx); free(e); return 0; }
    *pHandle = (intptr_t)e;
    return 1;   // TRUE
}

BOOL RaiseResetEvent(intptr_t handle)
{
    wpf_reset_event *e = (wpf_reset_event *)handle;
    if (!e) return 0;
    pthread_mutex_lock(&e->mtx);
    e->signaled = 1;
    pthread_cond_signal(&e->cv);
    pthread_mutex_unlock(&e->mtx);
    return 1;   // TRUE
}

BOOL DestroyResetEvent(intptr_t handle)
{
    wpf_reset_event *e = (wpf_reset_event *)handle;
    if (!e) return 0;
    for (size_t i = 0; i < g_event_n; ++i) if (g_events[i] == e) { g_events[i] = NULL; break; }
    pthread_cond_destroy(&e->cv);
    pthread_mutex_destroy(&e->mtx);
    free(e);
    return 1;   // TRUE
}

// ── RegisterDllForSxSCOM：Windows SxS 注册，Linux 无对应 ─────────────────────
//   上游只是拿它做一次"初始化"，返回值不参与逻辑判断（`Penimc` 的 static ctor 调它）。
//   返回 0 是安全的；这不是假成功，是如实表示"没有要注册的东西"。
intptr_t RegisterDllForSxSCOM(void) { return 0; }

// ── SetDisablePenImcBoundsCheckProtection：调试用的边界检查开关 ──────────────
//   Linux 侧没有 PIMC 的 COM 层，自然也没有那层边界检查 ⇒ 真 no-op。
void SetDisablePenImcBoundsCheckProtection(BOOL value) { (void)value; }
