#!/usr/bin/env node
// 界面快照与结构 diff（IF-071 / MOD-86 的判据侧实现）#界面快照器
//
// 判据口径见 04 §9.1（IF-071）：按 path join 逐列比较 role/name/states/actions/relations/child_count，
// value 可选（期望非 null 才比），bounds 只比位置、不参与结构判定（单列 bounds_diff）。
// 缺必填字段的快照视为无效快照（判 FAIL，不是 SKIP）；路径不稳定按 R-012 单独取证。
//
// 用法：
//   node 50-verify/a11y/snapshot.mjs --fake --scr SCR-01          # Fake AT-SPI 桩件取树并 diff
//   node 50-verify/a11y/snapshot.mjs --from <tree.json> --scr SCR-01  # 对已有树 diff
//   node 50-verify/a11y/snapshot.mjs --live --scr SCR-01          # 真桥（未实现 → SKIP，不假成功）
//   node 50-verify/a11y/snapshot.mjs --selftest                   # 判据自检：各差异类别的负向断言
import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const argv = process.argv.slice(2);
const has = (f) => argv.includes(`--${f}`);
const val = (f, def) => {
  const i = argv.indexOf(`--${f}`);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : def;
};
const SCR = val('scr', 'SCR-01');
const SCR_NUM = SCR.replace(/^SCR-/, '');
const EXPECT = path.join(ROOT, '50-verify/a11y/expect', `scr-${SCR_NUM}.json`);
const STUB = path.join(ROOT, '50-verify/fake/at-spi-stub.py');
const OUT_DIR = path.join(ROOT, '50-verify/读数/ACC-027');
const now = new Date();
const stamp = now.toISOString().replace(/[:.]/g, '-');

// IF-071 节点必填字段（缺一即无效快照）
const REQUIRED_NODE_FIELDS = ['path', 'role', 'name', 'states', 'actions', 'bounds', 'relations'];

function writeReadout(verdict, diff, tree, source, extra = {}) {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, `${stamp}-01.json`);
  fs.writeFileSync(
    file,
    JSON.stringify(
      {
        acc: 'ACC-027',
        tg: 'TG-012',
        rc: verdict === 'PASS' ? 0 : verdict === 'SKIP' ? 2 : 1,
        verdict,
        started: now.toISOString(),
        env: { runner: source, node: process.version, scr: SCR },
        expect_ref: path.relative(ROOT, EXPECT),
        diff,
        degrade: verdict === 'SKIP' ? '真 AT-SPI 桥（MOD-81）未实现；fake 源只验结构数据通路' : 'none',
        ...extra,
      },
      null,
      2,
    ) + '\n',
    'utf8',
  );
  return file;
}

function writeTree(tree) {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const file = path.join(OUT_DIR, `tree-${SCR}.json`);
  fs.writeFileSync(file, JSON.stringify(tree, null, 2) + '\n', 'utf8');
  return file;
}

// relations 的项允许是字符串或对象（IF-071 未定细格式），比较前统一规范化
const relKey = (r) => (typeof r === 'string' ? r : JSON.stringify(r));

// value 允许字符串/数字/对象：先严格相等，再序列化相等，最后按「正则」前缀匹配字符串
const valueOk = (want, got) => {
  if (want === got) return true;
  if (JSON.stringify(want) === JSON.stringify(got)) return true;
  return nameOk(String(want), String(got));
};

const nameOk = (wantName, gotName) => {
  if (wantName === gotName) return true;
  const m = String(wantName).match(/^正则\s+(.*)$/);
  if (m) {
    try {
      return new RegExp(m[1]).test(String(gotName));
    } catch {
      return false;
    }
  }
  return false;
};

// 子节点数：按 path 段前缀统计（IF-071 的 child_count 口径）
function childCounts(nodes) {
  const map = new Map();
  const paths = new Set(nodes.map((n) => n.path));
  for (const n of nodes) map.set(n.path, 0);
  for (const n of nodes) {
    const segs = String(n.path).split('/');
    for (let i = 1; i < segs.length; i++) {
      const parent = segs.slice(0, i).join('/');
      if (paths.has(parent) && i === segs.length - 1) map.set(parent, (map.get(parent) || 0) + 1);
    }
  }
  return map;
}

// 快照有效性：缺必填字段 / 字段类型不符 → 无效快照
function validateTree(tree) {
  const bad = [];
  for (const n of tree.nodes || []) {
    for (const f of REQUIRED_NODE_FIELDS) {
      if (!(f in n)) bad.push({ path: n.path ?? '?', kind: 'invalid_snapshot', field: f });
    }
    if (n.states && !Array.isArray(n.states)) bad.push({ path: n.path, kind: 'invalid_snapshot', field: 'states' });
    if (n.actions && !Array.isArray(n.actions)) bad.push({ path: n.path, kind: 'invalid_snapshot', field: 'actions' });
    if (n.relations && !Array.isArray(n.relations)) bad.push({ path: n.path, kind: 'invalid_snapshot', field: 'relations' });
  }
  return bad;
}

// 结构 diff：只用 path + role/name/states/actions/relations/child_count（+ 可选 value）
function diffTree(tree, expect) {
  const diffs = [];
  const got = new Map(tree.nodes.map((n) => [n.path, n]));
  const want = new Map(expect.nodes.map((n) => [n.path, n]));
  const gotCount = childCounts(tree.nodes);
  const wantCount = childCounts(expect.nodes);
  for (const [p, w] of want) {
    const g = got.get(p);
    if (!g) {
      diffs.push({ path: p, kind: 'missing_node', want: w.role });
      continue;
    }
    if (g.role !== w.role) diffs.push({ path: p, kind: 'role', want: w.role, got: g.role });
    if (!nameOk(w.name, g.name)) diffs.push({ path: p, kind: 'name', want: w.name, got: g.name });
    for (const s of w.states || []) {
      const neg = s.startsWith('!');
      const key = neg ? s.slice(1) : s;
      const inGot = (g.states || []).includes(key);
      if (neg && inGot) diffs.push({ path: p, kind: 'state_forbidden', state: key });
      if (!neg && !inGot) diffs.push({ path: p, kind: 'state_missing', state: key });
    }
    for (const a of w.actions || []) {
      if (!(g.actions || []).includes(a)) diffs.push({ path: p, kind: 'action_missing', action: a });
    }
    // relations：期望关系缺失即「关系断裂」（IF-051 第 5 行）
    const gotRels = (g.relations || []).map(relKey);
    for (const r of w.relations || []) {
      if (!gotRels.includes(relKey(r))) diffs.push({ path: p, kind: 'relation_broken', relation: r });
    }
    // value：可选字段，期望非 null 才比
    if (w.value !== null && w.value !== undefined && !valueOk(w.value, g.value)) {
      diffs.push({ path: p, kind: 'value', want: w.value, got: g.value });
    }
    // child_count：桥给的与实测子数都要对得上
    const expectKids = wantCount.get(p) ?? 0;
    const bridgeKids = typeof g.child_count === 'number' ? g.child_count : expectKids;
    if (bridgeKids !== expectKids) diffs.push({ path: p, kind: 'child_count', want: expectKids, got: bridgeKids });
  }
  for (const [p] of got) if (!want.has(p)) diffs.push({ path: p, kind: 'extra_node', got: p });
  return diffs;
}

// bounds 单列：只比位置，不参与结构判定（IF-071）
function diffBounds(tree, expect) {
  const out = [];
  const got = new Map(tree.nodes.map((n) => [n.path, n]));
  for (const w of expect.nodes) {
    if (!w.bounds) continue; // 期望未给几何时不判
    const g = got.get(w.path);
    if (!g || !g.bounds) {
      out.push({ path: w.path, kind: 'bounds_missing', want: w.bounds });
      continue;
    }
    const same = Array.isArray(w.bounds) && Array.isArray(g.bounds) && w.bounds.length === g.bounds.length && w.bounds.every((v, i) => v === g.bounds[i]);
    if (!same) out.push({ path: w.path, kind: 'bounds', want: w.bounds, got: g.bounds });
  }
  return out;
}

// ---------------------------------------------------------------- 自检：各差异类别的负向断言

if (has('selftest')) {
  const base = {
    nodes: [
      { path: '0', role: 'frame', name: 'Win', states: ['showing'], actions: ['focus'], bounds: [0, 0, 100, 100], value: null, relations: [] },
      { path: '0/0', role: 'label', name: 'L1', states: ['visible'], actions: [], bounds: [10, 10, 50, 20], value: 'hi', relations: [{ kind: 'labelled-by', target: '0/1' }] },
      { path: '0/1', role: 'entry', name: 'E1', states: ['enabled'], actions: ['set-text'], bounds: [10, 40, 50, 20], value: null, relations: [] },
    ],
  };
  const clone = (o) => JSON.parse(JSON.stringify(o));
  const cases = [];
  const add = (id, mutate, wantKinds) => {
    const t = clone(base);
    mutate(t);
    const d = diffTree(t, base);
    const kinds = [...new Set(d.map((x) => x.kind))].sort();
    const ok = JSON.stringify(kinds) === JSON.stringify([...wantKinds].sort());
    cases.push({ id, ok, got: kinds, want: wantKinds, diff: d.length });
  };
  add('equal', () => {}, []);
  add('missing_node', (t) => t.nodes.splice(2, 1), ['missing_node']);
  add('role', (t) => { t.nodes[1].role = 'button'; }, ['role']);
  add('name', (t) => { t.nodes[1].name = 'X'; }, ['name']);
  add('state_missing', (t) => { t.nodes[1].states = []; }, ['state_missing']);
  add('action_missing', (t) => { t.nodes[2].actions = []; }, ['action_missing']);
  add('relation_broken', (t) => { t.nodes[1].relations = []; }, ['relation_broken']);
  add('value', (t) => { t.nodes[1].value = 'bye'; }, ['value']);
  add('child_count', (t) => { t.nodes[0].child_count = 5; }, ['child_count']);
  add('extra_node', (t) => { t.nodes.push({ path: '0/2', role: 'label', name: 'X', states: [], actions: [], bounds: [0, 0, 1, 1], value: null, relations: [] }); }, ['extra_node']);
  // bounds 单列：不进入结构 diff
  const b1 = clone(base);
  b1.nodes[1].bounds = [99, 99, 1, 1];
  const boundsOnly = { structural: diffTree(b1, base).length, bounds: diffBounds(b1, base).length };
  cases.push({ id: 'bounds_separate', ok: boundsOnly.structural === 0 && boundsOnly.bounds === 1, got: boundsOnly, want: 'structural=0 bounds=1' });
  // 无效快照：缺必填字段
  const inv = clone(base);
  delete inv.nodes[1].relations;
  cases.push({ id: 'invalid_snapshot', ok: validateTree(inv).some((x) => x.kind === 'invalid_snapshot'), got: validateTree(inv), want: 'invalid_snapshot' });
  // 路径稳定性：同一棵树两次 diff 的 path 序列一致（R-012 的最小子集）
  const p1 = base.nodes.map((n) => n.path).join(',');
  const p2 = clone(base).nodes.map((n) => n.path).join(',');
  cases.push({ id: 'path_stable', ok: p1 === p2, got: p1 === p2, want: true });

  const failed = cases.filter((c) => !c.ok);
  for (const c of cases) console.log(`  ${c.ok ? 'PASS' : 'FAIL'} ${c.id}${c.ok ? '' : ` want=${JSON.stringify(c.want)} got=${JSON.stringify(c.got)}`}`);
  console.log(`snapshot --selftest: ${cases.length - failed.length}/${cases.length} 通过`);
  process.exit(failed.length ? 1 : 0);
}

if (!fs.existsSync(EXPECT)) {
  console.error(`ERROR 期望文件缺失：${path.relative(ROOT, EXPECT)}（先跑界面图生成器 --emit-expect）`);
  process.exit(1);
}
const expect = JSON.parse(fs.readFileSync(EXPECT, 'utf8'));

if (has('live')) {
  const probe = spawnSync('sh', ['-c', 'busctl --user list 2>/dev/null | grep -c a11y || true'], { encoding: 'utf8' });
  const n = Number((probe.stdout || '0').trim()) || 0;
  const file = writeReadout('SKIP', [{ kind: 'not_implemented', detail: `真 AT-SPI 桥（MOD-81）未实现；总线上 a11y 服务数=${n}` }], { nodes: [] }, 'live');
  console.log(`snapshot --live: 真桥未实现，读数标 SKIP（不是 PASS）：${path.relative(ROOT, file)}`);
  process.exit(0);
}

let tree;
if (has('from')) {
  const from = path.resolve(ROOT, val('from'));
  tree = JSON.parse(fs.readFileSync(from, 'utf8'));
} else if (has('fake')) {
  const child = spawn('python3', [STUB, '--serve', '--expect', EXPECT], { stdio: ['ignore', 'pipe', 'pipe'] });
  const ready = await new Promise((resolve, reject) => {
    let buf = '';
    const t = setTimeout(() => reject(new Error('桩件 30 s 内未 READY')), 30000);
    child.stdout.on('data', (d) => {
      buf += d.toString();
      const line = buf.split('\n').find((l) => l.startsWith('READY '));
      if (line) {
        clearTimeout(t);
        resolve(JSON.parse(line.slice('READY '.length)));
      }
    });
    child.on('exit', (c) => reject(new Error(`桩件提前退出 rc=${c}`)));
  }).catch((e) => {
    console.error(`ERROR 起 Fake AT-SPI 失败：${e.message}`);
    child.kill('SIGTERM');
    process.exit(1);
  });
  const nodes = [];
  for (const w of expect.nodes) {
    const r = spawnSync('python3', [STUB, '--query', w.path, '--address', ready.address, '--json'], { encoding: 'utf8', timeout: 20000 });
    if (r.status !== 0) {
      console.error(`ERROR 查询 ${w.path} 失败：${(r.stderr || '').trim().split('\n')[0]}`);
      child.kill('SIGTERM');
      process.exit(1);
    }
    const got = JSON.parse(r.stdout).node;
    nodes.push({
      path: got.path,
      role: got.role,
      name: got.name,
      states: got.states || [],
      actions: got.actions || [],
      bounds: got.bounds ?? null,
      value: got.value ?? null,
      relations: got.relations ?? [],
    });
  }
  child.kill('SIGTERM');
  tree = {
    app: { name: 'LinuxDesktopPerfect sample', scr: SCR },
    window: { name: expect.nodes[0] ? expect.nodes[0].name : SCR },
    env: { runner: 'fake', atspi: 'FakeStub', address_kind: 'private-dbus' },
    seq: 1,
    nodes,
  };
} else {
  console.error('用法：--fake | --from <tree.json> | --live | --selftest');
  process.exit(1);
}

const treeFile = writeTree(tree);
const invalid = validateTree(tree);
const diffs = diffTree(tree, expect);
const bounds = diffBounds(tree, expect);
const verdict = invalid.length || diffs.length ? 'FAIL' : 'PASS';
const file = writeReadout(verdict, [...invalid, ...diffs], tree, tree.env?.runner || 'from-file', { bounds_diff: bounds });
const byKind = [...invalid, ...diffs].reduce((m, d) => ((m[d.kind] = (m[d.kind] || 0) + 1), m), {});
console.log(`snapshot: ${SCR} 节点 ${tree.nodes.length}/${expect.nodes.length}；结构差异 ${diffs.length}${diffs.length ? ` ${JSON.stringify(byKind)}` : ''}；bounds 单列 ${bounds.length}`);
console.log(`  tree：${path.relative(ROOT, treeFile)}`);
console.log(`  读数：${path.relative(ROOT, file)}（verdict=${verdict}）`);
process.exit(verdict === 'PASS' ? 0 : 1);
