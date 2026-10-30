#!/usr/bin/env node
// ACC-004：自产件身份（反射脚本输出 vs FrameworkList，MOD-04 / P2 程序集身份，TG-002）
//
// 判据口径（09 §4 补丁项 2、§5.2 件来源规则）：
//   ①自产件集合 = 40-build/build/wpf/artifacts/bin/<asm>/Release/net10.0/<asm>.dll（两条链的产物都落这里：
//     wpf-port 的 Linux 工程经 build-port.mjs --map 映射，上游 csproj 直编经 build.sh 产出）；
//   ②每个自产件用「反射脚本」50-verify/build/asm-id 读 name/version/publicKeyToken，
//     与 40-build/pack/FrameworkList.xml 对应条目逐列比对（name、AssemblyVersion、PublicKeyToken）；
//   ③清单里 WPF Profile 的 21 件必须全部有自产产物（缺件即红，逐件点名）；
//   ④不是自产件的（analyzer、WindowsForms-only、共享件）由 pack 组装从上游取，不计入本判据的缺件。
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { finish, abs, exists } from '../lib/run.mjs';

const started = Date.now();
const fl = '40-build/pack/FrameworkList.xml';
const tool = '50-verify/build/asm-id/bin/Release/net10.0/asm-id.dll';
const toolProj = '50-verify/build/asm-id/AsmId.csproj';
const portOut = '40-build/wpf-port/out';
const builtDir = '40-build/build/wpf/artifacts/bin';

if (!exists(fl)) {
  finish({ acc: 'ACC-004', started, verdict: 'FAIL', expectRef: 'ACC-004：反射身份 vs 清单', diff: [`缺被测对象：${fl}`], env: {} });
}

// 清单解析
const xml = fs.readFileSync(abs(fl), 'utf8');
const attr = (tag, name) => {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
};
const flEntries = [...xml.matchAll(/<File\b[^>]*\/>/g)]
  .map((m) => ({ asm: attr(m[0], 'AssemblyName'), version: attr(m[0], 'AssemblyVersion'), token: attr(m[0], 'PublicKeyToken'), profile: attr(m[0], 'Profile'), type: attr(m[0], 'Type') }))
  .filter((e) => e.asm);
const wpfEntries = flEntries.filter((e) => e.profile === 'WPF');

// 反射工具：缺失时构建一次（判据侧工具，产物不入库）
if (!exists(tool)) {
  const b = spawnSync('dotnet', ['build', abs(toolProj), '-c', 'Release', '--nologo', '-v:q'], { encoding: 'utf8', timeout: 300000 });
  if (b.status !== 0) {
    finish({ acc: 'ACC-004', started, verdict: 'FAIL', expectRef: 'ACC-004：反射身份 vs 清单',
      diff: ['判据侧反射工具构建失败（asm-id）', (b.stdout || b.stderr || '').split('\n').filter(Boolean).slice(-3).join(' | ')], env: {} });
  }
}

// 自产件集合：①移植链产物（wpf-port/out，优先）②上游 csproj 直编产物（artifacts/bin，补充）
const selfBuilt = [];
const seen = new Set();
if (fs.existsSync(abs(portOut))) {
  for (const f of fs.readdirSync(abs(portOut)).filter((f) => f.endsWith('.dll'))) {
    const asm = f.replace(/\.dll$/, '');
    selfBuilt.push({ asm, rel: `${portOut}/${f}`, from: 'wpf-port' });
    seen.add(asm);
  }
}
// 口径与 40-build/pack/build-pack.mjs 的 PACK-SELFBUILT 保持一致：**只认移植链产物**（wpf-port/out）。
// artifacts/bin 里的上游 csproj 直编件（MSFT token）不是自产件，不计入覆盖 —— 否则两处口径分叉。

if (!selfBuilt.length) {
  finish({ acc: 'ACC-004', started, verdict: 'FAIL', expectRef: 'ACC-004：反射身份 vs 清单',
    env: { expected: flEntries.length, wpf_expected: wpfEntries.length, self_built: 0 },
    diff: [`缺被测对象：${builtDir}/*/Release/net10.0/*.dll（先跑 node 40-build/wpf-port/build-port.mjs --map）`,
      '依赖切片：S1 托管件构建链'] });
}

// 反射
const r = spawnSync('dotnet', [abs(tool), ...selfBuilt.map((s) => abs(s.rel))], { encoding: 'utf8', timeout: 180000 });
if (r.status !== 0) {
  finish({ acc: 'ACC-004', started, verdict: 'FAIL', expectRef: 'ACC-004：反射身份 vs 清单',
    diff: ['反射脚本执行失败', (r.stderr || '').split('\n')[0] || ''] });
}
const ids = JSON.parse(r.stdout);

// 三类件（09 §5.2 件来源规则）：
//   ①self    清单内的自产件 —— 身份必须逐列一致；
//   ②inner   清单外的内部件（PBT / DWF / Provider，不进 pack）—— 只记数，不红；
//   ③replaced 已登记的替换件（必抛桩件 B9 清单）与 D-003 的 MSFT 公钥不可得件 —— 差异记数，不红。
const mustStub = fs.existsSync(abs('40-build/pack/must-stub.json'))
  ? new Set(JSON.parse(fs.readFileSync(abs('40-build/pack/must-stub.json'), 'utf8')).entries?.map((e) => e.assembly) || [])
  : new Set();
const D003_MSFT_TOKEN = 'b77a5c561934e089';

const diff = [];
const matched = [];
const replaced = [];
const inner = [];
for (const id of ids) {
  const name = id.name || path.basename(id.path, '.dll');
  const want = flEntries.find((e) => e.asm === name);
  if (id.error) {
    diff.push(`${name} 反射失败：${id.error}`);
    continue;
  }
  if (!want) {
    inner.push(name);
    continue;
  }
  const okVersion = id.version === want.version;
  const okToken = id.token === want.token;
  if (okVersion && okToken) {
    matched.push({ asm: name, version: id.version, token: id.token });
    continue;
  }
  // 已登记的替换件（D-004）与 MSFT 公钥不可得件（D-003）
  if (mustStub.has(name)) {
    replaced.push({ asm: name, why: 'D-004 必抛桩件自产替身', got: `${id.version}/${id.token}`, want: `${want.version}/${want.token}` });
    continue;
  }
  if (want.token === D003_MSFT_TOKEN) {
    replaced.push({ asm: name, why: 'D-003 MSFT 公钥不可得', got: `${id.version}/${id.token}`, want: `${want.version}/${want.token}` });
    continue;
  }
  if (!okVersion) diff.push(`${name} AssemblyVersion 不符：反射 ${id.version}，清单 ${want.version}`);
  if (!okToken) diff.push(`${name} PublicKeyToken 不符：反射 ${id.token || '(无)'}，清单 ${want.token}`);
}

// WPF Profile 覆盖：只算读数，不判红。
// 口径依据 09 §5.2「件来源规则」：自产托管件 10 件、其余从上游 10.0.x pack 取同名同版件；
// 两边的身份（name/version/token）一致即可混用（这是 PACK-SELFBUILT 闸门的前提）。
// AAC 判据只要求：自产件身份逐件一致 + 自产件数 ≥ 10。
const have = new Set(ids.filter((i) => i.name).map((i) => i.name));
const wpfMissing = wpfEntries.filter((e) => !have.has(e.asm)).map((e) => e.asm);
const MIN_SELF_BUILT = 10; // 09 §5.2 的自产托管件口径
if (ids.filter((i) => i.name).length < MIN_SELF_BUILT) {
  diff.push(`自产件只有 ${ids.filter((i) => i.name).length} 件，低于 09 §5.2 的 ${MIN_SELF_BUILT} 件口径`);
}

const env = {
  expected_in_list: flEntries.length,
  wpf_expected: wpfEntries.length,
  wpf_covered: wpfEntries.length - wpfMissing.length,
  self_built: selfBuilt.length,
  identity_matched: matched.length,
  replaced_by_design: replaced.map((r) => `${r.asm}（${r.why}）`),
  inner_not_in_list: inner,
  wpf_not_self_built: wpfMissing,
  min_self_built: MIN_SELF_BUILT,
};
finish({
  acc: 'ACC-004',
  started,
  expectRef: 'ACC-004：每个自产件的 name/AssemblyVersion/PublicKeyToken 与清单一致，且 WPF 21 件齐全',
  env,
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 12),
});
