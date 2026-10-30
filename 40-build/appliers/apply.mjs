#!/usr/bin/env node
// 派生树生成器（applier 骨架）#applier
// 把只读上游快照复制成派生树，并按 09 §4 的补丁项做文本变换；只做确定性文本改写，不产出二进制。
// 用法：
//   node 40-build/appliers/apply.mjs [--src 40-build/upstream/wpf] [--dst 40-build/build/wpf] [--check]
// 补丁项（与 09 §4 对应）：
//   P1 TFM 改写（net10.0-windows* → net10.0）
//   P3 P/Invoke 目标名映射（生成 DllImportTargets.props，交给运行时 resolver）
//   P9 构建排除（redist/、tests/ 不进入派生树）
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const arg = (name, def) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && argv[i + 1] ? path.resolve(ROOT, argv[i + 1]) : def;
};
const SRC = arg('src', path.join(ROOT, '40-build/upstream/wpf'));
const DST = arg('dst', path.join(ROOT, '40-build/build/wpf'));
const CHECK = argv.includes('--check');
const SKIP_DIRS = new Set(['redist', 'tests', '.git']);
const TEXT_EXT = new Set(['.csproj', '.props', '.targets', '.csproj', '.projitems', '.props', '.targets', '.csdef']);
const TFM_RE = /net1[01]\.0-windows(?:[\d.]+)?/g;

function walk(dir, base = dir, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (SKIP_DIRS.has(ent.name)) continue;
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, base, out);
    else out.push(path.relative(base, p));
  }
  return out;
}

if (!fs.existsSync(SRC)) {
  console.error(`ERROR 上游快照不存在：${path.relative(ROOT, SRC)}`);
  process.exit(1);
}

const files = walk(SRC).sort();
const rewrites = [];
const plan = new Map(); // relPath -> content

for (const rel of files) {
  const abs = path.join(SRC, rel);
  const ext = path.extname(rel).toLowerCase();
  const buf = fs.readFileSync(abs);
  if (!TEXT_EXT.has(ext)) continue;
  const text = buf.toString('utf8');
  if (!TFM_RE.test(text)) continue;
  TFM_RE.lastIndex = 0;
  const next = text.replace(TFM_RE, 'net10.0');
  if (next !== text) {
    const n = (text.match(TFM_RE) || []).length;
    rewrites.push({ file: rel, patch: 'P1', from: 'net1x.0-windows*', to: 'net10.0', occurrences: n });
    plan.set(rel, next);
  }
}

const dllTargets = {
  generated_by: '40-build/appliers/apply.mjs',
  note: 'P3：托管层 DllImport 的库名 → Linux .so 的映射；运行时由 resolver 读取，构建期只落表。',
  mapping: [
    { from: 'user32.dll', to: 'libwpfwin32.so' },
    { from: 'gdi32.dll', to: 'libwpfwin32.so' },
    { from: 'kernel32.dll', to: 'libwpfwin32.so' },
    { from: 'gdiplus.dll', to: 'libwpfwin32.so' },
    { from: 'PresentationNative_cor3.dll', to: 'libwpfwin32.so' },
    { from: 'wpfgfx_cor3.dll', to: 'libwpfgfx_linux.so' },
    { from: 'WindowsCodecs.dll', to: 'libwpfwic.so' },
    { from: 'dwrite.dll', to: 'libdwlinux.so' },
  ],
};

if (CHECK) {
  if (!fs.existsSync(DST)) {
    console.error(`ERROR 派生树不存在：${path.relative(ROOT, DST)}（跑一次不带 --check）`);
    process.exit(1);
  }
  const problems = [];
  for (const [rel, content] of plan) {
    const abs = path.join(DST, rel);
    if (!fs.existsSync(abs)) problems.push(`缺文件 ${rel}`);
    else if (fs.readFileSync(abs, 'utf8') !== content) problems.push(`未按补丁改写 ${rel}`);
  }
  const report = path.join(DST, 'patch-report.json');
  if (!fs.existsSync(report)) problems.push('缺 patch-report.json');
  for (const p of problems) console.error(`ERROR ${p}`);
  console.log(`apply --check: 计划改写 ${rewrites.length} 个文件，问题 ${problems.length}`);
  process.exit(problems.length ? 1 : 0);
}

fs.rmSync(DST, { recursive: true, force: true });
let copied = 0;
for (const rel of files) {
  const to = path.join(DST, rel);
  fs.mkdirSync(path.dirname(to), { recursive: true });
  if (plan.has(rel)) fs.writeFileSync(to, plan.get(rel), 'utf8');
  else fs.copyFileSync(path.join(SRC, rel), to);
  copied += 1;
}
// P4：源覆盖——dnceng 私有源在本机不可达（实测 Resource temporarily unavailable），派生树统一走 nuget.org
fs.writeFileSync(
  path.join(DST, 'NuGet.config'),
  '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <!-- 由 apply.mjs 生成（P4）：dnceng 源不可达，统一走 nuget.org -->\n  <packageSources>\n    <clear />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n</configuration>\n',
  'utf8',
);

// 防止本仓 40-build/Directory.Packages.props 泄漏进派生构建（上游不用中央包管理）
if (!fs.existsSync(path.join(SRC, 'Directory.Packages.props'))) {
  fs.writeFileSync(
    path.join(DST, 'Directory.Packages.props'),
    '<Project>\n  <!-- 由 apply.mjs 生成：挡掉上层 40-build/Directory.Packages.props 的中央包管理（上游不含此文件） -->\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n',
    'utf8',
  );
}

fs.writeFileSync(path.join(DST, 'DllImportTargets.props'), `<Project>\n  <!-- 由 apply.mjs 生成（P3）：库名 → .so 映射，resolver 在运行期读取 -->\n  <ItemGroup>\n${dllTargets.mapping
  .map((m) => `    <DllImportTarget Include="${m.from}" LinuxLibrary="${m.to}" />`)
  .join('\n')}\n  </ItemGroup>\n</Project>\n`, 'utf8');

const snap = path.join(SRC, 'SNAPSHOT.json');
const snapshot = fs.existsSync(snap) ? JSON.parse(fs.readFileSync(snap, 'utf8')) : null;
fs.writeFileSync(
  path.join(DST, 'patch-report.json'),
  JSON.stringify(
    {
      generated_by: '40-build/appliers/apply.mjs',
      source: path.relative(ROOT, SRC),
      source_commit: snapshot ? snapshot.commit : null,
      generated_at_note: '确定性：同输入同输出；不含时间戳',
      skipped_dirs: [...SKIP_DIRS],
      copied_files: copied,
      patches: [
        { id: 'P1', desc: 'TFM 改写 net1x.0-windows* → net10.0', files: rewrites },
        { id: 'P3', desc: 'DllImport 库名 → .so 映射', files: ['DllImportTargets.props'], mapping: dllTargets.mapping },
        { id: 'P4', desc: '源覆盖（dnceng → nuget.org）', files: ['NuGet.config'] },
        { id: 'P9', desc: '构建排除', skipped: [...SKIP_DIRS] },
      ],
      not_yet: ['P2 程序集身份', 'P4 平台常量/注册表替换', 'P5 资源与主题', 'P6 PBT 定位', 'P7 pack 组装', 'P8 兼容层开关', 'P10 必抛件替换'],
    },
    null,
    2,
  ) + '\n',
  'utf8',
);

console.log(`apply: 复制 ${copied} 个文件 → ${path.relative(ROOT, DST)}；P1 改写 ${rewrites.length} 个文件；P3 映射 ${dllTargets.mapping.length} 条`);
