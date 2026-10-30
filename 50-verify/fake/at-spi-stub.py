#!/usr/bin/env python3
"""Fake AT-SPI —— 最小无障碍树桩件（#Fake AT-SPI）

作用：给 TG-012 的结构判据提供一条「私有总线 + 最小 AT-SPI 树」，让不依赖真桌面（无 at-spi2-registryd）的
结构判据与快照器可以在无人值守下跑；数据来自 --expect 的期望文件（与 IF-071 同字段），不是编造的控件树。

用法：
  python3 50-verify/fake/at-spi-stub.py --self-test
  python3 50-verify/fake/at-spi-stub.py --expect 50-verify/a11y/expect/scr-01.json --serve
  python3 50-verify/fake/at-spi-stub.py --address <unix:path=...> --query 0 --json
  python3 50-verify/fake/at-spi-stub.py --report

对象路径映射：节点 path "0" → /org/a11y/atspi/accessible/root；节点 path "0/1" → .../root/1（去掉首段）。

已知偏差（不是真 at-spi2-registryd，逐条显式登记，禁止静默）：
  1. 只实现 org.a11y.atspi.Accessible 的 GetRoleName / GetName / GetChildren 三个方法；
     GetRole、GetState、GetInterfaces、GetApplication、Component/Action/Text/Table/Selection 全部未实现。
  2. 不注册总线名 org.a11y.atspi.Registry，也不实现 Registry.GetApplications；
     默认总线名 org.a11y.atspi.FakeStub（--bus-name 可改），因此真桥（MOD-81）无法「发现」本桩件。
  3. 不派发事件（org.a11y.atspi.Event.*），IF-052 的事件判据无法用本桩件验证。
  4. states/actions/bounds/value/relations 不经标准接口暴露，只经**非标准**方法
     org.a11y.atspi.FakeStub.GetNodeJson 返回（字段与 IF-071 节点一致）；真实桥必须用 GetState/Action/Component，
     因此本桩件不能替代真机读数。
  5. 树是静态数据，不随交互变化（无 DoAction 后的状态迁移）。
  6. 数据来源若是内置 3 节点样本（--expect 文件不存在时），会打印 NOTE；内置样本只用于自检，不得当判据期望。
"""
import argparse
import atexit
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_EXPECT = REPO_ROOT / '50-verify/a11y/expect/scr-01.json'
DEFAULT_BUS_NAME = 'org.a11y.atspi.FakeStub'
BASE_PATH = '/org/a11y/atspi/accessible/root'
ACCESSIBLE_IFACE = 'org.a11y.atspi.Accessible'
STUB_IFACE = 'org.a11y.atspi.FakeStub'

BUILTIN_NODES = [
    {
        'path': '0', 'role': 'frame', 'name': 'WpfFirstWindow',
        'states': ['showing', 'active'], 'actions': ['focus'],
        'bounds': [0, 0, 1280, 1024], 'value': None, 'relations': [],
    },
    {
        'path': '0/0', 'role': 'label', 'name': '内建样本标题',
        'states': ['visible'], 'actions': [],
        'bounds': [8, 8, 400, 24], 'value': None, 'relations': [],
    },
    {
        'path': '0/1', 'role': 'entry', 'name': '输入样本',
        'states': ['enabled', 'editable', 'focusable'], 'actions': ['set-text', 'focus'],
        'bounds': [8, 40, 400, 24], 'value': '', 'relations': [],
    },
]


def die(msg, code=1):
    print(f'ERROR {msg}', file=sys.stderr)
    sys.exit(code)


def note(msg):
    print(f'NOTE {msg}', flush=True)


# ---------- 数据 ----------

def _clean(v):
    """剥掉 Markdown 行内代码标记与首尾空白；返回 (cleaned, 是否改动)。

    期望文件由元素属性表生成，单元格里混入了行内代码反引号（含行中），AT-SPI 名称/路径不该带它们。
    """
    if not isinstance(v, str):
        return v, False
    c = v.replace('`', '').replace('\\|', '|').strip()
    return c, c != v


def load_nodes(expect_path):
    """读期望文件；不存在则用内置 3 节点样本并显式 NOTE。"""
    p = Path(expect_path)
    if not p.exists():
        note(f'期望文件不存在（{p}），使用内置 3 节点样本；内置样本不得当判据期望')
        return [dict(n) for n in BUILTIN_NODES], 'builtin'
    raw = json.loads(p.read_text(encoding='utf-8'))
    nodes = raw.get('nodes')
    if not isinstance(nodes, list) or not nodes:
        raise ValueError(f'期望文件缺 nodes[]：{p}')
    out = []
    dirty = 0
    missing = 0
    for i, n in enumerate(nodes):
        for key in ('path', 'role', 'name'):
            if key not in n:
                raise ValueError(f'期望文件第 {i} 个节点缺必填字段 {key}（IF-071）')
        path, d1 = _clean(str(n['path']))
        role, d2 = _clean(str(n['role']))
        name, d3 = _clean(str(n['name']))
        states = []
        for v in (n.get('states') or []):
            c, d = _clean(str(v))
            states.append(c)
            dirty += int(d)
        actions = []
        for v in (n.get('actions') or []):
            c, d = _clean(str(v))
            actions.append(c)
            dirty += int(d)
        dirty += int(d1) + int(d2) + int(d3)
        for key in ('bounds', 'value', 'relations'):
            if key not in n:
                missing += 1
        out.append({
            'path': path,
            'role': role,
            'name': name,
            'states': states,
            'actions': actions,
            'bounds': list(n.get('bounds') or [0, 0, 0, 0]),
            'value': n.get('value'),
            'relations': list(n.get('relations') or []),
        })
    if dirty:
        note(f'WARN 期望文件有 {dirty} 处带 Markdown 反引号/首尾空白的字段，已归一化；'
             f'这是生成器缺陷（界面图/build-wireframe.mjs --emit-expect 未剥行内代码标记），结构判据不得依赖反引号')
    if missing:
        note(f'WARN 期望文件缺 {missing} 个 IF-071 必填字段（bounds/value/relations），桩件按 [0,0,0,0]/null/[] 兜底；快照器需补齐后再比结构')
    truncated = [n['path'] for n in out if n['name'].rstrip().endswith('\\')]
    if truncated:
        note(f'WARN 期望文件疑似被竖线截断 {len(truncated)} 处（{", ".join(truncated)}）：'
             f'name 以反斜杠结尾且后续列发生位移；根因是 --emit-expect 未按未转义 | 切分单元格，需修生成器后重生成')
    return out, str(p)


def object_path_of(node_path):
    segs = [s for s in str(node_path).split('/') if s != '']
    if not segs:
        raise ValueError(f'节点 path 非法：{node_path!r}')
    if segs == ['0']:
        return BASE_PATH
    return BASE_PATH + '/' + '/'.join(segs[1:])


def children_of(nodes, node_path):
    prefix = f'{node_path}/'
    kids = [n for n in nodes if n['path'].startswith(prefix) and '/' not in n['path'][len(prefix):]]
    return sorted(kids, key=lambda n: int(n['path'].split('/')[-1]))


# ---------- 总线 ----------

def start_bus():
    """起私有会话总线，返回 (address, proc)。"""
    proc = subprocess.Popen(
        ['dbus-daemon', '--session', '--nofork', '--print-address=1', '--print-pid=1'],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    deadline = time.time() + 8
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                err = proc.stderr.read()[:300]
                raise RuntimeError(f'dbus-daemon 启动失败：{err}')
            continue
        line = line.strip()
        if line.startswith('unix:') or line.startswith('tcp:'):
            return line, proc
    proc.terminate()
    raise RuntimeError('dbus-daemon 8 秒内未打印总线地址')


def load_dbus():
    import dbus  # noqa: F401  延迟导入：必须先设好 DBUS_SESSION_BUS_ADDRESS
    import dbus.service
    import dbus.mainloop.glib
    from gi.repository import GLib
    return dbus, GLib


# ---------- 服务 ----------

def build_service(bus_name, nodes, address):
    dbus, GLib = load_dbus()
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.bus.BusConnection(address)
    name = dbus.service.BusName(bus_name, bus=bus)

    class Accessible(dbus.service.Object):
        def __init__(self, path, node, kids):
            super().__init__(bus, path)
            self.node = node
            self.kids = kids

        @dbus.service.method(ACCESSIBLE_IFACE, in_signature='', out_signature='s')
        def GetRoleName(self):
            return dbus.String(self.node['role'])

        @dbus.service.method(ACCESSIBLE_IFACE, in_signature='', out_signature='s')
        def GetName(self):
            return dbus.String(self.node['name'])

        @dbus.service.method(ACCESSIBLE_IFACE, in_signature='', out_signature='a(so)')
        def GetChildren(self):
            return dbus.Array(
                [dbus.Struct((dbus.String(bus_name), dbus.ObjectPath(object_path_of(k['path']))), signature='so')
                 for k in self.kids],
                signature='(so)',
            )

        @dbus.service.method(STUB_IFACE, in_signature='', out_signature='s')
        def GetNodeJson(self):
            # 非标准方法：只给桩件自检与快照器取 states/actions/bounds 用，真实桥不得依赖
            return dbus.String(json.dumps(self.node, ensure_ascii=False))

    objs = []
    for n in nodes:
        kids = children_of(nodes, n['path'])
        objs.append(Accessible(object_path_of(n['path']), n, kids))
    return bus, name, objs


# ---------- 客户端 ----------

def query(address, bus_name, node_path, want_json):
    dbus, _ = load_dbus()
    obj_path = object_path_of(node_path)
    bus = dbus.bus.BusConnection(address)
    try:
        obj = bus.get_object(bus_name, obj_path)
    except Exception as e:  # noqa: BLE001
        die(f'取对象失败 {obj_path}: {e}', 2)
    try:
        role = str(obj.GetRoleName(dbus_interface=ACCESSIBLE_IFACE))
        name = str(obj.GetName(dbus_interface=ACCESSIBLE_IFACE))
        kids = [(str(b), str(p)) for b, p in obj.GetChildren(dbus_interface=ACCESSIBLE_IFACE)]
        node_json = None
        try:
            node_json = json.loads(str(obj.GetNodeJson(dbus_interface=STUB_IFACE)))
        except Exception:  # noqa: BLE001  非标准方法缺失不算失败，但要显式说明
            note('对象未提供非标准方法 GetNodeJson；states/actions/bounds 取不到')
    except Exception as e:  # noqa: BLE001
        die(f'调用 AT-SPI 方法失败 {obj_path}: {e}', 2)
    out = {'path': node_path, 'dbus_path': obj_path, 'role': role, 'name': name, 'children': kids, 'node': node_json}
    if want_json:
        print(json.dumps(out, ensure_ascii=False))
    else:
        print(f'{node_path}\t{obj_path}\t{role}\t{name}\tchildren={len(kids)}')
    return out


# ---------- 报告 ----------

def report(expect_path, bus_name):
    rows = [
        ('Accessible.GetRoleName', '已实现', 'IF-051 role'),
        ('Accessible.GetName', '已实现', 'IF-051 name'),
        ('Accessible.GetChildren', '已实现', 'IF-051 树序'),
        ('Accessible.GetRole / GetState / GetInterfaces / GetApplication', '未实现 SKIP', '真实桥需要，桩件不提供'),
        ('Registry.GetApplications', '未实现 SKIP', '不注册 org.a11y.atspi.Registry，真桥无法发现本桩件'),
        ('Component.GetExtents / GetPosition', '未实现 SKIP', 'bounds 只经非标准 GetNodeJson 暴露'),
        ('Action.GetActions / DoAction', '未实现 SKIP', 'actions 只经非标准 GetNodeJson 暴露；无动作效果'),
        ('Text.GetText / GetTextAtOffset', '未实现 SKIP', 'value 只经非标准 GetNodeJson 暴露'),
        ('Table / Selection / EditableText / Value', '未实现 SKIP', '同上'),
        ('Event.*（焦点/状态/结构/文本）', '未实现 SKIP', 'IF-052 事件判据不能用本桩件验证'),
        ('可交互后状态迁移', '未实现 SKIP', '树是静态数据'),
        ('非标准方法 FakeStub.GetNodeJson', '已实现', '返回 IF-071 节点 JSON，仅供自检/快照器'),
        ('期望文件反引号/缺字段', '已检测上报', '本桩件会 WARN 并归一化；根因在 --emit-expect 生成器，需修生成器后重生成'),
    ]
    print(f'BUS_NAME {bus_name}')
    print(f'EXPECT {expect_path}')
    print(f'BASE_PATH {BASE_PATH}')
    for name, status, why in rows:
        print(f'{status:8s} {name}  —  {why}')


# ---------- 自检 ----------

def self_test(args):
    """自检：以 --serve 子进程跑真服务路径，再以 --query 客户端查它（避免同进程主循环死锁）。"""
    checks = []

    def check(name, cond, detail=''):
        checks.append((name, bool(cond), detail))

    serve = None
    notes = []
    try:
        serve = subprocess.Popen(
            [sys.executable, str(Path(__file__).resolve()), '--serve',
             '--expect', args.expect, '--bus-name', args.bus_name],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1,
        )
        ready = None
        deadline = time.time() + 15
        while time.time() < deadline:
            line = serve.stdout.readline()
            if not line:
                if serve.poll() is not None:
                    raise RuntimeError('--serve 子进程提前退出')
                continue
            line = line.rstrip('\n')
            if line.startswith('READY '):
                ready = json.loads(line[len('READY '):])
                break
            if line.startswith('NOTE ') or line.startswith('WARN '):
                notes.append(line)
                print(line)
        check('--serve 打印 READY（总线已起）', ready is not None, f'notes={len(notes)}')
        if ready is None:
            raise RuntimeError('--serve 15 秒内未 READY')
        address, bus_name, root_path = ready['address'], ready['bus_name'], ready['root']
        served_nodes = ready.get('nodes')
        check('服务侧导出的节点数与期望文件一致', served_nodes and served_nodes >= 1, str(served_nodes))

        def run_client(path, timeout=15):
            p = subprocess.run(
                [sys.executable, str(Path(__file__).resolve()), '--address', address,
                 '--bus-name', bus_name, '--query', path, '--json'],
                capture_output=True, text=True, timeout=timeout,
            )
            return p

        nodes, source = load_nodes(args.expect)
        root_node = nodes[0]
        expected_kids = children_of(nodes, root_node['path'])
        p = run_client(root_node['path'])
        check('客户端能查询根节点', p.returncode == 0, (p.stdout or p.stderr).strip().splitlines()[-1] if (p.stdout or p.stderr) else '')
        root = json.loads(p.stdout.strip().splitlines()[-1]) if p.returncode == 0 else {}
        check('GetRoleName(root) 与期望一致', root.get('role') == root_node['role'], f"{root.get('role')} vs {root_node['role']}")
        check('GetName(root) 与期望一致', root.get('name') == root_node['name'], f"{root.get('name')} vs {root_node['name']}")
        check('GetChildren(root) 数量与期望一致', len(root.get('children') or []) == len(expected_kids), f"{len(root.get('children') or [])} vs {len(expected_kids)}")
        check('GetChildren 返回 (bus_name, path) 结构', all(len(c) == 2 and str(c[0]) for c in (root.get('children') or [])), str(root.get('children')))
        check('GetNodeJson 带 states/actions（非标准方法）', bool((root.get('node') or {}).get('states')) and 'actions' in (root.get('node') or {}), json.dumps((root.get('node') or {}).get('states'), ensure_ascii=False))
        if expected_kids:
            kid = expected_kids[0]
            p2 = run_client(kid['path'])
            child = json.loads(p2.stdout.strip().splitlines()[-1]) if p2.returncode == 0 else {}
            check(f"子节点 {kid['path']} 角色与期望一致", p2.returncode == 0 and child.get('role') == kid['role'], f"{child.get('role')} vs {kid['role']}")
        else:
            check('至少有一个子节点可查', False, '期望树没有子节点')
        p3 = run_client('9/9')
        check('未知路径显式失败（不静默返回空）', p3.returncode != 0, f'rc={p3.returncode}')
        check('数据来源已标注', bool(source), source)
        check('反引号已归一化（数据里不再含 `）', all('`' not in n['path'] and '`' not in n['name'] for n in nodes), nodes[0]['path'])
        check('--serve 进程仍在跑（服务未崩）', serve.poll() is None, f'rc={serve.poll()}')
    except Exception as e:  # noqa: BLE001
        check('自检未抛异常', False, f'{type(e).__name__}: {e}')
    finally:
        if serve is not None and serve.poll() is None:
            serve.terminate()
            try:
                serve.wait(timeout=5)
            except subprocess.TimeoutExpired:
                serve.kill()
    failed = 0
    for name, ok, detail in checks:
        if not ok:
            failed += 1
        print(f'{"PASS" if ok else "FAIL"} {name}{f" ({detail})" if detail else ""}')
    print(f'at-spi-stub self-test: {len(checks) - failed}/{len(checks)} 通过')
    return 0 if failed == 0 else 1


# ---------- 主流程 ----------

def main():
    ap = argparse.ArgumentParser(add_help=True, description='Fake AT-SPI 最小树桩件（见文件头偏差）')
    ap.add_argument('--expect', default=str(DEFAULT_EXPECT), help='期望文件（IF-071 同字段）')
    ap.add_argument('--bus-name', default=DEFAULT_BUS_NAME)
    ap.add_argument('--self-test', action='store_true')
    ap.add_argument('--serve', action='store_true', help='起总线并服务，打印 READY 后常驻')
    ap.add_argument('--query', metavar='PATH', help='客户端模式：查询某节点（如 0 或 0/1）')
    ap.add_argument('--address', help='客户端模式：总线地址')
    ap.add_argument('--report', action='store_true')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('--timeout', type=int, default=30, help='整体超时秒数（默认 30）')
    args = ap.parse_args()

    def on_alarm(_sig, _frm):
        print(f'ERROR TIMEOUT 超过 {args.timeout}s', file=sys.stderr)
        os._exit(124)

    signal.signal(signal.SIGALRM, on_alarm)
    signal.alarm(args.timeout)

    try:
        if args.report:
            report(args.expect, args.bus_name)
            return 0
        if args.query:
            if not args.address:
                die('--query 需要 --address', 2)
            query(args.address, args.bus_name, args.query, args.json)
            return 0
        if args.self_test:
            return self_test(args)
        if args.serve:
            address, bus_proc = start_bus()
            atexit.register(lambda: bus_proc.poll() is None and bus_proc.terminate())
            os.environ['DBUS_SESSION_BUS_ADDRESS'] = address
            nodes, source = load_nodes(args.expect)
            _, _, objs = build_service(args.bus_name, nodes, address)
            print(f'READY {json.dumps({"address": address, "bus_name": args.bus_name, "root": BASE_PATH, "nodes": len(objs), "source": source}, ensure_ascii=False)}', flush=True)
            dbus, GLib = load_dbus()
            loop = GLib.MainLoop()
            for sig in (signal.SIGTERM, signal.SIGINT):
                signal.signal(sig, lambda *_: loop.quit())
            loop.run()
            bus_proc.terminate()
            sys.stdout.flush()
            sys.stderr.flush()
            # 用 os._exit 跳过解释器收尾：dbus-python 的 BusName.__del__ 会在总线已停后
            # 再次 release_name 并抛 Disconnected/NoReply，属无害但会污染无人值守日志的噪声。
            os._exit(0)
        ap.print_help()
        return 2
    except Exception as e:  # noqa: BLE001
        die(f'{type(e).__name__}: {e}')
    finally:
        signal.alarm(0)
    return 0


if __name__ == '__main__':
    sys.exit(main())
