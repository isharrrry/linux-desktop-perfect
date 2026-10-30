#!/usr/bin/env node
// MOD-25 导出层构建入口：milcore（WpfGfx.Linux）→ NativeAOT 共享库（wpfgfx_cor3.so）
//
// 为什么需要它：托管层的 `DllImport("wpfgfx_cor3.dll")`（118 条 DUCE 命令 + 108 个入口）
// 需要 C ABI 符号。桥接层把 milcore 的 `[UnmanagedCallersOnly]` 入口导出成共享库；
// 托管侧由 DllImportResolver 把 `wpfgfx_cor3.dll` 映射到它（wpf-port 的 MilCoreDllImportResolver）。
//
// 用法：
//   node 40-build/milcore-bridge/build-bridge.mjs            # 构建 + AOT 发布
//   node 40-build/milcore-bridge/build-bridge.mjs --check    # 只检查产物是否已就绪
//   node 40-build/milcore-bridge/build-bridge.mjs --install <dotnetRoot>   # 装进用户目录框架根
//
// 读数：40-build/milcore-bridge/bridge-report.json（尺寸/sha256/导出符号数）

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const has = (f) => argv.includes(`--${f}`);
const val = (f, def) => {
  const i = argv.indexOf(`--${f}`);
  return i >= 0 && argv[i + 1] ? path.resolve(argv[i + 1].replace(/^~/, process.env.HOME || '')) : def;
};

const MILCORE = path.join(ROOT, '40-build/milcore/WpfGfx.Linux.csproj');
const BRIDGE = path.join(HERE, 'MilBridge.Linux.csproj');
const PUBLISH = path.join(HERE, 'bin/Release/net10.0/linux-x64/publish');
const ARTIFACT = path.join(PUBLISH, 'wpfgfx_cor3.so');
const REPORT = path.join(HERE, 'bridge-report.json');

const run = (cmd, args, opts = {}) => execFileSync(cmd, args, { stdio: ['ignore', 'pipe', 'pipe'], encoding: 'utf8', ...opts });
const sha256 = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

if (has('check')) {
  const problems = [];
  if (!fs.existsSync(ARTIFACT)) problems.push(`缺产物：${path.relative(ROOT, ARTIFACT)}`);
  if (!fs.existsSync(path.join(PUBLISH, 'libSkiaSharp.so'))) problems.push(`缺 libSkiaSharp.so（AOT 发布的运行时依赖）`);
  for (const p of problems) console.error(`ERROR ${p}`);
  console.log(`build-bridge --check: ${problems.length ? `问题 ${problems.length}` : '产物就绪'}`);
  process.exit(problems.length ? 1 : 0);
}

// 构建（milcore + 桥接层 + AOT 发布）。NativeAOT 需要 ILCompiler 包（首次会联网取）。
const t0 = Date.now();
let publishLog = '';
try {
  publishLog = run('dotnet', ['publish', BRIDGE, '-c', 'Release', '-r', 'linux-x64', '--nologo'], { timeout: 1800000 });
} catch (e) {
  const out = `${e.stdout || ''}${e.stderr || ''}`;
  const errs = out.split('\n').filter((l) => /error /.test(l)).slice(0, 6);
  for (const l of errs) console.error(`ERROR ${l.trim().slice(0, 240)}`);
  process.exit(1);
}
if (!fs.existsSync(ARTIFACT)) {
  console.error(`ERROR LDP-E-PACK-207 AOT 发布未产出 ${path.relative(ROOT, ARTIFACT)}`);
  process.exit(1);
}

// 导出符号（TG-001 对拍的输入：Mil* 入口）
const nm = run('nm', ['-D', '--defined-only', ARTIFACT]);
const exports = nm.split('\n').map((l) => l.trim().split(/\s+/).pop()).filter((s) => s && s.startsWith('Mil'));
const report = {
  generated_by: '40-build/milcore-bridge/build-bridge.mjs',
  artifact: path.relative(ROOT, ARTIFACT),
  bytes: fs.statSync(ARTIFACT).size,
  sha256: sha256(ARTIFACT),
  mil_export_count: exports.length,
  mil_exports: exports.sort(),
  skia_native: fs.existsSync(path.join(PUBLISH, 'libSkiaSharp.so'))
    ? { path: path.relative(ROOT, path.join(PUBLISH, 'libSkiaSharp.so')), bytes: fs.statSync(path.join(PUBLISH, 'libSkiaSharp.so')).size }
    : null,
  duration_ms: Date.now() - t0,
  notes: [
    'wpfgfx_cor3.so = milcore（WpfGfx.Linux）的 NativeAOT 共享库，导出 Mil* C ABI 入口（MOD-25）',
    '运行期由 wpf-port 的 MilCoreDllImportResolver 把 DllImport("wpfgfx_cor3.dll") 映射到本 .so',
    'libSkiaSharp.so 是 AOT 发布带出的运行时依赖，随框架根分发',
  ],
};
fs.writeFileSync(REPORT, JSON.stringify(report, null, 2) + '\n', 'utf8');

if (has('install')) {
  const root = val('install');
  const fxDirs = [];
  const shared = path.join(root, 'shared/Microsoft.WindowsDesktop.App');
  if (fs.existsSync(shared)) for (const v of fs.readdirSync(shared)) fxDirs.push(path.join(shared, v));
  if (!fxDirs.length) {
    console.error(`ERROR LDP-E-PACK-207 目标根下没有 Microsoft.WindowsDesktop.App：${root}`);
    process.exit(1);
  }
  for (const fx of fxDirs) {
    fs.copyFileSync(ARTIFACT, path.join(fx, 'wpfgfx_cor3.so'));
    // DllImport 探测列表含裸名 `wpfgfx_cor3.dll`，故同名再放一份（ELF 扩展名不参与加载判断）
    fs.copyFileSync(ARTIFACT, path.join(fx, 'wpfgfx_cor3.dll'));
    const skia = path.join(PUBLISH, 'libSkiaSharp.so');
    if (fs.existsSync(skia)) fs.copyFileSync(skia, path.join(fx, 'libSkiaSharp.so'));
  }
  console.log(`build-bridge: 已装入 ${fxDirs.length} 个框架目录（wpfgfx_cor3.so/.dll + libSkiaSharp.so）`);
}

console.log(`build-bridge: ${path.relative(ROOT, ARTIFACT)} ${report.bytes} B，Mil* 导出 ${exports.length} 个，耗时 ${report.duration_ms} ms`);
