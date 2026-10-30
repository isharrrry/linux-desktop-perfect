# 在册红 —— `D-A2` 接线暴露的 app-local 副本（**登记 ≠ 已容忍**）

- **车道**：`W23C`｜**首次登记**：2026-09-16（+0800）｜**预登记**：`docs/WAVE23-PREREGISTRATION.md` §3（P3）
- **裁决**：用户 2026-09-16 裁决「`ITEMS` 补上 `PresentationCore.dll` 后今天就有旧副本会变红 ⇒ **先登记为在册红、判据立刻上线**（不先把它们刷绿）」。

## 0 · 这份登记**不是**豁免机制（读之前先看这三条）

1. `check-applocal-sync.sh` **没有任何既有的"在册红"机制**（实测：`grep -n 'known-red\|registry\|在册'` 全 0 命中；
   退出码只有 `0=PASS` / `1=MISMATCH` / `3=NOINFO`）。⇒ 本表**不参与判定**：
   **它不让任何一条红变绿，也不改任何 `CNT_*` 计数**。
2. 检查器对本表**只读只打印**（`check-applocal-sync.sh` 的 `show_registry()`）：逐条印
   `[在册红]` / `[在册红·已转绿]`（后者 = 该副本内容已等于**当前**权威 ⇒ **应从本表移除**）。
3. **`APPSYNC` 仍是 `MISMATCH`、`rc` 仍是非 0**。本波之后 `build/close-wave.sh:159-160` 会**长期**打印
   `⚠️ APPSYNC 非 PASS` —— 那是**登记在册的**红（`APPSYNC` 在 `close-wave` 里是**告警不是硬闸**），
   **不是新问题**；`build/publish-milbridge.sh:100-112` 也不因它改退出码。

> ⚠️ **"登记时权威 sha"是快照，不是常量**：本波 `W23B` 正在重建 `pc`
> （`build/PresentationCore.Linux/bin/Debug/PresentationCore.dll`）⇒ 权威一换，**哪些副本算"陈旧"全部会变**
> （可能更多、也可能更少）。引用本表前**先现场重算**权威 sha：

```bash
sha256sum build/PresentationCore.Linux/bin/Debug/PresentationCore.dll | cut -c1-16
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh
```

## 1 · 表 A：`PresentationCore.dll` 副本（逐份判定 = `STALE`，红）

- 现场读数：**2026-09-16 23:57:29 +0800**（`bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh`，`rc=1`）
- 当时权威：`build/PresentationCore.Linux/bin/Debug/PresentationCore.dll` = **`e7cabff9417ed380`**（4,196,864 B，mtime 2026-09-16 18:47:26）
- 判定行原文（`check-applocal-sync.sh` 的 `scan()` ⑥ 分支，`:278-281`）：`STALE  <路径>  EXPECT e7cabff9417ed380  ACTUAL <sha>（副本早 N 秒 ⇒ 必须刷新）`
- **共 12 份**（下表的"登记时副本 sha16"由 `sha256sum` 逐份取，`类别`按检查器当时的输出标签抄录）

- **⏪ `#50` 波尾（2026-09-22，车道 `W95A`）：本表已按自己的口径**（"已转绿 ⇒ 应从本表移除"）**删掉 12 条**
  （现场由 `check-applocal-sync.sh` 逐条判 `[在册红·已转绿]`；删前本表 **13 条：仍红 1 ｜ 已转绿 12**）⇒ 现在**只剩 1 条仍红**。
  ⚠️ 同趟另**刷新了本表原第 13 条**（`tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll` `16baacfccfcf1df0 → 374b5a538ea955aa`）——
  它**只能**由 `sync-applocal-authority.sh --apply` 刷，而该件的默认 `SCAN_ROOTS` **不含 `$REPO/tools`**（与校验器的默认根**不一致**，见 `build/MilBridge/W95A-report.md` §6.3）
  ⇒ 本趟显式传 `SCAN_ROOTS=…:tools` 才刷到；**该缺口已报主控，本波未改那件仪器**。删表**不参与判定**（`show_registry()` 只读只打印、`登记 ≠ 已容忍`）。
| 路径 | 登记时副本 sha16 | 登记时权威 sha16 | 类别 | 首次登记日期 | 处置 |
|---|---|---|---|---|---|
| build/PresentationFramework.Classic.Linux/bin/Debug/PresentationCore.dll | 95a669cc510337d1 | e7cabff9417ed380 | STALE | 2026-09-16 | 待裁决（**另一份 PC 构建**，4,068,864 B，非"旧版同一件"；刷新或声明） |

**表 A 汇总**（来源 = 同一趟读数；全仓 `PresentationCore.dll` 共 **33 份** = 12 + 6 + 12 + 2 + 1）：

| 类别 | 份数 | 说明 |
|---|---|---|
| `STALE`（**红**） | **12** | 见上表 |
| `OK` | 6 | 权威自身 1 + `build/PresentationFramework.Linux`/`build/ReachFramework.Linux`/`build/System.Printing.Linux`/`samples/WpfTextDemo`/`tests/…/ManagedLayer.Tests` 5 |
| `NO-AUTHORITY`（**判不了，不算不一致**） | 12 | Release/RID 路径副本（`is_release_path` × 权威是 Debug） |
| `SKIP(stub)` | 2 | `build/CycleStub.PresentationFramework.Linux`、`build/CycleStub.PresentationUI.Linux` |
| `SKIP(obj)` | 1 | `build/PresentationCore.Linux/obj/Debug` |

## 2 · 表 B：`tools/` 漏扫暴露的那一份（`SCAN_ROOTS` 补齐 `$REPO/tools` 之前**全仓零读数**）

| 路径 | 登记时副本 sha16 | 登记时权威 sha16 | 类别 | 首次登记日期 | 处置 |
|---|---|---|---|---|---|

- 该目录有 `GeometryOracle.runtimeconfig.json` ⇒ **是启动宿主**；288,768 B；mtime 2026-09-11 09:21:37。
- 补 `tools` 之前它**连枚举都没有过**（`find` 都没 find 到）⇒ 连 sha 都没被打印 ⇒ 本表是它**第一次**被点名。
---

## 3 · 现场快照（**2026-09-17 12:28 +0800，W25C 现场算，非抄录**）

**权威 `pc`**（`build/PresentationCore.Linux/bin/Debug/PresentationCore.dll`，现场 `sha256sum`）：

```
476994e35d31a7e1  4197376 B  mtime 2026-09-17 10:35:02   ← 本表全部"在册红"的**唯一裁判**
```

- **权威已换世代**：登记时的 `e7cabff9417ed380`（世代 `#23`）→ 现在 `476994e35d31a7e1`
  （世代 `#24`；`pc` 由车道 `W24A` 落地 `D-T5` 后重建 —— 报告 `build/MilBridge/W24A-report.md` `4b517c6e8d1f76e8`）。
- ⇒ **表 A 那 12 份现在**逐份 `sha == 现权威` ⇒ 都走 `[在册红·已转绿]` 那一支。
- ⇒ **表 B 那 1 份 `16baacfccfcf1df0` 原地未动**（mtime 仍 `2026-09-11 09:21:37`）⇒ **仍红**（**与 `pc` 世代无关**）。

**本次逐份实测**（左二列 = 上表的**历史值，一字未改写**；右四列 = 本次现场实测）：

| # | 路径 | 登记时副本 sha16 | 本次实测副本 sha16 | 本次实测权威 sha16 | 本次登记段判定 | 副本字节 | 副本 mtime |
|---|---|---|---|---|---|---|---|
| 1 | `build/DirectWrite.Linux/FontEntryClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 2 | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 3 | `build/DirectWrite.Linux/WicClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 4 | `build/DirectWrite.Linux/WicWriteClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 5 | `build/DirectWrite.Linux/WiringSmoke/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 6 | `build/DirectWrite.Linux/FallbackCriteria/bin/Debug/PresentationCore.dll` | `c0763fc10173e7ff` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 7 | `build/MilBridge/tests/CoverageProbe/bin/Debug/PresentationCore.dll` | `9adac6b8d8e285c3` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 8 | `build/MilBridge/tests/HbTextLineParity/bin/Debug/PresentationCore.dll` | `f4a454c8fe69cdfe` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:58 |
| 9 | `build/MilBridge/tests/TextLineProto/bin/Debug/PresentationCore.dll` | `f4a454c8fe69cdfe` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 10 | `build/PresentationFramework.Classic.Linux/bin/Debug/PresentationCore.dll` | `95a669cc510337d1` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 11 | `samples/HelloWpf/bin/Debug/net10.0/PresentationCore.dll` | `f31822ce4a3e510d` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:36:00 |
| 12 | `samples/WpfFeatureProbe/bin/Debug/net10.0/PresentationCore.dll` | `9adac6b8d8e285c3` | `476994e35d31a7e1` | `476994e35d31a7e1` | **已转绿**（== 现权威） | 4197376 | 2026-09-17 10:35:59 |
| 13 | `tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll` | `16baacfccfcf1df0` | `16baacfccfcf1df0` | `476994e35d31a7e1` | **仍红**（≠ 现权威） | 288768 | 2026-09-11 09:21:37 |

**判定依据（行号 = 读该文件当时的现场行号；源码原文）**：

```
911   case "$rsha" in                   # 只认"登记行"：第 2 列必须是 16 位小写 hex 的副本 sha16
916   off=""; in_scan_roots "$REPO/$path" || off="（**不在本次扫描根内** ⇒ 本次没枚举它）"
917   if [ ! -f "$REPO/$path" ]; then   #   → :918 印 **缺件** 并 continue
920   cur="$(sha "$REPO/$path")"; nowauth="$(auth_sha_of "$(basename "$path")")"
921   if [ "$nowauth" != "<no-authority>" ] && [ "$cur" = "$nowauth" ]; then
922       green=$((green+1)); printf '    [在册红·已转绿] …'      # ← 本次 12 份走这一支
924   else
925       still=$((still+1)); printf '    [在册红] …'            # ← 本次 1 份（表 B）走这一支
929   echo "    在册 $n 条：**仍红 $still** ｜ 已转绿 $green（见上，应从表里删）｜ 缺件 $gone"
```

⇒ 本次 `show_registry()` 的汇总口径应为 **`在册 13 条：仍红 1 ｜ 已转绿 12 ｜ 缺件 0`**。

**现场实算**（把上面 `:905-929` 的**逐行读法**用 `bash` 复刻后在**本文件上**跑一遍，同一 `sha()`/`auth_sha_of()` 语义）：

```
  [在册红·已转绿] build/DirectWrite.Linux/FontEntryClosedLoop/bin/Debug/PresentationCore.dll   476994e35d31a7e1（现权威 476994e35d31a7e1；登记时 23567d420f0dbbaa）
  [在册红·已转绿] build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/PresentationCore.dll     476994e35d31a7e1（…；登记时 23567d420f0dbbaa）
  [在册红·已转绿] build/DirectWrite.Linux/WicClosedLoop/bin/Debug/PresentationCore.dll        476994e35d31a7e1（…；登记时 23567d420f0dbbaa）
  [在册红·已转绿] build/DirectWrite.Linux/WicWriteClosedLoop/bin/Debug/PresentationCore.dll   476994e35d31a7e1（…；登记时 23567d420f0dbbaa）
  [在册红·已转绿] build/DirectWrite.Linux/WiringSmoke/bin/Debug/PresentationCore.dll          476994e35d31a7e1（…；登记时 23567d420f0dbbaa）
  [在册红·已转绿] build/DirectWrite.Linux/FallbackCriteria/bin/Debug/PresentationCore.dll     476994e35d31a7e1（…；登记时 c0763fc10173e7ff）
  [在册红·已转绿] build/MilBridge/tests/CoverageProbe/bin/Debug/PresentationCore.dll          476994e35d31a7e1（…；登记时 9adac6b8d8e285c3）
  [在册红·已转绿] build/MilBridge/tests/HbTextLineParity/bin/Debug/PresentationCore.dll       476994e35d31a7e1（…；登记时 f4a454c8fe69cdfe）
  [在册红·已转绿] build/MilBridge/tests/TextLineProto/bin/Debug/PresentationCore.dll          476994e35d31a7e1（…；登记时 f4a454c8fe69cdfe）
  [在册红·已转绿] build/PresentationFramework.Classic.Linux/bin/Debug/PresentationCore.dll    476994e35d31a7e1（…；登记时 95a669cc510337d1）
  [在册红·已转绿] samples/HelloWpf/bin/Debug/net10.0/PresentationCore.dll                     476994e35d31a7e1（…；登记时 f31822ce4a3e510d）
  [在册红·已转绿] samples/WpfFeatureProbe/bin/Debug/net10.0/PresentationCore.dll              476994e35d31a7e1（…；登记时 9adac6b8d8e285c3）
  [在册红]        tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll                     16baacfccfcf1df0（现权威 c400ab1638e0c3d2；登记时权威 c400ab1638e0c3d2）
  在册 13 条：**仍红 1** ｜ 已转绿 12 ｜ 缺件 0
```

⚠️ **本车道没有亲自跑该检查器**（那是 W25B 的写域，且此刻它正在改同一个文件 ⇒ 读数不可归因）；
上面那 13 行是**用检查器自己的读法在本文件上实算**出来的（不是猜、不是抄），
且**登记条目数仍 = 13**（本文件加了三节新内容 ⇒ 第二列**不含** 16 位 hex 的表行**不会被误认成登记条目**，
已逐行核对：被 `:911-914` 那道闸挡掉的新行 = 表头/汇总/测试表三种）。
上表所有 sha16/字节/mtime 由 `sha256sum`/`stat` **现场算**；"12 绿 / 1 红"由**同一段源码逐行演算**得出
（该分支**只比 sha**，不比 mtime/权限；且 `show_registry()` 只打印，见 §5）。

## 4 · 按世代结算（**纪律 52**：`[在册红·已转绿]` **只在某一个权威 `pc` sha 下成立**）

### 4.1 三个批次

| 批次 | 登记行 | 台账读到的 `pc` 世代 | 当时权威 sha16 | 当时判定 | **归哪个 pc 世代** |
|---|---|---|---|---|---|
| **PC-1** | 表 A 12 份的 `登记时副本 sha16` | `#23`（登记当时的树） | `e7cabff9417ed380` | **红**（`STALE`） | 属 `#23` 世代；**与今天的判定无关** |
| **PC-2** | 表 A 12 份的 `登记时权威 sha16` | `#23` | `e7cabff9417ed380` | —— | 同上（**这是"当时**未来**的权威"，登记表在 `#23` 波中途写成，`pc` 稍后才定型**） |
| **PC-3** | 表 A 12 份的 `本次实测` | `#24` | `476994e35d31a7e1` | **已转绿** | **只在 `pc == 476994e35d31a7e1` 下成立** |
| **PC-4** | 表 B 1 份（`tools/GeometryOracle`） | **与 `pc` 无关** | 权威 = `src/WpfGfx.Linux/bin/Debug/net10.0/WpfGfx.Linux.dll` = `c400ab1638e0c3d2` | **仍红** | 归 **`WpfGfx.Linux.dll`** 的世代，不是 `pc` 的 |

### 4.2 逐份结算（12 份 `pc` 副本 —— **逐行同结论**）

- **`登记时副本 sha16` 列**（表 A 左二列）：归 **`#23` 世代的"未收敛态"** —— 它是"该消费者**最后一次构建时**拷到的那一代"。
- **`登记时权威 sha16` 列**：是**写这份登记那一刻的将来权威** `e7cabff9417ed380`（`#23` 波中途，`pc` 尚未定型）。
- **本次实测**：全部 `== 476994e35d31a7e1` ⇒ **归 `#24` 世代**。
- **它们是被"刷新"收敛的，不是"自己重编"收敛的**（现场 mtime 证）：
  权威 mtime `2026-09-17 10:35:02`，而 12 份副本 mtime 全在 **`10:35:58–10:36:00`**（**晚于**权威 56–58 s）；
  机制 = `build/DirectWrite.Linux/wic-shim/sync-applocal-authority.sh`（默认**干跑**、`--apply` 才写；
  只刷"同类 + 同配置 + 落后"的副本）⇒ 它覆盖了这 12 份。**⚠️ 该刷新动作本身不在本车道射程，未取证是哪一趟触发的。**
- **换世代的后果（**这是本节的要点**）**：`pc` **每重建一次**，那 12 份的"已转绿"**立刻又 stale** ——
  `#24` 已实测过一次（主控记录 `MISMATCH 13 → 18`，见 `samples/WpfTextDemo/ACCEPTANCE-BASELINE.md`）。
  ⇒ **"已转绿" ≠ 永久销账**（与 `docs/WAVE24-PREREGISTRATION.md` 的口径一致）。
- **⚠️ 队列形状的含义**：12 份与权威**同处一条刷新路径**（同 `Debug`、同目录树形态）
  ⇒ 只要波尾/集成波跑一次 `sync-applocal-authority.sh --apply`，它们就**集体**收敛
  ⇒ **它们不是"12 个独立缺陷"，而是"一条刷新机制覆盖了 12 个消费者"**。
  ⇒ 因此 **12 份的账应记在"刷新机制是否在波尾跑过"上**，不是记在这 12 条路径上。

### 4.3 表 B 那 1 份为什么**与 `pc` 世代无关**

- 它的权威是 **`WpfGfx.Linux.dll`**（`src/WpfGfx.Linux/bin/Debug/net10.0/`，现场算 = `c400ab1638e0c3d2`，358,912 B，mtime `2026-09-16 15:08:58`）。
- 它 `pc` 换几个世代都**不动**：本次实测 `16baacfccfcf1df0`（288,768 B，mtime `2026-09-11 09:21:37`）= 与登记**逐位相同**。
- 它的 mtime **早于**其权威 mtime（09-11 vs 09-16）⇒ 它在上一波刷新里**没被覆盖**；
  `sync-applocal-authority.sh` 是否覆盖 `tools/**`（`SCAN_ROOTS` 含 `$REPO/tools`，但刷新器的取数路径是另一回事）**本车道未取证** ⇒ **登记为"待查"，不作为结论**。

### 4.4 "转绿"是世代绑定的 —— **两极性实证（本地沙箱，仓库一个字节未动）**

唯一变量 = **权威件的内容**（同一份登记 + 同一份副本 + 同一命令形态）：

| 趟 | 沙箱权威 sha16（现场算） | 登记段措辞 | `show_registry()` 汇总 | `rc` |
|---|---|---|---|---|
| ① | `476994e35d31a7e1`（== 副本） | **`[在册红·已转绿]`** | 在册 1 条：仍红 0 ｜ 已转绿 **1** ｜ 缺件 0 | 1 |
| ② | `f21b1f8f6edd9da6`（**换成别的 sha**） | **`[在册红]`**（仍红） | 在册 1 条：仍红 **1** ｜ 已转绿 0 ｜ 缺件 0 | 1 |
| ③ | `476994e35d31a7e1`（**换回**） | **`[在册红·已转绿]`** | 在册 1 条：仍红 0 ｜ 已转绿 **1** ｜ 缺件 0 | 1 |

⇒ **`GENBIND=PASS`**：只换权威 sha，`已转绿 ⇄ 仍红` **两个方向都实测成立**；而 **`rc` 三趟相同**
⇒ 再次证明本段**不参与判定**（变的只是登记段的措辞）。
（测试脚本 `$HOME/w25c-run/run-genbind-test.sh`（sha16 `b4afa20e24aad84d`），输出 `$HOME/w25c-run/genbind.out`（`d51fe240d11e9267`）；
被测脚本 = `check-applocal-sync.sh` `7bc9364091a28fd4`。沙箱布局必须把**权威件 + 副本 + 登记表**放同一个 `$REPO` 根下 ——
因为检查器里 `REPO="${AUTH_ROOT:-…}"` **同时**是权威根与登记表路径的根。）

## 5 · 只读性证明（**删掉本文件不会让任何判据变绿/变红**）

**① 读者集合（现场 `grep`，排除本文件自身）**：`known-red-PC-copies.md` 这个字符串在全仓
（`*.sh`/`*.py`/`*.cs`/`*.json`）只有 **3 处命中**，全在 `check-applocal-sync.sh` 里：
`:147`（`ITEMS` 的**说明文字**提到它）、`:892`（注释）、`:896` 的
`REGISTRY="${REGISTRY_FILE:-$(dirname "$0")/known-red-PC-copies.md}"` ⇒ **它是唯一消费者**。
（行号 = **读该文件当时的现场行号**；W25B 正在改同一个文件，行号会移动。）

**② 沙箱三趟实测**（唯一变量 = 登记文件：**有登记** / **空登记** / **登记文件整份不存在**）：

```
rc：A(有登记)=1  B(**无登记文件**)=1  C(空登记)=1
[A] 计数：… MISMATCH=0（STALE=0 NEWER-DIFF=0）MISSING=0 UNEXPECTED=1[DECL-GAP-EQ=1] … AUTH-MISSING=0  BRIDGE-ANCHOR=0  BRIDGE-NOINFO=0
[B] 计数：… （与 A **逐字相同**） …
[C] 计数：… （与 A **逐字相同**） …
[A]/[B]/[C] APPSYNC=MISMATCH（… 逐字相同）
[A]/[B]/[C] scan() 内部 rc=1（逐字相同）
剔除登记段 + 归一化沙箱路径后：cmp A B = **IDENTICAL**；cmp A C = **IDENTICAL**；行数 104 / 104 / 104
sha16 = c07c481d87324ebc（三份**同一个** sha）
```

⇒ **登记文件的存在/内容/缺失，不改 rc、不改任何计数器、不改 `APPSYNC`、不改 `scan()` 的 rc**；
删掉它，只是**登记段整段消失**（B 趟连 `--- 在册红` 那一行都没有）。
（测试脚本 `$HOME/w25c-run/run-readonly-test.sh`（sha16 `53b3d1b65e4b85f1`），输出 `$HOME/w25c-run/readonly-test.out`（`9a1819b9b792fc7a`）；
跑的是 `check-applocal-sync.sh` `7bc9364091a28fd4`（**含 W25B 新计数器** —— "判定行逐字相同"这条在它上面也成立）；
**同一脚本的早期版本**（W25B 改之前）上也独立跑过一次、同结论（剔段后 sha16 `1c97df146afe27c5` × 3）。）

**③ 本文件的三段免责原文一条未删**：§0 第 1–3 条（"不参与判定 / 只读只打印 / `APPSYNC` 仍 `MISMATCH`"）
**逐字保留**；`rc` 的语义与所有权重（`CNT_MISMATCH`/`CNT_RETIRED`/`CNT_DIVERGENT`/`CNT_MISSING`/
`CNT_UNEXPECTED`/`CNT_AUTHMISS`/`scanrc`）**一个字节未动**。

## 6 · 射程与未取证（**明确列出**）

1. **实际数目 = 13（现场核对与登记一致）**：`known-red-PC-copies.md` 里符合"登记行"格式（第 2 列 16 位小写 hex）
   的行**恰好 13 条**（表 A 12 + 表 B 1），**与现场点名数一致 ⇒ 无需修正条数**。
2. **但"全仓副本数"已与登记表中写的 `33` 不同**：本次现场枚举 `PresentationCore.dll` = **34 份**
   （= 权威自身 1 + 非权威 33）；登记表写的是"33 = 12 + 6 + 12 + 2 + 1"（**不含**权威自身）⇒ 两套口径混着读会差 1。
   非权威 33 份的现场分组（**本车道现场枚举，不是跑检查器**）：**12 份** mtime `10:35:58–10:36:00`（表 A 那 12）；
   **15 份** mtime ∈ `{10:35:02, 09:54:50}`；**6 份** mtime `10:36:00–10:36:01`。
   ⚠️ **这 33 份各自的 `OK`/`NO-AUTHORITY`/`SKIP(obj)`/`SKIP(stub)`/`LIB-COPY` 精确分类，本车道未复算**（不跑检查器）⇒ **NOINFO**。
3. **未取证**：
   - 本波**没有**亲自跑 `check-applocal-sync.sh`（W25B 写域 + 正在被改）⇒ §3 的"12 绿 / 1 红"是**源码演算**，不是门禁读数；
   - `pc` 之外**其它 `ITEMS`**（`libwpfwic.so`/`libwpfwin32.so`/`Provider`/`WpfGfx.Linux.dll`/`ReachFramework.dll`）的副本**本轮没有逐份复算**；
   - `sync-applocal-authority.sh --apply` **是哪一趟、由谁**触发的（**没用 `pgrep -f` 下结论**）；
   - `tools/**` 是否在刷新器的覆盖范围内；
   - `#25`（W25A 落地 `D-T5-R`）**`pc` 必变** ⇒ **本表 §3 的快照在 `#25` 波尾必然过期，需重取**。

---

## 7 · ⚠️ **世代切换现场实录**（本车道**开工期间**发生 —— 这件本身就是本节的证据）

**时间线（全部现场算，非回忆）**：

| 时刻（+0800） | 权威 `pc`（`build/PresentationCore.Linux/bin/Debug/PresentationCore.dll`） | sha16 | 字节 | 12 份 `pc` 副本的登记段判定 |
|---|---|---|---|---|
| **12:25–12:29**（本车道 §3 快照那一刻） | `#24` 权威（W24A 落地 `D-T5`） | `476994e35d31a7e1` | 4,197,376 | **已转绿 12 ／ 仍红 1** |
| **12:30**（**W25A 正在落地 `D-T5-R`**） | **新权威（重建中）** | **`7374308a00c55572`** | **4197888** | **已转绿 0 ／ 仍红 13** |

⇒ **`pc` 权威一被重建（mtime `2026-09-17 12:30:42`），那 12 份的"已转绿"在同一分钟内全部回落为"仍红"** ——
**这不是回归，这正是纪律 52 说的"按世代结算"**：`[在册红·已转绿]` **只在某个权威 sha 下成立**。

**切换后现场（`2026-09-17 12:32:07 +0800` 时刻，现场算）**：

| # | 路径 | 登记时副本 sha16 | 切换后实测副本 sha16 | 切换后判定 | 副本 mtime |
|---|---|---|---|---|---|
| 1 | `build/DirectWrite.Linux/FontEntryClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 2 | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 3 | `build/DirectWrite.Linux/WicClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 4 | `build/DirectWrite.Linux/WicWriteClosedLoop/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 5 | `build/DirectWrite.Linux/WiringSmoke/bin/Debug/PresentationCore.dll` | `23567d420f0dbbaa` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 6 | `build/DirectWrite.Linux/FallbackCriteria/bin/Debug/PresentationCore.dll` | `c0763fc10173e7ff` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 7 | `build/MilBridge/tests/CoverageProbe/bin/Debug/PresentationCore.dll` | `9adac6b8d8e285c3` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 8 | `build/MilBridge/tests/HbTextLineParity/bin/Debug/PresentationCore.dll` | `f4a454c8fe69cdfe` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:58 |
| 9 | `build/MilBridge/tests/TextLineProto/bin/Debug/PresentationCore.dll` | `f4a454c8fe69cdfe` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 10 | `build/PresentationFramework.Classic.Linux/bin/Debug/PresentationCore.dll` | `95a669cc510337d1` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 11 | `samples/HelloWpf/bin/Debug/net10.0/PresentationCore.dll` | `f31822ce4a3e510d` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:36:00 |
| 12 | `samples/WpfFeatureProbe/bin/Debug/net10.0/PresentationCore.dll` | `9adac6b8d8e285c3` | `476994e35d31a7e1` | **仍红** | 2026-09-17 10:35:59 |
| 13 | `tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll` | `16baacfccfcf1df0` | `16baacfccfcf1df0` | **仍红** | 2026-09-11 09:21:37 |

**关键数字（现场枚举，`build`/`tests`/`samples`/`src`/`tools` 五个扫描根）**：

- `PresentationCore.dll` 全仓 **34 份**：**3 份** = 新权威（`7374308a00c55572`）、**31 份** = `476994e35d31a7e1`（**尚未刷新**）。
- ⇒ **波尾 `sync-applocal-authority.sh --apply` 需要刷的份数 ≈ 31**（不是 12）
  —— 因为 §3 那 12 份是**上一代**的落后集合，**新一代的落后集合大得多**。
- **表 B**（`tools/GeometryOracle`）**照旧仍红**，`16baacfccfcf1df0` 未动 ⇒ **再次证明它与 `pc` 世代无关**。

**⚠️ 本节的读数不可用作"门禁读数"**：本车道**没有跑 `check-applocal-sync.sh`**（W25B 写域 + 正在被改）；
上表由 `show_registry()` 的**同一段读法**在**当前权威**上重放得出。
**⚠️ 本节的权威 sha 也只属于"12:30 那一刻"**：`pc` 在 `#25` 波内仍会被再次重建 ⇒ **引用前必须现场重算**。
