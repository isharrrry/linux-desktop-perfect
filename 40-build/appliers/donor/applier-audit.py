#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""**应用器审计**（只读）：把"被登记了但其实没生效"变成会红的闸门（在册债务 #13）。

用法
----
    python3 src/Linux/build/MilBridge/tools/applier-audit.py                 # 用真实树 + src/Linux/build/integration-wave.sh
    python3 src/Linux/build/MilBridge/tools/applier-audit.py --root <dir> --wave <file>
    python3 src/Linux/build/MilBridge/tools/applier-audit.py --with-check   # 额外跑每个 applier 的 `--check`（只读）

审计口径（**期望值从应用器自己的声明表算出来，不看树**）
------------------------------------------------------
对 `APPLIERS_EXPLICIT` 里的每个应用器 `<name>`（模块 `src/WpfGfx.Linux.Native/tools/<name>.py`）：

* **A 级（强）**：该模块**自己声明**的每一条编辑 `(name, anchor, repl)` ⇒
  它的生成物 `<GEN>` 里 **`repl` 必须恰好出现 1 次**。
  这是"命中 0 但脚本 exit 0"的直接杀手：`repl` 不在 ⇒ 说明那次编辑**没落进生成物**。
  覆盖：`EDITS`（+`GEN`）、`EDITS_HS/HK`（+`GEN_HS/HK`）、`TARGETS`（多文件）、
  `PATCHES`（形如 `(rel, gen, [(old,new,expect)…])` ⇒ 期望 `new` 出现 `expect` 次）、
  `GENERATED`+`ANCHOR/REPLACEMENT`（单编辑）。
* **B 级（中）**：没有声明表的模块 ⇒ 生成物（`TARGET` / `GENERATED` / `GEN_FILE` / `GEN_NAME`）
  **必须存在**、**必须提到生成它的脚本名**（生成物 banner 里的 `src/Linux/tools/<name>.py`），
  且 `csproj` 里有该文件的 `<Compile Include=…>` 行。
* **C 级（弱）**：连生成物都没声明的 ⇒ 只查 `csproj` 里有该应用器的 `MARKER_BEGIN`
  （这一级只能抓"接线被 port-lib 抹掉"，抓不了"内容没生效"——**明说**，不含糊）。

为什么不"跑一遍看是否非零"：那样对 **exit 0 + 命中 0** 是瞎的（正是补丁 N/M 那类事故的形态）。
期望值全部来自**应用器源码里的声明**，与当前树无关；树只作为被检对象。

输出（机读）
------------
    APPLIER_AUDIT applier=<name> tier=<A|B|C> ok=<n> miss=<n> detail=<...>
    APPLIER_AUDIT_SUMMARY appliers=<n> ok=<n> miss=<n> red=<n> rc=<0|1>
`rc=1` ⇒ 至少有 1 条 miss（**这就是闸门的红**）。
"""

import argparse
import hashlib
import importlib.util
import io
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT_DEFAULT = os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", ".."))   # src/Linux/build/MilBridge/tools → 仓库根
TOOLS_REL = "src/WpfGfx.Linux.Native/tools"
WAVE_DEFAULT = "src/Linux/build/integration-wave.sh"


def sha8(path):
    try:
        with open(path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()[:8]
    except OSError:
        return "--------"


def read_wave(root, wave):
    """从 wave 脚本里抽出 APPLIERS_EXPLICIT 的条目（顺序保留）。"""
    p = os.path.join(root, wave)
    names, seen = [], set()
    if not os.path.exists(p):
        return names, f"wave 文件不存在：{wave}"
    text = io.open(p, encoding="utf-8", errors="replace").read()
    lines = text.split("\n")
    i = 0
    while i < len(lines):
        if re.match(r"^APPLIERS_EXPLICIT\+?=\(", lines[i]):
            # 逐行收到**单独一行 `)`** 为止（不要用非贪婪 `(.*?)` —— 注释里出现 `)` 会提前截断，实测漏掉两个应用器）
            # ⚠️ 收尾判据必须是"**这一行就是 `)`**"，不能是"这行里有 `)`" ——
            #    注释里出现 `abort(134)` 这种 ASCII 括号会让解析**提前收尾**（实测漏掉两个应用器）。
            j = i
            buf = [lines[i].split("(", 1)[1]]
            if ")" not in buf[0]:
                while j + 1 < len(lines):
                    j += 1
                    buf.append(lines[j])
                    if lines[j].strip().startswith(")"):
                        break
            for line in buf:
                line = line.split("#")[0].strip()
                for tok in line.split():
                    tok = tok.strip().strip(")")
                    if tok and not tok.startswith("(") and tok not in seen:
                        seen.add(tok)
                        names.append(tok)
            i = j
        i += 1
    return names, None


def load_module(root, name):
    p = os.path.join(root, TOOLS_REL, name + ".py")
    if not os.path.exists(p):
        return None, f"应用器脚本不存在：{TOOLS_REL}/{name}.py"
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), p)
    m = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(m)
    except Exception as e:                                    # noqa: BLE001
        return None, f"import 失败：{type(e).__name__}: {e}"
    return m, None


def _as_edits(pairs):
    """把各种形态的编辑表归一成 [(标签, 锚点, 替换)]。"""
    out = []
    for item in pairs:
        if isinstance(item, (list, tuple)) and len(item) == 3 and isinstance(item[1], str):
            out.append((str(item[0]), item[1], item[2]))
        elif isinstance(item, (list, tuple)) and len(item) == 2 and isinstance(item[0], str):
            out.append(("edit", item[0], item[1]))
    return out


def declarations(m):
    """返回 (tier, [(生成物路径, [(标签, 期望串, 期望次数)])], [csproj], [接线 Include 行])."""
    gens, expects = [], []

    def add(gen, edits):
        if not gen:
            return
        rows = []
        for label, anchor, repl in edits:
            if isinstance(repl, str) and repl:
                rows.append((label, repl, 1))
        if rows:
            gens.append(gen)
            expects.append((gen, rows))

    if hasattr(m, "EDITS"):
        for attr in ("GEN", "GENERATED", "TARGET", "GEN_FILE"):
            if hasattr(m, attr):
                add(getattr(m, attr), _as_edits(m.EDITS))
                break
    if hasattr(m, "EDITS_HS") and hasattr(m, "GEN_HS"):
        add(m.GEN_HS, _as_edits(m.EDITS_HS))
    if hasattr(m, "EDITS_HK") and hasattr(m, "GEN_HK"):
        add(m.GEN_HK, _as_edits(m.EDITS_HK))
    if hasattr(m, "TARGETS"):
        for t in m.TARGETS:
            if len(t) >= 3:
                add(t[1] if isinstance(t[1], str) else None, _as_edits(t[2]))
    if hasattr(m, "PATCHES"):
        for t in m.PATCHES:
            if len(t) == 3 and isinstance(t[2], (list, tuple)):
                gen = t[1]
                rows = []
                for sub in t[2]:
                    if len(sub) == 3 and isinstance(sub[1], str):
                        exp = sub[2] if isinstance(sub[2], int) else 1
                        if exp > 0:
                            rows.append((str(sub[0])[:40], sub[1], exp))
                if rows:
                    gens.append(gen)
                    expects.append((gen, rows))
    if not expects and hasattr(m, "GENERATED") and hasattr(m, "ANCHOR") and hasattr(m, "REPLACEMENT"):
        add(m.GENERATED, [("ANCHOR→REPLACEMENT", m.ANCHOR, m.REPLACEMENT)])

    csproj = getattr(m, "CSPROJ", None)
    proj_dir = os.path.dirname(csproj) if isinstance(csproj, str) else None
    includes = []
    fixed = []
    for gen, rows in expects:
        g = gen
        if isinstance(g, str) and not os.path.isabs(g) and proj_dir:
            g = os.path.join(proj_dir, g)        # 裸文件名 ⇒ 挂到本应用器的 csproj 目录
        fixed.append((g, rows))
        if isinstance(g, str):
            includes.append(os.path.basename(g))
    expects = fixed
    if expects:
        return "A", expects, csproj, includes

    # ── B 级：没有编辑表，但有生成物声明 ──
    gen = None
    for attr in ("TARGET", "GENERATED", "GEN_FILE"):
        v = getattr(m, attr, None)
        if isinstance(v, str):
            gen = v
            break
    if gen is None and isinstance(getattr(m, "GEN_NAME", None), str) and csproj:
        gen = os.path.join(os.path.dirname(csproj), m.GEN_NAME)
    if gen:
        # B 级：没有逐条编辑可核，靠"生成物存在 + banner 提到本脚本 + csproj Include"三件事
        return "B", [(gen, [])], csproj, [os.path.basename(gen)]
    return "C", [], csproj, []



def resolve_upstream(root, m, rel_hint=None):
    """把应用器声明的上游路径解析成绝对路径；解析不出返回 None。"""
    up = getattr(m, "UP", None) or getattr(m, "UPSTREAM", None)
    rel = rel_hint or getattr(m, "UP_REL", None) or getattr(m, "UPSTREAM_REL", None)
    if isinstance(up, str) and os.path.isfile(up):
        return up
    if isinstance(up, str) and isinstance(rel, str):
        return os.path.join(up, rel)
    if isinstance(rel, str) and rel.startswith("src/Microsoft.DotNet.Wpf/"):
        return os.path.join(root, rel)
    if isinstance(rel, str) and isinstance(up, str):
        return os.path.join(up, rel)
    return None


def edits_of(m):
    """把应用器声明的编辑表归一成 [(上游相对/绝对提示, 生成物, [(标签,锚点,替换)])]。"""
    out = []
    def push(gen, edits, hint=None):
        e = _as_edits(edits)
        if gen and e:
            out.append((hint, gen, e))
    if hasattr(m, "EDITS"):
        for attr in ("GEN", "GENERATED", "TARGET", "GEN_FILE"):
            if hasattr(m, attr):
                push(getattr(m, attr), m.EDITS)
                break
    if hasattr(m, "EDITS_HS") and hasattr(m, "GEN_HS"):
        push(m.GEN_HS, m.EDITS_HS)
    if hasattr(m, "EDITS_HK") and hasattr(m, "GEN_HK"):
        push(m.GEN_HK, m.EDITS_HK)
    if hasattr(m, "TARGETS"):
        for t in m.TARGETS:
            if len(t) >= 3:
                push(t[1], t[2], t[0])
    if hasattr(m, "PATCHES"):
        for t in m.PATCHES:
            if len(t) == 3 and isinstance(t[2], (list, tuple)):
                subs = []
                for i, sub in enumerate(t[2]):
                    if len(sub) == 3 and isinstance(sub[1], str):
                        exp = sub[2] if isinstance(sub[2], int) else 1
                        if exp > 0:
                            subs.append((str(sub[0])[:40], sub[0], sub[1], True))   # True = 全部替换
                if subs:
                    out.append((t[0], t[1], subs))
    if not out and hasattr(m, "GENERATED") and hasattr(m, "ANCHOR"):
        push(m.GENERATED, [("ANCHOR→REPLACEMENT", m.ANCHOR, m.REPLACEMENT)])
    return out


def transform_expected(root, m, rel_hint, edits):
    """**从声明重算期望文本**：upstream + 按顺序应用 EDITS。返回 (期望文本, 失败原因)。"""
    up = resolve_upstream(root, m, rel_hint)
    if not up or not os.path.exists(up):
        return None, f"上游解析不到（hint={rel_hint!r}）"
    text = io.open(up, encoding="utf-8-sig", errors="replace").read()
    for item in edits:
        label, anchor, repl = item[0], item[1], item[2]
        replace_all = item[3] if len(item) > 3 else False
        if anchor not in text:
            return None, f"上游里没有锚点[{label}]（上游改过？）"
        # ⚠️ 应用器各自的语义要跟住：`EDITS/ANCHOR` 是"只替换 1 次"，
        #    而 `PATCHES`（registry/olecontext）用的是 `out.replace(old,new)`（**全部**）
        text = text.replace(anchor, repl) if replace_all else text.replace(anchor, repl, 1)
    return text, None


def audit_one(root, name, with_check):
    m, err = load_module(root, name)
    if m is None:
        return {"applier": name, "tier": "-", "ok": 0, "miss": 1, "detail": err}

    tier, expects, csproj, includes = declarations(m)
    ok, miss, details = 0, 0, []

    # ── A 级：**变换等价**（从声明重算期望，再与落盘生成物比）──
    #    为什么不是"逐条 repl 计数==1"：应用器的编辑**可以互相覆盖**（实测 T1c/RTL 的两条宽松兜底
    #    覆盖了 T1b/D3 的同名站点）⇒ 逐条计数会把"设计如此"误报成 miss。
    #    变换等价同时抓得住：没跑、只跑了一半、被上游原样顶替。
    tr = edits_of(m)
    for rel_hint, gen, edits in tr:
        gp = gen if os.path.isabs(gen) else os.path.join(os.path.dirname(csproj) if isinstance(csproj, str) else root,
                                                         os.path.basename(gen)) if isinstance(csproj, str) else os.path.join(root, gen)
        if not os.path.exists(gp):
            gp = os.path.join(root, gen) if not os.path.isabs(gen) else gen
        if not os.path.exists(gp):
            miss += 1
            details.append(f"缺生成物 {os.path.basename(gen)}")
            continue
        disk = io.open(gp, encoding="utf-8", errors="replace").read()
        exp_text, why = transform_expected(root, m, rel_hint, edits)
        if exp_text is None:
            # 上游解析不到 ⇒ 退化为"逐条 repl 必须出现"（弱一档，明说）
            bad = [e[0] for e in edits if e[2] and disk.count(e[2]) != (len([e[1] for _x in [0]]) or 1) and disk.count(e[2]) == 0]
            if bad:
                miss += 1
                details.append(f"{os.path.basename(gp)}:退化检查（{why}）失败：编辑 {bad[:2]}")
            else:
                ok += 1
            continue
        if exp_text in disk:
            ok += 1
        else:
            miss += 1
            # 定位：先看是不是"压根没跑的原始上游"
            up = resolve_upstream(root, m, rel_hint)
            tag = "**生成物 == 未打补丁的上游（应用器没跑？）**" if (up and os.path.exists(up) and
                  io.open(up, encoding="utf-8-sig", errors="replace").read() in disk) else "生成物与声明的变换不一致"
            details.append(f"{os.path.basename(gp)}:{tag}")

    # B 级：生成物存在 + banner 提到脚本 + csproj Include
    if tier == "B":
        for gen, _rows in expects:
            gp = gen if os.path.isabs(gen) else os.path.join(root, gen)
            if not os.path.exists(gp):
                miss += 1
                details.append(f"缺生成物 {os.path.relpath(gp, root)}")
                continue
            text = io.open(gp, encoding="utf-8", errors="replace").read()
            if name + ".py" in text:
                ok += 1
            else:
                miss += 1
                details.append(f"{os.path.basename(gp)}:banner 里没有 {name}.py（可能被别的脚本产的文件顶了）")

    # 接线（A/B/C 都查）
    if isinstance(csproj, str):
        cp = csproj if os.path.isabs(csproj) else os.path.join(root, csproj)
        if not os.path.exists(cp):
            miss += 1
            details.append(f"缺 csproj {os.path.relpath(cp, root)}")
        else:
            ctext = io.open(cp, encoding="utf-8-sig", errors="replace").read()
            mb = getattr(m, "MARKER_BEGIN", None)
            if isinstance(mb, str) and mb and mb in ctext:
                ok += 1
            else:
                miss += 1
                details.append("csproj 里没有本应用器的 MARKER_BEGIN（接线被抹掉？）")
            for inc in includes:
                if not inc:
                    continue
                if f'Include="$(WpfLinuxRoot)' in ctext and inc in ctext:
                    ok += 1
                else:
                    miss += 1
                    details.append(f"csproj 里没有 <Compile Include=…{inc}>")

    extra = ""
    if with_check:
        script = os.path.join(root, TOOLS_REL, name + ".py")
        try:
            r = subprocess.run([sys.executable, script, "--check"], cwd=root,
                               capture_output=True, timeout=180)
            if r.returncode == 0:
                ok += 1
            else:
                miss += 1
                details.append(f"`--check` rc={r.returncode}")
            extra = f" selfcheck_rc={r.returncode}"
        except Exception as e:                                # noqa: BLE001
            miss += 1
            details.append(f"`--check` 抛 {type(e).__name__}")

    return {"applier": name, "tier": tier, "ok": ok, "miss": miss,
            "detail": ("; ".join(details) if details else "-") + extra}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=ROOT_DEFAULT)
    ap.add_argument("--wave", default=WAVE_DEFAULT)
    ap.add_argument("--with-check", action="store_true")
    ap.add_argument("--only", default=None, help="只审一个应用器（调试/牙齿用）")
    ap.add_argument("--expect-registered", default=os.path.join(HERE, "applier-audit-expected.txt"),
                    help="**必须被登记**的应用器清单（一行一个，`#` 注释）——独立于 wave ⇒ "
                         '"被摘掉/漏登记"也能变红')
    args = ap.parse_args()

    root = os.path.abspath(args.root)
    names, err = read_wave(root, args.wave)
    if err:
        print(f"APPLIER_AUDIT_SUMMARY appliers=0 ok=0 miss=1 red=1 rc=1 detail={err}")
        return 1
    if args.only:
        names = [n for n in names if n == args.only] or [args.only]
    if not names:
        # ⚠️ 不要在这里短路返回：否则"wave 被清空/被摘掉"只会得到一句"解析为空"，
        #    而**看不见"到底谁没登记"**。让它继续走到下面的登记清单比对 ⇒ 每个缺失者各报一行。
        print("APPLIER_AUDIT_NOTE detail=APPLIERS_EXPLICIT 解析为空（本行只说明 wave 侧为空，"
              "缺失清单见下面的**未登记**行）")

    # ② 登记清单核对（**独立于 wave**）：wave 里少一个 ⇒ miss
    want = []
    ep = args.expect_registered
    if ep and os.path.exists(ep):
        for line in io.open(ep, encoding="utf-8", errors="replace").read().split("\n"):
            line = line.split("#")[0].strip()
            if line:
                want.append(line)
    registered = set(names)
    missing_reg = [w for w in want if w not in registered]

    tot_ok = tot_miss = red = 0
    for w in missing_reg:
        print(f"APPLIER_AUDIT applier={w} tier=- ok=0 miss=1 "
              f"detail=**未登记**（不在 APPLIERS_EXPLICIT 里；登记清单 {os.path.basename(ep)} 要求它在）")
        tot_miss += 1
        red += 1
    for n in names:
        r = audit_one(root, n, args.with_check)
        tot_ok += r["ok"]
        tot_miss += r["miss"]
        red += 1 if r["miss"] else 0
        print(f"APPLIER_AUDIT applier={r['applier']} tier={r['tier']} ok={r['ok']} "
              f"miss={r['miss']} detail={r['detail']}")
    rc = 1 if red else 0
    print(f"APPLIER_AUDIT_SUMMARY appliers={len(names)} ok={tot_ok} miss={tot_miss} red={red} rc={rc}")
    return rc


if __name__ == "__main__":
    sys.exit(main())
