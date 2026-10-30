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
  return path.join(ROOT, 'samples', name, 'bin/Release/net10.0');
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
  return { display, proc, dpi: opts.dpi || 96, kill: () => { try { proc.kill('SIGTERM'); } catch {} } };
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
export async function findWindow(display, titlePrefix, timeoutMs = 25000) {
  return waitFor(() => {
    const r = spawnSync('sh', ['-c', `DISPLAY=${display} wmctrl -l 2>/dev/null`], { encoding: 'utf8' });
    if (r.status === 0 && r.stdout) {
      for (const line of r.stdout.split('\n')) {
        if (!line.includes(titlePrefix)) continue;
        const parts = line.trim().split(/\s+/);
        return { id: parts[0], title: parts.slice(3).join(' '), w: 0, h: 0 };
      }
    }
    // 没有 wmctrl：用 xwininfo 的树（含未映射窗口）
    const x = spawnSync('sh', ['-c', `DISPLAY=${display} xwininfo -root -tree 2>/dev/null`], { encoding: 'utf8' });
    if (x.status === 0 && x.stdout) {
      for (const line of x.stdout.split('\n')) {
        if (!line.includes(titlePrefix)) continue;
        const m = line.match(/(0x[0-9a-f]+)\s+"([^"]*)".*?(\d+)x(\d+)\+/);
        if (m) return { id: m[1], title: m[2], w: Number(m[3]), h: Number(m[4]) };
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
