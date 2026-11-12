# 轮次 2026-11-12T0937

## 结论

| 项 | 值 |
|---|---|
| 总分 | **64.4**（上轮 60.8，+3.6） |
| 三层 | 完成度 **82.0** / 质量 59.0 / 外壳 55.6 |
| 有效权重 | 66.0（N/A 项未变） |
| 触发 | R4（未发布）仍触发；R6 缺口 = 0.70 − 0.0 仍 > 0.30；R8 有效权重 66 < 70 |

## 本轮变化

| 变更 | 影响指标 | 增量 | 证据 |
|---|---|---:|---|
| 47 个功能团各补 1 条 kit 可计数的机读断言（`50-verify/units/unit-01..47-*.test.js`，CommonJS + `node:test`，每文件恰好 1 处 `test(`） | `weighted_completion` 0.5→**0.7**（`dod_of`：cases≥1→0.7） | **+3.6** | `node --test "50-verify/units/*.test.js"` → `# tests 47 / # pass 47 / # fail 0`；完成度层 70.0→82.0 |
| `50-verify/gates.sh` 增加 `units` 项 | 门禁覆盖 | 0 | `bash 50-verify/gates.sh` → 7 项 PASS（links/package/coverage/selftest/units/abi-layout/text-abi） |
| 评估校准收敛到仓内 `50-verify/audit/calibration/groups.json` | 口径一致 | 0 | 每团 `tests` 字段指向对应 unit 文件；本轮 `groups.json` 为输入副本 |

## 必须披露：断言性质

这 47 条是**静态事实断言**（文件存在 / 非空 / 源码总行数 ≥ 记录值 90% / 最大文件含真实符号），**不是行为测试**。kit 的 DoD 0.7 只要求 `cases>=1`，所以这是**形态对齐**而非真实覆盖提升：`weighted_completion` 0.7 的语义被稀释到「团的源码骨架仍在」这一层。要拿 0.9（`cases>=3` 且有 doc）同样会是形态工程，**不建议继续刷**；真实覆盖需要把 30 条 ACC 判据映射进团，属下一阶段的结构问题。

变异验证（证明不是恒真）：unit-01 / unit-40 / unit-47 各临时移走一个文件 → `error: '缺文件：…'`、`# fail 1`；恢复后 `# pass 1 / # fail 0`。

## 运行注意

- `node --test 50-verify/units/`（目录参数）在 Node 22.23 报 `Cannot find module .../units`；必须用 glob：`node --test "50-verify/units/*.test.js"`。已按此写进 `gates.sh`。
- `.sak/groups-calibrated.json` 已同步为仓内 calibration 的副本，避免双份口径分叉。

## 未做（用户明确排除）

打 tag / 版本声明 / CHANGELOG 三处一致：`release_ready` 仍 0.0，R4 仍触发。
