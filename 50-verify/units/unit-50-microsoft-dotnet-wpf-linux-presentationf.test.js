'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Microsoft.DotNet.Wpf.Linux·PresentationFramework·窗口与构建
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Baml2006SchemaContext.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FrameworkElement.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/IconHelper.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/MessageBox.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SR.g.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SystemResources.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Window.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxMessageBox.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxReaderPageHost.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxSplashScreen.Linux.cs"
];
const MIN_LOC = 18862;
const SYMBOL = "Window";

test("Microsoft.DotNet.Wpf.Linux·PresentationFramework·窗口与构建 · 静态事实", () => {
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
