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
| S1 进度 | ①`libwpfwin32.so` 编译通过（191 KB，ABI 断言全过，488 导出）；②符号对拍首份**红**（清单 impl 483 中 103 未导出、`.so` 多 105）；③派生树生成通过（7,026 文件，`apply --check` 绿）；④派生编译**已跑通**：`WindowsBase.dll` 1,023,488 B（离线桩包 + 4 个构建属性 + 系统 perl，降级 D-002）；⑤界面结构 diff 通路跑通（fake 源 11/11 节点 0 差异，判 **DEGRADED**；真桥 `SKIP`）；⑥30 条判据脚本全部落地（索引表 30/30）；⑦首错台账：14 件中 8 件编过（含 `WindowsBase`、`System.Xaml`、`PresentationBuildTasks`），其余首错见 [build-matrix.json](40-build/build-matrix.json)；⑧**pack 组装完成**：`build-pack.mjs` 产出 Ref（清单 55 条逐件命中、自产替换 8 件）与 `Runtime.linux-x64`（含 `libwpfwin32.so`）；**7 个样本构建全绿**（全部样本 `dotnet build -c Release` rc=0，XAML 走自产 PBT）；⑨ACC-001/002/003 三份 PASS 读数落 `50-verify/读数/`；⑩**text-stack（MOD-31/32/33/35）已实现**：`libdwlinux.so`（C + `dlopen`）字体枚举 414 族、HarfBuzz shaping、ICU 断行、CFF 轮廓；ACC-014 与 ACC-016 转绿（双路径对拍）；⑪**补丁项 16 落地**：applier 新增 `P16` 定点补丁（`PbtTfm` net6.0 → net10.0），派生树重生成 `--check` 绿，`PresentationCore`/`PresentationUI` 首错从 `NETSDK1005` 推进到 `MSB4278`（补丁 18 待做，见 [build-matrix.json](40-build/build-matrix.json)）；⑫**移植链落地**：`40-build/wpf-port/build-port.mjs` 按依赖序构建 **18/18 成功**（`PresentationCore`/`PresentationFramework`/`ReachFramework`/`System.Printing` 等），两轮破环收敛循环引用，产物映射到派生树 `artifacts/bin`；⑬**程序集身份 P2**：自产件 `AssemblyVersion` 统一 `10.0.0.0`，与 [#pack 文件清单](40-build/pack/FrameworkList.xml) 一致（MSFT 公钥不可得登记 D-003）；⑭**双公钥身份统一**：按清单 token 选公钥（WCP `31bf…` / ECMA `b77a…`），`build-port.mjs` **20/20** 成功；`build-pack.mjs` 替换闸门改为身份校验，替换 14 件后 **ACC-001/002/003/004 全绿**（WPF 覆盖 13/21，其余从上游 pack 取、身份一致）；D-003 收窄为 `cc7b13dd` 组；⑮**text-stack 判据接入**：新增回退链（`FcFontSort`）与双向（ICU `ubidi`）导出，**ACC-013 PASS**（三类样本 0 缺字、回退链一致）、**ACC-015 DEGRADED**（换行/双向双路径一致，分页宿主未接）；⑯**Runtime pack 分发侧落地**：`build-pack.mjs` 的 DepsWriter 把框架 `deps.json` 改为 `linux-x64` 并登记自产内部件（条目+文件一起落），`install-runtime.mjs` 用户目录安装（`--check` 绿）；冒烟读数显示启动已进到 **Application 初始化**阶段，下一阻塞是 `ApplicationInit` 内的 `BadImageFormatException`；⑰**启动环境闭合**：`DOTNET_ReadyToRun=0`（D-005：上游 pack 件是 R2R，Linux 加载抛 `BadImageFormatException`）+ shim 定位 + Xvfb，启动推进到**渲染提交阶段（DUCE Commit）**、窗口已在 Xvfb 上创建成功；首帧仍缺 `wpfgfx_cor3` 的托管桥（milcore 的 18 个 `Mil*` AOT 导出）；⑱**milcore 桥接层落地**：`40-build/milcore` 编译通过，`40-build/milcore-bridge/`（MOD-25 导出层）经 NativeAOT 发布 **`wpfgfx_cor3.so` 5,077,872 B / 70 个 `Mil*` 导出**，`--install` 装入框架后 DUCE 不再缺桥；运行期依赖补装 SkiaSharp 后 `FontFamily` 初始化通过，下一阻塞 `MS.Internal.Classification`；⑲**首帧达成**：补 `win32_classification.c`/`win32_unicode_tables.c` 进 shim 后，`WpfFirstWindow --scr SCR-01` 在 Xvfb 上开窗并渲染（1000×667、223 色、非白 31.3%）；**ACC-007 PASS**（帧非空率）、**ACC-009 PASS**（开窗/移动/缩放/关闭）；⑳**UIA 结构树导出**：`LdpA11y`（`DOTNET_STARTUP_HOOKS` 钩子）在 UI 线程遍历 AutomationPeer，按 IF-071 导出；与元素属性表 **11/11 节点一致**，**ACC-027 PASS**（真 UIA 树）；㉑**DWM 合成真探测**：`DwmIsCompositionEnabled` 改读 X11 `_NET_WM_CM_S0` selection 所有者，配 Fake 合成器与 dwm-probe 两极对照（`.so` 补全后 582 KB / 导出 846），**ACC-026 PASS**；㉒**DPI 三档**：样本读数动态化，判据三档 `Xvfb -dpi 120/144/192` 读数与几何全对，**ACC-011 PASS**；㉓**DUCE 命令覆盖**：5 条未实现命令族登记 D-006（GPU 图像）/D-007（媒体与双缓冲），加 0x00 哨兵豁免，覆盖口径改为「impl + 显式降级 + 哨兵」→ **118/118，ACC-006 PASS**；㉔**输入事件回放**：键入链路打通（`value=abc` 实测），并修掉键入触发的 `UIAutomationCore.dll` 崩溃；鼠标点击聚焦缺 → 登记 D-008，**ACC-010 DEGRADED**；㉕**六主题自产**：`gen-theme-projects.mjs` 按上游 csproj 的 Compile 清单生成 Aero/Aero2/AeroLite/Luna/Royale 5 个主题工程（含 BAML），build-port 26/26、pack 自产替换 **14 → 19**（判据 ACC-020 待接线）；㉖**六主题切换判据转绿**：样本 `--theme` 换字典、树读数新增 `env.theme`、`six-themes.mjs` 真跑六主题（帧指纹 222/438/397/384/554/670 色，均不崩），**ACC-020 PASS**；㉗**窗口激活语义修复**：`SetForegroundWindow`/`SetActiveWindow` 从空实现改为真设 X 输入焦点，并把 D-008（鼠标点击不聚焦）证据链收窄到「事件已到 TextBoxView、焦点仍未设置」；㉘**激活通知（F8）**：`wpf_x11_set_input_focus` 补发 `WM_ACTIVATE`+`WM_SETFOCUS`，`Window.IsActive` false→true，D-008 再收窄到「MouseDevice 按钮态仍是 Released」；㉙**鼠标键态语义**：`GetKeyState(VK_LBUTTON)` 改读翻译层维护的队列态（X 实时查询兜底），D-008 缺口收窄到 `MouseDevice.IsActive`；㉚**D-008 根因锁定**：`MouseDevice.IsActive` 已排除（反射读数 true），`GetKeyState` 读到 0 的原因是 X 事件泵整批抽干（Release 先于派发被处理）；修复方案已登记（鼠标消息携带入队时刻按钮快照）；㉛**D-008 解除**：鼠标消息携带「入队时刻」按钮快照（`s_push_mods` bit4 + 出队 apply，与键盘同一条机制）⇒ `state=Pressed`，**ACC-010 PASS**（点击聚焦 + 键入 + Tab）；㉜**Ribbon 自产**：`System.Windows.Controls.Ribbon` Linux 工程（130 Compile + 4 Page + resx，签名按上游 IVT 用 ECMA key，`SR.Shim.cs` 补资源属性），产物 738 KB，pack 自产替换 **19 → 20**；㉝**Ribbon 判据转绿**：LdpA11y 容器判定扩到 Ribbon 族，`ribbon.mjs` 真跑 SCR-04（结构 page tab list「Ribbon」+ Tab「开始」+ 帧非空 + 点击存活）→ **ACC-021 PASS**；㉞**FlowDocumentReader 判据转绿**：SCR-06 真跑（结构「流文档」+ 布局界限非零 + 帧非空 + 分页点击存活）→ **ACC-025 PASS**；㉟**剪贴板 Win32 面**：新增 `win32_clipboard.c`（X11 CLIPBOARD selection 拥有权 + property 写回）+ 泵的 `SelectionRequest` 分发 + 探针 `clip-probe`；同进程往返通过、跨进程/XDND 未通 → **ACC-012 DEGRADED**（D-010）；㊱**剪贴板跨进程第一跳**：selection owner 换成 shim 自建隐藏窗口 + `wpf_clip_pump`（在 shim 自己的 X 连接上抽干），A 侧已能处理 `SelectionRequest`，B 侧 `GetClipboardData` 仍 NULL（D-010 待续）；㊲**D-010 解除**：`XConvertSelection` 的 requestor 换成请求方自己的窗口（root 没人 select ⇒ 收不到 `SelectionNotify`），跨进程剪贴板读回成功；XDND 未实现登记 D-011 → **ACC-012 DEGRADED**（文本双向往返 PASS）；㊳**AE 回归闸门**：`ae-compare.mjs` 支持 `--baseline` 采集与逐像素 AE 比对（阈值 1%，基线本仓自产并如实标注），7 屏 5 屏通过、2 屏切片未到 → **ACC-008 DEGRADED**；㊴**打印/Ink 判据真跑化**：ACC-024（点「打印」读数不变，缺口 D-012）、ACC-023（`InkCanvas` 启动即 `COM Interop`，缺口 D-013）——两条从"未接入"变"接入且红"；㊵**Ink 的 STA 降级（P19）**：`Thread.SetApartmentState(STA)` 改为 try/catch 降级（并修好 applier 的 `.cs` 定点补丁此前静默不生效），现场从 `COM Interop` 崩变成跨线程 `WM_SIZE` 异常；泵线程限制方案试过但会打红 ACC-010/027，已回滚（正确方向是消息队列按窗口归属）；㊶**D-013 解除**：`push()` 改为按 `wpf_window.owner_thread` 路由窗口消息（Win32 语义：只投给创建它的线程），InkCanvas 能起来并落笔 → **ACC-023 PASS**；㊷**判据全部接入**：ACC-017（IME，shim 的 XIM 入口计数 0）、ACC-022（媒体，播放前后 state 不变）、ACC-028（WinForms，启动即 `DllNotFound(KERNEL32)`）三条从"探测式红"改为真跑并登记 D-014/D-015/D-016 —— 30 条判据再没有"未接入"的；㊸**ReachFramework 工程就位**：按上游 csproj 解析 284 Compile + 2 resx 生成 Linux 工程、补 `System.IO.Packaging 9.0.0`；卡在上游 67 个 XPS 序列化**生成文件**（D-017）；㊹**WIC 判据真跑化**：ACC-018/019 记录「resolver 的 WIC 映射已开 + `WindowsCodecs` P/Invoke 111 处 + `libwpfwic.so` 缺失」→ 登记 D-018；至此 30 条判据全部「接入且带缺口读数」；㊺**XIM 接入第一层**：shim 的 `XOpenIM` + 事件泵 `XFilterEvent`（无 IM 时 no-op），XIM 符号计数 0→8；第二步（IC 预编辑/提交 → `WM_IME_*`）待做，ACC-017 仍 FAIL；㊻**AE 场景扩到 15**：场景表改「样本 + 变体」（改尺寸/滚动/换主题），14 场景采到 **13 屏**基线（含刚修的 `WpfMediaInk`），逐像素 AE 全 0%；仅 WinForms 屏缺（D-016）→ ACC-008 DEGRADED；㊼**XIM 的 IC 接入**：焦点窗口 `XCreateIC`（`XIMPreeditNothing`）+ `XSetICFocus/XDestroyIC`（无 IM 时 no-op）；**D-018 纠偏**：WPF 的 WIC 是 `[ComImport]`、.NET 在 Linux 无 COM ⇒ 只补 `libwpfwic.so` 调不到，须先改上游接口层；㊽**WIC shim 骨架**：`40-build/wic-dwrite/` 产出 `libwpfwic.so`（41 导出）+ `wic-probe`；工厂入口可建（`hr_create=0`）、其余入口明确 `E_NOTIMPL`（`hr_decode=-2147467263`，不返回假句柄）——判据读数细化到「工厂通、解码未实现」；㊾**WIC 的 PNG 解码打通**：`png_decode.c` 用 zlib 自写 PNG 解码（8-bit RGB/RGBA/灰度，16-bit/调色板/隔行明确 `E_NOTIMPL`），接到 WIC 的文件名/帧/尺寸/像素入口 —— 探针 `dims=7x5`、RGBA 首像素正确；仍缺 JPEG/TIFF/GIF/BMP、`CreateDecoderFromStream`、ICC；**context→profile 自管表**：trace 定位到 bridge 的 `GetProfileBytes` 返回 `E_INVALIDARG`（长度却正确）⇒ 改为本 shim 自管 `context → profile` 表（登记点覆盖三处入口），并修掉一处盲解引用导致的段错误；src 侧已取到 588 字节，dst 侧待定位；**非恒等 profile 的转换**：生成 `linear-gamma1.icc`（lcms2 造、gamma 1.0，PIL 参考值 `RGB(48,33,20)`），把 `IWICColorTransform_Initialize` 改成真做 lcms2 转换（修前那句"目标=sRGB"其实等于没转换），bridge 的 `GetProfileBytes` 允许"只问长度"；卡在 `Initialize` 早退；**显式色彩转换端到端**：`wic_decoder` 与 color transform 统一到同一对象头（`wic_object`），`WICCreateColorTransform` 登记进活表，位图源四入口同时接受两类对象 —— 实测源 RGB(200,100,50) 经 `ColorConvertedBitmap` 后仍为 (200,100,50)（BGRA 正确）；**显式色彩转换的验证程序**：`WicApp` 加 `ColorConvertedBitmap` 模式（取源内嵌 profile → 转换 → 回前 8 像素），resolver 加 `mscms.dll` 映射并补 `OpenColorProfile`（ANSI）+ `GetColorProfileHeader`；`WIC-CT` **真的被触达**（计数 1），卡在 color transform 未登记进活表；**WIC 的色彩转换入口**：探明 WPF 的显式色彩转换走 `WindowsCodecsExt.dll`（`WICCreateColorTransform`+`Initialize`），已加映射并实现这两个入口 + `GetSize/CopyPixels`；实测普通 `BitmapImage` **不触发**该路径（只在显式 `ColorConvertedBitmap` 时走）；**ICC 的色彩转换引擎**：`mscms.c` 用系统 **lcms2**（dlopen）实现 mscms 五个入口，探针实测 sRGB→sRGB **恒等成立**（`translate_ret=1, identity=true`）—— 引擎真的在算；仍缺：WPF 渲染管线把转换后像素接到帧上 + 非 RGB8 类型；**ICC 端到端打通**：`GetColorContexts` 里残留的废弃早退代码删掉后 `SetProfile => S_OK`，带内嵌 ICC 的 PNG 应用内加载成功（`6x5/Bgra32`），ACC-019 判据升级为「profile 链路通」；仍缺色彩转换（mscms）；**ICC 的 bridge 符号查找修复**：`libwpfwic` 原来按裸文件名 `dlopen("wpfgfx_cor3.so", RTLD_NOLOAD)` 找 bridge，而 .NET 用完整路径 + `RTLD_LOCAL` 加载 ⇒ 恒查不到；改从 `/proc/self/maps` 取真路径后符号立即可见（该修复对 `CreateDecoderFromStream` 的读流路径同样有效）；**ICC 空上下文与填充**：`WICCreateColorContext`（造空）+ `InitializeFromMemory`（填 profile）+ bridge 的 `LdpMilColorContextSetProfile`，`GetColorContexts` 改为填充语义；卡在 `GetColorContextsHelper` 的二次 `HRESULT.Check`（E_NOTIMPL 来自下游）；**ICC 第一步**：PNG 的 **iCCP** 解析（实测 588 字节、签名 `acsp`）+ bridge 新导出 `LdpMilColorContextFromProfile` + `GetColorContexts` 返回真实句柄；卡在 `WICCreateColorContext`；**JPEG 路线探明**：本机只有 `libjpeg.so.8` 运行库、无头文件，标准 API 要求精确的 struct 大小/error_mgr 布局（实测 560..900 全失败、400 会栈溢出）⇒ **dlopen 路线不可行，JPEG 需自写基线解码**（明确不做"猜 ABI"）；**WIC 的 TIFF 格式**：`tiff_decode.c` **复用系统 libtiff**（dlopen 只碰四个入口，避开 ABI 风险；`TIFFOpen` 只吃路径故写 /tmp 临时文件），处理自下而上行序与 RGBA→BGRA；端到端 `11x7/Bgra32` —— 五格式 **4/5**（PNG/BMP/GIF/TIFF ✓）；**WIC 的 GIF 格式**：`gif_decode.c` 自写 LZW 变体（变长码 + clear code + EOI、4096 字典、KwKwK），支持全局/局部调色板，**只解第一帧**并如实写明；端到端 `8x6/Bgra32` —— 五格式 **3/5**（PNG/BMP/GIF ✓）；**WIC 的 BMP 格式**：`bmp_decode.c` 支持 BI_RGB 24/32bpp（处理"行自下而上 + 4 字节对齐"），加统一分派 `wic_decode_any`；端到端 BMP `9x6/Bgra32`、`5x4/Bgra32` —— ACC-018 升级为「PNG 与 BMP 的应用内解码已通」；**WIC 应用内解码打通**：补 `CreateBitmapFromSource`/`CreateBitmapFromMemory` 后，自产 WPF 的 `BitmapImage` **真加载** testdata PNG 成功（`rgb-7x5 → 7x5/Bgra32`、`rgba-4x3 → 4x3/Bgra32`），ACC-018 判据从"探针层通"升级为"PNG 的应用内解码已通"；**WIC 端到端打通**：根因是两处解码器创建点漏了 `wic_track_add`（解码器不在活表 → `WicShim_OwnsHandle`=0 → `MILQueryInterface` 退 E_HANDLE）；补登记后依次补上 `GetResolution`/`GetThumbnail`（上游真码 `0x88982f44`）/`GetColorContexts`，端到端推进到 `CachedBitmap` 的 `CreateBitmapFromSource`；**并纠正了上一轮"须改造上游"的误判**；**WIC 句柄契约 + 最终判定**：补 `WicShim_OwnsHandle/AddRef/Release/HandleCount/PeakHandleCount`（bridge 的归属契约）；完整堆栈把卡点定位到 `BitmapSource.set_WicSourceHandle` 的 `Marshal.GetComInterfaceForObject` —— **.NET 在 Linux 无 COM interop ⇒ `BitmapImage` 应用层路径需改造上游**（探针层已通）；**WIC 的 ole32 归属修正**：`ole32.dll` 属 WIC 组（→`libwpfwic.so`），补该 so 的 `CoInitialize` 族后异常消失，并撤掉误加进 Win32 组的重复映射；补 `GetFrameCount`；端到端卡点推进到 `E_HANDLE`（句柄语义）；**WIC 组件/能力信息**：补齐 `GetDecoderInfo` + `IWICComponentInfo_*`（6 个）与 `IWICBitmapCodecInfo_*`（9 个，按 PNG 真值），shim 加 `CoInitialize` 族并把 `ole32.dll` 加入映射表，`build-port` 改为只映射成功的件；端到端卡点推进到 `CoInitialize in ole32.dll`（映射闭环待查）；**WIC 落地运行时**：pack 的 native 目录 1→3 个 .so（win32/wic/dw），补 `CreateDecoderFromFileHandle`，新增无 XAML 的端到端验证应用 `50-verify/image/wic-app`（自产 WPF 的 `BitmapImage` 真加载 PNG），读数精确到 `EntryPointNotFound(GetDecoderInfo)`；**WIC 流入口打通**：bridge 加 `LdpMilStreamRead/Seek`（`StreamDescriptor` 槽序 slot1/2），`CreateDecoderFromStream` 经 `dlsym` 两级查找接到同一个 PNG 解码；`wic-stream-probe` 实测 RGB 7x5 / RGBA 4x3 —— WPF 主路径的下游就位；仍缺 JPEG/TIFF/GIF/BMP 与 ICC；AT-SPI 总线投影（MOD-81 第二半）仍待 |
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
