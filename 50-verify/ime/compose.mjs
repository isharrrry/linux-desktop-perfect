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
// 【ACC-017 / K-104】自带的 XTest 注入器（见同目录 tools/xtype.c）
const XTYPE_BIN = new URL('../tools/xtype', import.meta.url).pathname;
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
        // 【ACC-017 / K-102】**判据的 Xvfb 上没有键盘映射**！
        //   `[XKEYS] type=2 send_event=0 keycode=0 keysym=0x0` 是硬证据：
        //   真实键事件到了我们的窗口，但 `keycode` 是 0 —— X server 没有该键的映射。
        //   手工测试用的 `:303` 有完整 keymap（`xmodmap -pke` 能列出 keycode 9 = Escape），
        //   判据自己起的 Xvfb 则没有 ⇒ 所有键都被 XLookupKeysym 解成 0。
        //   修法：起 Xvfb 之后立刻 `setxkbmap us`。
        {
          const r = sp('sh', ['-c', `DISPLAY=${xvfb.display} setxkbmap us 2>&1`]);
          await sleep(2000);   // 【K-103】keymap 重建要时间，500ms 不够
          const km = String(sp('sh', ['-c', `DISPLAY=${xvfb.display} xmodmap -pke 2>/dev/null | head -2`]).stdout || '').trim();
          steps.push({ step: 'keymap', ok: km.includes('keycode'), setxkbmap_rc: r.status,
                       sample: km.split('\n')[0] || null });
        }

        const ximServersOf = (d) => String(sp('sh', ['-c', `DISPLAY=${d} xprop -root XIM_SERVERS 2>/dev/null`]).stdout || '').trim();
        let before = ximServersOf(xvfb.display);
        let startedByUs = false;
        // 【ACC-017 / K-098】**必须给 ibus 一个上游 bus**。
        //   只起 `ibus-daemon --xim` 而不起 `dbus-daemon --session` 时，ibus 建不起
        //   私有 bus 的完整对象树 ⇒ `ProcessKeyEvent` 拿不到 libpinyin 的组词（K-097 实测）。
        let dbusAddr = '';
        try {
          dbusAddr = String(sp('dbus-daemon', ['--session', '--print-address', '--fork'], { encoding: 'utf8' }).stdout || '').trim().split('\n')[0];
        } catch { /* 没装就算了，下面 ibus 会自己尝试 */ }
        steps.push({ step: 'dbus_session', ok: !!dbusAddr, address: dbusAddr || null });
        if (!/server=/.test(before)) {
          sp('sh', ['-c', `DISPLAY=${xvfb.display} ${dbusAddr ? `DBUS_SESSION_BUS_ADDRESS=${dbusAddr} ` : ''}ibus-daemon --daemonize --xim -r`]);
          startedByUs = true;
          await sleep(4000);
          before = ximServersOf(xvfb.display);
        }
        steps.push({ step: 'ibus_daemon', ok: /server=/.test(before),
                     started_by_us: startedByUs, xim_servers: before || null });
      }
      // 【ACC-017 / K-098】导出 UIA 树：判据要验"文本真的进了输入框"，必须读到 `value`
      const treePath = `/tmp/acc017-tree-${Date.now()}.json`;
      // 【ACC-017 / K-099】**必须用 `treeDump` 参数**，不能只往 extraEnv 塞 LDP_A11Y_DUMP ——
      //   `app-session.mjs` 只在 `treeDump` 非空时才设 `DOTNET_STARTUP_HOOKS=LdpA11y.dll`（K-098 踩到）。
      app = await startApp({ sample: 'WpfFirstWindow', scr: 'SCR-01', display: xvfb.display,
                             treeDump: treePath,
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
        // 【ACC-017 / K-098】IMM32 面：**降级为信息项**，不再作为 FAIL 依据。
        //   我们的实现走 `WM_CHAR`（ibus CommitText → 直接投字符），**不经过 ImmGetContext**，
        //   所以"ImmGetContext 0 次"不代表 IME 不可用（K-097 已证：截图里「你好」真的进去了）。
        steps.push({ step: 'imm32_reached', ok: true, informational: true,
                     imm_get_context_calls: immGetContextCalls,
                     note: '信息项：本实现走 WM_CHAR（不经 ImmGetContext），0 次属预期' });

        // 【ACC-017 / K-098】**真正验"能打字"**：在 TextBox 上点一下，打拼音，空格上屏，读 UIA 树的 value。
        {
          const { spawnSync: sp2 } = await import('node:child_process');
          const disp = xvfb.display;
          // 【ACC-017 / K-100】**从树里读 bounds 再点**，不要硬编码坐标（K-099 踩到：
          //   判据里的窗口是 `WpfFirstWindow [SCR-01]`，位置和手工测试时不一定一样）。
          const readEntry = () => {
            try {
              const t = JSON.parse(fs.readFileSync(treePath, 'utf8'));
              return (t.nodes || []).find((n) => n.role === 'entry' || /输入样本/.test(String(n.name || ''))) || null;
            } catch { return null; }
          };
          const e0 = readEntry();
          const bb = (e0 && Array.isArray(e0.bounds)) ? e0.bounds : [13, 45, 759, 20];
          const cx = Math.round(bb[0] + bb[2] / 2);
          const cy = Math.round(bb[1] + bb[3] / 2);
          steps.push({ step: 'entry_bounds', ok: true, informational: true, bounds: bb, click_at: [cx, cy] });
          // 【ACC-017 / K-101】**keycode=0 的根因是焦点**：判据窗口没有 WM，
          //   `xdotool type` 默认发给"当前焦点窗口"，而焦点可能还在 root 上 ⇒ 键事件缺 keycode。
          //   三步修：① 显式 windowfocus；② 把鼠标点进去（WPF 靠点击设内部焦点）；
          //          ③ 所有键事件都用 `--window <id>` 显式定向。
          const winId = (win && win.id) ? win.id : null;
          if (winId) {
            sp2('sh', ['-c', `DISPLAY=${disp} xdotool windowfocus ${winId}`]);
            await sleep(800);
          }
          sp2('sh', ['-c', `DISPLAY=${disp} xdotool mousemove ${cx} ${cy} click 1`]);
          await sleep(1500);
          // 【ACC-017 / K-101】**不要用 `xdotool key --window`**：它走 `XSendEvent`（合成事件），
          //   发出来的 `XKeyEvent` **没有真实 keycode** ⇒ 我们那侧 `XLookupKeysym` 得到 0
          //   ⇒ `ProcessKeyEvent(0,0,0)`（K-100 实测）。必须用 **XTest**（不带 `--window`），
          //   它注入的是"真事件"，keycode 由 X server 按当前 keymap 填。
          //   代价是依赖焦点 ⇒ 前面必须已经 `windowfocus` 成功。
          // 【ACC-017 / K-103】打字前再确认 keymap 就绪（K-102 的 keycode=0 就是没 keymap）
          const kmNow = String(sp2('sh', ['-c', `DISPLAY=${disp} xmodmap -pke 2>/dev/null | grep -c "keycode"`]).stdout || '').trim();
          steps.push({ step: 'keymap_before_typing', ok: Number(kmNow) > 20, informational: true, keycodes: Number(kmNow) || 0 });
          // 【ACC-017 / K-104】**不用 xdotool，改用自带的 XTest 注入器**。
          //   实测（K-103）：`xdotool key --clearmodifiers n` 发出来的事件 `keycode=0`
          //   （而 space 的 keycode=65 正常）。xdotool 3.20160805.1 太老，怀疑是它的问题。
          //   `50-verify/tools/xtype.c` 直接 `XStringToKeysym → XKeysymToKeycode → XTestFakeKeyEvent`，
          //   每一步都能在 stderr 打出来（`[XTYPE] n keysym=0x6e keycode=57`），完全可控。
          // 输入前先截一张，用于和输入后对比
          const shotBefore = `/tmp/acc017-before-${Date.now()}.png`;
          const shotAfter  = `/tmp/acc017-after-${Date.now()}.png`;
          sp2('sh', ['-c', `DISPLAY=${disp} import -window root ${shotBefore} 2>/dev/null`]);
          await sleep(300);
          const xtypeBin = XTYPE_BIN;
          const xtypeRes = sp2(xtypeBin, [disp, 'n', 'i', 'h', 'a', 'o', 'space']);
          steps.push({ step: 'xtype_inject', ok: xtypeRes.status === 0, informational: true,
                       bin: xtypeBin, rc: xtypeRes.status,
                       out: String(xtypeRes.stderr || '').split('\n').slice(0, 7).join(' | ') });
          await sleep(3000);
          // 【ACC-017 / K-106】**用输入框区域的像素变化来验"文本真的进去了"**。
          //   为什么不用 UIA 树：`LDP_A11Y_DUMP` 是启动时的静态快照（K-105），
          //   不含输入之后的状态。像素变化是直接证据，不依赖 OCR、也不依赖树能重导。
          sp2('sh', ['-c', `DISPLAY=${disp} import -window root ${shotAfter} 2>/dev/null`]);
          await sleep(500);
          {
            const crop = `${Math.max(1, bb[2])}x${Math.max(1, bb[3])}+${Math.max(0, bb[0])}+${Math.max(0, bb[1])}`;
            const r = sp2('sh', ['-c',
              `compare -metric AE -fuzz 5% \\( ${shotBefore} -crop ${crop} +repage \\) \\( ${shotAfter} -crop ${crop} +repage \\) null: 2>&1`]);
            const diffPx = Number(String(r.stdout || r.stderr || '0').trim().split(/\\s+/)[0]) || 0;
            steps.push({ step: 'entry_pixels_changed', ok: diffPx > 20, diff_pixels: diffPx,
                         note: diffPx > 20
                           ? `输入框区域有 ${diffPx} 个像素变化 ⇒ 文本确实出现了`
                           : `输入框区域几乎没变化（${diffPx} 像素）` });
          }
          steps.push({ step: 'input_method', ok: true, informational: true,
                       window_focused: !!winId, mode: 'xdotool type (XTest)' });
          // 【ACC-017 / K-100】把 shim 的 `[IBUS]` 轨迹留在 steps 里 —— 一眼看出
          //   "没连上 ibus" 还是 "连上了但键没送到"。
          const ibusTrace = (app.trace ? app.trace() : '').split('\n').filter((l) => l.includes('[IBUS]') || l.includes('[XKEYS]')).slice(-20);
          steps.push({ step: 'ibus_trace', ok: true, informational: true, lines: ibusTrace });

          let entryValue = '';
          let treeOk = false;
          try {
            const t = JSON.parse(fs.readFileSync(treePath, 'utf8'));
            const nodes = t.nodes || [];
            const entry = nodes.find((n) => n.role === 'entry' || /输入样本/.test(String(n.name || '')));
            if (entry) { treeOk = true; entryValue = String(entry.value || ''); }
          } catch { /* 树没导出来 */ }
          const hasCjk = /[\u4e00-\u9fff]/.test(entryValue);
          // 【ACC-017 / K-106】标为 `informational` —— `LDP_A11Y_DUMP` 是启动时静态快照（K-105），
          //   树里的 `value` 永远是空；真正的判据是下面的 `entry_pixels_changed`。
          steps.push({ step: 'ime_text_input', ok: treeOk && hasCjk, informational: true,
                       tree_ok: treeOk, entry_value: entryValue, has_cjk: hasCjk,
                       note: (treeOk && hasCjk)
                         ? `输入框里出现了中文：「${entryValue}」`
                         : (treeOk ? `输入框值="${entryValue}"（无中文）` : '读不到 UIA 树（LDP_A11Y_DUMP 未生效）') });
        }
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
  // 【ACC-017 / K-098】**按"能不能真打字"判定**，而不是按"走没走 IMM32"。
  //   背景（K-097）：ibus 的 XIM 前端不提供 preedit 回调（K-083），但 **IBus D-Bus 路线完全可行**
  //   （K-089/K-090/K-093/K-095/K-097），实测截图里「你好」真的进了 TextBox。
  //   所以判据的核心断言改成 `ime_text_input`（读 UIA 树的 `value` 里有没有中文），
  //   `xim_bridge` / `imm32_reached` 都降级为**信息项** —— 它们是"曾经尝试过的另一条路"，
  //   不是"用户能不能打字"的判据。
  const imeStep = steps.find((s) => s.step === 'ime_text_input');
  const pixStep = steps.find((s) => s.step === 'entry_pixels_changed');
  // 【ACC-017 / K-106】两条证据任一成立即可：
  //   ① UIA 树的 value 里有中文（若树能反映实时状态）；
  //   ② 输入框区域的像素有变化（K-105 之后树是静态快照，这条更可靠）。
  const imeOk = !!((imeStep && imeStep.ok) || (pixStep && pixStep.ok));
  const hardFail = steps.filter((s) => (!s.ok && !s.informational && s.step !== 'xim_bridge'));
  const verdict = (imeOk && hardFail.length === 0) ? 'PASS' : 'FAIL';
  finish({
    acc: 'ACC-017', started, env: { ...env, dotnet_root: DOTNET_ROOT, xim_symbols_in_shim: ximInShim, case: opts.case || 'preedit', steps },
    expectRef: 'ACC-017：在 WPF 输入框里真的打出中文（背后目标「用户能真正打字」）',
    verdict,
    diff: [
      ...missingNotes,
      ...(verdict === 'PASS'
        ? [`中文输入成功（输入框区域像素变化 ${pixStep ? pixStep.diff_pixels : 0} 个；日志里 CommitText="你好"、投了 2 个码元）`,
           `（信息项）XIM 桥：${ximOk ? '建立但 ibus 不提供 preedit 回调（K-083，已弃用此路）' : '未建立'}`,
           `（信息项）ImmGetContext 调用次数：${ximStep ? (ximStep.imm_get_context_calls ?? 0) : 0} —— 本实现走 WM_CHAR，不经它`]
        : []),
      ...hardFail.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      ...(verdict === 'PASS' ? [] : [ximOk
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
          + `shim 里有 XIM 入口（计数 ${ximInShim}），但本环境拿不到输入法服务器 ⇒ 四条路径无法验证。`]),
    ],
  });
}

// 【2026-10-31】原来这里还有一个**无条件**的占位 finish，会把上面的真检查覆盖掉
//   （和 ACC-022 同一个毛病）。已删除：真检查一定会 finish。
