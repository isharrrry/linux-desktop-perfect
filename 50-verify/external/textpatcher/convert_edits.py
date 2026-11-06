#!/usr/bin/env python3
"""临时转换脚本：把 patch-*.py 的 EDITS 转成 edits.txt（Tab 分隔）"""
import re, sys, io
src = sys.argv[1]
out = sys.argv[2]
s = io.open(src, encoding='utf-8').read()
# 提取 EDITS = [ ... ] 块
m = re.search(r'^EDITS\s*=\s*\[(.*?)^\]', s, re.S | re.M)
if not m:
    print('找不到 EDITS 块'); sys.exit(1)
block = m.group(1)
# 逐条提取 (name, anchor, repl)
# 简化：按 "),\n    (" 分割，或者按 "(" 匹配
entries = []
# 用正则匹配 ("...", ANCHOR, '''...'''|"...")
pat = re.compile(r'\(\s*"[^"]*"\s*,\s*(\w+)\s*,\s*(?:\'\'\'(.*?)\'\'\'|"((?:[^"\\]|\\.)*)")\s*\)', re.S)
for m2 in pat.finditer(block):
    anchor_name = m2.group(1)
    repl = m2.group(2) if m2.group(2) is not None else m2.group(3)
    entries.append((anchor_name, repl))
print(f'找到 {len(entries)} 条 EDITS')
# 提取 anchor 常量的定义（WB_TYPE_HEAD_ANCHOR = "..."）
anchors = {}
for m3 in re.finditer(r'^(\w+_ANCHOR)\s*=\s*(?:\'\'\'(.*?)\'\'\'|"((?:[^"\\]|\\.)*)")', s, re.S | re.M):
    name = m3.group(1)
    val = m3.group(2) if m3.group(2) is not None else m3.group(3)
    anchors[name] = val
print(f'找到 {len(anchors)} 个 anchor 常量')
# 写出 edits.txt（Tab 分隔：name\tanchor\trepl）
with io.open(out, 'w', encoding='utf-8') as f:
    for i, (aname, repl) in enumerate(entries):
        if aname not in anchors:
            print(f'[跳过] {aname} 没有定义'); continue
        anchor = anchors[aname]
        a = anchor.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        r = repl.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        f.write(f'{aname}\t{a}\t{r}\n')
print(f'写出 {out}')
