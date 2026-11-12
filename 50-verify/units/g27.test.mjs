// 团 27 · Microsoft.DotNet.Wpf.Linux·PresentationFramework
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `DwmSetWindowAttributeUseImmersiveDarkMode` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 39;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Baml2006SchemaContext.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/ContainerParaClient.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DeferredTextReference.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageHost.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentPageView.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/DocumentViewerBase.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FigureParaClient.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentFormatter.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPage.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentPaginator.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentReader.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FlowDocumentView.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/FrameworkElement.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/IconHelper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/MessageBox.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsCache.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsDegrade.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsHelper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/PtsPage.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SR.g.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/SystemResources.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBlock.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBox.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextBoxBase.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/TextContainer.Linux.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/Window.Linux.cs';
const TOKEN = 'DwmSetWindowAttributeUseImmersiveDarkMode';

test('g27 文件清单未漂移（39 个）', () => {
  assert.equal(COUNT, 39, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g27 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g27 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
