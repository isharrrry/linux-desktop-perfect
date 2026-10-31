# `WinApp` —— **在 Windows 上编译**的最小 WPF 程序（供「外部程序集」判据用）

这不是本仓构建的产物。它是在一台真实的 Windows 机器上（`192.168.15.125`，
.NET SDK 10.0.203，`dotnet build -c Release`）编译出来的，然后整体拷到 Linux 上。

用途：验证本仓的**核心目标** ——
「把 Windows 上编译好的 WPF 程序集拷过来，用我们构建的 `Microsoft.WindowsDesktop.App` 直接运行」。

## 源项目的形状（与本仓 `samples/` 无关）

- `net10.0-windows` + `UseWPF=true`
- 一个 `Window`，含 `TextBlock`（`hello-from-windows`）、`Button`（`btn`）、`TextBlock`（`clicks=N`）
- 手写 `Main`（不依赖 `ApplicationDefinition`），点按钮让计数 +1

## 文件

| 文件 | 说明 |
|---|---|
| `WinApp.dll` | Windows 上编译出的托管程序集（7680 B）|
| `WinApp.deps.json` | Windows 侧生成的依赖描述 |
| `WinApp.runtimeconfig.json` | **要求 `Microsoft.WindowsDesktop.App 10.0.0`**（我们的是 10.0.11，同 major ⇒ 默认 roll-forward 就能加载）|

`WinApp.exe`（apphost）**故意没放进来** —— Linux 上不需要，用 `dotnet WinApp.dll` 即可。

## 复现方式（本仓 `check-external-winapp.mjs` 做的就是这件事）

```bash
env -i PATH=/usr/bin:/bin HOME=$HOME DISPLAY=:<xvfb> \
  DOTNET_ROOT=$HOME/.local/share/ldp/dotnet \
  DOTNET_ReadyToRun=0 \
  LD_LIBRARY_PATH=$FX/native: \
  WPF_LINUX_WIN32_SHIM=$FX/libwpfwin32.so \
  DOTNET_STARTUP_HOOKS=$FX/LdpA11y.dll \
  LDP_A11Y_DUMP=/tmp/tree.json LDP_A11Y_REPEAT=1 \
  $HOME/.local/share/ldp/dotnet/dotnet WinApp.dll
```

实测（2026-10-31）：UIA 树 4 个节点、`clicks=0`，`xdotool` 点按钮后变 `clicks=1`。
