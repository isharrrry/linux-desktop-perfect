// 【ACC-017 / K-104】用 XTest 直接注入按键，绕开 xdotool 3.20160805.1 的怪癖。
// 用法：xtype <display> <key1> [key2 ...]      key 可以是单个字母/数字/space/Return
// 原理：XStringToKeysym -> XKeysymToKeycode -> XTestFakeKeyEvent(press/release)
#include <stdio.h>
#include <unistd.h>
#include <string.h>
#include <X11/Xlib.h>
#include <X11/keysym.h>
#include <X11/extensions/XTest.h>

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: %s <display> <key>...\n", argv[0]); return 2; }
    Display *d = XOpenDisplay(argv[1]);
    if (!d) { fprintf(stderr, "cannot open %s\n", argv[1]); return 1; }
    int ev, er, mj, mn;
    if (!XTestQueryExtension(d, &ev, &er, &mj, &mn)) { fprintf(stderr, "no XTest\n"); return 1; }
    for (int i = 2; i < argc; ++i) {
        KeySym ks = XStringToKeysym(argv[i]);
        if (ks == NoSymbol) { fprintf(stderr, "bad keysym: %s\n", argv[i]); continue; }
        KeyCode kc = XKeysymToKeycode(d, ks);
        fprintf(stderr, "[XTYPE] %-8s keysym=0x%lx keycode=%u\n", argv[i], (unsigned long)ks, kc);
        if (!kc) continue;
        XTestFakeKeyEvent(d, kc, True, 0);
        XFlush(d);
        usleep(40000);
        XTestFakeKeyEvent(d, kc, False, 0);
        XFlush(d);
        usleep(120000);
    }
    XCloseDisplay(d);
    return 0;
}
