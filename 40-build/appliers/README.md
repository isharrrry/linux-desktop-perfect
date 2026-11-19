# 六件补丁/垫片的源码与重建口径（PM-0166，2026-11-19 系统与底层）

这些源码原先只在开发者工作区（Windows）与 /home/links-dev/pnsguard，未入库；本轮实测全部可重建，收进这里。

| 目录 | 产物 | 重建方式 | 与部署件对比 |
|---|---|---|---|
| pm0148 | GongSolutions.Wpf.DragDrop.dll | `pm0148-patcher <原件> <输出> GongSolutions.Wpf.DragDrop.DragDrop DoDragSourceMove insert` | 同尺寸 105,984，仅 PE TimeDateStamp 2 字节不同 |
| pm0138 | SourceTree.Ui.FileListContainer.Split.Wpf.dll | `pm0138-patcher <原件> <输出>` | 同尺寸 37,888，仅 PE TimeDateStamp 2 字节不同 |
| pm0138fix | Pm0138Fix.dll（运行时 Hook） | `dotnet build -c Release`（引用 pm0138-fx 的 WPF 三件） | **逐字节相同**（sha 一致） |
| gitselfheal | GitSelfHeal.dll | `dotnet build -c Release` | 同尺寸 7,680，差异 = PE 时间戳 + MVID |
| uiabridge | UiaBridge.dll | `dotnet build -c Release` | 同尺寸 9,728，差异 = PE 时间戳 + MVID |
| pnsguard | PnsGuard.dll（DOTNET_STARTUP_HOOKS） | `dotnet build -c Release` | 同尺寸 6,656，差异 = PE 时间戳 + MVID |

重建环境：Windows + .NET SDK 10.0.203 + `Mono.Cecil.dll`（0.11.6）+ 本地 `pm0138-fx` 下 WPF 三件副本。
托管件因编译期时间戳/MVID 不可能逐字节复现；判据是**内容等价**（同尺寸、差异仅在 PE 时间戳 136–139 与 MVID 16 字节）。
原件（Cecil 输入）：`deploy-backup/appdll-pm0148-20261119-140148/GongSolutions.Wpf.DragDrop.dll.orig`（`abb63fdf…`）与
`deploy-backup/appdll-pm0138-20261119-183010/…Split.Wpf.dll.orig`（`60a38998…`）。
