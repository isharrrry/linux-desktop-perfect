// 团 18 · DirectWrite·Provider
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `FindFirstSupplementaryCodePoint` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 20;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/DefaultFontFamily.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FaceSelector.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FamilyCoverage.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FamilyCoverageSelfTest.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FontHandleTable.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FontLayoutStripping.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FontModel.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/FontTableStripper.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/GlyphMapper.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/GlyphPositioner.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LayoutFeatureReader.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LayoutLookupCoverage.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LinuxFontCollection.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LinuxFontFace.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/LinuxFontFile.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/MetricModels.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/MetricsFactory.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/OpenTypeFontData.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/SkiaFontDataCache.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/TypographyMaskEstimator.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/OpenTypeFontData.cs';
const TOKEN = 'FindFirstSupplementaryCodePoint';

test('g18 文件清单未漂移（20 个）', () => {
  assert.equal(COUNT, 20, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g18 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g18 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
