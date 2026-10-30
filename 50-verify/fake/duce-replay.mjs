#!/usr/bin/env node
// Fake 帧源 —— DUCE 命令流回放/录制桩件（#Fake 帧源）
//
// 作用：把「一条 DUCE 命令流」变成可离线重放的字节文件，让渲染后端（MOD-21/23）能脱开 WPF 真实布局单独判据，
//       并给 TG-003 的「命令覆盖 118/118」提供第一批可回放输入。
//
// 用法：
//   node 50-verify/fake/duce-replay.mjs --check
//   node 50-verify/fake/duce-replay.mjs --make-synthetic /tmp/duce.bin [--layout 40-build/milcore/DUCE-LAYOUT.csv]
//   node 50-verify/fake/duce-replay.mjs --decode /tmp/duce.bin [--json]
//   node 50-verify/fake/duce-replay.mjs --self-test
//
// 合成流格式（FAKE-FORMAT v1，**不是**真实 DUCE ABI）：
//   magic  "FAKEDUCE" (8B) | u8 version=1 | u32le count | 重复 { u32le id | u32le block_len | block_len 字节零填充 }
//   真实 DUCE 的命令头与参数块布局见 04 §3.1（IF-011），当前状态「待对拍确认」；本格式只服务桩件与解码器自检，
//   任何「合成流能解码」的结论都不得写成「真实 DUCE 已支持」。
//
// 已知偏差：
//   1. 参数块是零填充，不含真实字段值；解码只验证 id/长度/结构，不解释字段语义。
//   2. 布局表 DUCE-LAYOUT.csv 缺失时一律用 --default-block（默认 8），并打印 NOTE；不静默假设长度。
//   3. 真实命令 id 不连续：0x00~0x3d + 0x57~0x8e，缺口 0x3e~0x56 共 25 个（`--check` 会打印并登记为「未使用 id」）。
//      「0x00~0x75 连续」的假设与命令表不符，需要时用 `--strict-contiguous 0x00-0x75` 显式验证（会红）。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const MAGIC = 'FAKEDUCE';
const VERSION = 1;
const MAX_BLOCK = 1 << 20;

function die(msg, code = 1) {
  console.error(`ERROR ${msg}`);
  process.exit(code);
}

function note(msg) {
  console.log(`NOTE ${msg}`);
}

function parseArgs(argv) {
  const out = {
    commands: '40-build/milcore/DUCE-COMMANDS.txt',
    layout: '40-build/milcore/DUCE-LAYOUT.csv',
    defaultBlock: 8,
    expectCount: 118,
    mode: null,
    file: null,
    json: false,
    strict: null,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    const next = () => {
      const v = argv[i + 1];
      if (v === undefined || v.startsWith('--')) throw new Error(`${a} 缺少取值`);
      i += 1;
      return v;
    };
    if (a === '--commands') out.commands = next();
    else if (a === '--layout') out.layout = next();
    else if (a === '--default-block') out.defaultBlock = Number(next());
    else if (a === '--expect-count') out.expectCount = Number(next());
    else if (a === '--make-synthetic') { out.mode = 'make'; out.file = next(); }
    else if (a === '--decode') { out.mode = 'decode'; out.file = next(); }
    else if (a === '--check') out.mode = 'check';
    else if (a === '--self-test') out.mode = 'self';
    else if (a === '--strict-contiguous') out.strict = next();
    else if (a === '--json') out.json = true;
    else if (a === '--help' || a === '-h') { console.log('见文件头注释'); process.exit(0); }
    else throw new Error(`未知参数：${a}`);
  }
  if (!Number.isInteger(out.defaultBlock) || out.defaultBlock < 0 || out.defaultBlock > MAX_BLOCK) {
    throw new Error(`--default-block 非法：${out.defaultBlock}`);
  }
  return out;
}

function parseId(text) {
  const s = String(text).trim();
  if (/^0x[0-9a-f]+$/i.test(s)) return parseInt(s, 16);
  if (/^\d+$/.test(s)) return Number(s);
  return null;
}

function readCommands(file) {
  if (!fs.existsSync(file)) throw new Error(`命令表不存在：${file}`);
  const list = [];
  for (const [idx, line] of fs.readFileSync(file, 'utf8').split('\n').entries()) {
    const t = line.trim();
    if (!t || t.startsWith('#')) continue;
    const m = /^(0x[0-9a-fA-F]+|\d+)\s+(\S+)$/.exec(t);
    if (!m) throw new Error(`命令表第 ${idx + 1} 行无法解析：${t.slice(0, 60)}`);
    list.push({ id: parseId(m[1]), name: m[2] });
  }
  if (!list.length) throw new Error(`命令表为空：${file}`);
  return list;
}

function readLayout(file) {
  if (!fs.existsSync(file)) return null;
  const map = new Map();
  for (const [idx, line] of fs.readFileSync(file, 'utf8').split('\n').entries()) {
    const t = line.trim();
    if (!t || t.startsWith('#')) continue;
    const cols = t.split(',').map((c) => c.trim());
    if (cols.length < 2) continue;
    const id = parseId(cols[0]);
    if (id === null) continue; // 表头或注释列，跳过不算错
    const len = Number(cols[1]);
    if (!Number.isInteger(len) || len < 0 || len > MAX_BLOCK) {
      throw new Error(`布局表第 ${idx + 1} 行块长非法：${t.slice(0, 60)}`);
    }
    map.set(id, len);
  }
  return map;
}

function blockLenFor(id, layout, fallback) {
  return layout && layout.has(id) ? layout.get(id) : fallback;
}

function makeSynthetic(file, opts) {
  const commands = readCommands(opts.commands);
  const layout = readLayout(opts.layout);
  if (!layout) note(`布局表缺失（${opts.layout}）：118 条命令全部用 --default-block=${opts.defaultBlock}`);
  const chunks = [Buffer.from(MAGIC, 'ascii'), Buffer.from([VERSION])];
  const countBuf = Buffer.alloc(4);
  countBuf.writeUInt32LE(commands.length, 0);
  chunks.push(countBuf);
  let total = 0;
  for (const c of commands) {
    const len = blockLenFor(c.id, layout, opts.defaultBlock);
    const head = Buffer.alloc(8);
    head.writeUInt32LE(c.id, 0);
    head.writeUInt32LE(len, 4);
    chunks.push(head, Buffer.alloc(len));
    total += len;
  }
  const buf = Buffer.concat(chunks);
  fs.mkdirSync(path.dirname(path.resolve(file)), { recursive: true });
  fs.writeFileSync(file, buf);
  return {
    file,
    commands: commands.length,
    block_bytes: total,
    bytes: buf.length,
    layout: layout ? `${layout.size} 条命令有布局` : 'none(default-block)',
  };
}

function decode(file, opts) {
  if (!fs.existsSync(file)) throw new Error(`流文件不存在：${file}`);
  const buf = fs.readFileSync(file);
  if (buf.length < 13) throw new Error(`流太短（${buf.length} 字节），不足以容纳头`);
  if (buf.slice(0, 8).toString('ascii') !== MAGIC) {
    throw new Error(`magic 不符：期望 ${MAGIC}，实际 ${JSON.stringify(buf.slice(0, 8).toString('ascii'))}`);
  }
  if (buf[8] !== VERSION) throw new Error(`版本不符：期望 ${VERSION}，实际 ${buf[8]}`);
  const count = buf.readUInt32LE(9);
  const commands = new Map(readCommands(opts.commands).map((c) => [c.id, c.name]));
  const records = [];
  let off = 13;
  for (let i = 0; i < count; i += 1) {
    if (off + 8 > buf.length) throw new Error(`第 ${i} 条记录头越界（offset=${off}, len=${buf.length}）`);
    const id = buf.readUInt32LE(off);
    const blockLen = buf.readUInt32LE(off + 4);
    if (blockLen > MAX_BLOCK) throw new Error(`第 ${i} 条记录块长非法：${blockLen}`);
    if (off + 8 + blockLen > buf.length) throw new Error(`第 ${i} 条记录参数块越界（offset=${off}, block=${blockLen}）`);
    const name = commands.get(id);
    if (!name) throw new Error(`第 ${i} 条记录 id=${id} 不在命令表（未知命令，拒绝解码）`);
    records.push({ index: i, id, id_hex: `0x${id.toString(16).padStart(2, '0')}`, name, block_len: blockLen });
    off += 8 + blockLen;
  }
  if (off !== buf.length) throw new Error(`尾部多余 ${buf.length - off} 字节（结构不符，拒绝解码）`);
  return { file, count, records };
}

function parseStrictRange(text) {
  const m = /^(0x[0-9a-fA-F]+|\d+)\s*-\s*(0x[0-9a-fA-F]+|\d+)$/.exec(String(text).trim());
  if (!m) throw new Error(`--strict-contiguous 需要 A-B 形式，收到 ${text}`);
  return [parseId(m[1]), parseId(m[2])];
}

function gapRanges(ids) {
  const gaps = [];
  let prev = -1;
  for (const id of ids) {
    if (id !== prev + 1) gaps.push([prev + 1, id - 1]);
    prev = id;
  }
  return gaps;
}

function check(opts) {
  const commands = readCommands(opts.commands);
  const ids = commands.map((c) => c.id);
  const problems = [];
  if (commands.length !== opts.expectCount) problems.push(`命令条数 ${commands.length} ≠ ${opts.expectCount}`);
  const seen = new Set();
  for (const c of commands) {
    if (seen.has(c.id)) problems.push(`id 重复：0x${c.id.toString(16)}（${c.name}）`);
    seen.add(c.id);
  }
  for (let i = 1; i < ids.length; i += 1) {
    if (ids[i] <= ids[i - 1]) problems.push(`id 未严格升序：0x${ids[i - 1].toString(16)} → 0x${ids[i].toString(16)}`);
  }
  for (const c of commands) {
    if (c.id < 0 || c.id > 0xff) problems.push(`id 越界 0x${c.id.toString(16)}（只允许单字节 id）`);
    if (!c.name || /\s/.test(c.name)) problems.push(`名称非法：${JSON.stringify(c.name)}`);
  }
  const gaps = gapRanges(ids);
  if (opts.strict) {
    const [a, b] = parseStrictRange(opts.strict);
    for (let i = a; i <= b; i += 1) if (!seen.has(i)) problems.push(`--strict-contiguous 缺口：0x${i.toString(16)}`);
  }
  const gapText = gaps.map(([a, b]) => (a === b ? `0x${a.toString(16)}` : `0x${a.toString(16)}..0x${b.toString(16)}`)).join(' ');
  console.log(`CHECK commands=${commands.length} first=0x${ids[0].toString(16)} last=0x${ids[ids.length - 1].toString(16)} gaps=${gaps.length}${gapText ? ` [${gapText}]` : ''}`);
  for (const [a, b] of gaps) {
    console.log(`GAP 0x${a.toString(16)}..0x${b.toString(16)}（${b - a + 1} 个未使用 id，登记为缺口，不算错误）`);
  }
  if (!opts.strict && gaps.length) {
    note('真实命令表 id 上限 0x8e 且存在缺口，与「0x00~0x75 连续」的假设不符；本检查以「无重复 + 严格升序 + 118 条 + 缺口登记」为准，严格连续请用 --strict-contiguous 显式验证（预期红）');
  }
  for (const p of problems) console.error(`ERROR ${p}`);
  console.log(`duce-replay check: ${problems.length} 问题`);
  return problems.length === 0;
}

function selfTest() {
  const checks = [];
  const check1 = (name, cond, detail = '') => checks.push({ name, ok: Boolean(cond), detail });
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'duce-fake-'));
  const opts = { commands: '40-build/milcore/DUCE-COMMANDS.txt', layout: '40-build/milcore/DUCE-LAYOUT.csv', defaultBlock: 8, expectCount: 118 };
  try {
    const commands = readCommands(opts.commands);
    check1('命令表可解析为 118 条', commands.length === 118, `实际 ${commands.length}`);

    // 1) 结构检查（真实命令表）
    const okCheck = check({ ...opts });
    check1('--check 在真实命令表上通过', okCheck === true);

    // 2) 合成 → 解码回环
    const out = path.join(tmp, 'synthetic.bin');
    const made = makeSynthetic(out, { ...opts, layout: path.join(tmp, 'no-layout.csv') });
    check1('合成流写出', fs.existsSync(out) && made.commands === 118, JSON.stringify({ bytes: made.bytes, commands: made.commands }));
    const dec = decode(out, opts);
    check1('解码条数 = 118', dec.count === 118, String(dec.count));
    check1('解码 id 序列与命令表一致', JSON.stringify(dec.records.map((r) => r.id)) === JSON.stringify(commands.map((c) => c.id)));
    check1('默认块长 8 生效', dec.records.every((r) => r.block_len === 8), `首条 block_len=${dec.records[0].block_len}`);
    check1('输出字段名对齐 IF-051/IF-071（path/role 之外的 id/name/block_len）', ['id', 'name', 'block_len'].every((k) => k in dec.records[0]), Object.keys(dec.records[0]).join(','));

    // 3) 布局表覆盖：两条命令给非默认块长
    const layoutFile = path.join(tmp, 'layout.csv');
    fs.writeFileSync(layoutFile, 'id,block_len\n0x00,4\n0x01,12\n', 'utf8');
    const out2 = path.join(tmp, 'synthetic-layout.bin');
    makeSynthetic(out2, { ...opts, layout: layoutFile });
    const dec2 = decode(out2, opts);
    check1('布局表覆盖块长（0x00=4, 0x01=12）', dec2.records[0].block_len === 4 && dec2.records[1].block_len === 12, `${dec2.records[0].block_len}/${dec2.records[1].block_len}`);
    check1('未给布局的命令仍用默认块长', dec2.records[2].block_len === 8, String(dec2.records[2].block_len));

    // 4) 负向：magic 坏、截断、未知 id 都必须显式失败
    const badMagic = path.join(tmp, 'bad-magic.bin');
    const raw = fs.readFileSync(out2);
    const mut = Buffer.from(raw);
    mut.write('XXXXXXXX', 0, 'ascii');
    fs.writeFileSync(badMagic, mut);
    let e1 = null;
    try { decode(badMagic, opts); } catch (e) { e1 = e.message; }
    check1('坏 magic 显式报错', Boolean(e1 && e1.includes('magic 不符')), String(e1));

    const truncated = path.join(tmp, 'truncated.bin');
    fs.writeFileSync(truncated, raw.slice(0, raw.length - 3));
    let e2 = null;
    try { decode(truncated, opts); } catch (e) { e2 = e.message; }
    check1('截断流显式报错', Boolean(e2 && /越界|尾部多余/.test(e2)), String(e2));

    const unknown = path.join(tmp, 'unknown-id.bin');
    const u = Buffer.from(raw);
    u.writeUInt32LE(0xfe, 13);
    fs.writeFileSync(unknown, u);
    let e3 = null;
    try { decode(unknown, opts); } catch (e) { e3 = e.message; }
    check1('未知 id 显式报错', Boolean(e3 && e3.includes('不在命令表')), String(e3));

    // 5) --check 的严格连续模式应当红（证明真实 id 不连续）
    const strictOk = check({ ...opts, strict: '0x00-0x75' });
    check1('--strict-contiguous 0x00-0x75 判红（真实缺口）', strictOk === false);
  } catch (e) {
    check1('自检未抛异常', false, e.message);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
  let failed = 0;
  for (const c of checks) {
    if (!c.ok) failed += 1;
    console.log(`${c.ok ? 'PASS' : 'FAIL'} ${c.name}${c.detail ? ` (${c.detail})` : ''}`);
  }
  console.log(`duce-replay self-test: ${checks.length - failed}/${checks.length} 通过`);
  process.exit(failed ? 1 : 0);
}

function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (e) {
    die(e.message, 2);
  }
  try {
    if (opts.mode === 'self') { selfTest(); return; }
    if (opts.mode === 'check') {
      process.exit(check(opts) ? 0 : 1);
      return;
    }
    if (opts.mode === 'make') {
      const info = makeSynthetic(opts.file, opts);
      if (opts.json) console.log(JSON.stringify(info, null, 2));
      else console.log(`MADE ${info.file} commands=${info.commands} block_bytes=${info.block_bytes} bytes=${info.bytes} layout=${info.layout}`);
      process.exit(0);
      return;
    }
    if (opts.mode === 'decode') {
      const dec = decode(opts.file, opts);
      if (opts.json) console.log(JSON.stringify(dec.records, null, 2));
      else for (const r of dec.records) console.log(JSON.stringify(r));
      console.log(`DECODED ${dec.file} count=${dec.count}`);
      process.exit(0);
      return;
    }
    die('缺少模式：--check / --make-synthetic <file> / --decode <file> / --self-test', 2);
  } catch (e) {
    die(e.message);
  }
}

main();
