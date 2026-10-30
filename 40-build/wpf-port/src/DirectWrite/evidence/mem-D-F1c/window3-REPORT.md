# 窗口 3 读数（T2）：**本次 shim = `92fc7605480fb289`**（mtime 12:14:31）

**件**：shim `92fc7605480fb289`｜PC `9adac6b8d8e285c3`｜备份自证：`~/t1d-backups/20260915-1214-shim-1ebea99c-preW3.cs` 我重算 sha16 = `1ebea99cf011a3b7` ✓
**仪器（运行那一刻）**：`Program.cs` `fc4b8aa32ddf7599`（+ 本轮新增 `FACECACHE_DIAG`）、后修宿主 DLL `da1b242efa0232d3`、判据 `fc808896f23390f4`（口径 `D-F1 判据 v2`；本轮**换了一支牙**，见 §4）
**修前对照宿主**：`$HOME/wfp-runs/df1c-prefix-host2/FallbackCriteria.dll` `25eebec6872fcf64`（**同一份当前 `Program.cs`** + 修前 shim `17b2cdfe`）⇒ 下面的 before/after 是同一把尺子
**环境**：全程 `loadavg` 0.71–1.25、`free` avail ~3.0–3.2 GB；**串行**；T3 长趟未启

---

## 1. 任务 1 内存正极性（`--mode=null --para=b34`、**不设** `WPF_LINUX_FONT_DIR` ⇒ 系统 371 面）

| 量 | 修前 `17b2cdfe` | **修后 `92fc7605`** | 期望 | 判定 |
| --- | --- | --- | --- | --- |
| **权威峰值 RSS**（`/usr/bin/time -v`） | 3,459,876 KB ≈ **3.30 GB** | **2,119,824 KB ≈ 2.02 GB** | 几十 MB 量级（硬线 300 MB / 1 GB） | **未达成**（降 39%，仍差 7×） |
| rc / 墙钟 | 0 / 4 s | **0 / 3 s** | 完成 | ✓ |
| **`FACECACHE_DIAG`** | `faceLoads=372 faceLoadFailures=0 evictions=0 coverClears=0` | **`faceLoads=393 faceLoadFailures=0 evictions=0 coverClears=0`** | `FaceLoads` ≈1–3 | **未达成**（不降反升 372→393） |
| `candidates` | `371(扫描1次)` | `371(扫描1次)` | 不变 | ✓ |
| `Scans` | 1 | 1 | 1 | ✓ |
| `coverageProbe` | 60 | **53** | 已声明会降 | ✓（**仪器口径位移**：60→53） |
| `coverageCacheHit` | 12 | **12** | 不变 | ✓ |
| `Evictions` | 0 | **0** | 记实际值 | 实际 0（371 < `MaxFaces=512` ⇒ 永不淘汰） |
| 读数 | — | `LINE_W=16.0000 WITW=20.1600 CR_W=3.3440 GID=9498 ADV=16.0000 FACE_URI=…NotoSansCJK-Regular.ttc` | — | ✓ |

**给 T1d 的下一条线索（有数字支撑，非推断）**：`faceLoads ≈ candidates`（393 vs 371）+ `evictions=0` + `coverClears=0`
⇒ **物化发生在"扫描/挑面"这条路上，而不是被削减的 `CoversFace` 探测上**（后者确实降了：60→53）。
而 `HbFaceCache.Release(...)`（`:658-659` 注释原文："**D-F1c/B1：把某个 (path,face) 立刻从缓存里释放（销毁句柄）——扫描期用，避免 O(系统面数) 驻留**"）
⇒ 建议核对：**扫描/挑面路径是否真的调了 `Release`**（`coverClears=0` 说明**一次都没清**）。

## 2. 任务 2 `D-F1b` 的 C1 三腿 ⇒ **达成 ✓（两条路都绿）**

```
[null/b34] FACE_URI=file:///usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc  GID=9498  GLYPH_COUNT=65535  GID_LT_COUNT=true
[fb/b34]   FACE_URI=file:///usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc  GID=9498  GLYPH_COUNT=65535  GID_LT_COUNT=true
[null/b34] C1=PASS（①gid<GlyphCount(公开API)=绿；①gid<numGlyphs(独立/字体文件)=绿；②gid≠0⇒报出面须覆盖CP(独立cmap)=绿；③AdvanceWidths[gid]可查=绿；③×em==观测advance=绿）
[null/b34]   C1②b（.ttc 面号形态，**信息腿**，不进 C1 判定）：NOINFO（**face 0 形态不带 `#`** —— 主控口径；本机取不到 `#n` ⇒ 本腿不进 C1 判定）
[fb/b34]   C1=PASS（…）
```
* `ADV_DIP=16.0000` 且 **`ADV_FROM_TYPEFACE=16.0000`**（`AdvanceWidths[9498] × FontRenderingEmSize`，独立复算 hmtx 亦 16.0000）✓
* `FACE_URI` = **计划的那个面**（`…NotoSansCJK-Regular.ttc`，**face 0 不带 `#`**，与你的口径逐字一致）✓
* `GID_LT_COUNT=true`（9498 < 65535）✓；覆盖腿只用"覆盖 vs 不覆盖"（独立 `advance_from_font.py` 读 cmap：覆盖 ✓）✓
* `#n` 形态：本机取不到 ⇒ 该腿 `NOINFO`（已按你的口径实现并单列，**不进 C1 判定**）

## 3. 任务 3 计数读法 ⇒ 已直接读（无需改动 shim）

我 runner 新增一行（与 `FALLBACK_DIAG` 并列）：
```
MODE=… FACECACHE_DIAG=faceLoads=… faceLoadFailures=… evictions=… coverClears=…
```
`HbFaceCache` 的四个字段确实在反射形态下可读 ✓（T1d 读码结论成立）；`HbTextFallback.SummaryFragment()` 仍在 `#if TEXTLINE_SHIM_DIRECT` 内 ⇒ 我 runner 会**自报取到了谁**：
`SCAFFOLD_DIAG=[取到的是 HbFallbackDiag.SummaryFragment]…`。

## 4. 任务 4 两条硬判据

**(a) 面选择普查 26/26 逐格相同 ✅（按"面路径#面号"令牌比对）**
* 本次 `RC=0`、峰值 **2,123,836 KB ≈ 2.03 GB**、墙钟 8 s；修前 `RC=0`、3,492,848 KB、33 s
* 键集合相同（26 个 `(font,CP)` 格）；**令牌序列逐格相同** ⇒ **没有任何一格换面**
* ⚠️ **原始字符串确实不同 —— 但两处差异都在预期内，不是换面**：① `FACE_URI` 由**段落字体**变成**真面**（= `D-F1b` 修复本身）；② 段标签多了 `slot=0`（`Describe()` 渲染变化）
* 抽样（逐字）：`# CENSUS font=NotoSans-Bold CP=U+4E0E LINE_W=16.0000 GID=9498 ADV=16.0000 FACE_URI=file:///usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc SEL_PLAN=segments=2 [0,1) /usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc#0 (按码点回退) slot=0 run=0 1:[1,2) …/NotoSans-Bold.ttf#0 slot=0 run=0`

**(b) `D-F1` 正极性无回退 ✅**：`LINE_W=16.0000`、`GID=9498`、`CR_W=3.3440`、`C2=PASS`、`C3=PASS`（差 0.0000 / 0.0007 / 0.0007）、
`[fix] MODE=null == fb ⇒ 修法已生效`；**并且本趟判据首次全绿**：
```
CRITERIA=PASS（fail=0 noinfo=0；被判对象=null/b34；…）   RC=0（墙钟 5 s，峰值 2,109,516 KB ≈ 2.01 GB）
```

**⚠️ 本轮我改了一支牙的口径（仪器变更，必须记账）**：旧 `TOOTH-C1-NEG`＝"**今天这版必须判红**"是**针对 `D-F1b` 缺陷形态**的断言，缺陷一修好它**必然变假**（实测 `92fc7605` 上它 FAIL，成了**过期牙**——那唯一的一个红计数就是它，不是产品缺陷）。
换口径为两条（`eval-df1-criteria.py` `fc808896f23390f4`）：
* `TOOTH-D-F1b-ABSENT`（**现场**）：断言历史形态**不再出现** —— `TF_ADV_PRESENT=true` 且 `GID_LT_COUNT=true`（两条路都查）；
* `TOOTH-C1-REDCAP`（**指引**）：该判据"**能红**"由**判据层自验第 2 例**每趟保证（合成 D-F1b 日志 ⇒ 实得 `CRITERIA=FAIL`+`exit=1`，打印在本行上方）。

## 5. 一句话结论

**`D-F1b` 修好了（两条路 `FACE_URI` = 计划面、C1 三腿全绿、判据首次 `PASS`）；面选择 26/26 未变；`D-F1` 无回退；但内存只从 3.30 GB 降到 2.02 GB（未达成），且 `faceLoads` 不降反升（372→393）、`coverClears=0` ⇒ 物化在"扫描/挑面"路上，`Release` 似乎没被调到。**
