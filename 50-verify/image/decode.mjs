#!/usr/bin/env node
// ACC-018：WIC 五格式解码
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
    ...{ format: opts.format || "png", sample_images: exists("samples/WpfImageGallery/images") },
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
  // WIC 解码链路探针（D-018 第二步）：工厂 → 从文件名建解码器 → GetSize。
  //   有 testdata 的 PNG 时跑真解，读数里带 dims —— 这样"解码真的通了"是可验的，不是靠推断。
  let wicDecode = null;
  let wicStream = null;
  {
    const png = '40-build/wic-dwrite/testdata/rgb-7x5.png';
    const probe = '40-build/wic-dwrite/bin/wic-dec-probe';
    if (fs.existsSync(probe) && fs.existsSync(png)) {
      const r = spawnSync(probe, [png], { encoding: 'utf8', timeout: 30000 });
      const line = (r.stdout || '').trim().split(String.fromCharCode(10)).pop();
      try { wicDecode = JSON.parse(line); } catch { wicDecode = { raw: line }; }
    }
    // 流入口（WPF 主路径）：CreateDecoderFromStream ← LdpMilStreamRead 约定
    const spr = '40-build/wic-dwrite/bin/wic-stream-probe';
    if (fs.existsSync(spr) && fs.existsSync(png)) {
      const r = spawnSync(spr, [png], { encoding: 'utf8', timeout: 30000 });
      const line = (r.stdout || '').trim().split(String.fromCharCode(10)).pop();
      try { wicStream = JSON.parse(line); } catch { wicStream = { raw: line }; }
    }
  }
  // 应用内解码（端到端）：WicApp 用自产 WPF 的 BitmapImage 加载 testdata 的 PNG。
  //   现状（D-018）：resolver 已把 WindowsCodecs 映射到 libwpfwic，WPF 依次调用
  //   CreateDecoderFromFileHandle（已实现）→ GetDecoderInfo（未实现）⇒ EntryPointNotFound。
  //   读数里带 error_type，缺口演进一目了然。
  let wicApp = null;
  {
    const appDll = '50-verify/image/wic-app/bin/Release/net10.0/WicApp.dll';
    const png = '40-build/wic-dwrite/testdata/rgb-7x5.png';
    const outJson = '/tmp/ldp-wic-result.json';
    const so = '40-build/wic-dwrite/bin/libwpfwic.so';
    if (fs.existsSync(appDll) && fs.existsSync(png) && fs.existsSync(so)) {
      const cases = [
        { name: 'rgb-7x5', png, out: outJson },
        { name: 'rgba-4x3', png: '40-build/wic-dwrite/testdata/rgba-4x3.png', out: '/tmp/ldp-wic-rgba.json' },
      ];
      const results = [];
      const xv = startXvfb();
      try {
        const ready = await waitDisplay(xv.display);
        for (const c of cases) {
          if (!fs.existsSync(c.png)) { results.push({ case: c.name, ok: false, reason: '缺 PNG' }); continue; }
          fs.rmSync(c.out, { force: true });
          if (ready) {
            spawnSync('sh', ['-c',
              `DISPLAY=${xv.display} DOTNET_ROOT=${DOTNET_ROOT} DOTNET_ReadyToRun=0 ` +
              `MILBRIDGE_WIC_SO=${so} WPF_LINUX_WIN32_SHIM=${FRAMEWORK_DIR}/libwpfwin32.so WPF_LINUX_WIC_SHIM=${so} ` +
              `timeout 40 ${DOTNET_ROOT}/dotnet ${appDll} ${c.png} ${c.out} > /tmp/ldp-wic-app.log 2>&1`],
              { encoding: 'utf8', timeout: 60000 });
          }
          let parsed = null;
          try { parsed = JSON.parse(fs.readFileSync(c.out, 'utf8')); } catch { parsed = null; }
          results.push({ case: c.name, ok: !!(parsed && parsed.ok), ...(parsed || { reason: '无读数' }) });
        }
      } finally {
        xv.kill();
      }
      wicApp = { cases: results };
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
    acc: 'ACC-018', started,
    env: { ...env, dotnet_root: DOTNET_ROOT, wic_probe: wicProbe, wic_decode_chain: wicDecode, wic_stream_chain: wicStream, wic_app: wicApp, wic_mapping_enabled: resolverFlag === '1', windows_codecs_pinvoke_sites: Number(pInvokeCount) || 0, steps },
    expectRef: 'ACC-018：五格式',
    verdict: 'FAIL',
    diff: [
      ...missingNotes,
      ...bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 240)}`),
      wicApp && wicApp.cases && wicApp.cases.length > 0 && wicApp.cases.every((c) => c.ok)
        ? `缺口：**PNG 的应用内解码已通**（BitmapImage 真加载：${wicApp.cases.map((c) => `${c.case}=${c.width}x${c.height}/${c.format}`).join('、')}；` +
          `探针层：文件名 ${wicDecode.dims.join('x')}、流入口 ${wicStream && wicStream.hr_stream === 0 ? wicStream.dims.join('x') : '未通'}），` +
          `但 ①五格式里的 JPEG/TIFF/GIF/BMP 未实现 ②ICC 未做（D-018 后续）`
        : wicDecode && wicDecode.loaded && wicDecode.hr_size === 0 && wicDecode.dims && wicDecode.dims[0] > 0
        ? `缺口：**PNG 解码链路已通**（文件名入口 ${wicDecode.dims.join('x')}${wicStream && wicStream.hr_stream === 0 ? `、流入口 ${wicStream.dims.join('x')}` : '、流入口未通'}），但 ①应用内（BitmapImage）未通 ②五格式里的 JPEG/TIFF/GIF/BMP 未实现 ③ICC 未做（D-018 后续）`
        : wicProbe && wicProbe.loaded
        ? `缺口：libwpfwic.so 骨架已就位（工厂入口可建 hr_create=${wicProbe.hr_create}），但解码入口明确返回 E_NOTIMPL（hr_decode=${wicProbe.hr_decode}）⇒ 五格式解码/ICC 仍未实现（D-018 后续）；resolver 的 WIC 映射已开=${resolverFlag === '1'}，WindowsCodecs P/Invoke 站点 ${pInvokeCount} 处`
        : `缺口：libwpfwic.so 未实现（resolver 的 WIC 映射已开=${resolverFlag === '1'}，WindowsCodecs P/Invoke 站点 ${pInvokeCount} 处）`,
    ],
  });
}

// 被测对象齐备时才做真检查（当前路径下尚未到达）
finish({
  acc: 'ACC-018', started, env, verdict: 'FAIL', degrade: 'none',
  expectRef: 'ACC-018：png/jpeg/gif/bmp/tiff 解码像素与基准一致',
  diff: ['被测对象齐备，但本条断言尚未接入（判据实现未完成，不得计为通过）', '依赖切片：见 08 §4 矩阵对应门'],
});
