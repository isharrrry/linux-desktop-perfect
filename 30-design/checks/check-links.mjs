#!/usr/bin/env node
// 链接检查器：#链接检查器
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

// 把代码块与行内代码替换为等长空格，保留换行与行号
function stripCode(text) {
  return text
    .replace(/```[\s\S]*?```/g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/`[^`\n]*`/g, (m) => m.replace(/[^\n]/g, ' '));
}

function lineOf(text, index) {
  return text.slice(0, index).split('\n').length;
}

// 文档名 ↔ 路径登记表：可阅读文档的《》名称必须与 #名称清单 一致
const names = JSON.parse(fs.readFileSync(path.join(ROOT, '30-design/model/names.json'), 'utf8'));
const docNameToPath = new Map();
for (const [name, rel] of Object.entries(names.documents)) {
  docNameToPath.set(name, path.resolve(ROOT, rel));
}

const files = walk(ROOT);
for (const file of files) {
  const rel = path.relative(ROOT, file);
  const raw = fs.readFileSync(file, 'utf8');
  const stripped = stripCode(raw);

  // 1. 链接目标
  const linkRe = /\[[^\]]*\]\(([^)]*)\)/g;
  let m;
  while ((m = linkRe.exec(stripped))) {
    const target = m[1];
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    const line = lineOf(stripped, m.index);
    if (/\s/.test(target)) { errors.push(`${rel}:${line}: 链接目标含空白 -> ${target}`); continue; }
    if (target.includes('\\')) errors.push(`${rel}:${line}: 链接含反斜杠 -> ${target}`);
    else if (target.startsWith('/') || target.startsWith('~')) errors.push(`${rel}:${line}: 链接是绝对路径 -> ${target}`);
    else if (target.includes('file://')) errors.push(`${rel}:${line}: 链接含 file:// -> ${target}`);
    else if (target.includes('<') || target.includes('>')) errors.push(`${rel}:${line}: 链接含尖括号 -> ${target}`);
    else {
      const clean = target.split('#')[0];
      if (!clean) continue;
      const resolved = path.resolve(path.dirname(file), clean);
      if (!fs.existsSync(resolved)) errors.push(`${rel}:${line}: 链接目标不存在 -> ${target}`);
    }
  }

  // 2. 裸 .md 路径（先去掉链接目标，避免把链接 URL 当裸路径）
  const noLinks = stripped.replace(/\]\([^)]+\)/g, ']');
  const bareRe = /(?:\.{1,2}\/)?(?:[\p{L}\p{N}_.-]+\/)+[\p{L}\p{N}_.-]+\.md/gu;
  while ((m = bareRe.exec(noLinks))) {
    const p = m[0];
    const resolvedLocal = path.resolve(path.dirname(file), p);
    const resolvedRoot = path.resolve(ROOT, p);
    if (fs.existsSync(resolvedLocal) || fs.existsSync(resolvedRoot)) {
      const line = lineOf(noLinks, m.index);
      errors.push(`${rel}:${line}: 裸 .md 路径未用《》链接 -> ${p}`);
    }
  }

  // 3. 裸行号 L####
  const linenoRe = /\bL\d{2,}\b/g;
  while ((m = linenoRe.exec(stripped))) {
    const line = lineOf(stripped, m.index);
    errors.push(`${rel}:${line}: 裸行号 ${m[0]}（改用章节/编号/简述）`);
  }

  // 4. 《》文档名必须与 #名称清单 登记一致
  const labeledRe = /\[(《[^》]+》)\]\(([^)]*)\)/g;
  while ((m = labeledRe.exec(stripped))) {
    const name = m[1].slice(1, -1);
    const target = m[2];
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    const line = lineOf(stripped, m.index);
    const registered = docNameToPath.get(name);
    if (!registered) {
      errors.push(`${rel}:${line}: 未登记文档名：${m[1]} 不在 #名称清单 documents 里（改名字或先登记）`);
      continue;
    }
    const resolved = path.resolve(path.dirname(file), target.split('#')[0]);
    if (resolved !== registered) {
      errors.push(`${rel}:${line}: 文档名与链接不符：${m[1]} 应链 ${path.relative(ROOT, registered)}，实际链 ${target}`);
    }
  }

  // 5. 名称提示（仅警告）：首选名缺失
  if (rel === 'README.md' && !raw.includes('LinuxDesktopPerfect')) {
    warnings.push(`${rel}: 入口未出现首选名 LinuxDesktopPerfect`);
  }
}

for (const w of warnings) console.warn(`WARN ${w}`);
for (const e of errors) console.error(`ERROR ${e}`);
console.log(`check-links: ${files.length} 个文件，${errors.length} 错误，${warnings.length} 警告`);
process.exit(errors.length ? 1 : 0);
