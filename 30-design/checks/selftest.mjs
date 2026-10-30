#!/usr/bin/env node
// 自检的判据：#链接检查器 的负向测试。
// 造三类违规（裸 .md 路径、裸行号、死链），断言检查器 rc=1 且逐类命中；清理后再断言 rc=0。
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '../..');
const CHECK = path.join(__dirname, 'check-links.mjs');
const TMP = path.join(ROOT, '_selftest_links.md');

function run() {
  try {
    const stdout = execFileSync(process.execPath, [CHECK], { encoding: 'utf8', stdio: 'pipe' });
    return { code: 0, out: stdout };
  } catch (e) {
    return { code: e.status, out: `${e.stdout || ''}${e.stderr || ''}` };
  }
}

const assertions = [];
function check(name, cond, detail = '') {
  assertions.push({ name, ok: Boolean(cond), detail });
}

fs.writeFileSync(TMP, '见 30-design/概要设计.md 与 L123，另见 [死链](no-such-file.md)。\n');
try {
  const dirty = run();
  check('负向：rc=1', dirty.code === 1, `实际 rc=${dirty.code}`);
  for (const key of ['裸 .md 路径', '裸行号', '链接目标不存在']) {
    check(`负向：命中「${key}」`, dirty.out.includes(key));
  }
} finally {
  fs.rmSync(TMP, { force: true });
}

const clean = run();
check('正向：清理后 rc=0', clean.code === 0, `实际 rc=${clean.code}`);

let failed = 0;
for (const a of assertions) {
  if (!a.ok) failed += 1;
  console.log(`${a.ok ? 'PASS' : 'FAIL'} ${a.name}${a.detail ? ` (${a.detail})` : ''}`);
}
console.log(`selftest: ${assertions.length - failed}/${assertions.length} 通过`);
process.exit(failed ? 1 : 0);
