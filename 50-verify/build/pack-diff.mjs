#!/usr/bin/env node
// ACC-003：pack 解包清单与 FrameworkList 逐件 diff
import fs from 'node:fs';
import { finish, abs, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const fl = '40-build/pack/FrameworkList.xml';
const outDir = '40-build/pack/out';
if (!exists(fl)) {
  finish({ acc: 'ACC-003', started, verdict: 'FAIL', expectRef: 'ACC-003：pack 解包清单 diff 为空',
    diff: [`缺被测对象：${fl}（B3）`], env: {} });
}
const xml = fs.readFileSync(abs(fl), 'utf8');
const names = [...xml.matchAll(/<File [^>]*AssemblyName="([^"]+)"/g)].map((m) => m[1]);
const nupkgs = fs.existsSync(abs(outDir)) ? fs.readdirSync(abs(outDir)).filter((f) => f.endsWith('.nupkg')) : [];
const builtDir = '40-build/build/wpf/artifacts/bin';
const built = fs.existsSync(abs(builtDir)) ? fs.readdirSync(abs(builtDir)) : [];
const env = { framework_list_entries: names.length, pack_nupkgs: nupkgs.length, built_assemblies: built.length };

if (!nupkgs.length) {
  finish({ acc: 'ACC-003', started, verdict: 'FAIL', expectRef: 'ACC-003：pack 解包清单 diff 为空',
    env, diff: [`缺被测对象：${outDir}/*.nupkg（自产 Ref/Runtime pack，S1 pack 组装）`,
      `已具备：FrameworkList ${names.length} 条、派生树已构建程序集 ${built.length} 个`,
      `依赖切片：S1 pack 组装 → TG-002/ACC-003`] });
}
// 有 pack 时逐件 diff
const list = runCmd('unzip', ['-Z1', abs(`${outDir}/${nupkgs[0]}`)]);
const entries = list.out.split('\n').filter((l) => l.endsWith('.dll')).map((l) => l.split('/').pop().replace('.dll', ''));
const miss = names.filter((n) => !entries.includes(n));
const extra = entries.filter((e) => !names.includes(e));
finish({ acc: 'ACC-003', started, expectRef: 'ACC-003：pack 解包清单 diff 为空',
  env: { ...env, pack: nupkgs[0] }, verdict: miss.length || extra.length ? 'FAIL' : 'PASS',
  diff: [...miss.map((m) => `缺件：${m}`), ...extra.map((e) => `多件：${e}`)].slice(0, 10) });
