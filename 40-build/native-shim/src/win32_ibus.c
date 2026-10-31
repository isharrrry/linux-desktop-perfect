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
    if (!ibus_call_create_ic()) { ibus_diag("[IBUS] 建 IC 失败"); return 0; }
    ibus_simple_call(IBUS_IC_IFACE, "FocusIn");
    dbus_connection_flush(g_conn);
    g_ibus_ok = 1;
    ibus_diag("[IBUS] 就绪：ic=%s", g_ic_path);
    return 1;
}
