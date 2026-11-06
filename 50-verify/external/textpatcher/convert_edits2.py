#!/usr/bin/env python3
"""临时转换脚本 v2：支持多行拼接 ( '...' '...' )"""
import re, sys, io
src, out = sys.argv[1], sys.argv[2]
s = io.open(src, encoding='utf-8').read()

def _unescape(s):
    """把源码文本里的字面 \\n / \\t / \\\\ 转成真实字符"""
    return s.replace('\\\\', '\\').replace('\\n', '\n').replace('\\t', '\t')


def parse_const(name):
    # 三引号双引号（可带 r 前缀）
    m = re.search(rf'^{name}\s*=\s*[rR]?"""(.*?)"""', s, re.S | re.M)
    if m: return _unescape(m.group(1))
    # 单行单引号（排除三引号，在 _unescape 前）
    m = re.search(rf"^{name}\s*=\s*'(?!'')([^'\\]*(?:\\.[^'\\]*)*)'", s, re.M)
    if m: return _unescape(m.group(1))
    """解析 NAME = '''...''' 或 NAME = ( '...' '...' ) 或 NAME = "..." 或 NAME = "..." "..." """
    # 单块三引号
    # 三引号单引号：用「下一个顶层常量名之前」的最后一个 '''
    pos = s.find(f"{name} = '''")
    if pos < 0: pos = s.find(f"{name} = r'''")
    if pos >= 0:
        start = pos + len(f"{name} = '''") if s.find(f"{name} = '''") >= 0 else pos + len(f"{name} = r'''")
        # 找下一个顶层常量（^XXX = ）或最后
        m_next = re.search(r'^[A-Z_][A-Z0-9_]*\s*=', s[start:], re.M)
        limit = start + m_next.start() if m_next else len(s)
        # 在 limit 之前找最后一个 '''
        last = s.rfind("'''", start, limit)
        if last > start:
            return _unescape(s[start:last])
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
pat = re.compile(r'\(\s*"[^"]*"\s*,\s*(\w+)\s*,\s*(?:(\w+)|\'\'\'(.*?)\'\'\'|"((?:[^"\\]|\\.)*)")\s*\)', re.S)
for m2 in pat.finditer(block):
    entries.append((m2.group(1), m2.group(2), m2.group(3), m2.group(4) if m2.lastindex >= 4 else None))
print(f'找到 {len(entries)} 条 EDITS')
ok = 0
with io.open(out, 'w', encoding='utf-8') as f:
    for entry in entries:
        aname, rname = entry[0], entry[1]
        rinline = entry[2] if len(entry) > 2 else None
        rinline2 = entry[3] if len(entry) > 3 else None
        anchor = parse_const(aname)
        if rname and rname.isidentifier():
            repl = parse_const(rname)
        elif rinline is not None:
            repl = rinline
        elif rinline2 is not None:
            repl = rinline2
        else:
            repl = None
        if anchor is None:
            print(f'[跳过] {aname} 无法解析'); continue
        if repl is None:
            print(f'[跳过] {rname} 无法解析'); continue
        a = anchor.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        r = repl.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')
        f.write(f'{aname}\t{a}\t{r}\n')
        ok += 1
print(f'写出 {out}（{ok} 条）')
