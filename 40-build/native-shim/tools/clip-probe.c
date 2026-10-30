/* 剪贴板探针（ACC-012 的被测对象探针）
 *
 * 为什么需要它：WPF 的 `System.Windows.Clipboard` 走 OLE COM（Linux 上不存在），
 * 所以剪贴板实现的可验证面是 shim 的 **Win32 面**（`OpenClipboard`/`SetClipboardData`/…）。
 * 这个探针直接调这些导出，用两个进程做真实往返：
 *
 *   A: clip-probe --set "hello" --hold 3000   # 设值并持有 CLIPBOARD selection（处理 SelectionRequest）
 *   B: clip-probe --get                        # 从 A 的 selection 取回（XConvertSelection 同步等）
 *
 * 输出单行 JSON，便于判据解析；失败走非零退出码。
 */
#include <X11/Xlib.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern int OpenClipboard(void *hwnd);
extern int CloseClipboard(void);
extern int EmptyClipboard(void);
extern void *SetClipboardData(unsigned format, void *data);
extern void *GetClipboardData(unsigned format);
extern int IsClipboardFormatAvailable(unsigned format);
/* shim 内部（非导出）：SelectionRequest 的处理入口 */
extern void wpf_clip_on_selection_request(const void *xselectionrequest);
/* shim 内部：在**shim 自己的 X 连接**上抽干并处理 SelectionRequest（调用方连接看不到那些事件） */
extern int wpf_clip_pump(int max_ms);

#define CF_TEXT_ 1u
#define CF_UNICODETEXT_ 13u

static volatile sig_atomic_t g_stop = 0;
static void on_term(int sig) { (void)sig; g_stop = 1; }

int main(int argc, char **argv)
{
    const char *set_val = NULL;
    int hold_ms = 0;
    int do_get = 0;
    for (int i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "--set") == 0 && i + 1 < argc) set_val = argv[++i];
        else if (strcmp(argv[i], "--hold") == 0 && i + 1 < argc) hold_ms = atoi(argv[++i]);
        else if (strcmp(argv[i], "--get") == 0) do_get = 1;
    }

    /* 同进程往返自测：设值后立刻读回（验证 Win32 面的本地缓冲路径，不涉及 X 跨进程） */
    if (set_val && do_get) {
        if (!OpenClipboard(NULL)) { fprintf(stderr, "OpenClipboard 失败\n"); return 2; }
        EmptyClipboard();
        void *h = SetClipboardData(CF_TEXT_, (void *)set_val);
        void *g = h ? GetClipboardData(CF_UNICODETEXT_) : NULL;
        CloseClipboard();
        int ok = 0;
        if (g) {
            const uint16_t *u = (const uint16_t *)g;
            char buf[1024]; size_t o = 0;
            for (size_t i = 0; u[i] && o + 4 < sizeof(buf); ++i) buf[o++] = (char)(u[i] < 0x80 ? u[i] : '?');
            buf[o] = '\0';
            ok = (strcmp(buf, set_val) == 0);
            free(g);
        }
        printf("{\"op\":\"selftest\",\"set\":\"%s\",\"roundtrip_ok\":%s}\n", set_val, ok ? "true" : "false");
        return ok ? 0 : 6;
    }

    if (set_val) {
        if (!OpenClipboard(NULL)) { fprintf(stderr, "OpenClipboard 失败\n"); return 2; }
        EmptyClipboard();
        void *h = SetClipboardData(CF_TEXT_, (void *)set_val);
        int avail = IsClipboardFormatAvailable(CF_TEXT_);
        CloseClipboard();
        if (!h) { fprintf(stderr, "SetClipboardData 失败\n"); return 3; }
        printf("{\"op\":\"set\",\"value\":\"%s\",\"available\":%s}\n", set_val, avail ? "true" : "false");
        fflush(stdout);
        if (hold_ms > 0) {
            /* 持有 selection：把 X 事件过一遍，SelectionRequest 交给 shim 处理 */
            signal(SIGTERM, on_term);
            signal(SIGINT, on_term);
            (void)wpf_clip_pump(1);   // 预热：确认 shim 的 X 连接可用
            for (int waited = 0; waited < hold_ms && !g_stop; waited += 20) {
                int n = wpf_clip_pump(20);
                if (n && getenv("CLIP_PROBE_TRACE")) fprintf(stderr, "[PROBE-A] handled %d SelectionRequest\n", n);
            }
        }
        return 0;
    }

    if (do_get) {
        if (!OpenClipboard(NULL)) { fprintf(stderr, "OpenClipboard 失败\n"); return 2; }
        void *h = GetClipboardData(CF_UNICODETEXT_);
        CloseClipboard();
        if (!h) {
            printf("{\"op\":\"get\",\"value\":null}\n");
            return 5;
        }
        const uint16_t *u = (const uint16_t *)h;
        printf("{\"op\":\"get\",\"value\":\"");
        for (size_t i = 0; u && u[i]; ++i) {
            uint32_t cp = u[i];
            if (cp >= 0xD800 && cp <= 0xDBFF && u[i + 1] >= 0xDC00 && u[i + 1] <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (u[i + 1] - 0xDC00);
                ++i;
            }
            if (cp == '"' || cp == '\\') printf("\\%c", (char)cp);
            else if (cp < 0x80) putchar((int)cp);
            else if (cp < 0x800) { putchar(0xC0 | (cp >> 6)); putchar(0x80 | (cp & 0x3F)); }
            else if (cp < 0x10000) { putchar(0xE0 | (cp >> 12)); putchar(0x80 | ((cp >> 6) & 0x3F)); putchar(0x80 | (cp & 0x3F)); }
            else { putchar(0xF0 | (cp >> 18)); putchar(0x80 | ((cp >> 12) & 0x3F)); putchar(0x80 | ((cp >> 6) & 0x3F)); putchar(0x80 | (cp & 0x3F)); }
        }
        printf("\"}\n");
        free(h);
        return 0;
    }

    fprintf(stderr, "用法：clip-probe --set <text> [--hold <ms>] | --get\n");
    return 1;
}
