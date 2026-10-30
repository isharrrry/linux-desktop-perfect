#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T1c 第 6 批 · W7：`EffectiveValueEntry.GetFlattenedEntry` 的三个 return 分支（只读插桩）。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-entry-flatten-trace.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-entry-flatten-trace.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-entry-flatten-trace.py --prove   # 「只插入」证明（只读）

为什么打这一格（W7）
------------------
第 5 批实跑命中 **A 格**：写侧出口 `IsDeferredReference=True`（`newEntry.Value=ModifiedValue`），
读侧 `effectiveEntry.IsDeferredReference=False` 且值还是旧串 `"seed-文本"` ⇒ 两者之间**有效值槽被换回了普通字符串**。
判据 a/b/c/d 里的 **(c)** 是"`UpdateEffectiveValue` 内部把槽写坏"；**(d)** 是"读侧取错了槽位/索引"。
`GetFlattenedEntry` 是读侧**展平**那一步：

    EffectiveValueEntry.cs:322  `if ((_source & (ModifiersMask | HasExpressionMarker)) == 0)`
        :326  `return this;`              ← **无修饰 ⇒ 原样返回**（第 5 批 `HasModifiers=False` ⇒ 应当走这条）
    EffectiveValueEntry.cs:329  `if (!HasModifiers)`（只有表达式标记）→ :343 `return unsetEntry;`
    EffectiveValueEntry.cs:423  `Debug.Assert(entry.IsDeferredReference == (entry.Value is DeferredReference), …)`
        :425  `return entry;`             ← **有修饰 ⇒ 返回展开后的值**

⇒ 三个分支各打一行就能确认"**flatten 有没有把 deferred 标记吞掉**"。

⚠️ **上游那条 `Debug.Assert`（:423）正是我们这一族要的断言**（"flag 与 value 必须同步"），
   但 `Debug.Assert` 是 `[Conditional("DEBUG")]` ⇒ **Release 下被编译掉** ⇒ 这就是它没能替我们发现问题的原因。
   本条登记在报告里（也是"Linux 上哪些断言还活着"的一个实例）。

**依赖**：本文件用 `WpfLinuxDpValueTrace`（tracer 类定义在**另一个**生成物
`src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DependencyObject.Linux.cs` 里，由 `patch-windowsbase-dpvalue-trace.py` 生成/接线）。
两者同 namespace（`System.Windows`）同程序集 ⇒ 调用点**不必全限定**。
`--check` 会显式核对"那一批的接线已在 csproj 里"，否则报错（避免"只有一个 applier 被应用"时的 CS0103）。

**有界**：`GetFlattenedEntry` 对**每个 DP 读**都会走，且**没有 `dp` 参数** ⇒ 不能用 `dp.Name` 过滤；
本批用 `IsDeferredReference || requests 带了 DeferredReferences/RawEntry` 过滤（非 deferred 静默）。
开关：`WPF_LINUX_INPUT_TRACE`（严格）或 `WPF_LINUX_MSGFLOW_TRACE`（与原生同名同义）；缺省关 / ≤200 行 / 只打印。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")
UP_REL = "src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/EffectiveValueEntry.cs"
GEN = os.path.join(WB_DIR, "EffectiveValueEntry.Linux.cs")

# 依赖：另一批（第 5 批）的接线标记必须已在 csproj 里（否则 tracer 类不存在 ⇒ CS0103）
DEP_MARKER = "T1c 第 5 批：DP 读路径为什么不解析 deferred"

W7A_ANCHOR = '''            if ((_source & (FullValueSource.ModifiersMask | FullValueSource.HasExpressionMarker)) == 0)
            {
                // If the property does not have any modifiers
                // then just return the base value.
                return this;
            }
'''

W7B_ANCHOR = '''                };
                return unsetEntry;
            }
'''

W7C_ANCHOR = '''            Debug.Assert(entry.IsDeferredReference == (entry.Value is DeferredReference), "Value and DeferredReference flag should be in sync; hitting this may mean that it's time to divide the DeferredReference flag into a set of flags, one for each modifier");

            return entry;
        }
'''

EDITS = [
    ("W7a 无修饰 ⇒ 原样返回（`return this`）", W7A_ANCHOR, '''            if ((_source & (FullValueSource.ModifiersMask | FullValueSource.HasExpressionMarker)) == 0)
            {
                // ── T1c 第 6 批 W7a（只读插桩）：**无修饰 ⇒ 原样返回**（第 5 批 HasModifiers=False 应走这条）──
                WpfLinuxDpValueTrace.W7Flatten("W7a 无修饰 ⇒ return this", this, requests);

                // If the property does not have any modifiers
                // then just return the base value.
                return this;
            }
'''),
    ("W7b 只有表达式标记 ⇒ 返回 unset 条目", W7B_ANCHOR, '''                };
                // ── T1c W7b（只读插桩）：只有表达式标记 ⇒ 返回 unset 默认条目 ──
                WpfLinuxDpValueTrace.W7Flatten("W7b 只有表达式标记 ⇒ return unsetEntry", this, requests);
                return unsetEntry;
            }
'''),
    ("W7c 有修饰 ⇒ 返回展开后的值", W7C_ANCHOR, '''            Debug.Assert(entry.IsDeferredReference == (entry.Value is DeferredReference), "Value and DeferredReference flag should be in sync; hitting this may mean that it's time to divide the DeferredReference flag into a set of flags, one for each modifier");

            // ── T1c W7c（只读插桩）：**有修饰 ⇒ 返回展开后的值**（上面那条 Debug.Assert 在 Release 下被编译掉）──
            WpfLinuxDpValueTrace.W7Flatten("W7c 有修饰 ⇒ return entry（展开后的值）", this, requests);
            return entry;
        }
'''),
    ('A1',
     '                if ((requests & RequestFlags.CoercionBaseValue) == 0)\n                {\n                    entry.Value = modifiedValue.CoercedValue;\n                }\n',
     '                if ((requests & RequestFlags.CoercionBaseValue) == 0)\n                {\n                    entry.Value = modifiedValue.CoercedValue;\n                    WpfLinuxDpValueTrace.W10Pick("CoercedValue（IsCoerced 且未请求 CoercionBaseValue）", this, modifiedValue);\n                }\n'),
    ('A2',
     '                    if (IsCoercedWithCurrentValue)\n                    {\n                        entry.Value = modifiedValue.CoercedValue;\n                    }\n',
     '                    if (IsCoercedWithCurrentValue)\n                    {\n                        entry.Value = modifiedValue.CoercedValue;\n                        WpfLinuxDpValueTrace.W10Pick("CoercedValue（IsCoercedWithCurrentValue ⇒ SetCurrentDeferredValue 那一支）", this, modifiedValue);\n                    }\n'),
    ('A3',
     '                    else if (IsAnimated && ((requests & RequestFlags.AnimationBaseValue) == 0))\n                    {\n                        entry.Value = modifiedValue.AnimatedValue;\n                    }\n',
     '                    else if (IsAnimated && ((requests & RequestFlags.AnimationBaseValue) == 0))\n                    {\n                        entry.Value = modifiedValue.AnimatedValue;\n                        WpfLinuxDpValueTrace.W10Pick("AnimatedValue（IsAnimated 且未请求 AnimationBaseValue）", this, modifiedValue);\n                    }\n'),
    ('A4',
     '                    else if (IsExpression)\n                    {\n                        entry.Value = modifiedValue.ExpressionValue;\n                    }\n',
     '                    else if (IsExpression)\n                    {\n                        entry.Value = modifiedValue.ExpressionValue;\n                        WpfLinuxDpValueTrace.W10Pick("ExpressionValue（CoercionBaseValue 分支里的 IsExpression）", this, modifiedValue);\n                    }\n'),
    ('A5',
     '                    else\n                    {\n                        entry.Value = modifiedValue.BaseValue;\n                    }\n                }\n            }\n            else if (IsAnimated)\n',
     '                    else\n                    {\n                        entry.Value = modifiedValue.BaseValue;\n                        WpfLinuxDpValueTrace.W10Pick("**BaseValue**（Coerced 分支的兜底 ⇒ 取到旧串就是这里）", this, modifiedValue);\n                    }\n                }\n            }\n            else if (IsAnimated)\n'),
    ('A6',
     '                if ((requests & RequestFlags.AnimationBaseValue) == 0)\n                {\n                    entry.Value = modifiedValue.AnimatedValue;\n                }\n',
     '                if ((requests & RequestFlags.AnimationBaseValue) == 0)\n                {\n                    entry.Value = modifiedValue.AnimatedValue;\n                    WpfLinuxDpValueTrace.W10Pick("AnimatedValue（IsAnimated 且未请求 AnimationBaseValue）", this, modifiedValue);\n                }\n'),
    ('A7',
     '                    // requesting the base value of an animation.\n                    if (IsExpression)\n                    {\n                        entry.Value = modifiedValue.ExpressionValue;\n                    }\n',
     '                    // requesting the base value of an animation.\n                    if (IsExpression)\n                    {\n                        entry.Value = modifiedValue.ExpressionValue;\n                        WpfLinuxDpValueTrace.W10Pick("ExpressionValue（AnimationBaseValue + IsExpression）", this, modifiedValue);\n                    }\n'),
    ('A8',
     '                    else\n                    {\n                        entry.Value = modifiedValue.BaseValue;\n                     }\n                }\n            }\n',
     '                    else\n                    {\n                        entry.Value = modifiedValue.BaseValue;\n                        WpfLinuxDpValueTrace.W10Pick("**BaseValue**（AnimationBaseValue 分支的兜底）", this, modifiedValue);\n                     }\n                }\n            }\n'),
    ('A9',
     '                object expressionValue = modifiedValue.ExpressionValue;\n\n                entry.Value = expressionValue;\n',
     '                object expressionValue = modifiedValue.ExpressionValue;\n\n                entry.Value = expressionValue;\n                WpfLinuxDpValueTrace.W10Pick("expressionValue（末支 IsExpression）", this, modifiedValue);\n'),
]

REQUIRED = [
    ("W7a 调用点", 'WpfLinuxDpValueTrace.W7Flatten("W7a 无修饰 ⇒ return this", this, requests);'),
    ("W7b 调用点", 'WpfLinuxDpValueTrace.W7Flatten("W7b 只有表达式标记 ⇒ return unsetEntry", this, requests);'),
    ("W7c 调用点", 'WpfLinuxDpValueTrace.W7Flatten("W7c 有修饰 ⇒ return entry（展开后的值）", this, requests);'),
    ("W10 九个取值点都在（Coerced/Animated/Expression/Base 全覆盖）", 'WpfLinuxDpValueTrace.W10Pick("**BaseValue**（Coerced 分支的兜底'),
    ("W10 也覆盖 IsCoercedWithCurrentValue 那一支", 'SetCurrentDeferredValue 那一支'),
    ("上游三个 return 逐字未动", "                return this;"),
    ("上游 unset 分支逐字未动", "                return unsetEntry;"),
    ("上游断言与 return entry 逐字未动",
     "Debug.Assert(entry.IsDeferredReference == (entry.Value is DeferredReference),"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-windowsbase-entry-flatten-trace.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + {n} 处 T1c **只读插桩**（第 6 批 W7）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，不会静默产出未打补丁的副本。
//
// 为什么打：第 5 批命中 A 格（写侧 deferred、读侧 effectiveEntry 非 deferred 且值仍是旧串）
//   ⇒ 要判"flatten 有没有把 deferred 标记吞掉"。三个 return 分支各打一行（只对 deferred 条目打 ⇒ 有界）。
//   ⚠️ 上游 :423 那条 `Debug.Assert(IsDeferredReference == (Value is DeferredReference))` **正是**这一族的断言，
//      但 `Debug.Assert` 是 `[Conditional("DEBUG")]` ⇒ Release 下被编译掉（所以它没替我们发现）。
//   依赖另一批的 tracer 类（`DependencyObject.Linux.cs`，同 namespace 同程序集）。
//   开关：WPF_LINUX_INPUT_TRACE / WPF_LINUX_MSGFLOW_TRACE；缺省关、≤200 行、只打印。
"""

MARKER_BEGIN = ("  <!-- ==== T1c 第 6 批：EffectiveValueEntry.GetFlattenedEntry 的三个 return"
                "（patch-windowsbase-entry-flatten-trace.py 注入）==== -->")
MARKER_END = "  <!-- ==== T1c 第 6 批（flatten）结束 ==== -->"


def _count(h, n):
    return h.count(n)


def _sha(t):
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def _header():
    return HEADER.replace("{rel}", UP_REL).replace("{n}", str(len(EDITS)))


def _build():
    up = os.path.join(ROOT, UP_REL)
    if not os.path.exists(up):
        print(f"[失败] 找不到上游 {up}")
        return None, 1
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] EffectiveValueEntry {name}：上游出现 {n} 次（要求 1）")
        if n != 1:
            bad = True
    if bad:
        print("[失败] 锚点缺失或重复 —— 上游这段改过了？**不做任何静默降级**。")
        return None, 1

    out = text
    for _, anchor, repl in EDITS:
        out = out.replace(anchor, repl, 1)

    up_throws, out_throws = _count(text, "throw "), _count(out, "throw ")
    if up_throws != out_throws:
        print(f"[失败] `throw` 条数变了：上游 {up_throws} → 生成物 {out_throws}")
        return None, 1
    if (_count(text, "{") - _count(text, "}")) != (_count(out, "{") - _count(out, "}")):
        print("[失败] 大括号盈亏变化（只读插桩不该改结构）")
        return None, 1
    print(f"[断言] 大括号盈亏一致：{_count(out, '{')} / {_count(out, '}')}")
    print(f"[断言] `throw` {up_throws}=={out_throws}；行数 {len(text.splitlines())} → "
          f"{len(out.splitlines())}（+{len(out.splitlines()) - len(text.splitlines())}）")

    full = _header() + out
    for name, needle in REQUIRED:
        if needle not in full:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")
    return full, 0


def prove():
    up = os.path.join(ROOT, UP_REL)
    with open(up, encoding="utf-8-sig") as f:
        text = f.read()
    body, where = None, None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            raw = f.read()
        if raw.startswith(_header()):
            body = raw[len(_header()):]
            where = f"落盘生成物 {os.path.relpath(GEN, ROOT)}"
        else:
            print("[注意] 落盘生成物文件头与当前脚本不一致（上一版产的）=> 退化为**内存**证明")
    if body is None:
        full, rc = _build()
        if rc:
            return 1
        body = full[len(_header()):]
        where = "**内存**（生成物尚未产出/已过时）"
    cur = body
    for name, anchor, repl in reversed(EDITS):
        n = _count(cur, repl)
        if n != 1:
            print(f"[失败] 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）")
            return 1
        cur = cur.replace(repl, anchor, 1)
    if cur != text:
        print("[失败] 摘掉插桩后与上游**不一致** ⇒ 这不是「只插入」，别信。")
        return 1
    print(f"[① 只插入] 取自 {where}")
    print(f"[① 只插入] 逆序回代后与上游**逐字节相同** ✓ sha256={_sha(cur)}（== 上游 {_sha(text)}）")
    return 0


def generate(check_only):
    full, rc = _build()
    if rc:
        return rc

    up_to_date = True
    cur = None
    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            cur = f.read()
    if cur != full:
        up_to_date = False
        if not check_only:
            with open(GEN, "w", encoding="utf-8") as f:
                f.write(full)
            print(f"[生成] {os.path.relpath(GEN, ROOT)}：已从上游重生成 sha256={_sha(full)}")
    else:
        print(f"[生成] {os.path.relpath(GEN, ROOT)}：内容已是最新（未重写）sha256={_sha(full)}")

    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    # ── 依赖检查：tracer 类在**第 5 批**的生成物里 ⇒ 那一批的接线必须已在，否则 CS0103 ──
    dep_ok = DEP_MARKER in csproj
    if not dep_ok:
        print(f"[失败] csproj 里找不到**第 5 批**的接线标记（`{DEP_MARKER}`）——"
              "本批的 `WpfLinuxDpValueTrace` 定义在那批的生成物里 ⇒ 只应用本批会 CS0103。"
              " 请先跑 `patch-windowsbase-dpvalue-trace.py`。")
        if check_only:
            return 1

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        if check_only and not up_to_date:
            print("[检查] 生成物过时（需要重新生成）⇒ rc=1")
            return 1
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1

    anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if _count(csproj, anchor) != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
        return 1
    block = (MARKER_BEGIN + "\n"
             "  <ItemGroup>\n"
             f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL}" />\n'
             '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/EffectiveValueEntry.Linux.cs" />\n'
             "  </ItemGroup>\n"
             + MARKER_END + "\n")
    csproj = csproj.replace(anchor, block + anchor, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}")
    print("[注意] `src/Linux/build/port-lib.py WindowsBase` 会整份重写 csproj ⇒ 本块与第 5 批那块都会被抹掉；"
          "重写后必须重跑两个应用器。")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--prove", action="store_true", help="「只插入」机械证明（只读）")
    args = ap.parse_args()
    if args.prove:
        return prove()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
