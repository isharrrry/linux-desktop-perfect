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
// 缺陷条目 = 每个编号的**首次出现**（定义行）；同编号后续再出现（附录/引用表）不算条目
const seen = new Set();
const rows = [];
for (const l of lines) {
  const m = l.match(/^\|\s*((?:D|K|X)-\d{3,4})\s*\|/);
  if (!m || seen.has(m[1])) continue;
  seen.add(m[1]);
  rows.push(l.split('|').map((c) => c.trim()));
}
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
// K-1323：K 号唯一性检查（只查正文条目；引用表行第 2 列是短值，不算正文）
const looksBody = (l) => {
  if (!/^\|\s*K-\d{3,4}\s*\|/.test(l)) return false;
  // 正文行满 5 列（至少 6 个管道）且第 2 列不是短引用值
  const cells = l.split('|');
  return cells.length >= 6 && (cells[2] || '').trim().length > 20;
};
const kIds = lines.filter(looksBody).map((l) => l.split('|')[1].trim());
const dupK = [...new Set(kIds.filter((v, i, arr) => arr.indexOf(v) !== i))];
for (const d of dupK) bad.push(`K 号撞车：${d}`);

const mustHave = ['D-001', 'D-002', 'K-001'];
const missingIds = mustHave.filter((m) => !ids.includes(m));
const env = { entries: rows.length, body_entries: kIds.length, dup_k: dupK.length, ids, required_missing: missingIds, violations: bad.length };
const pass = rows.length > 0 && bad.length === 0 && missingIds.length === 0;
finish({ acc: 'ACC-030', started, env, expectRef: 'ACC-030：每条降级四要素齐全、无未登记静默失败',
  verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [...bad.slice(0, 5), ...missingIds.map((m) => `缺登记：${m}`), '说明：本判据只核缺陷册结构；实现期新增降级须回填'] });
