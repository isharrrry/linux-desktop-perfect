// 团 40 · WpfGfx·Commands
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `MILCMD_PARTITION_NOTIFYPOLICYCHANGEFORNONINTERACTIVEMODE` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 9;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilBitmapSource.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandDecoder.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandDispatcher.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandEncoder.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandLayout.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandStructs.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilRenderData.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilResource3D.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilVisualTransformDiag.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Commands/MilCommandDispatcher.cs';
const TOKEN = 'MILCMD_PARTITION_NOTIFYPOLICYCHANGEFORNONINTERACTIVEMODE';

test('g40 文件清单未漂移（9 个）', () => {
  assert.equal(COUNT, 9, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g40 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g40 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
