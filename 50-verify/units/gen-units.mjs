#!/usr/bin/env node
// 由 54 团校准配置生成「每团 3 条用例」：
//   ① 静态事实断言（文件清单未漂移 / 非空 / 行数下界 / 代表文件含真实符号）
//   ② ACC 判据锚定断言（该团关联的 ACC 在索引里有命令、命令脚本存在且语法可过、
//      08 文档有该 ACC、若本机有读数则校验 acc/verdict 字段；读数缺失打印 SKIP 不判失败）
//   ③ 闸门覆盖/入库断言（A 档：团内至少 1 个文件的祖先目录名匹配 50-verify/coverage.sh 的 PKGS 前缀；
//      B 档兜底：团内全部文件出现在 `git ls-files` 里，防未跟踪文件混入）
// 用法: node 50-verify/units/gen-units.mjs
// 产物: 50-verify/units/unit-XX-<slug>.test.js（CommonJS，node:test）
// 口径: kit 的 count_cases 只认 .js 里的 `test(`，不认 .mjs；每文件恰好 3 处 ⇒ cases=3。
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const CAL = path.join(ROOT, '50-verify/audit/calibration/groups.json');
const ACCMAP = path.join(ROOT, '50-verify/units/acc-map.json');
const IDX = path.join(ROOT, '50-verify/index/gen-index.mjs');
const OUT = path.join(ROOT, '50-verify/units');

const groups = JSON.parse(fs.readFileSync(CAL, 'utf8'));
const accMap = JSON.parse(fs.readFileSync(ACCMAP, 'utf8'));
// 闸门覆盖前缀（唯一事实源：coverage.sh；运行时断言会重新解析，改前缀会让对应用例变红）
const COVERAGE = path.join(ROOT, '50-verify/coverage.sh');
const PREFIXES = [...fs.readFileSync(COVERAGE, 'utf8').matchAll(/'([\w.]+)\*'/g)].map((m) => m[1]);
if (!PREFIXES.length) throw new Error('coverage.sh 里没抓到任何包前缀');

// 从索引生成器里抓 ACC → 单行命令（唯一事实源，不复制命令）
const idxSrc = fs.readFileSync(IDX, 'utf8');
const cmds = {};
for (const m of idxSrc.matchAll(/^ {2}'(ACC-\d{3})': '([^']+)',$/gm)) cmds[m[1]] = m[2];
if (!Object.keys(cmds).length) throw new Error('gen-index.mjs 里没抓到任何 ACC 命令（正则失配）');

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
  const any = txt.match(/[A-Za-z_]\w{3,}/);
  return any ? any[0] : null;
}

fs.mkdirSync(OUT, { recursive: true });
const reused = [];
const rows = [];
const accUse = {};

groups.forEach((g, i) => {
  const files = [...new Set(g.files)].sort();
  const covered = files.some((rel) =>
    rel.split('/').slice(0, -1).some((d) => PREFIXES.some((p) => d.startsWith(p))));
  const abs = files.map((f) => path.join(ROOT, f));
  const sizes = abs.map((p) => fs.statSync(p).size);
  const loc = abs.reduce((n, p) => n + fs.readFileSync(p, 'utf8').split('\n').length, 0);
  const biggest = files[sizes.indexOf(Math.max(...sizes))];
  const sym = symbolOf(biggest);
  if (!sym) throw new Error(`团 [${g.name}] 无法从 ${biggest} 提取符号`);

  const accs = accMap[g.name];
  if (!accs || !accs.length) throw new Error(`acc-map 缺团映射：${g.name}`);
  for (const a of accs) {
    if (!cmds[a]) throw new Error(`团 [${g.name}] 的 ${a} 在 gen-index.mjs 里没有命令`);
    accUse[a] = (accUse[a] || 0) + 1;
  }
  const accCmds = Object.fromEntries(accs.map((a) => [a, cmds[a]]));

  const slug = slugOf(g.name, i);
  if (reused.includes(slug)) throw new Error(`slug 冲突：${slug}`);
  reused.push(slug);

  const testFile = `${slug}.test.js`;
  const third = covered
    ? `test(${JSON.stringify(`${g.name} · 闸门覆盖`)}, () => {
  const sh = fs.readFileSync(path.join(ROOT, '50-verify/coverage.sh'), 'utf8');
  const prefixes = [...sh.matchAll(/'([\\w.]+)\\*'/g)].map((m) => m[1]);
  assert.ok(prefixes.length > 0, 'coverage.sh 无包前缀');
  const hit = FILES.filter((rel) => rel.split('/').slice(0, -1).some((d) => prefixes.some((p) => d.startsWith(p))));
  assert.ok(hit.length > 0, '团内无文件落在 coverage.sh 前缀下');
});`
    : `test(${JSON.stringify(`${g.name} · 文件全部入库`)}, () => {
  const out = execFileSync('git', ['-c', 'core.quotepath=false', 'ls-files', '-z'],
    { cwd: ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const tracked = new Set(out.split('\\0').filter(Boolean));
  for (const rel of FILES) assert.ok(tracked.has(rel), '未跟踪文件：' + rel);
});`;
  const body = `'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：${g.name}
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = ${JSON.stringify(files, null, 2)};
const MIN_LOC = ${Math.floor(loc * 0.9)};
const SYMBOL = ${JSON.stringify(sym)};
const ACC_CMDS = ${JSON.stringify(accCmds, null, 2)};

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
  assert.ok(loc >= MIN_LOC, '源码行数 ' + loc + ' < ' + MIN_LOC);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});

test(${JSON.stringify(`${g.name} · ACC 判据锚定`)}, () => {
  const doc = fs.readFileSync(path.join(ROOT, '30-design/设计文档包/08-判据先行与界面检测设计说明.md'), 'utf8');
  let withReading = 0;
  for (const acc of Object.keys(ACC_CMDS)) {
    assert.ok(doc.includes(acc), '08 文档缺 ' + acc);
    const cmd = ACC_CMDS[acc];
    const scripts = [...cmd.matchAll(/50-verify\\/[\\w./-]+\\.mjs/g)].map((m) => m[0]);
    assert.ok(scripts.length > 0, acc + ' 的命令里没有 .mjs 脚本：' + cmd);
    for (const rel of scripts) {
      const abs = path.join(ROOT, rel);
      assert.ok(fs.existsSync(abs), acc + ' 脚本缺失：' + rel);
      execFileSync(process.execPath, ['--check', abs], { stdio: 'pipe' });
    }
    const dir = path.join(ROOT, '50-verify/读数', acc);
    if (!fs.existsSync(dir)) { console.log('SKIP ' + acc + ' 读数缺失（不入库，CI 正常）'); continue; }
    const jsons = fs.readdirSync(dir).filter((f) => /^\\d{4}-\\d{2}-\\d{2}T.*\\.json$/.exec(f)).sort();
    if (!jsons.length) { console.log('SKIP ' + acc + ' 无 JSON 读数'); continue; }
    const rec = JSON.parse(fs.readFileSync(path.join(dir, jsons[jsons.length - 1]), 'utf8'));
    assert.equal(rec.acc, acc, acc + ' 读数 acc 字段不符：' + rec.acc);
    assert.ok(['PASS', 'DEGRADED', 'FAIL', 'SKIP'].includes(rec.verdict), acc + ' verdict 非法：' + rec.verdict);
    withReading += 1;
  }
  console.log('ACC 锚定：' + withReading + '/' + Object.keys(ACC_CMDS).length + ' 条有本机读数');
});

${third}
`;
  fs.writeFileSync(path.join(OUT, testFile), body);
  g.tests = [`50-verify/units/${testFile}`];
  rows.push({ i: i + 1, name: g.name, files: files.length, loc, symbol: sym, test: testFile, accs });
});

fs.writeFileSync(CAL, JSON.stringify(groups, null, 2) + '\n');
console.log('generated ' + rows.length + ' unit tests (3 cases each) -> ' + path.relative(ROOT, OUT));
console.log('updated ' + path.relative(ROOT, CAL) + ' (tests filled: ' + groups.filter((g) => g.tests.length).length + '/' + groups.length + ')');
console.log('ACC distinct: ' + Object.keys(accUse).length + ', min groups per ACC: ' + Math.min(...Object.values(accUse)));
