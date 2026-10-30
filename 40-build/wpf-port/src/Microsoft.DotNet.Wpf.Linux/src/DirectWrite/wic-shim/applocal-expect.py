#!/usr/bin/env python3
"""app-local **期望集合**枚举器（T2，2026-09-14）—— 只读，不改任何文件。

【为什么要它】校验器原先"拿**现存**副本去比权威" ⇒ **副本被删掉 ⇒ 它不在集合里 ⇒ 静默 PASS**。
本脚本把"**期望存在的副本集合**"从"现存文件"解耦出来：期望只由**仓库里的声明式来源**算出来：
  · `<Reference Include="X">`（含 `<HintPath>` 与 `<Private>`）—— 引用名、解析目录、是否随包拷（`Private=false` 不期望副本）
  · `<ProjectReference Include="…">` —— 工程到工程的边
  · 工程自己的输出目录（`AppendTargetFrameworkToOutputPath` + `TargetFramework` ⇒ `bin/<Cfg>[/<TFM>]`）
  · ITEMS 表里的权威路径（权威件本身就是"期望存在"的一条）
**关键性质**：期望集合的**基数不依赖现场文件是否存在**（删掉任何一份副本，本脚本的输出逐字不变）。
【覆盖边界（主控 2026-09-14 任务 5 要求如实报）】原生件（`libwpf*.so` / `wpfgfx_cor3.so`）的拷贝点是
  **shell `cp`**（实测 `src/Linux/build/MilBridge/run.sh:95`、`:228`）与发布脚本 ⇒ **没有声明式来源** ⇒ 本脚本对它们
  输出 `#UNKNOWN`（"这一条期望算不出来"），并列出"需要什么才能算"，**不**退回"只查现存副本"。

输出（stdout，`|` 分隔）：
  #REFDIR|<dir>                          被任何 HintPath 指向的输出目录（解析源）
  #EXPECT|<dir>|<item 文件名>|<来源链>    期望在该目录存在的副本
  #UNKNOWN|<item>|<原因>                 算不出期望的件（如实报，不静默）
  #IMPORT|工程|被导入件|深度               **跟随的显式 `<Import>` 边**（`W70B`/`TASK-1002` 新增通道；被导入
        件里的 `<Reference>`/`<HintPath>`/`<ProjectReference>`/`<TargetFramework>` 与工程自写的一视同仁）
  #UNRESOLVED-IMPORT|工程|表达式|桶        **没解析出来的 import**（桶 ∈ {sdk, repo}）：`sdk` = SDK 侧
        （`Sdk.props` / `$(MSBuildSDKsPath)/…`，按构造不在本仓 import 图里）；`repo` = **本仓接线缺口**
        （`>0` ⇒ 还有一处接线模型没跟进去，**不许当绿**；本件现场 = 0，见报告 §4/§8）。
  #INVISIBLE|<kind>|<file:line>|<原文>   **主扫**（`src/Linux/build/**/*.sh`）里的拷贝点（TAPPS 2026-09-15 增补说明）
        kind ∈ {write, read}；**注意：主扫有两条自带的边界，都由此处的输出显式承认**：
        ① **每文件最多印 3 条只读行**（计数是全量）⇒ 被截掉的条数印在
           `#INVISIBLE-CAPPED|read|<file>|<省略条数>|<该文件全量条数>`
        ② 主扫**只扫 `src/Linux/build/**/*.sh`** ⇒ 它看不见 (a) `src/Linux/build/**` 之外的 `.sh`
           （实测：`src/Linux/tests/**/run-wpftextdemo.sh` 里有 `wpfgfx_cor3.so` 的写点）
           与 (b) 任何 **MSBuild 拷贝**（`Directory.Build.targets` 的 `<Copy>`，实测 2 处）
           ⇒ 这些由 `#INVISIBLE-EXT|kind|file:line|原文` 补报（**补扫**，与主扫不重叠）。
  #SUMMARY|refdirs=N|expect=M|projects=P|unknown=K|unresolved_hintpath=U|
           invisible_copysites=W+R|invisible_write=W|invisible_read=R|
           invisible_capped=C|invisible_ext_write=XW|invisible_ext_read=XR|
           invisible_indirect_write=IW|import_edges=IE|import_unresolved_repo=IR|import_unresolved_sdk=IS
        （**新增的字段一律追加在末尾** ⇒ 旧消费者按位置读前 N 个字段不受影响）
用法：applocal-expect.py <REPO> [HINTPATH_ROOTS]
"""
import os
import re
import sys
import collections

REPO = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")

def _decl_candidates():
    """配置声明件的**候选路径**（按顺序；`#49` §10 裁定 ③ 定死的两条，不许加第三条）。

    ⚠️【`#49` `C1b`（W52C2 2026-09-20）】**顺序就是语义**：
      ① `<argv[1]>/build/SelfBuiltConfig.props` —— 被扫描/被比较的**那棵树自己的**声明
         （沙箱/合成权威根必须能自己声明配置：自检 P 段就靠这一条）。
      ② **脚本相对** `<本文件>/../../../build/SelfBuiltConfig.props` —— 与 `check-applocal-sync.sh:150`
         （`. "$(dirname "$0")/../../../build/selfbuilt-config.sh"`）读的**同一处**；
         用它兜住"调用方树的声明件缺失、但工具本身在一个完好 checkout 里"的情形。
      ③ 两者都没有（或都解析不出合法值）⇒ **不返回默认值** ⇒ 调用方 `NOINFO` + 非 0（见下）。
    """
    here = os.path.dirname(os.path.abspath(__file__))
    return [os.path.join(REPO, "src", "Linux", "build", "SelfBuiltConfig.props"),
            os.path.normpath(os.path.join(here, "..", "..", "..", "..", "..", "src", "Linux", "build", "SelfBuiltConfig.props"))]


def _selfbuilt_config():
    """读**唯一声明**（`src/Linux/build/SelfBuiltConfig.props`）里的自产件配置；**读不到 ⇒ None**。

    ⚠️【`#39` 阶段 2/3】本文件原先在若干处把**自产件配置写死为 Debug**（即拼出「bin 下的 Debug 目录」）⇒ 切 Release 后
    它会去比对**错的权威**（而且**不报错**）。这里与 `src/Linux/build/selfbuilt-config.sh`、`port-lib.py`
    读的是**同一份声明**（import 与读取都不算"第二处逻辑"，写死值才算）。

    ⚠️⚠️【`#49` `C1b`（W52C2 2026-09-20），主控 §10 裁定 ③：**静默回退 `Debug` 必须消失**】
    改前：`except OSError: return "Debug"` / `return … if mm else "Debug"` ⇒ **权威路径会被静默地
    按 Debug 拼出来**，而调用方（`check-applocal-sync.sh`）用的是**真声明**（现场 `Release`）⇒
    两张 ITEMS 表在**合成权威根**里各说各话（W52C 实测：自检 P 段 `MISSING=5` 假红，根因就是这里）。
    **本函数现在没有任何默认值**：两条候选都拿不到合法值 ⇒ 返回 `None` ⇒ 模块级 `NOINFO` + `exit 2`。
    """
    import re as _re
    for decl in _decl_candidates():
        try:
            txt = open(decl, encoding="utf-8").read()
        except OSError:
            continue
        mm = _re.search(r"<WpfLinuxSelfBuiltConfiguration[^>]*>([^<]*)<", txt)
        v = mm.group(1).strip() if mm else ""
        if v in ("Debug", "Release"):            # 合法值只此两个（与 selfbuilt-config.sh 同口径）
            return v
    return None


CFG = _selfbuilt_config()
if CFG is None:
    # 【`NOINFO` 不许当绿（本仓铁律）】**非 0 退出**是这里的判据本体：调用方 `check-applocal-sync.sh`
    #   的 `EXPECT_OK` 只在"命令成功"时为 1 ⇒ 本支路会让它印 `APPSYNC=NOINFO` + `exit 3`（不是 PASS）。
    _cand = " ｜ ".join(_decl_candidates())
    sys.stderr.write("applocal-expect.py: NOINFO 读不到配置声明（候选：%s）⇒ 不猜默认值（改前会静默回退 Debug）；"
                     "把声明件放回其中一处即可\n" % _cand)
    print("#NOINFO|config-decl|%s|读不到配置声明 ⇒ 不猜默认值（改前静默回退 Debug）" % _cand)
    sys.exit(2)

HP_ROOTS = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else REPO)

# 权威表（与 check-applocal-sync.sh 的 ITEMS 保持一致；改一处要同步另一处 —— 由 check 的 --list-items 校验）
ITEMS = [
    ("libwpfwic.so", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so"),
    ("libwpfwin32.so", REPO + "/src/WpfGfx.Linux.Native/bin/libwpfwin32.so"),
    ("DirectWrite.Linux.Provider.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/" + CFG + "/DirectWrite.Linux.Provider.dll"),
    ("WpfGfx.Linux.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/" + CFG + "/net10.0/WpfGfx.Linux.dll"),
    ("ReachFramework.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/" + CFG + "/ReachFramework.dll"),
    # 【W23C / `D-A2`，2026-09-16】PC 原先**两张 ITEMS 表里都没有** ⇒ 它既没有逐份判据（check 侧），
    #   也没有声明式期望（本侧）⇒ **删掉任何一份 PC 副本连 `MISSING` 都不会报**（那是另一半洞）。
    #   补在这里（**声明式来源那一路**：权威路径 + 各 csproj 的 Reference/HintPath/ProjectReference 闭包），
    #   **不手写任何副本清单**。权威 = check-applocal-sync.sh 的 ITEMS 同一路径（两处必须一致）。
    ("PresentationCore.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/" + CFG + "/PresentationCore.dll"),
    # 【`#49` `C1`（车道 W52C，2026-09-20）】PF/WB 原先**两张 ITEMS 表里都没有** ⇒ 它们既没有逐份判据
    #   （check 侧），也没有声明式期望（本侧）⇒ **删掉任何一份 PF/WB 副本连 `MISSING` 都不报**
    #   （`D-A2` 同族）。补在这里：权威路径 + 各 csproj 的 Reference/HintPath/ProjectReference 闭包，
    #   **不手写任何副本清单**。权威路径与 check-applocal-sync.sh 的 `ITEMS` 逐字相同
    #   （**两处必须一致** —— 由 check 的 `--list-items` 逐项校验）。
    ("PresentationFramework.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/" + CFG + "/PresentationFramework.dll"),
    ("WindowsBase.dll", REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/" + CFG + "/WindowsBase.dll"),
    ("wpfgfx_cor3.so", ""),                      # 无单一权威 ⇒ 不覆盖（见文件头）
]
PROPS = {
    "MSBuildThisFileDirectory": None,            # 逐工程填
    "WpfLinuxRoot": REPO + "/",
    "RepoRoot": REPO + "/",
    "WpfLinuxBinDir": REPO + "/build",
    "DwRoot": REPO + "/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite",
    # 【`#49` §10.5 裁定，新项 `C1e`（车道 W52C3，2026-09-20）】**必须跟随声明，不许写字面量。**
    #   本行原来是 `"Debug"` —— 于是"读声明"（`_selfbuilt_config()`，本文件 `:40-78`）与"替换字面量"
    #   **在同一条数据链上分叉**：`ITEMS` 用声明值（现场 `Release`），而 csproj 里的
    #   `$(WpfLinuxSelfBuiltConfiguration)` 被替换成 `Debug` ⇒ `REFDIR`（"被 HintPath 引用 ⇒ 解析源"
    #   那张表）**按另一个配置拼出来** ⇒ 拿它去决定"哪些非宿主目录要判"（`is_refdir()`）＝ 用错的尺子。
    #   实测（W52C2 量化）：只改这一行，`#REFDIR` 14 → 23、且**只在现清单里的目录恰好 1 个**、
    #   **只在跟随声明清单里的 10 个**。`CFG` 就是 `_selfbuilt_config()` 读出来的那个值（无默认值：
    #   读不到时模块级已 `NOINFO` + `exit 2`，见 `:58-68`）⇒ 本行**不是**"换个新字面量"。
    "WpfLinuxSelfBuiltConfiguration": CFG,
    # 【W70B / `TASK-1002`（2026-09-21）】`src/Linux/build/third-party/WpfLinux.props:46` **自己声明**了这条别名
    #   （`WpfLinuxBuildConfiguration Condition="''==''">$(WpfLinuxSelfBuiltConfiguration)`）—— 那是**那一行的
    #   字面语义**，不是这里新造的第二种配置语义（本文件不做"猜配置"，`CFG` 仍只来自 `_selfbuilt_config()`）。
    #   不代入它 ⇒ `WpfLinux.props` 的 11 条 `<HintPath>` 会**全部**落进 `UNRESOLVED`，而工具只印前 10 条
    #   ⇒ 会把真正的未解析项**挤出清单**（"看得见的清单其实不完整"那一族）。
    "WpfLinuxBuildConfiguration": CFG,
    "RootNamespace": "",
}
UNRESOLVED = []


def project_files():
    for dp, dn, fn in os.walk(HP_ROOTS):
        if "/upstream/" in dp + "/" or "/obj/" in dp + "/" or "/.artifacts" in dp:
            continue
        for f in fn:
            if f.endswith(".csproj"):
                yield os.path.join(dp, f)


def subst(text, projdir):
    out = text
    for k, v in PROPS.items():
        val = projdir + "/" if k == "MSBuildThisFileDirectory" else (v or "")
        out = out.replace("$(%s)" % k, val)
    return out


# ════════════════════════════════════════════════════════════════════════════════════════════════════
# 【`W70B` / `TASK-1002`（2026-09-21）】**显式 `<Import>` 闭包** —— 把"**只经
#   `src/Linux/build/third-party/WpfLinux.props` 接线**"的工程纳入期望模型。
#
# 【为什么必须有它（现场判定点，逐行可核）】本文件原先只解析"**工程自己那一份文本**"：
#   `refs_of()`/`project_refs()`（`:157-182`）、`outdirs()`（`:185-190`）、`E()` 里扫描 `<HintPath>` 的
#   `:468-479`，输入**全是** `main()` 在 `:394-412` 传进去的那一份 csproj 文本
#   ⇒ **`<Import>` 声明的接线对模型完全不可见**。`src/Linux/samples/ThirdPartyMini` 是极端形态：它**一条
#   `<Reference>` / `<ProjectReference>` / `<HintPath>` 都没有**，自产件引用**全部**来自被导入的
#   `src/Linux/build/third-party/WpfLinux.props:76-117`（含 `$(TargetFramework)`，`:48`）⇒ `E()` 算出空集
#   ⇒ `main()` 的 `if not e: continue`（`:489-490`）**直接跳过该工程** ⇒ 它两个输出目录里的
#   10 份"**内容 == 权威**"的副本被报成 `UNEXPECTED-EQ`（= `DECL-GAP-EQ`，按现行口径**照样红**）。
#
# 【这不是白名单】**判据是"显式 `<Import>` 图"这一条通用规则**，跟的是**真实接线**：
#   不删 `ITEMS`、不调小任何计数、不手写"哪件副本该在哪个目录"的清单。全仓 `grep -rl 'WpfLinux.props'
#   --include='*.csproj'` = **1** 个工程 ⇒ 本通道的**位移面**就是那个工程（读数见 `src/Linux/build/MilBridge/W70B-report.md` §4）。
#
# 【覆盖边界（**主动承认，不许猜**）】——这些是"本模型看不见的输入"，逐条写在这里而不是留白：
#   ① 只跟**显式** `<Import>`；MSBuild 的**隐式**导入（`Directory.Build.props` / `Directory.Build.targets`）
#      **不跟**。本仓 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Directory.Build.props:29` 里就有一条 `<TargetFramework>`，
#      "隐式导入要不要跟"是**另一个**语义决定（它会给仓内约 120 个工程各注入一个新的求值输入）
#      ⇒ 本件只把"显式 import 图"这一条做实，**并把这条边界印在输出里**（`#UNRESOLVED-IMPORT`）。
#   ② 只跟**仓内、存在、后缀 ∈ {`.props`,`.targets`}** 的文件 ⇒ SDK 侧 import（`Sdk.props`、
#      `$(MSBuildSDKsPath)/…Microsoft.WinFX.targets`）**按构造不在本图里**；它们进 `sdk` 桶
#      （计数 + 逐条登记），**不冒充**"本仓接线缺口"。
#   ③ 被导入件的文本**先剥 XML 注释**再解析：`WpfLinux.props:8-15` 的**用法模板注释**里有一行
#      `<Import Project="WpfLinux.props" />`（不剥 ⇒ 自指环），`SelfBuiltConfig.props:9` 与
#      `Directory.Upstream.props:46` 的注释里也各有一处 `<HintPath>` 字样。
#   ④ **工程自己的文本不剥注释** ⇒ 本通道的改动面被**严格限制在"新增的 import 通道"上**，仓内其余
#      工程的既有读数逐字不动（这一条由报告 §4 的"剥/不剥"对照读数证明）。
IMPORT_MAX_DEPTH = 8
IMPORT_UNRESOLVED = []      # [(consumer_rel, 原文表达式, 桶)]，桶 ∈ {sdk, repo}
IMPORT_EDGES = []           # [(consumer_rel, 被导入件 rel, 深度)]
_SDKISH = re.compile(r"MSBuildSDKsPath|MSBuildToolsPath|MSBuildExtensionsPath|Sdk\.WindowsDesktop|NETCoreSdkDir")


def strip_xml_comments(t):
    """剥离 `<!-- … -->`（跨行）。**只用于被导入件**（见上 ③/④ 的理由）。"""
    return re.sub(r"<!--.*?-->", "", t, flags=re.S)


def literal_props(text):
    """`<Name>值</Name>` ⇒ {Name: 值}（**只认"带闭合标签、值是纯文本"的** ⇒ 不猜、不做二阶求值）。

    只用来解析 `<Import>` 表达式里的"**工程内中转属性**"（实测 `ThirdPartyMini.csproj:60` 的
    `$(_ThirdPartyWinFX)`、`WpfFeatureProbe.csproj:165` 的 `$(_WfpWinFXTargets)` 等，共 5 处）。
    为什么值得做：代入之后**看得出它其实指向 `$(MSBuildSDKsPath)`** ⇒ 该 import 归 `sdk` 桶，
    而不是冒充成"本仓有一处接线模型算不出来"（那会变成假红/假缺口 —— 本件第一版就是
    `import_unresolved_repo=5`，5 条全是这一族，见报告 §4 的自纠）。
    ⚠️ 值里**允许**含 `$(…)`（如 `$(_ThirdPartyWinFX)` 的值就是 `$(MSBuildSDKsPath)/…`）：
    代入后仍含 `$(` 的，由 `resolve_msbuild_path()` 一律判"未解析"，**不会**被当成真路径。
    ⚠️ **不**把工程自写属性代入"声明通道"（`subst()` 一字未改）⇒ 本通道只影响 import 解析。
    """
    out = {}
    for mm in re.finditer(r"<([A-Za-z_][A-Za-z0-9_.]*)>([^<>]*)</\1>", text):
        out.setdefault(mm.group(1), mm.group(2).strip())
    return out


def _file_above(name, start):
    """`$([MSBuild]::GetPathOfFileAbove('X'))` 的语义：从 `start` 起向上找第一个含 X 的目录。"""
    d = os.path.abspath(start)
    while True:
        c = os.path.join(d, name)
        if os.path.exists(c):
            return c
        nd = os.path.dirname(d)
        if nd == d:
            return None
        d = nd


def resolve_msbuild_path(expr, basedir, localprops):
    """把一条 MSBuild 路径表达式解析成绝对路径。⇒ `(path | None, 尽力代入后的字符串)`

    第二个返回值**必须**返回：未解析时的**分桶**（`sdk` / `repo`）只能从它读出来
    —— 例如 `$(MSBuildSDKsPath)/…` 代入后仍含 `MSBuildSDKsPath` ⇒ 那是 SDK 侧，不是本仓缺口。
    """
    s = expr.strip()

    def _gpo(mm):
        args = [a.strip().strip("'\"") for a in mm.group(1).split(",")]
        start = args[1] if len(args) > 1 else basedir
        if not os.path.isabs(start):
            start = os.path.join(basedir, start)
        r = _file_above(args[0], start)
        return r if r else mm.group(0)

    s = re.sub(r"\$\(\[MSBuild\]::GetPathOfFileAbove\(([^)]*)\)\)", _gpo, s)
    # ⚠️ `$(MSBuildThisFileDirectory)` 按**声明所在文件**的目录代入（MSBuild 语义）：本函数由调用方
    #    逐文件传 `basedir`。`subst()` 用的是**工程目录** —— 那是给工程自己的文本用的，两者不许混。
    s = s.replace("$(MSBuildThisFileDirectory)", basedir + "/")
    for k, v in PROPS.items():
        if k == "MSBuildThisFileDirectory":
            continue
        s = s.replace("$(%s)" % k, v or "")
    # 工程内中转属性**代入两趟**（`$(_X)` 的值本身可能又是一层 `$(…)`；实测那 5 处就是这一形态）。
    # 两趟之后仍含 `$(` 的，下面一律返回 None ⇒ **不猜路径**。
    for _ in range(2):
        for k, v in localprops.items():
            s = s.replace("$(%s)" % k, v)
    s = s.replace("\\", "/")
    if "$(" in s:
        return None, s
    p = s if os.path.isabs(s) else os.path.join(basedir, s)
    return os.path.normpath(p), s


def import_closure(fp, text, localprops, seen, depth=0):
    """递归展开**显式 `<Import>`**（覆盖边界见上面 ①~④）。⇒ `[(被导入件文本, 仓内相对路径), …]`

    只收**仓内、存在**的 `.props`/`.targets`；其余一律登记进 `IMPORT_UNRESOLVED`（分桶，**不静默**）。
    环由 `seen`（realpath 集合）断掉 —— 仓内确实有 `PC ⇄ PF ⇄ ReachFramework` 这类环。
    第二元（相对路径）是**声明归属**用的：来源链要能点名"这条 `<Reference>` 出自哪一份被导入件"。
    """
    merged = []
    if depth >= IMPORT_MAX_DEPTH:
        return merged
    for mm in re.finditer(r"<Import\b([^>]*?)/?>", strip_xml_comments(text)):
        attrs = mm.group(1)
        pm = re.search(r'\bProject="([^"]*)"', attrs)
        if not pm:
            continue                                # 无 Project 的 <Import>：MSBuild 里不存在，跳过
        expr = pm.group(1)
        p, partial = resolve_msbuild_path(expr, os.path.dirname(fp), localprops)
        bucket = "sdk" if ('Sdk="' in attrs or _SDKISH.search(partial or "")) else "repo"
        if p is None:
            IMPORT_UNRESOLVED.append((os.path.relpath(fp, REPO), expr, bucket))
            continue
        rp = os.path.realpath(p)
        if not os.path.exists(rp):
            IMPORT_UNRESOLVED.append((os.path.relpath(fp, REPO), expr, bucket))
            continue
        if not rp.endswith((".props", ".targets")):
            continue                                # 非 props/targets（SDK 目标文件等）⇒ 不在本图
        if not rp.startswith(os.path.realpath(REPO) + os.sep):
            continue                                # 仓外（SDK / NuGet 缓存）⇒ 按构造不是本仓声明通道
        if rp in seen:
            continue                                # 环
        seen.add(rp)
        try:
            t = strip_xml_comments(open(rp, encoding="utf-8-sig", errors="replace").read())
        except OSError:
            continue
        IMPORT_EDGES.append((os.path.relpath(fp, REPO), os.path.relpath(rp, REPO), depth))
        merged.append((t, os.path.relpath(rp, REPO)))
        merged.extend(import_closure(rp, t, localprops, seen, depth + 1))
    return merged



def project_refs(p, text):
    """<ProjectReference> 的目标工程（**路径也要做属性替换** —— 实测 DWF 写的是 $(WpfLinuxRoot)…）"""
    out = []
    for r in re.findall(r'<ProjectReference\s+Include="([^"]+)"', text):
        q = subst(r, os.path.dirname(p))
        if "$(" in q:
            UNRESOLVED.append((os.path.relpath(p, REPO), r))
            continue
        q = q if os.path.isabs(q) else os.path.join(os.path.dirname(p), q)
        q = os.path.realpath(q)
        if os.path.exists(q):
            out.append(q)
    return out


def refs_of(text):
    """⇒ list[(name, private:bool, hintdir|None)]（逐 <Reference> 块解析）"""
    out = []
    for blk in re.findall(r"<Reference\b[^>]*>.*?</Reference>|<Reference\b[^>]*/>", text, re.S):
        m = re.search(r'Include="([^"]+)"', blk)
        if not m:
            continue
        name = m.group(1).split(",")[0].strip()
        priv = not re.search(r"<Private>\s*false\s*</Private>", blk, re.I)
        out.append((name, priv, None))
    return out


def outdirs(projdir, text, cfg):
    # 【`W70B` / `TASK-1002`】原先只认**无属性**形态 `<TargetFramework>net10.0</TargetFramework>`，
    #   而 `src/Linux/build/third-party/WpfLinux.props:48` 写的是**带条件属性**的
    #   `<TargetFramework Condition="'$(TargetFramework)' == ''">net10.0</TargetFramework>`
    #   ⇒ 即使把 import 跟进来，`net10.0` 也抓不到，期望目录会算成 `…/bin/Debug` 而真副本在
    #   `…/bin/Debug/net10.0` ⇒ **仍然 `UNEXPECTED`**（这是本缺陷的**第二条**缺口，容易漏）。
    #   `\b` 恰好排除掉 `<TargetFrameworks>`（复数）与 `<TargetFrameworkVersion>`/`<…Profile>`
    #   （'s'/'V'/'P' 与 'k' 都是词字符 ⇒ `\b` 不成立）。
    #   **实证**：放宽前后对仓内 86 个 csproj 的匹配集**逐字相同**（`grep -rl '<TargetFramework '
    #   --include='*.csproj'` = 0 个 ⇒ 仓内没有任何工程用属性形态）⇒ 本行对既有读数**零位移**。
    m = re.search(r"<TargetFramework\b[^>]*>([^<]+)</TargetFramework>", text)
    tfm = m.group(1).strip() if m else ""
    append = not re.search(r"<AppendTargetFrameworkToOutputPath>\s*false\s*</AppendTargetFrameworkToOutputPath>", text, re.I)
    cand = [os.path.join(projdir, "bin", cfg) + ("/" + tfm if (append and tfm) else "")]
    return [os.path.normpath(c) for c in cand if os.path.isdir(c)]



def invisible_copy_sites():
    """脚本里**会创建/删除副本**的拷贝点（= 本枚举器看不见的那些路径）。只报事实，不改任何脚本。

    判据（每条都过筛，避免把注释/打印当拷贝点）：
      · 只看 `src/Linux/build/**/*.sh`（排除 upstream/artifacts 与**本仪器自身** `wic-shim/**`）
      · 排除注释行（`#` 开头）与纯打印行（echo/printf）
      · 提到某个 ITEM 文件名
      · 含 `cp|install|mv|rm` 或输出重定向 ⇒ **写点**（会改变副本集合）
      · 其余（find / 赋值 / nm / sha / 条件判断）⇒ **只读**（只读或枚举，不产生副本）
    ⇒ 返回值 [(kind, "file:line", 该行原文)]，kind ∈ {write, read}

    ⚠️ **这个函数的覆盖边界本身也要"主动承认"**（TAPPS 2026-09-15）——它**不是**全仓拷贝点清单：
      ① 每文件最多印 3 条只读行（见 main 的 capping）⇒ 截掉的条数由 `#INVISIBLE-CAPPED` 报出；
      ② 只扫 `src/Linux/build/**/*.sh` ⇒ 别处的 `.sh`（`src/Linux/tests/**` 实测有 `wpfgfx_cor3.so` 写点）与
         **MSBuild `<Copy>`**（`*.csproj/*.targets/*.props` 实测 2 处）**完全不在这份清单里**
         ⇒ 由 `invisible_copy_sites_ext()` 补扫。
    """
    names = [os.path.basename(n) for n, _ in ITEMS]
    writes, reads = [], []
    for dp, dn, fn in os.walk(os.path.join(REPO, "src", "Linux", "build")):
        if "/upstream/" in dp + "/" or "/.artifacts" in dp or dp.endswith("/wic-shim"):
            continue
        for f in sorted(fn):
            if not f.endswith(".sh"):
                continue
            fp = os.path.join(dp, f)
            try:
                lines = open(fp, encoding="utf-8", errors="replace").read().splitlines()
            except OSError:
                continue
            for i, ln in enumerate(lines, 1):
                t = ln.strip()
                if not t or t.startswith("#"):
                    continue
                if re.match(r"(echo|printf|say)\b", t):
                    continue
                if not any(n in ln for n in names):
                    continue
                where = "%s:%d" % (os.path.relpath(fp, REPO), i)
                if re.search(r"(^|[;&|(]\s*)(cp|install|mv|rm)\s|>>?\s*\S*%s" % re.escape(names[0]), t) or \
                   re.search(r"(^|[;&|(]\s*)(cp|install|mv|rm)\s", t):
                    writes.append(("write", where, t[:160]))
                else:
                    reads.append(("read", where, t[:160]))
    return writes + reads


def invisible_copy_sites_indirect():
    """补扫 ②：**源路径藏在变量里**的拷贝点（TAPPS 2026-09-15 新增）—— 只报写点。

    为什么必须有它（主扫的**第三条**自带边界：它只认"字面提到 ITEM 文件名的行"）：
      `cp -f "$NATIVE_AUTH" "$c"` 这种行**一个字面 ITEM 名字都没有** ⇒ 主扫既不算写点也不算只读，
      它**完全不存在于任何清单里**。实测（都是本仓里真在跑的写点）：
        · `src/Linux/build/close-wave.sh:130` —— `NATIVE_AUTH="…/libwpfwin32.so"`（:99 定义）⇒ 拷贝到各副本
        · `src/Linux/build/MilBridge/run.sh:210` —— `refresh_applocal` 的 `cp -f "$f" "$d/$base"`
          （**REPORT §24.8 提到的"现有检测器漏报"就是它**）
        · `src/Linux/tests/…/run-wpfprobe.sh:210/:213/:214`、`run-wpftextdemo.sh:563`、`run-hellowpf.sh:197`
          —— 把 `libwpfwin32.so` **以 4–5 个 Windows DLL 别名**（uxtheme/wtsapi32/shell32/
          PresentationNative_cor3）拷进 app 目录（部署期止损；**陈旧别名同样没人判**）
    判据（"轻量数据流"：不做完全程分析，**只在本文件内**把"赋值/数组元素里含 ITEM 权威路径特征
      （字面 ITEM 名 **或** 以 `/bin/` 或 `/.artifacts/` 结尾的路径）"的变量名记下来，再看后续
      `cp|install|mv|rm <含该变量的参数>` 行；变量在拷贝行**之前**出现才算）：
      · 只在 `.sh` 里找；排除注释/纯打印；文件级排除同主扫
      · **与主扫及补扫①去重**由调用方完成（这里把主扫能抓到的行也返回，调用方过滤）
    ⇒ 返回值 [(kind, "file:line", 该行原文)]（含"经变量 $X"的说明）
    """
    names = [os.path.basename(n) for n, _ in ITEMS]
    writes = []
    for dp, dn, fn in os.walk(REPO):
        if "/upstream/" in dp + "/" or "/.artifacts" in dp or "/obj/" in dp + "/" \
           or dp.endswith("/wic-shim"):
            continue
        for f in sorted(fn):
            if not f.endswith(".sh"):
                continue
            fp = os.path.join(dp, f)
            try:
                lines = open(fp, encoding="utf-8", errors="replace").read().splitlines()
            except OSError:
                continue
            taint = {}          # 变量名 → 证据（一行原文的片段）
            arrtaint = {}       # 数组名 → 证据（供 `for x in "${arr[@]}"` 传播）
            in_arr = None       # 多行数组字面量：`pairs=(` 之后到 `)` 之前（实测 run.sh:192-197 就是这种）
            for i, ln in enumerate(lines, 1):
                t = ln.strip()
                if not t or t.startswith("#"):
                    continue
                # ---- 多行数组字面量：`NAME=(` … 元素 … `)` ----
                if in_arr is None:
                    m0 = re.search(r"(?:^|[;&|(]\s*)(?:local\s+|declare\s+(?:-a\s+)?)?([A-Za-z_][A-Za-z0-9_]*)=\(\s*$", t)
                    if m0:
                        in_arr = m0.group(1)
                        if any(n in ln for n in names):
                            arrtaint[in_arr] = ln.strip()[:70]
                        continue
                else:
                    if any(n in ln for n in names):
                        arrtaint.setdefault(in_arr, ln.strip()[:70])
                    if re.match(r"^\s*\)\s*;?\s*$", ln):
                        in_arr = None
                    continue
                # 变量绑定：字面 ITEM 名，或**权威产物路径特征**（`/bin/<cfg>/`、`…/bin/<file>`、
                #   `/.artifacts/`），或引用了**已认定权威**的另一个变量（逐行前向传播）。
                #   ⚠️ 刻意**不**认"随便一个赋值含 ITEM 名"以外的长路径 —— 否则 `SRC_BAK="$BAK_DIR/…"`
                #   这类**备份路径**会被误当权威源（第一版就是这么误报的，见报告 §3 的自纠）。
                m = re.match(r'(?:local\s+|declare\s+(?:-a\s+)?)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$', t)
                if m:
                    var, val = m.group(1), m.group(2)
                    # ⚠️ 一行可能有**多个赋值**（`A="…"; B="…"`）⇒ 只看**第一处**的值当证据，
                    #   否则同一行后半段的别的变量会把前半段"染"上（第一版就是这么误报 `SRC_BAK` 的）。
                    code = val.split(";")[0].split("#")[0]
                    looks_auth = (any(n in code for n in names)
                                  # "产物路径"特征：以 `/bin/<cfg>` 结尾（目录）
                                  or re.search(r"/bin/(?:debug|release)[^/\s\"']*$", code) is not None
                                  or "/.artifacts/" in code
                                  or any(re.search(r"\$\{?%s\b" % re.escape(tv), code) for tv in taint))
                    if looks_auth:
                        taint[var] = code.strip()[:70]
                    continue
                # `for x in "${arr[@]}"` ⇒ 把数组的证据传给循环变量（`for f in "${pairs[@]}"`）
                man = re.search(r"\bfor\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\s+\"?\$\{?([A-Za-z_][A-Za-z0-9_]*)", t)
                if man and man.group(2) in arrtaint:
                    taint[man.group(1)] = "数组 $%s：%s" % (man.group(2), arrtaint[man.group(2)])
                # `for x in <含 ITEM 名的字面量>` ⇒ 直接染
                m2 = re.search(r"\bfor\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\s+(.*)$", t)
                if m2 and any(n in m2.group(2) for n in names):
                    taint[m2.group(1)] = m2.group(2).strip()[:70]
                if re.match(r"(echo|printf|say)\b", t):
                    continue
                if not re.search(r"(^|[;&|(]\s*)(cp|install|mv|rm)\s", t):
                    continue
                used = [v for v in taint if re.search(r"\$\{?%s\b" % re.escape(v), t)]
                if used:
                    where = "%s:%d" % (os.path.relpath(fp, REPO), i)
                    ev = "；".join("$%s←%s" % (v, taint[v]) for v in sorted(used))
                    writes.append(("write", where, (t[:110] + "　（经变量 %s）" % ev)))
    return writes


def invisible_copy_sites_ext():
    """补扫 ①：`invisible_copy_sites()` **明确看不见的写点**（TAPPS 2026-09-15 新增）—— 只报写点。

    为什么必须有它：主扫只扫 `src/Linux/build/**/*.sh`，于是这些**会创建/覆盖副本**的行两边都漏：
      · `src/Linux/tests/**/run-wpftextdemo.sh:179/:191` —— `cp` 存/还原 `.artifacts/publish/…/wpfgfx_cor3.so`
        （**发布目录 = 该 `.so` 的唯一真身**，而 `.artifacts` 不在 SCAN_ROOTS 里）
      · **MSBuild `<Copy>`**：`src/Linux/build/{DirectWrite.Linux,MilBridge/tests}/Directory.Build.targets`
        的 `SyncProviderAuthority`（两处；它们正是"陈旧副本"的机制级修法，却不在任何清单里）
    判据（**故意收窄，只报"写"**；把 `HintPath`/注释/`<Message>` 这类**声明式或纯打印**行排除，
    否则清单会被 80+ 条非拷贝行淹掉 ⇒ 又是"清单看起来全、其实没人读"的那类假保证）：
      · 文件后缀 ∈ {.sh,.csproj,.targets,.props,.ps1}；排除 `upstream/`/`.artifacts`/`obj/`/`.git`/本仪器目录
      · 排除注释（`#`/`//`/`<!--`）与纯打印（`echo|printf|say|Message`）
      · 判为写点 = ① `sh` 形态：行首/分隔符后是 `cp|install|mv|rm` **且**该行提到 ITEM 文件名；
                   ② **MSBuild `<Copy>`**：行里有 `<Copy\b` 或 `DestinationFolder=`/`DestinationFiles=`，
                      且**本文件内**存在把该行引用的属性绑到某个 ITEM 文件名的行
                      （实测两处都是 `<Copy SourceFiles="$(ProviderAuthorityPath)" …>` ⇒ 靠
                       `$(ProviderAuthorityPath)` 的**定义行**建立关联）。
    ⇒ 返回值 [(kind, "file:line", 该行原文)]，kind 恒为 {"write"}（**不与主扫重叠**）
    """
    names = [os.path.basename(n) for n, _ in ITEMS]
    exts = (".sh", ".csproj", ".targets", ".props", ".ps1")
    writes = []
    for dp, dn, fn in os.walk(REPO):
        if "/upstream/" in dp + "/" or "/.artifacts" in dp or "/obj/" in dp + "/" \
           or "/.git" in dp + "/" or dp.endswith("/wic-shim"):
            continue
        for f in sorted(fn):
            if not f.endswith(exts):
                continue
            fp = os.path.join(dp, f)
            try:
                lines = open(fp, encoding="utf-8", errors="replace").read().splitlines()
            except OSError:
                continue
            # 本文件内"哪些 MSBuild 属性绑定到了某个 ITEM 文件名"（供 <Copy> 关联用）
            prop2name = {}
            for ln in lines:
                for m in re.finditer(r"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", ln):
                    for nm in names:
                        if nm in ln:
                            prop2name.setdefault(m.group(1), nm)
            for i, ln in enumerate(lines, 1):
                t = ln.strip()
                if not t or t.startswith("#") or t.startswith("//") or t.startswith("<!--"):
                    continue
                if re.match(r"(echo|printf|say|Message)\b", t):
                    continue
                where = "%s:%d" % (os.path.relpath(fp, REPO), i)
                hits_name = any(n in ln for n in names)
                if re.search(r"(^|[;&|(]\s*)(cp|install|mv|rm)\s", t):
                    if hits_name:
                        writes.append(("write", where, t[:160]))
                    continue
                if re.search(r"<Copy\b|DestinationFolder=|DestinationFiles=", t):
                    props = set(re.findall(r"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", t))
                    via = sorted(props & set(prop2name))
                    if hits_name or via:
                        tail = ("　（写点经属性 %s ⇒ %s）" % (",".join("$(%s)" % p for p in via),
                                                              prop2name[via[0]])) if via else ""
                        writes.append(("write", where, (t[:140] + tail)))
    return writes


def main():
    projs = {}
    for p in project_files():
        try:
            text = open(p, encoding="utf-8-sig", errors="replace").read()
        except OSError:
            continue
        m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
        if m:
            asm = m.group(1).strip()
        else:
            base = os.path.basename(p)[:-len(".csproj")]
            asm = base[:-len(".Linux")] if base.endswith(".Linux") else base
        # 【`W70B` / `TASK-1002`】把**显式 `<Import>` 图**里的声明并入工程文本（实现与覆盖边界见
        #   `import_closure()` 上方那一整段注释）。**顺序就是语义**：工程自己的文本在前
        #   ⇒ `outdirs()` 的 `re.search`（取"第一个"）与 `literal_props()` 的 `setdefault`
        #   都以**工程自报**优先，被导入件只在工程没声明时补位（与 MSBuild 的"后导入者覆盖前者"
        #   在**属性**上相反，但本模型只需要"取一个值"，且工程自报=最具体，故取前）。
        imp = import_closure(p, text, literal_props(text), {os.path.realpath(p)})
        eff = "\n".join([text] + [t for t, _owner in imp])
        # 【W70B】`ownnames` / `refsrc` = **声明归属**（只用于让来源链说真话）：
        #   `ownnames` = 工程**自己**文本里声明过的 `<Reference Include>` 名；
        #   `refsrc`   = 某条 `<Reference Include>` **出自哪一份被导入件**（仓内相对路径）。
        #   为什么要它：`ThirdPartyMini.csproj` **一条 `<Reference>` 都没有**，那 5 条全来自
        #   `src/Linux/build/third-party/WpfLinux.props:76-117` ⇒ 不标注会让读者以为引用写在 csproj 里。
        #   本模型**不做逐声明归属**（归属的完整、逐条可核的输入是 `#IMPORT` 边清单）；
        #   这里只对 `<Reference>` 做一次"哪份被导入件里有这个名字"的直接查表。
        refsrc = {}
        for _t, _owner in imp:
            for _nm in re.findall(r'<Reference\s+Include="([^"]+)"', _t):
                refsrc.setdefault(_nm.split(",")[0].strip(), _owner)
        projs[p] = dict(dir=os.path.dirname(p), text=eff, asm=asm,
                        refs=[r for r in refs_of(eff) if r[1]],          # Private=false ⇒ 不期望副本
                        ownnames={r[0] for r in refs_of(text) if r[1]},
                        refsrc=refsrc,
                        pref=project_refs(p, eff),
                        dirs={c: outdirs(os.path.dirname(p), eff, c) for c in ("Debug", "Release")})
    byasm = collections.defaultdict(list)
    for p, d in projs.items():
        byasm[d["asm"]].append(p)
    dir2proj = {}
    for p, d in projs.items():
        for c, ds in d["dirs"].items():
            for o in ds:
                dir2proj.setdefault(o, p)

    # 解析源目录（HintPath 指向的目录）——与 check-applocal-sync.sh 的 REFDIR 同源
    refdirs = set()
    for p, d in projs.items():
        for hp in re.findall(r"<HintPath>([^<]+)</HintPath>", d["text"]):
            r = subst(hp, d["dir"])
            if "$(" in r:
                UNRESOLVED.append((os.path.relpath(p, REPO), hp))
                continue
            if not os.path.isabs(r):
                r = os.path.join(d["dir"], r)
            refdirs.add(os.path.dirname(os.path.realpath(r)))

    # 权威目录也算"解析/生产源"：任何工程 HintPath 指向某件的权威目录 ⇒ 该件在期望集里
    auth_dir_item = {}
    for name, auth in ITEMS:
        if auth:
            auth_dir_item.setdefault(os.path.dirname(os.path.realpath(auth)), set()).add(name)

    stem2item = {n[:-len(".dll")] if n.endswith(".dll") else n: n for n, _ in ITEMS}
    memo = {}

    def E(p, stack=frozenset()):
        """⇒ {item: 来源链}（只从**声明式来源**算：Reference/HintPath/ProjectReference/自身产出）"""
        if p in memo:
            return memo[p]
        if p in stack:
            return {}
        d = projs[p]
        rel = os.path.relpath(p, REPO)
        out = {}
        own = stem2item.get(d["asm"])
        if own:
            out[own] = "%s **产出的程序集本身**" % rel
        for name, priv, _ in d["refs"]:                       # <Reference Include> 且 Private≠false
            it = stem2item.get(name)
            if it:
                # 【W70B】来源链如实标注"这条是被**哪一份被导入件**带进来的"（工程自己一行都没写）——
                #   `#IMPORT|工程|被导入件|深度` 是这条标注的**完整输入**（可逐条核）。
                _src = d["refsrc"].get(name)
                _via = "" if name in d["ownnames"] else (
                    "（**经 `<Import>` 带入**：%s）" % _src if _src else "（**经 `<Import>` 图带入**，非本工程自写）")
                out[it] = '%s 的 <Reference Include="%s">%s' % (rel, name, _via)
            for q in byasm.get(name, []):                     # 引用的是**工程产物** ⇒ 连带它的闭包
                if "/CycleStub." in q:                        # 循环桩只用于断环 ⇒ 不把它的依赖当传递依赖
                    continue
                for it2, why2 in E(q, stack | {p}).items():
                    out.setdefault(it2, "%s →(引用 %s)→ %s" % (rel, name, why2))
        for q in d["pref"]:
            qrel = os.path.relpath(q, REPO)
            for it2, why2 in E(q, stack | {p}).items():
                out.setdefault(it2, "%s →(ProjectReference)→ %s" % (rel, why2))
            qa = projs.get(q, {}).get("asm")
            qi = stem2item.get(qa)
            if qi:
                out.setdefault(qi, "%s →(ProjectReference)→ %s **产出的程序集**" % (rel, qrel))
        for hp in re.findall(r"<HintPath>([^<]+)</HintPath>", d["text"]):
            r = subst(hp, d["dir"])
            if "$(" in r:
                continue
            r = r if os.path.isabs(r) else os.path.join(d["dir"], r)
            rd = os.path.dirname(os.path.realpath(r))
            for it2 in auth_dir_item.get(rd, ()):             # 指向某件的**权威目录**
                out.setdefault(it2, "%s 的 <HintPath> → %s（权威目录）" % (rel, os.path.relpath(rd, REPO)))
            q = dir2proj.get(rd)
            if q and q != p and "/CycleStub." not in q:       # HintPath 指向别的工程输出 ⇒ 取它的闭包
                for it2, why2 in E(q, stack | {p}).items():
                    out.setdefault(it2, "%s 的 <HintPath> → %s" % (rel, why2))
        memo[p] = out
        return out

    # 期望：每个工程（非循环桩）的每个**已存在**输出目录 × 它的引用闭包
    exp = {}
    for p, d in projs.items():
        if "/CycleStub." in p:                                   # 循环桩：按构造不是普通 app-local
            continue
        e = E(p)
        if not e:
            continue
        for cfg, ds in d["dirs"].items():
            for o in ds:
                for it in sorted(e):
                    exp[(o, it)] = e[it]
    # 权威件本身也必须存在（权威枚举的一部分）
    for name, auth in ITEMS:
        if auth:
            exp[(os.path.dirname(os.path.realpath(auth)), name)] = "权威件本身的目录（权威枚举）"

    dbg = os.environ.get("APEXPECT_DEBUG", "")
    if dbg:
        for (o, it), why in sorted(exp.items()):
            if dbg in o:
                sys.stderr.write("DEBUG %s/%s  ← %s\n" % (o, it, why))
    for rd in sorted(refdirs):
        print("#REFDIR|%s" % rd)
    for (o, it), why in sorted(exp.items()):
        print("#EXPECT|%s|%s|%s" % (o, it, why))
    # 【W70B / `TASK-1002`】**新通道的可核输入**：① 跟过哪些 `<Import>` 边；② 哪些 import **没解析出来**
    #   （分 `sdk` / `repo` 两桶）。为什么必须印：期望集合现在有一部分来自**被导入件** ⇒ 不印这两样，
    #   这个新通道自己就变成"看不见的输入"（本仓反复登记的那一族）。
    #   ⚠️ 消费者**不许**把 `repo` 桶当绿：它意味着"有一处接线模型没跟进去"，见 `check-applocal-sync.sh`
    #   的「期望模型跟随的显式 import 边」段（那里把它印出来、给出计数与逐条清单）。
    for c, i, dp in sorted(set(IMPORT_EDGES)):
        print("#IMPORT|%s|%s|%d" % (c, i, dp))
    for c, e, b in sorted(set(IMPORT_UNRESOLVED)):
        print("#UNRESOLVED-IMPORT|%s|%s|%s" % (c, e, b))
    covered = {it for (_o, it) in exp}
    for name, auth in ITEMS:
        if name in covered:
            continue
        if not auth:
            print("#UNKNOWN|%s|无单一权威（跨波重建/发布脚本产物）⇒ 期望算不出；需要 owner 声明期望表" % name)
        else:
            print("#UNKNOWN|%s|所有引用点都无声明式来源（原生件由 shell cp 拷贝：见 src/Linux/build/MilBridge/run.sh:95/:228）⇒ 需要 owner 声明期望表" % name)
    all_sites = invisible_copy_sites()
    nw_all = len([1 for k, _w, _l in all_sites if k == "write"])
    nr_all = len(all_sites) - nw_all
    sites = all_sites
    nw = nw_all
    seen = {}
    capped = []
    for k, where, ln in sites:                      # 只读类：每文件最多留 3 条（计数仍按全量）
        f = where.split(":")[0]
        if k == "read":
            seen[f] = seen.get(f, 0) + 1
            if seen[f] > 3:
                continue
        capped.append((k, where, ln))
    sites = capped
    # 【TAPPS 2026-09-15】截断**不许静默**：主扫的 `invisible_read` 是**全量**计数，而打印出来的只读行
    #   比它少（实测 17 vs 15）⇒ 旧输出里这个差额**没有任何字样**。现在逐文件印 `#INVISIBLE-CAPPED`。
    n_capped = 0
    kept = {}
    for k, where, _ln in sites:
        if k == "read":
            f = where.split(":")[0]
            kept[f] = kept.get(f, 0) + 1
    for f in sorted(seen):
        drop = seen[f] - kept.get(f, 0)
        if drop > 0:
            n_capped += drop
            print("#INVISIBLE-CAPPED|read|%s|%d|%d" % (f, drop, seen[f]))
    for k, where, ln in sites:
        print("#INVISIBLE|%s|%s|%s" % (k, where, ln))
    # 【TAPPS 2026-09-15】补扫：主扫明确的盲区（`src/Linux/build/**` 之外的 .sh + MSBuild `<Copy>`）—— **只报写点**
    #   去重：主扫已报过的 `file:line` 不再出现在补扫里（否则"20 + 4"会被读成 24 个不同的拷贝点）
    main_where = {w for _k, w, _l in all_sites}
    ext_w = [s for s in invisible_copy_sites_ext() if s[1] not in main_where]
    for k, where, ln in ext_w:
        print("#INVISIBLE-EXT|%s|%s|%s" % (k, where, ln))
    # 【TAPPS 2026-09-15】补扫 ②：**源路径藏在变量里**的写点（主扫一条都抓不到：它只认字面文件名）
    ind_w = [s for s in invisible_copy_sites_indirect()
             if s[1] not in main_where and s[1] not in {w for _k, w, _l in ext_w}]
    for k, where, ln in ind_w:
        print("#INVISIBLE-INDIRECT|%s|%s|%s" % (k, where, ln))
    # 【W70B / `TASK-1002`】末尾追加两格（**只追加、不改前 12 格** ⇒ 旧消费者按位置读前 12 个字段不受影响）：
    #   import_edges      = 本趟跟过的显式 `<Import>` 边数（去重后）
    #   import_unresolved = 未解析的 import 条数，两个桶 `repo`（**真缺口**）/`sdk`（SDK 侧，按构造不在本图）
    print("#SUMMARY|refdirs=%d|expect=%d|projects=%d|unknown=%d|unresolved_hintpath=%d|invisible_copysites=%d|invisible_write=%d|invisible_read=%d|invisible_capped=%d|invisible_ext_write=%d|invisible_ext_read=%d|invisible_indirect_write=%d|import_edges=%d|import_unresolved_repo=%d|import_unresolved_sdk=%d"
          % (len(refdirs), len(exp), len(projs), len([n for n, _ in ITEMS if n not in covered]), len(UNRESOLVED),
             nw_all + nr_all, nw_all, nr_all, n_capped, len(ext_w), 0, len(ind_w),
             len(set(IMPORT_EDGES)),
             len({(c, e) for c, e, b in IMPORT_UNRESOLVED if b == "repo"}),
             len({(c, e) for c, e, b in IMPORT_UNRESOLVED if b == "sdk"})))
    for rel, hp in UNRESOLVED[:10]:
        print("#UNRESOLVED|%s|%s" % (rel, hp))


if __name__ == "__main__":
    main()
