# -*- coding: utf-8 -*-
"""LinuxDesktopPerfect 运行时本体校验器：逐条复跑 13 条判据，并生成单文件模型页。

用法： python3 本体/本体校验.py
判据全文见 本体/ontology.yaml 与 AGENTS.md「本体」条目。

只读 ontology.yaml 与设计文档，不改任何输入；唯一写出的文件是 本体/本体模型.html。
有 ✗ 退出码 1；全过打印 ALL_CHECKS_PASSED 退出码 0。
"""
import html
import json
import math
import re
import sys
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent          # 本体/
ROOT = HERE.parent                              # 项目根
YAML_PATH = HERE / "ontology.yaml"
HTML_PATH = HERE / "本体模型.html"

# 设计文档（判据 9 / 12 的回查目标）
DOCS = {
    "TG": ROOT / "30-design" / "设计文档包" / "08-判据先行与界面检测设计说明.md",
    "SAFE": ROOT / "30-design" / "设计文档包" / "06-安全性设计支撑材料.md",
    "ACC": ROOT / "20-spec" / "需求与验收判据文档.md",
    "ADR": ROOT / "30-design" / "概要设计.md",
    "DESIGN": ROOT / "30-design" / "概要设计.md",
}

TOP_SEGMENTS = ["ontology", "classes", "relations", "rules", "actions",
                "terms", "modules", "decisions", "excluded"]
LIST_SEGMENTS = TOP_SEGMENTS[1:]

PROP_TYPES = {"string", "integer", "float", "boolean", "datetime",
              "enum", "array", "object"}
CONFIDENCE_OK = {"medium", "low"}
DECISION_STATUS = {"已修", "已裁决", "已登记", "待裁决"}
SNAKE = re.compile(r"^[a-z][a-z0-9_]*$")
CJK_PRED = re.compile(r"^[\u4e00-\u9fff]{1,4}$")
ID_PATTERNS = {
    "ID_CLASS": re.compile(r"^C\d{3}$"),
    "ID_REL": re.compile(r"^RL\d{3}$"),
    "ID_RULE": re.compile(r"^R\d{3}$"),
    "ID_ACT": re.compile(r"^A\d{3}$"),
    "ID_DEC": re.compile(r"^D\d{2}$"),
}

results = []        # 供模型页「校验报告」页签使用
fails = []          # 失败明细


def esc(value):
    return html.escape(str(value if value is not None else ""), quote=True)


def record(symbol, code, title, detail=""):
    row = {"symbol": symbol, "code": code, "title": title, "detail": detail}
    results.append(row)
    line = "%s [%s] %s" % (symbol, code, title)
    if detail:
        line += "：" + detail
    print(line)
    if symbol == "✗":
        fails.append("[%s] %s%s" % (code, title, ("：" + detail) if detail else ""))
    return row


def ck(cond, code, title, detail_ok="", detail_fail=""):
    """条件成立记 ✓，否则记 ✗。"""
    if cond:
        return record("✓", code, title, detail_ok)
    return record("✗", code, title, detail_fail or detail_ok)


def na(code, title, detail=""):
    return record("–", code, title, detail)


def brief(items, limit=5):
    items = list(items)
    if not items:
        return ""
    head = "、".join(str(x) for x in items[:limit])
    return head + ("…" if len(items) > limit else "")


def strip_all(text):
    return re.sub(r"\s+", "", str(text or ""))


def read_doc(key):
    path = DOCS[key]
    if not path.is_file():
        return None
    return path.read_text(encoding="utf-8")


# ============================================================
# 判据 1~12
# ============================================================
def run_checks():
    # ---------- 1. 可解析 + 顶层段 ----------
    if not YAML_PATH.is_file():
        record("✗", 1, "YAML 可解析", "找不到 %s" % YAML_PATH)
        return None
    try:
        data = yaml.safe_load(YAML_PATH.read_text(encoding="utf-8"))
    except Exception as exc:                        # noqa: BLE001
        record("✗", 1, "YAML 可解析", "PyYAML 解析失败：%s" % exc)
        return None
    if not isinstance(data, dict):
        record("✗", 1, "YAML 可解析", "顶层不是映射")
        return None

    missing = [k for k in TOP_SEGMENTS if k not in data]
    bad_type = [k for k in LIST_SEGMENTS
                if k in data and not (isinstance(data[k], list) and data[k])]
    if missing or bad_type or not isinstance(data.get("ontology"), dict):
        detail = []
        if missing:
            detail.append("缺段 " + brief(missing))
        if bad_type:
            detail.append("段为空或非列表 " + brief(bad_type))
        if not isinstance(data.get("ontology"), dict):
            detail.append("ontology 不是映射")
        record("✗", 1, "顶层段齐全", "；".join(detail))
    else:
        record("✓", 1, "顶层段齐全", "%d 段：%s" % (len(TOP_SEGMENTS), " / ".join(TOP_SEGMENTS)))

    classes = data.get("classes") or []
    relations = data.get("relations") or []
    rules = data.get("rules") or []
    actions = data.get("actions") or []
    terms = data.get("terms") or []
    modules = data.get("modules") or []
    decisions = data.get("decisions") or []
    excluded = data.get("excluded") or []

    class_ids = set()
    for c in classes:
        cid = str(c.get("id") or "").strip()
        if cid:
            class_ids.add(cid)

    # ---------- 2. 类 id 连续 + 必填字段 ----------
    ids = [str(c.get("id") or "").strip() for c in classes]
    expect_ids = ["C%03d" % i for i in range(1, len(classes) + 1)]
    dup = sorted({x for x in ids if ids.count(x) > 1})
    bad_fmt = [x for x in ids if not ID_PATTERNS["ID_CLASS"].match(x)]
    fields = ("id_en", "name_zh", "source", "description")
    miss_fields = [(str(c.get("id")), f) for c in classes
                   for f in fields if not str(c.get(f) or "").strip()]
    if ids == expect_ids and not dup and not bad_fmt and not miss_fields:
        record("✓", 2, "类 id 连续且字段齐全",
               "C001..C%03d 共 %d 类，id_en/name_zh/source/description 均有值" % (len(classes), len(classes)))
    else:
        detail = []
        if ids != expect_ids:
            detail.append("id 序列不连续（期望 C001..C%03d，实测 %s）" % (len(classes), brief(ids)))
        if dup:
            detail.append("重复 " + brief(dup))
        if bad_fmt:
            detail.append("格式不合法 " + brief(bad_fmt))
        if miss_fields:
            detail.append("缺字段 " + brief("%s.%s" % pair for pair in miss_fields))
        record("✗", 2, "类 id 连续且字段齐全", "；".join(detail))

    # ---------- 3. properties 形态 ----------
    n_props = 0
    n_enum = 0
    prop_bad = []
    for c in classes:
        cid = str(c.get("id"))
        props = c.get("properties") or []
        if not props:
            prop_bad.append("%s 无 properties" % cid)
            continue
        for p in props:
            n_props += 1
            key = str(p.get("key") or "")
            ptype = p.get("type")
            if not SNAKE.match(key):
                prop_bad.append("%s.%s key 非 snake_case" % (cid, key))
            if ptype not in PROP_TYPES:
                prop_bad.append("%s.%s type=%s 不合法" % (cid, key, ptype))
            if ptype == "enum":
                n_enum += 1
                enums = p.get("enum_values")
                if not (isinstance(enums, list) and enums and all(str(x).strip() for x in enums)):
                    prop_bad.append("%s.%s enum 缺 enum_values" % (cid, key))
    ck(not prop_bad, 3, "类属性形态",
       "%d 条属性（%d 条 enum），key 全 snake_case，type 全在 8 种内" % (n_props, n_enum),
       "；".join(brief(prop_bad)))

    # ---------- 4. 关系形态 ----------
    rids = [str(r.get("id") or "").strip() for r in relations]
    expect_rids = ["RL%03d" % i for i in range(1, len(relations) + 1)]
    rel_bad = []
    for r in relations:
        rid = str(r.get("id"))
        if str(r.get("from")) not in class_ids:
            rel_bad.append("%s from=%s 未定义类" % (rid, r.get("from")))
        if str(r.get("to")) not in class_ids:
            rel_bad.append("%s to=%s 未定义类" % (rid, r.get("to")))
        pred = str(r.get("predicate") or "").strip()
        if not pred or not CJK_PRED.match(pred):
            rel_bad.append("%s predicate=「%s」非 1~4 汉字" % (rid, pred))
    if rids == expect_rids and not rel_bad:
        record("✓", 4, "关系 id 连续、端点与谓语合法",
               "RL001..RL%03d 共 %d 条，from/to 均在 24 类内，谓语均 ≤4 汉字" % (len(relations), len(relations)))
    else:
        detail = []
        if rids != expect_rids:
            detail.append("id 序列不连续（实测 %s）" % brief(rids))
        detail.extend(rel_bad)
        record("✗", 4, "关系 id 连续、端点与谓语合法", "；".join(brief(detail, 6)))

    # ---------- 5. 规则形态与置信度 ----------
    rule_ids = [str(r.get("id") or "").strip() for r in rules]
    expect_rule_ids = ["R%03d" % i for i in range(1, len(rules) + 1)]
    rule_bad = []
    conf_stat = {"medium": 0, "low": 0, "high": 0, "other": 0}
    for r in rules:
        rid = str(r.get("id"))
        for f in ("condition", "then", "source", "confidence"):
            if not str(r.get(f) or "").strip():
                rule_bad.append("%s 缺 %s" % (rid, f))
        conf = str(r.get("confidence") or "").strip()
        if conf in conf_stat:
            conf_stat[conf] += 1
        else:
            conf_stat["other"] += 1
        if conf not in CONFIDENCE_OK:
            rule_bad.append("%s confidence=%s（只允许 medium/low）" % (rid, conf))
    ck(rule_ids == expect_rule_ids and not rule_bad, 5, "规则 id 连续、字段齐全、无 high 置信度",
       "R001..R%03d 共 %d 条；confidence：medium %d / low %d / high %d"
       % (len(rules), len(rules), conf_stat["medium"], conf_stat["low"], conf_stat["high"]),
       "；".join(brief(rule_bad, 6)))

    # ---------- 6. 冲突组：conflict_reason + 裁决提及 ----------
    cg_rules = [r for r in rules if str(r.get("conflict_group") or "").strip()]
    if not cg_rules:
        na(6, "冲突组一致", "本体内没有 conflict_group，跳过")
        cgs = []
    else:
        groups = sorted({str(r["conflict_group"]).strip() for r in cg_rules})
        cgs = groups
        no_reason = [str(r.get("id")) for r in cg_rules
                     if not str(r.get("conflict_reason") or "").strip()]
        dec_blob = "\n".join(
            "%s %s %s" % (d.get("id"), d.get("topic") or "", d.get("detail") or "")
            for d in decisions)
        uncovered = [g for g in groups if g not in dec_blob]
        parts = []
        if no_reason:
            parts.append("缺 conflict_reason：" + brief(no_reason))
        if uncovered:
            hints = []
            for g in uncovered:
                refs = sorted({
                    did
                    for r in cg_rules
                    if str(r.get("conflict_group")).strip() == g
                    for did in re.findall(r"D\d{2}", str(r.get("conflict_reason") or ""))
                })
                hints.append("%s（其 conflict_reason 指向 %s，请在裁决 detail/topic 里写组名）"
                             % (g, "、".join(refs) or "未写裁决号"))
            parts.append("未在 decisions 的 detail/topic 提及：" + "；".join(hints))
        if parts:
            record("✗", 6, "冲突组有原因且在裁决中被提及", "；".join(parts))
        else:
            record("✓", 6, "冲突组有原因且在裁决中被提及",
                   "%d 个组（%s）均有 conflict_reason，且都在裁决里被点名"
                   % (len(groups), "、".join(groups)))

    # ---------- 7. 动作形态 ----------
    aids = [str(a.get("id") or "").strip() for a in actions]
    expect_aids = ["A%03d" % i for i in range(1, len(actions) + 1)]
    act_bad = []
    for a in actions:
        aid = str(a.get("id"))
        if str(a.get("target")) not in class_ids:
            act_bad.append("%s target=%s 未定义类" % (aid, a.get("target")))
        hint = a.get("api_hint")
        if hint is None or not (str(hint) == "" or str(hint).startswith("IF-")):
            act_bad.append("%s api_hint=「%s」须为空或 IF- 开头" % (aid, hint))
    ck(aids == expect_aids and not act_bad, 7, "动作 id 连续、target 与 api_hint 合法",
       "A001..A%03d 共 %d 条，target 均在 24 类内，api_hint 为空或 IF- 开头" % (len(actions), len(actions)),
       "；".join(brief(act_bad, 6)))

    # ---------- 8. 术语唯一性 ----------
    canon = [strip_all(t.get("canonical")) for t in terms]
    canon_set = set(canon)
    dup_canon = sorted({x for x in canon if canon.count(x) > 1})
    variant_bad = []
    for t in terms:
        own = strip_all(t.get("canonical"))
        for v in (t.get("variants") or []):
            sv = strip_all(v)
            if sv and sv in canon_set and sv != own:
                variant_bad.append("%s 的异名「%s」等于另一条术语的首选名" % (own, sv))
    ck(not dup_canon and not variant_bad, 8, "术语首选名唯一、异名不撞首选名",
       "%d 条术语，首选名无重复，%d 条异名均未与首选名重合"
       % (len(terms), sum(len(t.get("variants") or []) for t in terms)),
       "；".join(brief(dup_canon + variant_bad, 6)))

    # ---------- 9. 模块覆盖 概要设计 的 MOD ----------
    design_text = read_doc("DESIGN")
    if design_text is None:
        record("✗", 9, "模块覆盖概设 MOD", "读不到 30-design/概要设计.md")
    else:
        doc_mods = set(re.findall(r"MOD-\d{2}", design_text))
        yaml_mods = [str(m.get("mod") or "").strip() for m in modules]
        yaml_set = set(yaml_mods)
        dup_mod = sorted({x for x in yaml_mods if yaml_mods.count(x) > 1})
        missing_mod = sorted(doc_mods - yaml_set)
        extra_mod = sorted(yaml_set - doc_mods)
        cls_bad = [str(m.get("mod")) for m in modules
                   if any(str(c) not in class_ids for c in (m.get("classes") or []))]
        if not dup_mod and not missing_mod and not extra_mod and not cls_bad:
            record("✓", 9, "模块覆盖概设 MOD",
                   "概设 %d 个 MOD，本体 %d 个，不多不少；classes 引用全部有效"
                   % (len(doc_mods), len(yaml_mods)))
        else:
            parts = []
            if missing_mod:
                parts.append("缺 " + brief(missing_mod))
            if extra_mod:
                parts.append("多 " + brief(extra_mod))
            if dup_mod:
                parts.append("重复 " + brief(dup_mod))
            if cls_bad:
                parts.append("classes 引用未定义类：" + brief(cls_bad))
            record("✗", 9, "模块覆盖概设 MOD", "；".join(parts))

    # ---------- 10. 裁决形态 ----------
    dids = [str(d.get("id") or "").strip() for d in decisions]
    expect_dids = ["D%02d" % i for i in range(1, len(decisions) + 1)]
    dec_bad = []
    for d in decisions:
        did = str(d.get("id"))
        if str(d.get("status") or "").strip() not in DECISION_STATUS:
            dec_bad.append("%s status=%s 不在四值内" % (did, d.get("status")))
        if not str(d.get("detail") or "").strip():
            dec_bad.append("%s 缺 detail" % did)
    ck(dids == expect_dids and not dec_bad, 10, "裁决 id 连续、状态四值、detail 非空",
       "D01..D%02d 共 %d 条；状态分布 %s"
       % (len(decisions), len(decisions),
          "、".join("%s %d" % (s, sum(1 for d in decisions if str(d.get("status")) == s))
                   for s in sorted(DECISION_STATUS))),
       "；".join(brief(dec_bad, 6)))

    # ---------- 11. 排除清单 ----------
    exc_bad = []
    for x in excluded:
        if not str(x.get("concept") or "").strip():
            exc_bad.append("某条缺 concept")
        if not str(x.get("reason") or "").strip():
            exc_bad.append("%s 缺 reason" % (x.get("concept") or "?"))
    ck(bool(excluded) and not exc_bad, 11, "排除清单非空且 concept/reason 齐全",
       "%d 条排除，均有 concept 与 reason" % len(excluded),
       "；".join(brief(exc_bad, 6)) if exc_bad else "列表为空")

    # ---------- 12. 规则来源的编号回查 ----------
    source_blob = "\n".join(str(r.get("source") or "") for r in rules)
    patterns = {
        "TG": r"TG-\d{3}",
        "SAFE": r"SAFE-\d{3}",
        "ACC": r"ACC-\d{3}",
        "ADR": r"ADR-\d{3}",
    }
    families = {}
    dangling = []
    for key, pat in patterns.items():
        ids = sorted(set(re.findall(pat, source_blob)))
        families[key] = ids
        if not ids:
            continue
        text = read_doc(key)
        if text is None:
            dangling.append("%s 定义方文档读不到" % key)
            continue
        for i in ids:
            if i not in text:
                dangling.append("%s（%s 无定义）" % (i, DOCS[key].name))
    total_refs = sum(len(v) for v in families.values())
    if total_refs == 0:
        na(12, "规则来源编号可回查", "source 里没有 TG/SAFE/ACC/ADR 编号")
    else:
        ck(not dangling, 12, "规则来源编号可回查",
           "TG %d / SAFE %d / ACC %d / ADR %d 共 %d 个引用，全部在定义方出现"
           % (len(families["TG"]), len(families["SAFE"]),
              len(families["ACC"]), len(families["ADR"]), total_refs),
           "；".join(brief(dangling, 6)))

    summary = {
        "classes": len(classes),
        "relations": len(relations),
        "rules": len(rules),
        "rules_medium": conf_stat["medium"],
        "rules_low": conf_stat["low"],
        "actions": len(actions),
        "terms": len(terms),
        "modules": len(modules),
        "decisions": len(decisions),
        "excluded": len(excluded),
        "properties": n_props,
        "enums": n_enum,
        "conflict_groups": len(cgs),
        "refs": total_refs,
    }
    return {
        "data": data,
        "classes": classes,
        "relations": relations,
        "rules": rules,
        "actions": actions,
        "terms": terms,
        "modules": modules,
        "decisions": decisions,
        "excluded": excluded,
        "conflict_groups": cgs,
        "summary": summary,
    }


# ============================================================
# 模型页：单文件、无外部依赖、无 http(s)
# ============================================================
CSS = """
:root{
  --bg:#0d1117;--bg2:#131a23;--panel:#161e28;--line:#26313d;--line2:#33414f;
  --text:#e6edf3;--dim:#9aa9b9;--faint:#6c7c8d;--accent:#4ea1ff;
  --ok:#3fb950;--warn:#d29922;--bad:#f85149;--radius:10px;
}
*{box-sizing:border-box}
html,body{margin:0;padding:0;background:var(--bg);color:var(--text)}
body{font:14px/1.6 -apple-system,"Segoe UI","Noto Sans CJK SC",sans-serif}
.wrap{max-width:1320px;margin:0 auto;padding:22px 20px 60px}
h1{font-size:24px;margin:6px 0 4px}
h2{font-size:16px;margin:22px 0 10px}
h3{font-size:14px;margin:0 0 8px;color:var(--dim);font-weight:600}
code,.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12.5px}
.crumb{color:var(--faint);font-size:12px}
.lead{color:var(--dim);margin:2px 0 10px;max-width:1000px}
.meta{display:flex;gap:10px;align-items:center;flex-wrap:wrap;color:var(--faint);font-size:12.5px;margin-bottom:6px}
.verdict{display:inline-block;padding:3px 12px;border-radius:999px;font-weight:700;font-size:12.5px}
.verdict.ok{color:var(--ok);border:1px solid #2ea04366;background:#2ea0431f}
.verdict.bad{color:var(--bad);border:1px solid #f8514966;background:#f851491f}
.stats{display:flex;gap:10px;flex-wrap:wrap;margin:14px 0 6px}
.stat{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:8px 14px;min-width:92px}
.stat b{display:block;font-size:21px;line-height:1.25}
.stat span{color:var(--faint);font-size:12px}
.tabs{display:flex;gap:6px;flex-wrap:wrap;margin:18px 0 0;border-bottom:1px solid var(--line)}
.tabs button{background:transparent;border:0;border-bottom:2px solid transparent;color:var(--dim);
  padding:9px 14px;font-size:13.5px;cursor:pointer;font-family:inherit}
.tabs button[aria-selected="true"]{color:var(--text);border-bottom-color:var(--accent);font-weight:700}
.panel{display:none;padding-top:18px}
.panel.on{display:block}
.graph-wrap{display:grid;grid-template-columns:minmax(0,1fr) 390px;gap:18px;align-items:start}
@media (max-width:1060px){.graph-wrap{grid-template-columns:minmax(0,1fr)}}
#graph{width:100%;height:auto;background:var(--bg2);border:1px solid var(--line);border-radius:var(--radius)}
#graph .ring{fill:none;stroke:var(--line);stroke-dasharray:3 6}
#graph .edge line{stroke:var(--line2);stroke-width:1.1}
#graph .edge.on line{stroke:var(--accent);stroke-width:2.4}
#graph .node{cursor:pointer}
#graph .node circle{fill:var(--panel);stroke:var(--line2);stroke-width:1.6}
#graph .node .nid{fill:var(--text);font-size:11px;font-family:ui-monospace,monospace;pointer-events:none}
#graph .node .nname{fill:var(--faint);font-size:11px;pointer-events:none}
#graph .node.sel circle{fill:var(--accent);stroke:var(--text);stroke-width:2.4}
#graph .node.sel .nid{fill:#04121f;font-weight:700}
#graph .node.sel .nname{fill:var(--text)}
aside.side{position:sticky;top:12px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:14px 16px;margin-bottom:12px}
.card .en{color:var(--faint);font-size:12px;font-family:ui-monospace,monospace}
.card .desc{color:var(--dim);font-size:12.5px;margin:8px 0 10px}
.prop{display:grid;grid-template-columns:max-content 1fr;gap:2px 10px;font-size:12.5px;margin:6px 0;padding:6px 8px;background:var(--bg2);border-radius:6px}
.prop .k{font-family:ui-monospace,monospace;color:var(--text)}
.prop .t{color:var(--warn);font-size:11.5px}
.prop .d{color:var(--dim);grid-column:2}
.pill{display:inline-block;padding:1px 8px;border-radius:999px;font-size:11.5px;border:1px solid var(--line2);color:var(--dim)}
.pill.medium{color:var(--accent);border-color:#4ea1ff66}
.pill.low{color:var(--warn);border-color:#d2992266}
.pill.cg{color:var(--warn);border-color:#d2992266}
.pill.st-已修{color:var(--ok);border-color:#3fb95066}
.pill.st-已裁决{color:var(--accent);border-color:#4ea1ff66}
.pill.st-已登记{color:var(--dim)}
.pill.st-待裁决{color:var(--warn);border-color:#d2992266}
table{width:100%;border-collapse:collapse;font-size:12.5px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--faint);font-weight:600;font-size:12px;white-space:nowrap}
tbody tr:hover{background:#1c2632}
.toolbar{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:14px}
.toolbar button{background:var(--panel);border:1px solid var(--line);color:var(--dim);border-radius:999px;
  padding:5px 14px;font-size:12.5px;cursor:pointer;font-family:inherit}
.toolbar button.on{color:var(--accent);border-color:var(--accent)}
.toolbar input{background:var(--panel);border:1px solid var(--line);color:var(--text);
  border-radius:8px;padding:6px 11px;font-size:13px;min-width:260px;font-family:inherit}
#rules{display:grid;grid-template-columns:repeat(auto-fill,minmax(360px,1fr));gap:10px}
.rule{background:var(--panel);border:1px solid var(--line);border-left:3px solid var(--line2);border-radius:0 var(--radius) var(--radius) 0;padding:11px 13px}
.rule.cg{border-left-color:var(--warn)}
.rule .head{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-bottom:6px}
.rule .rid{font-family:ui-monospace,monospace;font-size:12.5px;color:var(--text)}
.rule .cond{font-family:ui-monospace,monospace;font-size:12.5px;color:var(--accent);word-break:break-word}
.rule .then{color:var(--dim);font-size:12.5px;margin-top:5px}
.rule .src{color:var(--faint);font-size:11.5px;margin-top:7px;word-break:break-all}
.chk{font-family:ui-monospace,monospace;font-size:12.5px;padding:5px 0;border-bottom:1px dashed var(--line)}
.chk .sym{display:inline-block;width:18px;font-weight:700}
.chk.ok .sym{color:var(--ok)}
.chk.bad .sym{color:var(--bad)}
.chk.na .sym{color:var(--faint)}
.chk .note{color:var(--dim)}
.hint{color:var(--faint);font-size:12px;margin:8px 0 0}
.scroll{max-height:330px;overflow:auto}
"""

JS = r"""
(function () {
  var tabs = Array.prototype.slice.call(document.querySelectorAll('[data-tab]'));
  var panels = Array.prototype.slice.call(document.querySelectorAll('[data-panel]'));
  function showTab(name) {
    tabs.forEach(function (t) { t.setAttribute('aria-selected', String(t.dataset.tab === name)); });
    panels.forEach(function (p) { p.classList.toggle('on', p.dataset.panel === name); });
  }
  tabs.forEach(function (t) { t.addEventListener('click', function () { showTab(t.dataset.tab); }); });

  var nodes = Array.prototype.slice.call(document.querySelectorAll('#graph .node'));
  var edges = Array.prototype.slice.call(document.querySelectorAll('#graph .edge'));
  var details = Array.prototype.slice.call(document.querySelectorAll('.cls-detail'));
  function select(id) {
    nodes.forEach(function (n) { n.classList.toggle('sel', n.dataset.classId === id); });
    edges.forEach(function (e) {
      e.classList.toggle('on', e.dataset.from === id || e.dataset.to === id);
    });
    details.forEach(function (d) { d.hidden = d.dataset.cls !== id; });
  }
  nodes.forEach(function (n) {
    n.addEventListener('click', function () { select(n.dataset.classId); });
    n.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); select(n.dataset.classId); }
    });
  });
  if (nodes.length) { select(nodes[0].dataset.classId); }

  var confBtns = Array.prototype.slice.call(document.querySelectorAll('[data-conf]'));
  var search = document.getElementById('rule-search');
  function applyRuleFilter() {
    var active = 'all';
    confBtns.forEach(function (b) { if (b.classList.contains('on')) { active = b.dataset.conf; } });
    var q = (search && search.value ? search.value : '').trim().toLowerCase();
    Array.prototype.forEach.call(document.querySelectorAll('#rules .rule'), function (el) {
      var okC = active === 'all' || el.dataset.confidence === active;
      var okQ = !q || el.textContent.toLowerCase().indexOf(q) >= 0;
      el.hidden = !(okC && okQ);
    });
  }
  confBtns.forEach(function (b) {
    b.addEventListener('click', function () {
      confBtns.forEach(function (x) { x.classList.remove('on'); });
      b.classList.add('on');
      applyRuleFilter();
    });
  });
  if (search) { search.addEventListener('input', applyRuleFilter); }
})();
"""


def svg_graph(classes, relations):
    n = len(classes)
    cx, cy, radius = 410.0, 410.0, 292.0
    pos = {}
    for i, c in enumerate(classes):
        ang = -math.pi / 2 + (2 * math.pi * i / n if n else 0)
        pos[str(c["id"])] = (cx + radius * math.cos(ang), cy + radius * math.sin(ang))

    out = [
        '<svg id="graph" viewBox="0 0 820 820" role="img" aria-label="类与关系环形图">',
        '<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" '
        'markerHeight="6" orient="auto-start-reverse">'
        '<path d="M0,0 L10,5 L0,10 z" fill="#5b6b7c"/></marker></defs>',
        '<circle class="ring" cx="410" cy="410" r="292"/>',
    ]
    label = {str(c["id"]): str(c.get("id_en") or c["id"]) for c in classes}
    for r in relations:
        a, b = str(r.get("from")), str(r.get("to"))
        if a not in pos or b not in pos:
            continue
        x1, y1 = pos[a]
        x2, y2 = pos[b]
        dx, dy = x2 - x1, y2 - y1
        dist = math.hypot(dx, dy) or 1.0
        ux, uy = dx / dist, dy / dist
        sx, sy = x1 + ux * 17, y1 + uy * 17
        ex, ey = x2 - ux * 21, y2 - uy * 21
        tip = "%s：%s %s %s — %s" % (r.get("id"), label.get(a, a),
                                     r.get("predicate") or "", label.get(b, b),
                                     r.get("description") or "")
        out.append(
            '<g class="edge" data-rel-id="%s" data-from="%s" data-to="%s">'
            "<title>%s</title>"
            '<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" marker-end="url(#arrow)"/>'
            "</g>" % (esc(r.get("id")), esc(a), esc(b), esc(tip), sx, sy, ex, ey)
        )
    for c in classes:
        cid = str(c["id"])
        x, y = pos[cid]
        ang = math.atan2(y - cy, x - cx)
        lx, ly = cx + (radius + 38) * math.cos(ang), cy + (radius + 38) * math.sin(ang)
        cosv = math.cos(ang)
        anchor = "middle" if -0.35 <= cosv <= 0.35 else ("start" if cosv > 0 else "end")
        tip = "%s %s %s — %s" % (cid, c.get("id_en") or "", c.get("name_zh") or "",
                                 c.get("description") or "")
        out.append(
            '<g class="node" data-class-id="%s" tabindex="0" role="button" aria-label="%s">'
            "<title>%s</title>"
            '<circle cx="%.1f" cy="%.1f" r="15"/>'
            '<text class="nid" x="%.1f" y="%.1f" text-anchor="middle">%s</text>'
            '<text class="nname" x="%.1f" y="%.1f" text-anchor="%s">%s</text>'
            "</g>" % (esc(cid), esc(c.get("name_zh") or cid), esc(tip), x, y, x, y + 4.2,
                      esc(cid), lx, ly + 4.2, anchor, esc(c.get("name_zh") or ""))
        )
    out.append("</svg>")
    return "".join(out)


def render_detail(c, index=0):
    rows = []
    for p in (c.get("properties") or []):
        extra = ""
        if p.get("type") == "enum":
            extra = " = {%s}" % ", ".join(esc(v) for v in (p.get("enum_values") or []))
        rows.append(
            '<div class="prop"><span class="k">%s</span><span class="t">%s%s</span>'
            '<span class="d">%s</span></div>'
            % (esc(p.get("key")), esc(p.get("type")), extra, esc(p.get("description")))
        )
    return (
        '<div class="card cls-detail" data-cls="%s"%s>'
        '<h3>%s <span class="en">%s</span></h3>'
        '<div class="desc">%s</div>'
        '<div class="en">source：%s</div>'
        "<div>%s</div></div>"
        % (esc(c.get("id")), "" if index == 0 else " hidden",
           esc(c.get("name_zh")), esc(c.get("id_en")),
           esc(c.get("description")), esc(c.get("source")), "".join(rows))
    )


def build_html(bundle, check_rows, verdict, verdict_class, meta):
    classes = bundle["classes"]
    relations = bundle["relations"]
    rules = bundle["rules"]
    actions = bundle["actions"]
    terms = bundle["terms"]
    modules = bundle["modules"]
    decisions = bundle["decisions"]
    excluded = bundle["excluded"]
    summary = bundle["summary"]

    payload = {
        "meta": meta,
        "counts": summary,
        "classes": classes,
        "relations": relations,
        "rules": rules,
        "actions": actions,
        "terms": terms,
        "modules": modules,
        "decisions": decisions,
        "excluded": excluded,
        "conflictGroups": bundle["conflict_groups"],
        "checks": check_rows,
        "generatedFrom": "本体/ontology.yaml",
    }
    blob = json.dumps(payload, ensure_ascii=False).replace("</", "<\\/")

    stats = [
        (summary["classes"], "类"),
        (summary["relations"], "关系"),
        (summary["rules"], "规则"),
        (summary["actions"], "动作"),
        (summary["terms"], "术语"),
        (summary["modules"], "模块"),
        (summary["decisions"], "裁决"),
        (summary["excluded"], "排除"),
        (summary["conflict_groups"], "冲突组"),
    ]
    stats_html = "".join('<div class="stat"><b>%d</b><span>%s</span></div>' % (n, esc(t))
                         for n, t in stats)

    # 关系表
    name_of = {str(c["id"]): "%s %s" % (c.get("id_en") or "", c.get("name_zh") or "")
               for c in classes}
    rel_rows = "".join(
        "<tr><td><code>%s</code></td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
        % (esc(r.get("id")), esc(name_of.get(str(r.get("from")), r.get("from"))),
           esc(r.get("predicate")), esc(name_of.get(str(r.get("to")), r.get("to"))),
           esc(r.get("description")))
        for r in relations
    )
    mod_rows = "".join(
        "<tr><td><code>%s</code></td><td>%s</td><td>%s</td></tr>"
        % (esc(m.get("mod")), esc(m.get("name")),
           "、".join("<code>%s</code>" % esc(c) for c in (m.get("classes") or [])))
        for m in modules
    )

    details_html = "".join(render_detail(c, i) for i, c in enumerate(classes))

    rule_cards = []
    for r in rules:
        cg = str(r.get("conflict_group") or "")
        pills = '<span class="pill %s">%s</span>' % (esc(r.get("confidence")), esc(r.get("confidence")))
        if cg:
            pills += ' <span class="pill cg">%s</span>' % esc(cg)
        reason = ""
        if cg:
            reason = '<div class="then"><em>冲突原因：%s</em></div>' % esc(r.get("conflict_reason"))
        rule_cards.append(
            '<article class="rule%s" data-rule-id="%s" data-confidence="%s">'
            '<div class="head"><span class="rid">%s</span>%s</div>'
            '<div class="cond">%s</div><div class="then">→ %s</div>%s'
            '<div class="src">source：%s</div></article>'
            % (" cg" if cg else "", esc(r.get("id")), esc(r.get("confidence")),
               esc(r.get("id")), pills, esc(r.get("condition")), esc(r.get("then")),
               reason, esc(r.get("source")))
        )
    rules_html = "".join(rule_cards)

    act_rows = "".join(
        "<tr><td><code>%s</code></td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"
        % (esc(a.get("id")), esc(a.get("name")), esc(a.get("trigger")),
           esc(name_of.get(str(a.get("target")), a.get("target")) or a.get("target")),
           esc(a.get("api_hint") or "（无）"))
        for a in actions
    )
    term_rows = "".join(
        "<tr><td><b>%s</b></td><td>%s</td><td>%s</td></tr>"
        % (esc(t.get("canonical")),
           "、".join(esc(v) for v in (t.get("variants") or [])) or "—",
           "、".join(esc(s) for s in (t.get("sources") or [])) or "—")
        for t in terms
    )
    dec_rows = "".join(
        "<tr><td><code>%s</code></td><td>%s</td>"
        '<td><span class="pill st-%s">%s</span></td><td>%s</td></tr>'
        % (esc(d.get("id")), esc(d.get("topic")), esc(d.get("status")),
           esc(d.get("status")), esc(d.get("detail")))
        for d in decisions
    )
    exc_rows = "".join(
        "<tr><td>%s</td><td>%s</td></tr>" % (esc(x.get("concept")), esc(x.get("reason")))
        for x in excluded
    )

    check_rows_html = "".join(
        '<div class="chk %s"><span class="sym">%s</span>[%s] %s%s</div>'
        % ("ok" if row["symbol"] == "✓" else ("bad" if row["symbol"] == "✗" else "na"),
           esc(row["symbol"]), esc(row["code"]), esc(row["title"]),
           (' <span class="note">%s</span>' % esc(row["detail"])) if row["detail"] else "")
        for row in check_rows
    )
    summary_rows = "".join(
        "<tr><td>%s</td><td>%s</td></tr>" % (esc(k), esc(v))
        for k, v in [
            ("类", summary["classes"]),
            ("关系", summary["relations"]),
            ("属性（含 enum）", "%d（%d）" % (summary["properties"], summary["enums"])),
            ("规则", "%d（medium %d / low %d）"
             % (summary["rules"], summary["rules_medium"], summary["rules_low"])),
            ("动作", summary["actions"]),
            ("术语", summary["terms"]),
            ("模块", summary["modules"]),
            ("裁决", summary["decisions"]),
            ("排除", summary["excluded"]),
            ("冲突组", summary["conflict_groups"]),
            ("来源编号引用", summary["refs"]),
        ]
    )

    parts = [
        "<!DOCTYPE html>",
        '<html lang="zh-CN">',
        "<head>",
        '<meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width, initial-scale=1">',
        "<title>%s · 运行时本体模型</title>" % esc(meta["name"]),
        "<style>%s</style>" % CSS,
        "</head>",
        "<body>",
        '<div class="wrap">',
        '<header class="page-head">',
        '<div class="crumb">本体 · LinuxDesktopPerfect 运行时本体 · 由 本体校验.py 生成，请勿手改</div>',
        "<h1>%s</h1>" % esc(meta["name"]),
        '<p class="lead">%s</p>' % esc(meta["domain"]),
        '<div class="meta"><span class="verdict %s">%s</span>'
        "<span>版本 %s</span><span>built_at %s</span>"
        "<span>数据源 本体/ontology.yaml</span></div>"
        % (verdict_class, esc(verdict), esc(meta["version"]), esc(meta["built_at"])),
        '<div class="stats">%s</div>' % stats_html,
        "</header>",
        '<nav class="tabs" role="tablist">',
        '<button data-tab="graph" aria-selected="true">类与关系</button>',
        '<button data-tab="rules" aria-selected="false">规则</button>',
        '<button data-tab="actions" aria-selected="false">动作与术语</button>',
        '<button data-tab="decisions" aria-selected="false">裁决与排除</button>',
        '<button data-tab="checks" aria-selected="false">校验报告</button>',
        "</nav>",
        # ---- 类与关系 ----
        '<section class="panel on" data-panel="graph">',
        '<div class="graph-wrap"><div>',
        svg_graph(classes, relations),
        '<p class="hint">点节点看该类的属性、source 与关联边；边只表示 relations 段的 from→to，'
        "介质与字段查 04-接口设计说明。悬停节点或边可看 <code>title</code> 提示。</p>",
        "</div><aside class=\"side\">",
        details_html,
        '<div class="card"><h3>关系（%d）</h3><div class="scroll"><table>'
        "<thead><tr><th>ID</th><th>from</th><th>谓语</th><th>to</th><th>说明</th></tr></thead>"
        "<tbody>%s</tbody></table></div></div>" % (len(relations), rel_rows),
        '<div class="card"><h3>模块 → 类（%d）</h3><div class="scroll"><table>'
        "<thead><tr><th>MOD</th><th>名称</th><th>承担类</th></tr></thead>"
        "<tbody>%s</tbody></table></div></div>" % (len(modules), mod_rows),
        "</aside></div></section>",
        # ---- 规则 ----
        '<section class="panel" data-panel="rules">',
        '<div class="toolbar">',
        '<button data-conf="all" class="on">全部 %d</button>' % len(rules),
        '<button data-conf="medium">medium %d</button>' % summary["rules_medium"],
        '<button data-conf="low">low %d</button>' % summary["rules_low"],
        '<input id="rule-search" type="search" placeholder="按条件 / 后果 / 来源过滤">',
        "</div>",
        '<div id="rules">%s</div>' % rules_html,
        '<p class="hint">置信度只有 medium / low：本项目尚无任何实测判据读数，'
        "规则全部由设计文档推导，没有一条被运行验证过。</p>",
        "</section>",
        # ---- 动作与术语 ----
        '<section class="panel" data-panel="actions">',
        '<div class="card"><h3>动作（%d）</h3><table id="actions">'
        "<thead><tr><th>ID</th><th>名称</th><th>触发</th><th>target</th><th>api_hint</th></tr></thead>"
        "<tbody>%s</tbody></table></div>" % (len(actions), act_rows),
        '<div class="card"><h3>术语（%d）</h3><table id="terms">'
        "<thead><tr><th>首选名</th><th>异名</th><th>来源</th></tr></thead>"
        "<tbody>%s</tbody></table></div>" % (len(terms), term_rows),
        "</section>",
        # ---- 裁决与排除 ----
        '<section class="panel" data-panel="decisions">',
        '<div class="card"><h3>裁决（%d）</h3><table id="decisions">'
        "<thead><tr><th>ID</th><th>主题</th><th>状态</th><th>处置</th></tr></thead>"
        "<tbody>%s</tbody></table></div>" % (len(decisions), dec_rows),
        '<div class="card"><h3>排除（%d）</h3><table id="excluded">'
        "<thead><tr><th>概念</th><th>理由</th></tr></thead>"
        "<tbody>%s</tbody></table></div>" % (len(excluded), exc_rows),
        "</section>",
        # ---- 校验报告 ----
        '<section class="panel" data-panel="checks">',
        '<div class="card"><h3>13 条判据</h3>%s</div>' % check_rows_html,
        '<div class="card"><h3>规模摘要</h3><table id="summary">'
        "<thead><tr><th>段</th><th>计数</th></tr></thead><tbody>%s</tbody></table>"
        '<p class="hint">降级说明：%s</p></div>' % (summary_rows, esc(meta["degraded_note"])),
        "</section>",
        "</div>",
        '<script id="ontology-data" type="application/json">%s</script>' % blob,
        "<script>%s</script>" % JS,
        "</body>",
        "</html>",
    ]
    return "\n".join(parts) + "\n"


def main():
    print("本体校验：%s" % YAML_PATH.relative_to(ROOT).as_posix())
    print("-" * 72)
    bundle = run_checks()
    if bundle is None:
        for code in range(2, 13):
            na(code, "跳过", "前置判据未过，无法继续")

    summary_print = []
    if bundle is not None:
        s = bundle["summary"]
        summary_print = [
            "类 %d / 关系 %d / 规则 %d（medium %d、low %d）/ 动作 %d"
            % (s["classes"], s["relations"], s["rules"], s["rules_medium"],
               s["rules_low"], s["actions"]),
            "术语 %d / 模块 %d / 裁决 %d / 排除 %d / 冲突组 %d / 来源引用 %d"
            % (s["terms"], s["modules"], s["decisions"], s["excluded"],
               s["conflict_groups"], s["refs"]),
        ]

    # ---------- 13. 生成模型页 ----------
    html_path = None
    if bundle is None:
        record("✗", 13, "生成模型页", "本体未通过前置判据，未生成")
    else:
        meta = {
            "name": str(bundle["data"].get("ontology", {}).get("name") or "运行时本体"),
            "domain": str(bundle["data"].get("ontology", {}).get("domain") or ""),
            "version": str(bundle["data"].get("ontology", {}).get("version") or ""),
            "built_at": str(bundle["data"].get("ontology", {}).get("built_at") or ""),
            "degraded_note": str(bundle["data"].get("ontology", {}).get("degraded_note") or ""),
        }
        bad = [r for r in results if r["symbol"] == "✗"]
        verdict = "校验未通过：%d 项失败" % len(bad) if bad else "校验全部通过（13/13）"
        verdict_class = "bad" if bad else "ok"
        provisional = {"symbol": "✓", "code": 13, "title": "生成模型页",
                       "detail": "本体模型.html（5 页签）"}
        rows = results + [provisional]
        try:
            doc = build_html(bundle, rows, verdict, verdict_class, meta)
            HTML_PATH.write_text(doc, encoding="utf-8", newline="\n")
            size = HTML_PATH.stat().st_size
            rows[-1]["detail"] = "本体模型.html %.1f KB（5 页签）" % (size / 1024.0)
            doc = build_html(bundle, rows, verdict, verdict_class, meta)
            HTML_PATH.write_text(doc, encoding="utf-8", newline="\n")
            results.append(rows[-1])
            record("✓", 13, "生成模型页",
                   "%s %.1f KB，5 个页签" % (HTML_PATH.name, HTML_PATH.stat().st_size / 1024.0))
            html_path = HTML_PATH
        except Exception as exc:                    # noqa: BLE001
            record("✗", 13, "生成模型页", "写入失败：%s" % exc)

    print("-" * 72)
    if summary_print:
        print("规模摘要")
        for line in summary_print:
            print("  " + line)
    print("-" * 72)
    if html_path is not None:
        print("模型页：%s" % html_path.relative_to(ROOT).as_posix())

    if fails:
        print("FAILED %d 项：" % len(fails))
        for item in fails:
            print("  - " + item)
        sys.exit(1)
    print("ALL_CHECKS_PASSED")
    sys.exit(0)


if __name__ == "__main__":
    main()
