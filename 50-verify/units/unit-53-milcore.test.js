'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：milcore·渲染与命令
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/milcore/Commands/MilBitmapSource.cs",
  "40-build/milcore/Commands/MilCommandDecoder.cs",
  "40-build/milcore/Commands/MilCommandDispatcher.cs",
  "40-build/milcore/Commands/MilCommandEncoder.cs",
  "40-build/milcore/Commands/MilCommandLayout.cs",
  "40-build/milcore/Commands/MilCommandStructs.cs",
  "40-build/milcore/Commands/MilRenderData.cs",
  "40-build/milcore/Commands/MilResource3D.cs",
  "40-build/milcore/Commands/MilVisualTransformDiag.cs",
  "40-build/milcore/Rendering/AssemblyInfo.cs",
  "40-build/milcore/Rendering/DrawInstructionCensus.cs",
  "40-build/milcore/Rendering/GlyphRunCensus.cs",
  "40-build/milcore/Rendering/MilResourceProvider.cs",
  "40-build/milcore/Rendering/PathGeometryParser.cs",
  "40-build/milcore/Rendering/RenderDiagnostics.cs",
  "40-build/milcore/Rendering/SkiaBrush.cs",
  "40-build/milcore/Rendering/SkiaColor.cs",
  "40-build/milcore/Rendering/SkiaEffect.cs",
  "40-build/milcore/Rendering/SkiaGeometry.cs",
  "40-build/milcore/Rendering/SkiaPen.cs",
  "40-build/milcore/Rendering/SkiaRenderBackend.cs",
  "40-build/milcore/Rendering/VisualBrushSource.cs"
];
const MIN_LOC = 8247;
const SYMBOL = "MilCommandDispatcher";
const ACC_CMDS = {
  "ACC-006": "node 50-verify/render/duce-coverage.mjs",
  "ACC-007": "node 50-verify/render/frame-probe.mjs --scr SCR-04 --frames 300"
};

test("milcore·渲染与命令 · 静态事实", () => {
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

test("milcore·渲染与命令 · ACC 判据锚定", () => {
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

test("milcore·渲染与命令 · 闸门覆盖", () => {
  const sh = fs.readFileSync(path.join(ROOT, '50-verify/coverage.sh'), 'utf8');
  const prefixes = [...sh.matchAll(/'([\w.]+)\*'/g)].map((m) => m[1]);
  assert.ok(prefixes.length > 0, 'coverage.sh 无包前缀');
  const hit = FILES.filter((rel) => rel.split('/').slice(0, -1).some((d) => prefixes.some((p) => d.startsWith(p))));
  assert.ok(hit.length > 0, '团内无文件落在 coverage.sh 前缀下');
});
