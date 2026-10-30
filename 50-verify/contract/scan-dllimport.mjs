#!/usr/bin/env node
// DllImport 调用约定清单（B10 / IF-006 的前置盘点）
//
// 扫描上游 40-build/upstream/wpf/src 下全部 [DllImport(...)] 声明，按库统计：
//   声明数、唯一符号数、CharSet 分布、ExactSpelling 分布、SetLastError 计数、
//   CallingConvention 分布、入口名前 20 个、W/A 变体但没写 CharSet 的风险计数。
//
// ── 结论口径（重要）────────────────────────────────────────────────────────
//   x64 上 Windows stdcall 与 SysV cdecl 在**寄存器传参**下没有栈差异，所以本项目
//   x64/X11 首发不做逐入口的约定改写；真正会出问题的是：
//     ① 结构体布局与字段宽度（Pack/FieldOffset，见 TBL-002 / win32_abi.h 断言）；
//     ② CharSet 与 A/W 选择（UTF-16 与 UTF-8 在边界上的转换方向）；
//     ③ 回调函数（托管委托传进原生，反向调用约定与生命周期）。
//   本文件给出的 ABI 结论标注为「本机推断」，**不是**在 x64 目标上逐入口核对过的读数；
//   逐入口核对要等 `win32_abi.h` 断言集与首批 69 个入口落地（09 §5.1）后才成立。
//
// 用法：node 50-verify/contract/scan-dllimport.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const UPSTREAM = path.join(ROOT, '40-build/upstream/wpf/src');
const OUT = path.join(ROOT, '40-build/native-shim/DLLIMPORT-INVENTORY.json');

const errors = [];

function walk(dir, ext, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, ext, out);
    else if (ent.name.endsWith(ext)) out.push(p);
  }
  return out;
}
const read = (p) => fs.readFileSync(p, 'utf8');

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


// ---------- 条件编译屏蔽（只处理会换库名的那一个分支）----------------------
// 上游 WindowsBase 的 RightsManagement 文件里有：
//     #if PRESENTATION_HOST_DLL
//     [DllImport(ExternDll.PresentationHostDll, ...)]
//     #else
//     [DllImport(ExternDll.MsDrm, ...)]
//     #endif
//     internal static extern int DRMInitEnvironment(...)
// 同一个方法挂两条属性，但只有 #else 支在常规 WPF 构建里生效。
// 这里只对 PRESENTATION_HOST_DLL 求值（本仓构建不定义它 → false），
// 其它 #if 一律两支都算「命中」——我们要的是声明面，不是某个构建变体的裁剪结果。
function activeLineMask(text) {
  const lines = text.split('\n');
  const active = new Array(lines.length).fill(true);
  const stack = [];
  const cur = () => stack.every((s) => s.active);
  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    const m = line.match(/^\s*#\s*(if|elif|else|endif)\b(.*)$/);
    if (!m) { active[i] = cur(); continue; }
    const kw = m[1];
    const expr = m[2].trim();
    if (kw === 'if' || kw === 'elif') {
      if (kw === 'if') stack.push({ active: true, known: false, ifActive: true });
      const frame = stack[stack.length - 1];
      if (/^!?\s*PRESENTATION_HOST_DLL$/.test(expr)) {
        // 本仓构建不定义 PRESENTATION_HOST_DLL：#if 支惰性，#else 支生效
        frame.known = true;
        frame.ifActive = expr.startsWith('!'); // `#if !X` 为真
        frame.active = frame.ifActive;
      } else {
        frame.known = false;
        frame.active = true; // 其它条件：两支都算命中
      }
    } else if (kw === 'else') {
      const frame = stack[stack.length - 1];
      if (frame && frame.known) frame.active = !frame.ifActive;
      else if (frame) frame.active = true;
    } else if (kw === 'endif') {
      stack.pop();
    }
    active[i] = cur();
  }
  return active;
}

function lineNoOf(text, index) {
  let n = 0;
  for (let i = 0; i < index && i < text.length; i += 1) if (text.charCodeAt(i) === 10) n += 1;
  return n;
}

// ---------- 1. 库名常量 ----------
const constMap = new Map();
const files = walk(UPSTREAM, '.cs');
for (const file of files) {
  const text = stripComments(read(file));
  const events = [];
  for (const m of text.matchAll(/\b(?:class|struct|interface)\s+(\w+)/g)) events.push({ at: m.index, kind: 'class', name: m[1] });
  for (const m of text.matchAll(/\bconst\s+string\s+(\w+)\s*=\s*(\$?"[^"]*")/g)) events.push({ at: m.index, kind: 'const', name: m[1], value: m[2] });
  events.sort((a, b) => a.at - b.at);
  let currentClass = null;
  for (const ev of events) {
    if (ev.kind === 'class') { currentClass = ev.name; continue; }
    let value = ev.value.replace(/^\$/, '').replace(/"/g, '').replace(/\{[^}]*\}/g, '');
    if (!constMap.has(ev.name)) constMap.set(ev.name, new Set());
    constMap.get(ev.name).add(value);
    if (currentClass) {
      const key = `${currentClass}.${ev.name}`;
      if (!constMap.has(key)) constMap.set(key, new Set());
      constMap.get(key).add(value);
    }
  }
}

function normalizeLib(value) {
  let v = value.toLowerCase().replace(/^\$/, '');
  v = v.replace(/^api-ms-win-([a-z0-9-]+?)-l1-[\d-]+\.dll$/, 'api-ms-win-$1');
  v = v.replace(/\.(dll|drv|ocx)$/, '').replace(/_cor3$/, '');
  return v;
}

function resolveLib(token) {
  const raw = token.trim();
  if (raw.startsWith('"')) return normalizeLib(raw.replace(/"/g, ''));
  for (const key of [raw, raw.split('.').slice(-1)[0]]) {
    const set = constMap.get(key);
    if (set && set.size) return normalizeLib([...set].sort()[0]);
  }
  return null;
}

// ---------- 2. 逐声明解析 ----------
const decls = [];
for (const file of files) {
  const rawText = read(file);
  const text = stripComments(rawText);
  const mask = activeLineMask(rawText);
  let m;
  const re = /\[DllImport\(\s*([^)]*)\)\s*\]/g;
  while ((m = re.exec(text))) {
    if (!mask[lineNoOf(text, m.index)]) continue; // 非活动条件编译支
    const args = m[1];
    const lib = resolveLib(args.split(',')[0].trim());
    if (!lib) { errors.push(`无法解析库名：${path.relative(ROOT, file)} @${m.index}`); continue; }
    const attr = (name) => {
      const hit = args.match(new RegExp(`${name}\\s*=\\s*([\\w.]+)`));
      return hit ? hit[1] : null;
    };
    const after = text.slice(m.index + m[0].length, m.index + m[0].length + 4000);
    const sig = after.match(/\bextern\s+[^;()]*?\b(\w+)\s*\(/);
    if (!sig) { errors.push(`属性块后无 extern 声明：${path.relative(ROOT, file)} @${m.index}`); continue; }
    const entry = attr('EntryPoint') ? attr('EntryPoint').replace(/"/g, '') : sig[1];
    decls.push({
      lib,
      symbol: entry,
      method: sig[1],
      charset: attr('CharSet'),
      exactSpelling: attr('ExactSpelling'),
      setLastError: attr('SetLastError'),
      callingConvention: attr('CallingConvention'),
      file: path.relative(ROOT, file),
    });
  }
}

if (decls.length < 1000) errors.push(`声明数异常偏低：${decls.length}（上游应约 1205 条）`);

// ---------- 3. 按库汇总 ----------
const libs = {};
for (const d of decls) {
  if (!libs[d.lib]) {
    libs[d.lib] = {
      declarations: 0,
      symbols: new Set(),
      charset: { Unicode: 0, Ansi: 0, Auto: 0, unspecified: 0 },
      exact_spelling: { true: 0, false: 0, unspecified: 0 },
      set_last_error: 0,
      calling_convention: {},
      wide_ambiguous: [], // 入口名以 W/A 结尾但没写 CharSet
      files: new Set(),
    };
  }
  const L = libs[d.lib];
  L.declarations += 1;
  L.symbols.add(d.symbol);
  L.files.add(d.file);
  L.charset[d.charset === null ? 'unspecified' : d.charset.split('.').pop()] += 1;
  L.exact_spelling[d.exactSpelling === null ? 'unspecified' : d.exactSpelling] += 1;
  if (d.setLastError === 'true') L.set_last_error += 1;
  const cc = d.callingConvention === null ? 'unspecified(=>Winapi)' : d.callingConvention.split('.').pop();
  L.calling_convention[cc] = (L.calling_convention[cc] || 0) + 1;
  if (d.charset === null && /[WA]$/.test(d.symbol)) L.wide_ambiguous.push(d.symbol);
}

const libraries = {};
for (const lib of Object.keys(libs).sort()) {
  const L = libs[lib];
  const symbols = [...L.symbols].sort();
  libraries[lib] = {
    declarations: L.declarations,
    unique_symbols: symbols.length,
    charset: L.charset,
    exact_spelling: L.exact_spelling,
    set_last_error: L.set_last_error,
    calling_convention: L.calling_convention,
    risk_wide_without_charset: L.wide_ambiguous.length,
    risk_examples: [...new Set(L.wide_ambiguous)].sort().slice(0, 10),
    sample_symbols: symbols.slice(0, 20),
    declared_in_files: [...L.files].sort(),
  };
}

// 同名符号在多个文件里重复声明（不是错，但改名时必须一起改）
const dupMap = new Map();
for (const d of decls) {
  const key = `${d.lib}::${d.symbol}`;
  dupMap.set(key, (dupMap.get(key) || 0) + 1);
}
const duplicates = [...dupMap.entries()]
  .filter(([, n]) => n > 1)
  .map(([key, n]) => {
    const [lib, symbol] = key.split('::');
    return { lib, symbol, declarations: n };
  })
  .sort((a, b) => (a.lib === b.lib ? a.symbol.localeCompare(b.symbol) : a.lib.localeCompare(b.lib)));

const totalDecls = decls.length;
const totalSymbols = new Set(decls.map((d) => `${d.lib}::${d.symbol}`)).size;
const wideRisk = Object.values(libs).reduce((n, L) => n + L.wide_ambiguous.length, 0);

const out = {
  generated_by: '50-verify/contract/scan-dllimport.mjs',
  inputs: {
    upstream: '40-build/upstream/wpf/src/**/*.cs',
    snapshot: '40-build/upstream/wpf/SNAPSHOT.json（commit 2565b0112 / release 10.0）',
    note: '库名来自 DllImport 属性实参，经 ExternDll.* / DllImport.* 常量解析后归一化（去 .dll/_cor3）',
  },
  summary: {
    libraries: Object.keys(libraries).length,
    declarations: totalDecls,
    unique_lib_symbol_pairs: totalSymbols,
    duplicate_declarations: duplicates.length,
    charset_unspecified: decls.filter((d) => d.charset === null).length,
    exact_spelling_unspecified: decls.filter((d) => d.exactSpelling === null).length,
    set_last_error_true: decls.filter((d) => d.setLastError === 'true').length,
    wide_without_charset: wideRisk,
  },
  conclusion: {
    abi: 'x64 上 Windows stdcall 与 SysV cdecl 在寄存器传参下无栈差异；本项目 V1（x64/X11）不逐入口改写调用约定。',
    real_risks: [
      '结构体布局与字段宽度（Pack/FieldOffset；TBL-002 与 win32_abi.h 断言覆盖）',
      'CharSet 与 A/W 选择（UTF-16 与 UTF-8 的转换方向与长度校验）',
      '回调函数（托管委托进原生后反向调用；生命周期与线程归属）',
    ],
    confidence: '本机推断（未在 x64 目标上逐入口核对）',
    upgrade_when: 'win32_abi.h 断言集覆盖 09 §5.1 首批 69 个入口，且首批入口在 X11 会话上跑通 TG-001/TG-004 后，才可改为「已核对」',
  },
  risk_detail: {
    wide_without_charset: {
      count: wideRisk,
      meaning: '入口名以 W/A 结尾但属性里没有 CharSet：A/W 变体靠名字区分，缺 CharSet 不影响选函数，但影响字符串封送方式，需逐条确认（首批入口由 B10 逐入口核对）',
    },
  },
  libraries,
  duplicates,
  open_questions: [
    'unresolved_libs: 无（全部常量解析成功或为字面量）',
    'CallingConvention 未写 = Winapi，在 x64 上等同默认；无 `CallingConvention.Cdecl` 声明',
    'PresentationNative/MilCore/WindowsCodecs 三库的 string 参数封装方式（StringBuilder / char* / 长度）需在 TBL-001 逐入口登记',
  ],
};

if (errors.length) {
  for (const e of errors.slice(0, 20)) console.error(`ERROR ${e}`);
  process.exit(1);
}

const content = `${JSON.stringify(out, null, 2)}\n`;
if (process.argv.includes('--check')) {
  const prev = fs.existsSync(OUT) ? read(OUT) : '';
  if (prev !== content) {
    console.error('ERROR DLLIMPORT-INVENTORY.json 与生成结果不一致；重跑生成器');
    process.exit(1);
  }
  console.log(`scan-dllimport: --check 通过（${totalDecls} 条声明 / ${out.summary.libraries} 个库）`);
} else {
  fs.writeFileSync(OUT, content, 'utf8');
  console.log(`scan-dllimport: 写出 ${totalDecls} 条声明 / ${out.summary.libraries} 个库 / 唯一符号 ${totalSymbols}`);
  const top = Object.entries(libraries).sort((a, b) => b[1].declarations - a[1].declarations).slice(0, 6);
  for (const [lib, s] of top) console.log(`  ${lib}\t声明 ${s.declarations}\t符号 ${s.unique_symbols}\t缺 CharSet ${s.charset.unspecified}`);
}
