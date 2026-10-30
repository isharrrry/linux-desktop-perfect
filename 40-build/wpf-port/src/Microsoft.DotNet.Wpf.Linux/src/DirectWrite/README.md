# build/DirectWrite.Linux —— T2：把 DirectWriteForwarder 的 PNSE 面接到 Skia/FreeType

> 一句话：**DirectWriteForwarder 的托管面（上游 C++/CLI）在 Linux 上原本有 62 处
> `PlatformNotSupportedException` 占位，现在其中 47 处（A/B/C 档）由本目录的 provider
> 提供真实现，并已接线进 `build/DirectWriteForwarder.Linux/`。**
> 剩余 15 处是 shaping / COM 回调 / 子集化（D 档，见 `REPORT.md` §5）。
>
> **先读哪个**：
> | 想知道 | 读 |
> |---|---|
> | 结论、实测数字、降级清单、诚实说明 | `REPORT.md` |
> | 62 处 PNSE 各自属于哪一档、PresentationCore 到底调不调它 | `PNSE-INVENTORY.md` |
> | 接线改了哪些行、还差哪两件事 | `WIRING.md` |

---

## 目录

| 路径 | 是什么 | 谁用 |
|---|---|---|
| `Provider/` | **字体提供者**（~3700 行，含 Phase 3 的布局特性读取/覆盖判定/掩码复算/表剥离）：Skia/FreeType 承载的真实现。度量/字形索引/字形度量/advance/OpenType 表访问/文件名/句柄表 | `DirectWriteForwarder.Linux` 引用它（接线后） |
| `Probe/` | **取证**：把 provider 的全部可观察输出压成一份确定性摘要 + SHA-256，供**跨进程**比对 | `Tests/` 当子进程拉起 |
| `Tests/` | **84 条断言**：确定性（含跨进程）/ 与 Skia 三方一致 / 与 M1 `Text/` 一致 / 边界 / 接线冒烟 | 人、CI |
| `WiringSmoke/` | **端到端**：反射访问**接线后的骨架 + PC 的 Linux Factory**，打印 45 项 `KEY=VALUE` 事实 | `Tests/WiringTests.cs` 当子进程拉起 |
| `SystemFontsProbe/` | **端到端**：`SystemFonts.*` + 直探 `libwpfwin32.so` 的 SPI + 装配身份诊断 | 人（结论见 REPORT.md §4.0.4） |
| `artifacts/` | 摘要与哈希留证（`probe-digest.txt` = 三次运行一致的那份） | 人 |

三工程共用 `Directory.Build.props`（net10.0 + **SkiaSharp 2.88.9 锁定**，版本由 T0 裁决；
4.x 有破坏性 API 变更）。本目录**不引用** `WpfGfx.Linux` / `PresentationCore` / 骨架，
方向永远是"骨架 → provider"（单向，不会成环）。

---

## 一条链复现全部验证

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/links-dev/netTest/wpf-linux-20260906/wpf-linux

# provider + 取证 + 端到端（-m:1：本机核少，避免并行 MSBuild 抢内存）
dotnet build build/DirectWrite.Linux/Provider/DirectWrite.Linux.Provider.csproj -m:1
dotnet build build/DirectWrite.Linux/Probe/DirectWrite.Linux.Probe.csproj -m:1
dotnet build build/DirectWrite.Linux/WiringSmoke/DirectWrite.Linux.WiringSmoke.csproj -m:1
dotnet test  build/DirectWrite.Linux/Tests/DirectWrite.Linux.Tests.csproj -m:1
```

预期：`已通过! - 失败: 0，通过: 94`；跨进程摘要三次相同
（`d8c7def143e83602355b50945561e93941f9ae99ca95c2850b9f2e2780330b1e`）。

## 看摘要（人读版）

```bash
# 详细事实（度量/来源/每张表哈希/语料映射/定位）
dotnet build/DirectWrite.Linux/Probe/bin/Debug/DirectWrite.Linux.Probe.dll --json

# 完整摘要落盘（出问题时逐行 diff）
dotnet build/DirectWrite.Linux/Probe/bin/Debug/DirectWrite.Linux.Probe.dll \
       --out build/DirectWrite.Linux/artifacts/probe-summary.txt

# 接线后的端到端事实
dotnet build/DirectWrite.Linux/WiringSmoke/bin/Debug/DirectWrite.Linux.WiringSmoke.dll
```

## 门禁（接线后的两个工程）

```bash
dotnet build build/DirectWriteForwarder.Linux/DirectWriteForwarder.Linux.csproj -m:1   # 0 错 0 警
dotnet build build/PresentationCore.Linux/PresentationCore.Linux.csproj -m:1           # 0 错 0 警
dotnet build build/PresentationFramework.Linux/PresentationFramework.Linux.csproj -m:1 # 0 错 0 警
```

> ⚠️ `build/PresentationCore.Linux/*.csproj` 是 `port-lib.py` 的**生成物**：
> 生成之外必需的改动写在 `build/PresentationCore.Linux/reapply-patches.py`（补丁 B/D/**F**），
> 标准链是 `port-lib.py` → `reapply-patches.py` → `dotnet build`。

---

## 派生 UI 字体（Phase 3）

`build/fonts-ui/UI-NoLayout.ttf` = `NotoSans-Regular` 剥离 **GSUB/GPOS** 的派生件，
由 `build/gen-ui-font.py` 生成。用途：让 **WPF 默认 UI 字体**不再触发
`TypographyAvailabilities` 的 `FastTextTypographyAvailable`（闸门 2），
从而走名义字形快路径而不是未实现的 LineServices。

* 实测：PC 侧掩码 **21 → 0**，快路径 **拒 → 放行**（REPORT.md §11.2）
* 等价性：字形 id/步进/BMP cmap/度量**逐项不变**（`TypographyGateTests` 有断言）
* **不要**把它放进 `build/fonts/`（会同族同字重遮蔽基准件），也**不要**改名成 `NotoSans-*.ttf`

## 字体资产的确定性前提

全部断言只用 `build/fonts/` 下 `SHA256SUMS` 锁定的 4 份 Noto Sans：
**绝不回落系统字体**（`/usr/share/fonts` 的有无、版本、hinting 都会改变度量与字形 id）。
`MetricsTests.PackagedFonts_MatchChecksums` 在每次跑测试时先校验哈希，
`FontSet` 与 `LinuxFontCollection` 两侧都断言"查不到就是查不到"。

## 边界（T2 的写入范围）

新增内容全部在本目录；接线阶段（主控授权）另改了
`build/DirectWriteForwarder.Linux/{ManagedSurface.cs, ProviderAdapters.cs, *.csproj}`、
`build/PresentationCore.Linux/reapply-patches.py`（补丁 F/G）与
`build/shims/PresentationCore.{Factory.Linux,FontBridge}.cs`（新增）。
完整清单与"未触碰"清单见 `REPORT.md` §7。
