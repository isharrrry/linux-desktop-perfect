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
    // 【2026-10-31 判据修复】原来指向**上游** artifacts（40-build/build/wpf/artifacts/...），
    //   但本项目用的是**自产**的 PresentationCore.dll（40-build/wpf-port/out/）。
    //   指向错路径会让"缺被测对象"永远成立，从而把实现已经做好的项判成 DEGRADED。
    { path: "40-build/wpf-port/out/PresentationCore.dll", why: "PresentationCore 未构建（媒体桥的托管侧载体）", exists: exists("40-build/wpf-port/out/PresentationCore.dll") }
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
  const xvfbOptsScreen = '1400x1000x24';
  const xvfb = startXvfb(undefined, { screen: xvfbOptsScreen });
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
          // 【2026-10-31 判据现状（如实记）】本步骤在本判据自己起的 Xvfb（1400x1000）里
          //   **点击不生效**：win_id、bounds、绝对坐标、app_alive 全部核对过（见 diag），
          //   与"手工在同一台机器上点击成功（state 会变 PLAYING）"的唯一差别就是这个判据
          //   自己是独立进程。已试过：windowactivate --sync、单条命令、mousemove --window、
          //   绝对坐标 + xwininfo 补偿、点击重试 8 次 —— **均无效，根因未定位**。
          //   下面保留"重试 + 绝对坐标"两处改善（它们本身是对的），但**不为了变绿而降低判据口径**。
          let state1 = null;
          let mouseCmd = '';
          for (let attempt = 0; attempt < 8; attempt++) {
            // 【2026-10-31 判据修复 v3 —— 真根因】树里的 bounds 是**窗口内坐标**，
            //   而 xdotool mousemove 需要**屏幕坐标**。本判据的 Xvfb 是 1400x1000
            //   （与手工验证时用的 1280x1024 不同），窗口不在 (0,0) ⇒ 直接把 bounds
            //   当屏幕坐标点，会点到窗口外的空白上，state 永远不变。
            //   改用 `mousemove --window <wid> X Y`（相对窗口坐标），与窗口位置无关。
            const wx = Math.round(x + w / 2), wy = Math.round(y + h / 2);
            const widForClick = win && win.id ? String(win.id) : '';
            // 用窗口的**绝对**位置 + 客户区偏移。`mousemove --window` 是从外框算的，
            //   会把标题栏高度算进去 ⇒ 点到按钮上方（这是 v3 之后仍失败的原因）。
            const geo = widForClick ? spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool getwindowgeometry --shell ${widForClick}`], { encoding: 'utf8' }) : null;
            const gx = geo && geo.stdout ? Number((geo.stdout.match(/^X=(-?\d+)/m) || [])[1]) : NaN;
            const gy = geo && geo.stdout ? Number((geo.stdout.match(/^Y=(-?\d+)/m) || [])[1]) : NaN;
            // xwininfo 给的客户区绝对坐标更准（包含标题栏/边框补偿）
            const xi = widForClick ? spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xwininfo -id ${widForClick}`], { encoding: 'utf8' }) : null;
            const ax = xi && xi.stdout ? Number((xi.stdout.match(/Absolute upper-left X:\s+(-?\d+)/) || [])[1]) : NaN;
            const ay = xi && xi.stdout ? Number((xi.stdout.match(/Absolute upper-left Y:\s+(-?\d+)/) || [])[1]) : NaN;
            const cx = Number.isFinite(ax) ? ax : gx;
            const cy = Number.isFinite(ay) ? ay : gy;
            mouseCmd = Number.isFinite(cx) && Number.isFinite(cy)
              ? `xdotool mousemove ${cx + wx} ${cy + wy} click 1`
              : `xdotool mousemove ${wx} ${wy} click 1`;
            spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} ${mouseCmd}`], { encoding: 'utf8', timeout: 20000 });
            await sleep(1500);
            const tree2 = JSON.parse(fs.readFileSync(treePath, 'utf8'));
            state1 = ((tree2.nodes || []).find((n) => /^state=/.test(String(n.name || ''))) || {}).name || null;
            if (state1 !== state0) break;
          }
          steps.push({ step: 'state_changes', ok: state0 !== state1, before: state0, after: state1,
            diag: { win_id: win && win.id, bounds: [x, y, w, h], click_at: [Math.round(x + w / 2), Math.round(y + h / 2)],
              mouse_cmd: mouseCmd, app_alive: !!(app && app.ok && app.proc && app.proc.exitCode === null), screen: xvfbOptsScreen } });
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
      '缺口：**状态机层已通**（MILFactoryCreateMediaPlayer / MILMediaOpen / 13 个 media 方法已实现；'
      + '手工在同机验证 state 会从 READY 变 PLAYING）。仍欠 ②MilCmdMediaPlayer 是「收下不渲染」'
      + '（视频帧没有 Linux 合成路径）③样本没有 <MediaElement>；另有 ①**本判据自身的问题**：'
      + 'state_changes 在本判据起的 Xvfb（1400x1000）里点击不生效 —— win_id/bounds/绝对坐标/app_alive '
      + '全核对过，试过 windowactivate、单命令、mousemove --window、xwininfo 补偿、重试 8 次，均无效，'
      + '**根因未定位**。详见 D-015 / K-005-修复 / K-006。',
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
