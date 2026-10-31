// xdnd-probe —— 扮演 **XDND 拖放源**，向目标窗口发 Enter/Position/Drop，看 shim 是否应答。
//
// 【为什么需要它（K-010）】原来的判据里写 `if (fs.existsSync(".../xdnd-probe")) …`，
//   而这个文件**从来不存在** ⇒ 整段静默跳过 ⇒ `status_received` / `drop_translated`
//   一直是默认值 false，导致"消息层未通"这个结论**从未被真正测过**。
//   本探针是最小可判定形态：能区分"消息层通不通"与"Drop 层通不通"。
//
// 用法：xdnd-probe <window-id-hex-or-dec>
// 输出（stdout 最后一行 JSON）：
//   {"status_received":bool,"accepted":bool,"selection_served":int,"version":int,
//    "drop_sent":bool,"finished_received":bool,"error":"..."}
//
// 协议要点（freedesktop XDND v5）：
//   XdndEnter   : l[0] = (version<<24)|flags, l[1..2] = 3 个类型 atom（低 16 位是 atom 序号）
//   XdndPosition: l[0] = 源窗口, l[2] = root_x<<16|root_y, l[3] = 时间戳, l[4] = 动作
//   应答 XdndStatus: l[0] = 目标窗口, l[1] = flags(bit0=accept), l[4] = 动作
//   XdndDrop   : l[0] = 源窗口, l[2] = 时间戳
//   目标随后用 XConvertSelection 取 text/uri-list；源收到 SelectionNotify = selection_served

#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

static Display *dpy;
static Window src;
static Atom a_xdnd_aware, a_xdnd_enter, a_xdnd_position, a_xdnd_status, a_xdnd_drop,
            a_xdnd_finished, a_xdnd_selection, a_text_uri_list;

static long now_ms(void) {
    struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec * 1000L + ts.tv_nsec / 1000000L;
}

// 取 atom 序号（XDND 的 l[1..2] 里放的是 atom 的**低 16 位**）
static long atom_id(Atom a) { return (long)a; }

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "用法: %s <window-id>\n", argv[0]); return 2; }
    Window target = (Window)strtoul(argv[1], NULL, 0);

    dpy = XOpenDisplay(NULL);
    if (!dpy) { printf("{\"error\":\"XOpenDisplay 失败\"}\n"); return 1; }

    a_xdnd_aware     = XInternAtom(dpy, "XdndAware", False);
    a_xdnd_enter     = XInternAtom(dpy, "XdndEnter", False);
    a_xdnd_position  = XInternAtom(dpy, "XdndPosition", False);
    a_xdnd_status    = XInternAtom(dpy, "XdndStatus", False);
    a_xdnd_drop      = XInternAtom(dpy, "XdndDrop", False);
    a_xdnd_finished  = XInternAtom(dpy, "XdndFinished", False);
    a_xdnd_selection = XInternAtom(dpy, "XdndSelection", False);
    a_text_uri_list  = XInternAtom(dpy, "text/uri-list", False);

    // 源窗口：1x1 的 InputOnly（只收事件，不显示）
    src = XCreateSimpleWindow(dpy, DefaultRootWindow(dpy), -10, -10, 1, 1, 0, 0, 0);
    XSelectInput(dpy, src, PropertyChangeMask | StructureNotifyMask);
    // 【必须】把自己设成 XdndSelection 的 owner —— 目标会用
    //   XConvertSelection(XdndSelection, text/uri-list, …) 来**向源取数据**；
    //   若源不是 owner，X 直接把那个转换判为失败（SelectionNotify 带 property=None），
    //   于是我们这边 `selection_served` 永远是 0 —— **看起来像目标没取数据，其实是源没接**。
    //   第一版探针就是漏了这一句，害我以为 Drop 层没通。
    XSetSelectionOwner(dpy, a_xdnd_selection, src, CurrentTime);
    XFlush(dpy);
    if (XGetSelectionOwner(dpy, a_xdnd_selection) != src) {
        printf("{\"status_received\":false,\"accepted\":false,\"selection_served\":0,"
               "\"version\":0,\"drop_sent\":false,\"finished_received\":false,"
               "\"error\":\"拿不到 XdndSelection 的 owner\"}\n");
        XCloseDisplay(dpy); return 0;
    }

    // 读目标的 XdndAware 版本（没有就报错 —— 这正是"声明面"的验证）
    Atom type; int fmt; unsigned long n, after; unsigned char *data = NULL;
    int version = 0;
    if (XGetWindowProperty(dpy, target, a_xdnd_aware, 0, 1, False, XA_ATOM,
                           &type, &fmt, &n, &after, &data) == Success && data) {
        if (n >= 1) version = (int)*(Atom *)data;
        XFree(data);
    }
    if (version == 0) {
        printf("{\"status_received\":false,\"accepted\":false,\"selection_served\":0,"
               "\"version\":0,\"drop_sent\":false,\"finished_received\":false,"
               "\"error\":\"目标窗口没有 XdndAware 属性\"}\n");
        XCloseDisplay(dpy); return 0;
    }

    // ① XdndEnter（声明我们支持 text/uri-list）
    {
        XEvent e; memset(&e, 0, sizeof(e));
        e.xclient.type = ClientMessage; e.xclient.display = dpy;
        e.xclient.window = target; e.xclient.message_type = a_xdnd_enter;
        e.xclient.format = 32;
        e.xclient.data.l[0] = ((long)version << 24);
        e.xclient.data.l[1] = atom_id(a_text_uri_list) & 0xFFFF;
        e.xclient.data.l[2] = 0; e.xclient.data.l[3] = 0; e.xclient.data.l[4] = 0;
        XSendEvent(dpy, target, False, NoEventMask, &e);
        XFlush(dpy);
    }

    // ② XdndPosition（窗口左上角，动作 = copy(1)）
    {
        XEvent e; memset(&e, 0, sizeof(e));
        e.xclient.type = ClientMessage; e.xclient.display = dpy;
        e.xclient.window = target; e.xclient.message_type = a_xdnd_position;
        e.xclient.format = 32;
        e.xclient.data.l[0] = (long)src;
        e.xclient.data.l[1] = 0;
        e.xclient.data.l[2] = (0 << 16) | 0;       // root x,y
        e.xclient.data.l[3] = CurrentTime;
        e.xclient.data.l[4] = 1;                   // XdndActionCopy
        XSendEvent(dpy, target, False, NoEventMask, &e);
        XFlush(dpy);
    }

    // ③ 等 XdndStatus（2 s）
    int status_received = 0, accepted = 0, finished_received = 0, drop_sent = 0;
    int selection_served = 0;   // **真计数**：我们实际响应了几次 SelectionRequest
    long t0 = now_ms();
    while (now_ms() - t0 < 2000) {
        while (XPending(dpy)) {
            XEvent e; XNextEvent(dpy, &e);
            if (e.type == ClientMessage && e.xclient.message_type == a_xdnd_status) {
                status_received = 1;
                accepted = (e.xclient.data.l[1] & 1) ? 1 : 0;
            }
        }
        if (status_received) break;
        usleep(20000);
    }

    // ④ XdndDrop（只有被 accept 才发；这是"消息层通了"之后才有意义的下一步）
    if (status_received && accepted) {
        XEvent e; memset(&e, 0, sizeof(e));
        e.xclient.type = ClientMessage; e.xclient.display = dpy;
        e.xclient.window = target; e.xclient.message_type = a_xdnd_drop;
        e.xclient.format = 32;
        e.xclient.data.l[0] = (long)src;
        e.xclient.data.l[1] = 0;
        e.xclient.data.l[2] = CurrentTime;
        XSendEvent(dpy, target, False, NoEventMask, &e);
        XFlush(dpy);
        drop_sent = 1;

        // ⑤ 目标会用 XConvertSelection(XdndSelection, text/uri-list)。源必须**服务**这个请求：
        //    在 3 s 内盯 SelectionRequest，把 uri-list 填进目标指定的 property。
        long t1 = now_ms();
        const char *payload = "file:///tmp/xdnd-probe.txt\r\n";
        while (now_ms() - t1 < 3000) {
            while (XPending(dpy)) {
                XEvent e2; XNextEvent(dpy, &e2);
                if (e2.type == SelectionRequest) {
                    ++selection_served;
                    XSelectionRequestEvent *r = &e2.xselectionrequest;
                    XChangeProperty(dpy, r->requestor, r->property ? r->property : r->target,
                                    r->target, 8, PropModeReplace,
                                    (const unsigned char *)payload, (int)strlen(payload));
                    XEvent ev; memset(&ev, 0, sizeof(ev));
                    ev.xselection.type = SelectionNotify;
                    ev.xselection.display = dpy;
                    ev.xselection.requestor = r->requestor;
                    ev.xselection.selection = r->selection;
                    ev.xselection.target = r->target;
                    ev.xselection.property = r->property ? r->property : r->target;
                    ev.xselection.time = r->time;
                    XSendEvent(dpy, r->requestor, False, NoEventMask, &ev);
                    XFlush(dpy);
                } else if (e2.type == ClientMessage && e2.xclient.message_type == a_xdnd_finished) {
                    finished_received = 1;
                }
            }
            if (finished_received) break;
            usleep(20000);
        }
    }

    // selection_served：我们服务了几次（>=1 说明目标真的来取 uri-list 了）
    printf("{\"status_received\":%s,\"accepted\":%s,\"selection_served\":%d,"
           "\"version\":%d,\"drop_sent\":%s,\"finished_received\":%s,\"error\":null}\n",
           status_received ? "true" : "false", accepted ? "true" : "false",
           selection_served,
           version, drop_sent ? "true" : "false", finished_received ? "true" : "false");

    XDestroyWindow(dpy, src);
    XCloseDisplay(dpy);
    return 0;
}
