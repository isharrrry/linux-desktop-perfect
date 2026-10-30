#!/usr/bin/env node
// ACC-028：WinForms 六控件与缺失控件不崩
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/build/wpf/artifacts/bin/System.Windows.Forms/Release/net10.0/System.Windows.Forms.dll", why: "WinForms 兼容层未构建（S6）", exists: exists("40-build/build/wpf/artifacts/bin/System.Windows.Forms/Release/net10.0/System.Windows.Forms.dll") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ sample: exists("samples/WinFormsBasics/WinFormsBasics.csproj") },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

// 探测缺失**不提前返回**：真跑段仍要跑，才能把"启动就被掀掉"的真实异常落进读数
// （比"程序集不存在"精确一档：缺口是兼容层，不是某个文件）。
const missingNotes = missing.map((m) => `缺被测对象：${m.path}（${m.why}）`);

// 真跑：WinForms 兼容层未自产时，样本会在**启动阶段**因为缺 `KERNEL32.dll` 的直接 P/Invoke 而崩。
//   这里把真实异常打进读数（比"程序集不存在"精确一档：缺口是 WinForms 兼容层，不是某个 dll 文件）。
const { startXvfb, waitDisplay, startApp, findWindow, sleep, DOTNET_ROOT } = await import('../lib/app-session.mjs');
{
  const xvfb = startXvfb();
  let app = null;
  const steps = [];
  try {
    if (await waitDisplay(xvfb.display)) {
      app = await startApp({ sample: 'WinFormsBasics', scr: 'SCR-07', display: xvfb.display });
      if (!app.ok) {
        steps.push({ step: 'startup', ok: false, reason: app.reason });
      } else {
        const win = await findWindow(xvfb.display, 'WinFormsBasics [SCR-07]', 15000);
        const trace = app.trace();
        const firstErr = trace.split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || null;
        steps.push({ step: 'window', ok: !!win, got: win ? win.title : null });
        steps.push({ step: 'startup_exception', ok: !firstErr, first_error: firstErr ? firstErr.slice(0, 300) : null });
      }
    }
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  const bad = steps.filter((s) => !s.ok);
  finish({
    acc: 'ACC-028', started, env: { ...env, dotnet_root: DOTNET_ROOT, steps },
    expectRef: 'ACC-028：加载 + 六控件 + 常用控件 + 缺失控件报错不崩 + MSAA 桥',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 260)}`),
      '缺口：WinForms 兼容层（System.Windows.Forms）未自产 —— 样本启动即 DllNotFoundException(KERNEL32.dll)',
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-028', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-028：加载 + 六控件 + 常用控件 + 缺失控件报错不崩 + MSAA 桥',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
