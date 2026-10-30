#!/usr/bin/env bash
# =====================================================================================
# **段数 / Σ虚拟 采样器**（T2 车道；`#15` 波后复取 D-F1c① 专用）
#
# 【为什么另开一个（而不是改 `mem-sampler.sh`）】
#   判据口径（预登记 ⑧）= **`/proc/<pid>/maps`/`smaps` 的「段数 / Σ虚拟」**，**RSS 只作旁证**
#   （`mmap` 后未被触碰的页不计 RSS；实测 B1：RSS 13 MB vs 虚拟 191 MB）。
#   而 `mem-sampler.sh` 只落 `maps`/`smaps` **原文快照**，**不算**"段数/Σ虚拟"这两个量
#   ⇒ 每个读的人都得自己再写一遍 awk（口径会漂）。本仪器把它们**在采样时就算出来**：
#     · 每 tick：整棵进程树的 **Σ段数 / Σ虚拟(MB) / ΣRSS(MB) / smaps_rollup 四列**
#     · **峰值那一刻**：按文件聚合的 `段数 / Σ虚拟 / ΣRss`（字体族另给一行）
#     · 结束时落 `segments-at-peak.txt`（逐文件）与 `segments-by-file.tsv`（全部 tick 的最大值）
#
# 【段数的定义（逐字写死，可复算）】
#   `/proc/<pid>/maps` 里**每一行** = 内核眼中**一段**。段数 = 该 pid 的 `maps` 行数。
#   "某文件的段数" = `maps` 里 pathname 字段（第 6 列起，含空格要 gobble）等于该文件的**行数**。
#   Σ虚拟 = 各段 `[start-end)` 之和（`maps` 第 1 列，单位 B）。ΣRss 取 `smaps` 的 `Rss:` 行归属到当前段。
#
# 【纪律】杀进程**只按 PID**（对整棵树 `kill -TERM` → 2 s → `kill -KILL`）；**绝不用 `pkill -f`**。
#   不用 `pgrep -f` 判"有没有进程"（会自匹配 `bash -c` 那一行）⇒ 一律走 `/proc`。
#
# 用法：
#   bash seg-instrument.sh <秒上限> <输出目录> <命令…>
#   bash seg-instrument.sh --selfcheck          # 自检：显式 mmap 字体的久活进程 ⇒ 断言段数/Σ虚拟采得到
# =====================================================================================
if [ "${1:-}" = "--selfcheck" ]; then
  SC="$(mktemp -d)"; trap 'rm -rf "$SC"' EXIT
  FONT="${SEG_SELFCHECK_FONT:-/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc}"
  [ -f "$FONT" ] || { echo "SEG-SELFCHECK=FAIL reason=font-missing:$FONT"; exit 1; }
  bash "$0" 40 "$SC" python3 -c "
import mmap,time
f=open(r'$FONT','rb'); m=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ); _=m[0]; time.sleep(6)
" >/dev/null 2>&1
  n=$(grep -ac '' "$SC"/segments-at-peak.txt 2>/dev/null)
  fb=$(awk -F'\t' '$1=="FONT_SEGMENTS"{print $2}' "$SC"/segments-at-peak.txt 2>/dev/null)
  fv=$(awk -F'\t' '$1=="FONT_VIRT_MB"{print $2}' "$SC"/segments-at-peak.txt 2>/dev/null)
  if [ "${fb:-0}" -ge 1 ] && [ -n "${fv:-}" ]; then
    echo "SEG-SELFCHECK=PASS（逐文件行 $n；字体段数=$fb Σ虚拟=${fv}MB —— 期望 1 段 ≈18.6MB）"
    exit 0
  fi
  echo "SEG-SELFCHECK=FAIL（逐文件行=${n:-0} 字体段数=${fb:-0} Σ虚拟=${fv:-none}）——**不许**用本仪器下结论"
  exit 1
fi

set -uo pipefail
LIMIT="${1:?秒上限}"; OUT="${2:?输出目录}"; shift 2
mkdir -p "$OUT"
S="$OUT/sample.txt"; : > "$S"
: > "$OUT/tick-pids.txt"

if [ -x /usr/bin/time ]; then TIMEV=(/usr/bin/time -v); else TIMEV=(); fi
"${TIMEV[@]}" "$@" > "$OUT/stdout.txt" 2> "$OUT/stderr.txt" &
PID=$!
echo "CMD=$*  PID=$PID  START=$(date '+%F %T')  LOADAVG=$(cut -d' ' -f1-3 /proc/loadavg)" | tee "$OUT/meta.txt"
T0=$(date +%s); BEST_SEG=-1; BEST_VIRT=-1; PEAK_RSS=0; LAST_CPU=0; SNAP_T=NA
SNAP_RSS=0; SNAP_PD=0; SNAP_PC=0; SNAP_SC=0; SNAP_SD=0; SNAP_ANON=0

tree_pids() {
    local frontier="$1" all="$1" next p pp d
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
  RSS=0; CPU=0; SEG=0; VIRT=0; PD=0; PC=0; SC=0; SD=0; ANON=0
  : > "$OUT/.tick.txt"
  for tp in $(tree_pids "$PID"); do
    [ -r "/proc/$tp/maps" ] || continue
    read -r ut st < <(awk '{print $14, $15}' /proc/$tp/stat 2>/dev/null)
    CPU=$((CPU + ${ut:-0} + ${st:-0}))
    read -r r < <(awk '/^VmRSS/{print $2}' /proc/$tp/status 2>/dev/null); RSS=$((RSS + ${r:-0}))
    read -r a1 a2 a3 a4 a5 a6 < <(awk '/^Rss:/{r=$2} /^Private_Clean:/{pc=$2} /^Private_Dirty:/{pd=$2} /^Shared_Clean:/{sc=$2} /^Shared_Dirty:/{sd=$2} /^Anonymous:/{an=$2} END{print r+0, pc+0, pd+0, sc+0, sd+0, an+0}' "/proc/$tp/smaps_rollup" 2>/dev/null)
    PD=$((PD + ${a3:-0})); PC=$((PC + ${a2:-0})); SC=$((SC + ${a4:-0})); SD=$((SD + ${a5:-0})); ANON=$((ANON + ${a6:-0}))
    # 逐段：maps 每行 = 一段。第 1 列 [start-end)，第 6 列起 = pathname（可含空格）。
    # 【为什么用 python 而不是 awk】本机 `awk` = **mawk**（实测 `strtonum` 未定义）⇒ 十六进制相减要自己写；
    #   python3 一次读全文件、按行切 5 个字段，pathname 里的空格天然安全。
    python3 - "$tp" "/proc/$tp/maps" > "$OUT/.m.$tp" 2>/dev/null <<'PYEOF'
import sys
pid, path = sys.argv[1], sys.argv[2]
out = []
with open(path, 'rb') as f:
    for line in f:
        line = line.rstrip(b'\n')
        parts = line.split(None, 5)
        if len(parts) < 5:
            continue
        try:
            a, b = parts[0].split(b'-')
            lo = int(a, 16); hi = int(b, 16); sz = hi - lo
        except Exception:
            continue
        perm = parts[1].decode()
        off = parts[2].decode()
        p = parts[5].decode('utf-8', 'replace') if len(parts) > 5 else ''
        # 列：pid / 字节 / perms / 文件内偏移 / start-hex / path
        out.append('%s\t%d\t%s\t%s\t%s\t%s' % (pid, sz, perm, off, a.decode(), p))
sys.stdout.write('\n'.join(out) + ('\n' if out else ''))
PYEOF
    if [ -s "$OUT/.m.$tp" ]; then
      read -r n t < <(awk -F'\t' '{n++; t+=$2} END{printf "%d %d", n+0, t+0}' "$OUT/.m.$tp")
      SEG=$((SEG + ${n:-0})); VIRT=$((VIRT + ${t:-0}))
      cat "$OUT/.m.$tp" >> "$OUT/.tick.txt"
    fi
    rm -f "$OUT/.m.$tp"
  done
  LAST_CPU="$CPU"
  [ "$RSS" -gt "$PEAK_RSS" ] && PEAK_RSS="$RSS"
  # 【每 tick 的字体面口径】本跑若只有 1–2 个 tick（实测 1CJK 档 0.9 s 就跑完），
  #   单看"峰值那一刻"会漏；把**字体段数/Σ虚拟**与**全树段数**一起逐 tick 落盘，
  #   并把它们的**窗内最大值**单独留一格 ⇒ 短跑也有可引用的"整窗最大"。
  if [ "${SEG_TICK_FONT:-1}" = 1 ]; then
    read -r fseg fvirt fbuf < <(awk -F'\t' '
      $6 ~ /\/fonts\// || $6 ~ /\.(tt[cf]|ot[cf])$/ { n++; v += $2; if ($6 ~ /fontconfig/) fc++ }
      END { printf "%d %d %d", n+0, v+0, fc+0 }' "$OUT/.tick.txt" 2>/dev/null)
  else fseg=0; fvirt=0; fbuf=0; fi
  [ "${fseg:-0}" -gt "${MAX_FSEG:-0}" ] && MAX_FSEG="$fseg"
  [ "${fvirt:-0}" -gt "${MAX_FVIRT:-0}" ] && MAX_FVIRT="$fvirt"
  echo "$(date +%H:%M:%S.%3N) seg=$SEG virt_mb=$((VIRT/1048576)) rss_mb=$((RSS/1024)) font_seg=$fseg font_virt_mb=$((fvirt/1048576)) priv_dirty_kb=$PD priv_clean_kb=$PC shared_clean_kb=$SC shared_dirty_kb=$SD anon_kb=$ANON cpu_ticks=$CPU（整棵树）" >> "$S"
  # 快照条件：**段数优先**（主判据），段数并列时取 Σ虚拟更大者；同时单独追 RSS 峰值口径
  take=0
  if [ "$SEG" -gt "$BEST_SEG" ]; then take=1
  elif [ "$SEG" -eq "$BEST_SEG" ] && [ "$VIRT" -gt "$BEST_VIRT" ]; then take=1; fi
  if [ "$take" = 1 ]; then
    BEST_SEG="$SEG"; BEST_VIRT="$VIRT"
    cp -f "$OUT/.tick.txt" "$OUT/segments-by-file.tsv" 2>/dev/null
    SNAP_T="$(date +%H:%M:%S.%3N)"; SNAP_RSS="$RSS"; SNAP_PD="$PD"; SNAP_PC="$PC"; SNAP_SC="$SC"; SNAP_SD="$SD"; SNAP_ANON="$ANON"
    bestp="$(awk -F'\t' '{c[$1]++} END{for(p in c) if(c[p]>m){m=c[p];bp=p} print bp}' "$OUT/.tick.txt")"
    { echo "tick seg=$SEG virt_mb=$((VIRT/1048576)) rss_mb=$((RSS/1024)) pids=$(tree_pids $PID)"; } >> "$OUT/tick-pids.txt"
    for tp in $(tree_pids "$PID"); do
      [ -d "/proc/$tp" ] || continue
      cp -f "/proc/$tp/maps"  "$OUT/maps-at-peak-$tp.txt"  2>/dev/null
      cp -f "/proc/$tp/smaps" "$OUT/smaps-at-peak-$tp.txt" 2>/dev/null
    done
    echo "  SNAP tick seg=$SEG virt_mb=$((VIRT/1048576)) rss_mb=$((RSS/1024)) main_pid=${bestp:-NA} at $SNAP_T" >> "$OUT/tick-pids.txt"
  fi
  NOW=$(date +%s); if [ $((NOW-T0)) -ge "$LIMIT" ]; then
    echo "WATCHDOG=kill_TERM at ${LIMIT}s（**只按 PID**）" | tee -a "$OUT/meta.txt"
    for tp in $(tree_pids "$PID"); do kill -TERM "$tp" 2>/dev/null; done; sleep 2
    for tp in $(tree_pids "$PID"); do kill -KILL "$tp" 2>/dev/null; done
    kill -KILL "$PID" 2>/dev/null; break
  fi
  sleep 0.2
done
wait "$PID" 2>/dev/null; RC=$?
rm -f "$OUT/.tick.txt"
mx=NA
if [ -s "$OUT/stderr.txt" ] && grep -q "Maximum resident set size" "$OUT/stderr.txt"; then
  mx="$(sed -n 's/.*Maximum resident set size (kbytes): \([0-9]*\)/\1/p' "$OUT/stderr.txt" | head -1)"
fi

# ── 峰值那一刻：按文件聚合（段数 / Σ虚拟 / ΣRss）──────────────────────────────────
peakpids="$(awk -F'\t' '{print $1}' "$OUT/segments-by-file.tsv" 2>/dev/null | sort -u | tr '\n' ' ')"
{
  echo "# SEGMENTS-AT-PEAK @ $SNAP_T  （整棵进程树 $peakpids）"
  echo "# 口径：『/proc/<pid>/maps』每行 = 一段；段数=行数；Σ虚拟=Σ(end-start)；ΣRss 来自 『smaps』逐段 『Rss:』"
  echo "# 字体段判据 = pathname 含 『/fonts/』 或以字体扩展名结尾"
} > "$OUT/segments-at-peak.txt"
cat "$OUT/segments-by-file.tsv" 2>/dev/null | awk -F'\t' '
  { sz=$2; p=$6; seg[p]++; v[p]+=sz }
  END { for (p in seg) printf "%d\t%d\t%s\n", seg[p], v[p], p }' | sort -rn > "$OUT/.byfile.txt"
{
  echo -e "ALL_SEGMENTS\t$(awk '{s+=$1} END{print s+0}' "$OUT/.byfile.txt")"
  echo -e "ALL_VIRT_MB\t$(awk '{s+=$2} END{printf "%.1f", s/1048576}' "$OUT/.byfile.txt")"
  awk -F'\t' -v OFS='\t' '{print "FILE_SEG", $1, $2, $3}' "$OUT/.byfile.txt"
  echo -e "FONT_SEGMENTS\t$(awk -F'\t' '$3 ~ /\/fonts\// || $3 ~ /\.(tt[cf]|ot[cf])$/ {s+=$1} END{print s+0}' "$OUT/.byfile.txt")"
  echo -e "FONT_VIRT_MB\t$(awk -F'\t' '$3 ~ /\/fonts\// || $3 ~ /\.(tt[cf]|ot[cf])$/ {s+=$2} END{printf "%.1f", s/1048576}' "$OUT/.byfile.txt")"
  echo -e "FONT_FILES\t$(awk -F'\t' '$3 ~ /\/fonts\// || $3 ~ /\.(tt[cf]|ot[cf])$/ {n++} END{print n+0}' "$OUT/.byfile.txt")"
} >> "$OUT/segments-at-peak.txt"
{
  echo "# 字体族逐文件（段数 / Σ虚拟MB / 路径）"
  awk -F'\t' '$3 ~ /\/fonts\// || $3 ~ /\.(tt[cf]|ot[cf])$/ {printf "%d\t%.1f\t%s\n", $1, $2/1048576, $3}' "$OUT/.byfile.txt"
  echo "# **字体段的逐段明细**（每段：pid/字节/perms/文件内偏移/start-hex/路径）—— 用来判"同文件同偏移的独立副本"vs"不同区段""
  awk -F'\t' '$6 ~ /\/fonts\// || $6 ~ /\.(tt[cf]|ot[cf])$/ {print}' "$OUT/segments-by-file.tsv"
} >> "$OUT/segments-at-peak.txt"

{
  echo "RC=$RC"
  echo "PEAK_SEGMENTS=$BEST_SEG（**主判据①**：整棵树在采样窗内的最大段数）"
  echo "PEAK_VIRT_MB=$((BEST_VIRT/1048576))（**主判据②**：该 tick 的 Σ虚拟）"
  echo "SNAP_AT_PEAK time=$SNAP_T rss_kb=$SNAP_RSS priv_dirty_kb=$SNAP_PD priv_clean_kb=$SNAP_PC shared_clean_kb=$SNAP_SC shared_dirty_kb=$SNAP_SD anon_kb=$SNAP_ANON"
  echo "PEAK_RSS_KB_SAMPLED=$PEAK_RSS（**旁证**：采样窗内 ΣRSS 最大值；0.2s 一跳 ⇒ 短跑会漏峰）"
  echo "FONT_SEG_WINDOW_MAX=${MAX_FSEG:-0}（**字体面主判据**：整窗内字体内核段数的最大值）"
  echo "FONT_VIRT_MB_WINDOW_MAX=$(( ${MAX_FVIRT:-0} / 1048576 ))（该 tick 的字体 Σ虚拟 MB）"
  echo "TIMEV_MAX_RSS_KB=$mx（**权威峰值**：/usr/bin/time -v 的 Maximum resident set size）"
  echo "CPU_TICKS_LAST=$LAST_CPU（/100 = 秒）"
  echo "WALL_S=$(( $(date +%s) - T0 ))"
  echo "SAMPLES=$(wc -l < "$S")"
} | tee -a "$OUT/meta.txt"
