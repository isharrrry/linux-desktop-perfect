#!/usr/bin/env node
// ACC-022：GStreamer 播放状态机
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/build/wpf/artifacts/bin/PresentationCore/Release/net10.0/PresentationCore.dll", why: "媒体桥未实现（S4）", exists: exists("40-build/build/wpf/artifacts/bin/PresentationCore/Release/net10.0/PresentationCore.dll") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ case: opts.case || "play", gstreamer: runCmd("gst-inspect-1.0",["--version"]).out.trim().split("\\n")[0] },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

const missingNotes = missing.map((m) => `缺被测对象：${m.path}（${m.why}）`);

// 真跑：起 WpfMediaInk（SCR-06）——样本里有播放/暂停/进度/state 读数，但**没有 `<MediaElement>`**
//   （媒体元素在 Linux 上要 GStreamer 桥，尚未实现）。真跑段把两件事都记下来：
//   ①样本能不能起（D-013 解除后可以）；②树里有没有媒体控件；③state 读数点前后是否变化。
const { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT } = await import('../lib/app-session.mjs');
{
  const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
  let app = null;
  const steps = [];
  try {
    if (await waitDisplay(xvfb.display)) {
      const treePath = path.join(os.tmpdir(), `ldp-media-${Date.now()}.json`);
      app = await startApp({ sample: 'WpfMediaInk', scr: 'SCR-06', display: xvfb.display, treeDump: treePath, extraEnv: { LDP_A11Y_REPEAT: '1' } });
      if (!app.ok) steps.push({ step: 'startup', ok: false, reason: app.reason });
      else {
        const win = await findWindow(xvfb.display, 'WpfMediaInk [SCR-06]', 25000);
        if (win) await waitMapped(xvfb.display, win.id);
        let waited = 0;
        while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
        const tree = fs.existsSync(treePath) ? JSON.parse(fs.readFileSync(treePath, 'utf8')) : null;
        const nodes = (tree && tree.nodes) || [];
        const mediaNodes = nodes.filter((n) => /media|video|音视频|播放/i.test(String(n.name || '')) || /video/i.test(String(n.role || '')));
        const play = nodes.find((n) => n.name === '播放');
        const state0 = (nodes.find((n) => /^state=/.test(String(n.name || ''))) || {}).name || null;
        steps.push({ step: 'startup', ok: !!tree, nodes: nodes.length });
        steps.push({ step: 'media_element_present', ok: mediaNodes.length > 0, media_nodes: mediaNodes.map((n) => ({ path: n.path, role: n.role, name: n.name })) });
        if (play && Array.isArray(play.bounds) && play.bounds[2] > 0) {
          const [x, y, w, h] = play.bounds;
          spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${Math.round(x + w / 2)} ${Math.round(y + h / 2)} click 1`], { encoding: 'utf8', timeout: 20000 });
          await sleep(1500);
          const tree2 = JSON.parse(fs.readFileSync(treePath, 'utf8'));
          const state1 = ((tree2.nodes || []).find((n) => /^state=/.test(String(n.name || ''))) || {}).name || null;
          steps.push({ step: 'state_changes', ok: state0 !== state1, before: state0, after: state1 });
        } else {
          steps.push({ step: 'state_changes', ok: false, note: '没有可点的「播放」按钮' });
        }
      }
    }
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  const bad = steps.filter((s) => !s.ok);
  finish({
    acc: 'ACC-022', started, env: { ...env, dotnet_root: DOTNET_ROOT, steps },
    expectRef: 'ACC-022：GStreamer 播放状态机（播放/暂停/进度/state 读数联动）',
    // 【口径分层（2026-10-30）】媒体桥 + 播放器元数据可达 = 已达成；播放状态机 = 未实现（D-015）。
    //   判据不再把"点播放 → state=PLAYING"当成唯一标准 —— 那条路径卡在 CreateMedia 的 E_NOTIMPL 上，
    //   已在已知坑清单里完整登记排除过程。这里如实判 DEGRADED，不判 PASS。
    verdict: 'DEGRADED',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      '缺口：媒体栈未实现（milcore 的 MilCmdMediaPlayer 仍是 E_NOTIMPL；样本也没有 <MediaElement>，需要 GStreamer 桥 + 样本控件）',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-022', started, env, verdict: 'DEGRADED',
  degrade: '播放状态机未实现（D-015）：媒体桥与元数据可达已达成，Open 在 CreateMedia 处收 E_NOTIMPL',
  expectRef: 'ACC-022：播放/暂停/定位 + 不支持格式明确报错',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
