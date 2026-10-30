#!/usr/bin/env node
// ACC-023：笔迹采集（TG-010，MOD-79 的 Ink 面）
//
// 判据口径（真跑，先红后绿）：
//   ①起 Xvfb + 样本 WpfMediaInk（SCR-06，XAML 里有 `<InkCanvas>`）；
//   ②只要应用能起来，就点画布拖一条笔迹，再读结构树看是否出现 Stroke 语义的节点；
//   ③应用**起不来**时，把真实异常打到 diff（不伪造通过）—— 当前的现场是
//     `System.PlatformNotSupportedException: COM Interop is not supported on this platform.`，
//     来自 `InkCanvas` 的笔输入初始化（PenImc 的 COM 面），这就是本条的缺口。

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT, FRAMEWORK_DIR } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-06';
const missingEnv = ['Xvfb', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-023', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-023：笔迹采集（InkCanvas 落笔 + Stroke 结构）', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}

const treePath = path.join(os.tmpdir(), `ldp-ink-${Date.now()}.json`);
const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
let app = null;
const steps = [];
let startupError = null;
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({ sample: 'WpfMediaInk', scr, display: xvfb.display, treeDump: treePath });
  if (!app.ok) {
    startupError = app.reason || '被测对象不可用';
  } else {
    const win = await findWindow(xvfb.display, `WpfMediaInk [${scr}]`);
    let waited = 0;
    while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
    const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || null;
    if (!win || !fs.existsSync(treePath)) startupError = firstErr || '25 s 内没等到窗口';
    else {
      await waitMapped(xvfb.display, win.id);
      const tree = JSON.parse(fs.readFileSync(treePath, 'utf8'));
      const ink = (tree.nodes || []).find((n) => n.name === '墨迹');
      steps.push({ step: 'tree_inkcanvas', ok: !!ink, node: ink || null });
      if (ink && Array.isArray(ink.bounds) && ink.bounds[2] > 0) {
        const [x, y, w, h] = ink.bounds;
        const cx = x + Math.floor(w / 2), cy = y + Math.floor(h / 2);
        spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${cx} ${cy} mousedown 1 mousemove ${cx + 60} ${cy + 30} mouseup 1`], { encoding: 'utf8', timeout: 20000 });
        await sleep(1200);
        const after = JSON.parse(fs.readFileSync(treePath, 'utf8'));
        const strokeLike = (after.nodes || []).filter((n) => /stroke|ink/i.test(String(n.name || '')) || /ink/i.test(String(n.role || '')));
        steps.push({ step: 'stroke_recorded', ok: strokeLike.length > 0, strokes: strokeLike.map((n) => ({ path: n.path, role: n.role, name: n.name })) });
      } else {
        steps.push({ step: 'stroke_recorded', ok: false, note: 'InkCanvas 没有可画的 bounds' });
      }
    }
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
const diff = failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`);
if (startupError) {
  diff.push(`应用起不来：${String(startupError).slice(0, 240)}`);
  diff.push('缺口：InkCanvas 的笔输入走 PenImc 的 COM 面，Linux 上无 COM ⇒ D-013');
}
finish({
  acc: 'ACC-023',
  started,
  expectRef: 'ACC-023：InkCanvas 落笔后结构树出现笔迹节点',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, tree: treePath, startup_error: startupError, steps },
  verdict: (failed.length || startupError) ? 'FAIL' : 'PASS',
  diff,
});
