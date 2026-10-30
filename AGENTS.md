# AGENTS — LinuxDesktopPerfect 维护约束

> 任何 Agent 在本仓开工前必读。人类维护者看 [CONTRIBUTING.md](CONTRIBUTING.md)。
> 本文件的规则不是建议：违反即视为交付不合格。

## 1. 先读什么

| 顺序 | 文件 | 为什么 |
|---|---|---|
| 1 | [README.md](README.md) | 项目定位、范围、非目标 |
| 2 | [《术语表》](00-admin/术语表.md) | 首选名与异名；正文只用首选名 |
| 3 | [《引用与名称规范》](00-admin/引用与名称规范.md) | 《》/# 标记、链接、行号、四张表示 |
| 4 | [《决策记录》](00-admin/决策记录.md) | 已裁决与待裁决；新 DQ 未关闭前不得开工 |
| 5 | [《需求与验收判据文档》](20-spec/需求与验收判据文档.md) | REQ/ACC/NF/XIF 的唯一事实源 |
| 6 | [《概要设计》](30-design/概要设计.md) | ADR/MOD/SYS/R 的唯一事实源 |

## 2. 硬约束

1. **编号只在定义方定义**：REQ/ACC/NF/XIF/DQ → `20-spec`；ADR/MOD/SYS/R → `30-design/概要设计.md`。其他文件只引用。
2. **名称只用首选名**：见 [《术语表》](00-admin/术语表.md)；发现异名先改文档，再改 [#名称清单](30-design/model/names.json)。
3. **引用必须带标记与链接**：可阅读文档用《》并链到文件；工具/数据/目录用 #名并链到文件；裸 `.md` 路径与裸行号 `L####` 一律禁止。
4. **不写未验证的结论**：状态写「待实现/待裁决/NOINFO」，不写「已支持/已修复」，除非有判据读数。
5. **诚实降级**：不可实现路径必须返回明确错误码并登记到 [《已知坑与降级清单》](70-operate/已知坑清单.md)；禁止静默返回空或假成功。
6. **不改上游与归档**：`40-build/upstream/` 只读；`90-archive/` 只读；`10-input/` 只追加新文件，不改旧记录。
7. **补丁只写在 applier 里**：派生树是产物，手改会被下次生成覆盖。
8. **范围不越界**：不做二进制兼容；不承诺 WinForms 全控件/设计器/ActiveX；DRM 按 D-001 报错降级。

## 3. 改完必跑

```bash
node 30-design/checks/check-links.mjs
node 30-design/checks/check-package.mjs
node 30-design/checks/selftest.mjs        # 改规则/改检查器后必跑
```

三条 `rc=0` 才算文档改动完成。若输出了新的 `ERROR`，先修文档，不要改脚本放宽规则。

## 4. 允许与禁止

| 允许 | 禁止 |
|---|---|
| 新增 REQ/ACC 并在需求文档定义 | 在概设或 README 里新造 REQ/ACC 编号 |
| 新增 ADR/MOD 并在概设定义 | 在实现代码注释里定义新 ADR 编号 |
| 修正名称、链接、章节引用 | 改 `90-archive/`、`10-input/` 旧记录 |
| 新增降级项并登记 | 把失败写成「降级」而不登记 |
| 补 `40-build/` 实现与 `50-verify/` 判据 | 在 `40-build/` 里手改上游快照 |

## 5. 目录职责

| 目录 | 职责 | 谁能写 |
|---|---|---|
| `00-admin/` | 术语、规范、决策台账 | 人 + Agent（按规范） |
| `10-input/` | 原始输入 | 只追加 |
| `20-spec/` | 需求与验收 | 人 + Agent（需同步 ACC） |
| `30-design/` | 概要设计与设计包 | 人 + Agent（需跑 checks） |
| `40-build/` | 实现与派生 | Agent（applier 优先） |
| `50-verify/` | 判据与记录 | Agent |
| `60-deliver/`、`70-operate/` | 交付与运维 | 人 + Agent |
| `90-archive/` | 归档 | 只读 |
