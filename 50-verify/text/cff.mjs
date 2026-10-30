#!/usr/bin/env node
// ACC-016：CFF 轮廓
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/text-stack/bin/libdwlinux.so", why: "libdwlinux.so 未编译（S2）", exists: exists("40-build/text-stack/bin/libdwlinux.so") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ freetype: runCmd("dpkg-query",["-W","-f=${Version}","libfreetype6"]).out.trim() },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-016', started, env, verdict: 'FAIL',
    expectRef: 'ACC-016：CFF 字形轮廓非空且与 FreeType 一致',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S2 文本（TG-005）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-016', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-016：CFF 字形轮廓非空且与 FreeType 一致',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
