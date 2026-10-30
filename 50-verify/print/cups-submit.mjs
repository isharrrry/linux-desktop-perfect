#!/usr/bin/env node
// ACC-024：XPS→CUPS 作业
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/build/wpf/artifacts/bin/ReachFramework/Release/net10.0/ReachFramework.dll", why: "XPS/打印路径未实现（S4）", exists: exists("40-build/build/wpf/artifacts/bin/ReachFramework/Release/net10.0/ReachFramework.dll") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ cupsd: which("cupsd"), libcups: runCmd("dpkg-query",["-W","-f=${Version}","libcups2"]).out.trim() },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-024', started, env, verdict: 'FAIL',
    expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S4 打印（TG-010）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-024', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
