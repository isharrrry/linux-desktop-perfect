'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Drawing·Imaging
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

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
const ACC_CMDS = {
  "ACC-018": "node 50-verify/image/decode.mjs --format png",
  "ACC-019": "node 50-verify/image/icc.mjs --profile srgb"
};

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
  assert.ok(loc >= MIN_LOC, '源码行数 ' + loc + ' < ' + MIN_LOC);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});

test("Drawing·Imaging · ACC 判据锚定", () => {
  const doc = fs.readFileSync(path.join(ROOT, '30-design/设计文档包/08-判据先行与界面检测设计说明.md'), 'utf8');
  let withReading = 0;
  for (const acc of Object.keys(ACC_CMDS)) {
    assert.ok(doc.includes(acc), '08 文档缺 ' + acc);
    const cmd = ACC_CMDS[acc];
    const scripts = [...cmd.matchAll(/50-verify\/[\w./-]+\.mjs/g)].map((m) => m[0]);
    assert.ok(scripts.length > 0, acc + ' 的命令里没有 .mjs 脚本：' + cmd);
    for (const rel of scripts) {
      const abs = path.join(ROOT, rel);
      assert.ok(fs.existsSync(abs), acc + ' 脚本缺失：' + rel);
      execFileSync(process.execPath, ['--check', abs], { stdio: 'pipe' });
    }
    const dir = path.join(ROOT, '50-verify/读数', acc);
    if (!fs.existsSync(dir)) { console.log('SKIP ' + acc + ' 读数缺失（不入库，CI 正常）'); continue; }
    const jsons = fs.readdirSync(dir).filter((f) => /^\d{4}-\d{2}-\d{2}T.*\.json$/.exec(f)).sort();
    if (!jsons.length) { console.log('SKIP ' + acc + ' 无 JSON 读数'); continue; }
    const rec = JSON.parse(fs.readFileSync(path.join(dir, jsons[jsons.length - 1]), 'utf8'));
    assert.equal(rec.acc, acc, acc + ' 读数 acc 字段不符：' + rec.acc);
    assert.ok(['PASS', 'DEGRADED', 'FAIL', 'SKIP'].includes(rec.verdict), acc + ' verdict 非法：' + rec.verdict);
    withReading += 1;
  }
  console.log('ACC 锚定：' + withReading + '/' + Object.keys(ACC_CMDS).length + ' 条有本机读数');
});
