# PresentationFramework → Linux 移植改动清单

> 由 `src/Linux/build/port-lib.py` 从上游自动生成，上游仓库零改动。

| 项 | 数值 |
|---|---|
| 上游源文件 | 1329 |
| 解析成功 | 1350 |
| 按 basename/大小写找回 | 67 |
| 仍缺失 | 0 |
| 剔除（src/Linux/build/excludes/PresentationFramework.txt） | 0 |
| shim（src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationFramework.shims.txt + 身份文件） | 3 |
| 丢弃 ProjectReference | 10（含 vcxproj 1） |
| 未解析的本地引用（需先构建对应工程） | 1 ['PresentationUI-PresentationFramework-impl-cycle'] |
| 丢弃私有 WinForms 引用 | 2 |
| 丢弃代码生成 Target | 0 [] |
| 丢弃 Arcade/CodeGen Import | 2 |
| DefineConstants | `FRAMEWORK_NATIVEMETHODS;COMMONDPS;PRESENTATIONFRAMEWORK_ONLY;PRESENTATIONFRAMEWORK;RIBBON_IN_FRAMEWORK;WINDOWS_BASE_OR_PC` |

