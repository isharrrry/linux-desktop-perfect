#!/usr/bin/env node
// 文档包检查器：#文档包检查器
// 规则见 00-admin/引用与名称规范.md。无外部依赖。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '../..');
// upstream = 只读上游快照（40-build/upstream/wpf），不参与本仓文档与编号校验
// 40-build = 实现树（含只读上游快照与派生树，自带上游文档），不参与本仓文档与编号校验
const EXCLUDE = new Set(['.git', 'node_modules', '90-archive', 'upstream', '40-build']);

const errors = [];
const warnings = [];

function walk(dir, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (EXCLUDE.has(ent.name)) continue;
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, out);
    else if (ent.name.endsWith('.md')) out.push(p);
  }
  return out;
}

function stripCode(text) {
  return text
    .replace(/```[\s\S]*?```/g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/`[^`\n]*`/g, (m) => m.replace(/[^\n]/g, ' '));
}

const names = JSON.parse(fs.readFileSync(path.join(ROOT, '30-design/model/names.json'), 'utf8'));

// 1. 必备文件
for (const [name, rel] of Object.entries(names.documents)) {
  if (!fs.existsSync(path.join(ROOT, rel))) errors.push(`必备文档缺失：《${name}》-> ${rel}`);
}

const spec = fs.readFileSync(path.join(ROOT, '20-spec/需求与验收判据文档.md'), 'utf8');
const design = fs.readFileSync(path.join(ROOT, '30-design/概要设计.md'), 'utf8');

// 2. 文档头
for (const [label, text] of [['需求与验收判据文档', spec], ['概要设计', design]]) {
  for (const key of ['文档状态', '日期', '上游']) {
    if (!text.includes(key)) errors.push(`《${label}》缺少文档头字段：${key}`);
  }
}

// 3. 编号定义与引用
const defs = new Set();
const collect = (text, prefix) => {
  const re = new RegExp(`\\b${prefix}-(\\d+)\\b`, 'g');
  let m;
  while ((m = re.exec(text))) defs.add(`${prefix}-${m[1].padStart(3, '0')}`);
};
['REQ-FN', 'ACC', 'NF', 'XIF', 'DQ'].forEach((p) => collect(spec, p));
['ADR', 'MOD', 'SYS', 'R'].forEach((p) => collect(design, p));

// 范围/斜杠展开：REQ-FN-013~017、REQ-FN-001/002/003
function expand(text, prefix) {
  const out = new Set();
  const re = new RegExp(`\\b${prefix}-(\\d+)((?:\\s*[~/]\\s*\\d+)*)`, 'g');
  let m;
  while ((m = re.exec(text))) {
    const start = Number(m[1]);
    out.add(`${prefix}-${String(start).padStart(3, '0')}`);
    const tail = m[2] || '';
    for (const part of tail.matchAll(/[~/]\s*(\d+)(?:\s*~\s*(\d+))?/g)) {
      const a = Number(part[1]);
      const b = part[2] ? Number(part[2]) : a;
      for (let i = a; i <= b; i += 1) out.add(`${prefix}-${String(i).padStart(3, '0')}`);
    }
  }
  return out;
}

const cited = new Set();
for (const file of walk(ROOT)) {
  const text = stripCode(fs.readFileSync(file, 'utf8'));
  for (const prefix of ['REQ-FN', 'ACC', 'NF', 'XIF', 'DQ', 'ADR', 'MOD', 'SYS', 'R']) {
    for (const ref of expand(text, prefix)) cited.add(ref);
  }
}
for (const ref of cited) {
  if (!defs.has(ref)) errors.push(`引用悬空：${ref} 未在定义方出现`);
}

// 4. REQ -> ACC 覆盖（需求文档内每条 REQ 都应在 ACC 表出现）
const reqs = [...defs].filter((x) => x.startsWith('REQ-FN-'));
const accTable = spec.slice(spec.indexOf('## 9. 验收准则'));
for (const req of reqs) {
  if (!accTable.includes(req)) errors.push(`覆盖缺失：${req} 没有对应 ACC`);
}

// 5. 每条 ACC 在 50-verify 索引前至少被定义（当前阶段只查存在）
const accs = [...defs].filter((x) => x.startsWith('ACC-'));
if (accs.length !== reqs.length) warnings.push(`REQ ${reqs.length} 条 vs ACC ${accs.length} 条，数量不一致（应 1:1）`);

// 6. 术语首选名
if (!design.includes('LinuxDesktopPerfect')) errors.push('《概要设计》未出现首选名 LinuxDesktopPerfect');
if (!fs.existsSync(path.join(ROOT, 'AGENTS.md'))) errors.push('缺少 AGENTS.md（Agent 维护约束入口）');

for (const w of warnings) console.warn(`WARN ${w}`);
for (const e of errors) console.error(`ERROR ${e}`);
console.log(`check-package: REQ=${reqs.length} ACC=${accs.length} 定义=${defs.size} 引用=${cited.size}，${errors.length} 错误`);
process.exit(errors.length ? 1 : 0);
