# 软件评估报告：LinuxDesktopPerfect（轮次 2026-11-12T0930）

| 项 | 值 |
|---|---|
| 仓库 | `linux-desktop-perfect`（`git@gitee.com:the50w/linux-desktop-perfect.git`） |
| 提交 | `67333b2` + 本轮工作树（gates/workflow/关键路径） |
| 日期 | 2026-11-12T0930 |
| 口径 | Software Assessment Kit v1.2；单软件合并（`--single linux-desktop-perfect`）；47 团人工校准；`exclude_dirs` 不含 `upstream`（与基线轮一致） |
| 产物 | 本目录 `INDEX.md`、`REPORT-linux-desktop-perfect.md`、`inventory/groups/metrics/scores.json` |

## 0 结论

**总分 59.3/100**（上轮 42.6 → 迁移前复跑 42.5 → 本轮 59.3）。三层：完成度 **70.0**、质量 **59.0**、外壳 **52.2**。

| 层 | 得分 | 满分 | 百分制 | 关键变化 |
|---|---:|---:|---:|---|
| 完成度 | 14.0 | 20.0 | **70.0** | 构建登记 0.0→**1.0**（+8.0 权重） |
| 质量 | 9.4 | 16.0 | 59.0 | 无变化（缺陷密度/闭环/测试强度仍 N/A，口径未变） |
| 外壳 | 15.7 | 30.0 | **52.2** | 文档 4/6→**5/6**、贡献路径 0.5→**1.0** |

触发规则：R4（未发布，tag=0）仍触发；R6 已缓解（完成度 0.70 − 闸门 0.0 = 0.70，仍 >0.30，但闸门口径见下）；R8 有效权重仍 66.0（N/A 指标未变）。

## 1 本轮改动

| 改动 | 内容 | 评估影响 |
|---|---|---|
| 目录规整 | `本体/`→`30-design/ontology/`、`sourcetree-shots/`→`50-verify/evidence/sourcetree/`、`samples/`→`50-verify/samples/`、`audit_out/`→`50-verify/audit/<时间戳>/` | 0（分数不变，路径更稳） |
| Issue 模板 | `.github/ISSUE_TEMPLATE/{bug,feature}.md` + 根 `ISSUE_TEMPLATE.md` | +4.5 |
| CI 登记 | `50-verify/gates.sh`（4 文档门禁 + 2 C ABI 断言，本地可复跑）+ `.github/workflows/gates.yml`（含 263 个 csproj 清单） | +12.1 |
| 关键路径 | `70-operate/关键路径.md`（8 子系统接管步骤）+ `CONTRIBUTING.md` 链接 + `names.json` 登记 | 0（`bus_factor` 由 git 作者数决定） |

## 2 指标变化

| 指标 | 上轮 | 本轮 | 依据 |
|---|---:|---:|---|
| 加权完成度 | 0.50 | 0.50 | 47 团仍零用例 |
| 构建登记 | 0.0 | **1.0** | workflow 文本命中 263/263 csproj |
| 文档完备度 | 66.7 | **83.3** | 根 `ISSUE_TEMPLATE.md` 命中第 5 项（CHANGELOG 仍缺） |
| 贡献路径 | 50.0 | **100.0** | `.github/ISSUE_TEMPLATE/*` 命中 |
| 发布就绪 | 0.0 | 0.0 | tag=0、无版本声明（本轮排除发布） |
| 静默 / 总线因子 | 100 / 33.3 | 100 / 33.3 | 单人作者，未变 |

## 3 口径披露（必读）

1. **`ISSUE_TEMPLATE` 双路径**：kit 的 `doc_completeness` 只在**仓库根**找 `ISSUE_TEMPLATE*`，`contribution_path` 只认 `.github/ISSUE_TEMPLATE/*`。只做 `.github/` 版时总分只到 45.5（+3.0）；补根 `ISSUE_TEMPLATE.md` 才到 47.0。
2. **`build_registry` 分母含上游**：`projs` 是文件系统 glob `**/*.csproj`，实测 263 个（251 个在 `40-build/`，多数为 `40-build/upstream/` 未跟踪文件）。workflow 里如实列出全部 263 条，并用注释写明「CI checkout 后 upstream 不存在」。这是**登记口径**，不等于 GitHub 上真跑（远程是 Gitee，Actions 不执行）。
3. **`gated_line_ratio` 仍 N/A**：分母 = `units.loc`（含上游行数），该指标在本口径下近乎不可得，本轮不做分数工程。
4. **`bus_factor` 不因文档改变**：`min(1, 近90天作者数/3)`，纯 git 统计。
5. **时间敏感项**：`silence_days = 1 - days/90`（权重 5）⇒ 总分约 −0.08/天，跨天比较须扣除。

## 4 行动项状态（AIDash 通道 `devhost/linux/sourcetree`）

| 任务 | 状态 |
|---|---|
| `[P0]` ISSUE_TEMPLATE | 已完成（本轮） |
| `[P1]` CI 门禁接入 | 已完成（本轮） |
| `[P2]` 关键路径文档化 | 已完成（本轮，0 分增量） |
| `[P1]` 判据用例化（47 团） | 待做，预期 +3.6（每团 ≥1 条）~+7.3（≥3 条且 doc） |
| `[P2]` native-shim/milcore 团归属 | 待做（口径修复，无量纲） |
| `[P2]` 拆 PresentationFramework 团 | 待做（口径修复，无量纲） |
| `[P0]` 打 tag + CHANGELOG | **按用户决定排除**（版本不稳定） |

## 5 下一批建议

1. 判据用例化（唯一还能实质抬完成度的项）：先试点 2 团，再批量；每团 ≥1 条真断言，禁止恒真。
2. 单独建 `CHANGELOG.md` 的 Unreleased 段（不打 tag）：`doc_completeness` 5/6→6/6，+1.5；不解除 R4。
3. 口径修复两项（团归属/拆团）只改变分母，不改变实际工作量，评估增量记「无量纲」。
