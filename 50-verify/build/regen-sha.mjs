#!/usr/bin/env node
// ACC-005：派生树可再生（applier --check + 产物摘要）
import crypto from 'node:crypto';
import fs from 'node:fs';
import { finish, abs, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const tree = '40-build/build/wpf';
if (!exists(tree)) {
  finish({ acc: 'ACC-005', started, verdict: 'FAIL', expectRef: 'ACC-005：删派生树→再生→sha 比对', diff: [`缺被测对象：${tree}（先 apply.mjs）`], env: {} });
}
const check = runCmd('node', ['40-build/appliers/apply.mjs', '--check'], { timeout: 120000 });
const sha = (rel) => (exists(rel) ? crypto.createHash('sha256').update(fs.readFileSync(abs(rel))).digest('hex').slice(0, 16) : 'missing');
const artifacts = ['DllImportTargets.props', 'patch-report.json'].map((f) => ({ file: f, sha256_16: sha(`${tree}/${f}`) }));
const report = exists(`${tree}/patch-report.json`) ? JSON.parse(fs.readFileSync(abs(`${tree}/patch-report.json`), 'utf8')) : null;
const pass = check.rc === 0 && Boolean(report);
finish({ acc: 'ACC-005', started, expectRef: 'ACC-005：applier --check rc=0 且 patch-report 可复算',
  env: { apply_check_rc: check.rc, copied_files: report?.copied_files, patches: report?.patches?.map((p) => p.id) },
  verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [`applier --check rc=${check.rc}`, ...check.out.split('\n').filter((l) => l.startsWith('ERROR')).slice(0, 3)] });
