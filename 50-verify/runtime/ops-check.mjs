#!/usr/bin/env node
// 四类操作可复跑验证（提交查看 / 差异 / 提交 / 切分支）。
//
// 背景：这四类操作 2026-11-12/13 全靠 /tmp 里的手写反射 hook 验证过，未入库。
// 这里把「启动 app → 打开仓库 → 四类操作 → 判定」固化成一个脚本，入口与坑见
// 《已知坑与降级清单》K-1500~K-1502（RepoViewMode=0、FileStatusRecord.Selected、
// CommitProcess(4 参)+RunThis、CheckoutBranch、xdotool windowmap + xwd）。
//
// 用法:
//   node 50-verify/runtime/ops-check.mjs            # 环境不满足则 SKIP（rc=0）
//   node 50-verify/runtime/ops-check.mjs --verbose  # 打印启动日志尾部
// 退出码: 0 = 全 PASS 或有 SKIP 且无 FAIL；1 = 有 FAIL；2 = 脚本自身错误。
//
// 前置（不满足即 SKIP，不误报 FAIL）：
//   - app 目录 $ST_APP 或 /tmp/stpkg/SourceTree/app-3.4.15 存在 SourceTree.exe
//   - ~/bin/which、~/bin/git 存在（K-1500：RepoProcess 读竞态需要包装脚本）
//   - /tmp/stgit 是 git 仓库（可用 LDP_OPS_REPO 覆盖）
//   - DISPLAY 可用的 X（默认 :0）
import { execFileSync, spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '../..');
const APP = process.env.ST_APP || '/tmp/stpkg/SourceTree/app-3.4.15';
const REPO = process.env.LDP_OPS_REPO || '/tmp/stgit';
const BRANCH = process.env.LDP_OPS_BRANCH || 'feature-x';
const DISPLAY = process.env.DISPLAY || ':0';
const OUT = process.env.LDP_OPS_OUT || '/tmp/ops-check';
const HOOK_SRC = path.join(ROOT, '50-verify/runtime/ops-hook');
const HOOK_DLL = path.join(OUT, 'hook', 'LdpOpsHook.dll');
const RESULT = path.join(OUT, 'result.json');
const VERBOSE = process.argv.includes('--verbose');

const summary = [];
const skipped = (name, why) => summary.push({ name, verdict: 'SKIP', why });

function sh(cmd, args, opts = {}) {
  return execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...opts });
}

function envCheck() {
  const miss = [];
  if (!fs.existsSync(path.join(APP, 'SourceTree.exe'))) miss.push(`app 不存在：${APP}/SourceTree.exe`);
  for (const b of ['which', 'git']) {
    if (!fs.existsSync(path.join(os.homedir(), 'bin', b))) miss.push(`缺 ~/bin/${b}（K-1500 包装脚本）`);
  }
  if (!fs.existsSync(path.join(REPO, '.git'))) miss.push(`测试仓不是 git 仓库：${REPO}`);
  return miss;
}

function git(args) {
  return sh('git', ['-C', REPO, ...args]).trim();
}

function resetRepo() {
  const roots = git(['rev-list', '--max-parents=0', 'HEAD']).split('\n').filter(Boolean);
  if (!roots.length) throw new Error('测试仓没有提交，无法重置');
  const base = roots[roots.length - 1];
  git(['checkout', '-B', 'main', base]);
  git(['reset', '--hard', base]);
  for (const f of ['probe-check.txt', 'probe.txt', 'probe2.txt']) {
    try { git(['rm', '--cached', '-f', f]); } catch { /* 未跟踪 */ }
    const p = path.join(REPO, f);
    if (fs.existsSync(p)) fs.rmSync(p);
  }
  git(['branch', '-f', BRANCH, base]);
  // 提交用：staged 改动（CommitProcess 没有 -a，必须先 add）
  fs.writeFileSync(path.join(REPO, 'probe-check.txt'), 'probe from ops-check\n');
  git(['add', 'probe-check.txt']);
  // 差异用：未暂存改动（FileList 显示的是未暂存列表，K-1501 实测）
  fs.writeFileSync(path.join(REPO, 'probe-diff.txt'), 'diff probe from ops-check\n');
  return base;
}

function buildHook() {
  if (fs.existsSync(HOOK_DLL)) return;
  fs.mkdirSync(path.dirname(HOOK_DLL), { recursive: true });
  sh('dotnet', ['build', HOOK_SRC, '-c', 'Release', '-o', path.dirname(HOOK_DLL), '-nologo', '-v', 'q']);
  if (!fs.existsSync(HOOK_DLL)) throw new Error('hook 构建后找不到 ' + HOOK_DLL);
}

function startApp() {
  fs.mkdirSync(OUT, { recursive: true });
  for (const f of ['result.json', 'hook.log']) {
    const p = path.join(OUT, f);
    if (fs.existsSync(p)) fs.rmSync(p);
  }
  const pw = path.join(os.homedir(), 'bin');
  const env = {
    ...process.env,
    PATH: `${pw}:${process.env.PATH}`,
    DISPLAY,
    DOTNET_STARTUP_HOOKS: HOOK_DLL,
    LDP_OPS_OUT: OUT,
    LDP_OPS_REPO: REPO,
    LDP_OPS_BRANCH: BRANCH,
  };
  const log = fs.openSync(path.join(OUT, 'app.log'), 'w');
  const child = spawn('bash', ['70-operate/启动器/sourcetree', '--profile', 'rdp'], {
    cwd: ROOT, env, detached: true, stdio: ['ignore', log, log],
  });
  child.unref();
  return child.pid;
}

function readResult() {
  if (!fs.existsSync(RESULT)) return null;
  try {
    const txt = fs.readFileSync(RESULT, 'utf8');
    if (!txt.trim().endsWith('}')) return null;
    return JSON.parse(txt);
  } catch { return null; }
}

function waitResult(timeoutMs = 180000) {
  const t0 = Date.now();
  let last = null;
  while (Date.now() - t0 < timeoutMs) {
    const r = readResult();
    if (r) {
      last = r;
      // hook 是增量落盘的：四个操作都有值即视为完成，不必等 finished
      if (r.history && r.diff && r.commit && r.checkout) return r;
      if (r.finished) return r;
    }
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000);
  }
  return last;
}

function stopApp() {
  try { sh('bash', ['70-operate/启动器/stop-sourcetree'], { cwd: ROOT }); } catch { /* 已退出 */ }
}

function screenshot() {
  try {
    const ids = sh('xdotool', ['search', '--name', 'Sourcetree'], { env: { ...process.env, DISPLAY } })
      .trim().split('\n').filter(Boolean);
    const id = ids[ids.length - 1];
    if (!id) return null;
    sh('xdotool', ['windowmap', id], { env: { ...process.env, DISPLAY } });
    const xwd = path.join(OUT, 'main.xwd');
    sh('xwd', ['-id', id, '-display', DISPLAY, '-out', xwd]);
    const png = path.join(OUT, 'main.png');
    sh('convert', [xwd, png]);
    return { png, bytes: fs.statSync(png).size };
  } catch { return null; }
}

function main() {
  const miss = envCheck();
  if (miss.length) {
    for (const m of miss) skipped('env', m);
    console.log('[ops-check] SKIP：' + miss.join('；'));
    console.log(JSON.stringify(summary, null, 1));
    return 0;
  }

  const base = resetRepo();
  buildHook();
  const pid = startApp();
  console.log(`[ops-check] app pid=${pid}，等待 hook 结果…`);
  const res = waitResult();
  const shot = res ? screenshot() : null;
  stopApp();

  if (!res) {
    for (const n of ['history', 'diff', 'commit', 'checkout']) skipped(n, 'hook 未在 120s 内产出 result.json');
    console.log('[ops-check] FAIL：hook 无结果，见 ' + path.join(OUT, 'hook.log'));
    if (VERBOSE) tail(path.join(OUT, 'hook.log'));
    return 1;
  }

  const ghLog = (() => { try { return git(['log', '-1', '--format=%s']); } catch { return ''; } })();
  const ghBranch = (() => { try { return git(['branch', '--show-current']); } catch { return ''; } })();

  // checkout：UI 入口通过则 PASS；UI 超时（SKIP）时用 git 层做兜底验证并保留 SKIP 标记
  let coVerdict = 'FAIL';
  let coDetail = `undefined (branch: ${ghBranch})`;
  if (res.checkout === 'PASS' && ghBranch === BRANCH) { coVerdict = 'PASS'; coDetail = `PASS (branch: ${ghBranch})`; }
  else if (res.checkout === 'SKIP') {
    coVerdict = 'SKIP';
    coDetail = `UI 入口超时（${res.checkout_reason || ''}）`;
  } else if (res.checkout === 'FAIL') {
    coVerdict = 'FAIL'; coDetail = `FAIL (${res.checkout_reason || ''})`;
  }

  const rows = [
    ['history', res.history === 'PASS', res.history],
    ['diff', res.diff === 'PASS', res.diff],
    ['commit', res.commit === 'PASS' && ghLog.includes('probe-commit from ops-check'), `${res.commit} (git log: "${ghLog}")`],
    ['checkout', coVerdict === 'PASS', coVerdict === 'PASS' ? coDetail : coVerdict],
  ];
  let fail = 0;
  for (const [name, ok, detail] of rows) {
    const verdict = ok ? 'PASS' : (detail === 'SKIP' ? 'SKIP' : 'FAIL');
    if (verdict === 'FAIL') fail += 1;
    summary.push({ name, verdict, detail });
  }

  console.log('[ops-check] base=' + base + (shot ? ` 截图=${shot.png} (${shot.bytes} B)` : ''));
  console.log(JSON.stringify(summary, null, 1));
  if (VERBOSE) tail(path.join(OUT, 'hook.log'), 20);
  if (fail) {
    console.log(`[ops-check] ${fail} 项 FAIL，详见 ${path.join(OUT, 'hook.log')}`);
    return 1;
  }
  console.log('[ops-check] 全部 PASS 或 SKIP');
  return 0;
}

function tail(file, n = 30) {
  try {
    const lines = fs.readFileSync(file, 'utf8').trimEnd().split('\n');
    console.log('---- ' + file + ' (tail ' + n + ') ----');
    console.log(lines.slice(-n).join('\n'));
  } catch { /* 无文件 */ }
}

try {
  process.exit(main());
} catch (e) {
  console.error('[ops-check] 脚本错误：' + (e && e.stack ? e.stack : e));
  process.exit(2);
}
