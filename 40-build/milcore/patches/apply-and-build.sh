#!/bin/bash
# PM-0112 层 alpha 本地化 · 从仓库一键重建
# 用法: bash 40-build/milcore/patches/apply-and-build.sh [--keep] [--no-build]
#       --keep      构建后不自动逆回（默认逆回，工作区回到 1c1bc80b 基线）
#       --no-build  只做基线校验 + 打补丁 + sha 校验 + 逆回，不调构建（自测用）
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO" || exit 2

PATCH=40-build/milcore/patches/PM-0112-local-layer-alpha.patch
SRC=40-build/milcore/Rendering/SkiaRenderBackend.cs
BUILD=40-build/milcore-bridge/build-bridge.mjs
BASE_SHA=1c1bc80b7baf791edc7801228f1cf2ca5866f6c1c2e6d50230648987313c310f
PATCHED_SHA=3681da3f088f417246985c6d51a99954469bb784c1257127efb46c7685ce0240
EXPECT_SO=e453e71495e4ca94bc9b428bcb7ba4666e4e7a3b5c2a20ae44c4cdd0460c1ae8
PATCH_SHA=4f626b12cf7be020100591b26c313dd3588a1f95dc63a884d2d1cca903097762

KEEP=0
NOBUILD=0
for a in "$@"; do
    case "$a" in
        --keep) KEEP=1 ;;
        --no-build) NOBUILD=1 ;;
        *) echo "unknown arg: $a"; exit 2 ;;
    esac
done

die() { echo "FAIL: $*"; exit "${2:-1}"; }

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
echo "BASE OK  sha=$cur lines=$(wc -l < "$SRC")"
# 留一份基线副本，用它量「本 patch 实际改了哪几行」。
# 不能用 git diff：工作区基线 1c1bc80b 与 HEAD ec11937e 本就差 28 行，
# 那样报出来的是累计差（29 insertions），会把这个 11 行补丁说成 29 行。
TMPD=$(mktemp -d)
trap 'rm -rf "$TMPD"' EXIT
cp "$SRC" "$TMPD/base.cs"

patch -p1 < "$PATCH" || die "打补丁失败" 6
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
    echo "BUILD: node $BUILD"
    node "$BUILD" || die "构建失败" 9
    out=$(find 40-build -name 'wpfgfx_cor3.so' -newermt '-40 minutes' -print -quit 2>/dev/null)
    if [ -n "$out" ]; then
        so=$(sha256sum "$out" | cut -d' ' -f1)
        echo "ARTIFACT $out"
        echo "ARTIFACT_SHA $so"
        if [ "$so" = "$EXPECT_SO" ]; then
            echo "BUILD_OK EXPECT_SHA_MATCH $EXPECT_SO"
        else
            echo "BUILD_DIFF expect $EXPECT_SO got $so"
        fi
    else
        echo "ARTIFACT_NOT_FOUND（产物路径可能已变，按 build 入口输出核对）"
    fi
fi

if [ "$KEEP" = 1 ]; then
    echo "KEEP_PATCHED（工作区现在是补丁后状态，记得 git apply -R 逆回）"
else
    patch -R -p1 < "$PATCH" || die "逆回失败（工作区现在是补丁后状态，请手工 git apply -R）" 10
    back=$(sha256sum "$SRC" | cut -d' ' -f1)
    [ "$back" = "$BASE_SHA" ] && echo "REVERT OK sha=$back" || echo "REVERT_MISMATCH $back"
fi
