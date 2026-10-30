#!/usr/bin/env node
// 判据索引生成器：#索引生成器
// 从 08 §4 矩阵生成「ACC → 单行命令 → 读数 → 是否已落地」，索引表是 REQ-FN-029 的直接交付物。
// 用法：node 50-verify/index/gen-index.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const MATRIX_DOC = path.join(ROOT, '30-design/设计文档包/08-判据先行与界面检测设计说明.md');
const outIdx = process.argv.indexOf('--out');
const OUT = outIdx >= 0 && process.argv[outIdx + 1] ? path.resolve(ROOT, process.argv[outIdx + 1]) : path.join(HERE, '索引表.md');
const CHECK = process.argv.includes('--check');

const COMMANDS = {
  'ACC-001': 'node 50-verify/build/build-sample.mjs',
  'ACC-002': 'node 50-verify/build/build-sample.mjs --log',
  'ACC-003': 'node 50-verify/build/pack-diff.mjs',
  'ACC-004': 'node 50-verify/build/identity.mjs',
  'ACC-005': 'node 50-verify/build/regen-sha.mjs',
  'ACC-006': 'node 50-verify/render/duce-coverage.mjs',
  'ACC-007': 'node 50-verify/render/frame-probe.mjs --scr SCR-04 --frames 300',
  'ACC-008': 'node 50-verify/render/ae-compare.mjs --scene 1',
  'ACC-009': 'node 50-verify/window/lifecycle.mjs',
  'ACC-010': 'node 50-verify/window/replay.mjs --script 1',
  'ACC-011': 'node 50-verify/window/dpi.mjs --scale 1.5',
  'ACC-012': 'node 50-verify/window/clipboard.mjs',
  'ACC-013': 'node 50-verify/text/font-enum.mjs',
  'ACC-014': 'node 50-verify/text/shaping.mjs --script 1',
  'ACC-015': 'node 50-verify/text/linebreak.mjs',
  'ACC-016': 'node 50-verify/text/cff.mjs',
  'ACC-017': 'node 50-verify/ime/compose.mjs --case preedit',
  'ACC-018': 'node 50-verify/image/decode.mjs --format png',
  'ACC-019': 'node 50-verify/image/icc.mjs --profile srgb',
  'ACC-020': 'node 50-verify/theme/six-themes.mjs',
  'ACC-021': 'node 50-verify/theme/ribbon.mjs',
  'ACC-022': 'node 50-verify/media/playback.mjs --case play',
  'ACC-023': 'node 50-verify/ink/trace.mjs',
  'ACC-024': 'node 50-verify/print/cups-submit.mjs',
  'ACC-025': 'node 50-verify/doc/flowreader.mjs',
  'ACC-026': 'node 50-verify/compose/dwm-detect.mjs --session composite',
  'ACC-027': 'node 50-verify/a11y/assert-tree.mjs --scr SCR-01',
  'ACC-028': 'node 50-verify/winforms/six-controls.mjs',
  'ACC-029': 'node 50-verify/index/run-all.mjs --dry',
  'ACC-030': 'node 50-verify/degrade/audit.mjs',
};

function parseMatrix() {
  const text = fs.readFileSync(MATRIX_DOC, 'utf8');
  const start = text.indexOf('## 4.');
  const end = text.indexOf('## 5.', start);
  const rows = [];
  for (const line of text.slice(start, end).split('\n')) {
    if (!line.trim().startsWith('|')) continue;
    const c = line.split('|').map((x) => x.trim());
    if (!/^ACC-\d{3}$/.test(c[1] || '')) continue;
    rows.push({ acc: c[1], tg: c[4], scr: c[5] });
  }
  return rows;
}

function latestReadout(acc) {
  const dir = path.join(ROOT, '50-verify/读数', acc);
  if (!fs.existsSync(dir)) return null;
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort();
  if (!files.length) return null;
  const file = path.join(dir, files[files.length - 1]);
  try {
    const d = JSON.parse(fs.readFileSync(file, 'utf8'));
    return { file: path.relative(ROOT, file), verdict: d.verdict || '?', started: d.started || '?' };
  } catch {
    return { file: path.relative(ROOT, file), verdict: 'UNREADABLE', started: '?' };
  }
}

const rows = parseMatrix();
const lines = [];
lines.push('# 判据索引表（生成物）');
lines.push('');
lines.push('> 由 `node 50-verify/index/gen-index.mjs` 从 08 §4 矩阵生成，**勿手改**；`--check` 校验同步。');
lines.push('> 一条 ACC 一行：命令可复制即跑，读数落 `50-verify/读数/ACC-xxx/`；`SKIP` 不计通过（08 §1.5）。');
lines.push('');
lines.push('| ACC | 测试门 | 样本界面 | 单行命令 | 脚本 |');
lines.push('|---|---|---|---|---|');
let scriptOk = 0;
for (const r of rows) {
  const cmd = COMMANDS[r.acc] || '（未登记命令）';
  const scriptPath = cmd.split(' ')[1] || '';
  const hasScript = scriptPath && fs.existsSync(path.join(ROOT, scriptPath));
  if (hasScript) scriptOk += 1;
  lines.push(`| ${r.acc} | ${r.tg} | ${r.scr} | \`${cmd}\` | ${hasScript ? '有' : '待实现'} |`);
}
lines.push('');
lines.push(`**统计**：${rows.length} 条 ACC；脚本已落地 ${scriptOk}/${rows.length}。写「待实现」的行＝该判据脚本还没落地——按 ADR-014，先写脚本跑红，才允许写实现。`);
lines.push('');
lines.push('**读数不在本表**：执行结果落 `50-verify/读数/ACC-xxx/`（不入版本控制，见 08 §3.6）；本表只保证「每条 ACC 有一行可复制的命令」。');
lines.push('');
lines.push('**环境判据**：`node 50-verify/env/probe-env.mjs` 输出依赖矩阵读数（09 §3），不属于任何 ACC，但缺项会让所有判据一起红。');
lines.push('');
const out = lines.join('\n');

if (CHECK) {
  const cur = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : null;
  if (cur === out) {
    console.log(`gen-index: 比对一致（${rows.length} 行）`);
    process.exit(0);
  }
  console.error('ERROR 索引表与矩阵/读数不同步：重跑 node 50-verify/index/gen-index.mjs');
  process.exit(1);
}
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, out, 'utf8');
const readoutDirs = rows.filter((r) => fs.existsSync(path.join(ROOT, '50-verify/读数', r.acc))).length;
console.log(`gen-index: 写出 ${rows.length} 行；脚本已落地 ${scriptOk}，已有读数目录 ${readoutDirs} → ${path.relative(ROOT, OUT)}`);
