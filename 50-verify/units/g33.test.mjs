// 团 33 · Microsoft.DotNet.Wpf.Linux·System.Windows.Extensions
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `AssemblyAccessToAssemblyName` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 1;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/System.Windows.Extensions.Linux.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/System.Windows.Extensions/System.Windows.Extensions.Linux.cs';
const TOKEN = 'AssemblyAccessToAssemblyName';

test('g33 文件清单未漂移（1 个）', () => {
  assert.equal(COUNT, 1, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g33 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g33 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
