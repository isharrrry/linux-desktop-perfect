# 60-deliver · 交付物

本目录回答四个问题：**装什么、怎么装、怎么验、怎么退回**。
所有内容都以 `40-build/`、`50-verify/` 的**实际产物与读数**为准，不写"设计上应该有"的东西。

## 0. 从这里开始

| 我想… | 看 |
|---|---|
| 知道**装了什么、怎么装** | 本文 §2–§3 |
| 知道**Windows 编译的程序集怎么跑起来** | [`外部程序集运行手册.md`](外部程序集运行手册.md)（本文 §3.2 是摘要）|
| 知道**当前哪些判据通过** | [`交付清单.md`](交付清单.md)（从读数自动生成）|
| 知道**读数怎么读、什么不算通过** | [`读数怎么读.md`](读数怎么读.md) |
| 知道**改一个程序集要跑哪几步** | 本文 §7 |
| 知道**怎么退回干净状态** | 本文 §6 |

## 1. 这是什么

`linux-desktop-perfect` 的目标是：让 .NET 的 WPF（桌面 UI 栈）在 Linux 上跑起来，
并且**可被无障碍工具检测**（UIA / AT-SPI 投影）。

交付形态是**一个自包含的 .NET 运行时目录**：`~/.local/share/ldp/dotnet`
（内含 `shared/Microsoft.WindowsDesktop.App/<版本>/` 的 WPF 程序集与 4 个原生 `.so`），
以及**七个界面样本**（`samples/`），用来证明界面能在上面跑、且结构可被断言。

## 2. 装什么（顺序不能变）

```bash
# ① 主构建链：生成 26/27 个 WPF 程序集（ReachFramework 见 §5 已知缺口）
node 40-build/wpf-port/build-port.mjs --map

# ② 打包：把程序集与原生件组装成 Runtime pack
node 40-build/pack/build-pack.mjs

# ③ 安装：解到 ~/.local/share/ldp/dotnet
node 40-build/pack/install-runtime.mjs

# ④ 桥（**必须放在最后**）：wpfgfx_cor3.so
node 40-build/milcore-bridge/build-bridge.mjs --install ~/.local/share/ldp/dotnet
```

**顺序为什么不能变**：`install-runtime` 会重写框架目录，把第 ④ 步装进去的
`wpfgfx_cor3.so` 清掉（已知坑 K-003）。第 ④ 步放最后是硬约束。

派生树的重建用 `node 40-build/appliers/apply.mjs`（它会自动补建
`PresentationBuildTasks`，见 K-002）。

## 3. 怎么跑

### 3.1 跑本仓样本

```bash
source ~/.local/share/ldp/dotnet/env.sh
export DOTNET_ROOT=$HOME/.local/share/ldp/dotnet
export DOTNET_ReadyToRun=0
export WPF_LINUX_WIN32_SHIM=$DOTNET_ROOT/shared/Microsoft.WindowsDesktop.App/*/native/libwpfwin32.so
dotnet samples/WpfFirstWindow/bin/Release/net10.0/WpfFirstWindow.dll
```

需要 X 显示（真桌面或 `Xvfb :N -screen 0 1280x1024x24 &`）。

### 3.2 跑外部（Windows 上编译的）程序集

**结论**：Windows 上 `dotnet build -c Release` 出来的 `net10.0-windows` 托管程序集，拷到 Linux 上
**不做任何重新编译**就能跑（判据 [`check-external-winapp.mjs`](../50-verify/external/check-external-winapp.mjs)；WPF 外部样本 6/6）。
原因：WPF/WinForms 的托管层是 IL，本仓发的是版本号一致的 `Microsoft.WindowsDesktop.App 10.0.11`，
程序要 `10.0.0`，同 major 默认 roll-forward 即加载。

**最短路径**（前提：本机已按 §2 装好运行时）：

```bash
D=~/.local/share/ldp/dotnet
env -i DISPLAY=:91 HOME=$HOME DOTNET_ReadyToRun=0 $D/dotnet /path/to/App.dll
```

只拷三个文件（`<App>.dll` + `<App>.deps.json` + `<App>.runtimeconfig.json`）；
`DOTNET_ReadyToRun=0` 是唯一省不掉的环境变量（D-005）；WinForms 另需框架目录里的 Win32 同名软链。

**完整步骤、每步的输入/输出/替代方式、环境变量实测记录、WPF 与 WinForms 逐项差异、失败对照表**，
见 [`外部程序集运行手册.md`](外部程序集运行手册.md)。

## 4. 怎么验

```bash
node 30-design/checks/check-links.mjs          # 文档链接
node 30-design/checks/check-package.mjs        # 文档包完整性
node 30-design/checks/check-design-ids.mjs     # 设计 ID 覆盖
node 30-design/checks/check-coverage.mjs       # 追溯矩阵覆盖
node 30-design/checks/selftest.mjs             # 判据自检
node 30-design/设计文档包/界面图/build-wireframe.mjs --check
python3 本体/本体校验.py && node 本体/渲染校验.mjs
node samples/gen-samples.mjs --check
node 40-build/appliers/apply.mjs --check

# 【K-007】跑 ACC-012（剪贴板/拖放）前必须先编探针，否则判据会报"缺被测对象：bin/clip-probe"、
#   并被记成 FAIL —— 那是探针没编，不是实现失败。编完立刻回到真实状态（DEGRADED）。
make -C 40-build/native-shim probes

node 50-verify/index/gen-index.mjs             # 汇总全部判据读数
```

单条判据：`node 50-verify/a11y/assert-tree.mjs --scr SCR-01`
（结构判据）、`node 50-verify/render/frame-probe.mjs`（渲染）、
`node 50-verify/image/decode.mjs`（图像解码）、
`node 50-verify/image/icc.mjs`（色彩管理）、
`node 50-verify/text/linebreak.mjs`（文本排版）、
`node 50-verify/window/clipboard.mjs`（剪贴板/XDND）、
`node 50-verify/media/playback.mjs`（媒体，见 §5）。

## 5. 未交付的部分（如实登记，不是"待办清单"）

> **2026-10-31 大幅重写**：多条缺口的**原因被推翻或重新界定**。
> 判定（FAIL/DEGRADED）没变，但"为什么"跟初版不同 —— 照初版读会得出错误结论。

| 缺口 | 现状（2026-10-31 界定） | 证据 |
|---|---|---|
| **ReachFramework**（XPS/打印） | **归因已修正**：不是"上游缺 67 个文件"（误判）。真相 ①上游 csproj 用反斜杠+大小写不敏感路径，Linux 上指向小写 `manager` 失配 ②上游只编 **63** 个（`ReachTokenContext.cs` 是死代码）③真正缺的是 **PF 类型**，上游用 **impl-cycle 独立程序集**提供，而它又要 ReachFramework 的 `PrintTicket` ⇒ **三层 cycle-breaker**。剩 80 个错误全是缺 PF 类型。**2026-10-31 进展**：impl-cycle **已编成**（513024 B）；但引进来会撞 **24 个 CS0433 类型歧义**（impl-cycle 的 `public PrintTicket` vs ReachFramework 自己的），只能靠 **extern alias** 区分，而引用点在上游源码里、大面积改会失去与上游的可对照性 ⇒ **代价大于收益，已回滚但保留工程与产物** | 已知坑 D-024 / D-026 / D-026-解2 |
| **打印路径**（ACC-024） | 判据红：点「打印」后 `job=0`/`pages=0` 不变（需 ReachFramework + shim 的 winspool 面） | `50-verify/读数/ACC-024/` |
| **媒体播放**（ACC-022） | **状态机已通**（手工与判据都能观测 `state=READY → PLAYING`，M94 以来首次）。仍 DEGRADED 的两条原因：①`MilCmdMediaPlayer` 是「收下不渲染」（无 Linux 合成路径）②样本无 `<MediaElement>`。**判据侧的点击问题已修**（K-006：`findWindow` 返回 BadWindow，已加 `windowUsable` 校验）| 已知坑 K-005-修复2、K-006-解 |
| **分页**（ACC-015） | **单段已能排完**（`dcpLim=6015` = 6014 字符 + EOP，`ok=1` 台账可用）；**但 `count-pages` 要排整篇多页，60 s 仍未完成**（原为 20 s 挂死）。已修字体覆盖自旋（32→1024）、blob 共享、整形结果缓存（3×）、两处大锁 | 已知坑 D-019-解…解11 |
| **输入法**（ACC-017） | 环境级阻塞：本机没有输入法服务器（`XOpenIM` 返回 NULL）| 已知坑 D-015 相关条 |
| **WinForms**（ACC-028） | **阻塞解除一半**：gitee 能拉源码（`40-build/upstream/winforms`，97M/4420 .cs）。**量化评估为不做**：需 **410** 个 Win32 函数，本机 shim 覆盖 **103（25%）**，缺 **307**（COM/主题/对话框/输入）；另需**源生成器**与 **IL 项目**。**明确不做**：不用 WPF 模拟 WinForms 去凑 SCR-07 的绿 | 已知坑 D-025 |
| **段落角色** | SCR-02 的段落以 `label` 暴露而非 `paragraph`（设计取舍，理由见《界面设计说明》§2.3） | — |
| **AT-SPI 总线** | UIA 树为**自建投影**（`LdpA11y`），未走真实 AT-SPI 总线 | `40-build/wpf-port/.../a11y/TreeDumper.cs` |

### 5.1 如果只看一件事

**这 7 条里没有一条是"没试"**：有的已量化评估（ACC-028）、有的已推翻归因（ACC-024）、
有的实现已做好卡在判据自身（ACC-022）、有的从"挂死"推进到"能跑但慢"（ACC-015）。
**逐条的证据都指向 `70-operate/已知坑清单.md` 的具体编号**，不是一句"环境限制"。

## 7. 升级（改了一个程序集之后，**已实测**）

日常开发里 95% 的改动是「改一两个 C# 源 → 重建那个工程 → 部署」。这条路径
**不需要**重跑 `build-port --map`，也不需要重跑 `apply.mjs`。

### 7.1 只改了一个项目（例：`LdpA11y`）

```bash
# ① 重建那一个工程（例：无障碍树导出钩子）
cd 40-build/wpf-port
dotnet build src/Microsoft.DotNet.Wpf.Linux/src/a11y/LdpA11y.csproj \
  -c Release -p:UpstreamWpfRoot=/home/links-dev/netTest/GitProj/linux-desktop-perfect/40-build/build/wpf/
cp src/Microsoft.DotNet.Wpf.Linux/src/a11y/bin/Release/LdpA11y.dll ../out/

# ② 打包 + 安装 + 桥（顺序仍然是 K-003 那个顺序）
node 40-build/pack/build-pack.mjs
node 40-build/pack/install-runtime.mjs
node 40-build/milcore-bridge/build-bridge.mjs --install ~/.local/share/ldp/dotnet

# ③ 验证
node 50-verify/a11y/assert-tree.mjs --scr SCR-01
```

**实测（2026-10-30）**：在 `TreeDumper.cs` 里加一行哨兵注释 → 重建（**2 s**）→
`build-pack` + `install-runtime` + `build-bridge`（**21 s**）→ `assert-tree --scr SCR-01`
**PASS**。整条升级路径 **约 23 秒**。

### 7.2 哪些步骤可以跳过

| 改动类型 | build-port --map | build-pack | install-runtime | build-bridge |
|---|---|---|---|---|
| 只改一个框架程序集（如 `LdpA11y`、`PresentationCore`）| 跳过 | **要** | **要** | **要** |
| 只改原生 shim（`native-shim/*.c`）| 跳过 | **要** | **要** | **要** |
| 只改 bridge（`milcore-bridge`、`WpfGfx/*.cs`）| 跳过 | 可选 | 可选 | **要** |
| 改了 `apply.mjs` 的补丁 | **要**（会重建派生树）| **要** | **要** | **要** |
| 改了 `wic-dwrite` / `media-bridge` 的 `.so` | 跳过 | **要** | **要** | **要** |

**两条硬约束**（都来自已知坑）：

- `install-runtime` 会清掉 `wpfgfx_cor3.so` ⇒ **`build-bridge --install` 必须放最后**（K-003）；
- `apply.mjs` 重建派生树会清掉 `PresentationBuildTasks` ⇒ 它会**自动补建**（K-002），但样本要**重新 `dotnet build`**。

## 6. 回滚与重装（**已实测**，不是理论步骤）

### 6.1 回滚

```bash
rm -rf ~/.local/share/ldp/dotnet      # 整目录删除即可
```

本项目不改动系统目录、不写 `/usr/local`、不注册桌面入口；所有产物都在
`~/.local/share/ldp/` 与仓库内的构建目录里。

### 6.2 重装（`2026-10-30` 实测记录）

```bash
$ du -sh ~/.local/share/ldp/dotnet
70M     /home/links-dev/.local/share/ldp/dotnet

$ rm -rf ~/.local/share/ldp/dotnet && ls ~/.local/share/ldp/
（空）

$ node 40-build/pack/install-runtime.mjs
install-runtime: … ← …Microsoft.WindowsDesktop.App.Runtime.linux-x64.10.0.11.nupkg
                 （框架件 61 个、原生件 4 个、NETCore 9.0.4）

$ node 40-build/milcore-bridge/build-bridge.mjs --install ~/.local/share/ldp/dotnet
build-bridge: …wpfgfx_cor3.so 5086064 B，Mil* 导出 70 个

$ du -sh ~/.local/share/ldp/dotnet
70M     /home/links-dev/.local/share/ldp/dotnet      ← 与删除前一致
```

**验证（重装后立刻跑）**：

```bash
$ node 50-verify/render/frame-probe.mjs --frames 2
[ACC-007] TG-003 verdict=PASS diff=0

$ node 50-verify/a11y/assert-tree.mjs --scr SCR-01
[ACC-027] TG-012 verdict=PASS

$ node 50-verify/image/decode.mjs
[ACC-018] …（五格式应用内解码 + 中央像素比对）
```

⇒ **回滚是真回滚、重装能恢复、判据全绿**。`70M` 的大小在删除前后一致，说明
`install-runtime` + `build-bridge` 这两步就足以从零重建整个运行时。

**注意**：重装只要**两步**（`install-runtime` + `build-bridge --install`），
不需要重跑 `build-port`/`build-pack` —— 前提是 `40-build/pack/out/` 里的 nupkg 还在。
如果那份 nupkg 也被删了，就要从 §2 的第 ① 步重新开始。
