#!/usr/bin/env node
// ACC-010：事件回放脚本
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "samples/WpfFirstWindow/bin", why: "样本未构建（S1）", exists: exists("samples/WpfFirstWindow/bin") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ xdotool: which("xdotool"), script_id: opts.script || "1" },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-010', started, env, verdict: 'FAIL',
    expectRef: 'ACC-010：键鼠/滚轮事件回放逐条命中',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S1 输入（TG-004）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-010', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-010：键鼠/滚轮事件回放逐条命中',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
