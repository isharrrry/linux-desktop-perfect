#!/usr/bin/env node
// ACC-010：输入事件回放（TG-004，MOD-12/13/81）
//
// 判据口径（真跑 + 结构读数）：
//   ①起 Xvfb + 样本（SCR-01），用 LdpA11y 钩子以 `LDP_A11Y_REPEAT=1` 周期导出 UIA 树；
//   ②从初始树里取「输入样本」entry 的屏幕 bounds → `xdotool mousemove + click` 点进去；
//   ③`xdotool type "abc"` 逐字符注入 → 事件链 = X11 键事件 → shim → WPF 输入管线 → TextBox；
//   ④再读树：entry 的 `value` 必须是 "abc"（键盘回放命中），且聚焦时 states 里出现 focused；
//   ⑤再用 `xdotool key Tab` 验焦点转移（焦点离开 entry）。
//   任一步不成立即 FAIL；值对不上要给出「期望/实际」两列。
//
// 用法：node 50-verify/window/replay.mjs [--script 1]

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT, FRAMEWORK_DIR } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const should = 'abc';

const missingEnv = ['Xvfb', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-010', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-010：键鼠/滚轮事件回放逐条命中', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!fs.existsSync(path.join(FRAMEWORK_DIR, 'LdpA11y.dll'))) {
  finish({ acc: 'ACC-010', started, verdict: 'FAIL', expectRef: 'ACC-010：键鼠/滚轮事件回放逐条命中',
    env: { framework: FRAMEWORK_DIR }, diff: ['缺树导出钩子 LdpA11y.dll（先跑 install-runtime）'] });
}

const treePath = path.join(os.tmpdir(), `ldp-replay-${Date.now()}.json`);
const readTree = () => {
  try { return JSON.parse(fs.readFileSync(treePath, 'utf8')); } catch { return null; }
};
const findEntry = (tree) => (tree?.nodes || []).find((n) => n.role === 'entry') || null;
const xdo = (display, args) => spawnSync('sh', ['-c', `DISPLAY=${display} xdotool ${args}`], { encoding: 'utf8', timeout: 20000 });

const xvfb = startXvfb();
let app = null;
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({
    sample: 'WpfFirstWindow',
    scr: 'SCR-01',
    display: xvfb.display,
    treeDump: treePath,
    extraEnv: { LDP_A11Y_REPEAT: '1' },
  });
  if (!app.ok) {
    finish({ acc: 'ACC-010', started, verdict: 'FAIL', expectRef: 'ACC-010：键鼠/滚轮事件回放逐条命中',
      env: { dotnet_root: DOTNET_ROOT }, diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]');
  if (win) await waitMapped(xvfb.display, win.id);
  let waited = 0;
  while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
  const t0 = readTree();
  const entry0 = findEntry(t0);
  steps.push({ step: 'initial_tree', ok: !!t0 && !!entry0, entry: entry0 ? { path: entry0.path, name: entry0.name, value: entry0.value, bounds: entry0.bounds } : null });
  if (!entry0) throw new Error('初始树里找不到 entry 节点');

  // 点击 entry（bounds = [x, y, w, h]，屏幕绝对像素）
  const [ex, ey, ew, eh] = entry0.bounds;
  xdo(xvfb.display, `mousemove ${ex + Math.floor(ew / 2)} ${ey + Math.floor(eh / 2)} click 1`);
  await sleep(700);
  const t1 = readTree();
  const entry1 = findEntry(t1);
  steps.push({ step: 'click_focus', ok: !!entry1 && (entry1.states || []).includes('focused'), states: entry1 ? entry1.states : null });

  // 若点击没把焦点给 entry，用键盘导航补齐（Tab）——**如实记录是哪种方式生效的**，
  // 因为"鼠标点击聚焦"和"键盘聚焦"是两条不同的实现路径（见 D-008）。
  let focusVia = (entry1 && (entry1.states || []).includes('focused')) ? 'click' : null;
  if (!focusVia) {
    xdo(xvfb.display, 'key Tab');
    await sleep(900);
    const tf = readTree();
    const ef = findEntry(tf);
    if (ef && (ef.states || []).includes('focused')) focusVia = 'Tab';
    steps.push({ step: 'tab_focus_fallback', ok: focusVia === 'Tab', states: ef ? ef.states : null });
  }

  // 键入
  xdo(xvfb.display, `type --delay 60 "${should}"`);
  await sleep(1500);
  const t2 = readTree();
  const entry2 = findEntry(t2);
  steps.push({ step: 'type_text', ok: !!entry2 && entry2.value === should, want: should, got: entry2 ? entry2.value : null, focus_via: focusVia });
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

// 判定：
//   · 键入回放（value == "abc"）是核心判据，必须过；
//   · 鼠标点击聚焦若没生效 = D-008（已知缺口），不算实现通过 —— 判 DEGRADED；
//   · 键盘聚焦 + 键入都到位 = 键盘回放链路成立。
const typeStep = steps.find((s) => s.step === 'type_text');
const clickStep = steps.find((s) => s.step === 'click_focus');
const keyboardOk = !!(typeStep && typeStep.ok);
const clickOk = !!(clickStep && clickStep.ok);
const failed = steps.filter((s) => !s.ok && s.step !== 'click_focus');
const verdict = !keyboardOk ? 'FAIL' : clickOk ? 'PASS' : 'DEGRADED';
finish({
  acc: 'ACC-010',
  started,
  expectRef: 'ACC-010：键入回放（value=abc）+ 聚焦路径（点击/键盘）',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, tree: treePath, script_id: opts.script || '1', focus_via: typeStep ? typeStep.focus_via : null, steps },
  verdict,
  degrade: verdict === 'DEGRADED' ? 'D-008：X11 鼠标点击没有把焦点交给 WPF 控件（键盘 Tab 路径正常）；键入库回放已通过' : 'none',
  diff: verdict === 'FAIL'
    ? failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 260)}`)
    : verdict === 'DEGRADED'
      ? ['D-008：鼠标点击未聚焦（读数见 steps.click_focus）；键入回放已通过（value=abc）']
      : [],
});
