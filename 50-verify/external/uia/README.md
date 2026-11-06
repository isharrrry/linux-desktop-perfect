# UIA / AT-SPI 验证脚本

## 用法

```bash
# 先启动 SourceTree
export DOTNET_ROOT=~/.local/share/ldp/dotnet
FX=~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11
cd /tmp/stpkg/SourceTree/app-3.4.15
DISPLAY=:303 DOTNET_ROOT=$DOTNET_ROOT DOTNET_ReadyToRun=0 \
  LD_LIBRARY_PATH="$FX/native:$FX:" WPF_LINUX_WIN32_SHIM=$FX/libwpfwin32.so \
  ~/.local/share/ldp/dotnet/dotnet SourceTree.exe &

# 等 20 秒后跑验证
sleep 20
DISPLAY=:303 python3 50-verify/external/uia/dump_tree.py
```

## 验收标准

- **输出里找到 `SourceTree`**（`app.name` 含 "sourcetree"）
- **枚举出至少 10 个控件**（含 `getRoleName()`）
- **退出码 0**

## 当前状态

**未通过**：`win32_gdi_text.c:229` 的 `UiaReturnRawElementProvider` 是桩（`{ return 0; }`）⇒ WPF 控件不暴露给 AT-SPI。

**要修的地方**：
1. `UiaReturnRawElementProvider` 实现（返回真正的 COM `IRawElementProviderSimple*`）
2. AT-SPI2 `org.a11y.atspi.Accessible` D-Bus 桥接
