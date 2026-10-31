#!/usr/bin/env node
// ACC-015：行布局与 PTS 替代（MOD-33/34，TG-005）
//
// 判据口径（双路径对拍，08 §1.5「不许自证」）：
//   ①换行（UAX#14）：libdwlinux.so 的 `dwp_line_breaks` 与 hb-oracle.py 的 ICU `ubrk_*` 逐项一致；
//   ②双向文本：`dwp_bidi_levels` 与基准的 ICU `ubidi` 级别逐项一致；
//   ③分页/FlowDocument 宿主集成**未实现** → 本条判 DEGRADED（换行与双向已验，分页未覆盖），
//     不允许读成 PASS（DEGRADED 不计通过）。
//
// 用法：node 50-verify/text/linebreak.mjs

import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, exists, runCmd } from '../lib/run.mjs';
const { startXvfb, waitDisplay, DOTNET_ROOT, FRAMEWORK_DIR } = await import('../lib/app-session.mjs');

const started = Date.now();
const { opts } = parseArgs();
const SO = '40-build/text-stack/bin/libdwlinux.so';
const PROBE = '40-build/text-stack/bin/dwp-probe';
const ORACLE = '50-verify/text/hb-oracle.py';

const PROBES = [
  { path: SO, why: 'libdwlinux.so 未编译（S2）' },
  { path: PROBE, why: 'dwp-probe 未编译（S2 判据探针）' },
  { path: ORACLE, why: 'hb-oracle.py 基准缺失（S2 判据侧）' },
].map((p) => ({ ...p, exists: exists(p.path) }));
const missing = PROBES.filter((p) => !p.exists);
const env = {
  icu: runCmd('dpkg-query', ['-W', '-f=${Version}', 'libicu70']).out.trim(),
  probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

if (missing.length) {
  finish({
    acc: 'ACC-015', started, env, verdict: 'FAIL',
    expectRef: 'ACC-015：换行机会点与双向级别与基准一致；分页由宿主集成',
    diff: [
      ...missing.map((m) => `缺被测对象：${m.path}（${m.why}）`),
      '依赖切片：S2 文本（TG-005）',
      '说明：本条为能力探测式红读数，不伪造通过（ADR-014）',
    ],
  });
}

const diff = [];
const rows = [];

// ①换行：多组文本（纯拉丁 / CJK / 混合 / 含连字符与空格）
const BREAK_CASES = ['Hello world', '中文字形断行测试', 'abc中文def', 'one-two three'];
for (const text of BREAK_CASES) {
  const p = runCmd(PROBE, ['--breaks', text], { timeout: 60000 });
  const payload = JSON.stringify({ text }).replace(/'/g, "'\\''");
  const o = runCmd('sh', ['-c', `printf '%s' '${payload}' | python3 ${ORACLE} --breaks`], { timeout: 120000 });
  if (p.rc !== 0 || o.rc !== 0) {
    diff.push(`断行取数失败「${text}」：probe rc=${p.rc} oracle rc=${o.rc}`);
    continue;
  }
  const got = JSON.parse(p.out.trim().split('\n').pop());
  const want = JSON.parse(o.out.trim().split('\n').pop());
  const same = JSON.stringify(got.byte_offsets) === JSON.stringify(want.byte_offsets);
  rows.push({ case: 'breaks', text, offsets: got.count, same });
  if (!same) diff.push(`断行不一致「${text}」：实现 ${JSON.stringify(got.byte_offsets)}，基准 ${JSON.stringify(want.byte_offsets)}`);
}

// ②双向：拉丁 + 阿拉伯 + 数字
const BIDI_CASES = ['abc مرحبا 123', 'مرحبا مرحبا'];
for (const text of BIDI_CASES) {
  const p = runCmd(PROBE, ['--bidi', text], { timeout: 60000 });
  const payload = JSON.stringify({ text }).replace(/'/g, "'\\''");
  const o = runCmd('sh', ['-c', `printf '%s' '${payload}' | python3 ${ORACLE} --bidi`], { timeout: 120000 });
  if (p.rc !== 0 || o.rc !== 0) {
    diff.push(`双向取数失败「${text}」：probe rc=${p.rc} oracle rc=${o.rc}`);
    continue;
  }
  const got = JSON.parse(p.out.trim().split('\n').pop());
  const want = JSON.parse(o.out.trim().split('\n').pop());
  const same = JSON.stringify(got.levels) === JSON.stringify(want.levels);
  rows.push({ case: 'bidi', text, levels: got.count, same });
  if (!same) diff.push(`双向级别不一致「${text}」：实现 ${JSON.stringify(got.levels)}，基准 ${JSON.stringify(want.levels)}`);
}

// ③分页/宿主集成未接：判 DEGRADED（不是 PASS）
// 分页（ACC-015 的"分页"面）：跑 50-verify/text/page-app，读它写的 JSON。
//   D-019：分页测量在字体回退缺口下会**卡住**；page-app 自带 20 s 看门狗，
//   所以这里拿到的是"WatchdogTimeout"读数而不是"进程被 kill、什么都没有"。
let pagination = null;
{
  const appDll = '50-verify/text/page-app/bin/Release/net10.0/PageApp.dll';
  if (fs.existsSync(appDll)) {
    const out = '/tmp/ldp-page-result.json';
    fs.rmSync(out, { force: true });
    const xv = startXvfb();
    try {
      if (await waitDisplay(xv.display)) {
        spawnSync('sh', ['-c',
          `DISPLAY=${xv.display} DOTNET_ROOT=${DOTNET_ROOT} DOTNET_ReadyToRun=0 ` +
          `WPF_LINUX_WIN32_SHIM=${FRAMEWORK_DIR}/libwpfwin32.so ` +
          `${DOTNET_ROOT}/dotnet ${appDll} ${out} > /tmp/ldp-page-app.log 2>&1`],
          // ⚠️ 不起 shell 的 `timeout`：page-app 自带 20 s 看门狗，外层再套 timeout 会和它抢
          //   退出权（实测：外层先杀 ⇒ 看门狗的回写被抢掉 ⇒ 判据读到 null）。
          { encoding: 'utf8', timeout: 45000 });
      }
    } finally {
      xv.kill();
    }
    try { pagination = JSON.parse(fs.readFileSync(out, 'utf8')); } catch { pagination = null; }
  }
}

// 【2026-10-31 判据修复】原来这条 degrade 是**无条件**的：即使 page-app 返回
//   `{"ok":true,page_count:...,page_size:...}` 也照旧说"卡住"（D-019）。
//   分页修通后再这么写就成了"判据与实现不同步" ⇒ 改成按实际读数分三态。
const paginationOk = !!(pagination && pagination.ok === true);
const degradeReason = paginationOk
    ? '换行与双向已双路径验；分页面已由 `page-app` 实测通过'
      + `（page_count=${pagination.page_count}，page_size=${JSON.stringify(pagination.page_size)}，`
      + `first_page_blank=${pagination.first_page_blank}）`
    : ('换行与双向已双路径验；**分页面**未通过'
      + `（${pagination ? (pagination.type || 'unknown') + '：' + (pagination.error || '') : '无读数'}）`);
finish({
  acc: 'ACC-015',
  started,
  expectRef: 'ACC-015：换行机会点与双向级别与基准一致（分页由宿主集成，未覆盖）',
  env: { ...env, cases: rows, pagination },
  verdict: diff.length ? 'FAIL' : 'DEGRADED',
  degrade: diff.length ? 'none' : degradeReason,
  diff: diff.length ? diff.slice(0, 8) : (paginationOk ? [] : [`未覆盖项：${degradeReason}`]),
});
