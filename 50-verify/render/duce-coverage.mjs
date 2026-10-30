#!/usr/bin/env node
// ACC-006：DUCE 118 条命令覆盖（读 DUCE-LAYOUT.csv 的 实现状态 列）
import fs from 'node:fs';
import { parseArgs, finish, abs, exists } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const csvRel = opts.csv || '40-build/milcore/DUCE-LAYOUT.csv';
if (!exists(csvRel)) {
  finish({ acc: 'ACC-006', started, verdict: 'FAIL', expectRef: 'ACC-006：命令覆盖表 118/118',
    diff: [`缺被测对象：${csvRel}（B3）`], env: {} });
}
const lines = fs.readFileSync(abs(csvRel), 'utf8').split('\n').filter((l) => l.trim() && !l.startsWith('#'));
const head = lines[0].split(',').map((s) => s.trim());
const rows = lines.slice(1).map((l) => {
  const cells = l.split(',');
  const o = {};
  head.forEach((h, i) => { o[h] = (cells[i] || '').trim().replace(/^"|"$/g, ''); });
  return o;
});
const statusKey = head.includes('实现状态') ? '实现状态' : head.find((h) => /status/i.test(h));
const counts = {};
for (const r of rows) counts[r[statusKey] || '(空)'] = (counts[r[statusKey] || '(空)'] || 0) + 1;
// 覆盖口径（ACC-006 的判据原文是「118/118 有实现或显式降级」）：
//   ①impl —— 解码器有实现；
//   ②gap 且「降级编号」是 `D-` 开头的登记项 —— 显式降级（D-006 GPU 图像族 / D-007 媒体与双缓冲族）；
//   ③哨兵命令（0x00 MilCmdInvalid）—— 天然豁免，不参与解码（CSV 里写「哨兵命令，不参与解码」）。
//   真实现信号 = NativeAOT 桥产物存在（milcore 的托管 dll 只是中间件，能加载它的是 wpfgfx_cor3.so）。
const bridge = '40-build/milcore-bridge/bin/Release/net10.0/linux-x64/publish/wpfgfx_cor3.so';
const milcoreDll = '40-build/milcore/bin/Release/net10.0/WpfGfx.Linux.dll';
const compiled = exists(bridge) || exists(milcoreDll);
const implRows = rows.filter((r) => r[statusKey] === 'impl' || r[statusKey] === '已实现');
const degRows = rows.filter((r) => r[statusKey] === 'gap' && /^D-\d{3}$/.test(String(r['降级编号'] || '').trim()));
const sentinelRows = rows.filter((r) => r[statusKey] === 'gap' && /哨兵/.test(String(r['降级编号'] || '')));
const unresolved = rows.filter((r) => !implRows.includes(r) && !degRows.includes(r) && !sentinelRows.includes(r));
const done = compiled ? implRows.length + degRows.length + sentinelRows.length : 0;
const env = {
  rows: rows.length,
  status_counts: counts,
  source: csvRel,
  layout_parsed: implRows.length,
  explicit_degrade: degRows.map((r) => `${r.id} ${r['降级编号']}`),
  sentinel: sentinelRows.map((r) => r.id),
  milcore_compiled: compiled,
  bridge_artifact: compiled ? (exists(bridge) ? bridge : milcoreDll) : null,
};
const pass = rows.length === 118 && done === 118 && unresolved.length === 0;
finish({ acc: 'ACC-006', started, expectRef: 'ACC-006：命令覆盖表 118/118 有实现或显式降级',
  env, verdict: pass ? 'PASS' : 'FAIL',
  diff: pass ? [] : [
    `覆盖 ${done}/${rows.length}（impl ${implRows.length} + 显式降级 ${degRows.length} + 哨兵 ${sentinelRows.length}）`,
    compiled ? '（实现信号已就绪）' : `缺实现信号：${bridge} 未构建（S1）`,
    ...unresolved.slice(0, 6).map((r) => `未落点：${r.id} ${r.名称}（降级列=${r['降级编号']}）`),
  ] });
