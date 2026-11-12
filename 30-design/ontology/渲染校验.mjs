// 模型页渲染校验：不启动浏览器，直接对 30-design/ontology/本体模型.html 做静态断言。
// 做法：正则取出 <script id="ontology-data"> 里的 JSON → JSON.parse → 与页面结构交叉核对。
// 用法： node 30-design/ontology/渲染校验.mjs      任一 FAIL 退出码 1。
import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HTML_PATH = path.join(HERE, '本体模型.html');

const MIN_BYTES = 8 * 1024;
const EXPECT = {
  classes: 24,
  relations: 26,
  rules: 40,
  actions: 11,
  terms: 18,
  decisions: 12,
  excluded: 12,
};
const TABS = ['类与关系', '规则', '动作与术语', '裁决与排除', '校验报告'];
const CONFIDENCE_OK = new Set(['medium', 'low']);

const checks = [];
function check(name, ok, detail = '') {
  checks.push({ name, ok: Boolean(ok), detail });
}

// ---------- 1) 文件存在且 > 8 KB ----------
if (!existsSync(HTML_PATH)) {
  check('文件存在且 > 8 KB', false, '本体模型.html 不存在');
} else {
  const size = statSync(HTML_PATH).size;
  check('文件存在且 > 8 KB', size > MIN_BYTES, `实际 ${(size / 1024).toFixed(1)} KB`);
}

let text = '';
if (existsSync(HTML_PATH)) text = readFileSync(HTML_PATH, 'utf8');

// ---------- 2) 无外链 ----------
const external = text.match(/https?:\/\//gi) || [];
check('无 http(s) 外链', external.length === 0, `命中 ${external.length} 处`);

// ---------- 3) 五个页签标题 ----------
const tabButtons = [...text.matchAll(/<button[^>]*data-tab="([^"]+)"[^>]*>([^<]*)<\/button>/g)]
  .map((m) => ({ key: m[1], label: m[2].trim() }));
const labels = tabButtons.map((t) => t.label);
const missingTabs = TABS.filter((t) => !labels.includes(t));
check('五个页签标题齐全', tabButtons.length === TABS.length && missingTabs.length === 0,
  tabButtons.length === TABS.length && missingTabs.length === 0
    ? `${tabButtons.length} 个页签：${labels.join(' / ')}`
    : `页签 ${tabButtons.length} 个，缺 ${missingTabs.join('、')}`);

// ---------- 4) ontology-data JSON 可解析 ----------
let data = null;
const m = text.match(/<script[^>]*id="ontology-data"[^>]*>([\s\S]*?)<\/script>/);
let parseError = '';
if (!m) {
  parseError = '找不到 <script id="ontology-data"> 数据块';
} else {
  try {
    data = JSON.parse(m[1]);
  } catch (e) {
    parseError = `JSON.parse 失败：${e.message}`;
  }
}
check('ontology-data JSON 可解析', Boolean(data), data ? 'JSON.parse 成功' : parseError);

const counts = data
  ? {
      classes: (data.classes || []).length,
      relations: (data.relations || []).length,
      rules: (data.rules || []).length,
      actions: (data.actions || []).length,
      terms: (data.terms || []).length,
      decisions: (data.decisions || []).length,
      excluded: (data.excluded || []).length,
    }
  : {};

// ---------- 5)~11) 各段条数 ----------
const COUNT_LABELS = [
  ['classes', 'classes = 24'],
  ['relations', 'relations = 26'],
  ['rules', 'rules = 40'],
  ['actions', 'actions = 11'],
  ['terms', 'terms = 18'],
  ['decisions', 'decisions = 12'],
  ['excluded', 'excluded = 12'],
];
for (const [key, label] of COUNT_LABELS) {
  const actual = counts[key];
  check(label, actual === EXPECT[key], `实际 ${actual === undefined ? '无数据' : actual}`);
}

// ---------- 12) data-class-id 节点数 == 类数 ----------
const nodeIds = [...text.matchAll(/data-class-id="([^"]*)"/g)].map((x) => x[1]);
check('data-class-id 节点数 == 类数',
  data ? nodeIds.length === counts.classes : false,
  data ? `节点 ${nodeIds.length} 个 / 类 ${counts.classes} 个` : '无数据');

// ---------- 13) 每条关系 id 至少出现一次（除数据块以外的渲染标记里） ----------
const markup = m ? text.replace(m[0], '') : text;
const missingRel = data
  ? (data.relations || []).map((r) => r.id).filter((id) => !markup.includes(id))
  : ['无数据'];
check('每条关系 id 在页面出现', missingRel.length === 0,
  missingRel.length === 0
    ? `${(data.relations || []).length} 条关系在渲染标记里全部命中`
    : `缺 ${missingRel.slice(0, 5).join('、')}`);

// ---------- 14) confidence 只有 medium/low ----------
const badConf = data
  ? (data.rules || []).filter((r) => !CONFIDENCE_OK.has(String(r.confidence))).map((r) => `${r.id}:${r.confidence}`)
  : ['无数据'];
const confStat = data
  ? (data.rules || []).reduce((acc, r) => {
      const k = String(r.confidence);
      acc[k] = (acc[k] || 0) + 1;
      return acc;
    }, {})
  : {};
check('confidence 只有 medium/low', badConf.length === 0,
  badConf.length === 0 ? `medium ${confStat.medium || 0} / low ${confStat.low || 0}` : badConf.slice(0, 5).join('、'));

// ---------- 汇总 ----------
let passed = 0;
for (const c of checks) {
  if (c.ok) passed += 1;
  console.log(`${c.ok ? 'PASS' : 'FAIL'} ${c.name}${c.detail ? `  → ${c.detail}` : ''}`);
}
console.log(`\n渲染校验: ${passed}/${checks.length} 通过`);
process.exit(passed === checks.length ? 0 : 1);
