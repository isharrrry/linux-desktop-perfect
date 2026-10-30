#!/usr/bin/env node
// ACC-026：合成器检测与降级路径（TG-011，MOD-24 的 DwmProbe）
//
// 判据口径（两极对照，08 §1.5「不许自证」）：
//   ①被测实现：`libwpfwin32.so` 的 `DwmIsCompositionEnabled`（经 `bin/dwm-probe` 直接调，
//     不经样本 —— 样本状态条里的 `composite=` 是静态文本，读它会得到假读数）；
//   ②会话 A（无合成器）：自由起一个 Xvfb，`_NET_WM_CM_S0` 无所有者 → 期望 `enabled=false`；
//   ③会话 B（有合成器）：同一 Xvfb 上起「Fake 合成器」（只持有该 selection，不真合成）→ 期望 `enabled=true`；
//   ④两会话读数必须**不同**且各自符合预期（相同即红 —— 那说明实现恒返回一个值）。
//
// 用法：node 50-verify/compose/dwm-detect.mjs [--session composite|none]

import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, exists, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, sleep } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const only = opts.session || null;
const PROBE = '40-build/native-shim/bin/dwm-probe';
const FAKE_CM = '40-build/native-shim/bin/fake-compositor';

const missingEnv = ['Xvfb', 'xprop'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-026', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-026：有合成器复用、无合成器 DwmIsCompositionEnabled=false',
    diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!exists(PROBE) || !exists(FAKE_CM)) {
  finish({ acc: 'ACC-026', started, verdict: 'FAIL', expectRef: 'ACC-026：合成器检测两极',
    env: { probe: PROBE, fake_compositor: FAKE_CM },
    diff: [`缺被测对象/探针：${PROBE} 或 ${FAKE_CM}`, '先跑 make -C 40-build/native-shim probes'] });
}

const runProbe = (display) => {
  const r = spawnSync(PROBE, [], { encoding: 'utf8', timeout: 20000, env: { ...process.env, DISPLAY: display } });
  if (r.status !== 0) return { ok: false, raw: `${r.stdout || ''}${r.stderr || ''}`.trim().split('\n')[0] };
  try {
    return { ok: true, ...JSON.parse((r.stdout || '').trim().split('\n').pop()) };
  } catch (e) {
    return { ok: false, raw: `解析失败：${e.message}` };
  }
};

const xvfb = startXvfb();
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  steps.push({ step: 'xvfb', ok: true, display: xvfb.display });

  if (only !== 'composite') {
    const a = runProbe(xvfb.display);
    steps.push({ step: 'session_none', ok: a.ok && a.enabled === false, ...a });
  }

  if (only !== 'none') {
    const bg = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} ${FAKE_CM} ${xvfb.display} > /tmp/ldp-fake-compositor.log 2>&1 & echo $!`], { encoding: 'utf8' });
    const pid = Number((bg.stdout || '').trim()) || 0;
    await sleep(1500);
    const readyLog = (() => { try { return fs.readFileSync('/tmp/ldp-fake-compositor.log', 'utf8'); } catch { return ''; } })();
    const b = runProbe(xvfb.display);
    steps.push({ step: 'session_composite', ok: b.ok && b.enabled === true, ...b, fake_ready: /READY/.test(readyLog), pid });
    if (pid) try { process.kill(pid, 'SIGTERM'); } catch {}
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
const noneStep = steps.find((s) => s.step === 'session_none');
const compStep = steps.find((s) => s.step === 'session_composite');
const diff = failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 200)}`);
if (noneStep && compStep && noneStep.enabled === compStep.enabled) {
  diff.push(`两会话读数相同（enabled=${noneStep.enabled}）—— 实现恒返回一个值，DwmProbe 没生效`);
}
finish({
  acc: 'ACC-026',
  started,
  expectRef: 'ACC-026：无合成器 → enabled=false；有合成器 → enabled=true（两极不同）',
  env: { steps },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff,
});
