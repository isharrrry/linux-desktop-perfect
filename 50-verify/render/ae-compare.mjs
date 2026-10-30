#!/usr/bin/env node
// ACC-008：AE 帧对照（TG-003，渲染回归的"牙齿"）
//
// 判据口径：
//   · `--baseline`：把 7 个样本各跑一次、抓帧存成 `50-verify/render/baseline/<样本>.png`
//     （**基线是本仓自产**：本机没有 Windows 可比对，读数里如实标 baseline_source，
//      不冒充"与 Windows 基准一致"）；
//   · 默认：再跑一遍，逐像素与基线比 AE（ImageMagick `compare -metric AE`），
//     差异像素占比 ≤ 阈值（默认 1%）即"这一屏没退步"。
//
// 为什么这条判据值得存在：它是**后续改动的回归闸门** —— 渲染/输入/主题任何一处动过，
// 这一跑就能看出哪一屏像素变了。
//
// 用法：
//   node 50-verify/render/ae-compare.mjs --baseline      # 采集/刷新基线
//   node 50-verify/render/ae-compare.mjs                 # 与基线比对

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, abs, exists, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, captureFrame, sleep, DOTNET_ROOT } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const doBaseline = Object.prototype.hasOwnProperty.call(opts, 'baseline') && opts.baseline !== '0' && opts.baseline !== 'false';
const thresholdPct = Number(opts.threshold || 1.0);
const BASE_DIR = abs('50-verify/render/baseline');

// 场景表：每条 = 样本 + 交互变体。变体让同一界面的**不同状态**各成一屏，
// 这样回归闸门不只盯"空窗口"，还能盯住"改过尺寸/滚过/换过主题"的帧。
const SCENES = [
  { sample: 'WpfFirstWindow', scr: 'SCR-01', variant: 'default' },
  { sample: 'WpfFirstWindow', scr: 'SCR-01', variant: 'resized', width: 720, height: 520 },
  { sample: 'WpfTextDemo', scr: 'SCR-02', variant: 'default' },
  { sample: 'WpfTextDemo', scr: 'SCR-02', variant: 'scrolled', scroll: 3 },
  { sample: 'WpfImageGallery', scr: 'SCR-03', variant: 'default' },
  { sample: 'WpfControlGallery', scr: 'SCR-04', variant: 'default' },
  { sample: 'WpfControlGallery', scr: 'SCR-04', variant: 'scrolled', scroll: 4 },
  { sample: 'WpfPrintDoc', scr: 'SCR-05', variant: 'default' },
  { sample: 'WpfPrintDoc', scr: 'SCR-05', variant: 'scrolled', scroll: 3 },
  // 主题变体：换主题字典后同一屏的像素应稳定（主题切换是 ACC-020 的覆盖面，这里只做回归锚点）
  { sample: 'WpfFirstWindow', scr: 'SCR-01', variant: 'theme-aero', theme: 'Aero' },
  { sample: 'WpfFirstWindow', scr: 'SCR-01', variant: 'theme-luna', theme: 'Luna' },
  { sample: 'WpfTextDemo', scr: 'SCR-02', variant: 'theme-aerolite', theme: 'AeroLite' },
  // 下面两屏所属切片尚未实现（媒体/Ink=ACC-022/023、WinForms=ACC-028）：
  // 它们起不来属于"切片刻度未到"，不计为 AE 回归失败，但要如实写进读数并让整体判 DEGRADED。
  { sample: 'WpfMediaInk', scr: 'SCR-06', variant: 'default', pendingSlice: 'ACC-022/023（媒体/Ink）' },
  { sample: 'WinFormsBasics', scr: 'SCR-07', variant: 'default', pendingSlice: 'ACC-028（WinForms）' },
];

const missingEnv = ['Xvfb', 'xwd', 'convert', 'compare'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-008', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-008：多场景 PNG 与基准的 AE 指标在阈值内', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}

const sceneKey = (sc) => (sc.variant && sc.variant !== 'default' ? `${sc.sample}-${sc.variant}` : sc.sample);

const captureOne = async (scene) => {
  const { sample, scr } = scene;
  const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
  let app = null;
  try {
    if (!(await waitDisplay(xvfb.display))) return { sample, scr, ok: false, reason: `Xvfb ${xvfb.display} 未就绪` };
    app = await startApp({ sample, scr, display: xvfb.display, extraArgs: scene.theme ? ['--theme', scene.theme] : [] });
    if (!app.ok) return { sample, scr, ok: false, reason: app.reason || '被测对象不可用' };
    const win = await findWindow(xvfb.display, `${sample} [${scr}]`);
    if (!win) {
      const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || '';
      return { sample, scr, ok: false, reason: `25 s 内没等到窗口${firstErr ? `；首行：${firstErr.slice(0, 120)}` : ''}` };
    }
    await waitMapped(xvfb.display, win.id);
    await sleep(1200);
    // 变体动作：改尺寸 / 滚轮下滚 / （换主题已在启动参数里）
    if (scene.width && scene.height) {
      spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool windowsize ${win.id} ${scene.width} ${scene.height}`], { encoding: 'utf8', timeout: 15000 });
      await sleep(800);
    }
    if (scene.scroll) {
      const g = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xwininfo -id ${win.id} | egrep "Absolute upper-left"`], { encoding: 'utf8' }).stdout || '';
      const gx = Number((g.match(/X:\s+(-?\d+)/) || [])[1] || 0);
      const gy = Number((g.match(/Y:\s+(-?\d+)/) || [])[1] || 0);
      const [cx, cy] = [gx + Math.floor(win.w / 2), gy + Math.floor(win.h / 2)];
      spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${cx} ${cy} click 4 --repeat ${scene.scroll}`], { encoding: 'utf8', timeout: 20000 });
      await sleep(900);
    }
    const frame = await captureFrame(xvfb.display, win.id);
    return frame
      ? { sample, scr, ok: true, png: frame.png, width: frame.width, height: frame.height, colors: frame.colors, non_white_ratio: Number(frame.non_white_ratio.toFixed(4)) }
      : { sample, scr, ok: false, reason: '抓帧失败' };
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
};

const aeRatio = (a, b) => {
  const r = spawnSync('compare', ['-metric', 'AE', a, b, 'null:'], { encoding: 'utf8', timeout: 60000 });
  const num = Number(((r.stderr || '').trim().split(/\s+/).pop() || '').replace(/[^0-9.]/g, ''));
  return Number.isFinite(num) ? num : null;
};

const rows = [];
for (const scene of SCENES) {
  await sleep(500);
  const cap = await captureOne(scene);
  if (!cap.ok) { rows.push({ ...scene, ok: false, reason: cap.reason }); continue; }
  if (doBaseline) {
    fs.mkdirSync(BASE_DIR, { recursive: true });
    const dst = path.join(BASE_DIR, `${sceneKey(scene)}.png`);
    fs.copyFileSync(cap.png, dst);
    rows.push({ ...scene, key: sceneKey(scene), ok: true, baseline: path.relative(abs('.'), dst), colors: cap.colors, non_white_ratio: cap.non_white_ratio });
    continue;
  }
  const base = path.join(BASE_DIR, `${sceneKey(scene)}.png`);
  if (!fs.existsSync(base)) { rows.push({ ...scene, ok: false, reason: `无基线：${path.relative(abs('.'), base)}（先跑 --baseline）` }); continue; }
  const ae = aeRatio(base, cap.png);
  const total = cap.width * cap.height;
  const pct = ae == null ? null : Number(((ae / total) * 100).toFixed(3));
  rows.push({ ...scene, ok: ae != null, ae_pixels: ae, ae_pct: pct, threshold_pct: thresholdPct, colors: cap.colors, non_white_ratio: cap.non_white_ratio, png: cap.png, baseline: path.relative(abs('.'), base) });
}

if (doBaseline) {
  const done = rows.filter((r) => r.ok);
  fs.mkdirSync(BASE_DIR, { recursive: true });
  fs.writeFileSync(path.join(BASE_DIR, 'manifest.json'), JSON.stringify({
    created: new Date().toISOString(),
    baseline_source: '本仓自产（本机无 Windows 可比对；ACC-008 的 Windows 基准待补）',
    scenes: done.map((r) => ({ sample: r.sample, scr: r.scr, variant: r.variant, baseline: r.baseline })),
    failed: rows.filter((r) => !r.ok).map((r) => ({ sample: r.sample, scr: r.scr, variant: r.variant, reason: r.reason })),
  }, null, 2) + '\n');
  finish({
    acc: 'ACC-008', started, expectRef: 'ACC-008：基线采集',
    env: { dotnet_root: DOTNET_ROOT, mode: 'baseline', scenes: rows, baseline_dir: path.relative(abs('.'), BASE_DIR) },
    verdict: done.length ? 'PASS' : 'FAIL',
    degrade: done.length < SCENES.length ? `基线覆盖 ${done.length}/${SCENES.length}（其余样本起不来，读数见 env.scenes）` : 'none',
    diff: rows.filter((r) => !r.ok).map((r) => `${r.sample}（${r.scr}）：${r.reason}`),
  });
}

const bad = rows.filter((r) => (!r.ok && !r.pendingSlice) || (r.ae_pct != null && r.ae_pct > thresholdPct));
const skipped = rows.filter((r) => !r.ok && r.pendingSlice);
const compared = rows.filter((r) => r.ok);
finish({
  acc: 'ACC-008',
  started,
  expectRef: `ACC-008：${SCENES.length} 屏逐像素 AE 差异 ≤ ${thresholdPct}%（基线为本仓自产）`,
  env: {
    dotnet_root: DOTNET_ROOT,
    mode: 'compare',
    threshold_pct: thresholdPct,
    baseline_source: exists('50-verify/render/baseline/manifest.json')
      ? (JSON.parse(fs.readFileSync(abs('50-verify/render/baseline/manifest.json'), 'utf8')).baseline_source || '本仓自产')
      : '本仓自产',
    scenes: rows,
  },
  verdict: bad.length ? 'FAIL' : skipped.length ? 'DEGRADED' : 'PASS',
  degrade: skipped.length
    ? `切片刻度未到：${skipped.map((r) => `${r.sample}（${r.pendingSlice}）`).join('、')}；已比对 ${compared.length}/${SCENES.length} 屏`
    : 'none',
  diff: bad.map((r) => (r.ok ? `${r.sample}：AE ${r.ae_pct}% > ${thresholdPct}%` : `${r.sample}（${r.scr}）：${r.reason}`))
    .concat(skipped.map((r) => `${r.sample}：切片未到（${r.pendingSlice}）—— ${r.reason}`)),
});
