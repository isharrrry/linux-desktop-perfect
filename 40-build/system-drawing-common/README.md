# `System.Drawing.Common` 的 Linux 派生构建

本目录是 [`40-build/upstream/system-drawing-common/`](../upstream/system-drawing-common/) 的**派生树**：
上游（corefx `release/3.1`）的源码保持原样，**所有修改都在这里**（符合 AGENTS.md 硬约束 6/7）。

## 为什么需要它

`.NET 6+` 的 `System.Drawing.Common` 在**托管层**就拒绝非 Windows
（`PInvokeGdiPlus..cctor` 里 `SetDllImportResolver` 的回调抛 `PlatformNotSupportedException`），
**从外部绕不过去**：抢在它之前装 resolver 会让它的 cctor 抛 `InvalidOperationException` 而炸。
详见 [`70-operate/已知坑清单.md`](../../70-operate/已知坑清单.md) 的 K-018 / K-019 / K-020。

所以只能**自己编一个同名同强名称的 `System.Drawing.Common.dll`**。

## 关键结论（踩过的坑）

| 项 | 值 |
|---|---|
| **程序集自己的公钥令牌** | **`cc7b13ffcd2ddd51`**（不是 `b03f5f7f11d50a3a` —— 那是它**引用别人**时出现的）|
| 强名称做法 | 从原版 dll 的 `AssemblyDef` 提取 **160 字节公钥** → `PublicSign=true` |
| corefx 源码约定 | **`X.cs` = Windows/共享，`X.Unix.cs` = Unix**；两者都在时必须排除 `X.cs` |
| 同一类型多文件 | corefx 的 `Configurations.props` 按目标框架**三选一**（如 `InvalidPrinterException` 有 3 份）|
| **MSBuild 通配符** | `Include` 上的 `Exclude` 与 `Remove` 里的 `**` **在本仓实测都不可靠** ⇒ **改为显式列出 221 个文件** |
| `SR` 类 | corefx 由构建系统生成，源码里没有 ⇒ 本仓用 [`tools/gen-sr.py`](tools/gen-sr.py) 从 `Strings.resx` 生成 |
| 依赖注入点 | `src/misc/ExternDll.Unix.cs` 里 `Gdiplus = "gdiplus"`（**无后缀**）⇒ 由本仓 `win32_gdiplus.c` 的 `Gdip*` 导出提供 |

## 编译

```bash
python3 tools/gen-sr.py          # 生成 SR（改过 resx 才需要）
dotnet build SystemDrawingCommon.Linux.csproj -c Release
```

产物：`bin/Release/System.Drawing.Common.dll`

## 本目录相对上游的改动

1. 新增 `SystemDrawingCommon.Linux.csproj`（**显式 221 个源文件**，不用通配符）；
2. 新增 `src/CommonInterop/`：`Interop.IStream.cs`（从 corefx `src/Common` 拷入）、`Interop.HRESULT.cs`、
   `Interop.STATSTG.cs`、`Interop.STATFLAG.cs`、`Interop.STGTY.cs`、`Interop.STGM.cs`，
   以及几个**桩**：`UITypeEditor`/`FontConverter`/`IconConverter`/`ImageConverter`/`Consts`/
   `Microsoft.Win32.UserPreferenceChangedEventArgs`；
3. `src/System/Drawing/ImageAnimator.Unix.cs`：`class` → `partial class`（`ImageInfo.cs` 里也有一个 partial）；
4. `src/System/Drawing/Printing/PageSettings.Unix.cs`：加 `partial`、去掉重复的 `[Serializable]`。

## 状态

**编译未完成**：截至最后一次尝试仍有 **10 个错误**（`GetCachedSwitchValue` 缺、`AdjustableArrowCap` 构造函数、
`PrinterSettings.GetHdevmodeInternal`/`CreateDeviceContext` 只在 `.Windows.cs` 里有）。
**没有产出 dll，因此 WinForms 仍未打通**。
