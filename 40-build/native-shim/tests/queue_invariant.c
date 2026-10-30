// WPF-on-Linux · MSGFLOW 离线用例：**队列不变量**（"WM_CHAR 会不会被静默吞掉"）
// ============================================================================
// 目的（主控 2026-09-13 派）：MSGFLOW 实跑显示 `push WM_CHAR` 打了、`pop … WM_CHAR` **0 行**，
//   而全 shim 只有 `wpf_queue_pop` 会 free 队列节点 ⇒ 要么 (a) 某次带 filter/区间的 pop 把
//   `WM_CHAR` **跳过**（它留在队里），要么 (b) `t->head/t->tail` 被改坏、节点**被摘掉但没走日志点**。
//   本用例把这两条**离线证死**（不需要 WPF 应用、不需要注入）：
//     · (a) 带区间的 pop 跳过 WM_CHAR 之后，**它必须仍在队里**（后续无 filter 的 pop 能取到）；
//     · (b) 任意 push/pop/filtered-pop/Peek(PM_NOREMOVE) 序列之后，**条数守恒**且**元素集合守恒**；
//     · 顺带把 `PeekMessageW(PM_NOREMOVE)` 的两条风险量出来：**回插到队首会改序**（可达时），
//       以及**条数不变**（即"没有丢件"；丢件只在 `malloc` 失败时发生，见报告 §2.1）。
//
// 怎么跑（**主控构建之后**；本文件不参与构建，只是源码）：
//   cd src/WpfGfx.Linux.Native
//   gcc -std=gnu11 -O1 -Isrc src/Linux/tests/queue_invariant.c -o /tmp/queue_invariant -Lbin -lwpfwin32
//       -Wl,-rpath,"$PWD/bin" -lX11 -ldl -lpthread
//   /tmp/queue_invariant            # 期望末行 `QUEUE_INVARIANT=PASS`
//   （不想动 .so 时，把 -Lbin -lwpfwin32 换成 obj/*.o 也可以）
// 【注意】本用例刻意**不连 X**：`g_wpf.dpy == NULL` ⇒ `PeekMessageW` 的内部泵分支自动跳过。
#include "win32_internal.h"
#include <stdio.h>
#include <string.h>

// Win32 API 原型不在 win32_internal.h（那里只放内部助手）⇒ 本用例自带声明
BOOL PeekMessageW(WPF_MSG *msg, HWND hwnd, UINT lo, UINT hi, UINT remove);

#define WM_CHAR_ 0x0102
#define WM_KEYDOWN_ 0x0100
#define WM_KEYUP_ 0x0101
#define PM_REMOVE_ 0x0001
#define PM_NOREMOVE_ 0x0000

static int s_fail = 0;
static void check(const char *what, int ok, const char *detail)
{
    printf("%s%s%s%s\n", ok ? "  PASS  " : "  FAIL  ", what,
           detail[0] ? " :: " : "", detail);
    if (!ok) s_fail++;
}

static void mk(WPF_MSG *m, UINT msg, WPARAM wp)
{
    memset(m, 0, sizeof(*m));
    m->message = msg;
    m->hwnd = (HWND)(uintptr_t)0x200005;   // 固定"窗口"，便于构造 hwndFilter 场景
    m->wParam = wp;
}

int main(void)
{
    static wpf_thread t;                    // 零初始化即可：本用例只碰 head/tail
    t.wake_read = -1; t.wake_write = -1;   // 不写 self-pipe（本用例不需要唤醒）

    WPF_MSG a, b, c, out;

    // ── 场景 (a)：带区间的 pop **跳过** WM_CHAR 之后，它必须还在队里 ──────────────
    mk(&a, WM_KEYDOWN_, 0x41); mk(&b, WM_CHAR_, 0x41); mk(&c, WM_KEYUP_, 0x41);
    wpf_queue_push(&t, &a); wpf_queue_push(&t, &b); wpf_queue_push(&t, &c);
    check("(a) push 3 条后 count==3", wpf_queue_count(&t) == 3, "");

    int got = wpf_queue_pop(&t, &out, (HWND)0, 0, 0);
    check("(a) 无 filter 的 pop 取到队首 KEYDOWN", got && out.message == WM_KEYDOWN_, "");

    got = wpf_queue_pop(&t, &out, (HWND)0, WM_KEYDOWN_, WM_KEYUP_);   // 区间**不含** 0x0102
    check("(a) 带区间(0x0100..0x0101) 的 pop 跳过 WM_CHAR、取到 KEYUP",
          got && out.message == WM_KEYUP_, "");
    check("(a) 跳过后 WM_CHAR 仍在队里（count==1）", wpf_queue_count(&t) == 1, "");

    got = wpf_queue_pop(&t, &out, (HWND)0, 0, 0);
    check("(a) 后续无 filter 的 pop 取到的正是 WM_CHAR（**没有丢件**）",
          got && out.message == WM_CHAR_ && (uint32_t)out.wParam == 0x41, "");

    // ── 场景 (b)：长序列下条数/集合守恒（head/tail 不变量）──────────────────────
    int pushed = 0, popped = 0, popped_in_loop_char_count = 0;
    for (int i = 0; i < 500; i++) {
        WPF_MSG k, ch, u;
        mk(&k, WM_KEYDOWN_, 0x41 + (i % 26)); mk(&ch, WM_CHAR_, 0x61 + (i % 26)); mk(&u, WM_KEYUP_, 0x41);
        wpf_queue_push(&t, &k); wpf_queue_push(&t, &ch); wpf_queue_push(&t, &u);
        pushed += 3;
        // 故意用"不含 WM_CHAR"的区间取：WM_CHAR 必须被留下（而不是消失）
        if (wpf_queue_pop(&t, &out, (HWND)0, WM_KEYDOWN_, WM_KEYUP_) && out.message != WM_CHAR_) popped++;
        if (wpf_queue_pop(&t, &out, (HWND)0, 0, 0)) { popped++; if (out.message == WM_CHAR_) popped_in_loop_char_count++; }
    }
    int left = wpf_queue_count(&t);
    check("(b) 长序列后 count == pushed - popped（无静默丢件）", left == pushed - popped, "");

    // 【为什么要按**类别**守恒】循环里被 pop 掉的是"最老的那条"（可能是上一轮的 KEYUP），
    //   类别是交错的 ⇒ 手算"剩下 500 条 WM_CHAR"是**期望写错**（会造假红）。正确的不变量是：
    //   **每一类**的 (push 数 − drain 前 pop 数) == 排空后该类计数。
    // ⚠️ 注意：drain 过程中**不要**再累加 `popped`（上一版就是在这里重复计数 ⇒ 假红）。
    int popped_before_drain = popped;
    int char_left = 0, other_left = 0;
    while (wpf_queue_pop(&t, &out, (HWND)0, 0, 0)) {
        if (out.message == WM_CHAR_) char_left++; else other_left++;
    }
    check("(b) 排空后 count == 0", wpf_queue_count(&t) == 0, "");
    check("(b) 排空后按类别守恒：char_left+other_left == pushed - popped(排空前)（一条都没少）",
          char_left + other_left == pushed - popped_before_drain, "");
    check("(b) 排空后 WM_CHAR 条数 == 500 - 循环里被取走的 CH 数",
          char_left == 500 - popped_in_loop_char_count, "");

    // ── 场景 (c)：**最小复现** —— 带 filter 的 pop 摘掉**队尾**（队列非空）⇒ `tail=NULL` 而 `head!=NULL`
    //   ⇒ 下一次 `push` 走 `else t->head = n;` 这一支，**把原有队列整条孤儿化**（消息静默消失，
    //     而且**没有经过任何 pop** ⇒ 出队侧日志自然一行都不打）。
    //   读码依据：`wpf_queue_push`（`if (t->tail) … else t->head = n;`）只认 `tail`；
    //           `wpf_queue_pop`（`if (t->tail == v) t->tail = NULL;`）摘队尾后**没有把 tail 指回新的队尾**。
    //   ⇒ 本用例断言"正确行为"（A 与 C 都还能取到）；**当前实现在这里应当红**。
    mk(&a, WM_KEYDOWN_, 0x41); mk(&b, WM_KEYUP_, 0x41); mk(&c, WM_KEYDOWN_, 0x42);
    wpf_queue_push(&t, &a);                     // A 先入队
    wpf_queue_push(&t, &b);                     // B 成为队尾（tail=B）
    int got_c = wpf_queue_pop(&t, &out, (HWND)0, WM_KEYUP_, WM_KEYUP_);   // 只取 KEYUP=B（**队尾**）
    check("(c) 带 filter 摘掉队尾：取到 B 且 count 应为 1（A 仍在）",
          got_c && out.message == WM_KEYUP_ && wpf_queue_count(&t) == 1, "");
    wpf_queue_push(&t, &c);                     // ⚠ 这一步在坏状态下会覆盖 head、孤儿化 A
    check("(c) 之后再 push 一条：count 应为 2（**不能**丢掉 A）", wpf_queue_count(&t) == 2, "");
    int sawA = 0, sawC = 0, drained = 0;
    while (wpf_queue_pop(&t, &out, (HWND)0, 0, 0)) {
        drained++;
        if (out.message == WM_KEYDOWN_ && (uint32_t)out.wParam == 0x41) sawA = 1;
        if (out.message == WM_KEYDOWN_ && (uint32_t)out.wParam == 0x42) sawC = 1;
    }
    check("(c) 排空能同时取到 A 与 C（**A 没有被孤儿化**）", sawA && sawC && drained == 2, "");

    // ── `PeekMessageW(PM_NOREMOVE)`：条数守恒 + **非队首命中会改序** ───────────────
    // ⚠️ `PeekMessageW` 操作的是**本线程的 TLS 队列**（`wpf_thread_self()`），不是我上面那个本地 `t`
    //   ⇒ 这一段必须用 TLS 队列，否则 peek 看不到任何东西（**上一版就是因此假红**：两个 Peek 断言
    //   在修前修后都失败，原因根本不是"改序"。这也是"用例自己会撒谎"的一例，写下来防复发）。
    wpf_thread *tls = wpf_thread_self();
    check("(PM_NOREMOVE) 拿到本线程 TLS 队列", tls != NULL, "");
    mk(&a, WM_KEYDOWN_, 0x41); mk(&b, WM_CHAR_, 0x41); mk(&c, WM_KEYUP_, 0x41);
    wpf_queue_push(tls, &a); wpf_queue_push(tls, &b); wpf_queue_push(tls, &c);
    WPF_MSG pm;
    int r = PeekMessageW(&pm, (HWND)0, WM_KEYUP_, WM_KEYUP_, PM_NOREMOVE_);   // 命中**非队首**
    check("Peek(PM_NOREMOVE, 命中非队首) 返回 KEYUP 且 count 不变（3）",
          r && pm.message == WM_KEYUP_ && wpf_queue_count(tls) == 3, "");
    wpf_queue_pop(tls, &out, (HWND)0, 0, 0);
    // 【v3 · F-C 之后】PM_NOREMOVE 改成**只读扫描** ⇒ **不再改序**：peek 之后队首仍是原来的 KEYDOWN。
    //   （修前这一条是**红**的：旧实现"pop 出来再插回队首"会把刚 peek 的 KEYUP 提前到队首。）
    check("(PM_NOREMOVE) 只读扫描**不改序**：peek 之后队首仍是 KEYDOWN",
          out.message == WM_KEYDOWN_, "");
    // 清理并确认 peek 没吃掉任何东西
    int n = 0; while (wpf_queue_pop(tls, &out, (HWND)0, 0, 0)) n++;
    check("peek 之后仍能排空剩下 2 条（无丢件）", n == 2, "");

    printf(s_fail == 0 ? "QUEUE_INVARIANT=PASS\n" : "QUEUE_INVARIANT=FAIL(%d)\n", s_fail);
    return s_fail == 0 ? 0 : 1;
}
