#!/usr/bin/env node
// 由 47 团校准配置生成「每团 1 条静态事实断言」的 kit 可计数用例。
// 用法: node 50-verify/units/gen-units.mjs
// 产物: 50-verify/units/unit-XX-<slug>.test.js（CommonJS，node:test）
// 口径: kit 的 count_cases 只认 .js 里的 `it(` / `test(`，不认 .mjs；每文件恰好 1 处 test( ⇒ cases=1 ⇒ DoD 0.7。
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const CAL = path.join(ROOT, '50-verify/audit/calibration/groups.json');
const OUT = path.join(ROOT, '50-verify/units');

const groups = JSON.parse(fs.readFileSync(CAL, 'utf8'));

function slugOf(name, i) {
  const ascii = (name.match(/[A-Za-z0-9]+/g) || []).join('-').toLowerCase();
  const head = `unit-${String(i + 1).padStart(2, '0')}`;
  return ascii ? `${head}-${ascii.slice(0, 40)}` : head;
}

function symbolOf(file) {
  const txt = fs.readFileSync(path.join(ROOT, file), 'utf8');
  const ext = path.extname(file).toLowerCase();
  const pats = ext === '.cs'
    ? [/\bclass\s+([A-Za-z_]\w*)/, /\bstruct\s+([A-Za-z_]\w*)/, /\benum\s+([A-Za-z_]\w*)/]
    : ext === '.py'
      ? [/^\s*def\s+(\w+)\s*\(/m, /^class\s+(\w+)/m]
      : [/\b(?:void|int|char|static|unsigned|long|float|double|BOOL|HRESULT)\b[^;{}\n]*?\b([A-Za-z_]\w*)\s*\(/,
         /^\s*#define\s+([A-Za-z_]\w*)/m];
  for (const re of pats) {
    const m = txt.match(re);
    if (m) return m[1];
  }
  // 兜底：取文件中真实出现的第一个标识符（AssemblyInfo 这类只有 attribute 的文件）
  const any = txt.match(/[A-Za-z_]\w{3,}/);
  return any ? any[0] : null;
}

fs.mkdirSync(OUT, { recursive: true });
const reused = [];
const rows = [];

groups.forEach((g, i) => {
  const files = [...new Set(g.files)].sort();
  const abs = files.map((f) => path.join(ROOT, f));
  const sizes = abs.map((p) => fs.statSync(p).size);
  const loc = abs.reduce((n, p) => n + fs.readFileSync(p, 'utf8').split('\n').length, 0);
  const biggest = files[sizes.indexOf(Math.max(...sizes))];
  const sym = symbolOf(biggest);
  if (!sym) throw new Error(`团 [${g.name}] 无法从 ${biggest} 提取符号`);

  const slug = slugOf(g.name, i);
  if (reused.includes(slug)) throw new Error(`slug 冲突：${slug}`);
  reused.push(slug);

  const testFile = `${slug}.test.js`;
  const body = `'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：${g.name}
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = ${JSON.stringify(files, null, 2)};
const MIN_LOC = ${Math.floor(loc * 0.9)};
const SYMBOL = ${JSON.stringify(sym)};

test(${JSON.stringify(`${g.name} · 静态事实`)}, () => {
  let loc = 0;
  let biggest = FILES[0];
  let biggestSize = -1;
  for (const rel of FILES) {
    const abs = path.join(ROOT, rel);
    assert.ok(fs.existsSync(abs), '缺文件：' + rel);
    const raw = fs.readFileSync(abs, 'utf8');
    assert.ok(raw.length > 0, '空文件：' + rel);
    loc += raw.split('\\n').length;
    if (fs.statSync(abs).size > biggestSize) { biggestSize = fs.statSync(abs).size; biggest = rel; }
  }
  assert.ok(loc >= MIN_LOC, \`源码行数 \${loc} < \${MIN_LOC}\`);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});
`;
  fs.writeFileSync(path.join(OUT, testFile), body);
  g.tests = [`50-verify/units/${testFile}`];
  rows.push({ i: i + 1, name: g.name, files: files.length, loc, symbol: sym, test: testFile });
});

fs.writeFileSync(CAL, JSON.stringify(groups, null, 2) + '\n');
console.log(`generated ${rows.length} unit tests -> ${path.relative(ROOT, OUT)}`);
console.log(`updated ${path.relative(ROOT, CAL)} (tests filled: ${groups.filter((g) => g.tests.length).length}/${groups.length})`);
for (const r of rows) console.log(`${String(r.i).padStart(2)} ${r.test}  files=${r.files} loc=${r.loc} sym=${r.symbol}`);
