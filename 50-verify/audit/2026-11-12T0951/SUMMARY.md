# 轮次 2026-11-12T0951

## 结论

| 项 | 值 |
|---|---|
| 总分 | **64.4**（与上轮 0937 持平，0 增量） |
| 三层 | 完成度 82.0 / 质量 59.0 / 外壳 55.6 |
| 有效权重 | 66.0 |
| 触发 | R4（未发布）仍触发；R8 有效权重 66 < 70 |

## 本轮变化（0 分增量，口径与 CI 加固）

| 变更 | 影响 | 证据 |
|---|---|---|
| 功能团 47→**54**：`native-shim` 拆 2（42 文件/35315 行）、`milcore` 拆 2（71/25755）、`PresentationFramework` 拆 4（39/56758，逐行相等） | `weighted_completion` 保持 0.7 | 581 entries / 581 unique / 0 重复；覆盖 42/42 与 71/71 |
| `units` 54 个（删 17 孤儿、新增 24） | 0 | `node --test 50-verify/units/*.test.js` → 54/54 |
| GitHub CI 转绿 | 0（不参与评分） | run [`43137279585`](https://github.com/isharrrry/linux-desktop-perfect/actions/runs/43137279585) success（head `f770cef`） |

## CI 异常的两处真根因（本地假绿的来源）

1. `70-operate/已知坑清单.md:4621` 的链接指向 `.gitignore` 忽略的 `40-build/upstream/wpf-10.0.11/` —— 本地有该快照所以 `check-links` 绿，干净 clone 断链。已改为行内代码。
2. `selftest.mjs` 3.8 的契约生成物依赖本机专属输入（上游快照 `40-build/upstream/wpf/src`、NuGet `FrameworkList.xml`），干净环境必然 `rc=1`。已改为「缺输入 SKIP、真实不同步仍 FAIL」，并登记 **K-1496**。
3. 附带：`node --test 50-verify/units/*.test.js` 的 glob 交给 shell 展开（Node 20 不认 `--test` glob）。

复现方式：`git clone` 到干净目录 + node 20.20.2 跑 `bash 50-verify/gates.sh` —— 修复前 `selftest 35/38`，修复后 `ALL GATES PASS`；本地（有快照）仍 `38/38`。

## 未做（用户明确排除）

打 tag / 版本声明 / 三处一致：`release_ready` 仍 0.0，R4 仍触发。
