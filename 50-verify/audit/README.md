# 评估轮次索引

每次软件评估的产物落 `50-verify/audit/<YYYY-MM-DDTHHMM>/`（含评估报告、REPORT 工具报告与 `inventory`/`groups`/`metrics`/`scores` 四份 JSON）。

| 轮次时间 | 总分 | 完成度/质量/外壳 | 报告路径 | 备注 |
|---|---:|---|---|---|
| 2026-11-12T0919 | 42.6 | 30.0 / 59.0 / 42.2 | [ASSESSMENT-linux-desktop-perfect.md](2026-11-12T0919/ASSESSMENT-linux-desktop-perfect.md) | 迁移前基线；本目录原为仓根 `audit_out/`；未发布（tag=0），触发 R4/R6/R8 |
| 2026-11-12T0927 | 47.0 | 30.0 / 59.0 / 52.0 | [SUMMARY.md](2026-11-12T0927/SUMMARY.md) | 目录规整（0 增量）+ Issue 模板（`doc_completeness` 4/6→5/6、`contribution_path` 0.5→1.0，+4.5）；未发布 |
| 2026-11-12T0930 | **59.3** | 70.0 / 59.0 / 52.2 | [ASSESSMENT-linux-desktop-perfect.md](2026-11-12T0930/ASSESSMENT-linux-desktop-perfect.md) | 目录规整 + Issue 模板 + CI 门禁登记 + 关键路径文档；构建登记 0→1.0、文档 5/6、贡献 1.0；R4 仍触发 |
| 2026-11-12T0935 | **60.8** | 70.0 / 59.0 / 55.6 | [SUMMARY.md](2026-11-12T0935/SUMMARY.md) | 新增 `CHANGELOG.md`（未发布段）：`doc_completeness` 5/6→6/6，+1.5；R4 仍触发 |

## 历史与证据中的旧路径

本仓 2026-11-12 做过一次目录规整，以下位置**有意保留**旧路径字符串，属历史记录或冻结证据，不随目录迁移改写：

| 位置 | 旧路径 | 为何不改 |
|---|---|---|
| `50-verify/读数/**` | `samples/` | 判据读数只追加、不回溯改写（08 §3.6） |
| `70-operate/已知坑清单.md` 中少量条目 | 旧仓绝对路径 `/home/links-dev/netTest/GitProj/sourcetree-shots/...`、`/home/links-dev/netTest/wpf-linux-20260906/...` | 记录的是当时的仓外目录与上一代工程，改写即伪造 |
| `40-build/wpf-port/**` 历史报告与 `src/Linux/samples/**` | `samples/HelloWpf`、`samples/HelloMil`、`samples/ThirdPartyMini`、`samples/WpfFeatureProbe` | 上游快照与旧仓样本名，本仓无对应目录 |
| `90-archive/`、`10-input/` | 任意 | 只读归档与原始输入 |

当前有效路径：签入样本在 `50-verify/samples/`，SourceTree 证据在 `50-verify/evidence/sourcetree/`，运行时本体在 `30-design/ontology/`。
