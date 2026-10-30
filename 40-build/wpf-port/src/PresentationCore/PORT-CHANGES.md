# PresentationCore → Linux 移植改动清单

> 由 `src/Linux/build/port-lib.py` 从上游自动生成，上游仓库零改动。

| 项 | 数值 |
|---|---|
| 上游源文件 | 1356 |
| 解析成功 | 1348 |
| 按 basename/大小写找回 | 214 |
| 仍缺失 | 0 |
| 剔除（src/Linux/build/excludes/PresentationCore.txt） | 8 |
| shim（src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.shims.txt + 身份文件） | 6 |
| 丢弃 ProjectReference | 8（含 vcxproj 1） |
| 未解析的本地引用（需先构建对应工程） | 1 ['System.Windows.Primitives'] |
| 丢弃私有 WinForms 引用 | 1 |
| 丢弃代码生成 Target | 0 [] |
| 丢弃 Arcade/CodeGen Import | 2 |
| DefineConstants | `CORE_NATIVEMETHODS;PRESENTATION_CORE;COMMONDPS;WINDOWS_BASE_OR_PC` |

## 剔除的源文件（src/Linux/build/excludes/PresentationCore.txt）

```
  ModuleInitializer.cs
  System/Windows/DataFormat.cs
  System/Windows/DataFormats.cs
  System/Windows/Nrbf/WpfNrbfSerializer.cs
  System/Windows/Ole/DataObjectAdapter.cs
  System/Windows/Ole/WpfOleServices.cs
  System/Windows/clipboard.cs
  System/Windows/dataobject.cs
```

