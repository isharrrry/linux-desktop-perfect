# 轮次 2026-11-12T1024（新口径基线）

## 结论

| 项 | 值 |
|---|---|
| 总分 | **64.4** |
| 三层 | 完成度 **82.0** / 质量 59.0 / 外壳 55.6 |
| 有效权重 | 66.0（`defect_density`/`test_strength`/`fix_closure_rate`/`gated_line_ratio` 仍 N/A） |
| 口径 | **只统计仓内本仓源码**：`--exclude-dir upstream`；54 团校准；单软件合并 |
| 触发 | R4（未发布，tag=0）；R6 缺口 0.70−0.0 > 0.30；R8 有效权重 66 < 70 |

## 口径变更（本轮唯一变量）

| 量 | 旧口径 | 新口径 | 说明 |
|---|---:|---:|---|
| `units.loc`（完成度/gated 分母） | 3,807,609 | **255,417** | 旧值含 `40-build/upstream/` 3.55M 行上游快照 |
| `projs`（构建登记分母） | 263 | **64** | 旧值含 199 个 `40-build/upstream/**` 未跟踪 csproj |
| 总分 | 64.4 | **64.4** | 无差异，原因见下 |

**为什么分数不变（用数字解释，不是「大概」）**：

- `weighted_completion` 用各团 `files` 的行数加权，团配置本来就不含 upstream ⇒ 两口径同为 0.7。
- `build_registry_ratio = ci_ref / projs`：旧口径 workflow 文本含 263 个 csproj 路径 → 263/263 = 1.0；新口径同一份 workflow 仍覆盖全部 64 个 → 64/64 = 1.0。**两版都是满分**，分母修正不改变结果。
- `gated_line_ratio` 两口径均为 `null`：`50-verify/gates.sh` 不叫 coverage、也没有 `'前缀*'` 包过滤，kit 抓不到 gated 脚本。
- 其余指标（缺陷密度/闭环/测试强度）本就因 `src_lines=0` 为 N/A，与 upstream 无关。

**结论**：旧口径的 3.6M 行上游污染了分母的真实性，但**没有虚增也没有虚减历史分数**；修正后指标数值可信，历史分数无需修正。

## 本轮产物

`INDEX.md`、`REPORT-linux-desktop-perfect.md`、`inventory.json`、`metrics.json`、`scores.json`、`groups.json`（54 团校准副本）、本 `SUMMARY.md`。

## 重算方法（可复跑）

```bash
git worktree add --detach /tmp/re-<ts> <commit>
bash /home/links-dev/netTest/GitProj/.sak/run-eval.sh /tmp/re-<ts> /tmp/reout-<ts> <该轮 groups 绝对路径>
git worktree remove --force /tmp/re-<ts>
```

worktree 里没有未跟踪的 upstream 文件，`--exclude-dir upstream` 再兜一层；两者叠加等价于「只统计仓内源码」。

## 数据勘误（已修正）

`50-verify/audit/2026-11-12T0951/groups.json` 原存 **47 团**副本，而该轮 SUMMARY 记录的是 **54 团**口径（`native-shim` 拆 2、`milcore` 拆 2、`PresentationFramework` 拆 4）。按目录内 47 团重算会得到 63.6（17 团 `cases=0`，40363 行、22.4%），属**输入错配的假差异**；改用 `git show f770cef:50-verify/audit/calibration/groups.json`（54 团）重算得 **64.4**，与旧口径一致。重算期间该文件已被并行修正为 54 团（与 commit 内 calibration 一致），故本轮对照表 0951 行按 54 团口径给出。
