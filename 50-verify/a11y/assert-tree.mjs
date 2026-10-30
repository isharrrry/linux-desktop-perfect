#!/usr/bin/env node
// ACC-027：界面结构判据（MOD-86 / IF-071）—— 接 snapshot.mjs 的读数
//
// 三步：
//   ①判据自检（snapshot --selftest）：各差异类别的负向断言必须全过，否则判据本身不可信，判 FAIL；
//   ②取树并 diff（--fake 源）：role/name/states/actions/relations/child_count 逐列比，bounds 单列；
//   ③判定：fake 源只能证明「期望→桩→diff」通路，不能证明真桥，按 08 §1.5 判 DEGRADED（≠PASS）。
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, abs, exists, latestReadout } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-01';
const expectRel = `50-verify/a11y/expect/scr-${scr.replace(/^SCR-/, '')}.json`;
if (!exists(expectRel)) {
  finish({ acc: 'ACC-027', started, verdict: 'FAIL', expectRef: `ACC-027：${scr} 元素属性表逐列吻合`,
    diff: [`缺被测对象：${expectRel}（界面图生成器 --emit-expect）`], env: { scr } });
}
const snapshot = abs('50-verify/a11y/snapshot.mjs');

// ①判据自检
const self = spawnSync('node', [snapshot, '--selftest'], { encoding: 'utf8', timeout: 120000 });
const selfOut = (self.stdout || '').trim().split('\n');
const selfLine = selfOut[selfOut.length - 1] || '';
if (self.status !== 0) {
  finish({
    acc: 'ACC-027', started, verdict: 'FAIL',
    expectRef: 'ACC-027：元素属性表逐列吻合（含 relations/child_count，bounds 单列）',
    env: { scr, selftest_rc: self.status, selftest: selfLine },
    diff: ['判据自检失败（snapshot --selftest 未全过），读数不可信', ...selfOut.filter((l) => l.includes('FAIL')).slice(0, 5)],
  });
}

// ②取树并 diff
const r = spawnSync('node', [snapshot, '--fake', '--scr', scr], { encoding: 'utf8', timeout: 180000 });
const after = latestReadout('ACC-027');
const env = {
  scr,
  selftest: selfLine,
  snapshot_rc: r.status === null ? -1 : r.status,
  snapshot: after?.file,
  runner: 'fake',
  bounds_diff: (after?.data?.bounds_diff || []).length,
};
// ③判定：DEGRADED 不算通过
const verdict = after?.data?.verdict === 'PASS' ? 'DEGRADED' : after?.data?.verdict === 'FAIL' ? 'FAIL' : 'SKIP';
const diffs = after?.data?.diff?.map((d) => (typeof d === 'string' ? d : JSON.stringify(d))) || [];
finish({
  acc: 'ACC-027', started, env,
  expectRef: 'ACC-027：节点 role/name/states/actions/relations/child_count 与期望一致',
  verdict,
  degrade: verdict === 'DEGRADED' ? 'fake 源只验结构数据通路；真桥（MOD-81）未实现（08 §1.5：DEGRADED≠PASS）' : 'none',
  diff: verdict === 'DEGRADED' || verdict === 'SKIP'
    ? [`snapshot 读数：${after?.file || '无'}`]
    : [...diffs.slice(0, 5), `snapshot 读数：${after?.file || '无'}`],
});
