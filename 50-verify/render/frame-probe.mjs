#!/usr/bin/env node
// ACC-007：首帧与连续帧非空率（TG-003，MOD-23/24）
//
// 判据口径：
//   ①真跑：起 Xvfb → 起样本（WpfFirstWindow --scr SCR-01）→ 等窗口出现 → 连续抓 N 帧；
//   ②每帧要求：尺寸 > 0、颜色数 > 1（不是纯色）、非白像素比例 > 0.5%（窗口有实际内容）；
//   ③非空率 = 合格帧 / 采样帧，要求 100%；
//   ④标准是 300 帧（08 §4），这里默认采样 5 帧，把「300 帧全量」明确交给 TG-015 长跑 ——
//     采样口径写进读数 env，不许把 5 帧读成 300 帧。
//
// 用法：node 50-verify/render/frame-probe.mjs [--scr SCR-01] [--frames 5]

import { parseArgs, finish } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, captureFrame, sleep, which, DOTNET_ROOT } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-01';
const frames = Number(opts.frames || 5);

const missingEnv = ['Xvfb', 'xwd', 'convert'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-007', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-007：首帧与连续帧非空率 100%',
    diff: [`环境缺件：${missingEnv.join(', ')}（不是被测对象的问题）`], env: {} });
}

const xvfb = startXvfb();
let app = null;
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 15s 内未就绪`);
  app = await startApp({ sample: 'WpfFirstWindow', scr, display: xvfb.display });
  if (!app.ok) {
    finish({ acc: 'ACC-007', started, verdict: 'FAIL', expectRef: 'ACC-007：首帧与连续帧非空率 100%',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display },
      diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, `WpfFirstWindow [${scr}]`);
  if (!win) {
    const trace = app.trace();
    const firstErr = trace.split('\n').find((l) => /Exception|error/.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-007', started, verdict: 'FAIL', expectRef: 'ACC-007：首帧与连续帧非空率 100%',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, log: app.logFile },
      diff: ['25s 内没等到窗口', `首行：${firstErr.slice(0, 200)}`] });
  }

  // 等窗口真正映射（IsViewable）再抓帧；未映射时 xwd 会报 X Error 抓到 0 字节
  const mapped = await waitMapped(xvfb.display, win.id);
  if (!mapped) {
    const trace = app.trace();
    const firstErr = trace.split(String.fromCharCode(10)).find((l) => /Exception|error/.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-007', started, verdict: 'FAIL', expectRef: 'ACC-007：首帧与连续帧非空率 100%',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, window: win, log: app.logFile },
      diff: ['40s 内窗口仍未映射（IsUnMapped）', `首行：${firstErr.slice(0, 200)}`] });
  }
  await sleep(1200);
  const samples = [];
  for (let i = 0; i < frames; i += 1) {
    const f = await captureFrame(xvfb.display, win.id);
    if (!f) {
      samples.push({ frame: i, ok: false, reason: '抓帧失败' });
      await sleep(120);
      continue;
    }
    const ok = f.width > 0 && f.height > 0 && f.colors > 1 && f.non_white_ratio > 0.005;
    samples.push({ frame: i, ok, width: f.width, height: f.height, colors: f.colors, non_white_ratio: Number(f.non_white_ratio.toFixed(4)), png: f.png });
    await sleep(120);
  }
  const okCount = samples.filter((s) => s.ok).length;
  const rate = okCount / samples.length;
  const diff = [];
  if (rate < 1) diff.push(`非空率 ${(rate * 100).toFixed(0)}%（${okCount}/${samples.length}），要求 100%`);
  for (const s of samples.filter((x) => !x.ok).slice(0, 3)) {
    diff.push(`帧 ${s.frame} 不合格：${JSON.stringify({ w: s.width, h: s.height, colors: s.colors, nw: s.non_white_ratio, reason: s.reason })}`);
  }
  finish({
    acc: 'ACC-007',
    started,
    expectRef: 'ACC-007：窗口出现后连续采样帧全部非空（300 帧全量由 TG-015 覆盖）',
    env: {
      dotnet_root: DOTNET_ROOT,
      display: xvfb.display,
      window: win,
      frames_sampled: samples.length,
      frame_scope: `${frames} 帧采样（标准 300 帧由 TG-015 覆盖）`,
      frame_png: samples.find((s) => s.png)?.png || null,
    },
    verdict: diff.length ? 'FAIL' : 'PASS',
    diff,
  });
} catch (e) {
  finish({ acc: 'ACC-007', started, verdict: 'FAIL', expectRef: 'ACC-007：首帧与连续帧非空率 100%',
    env: { dotnet_root: DOTNET_ROOT }, diff: [`判据执行失败：${e.message}`] });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}
