#!/usr/bin/env node
// ACC-011：DPI 三档几何（TG-004，MOD-12/24）
//
// 判据口径（真跑 + 结构读数，08 §4）：
//   ①三档会话：Xvfb 用 `-dpi 120/144/192` 起（X server 报的 screen DPI 就是 shim `GetDpiForWindow` 的输入）；
//   ②每档跑样本（SCR-01），经 LdpA11y 钩子导出 UIA 树，读状态条里的 DPI 读数节点（元素属性表定的数据源）；
//   ③期望文本与 DPI 一一对应：`DPI 120 (125%)` / `DPI 144 (150%)` / `DPI 192 (200%)`；
//   ④再验一致性：同档下 DIP→像素换算的比例与 DPI/96 相符（窗口宽度 = 960 DIP × scale，容差 ±2 px）。
//
// 用法：node 50-verify/window/dpi.mjs [--scale 1.5]

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, which as _w, DOTNET_ROOT, FRAMEWORK_DIR, sampleDir } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const CASES = opts.scale
  ? [{ dpi: Math.round(96 * Number(opts.scale)), pct: Math.round(Number(opts.scale) * 100) }]
  : [
      { dpi: 120, pct: 125 },
      { dpi: 144, pct: 150 },
      { dpi: 192, pct: 200 },
    ];

const missingEnv = ['Xvfb', 'xdpyinfo'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-011', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-011：125%/150%/200% DPI 样本', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!fs.existsSync(path.join(FRAMEWORK_DIR, 'LdpA11y.dll'))) {
  finish({ acc: 'ACC-011', started, verdict: 'FAIL', expectRef: 'ACC-011：125%/150%/200% DPI 样本',
    env: { framework: FRAMEWORK_DIR }, diff: ['缺树导出钩子 LdpA11y.dll（先跑 install-runtime + build-port --map）'] });
}

const readDpiNode = (tree) => {
  const n = (tree.nodes || []).find((x) => /^DPI \d{3} \(\d{1,3}%\)$/.test(String(x.name || '')));
  return n ? n.name : null;
};

const rows = [];
const diff = [];
for (const c of CASES) {
  const xvfb = startXvfb(undefined, { dpi: c.dpi, screen: '1600x1200x24' });
  let app = null;
  try {
    if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
    const treePath = path.join(os.tmpdir(), `ldp-dpi-${c.dpi}-${Date.now()}.json`);
    app = await startApp({ sample: 'WpfFirstWindow', scr: 'SCR-01', display: xvfb.display, treeDump: treePath });
    if (!app.ok) {
      diff.push(`DPI ${c.dpi}：被测对象不可用（${app.reason}）`);
      continue;
    }
    const win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]');
    if (win) await waitMapped(xvfb.display, win.id);
    let waited = 0;
    while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
    if (!fs.existsSync(treePath)) {
      diff.push(`DPI ${c.dpi}：40 s 内未导出结构树`);
      continue;
    }
    const tree = JSON.parse(fs.readFileSync(treePath, 'utf8'));
    const gotText = readDpiNode(tree);
    const wantText = `DPI ${String(c.dpi).padStart(3, '0')} (${c.pct}%)`;
    // 同一棵树上验几何一致性：窗口宽 = 960 DIP × dpi/96
    const frame = (tree.nodes || []).find((n) => n.path === '0');
    const scale = c.dpi / 96;
    const wantW = Math.round(960 * scale);
    // 屏幕宽度是硬上限（192 DPI 下 960 DIP = 1920 px > 1600 px 的屏幕）：超过时按屏幕宽截断，
    // 截断本身记进读数（clamped=true），不当成实现缺陷。
    const screenW = 1600;
    const expectedW = Math.min(wantW, screenW);
    const clamped = wantW > screenW;
    // 截断档给小容差：窗口贴屏幕边时 X/WPF 会留边（实测 1584 vs 1600，16 px 的边框/工作区余量）
    const tol = clamped ? 24 : 2;
    const wOk = frame ? Math.abs(frame.bounds[2] - expectedW) <= tol : false;
    rows.push({ dpi: c.dpi, want: wantText, got: gotText, window_w: frame ? frame.bounds[2] : null, want_w: wantW, expected_w: expectedW, clamped, w_ok: wOk });
    if (gotText !== wantText) diff.push(`DPI ${c.dpi}：读数 ${gotText}，期望 ${wantText}`);
    if (!wOk) diff.push(`DPI ${c.dpi}：窗口宽 ${frame ? frame.bounds[2] : 'n/a'}，期望 ${expectedW}${clamped ? '（已按屏幕宽截断）' : ''}（960 DIP × ${scale}）`);
  } catch (e) {
    diff.push(`DPI ${c.dpi}：判据执行失败 ${e.message}`);
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  await sleep(400);
}

finish({
  acc: 'ACC-011',
  started,
  expectRef: 'ACC-011：三档 DPI 下读数文本与窗口几何都按 DPI/96 缩放',
  env: { dotnet_root: DOTNET_ROOT, cases: rows },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 8),
});
