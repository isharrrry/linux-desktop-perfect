#!/usr/bin/env bash
# WPF-on-Linux · M7b · libwpfwin32.so 的构建入口
#
#   ./build-shim.sh              构建 bin/libwpfwin32.so
#   ./build-shim.sh --abi        构建 + 跑 ABI 布局自检并打印逐字段 offset
#   ./build-shim.sh --symbols    构建 + 打印导出符号数与清单
#   ./build-shim.sh --all        三件事都做（报告证据用）
#   ./build-shim.sh --clean      清理 obj/ bin/
#
# 【为什么默认直接调 gcc 而不是 make】
#   本机（Ubuntu 22.04 最小镜像）**没有装 make**。Makefile 保留给有 make 的
#   环境（`make all` 与这里的编译命令逐字等价），但构建入口必须零额外依赖——
#   否则「跑起来」这件事就被一个 40KB 的构建工具卡住了。
#   两者产物完全一致：同样的 -std/-O2/-Wall/-fPIC/-shared/--no-undefined。
#
# 【依赖】
#   gcc（或 cc）+ libc6-dev + libx11-dev。缺 libX11 头文件时给出明确的 apt 提示，
#   而不是让 gcc 吐一句 "X11/Xlib.h: No such file or directory"。

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"

CC="${CC:-gcc}"
CSTD="${CSTD:--std=gnu11}"
OPT="${OPT:--O2}"
# -Wno-cast-function-type：fast-sym 表里把不同签名的导出函数指针统一存成 void*，
#   这是 dlsym 风格符号表的固有做法，不是 bug。
WARN="-Wall -Wextra -Wno-unused-parameter -Wno-cast-function-type"
CFLAGS_BASE="$CSTD $OPT $WARN -fPIC -fvisibility=default"
LDLIBS="-lX11 -ldl -lpthread"

SRCS=(src/win32_core.c src/win32_msg.c src/win32_x11.c src/win32_misc.c src/win32_exports.c src/win32_unicode_tables.c src/win32_classification.c src/win32_oem.c src/win32_gdiplus.c src/win32_pts.c)
OBJS=()
TARGET="bin/libwpfwin32.so"

die() { echo "构建失败：$*" >&2; exit 1; }

preflight() {
    command -v "$CC" >/dev/null 2>&1 || die "找不到 C 编译器 '$CC'（apt install gcc）"
    [ -f /usr/include/X11/Xlib.h ] || die \
        "缺少 X11 开发头文件 /usr/include/X11/Xlib.h（Debian/Ubuntu: apt install libx11-dev；RHEL/Fedora: dnf install libX11-devel）"
}

build() {
    preflight
    mkdir -p obj bin
    echo "== 编译（$CC $CSTD $OPT）"
    for src in "${SRCS[@]}"; do
        obj="obj/$(basename "${src%.c}").o"
        echo "   CC  $src"
        "$CC" $CFLAGS_BASE -Isrc -c "$src" -o "$obj" || die "编译 $src"
        OBJS+=("$obj")
    done
    echo "== 链接 -shared -Wl,--no-undefined"
    "$CC" -shared -Wl,--no-undefined -Wl,-soname,libwpfwin32.so \
          -o "$TARGET" "${OBJS[@]}" $LDLIBS || die "链接"
    printf '== 产物：%s（%s 字节）\n' "$TARGET" "$(stat -c%s "$TARGET")"
}

abi_check() {
    mkdir -p bin
    "$CC" "$CSTD" "$OPT" $WARN -Isrc -o bin/abi-layout tests/abi_layout.c $LDLIBS \
        || die "编译 ABI 自检"
    echo
    ./bin/abi-layout || die "ABI 布局自检不通过"
}

symbols() {
    [ -f "$TARGET" ] || build
    local n
    n="$(nm -D --defined-only "$TARGET" | wc -l)"
    nm -D --defined-only "$TARGET" | awk '{print $3}' | sort > bin/exports.txt
    echo
    echo "== 导出符号总数：$n"
    echo "== 清单：bin/exports.txt"
    echo "-- 按前缀分组 --"
    awk '{ if ($0 ~ /^WpfLinuxWin32_/) a++; else if ($0 ~ /Wrapper$/) c++; else b++ }
         END { printf "   Win32 API 名（含 A/W 变体）: %d\n   托管侧 PresentationNative *Wrapper: %d\n   M7c 桥接（WpfLinuxWin32_*）: %d\n", b, c, a }' bin/exports.txt
}

case "${1:-build}" in
    build|"")   build ;;
    --abi)      build; abi_check ;;
    --symbols)  build; symbols ;;
    --all)      build; abi_check; symbols ;;
    --clean)    rm -rf obj bin; echo "已清理 obj/ bin/" ;;
    *) echo "用法: $0 [--abi|--symbols|--all|--clean]" >&2; exit 2 ;;
esac
