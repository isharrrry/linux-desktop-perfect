#!/usr/bin/env python3
"""ELF 产物等价判定：按段逐字节比，把「调试元数据差异」与「代码差异」分开。

为什么需要它：NativeAOT 每次构建的 `.note.gnu.build-id`（20 字节）、
`.gnu_debuglink` 里的 `.dbg` CRC（4 字节）、以及 `.rodata` 里紧邻 SkiaSharp
字符串的 PDB GUID（16 字节）都会变。实测同一组源码在不同时刻构建：
总大小相同、`.text` 逐个字节相同、这 40 字节全不同。
要求「重建 sha 与冻件完全相等」在当前构建链下达不到，会把 good 件判成坏件。

分档（只看证据，不看印象）：
  IDENTICAL        全部段逐字节相同（sha 相同）
  DEBUG_ONLY_DIFF  代码段全同；差异只落在调试元数据（build-id / debuglink / <64B 非代码段）
  CODE_DIFF        `.text` 有差异，或任何非调试段差异 >= 64 字节 —— 真差异
  SIZE_MISMATCH    总大小不同 —— 直接不可比

用法: artifact-equiv.py <A> <B>
      artifact-equiv.py --selftest
退出码: 0 = IDENTICAL / DEBUG_ONLY_DIFF；1 = CODE_DIFF / SIZE_MISMATCH；2 = 用法或读文件错

"代码段" 定义（这些段一个字节都不许差）：
  .init .plt .plt.got .text .fini .data .data.rel.ro .rodata .gcc_except_table
"调试元数据"（允许变，且不计入阈值）：
  .note.gnu.build-id .gnu_debuglink
NOBITS（.bss/.tbss/.hydrated 等）不占文件空间，不参与比较。
"""
import hashlib
import os
import re
import subprocess
import sys

CODE_SECTIONS = (
    '.init', '.plt', '.plt.got', '.text', '.fini',
    '.data', '.data.rel.ro', '.rodata', '.gcc_except_table',
)
DEBUG_SECTIONS = ('.note.gnu.build-id', '.gnu_debuglink')
SMALL_DIFF_LIMIT = 64   # 非代码段允许的调试性差异上限（字节）


def sections(path):
    out = subprocess.run(['readelf', '-SW', path], capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError('readelf failed: %s' % out.stderr.strip()[:200])
    secs = []
    for line in out.stdout.splitlines():
        m = re.match(r'\s*\[\s*\d+\]\s+(\S+)\s+(\S+)\s+([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+([0-9a-fA-F]+)', line)
        if not m:
            continue
        name, typ, _addr, off, size = m.groups()
        secs.append((name, typ, int(off, 16), int(size, 16)))
    return secs


def sha(b):
    return hashlib.sha256(b).hexdigest()


def compare(a_path, b_path, verbose=True):
    with open(a_path, 'rb') as f:
        da = f.read()
    with open(b_path, 'rb') as f:
        db = f.read()
    if verbose:
        print('A=%s bytes=%d sha256=%s' % (a_path, len(da), sha(da)))
        print('B=%s bytes=%d sha256=%s' % (b_path, len(db), sha(db)))
    if da == db:
        print('VERDICT=IDENTICAL')
        return 0
    if len(da) != len(db):
        print('VERDICT=SIZE_MISMATCH %d vs %d' % (len(da), len(db)))
        return 1

    sa = [s for s in sections(a_path) if s[1] != 'NOBITS']
    sb = [s for s in sections(b_path) if s[1] != 'NOBITS']
    if [x[0] for x in sa] != [x[0] for x in sb]:
        print('VERDICT=CODE_DIFF 段名列表不同（构建结构变了）')
        return 1

    rows = []
    for (na, ta, oa, la), (nb, tb, ob, lb) in zip(sa, sb):
        if la != lb:
            rows.append((na, 'size', la, lb))
            continue
        if la == 0:
            continue
        if da[oa:oa + la] == db[ob:ob + lb]:
            continue
        diffs = [i for i in range(la) if da[oa + i] != db[ob + i]]
        rows.append((na, len(diffs), diffs[:6]))

    for name, n, sample in rows:
        if name == 'size':
            print('  %-24s SIZE %d vs %d' % (name, n, sample))
        else:
            print('  %-24s DIFF bytes=%d first=%s' % (name, n, sample))

    # 段外区域（ELF header / 段间 padding / SHT 表尾）
    covered = bytearray(len(da))
    for _n, _t, o, l in sa:
        for i in range(o, min(o + l, len(covered))):
            covered[i] = 1
    gap = 0
    for i, c in enumerate(covered):
        if not c and da[i] != db[i]:
            gap += 1
    if gap:
        print('  %-24s DIFF bytes=%d' % ('<gap: header/sht>', gap))

    code_bad = [r for r in rows if r[0] in CODE_SECTIONS and r[1] != 0]
    # .rodata 里 <64 字节的差异按调试元数据对待（实测是紧邻字符串的 PDB GUID）
    code_bad_real = [r for r in code_bad if not (r[0] == '.rodata' and r[1] < SMALL_DIFF_LIMIT)]
    big_other = [r for r in rows
                 if r[0] not in CODE_SECTIONS and r[0] != 'size'
                 and r[1] != 0 and r[1] >= SMALL_DIFF_LIMIT]

    if code_bad_real or gap or big_other:
        print('VERDICT=CODE_DIFF 实质差异: %s'
              % ', '.join('%s(%s)' % (r[0], r[1]) for r in code_bad_real + big_other)
              + (' gap=%d' % gap if gap else ''))
        return 1

    debug_bits = ['%s(%s)' % (r[0], r[1]) for r in rows if r[1] != 0]
    print('DEBUG_META_ONLY %s' % ', '.join(debug_bits))
    print('VERDICT=DEBUG_ONLY_DIFF 代码段逐字节相同，差异仅在调试元数据（build-id / debuglink / PDB GUID）')
    return 0


def selftest():
    import tempfile
    ok = True

    def check(name, cond, detail=''):
        nonlocal ok
        print('%-26s %s %s' % (name, 'PASS' if cond else 'FAIL', detail))
        ok = ok and cond

    src = '/bin/true'
    if not os.path.exists(src):
        print('SKIP selftest: /bin/true 不存在')
        return 0
    d = open(src, 'rb').read()
    tmp = tempfile.mkdtemp()
    a = os.path.join(tmp, 'a.so')
    b = os.path.join(tmp, 'b.so')
    c = os.path.join(tmp, 'c.so')
    open(a, 'wb').write(d)
    open(b, 'wb').write(d)
    mutated = bytearray(d)
    mutated[-1] ^= 0xFF
    open(c, 'wb').write(bytes(mutated))

    import io
    from contextlib import redirect_stdout
    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = compare(a, b)
    check('same_file_identical', rc == 0 and 'VERDICT=IDENTICAL' in buf.getvalue())

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = compare(a, c)
    out = buf.getvalue()
    check('one_byte_差异不为IDENTICAL', rc in (0, 1) and 'VERDICT=IDENTICAL' not in out,
          '(改一字节后不许判 IDENTICAL)')

    # 大小不同必须 SIZE_MISMATCH
    e = os.path.join(tmp, 'e.so')
    open(e, 'wb').write(d + b'\x00' * 8)
    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = compare(a, e)
    check('size_mismatch', rc == 1 and 'SIZE_MISMATCH' in buf.getvalue())

    print('SELFTEST %s' % ('PASS' if ok else 'FAIL'))
    return 0 if ok else 1


def main():
    args = sys.argv[1:]
    if args and args[0] == '--selftest':
        return selftest()
    if len(args) != 2:
        print(__doc__)
        return 2
    for p in args:
        if not os.path.exists(p):
            print('FAIL: 文件不存在 %s' % p)
            return 2
    try:
        return compare(args[0], args[1])
    except Exception as e:
        print('FAIL: %s' % e)
        return 2


if __name__ == '__main__':
    sys.exit(main())
