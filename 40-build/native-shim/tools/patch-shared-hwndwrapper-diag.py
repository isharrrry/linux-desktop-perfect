#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M7b 补丁 P 的应用器：**建窗失败诊断接线**（无参运行 = 生成 + csproj 接线，与家族一致）。

    python3 src/WpfGfx.Linux.Native/tools/patch-shared-hwndwrapper-diag.py
    python3 src/WpfGfx.Linux.Native/tools/patch-shared-hwndwrapper-diag.py --check   # 未就位 ⇒ rc=1

【为什么打】T3 的 wave-9 现场：`Win32Exception(1400)` 起不来（约 1/6），而 `1400` 是本工程
  **自己映射**的复用值（`create_window_utf8` 的两处 + 11+ 处"hwnd 查不到"），单看它分不清
  「X 连接不可用 / `XCreateWindow` 失败 / 句柄非法」。shim 侧（补丁 P 的**原生那一半**）已经把
  真原因记进 `g_wpf.dpy_error` + `g_wpf.x_error`（`XSetErrorHandler` 抓的异步 X 错误），
  并通过 `WpfLinuxWin32_LastError()` 导出；**但全仓无人调用它** ⇒ 这一半就是那个"消费点"。
【为什么不改行为】插入点在**上游本身就有的** `_handle == 0` 失败分支里（那里本来就要
  `hwndSubclass.Dispose()`）：只多打一行 stderr，不抛、不吞、不改返回值、不动 CreateWindowEx 调用。
【有界】每进程 ≤ 8 行；异常全部吞在诊断里（诊断绝不能把失败路径搞坏）。
"""
import argparse, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
TARGET = os.path.join(WB_DIR, "HwndWrapper.Linux.cs")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")
UPSTREAM_REL = "Shared/MS/Win32/HwndWrapper.cs"
UPSTREAM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf", "src", UPSTREAM_REL)

CSPROJ_MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
CSPROJ_UPSTREAM_INCLUDE = ('    <Compile Include="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/'
                           + UPSTREAM_REL + '" />')
MARKER_BEGIN = ("  <!-- ==== WPF-on-Linux M7b 补丁 P：建窗失败诊断（由 src/Linux/tools/patch-shared-hwndwrapper-diag.py 注入）==== -->")
MARKER_END = "  <!-- ==== WPF-on-Linux M7b 补丁 P 结束 ==== -->"

ANCHOR_FAIL = """                if(_handle == 0)
                {
                    // Because the HwndSubclass is pinned, but the HWND creation failed,
                    // we need to manually clean it up.
                    hwndSubclass.Dispose();
                }"""
REPL_FAIL = """                if(_handle == 0)
                {
                    // Because the HwndSubclass is pinned, but the HWND creation failed,
                    // we need to manually clean it up.
                    // [M7b 补丁 P] 诊断接线（**只多打一行 stderr，行为不变**）：把 shim 侧真原因带出来。
                    //   读点是 shim 的 `WpfLinuxWin32_LastError()`（dpy_error + XSetErrorHandler 抓的 x_error）。
                    WpfLinuxShimDiag.ReportCreateFailure(className, name, parent);
                    hwndSubclass.Dispose();
                }"""

ANCHOR_NS_END = "    } // class RawWindow\n}\n"
REPL_NS_END = """    } // class RawWindow

    /// <summary>
    /// [M7b 补丁 P] 建窗失败诊断：`HwndWrapper` 只说 `Win32Exception(1400)`，而 1400 是本工程
    /// **自己映射**的复用值 ⇒ 真原因要问 shim（`WpfLinuxWin32_LastError()`：X 连接不可用 + 异步 X 错误）。
    /// 只打 stderr、有界（≤8 行）、任何异常都吞在诊断里（诊断不许把失败路径搞坏）。
    /// </summary>
    internal static class WpfLinuxShimDiag
    {
        private static int s_lines;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WpfLinuxWin32_LastError", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr ShimLastErrorRaw();

        internal static void ReportCreateFailure(string className, string title, System.IntPtr parent)
        {
            if (s_lines >= 8) return;
            s_lines++;
            string reason;
            try
            {
                System.IntPtr p = ShimLastErrorRaw();
                reason = (p == System.IntPtr.Zero) ? "(空)" :
                         (System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p) ?? "(空)");
            }
            catch (System.Exception e) { reason = "(读 shim LastError 抛 " + e.GetType().Name + ")"; }

            try
            {
                System.Console.Error.WriteLine(
                    "[SHIM_DIAG] HwndWrapper 建窗失败（CreateWindowEx 返回 0） cls=\\"" + className
                    + "\\" title=\\"" + title + "\\" parent=0x" + parent.ToInt64().ToString("x"));
                System.Console.Error.WriteLine("[SHIM_DIAG]   shim 侧真原因："
                    + (string.IsNullOrEmpty(reason) ? "(空：X 层没记原因)" : reason));
                System.Console.Error.Flush();
            }
            catch { }
        }
    }
}
"""

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-shared-hwndwrapper-diag.py **生成**，不要手改。
//
// 内容 = 上游 `{upstream}` 逐字复制 + **补丁 P 的一处诊断接线**：
//   在**上游本来就有的** `_handle == 0`（建窗失败）分支里，多打一行 stderr，内容是 shim 的
//   `WpfLinuxWin32_LastError()`（`dpy_error` + `XSetErrorHandler` 抓到的 `x_error`）。
//   不抛、不吞、不改返回值、不动 `CreateWindowEx` 调用 —— 只是"让失败自己说话"。
// 目标程序集 = **WindowsBase**（`Dispatcher..ctor → MessageOnlyHwndWrapper → HwndWrapper` 都在这）。
// 每次运行该脚本都从上游重读重生成；锚点计数对不上时**报错退出**，不静默产出未打补丁的副本。
//
// ↓↓↓ 以下为上游原文（仅补丁 P 的一处有改动）↓↓↓
"""


def build_patched(text):
    if text.count(ANCHOR_FAIL) != 1:
        raise SystemExit(f"[补丁 P] 锚点①（`_handle == 0` 失败分支）出现 {text.count(ANCHOR_FAIL)} 次，期望 1 次 —— 上游变了？")
    if text.count(ANCHOR_NS_END) != 1:
        raise SystemExit(f"[补丁 P] 锚点②（文件尾 `}} // class RawWindow`）出现 {text.count(ANCHOR_NS_END)} 次，期望 1 次 —— 上游变了？")
    patched = text.replace(ANCHOR_FAIL, REPL_FAIL).replace(ANCHOR_NS_END, REPL_NS_END)
    # 自检：上游原有的清理动作仍在（没有把失败分支改坏）
    if "hwndSubclass.Dispose();" not in patched:
        raise SystemExit("[补丁 P] 自检失败：`hwndSubclass.Dispose();` 丢了（失败分支被改坏）")
    if "_handle = UnsafeNativeMethods.CreateWindowEx(" not in patched:
        raise SystemExit("[补丁 P] 自检失败：`CreateWindowEx` 调用被改动")
    if patched.count("WpfLinuxShimDiag.ReportCreateFailure(") != 1:
        raise SystemExit("[补丁 P] 自检失败：诊断调用不是恰好 1 处")
    if "WpfLinuxWin32_LastError" not in patched:
        raise SystemExit("[补丁 P] 自检失败：没有读 shim 的 LastError（诊断接线没接上）")
    return patched


def wire_csproj(check_only):
    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 csproj：{CSPROJ}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0
    if check_only:
        print("[接线] csproj **未接线** —— 需要运行一次本脚本（无参即应用）")
        return 1
    if CSPROJ_MARKER not in csproj:
        print(f"[失败] csproj 里找不到 Sdk.targets 锚点：{CSPROJ_MARKER}")
        return 1
    if CSPROJ_UPSTREAM_INCLUDE not in csproj:
        print(f"[失败] csproj 里找不到上游 Include 行，接线锚点对不上：\n       {CSPROJ_UPSTREAM_INCLUDE}")
        return 1
    block = "\n".join([
        MARKER_BEGIN,
        "  <ItemGroup>",
        f'    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/{UPSTREAM_REL}" />',
        '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/HwndWrapper.Linux.cs" />',
        "  </ItemGroup>",
        MARKER_END,
        "",
    ])
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj.replace(CSPROJ_MARKER, block + CSPROJ_MARKER, 1))
    print(f"[接线] 已注入 2 行到 {os.path.relpath(CSPROJ, ROOT)}（Remove 上游 HwndWrapper.cs + Include 生成物）")
    return 0


def generate(check_only, out_path=None, csproj_path=None, show_diff=False):
    if not os.path.isfile(UPSTREAM):
        print(f"[失败] 找不到上游文件：{UPSTREAM}")
        return 1
    with open(UPSTREAM, encoding="utf-8-sig") as f:
        original = f.read()
    patched = build_patched(original)
    content = HEADER.format(upstream=UPSTREAM_REL) + patched
    print(f"[补丁 P] 上游：{os.path.relpath(UPSTREAM, ROOT)}")
    print("[补丁 P] 改动：`_handle == 0` 分支内插入 `WpfLinuxShimDiag.ReportCreateFailure(...)`"
          "（只打 stderr；读 shim 的 WpfLinuxWin32_LastError）")
    print(f"[补丁 P] 行数 {len(original.splitlines())} → {len(patched.splitlines())}"
          f"（+{len(patched.splitlines()) - len(original.splitlines())}，不含生成头）")
    if show_diff:
        import difflib
        for line in difflib.unified_diff(original.splitlines(), patched.splitlines(), lineterm="", n=1):
            print("  " + line)
    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"[补丁 P] 已另写一份（核对用）：{out_path}")
    up_to_date = os.path.exists(TARGET)
    if up_to_date:
        with open(TARGET, encoding="utf-8") as f:
            up_to_date = (f.read() == content)
    if check_only:
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
    wire_rc = wire_csproj(check_only) if csproj_path is None else 0
    return 0 if (rc == 0 and wire_rc == 0) else 1


def main():
    ap = argparse.ArgumentParser(description="补丁 P 应用器（无参 = 生成 + csproj 接线）")
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件（未就位 ⇒ 退出码 1）")
    ap.add_argument("--out", metavar="PATH")
    ap.add_argument("--diff", action="store_true")
    ap.add_argument("--csproj", metavar="PATH")
    args = ap.parse_args()
    return generate(args.check, args.out, args.csproj, args.diff)


if __name__ == "__main__":
    sys.exit(main())
