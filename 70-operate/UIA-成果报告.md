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
| **布局可视化** | 140 个矩形的 SVG | `sourcetree-shots/layout-tree.svg` |
| **AT-SPI 通路** | `org.a11y.atspi.SourceTree` + 10 控件 | `sourcetree-shots/proof-uia-sourcetree.json` |
| **可复现性** | 从入库代码重新生成 ⇒ **140 / 280 / 30 一致** | K-1028 |

## 二、实现方式（C#）

- **注入点**：`SourceTree.MainWindow.MainWindow_Loaded`（稳定，K-1006）
- **API**：`VisualTreeHelper.GetChildrenCount/GetChild` + `UIElementAutomationPeer.CreatePeerForElement`
- **递归**：`_depth` 静态计数，上限 12
- **代码**：`50-verify/external/uia/cecil-tools/inject-dump.cs`（137 行，**C#**）

## 三、未达成（如实）

| 项 | 原因 |
|---|---|
| **像素截图** | `wpfgfx` 的 `Present` 无 Linux 输出（L-1/L-6）；`RenderTargetBitmap` 也不可用（K-1007）|
| **`AutomationPeer.GetName()`** | 崩溃（K-989）⇒ name 只能用 `GetType().FullName` |
| **`AutomationPeer.Invoke()`** | IL 构造反复失败（K-1019/K-1021/K-1024/K-1025），`GetPattern` 本身可用（K-1022）|

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
