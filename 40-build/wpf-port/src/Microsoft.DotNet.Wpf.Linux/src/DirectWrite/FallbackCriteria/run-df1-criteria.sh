#!/usr/bin/env bash
# D-F1 判据 runner 的**唯一复算命令**（T2 车道）。
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/FallbackCriteria/run-df1-criteria.sh            # 只跑（要求已构建）
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/FallbackCriteria/run-df1-criteria.sh --build    # 先构建再跑（会编 shim 源；负载高时别用）
# 退出码：0=三判据全过；1=有 FAIL；3=NOINFO（**不等于通过**：runner 未构建 / 观测面不可用 / 缺健康正控）
#
# 【本脚本自己做的事（都是为了"读数可追责"）】
#   ① 跑前先落**四元组**：被测 shim 源 sha + 仪器（本脚本/Program.cs/判据/advance_from_font）sha + 口径（本判据 sha）
#      + 产物（DLL）sha 与 mtime + `loadavg`/`uptime`；
#   ② `--build` 时打印**构建前后**的 DLL sha+mtime，并检查"DLL 是否比源文件新"（旧产物 ⇒ 大声警告，别把旧读数当新读数）；
#   ③ 先跑**判据层自验**（`eval-df1-criteria.py --selftest`，三态正控）再跑真读数；
#   ④ raw 同时落 `$DF1_OUT`（默认 `$HOME/wfp-runs/df1c-<时间戳>/`）与 `/tmp`（**/tmp 会被清，$HOME 那份才是留存**）。
set -uo pipefail
# 【参数转发（主控 #15 / T1d HANDOFF）】`--mode=… --para=… --font=… --em=… --width=… --strict-probe --census [--census-cps=…]`
#   不给 ⇒ 默认三模式全段落（判据口径）。`--census` ⇒ 只跑面选择普查（不跑判据）。
RUN_ARGS=(); CENSUS=0
for a in "$@"; do
  case "$a" in
    --build) :;;
    --census) CENSUS=1; RUN_ARGS+=("$a");;
    --mode=*|--para=*|--font=*|--em=*|--width=*|--census-cps=*|--strict-probe|--probe-only) RUN_ARGS+=("$a");;
    *) echo "⚠️ 未知参数（忽略）：$a" >&2;;
  esac
done
[ "${#RUN_ARGS[@]}" = 0 ] && RUN_ARGS=(--mode=all --strict-probe)
# 【仪器自身健壮性（2026-09-15 实测踩过）】非交互 shell 里 `dotnet` **不在 PATH** 上 ⇒ 旧版会静默地
#   变成"no-runner-output（NOINFO）"，把**仪器故障**伪装成"读数缺失"。现在：显式解析 + 找不到就**响亮退出**。
if [ -x "$HOME/.dotnet/dotnet" ]; then PATH="$HOME/.dotnet:$PATH"; fi
DOTNET_BIN="$(command -v dotnet || true)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../../../.." && pwd)"
. "$REPO/src/Linux/build/selfbuilt-config.sh"   # ★ 权威件配置只走唯一声明（TASK-O1-O4-O6 §O2）
DLL="$HERE/bin/Debug/FallbackCriteria.dll"
if [ -z "$DOTNET_BIN" ]; then
  echo "CRITERIA=NOINFO reason=dotnet-not-found（**仪器故障**，不是"读数缺失"：请装 dotnet 或设 PATH；本行不许当绿）" >&2
  exit 3
fi
SHIM="$REPO/src/Microsoft.DotNet.Wpf.Linux/src/shims/PresentationCore.HbTextLine.cs"
PC="$REPO/src/Microsoft.DotNet.Wpf.Linux/src/PresentationCore/bin/$SELFBUILT_CONFIG/PresentationCore.dll"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUTDIR="${DF1_OUT:-$HOME/wfp-runs/df1c-$STAMP}"
mkdir -p "$OUTDIR"
LOG="$OUTDIR/raw.txt"; TMPLOG="/tmp/df1-criteria-raw-$STAMP.txt"
H8() { sha256sum "$1" 2>/dev/null | cut -c1-16 || echo "NA"; }

{
echo "== D-F1 判据趟（T2）$(date '+%F %T')  OUTDIR=$OUTDIR"
echo "== 四元组"
printf "   件：被测 shim=%s  PC=%s\n" "$(H8 "$SHIM")" "$(H8 "$PC")"
printf "   仪器：runner脚本=%s  Program.cs=%s  判据=%s  advance_from_font=%s\n" \
       "$(H8 "${BASH_SOURCE[0]}")" "$(H8 "$HERE/Program.cs")" "$(H8 "$HERE/eval-df1-criteria.py")" "$(H8 "$HERE/advance_from_font.py")"
printf "   口径：判据 sha=%s（**运行那一刻**的值；本行与上面『仪器』里的判据 sha 同源）\n" "$(H8 "$HERE/eval-df1-criteria.py")"
printf "   参考：harness run.sh=%s（只读，未参与本趟）\n" "$(H8 "$REPO/src/Linux/build/MilBridge/run.sh")"
echo "   环境：uptime=$(uptime | sed 's/^ *//')  loadavg=$(cut -d' ' -f1-3 /proc/loadavg)"
echo "   字体环境（**设没设会改读数**，主控 2026-09-15 ③ 硬要求）：WPF_LINUX_FONT_DIR=${WPF_LINUX_FONT_DIR:-<未设 ⇒ 系统目录>}  WPF_LINUX_MULTIFONT=${WPF_LINUX_MULTIFONT:-<未设>}"
echo "   产物：DLL sha16=$(H8 "$DLL") mtime=$(stat -c '%y' "$DLL" 2>/dev/null || echo NA)"
} | tee "$LOG"

if [ "${1:-}" = "--build" ]; then
  echo "== 构建（-m:1，编入 shim 真源）" | tee -a "$LOG"
  export PATH="$HOME/.dotnet:$PATH"
  "$DOTNET_BIN" build "$HERE/FallbackCriteria.csproj" -m:1 --nologo -v q -p:HbShimSrc="$SHIM" 2>&1 | tail -5 | tee -a "$LOG"
  rc=${PIPESTATUS[0]}
  echo "   构建 rc=$rc；构建后 DLL sha16=$(H8 "$DLL") mtime=$(stat -c '%y' "$DLL" 2>/dev/null || echo NA)" | tee -a "$LOG"
  [ "$rc" = 0 ] || { echo "CRITERIA=NOINFO reason=build-failed(rc=$rc)" | tee -a "$LOG"; exit 1; }
  newest_src=$(stat -c %Y "$HERE/Program.cs" "$HERE/FallbackCriteria.csproj" "$SHIM" 2>/dev/null | sort -n | tail -1)
  dll_t=$(stat -c %Y "$DLL" 2>/dev/null || echo 0)
  if [ "$dll_t" -lt "$newest_src" ]; then
    echo "   ⚠️ **DLL 比源文件旧**（DLL=$(date -d @$dll_t '+%T') < newest_src=$(date -d @$newest_src '+%T')）⇒ 下面的读数可能来自旧产物，别当新读数用" | tee -a "$LOG"
  else
    echo "   ✓ DLL 不旧于源文件（DLL $(date -d @$dll_t '+%T') ≥ 源 $(date -d @$newest_src '+%T')）" | tee -a "$LOG"
  fi
fi

if [ ! -f "$DLL" ]; then
  echo "CRITERIA=NOINFO reason=runner-not-built（先跑：bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/FallbackCriteria/run-df1-criteria.sh --build）" | tee -a "$LOG"
  exit 3
fi

echo "== 判据层自验（三态正控；合成日志只验判据层）" | tee -a "$LOG"
python3 "$HERE/eval-df1-criteria.py" --selftest 2>&1 | tee -a "$LOG"
echo "   自验 rc=${PIPESTATUS[0]}" | tee -a "$LOG"

echo "== 真读数（raw 全文落 $LOG；下面是读数行）" | tee -a "$LOG"
"$DOTNET_BIN" "$DLL" "${RUN_ARGS[@]}" 2>&1 | tee -a "$LOG" | grep -E "^MODE=|^# |SCAFFOLD_DIAG" || true
cp -f "$LOG" "$TMPLOG"    # 真·副本（**/tmp 会被清，留存看 $OUTDIR 那份**）

# 定位探针：**本 shim 自己的 HbShaper（内部装置）**在给定 `面#下标` 上把 `与` 整成什么字形号。
#   只为解释"C1② 独立读 cmap 得 9497、shim 报 9498"这个差 1 —— 探针行以 `#` 开头，**不参与判据**。
PROBE_FACES="${DF1_PROBE_FACES:-/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc#0,/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc#2}"
probe_args=()
IFS=',' read -r -a _faces <<< "$PROBE_FACES"
for f in "${_faces[@]}"; do
  [ -n "$f" ] || continue
  [ -f "${f%%#*}" ] && probe_args+=("--shape-probe=$f") || echo "   （探针跳过：面无此文件 $f）" | tee -a "$LOG"
done
if [ "${#probe_args[@]}" -gt 0 ]; then
  echo "== 定位探针（内部装置；不参与判据）" | tee -a "$LOG"
  "$DOTNET_BIN" "$DLL" --probe-only "${probe_args[@]}" 2>&1 | grep -E "^# PROBE-SHAPE" | tee -a "$LOG"
fi

if [ "$CENSUS" = 1 ]; then
  echo "== --census：只跑面选择普查（**不跑判据**；行首 『#』 ⇒ 不是读数行）  OUTDIR=$OUTDIR rc=0" | tee -a "$LOG"
  cp -f "$LOG" "$TMPLOG"; exit 0
fi
NMODE=$(grep -cE "^MODE=(null|fb|nofb) PARA=b34 RESULT=OK" "$LOG" || true)
[ "$NMODE" -lt 3 ] && echo "（注：本趟只喂了 $NMODE 个模式 ⇒ 判据层将按**防空过**报 NOINFO —— 这是**预期**，不是失败；要判据请跑默认三模式）" | tee -a "$LOG"
echo "== 判据" | tee -a "$LOG"
python3 "$HERE/eval-df1-criteria.py" < "$LOG" > "$OUTDIR/criteria.txt" 2>&1
rc=$?
cat "$OUTDIR/criteria.txt" | tee -a "$LOG"
echo "== 本趟退出码 rc=$rc（0=全过；1=有 FAIL；3=NOINFO）  OUTDIR=$OUTDIR" | tee -a "$LOG"
exit "$rc"
