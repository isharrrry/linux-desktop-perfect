# `ExtB_InkMedia` —— Windows 上编译，Linux 上免编运行（**含 MediaElement：崩溃**）

## 来源
`PC@192.168.15.125`（Windows 10.0.22631，.NET SDK 10.0.203），
`C:\Users\PC\ldpwb\extB\ExtB_InkMedia`，`dotnet build -c Release`（0 错 0 警）。

## 源项目形状
`net10.0-windows` + `UseWPF=true`，手写 `Main`。窗口 `Title = "ExtB InkMedia [EXT-21]"`，1000×700。
- 上方 `InkCanvas`（高 300，`AutomationProperties.Name = "inkcanvas"`，`EditingMode = Ink`）
- 下方 `MediaElement`（`LoadedBehavior = Manual`）
- `TextBlock`（`strokes=0`，由 `InkCanvas.Strokes.StrokesChanged` 更新）

`MediaElement.Source` 放在 `Dispatcher.BeginInvoke(ApplicationIdle)` 里延迟设置，
且只在 `tiny.wav` 存在时才设 —— **目的是让窗口先显示出来**。**实测这一步没救回来**（见下）。

## 实测结果（**失败**）

| 项 | 读数 |
|---|---|
| 窗口 | `0x200005` / 标题 `ExtB InkMedia [EXT-21]` ✓ **窗口建出来了** |
| 截图字节数 | **328**（几乎空白）|
| UIA 节点数 | **0**（树来不及导出）|
| 进程 | **`SIGABRT`（已退出）** |

## 根因（托管栈完整，已确定）
```
Unhandled exception. System.NotImplementedException: The method or operation is not implemented.
   at MS.Internal.HRESULT.Check(Int32 hr)
   at System.Windows.Media.Composition.DUCE.Channel.SendCommandMedia(ResourceHandle mediaHandle, SafeMediaHandle pMedia, Boolean notifyUceDirect)
   at System.Windows.Media.MediaPlayerState.SendMediaPlayerCommand(Channel channel, ResourceHandle handle, Boolean notifyUceDirectly)
   at System.Windows.Media.MediaPlayerState.SendCommandMedia(Channel channel, ResourceHandle handle, Boolean notifyUceDirectly)
   at System.Windows.Media.MediaPlayer.UpdateResourceInternal(Channel channel)
   at System.Windows.Media.MediaPlayer.AddRefOnChannelCore(Channel channel)
   at System.Windows.Media.RenderData.System.Windows.Media.Composition.DUCE.IResource.AddRefOnChannel(Channel channel)
   at System.Windows.UIElement.RenderContent(RenderContext ctx, Boolean isOnChannel)
   ...
   at System.Windows.Media.CompositionTarget.Compile(Channel channel)
   at System.Windows.Interop.HwndTarget.OnResize()
   at MS.Win32.HwndWrapper.WndProc(...)
```

**`MediaElement` 只要出现在可视树里**（`LoadedBehavior=Manual`、**完全不设 `Source`** 也一样），
首次渲染 `Compile` 阶段就会 `AddRefOnChannel` → `SendCommandMedia`，
而 MIL（`wpfgfx_cor3.so`）在 Linux 上的媒体通道**未实现**，返回 `E_NOTIMPL`
→ `HRESULT.Check` 抛 `NotImplementedException` → 未处理 → `SIGABRT`。

**⇒ 这不是「设了 Source 起不来」，而是「有 MediaElement 就起不来」。**

## 验证「设 Source 是否更糟」的尝试
`tiny.wav`（3244 B，0.2 s 静音 8 kHz 单声道）已在源目录生成，但**未随构建产物拷到输出目录**
（本次产出按要求只含 `.dll`/`.deps.json`/`.runtimeconfig.json`）。
因此本次运行 `tiny.wav` 不存在、`Source` 从未被设置 —— **仍然崩溃** ⇒ 证明与 `Source` 无关。

## 文件
| 文件 | 说明 |
|---|---|
| `ExtB_InkMedia.dll` (8192 B) + `.deps.json` + `.runtimeconfig.json` | Windows 侧产物 |
| `shot-*.png` (328 B) | 崩溃前的空画面 |
| `verify-*.json` / `run-*.log` | 验证读数与完整崩溃栈 |
