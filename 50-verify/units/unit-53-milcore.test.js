'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：milcore·渲染与命令
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/milcore/Commands/MilBitmapSource.cs",
  "40-build/milcore/Commands/MilCommandDecoder.cs",
  "40-build/milcore/Commands/MilCommandDispatcher.cs",
  "40-build/milcore/Commands/MilCommandEncoder.cs",
  "40-build/milcore/Commands/MilCommandLayout.cs",
  "40-build/milcore/Commands/MilCommandStructs.cs",
  "40-build/milcore/Commands/MilRenderData.cs",
  "40-build/milcore/Commands/MilResource3D.cs",
  "40-build/milcore/Commands/MilVisualTransformDiag.cs",
  "40-build/milcore/Rendering/AssemblyInfo.cs",
  "40-build/milcore/Rendering/DrawInstructionCensus.cs",
  "40-build/milcore/Rendering/GlyphRunCensus.cs",
  "40-build/milcore/Rendering/MilResourceProvider.cs",
  "40-build/milcore/Rendering/PathGeometryParser.cs",
  "40-build/milcore/Rendering/RenderDiagnostics.cs",
  "40-build/milcore/Rendering/SkiaBrush.cs",
  "40-build/milcore/Rendering/SkiaColor.cs",
  "40-build/milcore/Rendering/SkiaEffect.cs",
  "40-build/milcore/Rendering/SkiaGeometry.cs",
  "40-build/milcore/Rendering/SkiaPen.cs",
  "40-build/milcore/Rendering/SkiaRenderBackend.cs",
  "40-build/milcore/Rendering/VisualBrushSource.cs"
];
const MIN_LOC = 8247;
const SYMBOL = "MilCommandDispatcher";

test("milcore·渲染与命令 · 静态事实", () => {
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
