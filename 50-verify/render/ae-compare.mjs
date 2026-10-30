#!/usr/bin/env node
// ACC-008：AE 帧对照（15 场景阈值）
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "50-verify/render/baseline", why: "无基准帧目录（S3 采集）", exists: exists("50-verify/render/baseline") },
    { path: "40-build/milcore/bin/libwpfgfx_linux.so", why: "milcore 未编译（S1）", exists: exists("40-build/milcore/bin/libwpfgfx_linux.so") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ thresholds: exists("40-build/pack/FrameworkList.xml"), scenes: 0 },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-008', started, env, verdict: 'FAIL',
    expectRef: 'ACC-008：15 场景 PNG 与 Windows 基准的 AE 指标在阈值内',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S3 主题/控件帧（TG-003）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-008', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-008：15 场景 PNG 与 Windows 基准的 AE 指标在阈值内',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
