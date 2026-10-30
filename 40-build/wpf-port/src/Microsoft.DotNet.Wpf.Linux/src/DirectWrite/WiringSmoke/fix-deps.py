#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 app-local WindowsBase 写进 deps.json（T2 取证用；同时也验证"部署修法①"）。

背景：框架自带 WindowsBase 4.0.0.0/PublicKeyToken=31bf3856ad364e35，
本仓 WindowsBase 4.0.0.1/同一个 PKT —— 只差版本，于是运行期按框架 TPA 命中 4.0.0.0
并报 0x80131040。把它作为 app-local 条目写进 deps.json（版本更高）就能让宿主选它。
"""
import json, os, sys

path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "bin/Debug/DirectWrite.Linux.WiringSmoke.deps.json")
d = json.load(open(path, encoding="utf-8"))
lib = "WindowsBase/4.0.0.1"
changed = False

for tf, targets in d.get("targets", {}).items():
    if lib not in targets:
        targets[lib] = {"runtime": {"WindowsBase.dll": {}}}
        changed = True
if lib not in d.setdefault("libraries", {}):
    d["libraries"][lib] = {"type": "reference", "serviceable": False, "sha512": ""}
    changed = True

if changed:
    json.dump(d, open(path, "w", encoding="utf-8"), indent=2)
    print(f"[OK] 已把 {lib} 写进 {os.path.basename(path)}")
else:
    print(f"[SKIP] {lib} 已在 deps.json 里")
