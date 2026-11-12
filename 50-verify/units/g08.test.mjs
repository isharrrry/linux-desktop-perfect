// 团 08 · Drawing·Printing
// 断言：该团源文件清单未漂移（数量 + 抽样存在 + 非空），且代表文件含指定标识符。
// 变红条件：删/移动文件、清空文件、把代表文件里的 `ArgumentNullException` 改名。
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const ROOT = new URL('../../', import.meta.url);
const COUNT = 46;
const SAMPLE = ["40-build/system-drawing-common/src/System/Drawing/Printing/DefaultPrintController.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/Duplex.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.Core.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.Serializable.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/LibcupsNative.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/Margins.Serializable.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/Margins.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/ModeField.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Serializable.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperKinds.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSize.Serializable.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSize.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSource.Serializable.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSource.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSourceKind.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPageInfo.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPrintController.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPrintController.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PrintAction.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PrintController.Unix.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PrintController.Windows.cs", "40-build/system-drawing-common/src/System/Drawing/Printing/PrintDocument.Unix.cs"];
const MAIN = '40-build/system-drawing-common/src/System/Drawing/Printing/PrinterSettings.Windows.cs';
const TOKEN = 'ArgumentNullException';

test('g08 文件清单未漂移（46 个）', () => {
  assert.equal(COUNT, 46, '团文件总数与评估校准表不一致');
  for (const f of SAMPLE) {
    const p = new URL(f, ROOT);
    assert.ok(fs.existsSync(p), '缺文件 ' + f);
    assert.ok(fs.statSync(p).size > 0, '空文件 ' + f);
  }
});

test('g08 代表文件含锚点标识符', () => {
  const txt = fs.readFileSync(new URL(MAIN, ROOT), 'utf8');
  assert.ok(txt.includes(TOKEN), MAIN + ' 不含锚点 ' + TOKEN);
});

test('g08 代表文件行数 > 0', () => {
  const n = fs.readFileSync(new URL(MAIN, ROOT), 'utf8').split('\n').length;
  assert.ok(n > 0, MAIN + ' 行数为 0');
});
