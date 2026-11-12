'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Drawing·Drawing2D
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/AdjustableArrowCap.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/AdjustableArrowCap.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/Blend.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/BrushType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/ColorBlend.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CombineMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CompositingMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CompositingQuality.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CoordinateSpace.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCapType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/DashStyle.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/FillMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/FlushIntention.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsContainer.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPath.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPath.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPathIterator.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsState.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/HatchBrush.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/HatchStyle.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/InterpolationMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/LineCap.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/LineJoin.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/LinearGradientBrush.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/LinearGradientMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/Matrix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/MatrixOrder.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PathData.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PathGradientBrush.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PathPointType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PenAlignment.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PenType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/PixelOffsetMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/QualityMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/RegionData.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/SafeCustomLineCapHandle.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/SmoothingMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/WarpMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Drawing2D/WrapMode.cs"
];
const MIN_LOC = 3751;
const SYMBOL = "GraphicsPath";

test("Drawing·Drawing2D · 静态事实", () => {
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
