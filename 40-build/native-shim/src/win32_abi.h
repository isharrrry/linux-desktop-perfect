// WPF-on-Linux · M7b · Win32 shim 的 ABI 契约层
//
// ── 这个文件为什么单独存在 ────────────────────────────────────────────────
//   shim 里最容易出错、也最难定位的一类 bug 是「结构体布局对不上」：托管侧按
//   .NET 的 blittable 规则算出 48 字节，原生侧按自己的直觉写成 40 字节，
//   编译全过、跑起来字段全是垃圾。所以这里把每一个跨越托管/原生边界、且托管侧
//   **已经在编译产物里**的结构体逐字段抄一遍，然后用 `_Static_assert` 把
//   “推导出来的偏移”钉死。断言失败 = 编译失败，不给运行期留任何机会。
//
// ── 权威来源（不是猜的）────────────────────────────────────────────────────
//   · `System.Windows.Interop.MSG`        upstream WindowsBase/System/Windows/Interop/MSG.cs
//                                        （LayoutKind.Sequential，7 个字段，注释明写
//                                          "must agree EXACTLY with the native Win32 MSG"）
//   · `NativeMethods.WNDCLASSEX_D`        upstream Shared/MS/Win32/NativeMethodsOther.cs:607
//                                        （**class** + Sequential + CharSet.Unicode，按值传）
//   · `NativeMethods.RECT/POINT`          上游 Shared/MS/Win32（4×int32 / 2×int32）
//   · `NativeMethods.PAINTSTRUCT`         上游 Shared/MS/Win32
//   · `NativeMethods.WINDOWPOS`           上游 Shared/MS/Win32
//   · `NativeMethods.TRACKMOUSEEVENT`     上游 Shared/MS/Win32
//   · `NativeMethods.MONITORINFOEX`       上游 Shared/MS/Win32
//   · `NativeMethods.CHANGEFILTERSTRUCT`  上游 Shared/MS/Win32
//   · `MS.Win32.Win32Rect/Win32Point`     Program.cs 侧的同构镜像
//
// ── LP64 前提 ─────────────────────────────────────────────────────────────
//   全部按 Linux x86-64 / arm64（LP64，`long`/指针 8 字节，`int` 4 字节，
//   对齐 = 自身大小）推导。Windows 的 LLP64 差异只体现在 `long`，而托管侧
//   从不跨边界传 `long`（都用 IntPtr/Int32），所以 LP64 下逐字段一致。

#ifndef WPFWIN32_ABI_H
#define WPFWIN32_ABI_H

#include <stddef.h>
#include <stdint.h>

#if !defined(__x86_64__) && !defined(__aarch64__)
#  error "win32_abi.h 的偏移推导只覆盖 LP64（x86-64 / aarch64）；其它平台请先重新推导并补断言。"
#endif

// ── Win32 基础句柄类型（LP64 下统一为指针宽度）──────────────────────────────
typedef void    *HWND;          // == X11 Window（XID），见 win32_internal.h 的说明
typedef void    *HINSTANCE;
typedef void    *HMODULE;
typedef void    *HANDLE;
typedef void    *HGDIOBJ;
typedef void    *HDC;
typedef void    *HMENU;
typedef void    *HICON;
typedef void    *HCURSOR;
typedef void    *HBRUSH;
typedef void    *HBITMAP;
typedef void    *HPEN;
typedef void    *HFONT;
typedef void    *HRGN;
typedef void    *HKEY;
typedef void    *HLOCAL;
typedef void    *HGLOBAL;
typedef void    *HKL;           // HKL 在托管侧是 IntPtr
typedef void    *HIMC;
typedef void    *HMONITOR;
typedef void    *HPOWERNOTIFY;
typedef void    *FARPROC;
typedef uint8_t *LPSTR;
typedef uint8_t *LPWSTR;
typedef const uint8_t *LPCSTR;
typedef const uint8_t *LPCWSTR;
typedef void    *LPVOID;
typedef uint32_t UINT;
typedef int32_t  BOOL;
typedef uint16_t WORD;
typedef uint16_t ATOM;
typedef uint32_t DWORD;
typedef intptr_t WPARAM;
typedef intptr_t LPARAM;
typedef intptr_t LRESULT;
typedef uintptr_t UINT_PTR;
typedef intptr_t  INT_PTR;
typedef int32_t  LONG;
typedef uint32_t ULONG;
typedef uint8_t  BYTE;

// 窗口过程。签名与托管 `NativeMethods.WndProc` 逐参数一致：
//     public delegate IntPtr WndProc(IntPtr hWnd, Int32 msg, IntPtr wParam, IntPtr lParam);
// 返回值与四个参数都是 8/4/8/8 字节；Linux x86-64 只有一种整数调用约定，
// 所以 `CallingConvention.Winapi`（Unix 上折叠为 Cdecl）与这里天然一致。
typedef LRESULT (*WNDPROC)(HWND, UINT, WPARAM, LPARAM);
typedef void    (*TIMERPROC)(HWND, UINT, UINT_PTR, DWORD);

// ── MSG ────────────────────────────────────────────────────────────────────
// 托管侧 7 个私有字段的顺序（MSG.cs 注释：do not alter the number, order or size）：
//     _hwnd(IntPtr) _message(int) _wParam(IntPtr) _lParam(IntPtr) _time(int)
//     _pt_x(int) _pt_y(int)
// Sequential 布局下的偏移推导（LP64）：
//     _hwnd      0                     （8 字节，对齐 8）
//     _message   8                     （4 字节）
//     <pad>      12..15                （_wParam 需要 8 字节对齐）
//     _wParam    16
//     _lParam    24
//     _time      32                    （4 字节）
//     _pt_x      36
//     _pt_y      40
//     <pad>      44..47                （结构体对齐 8 → 总长 48）
// 与 Win32 的 `typedef struct tagMSG { HWND; UINT; WPARAM; LPARAM; DWORD; POINT; }` 一致。
typedef struct WPF_MSG {
    HWND     hwnd;
    uint32_t message;
    uint32_t _pad0;
    WPARAM   wParam;
    LPARAM   lParam;
    uint32_t time;
    int32_t  pt_x;
    int32_t  pt_y;
    uint32_t _pad1;
} WPF_MSG;

_Static_assert(sizeof(void *) == 8, "LP64 前提：指针必须 8 字节");
_Static_assert(offsetof(WPF_MSG, hwnd)    ==  0, "MSG.hwnd 必须在偏移 0");
_Static_assert(offsetof(WPF_MSG, message) ==  8, "MSG.message 必须在偏移 8");
_Static_assert(offsetof(WPF_MSG, wParam)  == 16, "MSG.wParam 必须在偏移 16");
_Static_assert(offsetof(WPF_MSG, lParam)  == 24, "MSG.lParam 必须在偏移 24");
_Static_assert(offsetof(WPF_MSG, time)    == 32, "MSG.time 必须在偏移 32");
_Static_assert(offsetof(WPF_MSG, pt_x)    == 36, "MSG.pt_x 必须在偏移 36");
_Static_assert(offsetof(WPF_MSG, pt_y)    == 40, "MSG.pt_y 必须在偏移 40");
_Static_assert(sizeof(WPF_MSG)            == 48, "MSG 总长必须是 48 字节（与托管 MSG 同）");

// ── WNDCLASSEX_D ───────────────────────────────────────────────────────────
// 托管侧是 **class**（引用类型）+ LayoutKind.Sequential + CharSet.Unicode，
// `IntRegisterClassEx(NativeMethods.WNDCLASSEX_D wc_d)` 按值传 → 原生存根指针。
// 字段（照抄 NativeMethodsOther.cs:607 的顺序与类型）：
//     int cbSize; int style; WndProc lpfnWndProc; int cbClsExtra; int cbWndExtra;
//     IntPtr hInstance; IntPtr hIcon; IntPtr hCursor; IntPtr hbrBackground;
//     string lpszMenuName; string lpszClassName; IntPtr hIconSm;
// CharSet.Unicode → 两个 string 在 Linux 上同样 marshal 成 **UTF-16**（2 字节/单元），
// 不是 4 字节 wchar_t。这是本次实测确认的一点，写错会得到空类名。
// 偏移推导：
//     cbSize       0   style        4   lpfnWndProc  8   cbClsExtra  16  cbWndExtra 20
//     hInstance   24   hIcon       32   hCursor     40   hbrBackground 48
//     lpszMenuName 56  lpszClassName 64 hIconSm     72   → 总长 80
typedef struct WPF_WNDCLASSEX_D {
    int32_t  cbSize;
    int32_t  style;
    WNDPROC  lpfnWndProc;
    int32_t  cbClsExtra;
    int32_t  cbWndExtra;
    HINSTANCE hInstance;
    HICON    hIcon;
    HCURSOR  hCursor;
    HBRUSH   hbrBackground;
    uint16_t *lpszMenuName;    // UTF-16
    uint16_t *lpszClassName;   // UTF-16
    HICON    hIconSm;
} WPF_WNDCLASSEX_D;

_Static_assert(offsetof(WPF_WNDCLASSEX_D, cbSize)        ==  0, "WNDCLASSEX_D.cbSize");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, style)         ==  4, "WNDCLASSEX_D.style");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, lpfnWndProc)   ==  8, "WNDCLASSEX_D.lpfnWndProc");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, cbClsExtra)    == 16, "WNDCLASSEX_D.cbClsExtra");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, cbWndExtra)    == 20, "WNDCLASSEX_D.cbWndExtra");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, hInstance)     == 24, "WNDCLASSEX_D.hInstance");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, hIcon)         == 32, "WNDCLASSEX_D.hIcon");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, hCursor)       == 40, "WNDCLASSEX_D.hCursor");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, hbrBackground) == 48, "WNDCLASSEX_D.hbrBackground");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, lpszMenuName)  == 56, "WNDCLASSEX_D.lpszMenuName");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, lpszClassName) == 64, "WNDCLASSEX_D.lpszClassName");
_Static_assert(offsetof(WPF_WNDCLASSEX_D, hIconSm)       == 72, "WNDCLASSEX_D.hIconSm");
_Static_assert(sizeof(WPF_WNDCLASSEX_D)                  == 80, "WNDCLASSEX_D 总长必须是 80");

// ── 其它跨边界 POD ─────────────────────────────────────────────────────────
typedef struct { int32_t x, y; }                       WPF_POINT;      // 8
typedef struct { int32_t left, top, right, bottom; }   WPF_RECT;       // 16
typedef struct { int32_t cx, cy; }                     WPF_SIZE;       // 8

// ⚠️ WINDOWPOS 是「**托管侧的形状 ≠ 真 Win32 形状**」的活例子，必须按托管侧写。
//   托管 `MS.Win32.NativeMethods.WINDOWPOS`（NativeMethodsCLR.cs:2282）是：
//       IntPtr hwnd; IntPtr hwndInsertAfter; int x; int y; int cx; int cy; int flags;
//   而真正的 Win32 WINDOWPOS 是
//       { HWND hwnd; HWND hwndInsertAfter; int x,y,cx,cy; UINT flags; } —— 名字一样，
//   但**上游这份是 40 字节的简化/过时形态，没有 time/pt**。
//   跨边界的是托管那份，所以原生必须跟着它走。（本工程把差异写成断言而不是注释，
//   就是为了让"到底谁对"这件事在编译期就有答案。）
typedef struct {
    HWND hwnd;              // 0
    HWND hwndInsertAfter;   // 8
    int32_t x;              // 16
    int32_t y;              // 20
    int32_t cx;             // 24
    int32_t cy;             // 28
    int32_t flags;          // 32
} WPF_WINDOWPOS;           // sizeof = 40

typedef struct {
    DWORD cbSize;
    DWORD dwFlags;
    HWND  hwndTrack;
    DWORD dwHoverTime;
} WPF_TRACKMOUSEEVENT;                                                  // 24

typedef struct {
    HDC  hdc;
    BOOL fErase;
    WPF_RECT rcPaint;
    BOOL fRestore;
    BOOL fIncUpdate;
    BYTE rgbReserved[32];
} WPF_PAINTSTRUCT;                                                       // 72

// ⚠️ MONITORINFOEX 的**长度随 CharSet 变**，这是本轮最反直觉的一条：
//   托管 `NativeMethods.MONITORINFOEX`（NativeMethodsCLR.cs:1515）标注的是
//       [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 4)]
//       int cbSize; RECT rcMonitor; RECT rcWork; int dwFlags;
//       [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] char[] szDevice;
//   `CharSet.Auto` 在 **Windows 上是 Unicode** → szDevice 是 32×2 = 64 字节 → 总长 104；
//   在 **Unix 上折叠为 Ansi** → szDevice 是 32×1 = 32 字节 → 总长 **72**。
//   实测确认：`Marshal.SizeOf(typeof(MONITORINFOEX))` 在 Linux 上返回 72。
//   所以这里按 **72 / char[32]** 写，并与托管侧对齐；这也是为什么这个结构体
//   必须由跨边界断言（Win32AbiLayoutTests）钉住，而不是靠"我记得 Win32 是 104"。
typedef struct {
    int32_t  cbSize;        // 0
    WPF_RECT rcMonitor;     // 4   (Pack=4 ⇒ RECT 紧跟在 int 后面)
    WPF_RECT rcWork;        // 20
    int32_t  dwFlags;       // 36
    char     szDevice[32];  // 40  UTF-8/ANSI（Unix 上 CharSet.Auto == Ansi）
} WPF_MONITORINFOEX;        // sizeof = 72

typedef struct { DWORD cbSize; DWORD ExtStatus; } WPF_CHANGEFILTERSTRUCT; // 8

typedef struct { int32_t x, y; } WPF_MOUSEMOVEPOINT;                     // 8

// ── LOGFONT / NONCLIENTMETRICS ─────────────────────────────────────────────
// 【为什么这几个结构体必须在这里逐字节钉死】
//   `SystemParametersInfo(SPI_GETNONCLIENTMETRICS, …)` 的出参是**托管侧的 class**
//   `MS.Win32.NativeMethods.NONCLIENTMETRICS`（`[In, Out]` 传引用 → 原生写、托管读回）。
//   写错一个偏移就会踩掉调用方的缓冲区；**而"什么都不写"的后果更严重**：
//   实测（T3 跑 HelloWpf）：出参不填 → `lfMessageFont.lfWeight == 0` →
//   `SystemFonts.MessageFontWeight` → `FontWeight.FromOpenTypeWeight(0)` →
//   `ArgumentOutOfRangeException`，**任何 WPF 窗口在任何 Linux 机器上都起不来**
//   （`SystemFonts` 在 TextElement/FrameworkElement/Window 的静态构造链上，与 XAML 无关）。
//
// 托管 `LOGFONT`（NativeMethodsCLR.cs:1682）是 StructLayout.Sequential + **CharSet.Unicode**：
//     int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;   // 0,4,8,12,16
//     byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet,
//          lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;  // 20..27
//     [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] string lfFaceName;
// ByValTStr 配 CharSet.Unicode ⇒ 32 个 **UTF-16 单元** = 64 字节 ⇒ 总长 **92**。
// （注意与 MONITORINFOEX 的 char[] 形成对照：那个用 ByValArray+Auto，在 Unix 上是 32 字节。
//   同一个工程里两种字段的编码规则不同，只能逐个核对，不能类推。）
typedef struct {
    int32_t  lfHeight;         // 0   负值 = 字符高度（Win32 约定）
    int32_t  lfWidth;          // 4
    int32_t  lfEscapement;     // 8
    int32_t  lfOrientation;    // 12
    int32_t  lfWeight;         // 16  OpenType 权重 1..999（400 = Normal）
    uint8_t  lfItalic;         // 20
    uint8_t  lfUnderline;      // 21
    uint8_t  lfStrikeOut;      // 22
    uint8_t  lfCharSet;        // 23
    uint8_t  lfOutPrecision;   // 24
    uint8_t  lfClipPrecision;  // 25
    uint8_t  lfQuality;        // 26
    uint8_t  lfPitchAndFamily; // 27
    uint16_t lfFaceName[32];   // 28  UTF-16（含结尾 0）
} WPF_LOGFONT;                 // 92

// 托管 NONCLIENTMETRICS（NativeMethodsCLR.cs:1569，class + Sequential）
//   cbSize 0 · iBorderWidth 4 · iScrollWidth 8 · iScrollHeight 12 ·
//   iCaptionWidth 16 · iCaptionHeight 20 · lfCaptionFont 24 ·
//   iSmCaptionWidth 116 · iSmCaptionHeight 120 · lfSmCaptionFont 124 ·
//   iMenuWidth 216 · iMenuHeight 220 · lfMenuFont 224 · lfStatusFont 316 · lfMessageFont 408
typedef struct {
    int32_t     cbSize;             // 0   托管侧填 Marshal.SizeOf = 500
    int32_t     iBorderWidth;       // 4
    int32_t     iScrollWidth;       // 8
    int32_t     iScrollHeight;      // 12
    int32_t     iCaptionWidth;      // 16
    int32_t     iCaptionHeight;     // 20
    WPF_LOGFONT lfCaptionFont;      // 24
    int32_t     iSmCaptionWidth;    // 116
    int32_t     iSmCaptionHeight;   // 120
    WPF_LOGFONT lfSmCaptionFont;    // 124
    int32_t     iMenuWidth;         // 216
    int32_t     iMenuHeight;        // 220
    WPF_LOGFONT lfMenuFont;         // 224
    WPF_LOGFONT lfStatusFont;       // 316
    WPF_LOGFONT lfMessageFont;      // 408
} WPF_NONCLIENTMETRICS;             // 500

// 托管 ICONMETRICS（NativeMethodsCLR.cs:1597）：cbSize 0 · iHorzSpacing 4 ·
// iVertSpacing 8 · iTitleWrap 12 · lfFont 16 → 108
typedef struct {
    int32_t     cbSize;         // 0
    int32_t     iHorzSpacing;   // 4
    int32_t     iVertSpacing;   // 8
    int32_t     iTitleWrap;     // 12
    WPF_LOGFONT lfFont;         // 16
} WPF_ICONMETRICS;              // 108

// 托管 HIGHCONTRAST_I（NativeMethodsCLR.cs:2242）：16
typedef struct { int32_t cbSize; int32_t dwFlags; void *lpszDefaultScheme; } WPF_HIGHCONTRAST_I;

// 托管 ANIMATIONINFO（NativeMethodsOther.cs:1076）：8
typedef struct { int32_t cbSize; int32_t iMinAnimate; } WPF_ANIMATIONINFO;

// ══════════════════════════════════════════════════════════════════════════
//  M7c #U · Unicode 分类表（`MILGetClassificationTables` 的跨边界结构）
// ══════════════════════════════════════════════════════════════════════════
// 托管侧：`PresentationCore/MS/internal/Classification.cs:161-199`
//   · `CharacterAttribute` 是 `[StructLayout(LayoutKind.Sequential, Pack=1)]`
//     ⇒ **必须用 __attribute__((packed))**，否则 C 会给 ushort 对齐填充、从 8 变 12。
//   · `RawClassificationTables` 是默认 Sequential（x64 自然对齐）⇒ 3 个指针 + 一个
//     48 字节的嵌套结构 = 72。
//   · `UnicodeClasses` 的读法见 Classification.cs:219-236：两级 `short**`，
//     **第二级条目既可能是小整数（<472 ⇒ 就是类值本身）也可能是叶子指针**。
//     所以 C 侧这一格的宽度必须是**指针宽度**（LP64 下 8 字节），不能写成 short。
//
// `WPF_UNICODE_CLASS_MAX` = 托管 `UnicodeClass.Max`（UnicodeClasses.cs:164）。
// **它是这套表的生命线**：类值必须 < 它，否则托管侧会把类值当成指针去解引用。
#define WPF_UNICODE_CLASS_MAX 0x1D8

// 托管 `MS.Internal.CharacterAttribute`（UnicodeClasses.cs:176-184，Pack=1）
typedef struct __attribute__((packed)) {    uint8_t  script;        // ScriptID
    uint8_t  item_class;    // ItemClass
    uint16_t flags;         // CharacterAttributeFlags
    uint8_t  break_type;    // CharBreakingType
    uint8_t  bidi;          // DirectionClass
    int16_t  line_break;    // short
} WPF_CHAR_ATTR;            // 8

// 托管 `Classification.CombiningMarksClassificationData`（Classification.cs:169-179）
typedef struct {
    void    *combining_chars_indexes;                   // 0
    int32_t  combining_chars_indexes_table_length;      // 8
    int32_t  combining_chars_indexes_table_segment_length; // 12
    void    *combining_mark_indexes;                    // 16
    int32_t  combining_mark_indexes_table_length;       // 24
    int32_t  _pad0;                                     // 28（自然对齐填充）
    void    *combination_chars;                         // 32
    int32_t  combination_chars_base_count;              // 40
    int32_t  combination_chars_mark_count;              // 44
} WPF_COMBINING_MARKS;      // 48

// 托管 `Classification.RawClassificationTables`（Classification.cs:186-192）
typedef struct {
    void                 *unicode_classes;       // 0  ← short***（17 个平面）
    void                 *character_attributes;  // 8  ← CharacterAttribute[类值]
    void                 *mirroring;             // 16
    WPF_COMBINING_MARKS   combining_marks;       // 24
} WPF_RAW_CLASSIFICATION_TABLES;                 // 72

// 托管 `MS.Internal.TextFormatting.EscStringInfo`（LineServices.cs:677-685）：
//   **6 个 IntPtr**（指向宽字符的指针，不是字符本身！）—— 托管侧把每个当"1 字符缓冲区"用：
//   `new CharacterBufferRange((char*)TextStore.PwchParaSeparator, 1)`（FormatSettings.cs:211/224/237/244）。
//   所以实现必须交回**有效指针**：给 NULL 会在下一次解引用时崩。
typedef struct {
    void *szParaSeparator;      // 0
    void *szLineSeparator;      // 8
    void *szHidden;             // 16
    void *szNbsp;               // 24
    void *szObjectTerminator;   // 32
    void *szObjectReplacement;  // 40
} WPF_ESCSTRING;                // 48

_Static_assert(sizeof(WPF_ESCSTRING) == 48, "EscStringInfo 48（6 个指针）");
_Static_assert(offsetof(WPF_ESCSTRING, szNbsp)             == 24, "EscStringInfo.szNbsp");
_Static_assert(offsetof(WPF_ESCSTRING, szObjectReplacement) == 40, "EscStringInfo.szObjectReplacement");
_Static_assert(sizeof(WPF_CHAR_ATTR)  ==  8, "CharacterAttribute 8（Pack=1）");_Static_assert(offsetof(WPF_CHAR_ATTR, script)     == 0, "CharacterAttribute.Script");
_Static_assert(offsetof(WPF_CHAR_ATTR, item_class) == 1, "CharacterAttribute.ItemClass");
_Static_assert(offsetof(WPF_CHAR_ATTR, flags)      == 2, "CharacterAttribute.Flags");
_Static_assert(offsetof(WPF_CHAR_ATTR, break_type) == 4, "CharacterAttribute.BreakType");
_Static_assert(offsetof(WPF_CHAR_ATTR, bidi)       == 5, "CharacterAttribute.BiDi");
_Static_assert(offsetof(WPF_CHAR_ATTR, line_break) == 6, "CharacterAttribute.LineBreak");
_Static_assert(sizeof(WPF_COMBINING_MARKS)              == 48, "CombiningMarksClassificationData 48");
_Static_assert(offsetof(WPF_COMBINING_MARKS, combining_mark_indexes) == 16, "CombiningMarks.CombiningMarkIndexes");
_Static_assert(offsetof(WPF_COMBINING_MARKS, combination_chars)      == 32, "CombiningMarks.CombinationChars");
_Static_assert(sizeof(WPF_RAW_CLASSIFICATION_TABLES)    == 72, "RawClassificationTables 72");
_Static_assert(offsetof(WPF_RAW_CLASSIFICATION_TABLES, unicode_classes)      ==  0, "Raw.UnicodeClasses");
_Static_assert(offsetof(WPF_RAW_CLASSIFICATION_TABLES, character_attributes) ==  8, "Raw.CharacterAttributes");
_Static_assert(offsetof(WPF_RAW_CLASSIFICATION_TABLES, mirroring)            == 16, "Raw.Mirroring");
_Static_assert(offsetof(WPF_RAW_CLASSIFICATION_TABLES, combining_marks)      == 24, "Raw.CombiningMarksClassification");


_Static_assert(sizeof(WPF_LOGFONT)            ==  92, "LOGFONT 92（lfFaceName 是 32 个 UTF-16 单元）");
_Static_assert(offsetof(WPF_LOGFONT, lfWeight)    == 16, "LOGFONT.lfWeight @16 —— lfWeight==0 会让 FontWeight.FromOpenTypeWeight 抛异常");
_Static_assert(offsetof(WPF_LOGFONT, lfFaceName)  == 28, "LOGFONT.lfFaceName @28");
_Static_assert(sizeof(WPF_NONCLIENTMETRICS)   == 500, "NONCLIENTMETRICS 500");
_Static_assert(offsetof(WPF_NONCLIENTMETRICS, lfCaptionFont)  ==  24, "NONCLIENTMETRICS.lfCaptionFont");
_Static_assert(offsetof(WPF_NONCLIENTMETRICS, lfSmCaptionFont) == 124, "NONCLIENTMETRICS.lfSmCaptionFont");
_Static_assert(offsetof(WPF_NONCLIENTMETRICS, lfMenuFont)     == 224, "NONCLIENTMETRICS.lfMenuFont");
_Static_assert(offsetof(WPF_NONCLIENTMETRICS, lfStatusFont)   == 316, "NONCLIENTMETRICS.lfStatusFont");
_Static_assert(offsetof(WPF_NONCLIENTMETRICS, lfMessageFont)  == 408, "NONCLIENTMETRICS.lfMessageFont");
_Static_assert(sizeof(WPF_ICONMETRICS)        == 108, "ICONMETRICS 108");
_Static_assert(offsetof(WPF_ICONMETRICS, lfFont) == 16, "ICONMETRICS.lfFont");
_Static_assert(sizeof(WPF_HIGHCONTRAST_I)     ==  16, "HIGHCONTRAST_I 16");
_Static_assert(sizeof(WPF_ANIMATIONINFO)      ==   8, "ANIMATIONINFO 8");

_Static_assert(sizeof(WPF_POINT)   ==  8, "POINT 8");
_Static_assert(sizeof(WPF_RECT)    == 16, "RECT 16");
_Static_assert(offsetof(WPF_WINDOWPOS, hwnd)            ==  0, "WINDOWPOS.hwnd");
_Static_assert(offsetof(WPF_WINDOWPOS, hwndInsertAfter) ==  8, "WINDOWPOS.hwndInsertAfter");
_Static_assert(offsetof(WPF_WINDOWPOS, x)               == 16, "WINDOWPOS.x");
_Static_assert(offsetof(WPF_WINDOWPOS, y)               == 20, "WINDOWPOS.y");
_Static_assert(offsetof(WPF_WINDOWPOS, cx)              == 24, "WINDOWPOS.cx");
_Static_assert(offsetof(WPF_WINDOWPOS, cy)              == 28, "WINDOWPOS.cy");
_Static_assert(offsetof(WPF_WINDOWPOS, flags)           == 32, "WINDOWPOS.flags");
_Static_assert(sizeof(WPF_WINDOWPOS)                    == 40, "WINDOWPOS 40（托管侧形态）");
_Static_assert(sizeof(WPF_TRACKMOUSEEVENT)      == 24, "TRACKMOUSEEVENT 24");
_Static_assert(offsetof(WPF_PAINTSTRUCT, rcPaint) == 12, "PAINTSTRUCT.rcPaint");
_Static_assert(sizeof(WPF_PAINTSTRUCT)          == 72, "PAINTSTRUCT 72");
_Static_assert(offsetof(WPF_MONITORINFOEX, rcMonitor) ==  4, "MONITORINFOEX.rcMonitor");
_Static_assert(offsetof(WPF_MONITORINFOEX, rcWork)    == 20, "MONITORINFOEX.rcWork");
_Static_assert(offsetof(WPF_MONITORINFOEX, dwFlags)   == 36, "MONITORINFOEX.dwFlags");
_Static_assert(offsetof(WPF_MONITORINFOEX, szDevice)  == 40, "MONITORINFOEX.szDevice");
_Static_assert(sizeof(WPF_MONITORINFOEX)              ==  72, "MONITORINFOEX 72（Unix 上 CharSet.Auto==Ansi，szDevice 是 32 字节）");
_Static_assert(sizeof(WPF_CHANGEFILTERSTRUCT)         ==   8, "CHANGEFILTERSTRUCT 8");

#endif // WPFWIN32_ABI_H
