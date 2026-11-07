# UIA / AT-SPI 验证工具（C# 版）

## 用法

```bash
# 1. 启动 SourceTree —— 用仓库自带的启动器（推荐，自动带好全部环境变量）
cp 70-operate/启动器/sourcetree /tmp/stpkg/SourceTree/app-3.4.15/sourcetree
chmod +x /tmp/stpkg/SourceTree/app-3.4.15/sourcetree
DISPLAY=:303 /tmp/stpkg/SourceTree/app-3.4.15/sourcetree >/tmp/st.log 2>&1 &

# 等价的手动写法（启动器内部就是这些）：
# export DOTNET_ROOT=~/.local/share/ldp/dotnet
# FX=~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
# cd /tmp/stpkg/SourceTree/app-3.4.15
# DISPLAY=:303 DOTNET_ROOT=$DOTNET_ROOT DOTNET_ReadyToRun=0 \
#   LD_LIBRARY_PATH="$FX/native:$FX:" WPF_LINUX_WIN32_SHIM=$FX/libwpfwin32.so \
#   ~/.local/share/ldp/dotnet/dotnet SourceTree.exe >/tmp/st.log 2>&1 &
#
# 启动器还做了两件额外的事：
#   - 检测 .lnk 快捷方式并明确报错（K-1310，不再静默失败）
#   - 自动补 DISPLAY 默认值 :303

# 2. 等 20 秒后跑验证
sleep 20
DISPLAY=:303 ~/.local/share/ldp/dotnet/dotnet \
  50-verify/external/uia/dump-tree-cs/bin/Release/net10.0/dump-tree.dll
```

## 参数

| 参数 | 说明 |
|---|---|
| 无参数 | 人类可读输出 |
| `--json` | JSON 输出（含 apps/controls/bus/path）|
| `--wait N` | 等最多 N 秒，直到找到 SourceTree |
| `--bus <addr>` | 指定 D-Bus 地址（默认自动发现 `bus_<display>`）|

## 退出码

| 码 | 含义 |
|---|---|
| **0** | 找到 SourceTree 且枚举 ≥ 10 个控件 ✅ |
| **1** | 找到但控件不足 |
| **3** | 超时未找到 |
| **4** | gdbus 不可用 / 找不到 bus |

## 实现

- **纯 C#（`net10.0`），无第三方 NuGet**（`Tmds.DBus` 离线不可用）
- **通过 `System.Diagnostics.Process` 调用 `gdbus` 命令行**
- **自动发现 bus**：`$AT_SPI_BUS_ADDRESS` → `$HOME/.cache/at-spi/bus_<display>` → `$XDG_RUNTIME_DIR/at-spi/bus_<display>`
- **递归读 `GetChildren` + `Properties.Get(Name/ChildCount)`**

## 当前状态（第 105 轮实测）

```
桌面子项数: 2
  [1] 'SourceTree' role=application children=9

✅ 找到 SourceTree，枚举了 10 个控件（5 个有 name）
[application] 'SourceTree' (childCount=9)
  [frame] '' (childCount=0)
  [frame] 'SystemResourceNotifyWindow' (childCount=0)
  [frame] '' (childCount=0)
  [frame] '' (childCount=0)
  [frame] 'Sourcetree' (childCount=0)
  [frame] '' (childCount=0)
  [frame] 'MediaContextNotificationWindow' (childCount=0)
  [frame] '' (childCount=0)
  [frame] '.NET-BroadcastEventWindow.30c4a4.0' (childCount=0)
退出码=0
```

**⇒ 达成「找到 SourceTree + ≥10 控件」；但控件树只有窗口级（无菜单/工具栏/按钮）。**

## 待办

- **`UiaReturnRawElementProvider` 仍是桩**（`win32_gdi_text.c:229` 返回 0）⇒ 这是「控件树只有窗口级」的根本原因
- **需要从 WPF `AutomationPeer` 树拿真实控件**
