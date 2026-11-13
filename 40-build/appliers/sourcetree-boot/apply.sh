#!/bin/sh
# applier：SourceTree 在 Linux 上的启动补丁（K-1497 / K-1500 / K-1502）。
# 用法:
#   sh apply.sh <appDir> [--check] [--runtime <runtimeDir>]
#   --check        只报告（输出 xxx: ALREADY|NEEDS），不写盘
#   --runtime      同时对运行时目录（含 PresentationCore.dll）打补丁；不给则跳过 runtime 两项
set -eu
APP_DIR=${1:?用法: sh apply.sh <appDir> [--check] [--runtime <runtimeDir>]}
shift
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "${DOTNET:-dotnet}" run --project "$HERE" -- "$APP_DIR" "$@"
