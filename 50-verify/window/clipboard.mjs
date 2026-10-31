#!/usr/bin/env node
// ACC-012：剪贴板与拖放（TG-004，MOD-63）
//
// 判据口径（真跑，探针直接调 shim 的 Win32 面 —— 不经 WPF 的 `Clipboard` 类：
//   上游 `System.Windows.Clipboard` 走 OLE COM，Linux 无 COM，那是另一条登记过的缺口）：
//   ①同进程往返：`clip-probe --set abc --get`（SetClipboardData → GetClipboardData，本地缓冲路径）；
//   ②跨进程往返：A 进程 `--set hello --hold` 持有 CLIPBOARD selection，B 进程 `--get` 经
//     `XConvertSelection` 取回 —— 这是 X11 上"剪贴板"的真实语义（selection 拥有权）；
//   ③XDND 拖入：本仓尚未实现（`XClientMessage` 的 XDND 协议），如实计入 diff。
//
// 用法：node 50-verify/window/clipboard.mjs

import { spawnSync } from 'node:child_process';
import { parseArgs, finish, exists, which } from '../lib/run.mjs';
import fs from 'node:fs';
import { startXvfb, waitDisplay, sleep, startApp, findWindow, waitMapped } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBE = '40-build/native-shim/bin/clip-probe';
const missingEnv = ['Xvfb'].filter((b) => !which(b));
if (missingEnv.length) {
  finish({ acc: 'ACC-012', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-012：文本/图像双向剪贴板 + XDND 拖入', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
if (!exists(PROBE)) {
  finish({ acc: 'ACC-012', started, verdict: 'FAIL', expectRef: 'ACC-012：文本/图像双向剪贴板 + XDND 拖入',
    env: { probe: PROBE }, diff: [`缺被测对象/探针：${PROBE}`, '先跑 make -C 40-build/native-shim probes'] });
}

const run = (display, args, timeout = 20000) => {
  const r = spawnSync(PROBE, args, { encoding: 'utf8', timeout, env: { ...process.env, DISPLAY: display } });
  let json = null;
  const line = (r.stdout || '').trim().split('\n').pop();
  if (line) { try { json = JSON.parse(line); } catch { json = null; } }
  return { rc: r.status, json, raw: line, stderr: (r.stderr || '').slice(0, 200) };
};

const xvfb = startXvfb();
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);

  // ① 同进程往返（ASCII；多字节在 GetClipboardData 的 UTF-16 反解上另算，见读数）
  const self = run(xvfb.display, ['--set', 'abc', '--get']);
  steps.push({ step: 'in_process_roundtrip', ok: !!(self.json && self.json.roundtrip_ok === true), ...self });

  // ② 跨进程：A 持有 selection，B 读
  const a = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} ${PROBE} --set cross-proc-value --hold 4000 > /tmp/ldp-clip-A.out 2>/tmp/ldp-clip-A.err &  echo $!`], { encoding: 'utf8' });
  const pid = Number((a.stdout || '').trim()) || 0;
  await sleep(1200);
  const b = run(xvfb.display, ['--get']);
  if (pid) { try { process.kill(pid, 'SIGTERM'); } catch {} }
  const crossOk = !!(b.json && typeof b.json.value === 'string' && b.json.value === 'cross-proc-value');
  steps.push({ step: 'cross_process', ok: crossOk, want: 'cross-proc-value', got: b.json ? b.json.value : null, rc: b.rc });

  // ③ XDND：**真的去读窗口的 XdndAware 属性**（D-011 第一步已实现"声明"，
  //   但"处理 XdndEnter/Position/Drop"那一层还没做 ⇒ 本步只验声明面）。
  {
    let xdndOk = false, xdndValue = null, win = null, xdndMsg = null, xdndRaw = null, xdndStderr = null;
    const app = await startApp({ sample: 'WpfFirstWindow', scr: 'SCR-01', display: xvfb.display });
    if (app.ok) {
      win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]', 25000);
      // 等窗口真的映射出来再发 XDND —— 修前没等，探针可能在窗口还没上屏时发消息
      //   （X 会把发给未映射窗口的 ClientMessage 丢掉），于是判据里 status_received=false。
      if (win) await waitMapped(xvfb.display, win.id);
      if (win) {
        // 直接用 xprop 读属性（不为此在 clip-probe 里加模式：那是另一个协议面）
        const rp = spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xprop -id ${win.id} XdndAware 2>/dev/null`],
                             { encoding: 'utf8', timeout: 15000 });
        const txt = (rp.stdout || '').trim();
        const m = txt.match(/=\s*(\S+)/);
        if (m && !/no such atom|not found/i.test(txt)) { xdndOk = true; xdndValue = m[1]; }
        // 再验**消息层**：扮演拖放源发 XdndEnter/Position，看 shim 是否回 XdndStatus(accept)
        const probe = '40-build/native-shim/bin/xdnd-probe';
        if (fs.existsSync(probe)) {
          const r2 = spawnSync(probe, [win.id], { encoding: 'utf8', timeout: 20000,
                                                 env: { ...process.env, DISPLAY: xvfb.display } });
          const line2 = (r2.stdout || '').trim().split(String.fromCharCode(10)).pop();
          try { xdndMsg = JSON.parse(line2); } catch { xdndMsg = null; }
          xdndRaw = (r2.stdout || '').trim().slice(-200);   // 诊断：把探针原始输出也带进读数
          // 【2026-10-31 D-011 第 18 步】探针的 stderr 里才有 shim 的诊断（[XDND-*]、[DISPATCH] 等）。
          //   原来只读 stdout ⇒ 那些诊断全被丢掉 ⇒ 排查时"看不到"不等于"没发生"。
          xdndStderr = (r2.stderr || '').trim().slice(-800);
        }
      }
      app.kill();
    }
    // ⚠️ **口径**：ACC-012 要的是"XDND 拖入"，不是"声明可接收"。
    //   声明面通过（有 XdndAware）只证明窗口愿意接，**不证明拖入的事件能到应用**。
    //   所以 ok 只能是 delivered（本轮恒 false），declared 单独记 —— 不接受"声明通过 = PASS"。
    const msgOk = !!(xdndMsg && xdndMsg.status_received && xdndMsg.accepted);
    const dropOk = !!(xdndMsg && xdndMsg.selection_served > 0);   // Drop 层：uri-list 被转走并构造了 HDROP
    steps.push({
      step: 'xdnd', ok: false, pending: true,
      declared: xdndOk, declared_value: xdndValue,
      status_received: !!(xdndMsg && xdndMsg.status_received),
      drop_translated: dropOk,
      selection_served: xdndMsg ? xdndMsg.selection_served : null,
      raw: xdndRaw,
      note: (xdndOk ? `窗口声明 XdndAware(${xdndValue})；` : '缺 XdndAware；')
        + (msgOk ? 'XdndEnter/Position 已回 XdndStatus(accept)；' : '消息层未通；')
        + (dropOk ? 'XdndDrop → XConvertSelection → HDROP → WM_DROPFILES 已走通（探针 serving 了 uri-list）；' : 'Drop 层未通；')
        + '但"应用侧的 Drop 事件是否真的被触发"未验（需要 AllowDrop=true 的样本），所以本步仍 pending',
    });
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok && !s.pending);
const inProc = steps.find((s) => s.step === 'in_process_roundtrip');
const cross = steps.find((s) => s.step === 'cross_process');
const coreOk = !!(inProc && inProc.ok);
// 文本剪贴板的两个往返（同进程 / 跨进程）都过 ⇒ 文本面成立；XDND 未实现 ⇒ 整体 DEGRADED（不是 PASS）
const xdnd = steps.find((s) => s.step === 'xdnd');
const verdict = !coreOk || !(cross && cross.ok) ? 'FAIL' : (xdnd && !xdnd.ok) ? 'DEGRADED' : 'PASS';
finish({
  acc: 'ACC-012',
  started,
  expectRef: 'ACC-012：文本剪贴板同进程/跨进程往返 + XDND 拖入',
  env: { display: xvfb.display, steps },
  verdict,
  degrade: verdict === 'DEGRADED'
    ? 'XDND 拖入未实现（文本剪贴板双向往返已通过：同进程 + 跨进程）'
    : 'none',
  diff: verdict === 'PASS' ? [] : failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
});
