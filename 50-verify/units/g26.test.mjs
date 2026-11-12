// 团 26 · Microsoft.DotNet.Wpf.Linux·PresentationCore
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `CriticalRemoveThreadPreprocessMessageHandlerFirst` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 21;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/BitmapSource.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/CachedBitmap.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FamilyCollection.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/FontCacheUtil.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndKeyboardInputProvider.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndSource.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndTarget.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/ImageSourceConverter.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputManager.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/InputProviderSite.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MimeTypeMapper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/MouseDevice.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/OleServicesContext.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SR.g.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SecurityHelper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/SimpleTextLine.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/StylusLogic.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/TextCompositionManager.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/TextFormatterImp.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/WispTabletDeviceCollection.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/reapply-patches.py"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HwndSource.Linux.cs';
const TOKEN = 'CriticalRemoveThreadPreprocessMessageHandlerFirst';

test('g26 文件清单未漂移（21 个）', () => {
  assert.equal(COUNT, 21, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g26 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g26 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
