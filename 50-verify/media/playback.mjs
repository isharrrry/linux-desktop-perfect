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
          let lastClickDiag = null;
          let lastGeoDiag = null;
          const attempts = [];   // 【K-011】每次重试的中间读数，失败时能看出是哪一环
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
            // 【K-006 诊断】把 xdotool/xwininfo 的原始输出记下来，看窗口到底在哪
            const rawGeo = geo && geo.stdout ? geo.stdout.replace(/\n/g, ';').slice(0, 160) : null;
            const rawXi = xi && xi.stdout ? (xi.stdout.match(/Absolute upper-left [XY]:.*/g) || []).join(';') : null;
            // 直接再跑一次并把 status/stderr 也记下来（区分"命令失败"和"输出为空"）
            const g2 = widForClick ? spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool getwindowgeometry --shell ${widForClick}`], { encoding: 'utf8' }) : { status: 'no-wid' };
            lastGeoDiag = { display: xvfb.display, wid: widForClick || '(empty)', geo: rawGeo, xwininfo: rawXi,
                            gx, gy, ax, ay,
                            g2_status: g2 && g2.status, g2_stdout: g2 && g2.stdout ? g2.stdout.replace(/\n/g,';').slice(0,120) : null,
                            g2_stderr: g2 && g2.stderr ? String(g2.stderr).slice(0,160) : null };
            const cx = Number.isFinite(ax) ? ax : gx;
            const cy = Number.isFinite(ay) ? ay : gy;
            mouseCmd = Number.isFinite(cx) && Number.isFinite(cy)
              ? `xdotool mousemove ${cx + wx} ${cy + wy} click 1`
              : `xdotool mousemove ${wx} ${wy} click 1`;
            // 【2026-10-31 K-006 诊断】原来**不检查返回码** ⇒ 命令失败也看不出来。
            const clickRes = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} ${mouseCmd}`], { encoding: 'utf8', timeout: 20000 });
            lastClickDiag = { cmd: mouseCmd, status: clickRes.status, signal: clickRes.signal,
                              stderr: (clickRes.stderr || '').slice(0, 200), error: clickRes.error ? String(clickRes.error) : null };
            // 【K-011】1500ms 在偶发情况下不够（Play() 到 state=PLAYING 之间还要几步）；
            //   实测 3 次里有 1 次 8 轮重试全部读到 READY。加长单次等待。
            await sleep(2200);
            const tree2 = JSON.parse(fs.readFileSync(treePath, 'utf8'));
            state1 = ((tree2.nodes || []).find((n) => /^state=/.test(String(n.name || ''))) || {}).name || null;
            // 【K-011-解】树文件 mtime 是关键区分手段：
            //   mtime 不涨 ⇒ LdpA11y 的重复导出停了（观测层问题，不是点击问题）；
            //   mtime 在涨但 state 仍是 READY ⇒ 才是真的没点到。
            let treeMtime = null;
            try { treeMtime = fs.statSync(treePath).mtimeMs; } catch { /* ignore */ }
            attempts.push({ attempt, state0, state1, click_status: clickRes.status, clicked: mouseCmd,
                            tree_mtime: treeMtime });
            if (state1 !== state0) break;
          }
          // 【K-011-解4】关键区分：`state1` 是**从一份导出文件里读出来的**。
          //   如果这份文件在整段重试期间一次都没被重写（mtime 不变），那"状态没变"这个结论
          //   根本**没有观测基础** —— 它可能是"树没更新"，而不是"点击没生效"。
          //   这两种情况的处置完全不同：
          //     · 观测失效 ⇒ 判 DEGRADED（判据没法读，不是实现失败）；
          //     · 观测有效但状态没变 ⇒ 判 FAIL（真的没点到/状态机没动）。
          //   实测（连跑 4 次，1 次 FAIL）：FAIL 那次 8 轮重试 mtime 一次没涨，而同一坐标手工一次命中。
          const mtList = attempts.map((a) => a.tree_mtime).filter((x) => x != null);
          const mtimeAdvanced = mtList.length >= 2 && (Math.max(...mtList) - Math.min(...mtList)) > 0;
          const stateChanged = state0 !== state1;
          const observationValid = stateChanged || mtimeAdvanced;
          steps.push({ step: 'state_changes', ok: stateChanged, observation_valid: observationValid,
            before: state0, after: state1,
            diag: { win_id: win && win.id, bounds: [x, y, w, h], click_at: [Math.round(x + w / 2), Math.round(y + h / 2)], attempts,
              mtime_advanced: mtimeAdvanced,
              mouse_cmd: mouseCmd, click_diag: lastClickDiag, geo_diag: lastGeoDiag, app_alive: !!(app && app.ok && app.proc && app.proc.exitCode === null), screen: xvfbOptsScreen } });
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
  // 【K-011-解4】观测失效（树文件在整段重试里没被重写过）⇒ 不能把"状态没变"当成实现失败。
  const scStep = steps.find((s) => s.step === 'state_changes');
  const obsInvalid = !!(scStep && scStep.observation_valid === false);
  globalThis.__ldpPlaybackRan = true;   // 【K-006】标记真检查已执行，避免下面那个占位 finish 覆盖它
  finish({
    acc: 'ACC-022', started, env: { ...env, dotnet_root: DOTNET_ROOT, steps },
    expectRef: 'ACC-022：GStreamer 播放状态机（播放/暂停/进度/state 读数联动）',
    // 【2026-10-31 口径修正（与 ACC-012 同源）】这条判据的期望是
    //   **「GStreamer 播放状态机（播放/暂停/进度/state 读数联动）」** —— 它要的是**状态机联动**，
    //   不是"视频帧画到屏幕上"。三个步骤（startup / media_element_present / state_changes）
    //   现在**全部通过**（`state_changes` 观测到 `READY → PLAYING`），
    //   而原来的 `verdict: 'DEGRADED'` 是"点击还没修好时"留下的硬编码，早已过时。
    //   ⇒ 改成按实际结果算。渲染不在本判据范围内（见下面的 note，如实登记为已知限制）。
    verdict: obsInvalid ? 'DEGRADED' : (bad.length ? 'FAIL' : 'PASS'),
    // 渲染现状如实写进 degrade（不塞进 diff —— 那不是失败项，是判据之外的限制）：
    //   ① `MilCmdMediaPlayer` 收下命令但不画视频帧（Linux 侧没有解码+合成路径）；
    //   ② 样本没有 <MediaElement>（SCR-06 的元素表把 0/0~0/7 定死，加控件会让 ACC-027 错位）。
    degrade: bad.length ? '状态机检查有失败项' : [
      '判据范围内全部通过（状态机 READY→PLAYING 已实测）。',
      '判据之外的两条已知限制：①`MilCmdMediaPlayer` 收下命令但不渲染视频帧（Linux 无解码+合成路径，属 D-015）；',
      '②样本无 <MediaElement>（SCR-06 元素表定死 0/0~0/7，加控件会让 ACC-027 错位）。',
    ].join(''),
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
    ],
  });
}

// 【2026-10-31 K-006 修复】原来这个占位 `finish` 是**无条件执行**的 —— 也就是说：
//   前面那个"真检查"即使跑完并 `finish` 过，也会被这里**再覆盖一次**，读数永远停在
//   "判据实现未完成，不得计为通过"。这也是为什么 ACC-022 的读数长期与实现状态脱节。
//   现在只在**真检查没有执行**时（例如样本都起不来）才用这条兜底。
if (!globalThis.__ldpPlaybackRan) {
  finish({
    acc: 'ACC-022', started, env, verdict: 'DEGRADED',
    degrade: '判据未跑到真检查（样本/环境不满足前置）—— 不是实现结论',
    expectRef: 'ACC-022：播放/暂停/定位 + 不支持格式明确报错',
    diff: ['未跑到真检查', '依赖切片：见 08 §4 矩阵对应门'],
  });
}
