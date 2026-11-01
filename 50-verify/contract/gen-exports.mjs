#!/usr/bin/env node
// 出口清单生成器：#出口清单生成器（TBL-001 / IF-006）
//
// 生成 40-build/native-shim/exports.txt。契约列定义见
// 30-design/设计文档包/05-数据与清单设计说明.md §3.1；格式规则见
// 30-design/设计文档包/04-接口设计说明.md §2.6。
//
// ── 状态判定规则（唯一口径，改这里等于改契约）──────────────────────────────
//   impl  上游声明存在，或在 C# 导出清单里登记为 Real/Identity/State，且实现侧有该符号
//   stub  实现侧有该符号，但函数体是「纯常量返回」（含 ERROR_CALL_NOT_IMPLEMENTED）
//   gap   上游声明存在，但实现侧找不到该符号（含整库无实现）
//   shim 额外导出（上游未声明）按 impl 登记，note 标「shim 额外导出」——04 §2.6 要求
//   实现多出的符号必须补登记，否则对拍时会被当成漂移。
//
// ── lib 列写什么 ───────────────────────────────────────────────────────────
//   TBL-001 要求 lib 列是 .so 文件名，且必须落在部署清单（概设 §2.4 的 4 个自产 .so）。
//   一个 Windows 库可能还没有原生实现：此时 lib 写「若实现将落在哪个 .so」，
//   由 status=gap 表达「现在没有」，note 写明替代路径（托管桥 / 降级 / 未迁移）。
//   本仓不存在 dwrite 的 DllImport 面（DirectWrite 面在 DirectWriteForwarder 程序集里），
//   所以 libdwlinux.so 在清单里没有行——这不是漏登记，见报告「与 04/05 口径差异」。
//
// ── 输入 ───────────────────────────────────────────────────────────────────
//   40-build/upstream/wpf/src/**/*.cs        上游 P/Invoke 声明（DllImport 属性 + DllImport/MilCore 等常量）
//   40-build/native-shim/src/*.c             Win32/PTS 实现（-fvisibility=default，非 static 即导出）
//   40-build/milcore/Interop/MilNative.Exports.cs  milcore 的导出覆盖清单（名称 → ExportDepth）
//
// 用法：node 50-verify/contract/gen-exports.mjs [--check]
// 无外部依赖；同输入同输出；--check 与磁盘不一致时 rc=1。
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const OUT = path.join(ROOT, '40-build/native-shim/exports.txt');
const UPSTREAM = path.join(ROOT, '40-build/upstream/wpf/src');
const SHIM_SRC = path.join(ROOT, '40-build/native-shim/src');
const MIL_MANIFEST = path.join(ROOT, '40-build/milcore/Interop/MilNative.Exports.cs');
const SINCE = '10.0.11'; // 首次登记版本取本机 ref pack 版本（见 40-build/pack/SOURCE.json）

const errors = [];
const warnings = [];

function walk(dir, ext, out = []) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) walk(p, ext, out);
    else if (ent.name.endsWith(ext)) out.push(p);
  }
  return out;
}

function read(p) {
  return fs.readFileSync(p, 'utf8');
}

// 去掉 // 与 /* */ 注释，但保留行数与字符串（够用即可：只做词法级扫描）
function stripComments(text) {
  // 词法级剥离注释：必须区分「代码 / 行注释 / 块注释 / 字符串 / 字符」五种状态。
  // 直接用正则先剥 /* */ 会把 // 注释里的 /* 当成块注释起点，从而吞掉后面的真实代码
  // （本仓 win32_core.c 就有一处，踩过）。被剥离的字符一律换成空格，保留换行与行号。
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
    if (state === 'str' || state === 'chr') {
      if (c === '\\') { out.push('  '); i += 2; continue; }
      if ((state === 'str' && c === '"') || (state === 'chr' && c === "'")) state = 'code';
      out.push(c); i += 1; continue;
    }
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

// ---------- 1. 库名常量（ExternDll.User32 / DllImport.MilCore / PresentationNativeDll）----------
const constMap = new Map(); // key -> Set(values)
function addConst(key, value) {
  if (!constMap.has(key)) constMap.set(key, new Set());
  constMap.get(key).add(value);
}
for (const file of walk(UPSTREAM, '.cs')) {
  const text = stripComments(read(file));
  const classRe = /\b(?:class|struct|interface)\s+(\w+)/g;
  const constRe = /\bconst\s+string\s+(\w+)\s*=\s*(\$?"[^"]*")/g;
  const events = [];
  let m;
  while ((m = classRe.exec(text))) events.push({ at: m.index, kind: 'class', name: m[1] });
  while ((m = constRe.exec(text))) events.push({ at: m.index, kind: 'const', name: m[1], value: m[2] });
  events.sort((a, b) => a.at - b.at);
  let currentClass = null;
  for (const ev of events) {
    if (ev.kind === 'class') {
      currentClass = ev.name;
      continue;
    }
    let value = ev.value.replace(/^\$/, '').replace(/"/g, '');
    value = value.replace(/\{[^}]*\}/g, ''); // $"...{BuildInfo.X}..." → 去插值
    addConst(ev.name, value);
    if (currentClass) addConst(`${currentClass}.${ev.name}`, value);
  }
}

function resolveLibToken(token) {
  const raw = token.trim();
  if (raw.startsWith('"')) return raw.replace(/"/g, '');
  for (const key of [raw, raw.split('.').slice(-1)[0]]) {
    const set = constMap.get(key);
    if (set && set.size) return [...set].sort()[0];
  }
  return null;
}

// ---------- 2. 库名归一化 + 落点 .so + 责任模块 ----------
// so: 若实现将落在哪个自产 .so（概设 §2.4 的部署清单）
// owner: 05 §3.1 要求「必须落在 38 个模块内」
const LIBS = {
  user32: ['libwpfwin32.so', 'MOD-11'],
  gdi32: ['libwpfwin32.so', 'MOD-13'],
  gdiplus: ['libwpfwin32.so', 'MOD-14'],
  kernel32: ['libwpfwin32.so', 'MOD-15'],
  advapi32: ['libwpfwin32.so', 'MOD-15'],
  shell32: ['libwpfwin32.so', 'MOD-15'],
  ole32: ['libwpfwin32.so', 'MOD-63'],
  oleacc: ['libwpfwin32.so', 'MOD-63'],
  oleaut32: ['libwpfwin32.so', 'MOD-63'],
  imm32: ['libwpfwin32.so', 'MOD-36'],
  msctf: ['libwpfwin32.so', 'MOD-36'],
  uxtheme: ['libwpfwin32.so', 'MOD-04'],
  dwmapi: ['libwpfwin32.so', 'MOD-11'],
  winmm: ['libwpfwin32.so', 'MOD-83'],
  comdlg32: ['libwpfwin32.so', 'MOD-62'],
  comctl32: ['libwpfwin32.so', 'MOD-62'],
  shfolder: ['libwpfwin32.so', 'MOD-15'],
  shcore: ['libwpfwin32.so', 'MOD-13'],
  psapi: ['libwpfwin32.so', 'MOD-15'],
  ntdll: ['libwpfwin32.so', 'MOD-15'],
  wtsapi32: ['libwpfwin32.so', 'MOD-15'],
  wininet: ['libwpfwin32.so', 'MOD-15'],
  urlmon: ['libwpfwin32.so', 'MOD-15'],
  'system.io.compression.native': ['libwpfwin32.so', 'MOD-15'],
  mshwgst: ['libwpfwin32.so', 'MOD-83'],
  presentationnative: ['libwpfwin32.so', 'MOD-16'],
  presentationcffrasterizer: ['libwpfwin32.so', 'MOD-35'],
  'wpfgfx_v0400': ['libwpfgfx_linux.so', 'MOD-25'],
  milctrl: ['libwpfgfx_linux.so', 'MOD-25'],
  windowscodecs: ['libwpfwic.so', 'MOD-41'],
  windowscodecsext: ['libwpfwic.so', 'MOD-41'],
  mscms: ['libwpfwic.so', 'MOD-42'],
  uiautomationcore: ['libwpfwin32.so', 'MOD-81'],
  msdrm: ['libwpfwin32.so', 'MOD-85'],
  penimc: ['libwpfwin32.so', 'MOD-84'],
  ninput: ['libwpfwin32.so', 'MOD-84'],
  prntvpt: ['libwpfwin32.so', 'MOD-82'],
  winspool: ['libwpfwin32.so', 'MOD-82'],
  presentationhost: ['libwpfwin32.so', 'MOD-51'],
};
// 无原生落点的库：显式给替代说明（note 列写出来）
const LIB_NOTES = {
  uiautomationcore: '托管桥替代（MOD-81，AT-SPI over D-Bus）；无原生入口',
  msdrm: '明确降级 D-001（E_NOTIMPL）',
  penimc: 'PenImc 替身（MOD-84，libinput/XInput2）；未实现',
  ninput: 'Windows Ink 面；未迁移',
  prntvpt: '打印路径走 CUPS（MOD-82）；未实现',
  winspool: '打印路径走 CUPS（MOD-82）；未实现',
  presentationhost: 'ClickOnce/XBAP 宿主件；不在 V1 范围',
  presentationcffrasterizer: 'CFF 轮廓由 FreeType 承接（MOD-35）',
};

function normalizeLib(value) {
  let v = value.toLowerCase().replace(/^\$/, '');
  v = v.replace(/^api-ms-win-([a-z0-9-]+?)-l1-[\d-]+\.dll$/, 'api-ms-win-$1');
  v = v.replace(/\.(dll|drv|ocx)$/, '');
  v = v.replace(/_cor3$/, '');
  if (v === 'presentationnative') return 'presentationnative';
  if (v.startsWith('api-ms-win-')) return v;
  return v;
}

// ---------- 3. 解析上游 DllImport 声明 ----------
// 每条形如：[DllImport(ExternDll.User32, CharSet = CharSet.Unicode, ...)] public static extern int Foo(...)
const skipped = [];
function parseDeclarations() {
  const decls = [];
  for (const file of walk(UPSTREAM, '.cs')) {
    const rawText = read(file);
    const text = stripComments(rawText);
    const mask = activeLineMask(rawText);
    const re = /\[DllImport\(\s*([^)]*)\)\s*\]/g;
    let m;
    while ((m = re.exec(text))) {
      if (!mask[lineNoOf(text, m.index)]) continue; // 非活动条件编译支
      const args = m[1];
      const libToken = args.split(',')[0].trim();
      const libRaw = resolveLibToken(libToken);
      if (!libRaw) {
        errors.push(`无法解析库名常量：${libToken}（${path.relative(ROOT, file)}）`);
        continue;
      }
      const entryMatch = args.match(/EntryPoint\s*=\s*"([^"]+)"/);
      // 找到属性块之后最近的一个 extern 方法声明
      const after = text.slice(m.index + m[0].length, m.index + m[0].length + 4000);
      const sig = after.match(/\bextern\s+[^;()]*?\b(\w+)\s*\(/);
      if (!sig) {
        skipped.push(`${path.relative(ROOT, file)} @${m.index}`);
        continue;
      }
      decls.push({
        lib: normalizeLib(libRaw),
        symbol: entryMatch ? entryMatch[1] : sig[1],
        method: sig[1],
        file: path.relative(ROOT, file),
      });
    }
  }
  return decls;
}

// ---------- 4. 实现侧符号集合 ----------
// 4a. native-shim：顶层非 static 函数定义（编译开关是 -fvisibility=default，因此非 static 即导出）
function parseShimExports() {
  const set = new Map(); // name -> {file, body}
  for (const file of walk(SHIM_SRC, '.c')) {
    const text = stripComments(read(file));
    const re = /^(?!static\b)([A-Za-z_][A-Za-z0-9_ \t\*]*?)\b([A-Za-z_]\w*)\s*\(/gm;
    let m;
    while ((m = re.exec(text))) {
      const ret = m[1].trim();
      const name = m[2];
      if (['if', 'for', 'while', 'switch', 'return', 'sizeof', 'else', 'case'].includes(ret)) continue;
      if (name.startsWith('wpf_') || name.startsWith('WpfLinuxWin32_')) continue; // 内部件/桥件，不进 Win32 契约
      // 提取函数体（找配对的 { }
      let i = text.indexOf('{', m.index + m[0].length);
      const close = i >= 0 ? braceEnd(text, i) : -1;
      const body = close > i ? text.slice(i, close + 1) : '';
      set.set(name, { file: path.relative(ROOT, file), body });
    }
  }
  // 【清算 / K-180】**宏生成的导出**：`WPF_OSVERSION_FALSE(IsWindowsXxx)` 展开后是
  //   `int IsWindowsXxx(void) { return 0; }`，但上面的行首正则扫不到宏调用。
  //   实测：`.so` 里 `nm -D` 明明有 20 个 `IsWindows*`，清单却全标 `gap` —— **假 gap**。
  //   这里按宏名把它们补进导出表，body 用宏展开后的实际函数体，让 isStub 如实判定。
  const MACRO_DEFS = { 'WPF_OSVERSION_FALSE': 'return 0;' };
  for (const file of walk(SHIM_SRC, '.c')) {
    const text = stripComments(read(file));
    for (const macro of Object.keys(MACRO_DEFS)) {
      const mre = new RegExp('\\b' + macro + '\\s*\\(\\s*([A-Za-z_]\\w*)\\s*\\)', 'g');
      let mm;
      while ((mm = mre.exec(text))) {
        const name = mm[1];
        if (!set.has(name)) set.set(name, { file: path.relative(ROOT, file), body: MACRO_DEFS[macro] });
      }
    }
  }

  return set;
}

function braceEnd(text, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < text.length; i += 1) {
    const c = text[i];
    if (c === '{') depth += 1;
    else if (c === '}') {
      depth -= 1;
      if (depth === 0) return i;
    }
  }
  return -1;
}

// 纯常量返回 → stub。只认「整个函数体就是一个常量返回（可选 SetLastError）」这一种形态，
// 有分支/赋值/调用的都算 impl；宁可低估 stub，不把真实现标成桩。
function isStub(body) {
  const t = body
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\/\/[^\n]*/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  const inner = t.replace(/^\{/, '').replace(/\}$/, '').trim();
  if (!inner) return false;
  const simple = /^(?:SetLastError\(\s*(?:ERROR_CALL_NOT_IMPLEMENTED|120)\s*\)\s*;\s*)?return\s+(?:FALSE|TRUE|0|NULL|E_NOTIMPL|WPF_PTS_ERR_NOT_IMPLEMENTED|-1)\s*;\s*$/;
  return simple.test(inner);
}

// 4b. milcore：导出覆盖清单（名称 → ExportDepth）
function parseMilcoreExports() {
  const map = new Map();
  if (!fs.existsSync(MIL_MANIFEST)) {
    warnings.push('milcore 导出清单不存在，libwpfgfx_linux.so 全部按 gap 处理');
    return map;
  }
  const text = stripComments(read(MIL_MANIFEST));
  const re = /\{\s*"([^"]+)"\s*,\s*ExportDepth\.(\w+)\s*\}/g;
  let m;
  while ((m = re.exec(text))) map.set(m[1], m[2]);
  return map;
}

// 【ACC-028 / K-168】`libwpfwic.so` 的导出表。
//   以前 `SO_IMPL` 只有 `libwpfwin32.so` 和 `libwpfgfx_linux.so`，
//   于是 `statusFor('libwpfwic.so', ...)` 永远落到 `gap` 分支 ——
//   **即使 .so 里真导出了也标 gap**（实测：补完 71 个 WIC 符号后清单仍是 gap 119）。
function parseWicExports() {
  const map = new Map();
  const soPath = path.join(ROOT, '40-build/wic-dwrite/bin/libwpfwic.so');
  if (!fs.existsSync(soPath)) {
    warnings.push('libwpfwic.so 不存在（先 make -C 40-build/wic-dwrite），windowscodecs 全部按 gap 处理');
    return map;
  }
  let out = '';
  try {
    out = execFileSync('nm', ['-D', '--defined-only', soPath], { encoding: 'utf8' });
  } catch (e) {
    warnings.push('nm 读取 libwpfwic.so 失败：' + e.message);
    return map;
  }
  for (const line of out.split('\n')) {
    const parts = line.trim().split(/\s+/);
    if (parts.length < 3) continue;
    const kind = parts[parts.length - 2];
    if (kind !== 'T') continue;                     // 只要文本段（真函数）
    map.set(parts[parts.length - 1], { file: 'libwpfwic.so', body: 'x;' });
  }
  return map;
}

// ---------- 5. 组装行 ----------
const SO_IMPL = {
  'libwpfwin32.so': parseShimExports(),
  'libwpfgfx_linux.so': parseMilcoreExports(),
  'libwpfwic.so': parseWicExports(),
};

function statusFor(so, symbol) {
  const impl = SO_IMPL[so];
  if (!impl) return 'gap';
  if (impl instanceof Map && impl.has(symbol)) {
    // 两种情况：shim 记录是 {file, body}，milcore 记录是 depth 字符串
    const rec = impl.get(symbol);
    if (typeof rec === 'string') return rec === 'NotImpl' ? 'gap' : 'impl';
    return isStub(rec.body) ? 'stub' : 'impl';
  }
  return 'gap';
}

const decls = parseDeclarations();
const rows = new Map(); // `${so}\t${symbol}` -> row

function put(row) {
  const key = `${row.lib}\t${row.symbol}`;
  const prev = rows.get(key);
  if (!prev) {
    rows.set(key, row);
    return;
  }
  const rank = { impl: 3, stub: 2, gap: 1 };
  if (rank[row.status] > rank[prev.status]) rows.set(key, row);
}

for (const d of decls) {
  const [so, owner] = LIBS[d.lib] || ['libwpfwin32.so', 'MOD-02'];
  if (!LIBS[d.lib] && !d.lib.startsWith('api-ms-win-')) warnings.push(`库未登记落点，按默认处理：${d.lib}`);
  const status = statusFor(so, d.symbol);
  put({
    lib: so,
    symbol: d.symbol,
    status,
    owner,
    since: SINCE,
    note: status === 'gap' ? (LIB_NOTES[d.lib] || `无实现（${d.lib}）`) : '',
  });
}

// shim 额外导出（上游声明里没有的符号）：按 impl 登记，note 说明
const declaredBySo = new Map();
for (const d of decls) {
  const [so] = LIBS[d.lib] || ['libwpfwin32.so'];
  if (!declaredBySo.has(so)) declaredBySo.set(so, new Set());
  declaredBySo.get(so).add(d.symbol);
}
for (const [name, rec] of SO_IMPL['libwpfwin32.so']) {
  if (declaredBySo.get('libwpfwin32.so')?.has(name)) continue;
  put({
    lib: 'libwpfwin32.so',
    symbol: name,
    status: isStub(rec.body) ? 'stub' : 'impl',
    owner: 'MOD-02',
    since: SINCE,
    note: 'shim 额外导出（上游未声明）',
  });
}

// milcore 导出清单里上游未声明的名字（如新增的 MilFontFace_RegisterFromFile）
for (const [name, depth] of SO_IMPL['libwpfgfx_linux.so']) {
  if (declaredBySo.get('libwpfgfx_linux.so')?.has(name)) continue;
  put({
    lib: 'libwpfgfx_linux.so',
    symbol: name,
    status: depth === 'NotImpl' ? 'gap' : 'impl',
    owner: 'MOD-25',
    since: SINCE,
    note: depth === 'NotImpl' ? 'milcore 登记 NotImpl（E_NOTIMPL）' : 'milcore 导出清单（上游未声明）',
  });
}

// ---------- 6. 输出 ----------
const cmp = (x, y) => (x < y ? -1 : x > y ? 1 : 0);
const sorted = [...rows.values()].sort((a, b) => (a.lib === b.lib ? cmp(a.symbol, b.symbol) : cmp(a.lib, b.lib)));
const byStatus = { impl: 0, stub: 0, gap: 0 };
const byLib = {};
for (const r of sorted) {
  byStatus[r.status] += 1;
  byLib[r.lib] = (byLib[r.lib] || 0) + 1;
  if (!r.symbol || /\s/.test(r.symbol)) errors.push(`符号名非法：${JSON.stringify(r.symbol)}`);
  if (!/^MOD-\d{2}$/.test(r.owner)) errors.push(`owner 非模块编号：${r.owner}（${r.symbol}）`);
  if (r.note.includes('\t')) errors.push(`note 含制表符：${r.symbol}`);
}

const header = [
  '# exports.txt — TBL-001 出口清单（IF-006 契约）',
  `# 生成器：50-verify/contract/gen-exports.mjs（勿手改；改生成器或实现后重跑）`,
  `# 列：lib\tsymbol\tstatus\towner\tsince[\tnote]（note 为可选列；为空时省略，行尾不留制表符）`,
  `# status 判定：impl=实现侧有该符号；stub=函数体仅常量返回；gap=声明有实现无（note 写替代路径）`,
  `# lib 列：若实现将落在哪个自产 .so（概设 §2.4）；status=gap 表示当前无实现，不是没登记`,
  `# 规模：${sorted.length} 行（impl ${byStatus.impl} / stub ${byStatus.stub} / gap ${byStatus.gap}）`,
  `# 输入：上游 ${decls.length} 条 DllImport 声明（另有 ${skipped.length} 条属性块后无 extern 声明被跳过）、native-shim ${SO_IMPL['libwpfwin32.so'].size} 个导出、milcore ${SO_IMPL['libwpfgfx_linux.so'].size} 个导出`,
  '# 排除：wpf_*（shim 内部件）、WpfLinuxWin32_*（桥件，非 Win32 契约入口）',
];
const body = sorted.map((r) => {
  const cells = [r.lib, r.symbol, r.status, r.owner, r.since];
  if (r.note) cells.push(r.note); // note 可选：为空时省略该列，行尾不留制表符
  return cells.join('\t');
});
const content = `${[...header, ...body].join('\n')}\n`;

if (process.argv.includes('--check')) {
  const prev = fs.existsSync(OUT) ? read(OUT) : '';
  if (prev !== content) {
    console.error(`ERROR exports.txt 与生成结果不一致（${path.relative(ROOT, OUT)}）；重跑生成器`);
    process.exit(1);
  }
  console.log(`gen-exports: --check 通过（${sorted.length} 行）`);
} else {
  fs.writeFileSync(OUT, content, 'utf8');
  console.log(`gen-exports: 写出 ${sorted.length} 行（impl ${byStatus.impl} / stub ${byStatus.stub} / gap ${byStatus.gap}）`);
}
for (const r of Object.entries(byLib).sort()) console.log(`  ${r[0]}\t${r[1]}`);
if (skipped.length) console.warn(`WARN 跳过 ${skipped.length} 条无 extern 的 DllImport 属性块，首条：${skipped[0]}`);
for (const w of warnings.slice(0, 10)) console.warn(`WARN ${w}`);
if (warnings.length > 10) console.warn(`WARN ...另有 ${warnings.length - 10} 条同类警告`);
if (errors.length) {
  for (const e of errors.slice(0, 20)) console.error(`ERROR ${e}`);
  process.exit(1);
}
