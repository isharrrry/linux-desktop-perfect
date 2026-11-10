#!/usr/bin/env node
// 字体修复链（B 链）自动化：`PresentationCore.dll` 构建 → 备份 → 替换共享框架 → 可回滚
//
// 为什么独立成一个脚本、不接进 `apply.mjs`：
//   `apply.mjs` 只改派生树文本与 `$APP` 下的二进制（K-1434 的 A 链），而这里要替换的是
//   **全局共享框架** `~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/<ver>/PresentationCore.dll`
//   —— 一改就影响所有用这套运行时的进程，所以必须**显式**执行，不能藏在普通 apply 里静默替换。
//
// 职责（四件事）：
//   · 构建：`node 40-build/wpf-port/build-port.mjs --only PresentationCore`（K-1425 已正式化 3 处适配）
//   · 备份：替换前把当前共享框架 DLL `cp -a` 到 `~/.local/share/ldp/fontchain-bak/<时间戳>/`
//   · 替换：把产物 `cp` 过去，打印前后 sha256 与大小
//   · 回滚：`--rollback` 恢复最近备份（或 `--rollback <文件>` 指定备份）
//
// 用法：
//   node 40-build/appliers/font-chain.mjs                # 构建 + 备份 + 替换（幂等：已是产物哈希则跳过替换）
//   node 40-build/appliers/font-chain.mjs --check        # 只检查（不构建、不写盘）
//   node 40-build/appliers/font-chain.mjs --no-build     # 跳过构建，用现有产物替换
//   node 40-build/appliers/font-chain.mjs --status       # 打印「当前共享框架 / 产物 / 已知哈希」
//   node 40-build/appliers/font-chain.mjs --rollback     # 恢复最近备份
//   node 40-build/appliers/font-chain.mjs --rollback <bak.dll>   # 恢复指定备份
//
// 环境：
//   DOTNET_ROOT  默认 ~/.local/share/ldp/dotnet
//   FXVER        默认自动探测 shared/Microsoft.WindowsDesktop.App 下最新版本目录
//   FC_KNOWN_SHA256  已知「适配已生效」的产物哈希（不符只警告，不中止——源码可能确实变了）
//
// 纪律：
//   · 替换前必须备份；备份失败即中止（不允许无备份替换）；
//   · 任一步失败透传非 0 退出码；
//   · 幂等：当前共享框架已是产物哈希 ⇒ 跳过替换、不产生新备份。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');

const argv = process.argv.slice(2);
const CHECK = argv.includes('--check');
const NO_BUILD = argv.includes('--no-build');
const STATUS = argv.includes('--status');
const rollbackIdx = argv.indexOf('--rollback');
const ROLLBACK = rollbackIdx >= 0;
const ROLLBACK_TARGET = ROLLBACK && argv[rollbackIdx + 1] && !argv[rollbackIdx + 1].startsWith('--')
  ? argv[rollbackIdx + 1]
  : null;

const KNOWN_SHA256 = process.env.FC_KNOWN_SHA256
  || 'a6a32d4c912e3b66a9d561e3466992765c141e7fd08cf5e539a92d0ed8221fb7';

const DOTNET_ROOT = process.env.DOTNET_ROOT || path.join(os.homedir(), '.local/share/ldp/dotnet');
const ARTIFACT = path.join(
  ROOT,
  '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/Release/PresentationCore.dll'
);
const BAK_ROOT = path.join(os.homedir(), '.local/share/ldp/fontchain-bak');

function sha256(file) {
  const h = crypto.createHash('sha256');
  h.update(fs.readFileSync(file));
  return h.digest('hex');
}

function statOf(file) {
  const st = fs.statSync(file);
  return `${st.size} B / ${sha256(file).slice(0, 16)}… / mtime ${st.mtime.toISOString()}`;
}

function resolveSharedDll() {
  const base = path.join(DOTNET_ROOT, 'shared/Microsoft.WindowsDesktop.App');
  if (!fs.existsSync(base)) return null;
  let ver = process.env.FXVER;
  if (!ver) {
    const dirs = fs.readdirSync(base, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => e.name)
      .sort((a, b) => {
        const pa = a.split('.').map((n) => parseInt(n, 10) || 0);
        const pb = b.split('.').map((n) => parseInt(n, 10) || 0);
        for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
          const d = (pb[i] || 0) - (pa[i] || 0);
          if (d) return d;
        }
        return 0;
      });
    ver = dirs[0];
  }
  if (!ver) return null;
  return path.join(base, ver, 'PresentationCore.dll');
}

function newestBackup() {
  if (!fs.existsSync(BAK_ROOT)) return null;
  const dirs = fs.readdirSync(BAK_ROOT, { withFileTypes: true })
    .filter((e) => e.isDirectory())
    .map((e) => e.name)
    .sort()
    .reverse();
  for (const d of dirs) {
    const f = path.join(BAK_ROOT, d, 'PresentationCore.dll');
    if (fs.existsSync(f)) return f;
  }
  return null;
}

function main() {
  const shared = resolveSharedDll();
  if (!shared) {
    console.error(`[fc] 找不到共享框架：${DOTNET_ROOT}/shared/Microsoft.WindowsDesktop.App/<ver>/PresentationCore.dll`);
    process.exit(2);
  }
  if (!fs.existsSync(shared)) {
    console.error(`[fc] 目标不存在：${shared}`);
    process.exit(2);
  }

  if (STATUS) {
    console.log(`[fc] 共享框架 ${shared}`);
    console.log(`[fc]   当前 ${statOf(shared)}`);
    console.log(`[fc] 产物 ${ARTIFACT}`);
    console.log(fs.existsSync(ARTIFACT) ? `[fc]   产物 ${statOf(ARTIFACT)}` : '[fc]   产物缺失（需先构建）');
    console.log(`[fc] 已知哈希 ${KNOWN_SHA256}`);
    console.log(`[fc] 备份目录 ${BAK_ROOT}${newestBackup() ? `（最近 ${newestBackup()}）` : '（无备份）'}`);
    const cur = sha256(shared);
    console.log(`[fc] 状态 ${cur === KNOWN_SHA256 ? '已替换（当前=已知产物哈希）' : '未替换/已改动'}`);
    return;
  }

  if (ROLLBACK) {
    const bak = ROLLBACK_TARGET || newestBackup();
    if (!bak || !fs.existsSync(bak)) {
      console.error(`[fc] 没有可用备份${ROLLBACK_TARGET ? `：${ROLLBACK_TARGET}` : `（${BAK_ROOT} 为空）`}`);
      process.exit(3);
    }
    const before = sha256(shared);
    fs.copyFileSync(bak, shared);
    const after = sha256(shared);
    console.log(`[fc] 已回滚：${bak}`);
    console.log(`[fc]   before ${before}`);
    console.log(`[fc]   after  ${after}`);
    if (CHECK) process.exit(0);
    return;
  }

  const cur = sha256(shared);
  const haveArtifact = fs.existsSync(ARTIFACT);

  if (CHECK) {
    console.log('[fc] --check（不构建、不写盘）');
    console.log(`[fc] 当前共享框架 ${cur}`);
    console.log(haveArtifact ? `[fc] 产物 ${sha256(ARTIFACT)}` : '[fc] 产物缺失 ⇒ 正常模式会先构建');
    const problems = [];
    if (!haveArtifact) problems.push('产物缺失（需 node 40-build/wpf-port/build-port.mjs --only PresentationCore）');
    if (haveArtifact && cur === sha256(ARTIFACT)) console.log('[fc] 幂等：当前已是产物哈希，正常模式会跳过替换');
    if (haveArtifact && sha256(ARTIFACT) !== KNOWN_SHA256) {
      problems.push(`产物哈希与已知 ${KNOWN_SHA256.slice(0, 12)}… 不同（源码可能已变更，需人工确认）`);
    }
    if (problems.length) {
      for (const p of problems) console.log(`[fc] WARN ${p}`);
      // 已知哈希不符属「需人工确认」，不判失败；产物缺失则判失败
      process.exit(haveArtifact ? 0 : 1);
    }
    console.log('[fc] OK');
    return;
  }

  if (!NO_BUILD) {
    console.log('[fc] 构建：node 40-build/wpf-port/build-port.mjs --only PresentationCore');
    const r = spawnSync('node', ['40-build/wpf-port/build-port.mjs', '--only', 'PresentationCore'], {
      cwd: ROOT,
      stdio: 'inherit',
    });
    if (r.status !== 0) {
      console.error(`[fc] 构建失败 rc=${r.status}`);
      process.exit(r.status || 1);
    }
  } else {
    console.log('[fc] --no-build：跳过构建');
  }

  if (!fs.existsSync(ARTIFACT)) {
    console.error(`[fc] 产物不存在：${ARTIFACT}`);
    process.exit(4);
  }
  const artSha = sha256(ARTIFACT);
  if (artSha !== KNOWN_SHA256) {
    console.log(`[fc] WARN 产物哈希 ${artSha} 与已知 ${KNOWN_SHA256} 不同（源码已变更？继续但请人工确认）`);
  }

  if (cur === artSha) {
    console.log('[fc] 幂等：共享框架已是产物哈希，跳过替换');
    console.log(`[fc]   ${shared} = ${cur}`);
    return;
  }

  // 备份（替换前必须成功）
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const bakDir = path.join(BAK_ROOT, stamp);
  fs.mkdirSync(bakDir, { recursive: true });
  const bakFile = path.join(bakDir, 'PresentationCore.dll');
  fs.copyFileSync(shared, bakFile);
  if (sha256(bakFile) !== cur) {
    console.error('[fc] 备份校验失败，中止（不允许无备份替换）');
    process.exit(5);
  }
  console.log(`[fc] 已备份：${bakFile}（${cur}）`);

  fs.copyFileSync(ARTIFACT, shared);
  const after = sha256(shared);
  if (after !== artSha) {
    console.error('[fc] 替换后哈希不一致，中止（请用 --rollback 恢复）');
    process.exit(6);
  }
  console.log(`[fc] 已替换：${shared}`);
  console.log(`[fc]   before ${cur}`);
  console.log(`[fc]   after  ${after}`);
  console.log('[fc] 提示：如需回滚 —— node 40-build/appliers/font-chain.mjs --rollback');
}

main();
