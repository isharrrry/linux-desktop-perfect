/* 假合成器（Fake 合成器，09 §8 的桩件思路）：只做一件事 —— 持有 EWMH 的 `_NET_WM_CM_S0` selection。
 *
 * 用途：给 ACC-026 造「有合成器」这一极。真合成器（xcompmgr/picom）本机未装，而判据要的是
 * 「合成会话」这个**属性**，不是合成质量；持有 selection 是 EWMH 定义该属性的唯一方式。
 *
 * 用法：fake-compositor [display]   —— 持有一段时间（SIGTERM 退出）
 */
#include <X11/Xlib.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static volatile sig_atomic_t g_stop = 0;
static void on_term(int sig) { (void)sig; g_stop = 1; }

int main(int argc, char **argv)
{
    const char *display = argc > 1 ? argv[1] : NULL;
    Display *dpy = XOpenDisplay(display);
    if (!dpy) {
        fprintf(stderr, "ERROR fake-compositor: 无法打开 X display（%s）\n", display ? display : "$DISPLAY");
        return 2;
    }
    Window root = DefaultRootWindow(dpy);
    Atom cm = XInternAtom(dpy, "_NET_WM_CM_S0", False);
    Atom wm_name = XInternAtom(dpy, "_NET_WM_NAME", False);
    Window owner = XCreateSimpleWindow(dpy, root, -10, -10, 1, 1, 0, 0, 0);
    XStoreName(dpy, owner, "LDP-FakeCompositor");
    XChangeProperty(dpy, owner, wm_name, XInternAtom(dpy, "UTF8_STRING", False), 8, PropModeReplace,
                    (const unsigned char *)"LDP-FakeCompositor", 19);
    XSetSelectionOwner(dpy, cm, owner, CurrentTime);
    XSync(dpy, False);
    if (XGetSelectionOwner(dpy, cm) != owner) {
        fprintf(stderr, "ERROR fake-compositor: selection 授予失败（已有所有者？）\n");
        return 3;
    }
    printf("READY {\"selection\":\"_NET_WM_CM_S0\",\"owner\":\"0x%lx\"}\n", (unsigned long)owner);
    fflush(stdout);
    signal(SIGTERM, on_term);
    signal(SIGINT, on_term);
    while (!g_stop) usleep(100000);
    XSetSelectionOwner(dpy, cm, None, CurrentTime);
    XSync(dpy, False);
    XCloseDisplay(dpy);
    return 0;
}
