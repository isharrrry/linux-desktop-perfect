# 更新记录（CHANGELOG）

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；本仓**尚未发布版本**（tag=0、无版本声明），因此只有「未发布」段。
每条记录必须能追到判据或提交；未验证的不写「已支持」。

## 未发布（Unreleased）

### 新增

- `50-verify/gates.sh`：可在任何 Linux + node20 + gcc 环境复跑的门禁集合（4 条文档门禁 + 2 个 C ABI 断言），不依赖本机运行时、显示器与截图工具。
- `.github/workflows/gates.yml`：调用 `gates.sh` 的 workflow，并登记全仓 csproj 清单（供 Software Assessment Kit 的 `build_registry` 统计；远程为 Gitee，Actions 不执行，属登记 + 本地可复跑）。
- `.github/ISSUE_TEMPLATE/{bug,feature}.md` 与根 `ISSUE_TEMPLATE.md`：问题与需求模板。
- `70-operate/关键路径.md`：8 个子系统的入口、最小验证命令、常见坑编号与接管步骤（不改变 `bus_factor` 读数）。
- `50-verify/audit/<YYYY-MM-DDTHHMM>/`：每轮软件评估产物（报告 + `inventory/groups/metrics/scores`），索引见 `50-verify/audit/README.md`。

### 变更

- 目录规整（不改变软件内容）：`本体/` → `30-design/ontology/`；`sourcetree-shots/` → `50-verify/evidence/sourcetree/`；`samples/` → `50-verify/samples/`；`audit_out/` → `50-verify/audit/`。
- `AGENTS.md` §3/§5、`README.md`、`CONTRIBUTING.md`、`30-design/model/{model,names}.json`、`.gitignore` 同步新路径。
- `CONTRIBUTING.md` 新增「关键路径与接管」一节。

### 说明

- 历史缺陷条目与 `50-verify/读数/**` 中保留旧路径字符串（历史记录与冻结证据，不回溯改写）；说明见 `50-verify/audit/README.md`。
- 版本发布（tag + 版本声明 + 本文件顶层条目三处一致）**尚未进行**，当前明确不发布。
