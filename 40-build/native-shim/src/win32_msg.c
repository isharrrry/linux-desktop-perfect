// WPF-on-Linux · M7b · 消息队列 · 消息泵 · 定时器 · 窗口过程派发
//
// ── 为什么消息泵要自己写 ──────────────────────────────────────────────────
//   WPF 的 Dispatcher 把**整个 UI 线程的主循环**建立在四个 user32 调用上：
//     GetMessageW → TranslateMessage → DispatchMessageW（PushFrame 内）
//     PeekMessage / MsgWaitForMultipleObjectsEx / SetTimer / KillTimer（优先级调度）
//   Linux 上没有这些东西，必须由 shim 提供，并且语义要**对到托管侧真的依赖的那几条**：
//     · GetMessageW 返回 0 ⇔ 取到了 WM_QUIT（Dispatcher 靠这个 break 出循环）；
//     · SetTimer(hwnd, id, ms, NULL) 到期必须产生 WM_TIMER，wParam == id
//       （Dispatcher 用 TIMERID_TIMERS / TIMERID_BACKGROUND 两个 id 区分用途）；
//     · PostMessage(hwnd, 私有消息, …) 必须能**唤醒**阻塞在 GetMessageW 的线程，
//       否则 InvokeShutdown 从别的线程发起时会死等。
//
// ── 阻塞怎么实现（这是本文件的核心）──────────────────────────────────────
//   GetMessageW 的等待用 poll() 同时盯三个来源：
//     1. self-pipe（本线程的唤醒 fd）—— PostMessage/PostQuitMessage 写一个字节；
//     2. X 连接的 fd（ConnectionNumber）—— 有输入事件时 X server 会写它；
//     3. 最近一个定时器的剩余时间 —— 作为 poll 的 timeout。
//   poll 返回后先抽干 self-pipe，再 XPending/XNextEvent 把 X 事件翻译入队，
//   然后把到期定时器转成 WM_TIMER。全部走同一条队列，GetMessage 只管出队。
//
// ── 无 X server 时 ────────────────────────────────────────────────────────
//   第 2 路退化为不存在，poll 只盯 self-pipe + 定时器。所以
//   `Dispatcher.CurrentDispatcher` + `DispatcherTimer` + `PushFrame` 在没有
//   DISPLAY 的机器上**依然能跑**——这正是 M7b 烟测要证明的独立性。

#define _GNU_SOURCE
#include "win32_internal.h"

#include <errno.h>
#include <poll.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <unistd.h>

// 【MSGFLOW】前置声明：`wpf_queue_push`/`wpf_queue_pop` 在诊断助手与 `wpf_msg_name` **之前**定义
//   ⇒ 必须前向声明，否则（实测）会 implicit-declaration + "static follows non-static" 一串错误。
static int  wpf_msgflow_on(void);
static void wpf_msgflow(const char *fmt, ...);
static unsigned long wpf_msgflow_tid(void);
static const char *wpf_msg_name(UINT m);
static void wpf_msgflow_snapshot(wpf_thread *t, char *buf, size_t cap);   // v2 仪器③：队列内容快照

// 【W136A · TASK-0209 · F2】队列链腐蚀**具名台账**（**static ⇒ 不新增导出，导出数仍 547**）。
//   不受 `WPF_LINUX_MSGFLOW_TRACE` 开关限制：故障诊断不是 trace —— **静默失败正是 D-G109 的要害**。
//   有界：每进程 ≤32 行。三件「写坏者」证据见函数体。
#define WPF_QNODE_MAGIC  0x51A7u
#define WPF_QCORRUPT_MAX 32
static uint64_t      s_push_seq;          // 进程内单调入队序号
static unsigned long s_last_push_tid;     // 最后一次写队列的 tid
static int           s_qcorrupt_lines;

static void wpf_queue_corrupt_report(wpf_thread *t, wpf_msg_node *suspect, uint32_t msg, const char *why)
{
    if (s_qcorrupt_lines >= WPF_QCORRUPT_MAX) return;
    s_qcorrupt_lines++;
    uintptr_t v = (uintptr_t)suspect;
    // ③ **不解引用**的形态判据：x86-64 上 malloc 指针必然 ≥ 0x10000 且 16 字节对齐
    //    ⇒ `0x102`（消息号 WMP_CHAR 当了指针）当场现形。这一条是本波现场崩点的指纹。
    int looks_like_node = (v >= 0x10000u) && ((v & 0xFu) == 0);
    fprintf(stderr,
            "[QUEUE_CORRUPT] 站点=wpf_queue_push 原因=%s 线程队列=%p 可疑链头=%p 形态判据=%s "
            "触发消息=0x%04x tid=%lu 上次写队列: seq=%llu tid=%lu 对策=隔离可疑链(零解引用)+本条消息照常入队\n",
            why, (void *)t, (void *)suspect,
            looks_like_node ? "疑似节点指针(须人工核)" : "**必非节点指针**（低值/未对齐 ⇒ 类型混淆或运行期被写坏）",
            (unsigned)msg, wpf_msgflow_tid(),
            (unsigned long long)s_push_seq, s_last_push_tid);
    fflush(stderr);
}

// ── 队列原语 ───────────────────────────────────────────────────────────────
// [D-K1 · 波 20] 入队时刻的修饰位（翻译层单线程设置 ⇒ 无需原子）
static uint32_t s_push_mods;
// [D-K1 · 波 24 修法乙] "这个戳是**翻译层刚为某个 X 事件**盖的"—— 与"值"分开记：
//   `PostMessageW`/`SetTimer` 等**非翻译层**入队会沿用上一次残留的 `s_push_mods`，
//   修法乙若不加这道闸，一条被 post 的按键消息就会用**陈旧戳**改写实时表（真回归）。
//   语义：**盖戳只对紧随其后的那一次入队有效**（push 时消费掉，见 `wpf_queue_push`）。
static uint8_t  s_push_mods_valid;
void wpf_keystate_set_push_mods(uint32_t mods) { s_push_mods = mods & 0x0F; s_push_mods_valid = 1; }

void wpf_queue_push(wpf_thread *t, const WPF_MSG *m)
{
    if (!t) return;
    wpf_msg_node *n = (wpf_msg_node *)malloc(sizeof(wpf_msg_node));
    if (!n) return;
    n->mods = (uint8_t)(s_push_mods & 0x0F);   // [D-K1] 随消息带走"事件时刻"的修饰位
    // [D-K1 · 波 24 修法乙] 只有**翻译层刚为某个 X 事件**盖的戳才算数，且**只对这一次入队有效**
    //   （`PostMessageW`/`SetTimer` 会沿用残留值 ⇒ 不加这道闸，被 post 的按键消息会用陈旧戳改写实时表）。
    n->mods_valid = s_push_mods_valid;
    s_push_mods_valid = 0;
    n->msg = *m;
    n->next = NULL;
    n->magic = WPF_QNODE_MAGIC;          // 【F2】写入魔数（写坏者覆盖它 ⇒ 校验失配，可归因）

    wpf_lock();
    s_push_seq++;                                  // 【F2】进程内单调：台账可指名"第几次写"
    s_last_push_tid = wpf_msgflow_tid();           // 【F2】谁写的
    n->push_seq = (uint32_t)s_push_seq;            // 【F2】把「谁写的、第几次」钉在这个节点上
    // 【T-A64 · D-G109 残余 · 对称守卫】F1 只把"链不可信"绑在 `tail==NULL` 上，但同一类型
    //   混淆还有**另一半**：`sizeof(wpf_thread)==sizeof(wpf_msg_node)==64` ⇒ 一块已 free 的线程结构
    //   被下一次 `malloc(0x40)` 复用成消息节点后，按 `wpf_thread` 视图读它是
    //     `tail(@16) = msg.wParam` —— `PostMessageW(hwnd,msg,1,0)` 一类 **wp≠0** 的形态
    //   （本仓现取调用点：本文件里 `PostMessageW(…, g_display_devices_msg, 1, 0)` 那处设备变更广播）。
    //   此时 `tail` **非空、却指着一个根本不是节点的值** ⇒ 旧实现 `if (t->tail) t->tail->next = n;`
    //   直接解引用 `tail+0x38` ⇒ **确定性 SEGV，且一行台账都没有**（与 F1 那半**同族的静默死**）。
    //   修法：把 F1 的"合法节点指针"形态判据（**零解引用**：≥0x10000 且 16 字节对齐）
    //   **对称地**用到 tail 上；`tail` 非空而形态非法 ⇒ 整条链不可信 ⇒ 隔离（与 F1 同款），
    //   本条消息照常入队成干净单节点 ⇒ 既不崩，也**不静默丢件**。
    //   （"只对 tail 加空指针守卫"同样被禁止，理由同 F1：那会把"链被写坏"降级成"静默丢消息"。）
    //   ⚠️ 本守卫**只覆盖 `tail`**；"`tail` 假合法而 `head` 坏"这一未观测形态见本件报告的 `NOINFO`。
    uintptr_t tv = (uintptr_t)t->tail;
    int tail_bad = t->tail && !((tv >= 0x10000u) && ((tv & 0xFu) == 0));
    if (tail_bad) {
        wpf_msg_node *quarantine = t->tail;      // 只记指针，**绝不解引用**
        n->next = NULL;
        t->head = n;
        t->tail = n;
        wpf_queue_corrupt_report(t, quarantine, m->message,
                                 "tail 非合法节点指针（链已不可信）");
    } else if (t->tail) {
        t->tail->next = n;
    } else if (t->head) {
        // 【W136A · TASK-0209 · F1 修法】本分支**禁止再走链**。
        //   ── 旧实现（波 #53 现场件）逐字是：
        //        wpf_msg_node *p = t->head;
        //        while (p->next) p = p->next;          ← ⚠️ 崩点
        //   ── 崩点已被**逐指令钉死**（沙箱件与仓内权威件逐字节相同，见 logs/disasm-queue_push.txt）：
        //        wpf_queue_push+259 = 0x14983 = `mov 0x38(%rax),%rax`
        //        0x38 = offsetof(wpf_msg_node, next) = 56；rax = `t->head`
        //   ── 为什么 `tail==NULL && head!=NULL` 会**真的发生**（不是理论）：
        //        sizeof(wpf_thread) == sizeof(wpf_msg_node) == 64 == 0x40
        //        ⇒ 一个**已 free 的线程结构**会被下一次 `malloc(0x40)` **原样复用成消息节点**；
        //          按 wpf_thread 视图读那块内存：head(@8) = msg.message|_pad0 = 0x0102、
        //          tail(@16) = msg.wParam = 0（`PostMessageW(hwnd,msg,0,0)` 的常见形态）
        //        ⇒ 恰好命中本分支 ⇒ 把**消息号 0x102 当节点指针**去读 `->next`
        //        ⇒ 确定性 SEGV，且**一行输出都没有**（应用 0 字节输出 = D-G109 的判词形态）。
        //   ── 修法：**一次 dereference 都不做**。可疑链**隔离登记**（有界、具名、可归因），
        //      本条消息照常入队成一条干净的单节点队列 ⇒ 既不崩，也**不静默丢件**。
        //      （"只加空指针守卫"被明令禁止：那会把"链被写坏"降级成"静默丢消息"，正是本缺口要防的。）
        wpf_msg_node *quarantine = t->head;      // 只记指针，**绝不解引用**
        n->next = NULL;
        t->head = n;
        t->tail = n;
        wpf_queue_corrupt_report(t, quarantine, m->message,
                                 "tail==NULL 而 head!=NULL（链已不可信）");
    } else {
        t->head = n;
    }
    t->tail = n;
    pthread_mutex_unlock(&g_wpf.lock);

    wpf_queue_wake(t);

    // 【MSGFLOW】入队侧：只打 WM_CHAR（KEYDOWN/UP 已有 `[KEY_DIAG]`，打了会刷屏）。
    if (m->message == WM_CHAR && wpf_msgflow_on()) {
        uint32_t cp = (uint32_t)m->wParam;
        char snap[160];
        wpf_msgflow_snapshot(t, snap, sizeof(snap));
        wpf_msgflow("push WM_CHAR 入队 码点=%u(0x%04x '%c') 队列长度=%d 队列内容=[%s 队列=%p tid=%lu",
                    (unsigned)cp, (unsigned)cp,
                    (cp >= 0x20 && cp < 0x7f) ? (char)cp : '?',
                    wpf_queue_count(t), snap, (void *)t, wpf_msgflow_tid());
    }
}

// 唤醒水位：往 self-pipe 写 1 字节。写满（EPIPE/EAGAIN）说明已经有未消费的
// 唤醒字节，等价于「已经醒了」，忽略即可。
void wpf_queue_wake(wpf_thread *t)
{
    if (!t || t->wake_write < 0) return;
    uint8_t b = 1;
    ssize_t r = write(t->wake_write, &b, 1);
    (void)r;
}

static void wpf_queue_drain_wake(wpf_thread *t)
{
    if (!t || t->wake_read < 0) return;
    uint8_t buf[64];
    while (read(t->wake_read, buf, sizeof(buf)) > 0) { }
}

int wpf_queue_count(wpf_thread *t)
{
    if (!t) return 0;
    wpf_lock();
    int n = 0;
    for (wpf_msg_node *p = t->head; p; p = p->next) n++;
    pthread_mutex_unlock(&g_wpf.lock);
    return n;
}

// ── 出队侧消息流 trace（`WPF_LINUX_MSGFLOW_TRACE=1`；**缺省关、只读、有界**）──────────
// 【为什么要有它（2026-09-13，must-do「打字到不了 TextBox」）】
//   已知：翻译层**决定**产出 `WM_CHAR`（`[KEY_DIAG] KEY KeyPress … → WM_KEYDOWN + WM_CHAR`），
//   但 `[msg]`（**dispatch 期** trace）里 `0x0102` 计数 = 0 ⇒ 问题在「入队 → 出队 → 派发」之间。
//   本 trace 把**入队**（只打 WM_CHAR）与**出队**（message/hwnd/剩余/api/线程队列）配成闭环：
//     · push 有、出队有 ⇒ 归**托管侧**（预处理吃掉/未派发）
//     · push 有、出队无 ⇒ 归**队列内部**（filter 不匹配 / `PM_NOREMOVE` 回插丢件 / **跨线程队列**）
//     · push 都没有   ⇒ 归**翻译层**（但 `[KEY_DIAG]` 已能证到"决定产出"这一步）
// 【有界】每进程 ≤200 行。**关注类**（WM_CHAR/KEY*/FOCUS/QUIT/SYSCHAR/DEADCHAR）**必打**；
//   其余只采样前 40 条 —— 否则泵里几千条 WM_PAINT/WM_TIMER 会把 200 行吃光、恰好把 WM_CHAR 挤掉。
// 【跨线程线索】入队/出队两侧都打「线程队列指针 + tid」：`wpf_x11_pump_into_queue(t)` 是把事件
//   推进**调用者线程**的队列 ⇒ 若 EventPump 在别的线程上跑，WM_CHAR 会进"错的队列"，两侧一比即现。
#define WPF_MSGFLOW_MAX 200
#define WPF_MSGFLOW_OTHER_MAX 40
static int wpf_msgflow_on(void)
{
    static int on = -1;
    if (on < 0) {
        const char *e = getenv("WPF_LINUX_MSGFLOW_TRACE");
        on = (e && *e && *e != '0') ? 1 : 0;
    }
    return on;
}
static void wpf_msgflow(const char *fmt, ...)
{
    if (!wpf_msgflow_on()) return;
    static int s_lines = 0;
    if (s_lines >= WPF_MSGFLOW_MAX) return;
    s_lines++;
    va_list ap;
    va_start(ap, fmt);
    fputs("[MSGFLOW] ", stderr);
    vfprintf(stderr, fmt, ap);
    fputc('\n', stderr);
    va_end(ap);
    fflush(stderr);
}
static int wpf_msgflow_interesting(UINT m)
{
    return m == WM_CHAR || m == WM_KEYDOWN || m == WM_KEYUP || m == WM_SYSKEYDOWN ||
           m == WM_SYSKEYUP || m == WM_SYSCHAR || m == 0x0103 /* WM_DEADCHAR：win32_internal.h 未定义 */ ||
           m == WM_SETFOCUS || m == WM_KILLFOCUS || m == WM_QUIT;
}
static unsigned long wpf_msgflow_tid(void) { return (unsigned long)(uintptr_t)pthread_self(); }

// 【MSGFLOW v2 · 仪器③】队列内容快照（从 head 起最多 8 条）。
//   作用：把"`WM_CHAR` 什么时候从队里消失"变成**逐行可见** —— 不必再靠"剩余条数"反推。
//   取锁：`wpf_lock()` 是**递归**锁（`wpf_do_global_init` 里设的 PTHREAD_MUTEX_RECURSIVE）⇒ 在持锁路径里调用也安全。
static void wpf_msgflow_snapshot(wpf_thread *t, char *buf, size_t cap)
{
    size_t off = 0;
    int n = 0, shown = 0;
    buf[0] = 0;
    if (!t) { snprintf(buf, cap, "[] 共0"); return; }
    wpf_lock();
    for (wpf_msg_node *p = t->head; p; p = p->next) {
        n++;
        if (shown < 8) {
            int w = snprintf(buf + off, (off < cap) ? cap - off : 0, "%s0x%04x",
                             shown ? "," : "", (unsigned)p->msg.message);
            if (w > 0) off += (size_t)w;
            shown++;
        }
    }
    pthread_mutex_unlock(&g_wpf.lock);
    snprintf(buf + ((off < cap) ? off : (cap ? cap - 1 : 0)), (off < cap) ? cap - off : 1,
             "] 共%d%s", n, (n > shown) ? "（只显示前 8 条）" : "");
}

// 「谁在取消息」：**进程级、非线程安全**（只影响文案，不影响行为；正常泵里同一时刻只有一个取消息者）。
//   为什么必须有：`PeekMessage(PM_NOREMOVE)` 内部也走 `wpf_queue_pop`，只看 pop 分不出是"主泵取的"
//   还是"某个嵌套 Peek 拿走的" —— 而后者正是本次要指认的嫌疑。
static const char *s_pop_api = "?";
static void wpf_msgflow_api(const char *a) { s_pop_api = a; }

// 【MSGFLOW v3 · F-C】**只读扫描**：匹配条件与 `wpf_queue_pop` **逐字一致**，但**不摘链、不 free、不重排**。
//   为什么必须换掉旧实现（"pop 出来 + malloc 一个节点插回队首"）：
//     · `malloc` 失败 ⇒ 消息**已被 pop 掉**、函数仍 `return 1` ⇒ **静默丢件**（Win32 语义下 PM_NOREMOVE 不该丢件）；
//     · 回插到**队首**（而命中的可能不是队首）⇒ **相对顺序被改变**；上游确有带 filter 的 PM_NOREMOVE 调用点
//       （`TextEditorTyping.cs:1609`：窗口 + `WM_MOUSEFIRST..WM_MOUSELAST`）⇒ 这条不是纸面风险。
//   Win32 的 PM_NOREMOVE 语义就是"**只看不取**" ⇒ 扫描是正确实现，同时把这条路上的 malloc/free 全去掉。
static int wpf_queue_peek_readonly(wpf_thread *t, WPF_MSG *out, HWND hwndFilter, UINT lo, UINT hi)
{
    if (!t) return 0;
    int found = 0;
    wpf_lock();
    for (wpf_msg_node *p = t->head; p; p = p->next) {
        WPF_MSG *m = &p->msg;
        int ok = 1;
        if (hwndFilter && m->hwnd != hwndFilter && m->message != WM_QUIT) ok = 0;
        if (lo || hi) {
            if (m->message < lo || m->message > hi) ok = 0;
        }
        if (ok) {
            *out = *m;               // 只拷一份出去，**不动链**
            found = 1;
            break;
        }
        // 【MSGFLOW v2 · 仪器②】跳过原因（只对 WM_CHAR）
        if (!ok && m->message == WM_CHAR)
            wpf_msgflow("skip(peek) msg=0x0102(WM_CHAR) hwnd=0x%llx 原因=%s filter=0x%llx lo=0x%x hi=0x%x "
                        "（只读扫描，不动链）",
                        (unsigned long long)(uintptr_t)m->hwnd,
                        (hwndFilter && m->hwnd != hwndFilter) ? "hwndFilter" : "range",
                        (unsigned long long)(uintptr_t)hwndFilter, (unsigned)lo, (unsigned)hi);
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return found;
}

// 取出第一条匹配的消息。filter 语义与 Win32 一致：
//   hwndFilter == NULL → 任意窗口；lo/hi 都是 0 → 任意消息号（含 WM_QUIT 之外的全部）
int wpf_queue_pop(wpf_thread *t, WPF_MSG *out, HWND hwndFilter, UINT lo, UINT hi)
{
    if (!t) return 0;
    wpf_lock();
    wpf_msg_node **pp = &t->head;
    wpf_msg_node  *prev = NULL;      // 【MSGFLOW v3 · F-A】被摘节点的**前驱**（用于修正 tail）
    uint8_t        last_mods = 0;    // [D-K1 · 波 21] 本次取出那条的修饰位快照（供诊断行）
    int found = 0;
    while (*pp) {
        WPF_MSG *m = &(*pp)->msg;
        int ok = 1;
        if (hwndFilter && m->hwnd != hwndFilter && m->message != WM_QUIT) ok = 0;
        if (lo || hi) {
            if (m->message < lo || m->message > hi) ok = 0;
        }
        if (ok) {
            wpf_msg_node *v = *pp;
            *pp = v->next;
            // 【MSGFLOW v3 · F-A 根因修】旧版无条件 `t->tail = NULL;`：**只要摘掉的是队尾**（哪怕队列
            //   里还有别的节点）就把 tail 置空 ⇒ 出现 `head!=NULL && tail==NULL` 的非法状态，下一次
            //   `push` 就会覆盖 head、孤儿化整条链（静默丢件）。现在：**指回新的队尾**（`prev==NULL`
            //   表示摘的是唯一节点 ⇒ 队列真为空，此时 tail 才是 NULL）。
            if (t->tail == v) t->tail = prev;
            *out = v->msg;
            last_mods = v->mods;
            wpf_keystate_note_pop_mods(out, v->mods);   // [D-K1] 按**这条消息**登记修饰位
            // [D-K1 · 波 24 修法乙]**按键类消息**取出时，让**实时表**采纳这条消息时刻的修饰键状态。
            //   依据（波 24 读数，私有件跨配置）：WPF 的修饰键读取**全在 `DispatchMessageW` 之外**
            //   （两档约 500 次调用，`在dispatch中=是` = 0）⇒ 它读的就是实时表；整批抽干把实时表推到了
            //   最后一个 X 事件（Ctrl 已抬）⇒ `Ctrl+A` 不生效。Win32 的 `GetKeyState` 本就是
            //   **消息队列语义** ⇒ 取出按键消息时按该消息的快照覆盖修饰键位。
            //   非按键消息**不动**；非翻译层盖戳的消息（post 进来的）**不动**（见 `mods_valid` 的注释）。
            //   本调用点在 `g_wpf.lock` 区间内 ⇒ `wpf_keystate_apply_queue_mods` 内部**不取锁**。
            if (v->mods_valid &&
                (out->message == WM_KEYDOWN || out->message == WM_KEYUP ||
                 out->message == WM_SYSKEYDOWN || out->message == WM_SYSKEYUP))
                wpf_keystate_apply_queue_mods(v->mods);
            free(v);
            found = 1;
            break;
        }
        // 【MSGFLOW v2 · 仪器②】跳过原因：**只对 WM_CHAR 打**（一行成本为零，且这正是要追的那条）。
        //   判据 (a) 的直接证据：某次带 filter/区间的 pop 把 WM_CHAR 跳过了 ⇒ WM_CHAR 留在队里。
        if (!ok && m->message == WM_CHAR)
            wpf_msgflow("skip msg=0x0102(WM_CHAR) hwnd=0x%llx 原因=%s filter=0x%llx lo=0x%x hi=0x%x "
                        "（本节点留在队里）",
                        (unsigned long long)(uintptr_t)m->hwnd,
                        (hwndFilter && m->hwnd != hwndFilter) ? "hwndFilter" : "range",
                        (unsigned long long)(uintptr_t)hwndFilter, (unsigned)lo, (unsigned)hi);
        prev = *pp;
        pp = &(*pp)->next;
    }
    if (found) {
        // GetMessagePos/GetMessageTime 的语义是「最近**取出**的消息」，
        // 所以记录点在这里，而不是投递点。
        t->last_pt_x = out->pt_x;
        t->last_pt_y = out->pt_y;
        t->last_msg_time = out->time;
    }
    pthread_mutex_unlock(&g_wpf.lock);

    // 【MSGFLOW】出队侧：message/hwnd/剩余条数/**调用来自哪个 API**/线程队列。
    //   注意：`wpf_msg_name()` 只接消息号，这里把十进制与名字一起给，便于与 `[msg]`/`[KEY_DIAG]` 对齐。
    if (found && wpf_msgflow_on()) {
        if (wpf_msgflow_interesting(out->message)) {
            char snap[160];
            wpf_msgflow_snapshot(t, snap, sizeof(snap));
            wpf_msgflow("pop api=%s msg=%u(0x%04x %s) hwnd=0x%llx 剩余=%d **该消息快照修饰位=0x%x** 队列内容=[%s 队列=%p tid=%lu",
                        s_pop_api, (unsigned)out->message, (unsigned)out->message,
                        wpf_msg_name(out->message),
                        (unsigned long long)(uintptr_t)out->hwnd, wpf_queue_count(t),
                        (unsigned)last_mods, snap, (void *)t, wpf_msgflow_tid());
        } else {
            static int s_other = 0;
            if (s_other < WPF_MSGFLOW_OTHER_MAX) {
                s_other++;
                wpf_msgflow("pop api=%s msg=%u(0x%04x %s) hwnd=0x%llx 剩余=%d 队列=%p tid=%lu（非关注类，采样 %d/%d）",
                            s_pop_api, (unsigned)out->message, (unsigned)out->message,
                            wpf_msg_name(out->message),
                            (unsigned long long)(uintptr_t)out->hwnd, wpf_queue_count(t),
                            (void *)t, wpf_msgflow_tid(), s_other, WPF_MSGFLOW_OTHER_MAX);
            }
        }
    }
    return found;
}

// ── 定时器 ─────────────────────────────────────────────────────────────────
int64_t wpf_timer_next_deadline(void)
{
    wpf_lock();
    int64_t best = -1;
    for (wpf_timer *t = g_wpf.timers; t; t = t->next)
        if (best < 0 || (int64_t)t->deadline_ms < best) best = (int64_t)t->deadline_ms;
    pthread_mutex_unlock(&g_wpf.lock);
    return best;
}

// 把到期的定时器转成 WM_TIMER（proc == NULL）或直接调回调（proc != NULL，
// Win32 里回调运行在**创建定时器的线程**，也就是这里）。
void wpf_timer_fire_due(wpf_thread *t)
{
    uint64_t now = wpf_now_ms();
    for (;;) {
        HWND hwnd = NULL;
        UINT_PTR id = 0;
        TIMERPROC proc = NULL;
        uint32_t elapse = 0;

        wpf_lock();
        wpf_timer *hit = NULL;
        for (wpf_timer *it = g_wpf.timers; it; it = it->next)
            if (it->deadline_ms <= now && it->owner_thread == (void *)wpf_thread_self()) { hit = it; break; }
        if (!hit) { pthread_mutex_unlock(&g_wpf.lock); return; }
        hwnd = hit->hwnd; id = hit->id; proc = hit->proc; elapse = hit->elapse_ms;
        if (proc) {
            // 回调型定时器在 Win32 里是**重复**的，直到 KillTimer。
            hit->deadline_ms = now + (elapse ? elapse : 1);
        } else {
            // 消息型定时器也是重复的（Win32 语义）。
            hit->deadline_ms = now + (elapse ? elapse : 1);
        }
        pthread_mutex_unlock(&g_wpf.lock);

        if (proc) {
            proc(hwnd, WM_TIMER, id, (DWORD)now);   // 不持锁调用（回调可能再进 shim）
        } else {
            WPF_MSG m;
            memset(&m, 0, sizeof(m));
            m.hwnd = hwnd;
            m.message = WM_TIMER;
            m.wParam = (WPARAM)id;
            m.lParam = (LPARAM)proc;
            m.time = (uint32_t)now;
            wpf_queue_push(t, &m);
        }
    }
}

UINT_PTR SetTimer(HWND hwnd, UINT_PTR id, UINT elapse, TIMERPROC proc)
{
    wpf_global_init();
    if (elapse == 0) elapse = 1;          // Win32 的 USER_TIMER_MINIMUM，取 1ms 更贴近 Linux 习惯

    wpf_lock();
    if (id == 0) {
        // 自动分配：与已存在的 (hwnd, id) 不冲突即可（我们从 1 往上扫）。
        for (id = 1; ; id++) {
            int used = 0;
            for (wpf_timer *t = g_wpf.timers; t; t = t->next)
                if (t->hwnd == hwnd && t->id == id) { used = 1; break; }
            if (!used) break;
        }
    } else {
        // Win32：同一个 (hwnd, id) 重复 SetTimer 会**重置**已有定时器并返回同一个 id。
        for (wpf_timer *t = g_wpf.timers; t; t = t->next) {
            if (t->hwnd == hwnd && t->id == id) {
                t->deadline_ms = wpf_now_ms() + elapse;
                t->elapse_ms = elapse;
                t->proc = proc;
                t->owner_thread = (void *)wpf_thread_self();
                pthread_mutex_unlock(&g_wpf.lock);
                return id;
            }
        }
    }
    wpf_timer *t = (wpf_timer *)calloc(1, sizeof(wpf_timer));
    t->hwnd = hwnd;
    t->id = id;
    t->elapse_ms = elapse;
    t->deadline_ms = wpf_now_ms() + elapse;
    t->proc = proc;
    t->owner_thread = (void *)wpf_thread_self();
    t->next = g_wpf.timers;
    g_wpf.timers = t;
    pthread_mutex_unlock(&g_wpf.lock);

    // 新建定时器后要唤醒本线程的泵（否则它可能正睡得比新定时器久）。
    wpf_queue_wake(wpf_thread_self());
    return id;
}
UINT_PTR SetTimerInternal(HWND h, UINT_PTR i, UINT e, TIMERPROC p) { return SetTimer(h, i, e, p); }

BOOL KillTimer(HWND hwnd, UINT_PTR id)
{
    wpf_global_init();
    wpf_lock();
    wpf_timer **pp = &g_wpf.timers;
    while (*pp) {
        if ((*pp)->hwnd == hwnd && (*pp)->id == id) {
            wpf_timer *v = *pp;
            *pp = v->next;
            free(v);
            pthread_mutex_unlock(&g_wpf.lock);
            return 1;
        }
        pp = &(*pp)->next;
    }
    pthread_mutex_unlock(&g_wpf.lock);
    return 0;   // Win32：定时器不存在返回 FALSE
}

// ── 窗口过程派发 ───────────────────────────────────────────────────────────
// 这是 DispatchMessageW / SendMessageW 的共同落点：查窗口当前的 GWL_WNDPROC，
// 有就调，没有就退回类的 lpfnWndProc，再没有就 DefWindowProcW。
//
// 【M7c Phase 2 · 消息台账（WPF_WIN32_MSG_TRACE=1）】
//   为什么需要它：Phase 2 实测到「窗口建出来了、映射了、标题对，但截屏是纯白」，
//   而纯白正是本 shim 建窗时给的 background_pixel（win32_x11.c 的 XCreateSimpleWindow
//   传的是 WhitePixel）—— 也就是说 **WPF 一个像素都没画上来**。
//   此时要回答的问题是"托管渲染 pass 到底有没有跑"，而托管侧（PresentationCore）
//   本轮不可改、无法加断点。消息台账就是那个**不改托管层也能看见它在动**的观测点：
//   WPF 的 HwndTarget 靠 WM_PAINT / WM_SIZE / WM_WINDOWPOSCHANGED 驱动重绘，
//   靠 MilCore 往通知窗口投私有消息驱动合成。谁到了、谁没到，一目了然。
//   默认关闭（一次 getenv + 一个 static），不影响验收路径。
static const char *wpf_msg_name(UINT m)
{
    switch (m) {
    case 0x0001: return "WM_CREATE";      case 0x0002: return "WM_DESTROY";
    case 0x0005: return "WM_SIZE";        case 0x0006: return "WM_ACTIVATE";
    case 0x0007: return "WM_SETFOCUS";    case 0x0008: return "WM_KILLFOCUS";
    case 0x000A: return "WM_ENABLE";      case 0x000F: return "WM_PAINT";
    case 0x0010: return "WM_CLOSE";       case 0x0011: return "WM_QUERYENDSESSION";
    case 0x0014: return "WM_ERASEBKGND";  case 0x0018: return "WM_SHOWWINDOW";
    case 0x001C: return "WM_ACTIVATEAPP"; case 0x0020: return "WM_SETCURSOR";
    case 0x0021: return "WM_MOUSEACTIVATE";
    case 0x0024: return "WM_GETMINMAXINFO";
    case 0x0046: return "WM_WINDOWPOSCHANGING";
    case 0x0047: return "WM_WINDOWPOSCHANGED";
    case 0x007E: return "WM_DISPLAYCHANGE";
    case 0x0081: return "WM_NCCALCSIZE";  case 0x0082: return "WM_NCHITTEST";
    case 0x0083: return "WM_NCACTIVATE";  case 0x0084: return "WM_NCCALCSIZE?";
    case 0x0085: return "WM_NCPAINT";     case 0x0086: return "WM_NCACTIVATE?";
    case 0x0113: return "WM_TIMER";       case 0x0118: return "WM_SYSTIMER";
    case 0x011F: return "WM_IME_SETCONTEXT";
    case 0x02E0: return "WM_DPICHANGED";
    case 0x031E: return "WM_DWMCOMPOSITIONCHANGED";
    default:
        if (m >= 0x0400 && m < 0x8000) return "WM_USER+n";
        if (m >= 0x8000) return "WM_APP+或注册消息";
        return "?";
    }
}

static void wpf_trace_msg(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    static int enabled = -1;
    if (enabled < 0) {
        const char *e = getenv("WPF_WIN32_MSG_TRACE");
        enabled = (e && *e && *e != '0') ? 1 : 0;
    }
    if (!enabled) return;

    const char *cls = "?";
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) {
        for (wpf_class *c = g_wpf.classes; c; c = c->next)
            if (c->atom == w->class_atom) { cls = c->name ? c->name : "?"; break; }
    }
    pthread_mutex_unlock(&g_wpf.lock);

    fprintf(stderr, "[msg] hwnd=0x%llx cls=%s msg=0x%04x %-24s wp=0x%llx lp=0x%llx\n",
            (unsigned long long)(uintptr_t)hwnd, cls, (unsigned)msg,
            wpf_msg_name(msg), (unsigned long long)(uintptr_t)wp,
            (unsigned long long)(uintptr_t)lp);
    fflush(stderr);
}

LRESULT wpf_dispatch_to_window(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    WNDPROC proc = NULL;
    wpf_global_init();

    // ── 波 51 · `TASK-0108` 的 `P3`：托管侧的**私有告示消息**在这里落地 ────────────────────
    // 【为什么拦在**这里**】本函数是 `SendMessageW`（`:837`）与 `DispatchMessageW`（`:744`）的
    //   **共同落点** ⇒ "发"与"投"两条路都覆盖；并且拦在这里 ⇒ 它**不再进**托管窗口过程
    //   （托管侧不必认识这条消息，也就不会与上游 `HwndSource` 的未知消息行为打交道）。
    // 【为什么调 `wpf_hints_publish`】它是写 X 的**唯一**入口、且**幂等**（值没变就不发
    //   `XSetWMNormalHints`）⇒ 托管侧多喊几次**不会**造成发布风暴（波 51 的先写判据 `I1`）；
    //   同时它复用 `P1` 的 `hints_pub_*` 缓存与 `P4` 的重入闸，**没有第二套发布路径**。
    // 【安全】非顶层/子窗口/message-only 窗口由 `wpf_hints_publish` 自己的谓词跳过；
    //   本分支**不进** `proc`（也就不会触发任何托管回调的同步重入）。
    if (msg == WPF_LINUX_WM_HINTS_CHANGED) {
        wpf_hints_publish(hwnd, "managed-DP-change");
        return 0;
    }

    wpf_trace_msg(hwnd, msg, wp, lp);
    wpf_lock();
    wpf_window *w = wpf_window_find(hwnd);
    if (w) {
        proc = w->wndproc ? w->wndproc : w->class_wndproc;
    } else if (hwnd == (HWND)(uintptr_t)g_wpf.root || hwnd == NULL) {
        proc = NULL;
    }
    pthread_mutex_unlock(&g_wpf.lock);

    if (!proc) return DefWindowProcW(hwnd, msg, wp, lp);
    return proc(hwnd, msg, wp, lp);
}

// ── 泵 ─────────────────────────────────────────────────────────────────────
// 一次「非阻塞轮询」：把 X 事件与到期定时器搬进队列，然后按 filter 出队一条。
static int pump_once(wpf_thread *t, WPF_MSG *out, HWND filter, UINT lo, UINT hi)
{
    // 「显示设备可用」的代发检查（M7c Phase 2）：消息号未登记时是一次静态读取，
    // 登记之后每个还没被告知的窗口只发一次。放在取消息**之前**，
    // 这样被延迟的 WM_PAINT（HwndTarget 的 _wasWmPaintProcessingDeferred）
    // 会在同一次泵里就被开闸 —— 不必等下一次 Expose。
    wpf_notify_display_devices_available();

    if (wpf_queue_pop(t, out, filter, lo, hi)) return 1;

    wpf_timer_fire_due(t);
    if (wpf_queue_pop(t, out, filter, lo, hi)) return 1;

    if (g_wpf.dpy) {
        wpf_lock();
        int n = wpf_x11_pump_into_queue(t);
        pthread_mutex_unlock(&g_wpf.lock);
        if (n > 0 && wpf_queue_pop(t, out, filter, lo, hi)) return 1;
    }
    return 0;
}

static int wait_for_input(wpf_thread *t, int timeout_ms)
{
    struct pollfd fds[2];
    int n = 0;
    if (t && t->wake_read >= 0) {
        fds[n].fd = t->wake_read;
        fds[n].events = POLLIN;
        fds[n].revents = 0;
        n++;
    }
    if (g_wpf.dpy && g_wpf.xfd >= 0) {
        // 先把 X 输出缓冲刷出去，否则 server 端可能还看不到我们的请求，
        // fd 也就永远不会变可读（这是 Xlib 的经典死锁点）。
        // 走 wpf_x11_flush（内部持 xlock）——XFlush 也会碰连接状态，
        // 必须和终结器线程的 XDestroyWindow 串行化。
        wpf_x11_flush();
        fds[n].fd = g_wpf.xfd;
        fds[n].events = POLLIN;
        fds[n].revents = 0;
        n++;
    }
    if (n == 0) {
        if (timeout_ms < 0) { usleep(2000); return 0; }
        usleep((useconds_t)timeout_ms * 1000);
        return 0;
    }
    return poll(fds, (nfds_t)n, timeout_ms);
}

// 把「下一个定时器到期」折算成 poll 的 timeout（毫秒）。无定时器 → -1（无限等）。
static int compute_timeout(const wpf_thread *t)
{
    (void)t;
    int64_t d = wpf_timer_next_deadline();
    if (d < 0) return -1;
    int64_t now = (int64_t)wpf_now_ms();
    int64_t delta = d - now;
    if (delta <= 0) return 0;
    if (delta > 1000) delta = 1000;   // 上限 1s：即便定时器很远也能周期性自查
    return (int)delta;
}

BOOL GetMessageW(WPF_MSG *msg, HWND hwnd, UINT lo, UINT hi)
{
    // 【诊断·env 门控】`WPF_X11_GRAB_PROBE=1` ⇒ 启动约 4s 后尝试 `XGrabPointer` 一次：
    //   - 返回 `GrabSuccess` ⇒ **没有别人持有指针抓取**（我们抢得到，随即释放）；
    //   - 返回 `AlreadyGrabbed`（1）⇒ **别的客户端正抓着指针** ⇒ 这就能解释"鼠标能动、按钮收不到"
    //     （抓取期间只有抓取方收按钮事件，掩码外的事件被丢弃）。
    //   用途：用户在真实桌面（RDP）上跑一次即可判定"是不是环境/转发层在抓指针"。
    {
        static int s_gp_done = 0;
        static unsigned long s_gp_t0 = 0;
        const char *gp = getenv("WPF_X11_GRAB_PROBE");
        if (gp && *gp && *gp != '0' && !s_gp_done) {
            if (!s_gp_t0) s_gp_t0 = wpf_now_ms();
            else if (wpf_now_ms() - s_gp_t0 >= 4000) {
                s_gp_done = 1;
                {
                    HWND gw = g_focus_window;
                    int r = wpf_x11_grab_probe(gw);
                    fprintf(stderr, "[GRABPROBE] XGrabPointer=%d (%s)%s\n", r,
                            r == 0 ? "GrabSuccess=没有人抓着" :
                            (r == 1 ? "AlreadyGrabbed=**别的客户端正抓着指针**" : "其它错误"),
                            "");
                }
            }
        }
    }
    // 【诊断·env 门控】`WPF_FAKE_CLICK="x,y[,delay_ms]"` ⇒ 启动约 3s 后，向**当前焦点顶层窗口**
    //   直接入队一对合成 `WM_LBUTTONDOWN`/`WM_LBUTTONUP`（用 `wpf_queue_push`，**完全绕开 X 注入**）。
    //   用途：本机 XTEST 与 XSendEvent 都注入不进这个栈（见 port/STATUS.md §32），
    //   而"点击到底响不响应"必须能验证 ⇒ 这条通道验证的是"入队 → 派发 → 命中测试 → UI 反应"这一段。
    {
        static int s_fc_done = 0;
        static int s_fc_delay = 3000;
        const char *fc = getenv("WPF_FAKE_CLICK");
        if (fc && *fc && !s_fc_done) {
            static unsigned long s_fc_t0 = 0;
            if (!s_fc_t0) {
                s_fc_t0 = wpf_now_ms();
                // 规格：`x,y[,delay_ms]` 或 `0xHWND,x,y[,delay_ms]`；delay 默认 3000
                s_fc_delay = 3000;
                {
                    const char *q = fc;
                    int ncommas = 0;
                    for (const char *cc = fc; *cc; cc++) if (*cc == ',') ncommas++;
                    // 最后一个逗号后的数字 == delay（仅当给的字段数比坐标多一个）
                    if ((ncommas == 2 && fc[0] != '0') || (ncommas == 3 && fc[0] == '0')) {
                        const char *last = strrchr(fc, ',');
                        if (last) s_fc_delay = atoi(last + 1);
                    }
                }
                fprintf(stderr, "[FAKECLICK] armed spec=%s delay=%d\n", fc, s_fc_delay);
            } else if (wpf_now_ms() - s_fc_t0 >= s_fc_delay) {
                const char *comma = strchr(fc, ',');
                int cx = 0, cy = 0;                 // 客户区坐标（规格里给出）
                HWND h = g_focus_window;
                if (fc[0] == '0' && (fc[1] == 'x' || fc[1] == 'X')) {
                    // 规格 "0xHWND,x,y"：显式指定窗口与坐标
                    h = (HWND)(uintptr_t)strtoul(fc, NULL, 0);
                    const char *c1 = strchr(fc, ',');
                    if (c1) { cx = atoi(c1 + 1); const char *c2 = strchr(c1 + 1, ','); if (c2) cy = atoi(c2 + 1); }
                } else {
                    cx = atoi(fc); cy = comma ? atoi(comma + 1) : 0;
                }
                // 自动挑**主窗口**：顶层 + 已映射 + 非 message-only + 尺寸 > 1x1 + **原点不在 (0,0)**。
                // 【为什么要"原点不在 (0,0)"】修前只按"第一个顶层窗口"取，取到的是隐藏的 `HwndWrapper`
                //   （800x600+0+0，如 0x200006/0x200005），点击打在它身上毫无反应 —— 实测踩到。
                //   主窗口在 (+240+212) 这类真实位置；隐藏的内部窗口都是 (0,0)。
                if (!h) {
                    wpf_lock();
                    for (wpf_window *wi = g_wpf.windows; wi; wi = wi->next) {
                        if (wi->is_message_only || wi->parent != NULL) continue;
                        if (!wi->mapped) continue;
                        if (wi->width < 2 || wi->height < 2) continue;
                        if (wi->x == 0 && wi->y == 0) continue;   // 隐藏的内部 HwndWrapper 都是 (0,0)
                        h = wi->hwnd;
                        break;
                    }
                    pthread_mutex_unlock(&g_wpf.lock);
                }
                if (h) {
                    WPF_MSG dm, um;
                    memset(&dm, 0, sizeof dm); memset(&um, 0, sizeof um);
                    dm.hwnd = um.hwnd = h;
                    dm.message = WM_LBUTTONDOWN; um.message = WM_LBUTTONUP;
                    dm.wParam = 1;   // MK_LBUTTON（本文件未定义该宏，按值写）
                    um.wParam = 0;
                    dm.lParam = um.lParam = (LPARAM)(uint32_t)(((uint32_t)(cx & 0xFFFF)) | (((uint32_t)(cy & 0xFFFF)) << 16));
                    dm.pt_x = um.pt_x = cx; dm.pt_y = um.pt_y = cy;
                    dm.time = um.time = (uint32_t)wpf_now_ms();
                    wpf_queue_push(wpf_thread_self(), &dm);
                    wpf_queue_push(wpf_thread_self(), &um);
                    fprintf(stderr, "[FAKECLICK] pushed DOWN+UP hwnd=0x%llx at %d,%d\n",
                            (unsigned long long)(uintptr_t)h, cx, cy);
                    s_fc_done = 1;
                } else {
                    fprintf(stderr, "[FAKECLICK] no focus window yet\n");
                }
            }
        }
    }

    wpf_global_init();
    if (!msg) { wpf_set_last_error(87); return (BOOL)-1; }
    wpf_thread *t = wpf_thread_self();
    wpf_msgflow_api("GetMessageW");          // 【MSGFLOW】取消息者标注
    // 【MSGFLOW v2 · 仪器①】入口参数：**这一格回答"有没有带区间/带窗口的调用在跑"** ——
    //   若某次 `GetMessageW` 带 `hwnd` 或 `lo..hi`，被它跳过的 `WM_CHAR` 就永远留在队里（判据 (a)）。
    wpf_msgflow("call api=GetMessageW hwnd=0x%llx lo=0x%x hi=0x%x tid=%lu",
                (unsigned long long)(uintptr_t)hwnd, (unsigned)lo, (unsigned)hi, wpf_msgflow_tid());

    for (;;) {
        if (pump_once(t, msg, hwnd, lo, hi)) {
            if (msg->message == WM_QUIT) return 0;   // Win32：取到 WM_QUIT → 返回 FALSE
            return 1;
        }
        // 有 PostQuitMessage 但队列里没有别的消息时，立刻醒来（不无限等）。
        wpf_lock();
        int quit = t->quit_code;
        pthread_mutex_unlock(&g_wpf.lock);
        if (quit >= 0) {
            memset(msg, 0, sizeof(*msg));
            msg->message = WM_QUIT;
            msg->wParam = (WPARAM)quit;
            return 0;
        }
        int timeout = compute_timeout(t);
        wait_for_input(t, timeout);
        wpf_queue_drain_wake(t);
    }
}

BOOL PeekMessageW(WPF_MSG *msg, HWND hwnd, UINT lo, UINT hi, UINT remove)
{
    wpf_global_init();
    if (!msg) { wpf_set_last_error(87); return 0; }
    wpf_thread *t = wpf_thread_self();

    // 【MSGFLOW v2 · 仪器①】入口参数（含 remove 位）
    wpf_msgflow("call api=PeekMessageW hwnd=0x%llx lo=0x%x hi=0x%x remove=0x%x tid=%lu",
                (unsigned long long)(uintptr_t)hwnd, (unsigned)lo, (unsigned)hi,
                (unsigned)remove, wpf_msgflow_tid());

    if (remove & PM_REMOVE) {
        wpf_msgflow_api("PeekMessageW(PM_REMOVE)");   // 【MSGFLOW】
        if (pump_once(t, msg, hwnd, lo, hi)) return 1;
        return 0;
    }
    wpf_msgflow_api("PeekMessageW(PM_NOREMOVE)");      // 【MSGFLOW】
    // PM_NOREMOVE：Win32 语义是「**看一眼不取走**」⇒ 用**只读扫描**实现（v3 · F-C）。
    //   旧实现"pop 出来 + malloc 一个节点插回队首"有两个真缺陷：malloc 失败会**静默丢件**、
    //   命中非队首时回插队首会**改序**（上游 `TextEditorTyping.cs:1609` 正是带 filter 的 PM_NOREMOVE）。
    //   现在既不摘链也不分配 ⇒ 两条风险同时消失（不再需要那两条告警）。
    if (!wpf_queue_peek_readonly(t, msg, hwnd, lo, hi)) {
        wpf_timer_fire_due(t);
        if (!wpf_queue_peek_readonly(t, msg, hwnd, lo, hi)) {
            if (g_wpf.dpy) {
                wpf_lock();
                int n = wpf_x11_pump_into_queue(t);
                pthread_mutex_unlock(&g_wpf.lock);
                (void)n;
            }
            if (!wpf_queue_peek_readonly(t, msg, hwnd, lo, hi)) return 0;
        }
    }
    return 1;
}
BOOL PeekMessageA(WPF_MSG *m, HWND h, UINT a, UINT b, UINT r) { return PeekMessageW(m, h, a, b, r); }
BOOL PeekMessage(WPF_MSG *m, HWND h, UINT a, UINT b, UINT r) { return PeekMessageW(m, h, a, b, r); }

BOOL TranslateMessage(const WPF_MSG *msg)
{
    // Win32 的 TranslateMessage 把 WM_KEYDOWN/WM_SYSKEYDOWN 变成 WM_CHAR。
    // 我们在 X11 翻译层已经**直接**产生了 WM_CHAR（见 win32_x11.c），
    // 这里再产生一次就会重复。返回 FALSE = 「没有翻译出字符消息」，
    // 调用方（Dispatcher.TranslateAndDispatchMessage）不检查返回值，安全。
    (void)msg;
    return 0;
}

// [D-K1 · 波 21] "正在派发的那条消息"的修饰快照 —— **按消息四元组登记/回查**。
// 【为什么不是"记住最后一条出队的消息"（波 20 的写法，实测**不成立**）】波 21 的取证行显示
//   `dispatch msg=0x0100 vk/wp=0xa2 快照修饰位=0x0` —— 连 Ctrl 自己的 KeyDown 都是 0，
//   而节点上明明盖了戳 ⇒ 说明"出队 → 派发"之间**还有别的出队**（托管泵会批量取消息、
//   也可能有别的线程在同一进程里取消息）⇒ "最后一条"会被覆盖。⇒ 改成**四元组匹配的小环形表**。
#define WPF_POPMODS_RING 16
typedef struct { HWND hwnd; UINT msg; WPARAM wp; LPARAM lp; uint8_t mods; } wpf_popmods_entry;
static wpf_popmods_entry s_ring[WPF_POPMODS_RING];
static int s_ring_next;
static uint8_t s_dispatch_mods;
static int     s_dispatch_valid;
// [D-K1 · 波 24] "此刻是否在 `DispatchMessageW` 之内" —— 与"快照有没有登记到"是**两件事**：
//   WPF 的输入路径（`ComponentDispatcher.ThreadPreprocessMessage` → `HwndKeyboardInputProvider`）
//   跑在**派发之前**，那时读表就是读实时表；而整批抽干时消息照样会被派发。
//   探针必须把这两种情形分开，否则"整批档读不到 Ctrl"会被误判成同一格。
//   用**深度**而不是布尔：wndproc 里再派发是合法的（嵌套），布尔会被内层提前清掉。
static int     s_dispatch_depth;
int wpf_msg_in_dispatch(void) { return s_dispatch_depth > 0; }

void wpf_keystate_note_pop_mods(const WPF_MSG *m, uint8_t mods)
{
    if (!m) return;
    wpf_popmods_entry *e = &s_ring[s_ring_next];
    s_ring_next = (s_ring_next + 1) % WPF_POPMODS_RING;
    e->hwnd = m->hwnd; e->msg = m->message; e->wp = m->wParam; e->lp = m->lParam; e->mods = mods;
}

int wpf_keystate_lookup_mods(const WPF_MSG *m, uint8_t *out)
{
    if (!m) return 0;
    for (int i = 0; i < WPF_POPMODS_RING; ++i) {                    // 从最新往回找
        int idx = (s_ring_next - 1 - i + WPF_POPMODS_RING * 2) % WPF_POPMODS_RING;
        wpf_popmods_entry *e = &s_ring[idx];
        if (e->msg == 0) continue;
        if (e->hwnd == m->hwnd && e->msg == m->message && e->wp == m->wParam && e->lp == m->lParam) {
            if (out) *out = e->mods;
            return 1;
        }
    }
    return 0;
}
int wpf_keystate_dispatch_mods(uint8_t *out)
{
    if (!s_dispatch_valid) return 0;
    if (out) *out = s_dispatch_mods;
    return 1;
}

LRESULT DispatchMessageW(const WPF_MSG *msg)
{
    if (!msg) return 0;
    // ComponentDispatcher.RaiseThreadMessage 之外的正常路径；
    // Dispatcher 只在未处理时才会调到这里。
    // [D-K1 · 波 20] 派发期间把"这条消息的修饰位快照"暴露出去 ⇒ `GetKeyState` 在**派发期**读到的
    //   是"事件时刻"的状态，而不是被后续事件改过的实时表（整批抽干的坑）。
    // 【已登记限制】`s_dispatch_valid` 是**单槽**：嵌套派发时内层会覆盖、退出时清掉外层的快照
    //   （波 24 的探针把 `在dispatch中=` 与 `快照=` 分开打，正是为了让这种情形可见而不是被误读）。
    s_dispatch_depth++;
    s_dispatch_valid = wpf_keystate_lookup_mods(msg, &s_dispatch_mods);
    if (wpf_msgflow_on() && (msg->message == WM_KEYDOWN || msg->message == WM_KEYUP ||
                             msg->message == WM_CHAR || msg->message == WM_SYSKEYDOWN ||
                             msg->message == WM_SYSKEYUP))
        wpf_msgflow("dispatch msg=0x%04x(%s) vk/wp=0x%llx 快照修饰位=0x%x（实时表 ctrl=%d shift=%d alt=%d）t=%llums",
                    (unsigned)msg->message, wpf_msg_name(msg->message),
                    (unsigned long long)(uintptr_t)msg->wParam, (unsigned)s_dispatch_mods,
                    (g_wpf.key_state[0x11] & 0x80) ? 1 : 0, (g_wpf.key_state[0x10] & 0x80) ? 1 : 0,
                    (g_wpf.key_state[0x12] & 0x80) ? 1 : 0,
                    (unsigned long long)wpf_now_ms());
    LRESULT r = wpf_dispatch_to_window(msg->hwnd, msg->message, msg->wParam, msg->lParam);
    s_dispatch_valid = 0;    // C 里没有 try/finally ⇒ 手动清（本函数无异常路径）
    s_dispatch_depth--;
    return r;
}
LRESULT DispatchMessageA(const WPF_MSG *m) { return DispatchMessageW(m); }
LRESULT DispatchMessage(const WPF_MSG *m) { return DispatchMessageW(m); }

// ── 投递 / 发送 ────────────────────────────────────────────────────────────
// 【W136A · F3b 后续 · 主控裁定 (A)】(A) 具名台账：`PostMessageW` 打到**线程已死**的窗口时，
//   **不许静默改投调用者队列**（本仓铁律：不许静默 no-op）。
//   **static ⇒ 不新增导出（导出数仍 547）**；**不受 MSGFLOW 开关限制**；每进程有界 ≤32 行。
//   主控要求的字段：hwnd / tid / caller_tid 都给到（tid 用"窗口的 owner 已置空"表达，
//   真线程 id 在 F3b 置空那一刻已不可追，故用 owner_thread=(nil) 指明"死因是线程退出"）。
#define WPF_POSTMSG_DEAD_MAX 32
static int s_postmsg_dead_lines;
static void wpf_postmsg_dead_target(HWND hwnd)
{
    if (s_postmsg_dead_lines >= WPF_POSTMSG_DEAD_MAX) return;
    s_postmsg_dead_lines++;
    fprintf(stderr,
            "[POSTMSG_DEAD_TARGET] hwnd=%p owner_thread=(nil) 原因=该窗口的线程已退出 "
            "caller_tid=%lu 处理=拒绝投递(return 0 + ERROR_INVALID_WINDOW_HANDLE) **不再改投调用者队列** "
            "（Win32：窗口线程退出后该 hwnd 已不可用；改投会导致窗口过程在错误线程上执行）\n",
            (void *)hwnd, wpf_msgflow_tid());
    fflush(stderr);
}

BOOL PostMessageW(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    wpf_global_init();

    // 目标线程：窗口的创建线程；hwnd 为 NULL 时是调用线程（Win32 语义）。
    wpf_thread *target = NULL;
    // 【W136A · 主控裁定 (A)/(B)】"窗口在表里但它的 owner_thread 为空" = 该窗口的线程已死
    //   （F3b 在线程退出时把 owner_thread 置空，见 win32_core.c 的 wpf_thread_destroy）。
    //   ⚠️ 必须在**锁内**取这个布尔：解锁之后 `w` 可能已被 DestroyWindow 释放（悬垂指针）。
    int owner_dead_in_table = 0;
    // 【W146A · TASK-0211 · 关闭 TOCTOU（站点 A）】「查表 → 判死 → 取 pt → 入队」必须是**同一个临界区**。
    //   g_wpf.lock 可重入（win32_core.c:49 PTHREAD_MUTEX_RECURSIVE）⇒ 持锁跨 wpf_queue_push 安全：
    //   push 的 :98 再取一次（深度 1→2）、:132 只把深度掉回 1（本函数这层仍持锁）
    //   ⇒ :134 的 wpf_queue_wake 与 :137-145 的 msgflow 仍在**本临界区内**。
    //   ⚠️【前提】这套修法**只靠递归锁计数成立**：若 g_wpf.lock 变成普通互斥量、或 push 少放/多放一次锁，
    //     它会**静默失效**（读数看似 HELD，实际 push 中途已放锁）⇒ 由 ~/w146a/bin/premise.py 盯着。
    //   充分性：属主线程的 wpf_thread_destroy 清 owner_thread 在锁内（win32_core.c:180-185）、
    //     free(t) 在其后（:190）⇒ 读到非空之后一直持锁 ⇒ 把它卡在 :180，到不了 :190。
    wpf_lock();
    if (hwnd) {
        wpf_window *w = wpf_window_find(hwnd);
        // owner_thread 直接就是 wpf_thread*（见 win32_internal.h 的说明），
        // 不需要再查表 —— 这也保证了 PostMessage 能在任意线程上安全调用。
        target = w ? (wpf_thread *)w->owner_thread : NULL;
        if (w && !w->owner_thread) owner_dead_in_table = 1;
        if (!target) {
            // (A)+(B)：先打具名台账，再按 **Win32 正确语义** 拒绝投递。
            //   为什么可以改语义（机械证，见报告 §⑬）：`wpf_window_add`（win32_core.c:893）
            //   与 `w->owner_thread = …`（:920）在**同一个临界区**（:880 lock … :921 unlock）
            //   ⇒ 在 F3b 之前，`窗口在表里 ∧ owner_thread==NULL` **不可观测** ⇒ 没有任何
            //   既有调用者可能依赖那支"改投调用者队列"的兜底；唯一能走到它的就是"线程已死"。
            //   （`hwnd == g_wpf.root` 那一格 **不受影响**，仍走下面的 `target = wpf_thread_self()`。）
            if (owner_dead_in_table) {
                pthread_mutex_unlock(&g_wpf.lock);   // 早退支：先放锁再报错
                wpf_postmsg_dead_target(hwnd);
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE);
                return 0;
            }
            // 窗口不在表里（已销毁或从未创建）→ Win32 返回 FALSE。
            if (!wpf_window_find(hwnd) && hwnd != (HWND)(uintptr_t)g_wpf.root) {
                pthread_mutex_unlock(&g_wpf.lock);   // 早退支：先放锁再报错
                wpf_set_last_error(ERROR_INVALID_WINDOW_HANDLE);
                return 0;
            }
            target = wpf_thread_self();
        }
    } else {
        target = wpf_thread_self();
    }

    WPF_MSG m;
    memset(&m, 0, sizeof(m));
    m.hwnd = hwnd;
    m.message = msg;
    m.wParam = wp;
    m.lParam = lp;
    m.time = (uint32_t)wpf_now_ms();
    m.pt_x = target ? target->last_pt_x : 0;      // 与"取指针"同一临界区 ⇒ 不再在陈旧指针上取锁
    m.pt_y = target ? target->last_pt_y : 0;
    wpf_queue_push(target, &m);
    pthread_mutex_unlock(&g_wpf.lock);            // ← 唯一一次放锁，在投递**之后**
    return 1;
}
BOOL PostMessageA(HWND h, UINT m, WPARAM w, LPARAM l) { return PostMessageW(h, m, w, l); }
BOOL PostMessage(HWND h, UINT m, WPARAM w, LPARAM l) { return PostMessageW(h, m, w, l); }

BOOL PostThreadMessageW(uint32_t threadId, UINT msg, WPARAM wp, LPARAM lp)
{
    wpf_global_init();
    wpf_thread *target = NULL;
    // 【W146A · TASK-0211 · 关闭 TOCTOU（站点 B）】与 PostMessageW 同形、同根因：
    //   修前 :896 放锁、:906 才用 ⇒ 缝里目标线程可退出并被 free（TLS 析构 wpf_thread_destroy），
    //   而这条路径**没有任何存活校验**。修法：**唯一一次放锁挪到 push 之后**
    //   （g_wpf.lock 可重入 ⇒ 嵌套安全；前提见站点 A 的注释）。
    //   充分性：wpf_thread_destroy 摘链在锁内（win32_core.c:161-168）⇒
    //     ① 目标线程先摘链 ⇒ 本函数找不到它 ⇒ 走 1444 早退（拿不到悬垂指针）；
    //     ② 本函数先找到 ⇒ 一直持锁到 push 完成 ⇒ 目标线程卡在 :161 摘不了链，
    //        更到不了 :180（清属主）与 :190（free）。
    wpf_lock();
    for (wpf_thread *t = g_wpf.threads; t; t = t->next)
        if (WPF_THREAD_ID(t) == threadId) { target = t; break; }
    if (!target) {
        pthread_mutex_unlock(&g_wpf.lock);   // 早退支：先放锁再报错
        wpf_set_last_error(1444);            // ERROR_INVALID_THREAD_ID
        return 0;
    }

    WPF_MSG m;
    memset(&m, 0, sizeof(m));
    m.hwnd = NULL;
    m.message = msg;
    m.wParam = wp;
    m.lParam = lp;
    m.time = (uint32_t)wpf_now_ms();
    wpf_queue_push(target, &m);
    pthread_mutex_unlock(&g_wpf.lock);            // ← 唯一一次放锁，在投递**之后**
    return 1;
}

void PostQuitMessage(int exitCode)
{
    wpf_global_init();
    wpf_thread *t = wpf_thread_self();
    wpf_lock();
    t->quit_code = exitCode;
    pthread_mutex_unlock(&g_wpf.lock);
    // 也投一条真 WM_QUIT，让队列里看得见（GetMessageW 会返回 0）。
    WPF_MSG m;
    memset(&m, 0, sizeof(m));
    m.message = WM_QUIT;
    m.wParam = (WPARAM)exitCode;
    m.time = (uint32_t)wpf_now_ms();
    wpf_queue_push(t, &m);
}

// SendMessage：同步。跨线程在 Win32 上会阻塞发送方直到目标线程派发完；
// 我们直接**在调用线程**上执行窗口过程。对本工程的实际用法（WPF 的
// HwndSubclass 自我解绑、ManagedWndProcTracker 清理）都在同一线程上，
// 语义等价。跨线程 SendMessage 未实现（登记为限制，不静默改变行为：
// 目标确实收到了消息，只是执行线程不同）。
LRESULT SendMessageW(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    wpf_global_init();
    return wpf_dispatch_to_window(hwnd, msg, wp, lp);
}
LRESULT SendMessageA(HWND h, UINT m, WPARAM w, LPARAM l) { return SendMessageW(h, m, w, l); }
LRESULT SendMessage(HWND h, UINT m, WPARAM w, LPARAM l) { return SendMessageW(h, m, w, l); }

LRESULT SendMessageTimeoutW(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp, UINT flags,
                            UINT timeout, UINT_PTR *result)
{
    (void)flags; (void)timeout;
    LRESULT r = SendMessageW(hwnd, msg, wp, lp);
    if (result) *result = (UINT_PTR)r;
    return r;
}
LRESULT SendMessageTimeoutA(HWND h, UINT m, WPARAM w, LPARAM l, UINT f, UINT t, UINT_PTR *r)
{ return SendMessageTimeoutW(h, m, w, l, f, t, r); }
LRESULT SendMessageTimeout(HWND h, UINT m, WPARAM w, LPARAM l, UINT f, UINT t, UINT_PTR *r)
{ return SendMessageTimeoutW(h, m, w, l, f, t, r); }

// ── 队列状态 ───────────────────────────────────────────────────────────────
DWORD MsgWaitForMultipleObjectsEx(DWORD nCount, const HANDLE *pHandles, DWORD ms,
                                  DWORD wakeMask, DWORD flags)
{
    (void)flags;
    wpf_global_init();

    if (nCount != 0) {
        // WPF 全树只有 Dispatcher.IsInputPending() 一处调用，且 nCount 恒为 0。
        // 真相待：不假装能等内核对象。返回 WAIT_FAILED + 明确的 LastError。
        wpf_set_last_error(50);     // ERROR_NOT_SUPPORTED
        return WAIT_FAILED;
    }

    wpf_thread *t = wpf_thread_self();
    uint64_t deadline = wpf_now_ms() + (ms == 0xFFFFFFFFu ? 0 : ms);
    int infinite = (ms == 0xFFFFFFFFu);

    for (;;) {
        int has = 0;
        // 队列里有匹配 wakeMask 的消息？
        wpf_lock();
        for (wpf_msg_node *n = t->head; n; n = n->next) {
            UINT m = n->msg.message;
            if (wakeMask & QS_POSTMESSAGE) { has = 1; break; }
            if ((wakeMask & QS_TIMER) && m == WM_TIMER) { has = 1; break; }
            if ((wakeMask & QS_PAINT) && m == WM_PAINT) { has = 1; break; }
            if ((wakeMask & QS_KEY) &&
                (m == WM_KEYDOWN || m == WM_KEYUP || m == WM_CHAR ||
                 m == WM_SYSKEYDOWN || m == WM_SYSKEYUP || m == WM_SYSCHAR)) { has = 1; break; }
            if ((wakeMask & QS_MOUSEMOVE) && (m == WM_MOUSEMOVE || m == WM_MOUSELEAVE)) { has = 1; break; }
            if ((wakeMask & QS_MOUSEBUTTON) &&
                (m == WM_LBUTTONDOWN || m == WM_LBUTTONUP || m == WM_RBUTTONDOWN ||
                 m == WM_RBUTTONUP || m == WM_MBUTTONDOWN || m == WM_MBUTTONUP ||
                 m == WM_MOUSEWHEEL)) { has = 1; break; }
        }
        int due_timer = 0;
        int64_t nd = -1;
        for (wpf_timer *it = g_wpf.timers; it; it = it->next)
            if (nd < 0 || (int64_t)it->deadline_ms < nd) nd = (int64_t)it->deadline_ms;
        if (nd >= 0 && (int64_t)wpf_now_ms() >= nd) due_timer = 1;
        pthread_mutex_unlock(&g_wpf.lock);

        if (has || due_timer) return WAIT_OBJECT_0;

        // X 侧有没有待处理输入？XPending 会 flush 输出缓冲，是真的探测。
        if (g_wpf.dpy && wpf_x11_pending() > 0) return WAIT_OBJECT_0;

        if (!infinite && wpf_now_ms() >= deadline) return WAIT_TIMEOUT;
        int timeout = infinite ? 50 : (int)(deadline - wpf_now_ms());
        if (timeout > 50) timeout = 50;   // 分片等待，及时响应 X 输入（MWMO_INPUTAVAILABLE）
        wait_for_input(t, timeout);
        wpf_queue_drain_wake(t);
    }
}

// ── RegisterWindowMessage ──────────────────────────────────────────────────
// 进程内唯一、同一字符串恒等。Win32 的取值范围是 0xC000..0xFFFF。
typedef struct wpf_regmsg {
    char *name;
    uint32_t id;
    struct wpf_regmsg *next;
} wpf_regmsg;
static wpf_regmsg *g_regmsgs = NULL;

// ==========================================================================
//  M7c Phase 2 · 渲染起搏器：代发 `DisplayDevicesAvailabilityChanged`
// ==========================================================================
// 【这一步解决的是"窗口出来了、截屏却是纯白"的那个真问题】
//   Phase 2 实测：窗口 0x200005 建出来、映射了、标题对、消息也到了
//   （WM_NCCREATE/WM_CREATE/WM_SIZE 640x400/WM_MOVE/WM_SHOWWINDOW/WM_PAINT/WM_SETICON），
//   但 **一个像素都没画上去**，而且启动后再无任何消息 —— 应用是**静止**的，不是崩了。
//
//   把托管侧读到的那条闸门找出来（PresentationCore/System/Windows/InterOp/HwndTarget.cs）：
//
//       private bool _displayDevicesAvailable = MediaContext.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable;
//
//       case WindowMessage.WM_PAINT:
//           if (_displayDevicesAvailable || MediaContext.ShouldRender...)
//               { _wasWmPaintProcessingDeferred = false; DoPaint(); }   // ← DoPaint 里才 InvalidateRect
//           else
//               _wasWmPaintProcessingDeferred = true;                   // ← 只是**记账**，什么都不做
//           break;
//
//   而 `MediaContext.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable`
//   （PresentationCore/System/Windows/Media/MediaContext.cs:172）= 桌面会话下
//   就是 AppContext 开关，**默认 false**。于是：
//       WM_PAINT → 不 DoPaint → 不 InvalidateRect → 不排渲染 pass → 不 Commit → 不 Present。
//   窗口保持 shim 建窗时给的那个 background_pixel（白），一动不动。**与实测逐条吻合。**
//
//   那 Windows 上这条闸门是谁打开的？HwndTarget 里只有一个地方把
//   `_displayDevicesAvailable` 置真（HwndTarget.cs:951）：
//
//       if (msg == s_DisplayDevicesAvailabilityChanged)
//           _displayDevicesAvailable = (wparam.ToInt32() != 0);
//
//   而 `s_DisplayDevicesAvailabilityChanged` = `RegisterWindowMessage("DisplayDevicesAvailabilityChanged")`
//   —— **托管侧全仓没有一处发送它**（grep 全 upstream：除 HwndTarget 自己外 0 命中）。
//   发它的是**原生 MilCore**（Windows 的 wpfgfx_cor3.dll 在枚举到有效显示设备/策略变更时
//   往每个 HwndTarget 窗口投这条消息）。这也解释了 MediaContext.CreateChannels() 里紧接着
//   `DUCE.NotifyPolicyChangeForNonInteractiveMode(ShouldRenderEvenWhenNoDisplayDevicesAreAvailable, Channel)`
//   这条"把策略告诉原生侧"的命令。
//
// 【本工程的缺口与修法】
//   我们的原生 MilCore 端（T1 的 AOT 桥 wpfgfx_cor3.so）**既没有消息循环、也没有 X 连接**：
//   它拿不到 HWND 对应的 X 窗口，也没法 PostMessage。而 Win32 shim **两样都有**
//   （它就是消息泵，`g_wpf.dpy` 就是那条 X 连接）。所以在 Linux 上由 shim 代发是唯一
//   能做到的位置，也是**语义上诚实**的：Xvfb/桌面下显示器确实可用（X 连上了、screen 有效），
//   所以 wParam = 1 是**真值**，不是为了让程序继续跑而编的假话。
//   若将来 T1 的 MilCore 侧要自己发（更贴近 Windows 的分工），把本函数变成 no-op 即可，
//   两者不会冲突（重复发只是把同一个标志再置一遍 1）。
//
// 【为什么在泵里"扫着发"而不是在某个固定时刻发一次】
//   `RegisterWindowMessage("DisplayDevicesAvailabilityChanged")` 由 HwndTarget 的**静态构造**
//   触发，而窗口是它之前建的（HwndSource 先建 HwndWrapper，再建 HwndTarget）。
//   也就是说"窗口已存在"与"消息号已知"这两件事的**先后顺序不固定**：
//     · 若在 RegisterWindowMessage 之前发 → 消息号还是 0，发不出去；
//     · 若只在 RegisterWindowMessage 那一刻发 → 那一刻窗口可能还没建。
//   所以登记到消息号之后，在泵里对"还没发过的窗口"补发一次（每窗口只发一次，标志位记账）。
//   这个设计对顺序不敏感，也不依赖任何时钟。
static uint32_t g_display_devices_msg = 0;

void wpf_notify_display_devices_available(void)
{
    if (g_display_devices_msg == 0) return;      // 还没登记 → 无事可做（快速路径）
    if (!g_wpf.dpy || g_wpf.x_failed) return;    // 没有可用的 X 显示 → 不发（不发假信号）

    // 诊断用总开关（WPF_WIN32_NO_DISPLAY_NOTIFY=1）：把这条代发关掉，
    // 用来实测"渲染 pass 到底依不依赖它"。M7c 实测结论：**不依赖** ——
    // 关掉之后 CompositionTarget.Rendering 照样 ~88 帧/秒（见报告 §4.x）。
    // 保留它的理由因此收窄为：它是 Windows 上原生 MilCore 的真实行为
    // （hwndtarget.cpp: PostDisplayAvailabilityMessage），管的是 WM_PAINT→DoPaint
    // 这条**重绘/校验**路径（`_displayDevicesAvailable` 就是那条闸门），
    // 而不是首帧渲染的调度。开关只在诊断时用，不参与验收。
    {
        static int disabled = -1;
        if (disabled < 0) {
            const char *e = getenv("WPF_WIN32_NO_DISPLAY_NOTIFY");
            disabled = (e && *e && *e != '0') ? 1 : 0;
        }
        if (disabled) return;
    }

    HWND pending[16];
    int n = 0;
    wpf_lock();
    for (wpf_window *w = g_wpf.windows; w && n < (int)(sizeof(pending) / sizeof(pending[0])); w = w->next) {
        if (w->display_devices_notified) continue;
        if (w->is_message_only) continue;        // 消息窗口不参与呈现，不打扰
        if (w->in_destroy) continue;
        w->display_devices_notified = 1;
        pending[n++] = w->hwnd;
    }
    pthread_mutex_unlock(&g_wpf.lock);

    for (int i = 0; i < n; i++) {
        // wParam = 1：「显示设备可用」。lParam 未用。
        PostMessageW(pending[i], g_display_devices_msg, 1, 0);
        if (getenv("WPF_WIN32_MSG_TRACE"))
            fprintf(stderr, "[msg] 代发 DisplayDevicesAvailabilityChanged(1) → hwnd=0x%llx"
                            "（Windows 上这条由原生 MilCore 发，见 win32_msg.c 的说明）\n",
                    (unsigned long long)(uintptr_t)pending[i]);
    }
}

// CharSet.Auto（Unix→Ansi）：运行时会先探到**裸名**，所以裸名必须收 UTF-8。
// ⚠️ 实测踩过：裸名原来转发到 W 变体，UTF-8 字节被当成 UTF-16 解释 ——
//    同一字符串仍会映射到同一个 id（因为乱码是确定的），但两张不同字符串
//    有概率撞成同一个 id，且 HwndSubclass.DetachMessage 这类名字全是垃圾。
static uint32_t register_window_message_utf8(const char *utf8)
{
    wpf_global_init();
    if (!utf8) { wpf_set_last_error(87); return 0; }

    wpf_lock();
    for (wpf_regmsg *r = g_regmsgs; r; r = r->next) {
        if (strcmp(r->name, utf8) == 0) {
            uint32_t id = r->id;
            pthread_mutex_unlock(&g_wpf.lock);
            return id;   // 命中已有登记：utf8 的所有权仍在调用方，不 free
        }
    }
    wpf_regmsg *r = (wpf_regmsg *)calloc(1, sizeof(wpf_regmsg));
    r->name = strdup(utf8);
    r->id = g_wpf.next_registered_msg++;
    if (g_wpf.next_registered_msg > 0xFFFF) g_wpf.next_registered_msg = 0xC000;  // 回绕（Win32 同样会失败，这里保守复用）
    r->next = g_regmsgs;
    g_regmsgs = r;
    uint32_t id = r->id;
    // M7c Phase 2：记下 HwndTarget 用来开"渲染闸门"的那条注册消息（见上面的长注释）。
    if (strcmp(utf8, "DisplayDevicesAvailabilityChanged") == 0)
        g_display_devices_msg = id;
    pthread_mutex_unlock(&g_wpf.lock);
    return id;
}

uint32_t RegisterWindowMessageA(const char *name) { return register_window_message_utf8(name); }
uint32_t RegisterWindowMessage(const char *name)  { return register_window_message_utf8(name); }
uint32_t RegisterWindowMessageW(const uint16_t *name)
{
    char *utf8 = wpf_utf16_to_utf8_dup(name);
    uint32_t r = register_window_message_utf8(utf8);
    free(utf8);
    return r;
}

// ── 消息附加信息 / 时间 / 位置 ──────────────────────────────────────────────
// GetMessageExtraInfo/SetMessageExtraInfo 在 Win32 里是**线程级**的。
// Dispatcher 用它保护 PeekMessage 期间的取值（Dispatcher.cs:2353），所以必须真实。
static __thread uintptr_t g_extra_info = 0;

uintptr_t GetMessageExtraInfo(void) { return g_extra_info; }
uintptr_t SetMessageExtraInfo(uintptr_t v)
{
    uintptr_t old = g_extra_info;
    g_extra_info = v;
    return old;
}

int GetMessagePos(void)
{
    wpf_thread *t = wpf_thread_self();
    wpf_lock();
    int v = ((t->last_pt_y & 0xFFFF) << 16) | (t->last_pt_x & 0xFFFF);
    pthread_mutex_unlock(&g_wpf.lock);
    return v;
}

int GetMessageTime(void)
{
    wpf_thread *t = wpf_thread_self();
    wpf_lock();
    int v = (int)t->last_msg_time;
    pthread_mutex_unlock(&g_wpf.lock);
    return v;
}

// 更新线程的「最后消息位置/时间」——由出队侧调用，语义与 Win32 一致
// （GetMessagePos 返回的是**最近一条被取出**的消息的位置）。
void wpf_note_message(const WPF_MSG *m)
{
    wpf_thread *t = wpf_thread_self();
    wpf_lock();
    t->last_pt_x = m->pt_x;
    t->last_pt_y = m->pt_y;
    t->last_msg_time = m->time;
    pthread_mutex_unlock(&g_wpf.lock);
}

DWORD GetTickCount(void)
{
    wpf_global_init();
    return (DWORD)(wpf_now_ms() & 0x7FFFFFFF);   // 不产生负值，Dispatcher 比大小安全
}
uint64_t GetTickCount64(void) { return wpf_now_ms(); }

BOOL QueryPerformanceCounter(int64_t *counter)
{
    if (!counter) return 0;
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    *counter = (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
    return 1;
}

BOOL QueryPerformanceFrequency(int64_t *freq)
{
    if (!freq) return 0;
    *freq = 1000000000LL;
    return 1;
}
