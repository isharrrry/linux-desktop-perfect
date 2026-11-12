#!/usr/bin/env bash
# 门禁集合：任何 Linux + node20 + gcc 环境可复跑（不依赖本机运行时/显示器/ImageMagick）。
#
# 用法: bash 50-verify/gates.sh
# 退出码: 0 全通过；1 有失败（打印失败项原文）。
#
# 覆盖：4 条文档门禁（design-ids 由 selftest 覆盖，单独跑会缺 `_selftest_ids.md`）+ 2 个 C ABI 断言。
# 不覆盖：50-verify/deliver-check.mjs（它依赖本机安装的运行时目录、临时示例应用包、
#         虚拟显示 :303 与截图工具，属本机专项验收，不能进通用 CI）。
set -euo pipefail

LOG_DIR=${TMPDIR:-/tmp}/ldp-gates
mkdir -p "$LOG_DIR"

check() {
    local name=$1; shift
    if "$@" > "$LOG_DIR/$name.log" 2>&1; then
        echo "PASS $name"
    else
        echo "FAIL $name"
        echo "---- $LOG_DIR/$name.log ----"
        cat "$LOG_DIR/$name.log"
        exit 1
    fi
}

check links    node 30-design/checks/check-links.mjs
check package  node 30-design/checks/check-package.mjs
check coverage node 30-design/checks/check-coverage.mjs
check selftest node 30-design/checks/selftest.mjs

check abi-layout cc -fsyntax-only -std=gnu11 -Wall -Wextra -Wno-unused-parameter \
    -I40-build/native-shim/src 40-build/native-shim/tests/abi_layout.c
check text-abi cc -fsyntax-only -std=gnu11 -Wall -Wextra -Wno-unused-parameter \
    -I40-build/text-stack/src 40-build/text-stack/tests/text_abi.c

echo "ALL GATES PASS"
