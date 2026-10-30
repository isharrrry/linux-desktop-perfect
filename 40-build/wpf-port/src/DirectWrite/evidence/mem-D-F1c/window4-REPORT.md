# 窗口 4：smaps 分解 + 全部计数（T2）—— **本次 shim = `14f6a728bc834c0e`**（mtime 12:19:26）

**件**：shim `14f6a728bc834c0e`（`+8/−0` 纯计数）｜PC `9adac6b8d8e285c3`｜备份自证 `~/t1d-backups/20260915-1219-shim-92fc7605-preW4.cs` = `92fc7605480fb289` ✓（我重算）
**仪器（运行那一刻）**：`Program.cs` 现行、后修宿主 DLL `121b72289b45063a`、判据 `fc808896f23390f4`（`D-F1 判据 v2`）
**环境**：`WPF_LINUX_FONT_DIR=<未设>`（系统 371 面）；`loadavg` 0.88/1.07/1.31；`free -m` used 4310 / free 678 / avail 3271；全程串行

---

## 1. 免费对照（本件=纯计数 ⇒ 行为应与 `92fc7605` 相同）✅

| 趟 | `TIMEV_MAX_RSS_KB`（`/usr/bin/time -v`） |
| --- | --- |
| 窗口 3（`92fc7605_5480fb289`） | **2,119,824** |
| 窗口 4 第 1 趟（`14f6a728`） | **2,119,788**（Δ **36 KB = 0.0017%**） |
| 窗口 4 第 2 趟（重跑，为读 `SegmentFaceResolveCalls`） | 2,124,816（Δ 0.24%） |
⇒ **复现**（不是"红旗"）；差异在运行间噪声量级。

## 2. smaps_rollup 四列 @**峰值那一刻**（0.2 s 采样，整棵树求和）

| 趟 | Rss | **Private_Dirty** | Private_Clean | **Shared_Clean(Shared_File)** | Shared_Dirty | Anonymous |
| --- | --- | --- | --- | --- | --- | --- |
| 第 1 趟 | 1,979,960 | 201,044（10%） | 31,132 | **1,748,632（88%）** | 24 | 195,884 |
| 第 2 趟 | 2,018,700 | 240,152（12%） | 30,992 | **1,748,720（87%）** | 36 | 235,072 |
| 判据趟（三模式×三段落） | 2,110,680 | 314,164（15%） | 34,668 | **1,761,720（83%）** | 36 | 308,000 |

**⇒ 判读（按主控给的判读表）：`Shared_File`/`Private_Clean` 占大头 ⇒ 「映射仍驻留」那一支 ⇒ 走 blob 引用链。**
`Private_Dirty` 只 196–235 MB（≈10%）⇒ **不是**"native 堆未归还"主导。
**注意 `shared_clean` 三趟几乎不动**（1,748,632 / 1,748,720 / 1,761,720 KB，前两趟 Δ=88 KB）⇒ 它是**确定的映射集合**，不是分配器噪声。

## 3. 全部计数（反射逐名读；**逐字**）

```
FACECACHE_DIAG=faceLoads=393 faceLoadFailures=0 evictions=0 coverClears=0 residentCount=22 releaseCalls=371
                maxFaces=512 coverEntries=NA s_coverEntries=41 s_faces=22 maxCoverEntries=262144 segmentFaceResolveCalls=3
                （找到 11/12 个字段）
FALLBACK_DIAG=… coverageProbe=53 coverageCacheHit=12 … fromSystemScan=1 segments=3 chunkedLines=1 candidates=371(扫描1次)
```
* `residentCount=22` + `releaseCalls=371` ⇒ **释放确实发生了**（缓存只剩 22 项），**但内存没还** ⇒ 与 smaps 一栏合起来看：没还的是**文件映射**。
* `segmentFaceResolveCalls=3`（= 被判行的 3 段）vs `faceLoads − evictions = 393` ⇒ **候选 A（驻留来自"段面解析"）不成立**（差两个数量级）。
* `coverEntries=NA`：字段是私有的 `s_coverEntries`（**41**，已读到）⇒ 我按名找不到 `CoverEntries` 是**名字问题**，不是缺失。
* `SegmentFaceResolveCalls` 原不属于 `HbFaceCache`（它由 `HbFallbackDiag` 持有，`:1106`）⇒ 我把反射改成**全类型搜索**后才读到（`11/12`）。

## 4. 独立定量交叉核对：**"一个面一份整文件映射"**

我按 shim 的目录口径（`/usr/share/fonts` 等）**自己枚举**候选：
* 文件 **299** 个、**面数合计 371**（与 shim 的 `candidates=371` **逐位相同** ✓）
* Σ文件大小 = **413 MB**
* **Σ(文件大小 × 面数) = 1,902 MB ≈ 1.86 GB**
* 实测 `shared_clean`（峰值）= **1.67 GB** ⇒ **实测/理论 = 90%**
⇒ 强烈支持：**同一个 `.ttc` 被按"面"重复 mmap**（如 `NotoSansCJK-Bold.ttc` 19.1 MB × 10 面 = 191 MB 一份文件就占 191 MB），而 `hb_blob_create_from_file` 的映射**没有被销毁**（`releaseCalls=371` 释放的是 face/缓存条目，不是映射）。
⇒ 可预期修法收益：映射总量从 **1.86 GB** 降到 **Σ文件大小 = 413 MB**（按面复用同一 blob），峰值预期 <600 MB。

## 5. 硬判据（全过）

* **`candidates=371`、`Scans=1`** ✓ 不变｜**允许变的**：`coverageProbe 60→53`、`coverageCacheHit=12`（不变）、新增计数
* **面选择普查 26/26 逐格相同 ✅**（与窗口 3 那趟、**以及修前 `17b2cdfe`** 三向一致；`RC=0`，峰值 2,131,064 KB）
* **`D-F1` 正极性无回退 ✅**：`LINE_W=16.0000`、`CR_W=3.3440`、`GID=9498`、`ADV_DIP=16.0000`、`FACE_URI=…NotoSansCJK-Regular.ttc`
* **判据趟 `CRITERIA=PASS`（fail=0 noinfo=0）`RC=0`**；`C1=PASS`（两条路）、`C2/C3=PASS`、`TOOTH-D-F1b-ABSENT=PASS`
* **内存目标：未达成**（峰值 2.10–2.12 GB ≫ 300 MB 硬线 1 GB）——但**归因已定位到"文件映射未销毁"**（§2+§4）

## 6. 关于"抬 T3 守卫到 2600 MB"：**我建议不抬**（附理由与风险）

1. **固定底噪 1.67 GB 与用例数无关**：2600 MB 会被这份映射吃掉 2/3，守卫留给"用例增长"的余量只剩 ~0.9 GB。
2. **大概率仍被止损**：harness 自身还要持有 614 例语料（T3 修前实测 RSS 已到 3.1 GB 级）⇒ 抬到 2600 MB 很可能只是**晚一点**触发 ⇒ 拿到的仍是"被守卫止损"，不是"能不能跑完"。
3. **越界后果更差**：本机 avail ~3.0–3.3 GB、swap 仅余 ~1.2 GB（`free -m` 交换 803/2047 已用）⇒ 真跑到 2.6 GB 时系统余量 <0.5 GB ⇒ 一旦越界就是 **swap 抖动**，正是我们花一天诊断的"零输出假死" ⇒ **证据质量更差**。
4. **更优路径**：先落"按面复用 blob / 销毁映射"（预期把 1.86 GB 映射降到 413 MB，峰值 <600 MB）⇒ 守卫不用抬也能干净跑完；若这轮就要"长趟能否跑完"的独立证据，请跑**有界切片**并**明确标注"切片，不是全趟"**，别用抬守卫把"被止损"洗成"跑完"。

## 7. 原始件

* `$HOME/wfp-runs/w4-mem-system/{meta.txt,sample.txt,stdout.txt}`（第 1 趟；`sample.txt` 每 0.2 s 一行含四列）
* `$HOME/wfp-runs/w4-mem-system2/`（第 2 趟，为读 `segmentFaceResolveCalls`）、`$HOME/wfp-runs/w4-criteria/`、`$HOME/wfp-runs/w4-census/`
