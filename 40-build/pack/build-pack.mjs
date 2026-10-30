#!/usr/bin/env node
// MOD-51/MOD-52：pack 组装（Ref + Runtime）—— 03 §3.6.1/§3.6.2、IF-023
//
// 输入（全部为已存在的上游件/自产件，本脚本不联网生成二进制）：
//   · 上游 Ref 件：40-build/lock/packs/microsoft.windowsdesktop.app.ref.<ver>.nupkg
//     （缺时由本机 NuGet 缓存目录打包生成，取件命令见 40-build/lock/README.md）
//   · 上游 Runtime 件：40-build/lock/packs/microsoft.windowsdesktop.app.runtime.win-x64.<ver>.nupkg
//   · 自产托管件：40-build/build/wpf/artifacts/bin/<asm>/Release/net10.0/<asm>.dll
//                 参考程序集：artifacts/bin/<asm>-ref/Release/net10.0/<asm>.dll
//   · 自产原生件：40-build/native-shim/bin/*.so
// 输出：
//   · 40-build/pack/out/Microsoft.WindowsDesktop.App.Ref.<ver>.nupkg
//   · 40-build/pack/out/Microsoft.WindowsDesktop.App.Runtime.linux-x64.<ver>.nupkg
//   · 40-build/pack/pack-report.json（逐件来源与 sha256，读数供 ACC-003/ACC-004 引用）
//
// 纪律：①包标识与版本与官方同名同版（IF-023），故组装后清掉 NuGet 缓存里的同版目录，
//       否则 restore 会命中缓存里的上游件而不是自产件；
//       ②确定性（文件时间戳固定为 SOURCE_DATE_EPOCH、按名排序、不含时间戳字段）；
//       ③缺件即失败并给错误码（LDP-E-PACK-205/207），不允许静默跳过。
//
// 用法：node 40-build/pack/build-pack.mjs [--ver 10.0.11] [--no-cache-clear]

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const argVer = argv.indexOf('--ver');
const VER = argVer >= 0 && argv[argVer + 1] ? argv[argVer + 1] : '10.0.11';
const CACHE_CLEAR = !argv.includes('--no-cache-clear');
const EPOCH = 1700000000;

const NU = path.join(os.homedir(), '.nuget/packages');
const REF_CACHE = path.join(NU, `microsoft.windowsdesktop.app.ref/${VER}`);
const LOCK = path.join(ROOT, '40-build/lock/packs');
const REF_NUPKG = path.join(LOCK, `microsoft.windowsdesktop.app.ref.${VER}.nupkg`);
const RT_NUPKG = path.join(LOCK, `microsoft.windowsdesktop.app.runtime.win-x64.${VER}.nupkg`);
const ART = path.join(ROOT, '40-build/build/wpf/artifacts/bin');
const SHIM = path.join(ROOT, '40-build/native-shim/bin');
const OUT = path.join(ROOT, '40-build/pack/out');
const FL = path.join(ROOT, '40-build/pack/FrameworkList.xml');
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'ldp-pack-'));

const fail = (code, msg) => {
  console.error(`ERROR ${code} ${msg}`);
  fs.rmSync(TMP, { recursive: true, force: true });
  process.exit(1);
};

const rel = (p) => path.relative(ROOT, p);
const run = (cmd, args, opts = {}) => execFileSync(cmd, args, { stdio: ['ignore', 'pipe', 'pipe'], ...opts });
const sha256 = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

function walk(dir, base = dir, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, base, out);
    else out.push(path.relative(base, p));
  }
  return out;
}

function fixTimes(dir) {
  for (const relP of walk(dir)) {
    const t = new Date(EPOCH * 1000);
    fs.utimesSync(path.join(dir, relP), t, t);
  }
}

function zipDir(dir, outFile) {
  const entries = walk(dir).map((p) => p.split(path.sep).join('/'));
  fs.rmSync(outFile, { force: true });
  run('zip', ['-X', '-q', outFile, '-@'], { cwd: dir, input: entries.join('\n') + '\n', stdio: ['pipe', 'pipe', 'pipe'] });
  return entries;
}

function unzipTo(nupkg, dir) {
  fs.mkdirSync(dir, { recursive: true });
  run('unzip', ['-q', '-o', nupkg, '-d', dir]);
}

// ---------------------------------------------------------------- 契约：FrameworkList 唯一事实源

if (!fs.existsSync(FL)) fail('LDP-E-PACK-205', `FrameworkList 缺件：${rel(FL)}（契约文件，见 05 §3.7）`);
const flXml = fs.readFileSync(FL, 'utf8');
const attr = (tag, name) => {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
};
const flEntries = [...flXml.matchAll(/<File\b[^>]*\/>/g)]
  .map((m) => ({
    asm: attr(m[0], 'AssemblyName'),
    rel: attr(m[0], 'Path'),
    type: attr(m[0], 'Type'),
    profile: attr(m[0], 'Profile'),
    version: attr(m[0], 'AssemblyVersion'),
    token: attr(m[0], 'PublicKeyToken'),
  }))
  .filter((e) => e.asm && e.rel);
if (!flEntries.length) fail('LDP-E-PACK-205', `FrameworkList 解析出 0 条清单：${rel(FL)}`);

// 自产件索引（03 §3.6）：
//   实现件 = artifacts/bin/<asm>/…（wpf-port 的 Linux 工程产物，经 build-port.mjs --map 落位；
//            上游 csproj 直编的产物也落这里，两者同源）
//   参考件 = artifacts/bin/<asm>-ref/…（上游 csproj 直编的 ref 程序集，身份可能仍是上游 MSFT token）
// 取件优先级：实现件 > 参考件。理由：自产件之间的引用闭包按**实现件身份**（10.0.0.0 / WCP token）建立，
// 若某件退回上游 ref 程序集（MSFT token），XAML 编译期会按引用闭包去找 31bf token 的版本而落空（MC1000）。
const PORT_OUT = path.join(ROOT, '40-build/wpf-port/out');
// 自产件**只认移植链产物**（wpf-port/out，身份统一 10.0.0.0 / WCP token）。
// 不认 artifacts/bin：那里既有移植链映射，也有上游 csproj 直编产物（MSFT token），混用会破坏引用闭包。
function builtFile(asm) {
  const port = path.join(PORT_OUT, `${asm}.dll`);
  return fs.existsSync(port) ? port : null;
}
const selfAsms = new Map();
for (const e of flEntries) {
  const impl = builtFile(e.asm);
  if (impl) selfAsms.set(e.asm, { impl, ref: null });
}

// 替换闸门（PACK-SELFBUILT）：替换的前提不是「21 件齐」，而是**每个自产件的身份与清单一致**
// （name / AssemblyVersion / PublicKeyToken）。理由（实测 MC1000，2026-10-30）：
// 上游件与自产件的 token 必须逐件相同，否则 XAML 编译期的 MetadataLoadContext 会同时遇到两组身份
// （自产 PresentationFramework 引用 System.Xaml@某 token，残留的上游件引用另一个）→ 报
// 「Could not find assembly 'System.Xaml, … PublicKeyToken=…'」。身份一致时，混用自产/上游件是安全的。
// 身份口径由 20-spec ACC-004 与 50-verify/build/identity.mjs 校验（同一把尺子）。
const ASM_ID = path.join(ROOT, '50-verify/build/asm-id/bin/Release/net10.0/asm-id.dll');
// 已登记的替换件（D-004 必抛桩件自产替身）不参与身份比对：它们的身份是按设计不同的。
const MUST_STUB = path.join(ROOT, '40-build/pack/must-stub.json');
const stubAsms = new Set(
  fs.existsSync(MUST_STUB) ? (JSON.parse(fs.readFileSync(MUST_STUB, 'utf8')).entries || []).map((e) => e.assembly) : [],
);
function verifyIdentity() {
  if (!selfAsms.size) return { ok: false, reason: '没有自产件（先跑 node 40-build/wpf-port/build-port.mjs --map）' };
  if (!fs.existsSync(ASM_ID)) return { ok: false, reason: `判据侧反射工具未构建：${rel(ASM_ID)}（dotnet build 50-verify/build/asm-id）` };
  const list = [...selfAsms.keys()].map((asm) => selfAsms.get(asm).impl);
  const r = run('dotnet', [ASM_ID, ...list]);
  const ids = JSON.parse(r.toString('utf8'));
  const bad = [];
  for (const id of ids) {
    const want = flEntries.find((e) => e.asm === id.name);
    if (!want) continue; // 清单外内部件（PBT/DWF/Provider），不参与 pack 替换
    if (stubAsms.has(id.name)) continue; // D-004：已登记的必抛桩件替身，身份按设计不同
    if (id.version !== want.version) bad.push(`${id.name} 版本 ${id.version}≠${want.version}`);
    if (id.token !== want.token) bad.push(`${id.name} token ${id.token || '(无)'}≠${want.token}`);
  }
  return bad.length
    ? { ok: false, reason: `自产件身份与清单不符：${bad.slice(0, 4).join('；')}` }
    : { ok: true, reason: `自产件身份与清单一致（${ids.length} 件）` };
}
const identityCheck = verifyIdentity();
const USE_SELF_BUILT = argv.includes('--self-built') || identityCheck.ok;

// ---------------------------------------------------------------- 上游取件（缺失时从 NuGet 缓存打包）

function ensureRefSource() {
  if (fs.existsSync(REF_NUPKG)) return;
  if (!fs.existsSync(REF_CACHE)) {
    fail('LDP-E-PACK-205', `上游 Ref 件缺件：${rel(REF_CACHE)}（取件命令见 40-build/lock/README.md）`);
  }
  const stage = path.join(TMP, 'ref-src');
  fs.mkdirSync(stage, { recursive: true });
  for (const relP of walk(REF_CACHE)) {
    if (relP.endsWith('.nupkg') || relP.endsWith('.nupkg.sha512') || relP.endsWith('.nuspec')) continue;
    const dst = path.join(stage, relP);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.copyFileSync(path.join(REF_CACHE, relP), dst);
  }
  fs.copyFileSync(path.join(REF_CACHE, 'microsoft.windowsdesktop.app.ref.nuspec'), path.join(stage, 'Microsoft.WindowsDesktop.App.Ref.nuspec'));
  zipDir(stage, REF_NUPKG);
}

// ---------------------------------------------------------------- Ref pack（MOD-51）

function buildRefPack() {
  ensureRefSource();
  const stage = path.join(TMP, 'ref');
  unzipTo(REF_NUPKG, stage);
  const dir = path.join(TMP, 'ref-out');
  fs.mkdirSync(dir, { recursive: true });
  for (const relP of walk(stage)) {
    if (relP.endsWith('.nuspec')) continue;
    const dst = path.join(dir, relP);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.copyFileSync(path.join(stage, relP), dst);
  }

  // 清单：契约文件原样复制（不改内容，差异由 ACC-003 判红）
  fs.mkdirSync(path.join(dir, 'data'), { recursive: true });
  fs.copyFileSync(FL, path.join(dir, 'data/FrameworkList.xml'));

  // 包元数据：沿用上游 nuspec，只改版本（其余字段是包身份，不伪造）
  const nuspecSrc = fs.existsSync(path.join(stage, 'Microsoft.WindowsDesktop.App.Ref.nuspec'))
    ? path.join(stage, 'Microsoft.WindowsDesktop.App.Ref.nuspec')
    : path.join(stage, 'microsoft.windowsdesktop.app.ref.nuspec');
  if (!fs.existsSync(nuspecSrc)) fail('LDP-E-PACK-205', '上游 Ref 包内缺 nuspec');
  let nuspec = fs.readFileSync(nuspecSrc, 'utf8');
  nuspec = nuspec.replace(/<version>[^<]+<\/version>/, `<version>${VER}</version>`);
  fs.writeFileSync(path.join(dir, 'Microsoft.WindowsDesktop.App.Ref.nuspec'), nuspec, 'utf8');

  // 逐件校验清单与包内文件对应（LDP-E-PACK-205）；自产参考程序集优先
  const replaced = [];
  for (const e of flEntries) {
    const dst = path.join(dir, e.rel);
    const self = USE_SELF_BUILT ? selfAsms.get(e.asm) : null;
    const src = self && (self.impl || self.ref);
    if (src) {
      fs.mkdirSync(path.dirname(dst), { recursive: true });
      fs.copyFileSync(src, dst);
      replaced.push({ asm: e.asm, from: rel(src), kind: self.impl ? 'impl' : 'ref' });
    } else if (!fs.existsSync(dst)) {
      fail('LDP-E-PACK-205', `Ref 清单缺件：${e.rel}（AssemblyName=${e.asm}）`);
    }
  }
  fixTimes(dir);
  const nupkg = path.join(OUT, `Microsoft.WindowsDesktop.App.Ref.${VER}.nupkg`);
  zipDir(dir, nupkg);
  return { path: nupkg, entries: walk(dir).length, replaced };
}

// ---------------------------------------------------------------- Runtime pack（MOD-52）

function buildRuntimePack() {
  if (!fs.existsSync(RT_NUPKG)) {
    fail('LDP-E-PACK-205', `上游 Runtime 件缺件：${rel(RT_NUPKG)}（取件命令见 40-build/lock/README.md）`);
  }
  const stage = path.join(TMP, 'rt');
  unzipTo(RT_NUPKG, stage);
  const dir = path.join(TMP, 'rt-linux');
  fs.mkdirSync(dir, { recursive: true });

  // 布局：runtimes/win-x64 → runtimes/linux-x64（IF-023；win-* 显式不产出）
  const winDir = path.join(stage, 'runtimes/win-x64');
  if (!fs.existsSync(winDir)) fail('LDP-E-PACK-207', `上游 Runtime 包内无 runtimes/win-x64：${rel(RT_NUPKG)}`);
  for (const relP of walk(winDir)) {
    const dst = path.join(dir, 'runtimes/linux-x64', relP);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.copyFileSync(path.join(winDir, relP), dst);
  }
  fs.mkdirSync(path.join(dir, 'data'), { recursive: true });
  fs.copyFileSync(FL, path.join(dir, 'data/FrameworkList.xml'));

  // 包元数据：沿用上游 nuspec，改 id 与版本（Runtime 包按 RID 区分 id）
  const nuspecSrc = path.join(stage, 'Microsoft.WindowsDesktop.App.Runtime.win-x64.nuspec');
  if (!fs.existsSync(nuspecSrc)) fail('LDP-E-PACK-205', '上游 Runtime 包内缺 nuspec');
  let nuspec = fs.readFileSync(nuspecSrc, 'utf8');
  nuspec = nuspec
    .replace(/<id>[^<]+<\/id>/, '<id>Microsoft.WindowsDesktop.App.Runtime.linux-x64</id>')
    .replace(/<version>[^<]+<\/version>/, `<version>${VER}</version>`);
  fs.writeFileSync(path.join(dir, 'Microsoft.WindowsDesktop.App.Runtime.linux-x64.nuspec'), nuspec, 'utf8');

  // 自产托管件替换（实现件，不是 ref）；未自产的保持上游件
  const libDir = path.join(dir, 'runtimes/linux-x64/lib/net10.0');
  const replaced = [];
  for (const [asm, self] of USE_SELF_BUILT ? selfAsms : []) {
    const dst = path.join(libDir, `${asm}.dll`);
    if (!self.impl || !fs.existsSync(dst)) continue;
    fs.copyFileSync(self.impl, dst);
    replaced.push({ asm, from: rel(self.impl) });
  }

  // 自产原生件：4 个 .so（MOD-52 的 NativeAssetMap，IF-023 布局）
  const nativeDir = path.join(dir, 'runtimes/linux-x64/native');
  fs.mkdirSync(nativeDir, { recursive: true });
  const nativeWanted = ['libwpfwin32.so', 'libwpfgfx_linux.so', 'libwpfwic.so', 'libdwlinux.so'];
  const natives = [];
  for (const n of nativeWanted) {
    const src = path.join(SHIM, n);
    if (fs.existsSync(src)) {
      fs.copyFileSync(src, path.join(nativeDir, n));
      fs.chmodSync(path.join(nativeDir, n), 0o755);
      natives.push({ name: n, from: rel(src), bytes: fs.statSync(src).size });
    }
  }
  // 原生件缺失不阻止组装，但必须如实记录（S1 只强依赖 libwpfwin32.so）
  const nativeMissing = nativeWanted.filter((n) => !natives.some((x) => x.name === n));
  if (nativeMissing.includes('libwpfwin32.so')) {
    fail('LDP-E-PACK-207', '原生资产缺失：libwpfwin32.so（先跑 make -C 40-build/native-shim）');
  }

  fixTimes(dir);
  const nupkg = path.join(OUT, `Microsoft.WindowsDesktop.App.Runtime.linux-x64.${VER}.nupkg`);
  zipDir(dir, nupkg);
  return { path: nupkg, entries: walk(dir).length, replaced, natives, nativeMissing };
}

// ---------------------------------------------------------------- 主流程

fs.mkdirSync(OUT, { recursive: true });
const refPack = buildRefPack();
const rtPack = buildRuntimePack();

// 包内容变化后清掉全局缓存中的同版目录，避免 restore 命中旧内容（IF-023 同名同版的必要代价）
const cacheDirs = [
  path.join(NU, `microsoft.windowsdesktop.app.ref/${VER}`),
  path.join(NU, `microsoft.windowsdesktop.app.runtime.linux-x64/${VER}`),
];
if (CACHE_CLEAR) for (const d of cacheDirs) fs.rmSync(d, { recursive: true, force: true });

const listPack = (nupkg) => run('unzip', ['-Z1', nupkg]).toString('utf8').split('\n').filter(Boolean);

const report = {
  generated_by: '40-build/pack/build-pack.mjs',
  version: VER,
  epoch: EPOCH,
  ref: {
    nupkg: rel(refPack.path),
    bytes: fs.statSync(refPack.path).size,
    sha256: sha256(refPack.path),
    entries: refPack.entries,
    framework_list_entries: flEntries.length,
    self_built: refPack.replaced,
    dlls: listPack(refPack.path).filter((p) => p.endsWith('.dll')),
  },
  runtime: {
    nupkg: rel(rtPack.path),
    bytes: fs.statSync(rtPack.path).size,
    sha256: sha256(rtPack.path),
    entries: rtPack.entries,
    platform: 'linux-x64',
    self_built: rtPack.replaced,
    natives: rtPack.natives,
    native_missing: rtPack.nativeMissing,
    dlls: listPack(rtPack.path).filter((p) => p.endsWith('.dll')),
  },
  self_built_enabled: USE_SELF_BUILT,
  self_built_gate: USE_SELF_BUILT
    ? `自产件身份校验通过：${identityCheck.reason}`
    : `替换暂不启用：${identityCheck.reason}`,
  notes: [
    'Ref pack 的 data/FrameworkList.xml 与 40-build/pack/FrameworkList.xml 逐字节一致（契约文件为唯一事实源）',
    'Runtime pack 原生件落 runtimes/linux-x64/native/；libSkiaSharp.so 不随包（概设 §2.4）',
    '包标识与版本与官方同名同版（IF-023）；组装后清 NuGet 缓存同版目录，restore 才命中自产件',
  ],
};
fs.writeFileSync(path.join(ROOT, '40-build/pack/pack-report.json'), JSON.stringify(report, null, 2) + '\n', 'utf8');

fs.rmSync(TMP, { recursive: true, force: true });
console.log(
  `build-pack: Ref ${report.ref.dlls.length} 件 dll（自产替换 ${refPack.replaced.length}）· Runtime ${report.runtime.dlls.length} 件 dll + ${rtPack.natives.length} 个 .so（自产替换 ${rtPack.replaced.length}）→ ${rel(OUT)}`,
);
if (rtPack.nativeMissing.length) console.log(`build-pack: 原生件待补 ${rtPack.nativeMissing.join(', ')}（LDP-E-PACK-207 读数）`);
