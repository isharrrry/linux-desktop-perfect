/* XDND 探针（D-011 第二半）：扮演"拖放源"，向目标窗口发 XdndEnter / XdndPosition，
 * 读回 XdndStatus —— 用来验证 shim 的 XDND 接收入口真的在工作。
 * 用法：xdnd-probe <window-id>   → 打印 JSON */
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    if (argc < 2) { printf("{\"ok\":false,\"error\":\"用法：xdnd-probe <window-id>\"}\n"); return 1; }
    Window target = (Window)strtoul(argv[1], NULL, 0);
    Display *d = XOpenDisplay(NULL);
    if (!d) { printf("{\"ok\":false,\"error\":\"XOpenDisplay 失败\"}\n"); return 2; }

    Atom a_enter = XInternAtom(d, "XdndEnter", False);
    Atom a_position = XInternAtom(d, "XdndPosition", False);
    Atom a_status = XInternAtom(d, "XdndStatus", False);
    Atom a_aware = XInternAtom(d, "XdndAware", False);
    Atom a_copy = XInternAtom(d, "XdndActionCopy", False);

    // ① 目标窗口是否声明了 XdndAware
    Atom actual; int fmt = 0; unsigned long n = 0, after = 0; unsigned char *prop = NULL;
    int aware = 0; unsigned long ver = 0;
    if (XGetWindowProperty(d, target, a_aware, 0, 1, False, XA_ATOM, &actual, &fmt,
                           &n, &after, &prop) == Success && prop) {
        aware = 1;
        if (n > 0) ver = ((unsigned long *)prop)[0];
        XFree(prop);
    }

    // ② 建一个 InputOnly 的源窗口并**监听 ClientMessage** ——
    //    Status 是发给**源**的；修前用 root 当源又没监听，所以永远收不到回执（实测踩过）。
    Window src = XCreateSimpleWindow(d, DefaultRootWindow(d), -10, -10, 1, 1, 0, 0, 0);
    XSelectInput(d, src, NoEventMask);   // InputOnly 也能收 ClientMessage（不需要事件掩码）
    XEvent ev;
    memset(&ev, 0, sizeof(ev));
    ev.xclient.type = ClientMessage;
    ev.xclient.window = target;
    ev.xclient.message_type = a_enter;
    ev.xclient.format = 32;
    ev.xclient.data.l[0] = (long)src;
    ev.xclient.data.l[1] = 5;          // version
    ev.xclient.data.l[2] = 0;          // 3 atoms, none
    XSendEvent(d, target, False, NoEventMask, &ev);
    XFlush(d);

    // ③ 发 XdndPosition
    memset(&ev, 0, sizeof(ev));
    ev.xclient.type = ClientMessage;
    ev.xclient.window = target;
    ev.xclient.message_type = a_position;
    ev.xclient.format = 32;
    ev.xclient.data.l[0] = (long)src;
    ev.xclient.data.l[2] = (10 << 16) | 20;   // x=10,y=20
    ev.xclient.data.l[4] = (long)a_copy;
    XSendEvent(d, target, False, NoEventMask, &ev);
    XFlush(d);

    // ④ 等 XdndStatus（最多 2 秒）
    int got_status = 0, accepted = 0;
    for (int i = 0; i < 20 && !got_status; ++i) {
        while (XPending(d)) {
            XEvent e2;
            XNextEvent(d, &e2);
            if (e2.type == ClientMessage && e2.xclient.message_type == a_status) {
                got_status = 1;
                accepted = (int)e2.xclient.data.l[1];
                break;
            }
        }
        usleep(100000);
    }

    printf("{\"ok\":%s,\"aware\":%s,\"version\":%lu,\"status_received\":%s,\"accepted\":%s}\n",
           (aware && got_status) ? "true" : "false",
           aware ? "true" : "false", ver,
           got_status ? "true" : "false", accepted ? "true" : "false");
    XDestroyWindow(d, src);
    XCloseDisplay(d);
    return (aware && got_status) ? 0 : 3;
}
