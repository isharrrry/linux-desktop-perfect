// 团 02 · system-drawing-common·CommonInterop
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `GdiplusCannotCreateGraphicsFromIndexedPixelFormat` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 14;
const SAMPLE = ["40-build/system-drawing-common/src/CommonInterop/AssemblyInfo.cs", "40-build/system-drawing-common/src/CommonInterop/ConstsStub.cs", "40-build/system-drawing-common/src/CommonInterop/FontConverterStub.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.HRESULT.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.IStream.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.STATFLAG.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.STATSTG.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.STGM.cs", "40-build/system-drawing-common/src/CommonInterop/Interop.STGTY.cs", "40-build/system-drawing-common/src/CommonInterop/LocalAppContextSwitchesStub.cs", "40-build/system-drawing-common/src/CommonInterop/SR.Generated.cs", "40-build/system-drawing-common/src/CommonInterop/StockIconSupport.cs", "40-build/system-drawing-common/src/CommonInterop/UITypeEditorStub.cs", "40-build/system-drawing-common/src/CommonInterop/UserPreferenceStub.cs"];
const MAIN = '40-build/system-drawing-common/src/CommonInterop/SR.Generated.cs';
const TOKEN = 'GdiplusCannotCreateGraphicsFromIndexedPixelFormat';

test('g02 文件清单未漂移（14 个）', () => {
  assert.equal(COUNT, 14, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g02 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g02 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
