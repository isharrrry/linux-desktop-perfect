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
  // 【判据口径修正 / K-229】**`wmctrl -e` 的 x/y 是 frame 的坐标，而 `geom()` 读的是客户区。**
  //   实测：让 frame 移 (40,30) 后，客户区位移读数是 (41,54) —— 差的 (1,24) 正是 openbox 的
  //   装饰（边框 1px + 标题栏 24px）。`xwininfo -frame` 对客户窗口无效（读数不变），
  //   所以这里**自己算装饰偏移**：比较窗口的 frame parent 与自身的绝对坐标。
  const frameDelta = (() => {
    const t = sh(`DISPLAY=${xvfb.display} xwininfo -id ${win.id} -tree 2>/dev/null`).stdout || "";
    const parent = (t.match(/Parent window id:\s+(0x[0-9a-f]+)/) || [])[1];
    if (!parent || parent === '0x0') return { dx: 0, dy: 0 };
    const pi = sh(`DISPLAY=${xvfb.display} xwininfo -id ${parent} 2>/dev/null`).stdout || "";
    const px = Number((pi.match(/Absolute upper-left X:\s+(-?\d+)/) || [])[1]);
    const py = Number((pi.match(/Absolute upper-left Y:\s+(-?\d+)/) || [])[1]);
    const cx = Number((t.match(/Absolute upper-left X:\s+(-?\d+)/) || [])[1]);
    const cy = Number((t.match(/Absolute upper-left Y:\s+(-?\d+)/) || [])[1]);
    if (!Number.isFinite(px) || !Number.isFinite(cx)) return { dx: 0, dy: 0 };
    return { dx: cx - px, dy: cy - py };   // 客户区相对 frame 的偏移
  })();
  const hasWM = (sh(`DISPLAY=${xvfb.display} xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null`).stdout || "").includes("window id");
  if (hasWM) {
    // wmctrl 设 frame 坐标 ⇒ 目标 = 客户区目标 − 客户区相对 frame 的偏移
    sh(`DISPLAY=${xvfb.display} wmctrl -i -r ${win.id} -e 0,${g0.x + 40 - frameDelta.dx},${g0.y + 30 - frameDelta.dy},-1,-1`);
  } else {
    sh(`DISPLAY=${xvfb.display} xdotool windowmove ${win.id} ${g0.x + 40} ${g0.y + 30}`);
  }
  await sleep(700);
  const g1 = geom(xvfb.display, win.id);
  // 【判据口径修正 / K-229】**比较"相对位移"而不是"绝对位置"，并容忍 frame 装饰偏移。**
  //   实测（K-228）：用 `wmctrl -e`（发 `_NET_MOVERESIZE_WINDOW`）后窗口**确实移动了**，
  //   但 `xwininfo -id <客户窗口>` 读到的位移是 (41, 54) 而不是 (40, 30) ——
  //   差的 (1, 24) 正是 openbox 的 frame 装饰（边框 1px + 标题栏 24px）。
  //   `wmctrl` 设的是 **frame** 的位置，`xwininfo -id <客户窗口>` 读的是**客户区**的位置。
  //   ⇒ 本项测的是「窗口能不能被移动」，**不是「X server 的坐标原点定义」**
  //     ⇒ 用相对位移 + 容差判定（容差 4px，足以容纳常见 WM 的装饰尺寸差异）。
  //   ⚠️ 这是**口径修正**不是放宽标准：移动幅度仍必须≈(40,30)，只是不要求绝对坐标相等。
  const mdx = g1.x - g0.x, mdy = g1.y - g0.y;
  const moveOk = Math.abs(mdx - 40) <= 2 && Math.abs(mdy - 30) <= 2;
  steps.push({ step: 'move', ok: moveOk, from: { x: g0.x, y: g0.y }, to: { x: g1.x, y: g1.y },
               delta: { dx: mdx, dy: mdy }, expect: { dx: 40, dy: 30 }, tol: 2,
               note: 'differences vs expect are WM frame decoration (K-228/K-229)' });

  // 同理（K-228）：改尺寸也走 EWMH；`wmctrl -e` 的 -1 表示"该维度不变"。
  if (hasWM) {
    sh(`DISPLAY=${xvfb.display} wmctrl -i -r ${win.id} -e 0,${g0.x + 40 - frameDelta.dx},${g0.y + 30 - frameDelta.dy},${g0.w + 120},${g0.h + 90}`);
  } else {
    sh(`DISPLAY=${xvfb.display} xdotool windowsize ${win.id} ${g0.w + 120} ${g0.h + 90}`);
  }
  await sleep(900);
  const g2 = geom(xvfb.display, win.id);
  // 同理（K-229）：尺寸也可能带 frame 的常量差（有些 WM 把装饰算进外框尺寸）。
  const rdw = g2.w - g0.w, rdh = g2.h - g0.h;
  const resizeOk = Math.abs(rdw - 120) <= 2 && Math.abs(rdh - 90) <= 2;
  steps.push({ step: 'resize', ok: resizeOk, from: { w: g0.w, h: g0.h }, to: { w: g2.w, h: g2.h },
               delta: { dw: rdw, dh: rdh }, expect: { dw: 120, dh: 90 }, tol: 2,
               note: 'same tolerance as move (K-229)' });

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
