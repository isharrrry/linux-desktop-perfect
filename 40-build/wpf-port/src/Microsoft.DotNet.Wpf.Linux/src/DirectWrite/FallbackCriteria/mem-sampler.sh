#!/usr/bin/env bash
# =====================================================================================
# **内存 / maps 采样器**（T2，2026-09-15；主控窗口 6b 批准落进 `src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/**`）
#
# 【它是什么】把一条命令跑在**有界装置**里，并在运行期每 0.2 s 采一次**整棵进程树**的：
#   · `/proc/<pid>/status`（`VmRSS` / `VmHWM`）与 `/proc/<pid>/stat`（CPU ticks）
#   · `/proc/<pid>/smaps_rollup` 的四列：**`Rss` / `Private_Dirty` / `Private_Clean` / `Shared_Clean`**
#   · **峰值那一刻**再落 `/proc/<pid>/maps` 与 **`/proc/<pid>/smaps`**（`smaps` 带**逐段 Rss**）
#   以及 `/usr/bin/time -v` 的**权威峰值**（`Maximum resident set size`；短跑只有它靠得住）。
#
# 【判据（这套装置产出的三个量）】
#   ① **权威峰值** = `/usr/bin/time -v` 的 `Maximum resident set size`（KB）
#   ② **`smaps_rollup` 四列**——判别"native 堆未归还"(`Private_Dirty` 占大头) vs "映射仍驻留"(`Shared_Clean` 占大头)
#   ③ **字体映射段数**：从峰值 `smaps` 里按**文件**汇总"被映射了几段 / Σ虚拟 / ΣRss"，
#      并与 ② 的 `Shared_Clean` **对账**（字体那一份应占大头；对不上 ⇒ 先怀疑采样器，而不是下结论）
#
# 【用法】
#   bash mem-sampler.sh --selfcheck                      # ← **先自检再跑**（见下）
#   bash mem-sampler.sh <秒上限> <输出目录> <命令…>        # 例：
#     bash mem-sampler.sh 120 $HOME/wfp-runs/run1 \
#       bash -c "export DOTNET_GCHeapHardLimit=0x10000000 DF1_REPO=$PWD \
#                WPF_LINUX_FONT_DIR=$HOME/wfp-runs/fontdir-1; exec dotnet <runner>.dll --mode=null --para=b34"
#   输出目录里：`meta.txt`(RC/峰值/CPU/墙钟/**SNAP_AT_PEAK 四列**)、`sample.txt`(0.2 s 全量)、
#               `maps-at-peak-<pid>.txt`、`smaps-at-peak-<pid>.txt`、`snap-ok.txt`、`snap-errors.txt`、
#               `stdout.txt` / `stderr.txt`
#
# 【`--selfcheck` 为什么必须有】2026-09-15 实测踩过：旧版对树里**每个** PID 都 `cp /proc/<pid>/maps`，
#   其中**瞬时子进程已消失** ⇒ `stat 失败` ⇒ **一个快照文件都没落**，而 `SNAP_AT_PEAK` 那行却照常有值
#   ⇒ 极易把"采不到"读成"没有映射"。自检 = 起一个**显式 mmap 某个字体文件并 sleep** 的久活进程，
#   断言：快照**非空**、**含字体行**、且 `Shared_Clean > 0`；不通过就**不许**拿这套装置的读数下结论。
#
# 【纪律】只按 PID 杀（看门狗对**整棵树** `kill -TERM` → 2 s → `kill -KILL`）；**绝不用 `pkill -f`**。
# =====================================================================================
# 有界诊断跑（T2，2026-09-15）—— **只按 PID 杀**、**不用 pkill -f**。

# ── --selfcheck：用一个"显式 mmap 字体文件 + sleep"的久活进程验证快照真的能采到 ──────────
if [ "${1:-}" = "--selfcheck" ]; then
  SC="$(mktemp -d)"; trap 'rm -rf "$SC"' EXIT
  FONT="${SAMPLER_SELFCHECK_FONT:-/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc}"
  [ -f "$FONT" ] || { echo "SAMPLER-SELFCHECK=FAIL reason=font-missing:$FONT"; exit 1; }
  bash "$0" 30 "$SC" python3 -c "
import mmap,time
f=open(r'$FONT','rb'); m=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ); _=m[0]; time.sleep(6)
" >/dev/null 2>&1
  n=$(cat "$SC"/smaps-at-peak-*.txt 2>/dev/null | wc -l)
  f=$(cat "$SC"/smaps-at-peak-*.txt 2>/dev/null | grep -ac "$(basename "$FONT")")
  sc=$(grep -a '^SNAP_AT_PEAK' "$SC"/meta.txt 2>/dev/null | grep -o 'shared_clean_kb=[0-9]*' | cut -d= -f2)
  if [ "${n:-0}" -gt 0 ] && [ "${f:-0}" -ge 1 ] && [ "${sc:-0}" -gt 0 ]; then
    echo "SAMPLER-SELFCHECK=PASS（smaps 快照 $n 行、含字体映射 $f 段、shared_clean=${sc}KB）"
    exit 0
  fi
  echo "SAMPLER-SELFCHECK=FAIL（行数=${n:-0} 字体段=${f:-0} shared_clean=${sc:-0}）——**不许**用本装置下结论"
  exit 1
fi
# 用法：bounded-run.sh <秒上限> <输出目录> <命令…>
#   记录：RSS/HWM 采样（每 0.2s）、CPU 时间、打开过的字体字面（maps+fd 两处）、是否被看门狗掐掉
set -uo pipefail
LIMIT="${1:?秒上限}"; OUT="${2:?输出目录}"; shift 2
mkdir -p "$OUT"
S="$OUT/sample.txt"; : > "$S"
# 【短跑也要有权威峰值】采样器 0.2s 一跳 ⇒ **秒级完成**的跑会一条都采不到（实测 PEAK=0 被误读成"0 MB"）。
#   故再用 `/usr/bin/time -v` 包一层：它的 `Maximum resident set size` 是**权威峰值**（含 exec 后的直接子进程）。
if [ -x /usr/bin/time ]; then TIMEV=(/usr/bin/time -v); else TIMEV=(); fi
"${TIMEV[@]}" "$@" > "$OUT/stdout.txt" 2> "$OUT/stderr.txt" &
PID=$!
echo "CMD=$*  PID=$PID  START=$(date '+%F %T')" | tee "$OUT/meta.txt"
PEAK=0; LAST_CPU=0; T0=$(date +%s)
# 【修正 2026-09-15】旧版只采**根 PID** ⇒ 走脚本包装时采到的是 bash（3 MB），dotnet 的峰值全看不见；
#   且看门狗只杀根 ⇒ 子进程 dotnet 会**变成孤儿继续跑**（实测：跑了 5 分钟没人管）。现在：**采整棵进程树、杀整棵树**。
tree_pids() {   # 根 + 所有后代（广度优先，只用 /proc 的 PPid）
    local root="$1" all="$1" frontier="$1" next p pp
    while [ -n "$frontier" ]; do
        next=""
        for p in $frontier; do
            for d in /proc/[0-9]*; do
                pp="$(awk '/^PPid:/{print $2}' "$d/status" 2>/dev/null)"
                [ "$pp" = "$p" ] && next="$next ${d#/proc/}"
            done
        done
        frontier="$next"; all="$all $next"
    done
    printf '%s' "$all"
}
while kill -0 "$PID" 2>/dev/null; do
  RSS=0; HWM=0; CPU=0; PD=0; PC=0; SC=0; SD=0; ANON=0
  for tp in $(tree_pids "$PID"); do
    read -r r h < <(awk '/VmRSS/{r=$2} /VmHWM/{h=$2} END{print r+0, h+0}' /proc/$tp/status 2>/dev/null)
    read -r ut st < <(awk '{print $14, $15}' /proc/$tp/stat 2>/dev/null)
    RSS=$((RSS + ${r:-0})); CPU=$((CPU + ${ut:-0} + ${st:-0}))
    [ "${h:-0}" -gt "$HWM" ] && HWM="${h:-0}"
    # 【快照用】记下本 tick RSS 最大的那个 PID（= 真正的 dotnet；瞬时子进程会消失 ⇒ 钉住主体才可靠）
    if [ "${r:-0}" -gt "${MAXR:-0}" ]; then MAXR="${r:-0}"; MAXP="$tp"; fi
    # 【窗口 4 判别量（主控 ②）】smaps_rollup 四列：Private_Dirty（native 堆未归还）vs Shared_File/Private_Clean（映射仍驻留）
    read -r a1 a2 a3 a4 a5 a6 < <(awk '/^Rss:/{r=$2} /^Private_Clean:/{pc=$2} /^Private_Dirty:/{pd=$2} /^Shared_Clean:/{sc=$2} /^Shared_Dirty:/{sd=$2} /^Anonymous:/{an=$2} END{print r+0, pc+0, pd+0, sc+0, sd+0, an+0}' /proc/$tp/smaps_rollup 2>/dev/null)
    PD=$((PD + ${a3:-0})); PC=$((PC + ${a2:-0})); SC=$((SC + ${a4:-0})); SD=$((SD + ${a5:-0})); ANON=$((ANON + ${a6:-0}))
  done
  [ "$HWM" -gt "$PEAK" ] && PEAK="$HWM"
  LAST_CPU="$CPU"
  if [ "$RSS" -gt "${SNAP_RSS:-0}" ]; then    # 峰值那一刻的快照（smaps 四列 + CPU + 时刻 + **maps 全量**）
    # 【2026-09-15 修】旧版对 tree_pids 里**每个** PID 都 cp ⇒ 瞬时子进程已消失 ⇒ stat 失败 ⇒ 一个文件都没落。
    #   现在只钉住本 tick RSS 最大的那个 PID（主体），并把成败逐条记进 snap-ok.txt。
    snap_one() {
      local tp="$1"
      [ -d "/proc/$tp" ] || { echo "  snap pid=$tp SKIP(已退出)" >> "$OUT/snap-ok.txt"; return; }
      cp -f "/proc/$tp/maps"  "$OUT/maps-at-peak-$tp.txt"  2>>"$OUT/snap-errors.txt"
      cp -f "/proc/$tp/smaps" "$OUT/smaps-at-peak-$tp.txt" 2>>"$OUT/snap-errors.txt"
      echo "  snap pid=$tp maps=$(stat -c%s "$OUT/maps-at-peak-$tp.txt" 2>/dev/null || echo 0)B smaps=$(stat -c%s "$OUT/smaps-at-peak-$tp.txt" 2>/dev/null || echo 0)B" >> "$OUT/snap-ok.txt"
    }
    [ -n "${MAXP:-}" ] && snap_one "$MAXP"
    for tp in $(tree_pids "$PID"); do [ "$tp" = "${MAXP:-}" ] && continue; snap_one "$tp"; done
    SNAP_RSS="$RSS"; SNAP_PD="$PD"; SNAP_PC="$PC"; SNAP_SC="$SC"; SNAP_SD="$SD"; SNAP_ANON="$ANON"; SNAP_T="$(date +%H:%M:%S.%3N)"
  fi
  echo "$(date +%H:%M:%S.%3N) rss_kb=$RSS hwm_kb=$HWM cpu_ticks=$LAST_CPU priv_dirty_kb=$PD priv_clean_kb=$PC shared_clean_kb=$SC shared_dirty_kb=$SD anon_kb=$ANON（整棵树）" >> "$S"
  grep -h "fonts\|\.tt[cf]" /proc/$PID/maps 2>/dev/null | awk '{print $NF}' >> "$OUT/maps-fonts.txt.raw"
  for fd in /proc/$PID/fd/*; do readlink "$fd" 2>/dev/null; done | grep -a "fonts\|\.tt[cf]" >> "$OUT/fd-fonts.txt.raw"
  NOW=$(date +%s); if [ $((NOW-T0)) -ge "$LIMIT" ]; then
    echo "WATCHDOG=kill_TERM_$PID at ${LIMIT}s（**只按 PID**）" | tee -a "$OUT/meta.txt"
    for tp in $(tree_pids "$PID"); do kill -TERM "$tp" 2>/dev/null; done; sleep 2
    for tp in $(tree_pids "$PID"); do kill -KILL "$tp" 2>/dev/null; done
    kill -KILL "$PID" 2>/dev/null; break
  fi
  sleep 0.2
done
wait "$PID"; RC=$?
if [ -s "$OUT/stderr.txt" ] && grep -q "Maximum resident set size" "$OUT/stderr.txt"; then
  mx="$(sed -n 's/.*Maximum resident set size (kbytes): \([0-9]*\)/\1/p' "$OUT/stderr.txt" | head -1)"
  [ -n "$mx" ] && [ "$mx" -gt "$PEAK" ] && PEAK="$mx"
  grep -q "Maximum resident set size" "$OUT/meta.txt" || echo "TIMEV_MAX_RSS_KB=$mx（/usr/bin/time -v 的权威峰值）" >> "$OUT/meta.txt"
fi
{
  echo "RC=$RC"
  echo "SNAP_AT_PEAK time=${SNAP_T:-NA} rss_kb=${SNAP_RSS:-0} priv_dirty_kb=${SNAP_PD:-0} priv_clean_kb=${SNAP_PC:-0} shared_clean_kb=${SNAP_SC:-0} shared_dirty_kb=${SNAP_SD:-0} anon_kb=${SNAP_ANON:-0}（**峰值那一刻**的 smaps_rollup 分解，整棵树求和）"
  echo "PEAK_HWM_KB=$PEAK"
  echo "CPU_TICKS_LAST=$LAST_CPU（/100 = 秒；ticks 取自 /proc/<pid>/stat 的 utime+stime）"
  echo "WALL_S=$(( $(date +%s) - T0 ))"
  echo "OPENED_FONT_FACES（maps ∪ fd 去重）："
  cat "$OUT/maps-fonts.txt.raw" "$OUT/fd-fonts.txt.raw" 2>/dev/null | sed '/^$/d' | LC_ALL=C sort -u | sed 's/^/    /'
} | tee -a "$OUT/meta.txt"
