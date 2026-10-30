#!/usr/bin/env node
// 编号检查器：#编号检查器
// 以 30-design/model/model.json 为期望，检查编号族的「定义在定义方、引用不悬空、模块在 03 有设计」。
// 规则见 00-admin/引用与名称规范.md 与 30-design/设计文档包/01 §3。无外部依赖。
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

const model = JSON.parse(fs.readFileSync(path.join(ROOT, '30-design/model/model.json'), 'utf8'));
function read(rel) {
  const abs = path.join(ROOT, rel);
  if (!fs.existsSync(abs)) {
    errors.push(`定义方缺失：${rel}（${path.basename(rel)} 未落地，编号无处定义）`);
    return '';
  }
  return stripCode(fs.readFileSync(abs, 'utf8'));
}

const pad = (n, digits) => String(n).padStart(digits, '0');

function expandGroups(groups) {
  const out = [];
  for (const [a, b] of groups) for (let i = a; i <= b; i += 1) out.push(i);
  return out;
}
const moduleNums = expandGroups(model.moduleGroups);
const interfaceNums = expandGroups(Object.values(model.interfaceGroups));
function expandRange(prefix, padDigits) {
  const [a, b] = model.ranges[prefix];
  const out = [];
  for (let i = a; i <= b; i += 1) out.push(`${prefix}-${pad(i, padDigits)}`);
  return out;
}

// 编号族的定义方与定义形态：headings = 标题里出现编号；rowstart = 行首/表格首格出现编号
const families = {
  'REQ-FN': { digits: 3, owner: model.definitionOwners['REQ-FN'], expected: expandRange('REQ-FN', 3), forms: ['heading', 'rowstart'], countKey: 'requirements' },
  ACC: { digits: 3, owner: model.definitionOwners.ACC, expected: expandRange('ACC', 3), forms: ['heading', 'rowstart'], countKey: 'acceptance' },
  NF: { digits: 3, owner: model.definitionOwners.NF, expected: expandRange('NF', 3), forms: ['heading', 'rowstart'], countKey: 'nonFunctional' },
  XIF: { digits: 3, owner: model.definitionOwners.XIF, expected: expandRange('XIF', 3), forms: ['heading', 'rowstart'], countKey: 'externalInterfaces' },
  DQ: { digits: 3, owner: model.definitionOwners.DQ, expected: expandRange('DQ', 3), forms: ['heading', 'rowstart'], countKey: 'decisions' },
  ADR: { digits: 3, owner: model.definitionOwners.ADR, expected: expandRange('ADR', 3), forms: ['heading'], countKey: 'adrs' },
  MOD: { digits: 2, owner: model.definitionOwners.MOD, expected: moduleNums.map((n) => `MOD-${pad(n, 2)}`), forms: ['rowstart'], countKey: 'modules' },
  SYS: { digits: 0, owner: model.definitionOwners.SYS, expected: Array.from({ length: 8 }, (_, i) => `SYS-${i + 1}`), forms: ['rowstart'], countKey: 'systems' },
  R: { digits: 3, owner: model.definitionOwners.R, expected: expandRange('R', 3), forms: ['rowstart'], countKey: 'risks' },
  SM: { digits: 3, owner: model.definitionOwners.SM, expected: expandRange('SM', 3), forms: ['heading'], countKey: 'stateMachines' },
  IF: { digits: 3, owner: model.definitionOwners.IF, expected: interfaceNums.map((n) => `IF-${pad(n, 3)}`), forms: ['heading'], countKey: 'interfaces' },
  TBL: { digits: 3, owner: model.definitionOwners.TBL, expected: expandRange('TBL', 3), forms: ['heading'], countKey: 'tables' },
  SAFE: { digits: 3, owner: model.definitionOwners.SAFE, expected: expandRange('SAFE', 3), forms: ['heading', 'rowstart'], countKey: 'safety' },
  SCR: { digits: 2, owner: model.definitionOwners.SCR, expected: expandRange('SCR', 2), forms: ['heading'], countKey: 'screens' },
  GAP: { digits: 3, owner: model.definitionOwners.GAP, expected: expandRange('GAP', 3), forms: ['heading', 'rowstart'], countKey: 'gaps' },
  TG: { digits: 3, owner: model.definitionOwners.TG, expected: expandRange('TG', 3), forms: ['heading'], countKey: 'testGates' },
};

function headingMatches(text, prefix, digits) {
  const re = new RegExp(`^#{1,6}[^\\n]*\\b${prefix}-(\\d{${digits}})\\b`, 'gm');
  const out = [];
  let m;
  while ((m = re.exec(text))) out.push(`${prefix}-${m[1]}`);
  return out;
}

function rowStartMatches(text, prefix, digits) {
  // 行首或表格首格出现编号：| MOD-01 ... / **SAFE-001** ... / GAP-001 ...
  const re = new RegExp(`^(?:[-*]\\s+)?(?:\\|\\s*)?\\*{0,2}(${prefix}-\\d{${digits}})\\*{0,2}(?=\\s|\\||：|:|$)`, 'gm');
  const out = [];
  let m;
  while ((m = re.exec(text))) out.push(m[1]);
  return out;
}

function definitions(rel, prefix, digits, forms) {
  const text = read(rel);
  const out = new Set();
  for (const form of forms) {
    const list = form === 'heading' ? headingMatches(text, prefix, digits) : rowStartMatches(text, prefix, digits);
    for (const id of list) out.add(id);
  }
  return out;
}

function references(prefix) {
  const re = new RegExp(`\\b${prefix}-\\d+\\b`, 'g');
  const out = new Map(); // id -> [files]
  for (const file of walk(ROOT)) {
    const rel = path.relative(ROOT, file);
    const text = stripCode(fs.readFileSync(file, 'utf8'));
    let m;
    while ((m = re.exec(text))) {
      if (!out.has(m[0])) out.set(m[0], new Set());
      out.get(m[0]).add(rel);
    }
  }
  return out;
}

// 1. 定义齐全、模型外编号、计数一致
const defined = {};
for (const [prefix, f] of Object.entries(families)) {
  const set = definitions(f.owner, prefix, f.digits || 1, f.forms);
  defined[prefix] = set;
  for (const id of f.expected) {
    if (!set.has(id)) errors.push(`定义缺失：${id} 未在定义方 ${f.owner} 以「${f.forms.join(' 或 ')}」形态出现`);
  }
  for (const id of set) {
    if (!f.expected.includes(id)) errors.push(`模型外编号：${id} 在 ${f.owner} 有定义，但 model.json 未分配（改模型或改文档）`);
  }
  if (f.countKey && model.counts[f.countKey] !== f.expected.length) {
    errors.push(`计数不符：model.json counts.${f.countKey}=${model.counts[f.countKey]}，但 ranges/moduleGroups 展开为 ${f.expected.length}`);
  }
}

// 2. 引用不悬空（包内所有 .md）
for (const [prefix, f] of Object.entries(families)) {
  const refs = references(prefix);
  for (const [id, files] of refs) {
    if (!f.expected.includes(id)) {
      errors.push(`引用悬空：${id} 出现在 ${[...files].join('、')}，但 ${prefix} 只允许 ${f.expected[0]}~${f.expected[f.expected.length - 1]}`);
    } else if (!defined[prefix].has(id)) {
      errors.push(`引用未定义：${id} 被 ${[...files].join('、')} 引用，但定义方 ${f.owner} 没有它的定义`);
    }
  }
}

// 3. 每个 MOD 必须在 03 有设计小节；每个 SM/TG 在对应文档有定义小节
for (const [prefix, docKey] of Object.entries(model.designLocatedIn)) {
  const rel = docKey;
  const digits = families[prefix].digits;
  const text = read(rel);
  const inDoc = new Set(headingMatches(text, prefix, digits));
  for (const id of families[prefix].expected) {
    if (!inDoc.has(id)) errors.push(`设计缺失：${id} 在 ${rel} 没有小节标题（每个编号都要有落点）`);
  }
}

// 4. 索引完整性：01 必须逐个索引 ADR/MOD/SM/TBL/TG
const indexDoc = '30-design/设计文档包/01-设计总纲与设计基线管理.md';
const indexText = read(indexDoc);
for (const prefix of ['ADR', 'MOD', 'SM', 'TBL', 'TG']) {
  for (const id of families[prefix].expected) {
    if (!new RegExp(`\\b${id}\\b`).test(indexText)) errors.push(`索引缺失：${id} 未出现在 ${indexDoc}（01 是唯一索引）`);
  }
}

// 5. 每个 SCR 必须有元素属性表（表头含 路径/角色/判据）
const screenDoc = model.definitionOwners.SCR;
const screenRaw = fs.existsSync(path.join(ROOT, screenDoc)) ? fs.readFileSync(path.join(ROOT, screenDoc), 'utf8') : '';
const screenSections = screenRaw.split(/^## /m);
for (const id of families.SCR.expected) {
  const sec = screenSections.find((s) => new RegExp(`\\b${id}\\b`).test(s.split('\n')[0] || ''));
  if (!sec) {
    errors.push(`界面缺失：${screenDoc} 没有以 ${id} 开头的二级小节`);
    continue;
  }
  for (const key of ['路径', '角色', '判据']) {
    if (!sec.includes(key)) errors.push(`元素属性表缺失：${id} 小节没有「${key}」列（检测需要 path/role/判据 三列）`);
  }
}

// 6. 契约数量口径：四个契约文件与 12 项专有面
const gaiyaoRaw = fs.existsSync(path.join(ROOT, '30-design/概要设计.md'))
  ? fs.readFileSync(path.join(ROOT, '30-design/概要设计.md'), 'utf8')
  : '';
for (const f of model.contractFiles) {
  if (!gaiyaoRaw.includes(f)) warnings.push(`契约文件未在概设出现：${f}`);
}

// 7. 目录骨架：设计承诺的落点必须存在（`--skeleton-root` 供自检指向空目录做负向断言）
const skelIdx = process.argv.indexOf('--skeleton-root');
const skeletonRoot = skelIdx >= 0 && process.argv[skelIdx + 1] ? path.resolve(ROOT, process.argv[skelIdx + 1]) : ROOT;
for (const rel of model.skeletonDirs || []) {
  if (!fs.existsSync(path.join(skeletonRoot, rel))) {
    errors.push(`目录缺失：${rel}（设计承诺的落点，见 03 §1.1、02 §11、08 §2；生成物见 model.generatedDirs）`);
  }
}

for (const w of warnings) console.warn(`WARN ${w}`);
for (const e of errors) console.error(`ERROR ${e}`);
const summary = Object.entries(families)
  .map(([p, f]) => `${p}=${f.expected.length}`)
  .join(' ');
console.log(`check-design-ids: ${summary}；${errors.length} 错误，${warnings.length} 警告`);
process.exit(errors.length ? 1 : 0);
