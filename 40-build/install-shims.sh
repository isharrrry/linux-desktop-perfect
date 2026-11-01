#!/bin/sh
# ── 把本仓自建的 .so 部署到 Microsoft.WindowsDesktop.App 的框架目录 ─────────────
#
# 【为什么需要这个脚本】
#   本仓有三个**自建**的原生库，运行时都必须能找到：
#     libwpfwin32.so                       Win32 兼容层（shim）
#     libwpfwic.so                         WIC（WindowsCodecs）shim
#     libSystem.IO.Compression.Native.so   zlib 适配层
#   **都放在 `$FX` 根目录**（`$FX` = .../shared/Microsoft.WindowsDesktop.App/<ver>）。
#   之前靠手工 `cp`，结果一次 `git checkout` 回滚 + 清理时把 `libwpfwic.so` 删了，
#   导致 WIC 链路整个不可用（K-231）。**这个脚本就是消除那种手工失误。**
#
# 【用法】
#   sh 40-build/install-shims.sh            # 安装（默认从 DOTNET_ROOT 推断 $FX）
#   FX=/path/to/fx sh 40-build/install-shims.sh
#   sh 40-build/install-shims.sh --check    # 只检查，不写
#
# 【退出码】0 = 三个库都到位；1 = 缺库或找不到 $FX
set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.local/share/ldp/dotnet}

if [ -z "$FX" ]; then
    # 取版本号最大的那个（兼容 10.0.x 这种）
    FX=$(ls -d "$DOTNET_ROOT"/shared/Microsoft.WindowsDesktop.App/*/ 2>/dev/null | sort -V | tail -1)
    FX=${FX%/}
fi

if [ ! -d "$FX" ]; then
    echo "找不到框架目录：$FX" >&2
    echo "用法：FX=/path/to/Microsoft.WindowsDesktop.App/<ver> sh $0" >&2
    exit 1
fi

CHECK=0
[ "$1" = "--check" ] && CHECK=1

# 源文件 → 目标名
set -- \
    "native-shim/bin/libwpfwin32.so:libwpfwin32.so" \
    "wic-dwrite/bin/libwpfwic.so:libwpfwic.so" \
    "zlib-native/bin/libSystem.IO.Compression.Native.so:libSystem.IO.Compression.Native.so"

fail=0
for pair in "$@"; do
    src="$ROOT/40-build/${pair%%:*}"
    name="${pair##*:}"
    if [ ! -f "$src" ]; then
        echo "缺构建产物：$src（先 cd $(dirname "$src") && make）" >&2
        fail=1
        continue
    fi
    if [ "$CHECK" = "1" ]; then
        if [ -f "$FX/$name" ]; then
            echo "OK   $name  ($(wc -c < "$FX/$name") 字节)"
        else
            echo "缺   $name  ← 应来自 $src" >&2
            fail=1
        fi
    else
        cp -f "$src" "$FX/$name"
        echo "装入 $name  ($(wc -c < "$FX/$name") 字节)"
    fi
done

exit $fail
