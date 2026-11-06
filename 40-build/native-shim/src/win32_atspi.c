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
            const char *bus = "org.a11y.atspi.SourceTree";
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

#define WPF_ATSPI_MAX_NODES 64
#define WPF_ATSPI_PATH_ROOT "/org/a11y/atspi/accessible/wpf/root"

typedef struct {
    char path[128];                 // D-Bus 对象路径
    char name[256];                 // 窗口标题
    char role[32];                  // "application" / "frame"
    uint64_t hwnd;                  // 关联的 HWND（0 = 应用 root）
    int registered;                 // 是否已注册到 D-Bus
    int alive;
    /* K-1161（T3）：Action 接口数据（由 WPF 侧通过 wpf_atspi_set_action 提供） */
    int has_action;
    char action_name[64];
    char action_desc[64];
} wpf_atspi_node;

static wpf_atspi_node g_nodes[WPF_ATSPI_MAX_NODES];
static int g_node_count = 0;

/* K-1161：WPF 侧注册的「执行 action」回调（Cecil 注入时传入） */
typedef void (*wpf_atspi_action_invoker_t)(uint64_t hwnd);
static wpf_atspi_action_invoker_t g_action_invoker = NULL;
static int g_atspi_tree_ready = 0;

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
             && strcmp(iface, "org.a11y.atspi.Application") != 0)
        return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;

    wpf_atspi_node *n = atspi_node_by_path(path);
    if (!n) return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
    const char *bus = g_bus_name;
    DBusMessage *reply = dbus_message_new_method_return(msg);
    if (!reply) return DBUS_HANDLER_RESULT_NEED_MEMORY;
    DBusMessageIter it;
    dbus_message_iter_init_append(reply, &it);

    if (strcmp(member, "GetName") == 0) {
        const char *nm = n->name;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &nm);
    } else if (strcmp(member, "GetRoleName") == 0) {
        const char *r = n->role;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &r);
    } else if (strcmp(member, "GetRole") == 0) {
        /* 【K-625】AT-SPI 的 `GetRole` 返回 uint32 role 码（pyatspi 用它，
         *   不是 `GetRoleName`）。`AT_SPI_ROLE_APPLICATION = 75`，
         *   `AT_SPI_ROLE_FRAME = 23`。 */
        dbus_uint32_t role = (n->hwnd == 0) ? 75 : 23;
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
        for (int i = 0; i < g_node_count; ++i)
            if (g_nodes[i].alive && g_nodes[i].hwnd != 0 && n->hwnd == 0)
                atspi_append_ref(&arr, bus, g_nodes[i].path);
        dbus_message_iter_close_container(&it, &arr);
    } else if (strcmp(member, "GetChildAtIndex") == 0) {
        // 读取请求的 index 参数
        dbus_int32_t idx = 0;
        DBusMessageIter in;
        if (dbus_message_iter_init(msg, &in) && dbus_message_iter_get_arg_type(&in) == DBUS_TYPE_INT32)
            dbus_message_iter_get_basic(&in, &idx);
        // 简化：忽略 idx，返回第一个顶层窗口
        const char *cp = "";
        int found = 0;
        for (int i = 0; i < g_node_count && !found; ++i)
            if (g_nodes[i].alive && g_nodes[i].hwnd != 0 && n->hwnd == 0) { cp = g_nodes[i].path; found = 1; }
        DBusMessageIter st;
        dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &cp);
        dbus_message_iter_close_container(&it, &st);
        (void)idx;
    } else if (strcmp(member, "GetParent") == 0) {
        const char *pp = (n->hwnd == 0) ? "" : WPF_ATSPI_PATH_ROOT;
        DBusMessageIter st;
        dbus_message_iter_open_container(&it, DBUS_TYPE_STRUCT, NULL, &st);
        dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
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
            dbus_int32_t cnt = 0;
            for (int i = 0; i < g_node_count; ++i)
                if (g_nodes[i].alive && g_nodes[i].hwnd != 0 && n->hwnd == 0) ++cnt;
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "i", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &cnt);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "NActions") == 0 || strcmp(want_prop, "nActions") == 0)) {
            /* K-1183：Action 接口的「动作数」是**属性**（libatspi 的 get_n_actions 读它） */
            dbus_int32_t na = (n->has_action ? 1 : 0);
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "i", &var);
            dbus_message_iter_append_basic(&var, DBUS_TYPE_INT32, &na);
            dbus_message_iter_close_container(&it, &var);
        } else if (want_prop && (strcmp(want_prop, "parent") == 0 || strcmp(want_prop, "Parent") == 0)) {
            const char *pp = (n->hwnd == 0) ? "" : WPF_ATSPI_PATH_ROOT;
            DBusMessageIter st;
            dbus_message_iter_open_container(&it, DBUS_TYPE_VARIANT, "(so)", &var);
            dbus_message_iter_open_container(&var, DBUS_TYPE_STRUCT, NULL, &st);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_STRING, &bus);
            dbus_message_iter_append_basic(&st, DBUS_TYPE_OBJECT_PATH, &pp);
            dbus_message_iter_close_container(&var, &st);
            dbus_message_iter_close_container(&it, &var);
        } else {
            dbus_message_unref(reply);
            return DBUS_HANDLER_RESULT_NOT_YET_HANDLED;
        }
    } else if (strcmp(member, "GetInterfaces") == 0) {
        /* K-1180：告诉 libatspi 本节点有哪些接口（否则 get_action_iface 返回 None） */
        DBusMessageIter arr;
        dbus_message_iter_open_container(&it, DBUS_TYPE_ARRAY, "s", &arr);
        const char *ifs[] = {
            "org.a11y.atspi.Accessible",
            "org.a11y.atspi.Action",
            "org.a11y.atspi.Component",
            "org.a11y.atspi.Text",
            "org.a11y.atspi.Value",
        };
        for (size_t k = 0; k < sizeof(ifs)/sizeof(ifs[0]); ++k)
            dbus_message_iter_append_basic(&arr, DBUS_TYPE_STRING, &ifs[k]);
        dbus_message_iter_close_container(&it, &arr);
    } else if (strcmp(member, "GetActions") == 0) {
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
        /* K-1163：回退到 false（带回调的版本崩溃，待排查） */
        dbus_bool_t r = FALSE;
        dbus_message_iter_append_basic(&it, DBUS_TYPE_BOOLEAN, &r);
    } else if (strcmp(member, "GetActionName") == 0 || strcmp(member, "GetActionDescription") == 0 || strcmp(member, "GetKeyBinding") == 0) {
        const char *empty = "";
        dbus_message_iter_append_basic(&it, DBUS_TYPE_STRING, &empty);
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
            "<method name='GetActionName'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetActionDescription'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
            "<method name='GetKeyBinding'><arg direction='in' type='i'/><arg direction='out' type='s'/></method>"
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
    n->hwnd = hwnd;
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
        root_done = 1;
    }
    char path[128];
    snprintf(path, sizeof(path), "/org/a11y/atspi/accessible/wpf/win_%llx", (unsigned long long)hwnd);
    /* 【K-1069】WPF 从不调 `SetWindowTextW`（K-1068 实测 `[SETTEXT]` 0 次）⇒ title 永远空
     *   ⇒ frame 的 name 是空串。兜底：空 title 时用 "SourceTree"。 */
    atspi_register_node(path, (title && *title) ? title : "SourceTree", "frame", hwnd);
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
