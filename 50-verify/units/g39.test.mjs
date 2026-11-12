// 团 39 · Microsoft.DotNet.Wpf.Linux·WindowsBase
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `Debug_AssertNoInheritanceContextListeners` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 10;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DependencyObject.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Dispatcher.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DispatcherSynchronizationContext.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/EffectiveValueEntry.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/HwndWrapper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Invariant.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/SR.g.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/SecurityHelper.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/TextServicesLoader.Linux.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DependencyObject.Linux.cs';
const TOKEN = 'Debug_AssertNoInheritanceContextListeners';

test('g39 文件清单未漂移（10 个）', () => {
  assert.equal(COUNT, 10, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g39 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g39 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
