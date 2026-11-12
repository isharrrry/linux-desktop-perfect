'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Microsoft.DotNet.Wpf.Linux·PresentationCore
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/BitmapSource.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/CachedBitmap.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FamilyCollection.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FontCacheUtil.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndKeyboardInputProvider.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndSource.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndTarget.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/ImageSourceConverter.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputManager.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputProviderSite.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MimeTypeMapper.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MouseDevice.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/OleServicesContext.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SR.g.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SecurityHelper.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SimpleTextLine.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/StylusLogic.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/TextCompositionManager.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/TextFormatterImp.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/WispTabletDeviceCollection.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/reapply-patches.py"
];
const MIN_LOC = 21777;
const SYMBOL = "presents";

test("Microsoft.DotNet.Wpf.Linux·PresentationCore · 静态事实", () => {
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
