#!/usr/bin/env node
// 符号对拍：#符号对拍器（TG-001 的第一块）
// 把编译出的 .so 的导出符号与 exports.txt 的 impl 行逐符号比对；差异即失败。
// 用法：node 50-verify/contract/check-symbols.mjs [--so <path>] [--json <dir>]
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const soIdx = argv.indexOf('--so');
const SO = soIdx >= 0 && argv[soIdx + 1] ? path.resolve(ROOT, argv[soIdx + 1]) : path.join(ROOT, '40-build/native-shim/bin/libwpfwin32.so');
const jsonIdx = argv.indexOf('--json');
const OUT_DIR = jsonIdx >= 0 && argv[jsonIdx + 1] ? path.resolve(ROOT, argv[jsonIdx + 1]) : path.join(ROOT, '50-verify/读数/contract');
const EXPORTS = path.join(ROOT, '40-build/native-shim/exports.txt');

if (!fs.existsSync(SO)) {
  console.error(`ERROR 找不到 .so：${path.relative(ROOT, SO)}（先 make -C 40-build/native-shim）`);
  process.exit(1);
}

const lib = path.basename(SO);
const nm = spawnSync('nm', ['-D', '--defined-only', SO], { encoding: 'utf8' });
if (nm.status !== 0) {
  console.error(`ERROR nm 失败：${(nm.stderr || '').trim()}`);
  process.exit(1);
}
const exported = new Set(
  nm.stdout
    .split('\n')
    .filter((l) => /\sT\s/.test(l))
    .map((l) => l.split(/\s+/).pop())
    .filter(Boolean),
);

const listed = new Map(); // symbol -> status
for (const line of fs.readFileSync(EXPORTS, 'utf8').split('\n')) {
  if (!line.trim() || line.startsWith('#')) continue;
  const c = line.split('\t');
  if (c[0] !== lib) continue;
  listed.set(c[1], c[2]);
}
const impl = new Set([...listed].filter(([, s]) => s === 'impl').map(([n]) => n));
const stub = new Set([...listed].filter(([, s]) => s === 'stub').map(([n]) => n));
const gap = new Set([...listed].filter(([, s]) => s === 'gap').map(([n]) => n));

const missing = [...impl].filter((s) => !exported.has(s)).sort();
const extra = [...exported].filter((s) => !impl.has(s) && !stub.has(s)).sort();
const stubExported = [...stub].filter((s) => exported.has(s)).sort();

const verdict = missing.length === 0 && stubExported.length === 0 ? 'PASS' : 'FAIL';
const now = new Date();
const stamp = now.toISOString().replace(/[:.]/g, '-');
fs.mkdirSync(OUT_DIR, { recursive: true });
const file = path.join(OUT_DIR, `${stamp}-symbols-${lib.replace(/[^\w.-]/g, '_')}.json`);
fs.writeFileSync(
  file,
  JSON.stringify(
    {
      kind: 'contract',
      gate: 'TG-001',
      lib,
      verdict,
      started: now.toISOString(),
      counts: { exported: exported.size, listed: listed.size, impl: impl.size, stub: stub.size, gap: gap.size, missing: missing.length, extra: extra.length },
      missing_impl: missing,
      extra_exports: extra,
      stub_but_exported: stubExported,
      note: 'missing_impl＝清单声明 impl 但 .so 未导出；extra_exports＝.so 导出但清单没有 impl/stub 行（清单只覆盖上游 DllImport 面，导出多于此属正常，需人工判定是否补录）；stub_but_exported＝清单标 stub 却真导出，属判定口径问题。',
    },
    null,
    2,
  ) + '\n',
  'utf8',
);

console.log(`check-symbols: ${lib} 导出 ${exported.size}；清单 impl ${impl.size} / stub ${stub.size} / gap ${gap.size}`);
console.log(`  清单 impl 未导出：${missing.length}${missing.length ? `（例：${missing.slice(0, 5).join(', ')}）` : ''}`);
console.log(`  .so 导出但清单无 impl/stub：${extra.length}${extra.length ? `（例：${extra.slice(0, 5).join(', ')}）` : ''}`);
console.log(`  清单标 stub 却真导出：${stubExported.length}`);
console.log(`  verdict=${verdict}  读数：${path.relative(ROOT, file)}`);
process.exit(verdict === 'PASS' ? 0 : 1);
