#!/usr/bin/env python3
# PM-0109 AT-SPI 枚举器（僵尸过滤版）
# 只走 D-Bus，不 import pyatspi：死应用不会把调用卡死在 pyatspi 的 Cache 请求上。
# 用法：python3 a11y_enum.py --name SourceTree --depth 8 [--bus unix:path=...]
#   --only 按 **总线名**（:1.49 这种）过滤，--name 按可访问名（SourceTree）过滤；
#   21:15 那次窗口就是错用了 --only SourceTree 导致 SELECTED 0。
#   --selftest 离线自测（不需要总线）：3 元组旧形态必红、5 元组错误项不抛、选择器语义。
import subprocess, os, sys, collections, re, argparse

DEFAULT_BUS = "unix:path=/run/user/1000/at-spi/bus"
REGISTRY = "org.a11y.atspi.Registry"
REG_PATH = "/org/a11y/atspi/accessible/root"

def gdbus(bus, dest, path, iface, method, args=None, timeout=4):
    cmd = ["gdbus", "call", "--address", bus, "--dest", dest,
           "--object-path", path, "--method", "%s.%s" % (iface, method)]
    if args:
        cmd.append(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    if p.returncode != 0:
        raise RuntimeError(((p.stderr or "") + (p.stdout or "")).strip()[:240])
    return p.stdout.strip()

def pairs(out):
    return re.findall(r"\(\s*'([^']+)'\s*,\s*(?:objectpath\s+)?'([^']+)'\s*\)", out)

def owner_alive(bus, name):
    try:
        out = gdbus(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus",
                    "org.freedesktop.DBus", "NameHasOwner", name, timeout=3)
        return "true" in out
    except Exception:
        return False

def get_name(bus, dest, path):
    try:
        raw = gdbus(bus, dest, path, "org.a11y.atspi.Accessible", "GetName", timeout=3)
        m = re.search(r"'([^']*)'", raw)
        return m.group(1) if m else "?"
    except Exception:
        return "?"

def get_role(bus, dest, path):
    try:
        out = gdbus(bus, dest, path, "org.a11y.atspi.Accessible", "GetRole", timeout=3)
        m = re.search(r"int32\s+(-?\d+)", out)
        return int(m.group(1)) if m else -1
    except Exception:
        return -1

# gi/Atspi 缺 binding 时角色会退化成裸数字（127 上实测 role_names() 返回 {}），
# 静态表兜底，至少让判读用到的几种角色显示成名字。gi 可用时以 gi 为准。
STATIC_ROLES = {
    7: "check box", 11: "combo box", 23: "frame", 32: "list item", 35: "menu item",
    43: "push button", 53: "split pane", 55: "table", 56: "table cell", 61: "text",
    65: "tree", 90: "table row", 91: "tree item",
}

def role_names():
    rn = dict(STATIC_ROLES)
    try:
        import gi
        gi.require_version("Atspi", "2.0")
        from gi.repository import Atspi
        rn.update({int(r): r.value_nick for r in Atspi.Role})
    except Exception:
        pass
    return rn

def walk(bus, dest, path, depth, maxdepth, out):
    if depth > maxdepth:
        return
    try:
        kids = pairs(gdbus(bus, dest, path, "org.a11y.atspi.Accessible", "GetChildren", timeout=3))
    except Exception as e:
        # 必须保持 5 元组：3 元组会让后面的解包直接抛 ValueError（2026-11-20 21:15 实崩于此）
        out.append((depth, "GETCHILDREN_ERR", dest, path, str(e)[:80]))
        return
    for d, p in kids:
        out.append((depth, get_role(bus, d, p), d, p, get_name(bus, d, p)))
        walk(bus, d, p, depth + 1, maxdepth, out)

def render_nodes(out, rn):
    """把 walk 的输出渲染成 (角色直方图, 行列表)。抽成函数是为了能离线自测。"""
    hist = collections.Counter()
    for item in out:
        r = item[1]
        hist[rn.get(r, str(r)) if isinstance(r, int) else r] += 1
    lines = []
    for depth, r, d, p, nm2 in out:
        label = rn.get(r, str(r)) if isinstance(r, int) else r
        lines.append("  " + "  " * depth + "| %s | %s" % (label, nm2))
    return hist, lines

def app_selected(busname, accname, only, wantname):
    """--only 按总线名过滤、--name 按可访问名过滤（两者语义不同，别混用）。"""
    if only and only.lower() not in busname.lower():
        return False
    if wantname and wantname.lower() not in (accname or "").lower():
        return False
    return True

def selftest():
    """离线自测：不连总线，只测渲染/角色表/选择器。要求有一条「旧形态必红」的红测。"""
    pass_n = 0
    fail_n = 0
    def ok(m):
        nonlocal pass_n
        pass_n += 1
        print("PASS  %s" % m)
    def ng(m):
        nonlocal fail_n
        fail_n += 1
        print("FAIL  %s" % m)

    rn = role_names()

    # 1) 修好后的 5 元组错误项：渲染不许抛，且错误项要单独计一桶
    out = [(1, 23, ":1.1", "/a", "SourceTree"),
           (2, "GETCHILDREN_ERR", ":1.1", "/b", "timeout"),
           (2, 32, ":1.1", "/c", "a.txt")]
    try:
        hist, lines = render_nodes(out, rn)
        ok("5 元组错误项渲染不抛（hist=%s）" % dict(hist))
        if "list item" in hist and "GETCHILDREN_ERR" in hist:
            ok("静态角色表生效：32→list item，GETCHILDREN_ERR 单独计一桶")
        else:
            ng("角色直方图不对 hist=%s" % dict(hist))
        if any("| a.txt" in l for l in lines):
            ok("节点行含 | a.txt（文件行判据的数据面）")
        else:
            ng("节点行里找不到 | a.txt")
    except Exception as e:
        ng("渲染抛异常 %r" % (e,))

    # 2) 红测：旧形态的 3 元组必须抛 ValueError（证明这条自测能变红）
    try:
        render_nodes([(1, 23, ":1.1", "/a", "x"), (2, "ERR", "boom")], rn)
        ng("3 元组居然没抛 ⇒ 红测失效")
    except ValueError:
        ok("红测：3 元组解包必抛 ValueError（21:15:17 那次崩的就是它）")
    except Exception as e:
        ng("红测异常类型不对 %r" % (e,))

    # 3) 选择器语义：--only 认总线名、--name 认可访问名
    if app_selected(":1.49", "SourceTree", "", "SourceTree"):
        ok("--name SourceTree 命中可访问名")
    else:
        ng("--name 不命中")
    if not app_selected(":1.49", "SourceTree", "SourceTree", ""):
        ok("--only SourceTree 不命中（总线名是 :1.49）——21:15 那次 SELECTED 0 的根因")
    else:
        ng("--only 语义被改坏")

    print("SELFTEST=%s fail=%d cases=%d" % ("PASS" if fail_n == 0 else "FAIL", fail_n, pass_n + fail_n))
    return 0 if fail_n == 0 else 1

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--depth", type=int, default=2)
    ap.add_argument("--only", default="", help="只枚举 bus name 含该子串的应用（是 :1.49 这种总线名，不是窗口标题）")
    ap.add_argument("--name", default="", help="只枚举可访问名（GetName）含该子串的应用")
    ap.add_argument("--bus", default=os.environ.get("AT_SPI_BUS_ADDRESS") or DEFAULT_BUS)
    ap.add_argument("--selftest", action="store_true", help="离线自测，不连总线")
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    bus, rn = a.bus, role_names()
    print("BUS %s" % bus)
    try:
        raw = gdbus(bus, REGISTRY, REG_PATH, "org.a11y.atspi.Accessible", "GetChildren", timeout=10)
    except Exception as e:
        print("REGISTRY_UNRESPONSIVE %s" % e)
        return 3
    entries = pairs(raw)
    counts = collections.Counter(entries)
    print("REGISTRY_ENTRIES %d  UNIQUE_ROOTS %d" % (len(entries), len(counts)))
    live, zombies, entries_zombie = [], [], 0
    for (name, path), n in counts.items():
        if not owner_alive(bus, name):
            zombies.append((name, path, n)); entries_zombie += n; continue
        live.append((name, path, n))
    print("ZOMBIE_FILTERED %d (unique=%d)" % (entries_zombie, len(zombies)))
    for name, path, n in zombies:
        print("  ZOMBIE name=%s path=%s entries=%d" % (name, path, n))
    # 先按总线名粗筛，再按可访问名细筛（可访问名要现取，故分两步）
    cand = [x for x in live if not a.only or a.only.lower() in x[0].lower()]
    sel = []
    for nm0, path0, n0 in cand:
        if a.name and a.name.lower() not in get_name(bus, nm0, path0).lower():
            continue
        sel.append((nm0, path0, n0))
    print("LIVE_APPS %d  SELECTED %d%s%s" % (len(live), len(sel),
          (" (--only %s)" % a.only) if a.only else "",
          (" (--name %s)" % a.name) if a.name else ""))
    total = 0
    for name, path, n in sel:
        nm = get_name(bus, name, path)
        print("APP %s name=%s entries=%d" % (name, nm, n))
        if n > 1:
            print("  DUPLICATE_ROOTS %d (同一 well-known 名被多次 Embed)" % n)
        out = []
        walk(bus, name, path, 1, a.depth, out)
        total += len(out)
        hist, lines = render_nodes(out, rn)
        print("  NODES %d  ROLES %s" % (len(out), dict(hist)))
        for ln in lines:
            print(ln)
        try:
            gdbus(bus, name, path, "org.a11y.atspi.Cache", "GetItems", timeout=3)
            print("  CACHE_GETITEMS OK")
        except Exception as e:
            print("  CACHE_GETITEMS MISSING: %s" % str(e)[:120])
    print("TOTAL_NODES %d" % total)
    return 0 if sel else 2

if __name__ == "__main__":
    sys.exit(main())