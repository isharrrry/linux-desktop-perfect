#!/usr/bin/env bash
# ============================================================================
# frames-check.sh —— `TASK-0502`（GIF **多帧解码 ＋ 帧时序**）的判据 runner
# ============================================================================
#
# 【判据三态 + 一档 PARTIAL】
#   WICFRAMES=PASS     所有硬判据 PASS 且 `noinfo=0`（rc=0 —— **只有这一档是 0**）
#   WICFRAMES=FAIL     ≥1 条硬判据不成立（rc=1）
#   WICFRAMES=NOINFO   前置不成立（无 gcc/python3/编不出探针/生不出夹具）⇒ 不许当绿（rc=2）
#   WICFRAMES=PARTIAL  没有 FAIL，但有 NOINFO 条目（例如"该 shim 没实现帧延迟"）（rc=3）
#
# 【硬判据（逐帧位级，不看状态码就算完）】
#   H1 帧数     GetFrameCount == 夹具真值帧数（真值由 frames-gen.py **自己解析 GIF 结构**得出）
#   H2 逐帧内容 每帧 CopyPixels 的 BGRA 缓冲与真值 `.bgra` **逐字节相同**（帧数不可解码 ⇒ FAIL）
#   H3 互不相同 多帧夹具的各帧两两不同（专抓"GetFrame(i>0) 返第 0 帧"这类假修）
#   H4 越界如实 GetFrame(帧数) **必须失败**（成功 ⇒ FAIL：那是"声明 1 帧却给得出第 1 帧"的形态）
#   H5 次序无关 倒序取一遍 / 正序取一遍，逐帧字节相同
#   D1 帧时序   GIF：`/grctlext/Delay` 必须 VT_UI2 且逐帧等于真值（未实现 ⇒ NOINFO，**不是** FAIL）
#   Z1 零回归   单帧 PNG / JPEG / 单帧 GIF 的第 0 帧与真值逐字节相同（判据本身不让静态图漂移）
#
# 【三种模式】
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/frames-check.sh                  # 打仓内权威 shim
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/frames-check.sh --shim <so>      # 打指定 shim
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/frames-check.sh --baseline <so>  # 与另一份 shim 逐帧比（零回归取证）
#   bash src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/frames-check.sh --selftest       # **反极性**：三只撒谎 shim 必须被打红
#
# 【为什么 selftest 长这样】产品 shim 通过探针只能说明"它没被抓住"。三只**撒谎 shim** 各自
#   模拟一种真实的假修形态（① 把修法还原成"恒 1 帧" ② 帧数真而像素恒第 0 帧
#   ③ 帧数 1 却给得出第 1 帧）—— 它们若也能绿，这份判据就是"只查状态码"的空尺子。
# ============================================================================
set -uo pipefail

HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd -- "$HERE/../../../../.." && pwd)"
SHIM="$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libwpfwic.so"
SKIA="$REPO/src/Microsoft.DotNet.Wpf.Linux/src/DirectWrite/wic-shim/libSkiaSharp.so"
SRC="$HERE/wic_proxy.c"
PROBE_C="$HERE/frames-probe.c"
GEN_PY="$HERE/frames-gen.py"
OUT="${WICFRAMES_DIR:-$(mktemp -d /tmp/wicframes.XXXXXX)}"
MODE=run
BASELINE=""
for a in "$@"; do
    case "$a" in
        --selftest) MODE=selftest ;;
        --shim)     MODE=shimset ;;
        --baseline) MODE=baselineset ;;
        *) case "$MODE" in
               shimset) SHIM="$a"; MODE=run ;;
               baselineset) BASELINE="$a"; MODE=run ;;
               *) echo "WICFRAMES=NOINFO reason=unknown-arg arg=$a"; exit 2 ;;
           esac ;;
    esac
done

mkdir -p "$OUT"
say() { echo "$@"; }
die() { echo "WICFRAMES=NOINFO reason=$1 ${2:-}"; exit 2; }

# ────────────────────────────── 一、前置 ──────────────────────────────
command -v gcc     >/dev/null 2>&1 || die "gcc-absent"
command -v python3 >/dev/null 2>&1 || die "python3-absent"
python3 -c 'import PIL' 2>/dev/null || die "pillow-absent"
[ -f "$SHIM" ] || die "shim-absent" "shim=$SHIM"
[ -f "$PROBE_C" ] || die "probe-src-absent" "path=$PROBE_C"
[ -f "$GEN_PY" ] || die "gen-src-absent" "path=$GEN_PY"

# ────────────────────── 二、反极性：三只撒谎 shim（只在 --selftest 用）──────────────────────
build_liar() {   # $1 = 名称, $2 = 被替换的锚, $3 = 替换成
    local name="$1" anchor="$2" repl="$3" dir="$OUT/liar-$1"
    mkdir -p "$dir"
    python3 - "$SRC" "$dir/wic_proxy.c" "$anchor" "$repl" "$name" >&2 <<'PY' || return 1
import sys
src, dst, anchor, repl, name = sys.argv[1:6]
b = open(src, "rb").read()
ob = anchor.encode()
n = b.count(ob)
if n != 1:
    print(f"LIAR_SURGERY=FAIL name={name} anchor-occurrences={n}")
    sys.exit(1)
b = b.replace(ob, repl.encode(), 1)
open(dst, "wb").write(b)
print(f"LIAR_SURGERY=OK name={name} bytes={len(b)}")
PY
    gcc -O2 -fPIC -shared -Wall -Wextra -Wno-unused-parameter \
        -o "$dir/libwpfwic.so" "$dir/wic_proxy.c" -ldl -lz 2>"$dir/gcc.log" || {
        echo "  liar $name: gcc 失败（见 $dir/gcc.log）" >&2; return 1; }
    echo "$dir/libwpfwic.so"
}

if [ "$MODE" = selftest ]; then
    say "# == W79A 反极性自测：三只撒谎 shim 各自必须让判据变红 =="
    [ -f "$SRC" ] || die "source-absent" "path=$SRC"
    declare -A ANCHOR REPL
    # ① NEG：把修法还原（帧数恒 1）⇒ 应回到"只出第 0 帧"
    ANCHOR[neg]='    if (o->frame_count > 0) return o->frame_count;'
    REPL[neg]='    { o->frame_count = 1; return 1; }   /* SELFTEST-NEG: 还原成恒 1 帧 */'
    # ② FAKE-pixels：帧数真、像素恒解第 0 帧 ⇒ 抓"逐帧内容"这一格
    ANCHOR[fake_px]='        opts.frame_index = o->frame_index;'
    REPL[fake_px]='        opts.frame_index = 0;   /* SELFTEST-FAKE: 假装逐帧，其实永远第 0 帧 */'
    # ③ FAKE-pair：帧数仍写 1、却给得出第 1 帧 ⇒ 抓"声明与行为不一致"
    ANCHOR[fake_pair]='    if (index >= (uint32_t)n) {'
    REPL[fake_pair]='    if (0) {   /* SELFTEST-FAKE: 越界检查被短路 ⇒ 声明 1 帧却给得出第 1 帧 */'
    okall=1
    for name in neg fake_px fake_pair; do
        so="$(build_liar "$name" "${ANCHOR[$name]}" "${REPL[$name]}")" || { okall=0; continue; }
        out="$OUT/selftest-$name.txt"
        WICFRAMES_DIR="$OUT/run-$name" bash "${BASH_SOURCE[0]}" --shim "$so" > "$out" 2>&1
        rc=$?
        verdict="$(grep -m1 '^WICFRAMES=' "$out" | cut -d' ' -f1)"
        if [ "$rc" -eq 1 ] && [ "$verdict" = "WICFRAMES=FAIL" ]; then
            say "  ✓ $name: 被打红（rc=1 $(grep -m1 '^WICFRAMES=' "$out" | cut -c1-120)）"
        else
            say "  ✗ $name: **没有**被打红（rc=$rc verdict=$verdict）—— 判据空转！"
            okall=0
        fi
    done
    if [ "$okall" -eq 1 ]; then
        say "SELFTEST=PASS liars=3 caught=3（三只撒谎 shim 全部被打红）"
        exit 0
    fi
    say "SELFTEST=FAIL liars=3 caught=0"
    exit 1
fi

# ───────────────────────── 三、探针 + 夹具 ─────────────────────────
PROBE="$OUT/frames-probe"
gcc -O1 -Wall -o "$PROBE" "$PROBE_C" -ldl 2>"$OUT/gcc-probe.log" || die "probe-compile-failed" "log=$OUT/gcc-probe.log"
WICFRAMES_DIR="$OUT" python3 "$GEN_PY" >"$OUT/gen.log" 2>&1 || die "fixture-gen-failed" "log=$OUT/gen.log"
grep -q '^GEN_OK=1' "$OUT/gen.log" || die "fixture-gen-silent" "log=$OUT/gen.log"

say "# shim=$SHIM"
say "# sha16=$(sha256sum "$SHIM" | cut -c1-16)  fixtures=$OUT/fix"
say "#"

# ───────────────────────── 四、逐夹具取读数 + 比对 ─────────────────────────
# HARD = 计入判词；OBS = 只观测（Skia 对 disposal=2 的后续帧自己就报 kInvalidConversion）
HARD="g1-3f-solid.gif g2-4f-partial.gif g5-1f.gif g3-1f.png g4-1f-jfif.jpg"
OBS="g6-disposal2.gif"
FAILS=0
NOINFOS=0
OBSLINE=""

for f in $HARD $OBS; do
    stem="${f%.*}"
    rec="$OUT/probe-$stem.txt"
    WPF_LINUX_WIC_SKIA="$SKIA" "$PROBE" "$SHIM" "$OUT/fix/$f" "$OUT" >"$rec" 2>&1
    [ -s "$rec" ] || { say "case $stem: NOINFO reason=probe-produced-nothing"; NOINFOS=$((NOINFOS+1)); continue; }
    verdict_line="$(python3 "$HERE/frames-compare.py" "$OUT/truth/$stem.tsv" "$rec" "$OUT" "$stem" "$f")"
    rc=$?
    say "case $stem: $verdict_line"
    case "$f" in
        $OBS) OBSLINE="obs=$stem:${verdict_line%% *}" ;;   # 观测档：**不改判词**，但必须出现在结论行里
        *)
            if [ "$rc" -eq 1 ]; then FAILS=$((FAILS+1)); fi
            if [ "$rc" -eq 3 ]; then NOINFOS=$((NOINFOS+1)); fi
            ;;
    esac
done

# ───────────────────────── 五、零回归（可选：与另一份 shim 比） ─────────────────────────
if [ -n "$BASELINE" ]; then
    [ -f "$BASELINE" ] || die "baseline-absent" "path=$BASELINE"
    bdir="$OUT/base"; mkdir -p "$bdir"
    zbad=0
    for f in g3-1f.png g4-1f-jfif.jpg g5-1f.gif g1-3f-solid.gif; do
        stem="${f%.*}"
        WPF_LINUX_WIC_SKIA="$SKIA" "$PROBE" "$BASELINE" "$OUT/fix/$f" "$bdir" >/dev/null 2>&1
        a="$(sha256sum "$bdir/$stem.shimf0p1.bgra" 2>/dev/null | cut -c1-16)"
        b="$(sha256sum "$OUT/$stem.shimf0p1.bgra" 2>/dev/null | cut -c1-16)"
        if [ -n "$a" ] && [ "$a" = "$b" ]; then
            say "case Z1-$stem: PASS 第 0 帧与基线逐字节相同 sha16=$a"
        else
            say "case Z1-$stem: FAIL 第 0 帧与基线不同 baseline=$a now=$b"
            zbad=$((zbad+1))
        fi
    done
    FAILS=$((FAILS+zbad))
fi

# ───────────────────────── 六、判词 ─────────────────────────
if [ "$FAILS" -gt 0 ]; then
    say "WICFRAMES=FAIL fails=$FAILS noinfo=$NOINFOS $OBSLINE shim16=$(sha256sum "$SHIM" | cut -c1-16)"
    exit 1
fi
if [ "$NOINFOS" -gt 0 ]; then
    say "WICFRAMES=PARTIAL fails=0 noinfo=$NOINFOS $OBSLINE shim16=$(sha256sum "$SHIM" | cut -c1-16)"
    exit 3
fi
say "WICFRAMES=PASS fails=0 noinfo=0 $OBSLINE shim16=$(sha256sum "$SHIM" | cut -c1-16)"
exit 0
