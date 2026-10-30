# 60-deliver · 交付物

本目录回答四个问题：**装什么、怎么装、怎么验、怎么退回**。
所有内容都以 `40-build/`、`50-verify/` 的**实际产物与读数**为准，不写"设计上应该有"的东西。

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

```bash
source ~/.local/share/ldp/dotnet/env.sh
export DOTNET_ROOT=$HOME/.local/share/ldp/dotnet
export DOTNET_ReadyToRun=0
export WPF_LINUX_WIN32_SHIM=$DOTNET_ROOT/shared/Microsoft.WindowsDesktop.App/*/native/libwpfwin32.so
dotnet samples/WpfFirstWindow/bin/Release/net10.0/WpfFirstWindow.dll
```

需要 X 显示（真桌面或 `Xvfb :N -screen 0 1280x1024x24 &`）。

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

| 缺口 | 现状 | 证据 |
|---|---|---|
| **ReachFramework**（XPS/打印） | 未自产：上游 67 个 `Serialization/Manager/*.cs` 在源码树与 GitHub 上都不存在（D-017） | `40-build/wpf-port/build-report.json` |
| **打印路径**（ACC-024） | 判据红：点「打印」后 `job=0`/`pages=0` 不变（需 ReachFramework + shim 的 winspool 面） | `50-verify/读数/ACC-024/` |
| **媒体播放状态机**（ACC-022） | 降级为 DEGRADED：媒体桥能解第一帧与元数据、播放器对象已建，但 `MediaPlayer.Open` 在 `CreateMedia` 收到 `E_NOTIMPL` | `70-operate/已知坑清单.md` D-015 |
| **输入法**（ACC-017） | 环境级阻塞：本机没有输入法服务器（`XOpenIM` 返回 NULL），预编辑/提交路径无法真验 | 同上 |
| **WinForms**（ACC-028） | 需上游 winforms 源码 + 自产 ~50 万行 Forms；SCR-07 的树因此无法导出 | `50-verify/读数/ACC-027/` |
| **段落角色** | SCR-02 的段落以 `label` 暴露而非 `paragraph`（设计取舍，理由见《界面设计说明》§2.3） | 同上 |
| **AT-SPI 总线** | UIA 树为**自建投影**（`LdpA11y`），未走真实 AT-SPI 总线 | `40-build/wpf-port/.../a11y/TreeDumper.cs` |

## 6. 回滚

```bash
rm -rf ~/.local/share/ldp/dotnet      # 整目录删除即可
```

本项目不改动系统目录、不写 `/usr/local`、不注册桌面入口；所有产物都在
`~/.local/share/ldp/` 与仓库内的构建目录里。
