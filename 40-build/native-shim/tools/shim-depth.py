#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7b：把 libwpfwin32.so 的导出符号按**实现深度**分档（真实现 / 降级 / 返回失败），
供 docs/U2-M7b-report.md 引用。数字是机械算出来的，不是手数的。

    python3 src/WpfGfx.Linux.Native/tools/shim-depth.py

分档口径
--------
* 统计单位 = **逻辑函数**（不是导出符号）。同一个函数的字符集/包装变体
  （`Foo` / `FooA` / `FooW` / `FooWrapper` / `FooInternal`）合成一条。
* `真实现`   —— 有真实数据面或真实状态机，返回值/副作用可以被断言
                （消息泵、窗口表、GWL_*、X11 建窗与事件翻译、UTF-16↔UTF-8、
                  GetProcAddress、性能计数器、DPI=96 的"真话"常量…）。
* `降级`     —— 在 Linux 上没有可操作的对象，返回一个"上层不会误入错误分支"的
                值，且该值在当前语境下是真话或哨兵（GDI 哨兵句柄、光标哨兵、
                无副作用开关、MessageBox 不阻塞直接返回 IDOK…）。
* `返回失败` —— 语义确实表达不了，返回 Win32 失败码 + 设 LastError。
                **绝不返回看起来能用的假句柄**。
* `桥接`     —— `WpfLinuxWin32_*`，给 M7c 用的扩展导出，不属于 Win32 API 面。
"""

import os
import re
import sys
import collections

HERE = os.path.dirname(os.path.abspath(__file__))
NATIVE_ROOT = os.path.normpath(os.path.join(HERE, ".."))
EXPORTS = os.path.join(NATIVE_ROOT, "bin", "exports.txt")

REAL = """
GetMessage PeekMessage TranslateMessage DispatchMessage PostMessage PostQuitMessage
SendMessage SendMessageTimeout PostThreadMessage MsgWaitForMultipleObjectsEx
SetTimer SetTimerInternal KillTimer RegisterWindowMessage GetMessageExtraInfo
SetMessageExtraInfo GetMessagePos GetMessageTime GetTickCount GetTickCount64
QueryPerformanceCounter QueryPerformanceFrequency
RegisterClassEx UnregisterClass CreateWindowEx DestroyWindow ShowWindow ShowWindowAsync
MoveWindow SetWindowPos GetWindowLongPtr GetWindowLong SetWindowLongPtr SetWindowLong
DefWindowProc DefWindowProcWorker CallWindowProc GetClientRect GetWindowRect
ScreenToClient ClientToScreen MapWindowPoints IsWindow IsWindowVisible IsWindowUnicode
IsWindowEnabled IsChild GetParent SetParent GetAncestor GetWindow GetDesktopWindow
GetActiveWindow GetForegroundWindow SetActiveWindow SetForegroundWindow GetFocus
SetFocus GetCapture SetCapture ReleaseCapture GetWindowThreadProcessId GetClassName
SetWindowText GetWindowText GetWindowTextLength SetProp GetProp RemoveProp EnableWindow
GetSystemMetrics GetMonitorInfo EnumDisplayMonitors MonitorFromWindow MonitorFromPoint
MonitorFromRect TrackMouseEvent GetCursorPos WindowFromPoint EnumThreadWindows
AdjustWindowRectEx MultiByteToWideChar GetModuleFileName LoadLibrary LoadLibraryEx
FreeLibrary GetModuleHandle GetModuleHandleEx GetProcAddress GetCurrentThreadId
GetDpiForWindow GetDpiForSystem GetDpiForMonitor SetProcessDPIAware IsProcessDPIAware
EnableNonClientDpiScaling GetWindowDpiAwarenessContext SetThreadDpiAwarenessContext
GetThreadDpiHostingBehavior AreDpiAwarenessContextsEqual
GetWindowPlacement SetWindowPlacement GetProcAddressNoThrow IntGetProcAddress
"""

DEGRADE = """
ShowWindowAsync GetDC GetWindowDC CreateCompatibleDC ReleaseDC DeleteDC GetStockObject
CriticalGetStockObject CreateCompatibleBitmap CriticalCreateCompatibleBitmap CreateBitmap
CreateDIBSection SelectObject DeleteObject GetObject GetBitmapBits GetDeviceCaps
ExtEscape FillRect StartDoc EndDoc StartPage EndPage SetEnhMetaFileBits BeginPaint
EndPaint InvalidateRect ValidateRect UpdateWindow RedrawWindow PrintWindow
LoadCursor LoadImage ExtractIconEx GetCursor SetCursor ShowCursor DestroyCursor
CreateIconIndirect DestroyIcon CreateCaret ShowCaret HideCaret DestroyCaret SetCaretPos
GetCaretBlinkTime GetKeyState GetAsyncKeyState GetKeyboardLayout GetKeyboardLayoutList
ActivateKeyboardLayout MapVirtualKey GetDoubleClickTime keybd_event mouse_event
MessageBeep SystemParametersInfo ChangeWindowMessageFilter ChangeWindowMessageFilterEx
NotifyWinEvent GetSystemPowerStatus GetOEMCP GetACP ProcessIdToSessionId DeactivateActCtx
UnregisterPowerSettingNotification MessageBox GetCurrentProcess IsDebuggerPresent
CloseHandle LocalFree GetSysColor LoadImageCursor
IsWindowsXPOrGreater IsWindowsXPSP1OrGreater IsWindowsXPSP2OrGreater IsWindowsXPSP3OrGreater
IsWindowsVistaOrGreater IsWindowsVistaSP1OrGreater IsWindowsVistaSP2OrGreater
IsWindows7OrGreater IsWindows7SP1OrGreater IsWindows8OrGreater IsWindows8Point1OrGreater
IsWindows10OrGreater IsWindows10TH1OrGreater IsWindows10TH2OrGreater IsWindows10RS1OrGreater
IsWindows10RS2OrGreater IsWindows10RS3OrGreater IsWindows10RS4OrGreater IsWindows10RS5OrGreater
IsWindowsServer IsThemeActive LsDisableSpecialCharacterLigature
"""

FAIL = """
GetLayeredWindowAttributes SetLayeredWindowAttributes UpdateLayeredWindow
GetMouseMovePointsEx SetWinEventHook UnhookWinEvent IsWinEventHookInstalled
RegisterPowerSettingNotification CreateEvent SetEvent ResetEvent WaitForMultipleObjectsEx
OpenProcess DuplicateHandle CreateFileMapping MapViewOfFileEx UnmapViewOfFile
GetFileSizeEx CreateFile GetLocaleInfo GetStringTypeEx FindNLSString GetTempFileName
GetIconInfo GetIconInfoImpl GetCurrentThemeName SetWindowTheme SetWindowThemeAttribute
CriticalSetWindowTheme BeginPanningFeedback UpdatePanningFeedback EndPanningFeedback
WTSRegisterSessionNotification WTSUnRegisterSessionNotification
WTSQuerySessionInformation WTSFreeMemory
"""


def norm(sym):
    for suffix in ("Wrapper", "Internal"):
        if sym.endswith(suffix) and len(sym) > len(suffix):
            sym = sym[: -len(suffix)]
    if sym and sym[-1] in "AW" and sym[:-1] in KNOWN_BASES:
        sym = sym[:-1]
    return sym


def main():
    if not os.path.exists(EXPORTS):
        print("[失败] 找不到 bin/exports.txt —— 先跑 build-shim.sh --symbols")
        return 1
    raw = [l.strip() for l in open(EXPORTS, encoding="utf-8") if l.strip()]

    global KNOWN_BASES
    real = set(REAL.split())
    degrade = set(DEGRADE.split())
    fail = set(FAIL.split())
    KNOWN_BASES = real | degrade | fail

    groups = collections.defaultdict(set)
    bridge = set()
    internal = set()
    unknown = set()
    for sym in raw:
        if sym.startswith("WpfLinuxWin32_"):
            bridge.add(sym)
            continue
        # shim 内部的 C 符号（wpf_* / 全局变量）也会被导出（没做 visibility 收敛）。
        # 它们不是 Win32 面，也不该被 GetProcAddress 依赖，统计时剔除。
        if sym.startswith("wpf_") or sym.startswith("g_"):
            internal.add(sym)
            continue
        n = norm(sym)
        if n in real:
            groups["真实现"].add(n)
        elif n in degrade:
            groups["降级"].add(n)
        elif n in fail:
            groups["返回失败"].add(n)
        else:
            unknown.add(n)

    total = sum(len(v) for v in groups.values())
    print(f"nm -D --defined-only 总数 : {len(raw)}")
    print(f"  其中 WpfLinuxWin32_*    : {len(bridge)}（M7c 桥接扩展导出，不属于 Win32 API 面）")
    print(f"  其中 wpf_* / g_* 内部符号: {len(internal)}（未做 visibility 收敛，不影响功能）")
    print(f"  → Win32 面导出符号       : {len(raw) - len(bridge) - len(internal)}")
    print(f"逻辑函数（去 A/W/Wrapper 变体后）: {total}")
    for k in ("真实现", "降级", "返回失败"):
        print(f"  {k:<8}: {len(groups[k])}")
    if unknown:
        print(f"\n[未分档] {len(unknown)} 个（需要在 REAL/DEGRADE/FAIL 里补名字）:")
        print("  " + " ".join(sorted(unknown)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
