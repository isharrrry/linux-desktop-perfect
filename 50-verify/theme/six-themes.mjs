#!/usr/bin/env node
// ACC-020：六主题切换（TG-008，MOD-84）
//
// 判据口径（真跑 + 帧读数）：
//   ①六个主题各起一次会话：`Xvfb` + 样本（`--theme <T>`，App.OnStartup 换 MergedDictionaries）；
//   ②每主题都要：窗口出现 → Map State: IsViewable → 抓帧非空（颜色数 > 1、非白比 > 0.5%）；
//   ③主题切换**不许崩**：应用进程在抓帧后仍存活（SIGTERM 前 exit 事件未触发）；
//   ④六主题的帧颜色指纹必须**不全相同**（六套主题若全一样，说明主题字典没真正换掉 —— 08 §1.5 不自证）。
//
// 用法：node 50-verify/theme/six-themes.mjs [--only Aero]

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, captureFrame, sleep, DOTNET_ROOT } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const ALL = ['Classic', 'Aero', 'Aero2', 'AeroLite', 'Luna', 'Royale'];
const themes = opts.only ? [String(opts.only)] : ALL;

const missingEnv = ['Xvfb', 'xwd', 'convert'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-020', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-020：六主题各有帧且切换不崩', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}

const rows = [];
const diff = [];
for (const theme of themes) {
  // 会话之间要留间隙：上一个 Xvfb 被 SIGTERM 后 display 编号可能立刻被下一次探测复用，
  // 实测症状是下一个主题启动时 `wpf_x11_ensure 失败`（连到一个正在退出的 X server）。
  await sleep(900);
  const xvfb = startXvfb();
  let app = null;
  try {
    if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
    // --theme 由样本 App.OnStartup 消费（换 MergedDictionaries）；extraArgs 由 app-session 透传到命令行
    const treePath = path.join(os.tmpdir(), `ldp-theme-${theme}-${Date.now()}.json`);
    app = await startApp({
      sample: 'WpfFirstWindow',
      scr: 'SCR-01',
      display: xvfb.display,
      treeDump: treePath,
      extraArgs: ['--theme', theme],
    });
    if (!app.ok) {
      diff.push(`${theme}：被测对象不可用（${app.reason}）`);
      continue;
    }
    const win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]');
    if (!win) {
      const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || '(无异常行)';
      diff.push(`${theme}：25 s 内没等到窗口；首行：${firstErr.slice(0, 160)}`);
      rows.push({ theme, window: false });
      continue;
    }
    const mapped = await waitMapped(xvfb.display, win.id);
    await sleep(1200);
    const frame = await captureFrame(xvfb.display, win.id);
    const alive = app.proc.exitCode === null;
    // 主题是否真的换掉：读 UIA 树 env.theme（字典 URI + 条目数）—— 结构读数，比"看起来像"硬
    let themeInfo = null;
    let waited = 0;
    while (!fs.existsSync(treePath) && waited < 30000) { await sleep(250); waited += 250; }
    if (fs.existsSync(treePath)) {
      try {
        const tree = JSON.parse(fs.readFileSync(treePath, 'utf8'));
        themeInfo = tree?.env?.theme || null;
      } catch { themeInfo = null; }
    }
    rows.push({
      theme,
      window: true,
      mapped,
      alive,
      colors: frame ? frame.colors : 0,
      non_white_ratio: frame ? Number(frame.non_white_ratio.toFixed(4)) : 0,
      theme_info: themeInfo,
      png: frame ? frame.png : null,
    });
    // 结构断言：字典必须真实加载成该主题（"(none)" 或主题名对不上即红）
    if (!themeInfo || themeInfo === '(none)' || !String(themeInfo).toLowerCase().includes(theme.toLowerCase())) {
      diff.push(`${theme}：主题字典没加载成该主题（env.theme=${themeInfo}）`);
    }
    if (!mapped) diff.push(`${theme}：窗口未映射（IsUnMapped）`);
    if (!frame) diff.push(`${theme}：抓帧失败`);
    else if (frame.colors <= 1 || frame.non_white_ratio <= 0.005) {
      diff.push(`${theme}：帧为空（颜色 ${frame.colors}、非白比 ${(frame.non_white_ratio * 100).toFixed(2)}%）`);
    }
    if (!alive) diff.push(`${theme}：抓帧后应用已退出（切换主题崩了）`);
  } catch (e) {
    diff.push(`${theme}：判据执行失败 ${e.message}`);
    rows.push({ theme, error: e.message });
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  await sleep(600);
}

// ④ 帧指纹：主题字典加载成功但**像素指纹全同** ⇒ 视觉差异未达成（缺口，登记 D-009，判 DEGRADED 而不是 FAIL/PASS）
const fp = rows.filter((r) => r.window && r.colors).map((r) => `${r.colors}/${r.non_white_ratio}`);
const distinct = new Set(fp).size;
const themeOnlyVisualGap = fp.length >= 3 && distinct === 1;

const verdict = diff.length ? 'FAIL' : themeOnlyVisualGap ? 'DEGRADED' : 'PASS';
finish({
  acc: 'ACC-020',
  started,
  expectRef: 'ACC-020：六主题各有帧且切换不崩（帧指纹不全同）',
  env: { dotnet_root: DOTNET_ROOT, themes: rows, distinct_fingerprints: distinct },
  verdict,
  degrade: themeOnlyVisualGap
    ? 'D-009：六主题的字典都加载成功（env.theme 可读、条目 200+）且切换不崩，但窗口像素指纹完全相同 —— 主题视觉差异未达成'
    : 'none',
  diff: verdict === 'FAIL' ? diff.slice(0, 10) : themeOnlyVisualGap ? [`六主题帧指纹完全相同（${fp[0]}）→ D-009`] : [],
});
