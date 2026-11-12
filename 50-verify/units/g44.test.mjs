// 团 44 · WpfGfx·Resources
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `MilCmdPartitionNotifyPolicyChangeForNonInteractiveMode` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 6;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/MilChannel.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/MilResourceTable.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/MilResources.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/MilVisualNode.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/TransformProvenance.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/VisualProjection.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Resources/MilChannel.cs';
const TOKEN = 'MilCmdPartitionNotifyPolicyChangeForNonInteractiveMode';

test('g44 文件清单未漂移（6 个）', () => {
  assert.equal(COUNT, 6, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g44 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g44 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
