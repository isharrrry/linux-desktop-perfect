'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：native-shim·核心与图形
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/native-shim/src/milqi_shim.c",
  "40-build/native-shim/src/win32_abi.h",
  "40-build/native-shim/src/win32_com_misc.c",
  "40-build/native-shim/src/win32_core.c",
  "40-build/native-shim/src/win32_gdi_dc.c",
  "40-build/native-shim/src/win32_gdi_dc.h",
  "40-build/native-shim/src/win32_gdi_text.c",
  "40-build/native-shim/src/win32_gdip_render.c",
  "40-build/native-shim/src/win32_gdiplus.c",
  "40-build/native-shim/src/win32_internal.h",
  "40-build/native-shim/src/win32_kernel32.c",
  "40-build/native-shim/src/win32_misc.c",
  "40-build/native-shim/src/win32_misc_ext.c",
  "40-build/native-shim/src/win32_msg.c",
  "40-build/native-shim/src/win32_oem.c",
  "40-build/native-shim/src/win32_penimc.c",
  "40-build/native-shim/src/win32_unicode_tables.c",
  "40-build/native-shim/src/win32_user32x.c",
  "40-build/native-shim/src/win32_x11.c"
];
const MIN_LOC = 16005;
const SYMBOL = "WPF_TBL";

test("native-shim·核心与图形 · 静态事实", () => {
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
