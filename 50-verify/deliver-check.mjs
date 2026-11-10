#!/usr/bin/env node
// 交付自检：四链（A/B/C/D）产物 + 四类操作 + 能力可达性
//
// 用法:
//   node 50-verify/deliver-check.mjs                  # L1 静态（四链 + 四类前置 + 能力可达性）
//   node 50-verify/deliver-check.mjs --with-gui       # L1 + L2 动态（需 Xvfb :303）
//   node 50-verify/deliver-check.mjs --full           # L3 预留（远程三/储藏三/revert 动态验证，未实现见 K-1475）
//   node 50-verify/deliver-check.mjs --json           # 机器可读
//
// 退出码: 0 全通过；1 有失败；2 环境缺失（静态都无法判定）

import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = path.resolve(import.meta.dirname, '..');
const HOME = os.homedir();
const FX = path.join(HOME, '.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11');
const APP = process.env.ST_APP || '/tmp/stpkg/SourceTree/app-3.4.15';
const WRAPPER_HOMES = ['/tmp/fakegit2', '/tmp/fakegit'];
const GUI = process.argv.includes('--with-gui');
const FULL = process.argv.includes('--full');
const JSON_OUT = process.argv.includes('--json');

// 能力可达性（L1 静态）：查的是「类型/方法名是否出现在 DLL 元数据字符串堆」，只读不解析、不改 DLL
const CAPABILITY_PROBES = [
  ['ITagSupport', 'SourceTree.Api.Dvcs.dll', '标签（K-1471）'],
  ['IStashSupport', 'SourceTree.Api.Dvcs.dll', '储藏（K-1469/K-1470）'],
  ['IPushSupport', 'SourceTree.Api.Dvcs.dll', '远程推送（K-1466/K-1467）'],
  ['GetReverseCommitProcess', 'SourceTree.Dvcs.Git.dll', '撤销提交（K-1473）'],
  ['RunThis', 'SourceTree.Dvcs.Git.dll', 'IRepoProcess 执行入口（K-1470）'],
];
const DOC_CONTRACTS = [
  ['70-operate/启动器/程序化操作清单.md', /第\s*⑥\s*条|远程操作/, '程序化清单第 ⑥ 条（远程操作）'],
];

const KNOWN = {
  A_md5: '18e599a87059e1499f01cdc71c8e57c5',
  B_sha: 'a6a32d4c912e3b66a9d561e3466992765c141e7fd08cf5e539a92d0ed8221fb7',
  C_sha: '9f4b635a2a3254fa9ae62d8487b1c85b55aace550c738e2821560c5be6f5dfbd',
  C_aliases: ['shlwapi.dll.so', 'libshlwapi.dll.so'],
  D_marker: 'sleep 0.15',
};

const results = [];
const record = (chain, item, ok, detail) => results.push({ chain, item, ok, detail });

function md5(file) {
  return createHash('md5').update(fs.readFileSync(file)).digest('hex');
}
function sha256(file) {
  return createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}
function exists(p) {
  try { fs.accessSync(p); return true; } catch { return false; }
}

// ---------- 静态：四链 ----------
function checkChainA() {
  const dll = path.join(APP, 'SourceTree.Api.UI.Wpf.dll');
  if (!exists(dll)) return record('A', 'SourceTree.Api.UI.Wpf.dll', false, `缺失 ${dll}`);
  const h = md5(dll);
  record('A', 'SourceTree.Api.UI.Wpf.dll (四补丁)', h === KNOWN.A_md5, h);
}
function checkChainB() {
  const dll = path.join(FX, 'PresentationCore.dll');
  if (!exists(dll)) return record('B', 'PresentationCore.dll', false, `缺失 ${dll}`);
  const h = sha256(dll);
  record('B', 'PresentationCore.dll (3 处解码适配)', h === KNOWN.B_sha, h.slice(0, 16) + '…');
}
function checkChainC() {
  const so = path.join(FX, 'libwpfwin32.so');
  if (!exists(so)) return record('C', 'libwpfwin32.so', false, `缺失 ${so}`);
  const h = sha256(so);
  record('C', 'libwpfwin32.so (PathRelativePathTo+WFE 降级)', h === KNOWN.C_sha, h.slice(0, 16) + '…');
  for (const alias of KNOWN.C_aliases) {
    const p = path.join(FX, alias);
    let ok = false;
    let detail = '缺失';
    if (exists(p)) {
      const target = fs.readlinkSync(p);
      ok = path.resolve(FX, target) === path.resolve(so);
      detail = `-> ${target}`;
    }
    record('C', `别名 ${alias}`, ok, detail);
  }
}
function checkChainD() {
  let found = null;
  for (const home of WRAPPER_HOMES) {
    const w = path.join(home, 'cmd\\git.exe');
    if (exists(w)) { found = w; break; }
  }
  if (!found) return record('D', 'cmd\\git.exe wrapper', false, '未找到（查 ' + WRAPPER_HOMES.join(' / ') + '）');
  const body = fs.readFileSync(found, 'utf8');
  const ok = body.includes(KNOWN.D_marker) && body.includes('/usr/bin/git');
  record('D', 'cmd\\git.exe wrapper', ok, ok ? `${found} 含 sleep 0.15` : `${found} 不含 sleep 0.15`);
}

// ---------- 静态：四类（可离线判定的部分） ----------
function checkCategoriesOffline() {
  // 提交/切分支/History 需要 GUI，离线只能验「前置存在」
  const handlerOk = exists(path.join(APP, 'SourceTree.Api.UI.Wpf.dll'));
  record('四类', '前置：四链齐备', results.every((r) => r.ok), results.every((r) => r.ok) ? '是' : '否');
  record('四类', '前置：APP 目录', handlerOk, APP);
}

// ---------- 静态：能力可达性（L1） ----------
// 只读：读 DLL 字节流，检查元数据字符串堆是否含目标名称（类型名/方法名以 UTF-8 存在 #Strings 堆）
function dllHasName(file, name) {
  if (!exists(file)) return { ok: false, detail: `缺失 ${file}` };
  const buf = fs.readFileSync(file);
  const needle = Buffer.from(name, 'utf8');
  return { ok: buf.includes(needle), detail: buf.includes(needle) ? '存在' : '未找到' };
}

function checkCapabilities() {
  // git 可用
  let gitOk = false;
  let gitDetail = '';
  try {
    gitDetail = String(run('git', ['--version'], { timeout: 10000 })).trim();
    gitOk = /^git version \d/.test(gitDetail);
  } catch (e) {
    gitDetail = String(e.message).slice(0, 80);
  }
  record('能力', 'git 可用', gitOk, gitDetail);

  // 关键类型/方法（Cecil 类静态判定：DLL 元数据只读扫描）
  if (!exists(APP)) {
    record('能力', 'APP 目录缺失', false, `${APP} 不存在，跳过能力静态检查`);
  } else {
    for (const [name, dll, label] of CAPABILITY_PROBES) {
      const r = dllHasName(path.join(APP, dll), name);
      record('能力', `${name}（${label}）`, r.ok, r.detail);
    }
  }

  // 文档契约
  for (const [rel, re, label] of DOC_CONTRACTS) {
    const p = path.join(ROOT, rel);
    let ok = false;
    let detail = '缺失';
    if (exists(p)) {
      ok = re.test(fs.readFileSync(p, 'utf8'));
      detail = ok ? '存在' : '未匹配';
    }
    record('能力', label, ok, detail);
  }
}

// ---------- 动态（--with-gui） ----------
function run(cmd, args, opts = {}) {
  return execFileSync(cmd, args, { encoding: 'utf8', timeout: opts.timeout || 180000, ...opts });
}
function sleep(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function checkChainDynamic() {
  const probeExe = '/tmp/fo/ST.fo.exe';
  const probeDll = '/tmp/fo/helper/bin/Debug/net10.0/FoHelper.dll';
  if (!exists(probeExe) || !exists(probeDll)) {
    record('四类', '动态验收', false, `探针资产缺失（${probeExe} / ${probeDll}）`);
    return;
  }
  const appExe = path.join(APP, 'SourceTree.exe');
  const backup = appExe + '.deliver-check.bak';
  const dllDst = path.join(APP, 'FoHelper.dll');
  const LOG_CANDIDATES = ['/tmp/fo/fo.log', path.join(APP, 'fo.log')];
  let restored = false;
  try {
    fs.copyFileSync(appExe, backup);
    fs.copyFileSync(probeExe, appExe);
    fs.copyFileSync(probeDll, dllDst);
    for (const c of LOG_CANDIDATES) { try { fs.unlinkSync(c); } catch {} }
    const dotnet = path.join(HOME, '.local/share/ldp/dotnet/dotnet');
    const env = {
      ...process.env,
      DISPLAY: ':303',
      WPF_LINUX_WIN32_SHIM: path.join(FX, 'libwpfwin32.so'),
      LD_LIBRARY_PATH: `${path.join(FX, 'native')}:${FX}:`,
      WPF_LINUX_ATSPI: '0',
      DOTNET_ReadyToRun: '0',
      GV_HOME: '/tmp/fakegit2',
    };
    const child = spawn('bash', ['-c', `cd "${APP}" && exec "${dotnet}" "${appExe}" >/tmp/deliver-check.out 2>/tmp/deliver-check.err`], {
      env, detached: true, stdio: 'ignore',
    });
    child.unref();
    sleep(26000);
    let alive = true;
    try { process.kill(child.pid, 0); } catch { alive = false; }
    record('四类', '进程存活', alive, alive ? `pid=${child.pid}` : '已退出（查 /tmp/deliver-check.err）');
    const logPath = LOG_CANDIDATES.find((c) => exists(c));
    let logBody = logPath ? fs.readFileSync(logPath, 'utf8') : '';
    record('四类', '探针日志', Boolean(logPath), logPath || '未生成（检查探针注入/DLL 部署）');
    let logRows = [...logBody.matchAll(/logRows(?:\.Count)?=(\d+)/g)].map((m) => Number(m[1]));
    let maxRows = logRows.length ? Math.max(...logRows) : 0;
    // K-1489：探针可能只跑到 tick1（未进 History 步骤，K-1305 类启动波动）⇒ 样本 0 时重试一次
    if (logRows.length === 0) {
      record('四类', 'History 重试', true, '首次样本 0，按 K-1489 重试一次');
      try { process.kill(child.pid, 0); } catch {}
      try { process.kill(-child.pid, 'SIGTERM'); } catch {}
      for (const c of LOG_CANDIDATES) { try { fs.unlinkSync(c); } catch {} }
      const child2 = spawn('bash', ['-c', `cd "${APP}" && exec "${dotnet}" "${appExe}" >/tmp/deliver-check2.out 2>/tmp/deliver-check2.err`], {
        env, detached: true, stdio: 'ignore',
      });
      child2.unref();
      sleep(26000);
      try { process.kill(-child2.pid, 'SIGTERM'); } catch {}
      const lp2 = LOG_CANDIDATES.find((c) => exists(c));
      if (lp2) logBody = fs.readFileSync(lp2, 'utf8');
      logRows = [...logBody.matchAll(/logRows(?:\.Count)?=(\d+)/g)].map((m) => Number(m[1]));
      maxRows = logRows.length ? Math.max(...logRows) : 0;
    }
    record('四类', 'History LogRows', maxRows >= 2, `max=${maxRows}（样本 ${logRows.length}）`);
    const stageLines = [...logBody.matchAll(/staged=(\d+)/g)].map((m) => Number(m[1]));
    record('四类', '文件状态 staged', stageLines.length > 0, `样本 ${stageLines.length} 个，max=${stageLines.length ? Math.max(...stageLines) : 0}`);
    const outDir = path.join(ROOT, '50-verify/读数/deliver-check', new Date().toISOString().replace(/[:.]/g, '-'));
    fs.mkdirSync(outDir, { recursive: true });
    fs.writeFileSync(path.join(outDir, 'fo.log'), logBody);
    const shot = path.join(outDir, 'shot.png');
    try {
      run('import', ['-window', 'root', shot], { env: { ...env, DISPLAY: ':303' } });
      record('四类', '截图', exists(shot), `${shot} ${exists(shot) ? fs.statSync(shot).size : 0} B`);
    } catch (e) {
      record('四类', '截图', false, String(e.message).slice(0, 80));
    }
  } catch (e) {
    record('四类', '动态验收', false, String(e.message).slice(0, 120));
  } finally {
    try {
      if (exists(backup)) { fs.copyFileSync(backup, path.join(APP, 'SourceTree.exe')); fs.unlinkSync(backup); restored = true; }
      if (exists(dllDst)) fs.unlinkSync(dllDst);
    } catch {}
  }
  try { process.kill(-child.pid, 'SIGTERM'); } catch {}
  try { execFileSync('bash', ['-c', 'pkill -f "dotnet .*SourceTree\.exe" || true']); } catch {}
  sleep(1500);
  record('四类', '探针复原', restored, restored ? 'SourceTree.exe 已还原' : '还原失败，请手工检查');
}

// ---------- main ----------
try { checkChainA(); } catch (e) { record('A', 'A 链', false, e.message); }
try { checkChainB(); } catch (e) { record('B', 'B 链', false, e.message); }
try { checkChainC(); } catch (e) { record('C', 'C 链', false, e.message); }
try { checkChainD(); } catch (e) { record('D', 'D 链', false, e.message); }
checkCategoriesOffline();
try { checkCapabilities(); } catch (e) { record('能力', '能力可达性', false, e.message); }
if (FULL) {
  console.log('[deliver-check] --full：L3 动态能力验证（远程三/储藏三/revert）未实现，见 K-1475；本次按 L2 执行。');
}
if (GUI || FULL) {
  try { checkChainDynamic(); } catch (e) { record('四类', '动态验收', false, e.message); }
}

if (JSON_OUT) {
  console.log(JSON.stringify({ results, ok: results.every((r) => r.ok) }, null, 2));
} else {
  const byChain = new Map();
  for (const r of results) {
    if (!byChain.has(r.chain)) byChain.set(r.chain, []);
    byChain.get(r.chain).push(r);
  }
  console.log('');
  for (const [chain, rows] of byChain) {
    console.log(`== ${chain} ==`);
    for (const r of rows) console.log(`  ${r.ok ? '✅' : '❌'} ${r.item}  ${r.detail}`);
  }
  console.log('');
  const fails = results.filter((r) => !r.ok);
  if (fails.length === 0) {
    console.log(`[deliver-check] 全部通过（${results.length} 项）${GUI || FULL ? '' : '（L1 静态，加 --with-gui 做 L2 动态验收）'}`);
  } else {
    console.log(`[deliver-check] ${fails.length}/${results.length} 项失败：`);
    for (const f of fails) console.log(`  - ${f.chain}/${f.item}: ${f.detail}`);
  }
  console.log('');
}
process.exit(results.every((r) => r.ok) ? 0 : 1);
