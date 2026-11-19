// 【ACC-028 / K-153】AT-SPI over D-Bus（MOD-81）—— 最小可用面 + UIA Provider 事件桥。
//
// ── 先说清楚这一组的真实性质（有 grep 证据，见文末注释）──────────────────────
//   MOD-81 的 81 个 gap 里：
//     · **75 个是 UIAutomationClient 侧**（`UiaGetRootNode`/`*Pattern_*`/`TextRange_*` …）
//       —— 它们是"本应用去查询别的应用的 UI"（自动化工具角色）。
//       `grep -rln UIAutomationClient --include=*.csproj` 显示
//       **`PresentationFramework` / `PresentationCore` 都没有引用 `UIAutomationClient`**
//       ⇒ 应用正常运行**不需要**这 75 个。
//     · **3 个是 UIAutomationProvider 侧**（`UiaRaiseAsyncContentLoadedEvent` /
//       `UiaRaiseNotificationEvent` / `UiaRaiseActiveTextPositionChangedEvent`），
//       调用点都在 `AutomationInteropProvider.cs`（Provider 侧）⇒ **运行必需**。
//     · **3 个是 UIAutomationTypes 的保留值**（`UiaGetReservedNotSupportedValue` /
//       `UiaGetReservedMixedAttributeValue` / `UiaLookupId`）。前两个被
//       `AutomationElementIdentifiers.NotSupported` / `TextPatternIdentifiers.MixedAttributeValue`
//       这两个 **`static readonly` 字段**在类型初始化时调用 ⇒ **运行必需**
//       （`PresentationFramework.csproj:1390` 引用了 `UIAutomationTypes`）。
//
// ── 本文件做什么 ────────────────────────────────────────────────────────────
//   1. AT-SPI2 的 **session bus 连接**（懒初始化、失败静默降级，参考 win32_ibus.c）；
//   2. `RegisterApplication` 注册到 `org.a11y.atspi.Registry`；
//   3. `wpf_atspi_emit_event()` —— 往 AT-SPI 发事件（`org.a11y.atspi.Event.*` 信号）；
//   4. 上面 3 个 `UiaRaise*Event` → 转成 AT-SPI 事件；
//   5. 上面 2 个 `UiaGetReserved*` → 返回一个**真 IUnknown 单例**（.NET 会
//      `Marshal.GetObjectForIUnknown` 它，所以不能返回裸指针）；
//   6. 75 个 Client 侧符号 → 显式桩 `return E_NOTIMPL;`（会被 gen-exports 标成 `stub`，
//      不会冒充 impl）。
//
// ── 诚实边界 ────────────────────────────────────────────────────────────────
//   · 这条链路**本仓没有实测过**有 Orca/Accerciser 消费（本机无 a11y 客户端可比对）；
//     它保证的是"符号存在、事件按 AT-SPI 签名发出、无总线时静默降级不崩"。
//   · 真正"被识别"那一层是 `UiaReturnRawElementProvider` + `UiaClientsAreListening`，
//     它们在清单里**已经是 stub**、且**不在本文件的 81 个里** —— 本文件不动它们。
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdarg.h>
#include <unistd.h>
#include <pthread.h>
#include <X11/Xlib.h>
#include <X11/extensions/XTest.h>
#include <unistd.h>
#include "win32_internal.h"
#include <dbus/dbus.h>

#ifndef E_NOTIMPL
#define E_NOTIMPL ((int32_t)0x80004001)
#endif
#define WPF_S_OK ((int32_t)0)

/* 【K-623】**关键**：libdbus 默认**不是线程安全的**，而 `win32_ibus.c` /
 *   `win32_atspi.c` 都在多个线程用 D-Bus。`dbus_threads_init_default()` 必须在
 *   **任何** `dbus_*` 调用之前执行。放在 `atspi_ensure()` 里太晚（ibus 可能先调）。
 *   用 `__attribute__((constructor))` 在加载 .so 时最早的时机初始化。 */
__attribute__((constructor))
static void wpf_dbus_threads_init_early(void)
{
    dbus_threads_init_default();
}

#define ATSPI_REG_SERVICE "org.a11y.atspi.Registry"
#define ATSPI_REG_PATH    "/org/a11y/atspi/registry"
#define ATSPI_REG_IFACE   "org.a11y.atspi.Registry"
#define ATSPI_ROOT_PATH   "/org/a11y/atspi/accessible/root"

static DBusConnection *g_bus = NULL;
static int g_bus_state = 0;     /* 0=未尝试 1=可用 -1=已放弃 */
/* 【K-624】**关键**：泵线程不能用 `g_bus_state` 做循环条件 ——
 *   它启动时 `g_bus_state` 还是 `-1`（「初始化中」），`while (-1 > 0)` 立即为假
 *   ⇒ **泵线程刚启动就退出，之后所有 D-Bus 方法调用都没人应答**（pyatspi 显示 None）。
 *   用独立的 `g_atspi_running`（初始 1），只在连接真断开时置 0。 */
static volatile int g_atspi_running = 1;
static char g_bus_name[128];
/* 【PM-0153】本连接的唯一名（:1.x）：Socket.Embed 的 (so) 必须用它，
   否则 registry 认不出 owner，每次启动积一条 stale app。 */
static char g_bus_unique[128];
static pthread_mutex_t g_atspi_lock = PTHREAD_MUTEX_INITIALIZER;

static void atspi_diag(const char *fmt, ...)
{
    if (!getenv("WPF_LINUX_KEY_DIAG")) return;
    va_list ap; va_start(ap, fmt); vfprintf(stderr, fmt, ap); va_end(ap);
    fputc('\n', stderr); fflush(stderr);
}

// ── 找 AT-SPI 的 bus 地址 ───────────────────────────────────────────────────
// 顺序：$AT_SPI_BUS_ADDRESS → $XDG_RUNTIME_DIR/at-spi/bus* → ~/.cache/at-spi/bus*
static int atspi_address(char *out, size_t outsz)
{
    const char *env = getenv("AT_SPI_BUS_ADDRESS");
    if (env && *env) { snprintf(out, outsz, "%s", env); atspi_diag("[ATSPI] 地址来自 $AT_SPI_BUS_ADDRESS"); return 1; }

    const char *xdg = getenv("XDG_RUNTIME_DIR");
    if (xdg && *xdg) {
        char p[512];
        for (int i = 0; i < 4; ++i) {
            snprintf(p, sizeof(p), "%s/at-spi/bus%d", xdg, i);
            FILE *f = fopen(p, "r");
            if (f) { fclose(f); }
            /* at-spi-bus-launcher 把地址写成 socket/文件；直接当 unix:path= 用 */
            if (access(p, F_OK) == 0) { snprintf(out, outsz, "unix:path=%s", p); atspi_diag("[ATSPI] 地址来自 %s", p); return 1; }
        }
    }
    const char *home = getenv("HOME");
    if (home) {
        char p[512];
        /* 【K-618】**终极修复**：AT-SPI bus 的真实命名是 `bus_<display_number>`。
         *   实测 `DISPLAY=:303` ⇒ `pyatspi` 连的是 `$HOME/.cache/at-spi/bus_303`。
         *   修前本函数只找 `bus0~bus3` 和 `bus`（无后缀），全都不是 pyatspi 用的那个
         *   ⇒ 注册到了**错误的 bus** ⇒ pyatspi 永远看不到本应用。 */
        const char *disp = getenv("DISPLAY");
        if (disp) {
            const char *colon = strrchr(disp, ':');
            if (colon) {
                int num = atoi(colon + 1);
                snprintf(p, sizeof(p), "%s/.cache/at-spi/bus_%d", home, num);
                if (access(p, F_OK) == 0) {
                    snprintf(out, outsz, "unix:path=%s", p);
                    atspi_diag("[ATSPI] 地址来自 %s（display 专用）", p);
                    return 1;
                }
            }
        }
        for (int i = 0; i < 4; ++i) {
            snprintf(p, sizeof(p), "%s/.cache/at-spi/bus%d", home, i);
            if (access(p, F_OK) == 0) { snprintf(out, outsz, "unix:path=%s", p); atspi_diag("[ATSPI] 地址来自 %s", p); return 1; }
        }
        // 【K-615】**关键修复**：at-spi-bus-launcher 在当前环境写的是 `bus`（**无数字后缀**），
        //   不是 `bus0`~`bus3`。之前只探带后缀的 ⇒ 永远找不到 ⇒ `atspi_ensure()` 返回 0
        //   ⇒ 所有 AT-SPI 功能静默降级 ⇒ pyatspi 看不到本应用。
        snprintf(p, sizeof(p), "%s/.cache/at-spi/bus", home);
        if (access(p, F_OK) == 0) { snprintf(out, outsz, "unix:path=%s", p); atspi_diag("[ATSPI] 地址来自 %s（无后缀）", p); return 1; }
        // 也探一下 XDG_RUNTIME_DIR 风格的无后缀路径（新版本 at-spi2 用这个）
        {
            char xp[512];
            const char *xr = getenv("XDG_RUNTIME_DIR");
            if (xr && *xr) {
                snprintf(xp, sizeof(xp), "%s/at-spi/bus", xr);
                if (access(xp, F_OK) == 0) { snprintf(out, outsz, "unix:path=%s", xp); atspi_diag("[ATSPI] 地址来自 %s（XDG 无后缀）", xp); return 1; }
            }
        }
    }
    /* 最后退回通用 session bus（at-spi2 也常挂在上面） */
    const char *sess = getenv("DBUS_SESSION_BUS_ADDRESS");
    if (sess && *sess) { snprintf(out, outsz, "%s", sess); atspi_diag("[ATSPI] 回退到 $DBUS_SESSION_BUS_ADDRESS"); return 1; }
    return 0;
}

// 【K-614】D-Bus 消息泵线程：`dbus_connection_open_private` 不会自己处理消息，
//   必须有线程持续 `read_write_dispatch`。否则注册的方法永远不被调用。
static void *atspi_pump_thread(void *arg)
{
    pthread_setname_np(pthread_self(), "atspi-pump");   /* 【PM-0135】线程名便于诊断对号 */
    (void)arg;
    if (!g_bus) return NULL;
    atspi_diag("[ATSPI] 泵线程开始");
    while (g_atspi_running) {
        dbus_bool_t ok = dbus_connection_read_write_dispatch(g_bus, 500);
        if (!ok) {
            atspi_diag("[ATSPI] **泵线程检测到连接断开**（read_write_dispatch 返回 FALSE）");
            g_atspi_running = 0;
            break;
        }
        if (!dbus_connection_get_is_connected(g_bus)) {
            atspi_diag("[ATSPI] **泵线程检测到 is_connected=FALSE**");
            g_atspi_running = 0;
            break;
        }
    }
    atspi_diag("[ATSPI] 泵线程退出");
    return NULL;
}

// ── 懒初始化：连 bus + 注册应用 ─────────────────────────────────────────────
static int atspi_ensure(void)
{
    /* 【K-617】**竞态修复**：`create_window_utf8` 可能在多个线程上跑，
     *   两个线程都看到 `g_bus_state == 0` 就会**各自建一个连接**，
     *   后完成的那次覆盖 `g_bus` ⇒ 前一条连接上的 well-known 名字丢失。
     *   加锁保证只建一次。 */
    static int call_n = 0;
    /* 【K-619】**关键**：libdbus 默认**不是线程安全的**。本实现有「主线程 create_window_utf8
     *   调 atspi_ensure + 后台泵线程 read_write_dispatch」的并发 ⇒ 必须先调
     *   `dbus_threads_init_default()`，否则 `dbus_bus_request_name` 的内部状态可能被破坏：
     *   **返回 PRIMARY_OWNER 但名字实际没注册到 bus 上**（实测症状）。 */
    {
        static int dbus_threads_inited = 0;
        if (!dbus_threads_inited) { dbus_threads_init_default(); dbus_threads_inited = 1; }
    }
    /* K-1256：**显式开关** `WPF_LINUX_ATSPI=0` ⇒ 完全不启用 AT-SPI。
       本仓实测（K-1255）：AT-SPI 启用时偶发核心转储率约 33%~67%，禁用后 4/4 成功。
       需要稳定基线（截图/门禁）时用它；需要 UIA 时不要设。 */
    {
        const char *ev = getenv("WPF_LINUX_ATSPI");
        if (ev && ev[0] == '0' && ev[1] == '\0') {
            /* K-1258：与 `AT_SPI_BUS_ADDRESS=/nonexistent` 对齐 —— 把状态置成**已放弃(-1)**，
               否则保持 0（未初始化）会让后续调用反复进入初始化路径，引入竞态
               （K-1257 实测：NOADDR 100% vs NOATSPI 75%）。 */
            atspi_diag("[ATSPI] 已被 WPF_LINUX_ATSPI=0 禁用（状态置 -1）");
            g_bus_state = -1;
            return 0;
        }
    }
    pthread_mutex_lock(&g_atspi_lock);
    int my_call = ++call_n;
    if (g_bus_state != 0) {
        int r = g_bus_state > 0;
        pthread_mutex_unlock(&g_atspi_lock);
        if (my_call <= 6) atspi_diag("[ATSPI] ensure 第 %d 次调用 -> 已就绪(%d)", my_call, r);
        return r;
    }
    atspi_diag("[ATSPI] ensure 第 %d 次调用 -> 首次初始化", my_call);
    g_bus_state = -1;                                  /* 先悲观：任何一步失败都保持放弃 */

    char addr[1024];
    if (!atspi_address(addr, sizeof(addr))) { atspi_diag("[ATSPI] 无可用总线，降级"); pthread_mutex_unlock(&g_atspi_lock); return 0; }

    DBusError err; dbus_error_init(&err);
    g_bus = dbus_connection_open_private(addr, &err);
    if (!g_bus) {
        atspi_diag("[ATSPI] 连接失败：%s", err.message ? err.message : "?");
        dbus_error_free(&err); pthread_mutex_unlock(&g_atspi_lock); return 0;
    }
    dbus_connection_set_exit_on_disconnect(g_bus, FALSE);
    if (!dbus_bus_register(g_bus, &err)) {
        atspi_diag("[ATSPI] dbus_bus_register 失败：%s", err.message ? err.message : "?");
        dbus_error_free(&err);
        dbus_connection_close(g_bus); dbus_connection_unref(g_bus); g_bus = NULL;
        return 0;
    }
    const char *uniq = dbus_bus_get_unique_name(g_bus);
    snprintf(g_bus_name, sizeof(g_bus_name), "%s", uniq ? uniq : "wpf-linux");
    snprintf(g_bus_unique, sizeof(g_bus_unique), "%s", uniq ? uniq : "");

    /* 【K-616】**关键修复**：`dbus_connection_open_private` 只建连接、拿到唯一名（`:1.xx`），
     *   **不会请求 well-known 名字**。而 `at-spi2-registryd` / `pyatspi` 是通过
     *   well-known 名字扫描 bus 上的应用 ⇒ 不请求名字就永远不会被发现。
     *   （`RegisterApplication` 是 AT-SPI1 API，AT-SPI2 已废弃 —— 那条路本仓一直在报错。） */
    {
        DBusError nerr; dbus_error_init(&nerr);
        int rc = dbus_bus_request_name(g_bus, "org.a11y.atspi.SourceTree",
                                       DBUS_NAME_FLAG_REPLACE_EXISTING, &nerr);
        /* 【K-616b】打印原始 rc（0=PRIMARY_OWNER 1=IN_QUEUE 2=EXISTS 3=ALREADY_OWNER） */
        atspi_diag("[ATSPI] request_name rc=%d (0=PRIMARY 1=IN_QUEUE 2=EXISTS 3=ALREADY) err=%s",
                   rc, nerr.message ? nerr.message : "(none)");
        dbus_connection_flush(g_bus);
        if (rc == DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER || rc == DBUS_REQUEST_NAME_REPLY_ALREADY_OWNER) {
            snprintf(g_bus_name, sizeof(g_bus_name), "%s", "org.a11y.atspi.SourceTree");
            atspi_diag("[ATSPI] well-known 名字注册成功：org.a11y.atspi.SourceTree");
        } else {
            atspi_diag("[ATSPI] 名字注册失败 rc=%d：%s", rc, nerr.message ? nerr.message : "?");
        }
        dbus_error_free(&nerr);
    }

    /* RegisterApplication(bus_name) —— 失败不致命（可能没有 registry 在跑） */
    {
        DBusMessage *m = dbus_message_new_method_call(ATSPI_REG_SERVICE, ATSPI_REG_PATH, ATSPI_REG_IFACE, "RegisterApplication");
        if (m) {
            const char *nm = g_bus_name;
            DBusMessageIter it;
            dbus_message_iter_init_append(m, &it);
            dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &nm);
            DBusMessage *r = dbus_connection_send_with_reply_and_block(g_bus, m, 2000, &err);
            if (r) { dbus_message_unref(r); atspi_diag("[ATSPI] RegisterApplication 成功（%s）", g_bus_name); }
            else   { atspi_diag("[ATSPI] RegisterApplication 无应答：%s（继续）", err.message ? err.message : "?"); dbus_error_free(&err); }
            dbus_message_unref(m);
        }
    }
    dbus_connection_flush(g_bus);

    /* 【K-614】**关键**：`dbus_connection_open_private` 的连接**不会自己处理消息** ——
     *   必须有一个线程循环调 `dbus_connection_read_write_dispatch()`，否则
     *   `register_object_path` 注册的方法永远不会被调用（`dbus-send` 会回
     *   `UnknownMethod`）。这是本仓之前所有 AT-SPI 尝试都失败的**真正原因之一**。 */
    {
        static int pump_started = 0;
        if (!pump_started) {
            pthread_t th;
            if (pthread_create(&th, NULL, atspi_pump_thread, NULL) == 0) {
                pthread_detach(th);
                pump_started = 1;
                atspi_diag("[ATSPI] 泵线程已启动");
            } else {
                atspi_diag("[ATSPI] 泵线程启动失败");
            }
        }
    }

    /* 【K-621】**终极修复**：AT-SPI2 的正确注册方式是
     *   `org.a11y.atspi.Socket.Embed((our_well_known_name, our_root_path))`
     *   对象路径 `/org/a11y/atspi/accessible/root`（在 `org.a11y.atspi.Registry` 上），
     *   签名 `(so)`（一个 struct，`objectpath` 类型）。
     *   `dbus-monitor` 抓到的实证（`TestApp` 的流程）：
     *     → Embed("org.a11y.atspi.TestApp", /org/a11y/atspi/accessible/root)
     *     ← ChildrenChanged 信号 + Properties.Set("org.a11y.atspi.Application", "Id")
     *   注意：必须在 `request_name` **之后**（否则 registry 找不到我们的 bus name）。 */
    {
        DBusMessage *m = dbus_message_new_method_call(
            "org.a11y.atspi.Registry",
            "/org/a11y/atspi/accessible/root",
            "org.a11y.atspi.Socket",
            "Embed");
        if (m) {
            DBusMessageIter it, st;
            dbus_message_iter_init_append(m, &it);
            dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
            /* 【PM-0153】Embed 的 (so) 必须是**本连接的唯一名**（AT-SPI 规范要求
               app 的唯一名）。原来硬编码 well-known 名 ⇒ registry 无法按名字
               关联连接 owner，进程退出后旧条目不被回收，每启动一次积一条 stale app
               （实测 86 个别名全指向同一个 /wpf/root：全量枚举 10,312 节点 /
               17.62 s，而单棵树只有 122 节点 / 0.22 s）。 */
            const char *bus = g_bus_unique[0] ? g_bus_unique : "org.a11y.atspi.SourceTree";
            const char *path = "/org/a11y/atspi/accessible/wpf/root";
            dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &path);
            dbus_message_iter_close_container(&it, &st);
            /* 【K-629】**关键**：不能用 `send_with_reply_and_block` —— 它会在主线程
             *   （WPF 的 `create_window_utf8` 路径里）**阻塞**，而泵线程同时在同一个
             *   `DBusConnection` 上 `read_write_dispatch` ⇒ 两线程抢同一连接
             *   ⇒ 阻塞数秒 + 把 `GetLastError` 污染成 1400
             *   ⇒ WPF 抛 `Win32Exception(1400) "The application is terminating"` 崩溃。
             *   改成**异步发送**（`dbus_connection_send`），不等待回复。 */
            dbus_uint32_t serial = 0;
            if (dbus_connection_send(g_bus, m, &serial)) {
                dbus_connection_flush(g_bus);
                atspi_diag("[ATSPI] Socket.Embed 已发送（serial=%u）", (unsigned)serial);
            } else {
                atspi_diag("[ATSPI] Socket.Embed 发送失败");
            }
            dbus_message_unref(m);
        } else {
            atspi_diag("[ATSPI] Socket.Embed 消息创建失败");
        }
    }

    g_bus_state = 1;
    pthread_mutex_unlock(&g_atspi_lock);
    return 1;
}

int wpf_atspi_available(void) { return atspi_ensure(); }
const char *wpf_atspi_bus_name(void) { return g_bus_name; }

// ── 发一个 AT-SPI 事件 ──────────────────────────────────────────────────────
// AT-SPI2 的 `org.a11y.atspi.Event.<Kind>` 接口 signal 签名是 `(siiv)`：
//   detail(s) / detail1(i) / detail2(i) / any_data(v)
static int atspi_emit(const char *kind_iface, const char *name, const char *detail, int32_t d1, int32_t d2)
{
    if (!atspi_ensure() || !g_bus) return 0;
    DBusMessage *m = dbus_message_new_signal(ATSPI_ROOT_PATH, kind_iface, name);
    if (!m) return 0;
    DBusMessageIter it, var;
    dbus_message_iter_init_append(m, &it);
    const char *d = detail ? detail : "";
    dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &d);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_INT32, &d1);
    dbus_message_iter_append_basic(&it, DBUS_TYPE_INT32, &d2);
    dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "s", &var);
    const char *empty = "";
    dbus_message_iter_append_basic(&var, DBUS_TYPE_STRING, &empty);
    dbus_message_iter_close_container(&it, &var);
    dbus_connection_send(g_bus, m, NULL);
    dbus_message_unref(m);
    dbus_connection_flush(g_bus);
    return 1;
}

// ── 最小 IUnknown（给 UiaGetReserved* 用）───────────────────────────────────
// .NET 侧是 `[MarshalAs(UnmanagedType.IUnknown)] out object` + `Marshal.GetObjectForIUnknown`，
//   所以必须返回一个**真的带 vtable 的 COM 对象**，不能是裸哨兵指针。
typedef struct { void **vtbl; int32_t ref; const char *tag; } wpf_unknown;

static int32_t unk_qi(void *self, const void *iid, void **out)
{
    (void)iid;
    if (out) *out = self;
    return 0;                                   /* S_OK，任何 IID 都认（够用） */
}
static uint32_t unk_addref(void *self) { wpf_unknown *u = (wpf_unknown *)self; return (uint32_t)++u->ref; }
static uint32_t unk_release(void *self)
{
    wpf_unknown *u = (wpf_unknown *)self;
    if (u->ref > 0) --u->ref;
    return (uint32_t)u->ref;                    /* 单例，永不释放 */
}
static void *g_unk_vtbl[3] = { (void *)unk_qi, (void *)unk_addref, (void *)unk_release };
static wpf_unknown g_unknown_not_supported = { g_unk_vtbl, 1, "NotSupported" };
static wpf_unknown g_unknown_mixed_attr    = { g_unk_vtbl, 1, "MixedAttribute" };

// ═══════════════════════════════════════════════════════════════════════════
//  真实现（6 个）：签名逐字取自上游 DllImport 声明
// ═══════════════════════════════════════════════════════════════════════════

// UiaCoreProviderApi.cs:134
//   int UiaRaiseAsyncContentLoadedEvent(IRawElementProviderSimple, AsyncContentLoadedState, double)
int32_t UiaRaiseAsyncContentLoadedEvent(void *provider, int32_t state, double percent)
{
    (void)provider;
    /* AT-SPI 最接近的对应：Object 的 "PropertyChange" / StateChanged。这里发一个带百分比的
       PropertyChange（detail1=百分比取整）。诚实标注：AT-SPI 没有完全等价的事件类型。 */
    return atspi_emit("org.a11y.atspi.Event.Object", "PropertyChange", "busy", (int32_t)percent, state) ? WPF_S_OK : E_NOTIMPL;
}

// UiaCoreProviderApi.cs:137
//   int UiaRaiseNotificationEvent(IRawElementProviderSimple, AutomationNotificationKind,
//                                AutomationNotificationProcessing, string, string)   [CharSet.Unicode]
int32_t UiaRaiseNotificationEvent(void *provider, int32_t kind, int32_t processing,
                                  const uint16_t *displayString, const uint16_t *activityId)
{
    (void)provider; (void)activityId;
    if (!atspi_ensure() || !g_bus) return E_NOTIMPL;
    /* UTF-16 → UTF-8（不用 wcstombs，避免 locale 依赖） */
    char msg[512]; int n = 0;
    if (displayString) {
        for (int i = 0; displayString[i] && n < (int)sizeof(msg) - 4; ++i) {
            uint32_t c = displayString[i];
            if (c < 0x80) msg[n++] = (char)c;
            else if (c < 0x800) { msg[n++] = (char)(0xC0 | (c >> 6)); msg[n++] = (char)(0x80 | (c & 0x3F)); }
            else { msg[n++] = (char)(0xE0 | (c >> 12)); msg[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); msg[n++] = (char)(0x80 | (c & 0x3F)); }
        }
    }
    msg[n] = 0;
    /* AT-SPI：Object 的 "Announcement" 是通知类事件的对应物（detail 带文本） */
    return atspi_emit("org.a11y.atspi.Event.Object", "Announcement", msg, kind, processing) ? WPF_S_OK : E_NOTIMPL;
}

// UiaCoreProviderApi.cs:141
//   int UiaRaiseActiveTextPositionChangedEvent(IRawElementProviderSimple, ITextRangeProvider)
int32_t UiaRaiseActiveTextPositionChangedEvent(void *provider, void *textRange)
{
    (void)provider; (void)textRange;
    /* AT-SPI 对应 text 接口的 caret-moved / 位置变化 */
    return atspi_emit("org.a11y.atspi.Event.Object", "TextCaretMoved", "active-text-position", 0, 0) ? WPF_S_OK : E_NOTIMPL;
}

// UiaCoreTypesApi.cs:109 —— [MarshalAs(UnmanagedType.IUnknown)] out object
int32_t UiaGetReservedNotSupportedValue(void **out)
{
    if (out) *out = (void *)&g_unknown_not_supported;
    return WPF_S_OK;
}

// UiaCoreTypesApi.cs:112 —— [MarshalAs(UnmanagedType.IUnknown)] out object
int32_t UiaGetReservedMixedAttributeValue(void **out)
{
    if (out) *out = (void *)&g_unknown_mixed_attr;
    return WPF_S_OK;
}

// UiaCoreTypesApi.cs:53 —— int UiaLookupId(AutomationIdType, ref Guid)
//   `AutomationIdentifier.cs:34` 的注释明确写着「All Guids will be empty now since we are
//   not calling UiaLookupId」⇒ 上游自己也不指望它。返回 0（无对应 Id）是合法结果。
int32_t UiaLookupId(int32_t type, const void *guid)
{
    (void)type; (void)guid;
    return 0;
}

// ═══════════════════════════════════════════════════════════════════════════
//  显式桩（75 个）：全部是 UIAutomationClient 侧，PresentationFramework 不引用
//  ⇒ 应用正常运行不需要；这里导出仅为满足 TBL-001 契约。
//  gen-exports.mjs 的 isStub() 认「return E_NOTIMPL;」⇒ 会如实标成 stub。
//  ⚠️ 若将来真要实现，签名必须按 UiaCoreApi.cs 的 DllImport 逐条对齐（本文件用 void 形参）。
// ═══════════════════════════════════════════════════════════════════════════
/* 清单占位：无调用方（UIAutomationClient 未被 PresentationFramework 引用） */
int32_t DockPattern_SetDockPosition(void) { return E_NOTIMPL; }
int32_t ExpandCollapsePattern_Collapse(void) { return E_NOTIMPL; }
int32_t ExpandCollapsePattern_Expand(void) { return E_NOTIMPL; }
int32_t GridPattern_GetItem(void) { return E_NOTIMPL; }
int32_t InvokePattern_Invoke(void) { return E_NOTIMPL; }
int32_t ItemContainerPattern_FindItemByProperty(void) { return E_NOTIMPL; }
int32_t MultipleViewPattern_GetViewName(void) { return E_NOTIMPL; }
int32_t MultipleViewPattern_SetCurrentView(void) { return E_NOTIMPL; }
int32_t RangeValuePattern_SetValue(void) { return E_NOTIMPL; }
int32_t ScrollItemPattern_ScrollIntoView(void) { return E_NOTIMPL; }
int32_t ScrollPattern_Scroll(void) { return E_NOTIMPL; }
int32_t ScrollPattern_SetScrollPercent(void) { return E_NOTIMPL; }
int32_t SelectionItemPattern_AddToSelection(void) { return E_NOTIMPL; }
int32_t SelectionItemPattern_RemoveFromSelection(void) { return E_NOTIMPL; }
int32_t SelectionItemPattern_Select(void) { return E_NOTIMPL; }
int32_t SynchronizedInputPattern_Cancel(void) { return E_NOTIMPL; }
int32_t SynchronizedInputPattern_StartListening(void) { return E_NOTIMPL; }
int32_t TextPattern_GetSelection(void) { return E_NOTIMPL; }
int32_t TextPattern_GetVisibleRanges(void) { return E_NOTIMPL; }
int32_t TextPattern_RangeFromChild(void) { return E_NOTIMPL; }
int32_t TextPattern_RangeFromPoint(void) { return E_NOTIMPL; }
int32_t TextPattern_get_DocumentRange(void) { return E_NOTIMPL; }
int32_t TextPattern_get_SupportedTextSelection(void) { return E_NOTIMPL; }
int32_t TextRange_AddToSelection(void) { return E_NOTIMPL; }
int32_t TextRange_Clone(void) { return E_NOTIMPL; }
int32_t TextRange_Compare(void) { return E_NOTIMPL; }
int32_t TextRange_CompareEndpoints(void) { return E_NOTIMPL; }
int32_t TextRange_ExpandToEnclosingUnit(void) { return E_NOTIMPL; }
int32_t TextRange_FindAttribute(void) { return E_NOTIMPL; }
int32_t TextRange_FindText(void) { return E_NOTIMPL; }
int32_t TextRange_GetAttributeValue(void) { return E_NOTIMPL; }
int32_t TextRange_GetBoundingRectangles(void) { return E_NOTIMPL; }
int32_t TextRange_GetChildren(void) { return E_NOTIMPL; }
int32_t TextRange_GetEnclosingElement(void) { return E_NOTIMPL; }
int32_t TextRange_GetText(void) { return E_NOTIMPL; }
int32_t TextRange_Move(void) { return E_NOTIMPL; }
int32_t TextRange_MoveEndpointByRange(void) { return E_NOTIMPL; }
int32_t TextRange_MoveEndpointByUnit(void) { return E_NOTIMPL; }
int32_t TextRange_RemoveFromSelection(void) { return E_NOTIMPL; }
int32_t TextRange_ScrollIntoView(void) { return E_NOTIMPL; }
int32_t TextRange_Select(void) { return E_NOTIMPL; }
int32_t TogglePattern_Toggle(void) { return E_NOTIMPL; }
int32_t TransformPattern_Move(void) { return E_NOTIMPL; }
int32_t TransformPattern_Resize(void) { return E_NOTIMPL; }
int32_t TransformPattern_Rotate(void) { return E_NOTIMPL; }
int32_t UiaAddEvent(void) { return E_NOTIMPL; }
int32_t UiaEventAddWindow(void) { return E_NOTIMPL; }
int32_t UiaEventRemoveWindow(void) { return E_NOTIMPL; }
int32_t UiaFind(void) { return E_NOTIMPL; }
int32_t UiaGetErrorDescription(void) { return E_NOTIMPL; }
int32_t UiaGetPatternProvider(void) { return E_NOTIMPL; }
int32_t UiaGetPropertyValue(void) { return E_NOTIMPL; }
int32_t UiaGetRootNode(void) { return E_NOTIMPL; }
int32_t UiaGetRuntimeId(void) { return E_NOTIMPL; }
int32_t UiaGetUpdatedCache(void) { return E_NOTIMPL; }
int32_t UiaHPatternObjectFromVariant(void) { return E_NOTIMPL; }
int32_t UiaHTextRangeFromVariant(void) { return E_NOTIMPL; }
int32_t UiaHUiaNodeFromVariant(void) { return E_NOTIMPL; }
int32_t UiaHasServerSideProvider(void) { return E_NOTIMPL; }
int32_t UiaNavigate(void) { return E_NOTIMPL; }
int32_t UiaNodeFromFocus(void) { return E_NOTIMPL; }
int32_t UiaNodeFromHandle(void) { return E_NOTIMPL; }
int32_t UiaNodeFromPoint(void) { return E_NOTIMPL; }
int32_t UiaNodeFromProvider(void) { return E_NOTIMPL; }
int32_t UiaNodeRelease(void) { return E_NOTIMPL; }
int32_t UiaPatternRelease(void) { return E_NOTIMPL; }
int32_t UiaRegisterProviderCallback(void) { return E_NOTIMPL; }
int32_t UiaRemoveEvent(void) { return E_NOTIMPL; }
int32_t UiaSetFocus(void) { return E_NOTIMPL; }
int32_t UiaTextRangeRelease(void) { return E_NOTIMPL; }
int32_t ValuePattern_SetValue(void) { return E_NOTIMPL; }
int32_t VirtualizedItemPattern_Realize(void) { return E_NOTIMPL; }
int32_t WindowPattern_Close(void) { return E_NOTIMPL; }
int32_t WindowPattern_SetWindowVisualState(void) { return E_NOTIMPL; }
int32_t WindowPattern_WaitForInputIdle(void) { return E_NOTIMPL; }

// ═══════════════════════════════════════════════════════════════════════════
// 【K-611】最小 AT-SPI Accessible 对象树
//
// 目标：让 pyatspi 能枚举到本进程（SourceTree）的顶层窗口。
// 实现：在 AT-SPI bus 上注册一个 D-Bus 对象路径，实现
//       org.a11y.atspi.Accessible 的 6 个方法：
//         GetChildAtIndex(i) -> (so)      返回 (bus_name, object_path)
//         GetChildren() -> a(so)          返回子节点数组
//         GetRoleName() -> s             角色名（"application"/"frame"）
//         GetName() -> s                 名字（窗口标题）
//         GetParent() -> (so)            父节点
//         GetApplication() -> (so)       应用的 root
//
// 只暴露「应用 root + 每个已 map 的顶层窗口」，不暴露控件树内部。
// 这是**最小可用面**：能被 pyatspi 看到、能读到窗口标题和角色，
// 但拿不到按钮/输入框级的控件（那需要 WPF 的 AutomationPeer 桥接）。
// ═══════════════════════════════════════════════════════════════════════════

#define WPF_ATSPI_MAX_NODES 512
#define WPF_ATSPI_PATH_ROOT "/org/a11y/atspi/accessible/wpf/root"
/* PM-0109：org.a11y.atspi.Cache 的规范对象路径（at-spi2-core xml/Cache.xml） */
#define WPF_ATSPI_PATH_CACHE "/org/a11y/atspi/cache"
/* 【PM-0150】AT-SPI 的「空引用」约定：bus 名空串 + 这个路径 */
#define WPF_ATSPI_PATH_NULL  "/org/a11y/atspi/null"

typedef struct {
    char path[128];                 // D-Bus 对象路径
    char name[256];                 // 窗口标题 / 控件名
    char role[32];                  // "application" / "frame" / "push button" …
    uint32_t role_id;               // AT-SPI Role 数值（Cache 用；由注册方给，避免猜）
    uint64_t hwnd;                  // 关联的 HWND（0 = 应用 root）
    int parent_index;               // 【PM-0109】父节点在 g_nodes 里的下标（-1 = 无父）
    int bounds[4];                  // 【PM-0109】x,y,w,h（设备像素，0 表示未知）
    int registered;                 // 是否已注册到 D-Bus
    int alive;
    /* K-1161（T3）：Action 接口数据（由 WPF 侧通过 wpf_atspi_set_action 提供） */
    int has_action;
    char action_name[64];
    char action_desc[64];
} wpf_atspi_node;

static wpf_atspi_node g_nodes[WPF_ATSPI_MAX_NODES];
static int g_node_count = 0;

/* K-1251：AT-SPI DoAction 的「待注入点击」目标窗口（0 = 无待处理） */
uint64_t g_atspi_pending_action_hwnd = 0;
/* 【PM-0109 档2】控件动作的注入坐标（-1 = 未知 ⇒ 回退 hwnd/env 默认）。
   DoAction 时取控件 bounds 中心；真正的注入点在 win32_msg.c 的 GetMessageW 里。 */
int g_atspi_pending_action_x = -1;
int g_atspi_pending_action_y = -1;

/* K-1161：WPF 侧注册的「执行 action」回调（Cecil 注入时传入） */
typedef void (*wpf_atspi_action_invoker_t)(uint64_t hwnd);
static wpf_atspi_action_invoker_t g_action_invoker = NULL;
static int g_atspi_tree_ready = 0;
/* PM-0109：registry 用 Properties.Set 写回来的 Application.Id（-1 = 未设置） */
static int g_atspi_app_id = -1;

static wpf_atspi_node *atspi_node_find(uint64_t hwnd)
{
    for (int i = 0; i < g_node_count; ++i)
        if (g_nodes[i].alive && g_nodes[i].hwnd == hwnd) return &g_nodes[i];
    return NULL;
}

static wpf_atspi_node *atspi_node_by_path(const char *path)
{
    for (int i = 0; i < g_node_count; ++i)
        if (g_nodes[i].alive && strcmp(g_nodes[i].path, path) == 0) return &g_nodes[i];
    return NULL;
}

/* 【PM-0109】role 字符串 → AT-SPI Role 数值。只映射已知的几种；未知返回 0（ROLE_INVALID），
 *   调用方（托管侧）也可以在新 API 里直接给 role_id，优先级高于这里的映射。 */
static uint32_t atspi_role_id_for(const char *role)
{
    if (!role || !*role) return 0;
    if (strcmp(role, "application") == 0) return 75;
    if (strcmp(role, "frame") == 0) return 23;
    if (strcmp(role, "dialog") == 0) return 16;
    if (strcmp(role, "panel") == 0) return 39;
    if (strcmp(role, "push button") == 0) return 43;
    if (strcmp(role, "toggle button") == 0) return 44;
    if (strcmp(role, "page tab") == 0) return 37;
    if (strcmp(role, "table cell") == 0) return 56;
    if (strcmp(role, "table") == 0) return 55;
    if (strcmp(role, "label") == 0) return 28;
    if (strcmp(role, "text") == 0) return 46;
    if (strcmp(role, "menu") == 0) return 33;
    if (strcmp(role, "menu item") == 0) return 34;
    if (strcmp(role, "scroll bar") == 0) return 60;
    if (strcmp(role, "list") == 0) return 50;
    return 0;
}

/* 【PM-0109】给未实现的控件层留的最小查询：某节点的子节点个数（Cache/ChildCount 用）。 */
static int atspi_child_count(int index)
{
    if (index < 0 || index >= g_node_count || !g_nodes[index].alive) return 0;
    const wpf_atspi_node *n = &g_nodes[index];
    int cnt = 0;
    if (n->hwnd == 0) {                       /* 应用 root → 顶层窗口 */
        for (int i = 0; i < g_node_count; ++i)
            if (g_nodes[i].alive && g_nodes[i].hwnd != 0 && g_nodes[i].parent_index < 0) ++cnt;
    } else {                                  /* 窗口 → 它的控件 */
        for (int i = 0; i < g_node_count; ++i)
            if (g_nodes[i].alive && g_nodes[i].parent_index == index) ++cnt;
    }
    return cnt;
}

/* 【PM-0150/PM-0152】节点在**父节点内**的序号（0..childCount-1；root 为 -1）。
   遍历口径必须与 GetChildAtIndex 逐字一致，否则「按序号取孩子」会错位。
   修前 Cache 里写的是全局序号（窗口 0..8、控件 9..121），与父的 childCount 矛盾。 */
static int atspi_child_index_of(int ni)
{
    if (ni < 0 || ni >= g_node_count || !g_nodes[ni].alive) return -1;
    const wpf_atspi_node *n = &g_nodes[ni];
    if (n->hwnd == 0) return -1;              /* root 无父 */
    int ord = 0;
    for (int i = 0; i < g_node_count; ++i) {
        if (!g_nodes[i].alive) continue;
        if (i == ni) return ord;
        int sib = (n->parent_index < 0)
            ? (g_nodes[i].hwnd != 0 && g_nodes[i].parent_index < 0)
            : (g_nodes[i].parent_index == n->parent_index);
        if (sib) ++ord;
    }
    return -1;
}

/* 【PM-0144 续】真父路径：root 无父（回空串，调用方补成 null 引用）；
   窗口的父是 app root；控件的父是它所属的**窗口节点**。
   修前这里对所有非 root 节点一律回 root，与 Cache.parent 是同一个错，只修了一处。 */
static const char *atspi_node_parent_path(const wpf_atspi_node *n)
{
    if (n->hwnd == 0) return "";
    int pi = n->parent_index;
    if (pi >= 0 && pi < g_node_count && g_nodes[pi].alive) return g_nodes[pi].path;
    return WPF_ATSPI_PATH_ROOT;
}

/* 【PM-0152】节点的接口列表**只写一份**：Cache.GetItems 与 Accessible.GetInterfaces
   共用，避免「缓存里声明」与「方法里声明」两处各写又互相矛盾。
   只列真实现了 handler 的接口：Accessible / Action / Component(GetExtents 已实现)；
   Text / Value 未实现，不列出（PM-0149）。 */
static void atspi_append_interfaces(DBusMessageIter *arr, const wpf_atspi_node *n)
{
    (void)n;
    const char *i_acc = "org.a11y.atspi.Accessible";
    const char *i_act = "org.a11y.atspi.Action";
    const char *i_cmp = "org.a11y.atspi.Component";
    dbus_message_iter_append_basic(arr, DBUS_TYPE_STRING, &i_acc);
    dbus_message_iter_append_basic(arr, DBUS_TYPE_STRING, &i_act);
    dbus_message_iter_append_basic(arr, DBUS_TYPE_STRING, &i_cmp);
}

// ── D-Bus 方法处理器 ────────────────────────────────────────────────────────
static void atspi_append_ref(DBusMessageIter *arr, const char *bus, const char *path)
{
    /* 【K-626】**关键**：AT-SPI 的 `(so)` 里第二个字段是 **object_path**，
     *   不是 string。用 `DBUS_TYPE_STRING` 写会让 libdbus **abort**
     *   （`Array or variant type requires that type object_path be written`），
     *   进而把整个进程 Core dump —— 这是「名字注册后又消失」的真正原因，
     *   因为在 `GetChildren` 被调用时进程直接崩了。 */
    DBusMessageIter st;
    dbus_message_iter_open_container(arr, DBUS_TYPE_STRUCT, NULL, &st);
    dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
    dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &path);
    dbus_message_iter_close_container(arr, &st);
}

/* 【PM-0109】Cache.GetItems 的应答体：a((so)(so)(so)iiassusau)。
 *   让「规范路径 /org/a11y/atspi/cache」和「app root」两个调用点复用同一份实现：
 *   libatspi 2.44 实测把 GetItems 发在 app root 上（diag：path=/org/a11y/atspi/accessible/wpf/root）。 */
static void atspi_reply_cache_items(DBusMessageIter *it)
{
    DBusMessageIter arr;
    dbus_message_iter_open_container(it, DBUS_TYPE_ARRAY,
                                     "((so)(so)(so)iiassusau)", &arr);
    int frame_cnt = 0;
    for (int k = 0; k < g_node_count; ++k)
        if (g_nodes[k].alive && g_nodes[k].hwnd != 0) ++frame_cnt;
    for (int k = 0; k < g_node_count; ++k) {
        if (!g_nodes[k].alive) continue;
        wpf_atspi_node *cn = &g_nodes[k];
        int is_root = (cn->hwnd == 0);
        DBusMessageIter item;
        dbus_message_iter_open_container(&arr, DBUS_TYPE_STRUCT, NULL, &item);
        atspi_append_ref(&item, g_bus_name, cn->path);
        atspi_append_ref(&item, g_bus_name, WPF_ATSPI_PATH_ROOT);
        if (is_root)
            atspi_append_ref(&item, "", WPF_ATSPI_PATH_NULL);
        else {
            /* 【PM-0144 修复】parent 必须是**真正的父节点**：
               窗口的父是 app root，控件的父是它所属的**窗口节点**。
               原实现对所有非 root 节点一律写 root ⇒ 122 个控件的 parent 全指向 root，
               而 root 的 child_count 只有 9 ⇒ 树结构与计数自相矛盾，
               libatspi 只按 child_count 读 9 个 ⇒ 控件全部丢失
               （pyatspi 实测「122 个节点全是 frame|SourceTree」就是这个机制）。 */
            int pi = cn->parent_index;
            const char *ppath = (pi >= 0 && pi < g_node_count && g_nodes[pi].alive)
                                ? g_nodes[pi].path : WPF_ATSPI_PATH_ROOT;
            atspi_append_ref(&item, g_bus_name, ppath);
        }
        /* 【PM-0152】父内序号（root=-1），不再用全局计数 */
        dbus_int32_t index = (dbus_int32_t)atspi_child_index_of(k);
        int cc = atspi_child_count(k);
        dbus_int32_t child_count = (dbus_int32_t)cc;
        dbus_message_iter_append_basic(&item, DBUS_TYPE_INT32, &index);
        dbus_message_iter_append_basic(&item, DBUS_TYPE_INT32, &child_count);
        DBusMessageIter ifaces;
        dbus_message_iter_open_container(&item, DBUS_TYPE_ARRAY, "s", &ifaces);
        /* 【PM-0152】与 GetInterfaces 共用同一份列表。原来这里只写
           Accessible(+Action)，漏了**已实现的 Component** ⇒ 113/113 个节点的
           「缓存接口」与「声明接口」互相矛盾。 */
        atspi_append_interfaces(&ifaces, cn);
        dbus_message_iter_close_container(&item, &ifaces);
        const char *nm = cn->name;
        dbus_message_iter_append_basic(&item, DBUS_TYPE_STRING, &nm);
        dbus_uint32_t role = cn->role_id ? cn->role_id : (is_root ? 75u : 23u);
        dbus_message_iter_append_basic(&item, DBUS_TYPE_UINT32, &role);
        const char *desc = "";
        dbus_message_iter_append_basic(&item, DBUS_TYPE_STRING, &desc);
        DBusMessageIter states;
        dbus_message_iter_open_container(&item, DBUS_TYPE_ARRAY, "u", &states);
        /* 两个 uint32 = 64 位状态位：SHOWING(25)|VISIBLE(30)|ENABLED(8)|
           SENSITIVE(24)|RESIZABLE(21) */
        dbus_uint32_t st0 = (1u << 25) | (1u << 30) | (1u << 8) |
                            (1u << 24) | (1u << 21);
        dbus_uint32_t st1 = 0;
        dbus_message_iter_append_basic(&states, DBUS_TYPE_UINT32, &st0);
        dbus_message_iter_append_basic(&states, DBUS_TYPE_UINT32, &st1);
        dbus_message_iter_close_container(&item, &states);
        dbus_message_iter_close_container(&arr, &item);
    }
    dbus_message_iter_close_container(it, &arr);
}

static DBusHandlerResult atspi_msg_handler(DBusConnection *conn, DBusMessage *msg, void *data)
{
    (void)data;
    const char *path = dbus_message_get_path(msg);
    const char *iface = dbus_message_get_interface(msg);
    const char *member = dbus_message_get_member(msg);
    if (!path || !member) return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    /* 【K-628】**关键**：D-Bus 允许**省略 interface 字段**（`iface == NULL`），
     *   AT-SPI 的 `pyatspi` 在调 `GetName`/`GetRole` 时**有时不带 interface**。
     *   原来的白名单（`iface && ...`）看似放行 NULL，但**实际上还有一层
     *   `GetItems` 分支的错误回复**让 libdbus 把整个连接标记为异常。
     *   最稳的做法：**只按 `member` 分支**（和父代理的 `t3.c` 一致），
     *   不检查 `iface`。 */
    if (iface && strcmp(iface, "org.freedesktop.DBus.Introspectable") != 0
             && strcmp(iface, "org.a11y.atspi.Accessible") != 0
             && strcmp(iface, "org.freedesktop.DBus.Properties") != 0
             && strcmp(iface, "org.a11y.atspi.Cache") != 0
             && strcmp(iface, "org.a11y.atspi.Application") != 0
             /* K-1249：**根因修复** —— 白名单漏了 `org.a11y.atspi.Action`，
                导致 Action 的所有方法（GetName/DoAction/GetActions…）都被
                NOT_YET_HANDLED ⇒ libdbus 回 `UnknownMethod`（L-15 的真因）。 */
             && strcmp(iface, "org.a11y.atspi.Action") != 0
             /* 【PM-0145】同 K-1249 的坑：`GetInterfaces` 里声明了 Component/Text/Value，
                但白名单没有它们 ⇒ 调用一律 NOT_YET_HANDLED ⇒ libdbus 回 UnknownMethod
                ⇒ 标准客户端拿不到控件坐标（GetExtents 就这么被拒的）。 */
             && strcmp(iface, "org.a11y.atspi.Component") != 0
             && strcmp(iface, "org.a11y.atspi.Text") != 0
             && strcmp(iface, "org.a11y.atspi.Value") != 0)
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;

    /* 【PM-0109】org.a11y.atspi.Cache 挂在规范路径 /org/a11y/atspi/cache
     *   （at-spi2-core xml/Cache.xml）。pyatspi 建对象缓存时先调这里的 GetItems；
     *   该路径修前没有注册对象路径 ⇒ 回 UnknownMethod ⇒ 控件枚举恒为空
     *   （运维实测 `GetItems ... Cache doesn't exist`）。
     *   返回签名 a((so)(so)(so)iiassusau)：
     *     accessible 引用 / application 引用 / parent 引用 /
     *     index / childCount / interfaces / name / role / description / states */
    if (strcmp(path, WPF_ATSPI_PATH_CACHE) == 0) {
        DBusMessage *creply = dbus_message_new_method_return(msg);
        if (!creply) return DBUS_HANDLER_RESULT_NEED_MEMORY;
        DBusMessageIter cit;
        dbus_message_iter_init_append(creply, &cit);
        if (strcmp(member, "GetItems") == 0) {
            atspi_reply_cache_items(&cit);
        } else if (strcmp(member, "Get") == 0) {
            const char *want_prop = NULL;
            DBusMessageIter in;
            if (dbus_message_iter_init(msg, &in)
                && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING
                && dbus_message_iter_next(&in)
                && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING)
                dbus_message_iter_get_basic(&in, &want_prop);
            if (want_prop && strcmp(want_prop, "version") == 0) {
                DBusMessageIter var;
                dbus_uint32_t ver = 1;   /* 1 = 不要求 Ready 信号；GetItems 版本 1 就有 */
                dbus_message_iter_open_container(&cit, DBUS_TYPE_VARIANT, "u", &var);
                dbus_message_iter_append_basic(&var, DBUS_TYPE_UINT32, &ver);
                dbus_message_iter_close_container(&cit, &var);
            } else {
                dbus_message_unref(creply);
                return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
            }
        } else if (strcmp(member, "Introspect") == 0) {
            const char *cxml =
                "<node><interface name='org.a11y.atspi.Cache'>"
                "<method name='GetItems'><arg direction='out' "
                "type='a((so)(so)(so)iiassusau)'/></method>"
                "<property name='version' type='u' access='read'/>"
                "</interface></node>";
            dbus_message_iter_append_basic(&cit, DBUS_TYPE_STRING, &cxml);
        } else {
            dbus_message_unref(creply);
            return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
        }
        dbus_connection_send(conn, creply, NULL);
        dbus_message_unref(creply);
        return DBUS_HANDLER_RESULT_HANDLED;
    }

    wpf_atspi_node *n = atspi_node_by_path(path);
    if (!n) return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    const char *bus = g_bus_name;
    DBusMessage *reply = dbus_message_new_method_return(msg);
    if (!reply) return DBUS_HANDLER_RESULT_NEED_MEMORY;
    DBusMessageIter it;
    dbus_message_iter_init_append(reply, &it);

    /* K-1186：打印每个 member（调试用） */
    atspi_diag("[ATSPI] member=%s iface=%s path=%s", member, iface ? iface : "(null)", path);

    if (strcmp(member, "GetItems") == 0) {
        /* PM-0109：libatspi 2.44 把 Cache.GetItems 发在 app root 上（实测 path=root），
           不只发规范路径 /org/a11y/atspi/cache —— 两个落点都接。 */
        atspi_reply_cache_items(&it);
    } else if (strcmp(member, "GetName") == 0) {
        /* K-1184：`Accessible.GetName`（无参）返回节点名；`Action.GetName(i)`（带参）返回 action 名 */
        int argi = -1;
        if (dbus_message_get_args(msg, NULL, DBUS_TYPE_INT32, &argi, DBUS_TYPE_INVALID)
            && n && n->has_action && argi == 0) {
            const char *an = n->action_name;
            dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &an);
        } else {
            const char *nm = n->name;
            dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &nm);
        }
    } else if (strcmp(member, "GetRoleName") == 0) {
        const char *r = n->role;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &r);
    } else if (strcmp(member, "GetRole") == 0) {
        /* 【K-625】AT-SPI 的 `GetRole` 返回 uint32 role 码（pyatspi 用它，
         *   不是 `GetRoleName`）。`AT_SPI_ROLE_APPLICATION = 75`，
         *   `AT_SPI_ROLE_FRAME = 23`。
         *   【PM-0109 修正】原来对**所有**节点都返回 75/23 ⇒ 控件节点（ctl_*）也报 frame。
         *   现在按节点自己的 `role_id` 返回（缺省才回落到 root=75 / 窗口=23）。 */
        dbus_uint32_t role = n->role_id ? n->role_id
                                        : (dbus_uint32_t)((n->hwnd == 0) ? 75 : 23);
        dbus_message_iter_append_basic(&it, DBUS_TYPE_UINT32, &role);
    } else if (strcmp(member, "GetChildren") == 0) {
        /* 【K-667】**触发 WPF 创建 UIA provider**：主窗口（`HwndSource`）不走
         *   `UiaReturnRawElementProvider`（只有 `HwndHost`/`Popup` 走），而是通过
         *   `WM_GETOBJECT`（`HwndTarget.cs:1106` → `CriticalHandleWMGetobject`）提供 provider。
         *   shim 之前不处理 `WM_GETOBJECT` ⇒ provider 从未被创建 ⇒ `[UIA]` 日志空。
         *   这里主动发 `WM_GETOBJECT`（`OBJID_CLIENT = -4`），让 WPF 创建 provider 并
         *   调 `UiaReturnRawElementProvider`（我们已实现，存 `hwnd → el`）。
         *   **注意**：`SendMessageW` 是同步的（会阻塞 AT-SPI 泵线程），
         *   所以只对**顶层窗口**发一次（用 static 标志去重）。 */
        /* 【K-896】**已移除**：跨线程发 `WM_GETOBJECT` 是无效的（`SubclassWndProc` 要求
         *   当前线程有 Dispatcher，见 K-895）。改在 `create_window_utf8`（UI 线程）里发。 */
        DBusMessageIter arr;
        dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "(so)", &arr);
        int ni = (int)(n - g_nodes);
        for (int i = 0; i < g_node_count; ++i)
        {
            if (!g_nodes[i].alive) continue;
            if (n->hwnd == 0)
            {
                /* 应用 root → 顶层窗口 */
                if (g_nodes[i].hwnd != 0 && g_nodes[i].parent_index < 0)
                    atspi_append_ref(&arr, bus, g_nodes[i].path);
            }
            else if (g_nodes[i].parent_index == ni)
            {
                /* 【PM-0109】窗口/节点 → 它下面注册的控件（托管侧 wpf_atspi_register_control 填的） */
                atspi_append_ref(&arr, bus, g_nodes[i].path);
            }
        }
        dbus_message_iter_close_container(&it, &arr);
    } else if (strcmp(member, "GetChildAtIndex") == 0) {
        // 读取请求的 index 参数
        dbus_int32_t idx = 0;
        DBusMessageIter in;
        if (dbus_message_iter_init(msg, &in) && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_INT32)
            dbus_message_iter_get_basic(&in, &idx);
        /* 【PM-0150】按**父关系**取第 idx 个真子节点。原实现是 stub：忽略 idx、
           固定返回「第一个顶层窗口」，且对非 root 节点回空 object path。
           抓包实证（mon-150536.log）：pyatspi 对 wpf/root 调 10370 次
           GetChildAtIndex，10370 次全部拿到同一个 win_3200003
           ⇒ 每个 app 长出 122 个重复的 frame|SourceTree 子节点。
           越界与无子节点按 AT-SPI 约定回空引用 ("", /org/a11y/atspi/null)。 */
        const char *cp = WPF_ATSPI_PATH_NULL;
        int ni = (int)(n - g_nodes);
        if (idx >= 0) {
            int seen = 0;
            for (int i = 0; i < g_node_count; ++i) {
                if (!g_nodes[i].alive) continue;
                int is_child = (n->hwnd == 0)
                    ? (g_nodes[i].hwnd != 0 && g_nodes[i].parent_index < 0)
                    : (g_nodes[i].parent_index == ni);
                if (!is_child) continue;
                if (seen++ == (int)idx) { cp = g_nodes[i].path; break; }
            }
        }
        const char *cp_bus = (strcmp(cp, WPF_ATSPI_PATH_NULL) == 0) ? "" : bus;
        DBusMessageIter st;
        dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &cp_bus);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &cp);
        dbus_message_iter_close_container(&it, &st);
    } else if (strcmp(member, "GetParent") == 0) {
        /* 【PM-0144 续】真父：控件→所属窗口，窗口→app root，root→空引用。
           修前对所有非 root 节点一律回 root。 */
        const char *pp = atspi_node_parent_path(n);
        const char *pn = (*pp) ? bus : "";
        if (!*pp) pp = WPF_ATSPI_PATH_NULL;
        DBusMessageIter st;
        dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &pn);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &pp);
        dbus_message_iter_close_container(&it, &st);
    } else if (strcmp(member, "GetApplication") == 0) {
        DBusMessageIter st;
        dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
        const char *app_root_p = WPF_ATSPI_PATH_ROOT;
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &app_root_p);
        dbus_message_iter_close_container(&it, &st);
    } else if (strcmp(member, "Get") == 0) {
        /* 【K-622】**关键**：AT-SPI2 的 `name`/`description`/`parent`/`childCount`
         *   是 **D-Bus 属性**（走 `org.freedesktop.DBus.Properties.Get`），
         *   不是 `GetName`/`GetRoleName` 方法。pyatspi 读属性拿名字。
         *   没有这个 ⇒ pyatspi 显示 `name=None`。 */
        const char *want_iface = NULL, *want_prop = NULL;
        DBusMessageIter in;
        if (dbus_message_iter_init(msg, &in)) {
            if (dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING)
                dbus_message_iter_get_basic(&in, &want_iface);
            if (dbus_message_iter_next(&in) && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING)
                dbus_message_iter_get_basic(&in, &want_prop);
        }
        atspi_diag("[ATSPI] Properties.Get: iface=%s prop=%s (path=%s)",
                   want_iface ? want_iface : "(null)", want_prop ? want_prop : "(null)", path);
        DBusMessageIter var;
        /* 【K-630】**关键**：AT-SPI2 的 `Properties.Get` 用的是**大写首字母**
         *   （GObject 风格）：`Name`/`Description`/`ChildCount`/`Parent`。
         *   我们之前只匹配小写 ⇒ `pyatspi` 拿到空串。两个都认。 */
        if (want_prop && (strcmp(want_prop, "name") == 0 || strcmp(want_prop, "Name") == 0)) {
            const char *nm_val = n->name;
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "s", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_STRING, &nm_val);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "description") == 0 || strcmp(want_prop, "Description") == 0)) {
            const char *ds_val = "";
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "s", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_STRING, &ds_val);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "childCount") == 0 || strcmp(want_prop, "ChildCount") == 0)) {
            /* 【PM-0152】必须复用 atspi_child_count()：原实现把「所有带 hwnd 的节点」
               都算成 root 的孩子（9 窗口 + 113 控件 = 122），且对非 root 恒回 0,
               与 Cache.child_count / GetChildren 三处自相矛盾
               ⇒ pyatspi 按 122 遍历，GetChildAtIndex 又回同一个窗口
               ⇒ 122 个重复 frame 节点。判据：root=9、窗口=113、控件=0。 */
            dbus_int32_t cnt = (dbus_int32_t)atspi_child_count((int)(n - g_nodes));
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "i", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &cnt);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "NActions") == 0 || strcmp(want_prop, "nActions") == 0)) {
            /* K-1183：Action 接口的「动作数」是**属性**（libatspi 的 get_n_actions 读它） */
            dbus_int32_t na = (n->has_action ? 1 : 0);
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "i", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &na);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "Id") == 0 || strcmp(want_prop, "id") == 0)) {
            /* PM-0109：registry 用 Properties.Get 读回 Application.Id */
            dbus_int32_t aid = (dbus_int32_t)g_atspi_app_id;
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "i", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &aid);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "parent") == 0 || strcmp(want_prop, "Parent") == 0)) {
            /* 【PM-0144 续】与 GetParent 同源：真父（控件→窗口，窗口→root，root→null） */
            const char *pp = atspi_node_parent_path(n);
            const char *pn = (*pp) ? bus : "";
            if (!*pp) pp = WPF_ATSPI_PATH_NULL;
            DBusMessageIter st;
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "(so)", &var);
            dbus_message_iter_open_container(&var, DBUS_TYPE_STRUCT, NULL, &st);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &pn);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &pp);
            dbus_message_iter_close_container(&var, &st);
            dbus_message_iter_close_container(&it, &var);
        } else {
            dbus_message_unref(reply);
            return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
        }
    } else if (strcmp(member, "Set") == 0) {
        /* 【PM-0109】registry 会用 `Properties.Set` 把 Application.Id 写回来，
         *   修前这里走 fallback ⇒ UnknownMethod ⇒ Id 没设上。
         *   参数是 (ssv)：iface / 属性名 / variant 值。 */
        const char *want_iface = NULL, *want_prop = NULL;
        DBusMessageIter in;
        if (dbus_message_iter_init(msg, &in)) {
            if (dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING)
                dbus_message_iter_get_basic(&in, &want_iface);
            if (dbus_message_iter_next(&in)
                && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_STRING)
                dbus_message_iter_get_basic(&in, &want_prop);
            if (dbus_message_iter_next(&in)
                && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_VARIANT) {
                DBusMessageIter var;
                dbus_message_iter_recurse(&in, &var);
                if (dbus_message_iter_get_arg_type(&var) == DBUS_TYPE_INT32) {
                    dbus_int32_t id = 0;
                    dbus_message_iter_get_basic(&var, &id);
                    g_atspi_app_id = (int)id;
                    atspi_diag("[ATSPI] Properties.Set: %s.%s = %d（已收下）",
                               want_iface ? want_iface : "(null)",
                               want_prop ? want_prop : "(null)", (int)id);
                }
            }
        }
        /* 空 body 的 method return（reply 已在上面建好） */
    } else if (strcmp(member, "GetInterfaces") == 0) {
        /* K-1180：告诉 libatspi 本节点有哪些接口（否则 get_action_iface 返回 None） */
        DBusMessageIter arr;
        dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "s", &arr);
        /* 【PM-0152】与 Cache.GetItems 共用 atspi_append_interfaces()，两处同源。
           Text / Value 未实现，不列出（PM-0149）。 */
        atspi_append_interfaces(&arr, n);
        dbus_message_iter_close_container(&it, &arr);
    } else if (strcmp(member, "GetExtents") == 0) {
        /* 【PM-0145 应答】`org.a11y.atspi.Component.GetExtents(coord_type:u)` 规范应答是
           `(iiii)` = (x, y, width, height)。此前 `GetInterfaces` 里**声明了** Component
           接口（:985），但 handler 没有这个分支 ⇒ 调用走默认分支返回空 body，
           pyatspi/gdbus 解析不到矩形 ⇒ 标准客户端拿不到控件坐标。
           口径：`n->bounds[0..3]` 存的是**注册时的屏幕坐标**（见 wpf_atspi_register_control
           契约 :1243），这里**直接返回，不再加一次窗口原点**（否则会重复偏移一个 origin）。
           窗口节点（无 bounds）返回全 0，表示未知。 */
        dbus_int32_t ex = 0, ey = 0, ew = 0, eh = 0;
        if (n) {
            ex = n->bounds[0]; ey = n->bounds[1];
            ew = n->bounds[2]; eh = n->bounds[3];
        }
        {
            DBusMessageIter st;
            dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &ex);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &ey);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &ew);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_INT32, &eh);
            dbus_message_iter_close_container(&it, &st);
        }
        atspi_diag("[ATSPI] GetExtents path=%s ⇒ %d,%d %dx%d", path, ex, ey, ew, eh);
    } else if (strcmp(member, "GetActions") == 0) {
        /* K-1250：若 WPF 侧还没设置 action，则按需补一个默认 action（"激活窗口"），
           让 NActions>=1、GetName(0)/GetActions 有真实数据。
           这样外部（pyatspi/gdbus）就能看到可执行的 action 项。 */
        if (n && !n->has_action && n->parent_index < 0) {
            snprintf(n->action_name, sizeof(n->action_name), "%s", "激活窗口");
            snprintf(n->action_desc, sizeof(n->action_desc), "%s", "Activate this window");
            n->has_action = 1;
            atspi_diag("[ATSPI] GetActions：按需补默认 action name=%s", n->action_name);
        }
        /* K-1161：返回 node 上真实设置的 action（WPF 侧通过 wpf_atspi_set_action 提供） */
        DBusMessageIter arr;
        dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "(ss)", &arr);
        /* K-1181：恢复真实返回（K-1165 已证明崩溃不是 GetActions 导致的） */
        atspi_diag("[ATSPI] GetActions path=%s n=%p has=%d name=%s", path, (void*)n, n ? n->has_action : -1, (n && n->has_action) ? n->action_name : "");
        if (n && n->has_action) {
            DBusMessageIter st;
            const char *an = n->action_name;
            const char *ad = n->action_desc;
            dbus_message_iter_open_container(&arr, DBUS_TYPE_STRUCT, NULL, &st);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &an);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &ad);
            dbus_message_iter_close_container(&arr, &st);
        }
        dbus_message_iter_close_container(&it, &arr);
    } else if (strcmp(member, "DoAction") == 0) {
        /* K-1251：不再依赖 WPF 侧注册回调。DoAction 只**置一个待处理标志**，
           真正的点击由 `GetMessageW` 在 UI 线程上用**已验证有效的**
           `wpf_queue_push(wpf_thread_self(), WM_LBUTTONDOWN/UP)` 注入
           （与 `WPF_FAKE_CLICK` 同一机制，K-1228/K-1239 证明它有效）。
           这样避免 K-1227 的「投给 g_pump_thread 崩溃」问题。

           【PM-0109 修】修前两个缺陷（Python 档 2 实测：3 次 DoAction 全返回
           true、像素差 0、`[ATSPI-ACTION]` 0 条）：
           ① 只要 has_action 就返回 TRUE —— 无 hwnd、无 focus、索引非法时也报成功，
              验收脚本会误判「能驱动」；
           ② pending 标志只在 `GetMessageW` **入口**检查，而 UI 线程此刻阻塞在
              该函数内部的 poll 里 ⇒ 只置标志不唤醒，永远没人消费。
           现在：索引只认 0；无目标返回 FALSE；置标志后补一条 WM_NULL 唤醒
           目标窗口的 owner 线程，下一次进 `GetMessageW` 入口就会注入。 */
        dbus_int32_t idx = -1;
        DBusMessageIter din;
        if (dbus_message_iter_init(msg, &din)
            && dbus_message_iter_get_arg_type(&din) == DBUS_TYPE_INT32)
            dbus_message_iter_get_basic(&din, &idx);
        uint64_t target = 0;
        if (n && n->has_action && (idx == 0 || idx == -1))
            target = n->hwnd ? n->hwnd : (uint64_t)(uintptr_t)g_focus_window;
        if (target) {
            g_atspi_pending_action_hwnd = target;
            g_atspi_pending_action_x = g_atspi_pending_action_y = -1;
            /* 【PM-0109 档2】控件有 bounds ⇒ 注入坐标取中心；没有则回退 hwnd/env 默认 */
            if (n && n->parent_index >= 0 && n->bounds[2] > 0 && n->bounds[3] > 0) {
                g_atspi_pending_action_x = n->bounds[0] + n->bounds[2] / 2;
                g_atspi_pending_action_y = n->bounds[1] + n->bounds[3] / 2;
                atspi_diag("[ATSPI] DoAction：控件 bounds=[%d,%d %dx%d] ⇒ 注入点 %d,%d",
                           n->bounds[0], n->bounds[1], n->bounds[2], n->bounds[3],
                           g_atspi_pending_action_x, g_atspi_pending_action_y);
            } else if (n && n->parent_index >= 0) {
                /* 【PM-0143 收口】控件没有 bounds 就**没有可靠的注入点**。
                   不再回退到写死的窗口坐标，也不接受 env 指定点——那会把不同控件
                   全点到同一处（今天一批假负结果的根源）。直接判失败，让调用方看得见。 */
                atspi_diag("[ATSPI] DoAction 失败：控件无 bounds，拒绝注入 path=%s", n->path);
                target = 0;
                g_atspi_pending_action_hwnd = 0;
            }
            /* 【PM-0143 B2】XTest 优先（坐标与旧消息路径同口径）：
               ① DoAction 取到的控件 bounds 中心是**屏幕坐标**，这里是客户区口径，
                  必须先换成客户区再用；否则 XTranslateCoordinates 会把窗口原点算两次
                  （屏幕点被当客户区 → 再加一次 origin），注入点整体偏移 = 窗口原点。
                  旧消息路径（win32_msg.c K-1252）已修，这条 XTest 旁路当时没跟上。
               ② `WPF_LINUX_ATSPI_ACTION_XY=x,y` 可钉住**客户区**注入点（探针用，
                  排除 bounds 快照差异）；设置后优先于 bounds。
               ③ `WPF_ATSPI_ACTION_XTEST_CONN=own` 用应用**新开的** X 连接注入（C 臂），
                  默认复用应用既有连接（B 臂）；两者只差这一条。
               ④ 日志 `[ATSPI-XTEST]` 一次打全四个数，可直接判偏移。
               `WPF_ATSPI_ACTION_XTEST=0` 关掉本旁路，回退旧消息注入。 */
            {
                const char *xt = getenv("WPF_ATSPI_ACTION_XTEST");
                int xt_ok = 0;
                if (!(xt && *xt == '0') && g_wpf.dpy && g_atspi_pending_action_x >= 0) {
                    Window rt = DefaultRootWindow(g_wpf.dpy);
                    Window ch;
                    int scr_x = g_atspi_pending_action_x, scr_y = g_atspi_pending_action_y;
                    int org_x = -1, org_y = -1;
                    int cl_x = scr_x, cl_y = scr_y;
                    int sx = 0, sy = 0;
                    int have_client = XTranslateCoordinates(g_wpf.dpy, rt,
                                                            (Window)(uintptr_t)target,
                                                            scr_x, scr_y, &cl_x, &cl_y, &ch) ? 1 : 0;
                    XTranslateCoordinates(g_wpf.dpy, (Window)(uintptr_t)target, rt, 0, 0,
                                          &org_x, &org_y, &ch);
                    if (have_client
                        && XTranslateCoordinates(g_wpf.dpy, (Window)(uintptr_t)target, rt,
                                                 cl_x, cl_y, &sx, &sy, &ch)) {
                        Display *xdpy = g_wpf.dpy;
                        Display *own = NULL;
                        const char *cn = getenv("WPF_ATSPI_ACTION_XTEST_CONN");
                        if (cn && strcmp(cn, "own") == 0) {
                            own = XOpenDisplay(NULL);
                            if (own) xdpy = own;
                        }
                        {
                            extern void wpf_x11_set_input_focus(HWND hwnd);
                            wpf_x11_set_input_focus((HWND)(uintptr_t)target);
                            XFlush(xdpy);
                            usleep(80 * 1000);
                        }
                        XWarpPointer(xdpy, None, rt, 0, 0, 0, 0, sx, sy);
                        XFlush(xdpy);
                        usleep(20 * 1000);
                        XTestFakeButtonEvent(xdpy, 1, True, 0);
                        XFlush(xdpy);
                        usleep(150 * 1000);
                        XTestFakeButtonEvent(xdpy, 1, False, 0);
                        XFlush(xdpy);
                        fprintf(stderr,
                                "[ATSPI-XTEST] conn=%s bounds_screen=%d,%d origin=%d,%d client=%d,%d warp_screen=%d,%d hwnd=0x%llx\n",
                                own ? "own" : "same", scr_x, scr_y, org_x, org_y,
                                cl_x, cl_y, sx, sy, (unsigned long long)target);
                        fflush(stderr);
                        if (own) { XSync(own, False); XCloseDisplay(own); }
                        xt_ok = 1;
                    }
                }
                if (xt_ok) {
                    g_atspi_pending_action_hwnd = 0;
                    g_atspi_pending_action_x = g_atspi_pending_action_y = -1;
                    target = target;   /* 保持 target 非 0，DoAction 返回 TRUE */
                    goto atspi_action_done;
                }
            }
            atspi_diag("[ATSPI] DoAction：置待处理标志 hwnd=0x%llx idx=%d",
                       (unsigned long long)target, (int)idx);
            /* 唤醒 owner 线程（PostMessageW 内部按窗口 owner 路由 + wake） */
            if (!PostMessageW((HWND)(uintptr_t)target, 0 /*WM_NULL*/, 0, 0)) {
                g_atspi_pending_action_hwnd = 0;
                g_atspi_pending_action_x = g_atspi_pending_action_y = -1;
                atspi_diag("[ATSPI] DoAction：PostMessage 唤醒失败，动作取消 hwnd=0x%llx",
                           (unsigned long long)target);
                target = 0;
            }
        }
    atspi_action_done:
        dbus_bool_t r = target ? TRUE : FALSE;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_BOOLEAN, &r);
    } else if (strcmp(member, "GetActionName") == 0 || strcmp(member, "GetActionDescription") == 0 || strcmp(member, "GetKeyBinding") == 0
               || strcmp(member, "GetName") == 0 || strcmp(member, "GetDescription") == 0 || strcmp(member, "getKeyBinding") == 0) {
        /* AT-SPI 的 `org.a11y.atspi.Action` 用**带参**的 `GetName(i)`/`GetDescription(i)`/`GetKeyBinding(i)` */
        const char *val = "";
        if (n && n->has_action && (strcmp(member, "GetName") == 0 || strcmp(member, "GetActionName") == 0))
            val = n->action_name;
        else if (n && n->has_action && (strcmp(member, "GetDescription") == 0 || strcmp(member, "GetActionDescription") == 0))
            val = n->action_desc;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &val);
    } else if (strcmp(member, "Introspect") == 0) {
        const char *xml =
            "<node><interface name='org.a11y.atspi.Accessible'>"
            "<method name='GetChildAtIndex'><arg direction='in' type='i'/><arg direction='out' type='(so)'/></method>"
            "<method name='GetChildren'><arg direction='out' type='a(so)'/></method>"
            "<method name='GetRoleName'><arg direction='out' type='s'/></method>"
            "<method name='GetName'><arg direction='out' type='s'/></method>"
            "<method name='GetParent'><arg direction='out' type='(so)'/></method>"
            "<method name='GetApplication'><arg direction='out' type='(so)'/></method>"
            "<method name='GetInterfaces'><arg direction='out' type='as'/></method>"
            "</interface>"
            /* K-1159（T3）：AT-SPI Action 接口 —— 最小实现（空 action 列表 + DoAction 返回 false）。
               目的：让 pyatspi 能调 GetActions()/DoAction()，证明接口存在；
               真正的 action 数据需要 WPF 侧通过 Cecil 注入提供（下轮）。*/
            "<interface name='org.a11y.atspi.Action'>"
            "<method name='GetActions'><arg direction='out' type='a(ss)'/></method>"
            "<method name='DoAction'><arg direction='in' type='i'/><arg direction='out' type='b'/></method>"
            "<method name='GetName'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetDescription'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetKeyBinding'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetActionName'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetActionDescription'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<property name='NActions' type='i' access='read'/>"
            "</interface>"
            "</node>";
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &xml);
    } else {
        dbus_message_unref(reply);
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    }
    dbus_connection_send(conn, reply, NULL);
    dbus_message_unref(reply);
    return DBUS_HANDLER_RESULT_HANDLED;
}

static const DBusObjectPathVTable g_atspi_vtbl = { NULL, atspi_msg_handler, NULL, NULL, NULL, NULL };

static int atspi_register_node(const char *path, const char *name, const char *role, uint64_t hwnd)
{
    if (!g_bus) return 0;
    wpf_atspi_node *n = atspi_node_find(hwnd);
    if (n) {
        snprintf(n->name, sizeof(n->name), "%s", name ? name : "");
        return 1;
    }
    if (g_node_count >= WPF_ATSPI_MAX_NODES) return 0;
    n = &g_nodes[g_node_count++];
    memset(n, 0, sizeof(*n));
    snprintf(n->path, sizeof(n->path), "%s", path);
    snprintf(n->name, sizeof(n->name), "%s", name ? name : "");
    snprintf(n->role, sizeof(n->role), "%s", role ? role : "unknown");
    n->role_id = atspi_role_id_for(role);      /* 【PM-0109】窗口/应用节点自带 role 数值 */
    n->hwnd = hwnd;
    n->parent_index = -1;
    n->bounds[0] = n->bounds[1] = n->bounds[2] = n->bounds[3] = 0;
    n->alive = 1;
    if (!dbus_connection_register_object_path(g_bus, n->path, &g_atspi_vtbl, NULL))
        return 0;
    n->registered = 1;
    atspi_diag("[ATSPI] 注册节点 path=%s name=%s role=%s hwnd=0x%llx", n->path, n->name, n->role, (unsigned long long)hwnd);
    return 1;
}

// 【对外】窗口创建时注册（在 create_window_utf8 里调用）
void wpf_atspi_register_window(uint64_t hwnd, const char *title)
{
    if (!atspi_ensure()) return;
    static int root_done = 0;
    if (!root_done) {
        atspi_register_node(WPF_ATSPI_PATH_ROOT, "SourceTree", "application", 0);
        /* PM-0109：Cache 接口的对象路径（规范路径，见 at-spi2-core xml/Cache.xml） */
        if (!dbus_connection_register_object_path(g_bus, WPF_ATSPI_PATH_CACHE,
                                                  &g_atspi_vtbl, NULL))
            atspi_diag("[ATSPI] Cache 路径注册失败: %s", WPF_ATSPI_PATH_CACHE);
        else
            atspi_diag("[ATSPI] Cache 路径已注册: %s", WPF_ATSPI_PATH_CACHE);
        root_done = 1;
    }
    char path[128];
    snprintf(path, sizeof(path), "/org/a11y/atspi/accessible/wpf/win_%llx", (unsigned long long)hwnd);
    /* 【K-1069】WPF 从不调 `SetWindowTextW`（K-1068 实测 `[SETTEXT]` 0 次）⇒ title 永远空
     *   ⇒ frame 的 name 是空串。兜底：空 title 时用 "SourceTree"。 */
    atspi_register_node(path, (title && *title) ? title : "SourceTree", "frame", hwnd);
}

/* ══ 【PM-0109 档 1】控件层注册（托管侧调用）══════════════════════════════════
 * 背景：原先 `atspi_register_node` 只被 `wpf_atspi_register_window` 调用 ⇒ 树里只有窗口，
 *   `GetChildren` 对窗口返回 0 ⇒ 任何 AT-SPI 客户端都只能看到 3 个 frame。
 *   要看到控件层，必须由**托管侧**（能看见 WPF 视觉树/AutomationPeer 的一侧）逐控件报上来；
 *   shim 只管把它们挂进 D-Bus 树并按 path 服务 GetChildren。
 *
 * 契约（托管侧在 UI 线程、布局完成后调用）：
 *   - hwnd：该控件所属窗口（必须已 `wpf_atspi_register_window` 注册过）；
 *   - id：同一 hwnd 下唯一（建议用 AutomationPeer 的稳定序号或 hash）；
 *   - role：AT-SPI 角色名（"push button"/"page tab"/"table cell"…），原样透传；
 *   - role_id：AT-SPI Role 数值；传 0 时由 shim 按 role 名映射；
 *   - x,y,w,h：设备像素；全 0 表示未知（不影响枚举，只影响 GetExtents）；
 *   - has_action：1 = 该控件可 Invoke（tier 1 只要求枚举，可为 0）。
 *   返回 1 成功；0 = AT-SPI 未就绪 / 父窗口未注册 / 超上限。
 */
int wpf_atspi_register_control(uint64_t hwnd, int id, const char *name, const char *role,
                               uint32_t role_id, int32_t x, int32_t y, int32_t w, int32_t h,
                               int has_action)
{
    /* 【PM-0145 诊断】失败路径必须留痕：否则「托管侧没调」「ensure 没就绪」「父窗口没挂上」
       三种情况在日志里长得一模一样。仅诊断，不改行为。 */
    if (!atspi_ensure() || !g_bus) {
        atspi_diag("[ATSPI] ctl 注册诊断：ensure/g_bus 未就绪 hwnd=0x%llx id=%d name=%s",
                   (unsigned long long)hwnd, id, name ? name : "?");
        return 0;
    }
    wpf_atspi_node *parent = atspi_node_find(hwnd);
    if (!parent || parent->hwnd == 0) {
        atspi_diag("[ATSPI] ctl 注册诊断：父窗口未注册 hwnd=0x%llx id=%d name=%s 节点数=%d",
                   (unsigned long long)hwnd, id, name ? name : "?", g_node_count);
        return 0;
    }
    int pidx = (int)(parent - g_nodes);

    char path[128];
    snprintf(path, sizeof(path), "%s/ctl_%d", parent->path, id);

    wpf_atspi_node *n = atspi_node_by_path(path);
    if (!n) {
        /* 【PM-0109 槽位复用修复】`clear_controls` 把子节点 memset 掉（path/alive/registered 全清）
           但**不归还槽位**，而旧实现只会 `g_node_count++` ⇒ 托管侧每拍「清空 + 重注册」
           就吃掉一整轮新槽位（113 个/轮），4–5 轮打满 512 后 register_control 全部静默失败
           ⇒ AT-SPI 树被清空后再也长不回来（GetChildren 返回空、path UnknownMethod）。
           修法：先找**死槽**（!alive && !registered && path 为空）复用；只有确实没有死槽才扩表。
           注意：不要动 `clear_controls` 的语义——它 memset 出的空槽正是这里的可复用来源。 */
        int slot = -1;
        for (int i = 0; i < g_node_count; ++i) {
            if (!g_nodes[i].alive && !g_nodes[i].registered && g_nodes[i].path[0] == '\0') {
                slot = i; break;
            }
        }
        if (slot >= 0) {
            n = &g_nodes[slot];
        } else {
            if (g_node_count >= WPF_ATSPI_MAX_NODES) {
                /* 不许静默失败：下次靠日志就能看出来，不用 12 连采样才反推 */
                atspi_diag("[ATSPI] 节点表满 %d，拒绝注册 path=%s（槽位未复用或树过大）",
                           WPF_ATSPI_MAX_NODES, path);
                return 0;
            }
            n = &g_nodes[g_node_count];
        }
        memset(n, 0, sizeof(*n));
        snprintf(n->path, sizeof(n->path), "%s", path);
        if (!dbus_connection_register_object_path(g_bus, n->path, &g_atspi_vtbl, NULL)) {
            atspi_diag("[ATSPI] register_object_path 失败 path=%s", n->path);
            return 0;
        }
        n->registered = 1;
        if (n == &g_nodes[g_node_count]) g_node_count++;
    }
    n->alive = 1;
    n->hwnd = hwnd;
    n->parent_index = pidx;
    snprintf(n->name, sizeof(n->name), "%s", name ? name : "");
    snprintf(n->role, sizeof(n->role), "%s", role ? role : "unknown");
    n->role_id = role_id ? role_id : atspi_role_id_for(role);
    n->bounds[0] = x; n->bounds[1] = y; n->bounds[2] = w; n->bounds[3] = h;
    /* 銆怭M-0109 妗?銆戞帶浠惰妭鐐归粯璁ゅ叿澶囥€岀偣鍑汇€嶅姩浣滐細娉ㄥ叆鐐圭敤 bounds 涓績锛沑n       娌℃湁 bounds 鐨勬帶浠舵墠涓嶇粰鍔ㄤ綔锛堟棤娉曞畾浣嶏級銆傛ˉ涓嶄紶 has_action 涔熻兘椹卞姩銆?*/
    n->has_action = (has_action || (w > 0 && h > 0)) ? 1 : 0;
    /* 【PM-0109 档2】控件节点的动作是「点击」，不再沿用窗口级「激活窗口」 */
    if (n->has_action) {
        snprintf(n->action_name, sizeof(n->action_name), "%s", "点击");
        snprintf(n->action_desc, sizeof(n->action_desc), "%s", n->name);
    }
    atspi_diag("[ATSPI] 注册控件 path=%s name=%s role=%s id=%d", n->path, n->name, n->role, id);
    return 1;
}

/* 清掉某窗口下已注册的整棵控件子树（重新布局/窗口销毁前调）。 */
void wpf_atspi_clear_controls(uint64_t hwnd)
{
    if (!atspi_ensure() || !g_bus) return;
    wpf_atspi_node *parent = atspi_node_find(hwnd);
    if (!parent || parent->hwnd == 0) return;
    int pidx = (int)(parent - g_nodes);
    for (int i = 0; i < g_node_count; ++i) {
        if (!g_nodes[i].alive || g_nodes[i].parent_index != pidx) continue;
        if (g_nodes[i].registered)
            dbus_connection_unregister_object_path(g_bus, g_nodes[i].path);
        memset(&g_nodes[i], 0, sizeof(g_nodes[i]));
    }
    atspi_diag("[ATSPI] 清空控件子树 hwnd=0x%llx", (unsigned long long)hwnd);
}

// 【对外】【K-655】窗口标题变化时更新 AT-SPI node 的 name
//   根因（K-654）：`create_window_utf8` 时 WPF 还没调 `SetWindowTextW`，
//   所以注册时 `title` 是空 ⇒ frame 的 name 是空串。
//   在 `SetWindowTextW` 里调本函数，把最新标题同步到 node。
void wpf_atspi_update_name(uint64_t hwnd, const char *title)
{
    atspi_diag("[ATSPI] update_name 被调用: hwnd=0x%llx title=%s",
               (unsigned long long)hwnd, title ? title : "(null)");
    if (!atspi_ensure()) { atspi_diag("[ATSPI] update_name: atspi_ensure 失败"); return; }
    if (!title || !*title) { atspi_diag("[ATSPI] update_name: title 空"); return; }
    wpf_atspi_node *n = atspi_node_find(hwnd);
    if (!n) {
        atspi_diag("[ATSPI] update_name: node 找不到 hwnd=0x%llx（已注册的节点：）",
                   (unsigned long long)hwnd);
        for (int i = 0; i < g_node_count; ++i)
            if (g_nodes[i].alive)
                atspi_diag("[ATSPI]   已注册 hwnd=0x%llx path=%s name=%s",
                           (unsigned long long)g_nodes[i].hwnd, g_nodes[i].path, g_nodes[i].name);
        return;
    }
    snprintf(n->name, sizeof(n->name), "%s", title);
    atspi_diag("[ATSPI] 更新 node name: hwnd=0x%llx name=%s", (unsigned long long)hwnd, title);
}

// 【对外】窗口销毁时标记
void wpf_atspi_unregister_window(uint64_t hwnd)
{
    wpf_atspi_node *n = atspi_node_find(hwnd);
    if (n) n->alive = 0;
}

// 【对外】查询是否已注册任意窗口（供诊断）
int wpf_atspi_node_count(void) { return g_node_count; }

/* ── K-1161（T3）：Action 接口的写入侧（由 WPF 侧通过 Cecil 注入调用）────────────
   wpf_atspi_set_action(hwnd, name, desc)：
     给某个已注册窗口设置它的 action（如 "Invoke"/"按下按钮"）。
     设置后 GetActions() 会返回它，DoAction(0) 会触发 invoker。
   wpf_atspi_set_action_invoker(fn)：
     注册「执行 action」的回调（WPF 侧在注入时传入一个函数指针）。*/
void wpf_atspi_set_action(uint64_t hwnd, const char *name, const char *desc)
{
    /* K-1165：hwnd==0 时设到所有 node（WPF 侧拿不到真实 hwnd 时的兜底，
       因为 pyatspi 查的是 frame 节点而非 root）*/
    int count = 0;
    for (int i = 0; i < g_node_count; ++i) {
        if (!g_nodes[i].alive) continue;
        if (hwnd != 0 && g_nodes[i].hwnd != hwnd) continue;
        snprintf(g_nodes[i].action_name, sizeof(g_nodes[i].action_name), "%s", name ? name : "");
        snprintf(g_nodes[i].action_desc, sizeof(g_nodes[i].action_desc), "%s", desc ? desc : "");
        g_nodes[i].has_action = 1;
        count++;
    }
    atspi_diag("[ATSPI] set_action: hwnd=0x%llx name=%s → %d 个 node", (unsigned long long)hwnd, name ? name : "", count);
}

void wpf_atspi_set_action_invoker(void *fn)
{
    g_action_invoker = (wpf_atspi_action_invoker_t)fn;
    atspi_diag("[ATSPI] set_action_invoker: %p", fn);
}
