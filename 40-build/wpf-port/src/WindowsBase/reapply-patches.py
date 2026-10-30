#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WindowsBase.Linux.csproj 生成后补丁（可重复执行、幂等）。

背景与 PresentationCore 的同名脚本一致：`src/Linux/build/port-lib.py` 每次运行都会**整份重写**
`src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj`，所以生成物之外必需的改动必须有
可重放的出处。本脚本就是那个出处。

    python3 src/Linux/build/port-lib.py WindowsBase
    python3 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py
    dotnet build src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/WindowsBase.Linux.csproj

补丁清单
--------
F. 用 Linux 版替换 `Shared/MS/Utility/Trace.cs`（ETW 初始化）—— **必需**
   实测（M7b 烟测第一轮，测试宿主**进程崩溃**而非用例失败）：
     `MS.Utility.EventTrace` 的静态构造函数在 Linux 上必然抛
     `TypeInitializationException → PlatformNotSupportedException: Registry is not
      supported on this platform`。
   两次短路都失效：
     · `Environment.OSVersion.Version.Major < 6` —— Unix 上 OSVersion 返回的是
       **内核版本**（本机 6.x），所以该条件为假，短路不掉；
     · `IsClassicETWRegistryEnabled()` —— 读
       `HKEY_CURRENT_USER\\Software\\Microsoft\\Avalon.Graphics\\ClassicETW`，
       `Microsoft.Win32.Registry` 在 Unix 上直接抛 PlatformNotSupportedException。
   影响面：`EventTrace.IsEnabled` 是 Dispatcher / DependencyObject / UIElement /
   LayoutManager 等 **24 个文件、506 处**调用的必经之路；`HwndWrapper` 的**终结器**
   也会走到它 → 终结器线程上的未处理异常 → 进程崩溃。
   处置：`<Compile Remove>` 掉上游 Trace.cs，加入
   `src/Microsoft.DotNet.Wpf.Linux/src/shims/WindowsBase.EventTrace.Shim.cs`（同一 `EventTrace` 表面，静态构造
   改用 `NullTraceProvider`）。语义论证见该 shim 的文件头：
   Linux 上没有 ETW，"无订阅者"正是上游在 Windows 上的默认路径。
   ⚠️ Include 与 Remove **必须同进同出**（少 Remove 是重复定义 CS0101，
      少 Include 是类型缺失 CS0246），所以写在同一个补丁块里。
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(HERE, "WindowsBase.Linux.csproj")
MARKER = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
BEGIN = "  <!-- ==== WPF-on-Linux 补丁开关：以下内容由 reapply-patches.py 追加 ==== -->"
END = "  <!-- ==== WPF-on-Linux 补丁结束 ==== -->"

PATCH_G = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 G：SecurityHelper.ReadRegistryValue 的 null 守卫
       ============================================================================
       上游 Shared/MS/Internal/SecurityHelper.cs:215 直接
           RegistryKey key = baseRegistryKey.OpenSubKey(keyName);
       而 Unix 上 `Registry.CurrentUser` 返回 **null**（不是抛异常），于是 NRE。
       链路：AvTrace.IsWpfTracingEnabledInRegistry → ReadRegistryValue →
       TraceDependencyProperty..cctor → System.Windows.PresentationSource 静态构造失败
       → HwndSource 不可用。
       处置：编译输入换成 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/SecurityHelper.Linux.cs ——
       它由本脚本从上游**逐字复制 + 插入 1 行 null 守卫**生成，因此永远与上游同步。
       生成物的完整内容与守卫行见该文件头部。
       ⚠️ 生成物由 reapply-patches.py 负责刷新；不要手改它。
       ============================================================================ -->
  <ItemGroup>
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SecurityHelper.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/SecurityHelper.Linux.cs" />
  </ItemGroup>
'''

PATCH_F = '''  <!-- ============================================================================
       WPF-on-Linux 补丁 F：Linux 版 MS.Utility.EventTrace（替换上游 Trace.cs）
       ============================================================================
       上游 Trace.cs 的静态构造函数在 Linux 上必然抛异常（详见
       src/Microsoft.DotNet.Wpf.Linux/src/shims/WindowsBase.EventTrace.Shim.cs 顶部的完整推导与实测症状）。
       这里成对地「摘掉上游文件 + 加入 Linux 版」，两个条目不可分离。
       ============================================================================ -->
  <ItemGroup>
    <Compile Remove="$(UpstreamWpfRoot)src/Microsoft.DotNet.Wpf/src/Shared/MS/Utility/Trace.cs" />
    <Compile Include="$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/WindowsBase.EventTrace.Shim.cs" />
  </ItemGroup>
'''


# ── 补丁 G 的生成器 ────────────────────────────────────────────────────────
UPSTREAM_SECURITY_HELPER = os.path.join(
    HERE, "..", "..", "..", "..",
    "src", "Microsoft.DotNet.Wpf", "src",
    "Shared", "MS", "Internal", "SecurityHelper.cs")
GENERATED_SECURITY_HELPER = os.path.join(HERE, "SecurityHelper.Linux.cs")

# 锚点：上游那一行本身。替换 = 守卫 + 原行（逐字保留，缩进也保留）。
ANCHOR = "            RegistryKey key = baseRegistryKey.OpenSubKey(keyName);"
GUARD = (
    "            // ── WPF-on-Linux 补丁 G（由 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py 插入）──\n"
    "            // Unix 上 Microsoft.Win32.Registry.CurrentUser 返回 **null**（不是抛异常），\n"
    "            // 而上游只判了 OpenSubKey 的返回值 → 这里会 NRE。实测链路：\n"
    "            //   AvTrace.IsWpfTracingEnabledInRegistry → SecurityHelper.ReadRegistryValue\n"
    "            //   → TraceDependencyProperty..cctor → PresentationSource 静态构造失败 → HwndSource 不可用。\n"
    "            // Linux 上没有 Windows 注册表：\"读不到 = 未配置\" 是真话，调用方按\"未启用 tracing\" 处理。\n"
    "            if (baseRegistryKey is null) { return null; }\n"
)
GENERATED_HEADER = """// ⚠️ 本文件由 src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/reapply-patches.py **生成**，不要手改。
//
// 内容 = 上游 `upstream/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/SecurityHelper.cs`
//        逐字复制 + 插入 1 行 null 守卫（见下方 PATCH G 标记）。
// 每次运行 reapply-patches.py 都会从上游重读重生成；上游锚点找不到时会**报错退出**，
// 不会静默产出一个未打补丁的副本。
//
// 为什么必须打这个补丁：
//   Unix 上 `Microsoft.Win32.Registry.CurrentUser` 返回 **null**（不是抛异常），
//   而 `ReadRegistryValue` 直接 `baseRegistryKey.OpenSubKey(...)` → NullReferenceException。
//   触发链（T3 跑 HelloWpf 时定位到行、M7b 独立复核）：
//     MS.Internal.AvTrace.IsWpfTracingEnabledInRegistry()      AvTrace.cs:213
//       → SecurityHelper.ReadRegistryValue(Registry.CurrentUser, …)  SecurityHelper.cs:215
//       → MS.Internal.TraceDependencyProperty..cctor            AvTraceMessages.cs:11
//       → System.Windows.PresentationSource 静态构造失败 → HwndSource 不可用。
//   语义：Linux 上没有 Windows 注册表，"读不到 = 未配置"是真话；调用方拿到 null 后
//   走的正是"未启用 tracing"分支，**不丢日志**（Linux 上不存在 managed tracing 的配置源）。
//
// ↓↓↓ 以下为上游原文（仅插入处有 PATCH G 标记）↓↓↓
"""


def generate_security_helper():
    """从上游生成 SecurityHelper.Linux.cs；返回 (ok, 说明)。"""
    if not os.path.exists(UPSTREAM_SECURITY_HELPER):
        return False, f"找不到上游文件 {UPSTREAM_SECURITY_HELPER}"
    with open(UPSTREAM_SECURITY_HELPER, encoding="utf-8-sig") as f:
        text = f.read()
    if ANCHOR not in text:
        return False, (f"上游 {os.path.basename(UPSTREAM_SECURITY_HELPER)} 里找不到锚点，"
                       f"补丁 G 无法应用（上游改过 ReadRegistryValue？）。锚点期望为：\n    {ANCHOR}")
    patched = text.replace(ANCHOR, GUARD + ANCHOR, 1)
    output = GENERATED_HEADER + patched
    # 幂等：内容相同就不重写（避免无谓地触碰时间戳 → 触发增量重编）
    if os.path.exists(GENERATED_SECURITY_HELPER):
        with open(GENERATED_SECURITY_HELPER, encoding="utf-8") as f:
            if f.read() == output:
                return True, "生成物已是最新（内容一致，未重写）"
    with open(GENERATED_SECURITY_HELPER, "w", encoding="utf-8") as f:
        f.write(output)
    return True, f"已从上游重生成（{output.count(chr(10)) + 1} 行，插入 1 行守卫）"


def main():
    ok, note = generate_security_helper()
    print(f"[补丁 G] SecurityHelper.Linux.cs：{note}")
    if not ok:
        print("[失败] 补丁 G 生成失败 —— 不做静默降级，直接退出（否则会编出一个未打补丁的副本）")
        return 1

    with open(CSPROJ, encoding="utf-8-sig") as f:
        text = f.read()

    # 幂等：先摘掉上一次注入的整块
    if BEGIN in text:
        i = text.index(BEGIN)
        j = text.index(END) + len(END) + 1
        text = text[:i] + text[j:]

    if MARKER not in text:
        print("[失败] csproj 里找不到 Sdk.targets import 锚点，请检查生成物")
        return 1

    block = BEGIN + "\n" + PATCH_G + "\n" + PATCH_F + END + "\n"
    text = text.replace(MARKER, block + MARKER)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(text)

    shim = os.path.join(HERE, "..", "..", "..", "..",
                        "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "WindowsBase.EventTrace.Shim.cs")
    print(f"[OK] 已注入补丁 G（SecurityHelper null 守卫）+ F（Linux 版 EventTrace）→ {CSPROJ}")
    if not os.path.exists(shim):
        print(f"[注意] {shim} 不存在 —— 补丁 F 的 Include 会指向一个缺失文件")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
