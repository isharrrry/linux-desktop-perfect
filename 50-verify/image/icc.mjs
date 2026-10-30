#!/usr/bin/env node
// ACC-019：ICC 色彩管理
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const PROBES = [
    { path: "40-build/wic-dwrite/bin/libwpfwic.so", why: "libwpfwic.so 未编译（S3）", exists: exists("40-build/wic-dwrite/bin/libwpfwic.so") }
];
const missing = PROBES.filter((p) => !p.exists);
const env = {
    ...{ profile: opts.profile || "srgb", lcms2: runCmd("dpkg-query",["-W","-f=${Version}","liblcms2-2"]).out.trim() },
    probes: Object.fromEntries(PROBES.map((p) => [p.path, p.exists ? 'present' : 'missing'])),
};

const missingNotes = missing.map((m) => `缺被测对象：${m.path}（${m.why}）`);

// 真跑：WIC 的承载面是 `libwpfwic.so`（IWICImagingFactory 等 COM 风格接口）。它不存在时，
//   本判据要落两件事：①resolver 的 WIC 映射是否已开（`WicEnabledByDefault`）；②起 SCR-03 图像样本，
//   看有没有别的解码路径（WPF 的 `BitmapDecoder` 在 Linux 上会走 WIC，缺 so 时应如实失败）。
const { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT } = await import('../lib/app-session.mjs');
{
  const { spawnSync } = await import('node:child_process');
  const resolverFlag = (spawnSync('sh', ['-c', 'grep -c "WicEnabledByDefault = true" 40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs'], { encoding: 'utf8' }).stdout.trim() || '0');
  const pInvokeCount = (spawnSync('sh', ['-c', 'grep -rh "WindowsCodecs" --include=*.cs 40-build/build/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/ | grep -c DllImport'], { encoding: 'utf8' }).stdout.trim() || '0');
  const xvfb = startXvfb();
  let app = null;
  const steps = [];
  try {
    if (await waitDisplay(xvfb.display)) {
      app = await startApp({ sample: 'WpfImageGallery', scr: 'SCR-03', display: xvfb.display });
      if (app.ok) {
        const win = await findWindow(xvfb.display, 'WpfImageGallery [SCR-03]', 25000);
        if (win) await waitMapped(xvfb.display, win.id);
        const firstErr = app.trace().split(String.fromCharCode(10)).find((l) => /Exception|error/i.test(l)) || null;
        steps.push({ step: 'window', ok: !!win, got: win ? win.title : null });
        steps.push({ step: 'no_startup_exception', ok: !firstErr, first_error: firstErr ? firstErr.slice(0, 260) : null });
      } else steps.push({ step: 'startup', ok: false, reason: app.reason });
    }
  } finally {
    if (app && app.ok) app.kill();
    xvfb.kill();
  }
  const bad = steps.filter((s) => !s.ok);
  finish({
    acc: 'ACC-019', started,
    env: { ...env, dotnet_root: DOTNET_ROOT, wic_mapping_enabled: resolverFlag === '1', windows_codecs_pinvoke_sites: Number(pInvokeCount) || 0, steps },
    expectRef: 'ACC-019：ICC',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      `缺口：libwpfwic.so 未实现（resolver 的 WIC 映射已开=${resolverFlag === '1'}，WPF 侧 WindowsCodecs P/Invoke 站点 ${pInvokeCount} 处）`,
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-019', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-019：ICC 嵌入图与 Windows 输出色差在阈值内',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
