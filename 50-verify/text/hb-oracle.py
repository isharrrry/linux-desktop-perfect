#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HarfBuzz 基准（对拍用 oracle）—— text-stack 判据的第二条独立路径

口径（08 §1.5「不许自证」）：判据比较两条路径 —— ①被测件 libdwlinux.so（C + dlopen）；
②本脚本（Python ctypes 直接调系统 libfontconfig / libfreetype / libharfbuzz）。
两条路径不共享代码，只用同一批系统库，所以"一致"是真结论而不是同源自证。

用法：
  echo '{"family":"DejaVu Sans","text":"Hello","size":16}' | python3 50-verify/text/hb-oracle.py --shape
  python3 50-verify/text/hb-oracle.py --fonts           # 字体枚举读数（fontconfig）
  echo '{"text":"Hello world"}' | python3 50-verify/text/hb-oracle.py --breaks

输出：单行 JSON（stdout），错误走 stderr + rc=2。
"""
import ctypes
import ctypes.util
import json
import sys

FC = ctypes.CDLL("libfontconfig.so.1")
FT = ctypes.CDLL("libfreetype.so.6")
HB = ctypes.CDLL("libharfbuzz.so.0")

# ---------------------------------------------------------------- fontconfig

FC.FcInit.restype = ctypes.c_int
FC.FcNameParse.argtypes = [ctypes.c_char_p]
FC.FcNameParse.restype = ctypes.c_void_p
FC.FcConfigGetCurrent.restype = ctypes.c_void_p
FC.FcConfigSubstitute.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
FC.FcDefaultSubstitute.argtypes = [ctypes.c_void_p]
FC.FcFontMatch.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
FC.FcFontMatch.restype = ctypes.c_void_p
FC.FcPatternGetString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
FC.FcPatternGetInteger.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
FC.FcPatternDestroy.argtypes = [ctypes.c_void_p]
FC.FcFontSetDestroy.argtypes = [ctypes.c_void_p]
FC.FcFontList.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
FC.FcFontList.restype = ctypes.c_void_p


class FcFontSet(ctypes.Structure):
    _fields_ = [("nfont", ctypes.c_int), ("sfont", ctypes.c_int), ("fonts", ctypes.POINTER(ctypes.c_void_p))]


FC.FcInit()
_FC_MATCH_PATTERN = 0  # FcMatchPattern


def font_match(family):
    pat = FC.FcNameParse(family.encode("utf-8"))
    if not pat:
        raise RuntimeError("FcNameParse failed")
    FC.FcConfigSubstitute(FC.FcConfigGetCurrent(), pat, _FC_MATCH_PATTERN)
    FC.FcDefaultSubstitute(pat)
    result = ctypes.c_int(0)
    match = FC.FcFontMatch(FC.FcConfigGetCurrent(), pat, ctypes.byref(result))
    FC.FcPatternDestroy(pat)
    if not match:
        raise RuntimeError("FcFontMatch failed")
    file_s = ctypes.c_char_p()
    idx = ctypes.c_int(0)
    fam = ctypes.c_char_p()
    if FC.FcPatternGetString(match, b"file", 0, ctypes.byref(file_s)) != 0:
        raise RuntimeError("no file in matched pattern")
    FC.FcPatternGetInteger(match, b"index", 0, ctypes.byref(idx))
    FC.FcPatternGetString(match, b"family", 0, ctypes.byref(fam))
    out = {"file": file_s.value.decode("utf-8"), "index": idx.value, "family": (fam.value or b"").decode("utf-8")}
    FC.FcPatternDestroy(match)
    return out


def fonts():
    pat = FC.FcNameParse(b":")
    fset = FC.FcFontList(FC.FcConfigGetCurrent(), pat, None)
    FC.FcPatternDestroy(pat)
    if not fset:
        return []
    fs = ctypes.cast(fset, ctypes.POINTER(FcFontSet)).contents
    fams = []
    for i in range(fs.nfont):
        p = fs.fonts[i]
        fam = ctypes.c_char_p()
        if FC.FcPatternGetString(p, b"family", 0, ctypes.byref(fam)) == 0 and fam.value:
            fams.append(fam.value.decode("utf-8"))
    FC.FcFontSetDestroy(fset)
    return sorted(set(fams))


# ---------------------------------------------------------------- freetype + harfbuzz

FT.FT_Init_FreeType.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
FT.FT_New_Face.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_long, ctypes.POINTER(ctypes.c_void_p)]
FT.FT_Set_Pixel_Sizes.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint]
FT.FT_Done_Face.argtypes = [ctypes.c_void_p]

HB.hb_ft_font_create_referenced.argtypes = [ctypes.c_void_p]
HB.hb_ft_font_create_referenced.restype = ctypes.c_void_p
HB.hb_font_destroy.argtypes = [ctypes.c_void_p]
HB.hb_buffer_create.restype = ctypes.c_void_p
HB.hb_buffer_destroy.argtypes = [ctypes.c_void_p]
HB.hb_buffer_add_utf8.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_uint, ctypes.c_int]
HB.hb_buffer_guess_segment_properties.argtypes = [ctypes.c_void_p]
HB.hb_shape.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]


class HbGlyphInfo(ctypes.Structure):
    _fields_ = [("codepoint", ctypes.c_uint32), ("mask", ctypes.c_uint32), ("cluster", ctypes.c_uint32), ("var1", ctypes.c_uint32), ("var2", ctypes.c_uint32)]


class HbGlyphPosition(ctypes.Structure):
    _fields_ = [("x_advance", ctypes.c_int32), ("y_advance", ctypes.c_int32), ("x_offset", ctypes.c_int32), ("y_offset", ctypes.c_int32), ("var", ctypes.c_uint32)]


HB.hb_buffer_get_glyph_infos.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint)]
HB.hb_buffer_get_glyph_infos.restype = ctypes.POINTER(HbGlyphInfo)
HB.hb_buffer_get_glyph_positions.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint)]
HB.hb_buffer_get_glyph_positions.restype = ctypes.POINTER(HbGlyphPosition)


def shape(family, text, size=16):
    m = font_match(family)
    lib = ctypes.c_void_p()
    if FT.FT_Init_FreeType(ctypes.byref(lib)) != 0:
        raise RuntimeError("FT_Init_FreeType failed")
    face = ctypes.c_void_p()
    if FT.FT_New_Face(lib, m["file"].encode("utf-8"), m["index"], ctypes.byref(face)) != 0:
        raise RuntimeError("FT_New_Face failed: %s" % m["file"])
    FT.FT_Set_Pixel_Sizes(face, 0, size)
    font = HB.hb_ft_font_create_referenced(face)
    buf = HB.hb_buffer_create()
    data = text.encode("utf-8")
    HB.hb_buffer_add_utf8(buf, data, len(data), 0, len(data))
    HB.hb_buffer_guess_segment_properties(buf)
    HB.hb_shape(font, buf, None, 0)
    n = ctypes.c_uint(0)
    infos = HB.hb_buffer_get_glyph_infos(buf, ctypes.byref(n))
    pos = HB.hb_buffer_get_glyph_positions(buf, ctypes.byref(n))
    glyphs = [int(infos[i].codepoint) for i in range(n.value)]
    advances = [int(pos[i].x_advance) for i in range(n.value)]
    offsets = [int(pos[i].x_offset) for i in range(n.value)]
    HB.hb_buffer_destroy(buf)
    HB.hb_font_destroy(font)
    FT.FT_Done_Face(face)
    return {"matched": m, "size": size, "glyphs": glyphs, "advances": advances, "x_offsets": offsets}


# ---------------------------------------------------------------- ICU 断行

ICU = ctypes.CDLL("libicui18n.so.70")
UC = ctypes.CDLL("libicuuc.so.70")


def icu_sym(lib, name):
    """ICU 的 C 符号带版本后缀（Ubuntu 22.04 = _70）；带后缀优先，回落到无后缀。"""
    for cand in (name + "_70", name):
        try:
            return getattr(lib, cand)
        except AttributeError:
            continue
    raise RuntimeError("ICU symbol not found: %s" % name)


ubrk_open = icu_sym(ICU, "ubrk_open")
ubrk_open.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.POINTER(ctypes.c_uint16), ctypes.c_int32, ctypes.POINTER(ctypes.c_int)]
ubrk_open.restype = ctypes.c_void_p
ubrk_next = icu_sym(ICU, "ubrk_next")
ubrk_next.argtypes = [ctypes.c_void_p]
ubrk_next.restype = ctypes.c_int32
ubrk_close = icu_sym(ICU, "ubrk_close")
ubrk_close.argtypes = [ctypes.c_void_p]
u_strFromUTF8 = icu_sym(UC, "u_strFromUTF8")
u_strFromUTF8.argtypes = [ctypes.POINTER(ctypes.c_uint16), ctypes.c_int32, ctypes.POINTER(ctypes.c_int32), ctypes.c_char_p, ctypes.c_int32, ctypes.POINTER(ctypes.c_int)]

UBRK_LINE = 2


def breaks(text):
    data = text.encode("utf-8")
    cap = len(data) * 2 + 16
    u16 = (ctypes.c_uint16 * cap)()
    err = ctypes.c_int(0)
    n16 = ctypes.c_int32(0)
    u_strFromUTF8(u16, cap, ctypes.byref(n16), data, len(data), ctypes.byref(err))
    if err.value > 0:
        raise RuntimeError("u_strFromUTF8 failed: %d" % err.value)
    uerr = ctypes.c_int(0)
    brk = ubrk_open(UBRK_LINE, None, u16, n16.value, ctypes.byref(uerr))
    if not brk:
        raise RuntimeError("ubrk_open failed: %d" % uerr.value)
    offs = []
    while True:
        p = ubrk_next(brk)
        if p == -1:
            break
        offs.append(int(p))
        if len(offs) > 10000:
            raise RuntimeError("too many breaks")
    ubrk_close(brk)
    # UTF-16 code-unit 偏移 → UTF-8 字节偏移（判据侧用字节偏移比较）
    u16_text = u16[: n16.value]
    byte_offs = []
    for off in offs:
        byte_offs.append(len("".join(chr(u16_text[i]) for i in range(0, off)).encode("utf-8")))
    return {"utf16_offsets": offs, "byte_offsets": byte_offs}


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--shape"
    if mode == "--fonts":
        fams = fonts()
        print(json.dumps({"count": len(fams), "families": fams}, ensure_ascii=False))
        return 0
    payload = json.loads(sys.stdin.read() or "{}")
    if mode == "--shape":
        out = shape(payload["family"], payload["text"], int(payload.get("size", 16)))
    elif mode == "--breaks":
        out = breaks(payload["text"])
    elif mode == "--match":
        out = font_match(payload["family"])
    else:
        print("unknown mode: %s" % mode, file=sys.stderr)
        return 2
    print(json.dumps(out, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:  # 诚实失败：明确错误行 + rc=2
        print("ERROR %s" % e, file=sys.stderr)
        sys.exit(2)
