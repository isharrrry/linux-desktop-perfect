#!/usr/bin/env node
// 必抛桩件清单（B9）+ pack 文件清单同步（TBL-007 / IF-021）
//
// 一次运行做三件事（都要求确定性）：
//   ① 把本机 NuGet 缓存里的 FrameworkList.xml **原样复制**到 40-build/pack/（不改一个字节）；
//   ② 写 40-build/pack/SOURCE.json（来源包、版本、条目统计、无 Profile 件名）；
//   ③ 扫上游源码与 ref 包，写 40-build/pack/must-stub.json（必抛/官宣 Windows-only 的
//      程序集与类型 + 处置建议）。
//
// ── 「必抛」的证据分级（entries[].evidence_kind）──────────────────────────
//   source_throw   上游源码里 `throw new PlatformNotSupportedException`（中文：真会抛）
//   source_attr    上游源码里 [UnsupportedOSPlatform("windows")] / [SupportedOSPlatform("windows")]
//   design_known   《09-开工前置与切片计划》§2.1 B9 与 R-003 已点名的必抛件
//   framework_profile  FrameworkList 的 Profile=WindowsForms（官宣 Windows 面；按 ADR-011 只做兼容层）
//   ref_only       只在 ref 包里出现、无源码证据（不猜，标「待裁决」）
//
// ── 纪律 ────────────────────────────────────────────────────────────────
//   不编造「某类型会抛」：type 只在源码能定位到封闭类时才写，否则留空并在 note 说明。
//   逐类型的 [SupportedOSPlatform] 元数据核对需要 MetadataLoadContext / System.Reflection.Metadata
//   读 ref 包，本仓当前没有该工具链（离线构建未验证），因此标为待对拍确认，不猜。
//
// 用法：node 50-verify/contract/scan-must-stub.mjs [--check]
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const UPSTREAM = path.join(ROOT, '40-build/upstream/wpf/src');
const PACK_DIR = path.join(ROOT, '40-build/pack');
const OUT_XML = path.join(PACK_DIR, 'FrameworkList.xml');
const OUT_SOURCE = path.join(PACK_DIR, 'SOURCE.json');
const OUT_STUB = path.join(PACK_DIR, 'must-stub.json');
const REF_PKG = 'microsoft.windowsdesktop.app.ref';
const REF_VER = '10.0.11';
const REF_HOME = path.join(os.homedir(), '.nuget/packages', REF_PKG, REF_VER);
const REF_LABEL = `~/.nuget/packages/${REF_PKG}/${REF_VER}`;
// 来源优先级（2026-10-30）：①40-build/lock/packs/ref-extract（build-pack 解出的只读副本，
// 不依赖 NuGet 缓存 —— 缓存会被 pack 组装的 PACK-SELFBUILT 清掉）②本机 NuGet 缓存。
const REF_EXTRACT = path.join(ROOT, '40-build/lock/packs/ref-extract');
const REF_BASE = fs.existsSync(path.join(REF_EXTRACT, 'data/FrameworkList.xml')) ? REF_EXTRACT : REF_HOME;
const SRC_XML = path.join(REF_BASE, 'data/FrameworkList.xml');
const SRC_REF_DLL_DIR = path.join(REF_BASE, 'ref/net10.0');

const errors = [];
const checkOnly = process.argv.includes('--check');

function walk(dir, ext, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, ext, out);
    else if (ent.name.endsWith(ext)) out.push(p);
  }
  return out;
}
const read = (p) => fs.readFileSync(p, 'utf8');

function writeOrCheck(target, content, label, encoding = 'utf8') {
  const buf = Buffer.isBuffer(content) ? content : Buffer.from(content, encoding);
  if (checkOnly) {
    const prev = fs.existsSync(target) ? fs.readFileSync(target) : Buffer.alloc(0);
    if (!prev.equals(buf)) {
      errors.push(`${label} 与源不一致（${path.relative(ROOT, target)}）；重跑生成器`);
      return;
    }
    return;
  }
  fs.writeFileSync(target, buf);
}

if (!fs.existsSync(SRC_XML)) {
  console.error(`ERROR 找不到 ref 包清单：${SRC_XML}`);
  process.exit(1);
}

// ---------- ① FrameworkList.xml 原样复制 ----------
const xmlBuf = fs.readFileSync(SRC_XML);
fs.mkdirSync(PACK_DIR, { recursive: true });
writeOrCheck(OUT_XML, xmlBuf, 'FrameworkList.xml');

// ---------- ② SOURCE.json ----------
const xml = xmlBuf.toString('utf8');
const fileEntries = [...xml.matchAll(/<File\s([^>]*)\/>/g)].map((m) => {
  const attrs = {};
  for (const a of m[1].matchAll(/(\w+)="([^"]*)"/g)) attrs[a[1]] = a[2];
  return attrs;
});
const profiles = {};
const noProfile = [];
const managed = [];
const analyzers = [];
for (const e of fileEntries) {
  const p = e.Profile || '';
  profiles[p || '(无 Profile)'] = (profiles[p || '(无 Profile)'] || 0) + 1;
  if (!p) noProfile.push(e.AssemblyName || e.Path);
  if (e.Type === 'Analyzer') analyzers.push(e.AssemblyName || e.Path);
  if (e.Type === 'Managed') managed.push(e.AssemblyName);
}
const refDlls = fs.existsSync(SRC_REF_DLL_DIR)
  ? fs.readdirSync(SRC_REF_DLL_DIR).filter((f) => f.endsWith('.dll')).sort()
  : [];

const sourceJson = {
  generated_by: '50-verify/contract/scan-must-stub.mjs',
  package: 'Microsoft.WindowsDesktop.App.Ref',
  version: REF_VER,
  source_path: REF_BASE === REF_EXTRACT ? '40-build/lock/packs/ref-extract/data/FrameworkList.xml（由 build-pack 从 lock/packs 的 Ref nupkg 解出）' : `${REF_LABEL}/data/FrameworkList.xml`,
  copy_command: `cp "${REF_LABEL}/data/FrameworkList.xml" 40-build/pack/FrameworkList.xml`,
  copy_policy: '原样复制，不改内容、不改编码、不改行尾；差异由 --check 判红',
  stats: {
    file_entries: fileEntries.length,
    managed_entries: managed.length,
    analyzer_entries: analyzers.length,
    by_profile: Object.fromEntries(Object.entries(profiles).sort()),
    no_profile_files: noProfile.sort(),
    ref_dlls_in_package: refDlls.length,
  },
  framework_identity: {
    framework_name: (xml.match(/FrameworkName="([^"]*)"/) || [])[1] || '',
    target_framework: `${(xml.match(/TargetFrameworkIdentifier="([^"]*)"/) || [])[1] || ''},Version=${(xml.match(/TargetFrameworkVersion="([^"]*)"/) || [])[1] || ''}`,
  },
  open_questions: [
    'FrameworkList 的 Profile 只有 WPF / WindowsForms / "WindowsForms;WPF" 三种取值，没有单独的"共享"标记：05 §3.7 的「共享 16 件」需按 20-spec §6 名单反查，不能靠 Profile 属性分组',
    '原生件（.so）不在 FrameworkList 里：TBL-007 的「原生件标记」列在本包里无从取值，待 pack 组装侧另立清单',
  ],
};
writeOrCheck(OUT_SOURCE, `${JSON.stringify(sourceJson, null, 2)}\n`, 'SOURCE.json');

// ---------- ③ must-stub 扫描 ----------
function stripComments(text) {
  const out = [];
  let i = 0;
  let state = 'code';
  while (i < text.length) {
    const c = text[i];
    const n = text[i + 1];
    if (state === 'code') {
      if (c === '/' && n === '/') { state = 'line'; out.push('  '); i += 2; continue; }
      if (c === '/' && n === '*') { state = 'block'; out.push('  '); i += 2; continue; }
      if (c === '"') { state = 'str'; out.push(c); i += 1; continue; }
      if (c === "'") { state = 'chr'; out.push(c); i += 1; continue; }
      out.push(c); i += 1; continue;
    }
    if (state === 'line') {
      if (c === '\n') { state = 'code'; out.push('\n'); } else out.push(' ');
      i += 1; continue;
    }
    if (state === 'block') {
      if (c === '*' && n === '/') { state = 'code'; out.push('  '); i += 2; continue; }
      out.push(c === '\n' ? '\n' : ' ');
      i += 1; continue;
    }
    if (c === '\\') { out.push('  '); i += 2; continue; }
    if ((state === 'str' && c === '"') || (state === 'chr' && c === "'")) state = 'code';
    out.push(c); i += 1;
  }
  return out.join('');
}

const entries = [];
const throws = [];
const unsupportedAttrs = [];
const supportedAttrs = [];
const mentions = [];

function assemblyOf(relPath) {
  // 40-build/upstream/wpf/src/<Project>/... → <Project>
  const parts = relPath.split(path.sep);
  const i = parts.indexOf('src');
  return i >= 0 && parts[i + 1] ? parts[i + 1] : '(unknown)';
}

for (const file of walk(UPSTREAM, '.cs')) {
  const rel = path.relative(ROOT, file);
  const raw = read(file);
  const text = stripComments(raw);
  const lines = text.split('\n');
  const rawLines = raw.split('\n');
  let currentClass = '';
  for (let n = 0; n < lines.length; n += 1) {
    const line = lines[n];
    const cls = line.match(/\b(?:class|struct|record|interface)\s+(\w+)/);
    if (cls) currentClass = cls[1];
    if (/\bthrow\s+new\s+PlatformNotSupportedException\b/.test(line)) {
      const hit = { assembly: assemblyOf(rel), type: currentClass, file: rel, line: n + 1, snippet: rawLines[n].trim().slice(0, 120) };
      throws.push(hit);
      entries.push({
        assembly: hit.assembly,
        type: hit.type,
        evidence_kind: 'source_throw',
        evidence: `${rel}:${n + 1}`,
        handling: '明确降级 + D-（待缺陷册分配）',
        status: '待裁决',
      });
      continue;
    }
    if (/\bPlatformNotSupportedException\b/.test(line)) mentions.push({ file: rel, line: n + 1 });
    if (/\[\s*UnsupportedOSPlatform\(\s*"windows"/i.test(line)) {
      unsupportedAttrs.push({ assembly: assemblyOf(rel), type: currentClass, file: rel, line: n + 1 });
      entries.push({
        assembly: assemblyOf(rel),
        type: currentClass,
        evidence_kind: 'source_attr',
        evidence: `${rel}:${n + 1}`,
        handling: '明确降级 + D-（待缺陷册分配）',
        status: '待裁决',
      });
    }
    if (/\[\s*SupportedOSPlatform\(\s*"windows"/i.test(line)) {
      supportedAttrs.push({ assembly: assemblyOf(rel), type: currentClass, file: rel, line: n + 1 });
    }
  }
}

// 设计已点名的必抛件（09 §2.1 B9 / R-003）：给出 ref 包存在性证据，不猜类型
const DESIGN_KNOWN = [
  ['System.Windows.Extensions', '自产替换（原始桩件在非 Windows 上抛 PlatformNotSupportedException）'],
  ['System.Drawing.Common', '自产替换或包内替换（R-003：非 Windows 默认抛）'],
  ['System.Private.Windows.GdiPlus', '自产替换（R-003 的优先路径）'],
];
for (const [assembly, handling] of DESIGN_KNOWN) {
  const inRef = refDlls.includes(`${assembly}.dll`);
  entries.push({
    assembly,
    type: '',
    evidence_kind: 'design_known',
    evidence: inRef
      ? `09 §2.1 B9 + ref/net10.0/${assembly}.dll 存在`
      : `09 §2.1 B9（ref 包里未找到该 dll，待对拍确认）`,
    handling,
    status: '待裁决',
  });
}

// Profile=WindowsForms 的程序集：官宣 Windows 面，按 ADR-011 只做兼容层
for (const e of fileEntries) {
  if (e.Type !== 'Managed') continue;
  const prof = (e.Profile || '').split(';');
  if (!prof.includes('WindowsForms')) continue;
  entries.push({
    assembly: e.AssemblyName,
    type: '',
    evidence_kind: 'framework_profile',
    evidence: `FrameworkList.xml Profile="${e.Profile}"`,
    handling: '明确降级（ADR-011：兼容层只做核心六控件 + 常用控件；未实现控件报错 + 空白占位）',
    status: '待裁决',
  });
}

// 证据优先级去重：同一程序集已有 source_throw/source_attr/design_known 时，
// 不再重复登记它的 framework_profile（否则 System.Drawing.Common 会同时出现两次）。
const strongAssemblies = new Set(
  entries.filter((e) => e.evidence_kind !== 'framework_profile').map((e) => e.assembly),
);
const kept = entries.filter((e) => !(e.evidence_kind === 'framework_profile' && strongAssemblies.has(e.assembly)));

// 稳定排序 + 去重（同 assembly+type+evidence）
const seen = new Set();
const deduped = [];
for (const e of kept) {
  const key = `${e.assembly}|${e.type}|${e.evidence}`;
  if (seen.has(key)) continue;
  seen.add(key);
  deduped.push(e);
}
deduped.sort((a, b) => (a.assembly === b.assembly ? `${a.type}${a.evidence}`.localeCompare(`${b.type}${b.evidence}`) : a.assembly.localeCompare(b.assembly)));

const byKind = {};
const byAssembly = {};
for (const e of deduped) {
  byKind[e.evidence_kind] = (byKind[e.evidence_kind] || 0) + 1;
  byAssembly[e.assembly] = (byAssembly[e.assembly] || 0) + 1;
}

const stubJson = {
  generated_by: '50-verify/contract/scan-must-stub.mjs',
  scope: '必抛 / 官宣 Windows-only 的程序集与类型（B9）',
  inputs: {
    framework_list: `${REF_LABEL}/data/FrameworkList.xml`,
    ref_assemblies: `${REF_LABEL}/ref/net10.0/*.dll`,
    upstream_source: '40-build/upstream/wpf/src/**/*.cs',
    design: '30-design/设计文档包/09-开工前置与切片计划.md §2.1 B9、03 §2.1 R-003',
  },
  summary: {
    entries: deduped.length,
    by_evidence_kind: Object.fromEntries(Object.entries(byKind).sort()),
    by_assembly: Object.fromEntries(Object.entries(byAssembly).sort((a, b) => b[1] - a[1])),
    source_throws: throws.length,
    source_pnse_mentions: mentions.length,
    source_unsupported_os_windows: unsupportedAttrs.length,
    source_supported_os_windows: supportedAttrs.length,
    ref_dlls: refDlls.length,
  },
  entries: deduped,
  source_scan: {
    throws,
    mentions_sample: mentions.slice(0, 20),
    unsupported_os_platform: unsupportedAttrs,
    supported_os_platform: supportedAttrs,
  },
  open_questions: [
    '逐类型的 [SupportedOSPlatform("windows")] 元数据核对需要 MetadataLoadContext / System.Reflection.Metadata 读 ref 包；本仓尚未验证该工具链可离线构建，故按程序集级给出，类型级留空',
    'System.Drawing.Common / System.Private.Windows.GdiPlus 的「必抛」来自设计文档（R-003），不是本次从元数据读出的；升级为「已核对」需实现阶段跑一次最小复现',
    'WinForms 兼容层之外的 Profile=WindowsForms 程序集共 ' + Object.keys(byAssembly).filter((a) => /^System\.Windows\.Forms|Microsoft\.VisualBasic|Accessibility/.test(a)).length + ' 个：是否需要「加载即占位」的粒度清单，待 DQ-003 边界细化',
  ],
};

writeOrCheck(OUT_STUB, `${JSON.stringify(stubJson, null, 2)}\n`, 'must-stub.json');

if (errors.length) {
  for (const e of errors) console.error(`ERROR ${e}`);
  process.exit(1);
}
if (checkOnly) {
  console.log(`scan-must-stub: --check 通过（FrameworkList ${fileEntries.length} 条 / must-stub ${deduped.length} 条）`);
} else {
  console.log(`scan-must-stub: FrameworkList ${fileEntries.length} 条（Managed ${managed.length} / Analyzer ${analyzers.length} / 无 Profile ${noProfile.length}）`);
  console.log(`scan-must-stub: must-stub ${deduped.length} 条（source_throw ${byKind.source_throw || 0} / design_known ${byKind.design_known || 0} / framework_profile ${byKind.framework_profile || 0}）`);
}
