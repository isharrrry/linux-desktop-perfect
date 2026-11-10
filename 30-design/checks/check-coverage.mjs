#!/usr/bin/env node
// 覆盖检查器：#覆盖检查器
// 核对 REQ → MOD → ACC → TG → SCR 矩阵、12 项专有面、界面清单与索引表是否闭合。
// 矩阵来源默认 08 §4；`--matrix-file <路径>` 供自检注入违规矩阵（#自检）。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '../..');
// upstream = 只读上游快照（40-build/upstream/wpf），不参与本仓文档与编号校验
// 40-build = 实现树（含只读上游快照与派生树，自带上游文档），不参与本仓文档与编号校验
const EXCLUDE = new Set(['.git', 'node_modules', '90-archive', 'upstream', '40-build']);

const argv = process.argv.slice(2);
const matrixFileArg = argv.indexOf('--matrix-file');
const matrixRel = matrixFileArg >= 0 ? argv[matrixFileArg + 1] : null;

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
const readFile = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

const pad = (n, digits) => String(n).padStart(digits, '0');
function expandGroups(groups) {
  const out = [];
  for (const [a, b] of groups) for (let i = a; i <= b; i += 1) out.push(i);
  return out;
}
// 编号范围的唯一事实源是 model.json 的 ranges：这里不硬编码条数（曾硬编码 30/15/7，
// 与 model 重复定义 ⇒ ACC-031 补登时两处对不上）。
function expandRange(prefix, digits) {
  const [a, b] = model.ranges[prefix];
  const out = [];
  for (let i = a; i <= b; i += 1) out.push(`${prefix}-${pad(i, digits)}`);
  return out;
}
const moduleSet = new Set(expandGroups(model.moduleGroups).map((n) => `MOD-${pad(n, 2)}`));
const tgSet = new Set(expandRange('TG', 3));
const scrSet = new Set(expandRange('SCR', 2));
const accSet = new Set(expandRange('ACC', 3));
// 矩阵 REQ 列允许的编号：REQ-FN 全量 + 外部需求族（model.externalRequirements，如 REQ-EXT-001）
const reqSet = new Set(expandRange('REQ-FN', 3));
for (const [prefix, range] of Object.entries(model.externalRequirements || {})) {
  const [a, b] = range;
  for (let i = a; i <= b; i += 1) reqSet.add(`${prefix}-${pad(i, 3)}`);
}
// ACC → 需求的显式映射（缺省按同号 REQ-FN-NNN；ACC-031 承接 REQ-EXT-001）
const accReqMap = model.acceptanceRequirements || {};

// 展开 `MOD-01/02/06`、`ACC-001~005`、`SCR-01~SCR-07`
function expandIds(cell, prefix, padDigits) {
  const out = new Set();
  const tokenRe = new RegExp(`(${prefix}-\\d+)|([/~])\\s*(?:${prefix}-)?(\\d+)`, 'g');
  let m;
  let last = null;
  while ((m = tokenRe.exec(cell))) {
    if (m[1]) {
      out.add(m[1]);
      last = Number(m[1].slice(prefix.length + 1));
    } else if (m[2] === '/') {
      const n = Number(m[3]);
      out.add(`${prefix}-${pad(n, padDigits)}`);
      last = n;
    } else {
      const n = Number(m[3]);
      const from = last === null ? n : last;
      for (let i = from; i <= n; i += 1) out.add(`${prefix}-${pad(i, padDigits)}`);
      last = n;
    }
  }
  return out;
}

// ---------- 1. 08 §4 矩阵 ----------
const matrixDocRel = '30-design/设计文档包/08-判据先行与界面检测设计说明.md';
const matrixRelPath = matrixRel || matrixDocRel;
if (!fs.existsSync(path.join(ROOT, matrixRelPath))) {
  errors.push(`矩阵文件不存在：${matrixRelPath}`);
}
const matrixText = readFile(matrixRelPath);
const matrixStart = matrixText.indexOf('## 4.');
const matrixEnd = matrixText.indexOf('## 5.', matrixStart);
if (matrixStart < 0 || matrixEnd < 0) {
  errors.push(`矩阵段落缺失：${matrixDocRel} 需要「## 4. …矩阵」到「## 5.」之间的表格`);
}
const matrixSection = matrixStart >= 0 && matrixEnd > matrixStart ? matrixText.slice(matrixStart, matrixEnd) : '';

const rows = [];
for (const line of matrixSection.split('\n')) {
  if (!line.trim().startsWith('|')) continue;
  const cells = line.split('|').map((c) => c.trim());
  if (cells.length < 8) continue;
  const acc = cells[1];
  if (!/^ACC-\d{3}$/.test(acc)) continue;
  rows.push({ acc, req: cells[2], mods: cells[3], tg: cells[4], scr: cells[5], form: cells[6], readout: cells[7] });
}

if (rows.length !== accSet.size) errors.push(`矩阵行数不符：${rows.length} 行，应为 ${accSet.size}（ACC 范围见 model.json ranges.ACC，每个编号一行）`);
const seen = new Set();
for (const r of rows) {
  if (seen.has(r.acc)) errors.push(`矩阵重复：${r.acc} 出现多次`);
  seen.add(r.acc);
  if (!accSet.has(r.acc)) errors.push(`矩阵编号非法：${r.acc}`);
  const expectReq = accReqMap[r.acc] || `REQ-FN-${r.acc.slice(4)}`;
  // 外部需求族（REQ-EXT-*）不在 expandIds 的 REQ-FN 正则内，按显式映射单独认
  const reqIds = expandIds(r.req, 'REQ-FN', 3);
  if (!reqIds.has(expectReq) && r.req.includes(expectReq)) reqIds.add(expectReq);
  if (!reqIds.has(expectReq)) errors.push(`矩阵对不上：${r.acc} 的 REQ 列应为 ${expectReq}，实际是「${r.req}」`);
  if (reqIds.size !== 1) errors.push(`矩阵一对多：${r.acc} 的 REQ 列只能有一条需求，实际 ${reqIds.size} 条`);
  for (const id of reqIds) if (!reqSet.has(id)) errors.push(`矩阵引用非法：${r.acc} 的 ${id} 不在 REQ 编号范围内（REQ-FN / externalRequirements）`);
  const modIds = expandIds(r.mods, 'MOD', 2);
  if (modIds.size === 0) errors.push(`矩阵缺模块：${r.acc} 的承接模块为空`);
  for (const id of modIds) if (!moduleSet.has(id)) errors.push(`矩阵引用非法：${r.acc} 的 ${id} 不在 moduleGroups 内`);
  const tgIds = expandIds(r.tg, 'TG', 3);
  if (tgIds.size === 0) errors.push(`矩阵缺测试门：${r.acc}`);
  for (const id of tgIds) if (!tgSet.has(id)) errors.push(`矩阵引用非法：${r.acc} 的 ${id}`);
  const scrIds = expandIds(r.scr, 'SCR', 2);
  for (const id of scrIds) if (!scrSet.has(id)) errors.push(`矩阵引用非法：${r.acc} 的 ${id}`);
  if (!r.form) errors.push(`矩阵缺判据形式：${r.acc}`);
  if (!r.readout.includes(`读数/ACC-${r.acc.slice(4)}/`)) errors.push(`读数落点不符：${r.acc} 的读数落点应形如 50-verify/读数/${r.acc}/，实际「${r.readout}」`);
}
for (const acc of accSet) if (!seen.has(acc)) errors.push(`矩阵缺行：${acc} 没有在 08 §4 矩阵出现`);

// TG-002~TG-014 至少有 acc；TG-001/015 是横切门
const tgUsed = new Set();
for (const r of rows) for (const id of expandIds(r.tg, 'TG', 3)) tgUsed.add(id);
for (const tg of tgSet) {
  if (!tgUsed.has(tg)) {
    if (['TG-001', 'TG-015'].includes(tg)) warnings.push(`横切门未在矩阵出现（预期行为）：${tg}`);
    else errors.push(`测试门空转：${tg} 没有承接任何 ACC`);
  }
}
const scrUsed = new Set();
for (const r of rows) for (const id of expandIds(r.scr, 'SCR', 2)) scrUsed.add(id);
for (const scr of scrSet) if (!scrUsed.has(scr)) errors.push(`界面空转：${scr} 没有承接任何 ACC`);

// ---------- 2. 01 §4.6 验收索引 ----------
const indexRel = '30-design/设计文档包/01-设计总纲与设计基线管理.md';
const indexText = stripCode(readFile(indexRel));
const i460 = indexText.indexOf('### 4.6');
const iNext = indexText.indexOf('## 5.', i460);
if (i460 < 0) errors.push(`索引段落缺失：${indexRel} 没有 §4.6`);
const idxSection = i460 >= 0 ? indexText.slice(i460, iNext > i460 ? iNext : undefined) : '';
for (const tg of tgSet) {
  const hits = (idxSection.match(new RegExp(`\\b${tg}\\b`, 'g')) || []).length;
  if (hits !== 1) errors.push(`索引不符：${tg} 在 ${indexRel} §4.6 出现 ${hits} 次，应恰好 1 次`);
}
const idxAccs = expandIds(idxSection, 'ACC', 3);
for (const acc of accSet) if (!idxAccs.has(acc)) errors.push(`索引缺判据：${acc} 未在 ${indexRel} §4.6 出现`);

// ---------- 3. 概要设计 §5：12 项专有面 ----------
const gaiyao = stripCode(readFile('30-design/概要设计.md'));
const g5 = gaiyao.indexOf('## 5. 12 项 Windows 专有面的落点');
const g6 = gaiyao.indexOf('## 6.', g5);
const g5Section = g5 >= 0 && g6 > g5 ? gaiyao.slice(g5, g6) : '';
if (!g5Section) errors.push('专有面段落缺失：30-design/概要设计.md §5');
const faceRows = g5Section.split('\n').filter((l) => /^\|\s*\d+\s*\|/.test(l));
if (faceRows.length !== model.specialFaces) errors.push(`专有面行数不符：${faceRows.length} 行，应为 ${model.specialFaces}`);
for (const line of faceRows) {
  const cells = line.split('|').map((c) => c.trim());
  const faceAcc = expandIds(cells[cells.length - 2] || '', 'ACC', 3);
  if (faceAcc.size === 0) errors.push(`专有面缺判据：${cells[2] || line.slice(0, 30)}`);
  for (const acc of faceAcc) if (!accSet.has(acc)) errors.push(`专有面判据非法：${cells[2]} -> ${acc}`);
}

// ---------- 4. 界面清单：SCR 全覆盖且各有检测列 ----------
const screenDocRel = '30-design/设计文档包/界面设计说明.md';
const screenText = readFile(screenDocRel);
const scrList = screenText.indexOf('## 1. 界面清单');
const scrBody = screenText.indexOf('\n## 2.', scrList);
const scrSection = scrList >= 0 && scrBody > scrList ? screenText.slice(scrList, scrBody) : '';
const scrRows = new Map();
for (const line of scrSection.split('\n')) {
  if (!line.trim().startsWith('|')) continue;
  const cells = line.split('|').map((c) => c.trim());
  if (/^SCR-\d{2}$/.test(cells[1] || '')) scrRows.set(cells[1], (scrRows.get(cells[1]) || 0) + 1);
}
for (const scr of scrSet) {
  const hits = scrRows.get(scr) || 0;
  if (hits !== 1) errors.push(`界面清单不符：${scr} 在 §1 清单表出现 ${hits} 次，应恰好 1 次`);
}
const tables = (screenText.match(/元素属性表/g) || []).length;
if (tables < 7) errors.push(`元素属性表不足：界面设计说明只有 ${tables} 处「元素属性表」，应 ≥ 7`);

// ---------- 5. 12 项专有面 ↔ ACC ↔ 矩阵 的交叉 ----------
const matrixAccs = new Set(rows.map((r) => r.acc));
for (const acc of accSet) if (!matrixAccs.has(acc)) errors.push(`交叉失败：${acc} 在 08 矩阵缺失，无法与专有面互指`);

for (const w of warnings) console.warn(`WARN ${w}`);
for (const e of errors) console.error(`ERROR ${e}`);
console.log(`check-coverage: 矩阵 ${rows.length} 行，ACC ${matrixAccs.size}/${accSet.size}，TG 使用 ${tgUsed.size}/${tgSet.size}，SCR 使用 ${scrUsed.size}/${scrSet.size}，${errors.length} 错误，${warnings.length} 警告`);
process.exit(errors.length ? 1 : 0);
