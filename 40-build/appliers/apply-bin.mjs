#!/usr/bin/env node
// 二进制 DLL 补丁的串行执行器（appliers 主链的「二进制段」）#applier
//
// 为什么存在：
//   `apply.mjs` 只做派生树文本改写、**不产出二进制**（见其文件头注释）。
//   而 UI 相关的三处修复都必须在已经部署的 `SourceTree.Api.UI.Wpf.dll` 上做 IL 补丁：
//     · loglabel-brush（K-1428）：History 面板首帧 `_backgroundBrush=null` 的 NRE
//     · commit-minreq （K-1431）：提交前的用户信息对话框 → `WindowsIdentity.GetCurrent()` 崩
//     · seticon-ret   （K-1433）：确认提交对话框 `Icon.FromHandle` 抛 `ArgumentException: handle`
//   四者改同一个 DLL，**顺序固定**：loglabel-brush → commit-minreq → seticon-ret → commit-noconfirm
//   （后两者在 patch 前会读回前一处的指纹，顺序错会以非 0 退出码拒绝）。
//
// 用法：
//   node 40-build/appliers/apply-bin.mjs                 # 默认 APP=/tmp/stpkg/SourceTree/app-3.4.15
//   APP=<dir> node 40-build/appliers/apply-bin.mjs       # 指定 APP
//   node 40-build/appliers/apply-bin.mjs --app <dir>     # 同上（显式参数优先）
//   node 40-build/appliers/apply-bin.mjs --check         # 只检查前置（不写盘）
//
// 纪律：
//   · 任一步非 0 退出 ⇒ 立即中止并透传退出码（不静默继续）；
//   · 目标 DLL 不存在 ⇒ 打印提示并 rc=0 跳过（APP 未部署不算失败）；
//   · 幂等由各子 applier 自己保证（已 patch 会打印「幂等跳过」，md5 不变）。
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const arg = (name, def) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] ? path.resolve(ROOT, argv[i + 1]) : def;
};

const APP = arg('app', process.env.APP || '/tmp/stpkg/SourceTree/app-3.4.15');
const CHECK = argv.includes('--check');
const TARGET = path.join(APP, 'SourceTree.Api.UI.Wpf.dll');

// 顺序固定且不可重排：每一项的 Program.cs 都会读回前项指纹，乱序会以退出码 5 拒绝。
const STEPS = [
  { name: 'loglabel-brush', ref: 'K-1428', why: 'LogLabelControl 的 brush 初始化（History 首帧 NRE）' },
  { name: 'commit-minreq', ref: 'K-1431', why: '跳过提交前的用户信息对话框（WindowsIdentity.GetCurrent 崩）' },
  { name: 'seticon-ret', ref: 'K-1433', why: '确认对话框 SetIcon 空实现（Icon.FromHandle 抛 ArgumentException）' },
  { name: 'commit-noconfirm', ref: 'K-1456', why: '确认提交对话框恒返回 true（避免提交被确认框阻塞）' },
];

if (!fs.existsSync(TARGET)) {
  console.log(`apply-bin: 跳过（未找到 ${TARGET}）—— 用 APP=<dir> 指定已部署的 app 目录`);
  process.exit(0);
}

const env = { ...process.env, APP };
for (const step of STEPS) {
  const script = path.join(HERE, step.name, 'apply.sh');
  if (!fs.existsSync(script)) {
    console.error(`apply-bin: ❌ 缺子 applier：${path.relative(ROOT, script)}`);
    process.exit(1);
  }
  console.log(`apply-bin: [${step.name}] ${step.ref} ${step.why}`);
  const args = [script];
  if (CHECK) args.push('--check');
  const r = spawnSync('sh', args, { stdio: 'inherit', cwd: ROOT, env });
  if (r.status !== 0) {
    console.error(`apply-bin: ❌ ${step.name} 失败（rc=${r.status}）⇒ 中止，后续步骤不执行`);
    process.exit(r.status || 1);
  }
}

const buf = fs.readFileSync(TARGET);
const md5 = crypto.createHash('md5').update(buf).digest('hex');
console.log(`apply-bin: ${CHECK ? '检查' : '完成'}四步；${path.relative(ROOT, TARGET)} md5=${md5}（${buf.length} 字节）`);
if (!CHECK) {
  // 三补丁全打后的已知 md5（K-1433 记录）。不一致只告警不改退出码：
  // 允许基线不同（例如上游更新过），但要让读者看见「和已知态不一样」。
  const KNOWN = '18e599a87059e1499f01cdc71c8e57c5';   // K-1456（四步：brush→minreq→seticon→noconfirm）
  if (md5 !== KNOWN) {
    console.log(`apply-bin: ⚠️ md5 与 K-1456 记录的已知态不同（期望 ${KNOWN}）—— 若基线/上游有变动属预期，否则请查补丁是否全部命中`);
  }
}
