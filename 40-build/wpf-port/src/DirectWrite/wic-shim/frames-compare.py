#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""frames-compare.py —— 把探针读数与真值逐条对，出**逐例判词**（frames-check.sh 的判据本体）。

用法：frames-compare.py <truth.tsv> <probe.txt> <outdir> <stem> <fixture-name>
输出：一行 `PASS/FAIL/NOINFO ...`（逐条 H1..H5/D1/Z1 都印出来）
rc：  0 = 全 PASS ｜ 1 = 有 FAIL ｜ 3 = 无 FAIL 但有 NOINFO

【为什么要它】探针只"报事实"；判绿红必须**逐条**有依据：帧数一格、逐帧像素一格（逐字节）、
两两不同一格、越界一格、次序一格、延迟一格。少任何一格，"GetFrame(i>0) 返第 0 帧"
这种假修就能混过去（帧数对、状态码 0 ⇒ 只有**内容**那一格抓得住）。
"""
import os
import sys


def parse_probe(path):
    d = {"frame": {}, "getframe": {}, "delay": {}, "meta": {}, "raw": {}}
    for ln in open(path, encoding="utf-8", errors="replace"):
        ln = ln.strip()
        if ln.startswith("GETFRAMECOUNT_HR="):
            for kv in ln.split():
                k, _, v = kv.partition("=")
                if k == "COUNT":
                    d["count"] = int(v)
                if k == "GETFRAMECOUNT_HR":
                    d["count_hr"] = int(v)
        elif ln.startswith("OOR_INDEX="):   # 探针这一行是 `OOR_INDEX=<n> OOR_HR=<hr> OOR_HANDLE=...`
            for kv in ln.split():
                k, _, v = kv.partition("=")
                if k == "OOR_HR":
                    d["oor_hr"] = int(v)
                if k == "OOR_HANDLE":
                    d["oor_handle"] = v
        elif ln.startswith("GETFRAME i="):
            kv = dict(x.split("=", 1) for x in ln.split() if "=" in x)
            d["getframe"][(int(kv["i"]), int(kv["PASS"]))] = int(kv["HR"])
        elif ln.startswith("FRAME i="):
            kv = dict(x.split("=", 1) for x in ln.split() if "=" in x)
            d["frame"][(int(kv["i"]), int(kv["PASS"]))] = {
                "copy_hr": int(kv["COPY_HR"]),
                "size": kv.get("SIZE", ""),
                "first": kv.get("FIRST", ""),
            }
        elif ln.startswith("FRAMEMETA i="):
            kv = dict(x.split("=", 1) for x in ln.split() if "=" in x)
            d["meta"][int(kv["i"])] = int(kv["HR"])
        elif ln.startswith("DELAY i="):
            kv = dict(x.split("=", 1) for x in ln.split() if "=" in x)
            d["delay"][int(kv["i"])] = (int(kv["HR"]), int(kv.get("VT", "-1")), int(kv.get("VALUE", "-1")))
    return d


def read_truth(path):
    n = None
    rows = []
    for ln in open(path, encoding="utf-8"):
        ln = ln.rstrip("\n")
        if ln.startswith("#"):
            for tok in ln.split():
                if tok.startswith("frames="):
                    n = int(tok.split("=")[1])
            continue
        if not ln.strip():
            continue
        c = ln.split("\t")
        rows.append({"i": int(c[0]), "delay_cs": int(c[1]), "w": int(c[2]), "h": int(c[3]),
                     "x": int(c[4]), "y": int(c[5]), "disposal": int(c[6]),
                     "transparent": int(c[7]), "cw": int(c[8]), "ch": int(c[9])})
    return n if n is not None else len(rows), rows


def main():
    truth_tsv, probe_txt, outdir, stem, fixture = sys.argv[1:6]
    n, rows = read_truth(truth_tsv)
    p = parse_probe(probe_txt)
    notes = []
    parts = []

    # H1 帧数
    got = p.get("count", -1)
    if p.get("count_hr", -1) != 0:
        parts.append(f"H1=FAIL(count_hr={p.get('count_hr')})")
    elif got != n:
        parts.append(f"H1=FAIL(count={got} expect={n})")
    else:
        parts.append(f"H1=PASS(count={n})")

    # H2 逐帧内容（逐字节 vs 真值）：两趟都要一致
    h2bad = []
    for i in range(n):
        tr = os.path.join(os.path.dirname(truth_tsv), f"{stem}.f{i}.bgra")
        want = open(tr, "rb").read() if os.path.exists(tr) else None
        for pss in (0, 1):
            f = p["frame"].get((i, pss))
            if f is None or f["copy_hr"] != 0:
                h2bad.append(f"f{i}p{pss}:copy_hr={f['copy_hr'] if f else 'missing'}")
                continue
            got_file = os.path.join(outdir, f"{stem}.shimf{i}p1.bgra")
            if pss == 0:
                continue          # 倒序那趟不落盘（见探针注释），只在 H5 用 HR 判
            if want is None or not os.path.exists(got_file):
                h2bad.append(f"f{i}:no-dump")
                continue
            have = open(got_file, "rb").read()
            if have != want:
                nd = sum(1 for k in range(0, min(len(have), len(want)), 4) if have[k:k + 4] != want[k:k + 4])
                h2bad.append(f"f{i}:DIFF({nd}px,len {len(have)}/{len(want)})")
    parts.append("H2=PASS(逐字节)" if not h2bad else f"H2=FAIL({','.join(h2bad[:6])})")

    # H3 互不相同（>1 帧时）
    if n < 2:
        parts.append("H3=NA(单帧)")
    else:
        shas = {}
        for i in range(n):
            f = os.path.join(outdir, f"{stem}.shimf{i}p1.bgra")
            if os.path.exists(f):
                import hashlib
                shas[i] = hashlib.sha256(open(f, "rb").read()).hexdigest()[:16]
        dup = [f"{a}=={b}" for a in shas for b in shas if a < b and shas[a] == shas[b]]
        if len(shas) != n:
            parts.append(f"H3=FAIL(missing-frames {len(shas)}/{n})")
        elif dup:
            parts.append(f"H3=FAIL(帧内容重复: {','.join(dup)})")
        else:
            parts.append(f"H3=PASS(n={n} 两两不同)")

    # H4 越界必须失败
    oor = p.get("oor_hr", 0)
    if oor == 0:
        parts.append(f"H4=FAIL(GetFrame({n}) 竟然成功 handle={p.get('oor_handle')})")
    else:
        parts.append(f"H4=PASS(GetFrame({n}) hr={oor})")

    # H5 次序无关（**逐字节**比两趟的输出，不只比状态码）
    h5bad = []
    for i in range(n):
        a = os.path.join(outdir, f"{stem}.shimf{i}p0.bgra")
        c = os.path.join(outdir, f"{stem}.shimf{i}p1.bgra")
        if p["frame"].get((i, 0), {}).get("copy_hr") != p["frame"].get((i, 1), {}).get("copy_hr"):
            h5bad.append(f"f{i}:hr")
        elif not (os.path.exists(a) and os.path.exists(c)):
            h5bad.append(f"f{i}:no-dump")
        elif open(a, "rb").read() != open(c, "rb").read():
            h5bad.append(f"f{i}:bytes")
    parts.append("H5=PASS(倒序/正序逐字节相同)" if not h5bad else f"H5=FAIL({','.join(h5bad)})")

    # D1 帧时序（GIF）
    if fixture.endswith(".gif"):
        noinfo = [i for i in range(n) if p["meta"].get(i, -1) != 0]
        if noinfo:
            parts.append(f"D1=NOINFO(帧元数据读取器未实现，i={noinfo})")
            notes.append("delay-not-implemented")
        else:
            dbad = []
            for i in range(n):
                hr, vt, val = p["delay"].get(i, (-1, -1, -1))
                if hr != 0 or vt != 18 or val != rows[i]["delay_cs"]:
                    dbad.append(f"f{i}(hr={hr},vt={vt},val={val},expect={rows[i]['delay_cs']})")
            parts.append("D1=PASS(VT_UI2 逐帧相符)" if not dbad else f"D1=FAIL({','.join(dbad)})")
    else:
        parts.append("D1=NA(非 GIF)")

    verdict = "FAIL" if any("=FAIL" in x for x in parts) else ("NOINFO" if any("=NOINFO" in x for x in parts) else "PASS")
    print(f"{verdict} " + " ".join(parts))
    return 1 if verdict == "FAIL" else (3 if verdict == "NOINFO" else 0)


if __name__ == "__main__":
    sys.exit(main())
