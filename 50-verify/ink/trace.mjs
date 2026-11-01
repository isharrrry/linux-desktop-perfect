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
        // 【K-264/K-265 修复】画一笔前后各截一张图，用**像素差**判定是否真的画出东西。
        //   为什么必须改：原来的判定是
        //     `strokeLike = nodes.filter(n => /stroke|ink/i.test(n.name) || /ink/i.test(n.role))`
        //   而根窗口名是 `WpfMediaInk` —— `/ink/i` 匹配了 "Media**Ink**" ⇒ **恒为真**（假阳性，K-264）。
        //   而且 `WpfMediaInk` 含 `<MediaElement>` ⇒ 命中官方 MIL 限制（K-257）⇒ **整个画面空白**
        //   ⇒ 连「InkCanvas 有没有画上」都观测不到（K-265）。
        //   ⇒ 改用像素差（不受 UIA 节点名巧合影响）+ 保留 UIA 作为辅助证据。
        const shotBefore = path.join(os.tmpdir(), `acc023-before-${Date.now()}.png`);
        const shotAfter = path.join(os.tmpdir(), `acc023-after-${Date.now()}.png`);
        spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} import -window root ${JSON.stringify(shotBefore)}`], { timeout: 20000 });
        spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${cx} ${cy} mousedown 1 mousemove ${cx + 60} ${cy + 30} mousemove ${cx + 120} ${cy + 60} mouseup 1`], { encoding: 'utf8', timeout: 20000 });
        await sleep(1200);
        spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} import -window root ${JSON.stringify(shotAfter)}`], { timeout: 20000 });
        // `compare -metric AE` 输出"不同像素数"到 stderr（ImageMagick 的惯例）
        let pxDiff = -1;
        if (fs.existsSync(shotBefore) && fs.existsSync(shotAfter)) {
          const cmp = spawnSync('sh', ['-c', `compare -metric AE ${JSON.stringify(shotBefore)} ${JSON.stringify(shotAfter)} null: 2>&1`], { encoding: 'utf8', timeout: 20000 });
          const m = String(cmp.stdout || '').trim().match(/^(\d+)/);
          if (m) pxDiff = Number(m[1]);
        }
        const after = JSON.parse(fs.readFileSync(treePath, 'utf8'));
        const inkAfter = (after.nodes || []).find((n) => n.name === '墨迹');
        // 【排除示例名】只认"墨迹"节点本身及其子节点，不再用 /ink/i 匹配任意节点
        const strokesUnderInk = inkAfter && Array.isArray(inkAfter.children) ? inkAfter.children.length : 0;
        const childCount = inkAfter ? Number(inkAfter.child_count || 0) : 0;
        // 【K-268 二次修正】**`pxDiff > 0` 不够** —— 实测 `WpfMediaInk` 画一笔后
        //   截图从 **13988 字节掉到 324 字节**（= 整个画面被打空），像素差 819388。
        //   那是"画面消失"而不是"笔画出现"。⇒ 判定必须同时满足：
        //     ① 像素差 > 0（有变化）
        //     ② **画后仍有实质内容**（`shot_after_bytes` 不低于画前的 60%）
        //   ②是关键：真正的笔画只会在局部增加像素，不会让整屏变空。
        const beforeB = fs.existsSync(shotBefore) ? fs.statSync(shotBefore).size : 0;
        const afterB  = fs.existsSync(shotAfter)  ? fs.statSync(shotAfter).size  : 0;
        const contentKept = beforeB > 0 && afterB >= Math.floor(beforeB * 0.6);
        const strokeOk = pxDiff > 0 && contentKept;
        steps.push({
          step: 'stroke_recorded',
          ok: strokeOk,
          pixel_diff: pxDiff,
          content_kept: contentKept,
          shot_before_bytes: beforeB,
          shot_after_bytes: afterB,
          ink_child_count: childCount,
          strokes_under_ink: strokesUnderInk,
          note: strokeOk ? '画面有变化且内容保留 ⇒ 笔画生效'
                : (pxDiff > 0 && !contentKept ? '**画面被打空**（画后截图骤降）⇒ 不是笔画生效'
                : '画面无变化 ⇒ **笔画没画上**'),
        });
        // 【附加证据】应用是否存活（区分"画不上"与"崩了"）
        steps.push({ step: 'app_alive_after_stroke', ok: !!(app && app.ok && app.proc && app.proc.exitCode === null),
                     exitCode: app && app.proc ? app.proc.exitCode : null });
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
