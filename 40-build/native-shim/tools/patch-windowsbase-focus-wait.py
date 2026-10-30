#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WindowsBase `DispatcherSynchronizationContext.Wait` —— **等待桩 ⇒ 启动即死**（`D-G65`）的修法。

用法
----
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-focus-wait.py --check   # 只读检查
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-focus-wait.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-windowsbase-focus-wait.py --prove   # 「只改那一处」机械证明（只读）

为什么打这一格
--------------
症状（用户会话实测 `/tmp/hc-diag.log`，以及本波的可复现最小装置）：

    Unhandled exception. System.ComponentModel.Win32Exception (50): No CSI structure available
       at MS.Win32.UnsafeNativeMethods.WaitForMultipleObjectsEx(...)          (Shared/…/UnsafeNativeMethodsOther.cs:137 —— result==WAIT_FAILED ⇒ throw)
       at System.Windows.Threading.DispatcherSynchronizationContext.Wait(...)  (上游 :91 —— 本应用器改的那一行)
       at System.Threading.Monitor.Enter_Slowpath(...)
       at System.Windows.ResourceDictionary.GetValue(...)                      (PF ResourceDictionary.cs:485)
    ⇒ **进程死**（概率性：启动期只要 UI 线程在锁上被争用就发）。

三句话的机制（都已实测，见 `src/Linux/build/MilBridge/W56A-report.md`）：
  ① `DispatcherSynchronizationContext` 的构造函数调用 `SetWaitNotificationRequired()`
     ⇒ **CLR 在锁阻塞时会把「等待」交给当前 SynchronizationContext**（`Monitor.Enter_Slowpath` 那一帧）。
  ② `Wait` 上游按 `_dispatcher._disableProcessingCount > 0` 分叉：非零时改调 **Win32**
     `WaitForMultipleObjectsEx`，零时调托管 `SynchronizationContext.WaitHelper`。
  ③ 本工程的 shim 里那条 Win32 函数是**失败桩**（`win32_misc.c:385`：`wpf_set_last_error(50); return WAIT_FAILED;`）
     —— 而且**在原理上不可能修好**：传进去的"句柄"是 .NET 运行期等待子系统内部的原语 id，
     **不是** Win32 HANDLE（宿主 C 代码无法 wait 它）。所以唯一的修法是**别走那条路**。

改什么（**一处**）
------------------
上游 `Wait` 的 if/else 两分支**合一** —— 都返回 `SynchronizationContext.WaitHelper(...)`。
上游分叉的**目的**（"避免 CLR 的锁等待替我们泵消息"）是 **Windows 专属**考量：
Windows 上被 SENT 到本窗口的消息会在 CLR 等待期间被派发 ⇒ 不可预期的重入。
本平台的实测事实：托管 `WaitHelper` **真等待**（无信号 300 ms ⇒ 258 `WAIT_TIMEOUT`；
400 ms 后置信号 ⇒ 0 `WAIT_OBJECT_0`），而且它只碰 .NET 的等待子系统、
**不碰任何 Win32 消息队列** ⇒ 上游那个目的在本平台由它满足，分叉的唯一理由消失。

⚠️ 同族的**第二个站点**（`Shared/MS/Internal/ReaderWriterLockWrapper.cs:287` 的
   `NonPumpingSynchronizationContext.Wait`）**也是无条件**调那条原生等待 —— 它不在本应用器的
   写域内（改它要动 Shared 源、四个工程的编译输入），由 shim 那一层兜底（见 W56A 报告 §1）。

守恒（牙齿）
------------
· 生成物 banner 里写明"由谁生成"，`applier-audit` 的 A 级断言直接查 `EDITS` 的 `repl` 命中数；
· `throw` 条数、大括号盈亏、方法签名（`override` + 参数表）**逐项断言未变**；
· **被删掉的那条调用点是被断言删掉的**（上游 1 → 生成物 0，`DELETIONS`），不是"没找到就算了"；
· 生成物里**禁止出现**`if(_dispatcher._disableProcessingCount > 0)` 与那条原生调用的原文
  （`FORBIDDEN`）——「分叉回来了」必须当场红，而不是静默继续绿。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))

UP_REL = ("src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/"
          "DispatcherSynchronizationContext.cs")
WB_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "WindowsBase")
CSPROJ = os.path.join(WB_DIR, "WindowsBase.Linux.csproj")
GEN = os.path.join(WB_DIR, "DispatcherSynchronizationContext.Linux.cs")

# --------------------------------------------------------------------------------------
#  锚点 = 上游 `Wait` 的**完整方法体**（逐字节）。生成物 = 修法后的同一个方法。
# --------------------------------------------------------------------------------------
ANCHOR = '''        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            if(_dispatcher._disableProcessingCount > 0)
            {
                // Call into native code directly in order to avoid the default
                // CLR locking behavior which pumps messages under contention.
                // Even though they try to pump only the COM messages, any
                // messages that have been SENT to the window are also
                // dispatched.  This can lead to unpredictable reentrancy.
                return MS.Win32.UnsafeNativeMethods.WaitForMultipleObjectsEx(waitHandles.Length, waitHandles, waitAll, millisecondsTimeout, false);
            }
            else
            {
                return SynchronizationContext.WaitHelper(waitHandles, waitAll, millisecondsTimeout);
            }
        }'''

REPLACEMENT = '''        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            // ══ WPF-on-Linux 修法（在册债务 `D-G65`）：上游的两分支**合一**，都走托管 `WaitHelper` ══
            //
            // 上游按 Dispatcher 的「禁用处理」计数分叉：非零时改调 Win32 的等待 API。
            // 那条路的**目的**是 Windows 专属的 —— 避免 CLR 的锁等待替我们泵消息
            // （Windows 上被 SENT 到本窗口的消息会在 CLR 等待期间被派发 ⇒ 不可预期的重入）。
            //
            // Linux 上这个目的不需要靠它，而它本身**在原理上不可能成立**：
            //   ① 传进去的"句柄"是 .NET 运行期等待子系统（`WaitSubsystem`）内部的对象 id，
            //      **不是** Win32 HANDLE ⇒ 宿主的 C 代码无法 wait 它。本工程的 shim 只能
            //      返回失败（`src/WpfGfx.Linux.Native/src/win32_misc.c` 的失败桩），
            //      而失败即抛：`MS.Win32.UnsafeNativeMethods` 的包装在 `result == WAIT_FAILED`
            //      时抛 `Win32Exception`（`Shared/MS/Win32/UnsafeNativeMethodsOther.cs:135`）。
            //   ② 于是只要 UI 线程在锁上被争用（CLR 把"等待"通知给当前 SynchronizationContext），
            //      而此刻该计数非零 ⇒ `Win32Exception (50)` ⇒ 未处理异常 ⇒ **进程死**。
            //      最小复现与两极化读数见 `src/Linux/build/MilBridge/W56A-report.md`。
            //   ③ 托管 `SynchronizationContext.WaitHelper` 在 Linux 上**真等待**（实测：
            //      无信号 300 ms ⇒ 返回 258 `WAIT_TIMEOUT`；400 ms 后置信号 ⇒ 返回 0
            //      `WAIT_OBJECT_0`），且它只碰 .NET 的等待子系统、**不碰任何 Win32 消息队列**
            //      ⇒ 上游那个"防重入"的目的在本平台由它满足。
            //
            // ⇒ 两分支的差别只在 Windows 上成立，本平台**合一**。
            //   ⚠️ 同族的第二个站点 `Shared/MS/Internal/ReaderWriterLockWrapper.cs:287`
            //   （`NonPumpingSynchronizationContext.Wait`）**也是无条件**调那条原生等待，
            //   不在本应用器写域内 ⇒ 由 shim 那一层兜底（见 W56A 报告 §1）。
            return SynchronizationContext.WaitHelper(waitHandles, waitAll, millisecondsTimeout);
        }'''

EDITS = [
    ("W1 `Wait` 两分支合一（`D-G65` 修法：不再走那条在原理上无法实现的 Win32 等待）",
     ANCHOR, REPLACEMENT),
]

# (名称, 上游应出现次数, 生成物应出现次数) —— **被删掉的东西也要有断言**，否则"没找到就算了"
DELETIONS = [
    ("原生等待调用点（上游 :91 的那一行）",
     "MS.Win32.UnsafeNativeMethods.WaitForMultipleObjectsEx(waitHandles.Length", 1, 0),
    ("按「禁用处理」计数的分叉条件",
     "if(_dispatcher._disableProcessingCount > 0)", 1, 0),
]

# 生成物里**必须没有**的东西（分叉/原生调用回来 ⇒ 当场红，而不是静默继续绿）
FORBIDDEN = [
    ("分叉条件回来了", "if(_dispatcher._disableProcessingCount > 0)"),
    ("原生等待调用点回来了", "MS.Win32.UnsafeNativeMethods.WaitForMultipleObjectsEx("),
]

REQUIRED = [
    ("修法标识（供审计/报告定位）", "WPF-on-Linux 修法（在册债务 `D-G65`）"),
    ("方法签名与上游逐字一致（仍是 override，参数表未动）",
     "public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)"),
    ("合并后的唯一返回：托管 WaitHelper",
     "return SynchronizationContext.WaitHelper(waitHandles, waitAll, millisecondsTimeout);"),
    ("生成物说明「为什么本平台可以合一」", "两分支的差别只在 Windows 上成立"),
    ("点名同族第二站点由 shim 兜底", "ReaderWriterLockWrapper.cs:287"),
]

HEADER = """// ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-windowsbase-focus-wait.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/{rel}` 逐字复制 + **1 处修法**（`D-G65`：`Wait` 的两分支合一）。
// 每次运行该脚本都会从上游重读重生成；锚点找不到 / 命中数不符时**报错退出**，
// 不会静默产出未打补丁的副本（那会让"修了"变成一行都没改而门禁照绿）。
"""

MARKER_BEGIN = "  <!-- ==== WPF-on-Linux 修法 D-G65：DispatcherSynchronizationContext.Wait 不再走原生等待（patch-windowsbase-focus-wait.py 注入）==== -->"
MARKER_END = "  <!-- ==== WPF-on-Linux 修法 D-G65 结束 ==== -->"


def _count(haystack, needle):
    return haystack.count(needle)


def _sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _build():
    """读上游 → 锚点/守恒自检 → 替换 → 结构断言。返回 (generated_text 或 None, rc)。"""
    upstream = os.path.join(ROOT, UP_REL)
    if not os.path.exists(upstream):
        print(f"[失败] 找不到上游 {upstream}")
        return None, 1
    with open(upstream, encoding="utf-8-sig") as f:
        text = f.read()

    bad = False
    for name, anchor, _ in EDITS:
        n = _count(text, anchor)
        print(f"[锚点] {name}：上游出现 {n} 次（要求 1）")
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
        print(f"[失败] 大括号盈亏变化：上游 {_count(text, '{')}/{_count(text, '}')} → "
              f"生成物 {_count(out, '{')}/{_count(out, '}')}")
        return None, 1
    print(f"[断言] `throw` {up_throws}=={out_throws}；大括号盈亏一致"
          f"（{_count(out, '{')} / {_count(out, '}')}）")

    for name, needle, want_up, want_gen in DELETIONS:
        got_up, got_gen = _count(text, needle), _count(out, needle)
        ok = (got_up == want_up and got_gen == want_gen)
        print(f"[{'断言' if ok else '失败'}] 删项 `{name}`：上游 {got_up}（期望 {want_up}）→ "
              f"生成物 {got_gen}（期望 {want_gen}）")
        if not ok:
            return None, 1

    full = HEADER.replace("{rel}", UP_REL) + out
    for name, needle in REQUIRED:
        if needle not in full:
            print(f"[失败] 生成物缺少结构断言：{name}")
            return None, 1
    print(f"[断言] 结构断言 {len(REQUIRED)}/{len(REQUIRED)} 全中")
    for name, needle in FORBIDDEN:
        if needle in full:
            print(f"[失败] 生成物里出现**禁止项**：{name} ⇒ 修法没生效或被人改回去了")
            return None, 1
    print(f"[断言] 禁止项 {len(FORBIDDEN)}/{len(FORBIDDEN)} 全无（分叉与原生调用都没有回来）")
    print(f"[断言] 行数 {len(text.splitlines())} → {len(out.splitlines())} "
          f"（{len(out.splitlines()) - len(text.splitlines()):+d}）")
    return full, 0


def _header():
    return HEADER.replace("{rel}", UP_REL)


def prove():
    """「只改那一处」机械证明：把修法**逆代**回去后必须与上游 sha256 逐字节相同。"""
    upstream = os.path.join(ROOT, UP_REL)
    with open(upstream, encoding="utf-8-sig") as f:
        text = f.read()

    if os.path.exists(GEN):
        with open(GEN, encoding="utf-8") as f:
            body = f.read()
        head = _header()
        if not body.startswith(head):
            print(f"[失败] {os.path.relpath(GEN, ROOT)} 不是本脚本产出的（文件头对不上）")
            return 1
        body = body[len(head):]
        where = f"落盘生成物 {os.path.relpath(GEN, ROOT)}"
    else:
        full, rc = _build()
        if rc:
            return 1
        body = full[len(_header()):]
        where = "**内存**（生成物尚未产出）"

    cur = body
    for name, anchor, repl in reversed(EDITS):
        n = _count(cur, repl)
        if n != 1:
            print(f"[失败] 逆向回代 {name}：`repl` 命中 {n} 次（要求 1）⇒ 生成物被手改过？")
            return 1
        cur = cur.replace(repl, anchor, 1)

    if cur != text:
        print("[失败] 逆代后与上游**不一致** ⇒ 这不是「只改那一处」，别信。")
        return 1
    print(f"[① 只改一处] 取自 {where}")
    print(f"[① 只改一处] 逆代后与上游**逐字节相同** ✓ sha256={_sha(cur)}")
    print(f"[① 只改一处] 上游 sha256={_sha(text)}（两条相同即证明：除 `Wait` 那一处外一个字节没动）")
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
            print(f"[生成] {os.path.relpath(GEN, ROOT)}：已从上游重生成（1 处修法）sha256={_sha(full)}")
    else:
        print(f"[生成] {os.path.relpath(GEN, ROOT)}：内容已是最新（未重写）sha256={_sha(full)}")

    # ---- csproj 接线（幂等；只加自己那一块）----
    if not os.path.exists(CSPROJ):
        print(f"[失败] 找不到 {os.path.relpath(CSPROJ, ROOT)}")
        return 1
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    if MARKER_BEGIN in csproj:
        print("[接线] csproj 已就位（幂等，不改）")
    elif check_only:
        print("[接线] csproj **未接线** —— 需要运行一次不带 --check 的本脚本")
        return 1
    else:
        anchor = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
        if _count(csproj, anchor) != 1:
            print(f"[失败] csproj 里 Sdk.targets 锚点出现 {_count(csproj, anchor)} 次（要求 1）")
            return 1
        block = (MARKER_BEGIN + "\n"
                 "  <ItemGroup>\n"
                 f'    <Compile Remove="$(UpstreamWpfRoot){UP_REL}" />\n'
                 '    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/DispatcherSynchronizationContext.Linux.cs" />\n'
                 "  </ItemGroup>\n"
                 + MARKER_END + "\n")
        csproj = csproj.replace(anchor, block + anchor, 1)
        with open(CSPROJ, "w", encoding="utf-8") as f:
            f.write(csproj)
        print(f"[接线] 已注入到 {os.path.relpath(CSPROJ, ROOT)}（Remove 上游 + Include 生成物）")

    # ---- 假绿防线：生成物在、接线丢了 ⇒ 上游那份会被编译、修法**静默消失** ----
    print("[注意] `src/Linux/build/port-lib.py WindowsBase` 会**整份重写** csproj ⇒ 本块会被抹掉；"
          "重写后必须重跑本脚本（不带 --check）。")
    print("        接线丢失**不会报编译错**、也不会改任何 rc，只会让 `Win32Exception (50)` 回来 ⇒ 别把绿读成修好了。")

    if check_only:
        print("[检查] " + ("生成物与接线都已就位且与上游同步" if up_to_date
                           else "生成物缺失/与上游不同步（需要重新生成）"))
        return 0 if up_to_date else 1

    print("\n下一步（重建权威 `windowsbase`）：")
    print("  . src/Linux/build/selfbuilt-config.sh   # ⇒ SELFBUILT_CONFIG")
    print("  dotnet build src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj -c \"$SELFBUILT_CONFIG\" -m:1 --nologo")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只检查，不修改任何文件")
    ap.add_argument("--prove", action="store_true", help="「只改那一处」机械证明（只读）")
    args = ap.parse_args()
    if args.prove:
        return prove()
    return generate(args.check)


if __name__ == "__main__":
    sys.exit(main())
