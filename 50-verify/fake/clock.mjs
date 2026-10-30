#!/usr/bin/env node
// Fake 时钟 —— 可注入时间源（#Fake 时钟）
// 给 TG-009/TG-015 用：超时、重试、长跑压缩都靠它；判据脚本也能用环境变量直接复用。
// 用法：
//   node 50-verify/fake/clock.mjs --self-test
//   node 50-verify/fake/clock.mjs --now            # 打印当前读数（受环境变量影响）
//   LDP_FAKE_CLOCK=1 LDP_FAKE_CLOCK_START=2026-01-01T00:00:00Z node 50-verify/fake/clock.mjs --now
// 编程接口（ESM）：
//   import { createFakeClock, envClock } from './clock.mjs'
//   const c = createFakeClock({ start: 0, stepMs: 0 })
//   c.now() / c.advance(ms) / c.freeze() / c.unfreeze() / c.setTimer(fn, delayMs) / c.runTimers(dueBefore)
//
// 已知偏差（逐条显式登记）：
//   1. 只替换「本模块的 now()」：不 monkey-patch 全局 Date.now/performance.now，避免污染被测代码的真实时基。
//      判据脚本要用假时钟，必须显式 `const clock = envClock()` 并把它传下去（不做的就还是真时钟，不静默替换）。
//   2. 定时器只在 runTimers 时推进（无后台线程）；不模拟 CPU 耗时、不做调度精度抖动。
//   3. 长跑压缩属加速：读数 env 里必须标 `clock=fake`（08 §1.4/09 §8 纪律）。
import process from 'node:process';

const ENV_ENABLED = 'LDP_FAKE_CLOCK';
const ENV_START = 'LDP_FAKE_CLOCK_START';
const ENV_STEP = 'LDP_FAKE_CLOCK_STEP';

function parseStart(v) {
  if (v === undefined || v === null || v === '') return Date.now();
  const s = String(v).trim();
  if (/^\d+$/.test(s)) return Number(s);
  const t = Date.parse(s);
  if (Number.isNaN(t)) throw new Error(`LDP_FAKE_CLOCK_START 无法解析为时间：${s}`);
  return t;
}

export function createFakeClock({ start = 0, stepMs = 0 } = {}) {
  let current = Number(start);
  let frozen = false;
  let seq = 0;
  const timers = new Map(); // id -> { dueAt, fn, id }

  const clock = {
    kind: 'fake',
    get frozen() {
      return frozen;
    },
    start() {
      return Number(start);
    },
    now() {
      const t = current;
      if (!frozen && stepMs > 0) current += stepMs;
      return t;
    },
    advance(ms) {
      const n = Number(ms);
      if (!Number.isFinite(n) || n < 0) throw new Error(`advance(ms) 需要非负有限数，收到 ${ms}`);
      if (frozen) return current;
      current += n;
      return current;
    },
    set(ms) {
      current = Number(ms);
      return current;
    },
    freeze() {
      frozen = true;
    },
    unfreeze() {
      frozen = false;
    },
    setTimer(fn, delayMs = 0) {
      if (typeof fn !== 'function') throw new Error('setTimer(fn, delayMs) 的 fn 必须是函数');
      const d = Number(delayMs);
      if (!Number.isFinite(d) || d < 0) throw new Error(`setTimer 的 delayMs 需要非负有限数，收到 ${delayMs}`);
      seq += 1;
      const id = seq;
      timers.set(id, { id, dueAt: current + d, fn });
      return id;
    },
    clearTimer(id) {
      return timers.delete(Number(id));
    },
    pending() {
      return timers.size;
    },
    flush() {
      // 把当前所有挂着的定时器按到期顺序跑完（长跑压缩用；不改变「runTimers() 只看当前时刻」的语义）
      return clock.runTimers(Number.POSITIVE_INFINITY);
    },
    runTimers(dueBefore) {
      const limit = dueBefore === undefined ? current : Number(dueBefore);
      const due = [...timers.values()].filter((t) => t.dueAt <= limit).sort((a, b) => a.dueAt - b.dueAt || a.id - b.id);
      const fired = [];
      for (const t of due) {
        timers.delete(t.id);
        if (!frozen && t.dueAt > current) current = t.dueAt;
        t.fn(t.dueAt);
        fired.push(t.id);
      }
      return fired;
    },
  };
  return clock;
}

// 环境变量入口：LDP_FAKE_CLOCK=1 时启用；START 支持 ISO 字符串或毫秒；STEP>0 时每次 now() 自动前进
export function envClock(env = process.env) {
  if (env[ENV_ENABLED] !== '1') return null;
  const start = parseStart(env[ENV_START]);
  const stepMs = env[ENV_STEP] ? Number(env[ENV_STEP]) : 0;
  if (!Number.isFinite(stepMs) || stepMs < 0) throw new Error(`LDP_FAKE_CLOCK_STEP 需要非负有限数：${env[ENV_STEP]}`);
  return createFakeClock({ start, stepMs });
}

function selfTest() {
  const checks = [];
  const ck = (name, cond, detail = '') => checks.push([name, Boolean(cond), detail]);

  const c = createFakeClock({ start: 1000 });
  ck('起点正确', c.now() === 1000, String(c.now()));
  const a = c.now();
  c.advance(500);
  ck('advance 后差值 500', c.now() - a === 500, String(c.now() - a));
  ck('单调不回退', c.now() >= a);
  c.freeze();
  const f = c.now();
  c.advance(9999);
  ck('freeze 后 advance 无效', c.now() === f, String(c.now() - f));
  c.unfreeze();
  ck('unfreeze 后可继续推进', (c.advance(1), c.now() === f + 1));

  const order = [];
  const c2 = createFakeClock({ start: 0 });
  c2.setTimer(() => order.push('late'), 300);
  c2.setTimer(() => order.push('early'), 100);
  c2.setTimer(() => order.push('same-a'), 200);
  const sameB = c2.setTimer(() => order.push('same-b'), 200);
  c2.clearTimer(sameB);
  const fired = c2.runTimers(250);
  ck('按到期顺序触发', JSON.stringify(order) === JSON.stringify(['early', 'same-a']), JSON.stringify(order));
  ck('runTimers 返回触发 id', fired.length === 2, JSON.stringify(fired));
  ck('未到期定时器仍挂着', c2.pending() === 1, String(c2.pending()));
  ck('runTimers 把 now 推到到期时刻', c2.now() === 200, String(c2.now()));
  ck('runTimers() 默认只跑当前时刻已到期的（late 不跑）', order.length === 2, JSON.stringify(order));
  c2.flush();
  ck('flush() 跑完剩余定时器', order[order.length - 1] === 'late', JSON.stringify(order));
  ck('flush 后 now 到最晚到期时刻', c2.now() === 300, String(c2.now()));

  const c3 = createFakeClock({ start: 5, stepMs: 10 });
  const t1 = c3.now();
  const t2 = c3.now();
  ck('stepMs 让每次 now 前进', t2 - t1 === 10, `${t1}->${t2}`);

  let threw = false;
  try {
    createFakeClock().advance(-1);
  } catch {
    threw = true;
  }
  ck('非法 advance 显式抛错', threw);

  const e1 = envClock({ [ENV_ENABLED]: '1', [ENV_START]: '2026-01-01T00:00:00Z' });
  ck('envClock 按 ISO 起算', e1 && e1.now() === Date.parse('2026-01-01T00:00:00Z'), String(e1 && e1.now()));
  const e2 = envClock({});
  ck('未启用 envClock 返回 null（不静默替换时间源）', e2 === null);

  const failed = checks.filter((x) => !x[1]);
  for (const [name, ok, detail] of checks) console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${!ok && detail ? ` (${detail})` : ''}`);
  console.log(`clock self-test: ${checks.length - failed.length}/${checks.length} 通过`);
  return failed.length ? 1 : 0;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const args = process.argv.slice(2);
  if (args.includes('--self-test')) process.exit(selfTest());
  if (args.includes('--now')) {
    const c = envClock() || createFakeClock({ start: Date.now() });
    console.log(JSON.stringify({ now: c.now(), iso: new Date(c.now()).toISOString(), kind: c.kind, env: process.env[ENV_ENABLED] === '1' }));
    process.exit(0);
  }
  console.log('用法：--self-test | --now（环境变量 LDP_FAKE_CLOCK=1 / LDP_FAKE_CLOCK_START / LDP_FAKE_CLOCK_STEP）');
  process.exit(2);
}
