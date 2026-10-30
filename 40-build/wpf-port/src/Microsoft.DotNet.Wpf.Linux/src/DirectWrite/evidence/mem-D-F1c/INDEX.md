# `mem-D-F1c` 归档索引（T2，2026-09-15；主控窗口 6b ② 批准）

**为什么归档**：这条线的原始件原先只在 `$HOME/wfp-runs/**`，而本机 `$HOME` **被整盘清过一次** ⇒ 支撑结论必须回仓。
**尺寸界（主控定）**：单文件 ≤2 MB、总计 ≤20 MB；可重生成的中间件（如逐行 `sample.txt`/`rss.csv`）**不进仓**，只留摘要行（见 `meta-all-windows.txt` 的 `SNAP_AT_PEAK` 行）。

| 文件 | sha16 | 哪一窗 | 支撑哪条结论 |
| --- | --- | --- | --- |
| `df1c-afterFIX-REPORT.md` | `51765eaa5b36eea9` | W2b（`1ebea99c` 死循环修好但仍 3.30 GB；`MULTIFONT=0` 不关这条路） | 判别量=映射；并记"63 MB 是卡住态"的撤回 |
| `df1c-localize-REPORT.md` | `474cb57daeeb9b10` | W2 定位（3.46 GB 一次读数；`ulimit -v` 不可用；面数扫描） | 判断"内存随面数涨"与"不是 hb_shape" |
| `diag-lines-all-windows.txt` | `964c1e3b1c9286e8` | W3–W6 | 判据用得到的读数行/诊断行（`FALLBACK_DIAG`/`FACECACHE_DIAG`/C1·C2·C3/牙/`CRITERIA=`） |
| `maps-at-peak-1038466.txt` | `39301679fb5cc84f` | W6b（1CJK 档，峰值时刻） | **段数**的直接证据（按文件数段） |
| `meta-all-windows.txt` | `1c55986786d83a86` | W2–W6b（21 个 run 的 `meta.txt` 汇总） | 每窗 env/log 头 + `SNAP_AT_PEAK` 四列 + RC/峰值/墙钟 |
| `smaps-at-peak-1038466.txt` | `2b144411b1e167b3` | W6b（1CJK 档，峰值时刻） | **逐段 Rss** ⇒ 与 `smaps_rollup.shared_clean` 对账（81%） |
| `snap-ok.txt` | `9bfcf11a2c33ab5b` | W6b | 证明快照确实落盘（含"瞬时子进程 SKIP(已退出)"） |
| `window3-REPORT.md` | `037b1b3599429b45` | W3 `92fc7605` | `D-F1b` 首绿 + 普查 26/26 + 换牙 |
| `window4-REPORT.md` | `65b67e95e06d3430` | W4 `14f6a728` | smaps 四列首次分解（`Shared_Clean` 83–88%）+ `residentCount/releaseCalls` |
| `window5-REPORT.md` | `61130a57ad79ad7e` | W5 `6c3afa30` | `faceLoads 393→22`、`releaseCalls→0` 但峰值未降 ⇒ 第二处逐面映射 |
| `window6-REPORT.md` | `d0fc79ff21ca0db8` | W6 `ac4104d67687c2c9` | `liveBlobsPeak=1` ⇒ 掐掉"blob 持有者"方向 |
| `window6b-maps-REPORT.md` | `a155a0f76dae00de` | W6b `ac4104d67687c2c9` | **决定性**：同一 `.ttc` 12 段 / Σ虚拟 223.0 MB / 字体 ΣRss 161.2 MB ↔ `shared_clean` 198.9 MB |

**总计 685 KB / 12 个文件**（限额 20 MB ⇒ 远未触界）。

**口径（§32.1 的摘要）**：六窗的 shim 依次 `17b2cdfe → 92fc7605 → 14f6a728 → 6c3afa30 → ac4104d6`；
仪器：宿主 `FallbackCriteria.dll` 逐窗为 `c3e5d553…/ad239102…/…/e643c371141946e5`（见 §32.1 与各窗报告），
采样器 = `build/DirectWrite.Linux/FallbackCriteria/mem-sampler.sh`（sha16 `09e5bab7c3d246f6`，`--selfcheck` 必须 PASS 才可用）。

**复取配方（M7b 落修法后用）**：`WPF_LINUX_FONT_DIR=$HOME/wfp-runs/fontdir-1` + `--mode=null --para=b34` + `maps`/`smaps` 快照；
**主判据 = 段数 12 → 1–2 与这些段的 Σ虚拟**（mmap 后未触碰的页不计 RSS ⇒ RSS/`Shared_Clean` 只作旁证）。
