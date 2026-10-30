# 在册红 —— `#49` `C1` 把 `PresentationFramework.dll` / `WindowsBase.dll` 纳入校验器后**首次进入判定**的 app-local 副本（**登记 ≠ 已容忍**）

- **车道**：`W52C`｜**首次登记**：2026-09-20 10:15（+0800）｜**预登记**：`docs/WAVE49-PREREGISTRATION.md` §3 `C1`
- **判据件**：`build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh`（`ITEMS` 6 条 → **8 条**）＋ `applocal-expect.py`（同一张表，`--list-items` 逐项校验 `ITEMS_SYNC=YES`）
- **裁决依据**：用户 2026-09-16 对 `D-A2`（PC 那一格）的裁决 —— 「**先登记为在册红、判据立刻上线**（不先把它们刷绿）」；本册沿用同一裁决，因为 `C1` 是 `D-A2` 的同族缺口。
- **登记时权威**（本册全部"红"的唯一裁判；现场 `sha256sum`）：

| 件 | 权威路径 | 登记时权威 sha16 | 字节 | mtime |
|---|---|---|---|---|
| `PresentationFramework.dll` | `build/PresentationFramework.Linux/bin/Release/PresentationFramework.dll` | `1011da6390c3bf1e` | 6,119,424 | 2026-09-20 00:13:43 |
| `WindowsBase.dll` | `build/WindowsBase.Linux/bin/Release/WindowsBase.dll` | `79740e9ba7fbf9ca` | 1,111,552 | 2026-09-20 00:12:23 |

> ⚠️ **配置口径**：权威件的配置**跟随唯一声明** `build/SelfBuiltConfig.props:28`（现场 = **`Release`**；`check-applocal-sync.sh` 经 `build/selfbuilt-config.sh` 读同一份声明）。
> ⚠️ **"登记时权威 sha"是快照，不是常量**：权威一换世代，**哪些副本算"陈旧"会整体重算**。引用本册前先现场重算（§5 第 1 条）。

## 0 · 这份登记**不是**豁免机制（与 `known-red-PC-copies.md` 逐条同源）

1. 检查器**没有**"在册红 ⇒ 变绿"的机制：本表**不参与判定** —— 不改任何 `CNT_*`、不改 `rc`、不改任何目录。
2. 检查器对本表**只读只打印**（`show_registry()`）：逐条印 `[在册红]` / `[在册红·已转绿]`（后者 = 该份内容已等于**当前**权威 ⇒ **应从本表移除**）。
3. **`APPSYNC` 仍是 `MISMATCH`、`rc` 仍是 1**。`build/close-wave.sh` 里 `APPSYNC` 是**告警不是硬闸** ⇒ 本波之后它会长期打印 `⚠️ APPSYNC 非 PASS`，那是**登记在册的**红，不是新问题。
4. 本表的"处置"列写的是**应该怎么收敛**，**不是**"已经收敛"（本车道**一份副本都没刷**，理由见 §6）。

## 1 · 表 A：`PresentationFramework.dll` 副本（逐份判定 = `STALE`，红）

- 现场读数：**2026-09-20 10:41 +0800**（`bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh`，`rc=1`）
- 判定行原文形态（`scan()` ⑥ 分支）：`STALE  <路径>  EXPECT 1011da6390c3bf1e  ACTUAL <sha>（副本早 N 秒 ⇒ 必须刷新）`
- **共 7 份**（不含 `CycleStub.PresentationFramework.Linux` 的 13,824 B 桩件 —— 那 4 份走 `SKIP(stub)`）
- ⚠️ `W52C3`（`C1e`）后**本表变动 1 份**：`…/PresentationFramework.Classic.Linux/bin/Debug/PresentationFramework.dll` **已改判 `LIB-COPY`、不再计红**（⇒ 移出本表，进 §6.2）；同时**新增** `…/PresentationFramework.Classic.Linux/bin/Release/PresentationFramework.dll`。

- **⏪ `#50` 波尾（2026-09-22，车道 `W95A`）：本表已按自己的口径**（"已转绿 ⇒ 应从本表移除"）**删掉全部 38 条**
  （现场由 `check-applocal-sync.sh` 逐条判 `[在册红·已转绿]`；删前本表 **38 条：仍红 0 ｜ 已转绿 38**）⇒ 本表**已无在册红**。
  原因 = 本波整波重建 ＋ 波尾 `sync-applocal-authority.sh` 把那些 PF/WB 副本都刷成了**新权威**（`pf f34bc297d19778fd`／`wb 2e4e46e539a72cd7`）；删表**不参与判定**。
| 路径 | 登记时副本 sha16 | 登记时权威 sha16 | 类别 | 首次登记日期 | 处置 |
|---|---|---|---|---|---|

## 2 · 表 B：`WindowsBase.dll` 副本（逐份判定 = `STALE`，红）

- 判定行原文形态同上（权威 `79740e9ba7fbf9ca`）
- **共 28 份**。其中**值得单独点名的一对**（预登记 §3 `C1` 的「附带判据」，措辞已在 §6.1 更正）：
  `build/PresentationFramework.Linux/bin/Debug/WindowsBase.dll` = `b39730566b1b480f`
  ≠ `build/WindowsBase.Linux/bin/Debug/WindowsBase.dll` = `19de048ecb968daa` ⇒ **连「框架件自己的输出目录」都落后**；
  这两处**都在** csproj 的 HintPath 引用面上（其 `bin/Debug` 是**写死**的 HintPath ⇒ `C1e` 之后**照样**是解析源）⇒ 两趟读数都判红。
- ⚠️ `W52C3`（`C1e`）后**本表变动 3 份**：`…/PresentationFramework.Classic.Linux/bin/Debug/WindowsBase.dll` **已改判 `LIB-COPY`、不再计红**（进 §6.2）；**新增** `…/PresentationFramework.Classic.Linux/bin/Release/WindowsBase.dll` 与 `build/DirectWriteForwarder.Linux/bin/Release/WindowsBase.dll`。

| 路径 | 登记时副本 sha16 | 登记时权威 sha16 | 类别 | 首次登记日期 | 处置 |
|---|---|---|---|---|---|

## 3 · 表 C：**不在声明图里**的 PF/WB 副本（`#49` 修后 = `UNEXPECTED-EQ` ⇒ 内容已对，**声明缺口仍在，仍红**）

落点全是 `samples/ThirdPartyMini`：该样例**真的带了这两件的 app-local 副本**（Debug + Release 各一份），但引用点**没有进声明式模型**
（`applocal-expect.py` 的 `<Reference>/<HintPath>/<ProjectReference>` 闭包算不出它）⇒ 既不是「陈旧件」、也**不许当绿**。
（`W52C3` `C1e` 前后**每格不变**：4 条、类别、sha 全同。）

### 3-Z · 【`#49` 主控处置（2026-09-21 01:xx）】**根因已定并已修** —— 读数逐条见 `docs/WAVE49-PREREGISTRATION.md` §13.4 ②

**根因不是"该不该带 PF/WB"，也不是"要不要刷新"，而是 `build/third-party/WpfLinux.props` 自己发了一份配置声明**
（`WpfLinuxBuildConfiguration` 默认 `Debug`），与全仓**唯一声明** `build/SelfBuiltConfig.props`
（`WpfLinuxSelfBuiltConfiguration` = **Release**）**分叉** —— 正是 `#39` 那份文件头警告的"同一语义两处声明"族。
现场机制：`run-thirdparty-mini.sh` 用 `-c $SELFBUILT_CONFIG`（Release）构建**应用**，却没传本配方的框架配置
⇒ 引用的是 **Debug** 自产件 ⇒ 副本内容**必然** ≠ 权威（`e9ea2f57c8a36e7d` vs `1011da6390c3bf1e`）。
修法 = 配方 **import 唯一声明处并从它派生**（保留显式覆盖这个"故意跨配置"的口子；覆盖后工具链照样把跨配置副本判红 `CROSS-CONFIG`）。
修后**两个配置各重编一次**（`-c Release` ＋ `-c Debug`，各 5 s / 2 s，零警告零错误）⇒ 四份副本 sha **逐条 == 权威**，
检查器当场 `DECL-GAP-DIFF 4 → 0`、本节 4 条 `仍红 42 → 仍红 38 ｜ 已转绿 4`。

**⚠️ 但这四条并没有"转绿"**（如实记）：它们从「**内容不同**的未声明副本」（`DECL-GAP-DIFF`，硬红）变成
「**内容与权威相同**、但仍不在声明图里」（`DECL-GAP-EQ`）—— 按现行口径 `UNEXPECTED>0` **照样判红**（"不许当绿"）。
⇒ 所以本表**不删**（删了就等于把这 4 条**在册红**洗成"不存在"），改写为**现状 + 剩余待办**。
**剩余待办（留给 `#50`，不许在本波为了归零而扩/删声明）**：**补声明图** —— `applocal-expect.py` 的引用点闭包
算不出"只经 `build/third-party/WpfLinux.props` 接线"的样例（样例自己**不 import** `BuildHygiene.props`，
而两条 import 图才是该模型的入口）。

**【本表已于 `#49` 移除（4 行 ⇒ 0 行）】** 依据 = 读取器 `show_registry()` 自己的口径（`check-applocal-sync.sh:1099-1102`）：
**"当且仅当该副本 `sha == 现权威` ⇒ 计 `已转绿`、应从登记表移除"**。修后四份副本逐条 == 权威 ⇒ 按该口径移除。
⚠️ **这不是洗白**，两点如实记：① 移除**不参与判定**（该段自己就写着"登记 ≠ 已容忍"，`rc` 只由五个计数器决定）；
② 这 4 条**没有变绿**，它们是转成了另一个**仍然判红**的类别 —— 详见下面的"剩余"。

**剩余（`#50` 待办，不许在本波为了让计数归零而扩/删声明）**：这 4 条现在是
`UNEXPECTED-EQ`（**内容 == 权威、但不在声明图里** ⇒ `DECL-GAP-EQ`，按现行口径 `UNEXPECTED>0` **照样判红**）。
更完整地说，`samples/ThirdPartyMini` 现在一共 **10 条 `UNEXPECTED-EQ`**（PF/WB ×2 配置 ＋ PC/ReachFramework/Provider/libwpfwic 等），
而全仓 `DECL-GAP-EQ` 已从 **12 涨到 16** —— 其中**只有 1 条**（`build/DirectWrite.Linux/FallbackCriteria/bin/Debug/WpfGfx.Linux.dll`）
在 `samples/WpfFeatureProbe/KNOWN-DEFECTS.md` 的 `D-A1` 条里登了记 ⇒ **剩下 15 条是"红而无登记"**。
⇒ **要办的是"补声明图"**（让 `applocal-expect.py` 的引用点闭包覆盖"只经 `build/third-party/WpfLinux.props` 接线"的样例），
**不是**把 `UNEXPECTED` 计数调小、也不是删 `ITEMS`。这条已写进波预登记 `docs/WAVE49-PREREGISTRATION.md` §13.4 ②。

### 3-A · `C1e` 新增的**非 PF/WB** 3 条（`STALE`，红）—— **跨册登记**

这 3 件的**主册**是 `known-red-PC-copies.md`（`PresentationCore.dll` / `ReachFramework.dll` / Provider 都在 `ITEMS` 里由 `#23`/`T2` 纳入），
但那份册子的写域**不在本车道**；`W52C3` 派单书判据 4 要求「每一条新红逐条登记进本册」⇒ 在此**跨册登记**，
并提请主控决定是否**移入 PC 册**（两处都留会让同一份红在两条 `[在册红]` 行里出现）。

| 路径 | 登记时副本 sha16 | 登记时权威 sha16 | 类别 | 首次登记日期 | 处置 |
|---|---|---|---|---|---|

## 4 · 表 D：跨副本一致性（**7 个** `DIVERGENT` 组）—— **信息性**（第 2 列不是 sha ⇒ 本表**不被** `show_registry()` 当登记条目解析）

| 件 | 配置 | 组内 sha 种数 | 组内成员数 | 现场「落单」成员（sha 只此一份） |
|---|---|---|---|---|
| `PresentationFramework.dll` | `Release` | 2 | 5 | `samples/ThirdPartyMini/bin/Release/net10.0/PresentationFramework.dll` `e9ea2f57c8a36e7d` |
| `PresentationFramework.dll` | `Debug` | 5 | 8 | `build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/PresentationFramework.dll` `bfb10fe2a01a986b`；`samples/WpfFeatureProbe/bin/Debug/net10.0/PresentationFramework.dll` `a93097f7a918597f`；`samples/WpfTextDemo/bin/Debug/net10.0/PresentationFramework.dll` `37fd347eb55ca02e` |
| `WindowsBase.dll` | `Release` | 6 | 18 | `build/MilBridge/tests/StrictTierProbe/bin/Release/WindowsBase.dll` `1114a28ec5a03ab7`；`build/MilBridge/tests/MinMaxProbe/bin/Release/WindowsBase.dll` `e6216fe961a2bfb9`；`build/MilBridge/tests/TabGapProbe/bin/Release/WindowsBase.dll` `84a2826c471e60ea`；`samples/ThirdPartyMini/bin/Release/net10.0/WindowsBase.dll` `19de048ecb968daa` |
| `WindowsBase.dll` | `Debug` | 10 | 24 | `build/MilBridge/tests/ProductEntryArm/bin/Debug/WindowsBase.dll` `79740e9ba7fbf9ca`；`build/PresentationFramework.Linux/bin/Debug/WindowsBase.dll` `b39730566b1b480f`；`build/DirectWrite.Linux/SystemFontsProbe/bin/Debug/WindowsBase.dll` `dc8d9c25a43d0660` |
| `PresentationCore.dll` | `Release` | 2 | 21 | **（`C1e` 新增组）** `build/PresentationFramework.Classic.Linux/bin/Release/PresentationCore.dll` `659a5dc64156b26a` |
| `ReachFramework.dll` | `Release` | 2 | 7 | **（`C1e` 新增组）** `build/PresentationFramework.Classic.Linux/bin/Release/ReachFramework.dll` `33b372daa826733e` |
| `DirectWrite.Linux.Provider.dll` | `Release` | 2 | 29 | **（`C1e` 新增组）** `build/PresentationFramework.Classic.Linux/bin/Release/DirectWrite.Linux.Provider.dll` `9aa0d744802aaa31` |

> 组内**多数成员**是「陈旧的副本」——PF/WB 那 4 组的成员**同时**被 §1/§2 逐份判红；本表只补「同组之间互相不一致」这一层信息（**计数是组数，不是份数**）。
> ⚠️ 后 3 组是 `W52C3`（`C1e`）的**直接产物**：`bin/Release` 那一族目录改由声明值决定后**首次成为解析源** ⇒ 首次进分组。
> 它们的落单者**都是同一份** `build/PresentationFramework.Classic.Linux/bin/Release/<件>`——该工程是**另一份框架构建**（`PresentationFramework.Classic`），
> 它随包带的是**自己产出的**同名件（sha 与真权威不同）⇒ 逐份判 `STALE`（已逐条登记在本册表 B/§3-A 之外的那 3 行，见 §5 的「`C1e` 新增」行）。

## 5 · 汇总（来源 = 同一趟读数）与复算命令

| 类别 | 份数/组数 | 说明 |
|---|---|---|
| `STALE`（**红**） | **38**（PF 7 + WB 28 + 其它件 3） | `W52C` 首次登记时是 34（PF 7 + WB 27）；**`W52C3`（`C1e`）净 +4**：新增 6、改判掉 2（见 §6.2） |
| `UNEXPECTED-DIFF`（**硬红**） | **0**（登记时 **4**） | `#49` 主控修（配方配置分叉）后 `DECL-GAP-DIFF 4 → 0`；那 4 条**转成** `UNEXPECTED-EQ`（仍红：内容对、声明缺）⇒ 见 §3-Z 的"剩余" |
| `DIVERGENT`（**红**） | **7 组** | 表 D（`C1e` 前 4 组 ⇒ **新增 3 组**） |
| `NEWER-DIFF`（**红**） | **0** | 两趟都没有「副本不早于权威」的条目 |
| `MISSING`（**红**） | **0** | 完好树上无缺件（缺件判据另有反极性读数：删 PF/WB 各一份 ⇒ 具名 `MISSING=2` + `rc=1`，见 `build/MilBridge/W52C-report.md` §4） |
| `CROSS-CONFIG`（**不判红**，`D-G62`） | **101** | `C1e` 前后**逐字未变**（该格数的是「枚举到的、配置≠声明的副本」，与 `REFDIR` 无关） |
| 判定计数器总量变化（`W52C` 起点 → 现在） | `OK 116→141`；`MISMATCH 1→39`；`UNEXPECTED 12→16`；`DIVERGENT 1→7`；`LIB-COPY 21→23`；`SKIP(obj) 10→14`；`SKIP(stub) 12→20` | 其中 `C1e` 那一步：`OK +6`／`MISMATCH +4`／`LIB-COPY −10`／`DIVERGENT +3`（§3/§6.2 逐格） |

```bash
# 1) 现场重算权威（本册全部"红"的裁判）
sha256sum build/PresentationFramework.Linux/bin/Release/PresentationFramework.dll build/WindowsBase.Linux/bin/Release/WindowsBase.dll | cut -c1-16
# 2) 全量读数（rc=1 是**预期**：本册的红照样计）
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh | grep -E '^计数：|^APPSYNC='
# 3) 只取本册的表 A/B（逐份 STALE）
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh | grep -E '^    STALE' | grep -E 'PresentationFramework\.dll|WindowsBase\.dll'
# 4) 表 C/表 D
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh | grep -E '^    UNEXPECTED-DIFF' | grep -E 'PresentationFramework\.dll|WindowsBase\.dll'
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh | grep -E 'DIVERGENT +(PresentationFramework|WindowsBase)\.dll'
# 5) 两张权威表是否一致（本波改了**两处**，必须一起改）
bash build/DirectWrite.Linux/wic-shim/check-applocal-sync.sh --list-items | tail -1   # 期望 ITEMS_SYNC=YES
```

## 6 · 边界与更新（`W52C2` 2026-09-20 追记；**处置与判据现状**）

> **① 主控裁定（`docs/WAVE49-PREREGISTRATION.md` §10 裁定 ①，2026-09-20）**：口径 = 「**权威即声明配置，副本必须与权威同代**」
> ⇒ 表 A/B 这 **34 条 `STALE` 是**真红且应刷**」，处置时机 = 波的 `3.5`（`sync-applocal-authority.sh --apply`），
> **本车道仍不手工 `--apply`**。依据：PC 那本册子已把 12 份"Debug 目录里装 Release 内容"读成
> `[在册红·已转绿]＋应从登记表移除` ⇒ 收敛到声明配置是**既成口径**。
> **② 主控裁定（§10 裁定 ②，新项 `D-G62`）**：旧格 `NO-AUTHORITY` 是**死格**（条件 `is_release_path ∧ is_debug_auth`
> 在 Release 权威下恒假）⇒ 已**改成具名诊断格 `CROSS-CONFIG=<n>`**（条件 = **副本路径的配置 ≠ 声明配置**）；
> 它**仍不判红**，且**跨配置副本照样计入 `STALE`**（旧行为是 `continue` **豁免**，新行为**不放行** ⇒ 方向是**加严**）。
> ⇒ 本册 §6 旧文第 3 条（"该不该红待裁定"）**已被裁定 ① 回答**；第 3 条里引用的 `NO-AUTHORITY` 一词**已不存在**
> （`W52C2` 现场：`grep -c 'is_debug_auth\|CNT_NOAUTH' check-applocal-sync.sh` = 0，只剩两处**说明性**的旧名）。
> **③ 实测（`W52C2` 现场）**：改格前后 `APPSYNC=` 行**逐字相同**、计数行**只多一格** ⇒ 本格**没有**新增红、也**没有**压掉任何红。

### 6.1 逐条：本册哪些行**只**因 `applocal-expect.py:88` 的硬编码 `Debug` 才被判

`applocal-expect.py` 的 `PROPS` 把 `$(WpfLinuxSelfBuiltConfiguration)` **替换成硬编码 `Debug`**（`:88`），
而声明配置是 `Release` ⇒ `REFDIR`（"被 HintPath 引用 ⇒ 解析源"的那张表）**按 Debug 口径拼出来**。
现场量化（同一个工具，仅把该行换成跟随声明；两趟都跑真仓）：

| 量 | 现件（硬编码 Debug） | 换成跟随声明 |
|---|---|---|
| `#REFDIR` 条数 | **14** | **23** |
| `#EXPECT` 行差异 | — | **0 行**（`expect=190` 不变） |
| 只在现清单里的目录 | `build/PresentationFramework.Classic.Linux/bin/Debug`（唯一 1 个） | — |
| 只在跟随声明清单里的目录 | — | 10 个（`build/*/bin/Release` 一族） |

对本册 34 条 `STALE` 逐行归因：

| 归因 | 条数 | 含义 |
|---|---|---|
| 副本所在目录是**启动宿主**（有 `*.runtimeconfig.json`） | **24** | 与 `REFDIR` 无关 ⇒ 判红**不依赖** `:88` |
| 目录在**两种** `REFDIR` 里都在（因为还有 csproj 写死 `bin/Debug` 的 HintPath） | **8** | 同样不依赖 `:88` |
| **只**因 `:88` 的 Debug 口径才成为"解析源" | **2** | `build/PresentationFramework.Classic.Linux/bin/Debug/{PresentationFramework,WindowsBase}.dll` —— 若 `:88` 跟随声明，这两份会退成 `LIB-COPY`（**不再判**） |

⇒ **34 条里 32 条与 `:88` 无关**；`W52C2` **没有**改 `:88`（它不在裁定 ③ 的改法里），只把影响**量化并上交**（报告 §6.3）。
⚠️ 注意方向：把 `:88` 跟随声明会**新增 10 个 Release 解析源** ⇒ 那多半是**加严**（更多非宿主目录进入判定），不是放松。
✅ **`W52C3` 已按主控 §10.5 裁定（新项 `C1e`）把 `:88` 改成跟随声明** ⇒ 上表第 3 行那 2 份的**实际结局**见 §6.2（**改判 `LIB-COPY`、不再计红**），逐格读数见 §6.3。

### 6.2 ⚠️ `W52C3`（`C1e`）后**改判**的 2 行（**不再是红**，但**仍具名可见**）—— 与报告判据 3 的冲突，如实列出

裁定 `C1e` 把 `:88` 改成**跟随声明**（现场 `Release`）后，下面两份**不再**被当成「解析源」⇒ 按 `LIB-COPY` 口径（`:411`：「库输出目录无 runtimeconfig ⇒ 不是启动宿主 ⇒ 私有依赖副本不是加载源」）**不判定**：

| 路径 | 副本 sha16 | 改前判定（`W52C2`） | 改后判定（`W52C3`） | 改后**仍在输出里的行** |
|---|---|---|---|---|
| `build/PresentationFramework.Classic.Linux/bin/Debug/PresentationFramework.dll` | `68d31452be50a89c` | `STALE`（**红**） | `LIB-COPY`（**不判**） | `CROSS-CONFIG …（副本配置=Debug ≠ 声明配置=Release…）` ＋ `LIB-COPY …（…与权威不同，但不是加载源）` |
| `build/PresentationFramework.Classic.Linux/bin/Debug/WindowsBase.dll` | `1b4ef36832ddca03` | `STALE`（**红**） | `LIB-COPY`（**不判**） | 同上 |

⇒ **它们没有变「看不见」**（两行都印出来了、sha 也印了），但**不再计入 `MISMATCH/STALE`**（`STALE 35→39` 里已经**扣掉**这 2 份）。
⚠️ **这与 `W52C3` 派单书的判据 3（「那 2 条改后仍必须被判」）冲突**，两条读法都摆出来：
  · 按「**具名可见**」读 ⇒ **满足**（`CROSS-CONFIG` ＋ `LIB-COPY` 两行俱在，sha 可核）；
  · 按「**仍计红**」读 ⇒ **不满足**（−2 `STALE`）。要满足它必须**加一条新口径**（例如「跨配置副本即使落在库输出目录也照判」），
    那是**判据变更**，不是本车道的授权 ⇒ 已上报等裁定（报告 §4）。
本册因此把这 2 行**移出解析表**（`show_registry()` 只认第 2 列为**裸** 16 位 hex 的行；本节的第 2 列带反引号 ⇒ **不被当登记条目**），
而**保留在本节**作为历史与证据；它们的处置是「**已改判为 `LIB-COPY`，勿再刷**」。

### 6.3 `W52C3`（`C1e`）落地读数（逐格）

| 量 | `C1e` 前（`W52C2` 末态） | `C1e` 后（`W52C3`） |
|---|---|---|
| `#REFDIR` 条数（真仓，声明 `Release`） | **14**（全 `…/bin/Debug`） | **23**（13 `…/bin/Debug` ＋ 10 `…/bin/Release`） |
| `#REFDIR` 逐目录 diff | — | **仅改前 1 个**（`build/PresentationFramework.Classic.Linux/bin/Debug`）／**仅改后 10 个**（`build/*/bin/Release` 一族） |
| `#SUMMARY` `expect=` | 190 | **190**（不变） |
| `MISMATCH`(`STALE`) | 35 | **39**（**+4**） |
| `OK` | 135 | **141**（+6） |
| `LIB-COPY` | 33 | **23**（−10） |
| `DIVERGENT` | 5 组 | **8 组**（+3） |
| `CROSS-CONFIG` | 101 | **101**（不变） |
| `MISSING`／`UNEXPECTED`／`SKIP(*)`／`AUTH-MISSING`／`BRIDGE-*`／`RETIRED` | 0／16／…／0／0／0 | **逐字不变** |
| `rc` / `APPSYNC` | 1 / `MISMATCH` | 1 / `MISMATCH`（**语义未动**） |

**判据 1 的成对读数**（同一工具、同一 `argv[1]`，只换声明）：

| 运行 | 声明 | `#REFDIR` 条数 | 以 `bin/Debug` 结尾 | 以 `bin/Release` 结尾 | 清单 sha12 |
|---|---|---|---|---|---|
| 改前件 | `Debug` | 18 | 18 | 0 | `068b2c684d27` |
| 改前件 | `Release` | 18 | 18 | 0 | `3d7afe5cba1c` |
| 改后件 | `Debug` | 18 | 18 | 0 | **`068b2c684d27`（与改前件@Debug **逐字相同**）** |
| 改后件 | `Release` | 30 | 16 | 14 | `1de4e93acc34` |

⇒ ① **改前件在两种声明下条数相同（18/18）** ⇒ 声明被忽略（硬编码的机器证）；
  ② **改后件换声明 ⇒ 清单跟着换**（18 → 30，出现 14 个 `…/bin/Release`）⇒ 跟随声明；
  ③ **改后件@Debug 与改前件@Debug 逐字相同** ⇒ **不是「换个 Release 字面量」**（否则 Debug 声明下会变成 Release 清单）；
  ④ 剩下那 16 个 `…/bin/Debug` 来自 csproj 里**写死**的 HintPath（与配置无关，本来就该是 Debug）。

### 6.4 棘轮盲区（本趟发现，建议主控裁定）

`bash build/selfbuilt-config.sh --debt` 的判据是 **`grep -rn "bin/Debug"`（按行计数）** ⇒ 它**只看得见写死的路径**，
**看不见写死的配置值**。`:88` 那一行写的是 `"Debug"`（不是 `bin/Debug`）⇒ **本趟真正的修法（跟随声明）对它零影响**（实测：改前 140、改后仍 140）。
为满足 `W52C3` 派单书判据 5（`140 ⇒ ≤139`），我把本文件里**唯一**一处含该字面串的**散文**提及改写（意思不变、不写该串）⇒ 棘轮 140 → **139**。
**如实声明：这 1 行的减少是「措辞级」的，不是「判据级」的**；真正的修法不动这把棘轮。建议把该计数器的图案扩到配置值（例如同时数 `<WpfLinuxSelfBuiltConfiguration[^>]*>\s*(Debug|Release)\s*<` 的场景），否则**下一个同类硬编码照样能活过 `#39`**。
> **要反驳本册只需两件事之一**：① 证明"把 38 份 `cp -f` 成 Release 权威"是**要**的行为（那 §1/§2 应改判为"已收敛"，本册删除）——**主控裁定 ① 已按此方向裁定**（⇒ 本册应在波尾 `3.5` 刷新后按 §7 第 1 条**逐条删除已转绿的行**）；② 证明 `:88` 的**硬编码 Debug** 是 bug（那 `REFDIR` 整体重算，本册需重写——已量化：34 条里 2 条会因此改判）。

## 7 · 撤登记条件（本册每一条都**可反驳**）

1. 权威件换世代后，某份**内容已等于现权威** ⇒ 检查器自己会印 `[在册红·已转绿]` ⇒ **必须从本册删除**（不是"保留为历史"）。
2. `samples/ThirdPartyMini` 的引用点补进声明式模型（或该样例真的不再随包拷这两件）⇒ 表 C 那 4 条应转 `OK`/消失。
3. 若裁定"跨配置副本不该按当前口径判红"（§6 第 3 条）⇒ 本册 §1/§2/表 D **整体重算**，本文件应被重写而不是打补丁。
4. 若 `SELFBUILT_CONFIG` 切回 `Debug` ⇒ §1/§2 的"哪些份旧"**全部改变**（本册的 sha 快照全部过期）⇒ 重跑 §5 第 1/3 条后重写。
5. 本册任何一条**长期无人处置**（处置列写"刷新"却连续两波不动）⇒ 应升级为独立 `D-` 编号，而不是永远躺在"在册"里。
