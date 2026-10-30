# ReachFramework → Linux 移植改动清单

> 由 `src/Linux/build/port-lib.py` 从上游自动生成，上游仓库零改动。

| 项 | 数值 |
|---|---|
| 上游源文件 | 285 |
| 解析成功 | 285 |
| 按 basename/大小写找回 | 64 |
| 仍缺失 | 0 |
| 剔除（src/Linux/build/excludes/ReachFramework.txt） | 0 |
| shim（src/Microsoft.DotNet.Wpf.Linux/src/shims/ReachFramework.shims.txt + 身份文件） | 1 |
| 丢弃 ProjectReference | 7（含 vcxproj 1） |
| 未解析的本地引用（需先构建对应工程） | 1 ['PresentationFramework-ReachFramework-impl-cycle'] |
| 丢弃私有 WinForms 引用 | 0 |
| 丢弃代码生成 Target | 0 [] |
| 丢弃 Arcade/CodeGen Import | 0 |
| DefineConstants | `REACHFRAMEWORK;WINDOWS_BASE_OR_PC` |

