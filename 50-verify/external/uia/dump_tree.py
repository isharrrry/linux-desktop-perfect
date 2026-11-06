#!/usr/bin/env python3
"""【K-631】用 pyatspi 枚举桌面上所有 AT-SPI 应用及其子控件。

用法：
    DISPLAY=:303 python3 50-verify/external/uia/dump_tree.py [--json]

依赖：python3-pyatspi（本机已装）
前提：SourceTree 运行中，且 `win32_atspi.c` 的 AT-SPI 桥接已生效：
  · `atspi_address()` 找 `$HOME/.cache/at-spi/bus_<display>`（K-625）
  · `dbus_bus_request_name("org.a11y.atspi.SourceTree")`（K-620）
  · `org.a11y.atspi.Socket.Embed` 到 registry（K-621）
  · `Properties.Get` 同时认 `Name`/`name`（K-630）
  · `(so)` 里用 `DBUS_TYPE_OBJECT_PATH`（K-626）
  · 泵线程用独立 `g_a11y_running` 标志（K-624）
"""
import json
import sys

import pyatspi


def dump_app(app):
    # 每条属性都可能抛 GLib.GError（应用已退出 / 是残留条目）⇒ 逐个容错
    def safe(fn, default=None):
        try:
            return fn()
        except Exception:  # noqa: BLE001
            return default

    item = {
        "name": safe(lambda: app.name, ""),
        "role": safe(lambda: app.getRoleName(), "?"),
        "childCount": safe(lambda: app.childCount, 0),
        "children": [],
    }
    try:
        for j in range(app.childCount):
            c = app.getChildAtIndex(j)
            item["children"].append({
                "name": c.name,
                "role": c.getRoleName(),
                "childCount": c.childCount,
            })
    except Exception as exc:  # noqa: BLE001 - pyatspi 抛的是 GLib.GError
        item["children_error"] = str(exc)[:120]
    return item


def main():
    as_json = "--json" in sys.argv
    desktop = pyatspi.Registry.getDesktop(0)
    apps = []
    for i in range(desktop.childCount):
        try:
            app = desktop.getChildAtIndex(i)
        except Exception as exc:  # noqa: BLE001
            apps.append({"index": i, "error": str(exc)[:120]})
            continue
        entry = dump_app(app)
        if entry["name"]:
            apps.append({"index": i, **entry})
        else:
            apps.append({"index": i, "error": "unnamed app (likely exited)"})

    if as_json:
        print(json.dumps({"desktop_child_count": desktop.childCount, "apps": apps},
                         ensure_ascii=False, indent=2))
        return 0

    print(f"桌面子项数: {desktop.childCount}")
    for a in apps:
        if "error" in a:
            print(f"  [{a['index']}] ERR {a['error']}")
            continue
        print(f"  [{a['index']}] {a['name']!r} role={a['role']} children={a['childCount']}")
        for j, c in enumerate(a["children"]):
            print(f"        - [{j}] {c['name']!r} role={c['role']} children={c['childCount']}")

    found = [a for a in apps if a.get("name") == "SourceTree"]
    if found:
        total = sum(a.get("childCount", 0) for a in found)
        print(f"✅ 找到 SourceTree，枚举了 {total} 个控件")
        return 0
    print("❌ 未找到 SourceTree")
    return 1


if __name__ == "__main__":
    sys.exit(main())
