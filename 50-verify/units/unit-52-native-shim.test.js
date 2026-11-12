'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：native-shim·系统集成
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/native-shim/src/win32_atspi.c",
  "40-build/native-shim/src/win32_classification.c",
  "40-build/native-shim/src/win32_clipboard.c",
  "40-build/native-shim/src/win32_comstr.c",
  "40-build/native-shim/src/win32_drm_stub.c",
  "40-build/native-shim/src/win32_exports.c",
  "40-build/native-shim/src/win32_ibus.c",
  "40-build/native-shim/src/win32_imm.c",
  "40-build/native-shim/src/win32_imm.h",
  "40-build/native-shim/src/win32_isf.c",
  "40-build/native-shim/src/win32_mmio.c",
  "40-build/native-shim/src/win32_ole32.c",
  "40-build/native-shim/src/win32_print_ext.c",
  "40-build/native-shim/src/win32_psapi_dwm.c",
  "40-build/native-shim/src/win32_pt.c",
  "40-build/native-shim/src/win32_pts.c",
  "40-build/native-shim/src/win32_shell32_ext.c",
  "40-build/native-shim/src/win32_shlwapi_ext.c",
  "40-build/native-shim/src/win32_unimpl_b.c",
  "40-build/native-shim/src/win32_urlmon_ext.c",
  "40-build/native-shim/src/win32_uxtheme.c",
  "40-build/native-shim/src/win32_winforms_extra.c",
  "40-build/native-shim/src/win32_winspool.c"
];
const MIN_LOC = 15815;
const SYMBOL = "wpf_pts_ni_diag_on";

test("native-shim·系统集成 · 静态事实", () => {
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
