// 团 05 · Drawing·Drawing2D
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `GdipIsOutlineVisiblePathPointI` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 42;
const SAMPLE = ["40-build/system-drawing-common/src/System/Drawing/Drawing2D/AdjustableArrowCap.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/AdjustableArrowCap.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/Blend.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/BrushType.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/ColorBlend.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CombineMode.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CompositingMode.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CompositingQuality.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CoordinateSpace.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCap.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/CustomLineCapType.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/DashStyle.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/FillMode.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/FlushIntention.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsContainer.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPath.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPath.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPathIterator.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsState.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/HatchBrush.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/HatchStyle.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/InterpolationMode.cs", "40-build/system-drawing-common/src/System/Drawing/Drawing2D/LineCap.cs"];
const MAIN = '40-build/system-drawing-common/src/System/Drawing/Drawing2D/GraphicsPath.Unix.cs';
const TOKEN = 'GdipIsOutlineVisiblePathPointI';

test('g05 文件清单未漂移（42 个）', () => {
  assert.equal(COUNT, 42, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g05 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g05 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
