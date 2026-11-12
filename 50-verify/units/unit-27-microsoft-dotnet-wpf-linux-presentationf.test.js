'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Microsoft.DotNet.Wpf.Linux·PresentationFramework
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Baml2006SchemaContext.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/ContainerParaClient.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DeferredTextReference.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageHost.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageView.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentViewerBase.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FigureParaClient.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentFormatter.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPage.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPaginator.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentReader.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentView.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FrameworkElement.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/IconHelper.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/MessageBox.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsCache.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsDegrade.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsHelper.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsPage.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SR.g.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SystemResources.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBlock.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBox.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBoxBase.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextContainer.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextEditorTyping.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextParaClient.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Window.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxChainProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxCpDcpMapProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxEHandleProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxLineBreakProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxLsSessionProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxMessageBox.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxPageVisProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxReaderPageHost.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxSplashScreen.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxTextParaChainProbe.Linux.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/WpfLinuxTextSrcProbe.Linux.cs"
];
const MIN_LOC = 51117;
const SYMBOL = "Window";

test("Microsoft.DotNet.Wpf.Linux·PresentationFramework · 静态事实", () => {
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
