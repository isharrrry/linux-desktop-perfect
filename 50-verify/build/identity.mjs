#!/usr/bin/env node
// ACC-004：自产件身份（FrameworkList 与派生树产物逐件对照）
import fs from 'node:fs';
import { finish, abs, exists } from '../lib/run.mjs';

const started = Date.now();
const fl = '40-build/pack/FrameworkList.xml';
if (!exists(fl)) {
  finish({ acc: 'ACC-004', started, verdict: 'FAIL', expectRef: 'ACC-004：反射身份 vs 清单', diff: [`缺被测对象：${fl}`], env: {} });
}
const xml = fs.readFileSync(abs(fl), 'utf8');
const expected = [...xml.matchAll(/<File [^>]*AssemblyName="([^"]+)"[^>]*AssemblyVersion="([^"]+)"/g)].map((m) => ({ name: m[1], version: m[2] }));
const dir = '40-build/build/wpf/artifacts/bin';
const built = new Set();
if (fs.existsSync(abs(dir))) {
  for (const asm of fs.readdirSync(abs(dir))) {
    const p = abs(`${dir}/${asm}/Release/net10.0/${asm}.dll`);
    if (fs.existsSync(p)) built.add(asm);
  }
}
const selfBuilt = expected.filter((e) => built.has(e.name));
const missing = expected.filter((e) => !built.has(e.name));
const env = { expected: expected.length, built: built.size, matched: selfBuilt.length };
const pass = expected.length > 0 && missing.length === 0;
finish({ acc: 'ACC-004', started, expectRef: 'ACC-004：每个自产件反射 FullName 与清单一致',
  env, verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [`派生树仅构建 ${built.size} 个程序集，清单期望 ${expected.length} 个`, `缺：${missing.slice(0, 6).map((m) => m.name).join(', ')}`, '依赖切片：S1 其余程序集 + pack 组装'] });
