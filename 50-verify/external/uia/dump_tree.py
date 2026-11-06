#!/usr/bin/env python3
"""UIA/AT-SPI 验证脚本：枚举 AT-SPI 桌面树，找 SourceTree 并 dump 控件。

用法：
  DISPLAY=:303 python3 dump_tree.py            # 人类可读
  DISPLAY=:303 python3 dump_tree.py --json     # JSON 输出
  DISPLAY=:303 python3 dump_tree.py --wait 60  # 等最多 60 秒直到找到

退出码：
  0 = 找到 SourceTree 且枚举 >= 10 个控件
  1 = 找到但控件不足
  2 = pyatspi 不可用
  3 = 超时未找到
"""
import sys, json, time

def collect(el, depth=0, max_depth=5, max_children=30):
    """递归收集控件信息。"""
    out = []
    if depth > max_depth:
        return out
    try:
        cnt = el.childCount
    except Exception:
        return out
    for i in range(min(cnt, max_children)):
        try:
            c = el.getChildAtIndex(i)
            item = {
                "depth": depth,
                "role": c.getRoleName(),
                "name": (c.name or ""),
            }
            # 尝试拿描述和状态
            try:
                item["description"] = c.description or ""
            except Exception:
                item["description"] = ""
            try:
                item["states"] = [s for s in c.getState().getStates()][:8]
            except Exception:
                item["states"] = []
            out.append(item)
            out.extend(collect(c, depth + 1, max_depth, max_children))
        except Exception as e:
            out.append({"depth": depth, "role": "ERROR", "name": str(e)})
    return out

def find_sourcetree(d):
    for i in range(d.childCount):
        try:
            app = d.getChildAtIndex(i)
            if "sourcetree" in (app.name or "").lower():
                return app
        except Exception:
            pass
    return None

def main():
    args = sys.argv[1:]
    as_json = "--json" in args
    wait_sec = 0
    if "--wait" in args:
        try:
            wait_sec = int(args[args.index("--wait") + 1])
        except Exception:
            pass

    try:
        import pyatspi
    except ImportError:
        print("ERROR: pyatspi 未安装", file=sys.stderr)
        return 2

    deadline = time.time() + wait_sec
    app = None
    d = pyatspi.Registry.getDesktop(0)
    while True:
        app = find_sourcetree(d)
        if app is not None or time.time() >= deadline:
            break
        time.sleep(1)

    result = {
        "desktop_child_count": d.childCount,
        "apps": [],
        "found_sourcetree": app is not None,
        "controls": [],
    }
    for i in range(d.childCount):
        try:
            a = d.getChildAtIndex(i)
            result["apps"].append({
                "index": i,
                "name": a.name or "",
                "role": a.getRoleName(),
                "children": a.childCount,
            })
        except Exception as e:
            result["apps"].append({"index": i, "error": str(e)})

    if app is not None:
        result["controls"] = collect(app)

    if as_json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(f"桌面子项数: {result['desktop_child_count']}")
        for a in result["apps"]:
            print(f"  [{a.get('index')}] {a.get('name')!r} role={a.get('role')} children={a.get('children')}")
        if app is None:
            print("\n❌ 未找到 SourceTree")
        else:
            n = len(result["controls"])
            print(f"\n✅ 找到 SourceTree，枚举了 {n} 个控件：")
            for c in result["controls"]:
                print(f"{'  ' * c['depth']}[{c['role']}] {c['name']!r}")

    if app is None:
        return 3
    return 0 if len(result["controls"]) >= 10 else 1

if __name__ == "__main__":
    sys.exit(main())
