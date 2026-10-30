#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7c · shim 导出覆盖自检：把「托管侧真的会去解析的名字」与 `libwpfwin32.so` 的导出比对。

    src/WpfGfx.Linux.Native/build-shim.sh --symbols      # 先产出 bin/exports.txt
    python3 src/WpfGfx.Linux.Native/tools/check-shim-coverage.py [--tier p0|mapped|all]

【为什么要这个工具（而不是一个个撞出来）】
  M7c Phase 2 的卡点里有**至少三次**是"shim 少导出一个名字"，而且每次都是在跑起来之后
  才撞到（`GetModuleFileName` 就是这么撞出来的 —— 我导出了 `...A`/`...W` 却漏了**裸名**）。
  这类缺口的共同点是：**编译期完全无感，只有走到那一行才炸**，而每次撞都要跑一轮
  完整链路（build + 启动 + 40s 超时）。

  所以这里把"缺哪些名字"变成一次静态计算：
    · 扫**实际编译进程序集**的源文件（两个集合：迁移的托管层 + 本工程自产件）；
    · 对每条 `[DllImport]` 按 **.NET 在 Unix 上的真实探测顺序**算出它会去找哪些名字；
    · 只看**本 shim 声明映射**的那几个 DLL（resolver 的 MappedLibraries）；
    · 与 `nm -D --defined-only libwpfwin32.so` 的结果比对，列出**会 EntryPointNotFoundException 的**。

【探测顺序（这一条错了整个工具就没意义）】
  · `CharSet.Unicode` 且 `ExactSpelling=false` → 先 `名W`，再 `名`
  · `CharSet.Auto`（**Unix 上折叠为 Ansi**）且 `ExactSpelling=false` → **只找裸名**（实测不补 A）
  · `ExactSpelling=true` → 只有 `名`
  · 有 `EntryPoint` 时用 EntryPoint 作为基名
  ⇒ **裸名必须收 Ansi/UTF-8**，这正是我漏掉 `GetModuleFileName` 裸名的原因。
"""

import argparse
import os
import re
import sys
import collections

HERE = os.path.dirname(os.path.abspath(__file__))
NATIVE_ROOT = os.path.normpath(os.path.join(HERE, ".."))
ROOT = os.path.normpath(os.path.join(NATIVE_ROOT, "..", ".."))
# `UP` = `$(UpstreamWpfRoot)` 的取值：结构上游化后 = **仓根**（上游 `src/Microsoft.DotNet.Wpf/**` 就在仓根下）。
UP = ROOT
SRC = os.path.join(UP, "src", "Microsoft.DotNet.Wpf", "src")
EXPORTS = os.path.join(NATIVE_ROOT, "bin", "exports.txt")

# resolver 实际映射到 libwpfwin32.so 的 DLL 名 —— **运行时从 resolver 源码解析**
# （见 load_mapped()）。下面的常量只是解析失败时的兜底，正常情况下不会用到。
MAPPED_FALLBACK = {"user32.dll", "gdi32.dll", "kernel32.dll",
                   "presentationnative_cor3.dll", "uxtheme.dll", "wtsapi32.dll"}


def load_constants():
    known = {}
    ext = os.path.join(SRC, "Shared/MS/Win32/ExternDll.cs")
    for m in re.finditer(r'public const string (\w+)\s*=\s*"([^"]*)"',
                         open(ext, encoding="utf-8-sig", errors="replace").read()):
        known["ExternDll." + m.group(1)] = m.group(2)
    known.update({
        "DllImport.PresentationNative": "PresentationNative_cor3.dll",
        "DllImport.User32": "user32.dll", "DllImport.Ole32": "ole32.dll",
        "DllImport.MilCore": "wpfgfx_cor3.dll", "DllImport.WindowsCodecs": "WindowsCodecs.dll",
        "DllImport.Mscms": "mscms.dll", "DllImport.NInput": "ninput.dll",
    })
    return known


def compiles(csproj):
    t = open(csproj, encoding="utf-8-sig", errors="replace").read()
    return [os.path.normpath(m.group(1).replace("$(UpstreamWpfRoot)", UP + "/")
                                       .replace("$(WpfLinuxRoot)", ROOT + "/").replace("\\", "/"))
            for m in re.finditer(r'<Compile Include="([^"]+)"', t)]


def find_attrs(text):
    for m in re.finditer(r"\[DllImport\s*\(", text):
        i, depth, j = m.end() - 1, 0, m.end() - 1
        while j < len(text):
            c = text[j]
            if c == '"':
                j += 1
                while j < len(text) and text[j] != '"':
                    if text[j] == "\\":
                        j += 1
                    j += 1
            elif c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        yield m.start(), text[i:j + 1], j + 1


def strip_comments(text):
    """去掉 `//` 行注释后再扫。

    【为什么必须做】`src/Microsoft.DotNet.Wpf.Linux/src/shims/Win32ShimResolver.cs` 的文件头注释里有一行**示例**：

        //   [DllImport("user32.dll")]  [DllImport("gdi32.dll")]  [DllImport("kernel32.dll")]

    不剥注释就会被当成 3 条真实声明扫出来（函数名解析失败 ⇒ 名字显示成 `?`），
    在报告里表现为"user32/gdi32/kernel32 各缺 1 个叫 `?` 的导出"。这是纯噪声，
    而且会让人误以为还有真缺口。所有 `?` 名字一律视为注释残留丢弃。
    """
    return "\n".join(l.split("//", 1)[0] for l in text.split("\n"))


def load_mapped():
    """从 **resolver 源码**解析 MappedLibraries，不硬编码。

    【为什么要解析而不是写死】这份清单在 `src/Linux/build/`（本轮边界外）里，且**已经变过两次**
    （父级陆续加了 `uxtheme.dll` / `wtsapi32.dll`）。硬编码的副本会悄悄漂移：
    工具说"imm32 已映射"，实际 resolver 里根本没有 imm32 —— 于是 14 条 imm32 声明
    被算成 EntryPointNotFound，而运行时抛的其实是 DllNotFoundException（两回事）。
    让工具直接读 ground truth，这类漂移就不可能再发生。
    """
    p = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "Win32ShimResolver.cs")
    if not os.path.exists(p):
        return set(), None
    text = strip_comments(open(p, encoding="utf-8-sig", errors="replace").read())
    m = re.search(r"MappedLibraries\s*=\s*(?:new\s+\w+\s*)?\{(.*?)\}", text, re.S)
    if not m:
        return set(), p
    return {s.lower() for s in re.findall(r'"([^"]+)"', m.group(1))}, p


def scan(files, known):
    out = []
    for f in files:
        if not os.path.exists(f):
            continue
        text = strip_comments(open(f, encoding="utf-8-sig", errors="replace").read())
        consts = {m.group(1): m.group(2)
                  for m in re.finditer(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"', text)}
        for a, attr, end in find_attrs(text):
            args = [x.strip() for x in attr[1:-1].split(",")]
            tok = args[0]
            dll = (tok.strip('"') if tok.startswith('"')
                   else consts.get(tok.split(".")[-1], known.get(tok, "?" + tok)))
            entry = charset = None
            exact = False
            for x in args[1:]:
                if x.startswith("EntryPoint"):
                    entry = x.split("=", 1)[1].strip().strip('"')
                elif x.startswith("CharSet"):
                    charset = x.split("=", 1)[1].strip().split(".")[-1]
                elif x.startswith("ExactSpelling"):
                    exact = "true" in x.split("=", 1)[1].lower()
            rest = text[end:end + 4000]
            mm = re.search(r"extern\s+([^;{]+);", rest, re.S)
            fn = "?"
            if mm:
                fm = re.search(r"(\w+)\s*\(", mm.group(1))
                if fm:
                    fn = fm.group(1)
            out.append(dict(file=os.path.relpath(f, UP), line=text.count("\n", 0, a) + 1,
                            dll=dll, fn=fn, entry=entry, charset=charset, exact=exact))
    return out


def probes(e):
    """按 .NET 在 Unix 上的真实探测顺序给出候选名。

    两条实测结论决定了这里的写法：
      1. **名字已经带 W/A 后缀时不再补后缀** —— 上游大量声明写成
         `EntryPoint="CreateWindowExW"`（后缀写在 EntryPoint 里），运行期直接用这个名字；
         早期版本在这里无脑补一个 W 得到 `CreateWindowExWW`，把 13 个**其实没问题**的
         函数误报成缺口。
      2. **裸名是 Ansi/Auto 的第一候选，而且不回落 `名A`** —— `GetModuleFileName` 裸名缺席时
         直接抛 EntryPointNotFound（尽管 `GetModuleFileNameA` 在）。所以判定只看第一个候选。
    """
    base = e["entry"] or e["fn"]
    if e["exact"]:
        return [base]
    if (e["charset"] or "Ansi") == "Unicode":
        return [base] if base.endswith("W") else [base + "W", base]
    return [base] if base.endswith("A") else [base, base + "A"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tier", default="mapped", choices=["mapped", "all"])
    args = ap.parse_args()

    if not os.path.exists(EXPORTS):
        print("[失败] 找不到 bin/exports.txt —— 先跑 build-shim.sh --symbols")
        return 1
    have = {l.strip() for l in open(EXPORTS, encoding="utf-8") if l.strip()}

    mapped, resolved_from = load_mapped()
    src = f"（解析自 {os.path.relpath(resolved_from, ROOT)}）" if resolved_from and mapped \
        else "（解析失败，用兜底常量）"
    if not mapped:
        mapped = MAPPED_FALLBACK
    print(f"MappedLibraries {len(mapped)} 个 {src}")

    known = load_constants()
    all_e = []
    for name in ["WindowsBase", "PresentationCore", "PresentationFramework"]:
        p = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", name, f"{name}.Linux.csproj")
        if os.path.exists(p):
            all_e += scan(compiles(p), known)
    # 本工程自产件（samples 也算；这里只取 HelloWpf）
    p = os.path.join(ROOT, "src/Linux/samples/HelloWpf/HelloWpf.csproj")
    if os.path.exists(p):
        all_e += scan(compiles(p), known)

    # 注释残留：函数名解析成 `?` 的一律丢弃（否则报告里会出现"缺一个叫 ? 的导出"）
    all_e = [e for e in all_e if (e["entry"] or e["fn"]) != "?"]

    # 去重：(dll, 基名, 探测序列)
    uniq = {}
    for e in all_e:
        key = (e["dll"].lower(), e["entry"] or e["fn"], tuple(probes(e)))
        uniq.setdefault(key, e)
    rows = list(uniq.values())

    # ── 第二类缺口：**DLL 层面**（resolver 没映射 ⇒ DllNotFoundException）────────
    #   这一类比 EntryPointNotFound 更早发生、也更硬：连库都打不开。
    #   实测：`shell32.dll`（IconHelper.GetDefaultIconHandles → ExtractIconEx）就是这么撞的。
    unmapped = collections.Counter(e["dll"] for e in rows if e["dll"].lower() not in mapped)
    if unmapped:
        print(f"\n未映射的 DLL（会 DllNotFoundException）: {len(unmapped)} 个")
        for d, n in unmapped.most_common():
            ex = next(e for e in rows if e["dll"] == d)
            print(f"  {d:28s} {n:4d} 条   例：{ex['file']}:{ex['line']}")

    if args.tier == "mapped":
        rows = [e for e in rows if e["dll"].lower() in mapped]

    # ★ 判据是**第一个候选名**必须存在：实测 Unix 上 .NET **不补 `A` 后缀**
    #   （`GetModuleFileName` 裸名缺席时抛 EntryPointNotFound，尽管 `GetModuleFileNameA` 在）。
    #   所以后续候选只作为"冗余导出"信息展示，不能当作覆盖。
    missing, ok = [], []
    for e in rows:
        names = probes(e)
        first = names[0]
        (ok if first in have else missing).append((e, names, first))

    print(f"\n扫描到 {len(rows)} 条 DllImport 指向本 shim 映射的 DLL"
          f"（{'仅映射集' if args.tier=='mapped' else '全部'}）")
    print(f"  已有导出可用 : {len(ok)}")
    print(f"  **会 EntryPointNotFoundException** : {len(missing)}")
    if missing:
        print()
        by_dll = collections.Counter(e["dll"] for e, _, _ in missing)
        for d, n in by_dll.most_common():
            print(f"  [{d}] {n} 条")
        print()
        for e, names, _ in sorted(missing, key=lambda t: (t[0]["dll"], t[0]["entry"] or t[0]["fn"])):
            base = e["entry"] or e["fn"]
            print(f"  {e['dll']:28s} {base:32s} 探测 {'/'.join(names):40s} {e['file']}:{e['line']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
