#!/usr/bin/env node
// Fake X11 —— 独立 Xvfb 会话桩件（#Fake X11）
//
// 作用：给窗口/消息/DPI 判据（TG-004、TG-011 无合成会话）提供一个可复现、可无人值守的 X11 会话，
//       不让判据依赖人开桌面。会话 = 一个独立 Xvfb + 一份 Xft.dpi 资源 + 可选的子进程。
//
// 用法：
//   node 50-verify/fake/xvfb-session.mjs --self-test
//   node 50-verify/fake/xvfb-session.mjs --dpi 144 --exec 'dotnet run --project 50-verify/samples/WpfFirstWindow'
//   node 50-verify/fake/xvfb-session.mjs --display :97 --screen 1920x1080x24 --extensions randr,xinput2
//
// 已知偏差（必须显式，禁止静默）：
//   1. 无合成器：Xvfb 没有 compositor，DWM 判据只能走 DwmIsCompositionEnabled=false 分支（TG-011 需真会话补一轮）。
//   2. Xft.dpi 走 `XENVIRONMENT` 资源文件 + 尽力 `xrdb -merge`；本机实测 xrdb 对该 Xvfb 返回 0 但不落
//      RESOURCE_MANAGER 属性，此时打印 NOTE 并只依赖 XENVIRONMENT（Xlib 资源管理器会合并它）。
//   3. 测试窗口由 `xclock` 创建；`xdotool` 没有「创建窗口」的能力，只用于检索 id 与几何。
//   4. 不注入输入事件（键盘/指针回放属 `50-verify/window/replay.mjs` 的职责，S1 之后落地）。
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const EXT_ALIASES = {
  xinput2: 'XInputExtension',
  xi: 'XInputExtension',
  randr: 'RANDR',
  render: 'RENDER',
  composite: 'Composite',
  glx: 'GLX',
  xfixes: 'XFIXES',
  xkb: 'XKEYBOARD',
};

const SOCKET_DIR = '/tmp/.X11-unix';

function die(msg, code = 1) {
  console.error(`ERROR ${msg}`);
  process.exit(code);
}

function note(msg) {
  console.log(`NOTE ${msg}`);
}

function has(bin) {
  return spawnSync('sh', ['-c', `command -v ${bin} >/dev/null 2>&1`]).status === 0;
}

function sh(bin, args, env = {}, timeout = 5000) {
  const r = spawnSync(bin, args, { encoding: 'utf8', timeout, env: { ...process.env, ...env } });
  return { code: r.status === null ? -1 : r.status, out: `${r.stdout || ''}${r.stderr || ''}`, raw: r };
}

function pickDisplay(explicit) {
  if (explicit) {
    const n = Number(String(explicit).replace(/^:/, ''));
    if (!Number.isInteger(n) || n < 1) throw new Error(`--display 非法：${explicit}（要 :N）`);
    return n;
  }
  for (let n = 90; n <= 199; n += 1) {
    if (!fs.existsSync(`${SOCKET_DIR}/X${n}`) && !fs.existsSync(`/tmp/.X${n}-lock`)) return n;
  }
  throw new Error('找不到空闲 display（90~199 全被占用）');
}

function parseExtensions(xdpyinfoOut) {
  const lines = xdpyinfoOut.split('\n');
  const i = lines.findIndex((l) => /^number of extensions:/.test(l));
  if (i < 0) return new Set();
  const out = new Set();
  for (const line of lines.slice(i + 1)) {
    const t = line.trim();
    if (!t) break;
    // 扩展名行没有冒号；遇到 "default screen number: 0" 这类属性行即结束
    if (t.includes(':')) break;
    out.add(t);
  }
  return out;
}

class XvfbSession {
  constructor(opts = {}) {
    this.dpi = opts.dpi ?? 96;
    this.screen = opts.screen ?? '1280x1024x24';
    this.displayNumber = pickDisplay(opts.display);
    this.display = `:${this.displayNumber}`;
    this.extraExtensions = opts.extensions ?? [];
    this.proc = null;
    this.resourcesFile = null;
    this.info = null;
  }

  start() {
    if (!has('Xvfb')) throw new Error('缺少 Xvfb（不在 PATH）');
    if (!has('xdpyinfo')) throw new Error('缺少 xdpyinfo（不在 PATH）');
    const logFile = path.join(os.tmpdir(), `fake-x11-${this.displayNumber}.log`);
    const out = fs.openSync(logFile, 'w');
    this.logFile = logFile;
    this.proc = spawn(
      'Xvfb',
      [this.display, '-screen', '0', this.screen, '-nolisten', 'tcp', '-dpi', String(this.dpi)],
      { stdio: ['ignore', out, out] },
    );
    this.exited = false;
    this.proc.on('exit', () => { this.exited = true; });

    const deadline = Date.now() + 8000;
    while (Date.now() < deadline) {
      if (this.exited) {
        throw new Error(`Xvfb ${this.display} 启动即退出，日志：${fs.readFileSync(logFile, 'utf8').slice(0, 400)}`);
      }
      if (fs.existsSync(`${SOCKET_DIR}/X${this.displayNumber}`)) {
        const probe = sh('xdpyinfo', ['-display', this.display]);
        // xdpyinfo 在 socket 刚出现时可能仍失败，重试到超时
        if (probe.code === 0) break;
      }
      spawnSync('sleep', ['0.15']);
    }
    const probe = sh('xdpyinfo', ['-display', this.display]);
    if (probe.code !== 0) throw new Error(`Xvfb ${this.display} 8 秒内未就绪：${probe.out.slice(0, 300)}`);

    // Xft.dpi 注入：先写 XENVIRONMENT 资源文件，再尽力 xrdb
    this.resourcesFile = path.join(os.tmpdir(), `fake-x11-${this.displayNumber}.Xresources`);
    fs.writeFileSync(this.resourcesFile, `Xft.dpi:\t${this.dpi}\nXft.antialias:\t1\n`, 'utf8');
    let xrdb = 'unavailable';
    if (has('xrdb') && has('xprop')) {
      sh('xrdb', ['-nocpp', '-merge', this.resourcesFile], { DISPLAY: this.display });
      const prop = sh('xprop', ['-root', 'RESOURCE_MANAGER'], { DISPLAY: this.display });
      xrdb = prop.out.includes('Xft.dpi') ? 'effective' : 'noop';
      if (xrdb === 'noop') {
        note(`xrdb 对该会话无效（RESOURCE_MANAGER 未写入）；Xft.dpi=${this.dpi} 只经 XENVIRONMENT=${this.resourcesFile} 生效`);
      }
    }
    const dims = /dimensions:\s+(\d+)x(\d+) pixels/.exec(probe.out);
    const resol = /resolution:\s+(\S+) dots per inch/.exec(probe.out);
    this.info = {
      display: this.display,
      display_number: this.displayNumber,
      screen: this.screen,
      dpi: this.dpi,
      geometry: dims ? [Number(dims[1]), Number(dims[2])] : null,
      resolution: resol ? resol[1] : null,
      extensions: [...parseExtensions(probe.out)],
      xresources: this.resourcesFile,
      xrdb,
      env: { DISPLAY: this.display, XENVIRONMENT: this.resourcesFile },
      log: this.logFile,
    };
    this.assertExtensions(this.extraExtensions);
    return this.info;
  }

  assertExtensions(names) {
    if (!names.length) return;
    const available = new Set(this.info.extensions);
    for (const raw of names) {
      const key = String(raw).trim();
      if (!key) continue;
      const canonical = EXT_ALIASES[key.toLowerCase()] || key;
      if (!EXT_ALIASES[key.toLowerCase()] && !available.has(canonical)) {
        throw new Error(`不支持的扩展名：${key}（可用别名：${Object.keys(EXT_ALIASES).join('、')}）`);
      }
      if (!available.has(canonical)) {
        throw new Error(`Xvfb 未提供扩展 ${canonical}（请求写作 ${key}）`);
      }
    }
  }

  run(cmd, timeoutMs = 30000) {
    const env = { ...process.env, ...this.info.env };
    const r = spawnSync('bash', ['-lc', cmd], { stdio: 'inherit', env, timeout: timeoutMs });
    return r.status === null ? 124 : r.status;
  }

  stop() {
    if (this.proc && !this.exited) {
      this.proc.kill('SIGTERM');
      const deadline = Date.now() + 3000;
      while (!this.exited && Date.now() < deadline) spawnSync('sleep', ['0.05']);
      if (!this.exited) this.proc.kill('SIGKILL');
    }
    for (const f of [this.resourcesFile, this.logFile, `/tmp/.X${this.displayNumber}-lock`]) {
      try { fs.rmSync(f, { force: true }); } catch { /* 清理失败不掩盖主错误 */ }
    }
  }
}

function parseArgs(argv) {
  const out = { extensions: [], dpi: 96, screen: '1280x1024x24', selfTest: false, exec: null, display: null };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    const next = () => {
      const v = argv[i + 1];
      if (v === undefined || v.startsWith('--')) throw new Error(`${a} 缺少取值`);
      i += 1;
      return v;
    };
    if (a === '--self-test') out.selfTest = true;
    else if (a === '--dpi') out.dpi = Number(next());
    else if (a === '--screen') out.screen = next();
    else if (a === '--display') out.display = next();
    else if (a === '--exec') out.exec = next();
    else if (a === '--extensions') out.extensions = next().split(',').map((s) => s.trim()).filter(Boolean);
    else if (a === '--json') out.json = true;
    else if (a === '--help' || a === '-h') { console.log('见文件头注释'); process.exit(0); }
    else throw new Error(`未知参数：${a}`);
  }
  if (![96, 120, 144, 192].includes(out.dpi)) throw new Error(`--dpi 只接受 96/120/144/192，收到 ${out.dpi}`);
  return out;
}

function selfTest() {
  const checks = [];
  const check = (name, cond, detail = '') => checks.push({ name, ok: Boolean(cond), detail });
  let session = null;
  let win = null;
  try {
    session = new XvfbSession({ dpi: 120, extensions: ['xinput2', 'randr'] });
    const info = session.start();
    check('会话起来：xdpyinfo 可读', Boolean(info.geometry), JSON.stringify(info.geometry));
    check('分辨率符合 --screen', info.geometry && info.geometry[0] === 1280 && info.geometry[1] === 1024, JSON.stringify(info.geometry));
    check('DPI 120 生效（X server 侧 resolution）', info.resolution === '120x120', String(info.resolution));
    const res = fs.readFileSync(info.xresources, 'utf8');
    check('Xft.dpi 资源文件写入', res.includes('Xft.dpi:\t120'), JSON.stringify(res.trim()));
    check('XENVIRONMENT 指向资源文件', info.env.XENVIRONMENT === info.xresources, info.env.XENVIRONMENT);
    check('扩展 randr 可用', info.extensions.includes('RANDR'));
    check('扩展 xinput2 可用', info.extensions.includes('XInputExtension'));
    let threw = null;
    try { session.assertExtensions(['no-such-extension']); } catch (e) { threw = e.message; }
    check('未知扩展名显式报错（不静默）', threw && threw.includes('不支持的扩展名'), String(threw));

    // 用 xclock 建测试窗口，再用 xdotool/xwininfo 读回
    if (!has('xclock')) {
      check('测试窗口可创建（xclock 可用）', false, '缺少 xclock');
    } else {
      win = spawn('xclock', ['-geometry', '120x120+10+10'], {
        env: { ...process.env, DISPLAY: info.display },
        stdio: 'ignore',
      });
      let ids = '';
      const deadline = Date.now() + 6000;
      while (Date.now() < deadline) {
        const r = sh('xdotool', ['search', '--class', 'xclock'], { DISPLAY: info.display });
        if (r.code === 0 && r.out.trim()) { ids = r.out.trim(); break; }
        spawnSync('sleep', ['0.1']);
      }
      check('xdotool 读到测试窗口 id', Boolean(ids), ids.split('\n')[0] || '未找到');
      const tree = sh('xwininfo', ['-root', '-tree'], { DISPLAY: info.display });
      check('xwininfo 窗口树里能看到测试窗口', /xclock|XClock/.test(tree.out), tree.code === 0 ? 'ok' : tree.out.slice(0, 120));
    }
  } catch (e) {
    check('自检未抛异常', false, e.message);
  } finally {
    if (win) win.kill('SIGTERM');
    if (session) {
      const display = session.display;
      session.stop();
      const after = sh('xdpyinfo', ['-display', display]);
      check('会话关闭后 display 不可用', after.code !== 0, `rc=${after.code}`);
    }
  }
  let failed = 0;
  for (const c of checks) {
    if (!c.ok) failed += 1;
    console.log(`${c.ok ? 'PASS' : 'FAIL'} ${c.name}${c.detail ? ` (${c.detail})` : ''}`);
  }
  console.log(`xvfb-session self-test: ${checks.length - failed}/${checks.length} 通过`);
  process.exit(failed ? 1 : 0);
}

function main() {
  let args;
  try {
    args = parseArgs(process.argv.slice(2));
  } catch (e) {
    die(e.message, 2);
  }
  if (args.selfTest) {
    selfTest();
    return;
  }
  const session = new XvfbSession(args);
  // 无人值守：被 SIGINT/SIGTERM 打断时也要回收 Xvfb 与临时资源，避免留下孤儿会话
  for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.on(sig, () => {
      console.error(`ERROR 收到 ${sig}，回收会话 ${session.display}`);
      session.stop();
      process.exit(130);
    });
  }
  let code = 0;
  try {
    const info = session.start();
    if (args.json) console.log(JSON.stringify(info, null, 2));
    else console.log(`SESSION display=${info.display} dpi=${info.dpi} screen=${info.screen} geometry=${info.geometry.join('x')} resolution=${info.resolution} extensions=${info.extensions.join(',')} xrdb=${info.xrdb}`);
    if (args.exec) {
      code = session.run(args.exec);
      if (code !== 0) console.error(`ERROR 子进程退出码 ${code}`);
    }
  } catch (e) {
    console.error(`ERROR ${e.message}`);
    code = 1;
  } finally {
    session.stop();
  }
  process.exit(code);
}

main();
