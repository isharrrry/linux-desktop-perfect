#!/usr/bin/env node
// 自检：#链接检查器 / #编号检查器 / #覆盖检查器 的负向测试。
// 造违规 → 断言 rc=1 且逐类命中；清理 → 断言全仓 rc=0。改规则或改检查器后必跑。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '../..');
const TMP_LINKS = path.join(ROOT, '_selftest_links.md');
const TMP_IDS = path.join(ROOT, '_selftest_ids.md');
const TMP_MATRIX = path.join(ROOT, '_selftest_matrix.md');
const TMP_MATRIX_MAP = path.join(ROOT, '_selftest_matrix_map.md');

const assertions = [];
function check(name, cond, detail = '') {
  assertions.push({ name, ok: Boolean(cond), detail });
}

function run(script, args = []) {
  try {
    const target = path.isAbsolute(script) ? script : path.join(__dirname, script);
    const stdout = execFileSync(process.execPath, [target, ...args], {
      cwd: ROOT,
      encoding: 'utf8',
      stdio: 'pipe',
    });
    return { code: 0, out: stdout };
  } catch (e) {
    return { code: e.status, out: `${e.stdout || ''}${e.stderr || ''}` };
  }
}

// ---------- 1. 链接检查器：裸 .md 路径 / 裸行号 / 死链 ----------
fs.writeFileSync(
  TMP_LINKS,
  '见 30-design/概要设计.md 与 L123，另见 [死链](no-such-file.md)，以及 [《不存在的文档》](30-design/概要设计.md)。\n',
);
try {
  const dirty = run('check-links.mjs');
  check('负向（链接）：rc=1', dirty.code === 1, `实际 rc=${dirty.code}`);
  for (const key of ['裸 .md 路径', '裸行号', '链接目标不存在', '未登记文档名']) {
    check(`负向（链接）：命中「${key}」`, dirty.out.includes(key));
  }
} finally {
  fs.rmSync(TMP_LINKS, { force: true });
}

// ---------- 2. 编号检查器：模型外编号被引用即红 ----------
fs.writeFileSync(TMP_IDS, '此处引用 SM-999、MOD-99、IF-999、TBL-99 与 TG-999，全部不在模型内。\n');
try {
  const dirty = run('check-design-ids.mjs');
  check('负向（编号）：rc=1', dirty.code === 1, `实际 rc=${dirty.code}`);
  check('负向（编号）：命中「引用悬空」', dirty.out.includes('引用悬空'));
  for (const id of ['SM-999', 'MOD-99', 'IF-999', 'TG-999']) {
    check(`负向（编号）：命中模型外引用 ${id}`, dirty.out.includes(id));
  }
} finally {
  fs.rmSync(TMP_IDS, { force: true });
}

// ---------- 3. 覆盖检查器：矩阵缺行 / 重复 / 错编号即红 ----------
const matrixDoc = path.join(ROOT, '30-design/设计文档包/08-判据先行与界面检测设计说明.md');
if (fs.existsSync(matrixDoc)) {
  const text = fs.readFileSync(matrixDoc, 'utf8');
  const start = text.indexOf('## 4.');
  const end = text.indexOf('## 5.', start);
  const rows = text
    .slice(start, end)
    .split('\n')
    .filter((l) => /^\|\s*ACC-\d{3}\s*\|/.test(l));
  const broken = rows
    .filter((l) => !/^\|\s*ACC-031\s*\|/.test(l)) // 删掉 ACC-031（编号范围来自 model.ranges.ACC，删行必须报「矩阵缺行」）
    .map((l) => {
      if (/^\|\s*ACC-005\s*\|/.test(l)) return l.replace('MOD-01/05', 'MOD-99'); // 引用非法模块
      if (/^\|\s*ACC-007\s*\|/.test(l)) return l.replace('REQ-FN-007', 'REQ-FN-008'); // 需求错位
      return l;
    });
  const dup = rows.find((l) => /^\|\s*ACC-002\s*\|/.test(l));
  fs.writeFileSync(TMP_MATRIX, `## 4. 自检矩阵\n\n| ACC | REQ | 承接模块 | 测试门 | 样本界面 | 判据形式 | 读数落点 |\n|---|---|---|---|---|---|---|\n${broken.join('\n')}\n${dup}\n\n## 5. 自检结束\n`);
  try {
    const dirty = run('check-coverage.mjs', ['--matrix-file', '_selftest_matrix.md']);
    check('负向（覆盖）：rc=1', dirty.code === 1, `实际 rc=${dirty.code}`);
    for (const key of ['矩阵缺行', '矩阵重复', '矩阵引用非法', '矩阵对不上']) {
      check(`负向（覆盖）：命中「${key}」`, dirty.out.includes(key));
    }
  } finally {
    fs.rmSync(TMP_MATRIX, { force: true });
  }
  // ACC-031 承接的是外部需求 REQ-EXT-001（model.acceptanceRequirements 显式映射）：
  // 把它改回同号的 REQ-FN-031 必须红 —— 证明「同号默认」没有把显式映射架空。
  const remapped = rows.map((l) => (/^\|\s*ACC-031\s*\|/.test(l) ? l.replace('REQ-EXT-001', 'REQ-FN-031') : l));
  fs.writeFileSync(TMP_MATRIX_MAP, `## 4. 自检矩阵\n\n| ACC | REQ | 承接模块 | 测试门 | 样本界面 | 判据形式 | 读数落点 |\n|---|---|---|---|---|---|---|\n${remapped.join('\n')}\n\n## 5. 自检结束\n`);
  try {
    const dirtyMap = run('check-coverage.mjs', ['--matrix-file', '_selftest_matrix_map.md']);
    check('负向（覆盖）：显式映射被改错 rc=1', dirtyMap.code === 1, `实际 rc=${dirtyMap.code}`);
    check('负向（覆盖）：命中「矩阵对不上 ACC-031」', dirtyMap.out.includes('矩阵对不上：ACC-031'), dirtyMap.out.slice(0, 200));
  } finally {
    fs.rmSync(TMP_MATRIX_MAP, { force: true });
  }
} else {
  check('负向（覆盖）：矩阵文档存在', false, '08-判据先行与界面检测设计说明.md 未落地');
}

// ---------- 3.5 编号检查器的目录骨架负向：指向空目录必须红 ----------
const emptySkeleton = fs.mkdtempSync(path.join(os.tmpdir(), 'ldp-skel-'));
try {
  const dirty = run('check-design-ids.mjs', ['--skeleton-root', emptySkeleton]);
  check('负向（骨架）：rc=1', dirty.code === 1, `实际 rc=${dirty.code}`);
  check('负向（骨架）：命中「目录缺失」', dirty.out.includes('目录缺失'));
} finally {
  fs.rmSync(emptySkeleton, { recursive: true, force: true });
}

// ---------- 3.6 结构图与元素属性表同步：生成→比对→改坏必红 ----------
const WIREFRAME = path.join(__dirname, '../设计文档包/界面图/build-wireframe.mjs');
const tmpSvg = fs.mkdtempSync(path.join(os.tmpdir(), 'ldp-wf-'));
try {
  const gen = run(WIREFRAME, ['--out', tmpSvg]);
  check('界面图：生成 rc=0', gen.code === 0, `实际 rc=${gen.code}`);
  const same = run(WIREFRAME, ['--out', tmpSvg, '--check']);
  check('界面图：生成结果与比对一致 rc=0', same.code === 0, `实际 rc=${same.code}`);
  const victim = path.join(tmpSvg, 'scr-01.svg');
  if (fs.existsSync(victim)) {
    fs.appendFileSync(victim, '<!-- drift -->');
    const drift = run(WIREFRAME, ['--out', tmpSvg, '--check']);
    check('负向（界面图）：手改后 rc=1', drift.code === 1, `实际 rc=${drift.code}`);
    check('负向（界面图）：命中「不同步」', drift.out.includes('不同步'));
  } else {
    check('负向（界面图）：结构图存在', false, 'scr-01.svg 未生成');
  }
} finally {
  fs.rmSync(tmpSvg, { recursive: true, force: true });
}
const wfReal = run(WIREFRAME, ['--check']);
check('正向（界面图）：仓内结构图与表同步 rc=0', wfReal.code === 0, `实际 rc=${wfReal.code}`);

// ---------- 3.7 判据索引表：生成→比对→改坏必红 ----------
const GEN_INDEX = path.join(ROOT, '50-verify/index/gen-index.mjs');
const tmpIndexDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ldp-index-'));
try {
  const tmpIndex = path.join(tmpIndexDir, '索引表.md');
  const gen = run(GEN_INDEX, ['--out', tmpIndex]);
  check('索引表：生成 rc=0', gen.code === 0, `实际 rc=${gen.code}`);
  const same = run(GEN_INDEX, ['--out', tmpIndex, '--check']);
  check('索引表：生成结果与比对一致 rc=0', same.code === 0, `实际 rc=${same.code}`);
  if (fs.existsSync(tmpIndex)) {
    fs.appendFileSync(tmpIndex, '\n<!-- drift -->\n');
    const drift = run(GEN_INDEX, ['--out', tmpIndex, '--check']);
    check('负向（索引表）：手改后 rc=1', drift.code === 1, `实际 rc=${drift.code}`);
    check('负向（索引表）：命中「不同步」', drift.out.includes('不同步'));
  } else {
    check('负向（索引表）：产物存在', false, '索引表未生成');
  }
} finally {
  fs.rmSync(tmpIndexDir, { recursive: true, force: true });
}
const idxReal = run(GEN_INDEX, ['--check']);
check('正向（索引表）：仓内索引与矩阵同步 rc=0', idxReal.code === 0, `实际 rc=${idxReal.code}`);

// ---------- 3.8 契约与清单生成物（存在才查；缺本机专属输入时 SKIP，其余照常判 FAIL） ----------
const contractChecks = [
  ['exports.txt', '50-verify/contract/gen-exports.mjs'],
  ['DUCE-LAYOUT.csv', '50-verify/contract/gen-duce-layout.mjs'],
  ['DLLIMPORT-INVENTORY.json', '50-verify/contract/scan-dllimport.mjs'],
  ['must-stub.json', '50-verify/contract/scan-must-stub.mjs'],
];
// 本机专属输入（上游快照不入库、NuGet 缓存不入库）缺失时的报错特征；命中即 SKIP，不算失败
const ENV_INPUT_MISSING = [
  /找不到 ref 包清单/,
  /ENOENT[^\n]*40-build\/upstream/,
  /ENOENT[^\n]*\.nuget\/packages/,
];
let contractSeen = 0;
for (const [label, rel] of contractChecks) {
  const abs = path.join(ROOT, rel);
  if (!fs.existsSync(abs)) continue;
  contractSeen += 1;
  const r = run(abs, ['--check']);
  if (r.code === 0) {
    check(`契约生成物：${label} 同步 rc=0`, true, '实际 rc=0');
    continue;
  }
  const envLine = (r.out || '').split('\n').find((l) => ENV_INPUT_MISSING.some((re) => re.test(l)));
  if (envLine) {
    console.log(`SKIP 契约生成物：${label}（缺本机专属输入：${envLine.trim().slice(0, 140)}）`);
    continue;
  }
  check(`契约生成物：${label} 同步 rc=0`, false, `实际 rc=${r.code}`);
}
if (!contractSeen) console.log('SKIP 契约生成物尚未落地（B3 未完成）');

// ---------- 4. 清理后全仓正向 ----------
for (const [label, script, args] of [
  ['链接', 'check-links.mjs', []],
  ['文档包', 'check-package.mjs', []],
  ['编号', 'check-design-ids.mjs', []],
  ['覆盖', 'check-coverage.mjs', []],
]) {
  const clean = run(script, args);
  const errs = clean.out.split('\n').filter((l) => l.includes('ERROR')).slice(0, 3).join(' / ');
  check(`正向（${label}）：rc=0`, clean.code === 0, `实际 rc=${clean.code}${errs ? ` :: ${errs}` : ''}`);
}

let failed = 0;
for (const a of assertions) {
  if (!a.ok) failed += 1;
  console.log(`${a.ok ? 'PASS' : 'FAIL'} ${a.name}${a.detail ? ` (${a.detail})` : ''}`);
}
console.log(`selftest: ${assertions.length - failed}/${assertions.length} 通过`);
process.exit(failed ? 1 : 0);
