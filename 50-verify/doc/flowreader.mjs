#!/usr/bin/env node
// ACC-025：FlowDocumentReader（TG-010，MOD-77 的文档面）
//
// 判据口径（真跑 + 结构读数 + 帧）：
//   ①起 Xvfb + 样本 WpfPrintDoc（SCR-06，XAML 里有 `<FlowDocumentReader>` + 两个 Paragraph）；
//   ②结构断言（UIA 树）：必须出现名字 `流文档` 的节点，且它的界限非零（文档真的布局了）；
//   ③帧断言：窗口抓帧非空；
//   ④交互断言：点 FlowDocumentReader 里的"下一页"按钮（走 X11 鼠标 + WPF 输入管线），
//     再读树/帧确认进程存活 —— 分页/缩放/查找三条控件都在同一屏上，至少证明文档宿主不崩。
//
// 用法：node 50-verify/doc/flowreader.mjs

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, captureFrame, sleep, DOTNET_ROOT, FRAMEWORK_DIR } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-06';
const missingEnv = ['Xvfb', 'xwd', 'convert', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-025', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-025：分页/缩放/查找不崩', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!fs.existsSync(path.join(FRAMEWORK_DIR, 'LdpA11y.dll'))) {
  finish({ acc: 'ACC-025', started, verdict: 'FAIL', expectRef: 'ACC-025：分页/缩放/查找不崩',
    env: { framework: FRAMEWORK_DIR }, diff: ['缺树导出钩子 LdpA11y.dll（先跑 install-runtime）'] });
}

const treePath = path.join(os.tmpdir(), `ldp-flow-${Date.now()}.json`);
const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
let app = null;
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({ sample: 'WpfPrintDoc', scr, display: xvfb.display, treeDump: treePath });
  if (!app.ok) {
    finish({ acc: 'ACC-025', started, verdict: 'FAIL', expectRef: 'ACC-025：分页/缩放/查找不崩',
      env: { dotnet_root: DOTNET_ROOT }, diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, `WpfPrintDoc [${scr}]`);
  if (win) await waitMapped(xvfb.display, win.id);
  let waited = 0;
  while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
  if (!fs.existsSync(treePath)) {
    const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-025', started, verdict: 'FAIL', expectRef: 'ACC-025：分页/缩放/查找不崩',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, log: app.logFile }, diff: ['40 s 内未导出结构树', `首行：${firstErr.slice(0, 200)}`] });
  }
  const tree = JSON.parse(fs.readFileSync(treePath, 'utf8'));
  const nodes = tree.nodes || [];
  const doc = nodes.find((n) => n.name === '流文档');
  const hasBounds = doc && Array.isArray(doc.bounds) && doc.bounds[2] > 0 && doc.bounds[3] > 0;
  steps.push({ step: 'tree_document', ok: !!doc, node: doc ? { path: doc.path, role: doc.role, name: doc.name, bounds: doc.bounds } : null, total_nodes: nodes.length });
  steps.push({ step: 'doc_laid_out', ok: !!hasBounds, bounds: doc ? doc.bounds : null });

  await sleep(1000);
  const frame = win ? await captureFrame(xvfb.display, win.id) : null;
  steps.push({ step: 'frame_nonempty', ok: !!frame && frame.colors > 1 && frame.non_white_ratio > 0.005,
    colors: frame ? frame.colors : 0, non_white_ratio: frame ? Number(frame.non_white_ratio.toFixed(4)) : 0 });

  // 交互：点"下一页"（文档宿主的分页按钮；位置从树里取，取不到就用文档下沿附近）
  const nextBtn = nodes.find((n) => n.name === '下一页');
  const target = nextBtn && Array.isArray(nextBtn.bounds) && nextBtn.bounds[2] > 0
    ? nextBtn.bounds
    : (doc && Array.isArray(doc.bounds) ? [doc.bounds[0] + doc.bounds[2] / 2, doc.bounds[1] + doc.bounds[3] - 20, 1, 1] : null);
  if (target) {
    const [x, y, w, h] = target;
    spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${Math.round(x + w / 2)} ${Math.round(y + h / 2)} click 1`], { encoding: 'utf8', timeout: 20000 });
    await sleep(1500);
    steps.push({ step: 'click_alive', ok: app.proc.exitCode === null, clicked: nextBtn ? '下一页' : '文档下沿', exit_code: app.proc.exitCode });
  } else {
    steps.push({ step: 'click_alive', ok: false, note: '树里没有可点的 bounds' });
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
finish({
  acc: 'ACC-025',
  started,
  expectRef: 'ACC-025：FlowDocumentReader 结构 + 布局界限 + 帧非空 + 分页点击不崩',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, tree: treePath, steps },
  verdict: failed.length ? 'FAIL' : 'PASS',
  diff: failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
});
