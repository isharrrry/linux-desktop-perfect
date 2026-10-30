#!/usr/bin/env node
// 界面快照与结构 diff（IF-071 的判据侧实现）#界面快照器
// 取一棵 AT-SPI 树（当前只有 fake 源可用），按《界面设计说明》元素属性表逐节点比 role/name/states/actions，落读数。
// 用法：
//   node 50-verify/a11y/snapshot.mjs --fake --scr SCR-01        # 起 Fake AT-SPI 桩件取树并 diff
//   node 50-verify/a11y/snapshot.mjs --from <tree.json> --scr SCR-01   # 对已有树 diff
//   node 50-verify/a11y/snapshot.mjs --live --scr SCR-01        # 真桥（未实现 → SKIP，不假成功）
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

function writeReadout(verdict, diff, tree, source) {
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

function diffTree(tree, expect) {
  const diffs = [];
  const got = new Map(tree.nodes.map((n) => [n.path, n]));
  const want = new Map(expect.nodes.map((n) => [n.path, n]));
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
  }
  for (const [p] of got) if (!want.has(p)) diffs.push({ path: p, kind: 'extra_node', got: p });
  return diffs;
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
  console.error('用法：--fake | --from <tree.json> | --live');
  process.exit(1);
}

const treeFile = writeTree(tree);
const diffs = diffTree(tree, expect);
const verdict = diffs.length ? 'FAIL' : 'PASS';
const file = writeReadout(verdict, diffs, tree, tree.env?.runner || 'from-file');
const byKind = diffs.reduce((m, d) => ((m[d.kind] = (m[d.kind] || 0) + 1), m), {});
console.log(`snapshot: ${SCR} 节点 ${tree.nodes.length}/${expect.nodes.length}；差异 ${diffs.length}${diffs.length ? ` ${JSON.stringify(byKind)}` : ''}`);
console.log(`  tree：${path.relative(ROOT, treeFile)}`);
console.log(`  读数：${path.relative(ROOT, file)}（verdict=${verdict}）`);
process.exit(verdict === 'PASS' ? 0 : 1);
