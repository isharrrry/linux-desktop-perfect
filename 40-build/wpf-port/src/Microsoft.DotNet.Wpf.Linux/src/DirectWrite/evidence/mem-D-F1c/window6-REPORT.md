# 窗口 6：活着的 blob（T2）—— **本次 shim = `ac4104d67687c2c9`**（mtime 12:26:30）

**件**：shim `ac4104d67687c2c9`｜PC `9adac6b8d8e285c3`｜备份自证 `~/t1d-backups/20260915-1226-shim-6c3afa30-preW6.cs` = `6c3afa3046073786` ✓（我重算）
**仪器**：宿主 DLL `b2696bfb07efdd24`（组 1/2 用）→ **`e643c371141946e5`**（加 `LiveBlobs/LiveBlobsPeak` 反射后，组 3 用）；判据 `fc808896f23390f4`
**环境**：`loadavg` 0.79–1.35；`free -m` used ~4.4–4.6 GB / avail ~3.0–3.2 GB；全程串行

---

## 1. 组 1：系统档（`WPF_LINUX_FONT_DIR=<未设>`，371 面）

| 量 | 窗口 4（`14f6a728`） | 窗口 5（`6c3afa30`） | **窗口 6（`ac4104d6`）** |
| --- | --- | --- | --- |
| 权威峰值 | 2,119,788 KB | 2,099,304 KB | **2,119,948 KB ≈ 2.02 GB** |
| `shared_clean`@峰值 | 1,748,720 KB | 1,752,776 KB | **1,753,220 KB ≈ 1.67 GB** |
| `priv_dirty` / `anon` | 240,152 / 235,072 | 282,056 / 276,092 | 282,412 / 276,496 |
| `candidates` / `Scans` | 371 / 1 | 371 / 1 | **371 / 1 ✓** |
| `coverageProbe` / `coverageCacheHit` | 53 / 12 | 53 / 12 | 53 / 12（不变） |
| `residentCount` / `releaseCalls` / `segmentFaceResolveCalls` | 22 / 371 / 3 | 22 / 0 / 3 | **22 / 0 / 3** |

⇒ **峰值与 `shared_clean` 三窗未动**（1.75 GB ∓0.3%）⇒ 内存目标仍未达成。

## 2. 组 2：三档缩放（各 ~2 秒；逐档 `WPF_LINUX_FONT_DIR` 已记）

| 档 | `WPF_LINUX_FONT_DIR` | `candidates` | 权威峰值 | `shared_clean`@峰值 |
| --- | --- | --- | --- | --- |
| 1CJK | `$HOME/wfp-runs/fontdir-1` | 10 | **415,348 KB**（→ 复跑 415,144） | **205,972 KB ≈ 201 MB**（→ 205,776） |
| 7CJK | `$HOME/wfp-runs/fontdir-7` | 45 | 994,588 KB | 528,232 KB |
| 4LAT | `$PWD/build/fonts` | 4 | 71,356 KB | **0** |

尺子（主控组 4）：1CJK 的目标值仍是 **~19 MB**（18.6 MB × 1 份）；现值 **201 MB ≈ 10 份** ⇒ **"每面一整份"未变**。

## 3. 组 3：**决定性数** —— `liveBlobsPeak = 1`（而映射没少）

反射读到：`residentCount=2`、`releaseCalls=0`、`segmentFaceResolveCalls=3`、**`liveBlobsPeak=1`**（1CJK 档）；
`[LIVEBLOBS]` **stderr 序列 0 行**（该行只在计数跨 10 的倍数时打印 ⇒ 全程**建过的 blob 不到 10 个**）。
而同一趟 `shared_clean = 201 MB`（1CJK）/ `1.67 GB`（系统档）。

⇒ **结论（实测，不是推断）：那 1.67 GB 文件映射不是 `hb_blob_create_from_file` 建的** —— HarfBuzz 的 blob 路径已经**不泄漏**（T1d 的修法有效 ✓），
但如果按"`liveBlobs` 峰值 − `residentCount` = 别处持有的活映射份数"去读，会得到 **1 − 2 < 0**（无意义）⇒ **"别处"不是 blob 计数器的持有者，而是另一套映射机制**。
⇒ 与 `CreateBlobFromFile` 的调用点清单对照：`hb_blob_create_from_file` 的直接调用**只剩包装器内部一处**（`:142`），所有 6 个调用者都走包装器 ⇒ **映射不在这条门里**。

**给 T1d 的下一步（两条，按可疑度）**：
1. **DWF/Skia 侧的文件字体加载**：`Typeface(FontFamily(family))`/`GetResolvedFace`/`segmentFaces` 构造（即 `D-F1b` 的 E1 新走的那条路）——它按**面**解析字体，可能在 Skia/FreeType 层 `mmap` 整份 `.ttc`；
2. 注意**拉丁小文件档 `shared_clean=0`**（4 个 ~0.5 MB 的 ttf）⇒ 这条路径**只在大 `.ttc`（18–26 MB）上显形**，与"按面加载"的量级吻合（10 面 × 18.6 MB = 186 MB ≈ 实测 201 MB）。

## 4. 判据（"不许变"全过）

* **`CRITERIA=PASS`（fail=0 noinfo=0）`rc=0`**；`C1=PASS`（`null`+`fb` 两条路）、`C2=PASS`、`C3=PASS`（0.0000/0.0007/0.0007）、`TOOTH-D-F1b-ABSENT=PASS`
* **`D-F1` 正极性无回退**：`LINE_W=16.0000`、`CR_W=3.3440`、`GID=9498`、`ADV_DIP=16.0000`、`FACE_URI=…NotoSansCJK-Regular.ttc`
* **面选择普查 26/26 逐格相同** ✅（与修前 `17b2cdfe` 一致）
* `candidates`（10/45/371）、`Scans=1` **不变** ✓；允许变的（已声明）：`faceLoads`/`releaseCalls`/`coverageProbe`/新增计数

## 5. 未取到 / 未做（照实说）

* **`maps` 峰值快照为空**：我给采样器加了"峰值时刻 `cp /proc/<pid>/maps`"，但本趟 4 个 `maps-at-peak-*.txt` **都是 0 行**（补丁没生效或 `cp` 未执行）⇒ **"谁在 mmap"这条最直接的证据这趟没有拿到**（下一窗我会先自检这条装置再跑）。也正因如此，§3 的结论只写到"不是 blob 门内的路径"，**没有**指认具体持有者。
* 组 3 的"更硬变体"（关掉共享 blob 的 `$HOME` 变体）**未做**（需要 T1d 的变体件）。
