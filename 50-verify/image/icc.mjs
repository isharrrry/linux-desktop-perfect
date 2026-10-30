#!/usr/bin/env node
// ACC-019：ICC 色彩管理
// 结构：参数解析 → 能力探测 → 三分判定（真跑 / 缺被测对象 FAIL / 环境缺件 SKIP-DEGRADED）
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
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
const { startXvfb, waitDisplay, startApp, findWindow, waitMapped, sleep, DOTNET_ROOT, FRAMEWORK_DIR } = await import('../lib/app-session.mjs');
{
  const { spawnSync } = await import('node:child_process');
  const resolverFlag = (spawnSync('sh', ['-c', 'grep -c "WicEnabledByDefault = true" 40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs'], { encoding: 'utf8' }).stdout.trim() || '0');
  const pInvokeCount = (spawnSync('sh', ['-c', 'grep -rh "WindowsCodecs" --include=*.cs 40-build/build/wpf/src/Microsoft.DotNet.Wpf/src/PresentationCore/ | grep -c DllImport'], { encoding: 'utf8' }).stdout.trim() || '0');
  // WIC 探针：区分「so 不存在」「工厂能建但解码没实现」「解码可用」三种状态。
  //   `hr_decode == 0x80004001(E_NOTIMPL)` 是**诚实失败**（不返回假句柄），必须如实记进读数。
  let wicProbe = null;
  {
    const probe = '40-build/wic-dwrite/bin/wic-probe';
    const so = '40-build/wic-dwrite/bin/libwpfwic.so';
    if (fs.existsSync(probe) && fs.existsSync(so)) {
      const r = spawnSync(probe, [so], { encoding: 'utf8', timeout: 30000 });
      const line = (r.stdout || '').trim().split(String.fromCharCode(10)).pop();
      try { wicProbe = JSON.parse(line); } catch { wicProbe = { raw: line }; }
    }
  }
  // 应用内（端到端）：BitmapImage 加载**带内嵌 ICC** 的 PNG。
  //   读数带 ok/格式；ICC 的"色差阈值"那一半仍要 mscms（未实现），所以本判据仍 FAIL ——
  //   但"profile 能被 WIC 导出并被 WPF 接受"这一环由此可验。
  let wicApp = null;
  {
    const appDll = '50-verify/image/wic-app/bin/Release/net10.0/WicApp.dll';
    const png = '40-build/wic-dwrite/testdata/icc-srgb-6x5.png';
    const so = '40-build/wic-dwrite/bin/libwpfwic.so';
    if (fs.existsSync(appDll) && fs.existsSync(png) && fs.existsSync(so)) {
      const out = '/tmp/ldp-wic-icc.json';
      fs.rmSync(out, { force: true });
      const xv = startXvfb();
      try {
        if (await waitDisplay(xv.display)) {
          spawnSync('sh', ['-c',
            `DISPLAY=${xv.display} DOTNET_ROOT=${DOTNET_ROOT} DOTNET_ReadyToRun=0 ` +
            `MILBRIDGE_WIC_SO=${so} WPF_LINUX_WIN32_SHIM=${FRAMEWORK_DIR}/libwpfwin32.so WPF_LINUX_WIC_SHIM=${so} ` +
            `timeout 40 ${DOTNET_ROOT}/dotnet ${appDll} ${png} ${out} > /tmp/ldp-wic-icc-app.log 2>&1`],
            { encoding: 'utf8', timeout: 60000 });
        }
      } finally {
        xv.kill();
      }
      try { wicApp = JSON.parse(fs.readFileSync(out, 'utf8')); } catch { wicApp = null; }
    }
  }
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
    env: { ...env, dotnet_root: DOTNET_ROOT, wic_probe: wicProbe, wic_app_icc: wicApp, wic_mapping_enabled: resolverFlag === '1', windows_codecs_pinvoke_sites: Number(pInvokeCount) || 0, steps },
    expectRef: 'ACC-019：ICC',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      wicApp && wicApp.ok
        ? `缺口：**带内嵌 ICC 的 PNG 能在应用内加载**（BitmapImage：${wicApp.width}x${wicApp.height}/${wicApp.format}；profile 经 bridge 的 MilColorContextTable 传过去），` +
          `但 ①ICC 的**色彩转换**（mscms 面：CreateMultiProfileTransform/TranslateColors 等）未实现 ②因此"色差阈值"这一半无处可比（D-018 后续）`
        : wicProbe && wicProbe.loaded
        ? `缺口：libwpfwic.so 就位（工厂 hr_create=${wicProbe.hr_create}），但带 ICC 的 PNG 在应用内未加载成功（wic_app=${JSON.stringify(wicApp).slice(0, 160)}）；resolver 的 WIC 映射已开=${resolverFlag === '1'}`
        : `缺口：libwpfwic.so 未实现（resolver 的 WIC 映射已开=${resolverFlag === '1'}，WindowsCodecs P/Invoke 站点 ${pInvokeCount} 处）`,
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-019', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-019：ICC 嵌入图与 Windows 输出色差在阈值内',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
