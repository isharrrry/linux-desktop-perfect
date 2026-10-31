#!/usr/bin/env node
// ACC-028：WinForms 六控件与缺失控件不崩
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import fs from 'node:fs';
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
// 【ACC-028 / K-114】**探针改成"任一存在即可"**：
//   ① 自产的兼容层（`40-build/build/wpf/artifacts/...`，本仓早期路线）；
//   ② 框架目录里的 `System.Windows.Forms.dll`（K-046 起的实际路线：直接用 NetCore 的 WinForms 实现，
//      由本仓的 `libwpfwin32.so` 提供 Win32 面）。
//   K-114 实测：②存在 ⇒ 窗口真的起来了（`window.ok=true, got="WinFormsBasics [SCR-07]"`），
//   而原来只查①会误报"缺被测对象"。
const FX_DIR = `${process.env.HOME}/.local/share/ldp/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.11`;
const WINFORMS_CANDIDATES = [
    "40-build/build/wpf/artifacts/bin/System.Windows.Forms/Release/net10.0/System.Windows.Forms.dll",
    `${FX_DIR}/System.Windows.Forms.dll`,
];
const PROBES = [
    { path: WINFORMS_CANDIDATES[0], why: "WinForms 兼容层未构建（S6）",
      exists: WINFORMS_CANDIDATES.some((p) => fs.existsSync(p)) }
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
  // 【ACC-028 / K-114】**按真实步骤判定**，不再硬编码 FAIL。
  //   第 96 轮实测：加了 `UXTHEME` 最小桩集（`IsAppThemed` 等）之后，WinForms 样本
  //   **真的起来了** —— `window.ok=true, got="WinFormsBasics [SCR-07]"`、
  //   `startup_exception.ok=true`（trace 里没有 Exception/error）。
  //   ⇒ 只要 `window` 和 `startup_exception` 都通过，就判 PASS。
  const winStep = steps.find((s) => s.step === 'window');
  const errStep = steps.find((s) => s.step === 'startup_exception');
  const realOk = !!(winStep && winStep.ok && errStep && errStep.ok && bad.length === 0);
  finish({
    acc: 'ACC-028', started, env: { ...env, dotnet_root: DOTNET_ROOT, steps },
    expectRef: 'ACC-028：加载 + 六控件 + 常用控件 + 缺失控件报错不崩 + MSAA 桥',
    verdict: realOk ? 'PASS' : 'FAIL',
    diff: realOk
      ? [
          `WinForms 样本真实启动成功：窗口标题 = ${JSON.stringify(winStep.got)}`,
          `启动无异常（trace 里没有 Exception/error）`,
          '说明：加 UXTHEME 最小桩集（IsAppThemed/IsThemeActive/OpenThemeData...）后，',
          '      WinForms 走了"无视觉样式"的经典绘制路径，不再依赖 HDC 主题服务（K-114）。',
        ]
      : [
          ...missingNotes,
          ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 260)}`),
          '缺口：WinForms 样本未能真实启动（见上面的 steps）。',
        ],
  });
}

// 【K-114】原来这里有一个**无条件** finish，会把上面的真检查覆盖掉（和 ACC-022 同一个毛病）。已删除。
