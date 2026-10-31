# `System.Drawing.Common` 的 Unix 后端源码（取自 corefx release/3.1）

## 为什么需要它
`System.Drawing.Common`（.NET 6+）在**托管层**就拒绝非 Windows：
`PInvokeGdiPlus..cctor` 里 `SetDllImportResolver` 的回调直接抛 `PlatformNotSupportedException`，
**从外部绕不过去**（抢装 resolver 会撞 `InvalidOperationException`，见 `70-operate/已知坑清单.md` K-018）。
唯一出路是**自己编一个同名同强名称的 `System.Drawing.Common.dll`**（`PublicKeyToken=b03f5f7f11d50a3a`）。

## 源码来源（可复现）
```bash
git clone --filter=blob:none --no-checkout --depth 1 -b release/3.1 --single-branch \
    https://gitee.com/mirrors_dotnet/corefx.git
cd corefx && git sparse-checkout init --cone && git sparse-checkout set src/System.Drawing.Common
git checkout
```
- 镜像：`gitee.com/mirrors_dotnet/corefx`（**国内源可用**；`github.com/dotnet/corefx` 在本机不可达）
- 分支：`release/3.1`
- 这里保存的是 `src/System.Drawing.Common/` 下的 **`src/` 与 `System.Drawing.Common.csproj`**（320 个 `.cs`）

## 为什么是 3.1 而不是更新版本
`.NET 6` 起 `dotnet/runtime` **删掉了** `System.Drawing.Common` 的 Unix 分支
（只保留 Windows 实现 + 平台检查）。`corefx release/3.1` 是**最后一个带完整 Unix 后端**的版本。

## 它的 Unix 后端长什么样
- 条件：`<Configurations>` 里有 `netcoreapp3.0-Unix-Release`；
  `<ItemGroup Condition="... AND '$(TargetsUnix)' == 'true'">` 里编
  `Graphics.Unix.cs` / `Icon.Unix.cs` / `SystemFonts.Unix.cs` / `Region.Unix.cs` / `GdiplusNative.Unix.cs` 等
- **native 依赖**：`src/misc/ExternDll.cs` → `public const string Gdiplus = "gdiplus.dll"`
  ⇒ Unix 上就是 **`libgdiplus`**（Mono 的 C 实现）

## 与我们的衔接点（重要）
本仓 `40-build/native-shim/src/win32_gdiplus.c` 已经导出 **29 个 `Gdip*`**
（`GdipCreateBitmapFromFile` / `GdipGetImageWidth` / `GdipBitmapLockBits` / `GdipDisposeImage` …），
所以这份源码编译出来**不需要再装 `libgdiplus`** —— `gdiplus.dll` 这个名字由我们的 shim 提供。
（缺口：隔壁 `WPFOnLinux` 实现了 44 个 `Gdip*`，我们 29 个，需要补齐到同一水位。）

## 待办（下一轮）
1. 给它写一个能在 .NET 10 上编译的 csproj（原 csproj 是 corefx 的旧构建系统，不能直接用）；
2. 用 `src/Linux/build/keys/WcpPublicKey.snk` 签名，保证 `AssemblyName=System.Drawing.Common` 且
   `PublicKeyToken=b03f5f7f11d50a3a`；
3. 编译 → 替换运行时的原版 → 跑 `WfApp`（Windows 编译的 WinForms 程序）验证。
