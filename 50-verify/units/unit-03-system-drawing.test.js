'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：System·Drawing（散件）
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/system-drawing-common/src/System/Drawing/Bitmap.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Bitmap.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Bitmap.cs",
  "40-build/system-drawing-common/src/System/Drawing/BitmapSelector.cs",
  "40-build/system-drawing-common/src/System/Drawing/BitmapSuffixInSameAssemblyAttribute.cs",
  "40-build/system-drawing-common/src/System/Drawing/BitmapSuffixInSatelliteAssemblyAttribute.cs",
  "40-build/system-drawing-common/src/System/Drawing/Brush.cs",
  "40-build/system-drawing-common/src/System/Drawing/Brushes.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsManager.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsManager.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/CharacterRange.cs",
  "40-build/system-drawing-common/src/System/Drawing/ClientUtils.cs",
  "40-build/system-drawing-common/src/System/Drawing/ColorConverter.cs",
  "40-build/system-drawing-common/src/System/Drawing/ContentAlignment.cs",
  "40-build/system-drawing-common/src/System/Drawing/CopyPixelOperation.cs",
  "40-build/system-drawing-common/src/System/Drawing/DashCap.cs",
  "40-build/system-drawing-common/src/System/Drawing/Font.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Font.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Font.cs",
  "40-build/system-drawing-common/src/System/Drawing/FontFamily.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/FontFamily.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/FontFamily.cs",
  "40-build/system-drawing-common/src/System/Drawing/FontStyle.cs",
  "40-build/system-drawing-common/src/System/Drawing/GdiPlusStreamHelper.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Gdiplus.cs",
  "40-build/system-drawing-common/src/System/Drawing/GdiplusNative.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/GdiplusNative.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/GdiplusNative.cs",
  "40-build/system-drawing-common/src/System/Drawing/Graphics.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Graphics.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Graphics.cs",
  "40-build/system-drawing-common/src/System/Drawing/GraphicsContext.cs",
  "40-build/system-drawing-common/src/System/Drawing/GraphicsUnit.cs",
  "40-build/system-drawing-common/src/System/Drawing/IDeviceContext.cs",
  "40-build/system-drawing-common/src/System/Drawing/Icon.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Icon.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Image.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Image.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Image.cs",
  "40-build/system-drawing-common/src/System/Drawing/ImageAnimator.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/ImageAnimator.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/ImageInfo.cs",
  "40-build/system-drawing-common/src/System/Drawing/ImageType.cs",
  "40-build/system-drawing-common/src/System/Drawing/LibX11Functions.cs",
  "40-build/system-drawing-common/src/System/Drawing/LibraryResolver.cs",
  "40-build/system-drawing-common/src/System/Drawing/LocalAppContextSwitches.cs",
  "40-build/system-drawing-common/src/System/Drawing/MarshallingHelpers.cs",
  "40-build/system-drawing-common/src/System/Drawing/NativeMethods.cs",
  "40-build/system-drawing-common/src/System/Drawing/NativeStructs.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Pen.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Pen.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Pen.cs",
  "40-build/system-drawing-common/src/System/Drawing/Pens.cs",
  "40-build/system-drawing-common/src/System/Drawing/PointConverter.cs",
  "40-build/system-drawing-common/src/System/Drawing/PropertyItemInternal.cs",
  "40-build/system-drawing-common/src/System/Drawing/RectangleConverter.cs",
  "40-build/system-drawing-common/src/System/Drawing/Region.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Region.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Region.cs",
  "40-build/system-drawing-common/src/System/Drawing/RotateFlipType.cs",
  "40-build/system-drawing-common/src/System/Drawing/SRDescriptionAttribute.cs",
  "40-build/system-drawing-common/src/System/Drawing/ScreenDC.cs",
  "40-build/system-drawing-common/src/System/Drawing/SizeConverter.cs",
  "40-build/system-drawing-common/src/System/Drawing/SolidBrush.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringAlignment.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringDigitSubstitute.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringFormat.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringFormatFlags.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringTrimming.cs",
  "40-build/system-drawing-common/src/System/Drawing/StringUnit.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemBrushes.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemFonts.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemFonts.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemFonts.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemIcons.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemIcons.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/SystemPens.cs",
  "40-build/system-drawing-common/src/System/Drawing/TextureBrush.cs",
  "40-build/system-drawing-common/src/System/Drawing/ToolboxBitmapAttribute.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/ToolboxBitmapAttribute.cs",
  "40-build/system-drawing-common/src/System/Drawing/UnsafeNativeMethods.cs",
  "40-build/system-drawing-common/src/System/Drawing/macFunctions.cs"
];
const MIN_LOC = 21884;
const SYMBOL = "Graphics";
const ACC_CMDS = {
  "ACC-018": "node 50-verify/image/decode.mjs --format png",
  "ACC-006": "node 50-verify/render/duce-coverage.mjs"
};

test("System·Drawing（散件） · 静态事实", () => {
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

test("System·Drawing（散件） · ACC 判据锚定", () => {
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

test("System·Drawing（散件） · 闸门覆盖", () => {
  const sh = fs.readFileSync(path.join(ROOT, '50-verify/coverage.sh'), 'utf8');
  const prefixes = [...sh.matchAll(/'([\w.]+)\*'/g)].map((m) => m[1]);
  assert.ok(prefixes.length > 0, 'coverage.sh 无包前缀');
  const hit = FILES.filter((rel) => rel.split('/').slice(0, -1).some((d) => prefixes.some((p) => d.startsWith(p))));
  assert.ok(hit.length > 0, '团内无文件落在 coverage.sh 前缀下');
});
