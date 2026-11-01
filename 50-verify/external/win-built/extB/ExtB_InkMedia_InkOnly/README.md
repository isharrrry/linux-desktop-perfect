# `ExtB_InkMedia_InkOnly` —— **隔离诊断变体**（无 MediaElement）

## 为什么有它
规格要求的 `ExtB_InkMedia` 同时含 `InkCanvas` 与 `MediaElement`。
实测 **`MediaElement` 会让应用在首次渲染时 `SIGABRT`**（见 `../ExtB_InkMedia/README.md`），
所以另编一个**只去掉 `MediaElement`** 的变体，单独验证 `InkCanvas` + 笔画计数链路。

## 来源
同 `ExtB_InkMedia`：`PC@192.168.15.125`，.NET SDK 10.0.203，`dotnet build -c Release`（0 错 0 警）。

## 实测结果

| 项 | 读数 |
|---|---|
| 窗口 | `0x200005` / 标题 `ExtB InkOnly [EXT-21b]` ✓ |
| 截图字节数 | **12837** |
| UIA 节点数 | **3**：`frame:ExtB InkOnly [EXT-21b]` / **`drawing area:inkcanvas`** / `label:strokes=0` |
| 进程存活（静置）| ✓ |
| **在 InkCanvas 上按下鼠标** | **SIGSEGV（exit 139，段错误）**，无托管异常 |

## 结论
- ✅ **`InkCanvas` 能渲染、能被 UIA 识别为 `drawing area`、`strokes=0` 标签正常显示**
- ❌ **任何指针按下都会让进程段错误**，`strokes` 计数**从未变成非 0**
- X 事件确实投递到了窗口：日志里 `[E3-REPLAY] ev=press ... deliver=1` 与 `ev=release ... deliver=1`
  都出现，**紧接着进程就没了** —— 崩溃发生在托管事件处理之后、WISP/笔输入原生命中链路里

## 原生取证（gdb 直接启动子进程，`ptrace_scope=1` 允许跟踪子进程）
```
Thread 1 "dotnet" received signal SIGSEGV, Segmentation fault.
0x00007fff7a8b5297 in ?? ()
#0  0x00007fff7a8b5297 in ?? ()
#1  0x0000555555654488 in ?? ()
#2  0x0000000000000000 in ?? ()
```
- 崩溃 PC 落在 **匿名可执行映射**（`0x7fff7a880000 - 0x7fff7a8b6000`，偏移 +0x35297）—— 即 JIT 代码区
- 回溯只有 3 帧且 `#2` 是 `0x0` ⇒ 栈帧链断裂，典型特征是**通过无效函数指针调用**
- 与「WISP/PenImc 的回调/COM 槽位在本仓是返回 0 的空桩」相吻合；**未取到更精确的托管帧**

## 文件
| 文件 | 说明 |
|---|---|
| `ExtB_InkMedia_InkOnly.dll` (7168 B) + `.deps.json` + `.runtimeconfig.json` | Windows 侧产物 |
| `shot-*.png` (12837 B) / `tree-*.json` / `verify-*.json` / `run-*.log` | 静置态证据 |
| `evidence-ink-click-sigsegv-gdb.log` | gdb 抓到的 SIGSEGV + 回溯 + 映射 |
| `evidence-ink-click-run.log` | 点击后运行的日志（停在 E3-REPLAY press/release 之后）|
| `evidence-ink-click-probe.log` | 单击即崩的那次探针日志 |
