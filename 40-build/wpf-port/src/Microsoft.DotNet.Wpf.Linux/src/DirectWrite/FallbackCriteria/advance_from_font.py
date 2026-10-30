#!/usr/bin/env python3
"""独立复算：**自己读字体文件**（不信任何 API 的自述）—— 供 D-F1 判据的 C1 各腿使用。

用法：advance_from_font.py <fontfile> <glyphIndex> <emSize> [--cp=U+XXXX]
      （`--cp` 给了 ⇒ **自己走 cmap 取 gid**，忽略实参里的 glyphIndex）
      `.ttc` 的面下标由环境变量 `FACE_INDEX` 给（默认 0）。

输出（顺序固定，机读；每行都可回源到具体表）：
  # face=<path> FACE_INDEX=<n> NUMGLYPHS=<maxp.numGlyphs> UPEM=<head.unitsPerEm>
  # CMAP cp=U+XXXX gid=<n> HAS=<true|false>          ← 仅 --cp 时（HAS=false ⇒ gid 0）
  # WARN gid=<n> >= NUMGLYPHS=<n>（该面装不下这个字形号）   ← 仅越界时
  ADV=<v> UPEM=<n> ADV_UNITS=<n> FACE_INDEX=<n>     ← 或 `ADV=NA reason=…`
退出码：0=给出 ADV；4=该面没有该码点（ADV=NA）；2=面下标越界；1=用法错
"""
import os, struct, sys

if len(sys.argv) < 4:
    print("ADV=NA reason=usage"); sys.exit(1)
path = sys.argv[1]
argv = sys.argv[2:]
cp = None
rest = []
for a in argv:
    if a.startswith("--cp="): cp = int(a[5:].replace("U+", "").replace("0x", ""), 16)
    else: rest.append(a)
gid = int(rest[0]); em = float(rest[1])
CP = cp
face = int(os.environ.get("FACE_INDEX", "0"))
try:
    d = open(path, 'rb').read()
except OSError as e:
    print(f"ADV=NA reason=cannot-read:{e.__class__.__name__}"); sys.exit(2)
off = 0
if d[:4] == b'ttcf':
    n = struct.unpack('>I', d[8:12])[0]
    if face >= n: print(f"ADV=NA reason=face-out-of-range(n={n})"); sys.exit(2)
    off = struct.unpack('>I', d[12 + 4 * face:16 + 4 * face])[0]
num = struct.unpack('>H', d[off + 4:off + 6])[0]
tabs = {}
for i in range(num):
    o = off + 12 + 16 * i
    tag = d[o:o + 4].decode('latin1'); o2, l = struct.unpack('>II', d[o + 8:o + 16]); tabs[tag] = (o2, l)
upem = struct.unpack('>H', d[tabs['head'][0] + 18:tabs['head'][0] + 20])[0]
numglyphs = struct.unpack('>H', d[tabs['maxp'][0] + 4:tabs['maxp'][0] + 6])[0]
nh = struct.unpack('>H', d[tabs['hhea'][0] + 34:tabs['hhea'][0] + 36])[0]
print(f"# face={path} FACE_INDEX={face} NUMGLYPHS={numglyphs} UPEM={upem}")
# --cp 给了 ⇒ 自己从 cmap 取 gid（**不信任任何 API 的说法**）
if CP is not None:
    cm = tabs['cmap'][0]; n = struct.unpack('>H', d[cm + 2:cm + 4])[0]; gid = 0
    for i in range(n):
        pid, eid, off2 = struct.unpack('>HHI', d[cm + 4 + 8 * i:cm + 4 + 8 * i + 8])
        if (pid, eid) not in ((3, 1), (3, 10)): continue
        fmt = struct.unpack('>H', d[cm + off2:cm + off2 + 2])[0]
        if fmt == 12:
            ng = struct.unpack('>I', d[cm + off2 + 12:cm + off2 + 16])[0]
            for k2 in range(ng):
                s0, e0, g0 = struct.unpack('>III', d[cm + off2 + 16 + 12 * k2:cm + off2 + 28 + 12 * k2])
                if s0 <= CP <= e0: gid = g0 + (CP - s0); break
        elif fmt == 4:
            segX2 = struct.unpack('>H', d[cm + off2 + 6:cm + off2 + 8])[0]; seg = segX2 // 2
            E = [struct.unpack('>H', d[cm + off2 + 14 + 2 * j:cm + off2 + 16 + 2 * j])[0] for j in range(seg)]
            Sb = cm + off2 + 16 + segX2; S = [struct.unpack('>H', d[Sb + 2 * j:Sb + 2 * j + 2])[0] for j in range(seg)]
            Db = Sb + segX2; D = [struct.unpack('>h', d[Db + 2 * j:Db + 2 * j + 2])[0] for j in range(seg)]
            Rb = Db + segX2; R = [struct.unpack('>H', d[Rb + 2 * j:Rb + 2 * j + 2])[0] for j in range(seg)]
            for j in range(seg):
                if CP <= E[j]:
                    if CP < S[j]: gid = 0
                    elif R[j] == 0: gid = (CP + D[j]) & 0xFFFF
                    else:
                        a2 = Rb + 2 * j + R[j] + 2 * (CP - S[j]); g2 = struct.unpack('>H', d[a2:a2 + 2])[0]
                        gid = ((g2 + D[j]) & 0xFFFF) if g2 else 0
                    break
        if gid: break
    print(f"# CMAP cp=U+{CP:04X} gid={gid} HAS={'true' if gid else 'false'}")
    if numglyphs and gid >= numglyphs:
        print(f"# WARN gid={gid} >= NUMGLYPHS={numglyphs}（该面装不下这个字形号）")
    if gid == 0:
        print("ADV=NA reason=cp-not-covered（该面没有这个码点 ⇒ .notdef）"); sys.exit(4)
if numglyphs and gid >= numglyphs:
    print(f"# WARN gid={gid} >= NUMGLYPHS={numglyphs}（该面装不下这个字形号）")
k = min(gid, nh - 1)
units = struct.unpack('>H', d[tabs['hmtx'][0] + 4 * k:tabs['hmtx'][0] + 4 * k + 2])[0]
print(f"ADV={units / upem * em:.4f} UPEM={upem} ADV_UNITS={units} FACE_INDEX={face}")
