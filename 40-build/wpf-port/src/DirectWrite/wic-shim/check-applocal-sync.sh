#!/usr/bin/env bash
# app-local 产物一致性检查（债务 #20 的机制级修法）—— **只报不改**
#
# 【它防的是什么】app-local 目录里的副本"落后一代"，而 DllImport/app-local 加载会**优先命中它**：
#   · 3 份 libwpfwic.so ⇒ WIC_SHIM_DOUBLE_TABLE=TRUE ⇒ E_HANDLE
#   · 发布目录里 09-10 的 29,392 B 旧 shim；SystemFontsProbe 里 108,584 B 老 libwpfwin32.so
#   · CoverageProbe/bin/Release 里的**旧 provider dll** ⇒ 跑探针会得出"补丁无效"的**假红**
# 【边界】只打印 + 校验；**绝不改写任何目录**（同步是各车道构建脚本/发布脚本的事）。
#
# 【判定口径（主控 2026-09-11 裁定；2026-09-14 补 ①~⑤，来历见下）】十类分开计数，
#   **"判不了 / 不是同类"不许冒充"不一致"**，而**同类加载源的不同 sha 永远是红的**：
#   OK            与权威件同 sha（同类）
#   STALE         同一权威下 sha 不同，且**副本 mtime 早于权威件** ⇒ 真发散：
#                 `HintPath + Private=true` 的语义就是"消费者构建时拷一次"，权威件之后变了，
#                 副本就停在旧代 ⇒ 必须刷新（**红**）
#   NEWER-DIFF    同一权威下 sha 不同，但副本**不早于**权威 ⇒ 也**红**，只是措辞不同：
#                 此时"谁是旧代"没定论（可能是权威落后、或竟是桩件/跨配置漏网）⇒ 不许当"落后"读
#   MISMATCH      = STALE + NEWER-DIFF（保留旧计数器名，供发布脚本/门禁沿用）
#   CROSS-CONFIG   **副本所在路径的配置 ≠ 声明配置（`$SELFBUILT_CONFIG`，唯一声明见 `src/Linux/build/SelfBuiltConfig.props`）**
#                 ⇒ **具名诊断格**（`D-G62`，W52C2 2026-09-20；**取代**旧格 `NO-AUTHORITY`）：
#                 **只增加可见性** —— ① **不判红**（不进最终判定式、不改 rc）；
#                 ② **不给任何副本免红**：本格**只打印，然后继续走 ⑤/⑥ 的正常判据** ⇒
#                 跨配置副本**照样**计入 `STALE`／`NEWER-DIFF`／`UNEXPECTED`（"跨配置"**不是**免红券）。
#                 【为什么旧格必须走】旧格的条件是 `is_release_path "$f" && is_debug_auth "$exp"` ⇒
#                 **只在"权威件是 Debug"时可达**；`#39` 把声明配置切成 `Release` 之后它**恒假**（**死格**）：
#                 W52C 实测现场 `NO-AUTHORITY=0`，而新纳入 PF/WB 后的 38 条红里 **31 条**恰恰是
#                 "Debug 目录里的副本" ⇒ 旧格既看不见它们、又曾把这一类**豁免**成"判不了"。
#                 【射程（本格的边界，如实报）】位置与旧格**同槽**（`SKIP(obj)`/`SKIP(stub)` 之后）⇒
#                 `obj/**` 与 `CycleStub.*` 里的跨配置副本**不进本格**（它们按构造不是加载源）；
#                 `RID` 目录（`release_*`）归 `Release`；路径里认不出配置的副本（如 `bin/` 直下、`net10.0/`
#                 之外的自定义目录）**不进本格**（认不出 ≠ 同配置 ⇒ 需更大范围的判据，今天没有）。
#   LIB-COPY      ② 副本所在输出目录**没有 `*.runtimeconfig.json`** ⇒ 该目录不是启动宿主
#                 ⇒ 里面的私有依赖副本**不是加载源**（没人从库输出目录启动）⇒ 不判定
#                 （实测：`src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework.Classic/bin/Debug` 无 runtimeconfig；
#                   而 5 个探针 / samples / tests 宿主目录都有 1 份）
#   SKIP(obj)     ③ `obj/**`（编译器工作目录：中间件 + ref/refint 引用程序集）
#                 ⇒ 与 bin 不必相同、也不是加载源 ⇒ 不判定（**逐条打印 sha**，不静默）
#   SKIP(stub)    ④ `CycleStub.*` 工程里的同名件 ⇒ **按构造就是另一个程序集**
#                 （实测 `ReachFramework.dll` 桩 5,120 B `067c03858367d880` vs 权威 741,376 B，
#                   T1c §32 登记过、主控自己也踩过一次）⇒"同名即同类"必然误报；判定**先于** obj
#   RETIRED       已退役别名（libole32.dll.so）存在即错
#   AUTH-MISSING  **某个权威件整份不见**（`D-G8`，W24D 2026-09-17 补）：本件**每一份副本都没有可比的真值**
#                 ⇒ **红**。补它之前这一类只印一行 `⚠ 权威件缺失`，而 `scan()` 内部置的 rc 在 `:654`
#                 被赋值后**从未被读** ⇒ 五个计数器全 0 时仍印 `APPSYNC=PASS` + `exit 0`（**骗人**）。
#   总判定：MISMATCH>0 或 DIVERGENT>0 或 RETIRED>0 或 UNEXPECTED>0 或 **AUTH-MISSING>0** 或
#           **BRIDGE-ANCHOR>0 / BRIDGE-NOINFO>0**（⑦） ⇒ APPSYNC=MISMATCH + exit 1；
#           `scan()` 内部 rc≠0（而上述计数器全 0）⇒ **也** APPSYNC=MISMATCH + exit 1（结构性兜底，防"将来再有只置 rc 的失败路径"）；
#           仅 CROSS-CONFIG / LIB-COPY / SKIP(*) > 0 ⇒ APPSYNC=PASS（附提示）+ exit 0。
#   ⚠️ **mtime 只用来"说清是哪种不同"，不用来放行**：同类加载源只要 sha 不同就是红的
#      （唯一非红的是那些**按构造就不是同一个东西**的类）。这条是防"假绿"的底线。
#
# 【⑦ 桥 `wpfgfx_cor3.so` 的**绝对锚**判据（`D-A2-r`，W25B 2026-09-17）——「两份副本一起换旧仍静默通过」的修法】
#   桥**在 `ITEMS` 里**（`:116` 附近）但 `exp` 是**空串** ⇒ 在 `:225` 被 `continue` 跳过 ⇒
#   它的两份副本**连一个读数都不产生** ⇒ "把两份一起换成旧件"**静默通过**（这就是 `D-A2-r`）。
#   本判据用**发布记录里的绝对锚** `BRIDGE_SO_SHA256`（唯一写点 `src/Linux/build/publish-milbridge.sh:81`）
#   逐份核桥副本的 sha（实现 `bridge_anchor_check()`，调用点在 `scan` 之后、摘要之前）：
#     BRIDGE-ANCHOR  某份副本 sha ≠ 记录 ⇒ **红**。**只看 sha，不看 mtime/权限**（mtime 只进诊断文字）；
#                    "发布记录过期"与"副本被换成旧件"**都判红**，本判据**不给成因**（今天没有可分它们的量）。
#     BRIDGE-NOINFO  记录缺失 / 记录里解析不出 64 位小写 hex / 本次扫描根内**一份桥副本都没有** ⇒ **红**。
#                    **不许当绿**：与 `AUTH-MISSING`（`D-G8`）同族 —— "没测到东西" ≠ "一致"。
#   【为什么**独立**、不把锚塞进 `ITEMS` 的 `exp` 格】给 `ITEMS` 配非空权威 ⇒ 桥进 ⑥ 判定 ⇒ 打
#     `STALE` / `NEWER-DIFF` **行首标签** ⇒ `sync-applocal-authority.sh:76` 消费它、`:103` 真 `cp -f "$asrc" "$dst"`
#     ⇒ **用 376 B 的 `.txt`（记录）覆盖 4,987,840 B 的 `.so`**；发布目录那一份更是 `cp X X` 自覆盖
#     ⇒ 一次 `--apply` 就毁掉桥和锚。本判据**只读**、且**不生成那两个行首标签**（用 `ANCHOR-*`）⇒ 天然不进写路径。
#   【射程（覆盖不到的，逐条）】① 只覆盖 `SCAN_ROOTS` 里 `find -name wpfgfx_cor3.so` 得到的**现存**副本 ⇒
#     桥若被发布到扫描根之外，只会出 `BRIDGE-NOINFO`（**红**，不是绿）；② **不是防篡改判据**：记录与副本
#     同权限、同在 `.artifacts` 下 ⇒ "副本 + 记录一起改旧"仍静默通过；③ 只比 `.so`，**`.so.dbg` 无判据**
#     （记录里没有 `BRIDGE_DBG_SHA256`）；④ 源级陈旧（`BRIDGE_SRC_FP` vs 现源）**不归它管**
#     （由 `run-wpftextdemo.sh` 的 `BRIDGE_SRC_STALE` 管，两层不要重复计数）。
#     【沙箱豁免（自杀式收窄的自称）】当**调用方显式收窄 `SCAN_ROOTS`**（`--selftest` 的私有沙箱就是）
#     且"记录所在的发布目录也不在本次扫描根里、且本次一份桥副本都没枚举到"⇒ 本次扫描**没有覆盖本判据的
#     对象** ⇒ 印 `BRIDGE-ANCHOR=SKIPPED` 自称、**不计数**。默认全仓扫描（5 个根）**永远覆盖**发布目录
#     （`$REPO/build` 是根之一，`.artifacts/**` 在里面）⇒ 正常路径永远是判定的。
#
# 【⑥ `D-A1` 加固（TAPPS 车道，2026-09-15）：`UNEXPECTED` **不改名**，但它现在按"内容 vs 权威"
#   显式二分，两个子类都可登记、都仍然红】口径 v2 —— **向后兼容**（`UNEXPECTED=N` 这个摘要格子保留，
#   只是后面加上方括号明细；exit code 与 `APPSYNC=` 字面都不变）：
#     UNEXPECTED[DECL-GAP-EQ=n DECL-GAP-DIFF=m]   ← 摘要格与 APPSYNC=MISMATCH 行里都印
#   DECL-GAP-EQ   副本**不在声明图里**（= 原来的 `UNEXPECTED`），但 **sha == 权威** ⇒
#                 "未声明的传递依赖副本"：**不是陈旧件，但仍是声明图缺口** ⇒ 具名、可见、
#                 **仍然计红**（不许洗绿 —— `D-A1` 的现场就是这个形态）。
#     DECL-GAP-DIFF 同一个"不在声明图里"的形态，但 **sha ≠ 权威** ⇒ **硬失败**：任何
#                 "内容不同"的未声明副本都从这里出来，**不可能**被读成绿。
#                 判据 = 拿 `ITEM→权威` 表按 basename 找到权威件再比 sha（`auth_sha_of`）；
#                 **权威查不到（算不出来）时按 DIFF 计**（"算不出来"绝不降级成 EQ）。
#   ⚠️ 两个子类**都在 `UNEXPECTED` 总数里**（旧消费者只看那一个数 ⇒ 行为逐字不变），
#      逐条行首标签也从 `UNEXPECTED` 变成 `UNEXPECTED-EQ` / `UNEXPECTED-DIFF`（**更具体，不隐藏**）。
#      旧输出（只有 `UNEXPECTED` 一个词）对应的现场：`D-A1` = `DECL-GAP-EQ=1`。
#
# 【⑤ 已关闭的口径缺口（2026-09-14 翻正；留档免得后人以为是漏改）】跨副本分组原先用
#   `find -path "*/$cfg/*"`（**大小写敏感**）⇒ 小写 `release/`、`release_linux-x64/`（含 `.artifacts/**`
#   与 MilBridge release 输出）**从不进分组**，而逐副本判定是大小写不敏感的。
#   当时不改的理由是"一改就会引入一批**没人清**的红"；**波里接进 3.6 刷新步骤之后这条前提不成立了**
#   ⇒ 主控 2026-09-14 派单翻正，现在按 `${f,,}` 小写化后匹配 `*/release/*`、`*/release_*/*`（及 `*/debug/*`）。
#   **翻正前实测预判**：小写 release 里的 `WpfGfx.Linux.dll` 副本**全是非启动宿主**（host=0）⇒ 已被
#   `LIB-COPY` 排除；`libwpfwic.so` 那两份与权威同 sha ⇒ 翻正本身**不新增红**（逐条见报告 §25.1）。
#
# 【这条口径的来历（可核，不是我编的）】2026-09-14 主控转来 T1c §32 对 `ReachFramework.dll` 的定性：
#   权威 `src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/{obj,bin}/Debug` 同 sha `f64b76d43d8a`（22:24:20）；
#   app-local 四份整齐 `daf9b6f073f6`（22:23:22，**早 62 秒**）⇒ 一个副本的年龄 = **该消费者最后一次构建的时刻**；
#   另有 09-10 `e8819668` 三份、09-11 `00723a05` 一份；`src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.ReachFramework/{bin,obj}` 里
#   同名 DLL 只有 **5,120 B `067c03858367`**（循环桩）⇒ **"同名就比"必然误报**。
#   ⇒ 结论：这是"**副本同步机制缺一条**"，不是"没重编"、也不是"跨配置口径错"。
#   缺的那一条由集成波补：`src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/sync-applocal-authority.sh --apply`
#   （只刷"同类 + 落后"的副本；接线位置与打印格式见该脚本头部）。
#
# 【凭什么可信（不必信任作者，可复算）】check-applocal-sync.sh --selftest
#   A 错 sha 副本（.so 与托管 .dll 各一次，放在启动宿主目录里）⇒ 必须 MISMATCH 且 exit≠0
#   B 正确副本 ⇒ 必须全 OK 且 exit=0
#   C 同一目录里新旧两份 ⇒ 必须 DIVERGENT 且 exit≠0
#   D **跨配置副本必须具名可见、且照样判红**（`D-G62`，W52C2 2026-09-20；**取代**原先的"无权威 ⇒ exit 0"）：
#     (a) 在**声明配置之外**的目录里放一份 **sha≠权威** 的副本 ⇒ 计数行 `CROSS-CONFIG=1` **且该份仍在 `STALE`**（rc≠0）；
#     (b) 同一份改为**声明配置内**的目录 ⇒ `CROSS-CONFIG=0`、**仍 `STALE`**（⇒ 跨配置**不是**免红券）
#   E **权威件换 sha（临时权威）⇒ 报红；还原 ⇒ 回绿**（两极化）；沙箱**八件权威俱全**（`AUTH-MISSING=0`）
#   O（`D-G8`，W24D 2026-09-17）**"权威件整份不见"⇒ 必须 rc≠0**：全绿沙箱 ⇒ 绿；移走一件权威
#             （该件在沙箱里**没有任何副本**）⇒ `AUTH-MISSING=1` + rc≠0，而**判定计数器逐字未动**
#             （⇒ 红**只**来自这条新信号）；`cp -p` 整份还原 ⇒ 回绿。**修复前**这一趟印 `APPSYNC=PASS`。
#   F **桩件（5,120 B 同名）不得被判成不一致**
#   G **obj 中间件不判定，但 stale 的 bin 副本必须仍被判红**（防"放宽后变瞎"）
#   H **库输出目录不判定；同一份副本放进宿主目录必须被判红**
#   I **小写 `release/` 目录也进分组**（`-ipath` 翻正的牙：两条不同 sha ⇒ DIVERGENT + 红）
#   J **非宿主但被 HintPath 引用的目录 ⇒ 必须判定**（解析源）；**没有 HintPath 时仍是 `LIB-COPY`**（两极化）
#   K **期望集合**：基数不依赖现场文件（删前=删后）+ 删一份⇒MISSING 红 + **整份**还原⇒绿
#   L **多余的副本** ⇒ `UNEXPECTED`（红），不是静默忽略；
#     **L v2**（`D-A1` 加固）：同一份多余副本**内容 == 权威** ⇒ 必须报 `UNEXPECTED-EQ`
#             （`DECL-GAP-EQ`）且计数格出现 `UNEXPECTED=1[DECL-GAP-EQ=1 DECL-GAP-DIFF=0]`；
#     **N** 同一位置换成**内容 ≠ 权威**的那一份 ⇒ 必须报 `UNEXPECTED-DIFF`（`DECL-GAP-DIFF=1`）
#             且 exit≠0（**"内容不同"不可能被洗成 EQ**）
#   M **仪器主动承认自己看不见**：输出里有"看不见的拷贝点（写点/只读）"字样与清单
#     **M2**（TAPPS 2026-09-15）：枚举器**自己的两条边界**也要自称 —— `invisible_capped>0` ⇒ 必须印
#             "只读清单**不完整**"；补扫写点 >0 ⇒ 必须印"补扫的写点 N 处"并逐条列出。
#     **M2 三态**（`D-G13`，W26C 2026-09-17 —— 本件修的是**假红**，不是假绿）：
#             原先 M2 在 **M2 时刻重新算** `invisible_capped`/`invisible_ext_write`，再把 **M 时刻的旧输出**
#             拿去跟它比 ⇒ **两次取数之间仓库只要变了**（本仓常态：别的车道在写盘），M 的旧输出必然缺那句
#             告诫 ⇒ `okM2=0` ⇒ `SELFTEST_M2=FAIL`。**结构上必然，不是偶发配置问题**（`D-G13`）。
#             现在：① M 时刻的**输入快照被序列化**（`snapPre`/`snapPost` 把"M 那次运行看到的是哪一棵树"夹住）；
#                   ② 判据只判**该次运行内部的一致性**（自报的数 vs 自己输出里的字样**与数字**）；
#                   ③ 两个时刻的快照**不一致** ⇒ `NOINFO`（"仓库不同"是**无信息**：**不许当绿、也不许冒充红**）。
#             ⇒ `--selftest` 退出码：`0` = 全 PASS｜`1` = 有子例 `FAIL`｜`3` = 无 `FAIL` 但有子例 `NOINFO`
#               （**仪器未能自证**，≠ 通过；与本文件既有的 `0/1/3` 口径同源）。
#     ⚠️ **同族的第二个假红源**（W26C 实测，M 段）：`printf '%s' "$big" | grep -q PAT` 在
#        `set -o pipefail` 下会因 `grep -q` 提前退出、`printf` 吃 `SIGPIPE` ⇒ **整条管道非零**
#        ⇒ "串在、却判成不在"（实测三条合计 **~6.8%/次**）。M 段那三条已改成 here-string
#        （`grep -q PAT <<<"$big"`，实测 0/400）；**其余子例里同形的调用点尚未改**（见 W26C 报告）。
#   P（`D-A2-r`，W25B 2026-09-17）**桥的绝对锚判据**（⑦，计数器 `BRIDGE-ANCHOR`/`BRIDGE-NOINFO`）：
#             沙箱里造"真形态"发布布局 + 合成记录 ⇒
#             ① 记录与两份副本一致 ⇒ `BRIDGE-ANCHOR=0 BRIDGE-NOINFO=0` 且 rc=0；
#             ② **两份一起换成另一个二进制** ⇒ `BRIDGE-ANCHOR=2` + `APPSYNC=MISMATCH` + rc≠0
#                （**这就是 `D-A2-r` 的永久反极性**：改前这两份连一个读数都不产生）；
#             ③ 只整份还原一份 ⇒ `BRIDGE-ANCHOR=1`（只换一份也抓）；④ 两份都还原 ⇒ 回绿（`cp -p` 逐字节）；
#             ⑤ 记录里的 `BRIDGE_SO_SHA256` 行删掉 ⇒ `BRIDGE-NOINFO=1` + rc≠0（**NOINFO 不许当绿**）；
#             ⑥ 还原记录 ⇒ 回绿。并断言"除 `BRIDGE-*` 两格外其余计数器逐字未动"（⇒ 红**只**来自新判据）。
#
# 用法：check-applocal-sync.sh [--selftest]
#       `--selftest` 退出码：`0` = 18 例全 `PASS`｜`1` = 有子例 `FAIL`｜`3` = 无 `FAIL` 但有子例 `NOINFO`
#       （**`0` 只在全 `PASS` 时给出** ⇒ `NOINFO` 不给 0，但也不与 `FAIL` 混为一谈；见 §M2 三态）。
#        ⚠️ 本自检**必须在静树上跑**（没有别的车道在写盘）：它会**有意**制造装桩与还原，
#           树在动时"两次取数看到的不是同一棵树" ⇒ 按 `D-G13` 报 `NOINFO`（那**不是**判据失败）。
# 环境：AUTH_ROOT（权威根，默认仓库根）/ SCAN_ROOTS（冒号分隔）/ BRIDGE_REC（⑦ 的锚记录路径，默认发布目录那份）
set -uo pipefail
REPO="${AUTH_ROOT:-$(cd "$(dirname "$0")/../../../../.." && pwd)}"
# ★ `#39` 阶段 2/3：**权威件的配置**跟随唯一声明（本表原先在 94 处写死 `bin/Debug`，而每行理由里还写着"Debug 权威" ⇒ 切配置必红）。散文/夹具里的 `Debug` 字样**保持不动**。
. "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/../../../../../src/Linux/build/selfbuilt-config.sh"
# 【`D-A2` 覆盖缺口（W23C，2026-09-16 补齐）】`$REPO/tools` 原先**不在任何一个扫描根里** ⇒
#   `src/Linux/tools/GeometryOracle/bin/Debug/net10.0/WpfGfx.Linux.dll`（`16baacfccfcf1df0`，288,768 B；
#   该目录有 `GeometryOracle.runtimeconfig.json` ⇒ **是启动宿主**）**连枚举都没有过** ⇒ 连 sha 都没被打印。
#   实锤（W22D §1.9 补的第 2 条、W23C 复核）：真正的漏扫是 `src/Linux/tools/`，**不是** `.artifacts/`
#   （`$REPO/build` 一直在扫描根里 ⇒ `.artifacts/**` 一直在范围内，别再抄错这一句）。
# 【`D-G91` 修复 · 车道 W113A · 2026-09-22】**扫描根集合的唯一定义处**。
#   原状：默认根集合被**两份实现**各写一遍 —— 本行（含 `$REPO/tools`）与
#   `sync-applocal-authority.sh:59`（**漏 `$REPO/tools`**）⇒ 那个刷新器
#   ① 用默认参数**永远刷不到**只藏在 `$REPO/src/Linux/tools/**` 下的 `STALE`；
#   ② 更危险：它拿**收窄根**打出 **`STALE=0` 的假绿**（成对读数：默认＋`--apply` ⇒ `refreshed=0/STALE=0`，
#      同刻全文口径 ⇒ `STALE=1`）。
#   修法（**不含白名单、不动十类口径、不动任何计数器**）：默认根集合提成**具名常量** `SCAN_ROOTS_DEFAULT`，
#   并新增**只读**模式 `--print-scan-roots` 供刷新器**派生** ⇒ "同一条语义存在两处必然分叉"在结构上消失。
#   自检（刷新器那侧）：它算出的根集合**必须**与这里逐集合相同，否则**大声 `APPSYNC_ROOTS=MISMATCH`＋rc≠0**，
#   且**在打印任何 `STALE=` 汇总之前退出**（收窄根再也打不出 `STALE=0`）。
SCAN_ROOTS_DEFAULT="$REPO/build:$REPO/tests:$REPO/samples:$REPO/src:$REPO/tools"
SCAN_ROOTS="${SCAN_ROOTS:-$SCAN_ROOTS_DEFAULT}"
# `--print-scan-roots`：打印**生效的默认**根集合，**不扫描、不写盘、立即 exit 0**（`D-G91` 的派生入口）。
for _a in "$@"; do
    case "$_a" in --print-scan-roots) printf '%s\n' "$SCAN_ROOTS_DEFAULT"; exit 0;; esac
done
RETIRED="libole32.dll.so"          # 已退役别名：出现即错

# 名字|权威路径|说明（权威为空 = 无法用单一 sha 定义，显式"不覆盖"+原因）
ITEMS=(
 "libwpfwic.so|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so|T2 的 WIC shim（唯一权威）"
 "libwpfwin32.so|$REPO/src/WpfGfx.Linux.Native/bin/libwpfwin32.so|win32 shim"
 "DirectWrite.Linux.Provider.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/DirectWrite.Linux.Provider.dll|Provider（**权威件的配置跟随唯一声明** src/Linux/build/SelfBuiltConfig.props；本行两条旧文案已过期并在此更正：其一「权威只按 Debug」是 #39 之前的事实；其二「Release 副本见 NO-AUTHORITY」引用的是**已删的死格** ⇒ 现行口径见文件头 CROSS-CONFIG 格：跨配置副本**可见、但一律照样判**）"
 "WpfGfx.Linux.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll|MIL 托管件（Debug 权威）"
 "ReachFramework.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll|框架件（Debug 权威）；2026-09-14 主控任务 2 纳入（前提：波里的 3.6 刷新会收敛落后者）；同名桩件 5,120 B 走 SKIP(stub)"
 "PresentationCore.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/PresentationCore.dll|PC 托管件（Debug 权威）；2026-09-16 W23C 按用户裁决纳入 D-A2（此前 PC 的 32 份非权威副本**没有任何逐份判据**）；暴露出的在册红见同目录 known-red-PC-copies.md（**登记≠已容忍**）"
 "PresentationFramework.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/PresentationFramework.dll|PF 框架件（**权威件的配置跟随唯一声明** src/Linux/build/SelfBuiltConfig.props ⇒ 本格随 $SELFBUILT_CONFIG 走，不写死 Debug/Release）；2026-09-20 车道 W52C 按 #49 的 C1 纳入（此前 PF **一条逐份判据都没有**：删掉任何一份副本**连 MISSING 都不报** —— D-A2 同族，PC 那一格 #23 才补上）。纳入后首次进入判定的副本逐条登记在同目录 known-red-PFWB-copies.md（**登记≠已容忍**）；同名件 CycleStub.PresentationFramework.Linux（13,824 B）走 SKIP(stub)"
 "WindowsBase.dll|$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/WindowsBase.dll|WB 框架件（**权威件的配置跟随唯一声明**，同上）；2026-09-20 车道 W52C 按 #49 的 C1 **同趟**纳入（**两处 ITEMS 必须一致**：本表与 applocal-expect.py 的 ITEMS，由 --list-items 逐项校验）。WB 的副本数远多于 PF（含 PresentationFramework.Linux/bin 自己那一份）⇒ 纳入后首次进入判定的副本逐条登记在 known-red-PFWB-copies.md；同名件 src/Linux/build/CycleStub.* 走 SKIP(stub)"
 "wpfgfx_cor3.so||**权威不进本表**（本格留空：见 ⑦ 与 BRIDGE_REC）：期望 sha **是有的** —— 发布记录 bridge-src-fp.txt 里的 BRIDGE_SO_SHA256（唯一写点 src/Linux/build/publish-milbridge.sh:81，每波发布时重写）；缺的不是记录，是**本表格子表达不了「权威是一份断言、不是一份二进制」** ⇒ 改由独立的 bridge_anchor_check() 逐份核（计数器 BRIDGE-ANCHOR / BRIDGE-NOINFO，**两者都红**）。⚠️ 旧文案「无跨波稳定的期望 sha」**只对一半**（W25B 更正）"
)
sha() { [ -f "$1" ] && sha256sum "$1" | awk '{print $1}' || echo "<missing>"; }
CNT_OK=0; CNT_MISMATCH=0; CNT_SKIPREF=0; CNT_RETIRED=0; CNT_DIVERGENT=0
# 【`D-G62`（W52C2 2026-09-20）】`CNT_NOAUTH`（旧 `NO-AUTHORITY` 格）已删 —— 它是**死格**（见文件头）。
#   取而代之的是**具名诊断格** `CNT_CROSSCFG`：**不判红**（不进下面的判定式），也**不放行**任何副本。
CNT_CROSSCFG=0
CNT_STALE=0; CNT_NEWER=0; CNT_LIBCOPY=0; CNT_SKIPOBJ=0; CNT_SKIPSTUB=0; CNT_REFSRC=0
CNT_MISSING=0; CNT_UNEXPECTED=0
# 【`D-G8` 修复（W24D，2026-09-17）】**"某个权威件整份不见"是一条独立的、必须点名的失败路径。**
#   以前它只印一行 `⚠ 权威件缺失：<路径>`，并在 `scan()` 内部把 `rc` 置 1 —— 而 `scan` 的 rc 在
#   **改前** `:654` 被 `scanrc=$?` 接住后**再也没有被读过**（`grep -c scanrc` = 1；本版该行是 `:715`）⇒ 只要五个计数器恰好全 0，
#   脚本仍然印 `APPSYNC=PASS` + `exit 0`（**可以骗人**：权威整份没了却被读成"一致"）。
#   本计数器让这一类**具名**（进摘要格、进最终判定）；`scanrc` 同时被消费（见文件尾）作为结构性兜底，
#   使得"将来任何只在 `scan()` 里置 rc、却没有计数器覆盖的失败"都不可能被吞掉。
CNT_AUTHMISS=0
# ⑥（TAPPS，2026-09-15，`D-A1` 加固）：`UNEXPECTED` 的两个**可登记子类**（见文件头 ⑥）。
#   两者都计进 CNT_UNEXPECTED（旧消费者行为逐字不变），但**分别留名**：EQ 也红、DIFF 更红。
CNT_DECLGAP_EQ=0; CNT_DECLGAP_DIFF=0
# ⑦（`D-A2-r`，W25B 2026-09-17）：桥的**绝对锚**判据（见文件头 ⑦，实现 `bridge_anchor_check()`）。
#   两个都**红**、都具名、都进摘要格与最终判定：
#     BRIDGE-ANCHOR  某份桥副本 sha ≠ 发布记录的 `BRIDGE_SO_SHA256`（**只看 sha**，mtime 只进诊断文字）
#     BRIDGE-NOINFO  记录缺失 / 解析不出 64 位 hex / 本次扫描根内 0 份桥副本（**没测到 ≠ 一致，不许当绿**）
CNT_BRIDGE_ANCHOR=0; CNT_BRIDGE_NOINFO=0
# 判定口径（主控 2026-09-11 裁定；2026-09-20 `#49` §10 裁定 ② 改一格）：
#   · MISMATCH（与权威**不同**）/ RETIRED（**不该存在**的东西存在） ⇒ APPSYNC=MISMATCH + exit 1
#   · CROSS-CONFIG（**副本所在路径的配置 ≠ 声明配置**）⇒ 只提示，**exit 0**，且**不豁免任何副本**
#     （旧格 `NO-AUTHORITY` 是"判不了 ⇒ 免红"，`D-G62` 裁定改为"看得见 ⇒ 照判"）
#     —— 把"跨配置"与"不一致"混为一谈会让检查器每次跨配置都报红 ⇒ "狼来了"；但反过来**豁免**它，
#        就成了"切一次配置，全仓副本的判据集体失效"（那正是 `#39` 之后发生的现场）。
count_of() { eval "printf '%s' \$$1"; }
short() { printf '%s' "${1:0:16}"; }
mt() { stat -c %Y "$1" 2>/dev/null || echo 0; }

# 路径类别判定（一律用 `${1,,}` 小写化，**不用 `nocasematch`**：那是个会泄漏到后续匹配的全局开关，
# 旧版在 NO-AUTHORITY 分支里 set/unset，其余分支却一直带着它 —— 已改掉）
is_release_path() { case "${1,,}" in */bin/release/*|*/release/*|*/release_*/*) return 0;; *) return 1;; esac; }
is_managed()     { case "${1,,}" in *.dll) return 0;; *) return 1;; esac; }
is_obj()         { case "$1" in */obj/*) return 0;; *) return 1;; esac; }
is_stub()        { case "$1" in */CycleStub.*/*|*/CycleStub/*) return 0;; *) return 1;; esac; }
# 【解析源目录集（2026-09-14 主控派修）】`<HintPath>` 指向的输出目录：**RAR 会从"被引用程序集所在目录"
#   解析传递依赖** ⇒ 这些目录里的副本即使不是启动宿主，也是**能被解析进消费者 app-local 的源**。
#   实例（主控现场抓到、我复现）：`src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/Debug/ReachFramework.dll`
#   经 `ManagedLayer.Tests.csproj:87-89` 的 HintPath 被 RAR 传递解析 ⇒ 拷进该测试的 app-local；
#   PF 一重建（PF⇄Reach 是环成员）那份就旧，消费者一构建就把旧件拷回来 ⇒ **靠"再刷一次"治不好**。
#   判据（主控指定）：**该目录是否出现在任何 csproj 的 `<HintPath>` 里**；属性按下表替换，替换不掉的
#   逐条登记（`HINTPATH_UNRESOLVED`），不静默。
HINTPATH_ROOTS="${HINTPATH_ROOTS:-$REPO}"
HINTPATH_UNRESOLVED=0
# 【期望集合（2026-09-14 主控派单：把"对删除瞎"补掉）】枚举**唯一实现**在 applocal-expect.py：
#   它只从**声明式来源**算期望（csproj 的 <Reference>/<HintPath>/<ProjectReference> + Private≠false +
#   结构性输出目录 + ITEMS 的权威路径），**基数不依赖现场文件是否存在** ⇒ 删掉任何副本，本段输出不变。
EXPECT_TOOL="$(dirname "$0")/applocal-expect.py"
EXPECT_TMP="$(mktemp)"; trap 'rm -f "$EXPECT_TMP"' EXIT
EXPECT_OK=0; EXPECT_SUMMARY="<未算>"
if command -v python3 >/dev/null 2>&1 && [ -f "$EXPECT_TOOL" ] \
   && python3 "$EXPECT_TOOL" "$REPO" "$HINTPATH_ROOTS" > "$EXPECT_TMP" 2>/dev/null; then EXPECT_OK=1; fi
declare -A REFDIR=() EXPECTA=() EXPECTWHY=()
EXPECT_UNKNOWN=()
INVIS_WRITE=(); INVIS_READ=()
INVIS_NW=0; INVIS_NR=0
# 【TAPPS 2026-09-15】主扫的两条自带边界现在**逐条进输出**（以前只有计数、且计数本身漏印）：
INVIS_CAPPED=0; INVIS_CAPPED_LIST=()
INVIS_EXT_WRITE=(); INVIS_EXT_READ=()
INVIS_EXT_NW=0; INVIS_EXT_NR=0
INVIS_IND_WRITE=(); INVIS_IND_NW=0
# 【⑧ `W70B` / `TASK-1002`（2026-09-21）】**期望模型的第二条入口：显式 `<Import>` 闭包**。
#   期望集合原先只由"**工程自己那一份 csproj 文本**"算出来（`applocal-expect.py` 的
#   `refs_of()`/`outdirs()`/`E()`）⇒ **`<Import>` 声明的接线对模型完全不可见**；`src/Linux/samples/ThirdPartyMini`
#   正是极端形态（它**一条 `<Reference>` 都没有**，引用全部来自被导入的 `src/Linux/build/third-party/WpfLinux.props`）
#   ⇒ 它的 10 份"内容==权威"的副本被报成 `UNEXPECTED-EQ`（= `DECL-GAP-EQ`，仍红）。
#   `applocal-expect.py` 现在跟**显式 import 图**（**不是**白名单：不删 `ITEMS`、不调小计数、不手写副本清单）。
#   ⇒ 本校验器必须把这个新通道的**输入本身**显示出来（否则新通道自己就是"看不见的输入"，本仓老毛病）：
#     `#IMPORT`            跟过的边数（全清单用 `grep '^#IMPORT'` 取；逐条印太吵但**可核**）
#     `#UNRESOLVED-IMPORT` **没解析出来的 import**，分两桶：`sdk`（SDK 侧，按构造不在本仓 import 图里）
#                          / `repo`（**本仓接线缺口** —— 有它就说明还有一处接线模型没跟进去）。
#   ⚠️ 覆盖面自认（与本文件其余"主动承认"同一条纪律）：本通道**只跟显式 `<Import>`**；
#      MSBuild 的**隐式**导入（`Directory.Build.props` / `.targets`）**不跟**。
#   ⚠️ 这两格**不进判定、不改 rc**，与 `HINTPATH_UNRESOLVED`（`:230` 那一格）同口径：
#      "登记、不静默"——但 `repo` 桶**不是绿**：见下面那段打印里的逐条点名。
IMPORT_EDGES_N=0; IMPORT_UNRESOLVED_REPO=(); IMPORT_UNRESOLVED_SDK_N=0
if [ "$EXPECT_OK" = 1 ]; then
    while IFS='|' read -r tag a b c d e f g h i j k l; do
        case "$tag" in
            '#REFDIR')  [ -n "$a" ] && REFDIR["$a"]=1;;
            '#EXPECT')  [ -n "$a" ] && { EXPECTA["$a|$b"]=1; EXPECTWHY["$a|$b"]="$c"; };;
            '#UNKNOWN') EXPECT_UNKNOWN+=("$a：$b");;
            '#INVISIBLE') [ "$a" = "write" ] && INVIS_WRITE+=("$b ｜ $c") || INVIS_READ+=("$b ｜ $c");;
            '#INVISIBLE-CAPPED') INVIS_CAPPED_LIST+=("$b 省略 $c 条（该文件全量 $d 条）"); INVIS_CAPPED=$((INVIS_CAPPED+${c:-0}));;
            '#INVISIBLE-EXT') [ "$a" = "write" ] && INVIS_EXT_WRITE+=("$b ｜ $c") || INVIS_EXT_READ+=("$b ｜ $c");;
            '#INVISIBLE-INDIRECT') INVIS_IND_WRITE+=("$b ｜ $c");;
            '#UNRESOLVED') HINTPATH_UNRESOLVED=$((HINTPATH_UNRESOLVED+1));;
            '#IMPORT') IMPORT_EDGES_N=$((IMPORT_EDGES_N+1));;
            '#UNRESOLVED-IMPORT') if [ "${c:-}" = "repo" ]; then IMPORT_UNRESOLVED_REPO+=("$a ｜ $b"); else IMPORT_UNRESOLVED_SDK_N=$((IMPORT_UNRESOLVED_SDK_N+1)); fi;;
            '#SUMMARY') EXPECT_SUMMARY="解析源目录=${a#refdirs=} 期望副本=${b#expect=} 工程数=${c#projects=} 算不出的件=${d#unknown=} 未解析HintPath=${e#unresolved_hintpath=}"; INVIS_NW="${g#invisible_write=}"; INVIS_NR="${h#invisible_read=}"; INVIS_CAPPED="${i#invisible_capped=}"; INVIS_EXT_NW="${j#invisible_ext_write=}"; INVIS_EXT_NR="${k#invisible_ext_read=}"; INVIS_IND_NW="${l#invisible_indirect_write=}";;
        esac
    done < "$EXPECT_TMP"
fi
# 该副本所在（目录,件）是否在期望集合里
in_expect() { [ -n "${EXPECTA[$(realpath -m -- "$(dirname "$1")")|$(basename "$1")]:-}" ]; }
# 期望路径是否落在本次 SCAN_ROOTS 之内（缺件只报"本次扫得到的范围"，否则局部扫描会继承全仓缺口）
in_scan_roots() {
    local p="$1" r
    IFS=:; for r in $SCAN_ROOTS; do
        r="$(realpath -m -- "$r" 2>/dev/null)"
        case "$p" in "$r"/*) IFS=$' \t\n'; return 0;; esac
    done
    IFS=$' \t\n'; return 1
}

# 启动宿主判据：输出目录里有没有 `*.runtimeconfig.json`（库输出目录没有 ⇒ 不是加载源）
is_host_dir() {
    local d; d="$(dirname "$1")"
    [ -n "$(find "$d" -maxdepth 1 -name '*.runtimeconfig.json' -print -quit 2>/dev/null)" ]
}
# 该副本所在目录是否被 csproj 的 HintPath 引用（⇒ RAR 传递解析源）；数据来自 applocal-expect.py
is_refdir() { [ -n "${REFDIR[$(realpath -m -- "$(dirname "$1")" 2>/dev/null)]:-}" ]; }

# 【`D-G62`（W52C2 2026-09-20）：`CROSS-CONFIG` 的两把尺子（见文件头那一格）】
#   path_cfg()  副本/权威**路径**的配置（大小写不敏感；`release_*` 这类 RID 目录归 `Release`）；
#               认不出 ⇒ **空串**（空串**不算**跨配置 —— "认不出" ≠ "不同配置"，不许拿它冒充发现）。
#               Release 那一臂**复用 `is_release_path()`**（同一把尺子只此一处，避免第二个正则分叉：
#               `:200` 的那个函数就是跨副本分组用的同一个大小写不敏感匹配）。
#   跨配置 = 该副本**有**可认出的配置、且**不等于**声明配置 `$SELFBUILT_CONFIG`。
path_cfg() {
    is_release_path "$1" && { printf '%s' Release; return 0; }
    case "${1,,}" in */debug/*) printf '%s' Debug;; *) printf '%s' "";; esac
}
is_cross_config() {
    local c; c="$(path_cfg "$1")"
    [ -n "$c" ] && [ "$c" != "$SELFBUILT_CONFIG" ]
}
# ⑥（TAPPS，`D-A1`）：按 **basename** 从 ITEMS 表取该件的权威 sha（"算不出来"⇒ 空串，**不降级**）。
#   为什么按 basename 而不是按路径：`UNEXPECTED` 判定的对象恰恰是"**期望模型里没有这一格**"的副本，
#   所以不能用期望集合去找它的权威；但"这件东西有没有权威"是 ITEMS 表的静态事实，与期望集合无关。
auth_sha_of() {
    local want="$1" item nm p
    for item in "${ITEMS[@]}"; do
        IFS='|' read -r nm p _ <<<"$item"
        if [ "$nm" = "$want" ] && [ -n "$p" ]; then sha "$p"; return 0; fi
    done
    printf '%s' "<no-authority>"
}

# ⑦（`D-A2-r`，W25B 2026-09-17）**桥的绝对锚判据**（只读；口径与射程见文件头 ⑦）。
#   · 锚 = 发布记录里的 `BRIDGE_SO_SHA256`（唯一写点 `src/Linux/build/publish-milbridge.sh:81`；记录在发布目录里）。
#   · **不进 `ITEMS`**：那一格的下游消费者（`sync-applocal-authority.sh` 的 `cp -f`、`applocal-expect.py`
#     的 `#EXPECT`/`--list-items` 对表）都默认"权威是一份可拷可 stat 的真件"，而记录是**关于**桥的断言。
#   · 行首标签用 `ANCHOR-OK` / `ANCHOR-DIFF`（**绝不** `STALE`/`NEWER-DIFF`）⇒ 结构上不进刷新器的写路径。
BRIDGE_REC="${BRIDGE_REC:-$REPO/src/Linux/build/MilBridge/.artifacts/publish/MilBridge.Linux/release_linux-x64/bridge-src-fp.txt}"
bridge_anchor_check() {
    local rec="$BRIDGE_REC" want="" f asha amt rmt n=0 differ=0
    local files=()
    while IFS= read -r f; do [ -n "$f" ] && files+=("$f"); done \
      < <(IFS=:; for r in $SCAN_ROOTS; do [ -d "$r" ] && find "$r" -name 'wpfgfx_cor3.so' 2>/dev/null; done)
    # 【沙箱豁免】调用方把扫描根收窄到"既不含发布目录、又一份桥副本都没有"⇒ 本次**没覆盖**本判据的对象。
    #   必须**自称**（否则沙箱自检会被自己的新判据打成假红），且**不计数**（没测到东西不是"一致"，
    #   但也不是"发现不一致"）；正常路径（默认 5 个根，含 `$REPO/build`）永远覆盖发布目录 ⇒ 永远判定。
    if [ "${#files[@]}" = 0 ] && ! in_scan_roots "$rec"; then
        printf '    BRIDGE-ANCHOR=SKIPPED 本次扫描根内既无桥副本、也不含发布目录（%s）⇒ 本判据本次无对象（**收窄 SCAN_ROOTS 会连它一起收窄**）\n' "${rec#$REPO/}"
        return 0
    fi
    echo "--- wpfgfx_cor3.so ★绝对锚判据（发布记录 BRIDGE_SO_SHA256；独立只读、不进 ITEMS）"
    printf '    锚记录 %s\n' "${rec#$REPO/}"
    if [ ! -f "$rec" ]; then
        CNT_BRIDGE_NOINFO=$((CNT_BRIDGE_NOINFO+1))
        printf '    BRIDGE-NOINFO %-74s 发布记录**整份不存在**：%s ⇒ 无绝对锚 ⇒ **不许当绿**（先跑 src/Linux/build/publish-milbridge.sh）\n' \
            "wpfgfx_cor3.so" "${rec#$REPO/}"
        return 0
    fi
    want="$(sed -n 's/.*BRIDGE_SO_SHA256=\([0-9a-f]\{64\}\).*/\1/p' "$rec" | head -1)"
    if [ -z "$want" ]; then
        CNT_BRIDGE_NOINFO=$((CNT_BRIDGE_NOINFO+1))
        printf '    BRIDGE-NOINFO %-74s 记录里**解析不出** BRIDGE_SO_SHA256（64 位小写 hex）：%s ⇒ **不许当绿**\n' \
            "wpfgfx_cor3.so" "${rec#$REPO/}"
        return 0
    fi
    rmt="$(mt "$rec")"
    printf '    锚值 BRIDGE_SO_SHA256=%s（记录 mtime=%s；**mtime 只作诊断，本判据只看 sha**）\n' \
        "$(short "$want")" "$(date -d "@$rmt" '+%F %T' 2>/dev/null || printf '%s' "$rmt")"
    if [ "${#files[@]}" != 0 ]; then for f in "${files[@]}"; do
        asha="$(sha "$f")"; amt="$(mt "$f")"; n=$((n+1))
        if [ "$asha" = "$want" ]; then
            printf '    ANCHOR-OK     %-76s %s（== 发布记录）\n' "${f#$REPO/}" "$(short "$asha")"
        else
            CNT_BRIDGE_ANCHOR=$((CNT_BRIDGE_ANCHOR+1)); differ=$((differ+1))
            printf '    ANCHOR-DIFF   %-76s RECORD %s  ACTUAL %s（诊断：%s %s 秒 —— **只看 sha ⇒ 红**；"记录过期"与"件被换"都红、本判据不给成因）\n' \
                "${f#$REPO/}" "$(short "$want")" "$(short "$asha")" \
                "$([ "$amt" -lt "$rmt" ] && echo '副本早于记录' || echo '副本不早于记录')" \
                "$(( amt < rmt ? rmt-amt : amt-rmt ))"
        fi
    done; fi
    if [ "$n" = 0 ]; then
        CNT_BRIDGE_NOINFO=$((CNT_BRIDGE_NOINFO+1))
        printf '    BRIDGE-NOINFO %-74s 本次扫描根内**一份桥副本都没有**（%s）⇒ 没测到东西 ⇒ **不许当绿**：桥可能被发布到扫描根之外\n' \
            "wpfgfx_cor3.so" "$SCAN_ROOTS"
    else
        printf '    ⇒ 本次枚举到桥副本 %s 份：与记录一致 %s 份 / **不符 %s 份**\n' "$n" "$((n-differ))" "$differ"
    fi
    return 0
}

scan() {
    local rc=0
    for item in "${ITEMS[@]}"; do
        IFS='|' read -r name exp note <<<"$item"
        echo "--- $name"
        echo "    权威 $name：$(short "$(sha "$exp")")  ${exp:-（无）}"
        [ -z "$exp" ] && { echo "    $note"; continue; }
        [ -f "$exp" ] || { CNT_AUTHMISS=$((CNT_AUTHMISS+1)); rc=1; printf '    AUTH-MISSING  %-76s ⚠ 权威件缺失：%s ⇒ **本件每一份副本都没有可比的真值**（`D-G8`：以前这行只告警、rc 被丢弃 ⇒ 仍可印 PASS）\n' "$name" "$exp"; }
        local esha; esha="$(sha "$exp")"; local emt; emt="$(mt "$exp")"
        local found=0 skipped_ref=0 skipped_obj=0 skipped_stub=0
        while IFS= read -r f; do
            # ⓪ 权威件本身：永远 OK（它不是"副本"）
            if [ "$f" = "$exp" ]; then
                CNT_OK=$((CNT_OK+1)); found=1
                printf '    OK            %-76s %s（这就是权威件本身）\n' "${f#$REPO/}" "$(short "$esha")"
                continue
            fi
            # ① 引用程序集（obj/ref、refint）：按设计就与实现程序集不同 ⇒ 计数后统一打印一行
            case "$f" in */obj/*/ref/*|*/obj/*/refint/*) skipped_ref=$((skipped_ref+1)); continue;; esac
            # ② 循环桩工程里的同名件：按构造就是另一个程序集 ⇒ 不判定（放在 obj 判定**之前**：
            #    这样 `CycleStub.*/obj/...` 也会被报成 SKIP(stub) —— 它"为什么不同"的答案更准确）
            if is_stub "$f"; then
                CNT_SKIPSTUB=$((CNT_SKIPSTUB+1)); skipped_stub=$((skipped_stub+1)); found=1
                printf '    SKIP(stub)    %-76s %s（循环桩工程，%s B；权威 %s B ⇒ 同名≠同类）\n' \
                    "${f#$REPO/}" "$(short "$(sha "$f")")" "$(stat -c%s "$f" 2>/dev/null)" "$(stat -c%s "$exp" 2>/dev/null)"
                continue
            fi
            # ③ obj 下其余（编译器中间件）：不是加载源 ⇒ 不判定，但**逐条打印 sha**（不静默）
            if is_obj "$f"; then
                CNT_SKIPOBJ=$((CNT_SKIPOBJ+1)); skipped_obj=$((skipped_obj+1)); found=1
                local osha; osha="$(sha "$f")"
                printf '    SKIP(obj)     %-76s %s（中间件，不是加载源；与权威%s）\n' \
                    "${f#$REPO/}" "$(short "$osha")" "$([ "$osha" = "$esha" ] && echo 一致 || echo 不同)"
                continue
            fi
            # ④（`D-G62`，W52C2）**跨配置 = 具名诊断格 `CROSS-CONFIG`：只打印，不放行、不判红**
            #    ⚠️ 与旧格（`NO-AUTHORITY`）的**唯一**行为差：旧格 `continue`（**豁免**该副本 ⇒ 判不了），
            #       本格**不 `continue`** ⇒ 该副本**继续**走 ⑤/⑥ 的既有判据（`LIB-COPY` / `OK` /
            #       `STALE` / `NEWER-DIFF` / `UNEXPECTED-*`）⇒ 跨配置副本**照样计红**（裁定要求）。
            if is_cross_config "$f"; then
                CNT_CROSSCFG=$((CNT_CROSSCFG+1))
                printf '    CROSS-CONFIG  %-76s ACTUAL %s（副本配置=%s ≠ 声明配置=%s ⇒ **只增加可见性**：本份照样走下面的判据，`STALE`/`NEWER-DIFF` 一条都不会因此豁免）\n' \
                    "${f#$REPO/}" "$(short "$(sha "$f")")" "$(path_cfg "$f")" "$SELFBUILT_CONFIG"
            fi
            # ⑤ 库输出目录（无 runtimeconfig.json）：不是启动宿主 ⇒ 私有依赖副本不是加载源 ⇒ 不判定
            #    （只对托管件用这条；原生 shim 的加载路径历史事故多，一律照判）
            if is_managed "$f" && ! is_host_dir "$f" && ! is_refdir "$f"; then
                CNT_LIBCOPY=$((CNT_LIBCOPY+1)); found=1
                local lsha; lsha="$(sha "$f")"
                printf '    LIB-COPY      %-76s %s（库输出目录无 runtimeconfig.json ⇒ 不是启动宿主 ⇒ 与权威%s）\n' \
                    "${f#$REPO/}" "$(short "$lsha")" "$([ "$lsha" = "$esha" ] && echo 一致 || echo 不同，但不是加载源)"
                continue
            fi
            # ⑥ 同类（启动宿主 + 同配置 + 非桩/非中间件）：OK / STALE / NEWER-DIFF
            found=1
            local asha amt age role; asha="$(sha "$f")"; amt="$(mt "$f")"; age=$((emt - amt)); role=""
            if is_refdir "$f"; then CNT_REFSRC=$((CNT_REFSRC+1)); role="；**该目录被 csproj 的 HintPath 引用 ⇒ RAR 会从这里解析传递依赖（解析源，必须与权威一致）**"; fi
            # 【多余副本】期望模型覆盖的件，若所在目录**没有任何引用图要求** ⇒ 报 UNEXPECTED（不静默、且是红）：
            #   判据与 HINTPATH_UNRESOLVED 同源（applocal-expect.py 的引用图）；原生件不参与（它们的期望
            #   来源是 shell cp，模型算不出 ⇒ 见 EXPECT=UNKNOWN），故 `is_managed` 前置。
            #   ⑥（TAPPS，2026-09-15，`D-A1` 加固）：**同一个 `UNEXPECTED`，按"内容 vs 权威"显式二分**：
            #     · `DECL-GAP-EQ`  = sha == 权威 ⇒ 未声明的传递依赖副本（**内容对，但声明图有缺口**）
            #                        ⇒ 具名、可见、**仍然红**（`D-A1` 现场的形态）
            #     · `DECL-GAP-DIFF`= sha ≠ 权威 ⇒ **硬失败**（内容不同**不可能**被读成 EQ）
            #   两个子类都计进 CNT_UNEXPECTED ⇒ `UNEXPECTED=N` 与 exit code 逐字向后兼容。
            if [ "$EXPECT_OK" = 1 ] && is_managed "$f" && ! in_expect "$f"; then
                local ash; ash="$(sha "$f")"; local aauth; aauth="$(auth_sha_of "$(basename "$f")")"
                CNT_UNEXPECTED=$((CNT_UNEXPECTED+1)); rc=1
                if [ "$aauth" = "$ash" ]; then
                    CNT_DECLGAP_EQ=$((CNT_DECLGAP_EQ+1))
                    printf '    UNEXPECTED-EQ %-76s %s（**不在声明图里**，但 **sha == 权威 %s** ⇒ 未声明的传递依赖副本；**不是陈旧件，也不许当绿**）%s\n' \
                        "${f#$REPO/}" "$(short "$ash")" "$(short "$aauth")" "$role"
                else
                    CNT_DECLGAP_DIFF=$((CNT_DECLGAP_DIFF+1))
                    printf '    UNEXPECTED-DIFF %-74s %s（**不在声明图里** 且 **sha ≠ 权威 %s** ⇒ **硬失败**：内容不同的未声明副本；权威算不出时也落这一类）%s\n' \
                        "${f#$REPO/}" "$(short "$ash")" "$(short "$aauth")" "$role"
                fi
                continue
            fi
            if [ "$asha" = "$esha" ]; then
                CNT_OK=$((CNT_OK+1)); printf '    OK            %-76s %s%s\n' "${f#$REPO/}" "$(short "$asha")" "$role"
            elif [ "$amt" -lt "$emt" ]; then
                CNT_MISMATCH=$((CNT_MISMATCH+1)); CNT_STALE=$((CNT_STALE+1)); rc=1
                printf '    STALE         %-76s EXPECT %s  ACTUAL %s（副本早 %s 秒 ⇒ 必须刷新）%s\n' \
                    "${f#$REPO/}" "$(short "$esha")" "$(short "$asha")" "$age" "$role"
            else
                CNT_MISMATCH=$((CNT_MISMATCH+1)); CNT_NEWER=$((CNT_NEWER+1)); rc=1
                printf '    NEWER-DIFF    %-76s EXPECT %s  ACTUAL %s（副本**不早于**权威 %s 秒 ⇒ 不是"落后"；查跨配置/桩件/权威是否落后）%s\n' \
                    "${f#$REPO/}" "$(short "$esha")" "$(short "$asha")" "$((-age))" "$role"
            fi
        done < <(IFS=:; for r in $SCAN_ROOTS; do [ -d "$r" ] && find "$r" -name "$name" 2>/dev/null; done)
        [ "$skipped_ref" != 0 ] && { CNT_SKIPREF=$((CNT_SKIPREF+skipped_ref)); echo "    SKIP(ref)     引用程序集 $skipped_ref 份（obj/ref、refint：**按设计就与实现程序集不同** ⇒ 不覆盖）"; }
        [ "$skipped_obj" != 0 ] && echo "    （其中 obj 中间件 $skipped_obj 份已逐条打印）"
        [ "$skipped_stub" != 0 ] && echo "    （其中循环桩 $skipped_stub 份已逐条打印）"
        [ "$found" = 0 ] && echo "    （未找到副本）"
    done
    echo "--- 期望集合 vs 现场（**权威枚举**：基数不依赖现场文件是否存在）"
    if [ "$EXPECT_OK" != 1 ]; then
        echo "    ⚠️ 期望集合**算不出来**（applocal-expect.py 不可用）：本趟只查了现存副本 ⇒ **不等于通过**"
        rc=3
    else
        echo "    摘要：$EXPECT_SUMMARY（解析源目录 / 期望副本 / 工程数 / 算不出的件 / 未解析 HintPath）"
        echo "    ⚠️ **本校验器看不见的拷贝点：写点 $INVIS_NW 处 / 只读 $INVIS_NR 处** —— 写点会创建或删除副本，但**不在**期望模型里"
        echo "       ⇒ 这些路径上的**删除不会被本校验器报出**（今天实测过：删一个写点产物只是 OK 41→40，无任何红行）"
        for w in "${INVIS_WRITE[@]:-}"; do [ -n "$w" ] && echo "       [写点] $w"; done
        for r in "${INVIS_READ[@]:-}"; do [ -n "$r" ] && echo "       [只读] $r"; done
        # 【TAPPS 2026-09-15】截断/盲区**必须自称**（否则读者会把"印出来的清单"当成"全仓清单"）
        if [ "${INVIS_CAPPED:-0}" != 0 ]; then
            echo "       ⚠️ 上面只读清单**不完整**：每文件只印 3 条 ⇒ 另有 **$INVIS_CAPPED 条只读点未印**（计数是全量）"
            for c in "${INVIS_CAPPED_LIST[@]:-}"; do [ -n "$c" ] && echo "          [未印] $c"; done
        fi
        echo "       ⚠️ 而且主扫**只扫 \`src/Linux/build/**/*.sh\`** ⇒ 另有补扫的写点 **$INVIS_EXT_NW 处**（\`src/Linux/build/\` 之外的 .sh ＋ 任何 MSBuild \`<Copy>\`；主扫已报过的不重复）"
        for w in "${INVIS_EXT_WRITE[@]:-}"; do [ -n "$w" ] && echo "       [补扫·写点] $w"; done
        echo "       ⚠️ 还有第三类主扫**结构性**抓不到的写点：**源路径藏在变量里**（行里一个字面件名都没有）⇒ 补扫 **$INVIS_IND_NW 处**"
        for w in "${INVIS_IND_WRITE[@]:-}"; do [ -n "$w" ] && echo "       [补扫·间接写点] $w"; done
        echo "          （全清单：\`python3 $EXPECT_TOOL <REPO> | grep -E '^#INVISIBLE-(EXT|INDIRECT)'\`）"
        echo "          ⚠️ **三张清单仍不是全仓拷贝点的完全集**：间接补扫只做**单文件内**的轻量数据流（不做跨文件、"
        echo "             不做函数间、不认 \`\$(...)\` 里现算的路径）⇒ 它给的是**下界**，不是"已穷尽"。"
        echo "       （目标形态：拷贝点写 manifest ⇒ 本校验器读 manifest、缺 manifest 报 NOINFO；见 REPORT.md §28.3）"
        # 【⑧ `W70B`/`TASK-1002`】新通道（显式 `<Import>` 闭包）的**输入**必须可见 —— 见上面 `IMPORT_EDGES_N` 的注释。
        echo "    期望模型的第二条入口（⑧ 显式 \`<Import>\` 闭包）：**跟随的边 $IMPORT_EDGES_N 条**"
        echo "       （全清单：\`python3 $EXPECT_TOOL $REPO | grep '^#IMPORT'\`；逐条形如 \`工程 → 被导入件（深度）\`）"
        echo "       被导入件里的 <Reference>/<HintPath>/<ProjectReference>/<TargetFramework> 与工程**自写的一视同仁**；"
        echo "         **这不是白名单**：判据是"显式 import 图"这条通用规则，\`ITEMS\`/计数器/判定式一字未动。"
        echo "       ⚠️ 覆盖面自认：只跟**显式** \`<Import>\`；**隐式**导入（\`Directory.Build.props\`/\`.targets\`）**不跟**。"
        if [ "${#IMPORT_UNRESOLVED_REPO[@]}" = 0 ]; then
            echo "       未能解析的 import：**repo（本仓接线缺口）= 0 条**（⇒ 本趟没有"接线被模型漏掉"的 import）／ sdk（SDK 侧，按构造不在本仓 import 图里）$IMPORT_UNRESOLVED_SDK_N 条"
        else
            echo "       未能解析的 import：**repo（本仓接线缺口）= ${#IMPORT_UNRESOLVED_REPO[@]} 条** ⚠️ **不许当绿**（有一处接线模型没跟进去 ⇒ 那边的副本仍可能被判成"多余"）／ sdk $IMPORT_UNRESOLVED_SDK_N 条"
            for u in "${IMPORT_UNRESOLVED_REPO[@]}"; do echo "       [未解析·repo] $u"; done
        fi
        local exp_n=0
        for k in "${!EXPECTA[@]}"; do
            exp_n=$((exp_n+1))
            local d="${k%|*}" it="${k##*|}"
            in_scan_roots "$d/$it" || continue          # 扫描根之外的期望不在本次判定范围
            if [ ! -f "$d/$it" ]; then
                CNT_MISSING=$((CNT_MISSING+1)); rc=1
                printf '    MISSING       %-76s ← 期望来源：%s\n' "${d#$REPO/}/$it" "${EXPECTWHY[$k]}"
                printf '                  %s\n' "判据：csproj 的 <Reference>(Private≠false)/<HintPath>/<ProjectReference> 引用图 + 该工程输出目录存在；**该判据不看现场文件**"
            fi
        done
        echo "    期望副本 $exp_n 条，其中缺件 $CNT_MISSING 条；算不出的件 ${#EXPECT_UNKNOWN[@]} 个"
        for u in "${EXPECT_UNKNOWN[@]:-}"; do [ -n "$u" ] && echo "    EXPECT=UNKNOWN $u"; done
    fi
    echo "--- 跨副本一致性（**同类、同配置、加载源之间**；不需要权威就能抓"落后一代"）"
    echo "    口径（2026-09-14 翻正）：本分组**大小写不敏感**，且 Release 组含 RID 目录 \`release_*\`；"
    echo "      仍排除 obj 中间件 / CycleStub 桩件 / 非启动宿主（库输出）—— 与逐副本判定同一把尺子。"
    for item in "${ITEMS[@]}"; do
        IFS='|' read -r name _ _ <<<"$item"
        for cfg in Release Debug; do
            mapfile -t rows < <(IFS=:; for r in $SCAN_ROOTS; do [ -d "$r" ] && find "$r" -name "$name" 2>/dev/null; done \
                                 | while IFS= read -r f; do
                                       # 配置归属（**大小写不敏感**；Release 还含 RID 目录 `release_*`）：
                                       #   2026-09-14 主控派单翻正：原先用 `-path "*/$cfg/*"`（大小写敏感）
                                       #   ⇒ 小写 `release/`、`release_linux-x64/` 从不进分组（登记缺口 ⑤）。
                                       case "${f,,}" in
                                           */release/*|*/release_*/*) [ "$cfg" = "Release" ] || continue;;
                                           */debug/*)                 [ "$cfg" = "Debug" ]   || continue;;
                                           *) continue;;
                                       esac
                                       is_stub "$f" && continue      # 循环桩：另一个程序集（按构造）
                                       is_obj "$f" && continue       # 中间件：与 bin 不同是常态
                                       if is_managed "$f" && ! is_host_dir "$f" && ! is_refdir "$f"; then continue; fi   # 库输出且**不被任何 HintPath 引用**：不是加载源
                                       printf '%s\n' "$f"
                                   done)
            [ "${#rows[@]}" -lt 2 ] && continue
            mapfile -t shas < <(for f in "${rows[@]}"; do sha "$f"; done | sort -u)
            if [ "${#shas[@]}" -gt 1 ]; then
                CNT_DIVERGENT=$((CNT_DIVERGENT+1)); echo "    DIVERGENT     $name [$cfg] 有 ${#shas[@]} 种 sha："
                for f in "${rows[@]}"; do printf '        %-74s %s  %s\n' "${f#$REPO/}" "$(short "$(sha "$f")")" "$(stat -c %y "$f" | cut -c1-19)"; done
                rc=1
            elif [ "${#rows[@]}" -gt 1 ]; then
                echo "    CONSISTENT    $name [$cfg] ${#rows[@]} 份副本同 sha $(short "${shas[0]}")"
            fi
        done
    done
    echo "--- $RETIRED（已退役别名，出现即错）"
    local bad=0
    while IFS= read -r f; do bad=1; CNT_RETIRED=$((CNT_RETIRED+1)); printf '    RETIRED       %-76s %s\n' "${f#$REPO/}" "$(short "$(sha "$f")")"; rc=1; done \
      < <(IFS=:; for r in $SCAN_ROOTS; do [ -d "$r" ] && find "$r" -name "$RETIRED" 2>/dev/null; done)
    [ "$bad" = 0 ] && echo "    OK            （全仓没有退役别名）"
    return $rc
}

if [ "${1:-}" = "--list-items" ]; then
    # 【W23C / `D-A2` 补的一把小钥匙】**为什么需要**：权威表有**两份**（本脚本的 `ITEMS` + `applocal-expect.py`
    #   的 `ITEMS`，后者管 `MISSING`/`UNEXPECTED` 的期望集合）。`applocal-expect.py:41` 的注释逐字写着
    #   "改一处要同步另一处 —— 由 check 的 `--list-items` 校验"，但那个开关**以前根本不存在**
    #   （实测：改动前 `grep -n 'list-items' check-applocal-sync.sh` = 0 命中）⇒ 那句承诺一直是空头支票。
    #   而 `D-A2` 补 `PresentationCore.dll` 恰恰是**两处都要改**的动作 ⇒ 把支票兑成代码。
    #   **只读、零副作用**：不扫描、不判定、不改任何文件；`APPSYNC=` 那套退出码不受影响
    #   （本模式自带 `ITEMS_SYNC=YES/NO`，退出码 0 / 1；python 不可用时 3 = NOINFO，**不等于一致**）。
    echo "权威根：$REPO"
    echo "--- 本脚本的 ITEMS（${#ITEMS[@]} 项）"
    for item in "${ITEMS[@]}"; do IFS='|' read -r nm p _ <<<"$item"; printf '    %-34s %s\n' "$nm" "${p:-（无权威：显式不覆盖）}"; done
    echo "--- $EXPECT_TOOL 的 ITEMS"
    PYOUT="$(python3 - "$EXPECT_TOOL" "$REPO" <<'PYEOF' 2>/dev/null
import importlib.util, sys
spec = importlib.util.spec_from_file_location("apexpect", sys.argv[1])
m = importlib.util.module_from_spec(spec)
sys.argv = [sys.argv[1], sys.argv[2], sys.argv[2]]          # 模块级会读 argv[1]/argv[2]
spec.loader.exec_module(m)
for n, a in m.ITEMS:
    print("%s|%s" % (n, a))
PYEOF
)"
    if [ -z "$PYOUT" ]; then
        echo "    ⚠ $EXPECT_TOOL 读不出来（python3 不可用或导入失败）⇒ **不等于一致**"
        echo "ITEMS_SYNC=NOINFO（权威表有两份，第二份读不出来 ⇒ 一致性无法判断）"; exit 3
    fi
    printf '%s\n' "$PYOUT" | while IFS='|' read -r nm p; do printf '    %-34s %s\n' "$nm" "${p:-（无权威：显式不覆盖）}"; done
    sync=1
    for item in "${ITEMS[@]}"; do
        IFS='|' read -r nm p _ <<<"$item"
        q="$(printf '%s\n' "$PYOUT" | awk -F'|' -v n="$nm" '$1==n{print $2; found=1} END{if(!found) print "\x01ABSENT\x01"}')"
        if [ "$q" = "$(printf '\x01ABSENT\x01')" ]; then echo "    ✗ $nm：只在 .sh 里有（.py 缺 ⇒ 期望集合里没有它）"; sync=0
        elif [ "$q" != "$p" ]; then echo "    ✗ $nm：权威路径不一致 —— .sh=[$p] .py=[$q]"; sync=0
        else echo "    ✓ $nm：权威一致（${p:-（无权威）}）"; fi
    done
    while IFS='|' read -r nm p; do
        printf '%s\n' "${ITEMS[@]}" | grep -q "^$nm|" || echo "    ✗ $nm：只在 .py 里有（.sh 缺 ⇒ 没有逐份判据）"
    done <<<"$PYOUT"
    if [ "$sync" = 1 ]; then echo "ITEMS_SYNC=YES（两张权威表的**件名与权威路径逐项一致**）；本模式只打印、不扫描"; exit 0; fi
    echo "ITEMS_SYNC=NO（两张权威表不一致 ⇒ 逐条修上面的 ✗；**这是红，不是提示**）"; exit 1
fi

if [ "${1:-}" = "--selftest" ]; then
    # 【`D-G13`（W26C）】子例的第三态：**仪器未能自证**（`NOINFO`）≠ 通过、也 ≠ 判据失败。
    #   记数 ⇒ 末尾不给 `SELFTEST=PASS`/rc=0，而以 rc=3 收尾（与文件尾 `APPSYNC=NOINFO` 的 `exit 3` 同源）。
    #   FAIL 仍然**立即** `exit 1`（红不受影响，逐例读数一个都不吞）。
    SELFTEST_NOINFO=0
    tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
    PREV_AUTH="$REPO"
    PROV="DirectWrite.Linux.Provider.dll"
    # 沙箱一律建成"启动宿主目录 + bin/Debug"（否则会被 LIB-COPY 口径正确地排除掉，测不到想测的东西）
    mkdir -p "$tmp/good/P/bin/Debug" "$tmp/bad/P/bin/Debug" "$tmp/good/prov/bin/Debug"
    touch "$tmp/good/P/bin/Debug/P.runtimeconfig.json" "$tmp/bad/P/bin/Debug/P.runtimeconfig.json"
    # 新契约（2026-09-14）：副本要有**声明式来源**（否则按 UNEXPECTED 判红）⇒ 给 good 沙箱一个工程图
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/good/prov/Prov.csproj"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/good" "$PROV" > "$tmp/good/P/P.csproj"
    # ⚠️【2026-10-05 修·`bin/Debug` 写死点】权威件在 `bin/$SELFBUILT_CONFIG`（现 = Release）。
    #   旧句从**硬编码** `bin/Debug/` 取"正确副本"的源 ⇒ 取到的是**跨配置陈旧副本**
    #   （实测 `Provider/bin/Debug/DirectWrite.Linux.Provider.dll` sha 与权威不同）⇒ 沙箱里
    #   那份"正确副本"其实 sha≠权威 ⇒ `SELFTEST_B=FAIL（MISSING/NEWER-DIFF）`，而**不是**判据坏了。
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/good/prov/bin/Debug/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so" "$tmp/good/P/bin/Debug/libwpfwic.so"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/good/P/bin/Debug/$PROV"
    head -c 4096 "$tmp/good/P/bin/Debug/libwpfwic.so" > "$tmp/bad/P/bin/Debug/libwpfwic.so"
    head -c 4096 "$tmp/good/P/bin/Debug/$PROV"       > "$tmp/bad/P/bin/Debug/$PROV"
    touch -d '2020-01-01' "$tmp/bad/P/bin/Debug/libwpfwic.so"   # .so 走 STALE、.dll 走 NEWER-DIFF：两条分支都测到
    echo "=== 自检 A：错 sha（.so + 托管 .dll，都在启动宿主目录里）必须 MISMATCH 且 exit≠0 ==="
    # ⚠ 必须先取变量再判定：`set -o pipefail` 下"脚本 exit≠0 | grep"会让整条管道非零，
    #   直接写 `script | grep -q X` 会把"报红成功"误判成失败（本脚本自己踩过一次）。
    outA="$(SCAN_ROOTS="$tmp/bad" AUTH_ROOT="$PREV_AUTH" "$0")"; rcA=$?
    if grep -qE 'MISMATCH|STALE|NEWER-DIFF' <<<"$outA" && grep -q 'APPSYNC=MISMATCH' <<<"$outA" && [ "$rcA" != 0 ]; then
        echo "SELFTEST_A=PASS（两类错件都被报红，exit=$rcA）"
        printf '%s\n' "$outA" | grep -E 'STALE|NEWER-DIFF' | head -2
    else echo "SELFTEST_A=FAIL（rc=$rcA）"; printf '%s\n' "$outA" | tail -6; exit 1; fi
    echo "=== 自检 B：正确副本必须全 OK 且 exit=0 ==="
    outB="$(SCAN_ROOTS="$tmp/good" HINTPATH_ROOTS="$tmp/good" AUTH_ROOT="$PREV_AUTH" "$0")"; rcB=$?
    if [ "$rcB" = 0 ] && grep -q "APPSYNC=PASS" <<<"$outB" \
       && ! grep -qE '^    (STALE|NEWER-DIFF|UNEXPECTED|MISSING|DIVERGENT|RETIRED)' <<<"$outB"; then
        echo "SELFTEST_B=PASS（exit=$rcB，全绿：.so + .dll + 产出目录那份，且都有声明式来源）"
    else echo "SELFTEST_B=FAIL（rc=$rcB）"; printf '%s\n' "$outB" | tail -6; exit 1; fi
    echo "=== 自检 C（真实场景复演）：同配置两份副本一新一旧 ⇒ 必须 DIVERGENT 且 exit≠0 ==="
    mkdir -p "$tmp/probe/ProbeA/bin/Release" "$tmp/probe/ProbeB/bin/Release"
    touch "$tmp/probe/ProbeA/bin/Release/A.runtimeconfig.json" "$tmp/probe/ProbeB/bin/Release/B.runtimeconfig.json"
    cp "$tmp/good/P/bin/Debug/$PROV" "$tmp/probe/ProbeA/bin/Release/$PROV"
    cp "$tmp/bad/P/bin/Debug/$PROV"  "$tmp/probe/ProbeB/bin/Release/$PROV"
    outC="$(SCAN_ROOTS="$tmp/probe" AUTH_ROOT="$PREV_AUTH" "$0")"
    if grep -q DIVERGENT <<<"$outC" && grep -q "APPSYNC=MISMATCH" <<<"$outC" \
       && ! SCAN_ROOTS="$tmp/probe" AUTH_ROOT="$PREV_AUTH" "$0" >/dev/null; then
        echo "SELFTEST_C=PASS（旧 provider dll 被跨副本判定抓出）"; printf '%s\n' "$outC" | grep -E "DIVERGENT|ProbeB" | head -3
    else echo "SELFTEST_C=FAIL —— 下面是 C 自己的完整输出，供定位："; printf '%s\n' "$outC" | tail -8; exit 1; fi
    echo "=== 自检 D（D-G62）：跨配置副本必须**具名 CROSS-CONFIG** 且**照样判红**；同配置陈旧副本 CROSS-CONFIG=0 ==="
    # 【`D-G62`（W52C2 2026-09-20，主控 `#49` §10 裁定 ②）】**本段被整体重写**：
    #   旧 D 测的是死格 `NO-AUTHORITY`（"副本在 Release 布局 ⇒ 判不了 ⇒ exit 0"）。该格已删（见文件头），
    #   取而代之的判据是：**跨配置副本照样按同代判据判红**，只是**多印一格具名诊断**。所以本段改成
    #   **同一沙箱内的成对两极化**（裁定给的两条判据逐字对应）：
    #     (a) 在**声明配置之外**的目录里放一份 sha≠权威 的副本 ⇒ `CROSS-CONFIG=1` **且该份仍在 `STALE`**（rc≠0）；
    #     (b) 同一份挪到**声明配置内**的目录 ⇒ `CROSS-CONFIG=0`、**仍 `STALE`**（⇒ 跨配置不是免红券）。
    #   沙箱形态与既有夹具同源：一个 prov 工程产出权威副本，两个 App 工程用 `HintPath + Private=true`
    #   把 prov 的产物**声明**进自己的输出目录（⇒ 该副本是"解析源"，走 ⑥ 的同代判据而不是 UNEXPECTED）。
    OTHER_CFG="$([ "$SELFBUILT_CONFIG" = Debug ] && printf '%s' Release || printf '%s' Debug)"   # 另一个配置（现场 = Debug）
    mkdir -p "$tmp/d/prov/bin/$SELFBUILT_CONFIG" "$tmp/d/AppS" "$tmp/d/AppX"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/d/prov/Prov.csproj"
    for app in AppS AppX; do
        printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/%s/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' \
            "$tmp/d" "$SELFBUILT_CONFIG" "$PROV" > "$tmp/d/$app/$app.csproj"
    done
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/d/prov/bin/$SELFBUILT_CONFIG/$PROV"   # 权威副本（同 sha ⇒ OK、不算跨配置）
    mkdir -p "$tmp/d/AppX/bin/$OTHER_CFG"; touch "$tmp/d/AppX/bin/$OTHER_CFG/AppX.runtimeconfig.json"
    head -c 4096 "$tmp/d/prov/bin/$SELFBUILT_CONFIG/$PROV" > "$tmp/d/AppX/bin/$OTHER_CFG/$PROV"
    touch -d '2020-01-01' "$tmp/d/AppX/bin/$OTHER_CFG/$PROV"          # 明确"早于权威"⇒ 走 STALE（不是 NEWER-DIFF）
    outD1="$(SCAN_ROOTS="$tmp/d" HINTPATH_ROOTS="$tmp/d" AUTH_ROOT="$PREV_AUTH" "$0")"; rcD1=$?
    mkdir -p "$tmp/d/AppS/bin/$SELFBUILT_CONFIG"; touch "$tmp/d/AppS/bin/$SELFBUILT_CONFIG/AppS.runtimeconfig.json"
    cp -f "$tmp/d/AppX/bin/$OTHER_CFG/$PROV" "$tmp/d/AppS/bin/$SELFBUILT_CONFIG/$PROV"
    touch -d '2020-01-01' "$tmp/d/AppS/bin/$SELFBUILT_CONFIG/$PROV"
    rm -rf "$tmp/d/AppX/bin"                                          # 只留下"同配置陈旧副本"这一态
    outD2="$(SCAN_ROOTS="$tmp/d" HINTPATH_ROOTS="$tmp/d" AUTH_ROOT="$PREV_AUTH" "$0")"; rcD2=$?
    dCC()    { printf '%s' "$1" | grep -m1 '^计数：' | sed -n 's/.*CROSS-CONFIG=\([0-9]*\).*/\1/p'; }
    dStale() { printf '%s' "$1" | grep -m1 '^计数：' | sed -n 's/.*STALE=\([0-9]*\).*/\1/p'; }
    # ⚠️ 三条**必须一起**成立（缺一条就等于"跨配置被豁免了"，那正是本段要防的）：
    #    ① (a) 趟：`CROSS-CONFIG=1`；② (a) 趟：同一份**仍在 `STALE`**（`STALE>=1`）且 rc≠0；
    #    ③ (b) 趟：`CROSS-CONFIG=0` 而 `STALE>=1`（⇒ 判红与"跨不跨配置"无关）。
    if [ "$(dCC "$outD1")" = 1 ] && [ "$(dStale "$outD1")" -ge 1 ] && [ "$rcD1" != 0 ] \
       && [ "$(dCC "$outD2")" = 0 ] && [ "$(dStale "$outD2")" -ge 1 ] && [ "$rcD2" != 0 ] \
       && grep -q '^    CROSS-CONFIG ' <<<"$outD1" && grep -q '^    STALE ' <<<"$outD1"; then
        echo "SELFTEST_D=PASS（(a) 跨配置目录（$OTHER_CFG）里的陈旧副本 ⇒ CROSS-CONFIG=1 **且仍 STALE**、exit=$rcD1；(b) 同一份挪进声明配置（$SELFBUILT_CONFIG）目录 ⇒ CROSS-CONFIG=0 **仍 STALE**、exit=$rcD2 ⇒ 跨配置不放行）"
        printf '%s\n' "$outD1" | grep -E 'CROSS-CONFIG|STALE' | head -2
    else echo "SELFTEST_D=FAIL（rc=$rcD1/$rcD2 CC=$(dCC "$outD1")/$(dCC "$outD2") STALE=$(dStale "$outD1")/$(dStale "$outD2")）"; printf '%s\n' "$outD1" | tail -5; exit 1; fi

    # ── E：**两极化**（权威件换 sha ⇒ 红；还原 ⇒ 绿）——用 AUTH_ROOT 换权威，不碰真件 ──
    #   ⚠ `D-G8`（W24D）：本沙箱的 AUTH_ROOT 是 `$tmp/authB` ⇒ **八件权威必须俱全**，否则 `scanrc` 一被消费，
    #     这一条会立刻变成**假红**（那正是"SELFTEST_E 必须与 rc 消费同趟改"的理由）。所以下面把
    #     `ReachFramework.dll` 与 `PresentationCore.dll` 的权威也造进沙箱，并断言 `AUTH-MISSING=0`。
    #   ⚠⚠ **`#49` `C1`（W52C 2026-09-20）：夹具里的权威目录必须跟着唯一声明走。**
    #     本机 `SELFBUILT_CONFIG=Release`（`src/Linux/build/SelfBuiltConfig.props:28`），而夹具原先把沙箱权威一律摆到
    #     `…/bin/Debug` ⇒ `ITEMS` 去 `…/bin/Release` 找 ⇒ **假红**：实测（W52C 2026-09-20，报 E 段同一摆法）
    #     改前件（6 件口径）= `AUTH-MISSING=4`、本件（8 件口径）= `AUTH-MISSING=6` ⇒ **两趟都 rc≠0**，
    #     而 E/O 断言的是 `AUTH-MISSING=0` ⇒ 那两条**改前就已经是红的**（`#39` 切配置之后没人重跑过 `--selftest`）。
    #     所以下面**只改夹具的摆法**：权威目录一律写成 `bin/$SELFBUILT_CONFIG`（判据、计数器、exit 口径一字未改）。
    echo "=== 自检 E：权威件换 sha ⇒ 必须报红；还原 ⇒ 必须回绿（两极化）==="
    mkdir -p "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG" \
             "$tmp/authB/src/WpfGfx.Linux.Native/bin" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0" \
             "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG" \
             "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG" \
             "$tmp/hostE/App/bin/Debug" "$tmp/hostE/prov/bin/Debug"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/hostE/prov/Prov.csproj"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/hostE" "$PROV" > "$tmp/hostE/App/App.csproj"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/hostE/prov/bin/Debug/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/"
    cp "$REPO/src/WpfGfx.Linux.Native/bin/libwpfwin32.so"    "$tmp/authB/src/WpfGfx.Linux.Native/bin/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll"             "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/PresentationCore.dll"         "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/PresentationFramework.dll" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/WindowsBase.dll"                     "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/"
    touch "$tmp/hostE/App/bin/Debug/App.runtimeconfig.json"
    cp "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/hostE/App/bin/Debug/$PROV"
    outE1="$(SCAN_ROOTS="$tmp/hostE" HINTPATH_ROOTS="$tmp/hostE" AUTH_ROOT="$tmp/authB" "$0")"; rcE1=$?
    head -c 2048 "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" > "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/x.tmp" \
        && mv -f "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/x.tmp" "$tmp/authB/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"
    outE2="$(SCAN_ROOTS="$tmp/hostE" HINTPATH_ROOTS="$tmp/hostE" AUTH_ROOT="$tmp/authB" "$0")"; rcE2=$?
    if [ "$rcE1" = 0 ] && [ "$rcE2" != 0 ] && grep -qE 'STALE|NEWER-DIFF' <<<"$outE2" \
       && grep -q 'APPSYNC=MISMATCH' <<<"$outE2" \
       && grep -q 'AUTH-MISSING=0' <<<"$outE1" && grep -q 'AUTH-MISSING=0' <<<"$outE2"; then
        echo "SELFTEST_E=PASS（权威同 sha ⇒ exit=$rcE1 绿；权威换 sha ⇒ exit=$rcE2 红；两趟 AUTH-MISSING=0 ⇒ 沙箱八件权威俱全，目录随 SELFBUILT_CONFIG=$SELFBUILT_CONFIG）"
        printf '%s\n' "$outE2" | grep -E 'STALE|NEWER-DIFF|APPSYNC=' | head -2
    else echo "SELFTEST_E=FAIL（rc_同sha=$rcE1 rc_换sha=$rcE2）"; printf '%s\n' "$outE2" | tail -6; exit 1; fi

    # ── F：桩件（5,120 B 同名）**不得**被判成不一致 ──
    echo "=== 自检 F：循环桩里的同名件不得判成不一致 ==="
    mkdir -p "$tmp/stub/CycleStub.Foo.Linux/bin/Debug" "$tmp/stub/App/bin/Debug" "$tmp/stub/prov/bin/Debug"
    touch "$tmp/stub/App/bin/Debug/App.runtimeconfig.json"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/stub/prov/Prov.csproj"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/stub" "$PROV" > "$tmp/stub/App/App.csproj"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/stub/prov/bin/Debug/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/stub/App/bin/Debug/$PROV"
    head -c 5120 /dev/zero > "$tmp/stub/CycleStub.Foo.Linux/bin/Debug/$PROV"
    outF="$(SCAN_ROOTS="$tmp/stub" HINTPATH_ROOTS="$tmp/stub" AUTH_ROOT="$PREV_AUTH" "$0")"; rcF=$?
    if grep -q 'SKIP(stub)' <<<"$outF" && grep -q 'APPSYNC=PASS' <<<"$outF" && [ "$rcF" = 0 ]; then
        echo "SELFTEST_F=PASS（桩件被 SKIP(stub) 打出来但不判不一致，exit=$rcF）"
        printf '%s\n' "$outF" | grep -E 'SKIP\(stub\)' | head -1
    else echo "SELFTEST_F=FAIL（rc=$rcF）"; printf '%s\n' "$outF" | tail -6; exit 1; fi

    # ── G：obj 中间件不判定，但**同内容的 stale bin 副本必须仍红**（防"放宽后变瞎"）──
    echo "=== 自检 G：obj 不判定，但 stale 的 bin 副本必须仍被判红 ==="
    mkdir -p "$tmp/og/Probe/bin/Debug" "$tmp/og/Probe/obj/Debug" "$tmp/og/prov/bin/Debug"
    touch "$tmp/og/Probe/bin/Debug/Probe.runtimeconfig.json"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/og/prov/Prov.csproj"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/og" "$PROV" > "$tmp/og/Probe/Probe.csproj"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/og/prov/bin/Debug/$PROV"
    head -c 4096 "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" > "$tmp/og/Probe/bin/Debug/$PROV"
    cp "$tmp/og/Probe/bin/Debug/$PROV" "$tmp/og/Probe/obj/Debug/$PROV"
    touch -d '2020-01-01' "$tmp/og/Probe/bin/Debug/$PROV"     # 明确"早于权威"
    outG="$(SCAN_ROOTS="$tmp/og" HINTPATH_ROOTS="$tmp/og" AUTH_ROOT="$PREV_AUTH" "$0")"; rcG=$?
    if grep -q 'SKIP(obj)' <<<"$outG" && grep -qE 'STALE|NEWER-DIFF' <<<"$outG" \
       && grep -q 'APPSYNC=MISMATCH' <<<"$outG" && [ "$rcG" != 0 ]; then
        echo "SELFTEST_G=PASS（obj 只 SKIP；bin 的落后副本仍报红、exit=$rcG）"
        printf '%s\n' "$outG" | grep -E 'SKIP\(obj\)|STALE' | head -2
    else echo "SELFTEST_G=FAIL（rc=$rcG）"; printf '%s\n' "$outG" | tail -6; exit 1; fi

    # ── H：库输出目录（无 runtimeconfig）不判定；同一份副本放进宿主目录必须被判定 ──
    echo "=== 自检 H：库输出目录不判定 / 同一份副本在宿主目录里必须被判定 ==="
    mkdir -p "$tmp/h/Nope/bin/Debug" "$tmp/h/App/bin/Debug"
    touch "$tmp/h/App/bin/Debug/App.runtimeconfig.json"
    head -c 4096 "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" > "$tmp/h/Nope/bin/Debug/$PROV"
    cp "$tmp/h/Nope/bin/Debug/$PROV" "$tmp/h/App/bin/Debug/$PROV"
    outH="$(SCAN_ROOTS="$tmp/h" AUTH_ROOT="$PREV_AUTH" "$0")"; rcH=$?
    n_lib=$(printf '%s' "$outH" | grep -c '^    LIB-COPY'); n_judged=$(printf '%s' "$outH" | grep -cE '^    (STALE|NEWER-DIFF|MISMATCH|UNEXPECTED)')
    if [ "$n_lib" = 1 ] && [ "$n_judged" -ge 1 ] && [ "$rcH" != 0 ]; then
        echo "SELFTEST_H=PASS（库输出 1 份 LIB-COPY 不判；宿主里那份被判定、exit=$rcH）"
        printf '%s\n' "$outH" | grep -E '^    (LIB-COPY|STALE|NEWER-DIFF)' | head -2
    else echo "SELFTEST_H=FAIL（LIB-COPY=$n_lib judged=$n_judged rc=$rcH）"; printf '%s\n' "$outH" | tail -6; exit 1; fi

    # ── I：`-ipath` 翻正**自己的牙**：小写 `release/` 目录也必须进分组 ──
    echo "=== 自检 I：小写 release/ 目录进入分组（-ipath 翻正的牙）==="
    mkdir -p "$tmp/lc/ProbeA/bin/release" "$tmp/lc/ProbeB/bin/release"
    touch "$tmp/lc/ProbeA/bin/release/A.runtimeconfig.json" "$tmp/lc/ProbeB/bin/release/B.runtimeconfig.json"
    cp "$tmp/good/P/bin/Debug/$PROV" "$tmp/lc/ProbeA/bin/release/$PROV"
    cp "$tmp/bad/P/bin/Debug/$PROV"  "$tmp/lc/ProbeB/bin/release/$PROV"
    outI="$(SCAN_ROOTS="$tmp/lc" AUTH_ROOT="$PREV_AUTH" "$0")"; rcI=$?
    if grep -qE "DIVERGENT +DirectWrite.Linux.Provider.dll \[Release\]" <<<"$outI" \
       && grep -q "APPSYNC=MISMATCH" <<<"$outI" && [ "$rcI" != 0 ]; then
        echo "SELFTEST_I=PASS（小写 release/ 的落单副本被抓出，exit=$rcI）"
        printf '%s\n' "$outI" | grep -E "DIVERGENT|ProbeB" | head -3
    else echo "SELFTEST_I=FAIL（rc=$rcI）"; printf '%s\n' "$outI" | tail -6; exit 1; fi

    # ── J：**解析源**（主控 2026-09-14 派修）：非宿主目录 + 被 csproj 的 HintPath 引用 ⇒ 必须判定；
    #      同一份副本**没有** HintPath 指向时 ⇒ 必须仍是 LIB-COPY（两极化，证明是 HintPath 在起作用）──
    echo "=== 自检 J：HintPath 解析源目录必须判定（无 HintPath 时仍是 LIB-COPY）==="
    mkdir -p "$tmp/j/refbin/bin/Debug" "$tmp/j/consumer" "$tmp/j/noHint/srcbin/bin/Debug"
    head -c 4096 "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" > "$tmp/j/refbin/bin/Debug/$PROV"
    touch -d '2020-01-01' "$tmp/j/refbin/bin/Debug/$PROV"
    cp "$tmp/j/refbin/bin/Debug/$PROV" "$tmp/j/noHint/srcbin/bin/Debug/$PROV"
    touch -d '2020-01-01' "$tmp/j/noHint/srcbin/bin/Debug/$PROV"
    printf '<Project><ItemGroup><Reference Include="X"><HintPath>%s</HintPath></Reference></ItemGroup></Project>\n' \
        "$tmp/j/refbin/bin/Debug/$PROV" > "$tmp/j/consumer/consumer.csproj"
    outJ1="$(SCAN_ROOTS="$tmp/j/refbin" HINTPATH_ROOTS="$tmp/j" AUTH_ROOT="$PREV_AUTH" "$0")"; rcJ1=$?
    outJ2="$(SCAN_ROOTS="$tmp/j/noHint" HINTPATH_ROOTS="$tmp/j" AUTH_ROOT="$PREV_AUTH" "$0")"; rcJ2=$?
    if grep -q "HintPath 引用" <<<"$outJ1" && grep -qE "STALE|NEWER-DIFF" <<<"$outJ1" && [ "$rcJ1" != 0 ] \
       && grep -q "LIB-COPY" <<<"$outJ2" && [ "$rcJ2" = 0 ]; then
        echo "SELFTEST_J=PASS（有 HintPath ⇒ 判定并报红 exit=$rcJ1；无 HintPath ⇒ LIB-COPY 不判 exit=$rcJ2）"
        printf '%s\n' "$outJ1" | grep -E "STALE|NEWER-DIFF" | head -1
    else echo "SELFTEST_J=FAIL（rc_有=$rcJ1 rc_无=$rcJ2）"; printf '%s\n' "$outJ1" | tail -4; printf '%s\n' "$outJ2" | tail -4; exit 1; fi

    # ── K：**期望集合**（权威枚举）——① 基数不依赖现场文件 ② 删一份 ⇒ MISSING 红并点名 ③ 整份还原 ⇒ 回绿 ──
    echo "=== 自检 K：期望集合的基数不变性 + 删一份必须 MISSING 红 + 还原回绿 ==="
    mkdir -p "$tmp/K/prov/bin/Debug" "$tmp/K/app/bin/Debug"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/K/prov/Prov.csproj"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/K/prov/bin/Debug/$PROV"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/K" "$PROV" > "$tmp/K/app/App.csproj"
    cp "$tmp/K/prov/bin/Debug/$PROV" "$tmp/K/app/bin/Debug/$PROV"
    kExp() { printf '%s' "$1" | sed -n 's/.*期望副本=\([0-9]*\).*/\1/p' | head -1; }
    outK1="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcK1=$?; n1="$(kExp "$outK1")"
    mv "$tmp/K/app/bin/Debug/$PROV" "$tmp/K.app.bak"
    outK2="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcK2=$?; n2="$(kExp "$outK2")"
    cp "$tmp/K.app.bak" "$tmp/K/app/bin/Debug/$PROV"          # 纪律：整份还原，不做"反向替换"
    outK3="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcK3=$?
    if [ -n "$n1" ] && [ "$n1" = "$n2" ] && grep -q MISSING <<<"$outK2" && [ "$rcK2" != 0 ] \
       && [ "$rcK1" = 0 ] && [ "$rcK3" = 0 ]; then
        echo "SELFTEST_K=PASS（期望基数 删前=$n1 / 删后=$n2 **不变**；删⇒MISSING 红 exit=$rcK2；整份还原⇒绿 exit=$rcK3）"
        printf '%s\n' "$outK2" | grep MISSING | head -1
    else echo "SELFTEST_K=FAIL（n1=$n1 n2=$n2 rc=$rcK1/$rcK2/$rcK3）"; printf '%s\n' "$outK2" | tail -5; exit 1; fi

    # ── L：**多余副本** ⇒ UNEXPECTED（且是红），不是静默忽略 ──
    echo "=== 自检 L：期望之外多一份 ⇒ UNEXPECTED（红）==="
    touch "$tmp/K/app/bin/Debug/App.runtimeconfig.json"                 # 让它成为"启动宿主"⇒ 参与判定
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll" "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    outL="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcL=$?
    rm -f "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    if grep -q UNEXPECTED <<<"$outL" && grep -q "APPSYNC=MISMATCH" <<<"$outL" && [ "$rcL" != 0 ]; then
        echo "SELFTEST_L=PASS（多余副本被报 UNEXPECTED 且计红 exit=$rcL）"
        printf '%s\n' "$outL" | grep UNEXPECTED | head -1
    else echo "SELFTEST_L=FAIL（rc=$rcL）"; printf '%s\n' "$outL" | tail -5; exit 1; fi

    # ── L v2：**同一份多余副本，内容 == 权威** ⇒ 必须报 `UNEXPECTED-EQ`（`DECL-GAP-EQ`）**且仍然红** ──
    #    `D-A1` 的现场形态：副本"不在声明图里"，但内容恰好等于权威 ⇒ **不是陈旧件，也不许当绿**。
    echo "=== 自检 L v2（D-A1 加固）：多余副本但 **sha==权威** ⇒ UNEXPECTED-EQ（DECL-GAP-EQ）+ 仍然红 ==="
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll" "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    outL2="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcL2=$?
    rm -f "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    if grep -q 'UNEXPECTED-EQ' <<<"$outL2" \
       && grep -q 'UNEXPECTED=1\[DECL-GAP-EQ=1 DECL-GAP-DIFF=0\]' <<<"$outL2" \
       && grep -q 'APPSYNC=MISMATCH' <<<"$outL2" && [ "$rcL2" != 0 ]; then
        echo "SELFTEST_L2=PASS（内容==权威的未声明副本被**具名**报出且仍然红 exit=$rcL2 —— 不许洗绿）"
        printf '%s\n' "$outL2" | grep -E 'UNEXPECTED-EQ|计数：' | head -2
    else echo "SELFTEST_L2=FAIL（rc=$rcL2）"; printf '%s\n' "$outL2" | tail -5; exit 1; fi

    # ── N：**内容 ≠ 权威**的未声明副本 ⇒ 必须报 `UNEXPECTED-DIFF`（`DECL-GAP-DIFF`）+ 硬红 ──
    echo "=== 自检 N（D-A1 加固）：同一位置换成 **sha≠权威** ⇒ UNEXPECTED-DIFF（DECL-GAP-DIFF）+ 硬红 ==="
    head -c 4096 "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll" > "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    outN="$(SCAN_ROOTS="$tmp/K" HINTPATH_ROOTS="$tmp/K" AUTH_ROOT="$PREV_AUTH" "$0")"; rcN=$?
    rm -f "$tmp/K/app/bin/Debug/WpfGfx.Linux.dll"
    if grep -q 'UNEXPECTED-DIFF' <<<"$outN" \
       && grep -q 'UNEXPECTED=1\[DECL-GAP-EQ=0 DECL-GAP-DIFF=1\]' <<<"$outN" \
       && grep -q 'APPSYNC=MISMATCH' <<<"$outN" && [ "$rcN" != 0 ]; then
        echo "SELFTEST_N=PASS（内容≠权威的未声明副本走 DIFF 类、硬失败 exit=$rcN）"
        printf '%s\n' "$outN" | grep -E 'UNEXPECTED-DIFF|计数：' | head -2
    else echo "SELFTEST_N=FAIL（rc=$rcN）"; printf '%s\n' "$outN" | tail -5; exit 1; fi

    # ── M：**仪器要主动承认自己看不见** —— summary/清单里必须出现"看不见的拷贝点"字样 ──
    #    【`D-G13`（W26C 2026-09-17）】**M 时刻的输入快照在这里被序列化**（`snapPre`/`snapPost` 夹住那次运行）：
    #      M2 原先不序列化任何东西，而是在 **M2 时刻重新算**一遍枚举器的自报，再拿 M 的**旧输出**去比 ⇒
    #      两次取数之间仓库只要变了（本仓常态：别的车道在写盘），M 的旧输出必然**缺**那句告诫 ⇒ 假红。
    #      夹逼的意义：`snapPre = snapPost` ⇒ "M 那次运行看到的就是这棵树"是**可核的**，不是推断。
    echo "=== 自检 M：输出里必须显式声明"看不见的拷贝点" ==="
    mSnap()  { python3 "$EXPECT_TOOL" "$PREV_AUTH" "$PREV_AUTH" 2>/dev/null | grep -m1 '^#SUMMARY|'; }
    mField() { printf '%s' "$1" | sed -n "s/.*|$2=\([0-9]*\).*/\1/p"; }
    snapPre="$(mSnap)"
    outM="$(AUTH_ROOT="$PREV_AUTH" "$0" 2>&1)"
    snapPost="$(mSnap)"
    #    ⚠️ 【W26C 实测的**第二个假红源**，与 `D-G13` 同族】这三条原来写成
    #      `printf '%s' "$outM" | grep -q PAT` —— 在 `set -o pipefail` 下，`grep -q` 命中即退出 ⇒
    #      `printf` 若还在分块写（本例 `$outM` 36 KB、多行）就吃 `SIGPIPE` ⇒ **管道整条非零** ⇒
    #      "串在、却判成不在" ⇒ `SELFTEST_M=FAIL`。**实测：三条合计假红 ~6.8%/次**（9+7+11 / 3×400）。
    #      改成 here-string（`grep -q PAT <<<"$outM"`）：无管道 ⇒ **0/400**；判据（要哪三条串）一字未改。
    if grep -q "本校验器看不见的拷贝点" <<<"$outM" \
       && grep -q "\[写点\]" <<<"$outM" && grep -q "不会被本校验器报出" <<<"$outM"; then
        echo "SELFTEST_M=PASS（summary + 写点清单 + "删除不会被报出"三件都在）"
        printf '%s\n' "$outM" | grep "本校验器看不见的拷贝点" | head -1
    else echo "SELFTEST_M=FAIL"; printf '%s\n' "$outM" | grep -i "看不见" | head -3; exit 1; fi

    # ── M2：**枚举器自己的两条边界必须自称**（TAPPS 2026-09-15）：截断（每文件 3 条只读）+ 只扫 src/Linux/build/**/*.sh ──
    #    判据**构造性**：*当* `invisible_capped>0` 就必须出现"不完整"字样；*当* 补扫有写点就必须出现"补扫"字样。
    #    （不给"必须恒 >0"的断言：那会在清单天然不超过 3 条/文件时变成假红。补扫写点今天恒 >0，因为它包含
    #      `#INVISIBLE-EXT` 的**存在性**断言 —— 若将来真的降到 0，这条会红，届时由人裁定"是有意为之"。）
    #    【`D-G13`（W26C）】三态改造（修的是**假红**，判据面**只加严不放松**）：
    #      · 同一棵树（`snapPre==snapPost==expM2` 的两个字段）⇒ 判**该次运行内部一致性** ⇒ PASS/FAIL；
    #      · 两个时刻的仓库不同 ⇒ `NOINFO`（**无信息，不是判据失败**）—— 这正是原来的假红形态；
    #      · 快照算不出来（枚举器不可用/无 `#SUMMARY`）⇒ `NOINFO`（同 `EXPECT_OK≠1` 的口径）。
    #      ⚠️ **加严的那一半**：以前只查"字样在不在"，**不查数字对不对** ⇒ "印了但印错数"能过。
    #         现在要求 `另有 **N 条只读点未印**` / `补扫的写点 **N 处**` 里的 N **逐位等于**枚举器自报值；
    #         并且 `capped=0`/`ext_write=0` 时**不许**出现对应告诫（自报与输出必须一致）。
    echo "=== 自检 M2：枚举器的截断与扫描范围边界必须自称 ==="
    expM2="$(python3 "$EXPECT_TOOL" "$PREV_AUTH" 2>/dev/null)"
    capM="$(mField "$snapPost" invisible_capped)";  extM="$(mField "$snapPost" invisible_ext_write)"
    capM2="$(mField "$expM2" invisible_capped)";    extM2="$(mField "$expM2" invisible_ext_write)"
    # M 那次运行的**输出**里自报的两个数（`补扫的写点 **N 处**` 恒印；`另有 **N 条只读点未印**` 只在 N>0 时印）
    extOut="$(printf '%s' "$outM" | sed -n 's/.*补扫的写点 \*\*\([0-9]*\) 处\*\*.*/\1/p' | head -1)"
    capOut="$(printf '%s' "$outM" | sed -n 's/.*另有 \*\*\([0-9]*\) 条只读点未印\*\*.*/\1/p' | head -1)"
    if [ -z "$snapPre" ] || [ -z "$snapPost" ] || [ -z "$capM" ] || [ -z "$extM" ] \
       || [ -z "$expM2" ] || [ -z "$capM2" ] || [ -z "$extM2" ]; then
        echo "SELFTEST_M2=NOINFO（参考快照算不出来：applocal-expect.py 不可用 / 无 #SUMMARY ⇒ **无信息**，不是判据失败）"
        SELFTEST_NOINFO=$((SELFTEST_NOINFO+1))
    elif [ "$snapPre" != "$snapPost" ]; then
        echo "SELFTEST_M2=NOINFO（M 那次运行的**窗口内**仓库就变了 ⇒ 那次运行看到的是哪一棵树不可知 ⇒ **无信息**）"
        SELFTEST_NOINFO=$((SELFTEST_NOINFO+1))
    elif [ "$capM" != "$capM2" ] || [ "$extM" != "$extM2" ]; then
        echo "SELFTEST_M2=NOINFO（**两次取数之间仓库变了** ⇒ **无信息**：M 时刻 capped=$capM ext_write=$extM ｜ M2 时刻 capped=$capM2 ext_write=$extM2；判据本身没有失败）"
        SELFTEST_NOINFO=$((SELFTEST_NOINFO+1))
    else
        okM2=1; whyM2=""
        # ⚠️ 本节自己的四条断言一律用 here-string（`<<<`）而**不是** `printf|grep -q`：
        #   后者在 `set -o pipefail` 下会因 `SIGPIPE` 假红（见上面 M 段的实测），`$outM` 有 36 KB。
        grep -q '^#INVISIBLE-EXT|write|' <<<"$expM2" || { okM2=0; whyM2="#INVISIBLE-EXT 写点一条都没有"; }
        if [ "$capM" = 0 ]; then
            [ -z "$capOut" ] || { okM2=0; whyM2="${whyM2:+$whyM2；}自报 capped=0 却印了截断告诫（**$capOut 条**）"; }
        else
            grep -q '只读清单\*\*不完整\*\*' <<<"$outM" || { okM2=0; whyM2="${whyM2:+$whyM2；}自报 capped=$capM 却没印「只读清单**不完整**」"; }
            [ "$capOut" = "$capM" ] || { okM2=0; whyM2="${whyM2:+$whyM2；}截断数不符：输出自报 [$capOut] ≠ 枚举器 [$capM]"; }
        fi
        if [ "$extM" = 0 ]; then
            [ -z "$extOut" ] || { okM2=0; whyM2="${whyM2:+$whyM2；}自报 ext_write=0 却印了补扫告诫（**$extOut 处**）"; }
        else
            grep -q '补扫的写点' <<<"$outM" || { okM2=0; whyM2="${whyM2:+$whyM2；}自报 ext_write=$extM 却没印「补扫的写点」"; }
            [ "$extOut" = "$extM" ] || { okM2=0; whyM2="${whyM2:+$whyM2；}补扫数不符：输出自报 [$extOut] ≠ 枚举器 [$extM]"; }
        fi
        if [ "$okM2" = 1 ]; then
            echo "SELFTEST_M2=PASS（同一棵树（snapPre==snapPost==expM2）：invisible_capped=$capM ⇒ 截断自称**且数字相符**；invisible_ext_write=$extM ⇒ 补扫自称且逐条印）"
            printf '%s\n' "$outM" | grep -E '只读清单|补扫的写点' | head -2
        else
            echo "SELFTEST_M2=FAIL（**同一棵树**（capped=$capM ext_write=$extM）而自称缺失/数字不符：$whyM2）"
            printf '%s\n' "$outM" | tail -6; exit 1
        fi
    fi

    # ── O（`D-G8`，W24D 2026-09-17）：**"某个权威件整份不见"必须能让 rc 非 0** ──
    #    这一段是 `D-G8` 的**永久反极性**：构造一个"八件权威俱全 + 一个全绿宿主副本"的沙箱（⇒ rc=0），
    #    再把**其中一件**（沙箱内**没有任何副本**的 `ReachFramework.dll`）移走 ⇒
    #      · `scan()` 内部 rc=1（**改前** `:212`／本版 `:226`）、**五个具名计数器一个都不动**（没有副本 ⇒ 没有副本级读数）；
    #      · **修复前**：脚本照样印 `APPSYNC=PASS` + `exit 0` ⇒ 这正是"权威整份不见仍被读成一致"的现场；
    #      · **修复后**：`AUTH-MISSING=1` 具名 + rc≠0（下面同时断言"判定计数器逐字未动"⇒ 红**只**来自新信号）。
    #    第三趟 `cp -p` 整份还原 ⇒ 必须回 rc=0（同一命令形态、同一沙箱）。
    echo "=== 自检 O（D-G8）：权威件整份不见 ⇒ 必须 rc≠0（修复前这里印 PASS）==="
    mkdir -p "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG" \
             "$tmp/authO/src/WpfGfx.Linux.Native/bin" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0" \
             "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG" \
             "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG" \
             "$tmp/hostO/App/bin/Debug" "$tmp/hostO/prov/bin/Debug"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so"          "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/"
    cp "$REPO/src/WpfGfx.Linux.Native/bin/libwpfwin32.so"            "$tmp/authO/src/WpfGfx.Linux.Native/bin/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll"   "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"      "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll"     "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/PresentationCore.dll" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/PresentationFramework.dll" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/WindowsBase.dll"                     "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>DirectWrite.Linux.Provider</AssemblyName><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup></Project>\n' > "$tmp/hostO/prov/Prov.csproj"
    printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath></PropertyGroup><ItemGroup><Reference Include="DirectWrite.Linux.Provider"><HintPath>%s/prov/bin/Debug/%s</HintPath><Private>true</Private></Reference></ItemGroup></Project>\n' "$tmp/hostO" "$PROV" > "$tmp/hostO/App/App.csproj"
    touch "$tmp/hostO/App/bin/Debug/App.runtimeconfig.json"
    cp "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/hostO/prov/bin/Debug/$PROV"
    cp "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV" "$tmp/hostO/App/bin/Debug/$PROV"
    oRun() { SCAN_ROOTS="$tmp/hostO" HINTPATH_ROOTS="$tmp/hostO" AUTH_ROOT="$tmp/authO" "$0"; }
    oCnt() { printf '%s' "$1" | grep -m1 '^计数：' | sed 's/  AUTH-MISSING=[0-9]*//'; }   # **剔掉新信号**再比 ⇒ 其余计数器必须逐字未动
    outO1="$(oRun)"; rcO1=$?
    mv "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll" "$tmp/O.reach.away"
    outO2="$(oRun)"; rcO2=$?
    cp -p "$tmp/O.reach.away" "$tmp/authO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll"
    outO3="$(oRun)"; rcO3=$?
    if [ "$rcO1" = 0 ] && [ "$rcO3" = 0 ] \
       && grep -q 'APPSYNC=PASS' <<<"$outO1" && grep -q 'AUTH-MISSING=0' <<<"$outO1" \
       && [ "$rcO2" != 0 ] && grep -q 'AUTH-MISSING=1' <<<"$outO2" \
       && grep -q '权威件缺失' <<<"$outO2" && grep -q 'APPSYNC=MISMATCH' <<<"$outO2" \
       && grep -q 'APPSYNC=PASS' <<<"$outO3" && grep -q 'AUTH-MISSING=0' <<<"$outO3" \
       && [ "$(oCnt "$outO1")" = "$(oCnt "$outO2")" ]; then
        echo "SELFTEST_O=PASS（权威俱全 exit=$rcO1 绿；移走一份权威 exit=$rcO2 红[AUTH-MISSING=1]；cp -p 还原 exit=$rcO3 绿；判定计数器两趟逐字相同 ⇒ 红只来自该新信号）"
        printf '%s\n' "$outO2" | grep -E 'AUTH-MISSING|APPSYNC=' | head -2
    else echo "SELFTEST_O=FAIL（rc1=$rcO1 rc2=$rcO2 rc3=$rcO3）"; printf '%s\n' "$outO2" | grep -E 'AUTH-MISSING|计数：|APPSYNC=' | head -3; exit 1; fi

    # ── P（`D-A2-r`，W25B 2026-09-17）：桥的**绝对锚**判据（⑦）——"两份副本一起换旧仍静默通过"必须被抓住 ──
    #    形态：沙箱里造**真形态**的发布布局（发布目录 + `.artifacts/bin/.../native` 两份副本 + 发布记录），
    #    并把**八件其它权威**也造齐（照 E/O 先例 ⇒ `AUTH-MISSING=0`，否则这一条会变成假红）。
    #    记录**由沙箱自己合成**、锚值取自**真桥的 sha** ⇒ 本自检不因"换波后桥/记录变了"而假红。
    #    关键：P2 把**两份换成同一个**其它内容（⇒ 跨副本分组仍然 CONSISTENT ⇒ **没有** DIVERGENT 兜底，
    #    这正是 `D-A2-r` 的现场：改前这条路上两份副本连一个读数都不产生）。
    echo "=== 自检 P（D-A2-r）：桥副本 vs 发布记录 BRIDGE_SO_SHA256 ⇒ 一起换旧必须红、NOINFO 不许当绿 ==="
    PREPO="$tmp/P/repo"
    PPUB="$PREPO/src/Linux/build/MilBridge/.artifacts/publish/MilBridge.Linux/release_linux-x64"
    PNAT="$PREPO/src/Linux/build/MilBridge/.artifacts/bin/MilBridge.Linux/release_linux-x64/native"
    mkdir -p "$PPUB" "$PNAT" "$tmp/P/host/bin/Debug" "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim" \
             "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG" "$PREPO/src/WpfGfx.Linux.Native/bin" \
             "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0" "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG" \
             "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG" \
             "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG" "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so"               "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/"
    # 【`#49` `C1`（W52C 2026-09-20）：沙箱必须**自带配置声明**，否则这一条会因"两侧读的声明不是同一份"而假红】
    #   两条 ITEMS 表读配置的位置**不同**（实测，见报告 §2.4）：
    #     · `check-applocal-sync.sh` 读的是**脚本相对**的 `src/Linux/build/SelfBuiltConfig.props`（⇒ 真仓的 Release）；
    #     · `applocal-expect.py::_selfbuilt_config()` 读的是 **argv[1]（= AUTH_ROOT）相对**的
    #       `<AUTH_ROOT>/build/SelfBuiltConfig.props`，**读不到就静默回退 "Debug"**（本文件 `:52-54`）。
    #   ⇒ 在 `$PREPO` 这种**合成权威根**里，判据侧拿 Release 权威、期望集合侧算 Debug 路径 ⇒
    #     `MISSING=5`（5 条"权威件本身的目录"期望落在 `…/bin/Debug`）⇒ `rc≠0` ⇒ 本段变**假红**
    #     （实测：`#49` `C1` 纳入 PF/WB 之前它**恰好**不红 —— 因为那时夹具把权威也摆在 `bin/Debug`，
    #       两个错**互相抵消**；那是巧合，不是一致）。
    #   夹具修法 = 把**同一份声明**复制进沙箱（真仓也有这个文件 ⇒ 沙箱更像"真形态"）；判据与断言一字未改。
    cp "$REPO/src/Linux/build/SelfBuiltConfig.props" "$PREPO/src/Linux/build/SelfBuiltConfig.props"
    cp "$REPO/src/WpfGfx.Linux.Native/bin/libwpfwin32.so"                  "$PREPO/src/WpfGfx.Linux.Native/bin/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/WpfGfx.Linux.dll"         "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/WpfGfx/bin/$SELFBUILT_CONFIG/net10.0/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"            "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/Provider/bin/$SELFBUILT_CONFIG/$PROV"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/ReachFramework.dll"     "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/PresentationCore.dll" "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/PresentationFramework.dll" "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationFramework/bin/$SELFBUILT_CONFIG/"
    cp "$REPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/WindowsBase.dll"                     "$PREPO/src/Microsoft.DotNet.Wpf.Linux/src/WindowsBase/bin/$SELFBUILT_CONFIG/"
    PREAL="$REPO/src/Linux/build/MilBridge/.artifacts/publish/MilBridge.Linux/release_linux-x64/wpfgfx_cor3.so"
    cp -p "$PREAL" "$tmp/P.good.so"                        # 正本（`cp -p` 还原用）
    cp -p "$tmp/P.good.so" "$PPUB/wpfgfx_cor3.so"
    cp -p "$tmp/P.good.so" "$PNAT/wpfgfx_cor3.so"
    head -c 262144 "$PREAL" > "$tmp/P.other.so"            # "另一个二进制"（不是记录里那一份）
    PGOOD="$(sha256sum "$PPUB/wpfgfx_cor3.so" | awk '{print $1}')"
    pRec() { { echo "BRIDGE_SRC_FP=<selftest 合成>"; [ -n "${1:-}" ] && echo "BRIDGE_SO_SHA256=$1"; echo "PUBLISHED_AT=2026-01-01T00:00:00+08:00"; } > "$PPUB/bridge-src-fp.txt"; }
    pRec "$PGOOD"
    pRun() { SCAN_ROOTS="$PREPO/src/Linux/build" HINTPATH_ROOTS="$PREPO" AUTH_ROOT="$PREPO" "$0"; }
    pL()   { printf '%s' "$1" | grep -m1 '^计数：'; }                      # 只取摘要行 ⇒ 断言不打偏
    pLold(){ pL "$1" | sed -e 's/  BRIDGE-ANCHOR=[0-9]*//' -e 's/  BRIDGE-NOINFO=[0-9]*//'; }
    pN()   { printf '%s' "$1" | grep -c "$2"; }
    pOut1="$(pRun)"; pRc1=$?
    pL1="$(pL "$pOut1")"
    cp -f "$tmp/P.other.so" "$PPUB/wpfgfx_cor3.so"          # ① **两份一起换**（同一个内容 ⇒ 跨副本仍 CONSISTENT）
    cp -f "$tmp/P.other.so" "$PNAT/wpfgfx_cor3.so"
    pOut2="$(pRun)"; pRc2=$?
    pL2="$(pL "$pOut2")"
    cp -p "$tmp/P.good.so" "$PPUB/wpfgfx_cor3.so"           # ② 只整份还原一份
    pOut3="$(pRun)"; pRc3=$?
    pL3="$(pL "$pOut3")"
    cp -p "$tmp/P.good.so" "$PNAT/wpfgfx_cor3.so"           # ③ 两份都还原（`cp -p`）
    pOut4="$(pRun)"; pRc4=$?
    pL4="$(pL "$pOut4")"
    pRec ""                                                 # ④ 记录里删掉锚行 ⇒ NOINFO
    pOut5="$(pRun)"; pRc5=$?
    pL5="$(pL "$pOut5")"
    pRec "$PGOOD"                                           # ⑤ 还原记录
    pOut6="$(pRun)"; pRc6=$?
    pL6="$(pL "$pOut6")"
    if [ "$pRc1" = 0 ] && grep -q 'AUTH-MISSING=0  BRIDGE-ANCHOR=0  BRIDGE-NOINFO=0' <<<"$pL1" \
       && [ "$(pN "$pOut1" '^    ANCHOR-OK')" = 2 ] \
       && [ "$pRc2" != 0 ] && grep -q 'AUTH-MISSING=0  BRIDGE-ANCHOR=2  BRIDGE-NOINFO=0' <<<"$pL2" \
       && [ "$(pN "$pOut2" '^    ANCHOR-DIFF')" = 2 ] && grep -q 'APPSYNC=MISMATCH' <<<"$pOut2" \
       && [ "$pRc3" != 0 ] && grep -q 'AUTH-MISSING=0  BRIDGE-ANCHOR=1  BRIDGE-NOINFO=0' <<<"$pL3" \
       && [ "$(pN "$pOut3" '^    ANCHOR-OK')" = 1 ] \
       && [ "$pRc4" = 0 ] && grep -q 'BRIDGE-ANCHOR=0  BRIDGE-NOINFO=0' <<<"$pL4" \
       && [ "$pRc5" != 0 ] && grep -q 'BRIDGE-ANCHOR=0  BRIDGE-NOINFO=1' <<<"$pL5" \
       && grep -q 'BRIDGE-NOINFO' <<<"$pOut5" && grep -q 'APPSYNC=MISMATCH' <<<"$pOut5" \
       && [ "$pRc6" = 0 ] && grep -q 'BRIDGE-ANCHOR=0  BRIDGE-NOINFO=0' <<<"$pL6" \
       && [ "$(pLold "$pOut1")" = "$(pLold "$pOut2")" ] && [ "$(pLold "$pOut1")" = "$(pLold "$pOut5")" ]; then
        echo "SELFTEST_P=PASS（锚一致 exit=$pRc1 绿｜两份一起换旧 exit=$pRc2 红[BRIDGE-ANCHOR=2]｜只还原一份 exit=$pRc3 红[=1]｜整份还原 exit=$pRc4 绿｜记录删锚行 exit=$pRc5 红[BRIDGE-NOINFO=1]；判定计数器三趟逐字相同 ⇒ 红**只**来自 ⑦）"
        printf '%s\n' "$pOut2" | grep -E '^    ANCHOR-DIFF|BRIDGE-ANCHOR=2' | head -3
    else
        echo "SELFTEST_P=FAIL（rc=$pRc1/$pRc2/$pRc3/$pRc4/$pRc5/$pRc6）"
        printf '%s\n' "$pL1" "$pL2" "$pL3" "$pL4" "$pL5" | sed 's/^/         /'
        printf '%s\n' "$pOut2" | tail -6; exit 1
    fi

    # 【`D-G13`（W26C）】**`rc=0` 只在全 PASS 时给出**：子例 `NOINFO` ⇒ `SELFTEST=NOINFO` + rc=3
    #   （"仪器未能自证"**不等于通过**，也不与 `FAIL` 的 rc=1 混为一谈 —— 后者是"判据失败"）。
    #   FAIL 在各自的分支里**已经** `exit 1`（上面一个都没改成 NOINFO）。
    if [ "${SELFTEST_NOINFO:-0}" = 0 ]; then
        echo "SELFTEST=PASS"; exit 0
    fi
    echo "SELFTEST=NOINFO（$SELFTEST_NOINFO 例为 NOINFO ⇒ **仪器未能自证**：无信息 ≠ 通过（**不给 rc=0**），也 ≠ 判据失败（**不混进 rc=1**）；逐例见上）"
    exit 3
fi

echo "权威根：$REPO"; echo "扫描根：$SCAN_ROOTS"
echo "解析源目录（csproj 的 HintPath 指向的输出目录）：${#REFDIR[@]} 个；未能解析的 HintPath：$HINTPATH_UNRESOLVED 条（登记，不静默）"
echo
scan; scanrc=$?
# 【`D-G8`（W24D）】`scan()` 的内部 rc **必须被消费**：它以前只被赋值、从不被读 ⇒ "权威整份不见"这类
#   没有计数器覆盖的失败会被丢弃。下面这行把它**印出来**（可核），文件尾把它**接进判定**。
printf 'scan() 内部 rc=%s（本值现在**参与**最终判定：rc≠0 而五个计数器全为 0 ⇒ 不再印 PASS）\n' "$scanrc"
# 【⑦ 调用点（W25B）】必须在下面 `计数：` 摘要行**之前**（否则摘要格恒 0 ⇒ "红了但读不出来"）。
bridge_anchor_check
echo
echo "计数：OK=$CNT_OK  MISMATCH=$CNT_MISMATCH（STALE=$CNT_STALE  NEWER-DIFF=$CNT_NEWER）  MISSING=$CNT_MISSING  UNEXPECTED=$CNT_UNEXPECTED[DECL-GAP-EQ=$CNT_DECLGAP_EQ DECL-GAP-DIFF=$CNT_DECLGAP_DIFF]  DIVERGENT=$CNT_DIVERGENT  CROSS-CONFIG=$CNT_CROSSCFG  LIB-COPY=$CNT_LIBCOPY  SKIP(obj)=$CNT_SKIPOBJ  SKIP(stub)=$CNT_SKIPSTUB  SKIP(ref)=$CNT_SKIPREF  RETIRED=$CNT_RETIRED  AUTH-MISSING=$CNT_AUTHMISS  BRIDGE-ANCHOR=$CNT_BRIDGE_ANCHOR  BRIDGE-NOINFO=$CNT_BRIDGE_NOINFO"
echo "        （CROSS-CONFIG（D-G62）= **副本所在路径的配置 ≠ 声明配置（$SELFBUILT_CONFIG）** 的份数：**只增加可见性** —— 不判红、也**不给任何副本免红**（那些副本照样在 MISMATCH/UNEXPECTED 里逐条出现）。旧格 NO-AUTHORITY 已删：它是死格，且旧行为是**豁免**）"
echo "        （桥的后两格是 ⑦ 的**绝对锚**判据：BRIDGE-ANCHOR = 副本 sha ≠ 发布记录 BRIDGE_SO_SHA256 的份数；BRIDGE-NOINFO = 记录缺失/解析不出/本次扫描根内 0 份桥副本 —— **两者都红**）"
echo "        （UNEXPECTED 的两个子类：DECL-GAP-EQ = 不在声明图里但 **sha==权威**（未声明的传递依赖副本，仍然红）｜DECL-GAP-DIFF = 不在声明图里且 **sha≠权威**（硬失败）；两者都计进 UNEXPECTED）"
# 【在册红（用户 2026-09-16 裁决；`D-A2`/W23C）】本检查器**没有**既有的"在册红"机制（退出码只有
#   0=PASS / 1=MISMATCH / 3=NOINFO）。用户裁决 = **不许发明"登记就变绿"的机制**（那等于把未登记红
#   压成绿），改为 **书面登记 + 逐条点名**：
#     · 登记文件 `known-red-PC-copies.md`（同目录）；逐份记 路径 / 登记时副本 sha / 登记时权威 sha /
#       类别 / 首次登记日期 / 处置。
#     · 本段**只读只打印**：不改任何 `CNT_*`、不改 `rc`、不改上面任何一条判据、不改任何目录。
#     · 登记条目**照样计红**（`APPSYNC=MISMATCH` + `exit 1` 不变）⇒ **登记 ≠ 已容忍**。
REGISTRY="${REGISTRY_FILE:-$(dirname "$0")/known-red-PC-copies.md}"
# 【`#49` `C1`（W52C 2026-09-20）】**第二本册子**（PF/WB）。为什么需要第二本而不是并进 PC 那本：
#   PC 册的口径句、表头与"权威快照"都是**围绕 `PresentationCore.dll` 一份件**写的（它自己的读者约定），
#   把 34 份 PF/WB 塞进去会把"哪一份件的权威"读混。两本册子**同一条纪律**：**只读只打印、不参与判定**、
#   `登记 ≠ 已容忍`。`REGISTRY_FILE` 显式给了 ⇒ **只读那一本**（向后兼容：老调用方的行为逐字不变）。
REGISTRY2="$(dirname "$0")/known-red-PFWB-copies.md"
[ -n "${REGISTRY_FILE:-}" ] && REGISTRY2="$REGISTRY"     # 显式指定册子 ⇒ 只读那一本（向后兼容）
_reg_trim() { local s="$1"; s="${s#"${s%%[![:space:]]*}"}"; s="${s%"${s##*[![:space:]]}"}"; printf '%s' "$s"; }
show_registry() {
    local REGISTRY="${1:-$REGISTRY}"
    [ -f "$REGISTRY" ] || return 0
    local n=0 still=0 green=0 gone=0 path rsha aauth cls date plan cur nowauth off
    echo "--- 在册红（书面登记 ${REGISTRY#$REPO/}；**登记≠已容忍**：本段不参与判定，rc 仍由上面五个计数器决定）"
    while IFS='|' read -r _ path rsha aauth cls date plan _; do
        path="$(_reg_trim "$path")"; rsha="$(_reg_trim "$rsha")"; aauth="$(_reg_trim "$aauth")"
        cls="$(_reg_trim "$cls")"; date="$(_reg_trim "$date")"; plan="$(_reg_trim "$plan")"
        [ -n "$path" ] || continue
        [ "$path" = "路径" ] && continue                      # 表头
        case "$rsha" in *---*) continue;; esac                 # 分隔行
        # 只认"登记行"：第 2 列必须是 16 位小写 hex 的副本 sha16
        #   （登记文件里另有**汇总表**等 markdown 表 ⇒ 不加这条闸会把它们的行也当登记条目，
        #    实测踩过：6 条汇总行被读成"缺件" ⇒ 计数虚高、读起来像真的）
        case "$rsha" in
            [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;;
            *) continue;;
        esac
        n=$((n+1))
        off=""; in_scan_roots "$REPO/$path" || off="（**不在本次扫描根内** ⇒ 本次没枚举它）"
        if [ ! -f "$REPO/$path" ]; then
            gone=$((gone+1)); printf '    [在册红] %-70s **缺件**（登记时 %s；类别 %s）%s\n' "$path" "$rsha" "$cls" "$off"; continue
        fi
        cur="$(sha "$REPO/$path")"; nowauth="$(auth_sha_of "$(basename "$path")")"
        if [ "$nowauth" != "<no-authority>" ] && [ "$cur" = "$nowauth" ]; then
            green=$((green+1)); printf '    [在册红·已转绿] %-63s %s（现权威 %s；登记时 %s %s ⇒ **应从登记表移除**）%s\n' \
                "$path" "$(short "$cur")" "$(short "$nowauth")" "$rsha" "$date" "$off"
        else
            still=$((still+1)); printf '    [在册红] %-70s %s（现权威 %s；登记时权威 %s；类别 %s；处置 %s）%s\n' \
                "$path" "$(short "$cur")" "$(short "$nowauth")" "$aauth" "$cls" "$plan" "$off"
        fi
    done < <(grep -a '^|' "$REGISTRY" 2>/dev/null)
    echo "    在册 $n 条：**仍红 $still** ｜ 已转绿 $green（见上，应从表里删）｜ 缺件 $gone"
    echo "    ⇒ 本波之后 close-wave.sh:159-160 会**长期**打印 [⚠️ APPSYNC 非 PASS]：那是**登记在册的**红（APPSYNC 是告警不是硬闸），不是新问题；处置见登记表的处置列。"
}
show_registry
# 【`#49` `C1`（W52C 2026-09-20）】第二本册子（PF/WB，来历见 `REGISTRY2` 定义处）：
#   口径与第一本**逐条相同**（只读只打印、不参与判定）。`REGISTRY_FILE` 显式给定时两本同路径 ⇒ 不重复印。
if [ "$REGISTRY2" != "$REGISTRY" ]; then show_registry "$REGISTRY2"; fi
if [ "$EXPECT_OK" != 1 ]; then
    echo "APPSYNC=NOINFO（期望集合算不出来 ⇒ **不等于通过**；见上"EXPECT=UNKNOWN"清单）"; exit 3
elif [ "$CNT_MISMATCH" != 0 ] || [ "$CNT_RETIRED" != 0 ] || [ "$CNT_DIVERGENT" != 0 ] || [ "$CNT_MISSING" != 0 ] || [ "$CNT_UNEXPECTED" != 0 ] || [ "$CNT_AUTHMISS" != 0 ] || [ "$CNT_BRIDGE_ANCHOR" != 0 ] || [ "$CNT_BRIDGE_NOINFO" != 0 ]; then
    echo "APPSYNC=MISMATCH（MISMATCH=$CNT_MISMATCH[STALE=$CNT_STALE NEWER-DIFF=$CNT_NEWER] MISSING=$CNT_MISSING UNEXPECTED=$CNT_UNEXPECTED[DECL-GAP-EQ=$CNT_DECLGAP_EQ DECL-GAP-DIFF=$CNT_DECLGAP_DIFF] DIVERGENT=$CNT_DIVERGENT RETIRED=$CNT_RETIRED AUTH-MISSING=$CNT_AUTHMISS BRIDGE-ANCHOR=$CNT_BRIDGE_ANCHOR BRIDGE-NOINFO=$CNT_BRIDGE_NOINFO —— 见上；本脚本**不改写任何目录**）"; exit 1
# 【`D-G8`（W24D）结构性兜底】`scan()` 的 rc 也必须被消费：若它非 0 而上面五个**具名**计数器全为 0，
#   说明"有一条失败路径没有被任何计数器覆盖"（权威整份不见就是原先那一条）⇒ **不许印 PASS**。
elif [ "$scanrc" != 0 ]; then
    echo "APPSYNC=MISMATCH（scan() 内部 rc=$scanrc ≠ 0，而具名计数器全为 0 ⇒ **有失败路径未被计数器覆盖**；逐条见上）—— 本脚本**不改写任何目录**"; exit 1
elif [ "$CNT_CROSSCFG" != 0 ] || [ "$CNT_LIBCOPY" != 0 ]; then
    echo "APPSYNC=PASS（另有 $CNT_CROSSCFG 份**跨配置副本**（副本路径的配置 ≠ 声明配置 $SELFBUILT_CONFIG；D-G62 的 CROSS-CONFIG 格：**只提示、且不放行** —— 它们该红的仍在 MISMATCH/UNEXPECTED 里逐条出现）+ $CNT_LIBCOPY 份**库输出目录**（不是加载源）+ SKIP(obj/stub/ref)=$((CNT_SKIPOBJ+CNT_SKIPSTUB+CNT_SKIPREF)) 份按构造不同类 —— 见上；它们**不是**不一致）"; exit 0
else
    echo "APPSYNC=PASS（所有副本与权威件一致）"; exit 0
fi
