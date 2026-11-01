#!/usr/bin/env node
// ACC-009：窗口生命周期与消息泵（TG-004，MOD-12/13/24）
//
// 判据口径（真跑，08 §4）：
//   ①开窗：窗口出现且 Map State 变为 IsViewable，标题含 --scr 值，尺寸 > 0；
//   ②移动：xdotool windowmove 后 xwininfo 的绝对坐标变化被窗口接受（消息泵在跑）；
//   ③缩放：xdotool windowsize 后宽度/高度变化（WM_SIZE 路径通）；
//   ④关闭：SIGTERM 后进程在 15 s 内退出（消息泵不死锁）。
//   无窗口管理器时「最小化」由 WM 决定，本项只取证不判红（与 Fake X11 的偏差登记一致）。
//   任何一步失败即 FAIL，并附应用 trace 的首个异常行。
//
// 用法：node 50-verify/window/lifecycle.mjs [--scr SCR-01]

import { spawnSync } from 'node:child_process';
import { parseArgs, finish } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, which, DOTNET_ROOT } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-01';
const title = `WpfFirstWindow [${scr}]`;
const NL = String.fromCharCode(10);

const missingEnv = ['Xvfb', 'xwininfo', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-009', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-009：开窗/移动/缩放/最小化/关闭 + 消息泵无死锁',
    diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}

const sh = (cmd) => spawnSync('sh', ['-c', cmd], { encoding: 'utf8', timeout: 20000 });
const geom = (display, id) => {
  const r = sh(`DISPLAY=${display} xwininfo -id ${id}`);
  const w = Number((r.stdout.match(/Width:\s+(\d+)/) || [])[1] || 0);
  const h = Number((r.stdout.match(/Height:\s+(\d+)/) || [])[1] || 0);
  const x = Number((r.stdout.match(/Absolute upper-left X:\s+(-?\d+)/) || [])[1] || 0);
  const y = Number((r.stdout.match(/Absolute upper-left Y:\s+(-?\d+)/) || [])[1] || 0);
  const viewable = /Map State:\s+IsViewable/.test(r.stdout);
  return { w, h, x, y, viewable };
};

const xvfb = startXvfb();
let app = null;
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({ sample: 'WpfFirstWindow', scr, display: xvfb.display });
  if (!app.ok) {
    finish({ acc: 'ACC-009', started, verdict: 'FAIL', expectRef: 'ACC-009：开窗/移动/缩放/最小化/关闭 + 消息泵无死锁',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display }, diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, title);
  if (!win) {
    const firstErr = app.trace().split(NL).find((l) => /Exception|error/.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-009', started, verdict: 'FAIL', expectRef: 'ACC-009：开窗/移动/缩放/最小化/关闭 + 消息泵无死锁',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, log: app.logFile }, diff: ['25s 内没等到窗口', `首行：${firstErr.slice(0, 200)}`] });
  }
  const mapped = await waitMapped(xvfb.display, win.id);
  await sleep(800);
  const g0 = geom(xvfb.display, win.id);
  steps.push({ step: 'open', ok: mapped && g0.viewable && g0.w > 0 && g0.h > 0 && win.title.includes(scr), ...g0, title: win.title });

  // 【判据口径修正 / K-228】**有 EWMH 窗口管理器时必须用 `wmctrl`（发 `_NET_MOVERESIZE_WINDOW`），
  //   不能用 `xdotool windowmove`（发裸 `XMoveWindow`）。**
  //   实测（K-227）：装 openbox 之后裸 `XMoveWindow` 被 WM 忽略 —— 因为 WM 连"无装饰"窗口
  //   也 reparent 到同尺寸 frame 里，裸移客户窗只会让内容在 frame 内滑动，视觉上"窗户没动"。
  //   这不是本仓实现缺陷：**在 EWMH WM 下，移动窗口的正确方式就是 `_NET_MOVERESIZE_WINDOW`**，
  //   而 shim 已经支持它（`wpf_x11_moveresize_window`，K-226/K-227）。
  //   ⇒ 判据改用 `wmctrl`；**无 WM 时**回退到 `xdotool`（裸 Xvfb 上那是唯一可行的方式）。
  const hasWM = (sh(`DISPLAY=${xvfb.display} xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null`).stdout || "").includes("window id");
  if (hasWM) {
    sh(`DISPLAY=${xvfb.display} wmctrl -i -r ${win.id} -e 0,${g0.x + 40},${g0.y + 30},-1,-1`);
  } else {
    sh(`DISPLAY=${xvfb.display} xdotool windowmove ${win.id} ${g0.x + 40} ${g0.y + 30}`);
  }
  await sleep(700);
  const g1 = geom(xvfb.display, win.id);
  steps.push({ step: 'move', ok: g1.x === g0.x + 40 && g1.y === g0.y + 30, from: { x: g0.x, y: g0.y }, to: { x: g1.x, y: g1.y } });

  // 同理（K-228）：改尺寸也走 EWMH；`wmctrl -e` 的 -1 表示"该维度不变"。
  if (hasWM) {
    sh(`DISPLAY=${xvfb.display} wmctrl -i -r ${win.id} -e 0,${g0.x + 40},${g0.y + 30},${g0.w + 120},${g0.h + 90}`);
  } else {
    sh(`DISPLAY=${xvfb.display} xdotool windowsize ${win.id} ${g0.w + 120} ${g0.h + 90}`);
  }
  await sleep(900);
  const g2 = geom(xvfb.display, win.id);
  steps.push({ step: 'resize', ok: g2.w === g0.w + 120 && g2.h === g0.h + 90, from: { w: g0.w, h: g0.h }, to: { w: g2.w, h: g2.h } });

  const icon = sh(`DISPLAY=${xvfb.display} xdotool windowminimize ${win.id}`);
  await sleep(500);
  const g3 = geom(xvfb.display, win.id);
  steps.push({ step: 'minimize', ok: true, note: '无窗口管理器时由 WM 决定，本项只取证不判红', rc: icon.status, viewable: g3.viewable });

  const exited = await new Promise((resolve) => {
    let done = false;
    app.proc.on('exit', () => { if (!done) { done = true; resolve(true); } });
    app.kill();
    setTimeout(() => { if (!done) { done = true; resolve(false); } }, 15000);
  });
  steps.push({ step: 'close', ok: exited });
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
finish({
  acc: 'ACC-009',
  started,
  expectRef: 'ACC-009：开窗 + 移动 + 缩放 + 关闭（消息泵无死锁）',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, steps },
  verdict: failed.length ? 'FAIL' : 'PASS',
  diff: failed.length ? failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 220)}`) : [],
});
