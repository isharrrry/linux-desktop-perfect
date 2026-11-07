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
