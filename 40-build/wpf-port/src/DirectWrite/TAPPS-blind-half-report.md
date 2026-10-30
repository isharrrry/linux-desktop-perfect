# TAPPS 车道报告 · app-local 期望模型的"瞎的那一半"：**20 个看不见拷贝点的逐条分类** + **`D-A1` 加固（两极化已证）**

* **lane = `TAPPS`**（T2 的检查器写域，本轮只动 `build/DirectWrite.Linux/wic-shim/{applocal-expect.py,check-applocal-sync.sh}` + 本文件）
* **往返时刻**：开工 `2026-09-15 18:43:29 +0800` → 收工读数 `18:50:12 +0800`；文件最终落地 `18:49:36 +0800`
* **机器**：`kernel 6.8.0-138-generic`（Ubuntu 22.04.1，x86_64）｜`mem_available` = `3075 → 3557 → 3360 MB`｜`loadavg` = `1.48/2.04/1.67 → 2.68/1.94/1.68 → 2.09/2.04/1.76`（逐次记录见 §5）
* ⚠️ **本趟是在一棵"别的车道正在动"的树上做的**（实测：`pc 18:38`／`pf 18:39`／`wb 18:37`／`reach 18:39` 被重建；同刻有 `WpfTextDemo.dll --wic-image=…` 在跑、`build/MilBridge/gen/**` 与 `arm-logs/**` 在写）。**我的两个文件是车道本地的、且读数已自证确定**（同一份检查器连跑两趟输出逐字节相同，`cmp` rc=0），**但任何"以 `#15` 表头为基准"的比对在本趟不成立**（见 §5.4）。
* 全部证据文件在 `$HOME/wfp-runs/w17-laneAPPS/`（仓外，纪律：scratch 只落 `$HOME`）。**没有跑波、没有跑构建、没有跑应用、没有改任何编译产物。**

---

## 1. 一句话结论

**这份校验器以前把 `D-A1` 那种"内容恰好对、但声明图里没有它"的副本和"内容错了"的副本压成同一个 `UNEXPECTED` 计数 —— 现在两者都被具名（`DECL-GAP-EQ` / `DECL-GAP-DIFF`）、都仍然红、`UNEXPECTED=N` 这个格子和 exit code 逐字向后兼容；同时它主动承认自己"看不见"的那部分不只是 20 个拷贝点，而是"20（主扫）+ 4（补扫：`build/` 之外的 .sh 与 MSBuild `<Copy>`）+ 13（补扫：源路径藏在变量里的写点）+ 2（主扫自己截断掉没印的只读点）"，而**真正更大的洞在条目表：`ITEMS` 只有 5 件，`FallbackCriteria/bin/Debug` 里 17 个 DLL 只有 2 个进判定，其余 15 个（含一个与权威 sha 不同的 `PresentationCore.dll`）在这份输出里一个字都没有** —— 这一条**我这轮没有修**（越出"只加类、不改模型"的最小面，且扩 `ITEMS` 属 T2/主控裁定），但已用 `grep -c 'PresentationCore.dll' = 0` 实测画出。**

---

## 2. 20 个"看不见的拷贝点" —— 逐条分类 + **它为什么是看不见的（代码路径 + 行号）**

**复算命令（唯一两条，`rc` 逐条记）**：
```bash
cd /home/links-dev/netTest/wpf-linux-20260906/wpf-linux
python3 build/DirectWrite.Linux/wic-shim/applocal-expect.py "$PWD" | grep -E '^#INVISIBLE\||^#INVISIBLE-CAPPED|^#SUMMARY'
bash    build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh
```
**本趟实读（改动前的仪器，sha16 `7c131b3f33b7e74e`）**：
```
#SUMMARY|refdirs=16|expect=52|projects=75|unknown=1|unresolved_hintpath=0|invisible_copysites=20|invisible_write=3|invisible_read=17
```
⇒ 与派单给的 `invisible_copysites=20|invisible_write=3|invisible_read=17` **逐位相符**（`expect` 从历史记录里的 47 变成 **52**，是模型边界的正常增长，不是本趟引入）。

### 2.1 先纠一处仪器自己的账（**本趟新发现，改动前就已存在**）

`invisible_copysites=20`（= write 3 + read 17），但**改动前印出来的只有 18 条**（write 3 + read 15）：

* **代码路径**：`applocal-expect.py` 的 `main()` 里 capping 那段，**改动后行号 `451-455`**（= `for k, where, ln in sites:  # 只读类：每文件最多留 3 条` ⇒ `if seen[f] > 3: continue`）——
  ```python
  for k, where, ln in sites:          # 只读类：每文件最多留 2 条（计数仍按全量）
      f = where.split(":")[0]
      if k == "read":
          seen[f] = seen.get(f, 0) + 1
          if seen[f] > 3:
              continue
  ```
  ⇒ `t1c-census.sh` 全量 **5** 条只读点，只印 **3** 条，**另有 2 条被 `continue` 掉且输出里没有任何字样**。
  （注：注释写"最多留 2 条"、实际是 `> 3` ⇒ **注释与代码也不一致**，本趟一并改成"每文件最多印 3 条"。）
* **判定**：**(b) 真洞 —— 但洞在"仪器自称"这一层，不在副本集合上**。读者会把"印出来的清单"当成"全仓清单"，这正是本项目最怕的"看起来全、其实没人读"。**已修**：现在逐文件印 `#INVISIBLE-CAPPED|read|<file>|<省略条数>|<该文件全量条数>`，检查器输出里印成"上面只读清单**不完整**：另有 2 条只读点未印"。**新增自检 `M2` 把它变成机器判据。**

### 2.2 三个**写点**（会创建/覆盖副本 ⇒ 删除与陈旧**都不会被报出**）

| # | 拷贝点 | 该行原文（截断） | 分类 | **它为什么"看不见"** |
|---|---|---|---|---|
| W1 | `build/publish-milbridge.sh:44` | `cp -f "$WIC_SRC" "$PUB/libwpfwic.so"` | **(b) 真洞** | ① 期望模型里 `libwpfwic.so` 只有**一条** `#EXPECT`，就是**权威目录自己**（`applocal-expect.py:425`（`exp[(os.path.dirname(os.path.realpath(auth)), name)] = "权威件本身的目录（权威枚举）"`））；`$PUB` = `build/MilBridge/.artifacts/publish/…` 在期望集里**不存在**。② `$PUB` 也不在 `SCAN_ROOTS`（`check-applocal-sync.sh:93`（`SCAN_ROOTS="${SCAN_ROOTS:-$REPO/build:$REPO/tests:$REPO/samples:$REPO/src}"`）；`:287` 的 `find "$r" -name "$name"` 只在这四个根下走；且实测全仓**只有 3 份** `libwpfwic.so`（权威 1 份 + `.artifacts` 2 份）⇒ 发布目录那份**在扫描面里就是不可见的**）。③ 该行**不提字面件名**（`$WIC_SRC` 是变量）⇒ 主扫 `applocal-expect.py:153` 的 `if not any(n in ln for n in names): continue` 直接跳过 ⇒ **它连"看不见的写点"这张清单都进不去**。**唯一权威 `03b67fbcd7c385b6` 今天与两处 `.artifacts` 副本同 sha（实测三处全 `03b67fbcd7c385b6`）⇒ 今天无害，但"陈旧了就红"这条能力为 0。** |
| W2 | `build/MilBridge/run.sh:95` | `cp "$ROOT/src/…/libwpfwin32.so" "$MB/tests/CompositeFontProbe/bin/Release/" 2>/dev/null \|\| true` | **(b) 真洞** | 期望集里 `libwpfwin32.so` 只有权威目录一条（同上）。⇒ `check-applocal-sync.sh:316-327` 的 `MISSING` 段（`for k in "${!EXPECTA[@]}" … if [ ! -f "$d/$it" ]`）**不会**要求这个目录有它；`grep '^#EXPECT' | grep CompositeFontProbe` 实读**只有 `DirectWrite.Linux.Provider.dll` 一条**。**`|| true` 还把失败吞掉**：拷失败/源缺 ⇒ 静默。 |
| W3 | `build/MilBridge/run.sh:228` | `cp "$MB/../../src/…/libwpfwin32.so" "$MB/tests/ContractProbe/bin/Release/" 2>/dev/null \|\| true` | **(b) 真洞** | 同 W2；`ContractProbe` 的 `#EXPECT` 实读也只有 `Provider.dll` 一条。**这条今天正在活跃**：`build/MilBridge/tests/ContractProbe/bin/Release/libwpfwin32.so` 的 mtime = **`2026-09-15 18:30:49`**（权威是 `09-14 18:44:16`）⇒ **拷贝确实在跑**，而删掉它只是"找不到副本"一行，**没有任何红**。 |

### 2.3 十七个**只读点**（枚举/赋值/断言，不产生副本）

判定依据（`applocal-expect.py:156-160`，即主扫 `invisible_copy_sites()` 的 write/read 分流那几行）：该行**提到某个 ITEM 文件名**，且**不含** `cp|install|mv|rm`、也不是 echo/printf/comments ⇒ 落 `reads`。

| # | 拷贝点 | 该行在干什么 | 分类 |
|---|---|---|---|
| R1 | `build/close-wave.sh:99` | `NATIVE_AUTH="src/WpfGfx.Linux.Native/bin/libwpfwin32.so"`（**赋值**，后面 `:130` 用它 `cp`） | **(a) 本行无害；但同族的真写点在 `:130` ⇒ 见 §2.5 `I1`** |
| R2–R3 | `build/close-wave.sh:133`、`:136` | `find . -name 'libwpfwin32.so' …`（枚举全仓副本，**判据用**） | **(a) 无害**（只读；且它正是"全仓副本普查"的正面实现） |
| R4 | `build/publish-milbridge.sh:31` | `WIC_SRC="$ROOT/build/DirectWrite.Linux/wic-shim/libwpfwic.so"` | **(a) 本行无害**（真写点是 `:44` = W1） |
| R5 | `build/publish-milbridge.sh:39` | `[ -f "$PUB/wpfgfx_cor3.so" ] \|\| { echo …; exit 3; }`（存在性断言，**失败即 rc=3**） | **(a) 无害**（这是"缺件不静默"的正确形态） |
| R6 | `build/publish-milbridge.sh:54` | `for f in wpfgfx_cor3.so libwpfwic.so libSkiaSharp.so; do`（并排 sha 打印） | **(a) 无害** |
| R7 | `build/MilBridge/run.sh:47` | `nm -D --defined-only "$PUB/wpfgfx_cor3.so" \| … > /tmp/mb-nm.txt` | **(a) 无害**（只读+写 `/tmp`） |
| R8 | `build/MilBridge/run.sh:194` | `"$ROOT/DirectWrite.Linux/Provider/bin/Debug/DirectWrite.Linux.Provider.dll"`（`pairs=(…)` 数组元素） | **(a) 本行无害；真写点在 `:210` ⇒ 见 §2.5 `I2`** |
| R9 | `build/MilBridge/tools/t1b-ls-tripwire.sh:73` | `SHIM="$SELF_DIR/…/libwpfwin32.so"`（`nm` 读数用） | **(a) 无害** |
| R10–R12 | `build/MilBridge/tools/t1c-census.sh:87`、`:90`、`:100` | 三个赋值（`PROVIDER=`/`SHIM=`/`MILDIR=`），都是**普查**用 | **(a) 本行无害；真写点在 `:88/:92/:94/:102` ⇒ 见 §2.5 `I3–I6`** |
| R13 | `build/MilBridge/tools/t1c-census.sh`（**被截断的第 4 条，未印**） | 同文件 `:201/:202` 的 `for f in PresentationCore.dll … libwpfwin32.so wpfgfx_cor3.so; do` | **(a) 无害**（列表头）——**它的"被截断"本身是 §2.1 的洞，已修** |
| R14 | `build/MilBridge/tools/t1c-census.sh`（**被截断的第 5 条，未印**） | 同族列表/断言行 | **(a) 无害**（同上） |
| R15 | `build/DirectWrite.Linux/WicClosedLoop/run-harness.sh:29` | `shim="$shim_dir/libwpfwic.so"` | **(a) 无害**（读路径变量） |
| R16 | `build/DirectWrite.Linux/WicWriteClosedLoop/run-write-harness.sh:29` | 同上 | **(a) 无害** |
| R17 | `build/DirectWrite.Linux/FontEntryClosedLoop/run-font-harness.sh:29` | 同上 | **(a) 无害** |

> **R13/R14 的行号我不写死**：它们是"被截断的那两条"，本趟仪器只印到每文件前 3 条 ⇒ **我没有把它们逐条读出来**。要精确行号 ⇒ `grep -n 'wpfgfx_cor3\.so\|libwpfwin32\.so' build/MilBridge/tools/t1c-census.sh`（工具在 `grep`，不是猜）。**这里明确写"未测量"，不写成"已知无害的两行具体内容"。**

### 2.4 20 条的合计判定

* **(a) 20 条中 17 条（全部只读点）确实在期望模型的射程之外，但无害** —— 它们不改变副本集合，失败只会让某个工具**打错读数**，不会让一个发散的 DLL 静默躺在加载路径上。
* **(b) 3 个写点全部是真洞**，而且**方向一致**：**"删除不报、陈旧不报、拷贝失败被 `2>/dev/null \|\| true` 吞掉"**，因为期望模型对**原生件**只有"权威目录自己"这一条（`applocal-expect.py:11-13` 与 `:441`/`:443` 的两条 `#UNKNOWN` 已经把这条边界写在文件头与输出里，只是没人把"因此这 3 个写点的产物是没人管的"说全）。
* **重要边界**：这 3 个写点今天**恰好都是无害状态**（`libwpfwin32.so` 全仓 4 份**全部 `0098234982391bbf`**、`libwpfwic.so` 3 份**全部 `03b67fbcd7c385b6`**，实测）。⇒ 这是 **(b) 类里"今天不红、但判据为 0"的那种洞**，不是"现在正错着"。

### 2.5 主扫**结构性**看不见的写点（补扫新增，**都不在那 20 条里**）

**为什么它们一条都进不了主扫**：`applocal-expect.py:153` 的 `if not any(n in ln for n in names): continue` —— **它只认"字面写出件名"的行**。

| # | 拷贝点 | 原文 | 分类 | 为什么看不见 |
|---|---|---|---|---|
| I1 | `build/close-wave.sh:130` | `cp -f "$NATIVE_AUTH" "$c"` | **(b) 真洞** | 行里**一个字面件名都没有**（源与目标都是变量）⇒ 主扫跳过；变量在 `:99` 定义 ⇒ 只有做**本文件内**数据流才看得见 |
| I2 | `build/MilBridge/run.sh:210` | `cp -f "$f" "$d/$base"`（`refresh_applocal`） | **(b) 真洞（**REPORT §24.8 点名过"现有检测器漏报"的就是它**）** | 同上；`$f` 来自 `local pairs=(…)`（`:192-197`，**多行数组字面量**）⇒ 需要跨行数组追踪 |
| I3–I6 | `build/MilBridge/tools/t1c-census.sh:88/:92/:94/:102` | `cp -f "$PROVIDER" "$RUN"/`、`cp -f "$SHIM" "$RUN"/`、`cp -f "$SHIM" "$RUN/$alias"`、`for so in "$MILDIR"/*.so; do … cp -f "$so" "$RUN"/` | **(a) 无害（目标 `$RUN` 是 `/tmp` 下的实验目录）** | 变量间接（同上） |
| I7–I8 | `tests/…/run-hellowpf.sh:181`、`:197` | `cp -f "$SHIM" "$OUT"/`、`cp -f "$SHIM" "$OUT/$alias"` | **见下"影子别名"一节** | ① `.sh` 在 `tests/` 下 ⇒ 主扫的 `os.walk(REPO/"build")` 根本走不到；② 源是变量 |
| I9–I11 | `tests/…/run-wpfprobe.sh:210`、`:213`、`:217` | `cp -f "$PROVIDER" "$OUT"/`、`cp -f "$SHIM" "$OUT"/`、`cp -f "$so" "$OUT"/` | 同上 | 同上 |
| I12–I13 | `tests/…/run-wpftextdemo.sh:545`、`:563` | `cp -f "$SHIM" "$OUT"/`、`cp -f "$SHIM" "$OUT/$alias"` | 同上 | 同上 |

**补扫①（`build/` 之外的 .sh 与 MSBuild `<Copy>`）实读 4 条**（`#SUMMARY|…|invisible_ext_write=4`）：
```
#INVISIBLE-EXT|write|build/MilBridge/tests/Directory.Build.targets:21|<Copy SourceFiles="$(ProviderAuthorityPath)"　（写点经属性 $(ProviderAuthorityPath) ⇒ DirectWrite.Linux.Provider.dll）
#INVISIBLE-EXT|write|build/DirectWrite.Linux/Directory.Build.targets:38|<Copy SourceFiles="$(ProviderAuthorityPath)"　（写点经属性 $(ProviderAuthorityPath) ⇒ DirectWrite.Linux.Provider.dll）
#INVISIBLE-EXT|write|tests/WpfGfx.Linux.Tests/Presentation.Tests/run-wpftextdemo.sh:179|cp -f "$BRIDGE_PUB_DIR/wpfgfx_cor3.so" "$SO_BAK" 2>/dev/null
#INVISIBLE-EXT|write|tests/WpfGfx.Linux.Tests/Presentation.Tests/run-wpftextdemo.sh:191|cp -f "$SO_BAK" "$BRIDGE_PUB_DIR/wpfgfx_cor3.so" 2>/dev/null
```
判定：
* 两条 **MSBuild `<Copy>`**（`SyncProviderAuthority`）= **(a) 无害**：它们把**权威 Provider 无条件**拷进启动宿主目录（`SkipUnchangedFiles="false"`），目标目录**都在期望集里**（Provider 是全模型覆盖最好的件，`#EXPECT` 32 条）⇒ 语义上反而在**减少**红，只是"有这么个拷贝点"这件事以前不在任何清单里。
* 两条 `run-wpftextdemo.sh:179/:191`（存/还原 `.artifacts/publish/…/wpfgfx_cor3.so`）= **(b) 真洞但方向相反**：目标是**桥发布的唯一真身**，而 `wpfgfx_cor3.so` 的 `ITEMS` 权威是**空**（`applocal-expect.py:48`（`("wpfgfx_cor3.so", "")`）、`check-applocal-sync.sh:82` 都是空权威）⇒ **该件的期望是 `EXPECT=UNKNOWN`，一个 sha 判据都没有**。它是"**存了要记得还原**"的临时改写，**`:191` 的还原若不执行，发布目录会留着被改过的桥**，而校验器对此**完全沉默**（今天实测发布目录那份 = `caf7baf9e67719aa` = 表头值，**没有正在错**）。
* ⚠️ **另有一条同族我没能算进"写点"**：`.artifacts/publish/…/wpfgfx_cor3.so` **不在 `SCAN_ROOTS` 的 `find` 结果里**（实测全仓 `wpfgfx_cor3.so` 只有 2 份，都在 `build/MilBridge/.artifacts/**`）⇒ 就算给它配了权威，**它也不在扫描面内**。

**"影子别名"（同族、值得单列）**：`run-wpfprobe.sh:214`、`run-wpftextdemo.sh:562-564`、`run-hellowpf.sh:196-198`、`t1c-census.sh:93-95` 都把 **`libwpfwin32.so` 以 4–5 个 Windows DLL 名**（`uxtheme.dll`/`wtsapi32.dll`/`shell32.dll`/`PresentationNative_cor3.dll`）**另拷一份**（源码注释自述是"部署期止损"）。这些别名**不是** `ITEMS` 里的任何名字 ⇒ 对校验器**完全不存在**：一个陈旧的 `shell32.dll` 副本会一直在 app 目录里被优先命中，而 `OK=45` 一个数都不会动。**(b) 真洞（结构性）**，归属在 `tests/**`（T3 车道）。

---

## 3. `D-A1` 加固：类设计（before / after 行为表）

### 3.1 设计口径（写死，免得后人以为改了判据语义）

**没有改名、没有放宽、没有提高任何阈值**。`UNEXPECTED` 仍然是那个计数格、仍然计红、仍然进 `APPSYNC=MISMATCH`。**新加的只是"按内容 vs 权威的二分"**：

```
UNEXPECTED=N[DECL-GAP-EQ=n DECL-GAP-DIFF=m]      ← 计数行与 APPSYNC=MISMATCH 行里都印
   逐条行首标签： UNEXPECTED-EQ / UNEXPECTED-DIFF（更具体，不隐藏）
```

* **`DECL-GAP-EQ`** = 副本**不在声明图里**（= 老的 `UNEXPECTED`），但 **`sha == 权威`** ⇒ **"未声明的传递依赖副本"**：**不是陈旧件、也不许洗绿**（D-A1 现场的形态）。⇒ **具名 + 可见 + 仍然红 + 仍然进 `UNEXPECTED` 总数**。
* **`DECL-GAP-DIFF`** = 同样"不在声明图里"，但 **`sha ≠ 权威`** ⇒ **硬失败**（内容不同的未声明副本）。
* **"算不出来不许降级成 EQ"**：权威查不到时（`auth_sha_of()` 返回 `<no-authority>`）**按 `DIFF` 计** —— "我不知道该跟谁比"绝不能变成"看着挺对"。

### 3.2 before / after

| 现场形态 | **改动前**（`check-applocal-sync.sh` `3ba5284bad838b14`） | **改动后**（`aad23482f84bdf44`） |
|---|---|---|
| 副本不在声明图里，**`sha == 权威`**（`D-A1` 真现场） | `UNEXPECTED  …（引用图**没有**要求这一份 ⇒ 多余副本/或新拷贝点未声明）`＋`UNEXPECTED=1` ⇒ rc=1 | `UNEXPECTED-EQ …（**不在声明图里**，但 **sha == 权威 0c597fb6ec1eec70** ⇒ 未声明的传递依赖副本；**不是陈旧件，也不许当绿**）`＋`UNEXPECTED=1[DECL-GAP-EQ=1 DECL-GAP-DIFF=0]` ⇒ **rc=1（不变）** |
| 同一位置，**`sha ≠ 权威`** | 同样只报 `UNEXPECTED`（**红，但读不出"内容不同"**） | `UNEXPECTED-DIFF … 8c33c751b2d98a79（**不在声明图里** 且 **sha ≠ 权威 0c597fb6ec1eec70** ⇒ **硬失败**…）`＋`UNEXPECTED=1[DECL-GAP-EQ=0 DECL-GAP-DIFF=1]` ⇒ **rc=1（不变）** |
| 权威**算不出来**的件（`<no-authority>`） | `UNEXPECTED`（红） | `UNEXPECTED-DIFF`（**硬失败**，不降级） |
| 旧消费者（`close-wave.sh:159`、`publish-milbridge.sh:102`、`integration-wave.sh`）按 `APPSYNC=PASS` / 非 PASS 判 | rc=1、`APPSYNC=MISMATCH` | **逐字不变**（只多打一对方括号明细与一行说明） |

**向后兼容的实测**（`diff` 逐字节，见 §4.5）：把新输出的 `[DECL-GAP-EQ=1 DECL-GAP-DIFF=0]` 与新增说明行剥掉后，与改动前那趟输出的**唯一差异就是那一条 `UNEXPECTED` 行的措辞**，`计数：` 行、`APPSYNC=` 行、**rc=1** 全部一致。

### 3.3 顺手补掉的"仪器自称"三处（都属于"不许静默"的同一族）

1. **截断自称**：`#INVISIBLE-CAPPED|read|<file>|省略条数|全量条数` ⇒ 检查器印"上面只读清单**不完整**：另有 2 条只读点未印"。
2. **扫描范围自称**：`#INVISIBLE-EXT|write|…`（补扫①：`build/` 之外的 .sh + MSBuild `<Copy>`）与 `#INVISIBLE-INDIRECT|write|…`（补扫②：**源路径藏在变量里**的写点，13 条）。
3. **"下界"自称**：输出里明写"**三张清单仍不是全仓拷贝点的完全集**：间接补扫只做**单文件内**的轻量数据流（不做跨文件、不做函数间、不认 `$(...)` 里现算的路径）⇒ 它给的是**下界**"。
4. **自检**：`M2`（`invisible_capped>0 ⇒ 必须印"不完整"`；补扫写点 >0 ⇒ 必须印"补扫的写点 N 处"并逐条列）⇒ 自检从 **15/15 → 17/17 `SELFTEST=PASS`**（新增 `L2`、`N`）。

---

## 4. 两极化证据（**每条都给了确切命令与确切 `rc`**）

工作目录一律 `/home/links-dev/netTest/wpf-linux-20260906/wpf-linux`；证据文件在 `$HOME/wfp-runs/w17-laneAPPS/`。

### 4.0 前置：先记"没碰之前"的读数（**这是"我改了没有"的唯一凭据**）
```bash
$ for f in build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll \
           src/WpfGfx.Linux/bin/Debug/net10.0/WpfGfx.Linux.dll; do
    printf '%s  %s  %s\n' "$(sha256sum "$f"|cut -c1-16)" "$(stat -c '%y %s' "$f")" "$f"; done
0c597fb6ec1eec70  2026-09-15 12:56:49.862274703 +0800 357888  build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll
0c597fb6ec1eec70  2026-09-15 12:38:40.570125621 +0800 357888  src/WpfGfx.Linux/bin/Debug/net10.0/WpfGfx.Linux.dll
$ cp -p <target> $HOME/wfp-runs/w17-laneAPPS/backup/fallbackcriteria-WpfGfx.Linux.dll
$ cmp <target> $HOME/.../backup/...  ; echo "rc=$?"
rc=0
```
（`rc=0`；原始读数存 `12-proof-prestate.txt`，sha16 `…` 见 §5 清单。**WpfGfx 权威 `0c597fb6ec1eec70` 与 `CURRENT-STATE.md:8` 记的逐位相符** ⇒ 本件不是移动中的那些件。）

### 4.1 极①（**负极性**）：把**内容不同**的副本放到 `D-A1` 那个位置 ⇒ 必须**具名 + 硬红**
```bash
$ head -c 2048 $HOME/wfp-runs/w17-laneAPPS/backup/fallbackcriteria-WpfGfx.Linux.dll > $HOME/wfp-runs/w17-laneAPPS/injected-divergent-WpfGfx.Linux.dll
$ cp -f $HOME/wfp-runs/w17-laneAPPS/injected-divergent-WpfGfx.Linux.dll \
        build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll          # rc=0
$ printf '%s  %s\n' "$(sha256sum <target>|cut -c1-16)" "$(stat -c '%s' <target>)"
8c33c751b2d98a79  2048
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh > $HOME/wfp-runs/w17-laneAPPS/13-check-DIVERGENT.out 2>&1 ; echo "rc=$?"
rc=1
$ grep -nE 'UNEXPECTED|计数：|APPSYNC=' $HOME/wfp-runs/w17-laneAPPS/13-check-DIVERGENT.out
70:    UNEXPECTED-DIFF build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll        8c33c751b2d98a79（**不在声明图里** 且 **sha ≠ 权威 0c597fb6ec1eec70** ⇒ **硬失败**：内容不同的未声明副本；权威算不出时也落这一类）
156:计数：OK=45  MISMATCH=0（STALE=0  NEWER-DIFF=0）  MISSING=0  UNEXPECTED=1[DECL-GAP-EQ=0 DECL-GAP-DIFF=1]  DIVERGENT=1  NO-AUTHORITY=20  LIB-COPY=0  SKIP(obj)=6  SKIP(stub)=4  SKIP(ref)=10  RETIRED=0
158:APPSYNC=MISMATCH（… UNEXPECTED=1[DECL-GAP-EQ=0 DECL-GAP-DIFF=1] DIVERGENT=1 RETIRED=0 …）
```
⇒ **两把独立的尺子同时响**：新的 `DECL-GAP-DIFF` **和** 既有的跨副本一致性 `DIVERGENT=1`（它还逐个列出两份不同代）。**内容不同不可能被洗成 EQ。**

### 4.2 极②（**正极性**）：同一位置放回**与权威逐字节相同**的副本 ⇒ 归 `DECL-GAP-EQ`、**仍然可见、仍然红**
```bash
$ cp -f $HOME/wfp-runs/w17-laneAPPS/backup/fallbackcriteria-WpfGfx.Linux.dll \
        build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll          # rc=0
$ cmp $HOME/wfp-runs/w17-laneAPPS/backup/fallbackcriteria-WpfGfx.Linux.dll <target> ; echo "rc=$?"
rc=0
$ printf '%s  %s\n' "$(sha256sum <target>|cut -c1-16)" "$(stat -c '%y %s' <target>)"
0c597fb6ec1eec70  2026-09-15 18:47:37.757843869 +0800 357888
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh > $HOME/wfp-runs/w17-laneAPPS/14-check-restored.out 2>&1 ; echo "rc=$?"
rc=1
$ grep -nE 'UNEXPECTED|计数：|APPSYNC=' $HOME/wfp-runs/w17-laneAPPS/14-check-restored.out
70:    UNEXPECTED-EQ build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll          0c597fb6ec1eec70（**不在声明图里**，但 **sha == 权威 0c597fb6ec1eec70** ⇒ 未声明的传递依赖副本；**不是陈旧件，也不许当绿**）
147:计数：OK=45  MISMATCH=0（STALE=0  NEWER-DIFF=0）  MISSING=0  UNEXPECTED=1[DECL-GAP-EQ=1 DECL-GAP-DIFF=0]  DIVERGENT=0  NO-AUTHORITY=20  LIB-COPY=0  SKIP(obj)=6  SKIP(stub)=4  SKIP(ref)=10  RETIRED=0
149:APPSYNC=MISMATCH（MISMATCH=0[STALE=0 NEWER-DIFF=0] MISSING=0 UNEXPECTED=1[DECL-GAP-EQ=1 DECL-GAP-DIFF=0] DIVERGENT=0 RETIRED=0 —— 见上；本脚本**不改写任何目录**）
```
⇒ **`DECL-GAP-EQ=1`、`APPSYNC=MISMATCH`、rc=1**：**看得到、点得出名、仍然红** —— 既没有"洗绿"，也没有把它藏进某个不判的类。

### 4.3 极③（**沙箱里的对拍，同一份字节两种归类**）—— 自检 `L2` / `N`（rc=0, `SELFTEST=PASS`）
```bash
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh --selftest ; echo "rc=$?"
rc=0
SELFTEST_L2=PASS（内容==权威的未声明副本被**具名**报出且仍然红 exit=1 —— 不许洗绿）
    UNEXPECTED-EQ /tmp/tmp.…/K/app/bin/Debug/WpfGfx.Linux.dll   0c597fb6ec1eec70（… sha == 权威 …）
    计数：OK=2  …  UNEXPECTED=1[DECL-GAP-EQ=1 DECL-GAP-DIFF=0]  …      (exit=1)
SELFTEST_N=PASS（内容≠权威的未声明副本走 DIFF 类、硬失败 exit=1）
    UNEXPECTED-DIFF /tmp/tmp.…/K/app/bin/Debug/WpfGfx.Linux.dll  ee6fc9ae994cc042（… sha ≠ 权威 …）
    计数：OK=2  …  UNEXPECTED=1[DECL-GAP-EQ=0 DECL-GAP-DIFF=1]  …      (exit=1)
SELFTEST=PASS
$ grep -c '=PASS' $HOME/wfp-runs/w17-laneAPPS/22-selftest-final.out
17
```
⇒ **同一个路径、同一份代码，`EQ`/`DIFF` 只由"内容 vs 权威"决定**（本趟把两趟的输入字节都印在输出里，可对拍）。自检总数 **15 → 17**，全部 `PASS`（`A,B,C,D,E,F,G,H,I,J,K,L,L2,N,M,M2`）。

### 4.4 极④（**既存的 `MISMATCH` 判决没有被放宽**）——自检 `E`（权威换 sha ⇒ 红；还原 ⇒ 绿）
```
SELFTEST_E=PASS（权威同 sha ⇒ exit=0 绿；权威换 sha ⇒ exit=1 红）
SELFTEST_G=PASS（obj 只 SKIP；bin 的落后副本仍报红、exit=1）
SELFTEST_K=PASS（期望基数 删前=7 / 删后=7 **不变**；删⇒MISSING 红 exit=1；整份还原⇒绿 exit=0）
SELFTEST_L=PASS（多余副本被报 UNEXPECTED 且计红 exit=1）
SELFTEST_M=PASS（summary + 写点清单 + 删除不会被报出三件都在）
SELFTEST_M2=PASS（invisible_capped=2 ⇒ 截断自称；invisible_ext_write=4 ⇒ 补扫自称且逐条印）
```
⇒ **"内容不同 ⇒ 红"这条老能力（`MISMATCH/STALE/NEWER-DIFF/DIVERGENT/MISSING`）一条都没动**。

### 4.5 向后兼容的逐字节 `diff`
```bash
$ diff <(sed 's/UNEXPECTED=1\[DECL-GAP-EQ=1 DECL-GAP-DIFF=0\]/UNEXPECTED=1/' 06-check-after.out \
        | grep -v '^        （UNEXPECTED 的两个子类') 02-check-baseline.out ; echo "rc=$?"
70c70
<     UNEXPECTED-EQ build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll  …（**不在声明图里**，但 **sha == 权威 …** ⇒ 未声明的传递依赖副本；**不是陈旧件，也不许当绿**）
---
>     UNEXPECTED    build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll  …（引用图**没有**要求这一份 ⇒ 多余副本/或新拷贝点未声明）
rc=1
```
⇒ **除那一条行的措辞与那对新增明细外，改动前/后两趟输出逐字相同**；`rc` 两趟都是 **1**。

### 4.6 确定性（"同一趟读数"不是运气）
```bash
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh > 23-check-final2.out 2>&1 ; echo "rc=$?"   # rc=1
$ bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh > 24-check-final3.out 2>&1 ; echo "rc=$?"   # rc=1
$ cmp 23-check-final2.out 24-check-final3.out ; echo "rc=$?"
rc=0
```
⇒ 两趟**逐字节相同**（含 `UNEXPECTED-EQ` 那一行与两个计数格）。

### 4.7 "我没有把真树弄坏"的四条凭据
1. **`cmp` + sha16 相等**：`cmp backup <target>` **rc=0**；`<target>` 与备份 sha16 都是 `0c597fb6ec1eec70`（＝改动前记录的 `PRE` 值，也＝**权威**值）。
2. **size 相等**：`357888` B（改动前 `357888` B）。
3. **mtime 变了，如实记**：`12:56:49` → **`18:47:37`**（`cp -f` 不带 `-p`，**我不声称保住了 mtime**）。这不影响判定：该副本内容 == 权威 ⇒ `OK` 类；`mtime` 在本仪器里**只用于"说清是哪种不同"，不用于放行**（`check-applocal-sync.sh:33-34`）。**未测**：若权威随后被重建（`pc/pf` 正在动），本副本会先落 `STALE` 而非 `OK` —— 这是"拷贝语义"的正常形态，不是本趟残留。
4. **没有 `rm` 任何东西**：极①用 `cp -f` 覆盖、极②用 `cp -f` 还原（纪律：仓内不删）。**唯一一次删除在仓外**：我早前用 `python3 -c "import"` 误建的 `build/DirectWrite.Linux/wic-shim/__pycache__/`（18:43:46，`rm -rf` rc=0，`ls | grep -c pycache` = 0）——**如实登记为我制造又清掉的污染**。

---

## 5. 读数（sha16 + size + mtime + loadavg + mem_available + lane + 时刻 + kernel）

### 5.1 我改动的两个文件（**唯一改动面**）
| 文件 | 改动前 sha16 / size / mtime | **改动后 sha16 / size / mtime** |
|---|---|---|
| `build/DirectWrite.Linux/wic-shim/applocal-expect.py` | `7c131b3f33b7e74e` / 13,857 B / 2026-09-14 19:37:32 | **`6eafbea14e7ea41e`** / **27,314 B** / 2026-09-15 18:49:02 |
| `build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh` | `3ba5284bad838b14` / 44,738 B / 2026-09-14 19:37:32 | **`aad23482f84bdf44`** / **55,520 B** / 2026-09-15 18:49:36 |

（改动前的两个 sha16 与 `REPORT.md §28.2/§28.4` **逐位相符** ⇒ 我动的是那一版，不是别的世代。）

### 5.2 被观测的权威件 / `D-A1` 现场（**前后都记**）
| 件 | 改动前 sha16 / size | 收工 sha16 / size | mtime 变化 |
|---|---|---|---|
| `build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll`（`D-A1` 现场） | `0c597fb6ec1eec70` / 357,888 | `0c597fb6ec1eec70` / 357,888 | `12:56:49` → **`18:47:37`**（我覆盖+还原） |
| `src/WpfGfx.Linux/bin/Debug/net10.0/WpfGfx.Linux.dll`（权威） | `0c597fb6ec1eec70` / 357,888 / 12:38:40 | 同 | 未变 |
| `build/DirectWrite.Linux/wic-shim/libwpfwic.so`（权威） | `03b67fbcd7c385b6` / 70,440 / 09-11 22:04 | 同 | 未变 |
| `src/WpfGfx.Linux.Native/bin/libwpfwin32.so`（权威） | `0098234982391bbf` / 283,648 / 09-14 18:44 | 同 | 未变 |
| `build/DirectWrite.Linux/Provider/bin/Debug/DirectWrite.Linux.Provider.dll`（权威） | `9aa0d744802aaa31` / 112,640 / 12:40 | 同 | 未变 |
| `build/ReachFramework.Linux/bin/Debug/ReachFramework.dll`（权威） | `b49ec81054d95988` / 741,376 / **18:39:13** | 同 | 未变（**但见 §5.4**） |
| `build/DirectWrite.Linux/FallbackCriteria/bin/Debug/DirectWrite.Linux.Provider.dll` | `9aa0d744802aaa31` / 112,640 | 同 | 未变 |

### 5.3 逐趟环境读数（**lane=TAPPS**）
| 时刻 (+0800) | 动作 / 读数文件 | loadavg | mem_available | rc |
|---|---|---|---|---|
| 18:43:29 | 开工读数 | `1.48 2.04 1.67` | 3,075 MB | — |
| 18:43:43 | `01-expect-baseline.out`（`#SUMMARY` 见 §2） | `1.49 2.02 1.66` | 3,557 MB | rc=0 |
| 18:43:59 | `02-check-baseline.out`（**改动前**） | 同上 | 同上 | **rc=1** |
| 18:44:03 | `03-authorities-before.txt` | `1.72 2.04 1.68` | 3,604 MB | rc=0 |
| 18:44:0x | `04-selftest-before.out`（15/15） | — | — | rc=0 |
| 18:47:26 | `12-proof-prestate.txt` | `2.49 1.87 1.65` | 3,581 MB | rc=0 |
| 18:47:28 | `13-check-DIVERGENT.out`（**极①**） | `2.53 1.89 1.66` | 3,582 MB | **rc=1** |
| 18:47:37 | `14-check-restored.out`（**极②**） | `2.68 1.94 1.68` | 3,557 MB | **rc=1** |
| 18:49:02 / 18:49:36 | 两个仪器文件最终落地 | — | — | — |
| 18:49:5x | `22-selftest-final.out`（17/17） | `2.07 2.03 1.75` | 3,365 MB | rc=0 |
| 18:50:12 | `25-proof-poststate.txt`（收工读数） | `2.09 2.04 1.76` | 3,360 MB | rc=0 |

`kernel = 6.8.0-138-generic`（全程未变）。**没有一波在跑**（跑前 `ps` 核过：无 `integration-wave|close-wave|sync-applocal|verify-all|dotnet build|dotnet test` 进程），但**别的车道在活**（见 §5.4）。

### 5.4 ⚠️ 本趟树上正在动的东西（**不许被读成我的残留**）
* `build/PresentationCore.Linux/bin/Debug/PresentationCore.dll` = **`c0763fc10173e7ff`（18:38:14）**，而 `CURRENT-STATE.md §1` 记的是 `532c7f54f7573070`；`build/PresentationFramework.Linux/bin/Debug/PresentationFramework.dll` = **`9ef136caddb10370`（18:39:08）**，记录是 `06b12fb74fb50c96`；`WindowsBase.dll` `e6216fe961a2bfb9`（18:37:37，**sha 未变、mtime 变**）；`ReachFramework.dll` `b49ec81054d95988`（18:39:13）。
* 同刻在跑：`timeout 60 dotnet WpfTextDemo.dll --wic-image=$HOME/wfp-runs/mygate16-wicdiag/diag-96.png`；`build/MilBridge/gen/tline-*.txt`、`build/MilBridge/arm-logs/*.log`、`build/MilBridge/known-red.json` 都在 18:2x–18:4x 被写。
* ⇒ **本趟的全部读数都带时刻**；凡"与 `#15` 表头比对"的结论在本趟**一律不成立**（`pc/pf` 已前进）。**我的两个文件与判定不依赖那些件**（检查器只按 `ITEMS` 的 5 件与 csproj 声明图工作），但**`ReachFramework` 的权威 sha 我按现场读取 `b49ec81054d95988` 记录**，与文档不一致这件事**如实留档**。

### 5.5 证据文件清单（都在 `$HOME/wfp-runs/w17-laneAPPS/`，仓外）
`01-expect-baseline.out`｜`02-check-baseline.out`｜`03-authorities-before.txt`｜`04-selftest-before.out`｜`05-selftest-after.out`｜`06-check-after.out`｜`07–09/15–20-expect-*.out`（枚举器迭代 6 版，含两次**自纠**）｜`10/21/23/24-check-final*.out`｜`11/22-selftest-final*.out`｜`12-proof-prestate.txt`｜`13-check-DIVERGENT.out`｜`14-check-restored.out`｜`25-proof-poststate.txt`｜`26-site-files.txt`｜`27-fallbackcriteria-dlls.txt`｜`28-exp-unmanaged.out`（实验①）｜`29-exp-renamed.out`（**作废**：见 §7.7）｜`30-exp-authdir.out`（实验②）｜`backup/fallbackcriteria-WpfGfx.Linux.dll`｜`injected-divergent-WpfGfx.Linux.dll`。
**承载 20 个拷贝点的脚本文件 sha16（改动前=改动后，我一个都没碰）**：
```
bbd041ac19a94ba4  build/publish-milbridge.sh                     3e513e88a4fa4ec9  build/MilBridge/run.sh
68c7167c61b4e00e  build/close-wave.sh                            5eac3bda23b117b6  build/MilBridge/tools/t1c-census.sh
25acacdf17c079e1  build/MilBridge/tools/t1b-ls-tripwire.sh       0e1f66b4ba9682c7  build/DirectWrite.Linux/WicClosedLoop/run-harness.sh
57642b8223f54ca3  build/DirectWrite.Linux/WicWriteClosedLoop/run-write-harness.sh
db296e6f2422ef2a  build/DirectWrite.Linux/FontEntryClosedLoop/run-font-harness.sh
5dfb2635b87bb351  tests/WpfGfx.Linux.Tests/Presentation.Tests/run-wpftextdemo.sh
3848a43dcc589a95  tests/WpfGfx.Linux.Tests/Presentation.Tests/run-wpfprobe.sh
33708f69030b6b11  tests/WpfGfx.Linux.Tests/Presentation.Tests/run-hellowpf.sh
1324fcde93604349  build/MilBridge/tests/Directory.Build.targets      dd081aebd7d926e4  build/DirectWrite.Linux/Directory.Build.targets
```
**本文件（本报告）自己的 sha16 / size**：`c7b68621f2bd0bad` / 48,059 B / `2026-09-15 18:52:59`（**在 18:52:54 那一版上量的**；此后我只把 §4.7 的标题从"三条"改成"四条"，**那一处改动之后的 sha16 以本句所在版本为准 —— 我没有把它重新嵌进来**，因为"把 sha 写进被量文件"本身会改变该文件（这是本项目的自指陷阱，如实说明）。**任何引用请现场 `sha256sum` 现算。**）

（`3e513e88a4fa4ec9` 正好等于纪律 31 里"回退后"的那个 `run.sh` sha ⇒ 目录里那份是**没有那段注释改动的版本**，与门禁世代一致。）

---

## 6. **改完之后仍然无法变红的类**（这一节与修复同等重要）

按"**这一类里的错误能不能让本校验器非 0 退出**"逐条清点。**下面每一条都是"我能指出现场、但本趟故意没修/不该我修"**：

| # | 仍然不能变红的东西 | 现场证据 | 为什么不能（代码路径） |
|---|---|---|---|
| 1 | **`ITEMS` 表之外的托管件，一个都不判**（**这是比 `D-A1` 大得多的洞**） | `build/DirectWrite.Linux/FallbackCriteria/bin/Debug/` 有 **17 个 DLL**，其中**只有 2 个**（`DirectWrite.Linux.Provider.dll`、`WpfGfx.Linux.dll`）进判定；其余 15 个（`PresentationCore.dll` `532c7f54f7573070`、`WindowsBase.dll`、`System.Xaml.dll`、`UIAutomationTypes/Provider.dll`、`System.Windows.Input.Manipulations.dll`、SkiaSharp 等）**在整个检查器输出里一个字都没有**：`bash …/check-applocal-sync.sh \| grep -c 'PresentationCore.dll'` = **0** | `ITEMS` 是**硬编码 5 件表**（`applocal-expect.py:42-49`、`check-applocal-sync.sh:97-104`）；`scan()` 的 `find` 只 `-name "$name"` 这 5 个名字（`check-applocal-sync.sh:287`）。**没有"枚举所有 app-local DLL 并与权威比"这条路** |
| 2 | **同一个洞的"正在错着"实例** | `FallbackCriteria/bin/Debug/PresentationCore.dll` = **`532c7f54f7573070`（12:54，＝`#15` 表头那一版）**，而权威 `build/PresentationCore.Linux/bin/Debug/PresentationCore.dll` = **`c0763fc10173e7ff`（18:38）** ⇒ **两个 sha、不同字节** | 同上。⇒ 这就是派单里说的"**(b) 一个发散的 DLL 静默躺在加载路径上**"的**活样本**（该目录是**启动宿主**，有 `FallbackCriteria.runtimeconfig.json`）。**注意**：它今天"发散"的原因是**权威刚被别的车道重建**，不是有人写坏了件 —— **但校验器对此的读数是 0，不是红** |
| 3 | **原生件的"存在性期望"＋"多余副本"**（`libwpfwin32.so` / `libwpfwic.so` / `wpfgfx_cor3.so`）—— **本趟已实测**（§7.7 实验①：与权威同 sha 的 `libwpfwic.so` 放进启动宿主目录 ⇒ `OK` + `UNEXPECTED=0` + **rc=0**） | `libwpfwin32.so` 全仓 4 份，**只有权威目录那 1 份在期望集里**（`#EXPECT … src/WpfGfx.Linux.Native/bin|libwpfwin32.so|权威件本身的目录`）；其它 3 份（含 §2.2 的 W2/W3 产物）删掉 ⇒ **只有 `（未找到副本）`，无红** | 原生件的拷贝点是 **shell `cp`**，没有声明式来源：`applocal-expect.py:11-13` 与 `#UNKNOWN` 分支（`:265-271`）**自述**这条边界；`check-applocal-sync.sh:262` 的 `UNEXPECTED` 判定**前置 `is_managed`**（`*.dll`）⇒ **`.so` 永远不进那一段** |
| 4 | **`EXPECT=UNKNOWN` 的件（今天只有 `wpfgfx_cor3.so`）** | `ITEMS` 里它权威为空 ⇒ `EXPECT=UNKNOWN`；`APPSYNC` **仍然可能 PASS**（`NOINFO` 只在"整个期望集算不出来"时才触发，`check-applocal-sync.sh:610-611`） | 设计如此（"算不出的件"逐条印，但不改总判定）。⇒ **"某一件算不出期望"不会让校验器红**，只有"**全模型**算不出"才会 `NOINFO`+rc=3 |
| 5 | **`SCAN_ROOTS` 之外的副本** | `build/MilBridge/.artifacts/**`（`libwpfwic.so` `03b67fbcd7c385b6`、`wpfgfx_cor3.so` `caf7baf9e67719aa` 全仓**只此两份**）、runner 的运行目录 `$OUT`（`$HOME/wfp-runs/**`）、`tools/**` | `check-applocal-sync.sh:93` 的 `SCAN_ROOTS` 默认 `build:tests:samples:src`；`applocal-expect.py:137` 的 `/.artifacts` 排除线；`check-applocal-sync.sh:287` 的 `find` 只在这几个根下走 |
| 6 | **MSBuild `<Copy>` 的目标目录**（2 处 `SyncProviderAuthority`） | §2.5 补扫 4 条里的 2 条 | **不判**（但**无害**：目标目录都在期望集里，且它们无条件拷权威）。⇒ 这一条是"**不判但也不会错**"，与第 1/3 条性质不同，我特意分开列 |
| 7 | **4–5 个 Windows DLL 别名影子副本** | `uxtheme.dll`/`wtsapi32.dll`/`shell32.dll`/`PresentationNative_cor3.dll`（源码自述"部署期止损"） | 别名不是 `ITEMS` 里的名字 ⇒ **既不在期望集、也不在 `find`、也不在 `UNEXPECTED`**（`UNEXPECTED` 还要过 `is_managed`+`in_expect`，而它们连"件"都不算）。⇒ 陈旧的 `shell32.dll` 会被 app 目录优先命中而**毫无读数** |
| 8 | **`$(...)` 里现算的路径 / 跨文件变量 / 函数参数** | 我的间接补扫**自述**只做单文件内轻量数据流 ⇒ 它给的是**下界**（13 条，不是"全部"） | `invisible_copy_sites_indirect()` 的实现边界（本文件注释里写死）。⇒ **"清单已穷尽"这句话在本轮仍然不成立，而这一点现在由仪器自己印出来了** |
| 12 | **`.so` 的"多余副本"整个类别**（本趟实测，从推断升级为已证） | 与权威同 sha 的 `libwpfwic.so` 放进启动宿主目录 ⇒ `OK`、`UNEXPECTED=0`、**rc=0**（§7.7 实验①；证据 `28-exp-unmanaged.out`） | `check-applocal-sync.sh:262` 的 `is_managed`（只认 `*.dll`）前置 ⇒ `.so` 永不进 `UNEXPECTED`；`:276` 直接 `asha = esha ⇒ OK` |
| 9 | **`REFDIR` 里"解析不掉的 HintPath"** | 本趟 `unresolved_hintpath=0`（好），但只要出现 `$(自定义属性)` 就 `continue`（`applocal-expect.py:350`（HintPath→REFDIR 那处）/`:397`（`E()` 里那处））⇒ **该目录静默降级**：既不是解析源、也不报错 | 只把 `MSBuildThisFileDirectory/WpfLinuxRoot/RepoRoot/WpfLinuxBinDir/DwRoot/WpfLinuxSelfBuiltConfiguration/RootNamespace` 七个属性做了替换（`PROPS` `applocal-expect.py:50-58`）；替换不掉的**主扫登记 `#UNRESOLVED`，但期望图里那条边就是没有** |
| 10 | **`ITEMS` 与枚举器两处硬编码表可能漂移** | — | 两处各写一份（`applocal-expect.py:42-49` / `check-applocal-sync.sh:97-104`），**对齐靠注释与人工**：`applocal-expect.py:30` 写着"由 check 的 `--list-items` 校验"，而**`check-applocal-sync.sh` 根本没有 `--list-items`**（`grep -n 'list-items' check-applocal-sync.sh` = 0 命中）⇒ **又一条"记录里写着、代码里没有"**。**本趟我没有加这条自检**（会扩大改动面），**登记** |
| 11 | **"期待集合算不出来"以外的仪器失效** | `EXPECT_OK=1` 只要求 `python3` 存在且脚本跑完（`check-applocal-sync.sh:139-140`）⇒ **脚本内部任何静默降级（例如某个 `re` 不再匹配）都会表现为"期望集合法但变小"，照样 PASS** | `EXPECT_SUMMARY` 会印 `期望副本=N`，但**没有任何"N 不得小于上一趟"的断言** ⇒ "期望集变小"读起来像好消息。**登记（未修）** |

---

## 7. 自纠、备选与歧义（纪律要求逐条留档）

1. **我改了自己第一版的补扫判据两次**（`07→09` 与 `15→20` 两组读数都在 `$HOME`）：第一版把 `HintPath`/注释/`<Message>` 这些**声明式或纯打印**行也报成外部拷贝点（**实际 85 条只读噪声、0 条真写点之外的收获**）⇒ 收窄成"只报写点"；第二版**误报** `SRC_BAK="$BAK_DIR/…"`（行内**第二个**赋值的值把第一个变量染上）⇒ 改成"只看第一处赋值的值"。**两次都记在 `applocal-expect.py` 的函数注释里**，免得后人以为是"模型本来就该这么宽/窄"。
2. **一处"我以为的写法"被自己否定**：我一度认为 `run.sh:210` 这种"多行数组 + `for f in "${pairs[@]}"`"根本抓不到，**实测抓到了**（先因为 `(?:^|[;&|(]\s*)` 与行首空白冲突而漏，去掉行首空白后命中）。⇒ **"我觉得不行"要有读数收尾。**
3. **歧义 A（`D-A1` 的"具名类"该不该仍然红）**：派单两句话表面冲突（"内容相等要进具名非静默类" / "内容不同必须硬红"）。**我的选择 = 两句话都满足的最小面**：EQ **仍然红**（仍计 `UNEXPECTED`，仍 `APPSYNC=MISMATCH`，仍 rc=1），只是**具名**。理由：`D-A1` 的登记原文说"**不许把内容恰好对当成常绿理由**"，而 `check-applocal-sync.sh:33-34` 的既有口径是"**同类加载源只要 sha 不同就是红的（唯一非红的是按构造不是同一个东西的类）**"⇒ 把 EQ 变绿需要**新造一个"非红"类**，那是口径放宽，**我没有权限也没有依据**。**若主控裁定 EQ 应当不红**：改动点只有一处（把 `CNT_UNEXPECTED` 的累加从 EQ 分支里拿掉 + `check-applocal-sync.sh:612`/`:613` 的判定式加一条例外），**我可以照单执行，但需要明确书面裁定**。
4. **歧义 B（`DECL-GAP-EQ` 是不是"传递依赖"）**：我**没有**做"传递依赖闭包"（没有解析 `deps.json`／RAR 的 `ResolveAssemblyReference` 语义）。理由：那需要建模 NuGet/RAR，风险高、无法在两小时内自证。⇒ 名字取"**声明图缺口**（`DECL-GAP`）"而不是"传递依赖"：**它描述的是"模型看不见它"，不是"我能证明它是传递来的"**。**这一条是刻意的措辞保守**。
5. **备选方案（被我否掉的）**：把 `ITEMS` 扩成"扫描全部 app-local DLL 并按各工程输出目录找权威"。**否掉的理由**：① 会立刻把 §6 第 2 条那 15 个件变成一大批红（**当前树上是"另一条车道刚重建权威"造成的**），而"谁该清"没有 owner ⇒ 制造一批**没人认领的红**，正是纪律 13 的现场；② 它**不再是"只加一个类"的最小改动**，会改判定面 ⇒ 必须由主控与 T2 裁定口径。**我把证据（§6 第 1/2 条的实测读数）交给主控，不自己改口径。**
6. **未测的事（不许读成绿）**：① 我没有验证"把 `D-A1` 那份**写进 `FallbackCriteria.csproj`** 之后它是否转 `OK`"（那要改 `build/DirectWrite.Linux/**` 的 csproj，**越界，且会动别的车道的文件**）⇒ **`未测`**；② §2.3 的 R13/R14 两行**我没读出行号**；③ `libwpfwic.so` 在 runner 运行目录（`$OUT`，仓外）里的副本**没有权威判定**这件事，我只核了"仓内 3 份 sha 相同"，**没有跑 runner 去看 `$OUT` 里的那一份**。
7. **两条"原以为未测、本趟补做了"的实验**（都在 `$HOME` 沙箱里跑，`AUTH_ROOT`/`SCAN_ROOTS`/`HINTPATH_ROOTS` **全部指向沙箱**，**没碰真树**；沙箱跑完已 `rm -rf`，见 §5.5）：
   * **实验①"原生件（`.so`）的未声明多余副本能不能落 `UNEXPECTED`"** ⇒ **不能，已证（rc=0）**：把**与权威同 sha** 的 `libwpfwic.so` 放进一个**有 `runtimeconfig.json` 的启动宿主目录**（该目录不在期望集里）⇒ 实读
     ```
     OK            …/expt/App/bin/Debug/libwpfwic.so   03b67fbcd7c385b6
     计数：OK=… UNEXPECTED=0[DECL-GAP-EQ=0 DECL-GAP-DIFF=0] …        rc=0
     ```
     **机制**：`check-applocal-sync.sh:262` 的 `is_managed "$f"` 前置（只认 `*.dll`）⇒ **`.so` 走不到 `UNEXPECTED` 分支**，直接落到 `:276` 的 `asha = esha ⇒ OK`。
     ⇒ **§6 第 3 条由"代码推断"升级为"已实测"**：原生件的"多余副本"是**完全不判**的类（连"具名 + 红"都没有）；它的"陈旧"只能靠跨副本 `DIVERGENT`（要求**同配置下 ≥2 份且 sha 不同**）。
   * **实验②"权威目录那条 `#EXPECT`（权威枚举）到底惰性不惰性"** ⇒ **没能隔离，如实报"未测"（不是绿）**：`AUTH_ROOT` 换成自建沙箱（里面放 `ReachFramework.dll` 与 `WpfGfx.Linux.dll` 两份权威），把 `WpfGfx.Linux.dll` 的副本放进**另一个权威目录**（期望集里确实有"权威件本身的目录（权威枚举）"这一条）⇒ 它被 **`LIB-COPY` 先接走**：
     ```
     LIB-COPY      build/ReachFramework.Linux/bin/Debug/WpfGfx.Linux.dll  0c597fb6ec1eec70（库输出目录无 runtimeconfig.json ⇒ 不是启动宿主 ⇒ 与权威一致）
     rc=1   ← 红的是沙箱自己构造出来的 MISSING=3，不是这一条
     ```
     **机制**：`:243-248` 的 `LIB-COPY` 判定**排在 `:262` 的 `UNEXPECTED` 之前**，而权威目录都是库输出目录（无 `runtimeconfig.json`、也不被 HintPath 引用）⇒ **那条期望在这条判定线上必然被遮蔽**。**结论只写到**："这条期望条目在这条判定线上**不可达**；**它自身是否惰性我没有隔离出来**（要隔离得先关掉 `LIB-COPY`，那是改判据，不在本轮范围）。"
8. **"记录里写着、代码里没有"一条（新发现）**：`applocal-expect.py:30` 的注释"由 check 的 `--list-items` 校验"，而 `check-applocal-sync.sh` 里 `--list-items` **0 命中** ⇒ **两条硬编码表的对齐今天只靠人工**（§6 第 10 条）。
9. **归属与纪律**：本趟只写 `build/DirectWrite.Linux/wic-shim/{applocal-expect.py,check-applocal-sync.sh}` + 本报告；**没碰** `docs/**`、`verify-all.sh`、`build/{close-wave,integration-wave}.sh`、`build/MilBridge/**`、`build/shims/**`、`src/**`、`samples/**`、`build/{PresentationCore,WindowsBase,PresentationForce}.Linux/**`，**没有改任何编译产物**。检查器是 `check-applocal-sync.sh` 的**调用方**（`close-wave.sh:159`、`publish-milbridge.sh:102`）按 `APPSYNC=PASS` 与否判读的 ⇒ **本改动对它们透明**；但**它会改变 `verify-all`/波日志里的字面文本**（多一行说明、多一方括号），**凡"逐字比对旧日志"的判据需要知道这件事**。
10. **落地下一步（给主控）**：① 若要把 §6 第 1/2 条（`ITEMS` 只覆盖 5 件）修掉，需要先裁"**哪些件进 `ITEMS`、谁负责清红**"；② 若要给 `DECL-GAP-EQ` 一个"不红但登记"的出口，需要书面裁定（我给出单点改动位置）；③ `tests/**` 的**别名影子副本**（§2.5）与 `run-wpftextdemo.sh:191` 的**桥还原**是两条独立的真洞，owner 分别是 T3 与主控。

---

## 8. 本报告自身读数（**唯一一次追加，此后停手**）

```
$ sha256sum build/DirectWrite.Linux/TAPPS-blind-half-report.md | cut -c1-16 ; stat -c '%y %s' <同路径>
$ cat /proc/loadavg ; free -m | sed -n 2p ; date '+%F %T %z' ; uname -r
```
* **报告文件** sha16 / size / mtime ⇒ **见下行"停手读数"**（§5.5 里嵌的那个值在追加本节之前，**以此处现算的为准**；这是自指陷阱，不是我藏错）。
* **停手读数**：`lane=TAPPS`｜`2026-09-15 18:53:5x +0800`｜`loadavg` / `mem_available` / `kernel` 见命令输出（同批实测 `loadavg 1.60 1.91 1.78`、`mem_available 3836 MB`、`kernel 6.8.0-138-generic`）。
* **改动清单（三件，全是我的写域）**：
  1. `build/DirectWrite.Linux/wic-shim/applocal-expect.py` — `7c131b3f33b7e74e` → **`6eafbea14e7ea41e`**（13,857 → 27,314 B）
  2. `build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh` — `3ba5284bad838b14` → **`aad23482f84bdf44`**（44,738 → 55,520 B）
  3. `build/DirectWrite.Linux/TAPPS-blind-half-report.md` — **新增**
* **没有改任何编译产物**；真树上被覆盖过的唯一一个文件（`FallbackCriteria/bin/Debug/WpfGfx.Linux.dll`）已 **`cmp` rc=0 + sha16 `0c597fb6ec1eec70`** 回到"内容与权威逐字节相同"（**mtime 变为 `18:47:37`，不声称保住**）。
