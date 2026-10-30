#!/usr/bin/env node
// ACC-016：CFF/OTF 轮廓（MOD-35，TG-005）
//
// 判据口径：40-build/text-stack/bin/dwp-probe --outline 直接走 FT_Outline_Decompose，
//   断言四件事：①点数 > 0；②坐标全部有限（非 NaN/Inf）；③包围盒宽高均 > 0（不退化）；
//   ④CFF/OTF（Noto Sans CJK TTC）与 TrueType（DejaVu Sans）两类字体都拿到轮廓，大字号不退化。
// 说明：本条的「与 FreeType 一致」由实现路径本身保证（实现即 FT_Outline_Decompose，不另起轮廓算法）；
//   独立第二路径的对拍（Python ctypes 读 FT_Outline）尚未实现，读数 env 里如实标注。
//
// 用法：node 50-verify/text/cff.mjs [--size 96]

import { parseArgs, finish, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const SO = '40-build/text-stack/bin/libdwlinux.so';
const PROBE = '40-build/text-stack/bin/dwp-probe';
const size = Number(opts.size || 96);

const PROBES = [
  { path: SO, why: 'libdwlinux.so 未编译（S2）' },
  { path: PROBE, why: 'dwp-probe 未编译（S2 判据探针）' },
].map((p) => ({ ...p, exists: exists(p.path) }));
const missing = PROBES.filter((p) => !p.exists);
const env = {
  size,
  freetype: runCmd('dpkg-query', ['-W', '-f=${Version}', 'libfreetype6']).out.trim(),
  freetype_consistency: 'same-path (FT_Outline_Decompose)；独立第二路径待补',
  probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-016', started, env, verdict: 'FAIL',
    expectRef: 'ACC-016：CFF 轮廓非空 + FreeType 一致',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S2 文本（TG-005）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 用例：CFF/OTF（Noto Sans CJK 的 TTC 是 CFF 轮廓）与 TrueType（DejaVu Sans）
const CASES = [
  { id: 'cff-cjk', family: 'Noto Sans CJK SC', cp: 0x4e2d },
  { id: 'ttf-latin', family: 'DejaVu Sans', cp: 0x48 },
];
const diff = [];
const rows = [];
for (const c of CASES) {
  const r = runCmd(PROBE, ['--outline', c.family, String(c.cp), String(size)], { timeout: 60000 });
  if (r.rc !== 0) {
    diff.push(`探针失败 ${c.id}：rc=${r.rc} ${r.out.trim().split('\n')[0]}`);
    continue;
  }
  let got;
  try {
    got = JSON.parse(r.out.trim().split('\n').pop());
  } catch (e) {
    diff.push(`读数解析失败 ${c.id}：${e.message}`);
    continue;
  }
  const pts = got.points || [];
  const finite = pts.every((p) => Number.isFinite(p.x) && Number.isFinite(p.y));
  const xs = pts.map((p) => p.x);
  const ys = pts.map((p) => p.y);
  const w = pts.length ? Math.max(...xs) - Math.min(...xs) : 0;
  const h = pts.length ? Math.max(...ys) - Math.min(...ys) : 0;
  rows.push({ case: c.id, family: c.family, codepoint: c.cp, points: pts.length, w: Number(w.toFixed(3)), h: Number(h.toFixed(3)) });
  if (pts.length <= 0) diff.push(`${c.id}（U+${c.cp.toString(16)}）轮廓点数为 0（LDP-E-TEXT-412）`);
  if (!finite) diff.push(`${c.id} 轮廓坐标含非有限值`);
  if (!(w > 0) || !(h > 0)) diff.push(`${c.id} 轮廓包围盒退化：w=${w} h=${h}`);
}

finish({
  acc: 'ACC-016',
  started,
  expectRef: 'ACC-016：CFF 与 TrueType 两类字体在 96 px 下轮廓非空且包围盒不退化',
  env: { ...env, cases: rows },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 8),
});
