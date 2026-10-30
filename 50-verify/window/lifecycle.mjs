#!/usr/bin/env node
// ACC-009：窗口生命周期与消息泵
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "samples/WpfFirstWindow/bin", why: "样本未构建（S1）", exists: exists("samples/WpfFirstWindow/bin") },
    { path: "40-build/native-shim/bin/libwpfwin32.so", why: "shim 未编译（S1）", exists: exists("40-build/native-shim/bin/libwpfwin32.so") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ xvfb_self_test: runCmd("node",["50-verify/fake/xvfb-session.mjs","--self-test"],{timeout:90000}).rc === 0 },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-009', started, env, verdict: 'FAIL',
    expectRef: 'ACC-009：开窗/移动/缩放/最小化/关闭 + 消息泵无死锁',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S1 窗口（TG-004）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-009', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-009：开窗/移动/缩放/最小化/关闭 + 消息泵无死锁',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
