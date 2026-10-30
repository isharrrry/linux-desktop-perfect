#!/usr/bin/env node
// 移植链构建入口（SYS-1：MOD-02/03/04 的 Linux 托管件构建）#移植构建
//
// 依据：03 §3.6.3（MOD-53 targets）、09 §4 补丁项 6/16/18、PORT-CHANGES.md（每工程的移植差异清单）
//
// 这条链与 40-build/build.sh 的分工：
//   · build.sh        → 上游 csproj 直编（派生树 40-build/build/wpf），用于「上游能不能编」的读数；
//   · build-port.mjs  → wpf-port 的 Linux 工程（显式列上游源文件 + Linux 补丁 + shim），
//                       产物是自产件（ACC-004 / pack 组装的输入）。
//
// 依赖序取自前身仓 integration-wave.sh 的 ORDER（实测得出，不是拓扑猜的）：
//   WpfGfx（叶子）→ SWE → System.Xaml → WindowsBase → DWF.Provider → DWF
//   → UIAutomationTypes → Manipulations → UIAutomationProvider → PresentationCore
//   → CycleStub.PresentationFramework → CycleStub.ReachFramework → PBT → CycleStub.PresentationUI
//   → System.Printing → ReachFramework → PresentationFramework → PresentationFramework.Classic
//
// 用法：
//   node 40-build/wpf-port/build-port.mjs             # 全链构建（Release）
//   node 40-build/wpf-port/build-port.mjs --map       # 构建后把产物映射到派生树 artifacts/bin（pack/ACC-004 消费）
//   node 40-build/wpf-port/build-port.mjs --only System.Xaml
//   node 40-build/wpf-port/build-port.mjs --list      # 只打印依赖序

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const has = (f) => argv.includes(`--${f}`);
const val = (f, def) => {
  const i = argv.indexOf(`--${f}`);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : def;
};

const CONFIG = val('config', 'Release');
const UPSTREAM = path.resolve(val('upstream', path.join(ROOT, '40-build/build/wpf')));
const SRC = path.join(HERE, 'src/Microsoft.DotNet.Wpf.Linux/src');
const OUT = path.join(HERE, 'out');
const REPORT = path.join(HERE, 'build-report.json');
const MAP = has('map');

// 依赖序（每项：工程相对路径 + 期望产物程序集名）
const ORDER = [
  { id: 'WpfGfx', proj: 'WpfGfx/WpfGfx.Linux.csproj', asm: 'WpfGfx.Linux' },
  { id: 'System.Windows.Extensions', proj: 'System.Windows.Extensions/System.Windows.Extensions.Linux.csproj', asm: 'System.Windows.Extensions' },
  { id: 'System.Xaml', proj: 'System.Xaml/System.Xaml.Linux.csproj', asm: 'System.Xaml' },
  { id: 'WindowsBase', proj: 'WindowsBase/WindowsBase.Linux.csproj', asm: 'WindowsBase' },
  { id: 'DirectWrite.Provider', proj: 'DirectWrite/Provider/DirectWrite.Linux.Provider.csproj', asm: 'DirectWrite.Linux.Provider' },
  { id: 'DirectWriteForwarder', proj: 'DirectWriteForwarder/DirectWriteForwarder.Linux.csproj', asm: 'DirectWriteForwarder' },
  { id: 'UIAutomationTypes', proj: 'UIAutomationTypes/UIAutomationTypes.Linux.csproj', asm: 'UIAutomationTypes' },
  { id: 'System.Windows.Input.Manipulations', proj: 'System.Windows.Input.Manipulations/System.Windows.Input.Manipulations.Linux.csproj', asm: 'System.Windows.Input.Manipulations' },
  { id: 'UIAutomationProvider', proj: 'UIAutomationProvider/UIAutomationProvider.Linux.csproj', asm: 'UIAutomationProvider' },
  { id: 'PresentationCore', proj: 'PresentationCore/PresentationCore.Linux.csproj', asm: 'PresentationCore' },
  { id: 'CycleStub.PresentationFramework', proj: 'CycleStub.PresentationFramework/CycleStub.PresentationFramework.Linux.csproj', asm: 'PresentationFramework' },
  { id: 'CycleStub.ReachFramework', proj: 'CycleStub.ReachFramework/CycleStub.ReachFramework.Linux.csproj', asm: 'ReachFramework' },
  { id: 'PresentationBuildTasks', proj: 'PresentationBuildTasks/PresentationBuildTasks.Linux.csproj', asm: 'PresentationBuildTasks' },
  { id: 'CycleStub.PresentationUI', proj: 'CycleStub.PresentationUI/CycleStub.PresentationUI.Linux.csproj', asm: 'PresentationUI' },
  { id: 'System.Printing', proj: 'System.Printing/System.Printing.Linux.csproj', asm: 'System.Printing' },
  { id: 'ReachFramework', proj: 'ReachFramework/ReachFramework.Linux.csproj', asm: 'ReachFramework' },
  { id: 'PresentationFramework', proj: 'PresentationFramework/PresentationFramework.Linux.csproj', asm: 'PresentationFramework' },
  { id: 'PresentationFramework.Classic', proj: 'PresentationFramework.Classic/PresentationFramework.Classic.Linux.csproj', asm: 'PresentationFramework.Classic' },
];

if (has('list')) {
  ORDER.forEach((o, i) => console.log(`${String(i + 1).padStart(2)}. ${o.id} → ${o.asm}.dll`));
  process.exit(0);
}
if (!fs.existsSync(UPSTREAM)) {
  console.error(`ERROR 派生树不存在：${path.relative(ROOT, UPSTREAM)}（先跑 node 40-build/appliers/apply.mjs）`);
  process.exit(1);
}

function artifactOf(item) {
  return path.join(SRC, path.dirname(item.proj), 'bin', CONFIG, `${item.asm}.dll`);
}

function buildOne(item) {
  const proj = path.join(SRC, item.proj);
  if (!fs.existsSync(proj)) return { id: item.id, rc: 1, first_error: `工程缺失：${path.relative(ROOT, proj)}`, ms: 0 };
  const t0 = Date.now();
  const r = spawnSync(
    'dotnet',
    ['build', proj, '-c', CONFIG, '--nologo', '-v:q', '-m:1', `-p:UpstreamWpfRoot=${UPSTREAM}${path.sep}`],
    { encoding: 'utf8', timeout: 1800000 },
  );
  const out = `${r.stdout || ''}${r.stderr || ''}`;
  const errs = out.split('\n').filter((l) => /error |错误/.test(l));
  const warns = out.split('\n').filter((l) => /warning |警告/.test(l) && !/0 个警告/.test(l));
  const artifact = artifactOf(item);
  return {
    id: item.id,
    rc: r.status === null ? -1 : r.status,
    ms: Date.now() - t0,
    first_error: errs[0] ? errs[0].slice(0, 300) : null,
    error_count: errs.length,
    warning_count: warns.length,
    artifact: fs.existsSync(artifact) ? { path: path.relative(ROOT, artifact), bytes: fs.statSync(artifact).size } : null,
  };
}

const only = val('only', null);
const queue = only ? ORDER.filter((o) => o.id === only) : ORDER;
if (only && !queue.length) {
  console.error(`ERROR 未知工程：${only}（--list 看可选项）`);
  process.exit(1);
}

// 两轮破环：PresentationFramework ⇄ System.Printing 是循环依赖（各自 HintPath 指向对方的 stub 产物），
// 干净树上第一轮必然有一侧红；第二轮用第一轮刚落地的 stub 收敛。实测两轮必收敛（前身仓 ORDER 同样依赖这一性质）。
const latest = new Map();
for (let round = 1; round <= 2; round += 1) {
  let roundFailed = 0;
  const roundItems = round === 1 ? queue : queue.filter((i) => latest.get(i.id)?.rc !== 0);
  if (!roundItems.length) break;
  if (round === 2) console.log('  ── 第二轮（破环复盘）');
  for (const item of roundItems) {
    process.stdout.write(`  ${item.id.padEnd(38)}`);
    const row = buildOne(item);
    row.round = round;
    latest.set(item.id, row);
    process.stdout.write(`${row.rc === 0 ? 'OK' : 'FAIL'}  ${row.ms} ms${row.artifact ? `  ${row.artifact.bytes} B` : ''}\n`);
    if (row.rc !== 0) {
      roundFailed += 1;
      if (row.first_error) console.log(`       ${row.first_error}`);
    }
  }
  if (!roundFailed) break;
}

const rows = ORDER.filter((o) => latest.has(o.id)).map((o) => latest.get(o.id));
const failed = rows.filter((r) => r.rc !== 0).length;

// 产物映射：自产件 → 40-build/wpf-port/out/<asm>.dll（pack 组装与 ACC-004 的消费位置）
// 刻意不落派生树 artifacts/bin：上游 csproj 直编（build.sh）也会往那里写同名件（身份是上游 MSFT token），
// 两条链互相覆盖会让「自产件引用闭包」断裂（实测表现为 XAML 编译期 MC1000：找不到同 token 的 System.Xaml）。
let mapped = [];
if (MAP && !failed) {
  fs.rmSync(OUT, { recursive: true, force: true });
  fs.mkdirSync(OUT, { recursive: true });
  for (const item of ORDER) {
    const src = artifactOf(item);
    if (!fs.existsSync(src)) continue;
    const dst = path.join(OUT, `${item.asm}.dll`);
    fs.copyFileSync(src, dst);
    mapped.push({ asm: item.asm, to: path.relative(ROOT, dst), bytes: fs.statSync(dst).size });
  }
}

const report = {
  generated_by: '40-build/wpf-port/build-port.mjs',
  config: CONFIG,
  upstream: path.relative(ROOT, UPSTREAM),
  order: ORDER.map((o) => o.id),
  results: rows,
  out_dir: path.relative(ROOT, OUT),
  mapped_artifacts: mapped,
  notes: [
    '本链产物是自产托管件（ACC-004 / MOD-51/52 的输入）；build.sh 是上游 csproj 直编的另一条读数',
    'CycleStub.* 是破 PresentationFramework ⇄ System.Printing 循环依赖的 API 子集件，正式件构建后覆盖同名产物',
    '--map 把产物复制到派生树 artifacts/bin（不手改派生树源码，只落产物）',
  ],
};
fs.writeFileSync(REPORT, JSON.stringify(report, null, 2) + '\n', 'utf8');

const okCount = rows.filter((r) => r.rc === 0).length;
console.log(`build-port: ${okCount}/${rows.length} 成功；读数 ${path.relative(ROOT, REPORT)}${MAP ? `；映射 ${mapped.length} 件到 wpf-port/out` : ''}`);
process.exit(failed ? 1 : 0);
