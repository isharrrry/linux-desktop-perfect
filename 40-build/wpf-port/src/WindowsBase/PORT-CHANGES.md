# WindowsBase → Linux 移植改动清单

> 由 `src/Linux/build/port-lib.py` 从上游自动生成，上游仓库零改动。

| 项 | 数值 |
|---|---|
| 上游源文件 | 313 |
| 解析成功 | 309 |
| 按 basename/大小写找回 | 0 |
| 仍缺失 | 3 |
| 剔除（src/Linux/build/excludes/WindowsBase.txt） | 1 |
| shim（src/Microsoft.DotNet.Wpf.Linux/src/shims/WindowsBase.shims.txt + 身份文件） | 4 |
| 丢弃 ProjectReference | 3（含 vcxproj 0） |
| 未解析的本地引用（需先构建对应工程） | 1 ['System.Windows.Primitives'] |
| 丢弃私有 WinForms 引用 | 2 |
| 丢弃代码生成 Target | 1 ['GenerateSources'] |
| 丢弃 Arcade/CodeGen Import | 3 |
| DefineConstants | `BASE_NATIVEMETHODS;WINDOWS_BASE` |

## 剔除的源文件（src/Linux/build/excludes/WindowsBase.txt）

```
  System/Windows/SplashScreen.cs
```

## 仍缺失的源文件（前 30）

| 原始 Include | 解析路径 |
|---|---|
| `System\Windows\Markup\SequencePartEditor.cs` | `/home/links-dev/netTest/GitProj/WPFOnLinux/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Markup/SequencePartEditor.cs` |
| `System\Windows\Markup\StreamPartReader.cs` | `/home/links-dev/netTest/GitProj/WPFOnLinux/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Markup/StreamPartReader.cs` |
| `System\Windows\Markup\XmlPartReader.cs` | `/home/links-dev/netTest/GitProj/WPFOnLinux/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Markup/XmlPartReader.cs` |

