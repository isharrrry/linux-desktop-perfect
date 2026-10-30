# 参与 LinuxDesktopPerfect

本仓是 WindowsDesktop 在 Linux 上的完整替代运行时（WPF 全量、WinForms 到兼容层）。主干只保留最小共识；范围与非目标见 [README.md](README.md)。

## 先读

1. [README.md](README.md)
2. [《术语表》](00-admin/术语表.md)
3. [《引用与名称规范》](00-admin/引用与名称规范.md)
4. [《决策记录》](00-admin/决策记录.md)
5. [《需求与验收判据文档》](20-spec/需求与验收判据文档.md)
6. [《概要设计》](30-design/概要设计.md)

Agent 维护者另读 [AGENTS.md](AGENTS.md)；那是硬约束，本文件是它的简版。

## 最小约定

- **编号要真**：新增 REQ/ACC 在 `20-spec` 定义；新增 ADR/MOD 在概设定义；其他文件只引用。
- **名称要统一**：正文只用 [《术语表》](00-admin/术语表.md) 的首选名。
- **引用要能点**：文档用《》、工具/数据/目录用 #，都带相对链接；不写裸路径与裸行号。
- **结论要能复算**：未验证的写「待实现/NOINFO」，不写「已支持」。
- **降级要登记**：见 [《已知坑与降级清单》](70-operate/已知坑清单.md)，禁止静默失败。

## 改完必跑

```bash
node 30-design/checks/check-links.mjs
node 30-design/checks/check-package.mjs
```

两条 `rc=0` 才算完成。

## 提交粒度

一次提交只做一件事：要么改需求/判据，要么改设计，要么改实现；混在一起评审无法判断哪边错了。
