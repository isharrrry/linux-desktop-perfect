#!/usr/bin/env node
// ACC-013：字体枚举与回退（MOD-31，TG-005）
//
// 判据口径（双路径对拍，08 §1.5「不许自证」）：
//   ①被测件：40-build/text-stack/bin/libdwlinux.so（`dwp-probe --fonts/--fallback/--shape`）；
//   ②基准：50-verify/text/hb-oracle.py（Python ctypes 直调 libfontconfig/libharfbuzz），与实现不共享代码；
//   ③三件事：a) 字体枚举非空；b) 拉丁/CJK/符号三类样本 shaping 后「无缺字」（glyph id 全非 0）；
//     c) 回退链（FcFontSort 顺序）与基准逐项一致。
// 注：被测对象从「PresentationCore.dll」改为 libdwlinux.so —— 字体枚举的产物就是 text-stack（03 §3.4 产物表），
//   PresentationCore 只是消费者；宿主集成（字体缓存 TBL-005 落位）待 ACC-015 的宿主侧一并验。
//
// 用法：node 50-verify/text/font-enum.mjs [--picks 10]

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
const env = {
  fontconfig_fonts: runCmd('sh', ['-c', 'fc-list | wc -l']).out.trim(),
  probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-013', started, env, verdict: 'FAIL',
    expectRef: 'ACC-013：CJK/拉丁/符号三类样本可出字，回退链命中登记顺序',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S2 文本（TG-005）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

const diff = [];
const rows = [];

// a) 字体枚举
const fonts = runCmd(PROBE, ['--fonts'], { timeout: 60000 });
if (fonts.rc !== 0) diff.push(`字体枚举失败：rc=${fonts.rc} ${fonts.out.split('\n')[0]}`);
let fontCount = 0;
try {
  fontCount = JSON.parse(fonts.out.trim().split('\n').pop()).count;
} catch (e) {
  diff.push(`字体枚举读数解析失败：${e.message}`);
}
if (!(fontCount > 0)) diff.push(`字体枚举为 0 族（fontconfig 不可用？）`);

// b) 三类样本可出字（glyph id 全非 0）
const CASES = [
  { id: 'latin', family: 'DejaVu Sans', text: 'Hello, WPF!' },
  { id: 'cjk', family: 'Noto Sans CJK SC', text: '中文字形' },
  { id: 'symbol', family: 'DejaVu Sans', text: '→✓∑≈' },
];
for (const c of CASES) {
  const r = runCmd(PROBE, ['--shape', c.family, c.text, '16'], { timeout: 60000 });
  if (r.rc !== 0) {
    diff.push(`shaping 失败 ${c.id}：${r.out.trim().split('\n')[0]}`);
    continue;
  }
  let got;
  try {
    got = JSON.parse(r.out.trim().split('\n').pop());
  } catch (e) {
    diff.push(`读数解析失败 ${c.id}：${e.message}`);
    continue;
  }
  const zero = (got.glyphs || []).filter((g) => g === 0).length;
  rows.push({ case: c.id, family: c.family, glyphs: (got.glyphs || []).length, missing_glyphs: zero });
  if (!(got.glyphs || []).length) diff.push(`${c.id} 没有产出任何字形`);
  if (zero > 0) diff.push(`${c.id} 有 ${zero} 个缺字（glyph id 0）—— 回退链没命中`);
}

// c) 回退链与基准逐项一致（前 picks 项）
const picks = Number(opts.picks || 10);
const fbProbe = runCmd(PROBE, ['--fallback', '', '中'], { timeout: 60000 });
const fbOracle = runCmd('sh', ['-c', `printf '%s' '{"family":"","text":"中"}' | python3 ${ORACLE} --fallback`], { timeout: 120000 });
if (fbProbe.rc !== 0 || fbOracle.rc !== 0) {
  diff.push(`回退链取数失败：probe rc=${fbProbe.rc} oracle rc=${fbOracle.rc}`);
} else {
  const a = JSON.parse(fbProbe.out.trim().split('\n').pop());
  const b = JSON.parse(fbOracle.out.trim().split('\n').pop());
  const ta = (a.families || []).slice(0, picks);
  const tb = (b.families || []).slice(0, picks);
  rows.push({ case: 'fallback', families: a.count, compared: ta.length });
  if (a.count !== b.count) diff.push(`回退链族数不一致：实现 ${a.count}，基准 ${b.count}`);
  if (JSON.stringify(ta) !== JSON.stringify(tb)) {
    diff.push(`回退链前 ${picks} 项不一致：实现 ${JSON.stringify(ta)}，基准 ${JSON.stringify(tb)}`);
  }
}

finish({
  acc: 'ACC-013',
  started,
  expectRef: 'ACC-013：枚举非空、三类样本无缺字、回退链前 N 项与基准一致',
  env: { ...env, fonts: fontCount, cases: rows },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 8),
});
