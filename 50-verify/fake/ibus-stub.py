#!/usr/bin/env python3
"""Fake ibus —— 假输入法引擎（#Fake ibus）

作用：给 TG-006（IME 四路径：预编辑/提交/取消/候选窗）提供一条「无真输入法也能跑」的 D-Bus 端点，
按脚本发事件，让判据能验证桥的事件序列与数值，而不依赖 ibus/fcitx5 真的在跑。

用法：
  python3 50-verify/fake/ibus-stub.py --self-test
  python3 50-verify/fake/ibus-stub.py --serve
  python3 50-verify/fake/ibus-stub.py --address <unix:...> --script 'preedit:zhong:5|commit:中|cancel|candidates:中,种:0' --json
  python3 50-verify/fake/ibus-stub.py --report

接口（IF-031 里「方法名/信号名待对拍确认」，本桩件因此双轨）：
  自有接口（判据脚本用这一套，名字稳定）：
    org.freedesktop.IBus.FakeEngine
      SetPreedit(s text, i cursor) / Commit(s text) / Cancel() /
      SetCandidates(as items, i cursor) / EmitSequence(s scriptJson) / GetState() -> a{sv}
    信号：PreeditChanged(s,i) / CommitText(s) / Cancelled() / CandidatesChanged(as,i)
  兼容轨（给真桥开发对照，按 IBus 常见命名，仍未标为已确证）：
    信号：UpdatePreeditText(s,i) / CommitText(s) / HidePreeditText()

已知偏差（逐条显式登记，禁止静默）：
  1. 不是真 ibus：不实现 ibus 输入法插件加载、不占 org.freedesktop.IBus 原名（默认 FakeStub 名），
     也不实现 ProcessKeyEvent/引擎切换/属性面板；真桥（MOD-36）无法把它当成真引擎。
  2. 真实 IBus 的对象路径、输入上下文方法名、候选上报方法一律**待对拍确认**；本桩件的自有接口
     不是 IBus 协议，只是「同字段结构」的替身，用来先跑通桥的事件序列与计数字段。
  3. 候选窗本体不在 WPF 树内（GAP-003）：本桩件只给候选列表与光标位置，坐标换算由桥负责。
  4. 事件是脚本驱动的（EmitSequence），不随按键实时产生；键盘路径仍走 Fake X11。
  5. 不派发 ibus 的 Panel/引擎注册相关信号；`--report` 逐条列出未实现能力。
"""
import argparse
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_BUS_NAME = 'org.freedesktop.IBus.FakeStub'
OBJECT_PATH = '/org/freedesktop/IBus/FakeEngine'
FAKE_IFACE = 'org.freedesktop.IBus.FakeEngine'

# 兼容轨：与真 IBus 常见信号名对齐，供桥开发对照（确认度：待对拍确认）
COMPAT_SIGNALS = {
    'PreeditChanged': 'UpdatePreeditText',
    'CommitText': 'CommitText',
    'Cancelled': 'HidePreeditText',
}

DEFAULT_SCRIPT = 'preedit:zhong:5|commit:中|cancel|candidates:中,种:0'


def die(msg, code=1):
    print(f'ERROR {msg}', file=sys.stderr)
    sys.exit(code)


def note(msg):
    print(f'NOTE {msg}', flush=True)


# ---------- 总线 ----------

def start_bus():
    proc = subprocess.Popen(
        ['dbus-daemon', '--session', '--nofork', '--print-address=1', '--print-pid=1'],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    deadline = time.time() + 8
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                raise RuntimeError(f'dbus-daemon 启动失败：{proc.stderr.read()[:300]}')
            continue
        line = line.strip()
        if line.startswith('unix:') or line.startswith('tcp:'):
            return line, proc
    proc.terminate()
    raise RuntimeError('dbus-daemon 8 秒内未打印总线地址')


def load_dbus():
    import dbus  # noqa: F401
    import dbus.service
    import dbus.mainloop.glib
    from gi.repository import GLib
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    return dbus, GLib


# ---------- 事件状态机 ----------

class Engine:
    def __init__(self):
        self.preedit = ''
        self.cursor = 0
        self.candidates = []
        self.occasion = 0
        self.committed = []
        self.cancel_count = 0
        self.events = []

    def snapshot(self):
        return {
            'preedit': self.preedit,
            'cursor': self.cursor,
            'candidates': list(self.candidates),
            'candidate_cursor': self.occasion,
            'committed': list(self.committed),
            'cancel_count': self.cancel_count,
            'events': list(self.events),
        }


# ---------- 服务 ----------

def build_service(bus_name, engine):
    dbus, _ = load_dbus()

    class FakeEngine(dbus.service.Object):
        def __init__(self, bus):
            super().__init__(bus, OBJECT_PATH)

        @dbus.service.method(FAKE_IFACE, in_signature='si', out_signature='')
        def SetPreedit(self, text, cursor):
            engine.preedit = str(text)
            engine.cursor = int(cursor)
            engine.events.append({'kind': 'preedit', 'text': engine.preedit, 'cursor': engine.cursor})
            self.PreeditChanged(engine.preedit, engine.cursor)

        @dbus.service.method(FAKE_IFACE, in_signature='s', out_signature='')
        def Commit(self, text):
            engine.committed.append(str(text))
            engine.events.append({'kind': 'commit', 'text': str(text)})
            self.CommitText(str(text))

        @dbus.service.method(FAKE_IFACE, in_signature='', out_signature='')
        def Cancel(self):
            engine.cancel_count += 1
            engine.preedit = ''
            engine.cursor = 0
            engine.events.append({'kind': 'cancel'})
            self.Cancelled()

        @dbus.service.method(FAKE_IFACE, in_signature='asi', out_signature='')
        def SetCandidates(self, items, cursor):
            engine.candidates = [str(x) for x in items]
            engine.occasion = int(cursor)
            engine.events.append({'kind': 'candidates', 'items': list(engine.candidates), 'cursor': engine.occasion})
            self.CandidatesChanged(dbus.Array(engine.candidates, signature='s'), engine.occasion)

        @dbus.service.method(FAKE_IFACE, in_signature='s', out_signature='i')
        def EmitSequence(self, script_json):
            """按 JSON 数组发一串事件；返回事件条数。项格式：
            {"kind":"preedit","text":"zhong","cursor":5} / {"kind":"commit","text":"中"} /
            {"kind":"cancel"} / {"kind":"candidates","items":["中","种"],"cursor":0}
            """
            seq = json.loads(str(script_json))
            if not isinstance(seq, list):
                raise dbus.exceptions.DBusException('script 必须是 JSON 数组', name=FAKE_IFACE + '.BadScript')
            for ev in seq:
                kind = ev.get('kind')
                if kind == 'preedit':
                    self.SetPreedit(ev.get('text', ''), int(ev.get('cursor', 0)))
                elif kind == 'commit':
                    self.Commit(ev.get('text', ''))
                elif kind == 'cancel':
                    self.Cancel()
                elif kind == 'candidates':
                    self.SetCandidates(ev.get('items', []), int(ev.get('cursor', 0)))
                else:
                    raise dbus.exceptions.DBusException(f'未知事件 kind={kind}', name=FAKE_IFACE + '.BadScript')
            return dbus.Int32(len(seq))

        @dbus.service.method(FAKE_IFACE, in_signature='', out_signature='a{sv}')
        def GetState(self):
            snap = engine.snapshot()
            out = {
                'preedit': dbus.String(snap['preedit']),
                'cursor': dbus.Int32(snap['cursor']),
                'candidates': dbus.Array(snap['candidates'], signature='s'),
                'candidate_cursor': dbus.Int32(snap['candidate_cursor']),
                'cancel_count': dbus.Int32(snap['cancel_count']),
                'committed': dbus.Array(snap['committed'], signature='s'),
            }
            return dbus.Dictionary(out, signature='sv')

        @dbus.service.signal(FAKE_IFACE, signature='si')
        def PreeditChanged(self, text, cursor):
            # 兼容轨（名字按 IBus 常见命名，确认度：待对拍确认）
            self.UpdatePreeditText(text, cursor)

        @dbus.service.signal(FAKE_IFACE, signature='s')
        def CommitText(self, text):
            pass  # 兼容轨信号名同为 CommitText，无需重复发

        @dbus.service.signal(FAKE_IFACE, signature='')
        def Cancelled(self):
            self.HidePreeditText()

        @dbus.service.signal(FAKE_IFACE, signature='asi')
        def CandidatesChanged(self, items, cursor):
            pass  # 候选列表没有对应的 IBus 兼容信号，不伪造

        @dbus.service.signal(FAKE_IFACE, signature='si')
        def UpdatePreeditText(self, text, cursor):
            """兼容轨（待对拍确认）：真 IBus 常见的预编辑信号名。"""

        @dbus.service.signal(FAKE_IFACE, signature='')
        def HidePreeditText(self):
            """兼容轨（待对拍确认）：真 IBus 常见的隐藏预编辑信号名。"""


    session_bus = dbus.SessionBus()
    # BusName 必须被持有：局部变量被回收会 release 名字，客户端随即找不到服务
    busname = dbus.service.BusName(bus_name, session_bus)
    return FakeEngine(session_bus), dbus, busname


def serve(bus_name):
    address, bus_proc = start_bus()
    os.environ['DBUS_SESSION_BUS_ADDRESS'] = address
    engine = Engine()
    obj, _, _busname = build_service(bus_name, engine)
    print(f'READY {json.dumps({"address": address, "bus_name": bus_name, "path": OBJECT_PATH, "iface": FAKE_IFACE, "methods": ["SetPreedit", "Commit", "Cancel", "SetCandidates", "EmitSequence", "GetState"], "signals": ["PreeditChanged", "CommitText", "Cancelled", "CandidatesChanged"], "compat_signals": COMPAT_SIGNALS}, ensure_ascii=False)}', flush=True)
    _, GLib = load_dbus()
    loop = GLib.MainLoop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: loop.quit())
    try:
        loop.run()
    finally:
        bus_proc.terminate()
        sys.stdout.flush()
        sys.stderr.flush()
        os._exit(0)


# ---------- 客户端 ----------

def parse_script(text):
    """'preedit:zhong:5|commit:中|cancel|candidates:中,种:0' → EmitSequence 用的 JSON 数组。"""
    seq = []
    for part in str(text).split('|'):
        part = part.strip()
        if not part:
            continue
        fields = part.split(':')
        kind = fields[0]
        if kind == 'preedit':
            seq.append({'kind': 'preedit', 'text': fields[1] if len(fields) > 1 else '', 'cursor': int(fields[2]) if len(fields) > 2 else 0})
        elif kind == 'commit':
            seq.append({'kind': 'commit', 'text': fields[1] if len(fields) > 1 else ''})
        elif kind == 'cancel':
            seq.append({'kind': 'cancel'})
        elif kind == 'candidates':
            items = fields[1].split(',') if len(fields) > 1 and fields[1] else []
            seq.append({'kind': 'candidates', 'items': items, 'cursor': int(fields[2]) if len(fields) > 2 else 0})
        else:
            raise ValueError(f'未知事件 kind={kind}')
    return seq


def client(address, bus_name, script, want_json):
    os.environ['DBUS_SESSION_BUS_ADDRESS'] = address
    dbus, GLib = load_dbus()
    bus = dbus.SessionBus()
    seen = []

    def on_signal(*args, **kwargs):
        seen.append({'signal': kwargs.get('member') or kwargs.get('signal'),
                     'args': [str(a) for a in args]})

    bus.add_signal_receiver(on_signal, dbus_interface=FAKE_IFACE, member_keyword='member')
    obj = bus.get_object(bus_name, OBJECT_PATH)
    iface = dbus.Interface(obj, FAKE_IFACE)
    seq = parse_script(script)
    try:
        n = int(iface.EmitSequence(json.dumps(seq, ensure_ascii=False)))
    except Exception as e:  # noqa: BLE001  未知事件必须显式失败
        print(f'ERROR EmitSequence 失败：{type(e).__name__}: {e}', file=sys.stderr)
        return 1
    ctx = GLib.MainContext.default()
    deadline = time.time() + 1.2
    while time.time() < deadline:
        while ctx.iteration(False):
            pass
        time.sleep(0.01)

    def norm(v):
        if isinstance(v, (list, tuple)):
            return [str(x) for x in v]
        try:
            if hasattr(v, '__iter__') and not isinstance(v, (str, bytes)):
                return [str(x) for x in v]
        except Exception:  # noqa: BLE001
            pass
        return str(v)

    raw_state = iface.GetState()
    out = {
        'emitted': n,
        'signals': seen,
        'state': {str(k): norm(v) for k, v in raw_state.items()},
    }
    if want_json:
        print(json.dumps(out, ensure_ascii=False))
    else:
        print(f"emitted={out['emitted']} signals={[s['signal'] for s in out['signals']]}")
        print(json.dumps(out['state'], ensure_ascii=False))
    return 0


# ---------- 报告与自检 ----------

def report(bus_name):
    print('Fake ibus 偏差清单：')
    print('  1. 不是真 ibus：不实现引擎插件加载、不占 org.freedesktop.IBus 原名（默认 %s）、无 ProcessKeyEvent/引擎切换/属性面板' % bus_name)
    print('  2. 真实 IBus 对象路径与输入上下文方法名 **待对拍确认**；本桩件用自有接口 org.freedesktop.IBus.FakeEngine 做同字段替身')
    print('  3. 兼容轨信号（待对拍确认，仅供桥开发对照）：' + ', '.join(f'{k}→{v}' for k, v in COMPAT_SIGNALS.items()))
    print('  4. 候选窗不在 WPF 树内（GAP-003）：只给候选列表与光标位置，坐标换算由桥负责')
    print('  5. 事件由脚本驱动（EmitSequence），不随按键实时产生；键盘路径走 Fake X11')
    print('  6. 未实现：ProcessKeyEvent、FocusIn/FocusOut、属性面板、引擎注册与切换、ibus Panel 相关信号')
    return 0


def self_test(bus_name):
    checks = []

    def ck(name, cond, detail=''):
        checks.append((name, bool(cond), detail))

    proc = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--serve', '--bus-name', bus_name],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        ready = None
        deadline = time.time() + 20
        while time.time() < deadline:
            line = proc.stdout.readline()
            if not line:
                if proc.poll() is not None:
                    raise RuntimeError(f'--serve 提前退出：{proc.stderr.read()[:300]}')
                continue
            if line.startswith('READY '):
                ready = json.loads(line[len('READY '):])
                break
        if not ready:
            raise RuntimeError('--serve 20 秒内未 READY')
        ck('READY 含总线地址', ready.get('address', '').startswith('unix:'))
        ck('READY 列出 6 个方法', len(ready.get('methods', [])) == 6, str(ready.get('methods')))
        script = 'preedit:zhong:5|commit:中|cancel|candidates:中,种:0'
        r = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--address', ready['address'],
                            '--bus-name', bus_name, '--script', script, '--json'],
                           capture_output=True, text=True, timeout=25)
        ck('客户端 rc=0', r.returncode == 0, r.stderr.strip()[:200])
        if r.returncode != 0:
            raise RuntimeError(r.stderr.strip()[:300])
        out = json.loads(r.stdout)
        ck('发出 4 个事件', out['emitted'] == 4, str(out['emitted']))
        sigs = [s['signal'] for s in out['signals']]
        for name, sig in [('预编辑信号', 'PreeditChanged'), ('提交信号', 'CommitText'), ('取消信号', 'Cancelled')]:
            ck(f'{name}收到', sig in sigs, str(sigs))
        pre = [s for s in out['signals'] if s['signal'] == 'PreeditChanged']
        ck('预编辑串=zhong', pre and pre[0]['args'][0] == 'zhong', str(pre[:1]))
        ck('预编辑光标=5', pre and pre[0]['args'][1] == '5', str(pre[:1]))
        com = [s for s in out['signals'] if s['signal'] == 'CommitText']
        ck('提交串=中', com and com[0]['args'][0] == '中', str(com[:1]))
        st = out['state']
        ck('状态：候选 2 个', st.get('candidates') == ['中', '种'], str(st.get('candidates')))
        ck('状态：提交 1 次', len(st.get('committed') or []) == 1, str(st.get('committed')))
        ck('状态：取消 1 次', st.get('cancel_count') == '1' or st.get('cancel_count') == 1, str(st.get('cancel_count')))
        bad = subprocess.run([sys.executable, str(Path(__file__).resolve()), '--address', ready['address'],
                              '--bus-name', bus_name, '--script', 'unknown:1', '--json'],
                             capture_output=True, text=True, timeout=25)
        ck('未知事件被显式拒绝（rc≠0）', bad.returncode != 0, f'rc={bad.returncode}')
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill()
    failed = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        print(f'{"PASS" if ok else "FAIL"} {name}' + (f' ({detail})' if detail and not ok else ''))
    print(f'ibus-stub self-test: {len(checks) - len(failed)}/{len(checks)} 通过')
    return 0 if not failed else 1


def main():
    ap = argparse.ArgumentParser(description='Fake ibus 假输入法引擎（见文件头偏差）')
    ap.add_argument('--serve', action='store_true')
    ap.add_argument('--self-test', action='store_true')
    ap.add_argument('--report', action='store_true')
    ap.add_argument('--bus-name', default=DEFAULT_BUS_NAME)
    ap.add_argument('--address', help='客户端模式：私有总线地址')
    ap.add_argument('--script', default=DEFAULT_SCRIPT, help='客户端模式：事件脚本 a:b:c|...')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('--timeout', type=int, default=30)
    args = ap.parse_args()

    def on_alarm(*_):
        print(f'ERROR TIMEOUT 超过 {args.timeout}s', file=sys.stderr)
        os._exit(124)

    signal.signal(signal.SIGALRM, on_alarm)
    signal.alarm(args.timeout)
    try:
        if args.report:
            return report(args.bus_name)
        if args.self_test:
            return self_test(args.bus_name)
        if args.serve:
            serve(args.bus_name)
            return 0
        if args.script and args.address:
            return client(args.address, args.bus_name, args.script, args.json)
        ap.print_help()
        return 2
    finally:
        signal.alarm(0)


if __name__ == '__main__':
    sys.exit(main())
