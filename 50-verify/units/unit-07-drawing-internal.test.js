'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Drawing·Internal
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/system-drawing-common/src/System/Drawing/Internal/GPStream.cs",
  "40-build/system-drawing-common/src/System/Drawing/Internal/GpPathData.cs",
  "40-build/system-drawing-common/src/System/Drawing/Internal/ISystemEventTracker.cs",
  "40-build/system-drawing-common/src/System/Drawing/Internal/SystemColorTracker.cs"
];
const MIN_LOC = 375;
const SYMBOL = "GPStream";

test("Drawing·Internal · 静态事实", () => {
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
