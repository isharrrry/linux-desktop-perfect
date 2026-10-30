#!/usr/bin/env node
// 离线桩包生成器（D-002 的复算入口）#离线桩包生成器
// dnceng 私有源不可达时，用官方 ref 包里的引用程序集拼出三个「编译期够用」的桩包。
// 用法：node 40-build/lock/make-stub-feed.mjs [--ref <ref/net10.0 目录>] [--out 40-build/lock/offline-feed]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const val = (n, d) => {
  const i = argv.indexOf(`--${n}`);
  return i >= 0 && argv[i + 1] ? path.resolve(ROOT, argv[i + 1]) : d;
};
const REF = val('ref', path.join(os.homedir(), '.nuget/packages/microsoft.windowsdesktop.app.ref/10.0.11/ref/net10.0'));
const OUT = val('out', path.join(HERE, 'offline-feed'));

if (!fs.existsSync(REF)) {
  console.error(`ERROR ref 包目录不存在：${REF}（先还原 microsoft.windowsdesktop.app.ref）`);
  process.exit(1);
}
const dlls = fs.readdirSync(REF).filter((f) => f.endsWith('.dll')).sort();
if (dlls.length < 10) {
  console.error(`ERROR ref 包里的程序集只有 ${dlls.length} 个，疑似不完整`);
  process.exit(1);
}

const pkgs = [
  { id: 'Microsoft.Private.Winforms', version: '10.0.14-servicing.26473.102', withRef: true },
  { id: 'Microsoft.Private.Intellisense', version: '10.0.0-preview-20251006.1', withRef: false },
  { id: 'Microsoft.NETCore.Platforms', version: '10.0.14-servicing.26473.102', withRef: false },
];

fs.mkdirSync(OUT, { recursive: true });
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ldp-stub-'));
try {
  for (const p of pkgs) {
    const dir = path.join(work, p.id);
    fs.mkdirSync(path.join(dir, 'lib/net10.0'), { recursive: true });
    fs.writeFileSync(path.join(dir, `${p.id}.nuspec`), `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>${p.id}</id><version>${p.version}</version><authors>LDP offline stub</authors>
    <description>桩包（降级 D-002）：${p.withRef ? `含官方 ref 包 ${dlls.length} 个引用程序集，仅满足编译期引用` : '空包，仅满足元数据依赖'}；生成器 40-build/lock/make-stub-feed.mjs</description>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
  </metadata>
</package>
`);
    fs.mkdirSync(path.join(dir, '_rels'), { recursive: true });
    fs.writeFileSync(path.join(dir, '_rels/.rels'), '');
    if (p.withRef) for (const d of dlls) fs.copyFileSync(path.join(REF, d), path.join(dir, 'lib/net10.0', d));
    fs.writeFileSync(path.join(dir, 'lib/net10.0/_._'), '');
    const out = path.join(OUT, `${p.id.toLowerCase()}.${p.version}.nupkg`);
    fs.rmSync(out, { force: true });
    execFileSync('zip', ['-q', '-X', '-r', out, '.'], { cwd: dir });
    console.log(`stub: ${path.relative(ROOT, out)}（${p.withRef ? `${dlls.length} 个引用程序集` : '空包'}）`);
  }
} finally {
  fs.rmSync(work, { recursive: true, force: true });
}
console.log(`make-stub-feed: ${pkgs.length} 个桩包 → ${path.relative(ROOT, OUT)}（生成物不入库，见 .gitignore）`);
