#!/usr/bin/env node
// ACC-012：剪贴板与拖放（TG-004，MOD-63）
//
// 判据口径（真跑，探针直接调 shim 的 Win32 面 —— 不经 WPF 的 `Clipboard` 类：
//   上游 `System.Windows.Clipboard` 走 OLE COM，Linux 无 COM，那是另一条登记过的缺口）：
//   ①同进程往返：`clip-probe --set abc --get`（SetClipboardData → GetClipboardData，本地缓冲路径）；
//   ②跨进程往返：A 进程 `--set hello --hold` 持有 CLIPBOARD selection，B 进程 `--get` 经
//     `XConvertSelection` 取回 —— 这是 X11 上"剪贴板"的真实语义（selection 拥有权）；
//   ③XDND 拖入：本仓尚未实现（`XClientMessage` 的 XDND 协议），如实计入 diff。
//
// 用法：node 50-verify/window/clipboard.mjs

import { spawnSync } from 'node:child_process';
import { parseArgs, finish, exists, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, sleep } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBE = '40-build/native-shim/bin/clip-probe';
const missingEnv = ['Xvfb'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-012', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-012：文本/图像双向剪贴板 + XDND 拖入', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!exists(PROBE)) {
  finish({ acc: 'ACC-012', started, verdict: 'FAIL', expectRef: 'ACC-012：文本/图像双向剪贴板 + XDND 拖入',
    env: { probe: PROBE }, diff: [`缺被测对象/探针：${PROBE}`, '先跑 make -C 40-build/native-shim probes'] });
}

const run = (display, args, timeout = 20000) => {
  const r = spawnSync(PROBE, args, { encoding: 'utf8', timeout, env: { ...process.env, DISPLAY: display } });
  let json = null;
  const line = (r.stdout || '').trim().split('\n').pop();
  if (line) { try { json = JSON.parse(line); } catch { json = null; } }
  return { rc: r.status, json, raw: line, stderr: (r.stderr || '').slice(0, 200) };
};

const xvfb = startXvfb();
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);

  // ① 同进程往返（ASCII；多字节在 GetClipboardData 的 UTF-16 反解上另算，见读数）
  const self = run(xvfb.display, ['--set', 'abc', '--get']);
  steps.push({ step: 'in_process_roundtrip', ok: !!(self.json && self.json.roundtrip_ok === true), ...self });

  // ② 跨进程：A 持有 selection，B 读
  const a = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} ${PROBE} --set cross-proc-value --hold 4000 > /tmp/ldp-clip-A.out 2>/tmp/ldp-clip-A.err &  echo $!`], { encoding: 'utf8' });
  const pid = Number((a.stdout || '').trim()) || 0;
  await sleep(1200);
  const b = run(xvfb.display, ['--get']);
  if (pid) { try { process.kill(pid, 'SIGTERM'); } catch {} }
  const crossOk = !!(b.json && typeof b.json.value === 'string' && b.json.value === 'cross-proc-value');
  steps.push({ step: 'cross_process', ok: crossOk, want: 'cross-proc-value', got: b.json ? b.json.value : null, rc: b.rc });

  // ③ XDND：未实现
  steps.push({ step: 'xdnd', ok: false, note: 'XDND 拖入未实现（XClientMessage 协议缺失），见 GAP 登记' });
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
const inProc = steps.find((s) => s.step === 'in_process_roundtrip');
const cross = steps.find((s) => s.step === 'cross_process');
const coreOk = !!(inProc && inProc.ok);
const verdict = !coreOk ? 'FAIL' : (cross && cross.ok) ? (failed.length ? 'FAIL' : 'PASS') : 'DEGRADED';
finish({
  acc: 'ACC-012',
  started,
  expectRef: 'ACC-012：文本剪贴板同进程/跨进程往返 + XDND 拖入',
  env: { display: xvfb.display, steps },
  verdict,
  degrade: verdict === 'DEGRADED'
    ? 'D-010：跨进程剪贴板（X11 CLIPBOARD selection 的 SelectionRequest/ConvertSelection 往返）未打通；同进程 Win32 面往返通过；XDND 未实现'
    : 'none',
  diff: verdict === 'PASS' ? [] : failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
});
