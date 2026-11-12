'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Drawing·Printing
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/system-drawing-common/src/System/Drawing/Printing/DefaultPrintController.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/Duplex.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.Core.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/InvalidPrinterException.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/LibcupsNative.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/Margins.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/Margins.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/ModeField.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PageSettings.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperKinds.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSize.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSize.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSource.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSource.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PaperSourceKind.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPageInfo.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPrintController.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PreviewPrintController.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintAction.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintController.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintController.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintDocument.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintDocument.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintEventArgs.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintEventArgs.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintEventHandler.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintPageEventArgs.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintPageEventArgs.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintPageEventHandler.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintPreviewGraphics.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintRange.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterResolution.Serializable.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterResolution.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterResolutionKind.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterSettings.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterSettings.Windows.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterUnit.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrinterUnitConvert.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/PrintingServices.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/QueryPageSettingsEventArgs.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/QueryPageSettingsEventHandler.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/StandardPrintController.Unix.cs",
  "40-build/system-drawing-common/src/System/Drawing/Printing/TriState.cs"
];
const MIN_LOC = 7155;
const SYMBOL = "PrinterSettings";

test("Drawing·Printing · 静态事实", () => {
  let loc = 0;
  let biggest = FILES[0];
  let biggestSize = -1;
  for (const rel of FILES) {
    const abs = path.join(ROOT, rel);
    assert.ok(fs.existsSync(abs), '缺文件：' + rel);
    const raw = fs.readFileSync(abs, 'utf8');
    assert.ok(raw.length > 0, '空文件：' + rel);
    loc += raw.split('\n').length;
    if (fs.statSync(abs).size > biggestSize) { biggestSize = fs.statSync(abs).size; biggest = rel; }
  }
  assert.ok(loc >= MIN_LOC, `源码行数 ${loc} < ${MIN_LOC}`);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});
