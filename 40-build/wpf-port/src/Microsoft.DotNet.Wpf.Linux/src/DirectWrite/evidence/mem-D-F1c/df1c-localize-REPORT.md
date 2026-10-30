# `D-F1c` 有界复现 + 定位（T2 交 T1d 的输入，2026-09-15 11:27–11:31）

**装置**：`build/DirectWrite.Linux/FallbackCriteria/bin/Debug/FallbackCriteria.dll`（我的 `D-F1` runner，sha16 `c3e5d553c84666c5`；
被测 shim 源 `build/shims/PresentationCore.HbTextLine.cs` sha16 `17b2cdfe08f13280` = `#14` 冻结件；
PC = `build/PresentationCore.Linux/bin/Debug/PresentationCore.dll` sha16 `9adac6b8d8e285c3`）。
**跑法**：`bash $HOME/wfp-runs/bounded-run.sh <秒上限> <输出目录> <命令…>` —— 看门狗**只按 PID** `kill -TERM`；
每 0.2 s 采 `/proc/<pid>/status`（RSS/HWM）、`/proc/<pid>/stat`（CPU ticks）、`maps`+`fd`（打开过的字体字面）。
**参数**：与 `#14` harness 的 b34 语料逐字同源（`F_nbsp_zwsp_w40` 整段 32 码元 / em16 / 段宽 40 / `alwaysCollapsible:true` / 取 `与 ` 那一行 `[14,2)`）。

## 一、四趟对照（全部实跑，原始日志在各目录）

| 趟 | 命令要点 | 字体目录 | 内存上限 | RC | **峰值 RSS** | CPU | 墙钟 | 打开面数 | `null/b34` 行宽 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A1 | `--mode=all --strict-probe` | 系统（未设） | `ulimit -v 2000000` | **134** | 51,864 KB | 0.16 s | 0 s | 0 | —（**CLR 没起来**） |
| A2 | 同上 | 系统（未设） | 堆硬上限 2 GiB | 0 | **3,476,872 KB ≈ 3.48 GB** | 11.78 s | 12 s | 300 | 16.0000 |
| B2 | `--mode=null --para=b34`（**单次读数**） | 系统（未设） | 堆硬上限 1.5 GiB | 0 | **3,463,368 KB ≈ 3.46 GB** | 4.27 s | 4 s | 300 | 16.0000 |
| B1 | `--probe-only`（只 `hb_shape` 4 次） | 系统（未设） | 堆硬上限 1.5 GiB | 0 | **8,852 KB ≈ 8.6 MB** | 0.1 s | 0 s | 2 | — |
| E2 | 同 A2 | `WPF_LINUX_FONT_DIR=build/fonts`（4 面） | 堆硬上限 2 GiB | 0 | **63,192 KB ≈ 63 MB** | 0.20 s | 0 s | 4 | **9.6000** |

**A1 的 stderr 原文**（说明这一趟**不是**"负载撞顶"，而是 `ulimit -v` 与 CLR 虚拟地址预留不兼容）：
```
Fatal error. .NET runtime failed to initialize.
```
⇒ 后续三趟改用 **托管堆硬上限**（`DOTNET_GCHeapHardLimit`）表达"有界"，并保留 60 s 看门狗。

## 二、从对照得出的四条（均为实测）

1. **不是 `hb_shape`**：B1（纯整形探针，含 CJK `Regular.ttc#0` 与 `Bold.ttc#0`）= **8.6 MB**。
2. **不是"读数次数累积"**：B2（**一次**读数）= **3.46 GB**，A2（九次 + 探针）= **3.48 GB** ⇒ 峰值由**一次**扫描/探测决定。
3. **随系统字体集爆炸**：系统目录 **3.46 GB / 300 面 / `candidates=371`** ↔ 限制到 4 面 **63 MB / `candidates=4`** ⇒ **55×**。
4. **粗算每单位代价**（两个口径都给，谁更接近请 T1d 在 shim 侧确认，我不下结论）：
   `3.46 GB ÷ coverageProbe=60 ≈ 58 MB/次探测`；`3.46 GB ÷ 300 打开面 ≈ 11.5 MB/面`。

## 三、触发区间的源码锚点（`build/shims/PresentationCore.HbTextLine.cs`）

* `:886 ResolveDirs()` —— 读的环境变量是 **`WPF_LINUX_FONT_DIR`（单数）**；未设 ⇒ `/usr/share/fonts` 等系统目录
  （仓库里**只有** `build/MilBridge/run.sh:99/:105` 设过它，且是给 **CompositeFontProbe** 的，**不是** `tline`）
* `:912 EnsureScan()` —— 递归枚举所有 `*.ttf/*.otf/*.ttc`（本机 ⇒ 371 个候选，扫描 1 次、进程内缓存）
* `:964 TryFindCovering()` —— 逐面覆盖探测（本趟 `coverageProbe=60 coverageCacheHit=12`）

`FALLBACK_DIAG` 同趟原文（B2 与 A2 **逐字相同**）：
```
multifont=plan=1 runs=1 runGt1=0 cpUncovered=1 coverageProbe=60 coverageCacheHit=12 fallbackApplied=1
fallbackFailed=0 fallbackUnrenderable=0 fromRunFaces=0 fromSystemScan=1 segments=3 chunkedLines=1
faceResolve=0/0(缓存命中0) candidates=371(扫描1次)
```
E2（限制字体目录）原文：
```
coverageProbe=36 coverageCacheHit=13 fallbackApplied=0 fallbackFailed=1 fromSystemScan=0 candidates=4(扫描1次)
```

## 四、为什么 harness 那趟表现为"自旋 + 无界"（数字支持的解释，不是我推断的因果）

本机：总内存 **7.9 GB**、已用 **4.1 GB**、可用 **≈3.4 GB**、**swap 已用 801 MB**（`free -m` 实测）。
⇒ **单次读数就要 3.46 GB** ⇒ 必然进 swap ⇒ "101% CPU / 零输出 / RSS 缓慢爬（1.9→3.1 GB in 5 s）"与**交换抖动**的形态一致
（我这趟 12 s 跑完、它跑到 12 min，差别在内存压力与用例数，**不是两条不同的代码路径**）。
⇒ **建议**：不要在 harness 上做二分（614 例 × 每个进程 3.4 GB）；用本装置（B2 = 4 s 一次读数；E2 = 0 s 对照）二分快得多。

## 五、给 T1d 的两条附加事实（避免误判方向）

1. **Bold 不是特殊触发点**：A2 一趟里被打开的 `NotoSansCJK-*.ttc` 有 **7 个权重全开**
   （`Black/Bold/DemiLight/Light/Medium/Regular/Thin`）⇒ T3 看到的 `NotoSansCJK-Bold.ttc` 是**扫描/探测走遍每个文件的每个面**的**症状**，不是触发条件。
2. **`WPF_LINUX_FONT_DIR` 会改读数**：E2 里 `null/b34` = `9.6000`（无回退面 ⇒ `.notdef`），A2/B2 里 = `16.0000`
   ⇒ `D-F1` 的任何读数**必须连"字体环境变量设没设"一起记**（我 §31 的四元组漏了这一项，本轮补记）。

## 六、原始日志路径（`$HOME`，`/tmp` 会被清）

* `$HOME/wfp-runs/df1c-sysfont-A1/`（`meta.txt` = RC/峰值/CPU/墙钟/打开面清单；`stderr.txt` = CLR 起不来的原文）
* `$HOME/wfp-runs/df1c-sysfont-A2/`、`$HOME/wfp-runs/df1c-localize-B2/`、`$HOME/wfp-runs/df1c-localize-B1/`、`$HOME/wfp-runs/df1c-fontdir-E2/`
* 每目录内：`meta.txt`（含采样收尾）、`sample.txt`（0.2 s 采样全量）、`stdout.txt`/`stderr.txt`、`maps-fonts.txt.raw`、`fd-fonts.txt.raw`
* 采样增长片段（A2）：`1.4 MB → 124 MB(0.2 s) → 504 MB(0.5 s) → … → 3.10 GB(10.9 s) → 3.43 GB(11.8 s)`

---

# 追加（2026-09-15 11:33–11:36）：峰值跟着"面数"涨 —— 斜率、最小复现件、`MULTIFONT` 反证、口径字段、假 0 纠正

## 七、"面数"扫描（同一命令 `--mode=null --para=b34`，只换 `WPF_LINUX_FONT_DIR`）

| 字体目录 | `FALLBACK_DIAG` 的 `candidates` | 峰值 RSS | CPU | 墙钟 | `null/b34` 读数 |
| --- | --- | --- | --- | --- | --- |
| （未设 ⇒ 系统目录） | **371**（扫描 1 次） | **3,463,368 KB ≈ 3.30 GB** | 4.27 s | 4 s | `LINE_W=16.0000 GID=9498` |
| 7 个 CJK ttc（`$HOME/wfp-runs/fontdir-7`） | **45** | **1,688,248 KB ≈ 1.61 GB** | 2.49 s | 3 s | `LINE_W=16.0000 GID=9498` |
| 1 个 CJK ttc（`$HOME/wfp-runs/fontdir-1` = `NotoSansCJK-Regular.ttc`） | **10** | **531,736 KB ≈ 519 MB** | 0.94 s | 2 s | `LINE_W=16.0000 GID=9498` |
| `build/fonts`（4 个拉丁 ttf） | **4** | **63,192 KB ≈ 63 MB** | 0.20 s | 0 s | `LINE_W=9.6000`（无回退面 ⇒ `.notdef`） |

1. **`candidates` 数的是"面（face）"不是"文件"**：`NotoSansCJK-Regular.ttc` **一个文件** ⇒ `candidates=10`（该 ttc 10 个面）
   ⇒ 系统目录的 `371` 是**面数**；峰值随它单调上升 ⇒ **放大单位 = 面**。
2. **最小复现件已备好**（阳性/阴性对照都只要 2–5 秒、峰值 ≤ 1.7 GB）：
   ```bash
   export PATH="$HOME/.dotnet:$PATH"
   DLL=<repo>/build/DirectWrite.Linux/FallbackCriteria/bin/Debug/FallbackCriteria.dll
   D=$HOME/wfp-runs/fontdir-1   # 1 个 CJK ttc ⇒ 10 面 ⇒ 519 MB（**回退仍生效**：16.0000 / gid 9498）
   # D=$HOME/wfp-runs/fontdir-7 # 7 个 CJK ttc ⇒ 45 面 ⇒ 1.61 GB
   WPF_LINUX_FONT_DIR=$D bash $HOME/wfp-runs/bounded-run.sh 60 $HOME/wfp-runs/try \
        bash -c "export DOTNET_GCHeapHardLimit=0x60000000; exec dotnet $DLL --mode=null --para=b34"
   ```
   跑完看 `$HOME/wfp-runs/try/meta.txt` 的 `PEAK_HWM_KB`；`sample.txt` 是 0.2 s 采样全量。
   ⇒ **修法的正极性判据可直接用 `fontdir-1`**（应保持 `16.0000`/`gid≠0` 且峰值回到与 4 面同量级），不必跑系统目录那趟 3.3 GB。

## 八、`WPF_LINUX_MULTIFONT=0` **不能**关掉这条路（实测 + 源码，不是推断）

同一趟加 `WPF_LINUX_MULTIFONT=0` + 系统目录：峰值 **3,470,808 KB ≈ 3.31 GB**、`FALLBACK_DIAG` **逐字相同**（`candidates=371(扫描1次) coverageProbe=60`）、读数相同。
源码依据：该开关只出现在 `:3940`（另一入口）与 `:4002`（打印），而 `plan == null` 的内部建计划段**不看它**：
```
3502:  var infos = new List<HbRunFaceInfo>(1) { new HbRunFaceInfo { …, Face = new HbFaceRef(fontPath, faceIdx), … } };
3508:  plan = HbFontPlanner.Build(text, infos, true, (f, cp2, w2, wd2, s2) => true);
3511:  var fcs = new List<GlyphTypeface>();      // ← 逐段构造 GlyphTypeface
```
⇒ `D-F1` 之后**任何** `FormatParagraph(plan: null)` 的调用（含 harness 的 `file` 字体用例）都走这条路 ⇒ **与 `zh/ja` 用例无关**。
⇒ 候选放大点（按可疑度）：`HbFontPlanner.Build → TryFindCovering` 的逐面探测（`:964`）、`:3511` 的逐段 `GlyphTypeface`、PC 侧**同一单数变量的第二个读者** `build/shims/PresentationCore.Factory.Linux.cs:384 ResolveSystemFontDirectories()`。

## 九、口径硬要求（主控 ③）已落地：读数行自带字体环境

```
# ENV WPF_LINUX_FONT_DIR=<值 或 <未设>>（未设 ⇒ 回退扫描走 /usr/share/fonts 等**系统目录**（371 面 ⇒ 峰值 GB 级）｜**已设** ⇒ 回退扫描被限制到这些目录）
      WPF_LINUX_MULTIFONT=<值 或 <未设>>（=0 对 `plan==null` 路径**无效**：shim `:3486-3508` 不看这个开关）
MODE=… PARA=… RESULT=OK … FONTDIR_ENV=<值 或 -> MULTIFONT_ENV=<值 或 ->
```
实测样例（`fontdir-1`）：`FONTDIR_ENV=/home/links-dev/wfp-runs/fontdir-1`、`MULTIFONT_ENV=-`、峰值 516,432 KB、`LINE_W=16.0000 GID=9498`。
仪器 sha 因此变更：`FallbackCriteria.dll` `c3e5d553c84666c5` ⇒ **`69c2dab1c04ca5ca`**（`REPORT.md` §31 那份产物属**旧**仪器；`D-F1c` 修好后我用新仪器重取，届时 §31 读数会带这一列）。
判据侧同步：`[tag] 行宽 …` 行尾现在带 `｜**字体环境口径**：WPF_LINUX_FONT_DIR=… WPF_LINUX_MULTIFONT=…`；`run-df1-criteria.sh` 的四元组也印这两个变量。

## 十、我上一轮"复数 0 命中"是**假 0**（接受主控 ① 的纠正，并写明 grep 口径）

**正对照（全仓两种拼写）**：`grep -rnI --exclude-dir={upstream,.artifacts,.artifacts-rb,obj,bin} -e WPF_LINUX_FONTS_DIR -e WPF_LINUX_FONT_DIR .`
⇒ 复数**确实**被读：`build/PresentationCore.Linux/FontCacheUtil.Linux.cs:334`（**FontCache 的平台目录**；注释 `:12/:331`；补丁源 `src/WpfGfx.Linux.Native/tools/patch-presentationcore-fontcache.py:99`）。
**假 0 的口径（必须写明）**：上一轮我只 grep 了 **4 个路径** —— `build/shims/PresentationCore.HbTextLine.cs`、`build/MilBridge/run.sh`、
`build/MilBridge/tests/HbTextLineParity/Program.cs`、`build/DirectWrite.Linux/FallbackCriteria/*.cs*`；**既没给正对照、也没声明覆盖范围**。
**实质结论仍成立**：回退扫描只读**单数**（`HbTextLine.cs:889`；另有 PC 侧同一单数的第二读者 `Factory.Linux.cs:384`；UI 字体链 `MilPresentation.cs:342`）。
**派生事实（请 owner 确认，不含推断）**：`ContractProbe/Program.cs:33` 与 `TextLineProto/Program.cs:34` 设的是**复数**（ContractProbe 的注释写明意图是"PC 的字体缓存（补丁 I）"⇒ 用法**正确**）；若某处**以为**设复数能限制"回退扫描的字体集合"，该意图**未生效**（扫描只看单数）——`TextLineProto` 无注释，**请其 owner 确认口径**。
