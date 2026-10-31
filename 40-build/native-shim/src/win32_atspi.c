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
#include "win32_internal.h"
#include <dbus/dbus.h>

#ifndef E_NOTIMPL
#define E_NOTIMPL ((int32_t)0x80004001)
#endif
#define WPF_S_OK ((int32_t)0)

#define ATSPI_REG_SERVICE "org.a11y.atspi.Registry"
#define ATSPI_REG_PATH    "/org/a11y/atspi/registry"
#define ATSPI_REG_IFACE   "org.a11y.atspi.Registry"
#define ATSPI_ROOT_PATH   "/org/a11y/atspi/accessible/root"

static DBusConnection *g_bus = NULL;
static int g_bus_state = 0;     /* 0=未尝试 1=可用 -1=已放弃 */
static char g_bus_name[128];

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
        for (int i = 0; i < 4; ++i) {
            snprintf(p, sizeof(p), "%s/.cache/at-spi/bus%d", home, i);
            if (access(p, F_OK) == 0) { snprintf(out, outsz, "unix:path=%s", p); atspi_diag("[ATSPI] 地址来自 %s", p); return 1; }
        }
    }
    /* 最后退回通用 session bus（at-spi2 也常挂在上面） */
    const char *sess = getenv("DBUS_SESSION_BUS_ADDRESS");
    if (sess && *sess) { snprintf(out, outsz, "%s", sess); atspi_diag("[ATSPI] 回退到 $DBUS_SESSION_BUS_ADDRESS"); return 1; }
    return 0;
}

// ── 懒初始化：连 bus + 注册应用 ─────────────────────────────────────────────
static int atspi_ensure(void)
{
    if (g_bus_state != 0) return g_bus_state > 0;
    g_bus_state = -1;                                  /* 先悲观：任何一步失败都保持放弃 */

    char addr[1024];
    if (!atspi_address(addr, sizeof(addr))) { atspi_diag("[ATSPI] 无可用总线，降级"); return 0; }

    DBusError err; dbus_error_init(&err);
    g_bus = dbus_connection_open_private(addr, &err);
    if (!g_bus) {
        atspi_diag("[ATSPI] 连接失败：%s", err.message ? err.message : "?");
        dbus_error_free(&err); return 0;
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
    g_bus_state = 1;
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
