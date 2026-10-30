# LinuxDesktopPerfect — 让 WindowsDesktop 在 Linux 上源码级完整运行

**中文**（英文版待补）

> 这个仓不是又一个 UI 框架，也不重新发明 XAML。它把 `Microsoft.WindowsDesktop.App` 的托管层原样保留（`dotnet/wpf` 全量 + `dotnet/winforms` 兼容层），把 Windows 专有的原生面（PresentationNative、wpfgfx、Win32/GDI+、DirectWrite/WIC、DWM、UIA、打印、Media、Ink、MSAA）逐个换成 Linux 实现，并用自产的 `Microsoft.WindowsDesktop.App` 框架包把这条栈接回标准 `dotnet build`。

**平台与基线**：Linux x64 / X11 首发；.NET 10 LTS（10.0.x release 分支）；上游 `dotnet/wpf` 只读快照 + 补丁式 applier；MIT。

**验收口径**：**判据先行 + 无障碍优先**。每个模块先写失败判据、跑出红读数，再写实现转绿（ADR-014）；界面成立与否先看 AT-SPI 无障碍树（角色/名称/状态/动作），像素只作第二判据（ADR-015）。这条口径让「界面是否真的跑起来了」变成可机读、可复算的结构比对，而不是看图说话。

---

## 它解决什么问题

WPF 的托管代码是 MIT 开源的，但它的运行时有一半不在托管层：

- `PresentationNative_cor3.dll` 没有源码，PTS/LineServices、文本分析、147 个入口只在二进制里；
- `wpfgfx_cor3.dll` 有源码，但绑定 D3D/DirectWrite；
- `gdiplus.dll`、`user32.dll`、`dwrite.dll`、`WindowsCodecs.dll`、`UIAutomationCore.dll` 是 Windows OS 组件；
- `Microsoft.WindowsDesktop.App` 的 Ref/Runtime pack 只发 `win-*` RID，Linux 上 `UseWPF=true` / `UseWindowsForms=true` 直接不可用；
- `dotnet/winforms` 的托管层同样开源，但控件几乎都是 USER32/GDI 包装，`System.Drawing.Common` 在非 Windows 上默认抛 `PlatformNotSupportedException`。

结果就是：WPF 应用源码能在 Linux 上编过一部分，但「开窗、画字、输入、打印、无障碍」每一条都断在某个 Windows 原生件上。

LinuxDesktopPerfect 把这条链拆成**八个子系统**（见[《概要设计》](30-design/概要设计.md) §4），用契约（ABI、DUCE 字节流、pack 清单）把它们接起来；每条契约都能独立版本化与对拍。

### 产品定义

**一句话**：LinuxDesktopPerfect 是 **WindowsDesktop 在 Linux 上的完整替代运行时**——源码级重编即可运行；范围是 WPF 全量、WinForms 到兼容层（ADR-011），WindowsDesktop 的每一条 Windows 专有路径都有一个 Linux 落点或一条明确记录的降级。

**给谁用**：手上有一份 WindowsDesktop 应用源码（以 WPF 为主，WinForms 可用）、想在 Linux 上编译并完整运行的个人开发者与小团队；需要把这类应用迁到国产/信创 Linux 桌面，但不愿意重写 UI 的团队。

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
30-design/       概要设计（ADR/MOD/SYS/R）与设计文档包（SM/IF/TBL/SAFE/SCR/GAP/TG）+ checks + model
40-build/        实现：wpf-port / native-shim / milcore / text / wic / pack / winforms
50-verify/       测试用例、执行记录、对照基准
60-deliver/      打包、安装、部署、回滚
70-operate/      已知坑、故障模板、诊断开关
80-derived/      SBOM、派生制品
本体/            运行时本体（24 类/26 关系/40 规则/11 动作/18 术语）+ 校验器 + 交互模型页
90-archive/      旧版本与归档
```

**纪律**：`20-spec` 管「要什么」，`30-design` 管「怎么建」，`40-build` 管「建出来」，`50-verify` 管「凭什么说建成了」。四边冲突时按 `30-design/设计文档包/01` §2 的权威顺序裁决。

---

## 当前状态

| 项 | 状态 |
|---|---|
| 需求文档 | V0.1 草案（[《需求与验收判据文档》](20-spec/需求与验收判据文档.md)） |
| 概要设计 | V0.1 草案（[《概要设计》](30-design/概要设计.md)），**待签署** |
| 设计文档包 | V0.1 草案（[《00-文档包总览与评审检查单》](30-design/设计文档包/00-文档包总览与评审检查单.md)：00–08 + 界面 + 界面图 + checks + model），**待签署** |
| 文档门禁 | 5 条检查器（链接/文档包/编号/覆盖/自检）全 `rc=0` |
| 本体 | V0.1：[《本体说明》](本体/本体说明.md)，24 类/26 关系/40 规则/11 动作/18 术语；校验器与模型页就绪 |
| 开工前置 | **5/6 满足**（[《09-开工前置与切片计划》](30-design/设计文档包/09-开工前置与切片计划.md) §2.4：B1~B5、B7~B10 已关闭，B6 六件桩件完成三件） |
| 实现骨架 | 已起：上游快照（整仓 7,380 文件，可用 `SNAPSHOT.json` 复算）+ milcore 71 个 `.cs` + native-shim 56 件 + wpf-port 341 件 + 四个契约文件 + 7 个样本工程 + 6 个桩件 + 环境探头 + **自产 pack 组装（`40-build/pack/build-pack.mjs`）+ 自产 targets（`40-build/sdk/`）** |
| S1 进度 | ①`libwpfwin32.so` 编译通过（191 KB，ABI 断言全过，488 导出）；②符号对拍首份**红**（清单 impl 483 中 103 未导出、`.so` 多 105）；③派生树生成通过（7,026 文件，`apply --check` 绿）；④派生编译**已跑通**：`WindowsBase.dll` 1,023,488 B（离线桩包 + 4 个构建属性 + 系统 perl，降级 D-002）；⑤界面结构 diff 通路跑通（fake 源 11/11 节点 0 差异，判 **DEGRADED**；真桥 `SKIP`）；⑥30 条判据脚本全部落地（索引表 30/30）；⑦首错台账：14 件中 8 件编过（含 `WindowsBase`、`System.Xaml`、`PresentationBuildTasks`），其余首错见 [build-matrix.json](40-build/build-matrix.json)；⑧**pack 组装完成**：`build-pack.mjs` 产出 Ref（清单 55 条逐件命中、自产替换 8 件）与 `Runtime.linux-x64`（含 `libwpfwin32.so`）；**7 个样本构建全绿**（全部样本 `dotnet build -c Release` rc=0，XAML 走自产 PBT）；⑨ACC-001/002/003 三份 PASS 读数落 `50-verify/读数/`；⑩**text-stack（MOD-31/32/33/35）已实现**：`libdwlinux.so`（C + `dlopen`）字体枚举 414 族、HarfBuzz shaping、ICU 断行、CFF 轮廓；ACC-014 与 ACC-016 转绿（双路径对拍）；⑪**补丁项 16 落地**：applier 新增 `P16` 定点补丁（`PbtTfm` net6.0 → net10.0），派生树重生成 `--check` 绿，`PresentationCore`/`PresentationUI` 首错从 `NETSDK1005` 推进到 `MSB4278`（补丁 18 待做，见 [build-matrix.json](40-build/build-matrix.json)）；⑫**移植链落地**：`40-build/wpf-port/build-port.mjs` 按依赖序构建 **18/18 成功**（`PresentationCore`/`PresentationFramework`/`ReachFramework`/`System.Printing` 等），两轮破环收敛循环引用，产物映射到派生树 `artifacts/bin`；⑬**程序集身份 P2**：自产件 `AssemblyVersion` 统一 `10.0.0.0`，与 [#pack 文件清单](40-build/pack/FrameworkList.xml) 一致（MSFT 公钥不可得登记 D-003）；⑭**双公钥身份统一**：按清单 token 选公钥（WCP `31bf…` / ECMA `b77a…`），`build-port.mjs` **20/20** 成功；`build-pack.mjs` 替换闸门改为身份校验，替换 14 件后 **ACC-001/002/003/004 全绿**（WPF 覆盖 13/21，其余从上游 pack 取、身份一致）；D-003 收窄为 `cc7b13dd` 组；⑮**text-stack 判据接入**：新增回退链（`FcFontSort`）与双向（ICU `ubidi`）导出，**ACC-013 PASS**（三类样本 0 缺字、回退链一致）、**ACC-015 DEGRADED**（换行/双向双路径一致，分页宿主未接）；⑯**Runtime pack 分发侧落地**：`build-pack.mjs` 的 DepsWriter 把框架 `deps.json` 改为 `linux-x64` 并登记自产内部件（条目+文件一起落），`install-runtime.mjs` 用户目录安装（`--check` 绿）；冒烟读数显示启动已进到 **Application 初始化**阶段，下一阻塞是 `ApplicationInit` 内的 `BadImageFormatException`；⑰**启动环境闭合**：`DOTNET_ReadyToRun=0`（D-005：上游 pack 件是 R2R，Linux 加载抛 `BadImageFormatException`）+ shim 定位 + Xvfb，启动推进到**渲染提交阶段（DUCE Commit）**、窗口已在 Xvfb 上创建成功；首帧仍缺 `wpfgfx_cor3` 的托管桥（milcore 的 18 个 `Mil*` AOT 导出）；⑱**milcore 桥接层落地**：`40-build/milcore` 编译通过，`40-build/milcore-bridge/`（MOD-25 导出层）经 NativeAOT 发布 **`wpfgfx_cor3.so` 5,077,872 B / 70 个 `Mil*` 导出**，`--install` 装入框架后 DUCE 不再缺桥；运行期依赖补装 SkiaSharp 后 `FontFamily` 初始化通过，下一阻塞 `MS.Internal.Classification`；⑲**首帧达成**：补 `win32_classification.c`/`win32_unicode_tables.c` 进 shim 后，`WpfFirstWindow --scr SCR-01` 在 Xvfb 上开窗并渲染（1000×667、223 色、非白 31.3%）；**ACC-007 PASS**（帧非空率）、**ACC-009 PASS**（开窗/移动/缩放/关闭）；⑳**UIA 结构树导出**：`LdpA11y`（`DOTNET_STARTUP_HOOKS` 钩子）在 UI 线程遍历 AutomationPeer，按 IF-071 导出；与元素属性表 **11/11 节点一致**，**ACC-027 PASS**（真 UIA 树）；㉑AT-SPI 总线投影（MOD-81 第二半）与其余界面判据（DUCE 覆盖、DPI、剪贴板）仍待 |
| 代码 | 未开始（迁移方案见概要设计 §2.5） |

---

## 维护约束（人 + Agent）

改任何文档前先读 [AGENTS.md](AGENTS.md)（Agent 硬约束）与 [CONTRIBUTING.md](CONTRIBUTING.md)（简版）；名称以 [《术语表》](00-admin/术语表.md) 为准，引用规则以 [《引用与名称规范》](00-admin/引用与名称规范.md) 为准。改完必须跑：

```bash
# 文档门禁
node 30-design/checks/check-links.mjs
node 30-design/checks/check-package.mjs
node 30-design/checks/check-design-ids.mjs
node 30-design/checks/check-coverage.mjs
node 30-design/checks/selftest.mjs        # 改规则/改检查器后必跑

# 生成物与本体
node 30-design/设计文档包/界面图/build-wireframe.mjs --check
python3 本体/本体校验.py
node 本体/渲染校验.mjs
```

五条文档门禁 + 三条生成物/本体校验全 `rc=0` 才算完成；先修文档，不许放宽脚本。

## 许可

- 本仓新增内容：MIT（见 `LICENSE`）。
- 上游 `dotnet/wpf` / `dotnet/windowsdesktop` / `dotnet/sdk` / `dotnet/runtime`：MIT，保留原始版权头。
- 字体：SIL OFL 1.1；GStreamer：LGPL-2.1；HarfBuzz：MIT；FreeType：FTL/GPLv2 双许可。
