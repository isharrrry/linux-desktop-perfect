#!/usr/bin/env node
// ACC-029：索引表逐条可执行（dry：只查索引一致性与脚本落地率，不执行 30 条真判据）
import fs from 'node:fs';
import { finish, abs, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const idx = '50-verify/index/索引表.md';
if (!exists(idx)) {
  finish({ acc: 'ACC-029', started, verdict: 'FAIL', expectRef: 'ACC-029：索引表逐条执行 + 落盘', diff: [`缺被测对象：${idx}`], env: {} });
}
const check = runCmd('node', ['50-verify/index/gen-index.mjs', '--check'], { timeout: 120000 });
const text = fs.readFileSync(abs(idx), 'utf8');
const rows = [...text.matchAll(/^\|\s*(ACC-\d{3})\s*\|/gm)].map((m) => m[1]);
const cmds = [...text.matchAll(/`((?:node|python3) [^`]+)`/g)].map((m) => m[1]);
const scripts = cmds.map((c) => c.split(/\s+/)[1]);
const missingScripts = scripts.filter((s) => !exists(s));
const readoutDirs = fs.existsSync(abs('50-verify/读数')) ? fs.readdirSync(abs('50-verify/读数')).filter((d) => /^ACC-\d{3}$/.test(d)).length : 0;
const env = { rows: rows.length, commands: cmds.length, scripts_missing: missingScripts.length, readout_dirs: readoutDirs, index_check_rc: check.rc };
const pass = check.rc === 0 && rows.length === 31 && missingScripts.length === 0;
finish({ acc: 'ACC-029', started, env, expectRef: 'ACC-029：索引表 30 行、逐条可执行、读数落盘',
  verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [`索引一致性 rc=${check.rc}`, `缺脚本：${missingScripts.slice(0, 5).join(', ') || '无'}`, `行数=${rows.length}（期望 30）`] });
