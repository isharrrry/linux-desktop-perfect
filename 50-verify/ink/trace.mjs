#!/usr/bin/env node
// ACC-023：笔迹采集
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/winforms-compat/bin/PenImc.dll", why: "PenImc 替身未实现（S4）", exists: exists("40-build/winforms-compat/bin/PenImc.dll") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ input_devices: runCmd("sh",["-c","ls /dev/input/event* 2>/dev/null | wc -l"]).out.trim(), note: "无笔设备时应 DEGRADED，当前连替身都没有" },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-023', started, env, verdict: 'FAIL',
    expectRef: 'ACC-023：笔/触摸轨迹采集与渲染点序列一致；无设备时降级',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S4 Ink（TG-009）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-023', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-023：笔/触摸轨迹采集与渲染点序列一致；无设备时降级',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
