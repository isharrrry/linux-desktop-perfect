#!/usr/bin/env node
// ACC-003：pack 解包清单 diff（MOD-51/52，TG-002）
//
// 口径（2026-10-30 实测修正后固化，写进 08 §4 ACC-003 行）：
//   ①Ref pack：40-build/pack/FrameworkList.xml 的每条 Path 在包内存在；包内 dll 去掉本地化资源件
//     （`*.resources.dll`，不属清单口径）后，主程序集名集合与清单 AssemblyName 集合互不差；
//     data/FrameworkList.xml 与契约文件逐字节一致。
//   ②Runtime pack：runtimes/linux-x64/lib/net10.0/ 覆盖清单全部主程序集名；
//     runtimes/linux-x64/native/ 至少含 libwpfwin32.so（其余原生件缺件按 LDP-E-PACK-207 记账）。
// 任一不满足即 FAIL，不允许静默跳过。
import fs from 'node:fs';
import { finish, abs, exists, runCmd } from '../lib/run.mjs';

const started = Date.now();
const fl = '40-build/pack/FrameworkList.xml';
const outDir = '40-build/pack/out';

if (!exists(fl)) {
  finish({ acc: 'ACC-003', started, verdict: 'FAIL', expectRef: 'ACC-003：pack 解包清单 diff',
    diff: [`缺被测对象：${fl}（B3）`], env: {} });
}
const xml = fs.readFileSync(abs(fl), 'utf8');
const attr = (tag, name) => {
  const m = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return m ? m[1] : null;
};
const entries = [...xml.matchAll(/<File\b[^>]*\/>/g)]
  .map((m) => ({ asm: attr(m[0], 'AssemblyName'), rel: attr(m[0], 'Path'), type: attr(m[0], 'Type') }))
  .filter((e) => e.asm && e.rel);
// Analyzer 条目只进 Ref pack（编译期分析器），不进 Runtime pack（05 §3.7）
const managedOnly = entries.filter((e) => e.type !== 'Analyzer');
const flBytes = fs.readFileSync(abs(fl));

const packs = exists(outDir) ? fs.readdirSync(abs(outDir)).filter((f) => f.endsWith('.nupkg')) : [];
const refPack = packs.find((f) => /\.Ref\..*\.nupkg$/.test(f));
const rtPack = packs.find((f) => /\.Runtime\.linux-x64\..*\.nupkg$/.test(f));
const env = { framework_list_entries: entries.length, packs: packs.length, ref_pack: refPack || null, runtime_pack: rtPack || null };

if (!refPack) {
  finish({ acc: 'ACC-003', started, verdict: 'FAIL', expectRef: 'ACC-003：pack 解包清单 diff',
    env, diff: [`缺被测对象：${outDir}/Microsoft.WindowsDesktop.App.Ref.*.nupkg（MOD-51）`,
      `已具备：FrameworkList ${entries.length} 条`,
      '依赖切片：S1 pack 组装 → 先跑 node 40-build/pack/build-pack.mjs'] });
}

const listOf = (nupkg) => runCmd('unzip', ['-Z1', abs(`${outDir}/${nupkg}`)]);
const readOf = (nupkg, member) => {
  const r = runCmd('unzip', ['-p', abs(`${outDir}/${nupkg}`), member]);
  return r.rc === 0 ? r.out : null;
};

const diff = [];

// ① Ref pack
const refEntries = listOf(refPack).out.split('\n').filter(Boolean);
const refDlls = refEntries.filter((p) => p.endsWith('.dll') && !/\.resources\.dll$/.test(p));
const refNames = refDlls.map((p) => p.split('/').pop().replace(/\.dll$/, ''));
const missInPack = entries.filter((e) => !refEntries.includes(e.rel));
const missInList = refNames.filter((n) => !entries.some((e) => e.asm === n));
if (missInPack.length) diff.push(...missInPack.slice(0, 8).map((e) => `Ref 缺件：${e.rel}`));
if (missInList.length) diff.push(...missInList.slice(0, 8).map((n) => `Ref 多件（清单外主程序集）：${n}`));
const flInPack = readOf(refPack, 'data/FrameworkList.xml');
if (flInPack === null) diff.push('Ref 包内缺 data/FrameworkList.xml');
else if (!Buffer.from(flInPack, 'utf8').equals(flBytes)) diff.push('Ref 包内 FrameworkList.xml 与契约文件不一致');

// ② Runtime pack
const rtDlls = [];
const rtNatives = [];
if (!rtPack) {
  diff.push(`缺被测对象：${outDir}/Microsoft.WindowsDesktop.App.Runtime.linux-x64.*.nupkg（MOD-52）`);
} else {
  const rtEntries = listOf(rtPack).out.split('\n').filter(Boolean);
  rtDlls.push(...rtEntries.filter((p) => /^runtimes\/linux-x64\/lib\/net10\.0\/[^/]+\.dll$/.test(p)));
  rtNatives.push(...rtEntries.filter((p) => /^runtimes\/linux-x64\/native\/.+\.so$/.test(p)));
  const rtNames = new Set(rtDlls.map((p) => p.split('/').pop().replace(/\.dll$/, '')));
  const missInRt = managedOnly.filter((e) => !rtNames.has(e.asm));
  if (missInRt.length) diff.push(...missInRt.slice(0, 8).map((e) => `Runtime 缺托管件：${e.asm}`));
  if (!rtNatives.some((p) => p.endsWith('/libwpfwin32.so'))) {
    diff.push('Runtime 缺原生件：runtimes/linux-x64/native/libwpfwin32.so（LDP-E-PACK-207）');
  }
  const rtFl = readOf(rtPack, 'data/FrameworkList.xml');
  if (rtFl === null) diff.push('Runtime 包内缺 data/FrameworkList.xml');
  else if (!Buffer.from(rtFl, 'utf8').equals(flBytes)) diff.push('Runtime 包内 FrameworkList.xml 与契约文件不一致');
}

finish({
  acc: 'ACC-003',
  started,
  expectRef: 'ACC-003：Ref 包逐件命中清单、Runtime 包含全部托管件与 libwpfwin32.so',
  env: { ...env, ref_dlls: refDlls.length, runtime_dlls: rtDlls.length, runtime_natives: rtNatives.length },
  verdict: diff.length ? 'FAIL' : 'PASS',
  diff: diff.slice(0, 12),
});
