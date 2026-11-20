#!/bin/bash
# PM-0112 层 alpha 本地化 · 从仓库一键重建
# 用法: bash 40-build/milcore/patches/apply-and-build.sh [--keep] [--no-build]
#       --keep      构建后不自动逆回（默认逆回，工作区回到 1c1bc80b 基线）
#       --no-build  只做基线校验 + 打补丁 + sha 校验 + 逆回，不调构建（自测用）
#
# 与 v1 的四处修正（12:2x 两个重建交叉污染后）：
#   1. node 不在 ssh 非交互 PATH —— 显式探测并给出候选路径
#   2. 无互斥 —— flock 独占，第二个实例立刻退出而不是交叉踩文件
#   3. `find -newermt` 不是固定产物路径 —— 用 build-bridge 的固定 PUBLISH 路径 + bridge-report.json
#   4. 失败不逆回 —— trap EXIT 保底逆回
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO" || exit 2

PATCH=40-build/milcore/patches/PM-0112-local-layer-alpha.patch
SRC=40-build/milcore/Rendering/SkiaRenderBackend.cs
BUILD=40-build/milcore-bridge/build-bridge.mjs
ARTIFACT=40-build/milcore-bridge/bin/Release/net10.0/linux-x64/publish/wpfgfx_cor3.so
REPORT=40-build/milcore-bridge/bridge-report.json
LOCK="$REPO/.git/pm0112-rebuild.lock"
BASE_SHA=1c1bc80b7baf791edc7801228f1cf2ca5866f6c1c2e6d50230648987313c310f
PATCHED_SHA=3681da3f088f417246985c6d51a99954469bb784c1257127efb46c7685ce0240
EXPECT_SO=e453e71495e4ca94bc9b428bcb7ba4666e4e7a3b5c2a20ae44c4cdd0460c1ae8
PATCH_SHA=4f626b12cf7be020100591b26c313dd3588a1f95dc63a884d2d1cca903097762

KEEP=0
NOBUILD=0
NOCLEAN=0
for a in "$@"; do
    case "$a" in
        --keep) KEEP=1 ;;
        --no-build) NOBUILD=1 ;;
        --no-clean) NOCLEAN=1 ;;
        *) echo "unknown arg: $a"; exit 2 ;;
    esac
done

die() { echo "FAIL: $*"; exit "${2:-1}"; }

# --- 1. node 探测（非交互 ssh 的 PATH 里没有 node） ---
if [ "$NOBUILD" = 1 ]; then
    NODE=""
else
    NODE=$(command -v node 2>/dev/null || true)
    if [ -z "$NODE" ] && [ -x /home/links-dev/node/bin/node ]; then
        NODE=/home/links-dev/node/bin/node
    fi
    [ -n "$NODE" ] || die "找不到 node（PATH 与 /home/links-dev/node/bin/node 都没有）；ssh 里请先 export PATH=/home/links-dev/node/bin:\$PATH" 13
fi

# --- 2. 互斥：同一时刻只允许一个重建 ---
exec 9>"$LOCK" || die "无法创建锁文件 $LOCK" 2
flock -n 9 || die "另一个重建正在跑（$LOCK 被占），本次不启动；交叉跑会产出脏 sha" 12

[ -f "$PATCH" ] || die "patch 不存在: $PATCH" 2
[ -f "$BUILD" ] || die "构建入口不存在: $BUILD" 2

got=$(sha256sum "$PATCH" | cut -d' ' -f1)
[ "$got" = "$PATCH_SHA" ] || die "patch sha 不符: $got" 3

cur=$(sha256sum "$SRC" | cut -d' ' -f1)
if [ "$cur" = "$PATCHED_SHA" ]; then
    echo "NOTE: 工作区已是打补丁后的版本，先逆回"
    patch -R -p1 < "$PATCH" || die "逆回失败" 4
    cur=$(sha256sum "$SRC" | cut -d' ' -f1)
fi
[ "$cur" = "$BASE_SHA" ] || die "基线 sha 不符: $cur（期望 $BASE_SHA；禁止用 git checkout 修这个文件）" 5

# 上一次失败留下的 .rej / .orig 会污染 patch 判断
leftover=$(ls "$SRC".rej "$SRC".orig 2>/dev/null || true)
[ -z "$leftover" ] || die "有上次残留：$leftover —— 先删掉再跑（.rej 是交叉跑产物）" 14

echo "BASE OK  sha=$cur lines=$(wc -l < "$SRC")"

# 留一份基线副本，用它量「本 patch 实际改了哪几行」。
# 不能用 git diff：工作区基线 1c1bc80b 与 HEAD 本来就差 28 行，
# 那样报出来的是累计差（29 insertions），会把这个 11 行补丁说成 29 行。
TMPD=$(mktemp -d)
PATCHED=0
cleanup() {
    rc=$?
    if [ "$rc" -ne 0 ] && [ "$PATCHED" = 1 ] && [ "$KEEP" = 0 ]; then
        echo "CLEANUP: 失败退出，自动逆回（保底，别让工作区停在补丁后）"
        patch -R -p1 < "$PATCH" >/dev/null 2>&1 || echo "CLEANUP_FAIL: 请手工 git apply -R -p1 < $PATCH"
        sha256sum "$SRC"
    fi
    rm -rf "$TMPD" 2>/dev/null || true
    exit $rc
}
trap cleanup EXIT
cp "$SRC" "$TMPD/base.cs"

patch -p1 < "$PATCH" || die "打补丁失败" 6
PATCHED=1
[ ! -f "$SRC.rej" ] || die "产生了 .rej，补丁没干净贴上" 15

now=$(sha256sum "$SRC" | cut -d' ' -f1)
[ "$now" = "$PATCHED_SHA" ] || die "打补丁后 sha 不符: $now" 7
lines=$(wc -l < "$SRC")
[ "$lines" = "822" ] || die "打补丁后行数不符: $lines" 8
adds=$(diff -u "$TMPD/base.cs" "$SRC" | grep -c '^+[^+]')
dels=$(diff -u "$TMPD/base.cs" "$SRC" | grep -c '^-[^-]')
[ "$adds" = "1" ] && [ "$dels" = "1" ] || die "改动行数不是 1/1: +$adds/-$dels（契约 C7：只许动一行）" 11
echo "PATCH OK sha=$now lines=$lines"
echo "DIFF +$adds/-$dels 行（期望 +1/-1，只动 :275）"

if [ "$NOBUILD" = 1 ]; then
    echo "SKIP_BUILD (--no-build)"
else
    # --- 5. 清空中间产物（脏 bin/obj 会产出不同 sha；实测清了也稳定出同一 sha） ---
    if [ "$NOCLEAN" = 0 ]; then
        echo "CLEAN: 清空 milcore{,-bridge}/{bin,obj}"
        rm -rf 40-build/milcore/bin 40-build/milcore/obj \
               40-build/milcore-bridge/bin 40-build/milcore-bridge/obj
    else
        echo "SKIP_CLEAN (--no-clean)"
    fi
    echo "NODE $NODE"
    echo "BUILD: $NODE $BUILD"
    "$NODE" "$BUILD" || die "构建失败" 9

    # --- 3. 固定产物路径（不是 find）；优先用 build-bridge 自己写的 report ---
    so=""
    if [ -f "$ARTIFACT" ]; then
        so=$(sha256sum "$ARTIFACT" | cut -d' ' -f1)
    fi
    rep=""
    if [ -f "$REPORT" ]; then
        rep=$(grep -o '"sha256": *"[0-9a-f]\{64\}"' "$REPORT" | head -1 | grep -o '[0-9a-f]\{64\}')
    fi
    echo "ARTIFACT $ARTIFACT"
    echo "ARTIFACT_SHA ${so:-MISSING}"
    echo "REPORT_SHA   ${rep:-MISSING}"
    if [ -n "$so" ] && [ -n "$rep" ] && [ "$so" != "$rep" ]; then
        die "产物 sha 与 bridge-report.json 不一致（$so vs $rep）" 16
    fi
    final=${so:-$rep}
    [ -n "$final" ] || die "拿不到产物 sha（$ARTIFACT 与 $REPORT 都读不到）" 17
    if [ "$final" = "$EXPECT_SO" ]; then
        echo "BUILD_OK EXPECT_SHA_MATCH $EXPECT_SO"
    else
        echo "BUILD_DIFF expect $EXPECT_SO got $final"
        exit 18
    fi
fi

if [ "$KEEP" = 1 ]; then
    echo "KEEP_PATCHED（工作区现在是补丁后状态，记得 git apply -R 逆回）"
    PATCHED=0
else
    patch -R -p1 < "$PATCH" || die "逆回失败（工作区现在是补丁后状态，请手工 git apply -R）" 10
    PATCHED=0
    back=$(sha256sum "$SRC" | cut -d' ' -f1)
    [ "$back" = "$BASE_SHA" ] && echo "REVERT OK sha=$back" || die "逆回后 sha 不符: $back" 19
fi
