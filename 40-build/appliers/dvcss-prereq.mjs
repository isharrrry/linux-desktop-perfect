#!/usr/bin/env node
// Dvcss 前置链（D 链）：让 `SourceTree.Dvcss.Git::GetHandler` 非 null 的**运行时前置**
//
// 背景（三条 K 条目串起来）：
//   · K-1413：`GetHandler` 首判 `CurrentInstallationConfiguration` 为 null 就 `ret null`
//     ⇒ 任何 VCS 操作都不执行（数据层空、UI「没什么可提交的」）；
//   · K-1415：该配置要由探针反射注入（3 参 `DvcsInstallationConfiguration`），
//     且 `get_ExePath` 硬编码 `ldstr "cmd\git.exe"` ⇒ 必须在 `<home>/` 下造一个
//     文件名**字面含反斜杠**的 `cmd\git.exe`；
//   · K-1441：把该文件做成「裸符号链接」时，`git` 毫秒级退出与 .NET 异步读输出形成竞态，
//     抛 `InvalidOperationException: Cannot process request because the process has exited`
//     （strace 证明子进程其实是 `exit_group(0)` 正常退出）。改成「延迟 0.15s 再退出」的
//     wrapper 后，该报错 6 次 → 0 次、自动刷新 2/2 成功。
//
// 职责（三件事）：
//   · wrapper：造 `<home>/cmd\git.exe` = `/usr/bin/git "$@"` + `sleep 0.15` + `exit $rc`（幂等）
//   · 校验：`--check` 校验 wrapper 存在/可执行/内容含 `sleep 0.15`
//   · 说明：Dvcss 配置本身**无法**在此脚本里造（需要运行时反射注入，见 K-1415 的 GvHelper 探针），
//           脚本会打印这条前置与可复跑路径
//
// 为什么独立脚本、不接进 `apply.mjs`：
//   与 B/C 链同理 —— 它改的是 `$HOME` 外的运行时布局（默认 `/tmp/fakegit`），
//   且必须配合探针注入才生效，属「显式启动前置」而非「派生树重建」。
//
// 用法：
//   node 40-build/appliers/dvcss-prereq.mjs                     # 造/修正 wrapper（幂等）
//   node 40-build/appliers/dvcss-prereq.mjs --check             # 只检查（不写盘）
//   node 40-build/appliers/dvcss-prereq.mjs --status            # 打印 wrapper 现状 + Dvcss 前置提示
//   node 40-build/appliers/dvcss-prereq.mjs --home /tmp/fakegit2
//   node 40-build/appliers/dvcss-prereq.mjs --remove            # 删除 wrapper（回退到「无 cmd\git.exe」）
//
// 环境：
//   GV_HOME   默认 `/tmp/fakegit`（与 K-1415 探针用的 `GV_HOME` 同名，便于对齐）
//
// 纪律：
//   · 幂等：内容完全一致 ⇒ 不写盘、不重复 chmod；
//   · 任一步失败透传非 0 退出码；
//   · `--check` 不写盘。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const argv = process.argv.slice(2);
const has = (f) => argv.includes(f);
const argOf = (f) => {
  const i = argv.indexOf(f);
  return i >= 0 ? argv[i + 1] : null;
};

const HOME_DIR = argOf('--home') || process.env.GV_HOME || '/tmp/fakegit';
// 注意：文件名是字面 `cmd\git.exe`（单个文件、名字里含反斜杠），不是 `cmd/git.exe` 目录结构
const WRAPPER = path.join(HOME_DIR, 'cmd\\git.exe');
const WRAPPER_DIR = HOME_DIR;

const EXPECT_SLEEP = 'sleep 0.15';
const WRAPPER_BODY = `#!/bin/sh
# Dvcss 前置 wrapper（D 链，K-1441）
# 为什么要有它：SourceTree 的 get_ExePath 硬编码 "cmd\\git.exe"（K-1415）；
# 而裸符号链接会让 git 毫秒级退出，与 .NET 异步读输出形成竞态（K-1441）。
# 延迟 0.15s 再退出即可把 "Cannot process request because the process has exited" 降到 0。
/usr/bin/git "$@"
rc=$?
sleep 0.15
exit $rc
`;

const CHECK_ONLY = has('--check');
const STATUS = has('--status');
const REMOVE = has('--remove');

function readIfExists(p) {
  try {
    return fs.readFileSync(p, 'utf8');
  } catch {
    return null;
  }
}

function statSafe(p) {
  try {
    return fs.lstatSync(p);
  } catch {
    return null;
  }
}

function isExec(p) {
  try {
    fs.accessSync(p, fs.constants.X_OK);
    return true;
  } catch {
    return false;
  }
}

function report(rc, lines) {
  for (const l of lines) console.log(l);
  process.exit(rc);
}

// ── status ────────────────────────────────────────────────────────────────
if (STATUS) {
  const st = statSafe(WRAPPER);
  const body = readIfExists(WRAPPER);
  const lines = [
    `[dp] home    : ${HOME_DIR}`,
    `[dp] wrapper : ${WRAPPER}`,
    st
      ? `[dp] exists  : yes  type=${st.isSymbolicLink() ? 'symlink' : st.isFile() ? 'file' : 'other'}  mode=${(st.mode & 0o777).toString(8)}  size=${st.size}`
      : '[dp] exists  : no',
    st && st.isSymbolicLink() ? `[dp] 注意    : 仍是符号链接（应换成 wrapper 脚本，K-1441）` : '',
    body && body.includes(EXPECT_SLEEP) ? `[dp] sleep   : 含 "${EXPECT_SLEEP}" ✓` : body ? `[dp] sleep   : 不含 "${EXPECT_SLEEP}" ✗` : '',
    '',
    '[dp] Dvcss 配置本身**不能**由本脚本造：`GetHandler` 需要运行时反射注入 3 参',
    '     `DvcsInstallationConfiguration`（K-1415）。探针路径：`/tmp/gv-probe/` + `/tmp/gv-helper/`，',
    `     运行环境变量 \`GV_HOME=${HOME_DIR}\`；判据：启动后 \`GetHandler.notnull=1\`。`,
  ].filter(Boolean);
  report(0, lines);
}

// ── remove ────────────────────────────────────────────────────────────────
if (REMOVE) {
  if (!statSafe(WRAPPER)) report(0, [`[dp] nothing to remove: ${WRAPPER}`]);
  try {
    fs.unlinkSync(WRAPPER);
    report(0, [`[dp] removed: ${WRAPPER}`]);
  } catch (e) {
    report(2, [`[dp] 删除失败: ${e.message}`]);
  }
}

// ── check / apply ─────────────────────────────────────────────────────────
const st = statSafe(WRAPPER);
const body = readIfExists(WRAPPER);

const problems = [];
if (!st) problems.push('wrapper 不存在');
else {
  if (st.isSymbolicLink()) problems.push('wrapper 是符号链接（应为脚本文件，K-1441）');
  if (st.isFile() && !isExec(WRAPPER)) problems.push('wrapper 不可执行');
  if (body === null || !body.includes(EXPECT_SLEEP)) problems.push(`wrapper 内容不含 "${EXPECT_SLEEP}"`);
}

if (CHECK_ONLY) {
  if (problems.length === 0) report(0, [`[dp] CHECK OK: ${WRAPPER}（可执行、含 ${EXPECT_SLEEP}）`]);
  report(1, [`[dp] CHECK FAIL:`, ...problems.map((p) => `[dp]   - ${p}`)]);
}

// apply：幂等写盘
const exists = st !== null;
const identical = exists && body === WRAPPER_BODY;
if (identical && isExec(WRAPPER)) {
  report(0, [
    `[dp] 幂等：wrapper 已存在且内容一致，跳过（${WRAPPER}）`,
    `[dp] 提示：Dvcss 配置仍需探针注入（K-1415），GV_HOME=${HOME_DIR}`,
  ]);
}

try {
  fs.mkdirSync(WRAPPER_DIR, { recursive: true });
  if (exists && st.isSymbolicLink()) fs.unlinkSync(WRAPPER);
  fs.writeFileSync(WRAPPER, WRAPPER_BODY, 'utf8');
  fs.chmodSync(WRAPPER, 0o755);
} catch (e) {
  report(3, [`[dp] 写 wrapper 失败: ${e.message}`]);
}

const after = statSafe(WRAPPER);
report(0, [
  `[dp] ${exists ? '已修正' : '已创建'} wrapper: ${WRAPPER}`,
  `[dp] mode=${(after.mode & 0o777).toString(8)} size=${after.size} 含 "${EXPECT_SLEEP}" ✓`,
  `[dp] 提示：Dvcss 配置仍需探针注入（K-1415），GV_HOME=${HOME_DIR}`,
  `[dp] 前置速记：shlwapi（C 链）+ PresentationCore（B 链）+ 三 DLL 补丁（A 链）齐全后`,
  `[dp]           一条命令链：node 40-build/appliers/apply.mjs && node 40-build/appliers/font-chain.mjs && node 40-build/appliers/shim-chain.mjs && node 40-build/appliers/dvcss-prereq.mjs`,
]);
