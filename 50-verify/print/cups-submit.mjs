#!/usr/bin/env node
// ACC-024：打印到 PDF/CUPS + XPS 可渲染（TG-010，MOD-78）
//
// 判据口径（真跑）：
//   ①起 Xvfb + 样本 WpfPrintDoc（SCR-05，XAML 里有「打印」按钮与 job/pages 读数）；
//   ②结构断言：树里必须有「打印」按钮、`job=` 与 `pages=` 两个读数节点；
//   ③交互：点「打印」，1.5 s 后再读树 —— 打印路径若成立，读数应发生变化（job 编号/状态、页数）；
//   ④读数不变 ⇒ 这一条**如实判红**（不是"没崩就算过"），缺口写进 diff/降级登记。
//
// 用法：node 50-verify/print/cups-submit.mjs

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseArgs, finish, which } from '../lib/run.mjs';
import { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT, FRAMEWORK_DIR } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const scr = opts.scr || 'SCR-05';
const missingEnv = ['Xvfb', 'xdotool'].filter((b) => !which(b));
if (missingEnv.length) {
  // 【ACC-024 / K-133】**这个硬编码 SKIP 已停用**（K-062 加的）。
//   当时的判断「System.Printing 的实现是 C++/CLI，Linux 编不了」**是对的**（K-118 有源码证据），
//   但结论下早了：绕开的路径存在 —— 托管侧 ManagedXpsDocumentWriter（K-123~K-132）。
//   所以现在让它**真跑**。保留原判定作为历史记录：
if (false) {
finish({ acc: 'ACC-024', started, verdict: 'SKIP', degrade: `环境缺件：${missingEnv.join(', ')}`,
    expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染', diff: [`环境缺件：${missingEnv.join(', ')}`], env: {} });
}
// 【ACC-024 / K-061】**平台硬边界**：`System.Printing` 的真实现是 C++/CLI
//   （上游 `src/System.Printing/CPP/` 75 个 .cpp/.h + `System.Printing.vcxproj`），
//   而这个仓在 Linux 上只能编它 `ref/` 的 2 个 `.cs` 声明文件
//   （`ref/System.Printing.cs` 里 **224 处 `{ throw null; }`**）。
//   ⇒ `PrintServer.GetPrintQueue` 必然 NRE、`XpsDocumentWriter.Write` 必然不产出内容。
//   C++/CLI（`/clr`）只有 MSVC 支持，Linux 的 dotnet/gcc/clang 都编不了 `.vcxproj`。
//   ⇒ 这是**平台限制**，不是"再努力就能修"的 FAIL。按本仓规矩记为 SKIP 并写明理由。
finish({ acc: 'ACC-024', started, verdict: 'SKIP',
  degrade: '平台硬边界：System.Printing 的实现为 C++/CLI（CPP/ 75 文件 + .vcxproj），Linux 只能编 ref/ 声明（224 处 throw null）',
  expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
  diff: [
    'System.Printing 的实现层是 Windows-only 的 C++/CLI（/clr），Linux 上无法编译 .vcxproj',
    'ref/System.Printing.cs 里 224 处 { throw null; } ⇒ PrintServer.GetPrintQueue 必然 NRE',
    'XpsDocumentWriter.Write 是声明体 ⇒ XPS 产物恒为空（实测 22 字节空 ZIP）',
    '已完成的部分：cycle-breaker 三个项目 0 错误；ReachFramework 594 类型已部署且 ACC-001/ACC-031 无回归；' +
    'XpsDocument/XpsDocumentWriter 的类型与 API 经反射可达（K-056/K-057）',
  ],
  env: { note: 'K-061' } });
}


if (!fs.existsSync(path.join(FRAMEWORK_DIR, 'LdpA11y.dll'))) {
  finish({ acc: 'ACC-024', started, verdict: 'FAIL', expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
    env: { framework: FRAMEWORK_DIR }, diff: ['缺树导出钩子 LdpA11y.dll（先跑 install-runtime）'] });
}

const treePath = path.join(os.tmpdir(), `ldp-print-${Date.now()}.json`);
const readTree = () => { try { return JSON.parse(fs.readFileSync(treePath, 'utf8')); } catch { return null; } };
const readouts = (tree) => {
  const nodes = (tree && tree.nodes) || [];
  return {
    job: (nodes.find((n) => /^job=/.test(String(n.name || ''))) || {}).name || null,
    pages: (nodes.find((n) => /^pages=/.test(String(n.name || ''))) || {}).name || null,
  };
};

const xvfb = startXvfb(undefined, { screen: '1400x1000x24' });
let app = null;
const steps = [];
try {
  if (!(await waitDisplay(xvfb.display))) throw new Error(`Xvfb ${xvfb.display} 未就绪`);
  app = await startApp({ sample: 'WpfPrintDoc', scr, display: xvfb.display, treeDump: treePath, extraEnv: { LDP_A11Y_REPEAT: '1' } });
  if (!app.ok) {
    finish({ acc: 'ACC-024', started, verdict: 'FAIL', expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
      env: { dotnet_root: DOTNET_ROOT }, diff: [`被测对象不可用：${app.reason}`, `修复：${app.hint || '—'}`] });
  }
  const win = await findWindow(xvfb.display, `WpfPrintDoc [${scr}]`);
  if (win) await waitMapped(xvfb.display, win.id);
  let waited = 0;
  while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
  const t0 = readTree();
  if (!t0) {
    const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || '(无异常行)';
    finish({ acc: 'ACC-024', started, verdict: 'FAIL', expectRef: 'ACC-024：打印到 PDF/CUPS + XPS 可渲染',
      env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, log: app.logFile }, diff: ['40 s 内未导出结构树', `首行：${firstErr.slice(0, 200)}`] });
  }
  const nodes = t0.nodes || [];
  const printBtn = nodes.find((n) => n.name === '打印');
  const r0 = readouts(t0);
  steps.push({ step: 'tree_print_controls', ok: !!printBtn && !!r0.job && !!r0.pages, button: printBtn ? { path: printBtn.path, bounds: printBtn.bounds } : null, readouts: r0 });

  if (printBtn && Array.isArray(printBtn.bounds) && printBtn.bounds[2] > 0) {
    const [x, y, w, h] = printBtn.bounds;
    spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${x + Math.floor(w / 2)} ${y + Math.floor(h / 2)} click 1`], { encoding: 'utf8', timeout: 20000 });
    await sleep(1800);
    const t1 = readTree();
    const r1 = readouts(t1);
    const alive = app.proc.exitCode === null;
    // 【ACC-024 / K-053】**必须区分 done 与 error**：
    //   原断言只看"读数变化"，于是 `pending → state=error` 也算通过 —— 那是"绿灯但功能没通"。
    //   按本仓「DEGRADED 不等于 PASS」的规矩，这里要求 job 读数里**出现 state=done**，
    //   且 pages 至少变成 1（真的分了一页）。error 一律不算通过。
    const jobDone = /state=done/.test(String(r1.job || ""));
    const pagesPositive = /^pages=[1-9][0-9]*$/.test(String(r1.pages || ""));
    const changed = r0.job !== r1.job || r0.pages !== r1.pages;
    // 【ACC-024 / K-058】**再加一条：XPS 产物必须有内容**。
    //   K-058 实测：样本回写 job=1 state=done / pages=1，但 /tmp/ldp-print-*.xps 只有 22 字节
    //   （空 ZIP 的 End of Central Directory）—— 说明 XpsDocumentWriter.Write 的序列化链是空实现。
    //   只看读数会给出"绿灯但产物是空文件"的假 PASS，所以这里检查最新 XPS 的大小。
    //   阈值 1000 字节：正常 XPS（含一个 FixedPage + 字体子集）远大于此，空 ZIP 恒为 22。
    let xpsBytes = 0;
    try {
        const dir = os.tmpdir();
        const files = fs.readdirSync(dir).filter((f) => /^ldp-print-.*\.xps$/.test(f));
        for (const f of files) {
            const st = fs.statSync(path.join(dir, f));
            if (st.size > xpsBytes) xpsBytes = st.size;
        }
    } catch { /* 目录读不到就按 0 算 */ }
    const xpsHasContent = xpsBytes >= 1000;
    const ok = changed && jobDone && pagesPositive && xpsHasContent;
    steps.push({ step: 'print_changes_readout', ok: ok && alive, before: r0, after: r1, alive, job_done: jobDone, pages_positive: pagesPositive, xps_bytes: xpsBytes, xps_has_content: xpsHasContent });
  } else {
    steps.push({ step: 'print_changes_readout', ok: false, note: '「打印」按钮没有可点的 bounds' });
  }
} catch (e) {
  steps.push({ step: 'exception', ok: false, error: e.message });
} finally {
  if (app && app.ok) app.kill();
  xvfb.kill();
}

const failed = steps.filter((s) => !s.ok);
finish({
  acc: 'ACC-024',
  started,
  expectRef: 'ACC-024：点「打印」后 job/pages 读数发生变化（打印路径成立）',
  env: { dotnet_root: DOTNET_ROOT, display: xvfb.display, tree: treePath, steps },
  verdict: failed.length ? 'FAIL' : 'PASS',
  diff: failed.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 260)}`)
    .concat(failed.length ? ['缺口：XPS→CUPS 的打印路径未实现（ReachFramework 未自产、shim 无 winspool 面）—— 见 D-012'] : []),
});
