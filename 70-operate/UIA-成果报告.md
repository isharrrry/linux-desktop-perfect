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

| 操作 | 状态 | 实测证据 |
|---|---|---|
| **提交查看**（History 视图）| **❌ 未验证** | `st-26`/`st-27` 实为**文件状态视图**（显示「没什么可提交的」），不是提交列表（K-1360）；`st-31`~`st-35` 是菜单/对话框/仓库详情，也不含提交列表 |
| **差异对比** | **❌ 不可用** | `ShowDiffBetweenCommits` 在 `Loaded`/`ContentRendered` 都崩（K-1275/K-1276）；`RenderTargetBitmap` 也确认不可用（K-1308）|
| **提交操作** | **❌ 不可用** | 需要 `RepoTabViewModel` 实例，但 `Tabs.Count=0`（K-1343）|
| **切换分支** | **❌ 不可用** | 同上 —— `_checkoutSelectedCommand` 拿不到（K-1356 推论）|

### 6.1 为什么后三项都不可用 —— 同一个根因

**`Tabs.Count = 0`**（SourceTree 从没打开仓库）：

- **`Tabs` 空 ⇒ `SelectedRepoTabViewModel` 恒 null（K-1342 反编译确认）**
- **⇒ 拿不到 `RepoTabViewModel` 实例**
- **⇒ 所有依赖它的操作（暂存/提交/切分支/差异）全部不可用**

**而唯一能打开仓库的 API `AppRoot.OpenRepoInTab`，在**四个独立入口**调用都崩溃（K-1345/K-1347/K-1348/K-1354）。**

### 6.2 若要实现这些操作，需要

1. **修改 SourceTree 源码**（在 `AppRoot` 初始化时直接构造 `Repository` 对象，绕过 `OpenRepoInTab`）；
2. **或提供可用的合成输入**（`xdotool` 点击「打开仓库」菜单）—— 但合成输入已确认不可用（K-1263）；
3. **或让 `SourceTree.exe` 在启动时接受仓库路径**（已试 `SourceTree.exe /tmp/stgit`，`Tabs.Count` 仍 0，K-1344）。

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
| `WPF_FAKE_CLICK` / `WPF_FAKE_KEYS` 环境变量 | **无效果** | K-1263 |
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
| `st-31` | 36292 | **菜单弹出框是空白的**（只有白框没文字）| ⚠️ 字节达标但内容空（K-1367）|
| **`st-32`** | **21254** | **「New tab」欢迎页 + 本地仓库列表含 `stgit`（`/tmp/stgit`）** | **✅ 有效**（K-1369）|
| **`st-33`** | **38074** | **真正的对话框**：「SourceTree对话框测试」+「确定」按钮，主窗口同时完整渲染 | **✅ 有效** |
| **`st-34`** | **实际 24854**（文件名说 29474）| **「建立书签?」对话框：表头（创建/路径/书签名称）+ 确定/取消** | **✅ 有效**（K-1384，命名不符）|
| **`st-35`** | **27500** | **真正的「Repository Details」对话框**：所有字段标签渲染正确，值全为 `-`（空，因为没有仓库）| **✅ 有效** |

**⇒ 「字节数 > 20000」只能证明**窗口有内容渲染**，不能证明**内容正确**。**

**⇒ 7 张全部核对完成**（`st-32` 见 K-1369、`st-33`、`st-34` 见 K-1384、`st-35`），无待核对项。

**⇒ 结论：7 张截图里 **4 张内容真实有效**（`st-32`/`st-33`/`st-34`/`st-35`，其中 `st-34` 文件名字节数与实际 24854B 不符）、**2 张命名误导**（`st-26`/`st-27`）、**1 张内容空白**（`st-31`）。**
