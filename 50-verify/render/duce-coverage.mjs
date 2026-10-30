#!/usr/bin/env node
// ACC-006：DUCE 118 条命令覆盖（读 DUCE-LAYOUT.csv 的 实现状态 列）
import fs from 'node:fs';
import { parseArgs, finish, abs, exists } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const csvRel = opts.csv || '40-build/milcore/DUCE-LAYOUT.csv';
if (!exists(csvRel)) {
  finish({ acc: 'ACC-006', started, verdict: 'FAIL', expectRef: 'ACC-006：命令覆盖表 118/118',
    diff: [`缺被测对象：${csvRel}（B3）`], env: {} });
}
const lines = fs.readFileSync(abs(csvRel), 'utf8').split('\n').filter((l) => l.trim() && !l.startsWith('#'));
const head = lines[0].split(',').map((s) => s.trim());
const rows = lines.slice(1).map((l) => {
  const cells = l.split(',');
  const o = {};
  head.forEach((h, i) => { o[h] = (cells[i] || '').trim().replace(/^"|"$/g, ''); });
  return o;
});
const statusKey = head.includes('实现状态') ? '实现状态' : head.find((h) => /status/i.test(h));
const counts = {};
for (const r of rows) counts[r[statusKey] || '(空)'] = (counts[r[statusKey] || '(空)'] || 0) + 1;
// 口径：CSV 的 `impl` 表示「布局已解析」，不等于「渲染已实现」。真实现信号是 milcore 已编译。
const dispatcher = '40-build/milcore/bin/libwpfgfx_linux.so';
const compiled = exists(dispatcher);
const layoutParsed = (counts.impl || 0) + (counts['已实现'] || 0);
const done = compiled ? layoutParsed : 0;
const env = { rows: rows.length, status_counts: counts, source: csvRel, layout_parsed: layoutParsed, milcore_compiled: compiled };
const pass = rows.length === 118 && done === 118;
finish({ acc: 'ACC-006', started, expectRef: 'ACC-006：命令覆盖表 118/118 有实现或显式降级',
  env, verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [
    `覆盖 ${done}/${rows.length}；CSV 记 ${layoutParsed} 条「布局已解析」（≠ 已实现）`,
    compiled ? 'milcore 已编译但无覆盖读数' : `缺实现信号：${dispatcher} 未编译（S1）`,
    '依赖切片：S1 首批命令实现 → S3 全量 118 条',
  ] });
