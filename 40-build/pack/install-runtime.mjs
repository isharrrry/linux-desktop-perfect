#!/usr/bin/env node
// MOD-52 分发侧：把自产 Runtime pack 安装到**用户目录**的 dotnet 根（SAFE-017：运行不提权）
//
// 为什么需要它：framework-dependent 应用要启动，宿主必须在 `DOTNET_ROOT/shared/` 下找到
// `Microsoft.WindowsDesktop.App/<ver>`。系统 dotnet 根是只读的（我们也不该往里装），
// 所以在用户目录造一个等价根：host 复用系统 host，`Microsoft.NETCore.App` 链接系统那位，
// `Microsoft.WindowsDesktop.App` 用自产 Runtime pack 的件。
//
// 用法：
//   node 40-build/pack/install-runtime.mjs                       # 安装/更新
//   node 40-build/pack/install-runtime.mjs --root ~/ldp-dotnet   # 指定根
//   node 40-build/pack/install-runtime.mjs --check               # 只检查是否已装且版本一致
//   node 40-build/pack/install-runtime.mjs --print               # 打印运行命令（不安装）
//
// 输出：<root>/shared/Microsoft.WindowsDesktop.App/<ver>/ + <root>/env.sh（DOTNET_ROOT / PATH 提示）

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const has = (f) => argv.includes(`--${f}`);
const val = (f, def) => {
  const i = argv.indexOf(`--${f}`);
  return i >= 0 && argv[i + 1] ? path.resolve(argv[i + 1].replace(/^~/, os.homedir())) : def;
};

const VER = val('ver', '10.0.11');
const SYS_DOTNET = val('sys', '/usr/share/dotnet');
const DEST = val('root', path.join(os.homedir(), '.local/share/ldp/dotnet'));
const NUPKG = path.join(ROOT, `40-build/pack/out/Microsoft.WindowsDesktop.App.Runtime.linux-x64.${VER}.nupkg`);
const FX = path.join(DEST, 'shared/Microsoft.WindowsDesktop.App', VER);

const fail = (code, msg) => {
  console.error(`ERROR ${code} ${msg}`);
  process.exit(1);
};

if (has('print')) {
  console.log(`DOTNET_ROOT=${DEST}`);
  console.log(`${DEST}/dotnet <应用>.dll`);
  process.exit(0);
}

if (!fs.existsSync(NUPKG)) fail('LDP-E-PACK-205', `缺 Runtime pack：${path.relative(ROOT, NUPKG)}（先跑 node 40-build/pack/build-pack.mjs）`);

if (has('check')) {
  const problems = [];
  if (!fs.existsSync(path.join(DEST, 'dotnet'))) problems.push(`缺 host：${path.join(DEST, 'dotnet')}`);
  if (!fs.existsSync(path.join(FX, 'PresentationFramework.dll'))) problems.push(`缺框架件：${path.join(FX, 'PresentationFramework.dll')}`);
  const deps = path.join(FX, 'Microsoft.WindowsDesktop.App.deps.json');
  if (!fs.existsSync(deps)) problems.push(`缺 deps.json：${deps}`);
  else {
    const d = JSON.parse(fs.readFileSync(deps, 'utf8'));
    if (d.runtimeTarget?.name && !d.runtimeTarget.name.includes('linux-x64')) {
      problems.push(`deps.json 的 runtimeTarget 不是 linux-x64：${d.runtimeTarget.name}`);
    }
  }
  for (const p of problems) console.error(`ERROR ${p}`);
  console.log(`install-runtime --check: ${problems.length ? `问题 ${problems.length}` : '已安装且版本一致'}（DOTNET_ROOT=${DEST}）`);
  process.exit(problems.length ? 1 : 0);
}

if (has('smoke')) {
  // 冒烟：用安装好的根跑一个样本，抓「启动到哪一步」的可复算读数。
  // 会自起 Xvfb（无头会话基线：08 §3.1），并按 install-runtime 生成的启动环境注入
  // DOTNET_ReadyToRun=0（D-005）与 WPF_LINUX_WIN32_SHIM。
  const sample = path.join(ROOT, 'samples/WpfFirstWindow/bin/Release/net10.0/WpfFirstWindow.dll');
  if (!fs.existsSync(sample)) fail('LDP-E-PACK-205', `样本未构建：${path.relative(ROOT, sample)}（先跑 node 50-verify/build/build-sample.mjs）`);
  const shim = path.join(FX, 'libwpfwin32.so');
  const display = `:${90 + Math.floor(Math.random() * 9)}`;
  const xvfb = spawnSync('sh', ['-c', `Xvfb ${display} -screen 0 1280x1024x24 >/dev/null 2>&1 & echo $!`], { encoding: 'utf8' });
  const xvfbPid = Number((xvfb.stdout || '').trim()) || 0;
  let result;
  try {
    await new Promise((r) => setTimeout(r, 1500));
    result = spawnSync(path.join(DEST, 'dotnet'), [sample, '--scr', 'SCR-01'], {
      encoding: 'utf8',
      timeout: 45000,
      env: {
        ...process.env,
        DOTNET_ROOT: DEST,
        DOTNET_ReadyToRun: '0',
        DISPLAY: display,
        WPF_LINUX_WIN32_SHIM: fs.existsSync(shim) ? shim : '',
        LD_LIBRARY_PATH: `${FX}/native:${process.env.LD_LIBRARY_PATH || ''}`,
      },
    });
  } finally {
    if (xvfbPid) try { process.kill(xvfbPid, 'SIGTERM'); } catch {}
  }
  const out = `${result.stdout || ''}${result.stderr || ''}`;
  const stages = [
    { key: 'host/framework 解析阶段', re: /deps\.json|host|framework/i },
    { key: 'Application 初始化阶段', re: /ApplicationInit|System\.Windows\.Application/ },
    { key: '窗口创建阶段', re: /CreateWindowEx|HwndWrapper|HwndSource/ },
    { key: '渲染提交阶段（DUCE Commit）', re: /DUCE|Commit\(\)|MediaContext/ },
  ];
  let stage = '未知阶段';
  for (const st of stages) if (st.re.test(out)) stage = st.key;
  const first = out.split('\n').find((l) => /Exception|error|Error/.test(l)) || '(无异常行)';
  const report = {
    generated_by: '40-build/pack/install-runtime.mjs --smoke',
    dotnet_root: DEST,
    framework: VER,
    display,
    env: { DOTNET_ReadyToRun: '0', WPF_LINUX_WIN32_SHIM: shim, DISPLAY: display },
    sample: path.relative(ROOT, sample),
    exit_code: result.status,
    timed_out: result.status === null,
    stage,
    first_line: first.slice(0, 240),
  };
  const dir = path.join(ROOT, '50-verify/读数/contract');
  fs.mkdirSync(dir, { recursive: true });
  const f = path.join(dir, `startup-${new Date().toISOString().replace(/[:.]/g, '-')}.json`);
  fs.writeFileSync(f, JSON.stringify(report, null, 2) + '\n', 'utf8');
  console.log(`install-runtime --smoke: 阶段=${stage}；首行=${first.slice(0, 180)}`);
  console.log(`  读数：${path.relative(ROOT, f)}`);
  process.exit(0);
}

// 1) host 与共享运行时
fs.mkdirSync(DEST, { recursive: true });
const hostBin = path.join(SYS_DOTNET, 'dotnet');
if (!fs.existsSync(hostBin)) fail('LDP-E-PACK-207', `系统 dotnet 根不可用：${hostBin}`);
fs.copyFileSync(hostBin, path.join(DEST, 'dotnet'));
fs.chmodSync(path.join(DEST, 'dotnet'), 0o755);
const fxrSrc = path.join(SYS_DOTNET, 'host/fxr');
if (fs.existsSync(fxrSrc)) {
  const fxrDst = path.join(DEST, 'host/fxr');
  fs.rmSync(path.join(DEST, 'host'), { recursive: true, force: true });
  fs.mkdirSync(fxrDst, { recursive: true });
  for (const d of fs.readdirSync(fxrSrc)) {
    fs.cpSync(path.join(fxrSrc, d), path.join(fxrDst, d), { recursive: true });
  }
}
// Microsoft.NETCore.App：链接系统那位（不是我们的包，避免复制 70+ MB）
const netcoreVers = fs.existsSync(path.join(SYS_DOTNET, 'shared/Microsoft.NETCore.App'))
  ? fs.readdirSync(path.join(SYS_DOTNET, 'shared/Microsoft.NETCore.App')).sort()
  : [];
const netcoreVer = netcoreVers[netcoreVers.length - 1];
if (!netcoreVer) fail('LDP-E-PACK-207', '系统 dotnet 根下没有 Microsoft.NETCore.App 共享框架');
fs.mkdirSync(path.join(DEST, 'shared'), { recursive: true });
fs.rmSync(path.join(DEST, 'shared/Microsoft.NETCore.App'), { recursive: true, force: true });
fs.symlinkSync(path.join(SYS_DOTNET, 'shared/Microsoft.NETCore.App'), path.join(DEST, 'shared/Microsoft.NETCore.App'));

// 2) 自产 Microsoft.WindowsDesktop.App
fs.rmSync(path.join(DEST, 'shared/Microsoft.WindowsDesktop.App'), { recursive: true, force: true });
fs.mkdirSync(FX, { recursive: true });
execFileSync('unzip', ['-q', '-o', NUPKG, '-d', path.join(DEST, '.stage')], { stdio: 'pipe' });
const staged = path.join(DEST, '.stage/runtimes/linux-x64/lib/net10.0');
if (!fs.existsSync(staged)) fail('LDP-E-PACK-207', `Runtime pack 内无 runtimes/linux-x64/lib/net10.0：${path.relative(ROOT, NUPKG)}`);
for (const f of fs.readdirSync(staged)) {
  if (fs.statSync(path.join(staged, f)).isDirectory()) continue;
  fs.copyFileSync(path.join(staged, f), path.join(FX, f));
}
fs.rmSync(path.join(DEST, '.stage'), { recursive: true, force: true });
// native 资产（libwpfwin32.so 等）：框架加载器不找它们，运行时由 P/Invoke resolver 解析；
// 这里放进共享框架的 native/ 子目录，并写一个 env 提示，实际搜索路径由 resolver 负责。
const nativeStage = path.join(ROOT, `40-build/pack/out/.native-${VER}`);
fs.mkdirSync(nativeStage, { recursive: true });
execFileSync('unzip', ['-q', '-o', NUPKG, `runtimes/linux-x64/native/*`, '-d', nativeStage], { stdio: 'pipe' });
const nativeSrc = path.join(nativeStage, 'runtimes/linux-x64/native');
let nativeCount = 0;
if (fs.existsSync(nativeSrc)) {
  const dst = path.join(FX, 'native');
  fs.mkdirSync(dst, { recursive: true });
  for (const f of fs.readdirSync(nativeSrc)) {
    fs.copyFileSync(path.join(nativeSrc, f), path.join(dst, f));
    fs.chmodSync(path.join(dst, f), 0o755);
    nativeCount++;
  }
}
fs.rmSync(nativeStage, { recursive: true, force: true });

// shim 库同时放到框架根：DllImportResolver（wpf-port 的 Win32ShimResolver）先在本程序集目录找，
// 再在框架目录找；放根目录最省事，env 里的 WPF_LINUX_WIN32_SHIM 是第二条保险。
const shimSrc = path.join(FX, 'native/libwpfwin32.so');
if (fs.existsSync(shimSrc)) fs.copyFileSync(shimSrc, path.join(FX, 'libwpfwin32.so'));

fs.writeFileSync(
  path.join(DEST, 'env.sh'),
  [
    '# 由 40-build/pack/install-runtime.mjs 生成（SAFE-017：用户目录安装，不提权）',
    `export DOTNET_ROOT=${DEST}`,
    `export PATH=${DEST}:$PATH`,
    `export LD_LIBRARY_PATH=${FX}/native:\${LD_LIBRARY_PATH:-}`,
    '# 上游 Runtime pack 的程序集是 ReadyToRun（win-x64 原生代码），Linux 上必须关闭 R2R 才能加载（D-005）',
    'export DOTNET_ReadyToRun=0',
    '# P/Invoke 的 Win32 面映射到自产 shim（MOD-02/03）',
    `export WPF_LINUX_WIN32_SHIM=${FX}/libwpfwin32.so`,
    'unset DISPLAY 2>/dev/null || true',
    '',
  ].join('\n'),
  'utf8',
);

const manifest = {
  generated_by: '40-build/pack/install-runtime.mjs',
  dotnet_root: DEST,
  framework: 'Microsoft.WindowsDesktop.App',
  version: VER,
  source_nupkg: path.relative(ROOT, NUPKG),
  netcore_app: netcoreVer,
  files: fs.readdirSync(FX).filter((f) => f.endsWith('.dll')).length,
  natives: nativeCount,
};
fs.writeFileSync(path.join(DEST, 'install-manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');
console.log(`install-runtime: ${DEST} ← ${path.relative(ROOT, NUPKG)}（框架件 ${manifest.files} 个、原生件 ${nativeCount} 个、NETCore ${netcoreVer}）`);
console.log(`  source ${path.join(DEST, 'env.sh')} && ${DEST}/dotnet <应用>.dll`);
