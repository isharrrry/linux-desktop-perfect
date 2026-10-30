#!/usr/bin/env node
// 环境探头：#环境探头
// 一条命令打印 09 §3 依赖矩阵的检出读数，并把结果落成 JSON（判据先行：环境也是判据）。
// 用法：node 50-verify/env/probe-env.mjs [--json <dir>]
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const jsonIdx = argv.indexOf('--json');
const OUT_DIR = jsonIdx >= 0 && argv[jsonIdx + 1] ? path.resolve(ROOT, argv[jsonIdx + 1]) : path.join(ROOT, '50-verify/读数/env');

function run(cmd, args) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', timeout: 15000 });
  const text = `${r.stdout || ''}${r.stderr || ''}`.replace(/\s+/g, ' ').trim();
  return { status: r.status === null ? -1 : r.status, out: text };
}

function sh(cmd, args) {
  const r = run(cmd, args);
  return { ok: r.status === 0, out: r.out };
}

function tool(bin, versionArgs) {
  const has = run('sh', ['-c', `command -v ${bin}`]);
  if (has.status !== 0) return { ok: false, out: '未安装' };
  const v = versionArgs ? run(bin, versionArgs) : { status: -1, out: '' };
  return { ok: true, out: v.out ? v.out.slice(0, 80) : '可用（无版本输出）' };
}

const probes = [
  { name: 'dotnet SDK', need: '10.0.x', run: () => sh('dotnet', ['--version']), required: true },
  { name: 'dotnet SDK 列表', need: '含 10.0', run: () => sh('dotnet', ['--list-sdks']), required: true, keep: 'all' },
  { name: 'Xvfb', need: '可用', run: () => tool('Xvfb'), required: true },
  { name: 'xdotool', need: '可用', run: () => tool('xdotool', ['--version']), required: true },
  { name: 'dbus-daemon', need: '可用', run: () => tool('dbus-daemon'), required: true },
  { name: 'libx11-6', need: '发行版稳定版', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libx11-6']), required: true },
  { name: 'libharfbuzz0b', need: '≥2.6', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libharfbuzz0b']), required: true },
  { name: 'libicu70', need: '≥70', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libicu70']), required: true },
  { name: 'libfreetype6', need: '≥2.10', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libfreetype6']), required: true },
  { name: 'libfontconfig1', need: '≥2.13', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libfontconfig1']), required: true },
  { name: 'fontconfig 字体数', need: '>0', run: () => sh('fc-list', []), required: true, keep: 'count' },
  { name: 'liblcms2-2', need: '发行版稳定版', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'liblcms2-2']), required: true },
  { name: 'libcups2', need: '≥2.3', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libcups2']), required: false },
  { name: 'cupsd', need: '可选（无则用假 IPP）', run: () => tool('cupsd'), required: false },
  { name: 'libatspi2.0-0', need: '≥2.44', run: () => sh('dpkg-query', ['-W', "-f=${Version}", 'libatspi2.0-0']), required: false },
  { name: 'GStreamer', need: '≥1.20', run: () => tool('gst-inspect-1.0', ['--version']), required: true },
  { name: 'ibus 或 fcitx5', need: 'ibus≥1.5 / fcitx5≥5', run: () => tool('ibus', ['version']), required: false },
  { name: 'Python yaml/dbus/gi/PIL', need: '四模块齐全', run: () => sh('python3', ['-c', 'import yaml,dbus,gi,PIL;print("yaml",yaml.__version__)']), required: true },
  { name: 'SkiaSharp 缓存', need: '2.88.9', run: () => sh('ls', [`${process.env.HOME}/.nuget/packages/skiasharp`]), required: true, keep: 'all' },
  { name: 'ref 包缓存', need: '10.0.11', run: () => sh('ls', [`${process.env.HOME}/.nuget/packages/microsoft.windowsdesktop.app.ref`]), required: true, keep: 'all' },
];

const rows = probes.map((p) => {
  const r = p.run();
  let value = r.out;
  if (p.keep === 'count') value = `${r.out ? r.out.split('\n').filter(Boolean).length : 0} 条`;
  if (p.keep === 'all') value = r.out.split('\n').filter(Boolean).join(' | ').slice(0, 120);
  return { name: p.name, need: p.need, required: p.required, ok: r.ok, value: value || '（空）' };
});

const failed = rows.filter((r) => r.required && !r.ok);
const verdict = failed.length ? 'FAIL' : 'PASS';
const now = new Date();
const stamp = now.toISOString().replace(/[:.]/g, '-');

fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, `${stamp}-env.json`);
fs.writeFileSync(
  file,
  JSON.stringify(
    {
      kind: 'env',
      verdict,
      started: now.toISOString(),
      env: { runner: 'host', os: process.platform, node: process.version },
      checks: rows,
      failed: failed.map((r) => r.name),
      note: '环境判据：required 项缺失即 FAIL；optional 缺失记 WARN，不阻塞（对应 09 §3）。',
    },
    null,
    2,
  ) + '\n',
  'utf8',
);

console.log('依赖'.padEnd(24) + '要求'.padEnd(20) + '必需  实测');
for (const r of rows) {
  console.log(`${r.name.padEnd(24)}${r.need.padEnd(20)}${(r.required ? '是' : '否').padEnd(6)}${r.ok ? '' : '✗ '}${r.value}`);
}
console.log(`\nprobe-env: ${rows.length} 项，必需项缺失 ${failed.length} 项，verdict=${verdict}`);
console.log(`读数：${path.relative(ROOT, file)}`);
process.exit(failed.length ? 1 : 0);
