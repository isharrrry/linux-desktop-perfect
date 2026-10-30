# PresentationFramework.Classic → Linux 移植改动清单

> 由 `build/port-lib.py` 从上游自动生成，上游仓库零改动。

| 项 | 数值 |
|---|---|
| 上游源文件 | 8 |
| 解析成功 | 8 |
| 按 basename/大小写找回 | 0 |
| 仍缺失 | 0 |
| 剔除（build/excludes/PresentationFramework.Classic.txt） | 0 |
| shim（build/shims/PresentationFramework.Classic.shims.txt + 身份文件） | 1 |
| 丢弃 ProjectReference | 6（含 vcxproj 0） |
| 未解析的本地引用（需先构建对应工程） | 1 ['PresentationUI'] |
| 丢弃私有 WinForms 引用 | 0 |
| 丢弃代码生成 Target | 0 [] |
| 丢弃 Arcade/CodeGen Import | 0 |
| DefineConstants | `THEME_CLASSIC;WINDOWS_BASE_OR_PC` |

