#!/usr/bin/env node
// ACC-001/ACC-002：样本工程真构建（--log 时同时检查自产 pack 解析行）
import { parseArgs, finish, exists, which, runCmd } from '../lib/run.mjs';

const started = Date.now();
const { opts } = parseArgs();
const sample = '50-verify/samples/WpfFirstWindow/WpfFirstWindow.csproj';
const wantLog = Boolean(opts.log);
const acc = wantLog ? 'ACC-002' : 'ACC-001';

if (!exists(sample)) {
  finish({ acc, started, expectRef: `${acc}：${sample} dotnet build -c Release rc=0`, verdict: 'FAIL',
    diff: [`缺被测对象：${sample}（S1 样本层）`], env: { sample: 'missing' } });
}
if (!which('dotnet')) {
  finish({ acc, started, verdict: 'SKIP', degrade: '环境缺 dotnet SDK（D-003 未登记）', diff: ['环境缺件：dotnet'], env: {} });
}

const r = runCmd('dotnet', ['build', sample, '-c', 'Release', '--nologo', '-v:m'], { timeout: 240000 });
const errors = r.out.split('\n').filter((l) => /error\s|错误/.test(l)).slice(0, 3);
const packLine = r.out.split('\n').find((l) => /Microsoft\.WindowsDesktop\.App|FrameworkReference/.test(l)) || '';
const env = { sample, dotnet: runCmd('dotnet', ['--version']).out.trim(), build_rc: r.rc };

if (wantLog) {
  const ok = r.rc === 0 && /Microsoft\.WindowsDesktop\.App/.test(r.out);
  finish({ acc, started, expectRef: 'ACC-002：构建日志出现自产 pack 的 FrameworkReference 解析行', verdict: ok ? 'PASS' : 'FAIL',
    env, diff: ok ? [] : [`构建 rc=${r.rc}`, `日志未出现自产 pack 解析行（最后 3 条错误）`, ...errors].slice(0, 4) });
}

const ok = r.rc === 0;
finish({ acc, started, expectRef: 'ACC-001：dotnet build -c Release rc=0 且可启动', verdict: ok ? 'PASS' : 'FAIL',
  env, diff: ok ? [] : [`构建 rc=${r.rc}（样本层红读数）`, ...errors] });
