// WPF-on-Linux · MSGFLOW 离线夹具：**坏链注入**（静默 SEGV 族的确定性形态）＋ **`si_addr` 两点校准**
// ============================================================================
// 【本件是什么 / 不是什么是逐字说清的】
//   · 本件是 `D-G109`（静默 SEGV 族）那条线的**确定性夹具**：把 `t->head = P ∧ t->tail == NULL`
//     这个**非法队列状态**注入到 `wpf_thread` 上，再走一次 `wpf_queue_push()` ⇒ **不需要 WPF 应用、
//     不需要 X、不需要注入、不需要 dotnet**（零槽）。
//   · 本件**不是**判据件、**不是**产出端：它不参与任何门禁步骤，也**不产生**任何在册读数 ——
//     它只把"崩点那一格"变成**可在几秒内重放**的硬读数。率与上界仍由应用级腿（`TASK-0201`）给。
//   · 本件**收编自**车道 `t10`（2026-09-28）在 `/tmp/t10-qcorrupt.c`（sha16 `c4ce076eadf0fa4b`）
//     里真跑过的夹具：**源码逐字相同**（`corrupt` 模式＝原 `main()` 的正文，包括输出标记
//     `T10_SEGV` / `T10_SURVIVED`），**只多**一个 `calibrate` 模式（见下）。⇒ 本件的读数与
//     `t10` 的读数**同一形态**，可逐字对照。
//
// 【为什么要在**同一件里**放两个模式（覆盖面的理由，别拆成两件）】
//   `src/Linux/build/close-wave.sh` 的 `fp_inputs()` 用
//     `find src/WpfGfx.Linux.Native -type f \( -name '*.c' -o -name '*.h' \)`
//   收录**该目录下所有 C 源**（`R-CSRC` 那条：原生 C 源必须进覆盖面）⇒ **每多一件 `.c` 就多一格
//   覆盖面**（件数 +1、`inputs_fp` 位移）。⇒ 两个夹具**合并成一件**，把代际位移压到**最小的一格**。
//
// 【`calibrate` 模式的判据（`si_addr` 到底可不可信）】
//   `D-G109` 里那句"不许把 `siaddr=0x0` 当依据"的**理由**（"该字段与指令语义不符"）**已被证伪**
//   （2026-09-28 车道 `t45` 的 dated 更正，落 `src/Linux/samples/WpfFeatureProbe/KNOWN-DEFECTS.md`）：
//   真值为 **`si_addr = P + 0x38`**，与 shim 里那条指令**逐字同形**：
//     `win32_msg.c` 的 `p = p->next` ⇒ 反汇编 `48 8b 40 38  mov 0x38(%rax),%rax`
//   ⇒ 本模式就用**同一条指令**去载入 `P + 0x38`，由 `SA_SIGINFO` 的 `si_addr` 读出故障地址：
//     · `P = 0`      ⇒ 期望 `si_addr = 0x38`（**非空偏移**：证明该字段不是"恒 0 的坏字段"）
//     · `P = 0x102`  ⇒ 期望 `si_addr = 0x13a`（＝ `corrupt` 模式里那个真崩点的形态）
//   两点**同一条指令、只有 `P` 变** ⇒ 字段**随输入逐位跟随** ⇒ **可采信**。
//   ⚠️ 边界：本模式证的是"**读器 + 字段 + 指令三者对齐**"；它**不**证"应用里那几格 `0x0` 是假读数"
//     —— 那几格的正解是 `#GP`（`SI_KERNEL`）形态，**判别量是 `si_code`，而它当年从未被采**。
//
// 【怎么跑（**不参与构建**，只是源码；构建入口 `Makefile` 只收 `src/` 下的件）】
//   cd src/WpfGfx.Linux.Native
//   gcc -std=gnu11 -O1 -Isrc src/Linux/tests/queue_corrupt_chain_fixture.c -o /tmp/qcfixture \
//       -Lbin -lwpfwin32 -Wl,-rpath,$PWD/bin -ldl -lpthread
//   /tmp/qcfixture corrupt  0x102      # 坏链注入
//   /tmp/qcfixture calibrate 0         # 校准第一点
//   /tmp/qcfixture calibrate 0x102     # 校准第二点
//
// 【期望读数（单变量：**只换 `libwpfwin32.so`**；两臂见 `src/Linux/build/MilBridge/P0-mvp-segv-report.md`）】
//   `corrupt 0x102`：
//     · 修前件 `abf6879c027c5e73` ⇒ `rc=139`、`T10_SEGV si_addr=0x13a`（＝ `0x102 + 0x38`）
//     · 修后件 `e8127a3d7128d417`（波 `#79` 冻结九位之一）⇒ `rc=0` ＋ 修法的具名台账
//       `[QUEUE_CORRUPT] … 可疑链头=0x102 … 对策=隔离可疑链(零解引用)` ＋ `T10_SURVIVED count=1
//       head==tail next=(nil)` ⇒ **不崩 ∧ 不静默丢件**（件照常入队，链被隔离）
//   `calibrate`：`0 ⇒ 0x38`｜`0x102 ⇒ 0x13a`
// ============================================================================
#include <stdio.h>
#include <stdlib.h>
#include <signal.h>
#include <string.h>
#include <stdint.h>
#include <unistd.h>          /* `_exit`（信号处理器里只许用异步信号安全调用） */
#include "win32_internal.h"

void wpf_queue_push(wpf_thread *t, const WPF_MSG *m);
int  wpf_queue_count(wpf_thread *t);

static unsigned long g_mode_cal = 0;   /* 0=corrupt 1=calibrate（只影响打印前缀） */

static void segv(int sig, siginfo_t *si, void *uc) {
    (void)sig; (void)uc;
    if (g_mode_cal) {
        fprintf(stderr, "T10_SEGV(mode=calibrate) si_addr=%p si_code=%d\n",
                (void *)si->si_addr, (int)si->si_code);
    } else {
        fprintf(stderr, "T10_SEGV si_addr=%p\n", (void *)si->si_addr);
    }
    _exit(139);
}

/* 与 shim 的 `p = p->next` **逐字同形**的一条指令：`48 8b 40 38  mov 0x38(%rax),%rax`
   ⇒ 故障地址必为 `P + 0x38`；返回值只为**制造对 `p` 的真实使用**（防被优化掉）。 */
static __attribute__((noinline)) unsigned long load_at_p_plus_38(unsigned long p) {
    __asm__ __volatile__("mov 0x38(%0), %0" : "+r"(p));
    return p;
}

static void install_handler(void) {
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = segv;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGSEGV, &sa, NULL);
}

int main(int argc, char **argv) {
    install_handler();
    const char *mode = (argc > 1) ? argv[1] : "corrupt";

    if (strcmp(mode, "calibrate") == 0) {
        unsigned long p = (argc > 2) ? strtoul(argv[2], NULL, 0) : 0UL;
        g_mode_cal = 1;
        fprintf(stderr, "T10_CAL p=%#lx expect si_addr=%#lx\n", p, p + 0x38UL);
        unsigned long v = load_at_p_plus_38(p);      /* P=0 ⇒ 0x38 ；P=0x102 ⇒ 0x13a */
        fprintf(stderr, "T10_CAL_UNEXPECTED value=%#lx（载入没有故障 ⇒ 该地址已映射，本格 NOINFO）\n", v);
        return 0;
    }

    /* ── corrupt（＝ `t10` 原夹具正文，逐字）──────────────────────────────── */
    static wpf_thread t;            /* 零初始化：只碰 head/tail */
    t.wake_read = -1; t.wake_write = -1;
    WPF_MSG m; memset(&m, 0, sizeof m);
    m.message = 0x0102; m.wParam = 0; m.hwnd = (HWND)(uintptr_t)0x200005;
    unsigned long p = (argc > 2) ? strtoul(argv[2], NULL, 0) : 0x102UL;
    t.head = (wpf_msg_node *)(uintptr_t)p;   /* 消息号当指针 = 现场崩点形态 */
    t.tail = NULL;
    fprintf(stderr, "T10_CORRUPT_INJECT head=%#lx tail=NULL\n", p);
    wpf_queue_push(&t, &m);
    fprintf(stderr, "T10_SURVIVED count=%d head=%p tail=%p next=%p\n",
            wpf_queue_count(&t), (void *)t.head, (void *)t.tail,
            (void *)(t.head ? t.head->next : NULL));
    return 0;
}
