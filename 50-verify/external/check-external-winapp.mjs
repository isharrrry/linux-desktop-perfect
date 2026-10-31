#!/usr/bin/env node
// ACC-031（TG-EXT）：**外部程序集**能否直接运行 —— 本仓的核心目标验收。
//
// 为什么单独一条：ACC-001..030 测的全是 samples/ 里本仓自己编的样本。
// 「Windows 上编译好的程序拷过来能跑」这件事，之前**零覆盖**。
// 这里用 50-verify/external/winapp/（真实 Windows 机器上 dotnet build 出来的）当被测对象。
//
// 三条断言：
//   ① 启动：UIA 树能导出，且根节点是 frame/WinApp；
//   ② 内容：树里有 hello-from-windows / btn / clicks=0 三个元素；
//   ③ 交互：xdotool 点 btn 之后 clicks=0 变成 clicks=1（证明不只是"画出来"）。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { parseArgs, finish } from '../lib/run.mjs';
import { startXvfb, waitDisplay, sleep, findWindow, waitMapped, DOTNET_ROOT } from '../lib/app-session.mjs';

const started = Date.now();
const { opts } = parseArgs();
const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const APPDIR = path.join(ROOT, '50-verify/external/winapp');
const DLL = path.join(APPDIR, 'WinApp.dll');
// `DOTNET_ROOT` 从 app-session.mjs 导进来时可能是**相对路径**（`../../../.local/...`），
//   path.join 之后会得到一个相对路径，导致 existsSync 判为"缺被测对象"（本轮实测踩到两次）。
//   这里直接按 home 拼绝对路径，最稳。
const DOTNET_ROOT_ABS = path.join(os.homedir(), '.local/share/ldp/dotnet');
const FRAMEWORK_DIR = path.join(DOTNET_ROOT_ABS, 'shared/Microsoft.WindowsDesktop.App/10.0.11');
const SHIM = path.join(FRAMEWORK_DIR, 'libwpfwin32.so');

const probes = [
    { path: DLL, why: '外部 Windows 程序集（50-verify/external/winapp/WinApp.dll）' },
    { path: SHIM, why: 'win32 shim（node 40-build/native-shim && cp …）' },
    { path: path.join(FRAMEWORK_DIR, 'LdpA11y.dll'), why: 'UIA 树导出钩子' },
];
// 【注】lib/run.mjs 的 exists() 是**相对路径**语义（path.join(ROOT, rel)），
//   而这里的探针是绝对路径 ⇒ 必须用 fs.existsSync（本轮实测踩到：LdpA11y.dll 明明在却报缺）。
const missing = probes.filter((p) => !fs.existsSync(p.path));
const missingNotes = missing.map((m) => `缺被测对象：${path.relative(ROOT, m.path)}（${m.why}）`);

const env = {
    external_app: path.relative(ROOT, DLL),
    // 这条是"从 Windows 拷来"的证据：runtimeconfig 要求 10.0.0，我们的是 10.0.11
    framework_required_by_app: (() => {
        try {
            const rc = JSON.parse(fs.readFileSync(path.join(APPDIR, 'WinApp.runtimeconfig.json'), 'utf8'));
            const f = (rc.runtimeOptions.frameworks || []).find((x) => x.name === 'Microsoft.WindowsDesktop.App');
            return f ? f.version : null;
        } catch { return null; }
    })(),
    framework_shipped: fs.existsSync(FRAMEWORK_DIR) ? '10.0.11' : null,
};

if (missing.length) {
    finish({ acc: 'ACC-031', started, env, verdict: 'FAIL',
             expectRef: 'ACC-031：外部（Windows 编译）WPF 程序集可直接运行',
             diff: [...missingNotes, '依赖切片：先构建 shim/运行时'] });
}

// 【必须有】`LDP_A11Y_REPEAT=1` 时树文件每 400ms 被整个重写 ⇒ 直接 readFileSync+JSON.parse
//   有相当概率读到半截（实测 `Unexpected end of JSON input`）。所有依赖树文件的判据都该这样读。
async function readTree(p) {
    for (let i = 0; i < 8; i++) {
        try {
            if (!fs.existsSync(p)) return null;
            const txt = fs.readFileSync(p, 'utf8');
            if (!txt.trim().endsWith('}')) { await sleep(120); continue; }
            return JSON.parse(txt);
        } catch { await sleep(120); }
    }
    return null;
}

const steps = [];
const xvfb = startXvfb(undefined, { screen: opts.screen || '1200x900x24' });
const treePath = path.join(os.tmpdir(), `ldp-ext-tree-${Date.now()}.json`);
let proc = null;
try {
    if (!(await waitDisplay(xvfb.display))) {
        steps.push({ step: 'xvfb', ok: false, reason: 'Xvfb 未就绪' });
    } else {
        // 【关键】干净环境 + 只给"用户可提供"的那些变量。
        //   `env -i` 不是洁癖：实测用交互 shell 的环境跑同一个程序会 BadImageFormatException，
        //   说明某些继承变量会干扰（K-013）。判据固定一份已知可用的最小集合。
        proc = spawn(path.join(DOTNET_ROOT_ABS, 'dotnet'), ['WinApp.dll'], {
            cwd: APPDIR,
            stdio: ['ignore', 'pipe', 'pipe'],
            env: {
                PATH: '/usr/bin:/bin',
                HOME: process.env.HOME,
                DISPLAY: xvfb.display,
                DOTNET_ROOT: DOTNET_ROOT_ABS,
                DOTNET_ReadyToRun: '0',
                LD_LIBRARY_PATH: `${path.join(FRAMEWORK_DIR, 'native')}:`,
                WPF_LINUX_WIN32_SHIM: SHIM,
                DOTNET_STARTUP_HOOKS: path.join(FRAMEWORK_DIR, 'LdpA11y.dll'),
                LDP_A11Y_DUMP: treePath,
                LDP_A11Y_REPEAT: '1',
                LDP_A11Y_SCREEN: 'SCR-01',
            },
        });
        let stderr = '';
        proc.stderr.on('data', (d) => { stderr += d.toString(); });

        // ① 启动
        let waited = 0;
        while (!fs.existsSync(treePath) && waited < 40000) { await sleep(250); waited += 250; }
        const tree0 = await readTree(treePath);
        const nodes0 = (tree0 && tree0.nodes) || [];
        const rootName = nodes0.length ? String(nodes0[0].name || '') : null;
        steps.push({ step: 'startup', ok: !!tree0 && nodes0.length > 0, nodes: nodes0.length, root: rootName });

        // ② 内容
        const names = nodes0.map((n) => String(n.name || ''));
        // UIA 的 name 取 AutomationProperties.Name（XAML 里是 "hello"），不是 Text 的内容
        const hasHello = names.includes('hello');
        const hasBtn = names.includes('btn');
        const hasZero = names.includes('clicks=0');
        steps.push({ step: 'content', ok: hasHello && hasBtn && hasZero,
                     hello: hasHello, btn: hasBtn, clicks0: hasZero, names: names.slice(0, 8) });

        // ③ 交互
        const win = await findWindow(xvfb.display, 'WinApp', 15000);
        if (win) await waitMapped(xvfb.display, win.id);
        const btnNode = nodes0.find((n) => n.name === 'btn');
        if (win && btnNode && Array.isArray(btnNode.bounds) && btnNode.bounds[2] > 0) {
            const [bx, by, bw, bh] = btnNode.bounds;
            const cx = Math.round(bx + bw / 2), cy = Math.round(by + bh / 2);
            spawnSync('sh', ['-c', `DISPLAY=${xvfb.display} xdotool mousemove ${cx} ${cy} click 1`], { timeout: 20000 });
            await sleep(2500);
            const tree1 = await readTree(treePath);
            const clicks1 = ((tree1 && tree1.nodes) || []).map((n) => String(n.name || '')).find((s) => /^clicks=\d+$/.test(s));
            steps.push({ step: 'interaction', ok: clicks1 === 'clicks=1',
                         clicked_at: [cx, cy], clicks_after: clicks1 || null,
                         mtime_changed: (() => {
                             try { return fs.statSync(treePath).mtimeMs > 0; } catch { return false; }
                         })() });
        } else {
            steps.push({ step: 'interaction', ok: false, note: '找不到窗口或 btn 的 bounds' });
        }

        if (stderr.trim()) {
            steps.push({ step: 'stderr_head', ok: true, head: stderr.split('\n').slice(0, 3).join(' | ').slice(0, 300) });
        }
    }
} catch (e) {
    steps.push({ step: 'exception', ok: false, error: String(e && e.message || e) });
} finally {
    if (proc) { try { proc.kill('SIGTERM'); } catch {} }
    xvfb.kill();
}

const bad = steps.filter((s) => !s.ok);
finish({
    acc: 'ACC-031', started,
    env: { ...env, steps },
    expectRef: 'ACC-031：外部（Windows 编译）WPF 程序集可直接运行（启动 + 内容 + 交互）',
    verdict: bad.length ? 'FAIL' : 'PASS',
    diff: bad.map((s) => `${s.step} 失败：${JSON.stringify(s).slice(0, 260)}`),
});
