'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：milcore·Interop 与文本资源
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/milcore/Contracts/Interfaces.cs",
  "40-build/milcore/Contracts/MilCmd.cs",
  "40-build/milcore/Contracts/MilDrawCommand.cs",
  "40-build/milcore/Contracts/MilPrimitives.cs",
  "40-build/milcore/Contracts/MilResourceType.cs",
  "40-build/milcore/Interop/AssemblyInfo.cs",
  "40-build/milcore/Interop/Duce.cs",
  "40-build/milcore/Interop/GlobalUsings.cs",
  "40-build/milcore/Interop/HResult.cs",
  "40-build/milcore/Interop/MilEnums.cs",
  "40-build/milcore/Interop/MilExportTypes.cs",
  "40-build/milcore/Interop/MilGeometryEngine.cs",
  "40-build/milcore/Interop/MilHandleTables.cs",
  "40-build/milcore/Interop/MilNative.Exports.cs",
  "40-build/milcore/Interop/MilNative.FontFace.cs",
  "40-build/milcore/Interop/MilNative.Geometry.cs",
  "40-build/milcore/Interop/MilNative.Glyph.cs",
  "40-build/milcore/Interop/MilNative.Media.cs",
  "40-build/milcore/Interop/MilNative.Misc.cs",
  "40-build/milcore/Interop/MilNative.NotificationWindow.cs",
  "40-build/milcore/Interop/MilNative.Offscreen.cs",
  "40-build/milcore/Interop/MilNative.Window.cs",
  "40-build/milcore/Interop/MilNative.cs",
  "40-build/milcore/Interop/MilPresentProbe.cs",
  "40-build/milcore/Interop/MilPresentation.cs",
  "40-build/milcore/Interop/MilValueTypes.cs",
  "40-build/milcore/Interop/SkiaFontFileCache.cs",
  "40-build/milcore/Interop/Win32UiFont.cs",
  "40-build/milcore/Resources/MilChannel.cs",
  "40-build/milcore/Resources/MilResourceTable.cs",
  "40-build/milcore/Resources/MilResources.cs",
  "40-build/milcore/Resources/MilVisualNode.cs",
  "40-build/milcore/Resources/TransformProvenance.cs",
  "40-build/milcore/Resources/VisualProjection.cs",
  "40-build/milcore/Text/AssemblyInfo.cs",
  "40-build/milcore/Text/FontSet.cs",
  "40-build/milcore/Text/GlyphFaceCensus.cs",
  "40-build/milcore/Text/GlyphRunLayout.cs",
  "40-build/milcore/Text/GlyphRunPainter.cs",
  "40-build/milcore/Text/GlyphRunRequest.cs",
  "40-build/milcore/Text/MilGlyphRunAdapter.cs",
  "40-build/milcore/Text/TextFontDescription.cs",
  "40-build/milcore/Text/TextRenderer.cs",
  "40-build/milcore/Windowing/WindowEvent.cs",
  "40-build/milcore/Windowing/X11Display.cs",
  "40-build/milcore/Windowing/X11Native.cs",
  "40-build/milcore/Windowing/X11PresentationTarget.cs",
  "40-build/milcore/Windowing/X11Structs.cs",
  "40-build/milcore/Windowing/X11Window.cs"
];
const MIN_LOC = 14995;
const SYMBOL = "MilPresentation";

test("milcore·Interop 与文本资源 · 静态事实", () => {
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
