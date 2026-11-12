'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Drawing·Imaging
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorAdjustType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorChannelFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMap.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMapType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMatrix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMatrixFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorPalette.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfPlusFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfPlusRecordType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/Encoder.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameter.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameterValueType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameters.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderValue.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/FrameDimension.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageAttributes.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecInfo.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecInfoPrivate.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageFormat.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageLockMode.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetaHeader.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetaHeader.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/Metafile.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/Metafile.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileFrameUnit.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileHeader.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileHeader.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileHeaderEmf.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileHeaderWmf.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/MetafileType.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/PaletteFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/PixelFormat.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/PlayRecordCallback.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/PropertyItem.cs",
  "40-build/system-drawing-common/src/System/Drawing/Imaging/WmfPlaceableFileHeader.cs"
];
const MIN_LOC = 5024;
const SYMBOL = "Metafile";

test("Drawing·Imaging · 静态事实", () => {
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
