#!/usr/bin/env node
// shim 修复链（C 链）自动化：`libwpfwin32.so` 构建 → 备份 → 替换共享框架 → 小写别名 → 可回滚
//
// 为什么独立成一个脚本、不接进 `apply.mjs`：
//   与 B 链（`font-chain.mjs`）同理 —— 这里替换的是**全局共享框架**里的
//   `~/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/<ver>/libwpfwin32.so`，
//   一改影响所有用这套运行时的进程，必须**显式**执行。
//
// 为什么还要建两个小写别名：
//   K-1437 的两条硬教训：① 入口名要导出**无后缀名**（`PathRelativePathTo`）；
//   ② CoreCLR 的 native 装载按 **小写** 文件名探测（`shlwapi.dll` → `shlwapi.dll.so` / `libshlwapi.dll.so`），
//   而框架目录里原本只有大写 `SHLWAPI.dll`。不建这两个链接 ⇒
//   `DllNotFoundException: shlwapi.dll` → watcher 事件被丢 → UI 不自动刷新（K-1436 现象）。
//
// 职责（六件事）：
//   · 构建：`bash 40-build/native-shim/build-shim.sh`（产物 `40-build/native-shim/bin/libwpfwin32.so`）
//   · 备份：替换前把当前共享框架 `.so` `cp -a` 到 `~/.local/share/ldp/shimchain-bak/<时间戳>/`
//   · 替换：把产物 `cp` 过去，打印前后 sha256 与大小
//   · 别名：建/修 `shlwapi.dll.so`、`libshlwapi.dll.so` 两个小写符号链接（幂等）
//   · 回滚：`--rollback` 恢复最近备份（或指定文件），并移除自己建的别名链接
//   · 状态：`--status` 打印 hash/大小/链接状态
//
// 用法：
//   node 40-build/appliers/shim-chain.mjs                    # 构建 + 备份 + 替换 + 别名（幂等）
//   node 40-build/appliers/shim-chain.mjs --check            # 只检查（不构建、不写盘）
//   node 40-build/appliers/shim-chain.mjs --no-build         # 跳过构建，用现有产物
//   node 40-build/appliers/shim-chain.mjs --status           # 打印当前共享框架/产物/别名/备份
//   node 40-build/appliers/shim-chain.mjs --rollback         # 恢复最近备份 + 移除别名链接
//   node 40-build/appliers/shim-chain.mjs --rollback <bak.so>        # 恢复指定备份
//   node 40-build/appliers/shim-chain.mjs --rollback --keep-aliases  # 恢复但保留别名链接
//
// 环境：
//   DOTNET_ROOT      默认 ~/.local/share/ldp/dotnet
//   FXVER            默认自动探测 shared/Microsoft.WindowsDesktop.App 下最新版本目录
//   SC_KNOWN_SHA256  已知「K-1437 已生效」的产物哈希（不符只警告，不中止——源码可能确实变了）
//
// 纪律：
//   · 替换前必须备份；备份校验失败即中止（不允许无备份替换）；
//   · 任一步失败透传非 0 退出码；
//   · 幂等：当前共享框架已是产物哈希 ⇒ 跳过替换、不产生新备份，但仍补齐别名链接。
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
const KEEP_ALIASES = argv.includes('--keep-aliases');
const rollbackIdx = argv.indexOf('--rollback');
const ROLLBACK = rollbackIdx >= 0;
const ROLLBACK_TARGET = ROLLBACK && argv[rollbackIdx + 1] && !argv[rollbackIdx + 1].startsWith('--')
  ? argv[rollbackIdx + 1]
  : null;

const SoName = 'libwpfwin32.so';
const ALIASES = ['shlwapi.dll.so', 'libshlwapi.dll.so'];

const KNOWN_SHA256 = process.env.SC_KNOWN_SHA256
  || 'bfcd63ea504bc70eb21165377f648b7fed6cb900723765138da32ac11dc744d7';

const DOTNET_ROOT = process.env.DOTNET_ROOT || path.join(os.homedir(), '.local/share/ldp/dotnet');
const ARTIFACT = path.join(ROOT, '40-build/native-shim/bin', SoName);
const BAK_ROOT = path.join(os.homedir(), '.local/share/ldp/shimchain-bak');

function sha256(file) {
  const h = crypto.createHash('sha256');
  h.update(fs.readFileSync(file));
  return h.digest('hex');
}

function statOf(file) {
  const st = fs.lstatSync(file);
  return `${st.size} B / ${st.isSymbolicLink() ? `link→${fs.readlinkSync(file)}` : sha256(file).slice(0, 16) + '…'} / mtime ${st.mtime.toISOString()}`;
}

function resolveFxDir() {
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
  const dir = path.join(base, ver);
  return fs.existsSync(dir) ? dir : null;
}

function aliasState(fxDir) {
  return ALIASES.map((name) => {
    const p = path.join(fxDir, name);
    if (!fs.existsSync(p) && !isLink(p)) return { name, p, state: 'missing', target: null };
    if (isLink(p)) {
      const t = fs.readlinkSync(p);
      return { name, p, state: t === SoName ? 'ok' : 'wrong', target: t };
    }
    return { name, p, state: 'regular', target: null };
  });
}

function isLink(p) {
  try {
    return fs.lstatSync(p).isSymbolicLink();
  } catch {
    return false;
  }
}

// 幂等：已是指向 SoName 的正确链接就跳过；错误/普通文件则先删后建
function ensureAliases(fxDir) {
  let created = 0;
  let fixed = 0;
  for (const a of aliasState(fxDir)) {
    if (a.state === 'ok') continue;
    if (a.state !== 'missing') {
      fs.unlinkSync(a.p);
      fixed += 1;
    } else {
      created += 1;
    }
    fs.symlinkSync(SoName, a.p);
  }
  return { created, fixed };
}

function removeAliases(fxDir) {
  let removed = 0;
  for (const a of aliasState(fxDir)) {
    if (a.state === 'ok' || a.state === 'wrong') {
      fs.unlinkSync(a.p);
      removed += 1;
    }
  }
  return removed;
}

function newestBackup() {
  if (!fs.existsSync(BAK_ROOT)) return null;
  const dirs = fs.readdirSync(BAK_ROOT, { withFileTypes: true })
    .filter((e) => e.isDirectory())
    .map((e) => e.name)
    .sort()
    .reverse();
  for (const d of dirs) {
    const f = path.join(BAK_ROOT, d, SoName);
    if (fs.existsSync(f)) return f;
  }
  return null;
}

function main() {
  const fxDir = resolveFxDir();
  if (!fxDir) {
    console.error(`[sc] 找不到共享框架目录：${DOTNET_ROOT}/shared/Microsoft.WindowsDesktop.App/<ver>/`);
    process.exit(2);
  }
  const shared = path.join(fxDir, SoName);

  if (STATUS) {
    console.log(`[sc] 共享框架 ${fxDir}`);
    console.log(fs.existsSync(shared) ? `[sc]   ${SoName} ${statOf(shared)}` : `[sc]   ${SoName} 缺失`);
    console.log(`[sc] 产物 ${ARTIFACT}`);
    console.log(fs.existsSync(ARTIFACT) ? `[sc]   ${statOf(ARTIFACT)}` : '[sc]   产物缺失（需先构建）');
    for (const a of aliasState(fxDir)) {
      console.log(`[sc]   别名 ${a.name} ${a.state}${a.target ? ` → ${a.target}` : ''}`);
    }
    console.log(`[sc] 已知哈希 ${KNOWN_SHA256}`);
    console.log(`[sc] 备份目录 ${BAK_ROOT}${newestBackup() ? `（最近 ${newestBackup()}）` : '（无备份）'}`);
    if (fs.existsSync(shared) && !isLink(shared)) {
      const cur = sha256(shared);
      console.log(`[sc] 状态 ${cur === KNOWN_SHA256 ? '已替换（当前=已知产物哈希）' : '未替换/已改动'}`);
    }
    return;
  }

  if (ROLLBACK) {
    const bak = ROLLBACK_TARGET || newestBackup();
    if (!bak || !fs.existsSync(bak)) {
      console.error(`[sc] 没有可用备份${ROLLBACK_TARGET ? `：${ROLLBACK_TARGET}` : `（${BAK_ROOT} 为空）`}`);
      process.exit(3);
    }
    const before = sha256(shared);
    fs.copyFileSync(bak, shared);
    const after = sha256(shared);
    console.log(`[sc] 已回滚：${bak}`);
    console.log(`[sc]   before ${before}`);
    console.log(`[sc]   after  ${after}`);
    if (!KEEP_ALIASES) {
      const removed = removeAliases(fxDir);
      console.log(`[sc] 已移除别名链接 ${removed} 个（若回滚后仍要文件监控，请再跑一次本脚本；或加 --keep-aliases）`);
    }
    return;
  }

  if (!fs.existsSync(shared)) {
    console.error(`[sc] 目标不存在：${shared}`);
    process.exit(2);
  }
  const cur = sha256(shared);
  const haveArtifact = fs.existsSync(ARTIFACT);

  if (CHECK) {
    console.log('[sc] --check（不构建、不写盘）');
    console.log(`[sc] 当前共享框架 ${cur}`);
    console.log(haveArtifact ? `[sc] 产物 ${sha256(ARTIFACT)}` : '[sc] 产物缺失 ⇒ 正常模式会先构建');
    const problems = [];
    if (!haveArtifact) problems.push('产物缺失（需 bash 40-build/native-shim/build-shim.sh）');
    if (haveArtifact && sha256(ARTIFACT) !== KNOWN_SHA256) {
      problems.push(`产物哈希与已知 ${KNOWN_SHA256.slice(0, 12)}… 不同（源码可能已变更，需人工确认）`);
    }
    const badAliases = aliasState(fxDir).filter((a) => a.state !== 'ok');
    if (badAliases.length) problems.push(`别名不完整/错误：${badAliases.map((a) => `${a.name}=${a.state}`).join(', ')}`);
    if (haveArtifact && cur === sha256(ARTIFACT) && !badAliases.length) {
      console.log('[sc] 幂等：当前已是产物哈希且别名正确，正常模式会跳过替换');
    }
    if (problems.length) {
      for (const p of problems) console.log(`[sc] WARN ${p}`);
      process.exit(haveArtifact ? 0 : 1);
    }
    console.log('[sc] OK');
    return;
  }

  if (!NO_BUILD) {
    console.log('[sc] 构建：bash 40-build/native-shim/build-shim.sh');
    const r = spawnSync('bash', ['40-build/native-shim/build-shim.sh'], { cwd: ROOT, stdio: 'inherit' });
    if (r.status !== 0) {
      console.error(`[sc] 构建失败 rc=${r.status}`);
      process.exit(r.status || 1);
    }
  } else {
    console.log('[sc] --no-build：跳过构建');
  }

  if (!fs.existsSync(ARTIFACT)) {
    console.error(`[sc] 产物不存在：${ARTIFACT}`);
    process.exit(4);
  }
  const artSha = sha256(ARTIFACT);
  if (artSha !== KNOWN_SHA256) {
    console.log(`[sc] WARN 产物哈希 ${artSha} 与已知 ${KNOWN_SHA256} 不同（源码已变更？继续但请人工确认）`);
  }

  if (cur === artSha) {
    console.log('[sc] 幂等：共享框架已是产物哈希，跳过替换');
    console.log(`[sc]   ${shared} = ${cur}`);
  } else {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const bakDir = path.join(BAK_ROOT, stamp);
    fs.mkdirSync(bakDir, { recursive: true });
    const bakFile = path.join(bakDir, SoName);
    fs.copyFileSync(shared, bakFile);
    if (sha256(bakFile) !== cur) {
      console.error('[sc] 备份校验失败，中止（不允许无备份替换）');
      process.exit(5);
    }
    console.log(`[sc] 已备份：${bakFile}（${cur}）`);

    fs.copyFileSync(ARTIFACT, shared);
    const after = sha256(shared);
    if (after !== artSha) {
      console.error('[sc] 替换后哈希不一致，中止（请用 --rollback 恢复）');
      process.exit(6);
    }
    console.log(`[sc] 已替换：${shared}`);
    console.log(`[sc]   before ${cur}`);
    console.log(`[sc]   after  ${after}`);
  }

  // 别名始终补齐（幂等），因为「替换成功但忘建链接」是最容易复发的一步
  const { created, fixed } = ensureAliases(fxDir);
  for (const a of aliasState(fxDir)) {
    console.log(`[sc]   别名 ${a.name} ${a.state}${a.target ? ` → ${a.target}` : ''}`);
  }
  console.log(`[sc] 别名处理：新建 ${created} 个 / 修正 ${fixed} 个`);
  console.log('[sc] 提示：如需回滚 —— node 40-build/appliers/shim-chain.mjs --rollback');
}

main();
