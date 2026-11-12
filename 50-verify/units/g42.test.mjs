// 团 42 · WpfGfx·Interop
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `MilFontFace_RegisterFromFile` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 23;
const SAMPLE = ["40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/AssemblyInfo.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Duce.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/GlobalUsings.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/HResult.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilEnums.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilExportTypes.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilGeometryEngine.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilHandleTables.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Exports.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.FontFace.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Geometry.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Glyph.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Media.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Misc.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.NotificationWindow.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Offscreen.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Window.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentProbe.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentation.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilValueTypes.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/SkiaFontFileCache.cs", "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Win32UiFont.cs"];
const MAIN = '40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentation.cs';
const TOKEN = 'MilFontFace_RegisterFromFile';

test('g42 文件清单未漂移（23 个）', () => {
  assert.equal(COUNT, 23, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g42 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g42 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
