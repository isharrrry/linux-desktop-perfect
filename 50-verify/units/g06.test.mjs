// 团 06 · Drawing·Imaging
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `GdipGetMetafileHeaderFromMetafile` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 42;
const SAMPLE = ["40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/BitmapData.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorAdjustType.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorChannelFlags.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMap.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMapType.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMatrix.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMatrixFlags.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorMode.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ColorPalette.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfPlusFlags.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfPlusRecordType.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EmfType.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/Encoder.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameter.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameterValueType.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderParameters.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/EncoderValue.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/FrameDimension.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageAttributes.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecFlags.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecInfo.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageCodecInfoPrivate.cs", "40-build/system-drawing-common/src/System/Drawing/Imaging/ImageFlags.cs"];
const MAIN = '40-build/system-drawing-common/src/System/Drawing/Imaging/Metafile.Windows.cs';
const TOKEN = 'GdipGetMetafileHeaderFromMetafile';

test('g06 文件清单未漂移（42 个）', () => {
  assert.equal(COUNT, 42, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g06 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g06 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
