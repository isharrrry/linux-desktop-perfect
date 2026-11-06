#!/usr/bin/env python3
"""UIA/AT-SPI 验证脚本：枚举 AT-SPI 桌面树，找 SourceTree 并 dump 控件。"""
import sys

def dump(el, depth=0, max_depth=4, max_children=15, out=None):
    if depth > max_depth:
        return 0
    n = 0
    try:
        cnt = el.childCount
    except Exception:
        return 0
    for i in range(min(cnt, max_children)):
        try:
            c = el.getChildAtIndex(i)
            name = c.name or ""
            role = c.getRoleName()
            print(f"{'  ' * depth}[{role}] {name!r}", file=out)
            n += 1
            n += dump(c, depth + 1, max_depth, max_children, out)
        except Exception as e:
            print(f"{'  ' * depth}ERROR: {e}", file=out)
    return n

def main():
    try:
        import pyatspi
    except ImportError:
        print("ERROR: pyatspi 未安装", file=sys.stderr)
        return 2

    d = pyatspi.Registry.getDesktop(0)
    print(f"桌面子项数: {d.childCount}")
    total = 0
    found = False
    for i in range(d.childCount):
        try:
            app = d.getChildAtIndex(i)
            name = app.name or ""
            role = app.getRoleName()
            print(f"[{i}] app={name!r} role={role} children={app.childCount}")
            if "sourcetree" in name.lower():
                found = True
                print(f"  *** 找到 SourceTree！开始枚举控件树 ***")
                n = dump(app, 1, 5, 20)
                total += n
                print(f"  *** 共枚举 {n} 个控件 ***")
        except Exception as e:
            print(f"[{i}] ERROR: {e}")

    print()
    if found:
        print(f"✅ 找到 SourceTree，枚举了 {total} 个控件")
        return 0 if total >= 10 else 1
    else:
        print("❌ 未找到 SourceTree")
        return 1

if __name__ == "__main__":
    sys.exit(main())
