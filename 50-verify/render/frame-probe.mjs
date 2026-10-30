#!/usr/bin/env node
// ACC-007：帧探针（300 帧非空率）
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/milcore/bin/libwpfgfx_linux.so", why: "milcore 未编译（K-001 已找回源码，S1）", exists: exists("40-build/milcore/bin/libwpfgfx_linux.so") },
    { path: "40-build/pack/out", why: "pack 未组装（S1）", exists: exists("40-build/pack/out") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ duce_replay: exists("50-verify/fake/duce-replay.mjs"), fake_frames: runCmd("node",["50-verify/fake/duce-replay.mjs","--check"]).rc === 0 },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-007', started, env, verdict: 'FAIL',
    expectRef: 'ACC-007：WpfTextDemo 连续 300 帧非空率 100%；场景逐像素输出',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S1 首帧（TG-003）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-007', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-007：WpfTextDemo 连续 300 帧非空率 100%；场景逐像素输出',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
