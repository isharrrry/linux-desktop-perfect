#!/usr/bin/env bash
# app-local 副本**刷新器**（债务 #20："副本同步机制缺的那一条"）—— 由集成波调用。
#
# 【它补的是哪条缝】`HintPath + Private=true` 的语义是「**消费者构建时拷一次**」
#   （主控 2026-09-10 裁定；见 src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/WiringSmoke/DirectWrite.Linux.WiringSmoke.csproj:6-11）。
#   ⇒ 权威件（Provider / WpfGfx.Linux.dll / libwpf*.so）之后被重建，**没重建过的消费者**就停在旧代。
#   实测定性（主控 2026-09-14 转来 T1c §32，我对过现场）：
#     · 权威 `src/Microsoft.DotNet.Wpf.Linux/src/ReachFramework/{obj,bin}/Debug/ReachFramework.dll` 同 sha `f64b76d43d8a`(22:24:20)；
#     · app-local 四份整齐 `daf9b6f073f6`(22:23:22) ⇒ **一个副本的年龄 = 该消费者最后一次构建的时刻**；
#     · 另有 09-10 `e8819668`×3、09-11 `00723a05`×1；
#     · `src/Microsoft.DotNet.Wpf.Linux/src/CycleStub.ReachFramework/{bin,obj}` 里同名件只有 5,120 B `067c03858367`（循环桩）。
#     ⇒ 结论：**"缺一条同步"**，不是"没重编"、也不是"跨配置口径错"。
#
# 【本脚本不做判据】判据**唯一**在 `check-applocal-sync.sh` 里（十类口径）。本脚本只执行它的结论：
#   路 1：它标成 `STALE` 的副本（启动宿主 + 同配置 + mtime 早于权威 + sha 不同）⇒ 刷成权威；
#   路 2：它标成 `DIVERGENT` 的**落单者**（同配置加载源之间只有它不同，且 sha≠权威、mtime 早于权威）
#         ⇒ 同一条"落后"，只是先用分组信号发现（例如 Release 目录里那份旧 Provider）；
#         **护栏**：只对"**权威锚定**的分组"生效（组里已有一份 == 权威 sha）。若该组整个是**另一个配置**
#         的产物（如 `.artifacts/**/release*/` 里的 Release WpfGfx），把 Debug 权威拷进去就是放错配置
#         ⇒ 只 WARN，交给那条构建链自己收敛。
#   `NEWER-DIFF`（副本不早于权威）**只告警不盲拷**：谁旧谁新没定论，盲拷可能把新件盖成旧件。
#   **绝不碰**：`SKIP(obj)` / `SKIP(stub)` / `SKIP(ref)` / `LIB-COPY` / `NO-AUTHORITY`（按构造不是"同一个东西"）。
#   **绝不删除**任何文件；只 `cp -f` 覆盖，且覆盖后**当场复算 sha 并断言等于权威**。
#
# 用法：
#   bash sync-applocal-authority.sh            # 干跑（默认）：只打印"会刷什么"，一个字节都不写
#   bash sync-applocal-authority.sh --apply    # 真刷 + 刷新后再跑一次校验器并打印 APPSYNC 行
# 环境：AUTH_ROOT（权威根，默认仓库根）/ SCAN_ROOTS（冒号分隔，默认与校验器一致）
# 【`D-G91` 修复 · 车道 W113A · 2026-09-22】上面那句"默认与校验器一致"**修前是假的**：
#   本脚本 `:59` 自己写死一份**漏了 `$REPO/tools`** 的根集合（校验器的默认根里有它）⇒
#   ① 默认参数永远刷不到只藏在 `$REPO/src/Linux/tools/**` 下的 `STALE`；② 它拿收窄根打出 `STALE=0` 的**假绿**
#   （成对读数：默认＋`--apply` ⇒ `refreshed=0/STALE=0`；同刻全文口径 ⇒ `STALE=1`）。
#   现行为：不覆盖时**从判据唯一实现派生**（`check-applocal-sync.sh --print-scan-roots`）；
#   显式收窄 ⇒ `APPSYNC_ROOTS=MISMATCH` ＋ `NOINFO` ＋ `rc=2`，**在打印任何 `STALE=` 汇总之前退出**。
#
# 【打印格式（刷新前后 sha 对比）】
#   REFRESH        <相对路径>  <before16> → <after16>  （权威 <auth16>；副本曾早 <N> 秒）
#   REFRESH(group) <相对路径>  同上（来源是跨副本分组的落单者）
#   WARN           <相对路径>  副本不早于权威（<actual16> vs 权威 <auth16>）⇒ **不盲拷**，需人判
#   末尾：APPSYNC-REFRESH=refreshed=<n> newer=<m> applied=<0|1> ；随后打印校验器的 `APPSYNC=...` 行与退出码
#
# 【接进 src/Linux/build/integration-wave.sh 的确切位置（本文件不改那个脚本，由主控接线）】
#   插在 **3. 重建主循环的 `done`（现 342 行）之后、`# ---- 4. 身份一致性自检`（现 344 行）之前**，
#   即"消费者刚被重建完 ⇒ 立刻把落后的副本刷成权威"。建议片段（用波自己的 `step`/`fail` 风格）：
#
#     # ------------------------------------------------------------ 3.6 app-local 副本刷新（债务 #20）
#     # 【为什么在这里】app-local 副本的刷新时机 = 消费者自己被构建时；波的主循环刚重建完消费者
#     #   ⇒ 此刻做一次"同类 + 落后 ⇒ 刷成权威"，全仓 app-local 就与权威件收敛。
#     # 【口径唯一】判据在 check-applocal-sync.sh；本脚本只执行它标 STALE / DIVERGENT 落单者的那些。
#     step "3.6/5 app-local 副本刷新（权威件 → 落后的加载源副本）"
#     SYNC="$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/sync-applocal-authority.sh"
#     if [ -f "$SYNC" ]; then
#         bash "$SYNC" --apply || fail=$((fail + 1))
#     else
#         echo "  ⚠️ 缺 $SYNC ⇒ **无信息**（不等于通过）"; fail=$((fail + 1))
#     fi
#
# 【可证伪（两条，都实测过）】
#   ① 校验器自检 E：权威件换一个 sha ⇒ 报红；还原 ⇒ 回绿。
#   ② 本脚本 `--apply` 前后：每个 `REFRESH` 行的 after16 == 权威 sha；再跑校验器 ⇒ 那些副本变 OK / 分组变 CONSISTENT。
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHECK="$HERE/check-applocal-sync.sh"
REPO="${AUTH_ROOT:-$(cd "$HERE/../../../../.." && pwd)}"
SCAN_ROOTS="${SCAN_ROOTS:-}"        # 空 = 调用方未覆盖 ⇒ 下面**从判据唯一实现派生**（`D-G91` 修复）
APPLY=0
for a in "$@"; do case "$a" in --apply) APPLY=1;; esac; done
short() { printf '%s' "${1:0:16}"; }
sha() { [ -f "$1" ] && sha256sum "$1" | awk '{print $1}' || echo "<missing>"; }

[ -f "$CHECK" ] || { echo "❌ 找不到判据唯一实现：$CHECK" >&2; exit 2; }

echo "== app-local 副本刷新（判据唯一实现：check-applocal-sync.sh）"
echo "   权威根=$REPO"
echo "   模式=$([ "$APPLY" = 1 ] && echo '--apply（会写副本）' || echo '干跑（默认，不写任何文件）')"

# ────────────────────────────────────────────────────────────────────────────
# 【`D-G91` 修复 · 车道 W113A · 2026-09-22】**根集合派生 ＋ 自检（假绿不可能）**
#   原状：本脚本 `:59` **自己写死**一份默认根集合，且**漏了 `$REPO/tools`**（判据唯一实现
#   `check-applocal-sync.sh` 的默认根里有它）⇒ ① 默认参数**永远刷不到**只藏在 `$REPO/src/Linux/tools/**`
#   下的 `STALE`；② 更危险：它拿**收窄根**打出 **`STALE=0` 的假绿**。
#   修法三条：
#     ① **派生**：不覆盖时，默认根集合**只从判据唯一实现取**（`--print-scan-roots`，只读、不扫描）
#        ⇒ "同一条语义两处各写一份"在结构上不可能；
#     ② **自检**：调用方**显式收窄**（`SCAN_ROOTS` 非空且与判据唯一实现**不同集**）⇒
#        大声 `APPSYNC_ROOTS=MISMATCH` ＋ `NOINFO` ＋ **rc=2**，且**在打印任何 `STALE=` 汇总之前就退出**
#        ⇒ 收窄根**再也打不出 `STALE=0`**（那正是假绿的唯一出口）；
#     ③ 取不到根集合（判据件不吐）⇒ 同样**大声拒绝**（`NOINFO`，rc=2），**不许自己猜一份**。
#   ⚠️ 比的是**集合**（逐个 `realpath -m` ＋ 排序去重），不比书写顺序 —— 顺序不改变射程。
# ────────────────────────────────────────────────────────────────────────────
norm_roots() {
    local r out=""
    IFS=:; for r in $1; do [ -n "$r" ] && out="$out$(realpath -m -- "$r" 2>/dev/null)
"; done; IFS=$' \t\n'
    printf '%s' "$out" | LC_ALL=C sort -u | paste -sd: -
}
CHECK_ROOTS="$(AUTH_ROOT="$REPO" bash "$CHECK" --print-scan-roots 2>/dev/null)"; croots_rc=$?
if [ "$croots_rc" != 0 ] || [ -z "$CHECK_ROOTS" ]; then
    printf 'APPSYNC_ROOTS=NOINFO reason=checker-cannot-print-roots rc=%s check=%s\n' "$croots_rc" "$CHECK" >&2
    printf '❌ **判据唯一实现不吐根集合 ⇒ 本脚本无权自己猜一份**（旧版正是"自己写死一份"才漏了 `$REPO/tools`）⇒ 拒绝继续。\n' >&2
    exit 2
fi
if [ -z "$SCAN_ROOTS" ]; then
    SCAN_ROOTS="$CHECK_ROOTS"
    printf '   扫描根=%s（**派生自判据唯一实现** `--print-scan-roots`）\n' "$SCAN_ROOTS"
else
    if [ "$(norm_roots "$SCAN_ROOTS")" != "$(norm_roots "$CHECK_ROOTS")" ]; then
        printf 'APPSYNC_ROOTS=MISMATCH sync=%s\n' "$SCAN_ROOTS"
        printf 'APPSYNC_ROOTS=MISMATCH check=%s\n' "$CHECK_ROOTS"
        printf 'APPSYNC_ROOTS=NOINFO reason=narrowed-scan-roots（**根集合不是同一个集合** ⇒ 本趟读数不可当"全仓一致"）\n' >&2
        printf '❌ **拒绝给出任何 `STALE=` 汇总**：收窄扫描根打出 `STALE=0` 就是 `D-G91` 的假绿。\n' >&2
        printf '   收窄根看不见的副本既不会被刷、也不会被报 ⇒ 要么去掉 `SCAN_ROOTS` 覆盖（改用派生值），\n' >&2
        printf '   要么把缺的根补进判据唯一实现（`check-applocal-sync.sh` 的 `SCAN_ROOTS_DEFAULT`，**唯一定义处**）。\n' >&2
        exit 2
    fi
    printf '   扫描根=%s（调用方显式给定；**与判据唯一实现同集合** ⇒ 自检通过）\n' "$SCAN_ROOTS"
fi

# 1) 让校验器给出分类（它就是判据）——本脚本不自己重算"什么算落后"
out="$(AUTH_ROOT="$REPO" SCAN_ROOTS="$SCAN_ROOTS" bash "$CHECK" 2>&1)"; chk_rc=$?
rows="$(printf '%s\n' "$out" | awk '
    /^--- /            { item = substr($0, 5); ingroup = 0 }
    /^    权威 /       { auth[item] = $NF }
    /^    STALE /      { print "STALE\t" item "\t" auth[item] "\t" $2 "\t-" "\t-" }
    /^    NEWER-DIFF / { print "NEWER\t" item "\t" auth[item] "\t" $2 "\t-" "\t-" }
    /^    DIVERGENT /  { item = $2; ingroup = 1; next }
    /^    [A-Z]/       { ingroup = 0 }
    ingroup && /^        / { print "GROUP\t" item "\t" auth[item] "\t" $1 "\t" $2 "\t" $3 " " $4 }
')"

n_ref=0; n_newer=0; fail=0
declare -A handled=()          # 路 1 已处理过的路径：路 2 不再重复列出（干跑也一致，计数不虚高）
# 【护栏：跨配置不许盖】路 2 只允许从权威刷新的**权威锚定**分组 —— 即"该组里已经有一份等于权威 sha"。
#   为什么：`.artifacts/**/release*/` 那种**同名的另一个配置产物**（Release WpfGfx ≠ Debug 权威，
#   尺寸 323,584 vs 351,232）一旦落单，把 **Debug 权威**拷进去就是**放错配置**。这类落单者交给
#   它自己那条构建链（"下次谁构建谁收敛"），不在这里盲拷。
declare -A grp_anchor=()
while IFS=$'\t' read -r kind item asrc rel f5 f6; do
    [ "${kind:-}" = "GROUP" ] || continue
    [ "$f5" = "$(short "$(sha "$asrc")")" ] && grp_anchor["$item"]=1
done <<< "$rows"
do_refresh() {   # $1=rel  $2=asrc  $3=label
    local rel="$1" asrc="$2" label="$3" dst before after age
    dst="$REPO/$rel"
    [ -f "$asrc" ] || { printf '    ❌ 权威件不在：%s（%s）\n' "$asrc" "$rel" >&2; fail=$((fail+1)); return; }
    [ -f "$dst" ]  || { printf '    ❌ 副本不在（已被移走？）：%s\n' "$rel" >&2; fail=$((fail+1)); return; }
    before="$(sha "$dst")"
    handled["$rel"]=1
    age=$(( $(stat -c %Y "$asrc") - $(stat -c %Y "$dst") ))
    if [ "$APPLY" = 1 ]; then
        cp -f "$asrc" "$dst" || { printf '    ❌ cp 失败：%s\n' "$rel" >&2; fail=$((fail+1)); return; }
        after="$(sha "$dst")"
        if [ "$after" = "$(sha "$asrc")" ]; then
            n_ref=$((n_ref+1))
            printf '    REFRESH%-8s %-58s %s → %s  （权威 %s；副本曾早 %s 秒）\n' \
                "$label" "$rel" "$(short "$before")" "$(short "$after")" "$(short "$(sha "$asrc")")" "$age"
        else
            printf '    ❌ 刷完 sha 仍不等于权威：%s（%s → %s）\n' "$rel" "$(short "$before")" "$(short "$after")" >&2; fail=$((fail+1))
        fi
    else
        n_ref=$((n_ref+1))
        printf '    REFRESH%-8s %-58s %s → %s  （干跑；加 --apply 才写；副本早 %s 秒）\n' \
            "$label" "$rel" "$(short "$before")" "$(short "$(sha "$asrc")")" "$age"
    fi
}

while IFS=$'\t' read -r kind item asrc rel f5 f6; do
    [ -n "${kind:-}" ] || continue
    case "$kind" in
        STALE) do_refresh "$rel" "$asrc" "" ;;
        NEWER) n_newer=$((n_newer+1))
               printf '    WARN             %-58s 副本不早于权威（%s vs 权威 %s）⇒ **不盲拷**，需人判\n' \
                   "$rel" "$(short "$(sha "$REPO/$rel")")" "$(short "$(sha "$asrc")")" ;;
        GROUP) [ -n "${handled[$rel]:-}" ] && continue     # 路 1 已经处理过
               if [ "${grp_anchor[$item]:-0}" != 1 ]; then
                   n_newer=$((n_newer+1))
                   printf '    WARN             %-58s 分组落单但该组**不由权威锚定**（另一配置产物？）⇒ 不盲拷，交给它自己的构建链\n' "$rel"
                   continue
               fi
               # 落单者：先按"是否已等于权威 + 是否比权威旧"复核，再刷（避免盲拷）
               if [ ! -f "$REPO/$rel" ] || [ ! -f "$asrc" ]; then printf '    ❌ 缺件：%s\n' "$rel" >&2; fail=$((fail+1)); continue; fi
               # 用**磁盘当前** sha 复核（不信校验器输出里的旧值）：上一路刚刷过的就别再刷（幂等、计数不重复）
               if [ "$(short "$(sha "$REPO/$rel")")" = "$(short "$(sha "$asrc")")" ]; then continue; fi
               mem_epoch="$(date -d "$f6" +%s 2>/dev/null || echo 0)"
               if [ "$mem_epoch" -lt "$(stat -c %Y "$asrc")" ]; then
                   do_refresh "$rel" "$asrc" "(group)"
               else
                   n_newer=$((n_newer+1))
                   printf '    WARN             %-58s 分组落单但**不早于**权威（%s vs %s，%s）⇒ 不盲拷\n' \
                       "$rel" "$f5" "$(short "$(sha "$asrc")")" "$f6"
               fi ;;
    esac
done <<< "$rows"
if [ -z "$(printf '%s' "$rows" | tr -d '[:space:]')" ]; then
    echo "    （没有 STALE / NEWER-DIFF / DIVERGENT 落单者：同类加载源副本都已是权威 sha）"
fi

printf '    APPSYNC-REFRESH=refreshed=%s newer=%s applied=%s\n' "$n_ref" "$n_newer" "$APPLY"

# 2) 刷新后再跑一次校验器（把"刷新前 / 刷新后"并排摆出来）
out2="$(AUTH_ROOT="$REPO" SCAN_ROOTS="$SCAN_ROOTS" bash "$CHECK" 2>&1)"; rc2=$?
printf '%s\n' "$out2" | grep -E 'APPSYNC=' | sed 's/^/    /'
printf '    校验器：刷新前 exit=%s / 刷新后 exit=%s\n' "$chk_rc" "$rc2"
[ "$fail" = 0 ] || exit "$fail"
exit 0
