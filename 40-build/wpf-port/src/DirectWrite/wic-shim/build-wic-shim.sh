#!/bin/sh
# 构建 WIC → Skia shim（T2）。用法：build-wic-shim.sh [输出目录]
# 产物：libwpfwic.so（dlopen 自定位 libSkiaSharp.so；不需要链接期依赖）
set -e
OUT="${1:-$(dirname "$0")}"
SRC="$(dirname "$0")/wic_proxy.c"
gcc -O2 -fPIC -shared -Wall -Wextra -Wno-unused-parameter \
    -o "$OUT/libwpfwic.so" "$SRC" -ldl -lz   # -lz：PNG iTXt 的 zlib 解压（uncompress）
echo "[OK] $OUT/libwpfwic.so"

# ---- 构建后自动校验 app-local 副本（债务 #20：让"副本落后一代"不可能悄悄发生）----
# 只**报**不改：不一致时打醒目告警并给出 EXPECT/ACTUAL 两个 sha；同步由发布脚本负责。
if [ -x "$(dirname "$0")/check-applocal-sync.sh" ]; then
    echo "==> app-local 一致性校验（只报不改）"
    if ! "$(dirname "$0")/check-applocal-sync.sh"; then
        echo "!!! 警告：存在与权威件不一致的 app-local 副本（上面标 MISMATCH/RETIRED/NO-AUTHORITY 的行）"
        echo "!!! 跑探针前请先同步，否则会测到旧件（历史事故：假红/假绿各一次）"
    fi
fi
