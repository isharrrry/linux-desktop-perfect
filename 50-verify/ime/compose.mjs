#!/usr/bin/env node
// ACC-017：IME 四条路径
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/native-shim/bin/libwpfwin32.so", why: "IME 桥未实现（S2）", exists: exists("40-build/native-shim/bin/libwpfwin32.so") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ case: opts.case || "preedit", ibus: runCmd("ibus",["version"]).out.trim().split("\\n").pop() },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

const missingNotes = missing.map((m) => `缺被测对象：${m.path}（${m.why}）`);

// 真跑：起样本（SCR-01 有 `TextBox`），把「有没有 IME 桥」这件事落成读数：
//   · shim 里没有任何 XIM/ibus 入口（`grep -c "XOpenIM\|XCreateIC\|ibus"` = 0）；
//   · WPF 侧的 `Ime`/`TextComposition` 路径要求 `WM_IME_*` 消息，而 shim 从不产生；
//   ⇒ 预编辑/提交/取消/候选窗四条路径都没有承载面。这里如实判红并写明缺口。
const { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT } = await import('../lib/app-session.mjs');
{
  const { spawnSync } = await import('node:child_process');
  // 用 node 自己数（shell 里的引号/awk 转义很容易数成 0 —— 实测踩过）
  let ximInShim = 0;
  for (const f of fs.readdirSync('40-build/native-shim/src')) {
    if (!f.endsWith('.c')) continue;
    const txt = fs.readFileSync(path.join('40-build/native-shim/src', f), 'utf8');
    ximInShim += (txt.match(/XOpenIM|XCreateIC|XFilterEvent|XIM_/g) || []).length;
  }
  const xvfb = startXvfb();
  let app = null;
  const steps = [];
  try {
    if (await waitDisplay(xvfb.display)) {
      app = await startApp({ sample: 'WpfFirstWindow', scr: 'SCR-01', display: xvfb.display });
      if (app.ok) {
        const win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]', 25000);
        if (win) await waitMapped(xvfb.display, win.id);
        steps.push({ step: 'window', ok: !!win, got: win ? win.title : null });
      } else {
        steps.push({ step: 'startup', ok: false, reason: app.reason });
      }
    }
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  const bad = steps.filter((s) => !s.ok);
  finish({
    acc: 'ACC-017', started, env: { ...env, dotnet_root: DOTNET_ROOT, xim_symbols_in_shim: ximInShim, case: opts.case || 'preedit', steps },
    expectRef: 'ACC-017：预编辑/提交/取消/候选窗定位四路径',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      ximInShim > 0
        ? `缺口：shim 已接 XIM（XOpenIM/XFilterEvent/焦点窗口的 XCreateIC+XSetICFocus 共 ${ximInShim} 处），但 ①本机**没有输入法服务器**（XOpenIM 返回 NULL，IC 建不起来）②**预编辑/提交回调 → WM_IME_*** 这一层还没做 ⇒ 四条路径仍无法验证（D-014 第二/三步）`
        : `缺口：shim 无 XIM 入口（计数 = ${ximInShim}），也不产生 WM_IME_* ⇒ 四条路径都没有承载面`,
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-017', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-017：预编辑/提交/取消/候选窗定位四路径',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
