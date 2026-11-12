# 评估轮次索引

每次软件评估的产物落 `50-verify/audit/<YYYY-MM-DDTHHMM>/`（含评估报告、REPORT 工具报告与 `inventory`/`groups`/`metrics`/`scores` 四份 JSON）。

| 轮次时间 | 总分 | 完成度/质量/外壳 | 报告路径 | 备注 |
|---|---:|---|---|---|
| 2026-11-12T0919 | 42.6 | 30.0 / 59.0 / 42.2 | [ASSESSMENT-linux-desktop-perfect.md](2026-11-12T0919/ASSESSMENT-linux-desktop-perfect.md) | 迁移前基线；本目录原为仓根 `audit_out/`；未发布（tag=0），触发 R4/R6/R8 |
| 2026-11-12T0927 | 47.0 | 30.0 / 59.0 / 52.0 | [SUMMARY.md](2026-11-12T0927/SUMMARY.md) | 目录规整（0 增量）+ Issue 模板（`doc_completeness` 4/6→5/6、`contribution_path` 0.5→1.0，+4.5）；未发布 |
| 2026-11-12T0930 | **59.3** | 70.0 / 59.0 / 52.2 | [ASSESSMENT-linux-desktop-perfect.md](2026-11-12T0930/ASSESSMENT-linux-desktop-perfect.md) | 目录规整 + Issue 模板 + CI 门禁登记 + 关键路径文档；构建登记 0→1.0、文档 5/6、贡献 1.0；R4 仍触发 |
| 2026-11-12T0935 | **60.8** | 70.0 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T0935/SUMMARY.md) | 新增 `CHANGELOG.md`（未发布段）：`doc_completeness` 5/6→6/6，+1.5；R4 仍触发 |
| 2026-11-12T0937 | **64.4** | 82.0 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T0937/SUMMARY.md) | 47 团各 1 条机读断言：`weighted_completion` 0.5→0.7，+3.6（静态事实断言，形态对齐，见报告披露）；`gates.sh` 增 `units` 项 |
| 2026-11-12T0951 | **64.4** | 82.0 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T0951/SUMMARY.md) | 0 增量：团 47→54（native-shim/milcore/PF 拆团）+ GitHub CI 转绿（run 43137279585）；两处根因与 K-1496 |
| 2026-11-12T1024 | **64.4** | 82.0 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T1024/SUMMARY.md) | **新口径基线**（`--exclude-dir upstream`）：`units.loc` 3,807,609→255,417、`projs` 263→64；分数与旧口径一致（见「口径变更」节） |
| 2026-11-12T1028 | **68.9** | 87.6 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T1028/SUMMARY.md) | 覆盖率闸门登记（`coverage.sh` + 23 个包前缀）：`gated_line_ratio` 0→98.8，+4.5；**是「被校验包行数占比」不是行覆盖率**（见报告披露） |
| 2026-11-12T1037 | **68.9** | 87.6 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T1037/SUMMARY.md) | 真实覆盖：54 团 → 31 ACC 锚定（108/108 断言，变异 2 次红→绿）；`cases=2` 仍 0.7 档，0 增量 |

## 口径变更 2026-11-12：排除 `40-build/upstream`

**变更理由**：旧口径把上游快照算进本软件 —— `units.loc` 3,807,609 行中 3.55M 来自 `40-build/upstream/`；`projs` 263 个 csproj 中 199 个是该目录下游未跟踪文件。分母被污染，指标数值不可信。新口径统一加 `--exclude-dir upstream`（worktree 重算时上游本就未入库，两者叠加等价）。

**六个历史轮次「旧口径 → 新口径」对照**（逐 commit 建 worktree 重算，groups 用该轮配置）：

| 轮次 | commit | 旧口径分 | 新口径分 | 三层（新） | 差异原因 |
|---|---|---:|---:|---|---|
| 2026-11-12T0919 | `9987f7d` | 42.6 | **42.6** | 30.0 / 59.0 / 42.2 | 一致 |
| 2026-11-12T0927 | `67333b2` | 47.0 | **47.1** | 30.0 / 59.0 / 52.2 | +0.1 仅因 `silence_days` 时间敏感（98.9→100.0），非口径影响 |
| 2026-11-12T0930 | `d4acedb` | 59.3 | **59.3** | 70.0 / 59.0 / 52.2 | 一致 |
| 2026-11-12T0935 | `a37be4a` | 60.8 | **60.8** | 70.0 / 59.0 / 55.6 | 一致 |
| 2026-11-12T0937 | `ce82a58` | 64.4 | **64.4** | 82.0 / 59.0 / 55.6 | 一致 |
| 2026-11-12T0951 | `f770cef` | 64.4 | **64.4** | 82.0 / 59.0 / 55.6 | 一致（须用该 commit 内 `calibration/groups.json`，54 团） |

**为什么分数不变**：`weighted_completion` 的权重来自各团 `files` 行数（团配置本就不含 upstream）；`build_registry_ratio` 旧口径 263/263、新口径 64/64，两版都是 1.0；`gated_line_ratio` 两口径都是 `null`（`gates.sh` 不含 coverage 关键字与 `'前缀*'` 包过滤）；其余 N/A 指标与 upstream 无关。即：污染的是分母真实性，不是分数。

> **历史轮次目录内的 `scores.json`/`metrics.json` 仍是旧口径，不可与新轮次直接比较数值口径。** 需要跨轮比较时，以上表新口径列为准。

> **数据勘误（已修正）**：`2026-11-12T0951/groups.json` 原存 47 团副本，与该轮 SUMMARY 的 54 团口径不符（属错副本）。按 47 团重算得 63.6（17 团 `cases=0`、40363 行），是**输入错配的假差异**；用 `git show f770cef:50-verify/audit/calibration/groups.json`（54 团）重算得 64.4。本轮重算期间该文件已被并行修正为 54 团（与 commit 内 calibration 一致），故上表 0951 行按 54 团口径给出。

## 历史与证据中的旧路径

本仓 2026-11-12 做过一次目录规整，以下位置**有意保留**旧路径字符串，属历史记录或冻结证据，不随目录迁移改写：

| 位置 | 旧路径 | 为何不改 |
|---|---|---|
| `50-verify/读数/**` | `samples/` | 判据读数只追加、不回溯改写（08 §3.6） |
| `70-operate/已知坑清单.md` 中少量条目 | 旧仓绝对路径 `/home/links-dev/netTest/GitProj/sourcetree-shots/...`、`/home/links-dev/netTest/wpf-linux-20260906/...` | 记录的是当时的仓外目录与上一代工程，改写即伪造 |
| `40-build/wpf-port/**` 历史报告与 `src/Linux/samples/**` | `samples/HelloWpf`、`samples/HelloMil`、`samples/ThirdPartyMini`、`samples/WpfFeatureProbe` | 上游快照与旧仓样本名，本仓无对应目录 |
| `90-archive/`、`10-input/` | 任意 | 只读归档与原始输入 |

当前有效路径：签入样本在 `50-verify/samples/`，SourceTree 证据在 `50-verify/evidence/sourcetree/`，运行时本体在 `30-design/ontology/`。
