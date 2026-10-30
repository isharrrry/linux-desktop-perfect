# LinuxDesktopPerfect — 让 WPF 在 Linux 上源码级完整运行

**中文**（英文版待补）

> 这个仓不是又一个 UI 框架，也不重新发明 XAML。它把 `dotnet/wpf` 的托管层原样保留，把 Windows 专有的原生面（PresentationNative、wpfgfx、Win32/GDI+、DirectWrite/WIC、DWM、UIA、打印、Media、Ink）逐个换成 Linux 实现，并用自产的 `Microsoft.WindowsDesktop.App` 框架包把这条栈接回标准 `dotnet build`。

**平台与基线**：Linux x64 / X11 首发；.NET 10 LTS（10.0.x release 分支）；上游 `dotnet/wpf` 只读快照 + 补丁式 applier；MIT。

---

## 它解决什么问题

WPF 的托管代码是 MIT 开源的，但它的运行时有一半不在托管层：

- `PresentationNative_cor3.dll` 没有源码，PTS/LineServices、文本分析、147 个入口只在二进制里；
- `wpfgfx_cor3.dll` 有源码，但绑定 D3D/DirectWrite；
- `gdiplus.dll`、`user32.dll`、`dwrite.dll`、`WindowsCodecs.dll`、`UIAutomationCore.dll` 是 Windows OS 组件；
- `Microsoft.WindowsDesktop.App` 的 Ref/Runtime pack 只发 `win-*` RID，Linux 上 `UseWPF=true` 直接不可用。

结果就是：WPF 应用源码能在 Linux 上编过一部分，但「开窗、画字、输入、打印、无障碍」每一条都断在某个 Windows 原生件上。

LinuxDesktopPerfect 把这条链拆成**四个可独立验收的子系统**，用契约（ABI、DUCE 字节流、pack 清单）把它们接起来。

### 产品定义

**一句话**：LinuxDesktopPerfect 是 **WPF 在 Linux 上的完整替代运行时**——源码级重编即可运行，WPF 的每一条 Windows 专有路径都有一个 Linux 落点或一条明确记录的降级。

**给谁用**：手上有一份 WPF 源码、想在 Linux 上编译并完整运行的个人开发者与小团队；需要把 WPF 应用迁到国产/信创 Linux 桌面，但不愿意重写 UI 的团队。

**核心闭环**：

```text
WPF 源码 → 自产 Ref pack 编译 → 自产 Runtime pack 加载
              ↑                        ↓
        PresentationBuildTasks     native 面（shim/milcore/text/wic）
              ↑                        ↓
        自产 SDK targets ←──── X11 · Skia · HarfBuzz · Fontconfig · CUPS · AT-SPI · GStreamer
```

**不做什么**：

- **不做二进制兼容**：不加载 Windows 编出来的 dll/exe，不仿真 `Microsoft.WindowsDesktop.App` 的 Windows 宿主身份（ADR-001）。
- **不重写 WinForms 控件树**：WinForms 只做兼容层——能加载、基础控件可用、MSAA 桥接到 AT-SPI；设计器、ActiveX/DCOM 明确不做（ADR-011）。
- **不仿真 Windows IME 协议**：输入法接 ibus/fcitx5（ADR-008）。
- **不做 DRM**：MsDrm/PlayReady 路径明确降级（D-001），返回错误，不静默。
- **不跟随上游 main**：pin 在 10.0.x，不追 12.0-alpha。

### 与 WPFOnLinux 的关系

本仓是 `WPFOnLinux` 的继任者，不是它的分支：

| 资产 | WPFOnLinux 现状 | 本仓处置 |
|---|---|---|
| 上游 WPF 托管源 | `src/Microsoft.DotNet.Wpf/`（4,188 个 `.cs`） | 只读快照，补丁走 applier，不在树上手改 |
| C 的 Win32/PresentationNative shim | `src/WpfGfx.Linux.Native/`（25,734 行，56 个文件） | 迁入 `40-build/native-shim/`，ABI 契约独立版本化 |
| MIL 渲染后端 | `src/WpfGfx.Linux/`（**当前 git 0 tracked，只有 bin**） | 迁入前必须先找回源码（DQ-002） |
| 构建编排与验收 | `src/Linux/build/`（2,522 个 tracked 文件） | 迁入 `40-build/`，保留可复算判据 |
| 托管侧替代 | `PtsCache.Linux.cs`、FontLayoutStripping、PBT.Linux | 留 `40-build/wpf-port/`，不进原生仓 |

---

## 仓库结构

```text
00-admin/        项目章程、决策记录、命名与编号纪律
10-input/        原始需求输入（聊天记录、参考资料、上游快照清单）
20-spec/         需求与验收判据（REQ/ACC/NF/XIF/DQ 的唯一事实源）
30-design/       概要设计与设计文档包（ADR/MOD/SM/IF/R 的唯一事实源）
40-build/        实现：wpf-port / native-shim / milcore / text / wic / pack / winforms
50-verify/       测试用例、执行记录、对照基准
60-deliver/      打包、安装、部署、回滚
70-operate/      已知坑、故障模板、诊断开关
80-derived/      SBOM、派生制品
90-archive/      旧版本与归档
```

**纪律**：`20-spec` 管「要什么」，`30-design` 管「怎么建」，`40-build` 管「建出来」，`50-verify` 管「凭什么说建成了」。四边冲突时按 `30-design/设计文档包/01` §2 的权威顺序裁决。

---

## 当前状态

| 项 | 状态 |
|---|---|
| 需求文档 | V0.1 草案（[《需求与验收判据文档》](20-spec/需求与验收判据文档.md)） |
| 概要设计 | V0.1 草案（[《概要设计》](30-design/概要设计.md)），**待签署** |
| 设计文档包 | 未开始（概要设计签署后补齐 01–07 + 界面 + checks + model） |
| 代码 | 未开始（迁移方案见概要设计 §2.5） |

---

## 维护约束（人 + Agent）

改任何文档前先读 [AGENTS.md](AGENTS.md)（Agent 硬约束）与 [CONTRIBUTING.md](CONTRIBUTING.md)（简版）；名称以 [《术语表》](00-admin/术语表.md) 为准，引用规则以 [《引用与名称规范》](00-admin/引用与名称规范.md) 为准。改完必须跑：

```bash
node 30-design/checks/check-links.mjs
node 30-design/checks/check-package.mjs
node 30-design/checks/selftest.mjs        # 改规则/改检查器后必跑
```

两条 `rc=0` 才算完成；先修文档，不许放宽脚本。

## 许可

- 本仓新增内容：MIT（见 `LICENSE`）。
- 上游 `dotnet/wpf` / `dotnet/windowsdesktop` / `dotnet/sdk` / `dotnet/runtime`：MIT，保留原始版权头。
- 字体：SIL OFL 1.1；GStreamer：LGPL-2.1；HarfBuzz：MIT；FreeType：FTL/GPLv2 双许可。
