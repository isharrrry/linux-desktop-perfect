# `extFce` —— 挂 `FirstChanceException` 的 WinForms 样本（诊断用）

## 它是什么

**Windows 上编译**的 WinForms 程序，控件结构与 `extA/ExtA_WinForms` **完全一致**
（Label/TextBox/Button/CheckBox/ComboBox/ListBox + `clicks=N`），
但多了两样东西：

1. `AppDomain.CurrentDomain.FirstChanceException` 处理器 —— 打印**每个首次机会异常的完整 `ToString()`（含栈）**
2. 一个 3 秒 `Timer` 自动 `Close()` —— 保证进程自己退出、不挂住

**为什么需要它**：`ExtA_WinForms` 的 `ThreadException` 处理器只打 `e.Message`，
那 4 个 `NotImplementedException` **没有栈**，无法定位。加上 `FirstChanceException` 就拿到了。

## 文件

| 文件 | 说明 |
|---|---|
| `ExtFce.dll` (9216 B) | Windows 上编译的托管程序集 |
| `ExtFce.deps.json` (412) / `ExtFce.runtimeconfig.json` (519) | 依赖与框架引用 |
| `run.log` (196 行) | Linux 上运行的完整 stderr（含全部 `=== FCE:` 块）|
| `shot.png` (859 B) | 运行中的截图（窗口与 6 个控件布局正确）|

## 🎯 结论：4 个 `NotImplementedException` 的来源

**全部来自同一处，且栈只有一帧**：

```
System.NotImplementedException: The method or operation is not implemented.
   at System.Drawing.BufferedGraphics.RenderInternal(HandleRef refTargetDC)
```

**栈只有一帧 ⇒ `RenderInternal` 自己就抛**。对照源码确认
（`40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.Unix.cs:89`）：

```csharp
private void RenderInternal(HandleRef refTargetDC)
{
    throw new NotImplementedException();        // ← 就是这里
}
```

**而同一个文件里 `Render(Graphics)` 是**有实现**的**：
```csharp
public void Render(Graphics target)
{
    if (target == null) return;
    target.DrawImage(membmp, size);             // ← 可用
}
```

Windows 版的对照（`BufferedGraphics.Windows.cs:79`）走的是 `BitBlt`（源 DC ← 成员位图；目标 DC ← 参数）。

**⇒ 本质：WinForms 的双缓冲绘制在 Unix 后端**完全没实现**。**
这解释了「`FillRect` 能画出东西（背景+文字，来自 shim 的 `DEFPAINT` 兜底），
但控件看起来仍是"死的"」—— 因为**最终合成那一步被丢了**。

## 建议的修法（~5 行，可行性已被实测支撑）

```csharp
private void RenderInternal(HandleRef refTargetDC)
{
    using (Graphics g = Graphics.FromHdc(refTargetDC.Handle))
        Render(g);                              // 复用已实现的 Render(Graphics)
}
```

**可行性依据**：`Graphics.FromHdc(<GpGraphics*>)` 能成功建出 `Graphics` 并画出真像素
（子代理用 pixel 采样验证过：`pixel(40,40) = A=255,R=255,G=0,B=0`）。

## 另一个发现：`DllNotFoundException: SHLWAPI.dll`

```
Unable to load shared library 'SHLWAPI.dll'
```

- **只影响 1 个 P/Invoke**：`Windows.Win32.PInvoke::SHAutoComplete`（在 `System.Windows.Forms.Primitives.dll`）
- **非致命**：被 `ThreadException` 捕获，进程照常 `Application.Run 已返回`
- **修法**：shim 里加 `SHAutoComplete` 桩（返回 `S_OK` 或 `E_NOTIMPL`），或把 `SHLWAPI.dll` 也映射到 shim

## 复现方法

`$FX` 里的 SDC 是**原版**（没有 `IGraphicsContextInfo` 注入）。要复现这 4 个
`NotImplementedException`，必须用**注入版 SDC**。本报告用的是符号链接农场，**不改 `$FX`**：

```bash
D=~/.local/share/ldp/dotnet
FX="$D/shared/Microsoft.WindowsDesktop.App/10.0.11"
R=/tmp/fxroot
rm -rf $R; mkdir -p $R/shared/Microsoft.WindowsDesktop.App/10.0.11
cp "$D/dotnet" $R/dotnet            # **必须 cp，不能 ln**（host 按真实路径解析，符号链接会指回 $D）
ln -s "$D/host" $R/host
ln -s "$D/shared/Microsoft.NETCore.App" $R/shared/Microsoft.NETCore.App
for f in "$FX"/*; do ln -s "$f" "$R/shared/Microsoft.WindowsDesktop.App/10.0.11/$(basename $f)"; done
rm -f "$R/shared/Microsoft.WindowsDesktop.App/10.0.11/System.Drawing.Common.dll"
ln -s /tmp/redir/out4.dll "$R/shared/Microsoft.WindowsDesktop.App/10.0.11/System.Drawing.Common.dll"

DISPLAY=:303 env -i PATH=/usr/bin:/bin HOME=$HOME DISPLAY=:303 \
  DOTNET_ROOT=$R DOTNET_ReadyToRun=0 \
  LD_LIBRARY_PATH="$R/shared/Microsoft.WindowsDesktop.App/10.0.11/native:$R/shared/Microsoft.WindowsDesktop.App/10.0.11:" \
  WPF_LINUX_WIN32_SHIM=$R/shared/Microsoft.WindowsDesktop.App/10.0.11/libwpfwin32.so \
  timeout 30 $R/dotnet ExtFce.dll
```

**⚠️ 踩到的坑**：第一版用 `ln -s` 链接 `dotnet`，结果 host 按**真实路径**解析框架目录，
仍然加载了 `$D` 里的原版 SDC（日志里照样是 `IGraphicsContextInfo.GetHdc` 的 `EntryPointNotFoundException`）。
**必须 `cp` 那个 71 KB 的 host 二进制。**
