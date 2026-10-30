#!/usr/bin/env node
// ACC-030：缺陷册四要素核对（编号/触发条件/返回码/可见行为/判据/状态）
import fs from 'node:fs';
import { finish, abs, exists } from '../lib/run.mjs';

const started = Date.now();
const rel = '70-operate/已知坑清单.md';
if (!exists(rel)) {
  finish({ acc: 'ACC-030', started, verdict: 'FAIL', expectRef: 'ACC-030：缺陷册四要素齐全', diff: [`缺被测对象：${rel}`], env: {} });
}
const lines = fs.readFileSync(abs(rel), 'utf8').split('\n');
const rows = lines.filter((l) => /^\|\s*(D|K|X)-\d{3}\s*\|/.test(l)).map((l) => l.split('|').map((c) => c.trim()));
const ids = rows.map((r) => r[1]);
const bad = [];
for (const r of rows) {
  const id = r[1];
  const cells = r.slice(2, -1); // 去掉首尾空
  if (cells.length < 5) bad.push(`${id}：列数不足（${cells.length}）`);
  const [trigger, action, judge, status] = cells;
  if (!trigger) bad.push(`${id}：缺触发条件`);
  if (!action) bad.push(`${id}：缺返回码/行为`);
  if (!judge) bad.push(`${id}：缺判据`);
  if (!status) bad.push(`${id}：缺状态`);
}
const mustHave = ['D-001', 'D-002', 'K-001'];
const missingIds = mustHave.filter((m) => !ids.includes(m));
const env = { entries: rows.length, ids, required_missing: missingIds, violations: bad.length };
const pass = rows.length > 0 && bad.length === 0 && missingIds.length === 0;
finish({ acc: 'ACC-030', started, env, expectRef: 'ACC-030：每条降级四要素齐全、无未登记静默失败',
  verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [...bad.slice(0, 5), ...missingIds.map((m) => `缺登记：${m}`), '说明：本判据只核缺陷册结构；实现期新增降级须回填'] });
