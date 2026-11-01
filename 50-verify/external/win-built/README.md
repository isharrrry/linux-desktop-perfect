# `win-built/` —— **Windows 上编译**的产物（供「免编运行」验证）

本目录下的每个子目录 = **一个在 Windows 机器上 `dotnet build -c Release` 出来的可运行程序**，
连同它在 Linux 上免编运行得到的证据。

## 为什么放这里

`AGENTS.md` §5 的目录职责里 `50-verify/` 是「判据与记录」；`50-verify/external/winapp/`
是 K-013 就确立的先例（Windows 编译的 `WinApp.dll`）。本目录是它的扩展。

## 每个样本目录里有什么

| 文件 | 说明 |
|---|---|
| `<Name>.dll` | **Windows 上编译**的托管程序集（唯一的「被测对象」）|
| `<Name>.deps.json` | Windows 侧生成的依赖描述 |
| `<Name>.runtimeconfig.json` | 框架引用（`Microsoft.WindowsDesktop.App 10.0.0` ⇒ 由 10.0.11 roll-forward）|
| `shot-*.png` | Linux 上运行时的**全屏截图**（视觉证据）|
| `tree-*.json` | Linux 上 `LdpA11y` 导出的 **UIA 树**（结构证据）|
| `verify-*.json` | 验证器写的**读数**（窗口/截图字节数/UIA 节点数/日志尾部）|
| `run-*.log` | 运行时 stdout+stderr（出问题先看它尾部）|
| `src/`（部分样本）| 该程序在 Windows 上编译时用的 `.cs`/`.csproj`（便于人工对照源码↔UIA 树）|

## 怎么复现

```bash
cd /home/links-dev/netTest/GitProj/linux-desktop-perfect
# 前置：三个自建 .so 必须在 $FX 根目录
sh 40-build/install-shims.sh --check

# 验证任意一个样本（自己起 Xvfb + openbox；144 DPI 场景加 --display）
node 50-verify/external/verify-win-built.mjs --name <子目录>/<样本名> --title "<窗口标题前缀>"
```

退出码 `0` = 出画面且 UIA 树非空；`1` = 缺窗口/没截图/无 UIA。

## 已验证的样本（2026-11-01）

| 样本 | 在 Windows 上编译 | 在 Linux 免编运行 | 截图 | UIA 节点 |
|---|---|---|---|---|
| `ProbeWpf`（基准 WPF）| ✅ | ✅ | 13246 | **5** |
| `extA/ExtA_WinForms`（WinForms 六控件）| ✅ | ⚠️ 建窗成功、**绘制抛异常** | 2328 | 0 |
| `extA/ExtA_Input`（WPF 焦点/输入）| ✅ | 见子代理报告 | — | — |
| `extB/ExtB_InkMedia_InkOnly`（InkCanvas）| ✅ | ✅ | 12837 | **3** |
| `extB/ExtB_InkMedia`（Ink + MediaElement）| ✅ | ⚠️ 截图几乎空白 | 328 | 0 |
| `extB/ExtB_Print`（打印/XPS）| ✅ | ✅ | 12511 | **3** |
| `extC/ExtC_BindStyle`（绑定/样式/动画）| ✅ | ✅ | 14912→15870 | **4** |
| `extC/ExtC_Dpi`（DPI 读数）| ✅ | ✅ | 25735 | **2** |

## ⚠️ 这能证明什么、不能证明什么（不要过度解读）

**能证明**：
1. **Windows 上编译的 `net10.0-windows` 产物，在 Linux 上不需要任何重新编译就能运行**（WPF 6/6 成功）；
2. UIA（AT-SPI）树可达 ⇒ 控件结构在 Linux 侧被正确导出；
3. 交互可用（点击后文本 `hello→bound`、动画 4 帧 MD5 不同）。

**不能证明**：
1. **「与 Windows 上运行时画面/结构逐项一致」** —— SSH 登录的 PowerShell 在 **session 0**，
   `CopyFromScreen` 抛 `Win32Exception`、`MainWindowHandle=0`（K-252/K-253），
   **Windows 侧的窗口既截不了图也遍历不了 UIA**；
2. **像素级视觉一致** —— 即使能截，跨 Windows/Linux 的 DPI、主题、字体也不同；
3. **WinForms 自绘** —— `ExtA_WinForms` 的绘制仍抛
   `EntryPointNotFoundException @ IGraphicsContextInfo.GetHdc`（K-249/K-250），
   画面里看到的控件是 shim 的 `BeginPaint` 兜底画的（K-163/K-164）。

**替代证据**：`src/` 里的源码与 `tree-*.json` 的节点名可人工对照 —— **这是当前能做到的最强一致性证据**。
