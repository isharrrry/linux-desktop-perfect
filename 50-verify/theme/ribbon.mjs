#!/usr/bin/env node
// ACC-021：Ribbon 渲染与交互（TG-008，MOD-84 的 Ribbon 件）
//
// 判据口径（真跑 + 结构读数 + 帧）：
//   ①起 Xvfb + 样本 WpfControlGallery（SCR-04，XAML 里带 `<ribbon:Ribbon>`/Tab/Group/Button）；
//   ②结构断言（UIA 树，真值不是"看起来像"）：必须出现
//       · `role=page tab list` 且名字为 `Ribbon` 的节点（Ribbon 的 AutomationPeer 角色）
//       · 名字为 `开始` 的 RibbonTab 节点；
//   ③帧断言：窗口抓帧非空（Ribbon 真的画出来了，不是"树里有节点但一片白"）；
//   ④交互断言：点 Ribbon 区域中心一次，进程仍存活（不崩、不卡死）。
//
// 用法：node 50-verify/theme/ribbon.mjs

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, captureFrame, sleep, DOTNET_ROOT, FRAMEWORK_DIR } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-04';
const missingEnv = ['Xvfb', 'xwd', 'convert', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-021', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-021：Ribbon 样本渲染与交互通过', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!fs.existsSync(path.join(FRAMEWORK_DIR, 'LdpA11y.dll'))) {
  finish({ acc: 'ACC-021', started, verdict: 'FAIL', expectRef: 'ACC-021：Ribbon 样本渲染与交互通过',
    env: { framework: FRAMEWORK_DIR }, diff: ['缺树导出钩子 LdpA11y.dll（先跑 install-runtime）'] });
}

const treePath = path.join(os.tmpdir(), `ldp-ribbon-${Date.now()}.json`);
const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
let app = null;
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({ sample: 'WpfControlGallery', scr, display: xvfb.display, treeDump: treePath });
  if (!app.ok) {
    finish({ acc: 'ACC-021', started, verdict: 'FAIL', expectRef: 'ACC-021：Ribbon 样本渲染与交互通过',
      env: { dotnet_root: DOTNET_ROOT }, diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, `WpfControlGallery [${scr}]`);
  if (win) await waitMapped(xvfb.display, win.id);
  let waited = 0;
  while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
  if (!fs.existsSync(treePath)) {
    const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-021', started, verdict: 'FAIL', expectRef: 'ACC-021：Ribbon 样本渲染与交互通过',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, log: app.logFile }, diff: ['40 s 内未导出结构树', `首行：${firstErr.slice(0, 200)}`] });
  }
  const tree = JSON.parse(fs.readFileSync(treePath, 'utf8'));
  const nodes = tree.nodes || [];
  const ribbon = nodes.find((n) => n.role === 'page tab list' && n.name === 'Ribbon');
  const tab = nodes.find((n) => n.name === '开始');
  steps.push({ step: 'tree_ribbon', ok: !!ribbon, node: ribbon ? { path: ribbon.path, role: ribbon.role, name: ribbon.name, bounds: ribbon.bounds } : null, total_nodes: nodes.length });
  steps.push({ step: 'tree_tab', ok: !!tab, node: tab ? { path: tab.path, role: tab.role, name: tab.name } : null });

  await sleep(1000);
  const frame = win ? await captureFrame(xvfb.display, win.id) : null;
  steps.push({ step: 'frame_nonempty', ok: !!frame && frame.colors > 1 && frame.non_white_ratio > 0.005,
    colors: frame ? frame.colors : 0, non_white_ratio: frame ? Number(frame.non_white_ratio.toFixed(4)) : 0 });

  if (ribbon && ribbon.bounds) {
    const [x, y, w, h] = ribbon.bounds;
    spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${x + Math.floor(w / 2)} ${y + Math.floor(h / 2)} click 1`], { encoding: 'utf8', timeout: 20000 });
    await sleep(1200);
    steps.push({ step: 'click_alive', ok: app.proc.exitCode === null, exit_code: app.proc.exitCode });
  } else {
    steps.push({ step: 'click_alive', ok: false, note: '没有 Ribbon 的 bounds，无法点击' });
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
finish({
  acc: 'ACC-021',
  started,
  expectRef: 'ACC-021：Ribbon 结构（page tab list + Tab）+ 帧非空 + 点击不崩',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, tree: treePath, steps },
  verdict: failed.length ? 'FAIL' : 'PASS',
  diff: failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
});
