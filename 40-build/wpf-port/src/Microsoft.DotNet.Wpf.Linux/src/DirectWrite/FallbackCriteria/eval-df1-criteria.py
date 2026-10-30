#!/usr/bin/env python3
"""D-F1 判据（**唯一实现**）v2 —— 吃 runner 的 `MODE=… PARA=… RESULT=OK …` 行，出 C1/C2/C3 + 两极牙。

被判对象：`MODE=null`（应用/harness 实际走的 `plan==null` 路径）× `PARA=b34`（b34 语料 F_nbsp_zwsp_w40，
取含 U+4E0E 的那一行 `[14,2)`）。`fb`/`nofb` 是**牙**；`PARA=b34line0` 是 C1 的**健康正控**。

──────────────────────────────────────────────────────────────────────────────
【C1（主控 2026-09-15 ④ 重定义：字体无关、本地就能判红）】三条腿（全部只用公开 API + **字体文件**，
两条都**不需要真机真值**）：
  ① `gid < 报出面的字形数`：`GlyphTypeface.GlyphCount`（公开 API）+ 独立的 `maxp.numGlyphs`（自读字体文件）
  ② **身份自洽**：`gid != 0 ⇒ 报出的 FontUri 那份面**必须**覆盖该码点`（独立读 cmap）；
     `gid == 0 ⇒ 该面**必须不**覆盖该码点`（.notdef 只有在"没人覆盖"时才诚实）。
     另：报出 URI 指向 `.ttc` 时必须带 `#n` 面号（真机形态 `…/YUGOTHM.TTC#1`）—— 本机当前用例无 .ttc ⇒ 不进判定
  ③ `GlyphTypeface.AdvanceWidths[gid]` **可查得**，且 `× GlyphRun.FontRenderingEmSize == 观测 AdvanceWidths[0]`
  ⇒ `有任一腿红 ⇒ C1=FAIL`；无红但有 `NOINFO` 腿 ⇒ `C1=NOINFO`；全绿 ⇒ `C1=PASS`。
【C2】观测面 `TextLine.GetIndexedGlyphRuns()`：`GID != 0`（回退**发生了**）。
【C3】与 b34 真值比（`F_nbsp_zwsp_w40#3`）：`LINE_W=16.0000`、`COL_W=12.6567`、`CR=[14,2) W=3.3433`。
     字体不同（真机回退面 `YUGOTHM.TTC#1`）⇒ 只作**量级/符号**判据（U+4E0E 在该字形上两侧都是 1 em）。
──────────────────────────────────────────────────────────────────────────────
【判据层自己的两极正控（纪律 27 / L25：判据层必须能"防空过"且**三态可产出**）】
  `--selftest` 用**合成日志**跑三态：真空 ⇒ `NOINFO`+`exit 3`；被判对象红 ⇒ `FAIL`+`exit 1`；修后形态 ⇒ `PASS`+`exit 0`。
  合成日志只验**判据层**，不代表任何真实路径（输出里逐条标明）。
──────────────────────────────────────────────────────────────────────────────
【来源口径（纪律 18）】本判据用到的每个量都要能一眼看出"报的是哪一侧"：
  · 来自 runner 的公开 API 读数（`TextLine.*` / `IndexedGlyphRun.GlyphRun.*` / `GlyphTypeface.*`）⇒ 值由**本 shim** 计算/写入
  · 来自 `advance_from_font.py` 的 ⇒ **外部独立**（自读 cmap/hmtx/maxp，不信任何 API 自述）
  · DIAG 行 ⇒ **次级来源**，只可出现在牙的 INFO 里，**C1/C2 永不使用**
"""
import os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
FONT_DEFAULT = os.path.join(REPO, "src", "Linux", "build", "fonts", "NotoSans-Regular.ttf")
PROBE_CP = 0x4E0E                      # 被判段第一行的首字符 = U+4E0E（下标 14）

# ---- 常量（逐条可回源）----
TRUTH = dict(line_w=16.0000, col_w=12.656666666666668, cr_w=3.343333333333332, cr_idx=14, cr_len=2,
             font="YUGOTHM.TTC#1", glyph=3883,
             src="src/Linux/tests/parity/windows/layout-b34（F_nbsp_zwsp_w40 行#3，经 gen/layout-b34-compact.json 复读）")
PRE = dict(line_w=9.6000, col_w=12.6560, cr_w=-3.0560, cr_idx=14, cr_len=2, adv=9.6000,
           src="src/Linux/build/MilBridge/gen/tline-detail-full.txt 逐字行（该文件头 sha 272833acb4fe03fe，内含被测 shim FDE9E511…）")
TAIL_LINE, TAIL_ADV = 16.0000, 16.0000     # 回退面 1 em（与本机 CJK 候选实测一致；真机亦 1 em）
TOL_TIGHT = 0.01
# 牙开关：牙是**针对"今天真实读数"的断言**（修前常量 / 今天的缺陷形态）⇒ 合成日志（判据层自验）里
#   它们**按设计会红**。故自验的"修后形态"用例显式 `DF1_TEETH=off` 并**在输出里标明**（绝不静默关闭）。
TEETH_ON = os.environ.get("DF1_TEETH", "on").lower() != "off"
CRITERIA_VERSION = "D-F1 判据 v2（2026-09-15：防空过四闸 + 新 C1 三腿 + 判据层三态自验）"


def _sha16(path):
    try:
        import hashlib
        return hashlib.sha256(open(path, "rb").read()).hexdigest()[:16]
    except OSError:
        return "NA"


def instrument_header():
    """自证头：判据自己的 sha + 相关件的 sha + loadavg/时间 ⇒ **引用这三支正控时不必另找上下文**。"""
    try:
        load = open("/proc/loadavg").read().split()[:3]
    except OSError:
        load = ["NA"]
    return ("# SELFTEST-HARNESS 判据 sha16=%s  口径版本=%s\n"
            "# 件：Program.cs sha16=%s  shim 源 sha16=%s（**仅为上下文**：本自验不编 shim、不读 PC ⇒ 该值与结论无关；"
            "落地窗口期它可能正处于**半改**状态）   PC sha16=%s  advance_from_font sha16=%s\n"
            "# 运行那一刻：%s  loadavg=%s（**合成输入**：不碰树、不跑 harness、不依赖 PC/源一致性）"
            % (_sha16(os.path.abspath(__file__)), CRITERIA_VERSION,
               _sha16(os.path.join(HERE, "Program.cs")),
               _sha16(os.path.join(REPO, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "PresentationCore.HbTextLine.cs")),
               _sha16(os.path.join(REPO, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore", "bin", "Debug", "PresentationCore.dll")),
               _sha16(os.path.join(HERE, "advance_from_font.py")),
               __import__("datetime").datetime.now().strftime("%Y-%m-%dT%H:%M:%S%z"), "/".join(load)))

KNOWN_MODES = ("null", "fb", "nofb")
KNOWN_PARAS = ("b34", "b34line0", "two")
SUBJECT = ("null", "b34")
HEALTHY = ("null", "b34line0")


# ────────────────────────────── 读入 ──────────────────────────────
def parse(raw):
    rows, diags, srcs, others = {}, {}, {}, []
    for line in raw.splitlines():
        m = re.match(r"^MODE=(\S+) PARA=(\S+) RESULT=(\S+) (.*)$", line)
        if m:
            mode, para = m.group(1), m.group(2)
            d = dict(kv.split("=", 1) for kv in m.group(4).split() if "=" in kv)
            if mode in KNOWN_MODES and para in KNOWN_PARAS:
                rows[(mode, para)] = d
            else:
                others.append(line)
            continue
        m = re.match(r"^MODE=(\S+) PARA=(\S+) DIAG=(.*)$", line)
        if m and m.group(1) in KNOWN_MODES and m.group(2) in KNOWN_PARAS:
            diags[(m.group(1), m.group(2))] = m.group(3)
            continue
        m = re.match(r"^# SRC MODE=(\S+) PARA=(\S+) (.*)$", line)
        if m:
            srcs[(m.group(1), m.group(2))] = m.group(3)
    return rows, diags, srcs, others


def fnum(d, k):
    try:
        return float(d.get(k, "nan"))
    except (TypeError, ValueError):
        return float("nan")


def ii(d, k):
    try:
        return int(d.get(k, ""))
    except (TypeError, ValueError):
        return None


# ────────────────── 外部独立复算（自读字体文件） ──────────────────
def font_probe(path, gid, em, cp=None):
    """调 `advance_from_font.py`：**唯一的外部独立来源**。返回 dict（读不到 ⇒ None）。"""
    if not path or not os.path.isfile(path):
        return None
    argv = [sys.executable, os.path.join(HERE, "advance_from_font.py"), path, str(gid if gid is not None else 0), str(em)]
    if cp is not None:
        argv.append("--cp=U+%04X" % cp)
    out = subprocess.run(argv, capture_output=True, text=True)
    txt = out.stdout
    r = {"raw": txt}
    m = re.search(r"NUMGLYPHS=(\d+)", txt)
    if m: r["numglyphs"] = int(m.group(1))
    m = re.search(r"# CMAP cp=U\+([0-9A-F]+) gid=(\d+) HAS=(true|false)", txt)
    if m:
        r["cmap_cp"] = int(m.group(1), 16); r["cmap_gid"] = int(m.group(2)); r["covers"] = (m.group(3) == "true")
    m = re.search(r"ADV=([\d.]+)", txt)
    if m: r["adv"] = float(m.group(1))
    return r


# ────────────────────────────── C1 ──────────────────────────────
def c1_legs(d, out, tag):
    """返回 {腿名: True/False/None}；`None` = 该腿因**非缺陷原因**读不到（拉低为 NOINFO，绝不当 PASS）。"""
    legs = {}
    gid_s = d.get("GID"); gid = ii(d, "GID"); cp = d.get("CP")
    face_uri = d.get("FACE_URI") or "-"
    face_path = face_uri.replace("file://", "")
    em = fnum(d, "RUN_EM")
    if not (em == em) or em <= 0: em = 16.0
    # 探针码点 = **读数行自己报的 CP**（不许用常量顶替：否则腿②会拿错码点去问字体文件）
    cp_val = None
    m = re.match(r"U\+([0-9A-Fa-f]{1,6})$", cp or "")
    if m: cp_val = int(m.group(1), 16)
    # 独立复算（一次性，供①②两腿共用）
    fp = font_probe(face_path, gid, em, cp=cp_val) if cp_val is not None else font_probe(face_path, gid, em)
    if fp is None:
        out.append(f"[{tag}]   ↳ 独立复算不可用（面文件读不到：{face_path or '-'}）⇒ ②腿与①的独立腿记 NOINFO（**不许当 PASS**）")
    # ① gid < 字形数
    lt = d.get("GID_LT_COUNT")
    legs["①gid<GlyphCount(公开API)"] = None if lt in (None, "-") else (lt == "true")
    if fp and fp.get("numglyphs") is not None and gid is not None:
        legs["①gid<numGlyphs(独立/字体文件)"] = gid < fp["numglyphs"]
    else:
        legs["①gid<numGlyphs(独立/字体文件)"] = None
    # ② 身份自洽（独立 cmap）
    if gid is None or fp is None or "covers" not in fp:
        legs["②gid≠0⇒报出面须覆盖CP(独立cmap)"] = None
    else:
        legs["②gid≠0⇒报出面须覆盖CP(独立cmap)"] = (fp["covers"] is True) if gid != 0 else (fp["covers"] is False)
    # 【主控 2026-09-15 任务 4 口径】`.ttc` 的**面号形态**单列成**信息腿**（不进 C1 判定）：
    #   真机形态是 `…YUGOTHM.TTC#1`（带 `#n`）；但本机 face 0 报出的是**不带 `#`** 的形态，且 `#n` 取不到
    #   ⇒ 按主控口径报 **NOINFO**（不判红、也不当绿）。**一旦能取到就纳入判定**（把 TTC_LEG_JUDGE 置真即可）。
    ttc_note = "n/a（报出的面不是 .ttc）"
    if face_path.lower().endswith(".ttc"):
        ttc_note = ("PASS（带面号 #n，与真机 `…TTC#1` 形态一致）" if ("#" in face_uri.rsplit("/", 1)[-1])
                    else "NOINFO（**face 0 形态不带 `#`** —— 主控口径；本机取不到 `#n` ⇒ 本腿不进 C1 判定）")
    # ③ AdvanceWidths[gid] 可查得且等值
    tfp = d.get("TF_ADV_PRESENT")
    legs["③AdvanceWidths[gid]可查"] = None if tfp in (None, "-") else (tfp == "true")
    adv, tadv = fnum(d, "ADV_DIP"), fnum(d, "ADV_FROM_TYPEFACE")
    if tfp == "true" and adv == adv and tadv == tadv:
        legs["③AdvanceWidths[gid]×em==观测advance"] = abs(adv - tadv) < 1e-6
    else:
        legs["③AdvanceWidths[gid]×em==观测advance"] = None
    return legs, ttc_note


def verdict(legs):
    if any(v is False for v in legs.values()): return "FAIL"
    if any(v is None for v in legs.values()): return "NOINFO"
    return "PASS"


def legs_txt(legs):
    return "；".join(f"{k}={('绿' if v else '红') if v is not None else 'NOINFO'}" for k, v in legs.items())


# ────────────────────────────── 主流程 ──────────────────────────────
def evaluate(raw, selftest_mode=False):
    rows, diags, srcs, others = parse(raw)
    lines, n_fail, n_noinfo = [], 0, 0
    emit = lines.append

    # ===== 防空过①：一条读数都没有 =====
    if not rows:
        emit("CRITERIA=NOINFO reason=no-runner-output（runner 未构建/未运行，或行格式不符 ⇒ **零检查不等于通过**）")
        for o in others[:5]: emit(f"  未识别的行：{o[:160]}")
        return "\n".join(lines) + "\n", 3
    # ===== 防空过②：三模式缺一 ⇒ NOINFO（v1 曾在此印出空过 PASS —— L25）=====
    missing_modes = [m for m in KNOWN_MODES if (m, "b34") not in rows]
    if missing_modes:
        emit("CRITERIA=NOINFO reason=missing-modes:" + ",".join(missing_modes) +
             "（缺读数 ⇒ **零检查不等于通过**；runner 兜底行见下）")
        for o in others[:5]: emit(f"  runner 兜底行：{o[:160]}")
        return "\n".join(lines) + "\n", 3
    # ===== 防空过③：被判对象 / 健康正控 缺一 ⇒ NOINFO =====
    if SUBJECT not in rows:
        emit(f"CRITERIA=NOINFO reason=missing-subject:{SUBJECT[0]}/{SUBJECT[1]}（被判对象没有读数 ⇒ 不许报绿）")
        return "\n".join(lines) + "\n", 3
    if HEALTHY not in rows:
        emit(f"CRITERIA=NOINFO reason=missing-healthy-control:{HEALTHY[0]}/{HEALTHY[1]}"
             "（C1 的**健康正控**缺读数 ⇒ 红与『恒红』不可分 ⇒ 不许报绿；见纪律 27/L25）")
        return "\n".join(lines) + "\n", 3

    # ---------- 逐行读数 ----------
    for (mode, para) in [(m, p) for m in KNOWN_MODES for p in KNOWN_PARAS if (m, p) in rows]:
        d = rows[(mode, para)]
        tag = f"{mode}/{para}"
        adv, tadv = fnum(d, "ADV_DIP"), fnum(d, "ADV_FROM_TYPEFACE")
        emit(f"[{tag}] 行宽 LINE_W={fnum(d,'LINE_W'):.4f} WITW={fnum(d,'LINE_WITW'):.4f} | 折后 COL_W={d.get('COL_W')} "
             f"COL_LEN={d.get('COL_LEN')} HAS={d.get('COL_HAS')} CR=[{d.get('CR_IDX')},{d.get('CR_LEN')}) W={d.get('CR_W')} "
             f"| API INDEXED={d.get('INDEXED')} RUNS={d.get('RUNS')} GID={d.get('GID')} ADV_DIP={d.get('ADV_DIP')} "
             f"ADV_FROM_TYPEFACE={d.get('ADV_FROM_TYPEFACE')} TF_ADV_PRESENT={d.get('TF_ADV_PRESENT')} "
             f"GLYPH_COUNT={d.get('GLYPH_COUNT')} GID_LT_COUNT={d.get('GID_LT_COUNT')} CP={d.get('CP')} "
             f"FACE={d.get('FACE_URI')} ｜ **字体环境口径**：WPF_LINUX_FONT_DIR={d.get('FONTDIR_ENV')} "
             f"WPF_LINUX_MULTIFONT={d.get('MULTIFONT_ENV')}（设没设**会改读数** ⇒ 必须随行记）")
        if (mode, para) in srcs:
            emit(f"[{tag}]   SRC {srcs[(mode, para)]}")

    # ================= C1（被判对象）=================
    dsub = rows[SUBJECT]
    tag_sub = f"{SUBJECT[0]}/{SUBJECT[1]}"
    legs, ttc_note = c1_legs(dsub, lines, tag_sub)
    c1 = verdict(legs)
    n_fail += (c1 == "FAIL"); n_noinfo += (c1 == "NOINFO")
    emit(f"[{tag_sub}] C1={c1}（新 C1 三腿：{legs_txt(legs)}）")
    emit(f"[{tag_sub}]   C1②b（.ttc 面号形态，**信息腿**，不进 C1 判定）：{ttc_note}")
    emit(f"[{tag_sub}]   ↳ 含义：{'①' if c1=='FAIL' else ''}"
         f"{'报出的面装不下该 gid / ' if legs.get('①gid<GlyphCount(公开API)') is False else ''}"
         f"{'报出非 .notdef 却报了一个不覆盖该码点的面（三元组不同源）/ ' if legs.get('②gid≠0⇒报出面须覆盖CP(独立cmap)') is False else ''}"
         f"{'报出面的 AdvanceWidths 里没有这个 gid / ' if legs.get('③AdvanceWidths[gid]可查') is False else ''}"
         f"三腿全部只用公开 API + 字体文件，**不需要真机真值**")

    # ================= C2（被判对象）=================
    indexed, gid_s = dsub.get("INDEXED"), dsub.get("GID")
    if indexed == "UNIMPLEMENTED":
        n_noinfo += 1; emit(f"[{tag_sub}] C2=NOINFO（GetIndexedGlyphRuns() 空序列/Owed 桩 ⇒ 观测面不可用；**不许报绿**）")
    elif indexed == "OK" and gid_s not in (None, "-"):
        ok = int(gid_s) != 0
        n_fail += (not ok)
        emit(f"[{tag_sub}] C2={'PASS' if ok else 'FAIL'}（glyph={gid_s} {'≠0 ⇒ 回退真的发生了' if ok else '==0 就是 .notdef'}）")
    else:
        n_noinfo += 1; emit(f"[{tag_sub}] C2=NOINFO（INDEXED={indexed}：没有覆盖被选码元的 run ⇒ 观测面不可用）")

    # ================= C3（被判对象 vs b34 真值）=================
    lw, colw, crw = fnum(dsub, "LINE_W"), fnum(dsub, "COL_W"), fnum(dsub, "CR_W")
    ci, cl = ii(dsub, "CR_IDX"), ii(dsub, "CR_LEN")
    same_font = TRUTH["font"] in (dsub.get("FACE_URI") or "")
    d1, d2, d3 = abs(lw - TRUTH["line_w"]), abs(colw - TRUTH["col_w"]), abs(crw - TRUTH["cr_w"])
    span_ok = (ci == TRUTH["cr_idx"] and cl == TRUTH["cr_len"])
    c3_ok = (d1 <= TOL_TIGHT and d2 <= TOL_TIGHT and d3 <= TOL_TIGHT and span_ok and crw > 0)
    n_fail += (not c3_ok)
    emit(f"[{tag_sub}] C3={'PASS' if c3_ok else 'FAIL'}（行宽 {lw:.4f} vs {TRUTH['line_w']}：差 {d1:.4f}；折后宽 {colw:.4f} vs "
         f"{TRUTH['col_w']:.4f}：差 {d2:.4f}；CR_W {crw:.4f} vs {TRUTH['cr_w']:.4f}：差 {d3:.4f}（符号{'正' if crw>0 else '负'}）；"
         f"CR 区间 [{'?' if ci is None else ci},{'?' if cl is None else cl}) vs [14,2)：{'一致' if span_ok else '**不一致**'}；"
         f"字体{'与真值相同' if same_font else '与真值**不同**（' + TRUTH['font'] + '）⇒ 只作量级/符号判据'}）")
    emit(f"[{tag_sub}]   ↳ 真值出处：{TRUTH['src']}；修前读数出处：{PRE['src']}")

    # ================= 牙（**针对"今天真实读数"的断言**）=================
    #   读数无论开关都算、都印；只有"判定/计数"受 `DF1_TEETH` 控制（关时逐条印『未判』，绝不静默变绿）。
    word = (lambda ok: 'PASS' if ok else 'FAIL') if TEETH_ON else (lambda ok: '未判(DF1_TEETH=off)')
    # ---- TOOTH-NEG：nofb 必须**逐项**复现修前读数（行宽 / 折后宽 / CR_W / CR 区间 / ADV 分开比）----
    dneg = rows[("nofb", "b34")]
    got = dict(line_w=fnum(dneg, "LINE_W"), col_w=fnum(dneg, "COL_W"), cr_w=fnum(dneg, "CR_W"), adv=fnum(dneg, "ADV_DIP"))
    okn = {k: (got[k] == got[k] and abs(got[k] - PRE[k]) <= TOL_TIGHT) for k in ("line_w", "col_w", "cr_w", "adv")}
    span_n = (ii(dneg, "CR_IDX") == PRE["cr_idx"] and ii(dneg, "CR_LEN") == PRE["cr_len"])
    neg_ok = all(okn.values()) and span_n
    # ---- TOOTH-POS：fb 行宽回到 1 em、CR_W 转正、ADV 回到 1 em ----
    dpos = rows[("fb", "b34")]
    lwp, crwp, advp = fnum(dpos, "LINE_W"), fnum(dpos, "CR_W"), fnum(dpos, "ADV_DIP")
    pos_items = {"行宽≈1em": abs(lwp - TAIL_LINE) <= TOL_TIGHT, "CR_W>0": crwp > 0, "ADV≈1em": abs(advp - TAIL_ADV) <= TOL_TIGHT}
    pos_ok = all(pos_items.values())
    # ---- 新 C1 的牙（2026-09-15 窗口 3 换口径）：① 现场断言"历史缺陷形态必须不再出现"；② "能红"由合成用例 2 保证；
    #      ③ 正牙：健康输入（同 plan==null 路径的全拉丁行）必须能绿 ⇒ 排除"恒红" ----
    dhea = rows[HEALTHY]
    legs_h, ttc_note_h = c1_legs(dhea, lines, f"{HEALTHY[0]}/{HEALTHY[1]}")
    c1h = verdict(legs_h)
    # 【2026-09-15 窗口 3 换口径：`D-F1b` 已落地】旧负牙"**今天这版必须判红**"是针对**具体缺陷形态**的断言，
    #   缺陷修好后它**必然变假**（实测 `92fc7605`：C1=PASS ⇒ 旧负牙 FAIL ＝ **过期牙**）。
    #   按纪律：牙的功能 = 证明该判据**能红** ⇒ 这一条改由**判据层自验的合成用例 2**（每趟先跑，打印在本行上方）承担；
    #   现场改为断言"**历史缺陷形态必须不再出现**"：`TF_ADV_PRESENT=true` 且 `GID_LT_COUNT=true`（D-F1b 时期这两项都是 false）。
    def _df1b_sig(d):
        return (d.get("TF_ADV_PRESENT") == "true") and (d.get("GID_LT_COUNT") == "true")
    subj_sig = _df1b_sig(dsub)
    fb_sig = _df1b_sig(rows[("fb", "b34")]) if ("fb", "b34") in rows else None
    c1neg_ok = subj_sig and (fb_sig in (True, None))
    c1pos_ok = (c1h == "PASS")
    if TEETH_ON:
        n_fail += (not neg_ok) + (not pos_ok) + (not c1neg_ok) + (not c1pos_ok)
        n_noinfo += (c1h == "NOINFO")
    emit(f"[nofb/b34] TOOTH-NEG={word(neg_ok)}（必须逐项复现修前：行宽 {got['line_w']:.4f}/{PRE['line_w']} "
         f"{'✓' if okn['line_w'] else '✗'}；**折后宽** {got['col_w']:.4f}/{PRE['col_w']} {'✓' if okn['col_w'] else '✗'}；"
         f"CR_W {got['cr_w']:.4f}/{PRE['cr_w']} {'✓' if okn['cr_w'] else '✗'}；ADV {got['adv']:.4f}/{PRE['adv']} "
         f"{'✓' if okn['adv'] else '✗'}；CR 区间 [{'?' if ii(dneg,'CR_IDX') is None else ii(dneg,'CR_IDX')},"
         f"{'?' if ii(dneg,'CR_LEN') is None else ii(dneg,'CR_LEN')}) vs [14,2) {'✓' if span_n else '✗'}）")
    emit(f"[fb/b34] TOOTH-POS={word(pos_ok)}（行宽 {lwp:.4f}≈{TAIL_LINE} {'✓' if pos_items['行宽≈1em'] else '✗'}；"
         f"CR_W={crwp:.4f}>0 {'✓' if pos_items['CR_W>0'] else '✗'}；ADV {advp:.4f}≈{TAIL_ADV} {'✓' if pos_items['ADV≈1em'] else '✗'}）")
    emit(f"[{tag_sub}] TOOTH-D-F1b-ABSENT={word(c1neg_ok)}（**历史形态必须不再出现**：`TF_ADV_PRESENT=true` 且 `GID_LT_COUNT=true`；"
         f"实得 null={subj_sig} fb={fb_sig}（D-F1b 时期两项都是 false）｜C1={c1}）")
    emit(f"[{tag_sub}] TOOTH-C1-REDCAP=合成用例保证（判据层自验第 2 例：只喂 D-F1b 形态的合成日志 ⇒ `CRITERIA=FAIL`+`exit=1`，见本趟输出上方）"
         f" —— 旧名 `TOOTH-C1-NEG` 的「今天必须判红」已随缺陷修好**作废**（2026-09-15 窗口 3 换口径）")
    emit(f"[{HEALTHY[0]}/{HEALTHY[1]}] TOOTH-C1-POS={word(c1pos_ok)}（正牙：**同一 plan==null 路径**下的健康输入"
         f"（全拉丁行，段落字体自己覆盖）必须判绿 ⇒ 排除『恒红』；实得 C1={c1h}｜{legs_txt(legs_h)}）")
    if not TEETH_ON:
        emit("（⚠️ 本趟 `DF1_TEETH=off`：上面这些牙**未判**、未计入 fail/noinfo —— 只验判据层三态；该开关只由判据层自验使用，真实读数趟**不许**用它）")

    # ---- 修后应绿（fb 路径实测；informational，不进 n_fail）----
    if ("fb", "b34") in rows:
        legs_fb, ttc_note_fb = c1_legs(rows[("fb", "b34")], lines, "fb/b34")
        c1fb = verdict(legs_fb)
        emit(f"[fb/b34] C1={c1fb}（**修后应绿的实证位**：fb 路径与 null 路径走同一 `_glyphRuns`/API 面；"
             f"{'已绿 ⇒ 把 null 路径接到同一报面逻辑即可转绿' if c1fb=='PASS' else '仍红/NOINFO ⇒ 单修 null 路径的报面**不足以**转绿，要让 T1d 看到这一条'}｜{legs_txt(legs_fb)}）")

    # ---- DIAG（次级来源，只打印，不参与 C1/C2）----
    for (mode, para) in [("null", "b34"), ("fb", "b34"), ("nofb", "b34")]:
        if (mode, para) in diags:
            emit(f"[{mode}/{para}] DIAG-INFO（**次级来源=内部装置**，仅供定位，不计入任何 PASS）：{diags[(mode,para)][:300]}")
        if (mode, para) in rows and rows[(mode, para)].get("FALLBACK_DIAG"):
            emit(f"[{mode}/{para}] FALLBACK_DIAG（次级来源）：{rows[(mode, para)]['FALLBACK_DIAG'][:300]}")

    # ================= [fix] 判别行 =================
    dn, df, dnf = rows[("null", "b34")], rows[("fb", "b34")], rows[("nofb", "b34")]
    v = {"null": dict(line_w=fnum(dn, "LINE_W"), cr_w=fnum(dn, "CR_W"), adv=fnum(dn, "ADV_DIP")),
         "fb": dict(line_w=fnum(df, "LINE_W"), cr_w=fnum(df, "CR_W"), adv=fnum(df, "ADV_DIP")),
         "nofb": dict(line_w=fnum(dnf, "LINE_W"), cr_w=fnum(dnf, "CR_W"), adv=fnum(dnf, "ADV_DIP"))}
    landed = all(abs(v["null"][k] - v["fb"][k]) < 1e-3 for k in v["null"])
    distinct = all(abs(v["null"][k] - v["nofb"][k]) > 1e-3 for k in v["null"])
    if TEETH_ON: n_fail += (not (landed and distinct))
    emit(f"[fix] MODE=null {'== fb ⇒ **修法已生效**（plan==null 路径真的回退了）' if (landed and distinct) else '≠ fb ⇒ 修法**未**生效/未全生效'}"
         f"（行宽 null={v['null']['line_w']:.4f} fb={v['fb']['line_w']:.4f} nofb={v['nofb']['line_w']:.4f}；"
         f"CR_W null={v['null']['cr_w']:.4f} fb={v['fb']['cr_w']:.4f} nofb={v['nofb']['cr_w']:.4f}；"
         f"ADV null={v['null']['adv']:.4f} fb={v['fb']['adv']:.4f} nofb={v['nofb']['adv']:.4f}）"
         + ("（本行计入 fail；与 C2/C3 同源缺陷会双计 —— 宁可多计也不漏）" if TEETH_ON else "（DF1_TEETH=off ⇒ 本行未计入）"))
    emit("[fix] 判别量来源口径：行宽 = `TextLine.Width`、CR_W = `TextLine.Collapse(...).GetTextCollapsedRanges()[0].Width`、"
         "ADV = `IndexedGlyphRun.GlyphRun.AdvanceWidths[0]` —— **三者都是『我们这一侧经公开 API 面报出来的量』（值由本 shim 计算/写入）**，"
         "互相独立的只是**代码路径**；**不是**外部独立量。外部独立量只有 `advance_from_font.py`（自读字体文件），本判据用它做 C1 的 ①② 腿。")

    # ================= 总判定 =================
    verdict_word = "NOINFO" if n_noinfo else ("FAIL" if n_fail else "PASS")
    emit(f"CRITERIA={verdict_word}（fail={n_fail} noinfo={n_noinfo}；被判对象={tag_sub}；观测面=GetIndexedGlyphRuns()；"
         f"真值={TRUTH['line_w']}/{TRUTH['col_w']:.4f}/{TRUTH['cr_w']:.4f}←{TRUTH['font']} glyph{TRUTH['glyph']}）")
    if verdict_word == "NOINFO":
        emit("  ⚠️ NOINFO **不等于通过**：上面每一条判据都标了它为什么缺读数。")
    return "\n".join(lines) + "\n", (3 if n_noinfo else (1 if n_fail else 0))


# ────────────────────────── 判据层自验（三态正控）──────────────────────────
def _mkrow(mode, para, **kw):
    base = dict(LINES="9", SEL="0", SEL_START="0", SEL_LEN="5", SEL_TEXT="x", CP="U+4E0E", LINE_W="16.0000",
                LINE_WITW="20.1600", LINE_NL="0", LINE_WS="1", HAS_OVERFLOWED="false",
                COL_CONSTRAINT="8.0000", COL_W="12.6560", COL_LEN="2", COL_HAS="true",
                CR_IDX="14", CR_LEN="2", CR_W="3.3440", INDEXED="OK", RUNS="2", RUN_EM="16.0000",
                FACE_URI="file://x.ttf", GID="9498", ADV_DIP="16.0000", ADV_FROM_TYPEFACE="-",
                TF_ADV_PRESENT="false", TF_ADV_RAW="-", GLYPH_COUNT="100", GID_LT_COUNT="false", PLAN_FACE="-")
    base.update({k: str(v) for k, v in kw.items()})
    body = " ".join(f"{k}={v}" for k, v in base.items())
    return f"MODE={mode} PARA={para} RESULT=OK {body}"


def selftest():
    """**判据层自己的**两极/三态正控（纪律 27 / L25）。合成日志只验判据层，不代表真实路径。"""
    f = FONT_DEFAULT
    if not os.path.isfile(f):
        print(f"SELFTEST=FAIL reason=fixture-font-missing:{f}（正控跑不起来 ⇒ 不许当通过）"); return 1
    probe_cjk = font_probe(f, 0, 16.0, cp=0x4E0E)      # 期望 covers=false（NotoSans 无 U+4E0E）
    probe_lat = font_probe(f, 0, 16.0, cp=0x006E)      # 期望 covers=true（'n'）
    if not probe_cjk or probe_cjk.get("covers") is not False or not probe_lat or probe_lat.get("covers") is not True:
        print(f"SELFTEST=FAIL reason=fixture-unexpected:{probe_cjk} {probe_lat}"); return 1
    uri = "file://" + f
    lat_gid, lat_adv = probe_lat["cmap_gid"], probe_lat["adv"]
    head = [f"# SELFTEST 合成日志（**只验判据层三态**，不代表真实路径）",
            _mkrow("null", "b34line0", FACE_URI=uri, GID=lat_gid, CP="U+006E", GLYPH_COUNT=1000, GID_LT_COUNT="true",
                   TF_ADV_PRESENT="true", TF_ADV_RAW=f"{lat_adv/16.0:.6f}", ADV_DIP=f"{lat_adv:.4f}",
                   ADV_FROM_TYPEFACE=f"{lat_adv:.4f}"),
            _mkrow("nofb", "b34", LINE_W="9.6000", COL_W="12.6560", CR_W="-3.0560", ADV_DIP="9.6000",
                   FACE_URI=uri, GID="0", GLYPH_COUNT="1000", GID_LT_COUNT="true", TF_ADV_PRESENT="true",
                   TF_ADV_RAW="0.600000", ADV_FROM_TYPEFACE="9.6000", COL_CONSTRAINT="4.8000"),
            _mkrow("fb", "b34", LINE_W="16.0000", COL_W="12.6560", CR_W="3.3440", ADV_DIP="16.0000",
                   FACE_URI=uri, GID="1234", GLYPH_COUNT="3000", GID_LT_COUNT="true", CP="U+006E",
                   TF_ADV_PRESENT="true", TF_ADV_RAW="1.000000", ADV_FROM_TYPEFACE="16.0000")]
    # 三态：① 真空（只喂一个非模式行）② 被判对象红（今天的 D-F1b 形态）③ 修后形态（全绿）
    cases = [
        ("真空：只喂 MODE=none 兜底行", ["MODE=none PARA=- RESULT=NOINFO reason=synthetic"], 3, "CRITERIA=NOINFO", {}),
        ("被判对象红：D-F1b 形态（gid 9498 挂在 NotoSans 上）",
         head + [_mkrow("null", "b34", FACE_URI=uri, GID="9498", CP="U+4E0E", GLYPH_COUNT="1000",
                        GID_LT_COUNT="false", TF_ADV_PRESENT="false", ADV_DIP="16.0000", ADV_FROM_TYPEFACE="-")],
         1, "CRITERIA=FAIL", {}),
        # 关牙的理由：牙是**针对今天真实读数**的断言（"被判对象必须红"），合成日志里按设计必红 ⇒
        #   本用例只验"判据层能产出 PASS/exit 0"（防空过、防恒红/恒 NOINFO）。
        ("修后形态：被判对象也自洽（面覆盖码点、advance 同源）",
         head + [_mkrow("null", "b34", FACE_URI=uri, GID=lat_gid, CP="U+006E", GLYPH_COUNT="1000",
                        GID_LT_COUNT="true", TF_ADV_PRESENT="true", TF_ADV_RAW="1.000000",
                        ADV_DIP="16.0000", ADV_FROM_TYPEFACE="16.0000")],
         0, "CRITERIA=PASS", {"DF1_TEETH": "off"}),
    ]
    bad = 0
    verbose = ("--verbose" in sys.argv) or (os.environ.get("DF1_SELFTEST_VERBOSE", "") not in ("", "0"))
    print(instrument_header())
    for name, log, want_rc, want_word, extra_env in cases:
        p = subprocess.run([sys.executable, os.path.abspath(__file__)], input="\n".join(log) + "\n",
                           capture_output=True, text=True, env={**os.environ, **extra_env})
        if verbose:
            print("\n" + "=" * 100)
            print(f"### 用例：{name}")
            print(f"### 合成输入（喂给判据层的 stdin，共 {len(log)} 行）：")
            for ln in log:
                print("    | " + ln)
            print(f"### 子进程判据层逐字输出（env 追加：{extra_env or '无'}）：")
            for ln in p.stdout.splitlines():
                print("    " + ln)
            if p.stderr.strip():
                print("### 子进程 stderr：")
                for ln in p.stderr.splitlines():
                    print("    " + ln)
            print(f"### 子进程退出码 exit={p.returncode}（期望 rc={want_rc}）")
        got_word = next((ln for ln in p.stdout.splitlines() if ln.startswith("CRITERIA=")), "(无 CRITERIA 行)")
        ok = (p.returncode == want_rc) and got_word.startswith(want_word)
        bad += (not ok)
        print(f"[SELFTEST] {name}：期望 rc={want_rc} 且以 `{want_word}` 开头 ⇒ 实得 rc={p.returncode}｜{got_word[:90]}"
              f" ⇒ {'OK' if ok else '**不符**'}")
        if not ok:
            for ln in p.stdout.splitlines():
                if ln.startswith(("[", "CRITERIA=")): print("    · " + ln[:200])
            if p.stderr.strip(): print("    ! stderr: " + p.stderr.strip()[:300])
    print(f"SELFTEST={'PASS' if not bad else 'FAIL'}（{len(cases)-bad}/{len(cases)}；合成日志只验判据层三态："
          f"真空⇒NOINFO/3、被判对象红⇒FAIL/1、修后形态⇒PASS/0）")
    return 1 if bad else 0


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(selftest())
    text, rc = evaluate(sys.stdin.read())
    sys.stdout.write(text)
    sys.exit(rc)
