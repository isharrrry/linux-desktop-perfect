# 轮次摘要：2026-11-12T0927

## 结论

| 项 | 值 |
|---|---|
| 总分 | **47.0**（上轮 42.6；同口径复现 42.5） |
| 三层 | 完成度 **30.0** / 质量 **59.0** / 外壳 **52.0** |
| 有效权重 | 66.0（`test_strength`/`defect_density`/`fix_closure`/`gated_line_ratio` 为 N/A） |
| 触发 | R4（未发布，tag=0）、R6（完成度 50.0% − 闸门 0.0% > 30pp）、R8（有效权重 66 < 70） |
| 决策 | 先加固再推进（不变） |

kit 原始产物：`REPORT-linux-desktop-perfect.md`、`INDEX.md`、`inventory.json`、`metrics.json`、`scores.json`；本轮输入校准 `groups.json`（47 团）。

## 本轮变化（对照 2026-11-12T0919）

| 变更 | 影响指标 | 增量 | 证据 |
|---|---|---:|---|
| 仓库目录规整（`本体/`→`30-design/ontology/`、`sourcetree-shots/`→`50-verify/evidence/sourcetree/`、`samples/`→`50-verify/samples/`、`audit_out/`→`50-verify/audit/<时间戳>/`） | 无 | **0**（中间态实测 42.5 = 迁移前 42.5） | 8 条门禁/校验 rc=0；`degrade/audit.mjs` `verdict=PASS diff=0` |
| 新增 `.github/ISSUE_TEMPLATE/{bug,feature}.md` + 仓库根 `ISSUE_TEMPLATE.md` | `doc_completeness` 66.7→83.3、`contribution_path` 50→100 | **+4.5** | 42.5 → 47.0；外壳 42.0 → 52.0 |

## 未做（用户明确排除）

- 打首个版本 tag + `CHANGELOG.md` 三处一致：R4 仍触发，`release_ready` 仍 0.0。本轮**不动**发布链。

## 口径披露（本轮踩到）

1. **复现差 0.1**：原报告 42.6，同口径复现 42.5 —— `silence_days` 归一为 `1 - days/90`，权重 5，跨 1 天约 −0.08。
2. **`ISSUE_TEMPLATE` 只认仓库根**：kit 的 `doc_completeness` 用 `glob(repo/ISSUE_TEMPLATE*)`，不认 `.github/ISSUE_TEMPLATE/`；只有 `.github/` 版时贡献路径 +2.0 但文档不涨（实测 45.5）。补根 `ISSUE_TEMPLATE.md`（GitHub 亦支持单文件默认模板）后才到 47.0。
3. **`selftest.mjs` 不能并发跑**：固定夹具 `_selftest_*.md` 在仓根，两实例并发会互删（实测 37/38、33/38），串行稳定 38/38。
4. **`build_registry`/`gated_line_ratio` 分母含上游**：`projs`=263（其中 `40-build/upstream` 251 个）、`units.loc` 含上游行数，故「接 CI 拿 +5~8」在这套口径下需把 263 个 csproj 全写进 workflow，属分数工程，见 `/home/links-dev/netTest/GitProj/.sak/README.md`。
5. **`bus_factor` 是纯 git 作者统计**（`min(1, 近90天作者数/3)`）——文档化关键路径不涨分。

## 下一步

AIDash `devhost/linux/sourcetree` 剩余 5 条 pending：CI 门禁接入、47 团用例化、`native-shim`/`milcore` 团归属、拆 `PresentationFramework`、关键路径文档化。每批完成后按同一口径重跑并记录到本目录。
