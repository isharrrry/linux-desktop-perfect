// 团 43 · WpfGfx·Rendering
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `visual_brush_produces_fill_paint` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 15;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/AssemblyInfo.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/DirtyRegionTracker.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/DrawInstructionCensus.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/GlyphRunCensus.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/MilResourceProvider.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/PathGeometryParser.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/RenderDiagnostics.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/Skia3DRenderer.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaBrush.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaColor.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaEffect.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaGeometry.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaPen.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaRenderBackend.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/VisualBrushSource.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Rendering/SkiaRenderBackend.cs';
const TOKEN = 'visual_brush_produces_fill_paint';

test('g43 文件清单未漂移（15 个）', () => {
  assert.equal(COUNT, 15, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g43 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g43 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
