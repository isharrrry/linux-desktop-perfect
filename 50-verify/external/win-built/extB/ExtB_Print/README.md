# `ExtB_Print` —— Windows 上编译，Linux 上免编运行（**通过**）

## 来源
`PC@192.168.15.125`（Windows 10.0.22631，.NET SDK 10.0.203），
工作目录 `C:\Users\PC\ldpwb\extB\ExtB_Print`，`dotnet build -c Release`（0 错 0 警）。

## 源项目形状
`net10.0-windows` + `UseWPF=true`，手写 `Main`（无 `App.xaml`/`ApplicationDefinition`）。
窗口 `Title = "ExtB Print [EXT-22]"`；`Button`（`AutomationProperties.Name = "btnPrint"`，文案「打印」）；
`TextBlock`（初始 `printed=pending`，点击后更新为 `printed=<结果>` 并同步 `AutomationProperties.Name`）。

点击后构造 `FixedDocument`（A4 = 793.7×1122.5 DIP，一行 `hello-print`），
先试 `LocalPrintServer.GetDefaultPrintQueue()` + `XpsDocumentWriter`；失败则用
`XpsDocument` + `XpsDocumentWriter` 写到 `Path.GetTempPath()/extb-print.xps`。

## Linux 运行结果（实测）

| 项 | 读数 |
|---|---|
| 窗口 | `0x200005` / 标题 `ExtB Print [EXT-22]` ✓ |
| 截图字节数 | **12511**（`shot-*.png`）|
| UIA 节点数 | **3**：`frame:ExtB Print [EXT-22]` / `push button:btnPrint` / `label:printed=pending` |
| 进程存活 | ✓（`exited=null`）|
| 点击 `btnPrint` 后 UIA | `label:` 从 **`printed=pending` → `printed=xps:2327`** |
| XPS 产物 | **2327 字节**，见 `evidence-print-output.xps` |

XPS 是**完整 OPC 包**（7 条目）：
```
FixedDocumentSequence.fdseq      195 B
Documents/1/FixedDocument.fdoc   156 B
Documents/1/Pages/1.fpage        265 B   ← 含 Text="hello-print"，A4 尺寸
_rels/.rels                      286 B
Documents/1/_rels/FixedDocument.fdoc.rels  383 B
_rels/FixedDocumentSequence.fdseq.rels     288 B
[Content_Types].xml              503 B
```

**打印队列不可用**：`[EXTB_PRINT] printqueue failed: NullReferenceException` ——
Linux 侧没有 CUPS 队列（`lpstat -p` 无输出），`GetDefaultPrintQueue()` 返回 null。
**这是预期回退**，XPS 写出成功即证明 `FixedDocument` 管线可用。

## 文件
| 文件 | 说明 |
|---|---|
| `ExtB_Print.dll` (8192 B) | Windows 侧编译的托管程序集 |
| `ExtB_Print.deps.json` (424 B) / `ExtB_Print.runtimeconfig.json` (519 B) | Windows 侧生成 |
| `shot-*.png` | Linux 运行截图（12511 B）|
| `tree-*.json` | UIA 树（LdpA11y 导出）|
| `verify-*.json` | 验证器读数 |
| `run-*.log` | 运行日志 |
| `evidence-print-output.xps` | 打印产物（2327 B）|
