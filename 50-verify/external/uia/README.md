# UIA / AT-SPI 验证脚本

## 用法

```bash
# 1. 先启动 SourceTree
export DOTNET_ROOT=~/.local/share/ldp/dotnet
FX=~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
cd /tmp/stpkg/SourceTree/app-3.4.15
DISPLAY=:303 DOTNET_ROOT=$DOTNET_ROOT DOTNET_ReadyToRun=0 \
  LD_LIBRARY_PATH="$FX/native:$FX:" WPF_LINUX_WIN32_SHIM=$FX/libwpfwin32.so \
  ~/.local/share/ldp/dotnet/dotnet SourceTree.exe >/tmp/st.log 2>&1 &

# 2. 等 20 秒后跑验证
sleep 20
DISPLAY=:303 python3 50-verify/external/uia/dump_tree.py
```

## 参数

| 参数 | 说明 |
|---|---|
| **无参数** | 人类可读输出 |
| **`--json`** | JSON 输出（含 apps/controls/states）|
| **`--wait N`** | 等最多 N 秒，直到找到 SourceTree |

## 退出码

| 码 | 含义 |
|---|---|
| **0** | 找到 SourceTree 且枚举 ≥ 10 个控件 ✅ |
| **1** | 找到但控件不足 |
| **2** | pyatspi 不可用 |
| **3** | 超时未找到 |

## 当前状态（基线）

`baseline-before-uia.txt` 是 UIA 实现**之前**的输出：

```
桌面子项数: 1
  [0] 'xdg-desktop-portal-gtk' role=application children=0

❌ 未找到 SourceTree
```

**原因**：`win32_gdi_text.c:229` 的 `UiaReturnRawElementProvider` 是桩（`{ return 0; }`）⇒ WPF 控件不暴露给 AT-SPI。

## 要实现的两层

1. **`UiaReturnRawElementProvider`** —— 返回真正的 COM `IRawElementProviderSimple*`（把 `el` 指针存进 `hwnd → el` 表）
2. **AT-SPI2 `org.a11y.atspi.Accessible` D-Bus 桥接** —— 把这张表暴露成 D-Bus 对象

**详见 `70-operate/已知坑清单.md` 的 K-611 / K-612。**
