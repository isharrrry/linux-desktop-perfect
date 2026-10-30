#!/usr/bin/env node
// ACC-013：字体枚举与回退
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/build/wpf/artifacts/bin/PresentationCore/Release/net10.0/PresentationCore.dll", why: "PresentationCore 未构建（S2）", exists: exists("40-build/build/wpf/artifacts/bin/PresentationCore/Release/net10.0/PresentationCore.dll") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ fontconfig_fonts: runCmd("sh",["-c","fc-list | wc -l"]).out.trim(), fallback_cache: exists("40-build/pack/FrameworkList.xml") },
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

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-013', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-013：CJK/拉丁/符号三类样本可出字，回退链命中登记顺序',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
