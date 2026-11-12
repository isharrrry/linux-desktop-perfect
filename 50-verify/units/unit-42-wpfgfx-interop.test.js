'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：WpfGfx·Interop
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/AssemblyInfo.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Duce.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/GlobalUsings.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/HResult.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilEnums.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilExportTypes.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilGeometryEngine.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilHandleTables.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Exports.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.FontFace.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Geometry.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Glyph.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Media.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Misc.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.NotificationWindow.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Offscreen.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Window.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentProbe.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentation.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilValueTypes.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/SkiaFontFileCache.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Win32UiFont.cs"
];
const MIN_LOC = 10568;
const SYMBOL = "MilPresentation";

test("WpfGfx·Interop · 静态事实", () => {
  let loc = 0;
  let biggest = FILES[0];
  let biggestSize = -1;
  for (const rel of FILES) {
    const abs = path.join(ROOT, rel);
    assert.ok(fs.existsSync(abs), '缺文件：' + rel);
    const raw = fs.readFileSync(abs, 'utf8');
    assert.ok(raw.length > 0, '空文件：' + rel);
    loc += raw.split('\n').length;
    if (fs.statSync(abs).size > biggestSize) { biggestSize = fs.statSync(abs).size; biggest = rel; }
  }
  assert.ok(loc >= MIN_LOC, `源码行数 ${loc} < ${MIN_LOC}`);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});
