#!/usr/bin/env node
// ACC-027：界面结构判据——直接复用 snapshot.mjs（IF-071 实现）的读数
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, abs, exists, latestReadout } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-01';
const expectRel = `50-verify/a11y/expect/scr-${scr.replace(/^SCR-/, '')}.json`;
if (!exists(expectRel)) {
  finish({ acc: 'ACC-027', started, verdict: 'FAIL', expectRef: `ACC-027：${scr} 元素属性表四列吻合`,
    diff: [`缺被测对象：${expectRel}（界面图生成器 --emit-expect）`], env: { scr } });
}
const snapshot = abs('50-verify/a11y/snapshot.mjs');
const before = latestReadout('ACC-027');
const r = spawnSync('node', [snapshot, '--fake', '--scr', scr], { encoding: 'utf8', timeout: 180000 });
const after = latestReadout('ACC-027');
const env = { scr, snapshot_rc: r.status === null ? -1 : r.status, snapshot: after?.file, runner: 'fake' };
// fake 源只能证明「期望→桩→diff」通路，不能证明桥；按 08 §1.5，DEGRADED ≠ PASS
const verdict = after?.data?.verdict === 'PASS' ? 'DEGRADED' : after?.data?.verdict === 'FAIL' ? 'FAIL' : 'SKIP';
const diffs = after?.data?.diff?.map((d) => (typeof d === 'string' ? d : JSON.stringify(d))) || [];
finish({ acc: 'ACC-027', started, env, expectRef: `ACC-027：${scr} 节点 role/name/states/actions 与期望一致`,
  verdict, degrade: verdict === 'DEGRADED' ? 'fake 源只验结构数据通路；真桥（MOD-81）未实现（08 §1.5：DEGRADED≠PASS）' : 'none',
  diff: verdict === 'DEGRADED' || verdict === 'SKIP' ? [`snapshot 读数：${after?.file || '无'}`] : [...diffs.slice(0, 5), `snapshot 读数：${after?.file || '无'}`] });
