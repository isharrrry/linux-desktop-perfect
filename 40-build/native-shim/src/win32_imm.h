// 【ACC-017 / D-014 第三层】IMM32 的最小承载面。
//   为什么需要它：WPF 的 IME 路径全程走 IMM32 —— `ImmGetContext(hwnd)` 拿 HIMC，
//   然后在 `WM_IME_COMPOSITION` 里用 `ImmGetCompositionString(himc, GCS_COMPSTR/GCS_RESULTSTR, …)`
//   读预编辑/提交文本（见上游 `PresentationFramework/System/Windows/Documents/ImmComposition.cs`）。
//   而 Linux 上没有 IMM32，只有 XIM。这里的做法是：
//     XIM 的预编辑/提交回调 → 更新本模块里的 `wpf_himc` → 投递 `WM_IME_*` → WPF 侧按 IMM32 语义读出来。
//   也就是说：**HIMC 就是那块"XIM 写、IMM32 读"的共享缓冲**。
#ifndef WPF_WIN32_IMM_H
#define WPF_WIN32_IMM_H

#include <stdint.h>

// 有没有 IME 服务器（由 win32_x11.c 的 XOpenIM 结果决定；没有时 ImmGetContext 返回 NULL）
void wpf_imm_set_available(int avail);
int  wpf_imm_is_available(void);

// 焦点窗口（投递 WM_IME_* 的目标）
void wpf_imm_bind_hwnd(void *hwnd);

// XIM 回调 → 这里（text 是 UTF-16，chars 是**字符数**，不是字节数）
void wpf_imm_preedit_start(void);
void wpf_imm_preedit_update(const uint16_t *text, int chars, int cursor);
void wpf_imm_preedit_done(void);
void wpf_imm_commit(const uint16_t *text, int chars);

// 诊断读数（判据用）
typedef struct {
    int preedit_starts;
    int preedit_updates;
    int preedit_dones;
    int commits;
    int cancellations;
    int candidate_window_calls;
    int composition_msgs;
} wpf_imm_stats;

void wpf_imm_get_stats(wpf_imm_stats *out);

#endif
