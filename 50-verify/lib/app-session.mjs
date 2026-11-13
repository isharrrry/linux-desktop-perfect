#!/usr/bin/env node
// 判据侧公共库：起 Xvfb + 起样本应用 + 抓帧（IF-071 的「结构判据之外的帧判据」入口）
//
// 依赖：Xvfb / xdotool / xwd / ImageMagick convert（环境探头的必需项）
// 安装根：LDP_DOTNET_ROOT（默认 ~/.local/share/ldp/dotnet），由
//   40-build/pack/install-runtime.mjs + 40-build/milcore-bridge/build-bridge.mjs --install 生成。
//
// 设计约束（08 §1.5）：判据脚本必须能区分「被测对象没跑起来」与「跑起来但读数不合格」。
// 本库把前者统一成 `{ ok:false, reason, trace }`，由调用方判 FAIL 或 SKIP，不许静默通过。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { ROOT } from './run.mjs';

export const DOTNET_ROOT = process.env.LDP_DOTNET_ROOT || path.join(os.homedir(), '.local/share/ldp/dotnet');
export const FRAMEWORK_DIR = path.join(DOTNET_ROOT, 'shared/Microsoft.WindowsDesktop.App/10.0.11');
export const SHIM = path.join(FRAMEWORK_DIR, 'libwpfwin32.so');
export const WPFGFX = path.join(FRAMEWORK_DIR, 'wpfgfx_cor3.so');

export function which(bin) {
  return spawnSync('sh', ['-c', `command -v ${bin}`], { encoding: 'utf8' }).status === 0;
}

export function sampleDir(name) {
  return path.join(ROOT, '50-verify', 'samples', name, 'bin/Release/net10.0');
}

export function sampleDll(name) {
  return path.join(sampleDir(name), `${name}.dll`);
}

function waitFor(fn, timeoutMs, stepMs = 200) {
  const t0 = Date.now();
  return new Promise((resolve) => {
    const tick = () => {
      let v = null;
      try { v = fn(); } catch { v = null; }
      if (v) return resolve(v);
      if (Date.now() - t0 > timeoutMs) return resolve(null);
      setTimeout(tick, stepMs);
    };
    tick();
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 起一个无头 X 会话（08 §3.1 的 Xvfb 基线）
// 挑一个**当前空闲**的 display：已存在的 Xvfb 会占住编号，贸然复用会连到旧会话
// （实测症状：`-dpi 120` 起的新会话没起来，应用连到旧 Xvfb，DPI 读数停在默认 100）。
function pickFreeDisplay() {
  for (let i = 0; i < 40; i += 1) {
    const d = `:${100 + Math.floor(Math.random() * 90)}`;
    const r = spawnSync('sh', ['-c', `DISPLAY=${d} xdpyinfo >/dev/null 2>&1`]);
    if (r.status !== 0) return d;
  }
  return `:${100 + Math.floor(Math.random() * 90)}`;
}

export function startXvfb(display = pickFreeDisplay(), opts = {}) {
  const args = [display, '-screen', '0', opts.screen || '1280x1024x24'];
  // Xvfb 的 -dpi 决定 X server 报告的 screen DPI —— shim 的 GetDpiForWindow 就是读它（ACC-011 的三档来源）
  if (opts.dpi) args.push('-dpi', String(opts.dpi));
  const proc = spawn('Xvfb', args, { stdio: 'ignore' });
  // 【环境缺口修复 / K-208-K-209】**Xvfb 只是 X server，它不提供窗口管理器。**
  //   而 `findWindow()` 靠 `wmctrl -l` 找窗口，`wmctrl` 需要 EWMH 的 `_NET_CLIENT_LIST`，
  //   **那个属性只能由窗口管理器提供**。没有 WM 时 `wmctrl -l` 返回空 ⇒ `findWindow` 永远超时
  //   ⇒ ACC-022（媒体）/ ACC-023（Ink）永远 FAIL —— 而应用其实完全正常
  //   （`xwininfo -root -children` 能看到 `WpfMediaInk [SCR-06]` 784×560）。
  //   ⇒ 这里在 Xvfb 起来后**再起一个轻量 EWMH WM**（openbox）。它只做 EWMH 记账，不接管绘制。
  let wm = null;
  if (opts.wm !== false) {
    wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: display } });
  }
  return {
    display, proc, wm, dpi: opts.dpi || 96,
    kill: () => {
      try { if (wm) wm.kill('SIGTERM'); } catch {}
      try { proc.kill('SIGTERM'); } catch {}
    },
  };
}

export async function waitDisplay(display, timeoutMs = 15000) {
  return waitFor(() => spawnSync('sh', ['-c', `DISPLAY=${display} xdpyinfo >/dev/null 2>&1`]).status === 0, timeoutMs);
}

// 起样本应用（带 install-runtime 生成的启动环境：D-005 的 R2R 关闭 + shim 路径）
export async function startApp({ sample = 'WpfFirstWindow', scr = 'SCR-01', display, treeDump = null, extraEnv = {}, extraArgs = [] } = {}) {
  const dll = sampleDll(sample);
  if (!fs.existsSync(dll)) return { ok: false, reason: `样本未构建：${path.relative(ROOT, dll)}`, hint: 'node 50-verify/build/build-sample.mjs' };
  if (!fs.existsSync(path.join(DOTNET_ROOT, 'dotnet'))) return { ok: false, reason: `运行时未安装：${path.relative(ROOT, DOTNET_ROOT)}`, hint: 'node 40-build/pack/install-runtime.mjs' };
  if (!fs.existsSync(WPFGFX)) return { ok: false, reason: '缺少 wpfgfx_cor3.so（MIL 桥未装）', hint: 'node 40-build/milcore-bridge/build-bridge.mjs --install ' + DOTNET_ROOT };

  const logFile = path.join(os.tmpdir(), `ldp-app-${sample}-${scr}-${Date.now()}.log`);
  const out = fs.openSync(logFile, 'w');
  const proc = spawn(path.join(DOTNET_ROOT, 'dotnet'), [dll, '--scr', scr, ...extraArgs], {
    cwd: sampleDir(sample),
    stdio: ['ignore', out, out],
    env: {
      ...process.env,
      DOTNET_ROOT,
      DOTNET_ReadyToRun: '0',
      DISPLAY: display,
      WPF_LINUX_WIN32_SHIM: SHIM,
      LD_LIBRARY_PATH: `${path.join(FRAMEWORK_DIR, 'native')}:${process.env.LD_LIBRARY_PATH || ''}`,
      ...(treeDump
        ? {
            DOTNET_STARTUP_HOOKS: path.join(FRAMEWORK_DIR, 'LdpA11y.dll'),
            LDP_A11Y_DUMP: treeDump,
            LDP_A11Y_SCREEN: scr,
          }
        : {}),
      ...extraEnv,
    },
  });
  const trace = () => {
    try { return fs.readFileSync(logFile, 'utf8'); } catch { return ''; }
  };
  return {
    ok: true,
    proc,
    logFile,
    trace,
    kill: () => { try { proc.kill('SIGTERM'); } catch {} },
  };
}

// 找窗口（按标题前缀），返回 { id, title, w, h }
// 【2026-10-31 K-006 修复】`findWindow` 原来只从 `wmctrl -l` / `xwininfo -root -tree` 的文本里
//   解析出窗口 id 就直接返回，**不验证这个 id 是否真的可用**。实测过：判据拿到
//   `0x200005`，但 `xdotool getwindowgeometry 0x200005` 报
//   `BadWindow (invalid Window parameter)` —— 那是个**已经不存在/不属于本 display 的窗口**，
//   于是后续 `mousemove X Y click 1` 点在空处，"命令成功但按钮没反应"（K-006 真正的原因）。
//   这里加一道有效性校验：只有 `xdotool getwindowgeometry` 成功返回的 id 才算找到。
function windowUsable(display, id) {
  if (!id) return false;
  // 【2026-10-31 回归修复】原来 timeout: 5000 —— `findWindow` 的 `waitFor` 会**反复**调这个，
  //   一旦 xdotool 每次都失败就是 5 s × N，把 25 s 的窗口耗尽 ⇒ `findWindow` 直接返回 null
  //   ⇒ 判据跳过 XDND 步（读数变成 `xdnd_declared: None`）。改成 800 ms：
  //   xdotool 是本地调用，正常应 <50 ms；800 ms 只用于兜住异常。
  const r = spawnSync('sh', ['-c', `DISPLAY=${display} xdotool getwindowgeometry --shell ${id} 2>/dev/null`], { encoding: 'utf8', timeout: 800 });
  return !!(r && r.status === 0 && r.stdout && /^X=-?\d+/m.test(r.stdout));
}

export async function findWindow(display, titlePrefix, timeoutMs = 25000) {
  return waitFor(() => {
    const r = spawnSync('sh', ['-c', `DISPLAY=${display} wmctrl -l 2>/dev/null`], { encoding: 'utf8' });
    if (r.status === 0 && r.stdout) {
      for (const line of r.stdout.split('\n')) {
        if (!line.includes(titlePrefix)) continue;
        const parts = line.trim().split(/\s+/);
        // 【2026-10-31 K-006】**必须严格**：无效 id 会让调用方点在空处（ACC-022 的 state_changes 就靠这个）。
        //   ⚠️ 曾试过"宽松 + usable 标记"，结果 ACC-022 立刻退回 READY→READY ⇒ 已恢复严格。
        if (windowUsable(display, parts[0]))
          return { id: parts[0], title: parts.slice(3).join(' '), w: 0, h: 0, usable: true };
      }
    }
    // 没有 wmctrl：用 xwininfo 的树（含未映射窗口）
    const x = spawnSync('sh', ['-c', `DISPLAY=${display} xwininfo -root -tree 2>/dev/null`], { encoding: 'utf8' });
    if (x.status === 0 && x.stdout) {
      for (const line of x.stdout.split('\n')) {
        if (!line.includes(titlePrefix)) continue;
        const m = line.match(/(0x[0-9a-f]+)\s+"([^"]*)".*?(\d+)x(\d+)\+/);
        if (m && windowUsable(display, m[1]))
          return { id: m[1], title: m[2], w: Number(m[3]), h: Number(m[4]), usable: true };
      }
    }
    return null;
  }, timeoutMs);
}

// 等窗口真正映射（Map State: IsViewable）。
// 实测：WPF 从 CreateWindowEx 到 XMapWindow 之间要做字体/主题初始化，Xvfb 上约 5~8 s；
// 只等「窗口存在」就抓帧会撞上 IsUnMapped（xwd 报 X Error，抓到 0 字节）。
export async function waitMapped(display, windowId, timeoutMs = 40000) {
  return waitFor(() => {
    const r = spawnSync('sh', ['-c', `DISPLAY=${display} xwininfo -id ${windowId} 2>/dev/null | grep -m1 "Map State"`], { encoding: 'utf8' });
    return /IsViewable/.test(r.stdout || '') ? true : null;
  }, timeoutMs, 250);
}

// 抓一帧：xwd → PNG，返回 { png, width, height, colors, nonWhiteRatio }
export async function captureFrame(display, windowId) {
  const xwd = path.join(os.tmpdir(), `ldp-frame-${Date.now()}.xwd`);
  const png = xwd.replace(/\.xwd$/, '.png');
  // 窗口未 viewable 时 xwd 会一直等 server 响应 → 必须给超时，否则判据会挂住
  const r = spawnSync('sh', ['-c', `DISPLAY=${display} xwd -id ${windowId} -silent > ${xwd} && convert ${xwd} ${png} 2>/dev/null`], {
    encoding: 'utf8',
    timeout: 15000,
  });
  if (r.status !== 0 || !fs.existsSync(png) || fs.statSync(png).size === 0) {
    // 退路：ImageMagick 直接抓窗口（对未 viewable 的窗口会明确失败而不是挂住）
    const r2 = spawnSync('sh', ['-c', `DISPLAY=${display} import -window ${windowId} ${png} 2>/dev/null`], { encoding: 'utf8', timeout: 15000 });
    if (r2.status !== 0 || !fs.existsSync(png) || fs.statSync(png).size === 0) return null;
  }
  const stats = spawnSync('python3', ['-c', `
import json,sys
from PIL import Image
im = Image.open(sys.argv[1]).convert('RGB')
px = list(im.getdata())
colors = len(set(px))
nonwhite = sum(1 for p in px if p != (255,255,255))
print(json.dumps({"width": im.size[0], "height": im.size[1], "colors": colors, "non_white_ratio": nonwhite/len(px)}))
`, png], { encoding: 'utf8' });
  if (stats.status !== 0) return null;
  return { png, ...JSON.parse(stats.stdout) };
}

export { sleep };
