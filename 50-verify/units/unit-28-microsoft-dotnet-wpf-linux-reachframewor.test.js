'use strict';
// 由 50-verify/units/gen-units.mjs 生成，勿手改。
// 团：Microsoft.DotNet.Wpf.Linux·ReachFramework
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const ROOT = path.resolve(__dirname, '..', '..');
const FILES = [
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/SR.Generated.MSInternal.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/SR.Generated.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/SR.g.cs",
  "40-build/wpf-port/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/reapply-patches.py"
];
const MIN_LOC = 369;
const SYMBOL = "SR";
const ACC_CMDS = {
  "ACC-024": "node 50-verify/print/cups-submit.mjs",
  "ACC-025": "node 50-verify/doc/flowreader.mjs"
};

test("Microsoft.DotNet.Wpf.Linux·ReachFramework · 静态事实", () => {
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

test("Microsoft.DotNet.Wpf.Linux·ReachFramework · ACC 判据锚定", () => {
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
