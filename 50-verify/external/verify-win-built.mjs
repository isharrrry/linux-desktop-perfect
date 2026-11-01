#!/usr/bin/env node
// 通用「Windows 编译产物 → Linux 免编运行」验证器
//
// 用法：
//   node 50-verify/external/verify-win-built.mjs --name ProbeWpf --title ProbeWpf --seconds 15
//   node 50-verify/external/verify-win-built.mjs --name extA/ExtA_WinForms --title "ExtA WinForms"
//
// 退出码：0 = 出画面 + UIA 树非空；1 = 缺窗口/没截图/无 UIA；2 = 参数错误
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { startXvfb, waitDisplay, findWindow, sleep, FRAMEWORK_DIR, DOTNET_ROOT } from '../lib/app-session.mjs';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const OUT = path.join(ROOT, '50-verify/external/win-built');

function arg(name, def = null) {
  const i = process.argv.indexOf('--' + name);
  return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : def;
}

const name = arg('name');
if (!name) {
  console.error('用法: node 50-verify/external/verify-win-built.mjs --name <dir[/sub]> [--title <标题前缀>] [--seconds N]');
  process.exit(2);
}
const titlePrefix = arg('title', '');
const seconds = Number(arg('seconds', '15'));
// 【K-254】`--display` 复用已有 display（如 `-dpi 144` 起的高 DPI 会话）
const reuseDisplay = arg('display', null);

const dir = path.join(OUT, name);
if (!fs.existsSync(dir)) { console.error('目录不存在: ' + dir); process.exit(2); }

const dlls = fs.readdirSync(dir).filter((f) => f.endsWith('.dll') && !f.includes('.resources.'));
if (dlls.length !== 1) { console.error('期望恰好一个 .dll，实际 ' + dlls.length + ': ' + dlls.join(', ')); process.exit(2); }
const dll = path.join(dir, dlls[0]);

const ts = new Date().toISOString().replace(/[:.]/g, '-');
const shot = path.join(dir, 'shot-' + ts + '.png');
const treePath = path.join(dir, 'tree-' + ts + '.json');
const logPath = path.join(dir, 'run-' + ts + '.log');

let xvfb = null;
let display = reuseDisplay;
if (!display) {
  xvfb = startXvfb();
  const ok = await waitDisplay(xvfb.display, 15000);
  if (!ok) { console.error('Xvfb 未就绪'); xvfb.kill(); process.exit(1); }
  display = xvfb.display;
}
await sleep(900);
spawnSync('sh', ['-c', 'DISPLAY=' + display + ' xsetroot -solid "#3050a0" 2>/dev/null']);

const out = fs.openSync(logPath, 'w');
const proc = spawn(path.join(DOTNET_ROOT, 'dotnet'), [dll], {
  stdio: ['ignore', out, out],
  env: {
    PATH: '/usr/bin:/bin',
    HOME: os.homedir(),
    DISPLAY: xvfb.display,
    DOTNET_ROOT,
    DOTNET_ReadyToRun: '0',
    LD_LIBRARY_PATH: FRAMEWORK_DIR + '/native:' + FRAMEWORK_DIR + ':',
    WPF_LINUX_WIN32_SHIM: FRAMEWORK_DIR + '/libwpfwin32.so',
    DOTNET_STARTUP_HOOKS: path.join(FRAMEWORK_DIR, 'LdpA11y.dll'),
    LDP_A11Y_DUMP: treePath,
    LDP_A11Y_REPEAT: '1',
  },
});
let exited = null;
proc.on('exit', (code, sig) => { exited = { code, sig }; });

const win = titlePrefix ? await findWindow(display, titlePrefix, seconds * 1000) : null;
for (let i = 0; i < 80 && !fs.existsSync(treePath); ++i) await sleep(250);
await sleep(600);

spawnSync('sh', ['-c', 'DISPLAY=' + display + ' import -window root ' + JSON.stringify(shot) + ' 2>/dev/null']);
const shotBytes = fs.existsSync(shot) ? fs.statSync(shot).size : 0;

let uia = [];
if (fs.existsSync(treePath)) {
  try {
    const t = JSON.parse(fs.readFileSync(treePath, 'utf8'));
    uia = (t.nodes || []).map((n) => (n.role || '?') + ':' + (n.name || ''));
  } catch { /* 树格式异常 */ }
}

if (exited == null) { try { proc.kill('SIGTERM'); } catch {} }
if (xvfb) { try { xvfb.kill(); } catch {} }
try { fs.closeSync(out); } catch {}

const logTail = fs.existsSync(logPath) ? fs.readFileSync(logPath, 'utf8').split('\n').filter(Boolean).slice(-6) : [];

const result = {
  name, dll: path.relative(ROOT, dll), display: display,
  windowFound: !!win,
  window: win ? { id: win.id, title: win.title } : null,
  shot: path.relative(ROOT, shot), shotBytes,
  uiaNodeCount: uia.length, uiaSample: uia.slice(0, 20),
  exited, logTail,
};
const reportPath = path.join(dir, 'verify-' + ts + '.json');
fs.writeFileSync(reportPath, JSON.stringify(result, null, 1));
console.log(JSON.stringify({ ...result, report: path.relative(ROOT, reportPath) }, null, 1));
process.exit(win && shotBytes > 0 && uia.length > 0 ? 0 : 1);
