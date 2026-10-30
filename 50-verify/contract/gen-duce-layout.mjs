#!/usr/bin/env node
// DUCE 命令布局表生成器（TBL-003 / IF-011 的机械侧）
//
// 生成 40-build/milcore/DUCE-LAYOUT.csv。列（固定顺序，供 04 §3.1 与 05 §3.3 消费）：
//   id,名称,参数结构体,字节长度,实现状态,降级编号,来源
//
// ── 权威来源（按优先级）───────────────────────────────────────────────────
//   1. 40-build/milcore/DUCE-COMMANDS.txt          命令集合（118 行，id + 名）
//   2. 40-build/milcore/Commands/MilCommandLayout.cs  FixedSize 定长表 + HasVariablePayload 变长集合
//                                                    + s_notImpl（E_NOTIMPL 登记）
//   3. 40-build/milcore/Commands/MilCommandStructs.cs 参数结构体（MILCMD_*）+ 字段偏移
//   4. 40-build/milcore/Contracts/MilCmd.cs          枚举值（与 1 交叉校验）
//
// ── 禁止事项 ──────────────────────────────────────────────────────────────
//   解析不出结构体名、或确定不了字节长度，就留空并在「来源」写「待对拍确认」。
//   **不按字段宽度自行相加**：上游结构体是 Pack=1 + FieldOffset 显式布局，宽度必须由
//   FixedSize 表或编译期 Marshal.SizeOf 给出（见 MilCommandLayout.cs 头注释）。
//
// 用法：node 50-verify/contract/gen-duce-layout.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const MIL = path.join(ROOT, '40-build/milcore');
const OUT = path.join(MIL, 'DUCE-LAYOUT.csv');
const INPUTS = {
  commands: path.join(MIL, 'DUCE-COMMANDS.txt'),
  layout: path.join(MIL, 'Commands/MilCommandLayout.cs'),
  structs: path.join(MIL, 'Commands/MilCommandStructs.cs'),
  enum: path.join(MIL, 'Contracts/MilCmd.cs'),
};

const errors = [];
const warnings = [];
const read = (p) => fs.readFileSync(p, 'utf8');

// 词法级剥离注释（同 gen-exports 的理由：// 注释里的 /* 会毁掉正则）
function stripComments(text) {
  const out = [];
  let i = 0;
  let state = 'code';
  while (i < text.length) {
    const c = text[i];
    const n = text[i + 1];
    if (state === 'code') {
      if (c === '/' && n === '/') { state = 'line'; out.push('  '); i += 2; continue; }
      if (c === '/' && n === '*') { state = 'block'; out.push('  '); i += 2; continue; }
      if (c === '"') { state = 'str'; out.push(c); i += 1; continue; }
      if (c === "'") { state = 'chr'; out.push(c); i += 1; continue; }
      out.push(c); i += 1; continue;
    }
    if (state === 'line') {
      if (c === '\n') { state = 'code'; out.push('\n'); } else out.push(' ');
      i += 1; continue;
    }
    if (state === 'block') {
      if (c === '*' && n === '/') { state = 'code'; out.push('  '); i += 2; continue; }
      out.push(c === '\n' ? '\n' : ' ');
      i += 1; continue;
    }
    if (c === '\\') { out.push('  '); i += 2; continue; }
    if ((state === 'str' && c === '"') || (state === 'chr' && c === "'")) state = 'code';
    out.push(c); i += 1;
  }
  return out.join('');
}

// ---------- 1. 命令集合 ----------
const commands = [];
for (const line of read(INPUTS.commands).split('\n')) {
  const m = line.match(/^\s*(0x[0-9a-fA-F]+)\s+(\w+)\s*$/);
  if (!m) continue;
  commands.push({ hex: m[1].toLowerCase().replace('0x', '0x'), dec: parseInt(m[1], 16), name: m[2] });
}
if (commands.length !== 118) errors.push(`命令集合应为 118 行，实际 ${commands.length}`);

// ---------- 2. 枚举交叉校验 ----------
const enumText = stripComments(read(INPUTS.enum));
const enumMap = new Map();
for (const m of enumText.matchAll(/\b(MilCmd\w+)\s*=\s*0x([0-9a-fA-F]+)/g)) {
  enumMap.set(m[1], parseInt(m[2], 16));
}
for (const c of commands) {
  const v = enumMap.get(c.name);
  if (v === undefined) errors.push(`枚举里没有 ${c.name}（DUCE-COMMANDS.txt 与 MilCmd.cs 不一致）`);
  else if (v !== c.dec) errors.push(`id 不一致：${c.name} 清单=${c.hex} 枚举=0x${v.toString(16)}`);
}

// ---------- 3. 定长 / 变长 / NotImpl ----------
const layoutText = stripComments(read(INPUTS.layout));
const fixed = new Map();
for (const m of layoutText.matchAll(/MilCmd\.(\w+)\s*=>\s*(\d+)/g)) fixed.set(m[1], Number(m[2]));

const varStart = layoutText.indexOf('HasVariablePayload');
const variable = new Set();
if (varStart >= 0) {
  for (const m of layoutText.slice(varStart).matchAll(/MilCmd\.(\w+)\s*=>\s*true/g)) variable.add(m[1]);
} else {
  warnings.push('MilCommandLayout.cs 里找不到 HasVariablePayload，变长集合按空处理');
}

const notImpl = new Set();
const niStart = layoutText.indexOf('s_notImpl');
if (niStart >= 0) {
  const niEnd = layoutText.indexOf('NotImplCount', niStart);
  for (const m of layoutText.slice(niStart, niEnd).matchAll(/MilCmd\.(\w+)/g)) notImpl.add(m[1]);
}

// ---------- 4. 参数结构体 ----------
const structText = stripComments(read(INPUTS.structs));
const structFields = new Map(); // MILCMD_X -> 字段数
for (const m of structText.matchAll(/struct\s+(MILCMD_\w+)([\s\S]*?)\n\s*\}/g)) {
  const fields = [...m[2].matchAll(/\[FieldOffset\(\s*\d+\s*\)\]/g)].length;
  structFields.set(m[1], fields);
}
const structByKey = new Map();
for (const name of structFields.keys()) {
  structByKey.set(name.replace(/^MILCMD_/, '').replace(/_/g, '').toUpperCase(), name);
}

// ---------- 5. 组装 118 行 ----------
const rows = [];
for (const c of commands) {
  const key = c.name.replace(/^MilCmd/, '').toUpperCase();
  const struct = structByKey.get(key) || '';
  const len = fixed.get(c.name);
  const isVar = variable.has(c.name);
  const isGap = notImpl.has(c.name);
  const sentinel = c.dec === 0x00;

  const source = [];
  if (struct) source.push(`MilCommandStructs.cs:${struct}(${structFields.get(struct)} 字段)`);
  else source.push('无参数结构体');
  if (len !== undefined) source.push('MilCommandLayout.cs:FixedSize');
  else source.push('长度待对拍确认');
  if (isVar) source.push('HasVariablePayload=变长（长度为固定头）');

  let status = 'impl';
  let degrade = '—';
  if (sentinel) {
    status = 'gap';
    degrade = '—（哨兵命令，不参与解码）';
    source.push('0x00 哨兵');
  } else if (isGap) {
    status = 'gap';
    degrade = '待登记（缺陷册未分配 D- 编号）';
    source.push('s_notImpl（E_NOTIMPL）');
  }
  if (!struct && !sentinel) source.push('参数结构体待对拍确认');
  if (len === undefined && !sentinel) source.push('字节长度待对拍确认');

  rows.push({
    id: c.hex,
    dec: c.dec,
    name: c.name,
    struct,
    len: len === undefined ? '' : String(len),
    status,
    degrade,
    source: source.join('；'),
  });
}

rows.sort((a, b) => a.dec - b.dec);
if (rows.length !== 118) errors.push(`输出行数应为 118，实际 ${rows.length}`);

const header = [
  '# DUCE 命令布局表（TBL-003 机械侧）',
  '# 生成器：50-verify/contract/gen-duce-layout.mjs（勿手改）',
  '# 列：id,名称,参数结构体,字节长度,实现状态,降级编号,来源',
  '# 字节长度空 = 待对拍确认（见来源列）；变长命令的长度是固定头，尾部单独处理',
  `# 规模：${rows.length} 行；定长 ${rows.filter((r) => r.len !== '').length}、变长 ${variable.size}、gap ${rows.filter((r) => r.status === 'gap').length}`,
];
function csvCell(v) {
  const s = String(v);
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}
const csv = [...header, 'id,名称,参数结构体,字节长度,实现状态,降级编号,来源']
  .concat(rows.map((r) => [r.id, r.name, r.struct, r.len, r.status, r.degrade, r.source].map(csvCell).join(',')))
  .join('\n') + '\n';

if (process.argv.includes('--check')) {
  const prev = fs.existsSync(OUT) ? read(OUT) : '';
  if (prev !== csv) {
    console.error(`ERROR DUCE-LAYOUT.csv 与生成结果不一致；重跑生成器`);
    process.exit(1);
  }
  console.log(`gen-duce-layout: --check 通过（${rows.length} 行）`);
} else {
  fs.writeFileSync(OUT, csv, 'utf8');
  const known = rows.filter((r) => r.len !== '').length;
  console.log(`gen-duce-layout: 写出 ${rows.length} 行；定长 ${known}、无长度 ${rows.length - known}、gap ${rows.filter((r) => r.status === 'gap').length}`);
}

for (const w of warnings) console.warn(`WARN ${w}`);
if (errors.length) {
  for (const e of errors.slice(0, 20)) console.error(`ERROR ${e}`);
  process.exit(1);
}
