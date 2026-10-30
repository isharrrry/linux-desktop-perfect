#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c #U · 生成 `MILGetClassificationTables` 需要的 Unicode 分类表。

    python3 src/WpfGfx.Linux.Native/tools/gen-unicode-tables.py            # 生成 .c
    python3 src/WpfGfx.Linux.Native/tools/gen-unicode-tables.py --stats     # 只打统计

【为什么必须"自己造数据"】
  `MILGetClassificationTables` 属于 `PresentationNative_cor3.dll`（原生 WPF 的
  PresentationNative 工程）。**上游快照里没有这个工程的源码**（`src/Microsoft.DotNet.Wpf/src/`
  下无 `PresentationNative` 目录）⇒ 这不是"移植"，只能自己把数据造出来。
  托管侧只给了**契约**：`PresentationCore/MS/internal/Classification.cs:186-199`
  的 `RawClassificationTables`（3 个表指针 + 1 个组合字符合并结构）与
  `MS/internal/UnicodeClasses.cs` 的**枚举语义**（`ItemClass`/`ScriptID`/
  `DirectionClass`/`CharacterAttributeFlags`/`CharacterAttribute` 结构）。

【数据从哪来（这一条决定它是"真实现"还是"编出来的"）】
  用**本机 Python 3 的 `unicodedata`**（实测 `unidata_version = 13.0.0`）——
  这是一份**真实的 UCD 视图**，不是手写常量：
    · `category(c)`      → 通用类别（Lu/Nd/Mn/Cf/…）      → ItemClass / Flags
    · `bidirectional(c)` → **Bidi_Class**（L/R/AL/AN/EN/…）→ `DirectionClass` + `CharacterRTL`
    · `combining(c)`     → 规范组合类（ccc）              → 是否组合标记
    · `mirrored(c)`      → Bidi_Mirrored                  → `ScriptID.Mirror`
    · `name(c)`          → 系统性字符名                   → **脚本**（"ARABIC LETTER…" ⇒ Arabic）
  脚本没有直接的 API，用**名字前缀**推：UCD 的名字对绝大多数文字是系统性的
  （`DEVANAGARI LETTER A`、`GREEK SMALL LETTER ALPHA`、`CJK UNIFIED IDEOGRAPH-4E2D`…）。
  认不出来的脚本一律 `ScriptID.Default` —— **宁可默认，不猜**。

【托管侧真正读的是哪两张表（决定本文件的正确性下限）】
  `Classification` 里被外部读到的只有两处：
    · `UnicodeClasses`  ← `GetUnicodeClassUTF16/GetUnicodeClass`
    · `CharacterAttributes` ← `CharAttributeOf(class)`（索引是**类值**，不是码点）
  其余两个（`Mirroring`、`CombiningMarksClassification`）在整棵树里
  **没有任何消费者**（`grep -rn CombiningMarksClassification|_mirroredCharTable` 零命中），
  所以对它们采取"结构有效 + 明确降级"的口径（见下面 MIRRORING / COMBINING 两段注释）。

【两级表的编码约定（照抄托管侧的读法，不能自创）】
  `Classification.cs:219-236`：
      short **plane0 = UnicodeClassTable[0];            // 平面 0（BMP）
      short  *pcc   = plane0[cp >> 8];                  // 第二级：256 个字节块
      return (long)pcc < (long)UnicodeClass.Max         // 472
             ? (short)pcc                               // ★ 值直接编码在指针里
             : pcc[cp & 0xFF];
  即**第二级条目要么是小整数（该 256 块整块同属这一类），要么是 `short[256]` 的指针**。
  标量版（`GetUnicodeClass(int)`）在**两级上都**用这个小整数技巧，并且平面索引是
  `((scalar >> 16) & 0xFF) % 17` ⇒ 表必须有 **17 个平面**（U+10FFFF 需要 17 个 64K 平面）。
"""

import argparse
import os
import sys
import unicodedata as u

HERE = os.path.dirname(os.path.abspath(__file__))
NATIVE_ROOT = os.path.normpath(os.path.join(HERE, ".."))
OUT_C = os.path.join(NATIVE_ROOT, "src", "win32_unicode_tables.c")

MAX_CP = 0x10FFFF
UNICODE_CLASS_MAX = 0x1D8          # 托管的 `UnicodeClass.Max`；类值必须 < 它

# ── 枚举值（照抄 MS/internal/UnicodeClasses.cs，勿改）───────────────────────
ITEM = {
    "Digit": 0x0, "AN": 0x1, "CS": 0x2, "ES": 0x3, "ET": 0x4, "Strong": 0x5,
    "Weak": 0x6, "SimpleMark": 0x7, "ComplexMark": 0x8, "Control": 0x9,
    "Joiner": 0xA, "NumberSign": 0xB,
}
SCRIPT = {
    "Default": 0x0, "Arabic": 0x1, "Armenian": 0x2, "Bengali": 0x3, "Bopomofo": 0x4,
    "Braille": 0x5, "Buginese": 0x6, "Buhid": 0x7, "CanadianSyllabics": 0x8,
    "Cherokee": 0x9, "CJKIdeographic": 0xA, "Coptic": 0xB, "CypriotSyllabary": 0xC,
    "Cyrillic": 0xD, "Deseret": 0xE, "Devanagari": 0xF, "Ethiopic": 0x10,
    "Georgian": 0x11, "Glagolitic": 0x12, "Gothic": 0x13, "Greek": 0x14,
    "Gujarati": 0x15, "Gurmukhi": 0x16, "Hangul": 0x17, "Hanunoo": 0x18,
    "Hebrew": 0x19, "Kannada": 0x1A, "Kana": 0x1B, "Kharoshthi": 0x1C,
    "Khmer": 0x1D, "Lao": 0x1E, "Latin": 0x1F, "Limbu": 0x20, "LinearB": 0x21,
    "Malayalam": 0x22, "MathematicalAlphanumericSymbols": 0x23, "Mongolian": 0x24,
    "MusicalSymbols": 0x25, "Myanmar": 0x26, "NewTaiLue": 0x27, "Ogham": 0x28,
    "OldItalic": 0x29, "OldPersianCuneiform": 0x2A, "Oriya": 0x2B, "Osmanya": 0x2C,
    "Runic": 0x2D, "Shavian": 0x2E, "Sinhala": 0x2F, "SylotiNagri": 0x30,
    "Syriac": 0x31, "Tagalog": 0x32, "Tagbanwa": 0x33, "TaiLe": 0x34, "Tamil": 0x35,
    "Telugu": 0x36, "Thaana": 0x37, "Thai": 0x38, "Tibetan": 0x39, "Tifinagh": 0x3A,
    "UgariticCuneiform": 0x3B, "Yi": 0x3C, "Digit": 0x3D, "Control": 0x3E, "Mirror": 0x3F,
}
BIDI = {
    "L": 0, "R": 1, "AN": 2, "EN": 3, "AL": 4, "ES": 5, "CS": 6, "ET": 7,
    "NSM": 8, "BN": 9, "GN": 10, "B": 11, "LRE": 12, "LRO": 13, "RLE": 14,
    "RLO": 15, "PDF": 16, "S": 17, "WS": 18, "ON": 19,
    # Unicode 6.3 的"隔离"类在 DirectionClass 里**没有对应成员**（该枚举早于它们）。
    # 归到语义最近的 embedding/override（合成，见文件头说明）。
    "LRI": 12, "RLI": 14, "FSI": 12, "PDI": 16,
}
FLAG = {
    "Complex": 0x1, "RTL": 0x2, "LineBreak": 0x4, "FormatAnchor": 0x8,
    "FastText": 0x10, "Ideo": 0x20, "Extended": 0x40, "Space": 0x80,
    "Digit": 0x100, "ParaBreak": 0x200, "CRLF": 0x400, "Letter": 0x800,
}
BREAK = {"NoBreak": 0x0, "ControlBreak": 0x1, "DigitBreak": 0x2,
         "PairMirror": 0x4, "SingleMirror": 0x8}

# ── 名字前缀 → 脚本 ────────────────────────────────────────────────────────
# 只列 WPF `ScriptID` 枚举里真实存在的脚本；认不出的落 Default。
NAME_TO_SCRIPT = [
    ("LATIN", "Latin"), ("GREEK", "Greek"), ("COPTIC", "Coptic"), ("CYRILLIC", "Cyrillic"),
    ("ARMENIAN", "Armenian"), ("HEBREW", "Hebrew"), ("ARABIC", "Arabic"),
    ("SYRIAC", "Syriac"), ("THAANA", "Thaana"), ("DEVANAGARI", "Devanagari"),
    ("BENGALI", "Bengali"), ("GURMUKHI", "Gurmukhi"), ("GUJARATI", "Gujarati"),
    ("ORIYA", "Oriya"), ("TAMIL", "Tamil"), ("TELUGU", "Telugu"), ("KANNADA", "Kannada"),
    ("MALAYALAM", "Malayalam"), ("SINHALA", "Sinhala"), ("THAI", "Thai"), ("LAO", "Lao"),
    ("TIBETAN", "Tibetan"), ("MYANMAR", "Myanmar"), ("GEORGIAN", "Georgian"),
    ("HANGUL", "Hangul"), ("ETHIOPIC", "Ethiopic"), ("CHEROKEE", "Cherokee"),
    ("CANADIAN SYLLABICS", "CanadianSyllabics"), ("OGHAM", "Ogham"), ("RUNIC", "Runic"),
    ("TAGALOG", "Tagalog"), ("HANUNOO", "Hanunoo"), ("BUHID", "Buhid"),
    ("TAGBANWA", "Tagbanwa"), ("KHMER", "Khmer"), ("MONGOLIAN", "Mongolian"),
    ("LIMBU", "Limbu"), ("TAI LE", "TaiLe"), ("NEW TAI LUE", "NewTaiLue"),
    ("BUGINESE", "Buginese"), ("SYLOTI NAGRI", "SylotiNagri"), ("TIFINAGH", "Tifinagh"),
    ("OSMANYA", "Osmanya"), ("SHAVIAN", "Shavian"), ("GOTHIC", "Gothic"),
    ("DESERET", "Deseret"), ("GLAGOLITIC", "Glagolitic"), ("OLD ITALIC", "OldItalic"),
    ("OLD PERSIAN", "OldPersianCuneiform"), ("UGARITIC", "UgariticCuneiform"),
    ("CYPRIOT SYLLABLE", "CypriotSyllabary"), ("LINEAR B", "LinearB"),
    ("KHAROSHTHI", "Kharoshthi"), ("CJK UNIFIED IDEOGRAPH", "CJKIdeographic"),
    ("CJK COMPATIBILITY IDEOGRAPH", "CJKIdeographic"), ("KANGXI RADICAL", "CJKIdeographic"),
    ("CJK RADICAL", "CJKIdeographic"), ("IDEOGRAPHIC", "CJKIdeographic"),
    ("HIRAGANA", "Kana"), ("KATAKANA", "Kana"), ("BOPOMOFO", "Bopomofo"),
    ("YI SYLLABLE", "Yi"), ("BRAILLE PATTERN", "Braille"),
    ("MATHEMATICAL", "MathematicalAlphanumericSymbols"), ("MUSICAL SYMBOL", "MusicalSymbols"),
]

# 需要复杂整形（**保守方向**：拿不准就算复杂 —— 见文件头"宁可降级不猜"）
SIMPLE_SCRIPTS = {"Latin", "Greek", "Cyrillic", "CJKIdeographic", "Hangul", "Kana",
                  "Bopomofo", "Default", "Digit", "Control", "Mirror", "Braille",
                  "MathematicalAlphanumericSymbols", "MusicalSymbols", "LinearB"}
# `CharacterFastText` / `CharacterIdeo` 的划分（M7c 收尾轮按主控裁定修正）：
#   闸门在 `Typeface.CheckFastPathNominalGlyphs`：它把 `FastText|Ideo` 沿整串字符**按位与**，
#   任何一位被清掉就掉进 "else ⇒ (typography & Available)==0" 那个分支 ⇒ 只要字体报任何
#   排版特性就直接 false（回落到 LineServices）。所以**空格/标点/数字必须带 FastText**，
#   否则 "Hello WPF on Linux" 会在两个空格处把 FastText 位清掉。
#   规则（互斥、互补、可复核）：
#     · Complex  = 需要 shaping 的复杂脚本及其标记（阿拉伯/印度系/泰/高棉…）⇒ 既不是 FastText 也不是 Ideo
#     · Ideo     = CJK/Kana/Hangul 及 CJK 标点/全角区段
#     · FastText = **其余全部**（拉丁/希腊/西里尔字母 + ASCII/Latin-1 标点 + 空白 + 数字 + 货币/数学符号…）
#   即"简单文本"三类恰好覆盖，复杂脚本单独走 FullTextLine（那是正确行为，不是降级）。
IDEO_SCRIPTS = {"CJKIdeographic", "Kana", "Hangul"}
IDEO_CP = [(0x2E80, 0x2EFF), (0x2F00, 0x2FDF), (0x3000, 0x303F), (0x3040, 0x30FF),
           (0x3100, 0x312F), (0x31F0, 0x31FF), (0x3200, 0x32FF), (0x3300, 0x33FF),
           (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFAFF), (0xFE30, 0xFE4F),
           (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x20000, 0x3FFFF)]
EXTENDED_CP = [(0x0100, 0x024F), (0x1E00, 0x1EFF), (0x2C60, 0x2C7F), (0xA720, 0xA7FF)]

# UAX#9 的"未分配码点默认 Bidi_Class"（只取主要区段；其余按 L）
DEF_RTL = [(0x0590, 0x05FF), (0x07C0, 0x085F), (0xFB1D, 0xFB4F), (0x10800, 0x10CFF),
           (0x10D40, 0x10EBF), (0x10F00, 0x10F2F), (0x10F70, 0x10FFF), (0x1E800, 0x1EC6F),
           (0x1ECC0, 0x1ECFF), (0x1ED50, 0x1EDFF), (0x1EF00, 0x1EFFF)]
DEF_AL = [(0x0600, 0x07BF), (0x0860, 0x08FF), (0xFB50, 0xFDCF), (0xFDF0, 0xFDFF),
          (0xFE70, 0xFEFF), (0x10D00, 0x10D3F), (0x10EC0, 0x10EFF), (0x1EC70, 0x1ECBF),
          (0x1ED00, 0x1ED4F), (0x1EE00, 0x1EEFF)]
DEF_ET = [(0x20A0, 0x20CF)]
DEF_BN = [(0xFDD0, 0xFDEF), (0xE0000, 0xE0FFF)] + [(p * 0x10000 + 0xFFFE, p * 0x10000 + 0xFFFF)
                                                    for p in range(17)]


def in_ranges(cp, ranges):
    for lo, hi in ranges:
        if lo <= cp <= hi:
            return True
    return False


def script_of(cp, cat, name):
    """脚本：先按"伪脚本"（Control/Digit/Mirror）再按名字前缀。"""
    if cat in ("Cc", "Cf", "Co", "Cs", "Cn"):
        return "Control"
    if cat == "Nd":
        return "Digit"
    if u.mirrored(chr(cp)) and cat not in ("Lu", "Ll", "Lt", "Lm", "Lo"):
        return "Mirror"
    for prefix, sid in NAME_TO_SCRIPT:
        if name.startswith(prefix):
            return sid
    return "Default"


def bidi_of(cp, b):
    if b:
        return BIDI[b]
    # 未分配：按 UAX#9 的默认值（RTL 区块 → R/AL，不可见区 → BN，货币符号 → ET，其余 L）
    for lo, hi in DEF_AL:
        if lo <= cp <= hi:
            return BIDI["AL"]
    for lo, hi in DEF_RTL:
        if lo <= cp <= hi:
            return BIDI["R"]
    for lo, hi in DEF_ET:
        if lo <= cp <= hi:
            return BIDI["ET"]
    for lo, hi in DEF_BN:
        if lo <= cp <= hi:
            return BIDI["BN"]
    return BIDI["L"]


def classify(cp):
    """一个码点 → (ItemClass, ScriptID, Flags, BreakType, DirectionClass, LineBreak)。"""
    ch = chr(cp)
    cat = u.category(ch)
    b = u.bidirectional(ch)
    name = u.name(ch, "")
    script = script_of(cp, cat, name)
    bidi = bidi_of(cp, b)
    ccc = u.combining(ch)
    is_mark = cat in ("Mn", "Mc", "Me") or (ccc != 0) or (b == "NSM")

    # ---- ItemClass ---------------------------------------------------------
    if cp in (0x200C, 0x200D):                       # ZWNJ / ZWJ
        item = "Joiner"
    elif cp == 0x0023:                               # '#'
        item = "NumberSign"
    elif is_mark:
        item = "ComplexMark" if script not in SIMPLE_SCRIPTS else "SimpleMark"
    elif b == "EN":
        item = "Digit"
    elif b == "AN":
        item = "AN"
    elif b == "CS":
        item = "CS"
    elif b == "ES":
        item = "ES"
    elif b == "ET":
        item = "ET"
    elif cat in ("Cc", "Cf", "Co", "Cs", "Cn"):
        item = "Control"
    elif cat.startswith("L"):
        item = "Strong"
    else:
        item = "Weak"

    # ---- Flags -------------------------------------------------------------
    # Complex / Ideo / FastText **三类互斥**（见文件头 IDEO_CP 上面的说明）：
    #   · Complex  —— 复杂脚本及其标记：走不上快速路径是**正确行为**
    #   · Ideo     —— 表意文字（CJK/Kana/Hangul + CJK 标点/全角）
    #   · FastText —— 其余（"简单文本"：拉丁系字母 + 标点/空白/数字/符号）
    # FastText 必须覆盖空格/标点/数字：`charFastTextCheck` 是沿整串**按位与**的，
    # 漏掉一个空格就足以让整串掉出快速路径（M7c 实测的闸门 1 就是它）。
    f = 0
    is_complex = (script not in SIMPLE_SCRIPTS) or (item == "ComplexMark")
    is_ideo = (script in IDEO_SCRIPTS) or in_ranges(cp, IDEO_CP)
    if is_complex:
        f |= FLAG["Complex"]
    if is_ideo:
        f |= FLAG["Ideo"]
    if not is_complex and not is_ideo:
        f |= FLAG["FastText"]
    if bidi in (BIDI["R"], BIDI["AL"], BIDI["AN"]):
        f |= FLAG["RTL"]
    if cp in (0x000A, 0x000B, 0x000C, 0x0085, 0x2028):
        f |= FLAG["LineBreak"]
    if cp == 0x2029:
        f |= FLAG["ParaBreak"]
    if cp == 0x000D:
        f |= FLAG["CRLF"]
    if b in ("LRE", "RLE", "LRO", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI",
             "LRM", "RLM", "ALM") or cp in (0x200E, 0x200F, 0x061C):
        f |= FLAG["FormatAnchor"]
    if in_ranges(cp, EXTENDED_CP):
        f |= FLAG["Extended"]
    if cat == "Zs":
        f |= FLAG["Space"]
    if cat == "Nd":
        f |= FLAG["Digit"]
    if cat.startswith("L"):
        f |= FLAG["Letter"]

    # ---- BreakType（托管侧无消费者，合成）---------------------------------
    if item == "Control":
        brk = BREAK["ControlBreak"]
    elif item in ("Digit", "AN"):
        brk = BREAK["DigitBreak"]
    else:
        brk = BREAK["NoBreak"]

    return (ITEM[item], SCRIPT[script], f, brk, bidi, 0)


def build_classes():
    """全码点 → 类值（类的编号 = 属性元组的字典序序号，保证类值 < 472）。"""
    per_cp = [None] * (MAX_CP + 1)
    tuples = {}
    for cp in range(MAX_CP + 1):
        t = classify(cp)
        per_cp[cp] = t
        tuples.setdefault(t, 0)
    ordered = sorted(tuples.keys(), key=lambda x: (x[0], x[1], x[2], x[3], x[4], x[5]))
    idx = {t: i for i, t in enumerate(ordered)}
    return [idx[t] for t in per_cp], ordered


def emit(classes, tuples):
    blocks = {}          # 256 码点的类元组 → 叶子编号
    leaves = []
    # 17 个平面 × 256 个块；块内容相同的叶子共用
    planes = []
    for p in range(17):
        rows = []
        for b in range(256):
            base = (p << 16) | (b << 8)
            block = tuple(classes[base:base + 256])
            if len(set(block)) == 1:
                rows.append(("inline", block[0]))
            else:
                if block not in blocks:
                    blocks[block] = len(leaves)
                    leaves.append(block)
                rows.append(("ptr", blocks[block]))
        planes.append(rows)

    used_leaves = set()
    for rows in planes:
        for kind, v in rows:
            if kind == "ptr":
                used_leaves.add(v)

    L = []
    L.append("// WPF-on-Linux · M7c #U · **自动生成，请勿手改**")
    L.append("//")
    L.append("// 生成器：src/WpfGfx.Linux.Native/tools/gen-unicode-tables.py")
    L.append(f"// 数据源：本机 Python 3 的 unicodedata（UCD 版本 {u.unidata_version}）")
    L.append("//          category / bidirectional / combining / mirrored / name（脚本按名字前缀推）")
    L.append("//")
    L.append(f"// 类元组数 {len(tuples)}（必须 < {UNICODE_CLASS_MAX}，托管侧用小整数与指针共用同一格）")
    L.append("//")
    L.append("// 生成内容：")
    L.append("//   · k_wpf_uni_leaf_*   —— 256 个码点的类值（short[256] 的落点）")
    L.append("//   · k_wpf_uni_row_*    —— 第二级：**小整数(<472) 或指向叶子的指针**")
    L.append("//   · k_wpf_uni_planes   —— 17 个平面（托管用 ((cp>>16)&0xFF)%17 索引）")
    L.append("//   · k_wpf_char_attr    —— CharacterAttribute[类值]（Pack=1，8 字节/项）")
    L.append("")
    L.append('#include "win32_internal.h"')
    L.append("")
    L.append("// 所有表都是 .so **内部**符号（-fvisibility=default 的构建下显式标 hidden）：")
    L.append("// 它们只给同一 .so 里的 MILGetClassificationTables 用，不该出现在导出面里")
    L.append("// （导出面是给托管 DllImport 查的，多一个少一个都会让人误判覆盖情况）。")
    L.append("#ifndef WPF_TBL")
    L.append("#define WPF_TBL __attribute__((visibility(\"hidden\")))")
    L.append("#endif")
    L.append("")
    L.append("// ── 叶子：256 个码点的类值 ────────────────────────────────────────────────")
    for i, block in enumerate(leaves):
        if i not in used_leaves:
            continue
        L.append(f"static const uint16_t k_wpf_uni_leaf_{i}[256] = {{")
        for r in range(0, 256, 16):
            L.append("    " + " ".join(f"{v}," for v in block[r:r + 16]))
        L.append("};")
    L.append("")
    L.append("// ── 第二级：每个 256 码点块 = 一个小整数（整块同类）或叶子指针 ──────────")
    for p in range(17):
        L.append(f"static const uintptr_t k_wpf_uni_row_p{p}[256] = {{")
        for r in range(0, 256, 8):
            cells = []
            for kind, v in planes[p][r:r + 8]:
                cells.append(f"{v}," if kind == "inline" else f"(uintptr_t)k_wpf_uni_leaf_{v},")
            L.append("    " + " ".join(cells))
        L.append("};")
    L.append("")
    L.append("// ── 17 个平面（U+10FFFF 需要 17 个 64K 平面）────────────────────────────")
    L.append("WPF_TBL const uintptr_t *const k_wpf_uni_planes[17] = {")
    for p in range(17):
        L.append(f"    k_wpf_uni_row_p{p},")
    L.append("};")
    L.append("")
    L.append("// ── CharacterAttribute[类值]：{Script, ItemClass, Flags, BreakType, BiDi, LineBreak} ──")
    L.append("//    Pack=1（托管侧 `[StructLayout(LayoutKind.Sequential, Pack=1)]`），共 8 字节。")
    L.append("WPF_TBL const WPF_CHAR_ATTR k_wpf_char_attr[] = {")
    for t in tuples:
        item, script, flags, brk, bidi, lb = t
        L.append(f"    {{ {script}, {item}, {flags}, {brk}, {bidi}, {lb} }},   // 类 {tuples.index(t) if False else ''}".rstrip())
    L.append("};")
    L.append(f"WPF_TBL const int k_wpf_char_attr_count = {len(tuples)};")
    L.append("")

    # ── 镜像表（**降级：恒等**，理由见 win32_classification.c）────────────────
    # 形状与 UnicodeClasses 完全一致（17 平面 × 两级），只是叶子元素换成"镜像字符"。
    # 恒等 ⇒ 每块的叶子都是 0..255，于是**所有块共用同一张叶子**，17 个平面也共用同一行。
    L.append("// ── 镜像表（降级：恒等映射。托管侧无消费者 + 本地无 BidiMirroring 数据）──")
    L.append("static const uint16_t k_wpf_mirror_leaf[256] = {")
    for r in range(0, 256, 16):
        L.append("    " + " ".join(f"{v}," for v in range(r, r + 16)))
    L.append("};")
    L.append("static const uintptr_t k_wpf_mirror_row[256] = {")
    for r in range(0, 256, 8):
        L.append("    " + " ".join("(uintptr_t)k_wpf_mirror_leaf," for _ in range(8)))
    L.append("};")
    L.append("WPF_TBL const uintptr_t *const k_wpf_mirror_planes[17] = {")
    for p in range(17):
        L.append("    k_wpf_mirror_row,")
    L.append("};")
    L.append("")
    return "\n".join(L) + "\n", len(leaves), len(tuples)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--stats", action="store_true", help="只打统计，不写文件")
    args = ap.parse_args()

    classes, tuples = build_classes()
    text, n_leaves, n_tuples = emit(classes, tuples)

    print(f"UCD 版本            : {u.unidata_version}")
    print(f"码点数              : {MAX_CP + 1}（含补充平面）")
    print(f"不同属性元组（=类数）: {n_tuples}   （上限 {UNICODE_CLASS_MAX}）")
    print(f"叶子数组            : {n_leaves}")
    print(f"生成的 .c           : {len(text)} 字节")
    if n_tuples >= UNICODE_CLASS_MAX:
        print(f"[失败] 类数 {n_tuples} >= {UNICODE_CLASS_MAX}：托管侧会把类值误判成指针。", file=sys.stderr)
        return 1
    # 抽样自检：任何码点的类值都必须 < 472
    assert max(classes) < UNICODE_CLASS_MAX, max(classes)

    if args.stats:
        return 0
    os.makedirs(os.path.dirname(OUT_C), exist_ok=True)
    with open(OUT_C, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"写出                : {os.path.relpath(OUT_C, os.path.normpath(os.path.join(NATIVE_ROOT, '..', '..')))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
