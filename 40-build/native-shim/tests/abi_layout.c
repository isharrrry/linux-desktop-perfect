// ABI 布局自检：把 win32_abi.h 里静态断言的偏移**打印出来**。
//
// 编译期的 _Static_assert 已经保证断言不成立就编译不过；这个可执行文件是给
// 报告与 CI 留的**可读证据**——它能直接和托管侧的
// `Marshal.OffsetOf(typeof(MSG), "_hwnd")` 逐行对照（见
// src/Linux/tests/.../ManagedLayer.Tests/NativeMessageLayoutTests.cs）。
//
// 退出码：0 = 全部与托管侧一致；1 = 有不一致（人眼一眼能看出是哪一栏）。

#include "../src/win32_abi.h"

#include <stdio.h>
#include <string.h>

static int g_fail = 0;

static void row(const char *struct_name, const char *field, size_t off, size_t size)
{
    printf("  %-16s %-16s offset=%-4zu size=%zu\n", struct_name, field, off, size);
}

static void expect_size(const char *name, size_t got, size_t want)
{
    if (got != want) {
        printf("  !! %s 总长 %zu != 期望 %zu\n", name, got, want);
        g_fail = 1;
    }
}

int main(void)
{
    printf("== libwpfwin32 / Win32 shim ABI 布局（LP64）==\n");
    printf("sizeof(void*)=%zu  sizeof(long)=%zu  sizeof(wchar_t)=%zu\n\n",
           sizeof(void *), sizeof(long), sizeof(wchar_t));

    printf("MSG（托管 System.Windows.Interop.MSG，7 字段 Sequential）\n");
    row("MSG", "hwnd",    offsetof(WPF_MSG, hwnd),    sizeof(((WPF_MSG *)0)->hwnd));
    row("MSG", "message", offsetof(WPF_MSG, message), sizeof(((WPF_MSG *)0)->message));
    row("MSG", "wParam",  offsetof(WPF_MSG, wParam),  sizeof(((WPF_MSG *)0)->wParam));
    row("MSG", "lParam",  offsetof(WPF_MSG, lParam),  sizeof(((WPF_MSG *)0)->lParam));
    row("MSG", "time",    offsetof(WPF_MSG, time),    sizeof(((WPF_MSG *)0)->time));
    row("MSG", "pt_x",    offsetof(WPF_MSG, pt_x),    sizeof(((WPF_MSG *)0)->pt_x));
    row("MSG", "pt_y",    offsetof(WPF_MSG, pt_y),    sizeof(((WPF_MSG *)0)->pt_y));
    expect_size("MSG", sizeof(WPF_MSG), 48);

    printf("\nWNDCLASSEX_D（托管 NativeMethods.WNDCLASSEX_D，class + Unicode + 按值传）\n");
    row("WNDCLASSEX_D", "cbSize",         offsetof(WPF_WNDCLASSEX_D, cbSize),        4);
    row("WNDCLASSEX_D", "style",          offsetof(WPF_WNDCLASSEX_D, style),         4);
    row("WNDCLASSEX_D", "lpfnWndProc",    offsetof(WPF_WNDCLASSEX_D, lpfnWndProc),   8);
    row("WNDCLASSEX_D", "cbClsExtra",     offsetof(WPF_WNDCLASSEX_D, cbClsExtra),    4);
    row("WNDCLASSEX_D", "cbWndExtra",     offsetof(WPF_WNDCLASSEX_D, cbWndExtra),    4);
    row("WNDCLASSEX_D", "hInstance",      offsetof(WPF_WNDCLASSEX_D, hInstance),     8);
    row("WNDCLASSEX_D", "hIcon",          offsetof(WPF_WNDCLASSEX_D, hIcon),         8);
    row("WNDCLASSEX_D", "hCursor",        offsetof(WPF_WNDCLASSEX_D, hCursor),       8);
    row("WNDCLASSEX_D", "hbrBackground",  offsetof(WPF_WNDCLASSEX_D, hbrBackground), 8);
    row("WNDCLASSEX_D", "lpszMenuName",   offsetof(WPF_WNDCLASSEX_D, lpszMenuName),  8);
    row("WNDCLASSEX_D", "lpszClassName",  offsetof(WPF_WNDCLASSEX_D, lpszClassName), 8);
    row("WNDCLASSEX_D", "hIconSm",        offsetof(WPF_WNDCLASSEX_D, hIconSm),       8);
    expect_size("WNDCLASSEX_D", sizeof(WPF_WNDCLASSEX_D), 80);

    printf("\n其它跨边界 POD\n");
    row("RECT",             "left",  offsetof(WPF_RECT, left),  4);
    row("RECT",             "top",   offsetof(WPF_RECT, top),   4);
    row("RECT",             "right", offsetof(WPF_RECT, right), 4);
    row("RECT",             "bottom",offsetof(WPF_RECT, bottom),4);
    expect_size("RECT", sizeof(WPF_RECT), 16);
    // ⚠️ 托管 NativeMethods.WINDOWPOS 是**上游自己的简化形态**（2 指针 + 5 个 int，
    //    没有真 Win32 的 time/pt）。跨边界的是托管那份，所以这里按它列。
    //    本轮实测：按"真 Win32"写成 48 字节，跨边界断言直接报 Expected 48 / Actual 40。
    row("WINDOWPOS",        "hwnd",            offsetof(WPF_WINDOWPOS, hwnd),            8);
    row("WINDOWPOS",        "hwndInsertAfter", offsetof(WPF_WINDOWPOS, hwndInsertAfter), 8);
    row("WINDOWPOS",        "x",               offsetof(WPF_WINDOWPOS, x),               4);
    row("WINDOWPOS",        "y",               offsetof(WPF_WINDOWPOS, y),               4);
    row("WINDOWPOS",        "cx",              offsetof(WPF_WINDOWPOS, cx),              4);
    row("WINDOWPOS",        "cy",              offsetof(WPF_WINDOWPOS, cy),              4);
    row("WINDOWPOS",        "flags",           offsetof(WPF_WINDOWPOS, flags),           4);
    expect_size("WINDOWPOS", sizeof(WPF_WINDOWPOS), 40);
    row("TRACKMOUSEEVENT",  "cbSize",  offsetof(WPF_TRACKMOUSEEVENT, cbSize),      4);
    row("TRACKMOUSEEVENT",  "dwFlags", offsetof(WPF_TRACKMOUSEEVENT, dwFlags),     4);
    row("TRACKMOUSEEVENT",  "hwndTrack", offsetof(WPF_TRACKMOUSEEVENT, hwndTrack), 8);
    expect_size("TRACKMOUSEEVENT", sizeof(WPF_TRACKMOUSEEVENT), 24);
    row("PAINTSTRUCT",      "hdc",     offsetof(WPF_PAINTSTRUCT, hdc),     8);
    row("PAINTSTRUCT",      "fErase",  offsetof(WPF_PAINTSTRUCT, fErase),  4);
    row("PAINTSTRUCT",      "rcPaint", offsetof(WPF_PAINTSTRUCT, rcPaint), 16);
    expect_size("PAINTSTRUCT", sizeof(WPF_PAINTSTRUCT), 72);
    row("MONITORINFOEX",    "cbSize",    offsetof(WPF_MONITORINFOEX, cbSize),    4);
    row("MONITORINFOEX",    "rcMonitor", offsetof(WPF_MONITORINFOEX, rcMonitor), 16);
    row("MONITORINFOEX",    "rcWork",    offsetof(WPF_MONITORINFOEX, rcWork),    16);
    row("MONITORINFOEX",    "dwFlags",   offsetof(WPF_MONITORINFOEX, dwFlags),   4);
    // szDevice 在 Unix 上是 char[32]（CharSet.Auto == Ansi）→ 32 字节，不是 Windows 的 64
    row("MONITORINFOEX",    "szDevice",  offsetof(WPF_MONITORINFOEX, szDevice),  32);
    expect_size("MONITORINFOEX", sizeof(WPF_MONITORINFOEX), 72);
    expect_size("CHANGEFILTERSTRUCT", sizeof(WPF_CHANGEFILTERSTRUCT), 8);

    // ── SystemParametersInfo 的结构化出参（M7c/窗口启动路径的必需项）────────
    // lfWeight==0 会让 FontWeight.FromOpenTypeWeight(0) 抛异常 ⇒ 任何 WPF 窗口起不来。
    printf("\nLOGFONT / NONCLIENTMETRICS / ICONMETRICS（SystemParametersInfo 出参）\n");
    row("LOGFONT", "lfHeight",       offsetof(WPF_LOGFONT, lfHeight),       4);
    row("LOGFONT", "lfWeight",       offsetof(WPF_LOGFONT, lfWeight),       4);
    row("LOGFONT", "lfFaceName",     offsetof(WPF_LOGFONT, lfFaceName),    64);
    expect_size("LOGFONT", sizeof(WPF_LOGFONT), 92);
    row("NONCLIENTMETRICS", "cbSize",         offsetof(WPF_NONCLIENTMETRICS, cbSize),        4);
    row("NONCLIENTMETRICS", "lfCaptionFont",  offsetof(WPF_NONCLIENTMETRICS, lfCaptionFont), 92);
    row("NONCLIENTMETRICS", "lfSmCaptionFont",offsetof(WPF_NONCLIENTMETRICS, lfSmCaptionFont),92);
    row("NONCLIENTMETRICS", "lfMenuFont",     offsetof(WPF_NONCLIENTMETRICS, lfMenuFont),    92);
    row("NONCLIENTMETRICS", "lfStatusFont",   offsetof(WPF_NONCLIENTMETRICS, lfStatusFont),  92);
    row("NONCLIENTMETRICS", "lfMessageFont",  offsetof(WPF_NONCLIENTMETRICS, lfMessageFont), 92);
    expect_size("NONCLIENTMETRICS", sizeof(WPF_NONCLIENTMETRICS), 500);
    row("ICONMETRICS", "cbSize",   offsetof(WPF_ICONMETRICS, cbSize),   4);
    row("ICONMETRICS", "lfFont",   offsetof(WPF_ICONMETRICS, lfFont),  92);
    expect_size("ICONMETRICS", sizeof(WPF_ICONMETRICS), 108);

    printf("\n#U · Unicode 分类表（MILGetClassificationTables 的跨边界结构）\n");
    row("RAWCLASSIFICATION", "UnicodeClasses",  offsetof(WPF_RAW_CLASSIFICATION_TABLES, unicode_classes),      sizeof(void *));
    row("RAWCLASSIFICATION", "CharAttributes",  offsetof(WPF_RAW_CLASSIFICATION_TABLES, character_attributes), sizeof(void *));
    row("RAWCLASSIFICATION", "Mirroring",       offsetof(WPF_RAW_CLASSIFICATION_TABLES, mirroring),            sizeof(void *));
    row("RAWCLASSIFICATION", "CombiningMarks",  offsetof(WPF_RAW_CLASSIFICATION_TABLES, combining_marks),      sizeof(WPF_COMBINING_MARKS));
    expect_size("RAWCLASSIFICATION", sizeof(WPF_RAW_CLASSIFICATION_TABLES), 72);
    row("CHARATTR", "Script",    offsetof(WPF_CHAR_ATTR, script),     1);
    row("CHARATTR", "ItemClass", offsetof(WPF_CHAR_ATTR, item_class), 1);
    row("CHARATTR", "Flags",     offsetof(WPF_CHAR_ATTR, flags),      2);
    row("CHARATTR", "BreakType", offsetof(WPF_CHAR_ATTR, break_type), 1);
    row("CHARATTR", "BiDi",      offsetof(WPF_CHAR_ATTR, bidi),       1);
    row("CHARATTR", "LineBreak", offsetof(WPF_CHAR_ATTR, line_break), 2);
    expect_size("CHARATTR", sizeof(WPF_CHAR_ATTR), 8);
    row("COMBININGMARKS", "MarkIndexes",  offsetof(WPF_COMBINING_MARKS, combining_mark_indexes), 8);
    row("COMBININGMARKS", "CombChars",    offsetof(WPF_COMBINING_MARKS, combination_chars),      8);
    expect_size("COMBININGMARKS", sizeof(WPF_COMBINING_MARKS), 48);

    printf("\n结果：%s\n", g_fail ? "失败" : "全部一致（编译期 _Static_assert 亦已通过）");
    return g_fail;
}
