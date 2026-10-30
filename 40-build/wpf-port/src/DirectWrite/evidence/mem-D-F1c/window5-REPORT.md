# 窗口 5：一个文件一份映射（T2）—— **本次 shim = `6c3afa3046073786`**（mtime 12:23:25）

**件**：shim `6c3afa3046073786`｜PC `9adac6b8d8e285c3`｜备份自证 `~/t1d-backups/20260915-1223-shim-14f6a728-preW5.cs` = `14f6a728bc834c0e` ✓（我重算）
**仪器**：宿主 DLL `44ca52a625d83833`（编入本件）、判据 `fc808896f23390f4`（`D-F1 判据 v2`）、采样器 `$HOME/wfp-runs/bounded-run.sh`（0.2 s、整棵树、smaps_rollup、`/usr/bin/time -v` 权威峰值）
**环境**：`WPF_LINUX_FONT_DIR=<未设>`（系统 371 面）；`loadavg 1.82/1.40/1.37`；`free -m` used 4543 / free 481 / avail 3037

---

## 1. 峰值与 smaps（同命令、同仪器，与窗口 3/4 逐位对拍）

| 趟 | 权威峰值 | `shared_clean`@峰值 | `priv_dirty` | `anon` | 墙钟 |
| --- | --- | --- | --- | --- | --- |
| 窗口 3（`92fc7605`） | 2,119,824 KB | 1,748,632 KB（88%） | 201,044 | 195,884 | 3 s |
| 窗口 4（`14f6a728`） | 2,119,788 KB | 1,748,720 KB（87%） | 240,152 | 235,072 | 4 s |
| **窗口 5（`6c3afa30`）** | **2,099,304 KB ≈ 2.00 GB** | **1,752,776 KB ≈ 1.67 GB** | 282,056（13%） | 276,092 | 3 s |
| 窗口 5 判据趟 | 2,115,536 KB | — | — | — | 5 s |

⇒ **内存目标仍未达成**（−1.0% vs 窗口 4）；**`shared_clean` 没动**（+0.2%，噪声内）⇒ 映射仍是主项（83–84%）。

## 2. 计数：**修法确实生效了**（这就是它的功劳）

```
faceLoads=393 → 22        （**扫描不再建 Entry** ✓✓）
releaseCalls=371 → 0      （**扫描不再走 Release** ✓✓）
residentCount=22（不变） evictions=0  coverClears=0  s_coverEntries=41  maxFaces=512  segmentFaceResolveCalls=3
coverageProbe=53（不变）  coverageCacheHit=12（不变）  candidates=371(扫描1次) ✓  Scans=1 ✓
```

## 3. 新证据：**"每面一整份映射"仍然成立**（三档缩放，各 ~2 秒）

| 字体目录 | `candidates`（面） | 权威峰值 | **`shared_clean`@峰值** | 理论 Σ(大小×面) |
| --- | --- | --- | --- | --- |
| 1 个 CJK ttc（`$HOME/wfp-runs/fontdir-1`） | 10 | 410,128 KB ≈ 400 MB | **203,604 KB ≈ 199 MB** | **186 MB（18.6 MB × 10 面）⇒ 实测/理论 = 1.07** |
| 7 个 CJK ttc（`fontdir-7`） | 45 | 979,676 KB ≈ 957 MB | 467,000 KB ≈ 456 MB | （45 面 × ~19 MB ≈ 855 MB） |
| 4 个拉丁 ttf（`build/fonts`） | 4 | 71,316 KB ≈ 70 MB | **0** | ~2 MB |

⇒ ① **1.07×** 那一档把"**每个面 = 整份文件的映射**"钉死（10 面 × 18.6 MB = 199 MB）；② 拉丁 ttf 档 `shared_clean=0` ⇒ 这条路径**只在大文件/`.ttc` 上显形**。

## 4. 按主控给的否证表判读

峰值**既不是 ≈413 MB，也没降下来** ⇒ 落 **"仍 GB 级 ⇒ 还有第二处逐面建 blob 的路径"** 那一支。
⇒ 建议 T1d 用 **`fontdir-1`（1 个文件 / 10 面 / 400 MB / 2 秒）**做二分，而不是系统目录那趟 2 GB：
那 6 处 `hb_blob_create_from_file` 调用者在这个最小件里就会被逐个验出来（`shared_clean` 只要从 199 MB 掉到 ~19 MB 就说明那一处修对了）。

## 5. 硬判据（全过，"不许变"逐条）

* **`CRITERIA=PASS`（fail=0 noinfo=0）`rc=0`**；`C1=PASS`（`null` 与 `fb` **两条路**）、`C2=PASS`、`C3=PASS`（差 0.0000/0.0007/0.0007）、`TOOTH-D-F1b-ABSENT=PASS`、`[fix]=已生效`
* **`D-F1` 正极性无回退**：`LINE_W=16.0000`、`CR_W=3.3440`、`GID=9498`、`ADV_DIP=16.0000`、`FACE_URI=…NotoSansCJK-Regular.ttc`
* **面选择普查 26/26 逐格相同** ✅ —— 与窗口 4 那趟**以及修前 `17b2cdfe`** 三向一致
* `candidates=371`、`Scans=1` **不变** ✓；允许变的（已声明仪器位移）：`faceLoads`、`ReleaseCalls`、`coverageProbe`/`coverageCacheHit`

## 6. 原始件

`$HOME/wfp-runs/w5-mem-system/`（单次趟：`meta.txt` 含 `SNAP_AT_PEAK` 四列、`sample.txt` 每 0.2 s 一行）、
`w5-scale-{1CJK,7CJK,4LAT}/`、`w5-criteria/`、`w5-census/`
