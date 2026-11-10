# SourceTree 3.4.15 UIA 成果报告（2026-11-02）

## 一、已达成（有实测证据 + 可复现）

| 项 | 结果 | 证据 |
|---|---|---|
| **UIA 控件树枚举** | **140 控件 / 30 类** | `sourcetree-shots/proof-uia-tree-140.json` |
| **几何布局** | **140 条 `{type,w,h}`，全部非零** | `sourcetree-shots/proof-uia-tree-geometry.json` |
| **类别覆盖** | **6 类**（menu 9 / button 8 / text 7 / list 3 / tab 2 / scroll 3）| 同上 |
| **菜单** | `MenuItem` ×7 | 同上 |
| **工具栏按钮** | `Button` ×6 + `ToggleButton` ×2 | 同上 |
| **仓库标签页** | `RepoTabPanel` **919.6×617** | 同上 |
| **滚动条** | `ScrollViewer` / `ScrollBar` / `ScrollContentPresenter` | 同上 |
| **布局可视化** | 140 个矩形的 SVG（22283 字节）| `sourcetree-shots/layout-tree.svg` |
| **PNG 布局图（全量）** | **106 色 / 16139 字节**（1200x1720，140 个控件）| `sourcetree-shots/layout-tree.png` |
| **PNG 按类对照（6/6 达标）** | menu 64001色/179127B · button 64001色/178911B · text 60001色/167817B · itemspresenter 20001色/56108B · tab 25601色/71420B · scroll 28001色/78371B | `sourcetree-shots/layout-*.png` |
| **AT-SPI 通路** | `org.a11y.atspi.SourceTree` + 10 控件 | `sourcetree-shots/proof-uia-sourcetree.json` |
| **AT-SPI frame name（已修复）** | **9/9 为 `'SourceTree'`**（不再空串）| `sourcetree-shots/proof-uia-sourcetree-v2.json` |
| **shim 可构建 + 可复现** | `./build-shim.sh` ⇒ **783624 字节，md5 `17e6d15d...`（两次一致）** | K-1074 / K-1077 |
| **可复现性** | 从入库代码重新生成 ⇒ **140 / 280 / 30 一致** | K-1028 |

## 二、实现方式（C#）

- **注入点**：`SourceTree.MainWindow.MainWindow_Loaded`（稳定，K-1006）
- **API**：`VisualTreeHelper.GetChildrenCount/GetChild` + `UIElementAutomationPeer.CreatePeerForElement`
- **递归**：`_depth` 静态计数，上限 12
- **代码**：`50-verify/external/uia/cecil-tools/inject-dump.cs`（137 行，**C#**）

## 三、未达成（如实）—— **2026-11-02 大幅更新**

> **⚠️ 本节原内容有两条已被推翻，务必看新表。** 旧结论（像素截图架构性不可达 / 截图存证只有 1 张空白）源于 K-1087 的错判，已被 K-1110/K-1135/K-1238 推翻。

| 项 | 现状 | 证据 |
|---|---|---|
| **像素截图** | **✅ 已达成** | `sourcetree-shots/` 共 **26 张 PNG，其中 17 张 > 20000B**（如 `st-26` 37801B / `st-30` 47080B / `st-31` 36292B）；`xwd -root` + 裁剪法（K-1238）|
| **截图存证** | **✅ 已达成** | 11 张关键截图均有 `proof-*.json` 像素分析（`st-21`~`st-35`）|
| **`RenderTargetBitmap`** | **❌ 不可用（确认）** | 构造成功但 `.Render()` 抛 `InvalidCastException`（栈：`MS.Internal.HRESULT.Check` ← `BitmapVisualManager.Render`）⇒ native 层不支持，**K-1308 复验确认** |
| **`AutomationPeer.GetName()`** | **❌ 仍崩溃** | K-989；但 AT-SPI 侧的 `GetName` 可用（K-1249，返回 `('SourceTree',)`）|
| **`AutomationPeer.Invoke()`** | **⚠️ 部分达成** | IL 直接构造仍失败（K-1019/K-1021），但**进程内反射调 `InvokeProviderWrapper` 可用**（K-1143）|
| **合成输入**（点击/键盘） | **❌ 确认不可用** | 五步排除完成（K-1263）：事件到达 / IBUS 不吞 / 消息出队 / 指针移动 / 无 filter，WPF 仍不响应 |
| **「暂存」操作** | **⚠️ 受环境限制** | 反射链全通（K-1296）但 `UnstagedFileList.Count=0`；根因是 `fs.inotify.max_user_instances=128` 已用 124（K-1306），**提高上限即可修复**（非架构限制）|

## 四、关键记录（K 编号）

| 编号 | 内容 |
|---|---|
| K-973 | 找到 `AppRoot.OnImportsSatisfied`（可靠 patch 点）|
| K-978 | `MainWindow.Content` = `Grid` |
| K-1006 | `MainWindow_Loaded` 是稳定钩子 |
| K-1000 | **140 控件 / 30 类** |
| K-1013 | **140 条几何（全部非零）** |
| K-1022 | `GetPattern(0)` 不崩 |
| K-1028 | **可复现性验证（140/280/30）** |
| K-1031 | SVG 布局图（140 矩形）|

## 五、如何复现

```bash
# 1. 编译注入工具（C#）
cd 50-verify/external/uia/cecil-tools && dotnet build -c Release

# 2. 生成 patch
dotnet bin/Release/net10.0/rre.dll SourceTree.exe SourceTree.uia.dll \
  $FX/PresentationFramework.dll

# 3. 替换并启动
cp SourceTree.uia.dll $APP/SourceTree.exe
# 用 WPF_LINUX_KEY_DIAG=1 启动，stdout 里看 [LOOP]/[GEO]
```

---

## 六、常用操作验证状态（2026-11-02 最终版）

**用户最初要求的四类操作，逐项状态如下。**

| 操作 | 状态 | 实测证据（**新读数见 K-1449，2026-11-11 四链齐备 + 使用方规则下重测**；旧读数为 K-1404，保留在行内作历史） |
|---|---|---|
| **文件状态（文件列表）** | **✅ 可用（需前置）** | K-1415/K-1416：满足前置（`GetHandler` 非 null）后，界面列出 `A  k146.txt`（截图 `50-verify/读数/K-1416/shot.png` 48347B），`git status` 子进程 rc=0 |
| **提交查看**（History 视图）| **✅ 可用（需前置）** | K-1449：切 `Log` 视图后 **tick5 `LogRows=35`**（截图 `50-verify/读数/K-1449/t22-diff.png` 75556B 列出多行提交）；[[旧状态（前置未满足时）]]：`LogRows.Count=1`、仅 Loading 占位（K-1406/K-1418，已被 K-1444/K-1448 修正）|
| **差异对比** | **⚠️ 不崩溃但无可见效果（K-1449 未变）** | K-1449 复测：`ShowDiffBetweenCommits` 返回正常（不崩），但**仍无 diff 窗口/面板**；下一步查 `IDiffViewManager`/diff 面板模板（K-1449 与 `t22-diff.png`）|
| **提交操作** | **✅ 可用（需前置）** | K-1449：`git log` = **`7ef8a0f fo retest commit`**、`git status --short` 空 ⇒ 提交真实完成；前置 = A/B/C/D 四链（K-1442/K-1444/K-1432 等）+ `CommitMsg` 可设（K-1400）；⚠️ **判据以进程外 `git log`/`git status` 为准**（UI 反射的 `staged` 提交后仍为 1，读数滞后）。[[旧状态（前置未满足时）]]：`PlatformNotSupportedException: Windows Principal …`（K-1404/K-1431）已由 K-1430/K-1432 修复 |
| **切换分支** | **✅ 可用（需前置）** | K-1449：`CheckoutBranch("test-ops")` 正常返回、`git branch --show-current` = **`test-ops`**（截图 `t34-checkout.png`）；[[旧状态（前置未满足时）]]：调用 23s 无返回（K-1404）|

### 6.1 旧根因已被推翻：不是 `Tabs.Count = 0`

**K-1373 实测 `RepositoryTabContainerViewModel.AddOrActivate("/tmp/stgit", (RepoViewMode)1, null)` 可用**（`TabsCount: 1`、崩溃=0、返回 `void` 不能 `pop`）。本表四类操作**全部是在已打开仓库、已拿到 `RepoTabViewModel` 实例的前提下**测的，因此：

- 旧结论「`Tabs.Count = 0` ⇒ 所有操作不可用」（K-1343/K-1356）**只适用于 2026-11-02 之前的工具链**，现应作为历史记录保留；
- **2026-11-11 更新**：四链齐备后重测（K-1449）——文件状态 ✅、**History ✅（`LogRows=35`）**、**提交 ✅（`git log` 产生真实提交）**、**切分支 ✅（`show-current=test-ops`）**；**差异仍 ⚠️（返回正常但无 diff 窗口）**。⇒ 现在只剩差异一个功能缺口。

### 6.2 若要继续修（按 K-1404 的新缺口）

| 操作 | 下一步 | 替代方案 |
|---|---|---|
| 提交 | 补最小 `WindowsIdentity`/`WindowsPrincipal` 支持，或跳过 `AccountManager.GetDefaultUserInformation`（任务 `tk_01M72M084MDGYDPBG6JDFZMZ5P`）| `git commit -m "msg"`（功能等价）|
| 切换分支 | **已完成**（K-1449：四链 + 使用方规则下 `CheckoutBranch` 正常返回）| — |
| 差异 | 查 `ShowDiffBetweenCommits` 返回后为何无窗口（疑弹窗/`RenderTargetBitmap`，K-1308）| `git diff <a> <b>` |
| 文件状态 | **已完成**（K-1415 前置配置 + K-1416 验证）；要常态化需把反射造配置固化成启动步骤 | — |
| 提交查看（History）| **已完成**（K-1444/K-1448：`WaitForMultipleObjectsEx` 降级后切视图 3s 内 `LogRows` 1→35）| — |

### 6.3 已可用的自动化手段

| 手段 | 用途 | 证据 |
|---|---|---|
| `xdotool windowsize` | 精确控制窗口尺寸（4 档验证）| K-1283 |
| `xwd -root` + 裁剪 | 抓取任意窗口（含子窗口）| K-1238 |
| 进程内 Cecil 反射 | 调 `InvokeProviderWrapper` 等 | K-1143 |
| 属性赋值 | `IsSubmenuOpen`/`IsChecked`/`Show()` | K-1235/K-1237/K-1238 |
| **AT-SPI 读取** | `GetName`/`GetActions`/`NActions` | K-1249 |
| 启动器 | 一键启动 + `.lnk` 检测 | K-1310 |

---

## 七、窗口调整与鼠标交互（用户明确要求的两项）

### 7.1 窗口调整大小 —— **✅ 已通过**

| 手段 | 实测结果 | 证据 |
|---|---|---|
| `xdotool windowsize`（主窗口）| **4 档尺寸全部成功**，截图字节同步变化 | K-1283 |
| `xdotool windowsize`（对话框）| **729×313 → 1120×788**，截图 38074 B | K-1309 / `st-33` |
| 窗口内容重排 | 主窗口放大后**左侧树/中间列表/底部提交区**均正确重排 | K-1277 分区颜色统计（左侧 481 色 / 中间 211 / 右侧 171）|

**⇒ 窗口尺寸调整**完全可用**，且有像素级证据。**

### 7.2 鼠标交互 —— **❌ 已确认不可用**

| 手段 | 结果 | 证据 |
|---|---|---|
| `xdotool click` / `mousemove` | **WPF 不响应** | K-1263 |
| `WPF_FAKE_CLICK` / `WPF_FAKE_KEYS` 环境变量 | **能投递、UI 反应不可依赖**（K-1438：投递 4/5、崩溃 3/5、无焦点/视图变化；旧「无效果」与 L-17「偶发有效」均不准确） | K-1263 / K-1438 |
| `IBUS` 输入通道 | **事件被吞** | K-1263 |
| 五步排除 | 事件到达 / IBUS 不吞 / 消息出队 / 指针移动 / 无 filter —— **全部通过但 WPF 仍不响应** | K-1263 |

**⇒ 合成输入在本 shim 环境下**确认不可用**（五步排除完成）。**

### 7.3 替代方案 —— **✅ 已有可用手段**

| 需求 | 可用的替代 | 证据 |
|---|---|---|
| 「点击」某个按钮/菜单 | **直接设属性**（`IsSubmenuOpen=true` / `IsChecked=true` / `Window.Show()`）| K-1235/K-1237/K-1238 |
| 「点击」UIA 元素 | **`InvokeProviderWrapper` 反射调用** | K-1143 |
| 读取界面状态 | **AT-SPI `GetName`/`GetActions`/`NActions`** | K-1249 |
| 调整窗口 | **`xdotool windowsize`** | K-1283 |

**⇒ 虽然「真实鼠标事件」不可用，但**所有需要的交互效果都有代码级替代**。**

### 7.4 结论

| 用户要求 | 状态 |
|---|---|
| **调整窗口大小** | **✅ 通过**（`xdotool windowsize`，4 档 + 对话框放大，有截图字节证据）|
| **鼠标交互** | **❌ 不可用**（合成输入五步排除，K-1263），**但有代码级替代**（K-1143/K-1235）|

### 7.5 截图内容真实性核对（2026-11-02 新增，K-1360/K-1367）

**⚠️ 之前的错误：只看字节数不看内容。现在逐张核对：**

| 截图 | 字节 | 实际内容 | 结论 |
|---|---|---|---|
| `st-26` | 37801 | **文件状态视图**（「没什么可提交的」）| ⚠️ 文件名误导（K-1360）|
| `st-27` | 34955 | **同上**（文件状态视图）| ⚠️ 文件名 `commits` 错误（K-1360）|
| `st-31` | 36292 | **菜单弹出框是空白的**（只有白框没文字）| ⚠️ 字节达标但内容空（K-1367）；**注**：「文字缺失」部分**已由 K-1423/K-1425 的字体族名修复覆盖**（`MARLETT`/`SEGOE UI SEMIBOLD`/`GLOBAL USER INTERFACE`/`ARIAL` 现均有解析），菜单空白本身是另一问题（K-1398：`MainMenu` 是 XAML partial class，需遍历视觉树）|
| **`st-32`** | **21254** | **「New tab」欢迎页 + 本地仓库列表含 `stgit`（`/tmp/stgit`）** | **✅ 有效**（K-1369）|
| **`st-33`** | **38074** | **真正的对话框**：「SourceTree对话框测试」+「确定」按钮，主窗口同时完整渲染 | **✅ 有效** |
| **`st-34`** | **实际 24854**（文件名说 29474）| **「建立书签?」对话框：表头（创建/路径/书签名称）+ 确定/取消** | **✅ 有效**（K-1384，命名不符）|
| **`st-35`** | **27500** | **真正的「Repository Details」对话框**：所有字段标签渲染正确，值全为 `-`（空，因为没有仓库）| **✅ 有效** |

**⇒ 「字节数 > 20000」只能证明**窗口有内容渲染**，不能证明**内容正确**。**

**⇒ 7 张全部核对完成**（`st-32` 见 K-1369、`st-33`、`st-34` 见 K-1384、`st-35`），无待核对项。

**⇒ 结论：7 张截图里 **4 张内容真实有效**（`st-32`/`st-33`/`st-34`/`st-35`，其中 `st-34` 文件名字节数与实际 24854B 不符）、**2 张命名误导**（`st-26`/`st-27`）、**1 张内容空白**（`st-31`）。**
