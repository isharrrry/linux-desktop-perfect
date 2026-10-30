#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""补丁 N 应用器：让 **assert 的原文在 Linux 上可见**（**无参运行 = 应用**，家族约定）。

    python3 src/WpfGfx.Linux.Native/tools/patch-shared-invariant-failfast.py            # = 应用（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-shared-invariant-failfast.py --check    # 只读（未就位 ⇒ rc=1）

【它挡的是什么 —— "诊断能力本身被掩蔽"】
  `textbox-edit` 那条路上某个不变量被打破，本来应当看到断言原文；实际看到的却是：
```
NullReferenceException at MS.Internal.Invariant.get_IsDialogOverrideEnabled()   ← **FailFast 自己 NRE**
  ← Invariant.FailFast ← Invariant.Assert(condition, msg) ← TextContainer.GetNodeAndEdgeAtOffset ← …
```
  ⇒ **assert 原文被吃掉**，只剩不透明的 NRE + abort。**先修这个（让诊断可见），(a) 才有得查。**

【根因（逐跳，`upstream/.../Shared/MS/Internal/Invariant.cs`）】
  · `:194` `if (IsDialogOverrideEnabled)` → `:222` 该属性 getter
  · `:232` `key = Registry.LocalMachine.OpenSubKey("Software\\\\Microsoft\\\\.NETFramework");`
  · Unix 上 `Microsoft.Win32.Registry.LocalMachine` **是 null**（不是抛异常）⇒ **NRE**
  ⇒ 与补丁 G / J / M **同一家族**（Registry 根键在 Unix 上为 null），这一处落在 **assert 的失败路径**上。

【修法（两处，都不许把 assert 变成 no-op、不许吞消息）】
  ① `Registry.LocalMachine?.OpenSubKey(…)` ⇒ Linux 上 `IsDialogOverrideEnabled` 恒 `false`，
     失败路径不再崩在"准备报错"这一步；
  ② **失败路径先主动把原文写到 stderr**：`Debug.Fail(...)` 是 `[Conditional("DEBUG")]`，
     **Release 下整句被编译掉**，而 `Environment.FailFast(SR.InvariantFailure)` 只有一个通用串
     ⇒ 原文仍会丢。所以新增 `PrintInvariantFailure`（写 stderr）并把原文拼进 FailFast 文本。
  **语义**：仍然 FailFast（如实报错、进程照旧终止），**只是把原文带出来**。

【生成式补丁 + csproj 接线】生成 `src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Invariant.Linux.cs`（该文件只编进 WindowsBase：
  `src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj:52`），并注入 Remove/Include（波重生成后由本脚本重放）。
"""

import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
TARGET = os.path.join(WB_DIR, "Invariant.Linux.cs")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")

UPSTREAM_REL = "Shared/MS/Internal/Invariant.cs"
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src", UPSTREAM_REL)

CSPROJ_MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
CSPROJ_UPSTREAM_INCLUDE = ('    <Compile Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/'
                           + UPSTREAM_REL + '" />')

MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux 补丁 N：assert 原文可见"
                "（由 src/Linux/tools/patch-shared-invariant-failfast.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux 补丁 N 结束 ==== -->"

ANCHOR_REG = '                key = Registry.LocalMachine.OpenSubKey("Software\\\\Microsoft\\\\.NETFramework");'
REPL_REG = ('                // [补丁 N] Unix 上 Registry.LocalMachine 是 **null**（不是抛异常）——\n'
            '                //   这一处落在 **assert 的失败路径**上 ⇒ 修前是 "准备报错时自己 NRE"，\n'
            '                //   把真正的断言原文吃掉了。\n'
            '                key = Registry.LocalMachine?.OpenSubKey("Software\\\\Microsoft\\\\.NETFramework");')

ANCHOR_FAILFAST = '''        [DoesNotReturn]
        private static void FailFast(string message, string detailMessage)
        {
            if (IsDialogOverrideEnabled)'''
REPL_FAILFAST = '''        [DoesNotReturn]
        private static void FailFast(string message, string detailMessage)
        {
            // [补丁 N] **先把原文写出去**：`Debug.Fail` 带 `[Conditional("DEBUG")]`，Release 下整句被编译掉，
            //   而 `Environment.FailFast(SR.InvariantFailure)` 只有一个通用串 ⇒ 不加这一步，**原文照样丢**。
            //   语义不变：仍然 FailFast（进程照旧终止），只是把"哪个不变量、什么条件"带出来。
            PrintInvariantFailure(message, detailMessage);

            if (IsDialogOverrideEnabled)'''

ANCHOR_TAIL = '''            Debug.Fail($"Invariant failure: {message}", detailMessage);
            Environment.FailFast(SR.InvariantFailure);
        }'''
REPL_TAIL = '''            Debug.Fail($"Invariant failure: {message}", detailMessage);
            Environment.FailFast(BuildInvariantFailureText(message, detailMessage));
        }

        // [补丁 N] 新增：把断言原文（不变量条件 + 明细）打到 stderr；Linux 上这是**唯一**能看到原文的通道。
        private static void PrintInvariantFailure(string message, string detailMessage)
        {
            try
            {
                System.Console.Error.WriteLine(BuildInvariantFailureText(message, detailMessage));
                System.Console.Error.Flush();
            }
            catch
            {
                // 输出失败不改变语义（仍然 FailFast）。
            }
        }

        // [补丁 N] 新增：拼出带原文的终止文本（`SR.InvariantFailure` 是通用串，单靠它定位不到现场）。
        private static string BuildInvariantFailureText(string message, string detailMessage)
        {
            string text = SR.InvariantFailure;
            if (!string.IsNullOrEmpty(message))
            {
                text += ": " + message;
            }
            if (!string.IsNullOrEmpty(detailMessage))
            {
                text += " — " + detailMessage;
            }
            return text;
        }'''

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-shared-invariant-failfast.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 + **补丁 N** 的两处：
//   ① `Registry.LocalMachine?.OpenSubKey(…)` —— Unix 上根键为 null，修前会让 **FailFast 自己 NRE**；
//   ② 失败路径**主动打印断言原文**（`Debug.Fail` 在 Release 下被编译掉，原文否则会丢），
//      并把原文拼进 FailFast 文本。
// 语义：**仍然 FailFast**（进程照旧终止），**不吞消息、不把 assert 变成 no-op**。
//
// ↓↓↓ 以下为上游原文（仅补丁 N 的两处有改动）↓↓↓
"""


def build_patched(text):
    for name, anchor in (("锚点①（Registry.LocalMachine.OpenSubKey）", ANCHOR_REG),
                         ("锚点②（FailFast 头部）", ANCHOR_FAILFAST),
                         ("锚点③（FailFast 尾部）", ANCHOR_TAIL)):
        if text.count(anchor) != 1:
            raise SystemExit(f"[补丁 N] {name} 出现 {text.count(anchor)} 次，期望 1 次 —— 上游变了？")
    out = text.replace(ANCHOR_REG, REPL_REG).replace(ANCHOR_FAILFAST, REPL_FAILFAST).replace(ANCHOR_TAIL, REPL_TAIL)

    # 自检：必须**仍然** FailFast、仍然调 Debug.Fail；且原文必须出现在输出里
    for must in ("Environment.FailFast(BuildInvariantFailureText(message, detailMessage));",
                 "Debug.Fail($" if False else "Debug.Fail(",
                 "PrintInvariantFailure(message, detailMessage);",
                 "System.Console.Error.WriteLine(BuildInvariantFailureText"):
        if must not in out:
            raise SystemExit(f"[补丁 N] 自检失败：生成物里缺少 `{must}`")
    return out


def wire_csproj(csproj_path, check_only):
    if not os.path.exists(csproj_path):
        print(f"[失败] 找不到 csproj：{csproj_path}")
        return 1
    with open(csproj_path, encoding="utf-8-sig") as f:
        csproj = f.read()
    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次本脚本（无参即应用）")
        return 1
    if CSPROJ_MARKER not in csproj or CSPROJ_UPSTREAM_INCLUDE not in csproj:
        print("[失败] csproj 锚点对不上（Sdk.targets 或上游 Include 行找不到）")
        return 1
    block = "\n".join([
        MARKER_BEGIN,
        "  <ItemGroup>",
        f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/{UPSTREAM_REL}" />',
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/Invariant.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
        "",
    ])
    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(csproj.replace(CSPROJ_MARKER, block + CSPROJ_MARKER, 1))
    print(f"[接线] 已注入 2 行到 {os.path.relpath(csproj_path, ROOT)}")
    return 0


def main():
    ap = argparse.ArgumentParser(description="补丁 N 应用器（**无参运行 = 应用**）")
    ap.add_argument("--check", action="store_true", help="只检查，不写盘（未就位 ⇒ 退出码 1）")
    ap.add_argument("--out", metavar="PATH", help="额外写一份生成物（核对用）")
    ap.add_argument("--csproj", metavar="PATH", help="覆盖 csproj 路径（自测用）")
    args = ap.parse_args()

    if not os.path.isfile(UPSTREAM):
        print(f"[失败] 找不到上游：{UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        original = f.read()

    patched = build_patched(original)
    content = HEADER.format(upstream=UPSTREAM_REL) + patched
    print(f"[补丁 N] 上游：{os.path.relpath(UPSTREAM, ROOT)}；行数 {len(original.splitlines())} → {len(patched.splitlines())}")

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[补丁 N] 已另写一份：{args.out}")

    up_to_date = os.path.exists(TARGET)
    if up_to_date:
        with open(TARGET, encoding="utf-8") as f:
            up_to_date = (f.read() == content)

    if args.check:
        print(f"[检查] {os.path.relpath(TARGET, ROOT)}：{'内容已是最新' if up_to_date else '缺失/与上游不同步'}")
        rc = 0 if up_to_date else 1
    elif up_to_date:
        print(f"[生成] {os.path.relpath(TARGET, ROOT)}：内容已是最新（未重写）")
        rc = 0
    else:
        os.makedirs(os.path.dirname(TARGET), exist_ok=True)
        with open(TARGET, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[生成] {os.path.relpath(TARGET, ROOT)}：已从上游重生成")
        rc = 0

    wire = wire_csproj(args.csproj or CSPROJ, args.check)
    print("\n下一步：python3 src/Linux/build/port-lib.py WindowsBase && dotnet build src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj -m:1")
    return 0 if (rc == 0 and wire == 0) else 1


if __name__ == "__main__":
    sys.exit(main())
