#!/usr/bin/env node
// 界面结构图生成器：#界面图生成器
// 从《界面设计说明》的元素属性表生成 SCR 结构图（SVG），保证「图与表同源」：表改则图跟着改，图旧即红。
// 用法：
//   node 界面图/build-wireframe.mjs            # 写 scr-01.svg ~ scr-07.svg
//   node 界面图/build-wireframe.mjs --check    # 与磁盘比对，不一致 rc=1
//   node 界面图/build-wireframe.mjs --out <dir> [--check]
// 解析口径：按未转义竖线切分（\| 视为单元格内竖线）、剥反引号与粗体、path 只收数字段。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DOC = path.resolve(HERE, '../界面设计说明.md');

const argv = process.argv.slice(2);
const checkOnly = argv.includes('--check');
const outIdx = argv.indexOf('--out');
const OUT = outIdx >= 0 && argv[outIdx + 1] ? path.resolve(HERE, argv[outIdx + 1]) : HERE;
const expectIdx = argv.indexOf('--expect-out');
const EXPECT_OUT = expectIdx >= 0 && argv[expectIdx + 1]
  ? path.resolve(HERE, argv[expectIdx + 1])
  : path.resolve(HERE, '../../../50-verify/a11y/expect');
const emitExpect = argv.includes('--emit-expect');
const srcSection = (n) => `界面设计说明 §${n}`;

const errors = [];

function esc(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function parseScreens(md) {
  const screens = [];
  const chunks = md.split(/^## /m).slice(1);
  for (const chunk of chunks) {
    const head = chunk.split('\n')[0].trim();
    const m = head.match(/^(\d+)\.\s+(SCR-\d{2})\s+(.+)$/);
    if (!m) continue;
    const lines = chunk.split('\n');
    let header = null;
    const rows = [];
    for (const line of lines) {
      if (!line.trim().startsWith('|')) {
        if (header && rows.length) break;
        continue;
      }
      // 按未转义的竖线切分；单元格内的 \| 还原为 |；去掉 Markdown 反引号与粗体标记
      const cells = line.split(/(?<!\\)\|/).map((c) => c.replace(/\\\|/g, '|').replace(/`/g, '').replace(/\*\*/g, '').trim());
      const cols = cells.slice(1, cells.length - 1);
      if (!header) {
        const has = (k) => cols.some((c) => c.replace(/[`*]/g, '').trim().startsWith(k));
        if (has('路径') && has('角色') && has('判据')) header = cols;
        continue;
      }
      if (cols.every((c) => /^:?-{2,}:?$/.test(c) || c === '')) continue;
      if (cols.length < 7) continue;
      rows.push({ element: cols[0], p: cols[1], role: cols[2], name: cols[3], states: cols[4], actions: cols[5], acc: cols[6] });
    }
    const nodes = rows.filter((r) => /^\d+(\/\d+)*$/.test(r.p.replace(/`/g, '')));
    screens.push({ no: Number(m[1]), id: m[2], title: m[3].replace(/`/g, ''), section: Number(m[1]), header, nodes });
  }
  return screens;
}

function render(scr) {
  const rowH = 30;
  const headH = 78;
  const padTop = 18;
  const width = 860;
  const height = headH + scr.nodes.length * rowH + 60;
  const maxDepth = Math.max(1, ...scr.nodes.map((n) => n.p.split('/').length));
  const indent = 24;
  const boxLeft = 26;
  const boxRight = width - 26;
  const parts = [];

  parts.push(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" font-family="Noto Sans CJK SC, Source Han Sans SC, sans-serif">`);
  parts.push(`<rect width="${width}" height="${height}" fill="#ffffff"/>`);
  parts.push(`<text x="${boxLeft}" y="34" font-size="20" font-weight="700" fill="#111">${esc(scr.id)} ${esc(scr.title)}</text>`);
  parts.push(`<text x="${boxLeft}" y="58" font-size="12" fill="#555">由《界面设计说明》§${scr.section} 元素属性表生成：${scr.nodes.length} 个可检测节点，${maxDepth} 层；左侧缩进表示 path 层级，右侧为 role · path · 状态/动作数</text>`);
  parts.push(`<text x="${boxLeft}" y="74" font-size="11" fill="#8a8a8a">路径即结构判据的 join 键；图与表不一致时以表为准并重跑本脚本（--check 会红）</text>`);

  // 竖向层级线
  for (let d = 2; d <= maxDepth; d += 1) {
    const x = boxLeft + 10 + (d - 2) * indent;
    parts.push(`<line x1="${x}" y1="${headH - 4}" x2="${x}" y2="${headH + scr.nodes.length * rowH - 6}" stroke="#e2e2e2" stroke-width="1" stroke-dasharray="3 4"/>`);
  }

  scr.nodes.forEach((n, i) => {
    const depth = n.p.split('/').length;
    const x = boxLeft + (depth - 1) * indent;
    const y = headH + i * rowH;
    const w = boxRight - x;
    const focus = /push button|entry|check box|radio button|slider/.test(n.role);
    const fill = focus ? '#eef4ff' : '#f7f7f7';
    const stroke = focus ? '#7aa2e8' : '#d0d0d0';
    const stateCount = n.states === '—' ? 0 : n.states.split(/[、,]/).filter((s) => s.trim()).length;
    const actionCount = n.actions === '—' ? 0 : n.actions.split(/[、,]/).filter((s) => s.trim()).length;
    const accs = [...new Set((n.acc.match(/ACC-\d{3}/g) || []))].join(' ');
    parts.push(`<g data-path="${esc(n.p)}" data-role="${esc(n.role)}" data-name="${esc(n.name)}">`);
    parts.push(`<rect x="${x}" y="${y}" width="${w}" height="24" rx="3" fill="${fill}" stroke="${stroke}"/>`);
    parts.push(`<text x="${x + 8}" y="${y + 16}" font-size="12" fill="#111">${esc(n.element)}</text>`);
    parts.push(`<text x="${boxRight - 8}" y="${y + 16}" font-size="10" fill="#666" text-anchor="end">${esc(n.role)} · ${esc(n.p)} · 状态 ${stateCount} / 动作 ${actionCount}${accs ? ` · ${esc(accs)}` : ''}</text>`);
    parts.push('</g>');
  });

  parts.push(`<text x="${boxLeft}" y="${height - 24}" font-size="10" fill="#8a8a8a">蓝框＝可交互节点（Button/Entry/Check/Radio/Slider 类）；全部节点须能在 AT-SPI 树快照中按 path 命中（TG-012 / ACC-027）</text>`);
  parts.push('</svg>');
  return parts.join('\n') + '\n';
}

function renderExpect(scr) {
  const split = (v) => (v === '—' ? [] : v.split(/[、,]/).map((x) => x.trim()).filter(Boolean));
  return JSON.stringify(
    {
      scr: scr.id,
      source: srcSection(scr.section),
      generated_from: '界面图/build-wireframe.mjs --emit-expect（与结构图读同一张表）',
      nodes: scr.nodes.map((n) => ({
        path: n.p,
        role: n.role,
        name: n.name,
        states: split(n.states),
        actions: split(n.actions),
        acc: [...new Set(n.acc.match(/ACC-\d{3}/g) || [])],
        // IF-071 必填字段：元素属性表不给几何与值，这里显式置空，避免「缺字段」被判成字段缺失
        bounds: null,
        value: null,
        relations: [],
      })),
    },
    null,
    2,
  ) + '\n';
}

if (!fs.existsSync(DOC)) {
  console.error(`ERROR 缺少《界面设计说明》：${DOC}`);
  process.exit(1);
}
const md = fs.readFileSync(DOC, 'utf8');
const screens = parseScreens(md);

if (screens.length === 0) errors.push('未能从《界面设计说明》解析出任何 SCR 元素属性表');
for (const scr of screens) {
  if (!scr.header) errors.push(`${scr.id} 找不到含「路径/角色/判据」的表头，无法生成结构图`);
  if (scr.nodes.length === 0) errors.push(`${scr.id} 没有可检测节点（path 为数字段的行）`);
}

fs.mkdirSync(OUT, { recursive: true });
let changed = 0;
let expectChanged = 0;
for (const scr of screens) {
  const num = scr.id.slice(4);
  const pairs = [
    { target: path.join(OUT, `scr-${num}.svg`), content: render(scr), label: `结构图 scr-${num}.svg`, kind: 'svg' },
    { target: path.join(EXPECT_OUT, `scr-${num}.json`), content: renderExpect(scr), label: `期望文件 scr-${num}.json`, kind: 'expect' },
  ];
  for (const p of pairs) {
    if (p.kind === 'expect' && !checkOnly && !emitExpect) continue;
    if (checkOnly) {
      if (!fs.existsSync(p.target)) {
        errors.push(`${p.label} 缺失：${path.relative(process.cwd(), p.target)}（${p.kind === 'svg' ? '跑一次生成器' : '加 --emit-expect'}）`);
      } else if (fs.readFileSync(p.target, 'utf8') !== p.content) {
        errors.push(`${p.label} 与元素属性表不同步（重跑生成器${p.kind === 'expect' ? ' --emit-expect' : ''}）`);
      }
    } else {
      fs.mkdirSync(path.dirname(p.target), { recursive: true });
      const before = fs.existsSync(p.target) ? fs.readFileSync(p.target, 'utf8') : null;
      fs.writeFileSync(p.target, p.content, 'utf8');
      if (before !== p.content) {
        if (p.kind === 'svg') changed += 1;
        else expectChanged += 1;
      }
    }
  }
}

const total = screens.reduce((n, s) => n + s.nodes.length, 0);
for (const e of errors) console.error(`ERROR ${e}`);
const mode = checkOnly
  ? '比对模式（结构图 + 期望文件）'
  : `写出 ${changed} 张结构图${emitExpect ? `、${expectChanged} 份期望文件` : ''}`;
console.log(`build-wireframe: ${screens.length} 张结构图，${total} 个节点，${mode}，${errors.length} 错误`);
process.exit(errors.length ? 1 : 0);
