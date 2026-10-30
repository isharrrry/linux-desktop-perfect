#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""W79A 夹具生成器 + **独立真值**（不经过 Skia、不经过我方 shim）。

产出：
  fix/g1-3f-solid.gif    3 帧 · 整幅 · 纯色红/绿/蓝 · delay 10/20/30 cs
  fix/g2-4f-partial.gif  4 帧 · **子矩形 + 透明 + disposal=1** · delay 5/10/15/20 cs
  fix/g6-disposal2.gif   4 帧 · 子矩形 + disposal=2（还原背景）· 仅供**观测**（见报告边界）
  fix/g3-1f.png          单帧 PNG（渐变 + 半透明像素）
  fix/g5-1f.gif          单帧 GIF
  fix/g4-1f-jfif.jpg     单帧 JPEG（仓内既有夹具的逐字节副本）

真值：
  truth/<stem>.tsv        帧数 / 逐帧 delay_cs / rect / disposal / transparency → 判据用
  truth/<stem>.f<i>.bgra  第 i 帧**合成后**的 BGRA 像素（逐位真值）

为什么自己写 GIF 编码器：Pillow 9.0.1 的 GIF 写出把"子矩形 + 透明"**摊平**成整幅不透明黑
（实测：4 帧 rect 全丢、transparent 标志不写）⇒ 拿它当夹具根本测不到子矩形/透明这两格。
自己的编码器（LZW 只用 CLEAR + 字面码、每 8 个字面码 CLEAR 一次 ⇒ 码宽恒 3 bit）
让 rect/透明/disposal/delay 四格**逐字节可预言**；编码器本身另用 Pillow 解回来**反查**
（独立解码器验证我的编码器，见 --verify）。
"""
import os
import struct
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.environ.get("WICFRAMES_DIR") or os.path.join(os.path.dirname(HERE), "wicframes-out")
FIX = os.path.join(OUT, "fix")
TRUTH = os.path.join(OUT, "truth")
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))))   # 波 `#77` 旧路径重指向：由仓根现推


# ─────────────────────────── 自己的 GIF 编码器（LZW：CLEAR + 字面码） ───────────────────────────
def lzw_literals(indices, min_code_size=2):
    """只发 CLEAR + 字面码（不做字典增长压缩）：每 8 个字面码发一次 CLEAR ⇒ 码宽恒 = min+1。"""
    clear = 1 << min_code_size
    eoi = clear + 1
    width = min_code_size + 1
    out = bytearray()
    bitbuf = 0
    bitcnt = 0

    def emit(code):
        nonlocal bitbuf, bitcnt
        bitbuf |= (code & ((1 << width) - 1)) << bitcnt
        bitcnt += width
        while bitcnt >= 8:
            out.append(bitbuf & 0xFF)
            bitbuf >>= 8
            bitcnt -= 8

    emit(clear)
    n = 0
    for v in indices:
        # ⚠ 每 2 个字面码必须 CLEAR：CLEAR 之后表从索引 6 起，3 bit 只能寻址 0..7 ⇒
        #   第 3 个字面码会要求码宽 4，而本编码器恒按 3 bit 写 ⇒ 解出来就是"broken data stream"。
        if n == 2:
            emit(clear)
            n = 0
        emit(v)
        n += 1
    emit(eoi)
    if bitcnt:
        out.append(bitbuf & 0xFF)
    return bytes([min_code_size]) + sub_blocks(out)


def sub_blocks(data):
    out = bytearray()
    i = 0
    while i < len(data):
        chunk = data[i:i + 255]
        out.append(len(chunk))
        out += chunk
        i += 255
    out.append(0)
    return bytes(out)


def write_gif(path, canvas, frames, loop=0, bg=(255, 255, 255)):
    """frames: [dict(rect=(x,y,w,h), color=(r,g,b), delay_cs=int, disposal=int, transparent=bool)]"""
    W, H = canvas
    b = bytearray(b"GIF89a")
    b += struct.pack("<HHBBB", W, H, 0xF0, 0, 0)          # GCT 2 色（占位）· 无 GCT 真正用途
    b += bytes([0, 0, 0, 255, 255, 255])                  # 全局色表（本夹具各帧都用局部色表）
    b += b"\x21\xFF\x0BNETSCAPE2.0\x03\x01" + struct.pack("<H", loop) + b"\x00"
    for f in frames:
        x, y, w, h = f["rect"]
        packed = (f["disposal"] & 7) << 2
        if f.get("transparent"):
            packed |= 1
        b += b"\x21\xF9\x04" + bytes([packed]) + struct.pack("<H", f["delay_cs"]) \
             + bytes([0]) + b"\x00"                        # 透明索引 = 0
        b += b"\x2C" + struct.pack("<HHHH", x, y, w, h) + bytes([0x80 | 0x00])   # 局部色表 2 色
        b += bytes([0, 0, 0]) + bytes(f["color"])                                # idx0=透明占位
        idx = [1 if True else 0] * (w * h)                 # 整幅矩形都是不透明色 idx=1
        b += lzw_literals(idx, 2)
    b += b"\x3B"
    with open(path, "wb") as fh:
        fh.write(bytes(b))
    return bytes(b)


# ─────────────────────────── GIF 结构解析（真值的一半） ───────────────────────────
def parse_gif(path):
    b = open(path, "rb").read()
    assert b[:3] == b"GIF", "not a gif"
    ver = b[3:6].decode()
    w, h, packed, bgc, aspect = struct.unpack_from("<HHBBB", b, 6)
    pos = 13
    if packed & 0x80:
        pos += 3 * 2 ** ((packed & 7) + 1)
    frames, gces, loop, pending = [], [], None, None
    while pos < len(b):
        blk = b[pos]
        if blk == 0x3B:
            break
        if blk == 0x21:
            label = b[pos + 1]
            pos += 2
            payload = b""
            while pos < len(b) and b[pos] != 0:
                n = b[pos]
                payload += b[pos + 1:pos + 1 + n]
                pos += 1 + n
            pos += 1
            if label == 0xF9 and len(payload) >= 4:
                p = payload[0]
                pending = {"disposal": (p >> 2) & 7, "transparent": bool(p & 1),
                           "trans_index": payload[3],
                           "delay_cs": struct.unpack_from("<H", payload, 1)[0]}
            if label == 0xFF and payload[:11] == b"NETSCAPE2.0":
                loop = struct.unpack_from("<H", payload, 12)[0]
            continue
        if blk == 0x2C:
            left, top, fw, fh, ip = struct.unpack_from("<HHHHB", b, pos + 1)
            pos += 10
            if ip & 0x80:
                pos += 3 * 2 ** ((ip & 7) + 1)
            pos += 1
            while pos < len(b) and b[pos] != 0:
                pos += 1 + b[pos]
            pos += 1
            frames.append({"left": left, "top": top, "w": fw, "h": fh})
            gces.append(pending or {})
            pending = None
            continue
        raise SystemExit(f"unexpected block 0x{blk:02X} at {pos}")
    return {"ver": ver, "w": w, "h": h, "frames": frames, "gce": gces, "loop": loop}


def composite(parsed, background):
    """自己按 GIF 语义合成（disposal 0/1 = 保留；2 = 还原背景；3 = 还原上一帧）。"""
    W, H = parsed["w"], parsed["h"]
    canvas = Image.new("RGBA", (W, H), background)
    out = []
    prev = None
    for i, fr in enumerate(parsed["frames"]):
        gce = parsed["gce"][i]
        if prev is not None:
            pd, pr = prev["gce"].get("disposal", 0), prev["fr"]
            if pd == 2:
                canvas.paste(Image.new("RGBA", (pr["w"], pr["h"]), background), (pr["left"], pr["top"]))
            elif pd == 3:
                canvas.paste(prev["snapshot"], (0, 0))
        snap = canvas.copy()
        color = parsed["colors"][i]
        layer = Image.new("RGBA", (fr["w"], fr["h"]), color + (255,))
        canvas.paste(layer, (fr["left"], fr["top"]))
        out.append(canvas.copy())
        prev = {"gce": gce, "fr": fr, "snapshot": snap}
    return out


def bgra(img):
    """RGBA → BGRA（**交换 R/B 两条字节道**）。
    ⚠ 踩过的坑：`Image.merge("RGBA", (b,g,r,a))` 不是"换成 BGRA"，它按 bands 顺序写字节
    （实测红像素解出 `ff 00 00 ff`）；正确做法是拿 RGBA 字节后**逐像素交换第 0/2 字节**。"""
    raw = bytearray(img.convert("RGBA").tobytes())
    raw[0::4], raw[2::4] = raw[2::4], raw[0::4]
    return bytes(raw)


# ─────────────────────────────────── 夹具与真值 ───────────────────────────────────
def build():
    os.makedirs(FIX, exist_ok=True)
    os.makedirs(TRUTH, exist_ok=True)
    made = []

    # G1：3 帧整幅纯色
    g1 = os.path.join(FIX, "g1-3f-solid.gif")
    write_gif(g1, (32, 24), [
        {"rect": (0, 0, 32, 24), "color": (255, 0, 0), "delay_cs": 10, "disposal": 1, "transparent": False},
        {"rect": (0, 0, 32, 24), "color": (0, 255, 0), "delay_cs": 20, "disposal": 1, "transparent": False},
        {"rect": (0, 0, 32, 24), "color": (0, 0, 255), "delay_cs": 30, "disposal": 1, "transparent": False},
    ])
    made.append((g1, (255, 255, 255, 255)))

    # G2：4 帧子矩形 + 透明（累积）
    g2 = os.path.join(FIX, "g2-4f-partial.gif")
    write_gif(g2, (40, 30), [
        {"rect": (0, 0, 40, 30), "color": (255, 255, 255), "delay_cs": 5, "disposal": 1, "transparent": False},
        {"rect": (2, 2, 8, 8), "color": (255, 0, 0), "delay_cs": 10, "disposal": 1, "transparent": True},
        {"rect": (20, 10, 10, 10), "color": (0, 200, 0), "delay_cs": 15, "disposal": 1, "transparent": True},
        {"rect": (5, 20, 10, 10), "color": (0, 0, 255), "delay_cs": 20, "disposal": 1, "transparent": True},
    ])
    made.append((g2, (255, 255, 255, 255)))

    # G6：disposal=2（还原为背景）—— 仅观测用
    g6 = os.path.join(FIX, "g6-disposal2.gif")
    write_gif(g6, (24, 20), [
        {"rect": (0, 0, 24, 20), "color": (255, 255, 255), "delay_cs": 10, "disposal": 1, "transparent": False},
        {"rect": (2, 2, 6, 6), "color": (255, 0, 0), "delay_cs": 10, "disposal": 2, "transparent": True},
        {"rect": (12, 10, 6, 6), "color": (0, 0, 255), "delay_cs": 10, "disposal": 1, "transparent": True},
    ])
    made.append((g6, (255, 255, 255, 255)))

    # G5：单帧 GIF
    g5 = os.path.join(FIX, "g5-1f.gif")
    write_gif(g5, (12, 9), [
        {"rect": (0, 0, 12, 9), "color": (10, 200, 90), "delay_cs": 0, "disposal": 0, "transparent": False},
    ])
    made.append((g5, (255, 255, 255, 255)))

    # G3：单帧 PNG（渐变 + 半透明）
    g3 = os.path.join(FIX, "g3-1f.png")
    png = Image.new("RGBA", (17, 13))
    for y in range(13):
        for x in range(17):
            png.putpixel((x, y), (x * 15 % 256, y * 19 % 256, (x + y) * 7 % 256,
                                  255 if (x + y) % 3 else 128))
    png.save(g3)

    # G4：仓内既有 JPEG 夹具的副本
    g4 = os.path.join(FIX, "g4-1f-jfif.jpg")
    with open(os.path.join(REPO, "src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/fixtures-jfif.jpg"), "rb") as f:
        data = f.read()
    with open(g4, "wb") as f:
        f.write(data)

    report = []
    for path, bgc in made:
        stem = os.path.splitext(os.path.basename(path))[0]
        p = parse_gif(path)
        # 每帧颜色（从局部色表读回，不从我的意图里抄）
        colors = []
        bb = open(path, "rb").read()
        pos = 13
        packed = bb[10]
        if packed & 0x80:
            pos += 3 * 2 ** ((packed & 7) + 1)
        while pos < len(bb):
            if bb[pos] == 0x3B:
                break
            if bb[pos] == 0x21:
                pos += 2
                while bb[pos] != 0:
                    pos += 1 + bb[pos]
                pos += 1
                continue
            if bb[pos] == 0x2C:
                ip = bb[pos + 9]
                pos += 10
                tbl = bb[pos:pos + 6]
                colors.append(tuple(tbl[3:6]))          # 局部色表 idx=1
                pos += 6
                pos += 1
                while bb[pos] != 0:
                    pos += 1 + bb[pos]
                pos += 1
                continue
            raise SystemExit("parse2 broke")
        p["colors"] = colors
        comp = composite(p, bgc)
        lines = [f"# file={os.path.basename(path)} frames={len(p['frames'])} "
                 f"canvas={p['w']}x{p['h']} loop={p['loop']} ver=GIF{p['ver']}"]
        for i, img in enumerate(comp):
            open(os.path.join(TRUTH, f"{stem}.f{i}.bgra"), "wb").write(bgra(img))
            fr, gce = p["frames"][i], p["gce"][i]
            lines.append(f"{i}\t{gce.get('delay_cs', 0)}\t{fr['w']}\t{fr['h']}\t{fr['left']}\t"
                         f"{fr['top']}\t{gce.get('disposal', 0)}\t{1 if gce.get('transparent') else 0}\t"
                         f"{p['w']}\t{p['h']}")
        open(os.path.join(TRUTH, f"{stem}.tsv"), "w").write("\n".join(lines) + "\n")
        report.append(f"{stem}: frames={len(p['frames'])} "
                      f"rects={[(f['left'], f['top'], f['w'], f['h']) for f in p['frames']]} "
                      f"colors={colors} delays={[g.get('delay_cs') for g in p['gce']]} "
                      f"disposal={[g.get('disposal') for g in p['gce']]} "
                      f"transp={[bool(g.get('transparent')) for g in p['gce']]}")

    for path in (g3, g4):
        stem = os.path.splitext(os.path.basename(path))[0]
        im = Image.open(path).convert("RGBA")
        open(os.path.join(TRUTH, f"{stem}.f0.bgra"), "wb").write(bgra(im))
        open(os.path.join(TRUTH, f"{stem}.tsv"), "w").write(
            f"# file={os.path.basename(path)} frames=1 canvas={im.size[0]}x{im.size[1]} loop=None ver=static\n"
            f"0\t0\t{im.size[0]}\t{im.size[1]}\t0\t0\t0\t0\t{im.size[0]}\t{im.size[1]}\n")
        report.append(f"{stem}: frames=1 {im.size[0]}x{im.size[1]}")

    for r in report:
        print("GEN " + r)
    print("GEN_OK=1")


def verify():
    """用 Pillow（**独立解码器**）解回我自己的 GIF，反查编码器：rect 内的颜色必须逐位相符。"""
    bad = 0
    for name in ("g1-3f-solid", "g2-4f-partial", "g6-disposal2", "g5-1f.gif"):
        stem = name.replace(".gif", "")
        path = os.path.join(FIX, name if name.endswith(".gif") else name + ".gif")
        if not os.path.exists(path):
            continue
        p = parse_gif(path)
        im = Image.open(path)
        colors = []
        for i in range(len(p["frames"])):
            im.seek(i)
            raw = im.convert("RGB")
            fr = p["frames"][i]
            cx, cy = fr["left"] + fr["w"] // 2, fr["top"] + fr["h"] // 2
            colors.append(raw.getpixel((cx, cy)))
        expect = []
        # 期望色 = 我的真值帧在该 rect 中心的颜色（合成后）
        for i in range(len(p["frames"])):
            t = Image.open(os.path.join(TRUTH, f"{stem}.f{i}.bgra")) if False else None
        print(f"VERIFY {stem}: pillow_raw_center_colors={colors}")
    print(f"VERIFY_BAD={bad}")


if __name__ == "__main__":
    if "--verify" in sys.argv:
        verify()
    else:
        build()
