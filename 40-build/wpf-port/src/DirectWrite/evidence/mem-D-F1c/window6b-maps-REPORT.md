# 窗口 6b：**峰值时刻的 `/proc/<pid>/smaps` 分解**（T2）—— 本次 shim = `ac4104d67687c2c9`

**件**：shim `ac4104d67687c2c9`（mtime 12:26:30）｜PC `9adac6b8d8e285c3`
**仪器**：宿主 DLL `e643c371141946e5`（含 `LiveBlobs` 反射）｜采样器 `$HOME/wfp-runs/bounded-run.sh`（**已修**，见 §1）
**env（本档）**：`WPF_LINUX_FONT_DIR=$HOME/wfp-runs/fontdir-1`（只有 1 个 CJK ttc）；`DF1_REPO=<repo>`；`loadavg ~1.2–1.4`；`free -m` used ~4.5 GB / avail ~3.0 GB

---

## 1. 装置先自检（主控 ① 要求"先自检再跑"）

* **旧版为什么全 0 行**：`cp /proc/<pid>/maps` 对 `tree_pids` 里**每个** PID 都做 ⇒ 其中**瞬时子进程已消失** ⇒ `stat 失败` ⇒ 一个文件都没落。
  实测错误原文：`cp: 对 '/proc/1036579/maps' 调用 stat 失败: 没有那个文件或目录`
* **自检（用一个显式 mmap 字体文件的久活进程）**：`python3 -c "mmap …NotoSansCJK-Regular.ttc…; sleep 8"`
  ⇒ 快照落盘、非空（2241 B/5416 B）、**含字体行 1 条**、`shared_clean=7092 KB` 与映射一致 ✓
* **修法**：只钉住**本 tick RSS 最大的那个 PID**（= 真正的 dotnet），其余尽力而为；并新增 **`/proc/<pid>/smaps`**（带**逐段 Rss**）+ 成败日志 `snap-ok.txt`。
  本趟实测：`snap pid=1038466 maps=68684B smaps=500743B`；瞬时子进程记 `SKIP(已退出)` ✓

## 2. 决定性读数：**同一个 `.ttc` 被映射了 12 段**

| 文件 | **段数** | Σ虚拟 | **ΣRss** |
| --- | --- | --- | --- |
| `/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc`（磁盘 **18.6 MB**） | **12 段** | **223.0 MB** | **161.0 MB** |
| `…/build/fonts/NotoSans-Regular.ttf`（段落字体，~0.4 MB） | 2 段 | 0.8 MB | 0.2 MB |
| **Σ Rss（字体映射）** | — | — | **161.2 MB** |

* **223.0 MB ÷ 18.6 MB = 12.0** ⇒ **同一个文件被独立映射了 12 份整文件**（不是 1 份 18.6 MB）。
* **对账（主控的判据）**：Σ Rss(字体) **161.2 MB** ↔ `smaps_rollup` 的 `shared_clean` **198.9 MB** ⇒ 字体占 **81%**，其余是 dotnet 自身的共享库 ⇒ **对得上**（不是采样器问题）。
* 与 shim 侧的既有事实互证：`liveBlobsPeak=1`、`[LIVEBLOBS]` 0 行、`hb_blob_create_from_file` 直连只剩包装器内部一处 ⇒ **这 12 份映射不在 HarfBuzz 的 blob 门里**。

## 3. 结论（给 T1d 的靶子）

**"每面一整份映射"现在是直接看到的，不是推的**：一个 10 面的 ttc 被映射 **12 段**（10 面 + 2）。
⇒ 与主控锁定的两个 shim 之外候选**完全吻合**，且**段数可直接用于二分**：
* ㈠ PC 侧 `FileMapping : UnmanagedMemoryStream`（`build/PresentationCore.Linux/FontCacheUtil.Linux.cs:824`）若按"**文件+面**"各建一份 ⇒ 10 段；
* ㈡ 渲染侧 `SKTypeface.FromFile(path, faceIndex)`（`src/WpfGfx.Linux/Interop/MilHandleTables.cs:671`）若 FreeType 每 typeface 各 mmap 整份 ⇒ 也是 10 段。
**下一窗的判据可以变成一句话**：修对之后，`NotoSansCJK-Regular.ttc` 的**段数应从 12 落到 1–2**、ΣRss 从 161 MB 落到 ~19–37 MB、`shared_clean` 落到 ~30 MB 量级。
（`fontdir-1` 档 2 秒一趟；系统档同比例放大 ⇒ 这就是那 1.67 GB 的来源。）

## 4. 原始件

* `$HOME/wfp-runs/w6d-1CJK/{smaps-at-peak-1038466.txt, maps-at-peak-1038466.txt, snap-ok.txt, meta.txt(SNAP_AT_PEAK), sample.txt}`
* 自检：`$HOME/wfp-runs/sampler-selfcheck/`
* 上一窗（组 1/2/3 与硬判据）：`$HOME/wfp-runs/window6-REPORT.md`（`d0fc79ff21ca0db8`）
