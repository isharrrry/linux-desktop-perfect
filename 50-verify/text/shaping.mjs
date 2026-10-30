#!/usr/bin/env node
// ACC-014：HarfBuzz shaping（MOD-32，TG-005）
//
// 判据口径（双路径对拍，08 §1.5「不许自证」）：
//   ①被测件：40-build/text-stack/bin/libdwlinux.so 经 bin/dwp-probe --shape 输出字形 id / 步进 / cluster；
//   ②基准：50-verify/text/hb-oracle.py（Python ctypes 直接调系统 libharfbuzz），与①不共享代码；
//   ③三组用例（拉丁 / 复杂脚本 / CJK）逐组比较 glyphs 与 advances，任一不等即 FAIL；
//   ④探针或基准跑不起来如实判红，不判 PASS（ADR-014）。
//
// 用法：node 50-verify/text/shaping.mjs [--script 1] [--case latin|complex|cjk]

import { parseArgs, finish, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const SO = '40-build/text-stack/bin/libdwlinux.so';
const PROBE = '40-build/text-stack/bin/dwp-probe';
const ORACLE = '50-verify/text/hb-oracle.py';

const PROBES = [
  { path: SO, why: 'libdwlinux.so 未编译（S2）' },
  { path: PROBE, why: 'dwp-probe 未编译（S2 判据探针）' },
  { path: ORACLE, why: 'hb-oracle.py 基准缺失（S2 判据侧）' },
].map((p) => ({ ...p, exists: exists(p.path) }));
const missing = PROBES.filter((p) => !p.exists);
const hbVersion = runCmd('dpkg-query', ['-W', '-f=${Version}', 'libharfbuzz0b']).out.trim();
const env = {
  script_id: opts.script || '1',
  harfbuzz: hbVersion,
  probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-014', started, env, verdict: 'FAIL',
    expectRef: 'ACC-014：复杂脚本字形 id 与步进与基准一致',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S2 文本（TG-005）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 用例：族名 + 文本 + 字号。复杂脚本选天城文（Lohit Devanagari，本机实测有字），
// CJK 选 Noto Sans CJK SC（TTC face index 2）。
const CASES = [
  { id: 'latin', family: 'DejaVu Sans', text: 'Hello, WPF!', size: 16 },
  { id: 'complex', family: 'Lohit Devanagari', text: 'हिन्दी', size: 16 },
  { id: 'cjk', family: 'Noto Sans CJK SC', text: '中文字形', size: 20 },
];
const picked = opts.case ? CASES.filter((c) => c.id === opts.case) : CASES;
if (!picked.length) {
  finish({ acc: 'ACC-014', started, env, verdict: 'FAIL', expectRef: 'ACC-014：复杂脚本字形 id 与步进与基准一致',
    diff: [`未知用例：${opts.case}（可选 ${CASES.map((c) => c.id).join('/')}）`] });
}

const diff = [];
const rows = [];
for (const c of picked) {
  const probe = runCmd(PROBE, ['--shape', c.family, c.text, String(c.size)], { timeout: 60000 });
  if (probe.rc !== 0) {
    diff.push(`探针失败 ${c.id}：rc=${probe.rc} ${probe.out.trim().split('\n')[0]}`);
    continue;
  }
  const payload = JSON.stringify({ family: c.family, text: c.text, size: c.size }).replace(/'/g, "'\\''");
  const oracleCmd = runCmd('sh', ['-c', `printf '%s' '${payload}' | python3 ${ORACLE} --shape`], { timeout: 120000 });
  if (oracleCmd.rc !== 0) {
    diff.push(`基准失败 ${c.id}：rc=${oracleCmd.rc} ${oracleCmd.out.trim().split('\n')[0]}`);
    continue;
  }
  let got;
  let want;
  try {
    got = JSON.parse(probe.out.trim().split('\n').pop());
    want = JSON.parse(oracleCmd.out.trim().split('\n').pop());
  } catch (e) {
    diff.push(`读数解析失败 ${c.id}：${e.message}`);
    continue;
  }
  const sameGlyphs = JSON.stringify(got.glyphs) === JSON.stringify(want.glyphs);
  const sameAdvances = JSON.stringify(got.advances) === JSON.stringify(want.advances);
  rows.push({ case: c.id, family: c.family, glyphs: got.glyphs.length, matched: want.matched.family, sameGlyphs, sameAdvances });
  if (!sameGlyphs) diff.push(`${c.id} 字形 id 不一致：got=${JSON.stringify(got.glyphs)} want=${JSON.stringify(want.glyphs)}`);
  if (!sameAdvances) diff.push(`${c.id} 步进不一致：got=${JSON.stringify(got.advances)} want=${JSON.stringify(want.advances)}`);
}

finish({
  acc: 'ACC-014',
  started,
  expectRef: 'ACC-014：三组用例的字形 id 与步进与基准逐项一致',
  env: { ...env, cases: rows },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 8),
});
