'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：WpfGfx·Interop
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/AssemblyInfo.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Duce.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/GlobalUsings.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/HResult.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilEnums.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilExportTypes.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilGeometryEngine.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilHandleTables.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Exports.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.FontFace.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Geometry.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Glyph.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Media.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Misc.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.NotificationWindow.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Offscreen.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.Window.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilNative.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentProbe.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilPresentation.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/MilValueTypes.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/SkiaFontFileCache.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/Interop/Win32UiFont.cs"
];
const MIN_LOC = 10568;
const SYMBOL = "MilPresentation";
const ACC_CMDS = {
  "ACC-029": "node 50-verify/index/run-all.mjs --dry",
  "ACC-012": "node 50-verify/window/clipboard.mjs"
};

test("WpfGfx·Interop · 静态事实", () => {
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
  assert.ok(loc >= MIN_LOC, '源码行数 ' + loc + ' < ' + MIN_LOC);
  assert.ok(fs.readFileSync(path.join(ROOT, biggest), 'utf8').includes(SYMBOL), '缺符号：' + SYMBOL);
});

test("WpfGfx·Interop · ACC 判据锚定", () => {
  const doc = fs.readFileSync(path.join(ROOT, '30-design/设计文档包/08-判据先行与界面检测设计说明.md'), 'utf8');
  let withReading = 0;
  for (const acc of Object.keys(ACC_CMDS)) {
    assert.ok(doc.includes(acc), '08 文档缺 ' + acc);
    const cmd = ACC_CMDS[acc];
    const scripts = [...cmd.matchAll(/50-verify\/[\w./-]+\.mjs/g)].map((m) => m[0]);
    assert.ok(scripts.length > 0, acc + ' 的命令里没有 .mjs 脚本：' + cmd);
    for (const rel of scripts) {
      const abs = path.join(ROOT, rel);
      assert.ok(fs.existsSync(abs), acc + ' 脚本缺失：' + rel);
      execFileSync(process.execPath, ['--check', abs], { stdio: 'pipe' });
    }
    const dir = path.join(ROOT, '50-verify/读数', acc);
    if (!fs.existsSync(dir)) { console.log('SKIP ' + acc + ' 读数缺失（不入库，CI 正常）'); continue; }
    const jsons = fs.readdirSync(dir).filter((f) => /^\d{4}-\d{2}-\d{2}T.*\.json$/.exec(f)).sort();
    if (!jsons.length) { console.log('SKIP ' + acc + ' 无 JSON 读数'); continue; }
    const rec = JSON.parse(fs.readFileSync(path.join(dir, jsons[jsons.length - 1]), 'utf8'));
    assert.equal(rec.acc, acc, acc + ' 读数 acc 字段不符：' + rec.acc);
    assert.ok(['PASS', 'DEGRADED', 'FAIL', 'SKIP'].includes(rec.verdict), acc + ' verdict 非法：' + rec.verdict);
    withReading += 1;
  }
  console.log('ACC 锚定：' + withReading + '/' + Object.keys(ACC_CMDS).length + ' 条有本机读数');
});
