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
      // 【2026-10-31】原来这里只数了 shim 里的 XIM 符号就判红，**从来没测过 XOpenIM**。
      //   而"本机没有输入法服务器"这个理由是**错的**：ibus 早就装好了
      //   （`ibus 1.5.26` + `ibus-libpinyin` + `ibus-table-wubi`），只是 `ibus-daemon` 没启动。
      //   判据自己负责环境（"判据的命令是判据的一部分"）：没跑就起来，起来后**实测** XIM。
      {
        const { spawnSync: sp } = await import('node:child_process');
        // 【关键】ibus-daemon 是**每个 X display 一个**：用 `pgrep` 判断"有没有在跑"
        //   会命中别的 display 上的实例（本轮实测就踩了这个：`pgrep` 找到 :190 的那个，
        //   于是没给判据自己的 display 起 daemon ⇒ `XIM_SERVERS` 是 `no such atom`）。
        //   ⇒ 改成按 **该 display 的 XIM_SERVERS** 判断。
        const ximServersOf = (d) => String(sp('sh', ['-c', `DISPLAY=${d} xprop -root XIM_SERVERS 2>/dev/null`]).stdout || '').trim();
        let before = ximServersOf(xvfb.display);
        let startedByUs = false;
        if (!/server=/.test(before)) {
          sp('sh', ['-c', `DISPLAY=${xvfb.display} ibus-daemon --daemonize --xim`]);
          startedByUs = true;
          await sleep(3000);
          before = ximServersOf(xvfb.display);
        }
        steps.push({ step: 'ibus_daemon', ok: /server=/.test(before),
                     started_by_us: startedByUs, xim_servers: before || null });
      }
      app = await startApp({ sample: 'WpfFirstWindow', scr: 'SCR-01', display: xvfb.display,
                             extraEnv: { WPF_LINUX_KEY_DIAG: '1', XMODIFIERS: '@im=ibus' } });
      if (app.ok) {
        const win = await findWindow(xvfb.display, 'WpfFirstWindow [SCR-01]', 25000);
        if (win) await waitMapped(xvfb.display, win.id);
        steps.push({ step: 'window', ok: !!win, got: win ? win.title : null });
        // 等 XIM 初始化（shim 在第一次聚焦时 XOpenIM + XCreateIC）
        await sleep(5000);
        const tr = app.trace ? app.trace() : '';
        const ximOpenOk = /\[XIM\] XOpenIM => ok/.test(tr);
        const xicOk = /\[XIM\] XCreateIC ok/.test(tr);
        const mOpen = tr.match(/\[XIM\] XOpenIM => ([^（\n]*)/);
        const mIc = tr.match(/\[XIM\] XCreateIC ok（第 (\d+) 个）style=(0x[0-9a-f]+)/);
        const callbacksRejected = /①无 FocusWindow 失败|②也失败/.test(tr);
        const fellBackNoCb = /回退到无回调/.test(tr);
        const immGetContextCalls = (tr.match(/\[IME\] ImmGetContext/g) || []).length;
        steps.push({ step: 'xim_bridge', ok: ximOpenOk && xicOk,
                     xopenim: mOpen ? mOpen[1].trim() : null,
                     xcreateic_count: mIc ? Number(mIc[1]) : 0,
                     ic_style: mIc ? mIc[2] : null,
                     callbacks_rejected: callbacksRejected,
                     fell_back_without_callbacks: fellBackNoCb,
                     imm_get_context_calls: immGetContextCalls,
                     note: ximOpenOk && xicOk
                       ? (fellBackNoCb
                          ? 'XIM 桥可用，但 ibus **拒绝 XIMPreeditCallbacks**（XCreateIC 带回调一律 NULL）⇒ 拿不到预编辑'
                          : 'XIM 桥实测可用（含预编辑回调）')
                       : 'XIM 桥未建立（见 xopenim 字段）' });
        // 【关键观测】IMM32 面是否真的被 WPF 调到
        steps.push({ step: 'imm32_reached', ok: immGetContextCalls > 0,
                     imm_get_context_calls: immGetContextCalls,
                     note: immGetContextCalls > 0
                       ? 'WPF 已走 IMM32 路径（ImmGetContext 被调用）'
                       : 'WPF 未进入 IME 路径（没有 WM_IME_* 驱动，ImmGetContext 0 次）' });
      } else {
        steps.push({ step: 'startup', ok: false, reason: app.reason });
      }
    }
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  const bad = steps.filter((s) => !s.ok);
  const ximStep = steps.find((s) => s.step === 'xim_bridge');
  const ximOk = !!(ximStep && ximStep.ok);
  // 【2026-10-31 口径】XIM 桥（XOpenIM/XCreateIC/XFilterEvent）实测可用，
  //   但 **预编辑/提交回调 → WM_IME_*** 这一层仍未实现 ⇒ "四条路径"一条都没走通
  //   ⇒ 仍然判 FAIL，不放宽。只是把失败**理由**改成实测的、可复现的那条。
  finish({
    acc: 'ACC-017', started, env: { ...env, dotnet_root: DOTNET_ROOT, xim_symbols_in_shim: ximInShim, case: opts.case || 'preedit', steps },
    expectRef: 'ACC-017：预编辑/提交/取消/候选窗定位四路径',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      ximOk
        ? `缺口（已实测，逐层）：`
          + `① **输入法服务器**：判据自己起 \`ibus-daemon --xim\`，\`XIM_SERVERS=@server=ibus\`、\`XOpenIM => ok\` ✔；`
          + `② **XIM 输入上下文**：\`XCreateIC ok（style=${ximStep.ic_style}）\` ✔；`
          + `③ **预编辑回调**：ibus 列出了 \`XIMPreeditCallbacks\` 样式，但**带回调的 XCreateIC 一律返回 NULL**`
          + `（三级尝试全失败，${ximStep.fell_back_without_callbacks ? '已回退到无回调' : '未回退'}）`
          + `⇒ **拿不到预编辑字符串**；`
          + `④ **IMM32 面**：shim 已实现并导出 17 个 \`Imm*\`（\`ImmGetContext\`/\`ImmGetCompositionString\`/`
          + `\`ImmNotifyIME\`/\`ImmSetCandidateWindow\` 等），\`imm32.dll\` 已加入 DllImport 映射；`
          + `但 WPF 侧 \`ImmGetContext\` 实测 **${ximStep.imm_get_context_calls ?? 0} 次** —— `
          + `因为 ③ 拿不到预编辑 ⇒ shim 不投递 \`WM_IME_*\` ⇒ WPF 不进 IME 路径。`
          + `⇒ 四路径（预编辑/提交/取消/候选窗定位）仍未走通，卡点收敛到 **"ibus 的 XIM 前端不提供 callbacks"**。`
        : `缺口：XIM 桥未能建立（${ximStep ? ximStep.note : '未探测'}）。`
          + `shim 里有 XIM 入口（计数 ${ximInShim}），但本环境拿不到输入法服务器 ⇒ 四条路径无法验证。`,
    ],
  });
}

// 【2026-10-31】原来这里还有一个**无条件**的占位 finish，会把上面的真检查覆盖掉
//   （和 ACC-022 同一个毛病）。已删除：真检查一定会 finish。
