# System.Windows.Extensions（Linux 原生替身）· 变更说明

## 一句话

官方包在非 Windows 上把 `System.Xaml.Permissions.XamlAccessLevel` 实现成**直接抛**
`PlatformNotSupportedException`；而 WPF 的 `XamlReader.LoadBaml` 只要程序集里生成了
`GeneratedInternalTypeHelper` 就**必然**调它 ⇒ **任何正常构建的第三方 WPF 程序集一装 BAML 就崩**。
本工程给出 Linux 原生实现（不抛），并通过 `ExcludeAssets="runtime"` 替换官方包的**运行期**实现。

## 实测证词（`#34` 波）

```
Unhandled exception. System.PlatformNotSupportedException: System.Windows.Extensions types are not supported on this platform.
   at System.Xaml.Permissions.XamlAccessLevel.AssemblyAccessTo(Assembly assembly)
   at System.Windows.Markup.XamlReader.LoadBaml(...)      ← PresentationFramework/System/Windows/Markup/XamlReader.cs:1096-1104
   at System.Windows.Application.LoadComponent(...)
   at HandyControlDemo.App.InitializeComponent()
```
A/B：把 PF 换成 Debug（`445a278b4a17ba07`）与 Release（`47e51c58bcfe8547`）**失败位置逐字相同**
⇒ 与 PF 配置无关，是"程序集里有 internal type helper"这件事触发的。

## 覆盖与不覆盖

- 覆盖：`XamlAccessLevel`、`SystemSound`/`SystemSounds`、`SoundPlayer`。
- **不覆盖**：`X509Certificate2UI`、`X509SelectionFlag`（Linux 上没有证书选择 UI）。
  谁用它们，谁在**编译期**缺类型 —— 比运行期抛异常更早更清楚。

## 语义边界（明说）

- `XamlAccessLevel`：只保存"程序集名 / 类型名"两个字段，**不做任何安全判定**（.NET Core 上没有 CAS）。
  这不比官方包弱：官方包在 Windows 上做的也只是"标记 + 由 XamlObjectWriter 决定可见性"。
- 声音：`Play()` **不播**（没有 XBell/音频通道就不产生声音），也不抛异常打断应用；调用次数进程内计数。
