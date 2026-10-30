#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7b 第 1 步：从**实际编译进** WindowsBase.Linux / PresentationCore.Linux 的源文件里
提取全部 `[DllImport]`，分类成 P0/P1/P2，并产出
`docs/U2-M7b-win32-inventory.md`。

    python3 src/WpfGfx.Linux.Native/tools/extract-win32-inventory.py            # 写文档
    python3 src/WpfGfx.Linux.Native/tools/extract-win32-inventory.py --stdout   # 只打印

口径（为什么这样做）
--------------------
* **编译集合不是猜的**：直接从两个 csproj 的 `<Compile Include=...>` 列表取文件，
  展开 `$(UpstreamWpfRoot)`。上游 `src/` 下没被编译的文件一律不计入 —— 这一点很关键，
  U2 扫描报告用的是"源码树里出现过"，与"真的会编进程序集"差着一个 excludes 的距离。
* **DLL 名要解引用常量**：源码里写的是 `ExternDll.User32` / `DllImport.MilCore`
  这类常量，不是字面量。常量表从 `Shared/MS/Win32/ExternDll.cs` 与
  `Shared/RefAssemblyAttrs.cs` 机械提取（含 `BuildInfo.WCP_VERSION_SUFFIX = "_cor3"`）。
* **"是否已实现"由产物回答，不由注释回答**：拿 `bin/exports.txt`
  （`nm -D --defined-only libwpfwin32.so` 的结果）去匹配。
  匹配规则按 .NET 在 **Unix** 上的探测顺序：
    · `CharSet.Unicode` 且 `ExactSpelling=false` → 先 `名W` 再 `名`
    · `CharSet.Auto`（Unix 上折叠为 Ansi）且 `ExactSpelling=false` → 先 `名` 再 `名A`
    · 有 `EntryPoint` 时用 EntryPoint 作为基名
  另外 `PresentationNative_cor3.dll` 的那批是 `...Wrapper` 后缀的包装名。
"""

import collections
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
NATIVE_ROOT = os.path.normpath(os.path.join(HERE, ".."))
ROOT = os.path.normpath(os.path.join(NATIVE_ROOT, "..", ".."))
# `UP` = `$(UpstreamWpfRoot)` 的取值：结构上游化后 = **仓根**（上游 `src/Microsoft.DotNet.Wpf/**` 就在仓根下）。
UP = ROOT
SRC = os.path.join(UP, "src", "Microsoft.DotNet.Wpf", "src")

# ── DLL 名常量表 ──────────────────────────────────────────────────────────
def load_constants():
    known = {}
    ext = os.path.join(SRC, "Shared/MS/Win32/ExternDll.cs")
    for m in re.finditer(r'public const string (\w+)\s*=\s*"([^"]*)"',
                         open(ext, encoding="utf-8-sig", errors="replace").read()):
        known["ExternDll." + m.group(1)] = m.group(2)
    known.update({
        "DllImport.PresentationNative": "PresentationNative_cor3.dll",
        "DllImport.PresentationCFFRasterizerNative": "PresentationCFFRasterizerNative_cor3.dll",
        "DllImport.MilCore": "wpfgfx_cor3.dll",
        "DllImport.UIAutomationCore": "UIAutomationCore.dll",
        "DllImport.Wininet": "Wininet.dll",
        "DllImport.WindowsCodecs": "WindowsCodecs.dll",
        "DllImport.WindowsCodecsExt": "WindowsCodecsExt.dll",
        "DllImport.Mscms": "mscms.dll",
        "DllImport.PrntvPt": "prntvpt.dll",
        "DllImport.Ole32": "ole32.dll",
        "DllImport.User32": "user32.dll",
        "DllImport.NInput": "ninput.dll",
        "DllImport.ApiSetWinRT": "api-ms-win-core-winrt-l1-1-0.dll",
        "DllImport.ApiSetWinRTString": "api-ms-win-core-winrt-string-l1-1-0.dll",
    })
    return known


def compiles(csproj):
    txt = open(csproj, encoding="utf-8-sig", errors="replace").read()
    out = []
    for m in re.finditer(r'<Compile Include="([^"]+)"', txt):
        p = (m.group(1).replace("$(UpstreamWpfRoot)", UP + "/")
                       .replace("$(WpfLinuxRoot)", ROOT + "/")
                       .replace("\\", "/"))
        out.append(os.path.normpath(p))
    return out


def find_attrs(text):
    """返回 (起始位置, 属性文本, 属性结束位置)。跨行属性块必须括号配平才能截对。"""
    res = []
    for m in re.finditer(r"\[DllImport\s*\(", text):
        i = m.end() - 1
        depth = 0
        j = i
        while j < len(text):
            c = text[j]
            if c == '"':
                j += 1
                while j < len(text) and text[j] != '"':
                    if text[j] == "\\":
                        j += 1
                    j += 1
            elif c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        res.append((m.start(), text[i:j + 1], j + 1))
    return res


def scan(files, known):
    entries = []
    for f in files:
        if not os.path.exists(f):
            continue
        text = open(f, encoding="utf-8-sig", errors="replace").read()
        consts = {m.group(1): m.group(2)
                  for m in re.finditer(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"', text)}
        for a, attr, end in find_attrs(text):
            inner = attr[1:-1]
            args = [x.strip() for x in inner.split(",")]
            dll_tok = args[0].strip()
            if dll_tok.startswith('"'):
                dll = dll_tok.strip('"')
            else:
                dll = consts.get(dll_tok.split(".")[-1], known.get(dll_tok, "?" + dll_tok))

            entry = None
            charset = None
            exact = False
            for x in args[1:]:
                if x.startswith("EntryPoint"):
                    entry = x.split("=", 1)[1].strip().strip('"')
                elif x.startswith("CharSet"):
                    charset = x.split("=", 1)[1].strip().split(".")[-1]
                elif x.startswith("ExactSpelling"):
                    exact = "true" in x.split("=", 1)[1].lower()

            rest = text[end:end + 4000]
            mm = re.search(r"extern\s+([^;{]+);", rest, re.S)
            sig = " ".join(mm.group(1).split()) if mm else "?"
            fn = "?"
            if mm:
                fm = re.search(r"(\w+)\s*\(", mm.group(1))
                if fm:
                    fn = fm.group(1)

            entries.append(dict(
                file=os.path.relpath(f, SRC), line=text.count("\n", 0, a) + 1,
                dll=dll, fn=fn, entry=entry, charset=charset, exact=exact, sig=sig))
    return entries


# ── P0：消息泵 + 窗口生命周期（本 shim 必须做的那一批）──────────────────────
P0 = {
    # 消息泵
    "GetMessage", "PeekMessage", "TranslateMessage", "DispatchMessage",
    "PostMessage", "PostQuitMessage", "SendMessage", "SendMessageTimeout",
    "MsgWaitForMultipleObjectsEx", "SetTimer", "KillTimer", "RegisterWindowMessage",
    "GetMessageExtraInfo", "SetMessageExtraInfo", "GetMessagePos", "GetMessageTime",
    "GetTickCount", "GetTickCount64", "QueryPerformanceCounter", "QueryPerformanceFrequency",
    # 窗口类 / 建窗 / 销毁
    "RegisterClassEx", "UnregisterClass", "CreateWindowEx", "DestroyWindow",
    "ShowWindow", "ShowWindowAsync", "MoveWindow", "SetWindowPos",
    # 窗口长整型 / 窗口过程链
    "GetWindowLong", "GetWindowLongPtr", "SetWindowLong", "SetWindowLongPtr",
    "DefWindowProc", "CallWindowProc",
    # 几何 / 身份
    "GetClientRect", "GetWindowRect", "ScreenToClient", "ClientToScreen",
    "IsWindow", "IsWindowUnicode", "GetParent", "SetParent", "GetAncestor", "GetWindow",
    "GetDesktopWindow", "GetWindowThreadProcessId", "GetClassName",
    # 焦点 / 捕获（HwndSource/HwndTarget 直接依赖）
    "GetFocus", "SetFocus", "GetCapture", "SetCapture", "ReleaseCapture",
    "GetActiveWindow", "SetActiveWindow", "GetForegroundWindow", "SetForegroundWindow",
    # 模块 / 过程地址（HwndSubclass 静态构造解析 DefWindowProcW 用）
    "GetModuleHandle", "GetModuleHandleEx", "GetProcAddress",
    # 其它窗口骨架依赖
    "GetStockObject", "GetSystemMetrics",
}
P0 |= {n + "Wrapper" for n in ("GetWindowLong", "GetWindowLongPtr", "SetWindowLong",
                              "SetWindowLongPtr", "GetParent", "GetWindow", "SetFocus",
                              "EnableWindow", "GetWindowText", "GetWindowTextLength",
                              "GetAncestor", "MapWindowPoints")}

# ── P1：能无副作用实现或静默降级的那一批 ────────────────────────────────────
P1_DLLS = {"user32.dll", "gdi32.dll", "kernel32.dll"}
P1 = {
    # DPI（Linux 上 96 DPI 是真话，不是伪造）
    "GetDpiForWindow", "GetDpiForSystem", "GetDpiForMonitor", "IsProcessDPIAware",
    "SetProcessDPIAware", "EnableNonClientDpiScaling", "GetWindowDpiAwarenessContext",
    "SetThreadDpiAwarenessContext", "GetThreadDpiHostingBehavior",
    "AreDpiAwarenessContextsEqual",
    # 窗口文本 / 属性
    "SetWindowText", "GetWindowText", "GetWindowTextLength",
    "SetProp", "GetProp", "RemoveProp",
    # GDI 面（无 GDI，返回哨兵句柄/失败码并注明）
    "GetDC", "GetWindowDC", "ReleaseDC", "CreateCompatibleDC", "DeleteDC",
    "CreateCompatibleBitmap", "CreateBitmap", "CreateDIBSection", "SelectObject",
    "DeleteObject", "GetObject", "GetBitmapBits", "GetDeviceCaps", "ExtEscape",
    "FillRect", "StartDoc", "EndDoc", "StartPage", "EndPage", "SetEnhMetaFileBits",
    "BeginPaint", "EndPaint", "InvalidateRect", "ValidateRect", "UpdateWindow",
    "RedrawWindow", "PrintWindow",
    # 光标 / 图标 / 插入符
    "LoadCursor", "LoadImage", "ExtractIconEx", "GetCursor", "SetCursor", "ShowCursor",
    "DestroyCursor", "GetIconInfo", "CreateIconIndirect", "DestroyIcon",
    "CreateCaret", "ShowCaret", "HideCaret", "DestroyCaret", "SetCaretPos",
    "GetCaretBlinkTime",
    # 监视器 / 屏幕度量
    "MonitorFromWindow", "MonitorFromPoint", "MonitorFromRect", "GetMonitorInfo",
    "EnumDisplayMonitors", "SystemParametersInfo",
    # 键盘 / 指针状态
    "GetKeyState", "GetAsyncKeyState", "GetKeyboardLayout", "GetKeyboardLayoutList",
    "ActivateKeyboardLayout", "MapVirtualKey", "GetDoubleClickTime", "GetCursorPos",
    "SetCursorPos", "TrackMouseEvent", "WindowFromPoint", "GetMouseMovePointsEx",
    "keybd_event", "mouse_event", "EnumThreadWindows", "AdjustWindowRectEx",
    "GetWindowPlacement", "SetWindowPlacement",
    # 分层窗口（明确返回失败）
    "GetLayeredWindowAttributes", "SetLayeredWindowAttributes", "UpdateLayeredWindow",
    # 消息过滤 / 无障碍 / 电源
    "ChangeWindowMessageFilter", "ChangeWindowMessageFilterEx",
    "NotifyWinEvent", "SetWinEventHook", "UnhookWinEvent", "IsWinEventHookInstalled",
    "RegisterPowerSettingNotification", "UnregisterPowerSettingNotification",
    "MessageBeep", "MessageBox",
    # kernel32
    "GetModuleFileName", "LoadLibrary", "LoadLibraryEx", "FreeLibrary",
    "GetCurrentProcess", "GetCurrentThreadId", "IsDebuggerPresent", "CloseHandle",
    "CreateEvent", "SetEvent", "ResetEvent", "WaitForMultipleObjectsEx", "OpenProcess",
    "DuplicateHandle", "ProcessIdToSessionId", "CreateFileMapping", "MapViewOfFileEx",
    "UnmapViewOfFile", "GetFileSizeEx", "CreateFile", "LocalFree",
    "MultiByteToWideChar", "GetLocaleInfo", "GetStringTypeEx", "FindNLSString",
    "GetOEMCP", "GetACP", "GetSystemPowerStatus", "DeactivateActCtx", "GetTempFileName",
}


def implemented_names():
    p = os.path.join(NATIVE_ROOT, "bin", "exports.txt")
    if not os.path.exists(p):
        return set(), False
    names = {l.strip() for l in open(p, encoding="utf-8") if l.strip()}
    return names, True


def probes(entry):
    """按 .NET 在 Unix 上的探测顺序给出候选导出名（基名在前）。"""
    base = entry["entry"] or entry["fn"]
    cs = (entry["charset"] or "Ansi")
    if entry["exact"]:
        return [base]
    if cs == "Unicode":
        return [base + "W", base]
    return [base, base + "A"]           # Auto 在 Unix 上 == Ansi


def classify(entry, exports):
    base = entry["entry"] or entry["fn"]
    dll = entry["dll"]
    if dll in ("user32.dll", "gdi32.dll", "kernel32.dll", "PresentationNative_cor3.dll"):
        tier = "P0" if base in P0 else ("P1" if base in P1 else "P1")
    else:
        tier = "P2"
    hit = None
    for cand in probes(entry):
        if cand in exports:
            hit = cand
            break
    if hit is None and (base + "Wrapper") in exports:
        hit = base + "Wrapper"
    return tier, hit


def main():
    known = load_constants()
    wb = scan(compiles(os.path.join(ROOT, "src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj")), known)
    pc = scan(compiles(os.path.join(ROOT, "src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj")), known)
    for e in wb:
        e["asm"] = "WindowsBase"
    for e in pc:
        e["asm"] = "PresentationCore"
    all_e = wb + pc

    exports, have_exports = implemented_names()

    rows = []
    for e in all_e:
        tier, hit = classify(e, exports)
        rows.append(dict(e, tier=tier, hit=hit))

    # 去重：(dll, entry||fn, sig) —— 同一函数在多个文件里声明多次只统计一次
    uniq = {}
    for r in rows:
        uniq.setdefault((r["dll"], r["entry"] or r["fn"], r["sig"]), []).append(r)
    ur = [v[0] for v in uniq.values()]

    by_tier = collections.Counter(x["tier"] for x in ur)
    whole_by_dll = collections.Counter(x["dll"] for x in all_e)
    uniq_by_dll = collections.Counter(x["dll"] for x in ur)
    p0_done = sum(1 for x in ur if x["tier"] == "P0" and x["hit"])
    p0_total = sum(1 for x in ur if x["tier"] == "P0")
    p1_done = sum(1 for x in ur if x["tier"] == "P1" and x["hit"])
    p1_total = sum(1 for x in ur if x["tier"] == "P1")

    if "--stdout" in sys.argv:
        print("属性条数: WindowsBase", len(wb), "PresentationCore", len(pc), "合计", len(all_e))
        print("去重 (dll,name,sig):", len(ur))
        print("P0", p0_total, "done", p0_done)
        print("P1", p1_total, "done", p1_done)
        print("P2", by_tier["P2"])
        return 0

    L = []
    A = L.append
    A("# M7b · Win32 API 需求清单（从实际编译集合机械提取）")
    A("")
    A("> 本文件由 `src/WpfGfx.Linux.Native/tools/extract-win32-inventory.py` **生成**，")
    A("> 不要手改。重跑：")
    A("> ```bash")
    A("> src/WpfGfx.Linux.Native/build-shim.sh --symbols      # 先产出 bin/exports.txt")
    A("> python3 src/WpfGfx.Linux.Native/tools/extract-win32-inventory.py")
    A("> ```")
    A("")
    A("## 0. 口径（为什么数字和 U2 扫描报告不同）")
    A("")
    A("| 维度 | 本清单 | docs/U2-PresentationCore-scan.md §2.2 |")
    A("|---|---|---|")
    A("| 来源 | 两个 csproj 的 `<Compile Include>` 列表（**真的会编进程序集**） | 源码树里出现过 `[DllImport]` 的文件 |")
    A("| DLL 名 | 解引用 `ExternDll.*` / `DllImport.*` 常量表 + `WCP_VERSION_SUFFIX=_cor3` | 同上，但只覆盖 PresentationCore |")
    A("| 范围 | WindowsBase ∪ PresentationCore | PresentationCore |")
    A("")
    A(f"统计（**属性条数**，同一函数在多文件声明会重复计）：WindowsBase **{len(wb)}**、")
    A(f"PresentationCore **{len(pc)}**，合计 **{len(all_e)}** 条。")
    A(f"按 `(DLL, 名, 签名)` 去重后 **{len(ur)}** 条 —— 下面所有分类都按去重口径。")
    A("")
    A("## 1. 总览")
    A("")
    A("| 档 | 含义 | 条数 | 已由 libwpfwin32.so 实现 |")
    A("|---|---|---|---|")
    A(f"| **P0** | 消息泵 + 窗口生命周期（不做就跑不起来） | **{p0_total}** | **{p0_done}** |")
    A(f"| **P1** | 能无副作用实现 / 静默降级（不做多数不致命） | **{p1_total}** | **{p1_done}** |")
    A(f"| **P2** | 明确不做（各有替代路线或本工程不需要） | **{by_tier['P2']}** | 0 |")
    A(f"| 合计 | | **{len(ur)}** | |")
    A("")
    A("### 1.1 按 DLL 分布（去重口径）")
    A("")
    A("| DLL | 去重条数 | 属性条数 | 档 |")
    A("|---|---|---|---|")
    for dll, n in uniq_by_dll.most_common():
        tier = "P0/P1" if dll in ("user32.dll", "gdi32.dll", "kernel32.dll",
                                  "PresentationNative_cor3.dll") else "P2"
        A(f"| `{dll}` | {n} | {whole_by_dll[dll]} | {tier} |")
    A("")

    A("## 2. P0 · 必做（消息泵 + 窗口生命周期）")
    A("")
    A("| DLL | 函数（EntryPoint） | 托管签名 | 声明位置 | shim 导出名 |")
    A("|---|---|---|---|---|")
    for x in sorted((r for r in ur if r["tier"] == "P0"),
                    key=lambda r: (r["dll"], r["entry"] or r["fn"])):
        base = x["entry"] or x["fn"]
        A(f"| `{x['dll']}` | `{base}` | `{x['sig']}` | `{x['file']}:{x['line']}` "
          f"| {('`' + x['hit'] + '`') if x['hit'] else '**未导出**'} |")
    A("")

    A("## 3. P1 · 值得做（真实现 / 降级 / 返回失败三档）")
    A("")
    A("| DLL | 函数（EntryPoint） | 托管签名 | 声明位置 | shim 导出名 |")
    A("|---|---|---|---|---|")
    for x in sorted((r for r in ur if r["tier"] == "P1"),
                    key=lambda r: (r["dll"], r["entry"] or r["fn"])):
        base = x["entry"] or x["fn"]
        A(f"| `{x['dll']}` | `{base}` | `{x['sig']}` | `{x['file']}:{x['line']}` "
          f"| {('`' + x['hit'] + '`') if x['hit'] else '**未导出**'} |")
    A("")

    A("## 4. P2 · 不做（清单 + 理由）")
    A("")
    A("P2 的判据不是「难」，而是**本工程已经有别的承接面，或在 Linux 上没有对象可做**。")
    A("逐 DLL 的理由：")
    A("")
    reasons = {
        "wpfgfx_cor3.dll": "MIL/DUCE 原生面。M7a 已把 **108 个导出名**实现成托管方法"
                           "（`src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative*.cs`），把它们**导出成原生符号**"
                           "并接上是 M7c 的活；Win32 shim 不该重复承担。",
        "WindowsCodecs.dll": "WIC 成像栈（109 条）。M1 已有 Skia 解码/编码能力面，"
                             "映射关系待 M7c/后续里程碑定；直接 stub 会让图像路径静默失真。",
        "PresentationNative_cor3.dll": "**部分做了**：只有 `*Wrapper` 那一批（窗口长整型/父窗口/"
                                       "焦点/标题/EnableWindow）映射到了本 shim。"
                                       "其余是 LineServices 行排版（`Lo*`）与 `LsDisableSpecialCharacterLigature`，"
                                       "依赖 Windows 的 line-services 引擎，Linux 上应由 M1 的 Skia 文本栈承接。",
        "dwrite.dll / DirectWrite 面": "上游由 DirectWriteForwarder（C++/CLI）承担，"
                                       "本工程已有 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWriteForwarder` 托管骨架。",
        "PenIMC_cor3.dll": "触笔/手写输入（16 条）。X11 上走 XInput2，属独立里程碑。",
        "mshwgst.dll": "手写识别（14 条）。无 Linux 对应物。",
        "ninput.dll": "Windows 指针输入（7 条）。X11 上等价物是 XInput2。",
        "msdrm.dll": "Windows DRM/权限管理（49 条）。WPF 用它做 XPS 权限，Linux 无对应物。",
        "mscms.dll": "Windows 色彩管理（9 条）。Linux 上是 colord/ICC，M1 的色彩面另议。",
        "advapi32.dll": "注册表/安全令牌/事件日志。Linux 无注册表；诊断走 stderr/Journal。",
        "imm32.dll": "IME（18 条）。Linux 上是 IBus/Fcitx + XIM，属独立里程碑。",
        "uxtheme.dll": "视觉样式（7 条）。WPF 自绘主题，Linux 上无系统主题可问。",
        "ole32.dll": "OLE 剪贴板/DnD。M4 已裁决**最小诚实 stub**"
                     "（`src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.OleApi.Stubs.cs`，一律 PlatformNotSupportedException）。",
        "api-ms-win-core-winrt-*.dll": "WinRT 投影（InputPane/UISettings）。Linux 无 WinRT。",
        "oleaut32.dll / oleacc.dll / urlmon.dll / shell32.dll / shfolder.dll": "COM 自动化 / 无障碍 / URL 处理 / Shell 集成。",
        "wtsapi32.dll": "终端服务会话通知。X11 无对应物。",
        "ntdll.dll": "RtlGetVersion 之类。Linux 上等价物是 uname/Environment.OSVersion。",
        "winmm.dll": "多媒体定时器/音频。无对应物（WPF 媒体在 U3 已定暂缓）。",
        "psapi.dll / winspool.drv / wininet.dll / msctf.dll": "进程信息 / 打印 / WinINet / TSF。",
    }
    A("")
    A("| DLL | 条数 | 处置与理由 |")
    A("|---|---|---|")
    for dll, n in uniq_by_dll.most_common():
        if dll in ("user32.dll", "gdi32.dll", "kernel32.dll", "PresentationNative_cor3.dll"):
            continue
        A(f"| `{dll}` | {n} | {reasons.get(dll, '见 docs/U2-M7b-report.md 的未做清单')} |")
    A("")

    A("## 5. P1 里**返回失败**的条目（诚实清单，不假装成功）")
    A("")
    A("这些函数在 Linux 上**没有可表达的对象**，shim 返回 Win32 的失败码并设 LastError。")
    A("它们的共同点是：假装成功会让上层拿到一个无效句柄，比失败更危险。")
    A("")
    A("| 函数 | shim 返回 | 理由 |")
    A("|---|---|---|")
    fails = [
        ("LoadLibrary/GetProcAddress", "真实现", "查本 .so 导出表 → dlopen；是真实现不是 stub"),
        ("CreateFile", "INVALID_HANDLE_VALUE", "内核文件句柄，Linux 上应走 BCL 的 FileStream"),
        ("CreateFileMapping/MapViewOfFileEx", "NULL", "跨进程共享内存；Linux 上应改 memfd/shm_open"),
        ("CreateEvent/SetEvent/WaitForMultipleObjectsEx", "NULL / FALSE / WAIT_FAILED", "内核事件对象"),
        ("OpenProcess/DuplicateHandle", "NULL / FALSE", "跨进程句柄"),
        ("GetLocaleInfo/GetStringTypeEx/FindNLSString", "0 / -1", "NLS 表；Linux 上用 ICU（BCL 已提供）"),
        ("GetLayeredWindowAttributes/SetLayeredWindowAttributes/UpdateLayeredWindow", "FALSE",
         "分层窗口是 Windows 合成路径；X11 上等价的正确做法是 ARGB visual + XComposite"),
        ("SetWinEventHook/NotifyWinEvent", "NULL / no-op", "UIA 事件广播；Linux 上是 AT-SPI2 over D-Bus"),
        ("RegisterPowerSettingNotification", "NULL", "WM_POWERBROADCAST 无对应物"),
        ("GetMouseMovePointsEx", "-1", "依赖 Windows 的鼠标输入历史队列"),
        ("GetTempFileName", "0", "不伪造临时文件语义；BCL 的 Path.GetTempFileName 可用"),
        ("MsgWaitForMultipleObjectsEx(nCount>0)", "WAIT_FAILED + ERROR_NOT_SUPPORTED",
         "不假装能等内核对象。WPF 全树只有 nCount==0 的调用点（Dispatcher.IsInputPending）"),
    ]
    for fn, ret, why in fails:
        A(f"| `{fn}` | {ret} | {why} |")
    A("")

    A("## 6. 复现命令")
    A("")
    A("```bash")
    A("# 1) 属性条数 / 去重条数 / 各档统计")
    A("python3 src/WpfGfx.Linux.Native/tools/extract-win32-inventory.py --stdout")
    A("# 2) 产物里到底导出了哪些符号")
    A("nm -D --defined-only src/WpfGfx.Linux.Native/bin/libwpfwin32.so | awk '{print $3}' | sort")
    A("```")

    out = os.path.join(ROOT, "docs", "U2-M7b-win32-inventory.md")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print(f"[OK] 写出 {out}")
    print(f"     属性条数 WB={len(wb)} PC={len(pc)} 合计={len(all_e)}；去重={len(ur)}")
    print(f"     P0 {p0_done}/{p0_total} 已实现；P1 {p1_done}/{p1_total}；P2 {by_tier['P2']}")
    miss = [ (r['dll'], r['entry'] or r['fn']) for r in ur if r['tier']=='P0' and not r['hit'] ]
    print("     P0 未实现:", miss)
    if not have_exports:
        print("[注意] 没找到 bin/exports.txt —— 先跑 build-shim.sh --symbols，"
              "否则「已实现」一栏全是空的")
    return 0


if __name__ == "__main__":
    sys.exit(main())
