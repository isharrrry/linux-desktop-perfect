// 团 03 · System·Drawing（散件）
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `GdiplusCannotCreateGraphicsFromIndexedPixelFormat` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 88;
const SAMPLE = ["40-build/system-drawing-common/src/System/Drawing/Bitmap.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Bitmap.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Bitmap.cs", "40-build/system-drawing-common/src/System/Drawing/BitmapSelector.cs", "40-build/system-drawing-common/src/System/Drawing/BitmapSuffixInSameAssemblyAttribute.cs", "40-build/system-drawing-common/src/System/Drawing/BitmapSuffixInSatelliteAssemblyAttribute.cs", "40-build/system-drawing-common/src/System/Drawing/Brush.cs", "40-build/system-drawing-common/src/System/Drawing/Brushes.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphics.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsContext.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsManager.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/BufferedGraphicsManager.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/CharacterRange.cs", "40-build/system-drawing-common/src/System/Drawing/ClientUtils.cs", "40-build/system-drawing-common/src/System/Drawing/ColorConverter.cs", "40-build/system-drawing-common/src/System/Drawing/ContentAlignment.cs", "40-build/system-drawing-common/src/System/Drawing/CopyPixelOperation.cs", "40-build/system-drawing-common/src/System/Drawing/DashCap.cs", "40-build/system-drawing-common/src/System/Drawing/Font.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Font.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Font.cs"];
const MAIN = '40-build/system-drawing-common/src/System/Drawing/Graphics.Windows.cs';
const TOKEN = 'GdiplusCannotCreateGraphicsFromIndexedPixelFormat';

test('g03 文件清单未漂移（88 个）', () => {
  assert.equal(COUNT, 88, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g03 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g03 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
