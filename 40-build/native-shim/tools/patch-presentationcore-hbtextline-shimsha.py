#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WAVE17 · 车道 W17C · §1 P1：把「本产物编译进去的那一份 shim 源」的内容 sha256
写进 PresentationCore 的程序集级元数据（`AssemblyMetadata("HbTextLineShimSha", <sha256>)`）。

    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hbtextline-shimsha.py --check   # 只读
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hbtextline-shimsha.py           # 生成 + 接线（幂等）
    python3 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hbtextline-shimsha.py --prove   # 「只插入」机械证明（只读）

【为什么需要】
  `hbtextline_shim_stale` 这条位原本只能给出**下界**：T17A 的
  `src/Linux/build/MilBridge/tools/shim-in-artifact.sh` 在**编译后 DLL 的托管堆**里找"只有最新
  shim 修订才有的标识符"，所以一次**不引入新符号**的 shim 改动会让它退化（它自己写明了
  这条边界）。本件把"产物里写着哪个 shim 内容"变成**直接读数**：
    · 构建时算 `src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.HbTextLine.cs` 的 sha256；
    · 写进程序集级 `AssemblyMetadata`；
    · 读侧 `src/Linux/build/MilBridge/tests/ShimShaReader/`（PEReader + MetadataReader，**不加载程序集**）
      把"产物说的"与"现树算的"逐字比 ⇒ `no` / `yes` / `NOINFO` 三态。
  ⇒ 判据从"有没有新符号"里解耦：**纯注释改动也会让等号变红**（实测见 W17C 报告 §4/§6.2）。

【为什么要有这个应用器（2026-09-16 的事故，本件就是它的修法）】
  W17C 第一版是**手改** `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj`
  插入 `<Import Project="HbTextLineShimSha.targets" />`。而这一份 csproj 是
  **port-lib 的生成物**：波 `close-wave-w17` 第 1 步 `python3 src/Linux/build/port-lib.py PresentationCore`
  **整份重写**它 ⇒ 那 6 行（468 B）被**静默**抹掉（实测：csproj 由 `26ce64b8f4452ed4`
  变回 `e2558faa0cc6b5d1`，`grep -c HbTextLineShimSha` = 0），权威 `pc` 随之失去元数据
  （`ac16320a14f549d4`，读侧给 `NOINFO reason=attribute-absent`）。
  ⇒ **改生成物 = 白改**。本应用器把接线放进**可重放的生成通道**：
    · 生成 `src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HbTextLineShimSha.targets`（**单一写者 = 本脚本**，
      与 apartment/lineheight-trace 的"一个应用器一个生成物"同款纪律）；
    · 往 csproj 的 `Sdk.targets` 锚点前**只插入** 3 行（MARKER_BEGIN / Import / MARKER_END）；
    · `integration-wave.sh` 的 `patch-presentation*` 兜底 glob 会自动执行本脚本
      （作者未把它写进 `APPLIERS_EXPLICIT`，因为那是**别人**的脚本，且兜底本来就覆盖它）
      —— 它已被登记进 `src/Linux/build/MilBridge/tools/applier-audit-expected.txt`，于是"被摘掉"会**判红**。

【幂等 / 回滚】
  · 重复运行 0 改动（逐字节比对内容，相同就不重写，避免动时间戳触发全量重编）。
  · 回滚 = 删掉 csproj 里的 3 行接线 + 删掉 `.targets` 生成物；或重跑
    `port-lib.py PresentationCore` 后**不要**再跑本脚本（那就回到事故形态：读侧 `NOINFO`）。

【注意（本应用器**不**改任何行为）】
  · 只加一条 assembly 级 attribute ⇒ 改的是**身份**，不是渲染/排版语义；
  · 生成物落在 `$(IntermediateOutputPath)`（仓内 `obj/`，`src/Linux/build/artifact-src-fp.py` 把
    `bin/obj/.artifacts` **排除**在指纹输入之外）⇒ **不动** `ARTIFACT-SRC-FP`；
  · 生成文件的内容只有 sha256 的十六进制串 ⇒ **无时间戳、无路径、无主机名**（确定性）。
"""

import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
PC_DIR = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "PresentationCore")
CSPROJ = os.path.join(PC_DIR, "PresentationCore.Linux.csproj")
TARGETS_PATH = os.path.join(PC_DIR, "HbTextLineShimSha.targets")   # ⚠️ 不能叫 TARGETS_PATH：
# applier-audit 把模块级 `TARGETS_PATH` 当成"多文件编辑表"（[label, 生成物, [(..)]]），
# 撞名会让审计去迭代一个字符串（我实测过：报"缺生成物 <Project>"）。
SHIM = os.path.join(ROOT, "src", "Microsoft.DotNet.Wpf.Linux", "src", "shims", "PresentationCore.HbTextLine.cs")
READER = os.path.join("src", "Linux", "build", "MilBridge", "tests", "ShimShaReader")

MARKER_BEGIN = ("  <!-- ==== WAVE17 · W17C：shim 内容 sha256 进程序集元数据"
                "（由 src/Linux/tools/patch-presentationcore-hbtextline-shimsha.py 注入）==== -->")
MARKER_END = "  <!-- ==== WAVE17 · W17C：shim sha 接线 结束 ==== -->"

# ── 生成物内容（逐字）────────────────────────────────────────────────────────
# sha256 由本脚本自己复算并比对（TARGETS_SHA256_PIN）⇒ 本字符串被改动会被**当场抓住**，
# 而不是悄悄产出一个"内容变了但没人知道"的生成物。
TARGETS_CONTENT = '''<Project>
  <!-- ============================================================================
       ⚠️ 本文件由 src/WpfGfx.Linux.Native/tools/patch-presentationcore-hbtextline-shimsha.py
          **生成**，不要手改（单一写者纪律）。来源 = WAVE17 车道 W17C 的 §1 P1。
       ============================================================================

       WAVE17 · 车道 W17C · P1：把「本产物编译进去的那一份 shim 源」的内容 sha256
       写进程序集级元数据，使产物侧证据从**下界**（T17A 的 shim-in-artifact.sh：
       靠"只有最新修订才有的标识符"判红）升级成**等号**（内容哈希逐字相等）。

       为什么必须是内容而不是 mtime：纪律 24 记录过 mtime 双向都骗过人 ——
       `cp -p` 还原源码会让判据假绿（mtime 未变），而"同内容重写"会让它假报警。
       本件判据与 mtime 无关：产物里存的 sha 是"编译那一刻的源内容"的函数。

       形态：构建时生成一个只含**一行** assembly attribute 的 .cs，放进
       $(IntermediateOutputPath)（仓内 obj/，被 src/Linux/build/artifact-src-fp.py 显式排除：
       它第 34 行把 bin/obj/.artifacts 排除，故本件**不动** ARTIFACT-SRC-FP）。
       不新增仓内被跟踪的源文件、不要求任何应用器改 csproj 的 Compile 列表。

       确定性（三条，逐条可复算）：
         ① 生成文件的内容**只是** sha256 的十六进制串 ⇒ 不含时间戳、不含路径、
            不含主机名（同一份 shim 源 ⇒ 逐字节同一个生成文件）；
         ② `WriteOnlyWhenDifferent="true"` ⇒ 内容未变时**不重写**（mtime 也不动），
            因此不会把"没变化的构建"变成"每次都变"；
         ③ hash 由 MSBuild 内建任务 `GetFileHash` 现算（**实测存在**：SDK
            10.0.111 的 Microsoft.Build.Tasks.Core.dll 里有该任务，见 W17C 报告
            §2；`GetFileHash` 返回**大写**十六进制 ⇒ 这里显式 ToLowerInvariant()）。

       读取侧：src/Linux/build/MilBridge/tests/ShimShaReader/
       （PEReader + MetadataReader，**不加载程序集**；三态 no / yes / NOINFO）。
       ============================================================================ -->
  <PropertyGroup>
    <!-- 被嵌 sha 的那个源文件；可用 -p: 覆盖（便于做"属性缺省是否生效"的对照） -->
    <HbTextLineShimSrcFile Condition="'$(HbTextLineShimSrcFile)' == ''">$(WpfLinuxRoot)src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.HbTextLine.cs</HbTextLineShimSrcFile>
    <!-- 生成物文件名（固定，不含时间戳） -->
    <HbTextLineShimShaGeneratedFileName>HbTextLineShimSha.g.cs</HbTextLineShimShaGeneratedFileName>
  </PropertyGroup>

  <Target Name="WpfLinux_GenerateHbTextLineShimSha"
          BeforeTargets="CoreCompile"
          Condition="Exists('$(HbTextLineShimSrcFile)')">
    <PropertyGroup>
      <!-- IntermediateOutputPath 由 Sdk.props/Sdk.targets 定义，只能在 Target 内取 -->
      <HbTextLineShimShaGeneratedFile>$(IntermediateOutputPath)$(HbTextLineShimShaGeneratedFileName)</HbTextLineShimShaGeneratedFile>
    </PropertyGroup>

    <GetFileHash Files="$(HbTextLineShimSrcFile)" Algorithm="SHA256">
      <Output TaskParameter="Items" ItemName="_HbTextLineShimHashed" />
    </GetFileHash>
    <PropertyGroup>
      <HbTextLineShimSha256>$([System.String]::Copy('%(_HbTextLineShimHashed.FileHash)').ToLowerInvariant())</HbTextLineShimSha256>
    </PropertyGroup>

    <!-- 生成文件的内容 = 固定头部两行 + 一行 attribute。没有时间戳/路径/版本号。 -->
    <ItemGroup>
      <_HbTextLineShimShaLine Include="#nullable enable" />
      <_HbTextLineShimShaLine Include="// 由 src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/HbTextLineShimSha.targets 生成；请勿手改。" />
      <_HbTextLineShimShaLine Include="[assembly: System.Reflection.AssemblyMetadata(&quot;HbTextLineShimSha&quot;, &quot;$(HbTextLineShimSha256)&quot;)]" />
    </ItemGroup>
    <WriteLinesToFile File="$(HbTextLineShimShaGeneratedFile)"
                      Lines="@(_HbTextLineShimShaLine)"
                      Overwrite="true"
                      WriteOnlyWhenDifferent="true" />

    <ItemGroup>
      <Compile Include="$(HbTextLineShimShaGeneratedFile)" />
      <FileWrites Include="$(HbTextLineShimShaGeneratedFile)" />
    </ItemGroup>

    <Message Importance="high"
             Text="WPF-on-Linux: HbTextLineShimSha=$(HbTextLineShimSha256) （源 %(_HbTextLineShimHashed.Identity)）→ $(HbTextLineShimShaGeneratedFile)" />
  </Target>

  <!-- 源文件缺失时**不许静默跳过**：印一行高优先级告警（构建不会因此成功"装作有值"，
       读取侧会读到"属性缺失" ⇒ NOINFO）。 -->
  <Target Name="WpfLinux_WarnHbTextLineShimShaMissing"
          BeforeTargets="CoreCompile"
          Condition="!Exists('$(HbTextLineShimSrcFile)')">
    <Warning Text="WPF-on-Linux: HbTextLineShimSha 未生成 —— shim 源不存在：$(HbTextLineShimSrcFile) ⇒ 产物里不会有 HbTextLineShimSha 元数据（读取侧会给 NOINFO）" />
  </Target>
</Project>
'''

CSHARP = '''  <Import Project="$(MSBuildThisFileDirectory)HbTextLineShimSha.targets" />
'''
# 只插入 3 行（BEGIN / Import / END）——不含任何 Remove/Include ⇒ 与 port-lib 生成的
# Compile 列表**不相交**，不会与别的应用器争同一批条目。
CSPROJ_BLOCK = MARKER_BEGIN + "\n" + CSHARP + MARKER_END + "\n"
CSPROJ_ANCHOR = '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'

# 机械自检：生成物内容必须逐字节等于这份字面量（防"改内容忘了改期望"）
TARGETS_SHA256_PIN = "608a687bddf1b86249605490e6063129098701ee94dc13f5fdca8444b9e3ffa3"

# ── applier-audit.py 的接口（**别改坏**：审计 A 级从这些声明重算期望文本）─────────
# ── 审计 A 级接口（applier-audit.py）──────────────────────────────────────────
# 本应用器**不改上游**（它生成一个 .targets 并往 csproj 插 3 行），所以：
#   · `UP` / `UP_REL` 都**声明为 None** ⇒ 审计里 `resolve_upstream` 返回 None ⇒
#     `transform_expected` 走**退化分支**（"编辑的替换串必须逐字出现在落盘生成物里"）。
#   · 因此 `REPLACEMENT` 必须**逐字等于**生成物内容（下面就是这么写的）；`ANCHOR` 只作标签。
#   · `GEN_FILE` 必须是**仓内真实生成物路径**（审计要对它做"包含"判断）。
#   · 生成物里有本脚本文件名（banner）⇒ B 级那条"banner 提到本脚本"也成立。
# 现场踩过的两个坑（都留在 W17C 报告 §10）：
#   ① 模块级变量**不能叫 `TARGETS`**（审计把 `TARGETS` 当"多文件编辑表"）—— 故路径叫 TARGETS_PATH；
#   ② 模块级 `GENERATED`+`ANCHOR`+`REPLACEMENT` 会触发审计的兜底分支把**内容串当路径** ⇒ 故内容叫
#      TARGETS_CONTENT。
ANCHOR = "</Project>"
REPLACEMENT = TARGETS_CONTENT
EDITS = [("生成物 = 逐字固定内容（无上游：声明即生成物）", ANCHOR, REPLACEMENT)]

GEN_FILE = TARGETS_PATH
UP = None
UP_REL = None

def _count(haystack, needle):
    return haystack.count(needle)


def _sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def check_pin():
    """生成物内容的 sha256 必须等于 pin（否则本脚本自己就是坏的）。"""
    got = _sha(TARGETS_CONTENT)
    if got != TARGETS_SHA256_PIN:
        print(f"[失败] TARGETS_CONTENT 内容的 sha256 与 pin 不符：\n  实际 {got}\n  pin  {TARGETS_SHA256_PIN}\n"
              f"  ⇒ 要么改了内容，要么 pin 过期；**不许**带着这个不一致继续（否则生成物会静默漂移）。")
        return 1
    return 0


def prove():
    """只读：把"接线就是一次纯插入"这件事机械地证出来。"""
    rc = check_pin()
    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()
    print(f"[证明] 生成物内容 sha256 = {_sha(TARGETS_CONTENT)}（与 pin 一致：{rc == 0}）")
    print(f"[证明] 生成物体积 = {len(TARGETS_CONTENT.encode('utf-8'))} B")
    on_disk = None
    if os.path.exists(TARGETS_PATH):
        with open(TARGETS_PATH, encoding="utf-8") as f:
            on_disk = f.read()
    print(f"[证明] 落盘生成物逐字等于声明内容 = {on_disk == TARGETS_CONTENT}"
          f"（审计 A 级退化分支要求 `REPLACEMENT` 逐字出现在它里面 = {REPLACEMENT in on_disk if on_disk else 'NOFILE'}）")
    print(f"[证明] 声明：UP=None / UP_REL=None（本件不改上游 ⇒ 审计走退化分支）；"
          f"GEN_FILE={os.path.relpath(GEN_FILE, ROOT)}")
    print(f"[证明] 内容里 `</Project>` 出现 {_count(TARGETS_CONTENT, ANCHOR)} 次（要求 1，锚点唯一）")
    print(f"[证明] csproj 里 Sdk.targets 锚点出现 {_count(csproj, CSPROJ_ANCHOR)} 次（要求 1）")
    print(f"[证明] csproj 里 MARKER_BEGIN 出现 {_count(csproj, MARKER_BEGIN)} 次（要求 ∈ {{0,1}}）")
    print(f"[证明] 接线块是「纯插入」：不含 Remove/Include = "
          f"{('Remove' not in CSPROJ_BLOCK) and ('Include' not in CSPROJ_BLOCK)}")
    return rc


def generate(check_only):
    rc = check_pin()
    if rc:
        return rc

    if not os.path.exists(CSPROJ):
        print(f"[失败] 缺 csproj {os.path.relpath(CSPROJ, ROOT)}（先跑 port-lib.py PresentationCore）")
        return 1
    if not os.path.exists(SHIM):
        print(f"[失败] 缺 shim 源 {os.path.relpath(SHIM, ROOT)} ⇒ 本接线没有对象")
        return 1
    if not os.path.isdir(os.path.join(ROOT, READER)):
        print(f"[失败] 缺读侧工具目录 {READER}/ ⇒ 接线会产出没人能读的元数据")
        return 1

    with open(CSPROJ, encoding="utf-8-sig") as f:
        csproj = f.read()

    # ── 生成物 ────────────────────────────────────────────────────────────────
    gen_on_disk = None
    if os.path.exists(TARGETS_PATH):
        with open(TARGETS_PATH, encoding="utf-8") as f:
            gen_on_disk = f.read()
    gen_ok = (gen_on_disk == TARGETS_CONTENT)

    # ── 接线 ─────────────────────────────────────────────────────────────────
    n_begin, n_end = _count(csproj, MARKER_BEGIN), _count(csproj, MARKER_END)
    n_anchor = _count(csproj, CSPROJ_ANCHOR)
    wired = (n_begin == 1 and n_end == 1)

    if n_begin != n_end:
        print(f"[失败] csproj 里 MARKER_BEGIN={n_begin} / MARKER_END={n_end} 不对称 ⇒ 半截接线，拒绝继续"
              f"（请人工检查 {os.path.relpath(CSPROJ, ROOT)}）")
        return 1
    if not wired and n_anchor != 1:
        print(f"[失败] csproj 里 Sdk.targets 锚点出现 {n_anchor} 次（要求 1）⇒ 不知道该往哪插，拒绝继续")
        return 1

    if check_only:
        ok = True
        print(f"[检查] {os.path.relpath(TARGETS_PATH, ROOT)}："
              + ("内容已是最新（未重写，不动时间戳）" if gen_ok else "**缺失/与脚本声明不同步**！"))
        if not gen_ok:
            ok = False
        print(f"[检查] csproj 接线：" + ("已就位" if wired else "**未接线**（需要跑一次不带 --check 的本脚本）"))
        if not wired:
            ok = False
        print(f"[检查] 读侧工具目录 {READER}/：" + ("在" if os.path.isdir(os.path.join(ROOT, READER)) else "**缺**"))
        return 0 if ok else 1

    if gen_ok:
        print(f"[生成] {os.path.relpath(TARGETS_PATH, ROOT)}：内容已是最新（未重写，不动时间戳）")
    else:
        with open(TARGETS_PATH, "w", encoding="utf-8") as f:
            f.write(TARGETS_CONTENT)
        print(f"[生成] {os.path.relpath(TARGETS_PATH, ROOT)}：已写出 sha256={_sha(TARGETS_CONTENT)}"
              f"（{'从缺失' if gen_on_disk is None else '从旧内容'}刷新）")

    if wired:
        print("[接线] csproj 已就位（幂等，不改）")
        return 0

    csproj = csproj.replace(CSPROJ_ANCHOR, CSPROJ_BLOCK + CSPROJ_ANCHOR, 1)
    with open(CSPROJ, "w", encoding="utf-8") as f:
        f.write(csproj)
    print(f"[接线] 已注入 3 行到 {os.path.relpath(CSPROJ, ROOT)}（Sdk.targets 之前；纯插入）")
    print("\n下一步：dotnet build src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/PresentationCore.Linux.csproj -m:1")
    print("       读侧：dotnet src/Linux/build/MilBridge/tests/ShimShaReader → SHIM_SHA=no|yes|NOINFO")
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
