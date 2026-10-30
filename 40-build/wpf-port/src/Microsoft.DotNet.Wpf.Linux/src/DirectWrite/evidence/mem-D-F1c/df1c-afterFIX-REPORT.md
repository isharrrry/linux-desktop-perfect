# `D-F1c` 修后（`16db2d6194edc1f5`）正/负极性 + 二分定位（T2，2026-09-15 11:39–11:52）

**件**：shim 源 `build/shims/PresentationCore.HbTextLine.cs` sha16 **`16db2d6194edc1f5`**（mtime 11:35:56，**这就是本次全部读数的 shim**）
｜PC `9adac6b8d8e285c3`｜**后修宿主** `FallbackCriteria.dll` `ad239102881a787b`（编入上面那份 shim）
｜**修前对照宿主** `$HOME/wfp-runs/df1c-prefix-host/FallbackCriteria.dll` `d6116ddfd84248ee`（编自 `$HOME/t1d-backups/20260915-1135-shim-17b2cdfe-preF1cF1b.cs`，我独立重算该文件 sha16 = `17b2cdfe08f13280` ✓）
｜装置：`bash $HOME/wfp-runs/bounded-run.sh <秒> <目录> <cmd>`（树采样 + 树杀 + `/usr/bin/time -v` 权威峰值；`DOTNET_GCHeapHardLimit`，**不用 `ulimit -v`**）

## 一、正极性（任务 1）：**内存达成，且超额**

| 宿主 | 字体目录（候选面数） | 结果 | **权威峰值 RSS** | CPU | 墙钟 |
| --- | --- | --- | --- | --- | --- |
| 后修 | **0 面**（空目录） | **RC=0 完成** | 74,104 KB ≈ 72 MB | ~0.1 s | 1 s |
| 后修 | 4 面 `build/fonts` | **未完成（看门狗 45 s）** | 64,704 KB ≈ **63 MB** | 44.9 s | 48 s |
| 后修 | 10 面（1 个 CJK ttc） | **未完成（看门狗 120 s）** | 65,436 KB ≈ **64 MB** | 119.6 s | 122 s |
| 后修 | 45 面（7 个 CJK ttc） | **未完成（看门狗 120 s）** | 65,804 KB ≈ **64 MB** | 119.0 s | 122 s |
| 后修 | 371 面（系统目录） | **未完成（看门狗 150 s）** | 65,236 KB ≈ **64 MB** | 141.0 s | 153 s |
| 修前 | 4 面 | RC=0 完成 | 65,532 KB | 0.4 s | 0 s |
| 修前 | 371 面（系统） | RC=0 完成 | **3,464,264 KB ≈ 3.30 GB** | 4.0 s | 4 s |

* **内存目标：达成 ✓✓** —— `3.46 GB → 63–65 MB`（**≥50×**；远低于 300 MB 目标与 1 GB 硬线），而且**跨 4/10/45/371 面全部平台化在 63–65 MB**（修前是随面数涨）。
* **但出现新的 CPU 症状（阻塞读数）**：**只要候选面 ≥1 就完不成**（4 面就 >45 s，而修前 0.4 s），**0 面则 1 秒完成** ⇒ 与面数**不成比例**（1 个文件 10 面 与 371 面都是"完不成"）⇒ 更像**每探测一次就卡住/活锁**，而不是"每面慢一点"。
* **`Scans==1`/`candidates==371`/`FaceLoads`/`Evictions` 取不到**：这几项只在跑完时才打印（`SCAFFOLD_DIAG`），而本趟跑不完 ⇒ 任务 1 的这四个字段**仍空**（不是没做，是被 CPU 症状挡住）。

## 二、负极性（任务 2）：**已拿到**（修前对照宿主）

修前宿主同命令：4 面 `RC=0 / 65,532 KB / 0.4 s`；系统 `RC=0 / **3,464,264 KB** / 4.0 s` ⇒ **GB 级可复现** ✓，且证明"卡"是**后修引入**（修前 4 面 0.4 s 就完成）。

## 三、二分（给 T1d 的四格）

| 变量 | 观察 |
| --- | --- |
| 候选面 0 vs ≥1 | 0 ⇒ 1 s 完成；≥1 ⇒ 卡（4/10/45/371 面全卡） |
| 段落是否需要回退 | **不需要**也卡：全拉丁段 `b34line0`（段落字体自己覆盖）在 4 面下同样卡 |
| `WPF_LINUX_MULTIFONT=0` | **不改变症状**（`plan==null` 路径本就不看这个开关，源码 `:3486-3508`） |
| `--probe-only`（只 `hb_shape`，不经 `FormatParagraph`） | **RC=0、1 s、63,708 KB**，且给出 `#0+zh-cn ⇒ gids=9498 / #0+en-us ⇒ gids=9497` ⇒ **shaper 路径正常** |
| `WPF_LINUX_TEXTLINE_DIAG=1` | **stderr 一行都没有**（stdout 只有 runner 的 4 行头）⇒ 卡点在**任何 diag 之前**（`FormatParagraph` 内部、候选探测/缓存那一段） |

⇒ 可疑区间仍是 `HbFontPlanner.Build → TryFindCovering` 的**逐候选探测**与 `HbFaceCache`（新 `FaceLoads/Evictions/CoverClears`）——
**"0 面秒过、≥1 面不返回"** 指向"第一次探测就进不去出"，而不是"探测次数多"。

## 四、同现象旁证（不是我的读数，是现场观察）

T3 的 `run.sh tline`（PID 245458）在 11:37–11:52 期间：**99.7% CPU、RSS 平 ~83 MB、已 9 分钟**（修前同段是"内存涨到 4.19 GB、零输出"）⇒ **同一签名**：内存不再涨、CPU 不再停。它那边有 1200 s 上限。

## 五、被挡住而**未做**的任务（照实报）

* **任务 3 面选择普查**：`--census`（我 11:38 新实现，逐格报 `SEL_PLAN`（内部装置）+ `FACE_URI/GID`（公开 API）+ 同源判定）——**跑不完**（它同样走 `FormatParagraph`）。
* **任务 4 `D-F1b` 的 C1 三腿**（`null/b34` 与 `fb/b34` 的 `GID_LT_COUNT=true`、覆盖腿、`AdvanceWidths[gid]×em==16.0000`）：需要**跑完的读数**⇒ 同样被挡。
* **任务 5 `D-F1` 正极性不得回退**：同上（`null/b34` 的 `LINE_W=16.0000`、`gids≠0` 都需要跑完）。
⇒ 四件**都准备好了**（代码/装置/对照宿主全部就位），**T1d 一落 CPU 那一处，我立刻按 HANDOFF 取**。

## 六、我这一趟自己踩到并修掉的三个仪器坑（诚实记，都会影响读数可信度）

1. **`dotnet` 不在非交互 shell 的 PATH 上** ⇒ 旧脚本静默变成 `CRITERIA=NOINFO reason=no-runner-output` ＝**把仪器故障伪装成"读数缺失"**。已修：显式解析 `dotnet`，找不到就**响亮退出 3**。
2. **采样器只采根 PID** ⇒ 走脚本包装时只看到 bash（3 MB），dotnet 峰值全看不见；**看门狗只杀根** ⇒ dotnet 变孤儿继续跑（实测 5 分钟无人管，我按 PID 杀掉）。已修：**采整棵树 + 杀整棵树**。
3. **秒级完成的跑采不到样** ⇒ `PEAK_HWM_KB=0` 会被误读成"0 MB"。已修：并用 `/usr/bin/time -v` 的 `Maximum resident set size` 作**权威峰值**（上表全部为权威值）。
