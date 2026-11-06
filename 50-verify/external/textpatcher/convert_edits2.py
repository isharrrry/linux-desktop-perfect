#!/usr/bin/env python3
"""临时转换脚本 v2：支持多行拼接 ( '...' '...' )"""
import re, sys, io
src, out = sys.argv[1], sys.argv[2]
s = io.open(src, encoding='utf-8').read()

def _unescape(s):
    """把源码文本里的字面 \\n / \\t / \\\\ 转成真实字符"""
    return s.replace('\\\\', '\\').replace('\\n', '\n').replace('\\t', '\t')


def parse_const(name):
    """解析 NAME = '''...''' 或 NAME = ( '...' '...' ) 或 NAME = "..." 或 NAME = "..." "..." """
    # 单块三引号
    m = re.search(rf'^{name}\s*=\s*[rR]?\'\'\'(.*?)\'\'\'', s, re.S | re.M)
    if m: return _unescape(m.group(1))
    # 多行拼接（括号内单引号或双引号，可带 \n）
    m = re.search(rf'^{name}\s*=\s*\(\s*((?:\s*(?:\'[^\']*\'|"[^"]*")\s*)+)\)', s, re.S | re.M)
    if m:
        parts = re.findall(r"(?:'([^']*)'|\"([^\"]*)\")", m.group(1))
        return _unescape(''.join(a if a else b for a, b in parts))
    # 单行双引号
    m = re.search(rf'^{name}\s*=\s*"((?:[^"\\]|\\.)*)"', s, re.M)
    if m: return _unescape(m.group(1))
    return None

# 提取 EDITS 块
m = re.search(r'^EDITS\s*=\s*\[(.*?)^\]', s, re.S | re.M)
if not m:
    print('找不到 EDITS 块'); sys.exit(1)
block = m.group(1)
entries = []
pat = re.compile(r'\(\s*"[^"]*"\s*,\s*(\w+)\s*,\s*(\w+)\s*\)')
for m2 in pat.finditer(block):
    entries.append((m2.group(1), m2.group(2)))
print(f'找到 {len(entries)} 条 EDITS')
ok = 0
with io.open(out, 'w', encoding='utf-8') as f:
    for aname, rname in entries:
        anchor = parse_const(aname)
        repl = parse_const(rname)
        if anchor is None:
            print(f'[跳过] {aname} 无法解析'); continue
        if repl is None:
            print(f'[跳过] {rname} 无法解析'); continue
        a = anchor.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        r = repl.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        f.write(f'{aname}\t{a}\t{r}\n')
        ok += 1
print(f'写出 {out}（{ok} 条）')
