#!/usr/bin/env node
// 判据脚本公共库：参数解析、能力探测、读数写出、结果打印
// 约定见 08 §1.2（七字段）与 04 §9.3（IF-073 读数结构）。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
export const exists = (rel) => fs.existsSync(path.join(ROOT, rel));
export const abs = (rel) => path.join(ROOT, rel);

export function parseArgs(argv = process.argv.slice(2)) {
  const opts = {};
  const rest = [];
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next && !next.startsWith('--')) {
        opts[key] = next;
        i += 1;
      } else opts[key] = true;
    } else rest.push(a);
  }
  return { opts, rest };
}

// 从 08 §4 矩阵读 TG/SCR，避免脚本里硬编码
export function matrixRow(acc) {
  const doc = fs.readFileSync(abs('30-design/设计文档包/08-判据先行与界面检测设计说明.md'), 'utf8');
  const start = doc.indexOf('## 4.');
  const end = doc.indexOf('## 5.', start);
  for (const line of doc.slice(start, end).split('\n')) {
    if (!line.trim().startsWith('|')) continue;
    const c = line.split('|').map((x) => x.trim());
    if (c[1] === acc) return { acc, req: c[2], mods: c[3], tg: c[4], scr: c[5] };
  }
  return { acc, tg: '?', scr: '?' };
}

export function latestReadout(acc) {
  const dir = abs(`50-verify/读数/${acc}`);
  if (!fs.existsSync(dir)) return null;
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json') && !/^tree-/.test(f)).sort();
  if (!files.length) return null;
  const p = path.join(dir, files[files.length - 1]);
  try {
    return { file: path.relative(ROOT, p), data: JSON.parse(fs.readFileSync(p, 'utf8')) };
  } catch {
    return { file: path.relative(ROOT, p), data: { verdict: 'UNREADABLE' } };
  }
}

export function writeReadout({ acc, tg, verdict, diff = [], expectRef = '', env = {}, degrade = 'none', started, duration = 0 }) {
  const start = started ? new Date(started) : new Date();
  const ts = start.toISOString().replace(/[:.]/g, '-');
  const rc = verdict === 'FAIL' ? 1 : 0;
  const obj = {
    acc,
    tg,
    rc,
    verdict,
    started: start.toISOString(),
    duration_ms: duration,
    env,
    expect_ref: expectRef,
    diff,
    degrade,
  };
  const dir = abs(`50-verify/读数/${acc}`);
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${ts}-01.json`);
  fs.writeFileSync(file, JSON.stringify(obj, null, 2) + '\n', 'utf8');
  return { file, obj, rel: path.relative(ROOT, file) };
}

// 统一收尾：PASS→0；FAIL→1；SKIP/DEGRADED→0（但不算通过，diff 必写原因）
export function finish({ acc, verdict, diff = [], expectRef = '', env = {}, degrade = 'none', started = Date.now(), extra = {} }) {
  const { tg } = matrixRow(acc);
  const { file, rel } = writeReadout({
    acc,
    tg,
    verdict,
    diff,
    expectRef,
    env: { runner: 'node', cwd: path.basename(ROOT), ...env },
    degrade,
    started,
    duration: Date.now() - new Date(started).getTime(),
  });
  console.log(`[${acc}] ${tg} verdict=${verdict} diff=${diff.length}`);
  for (const d of diff.slice(0, 6)) console.log(`  - ${typeof d === 'string' ? d : JSON.stringify(d)}`);
  if (Object.keys(extra).length) console.log(`  ${JSON.stringify(extra)}`);
  console.log(`  读数：${rel}`);
  process.exit(verdict === 'FAIL' ? 1 : 0);
}

// 统一失败原因：缺被测对象（及其依赖切片）
export function missingObjects(list) {
  return list.map((m) => `缺被测对象：${m.path}（${m.why}）`);
}

export function probe(relPaths) {
  return relPaths.map((p) => ({ path: p, exists: exists(p) }));
}

export function which(bin) {
  return spawnSync('sh', ['-c', `command -v ${bin}`], { encoding: 'utf8' }).status === 0;
}

export function runCmd(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', timeout: opts.timeout || 300000, cwd: opts.cwd || ROOT });
  return { rc: r.status === null ? -1 : r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
}
