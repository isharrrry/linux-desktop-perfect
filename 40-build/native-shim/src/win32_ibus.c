// 【ACC-017 / K-089】IBus D-Bus 客户端 —— 让 WPF 能真正输入中文。
//
// 为什么是 D-Bus 而不是 XIM（K-087/K-089 实测）：
//   · XIM 在 ibus 上根本拿不到转换后的字：`0x408`(PreeditNothing) 下 ibus 把原始键透传
//     （我们收到的是 `n i h a o`，不是「你好」）；`0x402`(PreeditCallbacks) 带回调的
//     `XCreateIC` 三种参数组合全失败。Wine 的 `winex11.drv/xim.c` 也是"拿不到就降级"。
//   · GTK/GDK/WebKit 的库里 `XOpenIM`/`XCreateIC` 符号数都是 **0** —— 它们根本不走 XIM。
//   · 真正的输入法模块是 `gtk-3.0/3.0.0/immodules/im-ibus.so`，它依赖 `libdbus-1`。
//
// 关键发现（K-088/K-089）：`ibus-daemon` 会创建**自己的私有 D-Bus 地址**，
//   写在 `~/.config/ibus/bus/<machine-id>-unix-<display>` 的 `IBUS_ADDRESS=` 行里。
//   在**那台 bus** 上 `GetGlobalEngine` 返回 `libpinyin`、`CreateInputContext` 返回真实的
//   `/org/freedesktop/IBus/InputContext_N` —— 全部正常。
//   （K-041 说"所有方法 UnknownMethod"是因为当时连的是通用 `dbus-daemon --session`。）
//
// 本文件职责：
//   1. 定位并读取 ibus 的私有 bus 地址（`IBUS_ADDRESS` 环境变量优先，其次地址文件）；
//   2. 连上去，`CreateInputContext`，`FocusIn`；
//   3. 把 X 键事件转成 ibus 的 `ProcessKeyEvent`；
//   4. 监听 `CommitText` 信号 → 投 `WM_CHAR`（投递链见 K-079/K-082，已验证通畅）。
//
// 线程模型：本文件的 X 部分（`XOpenDisplay` 的借用）与事件泵同线程；
//   D-Bus 连接用 `dbus_connection_open_private`（**不**走共享连接），避免和 GTK 抢。
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <time.h>
#include <dirent.h>
#include <sys/stat.h>
#include "win32_internal.h"
#include <dbus/dbus.h>

// 由 win32_imm.c 提供：把 UTF-16 码元投成 WM_CHAR
extern void wpf_imm_post_char(uint16_t ch);

#define IBUS_SERVICE   "org.freedesktop.IBus"
#define IBUS_PATH      "/org/freedesktop/IBus"
#define IBUS_IFACE     "org.freedesktop.IBus"
#define IBUS_IC_IFACE  "org.freedesktop.IBus.InputContext"

static DBusHandlerResult ibus_filter(DBusConnection *conn, DBusMessage *msg, void *ud);  // 前置声明
static DBusConnection *g_conn = NULL;
static char g_ic_path[256];
static int  g_ibus_ok = 0;

static void ibus_diag(const char *fmt, ...)
{
    if (!getenv("WPF_LINUX_KEY_DIAG")) return;
    va_list ap; va_start(ap, fmt); vfprintf(stderr, fmt, ap); va_end(ap);
    fputc('\n', stderr); fflush(stderr);
}

// ── 读 ibus 私有 bus 地址 ───────────────────────────────────────────────────
// 顺序：$IBUS_ADDRESS → ~/.config/ibus/bus/<machine-id>-unix-<display> → ...-unix-0
static int read_ibus_address(char *out, size_t outsz)
{
    const char *env = getenv("IBUS_ADDRESS");
    if (env && *env) { snprintf(out, outsz, "%s", env); ibus_diag("[IBUS] 地址来自 $IBUS_ADDRESS"); return 1; }

    const char *home = getenv("HOME");
    if (!home) return 0;
    char dir[512]; snprintf(dir, sizeof(dir), "%s/.config/ibus/bus", home);

    // 按修改时间挑最新的（ibus 重启会更新它）
    char best[512] = {0};
    time_t best_t = 0;
    DIR *d = opendir(dir);
    if (d) {
        struct dirent *e;
        while ((e = readdir(d)) != NULL) {
            if (e->d_name[0] == '.') continue;
            char p[1024]; snprintf(p, sizeof(p), "%s/%s", dir, e->d_name);
            struct stat st;
            if (stat(p, &st) == 0 && st.st_mtime > best_t) { best_t = st.st_mtime; snprintf(best, sizeof(best), "%s", p); }
        }
        closedir(d);
    }
    if (!best[0]) { ibus_diag("[IBUS] 找不到地址文件（%s）", dir); return 0; }

    FILE *f = fopen(best, "r");
    if (!f) return 0;
    char line[1024]; int got = 0;
    while (fgets(line, sizeof(line), f)) {
        if (strncmp(line, "IBUS_ADDRESS=", 13) == 0) {
            char *v = line + 13; char *nl = strpbrk(v, "\r\n");
            if (nl) *nl = 0;
            snprintf(out, outsz, "%s", v);
            got = 1; break;
        }
    }
    fclose(f);
    if (got) ibus_diag("[IBUS] 地址来自 %s", best);
    return got;
}

// ── 发一个返回 object path 的调用（CreateInputContext）──────────────────────
static int ibus_call_create_ic(void)
{
    DBusMessage *m = dbus_message_new_method_call(IBUS_SERVICE, IBUS_PATH, IBUS_IFACE, "CreateInputContext");
    if (!m) return 0;
    const char *name = "wpf-linux";
    DBusMessageIter it;
    dbus_message_iter_init_append(m, &it);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &name);

    DBusError err; dbus_error_init(&err);
    DBusMessage *r = dbus_connection_send_with_reply_and_block(g_conn, m, 5000, &err);
    dbus_message_unref(m);
    if (!r) {
        ibus_diag("[IBUS] CreateInputContext 失败：%s", err.message ? err.message : "?");
        dbus_error_free(&err);
        return 0;
    }
    const char *path = NULL;
    if (dbus_message_get_args(r, &err, DBUS_TYPE_OBJECT_PATH, &path, DBUS_TYPE_INVALID)) {
        snprintf(g_ic_path, sizeof(g_ic_path), "%s", path ? path : "");
        ibus_diag("[IBUS] CreateInputContext -> %s", g_ic_path);
    } else {
        ibus_diag("[IBUS] CreateInputContext 返回解析失败：%s", err.message ? err.message : "?");
        dbus_error_free(&err);
    }
    dbus_message_unref(r);
    return g_ic_path[0] != 0;
}

static void ibus_simple_call(const char *iface, const char *method)
{
    DBusMessage *m = dbus_message_new_method_call(IBUS_SERVICE, g_ic_path, iface, method);
    if (!m) return;
    dbus_connection_send(g_conn, m, NULL);
    dbus_message_unref(m);
}

// ── 公开入口 ────────────────────────────────────────────────────────────────
int wpf_ibus_available(void) { return g_ibus_ok; }
const char *wpf_ibus_ic_path(void) { return g_ic_path; }

int wpf_ibus_init(void)
{
    if (g_conn) return g_ibus_ok ? 1 : 0;
    char addr[1024];
    if (!read_ibus_address(addr, sizeof(addr))) { ibus_diag("[IBUS] 无地址，放弃"); return 0; }

    DBusError err; dbus_error_init(&err);
    g_conn = dbus_connection_open_private(addr, &err);
    if (!g_conn) { ibus_diag("[IBUS] 连接失败：%s", err.message ? err.message : "?"); dbus_error_free(&err); return 0; }
    dbus_connection_set_exit_on_disconnect(g_conn, FALSE);
    if (!dbus_bus_register(g_conn, &err)) {
        // 注册失败不致命（我们只是客户端），继续
        ibus_diag("[IBUS] dbus_bus_register 失败（非致命）：%s", err.message ? err.message : "?");
        dbus_error_free(&err);
    }
    dbus_connection_add_filter(g_conn, ibus_filter, NULL, NULL);
    if (!ibus_call_create_ic()) { ibus_diag("[IBUS] 建 IC 失败"); return 0; }
    // 【ACC-017 / K-093】**照抄 im-ibus.so 的初始化序列**（用 LD_PRELOAD wrapper 观察 zenity 得到）：
    //   ① SetCapabilities(0x29) —— 0x29 = SURROUNDING_TEXT(0x20) | FOCUS(0x8) | PREEDIT_TEXT(0x1)
    //      不调它 ibus 就不知道客户端支持什么，`ProcessKeyEvent` 一律回 handled=0（K-092 的现象）。
    //   ② FocusIn
    //   ③ SetCursorLocation（每次都更新，让候选窗跟随光标）
    {
        DBusMessage *m = dbus_message_new_method_call(IBUS_SERVICE, g_ic_path, IBUS_IC_IFACE, "SetCapabilities");
        if (m) {
            dbus_uint32_t caps = 0x29u;
            DBusMessageIter it;
            dbus_message_iter_init_append(m, &it);
            dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &caps);
            dbus_connection_send(g_conn, m, NULL);
            dbus_message_unref(m);
            ibus_diag("[IBUS] SetCapabilities(0x29)");
        }
    }
    ibus_simple_call(IBUS_IC_IFACE, "FocusIn");
    ibus_diag("[IBUS] FocusIn");
    dbus_connection_flush(g_conn);
    g_ibus_ok = 1;
    ibus_diag("[IBUS] 就绪：ic=%s", g_ic_path);
    return 1;
}

// ── D-Bus 消息过滤器：接 CommitText ─────────────────────────────────────────
// 【ACC-017 / K-091】ibus 把最终上屏的字符串通过 `CommitText(STRING)` 发给我们。
//   这是**唯一**需要监听的方法 —— 预编辑（PreeditText/PreeditShowTable）可以先不管，
//   因为候选窗由 ibus 自己画（XIM 那条路已经证明它画得出来）。
static DBusHandlerResult ibus_filter(DBusConnection *conn, DBusMessage *msg, void *ud)
{
    (void)conn; (void)ud;
    // 【ACC-017 / K-094】**打印必须放在最前面**：原来放在 CommitText 分支之后，
    //   所以 CommitText 一旦命中就 return HANDLED，日志里根本看不到它 —— 差点因此误判"ibus 不发 CommitText"。
    if (getenv("WPF_LINUX_KEY_DIAG")) {
        const char *iface = dbus_message_get_interface(msg);
        const char *member = dbus_message_get_member(msg);
        if (member) { fprintf(stderr, "[IBUS] recv %s.%s\n", iface ? iface : "?", member); fflush(stderr); }
    }

    if (dbus_message_is_signal(msg, IBUS_IC_IFACE, "CommitText")) {
        // 【ACC-017 / K-094】ibus 的 `CommitText` 参数**不是**裸 STRING（实测 `get_args(STRING)` 失败）。
        //   类型可能是 VARIANT（`IBusText` 对象）或 STRUCT。这里把实际签名打出来，一次看清。
        if (getenv("WPF_LINUX_KEY_DIAG")) {
            DBusMessageIter it;
            if (dbus_message_iter_init(msg, &it)) {
                int t = dbus_message_iter_get_arg_type(&it);
                fprintf(stderr, "[IBUS] CommitText 参数类型='%c' (%d)\n", t ? t : '?', t); fflush(stderr);
                if (t == DBUS_TYPE_VARIANT) {
                    DBusMessageIter sub; dbus_message_iter_recurse(&it, &sub);
                    int st2 = dbus_message_iter_get_arg_type(&sub);
                    fprintf(stderr, "[IBUS]   variant 内层类型='%c' (%d)\n", st2 ? st2 : '?', st2); fflush(stderr);
                    if (st2 == DBUS_TYPE_STRING) {
                        const char *sv = NULL; dbus_message_iter_get_basic(&sub, &sv);
                        fprintf(stderr, "[IBUS]   variant string = \"%s\"\n", sv ? sv : "(null)"); fflush(stderr);
                    }
                } else if (t == DBUS_TYPE_STRING) {
                    const char *sv = NULL; dbus_message_iter_get_basic(&it, &sv);
                    fprintf(stderr, "[IBUS]   string = \"%s\"\n", sv ? sv : "(null)"); fflush(stderr);
                }
            } else {
                fprintf(stderr, "[IBUS] CommitText 无参数\n"); fflush(stderr);
            }
        }
        // 【ACC-017 / K-095】**参数是 VARIANT{STRUCT{string, ...}}（IBusText 对象）**，不是裸 STRING。
        //   实测：`get_args(STRING)` 失败，`iter` 显示 'v' → 'r'(STRUCT)。这里递归取第一个 string。
        const char *utf8 = NULL;
        {
            DBusMessageIter it, var, st2;
            if (dbus_message_iter_init(msg, &it) && dbus_message_iter_get_arg_type(&it) == DBUS_TYPE_VARIANT) {
                dbus_message_iter_recurse(&it, &var);
                if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_STRUCT) {
                    dbus_message_iter_recurse(&var, &st2);
                    if (dbus_message_iter_get_arg_type(&st2) == DBUS_TYPE_STRING) {
                        dbus_message_iter_get_basic(&st2, &utf8);
                    }
                } else if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_STRING) {
                    dbus_message_iter_get_basic(&var, &utf8);
                }
            }
        }
        if (utf8) {
            if (getenv("WPF_LINUX_KEY_DIAG")) {
                fprintf(stderr, "[IBUS] CommitText = \"%s\"\n", utf8); fflush(stderr);
            }
            // UTF-8 → UTF-16，代理对分两次投（同 win32_x11.c 的 commit 路径）
            const unsigned char *p2 = (const unsigned char *)utf8;
            int posted = 0;
            while (*p2) {
                uint32_t cp;
                if (*p2 < 0x80) cp = *p2++;
                else if ((*p2 & 0xE0) == 0xC0) { if (!p2[1]) break; cp = (uint32_t)(p2[0] & 0x1F) << 6 | (p2[1] & 0x3F); p2 += 2; }
                else if ((*p2 & 0xF0) == 0xE0) { if (!p2[1] || !p2[2]) break; cp = (uint32_t)(p2[0] & 0x0F) << 12 | (uint32_t)(p2[1] & 0x3F) << 6 | (p2[2] & 0x3F); p2 += 3; }
                else if ((*p2 & 0xF8) == 0xF0) { if (!p2[1] || !p2[2] || !p2[3]) break; cp = (uint32_t)(p2[0] & 0x07) << 18 | (uint32_t)(p2[1] & 0x3F) << 12 | (uint32_t)(p2[2] & 0x3F) << 6 | (p2[3] & 0x3F); p2 += 4; }
                else { p2++; continue; }
                if (cp <= 0xFFFF) { wpf_imm_post_char((uint16_t)cp); posted++; }
                else { cp -= 0x10000; wpf_imm_post_char((uint16_t)(0xD800 + (cp >> 10))); wpf_imm_post_char((uint16_t)(0xDC00 + (cp & 0x3FF))); posted += 2; }
            }
            if (posted) ibus_diag("[IBUS] 已投 %d 个码元到 WPF", posted);
        }
        return DBUS_HANDLER_RESULT_HANDLED;
    }
    return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
}

// ── 把 X 键事件转给 ibus ────────────────────────────────────────────────────
// ibus 的 `ProcessKeyEvent(keyval:u32, keycode:u32, state:u32)` 语义与 X 完全一致：
//   keyval 来自 `XLookupKeysym`，state 直接用 X 的修饰键位。
// 返回 1 表示 ibus **消费**了这个键（调用方不要再翻译成 WM_KEYDOWN）。
int wpf_ibus_process_key(unsigned int keyval, unsigned int keycode, unsigned int state, unsigned int *out_keyval)
{
    if (!g_ibus_ok || !g_conn || !g_ic_path[0]) { ibus_diag("[IBUS] process_key 跳过：ok=%d conn=%p ic=%s", g_ibus_ok, (void*)g_conn, g_ic_path); return 0; }
    DBusMessage *m = dbus_message_new_method_call(IBUS_SERVICE, g_ic_path, IBUS_IC_IFACE, "ProcessKeyEvent");
    if (!m) return 0;
    dbus_uint32_t kv = keyval, kc = keycode, st = state;
    DBusMessageIter it;
    dbus_message_iter_init_append(m, &it);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &kv);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &kc);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &st);

    DBusError err; dbus_error_init(&err);
    DBusMessage *r = dbus_connection_send_with_reply_and_block(g_conn, m, 1000, &err);
    dbus_message_unref(m);
    int handled = 0;
    if (!r) { if (err.message) { ibus_diag("[IBUS] ProcessKeyEvent 失败：%s", err.message); } dbus_error_free(&err); return 0; }
    dbus_bool_t h = FALSE;
    dbus_uint32_t got = 0;
    if (dbus_message_get_args(r, &err, DBUS_TYPE_BOOLEAN, &h, DBUS_TYPE_UINT32, &got, DBUS_TYPE_INVALID)) {
        handled = h ? 1 : 0;
        if (out_keyval) *out_keyval = got;
    } else { dbus_error_free(&err); }
    dbus_message_unref(r);
    if (getenv("WPF_LINUX_KEY_DIAG")) {
        fprintf(stderr, "[IBUS] ProcessKeyEvent(kv=0x%x kc=%u st=0x%x) -> handled=%d out=0x%x\n",
                keyval, keycode, state, handled, (unsigned)got); fflush(stderr);
    }
    return handled;
}

// ── 非阻塞轮询：在 X 事件泵每次循环里调一次 ─────────────────────────────────
// 【为什么不在事件泵里阻塞】D-Bus 的 `send_with_reply_and_block` 只在 ProcessKeyEvent
//   里用（有 1s 超时，且用户按键本身就是异步的），而**信号**必须靠这里轮询收，
//   一轮最多读几条消息，绝不阻塞 X 事件循环。
void wpf_ibus_pump(void)
{
    // 【ACC-017 / K-091】懒初始化：第一次 pump 时去连 ibus。
    //   放在这里而不是启动路径，是因为：`wpf_x11_ensure` 早于窗口创建，那时还没有 hwnd 可绑；
    //   而 pump 是事件泵每次循环都调的，窗口一定已经在了。
    static int tried = 0;
    if (!g_conn && !tried) { tried = 1; wpf_ibus_init(); }
    if (!g_conn) return;
    // 一次最多读 4 条，避免单次循环太久
    for (int i = 0; i < 4; ++i) {
        if (!dbus_connection_read_write(g_conn, 0)) return;   // 0ms = 非阻塞
        while (dbus_connection_dispatch(g_conn) == DBUS_DISPATCH_DATA_REMAINS) { }
        int st2 = dbus_connection_get_dispatch_status(g_conn);
        if (getenv("WPF_LINUX_KEY_DIAG") && st2 != DBUS_DISPATCH_COMPLETE) {
            fprintf(stderr, "[IBUS] pump dispatch_status=%d\n", st2); fflush(stderr);
        }
        if (st2 == DBUS_DISPATCH_COMPLETE) break;
    }
}
