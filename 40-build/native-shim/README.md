# `src/WpfGfx.Linux.Native/` — Win32 子集 shim（libwpfwin32.so）

M7b 的产物。用 C 实现一个 **user32 / gdi32 / kernel32 / PresentationNative 的子集**，
让移植到 Linux 的 WindowsBase / PresentationCore 里那些 `[DllImport("user32.dll")]`
有一个真实落点；消息泵转发到 X11。

```
src/WpfGfx.Linux.Native/
├── build-shim.sh              构建入口（**不需要 make**，直接调 gcc）
├── Makefile                   给有 make 的环境；编译命令与 build-shim.sh 逐字等价
├── include/                   （预留）
├── src/
│   ├── win32_abi.h            跨边界结构体的逐字段布局 + 编译期 _Static_assert
│   ├── win32_internal.h       全局状态、窗口/类/定时器/线程队列的数据结构
│   ├── win32_core.c           窗口表 + 窗口生命周期 API + GWL_*/焦点/属性
│   ├── win32_msg.c            消息队列 / 消息泵 / 定时器 / 窗口过程派发
│   ├── win32_x11.c            **唯一**碰 Xlib 的地方：建窗 + XEvent → MSG 翻译
│   ├── win32_misc.c           GDI / kernel32 / DPI / 监视器 / 系统参数
│   └── win32_exports.c        GetProcAddress 落点 + M7c 桥接导出 + ABI 探针
├── tests/abi_layout.c         布局自检可执行文件（打印逐字段 offset）
├── tools/
│   ├── extract-win32-inventory.py   产出 docs/U2-M7b-win32-inventory.md
│   └── wire-managed-layer.py        把 shim 编进两个 csproj（幂等）
└── bin/  obj/                 产物（可删）
```

## 构建

```bash
./build-shim.sh              # → bin/libwpfwin32.so
./build-shim.sh --abi        # 构建 + 打印 ABI 逐字段 offset（报告证据）
./build-shim.sh --symbols    # 构建 + 导出符号数 + bin/exports.txt
./build-shim.sh --all        # 三件事都做
```

依赖：`gcc` + `libc6-dev` + `libx11-dev`（`/usr/include/X11/Xlib.h`）。
本机没有装 `make`，所以 `build-shim.sh` **直接调 gcc**；Makefile 只是给有 make 的环境留一份等价入口。

## 三条设计决定（以及为什么）

### 1. `HWND == X11 Window（XID）`
不做句柄转换表。理由：M7a 的 `MilVisualTarget_AttachToHwnd(hwnd)` 是**身份映射**，
M7c 要把这个 nint 落到真实 X11 窗口上；让 HWND 直接就是 XID，接线零转换
（`X11PresentationTarget.NativeHandle` 也已经是 nint）。
`GetDesktopWindow()` 直接返回 `XRootWindow(dpy, screen)` —— 本来就是合法 XID。
`HWND_MESSAGE`（-3）是父窗口哨兵，特判；message-only 窗口照样建一个不 map 的真窗口。

M7c 需要的桥接导出：
`WpfLinuxWin32_GetX11Window(HWND)`（0 = 不是本 shim 的窗口）、
`WpfLinuxWin32_GetX11Display/Screen/ConnectionNumber`、`WpfLinuxWin32_EnsureX11`、
`WpfLinuxWin32_PumpOnce`、`WpfLinuxWin32_WindowCount`、`WpfLinuxWin32_GetWndProc`、
`WpfLinuxWin32_LastError`、`WpfLinuxWin32_AbiLayout`。

### 2. Xlib 只在一个文件里，且**两道线程安全防线**
`win32_x11.c` 是唯一 include `<X11/Xlib.h>` 的文件。
Xlib 连接不是线程安全的，而 WPF 真的会多线程碰它（`HwndWrapper` 的**终结器**
会走 `DestroyWindow`）。本轮实测踩到过：

```
[xcb] Unknown sequence number while processing queue
[xcb] Most likely this is a multi-threaded client and XInitThreads has not been called
dotnet: ../../src/xcb_io.c:278: poll_for_event: Assertion `!xcb_xlib_threads_sequence_lost' failed.
```

防线 1：`__attribute__((constructor))` 里调 `XInitThreads()`（必须早于任何 Xlib 调用，
所以不能放在 `wpf_x11_ensure()` 里）。
防线 2：自己的 `g_xlock` 把本 shim 的每个 Xlib 调用串行化（与状态锁 `g_wpf.lock` 分开，
锁序恒为 `g_wpf.lock` → `g_xlock`；**调用托管窗口过程时绝不持锁**）。

### 3. 裸名 == A（UTF-8）实现，`...W` 先转码
托管侧 `[DllImport]` 的 `CharSet` 有两种，探测顺序不同：
`CharSet.Unicode` → 先探 `名W`；`CharSet.Auto`（**Unix 上折叠为 Ansi**）→ 先探裸名。
所以约定「**裸名 == A（UTF-8）实现**，`...W` 负责把 UTF-16 转成 UTF-8 再转发」。
本轮实测踩过这条：最初裸名一律转发到 `W`，`CreateWindowEx` 收到的 UTF-8 字节被当成
UTF-16 解释 → 类名乱码 → `ERROR_CLASS_DOES_NOT_EXIST (1411)` →
`Dispatcher.CurrentDispatcher` 都建不出来。

## 实现深度

`--symbols` 会打印导出符号数（当前 **347**）。逐条的档位（真实现 / 降级 / 返回失败）
写在每个函数上方的 `[真实现]` / `[降级]` / `[失败]` 注释里，
汇总见 `docs/U2-M7b-report.md`。

**「不伪造」原则**：语义无法在 Linux 表达的函数一律返回明确的失败码并设 LastError
（`CreateFileMapping` → NULL、`UpdateLayeredWindow` → FALSE、`MsgWaitForMultipleObjectsEx(nCount>0)`
→ WAIT_FAILED + ERROR_NOT_SUPPORTED …），**绝不返回一个看起来能用的假句柄**。

## 单元/烟测

本目录只有 ABI 自检（`--abi`）。托管层的端到端烟测在
`tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/`：

```bash
export PATH="$HOME/.dotnet:$PATH"
tests/WpfGfx.Linux.Tests/Windowing.Tests/start-xvfb.sh start      # Xvfb :99
export DISPLAY=:99
dotnet test tests/WpfGfx.Linux.Tests/ManagedLayer.Tests/ManagedLayer.Tests.csproj
```

无 X server 时窗口/输入用例在**发现期**跳过（不会红）。

## 接线（改 csproj 的两条通道）

`build/*.Linux/*.csproj` 是 `port-lib.py` 的**生成物**，额外内容必须有可重放的出处：

| 通道 | 用途 | 本目录的关联 |
|---|---|---|
| `build/shims/<Name>.shims.txt` | port-lib 读它注入 `<Compile Include>` | M7b 已把 `Win32ShimResolver.cs` 追加进两个 shims.txt（下次重生成自动带上） |
| `build/<Name>.Linux/reapply-patches.py` | 生成后再补的块 | 补丁 F（Linux 版 EventTrace） |
| `tools/wire-managed-layer.py` | **不重生成整个 csproj** 的定点插入（1 行/工程），幂等 | 当前环境用这条（PresentationCore 目录有别的 agent 在动） |
